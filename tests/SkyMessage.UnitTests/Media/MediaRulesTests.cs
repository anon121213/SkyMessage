using Shouldly;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Media;

public sealed class MediaContentTypesTests
{
    [Theory]
    [InlineData("image/JPEG", "image/jpeg")]
    [InlineData("image/jpg", "image/jpeg")]
    [InlineData(" audio/webm;codecs=opus ", "audio/webm")]
    [InlineData("video/mp4; codecs=\"avc1.42E01E\"", "video/mp4")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public void Normalizes(string input, string expected) => MediaContentTypes.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jpeg")]
    [InlineData("image/")]
    [InlineData("image/<script>")]
    [InlineData("a b/c")]
    public void Rejects_malformed(string? input) => MediaContentTypes.Normalize(input).ShouldBeNull();

    [Theory]
    [InlineData(MediaKind.Photo, "image/jpeg", true)]
    [InlineData(MediaKind.Photo, "image/png", true)]
    [InlineData(MediaKind.Photo, "image/webp", true)]
    [InlineData(MediaKind.Photo, "image/gif", true)]
    [InlineData(MediaKind.Photo, "image/heic", true)]
    [InlineData(MediaKind.Photo, "image/heif", true)]
    [InlineData(MediaKind.Photo, "image/svg+xml", false)]
    [InlineData(MediaKind.Photo, "image/bmp", false)]
    [InlineData(MediaKind.Video, "video/quicktime", true)]
    [InlineData(MediaKind.Video, "audio/mp4", false)]
    [InlineData(MediaKind.VideoNote, "video/webm", true)]
    [InlineData(MediaKind.Voice, "audio/ogg", true)]
    [InlineData(MediaKind.Voice, "video/webm", true)]
    [InlineData(MediaKind.Voice, "video/mp4", false)]
    [InlineData(MediaKind.Audio, "audio/mpeg", true)]
    [InlineData(MediaKind.Audio, "video/webm", false)]
    [InlineData(MediaKind.File, "text/html", true)]
    [InlineData(MediaKind.File, "application/x-msdownload", true)]
    public void Accepts_content_types_per_kind(MediaKind kind, string contentType, bool accepted) =>
        (MediaContentTypes.ResolveForUpload(kind, contentType) is not null).ShouldBe(accepted);

    [Fact]
    public void Files_accept_anything()
    {
        MediaContentTypes.ResolveForUpload(MediaKind.File, "garbage").ShouldBe(MediaContentTypes.OctetStream);
        MediaContentTypes.ResolveForUpload(MediaKind.File, null).ShouldBe(MediaContentTypes.OctetStream);
        MediaContentTypes.ResolveForUpload(MediaKind.Photo, null).ShouldBeNull();
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("text/xml", true)]
    [InlineData("application/xml", true)]
    [InlineData("application/rss+xml", true)]
    [InlineData("text/javascript", true)]
    [InlineData("application/javascript", true)]
    [InlineData("application/ecmascript", true)]
    [InlineData("multipart/x-mixed-replace", true)]
    [InlineData("TEXT/HTML; charset=utf-8", true)]
    [InlineData("image/jpeg", false)]
    [InlineData("video/mp4", false)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    public void Flags_active_document_types(string contentType, bool dangerous) =>
        MediaContentTypes.IsDangerous(contentType).ShouldBe(dangerous);

    [Theory]
    [InlineData("image/jpeg", ".jpg", true)]
    [InlineData("image/jpeg", ".JPEG", true)]
    [InlineData("image/jpeg", ".png", false)]
    [InlineData("image/png", ".png", true)]
    [InlineData("image/png", "", false)]
    [InlineData("audio/mp4", ".m4a", true)]
    [InlineData("application/x-unknown", ".bin", false)]
    public void Knows_the_usual_extensions_of_a_type(string contentType, string extension, bool expected) =>
        MediaContentTypes.IsExtensionOf(contentType, extension).ShouldBe(expected);
}

public sealed class MediaFileNamesTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("C:\\Users\\me\\Desktop\\report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("  spaced name.txt  ", "spaced name.txt")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("tab\there\u0000.txt", "tabhere.txt")]
    [InlineData("invoice\u202Etxt.exe", "invoicetxt.exe")]
    [InlineData("Фото отпуска.jpg", "Фото отпуска.jpg")]
    [InlineData(".bashrc", ".bashrc")]
    public void Sanitizes(string input, string expected) =>
        MediaFileNames.Sanitize(input, MediaKind.File, "application/octet-stream").ShouldBe(expected);

    [Theory]
    [InlineData(null, MediaKind.Photo, "image/jpeg", "photo.jpg")]
    [InlineData("", MediaKind.Voice, "audio/webm", "voice.webm")]
    [InlineData("...", MediaKind.Video, "video/mp4", "video.mp4")]
    [InlineData("/", MediaKind.File, "application/x-unknown", "file")]
    [InlineData("\u202E", MediaKind.VideoNote, "video/mp4", "video_note.mp4")]
    public void Falls_back_to_a_default_name(string? input, MediaKind kind, string contentType, string expected) =>
        MediaFileNames.Sanitize(input, kind, contentType).ShouldBe(expected);

    [Fact]
    public void Long_names_are_truncated_keeping_the_extension()
    {
        var name = new string('x', 400) + ".docx";

        var sanitized = MediaFileNames.Sanitize(name, MediaKind.File, "application/octet-stream");

        sanitized.Length.ShouldBe(Limits.FileNameMaxLength);
        sanitized.ShouldEndWith(".docx");
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        // The 255-character cut falls between the two halves of the first emoji.
        var name = new string('a', 254) + "😀😀😀";

        var sanitized = MediaFileNames.Sanitize(name, MediaKind.File, null);

        sanitized.ShouldBe(new string('a', 254));
        char.IsHighSurrogate(sanitized[^1]).ShouldBeFalse();
    }

    [Theory]
    [InlineData("IMG_0001.HEIC", ".jpg", "IMG_0001.jpg")]
    [InlineData("clip.webm", ".mp4", "clip.mp4")]
    [InlineData("voice", ".m4a", "voice.m4a")]
    [InlineData("archive.tar.gz", ".zip", "archive.tar.zip")]
    [InlineData(".hidden", ".jpg", ".hidden.jpg")]
    [InlineData("Recording 2026-05-12 10.15.33", ".m4a", "Recording 2026-05-12 10.15.33.m4a")]
    [InlineData("v1.2 final", ".mp4", "v1.2 final.mp4")]
    [InlineData("clip.MOV", ".mp4", "clip.mp4")]
    [InlineData("track.mp3", ".m4a", "track.m4a")]
    public void Replaces_extensions(string input, string extension, string expected) =>
        MediaFileNames.WithExtension(input, extension).ShouldBe(expected);

    [Theory]
    [InlineData("IMG_0001.HEIC", "image/jpeg", "IMG_0001.jpg")]
    [InlineData("mislabeled.jpg", "image/png", "mislabeled.png")]
    [InlineData("sticker.png", "image/webp", "sticker.webp")]
    [InlineData("Screenshot", "image/png", "Screenshot.png")]
    [InlineData("Screenshot 12.05.2026", "image/png", "Screenshot 12.05.2026.png")]
    [InlineData("v1.2 final", "image/gif", "v1.2 final.gif")]
    [InlineData("photo.jpg", "image/jpeg", "photo.jpg")]
    [InlineData("photo.JPG", "image/jpeg", "photo.JPG")]
    [InlineData("photo.jpeg", "image/jpeg", "photo.jpeg")]
    [InlineData("scan.jfif", "image/jpeg", "scan.jfif")]
    [InlineData("anim.GIF", "image/gif", "anim.GIF")]
    [InlineData(".png", "image/png", ".png")]
    [InlineData("data.bin", "application/x-unknown", "data.bin")]
    public void Extension_is_made_to_match_the_content_type(string input, string contentType, string expected) =>
        MediaFileNames.WithExtensionFor(input, contentType).ShouldBe(expected);
}
