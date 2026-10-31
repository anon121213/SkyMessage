using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>Message content is untrusted (SPEC §1.4): links that could run script or disguise their target are refused.</summary>
[Collection(ApiCollection.Name)]
public sealed class UntrustedContentTests(ApiFactory factory)
{
    public static readonly TheoryData<string> HostileUrls =
    [
        "javascript:alert(document.cookie)",
        "JavaScript:alert(1)",
        "data:text/html,<script>alert(1)</script>",
        "vbscript:msgbox(1)",
        "file:///etc/passwd",
        "http://bank.example@evil.example/login",
        "//evil.example/no-scheme",
        "/relative/path",
        "https://" + new string('a', Limits.LinkUrlMaxLength),
    ];

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public async Task Text_links_must_be_plain_http_https_mailto_or_tel(string url)
    {
        var alice = await factory.RegisterUserAsync("alice");
        const string text = "click here";
        var problem = await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(alice.SavedChatId), new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = text,
            Entities = [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = text.Length, Url = url }],
        }, HttpStatusCode.BadRequest, ErrorCodes.Validation);
        problem.Root.TryGetProperty("errors", out _).ShouldBeTrue();

        // Editing an existing message into a hostile link is refused the same way.
        var message = await alice.SendTextAsync(alice.SavedChatId, text);
        await alice.Client.ProblemAsync(HttpMethod.Patch, ApiRoutes.Messages.ByIdPath(alice.SavedChatId, message.Id), new EditMessageRequest
        {
            Text = text,
            Entities = [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = text.Length, Url = url }],
        }, HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Safe_links_and_raw_markup_are_stored_verbatim_as_text()
    {
        var alice = await factory.RegisterUserAsync("alice");
        const string markup = "<img src=x onerror=alert(1)> & <b>bold</b>";
        var sent = await alice.Client.PostJsonAsync<MessageDto>(ApiRoutes.Messages.RootPath(alice.SavedChatId), new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = markup,
            Entities = [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = "https://example.org/a?b=c" }],
        });
        // The server neither renders nor "sanitizes" text: clients display it as text.
        sent.Text.ShouldBe(markup);
        sent.Entities!.Single().Url.ShouldBe("https://example.org/a?b=c");
    }
}
