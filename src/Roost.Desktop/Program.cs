using Avalonia;
using Roost.Desktop.Services;

namespace Roost.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Una sola app por usuario: si ya hay una, se le pide que se enseñe y esta sale.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return 0;
        App.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
