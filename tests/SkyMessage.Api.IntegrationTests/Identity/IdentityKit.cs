using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using MediaEntity = SkyMessage.Domain.Entities.Media;
using SkyMessage.Infrastructure.Persistence;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Identity;

/// <summary>A registered user plus an authenticated client that has a client address of its own.</summary>
internal sealed record TestUser(ApiFactory Factory, HttpClient Client, AuthResponse Auth, string Password, string Ip)
{
    public Guid Id => Auth.User.Id;
    public string Username => Auth.User.Username;
    public Guid SessionId => Auth.SessionId;
    public string AccessToken => Auth.AccessToken;
}

/// <summary>HTTP, database and test-data helpers for the identity integration tests.</summary>
internal static class IdentityKit
{
    public const string DefaultPassword = "correct-horse-battery";
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    private static int _ipSequence = Random.Shared.Next(1 << 12, 1 << 22);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A fresh client address. The test server has no remote IP, so the API's forwarded-headers middleware takes
    /// X-Forwarded-For: every test user gets its own per-IP rate-limit partition (Auth: 30/min).
    /// </summary>
    public static string NewIp()
    {
        var n = Interlocked.Increment(ref _ipSequence);
        return $"10.{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }

    /// <summary>Unique valid username (letter first, 5..32 chars).</summary>
    public static string NewUsername(string prefix = "u") => prefix + Guid.NewGuid().ToString("N")[..12];

    public static DeviceInfoDto Device(string name = "tests", ClientPlatform platform = ClientPlatform.Linux) =>
        new() { DeviceName = name, Platform = platform, AppVersion = "1.0.0-test", OsVersion = "TestOS 1" };

    public static HttpClient NewClient(this ApiFactory factory, string? ip = null, string? accessToken = null)
    {
        var client = factory.CreateJsonClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip ?? NewIp());
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static RegisterRequest RegisterRequest(string? username = null, string displayName = "Test User", string password = DefaultPassword) =>
        new()
        {
            Username = username ?? NewUsername(),
            Password = password,
            DisplayName = displayName,
            Device = Device(),
        };

    public static async Task<TestUser> NewUserAsync(this ApiFactory factory, string? username = null, string displayName = "Test User", string password = DefaultPassword)
    {
        var ip = NewIp();
        var client = factory.NewClient(ip);
        var auth = await client.PostAs<AuthResponse>(ApiRoutes.Auth.Register, RegisterRequest(username, displayName, password));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(factory, client, auth, password, ip);
    }

    public static async Task<IReadOnlyList<TestUser>> NewUsersAsync(this ApiFactory factory, int count, string displayName = "Test User")
    {
        var users = new List<TestUser>(count);
        for (var i = 0; i < count; i++)
            users.Add(await factory.NewUserAsync(displayName: displayName));
        return users;
    }

    public static Task<HttpResponseMessage> TryLoginAsync(this ApiFactory factory, string login, string password, string? ip = null,
        string? defaultRegion = null, DeviceInfoDto? device = null) =>
        factory.NewClient(ip).Post(ApiRoutes.Auth.Login, new LoginRequest
        {
            Login = login,
            Password = password,
            Device = device ?? Device(),
            DefaultRegion = defaultRegion,
        });

    /// <summary>Signs the same user in again: another device with its own session and client.</summary>
    public static async Task<TestUser> SignInAgainAsync(this TestUser user, string deviceName = "second device", ClientPlatform platform = ClientPlatform.Android)
    {
        var ip = NewIp();
        var client = user.Factory.NewClient(ip);
        var auth = await client.PostAs<AuthResponse>(ApiRoutes.Auth.Login, new LoginRequest
        {
            Login = user.Username,
            Password = user.Password,
            Device = Device(deviceName, platform),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return user with { Client = client, Auth = auth, Ip = ip };
    }

    // ---- HTTP ----

    public static Task<HttpResponseMessage> Get(this HttpClient client, string path) => client.GetAsync(path, Ct);

    public static Task<HttpResponseMessage> Post(this HttpClient client, string path, object? body = null) =>
        client.PostAsync(path, Json(body), Ct);

    public static Task<HttpResponseMessage> Put(this HttpClient client, string path, object? body = null) =>
        client.PutAsync(path, Json(body), Ct);

    public static Task<HttpResponseMessage> Patch(this HttpClient client, string path, object body) =>
        client.PatchAsync(path, Json(body), Ct);

    private static JsonContent? Json(object? body) =>
        body is null ? null : JsonContent.Create(body, body.GetType(), options: SkyJson.Options);

    public static Task<HttpResponseMessage> Delete(this HttpClient client, string path) => client.DeleteAsync(path, Ct);

    public static async Task<T> GetAs<T>(this HttpClient client, string path) => await (await client.Get(path)).ReadAsync<T>();

    public static async Task<T> PostAs<T>(this HttpClient client, string path, object? body = null) => await (await client.Post(path, body)).ReadAsync<T>();

    public static async Task<T> PutAs<T>(this HttpClient client, string path, object? body = null) => await (await client.Put(path, body)).ReadAsync<T>();

    public static async Task<T> PatchAs<T>(this HttpClient client, string path, object body) => await (await client.Patch(path, body)).ReadAsync<T>();

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        if (!response.IsSuccessStatusCode)
            throw new ShouldAssertException($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected success, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, SkyJson.Options)!;
    }

    public static async Task ShouldBeStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            throw new ShouldAssertException($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        }
    }

    /// <summary>Asserts a problem-details failure with the given status and machine-readable code.</summary>
    public static async Task ShouldFailAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        var where = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}";
        if (response.StatusCode != status)
            throw new ShouldAssertException($"{where}: expected {(int)status} {code}, got {(int)response.StatusCode}: {body}");
        ProblemCode(body).ShouldBe(code, $"{where}: {body}");
    }

    /// <summary>Asserts that <paramref name="action"/> fails (any exception type).</summary>
    public static async Task<Exception> ShouldFailToRunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            return e;
        }
        throw new ShouldAssertException("Expected the call to fail, but it succeeded.");
    }

    public static string? ProblemCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // ---- Database ----

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

    public static Task<User> LoadUserAsync(this ApiFactory factory, Guid userId) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct));

    public static Task<Session> LoadSessionAsync(this ApiFactory factory, Guid sessionId) =>
        factory.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct));

    /// <summary>A processed photo owned by <paramref name="ownerId"/> (inserted directly: avatars only need the row).</summary>
    public static Task<Guid> CreateMediaAsync(this ApiFactory factory, Guid ownerId, MediaKind kind = MediaKind.Photo, MediaStatus status = MediaStatus.Ready) =>
        factory.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var media = new MediaEntity
            {
                OwnerId = ownerId,
                Kind = kind,
                Status = status,
                FileName = kind == MediaKind.Photo ? "avatar.jpg" : "file.bin",
                ContentType = kind == MediaKind.Photo ? "image/jpeg" : "application/octet-stream",
                SizeBytes = 1024,
                DeclaredSizeBytes = 1024,
                ChunkSize = Limits.DefaultChunkSize,
                ChunkCount = 1,
                Width = 64,
                Height = 64,
                CreatedAt = now,
                CompletedAt = status == MediaStatus.Ready ? now : null,
                UpdatedAt = now,
            };
            db.Media.Add(media);
            await db.SaveChangesAsync(Ct);
            return media.Id;
        });

    // ---- Relationships (contacts, blocks, chats) ----

    public static async Task AddContactAsync(this TestUser owner, TestUser contact, string? customName = null) =>
        await (await owner.Client.Post(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = contact.Id, CustomName = customName }))
            .ShouldBeStatusAsync(HttpStatusCode.OK);

    public static async Task BlockAsync(this TestUser blocker, TestUser target) =>
        await (await blocker.Client.Put(ApiRoutes.Blocks.ByUserIdPath(target.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);

    public static async Task SetPrivacyAsync(this TestUser user, Func<PrivacySettingsDto, PrivacySettingsDto> change)
    {
        var current = await user.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy);
        await user.Client.PutAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, change(current));
    }

    public static async Task<Guid> CreateGroupAsync(this TestUser owner, params TestUser[] members)
    {
        var result = await owner.Client.PostAs<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest
        {
            Title = "Group " + Guid.NewGuid().ToString("N")[..6],
            MemberIds = members.Select(m => m.Id).ToList(),
        });
        result.Failed.ShouldBeEmpty();
        return result.Chat.Id;
    }

    /// <summary>Private chat with a first message, so both sides see it (the peer's row starts hidden).</summary>
    public static async Task<Guid> CreatePrivateChatWithMessageAsync(this TestUser from, TestUser to, string text = "hello")
    {
        var chat = await from.Client.PostAs<ChatSummaryDto>(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = to.Id });
        await from.SendMessageAsync(chat.Id, text);
        return chat.Id;
    }

    public static async Task<MessageDto> SendMessageAsync(this TestUser from, Guid chatId, string text) =>
        await from.Client.PostAs<MessageDto>(ApiRoutes.Messages.RootPath(chatId), new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = text,
        });
}

/// <summary>Unique, valid phone numbers (the phone column is unique across the shared database).</summary>
internal static class TestPhones
{
    private static int _sequence = Random.Shared.Next(0, 5_000_000);

    /// <summary>Thai mobile: E.164 "+668…" and the national form "08x xxx xxxx".</summary>
    public static (string E164, string National) Thai()
    {
        var n = Interlocked.Increment(ref _sequence) % 10_000_000;
        var nsn = $"8{Random.Shared.Next(1, 10)}{n:D7}";
        return ($"+66{nsn}", $"0{nsn[..2]} {nsn[2..5]} {nsn[5..]}");
    }

    /// <summary>Russian mobile: E.164 "+79…" and the national form "8 (9xx) xxx-xx-xx".</summary>
    public static (string E164, string National) Russian()
    {
        var n = Interlocked.Increment(ref _sequence) % 10_000_000;
        var nsn = $"9{Random.Shared.Next(10, 100)}{n:D7}";
        return ($"+7{nsn}", $"8 ({nsn[..3]}) {nsn[3..6]}-{nsn[6..8]}-{nsn[8..]}");
    }
}
