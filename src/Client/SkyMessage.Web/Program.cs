using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core;
using SkyMessage.Client.UI;
using SkyMessage.Web.Platform;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.SetMinimumLevel(builder.HostEnvironment.IsDevelopment() ? LogLevel.Debug : LogLevel.Warning);

// Served by the API: always talk to the page's own origin. Standalone dev (dotnet run on this project):
// set "ServerUrl" in wwwroot/appsettings.Development.json (the API's CORS must allow the dev origin).
var serverUrl = builder.Configuration["ServerUrl"];
builder.Services.AddSkyMessageClientCore(options =>
{
    options.MediaUrlMode = MediaUrlMode.Direct;
    if (string.IsNullOrWhiteSpace(serverUrl))
    {
        options.UseHostOrigin = true;
        options.HostOrigin = builder.HostEnvironment.BaseAddress;
        options.DefaultServerUrl = builder.HostEnvironment.BaseAddress;
    }
    else
    {
        options.DefaultServerUrl = serverUrl.EndsWith('/') ? serverUrl : serverUrl + "/";
    }
});
builder.Services.AddSkyMessageWebPlatform();
builder.Services.AddSkyMessageUI();

await builder.Build().RunAsync();
