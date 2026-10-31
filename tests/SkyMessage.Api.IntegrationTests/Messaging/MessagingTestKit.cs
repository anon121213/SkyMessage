using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.Persistence;
using Xunit;
using MediaEntity = SkyMessage.Domain.Entities.Media;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>A signed-in test user on one device (one session) with its own authenticated client.</summary>
public sealed class TestUser(ApiFactory factory, AuthResponse auth, string username)
{
    public ApiFactory Factory { get; } = factory;
    public AuthResponse Auth { get; } = auth;
    public HttpClient Client { get; } = factory.AuthorizedClient(auth.AccessToken);
    public string Username { get; } = username;

    public Guid Id => Auth.User.Id;
    public Guid SessionId => Auth.SessionId;
    public Guid SavedChatId => Auth.User.SavedMessagesChatId;
    public string DisplayName => Auth.User.DisplayName;
    public string AccessToken => Auth.AccessToken;

    public override string ToString() => $"{DisplayName} (@{Username})";
}

internal static class TestKit
{
    public const string Password = "correct-horse-battery";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DeviceInfoDto Device = new() { DeviceName = "it-messaging", Platform = ClientPlatform.Linux, AppVersion = "test" };

    public static HttpClient AuthorizedClient(this ApiFactory factory, string accessToken)
    {
        var client = factory.CreateJsonClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>
    /// Registers a fresh user through <see cref="AuthService"/> (the code behind POST api/auth/register: user,
    /// Saved Messages chat and session). Going through the service instead of HTTP keeps this suite, which needs
    /// hundreds of users, clear of the per-IP "auth" rate limit (30/min, one partition under TestServer).
    /// </summary>
    public static async Task<TestUser> NewUserAsync(this ApiFactory factory, string displayName = "Test User")
    {
        var username = "m" + Guid.NewGuid().ToString("N")[..16];
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = await scope.ServiceProvider.GetRequiredService<AuthService>().RegisterAsync(new RegisterRequest
        {
            Username = username,
            Password = Password,
            DisplayName = displayName,
            Device = Device,
        }, ipAddress: null, CancellationToken.None);
        return new TestUser(factory, auth, username);
    }

    /// <summary>The same user signed in on another device (new session, own client).</summary>
    public static async Task<TestUser> SecondDeviceAsync(this TestUser user)
    {
        await using var scope = user.Factory.Services.CreateAsyncScope();
        var auth = await scope.ServiceProvider.GetRequiredService<AuthService>().LoginAsync(new LoginRequest
        {
            Login = user.Username,
            Password = Password,
            Device = Device with { DeviceName = "it-messaging-2" },
        }, ipAddress: null, CancellationToken.None);
        return new TestUser(user.Factory, auth, user.Username);
    }

    public static async Task WithDbAsync(this ApiFactory factory, Func<AppDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static async Task<T> WithDbAsync<T>(this ApiFactory factory, Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// A media row owned by <paramref name="owner"/> in the given state, inserted directly: messaging only cares
    /// about owner, kind and status (upload and processing belong to the media feature).
    /// </summary>
    public static Task<Guid> CreateMediaAsync(this ApiFactory factory, TestUser owner, MediaKind kind, MediaStatus status = MediaStatus.Ready) =>
        factory.WithDbAsync(async db =>
        {
            var now = Now();
            var (fileName, contentType) = kind switch
            {
                MediaKind.Photo => ("photo.jpg", "image/jpeg"),
                MediaKind.Video => ("clip.mp4", "video/mp4"),
                MediaKind.Voice => ("voice.m4a", "audio/mp4"),
                MediaKind.VideoNote => ("note.mp4", "video/mp4"),
                MediaKind.Audio => ("song.mp3", "audio/mpeg"),
                _ => ("report.pdf", "application/pdf"),
            };
            var media = new MediaEntity
            {
                OwnerId = owner.Id,
                Kind = kind,
                Status = status,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = 16,
                DeclaredSizeBytes = 16,
                ChunkSize = Limits.DefaultChunkSize,
                ChunkCount = 1,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = status == MediaStatus.Ready ? now : null,
            };
            db.Media.Add(media);
            await db.SaveChangesAsync(CancellationToken.None);
            return media.Id;
        });

    /// <summary>Microsecond-truncated UTC now, like the server's clock.</summary>
    public static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }

    public static Task<RealtimeProbe> ConnectAsync(this TestUser user) => RealtimeProbe.ConnectAsync(user);
}

/// <summary>Typed calls of the messaging API (each throws with the response body when the call fails).</summary>
internal static class MessagingApi
{
    // ---- raw HTTP ----

    public static Task<HttpResponseMessage> GetRawAsync(this TestUser user, string path) =>
        user.Client.GetAsync(path, TestKit.Ct);

    public static Task<HttpResponseMessage> PostRawAsync<T>(this TestUser user, string path, T body) =>
        user.Client.PostAsJsonAsync(path, body, SkyJson.Options, TestKit.Ct);

    public static Task<HttpResponseMessage> PostEmptyAsync(this TestUser user, string path) =>
        user.Client.PostAsync(path, null, TestKit.Ct);

    public static Task<HttpResponseMessage> PostJsonTextAsync(this TestUser user, string path, string json) =>
        user.Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), TestKit.Ct);

