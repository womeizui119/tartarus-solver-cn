using Android.Content;
using Android.Hardware.Display;
using Android.Graphics;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using Android.Views;
using Tartarus.Vision;

namespace TartarusDroid;

/// <summary>
/// 录屏取画面。只走一种模式：系统有新帧时回调通知（ImageReader.OnImageAvailable），回调里立刻取出、拷走、关掉。
/// 这样缓冲槽位永远不会积压，也就不存在“槽位占满后系统不再推帧”的死局
/// （2026-11-06 荣耀实机记录：主动轮询 + 异常路径没关帧 → maxImages 占满 → 不切屏永远拿不到新画面，从第一版起就是这个根源）。
/// 主循环只通过 <see cref="Latest"/> 取最新一帧的拷贝，不碰 ImageReader。
/// 转屏、断流自愈也都在这里：画面方向变了就重建；超过一定时间没有新帧（画面静止时属正常）也不再误判为断流，
/// 只有“方向对不上”或“系统明确报错”才重建。
/// </summary>
public sealed class Capture : Java.Lang.Object, ImageReader.IOnImageAvailableListener
{
    private readonly Context _ctx;
    private readonly MediaProjection _projection;
    private readonly Action<string> _log;
    private readonly HandlerThread _thread;
    private readonly Handler _handler;
    private readonly object _gate = new();

    private ImageReader? _reader;
    private VirtualDisplay? _display;
    private int _w, _h;
    private Frame? _latest;
    private DateTime _latestAt = DateTime.MinValue;
    private long _frames, _errors;
    private DateTime _lastErrorLog = DateTime.MinValue;

    public Capture(Context ctx, MediaProjection projection, Action<string> log)
    {
        _ctx = ctx;
        _projection = projection;
        _log = log;
        _thread = new HandlerThread("capture");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);
        Build();
    }

    /// <summary>当前屏幕尺寸（横竖屏会变）；长边最多 1920。
    /// （2026-11-06 小米实机：1280 宽时选目的地的卡字太小，“去3号”和被选中的那张常读不出来，推荐在“去5号”和“去审讯室”间反复；
    /// 1600 以上三张都读得出，识别只慢三成左右）</summary>
    private (int W, int H, int Dpi) ScreenSize()
    {
        var wm = Android.Runtime.Extensions.JavaCast<IWindowManager>(_ctx.GetSystemService(Context.WindowService))!;
        var m = new DisplayMetrics();
        wm.DefaultDisplay!.GetRealMetrics(m);
        int w = m.WidthPixels, h = m.HeightPixels;
        double k = Math.Min(1.0, 1920.0 / Math.Max(w, h));
        return ((int)(w * k) & ~1, (int)(h * k) & ~1, Math.Max(120, (int)((int)m.DensityDpi * k)));
    }

    private void Build()
    {
        var (w, h, dpi) = ScreenSize();
        lock (_gate)
        {
            var oldReader = _reader;
            // 3 张就够：回调里立刻取走，同一时刻最多持有 1 张；多给一张冗余
            _reader = ImageReader.NewInstance(w, h, (ImageFormatType)1 /* RGBA_8888 */, 3);
            _reader.SetOnImageAvailableListener(this, _handler);
            if (_display == null)
                _display = _projection.CreateVirtualDisplay("tartarus", w, h, dpi, (DisplayFlags)16 /* AUTO_MIRROR */, _reader.Surface, null, null);
            else
            {
                _display.Resize(w, h, dpi);
                _display.Surface = _reader.Surface;
            }
            (_w, _h) = (w, h);
            _latest = null;
            oldReader?.Close();
        }
        _log($"录屏画面 {w}×{h}");
    }

    /// <summary>屏幕方向或尺寸变了就重建。返回 true 表示刚重建过（上一帧作废）。</summary>
    public bool FollowRotation()
    {
        var (w, h, _) = ScreenSize();
        if (w == _w && h == _h) return false;
        _log($"转屏：录屏画面 {_w}×{_h} → {w}×{h}");
        Build();
        return true;
    }

    /// <summary>系统有新帧：取出、拷走、关掉，一步不留。</summary>
    public void OnImageAvailable(ImageReader? reader)
    {
        Android.Media.Image? image = null;
        try
        {
            image = reader?.AcquireLatestImage();
            if (image == null) return;
            var plane = image.GetPlanes()![0];
            var buffer = plane.Buffer!;
            int w = image.Width, h = image.Height, rowStride = plane.RowStride, pixelStride = plane.PixelStride;
            var pixels = new byte[w * h * 4];
            var row = new byte[rowStride];
            buffer.Rewind();
            for (int y = 0; y < h; y++)
            {
                int len = Math.Min(rowStride, buffer.Remaining());
                if (len <= 0) break;
                buffer.Get(row, 0, len);
                if (pixelStride == 4) Buffer.BlockCopy(row, 0, pixels, y * w * 4, w * 4);
                else for (int x = 0; x < w; x++) Buffer.BlockCopy(row, x * pixelStride, pixels, (y * w + x) * 4, 4);
            }
            var frame = new Frame(w, h, pixels, Tartarus.Vision.PixelFormat.Rgba32);
            lock (_gate)
            {
                _latest = frame;
                _latestAt = DateTime.Now;
                _frames++;
            }
        }
        catch (Exception ex)
        {
            _errors++;
            if (DateTime.Now - _lastErrorLog > TimeSpan.FromSeconds(5))
            {
                _lastErrorLog = DateTime.Now;
                _log("取帧出错：" + ex.GetType().Name + " " + ex.Message);
            }
        }
        finally
        {
            try { image?.Close(); } catch (Exception) { }
        }
    }

    /// <summary>最新一帧（拷贝），以及它是多久前到的。没有帧时返回 null。</summary>
    public (Frame? Frame, double AgeSeconds, bool IsNew) Latest(ref long seen)
    {
        lock (_gate)
        {
            if (_latest == null) return (null, double.MaxValue, false);
            bool isNew = _frames != seen;
            seen = _frames;
            return (_latest, (DateTime.Now - _latestAt).TotalSeconds, isNew);
        }
    }

    public long Frames { get { lock (_gate) return _frames; } }
    public long Errors => _errors;
    public (int W, int H) Size { get { lock (_gate) return (_w, _h); } }

    /// <summary>外部要求重建（比如很久没新帧且怀疑断流）。</summary>
    public void Rebuild()
    {
        _log("重建录屏通道");
        Build();
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _display?.Release(); } catch (Exception) { }
            try { _reader?.Close(); } catch (Exception) { }
            _display = null;
            _reader = null;
        }
        _thread.QuitSafely();
    }
}
