using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>The messaging rows of the realtime event matrix: who receives what (SPEC §7).</summary>
[Collection(ApiCollection.Name)]
public sealed class RealtimeMatrixTests(ApiFactory factory)
{
    [Fact]
    public async Task Group_events_reach_every_member_device_and_never_outsiders()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var former = await factory.NewUserAsync("Former");
        var outsider = await factory.NewUserAsync("Outsider");
        var newcomer = await factory.NewUserAsync("Newcomer");
        var chat = await owner.NewGroupAsync(bob, former);
        await owner.RemoveMemberAsync(chat, former.Id);
        var ownerPhone = await owner.SecondDeviceAsync();
        await using var ownerRt = await owner.ConnectAsync();
        await using var phoneRt = await ownerPhone.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();
        await using var formerRt = await former.ConnectAsync();
        await using var outsiderRt = await outsider.ConnectAsync();
        await using var newcomerRt = await newcomer.ConnectAsync();
        var members = new[] { ownerRt, phoneRt, bobRt };

        // MessageCreated: everyone in the chat, including the sender's other device.
        var message = await owner.SendAsync(chat, "hello");
        foreach (var rt in members) await rt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == message.Id);

        // MessageUpdated: edit, reaction, pin/unpin.
        await owner.EditAsync(chat, message.Id, "hello all");
        foreach (var rt in members) await rt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id && m.Text == "hello all");
        await bob.ReactAsync(chat, message.Id, "👍");
        foreach (var rt in members) await rt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id && m.Reactions.Count == 1);
        await owner.PinAsync(chat, message.Id);
        var pinnedAt = DateTimeOffset.MinValue;
        foreach (var rt in members)
        {
            pinnedAt = (await rt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id && m.IsPinned)).UpdatedAt;
            await rt.WaitAsync<ChatRefEvent>(Ev.PinnedMessagesChanged, e => e.ChatId == chat);
            await rt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.MessagePinned);
        }
        await owner.UnpinAsync(chat, message.Id);
        foreach (var rt in members) await rt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id && !m.IsPinned && m.UpdatedAt > pinnedAt);

        // MessagesRead: every member, including the reader's other devices.
        var bobs = await bob.SendAsync(chat, "read me");
        await owner.MarkReadAsync(chat, bobs.Seq);
        foreach (var rt in members) await rt.WaitAsync<MessagesReadEvent>(Ev.MessagesRead, e => e.ChatId == chat && e.UserId == owner.Id && e.UpToSeq == bobs.Seq);

        // ChatUpdated: info changes.
        await owner.UpdateChatAsync(chat, new UpdateChatRequest { Title = "Renamed" });
        foreach (var rt in members) await rt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat && c.Title == "Renamed");

        // ChatAdded to the newcomer only; ChatMembersChanged + ChatUpdated to all current members.
        await owner.AddMembersAsync(chat, newcomer.Id);
        await newcomerRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat);
        foreach (var rt in members.Append(newcomerRt))
        {
            await rt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
            await rt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat && c.MemberCount == 3);
        }

        // MessagesDeleted for everyone: all members (the newcomer too).
        await owner.DeleteMessagesAsync(chat, forEveryone: true, bobs.Id);
        foreach (var rt in members.Append(newcomerRt))
            await rt.WaitAsync<MessagesDeletedEvent>(Ev.MessagesDeleted, e => e.ChatId == chat && e.ForEveryone && e.MessageIds.Contains(bobs.Id));

        // Per-user events stay with the user.
        await bob.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { IsArchived = true });
        await bobRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, e => e.ChatId == chat && e.Settings.IsArchived);
        await bob.DeleteMessagesAsync(chat, forEveryone: false, message.Id);
        await bobRt.WaitAsync<MessagesDeletedEvent>(Ev.MessagesDeleted, e => e.ChatId == chat && !e.ForEveryone);
        await bob.ClearHistoryAsync(chat);
        await bobRt.WaitAsync<HistoryClearedEvent>(Ev.HistoryCleared, e => e.ChatId == chat && !e.ForEveryone);

        foreach (var rt in new[] { ownerRt, phoneRt, newcomerRt, formerRt, outsiderRt }) await rt.BarrierAsync();
        foreach (var rt in new[] { ownerRt, phoneRt, newcomerRt })
        {
            rt.All<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, e => e.ChatId == chat).ShouldBeEmpty();
            rt.All<MessagesDeletedEvent>(Ev.MessagesDeleted, e => e.ChatId == chat && !e.ForEveryone).ShouldBeEmpty();
            rt.All<HistoryClearedEvent>(Ev.HistoryCleared, e => e.ChatId == chat).ShouldBeEmpty();
        }
        foreach (var rt in members) rt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat).ShouldBeEmpty();
        // Nothing about the chat leaks to people who are not (or no longer) in it.
        formerRt.EventsFor(chat).ShouldBeEmpty();
        outsiderRt.EventsFor(chat).ShouldBeEmpty();
        // The newcomer heard nothing from before they joined.
        newcomerRt.All<MessageDto>(Ev.MessageUpdated, m => m.ChatId == chat).ShouldBeEmpty();
        newcomerRt.All<MessagesReadEvent>(Ev.MessagesRead, e => e.ChatId == chat).ShouldBeEmpty();
    }

    [Fact]
    public async Task Removal_and_deletion_events_go_to_the_affected_users()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await owner.NewGroupAsync(bob, carol);
        var bobPhone = await bob.SecondDeviceAsync();
        await using var ownerRt = await owner.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();
        await using var bobPhoneRt = await bobPhone.ConnectAsync();
        await using var carolRt = await carol.ConnectAsync();

        await owner.RemoveMemberAsync(chat, bob.Id);
        foreach (var rt in new[] { bobRt, bobPhoneRt }) await rt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        foreach (var rt in new[] { ownerRt, carolRt })
        {
            await rt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
            await rt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat && c.MemberCount == 2);
        }

        await carol.LeaveAsync(chat);
        await carolRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        await ownerRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat && c.MemberCount == 1);

        await owner.DeleteChatAsync(chat, forEveryone: true);
        await ownerRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);

        foreach (var rt in new[] { ownerRt, bobRt, carolRt }) await rt.BarrierAsync();
        ownerRt.All<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat).Count.ShouldBe(1);
        bobRt.All<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat).Count.ShouldBe(1);
        carolRt.All<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat).Count.ShouldBe(1);
        bobRt.All<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat).ShouldBeEmpty();
        carolRt.All<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat && c.MemberCount == 1).ShouldBeEmpty();
    }

    [Fact]
    public async Task Private_chat_events_reach_both_sides()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        var hello = await alice.SendAsync(chat, "hello");
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat);
        var reply = await bob.SendAsync(chat, "hi", replyTo: hello.Id);
        await bob.MarkReadAsync(chat, reply.Seq);
        await alice.ReactAsync(chat, reply.Id, "❤️");
        await alice.PinAsync(chat, reply.Id);
        await bob.DeleteMessagesAsync(chat, forEveryone: true, hello.Id);

        foreach (var rt in new[] { aliceRt, bobRt })
        {
            await rt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == hello.Id);
            (await rt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == reply.Id)).ReplyTo.ShouldNotBeNull().MessageId.ShouldBe(hello.Id);
            await rt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == reply.Id && m.Reactions.Count == 1);
            await rt.WaitAsync<ChatRefEvent>(Ev.PinnedMessagesChanged, e => e.ChatId == chat);
            await rt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.MessagePinned);
            await rt.WaitAsync<MessagesDeletedEvent>(Ev.MessagesDeleted, e => e.ChatId == chat && e.ForEveryone && e.MessageIds.Contains(hello.Id));
        }
        // Bob's read receipt for his own reply is a no-op (sending already marked it read).
        await aliceRt.BarrierAsync();
        aliceRt.All<MessagesReadEvent>(Ev.MessagesRead, e => e.ChatId == chat).ShouldBeEmpty();
        var last = await bob.SendAsync(chat, "last one");
        await alice.MarkReadAsync(chat, last.Seq);
        foreach (var rt in new[] { aliceRt, bobRt })
        {
            var read = await rt.WaitAsync<MessagesReadEvent>(Ev.MessagesRead, e => e.ChatId == chat);
            read.UserId.ShouldBe(alice.Id);
            read.UpToSeq.ShouldBe(last.Seq);
        }
    }

    [Fact]
    public async Task A_file_uploaded_through_the_media_api_can_be_sent_and_its_access_follows_the_message()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        var bytes = "%PDF-1.4 sky message test file"u8.ToArray();
        var upload = await alice.PostAsync<CreateUploadRequest, UploadSessionDto>(ApiRoutes.Media.Uploads, new CreateUploadRequest
        {
            Kind = MediaKind.File,
            FileName = "notes.pdf",
            ContentType = "application/pdf",
            SizeBytes = bytes.Length,
        });
        upload.ChunkCount.ShouldBe(1);
        using (var chunk = new ByteArrayContent(bytes))
        {
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await alice.Client.PutAsync(ApiRoutes.Media.UploadChunkPath(upload.MediaId, 0), chunk, TestKit.Ct);
            await put.ShouldSucceedAsync();
        }
        await (await alice.PostEmptyAsync(ApiRoutes.Media.UploadCompletePath(upload.MediaId))).ShouldSucceedAsync();
        (await aliceRt.WaitAsync<MediaDto>(Ev.MediaProcessed, m => m.Id == upload.MediaId)).Status.ShouldBe(MediaStatus.Ready);

        // Not attached anywhere yet: the peer cannot see it.
        await (await bob.GetRawAsync(ApiRoutes.Media.ByIdPath(upload.MediaId))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        var sent = await alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "the notes", MediaIds = [upload.MediaId] });
        sent.Type.ShouldBe(MessageType.File);
        var received = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == sent.Id);
        var attachment = received.Attachments.Single();
        attachment.Id.ShouldBe(upload.MediaId);
        attachment.FileName.ShouldBe("notes.pdf");
        attachment.SizeBytes.ShouldBe(bytes.Length);
        (await bob.GetAsync<MediaDto>(ApiRoutes.Media.ByIdPath(upload.MediaId))).Status.ShouldBe(MediaStatus.Ready);
        using (var content = await bob.GetRawAsync(ApiRoutes.Media.ContentPath(upload.MediaId, signature: null)))
        {
            await content.ShouldSucceedAsync();
            (await content.Content.ReadAsByteArrayAsync(TestKit.Ct)).ShouldBe(bytes);
        }
        await (await carol.GetRawAsync(ApiRoutes.Media.ByIdPath(upload.MediaId))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // Bob cannot re-attach Alice's media in a message of his own...
        await (await bob.TrySendAsync(chat, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [upload.MediaId] }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        // ...but forwarding keeps the attachment (and the access that comes with it).
        var forwarded = (await bob.ForwardAsync(bob.SavedChatId, new ForwardMessagesRequest
        {
            FromChatId = chat,
            MessageIds = [sent.Id],
            ClientMessageIds = [Guid.NewGuid()],
        })).Single();
        forwarded.Attachments.Single().Id.ShouldBe(upload.MediaId);
    }
}
