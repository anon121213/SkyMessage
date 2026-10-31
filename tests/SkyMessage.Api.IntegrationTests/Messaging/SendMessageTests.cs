using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>POST .../messages: validation, idempotency, type inference, replies and permissions (SPEC §6.3).</summary>
[Collection(ApiCollection.Name)]
public sealed class SendMessageTests(ApiFactory factory)
{
    [Fact]
    public async Task Client_message_id_is_required()
    {
        var alice = await factory.NewUserAsync("Alice");
        using var response = await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.Empty, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestKit.Ct));
        problem.RootElement.GetProperty("code").GetString().ShouldBe(ErrorCodes.Validation);
        problem.RootElement.GetProperty("errors").TryGetProperty("clientMessageId", out _).ShouldBeTrue();
        (await alice.ChatAsync(alice.SavedChatId)).LastMessageSeq.ShouldBe(0);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\t \r\n")]
    [InlineData("​​⁠")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Blank_text_without_media_is_empty(string? text)
    {
        var alice = await factory.NewUserAsync("Alice");
        await (await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = text }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MessageEmpty);
    }

    [Fact]
    public async Task Text_length_and_characters_are_limited()
    {
        var alice = await factory.NewUserAsync("Alice");
        var longest = await alice.SendAsync(alice.SavedChatId, new string('a', Limits.MessageTextMaxLength));
        longest.Text!.Length.ShouldBe(Limits.MessageTextMaxLength);

        await (await alice.TrySendAsync(alice.SavedChatId, MessagingApi.Text(new string('a', Limits.MessageTextMaxLength + 1))))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TrySendAsync(alice.SavedChatId, MessagingApi.Text("nul\0byte")))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        // A lone surrogate cannot even be produced by a JSON serializer: send the escape by hand.
        await (await alice.PostJsonTextAsync(ApiRoutes.Messages.RootPath(alice.SavedChatId),
                $$"""{"clientMessageId":"{{Guid.NewGuid()}}","text":"broken \uD83D surrogate"}"""))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Leading/trailing whitespace of real text is kept as typed.
        (await alice.SendAsync(alice.SavedChatId, "  indented")).Text.ShouldBe("  indented");
        (await alice.SendAsync(alice.SavedChatId, "emoji 👍🏽 ok")).Text.ShouldBe("emoji 👍🏽 ok");
    }

    public static TheoryData<string> BadEntities => new()
    {
        "out-of-bounds",
        "zero-length",
        "negative-offset",
        "unknown-type",
        "empty-mention",
        "bad-language",
        "too-many",
        "no-text",
    };

    [Theory]
    [MemberData(nameof(BadEntities))]
    public async Task Entities_must_fit_the_text(string kind)
    {
        var alice = await factory.NewUserAsync("Alice");
        const string text = "hello world";
        var request = kind switch
        {
            "out-of-bounds" => MessagingApi.Text(text, entities: [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 6, Length = 6 }]),
            "zero-length" => MessagingApi.Text(text, entities: [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 0 }]),
            "negative-offset" => MessagingApi.Text(text, entities: [new MessageEntityDto { Type = MessageEntityType.Italic, Offset = -1, Length = 2 }]),
            "unknown-type" => MessagingApi.Text(text, entities: [new MessageEntityDto { Type = (MessageEntityType)99, Offset = 0, Length = 2 }]),
            "empty-mention" => MessagingApi.Text(text, entities: [new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 0, Length = 5, UserId = Guid.Empty }]),
            "bad-language" => MessagingApi.Text(text, entities: [new MessageEntityDto { Type = MessageEntityType.Pre, Offset = 0, Length = 5, Language = "c sharp" }]),
            "too-many" => MessagingApi.Text(text, entities: Enumerable.Range(0, Limits.MaxEntitiesPerMessage + 1)
                .Select(_ => new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 1 }).ToList()),
            _ => new SendMessageRequest
            {
                ClientMessageId = Guid.NewGuid(),
                MediaIds = [await factory.CreateMediaAsync(alice, MediaKind.Photo)],
                Entities = [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 1 }],
            },
        };
        await (await alice.TrySendAsync(alice.SavedChatId, request)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Valid_entities_are_stored_with_only_their_meaningful_fields()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        const string text = "bold code link @bob";
        var sent = await alice.SendAsync(chat, text, entities:
        [
            new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 4, Url = "https://ignored.example", UserId = bob.Id },
            new MessageEntityDto { Type = MessageEntityType.Pre, Offset = 5, Length = 4, Language = "csharp" },
            new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 10, Length = 4, Url = "https://example.com/a?b=c" },
            new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 15, Length = 4, UserId = bob.Id },
            new MessageEntityDto { Type = MessageEntityType.Url, Offset = 10, Length = 4 },
        ]);

        var entities = sent.Entities.ShouldNotBeNull();
        entities.Count.ShouldBe(5);
        entities[0].ShouldBe(new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 4 });
        entities[1].Language.ShouldBe("csharp");
        entities[2].Url.ShouldBe("https://example.com/a?b=c");
        entities[3].UserId.ShouldBe(bob.Id);
        (await bob.GetMessageAsync(chat, sent.Id)).Entities.ShouldBe(entities);
        (await bob.ChatAsync(chat)).UnreadMentionCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://example.com/path?q=1#frag")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/UPPER")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+66812345678")]
    [InlineData("https://пример.рф/путь")]
    public async Task Text_links_accept_web_mail_and_phone_urls(string url)
    {
        var alice = await factory.NewUserAsync("Alice");
        var sent = await alice.SendAsync(alice.SavedChatId, "click here", entities:
            [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = url }]);
        sent.Entities.ShouldNotBeNull().Single().Url.ShouldBe(url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("//example.com/protocol-relative")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://google.com@evil.example")]
    [InlineData("https://exa mple.com")]
    [InlineData(" https://example.com")]
    [InlineData("https://example.com/\nnext")]
    [InlineData("https://")]
    [InlineData("mailto:")]
    [InlineData("tel:")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("too-long")]
    public async Task Text_links_reject_everything_else(string? url)
    {
        if (url == "too-long") url = "https://example.com/" + new string('a', Limits.LinkUrlMaxLength - "https://example.com/".Length + 1);
        var alice = await factory.NewUserAsync("Alice");
        await (await alice.TrySendAsync(alice.SavedChatId, MessagingApi.Text("click here", entities:
                [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = url }])))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        // The same rules apply to edits.
        var plain = await alice.SendAsync(alice.SavedChatId, "click here");
        await (await alice.TryEditAsync(alice.SavedChatId, plain.Id, "click here",
                [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = url }]))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Link_of_exactly_the_maximum_length_is_accepted()
    {
        var alice = await factory.NewUserAsync("Alice");
        var url = "https://example.com/" + new string('a', Limits.LinkUrlMaxLength - "https://example.com/".Length);
        url.Length.ShouldBe(Limits.LinkUrlMaxLength);
        var sent = await alice.SendAsync(alice.SavedChatId, "long", entities:
            [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 4, Url = url }]);
        sent.Entities.ShouldNotBeNull().Single().Url.ShouldBe(url);
    }

    [Fact]
    public async Task Retrying_a_send_returns_the_original_message()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await using var bobRt = await bob.ConnectAsync();
        var clientId = Guid.NewGuid();

        var first = await alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = clientId, Text = "only once" });
        first.ClientMessageId.ShouldBe(clientId);
        first.Seq.ShouldBe(1);
        var retry = await alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = clientId, Text = "only once" });
        (retry.Id, retry.Seq, retry.Text, retry.CreatedAt, retry.UpdatedAt).ShouldBe((first.Id, first.Seq, first.Text, first.CreatedAt, first.UpdatedAt));
        // "Whatever changed since": the first copy wins even if the retry differs.
        var changed = await alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = clientId, Text = "edited locally" });
        changed.Id.ShouldBe(first.Id);
        changed.Text.ShouldBe("only once");

        // No seq was burnt by the retries.
        (await alice.SendAsync(chat, "next")).Seq.ShouldBe(2);
        await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.Seq == 2);
        bobRt.All<MessageDto>(Ev.MessageCreated, m => m.Id == first.Id).Count.ShouldBe(1);

        // The idempotency key belongs to one chat.
        await (await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = clientId, Text = "elsewhere" }))
            .ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.Conflict);
        // Another sender may use the same client id.
        (await bob.SendAsync(chat, new SendMessageRequest { ClientMessageId = clientId, Text = "mine" })).Seq.ShouldBe(3);
    }

    [Fact]
    public async Task Outbox_retries_succeed_even_after_sending_became_forbidden()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var group = await owner.NewGroupAsync(bob);
        var inGroup = new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "sent just before" };
        var original = await bob.SendAsync(group, inGroup);
        await owner.SetPermissionsAsync(group, new ChatPermissionsDto { SendMessages = false });

        // The response of the first attempt was lost; the retry must not turn a delivered message into a failure.
        (await bob.SendAsync(group, inGroup)).Id.ShouldBe(original.Id);
        await (await bob.TrySendAsync(group, MessagingApi.Text("a new one"))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);

        var carol = await factory.NewUserAsync("Carol");
        var chat = (await bob.OpenPrivateAsync(carol.Id)).Id;
        var inPrivate = new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "hello" };
        var first = await bob.SendAsync(chat, inPrivate);
        await carol.BlockAsync(bob.Id);
        (await bob.SendAsync(chat, inPrivate)).Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Concurrent_duplicates_create_one_message_and_one_event()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await using var bobRt = await bob.ConnectAsync();
        var clientId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = clientId, Text = "racing" })));

        results.Select(r => r.Id).Distinct().Count().ShouldBe(1);
        results.ShouldAllBe(r => r.Seq == 1);
        (await factory.WithDbAsync(db => db.Messages.CountAsync(m => m.ChatId == chat))).ShouldBe(1);
        (await alice.ChatAsync(chat)).LastMessageSeq.ShouldBe(1);
        (await alice.SendAsync(chat, "after the race")).Seq.ShouldBe(2);

        await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.Seq == 2);
        bobRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat).Select(m => m.Seq).ShouldBe([1L, 2L]);
        (await bob.ChatAsync(chat)).UnreadCount.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_duplicates_across_two_chats_are_one_message_and_conflicts()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var clientId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            alice.TrySendAsync(i % 2 == 0 ? chat : alice.SavedChatId, new SendMessageRequest { ClientMessageId = clientId, Text = "where?" })));
        try
        {
            var ok = new List<MessageDto>();
            foreach (var response in responses)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                    ok.Add((await response.Content.ReadFromJsonAsync<MessageDto>(SkyJson.Options, TestKit.Ct))!);
                else
                    response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(TestKit.Ct));
            }
            ok.ShouldNotBeEmpty();
            ok.Select(m => m.Id).Distinct().Count().ShouldBe(1);
            var winnerChat = ok[0].ChatId;
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(5); // every request to the winning chat
            (await factory.WithDbAsync(db => db.Messages.CountAsync(m => m.SenderId == alice.Id && m.ClientMessageId == clientId))).ShouldBe(1);
            (await alice.ChatAsync(winnerChat)).LastMessageSeq.ShouldBe(1);
            (await alice.ChatAsync(winnerChat == chat ? alice.SavedChatId : chat)).LastMessageSeq.ShouldBe(0);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Concurrent_sends_get_gap_free_unique_seqs()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.OpenPrivateAsync(alice.Id);

        var sent = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? alice : bob).SendAsync(chat, $"parallel {i}")));

        sent.Select(m => m.Seq).Order().ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
        var page = await bob.PageAsync(chat);
        page.Messages.Select(m => m.Seq).ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
        (await alice.ChatAsync(chat)).LastMessage.ShouldNotBeNull().Seq.ShouldBe(20);
    }

    [Fact]
    public async Task Message_type_is_inferred_from_the_attachments()
    {
        var alice = await factory.NewUserAsync("Alice");
        var chat = alice.SavedChatId;
        Task<Guid> Media(MediaKind kind) => factory.CreateMediaAsync(alice, kind);

        async Task Expect(MessageType type, params MediaKind[] kinds)
        {
            var ids = new List<Guid>();
            foreach (var kind in kinds) ids.Add(await Media(kind));
            var sent = await alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = ids });
            sent.Type.ShouldBe(type, string.Join("+", kinds));
            sent.Attachments.Select(a => a.Id).ShouldBe(ids); // request order is kept
            sent.Attachments.ShouldAllBe(a => a.Status == MediaStatus.Ready);
        }

        await Expect(MessageType.Photo, MediaKind.Photo);
        await Expect(MessageType.Video, MediaKind.Video);
        await Expect(MessageType.Album, MediaKind.Photo, MediaKind.Photo);
        await Expect(MessageType.Album, MediaKind.Video, MediaKind.Photo, MediaKind.Video);
        await Expect(MessageType.Voice, MediaKind.Voice);
        await Expect(MessageType.VideoNote, MediaKind.VideoNote);
        await Expect(MessageType.File, MediaKind.File);
        await Expect(MessageType.File, MediaKind.File, MediaKind.Audio);
        await Expect(MessageType.Audio, MediaKind.Audio);
        await Expect(MessageType.Audio, MediaKind.Audio, MediaKind.Audio);
        await Expect(MessageType.Album, Enumerable.Repeat(MediaKind.Photo, Limits.MaxAttachmentsPerMessage).ToArray());

        var captioned = await alice.SendAsync(chat, new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = "look at this",
            MediaIds = [await Media(MediaKind.Photo)],
        });
        captioned.Type.ShouldBe(MessageType.Photo);
        captioned.Text.ShouldBe("look at this");
        var blankCaption = await alice.SendAsync(chat, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "  ", MediaIds = [await Media(MediaKind.File)] });
        blankCaption.Text.ShouldBeNull();
        blankCaption.Type.ShouldBe(MessageType.File);
    }

    [Theory]
    [InlineData(MediaKind.Voice, MediaKind.Photo)]
    [InlineData(MediaKind.Voice, MediaKind.Voice)]
    [InlineData(MediaKind.VideoNote, MediaKind.File)]
    [InlineData(MediaKind.VideoNote, MediaKind.VideoNote)]
    [InlineData(MediaKind.Photo, MediaKind.File)]
    [InlineData(MediaKind.Video, MediaKind.Audio)]
    public async Task Incompatible_attachments_are_rejected(MediaKind first, MediaKind second)
    {
        var alice = await factory.NewUserAsync("Alice");
        var ids = new[] { await factory.CreateMediaAsync(alice, first), await factory.CreateMediaAsync(alice, second) };
        await (await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = ids }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        (await alice.ChatAsync(alice.SavedChatId)).LastMessageSeq.ShouldBe(0);
    }

    [Fact]
    public async Task Attachments_must_be_my_own_ready_distinct_media()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = alice.SavedChatId;
        var photo = await factory.CreateMediaAsync(alice, MediaKind.Photo);

        Task<HttpResponseMessage> Send(params Guid[] ids) =>
            alice.TrySendAsync(chat, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = ids });

        await (await Send(await factory.CreateMediaAsync(bob, MediaKind.Photo))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await Send(Guid.NewGuid())).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await Send(await factory.CreateMediaAsync(alice, MediaKind.Photo, MediaStatus.Uploading))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaNotReady);
        await (await Send(await factory.CreateMediaAsync(alice, MediaKind.Photo, MediaStatus.Processing))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaNotReady);
        await (await Send(await factory.CreateMediaAsync(alice, MediaKind.Photo, MediaStatus.Failed))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaUnsupported);
        await (await Send(photo, photo)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await Send(Guid.Empty)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var eleven = new List<Guid>();
        for (var i = 0; i < Limits.MaxAttachmentsPerMessage + 1; i++) eleven.Add(await factory.CreateMediaAsync(alice, MediaKind.Photo));
        await (await Send([.. eleven])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        (await alice.ChatAsync(chat)).LastMessageSeq.ShouldBe(0);
        (await alice.SendMediaAsync(chat, photo)).Attachments.Single().Id.ShouldBe(photo);
    }

    [Fact]
    public async Task Group_permissions_limit_what_members_may_send()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);

        await owner.SetPermissionsAsync(chat, new ChatPermissionsDto { SendMessages = false });
        await (await bob.TrySendAsync(chat, MessagingApi.Text("may I?"))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);
        (await owner.SendAsync(chat, "announcement")).Type.ShouldBe(MessageType.Text);

        await owner.SetPermissionsAsync(chat, new ChatPermissionsDto { SendMessages = true, SendMedia = false });
        (await bob.SendAsync(chat, "text is fine")).SenderId.ShouldBe(bob.Id);
        var bobPhoto = await factory.CreateMediaAsync(bob, MediaKind.Photo);
        await (await bob.TrySendAsync(chat, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [bobPhoto] }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);
        (await owner.SendMediaAsync(chat, await factory.CreateMediaAsync(owner, MediaKind.Photo))).Type.ShouldBe(MessageType.Photo);

        await owner.SetRoleAsync(chat, bob.Id, ChatRole.Admin);
        (await bob.SendMediaAsync(chat, bobPhoto)).Type.ShouldBe(MessageType.Photo);
    }

    [Fact]
    public async Task Replies_must_target_a_visible_message_of_the_same_chat()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var longText = string.Concat(Enumerable.Repeat("0123456789", 20));
        var original = await bob.SendAsync(chat, longText);
        var hiddenForAlice = await bob.SendAsync(chat, "hidden for Alice");
        var deleted = await bob.SendAsync(chat, "deleted for everyone");
        var elsewhere = await alice.SendAsync(alice.SavedChatId, "in another chat");
        await alice.DeleteMessagesAsync(chat, forEveryone: false, hiddenForAlice.Id);
        await bob.DeleteMessagesAsync(chat, forEveryone: true, deleted.Id);

        var reply = await alice.SendAsync(chat, "replying", replyTo: original.Id);
        var preview = reply.ReplyTo.ShouldNotBeNull();
        preview.MessageId.ShouldBe(original.Id);
        preview.Seq.ShouldBe(original.Seq);
        preview.SenderId.ShouldBe(bob.Id);
        preview.Type.ShouldBe(MessageType.Text);
        preview.Text.ShouldBe(longText[..Limits.ReplyPreviewTextLength]);
        preview.IsDeleted.ShouldBeFalse();
        (await bob.ChatAsync(chat)).UnreadMentionCount.ShouldBe(1); // a reply to Bob concerns Bob

        await (await alice.TrySendAsync(chat, MessagingApi.Text("x", replyTo: elsewhere.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.TrySendAsync(chat, MessagingApi.Text("x", replyTo: hiddenForAlice.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.TrySendAsync(chat, MessagingApi.Text("x", replyTo: Guid.NewGuid()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.TrySendAsync(chat, MessagingApi.Text("x", replyTo: Guid.Empty))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Bob still sees his message, so he may reply to it.
        (await bob.SendAsync(chat, "me too", replyTo: hiddenForAlice.Id)).ReplyTo.ShouldNotBeNull().MessageId.ShouldBe(hiddenForAlice.Id);
        // The target vanished for everyone: the reply goes out as a plain message.
        (await alice.SendAsync(chat, "too late", replyTo: deleted.Id)).ReplyTo.ShouldBeNull();

        // Deleting the original later turns the preview into a tombstone preview.
        await bob.DeleteMessagesAsync(chat, forEveryone: true, original.Id);
        var reloaded = await alice.GetMessageAsync(chat, reply.Id);
        reloaded.ReplyTo.ShouldNotBeNull().IsDeleted.ShouldBeTrue();
        reloaded.ReplyTo.Text.ShouldBeNull();

        // Messages before my clear-history mark are gone for me, also as reply targets.
        var old = await bob.SendAsync(chat, "old");
        await alice.ClearHistoryAsync(chat);
        await (await alice.TrySendAsync(chat, MessagingApi.Text("x", replyTo: old.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
    }

    [Fact]
    public async Task Reply_preview_shows_the_first_attachment()
    {
        var alice = await factory.NewUserAsync("Alice");
        var photo = await factory.CreateMediaAsync(alice, MediaKind.Photo);
        var second = await factory.CreateMediaAsync(alice, MediaKind.Video);
        var album = await alice.SendMediaAsync(alice.SavedChatId, photo, second);

        var reply = await alice.SendAsync(alice.SavedChatId, "nice", replyTo: album.Id);
        var preview = reply.ReplyTo.ShouldNotBeNull();
        preview.Type.ShouldBe(MessageType.Album);
        preview.Text.ShouldBeNull();
        preview.Media.ShouldNotBeNull().Id.ShouldBe(photo);
    }

    [Fact]
    public async Task Malformed_bodies_are_validation_errors_never_server_errors()
    {
        var alice = await factory.NewUserAsync("Alice");
        var saved = alice.SavedChatId;
        await (await alice.PostJsonTextAsync(ApiRoutes.Messages.RootPath(saved), "{not json"))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.PostJsonTextAsync(ApiRoutes.Messages.RootPath(saved), $$"""{"clientMessageId":"{{Guid.NewGuid()}}","text":"x","entities":[null]}"""))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Lists the request cannot do without: null is a validation error, not a crash.
        await (await alice.PostJsonTextAsync(ApiRoutes.Messages.DeletePath(saved), """{"messageIds":null,"forEveryone":true}"""))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.PostJsonTextAsync(ApiRoutes.Messages.ForwardPath(saved), $$"""{"fromChatId":"{{saved}}","messageIds":null,"clientMessageIds":null}"""))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.PostJsonTextAsync(ApiRoutes.Messages.ForwardPath(saved), $$"""{"fromChatId":"{{saved}}","messageIds":["{{Guid.NewGuid()}}"],"clientMessageIds":null}"""))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var group = await alice.NewGroupAsync();
        await (await alice.PostJsonTextAsync(ApiRoutes.Chats.MembersPath(group), """{"userIds":null}"""))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Optional lists: null may mean "none" or be rejected, but never a server error.
        using var noMedia = await alice.PostJsonTextAsync(ApiRoutes.Messages.RootPath(saved), $$"""{"clientMessageId":"{{Guid.NewGuid()}}","text":"x","mediaIds":null}""");
        ((int)noMedia.StatusCode).ShouldBeLessThan(500, await noMedia.Content.ReadAsStringAsync(TestKit.Ct));
        using var noMembers = await alice.PostJsonTextAsync(ApiRoutes.Chats.Groups, """{"title":"t","memberIds":null}""");
        ((int)noMembers.StatusCode).ShouldBeLessThan(500, await noMembers.Content.ReadAsStringAsync(TestKit.Ct));
    }
}
