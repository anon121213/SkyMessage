using Shouldly;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class MessageContentRulesTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1#frag")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("https://пример.рф/страница")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+79991234567")]
    public void Allowed_links(string url) => MessageContentRules.IsAllowedLinkUrl(url).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://example.com/\nnext")]
    [InlineData("https://google.com@evil.example/")]
    [InlineData("mailto:")]
    [InlineData("tel:")]
    [InlineData("http://")]
    public void Rejected_links(string? url) => MessageContentRules.IsAllowedLinkUrl(url).ShouldBeFalse();

    [Fact]
    public void Link_length_is_capped()
    {
        var prefix = "https://example.com/";
        MessageContentRules.IsAllowedLinkUrl(prefix + new string('a', Limits.LinkUrlMaxLength - prefix.Length)).ShouldBeTrue();
        MessageContentRules.IsAllowedLinkUrl(prefix + new string('a', Limits.LinkUrlMaxLength - prefix.Length + 1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   \n\t ", true)]
    [InlineData("​‍﻿", true)]
    [InlineData(" a ", false)]
    [InlineData("👍", false)]
    [InlineData("‍👍", false)]
    public void Blank_detection(string? text, bool expected) => MessageContentRules.IsBlank(text).ShouldBe(expected);

    [Theory]
    [InlineData("plain", false)]
    [InlineData("emoji 👨‍👩‍👧", false)]
    [InlineData("nul\0", true)]
    public void Forbidden_characters(string value, bool expected) => MessageContentRules.HasForbiddenChars(value).ShouldBe(expected);

    [Fact]
    public void Unpaired_surrogates_are_forbidden()
    {
        // Built at runtime: attribute arguments cannot carry unpaired surrogates (they become U+FFFD).
        const char high = '\uD83D';
        const char low = '\uDC4D';
        MessageContentRules.HasForbiddenChars("lone " + high + " high").ShouldBeTrue();
        MessageContentRules.HasForbiddenChars("lone " + low + " low").ShouldBeTrue();
        MessageContentRules.HasForbiddenChars("end " + high).ShouldBeTrue();
        MessageContentRules.HasForbiddenChars("pair " + high + low).ShouldBeFalse();
    }

    [Fact]
    public void Text_errors_cover_length_and_characters()
    {
        MessageContentRules.TextErrors(null).ShouldBeEmpty();
        MessageContentRules.TextErrors(new string('x', Limits.MessageTextMaxLength)).ShouldBeEmpty();
        MessageContentRules.TextErrors(new string('x', Limits.MessageTextMaxLength + 1)).Count().ShouldBe(1);
        MessageContentRules.TextErrors("a\0b").Count().ShouldBe(1);
    }

    [Fact]
    public void Entities_inside_the_text_are_valid()
    {
        var entities = new[]
        {
            new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 5 },
            new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 6, Length = 5, Url = "https://example.com" },
            new MessageEntityDto { Type = MessageEntityType.Pre, Offset = 0, Length = 11, Language = "c#" },
            new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 0, Length = 5, UserId = Guid.NewGuid() },
        };
        MessageContentRules.EntityErrors("hello world", entities).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void Entities_outside_the_text_are_rejected(int offset, int length) =>
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = offset, Length = length }])
            .ShouldNotBeEmpty();

    [Fact]
    public void Entities_without_text_are_rejected() =>
        MessageContentRules.EntityErrors(null, [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 1 }])
            .ShouldNotBeEmpty();

    [Fact]
    public void Entity_rules_per_type()
    {
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = (MessageEntityType)77, Offset = 0, Length = 1 }]).ShouldNotBeEmpty();
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 5 }]).ShouldNotBeEmpty();
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = "javascript:x" }]).ShouldNotBeEmpty();
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 0, Length = 5, UserId = Guid.Empty }]).ShouldNotBeEmpty();
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = MessageEntityType.Code, Offset = 0, Length = 5, Language = "<script>" }]).ShouldNotBeEmpty();
        // A Url entity marks an auto-detected link in the text itself: no Url property needed.
        MessageContentRules.EntityErrors("hello", [new MessageEntityDto { Type = MessageEntityType.Url, Offset = 0, Length = 5 }]).ShouldBeEmpty();
    }

    [Fact]
    public void Entity_count_is_capped()
    {
        var text = new string('x', 500);
        var entities = Enumerable.Range(0, Limits.MaxEntitiesPerMessage + 1)
            .Select(i => new MessageEntityDto { Type = MessageEntityType.Bold, Offset = i, Length = 1 })
            .ToList();
        MessageContentRules.EntityErrors(text, entities).ShouldNotBeEmpty();
        MessageContentRules.EntityErrors(text, entities.Take(Limits.MaxEntitiesPerMessage).ToList()).ShouldBeEmpty();
    }

    [Fact]
    public void Stored_entities_keep_only_meaningful_fields()
    {
        var userId = Guid.NewGuid();
        var stored = MessageContentRules.ToEntities(
        [
            new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 2, Url = "https://x.example", UserId = userId, Language = "js" },
            new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 2, Url = "https://x.example", UserId = userId },
            new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 0, Length = 2, Url = "https://x.example", UserId = userId },
            new MessageEntityDto { Type = MessageEntityType.Pre, Offset = 0, Length = 2, Language = "js" },
        ]);

        stored[0].Url.ShouldBeNull();
        stored[0].UserId.ShouldBeNull();
        stored[0].Language.ShouldBeNull();
        stored[1].Url.ShouldBe("https://x.example");
        stored[1].UserId.ShouldBeNull();
        stored[2].UserId.ShouldBe(userId);
        stored[2].Url.ShouldBeNull();
        stored[3].Language.ShouldBe("js");
    }

    [Fact]
    public void Entity_comparison_detects_changes()
    {
        var a = new List<MessageEntity> { new() { Type = MessageEntityType.Bold, Offset = 0, Length = 3 } };
        var b = new List<MessageEntity> { new() { Type = MessageEntityType.Bold, Offset = 0, Length = 3 } };
        MessageContentRules.SameEntities(a, b).ShouldBeTrue();
        b[0].Length = 4;
        MessageContentRules.SameEntities(a, b).ShouldBeFalse();
        MessageContentRules.SameEntities(a, []).ShouldBeFalse();
    }

    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        MessageContentRules.Truncate("short", 120).ShouldBe("short");
        MessageContentRules.Truncate("abcdef", 3).ShouldBe("abc");
        var text = "ab😀cd"; // 😀 is two UTF-16 units at index 2..3
        MessageContentRules.Truncate(text, 3).ShouldBe("ab");
        MessageContentRules.Truncate(text, 4).ShouldBe("ab😀");
    }
}
