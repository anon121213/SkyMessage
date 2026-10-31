namespace SkyMessage.Client.UI.Features.Settings;

/// <summary>
/// One third-party component shipped in some SkyMessage build. The full text is either a static file shipped with
/// the UI (<see cref="TextUrl"/>, relative to the page) or embedded here (<see cref="Text"/>); <see cref="Link"/>
/// points to terms that cannot be shipped as a file (Google Play services).
/// </summary>
public sealed record LicenseInfo(
    string Name,
    string License,
    string Copyright,
    string? TextUrl = null,
    string? Text = null,
    string? Link = null,
    string? ExtraUrl = null,
    string? NoteKey = null);

/// <summary>Everything third-party that ships in the apps (About › Licenses). Keep in sync with the client packages.</summary>
public static class LicenseCatalog
{
    private const string Static = "_content/SkyMessage.Client.UI/";

    public static IReadOnlyList<LicenseInfo> All { get; } =
    [
        new(".NET, ASP.NET Core Blazor, SignalR", "MIT", "© .NET Foundation and Contributors", Text: Mit("Copyright (c) .NET Foundation and Contributors")),
        new(".NET MAUI", "MIT", "© .NET Foundation and Contributors", Text: Mit("Copyright (c) .NET Foundation and Contributors"), NoteKey: "settings.licenses.apps"),
        new("livekit-client 2.22.4", "Apache-2.0", "© LiveKit, Inc.", TextUrl: Static + "lib/livekit/LICENSE",
            ExtraUrl: Static + "lib/livekit/THIRD-PARTY-NOTICES.txt", NoteKey: "settings.licenses.livekit"),
        new("Lucide (lucide-static 1.57.0)", "ISC", "© Lucide Icons and Contributors; parts © Cole Bemis (Feather, MIT)", TextUrl: Static + "licenses/lucide.txt",
            NoteKey: "settings.licenses.icons"),
        new("Rubik 1.100, Montserrat 8.000 (fonts)", "OFL-1.1", "© Hubert & Fischer, Meir Sadan; © The Montserrat Project Authors",
            TextUrl: Static + "licenses/fonts.txt"),
        new("QRCoder 1.8.1", "MIT", "© Raffael Herrmann, Shane Krueger", TextUrl: Static + "licenses/qrcoder.txt"),
        new("AndroidX (Media3, Fragment, WebKit, ExifInterface, Lifecycle), Kotlin", "Apache-2.0", "© The Android Open Source Project; © JetBrains s.r.o.",
            TextUrl: Static + "lib/livekit/LICENSE", NoteKey: "settings.licenses.android"),
        new(".NET for Android bindings (Xamarin.AndroidX, Xamarin.GooglePlayServices)", "MIT", "© .NET Foundation and Contributors",
            Text: Mit("Copyright (c) .NET Foundation and Contributors"), NoteKey: "settings.licenses.android"),
        new("Google Play services (Phone Number Hint)", "Android SDK License", "© Google LLC", Link: "https://developer.android.com/studio/terms",
            NoteKey: "settings.licenses.playServices"),
    ];

    private static string Mit(string copyright) =>
        $"""
        MIT License

        {copyright}

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;
}
