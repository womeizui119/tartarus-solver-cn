using Tartarus.App;
using Tartarus.Rules;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace TartarusDroid;

/// <summary>浮窗上显示的内容。</summary>
public sealed record Shown(string Main, string Detail, string? Extra, bool HasHint, string Status);

/// <summary>
/// 一帧画面 → 识别 → 推算 → 浮窗文字。电脑版靠游戏日志判断时机，手机读不到日志，所以这里做了简化：
/// 只在画面明确在问的时候给提示（选目的地、做决策、选吃的目标、选刑具/珍藏），
/// 并且同一个局面连续读到两次才推算，避免动画中途读错。
/// </summary>
public sealed class Pipeline : IDisposable
{
    private readonly ISandboxScanner _scanner;
    private readonly HintService _hints;
    private readonly object _gate = new();

    private string? _lastKey, _requestedKey;
    private EditableSandbox? _sticky;
    private EditableSandbox? _lastBoard;
    private ScreenText? _lastText, _textBeforeRed;
    private int? _lastDays, _consumedDay;
    private (int Warden, int? Days, EditableSandbox Board, ScreenText? Text)? _huntOffer;
    private Hint? _hint;
    private string _status = "准备中…";

    public event Action<Shown>? Changed;

    private readonly Func<LoggedRun?>? _live;

    /// <param name="live">游戏日志里的当前这一局（读到日志时）：天数、今天吃过没有以它为准。</param>
    public Pipeline(Func<LoggedRun?>? live = null)
    {
        _live = live;
        var cells = new CnnCellReader(panels: new SlotReader());
        var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
        _scanner = new LayoutScanner(new OcrEngine(threads: Math.Clamp(Environment.ProcessorCount / 2, 2, 4), gameLanguage: "zh"), cells, presence);
        // 手机比电脑慢：每步最多算 0.6 秒（出得快；结果和多算一会儿基本一样）
        _hints = new HintService(playouts: 512, seconds: 0.6);
        _hints.Ready += h =>
        {
            lock (_gate) _hint = h;
            Publish();
        };
    }

    public ScanResult? LastScan { get; private set; }
    public double LastScanMs { get; private set; }

