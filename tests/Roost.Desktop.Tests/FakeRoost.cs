using Roost.Core.Platform;
using Roost.Daemon;

namespace Roost.Desktop.Tests;

/// <summary>
/// Una raíz de Roost en una carpeta temporal con lo justo para que las pantallas tengan qué
/// enseñar: tres PHP (uno EOL), MariaDB, nginx, tres sitios y un roost.toml. Los "binarios"
/// son ficheros vacíos: nada los ejecuta.
/// </summary>
public sealed class FakeRoost : IDisposable
{
    public FakeRoost()
    {
        var root = Path.Combine(Path.GetTempPath(), "roost-desktop-" + Guid.NewGuid().ToString("N"));
        Paths = new RoostPaths(Path.Combine(root, "roost"), Path.Combine(root, "cache"), WwwDir: Path.Combine(root, "www"));

        foreach (var php in new[] { "8.4.27-nts-x64", "8.3.36-nts-x64", "7.4.33-nts-x64" })
        {
            Touch(Path.Combine(Paths.PhpBin, php, OsNames.Exe("php")));
            Touch(Path.Combine(Paths.PhpBin, php, OsNames.Exe("php-cgi")));
            foreach (var ext in new[] { "bz2", "curl", "exif", "fileinfo", "gd", "gettext", "intl", "ldap", "mbstring", "mysqli", "opcache", "openssl", "pdo_mysql", "pdo_pgsql", "pdo_sqlite", "pgsql", "soap", "sodium", "sqlite3", "xsl", "zip" })
                Touch(Path.Combine(Paths.PhpBin, php, "ext", $"php_{ext}.dll"));
        }
        Touch(Path.Combine(Paths.DbBin("mariadb"), "11.4.13", "bin", OsNames.Exe("mariadbd")));
        Touch(Path.Combine(Paths.NginxBin, "1.30.6", OsNames.Exe("nginx")));
        Write(Path.Combine(Paths.Logs, "roostd.log"), """
            2026-10-22 21:31:47 roostd en marcha (pid 69264)
            2026-10-22 21:31:48 mariadb-11.4: en marcha (pid 43988)
            2026-10-22 21:31:48 php-8.4-0: en marcha (pid 55564)
            2026-10-22 21:31:48 nginx: en marcha (pid 31532)
            2026-10-22 21:31:48 salud: todo responde en 0.5 s
            2026-10-22 22:14:40 php-7.4-2: se cayo a los 0.4 s (codigo 255); se relanza
            2026-10-22 22:14:41 php-7.4-2: en marcha (pid 47812)

            """);
        Write(Path.Combine(Paths.Logs, "nginx-error.log"), """
            2026/10/22 21:40:12 [warn] 31532#40188: *18 an upstream response is buffered to a temporary file
            2026/10/22 21:52:03 [error] 31532#40188: *41 FastCGI sent in stderr: "PHP message: PHP Warning:  Undefined variable $total"

            """);
        Write(Path.Combine(Paths.Logs, "nginx-access.log"), """
            127.0.0.1 - - [22/Oct/2026:21:52:03 +0200] "GET / HTTP/2.0" 200 1843

            """);
        Write(Path.Combine(Paths.Logs, "php-8.4-error.log"), "");
        Write(Paths.ConfigFile, "[php]\nversion = \"8.4\"\npool = 4\n\n[web]\nport = 80\nhttps_port = 443\n\n[db]\nengine = \"mariadb\"\nversion = \"11.4\"\nport = 3308\n");
        // Un proyecto con composer.json (^8.2) y public/, y un hosts de pruebas sin legacy.test.
        Directory.CreateDirectory(Path.Combine(root, "www", "tienda", "public"));
        Write(Path.Combine(root, "www", "tienda", "composer.json"), """{ "require": { "php": "^8.2" } }""");
        HostsFile = Path.Combine(root, "hosts");
        Write(HostsFile, """
            # roost:begin
            127.0.0.1 blog.test
            127.0.0.1 tienda.test
            # roost:end

            """);
        Environment.SetEnvironmentVariable(Roost.Core.Sites.HostsUpdater.HostsFileVariable, HostsFile);
        // Ni el Firefox de verdad: una carpeta sin profiles.ini (no hay Firefox).
        Environment.SetEnvironmentVariable(Roost.Core.Platform.FirefoxTrust.DirVariable, Path.Combine(root, "firefox"));

        Write(Path.Combine(Paths.State, "sites.json"), $$"""
            [
              { "name": "tienda", "folder": "{{Esc(Path.Combine(root, "www", "tienda"))}}", "root": "public" },
              { "name": "blog", "folder": "{{Esc(Path.Combine(root, "www", "blog"))}}" },
              { "name": "legacy", "folder": "{{Esc(Path.Combine(root, "proyectos", "legacy"))}}", "php": "7.4" }
            ]
            """);
    }

