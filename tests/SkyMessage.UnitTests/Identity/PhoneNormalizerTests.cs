using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class PhoneNormalizerTests
{
    [Theory]
    // Russia: national prefix 8, bare 10-digit numbers, international form.
    [InlineData("8 999 123-45-67", "RU", "+79991234567")]
    [InlineData("89991234567", "RU", "+79991234567")]
    [InlineData("8 (999) 123 45 67", "RU", "+79991234567")]
    [InlineData("9991234567", "RU", "+79991234567")]
    [InlineData("+7 (999) 123-45-67", "TH", "+79991234567")]
    // Thailand: national prefix 0, international form.
    [InlineData("081 234 5678", "TH", "+66812345678")]
    [InlineData("0812345678", "TH", "+66812345678")]
    [InlineData("081-234-5678", "th", "+66812345678")]
    [InlineData("+66 81 234 5678", "RU", "+66812345678")]
    // Already E.164.
    [InlineData("+79991234567", null, "+79991234567")]
    [InlineData("+66812345678", null, "+66812345678")]
    public void Normalizes_national_and_international_formats_to_E164(string input, string? region, string expected)
    {
        PhoneNormalizer.Normalize(input, region).ShouldBe(expected);
    }

    [Theory]
    [InlineData("", "RU")]
    [InlineData("   ", "RU")]
    [InlineData("12345", "RU")]
    [InlineData("not a phone", "RU")]
    [InlineData("+7 999", null)]
    [InlineData("0812345678", "RU")]
    public void Rejects_invalid_numbers(string input, string? region)
    {
        PhoneNormalizer.Normalize(input, region).ShouldBeNull();
    }

    [Fact]
    public void Rejects_overlong_input()
    {
        PhoneNormalizer.Normalize("+7" + new string('9', PhoneNormalizer.MaxInputLength), "RU").ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("XX")]
    [InlineData("Russia")]
    public void Missing_or_unknown_regions_fall_back_to_RU(string? region)
    {
        PhoneNormalizer.Normalize("8 999 123-45-67", region).ShouldBe("+79991234567");
    }

    [Fact]
    public void Region_of_a_number_is_detected()
    {
        PhoneNormalizer.RegionOf("+79991234567").ShouldBe("RU");
        PhoneNormalizer.RegionOf("+66812345678").ShouldBe("TH");
        PhoneNormalizer.RegionOf(null).ShouldBeNull();
        PhoneNormalizer.RegionOf("garbage").ShouldBeNull();
    }

    [Fact]
    public void Resolve_region_takes_the_first_usable_candidate()
    {
        PhoneNormalizer.ResolveRegion(null, "zz", "th", "RU").ShouldBe("TH");
        PhoneNormalizer.ResolveRegion(null, PhoneNormalizer.RegionOf("+66812345678")).ShouldBe("TH");
        PhoneNormalizer.ResolveRegion(null, null).ShouldBe(PhoneNormalizer.FallbackRegion);
    }

    [Theory]
    [InlineData("+79991234567", true)]
    [InlineData("8 (999) 123-45-67", true)]
    [InlineData("081 234 5678", true)]
    [InlineData("john_doe", false)]
    [InlineData("@john_doe", false)]
    [InlineData("user123456", false)]
    [InlineData("1234", false)]
    [InlineData("", false)]
    public void Detects_phone_logins(string login, bool expected)
    {
        PhoneNormalizer.LooksLikePhone(login).ShouldBe(expected);
    }

    [Fact]
    public void Login_candidates_prefer_the_client_region_then_RU()
    {
        PhoneNormalizer.LoginCandidates("081 234 5678", "TH").ShouldBe(["+66812345678"]);
        PhoneNormalizer.LoginCandidates("8 999 123-45-67", "TH").ShouldBe(["+79991234567"]);
        PhoneNormalizer.LoginCandidates("8 999 123-45-67", null).ShouldBe(["+79991234567"]);
        PhoneNormalizer.LoginCandidates("+66812345678", "RU").ShouldBe(["+66812345678"]);
        PhoneNormalizer.LoginCandidates("12345", "TH").ShouldBeEmpty();
    }

    [Fact]
    public void Phone_lookup_regions_prefer_the_request_list_then_own_region_then_RU()
    {
        var dualSim = new FindByPhonesRequest { DefaultRegions = ["th", "TH", "xx", "RU"], DefaultRegion = "US" };
        UserDirectoryService.LookupRegions(dualSim, ownRegion: "TH").ShouldBe(["TH", "RU"]);

        var single = new FindByPhonesRequest { DefaultRegion = "TH" };
        UserDirectoryService.LookupRegions(single, ownRegion: "US").ShouldBe(["TH", "US", "RU"]);

        var none = new FindByPhonesRequest();
        UserDirectoryService.LookupRegions(none, ownRegion: null).ShouldBe(["RU"]);
    }
}
