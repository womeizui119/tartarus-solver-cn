using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using Android.Util;
using Tartarus.Vision;

namespace TartarusDroid;

/// <summary>
/// 读游戏日志（可选，测试版 3.2）。和电脑版一样把每一行交给 GameLog 解析：剩余天数、今天吃过没有、目标完成没有、
/// 绮彩数、吃了几次……都以日志为准；日志一有新动静（走了一步），浮窗上的提示马上撤下。三条路，按顺序试：
/// <list type="number">
/// <item>Shizuku 开着：先用它给本软件开“读取系统日志”权限（pm grant READ_LOGS，开一次一直有效），再直接读游戏的日志文件
/// （Android/data/游戏包名/…/Saved/Logs/*.log，和电脑版读的是同一种文件）。</item>
/// <item>没有 Shizuku，但“读取系统日志”权限已经开过：读手机的系统日志（logcat），游戏写的行如果也打印到这里就能用。</item>
/// <item>都没有：什么都不做，软件照样只看画面。</item>
/// </list>
/// 记录（记录-….txt）里会写清楚走的是哪条路、读了多少行、其中塔罗斯（S16）的有几行，以及前几行塔罗斯日志的样子。
/// Shizuku 的库只打包、没生成 C# 绑定，用反射调用；类要从本软件的 ClassLoader 找——后台线程直接 Class.forName
/// 用的是系统 ClassLoader，找不到打包进来的类（1.x 测试时一直显示“缺少 Shizuku 组件”就是这个原因）。
/// </summary>
public sealed class LogReader : IDisposable
{
    private const int PermissionCode = 3916;
    private const string ReadLogs = "android.permission.READ_LOGS";
    private readonly Context _ctx;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private Java.Lang.Process? _tail;
    private GameLog _log = new();
    private int _lines, _s16, _samples;
    private bool _reported, _askedShizuku, _grantTried;
    private DateTime _since;
    private string? _lastFound;

