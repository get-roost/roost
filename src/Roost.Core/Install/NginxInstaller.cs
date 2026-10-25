using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>
/// Un paquete de nginx con el SHA-256 que tiene que dar: el zip de nginx.org en Windows, el tar.gz
/// estático de roost-builds en Linux.
/// </summary>
public sealed record NginxBuild(string Version, Uri Url, string Sha256)
{
    /// <summary>El nombre con el que se guarda en la caché de descargas: el de la URL (<c>nginx-1.30.6.zip</c>, <c>nginx-1.30.6-linux-x86_64.tar.gz</c>).</summary>
    public string FileName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>
/// nginx fijado a mano mientras no exista el feed roost-catalog. nginx.org no publica
/// checksums, solo firmas PGP: el SHA-256 del zip de Windows es el de un zip cuya firma se verificó con
/// la clave del desarrollador que lo firmó, descargada de nginx.org/keys. El de Linux lo compila
/// <c>get-roost/roost-builds</c> (<c>nginx.yml</c>: Alpine, estático con pcre2, zlib y OpenSSL, de la
/// fuente de nginx.org verificada con su firma PGP en el propio workflow) y su SHA-256 es el del
/// artefacto publicado en la release <c>nginx-&lt;versión&gt;</c>, con su <c>SHA256SUMS.txt</c> y su attestation.
/// </summary>
public static class NginxCatalog
{
    /// <summary>El de este sistema: el zip de nginx.org en Windows, el tar.gz de roost-builds en el resto.</summary>
    public static NginxBuild Current => OperatingSystem.IsWindows() ? Windows : Linux;

    // nginx-1.30.6.zip (rama estable): firma PGP verificada el 22-oct-2026 con la clave de Sergey
    // Kandaurov (D678 6CE3 03D9 A902 2998 DC6C C846 4D54 9AF7 5C0A, nginx.org/keys/pluknet.key).
    public static readonly NginxBuild Windows = new(
        "1.30.6",
        new Uri("https://nginx.org/download/nginx-1.30.6.zip"),
        "c6ee35edb0a861063323f709192ae50d1eefaf2ea7d00678bd2b22d8542dc61e");

    // nginx-1.30.6-linux-x86_64.tar.gz (2.892.542 bytes): el publicado en la release nginx-1.30.6 de roost-builds
    // (run 40199286172 de nginx.yml, 25-oct-2026, con su SHA256SUMS.txt y su attestation; fuente
    // nginx-1.30.6.tar.gz de nginx.org, firmada por la clave de Kandaurov y verificada en el workflow;
    // OpenSSL 3.5.9, PCRE2 10.46, zlib 1.3.2, musl 1.2.5), bajado de la release y probado en Ubuntu 24.04.
    // Para cambiar de versión: lanzar nginx.yml con la nueva, probar el artefacto, publicarlo con publish: true
    // y from_run: <ese run> (los mismos bytes; sin from_run recompila y sale otro SHA-256) y fijar aquí su SHA-256.
    public static readonly NginxBuild Linux = new(
        "1.30.6",
        new Uri("https://github.com/get-roost/roost-builds/releases/download/nginx-1.30.6/nginx-1.30.6-linux-x86_64.tar.gz"),
        "a20525179acfcdf36f40eeb58ab1ab9bbae5dfb4ac0f6ec53cf5f8c8ce28c40a");
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
    public static string ExecutableName => OsNames.Exe("nginx");

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
        var archive = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // El zip oficial de Windows lo mete todo en nginx-<versión>/: se instala su contenido. El tar.gz
        // de roost-builds trae nginx, conf/ y licenses/ en la raíz (y conserva el bit de ejecución).
        InstallFiles.ExtractAtomic(archive, target, ExecutableName, stripSingleRoot: true, build.FileName);
        InstallFiles.MarkExecutable(Path.Combine(target, ExecutableName));
        return new InstalledNginx(build.Version, target);
    }
}
