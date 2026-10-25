using System.Text.RegularExpressions;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Install;

/// <summary>
/// Un Apache: en Windows, el zip de Apache Lounge con el SHA-256 que tiene que dar; en Linux, el de la
/// distro (<see cref="IsDistro"/>: no se descarga nada, lo trae apt).
/// </summary>
public sealed record ApacheBuild(string Version, Uri Url, string Sha256, string Redistributable)
{
    public string FileName => Path.GetFileName(Url.AbsolutePath);

    /// <summary>true si es el Apache de la distro (Linux): Roost no lo descarga ni lo instala, solo lo encuentra.</summary>
    public bool IsDistro => Sha256.Length == 0;
}

/// <summary>
/// Apache fijado a mano, como nginx, mientras no exista el feed roost-catalog. Apache no publica
/// binarios para Windows: los de Apache Lounge son el estándar de hecho. Los firma Steffen Land
/// con PGP; el SHA-256 de aquí es el de un zip cuya firma se ha verificado con esa clave. En Linux
/// es el <c>apache2</c> (Debian, Ubuntu) o <c>httpd</c> (Fedora, RHEL) de la distro (decisión del plan
/// de Linux): Roost no instala paquetes del sistema, lo lanza como el usuario con su propio httpd.conf
/// y los módulos de la distro (<see cref="LinuxApache"/>).
/// </summary>
public static class ApacheCatalog
{
    /// <summary>El de este sistema: el zip de Apache Lounge en Windows; el de la distro en el resto.</summary>
    public static ApacheBuild Current => OperatingSystem.IsWindows() ? Windows : Linux;

    // httpd-2.4.69-261002-Win64-VS18.zip: firma PGP verificada el 23-oct-2026 con la clave de
    // Steffen Land (Apache Lounge), 3D49 885E ADE8 BC39 9F46 D5DD BEE8 8A78 29C1 7558, la misma
    // en apachelounge.com/download/Steffen_Land.asc y en keyserver.ubuntu.com. Para cambiar de
    // versión: bajar el zip y su .asc, gpg --verify con esa clave y fijar aquí el SHA-256.
    public static readonly ApacheBuild Windows = new(
        "2.4.69",
        new Uri("https://www.apachelounge.com/download/VS18/binaries/httpd-2.4.69-261002-Win64-VS18.zip"),
        "9f1f93cab9a0c6dc5e628aa640784bb30347f93fcd01b46a4c0d6b5ba76bf4e2",
        "Visual C++ Redistributable 2017-2026 (14.50 o posterior)");

    // El de la distro: todas traen la rama 2.4 (la versión exacta la dice InstalledApache.Version, leída
    // con -v); sin SHA-256 porque no se descarga. El «redistributable» es el propio paquete.
    public static readonly ApacheBuild Linux = new(
        "2.4",
        new Uri("https://httpd.apache.org/"),
        "",
        "el paquete apache2 de la distro (" + LinuxApache.InstallHint + ")");
}

/// <summary>
/// Un Apache instalado por Roost en <c>bin/apache/&lt;versión&gt;</c> o, en Linux, el de la distro
/// (<see cref="DistroExecutable"/>): entonces <see cref="Directory"/> es la carpeta que tiene sus
/// <c>modules/</c> (<c>/usr/lib/apache2</c>) y los tipos MIME son los del sistema.
/// </summary>
public sealed record InstalledApache(string Version, string Directory)
{
    /// <summary>Linux: el ejecutable de la distro (<c>/usr/sbin/apache2</c> o <c>/usr/sbin/httpd</c>); null = el de Roost, en <c>bin/httpd.exe</c>.</summary>
    public string? DistroExecutable { get; init; }

    public bool IsDistro => DistroExecutable is not null;

    public string Executable => DistroExecutable ?? Path.Combine(Directory, "bin", ApacheInstaller.ExecutableName);
    public string MimeTypes => IsDistro ? LinuxApache.MimeTypes : Path.Combine(Directory, "conf", "mime.types");
    // Las rutas de la distro son de Linux: con '/' siempre (también en los tests, que corren en Windows).
    public string Modules => IsDistro ? Directory + "/modules" : Path.Combine(Directory, "modules");
}

/// <summary>
/// El Apache de la distro en Linux: dónde está (Debian y Ubuntu ponen <c>apache2</c> con sus módulos
/// en <c>/usr/lib/apache2/modules</c>; Fedora y RHEL, <c>httpd</c> en <c>/usr/lib64/httpd/modules</c>),
/// su versión (<c>-v</c>, una vez: se recuerda por ruta y fecha) y los avisos: el servicio del sistema
/// (<c>apache2.service</c>, que al instalar el paquete queda habilitado en el 80 y el 443) y los puertos
/// bajos, que a este Apache no se le pueden dar con <c>setcap</c> (es un binario del sistema): como
/// usuario, solo puertos de 1024 en adelante.
/// </summary>
public static partial class LinuxApache
{
    /// <summary>Los sitios donde lo deja cada familia de distros: el ejecutable y la carpeta que tiene <c>modules/</c>.</summary>
    public static readonly IReadOnlyList<(string Executable, string ModulesParent)> Layouts =
    [
        ("/usr/sbin/apache2", "/usr/lib/apache2"),
        ("/usr/sbin/httpd", "/usr/lib64/httpd"),
        ("/usr/sbin/httpd", "/usr/lib/httpd"),
    ];

    /// <summary>Los tipos MIME del sistema (el <c>TypesConfig</c> de la propia distro).</summary>
    public const string MimeTypes = "/etc/mime.types";

