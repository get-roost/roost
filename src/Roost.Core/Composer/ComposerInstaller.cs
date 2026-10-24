using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Composer;

/// <summary>Un Composer instalado por Roost, en <c>bin/composer/&lt;versión&gt;/composer.phar</c>.</summary>
public sealed record InstalledComposer(string Version, string Directory)
{
    public const string PharName = "composer.phar";

    public string Phar => Path.Combine(Directory, PharName);

    public string Label => $"Composer {Version}";
}

/// <summary>
/// Instala <c>composer.phar</c> con las reglas de siempre (ver <c>InstallFiles</c>): se descarga a la
/// caché y se verifica su SHA-256 antes de nada, y se copia a una carpeta <c>.tmp-…</c> que se
/// renombra al final, así que o queda instalado entero o no queda nada. Cada versión va en su
/// carpeta; una vieja no se borra al instalar la nueva.
/// </summary>
public sealed class ComposerInstaller
{
    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public ComposerInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public string InstallDirectory(ComposerBuild build) => Path.Combine(_paths.ComposerBin, build.Version);

    public InstalledComposer? Find(ComposerBuild build)
    {
        var dir = InstallDirectory(build);
        return File.Exists(Path.Combine(dir, InstalledComposer.PharName)) ? new InstalledComposer(build.Version, dir) : null;
    }

    /// <summary>Los Composer instalados, el más nuevo primero. Las carpetas de trabajo (con punto) no cuentan.</summary>
    public IReadOnlyList<InstalledComposer> ListInstalled()
    {
        if (!System.IO.Directory.Exists(_paths.ComposerBin)) return [];
        return System.IO.Directory.EnumerateDirectories(_paths.ComposerBin)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Where(x => !x.Name.StartsWith('.') && File.Exists(Path.Combine(x.Dir, InstalledComposer.PharName)))
            .Select(x => (x.Dir, x.Name, Version: ComponentVersion.TryParse(x.Name, out var v) ? v : default))
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => new InstalledComposer(x.Name, x.Dir))
            .ToList();
    }

    public async Task<InstalledComposer> InstallAsync(
        ComposerBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("ComposerInstaller sin HttpClient no puede instalar.");
        var target = InstallDirectory(build);
        if (Find(build) is not null)
            throw new RoostException($"Composer {build.Version} ya esta instalado en {target}.");

        var temp = Path.Combine(_paths.ComposerBin, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            InstallFiles.CleanLeftovers(_paths.ComposerBin);
            // Una carpeta de la versión sin su phar no la dejó Roost a medias (renombra al final):
            // es que alguien borró el fichero. Se aparta y se instala de nuevo.
            if (System.IO.Directory.Exists(target))
                InstallFiles.RemoveAtomic(target, _paths.ComposerBin, $"la carpeta vacia de Composer {build.Version}");

            var phar = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            System.IO.Directory.CreateDirectory(temp);
            File.Copy(phar, Path.Combine(temp, InstalledComposer.PharName));
            // Renombrar es atómico dentro del mismo volumen: o queda instalado entero o no queda.
            InstallFiles.MoveDirectory(temp, target);
            return new InstalledComposer(build.Version, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo preparar Composer: {ex.Message}", ex);
        }
        catch (RoostException ex)
        {
            throw new RoostException($"No se pudo preparar Composer: {ex.Message} Prueba de nuevo (roost composer install).", ex);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }
}
