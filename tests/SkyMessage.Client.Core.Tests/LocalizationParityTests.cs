using System.Reflection;
using System.Text.Json;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Every UI string exists in Russian and English (the same keys in each pair of files).</summary>
public sealed class LocalizationParityTests
{
    private static readonly Assembly Ui = typeof(SkyMessage.Client.UI.Services.MessagePreviewFormatter).Assembly;

    private static Dictionary<string, HashSet<string>> KeysByFile(string language)
    {
        var marker = $".Localization.{language}.";
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in Ui.GetManifestResourceNames().Where(n => n.Contains(marker, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = Ui.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            result[name[(name.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]] =
                document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        }
        return result;
    }

    [Fact]
    public void Russian_and_english_files_hold_the_same_keys()
    {
        var ru = KeysByFile("ru");
        var en = KeysByFile("en");
        ru.Keys.Order().ShouldBe(en.Keys.Order());
        ru.ShouldContainKey("stickers.json");
        foreach (var (file, keys) in ru)
        {
            keys.Except(en[file]).ShouldBeEmpty($"{file}: keys only in Russian");
            en[file].Except(keys).ShouldBeEmpty($"{file}: keys only in English");
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Sticker_errors_have_their_own_text(string language)
    {
        var keys = KeysByFile(language).Values.SelectMany(k => k).ToHashSet(StringComparer.Ordinal);
        string[] codes =
        [
            ErrorCodes.StickerPackNotFound, ErrorCodes.StickerNotFound, ErrorCodes.StickerPackFull, ErrorCodes.StickerPackLimit,
            ErrorCodes.StickerImageInvalid, ErrorCodes.NotStickerPackCreator,
        ];
        foreach (var code in codes) keys.ShouldContain($"error.{code}");
    }
}
