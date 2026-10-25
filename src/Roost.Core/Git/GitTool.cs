using Roost.Core.Config;
using Roost.Core.Platform;

namespace Roost.Core.Git;

/// <summary>De dónde sale el git que usa Roost.</summary>
public enum GitSource
{
    /// <summary>El MinGit que instala Roost en <c>bin/git</c>.</summary>
    Roost,

    /// <summary>El que dice <c>[git] exe</c> en roost.toml.</summary>
    Configured,

    /// <summary>El git del sistema, el primero del PATH (Linux y macOS: Roost no instala el suyo).</summary>
    System,
}

/// <summary>El git que usa Roost: su ejecutable, de dónde sale y, si es el de Roost, su versión.</summary>
public sealed record GitLocation(string Executable, GitSource Source, string? Version);

/// <summary>
/// Qué git usa Roost (ROOST.md: git al estilo Plesk). El de <c>[git] exe</c> si está configurado;
/// si no, en Windows el MinGit de <c>bin/git</c>, que se instala la primera vez que hace falta y no al
/// instalar Roost; en Linux y macOS, el git del sistema (el primero del PATH), que lo pone la distro.
/// </summary>
public static class GitTool
{
    /// <summary>
    /// El git que toca, o null si no hay ninguno (ni configurado ni instalado). Lanza
    /// <see cref="RoostException"/> si el configurado no existe: eso es un error del usuario, no un
    /// «ya usaré el de Roost».
    /// </summary>
    public static GitLocation? Locate(RoostPaths paths, RoostConfig config)
    {
        if (config.GitExe is { } exe)
        {
            if (!File.Exists(exe))
                throw new RoostException($"El git de roost.toml ([git] exe = \"{exe}\") no existe. Arregla la ruta o quita la clave para usar el git de {(OperatingSystem.IsWindows() ? "Roost" : "tu sistema")}.");
            return new GitLocation(Path.GetFullPath(exe), GitSource.Configured, null);
        }

        if (!OperatingSystem.IsWindows())
            return FindSystemGit() is { } system ? new GitLocation(system, GitSource.System, null) : null;

        var installed = new GitInstaller(paths).Find(GitCatalog.Current);
        return installed is null ? null : new GitLocation(installed.Executable, GitSource.Roost, installed.Version);
    }

    /// <summary>El <c>git</c> del PATH de este proceso (Linux y macOS), o null.</summary>
    public static string? FindSystemGit() => PathProbe.ResolveActive(["git"]);

    /// <summary>
    /// Cómo instalar git en esta distro, para decirlo cuando falta: <c>sudo apt install git</c> en
    /// Debian y Ubuntu, <c>dnf</c> en Fedora, <c>pacman</c> en Arch, <c>zypper</c> en openSUSE, <c>apk</c> en
    /// Alpine. PURO sobre el <c>ID</c> y el <c>ID_LIKE</c> de <paramref name="osRelease"/> (<c>/etc/os-release</c>).
    /// </summary>
    public static string SystemInstallHint(string? osRelease)
    {
        var ids = (osRelease ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("ID=", StringComparison.Ordinal) || l.StartsWith("ID_LIKE=", StringComparison.Ordinal))
            .SelectMany(l => l[(l.IndexOf('=') + 1)..].Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(id => id.ToLowerInvariant())
            .ToList();
        if (ids.Any(id => id is "fedora" or "rhel" or "centos" or "rocky" or "almalinux")) return "sudo dnf install git";
        if (ids.Any(id => id is "arch" or "manjaro")) return "sudo pacman -S git";
        if (ids.Any(id => id is "opensuse" or "suse" or "opensuse-leap" or "opensuse-tumbleweed")) return "sudo zypper install git";
        if (ids.Any(id => id is "alpine")) return "sudo apk add git";
        return "sudo apt install git";
    }

    /// <summary>El texto de «no hay git» fuera de Windows: Roost no lo instala; lo pone la distro.</summary>
    public static string MissingSystemGitMessage()
    {
        string? osRelease = null;
        try { if (File.Exists("/etc/os-release")) osRelease = File.ReadAllText("/etc/os-release"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return $"No hay git en el PATH. En Linux Roost usa el git del sistema: instalalo ({SystemInstallHint(osRelease)}) o indica uno en roost.toml ([git] exe = \"/ruta/a/git\").";
    }

    /// <summary>
    /// Como <see cref="Locate"/>, pero si no hay ninguno, en Windows instala el MinGit de Roost; fuera
    /// de Windows lanza <see cref="RoostException"/> con cómo instalar el de la distro.
    /// </summary>
    public static async Task<GitLocation> EnsureAsync(
        RoostPaths paths, RoostConfig config, HttpClient http, IProgress<long>? downloaded = null, CancellationToken cancellation = default)
    {
        if (Locate(paths, config) is { } found) return found;
        if (!OperatingSystem.IsWindows())
            throw new RoostException(MissingSystemGitMessage());

        var installed = await new GitInstaller(paths, http).InstallAsync(GitCatalog.Current, downloaded, cancellation);
        return new GitLocation(installed.Executable, GitSource.Roost, installed.Version);
    }
}
