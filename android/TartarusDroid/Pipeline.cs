using Tartarus.App;
using Tartarus.Rules;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace TartarusDroid;

/// <summary>浮窗上显示的内容。</summary>
public sealed record Shown(string Main, string Detail, string? Extra, bool HasHint, string Status)
{
    /// <summary>要画在游戏画面上的框（录屏画面坐标）和那一帧的大小；没有推荐时为空。</summary>
    public IReadOnlyList<HintBox> Boxes { get; init; } = [];
    /// <summary>推荐这一步后，这一局预计最后的绮彩、至臻数（和电脑版浮层上的 ×8.0 一样）。</summary>
    public double? Chromatic { get; init; }
    public double? Ultimate { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
}

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

    /// <summary>认定局面（见 <see cref="Settler"/>）；正在推算 / 已显示的是哪个局面；算过的局面的结果。</summary>
    private readonly Settler _settle = new();
    private string? _pendingKey;
    private readonly Dictionary<string, Hint> _cache = [];
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
    private string _status = "准备中…";

    public event Action<Shown>? Changed;

    private readonly Func<LoggedRun?>? _live;

    /// <param name="live">游戏日志里的当前这一局（读到日志时）：天数、今天吃过没有以它为准。</param>
    public Pipeline(Func<LoggedRun?>? live = null)
    {
        _live = live;
        // 不管发热，全速：识别和推算都用满所有核（2026-11-07 玩家要求速度拉到最快）
        int cores = Math.Max(2, Environment.ProcessorCount);
        _slots = new SlotReader(threads: 2);
        var cells = new CnnCellReader(panels: _slots, threads: Math.Max(2, cores / 2)) { ReuseUnchanged = true };
        var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
        _scanner = new LayoutScanner(new OcrEngine(threads: Math.Max(2, cores / 2), gameLanguage: "zh"), cells, presence) { PickFirst = true };
        // 和电脑版一样每个选项 1024 局模拟；手机慢，时间给到 1.5 秒才跑得满（2.4 起局面认定后不再反复重算，有这个时间）
        // （2026-11-06：手机原来 512 局、0.6 秒，玩家反映推荐不如电脑版、彩色箱子少）
        _hints = new HintService(playouts: int.TryParse(Environment.GetEnvironmentVariable("TARTARUS_PLAYOUTS"), out int po) ? po : 1024,
            seconds: double.TryParse(Environment.GetEnvironmentVariable("TARTARUS_SECONDS"), out double sec) ? sec : 1.0, threads: cores);
        _hints.Ready += h =>
        {
            lock (_gate)
            {
                // 只认当前认定局面的结果：算完时局面已经换了，就只存进缓存，不显示
                if (_pendingKey == null) return;
                if (_cache.Count > 60) _cache.Clear();
                if (IsCancel(h) && _held == null && !_cache.ContainsKey(_pendingKey))
                {
                    _held = h;
                    _heldUntil = DateTime.Now.AddSeconds(1.5);
                    Trace?.Invoke("推算完成（取消类，画面保持 1.5 秒再显示）");
                    return;
                }
                _cache[_pendingKey] = h;
                if (h.Ranked.Count > 0 && h.Ranked[0].Action.Kind == ActionKind.PickInstrument)
                    _pickHint = Tartarus.Rules.GameData.Info((Tartarus.Rules.Instrument)h.Ranked[0].Action.Arg).Id;
                Trace?.Invoke($"推算完成：{h.Result.Playouts} 局模拟 · 推荐 {(h.Ranked.Count > 0 ? Names.Ranked(h, h.Ranked[0]) : "无")} · 各选项："
                    + string.Join("；", h.Ranked.Take(4).Select(a => $"{Names.Ranked(h, a)} 绮彩{a.Chromatic:0.00} 至臻{a.Ultimate:0.00}")));
                if (_pendingKey != _settle.Committed) return;
                // 算完时画面已经不是这一步了（玩家点了、画面在变）：先不显示，等画面回到这一步再由 ShowSettled 放出
                // （2026-11-07 华为录屏：撤下的“吃这个”“选这张”算完后又被放回，挂在别的画面上）
                if (_frameKey is { } now && !_settle.Matches(now)) return;
                if (!MayShow(_pendingKey)) return;  // 刚撤下的局面：到时间由 ShowSettled 放出
                _hint = h;
            }
            SetStatus($"已识别 · {LastScanMs:0} 毫秒");
        };
        _hints.Log += s => Trace?.Invoke("推算：" + s);
        _hints.Failed += ex =>
        {
            Trace?.Invoke("推算出错：" + ex);
            lock (_gate) { _hint = null; _pendingKey = null; }
            _settle.Reset();  // 重新认定后会再算一次
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
        // 整个画面和上次识别时几乎一样（停在选择画面、等玩家点）：直接用上次的结果，不再识别（手机上省 1 秒多）
        var thumb = Thumb(frame);
        bool same = LastScan != null && _lastThumb != null && _lastThumb.Length == thumb.Length && ThumbDiff(thumb, _lastThumb) < 2.5 && ++_reused < 4;
        ScanResult r;
        if (same) r = LastScan!;
        else
        {
            r = _scanner.Scan(frame);
            _lastThumb = thumb;
            _reused = 0;
        }
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
            // 每一帧只读出一个“局面键”交给 Settler；局面真的换了（Settler 认定）才重算，读数抖动不撤提示、不重算
            // （2026-11-06 两台实机：卡字、稀有度、进度一帧一个样，每变一下就重算，推荐一直闪、一直“计算中”）
            BoardRead? b = null;
            string key = r.Ok ? (b = ReadBoard(r)).Key
                : r.Pick is { } p ? "pick|" + p.Kind + "|" + string.Join(",", p.Cards.Select(c => c.Id ?? c.Text))
                : "none";
            bool changed = _settle.Feed(key);
            _frameKey = key;
            // 画面换了种类（棋盘 ↔ 选刑具/珍藏 ↔ 没棋盘）连着 2 帧：旧提示马上撤下，不等新画面认定
            // （2026-11-06 华为录屏：连吃结束进“典狱长珍藏”，“取消 → 结束”在珍藏画面上挂了 10 秒）
            string kindNow = KindOf(key);
            if (!changed && _settle.Committed is { } committed && KindOf(committed) != kindNow)
            {
                if (++_otherKind >= 1 && HasHint) Show(null, "识别中…", keepPending: true);
            }
            else _otherKind = 0;
            // 棋盘上卡牌变了（玩家点了卡、卡消失或换了）：旧提示这一帧就撤下，不等新局面认定（2026-11-07 华为：提示消失太慢）
            if (!changed && b != null && _settle.Committed is { } now2 && KindOf(now2) == "board" && Settler.Tail(key) != Settler.Tail(now2)
                && !key.Contains(ScreenText.UnreadCard) && HasHint)
                Show(null, "识别中…", keepPending: true);
            // 回到认定的画面：提示重新显示（算过的直接用）；等待中的“取消”提示到时间了也在这里放出来
            if (_settle.Matches(key) || changed) _boxScan = r;
            if (!changed && _settle.Matches(key)) ShowSettled();
            if (b != null && _settle.Matches(key))
            {
                _lastBoard = b.Board.Clone();
                _lastText = r.Text;
                if (b.Decision && !b.ConsumeOnly && !b.HuntPick) _textBeforeRed = r.Text;
            }
            if (!changed)
            {
                if (_settle.Committed == null && !HasHint) SetStatus($"识别中… {LastScanMs:0} 毫秒");
                return;
            }
            Trace?.Invoke("认定局面：" + key.Replace("\n", " "));
            // 离开选刑具画面：推荐的那件当作已选（手机刑具栏常读成“?”，2026-11-07 荣耀：整局推算都以为一件刑具都没有）
            if (b != null && _pickHint != null) { _pendingPick = _pickHint; _pickHint = null; }
            if (b != null) OnBoard(r, b);
            else if (r.Pick is { } pick) OnPick(pick, key);
            else Clear($"没看到塔罗斯监狱棋盘（{Zh.Reason(r.Reason)}）");
        }
        catch (Exception ex)
        {
            Clear("出错：" + ex.Message);
        }
    }

