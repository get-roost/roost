using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>
/// El known_hosts de Roost: las claves de serie contra las huellas que publican GitHub, GitLab y
/// Bitbucket, la huella calculada por Roost (no la de ssh-keyscan), el estado de cada servidor, el
/// TOFU y olvidar. Sin red: el ssh-keyscan es el FakeCommandRunner.
/// </summary>
public class KnownHostsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-knownhosts-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private string File_ => Path.Combine(_root, "known_hosts");
    private KnownHosts Hosts => new(File_, _runner, new OpenSshTools(Path.Combine(_root, "openssh")));

    // Una clave ed25519 de pega (la de un servidor que no es nadie) y otra para «la huella ha cambiado».
    private const string FakeEd25519 = "AAAAC3NzaC1lZDI1NTE5AAAAIL+pitVcgStKA+2SdBhY6HXfTF8cs0wddhi72/OCTOP6";
    private const string OtherEd25519 = "AAAAC3NzaC1lZDI1NTE5AAAAICMlGcj+Y4aoP5ASHF63pVz9aBKDprQDbbwJqxhTKsvc";

    [Theory]
    [InlineData("github.com")]
    [InlineData("gitlab.com")]
    [InlineData("bitbucket.org")]
    public void ClavesDeSerie_DanLasHuellasQuePublicaCadaProveedor(string host)
    {
        var builtIn = KnownHosts.BuiltIn.Single(b => b.Host == host);

        var computed = builtIn.Keys.Select(k => KnownHosts.Fingerprint(k.Base64)).ToList();

        // Las tres (ED25519, ECDSA y RSA), en el mismo orden que la pagina de cada proveedor.
        Assert.Equal(KnownHosts.PublishedFingerprints[host], computed);
        Assert.Equal(["ED25519", "ECDSA", "RSA"], builtIn.Keys.Select(k => k.Algorithm));
        Assert.All(builtIn.Keys, k => Assert.Equal(k.Type, KnownHosts.DeclaredType(k.Base64)));
    }

    [Fact]
    public void Huella_EsLaDeOpenSsh_Sha256EnBase64SinRelleno()
    {
        // La ED25519 de GitHub, tal como la ensena su pagina y ssh-keygen -l.
        Assert.Equal("SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU",
            KnownHosts.Fingerprint("AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"));
    }

    [Theory]
    [InlineData("# un comentario")]
    [InlineData("")]
    [InlineData("@cert-authority *.acme.es ssh-ed25519 " + FakeEd25519)]
    [InlineData("|1|abc=|def= ssh-ed25519 " + FakeEd25519)]
    [InlineData("git.acme.es ssh-dss AAAAB3NzaC1kc3M=")]
    [InlineData("git.acme.es ssh-rsa " + FakeEd25519)]   // el blob dice ed25519: no cuadra con el tipo
    [InlineData("git.acme.es ssh-ed25519 esto-no-es-base64")]
    public void ParseLine_SaltaLoQueNoEsUnaLineaUtil(string line)
    {
        Assert.Null(KnownHosts.ParseLine(line));
    }

    [Fact]
    public void ParseLine_VariosNombresYComentario()
    {
        var entry = KnownHosts.ParseLine("Git.Acme.es,[git.acme.es]:2222 ssh-ed25519 " + FakeEd25519 + " roost:aceptada-2026-10-24")!;

        Assert.Equal(["git.acme.es", "[git.acme.es]:2222"], entry.Hosts);
        Assert.True(entry.Matches("[git.acme.es]:2222"));
        Assert.Equal("roost:aceptada-2026-10-24", entry.Comment);
        Assert.Equal("2026-10-24", KnownHosts.AcceptedOn(entry));
    }

    [Theory]
    [InlineData("git.acme.es", null, "git.acme.es")]
    [InlineData("git.acme.es", 22, "git.acme.es")]
    [InlineData("Git.Acme.es", 2222, "[git.acme.es]:2222")]
    public void HostToken_ComoLoEscribeOpenSsh(string host, int? port, string expected)
    {
        Assert.Equal(expected, KnownHosts.HostToken(host, port));
    }

    [Fact]
    public void EnsurePublished_PoneLasDeSerieUnaVez_YRespetaLoDemas()
    {
        var hosts = Hosts;
        hosts.EnsurePublished();
        var first = File.ReadAllText(File_);
        hosts.Trust("git.acme.es", null, [new SshHostKey("ssh-ed25519", FakeEd25519)]);

        hosts.EnsurePublished();

        Assert.StartsWith("# Lo escribe Roost", first);
        Assert.Equal(9, KnownHosts.BuiltIn.Sum(b => hosts.For(b.Host).Count));
        Assert.Equal(HostTrust.Published, hosts.Status("github.com").Trust);
        Assert.Equal(HostTrust.Trusted, hosts.Status("git.acme.es").Trust);
        Assert.DoesNotContain('\r', File.ReadAllText(File_));
    }

    [Fact]
    public void Status_DeSerieCambiadaEnElFichero_EsMismatch_YOlvidarlaDevuelveLaPublicada()
    {
        var hosts = Hosts;
        hosts.EnsurePublished();
        hosts.Trust("github.com", null, [new SshHostKey("ssh-ed25519", FakeEd25519)]);

        Assert.Equal(HostTrust.Mismatch, hosts.Status("github.com").Trust);

        Assert.Equal(1, hosts.Forget("github.com", null));
        hosts.EnsurePublished();
        Assert.Equal(HostTrust.Published, hosts.Status("github.com").Trust);
        Assert.Equal(3, hosts.For("github.com").Count);
    }

    [Fact]
    public void Status_DesconocidoYAceptado()
    {
        var hosts = Hosts;
        Assert.Equal(HostTrust.Unknown, hosts.Status("git.acme.es").Trust);
        // El puerto cuenta: el mismo servidor en otro puerto es otra entrada.
        hosts.Trust("git.acme.es", 2222, [new SshHostKey("ssh-ed25519", FakeEd25519)]);

        Assert.Equal(HostTrust.Unknown, hosts.Status("git.acme.es").Trust);
        var status = hosts.Status("git.acme.es", 2222);
        Assert.Equal(HostTrust.Trusted, status.Trust);
        Assert.Equal("[git.acme.es]:2222", status.Token);
        Assert.StartsWith("roost:aceptada-", status.Stored.Single().Comment);
    }

    [Fact]
    public void Trust_SustituyeLaQueHabia()
    {
        var hosts = Hosts;
        hosts.Trust("git.acme.es", null, [new SshHostKey("ssh-ed25519", FakeEd25519)]);
        hosts.Trust("git.acme.es", null, [new SshHostKey("ssh-ed25519", OtherEd25519)]);

        Assert.Equal(OtherEd25519, hosts.For("git.acme.es").Single().Key.Base64);
    }

    [Fact]
    public async Task Scan_CalculaLaHuellaYSoloCogeLasDelServidorPedido()
    {
        // Lo que imprime ssh-keyscan (los comentarios van a stderr), con una linea de otro servidor
        // y una con el tipo cambiado: ninguna de las dos vale.
        _runner.On("-T 10 -t ed25519,ecdsa,rsa -p 2222 git.acme.es", stdout:
            $"[git.acme.es]:2222 ssh-rsa {KnownHosts.BuiltIn[0].Keys[2].Base64}\n"
            + $"[git.acme.es]:2222 ssh-ed25519 {FakeEd25519}\n"
            + $"[otro.es]:2222 ssh-ed25519 {OtherEd25519}\n"
            + $"[git.acme.es]:2222 ssh-ed25519 {KnownHosts.BuiltIn[0].Keys[2].Base64}\n",
            stderr: "# git.acme.es:2222 SSH-2.0-OpenSSH_9.6\n");

        var keys = await Hosts.ScanAsync("git.acme.es", 2222, CancellationToken.None);

        Assert.Equal(["ssh-ed25519", "ssh-rsa"], keys.Select(k => k.Type));   // la ed25519 primero
        Assert.Equal(KnownHosts.Fingerprint(FakeEd25519), keys[0].Fingerprint);
        var call = _runner.Calls.Single();
        Assert.EndsWith("ssh-keyscan.exe", call.Exe);
    }

    [Fact]
    public async Task Scan_SinRespuesta_LanzaConElMotivo()
    {
        _runner.On("-T 10", exitCode: 1, stderr: "getaddrinfo git.nada.invalid: No such host is known.\n");

        var ex = await Assert.ThrowsAsync<RoostException>(() => Hosts.ScanAsync("git.nada.invalid", null, CancellationToken.None));

        Assert.Contains("no ha dado ninguna clave SSH", ex.Message);
    }

    [Theory]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("git acme")]
    [InlineData("")]
    public void HostRaro_NoLlegaASshKeyscan(string host)
    {
        Assert.False(KnownHosts.IsValidHost(host));
        Assert.Throws<RoostException>(() => Hosts.Trust(host, null, [new SshHostKey("ssh-ed25519", FakeEd25519)]));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
