using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class UsernameRulesTests
{
    [Theory]
    [InlineData("john_doe")]
    [InlineData("Alice2024")]
    [InlineData("abcde")]
    [InlineData("a_________")]
    [InlineData("a2345678901234567890123456789012")]
    public void Accepts_well_formed_usernames(string username)
    {
        UsernameRules.Validate(username).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("1abcde")]
    [InlineData("_abcde")]
    [InlineData("john doe")]
    [InlineData("john-doe")]
    [InlineData("john.doe")]
    [InlineData("иван_петров")]
    [InlineData("a23456789012345678901234567890123")]
    public void Rejects_malformed_usernames(string username)
    {
        UsernameRules.Validate(username).ShouldBe(ErrorCodes.UsernameInvalid);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Support")]
    [InlineData("SYSTEM")]
    [InlineData("skymessage")]
    [InlineData("SkyMessage_Team")]
    [InlineData("telegram")]
    [InlineData("Telegram_News")]
    [InlineData("whatsapp")]
    [InlineData("deleted")]
    [InlineData("deleted_account")]
    public void Rejects_reserved_usernames(string username)
    {
        UsernameRules.Validate(username).ShouldBe(ErrorCodes.UsernameInvalid);
    }

    [Theory]
    [InlineData("@john_doe", "john_doe")]
    [InlineData("  @@John_Doe  ", "John_Doe")]
    [InlineData("john_doe", "john_doe")]
    [InlineData(null, "")]
    public void Clean_strips_at_signs_and_whitespace(string? input, string expected)
    {
        UsernameRules.Clean(input).ShouldBe(expected);
    }

    [Fact]
    public void Normalized_form_is_lower_case_without_at_sign()
    {
        User.Normalize(" @John_Doe ").ShouldBe("john_doe");
    }

    [Fact]
    public void Tombstone_usernames_fit_the_column_but_can_never_be_registered()
    {
        var tombstone = User.DeletedUsernameFor(Guid.CreateVersion7());
        tombstone.Length.ShouldBe(User.StoredUsernameMaxLength);
        tombstone.ShouldBe(User.Normalize(tombstone));
        UsernameRules.Validate(tombstone).ShouldBe(ErrorCodes.UsernameInvalid);
    }
}
