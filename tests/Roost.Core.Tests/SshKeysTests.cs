using Roost.Core.Git;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Las claves SSH de los sitios: ssh-keygen es el FakeCommandRunner (escribe ficheros como el de
/// verdad), pero la ACL es la real, en una carpeta temporal: solo el usuario actual.
/// </summary>
public class SshKeysTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-sshkeys-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));
    private SshKeys Keys => new(Paths, _runner, new OpenSshTools(Path.Combine(_root, "openssh")));

    private const string PublicBlob = "AAAAC3NzaC1lZDI1NTE5AAAAIL+pitVcgStKA+2SdBhY6HXfTF8cs0wddhi72/OCTOP6";

    /// <summary>Lo que hace ssh-keygen -f &lt;fichero&gt;: la privada y la .pub al lado.</summary>
    private static void FakeKeygen(Scanning.CommandSpec spec)
    {
        var file = spec.Arguments[spec.Arguments.ToList().IndexOf("-f") + 1];
        var comment = spec.Arguments[spec.Arguments.ToList().IndexOf("-C") + 1];
        File.WriteAllText(file, "-----BEGIN OPENSSH PRIVATE KEY-----\nde pega\n-----END OPENSSH PRIVATE KEY-----\n");
        File.WriteAllText(file + ".pub", $"ssh-ed25519 {PublicBlob} {comment}\n");
    }

    [Fact]
    public async Task Generate_Ed25519SinContrasena_ConLaAclSoloDelUsuario()
    {
        _runner.On("-q -t ed25519", onRun: FakeKeygen);

        var key = await Keys.GenerateAsync("tienda", CancellationToken.None);

        var call = _runner.Calls.Single();
        Assert.Equal(["-q", "-t", "ed25519", "-N", "", "-C", SshKeys.Comment("tienda"), "-f"], call.Arguments.Take(8));
        Assert.Equal("", call.Stdin);   // si preguntara algo, no se queda esperando
        Assert.StartsWith(Path.Combine(Paths.GitState, "keys", ".tmp-"), call.Arguments[^1]);   // se genera aparte y se renombra

        Assert.Equal(Path.Combine(Paths.GitState, "keys", "tienda"), key.PrivateFile);
        Assert.True(File.Exists(key.PrivateFile));
        Assert.Equal($"ssh-ed25519 {PublicBlob} roost:tienda@{Environment.MachineName}", key.PublicKey);
        Assert.Equal(KnownHosts.Fingerprint(PublicBlob), key.Fingerprint);
        Assert.Empty(Directory.GetFiles(Keys.Directory, ".tmp-*"));
        if (OperatingSystem.IsWindows())
            Assert.True(PrivateFile.IsRestrictedToCurrentUser(key.PrivateFile));
    }

    [Fact]
    public async Task Generate_SiYaTieneUna_NoLaPisa()
    {
        _runner.On("-q -t ed25519", onRun: FakeKeygen);
        await Keys.GenerateAsync("tienda", CancellationToken.None);
        var before = File.ReadAllText(Keys.PrivateFile("tienda"));

        await Assert.ThrowsAsync<RoostException>(() => Keys.GenerateAsync("tienda", CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(Keys.PrivateFile("tienda")));
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task Generate_SiSshKeygenFalla_NoQuedaNada()
    {
        _runner.On("-q -t ed25519", exitCode: 1, stderr: "Saving key failed: no se puede\n");

        var ex = await Assert.ThrowsAsync<RoostException>(() => Keys.GenerateAsync("tienda", CancellationToken.None));

        Assert.Contains("ssh-keygen no ha podido crear la clave de tienda", ex.Message);
        Assert.False(Keys.Exists("tienda"));
        Assert.Empty(Directory.GetFiles(Keys.Directory));
    }

    [Fact]
    public async Task Import_CopiaLaClaveDelUsuario_YNoTocaLaSuya()
    {
        var mine = Path.Combine(_root, "mias", "deploy_tienda");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "-----BEGIN OPENSSH PRIVATE KEY-----\nde pega\n-----END OPENSSH PRIVATE KEY-----\n");
        _runner.On("-y -P", stdout: $"ssh-ed25519 {PublicBlob} ana@portatil\n");

        var key = await Keys.ImportAsync("tienda", mine, CancellationToken.None);

        Assert.True(File.Exists(mine));
        Assert.Equal(File.ReadAllText(mine), File.ReadAllText(key.PrivateFile));
        Assert.Equal($"ssh-ed25519 {PublicBlob} ana@portatil", File.ReadAllText(key.PublicFile).Trim());
        // ssh-keygen lee la copia (ya protegida), no la del usuario.
        Assert.StartsWith(Path.Combine(Paths.GitState, "keys", ".tmp-"), _runner.Calls.Single().Arguments[^1]);
        if (OperatingSystem.IsWindows())
            Assert.True(PrivateFile.IsRestrictedToCurrentUser(key.PrivateFile));
    }

    [Fact]
    public async Task Import_ConContrasena_LoDiceYNoDejaCopia()
    {
        var mine = Path.Combine(_root, "con-clave");
        Directory.CreateDirectory(_root);
        File.WriteAllText(mine, "-----BEGIN OPENSSH PRIVATE KEY-----\ncifrada\n-----END OPENSSH PRIVATE KEY-----\n");
        _runner.On("-y -P", exitCode: 255, stderr: "Load key \"x\": incorrect passphrase supplied to decrypt private key\n");

        var ex = await Assert.ThrowsAsync<RoostException>(() => Keys.ImportAsync("tienda", mine, CancellationToken.None));

        Assert.Contains("tiene contrasena", ex.Message);
        Assert.False(Keys.Exists("tienda"));
        Assert.Empty(Directory.GetFiles(Keys.Directory));
    }

    [Fact]
    public async Task Import_LaPublicaOUnaDePutty_SeRechazanSinLlamarASshKeygen()
    {
        Directory.CreateDirectory(_root);
        var pub = Path.Combine(_root, "id.pub");
        File.WriteAllText(pub, $"ssh-ed25519 {PublicBlob} ana\n");
        var ppk = Path.Combine(_root, "id.ppk");
        File.WriteAllText(ppk, "PuTTY-User-Key-File-3: ssh-ed25519\n");

        Assert.Contains("clave publica", (await Assert.ThrowsAsync<RoostException>(() => Keys.ImportAsync("tienda", pub, CancellationToken.None))).Message);
        Assert.Contains("PuTTY", (await Assert.ThrowsAsync<RoostException>(() => Keys.ImportAsync("tienda", ppk, CancellationToken.None))).Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task ListYRemove()
    {
        _runner.On("-q -t ed25519", onRun: FakeKeygen);
        await Keys.GenerateAsync("tienda", CancellationToken.None);
        await Keys.GenerateAsync("blog", CancellationToken.None);

        Assert.Equal(["blog", "tienda"], Keys.List().Select(k => k.Name));
        Assert.True(Keys.Remove("tienda"));
        Assert.False(Keys.Remove("tienda"));
        Assert.Equal(["blog"], Keys.List().Select(k => k.Name));
    }

    [Theory]
    [InlineData("../fuera")]
    [InlineData("Tienda")]
    [InlineData("")]
    public void NombreRaro_NoSaleDeLaCarpeta(string name)
    {
        Assert.Throws<RoostException>(() => Keys.PrivateFile(name));
    }

    [Fact]
    public void LaCarpetaDeClaves_NaceProtegida()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(_root, "protegida");

        PrivateFile.EnsureDirectory(dir);
        var file = Path.Combine(dir, "dentro");
        File.WriteAllText(file, "x");

        // Lo que se crea dentro hereda la ACL solo del usuario, sin haber hecho nada mas.
        var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        Assert.All(rules.Cast<System.Security.AccessControl.FileSystemAccessRule>(),
            r => Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, r.IdentityReference));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
