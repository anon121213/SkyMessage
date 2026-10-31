using Shouldly;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class ChatPolicyTests
{
    private static readonly GroupPermissions Default = new();
    private static readonly GroupPermissions LockedDown = new()
    {
        SendMessages = false,
        SendMedia = false,
        AddMembers = false,
        PinMessages = false,
        ChangeInfo = false,
    };
    private static readonly GroupPermissions Open = new()
    {
        SendMessages = true,
        SendMedia = true,
        AddMembers = true,
        PinMessages = true,
        ChangeInfo = true,
    };

    [Theory]
    [InlineData(ChatRole.Member, false)]
    [InlineData(ChatRole.Admin, true)]
    [InlineData(ChatRole.Owner, true)]
    public void Admins_bypass_group_permissions(ChatRole role, bool expected)
    {
        ChatPolicy.CanSendMessages(ChatType.Group, role, LockedDown).ShouldBe(expected);
        ChatPolicy.CanSendMedia(ChatType.Group, role, LockedDown).ShouldBe(expected);
        ChatPolicy.CanAddMembers(ChatType.Group, role, LockedDown).ShouldBe(expected);
        ChatPolicy.CanPinMessages(ChatType.Group, role, LockedDown).ShouldBe(expected);
        ChatPolicy.CanChangeInfo(ChatType.Group, role, LockedDown).ShouldBe(expected);
        ChatPolicy.CanChangePermissions(ChatType.Group, role).ShouldBe(expected);
    }

    [Fact]
    public void Members_follow_group_permissions()
    {
        ChatPolicy.CanSendMessages(ChatType.Group, ChatRole.Member, Default).ShouldBeTrue();
        ChatPolicy.CanSendMedia(ChatType.Group, ChatRole.Member, Default).ShouldBeTrue();
        ChatPolicy.CanAddMembers(ChatType.Group, ChatRole.Member, Default).ShouldBeTrue();
        ChatPolicy.CanPinMessages(ChatType.Group, ChatRole.Member, Default).ShouldBeFalse();
        ChatPolicy.CanChangeInfo(ChatType.Group, ChatRole.Member, Default).ShouldBeFalse();
        ChatPolicy.CanPinMessages(ChatType.Group, ChatRole.Member, Open).ShouldBeTrue();
        ChatPolicy.CanChangeInfo(ChatType.Group, ChatRole.Member, Open).ShouldBeTrue();
        ChatPolicy.CanChangePermissions(ChatType.Group, ChatRole.Member).ShouldBeFalse();
    }

    [Fact]
    public void Media_needs_both_send_permissions()
    {
        var mediaOnly = new GroupPermissions { SendMessages = false, SendMedia = true };
        ChatPolicy.CanSendMedia(ChatType.Group, ChatRole.Member, mediaOnly).ShouldBeFalse();
        var textOnly = new GroupPermissions { SendMessages = true, SendMedia = false };
        ChatPolicy.CanSendMessages(ChatType.Group, ChatRole.Member, textOnly).ShouldBeTrue();
        ChatPolicy.CanSendMedia(ChatType.Group, ChatRole.Member, textOnly).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChatType.Private)]
    [InlineData(ChatType.Saved)]
    public void Non_groups_ignore_group_permissions(ChatType type)
    {
        ChatPolicy.CanSendMessages(type, ChatRole.Member, LockedDown).ShouldBeTrue();
        ChatPolicy.CanSendMedia(type, ChatRole.Member, LockedDown).ShouldBeTrue();
        ChatPolicy.CanPinMessages(type, ChatRole.Member, LockedDown).ShouldBeTrue();
        ChatPolicy.CanAddMembers(type, ChatRole.Owner, Open).ShouldBeFalse();
        ChatPolicy.CanChangeInfo(type, ChatRole.Owner, Open).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChatRole.Owner, ChatRole.Admin, true)]
    [InlineData(ChatRole.Owner, ChatRole.Member, true)]
    [InlineData(ChatRole.Owner, ChatRole.Owner, false)]
    [InlineData(ChatRole.Admin, ChatRole.Member, true)]
    [InlineData(ChatRole.Admin, ChatRole.Admin, false)]
    [InlineData(ChatRole.Admin, ChatRole.Owner, false)]
    [InlineData(ChatRole.Member, ChatRole.Member, false)]
    public void Removal_matrix(ChatRole actor, ChatRole target, bool expected) =>
        ChatPolicy.CanRemoveMember(actor, target).ShouldBe(expected);

    [Fact]
    public void Only_the_owner_manages_roles_and_only_admins_lift_bans()
    {
        ChatPolicy.CanSetRoles(ChatRole.Owner).ShouldBeTrue();
        ChatPolicy.CanSetRoles(ChatRole.Admin).ShouldBeFalse();
        ChatPolicy.CanSetRoles(ChatRole.Member).ShouldBeFalse();
        ChatPolicy.CanAddBannedUser(ChatRole.Admin).ShouldBeTrue();
        ChatPolicy.CanAddBannedUser(ChatRole.Member).ShouldBeFalse();
    }

    [Fact]
    public void Delete_for_everyone_matrix()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        ChatPolicy.CanDeleteForEveryone(ChatType.Group, ChatRole.Member, me, me).ShouldBeTrue();
        ChatPolicy.CanDeleteForEveryone(ChatType.Group, ChatRole.Member, me, other).ShouldBeFalse();
        ChatPolicy.CanDeleteForEveryone(ChatType.Group, ChatRole.Member, me, null).ShouldBeFalse();
        ChatPolicy.CanDeleteForEveryone(ChatType.Group, ChatRole.Admin, me, other).ShouldBeTrue();
        ChatPolicy.CanDeleteForEveryone(ChatType.Group, ChatRole.Admin, me, null).ShouldBeTrue();
        ChatPolicy.CanDeleteForEveryone(ChatType.Private, ChatRole.Member, me, other).ShouldBeTrue();
        ChatPolicy.CanDeleteForEveryone(ChatType.Private, ChatRole.Member, me, null).ShouldBeTrue();
        ChatPolicy.CanDeleteForEveryone(ChatType.Saved, ChatRole.Owner, me, me).ShouldBeTrue();
    }

    [Fact]
    public void Delete_chat_for_everyone_matrix()
    {
        ChatPolicy.CanDeleteChatForEveryone(ChatType.Private, ChatRole.Member).ShouldBeTrue();
        ChatPolicy.CanDeleteChatForEveryone(ChatType.Group, ChatRole.Owner).ShouldBeTrue();
        ChatPolicy.CanDeleteChatForEveryone(ChatType.Group, ChatRole.Admin).ShouldBeFalse();
        ChatPolicy.CanDeleteChatForEveryone(ChatType.Group, ChatRole.Member).ShouldBeFalse();
    }
}

