using Spectre.Console;

namespace Roost.Cli.Commands;

/// <summary>
/// Lo que de momento solo funciona en Windows, con el mismo mensaje en todos los comandos: así en
/// Linux y macOS no se contradicen («arrancalo con roost up» y luego «up solo en Windows»).
/// </summary>
internal static class WindowsOnly
{
    /// <summary>true si se puede seguir; si no, explica qué hay hoy fuera de Windows y devuelve false.</summary>
    public static bool Check(string command)
    {
        if (OperatingSystem.IsWindows()) return true;
        AnsiConsole.MarkupLineInterpolated($"[yellow]roost {command} solo funciona en Windows por ahora.[/]");
        AnsiConsole.MarkupLine("[grey]En Linux y macOS, Roost hoy es roost scan y roost audit; el stack (PHP, servidor web, base de datos y sitios) llegara despues.[/]");
        return false;
    }
}
