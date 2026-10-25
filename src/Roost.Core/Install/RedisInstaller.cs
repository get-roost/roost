using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Un paquete de Redis con el SHA-256 que tiene que dar: el zip de redis-windows en Windows, el tar.gz estático de roost-builds en Linux.</summary>
public sealed record RedisBuild(string Version, Uri Url, string Sha256)
{
    public string FileName => Path.GetFileName(Url.AbsolutePath);

    public string Label => $"Redis {Version}";
}

/// <summary>
/// Redis fijado a mano, como <c>ApacheCatalog</c>: Redis no publica binarios para Windows, y el
/// de Roost es la build MSYS2 de <c>redis-windows/redis-windows</c>, que compila el tarball
/// oficial de cada versión en GitHub Actions públicas (el informe de Redis del plan explica por
/// qué esta y no Memurai, Garnet ni la 5.0 de tporadowski). redis-windows no firma con PGP: el
/// SHA-256 se comprueba por tres vías. El de Linux lo compila <c>get-roost/roost-builds</c>
/// (<c>redis.yml</c>: Alpine, estático con jemalloc y OpenSSL, de la fuente de download.redis.io
/// verificada en el workflow con el SHA-256 de github.com/redis/redis-hashes; sin los módulos de Redis 8)
/// y su SHA-256 es el del artefacto publicado en la release <c>redis-&lt;versión&gt;</c>, con su
/// <c>SHA256SUMS.txt</c> y su attestation. Redis 8 es RSALv2, SSPLv1 o AGPLv3 a elegir: roost-builds
/// lo redistribuye bajo la AGPLv3 y enlaza en la release la fuente y el workflow.
/// </summary>
/// <remarks>
/// Para cambiar de versión: en <c>https://api.github.com/repos/redis-windows/redis-windows/releases/tags/&lt;versión&gt;</c>,
/// el asset <c>Redis-&lt;versión&gt;-Windows-x64-msys2.zip</c> (no el <c>-with-Service</c>, que trae un
/// servicio de Windows, ni el de Cygwin); su <c>digest</c>; el hash de la tabla «MSYS2 Builds Hashes»
/// del cuerpo de la release, y el SHA-256 del zip bajado a una carpeta vacía. Si los tres
/// coinciden, se fijan aquí la versión, la URL y el hash. Redis saca parches de seguridad: hay
/// que subirlo con Roost. La licencia (RSALv2, SSPLv1 o AGPLv3, a elegir) no afecta: Roost lo
/// descarga en la máquina del usuario, no lo redistribuye.
/// </remarks>
public static class RedisCatalog
{
    /// <summary>El de este sistema: el zip de redis-windows en Windows, el tar.gz de roost-builds en el resto.</summary>
    public static RedisBuild Current => OperatingSystem.IsWindows() ? Windows : Linux;

    // Redis-8.10.2-Windows-x64-msys2.zip (13.938.657 bytes), release 8.10.2 del 18-sep-2026, verificado
    // el 24-oct-2026: el digest de la API de GitHub, el hash del cuerpo de la release (run de Actions
    // 35294974076) y el SHA-256 del zip descargado coinciden.
    public static readonly RedisBuild Windows = new(
        "8.10.2",
        new Uri("https://github.com/redis-windows/redis-windows/releases/download/8.10.2/Redis-8.10.2-Windows-x64-msys2.zip"),
        "7c8cebd50347eaa1d9e784da842ed47a4f33394637835531d6614777b950ee85");

    // redis-8.10.2-linux-x86_64.tar.gz (8.594.196 bytes): el artefacto del run 40208084641 de redis.yml, 25-oct-2026, de
    // roost-builds (con su SHA256SUMS.txt; fuente redis-8.10.2.tar.gz de download.redis.io verificada en el workflow con
    // redis-hashes; OpenSSL 3.5.9 y jemalloc estaticos, musl), bajado y probado en Ubuntu 24.04. La release redis-8.10.2,
    // con su attestation, se publica con publish: true y from_run: ese run (los mismos bytes). Para cambiar de version:
    // lanzar redis.yml con la nueva, probar el artefacto, publicarlo con from_run y fijar aqui su SHA-256 (fijar-en-roost.py lo hace).
    public static readonly RedisBuild Linux = new(
        "8.10.2",
        new Uri("https://github.com/get-roost/roost-builds/releases/download/redis-8.10.2/redis-8.10.2-linux-x86_64.tar.gz"),
        "d285fc669aaa732aabf2d82a2811bf65e441a81b060ee2ef805b76dd4b7206d9");
}

