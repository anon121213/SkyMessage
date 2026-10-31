using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.App.Services;

/// <summary>
/// Localized texts for native UI (notifications, channels, audio routes) from
/// <c>Localization/{ru,en}/platform.json</c>. The UI registers <see cref="ILocalizer"/> as a singleton;
/// it is resolved lazily because native services can be created before the UI.
/// </summary>
public sealed class NativeStrings(IServiceProvider services)
{
    private ILocalizer? _localizer;

    private ILocalizer Localizer => _localizer ??= services.GetService<ILocalizer>() ?? new Localizer(services);

    public string this[string key] => Localizer[key];

    public string Format(string key, params object?[] args) => Localizer.Format(key, args);

    public string Language => Localizer.Language;
}
