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

    private string? _lastKey;
    private EditableSandbox? _lastBoard;
    private ScreenText? _lastText, _textBeforeRed;
    private int? _lastDays, _consumedDay;
    private (int Warden, int? Days, EditableSandbox Board, ScreenText? Text)? _huntOffer;
    private Hint? _hint;
    private string _status = "准备中…";

    public event Action<Shown>? Changed;

    public Pipeline()
    {
        var cells = new CnnCellReader(panels: new SlotReader());
        var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
        _scanner = new LayoutScanner(new OcrEngine(threads: 2, gameLanguage: "zh"), cells, presence);
        // 手机比电脑慢：每步最多算 0.8 秒
        _hints = new HintService(playouts: 512, seconds: 0.8);
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
                _lastKey = null;
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
        board.Days ??= _lastDays;
        if (board.Days is int d) _lastDays = d;

        // 同一个局面连续两次才算数
        string key = SandboxText.Describe(board) + "|" + string.Join(",", r.Text?.Cards.Select(c => c.Meaning) ?? []) + "|" + r.Text?.ConsumePick + r.Text?.RatwayPick;
        if (key != _lastKey)
        {
            _lastKey = key;
            SetStatus($"识别中… {LastScanMs:0} 毫秒");
            return;
        }
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

        var hb = huntPick ? _huntOffer!.Value.Board.Clone() : board;
        var text = consumeOnly ? _textBeforeRed ?? r.Text : huntPick ? _huntOffer!.Value.Text : r.Text;
        var observation = GameObservation.From(hb, text, 0);
        observation.ConsumedToday = _consumedDay != null && _consumedDay == hb.Days;
        observation.ChainGoesOn = observation.ConsumedToday == true && consumeOnly;
        if (_hints.Request(observation, consumeOnly, huntPick)) lock (_gate) _hint = null;
        SetStatus(_hint == null ? "计算中…" : $"已识别 · {LastScanMs:0} 毫秒");
    }

    private void Pick(PickRead pick, ScanResult r)
    {
        string key = "pick|" + pick.Kind + "|" + string.Join(",", pick.Cards.Select(c => c.Id ?? c.Text));
        if (key != _lastKey)
        {
            _lastKey = key;
            SetStatus($"识别中… {LastScanMs:0} 毫秒");
            return;
        }
        var under = _lastBoard;
        if (pick.Kind == PickRead.Instrument && (under == null || under.Days == 0)) under = EditableSandbox.Empty();
        var observation = GameObservation.FromPick(pick, under ?? EditableSandbox.Empty(), _lastText);
        if (_hints.Request(observation, consumeOnly: false)) lock (_gate) _hint = null;
        SetStatus(_hint == null ? "计算中…" : $"已识别 · {LastScanMs:0} 毫秒");
    }

    private void Clear(string status)
    {
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
        if (hint is not { Ranked: { Count: > 0 } ranked }) return new Shown(status.StartsWith("计算") ? "计算中…" : "暂无", status, null, false, status);
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
