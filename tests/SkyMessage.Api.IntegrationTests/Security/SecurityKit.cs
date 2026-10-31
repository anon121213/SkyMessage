using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using SkiaSharp;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>A registered user: authenticated client (with its own client IP) plus the auth response.</summary>
internal sealed record TestUser(HttpClient Client, AuthResponse Auth, string Username, string DisplayName, string Ip)
{
    public Guid Id => Auth.User.Id;
    public Guid SessionId => Auth.SessionId;
    public string AccessToken => Auth.AccessToken;
    public Guid SavedChatId => Auth.User.SavedMessagesChatId;

    /// <summary>The <c>?t=</c> value this session's clients put on media URLs.</summary>
    public string Sign(Guid mediaId) => MediaSignature.Create(Auth.MediaKey, Auth.SessionId, mediaId);
}

/// <summary>The parsed body of an RFC 7807 response.</summary>
internal sealed record ProblemBody(int Status, string Title, string Code, string? MediaType, JsonElement Root)
{
    public bool HasErrorsFor(string field) =>
        Root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.TryGetProperty(field, out _);
}

internal static class SecurityKit
{
    public const string Password = "correct-horse-battery";
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(20);

    private static int _ip = Random.Shared.Next(1 << 10, 1 << 22);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A fresh client address. TestServer requests carry no remote IP, so the forwarded-headers middleware takes
    /// X-Forwarded-For from the request: every test identity gets its own per-IP rate-limit partition and the
    /// suite never trips the auth limiter by accident.
    /// </summary>
    public static string NextIp()
    {
        var n = Interlocked.Increment(ref _ip);
        return $"10.{(n >> 16) & 0xFF}.{(n >> 8) & 0xFF}.{n & 0xFF}";
    }

    public static string NewUsername(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..10];

    public static string Unique(string text) => $"{text} {Guid.NewGuid().ToString("N")[..8]}";

    public static DeviceInfoDto Device(string name) =>
        new() { DeviceName = name, Platform = ClientPlatform.Linux, AppVersion = "security-tests" };

    /// <summary>Anonymous JSON client with its own client IP.</summary>
    public static HttpClient NewClient(this ApiFactory factory, string? ip = null)
    {
        var client = factory.CreateJsonClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", ip ?? NextIp());
        return client;
    }