    /// <summary>浮窗菜单里显示的状态：“日志：已连接（文件）” 等。</summary>
    public string State { get; private set; } = "日志：未连接（只看画面）";
    public bool Connected { get; private set; }
    public event Action<string>? StateChanged;
    /// <summary>日志里来了一行改变了这一局状态的（走了一步、点了卡、换了天）：提示马上撤下。</summary>
    public event Action? Activity;

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
            lock (_gate) return Connected && _log.Current is { Finished: false } run && run.MaxDays > 0 ? run : null;
        }
    }

    private void Set(string state, bool connected = false)
    {
        Connected = connected;
        if (state == State) return;
        State = state;
        Saver.Log(_ctx, state);
        StateChanged?.Invoke(state);
    }

    private bool HasReadLogs => _ctx.CheckSelfPermission(ReadLogs) == Permission.Granted;

    private void Run()
    {
        Saver.Log(_ctx, $"日志：开始检查（读取系统日志权限：{(HasReadLogs ? "已开" : "未开")}）");
        while (!_stop)
        {
            try
            {
                bool shizuku = ShizukuReady(out string why);
                if (shizuku)
                {
                    // 顺手把“读取系统日志”权限开了：开一次一直有效，以后不开 Shizuku 也能读系统日志
                    if (!HasReadLogs && !_grantTried)
                    {
                        _grantTried = true;
                        string pkg = _ctx.PackageName!;
                        string res = RunShell($"pm grant {pkg} {ReadLogs} 2>&1; echo 结果=$?");
                        Saver.Log(_ctx, "日志：用 Shizuku 开读取系统日志权限：" + res.Trim() + "（要停止后重新打开本软件才生效）");
                    }
                    var path = FindLog();
                    Saver.Log(_ctx, "日志：Shizuku 找日志文件：" + (path ?? "没找到"));
                    if (path != null)
                    {
                        FollowFile(path);
                        continue;
                    }
                }
                if (HasReadLogs)
                {
                    FollowLogcat();
                    continue;
                }
                Set(shizuku ? "日志：没找到游戏日志文件（游戏里开启日志了吗）" : why);
                Thread.Sleep(shizuku ? 15000 : 3000);
            }
            catch (Exception ex)
            {
                Log.Error("tartarus", ex.ToString());
                Set("日志：读取出错 " + ex.Message);
                Thread.Sleep(5000);
            }
        }
    }

    private Java.Lang.Class? ShizukuClass()
    {
        try { return Java.Lang.Class.ForName("rikka.shizuku.Shizuku", true, _ctx.ClassLoader); }
        catch (Exception) { return null; }
    }

    private bool ShizukuReady(out string why)
    {
        var cls = ShizukuClass();
        if (cls == null) { why = "日志：缺少 Shizuku 组件"; return false; }
        bool ping = Bool(cls.GetMethod("pingBinder")!.Invoke(null));
        if (!ping)
        {
            why = HasReadLogs ? "日志：未连接" : "日志：未连接（没开 Shizuku，只看画面）";
            return false;
        }
        int granted = Int(cls.GetMethod("checkSelfPermission")!.Invoke(null));
        if (granted != 0) // PackageManager.PERMISSION_GRANTED
        {
            if (!_askedShizuku)
            {
                _askedShizuku = true;
                cls.GetMethod("requestPermission", Java.Lang.Integer.Type)!.Invoke(null, Java.Lang.Integer.ValueOf(PermissionCode));
            }
            why = "日志：请在 Shizuku 弹窗里允许“困兽玩法推理”";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>用系统调试身份执行一条 shell 命令（Shizuku.newProcess 是私有方法，反射调用）。</summary>
    private Java.Lang.Process Shell(string command)
    {
        var cls = ShizukuClass()!;
        var stringArray = Java.Lang.Class.ForName("[Ljava.lang.String;");
        var method = cls.GetDeclaredMethod("newProcess", stringArray, stringArray, Java.Lang.Class.FromType(typeof(Java.Lang.String)))!;
        method.Accessible = true;
        var cmd = new Java.Lang.Object(JNIEnv.NewArray(new[] { "sh", "-c", command }), JniHandleOwnership.TransferLocalRef);
        return method.Invoke(null, cmd, null, null)!.JavaCast<Java.Lang.Process>()!;
    }

    private string RunShell(string command)
    {
        var p = Shell(command);
        using var reader = new StreamReader(p.InputStream!, System.Text.Encoding.UTF8);
        string output = reader.ReadToEnd();
        p.WaitFor();
        return output;
    }

    /// <summary>在 Android/data 里找火炬之光的日志：Saved/Logs 下最新的 .log。找到的全部写进记录，方便核对。</summary>
    private string? FindLog()
    {
        string list = RunShell("find /sdcard/Android/data -maxdepth 10 -path '*Saved/Logs/*.log' 2>/dev/null | while read f; do echo \"$(stat -c %Y \"$f\") $(stat -c %s \"$f\") $f\"; done | sort -rn | head -20");
        var rows = list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 3)).Where(p => p.Length == 3).ToList();
        var files = rows.Select(r => r[2].Trim()).ToList();
        // 找到的文件只在变了的时候写进记录（3.2 每 30 秒写一遍，记录被撑到几千行）
        string joined = string.Join("|", files);
        if (rows.Count > 0 && joined != _lastFound)
        {
            _lastFound = joined;
            Saver.Log(_ctx, "日志：找到的日志文件（修改时间 大小 路径）：\n" + string.Join("\n", rows.Select(r => $"  {r[0]} {r[1]} {r[2]}")));
        }
        return files.FirstOrDefault(f => f.Contains("orch", StringComparison.OrdinalIgnoreCase) || f.Contains("TLI", StringComparison.Ordinal) || f.Contains("UE_game", StringComparison.Ordinal))
            ?? files.FirstOrDefault();
    }

    private void FollowFile(string path)
    {
        lock (_gate) _log = new GameLog();
        _tail = Shell($"tail -n +1 -f '{path}'");
        Follow("文件", path, checkNewest: () => FindLog() is { } newest && newest != path);
    }

    /// <summary>读手机的系统日志（要“读取系统日志”权限）。先读已有的，再跟着读新的。</summary>
    private void FollowLogcat()
    {
        lock (_gate) _log = new GameLog();
        _tail = Java.Lang.Runtime.GetRuntime()!.Exec(new[] { "logcat", "-v", "brief" });
        Follow("系统日志", "logcat", checkNewest: null);
        Thread.Sleep(3000);
    }

    private void Follow(string source, string where, Func<bool>? checkNewest)
    {
        using var reader = new StreamReader(_tail!.InputStream!, System.Text.Encoding.UTF8);
        Set($"日志：读取中（{source}）", connected: true);
        (_lines, _s16, _samples, _reported, _since) = (0, 0, 0, false, DateTime.Now);
        var lastCheck = DateTime.Now;
        while (!_stop)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            bool changed;
            lock (_gate) changed = _log.Feed(line);
            _lines++;
            if (line.Contains("S16", StringComparison.Ordinal))
            {
                _s16++;
                // 前 8 行塔罗斯日志原样写进记录：确认手机版日志的样子和电脑版一样
                if (_samples < 8) { _samples++; Saver.Log(_ctx, $"日志样本（{source}）：" + (line.Length > 300 ? line[..300] : line)); }
                if (_s16 == 1) Set($"日志：已连接（{source}）", connected: true);
            }
            if (changed) Activity?.Invoke();
            // 连上后 30 秒、之后每 2 分钟在记录里写一次：读了多少行、塔罗斯相关多少行、本局认出了什么
            if (DateTime.Now - _since > TimeSpan.FromSeconds(_reported ? 120 : 30))
            {
                _reported = true;
                _since = DateTime.Now;
                var run = Live;
                Saver.Log(_ctx, $"日志（{source} {where}）：已读 {_lines} 行，其中塔罗斯相关 {_s16} 行；本局："
                    + (run == null ? "没认出" : $"第 {run.Day}/{run.MaxDays} 天，今天吃过={run.ConsumedToday}，吃了 {run.Consumes} 次，目标完成={run.ObjectiveDone}，绮彩≥{run.ChromaticAtLeast}"));
                if (_s16 == 0) Set($"日志：{source}里还没有塔罗斯的日志", connected: false);
            }
            if (checkNewest != null && DateTime.Now - lastCheck > TimeSpan.FromSeconds(120))
            {
                lastCheck = DateTime.Now;
                if (checkNewest()) break;
            }
        }
        try { _tail?.Destroy(); } catch (Exception) { }
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
