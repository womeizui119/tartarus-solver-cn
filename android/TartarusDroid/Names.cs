using Tartarus.App;
using Tartarus.Rules;
using Tartarus.Solver;

namespace TartarusDroid;

/// <summary>走法的中文名（游戏内叫法），与电脑版浮层一致。</summary>
public static class Names
{
    public static string Ranked(Hint hint, ActionValue a) => !hint.ConsumeOnly || a.Action.Kind is ActionKind.Consume or ActionKind.Chain ? Action(a.Action)
        : a.Action.Kind == ActionKind.StopChain ? "取消" : $"取消 → {Action(a.Action)}";

    public static string Action(GameAction a) => a.Kind switch
    {
        ActionKind.Move => a.Arg == GameData.Room ? "去审讯室" : $"去{a.Arg + 1}号牢房",
        ActionKind.Consume or ActionKind.Chain => $"吃：{a.Arg + 1}号牢房",
        ActionKind.StopChain => "取消",
        ActionKind.Replace => "换",
        ActionKind.End => "结束",
        ActionKind.Hunt => $"寻猎：{a.Arg + 1}号牢房",
        ActionKind.Interrogate => a.Arg == 0 ? "煽动" : "赏赐",
        ActionKind.PickInstrument => Zh.Instrument((Instrument)a.Arg),
        ActionKind.Reroll => "重新随机",
        ActionKind.PickTreasure => Zh.Treasure((Treasure)a.Arg),
        ActionKind.Ratway => $"暗渠鼠道：{a.Arg + 1}号牢房",
        _ => a.ToString(),
    };
}
