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

    /// <summary>
    /// PURO (salvo <paramref name="exists"/>): lo mismo en el PATH de esta terminal (el del proceso).
    /// Ahí sale también lo que pone cada shell al abrirse y que no está en el registro: <c>fnm env</c>
    /// en el perfil de PowerShell, un <c>nvm use</c> de esa sesión. null si gana el shim.
    /// </summary>
    public static ShimShadow? FindInSession(string tool, string shimDirectory, string? processPath, Func<string, bool> exists) =>
        Find(tool, shimDirectory, machinePath: null, userPath: processPath, exists);

    /// <summary>
    /// PURO: de quién es un <c>node</c>, <c>npm</c> o <c>npx</c> que le gana al shim, para decirlo por su
    /// nombre: nvm-windows (su <c>NVM_SYMLINK</c> o su <c>NVM_HOME</c>, que por defecto es
    /// <c>C:\Program Files\nodejs</c>, la misma carpeta que el instalador), Volta, fnm, el instalador de
    /// nodejs.org, Scoop, Chocolatey, Laragon, Herd o nvs. null si no se reconoce.
    /// </summary>
    public static string? NodeOwner(string path, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var dir = System.IO.Path.GetDirectoryName(path) ?? path;
        bool Under(string? root) => root is { Length: > 0 } r
            && (Same(dir, Environment.ExpandEnvironmentVariables(r)) || dir.StartsWith(Environment.ExpandEnvironmentVariables(r).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        bool Has(string part) => dir.Contains(part, StringComparison.OrdinalIgnoreCase);

        if (Under(env("NVM_SYMLINK")) || Under(env("NVM_HOME")) || Has(@"\nvm\") || dir.EndsWith(@"\nvm", StringComparison.OrdinalIgnoreCase))
            return Has(@"\.config\herd\") ? "el nvm de Herd" : "nvm-windows";
        if (Has(@"\Volta\") || dir.EndsWith(@"\Volta", StringComparison.OrdinalIgnoreCase)) return "Volta";
        if (Has("fnm_multishells") || Has(@"\fnm\")) return "fnm";
        if (Has(@"\.config\herd\")) return "Herd";
        if (Has(@"\scoop\")) return "Scoop";
        if (Has(@"\chocolatey\")) return "Chocolatey";
        if (Has(@"\laragon\")) return "Laragon";
        if (Has(@"\nvs\")) return "nvs";
        if (dir.EndsWith(@"\nodejs", StringComparison.OrdinalIgnoreCase) && (Has(@"\Program Files\") || Has(@"\Program Files (x86)\")))
            return "el instalador de Node (nodejs.org)";
        return null;
    }

    private static IEnumerable<(string Dir, bool System)> Entries(string? path, bool system) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => (Environment.ExpandEnvironmentVariables(entry.Trim('"')), system))
            .Where(x => x.Item1.Length > 0);

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
