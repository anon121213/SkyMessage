using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Persistence;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>A registered user (one session = one device) with a bearer-authenticated client.</summary>
internal sealed record TestUser(HttpClient Client, AuthResponse Auth)
{
    public Guid Id => Auth.User.Id;

    /// <summary>The <c>?t=</c> token this session puts on media URLs (&lt;img&gt;/&lt;video&gt; cannot send headers).</summary>
    public string Sign(Guid mediaId) => MediaSignature.Create(Auth.MediaKey, Auth.SessionId, mediaId);
}

/// <summary>HTTP helpers for the media integration tests (users, uploads, chats, content, database access).</summary>
internal static class MediaTestKit
{
    public const string Password = "correct-horse-battery";
    public static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(90);

    private static readonly DeviceInfoDto Device = new() { DeviceName = "media-tests", Platform = ClientPlatform.Linux, AppVersion = "test" };
    private static int _clientAddressCounter;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static StorageOptions Storage(this ApiFactory factory) =>
        factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value;

    public static MediaProcessingOptions Processing(this ApiFactory factory) =>
        factory.Services.GetRequiredService<IOptions<MediaProcessingOptions>>().Value;

    // ---------------------------------------------------------------- users

    public static async Task<TestUser> RegisterUserAsync(this ApiFactory factory, string displayName = "Media Tester")
    {
        var client = factory.CreateJsonClient();
        var auth = await SendAuthAsync(client, ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = "m" + Guid.NewGuid().ToString("N")[..15],
            Password = Password,
            DisplayName = displayName,
            Device = Device,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(client, auth);
    }

    /// <summary>The same session talking to another host of the same deployment (shared database and JWT key).</summary>
    public static TestUser On(this TestUser user, WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Auth.AccessToken);
        return user with { Client = client };
    }

    /// <summary>Signs the same user in again: another session (device) with its own media key.</summary>
    public static async Task<TestUser> SignInAgainAsync(this ApiFactory factory, TestUser user)
    {
        var client = factory.CreateJsonClient();
        var auth = await SendAuthAsync(client, ApiRoutes.Auth.Login, new LoginRequest
        {
            Login = user.Auth.User.Username,
            Password = Password,
            Device = Device,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(client, auth);
    }

    private static async Task<AuthResponse> SendAuthAsync<TRequest>(HttpClient client, string path, TRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: SkyJson.Options) };
        // Register/login are rate limited per client address (30/min). TestServer requests carry no address, so the
        // forwarded-for header gives each call its own: this suite signs in far more users than one window allows.
        request.Headers.Add("X-Forwarded-For", NextClientAddress());
        using var response = await client.SendAsync(request, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(SkyJson.Options, Ct))!;
    }

    private static string NextClientAddress()
    {
        var n = Interlocked.Increment(ref _clientAddressCounter);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }

    // ---------------------------------------------------------------- uploads

    public static CreateUploadRequest UploadRequest(MediaKind kind, string fileName, string contentType, long sizeBytes) =>
        new() { Kind = kind, FileName = fileName, ContentType = contentType, SizeBytes = sizeBytes };

    public static Task<HttpResponseMessage> PostUploadAsync(this HttpClient client, CreateUploadRequest request) =>
        client.PostAsJsonAsync(ApiRoutes.Media.Uploads, request, SkyJson.Options, Ct);

