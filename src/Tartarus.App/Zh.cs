using Tartarus.Rules;

namespace Tartarus.App;

/// <summary>
/// 中文版：界面上显示的游戏术语，取自游戏自带的简体中文文本（models/text/tartarus_text.json 的 zhCN），
/// 与国服游戏内叫法一致。日志与错误报告仍用英文，便于与原版对照排查。
/// </summary>
public static class Zh
{
    public const string AppName = "塔罗斯监狱求解器";

    private static readonly Dictionary<string, string> Instruments = new()
    {
        ["muzzle"] = "狂兽的剧痛口枷",
        ["chains"] = "凶兽的饥饿绞链",
        ["collar"] = "疯兽的倒刺项圈",
        ["casket"] = "复仇者之匣",
        ["cage"] = "拥挤兽笼",
        ["whip"] = "棘刺铁鞭",
        ["saw"] = "锈锯",
        ["blacklist"] = "执刑官的黑名册",
        ["keys"] = "遗落的钥匙串",
        ["coin"] = "手足的命运硬币",
        ["brand"] = "灼痛的烙铁",
        ["dogtag"] = "重刑铭牌",
        ["forged"] = "伪造钥匙",
        ["hook"] = "挑拣钩",
        ["scapegoat"] = "替罪人的断头台",
        ["axe"] = "狱卒的行刑巨斧",
        ["maiden"] = "切开的人形棺",
        ["scales"] = "典狱官的天平",
        ["noose"] = "绞首绳",
        ["stake"] = "火刑柱",
        ["pestle"] = "铁杵",
        ["shortchain"] = "短链脚镣",
        ["shackles"] = "长链脚镣",
        ["tongs"] = "恶欲铁钳：巨象",
        ["maul"] = "象脚重锤：巨象",
        ["trap"] = "染红的捕兽夹：狂狮",
        ["crown"] = "牢中王的铁冠：狂狮",
        ["claws"] = "脏污铁爪：头狼",
        ["guillotine"] = "狼牙铡刀：头狼",
        ["ironbox"] = "蠕动铁盒：硕鼠",
        ["skewer"] = "鼠头串：硕鼠",
    };

    private static readonly Dictionary<string, string> Themes = new()
    {
        ["tyranny"] = "滥权",
        ["opulence"] = "奢靡",
        ["ironlaw"] = "铁律",
        ["cagefight"] = "囚徒困斗",
    };

    // 号角：文本表里是“升阶奖励”，游戏卡牌上实际显示“斗争铜号”。
    private static readonly Dictionary<string, string> Treasures = new()
    {
        ["ring"] = "三首扳指",
        ["horn"] = "斗争铜号",
        ["vintage"] = "赤色佳酿",
        ["sin"] = "原罪烙印",
    };

    public static string Instrument(string? id) => id != null && Instruments.TryGetValue(id, out var n) ? n : id ?? "?";
    public static string Instrument(Instrument i) => Instrument(GameData.Info(i).Id);
    /// <summary>两行显示：名字与所属代号分开。</summary>
    public static string InstrumentCaption(string id) => Instrument(id).Replace("：", "\n", StringComparison.Ordinal);
    public static string Theme(string? id) => id != null && Themes.TryGetValue(id, out var n) ? n : id ?? "?";
    public static string Treasure(string? id) => id != null && Treasures.TryGetValue(id, out var n) ? n : id ?? "?";

    public static string Treasure(Tartarus.Rules.Treasure t) => t switch
    {
        Tartarus.Rules.Treasure.ThumbRing => Treasures["ring"],
        Tartarus.Rules.Treasure.BrassHorn => Treasures["horn"],
        Tartarus.Rules.Treasure.CrimsonVintage => Treasures["vintage"],
        Tartarus.Rules.Treasure.BrandOfSin => Treasures["sin"],
        _ => t.ToString(),
    };

    /// <summary>选择界面（刑具或典狱长的珍藏）上一张卡的名字。</summary>
    public static string PickName(Vision.PickRead pick, string id) => pick.Kind == Vision.PickRead.Treasure ? Treasure(id) : Instrument(id);

    public static string Code(Vision.Code? code) => code switch
    {
        Vision.Code.Rat => "硕鼠",
        Vision.Code.Wolf => "头狼",
        Vision.Code.Lion => "狂狮",
        Vision.Code.Mammoth => "巨象",
        _ => "?",
    };

    public static string Rarity(Vision.Rarity r) => RarityName((int)r);
    public static string Rarity(Tartarus.Rules.Rarity r) => RarityName((int)r);

    private static string RarityName(int r) => r switch
    {
        0 => "普通",
        1 => "魔法",
        2 => "稀有",
        3 => "传奇",
        4 => "至臻",
        5 => "绮彩",
        _ => "?",
    };

    /// <summary>游戏语言列表里的名字（中文），各语言的原名附在后面。</summary>
    public static string LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        "en" => "英语",
        "ru" => "俄语",
        "de" => "德语",
        "es" => "西班牙语",
        "fr" => "法语",
        "pt" => "葡萄牙语",
        "id" => "印尼语",
        "vi" => "越南语",
        "ja" => "日语",
        "ko" => "韩语",
        "th" => "泰语",
        "zh" or "zhcn" => "简体中文",
        "zhtw" => "繁体中文",
        _ => code ?? "?",
    };

    public static string LanguageItem(string code)
    {
        string zh = LanguageName(code), native = Vision.Ocr.OcrPacks.NativeName(code);
        return zh == native ? zh : $"{zh} · {native}";
    }

    /// <summary>识别模块给出的“没有棋盘”原因（英文）换成中文。</summary>
    public static string Reason(string? reason) => reason switch
    {
        null or "" => "未知原因",
        "Run theme." => "本局主题画面",
        "Select a Torture Instrument." => "选择刑具画面",
        "Warden's Treasure." => "典狱长的珍藏画面",
        "No Tartarus Sandbox on screen." => "屏幕上没有塔罗斯监狱棋盘",
        "warm-up" => "正在预热",
        "empty result" => "识别结果为空",
        _ when reason.StartsWith("Scanner failed to start: ", StringComparison.Ordinal) => "识别器启动失败：" + reason["Scanner failed to start: ".Length..],
        _ => reason,
    };
}
