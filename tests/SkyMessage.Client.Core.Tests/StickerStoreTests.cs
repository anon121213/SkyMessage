using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class StickerStoreTests
{
    private sealed class Harness
    {
        public Guid Me { get; } = Guid.CreateVersion7();
        public ISkyApi Api { get; } = Substitute.For<ISkyApi>();
        public IRealtimeService Realtime { get; } = Substitute.For<IRealtimeService>();
        public IMediaUploader Uploader { get; } = Substitute.For<IMediaUploader>();
        public InMemoryCacheStore CacheStore { get; }
        public AccountLifecycle Lifecycle { get; }
        public StickerStore Store { get; }

        public Harness(InMemoryCacheStore? cacheStore = null)
        {
            CacheStore = cacheStore ?? new InMemoryCacheStore();
            var (cache, lifecycle) = Fakes.Account(CacheStore);
            Lifecycle = lifecycle;
            Store = new StickerStore(Api, Realtime, Uploader, cache, lifecycle, TimeProvider.System, Log.For<StickerStore>());
        }

        public async Task LoadAsync(StickerStateDto state)
        {
            Api.GetStickerStateAsync(Arg.Any<CancellationToken>()).Returns(state);
            await Lifecycle.ActivateAsync(Me);
            await Store.LoadAsync(force: true);
        }

        public void Raise(StickerStateDto state) => Realtime.StickersChanged += NSubstitute.Raise.Event<Action<StickerStateDto>>(state);

        public void Resync() => Realtime.Resynced += NSubstitute.Raise.Event<Action>();
    }

    private static StickerDto Sticker(Guid packId, params string[] emoji) => new()
    {
        Id = Guid.CreateVersion7(),
        PackId = packId,
        Emoji = emoji,
        Media = new MediaDto { Id = Guid.CreateVersion7(), Kind = MediaKind.Photo, Status = MediaStatus.Ready, ContentType = "image/webp" },
    };

    private static StickerPackDto Pack(string title, long version, params string[][] stickers)
    {
        var id = Guid.CreateVersion7();
        return new StickerPackDto
        {
            Id = id,
            Slug = StickerRules.NewSlug(),
            Title = title,
            Version = version,
            Stickers = stickers.Select(e => Sticker(id, e)).ToList(),
        };
    }

    private static StickerStateDto State(long revision, params StickerPackDto[] installed) => new()
    {
        Revision = revision,
        Installed = installed.Select(StickerEdits.RefOf).ToList(),
    };

    [Fact]
    public async Task Newer_states_replace_older_ones_and_installed_packs_are_fetched_by_version()
    {
        var h = new Harness();
        var cats = Pack("Котики", 1, ["😺"], ["❤️", "😻"]);
        h.Api.GetStickerPackAsync(cats.Id, Arg.Any<CancellationToken>()).Returns(cats);
        await h.LoadAsync(State(3, cats));

        await Wait.UntilAsync(() => h.Store.GetPack(cats.Id) is not null, "the installed pack's stickers arrive");
        h.Store.State.Installed.Single().Title.ShouldBe("Котики");
        h.Store.IsInstalled(cats.Id).ShouldBeTrue();

        h.Raise(State(2));
        h.Store.State.Installed.ShouldHaveSingleItem("an older state is ignored");

        // The creator edited the pack: a newer state names a newer version, and the store fetches it again.
        var edited = cats with { Version = 2, Title = "Коты", Stickers = [.. cats.Stickers, Sticker(cats.Id, "🐱")] };
        h.Api.GetStickerPackAsync(cats.Id, Arg.Any<CancellationToken>()).Returns(edited);
        h.Raise(State(4, edited));
        await Wait.UntilAsync(() => h.Store.GetPack(cats.Id)?.Version == 2, "the newer version is fetched");
        h.Store.GetPack(cats.Id)!.Stickers.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_reload_at_the_same_revision_brings_which_saved_gifs_i_can_no_longer_see()
    {
        var h = new Harness();
        var gif = new MediaDto { Id = Guid.CreateVersion7(), Kind = MediaKind.Video, Status = MediaStatus.Ready, IsAnimation = true };
        var state = new StickerStateDto { Revision = 5, Gifs = [new SavedGifDto { Media = gif, SavedAt = DateTimeOffset.UtcNow }] };
        await h.LoadAsync(state);
        h.Store.State.Gifs.Single().Unavailable.ShouldBeFalse();

        // Someone deleted its message for everyone: my revision did not move, the server's verdict did.
        var gone = state with { Gifs = [state.Gifs[0] with { Unavailable = true }] };
        var changes = 0;
        h.Store.Changed += () => changes++;
        h.Raise(gone);
        h.Store.State.Gifs.Single().Unavailable.ShouldBeFalse("an event at the same revision is the same change");
        h.Api.GetStickerStateAsync(Arg.Any<CancellationToken>()).Returns(gone);
        await h.Store.LoadAsync(force: true);
        h.Store.State.Gifs.Single().Unavailable.ShouldBeTrue();
        changes.ShouldBe(1);

        // The same answer again changes nothing.
        await h.Store.LoadAsync(force: true);
        changes.ShouldBe(1);
        // And media cleanup took it later: gone from the list at the same revision too.
        h.Api.GetStickerStateAsync(Arg.Any<CancellationToken>()).Returns(state with { Gifs = [] });
        await h.Store.LoadAsync(force: true);
        h.Store.State.Gifs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Suggestions_come_from_installed_packs_in_my_order_whatever_the_emoji_variant()
    {
        var h = new Harness();
        var first = Pack("Первый", 1, ["😀"], ["❤"]);
        var second = Pack("Второй", 1, ["❤️", "🔥"], ["👍🏽"]);
        h.Api.GetStickerPackAsync(first.Id, Arg.Any<CancellationToken>()).Returns(first);
        h.Api.GetStickerPackAsync(second.Id, Arg.Any<CancellationToken>()).Returns(second);
        await h.LoadAsync(State(1, second, first));
        await Wait.UntilAsync(() => h.Store.GetPack(first.Id) is not null && h.Store.GetPack(second.Id) is not null, "packs arrive");

        h.Store.Suggest("❤️").Select(s => s.Id).ShouldBe([second.Stickers[0].Id, first.Stickers[1].Id]);
        h.Store.Suggest("👍").Select(s => s.Id).ShouldBe([second.Stickers[1].Id]);
        h.Store.Suggest("🐸").ShouldBeEmpty();
        h.Store.Suggest("❤", max: 1).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Installing_shows_at_once_and_a_refused_change_is_put_back()
    {
        var h = new Harness();
        await h.LoadAsync(State(1));
        var pack = Pack("Котики", 1, ["😺"]);
        var answer = new TaskCompletionSource<StickerStateDto>();
        h.Api.InstallStickerPackAsync(pack.Id, Arg.Any<CancellationToken>()).Returns(answer.Task);

        var installing = h.Store.InstallAsync(pack);
        h.Store.State.Installed.ShouldHaveSingleItem().Id.ShouldBe(pack.Id);
        h.Store.GetPack(pack.Id).ShouldNotBeNull("the previewed pack is kept for the panel");
        answer.SetResult(State(2, pack));
        await installing;
        h.Store.State.Revision.ShouldBe(2);

        h.Api.UninstallStickerPackAsync(pack.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network, "offline"));
        await Should.ThrowAsync<ApiException>(() => h.Store.UninstallAsync(pack.Id));
        h.Store.IsInstalled(pack.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Favorites_and_saved_gifs_go_first_and_stay_on_top_of_a_newer_state_while_pending()
    {
        var h = new Harness();
        var pack = Pack("Котики", 1, ["😺"], ["😻"]);
        await h.LoadAsync(State(1, pack) with { Favorites = [pack.Stickers[0]] });
        var favorite = new TaskCompletionSource<StickerStateDto>();
        h.Api.SetFavoriteStickerAsync(pack.Stickers[1].Id, true, Arg.Any<CancellationToken>()).Returns(favorite.Task);

        var adding = h.Store.SetFavoriteAsync(pack.Stickers[1], true);
        h.Store.State.Favorites.Select(s => s.Id).ShouldBe([pack.Stickers[1].Id, pack.Stickers[0].Id]);
        h.Store.IsFavorite(pack.Stickers[1].Id).ShouldBeTrue();
        // Another device saved a GIF meanwhile: the favorite still shows on top of that newer state.
        var gif = new MediaDto { Id = Guid.CreateVersion7(), Kind = MediaKind.Video, IsAnimation = true, Status = MediaStatus.Ready };
        h.Raise(State(2, pack) with { Favorites = [pack.Stickers[0]], Gifs = [new SavedGifDto { Media = gif }] });
        h.Store.State.Favorites.Count.ShouldBe(2);
        h.Store.IsSavedGif(gif.Id).ShouldBeTrue();

        favorite.SetResult(State(3, pack) with { Favorites = [pack.Stickers[1], pack.Stickers[0]], Gifs = [new SavedGifDto { Media = gif }] });
        await adding;
        h.Store.State.Revision.ShouldBe(3);

        var removal = new TaskCompletionSource<StickerStateDto>();
        h.Api.RemoveGifAsync(gif.Id, Arg.Any<CancellationToken>()).Returns(removal.Task);
        var removing = h.Store.RemoveGifAsync(gif.Id);
        h.Store.IsSavedGif(gif.Id).ShouldBeFalse();
        removal.SetResult(State(4, pack) with { Favorites = [pack.Stickers[1], pack.Stickers[0]] });
        await removing;
    }

    [Fact]
    public async Task The_state_and_installed_packs_survive_a_restart_and_leave_with_the_account()
    {
        var h = new Harness();
        var pack = Pack("Котики", 1, ["😺"]);
        h.Api.GetStickerPackAsync(pack.Id, Arg.Any<CancellationToken>()).Returns(pack);
        await h.LoadAsync(State(5, pack));
        await Wait.UntilAsync(() => h.Store.GetPack(pack.Id) is not null, "pack fetched");
        await Wait.UntilAsync(() => h.CacheStore.Entries.ContainsKey(LocalCache.AccountKey(h.Me, "sticker-packs")), "persisted");
        await Task.Delay(1200);

        var restarted = new Harness(h.CacheStore.Clone());
        await restarted.Lifecycle.ActivateAsync(h.Me);
        await Wait.UntilAsync(() => restarted.Store.IsLoaded, "restored from the cache");
        restarted.Store.State.Revision.ShouldBe(5);
        restarted.Store.GetPack(pack.Id)!.Stickers.ShouldHaveSingleItem();
        restarted.Store.Suggest("😺").ShouldHaveSingleItem("suggestions work before the network");

        await restarted.Lifecycle.SignedOutAsync(h.Me);
        restarted.Store.IsLoaded.ShouldBeFalse();
        restarted.Store.GetPack(pack.Id).ShouldBeNull();
    }

    [Fact]
    public async Task A_deleted_pack_leaves_the_lists_and_the_cache_of_packs()
    {
        var h = new Harness();
        var pack = Pack("Котики", 1, ["😺"]);
        h.Api.GetStickerPackAsync(pack.Id, Arg.Any<CancellationToken>()).Returns(pack);
        await h.LoadAsync(State(1, pack) with { Mine = [StickerEdits.RefOf(pack)], Recent = [pack.Stickers[0]] });
        await Wait.UntilAsync(() => h.Store.GetPack(pack.Id) is not null, "pack fetched");

        await h.Store.DeletePackAsync(pack.Id);

        h.Store.State.Installed.ShouldBeEmpty();
        h.Store.State.Mine.ShouldBeEmpty();
        h.Store.State.Recent.ShouldBeEmpty();
        h.Store.GetPack(pack.Id).ShouldBeNull();
        await h.Api.Received(1).DeleteStickerPackAsync(pack.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_edit_that_finds_the_pack_deleted_elsewhere_takes_it_out_of_my_lists_at_once()
    {
        var h = new Harness();
        var pack = Pack("Котики", 1, ["😺"]);
        h.Api.GetStickerPackAsync(pack.Id, Arg.Any<CancellationToken>()).Returns(pack);
        await h.LoadAsync(State(1, pack) with { Mine = [StickerEdits.RefOf(pack)], Favorites = [pack.Stickers[0]] });
        await Wait.UntilAsync(() => h.Store.GetPack(pack.Id) is not null, "pack fetched");
        // Deleted on another of my devices; its StickersChanged has not come.
        h.Api.AddStickerAsync(pack.Id, Arg.Any<AddStickerRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(404, ErrorCodes.StickerPackNotFound, "gone"));
        h.Uploader.UploadAsync(Arg.Any<MediaUploadRequest>(), Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(new MediaDto { Id = Guid.CreateVersion7(), Kind = MediaKind.File, Status = MediaStatus.Ready });
        var picture = new PickedFile
        {
            FileName = "a.png",
            ContentType = "image/png",
            Size = 10,
            OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])),
        };

        var refused = await Should.ThrowAsync<ApiException>(() => h.Store.AddStickerAsync(pack.Id, picture, ["😺"]));

        refused.Code.ShouldBe(ErrorCodes.StickerPackNotFound);
        h.Store.State.Installed.ShouldBeEmpty();
        h.Store.State.Mine.ShouldBeEmpty();
        h.Store.State.Favorites.ShouldBeEmpty();
        h.Store.GetPack(pack.Id).ShouldBeNull();
        await Wait.UntilAsync(() => h.Api.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISkyApi.GetStickerStateAsync)) >= 2,
            "my state is asked for again");
    }

    [Fact]
    public async Task A_picture_of_unknown_size_is_measured_before_upload_and_one_over_5_mb_is_refused()
    {
        var h = new Harness();
        var pack = Pack("Котики", 1);
        await h.LoadAsync(State(1, pack));
        MediaUploadRequest? upload = null;
        h.Uploader.UploadAsync(Arg.Do<MediaUploadRequest>(r => upload = r), Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(new MediaDto { Id = Guid.CreateVersion7(), Kind = MediaKind.File, Status = MediaStatus.Ready });
        h.Api.AddStickerAsync(pack.Id, Arg.Any<AddStickerRequest>(), Arg.Any<CancellationToken>()).Returns(pack);
        static PickedFile Picture(int length) => new()
        {
            FileName = "", ContentType = "",
            OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(new byte[length])),
        };

        await h.Store.AddStickerAsync(pack.Id, Picture(70_000), ["😺"]);

        upload.ShouldNotBeNull();
        upload.Size.ShouldBe(70_000);
        upload.FileName.ShouldBe("sticker.png");
        await using (var stream = await upload.OpenReadAsync(CancellationToken.None))
            stream.Length.ShouldBe(70_000);

        upload = null;
        var refused = await Should.ThrowAsync<ApiException>(() => h.Store.AddStickerAsync(pack.Id, Picture((int)Limits.StickerSourceMaxBytes + 10), ["😺"]));
        refused.Code.ShouldBe(ErrorCodes.StickerImageInvalid);
        upload.ShouldBeNull();
    }

    [Fact]
    public async Task A_sticker_goes_out_as_a_sticker_without_uploading_and_shows_itself_while_pending()
    {
        var h = new MessageStoreHarness();
        h.ReturnsPage(Fakes.Page([Fakes.Message(h.ChatId, 1, Guid.CreateVersion7())]));
        var response = new TaskCompletionSource<MessageDto>();
        SendMessageRequest? sent = null;
        h.Api.SendMessageAsync(h.ChatId, Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(response.Task);
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);
        var sticker = Sticker(Guid.CreateVersion7(), "😺");

        await h.Store.SendStickerAsync(h.ChatId, sticker, replyToMessageId: h.State.Items[0].Message.Id);

        var pending = h.State.Items[^1];
        pending.IsPending.ShouldBeTrue();
        pending.Message.Type.ShouldBe(MessageType.Photo);
        pending.Message.Attachments.ShouldHaveSingleItem().Id.ShouldBe(sticker.Media.Id);
        await Wait.UntilAsync(() => sent is not null, "posted");
        sent!.StickerId.ShouldBe(sticker.Id);
        sent.MediaIds.ShouldBeEmpty();
        sent.Text.ShouldBeNull();
        sent.ReplyToMessageId.ShouldBe(h.State.Items[0].Message.Id);
        await h.Api.DidNotReceiveWithAnyArgs().CreateUploadAsync(default!, default);

        response.SetResult(Fakes.Message(h.ChatId, 2, h.Me, clientMessageId: pending.Key) with { Type = MessageType.Photo, Attachments = [sticker.Media] });
        await Wait.UntilAsync(() => h.State.Items.All(i => !i.IsPending), "confirmed");
    }

    [Fact]
    public async Task A_saved_gif_is_sent_again_by_its_media_id_and_survives_a_restart_while_offline()
    {
        var h = new MessageStoreHarness();
        h.ReturnsPage(Fakes.Page([Fakes.Message(h.ChatId, 1, Guid.CreateVersion7())]));
        h.Api.SendMessageAsync(h.ChatId, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(0, ClientErrorCodes.Network, "offline"));
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);
        var gif = new MediaDto { Id = Guid.CreateVersion7(), Kind = MediaKind.Video, IsAnimation = true, Status = MediaStatus.Ready, ContentType = "video/mp4" };

        await h.Store.SendGifAsync(h.ChatId, gif);
        await Wait.UntilAsync(() => h.State.Items[^1].SendState == MessageSendState.Queued, "queued while offline");

        var restarted = h.Restart();
        SendMessageRequest? sent = null;
        // The server answers only once the restored bubble has been checked (otherwise the answer can replace it first).
        var response = new TaskCompletionSource<MessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.ReturnsPage(Fakes.Page([Fakes.Message(h.ChatId, 1, Guid.CreateVersion7())]));
        restarted.Api.SendMessageAsync(h.ChatId, Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(response.Task);
        await restarted.SignInAsync();
        await restarted.Store.OpenAsync(h.ChatId);
        var restored = restarted.State.Items[^1];
        restored.IsPending.ShouldBeTrue();
        restored.Message.Attachments.ShouldHaveSingleItem().IsAnimation.ShouldBeTrue();
        await Wait.UntilAsync(() => sent is not null, "resent after the restart", 20000);
        sent!.GifMediaId.ShouldBe(gif.Id);
        sent.StickerId.ShouldBeNull();

        response.SetResult(Fakes.Message(h.ChatId, 2, h.Me, clientMessageId: sent.ClientMessageId) with { Type = MessageType.Video, Attachments = [gif] });
        await Wait.UntilAsync(() => restarted.State.Items.All(i => !i.IsPending), "confirmed");
        restarted.State.Items[^1].Message.Attachments.ShouldHaveSingleItem().Id.ShouldBe(gif.Id);
    }
}
