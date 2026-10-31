using Shouldly;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class ChatBasicsRulesTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid Stranger = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    [Fact]
    public void A_full_request_becomes_the_new_order()
    {
        PinOrderRules.Arrange([A, B, C], [C, A, B]).ShouldBe([C, A, B]);
    }

    [Fact]
    public void Chats_left_out_keep_their_places_and_the_listed_ones_fill_the_rest()
    {
        // D was pinned on another device (on top) and C sits in the archive: only A and B swap.
        PinOrderRules.Arrange([D, A, C, B], [B, A]).ShouldBe([D, B, C, A]);
    }

    [Fact]
    public void Unknown_and_repeated_ids_are_ignored()
    {
        PinOrderRules.Arrange([A, B, C], [Stranger, C, C, Stranger, B]).ShouldBe([A, C, B]);
        PinOrderRules.Arrange([A, B], [Stranger]).ShouldBe([A, B]);
        PinOrderRules.Arrange([], [A, B]).ShouldBeEmpty();
    }

    [Fact]
    public void Orders_count_down_to_one_and_a_new_pin_goes_on_top()
    {
        Enumerable.Range(0, 4).Select(i => PinOrderRules.OrderAt(i, 4)).ShouldBe([4L, 3L, 2L, 1L]);
        PinOrderRules.NextPin(null).ShouldBe(1);
        PinOrderRules.NextPin(0).ShouldBe(1);
        PinOrderRules.NextPin(7).ShouldBe(8);
    }

    [Fact]
    public void Each_participant_holds_their_own_part_of_a_private_chats_protection()
    {
        var bobOn = ContentProtectionRules.Toggle([], B, enabled: true);
        bobOn.ShouldBe([B]);
        ContentProtectionRules.IsProtected(bobOn).ShouldBeTrue();

        // Alice switching "off" cannot touch Bob's part; switching on adds hers once.
        ContentProtectionRules.Toggle(bobOn, A, enabled: false).ShouldBe([B]);
        var both = ContentProtectionRules.Toggle(bobOn, A, enabled: true);
        both.ShouldBe([B, A]);
        ContentProtectionRules.Toggle(both, A, enabled: true).ShouldBe([B, A]);

        var aliceOnly = ContentProtectionRules.Toggle(both, B, enabled: false);
        aliceOnly.ShouldBe([A]);
        ContentProtectionRules.IsProtected(aliceOnly).ShouldBeTrue();
        ContentProtectionRules.IsProtected(ContentProtectionRules.Toggle(aliceOnly, A, enabled: false)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChatType.Group, ChatRole.Member, false)]
    [InlineData(ChatType.Group, ChatRole.Admin, true)]
    [InlineData(ChatType.Group, ChatRole.Owner, true)]
    [InlineData(ChatType.Private, ChatRole.Member, true)]
    [InlineData(ChatType.Saved, ChatRole.Owner, false)]
    public void Group_admins_and_both_sides_of_a_private_chat_switch_protection(ChatType type, ChatRole role, bool expected)
    {
        ChatPolicy.CanSetContentProtection(type, role).ShouldBe(expected);
    }
}
