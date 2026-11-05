using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media.Projection;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace TartarusDroid;

/// <summary>主界面：两步授权（悬浮窗、录屏），然后开始。之后切到游戏即可。</summary>
[Activity(Label = "塔罗斯求解器", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait)]
public class MainActivity : Activity
{
    private const int CaptureRequest = 7;
    private TextView? _overlayState;
    private Button? _start;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(24), Dp(32), Dp(24), Dp(24));
        root.SetBackgroundColor(Color.Rgb(30, 31, 36));

        root.AddView(Label("塔罗斯监狱求解器", 24, Color.Rgb(232, 232, 236), bold: true));
        root.AddView(Label("安卓浮窗版 0.1（测试版）", 13, Color.Rgb(154, 160, 172)));
        root.AddView(Label("\n在游戏上面开一个小窗，写出塔罗斯监狱这一步怎么走。只看画面，不碰游戏操作。\n", 15, Color.Rgb(200, 204, 212)));

        root.AddView(Label("第 1 步：允许显示在其他应用上层", 16, Color.Rgb(232, 232, 236), bold: true));
        _overlayState = Label("", 13, Color.Rgb(154, 160, 172));
        root.AddView(_overlayState);
        var overlay = new Button(this) { Text = "去设置" };
        overlay.Click += (_, _) => StartActivity(new Intent(Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse("package:" + PackageName)));
        root.AddView(overlay);

        root.AddView(Label("\n第 2 步：开始", 16, Color.Rgb(232, 232, 236), bold: true));
        root.AddView(Label("点下面的按钮，系统会问“是否允许录制屏幕”，选“整个屏幕”并允许。然后切到游戏，横屏进入塔罗斯监狱。", 13, Color.Rgb(154, 160, 172)));
        _start = new Button(this) { Text = "开始" };
        _start.Click += (_, _) => Start();
        root.AddView(_start);

        root.AddView(Label("\n测试时请打开小窗上的“自动存图”，玩完一局后把手机“下载/TartarusSolver”文件夹里的图片和 记录.txt 发过来。", 13, Color.Rgb(230, 180, 110)));

        SetContentView(new ScrollView(this) { }.Also(s => s.AddView(root)));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            RequestPermissions(["android.permission.POST_NOTIFICATIONS"], 1);
    }

    protected override void OnResume()
    {
        base.OnResume();
        bool can = Settings.CanDrawOverlays(this);
        _overlayState!.Text = can ? "✓ 已允许" : "还没允许：点“去设置”，打开“塔罗斯求解器”的开关";
        _overlayState.SetTextColor(can ? Color.Rgb(91, 227, 143) : Color.Rgb(230, 180, 110));
        _start!.Text = SolverService.Running != null ? "正在运行（到游戏里看小窗）" : "开始";
    }

    private void Start()
    {
        if (!Settings.CanDrawOverlays(this))
        {
            Toast.MakeText(this, "请先完成第 1 步", ToastLength.Short)!.Show();
            return;
        }
        if (SolverService.Running != null)
        {
            Toast.MakeText(this, "已经在运行了，切到游戏即可", ToastLength.Short)!.Show();
            return;
        }
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), CaptureRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CaptureRequest) return;
        if (resultCode != Result.Ok || data == null)
        {
            Toast.MakeText(this, "没有允许录屏，求解器看不到游戏画面", ToastLength.Long)!.Show();
            return;
        }
        var intent = new Intent(this, typeof(SolverService));
        intent.PutExtra(SolverService.ExtraCode, (int)resultCode);
        intent.PutExtra(SolverService.ExtraData, data);
        StartForegroundService(intent);
        Toast.MakeText(this, "已开始，切到游戏吧", ToastLength.Short)!.Show();
        MoveTaskToBack(true);
    }

    private TextView Label(string s, float sp, Color color, bool bold = false)
    {
        var t = new TextView(this) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color);
        if (bold) t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        return t;
    }

    private int Dp(float dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);
}

internal static class Extensions
{
    public static T Also<T>(this T t, Action<T> a)
    {
        a(t);
        return t;
    }
}
