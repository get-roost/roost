using Spectre.Console;

namespace Roost.Cli.Commands;

/// <summary>
/// La puerta de los comandos que aún no funcionan en todos los sistemas, con el mismo mensaje en todos:
/// así en Linux y macOS no se contradicen («arrancalo con roost up» y luego «up solo en Windows»).
/// Cuando un comando funciona ya en Linux, se quita su llamada (en el comando, no aquí).
/// </summary>
internal static class PlatformGate
{
    /// <summary>true si se puede seguir; si no, explica qué hay hoy fuera de Windows y devuelve false.</summary>
    public static bool Require(string command)
    {
        if (OperatingSystem.IsWindows()) return true;
        AnsiConsole.MarkupLineInterpolated($"[yellow]roost {command} solo funciona en Windows por ahora.[/]");
        AnsiConsole.MarkupLine(OperatingSystem.IsLinux()
            ? "[grey]En Linux ya van roost php, composer, up, down, status, site, park, cert, dns y www (con nginx); la base de datos, Apache, Redis, Mailpit, compartir, git, Node, los shims y la app llegan en la proxima version.[/]"
            : "[grey]En macOS, Roost hoy es roost scan y roost audit; el stack (PHP, servidor web, base de datos y sitios) llegara despues.[/]");
        return false;
    }

    /// <summary>
    /// Una sugerencia que nombra un comando que hoy solo funciona en Windows («Prueba: roost php install
    /// 8.4»): tal cual en Windows y vacía en el resto, para no proponer lo que no funciona. Cuando el
    /// comando funciona ya en Linux, se quita la llamada y queda el texto.
    /// </summary>
    public static string WindowsHint(string hint) => OperatingSystem.IsWindows() ? hint : "";
}
