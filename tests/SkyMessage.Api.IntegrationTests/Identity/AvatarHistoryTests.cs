using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.MediaFiles;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

/// <summary>
/// Avatar histories of profiles and groups: newest first with the current photo leading, set-as-main, delete, and who
/// may see them (profile-photo privacy and blocks for people, membership for groups) — the photos' media too.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AvatarHistoryTests(ApiFactory factory)
{
    [Fact]
    public async Task Every_photo_set_joins_the_history_and_setting_one_again_brings_it_to_the_front()
    {
        var owner = await factory.NewUserAsync();
        var (a, b, c) = (await PhotoAsync(owner), await PhotoAsync(owner), await PhotoAsync(owner));
        foreach (var photo in new[] { a, b, c })
            await SetAvatarAsync(owner, photo);

        (await History(owner, owner)).Select(p => p.Media.Id).ShouldBe([c, b, a]);
        (await History(owner, owner))[0].IsCurrent.ShouldBeTrue();

        (await SetAvatarAsync(owner, a)).AvatarMediaId.ShouldBe(a);

        var history = await History(owner, owner);
        history.Select(p => p.Media.Id).ShouldBe([a, c, b]);
        history.Select(p => p.IsCurrent).ShouldBe([true, false, false]);
        history[0].SetAt.ShouldBeGreaterThan(history[1].SetAt);
    }

    [Fact]
    public async Task Deleting_the_current_photo_brings_back_the_previous_one()
    {
        var owner = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync();
        await friend.AddContactAsync(owner);
        var (a, b) = (await PhotoAsync(owner), await PhotoAsync(owner));
        await SetAvatarAsync(owner, a);
        await SetAvatarAsync(owner, b);
        await using var probe = await RealtimeProbe.ConnectAsync(friend);

        var me = await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatarByIdPath(b));

        me.AvatarMediaId.ShouldBe(a);
        (await History(owner, owner)).Select(p => p.Media.Id).ShouldBe([a]);
        await probe.WaitForAsync<UserRefEvent>(e => e.UserId == owner.Id);
        (await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatarByIdPath(a))).AvatarMediaId.ShouldBeNull();
        (await History(owner, owner)).ShouldBeEmpty();
        await (await owner.Client.Delete(ApiRoutes.Users.MeAvatarByIdPath(a))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }

    [Fact]
    public async Task Removing_the_avatar_leaves_no_photo_and_keeps_the_older_ones_for_the_owner_only()
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        var (a, b) = (await PhotoAsync(owner), await PhotoAsync(owner));
        await SetAvatarAsync(owner, a);
        (await SetAvatarAsync(owner, b)).EarlierAvatarCount.ShouldBe(1);

        var me = await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatar);

        me.AvatarMediaId.ShouldBeNull();
        me.EarlierAvatarCount.ShouldBe(1, "Settings still offers the earlier photo");
        (await owner.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).EarlierAvatarCount.ShouldBe(1);
        (await History(owner, owner)).Select(p => p.Media.Id).ShouldBe([a]);
        (await History(viewer, owner)).ShouldBeEmpty();
        await MediaShouldBeVisibleAsync(viewer, a, false);
        // Back again from the history.
        (await SetAvatarAsync(owner, a)).AvatarMediaId.ShouldBe(a);
        (await History(viewer, owner)).Select(p => p.Media.Id).ShouldBe([a]);
    }

    [Fact]
    public async Task Deleting_all_photos_leaves_no_photo_and_nothing_to_show_again_with_the_next_one()
    {
        var owner = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync();
        await friend.AddContactAsync(owner);
        var (a, b, c) = (await PhotoAsync(owner), await PhotoAsync(owner), await PhotoAsync(owner));
        await SetAvatarAsync(owner, a);
        await SetAvatarAsync(owner, b);
        await using var probe = await RealtimeProbe.ConnectAsync(friend);

        var me = await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatars);

        me.AvatarMediaId.ShouldBeNull();
        me.EarlierAvatarCount.ShouldBe(0);
        (await History(owner, owner)).ShouldBeEmpty();
        await probe.WaitForAsync<UserRefEvent>(e => e.UserId == owner.Id);

        (await SetAvatarAsync(owner, c)).EarlierAvatarCount.ShouldBe(0);
        (await History(friend, owner)).Select(p => p.Media.Id).ShouldBe([c]);
        await MediaShouldBeVisibleAsync(friend, a, false);
        // Without photos it changes nothing.
        await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatars);
        (await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatars)).AvatarMediaId.ShouldBeNull();
    }

    [Fact]
    public async Task A_new_session_and_a_refresh_carry_the_count_of_earlier_photos()
    {
        var owner = await factory.NewUserAsync();
        var (a, b) = (await PhotoAsync(owner), await PhotoAsync(owner));
        await SetAvatarAsync(owner, a);
        await SetAvatarAsync(owner, b);
        await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeAvatar);

        var second = await owner.SignInAgainAsync();
        second.Auth.User.EarlierAvatarCount.ShouldBe(1);
        var refreshed = await factory.NewClient().PostAs<AuthResponse>(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = second.Auth.RefreshToken });
        refreshed.User.EarlierAvatarCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(PrivacyLevel.Everybody, true, true)]
    [InlineData(PrivacyLevel.Contacts, false, true)]
    [InlineData(PrivacyLevel.Nobody, false, false)]
    public async Task History_and_its_photos_follow_the_profile_photo_privacy(PrivacyLevel level, bool strangerSees, bool contactSees)
    {
        var owner = await factory.NewUserAsync();
        var stranger = await factory.NewUserAsync();
        var contact = await factory.NewUserAsync();
        await owner.AddContactAsync(contact);
        var (old, current) = (await PhotoAsync(owner), await PhotoAsync(owner));
        await SetAvatarAsync(owner, old);
        await SetAvatarAsync(owner, current);
        await owner.SetPrivacyAsync(p => p with { ProfilePhoto = level });

        (await History(stranger, owner)).Count.ShouldBe(strangerSees ? 2 : 0);
        (await History(contact, owner)).Count.ShouldBe(contactSees ? 2 : 0);
        await MediaShouldBeVisibleAsync(stranger, old, strangerSees);
        await MediaShouldBeVisibleAsync(contact, old, contactSees);
    }

    [Fact]
    public async Task Someone_who_blocked_the_viewer_shows_no_history()
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        var photo = await PhotoAsync(owner);
        await SetAvatarAsync(owner, photo);
        await owner.BlockAsync(viewer);

        (await History(viewer, owner)).ShouldBeEmpty();
        await MediaShouldBeVisibleAsync(viewer, photo, false);
        await (await viewer.Client.Get(ApiRoutes.Users.AvatarsPath(Guid.CreateVersion7()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Only_own_photos_can_become_the_avatar_and_be_deleted()
    {
        var owner = await factory.NewUserAsync();
        var other = await factory.NewUserAsync();
        var theirs = await PhotoAsync(other);
        await SetAvatarAsync(other, theirs);

        await (await owner.Client.Put(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = theirs }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await owner.Client.Delete(ApiRoutes.Users.MeAvatarByIdPath(theirs)))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        (await History(other, other)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task History_keeps_the_newest_photos_up_to_its_limit()
    {
        var owner = await factory.NewUserAsync();
        var photos = new List<Guid>();
        for (var i = 0; i < Limits.AvatarHistoryMax + 2; i++)
        {
            var photo = await PhotoAsync(owner);
            photos.Add(photo);
            await SetAvatarAsync(owner, photo);
        }

        var history = await History(owner, owner);

        history.Count.ShouldBe(Limits.AvatarHistoryMax);
        history[0].Media.Id.ShouldBe(photos[^1]);
        history.Select(p => p.Media.Id).ShouldNotContain(photos[0]);
        history.Select(p => p.Media.Id).ShouldNotContain(photos[1]);
    }

    [Fact]
    public async Task Media_cleanup_keeps_photos_of_a_history_and_a_cover()
    {
        var owner = await factory.NewUserAsync();
        var (old, current, cover) = (await PhotoAsync(owner), await PhotoAsync(owner), await PhotoAsync(owner));
        await SetAvatarAsync(owner, old);
        await SetAvatarAsync(owner, current);
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeCover, new SetCoverRequest { MediaId = cover });
        var stray = await PhotoAsync(owner);
        await factory.WithDbAsync(db => db.Media.Where(m => m.OwnerId == owner.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, DateTimeOffset.UtcNow.AddDays(-30)), Ct));

        var cleanup = factory.Services.GetServices<IHostedService>().OfType<MediaCleanupService>().Single();
        await cleanup.RunOnceAsync(Ct);

        var left = await factory.WithDbAsync(db => db.Media.Where(m => m.OwnerId == owner.Id).Select(m => m.Id).ToListAsync(Ct));
        left.ShouldBe([old, current, cover], ignoreOrder: true);
        left.ShouldNotContain(stray);
    }

    [Fact]
    public async Task Group_history_is_for_members_and_admins_set_or_delete_its_photos()
    {
        var owner = await factory.NewUserAsync();
        var admin = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var outsider = await factory.NewUserAsync();
        var chatId = await owner.CreateGroupAsync(admin, member);
        await (await owner.Client.Put(ApiRoutes.Chats.MemberRolePath(chatId, admin.Id), new SetMemberRoleRequest { Role = ChatRole.Admin }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);
        var first = await PhotoAsync(owner);
        var second = await PhotoAsync(admin);
        await owner.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = first });
        await admin.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = second });

        (await GroupHistory(member, chatId)).Select(p => p.Media.Id).ShouldBe([second, first]);
        await MediaShouldBeVisibleAsync(member, first, true);
        await (await outsider.Client.Get(ApiRoutes.Chats.AvatarsPath(chatId))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await MediaShouldBeVisibleAsync(outsider, first, false);

        // The owner's photo from the history comes back through the other admin, who did not upload it.
        (await admin.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = first })).AvatarMediaId.ShouldBe(first);
        (await GroupHistory(member, chatId)).Select(p => p.Media.Id).ShouldBe([first, second]);

        // Members cannot change the group's info by default.
        await (await member.Client.Delete(ApiRoutes.Chats.AvatarByIdPath(chatId, second))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        var notAMember = await PhotoAsync(member);
        await (await member.Client.Put(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = notAMember }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);

        await using var probe = await RealtimeProbe.ConnectAsync(member);
        (await admin.Client.DeleteAs<ChatInfoDto>(ApiRoutes.Chats.AvatarByIdPath(chatId, first))).AvatarMediaId.ShouldBe(second);
        (await GroupHistory(member, chatId)).Select(p => p.Media.Id).ShouldBe([second]);
        (await owner.Client.DeleteAs<ChatInfoDto>(ApiRoutes.Chats.AvatarByIdPath(chatId, second))).AvatarMediaId.ShouldBeNull();
        (await GroupHistory(member, chatId)).ShouldBeEmpty();
        await (await owner.Client.Delete(ApiRoutes.Chats.AvatarByIdPath(chatId, second))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }

    [Fact]
    public async Task Without_a_photo_a_groups_earlier_photos_are_only_for_who_may_change_it()
    {
        var owner = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var chatId = await owner.CreateGroupAsync(member);
        var (first, second) = (await PhotoAsync(owner), await PhotoAsync(owner));
        await owner.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = first });
        await owner.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = second });
        (await Details(member, chatId)).EarlierAvatarCount.ShouldBe(1);

        (await owner.Client.DeleteAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId))).AvatarMediaId.ShouldBeNull();

        (await GroupHistory(owner, chatId)).Select(p => p.Media.Id).ShouldBe([first]);
        (await Details(owner, chatId)).EarlierAvatarCount.ShouldBe(1);
        (await GroupHistory(member, chatId)).ShouldBeEmpty();
        (await Details(member, chatId)).EarlierAvatarCount.ShouldBe(0);
        await MediaShouldBeVisibleAsync(member, first, false);

        // A photo again: the earlier one shows with it.
        await owner.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = first });
        (await GroupHistory(member, chatId)).Select(p => p.Media.Id).ShouldBe([first]);
    }

    [Fact]
    public async Task Deleting_all_of_a_groups_photos_leaves_no_photo_and_no_history()
    {
        var owner = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var chatId = await owner.CreateGroupAsync(member);
        var (first, second) = (await PhotoAsync(owner), await PhotoAsync(owner));
        await owner.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = first });
        await owner.Client.PutAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = second });

        await (await member.Client.Delete(ApiRoutes.Chats.AvatarsPath(chatId))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await using var probe = await RealtimeProbe.ConnectAsync(member);
        (await owner.Client.DeleteAs<ChatInfoDto>(ApiRoutes.Chats.AvatarsPath(chatId))).AvatarMediaId.ShouldBeNull();

        await probe.WaitForAsync<ChatInfoDto>(e => e.Id == chatId && e.AvatarMediaId == null);
        (await GroupHistory(owner, chatId)).ShouldBeEmpty();
        (await Details(owner, chatId)).EarlierAvatarCount.ShouldBe(0);
        // Without photos it changes nothing.
        (await owner.Client.DeleteAs<ChatInfoDto>(ApiRoutes.Chats.AvatarsPath(chatId))).AvatarMediaId.ShouldBeNull();
    }

    [Fact]
    public async Task A_group_created_with_a_photo_starts_its_history_and_removing_the_photo_drops_it()
    {
        var owner = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var photo = await PhotoAsync(owner);
        var created = await owner.Client.PostAs<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest
        {
            Title = "Family",
            MemberIds = [member.Id],
            AvatarMediaId = photo,
        });
        var chatId = created.Chat.Id;

        (await GroupHistory(member, chatId)).Single().Media.Id.ShouldBe(photo);

        (await owner.Client.DeleteAs<ChatInfoDto>(ApiRoutes.Chats.AvatarPath(chatId))).AvatarMediaId.ShouldBeNull();
        (await GroupHistory(member, chatId)).ShouldBeEmpty();
    }

    private Task<Guid> PhotoAsync(TestUser owner) => factory.CreateMediaAsync(owner.Id);

    private static Task<MeDto> SetAvatarAsync(TestUser owner, Guid mediaId) =>
        owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = mediaId });

    private static Task<IReadOnlyList<AvatarPhotoDto>> History(TestUser viewer, TestUser owner) =>
        viewer.Client.GetAs<IReadOnlyList<AvatarPhotoDto>>(ApiRoutes.Users.AvatarsPath(owner.Id));

    private static Task<IReadOnlyList<AvatarPhotoDto>> GroupHistory(TestUser viewer, Guid chatId) =>
        viewer.Client.GetAs<IReadOnlyList<AvatarPhotoDto>>(ApiRoutes.Chats.AvatarsPath(chatId));

    private static Task<ChatDetailsDto> Details(TestUser viewer, Guid chatId) =>
        viewer.Client.GetAs<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(chatId));

    private static async Task MediaShouldBeVisibleAsync(TestUser viewer, Guid mediaId, bool visible)
    {
        var response = await viewer.Client.Get(ApiRoutes.Media.ByIdPath(mediaId));
        if (visible) await response.ShouldBeStatusAsync(HttpStatusCode.OK);
        else await response.ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }
}
