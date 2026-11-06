using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using Android.Views;
using Tartarus.Vision;

namespace TartarusDroid;

/// <summary>
/// 前台服务：拿到录屏授权后，每隔一会儿取一帧游戏画面，交给 <see cref="Pipeline"/> 识别和推算，结果写到浮窗上。
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class SolverService : Service
{
    public const string ExtraCode = "code", ExtraData = "data";
    private const int NotificationId = 1;
    private const string Channel = "solver";

    public static SolverService? Running { get; private set; }

    private MediaProjection? _projection;
    private Capture? _capture;
    private Overlay? _overlay;
    private Pipeline? _pipeline;
    private Thread? _worker;
    private volatile bool _stop;
    private Frame? _lastFrame;
    private DateTime _lastAutoSave;
    private readonly Handler _ui = new(Looper.MainLooper!);

    public bool AutoSave { get; set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (_projection != null || intent == null) return StartCommandResult.NotSticky;
        Running = this;
        StartForegroundNow();
        // 崩溃时把原因写进“下载/TartarusSolver/记录-….txt”，发过来就能查
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Saver.Log(this, "崩溃：" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Saver.Log(this, "后台出错：" + e.Exception); e.SetObserved(); };
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Saver.Log(this, "崩溃：" + e.Exception);
        Saver.Log(this, $"手机：{Build.Manufacturer} {Build.Model} · 安卓 {Build.VERSION.Release} · 版本 {PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName}");
        int code = intent.GetIntExtra(ExtraCode, 0);
        var data = (Intent?)intent.GetParcelableExtra(ExtraData);
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        _projection = data != null ? mpm.GetMediaProjection(code, data) : null;
        if (_projection == null)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        _projection.RegisterCallback(new ProjectionStopped(this), _ui);

        _overlay = new Overlay(this);
        _overlay.Show(new Shown("准备中…", "第一次打开要解压识别模型，约十几秒", null, false, "准备中…"));
        _overlay.SaveClicked += () => Save(manual: true);
        _overlay.AutoSaveChanged += on => AutoSave = on;
        _overlay.StopClicked += StopAll;
        _overlay.Tapped += () => _pipeline?.UserTapped();


        _capture = new Capture(this, _projection, s => Saver.Log(this, s));
        _worker = new Thread(Work) { IsBackground = true, Name = "solver" };
        _worker.Start();
        return StartCommandResult.NotSticky;
    }

    private void StartForegroundNow()
    {
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.CreateNotificationChannel(new NotificationChannel(Channel, "求解器运行中", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
        var n = new Notification.Builder(this, Channel)
            .SetContentTitle("困兽玩法推理正在看游戏画面")
            .SetContentText("点浮窗上的 ■ 停止")
            .SetSmallIcon(Android.Resource.Drawable.IcMenuView)
            .SetContentIntent(open)
            .SetOngoing(true)
            .Build()!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, n, ForegroundService.TypeMediaProjection);
        else
            StartForeground(NotificationId, n);
    }

    private void Work()
    {
        try
        {
            ModelSetup.Prepare(this);
            Post(new Shown("准备中…", "正在加载识别和推算模型…", null, false, "准备中…"));
            _pipeline = new Pipeline();
            _pipeline.Trace += s => Saver.Log(this, s);
            _pipeline.Changed += Post;
            Post(new Shown("暂无", "进入塔罗斯监狱后，这里会写出该怎么走", null, false, "就绪"));
        }
        catch (Exception ex)
        {
            Post(new Shown("启动失败", ex.Message, null, false, "启动失败"));
            Log.Error("tartarus", ex.ToString());
            return;
        }
        long seen = 0;
        DateTime lastTrace = DateTime.MinValue, staleSince = DateTime.MinValue;
        while (!_stop)
        {
            var started = DateTime.Now;
            try
            {
                var cap = _capture!;
                if (cap.FollowRotation()) { _pipeline.Reset(); seen = 0; }
                var (frame, age, isNew) = cap.Latest(ref seen);
                var (sw, sh) = cap.Size;
                if (DateTime.Now - lastTrace > TimeSpan.FromSeconds(1))
                {
                    lastTrace = DateTime.Now;
                    Saver.Log(this, frame == null ? "还没有画面" : $"画面 {frame.Width}×{frame.Height}{(isNew ? " 新" : $" {age:0.0} 秒前")} · 共 {cap.Frames} 帧 · 出错 {cap.Errors}");
                }
                if (frame == null)
                {
                    // 开始后一直没有任何画面：5 秒还没有就重建一次通道；20 秒还没有提示玩家
                    if (staleSince == DateTime.MinValue) staleSince = DateTime.Now;
                    double wait = (DateTime.Now - staleSince).TotalSeconds;
                    if (wait > 5 && wait < 6) cap.Rebuild();
                    if (wait > 20) Post(new Shown("没拿到画面", "屏幕录制没有画面传过来：请停止后重新打开，录屏弹窗里选“整个屏幕”", null, false, "无画面"));
                }
                else if ((frame.Width > frame.Height) != (sw > sh))
                {
                    // 方向和屏幕对不上：转屏瞬间的残留，等下一帧
                }
                else
                {
                    staleSince = DateTime.MinValue;
                    _lastFrame = frame;
                    // 画面静止时系统不推新帧，这是正常的：照样拿最新一帧反复识别（静止画面正是识别最准的时候）
                    if (AutoSave && isNew && DateTime.Now - _lastAutoSave > TimeSpan.FromSeconds(3)) { _lastAutoSave = DateTime.Now; Task.Run(() => Save(manual: false)); }  // 存图放到后台，不拖慢识别
                    if (frame.Width < frame.Height) Post(new Shown("请横屏", "游戏要横屏玩，求解器才能看清棋盘", null, false, "竖屏"));
                    else if (IsBlack(frame)) Post(new Shown("画面是黑的", "录屏只录到黑屏：请停止后重新打开，录屏弹窗里选“整个屏幕”；若仍是黑屏，可能是手机或游戏禁止录屏", null, false, "黑屏"));
                    else
                    {
                        try
                        {
                            _pipeline.Process(frame);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("tartarus", ex.ToString());
                            Saver.Log(this, "识别出错：" + ex);
                            Post(new Shown("识别出错", ex.GetType().Name + "：" + ex.Message, null, false, "出错"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("tartarus", ex.ToString());
            }
            // 全速：每轮只歇 0.02 秒（画面没变时识别直接沿用上次结果，很快）
            var rest = TimeSpan.FromMilliseconds(20) - (DateTime.Now - started);
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
        }
    }

    private void Save(bool manual)
    {
        var frame = _lastFrame;
        if (frame == null)
        {
            if (manual) _ui.Post(() => _overlay?.Toast("还没拿到画面，存不了图"));
            return;
        }
        _lastAutoSave = DateTime.Now;
        try
        {
            string note = "";
            try
            {
                var scan = _pipeline?.LastScan;
                note = scan == null ? "" : $"{(scan.Ok ? "棋盘" : scan.Pick != null ? "选择" : "无")} {scan.Reason} | {(scan.Ok ? SandboxText.Describe(scan) : "")}";
            }
            catch (Exception) { }
            string name = Saver.SaveFrame(this, frame, manual, note);
            _savedCount++;
            if (manual) _ui.Post(() => _overlay?.Toast("已保存到 下载/TartarusSolver：" + name));
            else if (_savedCount == 1) _ui.Post(() => _overlay?.Toast("自动存图开始了：每 3 秒一张，存在 下载/TartarusSolver"));
        }
        catch (Exception ex)
        {
            Log.Error("tartarus", ex.ToString());
            // 存不了时告诉玩家原因（自动存图每 30 秒最多提示一次）
            if (manual || DateTime.Now - _lastSaveError > TimeSpan.FromSeconds(30))
            {
                _lastSaveError = DateTime.Now;
                string msg = ex.GetType().Name + "：" + ex.Message;
                _ui.Post(() => _overlay?.Toast("存图失败：" + msg));
            }
        }
    }

    /// <summary>抽样看画面是不是几乎全黑（录到的是黑屏）。</summary>
    private static bool IsBlack(Frame f)
    {
        long sum = 0; int n = 0;
        for (int i = 0; i < f.Pixels.Length; i += 4 * 997) { sum += f.Pixels[i] + f.Pixels[i + 1] + f.Pixels[i + 2]; n++; }
        return n > 0 && sum / (3.0 * n) < 4;
    }

    private DateTime _lastSaveError = DateTime.MinValue;
    private int _savedCount;

    private void Post(Shown s) => _ui.Post(() => _overlay?.Update(s));

    public void StopAll()
    {
        _stop = true;
        _ui.Post(() =>
        {
            _overlay?.Hide();
            _overlay = null;
            _capture?.Stop();
            _projection?.Stop();
            _projection = null;
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        });
    }

    public override void OnDestroy()
    {
        _stop = true;
        _overlay?.Hide();
        _capture?.Stop();
        _projection?.Stop();
        Running = null;
        Task.Run(() =>
        {
            _worker?.Join(3000);
            _pipeline?.Dispose();
        });
        base.OnDestroy();
    }

    private sealed class ProjectionStopped(SolverService service) : MediaProjection.Callback
    {
        // 用户在系统里停止了录屏
        public override void OnStop() => service.StopAll();
    }
}
