using Microsoft.Extensions.DependencyInjection;

namespace SkyMessage.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        // The keyboard must not pan the window: its height reaches the page as --sm-kb-inset.
        Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.Application.SetWindowSoftInputModeAdjust(
            this, Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.WindowSoftInputModeAdjust.Resize);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<MainPage>()) { Title = "SkyMessage" };
#if MACCATALYST
        window.MinimumWidth = 380;
        window.MinimumHeight = 560;
        window.Width = 1180;
        window.Height = 780;
#endif
        return window;
    }
}