    private int _otherKind;
    /// <summary>最近一帧的局面键。</summary>
    private volatile string? _frameKey;
    /// <summary>选刑具画面上推荐的那件（离开选刑具画面、回到棋盘后当作已选，记进整局记忆）。</summary>
    private string? _pickHint, _pendingPick;
    /// <summary>这一局按推荐选过的刑具（新的一局开头清空）。</summary>
    private readonly List<string> _runPicks = [];

    /// <summary>
    /// 新的一局：上一局的目标进度、目标完成、绮彩数、算过的结果都清掉。
    /// </summary>
    private void NewRun()
    {
        _progress.Clear();
        _objectiveDone = false;
        _chroma = 0;
        _chromaCand = -1;
        _lastDays = null;
        _runPicks.Clear();
        _run.NewRun();
        lock (_gate) _cache.Clear();
        Trace?.Invoke("新的一局");
    }
    /// <summary>撤下提示时认定的那个局面，和它的提示最早什么时候能再放出来（新认出的局面不受限，马上显示）。</summary>
    private string? _hideKey;
    private DateTime _hideUntil;

    /// <summary>
    /// 玩家手指碰了屏幕：提示马上撤下（不等识别，2026-11-07：提示消失太慢）。点完画面变了就按新局面马上给；
    /// 1 秒后画面还是原来那样（点在空处），原来的提示再出来。
    /// </summary>
    public void UserTapped() => HideFor(TimeSpan.FromSeconds(1));

