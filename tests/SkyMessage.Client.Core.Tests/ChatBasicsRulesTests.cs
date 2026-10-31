using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Pinned order arithmetic, the new-contact bar and content protection as the client sees them.</summary>
public sealed class ChatBasicsRulesTests
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Peer = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    private static ChatSummaryDto PrivateChat(bool dismissed = false) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = ChatType.Private,
        Peer = PeerUser(),
        ContactBarDismissed = dismissed,
    };

    private static UserSummaryDto PeerUser(bool contact = false, bool deleted = false) =>
        new() { Id = Peer, Username = "stranger", DisplayName = "Stranger", IsContact = contact, IsDeleted = deleted };

    // ---- Pinned order ----

    [Fact]
    public void A_drag_moves_one_chat_and_the_others_close_up()
    {
        PinnedOrderRules.Move([A, B, C, D], 0, 2).ShouldBe([B, C, A, D]);
        PinnedOrderRules.Move([A, B, C, D], 3, 0).ShouldBe([D, A, B, C]);
        PinnedOrderRules.Move([A, B, C], 1, 1).ShouldBe([A, B, C]);
        PinnedOrderRules.Move([A, B, C], 1, 9).ShouldBe([A, C, B]);
        PinnedOrderRules.Move([A, B], 5, 0).ShouldBe([A, B]);
    }

    [Fact]
    public void Arranging_matches_the_server_chats_left_out_keep_their_places()
    {
        PinnedOrderRules.Arrange([A, B, C], [C, A, B]).ShouldBe([C, A, B]);
        // D sits in the archive (or was pinned on another device): only the listed ones swap places.
        PinnedOrderRules.Arrange([D, A, C, B], [B, A]).ShouldBe([D, B, C, A]);
        PinnedOrderRules.Arrange([A, B], [Guid.NewGuid(), B, B]).ShouldBe([A, B]);
    }

    [Fact]
    public void Renumbering_counts_down_over_every_pinned_chat_in_list_order()
    {
        var t = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        ChatSummaryDto Pin(Guid id, long order, int minutes = 0) =>
            new() { Id = id, Type = ChatType.Group, IsPinned = true, PinOrder = order, PinnedAt = t.AddMinutes(minutes) };
        var chats = new[] { Pin(A, 5), Pin(B, 2), Pin(C, 0, minutes: 9), Pin(D, 0, minutes: 1), new ChatSummaryDto { Id = Guid.NewGuid() } };

        var orders = PinnedOrderRules.Renumber(chats, [C, A]);

        orders.Select(o => o.ChatId).ShouldBe([C, B, A, D]);
        orders.Select(o => o.PinOrder).ShouldBe([4L, 3L, 2L, 1L]);
        PinnedOrderRules.NextPin(chats).ShouldBe(6);
        PinnedOrderRules.NextPin([]).ShouldBe(1);
    }

    // ---- Contact bar ----

    [Fact]
    public void The_contact_bar_shows_for_a_stranger_until_added_blocked_or_closed()
    {
        var chat = PrivateChat();
        ContactBarRules.ShouldShow(chat, PeerUser(), Me, contactsKnown: true, isContact: false, blockedByMe: false).ShouldBeTrue();

        ContactBarRules.ShouldShow(chat, PeerUser(), Me, true, isContact: true, blockedByMe: false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat, PeerUser(contact: true), Me, true, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat, PeerUser(), Me, true, false, blockedByMe: true).ShouldBeFalse();
        ContactBarRules.ShouldShow(PrivateChat(dismissed: true), PeerUser(), Me, true, false, false).ShouldBeFalse();
    }

    [Fact]
    public void The_contact_bar_never_shows_for_groups_saved_messages_deleted_accounts_or_before_contacts_are_known()
    {
        var chat = PrivateChat();
        ContactBarRules.ShouldShow(chat, PeerUser(), Me, contactsKnown: false, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat, PeerUser(deleted: true), Me, true, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat, null, Me, true, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat, PeerUser(), null, true, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat with { Type = ChatType.Group }, PeerUser(), Me, true, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(chat with { Type = ChatType.Saved }, PeerUser() with { Id = Me }, Me, true, false, false).ShouldBeFalse();
        ContactBarRules.ShouldShow(null, PeerUser(), Me, true, false, false).ShouldBeFalse();
    }

    // ---- Content protection ----

    [Theory]
    [InlineData(ChatRole.Member, false)]
    [InlineData(ChatRole.Admin, true)]
    [InlineData(ChatRole.Owner, true)]
    public void Group_admins_switch_protection_for_everyone(ChatRole role, bool canChange)
    {
        var group = new ChatSummaryDto { Id = Guid.NewGuid(), Type = ChatType.Group, MyRole = role, ProtectContent = true };

        var state = ContentProtectionRules.Describe(group, Me);

        state.IsProtected.ShouldBeTrue();
        state.CanChange.ShouldBe(canChange);
        ContentProtectionRules.Apply(group, Me, false).ProtectContent.ShouldBeFalse();
    }

    [Fact]
    public void In_a_private_chat_I_can_add_my_part_but_never_take_back_the_other_ones()
    {
        var open = PrivateChat();
        ContentProtectionRules.Describe(open, Me).ShouldBe(new ContentProtectionState(false, true, false, false));

        var mine = ContentProtectionRules.Apply(open, Me, true);
        mine.ProtectContent.ShouldBeTrue();
        mine.ProtectContentBy.ShouldBe([Me]);
        ContentProtectionRules.Describe(mine, Me).ShouldBe(new ContentProtectionState(true, true, true, false));

        var theirs = open with { ProtectContent = true, ProtectContentBy = [Peer] };
        ContentProtectionRules.Describe(theirs, Me).ShouldBe(new ContentProtectionState(true, false, false, true));
        ContentProtectionRules.Apply(theirs, Me, false).ProtectContent.ShouldBeTrue("my switch cannot undo the other side's choice");

        var both = ContentProtectionRules.Apply(theirs, Me, true);
        ContentProtectionRules.Describe(both, Me).ShouldBe(new ContentProtectionState(true, true, true, true));
        var stillOn = ContentProtectionRules.Apply(both, Me, false);
        stillOn.ProtectContent.ShouldBeTrue();
        stillOn.ProtectContentBy.ShouldBe([Peer]);
    }

    [Fact]
    public void Saved_messages_are_never_protected()
    {
        var saved = new ChatSummaryDto { Id = Guid.NewGuid(), Type = ChatType.Saved, ProtectContent = true };
        ContentProtectionRules.IsProtected(saved).ShouldBeFalse();
        ContentProtectionRules.Describe(saved, Me).CanChange.ShouldBeFalse();
        ContentProtectionRules.IsProtected(null).ShouldBeFalse();
    }

    // ---- Screenshots ----

    [Fact]
    public void The_window_stays_secure_while_any_protected_screen_holds_it()
    {
        var platform = Substitute.For<IScreenSecurityService>();
        var guard = new ScreenCaptureGuard(platform, Log.For<ScreenCaptureGuard>());

        var chat = guard.Protect();
        var media = guard.Protect();
        guard.IsSecure.ShouldBeTrue();
        platform.Received(1).SetSecure(true);

        chat.Dispose();
        chat.Dispose();
        guard.IsSecure.ShouldBeTrue("the shared media page of the chat still shows protected photos");
        platform.DidNotReceive().SetSecure(false);

        media.Dispose();
        guard.IsSecure.ShouldBeFalse();
        platform.Received(1).SetSecure(false);

        using (guard.Protect()) platform.Received(2).SetSecure(true);
        platform.Received(2).SetSecure(false);
    }

    [Fact]
    public void A_platform_that_fails_to_switch_never_breaks_the_screen()
    {
        var platform = Substitute.For<IScreenSecurityService>();
        platform.When(p => p.SetSecure(Arg.Any<bool>())).Do(_ => throw new InvalidOperationException("no window"));
        var guard = new ScreenCaptureGuard(platform, Log.For<ScreenCaptureGuard>());

        var handle = Should.NotThrow(guard.Protect);
        Should.NotThrow(handle.Dispose);
        guard.IsSecure.ShouldBeFalse();
    }
}
