using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Profile;

/// <summary>
/// Where a new profile or group photo comes from: the gallery or the camera (then framed in the crop editor) or an
/// emoji on a color. Every path ends in a square JPEG made on the device (<see cref="PickedPhoto.Prepared"/>), which
/// <see cref="AvatarUploader.UploadAsync"/> sends as it is. Scoped (it opens overlays and dialogs).
/// </summary>
public sealed class AvatarFlows(AvatarUploader uploader, IFilePickerService picker, AvatarOverlays overlays, DialogService dialogs, ILocalizer l)
{
    public const string Gallery = "avatar.gallery";
    public const string Camera = "avatar.camera";
    public const string Emoji = "avatar.emoji";

    /// <summary>The sources this device offers, for a choice sheet (callers may add their own options).</summary>
    public IReadOnlyList<ChoiceOption<string>> SourceOptions()
    {
        var options = new List<ChoiceOption<string>> { new(Gallery, l["avatar.choose.gallery"]) { Icon = IconNames.ImageUp } };
        if (picker.CanCapture) options.Add(new ChoiceOption<string>(Camera, l["avatar.choose.camera"]) { Icon = IconNames.Camera });
        options.Add(new ChoiceOption<string>(Emoji, l["avatar.choose.emoji"]) { Icon = IconNames.Smile });
        return options;
    }

    public static bool IsSource(string? key) => key is Gallery or Camera or Emoji;

    /// <summary>Asks for a source, then makes the photo; null when cancelled anywhere on the way.</summary>
    public async Task<PickedPhoto?> ChooseAsync()
    {
        var choice = await dialogs.ChooseAsync(l["avatar.choose.title"], SourceOptions());
        return choice is null ? null : await MakeAsync(choice.Value);
    }

    /// <summary>A photo from <paramref name="source"/> (<see cref="Gallery"/>, <see cref="Camera"/>, <see cref="Emoji"/>), framed.</summary>
    public async Task<PickedPhoto?> MakeAsync(string source, CancellationToken cancellationToken = default)
    {
        switch (source)
        {
            case Emoji:
                var drawn = await dialogs.ShowAsync<EmojiAvatarDialog>(l["avatar.emoji.title"], null,
                    new DialogOptions { Size = DialogSize.Medium, Presentation = OverlayPresentation.Sheet });
                return drawn is byte[] { Length: > 0 } emoji ? PickedPhoto.FromJpeg(emoji, AvatarEditor.OutputSize) : null;
            case Camera:
            case Gallery:
                var file = source == Camera
                    ? await picker.CapturePhotoAsync(cancellationToken)
                    : await picker.PickImageAsync(cancellationToken);
                if (file is null) return null;
                var editable = await uploader.PrepareForEditingAsync(file, cancellationToken: cancellationToken);
                var framed = await overlays.EditAsync(editable);
                return framed is { Length: > 0 } jpeg ? PickedPhoto.FromJpeg(jpeg, AvatarEditor.OutputSize) : null;
            default:
                return null;
        }
    }
}