    /// <summary>
    /// 日志里刚有新动静（走了一步、点了卡、换了天）：提示马上撤下，原来那个局面的提示 2.5 秒内不再放回来
    /// （游戏切画面有动画，1 秒后屏幕看着还是老样子，3.2 会把旧提示又放回来，2026-11-07 荣耀“选完还停在上一次的提示”）。
    /// </summary>
    public void LogActivity() => HideFor(TimeSpan.FromSeconds(2.5));

    private void HideFor(TimeSpan span)
    {
        var until = DateTime.Now + span;
        if (_hideKey == _settle.Committed && until < _hideUntil) return;
        (_hideKey, _hideUntil) = (_settle.Committed, until);
        if (HasHint) Show(null, "识别中…", keepPending: true);
    }

    /// <summary>这个局面的提示现在能不能显示：刚撤下的那个局面要等到时间，别的（新认出的）马上能显示。</summary>
    private bool MayShow(string? key) => key != _hideKey || DateTime.Now >= _hideUntil;
    private byte[]? _lastThumb;
    private int _reused;

    /// <summary>画面缩成 96×44 的灰度小图，用来判断画面变没变。</summary>
    private static byte[] Thumb(Frame f)
    {
        const int W = 96, H = 44;
        var t = new byte[W * H];
        var px = f.Pixels;
        for (int y = 0; y < H; y++)
        {
            int sy = (int)((y + 0.5) * f.Height / H);
            for (int x = 0; x < W; x++)
            {
                int sx = (int)((x + 0.5) * f.Width / W);
                int i = (sy * f.Width + sx) * 4;
                t[y * W + x] = (byte)((px[i] + 2 * px[i + 1] + px[i + 2]) >> 2);
            }
        }
        return t;
    }

    /// <summary>两张小图每个点的平均差（0～255）。</summary>
    private static double ThumbDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
    private readonly SlotReader _slots;
    /// <summary>当前认定局面最近一帧的识别结果：画框用它的位置（卡牌、牢房在画面上的哪里）。</summary>
    private ScanResult? _boxScan;
    /// <summary>这一局的目标已经完成（新的一局清掉）。</summary>
    private bool _objectiveDone;
    private Hint? _held;
    private DateTime _heldUntil;

    private static string KindOf(string key) => key.StartsWith("pick|") ? "pick" : key == "none" ? "none" : "board";

