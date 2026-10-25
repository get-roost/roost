using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>El cloudflared (un solo ejecutable, sin zip: el de Windows o el de Linux) con el SHA-256 que tiene que dar.</summary>
public sealed record CloudflaredBuild(string Version, Uri Url, string Sha256)
{
    /// <summary>En la caché, con la versión (el asset se llama igual en todas las releases): <c>cloudflared-2026.10.3-windows-amd64.exe</c>, <c>cloudflared-2026.10.3-linux-amd64</c>.</summary>
    public string FileName => $"cloudflared-{Version}-{Path.GetFileName(Url.AbsolutePath)["cloudflared-".Length..]}";

    public string Label => $"cloudflared {Version}";
}

/// <summary>
/// cloudflared fijado a mano, como <c>MailpitCatalog</c>: el ejecutable de Windows amd64 (o el de
/// Linux amd64) de la release de <c>cloudflare/cloudflared</c> en GitHub, con su SHA-256. Es el cliente
/// de Cloudflare Tunnel (Apache-2.0, un solo ejecutable de Go); Roost lo usa solo para los túneles
/// rápidos de <c>trycloudflare.com</c>, que no piden cuenta (<c>roost share</c>).
/// </summary>
/// <remarks>
/// Para cambiar de versión: en <c>https://api.github.com/repos/cloudflare/cloudflared/releases/latest</c>, los
/// assets <c>cloudflared-windows-amd64.exe</c> y <c>cloudflared-linux-amd64</c> y su <c>digest</c>, y el mismo
/// SHA-256 en la lista «SHA256 Checksums» del cuerpo de la release; bajarlos a una carpeta vacía y
/// comprobar que su SHA-256 da lo mismo (son las tres vías), y en Windows que lleva la firma Authenticode
/// válida de «Cloudflare, Inc.» (<c>Get-AuthenticodeSignature</c>). Después, <c>cloudflared --version</c>.
/// Roost lo lanza con <c>--no-autoupdate</c>: nunca se reemplaza solo.
/// </remarks>
public static class CloudflaredCatalog
{
    /// <summary>El de este sistema: el exe de Windows o el binario de Linux.</summary>
    public static CloudflaredBuild Current => OperatingSystem.IsWindows() ? Windows : Linux;

    // cloudflared-windows-amd64.exe (55.329.336 bytes) de la release 2026.10.3 del 21-oct-2026, verificado
    // el 24-oct-2026: el digest de la API de GitHub, la lista del cuerpo de la release y el SHA-256 del
    // exe descargado coinciden, y su firma Authenticode es válida (Cloudflare, Inc., DigiCert).
    public static readonly CloudflaredBuild Windows = new(
        "2026.10.3",
        new Uri("https://github.com/cloudflare/cloudflared/releases/download/2026.10.3/cloudflared-windows-amd64.exe"),
        "76b2805253d0d6ffacb092a7acfbee99e7af6917835c0c5410e23f3ef5052da2");

    // cloudflared-linux-amd64 (40.227.721 bytes) de la misma release, verificado el 25-oct-2026: el digest de la
    // API de GitHub, la lista «SHA256 Checksums» del cuerpo de la release y el SHA-256 del binario descargado coinciden.
    public static readonly CloudflaredBuild Linux = new(
        "2026.10.3",
        new Uri("https://github.com/cloudflare/cloudflared/releases/download/2026.10.3/cloudflared-linux-amd64"),
        "053b80b0a23031a038fdbf3c073f59f82b8f1f152aa67971d7383ffafc0dbab0");
}

/// <summary>Un cloudflared instalado por Roost, en <c>bin/cloudflared/&lt;versión&gt;</c>.</summary>
public sealed record InstalledCloudflared(string Version, string Directory)
{
    public string Executable => Path.Combine(Directory, CloudflaredInstaller.ExecutableName);

    public string Label => $"cloudflared {Version}";
}

/// <summary>
/// Instala cloudflared con las reglas de siempre (ver <see cref="InstallFiles"/>): descarga verificada
/// a la caché, copia a una carpeta <c>.tmp-…</c> y renombrado al final, como Composer.
/// </summary>
public sealed class CloudflaredInstaller
{
    public static string ExecutableName => OsNames.Exe("cloudflared");

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public CloudflaredInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public string InstallDirectory(CloudflaredBuild build) => Path.Combine(_paths.CloudflaredBin, build.Version);

    public InstalledCloudflared? Find(CloudflaredBuild build)
    {
        var dir = InstallDirectory(build);
        return File.Exists(Path.Combine(dir, ExecutableName)) ? new InstalledCloudflared(build.Version, dir) : null;
    }

    /// <summary>Los cloudflared instalados, el más nuevo primero. Las carpetas de trabajo (con punto) no cuentan.</summary>
    public IReadOnlyList<InstalledCloudflared> ListInstalled()
    {
        if (!System.IO.Directory.Exists(_paths.CloudflaredBin)) return [];
        return System.IO.Directory.EnumerateDirectories(_paths.CloudflaredBin)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Where(x => !x.Name.StartsWith('.') && File.Exists(Path.Combine(x.Dir, ExecutableName)))
            .Select(x => (x.Dir, x.Name, Version: ComponentVersion.TryParse(x.Name, out var v) ? v : default))
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => new InstalledCloudflared(x.Name, x.Dir))
            .ToList();
    }

    /// <summary>El que usa <c>roost share</c>: el de esta versión de Roost o, si no está, el más nuevo instalado.</summary>
    public InstalledCloudflared? Locate() => Find(CloudflaredCatalog.Current) ?? ListInstalled().FirstOrDefault();

    public async Task<InstalledCloudflared> InstallAsync(
        CloudflaredBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("CloudflaredInstaller sin HttpClient no puede instalar.");
        var target = InstallDirectory(build);
        if (Find(build) is not null)
            throw new RoostException($"cloudflared {build.Version} ya esta instalado en {target}.");

        var temp = Path.Combine(_paths.CloudflaredBin, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            InstallFiles.CleanLeftovers(_paths.CloudflaredBin);
            // Una carpeta de la versión sin su exe no la dejó Roost a medias (renombra al final): es
            // que alguien borró el fichero. Se aparta y se instala de nuevo.
            if (System.IO.Directory.Exists(target))
                InstallFiles.RemoveAtomic(target, _paths.CloudflaredBin, $"la carpeta incompleta de cloudflared {build.Version}");

            var exe = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            System.IO.Directory.CreateDirectory(temp);
            File.Copy(exe, Path.Combine(temp, ExecutableName));
            InstallFiles.MarkExecutable(Path.Combine(temp, ExecutableName));   // un binario suelto no trae sus permisos
            // Renombrar es atómico dentro del mismo volumen: o queda instalado entero o no queda.
            InstallFiles.MoveDirectory(temp, target);
            return new InstalledCloudflared(build.Version, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo preparar cloudflared: {ex.Message}", ex);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }

    /// <summary>Quita el ejecutable (un túnel en marcha lo tiene abierto: entonces falla sin tocar nada).</summary>
    public void Remove(InstalledCloudflared cloudflared) =>
        InstallFiles.RemoveAtomic(cloudflared.Directory, _paths.CloudflaredBin, cloudflared.Label);
}
