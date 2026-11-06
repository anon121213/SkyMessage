using System.Security.Cryptography;
using System.Text.Json.Serialization;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Settings;

/// <summary>How my message bubbles are painted.</summary>
public enum OutgoingBubbleStyle
{
    /// <summary>The accent's gradient pair.</summary>
    Gradient,
    /// <summary>The accent as one flat color.</summary>
    Solid,
    /// <summary>My own color, or two of them as a gradient (<see cref="AppearanceSettings.OutgoingColors"/>).</summary>
    Custom,
}

/// <summary>What a background shows.</summary>
public enum BackdropKind
{
    /// <summary>One of the built-in skies (<see cref="Backdrop.Sky"/>), drawn by the UI's SkyBackdrop.</summary>
    Sky,
    /// <summary>A gradient of the user's colors (<see cref="Backdrop.Colors"/> at <see cref="Backdrop.Angle"/>).</summary>
    Gradient,
    /// <summary>A picture from the gallery kept on this device (<see cref="Backdrop.Photo"/>), with blur and dim.</summary>
    Photo,
}

/// <summary>A picture the user chose as a background: a file on this device (IBackdropImageStore), described here.</summary>
public sealed class BackdropPhoto
{
    /// <summary>12 lowercase hex characters (<see cref="BackdropRules.NewId"/>): every picked picture gets a new one.</summary>
    public string Id { get; set; } = "";
    /// <summary>One of <see cref="BackdropRules.Extensions"/>.</summary>
    public string Extension { get; set; } = ".jpg";
    /// <summary>Size as stored, in pixels (0 = unknown).</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The stored file's name.</summary>
    [JsonIgnore]
    public string FileName => Id + Extension;
}

/// <summary>A background behind the app's screens (<see cref="AppBackground"/>) or of chats.</summary>
public sealed class Backdrop
{
    [JsonConverter(typeof(TolerantEnumConverter<BackdropKind>))]
    public BackdropKind Kind { get; set; }
    /// <summary>Kind = Sky: the sky's key (the UI falls back to its default sky for a key it does not know).</summary>
    public string? Sky { get; set; }
    /// <summary>Kind = Gradient: 2–3 "#RRGGBB" stops, first to last.</summary>
    public List<string> Colors { get; set; } = [];
    /// <summary>Kind = Gradient: the direction in degrees as in CSS (0 points up, 90 right, 180 down), 0..359.</summary>
    public int Angle { get; set; } = BackdropRules.DefaultAngle;
    /// <summary>Kind = Photo.</summary>
    public BackdropPhoto? Photo { get; set; }
    /// <summary>Kind = Photo: 0 (sharp) .. 100 (as soft as it gets).</summary>
    public int Blur { get; set; }
    /// <summary>Kind = Photo: 0 (as it is) .. 100, a veil of the theme's own background color over the picture.</summary>
    public int Dim { get; set; }

    public Backdrop Clone() => new()
    {
        Kind = Kind,
        Sky = Sky,
        Colors = [.. Colors ?? []],
        Angle = Angle,
        Photo = Photo is { } p ? new BackdropPhoto { Id = p.Id, Extension = p.Extension, Width = p.Width, Height = p.Height } : null,
        Blur = Blur,
        Dim = Dim,
    };
}

/// <summary>"Свой фон" of the app (<see cref="AppearanceSettings.Wallpaper"/> = "custom"): one for both themes, or one for each.</summary>
public sealed class AppBackground
{
    /// <summary>True: <see cref="Light"/> shows in the dark theme too.</summary>
    public bool SameForBoth { get; set; } = true;
    public Backdrop Light { get; set; } = BackdropRules.DefaultGradient(dark: false);
    public Backdrop Dark { get; set; } = BackdropRules.DefaultGradient(dark: true);

    /// <summary>The background in effect in a theme.</summary>
    public Backdrop For(bool dark) => SameForBoth || !dark ? Light : Dark;

    public AppBackground Clone() => new() { SameForBoth = SameForBoth, Light = Light.Clone(), Dark = Dark.Clone() };
}

