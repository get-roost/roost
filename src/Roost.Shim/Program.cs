using System.Diagnostics;
using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Shims;

namespace Roost.Shim;

/// <summary>
/// El shim: elige el PHP de esta carpeta y lo ejecuta con los argumentos tal cual llegaron,
/// heredando la consola, y devuelve su código de salida. Ctrl+C le llega al PHP (comparten
/// consola); el shim solo espera a que acabe.
/// </summary>
internal static class Program
{
    public static int Main()
    {
        var self = Environment.ProcessPath ?? "php";
        var tool = Path.GetFileNameWithoutExtension(self).ToLowerInvariant();
        try
        {
            var paths = PathsFor(self);
            var choice = TerminalPhp.Resolve(paths, Environment.CurrentDirectory, Environment.GetEnvironmentVariable(TerminalPhp.VersionVariable));
            var target = tool == "php-cgi" ? choice.Php.CgiExecutable : choice.Php.Executable;

            var start = new ProcessStartInfo(target, TerminalPhp.ArgumentsAfterProgram(Environment.CommandLine)) { UseShellExecute = false };
            foreach (var (name, value) in TerminalPhp.Environment(paths, choice.Php))
                start.Environment[name] = value;

            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            using var php = Process.Start(start) ?? throw new RoostException($"No se pudo lanzar {target}.");
            php.WaitForExit();
            return php.ExitCode;
        }
        catch (RoostException ex)
        {
            Console.Error.WriteLine($"roost: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"roost: no se pudo ejecutar PHP: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Las rutas de Roost: si el shim está en <c>&lt;raíz&gt;/bin/shims</c>, las de esa raíz (así un
    /// Roost de pruebas con ROOST_HOME tiene sus propios shims); si no, las de siempre.
    /// </summary>
    private static RoostPaths PathsFor(string self)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RoostPaths.HomeVariable))) return RoostPaths.Default();
        var shims = new DirectoryInfo(Path.GetDirectoryName(self)!);
        return shims is { Name: "shims", Parent: { Name: "bin", Parent: { } root } } ? RoostPaths.ForRoot(root.FullName) : RoostPaths.Default();
    }
}