    public RoostPaths Paths { get; }

    /// <summary>La versión «instalada» en las pantallas (la última publicada en FakeUpstream es la 0.2.0).</summary>
    public const string Version = "0.1.0";

    /// <summary>El hosts de pruebas (ROOST_HOSTS_FILE): nada toca el del sistema.</summary>
    public string HostsFile { get; }

    /// <summary>La "red" de los tests: los catálogos de upstream servidos en memoria.</summary>
    public HttpClient Http => _http ??= new HttpClient(Upstream);

    private HttpClient? _http;

    internal FakeUpstream Upstream { get; } = new();

    /// <summary>Lo que contaría roostd con todo en marcha.</summary>
    public static StatusResponse Running => new(
        70324, true, "http://localhost/", "8.4.27-nts-x64", "1.30.6", "MariaDB 11.4.13", 3308,
        [
            new SiteDto("blog", "https://blog.test/", "8.4.27-nts-x64"),
            new SiteDto("legacy", "https://legacy.test/", "7.4.33-nts-x64"),
            new SiteDto("tienda", "https://tienda.test/", "8.4.27-nts-x64"),
        ],
        [
            new ServiceDto("mariadb-11.4", "Running", 7001, 0, 3308, null),
            .. Enumerable.Range(0, 4).Select(i => new ServiceDto($"php-8.4-{i}", "Running", 7100 + i, i == 0 ? 3 : 0, 9840 + i, null)),
            .. Enumerable.Range(0, 4).Select(i => new ServiceDto($"php-7.4-{i}", "Running", 7200 + i, 0, 9740 + i, null)),
            new ServiceDto("nginx", "Running", 7300, 0, 80, null),
        ],
        new DateTimeOffset(2026, 10, 23, 21, 14, 0, TimeSpan.FromHours(2)));

    // Composer y Sitios-2

    /// <summary>Un composer.phar «instalado» (de pega) en bin/composer: con él, la app no intenta descargarlo.</summary>
    public string InstallFakeComposer()
    {
        var phar = Path.Combine(Paths.ComposerBin, Roost.Core.Composer.ComposerCatalog.Stable.Version, Roost.Core.Composer.InstalledComposer.PharName);
        Touch(phar);
        return phar;
    }

