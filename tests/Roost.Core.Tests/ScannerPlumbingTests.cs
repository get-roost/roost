using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Scanning.Scanners;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el "fontanero" de <see cref="ComponentScannerBase"/> (existencia de rutas,
/// deduplicado, propagación de la salida al parser) SIN ejecutar binarios reales:
/// se inyecta un <see cref="IProcessRunner"/> falso con salidas de ejemplo.
/// </summary>
public class ScannerPlumbingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _realExe;

    public ScannerPlumbingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "roost-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _realExe = Path.Combine(_dir, OperatingSystem.IsWindows() ? "php.exe" : "php");
        File.WriteAllText(_realExe, string.Empty);
    }

    private sealed class FakeRunner(string? output) : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => output;
    }

    // Scanner mínimo que devuelve los candidatos que le pasemos; el resto es la base real.
    private sealed class StubPhpScanner(IProcessRunner runner, IEnumerable<Candidate> candidates)
        : ComponentScannerBase(runner)
    {
        public override ComponentKind Kind => ComponentKind.Php;
        public override bool SupportsCurrentOs => true;
        protected override string VersionArguments => "-v";
        protected override IReadOnlyList<string> PathExecutableNames => ["php.exe", "php"];
        protected override IEnumerable<Candidate> EnumerateCandidates() => candidates;
        protected override DetectedComponent? Build(Candidate c, string? output, bool isOnPath) =>
            ComponentParser.ParsePhp(output, c.Path, c.Source, isOnPath);
    }

    [Fact]
    public void Scan_IgnoraRutasInexistentes_YConstruyeDesdeElBinarioReal()
    {
        var runner = new FakeRunner("PHP 8.3.14 (cli) (built: ...) (NTS)\n");
        var candidatos = new[]
        {
            new Candidate(Path.Combine(_dir, "no-existe.exe"), "PATH"),
            new Candidate(_realExe, "Laragon"),
        };

        var found = new StubPhpScanner(runner, candidatos).Scan().ToList();

        Assert.Single(found);
        Assert.Equal(ComponentKind.Php, found[0].Kind);
        Assert.Equal("Laragon", found[0].Source);
        Assert.Equal(Path.GetFullPath(_realExe), found[0].ExecutablePath);
        Assert.Equal(8, found[0].Version.Major);
    }

    [Fact]
    public void Scan_DeduplicaElMismoBinarioVistoPorDosFuentes()
    {
        var runner = new FakeRunner("PHP 8.2.20 (cli) (NTS)\n");
        var candidatos = new[]
        {
            new Candidate(_realExe, "PATH"),
            new Candidate(_realExe, "Laragon"), // misma ruta, otra fuente
        };

        var found = new StubPhpScanner(runner, candidatos).Scan().ToList();

        Assert.Single(found); // una sola entrada pese a dos candidatos
    }

    [Fact]
    public void Scan_BinarioQueNoResponde_NoProduceComponente()
    {
        var runner = new FakeRunner(null); // simula timeout / fallo
        var candidatos = new[] { new Candidate(_realExe, "PATH") };

        Assert.Empty(new StubPhpScanner(runner, candidatos).Scan());
    }

    [Fact]
    public void CatalogoPorDefecto_RegistraLosTresScannersEnEsteSo()
    {
        var scanners = ScannerCatalog.CreateDefault();

        Assert.Equal(3, scanners.Count);
        Assert.Contains(scanners, s => s.Kind == ComponentKind.Php);
        Assert.Contains(scanners, s => s.Kind == ComponentKind.MySql);
        Assert.Contains(scanners, s => s.Kind == ComponentKind.MariaDb);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
