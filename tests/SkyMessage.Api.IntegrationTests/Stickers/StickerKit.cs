using System.Net.Http.Headers;
using SkiaSharp;
using Shouldly;
using SkyMessage.Api.IntegrationTests.Messaging;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Api.IntegrationTests.Stickers;

/// <summary>Uploads, packs and stickers through the real API (uploads are processed by the real pipeline).</summary>
internal static class StickerKit
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(90);

    /// <summary>A red disc on a transparent background.</summary>
    public static byte[] Png(int width = 200, int height = 200)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(230, 20, 20), IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) * 0.4f, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)!;
        return data.ToArray();
    }

    /// <summary>Create, every chunk, complete, then waits for processing (Ready or Failed).</summary>
    public static async Task<MediaDto> UploadAsync(this TestUser user, MediaKind kind, byte[] content, string contentType, string fileName)
    {
        var session = await (await user.PostRawAsync(ApiRoutes.Media.Uploads, new CreateUploadRequest
        {
            Kind = kind,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.Length,
        })).ReadAsync<UploadSessionDto>();
        for (var index = 0; index < session.ChunkCount; index++)
        {
            var start = index * session.ChunkSize;
            var chunk = new ByteArrayContent(content[start..Math.Min(content.Length, start + session.ChunkSize)]);
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await user.Client.PutAsync(ApiRoutes.Media.UploadChunkPath(session.MediaId, index), chunk, TestKit.Ct);
            await put.ShouldSucceedAsync();
        }
        await (await user.PostEmptyAsync(ApiRoutes.Media.UploadCompletePath(session.MediaId))).ReadAsync<MediaDto>();

        var deadline = DateTimeOffset.UtcNow + ProcessingTimeout;
        while (true)
        {
            var media = await user.GetAsync<MediaDto>(ApiRoutes.Media.ByIdPath(session.MediaId));
            if (media.Status is MediaStatus.Ready or MediaStatus.Failed) return media;
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Media {media.Id} is still {media.Status}.");
            await Task.Delay(50, TestKit.Ct);
        }
    }

    /// <summary>A sticker source as the apps upload it: a file (its original bytes).</summary>
    public static Task<MediaDto> UploadSourceAsync(this TestUser user, byte[]? png = null) =>
        user.UploadAsync(MediaKind.File, png ?? Png(), "image/png", "sticker.png");

    public static Task<HttpResponseMessage> TryCreatePackAsync(this TestUser user, Guid id, string title) =>
        user.PostRawAsync(ApiRoutes.Stickers.Packs, new CreateStickerPackRequest { Id = id, Title = title });

    public static async Task<StickerPackDto> CreatePackAsync(this TestUser user, string title = "Котики") =>
        await (await user.TryCreatePackAsync(Guid.CreateVersion7(), title)).ReadAsync<StickerPackDto>();

    public static Task<HttpResponseMessage> TryAddStickerAsync(this TestUser user, Guid packId, Guid mediaId, params string[] emoji) =>
        user.PostRawAsync(ApiRoutes.Stickers.PackStickersPath(packId), new AddStickerRequest { Id = Guid.CreateVersion7(), MediaId = mediaId, Emoji = emoji });

    public static async Task<StickerPackDto> AddStickerAsync(this TestUser user, Guid packId, params string[] emoji)
    {
        var source = await user.UploadSourceAsync();
        return await (await user.TryAddStickerAsync(packId, source.Id, emoji.Length == 0 ? ["😺"] : emoji)).ReadAsync<StickerPackDto>();
    }

    /// <summary>A pack of the user's with one sticker.</summary>
    public static async Task<(StickerPackDto Pack, StickerDto Sticker)> PackWithStickerAsync(this TestUser user, string title = "Котики")
    {
        var pack = await user.CreatePackAsync(title);
        pack = await user.AddStickerAsync(pack.Id, "😺", "❤️");
        return (pack, pack.Stickers.Single());
    }

    public static Task<StickerStateDto> StickerStateAsync(this TestUser user) => user.GetAsync<StickerStateDto>(ApiRoutes.Stickers.Root);

    public static async Task<StickerStateDto> InstallAsync(this TestUser user, Guid packId) =>
        await (await user.PutEmptyAsync(ApiRoutes.Stickers.InstalledPath(packId))).ReadAsync<StickerStateDto>();

    public static async Task<MessageDto> SendStickerAsync(this TestUser user, Guid chatId, Guid stickerId, Guid? replyTo = null) =>
        await user.SendAsync(chatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), StickerId = stickerId, ReplyToMessageId = replyTo });

    /// <summary>Sticker rows straight into the pack (the limit tests need many; they reuse an existing sticker's media).</summary>
    public static Task AddStickerRowsAsync(this ApiFactory factory, Guid packId, Guid mediaId, int count) =>
        factory.WithDbAsync(async db =>
        {
            var now = TestKit.Now();
            for (var i = 0; i < count; i++)
                db.Stickers.Add(new Sticker { Id = Guid.CreateVersion7(), PackId = packId, MediaId = mediaId, Emoji = ["🙂"], Position = 1000 + i, CreatedAt = now });
            await db.SaveChangesAsync(TestKit.Ct);
        });

    public static void ShouldBeStickerOf(this MediaDto media, StickerDto sticker)
    {
        media.Kind.ShouldBe(MediaKind.Photo);
        media.ContentType.ShouldBe("image/webp");
        var info = media.Sticker.ShouldNotBeNull();
        info.StickerId.ShouldBe(sticker.Id);
        info.PackId.ShouldBe(sticker.PackId);
        info.Emoji.ShouldBe(sticker.Emoji[0]);
    }
}