    public void Process(Frame frame)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = _scanner.Scan(frame);
        LastScanMs = sw.Elapsed.TotalMilliseconds;
        LastScan = r;
        try
        {
            if (r.Ok) Board(r);
            else if (r.Pick is { } pick) Pick(pick, r);
            else
            {
                _lastKey = null; _recent.Clear();
                Clear($"没看到塔罗斯监狱棋盘（{Zh.Reason(r.Reason)}）");
            }
        }
        catch (Exception ex)
        {
            Clear("出错：" + ex.Message);
        }
    }

    private void Board(ScanResult r)
    {
        var board = EditableSandbox.FromScan(r);
        // 这一帧被挡住、看不清的格子，沿用上次读到的；守卫标记（会闪）一时没认出也沿用上次的位置
        // （2026-11-05 实机录屏：15 号格一帧“被挡住”一帧“未开”，守卫位置时有时无，局面一直“变”，红字就不刷新）
        if (_sticky != null)
        {
            for (int n = 2; n <= 16; n++)
                if (board[n].Covered && !_sticky[n].Covered) board.Cells[n - 1] = _sticky[n].Clone();
            if (board.Warden <= 1 && _sticky.Warden > 1) board.Warden = _sticky.Warden;
        }
        _sticky = board.Clone();
        var live = _live?.Invoke();
        if (live != null) board.Days = live.RemainingDays;  // 日志里的天数比画面上认的准
        board.Days ??= _lastDays;
        if (board.Days is int d) _lastDays = d;

        // 同一个局面读到两次才算数（最近 4 次里有 2 次即可，不必连着：卡牌发光动画会让个别几次读得不一样）
        // 判断“是不是同一个局面”时不看绮彩计数和刑具栏（容易读得忽有忽无，变化时局面本身也会变）
        string key = System.Text.RegularExpressions.Regex.Replace(SandboxText.Describe(board), @" · Torture Instruments \[[^\]]*\] · Chromatic \d+", "") + "|" + string.Join(",", r.Text?.Cards.Select(c => c.Meaning) ?? []) + "|" + r.Text?.ConsumePick + r.Text?.RatwayPick;
        if (!Stable(key)) return;
        _lastBoard = board.Clone();
        _lastText = r.Text;

        bool destinations = board.Phase == Phases.Destination && board.Destinations.Count > 0;
        bool decision = r.Text?.Cards.Any(c => c.Meaning is var t
            && (t.Contains("consume") || t.Contains("replace") || t.Contains("hunt") || t.Contains("instigate") || t.Contains("bestow") || t == "end" || t.EndsWith(" end"))) == true;
        bool consumeOnly = r.Text?.ConsumePick == true;
        if (r.Text?.Cards.Any(c => c.Meaning.Contains("hunt")) == true) _huntOffer = (board.Warden, board.Days, board.Clone(), r.Text);
        bool huntPick = r.Text?.RatwayPick == true && _huntOffer is { } hunt && hunt.Warden == board.Warden && hunt.Days == board.Days;
        if (r.Text?.RatwayPick == true && !huntPick) decision = true;
        var here = board.Warden > 1 ? board[board.Warden] : null;
        bool endOnly = r.Text?.Cards is { Count: > 0 } cards && cards.All(c => c.Meaning is "end" || c.Meaning.EndsWith(" end"));
        if (decision && !consumeOnly && r.Text?.RatwayPick != true && here != null && here.Kind != CellKind.Inmate && !endOnly) decision = false;

        if (!consumeOnly && !destinations && !decision)
        {
            Clear("等待选择画面…");
            return;
        }
        if (consumeOnly) _consumedDay = board.Days;
        else if (!huntPick) _textBeforeRed = r.Text;

        // 同一个局面只推算一次：之后的帧里 OCR 的细小差别（比如读到了浮窗上的字）不会让推算一次次重来、永远算不完
        // （2026-11-05 实机：一直停在“计算中…”，切屏才恢复）
        if (key == _requestedKey)
        {
            SetStatus(_hint == null ? "计算中…" : $"已识别 · {LastScanMs:0} 毫秒");
            return;
        }
        _requestedKey = key;
        var hb = huntPick ? _huntOffer!.Value.Board.Clone() : board;
        var text = consumeOnly ? _textBeforeRed ?? r.Text : huntPick ? _huntOffer!.Value.Text : r.Text;
        var observation = GameObservation.From(hb, text, 0);
        observation.ConsumedToday = live != null ? live.ConsumedToday : _consumedDay != null && _consumedDay == hb.Days;
        observation.ChainGoesOn = observation.ConsumedToday == true && consumeOnly;
        if (_hints.Request(observation, consumeOnly, huntPick)) lock (_gate) _hint = null;
        SetStatus(_hint == null ? "计算中…" : $"已识别 · {LastScanMs:0} 毫秒");
    }

    private void Pick(PickRead pick, ScanResult r)
    {
        string key = "pick|" + pick.Kind + "|" + string.Join(",", pick.Cards.Select(c => c.Id ?? c.Text));
        if (!Stable(key)) return;
        if (key == _requestedKey)
        {
            SetStatus(_hint == null ? "计算中…" : $"已识别 · {LastScanMs:0} 毫秒");
            return;
        }
        _requestedKey = key;
        var under = _lastBoard;
        if (pick.Kind == PickRead.Instrument && (under == null || under.Days == 0)) under = EditableSandbox.Empty();
        var observation = GameObservation.FromPick(pick, under ?? EditableSandbox.Empty(), _lastText);
        if (_hints.Request(observation, consumeOnly: false)) lock (_gate) _hint = null;
        SetStatus(_hint == null ? "计算中…" : $"已识别 · {LastScanMs:0} 毫秒");
    }

    private readonly List<string> _recent = [];
    private int _changedReads;

    /// <summary>
    /// 这次读到的局面算不算稳定：最近 4 次里出现过 2 次。不稳定时，若已经和上次推算的局面不同了（走了一步），
    /// 连着两次不同就先把上一步的推荐撤下，改显示“识别中…”，免得一直挂着过时的红字
    /// （2026-11-05 实机：走一步后红字不刷新，要回主界面再进来才变）。
    /// </summary>
    private bool Stable(string key)
    {
        _recent.Add(key);
        if (_recent.Count > 4) _recent.RemoveAt(0);
        _lastKey = key;
        if (key == _requestedKey) { _changedReads = 0; return true; }
        if (_requestedKey != null && ++_changedReads >= 2)
        {
            _changedReads = 0;
            _requestedKey = null;
            _hints.Clear();
            lock (_gate) _hint = null;
        }
        if (_recent.Count(k => k == key) >= 2) return true;
        SetStatus($"识别中… {LastScanMs:0} 毫秒");
        return false;
    }

    private void Clear(string status)
    {
        _requestedKey = null;
        _hints.Clear();
        lock (_gate) _hint = null;
        SetStatus(status);
    }

    private void SetStatus(string s)
    {
        _status = s;
        Publish();
    }

    private void Publish()
    {
        Hint? hint;
        lock (_gate) hint = _hint;
        Changed?.Invoke(Describe(hint, _status));
    }

    /// <summary>推荐写成文字，和电脑版主窗口的“当前推荐”一样。</summary>
    private static Shown Describe(Hint? hint, string status)
    {
        if (hint is not { Ranked: { Count: > 0 } ranked })
            return new Shown(status.StartsWith("计算") ? "计算中…" : status.StartsWith("识别") ? "识别中…" : "暂无", status, null, false, status);
        var best = ranked[0];
        string main = Names.Ranked(hint, best);
        // 和电脑版一样只给收益最高的一步
        string detail = best.Action.Kind is ActionKind.Consume or ActionKind.Hunt && !hint.ConsumeOnly && !hint.HuntOnly
            ? "先点卡牌，再点目标牢房" : "收益最高的选择";
        string extra = $"预计本局：绮彩 ×{best.Chromatic:0.0} · 至臻 ×{best.Ultimate:0.0}";
        if (hint.State.MaxDays > 0) extra += $"\n按剩余 {hint.State.RemainingDays} 天计算";
        return new Shown(main, detail, extra, true, status);
    }

    public void Dispose()
    {
        _hints.Dispose();
        _scanner.Dispose();
    }
}
