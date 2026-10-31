using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.Persistence;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>A signed-in device: user + session + an HTTP client bound to one API host.</summary>
internal sealed record CallUser(WebApplicationFactory<Program> Host, HttpClient Http, AuthResponse Auth, string Username, string DisplayName)
{
    public Guid Id => Auth.User.Id;
    public Guid SessionId => Auth.SessionId;
    public string AccessToken => Auth.AccessToken;

    /// <summary>This device's LiveKit participant identity.</summary>
    public string Identity => LiveKitNames.Identity(Id, SessionId);

    public override string ToString() => $"{DisplayName} ({Id:N})";
}

/// <summary>HTTP, chat, call and database helpers for the calls integration tests.</summary>
internal static class CallKit
{
    public const string Password = "correct-horse-battery";
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    private static int _ipSequence = Random.Shared.Next(1 << 12, 1 << 22);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The test server has no remote address, so the forwarded-headers middleware honours X-Forwarded-For: every
    /// client gets its own rate-limit partition (Auth allows 30 registrations per minute and address).
    /// </summary>
    private static string NewIp()
    {
        var n = Interlocked.Increment(ref _ipSequence);
        return $"10.{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }

    public static string NewUsername() => "call" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>For state that background work changes right after the event a test can await (no fixed sleeps).</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + EventTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"Timed out waiting until {what}");
            await Task.Delay(20, Ct);
        }
    }

    public static HttpClient NewClient(this WebApplicationFactory<Program> host, string? accessToken = null)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", NewIp());
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static DeviceInfoDto Device(string name) =>
        new() { DeviceName = name, Platform = ClientPlatform.Linux, AppVersion = "1.0.0-test" };

    public static async Task<CallUser> NewUserAsync(this WebApplicationFactory<Program> host, string displayName = "Caller")
    {
        var username = NewUsername();
        var client = host.NewClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = username,
            Password = Password,
            DisplayName = displayName,
            Device = Device("tests"),
        }, SkyJson.Options, Ct);
        var auth = await response.ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new CallUser(host, client, auth, username, displayName);
    }

    /// <summary>The same user signed in on another device (own session, own LiveKit identity).</summary>
    public static async Task<CallUser> SignInAgainAsync(this CallUser user)
    {
        var client = user.Host.NewClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login, new LoginRequest
        {
            Login = user.Username,
            Password = Password,
            Device = Device("second device"),
        }, SkyJson.Options, Ct);
        var auth = await response.ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return user with { Http = client, Auth = auth };
    }

    /// <summary>The same device talking to another API host over the same database.</summary>
    public static CallUser On(this CallUser user, WebApplicationFactory<Program> host) =>
        user with { Host = host, Http = host.NewClient(user.AccessToken) };

    // ---- HTTP ----

    /// <summary>POST with a JSON body, or with no body at all when <paramref name="body"/> is null.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient http, string path, object? body) =>
        http.PostAsync(path, body is null ? null : JsonContent.Create(body, body.GetType(), options: SkyJson.Options), Ct);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            throw new ShouldAssertException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected} but got {(int)response.StatusCode} {body}");
        }
        return (await response.Content.ReadFromJsonAsync<T>(SkyJson.Options, Ct))!;
    }

    public static async Task ShouldBeAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected) return;
        var body = await response.Content.ReadAsStringAsync(Ct);
        throw new ShouldAssertException(
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected} but got {(int)response.StatusCode} {body}");
    }

    /// <summary>Asserts a problem-details answer with the given status and machine-readable code.</summary>
    public static async Task ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        var where = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}";
        ((int)response.StatusCode).ShouldBe((int)status, $"{where} answered {body}");
        string? actual = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("code", out var value)) actual = value.GetString();
        }
        catch (JsonException)
        {
        }
        actual.ShouldBe(code, $"{where} answered {body}");
    }

    // ---- chats ----

    public static async Task<Guid> PrivateChatWithAsync(this CallUser me, CallUser peer)
    {
        var response = await me.Http.PostJsonAsync(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = peer.Id });
        return (await response.ReadAsync<ChatSummaryDto>()).Id;
    }

    public static async Task<Guid> CreateGroupAsync(this CallUser owner, string title, params CallUser[] members)
    {
        var response = await owner.Http.PostJsonAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest
        {
            Title = title,
            MemberIds = members.Select(m => m.Id).ToList(),
        });
        var result = await response.ReadAsync<CreateGroupResultDto>();
        result.Failed.ShouldBeEmpty();
        return result.Chat.Id;
    }

    public static async Task RemoveMemberAsync(this CallUser admin, Guid chatId, CallUser member) =>
        await (await admin.Http.DeleteAsync(ApiRoutes.Chats.MemberPath(chatId, member.Id), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

    public static async Task LeaveChatAsync(this CallUser member, Guid chatId) =>
        await (await member.Http.PostJsonAsync(ApiRoutes.Chats.LeavePath(chatId), null)).ShouldBeAsync(HttpStatusCode.NoContent);

    public static async Task<MessagesPageDto> GetMessagesAsync(this CallUser user, Guid chatId) =>
        await (await user.Http.GetAsync(ApiRoutes.Messages.PagePath(chatId, limit: 100), Ct)).ReadAsync<MessagesPageDto>();

    public static async Task<ChatSummaryDto> GetChatAsync(this CallUser user, Guid chatId) =>
        await (await user.Http.GetAsync(ApiRoutes.Chats.ByIdPath(chatId), Ct)).ReadAsync<ChatSummaryDto>();

    public static async Task SetCallPrivacyAsync(this CallUser user, PrivacyLevel level)
    {
        var current = await (await user.Http.GetAsync(ApiRoutes.Users.MePrivacy, Ct)).ReadAsync<PrivacySettingsDto>();
        var response = await user.Http.PutAsJsonAsync(ApiRoutes.Users.MePrivacy, current with { Calls = level }, SkyJson.Options, Ct);
        (await response.ReadAsync<PrivacySettingsDto>()).Calls.ShouldBe(level);
    }

    public static async Task BlockAsync(this CallUser user, CallUser target) =>
        await (await user.Http.PutAsync(ApiRoutes.Blocks.ByUserIdPath(target.Id), null, Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

    public static async Task AddContactAsync(this CallUser user, CallUser contact, string? customName = null) =>
        await (await user.Http.PostJsonAsync(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = contact.Id, CustomName = customName }))
            .ShouldBeAsync(HttpStatusCode.OK);

    // ---- calls ----

    public static Task<HttpResponseMessage> TryStartCallAsync(this CallUser user, Guid chatId, bool isVideo = false) =>
        user.Http.PostJsonAsync(ApiRoutes.Calls.Root, new StartCallRequest { ChatId = chatId, IsVideo = isVideo });

    public static async Task<CallJoinInfoDto> StartCallAsync(this CallUser user, Guid chatId, bool isVideo = false) =>
        await (await user.TryStartCallAsync(chatId, isVideo)).ReadAsync<CallJoinInfoDto>();

    public static Task<HttpResponseMessage> TryAcceptAsync(this CallUser user, Guid callId) =>
        user.Http.PostJsonAsync(ApiRoutes.Calls.AcceptPath(callId), null);

    public static async Task<CallJoinInfoDto> AcceptAsync(this CallUser user, Guid callId) =>
        await (await user.TryAcceptAsync(callId)).ReadAsync<CallJoinInfoDto>();

    /// <summary><paramref name="busy"/> null sends no body at all (the request DTO is optional).</summary>
    public static Task<HttpResponseMessage> TryDeclineAsync(this CallUser user, Guid callId, bool? busy = null) =>
        user.Http.PostJsonAsync(ApiRoutes.Calls.DeclinePath(callId), busy is { } b ? new DeclineCallRequest { Busy = b } : null);

    public static async Task DeclineAsync(this CallUser user, Guid callId, bool? busy = null) =>
        await (await user.TryDeclineAsync(callId, busy)).ShouldBeAsync(HttpStatusCode.NoContent);

    public static Task<HttpResponseMessage> TryJoinAsync(this CallUser user, Guid callId) =>
        user.Http.PostJsonAsync(ApiRoutes.Calls.JoinPath(callId), null);

    public static async Task<CallJoinInfoDto> JoinAsync(this CallUser user, Guid callId) =>
        await (await user.TryJoinAsync(callId)).ReadAsync<CallJoinInfoDto>();

    public static Task<HttpResponseMessage> TryHangupAsync(this CallUser user, Guid callId) =>
        user.Http.PostJsonAsync(ApiRoutes.Calls.HangupPath(callId), null);

    public static async Task HangupAsync(this CallUser user, Guid callId) =>
        await (await user.TryHangupAsync(callId)).ShouldBeAsync(HttpStatusCode.NoContent);

    public static async Task<CallDto> GetCallAsync(this CallUser user, Guid callId) =>
        await (await user.Http.GetAsync(ApiRoutes.Calls.ByIdPath(callId), Ct)).ReadAsync<CallDto>();

    /// <summary>The chat's unfinished call, or null on 204.</summary>
    public static async Task<CallDto?> GetActiveCallAsync(this CallUser user, Guid chatId)
    {
        var response = await user.Http.GetAsync(ApiRoutes.Calls.ActivePath(chatId), Ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.ReadAsync<CallDto>();
    }

    public static async Task<IReadOnlyList<CallHistoryItemDto>> GetHistoryAsync(this CallUser user, DateTimeOffset? before = null, int limit = 50) =>
        await (await user.Http.GetAsync(ApiRoutes.Calls.HistoryPath(before, limit), Ct)).ReadAsync<List<CallHistoryItemDto>>();

    /// <summary>A rings B in their private chat; B answers.</summary>
    public static async Task<CallDto> ActivePrivateCallAsync(this CallUser caller, CallUser callee, Guid chatId)
    {
        var started = await caller.StartCallAsync(chatId);
        var accepted = await callee.AcceptAsync(started.Call.Id);
        accepted.Call.Status.ShouldBe(CallStatus.Active);
        return accepted.Call;
    }

    // ---- assertions on DTOs ----

    public static CallParticipantDto Participant(this CallDto call, CallUser user) =>
        call.Participants.SingleOrDefault(p => p.UserId == user.Id)
        ?? throw new ShouldAssertException($"{user} is not a participant of call {call.Id}");

    public static CallParticipantStatus StatusOf(this CallDto call, CallUser user) => call.Participant(user).Status;

    /// <summary>Exactly these participants with these statuses (any order).</summary>
    public static void ShouldHaveParticipants(this CallDto call, params (CallUser User, CallParticipantStatus Status)[] expected)
    {
        var actual = call.Participants.Select(p => $"{p.UserId:N}={p.Status}").Order().ToList();
        var wanted = expected.Select(e => $"{e.User.Id:N}={e.Status}").Order().ToList();
        actual.ShouldBe(wanted, $"participants of call {call.Id} ({call.Status})");
    }

    // ---- database and services of a host ----

    public static async Task<T> WithDbAsync<T>(this WebApplicationFactory<Program> host, Func<AppDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static async Task WithDbAsync(this WebApplicationFactory<Program> host, Func<AppDbContext, Task> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static Task<Call> LoadCallAsync(this WebApplicationFactory<Program> host, Guid callId) =>
        host.WithDbAsync(db => db.Calls.AsNoTracking().Include(c => c.Participants).SingleAsync(c => c.Id == callId, Ct));

    /// <summary>Moves a call (and its invitations) into the past, as if it had been ringing for <paramref name="age"/> already.</summary>
    public static Task BackdateCallAsync(this WebApplicationFactory<Program> host, Guid callId, TimeSpan age) =>
        host.WithDbAsync(async db =>
        {
            await db.Calls.Where(c => c.Id == callId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, c => c.CreatedAt - age), Ct);
            await db.CallParticipants.Where(p => p.CallId == callId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.InvitedAt, p => p.InvitedAt - age), Ct);
        });

    /// <summary>Runs the background maintenance logic right now (in its own scope, like the hosted service does).</summary>
    public static async Task RunMaintenanceAsync(this WebApplicationFactory<Program> host, Func<ICallMaintenance, Task> run)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await run(scope.ServiceProvider.GetRequiredService<ICallMaintenance>());
    }

    /// <summary>Call-log messages of a call in its chat (seen by <paramref name="viewer"/>).</summary>
    public static async Task<IReadOnlyList<MessageDto>> CallLogsAsync(this CallUser viewer, Guid chatId, Guid callId) =>
        (await viewer.GetMessagesAsync(chatId)).Messages.Where(m => m.Type == MessageType.Call && m.Call?.CallId == callId).ToList();
}
