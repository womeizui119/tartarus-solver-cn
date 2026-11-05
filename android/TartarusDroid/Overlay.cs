using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace TartarusDroid;

/// <summary>
/// 悬在游戏上面的小窗：大绿字写这一步，下面是次选、预计收获、状态，最下面一排按钮。拖动标题栏可以移动。
/// 窗口本身不抢焦点，点小窗以外的地方照常操作游戏。
/// </summary>
public sealed class Overlay
{
    private readonly Context _ctx;
    private readonly IWindowManager _wm;
    private readonly LinearLayout _root;
    private readonly LinearLayout _body;
    private readonly TextView _main, _detail, _extra, _status, _autoBtn, _foldBtn;
    private readonly WindowManagerLayoutParams _lp;
    private bool _shown, _auto, _folded;

    public event Action? SaveClicked, StopClicked;
    public event Action<bool>? AutoSaveChanged;

    public Overlay(Context ctx)
    {
        _ctx = ctx;
        _wm = Android.Runtime.Extensions.JavaCast<IWindowManager>(ctx.GetSystemService(Context.WindowService))!;

        _root = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var bg = new GradientDrawable();
        bg.SetColor(Color.Argb(225, 24, 26, 32));
        bg.SetCornerRadius(Dp(12));
        _root.Background = bg;
        _root.SetPadding(Dp(12), Dp(6), Dp(12), Dp(8));

        // 标题栏（拖动这里移动小窗）
        var bar = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        var title = Text("⠿ 塔罗斯求解器", 12, Color.Rgb(154, 160, 172));
        bar.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _foldBtn = Button("—", () => Fold(!_folded));
        bar.AddView(_foldBtn);
        bar.AddView(Button("■", () => StopClicked?.Invoke()));
        _root.AddView(bar);

        _body = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        _main = Text("准备中…", 24, Color.Rgb(91, 227, 143), bold: true);
        _detail = Text("", 13, Color.Rgb(200, 204, 212));
        _extra = Text("", 12, Color.Rgb(230, 180, 110));
        _status = Text("", 11, Color.Rgb(111, 117, 128));
        _body.AddView(_main);
        _body.AddView(_detail);
        _body.AddView(_extra);
        _body.AddView(_status);
        var buttons = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        buttons.SetPadding(0, Dp(4), 0, 0);
        buttons.AddView(Button("存图", () => SaveClicked?.Invoke()));
        _autoBtn = Button("自动存图：关", () =>
        {
            _auto = !_auto;
            _autoBtn!.Text = _auto ? "自动存图：开" : "自动存图：关";
            _autoBtn.SetTextColor(_auto ? Color.Rgb(91, 227, 143) : Color.Rgb(232, 232, 236));
            AutoSaveChanged?.Invoke(_auto);
        });
        buttons.AddView(_autoBtn);
        _body.AddView(buttons);
        _root.AddView(_body);

        _lp = new WindowManagerLayoutParams(
            Dp(300), ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Dp(16),
            Y = Dp(16),
        };
        Draggable(bar);
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
        _main.Text = s.Main;
        _main.SetTextColor(s.HasHint ? Color.Rgb(255, 0, 0) : Color.Rgb(154, 160, 172));  // 推荐用正红色，和电脑版一致
        _detail.Text = s.Detail;
        _extra.Text = s.Extra ?? "";
        _extra.Visibility = s.Extra != null ? ViewStates.Visible : ViewStates.Gone;
        _status.Text = s.HasHint ? s.Status : "";
        _status.Visibility = s.HasHint ? ViewStates.Visible : ViewStates.Gone;
        // 收起时标题栏显示推荐，不挡游戏
        if (_folded) _main.Visibility = ViewStates.Gone;
    }

    public void Toast(string text) => Android.Widget.Toast.MakeText(_ctx, text, ToastLength.Short)!.Show();

    private void Fold(bool folded)
    {
        _folded = folded;
        _body.Visibility = folded ? ViewStates.Gone : ViewStates.Visible;
        _foldBtn.Text = folded ? "＋" : "—";
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
                    _lp.Y = startY + (int)(e.Event.RawY - downY);
                    if (_shown) _wm.UpdateViewLayout(_root, _lp);
                    break;
            }
            e.Handled = true;
        };
    }

    private TextView Text(string s, float sp, Color color, bool bold = false)
    {
        var t = new TextView(_ctx) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color);
        if (bold) t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        return t;
    }

    private TextView Button(string s, Action click)
    {
        var b = Text(s, 13, Color.Rgb(232, 232, 236));
        b.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        var bg = new GradientDrawable();
        bg.SetColor(Color.Argb(255, 50, 53, 62));
        bg.SetCornerRadius(Dp(6));
        b.Background = bg;
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = Dp(6) };
        b.LayoutParameters = lp;
        b.Click += (_, _) => click();
        return b;
    }

    private int Dp(float dp) => (int)(dp * _ctx.Resources!.DisplayMetrics!.Density + 0.5f);
}
