using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

/// <summary>Identity rules other features rely on: media URL signatures, group-invite privacy, activity stamps.</summary>
[Collection(ApiCollection.Name)]
public sealed class SessionValidatorAndPrivacyRulesTests(ApiFactory factory)
{
    [Fact]
    public async Task Media_signatures_verify_against_the_session_key_until_the_session_ends()
    {
        var user = await factory.NewUserAsync();
        var mediaId = Guid.CreateVersion7();
        var token = MediaSignature.Create(user.Auth.MediaKey, user.SessionId, mediaId);

        (await ValidateAsync(token, mediaId)).ShouldBe((user.Id, user.SessionId));
        (await ValidateAsync(token, Guid.CreateVersion7())).ShouldBeNull();
        (await ValidateAsync(MediaSignature.Create(MediaSignature.NewKey(), user.SessionId, mediaId), mediaId)).ShouldBeNull();
        (await ValidateAsync(MediaSignature.Create(user.Auth.MediaKey, Guid.CreateVersion7(), mediaId), mediaId)).ShouldBeNull();
        (await ValidateAsync("not-a-signature", mediaId)).ShouldBeNull();

        // The key survives a refresh (same session)...
        var refreshed = await factory.NewClient().PostAs<AuthResponse>(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = user.Auth.RefreshToken });
        (await ValidateAsync(MediaSignature.Create(refreshed.MediaKey, user.SessionId, mediaId), mediaId)).ShouldBe((user.Id, user.SessionId));

        // ...but dies with the session, although the positive result was cached.
        await (await user.Client.Post(ApiRoutes.Auth.Logout)).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        (await ValidateAsync(token, mediaId)).ShouldBeNull();
    }

    [Theory]
    [InlineData(PrivacyLevel.Everybody, true)]
    [InlineData(PrivacyLevel.Contacts, false)]
    [InlineData(PrivacyLevel.Nobody, false)]
    public async Task Group_invites_privacy_decides_who_may_add_someone_to_a_group(PrivacyLevel level, bool strangerMayAdd)
    {
        var target = await factory.NewUserAsync();
        var stranger = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync();
        await target.AddContactAsync(friend);
        await target.SetPrivacyAsync(p => p with { GroupInvites = level });

        var byStranger = await stranger.Client.PostAs<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "S", MemberIds = [target.Id] });
        var byFriend = await friend.Client.PostAs<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "F", MemberIds = [target.Id] });

        if (strangerMayAdd)
            byStranger.Failed.ShouldBeEmpty();
        else
            byStranger.Failed.ShouldHaveSingleItem().ShouldBe(new AddMemberFailureDto { UserId = target.Id, Code = ErrorCodes.PrivacyRestricted });
        // Nobody behaves like Contacts: the target's own contacts may still add them.
        byFriend.Failed.ShouldBeEmpty();
        byFriend.Chat.MemberCount.ShouldBe(2);
    }

    [Fact]
    public async Task Blocks_in_either_direction_prevent_adding_to_a_group()
    {
        var target = await factory.NewUserAsync();
        var blockedByTarget = await factory.NewUserAsync();
        var blocker = await factory.NewUserAsync();
        await target.BlockAsync(blockedByTarget);
        await blocker.BlockAsync(target);

        var first = await blockedByTarget.Client.PostAs<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "A", MemberIds = [target.Id] });
        var second = await blocker.Client.PostAs<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "B", MemberIds = [target.Id] });

        first.Failed.ShouldHaveSingleItem().Code.ShouldBe(ErrorCodes.UserBlocked);
        second.Failed.ShouldHaveSingleItem().Code.ShouldBe(ErrorCodes.UserBlocked);
    }

    [Fact]
    public async Task Realtime_connections_stamp_session_activity_at_most_every_five_minutes()
    {
        var user = await factory.NewUserAsync();
        var longAgo = DateTimeOffset.UtcNow.AddHours(-3);
        await SetLastActiveAsync(user.SessionId, longAgo);

        await using (await RealtimeProbe.ConnectAsync(user))
        {
            var stamped = (await factory.LoadSessionAsync(user.SessionId)).LastActiveAt;
            (DateTimeOffset.UtcNow - stamped).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
        }

        // Within the interval another connection does not write again.
        await SetLastActiveAsync(user.SessionId, longAgo);
        await using (await RealtimeProbe.ConnectAsync(user))
        {
            (await factory.LoadSessionAsync(user.SessionId)).LastActiveAt.ShouldBe(longAgo, TimeSpan.FromMilliseconds(1));
        }
    }

    private async Task<(Guid UserId, Guid SessionId)?> ValidateAsync(string token, Guid mediaId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISessionValidator>().ValidateMediaSignatureAsync(token, mediaId, Ct);
    }

    private Task SetLastActiveAsync(Guid sessionId, DateTimeOffset at) =>
        factory.WithDbAsync(db => db.Sessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActiveAt, at), Ct));
}
