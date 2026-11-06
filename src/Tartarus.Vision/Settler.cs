namespace Tartarus.Vision;

/// <summary>
/// 中文版（手机）：认定“现在是哪个局面”。实时录屏每帧都有读得不一样的地方（卡牌字、稀有度、进度、守卫标记），
/// 以前每变一下就重算、撤下提示，推荐一直闪、一直“计算中”（2026-11-06 两台实机）。这里的规则：
/// <list type="bullet">
/// <item>还没认定任何局面：同一个读数（或只差一项的读数）在最近 <see cref="Window"/> 次里出现 2 次就认定。</item>
/// <item>已经认定了 C：读数和 C 只差一项，就当还是 C（读错一处不算变）。键里“§”之后的部分（卡牌、画面状态）不算“一项”，
/// 不同就是不同的局面。</item>
/// <item>只差一项的变化，要在最近 20 次里占到 15 次才算真变。</item>
/// <item>换成新局面 K，要同时满足：K 在最近几次里占了 <see cref="Need"/> 次，而且 C 只剩不到 2 次。
/// 两个读数来回跳（A、B、A、B）时两边都凑不够，就一直停在原来的局面上，不闪。</item>
/// </list>
/// 真的走了一步，连着 3 帧都是新局面就换过去（约 1 秒）。
/// </summary>
public sealed class Settler
{
    private readonly List<string> _recent = [];
    private readonly List<string> _long = [];

    public int Window { get; init; } = 4;
    public int Need { get; init; } = 3;
    /// <summary>只差一项的变化：最近 <see cref="LongWindow"/> 次里新读数至少占 <see cref="OneFieldNeed"/> 次才算真变
    /// （一项来回抖很常见，真实对局里只变一项的情况很少）。开头碰巧认定了读错的值，多数读数也能把它纠正回来。</summary>
    public int LongWindow { get; init; } = 20;
    public int OneFieldNeed { get; init; } = 15;

    /// <summary>当前认定的局面；null 表示还没认定。</summary>
    public string? Committed { get; private set; }

    /// <summary>喂一次读数。返回 true 表示认定的局面换了（换成了这次的读数）。</summary>
    public bool Feed(string key)
    {
        _recent.Add(key);
        if (_recent.Count > Window) _recent.RemoveAt(0);
        _long.Add(key);
        if (_long.Count > LongWindow) _long.RemoveAt(0);
        if (Committed is not { } c)
        {
            if (_recent.Count(k => Close(k, key)) < 2) return false;
            Committed = key;
            return true;
        }
        if (key == c) return false;
        // 只差一项：这一项稳定地变了（连着出现够次数、原来的几乎没了）才算真变；否则当成读错
        if (Close(key, c))
        {
            if (_long.Count(k => k == key) >= OneFieldNeed && _long.Count(k => k == c) <= LongWindow - OneFieldNeed) { Committed = key; return true; }
            return false;
        }
        // 卡牌 / 画面状态（“§”之后）变了，而且连着 2 次读到一模一样的新局面：直接认（玩家点了一下，画面真的变了）
        if (Tail(key) != Tail(c) && _recent.Count >= 2 && _recent[^1] == key && _recent[^2] == key) { Committed = key; return true; }
        int support = _recent.Count(k => Close(k, key) && !Close(k, c));
        int stayNear = _recent.Count(k => Close(k, c));
        if (support >= Need && stayNear <= 1) { Committed = key; return true; }
        return false;
    }

    /// <summary>键里“§”之后的部分（卡牌、画面状态）；没有“§”时是整个键。</summary>
    public static string Tail(string key) => key.IndexOf('§') is int i and >= 0 ? key[i..] : key;

    /// <summary>这次读数算不算当前认定的局面（同一个或只差一项）。</summary>
    public bool Matches(string key) => Committed is { } c && Close(key, c);

    public void Reset()
    {
        _recent.Clear();
        Committed = null;
        _long.Clear();
    }

    /// <summary>两次读数是不是同一个局面：按“|”和“·”拆成项，最多允许 1 项不同。</summary>
    public static bool Close(string a, string b)
    {
        if (a == b) return true;
        // “§”后面是卡牌和画面状态（有没有卡、是什么卡）：这一段必须一模一样，不当成读错
        // （2026-11-06 华为实机：换完以后只多出“吃 / 结束”两张卡，被当成“只差一项”，等了 16 秒才认，一直显示“暂无”）
        int ia = a.IndexOf('§'), ib = b.IndexOf('§');
        if ((ia < 0) != (ib < 0)) return false;
        if (ia >= 0)
        {
            if (a[ia..] != b[ib..]) return false;
            a = a[..ia];
            b = b[..ib];
        }
        var xa = a.Split('|', '·');
        var xb = b.Split('|', '·');
        if (xa.Length != xb.Length) return false;
        int diff = 0;
        for (int i = 0; i < xa.Length; i++)
            if (xa[i].Trim() != xb[i].Trim() && ++diff > 1) return false;
        return true;
    }
}