    /// <summary>当前认定局面的提示：缓存里有就显示；“取消”类提示到时间才显示。</summary>
    private void ShowSettled()
    {
        Hint? show = null;
        lock (_gate)
        {
            if (_pendingKey == null || _pendingKey != _settle.Committed) return;
            if (!MayShow(_pendingKey)) return;
            if (_held != null && DateTime.Now >= _heldUntil) { show = _held; _held = null; _cache[_pendingKey] = show; }
            else if (_hint == null && _held == null && _cache.TryGetValue(_pendingKey, out var c)) show = c;
            if (show == null) return;
            _hint = show;
        }
        SetStatus($"已识别 · {LastScanMs:0} 毫秒");
    }

    /// <summary>选吃的目标时推荐“取消”：多半是吃完一口、红屏正在退的过渡画面。画面保持 1.5 秒才显示（2026-11-06 华为录屏：连吃最后总闪一下“取消 → 结束”）。</summary>
    private static bool IsCancel(Hint h) => h.ConsumeOnly && h.Ranked.Count > 0 && h.Ranked[0].Action.Kind is not (ActionKind.Consume or ActionKind.Chain);

    private sealed record BoardRead(EditableSandbox Board, string Key, bool Decision, bool Destinations, bool ConsumeOnly, bool HuntPick, LoggedRun? Live);

