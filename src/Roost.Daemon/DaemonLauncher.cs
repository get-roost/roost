using System.Diagnostics;
using System.Reflection;
using Roost.Core;

namespace Roost.Daemon;

/// <summary>
/// Cómo se lanza roostd: <c>roost.exe daemon</c>, el subcomando oculto de la CLI. Lo usan la
/// CLI (con su propio ejecutable) y la app de escritorio (con el <c>roost.exe</c> que lleva al lado).
/// </summary>
public static class DaemonLauncher
{
    /// <summary>El daemon con el ejecutable de la CLI que está en marcha.</summary>
    public static ProcessStartInfo ForCurrentCli()
    {
        var exe = Environment.ProcessPath ?? throw new RoostException("No se sabe la ruta de roost para lanzar el daemon.");
        // Con `dotnet roost.dll` el proceso es dotnet.exe: hay que pasarle la dll.
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? StartInfo(exe, $"\"{Assembly.GetEntryAssembly()!.Location}\" daemon")
            : StartInfo(exe, "daemon");
    }

    /// <summary>El daemon con el <c>roost.exe</c> que hay en <paramref name="directory"/>.</summary>
    public static ProcessStartInfo FromDirectory(string directory)
    {
        var exe = Path.Combine(directory, OperatingSystem.IsWindows() ? "roost.exe" : "roost");
        if (!File.Exists(exe))
            throw new RoostException($"No se encuentra {exe}, que es el que hace de daemon.");
        return StartInfo(exe, "daemon");
    }

    private static ProcessStartInfo StartInfo(string exe, string args)
    {
        // El daemon NO puede quedarse con el stdout/stderr de quien lo lanza: quien capture la salida
        // de `roost up` (un script, una CI) esperaría al cierre de esa tubería, es decir, a que
        // muera el daemon. En Windows, ShellExecute con la ventana oculta lo lanza sin heredar
        // handles (el entorno, con ROOST_HOME, sí se hereda).
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
        }

        // En Linux y macOS es el propio hijo quien se desliga nada más arrancar (DaemonHost): su propia
        // sesión (setsid), SIGHUP ignorado y stdout/stderr a logs/roostd.log, con lo que suelta lo que
        // heredó. Aquí no se le dejan tuberías que nadie leería (y que, muerto el padre, darían EPIPE).
        return new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
    }
}
