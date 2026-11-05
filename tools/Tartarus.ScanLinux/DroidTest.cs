using Tartarus.App;
using Tartarus.Rules;
using Tartarus.Vision;
using TartarusDroid;

/// <summary>
/// droid &lt;图片&gt;...：安卓版的流程（Pipeline）逐张处理，每张处理两次（手机上同一局面要连续读到两次才推算），
/// 打印浮窗会显示的内容；最后用一个构造的“选目的地”局面测推算和中文走法名。
/// </summary>
internal static class DroidTest
{
    public static int Run(string[] files)
    {
        Console.WriteLine("TARTARUS_HOME = " + Environment.GetEnvironmentVariable("TARTARUS_HOME"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var p = new Pipeline();
        Console.WriteLine($"加载模型 {sw.ElapsedMilliseconds} 毫秒");
        Shown? last = null;
        p.Changed += s => last = s;
        foreach (var f in files)
        {
            var frame = ImageIO.Load(f);
            for (int i = 0; i < 2; i++)
            {
                p.Process(frame);
                Console.WriteLine($"  {Path.GetFileName(f)} 第{i + 1}次 识别 {p.LastScanMs:0} 毫秒 → 大字「{last?.Main}」 {last?.Detail}");
            }
        }

        // 构造一个局面：第 5 天、要选目的地（3 号、7 号、审讯室），看推算和中文名
        var hints = new HintService(playouts: 512, seconds: 1.2);
        var done = new ManualResetEventSlim();
        Hint? got = null;
        hints.Ready += h => { got = h; done.Set(); };
        var engine = new Engine();
        var state = engine.NewRun(42);
        Console.WriteLine("构造局面：\n" + StateText.Describe(state));
        var b = EditableSandbox.Empty();
        Console.WriteLine("Empty sandbox: " + SandboxText.Describe(b));
        sw.Restart();
        // 新局的第一个选择是选刑具，这里直接推算引擎生成的新局
        var search = new Tartarus.Solver.MonteCarloSearch(engine, Tartarus.Solver.EvalWeights.Default) { Threads = 2 };
        var result = search.Search(state, 512, TimeSpan.FromSeconds(1.2));
        Console.WriteLine($"推算 {sw.ElapsedMilliseconds} 毫秒，{result.Playouts} 局模拟，前三：");
        foreach (var a in result.Actions.Take(3)) Console.WriteLine($"  {Names.Action(a.Action)}  绮彩≈{a.Chromatic:0.00}");
        return 0;
    }
}
