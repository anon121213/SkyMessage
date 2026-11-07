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
/// the editor (so leaving it loses nothing), a failure is told and skipped, and signing out drops the rest.
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
    }

    private static PickedFile Picture(string name) => new()
    {
        FileName = name,
        ContentType = "image/png",
        Size = 10,
        OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(new byte[10])),
    };

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
