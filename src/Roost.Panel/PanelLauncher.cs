using System.Diagnostics;
using System.Reflection;
using Roost.Core;

namespace Roost.WebPanel;

/// <summary>
/// Cómo se lanza el panel: <c>roost panel serve --port N</c>, un subcomando oculto de la CLI, desligado
/// como el daemon (en Windows con ShellExecute y la ventana oculta; en Linux el propio hijo hace
/// <c>setsid</c> y manda su salida a <c>logs/panel.log</c>). Así <c>roost panel up</c> vuelve en el acto y
/// el panel sigue al cerrar la terminal.
/// </summary>
public static class PanelLauncher
{
    public static ProcessStartInfo ForCurrentCli(int port)
    {
        var exe = Environment.ProcessPath ?? throw new RoostException("No se sabe la ruta de roost para lanzar el panel.");
        var args = $"panel serve --port {port}";
        // Con `dotnet roost.dll` el proceso es dotnet.exe: hay que pasarle la dll.
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? StartInfo(exe, $"\"{Assembly.GetEntryAssembly()!.Location}\" {args}")
            : StartInfo(exe, args);
    }

    private static ProcessStartInfo StartInfo(string exe, string args)
    {
        if (OperatingSystem.IsWindows())
        {
            // Sin heredar la consola ni sus handles: quien capture la salida de `roost panel up` no se queda
            // esperando a que muera el panel. El entorno (ROOST_HOME) sí se hereda.
            return new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
        }

        return new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
    }
}
