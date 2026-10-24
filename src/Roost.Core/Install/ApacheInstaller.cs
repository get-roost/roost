using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Un zip de Apache para Windows con el SHA-256 que tiene que dar.</summary>
public sealed record ApacheBuild(string Version, Uri Url, string Sha256, string Redistributable)
{
    public string FileName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>
/// Apache fijado a mano, como nginx, mientras no exista el feed roost-catalog. Apache no publica
/// binarios para Windows: los de Apache Lounge son el estándar de hecho. Los firma Steffen Land
/// con PGP; el SHA-256 de aquí es el de un zip cuya firma se ha verificado con esa clave.
/// </summary>
public static class ApacheCatalog
{
    /// <summary>El de este sistema (hoy, el de Windows; el de Linux llega con el plan de Linux).</summary>
    public static ApacheBuild Current => Windows;

    // httpd-2.4.69-261002-Win64-VS18.zip: firma PGP verificada el 23-oct-2026 con la clave de
    // Steffen Land (Apache Lounge), 3D49 885E ADE8 BC39 9F46 D5DD BEE8 8A78 29C1 7558, la misma
    // en apachelounge.com/download/Steffen_Land.asc y en keyserver.ubuntu.com. Para cambiar de
    // versión: bajar el zip y su .asc, gpg --verify con esa clave y fijar aquí el SHA-256.
    public static readonly ApacheBuild Windows = new(
        "2.4.69",
        new Uri("https://www.apachelounge.com/download/VS18/binaries/httpd-2.4.69-261002-Win64-VS18.zip"),
        "9f1f93cab9a0c6dc5e628aa640784bb30347f93fcd01b46a4c0d6b5ba76bf4e2",
        "Visual C++ Redistributable 2017-2026 (14.50 o posterior)");
}

/// <summary>Un Apache instalado por Roost, en <c>bin/apache/&lt;versión&gt;</c>.</summary>
public sealed record InstalledApache(string Version, string Directory)
{
    public string Executable => Path.Combine(Directory, "bin", ApacheInstaller.ExecutableName);
    public string MimeTypes => Path.Combine(Directory, "conf", "mime.types");
    public string Modules => Path.Combine(Directory, "modules");
}

/// <summary>Instala Apache con las mismas reglas que PHP y nginx (ver <see cref="InstallFiles"/>).</summary>
public sealed class ApacheInstaller
{
    public const string ExecutableName = "httpd.exe";

    /// <summary>La carpeta del zip de Apache Lounge que es Apache; al lado trae un ReadMe y poco más.</summary>
    public const string ZipFolder = "Apache24";

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public ApacheInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public InstalledApache? Find(ApacheBuild build)
    {
        var dir = Path.Combine(_paths.ApacheBin, build.Version);
        return File.Exists(Path.Combine(dir, "bin", ExecutableName)) ? new InstalledApache(build.Version, dir) : null;
    }

    public async Task<InstalledApache> InstallAsync(
        ApacheBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("ApacheInstaller sin HttpClient no puede instalar.");
        var target = Path.Combine(_paths.ApacheBin, build.Version);
        if (Directory.Exists(target))
            throw new RoostException($"Apache {build.Version} ya esta instalado en {target}.");

        InstallFiles.CleanLeftovers(_paths.ApacheBin);
        var zip = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        InstallFiles.ExtractAtomic(zip, target, Path.Combine("bin", ExecutableName), stripSingleRoot: false, build.FileName, fromFolder: ZipFolder);
        return new InstalledApache(build.Version, target);
    }
}
