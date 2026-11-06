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
    private readonly TextView _main, _sub, _autoBtn, _odds, _mode;
    private readonly WindowManagerLayoutParams _lp;
    private bool _shown, _auto;
    // 画框的全屏透明层：不接收触摸，点哪里都直接点到游戏上
    private readonly BoxView _boxes;
    private readonly WindowManagerLayoutParams _boxLp;
    private bool _boxShown;

    public event Action? SaveClicked, StopClicked, Tapped;
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
        // “⋯”后面：推荐这一步后这一局预计的绮彩、至臻（和电脑版浮层上的 ×8.0 一样）
        _odds = Outlined("", 14, Color.Rgb(255, 214, 102), bold: true);
        _odds.SetPadding(Dp(6), 0, 0, 0);
        line.AddView(_odds);
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
        // 菜单展开时下面一行小字：日志读取状态
        _mode = Outlined("", 11, Color.Rgb(200, 204, 212));
        _mode.Visibility = ViewStates.Gone;
        _root.AddView(_mode);

        _lp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.WatchOutsideTouch,
            Format.Translucent)
        {
            // 默认在左下角：左上是游戏的目标和刑具栏。按住红字可以拖到别处
            Gravity = GravityFlags.Bottom | GravityFlags.Start,
            X = Dp(16),
            Y = Dp(8),
        };
        Draggable(_main);
        // 手指碰到屏幕任何别的地方（游戏上）：系统发一个“外面被点了”，提示马上撤下
        _root.Touch += (_, e) =>
        {
            if (e.Event!.Action == MotionEventActions.Outside) { Tapped?.Invoke(); e.Handled = true; }
            else e.Handled = false;
        };

        _boxes = new BoxView(ctx);
        _boxLp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = 0,
            Y = 0,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) _boxLp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
    }

    public void Show(Shown s)
    {
        Update(s);
        if (_shown) return;
        _wm.AddView(_root, _lp);
        _shown = true;
    }

    /// <summary>屏幕实际大小（含状态栏、刘海区域），和录屏画面是同一块。</summary>
    private (int W, int H) RealSize()
    {
        var m = new Android.Util.DisplayMetrics();
        _wm.DefaultDisplay!.GetRealMetrics(m);
        return (m.WidthPixels, m.HeightPixels);
    }

    private void UpdateBoxes(Shown s)
    {
        var (w, h) = RealSize();
        bool any = s.HasHint && s.Boxes.Count > 0 && s.FrameWidth > 0 && (s.FrameWidth > s.FrameHeight) == (w > h);
        if (any)
        {
            _boxes.Set(s.Boxes, (float)w / s.FrameWidth, (float)h / s.FrameHeight);
            _boxLp.Width = w;
            _boxLp.Height = h;
            if (!_boxShown) { _wm.AddView(_boxes, _boxLp); _boxShown = true; }
            else _wm.UpdateViewLayout(_boxes, _boxLp);
        }
        else
        {
            _boxes.Set([], 1, 1);
        }
    }

    public void Hide()
    {
        if (_boxShown) { _wm.RemoveView(_boxes); _boxShown = false; }
        if (!_shown) return;
        _wm.RemoveView(_root);
        _shown = false;
    }

    public void Update(Shown s)
    {
        try { UpdateBoxes(s); } catch (Exception) { }
        // 推荐：只写几号几号（“去13号牢房”→“去13号”）；没有推荐时灰色小字写状态，让人知道它在工作
        // 游戏画面上有标签时，左下角只留“⋯”：画面不乱（2026-11-07）
        bool labels = s.HasHint && s.Boxes.Count > 0;
        _odds.Text = s.HasHint && s.Chromatic is double c ? $"绮彩{c:0.0} 至臻{s.Ultimate ?? 0:0.0}" : "";
        _odds.Visibility = _odds.Text!.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        _main.Visibility = labels ? ViewStates.Gone : ViewStates.Visible;
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
        string sub = labels ? "" : s.HasHint ? (s.Detail.StartsWith("先点") ? s.Detail : "") : (s.Main is "暂无" or "计算中…" or "准备中…" or "识别中…" ? "" : s.Detail);
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

    /// <summary>日志状态，如“日志：已连接（文件）”。</summary>
    public void SetMode(string state)
    {
        _mode.Text = state;
        _mode.SetTextColor(state.StartsWith("日志：已连接") ? Color.Rgb(91, 227, 143) : Color.Rgb(200, 204, 212));
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

/// <summary>
/// 渴瘾那样的提示：目标上方一个粉色圆角小标签（带发光和指向目标的小三角），写“点这张”“吃这个”“去这里”；
/// 吃 / 寻猎接下来要点的牢房用小一号、半透明的“再点这里”。目标太靠上时标签放到目标下面、三角朝上。
/// </summary>
internal sealed class BoxView : View
{
    private readonly Paint _fill, _text, _fillNext, _textNext;
    private readonly float _d;
    private IReadOnlyList<HintBox> _boxes = [];
    private float _sx = 1, _sy = 1;

    public BoxView(Context ctx) : base(ctx)
    {
        _d = ctx.Resources!.DisplayMetrics!.Density;
        SetLayerType(LayerType.Software, null);  // 发光（阴影）要软件绘制
        _fill = new Paint(PaintFlags.AntiAlias) { Color = Color.Rgb(255, 79, 154) };
        _fill.SetShadowLayer(10 * _d, 0, 0, Color.Argb(220, 255, 79, 154));
        _text = new Paint(PaintFlags.AntiAlias) { Color = Color.White, TextSize = 15 * _d, TextAlign = Paint.Align.Center };
        _text.SetTypeface(Typeface.DefaultBold);
        _fillNext = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(200, 255, 79, 154) };
        _fillNext.SetShadowLayer(6 * _d, 0, 0, Color.Argb(160, 255, 79, 154));
        _textNext = new Paint(PaintFlags.AntiAlias) { Color = Color.White, TextSize = 12 * _d, TextAlign = Paint.Align.Center };
        _textNext.SetTypeface(Typeface.DefaultBold);
    }

    public void Set(IReadOnlyList<HintBox> boxes, float sx, float sy)
    {
        _boxes = boxes;
        (_sx, _sy) = (sx, sy);
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        foreach (var b in _boxes)
        {
            var fill = b.Dashed ? _fillNext : _fill;
            var text = b.Dashed ? _textNext : _text;
            float l = b.X0 * _sx, r = b.X1 * _sx, top = b.Y0 * _sy, bottom = b.Y1 * _sy;
            float cx = (l + r) / 2, cy = (top + bottom) / 2;
            float tw = text.MeasureText(b.Label);
            float padX = 10 * _d, h = text.TextSize + 10 * _d, w = tw + 2 * padX, tri = 7 * _d, gap = 2 * _d;
            int side = b.Side;
            if (side == 0 && top - h - tri - gap < 4 * _d) side = 1;   // 上面放不下就放下面
            RectF pill = side switch
            {
                0 => new RectF(cx - w / 2, top - gap - tri - h, cx + w / 2, top - gap - tri),
                1 => new RectF(cx - w / 2, bottom + gap + tri, cx + w / 2, bottom + gap + tri + h),
                2 => new RectF(l - gap - tri - w, cy - h / 2, l - gap - tri, cy + h / 2),
                _ => new RectF(r + gap + tri, cy - h / 2, r + gap + tri + w, cy + h / 2),
            };
            if (side <= 1)
            {
                float shift = Math.Max(0, 4 * _d - pill.Left) - Math.Max(0, pill.Right - (Width - 4 * _d));
                pill.Offset(shift, 0);
            }
            canvas.DrawRoundRect(pill, 6 * _d, 6 * _d, fill);
            using var path = new Android.Graphics.Path();
            switch (side)
            {
                case 0: path.MoveTo(cx - tri, pill.Bottom - 1); path.LineTo(cx + tri, pill.Bottom - 1); path.LineTo(cx, pill.Bottom + tri); break;
                case 1: path.MoveTo(cx - tri, pill.Top + 1); path.LineTo(cx + tri, pill.Top + 1); path.LineTo(cx, pill.Top - tri); break;
                case 2: path.MoveTo(pill.Right - 1, cy - tri); path.LineTo(pill.Right - 1, cy + tri); path.LineTo(pill.Right + tri, cy); break;
                default: path.MoveTo(pill.Left + 1, cy - tri); path.LineTo(pill.Left + 1, cy + tri); path.LineTo(pill.Left - tri, cy); break;
            }
            path.Close();
            canvas.DrawPath(path, fill);
            var fm = text.GetFontMetrics();
            canvas.DrawText(b.Label, pill.CenterX(), pill.CenterY() - (fm!.Ascent + fm.Descent) / 2, text);
        }
    }
}
