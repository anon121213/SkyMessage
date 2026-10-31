using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Media_signature_round_trips()
    {
        var key = MediaSignature.NewKey();
        var session = Guid.CreateVersion7();
        var media = Guid.CreateVersion7();

        var token = MediaSignature.Create(MediaSignature.EncodeKey(key), session, media);

        MediaSignature.TryParse(token, out var parsedSession, out var signature).ShouldBeTrue();
        parsedSession.ShouldBe(session);
        MediaSignature.Verify(key, media, signature).ShouldBeTrue();
        MediaSignature.Verify(key, Guid.CreateVersion7(), signature).ShouldBeFalse();
    }

    [Theory]
    [InlineData("skymessage://u/alice", DeepLinkKind.User, "alice")]
    [InlineData("https://example.org/u/@bob", DeepLinkKind.User, "bob")]
    [InlineData("skymessage://join/AbC_123", DeepLinkKind.Join, "AbC_123")]
    public void Deep_links_parse(string url, DeepLinkKind kind, string value)
    {
        DeepLinks.TryParse(new Uri(url), out var target).ShouldBeTrue();
        target!.Kind.ShouldBe(kind);
        target.Value.ShouldBe(value);
    }

    [Fact]
    public void Chat_deep_link_round_trips()
    {
        var id = Guid.CreateVersion7();
        DeepLinks.TryParse(DeepLinks.AppChatUri(id), out var target).ShouldBeTrue();
        target!.Kind.ShouldBe(DeepLinkKind.Chat);
        Guid.Parse(target.Value).ShouldBe(id);
    }
}
