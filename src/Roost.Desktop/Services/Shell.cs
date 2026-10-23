using System.Diagnostics;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Stack;

namespace Roost.Desktop.Services;

/// <summary>Abrir cosas fuera de la app: el navegador, el explorador, una terminal.</summary>
public static class Shell
{
    public static void OpenUrl(string url) => Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Abre la carpeta en el explorador (creándola si aún no existe, como C:\www el primer día).</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Abre el fichero con su programa asociado (un log, roost.toml).</summary>
    public static void OpenFile(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Selecciona el fichero en el explorador.</summary>
    public static void RevealFile(string path)
    {
        if (OperatingSystem.IsWindows())
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        else
            OpenFolder(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Una terminal en la carpeta www con el PHP global (y el cliente de la base de datos
    /// global, si la hay) delante en el PATH: lo que en Laragon es «Terminal».
    /// </summary>
    /// <param name="phpDirectory">Otro PHP en vez del global (el botón Terminal de la pantalla PHP).</param>
    /// <param name="workingDirectory">Otra carpeta en vez de www (la de un sitio).</param>
    public static void OpenTerminal(RoostPaths paths, string? phpDirectory = null, string? workingDirectory = null)
    {
        var extra = new List<string>();
        var environment = new Dictionary<string, string>();
        var shims = new ShimInstaller(paths);
        if (shims.IsInstalled)
        {
            // Con los shims, php sigue la carpeta; un PHP concreto se pide por la variable.
            extra.Add(shims.Directory);
            if (phpDirectory is not null) environment[TerminalPhp.VersionVariable] = Path.GetFileName(phpDirectory);
        }
        else if (phpDirectory is not null)
        {
            extra.Add(phpDirectory);
        }
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            if (extra.Count == 0) extra.Add(planner.ResolvePhp(config, new PhpInstaller(paths).ListInstalled()).Directory);
            if (planner.ResolveDb(config, new DbInstaller(paths).ListInstalled()) is { } db)
                extra.Add(Path.Combine(db.Directory, "bin"));
        }
        catch (RoostException)
        {
            // Sin PHP instalado (o con roost.toml roto) la terminal se abre igual, sin nada extra.
        }

        var folder = workingDirectory ?? paths.Www;
        Directory.CreateDirectory(folder);
        var shell = OperatingSystem.IsWindows() ? FindOnPath("pwsh.exe") ?? "powershell.exe" : "x-terminal-emulator";
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        if (OperatingSystem.IsWindows()) start.ArgumentList.Add("-NoExit");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["PATH"] = string.Join(Path.PathSeparator, extra.Append(path));
        foreach (var (name, value) in environment) start.Environment[name] = value;
        Start(start);
    }

    /// <summary>Un programa de consola en su propia ventana (la consola SQL de la base de datos).</summary>
    public static void OpenConsole(string exe, string arguments, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        // Desde una app sin consola, Windows le da al hijo una ventana de consola propia.
        Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = workingDirectory });
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .FirstOrDefault(File.Exists);

    private static void Start(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new RoostException($"No se pudo abrir {start.FileName}: {ex.Message}");
        }
    }
}