    /// <summary>读一帧棋盘：盖住的格子沿用、每格多数表决、天数/刑具/绮彩用整局记忆，得出局面键和这是什么画面。</summary>
    private BoardRead ReadBoard(ScanResult r)
    {
        var board = EditableSandbox.FromScan(r);
        // 这一帧被挡住、看不清的格子，沿用上次读到的；守卫标记（会闪）一时没认出也沿用上次的位置
        // （2026-11-05 实机录屏：15 号格一帧“被挡住”一帧“未开”，守卫位置时有时无）
        if (_sticky != null)
        {
            for (int n = 2; n <= 16; n++)
                if (board[n].Covered && !_sticky[n].Covered) board.Cells[n - 1] = _sticky[n].Clone();
            // 只有完全没认出守卫（0）才沿用上次的位置。1 是审讯室，是真的位置，不能换掉
            // （2026-11-06 小米实机 2.6：守卫进了审讯室读成 1，被换成上一步的 16 号，推算不知道在审讯室，“煽动/赏赐”给成“结束”“吃9号”）
            if (board.Warden == 0 && _sticky.Warden > 0) board.Warden = _sticky.Warden;
        }
        // 画面上是“煽动 / 赏赐”：守卫一定在审讯室
        if (r.Text?.Cards.Any(c => c.Meaning is "instigate" or "bestow") == true) board.Warden = 1;
        _sticky = board.Clone();
        Vote(board);
        // 选目的地：同一个局面（守卫位置、天数不变）里读到过的目的地都算上，某一帧漏读一张卡不会少一个选项
        // （2026-11-06 小米实机：选中审讯室时只读到 [5]，选中 5 号时只读到 [1]，推荐就在两者间来回反）
        if (board.Phase == Phases.Destination && board.Destinations.Count > 0)
        {
            var destAt = (board.Warden, board.Days ?? _lastDays);
            if (_destAt != destAt) { _destAt = destAt; _dest.Clear(); }
            foreach (int c in board.Destinations) _dest.Add(c);
            if (_dest.Count <= 3) board.Destinations = [.. _dest.Order()];
        }
        var live = _live?.Invoke();
        if (live != null) board.Days = live.RemainingDays;  // 日志里的天数比画面上认的准
        if (board.Days is int nd && _lastDays is int od && nd > od) NewRun();  // 剩余天数变多：新的一局（开局刚选的刑具还在 _pendingPick 里，下面才记上，不会被清掉）
        _run.Days(board.Days);
        if (_pendingPick != null)
        {
            if (!_runPicks.Contains(_pendingPick)) _runPicks.Add(_pendingPick);
            Trace?.Invoke("记下刚选的刑具：" + _pendingPick);
            _pendingPick = null;
        }
        // 这一局选过的刑具每次都补进整局记忆（整局记忆因换局清空时也不丢；2026-11-07 华为：开局选的捕兽夹被清掉）
        foreach (var picked in _runPicks) _run.Picked(picked);
        board.Days ??= _lastDays;
        if (board.Days is int d) _lastDays = d;
        // 刑具栏和绮彩数：用整局记住的，不只看这一帧（被挡住、动画、没认出都沿用之前的）
        board.Instruments = [.. _run.Instruments(r.Instruments?.Instruments.Select(x => x.Id).ToList() ?? [])];
        board.MarksByInstruments();
        // 绮彩数用手机自己的跟踪（见 Chroma），不用电脑版的“读高一次就记住”：手机画面变暗、动画的那几帧常读成 6、10
        // （2026-11-06 小米实机：绮彩栏是空的，推算却一直当成 6/10，不再去争彩色，玩家说彩色箱子比电脑版少）
        // 只看绮彩栏自己被挡没有（刑具栏在手机上常读成 hidden，拿它判断会让绮彩数永远不更新）
        bool dim = r.Chromatic?.Covered == true;
        board.Chromatic = Chroma(board.Chromatic, dim);
        // 读到日志：绮彩数至少是日志里记的
        if (_live?.Invoke() is { FromStart: true } logRun) board.Chromatic = Math.Max(board.Chromatic, logRun.ChromaticAtLeast);

        // 局面键不看绮彩计数和刑具栏（容易读得忽有忽无，变化时局面本身也会变）
        string key = System.Text.RegularExpressions.Regex.Replace(SandboxText.Describe(board), @" · Torture Instruments \[[^\]]*\] · Chromatic \d+", "") + "§" + (board.Phase == Phases.Destination && board.Destinations.Count > 0 ? "dest" : CardsKey(r.Text)) + "|" + r.Text?.ConsumePick + r.Text?.RatwayPick;

        bool destinations = board.Phase == Phases.Destination && board.Destinations.Count > 0;
        bool decision = r.Text?.Cards.Any(c => c.Meaning is var t
            && (t.Contains("consume") || t.Contains("replace") || t.Contains("hunt") || t.Contains("instigate") || t.Contains("bestow") || t == "end" || t.EndsWith(" end"))) == true;
        bool consumeOnly = r.Text?.ConsumePick == true;
        if (r.Text?.Cards.Any(c => c.Meaning.Contains("hunt")) == true) _huntOffer = (board.Warden, board.Days, board.Clone(), r.Text);
        bool huntPick = r.Text?.RatwayPick == true && _huntOffer is { } hunt && hunt.Warden == board.Warden && hunt.Days == board.Days;
        if (r.Text?.RatwayPick == true && !huntPick) decision = true;
        // 寻猎选目标（“请选择目标”）也是要做决定的一步（2026-11-07 华为录屏：选寻猎目标时一直“暂无”）
        if (huntPick) decision = true;
        var here = board.Warden > 1 ? board[board.Warden] : null;
        bool endOnly = r.Text?.Cards is { Count: > 0 } cards && cards.All(c => c.Meaning is "end" || c.Meaning.EndsWith(" end"));
        if (decision && !consumeOnly && r.Text?.RatwayPick != true && here != null && here.Kind != CellKind.Inmate && !endOnly) decision = false;
        return new BoardRead(board, key, decision, destinations, consumeOnly, huntPick, live);
    }

