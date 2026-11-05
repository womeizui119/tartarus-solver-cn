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
    private VirtualDisplay? _display;
    private ImageReader? _reader;
    private int _w, _h;
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
        _logReader = new LogReader(this);
        _logReader.StateChanged += s => _ui.Post(() => _overlay?.SetMode(s));
        _overlay.SetMode(_logReader.State);

        CreateDisplay();
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
            .SetContentTitle("塔罗斯求解器正在看游戏画面")
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

    /// <summary>录屏画面的大小（横竖屏会变）；长边最多 1600：识别不看分辨率，画面小一半，内存和耗时都省很多
    /// （2026-11-05：2868×1320 的手机按原尺寸录，每张 15MB，一秒四张，几步后被系统杀掉）。</summary>
    private (int W, int H, int Dpi) ScreenSize()
    {
        var wm = Android.Runtime.Extensions.JavaCast<IWindowManager>(GetSystemService(WindowService))!;
        var m = new DisplayMetrics();
        wm.DefaultDisplay!.GetRealMetrics(m);
        int w = m.WidthPixels, h = m.HeightPixels;
        double k = Math.Min(1.0, 1600.0 / Math.Max(w, h));
        return ((int)(w * k) & ~1, (int)(h * k) & ~1, Math.Max(120, (int)((int)m.DensityDpi * k)));
    }

    private void CreateDisplay()
    {
        var (w, h, dpi) = ScreenSize();
        Saver.Log(this, $"录屏画面 {w}×{h}");
        (_w, _h) = (w, h);
        _reader = ImageReader.NewInstance(w, h, (ImageFormatType)1 /* RGBA_8888 */, 2);
        _display = _projection!.CreateVirtualDisplay("tartarus", w, h, dpi, (DisplayFlags)16 /* VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR：镜像整个屏幕 */, _reader.Surface, null, null);
    }

    /// <summary>转屏后跟着改大小（同一个授权只能建一次虚拟屏幕，所以改大小而不是重建）。</summary>
    private void FollowRotation()
    {
        var (w, h, dpi) = ScreenSize();
        if (w == _w && h == _h) return;
        var old = _reader;
        _reader = ImageReader.NewInstance(w, h, (ImageFormatType)1, 2);
        _display!.Resize(w, h, dpi);
        _display.Surface = _reader.Surface;
        (_w, _h) = (w, h);
        old?.Close();
    }

    private void Work()
    {
        try
        {
            ModelSetup.Prepare(this);
            Post(new Shown("准备中…", "正在加载识别和推算模型…", null, false, "准备中…"));
            _pipeline = new Pipeline(() => _logReader?.Live);
            _pipeline.Changed += Post;
            Post(new Shown("暂无", "进入塔罗斯监狱后，这里会写出该怎么走", null, false, "就绪"));
        }
        catch (Exception ex)
        {
            Post(new Shown("启动失败", ex.Message, null, false, "启动失败"));
            Log.Error("tartarus", ex.ToString());
            return;
        }
        while (!_stop)
        {
            var started = DateTime.Now;
            try
            {
                FollowRotation();
                var frame = Grab();
                if (frame != null)
                {
                    _lastFrame = frame;
                    _lastFrameAt = DateTime.Now;
                    // 先存图再识别：识别出错也照样存（出问题的画面正是最需要的）
                    if (AutoSave && DateTime.Now - _lastAutoSave > TimeSpan.FromSeconds(3)) Save(manual: false);
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
                else if (DateTime.Now - _lastFrameAt > TimeSpan.FromSeconds(5))
                {
                    Post(new Shown("没拿到画面", "屏幕录制没有画面传过来：请停止后重新打开，并在弹窗里选“整个屏幕”", null, false, "无画面"));
                    _lastFrameAt = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Log.Error("tartarus", ex.ToString());
            }
            // 大约每 0.25 秒看一次（识别本身约 0.3 秒，基本是连着看），推荐出得快
            var rest = TimeSpan.FromMilliseconds(250) - (DateTime.Now - started);
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
        }
    }

    /// <summary>取最新一帧，按行拷出来（每行末尾可能有对齐用的空字节）。</summary>
    private Frame? Grab()
    {
        using var image = _reader?.AcquireLatestImage();
        if (image == null) return null;
        var plane = image.GetPlanes()![0];
        var buffer = plane.Buffer!;
        int w = image.Width, h = image.Height, rowStride = plane.RowStride, pixelStride = plane.PixelStride;
        var pixels = new byte[w * h * 4];
        var row = new byte[rowStride];
        buffer.Rewind();
        for (int y = 0; y < h; y++)
        {
            int len = Math.Min(rowStride, buffer.Remaining());
            buffer.Get(row, 0, len);
            if (pixelStride == 4) Buffer.BlockCopy(row, 0, pixels, y * w * 4, w * 4);
            else for (int x = 0; x < w; x++) Buffer.BlockCopy(row, x * pixelStride, pixels, (y * w + x) * 4, 4);
        }
        return new Frame(w, h, pixels, Tartarus.Vision.PixelFormat.Rgba32);
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

    private LogReader? _logReader;
    private DateTime _lastFrameAt = DateTime.Now, _lastSaveError = DateTime.MinValue;
    private int _savedCount;

    private void Post(Shown s) => _ui.Post(() => _overlay?.Update(s));

    public void StopAll()
    {
        _stop = true;
        _logReader?.Dispose();
        _ui.Post(() =>
        {
            _overlay?.Hide();
            _overlay = null;
            _display?.Release();
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
        _display?.Release();
        _projection?.Stop();
        _reader?.Close();
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
