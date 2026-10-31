using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Client.Core.Services;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// UI strings from embedded <c>Localization/{lang}/{area}.json</c> files (flat keys such as
/// <c>"chat.composer.placeholder"</c>). Russian is the primary language. Singleton without JS dependencies,
/// so Core and native platform services can use it too.
/// </summary>
public interface ILocalizer
{
    /// <summary>Effective language: "ru" or "en".</summary>
    string Language { get; }
    /// <summary>Culture matching <see cref="Language"/> (used for number formatting).</summary>
    CultureInfo Culture { get; }
    /// <summary>Raised after the language changed (settings "Language").</summary>
    event Action? Changed;

    /// <summary>Localized string; falls back to English, then to the key itself.</summary>
    string this[string key] { get; }
    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> over the localized template.</summary>
    string Format(string key, params object?[] args);
    /// <summary>
    /// Picks <c>key.one / key.few / key.many / key.other</c> by the language's plural rules; <c>{0}</c> is the
    /// count, further placeholders come from <paramref name="args"/>.
    /// </summary>
    string Plural(string key, long count, params object?[] args);
    /// <summary>Localized text for an error code (<c>error.&lt;code&gt;</c>), generic text when unknown.</summary>
    string Error(string? code);
    /// <summary>Localized text for an exception (ApiException codes, timeouts, anything else).</summary>
    string Error(Exception exception);
    bool Has(string key);
    /// <summary>Applies a language setting: "auto" (follow the OS), "ru" or "en".</summary>
    void SetLanguage(string? setting);
}

public enum PluralCategory
{
    One,
    Few,
    Many,
    Other,
}

/// <summary>CLDR cardinal plural rules for the languages we ship (integers).</summary>
public static class PluralRules
{
    public static PluralCategory Select(string language, long count)
    {
        var n = Math.Abs(count);
        if (language == "ru")
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11) return PluralCategory.One;
            if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return PluralCategory.Few;
            return PluralCategory.Many;
        }

        return n == 1 ? PluralCategory.One : PluralCategory.Other;
    }

    internal static string Suffix(PluralCategory category) => category switch
    {
        PluralCategory.One => "one",
        PluralCategory.Few => "few",
        PluralCategory.Many => "many",
        _ => "other",
    };
}

public sealed class Localizer : ILocalizer
{
    public const string Russian = "ru";
    public const string English = "en";

    private static readonly string[] RussianLike = ["ru", "be", "kk", "ky", "uk", "tg", "uz"];

    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _tables = new();
    private readonly object _gate = new();
    private string _language;
    private CultureInfo _culture;
    private bool _settingsHooked;

    public Localizer(IServiceProvider services)
    {
        _services = services;
        _language = ResolveAuto();
        _culture = CultureFor(_language);
    }

    public event Action? Changed;

    public string Language
    {
        get
        {
            EnsureSettingsHooked();
            return _language;
        }
    }

    public CultureInfo Culture
    {
        get
        {
            EnsureSettingsHooked();
            return _culture;
        }
    }

    public string this[string key] => Lookup(key) ?? key;

    public bool Has(string key) => Lookup(key) is not null;

    public string Format(string key, params object?[] args)
    {
        var template = this[key];
        if (args.Length == 0) return template;
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public string Plural(string key, long count, params object?[] args)
    {
        var language = Language;
        var category = PluralRules.Select(language, count);
        var template = Lookup($"{key}.{PluralRules.Suffix(category)}")
                       ?? Lookup($"{key}.other")
                       ?? Lookup($"{key}.many")
                       ?? Lookup(key)
                       ?? key;
        var all = new object?[args.Length + 1];
        all[0] = count;
        Array.Copy(args, 0, all, 1, args.Length);
        try
        {
            return string.Format(Culture, template, all);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public string Error(string? code)
    {
        if (!string.IsNullOrEmpty(code) && Lookup($"error.{code}") is { } text) return text;
        return this["error.unknown"];
    }

    public string Error(Exception exception) => exception switch
    {
        ApiException api => Error(api.Code),
        TimeoutException => Error(ClientErrorCodes.Timeout),
        TaskCanceledException => Error(ClientErrorCodes.Timeout),
        HttpRequestException => Error(ClientErrorCodes.Network),
        _ => Error(ClientErrorCodes.Unknown),
    };

    public void SetLanguage(string? setting)
    {
        var language = setting switch
        {
            Russian => Russian,
            English => English,
            _ => ResolveAuto(),
        };
        bool changed;
        lock (_gate)
        {
            changed = language != _language;
            _language = language;
            _culture = CultureFor(language);
        }
        if (!changed) return;
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch { /* one broken subscriber must not break the others */ }
        }
    }

    private string? Lookup(string key)
    {
        var language = Language;
        if (Table(language).TryGetValue(key, out var value)) return value;
        if (language != English && Table(English).TryGetValue(key, out value)) return value;
        return null;
    }

    private IReadOnlyDictionary<string, string> Table(string language) => _tables.GetOrAdd(language, Load);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(Localizer).Assembly;
        var marker = $".Localization.{language}.";
        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!name.Contains(marker, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                Flatten(document.RootElement, null, result);
            }
            catch (JsonException)
            {
                // A malformed area file must not take the whole UI down; its keys simply fall back.
            }
        }
        return result;
    }

    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Flatten(property.Value, prefix is null ? property.Name : $"{prefix}.{property.Name}", into);
                break;
            case JsonValueKind.String when prefix is not null:
                into[prefix] = element.GetString() ?? "";
                break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False when prefix is not null:
                into[prefix] = element.GetRawText();
                break;
        }
    }

    private void EnsureSettingsHooked()
    {
        if (_settingsHooked) return;
        lock (_gate)
        {
            if (_settingsHooked) return;
            _settingsHooked = true;
        }
        try
        {
            // Resolved lazily: platform services may use the localizer while the settings service is being built.
            var settings = _services.GetService<ISettingsService>();
            if (settings is null) return;
            settings.Changed += () => SetLanguage(settings.Current.Language);
            SetLanguage(settings.Current.Language);
        }
        catch (InvalidOperationException)
        {
            lock (_gate) _settingsHooked = false;
        }
    }

    private static string ResolveAuto()
    {
        var name = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.IsNullOrEmpty(name) || name == "iv") return Russian;
        if (name == English) return English;
        return RussianLike.Contains(name) ? Russian : English;
    }

    private static CultureInfo CultureFor(string language)
    {
        try
        {
            if (language == English)
            {
                var current = CultureInfo.CurrentCulture;
                return current.TwoLetterISOLanguageName == English ? current : CultureInfo.GetCultureInfo("en-US");
            }
            return CultureInfo.GetCultureInfo("ru-RU");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