    public static Task<HttpResponseMessage> PutRawAsync<T>(this TestUser user, string path, T body) =>
        user.Client.PutAsJsonAsync(path, body, SkyJson.Options, TestKit.Ct);

    public static Task<HttpResponseMessage> PutEmptyAsync(this TestUser user, string path) =>
        user.Client.PutAsync(path, null, TestKit.Ct);

    public static Task<HttpResponseMessage> PatchRawAsync<T>(this TestUser user, string path, T body) =>
        user.Client.PatchAsJsonAsync(path, body, SkyJson.Options, TestKit.Ct);

    public static Task<HttpResponseMessage> DeleteRawAsync(this TestUser user, string path) =>
        user.Client.DeleteAsync(path, TestKit.Ct);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        using (response)
        {
            await response.ShouldSucceedAsync();
            return (await response.Content.ReadFromJsonAsync<T>(SkyJson.Options, TestKit.Ct))!;
        }
    }

    public static async Task ShouldSucceedAsync(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(TestKit.Ct);
        throw new ShouldAssertException(
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} failed with {(int)response.StatusCode}: {body}");
    }

    /// <summary>Asserts a problem-details response with the given status and machine-readable code.</summary>
    public static async Task ShouldFailAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(TestKit.Ct);
            var what = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} → {(int)response.StatusCode}: {body}";
            response.StatusCode.ShouldBe(status, what);
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.TryGetProperty("code", out var actual).ShouldBeTrue(what);
            actual.GetString().ShouldBe(code, what);
        }
    }

    public static async Task<T> GetAsync<T>(this TestUser user, string path) => await (await user.GetRawAsync(path)).ReadAsync<T>();

    public static async Task<T> PostAsync<TBody, T>(this TestUser user, string path, TBody body) =>
        await (await user.PostRawAsync(path, body)).ReadAsync<T>();

    public static async Task NoContentAsync(this Task<HttpResponseMessage> call)
    {
        using var response = await call;
        await response.ShouldSucceedAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---- chats ----

    public static async Task<ChatSummaryDto> OpenPrivateAsync(this TestUser user, Guid peerId) =>
        await (await user.PostRawAsync(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = peerId })).ReadAsync<ChatSummaryDto>();

    public static Task<HttpResponseMessage> TryOpenPrivateAsync(this TestUser user, Guid peerId) =>
        user.PostRawAsync(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = peerId });

    public static async Task<CreateGroupResultDto> CreateGroupAsync(this TestUser owner, string title, params TestUser[] members) =>
        await owner.CreateGroupAsync(new CreateGroupRequest { Title = title, MemberIds = members.Select(m => m.Id).ToList() });

    public static async Task<CreateGroupResultDto> CreateGroupAsync(this TestUser owner, CreateGroupRequest request) =>
        await (await owner.PostRawAsync(ApiRoutes.Chats.Groups, request)).ReadAsync<CreateGroupResultDto>();

    /// <summary>A group owned by <paramref name="owner"/> with the given members; returns its id.</summary>
    public static async Task<Guid> NewGroupAsync(this TestUser owner, params TestUser[] members)
    {
        var result = await owner.CreateGroupAsync("Group " + Guid.NewGuid().ToString("N")[..6], members);
        result.Failed.ShouldBeEmpty();
        return result.Chat.Id;
    }

    public static Task<IReadOnlyList<ChatSummaryDto>> ChatsAsync(this TestUser user) =>
        user.GetAsync<IReadOnlyList<ChatSummaryDto>>(ApiRoutes.Chats.Root);

    public static Task<ChatSummaryDto> ChatAsync(this TestUser user, Guid chatId) =>
        user.GetAsync<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(chatId));

    public static Task<ChatDetailsDto> DetailsAsync(this TestUser user, Guid chatId) =>
        user.GetAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(chatId));

    public static Task<IReadOnlyList<MessageDto>> PinnedAsync(this TestUser user, Guid chatId) =>
        user.GetAsync<IReadOnlyList<MessageDto>>(ApiRoutes.Chats.PinnedPath(chatId));

    public static async Task<ChatInfoDto> UpdateChatAsync(this TestUser user, Guid chatId, UpdateChatRequest request) =>
        await (await user.PatchRawAsync(ApiRoutes.Chats.ByIdPath(chatId), request)).ReadAsync<ChatInfoDto>();

    public static Task<ChatInfoDto> SetPermissionsAsync(this TestUser user, Guid chatId, ChatPermissionsDto permissions) =>
        user.UpdateChatAsync(chatId, new UpdateChatRequest { Permissions = permissions });

    public static async Task<AddMembersResultDto> AddMembersAsync(this TestUser user, Guid chatId, params Guid[] userIds) =>
        await (await user.PostRawAsync(ApiRoutes.Chats.MembersPath(chatId), new AddMembersRequest { UserIds = userIds })).ReadAsync<AddMembersResultDto>();

    public static Task<HttpResponseMessage> TryAddMembersAsync(this TestUser user, Guid chatId, params Guid[] userIds) =>
        user.PostRawAsync(ApiRoutes.Chats.MembersPath(chatId), new AddMembersRequest { UserIds = userIds });

    public static Task<HttpResponseMessage> TryRemoveMemberAsync(this TestUser user, Guid chatId, Guid userId) =>
        user.DeleteRawAsync(ApiRoutes.Chats.MemberPath(chatId, userId));

    public static Task RemoveMemberAsync(this TestUser user, Guid chatId, Guid userId) =>
        user.TryRemoveMemberAsync(chatId, userId).NoContentAsync();

    public static Task<HttpResponseMessage> TrySetRoleAsync(this TestUser user, Guid chatId, Guid userId, ChatRole role) =>
        user.PutRawAsync(ApiRoutes.Chats.MemberRolePath(chatId, userId), new SetMemberRoleRequest { Role = role });

    public static Task SetRoleAsync(this TestUser user, Guid chatId, Guid userId, ChatRole role) =>
        user.TrySetRoleAsync(chatId, userId, role).NoContentAsync();

    public static Task LeaveAsync(this TestUser user, Guid chatId) => user.PostEmptyAsync(ApiRoutes.Chats.LeavePath(chatId)).NoContentAsync();

    public static async Task<string> CreateInviteLinkAsync(this TestUser user, Guid chatId) =>
        (await (await user.PostEmptyAsync(ApiRoutes.Chats.InviteLinkPath(chatId))).ReadAsync<InviteLinkDto>()).Token;

    public static async Task<ChatUserSettingsDto> UpdateSettingsAsync(this TestUser user, Guid chatId, UpdateChatSettingsRequest request) =>
        await (await user.PutRawAsync(ApiRoutes.Chats.SettingsPath(chatId), request)).ReadAsync<ChatUserSettingsDto>();

    public static Task MarkReadAsync(this TestUser user, Guid chatId, long upToSeq) =>
        user.PostRawAsync(ApiRoutes.Chats.ReadPath(chatId), new MarkReadRequest { UpToSeq = upToSeq }).NoContentAsync();

    public static Task ClearHistoryAsync(this TestUser user, Guid chatId, bool forEveryone = false) =>
        user.PostRawAsync(ApiRoutes.Chats.ClearHistoryPath(chatId), new ClearHistoryRequest { ForEveryone = forEveryone }).NoContentAsync();

    public static Task DeleteChatAsync(this TestUser user, Guid chatId, bool forEveryone) =>
        user.DeleteRawAsync(ApiRoutes.Chats.DeletePath(chatId, forEveryone)).NoContentAsync();

    // ---- messages ----

    public static SendMessageRequest Text(string text, Guid? replyTo = null, IReadOnlyList<MessageEntityDto>? entities = null) => new()
    {
        ClientMessageId = Guid.NewGuid(),
        Text = text,
        ReplyToMessageId = replyTo,
        Entities = entities,
    };

    public static Task<HttpResponseMessage> TrySendAsync(this TestUser user, Guid chatId, SendMessageRequest request) =>
        user.PostRawAsync(ApiRoutes.Messages.RootPath(chatId), request);

    public static async Task<MessageDto> SendAsync(this TestUser user, Guid chatId, SendMessageRequest request) =>
        await (await user.TrySendAsync(chatId, request)).ReadAsync<MessageDto>();

    public static Task<MessageDto> SendAsync(this TestUser user, Guid chatId, string text, Guid? replyTo = null, IReadOnlyList<MessageEntityDto>? entities = null) =>
        user.SendAsync(chatId, Text(text, replyTo, entities));

    public static async Task<List<MessageDto>> SendManyAsync(this TestUser user, Guid chatId, int count, string prefix = "msg")
    {
        var sent = new List<MessageDto>(count);
        for (var i = 1; i <= count; i++)
            sent.Add(await user.SendAsync(chatId, $"{prefix} {i}"));
        return sent;
    }

    public static Task<MessageDto> SendMediaAsync(this TestUser user, Guid chatId, params Guid[] mediaIds) =>
        user.SendAsync(chatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = mediaIds });

    public static Task<MessagesPageDto> PageAsync(this TestUser user, Guid chatId, long? beforeSeq = null, long? afterSeq = null,
        long? aroundSeq = null, int limit = Limits.MessagesPageDefault) =>
        user.GetAsync<MessagesPageDto>(ApiRoutes.Messages.PagePath(chatId, beforeSeq, afterSeq, aroundSeq, limit));

    public static Task<MessageChangesDto> ChangesAsync(this TestUser user, Guid chatId, string cursor) =>
        user.GetAsync<MessageChangesDto>(ApiRoutes.Messages.ChangesPath(chatId, cursor));

    public static Task<IReadOnlyList<long>> MentionsAsync(this TestUser user, Guid chatId) =>
        user.GetAsync<IReadOnlyList<long>>(ApiRoutes.Messages.MentionsPath(chatId));

    public static Task<MessageDto> GetMessageAsync(this TestUser user, Guid chatId, Guid messageId) =>
        user.GetAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(chatId, messageId));

    public static Task<HttpResponseMessage> TryEditAsync(this TestUser user, Guid chatId, Guid messageId, string? text, IReadOnlyList<MessageEntityDto>? entities = null) =>
        user.PatchRawAsync(ApiRoutes.Messages.ByIdPath(chatId, messageId), new EditMessageRequest { Text = text, Entities = entities });

    public static async Task<MessageDto> EditAsync(this TestUser user, Guid chatId, Guid messageId, string? text, IReadOnlyList<MessageEntityDto>? entities = null) =>
        await (await user.TryEditAsync(chatId, messageId, text, entities)).ReadAsync<MessageDto>();

    public static Task<HttpResponseMessage> TryDeleteMessagesAsync(this TestUser user, Guid chatId, bool forEveryone, params Guid[] messageIds) =>
        user.PostRawAsync(ApiRoutes.Messages.DeletePath(chatId), new DeleteMessagesRequest { MessageIds = messageIds, ForEveryone = forEveryone });

    public static Task DeleteMessagesAsync(this TestUser user, Guid chatId, bool forEveryone, params Guid[] messageIds) =>
        user.TryDeleteMessagesAsync(chatId, forEveryone, messageIds).NoContentAsync();

    public static Task<HttpResponseMessage> TryReactAsync(this TestUser user, Guid chatId, Guid messageId, string emoji) =>
        user.PutRawAsync(ApiRoutes.Messages.ReactionPath(chatId, messageId), new SetReactionRequest { Emoji = emoji });

    public static async Task<MessageDto> ReactAsync(this TestUser user, Guid chatId, Guid messageId, string emoji) =>
        await (await user.TryReactAsync(chatId, messageId, emoji)).ReadAsync<MessageDto>();

    public static async Task<MessageDto> UnreactAsync(this TestUser user, Guid chatId, Guid messageId) =>
        await (await user.DeleteRawAsync(ApiRoutes.Messages.ReactionPath(chatId, messageId))).ReadAsync<MessageDto>();

    public static Task<HttpResponseMessage> TryPinAsync(this TestUser user, Guid chatId, Guid messageId) =>
        user.PutEmptyAsync(ApiRoutes.Messages.PinPath(chatId, messageId));

    public static Task PinAsync(this TestUser user, Guid chatId, Guid messageId) => user.TryPinAsync(chatId, messageId).NoContentAsync();

    public static Task UnpinAsync(this TestUser user, Guid chatId, Guid messageId) =>
        user.DeleteRawAsync(ApiRoutes.Messages.PinPath(chatId, messageId)).NoContentAsync();

    public static Task<HttpResponseMessage> TryForwardAsync(this TestUser user, Guid targetChatId, ForwardMessagesRequest request) =>
        user.PostRawAsync(ApiRoutes.Messages.ForwardPath(targetChatId), request);

    public static async Task<IReadOnlyList<MessageDto>> ForwardAsync(this TestUser user, Guid targetChatId, ForwardMessagesRequest request) =>
        await (await user.TryForwardAsync(targetChatId, request)).ReadAsync<IReadOnlyList<MessageDto>>();

    // ---- other users' features used as preconditions ----

    public static async Task SetPrivacyAsync(this TestUser user, Func<PrivacySettingsDto, PrivacySettingsDto> change)
    {
        var current = await user.GetAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy);
        await (await user.PutRawAsync(ApiRoutes.Users.MePrivacy, change(current))).ShouldSucceedAsync();
    }

    public static async Task AddContactAsync(this TestUser user, Guid contactId, string? customName = null) =>
        await (await user.PostRawAsync(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = contactId, CustomName = customName })).ShouldSucceedAsync();

    public static Task BlockAsync(this TestUser user, Guid targetId) => user.PutEmptyAsync(ApiRoutes.Blocks.ByUserIdPath(targetId)).NoContentAsync();
}

