using Roost.Core.Platform;

namespace Roost.Core.Shims;

/// <summary>Otro ejecutable con el nombre de un shim que Windows encuentra antes que él.</summary>
/// <param name="InSystemPath">true si está en el PATH del sistema (quitarlo de ahí pide ser administrador).</param>
/// <param name="ShimsInPath">false si la carpeta de los shims ni siquiera está en el PATH (entonces gana cualquiera).</param>
public sealed record ShimShadow(string Tool, string Path, bool InSystemPath, bool ShimsInPath = true);

/// <summary>
/// Quién le gana a un shim en el PATH. Windows junta el PATH del sistema y después el del usuario,
/// y en cada carpeta prueba las extensiones de <c>PATHEXT</c>: un <c>composer.bat</c> (ComposerSetup,
/// Laragon) o un <c>php.exe</c> (XAMPP) en el PATH del sistema gana siempre al shim, que está en el
/// del usuario. Roost no lo puede cambiar sin ser administrador: lo dice.
/// </summary>
public static class ShimShadowing
{
    /// <summary>Lo que prueban cmd y PowerShell en cada carpeta, en el orden del <c>PATHEXT</c> de serie.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".com", ".exe", ".bat", ".cmd"];

    /// <summary>
    /// PURO (salvo <paramref name="exists"/>): el primer <paramref name="tool"/> que se encontraría
    /// antes que el de <paramref name="shimDirectory"/>, recorriendo el PATH del sistema y después el
    /// del usuario hasta llegar a la carpeta de los shims. null si nada le gana.
    /// </summary>
    public static ShimShadow? Find(string tool, string shimDirectory, string? machinePath, string? userPath, Func<string, bool> exists)
    {
        var entries = Entries(machinePath, system: true).Concat(Entries(userPath, system: false)).ToList();
        var shimsInPath = entries.Any(e => Same(e.Dir, shimDirectory));
        foreach (var (dir, system) in entries)
        {
            if (Same(dir, shimDirectory)) return null;   // de aquí en adelante, gana el shim
            foreach (var extension in Extensions)
            {
                var candidate = System.IO.Path.Combine(dir, tool + extension);
                if (exists(candidate)) return new ShimShadow(tool, candidate, system, shimsInPath);
            }
        }
        return null;
    }

    /// <summary>Lo mismo con los PATH de verdad de esta máquina (solo lee: el registro y el disco).</summary>
    public static ShimShadow? Find(string tool, string shimDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? machine;
        try { machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine); }
        catch (System.Security.SecurityException) { machine = null; }
        return Find(tool, shimDirectory, machine, UserPath.Read(), File.Exists);
    }

    private static IEnumerable<(string Dir, bool System)> Entries(string? path, bool system) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => (Environment.ExpandEnvironmentVariables(entry.Trim('"')), system))
            .Where(x => x.Item1.Length > 0);

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
