using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Updates;

/// <summary>api/app/version and the APK downloads under /updates, read from the directory CI publishes into.</summary>
[Collection(ApiCollection.Name)]
public sealed class AppVersionEndpointTests(ApiFactory factory) : IAsyncDisposable
{
    private const string ApkName = "SkyMessage-0.2.57.apk";
    private readonly string _updates = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sky-it-updates-" + Guid.NewGuid().ToString("N"))).FullName;
    private WebApplicationFactory<Program>? _configured;

    public async ValueTask DisposeAsync()
    {
        if (_configured is not null) await _configured.DisposeAsync();
        try
        {
            Directory.Delete(_updates, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private byte[] PublishRelease(int versionCode = 57, int minVersionCode = 0, long? announcedSize = null)
    {
        var apk = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
        File.WriteAllBytes(Path.Combine(_updates, ApkName), apk);
        var sha = Convert.ToHexStringLower(SHA256.HashData(apk));
        File.WriteAllText(Path.Combine(_updates, "latest.json"), $$"""
            { "android": { "versionCode": {{versionCode}}, "versionName": "0.2.{{versionCode}}", "file": "{{ApkName}}",
                           "sha256": "{{sha}}", "size": {{announcedSize ?? apk.Length}}, "notes": "Calmer calls",
                           "minVersionCode": {{minVersionCode}} } }
            """);
        return apk;
    }

    private HttpClient Client()
    {
        _configured ??= factory.WithWebHostBuilder(b => b.UseSetting("Updates:Path", _updates));
        var client = _configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", NewIp());
        return client;
    }

    [Fact]
    public async Task Without_an_updates_directory_no_android_build_is_offered()
    {
        using var response = await factory.NewClient().Get(ApiRoutes.AppVersion);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoCache.ShouldBe(true);
        var version = await response.ReadAsync<AppVersionDto>();
        version.Android.ShouldBeNull();
        version.ServerVersion.ShouldNotBeNullOrWhiteSpace();
        (await factory.NewClient().Get(ApiRoutes.LatestApk)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_published_release_is_offered_anonymously_and_its_apk_downloads_in_ranges()
    {
        var apk = PublishRelease(minVersionCode: 40);
        var client = Client();

        var version = await client.GetAs<AppVersionDto>(ApiRoutes.AppVersion);
        var android = version.Android.ShouldNotBeNull();
        android.VersionCode.ShouldBe(57);
        android.VersionName.ShouldBe("0.2.57");
        android.ApkUrl.ShouldBe($"updates/{ApkName}");
        android.Size.ShouldBe(apk.Length);
        android.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(apk)));
        android.MinVersionCode.ShouldBe(40);
        android.Notes.ShouldBe("Calmer calls");

        using var full = await client.Get("/" + android.ApkUrl);
        full.StatusCode.ShouldBe(HttpStatusCode.OK);
        full.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.android.package-archive");
        full.Content.Headers.ContentLength.ShouldBe(apk.Length);
        full.Headers.AcceptRanges.ShouldContain("bytes");
        (await full.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(apk);

        // A phone resuming an interrupted download.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + android.ApkUrl);
        request.Headers.Range = new RangeHeaderValue(100_000, null);
        using var rest = await client.SendAsync(request, Ct);
        rest.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        rest.Content.Headers.ContentRange!.From.ShouldBe(100_000);
        (await rest.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(apk[100_000..]);
    }

    [Fact]
    public async Task The_stable_link_redirects_to_the_current_apk()
    {
        PublishRelease();
        var client = Client();

        using var response = await client.Get("/" + ApiRoutes.LatestApk);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe($"/updates/{ApkName}");
    }

    [Fact]
    public async Task A_manifest_announcing_another_size_offers_nothing()
    {
        PublishRelease(announcedSize: 999);
        var client = Client();

        (await client.GetAs<AppVersionDto>(ApiRoutes.AppVersion)).Android.ShouldBeNull();
        (await client.Get("/" + ApiRoutes.LatestApk)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_apks_are_served_and_missing_files_are_404_not_the_web_client()
    {
        PublishRelease();
        File.WriteAllText(Path.Combine(_updates, "notes.txt"), "internal");
        var client = Client();

        foreach (var path in new[] { "/updates/latest.json", "/updates/notes.txt", "/updates/SkyMessage-0.1.1.apk", "/updates/" })
        {
            using var response = await client.Get(path);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            (response.Content.Headers.ContentType?.MediaType ?? "").ShouldNotBe("text/html", path);
        }
    }
}
