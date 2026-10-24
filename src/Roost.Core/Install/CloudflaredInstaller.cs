using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>El cloudflared de Windows (un solo ejecutable, sin zip) con el SHA-256 que tiene que dar.</summary>
public sealed record CloudflaredBuild(string Version, Uri Url, string Sha256)
{
    /// <summary>En la caché, con la versión: el asset se llama igual en todas las releases.</summary>
    public string FileName => $"cloudflared-{Version}-windows-amd64.exe";

    public string Label => $"cloudflared {Version}";
}

/// <summary>
/// cloudflared fijado a mano, como <c>MailpitCatalog</c>: el ejecutable de Windows amd64 de la release
/// de <c>cloudflare/cloudflared</c> en GitHub, con su SHA-256. Es el cliente de Cloudflare Tunnel
/// (Apache-2.0, un solo ejecutable de Go); Roost lo usa solo para los túneles rápidos de
/// <c>trycloudflare.com</c>, que no piden cuenta (<c>roost share</c>).
/// </summary>
/// <remarks>
/// Para cambiar de versión: en <c>https://api.github.com/repos/cloudflare/cloudflared/releases/latest</c>, el
/// asset <c>cloudflared-windows-amd64.exe</c> y su <c>digest</c>, y el mismo SHA-256 en la lista
/// «SHA256 Checksums» del cuerpo de la release; bajarlo a una carpeta vacía y comprobar que su
/// SHA-256 da lo mismo (son las tres vías), y que lleva la firma Authenticode válida de
/// «Cloudflare, Inc.» (<c>Get-AuthenticodeSignature</c>). Después, <c>cloudflared --version</c>.
/// Roost lo lanza con <c>--no-autoupdate</c>: nunca se reemplaza solo.
/// </remarks>
public static class CloudflaredCatalog
{
    // cloudflared-windows-amd64.exe (55.329.336 bytes) de la release 2026.10.3 del 21-oct-2026, verificado
    // el 24-oct-2026: el digest de la API de GitHub, la lista del cuerpo de la release y el SHA-256 del
    // exe descargado coinciden, y su firma Authenticode es válida (Cloudflare, Inc., DigiCert).
    public static readonly CloudflaredBuild Windows = new(
        "2026.10.3",
        new Uri("https://github.com/cloudflare/cloudflared/releases/download/2026.10.3/cloudflared-windows-amd64.exe"),
        "76b2805253d0d6ffacb092a7acfbee99e7af6917835c0c5410e23f3ef5052da2");
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
    public const string ExecutableName = "cloudflared.exe";

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
    public InstalledCloudflared? Locate() => Find(CloudflaredCatalog.Windows) ?? ListInstalled().FirstOrDefault();

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
