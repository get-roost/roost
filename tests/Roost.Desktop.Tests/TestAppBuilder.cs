using Avalonia;
using Avalonia.Headless;
using Roost.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Roost.Desktop.Tests;

/// <summary>La app de verdad (estilos, recursos, Inter) sobre la plataforma headless, dibujando con Skia.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
