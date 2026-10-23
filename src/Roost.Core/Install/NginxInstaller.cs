using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Un zip de nginx para Windows con el SHA-256 que tiene que dar.</summary>
public sealed record NginxBuild(string Version, Uri Url, string Sha256)
{
    public string FileName => $"nginx-{Version}.zip";
}

/// <summary>
/// nginx fijado a mano mientras no exista el feed roost-catalog. nginx.org no publica
/// checksums, solo firmas PGP: el SHA-256 de aquí es el de un zip cuya firma se verificó con
/// la clave del desarrollador que lo firmó, descargada de nginx.org/keys.
/// </summary>
public static class NginxCatalog
{
    // nginx-1.30.6.zip (rama estable): firma PGP verificada el 22-oct-2026 con la clave de Sergey
    // Kandaurov (D678 6CE3 03D9 A902 2998 DC6C C846 4D54 9AF7 5C0A, nginx.org/keys/pluknet.key).
    public static readonly NginxBuild Windows = new(
        "1.30.6",
        new Uri("https://nginx.org/download/nginx-1.30.6.zip"),
        "c6ee35edb0a861063323f709192ae50d1eefaf2ea7d00678bd2b22d8542dc61e");
}

/// <summary>Un nginx instalado por Roost, en <c>bin/nginx/&lt;versión&gt;</c>.</summary>
public sealed record InstalledNginx(string Version, string Directory)
{
    public string Executable => Path.Combine(Directory, NginxInstaller.ExecutableName);
    public string MimeTypes => Path.Combine(Directory, "conf", "mime.types");
    public string FastCgiParams => Path.Combine(Directory, "conf", "fastcgi_params");
}

/// <summary>Instala nginx con las mismas reglas que PHP (ver <see cref="InstallFiles"/>).</summary>
public sealed class NginxInstaller
{
    public const string ExecutableName = "nginx.exe";

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public NginxInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public InstalledNginx? Find(NginxBuild build)
    {
        var dir = Path.Combine(_paths.NginxBin, build.Version);
        return File.Exists(Path.Combine(dir, ExecutableName)) ? new InstalledNginx(build.Version, dir) : null;
    }

    public async Task<InstalledNginx> InstallAsync(
        NginxBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("NginxInstaller sin HttpClient no puede instalar.");
        var target = Path.Combine(_paths.NginxBin, build.Version);
        if (Directory.Exists(target))
            throw new RoostException($"nginx {build.Version} ya esta instalado en {target}.");

        InstallFiles.CleanLeftovers(_paths.NginxBin);
        var zip = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // El zip oficial lo mete todo en nginx-<versión>/: se instala su contenido.
        InstallFiles.ExtractAtomic(zip, target, ExecutableName, stripSingleRoot: true, build.FileName);
        return new InstalledNginx(build.Version, target);
    }
}
