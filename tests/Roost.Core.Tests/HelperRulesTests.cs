using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Core.Share;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Lo que decide qué admite roost-helper en Linux, compilado dentro del helper y probado aquí con
/// entradas hostiles: el PEM de <c>ca-trust</c> (CaCertRules), el drop-in de resolved y los ficheros
/// ajenos (ResolvedDropIn), las rutas y el ELF de <c>setcap-nginx</c>, el uid (HelperChecks) y las
/// órdenes de ufw y firewalld (FirewallRule). Nada toca el sistema.
/// </summary>
public class HelperRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-helper-rules-" + Guid.NewGuid().ToString("N"));

    // ---- CaCertRules ------------------------------------------------------------------------

    private static X509Certificate2 SelfSigned(string cn, bool ca = true, X509KeyUsageFlags usage = X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
        int years = 10, DateTimeOffset? notBefore = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={cn}, O=Roost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        return request.CreateSelfSigned(start, start.AddYears(years));
    }

    [Fact]
    public void CaCertRules_LaCaDeRoost_Pasa_YSeDevuelveLaMisma()
    {
        var ca = new LocalCa(new RoostPaths(_root, Path.Combine(_root, "cache")));
        using var created = ca.LoadOrCreate();

        using var parsed = CaCertRules.Parse(File.ReadAllText(ca.CaCertFile), out var problem);

        Assert.NotNull(parsed);
        Assert.Equal("", problem);
        Assert.Equal(created.Thumbprint, parsed.Thumbprint);
    }

    public static TheoryData<string, string> CasQueNoValen()
    {
        var data = new TheoryData<string, string>();
        using (var notCa = SelfSigned("Roost Local CA (ana@pc)", ca: false)) data.Add(notCa.ExportCertificatePem(), "no es una CA");
        using (var noSign = SelfSigned("Roost Local CA (ana@pc)", usage: X509KeyUsageFlags.DigitalSignature)) data.Add(noSign.ExportCertificatePem(), "KeyCertSign");
        using (var otherCn = SelfSigned("Acme Dev CA")) data.Add(otherCn.ExportCertificatePem(), "CN");
        using (var lookalike = SelfSigned("Roost Local CA")) data.Add(lookalike.ExportCertificatePem(), "CN");   // sin el «(»
        using (var tooLong = SelfSigned("Roost Local CA (ana@pc)", years: 20)) data.Add(tooLong.ExportCertificatePem(), "10 anos");
        using (var future = SelfSigned("Roost Local CA (ana@pc)", notBefore: DateTimeOffset.UtcNow.AddDays(3))) data.Add(future.ExportCertificatePem(), "en vigor");
        using (var expired = SelfSigned("Roost Local CA (ana@pc)", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-2))) data.Add(expired.ExportCertificatePem(), "en vigor");
        using (var fine = SelfSigned("Roost Local CA (ana@pc)"))
        {
            data.Add(fine.ExportCertificatePem() + "\n" + fine.ExportCertificatePem(), "exactamente un certificado");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            data.Add(fine.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem(), "ninguna clave");
            data.Add(fine.ExportCertificatePem() + new string(' ', CaCertRules.MaxPemLength), "16 KB");
        }
        data.Add("-----BEGIN CERTIFICATE-----\nno es base64 valido\n-----END CERTIFICATE-----\n", "legible");
        data.Add("", "exactamente un certificado");
        return data;
    }

    [Theory]
    [MemberData(nameof(CasQueNoValen))]
    public void CaCertRules_LoQueNoEsUnaCaDeRoost_SeRechazaConElMotivo(string pem, string reason)
    {
        using var parsed = CaCertRules.Parse(pem, out var problem);

        Assert.Null(parsed);
        Assert.Contains(reason, problem);
    }

    [Fact]
    public void CaCertRules_UnaCaConElNombreDeRoost_PeroFirmadaPorOtra_NoEsAutofirmada()
    {
        using var issuer = SelfSigned("Acme Root");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Roost Local CA (ana@pc), O=Roost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var serial = new byte[8];
        serial[0] = 1;
        using var impostor = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);

        using var parsed = CaCertRules.Parse(impostor.ExportCertificatePem(), out var problem);

        Assert.Null(parsed);
        Assert.Contains("autofirmado", problem);
    }

    // ---- ResolvedDropIn -------------------------------------------------------------------

    [Fact]
    public void ResolvedDropIn_ElContenidoEsFijo_YSeReconoce()
    {
        Assert.Equal("/etc/systemd/resolved.conf.d/roost.conf", ResolvedDropIn.FilePath);
        Assert.Contains("\n[Resolve]\nDNS=127.53.0.1:5353\nDomains=~test\n", ResolvedDropIn.Content);
        Assert.True(ResolvedDropIn.IsRoost(ResolvedDropIn.Content));
        Assert.False(ResolvedDropIn.IsRoost("# Roost\n[Resolve]\nDomains=~test\n"));   // una marca parecida no vale
        var (domains, servers) = ResolvedDropIn.Parse(ResolvedDropIn.Content);
        Assert.Equal(["~test"], domains);
        Assert.Equal(["127.53.0.1:5353"], servers);
    }

    [Fact]
    public void ResolvedDropIn_Parse_SumaLasAsignaciones_YUnaVaciaBorra_SoloEnResolve()
    {
        var (domains, servers) = ResolvedDropIn.Parse("""
            # comentario
            ; otro
            [Resolve]
            DNS=1.1.1.1 8.8.8.8
            Domains=~test lan
            Domains=
            Domains = ~docker
            DNS=9.9.9.9
            [Otra]
            Domains=~test
            DNS=
            """);

        Assert.Equal(["~docker"], domains);
        Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9"], servers);
    }

    [Theory]
    [InlineData("~test", true)]
    [InlineData("test", true)]
    [InlineData("~.test", true)]
    [InlineData(".test", true)]
    [InlineData("TEST.", true)]
    [InlineData("~test.local", false)]
    [InlineData("testing", false)]
    [InlineData("~mytest", false)]
    [InlineData("~.", false)]
    public void ResolvedDropIn_SabeCuandoUnDominioEsTest(string domain, bool isTest)
    {
        Assert.Equal(isTest, ResolvedDropIn.IsTestDomain(domain));
    }

    [Fact]
    public void ResolvedDropIn_ReadFiles_ElPrincipalYLosDropIns_PorOrden_YSinLosQueNoSeLeen()
    {
        var dir = Path.Combine(_root, "resolved.conf.d");
        Directory.CreateDirectory(dir);
        var main = Path.Combine(_root, "resolved.conf");
        File.WriteAllText(main, "[Resolve]\nDomains=~corp\n");
        File.WriteAllText(Path.Combine(dir, "20-b.conf"), "[Resolve]\nDomains=test\nDNS=127.0.0.1\n");
        File.WriteAllText(Path.Combine(dir, "10-a.conf"), "[Resolve]\nDNS=1.1.1.1\n");
        File.WriteAllText(Path.Combine(dir, "ignorado.txt"), "[Resolve]\nDomains=~test\n");
        File.WriteAllText(Path.Combine(dir, "roost.conf"), ResolvedDropIn.Content);

        var entries = ResolvedDropIn.ReadFiles(dir, main);

        Assert.Equal([main, Path.Combine(dir, "10-a.conf"), Path.Combine(dir, "20-b.conf"), Path.Combine(dir, "roost.conf")], entries.Select(e => e.File));
        Assert.Equal([false, false, true, true], entries.Select(e => e.RoutesTest));
        Assert.Equal([false, false, false, true], entries.Select(e => e.IsRoost));
        // Lo que mira el helper antes de escribir: otro fichero que ya enrute test y no sea el suyo.
        Assert.Equal(Path.Combine(dir, "20-b.conf"), entries.Single(e => !e.IsRoost && e.RoutesTest).File);
        Assert.Empty(ResolvedDropIn.ReadFiles(Path.Combine(_root, "no-existe"), Path.Combine(_root, "tampoco")));
    }

    // ---- HelperChecks ---------------------------------------------------------------------

    [Theory]
    [InlineData("/home/ana/.local/share/roost/bin/nginx/1.30.6/nginx", true)]
    [InlineData("/x", true)]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("nginx", false)]
    [InlineData("home/ana/nginx", false)]
    [InlineData("/home//ana/nginx", false)]
    [InlineData("/home/./ana/nginx", false)]
    [InlineData("/home/ana/../root/nginx", false)]
    [InlineData("/home/ana/nginx/", false)]
    [InlineData("/home/ana/nginx\n", false)]
    [InlineData("/home/ana/ngi\u0001nx", false)]
    [InlineData("/home/ana/nginx\0", false)]
    public void HelperChecks_SoloRutasAbsolutasYLimpias(string path, bool clean)
    {
        Assert.Equal(clean, HelperChecks.IsCleanAbsolutePath(path));
    }

    [Fact]
    public void HelperChecks_UnaRutaDeMasDe4096_NoVale()
    {
        Assert.False(HelperChecks.IsCleanAbsolutePath("/" + new string('a', HelperChecks.MaxPathLength)));
    }

    [Theory]
    [InlineData("/home/ana/roost/nginx", true)]
    [InlineData("/home/ana/nginx.sh", false)]
    [InlineData("/home/ana/Nginx", false)]
    [InlineData("/home/ana/nginx/", false)]
    [InlineData("/home/ana/minginx", false)]
    public void HelperChecks_SoloSiSeLlamaNginx(string path, bool named)
    {
        Assert.Equal(named, HelperChecks.IsNamedNginx(path));
    }

    [Theory]
    [InlineData("/home/ana", "/home/ana/x/nginx", true)]
    [InlineData("/home/ana/", "/home/ana/x/nginx", true)]
    [InlineData("/home/ana", "/home/anabel/nginx", false)]
    [InlineData("/home/ana", "/home/ana", false)]
    [InlineData("/home/ana", "/tmp/nginx", false)]
    [InlineData("/", "/tmp/nginx", false)]
    public void HelperChecks_DentroDeLaHome(string home, string path, bool under)
    {
        Assert.Equal(under, HelperChecks.IsUnder(home, path));
    }

    [Fact]
    public void HelperChecks_SoloElf()
    {
        Assert.True(HelperChecks.IsElf([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1]));
        Assert.False(HelperChecks.IsElf("#!/bin/sh\n"u8));
        Assert.False(HelperChecks.IsElf("MZ\x90\0"u8));
        Assert.False(HelperChecks.IsElf([0x7F, (byte)'E', (byte)'L']));
        Assert.False(HelperChecks.IsElf([]));
    }

    [Theory]
    [InlineData("1003", true, 1003u)]
    [InlineData("0", true, 0u)]
    [InlineData("4294967295", true, 4294967295u)]
    [InlineData("4294967296", false, 0u)]
    [InlineData("01", false, 0u)]
    [InlineData("-1", false, 0u)]
    [InlineData(" 1", false, 0u)]
    [InlineData("1 ", false, 0u)]
    [InlineData("1e3", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData(null, false, 0u)]
    public void HelperChecks_ElUidDePkexecOSudo_SoloUnNumero(string? text, bool ok, uint expected)
    {
        Assert.Equal(ok, HelperChecks.TryParseUid(text, out var uid));
        Assert.Equal(expected, uid);
    }

    [LinuxFact]
    public void HelperChecks_ElUidDeEsteProceso_SaleDeProc()
    {
        Assert.NotNull(HelperChecks.CurrentUid());
    }

    // ---- FirewallRule (Linux) -------------------------------------------------------------

    [Fact]
    public void FirewallRule_Ufw_SoloTcpAEsePuerto_DesdeUnaRedPrivada_ConElComentarioDeRoost()
    {
        Assert.Equal(["allow", "in", "proto", "tcp", "from", "192.168.0.0/16", "to", "any", "port", "8443", "comment", "Roost LAN 8443"],
            FirewallRule.UfwArguments(8443, "192.168.0.0/16", allow: true));
        Assert.Equal(["--force", "delete", "allow", "in", "proto", "tcp", "from", "10.0.0.0/8", "to", "any", "port", "8443", "comment", "Roost LAN 8443"],
            FirewallRule.UfwArguments(8443, "10.0.0.0/8", allow: false));
        Assert.Equal(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"], FirewallRule.PrivateNetworks);
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRule.UfwArguments(3389, "10.0.0.0/8", allow: true));
    }

    [Fact]
    public void FirewallRule_Firewalld_ReglaEnriquecida_EnCalienteYPermanente()
    {
        Assert.Equal("rule family=\"ipv4\" source address=\"10.0.0.0/8\" port port=\"8443\" protocol=\"tcp\" accept", FirewallRule.FirewalldRichRule(8443, "10.0.0.0/8"));
        Assert.Equal(["--add-rich-rule=" + FirewallRule.FirewalldRichRule(8443, "10.0.0.0/8")], FirewallRule.FirewalldArguments(8443, "10.0.0.0/8", allow: true, permanent: false));
        Assert.Equal(["--permanent", "--remove-rich-rule=" + FirewallRule.FirewalldRichRule(8443, "10.0.0.0/8")], FirewallRule.FirewalldArguments(8443, "10.0.0.0/8", allow: false, permanent: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRule.FirewalldRichRule(6379, "10.0.0.0/8"));
    }

    [Theory]
    [InlineData(false, false, null, false)]                       // ni ufw ni firewalld: nada que abrir, sin pedir permiso
    [InlineData(true, false, "ENABLED=no\nLOGLEVEL=low\n", false)] // ufw instalado pero apagado (Ubuntu de escritorio)
    [InlineData(true, false, "# comentario\nENABLED=yes\n", true)]
    [InlineData(true, false, null, true)]                         // ufw.conf ilegible: lo mira el helper con root
    [InlineData(false, true, null, true)]                         // firewalld
    public void FirewallRule_LinuxMayNeedRule_SoloConUfwEncendidoOFirewalld(bool ufw, bool firewalld, string? conf, bool expected)
    {
        bool Exists(string path) => path == FirewallRule.UfwPath ? ufw : path == FirewallRule.FirewalldPath && firewalld;
        Assert.Equal(expected, FirewallRule.LinuxMayNeedRule(Exists, path => path == FirewallRule.UfwConf ? conf : null));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