    /// <summary>认定了一个新的棋盘局面：要选择就推算（算过的直接用），不用选择就撤下提示。</summary>
    private void OnBoard(ScanResult r, BoardRead b)
    {
        var board = b.Board;
        bool consumeOnly = b.ConsumeOnly, huntPick = b.HuntPick;
        // 今天吃过没有：第一次进“选目标”画面时还没吃；选完目标回到棋盘再出现“选目标”才是连吃
        // （2026-11-06 实机：一进选目标就记成“今天吃过”，推算当成连吃，常常建议“先取消再吃”）
        if (consumeOnly)
        {
            if (!_inConsumePick)
            {
                _consumedToday = _consumeLeftDay != null && _consumeLeftDay == board.Days;
                _inConsumePick = true;
            }
        }
        else if (_inConsumePick)
        {
            _inConsumePick = false;
            _consumeLeftDay = _lastDays ?? board.Days;
        }
        if (!consumeOnly && !b.Destinations && !b.Decision)
        {
            Show(null, "等待选择画面…");
            return;
        }
        var hb = huntPick ? _huntOffer!.Value.Board.Clone() : board;
        var text = consumeOnly ? _textBeforeRed ?? r.Text : huntPick ? _huntOffer!.Value.Text : r.Text;
        var observation = GameObservation.From(hb, text, 0);
        if (observation.Objective is { } obj)
        {
            int best = Math.Max(observation.ObjectiveProgress, _progress.GetValueOrDefault(obj));
            _progress[obj] = best;
            observation.ObjectiveProgress = best;
            // 目标完成过就一直算完成（读到“已完成”，或中途给过一次选刑具 = 目标奖励）
            if (observation.ObjectiveDone) _objectiveDone = true;
            // 读到日志：目标完成没有、“弱肉强食”吃了几次、“暗流涌动”触发了几次狱中事变，以日志为准（和电脑版一样）
            if (b.Live is { FromStart: true } lr)
            {
                if (lr.ObjectiveDone) _objectiveDone = true;
                int counted = obj == Tartarus.Rules.ObjectiveKind.SurvivalOfTheFittest ? lr.Consumes
                    : obj == Tartarus.Rules.ObjectiveKind.Undercurrents ? lr.Incidents : -1;
                if (counted >= 0) observation.ObjectiveProgress = Math.Max(observation.ObjectiveProgress, Math.Min(counted, Tartarus.Rules.GameData.Target(obj)));
            }
            if (_objectiveDone) observation.ObjectiveDone = true;
        }
        // 审讯室：卡读出一张就够了（两张一定同时在）
        if (text?.Cards.Any(c => c.Meaning is "instigate" or "bestow") == true) observation.Interrogation = true;
        observation.ConsumedToday = b.Live != null ? b.Live.ConsumedToday
            : consumeOnly ? _consumedToday                                   // 选目标画面上：进来时判断好的
            : _consumeLeftDay != null && _consumeLeftDay == hb.Days;          // 棋盘上：今天离开过选目标画面 = 今天吃过
        observation.ChainGoesOn = observation.ConsumedToday == true && consumeOnly;
        Request(b.Key, observation, consumeOnly, huntPick);
    }

    /// <summary>认定了选刑具 / 典狱长珍藏画面。</summary>
    private void OnPick(PickRead pick, string key)
    {
        // 审讯室 / 典狱长珍藏：在当前局面上算。只有这一局还没读到过棋盘（开局第一次选刑具）才用空棋盘
        // （2026-11-06 实机：中途进审讯室时天数一帧没认出（=0）就被换成了第 1 天的空棋盘，推荐全错）
        var under = _lastBoard?.Clone();
        // 新的一局开头选刑具：上一个棋盘是上一局最后一天（剩 0 天），或日志说这局还没选过刑具 → 用空棋盘
        // （2026-11-07 荣耀：新一局第一次选刑具在上一局最后的棋盘上算）
        var liveRun = _live?.Invoke();
        bool newRun = pick.Kind != PickRead.Treasure && (under?.Days == 0 || liveRun is { FromStart: true, Day: 0, InstrumentPicks: <= 1 });
        if (newRun) { under = null; NewRun(); }
        if (under == null) under = EditableSandbox.Empty();
        else
        {
            under.Days ??= _lastDays;
            under.Instruments = [.. _run.Instruments([])];
            under.MarksByInstruments();
        }
        Trace?.Invoke($"选择画面 {pick.Kind}：在 {(_lastBoard == null ? "空" : "当前")}局面上算 · 第 {under.Days?.ToString() ?? "?"} 天 · 刑具 [{string.Join(",", under.Instruments)}]");
        var observation = GameObservation.FromPick(pick, under, _lastText);
        if (pick.Kind != PickRead.Treasure && observation.OfferForObjective && _lastBoard != null)
        {
            if (!_objectiveDone) Trace?.Invoke("中途选刑具：目标已完成");
            _objectiveDone = true;
        }
        Request(key, observation, consumeOnly: false, huntOnly: false);
    }

