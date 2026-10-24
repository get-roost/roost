using System.Security.Cryptography;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>
/// Instala la DLL de Xdebug con las reglas de siempre (ver <c>InstallFiles</c>): se descarga a la
/// caché y se verifica su SHA-256 antes de nada, se copia a una carpeta <c>.tmp-…</c> y se mueve a
/// su sitio al final (renombrar dentro del mismo volumen es atómico: o está entera o no está).
/// </summary>
/// <remarks>
/// Vive en <c>bin/xdebug/&lt;versión&gt;/&lt;nombre de xdebug.org&gt;</c> y el php.ini la carga con su ruta
/// completa (<c>zend_extension="…"</c>), no en el <c>ext/</c> de cada PHP:
/// <list type="bullet">
/// <item>la carpeta de cada PHP sigue siendo el zip oficial tal cual (quitarlo o instalar la 8.4.28 no
/// sabe nada de Xdebug, y la 8.4.28 usa la misma DLL sin bajarla otra vez: es de la rama);</item>
/// <item>no sale entre las extensiones de <c>ext/</c>, así que nada puede escribirla como
/// <c>extension=</c>;</item>
/// <item>quitar un PHP no la borra (330 KB): la reutiliza el siguiente de esa rama.</item>
/// </list>
/// <b>En Linux</b> es al revés: el <c>xdebug.so</c> solo vale con el PHP exacto con el que se compiló,
/// así que cada PHP de roost-builds trae el suyo en <c>ext/xdebug.so</c> y aquí no se instala nada:
/// <see cref="Locate"/> devuelve ese fichero (<see cref="BundledPath"/>).
/// </remarks>
public sealed class XdebugInstaller
{
    /// <summary>El nombre del módulo compartido en Linux, dentro del <c>ext/</c> de cada PHP de roost-builds.</summary>
    public const string SharedObjectName = "xdebug.so";

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public XdebugInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    /// <summary>Una subcarpeta por versión de Xdebug: <c>bin/xdebug/3.5.3</c>.</summary>
    public string Bin => Path.Combine(_paths.Root, "bin", "xdebug");

    public string DllPath(XdebugBuild build) => Path.Combine(Bin, build.Version, Path.GetFileName(build.FileName));

    /// <summary>La DLL de ese build si ya está instalada.</summary>
    public string? Find(XdebugBuild build) => File.Exists(DllPath(build)) ? DllPath(build) : null;

    /// <summary>El <c>ext/xdebug.so</c> que trae la propia instalación (los PHP de roost-builds), o null si no lo trae.</summary>
    public static string? BundledPath(InstalledPhp php)
    {
        var so = Path.Combine(php.Directory, "ext", SharedObjectName);
        return File.Exists(so) ? so : null;
    }

    /// <summary>La versión del Xdebug que trae la instalación, según su <c>BUILD.json</c>; null si no lo dice.</summary>
    public static string? BundledVersion(InstalledPhp php) => php.ReadBuildInfo()?.Xdebug;

    /// <summary>
    /// El módulo de Xdebug que carga una instalación. En Linux, el <c>ext/xdebug.so</c> de la propia
    /// instalación. En Windows, la DLL que fija <see cref="XdebugCatalog"/> si está y, si no (una Roost
    /// nueva fija otra versión), la más nueva que haya de su rama. null si no hay ninguna o si Roost
    /// no pone Xdebug en esa instalación (TS, x86).
    /// </summary>
    public string? Locate(InstalledPhp php)
    {
        if (XdebugCatalog.IsBundled(php)) return BundledPath(php);
        if (XdebugCatalog.WhyNot(php) is not null || php.Version is not { } v) return null;
        if (XdebugCatalog.For(php) is { } pinned && Find(pinned) is { } dll) return dll;
        if (!Directory.Exists(Bin)) return null;

        // php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll y php_xdebug-3.1.6-7.4-vc15-nts-x86_64.dll.
        var branch = $"{v.Major}.{v.Minor}";
        return Directory.EnumerateDirectories(Bin)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Select(d => (Dir: d, Version: ComponentVersion.TryParse(Path.GetFileName(d), out var x) ? x : (ComponentVersion?)null))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .SelectMany(x => Directory.EnumerateFiles(x.Dir, $"php_xdebug-*-{branch}-*.dll"))
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("-nts-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-x86_64.dll", StringComparison.OrdinalIgnoreCase);
            });
    }

    /// <summary>
    /// Deja instalada la DLL de <paramref name="build"/> y devuelve su ruta. Si ya está y su SHA-256
    /// cuadra, no baja nada. Lanza <see cref="RoostException"/> si algo falla, sin dejar nada a medias.
    /// </summary>
    public async Task<string> InstallAsync(XdebugBuild build, IProgress<long>? downloaded = null, CancellationToken cancellation = default)
    {
        var target = DllPath(build);
        if (File.Exists(target) && Sha256Of(target) == build.Sha256) return target;

        var http = _http ?? throw new InvalidOperationException("XdebugInstaller sin HttpClient no puede instalar.");
        var dir = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(dir, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            InstallFiles.CleanLeftovers(dir);
            var dll = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            Directory.CreateDirectory(temp);
            var staged = Path.Combine(temp, Path.GetFileName(build.FileName));
            File.Copy(dll, staged);
            // Si había una con otro SHA-256 (rota), se pisa; si la tiene cargada un php-cgi, falla sin tocarla.
            File.Move(staged, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo instalar {build.Label} en {dir}: {ex.Message} Si un PHP la tiene cargada, para el stack (roost down) y prueba otra vez.", ex);
        }
        catch (RoostException ex)
        {
            throw new RoostException($"No se pudo instalar {build.Label}: {ex.Message}", ex);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }

    private static string Sha256Of(string file)
    {
        try { return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
