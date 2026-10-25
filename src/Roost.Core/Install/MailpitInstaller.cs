using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Un paquete de Mailpit (el zip de Windows o el tar.gz de Linux) con el SHA-256 que tiene que dar.</summary>
public sealed record MailpitBuild(string Version, Uri Url, string Sha256)
{
    /// <summary>En la caché, con la versión (el asset se llama igual en todas las releases) y la extensión de la URL.</summary>
    public string FileName => $"mailpit-{Version}-{Path.GetFileName(Url.AbsolutePath)["mailpit-".Length..]}";

    public string Label => $"Mailpit {Version}";
}

/// <summary>
/// Mailpit fijado a mano, como <c>GitCatalog</c> con MinGit: el zip de Windows amd64 (o el tar.gz de
/// Linux amd64) de la release de <c>axllent/mailpit</c> en GitHub, con su SHA-256. Mailpit es un
/// servidor SMTP de pruebas con bandeja web (MIT, un solo ejecutable de Go): lo que llega a su SMTP
/// no sale del equipo. En Linux hace además de <c>sendmail</c> para el <c>mail()</c> de PHP (<c>MailConf</c>).
/// </summary>
/// <remarks>
/// Para cambiar de versión: en <c>https://api.github.com/repos/axllent/mailpit/releases/latest</c>, los
/// assets <c>mailpit-windows-amd64.zip</c> y <c>mailpit-linux-amd64.tar.gz</c> y su <c>digest</c>; bajarlos a
/// una carpeta vacía y comprobar que su SHA-256 da lo mismo. Las releases de Mailpit no traen
/// <c>checksums.txt</c> ni firma (a 24-oct-2026): esas dos vías son las que hay. Si trae arreglos de
/// seguridad (la 1.32.0 los trae), conviene subirlo con Roost.
/// </remarks>
public static class MailpitCatalog
{
    /// <summary>El de este sistema: el zip de Windows o el tar.gz de Linux.</summary>
    public static MailpitBuild Current => OperatingSystem.IsWindows() ? Windows : Linux;

    // mailpit-windows-amd64.zip (10.632.291 bytes) de la release v1.32.0 del 22-oct-2026, verificado el
    // 24-oct-2026: el digest de la API de GitHub y el SHA-256 del zip descargado coinciden.
    public static readonly MailpitBuild Windows = new(
        "1.32.0",
        new Uri("https://github.com/axllent/mailpit/releases/download/v1.32.0/mailpit-windows-amd64.zip"),
        "16915767a6c95dca569336e8f561f91cf355e9a33c57b91deedcc10a4bba7fd4");

    // mailpit-linux-amd64.tar.gz (10.583.464 bytes; mailpit, LICENSE y README.md) de la misma release, verificado
    // el 25-oct-2026: el digest de la API de GitHub y el SHA-256 del tar.gz descargado coinciden.
    public static readonly MailpitBuild Linux = new(
        "1.32.0",
        new Uri("https://github.com/axllent/mailpit/releases/download/v1.32.0/mailpit-linux-amd64.tar.gz"),
        "4b5993c978e8604c70d43927e2ad103b311bba3b1822fa0cf1d4ee4ec890a93c");
}

/// <summary>Un Mailpit instalado por Roost, en <c>bin/mailpit/&lt;versión&gt;</c>.</summary>
public sealed record InstalledMailpit(string Version, string Directory)
{
    public string Executable => Path.Combine(Directory, MailpitInstaller.ExecutableName);

    public string Label => $"Mailpit {Version}";
}

/// <summary>
/// Instala Mailpit con las reglas de siempre (ver <see cref="InstallFiles"/>). Quitarlo solo quita
/// el ejecutable: la bandeja de <c>data/mailpit</c> no la borra nada en Roost.
/// </summary>
public sealed class MailpitInstaller
{
    public static string ExecutableName => OsNames.Exe("mailpit");

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public MailpitInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    public InstalledMailpit? Find(MailpitBuild build)
    {
        var dir = Path.Combine(_paths.MailpitBin, build.Version);
        return File.Exists(Path.Combine(dir, ExecutableName)) ? new InstalledMailpit(build.Version, dir) : null;
    }

    /// <summary>Los Mailpit instalados, el más nuevo primero. Las carpetas de trabajo (con punto) no cuentan.</summary>
    public IReadOnlyList<InstalledMailpit> ListInstalled()
    {
        if (!System.IO.Directory.Exists(_paths.MailpitBin)) return [];
        return System.IO.Directory.EnumerateDirectories(_paths.MailpitBin)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Where(x => !x.Name.StartsWith('.') && File.Exists(Path.Combine(x.Dir, ExecutableName)))
            .Select(x => (x.Dir, x.Name, Version: ComponentVersion.TryParse(x.Name, out var v) ? v : default))
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => new InstalledMailpit(x.Name, x.Dir))
            .ToList();
    }

    /// <summary>El que usa el stack: el de esta versión de Roost o, si no está, el más nuevo instalado.</summary>
    public InstalledMailpit? Locate() => Find(MailpitCatalog.Current) ?? ListInstalled().FirstOrDefault();

    public async Task<InstalledMailpit> InstallAsync(
        MailpitBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("MailpitInstaller sin HttpClient no puede instalar.");
        var target = Path.Combine(_paths.MailpitBin, build.Version);
        if (Find(build) is not null)
            throw new RoostException($"Mailpit {build.Version} ya esta instalado en {target}.");

        InstallFiles.CleanLeftovers(_paths.MailpitBin);
        if (System.IO.Directory.Exists(target))
            InstallFiles.RemoveAtomic(target, _paths.MailpitBin, $"la carpeta incompleta de Mailpit {build.Version}");

        var zip = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // El zip (y el tar.gz de Linux) trae el ejecutable, el LICENSE y el README en la raíz.
        InstallFiles.ExtractAtomic(zip, target, ExecutableName, stripSingleRoot: true, build.FileName);
        InstallFiles.MarkExecutable(Path.Combine(target, ExecutableName));
        return new InstalledMailpit(build.Version, target);
    }

    /// <summary>Quita el ejecutable. La bandeja (<c>data/mailpit</c>) se queda.</summary>
    public void Remove(InstalledMailpit mailpit) => InstallFiles.RemoveAtomic(mailpit.Directory, _paths.MailpitBin, mailpit.Label);
}