    /// <summary>推算一个认定的局面。这个局面算过就直接用上次的结果（读数在两个局面间来回时不再重算）。</summary>
    private void Request(string key, Observation observation, bool consumeOnly, bool huntOnly)
    {
        Hint? cached;
        lock (_gate)
        {
            _pendingKey = key;
            _held = null;
            _cache.TryGetValue(key, out cached);
            if (!MayShow(key)) cached = null;  // 刚撤下的局面：算过也先不放，到时间由 ShowSettled 放出
            _hint = cached;
        }
        if (cached != null)
        {
            SetStatus($"已识别 · {LastScanMs:0} 毫秒");
            return;
        }
        // HintService 认为“同一个局面已经算过”时不会再报结果：先忘掉再算，保证这次一定有结果
        if (!_hints.Request(observation, consumeOnly, huntOnly))
        {
            _hints.Clear();
            if (!_hints.Request(observation, consumeOnly, huntOnly)) { SetStatus("暂无"); return; }
        }
        SetStatus("计算中…");
    }

    private void Show(Hint? hint, string status, bool keepPending = false)
    {
        lock (_gate) { _hint = hint; if (!keepPending) { _pendingKey = null; _held = null; } }
        SetStatus(status);
    }

    /// <summary>转屏、重新开始后：忘掉之前读到的一切，从头识别。</summary>
    public void Reset()
    {
        _run.NewRun();
        _lastBoard = null;
        _lastDays = null;
        _consumeLeftDay = null;
        _inConsumePick = _consumedToday = false;
        _sticky = null;
        _cellHist.Clear();
        _progress.Clear();
        _settle.Reset();
        _objectiveDone = false;
        _chroma = 0;
        _chromaCand = -1;
        lock (_gate) _cache.Clear();
        Clear("识别中…");
    }

    private int _chroma, _chromaCand = -1, _chromaTimes;
    private (int Warden, int? Days)? _destAt;
    private readonly HashSet<int> _dest = [];

    /// <summary>
    /// 这一局的绮彩数：同一个新读数连着读到够多次才改。涨 1、2 个要 6 次；一下子涨很多（多半是读错）要 20 次；
    /// 往下改（之前记错了）要 12 次。画面变暗 / 刑具栏被挡的帧不算。
    /// </summary>
    private int Chroma(int read, bool dim)
    {
        if (dim || read == _chroma) { if (!dim) { _chromaCand = -1; _chromaTimes = 0; } return _chroma; }
        _chromaTimes = read == _chromaCand ? _chromaTimes + 1 : 1;
        _chromaCand = read;
        // 一局里绮彩只会变多：读到比现在少的一律不认（换局时清零）。2026-11-07 华为：0→2→0、3→6→8→7→0 来回变
        if (read < _chroma) return _chroma;
        int need = read - _chroma <= 2 ? 4 : 20;
        if (_chromaTimes >= need)
        {
            Trace?.Invoke($"绮彩数 {_chroma} → {read}");
            _chroma = read;
            _chromaCand = -1;
            _chromaTimes = 0;
        }
        return _chroma;
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

    private void Clear(string status)
    {
        _hints.Clear();
        Show(null, status);
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
        var shown = Describe(hint, _status);
        if (hint != null && _boxScan is { } scan && shown.HasHint)
        {
            try
            {
                shown = shown with { Boxes = HintBoxes.For(scan, hint, _slots.ChromaShift < 0 ? -_slots.ChromaShift : 0), FrameWidth = scan.Layout?.Width ?? 0, FrameHeight = scan.Layout?.Height ?? 0 };
            }
            catch (Exception) { }
        }
        Changed?.Invoke(shown);
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
        return new Shown(main, detail, extra, true, status) { Chromatic = best.Chromatic, Ultimate = best.Ultimate };
    }

    public void Dispose()
    {
        _hints.Dispose();
        _scanner.Dispose();
    }
}