/// <summary>What a background may be, and the pictures it uses.</summary>
public static class BackdropRules
{
    /// <summary><see cref="AppearanceSettings.Wallpaper"/> value for the app's own background.</summary>
    public const string CustomWallpaper = "custom";
    public const string DefaultSky = "aurora";
    public const int DefaultAngle = 160;
    public const int MinColors = 2;
    public const int MaxColors = 3;
    public const int MaxLevel = 100;
    /// <summary>
    /// A new picture behind the app's screens starts soft and veiled: titles and list rows sit right on it. In a chat the
    /// bubbles carry their own background, so the picture starts sharp with a light veil.
    /// </summary>
    public const int AppPhotoBlur = 24;
    public const int AppPhotoDim = 40;
    public const int ChatPhotoBlur = 0;
    public const int ChatPhotoDim = 15;
    /// <summary>A picture is downscaled to the screen before it is stored; this only stops an original that could not be.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>Stored formats and their content types. Pictures are always shown still (a GIF shows its first frame).</summary>
    public static IReadOnlyDictionary<string, string> Extensions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
    };

    private static readonly Dictionary<string, string> TypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/pjpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    /// <summary>
    /// The extension to store a picture under: from its content type, or from its name when the type says nothing
    /// (none, or application/octet-stream); null for formats a page cannot show (e.g. HEIC).
    /// </summary>
    public static string? ExtensionOf(string? contentType, string? fileName)
    {
        var type = contentType?.Split(';', 2)[0].Trim();
        if (type is { Length: > 0 } && !string.Equals(type, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return TypeExtensions.GetValueOrDefault(type);
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (extension == ".jpeg") return ".jpg";
        return Extensions.ContainsKey(extension) ? extension : null;
    }

    /// <summary>12 lowercase hex characters from a CSPRNG.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    /// <summary>A description that can come from saved settings: an id and extension safe to use as a file name.</summary>
    public static bool IsWellFormed(BackdropPhoto? photo) =>
        photo is not null
        && photo.Id.Length == 12
        && photo.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && Extensions.ContainsKey(photo.Extension)
        && photo.Width >= 0 && photo.Height >= 0;

    /// <summary>The gradient a new "own background" starts from: a soft dawn by day, a deep night at night.</summary>
    public static Backdrop DefaultGradient(bool dark) => new()
    {
        Kind = BackdropKind.Gradient,
        Colors = dark ? ["#2A1D5C", "#120E2E"] : ["#E9E2FF", "#FFE9EF"],
        Angle = DefaultAngle,
    };

    /// <summary>
    /// A clean copy of a saved background, or null when it cannot be shown: Sky needs a key (and <paramref name="allowSky"/>),
    /// Gradient 2–3 valid colors (more are dropped), Photo a well-formed picture. Fields of other kinds are dropped; the
    /// angle wraps into 0..359, blur and dim are clamped to 0..100.
    /// </summary>
    public static Backdrop? Normalize(Backdrop? backdrop, bool allowSky = true)
    {
        if (backdrop is null) return null;
        switch (backdrop.Kind)
        {
            case BackdropKind.Sky:
                var sky = backdrop.Sky?.Trim().ToLowerInvariant();
                return allowSky && !string.IsNullOrEmpty(sky) && sky != CustomWallpaper ? new Backdrop { Kind = BackdropKind.Sky, Sky = sky } : null;
            case BackdropKind.Gradient:
                var colors = (backdrop.Colors ?? []).Select(AppearanceRules.NormalizeHex).OfType<string>().Take(MaxColors).ToList();
                return colors.Count < MinColors ? null : new Backdrop { Kind = BackdropKind.Gradient, Colors = colors, Angle = WrapAngle(backdrop.Angle) };
            case BackdropKind.Photo:
                if (!IsWellFormed(backdrop.Photo)) return null;
                var p = backdrop.Photo!;
                return new Backdrop
                {
                    Kind = BackdropKind.Photo,
                    Photo = new BackdropPhoto { Id = p.Id, Extension = p.Extension, Width = p.Width, Height = p.Height },
                    Blur = Math.Clamp(backdrop.Blur, 0, MaxLevel),
                    Dim = Math.Clamp(backdrop.Dim, 0, MaxLevel),
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// The app's own background, repaired: each theme's side is a gradient or a photo (a sky is chosen in the sky list
    /// itself); a side that cannot be shown starts over from <see cref="DefaultGradient"/>. Null when neither side can.
    /// </summary>
    public static AppBackground? Normalize(AppBackground? background)
    {
        if (background is null) return null;
        var light = Normalize(background.Light, allowSky: false);
        var dark = Normalize(background.Dark, allowSky: false);
        if (light is null && dark is null) return null;
        return new AppBackground
        {
            SameForBoth = background.SameForBoth,
            Light = light ?? (background.SameForBoth ? dark!.Clone() : DefaultGradient(dark: false)),
            Dark = dark ?? DefaultGradient(dark: true),
        };
    }

    public static int WrapAngle(int angle) => (angle % 360 + 360) % 360;

    /// <summary>Every picture the appearance settings refer to (the app's own background even while a sky is chosen).</summary>
    public static IEnumerable<BackdropPhoto> PhotosIn(AppearanceSettings appearance)
    {
        if (appearance.CustomBackground is { } custom)
        {
            if (custom.Light.Photo is { } light) yield return light;
            if (custom.Dark.Photo is { } dark) yield return dark;
        }
        if (appearance.ChatBackground?.Photo is { } chat) yield return chat;
        foreach (var backdrop in appearance.ChatBackgrounds?.Values ?? Enumerable.Empty<Backdrop>())
        {
            if (backdrop.Photo is { } photo) yield return photo;
        }
    }

    /// <summary>File names of <see cref="PhotosIn"/>.</summary>
    public static HashSet<string> FileNamesIn(AppearanceSettings appearance) =>
        PhotosIn(appearance).Where(IsWellFormed).Select(p => p.FileName).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Repairs the appearance settings on load and after every change (SettingsService).</summary>
public static class AppearanceRules
{
    public const string DefaultTint = "default";

    /// <summary>Calm tints for the other person's bubbles; the UI's theme catalog paints them.</summary>
    public static IReadOnlyList<string> IncomingTints { get; } = [DefaultTint, "lavender", "peach", "mint", "sky", "sand"];

    /// <summary>At most this many own bubble colors (two make a gradient).</summary>
    public const int MaxOutgoingColors = 2;

    /// <summary>"#RRGGBB" in upper case, or null when the value is not a 6-digit hex color ("6c5ce7" is accepted).</summary>
    public static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith('#')) v = v[1..];
        return v.Length == 6 && v.All(Uri.IsHexDigit) ? "#" + v.ToUpperInvariant() : null;
    }

    public static void Normalize(AppearanceSettings a)
    {
        a.OutgoingColors = (a.OutgoingColors ?? []).Select(NormalizeHex).OfType<string>().Take(MaxOutgoingColors).ToList();
        if (!Enum.IsDefined(a.OutgoingBubble)) a.OutgoingBubble = OutgoingBubbleStyle.Gradient;
        if (a.OutgoingBubble == OutgoingBubbleStyle.Custom && a.OutgoingColors.Count == 0) a.OutgoingBubble = OutgoingBubbleStyle.Gradient;

        var tint = a.IncomingTint?.Trim();
        a.IncomingTint = IncomingTints.FirstOrDefault(t => string.Equals(t, tint, StringComparison.OrdinalIgnoreCase)) ?? DefaultTint;

        a.CustomBackground = BackdropRules.Normalize(a.CustomBackground);
        if (string.Equals(a.Wallpaper?.Trim(), BackdropRules.CustomWallpaper, StringComparison.OrdinalIgnoreCase))
            a.Wallpaper = a.CustomBackground is null ? BackdropRules.DefaultSky : BackdropRules.CustomWallpaper;

        a.ChatBackground = BackdropRules.Normalize(a.ChatBackground);
        var chats = new Dictionary<Guid, Backdrop>();
        foreach (var (chatId, backdrop) in a.ChatBackgrounds ?? [])
        {
            if (chatId != Guid.Empty && BackdropRules.Normalize(backdrop) is { } clean) chats[chatId] = clean;
        }
        a.ChatBackgrounds = chats;
    }
}
