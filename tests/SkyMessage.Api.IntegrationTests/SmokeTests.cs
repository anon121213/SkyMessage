using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.Persistence;
using Xunit;

namespace SkyMessage.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SmokeTests(ApiFactory factory)
{
    [Fact]
    public async Task Health_endpoint_is_up()
    {
        var response = await factory.CreateJsonClient().GetAsync("/" + ApiRoutes.Health, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Model_round_trips_json_and_owned_columns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var user = new User { Username = "Smoke_" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Smoke", PasswordHash = "x", CreatedAt = now, UpdatedAt = now };
        user.UsernameNormalized = User.Normalize(user.Username);
        var chat = new Chat { Type = ChatType.Saved, PrivateKey = Chat.SavedKeyFor(user.Id), CreatedAt = now, UpdatedAt = now, LastActivityAt = now, MemberCount = 1 };
        chat.Members.Add(new ChatMember { UserId = user.Id, Role = ChatRole.Owner, JoinedAt = now });
        var message = new Message
        {
            ChatId = chat.Id, Seq = 1, SenderId = user.Id, Type = MessageType.System, Text = "hello", CreatedAt = now, UpdatedAt = now,
            Entities = [new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 5 }],
            SystemEvent = new SystemEvent { Type = SystemEventType.MembersAdded, ActorId = user.Id, TargetUserIds = [user.Id] },
        };
        db.AddRange(user, chat, message);
        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        var loaded = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == message.Id, ct);
        loaded.Entities.Single().Type.ShouldBe(MessageEntityType.Bold);
        loaded.SystemEvent!.TargetUserIds.ShouldBe([user.Id]);
        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, ct)).Privacy.FindByPhone.ShouldBe(PrivacyLevel.Nobody);

        var hits = await db.Messages.Where(m => EF.Functions.ILike(m.Text!, "%ell%")).CountAsync(ct);
        hits.ShouldBeGreaterThan(0);
    }
}