public sealed class OwnershipRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Oldest_admin_wins_over_older_members()
    {
        var oldestMember = new OwnershipRules.Candidate(Guid.NewGuid(), ChatRole.Member, T0);
        var newerAdmin = new OwnershipRules.Candidate(Guid.NewGuid(), ChatRole.Admin, T0.AddDays(2));
        var oldestAdmin = new OwnershipRules.Candidate(Guid.NewGuid(), ChatRole.Admin, T0.AddDays(1));
        OwnershipRules.PickSuccessor([oldestMember, newerAdmin, oldestAdmin]).ShouldBe(oldestAdmin.UserId);
    }

    [Fact]
    public void Without_admins_the_oldest_member_wins()
    {
        var newer = new OwnershipRules.Candidate(Guid.NewGuid(), ChatRole.Member, T0.AddHours(1));
        var older = new OwnershipRules.Candidate(Guid.NewGuid(), ChatRole.Member, T0);
        OwnershipRules.PickSuccessor([newer, older]).ShouldBe(older.UserId);
    }

    [Fact]
    public void Ties_are_broken_deterministically()
    {
        var a = new OwnershipRules.Candidate(new Guid("00000000-0000-0000-0000-000000000002"), ChatRole.Member, T0);
        var b = new OwnershipRules.Candidate(new Guid("00000000-0000-0000-0000-000000000001"), ChatRole.Member, T0);
        OwnershipRules.PickSuccessor([a, b]).ShouldBe(b.UserId);
        OwnershipRules.PickSuccessor([b, a]).ShouldBe(b.UserId);
    }

    [Fact]
    public void Nobody_left_means_no_successor() => OwnershipRules.PickSuccessor([]).ShouldBeNull();
}