    public static async Task<UploadSessionDto> StartUploadAsync(this TestUser user, CreateUploadRequest request)
    {
        using var response = await user.Client.PostUploadAsync(request);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UploadSessionDto>(SkyJson.Options, Ct))!;
    }

    public static async Task<UploadSessionDto> GetUploadAsync(this TestUser user, Guid mediaId)
    {
        using var response = await user.Client.GetAsync(ApiRoutes.Media.UploadPath(mediaId), Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UploadSessionDto>(SkyJson.Options, Ct))!;
    }

    public static Task<HttpResponseMessage> PutChunkAsync(this HttpClient client, Guid mediaId, int index, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PutAsync(ApiRoutes.Media.UploadChunkPath(mediaId, index), content, Ct);
    }

    public static async Task PutChunkOkAsync(this TestUser user, Guid mediaId, int index, byte[] bytes)
    {
        using var response = await user.Client.PutChunkAsync(mediaId, index, bytes);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    /// <summary>Bytes of chunk <paramref name="index"/>: chunk i holds [i·chunkSize, min((i+1)·chunkSize, length)).</summary>
    public static byte[] Slice(byte[] content, int chunkSize, int index)
    {
        var start = index * chunkSize;
        return content[start..Math.Min(content.Length, start + chunkSize)];
    }

    public static async Task PutAllChunksAsync(this TestUser user, UploadSessionDto session, byte[] content)
    {
        for (var index = 0; index < session.ChunkCount; index++)
            await user.PutChunkOkAsync(session.MediaId, index, Slice(content, session.ChunkSize, index));
    }

    public static Task<HttpResponseMessage> PostCompleteAsync(this HttpClient client, Guid mediaId) =>
        client.PostAsync(ApiRoutes.Media.UploadCompletePath(mediaId), content: null, Ct);

    public static async Task<MediaDto> CompleteAsync(this TestUser user, Guid mediaId)
    {
        using var response = await user.Client.PostCompleteAsync(mediaId);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MediaDto>(SkyJson.Options, Ct))!;
    }

    /// <summary>Create + every chunk + complete. Returns what "complete" answered (normally Processing).</summary>
    public static async Task<MediaDto> UploadAsync(this TestUser user, MediaKind kind, byte[] content, string contentType, string fileName,
        Func<CreateUploadRequest, CreateUploadRequest>? customize = null)
    {
        var request = UploadRequest(kind, fileName, contentType, content.Length);
        var session = await user.StartUploadAsync(customize?.Invoke(request) ?? request);
        await user.PutAllChunksAsync(session, content);
        return await user.CompleteAsync(session.MediaId);
    }

    /// <summary>Uploads and waits until processing finished (Ready or Failed).</summary>
    public static async Task<MediaDto> UploadProcessedAsync(this TestUser user, MediaKind kind, byte[] content, string contentType, string fileName,
        Func<CreateUploadRequest, CreateUploadRequest>? customize = null)
    {
        var completed = await user.UploadAsync(kind, content, contentType, fileName, customize);
        return await user.WaitProcessedAsync(completed.Id);
    }

    public static async Task<MediaDto> UploadReadyAsync(this TestUser user, MediaKind kind, byte[] content, string contentType, string fileName,
        Func<CreateUploadRequest, CreateUploadRequest>? customize = null)
    {
        var media = await user.UploadProcessedAsync(kind, content, contentType, fileName, customize);
        media.Status.ShouldBe(MediaStatus.Ready, $"{kind} {fileName} ({contentType}) should have been processed");
        return media;
    }

    /// <summary>A small, valid, unattached photo (PNG kept as uploaded).</summary>
    public static Task<MediaDto> UploadPhotoAsync(this TestUser user, string fileName = "photo.png") =>
        user.UploadReadyAsync(MediaKind.Photo, TestImages.Png(64, 48), "image/png", fileName);

    public static Task<HttpResponseMessage> GetMediaResponseAsync(this HttpClient client, Guid mediaId) =>
        client.GetAsync(ApiRoutes.Media.ByIdPath(mediaId), Ct);

    public static async Task<MediaDto> GetMediaAsync(this TestUser user, Guid mediaId)
    {
        using var response = await user.Client.GetMediaResponseAsync(mediaId);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MediaDto>(SkyJson.Options, Ct))!;
    }

    /// <summary>Polls the owner's metadata until processing finished (bounded wait, growing interval).</summary>
    public static async Task<MediaDto> WaitProcessedAsync(this TestUser owner, Guid mediaId)
    {
        var deadline = DateTimeOffset.UtcNow + ProcessingTimeout;
        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            var media = await owner.GetMediaAsync(mediaId);
            if (media.Status is MediaStatus.Ready or MediaStatus.Failed) return media;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Media {mediaId} ({media.Kind}) is still {media.Status} after {ProcessingTimeout.TotalSeconds:0} s.");
            await Task.Delay(delay, Ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 250));
        }
    }

    // ---------------------------------------------------------------- content

    public static async Task<HttpResponseMessage> GetContentAsync(this HttpClient client, Guid mediaId, string? signature = null,
        bool download = false, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Media.ContentPath(mediaId, signature, download));
        configure?.Invoke(request);
        return await client.SendAsync(request, Ct);
    }

    public static Task<HttpResponseMessage> GetThumbnailAsync(this HttpClient client, Guid mediaId, string? signature = null) =>
        client.GetAsync(ApiRoutes.Media.ThumbnailPath(mediaId, signature), Ct);

    public static async Task<byte[]> DownloadAsync(this HttpClient client, Guid mediaId, string? signature = null)
    {
        using var response = await client.GetContentAsync(mediaId, signature);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    public static async Task<byte[]> DownloadThumbnailAsync(this HttpClient client, Guid mediaId, string? signature = null)
    {
        using var response = await client.GetThumbnailAsync(mediaId, signature);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/jpeg");
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    /// <summary>Content, thumbnail and metadata are all readable by this viewer.</summary>
    public static async Task ShouldSeeAsync(this TestUser viewer, MediaDto media)
    {
        using (var content = await viewer.Client.GetContentAsync(media.Id))
            await content.ShouldHaveStatusAsync(HttpStatusCode.OK);
        if (media.HasThumbnail)
        {
            using var thumbnail = await viewer.Client.GetThumbnailAsync(media.Id);
            await thumbnail.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }
        (await viewer.GetMediaAsync(media.Id)).Id.ShouldBe(media.Id);
    }

    /// <summary>Content, thumbnail and metadata are all 404 <c>media_not_found</c> for this viewer (no existence leak).</summary>
    public static async Task ShouldNotSeeAsync(this TestUser viewer, Guid mediaId)
    {
        using (var content = await viewer.Client.GetContentAsync(mediaId))
            await content.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var thumbnail = await viewer.Client.GetThumbnailAsync(mediaId))
            await thumbnail.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var metadata = await viewer.Client.GetMediaResponseAsync(mediaId))
            await metadata.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }

    // ---------------------------------------------------------------- chats & profile

    public static async Task<Guid> OpenPrivateChatAsync(this TestUser user, Guid peerId)
    {
        using var response = await user.Client.PostAsJsonAsync(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = peerId }, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ChatSummaryDto>(SkyJson.Options, Ct))!.Id;
    }

    public static async Task<Guid> CreateGroupAsync(this TestUser owner, IEnumerable<TestUser> members, Guid? avatarMediaId = null)
    {
        var request = new CreateGroupRequest { Title = "Media group", MemberIds = members.Select(m => m.Id).ToList(), AvatarMediaId = avatarMediaId };
        using var response = await owner.Client.PostAsJsonAsync(ApiRoutes.Chats.Groups, request, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<CreateGroupResultDto>(SkyJson.Options, Ct))!;
        result.Failed.ShouldBeEmpty();
        return result.Chat.Id;
    }

    public static async Task<MessageDto> SendMediaAsync(this TestUser sender, Guid chatId, params Guid[] mediaIds)
    {
        var request = new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = mediaIds };
        using var response = await sender.Client.PostAsJsonAsync(ApiRoutes.Messages.RootPath(chatId), request, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MessageDto>(SkyJson.Options, Ct))!;
    }

    public static async Task<IReadOnlyList<MessageDto>> ForwardAsync(this TestUser user, Guid fromChatId, Guid toChatId, params Guid[] messageIds)
    {
        var request = new ForwardMessagesRequest
        {
            FromChatId = fromChatId,
            MessageIds = messageIds,
            ClientMessageIds = messageIds.Select(_ => Guid.NewGuid()).ToList(),
        };
        using var response = await user.Client.PostAsJsonAsync(ApiRoutes.Messages.ForwardPath(toChatId), request, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<MessageDto>>(SkyJson.Options, Ct))!;
    }

    public static async Task DeleteMessageAsync(this TestUser user, Guid chatId, Guid messageId, bool forEveryone)
    {
        var request = new DeleteMessagesRequest { MessageIds = [messageId], ForEveryone = forEveryone };
        using var response = await user.Client.PostAsJsonAsync(ApiRoutes.Messages.DeletePath(chatId), request, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    public static async Task ClearHistoryAsync(this TestUser user, Guid chatId)
    {
        using var response = await user.Client.PostAsJsonAsync(ApiRoutes.Chats.ClearHistoryPath(chatId), new ClearHistoryRequest(), SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    public static async Task DeleteChatForEveryoneAsync(this TestUser user, Guid chatId)
    {
        using var response = await user.Client.DeleteAsync(ApiRoutes.Chats.DeletePath(chatId, forEveryone: true), Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    public static async Task LeaveChatAsync(this TestUser user, Guid chatId)
    {
        using var response = await user.Client.PostAsync(ApiRoutes.Chats.LeavePath(chatId), content: null, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    public static async Task SetChatAvatarAsync(this TestUser user, Guid chatId, Guid mediaId)
    {
        using var response = await user.Client.PutAsJsonAsync(ApiRoutes.Chats.AvatarPath(chatId), new SetChatAvatarRequest { MediaId = mediaId }, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    public static async Task SetAvatarAsync(this TestUser user, Guid mediaId)
    {
        using var response = await user.Client.PutAsJsonAsync(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = mediaId }, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<MeDto>(SkyJson.Options, Ct))!.AvatarMediaId.ShouldBe(mediaId);
    }

    public static async Task RemoveAvatarAsync(this TestUser user)
    {
        using var response = await user.Client.DeleteAsync(ApiRoutes.Users.MeAvatar, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    public static async Task SetProfilePhotoPrivacyAsync(this TestUser user, PrivacyLevel level)
    {
        using var response = await user.Client.PutAsJsonAsync(ApiRoutes.Users.MePrivacy, new PrivacySettingsDto { ProfilePhoto = level }, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PrivacySettingsDto>(SkyJson.Options, Ct))!.ProfilePhoto.ShouldBe(level);
    }

    public static async Task AddContactAsync(this TestUser user, TestUser contact)
    {
        using var response = await user.Client.PostAsJsonAsync(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = contact.Id }, SkyJson.Options, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    public static async Task BlockAsync(this TestUser user, TestUser target)
    {
        using var response = await user.Client.PutAsync(ApiRoutes.Blocks.ByUserIdPath(target.Id), content: null, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    // ---------------------------------------------------------------- assertions

    public static async Task ShouldHaveStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected) return;
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered: {body}");
    }

    /// <summary>Problem details with the given status and machine-readable <c>code</c>.</summary>
    public static async Task ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        var what = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered {(int)response.StatusCode}: {body}";
        response.StatusCode.ShouldBe(status, what);
        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("code", out var actual).ShouldBeTrue(what);
        actual.GetString().ShouldBe(code, what);
    }

    public static void ShouldBeSameBytes(this byte[] actual, byte[] expected, string what)
    {
        actual.Length.ShouldBe(expected.Length, what);
        Sha256Hex(actual).ShouldBe(Sha256Hex(expected), what);
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Deterministic pseudo-random bytes (incompressible, so nothing can "accidentally" match).</summary>
    public static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---------------------------------------------------------------- server internals

    public static async Task<T> QueryDbAsync<T>(this ApiFactory factory, Func<AppDbContext, Task<T>> query)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// Forgets the 60 s positive access decisions (SPEC §6.4) and other cached lookups, so a test can observe the
    /// database rule right after revoking access from a viewer who already read the media.
    /// </summary>
    public static void ForgetCachedDecisions(this ApiFactory factory) =>
        ((MemoryCache)factory.Services.GetRequiredService<IMemoryCache>()).Compact(1.0);
}
