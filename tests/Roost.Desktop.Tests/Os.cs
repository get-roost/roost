namespace Roost.Desktop.Tests;

/// <summary>
/// Para los tests de vistas de un solo sistema (xUnit v3): <c>[AvaloniaFact(Skip = "Solo en Windows.",
/// SkipUnless = nameof(Os.IsWindows), SkipType = typeof(Os))]</c>. Así la misma suite corre en los dos runners.
/// </summary>
public static class Os
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsLinux => OperatingSystem.IsLinux();
}
