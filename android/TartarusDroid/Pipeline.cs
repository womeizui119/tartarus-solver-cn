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

    private string? _lastKey, _requestedKey, _kind;
    private DateTime _lastTrace;
    private EditableSandbox? _sticky;
    private EditableSandbox? _lastBoard;
    /// <summary>整局的记忆（和电脑版同一套）：刑具栏、绮彩数、天数。一帧认不出就沿用之前认出的；天数变多算新的一局。
    /// 2026-11-06 两台实机整局 190 次识别刑具栏全是 ?/hidden，推算不知道手上有什么刑具，推荐就偏。</summary>
    private readonly RunMemory _run = new();
    private ScreenText? _lastText, _textBeforeRed;
    private int? _lastDays, _consumeLeftDay;
    private bool _inConsumePick, _consumedToday;
    private (int Warden, int? Days, EditableSandbox Board, ScreenText? Text)? _huntOffer;
    private Hint? _hint;
    /// <summary>最近几次读到的格子：每格按多数算（2026-11-06 小米实机：14 号一帧 Magic 一帧 Normal，局面一闪就重算，推荐跟着跳）。</summary>
    private readonly List<CellLabel[]> _cellHist = [];
    /// <summary>这一局每个目标读到过的最大进度：进度只会涨（“1/4”偶尔读成“0/4”，推荐就从“煽动”变成“结束”）。</summary>
    private readonly Dictionary<Tartarus.Rules.ObjectiveKind, int> _progress = [];
    private int _noDecision;
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
        _hints.Log += s => Trace?.Invoke("推算：" + s);
        _hints.Failed += ex =>
        {
            Trace?.Invoke("推算出错：" + ex);
            lock (_gate) _hint = null;
            _requestedKey = null;
            SetStatus("推算出错：" + ex.GetType().Name + " " + ex.Message);
        };
    }

    /// <summary>排查用的一行行记录（写进 记录-….txt）。</summary>
    public event Action<string>? Trace;

    public ScanResult? LastScan { get; private set; }
    /// <summary>当前有推荐在显示（画面静止时不用重建录屏）。</summary>
    public bool HasHint { get { lock (_gate) return _hint is { Ranked.Count: > 0 }; } }
    public double LastScanMs { get; private set; }

    public void Process(Frame frame)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = _scanner.Scan(frame);
        LastScanMs = sw.Elapsed.TotalMilliseconds;
        LastScan = r;
        if (DateTime.Now - _lastTrace > TimeSpan.FromSeconds(1))
        {
            _lastTrace = DateTime.Now;
            Trace?.Invoke($"帧 {frame.Width}×{frame.Height} · 识别 {LastScanMs:0} 毫秒 · {(r.Ok ? "棋盘" : r.Pick != null ? "选择:" + r.Pick.Kind : "无:" + r.Reason)}"
                + (r.Ok ? " · " + SandboxText.Describe(EditableSandbox.FromScan(r)) : "") + $" · 状态「{_status}」");
        }
        try
        {
            // 画面种类一变（选刑具 ↔ 棋盘 ↔ 没棋盘），上一种画面的推荐和稳定性记录立刻作废
            // （2026-11-05 实机：选完刑具进棋盘后，“重新随机”挂了整整一局）
            string kind = r.Ok ? "board" : r.Pick != null ? "pick" : "none";
            if (kind != _kind)
            {
                _kind = kind;
                _recent.Clear();
                _changedReads = 0;
                if (_requestedKey != null) Clear("识别中…");
                if (kind != "board") _sticky = null;
            }
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
        // 这一帧被挡住、看不清的格子，沿用上次读到的；守卫标记（会闪）一时没认出也沿用上次的位置
        // （2026-11-05 实机录屏：15 号格一帧“被挡住”一帧“未开”，守卫位置时有时无，局面一直“变”，红字就不刷新）
        if (_sticky != null)
        {
            for (int n = 2; n <= 16; n++)
                if (board[n].Covered && !_sticky[n].Covered) board.Cells[n - 1] = _sticky[n].Clone();
            if (board.Warden <= 1 && _sticky.Warden > 1) board.Warden = _sticky.Warden;
        }
        _sticky = board.Clone();
        Vote(board);
        var live = _live?.Invoke();
        if (live != null) board.Days = live.RemainingDays;  // 日志里的天数比画面上认的准
        if (board.Days is int nd && _lastDays is int od && nd > od) _progress.Clear();  // 剩余天数变多：新的一局
        _run.Days(board.Days);
        board.Days ??= _lastDays;
        if (board.Days is int d) _lastDays = d;
        // 刑具栏和绮彩数：用整局记住的，不只看这一帧（被挡住、动画、没认出都沿用之前的）
        board.Instruments = [.. _run.Instruments(r.Instruments?.Instruments.Select(x => x.Id).ToList() ?? [])];
        board.MarksByInstruments();
        board.Chromatic = _run.ChromaticCount(board.Chromatic, r.Chromatic?.Covered == true, DateTimeOffset.Now);

        // 同一个局面读到两次才算数（最近 4 次里有 2 次即可，不必连着：卡牌发光动画会让个别几次读得不一样）
        // 判断“是不是同一个局面”时不看绮彩计数和刑具栏（容易读得忽有忽无，变化时局面本身也会变）
        string key = System.Text.RegularExpressions.Regex.Replace(SandboxText.Describe(board), @" · Torture Instruments \[[^\]]*\] · Chromatic \d+", "") + "|" + CardsKey(r.Text) + "|" + r.Text?.ConsumePick + r.Text?.RatwayPick;
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
            // 卡牌字一帧没读出来不算：连着两次都没有选择才撤下提示（审讯室一闪一闪的根源之一）
            if (++_noDecision < 3 && _hint != null) return;
            Clear("等待选择画面…");
            return;
        }
        _noDecision = 0;
        // 今天吃过没有：第一次进“选目标”画面时还没吃；选完目标回到棋盘再出现“选目标”才是连吃
        // （2026-11-06 实机：一进选目标就记成“今天吃过”，推算当成连吃，常常建议“先取消再吃”）
        if (consumeOnly)
        {
            if (!_inConsumePick)
            {
                // 刚进选目标画面：上一次离开选目标画面时，如果是同一天，说明今天已经吃过一次了
                _consumedToday = _consumeLeftDay != null && _consumeLeftDay == board.Days;
                _inConsumePick = true;
            }
        }
        else
        {
            if (_inConsumePick)
            {
                // 离开了选目标画面（选完了）：记下这一天
                _inConsumePick = false;
                _consumeLeftDay = _lastDays ?? board.Days;
            }
            if (!huntPick) _textBeforeRed = r.Text;
        }

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
        if (observation.Objective is { } obj)
        {
            int best = Math.Max(observation.ObjectiveProgress, _progress.GetValueOrDefault(obj));
            _progress[obj] = best;
            observation.ObjectiveProgress = best;
        }
        // 审讯室：卡读出一张就够了（两张一定同时在）
        if (text?.Cards.Any(c => c.Meaning is "instigate" or "bestow") == true) observation.Interrogation = true;
        observation.ConsumedToday = live != null ? live.ConsumedToday
            : consumeOnly ? _consumedToday                                   // 选目标画面上：进来时判断好的
            : _consumeLeftDay != null && _consumeLeftDay == hb.Days;          // 棋盘上：今天离开过选目标画面 = 今天吃过
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
        // 审讯室 / 典狱长珍藏：在当前局面上算。只有这一局还没读到过棋盘（开局第一次选刑具）才用空棋盘
        // （2026-11-06 实机：中途进审讯室时天数一帧没认出（=0）就被换成了第 1 天的空棋盘，推荐全错）
        var under = _lastBoard?.Clone();
        if (under == null) under = EditableSandbox.Empty();
        else
        {
            under.Days ??= _lastDays;
            under.Instruments = [.. _run.Instruments([])];
            under.MarksByInstruments();
        }
        Trace?.Invoke($"选择画面 {pick.Kind}：在 {(ReferenceEquals(under, _lastBoard) ? "空" : "当前")}局面上算 · 第 {under.Days?.ToString() ?? "?"} 天 · 刑具 [{string.Join(",", under.Instruments)}]");
        var observation = GameObservation.FromPick(pick, under, _lastText);
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
        // 和正在推算的局面“差不多”（最多差 1 项）就当同一个：实时录屏每帧都有闪动的东西，逐字相同凑不齐
        // （2026-11-06 实机：一直停在“识别中”，我这边用同一张图测永远复现不了）
        if (_requestedKey != null && Close(key, _requestedKey)) { _changedReads = 0; return true; }
        if (_requestedKey != null && ++_changedReads >= 2)
        {
            _changedReads = 0;
            _requestedKey = null;
            _hints.Clear();
            lock (_gate) _hint = null;
        }
        int alike = _recent.Count(k => Close(k, key));
        if (alike >= 2) return true;
        Trace?.Invoke($"识别中：最近 {_recent.Count} 次里相近的 {alike} 次 · {key}");
        SetStatus($"识别中… {LastScanMs:0} 毫秒");
        return false;
    }

    /// <summary>转屏、重新开始后：忘掉之前读到的一切，从头识别。</summary>
    public void Reset()
    {
        _run.NewRun();
        _lastBoard = null;
        _lastDays = null;
        _consumeLeftDay = null;
        _inConsumePick = _consumedToday = false;
        _kind = null;
        _sticky = null;
        _cellHist.Clear();
        _progress.Clear();
        _noDecision = 0;
        _recent.Clear();
        _changedReads = 0;
        _lastKey = null;
        Clear("识别中…");
    }

    /// <summary>卡牌部分的“局面键”：审讯室只要读到煽动或赏赐之一就当两张都在；别的卡按读到的意思，多读出的杂字（“送”）去掉。</summary>
    private static string CardsKey(ScreenText? text)
    {
        var m = text?.Cards.Select(c => c.Meaning).ToList() ?? [];
        if (m.Any(x => x is "instigate" or "bestow")) return "instigate,bestow";
        return string.Join(",", m.Where(x => x.Length > 1 || x is ScreenText.UnreadCard));
    }

    /// <summary>每格取最近 4 次里读得最多的样子；一样多时用最新的（真的变了，两次就跟上）。</summary>
    private void Vote(EditableSandbox board)
    {
        _cellHist.Add(board.Cells.Select(c => c.Clone()).ToArray());
        if (_cellHist.Count > 4) _cellHist.RemoveAt(0);
        if (_cellHist.Count < 2) return;
        for (int i = 1; i < 16; i++)
        {
            if (board.Cells[i].Covered) continue;
            string now = SandboxText.CellShort(board.Cells[i]);
            var groups = _cellHist.Select(h => h[i]).Where(c => !c.Covered).GroupBy(SandboxText.CellShort).ToList();
            var top = groups.OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key == now).First();
            if (top.Key != now) board.Cells[i] = top.Last().Clone();
        }
    }

    /// <summary>两次读数是不是同一个局面：按“|”和“·”拆成项，最多允许 1 项不同。</summary>
    private static bool Close(string a, string b)
    {
        if (a == b) return true;
        var xa = a.Split('|', '·'); var xb = b.Split('|', '·');
        if (xa.Length != xb.Length) return false;
        int diff = 0;
        for (int i = 0; i < xa.Length; i++) if (xa[i].Trim() != xb[i].Trim() && ++diff > 1) return false;
        return true;
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
            return new Shown(status.StartsWith("计算") ? "计算中…" : status.StartsWith("识别") ? "识别中…" : status.StartsWith("推算出错") ? "推算出错" : "暂无", status, null, false, status);
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
