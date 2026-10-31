using Shouldly;
using SkyMessage.Client.Core.Services;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Several people sharing their screens in one group call: whose screen is on the stage.</summary>
public sealed class CallScreenSharesTests
{
    private const string Mama = "aaaa";
    private const string Papa = "bbbb";
    private const string Sis = "cccc";

    [Fact]
    public void Nobody_sharing_shows_nothing()
    {
        CallScreenShares.None.Shown.ShouldBeNull();
        CallScreenShares.None.Update([]).Shown.ShouldBeNull();
    }

    [Fact]
    public void The_newest_share_takes_the_stage_whatever_the_ids()
    {
        // The media engine lists people sorted by id: Papa starts later but comes after Mama only by chance.
        var shares = CallScreenShares.None.Update([Papa]).Update([Mama, Papa]);
        shares.Shown.ShouldBe(Mama);
        shares.Order.ShouldBe([Papa, Mama]);
    }

    [Fact]
    public void When_the_newest_share_ends_the_earlier_one_comes_back()
    {
        var shares = CallScreenShares.None.Update([Papa]).Update([Mama, Papa]).Update([Papa]);
        shares.Shown.ShouldBe(Papa);
    }

    [Fact]
    public void A_picked_share_stays_on_the_stage_while_it_lasts()
    {
        var shares = CallScreenShares.None.Update([Papa]).Update([Mama, Papa]).Pick(Papa);
        shares.Shown.ShouldBe(Papa);
        shares.Update([Mama, Papa]).Shown.ShouldBe(Papa, "nothing changed");
        shares.Update([Mama]).Shown.ShouldBe(Mama, "the picked share ended");
    }

    [Fact]
    public void A_new_share_takes_the_stage_over_a_pick()
    {
        var shares = CallScreenShares.None.Update([Papa]).Update([Mama, Papa]).Pick(Papa).Update([Mama, Papa, Sis]);
        shares.Shown.ShouldBe(Sis);
    }

    [Fact]
    public void Picking_someone_who_does_not_share_changes_nothing()
    {
        var shares = CallScreenShares.None.Update([Mama]);
        shares.Pick(Papa).ShouldBeSameAs(shares);
        shares.Pick(Papa).Shown.ShouldBe(Mama);
    }

    [Fact]
    public void A_share_that_stops_and_starts_again_is_the_newest()
    {
        var shares = CallScreenShares.None.Update([Mama]).Update([Mama, Papa]).Update([Papa]).Update([Mama, Papa]);
        shares.Shown.ShouldBe(Mama);
    }

    [Fact]
    public void An_unchanged_update_keeps_the_same_state()
    {
        var shares = CallScreenShares.None.Update([Mama, Papa]);
        shares.Update([Papa, Mama, Mama]).ShouldBeSameAs(shares);
    }
}