    /// <summary>Cómo se instala, sin tildes (lo enseña la CLI).</summary>
    public const string InstallHint = "sudo apt install apache2 en Debian y Ubuntu; sudo dnf install httpd en Fedora y RHEL";

    /// <summary>PURO: el Apache de la distro según qué ficheros existen, o null si no está. <paramref name="version"/> da la versión del ejecutable.</summary>
    public static InstalledApache? Locate(Func<string, bool> fileExists, Func<string, bool> directoryExists, Func<string, string> version)
    {
        foreach (var (executable, parent) in Layouts)
        {
            if (fileExists(executable) && directoryExists(parent + "/modules"))
                return new InstalledApache(version(executable), parent) { DistroExecutable = executable };
        }
        return null;
    }

    /// <summary>PURO: «2.4.58» de «Server version: Apache/2.4.58 (Ubuntu)»; null si no se lee.</summary>
    public static string? ParseVersion(string? output)
    {
        if (output is null) return null;
        var match = ServerVersion().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Apache/(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ServerVersion();

    private static readonly object VersionLock = new();
    private static (string Executable, DateTime Written, string Version)? s_version;

    /// <summary>La versión del ejecutable (<c>-v</c>), recordada mientras el fichero no cambie; «2.4» si no contesta.</summary>
    public static string Version(string executable, IProcessRunner runner)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(executable); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (VersionLock)
        {
            if (s_version is { } cached && cached.Executable == executable && cached.Written == written) return cached.Version;
            var version = ParseVersion(runner.Run(executable, "-v", TimeSpan.FromSeconds(10))) ?? ApacheCatalog.Linux.Version;
            s_version = (executable, written, version);
            return version;
        }
    }

    /// <summary>
    /// PURO: el aviso si el servicio del sistema de Apache arranca con el sistema o está en marcha (es
    /// otro Apache, con su configuración, en el 80 y el 443; el de Roost va con tu usuario en los puertos
    /// de roost.toml, así que no se pisan salvo que sean los mismos). null si no hay tal servicio. Sin tildes.
    /// </summary>
    public static string? ServiceWarning(IEnumerable<WindowsService> services, int httpPort, int httpsPort)
    {
        var service = services.FirstOrDefault(s => s.Kind == "Apache" && (s.StartsWithWindows || s.Running));
        if (service is null) return null;
        var unit = SystemdServices.UnitName(service);
        var clash = service.UsualPorts.Intersect([httpPort, httpsPort]).ToList();
        var ports = clash.Count > 0
            ? $" Roost quiere el {string.Join(" y el ", clash)}, que es suyo: cambia [web] port/https_port en roost.toml o para ese servicio."
            : $" El de Roost va con tu usuario en el {httpPort} y el {httpsPort}, asi que no se pisan.";
        return $"El servicio del sistema {SystemdServices.Label(service)} {SystemdServices.StateText(service)}: es otro Apache, con su propia configuracion, en el 80 y el 443.{ports} "
            + $"Si no lo quieres, sudo systemctl disable --now {unit} (Roost no toca servicios del sistema).";
    }

    /// <summary>
    /// PURO: el mensaje de un puerto bajo con Apache en Linux (sin tildes): no esta ocupado, es que el usuario
    /// no puede abrirlo, y a este Apache no se le da el permiso con setcap. Lo que toca es un puerto alto
    /// (<paramref name="key"/> = <paramref name="suggested"/> en <paramref name="configFile"/>) o nginx.
    /// </summary>
    public static string DeniedPortMessage(int port, string key, int suggested, string configFile) =>
        $"El puerto {port} esta por debajo del 1024 y Apache (el de la distro) corre con tu usuario, que no puede abrirlo (no es que este ocupado); "
        + $"a un binario del sistema no se le da el permiso con setcap. Usa un puerto alto: {key} = {suggested} en {configFile}. "
        + "Para el 80 y el 443 sin root, nginx (roost web use nginx): a el si se le da el permiso.";
}

/// <summary>
/// Instala Apache con las mismas reglas que PHP y nginx (ver <see cref="InstallFiles"/>). En Linux no
/// instala nada: <see cref="Find"/> da el de la distro si está, e <see cref="InstallAsync"/> dice cómo
/// ponerlo con apt.
/// </summary>
public sealed class ApacheInstaller
{
    public const string ExecutableName = "httpd.exe";

    /// <summary>La carpeta del zip de Apache Lounge que es Apache; al lado trae un ReadMe y poco más.</summary>
    public const string ZipFolder = "Apache24";

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;
    private readonly IProcessRunner _runner;

    /// <param name="http">Solo hace falta para instalar.</param>
    /// <param name="runner">Linux: con el que se pregunta la versión al Apache de la distro (<c>-v</c>).</param>
    public ApacheInstaller(RoostPaths paths, HttpClient? http = null, IProcessRunner? runner = null)
    {
        _paths = paths;
        _http = http;
        _runner = runner ?? ProcessRunner.Default;
    }

    public InstalledApache? Find(ApacheBuild build)
    {
        if (build.IsDistro)
            return LinuxApache.Locate(File.Exists, Directory.Exists, exe => LinuxApache.Version(exe, _runner));
        var dir = Path.Combine(_paths.ApacheBin, build.Version);
        return File.Exists(Path.Combine(dir, "bin", ExecutableName)) ? new InstalledApache(build.Version, dir) : null;
    }

    public async Task<InstalledApache> InstallAsync(
        ApacheBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        if (build.IsDistro)
        {
            return Find(build) ?? throw new RoostException(
                "Apache no esta instalado en esta distro, y Roost no instala paquetes del sistema. "
                + $"Instalalo tu ({LinuxApache.InstallHint}) y repite: Roost lo arranca con tu usuario y su propia configuracion.");
        }

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
