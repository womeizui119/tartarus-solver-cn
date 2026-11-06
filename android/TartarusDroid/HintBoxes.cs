using Tartarus.App;
using Tartarus.Rules;
using Tartarus.Solver;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace TartarusDroid;

/// <summary>画在游戏画面上的一个框（录屏画面的像素坐标）：实线是这一步要点的，虚线是点完卡牌后要点的牢房。</summary>
public readonly record struct HintBox(float X0, float Y0, float X1, float Y1, bool Dashed);

/// <summary>
/// 和电脑版一样把推荐框出来（电脑版 SandboxOverlayView.Hints 的同一套规则，搬到手机）：
/// 选目的地框那张卡、做决策框那张卡（吃 / 换 / 结束 / 煽动 / 赏赐…），选吃的目标框那个牢房，
/// 推荐“吃 / 寻猎”时再用虚线框出接下来要点的牢房；选刑具、典狱长珍藏框那张卡或“重新随机”。
/// </summary>
public static class HintBoxes
{
    /// <param name="rightShift">靠右对齐的按钮（“取消”）在手机宽屏上往左挪的画布单位（和绮彩栏一样）。</param>
    public static List<HintBox> For(ScanResult scan, Hint hint, double rightShift)
    {
        var boxes = new List<HintBox>();
        if (scan.Layout is not { } layout || hint.Ranked.Count == 0) return boxes;
        HintBox Px(CanvasRect r, bool dashed = false)
        {
            var b = layout.ToPixels(r);
            return new HintBox((float)b.X0, (float)b.Y0, (float)(b.X0 + b.Width), (float)(b.Y0 + b.Height), dashed);
        }

        if (scan.Pick is { } pick)
        {
            var best = hint.Ranked[0].Action;
            bool treasure = pick.Kind == PickRead.Treasure;
            if (best.Kind == ActionKind.Reroll) boxes.Add(Px(ScreenLayout.PickRerollButton));
            else
                foreach (var card in pick.Cards)
                {
                    if (card.Id == null) continue;
                    GameAction? act = treasure
                        ? GameObservation.Treasure(card.Id) is { } t ? new GameAction(ActionKind.PickTreasure, (byte)t) : null
                        : GameData.InstrumentById(card.Id) is { } i ? new GameAction(ActionKind.PickInstrument, (byte)i) : null;
                    if (act == best) boxes.Add(Px(treasure ? ScreenLayout.TreasureCard(card.X) : ScreenLayout.PickCard(card.Index)));
                }
            return boxes;
        }

        if (scan.Cells is not { } cells) return boxes;
        HintBox? Cell(int n, bool dashed = false)
        {
            if (cells.FirstOrDefault(c => c.N == n) is not { } c) return null;
            var (x0, y0, x1, y1) = SandboxGeometry.FrameBox(c.X, c.Y, c.K);
            return new HintBox((float)x0, (float)y0, (float)x1, (float)y1, dashed);
        }
        HintBox? Card(Func<CardText, bool> which) =>
            scan.Text?.Cards.FirstOrDefault(which) is { } card
                ? Px(ScreenLayout.CardAt((card.Line.Box.CenterX - layout.Width / 2.0) / layout.K)) : null;
        HintBox? Word(string w) => Card(t => t.Meaning.Contains(w));

        var a = hint.Ranked[0].Action;
        HintBox? main;
        if (hint.HuntOnly) main = a.Kind == ActionKind.Hunt ? Cell(a.Arg) : null;
        else if (hint.ConsumeOnly)
            main = a.Kind is ActionKind.Consume or ActionKind.Chain ? Cell(a.Arg) : Px(ScreenLayout.ConsumeCancel.Offset(-rightShift, 0));
        else
            main = a.Kind switch
            {
                ActionKind.Move => Card(t => t.DestinationCell == a.Arg + 1) ?? Cell(a.Arg),
                ActionKind.Ratway => Cell(a.Arg),
                ActionKind.Consume or ActionKind.Chain => Word("consume"),
                ActionKind.Hunt => Word("hunt"),
                ActionKind.Replace => Word("replace"),
                ActionKind.End or ActionKind.StopChain => Word("end"),
                ActionKind.Interrogate => Word(a.Arg == 0 ? "instigate" : "bestow"),
                _ => null,
            };
        if (main is { } m) boxes.Add(m);
        // 推荐“吃 / 寻猎”：点完卡牌要点的那个牢房，先用虚线框出来
        if (!hint.ConsumeOnly && !hint.HuntOnly && a.Kind is ActionKind.Consume or ActionKind.Chain or ActionKind.Hunt && Cell(a.Arg, dashed: true) is { } target)
            boxes.Add(target);
        return boxes;
    }
}
