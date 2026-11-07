using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkiaSharp;
using SkyMessage.Api.IntegrationTests.Messaging;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;
using Ffmpeg = SkyMessage.Api.IntegrationTests.Media.Ffmpeg;
using TestImages = SkyMessage.Api.IntegrationTests.Media.TestImages;

namespace SkyMessage.Api.IntegrationTests.Stickers;

/// <summary>
/// Sticker packs and GIFs (SPEC §6.6): only the creator edits a pack, anyone signed in sees and installs it, the limits,
/// my state synced to my devices by revision, sending stickers (only of existing packs) and saved GIFs (only while I can
/// still see them), content protection, and what stays after a pack is deleted.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StickerTests(ApiFactory factory)
{
    private static async Task ShouldReadImageAsync(TestUser viewer, Guid mediaId, string contentType)
    {
        using var content = await viewer.Client.GetAsync(ApiRoutes.Media.ContentPath(mediaId, null), TestKit.Ct);
        content.StatusCode.ShouldBe(HttpStatusCode.OK);
        content.Content.Headers.ContentType?.MediaType.ShouldBe(contentType);
        using var thumbnail = await viewer.Client.GetAsync(ApiRoutes.Media.ThumbnailPath(mediaId, null), TestKit.Ct);
        thumbnail.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Packs ----

    [Fact]
    public async Task A_pack_is_made_by_its_creator_kept_transparent_and_seen_and_installed_by_anyone()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var packId = Guid.CreateVersion7();

        var created = await (await alice.TryCreatePackAsync(packId, "  Наш\nкот ")).ReadAsync<StickerPackDto>();
        created.Title.ShouldBe("Наш кот");
        created.CreatorId.ShouldBe(alice.Id);
        created.Version.ShouldBe(1);
        StickerRules.IsValidSlug(created.Slug).ShouldBeTrue();
        // A repeat (lost answer) is the same pack.
        (await (await alice.TryCreatePackAsync(packId, "Другое")).ReadAsync<StickerPackDto>()).Title.ShouldBe("Наш кот");
        var aliceState = await alice.StickerStateAsync();
        aliceState.Installed.Select(p => p.Id).ShouldBe([packId]);
        aliceState.Mine.Select(p => p.Id).ShouldBe([packId]);

        var source = await alice.UploadSourceAsync(StickerKit.Png(640, 480));
        var pack = await (await alice.TryAddStickerAsync(packId, source.Id, "😺", "❤️")).ReadAsync<StickerPackDto>();
        var sticker = pack.Stickers.ShouldHaveSingleItem();
        sticker.Emoji.ShouldBe(["😺", "❤️"]);
        sticker.Media.ShouldBeStickerOf(sticker);
        (sticker.Media.Width, sticker.Media.Height).ShouldBe((512, 384));
        sticker.Media.HasThumbnail.ShouldBeTrue();
        pack.Version.ShouldBe(2);
        // The upload it was made from is gone (the sticker has its own WebP).
        (await alice.GetRawAsync(ApiRoutes.Media.ByIdPath(source.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The WebP keeps the transparent corners.
        using (var webp = await alice.Client.GetAsync(ApiRoutes.Media.ContentPath(sticker.Media.Id, null), TestKit.Ct))
        {
            webp.Content.Headers.ContentType?.MediaType.ShouldBe("image/webp");
            using var bitmap = SKBitmap.Decode(await webp.Content.ReadAsByteArrayAsync(TestKit.Ct));
            bitmap.GetPixel(2, 2).Alpha.ShouldBe((byte)0);
            bitmap.GetPixel(256, 192).Alpha.ShouldBe((byte)255);
        }

        // Anyone signed in sees the pack (by id and by its link) and its images, and may install it.
        (await bob.GetAsync<StickerPackDto>(ApiRoutes.Stickers.PackPath(packId))).Stickers.Single().Id.ShouldBe(sticker.Id);
        (await bob.GetAsync<StickerPackDto>(ApiRoutes.Stickers.PackBySlugPath(pack.Slug.ToUpperInvariant()))).Id.ShouldBe(packId);
        await ShouldReadImageAsync(bob, sticker.Media.Id, "image/webp");
        var bobTablet = await bob.SecondDeviceAsync();
        await using var tablet = await bobTablet.ConnectAsync();
        var bobState = await bob.InstallAsync(packId);
        bobState.Installed.ShouldHaveSingleItem().StickerCount.ShouldBe(1);
        bobState.Installed[0].Cover!.Id.ShouldBe(sticker.Id);
        bobState.Mine.ShouldBeEmpty();
        (await tablet.WaitAsync<StickerStateDto>(Ev.StickersChanged, s => s.Revision == bobState.Revision)).Installed.Single().Id.ShouldBe(packId);
        // Installing again changes nothing (no new revision).
        (await bob.InstallAsync(packId)).Revision.ShouldBe(bobState.Revision);

        // Only the creator changes the pack.
        await (await bob.PatchRawAsync(ApiRoutes.Stickers.PackPath(packId), new UpdateStickerPackRequest { Title = "Мой" }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotStickerPackCreator);
        var bobSource = await bob.UploadSourceAsync();
        await (await bob.TryAddStickerAsync(packId, bobSource.Id, "😀")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotStickerPackCreator);
        await (await bob.DeleteRawAsync(ApiRoutes.Stickers.PackStickerPath(packId, sticker.Id)))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotStickerPackCreator);
        await (await bob.PutRawAsync(ApiRoutes.Stickers.PackOrderPath(packId), new ReorderStickersRequest { StickerIds = [sticker.Id] }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotStickerPackCreator);
        await (await bob.DeleteRawAsync(ApiRoutes.Stickers.PackPath(packId))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotStickerPackCreator);

        // The creator's edits reach everyone who installed the pack (a newer revision with the new version).
        var renamed = await (await alice.PatchRawAsync(ApiRoutes.Stickers.PackPath(packId), new UpdateStickerPackRequest { Title = "Коты" }))
            .ReadAsync<StickerPackDto>();
        renamed.Version.ShouldBe(3);
        var pushed = await tablet.WaitAsync<StickerStateDto>(Ev.StickersChanged, s => s.Installed.Any(p => p.Title == "Коты"));
        pushed.Revision.ShouldBeGreaterThan(bobState.Revision);
        pushed.Installed.Single().Version.ShouldBe(3);

        // Emoji edits reach the sticker's media too (previews of sent copies).
        var edited = await (await alice.PatchRawAsync(ApiRoutes.Stickers.PackStickerPath(packId, sticker.Id), new UpdateStickerRequest { Emoji = ["🐱"] }))
            .ReadAsync<StickerPackDto>();
        edited.Stickers.Single().Emoji.ShouldBe(["🐱"]);
        edited.Stickers.Single().Media.Sticker!.Emoji.ShouldBe("🐱");
    }

    [Fact]
    public async Task Titles_emoji_and_images_are_checked()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        await (await alice.TryCreatePackAsync(Guid.CreateVersion7(), " ")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryCreatePackAsync(Guid.CreateVersion7(), new string('к', Limits.StickerPackTitleMaxLength + 1)))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryCreatePackAsync(Guid.Empty, "Пусто")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var pack = await alice.CreatePackAsync();
        // Someone else's pack id is not free for the taking.
        await (await bob.TryCreatePackAsync(pack.Id, "Чужой")).ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.Conflict);

        var source = await alice.UploadSourceAsync();
        await (await alice.TryAddStickerAsync(pack.Id, source.Id)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryAddStickerAsync(pack.Id, source.Id, "abc")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryAddStickerAsync(pack.Id, source.Id, Enumerable.Repeat("😀😃", 1).ToArray()))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Too small, not an image, too large, someone else's upload.
        var tiny = await alice.UploadSourceAsync(StickerKit.Png(40, 120));
        await (await alice.TryAddStickerAsync(pack.Id, tiny.Id, "😀")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerImageInvalid);
        var text = await alice.UploadAsync(MediaKind.File, "not an image at all"u8.ToArray(), "image/png", "fake.png");
        await (await alice.TryAddStickerAsync(pack.Id, text.Id, "😀")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerImageInvalid);
        var huge = new byte[Limits.StickerSourceMaxBytes + 1];
        Random.Shared.NextBytes(huge);
        var large = await alice.UploadAsync(MediaKind.File, huge, "image/png", "huge.png");
        await (await alice.TryAddStickerAsync(pack.Id, large.Id, "😀")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerImageInvalid);
        var bobs = await bob.UploadSourceAsync();
        await (await alice.TryAddStickerAsync(pack.Id, bobs.Id, "😀")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        (await alice.GetAsync<StickerPackDto>(ApiRoutes.Stickers.PackPath(pack.Id))).Stickers.ShouldBeEmpty();

        await (await alice.GetRawAsync(ApiRoutes.Stickers.PackPath(Guid.NewGuid()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerPackNotFound);
        await (await alice.GetRawAsync(ApiRoutes.Stickers.PackBySlugPath("nosuchpack1"))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerPackNotFound);
        await (await alice.PutEmptyAsync(ApiRoutes.Stickers.InstalledPath(Guid.NewGuid()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerPackNotFound);
        await (await alice.PutEmptyAsync(ApiRoutes.Stickers.FavoritePath(Guid.NewGuid()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerNotFound);
    }

    [Fact]
    public async Task A_pack_holds_at_most_120_stickers_and_favorites_keep_the_newest_50()
    {
        var alice = await factory.NewUserAsync();
        var (pack, sticker) = await alice.PackWithStickerAsync();
        await factory.AddStickerRowsAsync(pack.Id, sticker.Media.Id, Limits.MaxStickersPerPack - 1);
        var source = await alice.UploadSourceAsync();
        await (await alice.TryAddStickerAsync(pack.Id, source.Id, "😀")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerPackFull);

        var full = await alice.GetAsync<StickerPackDto>(ApiRoutes.Stickers.PackPath(pack.Id));
        full.Stickers.Count.ShouldBe(Limits.MaxStickersPerPack);
        StickerStateDto state = new();
        foreach (var favorite in full.Stickers.Skip(1).Take(Limits.MaxFavoriteStickers))
            state = await (await alice.PutEmptyAsync(ApiRoutes.Stickers.FavoritePath(favorite.Id))).ReadAsync<StickerStateDto>();
        state.Favorites.Count.ShouldBe(Limits.MaxFavoriteStickers);
        var oldest = state.Favorites[^1].Id;

        state = await (await alice.PutEmptyAsync(ApiRoutes.Stickers.FavoritePath(sticker.Id))).ReadAsync<StickerStateDto>();
        state.Favorites.Count.ShouldBe(Limits.MaxFavoriteStickers);
        state.Favorites[0].Id.ShouldBe(sticker.Id);
        state.Favorites.ShouldNotContain(f => f.Id == oldest);
        state = await (await alice.DeleteRawAsync(ApiRoutes.Stickers.FavoritePath(sticker.Id))).ReadAsync<StickerStateDto>();
        state.Favorites.ShouldNotContain(f => f.Id == sticker.Id);
    }

    [Fact]
    public async Task Installed_packs_keep_my_order_and_are_limited()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var first = await alice.CreatePackAsync("Первый");
        var second = await alice.CreatePackAsync("Второй");
        await bob.InstallAsync(first.Id);
        var state = await bob.InstallAsync(second.Id);
        // A new pack goes first.
        state.Installed.Select(p => p.Id).ShouldBe([second.Id, first.Id]);
        state = await (await bob.PutRawAsync(ApiRoutes.Stickers.InstalledOrder, new ReorderStickerPacksRequest { PackIds = [first.Id, second.Id] }))
            .ReadAsync<StickerStateDto>();
        state.Installed.Select(p => p.Id).ShouldBe([first.Id, second.Id]);
        state = await (await bob.DeleteRawAsync(ApiRoutes.Stickers.InstalledPath(first.Id))).ReadAsync<StickerStateDto>();
        state.Installed.Select(p => p.Id).ShouldBe([second.Id]);

        // At most 200 installed packs.
        await factory.WithDbAsync(async db =>
        {
            var now = TestKit.Now();
            for (var i = 0; i < Limits.MaxInstalledStickerPacks - 1; i++)
            {
                var id = Guid.CreateVersion7();
                db.StickerPacks.Add(new StickerPack { Id = id, CreatorId = alice.Id, Title = $"Набор {i}", Slug = StickerRules.NewSlug(), Version = 1, CreatedAt = now, UpdatedAt = now });
                db.InstalledStickerPacks.Add(new InstalledStickerPack { UserId = bob.Id, PackId = id, Position = 10 + i, InstalledAt = now });
            }
            await db.SaveChangesAsync(TestKit.Ct);
        });
        await (await bob.PutEmptyAsync(ApiRoutes.Stickers.InstalledPath(first.Id))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerPackLimit);
    }

    // ---- Sending stickers ----

    [Fact]
    public async Task Stickers_go_as_photos_with_sticker_attributes_and_keep_showing_after_their_pack_is_deleted()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await alice.OpenPrivateAsync(bob.Id);
        var (pack, sticker) = await alice.PackWithStickerAsync();
        await bob.InstallAsync(pack.Id);
        var phone = await alice.SecondDeviceAsync();
        await using var alicePhone = await phone.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        var hello = await alice.SendAsync(chat.Id, "Привет");
        var sent = await alice.SendStickerAsync(chat.Id, sticker.Id, replyTo: hello.Id);
        sent.Type.ShouldBe(MessageType.Photo);
        sent.Text.ShouldBeNull();
        sent.ReplyTo!.MessageId.ShouldBe(hello.Id);
        sent.Attachments.ShouldHaveSingleItem().ShouldBeStickerOf(sticker);
        (await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == sent.Id)).Attachments.Single().Sticker!.StickerId.ShouldBe(sticker.Id);
        // Recent stickers follow sends, on all my devices.
        (await alicePhone.WaitAsync<StickerStateDto>(Ev.StickersChanged, s => s.Recent.Count > 0)).Recent[0].Id.ShouldBe(sticker.Id);
        (await alice.StickerStateAsync()).Recent.Select(s => s.Id).ShouldBe([sticker.Id]);

        // Nothing goes with a sticker, it is not editable, and only real stickers can be sent.
        await (await alice.TrySendAsync(chat.Id, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), StickerId = sticker.Id, Text = "и текст" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TrySendAsync(chat.Id, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), StickerId = Guid.NewGuid() }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerNotFound);
        await (await alice.TryEditAsync(chat.Id, sent.Id, "подпись")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.MessageNotEditable);

        // Forwarded, it stays a sticker.
        var forwarded = await bob.ForwardAsync(bob.SavedChatId, new ForwardMessagesRequest
        {
            FromChatId = chat.Id,
            MessageIds = [sent.Id],
            ClientMessageIds = [Guid.NewGuid()],
        });
        forwarded.Single().Attachments.Single().Sticker!.StickerId.ShouldBe(sticker.Id);

        // Anyone signed in sees a pack's sticker images; after the pack is deleted only who can see a message with it.
        await ShouldReadImageAsync(carol, sticker.Media.Id, "image/webp");
        await alice.DeleteRawAsync(ApiRoutes.Stickers.PackPath(pack.Id)).NoContentAsync();
        await (await bob.GetRawAsync(ApiRoutes.Stickers.PackPath(pack.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerPackNotFound);
        await (await bob.PutEmptyAsync(ApiRoutes.Stickers.InstalledPath(pack.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerPackNotFound);
        await (await alice.TrySendAsync(chat.Id, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), StickerId = sticker.Id }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.StickerNotFound);
        (await bobRt.WaitAsync<StickerStateDto>(Ev.StickersChanged, s => s.Installed.Count == 0)).Installed.ShouldBeEmpty();
        var aliceAfter = await alice.StickerStateAsync();
        aliceAfter.Installed.ShouldBeEmpty();
        aliceAfter.Mine.ShouldBeEmpty();
        aliceAfter.Recent.ShouldBeEmpty();

        var kept = await bob.GetMessageAsync(chat.Id, sent.Id);
        kept.Attachments.Single().ShouldBeStickerOf(sticker);
        // Positive access answers are cached for 60 s.
        Media.MediaTestKit.ForgetCachedDecisions(factory);
        await ShouldReadImageAsync(bob, sticker.Media.Id, "image/webp");
        await (await carol.GetRawAsync(ApiRoutes.Media.ByIdPath(sticker.Media.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        // Media cleanup keeps it while a message shows it.
        (await factory.WithDbAsync(db => db.Media.AnyAsync(m => m.Id == sticker.Media.Id, TestKit.Ct))).ShouldBeTrue();
    }

    [Fact]
    public async Task Group_media_permission_covers_stickers()
    {
        var owner = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var groupId = await owner.NewGroupAsync(member);
        await owner.SetPermissionsAsync(groupId, new ChatPermissionsDto { SendMessages = true, SendMedia = false });
        var (_, sticker) = await member.PackWithStickerAsync();

        await (await member.TrySendAsync(groupId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), StickerId = sticker.Id }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);
    }

    // ---- GIFs ----

    private async Task<MediaDto> UploadGifAsync(TestUser user)
    {
        Ffmpeg.Require();
        var gif = await Ffmpeg.GenerateAsync(".gif", "-f", "lavfi", "-i", "testsrc=size=200x150:rate=10", "-t", "1.5");
        var media = await user.UploadAsync(MediaKind.Photo, gif, "image/gif", "funny.gif");
        media.Status.ShouldBe(MediaStatus.Ready);
        return media;
    }

    [Fact]
    public async Task A_gif_becomes_a_muted_looping_video_that_others_can_save_and_send_again()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await alice.OpenPrivateAsync(bob.Id);

        var gif = await UploadGifAsync(alice);
        gif.Kind.ShouldBe(MediaKind.Video);
        gif.IsAnimation.ShouldBeTrue();
        gif.ContentType.ShouldBe("video/mp4");
        gif.FileName.ShouldBe("funny.mp4");
        gif.HasThumbnail.ShouldBeTrue();
        gif.DurationMs.ShouldNotBeNull().ShouldBeInRange(1300, 1700);
        (gif.Width!.Value % 2, gif.Height!.Value % 2).ShouldBe((0, 0));
        using (var content = await alice.Client.GetAsync(ApiRoutes.Media.ContentPath(gif.Id, null), TestKit.Ct))
        {
            var probe = await Ffmpeg.ProbeAsync(await content.Content.ReadAsByteArrayAsync(TestKit.Ct));
            probe.Streams.ShouldNotContain(s => s.CodecType == "audio");
            probe.Streams.Single(s => s.CodecType == "video").CodecName.ShouldBe("h264");
        }

        var sent = await alice.SendMediaAsync(chat.Id, gif.Id);
        sent.Type.ShouldBe(MessageType.Video);
        sent.Attachments.Single().IsAnimation.ShouldBeTrue();

        // Bob saves it, then sends it again elsewhere without uploading anything.
        var bobPhone = await bob.SecondDeviceAsync();
        await using var phone = await bobPhone.ConnectAsync();
        var state = await (await bob.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = chat.Id, MessageId = sent.Id }))
            .ReadAsync<StickerStateDto>();
        state.Gifs.ShouldHaveSingleItem().Media.Id.ShouldBe(gif.Id);
        state.Gifs[0].Media.IsAnimation.ShouldBeTrue();
        (await phone.WaitAsync<StickerStateDto>(Ev.StickersChanged, s => s.Revision == state.Revision)).Gifs.Single().Media.Id.ShouldBe(gif.Id);
        var again = await bob.SendAsync(bob.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), GifMediaId = gif.Id });
        again.Type.ShouldBe(MessageType.Video);
        again.Attachments.Single().Id.ShouldBe(gif.Id);

        // Someone who cannot see it cannot send it; a photo is no GIF; saving from a message without one fails.
        await (await carol.TrySendAsync(carol.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), GifMediaId = gif.Id }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        var text = await alice.SendAsync(chat.Id, "без гифки");
        await (await bob.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = chat.Id, MessageId = text.Id }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await carol.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = chat.Id, MessageId = sent.Id }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // Once Alice deletes her message, Bob can only send it from where he still sees it (his Saved Messages).
        await alice.DeleteMessagesAsync(chat.Id, forEveryone: true, sent.Id);
        await bob.DeleteMessagesAsync(bob.SavedChatId, forEveryone: true, again.Id);
        // Positive access answers are cached for 60 s.
        Media.MediaTestKit.ForgetCachedDecisions(factory);
        await (await bob.TrySendAsync(bob.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), GifMediaId = gif.Id }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        // The saved entry stays until Bob removes it.
        state = await (await bob.DeleteRawAsync(ApiRoutes.Stickers.GifPath(gif.Id))).ReadAsync<StickerStateDto>();
        state.Gifs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Content_protection_blocks_saving_gifs_but_not_adding_packs()
    {
        var owner = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var groupId = await owner.NewGroupAsync(member);
        var gif = await UploadGifAsync(owner);
        var sent = await owner.SendMediaAsync(groupId, gif.Id);
        var (pack, sticker) = await owner.PackWithStickerAsync();
        await owner.SendStickerAsync(groupId, sticker.Id);
        await owner.SetContentProtectionAsync(groupId, true);

        await (await member.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = groupId, MessageId = sent.Id }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
        (await member.InstallAsync(pack.Id)).Installed.Single().Id.ShouldBe(pack.Id);
    }

    [Fact]
    public async Task A_gif_with_one_frame_stays_a_photo()
    {
        var alice = await factory.NewUserAsync();
        var still = await alice.UploadAsync(MediaKind.Photo, TestImages.Gif(), "image/gif", "dot.gif");
        still.Kind.ShouldBe(MediaKind.Photo);
        still.IsAnimation.ShouldBeFalse();
        still.ContentType.ShouldBe("image/gif");
    }
}