/// <summary>Un Redis instalado por Roost, en <c>bin/redis/&lt;versión&gt;</c>.</summary>
public sealed record InstalledRedis(string Version, string Directory)
{
    public string Server => Path.Combine(Directory, RedisInstaller.ServerName);

    public string Cli => Path.Combine(Directory, RedisInstaller.CliName);

    public string Label => $"Redis {Version}";
}

/// <summary>
/// Instala Redis con las reglas de siempre (ver <see cref="InstallFiles"/>): descarga verificada
/// antes de extraer, extracción a una carpeta temporal que se renombra al final, y quitar solo los
/// binarios. Los datos de <c>data/redis</c> no los borra nada en Roost.
/// </summary>
public sealed class RedisInstaller
{
    public static string ServerName => OsNames.Exe("redis-server");

    public static string CliName => OsNames.Exe("redis-cli");

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public RedisInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public InstalledRedis? Find(RedisBuild build)
    {
        var dir = Path.Combine(_paths.RedisBin, build.Version);
        return File.Exists(Path.Combine(dir, ServerName)) && File.Exists(Path.Combine(dir, CliName)) ? new InstalledRedis(build.Version, dir) : null;
    }

    /// <summary>Los Redis instalados, el más nuevo primero. Las carpetas de trabajo (con punto) no cuentan.</summary>
    public IReadOnlyList<InstalledRedis> ListInstalled()
    {
        if (!System.IO.Directory.Exists(_paths.RedisBin)) return [];
        return System.IO.Directory.EnumerateDirectories(_paths.RedisBin)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Where(x => !x.Name.StartsWith('.') && File.Exists(Path.Combine(x.Dir, ServerName)))
            .Select(x => (x.Dir, x.Name, Version: ComponentVersion.TryParse(x.Name, out var v) ? v : default))
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => new InstalledRedis(x.Name, x.Dir))
            .ToList();
    }

    /// <summary>
    /// El que usa el stack: el de esta versión de Roost o, si no está, el más nuevo instalado. Así
    /// una Roost nueva con otro Redis fijado no deja el stack sin arrancar hasta que se instale.
    /// </summary>
    public InstalledRedis? Locate() => Find(RedisCatalog.Current) ?? ListInstalled().FirstOrDefault();

    public async Task<InstalledRedis> InstallAsync(
        RedisBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("RedisInstaller sin HttpClient no puede instalar.");
        var target = Path.Combine(_paths.RedisBin, build.Version);
        if (Find(build) is not null)
            throw new RoostException($"Redis {build.Version} ya esta instalado en {target}.");

        InstallFiles.CleanLeftovers(_paths.RedisBin);
        // Una carpeta de la versión sin sus exe no la dejó Roost a medias (renombra al final): se aparta.
        if (System.IO.Directory.Exists(target))
            InstallFiles.RemoveAtomic(target, _paths.RedisBin, $"la carpeta incompleta de Redis {build.Version}");

        var zip = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // El zip lo mete todo en Redis-<versión>-Windows-x64-msys2/: se instala su contenido. El tar.gz de
        // roost-builds trae redis-server, redis-cli y redis-benchmark en la raíz (con su bit de ejecución).
        InstallFiles.ExtractAtomic(zip, target, ServerName, stripSingleRoot: true, build.FileName);
        if (!File.Exists(Path.Combine(target, CliName)))
        {
            InstallFiles.RemoveAtomic(target, _paths.RedisBin, build.Label);
            throw new RoostException($"El paquete {build.FileName} no trae {CliName} (lo necesita la parada limpia); no se instala.");
        }
        InstallFiles.MarkExecutable(Path.Combine(target, ServerName));
        InstallFiles.MarkExecutable(Path.Combine(target, CliName));
        return new InstalledRedis(build.Version, target);
    }

    /// <summary>Quita los binarios. Los datos (<c>data/redis</c>) se quedan.</summary>
    public void Remove(InstalledRedis redis) => InstallFiles.RemoveAtomic(redis.Directory, _paths.RedisBin, redis.Label);
}
