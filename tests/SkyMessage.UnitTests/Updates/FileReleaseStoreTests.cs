using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SkyMessage.Application.Common.Options;
using SkyMessage.Infrastructure.Updates;
using Xunit;

namespace SkyMessage.UnitTests.Updates;

public sealed class FileReleaseStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sky-updates-" + Guid.NewGuid().ToString("N"));

    public FileReleaseStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FileReleaseStore Store(string? path = null, string manifest = "latest.json") =>
        new(Options.Create(new UpdatesOptions { Path = path ?? _root, ManifestFile = manifest }), NullLogger<FileReleaseStore>.Instance);

    private void WriteManifest(int versionCode, string name = "latest.json")
    {
        // Like CI: a temporary file renamed over the old manifest.
        var temp = Path.Combine(_root, name + ".tmp");
        File.WriteAllText(temp, $$"""{ "android": { "versionCode": {{versionCode}}, "file": "SkyMessage-{{versionCode}}.apk" } }""");
        File.Move(temp, Path.Combine(_root, name), overwrite: true);
    }

    [Fact]
    public void No_directory_configured_means_no_releases()
    {
        var store = new FileReleaseStore(Options.Create(new UpdatesOptions { Path = null }), NullLogger<FileReleaseStore>.Instance);

        store.ReadManifest().ShouldBeNull();
        store.PackageLength("SkyMessage-1.apk").ShouldBeNull();
    }

    [Fact]
    public void A_missing_or_broken_manifest_is_no_release()
    {
        var store = Store();
        store.ReadManifest().ShouldBeNull();

        File.WriteAllText(Path.Combine(_root, "latest.json"), "{ half");
        store.ReadManifest().ShouldBeNull();
    }

    [Fact]
    public void A_replaced_manifest_is_read_again()
    {
        var store = Store();
        WriteManifest(5);
        store.ReadManifest()!.Android!.VersionCode.ShouldBe(5);

        WriteManifest(123456);
        // Another length: noticed even when the file system's timestamps are coarse.
        store.ReadManifest()!.Android!.VersionCode.ShouldBe(123456);
    }

    [Theory]
    [InlineData("../latest.json")]
    [InlineData("sub/latest.json")]
    [InlineData(".latest.json")]
    [InlineData("")]
    public void The_manifest_name_must_be_a_plain_file_in_the_directory(string name)
    {
        Store(manifest: name).ReadManifest().ShouldBeNull();
    }

    [Fact]
    public void Package_lengths_come_from_the_directory_and_unsafe_names_are_refused()
    {
        var updates = Directory.CreateDirectory(Path.Combine(_root, "updates")).FullName;
        File.WriteAllBytes(Path.Combine(updates, "SkyMessage-7.apk"), new byte[321]);
        File.WriteAllBytes(Path.Combine(updates, "latest.json"), new byte[2]);
        File.WriteAllBytes(Path.Combine(_root, "outside.apk"), new byte[5]);
        var store = Store(updates);

        store.PackageLength("SkyMessage-7.apk").ShouldBe(321);
        store.PackageLength("SkyMessage-8.apk").ShouldBeNull();
        store.PackageLength("../outside.apk").ShouldBeNull();
        store.PackageLength("latest.json").ShouldBeNull();
    }
}
