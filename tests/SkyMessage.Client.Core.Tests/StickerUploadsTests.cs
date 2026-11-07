using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Features.Stickers;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// The stickers on their way into a pack (StickerUploads): they upload one after another in the order chosen, outside
/// the editor (so leaving it loses nothing), a failure is told and skipped, a pack that goes takes its pictures along
/// quietly (deleted in the editor, on another device, or found gone by the server), and signing out drops the rest.
/// </summary>
public sealed class StickerUploadsTests
{
    private sealed class Harness
    {
        public IStickerStore Stickers { get; } = Substitute.For<IStickerStore>();
        public IAuthService Auth { get; } = Substitute.For<IAuthService>();
        public ToastService Toasts { get; }
        public StickerUploads Uploads { get; }

        public Harness()
        {
            Auth.State.Returns(AuthState.SignedIn);
            var localizer = new Localizer(new ServiceCollection().BuildServiceProvider());
            Toasts = new ToastService(localizer, NullLogger<ToastService>.Instance);
            var settings = Substitute.For<ISettingsService>();
            settings.Current.Returns(new AppSettings());
            Uploads = new StickerUploads(Stickers, Auth, Toasts, new HapticFeedback(Substitute.For<IHapticsService>(), settings),
                NullLogger<StickerUploads>.Instance);
        }

        public void SignOut()
        {
            Auth.State.Returns(AuthState.SignedOut);
            Auth.Changed += Raise.Event<Action>();
        }

        /// <summary>My state now lists these packs among mine (as a StickersChanged or a load would bring it).</summary>
        public void Mine(params Guid[] packIds)
        {
            Stickers.IsLoaded.Returns(true);
            Stickers.State.Returns(new StickerStateDto
            {
                Mine = packIds.Select(id => new StickerPackRefDto { Id = id, Slug = StickerRules.NewSlug(), Title = "Мой" }).ToList(),
            });
            Stickers.Changed += Raise.Event<Action>();
        }

