using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary><see cref="ISystemMessageService"/>: service messages and call logs posted by other features (SPEC §6.3).</summary>
[Collection(ApiCollection.Name)]
public sealed class SystemMessageTests(ApiFactory factory)
{
    private async Task<MessageDto> PostCallLogAsync(Guid chatId, CallInfoDto call)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISystemMessageService>().PostCallLogAsync(chatId, call, TestKit.Ct);
    }

    [Fact]
    public async Task A_call_is_logged_once_and_brings_a_hidden_chat_back()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();
        var call = new CallInfoDto { CallId = Guid.NewGuid(), IsVideo = true, Status = CallStatus.Missed, DurationSeconds = -3, InitiatorId = alice.Id };

        // Hang-up and webhook may log the same call at the same time.
        var logs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostCallLogAsync(chat, call)));

        logs.Select(l => l.Id).Distinct().Count().ShouldBe(1);
        var log = logs[0];
        log.Type.ShouldBe(MessageType.Call);
        log.SenderId.ShouldBe(alice.Id);
        log.Seq.ShouldBe(1);
        var info = log.Call.ShouldNotBeNull();
        info.CallId.ShouldBe(call.CallId);
        info.Status.ShouldBe(CallStatus.Missed);
        info.IsVideo.ShouldBeTrue();
        info.DurationSeconds.ShouldBe(0);
        info.InitiatorId.ShouldBe(alice.Id);

        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat);
        await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == log.Id);
        await bobRt.BarrierAsync();
        await aliceRt.BarrierAsync();
        bobRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat).Count.ShouldBe(1);
        aliceRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat).Count.ShouldBe(1);
        bobRt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat).Count.ShouldBe(1);
        (await factory.WithDbAsync(db => db.Messages.CountAsync(m => m.ChatId == chat))).ShouldBe(1);

        // The caller placed the call: not unread for them, unread for the callee.
        (await alice.ChatAsync(chat)).UnreadCount.ShouldBe(0);
        var bobs = (await bob.ChatsAsync()).Single(c => c.Id == chat);
        bobs.UnreadCount.ShouldBe(1);
        bobs.LastMessage.ShouldNotBeNull().Type.ShouldBe(MessageType.Call);
        bobs.LastMessageSender.ShouldNotBeNull().Id.ShouldBe(alice.Id);

        // Call logs are history, not content: not editable, not forwardable, but they can be reacted to.
        await (await alice.TryEditAsync(chat, log.Id, "edited")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.MessageNotEditable);
        (await alice.ForwardAsync(alice.SavedChatId, new ForwardMessagesRequest { FromChatId = chat, MessageIds = [log.Id], ClientMessageIds = [Guid.NewGuid()] }))
            .ShouldBeEmpty();
        (await bob.ReactAsync(chat, log.Id, "😢")).Reactions.Single().Emoji.ShouldBe("😢");

        // A different call is a new entry.
        var next = await PostCallLogAsync(chat, call with { CallId = Guid.NewGuid(), Status = CallStatus.Ended, DurationSeconds = 65, InitiatorId = bob.Id });
        next.Seq.ShouldBe(2);
        next.Call.ShouldNotBeNull().DurationSeconds.ShouldBe(65);
        (await bob.ChatAsync(chat)).UnreadCount.ShouldBe(0); // Bob placed it, which also marks the older entry read
    }

    [Fact]
    public async Task Service_messages_mark_the_actor_read_and_skip_empty_targets()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        await bob.SendAsync(chat, "unread for the owner");

        await using var scope = factory.Services.CreateAsyncScope();
        var posted = await scope.ServiceProvider.GetRequiredService<ISystemMessageService>().PostSystemEventAsync(chat, new SystemEventDto
        {
            Type = SystemEventType.MembersAdded,
            ActorId = owner.Id,
            TargetUserIds = [bob.Id, Guid.Empty, bob.Id],
        }, TestKit.Ct);

        posted.Type.ShouldBe(MessageType.System);
        posted.SenderId.ShouldBeNull();
        posted.SystemEvent.ShouldNotBeNull().TargetUserIds.ShouldBe([bob.Id]);
        (await owner.ChatAsync(chat)).UnreadCount.ShouldBe(0);
        (await bob.ChatAsync(chat)).UnreadCount.ShouldBe(1); // Bob's own message marked ChatCreated read
    }

    [Fact]
    public async Task Service_messages_refuse_to_run_inside_the_callers_transaction()
    {
        var owner = await factory.NewUserAsync("Owner");
        var chat = await owner.NewGroupAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ISystemMessageService>();
        await using (await db.Database.BeginTransactionAsync(TestKit.Ct))
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                service.PostSystemEventAsync(chat, new SystemEventDto { Type = SystemEventType.TitleChanged, ActorId = owner.Id, Text = "x" }, TestKit.Ct));
        }
        (await owner.PageAsync(chat)).Messages.Count.ShouldBe(1);
    }
}
