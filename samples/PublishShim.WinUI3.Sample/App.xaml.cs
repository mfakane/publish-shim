using Microsoft.UI.Xaml;

namespace PublishShim.WinUI3.Sample;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            var markerPath = Environment.GetEnvironmentVariable("PUBLISH_SHIM_WINUI_SMOKE_MARKER");
            if (!string.IsNullOrWhiteSpace(markerPath))
            {
                File.WriteAllText($"{markerPath}.error", eventArgs.Exception.ToString());
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();

        var markerPath = Environment.GetEnvironmentVariable("PUBLISH_SHIM_WINUI_SMOKE_MARKER");
        if (!string.IsNullOrWhiteSpace(markerPath))
        {
            File.WriteAllText(markerPath, "Windows App SDK initialized and the WinUI window was created.");
            Environment.Exit(0);
        }
    }
}
