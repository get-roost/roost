using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Scanning.Scanners;

namespace Roost.Core.Tests;

/// <summary>
/// Lo que el scanner hacía mal en Linux: los PHP de /usr/local y /opt salían como de la distro
/// (ámbar siempre), con usrmerge el mismo binario salía dos veces (/bin es un enlace a usr/bin)
/// y el cliente `mysql` contaba como servidor. Sin binarios reales ni enlaces reales.
/// </summary>
public class LinuxScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-linux-" + Guid.NewGuid().ToString("N"));

    // Los enlaces de una Debian con usrmerge y alternatives.
    private static readonly Dictionary<string, string> Links = new()
    {
        ["/bin"] = "usr/bin",
        ["/sbin"] = "usr/sbin",
        ["/usr/bin/php"] = "/etc/alternatives/php",
        ["/etc/alternatives/php"] = "/usr/bin/php8.3",
        ["/usr/local/bin/php"] = "../lib/php/bin/php",
        ["/loop/a"] = "/loop/b",
        ["/loop/b"] = "/loop/a",
    };

    private static string Resolve(string path) => CanonicalPath.Resolve(path, p => Links.GetValueOrDefault(p));

    [Theory]
    [InlineData("/bin/php", "/usr/bin/php8.3")]             // /bin → usr/bin, y luego alternatives
    [InlineData("/usr/bin/php", "/usr/bin/php8.3")]
    [InlineData("/sbin/mysqld", "/usr/sbin/mysqld")]
    [InlineData("/usr/local/bin/php", "/usr/local/lib/php/bin/php")]   // destino relativo con ..
    [InlineData("/usr/bin/./php8.3", "/usr/bin/php8.3")]
    [InlineData("/opt/php/bin/php", "/opt/php/bin/php")]     // sin enlaces, igual
    [InlineData("/loop/a", "/loop/a")]                       // un bucle se deja como estaba
    public void RutaReal_ResuelveLosEnlacesDeTodosLosComponentes(string path, string expected)
        => Assert.Equal(expected, Resolve(path));

    [Theory]
    [InlineData("/usr/bin/php8.3", "system")]
    [InlineData("/usr/sbin/mysqld", "system")]
    [InlineData("/opt/remi/php83/root/usr/bin/php", "system")]
    [InlineData("/usr/local/bin/php", "/usr/local")]          // Docker php:*, compilados a mano
    [InlineData("/usr/local/mysql/bin/mysqld", "/usr/local")] // el tarball oficial
    [InlineData("/opt/php-8.4/bin/php", "/opt")]
    [InlineData("/opt/homebrew/bin/php", "Homebrew")]
    public void Origen_SoloEsSystemLoQuePoneLaDistro(string path, string source)
        => Assert.Equal(source, PosixOrigin.Of(path));

    private static DetectedComponent Php(string source, string raw = "8.3.18") => new()
    {
        Kind = ComponentKind.Php,
        Version = ComponentVersion.TryParse(raw, out var v) ? v : default,
        ExecutablePath = "/usr/local/bin/php",
        Source = source,
    };

    [Fact]
    public void UnPhpDeUsrLocal_SeEvaluaConCerteza_YElDeLaDistroSigueEnAmbar()
    {
        var evaluator = new AuditEvaluator(AuditDataset.LoadEmbedded(), new DateOnly(2026, 10, 22));

        var docker = evaluator.Evaluate(Php("/usr/local"));
        Assert.False(docker.Uncertain);
        Assert.Equal(AuditColor.Red, docker.Color);
        Assert.True(docker.IsBlocking);

        var distro = evaluator.Evaluate(Php("system"));
        Assert.True(distro.Uncertain);
        Assert.Equal(AuditColor.Amber, distro.Color);

        // Un sufijo de distro da ámbar esté donde esté.
        Assert.True(evaluator.Evaluate(Php("/usr/local", "8.3.18+deb12u1")).Uncertain);
    }

    [Theory]
    [InlineData("mysql  Ver 8.0.39 for Linux on x86_64 (MySQL Community Server - GPL)")]
    [InlineData("mysql  Ver 15.1 Distrib 10.6.18-MariaDB, for debian-linux-gnu (x86_64) using  EditLine wrapper")]
    [InlineData("mariadb from 11.4.2-MariaDB, client 15.2 for Linux (x86_64) using readline 5.1")]
    [InlineData(@"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysql.exe  Ver 8.0.39 for Win64 on x86_64 (MySQL Community Server - GPL)")]
    [InlineData("mysqladmin  Ver 8.0.39 for Linux on x86_64 (MySQL Community Server - GPL)")]
    public void UnCliente_NoEsUnaInstalacion(string output)
    {
        Assert.True(ComponentParser.IsClient(output));
        var kind = output.Contains("MariaDB") ? ComponentKind.MariaDb : ComponentKind.MySql;
        Assert.Null(ComponentParser.ParseDatabase(output, kind, "/usr/bin/mysql", "PATH", false));
    }

    [Theory]
    [InlineData("/usr/sbin/mysqld  Ver 8.0.39-0ubuntu0.22.04.1 for Linux on x86_64 ((Ubuntu))", ComponentKind.MySql)]
    [InlineData("/usr/sbin/mariadbd  Ver 10.11.6-MariaDB-0+deb12u1 for debian-linux-gnu on x86_64 (Debian 12)", ComponentKind.MariaDb)]
    [InlineData(@"C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe  Ver 8.0.39 for Win64 on x86_64 (MySQL Community Server - GPL)", ComponentKind.MySql)]
    [InlineData("mariadbd  Ver 11.4.2-MariaDB for Linux on x86_64 (MariaDB Server)", ComponentKind.MariaDb)]
    public void UnServidor_SiCuenta(string output, ComponentKind kind)
    {
        Assert.False(ComponentParser.IsClient(output));
        Assert.NotNull(ComponentParser.ParseDatabase(output, kind, "/usr/sbin/mysqld", "system", false));
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => "PHP 8.3.18 (cli) (NTS)\n";
    }

    /// <summary>Un scanner con dos candidatos que son el mismo binario por enlaces distintos.</summary>
    private sealed class UsrMergeScanner(IEnumerable<Candidate> candidates, Func<string, string> realPath)
        : ComponentScannerBase(new FakeRunner())
    {
        public override ComponentKind Kind => ComponentKind.Php;
        public override bool SupportsCurrentOs => true;
        protected override string VersionArguments => "-v";
        protected override IReadOnlyList<string> PathExecutableNames => ["php"];
        protected override IEnumerable<Candidate> EnumerateCandidates() => candidates;
        protected override string RealPath(string path) => realPath(path);
        protected override DetectedComponent? Build(Candidate c, string? output, bool isOnPath) =>
            ComponentParser.ParsePhp(output, c.Path, c.Source, isOnPath);
    }

    [Fact]
    public void ElMismoBinarioPorDosEnlaces_SaleUnaVez_ConElOrigenDelPrimero()
    {
        // En Windows no se pueden crear enlaces sin permisos: dos ficheros reales que la ruta
        // real (simulada) da por el mismo, como /usr/bin/php y /bin/php con usrmerge.
        Directory.CreateDirectory(_dir);
        var usrBin = Path.Combine(_dir, "usr-bin-php");
        var bin = Path.Combine(_dir, "bin-php");
        File.WriteAllText(usrBin, "");
        File.WriteAllText(bin, "");
        var scanner = new UsrMergeScanner(
            [new Candidate(usrBin, "system"), new Candidate(bin, "PATH")],
            p => Path.GetFileName(p) is "usr-bin-php" or "bin-php" ? "/usr/bin/php8.3" : p);

        var found = Assert.Single(scanner.Scan());

        Assert.Equal("system", found.Source);
        Assert.Equal(Path.GetFullPath(usrBin), found.ExecutablePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
