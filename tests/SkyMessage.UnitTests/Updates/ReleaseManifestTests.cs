using Shouldly;
using SkyMessage.Application.Common;
using SkyMessage.Application.Updates;
using Xunit;

namespace SkyMessage.UnitTests.Updates;

public sealed class ReleaseManifestTests
{
    private const string Sha = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static AndroidReleaseEntry Entry(int code = 57, string? file = "SkyMessage-0.2.57.apk", string? sha = Sha, long size = 1234,
        int min = 0, string? name = "0.2.57", string? notes = null) => new()
        {
            VersionCode = code, VersionName = name, File = file, Sha256 = sha, Size = size, MinVersionCode = min, Notes = notes,
        };

    private static Func<string, long?> Files(params (string Name, long Length)[] files) =>
        name => files.FirstOrDefault(f => f.Name == name) is { Name: not null } hit ? hit.Length : null;

    [Fact]
    public void The_manifest_ci_writes_parses_and_unknown_fields_are_ignored()
    {
        var manifest = ReleaseManifestRules.Parse("""
            { "android": { "versionCode": 57, "versionName": "0.2.57", "file": "SkyMessage-0.2.57.apk", "sha256": "ABC",
                           "size": 61234567, "notes": "Fixes", "minVersionCode": 40, "publishedAt": "2026-10-31T12:00:00Z",
                           "commit": "8ed03bb" },
              "web": { "version": "0.2.57" } }
            """);

        manifest.ShouldNotBeNull();
        var android = manifest.Android.ShouldNotBeNull();
        android.VersionCode.ShouldBe(57);
        android.File.ShouldBe("SkyMessage-0.2.57.apk");
        android.Size.ShouldBe(61234567);
        android.MinVersionCode.ShouldBe(40);
        android.PublishedAt.ShouldBe(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"android\": 5 }")]
    public void Broken_manifests_are_none_instead_of_errors(string? json)
    {
        ReleaseManifestRules.Parse(json).ShouldBeNull();
    }

    [Fact]
    public void A_complete_release_becomes_a_relative_download_with_a_lowercase_hash()
    {
        var dto = ReleaseManifestRules.ToDto(Entry(sha: Sha.ToUpperInvariant(), min: 12, notes: "  Faster calls  "), Files(("SkyMessage-0.2.57.apk", 1234)));

        dto.ShouldNotBeNull();
        dto.VersionCode.ShouldBe(57);
        dto.VersionName.ShouldBe("0.2.57");
        dto.ApkUrl.ShouldBe("updates/SkyMessage-0.2.57.apk");
        dto.Sha256.ShouldBe(Sha);
        dto.Size.ShouldBe(1234);
        dto.MinVersionCode.ShouldBe(12);
        dto.Notes.ShouldBe("Faster calls");
    }

    [Fact]
    public void A_release_whose_apk_is_missing_or_still_uploading_is_not_offered()
    {
        ReleaseManifestRules.ToDto(Entry(), Files()).ShouldBeNull();
        ReleaseManifestRules.ToDto(Entry(size: 1234), Files(("SkyMessage-0.2.57.apk", 1000))).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, "SkyMessage.apk", Sha, 10L)]
    [InlineData(5, "SkyMessage.apk", Sha, 0L)]
    [InlineData(5, "SkyMessage.apk", "abc", 10L)]
    [InlineData(5, "SkyMessage.apk", null, 10L)]
    [InlineData(5, "../secret.apk", Sha, 10L)]
    [InlineData(5, "SkyMessage.zip", Sha, 10L)]
    [InlineData(5, null, Sha, 10L)]
    public void Incomplete_or_unsafe_entries_are_not_offered(int code, string? file, string? sha, long size)
    {
        ReleaseManifestRules.ToDto(Entry(code, file, sha, size), _ => size).ShouldBeNull();
    }

    [Fact]
    public void Nothing_is_offered_without_an_entry()
    {
        ReleaseManifestRules.ToDto(null, _ => 1).ShouldBeNull();
    }

    [Fact]
    public void A_minimum_above_the_offered_build_cannot_lock_every_phone_out()
    {
        ReleaseManifestRules.ToDto(Entry(code: 57, min: 900), _ => 1234)!.MinVersionCode.ShouldBe(57);
        ReleaseManifestRules.ToDto(Entry(code: 57, min: -3), _ => 1234)!.MinVersionCode.ShouldBe(0);
    }

    [Fact]
    public void A_missing_version_name_falls_back_to_the_code_and_long_notes_are_cut()
    {
        var dto = ReleaseManifestRules.ToDto(Entry(name: " ", notes: new string('a', ReleaseManifestRules.NotesMaxLength + 50)), _ => 1234)!;

        dto.VersionName.ShouldBe("57");
        dto.Notes!.Length.ShouldBe(ReleaseManifestRules.NotesMaxLength + 1);
        dto.Notes.ShouldEndWith("…");
    }

    [Theory]
    [InlineData("SkyMessage-0.2.57.apk", true)]
    [InlineData("skymessage_57.APK", true)]
    [InlineData(".hidden.apk", false)]
    [InlineData(".apk", false)]
    [InlineData("a..b.apk", false)]
    [InlineData("dir/app.apk", false)]
    [InlineData("dir\\app.apk", false)]
    [InlineData("app.apk.part", false)]
    [InlineData("прил.apk", false)]
    [InlineData("app name.apk", false)]
    public void Only_plain_apk_file_names_are_served(string name, bool safe)
    {
        ReleaseManifestRules.IsSafeFileName(name).ShouldBe(safe);
    }

    [Fact]
    public void The_version_endpoint_reports_the_server_build_for_the_web_client()
    {
        var store = new FakeStore(new ReleaseManifest { Android = Entry() }, 1234);
        var service = new AppVersionService(store);

        var withWeb = service.Get(servesWebClient: true);
        withWeb.Android!.VersionCode.ShouldBe(57);
        withWeb.Web!.Version.ShouldBe(ServerBuild.Version);
        withWeb.ServerVersion.ShouldBe(ServerBuild.Version);

        service.Get(servesWebClient: false).Web.ShouldBeNull();
        new AppVersionService(new FakeStore(null, null)).Get(true).Android.ShouldBeNull();
    }

    private sealed class FakeStore(ReleaseManifest? manifest, long? length) : IReleaseStore
    {
        public ReleaseManifest? ReadManifest() => manifest;

        public long? PackageLength(string fileName) => length;
    }
}