    /// <summary>Un proyecto en la www con estos ficheros (vacíos; los que acaban en / son carpetas).</summary>
    public string Project(string name, params string[] files)
    {
        var dir = Path.Combine(Paths.Www, name);
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            var path = Path.Combine(dir, file.TrimEnd('/'));
            if (file.EndsWith('/')) Directory.CreateDirectory(path);
            else Write(path, file == "composer.json" ? """{ "require": { "php": "^8.2", "laravel/framework": "^11.0" } }""" : "");
        }
        return dir;
    }

    // Servicios (oleada 3)

    /// <summary>Redis y Mailpit «instalados» (de pega) y en el stack, con estos puertos.</summary>
    public void InstallFakeServices(int redisPort = 6379, int smtpPort = 1025, int webPort = 8025)
    {
        Touch(Path.Combine(Paths.RedisBin, "8.10.2", OsNames.Exe("redis-server")));
        Touch(Path.Combine(Paths.RedisBin, "8.10.2", OsNames.Exe("redis-cli")));
        Touch(Path.Combine(Paths.MailpitBin, "1.32.0", OsNames.Exe("mailpit")));
        File.AppendAllText(Paths.ConfigFile, $"\n[redis]\nenabled = true\nport = {redisPort}\n\n[mail]\nenabled = true\nsmtp_port = {smtpPort}\nweb_port = {webPort}\n");
    }

    /// <summary>Lo que contaría roostd con todo en marcha, también Redis y Mailpit (tras la base de datos).</summary>
    public static StatusResponse RunningWithServices(int redisPort = 6379, int smtpPort = 1025, int webPort = 8025)
    {
        var running = Running;
        return running with
        {
            Services = [running.Services[0], new ServiceDto("redis", "Running", 7010, 0, redisPort, null), new ServiceDto("mailpit", "Running", 7020, 0, smtpPort, null), .. running.Services.Skip(1)],
            Ports = [.. running.Services.Where(s => s.Port is not null).Select(s => s.Port!.Value), redisPort, smtpPort, webPort],
        };
    }

    private static string Esc(string path) => path.Replace("\\", "\\\\");

    private static void Touch(string file) => Write(file, "");

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Roost.Core.Sites.HostsUpdater.HostsFileVariable, null);
        Environment.SetEnvironmentVariable(Roost.Core.Platform.FirefoxTrust.DirVariable, null);
        try { Directory.Delete(Path.GetDirectoryName(Paths.Root)!, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Sirve un releases.json de windows.php.net con unas cuantas ramas, la API de MariaDB y la
/// última release de Roost en GitHub (la 0.2.0); lo demás, 404.
/// </summary>
internal sealed class FakeUpstream : HttpMessageHandler
{
    public const string RoostLatestUrl = "https://api.github.com/repos/get-roost/roost/releases/latest";

    // Actualizador: el «instalador» de la 0.2.0 es un fichero cualquiera (nada lo ejecuta), con su SHA-256 en el SHA256SUMS.txt.
    public const string RoostSetupUrl = "https://github.com/get-roost/roost/releases/download/v0.2.0/roost-0.2.0-win-x64-setup.exe";
    public const string RoostSumsUrl = "https://github.com/get-roost/roost/releases/download/v0.2.0/SHA256SUMS.txt";
    public const string RoostSetup = "MZ esto no es un instalador: es el setup de mentira de los tests del actualizador de Roost\n";
    public static string RoostSetupSha => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RoostSetup)));

    /// <summary>Actualizador: el SHA256SUMS.txt miente sobre el setup (para ver que entonces no se ejecuta nada).</summary>
    public bool CorruptSums { get; set; }

    /// <summary>
    /// Actualizador (A6): mientras no se complete, lo que daría 404 (el zip de nginx…) espera. Así
    /// da tiempo a pasar una instalación a segundo plano antes de que falle.
    /// </summary>
    public TaskCompletionSource? HoldMissing { get; set; }

    private static async Task<HttpResponseMessage> Held404Async(HttpRequestMessage request, Task hold)
    {
        await hold;
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private string Sums => $"{(CorruptSums ? new string('0', 64) : RoostSetupSha)}  roost-0.2.0-win-x64-setup.exe\n"
        + $"{new string('a', 64)}  roost-0.2.0-win-x64.zip\n{new string('b', 64)}  roost-0.2.0-linux-x64.tar.gz\n";

    private static readonly string RoostLatest = $$"""
        {
          "html_url": "https://github.com/get-roost/roost/releases/tag/v0.2.0",
          "tag_name": "v0.2.0",
          "name": "Roost 0.2.0",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-11-02T10:05:00Z",
          "assets": [
            { "name": "roost-0.2.0-win-x64-setup.exe", "size": {{System.Text.Encoding.UTF8.GetByteCount(RoostSetup)}}, "browser_download_url": "{{RoostSetupUrl}}" },
            { "name": "roost-0.2.0-win-x64.zip", "size": 52428800, "browser_download_url": "https://github.com/get-roost/roost/releases/download/v0.2.0/roost-0.2.0-win-x64.zip" },
            { "name": "roost-0.2.0-linux-x64.tar.gz", "size": 31457280, "browser_download_url": "https://github.com/get-roost/roost/releases/download/v0.2.0/roost-0.2.0-linux-x64.tar.gz" },
            { "name": "SHA256SUMS.txt", "size": 256, "browser_download_url": "{{RoostSumsUrl}}" }
          ],
          "body": "## Novedades\r\n\r\n- **nginx 1.30.7**, con el arreglo de [CVE-2026-1234](https://nginx.org/).\r\n- MySQL 8.4.12 y el dataset de avisos de noviembre.\r\n- `roost update` y el aviso de versiones nuevas en la app.\r\n"
        }
        """;

    /// <summary>Cuántas veces se ha preguntado a GitHub (para ver que la caché ahorra la red).</summary>
    public int RoostLatestRequests { get; private set; }

    private static string Branch(string version, string vs) => $$"""
        "{{version[..version.LastIndexOf('.')]}}": {
          "version": "{{version}}",
          "nts-{{vs}}-x64": { "zip": { "path": "php-{{version}}-nts-Win32-{{vs}}-x64.zip", "size": "32.5MB", "sha256": "{{new string('a', 64)}}" } },
          "ts-{{vs}}-x64": { "zip": { "path": "php-{{version}}-Win32-{{vs}}-x64.zip", "size": "32.7MB", "sha256": "{{new string('b', 64)}}" } },
          "nts-{{vs}}-x86": { "zip": { "path": "php-{{version}}-nts-Win32-{{vs}}-x86.zip", "size": "30.1MB", "sha256": "{{new string('c', 64)}}" } }
        }
        """;

    private static readonly string Releases = "{" + string.Join(",", Branch("8.5.12", "vs17"), Branch("8.4.27", "vs17"),
        Branch("8.3.36", "vs16"), Branch("8.2.35", "vs16"), Branch("8.1.34", "vs16"), Branch("7.4.33", "vc15")) + "}";

    // La API de MariaDB: las ramas mayores y, de cada una estable, su última versión.
    private static readonly (string Branch, string Latest, string Support, string Eol)[] MariaDb =
    [
        ("12.3", "12.3.3", "Long Term Support", "2029-06-12"),
        ("11.8", "11.8.6", "Long Term Support", "2028-06-04"),
        ("11.4", "11.4.13", "Long Term Support", "2029-05-29"),
        ("10.11", "10.11.19", "Long Term Support", "2028-02-16"),
    ];

    private static string MajorsJson => "{\"major_releases\": [" + string.Join(",", MariaDb.Select(m =>
        $$"""{"release_id": "{{m.Branch}}", "release_name": "{{m.Branch}}", "release_status": "Stable", "release_support_type": "{{m.Support}}", "release_eol_date": "{{m.Eol}}"}"""))
        + "]}";

    private static string LatestJson(string branch)
    {
        var latest = MariaDb.First(m => m.Branch == branch).Latest;
        return $$"""
            {"releases": {"{{latest}}": {"release_id": "{{latest}}", "files": [
              {"file_name": "mariadb-{{latest}}-winx64.zip", "package_type": "ZIP file", "checksum": {"sha256sum": "{{new string('d', 64)}}"},
               "file_download_url": "https://downloads.mariadb.org/rest-api/mariadb/{{latest}}/mariadb-{{latest}}-winx64.zip"}
            ] } } }
            """;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (url == RoostLatestUrl) RoostLatestRequests++;
        var body = url.EndsWith("releases.json", StringComparison.Ordinal) ? Releases
            : url == RoostLatestUrl ? RoostLatest
            : url == RoostSetupUrl ? RoostSetup
            : url == RoostSumsUrl ? Sums
            : url == "https://downloads.mariadb.org/rest-api/mariadb/" ? MajorsJson
            : MariaDb.FirstOrDefault(m => url == $"https://downloads.mariadb.org/rest-api/mariadb/{m.Branch}/latest/") is { Branch: not null } major ? LatestJson(major.Branch)
            : null;
        if (body is null && HoldMissing is { } hold) return Held404Async(request, hold.Task);
        var response = body is null
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
