using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class ProfileStyleTests
{
    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  https://example.com/path?q=1  ", "https://example.com/path?q=1")]
    [InlineData("http://сайт.рф", "http://сайт.рф")]
    [InlineData("vk.com/anna", "https://vk.com/anna")]
    [InlineData("https://192.168.1.10:8080/", "https://192.168.1.10:8080/")]
    public void Links_are_normalized_to_absolute_web_addresses(string input, string expected)
    {
        ProfileStyle.NormalizeLinkUrl(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("mailto:anna@example.com")]
    public void Anything_but_a_web_address_is_refused(string? input)
    {
        ProfileStyle.NormalizeLinkUrl(input).ShouldBeNull();
    }

    [Fact]
    public void Overlong_links_are_refused_and_titles_are_cut()
    {
        ProfileStyle.NormalizeLinkUrl("https://example.com/" + new string('a', Limits.ProfileLinkUrlMaxLength)).ShouldBeNull();
        ProfileStyle.NormalizeLinkTitle("  " + new string('я', 40) + "  ")!.Length.ShouldBe(Limits.ProfileLinkTitleMaxLength);
        ProfileStyle.NormalizeLinkTitle("   ").ShouldBeNull();
    }

    [Fact]
    public void Colors_and_covers_are_known_keys_only()
    {
        ProfileStyle.IsColor("violet").ShouldBeTrue();
        ProfileStyle.IsColor("Violet").ShouldBeFalse();
        ProfileStyle.IsColor(null).ShouldBeFalse();
        ProfileStyle.IsCoverGradient("night").ShouldBeTrue();
        ProfileStyle.IsCoverGradient("aurora-borealis").ShouldBeFalse();
    }
}

public sealed class BirthdaysTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    [Theory]
    [InlineData(29, 2, null, true)]
    [InlineData(29, 2, 2024, true)]
    [InlineData(29, 2, 2025, false)]
    [InlineData(31, 11, null, false)]
    [InlineData(30, 11, 1950, true)]
    [InlineData(1, 1, 1899, false)]
    [InlineData(5, 11, 2026, true)]
    [InlineData(6, 11, 2026, false)]
    [InlineData(1, 1, 2027, false)]
    [InlineData(0, 5, null, false)]
    [InlineData(10, 0, null, false)]
    public void Valid_birthdays_are_real_dates_that_already_happened(int day, int month, int? year, bool valid)
    {
        Birthdays.IsValid(day, month, year, Today).ShouldBe(valid);
    }

    [Fact]
    public void A_leap_day_birthday_is_on_february_28_in_other_years()
    {
        var leap = new BirthdayDto { Day = 29, Month = 2, Year = 2000 };

        Birthdays.InYear(29, 2, 2027).ShouldBe(new DateOnly(2027, 2, 28));
        Birthdays.InYear(29, 2, 2028).ShouldBe(new DateOnly(2028, 2, 29));
        Birthdays.IsOn(leap, new DateOnly(2027, 2, 28)).ShouldBeTrue();
        Birthdays.IsOn(leap, new DateOnly(2028, 2, 28)).ShouldBeFalse();
        Birthdays.Next(leap, Today).ShouldBe(new DateOnly(2027, 2, 28));
    }

    [Fact]
    public void The_next_birthday_is_today_or_later()
    {
        var birthday = new BirthdayDto { Day = 4, Month = 11 };

        Birthdays.Next(birthday, Today).ShouldBe(Today);
        Birthdays.Next(birthday, Today.AddDays(1)).ShouldBe(new DateOnly(2027, 11, 4));
    }

    [Fact]
    public void Age_needs_the_year_and_turns_on_the_birthday()
    {
        var birthday = new BirthdayDto { Day = 5, Month = 11, Year = 1990 };

        Birthdays.AgeOn(birthday, Today).ShouldBe(35);
        Birthdays.AgeOn(birthday, Today.AddDays(1)).ShouldBe(36);
        Birthdays.AgeOn(birthday with { Year = null }, Today).ShouldBeNull();
    }
}
