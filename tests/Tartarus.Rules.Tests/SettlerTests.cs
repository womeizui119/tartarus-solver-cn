using Tartarus.Vision;
using Xunit;

namespace Tartarus.Rules.Tests;

/// <summary>中文版手机：局面认定不因读数抖动而来回变（2026-11-06 实机：审讯室提示在“结束”和“煽动”间闪）。</summary>
public class SettlerTests
{
    // 一个局面的读数：几项用“|”隔开，和 Pipeline 的局面键同样的形状
    private static string Key(string cell14 = "Rat Magic", string cells = "instigate,bestow", string days = "17", string warden = "1", string cell8 = "Unopened")
        => $"08: {cell8} | 14: {cell14} | Warden {warden} · Remaining Days {days}§{cells}";

    private static int Changes(Settler s, IEnumerable<string> reads) => reads.Count(s.Feed);

    [Fact]
    public void First_position_settles_after_two_reads()
    {
        var s = new Settler();
        Assert.False(s.Feed(Key()));
        Assert.True(s.Feed(Key()));
        Assert.Equal(Key(), s.Committed);
    }

    [Fact]
    public void One_field_flickering_almost_never_changes_the_position()
    {
        // 实机：每三帧左右读错一次（这里 35%，比实机还多）。500 局 × 400 帧，统计开头认定之后的多余切换
        int extra = 0, worst = 0, wrongAtEnd = 0;
        for (int seed = 0; seed < 500; seed++)
        {
            var s = new Settler();
            var rng = new Random(seed);
            int changes = 0;
            for (int i = 0; i < 400; i++)
                if (s.Feed(Key(cell14: rng.NextDouble() < 0.35 ? "Rat Normal" : "Rat Magic")) && i > 40) changes++;
            extra += changes;
            worst = Math.Max(worst, changes);
            if (s.Committed != Key()) wrongAtEnd++;
        }
        // 平均每 1500 帧以上（约 7 分钟）才多切一次；一局最多几次；绝大多数最后停在读得多的那个
        Assert.True(extra * 1500 < 500 * 360, $"extra {extra}");
        Assert.True(worst <= 4, $"worst {worst}");
        Assert.True(wrongAtEnd <= 5, $"wrong {wrongAtEnd}");
    }

    [Fact]
    public void Two_fields_flickering_together_never_change_the_position()
    {
        // 卡牌字和稀有度一起抖（实机日志里同一帧两处都读得不一样）
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key());
        var noisy = Key(cell14: "Rat Normal", cells: "bestow");
        int changes = Changes(s, new[] { noisy, Key(), noisy, Key(), noisy, Key(), noisy, Key(), noisy, Key() });
        Assert.Equal(0, changes);
        Assert.Equal(Key(), s.Committed);
    }

    [Fact]
    public void Random_junk_on_single_frames_is_ignored()
    {
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key());
        var rng = new Random(7);
        int changes = 0;
        for (int i = 0; i < 300; i++)
            changes += s.Feed(rng.Next(4) == 0 ? Key(cell14: "x" + rng.Next(), cells: "送," + rng.Next(), warden: rng.Next(16).ToString()) : Key()) ? 1 : 0;
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_real_move_is_followed_within_two_reads()
    {
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key()); s.Feed(Key()); s.Feed(Key());
        var next = Key(cells: "", days: "16", warden: "5");
        Assert.False(s.Feed(next));
        Assert.True(s.Feed(next));  // 卡牌也变了：连着 2 次一样就认
        Assert.Equal(next, s.Committed);
    }

    [Fact]
    public void A_real_move_with_noisy_reads_is_still_followed()
    {
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key()); s.Feed(Key());
        // 新局面本身也在抖：14 号一会儿 Magic 一会儿 Normal
        var a = Key(cells: "consume,end", days: "16", warden: "5");
        var b = Key(cell14: "Rat Normal", cells: "consume,end", days: "16", warden: "5");
        Changes(s, new[] { a, b, a, b });
        Assert.True(Settler.Close(s.Committed!, a));
        Assert.False(Settler.Close(s.Committed!, Key()));
    }

    [Fact]
    public void A_real_one_field_change_is_followed()
    {
        // 只有一项真的变了（比如换了一张卡），稳定读到几次后也要跟上
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key()); s.Feed(Key()); s.Feed(Key());
        var next = Key(cell8: "Wolf Rare");
        int changes = Changes(s, Enumerable.Repeat(next, 16));
        Assert.Equal(1, changes);
        Assert.Equal(next, s.Committed);
    }

    [Fact]
    public void A_screen_lost_for_one_frame_keeps_the_position()
    {
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key());
        int changes = Changes(s, new[] { "none", Key(), Key(), "none", Key(), "none", Key() });
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Leaving_the_game_is_noticed()
    {
        var s = new Settler();
        s.Feed(Key()); s.Feed(Key());
        int changes = Changes(s, new[] { "none", "none", "none" });
        Assert.Equal(1, changes);
        Assert.Equal("none", s.Committed);
    }

    [Fact]
    public void Cards_coming_out_are_followed_within_two_reads()
    {
        // 换完以后：棋盘不变，只多出“吃 / 结束”两张卡（2026-11-06 华为实机：等了 16 秒）
        var s = new Settler();
        var waiting = Key(cells: "");
        s.Feed(waiting); s.Feed(waiting); s.Feed(waiting); s.Feed(waiting);
        var decision = Key(cells: "consume,end");
        Assert.False(s.Feed(decision));
        Assert.True(s.Feed(decision));  // 卡牌变了：连着 2 次一样就认
    }

    [Fact]
    public void Cards_flickering_between_two_readings_keep_the_position()
    {
        var s = new Settler();
        s.Feed(Key(cells: "consume,replace,end")); s.Feed(Key(cells: "consume,replace,end"));
        var a = Key(cells: "consume,replace,end");
        var b = Key(cells: "consume,end");
        Assert.Equal(0, Changes(s, new[] { b, a, b, a, a, b, a, b, a }));
    }
}