/// <summary>Event names of <see cref="IRealtimeClient"/>.</summary>
internal static class Ev
{
    public const string MessageCreated = nameof(IRealtimeClient.MessageCreated);
    public const string MessageUpdated = nameof(IRealtimeClient.MessageUpdated);
    public const string MessagesDeleted = nameof(IRealtimeClient.MessagesDeleted);
    public const string MessagesRead = nameof(IRealtimeClient.MessagesRead);
    public const string Typing = nameof(IRealtimeClient.Typing);
    public const string ChatAdded = nameof(IRealtimeClient.ChatAdded);
    public const string ChatUpdated = nameof(IRealtimeClient.ChatUpdated);
    public const string ChatRemoved = nameof(IRealtimeClient.ChatRemoved);
    public const string ChatMembersChanged = nameof(IRealtimeClient.ChatMembersChanged);
    public const string ChatSettingsChanged = nameof(IRealtimeClient.ChatSettingsChanged);
    public const string HistoryCleared = nameof(IRealtimeClient.HistoryCleared);
    public const string PinnedMessagesChanged = nameof(IRealtimeClient.PinnedMessagesChanged);
    public const string PresenceChanged = nameof(IRealtimeClient.PresenceChanged);
    public const string UserChanged = nameof(IRealtimeClient.UserChanged);
    public const string ContactsChanged = nameof(IRealtimeClient.ContactsChanged);
    public const string MediaProcessed = nameof(IRealtimeClient.MediaProcessed);
    public const string IncomingCall = nameof(IRealtimeClient.IncomingCall);
    public const string CallUpdated = nameof(IRealtimeClient.CallUpdated);
    public const string SessionRevoked = nameof(IRealtimeClient.SessionRevoked);

