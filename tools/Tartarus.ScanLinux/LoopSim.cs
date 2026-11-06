using Tartarus.Vision;
using TartarusDroid;

namespace Tartarus.ScanLinux;

/// <summary>
/// 模拟安卓主循环的决策逻辑（不含真正的录屏），用假画面源重现实机记录里的各种情况：
/// 画面静止不推新帧、帧流断掉、转屏、竖屏残留帧、画面切换。验证每种情况下推荐都能出来、不会卡死。
/// </summary>
public static class LoopSim
{
    public static void Run(string boardPng, string pickPng)
    {
        Console.WriteLine("加载模型…");
        using var p = new Pipeline();
        Shown? last = null; string log = "";
        p.Changed += s => last = s;
        p.Trace += s => log = s;
        var board = ImageIO.Load(boardPng);
        var pick = ImageIO.Load(pickPng);
        var portrait = new Frame(board.Height, board.Width, new byte[board.Width * board.Height * 4], Tartarus.Vision.PixelFormat.Rgba32);

        // 场景脚本：每一步 = (这一轮拿到的帧或 null, 是否新帧, 屏幕是横屏?)
        var script = new List<(string name, Frame? f, bool isNew, bool landscape)>();
        void Add(string name, Frame? f, bool isNew, bool landscape = true, int times = 1) { for (int i = 0; i < times; i++) script.Add((name, f, isNew, landscape)); }

        Add("选刑具画面，连续新帧", pick, true, times: 3);
        Add("选刑具画面静止，不推新帧（照样识别最新帧）", pick, false, times: 15);
        Add("切到棋盘，连续新帧", board, true, times: 3);
        Add("棋盘静止 3 秒（不推新帧）", board, false, times: 30);
        Add("切桌面：竖屏残留帧", portrait, true, landscape: false, times: 5);
        Add("切回游戏：转屏回横屏，但先来一张竖屏残留", portrait, true, landscape: true, times: 2);
        Add("横屏新帧到了", board, true, times: 3);
        Add("棋盘静止 10 秒", board, false, times: 100);

        long tick = 0; string lastName = ""; var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (name, f, isNew, landscape) in script)
        {
            tick++;
            if (name != lastName) { Console.WriteLine($"\n== {name}"); lastName = name; }
            // === 这里是 SolverService.Work 主循环的决策逻辑，照抄 ===
            Frame? frame = f;
            if (frame == null) { Console.Write("."); Thread.Sleep(100); continue; }
            bool orientationOk = (frame.Width > frame.Height) == landscape;
            if (!orientationOk) { Console.Write("x"); Thread.Sleep(100); continue; }  // 方向不对：等下一帧
            if (frame.Width < frame.Height) { Console.Write("P"); Thread.Sleep(100); continue; } // 请横屏
            p.Process(frame);
            Console.Write(last?.HasHint == true ? "★" : (last?.Main ?? "?")[..1]);
            Thread.Sleep(100);
            if (last?.HasHint == true && tick % 5 == 0) Console.Write($"[{last.Main}]");
        }
        Console.WriteLine($"\n\n总耗时 {sw.Elapsed.TotalSeconds:0.0} 秒。最后状态：「{last?.Main}」 {last?.Detail}");
        Console.WriteLine("图例：★ 有推荐  其他字 = 状态首字  . 没新帧跳过  x 方向不对跳过  P 竖屏");
    }
}