    public static HttpClient ClientWithToken(this ApiFactory factory, string? accessToken, string? ip = null)
    {
        var client = factory.NewClient(ip);
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static async Task<TestUser> RegisterUserAsync(this ApiFactory factory, string prefix = "user", string? displayName = null)
    {
        var ip = NextIp();
        var client = factory.NewClient(ip);
        var username = NewUsername(prefix);
        displayName ??= char.ToUpperInvariant(prefix[0]) + prefix[1..];
        using var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = username,
            Password = Password,
            DisplayName = displayName,
            Device = Device("phone"),
        }, SkyJson.Options, Ct);
        var auth = await response.ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(client, auth, username, displayName, ip);
    }

    /// <summary>Another device of the same person: a new session with its own token and media key.</summary>
    public static async Task<TestUser> SignInAgainAsync(this ApiFactory factory, TestUser user, string deviceName = "second device")
    {
        var ip = NextIp();
        var client = factory.NewClient(ip);
        using var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest { Login = user.Username, Password = Password, Device = Device(deviceName) }, SkyJson.Options, Ct);
        var auth = await response.ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return user with { Client = client, Auth = auth, Ip = ip };
    }

    // ---- HTTP helpers -------------------------------------------------------------------------------------------

    public static Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: SkyJson.Options);
        return client.SendAsync(request, Ct);
    }

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        return await response.ReadAsync<T>();
    }

    public static async Task<T> PostJsonAsync<T>(this HttpClient client, string path, object? body)
    {
        using var response = await client.SendJsonAsync(HttpMethod.Post, path, body);
        return await response.ReadAsync<T>();
    }

    public static async Task<T> PutJsonAsync<T>(this HttpClient client, string path, object? body)
    {
        using var response = await client.SendJsonAsync(HttpMethod.Put, path, body);
        return await response.ReadAsync<T>();
    }

    public static async Task<T> PatchJsonAsync<T>(this HttpClient client, string path, object? body)
    {
        using var response = await client.SendJsonAsync(HttpMethod.Patch, path, body);
        return await response.ReadAsync<T>();
    }

    /// <summary>Sends and expects 204.</summary>
    public static async Task NoContentAsync(this HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var response = await client.SendJsonAsync(method, path, body);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    /// <summary>Sends and expects problem details with this status and code.</summary>
    public static async Task<ProblemBody> ProblemAsync(this HttpClient client, HttpMethod method, string path, object? body,
        HttpStatusCode status, string code)
    {
        using var response = await client.SendJsonAsync(method, path, body);
        return await response.ShouldBeProblemAsync(status, code);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        await response.ShouldHaveStatusAsync(expected);
        return (await response.Content.ReadFromJsonAsync<T>(SkyJson.Options, Ct))!;
    }

    public static async Task ShouldHaveStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected) return;
        var body = await response.Content.ReadAsStringAsync(Ct);
        throw new ShouldAssertException(
            $"{response.RequestMessage?.Method} /{response.RequestMessage?.RequestUri?.PathAndQuery.TrimStart('/')}: " +
            $"expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode}. Body: {Truncate(body)}");
    }

    /// <summary>
    /// Asserts the response is problem details <c>{status, title, code}</c> with a JSON media type. The exact
    /// <c>application/problem+json</c> media type is checked separately in <see cref="ProblemDetailsTests"/>.
    /// </summary>
    public static async Task<ProblemBody> ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await response.ShouldHaveStatusAsync(status);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var where = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}";
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        mediaType.ShouldNotBeNull($"{where}: problem details without a content type. Body: {Truncate(text)}");
        mediaType.ShouldContain("json", Case.Insensitive, $"{where}: body: {Truncate(text)}");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ShouldAssertException($"{where}: expected problem details JSON, got: {Truncate(text)}");
        }

        root.ValueKind.ShouldBe(JsonValueKind.Object, where);
        root.TryGetProperty("status", out var statusElement).ShouldBeTrue($"{where}: no 'status' in {Truncate(text)}");
        statusElement.GetInt32().ShouldBe((int)status, where);
        root.TryGetProperty("title", out var titleElement).ShouldBeTrue($"{where}: no 'title' in {Truncate(text)}");
        var title = titleElement.GetString();
        title.ShouldNotBeNullOrWhiteSpace(where);
        root.TryGetProperty("code", out var codeElement).ShouldBeTrue($"{where}: no 'code' in {Truncate(text)}");
        codeElement.GetString().ShouldBe(code, $"{where}: {Truncate(text)}");
        return new ProblemBody((int)status, title!, code, mediaType, root);
    }

    public static string Truncate(string text, int max = 600) => text.Length <= max ? text : text[..max] + "…";

    // ---- Domain shortcuts -----------------------------------------------------------------------------------------

    public static Task<MessageDto> SendTextAsync(this TestUser user, Guid chatId, string text, Guid? replyTo = null) =>
        user.Client.PostJsonAsync<MessageDto>(ApiRoutes.Messages.RootPath(chatId), new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = text,
            ReplyToMessageId = replyTo,
        });

    public static Task<MessageDto> SendMediaAsync(this TestUser user, Guid chatId, string? caption, params Guid[] mediaIds) =>
        user.Client.PostJsonAsync<MessageDto>(ApiRoutes.Messages.RootPath(chatId), new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = caption,
            MediaIds = mediaIds,
        });

    public static Task<ChatSummaryDto> OpenPrivateChatAsync(this TestUser user, TestUser peer) =>
        user.Client.PostJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = peer.Id });

    public static async Task<ChatSummaryDto> CreateGroupAsync(this TestUser owner, string title, params TestUser[] members)
    {
        var result = await owner.Client.PostJsonAsync<CreateGroupResultDto>(ApiRoutes.Chats.Groups, new CreateGroupRequest
        {
            Title = title,
            MemberIds = members.Select(m => m.Id).ToList(),
        });
        result.Failed.ShouldBeEmpty();
        return result.Chat;
    }

    public static async Task<MessagesPageDto> PageAsync(this TestUser user, Guid chatId) =>
        await user.Client.GetJsonAsync<MessagesPageDto>(ApiRoutes.Messages.PagePath(chatId, limit: Limits.MessagesPageMax));
}

/// <summary>Hand-made access tokens for authentication tests (signed with the test host's own key unless told otherwise).</summary>
internal static class TokenKit
{
    public static JwtOptions Options(ApiFactory factory) => factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    public static Dictionary<string, object> Claims(Guid userId, Guid sessionId) => new()
    {
        ["sub"] = userId.ToString(),
        ["sid"] = sessionId.ToString(),
        ["name"] = "forged",
        ["jti"] = Guid.NewGuid().ToString("N"),
    };

