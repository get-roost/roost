using Roost.Core.Platform;

namespace Roost.Core.Shims;

/// <summary>Otro ejecutable con el nombre de un shim que la terminal encuentra antes que él.</summary>
/// <param name="InSystemPath">Windows: true si está en el PATH del sistema (quitarlo de ahí pide ser administrador). Linux: true si es una carpeta de la distro (<c>/usr/bin</c>, <c>/snap/bin</c>…), que el perfil de la shell pone antes que <c>~/.local/bin</c>.</param>
/// <param name="ShimsInPath">false si la carpeta de los shims ni siquiera está en el PATH (entonces gana cualquiera).</param>
public sealed record ShimShadow(string Tool, string Path, bool InSystemPath, bool ShimsInPath = true);

/// <summary>
/// Quién le gana a un shim en el PATH. Windows junta el PATH del sistema y después el del usuario,
/// y en cada carpeta prueba las extensiones de <c>PATHEXT</c>: un <c>composer.bat</c> (ComposerSetup,
/// Laragon) o un <c>php.exe</c> (XAMPP) en el PATH del sistema gana siempre al shim, que está en el
/// del usuario. Roost no lo puede cambiar sin ser administrador: lo dice.
/// <para>
/// En Linux y macOS el PATH es uno solo (lo arma la shell con sus perfiles) y los shims llegan por los
/// enlaces de <c>~/.local/bin</c> (<see cref="UnixPath"/>): gana lo que esté en una carpeta anterior, sin
/// extensiones. Lo típico es el <c>node</c> de nvm, que <c>~/.bashrc</c> pone el primero en cada terminal, o
/// el <c>php</c> de la distro si <c>~/.local/bin</c> no está en el PATH.
/// </para>
/// </summary>
public static class ShimShadowing
{
    /// <summary>Las carpetas de la distro en Linux: lo que hay ahí lo puso un paquete, no el usuario.</summary>
    public static readonly IReadOnlyList<string> UnixSystemDirs = ["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin", "/usr/local/sbin", "/snap/bin"];
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
                // Con \ a mano (no Path.Combine): es la forma de Windows aunque el test corra en Linux.
                var candidate = dir.TrimEnd('\\') + "\\" + tool + extension;
                if (exists(candidate)) return new ShimShadow(tool, candidate, system, shimsInPath);
            }
        }
        return null;
    }

    /// <summary>
    /// Lo mismo con los PATH de verdad de esta máquina (solo lee: el registro y el disco). En Linux y
    /// macOS, con el PATH de este proceso (el de la terminal) hasta <paramref name="shimDirectory"/>, que
    /// ahí es la carpeta de los enlaces (<c>ShimInstaller.PathDirectory</c>).
    /// </summary>
    public static ShimShadow? Find(string tool, string shimDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return FindUnix(tool, shimDirectory, Environment.GetEnvironmentVariable("PATH"), File.Exists);
        string? machine;
        try { machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine); }
        catch (System.Security.SecurityException) { machine = null; }
        return Find(tool, shimDirectory, machine, UserPath.Read(), File.Exists);
    }

    /// <summary>
    /// PURO (salvo <paramref name="exists"/>): en un PATH de Unix (separado por <c>:</c>), el primer
    /// <paramref name="tool"/> que se encontraría antes de llegar a <paramref name="linkDirectory"/>
    /// (<c>~/.local/bin</c>). null si gana el shim. <c>InSystemPath</c> dice si lo que gana está en una
    /// carpeta de la distro (<see cref="UnixSystemDirs"/>).
    /// </summary>
    public static ShimShadow? FindUnix(string tool, string linkDirectory, string? path, Func<string, bool> exists)
    {
        var entries = UnixEntries(path).ToList();
        var shimsInPath = entries.Any(e => SameUnix(e, linkDirectory));
        foreach (var dir in entries)
        {
            if (SameUnix(dir, linkDirectory)) return null;
            var candidate = dir.TrimEnd('/') + "/" + tool;
            if (exists(candidate)) return new ShimShadow(tool, candidate, UnixSystemDirs.Any(s => SameUnix(s, dir)), shimsInPath);
        }
        return null;
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
        if (path.Contains('/') && !path.Contains('\\')) return UnixOwner(path);
        env ??= Environment.GetEnvironmentVariable;
        // La carpeta a mano (no Path.GetDirectoryName): en Linux no entiende las rutas de Windows, y esto es puro.
        var dir = path.LastIndexOf('\\') is var cut and > 0 ? path[..cut] : path;
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

    /// <summary>
    /// PURO: de quién es un ejecutable de Linux o macOS que le gana al shim (<c>node</c>, pero también
    /// <c>php</c> o <c>composer</c>): nvm, fnm, Volta, asdf, mise, nodenv, n, phpenv, Homebrew, snap, la
    /// distro (<c>/usr/bin</c>), algo compilado o copiado a mano en <c>/usr/local</c>, Herd, o un programa de
    /// Windows por el interop de WSL (<c>/mnt/c/…</c>). null si no se reconoce.
    /// </summary>
    public static string? UnixOwner(string path)
    {
        var dir = (path.LastIndexOf('/') is var cut and > 0 ? path[..cut] : "/") + "/";
        bool Has(string part) => dir.Contains(part, StringComparison.Ordinal);
        bool Starts(string prefix) => dir.StartsWith(prefix, StringComparison.Ordinal);

        if (Has("/.nvm/")) return "nvm";
        if (Has("/fnm_multishells/") || Has("/fnm/")) return "fnm";
        if (Has("/.volta/")) return "Volta";
        if (Has("/.asdf/")) return "asdf";
        if (Has("/mise/") && (Has("/.local/share/") || Has("/.config/"))) return "mise";
        if (Has("/.nodenv/")) return "nodenv";
        if (Has("/.phpenv/")) return "phpenv";
        if (Has("/n/versions/") || Has("/.n/")) return "n";
        if (Has("/.config/herd")) return "Herd";
        if (Starts("/home/linuxbrew/") || Has("/.linuxbrew/") || Starts("/opt/homebrew/")) return "Homebrew";
        if (Starts("/snap/") || Has("/snap/bin/")) return "snap";
        if (Starts("/mnt/c/") || Starts("/mnt/d/")) return "un programa de Windows, por el interop de WSL";
        if (Starts("/usr/local/")) return "algo instalado a mano en /usr/local";
        if (Starts("/usr/bin/") || Starts("/bin/") || Starts("/usr/sbin/") || Starts("/sbin/")) return "la distro (su paquete)";
        if (Starts("/opt/")) return "algo instalado en /opt";
        return null;
    }

    private static IEnumerable<string> UnixEntries(string? path) =>
        (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0);

    private static bool SameUnix(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    private static IEnumerable<(string Dir, bool System)> Entries(string? path, bool system) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => (Environment.ExpandEnvironmentVariables(entry.Trim('"')), system))
            .Where(x => x.Item1.Length > 0);

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
