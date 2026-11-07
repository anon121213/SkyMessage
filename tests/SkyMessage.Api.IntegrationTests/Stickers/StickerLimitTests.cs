using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SkyMessage.Api.IntegrationTests.Messaging;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.MediaFiles;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Stickers;

/// <summary>
/// The sticker rules around the edges (SPEC §6.6): the caps on recent stickers, saved GIFs and own packs, what a
/// sticker's image may be made from, that a sticker's media never goes as a plain attachment, who hears about an edit,
/// what media cleanup keeps, and what deleting an account takes along.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StickerLimitTests(ApiFactory factory)
{
    private Task RunMediaCleanupAsync() =>
        factory.Services.GetServices<IHostedService>().OfType<MediaCleanupService>().Single().RunOnceAsync(TestKit.Ct);

    private Task BackdateMediaAsync(params Guid[] mediaIds) =>
        factory.WithDbAsync(db => db.Media.Where(m => mediaIds.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, DateTimeOffset.UtcNow.AddDays(-30)), TestKit.Ct));

    [Fact]
    public async Task Recent_stickers_keep_the_newest_20()
    {
        var alice = await factory.NewUserAsync();
        var (pack, sticker) = await alice.PackWithStickerAsync();
        await factory.AddStickerRowsAsync(pack.Id, sticker.Media.Id, Limits.MaxRecentStickers);
        var stickers = (await alice.GetAsync<StickerPackDto>(ApiRoutes.Stickers.PackPath(pack.Id))).Stickers;

        foreach (var next in stickers.Take(Limits.MaxRecentStickers + 1))
            await alice.SendStickerAsync(alice.SavedChatId, next.Id);

        var recent = (await alice.StickerStateAsync()).Recent;
        recent.Count.ShouldBe(Limits.MaxRecentStickers);
        recent[0].Id.ShouldBe(stickers[Limits.MaxRecentStickers].Id);
        recent.ShouldNotContain(s => s.Id == stickers[0].Id, "the oldest one went");
        (await factory.WithDbAsync(db => db.RecentStickers.CountAsync(r => r.UserId == alice.Id, TestKit.Ct))).ShouldBe(Limits.MaxRecentStickers);

        // Sending the newest one again changes nothing.
        var revision = (await alice.StickerStateAsync()).Revision;
        await alice.SendStickerAsync(alice.SavedChatId, stickers[Limits.MaxRecentStickers].Id);
        (await alice.StickerStateAsync()).Revision.ShouldBe(revision);
    }

    [Fact]
    public async Task Saved_gifs_keep_the_newest_200()
    {
        var bob = await factory.NewUserAsync();
        // 200 GIFs of his own already saved (rows straight in: the cap is what is tested here).
        var old = new List<Guid>();
        for (var i = 0; i < Limits.MaxSavedGifs; i++) old.Add(await factory.CreateMediaAsync(bob, MediaKind.Video));
        await factory.WithDbAsync(async db =>
        {
            await db.Media.Where(m => old.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsAnimation, true), TestKit.Ct);
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            for (var i = 0; i < old.Count; i++)
                db.SavedGifs.Add(new SavedGif { UserId = bob.Id, MediaId = old[i], SavedAt = start.AddSeconds(i) });
            await db.SaveChangesAsync(TestKit.Ct);
        });
        var alice = await factory.NewUserAsync();
        var chat = await alice.OpenPrivateAsync(bob.Id);
        var gif = await alice.UploadAsync(MediaKind.Photo, await StickerKit.AnimatedGifAsync(), "image/gif", "new.gif", convertAnimation: true);
        var sent = await alice.SendMediaAsync(chat.Id, gif.Id);

        var state = await (await bob.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = chat.Id, MessageId = sent.Id }))
            .ReadAsync<StickerStateDto>();

        state.Gifs.Count.ShouldBe(Limits.MaxSavedGifs);
        state.Gifs[0].Media.Id.ShouldBe(gif.Id);
        state.Gifs.ShouldNotContain(g => g.Media.Id == old[0], "the oldest one went");
        state.Gifs.ShouldAllBe(g => !g.Unavailable && g.Media.MiniThumbnail == null);
        (await factory.WithDbAsync(db => db.SavedGifs.CountAsync(g => g.UserId == bob.Id, TestKit.Ct))).ShouldBe(Limits.MaxSavedGifs);
    }

    [Fact]
    public async Task A_user_makes_at_most_100_packs_and_a_full_panel_does_not_take_a_new_one()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        await factory.WithDbAsync(async db =>
        {
            var now = TestKit.Now();
            for (var i = 0; i < Limits.MaxOwnStickerPacks; i++)
                db.StickerPacks.Add(new StickerPack { Id = Guid.CreateVersion7(), CreatorId = alice.Id, Title = $"Набор {i}", Slug = StickerRules.NewSlug(), Version = 1, CreatedAt = now, UpdatedAt = now });
            // Bob's panel is full with Alice's packs.
            await db.SaveChangesAsync(TestKit.Ct);
            var ids = await db.StickerPacks.Where(p => p.CreatorId == alice.Id).Select(p => p.Id).ToListAsync(TestKit.Ct);
            foreach (var id in ids) db.InstalledStickerPacks.Add(new InstalledStickerPack { UserId = bob.Id, PackId = id, Position = 10, InstalledAt = now });
            for (var i = ids.Count; i < Limits.MaxInstalledStickerPacks; i++)
            {
                var id = Guid.CreateVersion7();
                db.StickerPacks.Add(new StickerPack { Id = id, CreatorId = alice.Id, Title = $"Ещё {i}", Slug = StickerRules.NewSlug(), Version = 1, CreatedAt = now, UpdatedAt = now });
                db.InstalledStickerPacks.Add(new InstalledStickerPack { UserId = bob.Id, PackId = id, Position = 10, InstalledAt = now });
            }
            await db.SaveChangesAsync(TestKit.Ct);
        });
        await (await alice.TryCreatePackAsync(Guid.CreateVersion7(), "Сто первый")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerPackLimit);

        // Bob may still make a pack of his own; with his panel full it is simply not installed.
        var made = await bob.CreatePackAsync("Свой");
        var state = await bob.StickerStateAsync();
        state.Mine.Select(p => p.Id).ShouldBe([made.Id]);
        state.Installed.Count.ShouldBe(Limits.MaxInstalledStickerPacks);
        state.Installed.ShouldNotContain(p => p.Id == made.Id);
    }

    [Fact]
    public async Task A_sticker_is_made_only_from_a_file_upload_of_a_sensible_size()
    {
        var alice = await factory.NewUserAsync();
        var pack = await alice.CreatePackAsync();

        // A photo upload is no sticker source (the apps upload the original bytes as a file).
        var photo = await alice.UploadAsync(MediaKind.Photo, StickerKit.Png(), "image/png", "photo.png");
        await (await alice.TryAddStickerAsync(pack.Id, photo.Id, "😀")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // A few kilobytes of PNG can describe a canvas that takes hundreds of megabytes to decode: refused before that.
        var wide = await alice.UploadSourceAsync(StickerKit.Png(Limits.StickerSourceMaxSide + 1, 100));
        await (await alice.TryAddStickerAsync(pack.Id, wide.Id, "😀")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerImageInvalid);
        var tall = await alice.UploadSourceAsync(StickerKit.Png(100, Limits.StickerSourceMaxSide + 1));
        await (await alice.TryAddStickerAsync(pack.Id, tall.Id, "😀")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.StickerImageInvalid);

        // At the limit it works (and comes out at most 512 px).
        var edge = await alice.UploadSourceAsync(StickerKit.Png(Limits.StickerSourceMaxSide, 256));
        var sticker = (await (await alice.TryAddStickerAsync(pack.Id, edge.Id, "😀")).ReadAsync<StickerPackDto>()).Stickers.Single();
        (sticker.Media.Width, sticker.Media.Height).ShouldBe((Limits.StickerMaxSide, 32));
    }

    [Fact]
    public async Task An_upload_still_in_use_is_kept_after_a_sticker_is_made_from_it()
    {
        var alice = await factory.NewUserAsync();
        var pack = await alice.CreatePackAsync();
        var source = await alice.UploadSourceAsync();
        // Sent as a file first: the message still needs it.
        await alice.SendMediaAsync(alice.SavedChatId, source.Id);

        await (await alice.TryAddStickerAsync(pack.Id, source.Id, "😀")).ReadAsync<StickerPackDto>();

        (await alice.GetAsync<MediaDto>(ApiRoutes.Media.ByIdPath(source.Id))).Id.ShouldBe(source.Id);
    }

    [Fact]
    public async Task A_stickers_media_goes_only_as_that_sticker()
    {
        var alice = await factory.NewUserAsync();
        var (_, sticker) = await alice.PackWithStickerAsync();
        var photo = await factory.CreateMediaAsync(alice, MediaKind.Photo);

        // Its creator owns the WebP, but as a plain attachment it would be a sticker with a hidden caption, or in an album.
        await (await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [sticker.Media.Id], Text = "подпись" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [photo, sticker.Media.Id] }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TrySendAsync(alice.SavedChatId, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [sticker.Media.Id] }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        (await alice.SendStickerAsync(alice.SavedChatId, sticker.Id)).Attachments.Single().ShouldBeStickerOf(sticker);
    }

    [Fact]
    public async Task An_emoji_edit_reaches_who_keeps_the_sticker_without_its_pack()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var (pack, sticker) = await alice.PackWithStickerAsync();
        // Bob favorites it from a chat without adding the pack.
        var favorite = await (await bob.PutEmptyAsync(ApiRoutes.Stickers.FavoritePath(sticker.Id))).ReadAsync<StickerStateDto>();
        favorite.Installed.ShouldBeEmpty();
        await using var bobRt = await bob.ConnectAsync();

        await (await alice.PatchRawAsync(ApiRoutes.Stickers.PackStickerPath(pack.Id, sticker.Id), new UpdateStickerRequest { Emoji = ["🐱"] }))
            .ReadAsync<StickerPackDto>();

        var pushed = await bobRt.WaitAsync<StickerStateDto>(Ev.StickersChanged, s => s.Revision > favorite.Revision);
        pushed.Favorites.Single().Emoji.ShouldBe(["🐱"]);
        (await bob.StickerStateAsync()).Favorites.Single().Media.Sticker!.Emoji.ShouldBe("🐱");
    }

    [Fact]
    public async Task Media_cleanup_keeps_stickers_and_my_own_saved_gifs_but_not_a_gif_deleted_for_everyone()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await alice.OpenPrivateAsync(bob.Id);
        var (pack, sticker) = await alice.PackWithStickerAsync();
        var gif = await alice.UploadAsync(MediaKind.Photo, await StickerKit.AnimatedGifAsync(), "image/gif", "loop.gif", convertAnimation: true);
        var sent = await alice.SendMediaAsync(chat.Id, gif.Id);
        foreach (var saver in new[] { alice, bob })
            await (await saver.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = chat.Id, MessageId = sent.Id })).ShouldSucceedAsync();
        var keptGif = await alice.UploadAsync(MediaKind.Photo, await StickerKit.AnimatedGifAsync(), "image/gif", "mine.gif", convertAnimation: true);
        await (await alice.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest
        {
            ChatId = alice.SavedChatId,
            MessageId = (await alice.SendMediaAsync(alice.SavedChatId, keptGif.Id)).Id,
        })).ShouldSucceedAsync();
        await BackdateMediaAsync(sticker.Media.Id, gif.Id, keptGif.Id);

        // Shown nowhere any more, but a sticker of a pack and in its owner's own saved GIFs.
        await alice.DeleteMessagesAsync(chat.Id, forEveryone: true, sent.Id);
        await alice.DeleteMessagesAsync(alice.SavedChatId, forEveryone: true,
            (await alice.PageAsync(alice.SavedChatId)).Messages.Where(m => m.Attachments.Count > 0).Select(m => m.Id).ToArray());
        await RunMediaCleanupAsync();
        var left = await factory.WithDbAsync(db => db.Media.Where(m => m.Id == sticker.Media.Id || m.Id == gif.Id || m.Id == keptGif.Id)
            .Select(m => m.Id).ToListAsync(TestKit.Ct));
        left.ShouldBe([sticker.Media.Id, gif.Id, keptGif.Id], ignoreOrder: true);
        // Bob can no longer see the GIF Alice deleted: his state says so, and he cannot send it.
        (await bob.StickerStateAsync()).Gifs.Single().Unavailable.ShouldBeTrue();
        (await alice.StickerStateAsync()).Gifs.ShouldAllBe(g => !g.Unavailable);

        // Once Alice takes it out of her saved GIFs too, nothing keeps it: it leaves the server, Bob's entry with it.
        await (await alice.DeleteRawAsync(ApiRoutes.Stickers.GifPath(gif.Id))).ShouldSucceedAsync();
        await RunMediaCleanupAsync();
        (await factory.WithDbAsync(db => db.Media.AnyAsync(m => m.Id == gif.Id, TestKit.Ct))).ShouldBeFalse();
        (await bob.StickerStateAsync()).Gifs.ShouldBeEmpty();
        // A deleted pack's sticker media stays only while a message shows it (here none: cleanup takes it).
        await alice.DeleteRawAsync(ApiRoutes.Stickers.PackPath(pack.Id)).NoContentAsync();
        await RunMediaCleanupAsync();
        (await factory.WithDbAsync(db => db.Media.AnyAsync(m => m.Id == sticker.Media.Id, TestKit.Ct))).ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_an_account_takes_its_sticker_rows_and_leaves_its_packs()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var chat = await alice.OpenPrivateAsync(bob.Id);
        var (pack, sticker) = await alice.PackWithStickerAsync();
        await bob.InstallAsync(pack.Id);
        await (await alice.PutEmptyAsync(ApiRoutes.Stickers.FavoritePath(sticker.Id))).ShouldSucceedAsync();
        await alice.SendStickerAsync(chat.Id, sticker.Id);
        var gif = await alice.UploadAsync(MediaKind.Photo, await StickerKit.AnimatedGifAsync(), "image/gif", "loop.gif", convertAnimation: true);
        var sent = await alice.SendMediaAsync(chat.Id, gif.Id);
        await (await alice.PostRawAsync(ApiRoutes.Stickers.Gifs, new SaveGifRequest { ChatId = chat.Id, MessageId = sent.Id })).ShouldSucceedAsync();

        await (await alice.PostRawAsync(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = TestKit.Password })).ShouldSucceedAsync();

        var rows = await factory.WithDbAsync(async db => new
        {
            Installed = await db.InstalledStickerPacks.CountAsync(i => i.UserId == alice.Id, TestKit.Ct),
            Favorites = await db.FavoriteStickers.CountAsync(f => f.UserId == alice.Id, TestKit.Ct),
            Recent = await db.RecentStickers.CountAsync(r => r.UserId == alice.Id, TestKit.Ct),
            Gifs = await db.SavedGifs.CountAsync(g => g.UserId == alice.Id, TestKit.Ct),
            Preferences = await db.StickerPreferences.CountAsync(p => p.UserId == alice.Id, TestKit.Ct),
        });
        rows.ShouldBe(new { Installed = 0, Favorites = 0, Recent = 0, Gifs = 0, Preferences = 0 });
        // The pack she made stays for whoever added it.
        (await bob.GetAsync<StickerPackDto>(ApiRoutes.Stickers.PackPath(pack.Id))).Stickers.Single().Id.ShouldBe(sticker.Id);
        (await bob.StickerStateAsync()).Installed.Single().Id.ShouldBe(pack.Id);
    }
}
