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
}

/// <summary>El git que usa Roost: su ejecutable, de dónde sale y, si es el de Roost, su versión.</summary>
public sealed record GitLocation(string Executable, GitSource Source, string? Version);

/// <summary>
/// Qué git usa Roost (ROOST.md: git al estilo Plesk). El de <c>[git] exe</c> si está configurado;
/// si no, el MinGit de <c>bin/git</c>, que se instala la primera vez que hace falta y no al
/// instalar Roost.
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
                throw new RoostException($"El git de roost.toml ([git] exe = \"{exe}\") no existe. Arregla la ruta o quita la clave para usar el git de Roost.");
            return new GitLocation(Path.GetFullPath(exe), GitSource.Configured, null);
        }

        var installed = new GitInstaller(paths).Find(GitCatalog.Current);
        return installed is null ? null : new GitLocation(installed.Executable, GitSource.Roost, installed.Version);
    }

    /// <summary>Como <see cref="Locate"/>, pero si no hay ninguno instala el MinGit de Roost (solo en Windows).</summary>
    public static async Task<GitLocation> EnsureAsync(
        RoostPaths paths, RoostConfig config, HttpClient http, IProgress<long>? downloaded = null, CancellationToken cancellation = default)
    {
        if (Locate(paths, config) is { } found) return found;
        if (!OperatingSystem.IsWindows())
            throw new RoostException("Roost solo instala git en Windows por ahora. Indica el tuyo en roost.toml: [git] exe = \"/usr/bin/git\".");

        var installed = await new GitInstaller(paths, http).InstallAsync(GitCatalog.Current, downloaded, cancellation);
        return new GitLocation(installed.Executable, GitSource.Roost, installed.Version);
    }
}