    /// <summary>Events that carry a chat id, with a selector for it.</summary>
    public static Guid? ChatIdOf(object payload) => payload switch
    {
        MessageDto m => m.ChatId,
        MessagesDeletedEvent e => e.ChatId,
        MessagesReadEvent e => e.ChatId,
        TypingEvent e => e.ChatId,
        ChatRefEvent e => e.ChatId,
        ChatInfoDto c => c.Id,
        ChatSettingsChangedEvent e => e.ChatId,
        HistoryClearedEvent e => e.ChatId,
        _ => null,
    };
}

/// <summary>
/// One realtime connection (SignalR over long polling) that records every event it receives. Waits never
/// sleep: <see cref="WaitAsync{T}"/> completes as soon as a matching event arrives, and "did not receive"
/// checks call <see cref="BarrierAsync"/> first: a server round trip that ends with an event only this user
/// gets, so anything published before it has already been delivered (SignalR keeps per-connection order).
/// </summary>
public sealed class RealtimeProbe : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static int s_barrierSeconds = 1_000_000;

    private readonly HubConnection _hub;
    private readonly TestUser _user;
    private readonly object _gate = new();
    private readonly List<(string Name, object Payload)> _events = [];
    private readonly List<Waiter> _waiters = [];

    private sealed class Waiter(string name, Func<object, bool> match)
    {
        public string Name { get; } = name;
        public Func<object, bool> Match { get; } = match;
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private RealtimeProbe(TestUser user)
    {
        _user = user;
        _hub = user.Factory.CreateHubConnection(user.AccessToken);
        Listen<MessageDto>(Ev.MessageCreated);
        Listen<MessageDto>(Ev.MessageUpdated);
        Listen<MessagesDeletedEvent>(Ev.MessagesDeleted);
        Listen<MessagesReadEvent>(Ev.MessagesRead);
        Listen<TypingEvent>(Ev.Typing);
        Listen<ChatRefEvent>(Ev.ChatAdded);
        Listen<ChatInfoDto>(Ev.ChatUpdated);
        Listen<ChatRefEvent>(Ev.ChatRemoved);
        Listen<ChatRefEvent>(Ev.ChatMembersChanged);
        Listen<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged);
        Listen<HistoryClearedEvent>(Ev.HistoryCleared);
        Listen<ChatRefEvent>(Ev.PinnedMessagesChanged);
        Listen<PresenceChangedEvent>(Ev.PresenceChanged);
        Listen<UserRefEvent>(Ev.UserChanged);
        Listen<ContactsChangedEvent>(Ev.ContactsChanged);
        Listen<MediaDto>(Ev.MediaProcessed);
        Listen<CallDto>(Ev.IncomingCall);
        Listen<CallDto>(Ev.CallUpdated);
        Listen<SessionRevokedEvent>(Ev.SessionRevoked);
    }

    public TestUser User => _user;

    public static async Task<RealtimeProbe> ConnectAsync(TestUser user)
    {
        var probe = new RealtimeProbe(user);
        await probe._hub.StartAsync(TestKit.Ct);
        // StartAsync returns after the handshake; the server maps the connection to its user right after that.
        // A barrier that round-trips proves user-targeted events reach this connection.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await probe.BarrierAsync(TimeSpan.FromSeconds(3));
                return probe;
            }
            catch (TimeoutException) when (attempt < 5)
            {
            }
        }
    }

    private void Listen<T>(string name) => _hub.On<T>(name, payload => Record(name, payload!));

    private void Record(string name, object payload)
    {
        List<Waiter> matched;
        lock (_gate)
        {
            _events.Add((name, payload));
            matched = _waiters.Where(w => w.Name == name && SafeMatch(w, payload)).ToList();
            foreach (var waiter in matched) _waiters.Remove(waiter);
        }
        foreach (var waiter in matched) waiter.Completion.TrySetResult(payload);
    }

    /// <summary>A predicate that throws (e.g. on an unrelated payload) is a non-match, never a crash of the receive loop.</summary>
    private static bool SafeMatch(Waiter waiter, object payload)
    {
        try
        {
            return waiter.Match(payload);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The first received (or next arriving) <paramref name="name"/> event matching <paramref name="match"/>.</summary>
    public async Task<T> WaitAsync<T>(string name, Func<T, bool>? match = null, TimeSpan? timeout = null)
    {
        bool Matches(object payload) => payload is T typed && (match?.Invoke(typed) ?? true);

        Waiter waiter;
        lock (_gate)
        {
            foreach (var (eventName, payload) in _events)
                if (eventName == name && Matches(payload)) return (T)payload;
            waiter = new Waiter(name, Matches);
            _waiters.Add(waiter);
        }

        try
        {
            return (T)await waiter.Completion.Task.WaitAsync(timeout ?? DefaultTimeout, TestKit.Ct);
        }
        catch (TimeoutException)
        {
            lock (_gate) _waiters.Remove(waiter);
            throw new TimeoutException($"{_user} did not receive a matching {name} within {(timeout ?? DefaultTimeout).TotalSeconds:0.#}s. Received: {Describe()}");
        }
    }

    /// <summary>Every received <paramref name="name"/> event matching <paramref name="match"/> so far.</summary>
    public IReadOnlyList<T> All<T>(string name, Func<T, bool>? match = null)
    {
        lock (_gate)
            return _events.Where(e => e.Name == name && e.Payload is T typed && (match?.Invoke(typed) ?? true))
                .Select(e => (T)e.Payload)
                .ToList();
    }

    /// <summary>Names of every event received so far that concerns <paramref name="chatId"/>.</summary>
    public IReadOnlyList<string> EventsFor(Guid chatId)
    {
        lock (_gate)
            return _events.Where(e => Ev.ChatIdOf(e.Payload) == chatId).Select(e => e.Name).ToList();
    }

    /// <summary>
    /// Changes the mute of this user's Saved Messages to a unique value and waits for the resulting
    /// ChatSettingsChanged (sent only to this user): every event published before it has arrived by then.
    /// </summary>
    public async Task BarrierAsync(TimeSpan? timeout = null)
    {
        var seconds = Interlocked.Increment(ref s_barrierSeconds);
        using var response = await _user.Client.PutAsJsonAsync(ApiRoutes.Chats.SettingsPath(_user.SavedChatId),
            new UpdateChatSettingsRequest { MuteForSeconds = seconds }, SkyJson.Options, TestKit.Ct);
        await response.ShouldSucceedAsync();
        var settings = (await response.Content.ReadFromJsonAsync<ChatUserSettingsDto>(SkyJson.Options, TestKit.Ct))!;
        await WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged,
            e => e.ChatId == _user.SavedChatId && e.Settings.MutedUntil == settings.MutedUntil, timeout);
    }

    private string Describe()
    {
        lock (_gate)
            return _events.Count == 0 ? "nothing" : string.Join(", ", _events.Select(e => e.Name));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _hub.DisposeAsync();
        }
        catch
        {
            // Stopping a long-polling connection while the server shuts down may throw; irrelevant for the test.
        }
    }
}
