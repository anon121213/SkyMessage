using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using SkiaSharp;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// The family the product is built for, end to end over HTTP + SignalR: a mother in Russia and her son in Thailand
/// find each other, talk privately with photos and voice, and the family group holds a call. A neighbour with an
/// account on the same server watches the whole time and must never learn anything.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FamilyScenarioTests(ApiFactory factory)
{
    private static int Digits(int from, int to) => Random.Shared.Next(from, to + 1);

    [Fact]
    public async Task Mother_and_son_find_each_other_and_talk_privately_with_photo_and_voice()
    {
        var mama = await factory.RegisterUserAsync("mama", "Мама");
        var mamaWeb = await factory.SignInAgainAsync(mama, "web browser");
        var son = await factory.RegisterUserAsync("son", "Артём");
        var neighbour = await factory.RegisterUserAsync("neighbour", "Сосед");
        await using var mamaHub = await HubProbe.ConnectAsync(factory, mama, "mama/phone");
        await using var mamaWebHub = await HubProbe.ConnectAsync(factory, mamaWeb, "mama/web");
        await using var sonHub = await HubProbe.ConnectAsync(factory, son, "son");
        await using var neighbourHub = await HubProbe.ConnectAsync(factory, neighbour, "neighbour");

        // Phones: hers typed the Russian way (discoverable by contacts), his read from the Thai SIM (discoverable by anyone).
        var mamaLocal = $"8 (999) {Digits(100, 999)}-{Digits(10, 99)}-{Digits(10, 99)}";
        var mamaMe = await mama.Client.PutJsonAsync<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest
        {
            PhoneNumber = mamaLocal, DefaultRegion = "RU", Source = PhoneSource.Manual, FindByPhone = PrivacyLevel.Contacts,
        });
        var sonMe = await son.Client.PutJsonAsync<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest
        {
            PhoneNumber = $"08{Digits(1, 9)} {Digits(100, 999)} {Digits(1000, 9999)}", DefaultRegion = "TH", Source = PhoneSource.Sim,
            FindByPhone = PrivacyLevel.Everybody,
        });
        mamaMe.PhoneNumber.ShouldNotBeNull().ShouldStartWith("+7999");
        sonMe.PhoneNumber.ShouldNotBeNull().ShouldStartWith("+668");
        sonMe.PhoneSource.ShouldBe(PhoneSource.Sim);

        // 1. They find each other by username ...
        (await son.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath(mama.Username))).ShouldContain(u => u.Id == mama.Id);
        (await mama.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath("@" + son.Username))).ShouldContain(u => u.Id == son.Id);
        var mamaCard = await son.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByUsernamePath("@" + mama.Username.ToUpperInvariant()));
        mamaCard.User.Id.ShouldBe(mama.Id);
        mamaCard.CanMessage.ShouldBeTrue();
        mamaCard.CanCall.ShouldBeTrue();
        mamaCard.PhoneNumber.ShouldBeNull("her number is for her contacts only");

        // ... and by phone: she has his number as "+66 …", he has hers in Russian local format on a Thai phone.
        var sonIntl = "+66 " + sonMe.PhoneNumber[3..];
        var bySonsNumber = await mama.Client.PostJsonAsync<List<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones,
            new FindByPhonesRequest { PhoneNumbers = [sonIntl, "+7 000 000-00-00", "not a number"], DefaultRegion = "RU" });
        bySonsNumber.Single().User.Id.ShouldBe(son.Id);
        bySonsNumber.Single().PhoneNumber.ShouldBe(sonIntl, "the submitted string is echoed for the address book");

        var mark = sonHub.Mark();
        await mama.Client.PostJsonAsync<ContactDto>(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = son.Id, CustomName = "Сынок" });
        await mamaWebHub.WaitAsync<ContactsChangedEvent>(nameof(IRealtimeClient.ContactsChanged));
        // Being saved as her contact changes what he may see of her.
        await sonHub.WaitAsync<UserRefEvent>(nameof(IRealtimeClient.UserChanged), e => e.UserId == mama.Id, since: mark);

        var byMamasNumber = await son.Client.PostJsonAsync<List<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones,
            new FindByPhonesRequest { PhoneNumbers = [mamaLocal], DefaultRegions = ["TH"] });
        byMamasNumber.Single().User.Id.ShouldBe(mama.Id);
        (await son.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(mama.Id))).PhoneNumber.ShouldBe(mamaMe.PhoneNumber);
        (await neighbour.Client.PostJsonAsync<List<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones,
            new FindByPhonesRequest { PhoneNumbers = [mamaMe.PhoneNumber] })).ShouldBeEmpty();
        (await neighbour.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(mama.Id))).PhoneNumber.ShouldBeNull();

        // 2. He writes first; the chat appears on both of her devices.
        var chat = await son.OpenPrivateChatAsync(mama);
        var hello = await son.SendTextAsync(chat.Id, "Привет, мам! Долетел нормально 🌴");
        await mamaHub.WaitAsync<ChatRefEvent>(nameof(IRealtimeClient.ChatAdded), e => e.ChatId == chat.Id);
        (await mamaHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == hello.Id)).SenderId.ShouldBe(son.Id);
        await mamaWebHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == hello.Id);
        var mamasView = (await mama.Client.GetJsonAsync<List<ChatSummaryDto>>(ApiRoutes.Chats.Root)).Single(c => c.Id == chat.Id);
        mamasView.Title.ShouldBe("Сынок");
        mamasView.Peer!.Id.ShouldBe(son.Id);
        mamasView.UnreadCount.ShouldBe(1);

        var reply = await mama.SendTextAsync(chat.Id, "Слава богу! Пришли фото", replyTo: hello.Id);
        (await sonHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == reply.Id)).ReplyTo!.MessageId.ShouldBe(hello.Id);

        // 3. A photo: uploaded in chunks, processed, delivered, loaded through signed URLs on both of her devices.
        var photo = await MediaKit.UploadAsync(son, MediaKind.Photo, "beach.jpg", "image/jpeg", MediaKit.Jpeg(320, 240));
        (await sonHub.WaitAsync<MediaDto>(nameof(IRealtimeClient.MediaProcessed), m => m.Id == photo.Id)).Status.ShouldBe(MediaStatus.Ready);
        var photoMessage = await son.SendMediaAsync(chat.Id, "Пляж у отеля", photo.Id);
        photoMessage.Type.ShouldBe(MessageType.Photo);
        var delivered = await mamaHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == photoMessage.Id);
        var attachment = delivered.Attachments.ShouldHaveSingleItem();
        attachment.Id.ShouldBe(photo.Id);
        (attachment.Width, attachment.Height).ShouldBe((320, 240));

        using (var image = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, mama.Sign(photo.Id))))
        {
            await image.ShouldHaveStatusAsync(HttpStatusCode.OK);
            image.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
            using var decoded = SKBitmap.Decode(await image.Content.ReadAsByteArrayAsync(SecurityKit.Ct));
            (decoded.Width, decoded.Height).ShouldBe((320, 240));
        }
        using (var thumbnail = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ThumbnailPath(photo.Id, mamaWeb.Sign(photo.Id))))
            await thumbnail.ShouldHaveStatusAsync(HttpStatusCode.OK);
        using (var forNeighbour = await neighbour.Client.GetAsync(ApiRoutes.Media.ContentPath(photo.Id, null), SecurityKit.Ct))
            await forNeighbour.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var neighbourSigned = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ThumbnailPath(photo.Id, neighbour.Sign(photo.Id))))
            await neighbourSigned.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // 4. She answers with a voice message (recorded as WAV, served as AAC with a waveform).
        var voice = await MediaKit.UploadAsync(mama, MediaKind.Voice, "voice.wav", "audio/wav", MediaKit.Wav());
        if (MediaKit.FfmpegAvailable)
        {
            // SPEC §6.4: voice is re-encoded to AAC (.m4a) with a duration and a 100-bucket waveform.
            voice.ContentType.ShouldBe("audio/mp4");
            voice.DurationMs.ShouldNotBeNull().ShouldBeGreaterThan(500);
            voice.Waveform.ShouldNotBeNull().Length.ShouldBe(Limits.WaveformSamples);
        }
        var voiceMessage = await mama.SendMediaAsync(chat.Id, null, voice.Id);
        voiceMessage.Type.ShouldBe(MessageType.Voice);
        (await sonHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == voiceMessage.Id))
            .Attachments.ShouldHaveSingleItem().Kind.ShouldBe(MediaKind.Voice);
        using (var partial = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(voice.Id, son.Sign(voice.Id)), new RangeHeaderValue(0, 99)))
        {
            partial.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            partial.Content.Headers.ContentRange!.Length.ShouldBe(voice.SizeBytes);
            (await partial.Content.ReadAsByteArrayAsync(SecurityKit.Ct)).Length.ShouldBe(100);
        }

        await son.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Chats.ReadPath(chat.Id), new MarkReadRequest { UpToSeq = voiceMessage.Seq });
        (await mamaHub.WaitAsync<MessagesReadEvent>(nameof(IRealtimeClient.MessagesRead), e => e.ChatId == chat.Id && e.UserId == son.Id))
            .UpToSeq.ShouldBe(voiceMessage.Seq);

        // 5. He sends the wrong photo and deletes it for everyone before she opens it.
        var wrong = await MediaKit.UploadAsync(son, MediaKind.Photo, "wrong.jpg", "image/jpeg", MediaKit.Jpeg(64, 64, 200, 30, 30));
        var oops = await son.SendMediaAsync(chat.Id, "ой, не то фото", wrong.Id);
        await mamaHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == oops.Id);
        await son.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(chat.Id),
            new DeleteMessagesRequest { MessageIds = [oops.Id], ForEveryone = true });
        foreach (var hub in new[] { mamaHub, mamaWebHub, sonHub })
        {
            (await hub.WaitAsync<MessagesDeletedEvent>(nameof(IRealtimeClient.MessagesDeleted), e => e.ChatId == chat.Id && e.MessageIds.Contains(oops.Id)))
                .ForEveryone.ShouldBeTrue();
        }
        var tombstone = await mama.Client.GetJsonAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(chat.Id, oops.Id));
        tombstone.IsDeleted.ShouldBeTrue();
        tombstone.Text.ShouldBeNull();
        tombstone.Attachments.ShouldBeEmpty();
        (await mama.PageAsync(chat.Id)).Messages.ShouldNotContain(m => m.Id == oops.Id);
        using (var gone = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(wrong.Id, mama.Sign(wrong.Id))))
            await gone.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // 6. She updates her profile: his device and her own other device are told to refetch.
        var sonMark = sonHub.Mark();
        var webMark = mamaWebHub.Mark();
        await mama.Client.PatchJsonAsync<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { DisplayName = "Мама Люда", Bio = "Скучаю по сыну ❤️" });
        await sonHub.WaitAsync<UserRefEvent>(nameof(IRealtimeClient.UserChanged), e => e.UserId == mama.Id, since: sonMark);
        await mamaWebHub.WaitAsync<UserRefEvent>(nameof(IRealtimeClient.UserChanged), e => e.UserId == mama.Id, since: webMark);
        var refreshed = await son.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(mama.Id));
        refreshed.User.DisplayName.ShouldBe("Мама Люда");
        refreshed.Bio.ShouldBe("Скучаю по сыну ❤️");
        (await son.Client.GetJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(chat.Id))).Title.ShouldBe("Мама Люда");
        (await mamaWeb.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).DisplayName.ShouldBe("Мама Люда");

        // The neighbour, connected the whole time, learned nothing about them.
        await neighbourHub.FlushAsync(neighbour);
        neighbourHub.All().Where(e =>
                e.Payload is MessageDto m && m.ChatId == chat.Id
                || e.Payload is ChatRefEvent c && c.ChatId == chat.Id
                || e.Payload is MessagesDeletedEvent d && d.ChatId == chat.Id
                || e.Payload is MessagesReadEvent r && r.ChatId == chat.Id
                || e.Payload is UserRefEvent u && (u.UserId == mama.Id || u.UserId == son.Id)
                || e.Payload is MediaDto media && (media.Id == photo.Id || media.Id == voice.Id))
            .Select(e => e.Name)
            .ShouldBeEmpty();
        await neighbour.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.PagePath(chat.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Family_group_chats_holds_a_group_call_and_sees_profile_changes()
    {
        var mama = await factory.RegisterUserAsync("mama", "Мама");
        var son = await factory.RegisterUserAsync("son", "Артём");
        var papa = await factory.RegisterUserAsync("papa", "Папа");
        var neighbour = await factory.RegisterUserAsync("neighbour", "Сосед");
        await using var mamaHub = await HubProbe.ConnectAsync(factory, mama, "mama");
        await using var sonHub = await HubProbe.ConnectAsync(factory, son, "son");
        await using var papaHub = await HubProbe.ConnectAsync(factory, papa, "papa");
        await using var neighbourHub = await HubProbe.ConnectAsync(factory, neighbour, "neighbour");
        var family = new[] { mamaHub, sonHub, papaHub };

        // 1. She creates the family group with both of them.
        var created = await mama.Client.PostJsonAsync<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest
        {
            Title = "Семья 👨‍👩‍👦", Description = "Россия ↔ Таиланд", MemberIds = [son.Id, papa.Id],
        });
        created.Failed.ShouldBeEmpty();
        var group = created.Chat;
        group.MemberCount.ShouldBe(3);
        group.MyRole.ShouldBe(ChatRole.Owner);
        await sonHub.WaitAsync<ChatRefEvent>(nameof(IRealtimeClient.ChatAdded), e => e.ChatId == group.Id);
        await papaHub.WaitAsync<ChatRefEvent>(nameof(IRealtimeClient.ChatAdded), e => e.ChatId == group.Id);
        foreach (var hub in family)
        {
            await hub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated),
                m => m.ChatId == group.Id && m.SystemEvent?.Type == SystemEventType.ChatCreated);
        }
        (await son.Client.GetJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(group.Id))).MyRole.ShouldBe(ChatRole.Member);

        // 2. Everyone talks; the son shares a photo with the group.
        var papaHello = await papa.SendTextAsync(group.Id, "Всем привет из Новосибирска! У нас -25");
        await sonHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == papaHello.Id);
        await mamaHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == papaHello.Id);
        var photo = await MediaKit.UploadAsync(son, MediaKind.Photo, "sunset.jpg", "image/jpeg", MediaKit.Jpeg(200, 150, 250, 120, 40));
        var photoMessage = await son.SendMediaAsync(group.Id, "А у нас +32 😎", photo.Id);
        await papaHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == photoMessage.Id);
        foreach (var member in new[] { mama, papa })
        {
            using var image = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, member.Sign(photo.Id)));
            await image.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }
        using (var forNeighbour = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, neighbour.Sign(photo.Id))))
            await forNeighbour.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // 3. A group video call: mother starts it, son and father join, everyone leaves, the last one out ends it.
        var start = await mama.Client.PostJsonAsync<CallJoinInfoDto>(ApiRoutes.Calls.Root, new StartCallRequest { ChatId = group.Id, IsVideo = true });
        var callId = start.Call.Id;
        start.Call.Kind.ShouldBe(CallKind.Group);
        start.Call.Status.ShouldBe(CallStatus.Active);
        start.LiveKitUrl.ShouldBe(HubRoutes.LiveKitProxy);
        AssertLiveKitToken(start.LiveKitToken, mama, callId);
        foreach (var hub in new[] { sonHub, papaHub })
            (await hub.WaitAsync<CallDto>(nameof(IRealtimeClient.IncomingCall), c => c.Id == callId)).IsVideo.ShouldBeTrue();
        foreach (var hub in family)
        {
            await hub.WaitAsync<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), c => c.Id == group.Id && c.ActiveCallId == callId);
            await hub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated),
                m => m.ChatId == group.Id && m.SystemEvent?.Type == SystemEventType.GroupCallStarted && m.SystemEvent.CallId == callId);
        }
        (await son.Client.GetJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(group.Id))).ActiveCallId.ShouldBe(callId);
        await neighbour.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.JoinPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);

        var sonJoin = await son.Client.PostJsonAsync<CallJoinInfoDto>(ApiRoutes.Calls.JoinPath(callId), null);
        AssertLiveKitToken(sonJoin.LiveKitToken, son, callId);
        await mamaHub.WaitAsync<CallDto>(nameof(IRealtimeClient.CallUpdated),
            c => c.Id == callId && c.Participants.Any(p => p.UserId == son.Id && p.Status == CallParticipantStatus.Joined));
        var papaJoin = await papa.Client.PostJsonAsync<CallJoinInfoDto>(ApiRoutes.Calls.JoinPath(callId), null);
        AssertLiveKitToken(papaJoin.LiveKitToken, papa, callId);
        await sonHub.WaitAsync<CallDto>(nameof(IRealtimeClient.CallUpdated),
            c => c.Id == callId && c.Participants.Any(p => p.UserId == papa.Id && p.Status == CallParticipantStatus.Joined));

        await son.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId));
        (await papaHub.WaitAsync<CallDto>(nameof(IRealtimeClient.CallUpdated),
                c => c.Id == callId && c.Participants.Any(p => p.UserId == son.Id && p.Status == CallParticipantStatus.Left)))
            .Status.ShouldBe(CallStatus.Active);
        await papa.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId));
        (await mamaHub.WaitAsync<CallDto>(nameof(IRealtimeClient.CallUpdated),
                c => c.Id == callId && c.Participants.Any(p => p.UserId == papa.Id && p.Status == CallParticipantStatus.Left)))
            .Status.ShouldBe(CallStatus.Active);

        var marks = family.Select(h => h.Mark()).ToArray();
        await mama.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId));
        for (var i = 0; i < family.Length; i++)
        {
            var hub = family[i];
            await hub.WaitAsync<CallDto>(nameof(IRealtimeClient.CallUpdated), c => c.Id == callId && c.Status == CallStatus.Ended, since: marks[i]);
            await hub.WaitAsync<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), c => c.Id == group.Id && c.ActiveCallId == null, since: marks[i]);
            await hub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated),
                m => m.ChatId == group.Id && m.SystemEvent?.Type == SystemEventType.GroupCallEnded && m.SystemEvent.CallId == callId, since: marks[i]);
        }
        (await son.Client.GetJsonAsync<List<CallHistoryItemDto>>(ApiRoutes.Calls.HistoryPath()))
            .ShouldContain(h => h.Call.Id == callId && h.ChatType == ChatType.Group && h.Call.Status == CallStatus.Ended);
        (await neighbour.Client.GetJsonAsync<List<CallHistoryItemDto>>(ApiRoutes.Calls.HistoryPath())).ShouldBeEmpty();

        // 4. The son deletes his own typo for everyone, but cannot delete his father's message.
        var typo = await son.SendTextAsync(group.Id, "Опечатака");
        await papaHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.Id == typo.Id);
        await son.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(group.Id), new DeleteMessagesRequest { MessageIds = [typo.Id], ForEveryone = true });
        foreach (var hub in family)
        {
            (await hub.WaitAsync<MessagesDeletedEvent>(nameof(IRealtimeClient.MessagesDeleted), e => e.ChatId == group.Id && e.MessageIds.Contains(typo.Id)))
                .ForEveryone.ShouldBeTrue();
        }
        await son.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(group.Id),
            new DeleteMessagesRequest { MessageIds = [papaHello.Id], ForEveryone = true }, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        var papasMessage = await mama.Client.GetJsonAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(group.Id, papaHello.Id));
        papasMessage.IsDeleted.ShouldBeFalse();
        papasMessage.Text.ShouldBe(papaHello.Text);

        // 5. The mother edits her profile: every member (and her own device) is told.
        marks = family.Select(h => h.Mark()).ToArray();
        await mama.Client.PatchJsonAsync<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { DisplayName = "Мама Люда" });
        for (var i = 0; i < family.Length; i++)
            await family[i].WaitAsync<UserRefEvent>(nameof(IRealtimeClient.UserChanged), e => e.UserId == mama.Id, since: marks[i]);
        var details = await papa.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id));
        details.Members.Single(m => m.User.Id == mama.Id).User.DisplayName.ShouldBe("Мама Люда");
        details.Members.Single(m => m.User.Id == mama.Id).Role.ShouldBe(ChatRole.Owner);
        details.InviteToken.ShouldBeNull("papa is not an admin");

        // The neighbour saw none of it.
        await neighbourHub.FlushAsync(neighbour);
        neighbourHub.All().Where(e =>
                e.Payload is MessageDto m && m.ChatId == group.Id
                || e.Payload is ChatRefEvent c && c.ChatId == group.Id
                || e.Payload is ChatInfoDto info && info.Id == group.Id
                || e.Payload is CallDto call && call.Id == callId
                || e.Payload is MessagesDeletedEvent d && d.ChatId == group.Id
                || e.Payload is UserRefEvent u && (u.UserId == mama.Id || u.UserId == son.Id || u.UserId == papa.Id))
            .Select(e => e.Name)
            .ShouldBeEmpty();
    }

    /// <summary>The LiveKit grant lets this device into this call's room and nothing more (no admin, list or record rights).</summary>
    private static void AssertLiveKitToken(string token, TestUser user, Guid callId)
    {
        var payload = TokenKit.Payload(token);
        payload.GetProperty("sub").GetString().ShouldBe($"{user.Id:N}.{user.SessionId:N}");
        var video = payload.GetProperty("video");
        video.GetProperty("room").GetString().ShouldBe($"call-{callId:N}");
        video.GetProperty("roomJoin").GetBoolean().ShouldBeTrue();
        foreach (var privileged in new[] { "roomAdmin", "roomCreate", "roomList", "roomRecord", "ingressAdmin", "recorder", "agent" })
            video.TryGetProperty(privileged, out _).ShouldBeFalse(privileged);
        var lifetime = payload.GetProperty("exp").GetInt64() - payload.GetProperty("nbf").GetInt64();
        lifetime.ShouldBeLessThanOrEqualTo(15 * 60 + 5);
        payload.GetProperty("metadata").GetString()!.ShouldContain(user.Id.ToString());
    }
}