    public static string Create(ApiFactory factory, Dictionary<string, object> claims, DateTime? notBefore = null, DateTime? expires = null,
        string? issuer = null, string? audience = null, string? signingKey = null)
    {
        var options = Options(factory);
        var now = DateTime.UtcNow;
        var nbf = notBefore ?? now.AddMinutes(-1);
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? options.Issuer,
            Audience = audience ?? options.Audience,
            IssuedAt = nbf,
            NotBefore = nbf,
            Expires = expires ?? now.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? options.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>An <c>alg: none</c> token with otherwise perfect claims.</summary>
    public static string Unsigned(ApiFactory factory, Guid userId, Guid sessionId)
    {
        var options = Options(factory);
        var now = DateTimeOffset.UtcNow;
        var header = JsonSerializer.Serialize(new Dictionary<string, object> { ["alg"] = "none", ["typ"] = "JWT" });
        var payload = new Dictionary<string, object>(Claims(userId, sessionId))
        {
            ["iss"] = options.Issuer,
            ["aud"] = options.Audience,
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["iat"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
        };
        return $"{Base64Url(header)}.{Base64Url(JsonSerializer.Serialize(payload))}.";
    }

    /// <summary>The payload of a JWT (no validation).</summary>
    public static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1];
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(part);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static string Base64Url(string json) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}

/// <summary>Real media files (decodable by SkiaSharp / ffmpeg) and the chunked upload flow.</summary>
internal static class MediaKit
{
    /// <summary>The server shells out to ffmpeg (MediaProcessing:FfmpegPath = "ffmpeg"); without it voice is stored as uploaded.</summary>
    public static readonly bool FfmpegAvailable = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, "ffmpeg")) || File.Exists(Path.Combine(dir, "ffmpeg.exe")));

    public static byte[] Jpeg(int width = 64, int height = 48, byte red = 108, byte green = 92, byte blue = 231)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(red, green, blue));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    /// <summary>16-bit PCM mono WAV with a pulsing tone (a voice message as a recorder might produce it).</summary>
    public static byte[] Wav(double seconds = 1.2, int sampleRate = 16000)
    {
        var samples = (int)(seconds * sampleRate);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (var i = 0; i < samples; i++)
            {
                var envelope = (i / (sampleRate / 4)) % 2 == 0 ? 1.0 : 0.25;
                writer.Write((short)(Math.Sin(2 * Math.PI * 330 * i / sampleRate) * 14000 * envelope));
            }
        }
        return stream.ToArray();
    }

    /// <summary>Creates the upload, sends every chunk and completes it; waits until processing finished unless told not to.</summary>
    public static async Task<MediaDto> UploadAsync(TestUser user, MediaKind kind, string fileName, string contentType, byte[] bytes, bool waitReady = true)
    {
        var session = await StartAsync(user, kind, fileName, contentType, bytes.Length);
        for (var index = 0; index < session.ChunkCount; index++)
        {
            using var put = await PutChunkAsync(user.Client, session, index, bytes);
            await put.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        using var complete = await user.Client.PostAsync(ApiRoutes.Media.UploadCompletePath(session.MediaId), null, SecurityKit.Ct);
        var media = await complete.ReadAsync<MediaDto>();
        return waitReady ? await WaitProcessedAsync(user, session.MediaId) : media;
    }

    public static Task<UploadSessionDto> StartAsync(TestUser user, MediaKind kind, string fileName, string contentType, long size) =>
        user.Client.PostJsonAsync<UploadSessionDto>(ApiRoutes.Media.Uploads, new CreateUploadRequest
        {
            Kind = kind,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = size,
        });

    public static Task<HttpResponseMessage> PutChunkAsync(HttpClient client, UploadSessionDto session, int index, byte[] bytes)
    {
        var offset = index * session.ChunkSize;
        var length = Math.Min(session.ChunkSize, bytes.Length - offset);
        var content = new ByteArrayContent(bytes, offset, length);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PutAsync(ApiRoutes.Media.UploadChunkPath(session.MediaId, index), content, SecurityKit.Ct);
    }

    /// <summary>Polls the owner's view until the worker finished (Ready); fails on Failed or after a minute.</summary>
    public static async Task<MediaDto> WaitProcessedAsync(TestUser owner, Guid mediaId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var media = await owner.Client.GetJsonAsync<MediaDto>(ApiRoutes.Media.ByIdPath(mediaId));
            if (media.Status == MediaStatus.Ready) return media;
            media.Status.ShouldNotBe(MediaStatus.Failed, $"processing of {media.Kind} {media.FileName} failed");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Media {mediaId} still {media.Status} after a minute.");
            await Task.Delay(100, SecurityKit.Ct);
        }
    }

    public static Task<MediaDto> UploadPhotoAsync(TestUser user, string fileName = "photo.jpg") =>
        UploadAsync(user, MediaKind.Photo, fileName, "image/jpeg", Jpeg());

    /// <summary>GET of the content with a <c>?t=</c> signature and no Authorization header (how &lt;img&gt; loads it).</summary>
    public static Task<HttpResponseMessage> GetSignedAsync(ApiFactory factory, string path, RangeHeaderValue? range = null)
    {
        var client = factory.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (range is not null) request.Headers.Range = range;
        return client.SendAsync(request, SecurityKit.Ct);
    }
}
