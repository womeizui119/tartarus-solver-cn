using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace TartarusDroid;

/// <summary>
/// 悬在游戏上面的极简小窗：没有底框，只有一行带黑描边的红字写这一步（“去13号”“吃9号”“换”），
/// “先点卡牌再点牢房”这类必须的提示用一行小字；按钮收在右边的“⋯”里，点一下才展开。
/// 按住红字可以拖动位置。窗口本身不抢焦点，点小窗以外的地方照常操作游戏。
/// </summary>
public sealed class Overlay
{
    private readonly Context _ctx;
    private readonly IWindowManager _wm;
    private readonly LinearLayout _root, _menu;
    private readonly TextView _main, _sub, _autoBtn, _mode;
    private readonly WindowManagerLayoutParams _lp;
    private bool _shown, _auto;

    public event Action? SaveClicked, StopClicked;
    public event Action<bool>? AutoSaveChanged;

    public Overlay(Context ctx)
    {
        _ctx = ctx;
        _wm = Android.Runtime.Extensions.JavaCast<IWindowManager>(ctx.GetSystemService(Context.WindowService))!;

        _root = new LinearLayout(ctx) { Orientation = Orientation.Vertical };

        var line = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        line.SetGravity(GravityFlags.CenterVertical);
        _main = Outlined("准备中…", 24, Color.Rgb(154, 160, 172), bold: true);
        line.AddView(_main);
        var more = Outlined("  ⋯", 22, Color.Argb(200, 232, 232, 236), bold: true);
        more.SetPadding(Dp(4), 0, Dp(4), 0);
        more.Click += (_, _) => ToggleMenu();
        line.AddView(more);
        _root.AddView(line);

        _sub = Outlined("", 12, Color.Rgb(240, 240, 240));
        _sub.Visibility = ViewStates.Gone;
        _root.AddView(_sub);

        // 菜单：默认收起
        _menu = new LinearLayout(ctx) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        _menu.SetPadding(0, Dp(4), 0, 0);
        _menu.AddView(Button("存图", () => SaveClicked?.Invoke()));
        _autoBtn = Button("自动存图：关", () =>
        {
            _auto = !_auto;
            _autoBtn!.Text = _auto ? "自动存图：开" : "自动存图：关";
            _autoBtn.SetTextColor(_auto ? Color.Rgb(91, 227, 143) : Color.Rgb(232, 232, 236));
            AutoSaveChanged?.Invoke(_auto);
        });
        _menu.AddView(_autoBtn);
        _menu.AddView(Button("停止", () => StopClicked?.Invoke()));
        _root.AddView(_menu);
        // 菜单展开时下面一行小字：日志读取状态（读到日志更准更快）
        _mode = Outlined("", 11, Color.Rgb(200, 204, 212));
        _mode.Visibility = ViewStates.Gone;
        _root.AddView(_mode);

        _lp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            // 默认在左下角：左上是游戏的目标和刑具栏。按住红字可以拖到别处
            Gravity = GravityFlags.Bottom | GravityFlags.Start,
            X = Dp(16),
            Y = Dp(8),
        };
        Draggable(_main);
    }

    public void Show(Shown s)
    {
        Update(s);
        if (_shown) return;
        _wm.AddView(_root, _lp);
        _shown = true;
    }

    public void Hide()
    {
        if (!_shown) return;
        _wm.RemoveView(_root);
        _shown = false;
    }

    public void Update(Shown s)
    {
        // 推荐：只写几号几号（“去13号牢房”→“去13号”）；没有推荐时灰色小字写状态，让人知道它在工作
        if (s.HasHint)
        {
            _main.Text = Short(s.Main);
            _main.SetTextColor(Color.Rgb(255, 0, 0));
            _main.SetTextSize(Android.Util.ComplexUnitType.Sp, 26);
        }
        else
        {
            _main.Text = s.Main;
            _main.SetTextColor(Color.Rgb(190, 194, 202));
            _main.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        }
        // 小字只在必须时出现：吃/寻猎要点两下、出错、黑屏这类
        string sub = s.HasHint ? (s.Detail.StartsWith("先点") ? s.Detail : "") : (s.Main is "暂无" or "计算中…" or "准备中…" or "识别中…" ? "" : s.Detail);
        _sub.Text = sub;
        _sub.Visibility = sub.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private static string Short(string s) => s.Replace("号牢房", "号").Replace("：", "");

    public void Toast(string text) => Android.Widget.Toast.MakeText(_ctx, text, ToastLength.Short)!.Show();

    private void ToggleMenu()
    {
        bool open = _menu.Visibility != ViewStates.Visible;
        _menu.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        _mode.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>日志状态，如“日志：已连接”。</summary>
    public void SetMode(string state)
    {
        _mode.Text = state;
        _mode.SetTextColor(state == "日志：已连接" ? Color.Rgb(91, 227, 143) : Color.Rgb(200, 204, 212));
    }

    private void Draggable(View handle)
    {
        float downX = 0, downY = 0;
        int startX = 0, startY = 0;
        handle.Touch += (_, e) =>
        {
            switch (e.Event!.Action)
            {
                case MotionEventActions.Down:
                    (downX, downY, startX, startY) = (e.Event.RawX, e.Event.RawY, _lp.X, _lp.Y);
                    break;
                case MotionEventActions.Move:
                    _lp.X = startX + (int)(e.Event.RawX - downX);
                    _lp.Y = startY - (int)(e.Event.RawY - downY);  // 靠底部摆放：Y 是离底边的距离
                    if (_shown) _wm.UpdateViewLayout(_root, _lp);
                    break;
            }
            e.Handled = true;
        };
    }

    /// <summary>带黑色描边阴影的字：没有底框也能在任何画面上看清。</summary>
    private TextView Outlined(string s, float sp, Color color, bool bold = false)
    {
        var t = new TextView(_ctx) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color);
        t.SetShadowLayer(Dp(3), 0, 0, Color.Black);
        if (bold) t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        return t;
    }

    private TextView Button(string s, Action click)
    {
        var b = new TextView(_ctx) { Text = s };
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        b.SetTextColor(Color.Rgb(232, 232, 236));
        b.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        var bg = new GradientDrawable();
        bg.SetColor(Color.Argb(200, 40, 42, 50));
        bg.SetCornerRadius(Dp(6));
        b.Background = bg;
        b.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = Dp(6) };
        b.Click += (_, _) => click();
        return b;
    }

    private int Dp(float dp) => (int)(dp * _ctx.Resources!.DisplayMetrics!.Density + 0.5f);
}