        /// <summary>Every upload waits until cancelled; the tokens they got, by file name.</summary>
        public ConcurrentDictionary<string, CancellationToken> HoldUploads()
        {
            var tokens = new ConcurrentDictionary<string, CancellationToken>();
            Stickers.AddStickerAsync(Arg.Any<Guid>(), Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var token = call.ArgAt<CancellationToken>(4);
                    tokens[call.ArgAt<PickedFile>(1).FileName] = token;
                    await Task.Delay(Timeout.Infinite, token);
                    return new StickerPackDto { Id = call.ArgAt<Guid>(0) };
                });
            return tokens;
        }
    }

    /// <summary>The server's answer once <paramref name="gate"/> opens: the pack is not there.</summary>
    private static async Task<StickerPackDto> GoneAsync(Task gate)
    {
        await gate;
        throw new ApiException(404, ErrorCodes.StickerPackNotFound, "gone");
    }

    private static PickedFile Picture(string name) => new()
    {
        FileName = name,
        ContentType = "image/png",
        Size = 10,
        OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])),
    };

    [Fact]
    public async Task Deleting_a_pack_drops_its_pictures_and_stops_the_one_on_its_way_without_a_word()
    {
        var h = new Harness();
        var pack = Guid.NewGuid();
        var other = Guid.NewGuid();
        var uploads = h.HoldUploads();
        h.Uploads.Add(pack, Picture("a.png"), ["😀"]);
        h.Uploads.Add(pack, Picture("b.png"), ["😀"]);
        h.Uploads.Add(other, Picture("c.png"), ["😀"]);
        await Wait.UntilAsync(() => uploads.ContainsKey("a.png"), "the first upload started");

        h.Uploads.Cancel(pack);

        h.Uploads.For(pack).ShouldBeEmpty("its pictures leave the queue at once");
        await Wait.UntilAsync(() => uploads["a.png"].IsCancellationRequested, "the upload on its way stops where it is");
        await Wait.UntilAsync(() => uploads.ContainsKey("c.png"), "another pack's picture goes next");
        uploads.ContainsKey("b.png").ShouldBeFalse("a dropped picture never starts");
        h.Uploads.For(other).Count.ShouldBe(1);
        h.Toasts.Items.ShouldBeEmpty("deleting a pack says nothing about its pictures");
        h.Uploads.Dispose();
    }

    [Fact]
    public async Task A_pack_that_leaves_my_packs_takes_its_pictures_along_but_a_new_one_not_listed_yet_keeps_them()
    {
        var h = new Harness();
        var deleted = Guid.NewGuid();
        var created = Guid.NewGuid();
        var uploads = h.HoldUploads();
        h.Mine(deleted);
        h.Uploads.Add(deleted, Picture("a.png"), ["😀"]);
        h.Uploads.Add(deleted, Picture("b.png"), ["😀"]);
        // Made a moment ago: my state does not list it yet.
        h.Uploads.Add(created, Picture("c.png"), ["😀"]);
        await Wait.UntilAsync(() => uploads.ContainsKey("a.png"), "the first upload started");

        // Deleted on another of my devices: a newer state no longer lists it (nor, yet, the new pack).
        h.Mine();

        h.Uploads.For(deleted).ShouldBeEmpty();
        await Wait.UntilAsync(() => uploads["a.png"].IsCancellationRequested, "the upload on its way stops");
        await Wait.UntilAsync(() => uploads.ContainsKey("c.png"), "the new pack's picture goes next");
        uploads.ContainsKey("b.png").ShouldBeFalse();
        h.Uploads.For(created).Count.ShouldBe(1, "a pack my state has not listed yet is not taken for a deleted one");

        // Listed once its state comes, then deleted too.
        h.Mine(created);
        h.Uploads.For(created).Count.ShouldBe(1);
        h.Mine();
        h.Uploads.For(created).ShouldBeEmpty();
        await Wait.UntilAsync(() => uploads["c.png"].IsCancellationRequested, "its upload stops too");
        h.Toasts.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_pack_the_server_no_longer_has_drops_the_rest_of_its_pictures_without_a_toast_each()
    {
        var h = new Harness();
        var gone = Guid.NewGuid();
        var other = Guid.NewGuid();
        var answer = new TaskCompletionSource();
        h.Stickers.AddStickerAsync(gone, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => GoneAsync(answer.Task));
        h.Stickers.AddStickerAsync(other, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new StickerPackDto { Id = other });
        h.Uploads.Add(gone, Picture("a.png"), ["😀"]);
        h.Uploads.Add(gone, Picture("b.png"), ["😀"]);
        h.Uploads.Add(gone, Picture("c.png"), ["😀"]);
        h.Uploads.Add(other, Picture("d.png"), ["😀"]);

        answer.SetResult();

        await Wait.UntilAsync(() => h.Uploads.For(other).Count == 0, "the queue is done");
        h.Uploads.For(gone).ShouldBeEmpty();
        await h.Stickers.Received(1).AddStickerAsync(gone, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(),
            Arg.Any<CancellationToken>());
        await h.Stickers.Received(1).AddStickerAsync(other, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(),
            Arg.Any<CancellationToken>());
        h.Toasts.Items.ShouldBeEmpty("the pack is gone: one quiet stop, not an error for each picture");
    }

    [Fact]
    public async Task Pictures_upload_one_after_another_in_the_order_chosen()
    {
        var h = new Harness();
        var pack = Guid.NewGuid();
        var order = new List<string>();
        var first = new TaskCompletionSource<StickerPackDto>();
        h.Stickers.AddStickerAsync(pack, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var file = call.ArgAt<PickedFile>(1);
                lock (order) order.Add(file.FileName);
                return file.FileName == "a.png" ? first.Task : Task.FromResult(new StickerPackDto { Id = pack });
            });

        h.Uploads.Add(pack, Picture("a.png"), ["😀"]);
        h.Uploads.Add(pack, Picture("b.png"), ["❤️"]);

        await Wait.UntilAsync(() => order.Count == 1, "the first upload started");
        h.Uploads.For(pack).Select(p => p.File.FileName).ShouldBe(["a.png", "b.png"], "both wait in order");
        h.Uploads.For(Guid.NewGuid()).ShouldBeEmpty();
        order.ShouldBe(["a.png"], "the second waits for the first");

        first.SetResult(new StickerPackDto { Id = pack });
        await Wait.UntilAsync(() => h.Uploads.For(pack).Count == 0, "both done");
        order.ShouldBe(["a.png", "b.png"]);
    }

    [Fact]
    public async Task A_failure_is_told_and_the_next_picture_still_goes()
    {
        var h = new Harness();
        var pack = Guid.NewGuid();
        h.Stickers.AddStickerAsync(pack, Arg.Is<PickedFile>(f => f.FileName == "bad.png"), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(400, ErrorCodes.StickerImageInvalid, "no"));
        h.Stickers.AddStickerAsync(pack, Arg.Is<PickedFile>(f => f.FileName == "good.png"), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(new StickerPackDto { Id = pack });

        h.Uploads.Add(pack, Picture("bad.png"), ["😀"]);
        h.Uploads.Add(pack, Picture("good.png"), ["😀"]);

        await Wait.UntilAsync(() => h.Uploads.For(pack).Count == 0, "the queue is done");
        await h.Stickers.Received(1).AddStickerAsync(pack, Arg.Is<PickedFile>(f => f.FileName == "good.png"), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        h.Toasts.Items.ShouldContain(t => t.Kind == ToastKind.Error);
    }

    [Fact]
    public async Task Signing_out_drops_the_rest()
    {
        var h = new Harness();
        var pack = Guid.NewGuid();
        CancellationToken seen = default;
        h.Stickers.AddStickerAsync(pack, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                seen = call.ArgAt<CancellationToken>(4);
                await Task.Delay(Timeout.Infinite, seen);
                return new StickerPackDto { Id = pack };
            });
        h.Uploads.Add(pack, Picture("a.png"), ["😀"]);
        h.Uploads.Add(pack, Picture("b.png"), ["😀"]);
        await Wait.UntilAsync(() => seen.CanBeCanceled, "the first upload started");

        h.SignOut();

        h.Uploads.For(pack).ShouldBeEmpty();
        await Wait.UntilAsync(() => seen.IsCancellationRequested, "the running upload is cancelled");
        await h.Stickers.Received(1).AddStickerAsync(pack, Arg.Any<PickedFile>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IProgress<double>?>(),
            Arg.Any<CancellationToken>());
    }
}
