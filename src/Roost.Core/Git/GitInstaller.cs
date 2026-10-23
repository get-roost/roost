using System.IO.Compression;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Git;

/// <summary>Un MinGit instalado por Roost, en <c>bin/git/&lt;versión&gt;</c>.</summary>
public sealed record InstalledGit(string Version, string Directory)
{
    public string Executable => Path.Combine(Directory, "cmd", GitInstaller.ExecutableName);
}

/// <summary>Instala MinGit con las mismas reglas que nginx (ver <c>InstallFiles</c>).</summary>
public sealed class GitInstaller
{
    public const string ExecutableName = "git.exe";

    /// <summary>El transporte HTTPS de git (libcurl). Sin él, MinGit solo clonaría por ssh y file://.</summary>
    private const string HttpsTransport = "git-remote-https.exe";

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public GitInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public InstalledGit? Find(GitBuild build)
    {
        var dir = Path.Combine(_paths.GitBin, build.Version);
        return File.Exists(Path.Combine(dir, "cmd", ExecutableName)) ? new InstalledGit(build.Version, dir) : null;
    }

    public async Task<InstalledGit> InstallAsync(
        GitBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("GitInstaller sin HttpClient no puede instalar.");
        var target = Path.Combine(_paths.GitBin, build.Version);
        if (Directory.Exists(target))
            throw new RoostException($"git {build.Version} ya esta instalado en {target}.");

        try
        {
            InstallFiles.CleanLeftovers(_paths.GitBin);
            var zip = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            // Antes de extraer: que sea un MinGit completo, con el transporte HTTPS.
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (!archive.Entries.Any(e => e.FullName.EndsWith("/" + HttpsTransport, StringComparison.OrdinalIgnoreCase)))
                    throw new RoostException($"El zip {build.FileName} no trae {HttpsTransport} (el transporte HTTPS de git); no se instala.");
            }

            // El zip de MinGit no lleva carpeta envolvente (cmd/, mingw64/, etc/, usr/ en la raíz).
            InstallFiles.ExtractAtomic(zip, target, Path.Combine("cmd", ExecutableName), stripSingleRoot: true, build.FileName);
            return new InstalledGit(build.Version, target);
        }
        catch (RoostException ex)
        {
            // El texto 13 del diseño: que diga que es git lo que no se pudo preparar y cuál es la salida.
            throw new RoostException($"No se pudo preparar git: {ex.Message} Prueba de nuevo o indica otro git en roost.toml ([git] exe).", ex);
        }
    }
}
