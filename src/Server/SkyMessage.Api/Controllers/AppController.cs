using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Updates;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>App releases: the version check every client runs and a stable download link for the APK.</summary>
[ApiController]
public sealed class AppController(AppVersionService versions, IWebHostEnvironment environment) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet(ApiRoutes.AppVersion)]
    public AppVersionDto Version()
    {
        // A release replaces the manifest in place: never let a cache keep answering with the previous one.
        Response.Headers.CacheControl = "no-cache";
        return versions.Get(ServesWebClient());
    }

    /// <summary>
    /// The current APK under a fixed address (to send to someone installing SkyMessage for the first time). HEAD too:
    /// download managers and link checkers probe with it before they fetch.
    /// </summary>
    [AllowAnonymous]
    [HttpGet(ApiRoutes.LatestApk)]
    [HttpHead(ApiRoutes.LatestApk)]
    public IActionResult LatestApk()
    {
        var release = versions.LatestAndroid() ?? throw new NotFoundException();
        Response.Headers.CacheControl = "no-store";
        return Redirect(Url.Content("~/" + release.ApkUrl));
    }

    private bool ServesWebClient() => environment.WebRootFileProvider.GetFileInfo("index.html").Exists;
}
