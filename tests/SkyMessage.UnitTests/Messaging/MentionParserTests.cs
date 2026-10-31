using Shouldly;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class MentionParserTests
{
    [Fact]
    public void Finds_usernames_normalized_and_distinct() =>
        MentionParser.ExtractUsernames("@Alice_1 hi @bobby_x and again @alice_1!", null)
            .ShouldBe(["alice_1", "bobby_x"]);

    [Theory]
    [InlineData("@alice")]
    [InlineData("(@alice)")]
    [InlineData("hi,@alice.")]
    [InlineData("line\n@alice")]
    [InlineData("@alice's cat")]
    public void Word_starting_mentions_are_found(string text) =>
        MentionParser.ExtractUsernames(text, null).ShouldBe(["alice"]);

    [Theory]
    [InlineData("mail@alice.com")]
    [InlineData("@@alice")]
    [InlineData("привет@alice")]
    [InlineData("@bob")]
    [InlineData("@1alice")]
    [InlineData("@_alice")]
    [InlineData("no mention here")]
    [InlineData("")]
    public void Non_mentions_are_ignored(string text) => MentionParser.ExtractUsernames(text, null).ShouldBeEmpty();

    [Fact]
    public void Usernames_longer_than_allowed_are_not_truncated_into_a_match()
    {
        var tooLong = "@a" + new string('b', Limits.UsernameMaxLength);
        MentionParser.ExtractUsernames(tooLong, null).ShouldBeEmpty();
        var longest = "@a" + new string('b', Limits.UsernameMaxLength - 1);
        MentionParser.ExtractUsernames(longest, null).Single().Length.ShouldBe(Limits.UsernameMaxLength);
    }

    [Fact]
    public void Code_pre_url_and_email_spans_are_skipped()
    {
        const string text = "`@alice` @bobby https://x.example/@carol @david";
        var entities = new[]
        {
            new MessageEntityDto { Type = MessageEntityType.Code, Offset = 0, Length = 8 },
            new MessageEntityDto { Type = MessageEntityType.Url, Offset = 16, Length = 24 },
        };
        MentionParser.ExtractUsernames(text, entities).ShouldBe(["bobby", "david"]);
    }

    [Fact]
    public void Result_is_capped()
    {
        var text = string.Join(' ', Enumerable.Range(0, MentionParser.MaxUsernames + 20).Select(i => $"@user{i:D4}"));
        MentionParser.ExtractUsernames(text, null).Count.ShouldBe(MentionParser.MaxUsernames);
    }
}
