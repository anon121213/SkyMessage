using System.Globalization;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.App.Services;

public sealed class PlatformInfo : IPlatformInfo
{
    private static readonly TimeSpan CountryCacheTime = TimeSpan.FromMinutes(5);

    private readonly Lazy<string> _deviceName = new(ResolveDeviceName);
    private readonly object _countryGate = new();
    private string? _country;
    private DateTime _countryResolvedAt = DateTime.MinValue;

    public ClientPlatform Platform =>
#if ANDROID
        ClientPlatform.Android;
#elif IOS
        ClientPlatform.iOS;
#else
        ClientPlatform.MacOS;
#endif

    public string DeviceName => _deviceName.Value;

    public string? OsVersion
    {
        get
        {
            try
            {
                return DeviceInfo.Current.VersionString;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public string AppVersion => AppInfo.Current.VersionString;

    public bool IsMobile =>
#if MACCATALYST
        false;
#else
        DeviceInfo.Current.Idiom == DeviceIdiom.Phone || DeviceInfo.Current.Idiom == DeviceIdiom.Tablet;
#endif

    public bool IsNativeApp => true;

    /// <summary>SIM country, then network country (Android), then the device region. No permission needed.</summary>
    public string? CountryCode
    {
        get
        {
            lock (_countryGate)
            {
                if (DateTime.UtcNow - _countryResolvedAt > CountryCacheTime)
                {
                    _country = ResolveCountry();
                    _countryResolvedAt = DateTime.UtcNow;
                }
                return _country;
            }
        }
    }

    private static string ResolveDeviceName()
    {
        string name;
        try
        {
#if ANDROID
            var manufacturer = Android.OS.Build.Manufacturer ?? "";
            var model = Android.OS.Build.Model ?? "";
            name = model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase) || manufacturer.Length == 0
                ? model
                : $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(manufacturer.ToLowerInvariant())} {model}";
#else
            name = DeviceInfo.Current.Name;
            if (string.IsNullOrWhiteSpace(name)) name = DeviceInfo.Current.Model;
#endif
        }
        catch (Exception)
        {
            name = "";
        }
        name = name.Trim();
        if (name.Length == 0) name = "SkyMessage";
        return name.Length > Limits.DeviceNameMaxLength ? name[..Limits.DeviceNameMaxLength] : name;
    }

    private static string? ResolveCountry()
    {
        try
        {
#if ANDROID
            var context = Android.App.Application.Context;
            if (context.GetSystemService(Android.Content.Context.TelephonyService) is Android.Telephony.TelephonyManager telephony)
            {
                var sim = Normalize(telephony.SimCountryIso);
                if (sim is not null) return sim;
                var network = Normalize(telephony.NetworkCountryIso);
                if (network is not null) return network;
            }
            var locale = Normalize(Java.Util.Locale.Default.Country);
            if (locale is not null) return locale;
#else
            var region = Normalize(Foundation.NSLocale.CurrentLocale.CountryCode);
            if (region is not null) return region;
#endif
        }
        catch (Exception)
        {
            // Fall through to the .NET culture.
        }
        try
        {
            return Normalize(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Normalize(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        iso = iso.Trim();
        return iso.Length == 2 && char.IsAsciiLetter(iso[0]) && char.IsAsciiLetter(iso[1]) ? iso.ToUpperInvariant() : null;
    }
}
