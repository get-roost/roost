using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// La CA en los almacenes NSS de Linux, con un certutil de pega (el runner falso) sobre una home
/// temporal: qué almacenes se recorren, cómo se crea el de Chrome, cómo se añade (el PEM por stdin),
/// cómo se sabe si está (la huella) y cómo se quita. Nunca el certutil ni los perfiles de verdad.
/// </summary>
public class NssTrustTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "roost-nss-" + Guid.NewGuid().ToString("N"));
    private const string Certutil = "/usr/bin/certutil";

    private string MainDb => Path.Combine(_home, ".pki", "nssdb");

    private string FakeFirefoxProfile(string root, string name)
    {
        var profile = Path.Combine(root, name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(root, "profiles.ini"), $"[Profile0]\nName={name}\nIsRelative=1\nPath={name}\n");
        return profile;
    }

    private static X509Certificate2 NewCa(string cn = "Roost Local CA (ana@pc)")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    private static string Db(string path, string rest) => $"-d sql:{path} {rest}";

    [Fact]
    public void Databases_ElDeChromeSiempre_YLosDemasSoloSiExisten()
    {
        var firefox = FakeFirefoxProfile(Path.Combine(_home, ".mozilla", "firefox"), "abcd.default-release");
        File.WriteAllText(Path.Combine(firefox, "cert9.db"), "");
        FakeFirefoxProfile(Path.Combine(_home, "snap", "firefox", "common", ".mozilla", "firefox"), "snap.default");   // sin cert9.db: aún no abierto
        var snapChromium = Path.Combine(_home, "snap", "chromium", "current", ".pki", "nssdb");
        Directory.CreateDirectory(snapChromium);
        File.WriteAllText(Path.Combine(snapChromium, "cert9.db"), "");
        var flatpak = Path.Combine(_home, ".var", "app", "com.brave.Browser", ".pki", "nssdb");
        Directory.CreateDirectory(flatpak);
        File.WriteAllText(Path.Combine(flatpak, "cert9.db"), "");

        var dbs = new NssTrust(new FakeCommandRunner(), _home, Certutil).Databases();

        Assert.Equal([MainDb, snapChromium, flatpak, firefox], dbs.Select(d => d.Path));
        Assert.Equal(["Chrome, Chromium, Edge y Brave", "Chromium (snap)", "com.brave.Browser (flatpak)", "Firefox (abcd.default-release)"], dbs.Select(d => d.Label));
        Assert.True(dbs[0].IsMain);
        Assert.False(dbs[0].Exists);
    }

    [Fact]
    public void Trust_CreaElDeChromeSiFalta_YAnadeElPemPorStdin_EnCadaAlmacen()
    {
        var firefox = FakeFirefoxProfile(Path.Combine(_home, ".mozilla", "firefox"), "abcd.default-release");
        File.WriteAllText(Path.Combine(firefox, "cert9.db"), "");
        using var ca = NewCa();
        var runner = new FakeCommandRunner()
            .On(Db(MainDb, "-N --empty-password"), onRun: _ => File.WriteAllText(Path.Combine(MainDb, "cert9.db"), ""))
            .On(Db(MainDb, "-L -n Roost Local CA -a"), exitCode: 255, stderr: "certutil: Could not find cert: Roost Local CA")
            .On(Db(firefox, "-L -n Roost Local CA -a"), exitCode: 255, stderr: "certutil: Could not find cert: Roost Local CA")
            .On(Db(MainDb, "-A -n Roost Local CA -t C,,"))
            .On(Db(firefox, "-A -n Roost Local CA -t C,,"));

        var report = new NssTrust(runner, _home, Certutil).Trust(ca);

        Assert.Equal(["Chrome, Chromium, Edge y Brave", "Firefox (abcd.default-release)"], report.Stores);
        Assert.Empty(report.Skipped);
        var adds = runner.Calls.Where(c => c.Arguments.Contains("-A")).ToList();
        Assert.Equal(2, adds.Count);
        Assert.All(adds, add =>
        {
            Assert.Equal(Certutil, add.Exe);
            Assert.Equal(ca.ExportCertificatePem(), add.Stdin);   // el PEM va por stdin, sin ficheros temporales
            Assert.Equal("C,,", add.Arguments[add.Arguments.ToList().IndexOf("-t") + 1]);
        });
        Assert.Equal(Db(MainDb, "-N --empty-password"), string.Join(' ', runner.Calls[0].Arguments));   // primero se crea
    }

    [Fact]
    public void Trust_ConFirefoxAbierto_EsePerfilSeSalta_YSeDice()
    {
        var firefox = FakeFirefoxProfile(Path.Combine(_home, ".mozilla", "firefox"), "abcd.default-release");
        File.WriteAllText(Path.Combine(firefox, "cert9.db"), "");
        Directory.CreateDirectory(MainDb);
        File.WriteAllText(Path.Combine(MainDb, "cert9.db"), "");
        using var ca = NewCa();
        var runner = new FakeCommandRunner()
            .On("-d sql:", exitCode: 255)   // -L: no hay nada
            .On(Db(MainDb, "-A"))
            .On(Db(firefox, "-A"), exitCode: 255, stderr: "certutil: could not add certificate to token or database: SEC_ERROR_BUSY");

        var report = new NssTrust(runner, _home, Certutil).Trust(ca);

        Assert.Equal(["Chrome, Chromium, Edge y Brave"], report.Stores);
        var skipped = Assert.Single(report.Skipped);
        Assert.Contains("SEC_ERROR_BUSY", skipped);
        Assert.Contains("cierra Firefox", skipped);
    }

    [Fact]
    public void IsTrusted_ComparaLaHuella_YUnaCaDeRoostAnterior_SeSustituye()
    {
        Directory.CreateDirectory(MainDb);
        File.WriteAllText(Path.Combine(MainDb, "cert9.db"), "");
        using var ca = NewCa();
        using var old = NewCa();
        var listed = old.ExportCertificatePem();
        var runner = new FakeCommandRunner();
        runner.On(Db(MainDb, "-L -n Roost Local CA -a"), stdout: listed)
            .On(Db(MainDb, "-D -n Roost Local CA"), onRun: _ => runner.On(Db(MainDb, "-L -n Roost Local CA -a"), exitCode: 255))
            .On(Db(MainDb, "-A"), onRun: _ => runner.On(Db(MainDb, "-L -n Roost Local CA -a"), stdout: ca.ExportCertificatePem()));
        var nss = new NssTrust(runner, _home, Certutil);

        Assert.False(nss.IsTrusted(ca));   // la que hay con ese nombre es otra
        Assert.True(nss.IsTrusted(old));

        nss.Trust(ca);

        // Tras los dos IsTrusted: listar, quitar la vieja, comprobar que ya no está, mirar si está la nueva, añadirla.
        Assert.Equal(["-L", "-D", "-L", "-L", "-A"], runner.Calls.Skip(2).Select(c => c.Arguments[2]));
        Assert.True(nss.IsTrusted(ca));
    }

    [Fact]
    public void Untrust_QuitaDondeEste_YDejaLoQueNoEsSuyo()
    {
        var firefox = FakeFirefoxProfile(Path.Combine(_home, ".mozilla", "firefox"), "abcd.default-release");
        File.WriteAllText(Path.Combine(firefox, "cert9.db"), "");
        Directory.CreateDirectory(MainDb);
        File.WriteAllText(Path.Combine(MainDb, "cert9.db"), "");
        using var ca = NewCa();
        using var other = NewCa("Roost Local CA (otro@pc)");
        var runner = new FakeCommandRunner();
        runner.On(Db(MainDb, "-L -n Roost Local CA -a"), stdout: ca.ExportCertificatePem())
            .On(Db(firefox, "-L -n Roost Local CA -a"), stdout: other.ExportCertificatePem())
            .On(Db(MainDb, "-D -n Roost Local CA"), onRun: _ => runner.On(Db(MainDb, "-L -n Roost Local CA -a"), exitCode: 255));

        var report = new NssTrust(runner, _home, Certutil).Untrust(ca);

        Assert.Equal(["Chrome, Chromium, Edge y Brave"], report.Stores);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("-D") && c.Arguments[1] == "sql:" + firefox);   // la de otro, intacta
    }

    [Fact]
    public void SinCertutil_IsTrustedEsFalse_YTrustDiceElPaquete()
    {
        using var ca = NewCa();
        var nss = new NssTrust(new FakeCommandRunner(), _home, certutil: NssTrust.NoCertutil);

        Assert.False(nss.IsTrusted(ca));
        var ex = Assert.Throws<RoostException>(() => nss.Trust(ca));
        Assert.Contains("libnss3-tools", ex.Message);
    }

    [Fact]
    public void LocalCa_DelegaEnSuAlmacen()
    {
        var paths = new RoostPaths(Path.Combine(_home, "roost"), Path.Combine(_home, "cache"));
        var store = new FakeStore();
        var ca = new LocalCa(paths) { Store = store };

        Assert.False(ca.IsTrusted());   // aún no hay CA
        Assert.Equal(["de pega"], ca.Trust().Stores);
        Assert.True(ca.IsTrusted());
        Assert.Equal(["de pega"], ca.Untrust().Stores);
        Assert.False(ca.IsTrusted());
    }

    [Fact]
    public void PemBlocks_SacaCadaCertificado_DeLaSalidaDeCertutil()
    {
        using var a = NewCa();
        using var b = NewCa();
        var text = "Certificate:\n" + a.ExportCertificatePem() + "\n\n" + b.ExportCertificatePem() + "\n";
        Assert.Equal([a.ExportCertificatePem(), b.ExportCertificatePem()], NssTrust.PemBlocks(text));
        Assert.Empty(NssTrust.PemBlocks("-----BEGIN CERTIFICATE-----\nsin cierre"));
    }

    private sealed class FakeStore : ITrustStore
    {
        private string? _thumbprint;
        public string Description => "el almacen de pega";
        public bool IsTrusted(X509Certificate2 ca) => _thumbprint == ca.Thumbprint;
        public TrustReport Trust(X509Certificate2 ca) { _thumbprint = ca.Thumbprint; return new TrustReport(["de pega"], []); }
        public TrustReport Untrust(X509Certificate2 ca) { _thumbprint = null; return new TrustReport(["de pega"], []); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }
}
