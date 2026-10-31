using System.Security.Cryptography;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class AppUpdateRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    internal static AndroidReleaseDto Release(int code = 57, string? url = null, string? sha = null, long size = 1000, int min = 0) => new()
    {
        VersionCode = code,
        VersionName = $"0.2.{code}",
        ApkUrl = url ?? $"updates/SkyMessage-0.2.{code}.apk",
        Sha256 = sha ?? new string('a', 64),
        Size = size,
        MinVersionCode = min,
    };

    [Fact]
    public void Start_and_the_user_always_check_the_server()
    {
        AppUpdateRules.ShouldCheck(UpdateTrigger.Start, Now, Now).ShouldBeTrue();
        AppUpdateRules.ShouldCheck(UpdateTrigger.Manual, Now, Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData((int)UpdateTrigger.Resume, 59, false)]
    [InlineData((int)UpdateTrigger.Resume, 60, true)]
    [InlineData((int)UpdateTrigger.Periodic, 239, false)]
    [InlineData((int)UpdateTrigger.Periodic, 240, true)]
    [InlineData((int)UpdateTrigger.Reconnect, 0, false)]
    [InlineData((int)UpdateTrigger.Reconnect, 1, true)]
    [InlineData((int)UpdateTrigger.Continue, 600, false)]
    public void Resume_periodic_and_reconnect_checks_are_spaced(int trigger, int minutesSinceLastCheck, bool due)
    {
        AppUpdateRules.ShouldCheck((UpdateTrigger)trigger, Now.AddMinutes(-minutesSinceLastCheck), Now).ShouldBe(due);
    }

    [Theory]
    [InlineData((int)UpdateTrigger.Resume)]
    [InlineData((int)UpdateTrigger.Periodic)]
    [InlineData((int)UpdateTrigger.Reconnect)]
    [InlineData((int)UpdateTrigger.Continue)]
    public void Every_trigger_checks_when_there_never_was_a_check(int trigger)
    {
        AppUpdateRules.ShouldCheck((UpdateTrigger)trigger, null, Now).ShouldBeTrue();
    }

    [Fact]
    public void A_clock_moved_backwards_does_not_postpone_checks_for_hours()
    {
        AppUpdateRules.ShouldCheck(UpdateTrigger.Resume, Now.AddDays(2), Now).ShouldBeTrue();
    }

    [Fact]
    public void Failed_attempts_back_off_up_to_three_hours()
    {
        Enumerable.Range(1, 7).Select(AppUpdateRules.RetryDelay).ShouldBe(
        [
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
            TimeSpan.FromHours(3), TimeSpan.FromHours(3), TimeSpan.FromHours(3),
        ]);
    }

    [Theory]
    [InlineData(false, true, NetworkKind.Cellular, true, true)]
    [InlineData(false, false, NetworkKind.None, false, false)]
    [InlineData(true, true, NetworkKind.Wifi, false, true)]
    [InlineData(true, true, NetworkKind.Ethernet, false, true)]
    [InlineData(true, true, NetworkKind.Wifi, true, false)]
    [InlineData(true, true, NetworkKind.Cellular, true, false)]
    [InlineData(true, true, NetworkKind.Unknown, false, false)]
    [InlineData(true, false, NetworkKind.Wifi, false, false)]
    public void Wifi_only_allows_unmetered_wifi_and_ethernet(bool wifiOnly, bool connected, NetworkKind kind, bool metered, bool allowed)
    {
        AppUpdateRules.NetworkAllows(wifiOnly, connected, kind, metered).ShouldBe(allowed);
    }

    [Theory]
    [InlineData("updates/SkyMessage-0.2.57.apk", true)]
    [InlineData("/updates/SkyMessage-0.2.57.apk", true)]
    [InlineData("https://elsewhere.example/app.apk", false)]
    [InlineData("http://chat.example/updates/a.apk", false)]
    [InlineData("//elsewhere.example/app.apk", false)]
    [InlineData("updates/../api/auth/me", false)]
    [InlineData("updates/./a.apk", false)]
    [InlineData("updates//a.apk", false)]
    [InlineData("updates\\a.apk", false)]
    [InlineData("updates/a.apk?x=1", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void Only_relative_download_paths_on_this_server_are_accepted(string url, bool valid)
    {
        AppUpdateRules.IsValid(Release(url: url)).ShouldBe(valid);
    }

    [Fact]
    public void Offers_without_a_hash_size_or_version_are_ignored()
    {
        AppUpdateRules.IsValid(null).ShouldBeFalse();
        AppUpdateRules.IsValid(Release(code: 0)).ShouldBeFalse();
        AppUpdateRules.IsValid(Release(size: 0)).ShouldBeFalse();
        AppUpdateRules.IsValid(Release(sha: "abc")).ShouldBeFalse();
        AppUpdateRules.IsValid(Release(sha: new string('g', 64))).ShouldBeFalse();
        AppUpdateRules.IsValid(Release(sha: new string('F', 64))).ShouldBeTrue();
    }

    [Fact]
    public void Only_a_higher_version_code_is_an_update_and_the_minimum_makes_it_required()
    {
        AppUpdateRules.IsNewer(Release(code: 57), 56).ShouldBeTrue();
        AppUpdateRules.IsNewer(Release(code: 57), 57).ShouldBeFalse();
        AppUpdateRules.IsNewer(Release(code: 57), 60).ShouldBeFalse();
        AppUpdateRules.IsNewer(Release(code: 57, sha: "x"), 1).ShouldBeFalse();

        AppUpdateRules.IsRequired(Release(min: 50), 49).ShouldBeTrue();
        AppUpdateRules.IsRequired(Release(min: 50), 50).ShouldBeFalse();
        AppUpdateRules.IsRequired(null, 1).ShouldBeFalse();
    }

    [Theory]
    [InlineData("0.2.57", "0.2.56", "0.2.57")]
    [InlineData("0.2.56", "0.2.57", "0.2.56")]
    [InlineData("0.2.57", "0.2.57", null)]
    [InlineData(" 0.2.57 ", "0.2.57", null)]
    [InlineData(null, "0.2.57", null)]
    [InlineData("", "0.2.57", null)]
    public void Any_other_web_version_on_the_server_is_new_even_a_rollback(string? served, string running, string? expected)
    {
        AppUpdateRules.NewWebVersion(served, running).ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, false, false, true, false, true)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(true, false, false, true, true, false)]
    public void Background_installs_only_when_nobody_is_using_the_app(bool autoUpdate, bool foreground, bool inCall, bool silent,
        bool confirmationNeeded, bool expected)
    {
        AppUpdateRules.MayInstallInBackground(autoUpdate, foreground, inCall, silent, confirmationNeeded).ShouldBe(expected);
    }

    [Fact]
    public void The_stored_package_is_named_after_its_version_code()
    {
        AppUpdateRules.PackageFileName(Release(code: 123)).ShouldBe("skymessage-123.apk");
    }

    [Fact]
    public async Task Verification_needs_the_exact_size_and_hash()
    {
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));

        (await AppUpdateRules.VerifyAsync(new MemoryStream(bytes), bytes.Length, sha, default)).ShouldBeTrue();
        (await AppUpdateRules.VerifyAsync(new MemoryStream(bytes), bytes.Length, sha.ToLowerInvariant(), default)).ShouldBeTrue();
        (await AppUpdateRules.VerifyAsync(new MemoryStream(bytes[..^1]), bytes.Length, sha, default)).ShouldBeFalse();
        (await AppUpdateRules.VerifyAsync(new MemoryStream([.. bytes, 0]), bytes.Length, sha, default)).ShouldBeFalse();
        var tampered = bytes.ToArray();
        tampered[123] ^= 1;
        (await AppUpdateRules.VerifyAsync(new MemoryStream(tampered), bytes.Length, sha, default)).ShouldBeFalse();
    }
}
