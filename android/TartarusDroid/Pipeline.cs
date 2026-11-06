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
        var cells = new CnnCellReader(panels: new SlotReader());
        var presence = SandboxPresence.FindTemplate() is { } t ? SandboxPresence.Load(t) : null;
        _scanner = new LayoutScanner(new OcrEngine(threads: Math.Clamp(Environment.ProcessorCount / 2, 2, 4), gameLanguage: "zh"), cells, presence);
        // 和电脑版一样每个选项 1024 局模拟；手机慢，时间给到 1.5 秒才跑得满（2.4 起局面认定后不再反复重算，有这个时间）
        // （2026-11-06：手机原来 512 局、0.6 秒，玩家反映推荐不如电脑版、彩色箱子少）
        _hints = new HintService(playouts: 1024, seconds: 1.5);
        _hints.Ready += h =>
        {
            lock (_gate)
            {
                // 只认当前认定局面的结果：算完时局面已经换了，就只存进缓存，不显示
                if (_pendingKey == null) return;
                if (_cache.Count > 60) _cache.Clear();
                _cache[_pendingKey] = h;
                Trace?.Invoke($"推算完成：{h.Result.Playouts} 局模拟 · 推荐 {(h.Ranked.Count > 0 ? Names.Ranked(h, h.Ranked[0]) : "无")}");
                if (_pendingKey != _settle.Committed) return;
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
            // 每一帧只读出一个“局面键”交给 Settler；局面真的换了（Settler 认定）才重算，读数抖动不撤提示、不重算
            // （2026-11-06 两台实机：卡字、稀有度、进度一帧一个样，每变一下就重算，推荐一直闪、一直“计算中”）
            BoardRead? b = null;
            string key = r.Ok ? (b = ReadBoard(r)).Key
                : r.Pick is { } p ? "pick|" + p.Kind + "|" + string.Join(",", p.Cards.Select(c => c.Id ?? c.Text))
                : "none";
            bool changed = _settle.Feed(key);
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
            if (b != null) OnBoard(r, b);
            else if (r.Pick is { } pick) OnPick(pick, key);
            else Clear($"没看到塔罗斯监狱棋盘（{Zh.Reason(r.Reason)}）");
        }
        catch (Exception ex)
        {
            Clear("出错：" + ex.Message);
        }
    }

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
        if (board.Days is int nd && _lastDays is int od && nd > od) { _progress.Clear(); _chroma = 0; lock (_gate) _cache.Clear(); }  // 剩余天数变多：新的一局
        _run.Days(board.Days);
        board.Days ??= _lastDays;
        if (board.Days is int d) _lastDays = d;
        // 刑具栏和绮彩数：用整局记住的，不只看这一帧（被挡住、动画、没认出都沿用之前的）
        board.Instruments = [.. _run.Instruments(r.Instruments?.Instruments.Select(x => x.Id).ToList() ?? [])];
        board.MarksByInstruments();
        // 绮彩数用手机自己的跟踪（见 Chroma），不用电脑版的“读高一次就记住”：手机画面变暗、动画的那几帧常读成 6、10
        // （2026-11-06 小米实机：绮彩栏是空的，推算却一直当成 6/10，不再去争彩色，玩家说彩色箱子比电脑版少）
        bool dim = r.Chromatic?.Covered == true || r.Instruments?.Instruments.Any(x => x.Id == SlotReader.Hidden) == true;
        board.Chromatic = Chroma(board.Chromatic, dim);

        // 局面键不看绮彩计数和刑具栏（容易读得忽有忽无，变化时局面本身也会变）
        string key = System.Text.RegularExpressions.Regex.Replace(SandboxText.Describe(board), @" · Torture Instruments \[[^\]]*\] · Chromatic \d+", "") + "|" + (board.Phase == Phases.Destination && board.Destinations.Count > 0 ? "dest" : CardsKey(r.Text)) + "|" + r.Text?.ConsumePick + r.Text?.RatwayPick;

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
        if (under == null) under = EditableSandbox.Empty();
        else
        {
            under.Days ??= _lastDays;
            under.Instruments = [.. _run.Instruments([])];
            under.MarksByInstruments();
        }
        Trace?.Invoke($"选择画面 {pick.Kind}：在 {(_lastBoard == null ? "空" : "当前")}局面上算 · 第 {under.Days?.ToString() ?? "?"} 天 · 刑具 [{string.Join(",", under.Instruments)}]");
        var observation = GameObservation.FromPick(pick, under, _lastText);
        Request(key, observation, consumeOnly: false, huntOnly: false);
    }

    /// <summary>推算一个认定的局面。这个局面算过就直接用上次的结果（读数在两个局面间来回时不再重算）。</summary>
    private void Request(string key, Observation observation, bool consumeOnly, bool huntOnly)
    {
        Hint? cached;
        lock (_gate)
        {
            _pendingKey = key;
            _cache.TryGetValue(key, out cached);
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

    private void Show(Hint? hint, string status)
    {
        lock (_gate) { _hint = hint; _pendingKey = null; }
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
        int need = read > _chroma ? (read - _chroma <= 2 ? 6 : 20) : 12;
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
