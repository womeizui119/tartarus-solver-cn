using Android.Content;
using Android.Runtime;
using Android.Util;
using Tartarus.Vision;

namespace TartarusDroid;

/// <summary>
/// 读游戏日志（可选）：游戏把日志写在 Android/data/游戏包名/…/Saved/Logs 里，安卓 11 起别的应用读不到，
/// 借 Shizuku（玩家自己装、自己启动的授权工具）用系统调试身份读。读到的每一行交给电脑版同一个 GameLog 解析，
/// 得到剩余天数、今天吃过没有等画面上看不准的信息。没装 / 没启动 Shizuku 或游戏没写日志时，什么都不做，软件照样靠画面工作。
/// Shizuku 的库没有生成 C# 绑定（只打包进去），用 Java 反射调用。
/// </summary>
public sealed class LogReader : IDisposable
{
    private const int PermissionCode = 3916;
    private readonly Context _ctx;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private Java.Lang.Process? _tail;
    private GameLog _log = new();
    private int _lines, _s16;
    private bool _reported;
    private DateTime _since;

    /// <summary>浮窗/记录里显示的状态：“日志：已连接” 等。</summary>
    public string State { get; private set; } = "日志：未连接";
    public string? Path { get; private set; }
    public bool Connected { get; private set; }
    public event Action<string>? StateChanged;

    public LogReader(Context ctx)
    {
        _ctx = ctx;
        _thread = new Thread(Run) { IsBackground = true, Name = "gamelog" };
        _thread.Start();
    }

    /// <summary>当前这一局（没读到日志或这局已结束时为 null）。</summary>
    public LoggedRun? Live
    {
        get
        {
            lock (_gate) return _log.Current is { Finished: false } run && run.MaxDays > 0 ? run : null;
        }
    }

    private void Set(string state, bool connected = false)
    {
        Connected = connected;
        if (state == State) return;
        State = state;
        Saver.Log(_ctx, state + (Path != null ? "：" + Path : ""));
        StateChanged?.Invoke(state);
    }

    private void Run()
    {
        bool asked = false;
        while (!_stop)
        {
            try
            {
                if (!ShizukuReady(out string why, ref asked))
                {
                    Set(why);
                    Thread.Sleep(3000);
                    continue;
                }
                var path = FindLog();
                if (path == null)
                {
                    Set("日志：没找到游戏日志（游戏可能没写日志）");
                    Thread.Sleep(15000);
                    continue;
                }
                Path = path;
                Follow(path);
            }
            catch (Exception ex)
            {
                Log.Error("tartarus", ex.ToString());
                Set("日志：读取出错 " + ex.Message);
                Thread.Sleep(5000);
            }
        }
    }

    private static Java.Lang.Class Shizuku => Java.Lang.Class.ForName("rikka.shizuku.Shizuku");

    private bool ShizukuReady(out string why, ref bool asked)
    {
        Java.Lang.Class cls;
        try { cls = Shizuku; }
        catch (Exception) { why = "日志：缺少 Shizuku 组件"; return false; }
        bool ping = Bool(cls.GetMethod("pingBinder")!.Invoke(null));
        if (!ping)
        {
            why = "日志：未连接（没装或没启动 Shizuku，只看画面）";
            return false;
        }
        int granted = Int(cls.GetMethod("checkSelfPermission")!.Invoke(null));
        if (granted != 0) // PackageManager.PERMISSION_GRANTED
        {
            if (!asked)
            {
                asked = true;
                cls.GetMethod("requestPermission", Java.Lang.Integer.Type)!.Invoke(null, Java.Lang.Integer.ValueOf(PermissionCode));
            }
            why = "日志：请在 Shizuku 弹窗里允许“塔罗斯求解器”";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>用系统调试身份执行一条 shell 命令（Shizuku.newProcess 是私有方法，反射调用）。</summary>
    private static Java.Lang.Process Shell(string command)
    {
        var stringArray = Java.Lang.Class.ForName("[Ljava.lang.String;");
        var method = Shizuku.GetDeclaredMethod("newProcess", stringArray, stringArray, Java.Lang.Class.FromType(typeof(Java.Lang.String)))!;
        method.Accessible = true;
        var cmd = new Java.Lang.Object(JNIEnv.NewArray(new[] { "sh", "-c", command }), JniHandleOwnership.TransferLocalRef);
        return method.Invoke(null, cmd, null, null)!.JavaCast<Java.Lang.Process>()!;
    }

    private static string RunShell(string command)
    {
        var p = Shell(command);
        using var reader = new StreamReader(p.InputStream!, System.Text.Encoding.UTF8);
        string output = reader.ReadToEnd();
        p.WaitFor();
        return output;
    }

    /// <summary>在 Android/data 里找火炬之光的日志：Saved/Logs 下最新的 .log。</summary>
    private static string? FindLog()
    {
        string list = RunShell("find /sdcard/Android/data -maxdepth 10 -path '*Saved/Logs/*.log' 2>/dev/null | while read f; do echo \"$(stat -c %Y \"$f\") $f\"; done | sort -rn");
        var files = list.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).Select(p => p[1].Trim()).ToList();
        return files.FirstOrDefault(f => f.Contains("orch", StringComparison.OrdinalIgnoreCase) || f.Contains("TLI", StringComparison.Ordinal))
            ?? files.FirstOrDefault();
    }

    /// <summary>从头读一遍（恢复本局状态），然后跟着读新写的行；游戏重开换了新日志时重新找。</summary>
    private void Follow(string path)
    {
        lock (_gate) _log = new GameLog();
        _tail = Shell($"tail -n +1 -f '{path}'");
        using var reader = new StreamReader(_tail.InputStream!, System.Text.Encoding.UTF8);
        Set("日志：已连接", connected: true);
        (_lines, _s16, _reported, _since) = (0, 0, false, DateTime.Now);
        var lastCheck = DateTime.Now;
        while (!_stop)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            bool used;
            lock (_gate) used = _log.Feed(line);
            _lines++;
            if (line.Contains("S16", StringComparison.Ordinal)) _s16++;
            // 连上后 20 秒在记录里写一次：读了多少行、有多少行是塔罗斯玩法的（判断手机版日志能不能用）
            if (!_reported && DateTime.Now - _since > TimeSpan.FromSeconds(20))
            {
                _reported = true;
                var run = Live;
                Saver.Log(_ctx, $"日志：已读 {_lines} 行，其中塔罗斯相关 {_s16} 行；本局：{(run == null ? "没认出" : $"第 {run.Day}/{run.MaxDays} 天，今天吃过={run.ConsumedToday}")}");
            }
            if (DateTime.Now - lastCheck > TimeSpan.FromSeconds(30))
            {
                lastCheck = DateTime.Now;
                if (FindLog() is { } newest && newest != path) break;
            }
        }
        try { _tail.Destroy(); } catch (Exception) { }
        _tail = null;
    }

    private static bool Bool(Java.Lang.Object? o) => o?.JavaCast<Java.Lang.Boolean>()?.BooleanValue() == true;
    private static int Int(Java.Lang.Object? o) => o?.JavaCast<Java.Lang.Integer>()?.IntValue() ?? -1;

    public void Dispose()
    {
        _stop = true;
        try { _tail?.Destroy(); } catch (Exception) { }
    }
}
