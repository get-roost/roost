using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// El acceso de cada invocacion de git: el helper de credenciales por GIT_CONFIG_COUNT (solo en la
/// invocacion), el GIT_SSH_COMMAND de cada tipo y lo que no casa con la direccion. Sin red, sin git
/// real y con el almacen en memoria.
/// </summary>
public class GitAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-gitaccess-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private readonly InMemoryCredentialStore _store = new();
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));
    private const string Helper = @"C:\Program Files\Roost\roost.exe";

    private GitAccessFactory Factory => new(Paths, _store, _runner, Helper, new OpenSshTools(@"C:\Windows\System32\OpenSSH"), GitCredentials.ProductionPrefix);

    private void FakeKey(string name)
    {
        var keys = Path.Combine(Paths.GitState, "keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, name), "privada de pega");
        File.WriteAllText(Path.Combine(keys, name + ".pub"), "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL+pitVcgStKA+2SdBhY6HXfTF8cs0wddhi72/OCTOP6 roost:" + name);
    }

    [Fact]
    public void Https_SiempreConElHelperDeRoost_DetrasDeUnHelperVacio()
    {
        var access = Factory.For(GitUrl.Parse("https://github.com/acme/tienda.git"), SiteGit.AccessNone);

        Assert.Equal([("credential.helper", ""), ("credential.helper", "!'C:/Program Files/Roost/roost.exe' git-credential")], access.Config);
        Assert.Null(access.SshCommand);
        Assert.False(access.HasCredential);
    }

    [Fact]
    public void Https_ConUnTokenQueVale_LoDice()
    {
        var factory = Factory;
        factory.Credentials.Save(CredentialLocation.Create("https", "github.com", "acme"), "ghp_de_acme");

        var access = factory.For(GitUrl.Parse("https://github.com/acme/tienda.git"), SiteGit.AccessToken);

        Assert.True(access.HasCredential);
        Assert.Equal("Roost:git:https://github.com/acme", access.CredentialTarget);
        // El token nunca va en la configuracion ni en el entorno: lo pide git al helper.
        Assert.DoesNotContain(access.Config, c => c.Value.Contains("ghp_de_acme"));
    }

    [Fact]
    public void Ssh_ConLaClaveDeRoost_ElComandoConSuClaveYLasHuellasDeSerie()
    {
        FakeKey("tienda");
        var factory = Factory;

        var access = factory.For(GitUrl.Parse("git@github.com:acme/tienda.git"), SiteGit.AccessKey, "tienda");

        Assert.Equal(OpenSsh.KeyCommand(factory.Ssh, factory.Keys.PrivateFile("tienda"), factory.KnownHosts.FilePath), access.SshCommand);
        Assert.Empty(access.Config);
        Assert.Equal("tienda", access.KeyName);
        Assert.Equal(3, access.HostKeys.Count);              // las de serie ya estan en el known_hosts de Roost
        Assert.Equal(GitProvider.GitHub, access.PublishedHost?.Provider);
        Assert.True(File.Exists(factory.KnownHosts.FilePath));
    }

    [Fact]
    public void Ssh_MiClave_ElOpenSshDeWindowsConLasDelUsuario()
    {
        var factory = Factory;

        var access = factory.For(GitUrl.Parse("ssh://git@git.acme.es:2222/web/tienda.git"), SiteGit.AccessOwn);

        Assert.Equal(OpenSsh.OwnCommand(factory.Ssh, factory.KnownHosts.FilePath), access.SshCommand);
        Assert.Null(access.KeyName);
        Assert.Null(access.PublishedHost);
    }

    [Fact]
    public void Ssh_SinAcceso_NoSeLanza_PorqueSiempreHaceFaltaUnaClave()
    {
        var ex = Assert.Throws<GitException>(() => Factory.For(GitUrl.Parse("git@github.com:acme/tienda.git"), SiteGit.AccessNone));

        Assert.Equal(GitFailureKind.KeyRequired, ex.Failure.Kind);
        Assert.Contains("--ssh", ex.Message);
    }

    [Fact]
    public void Ssh_ConToken_NoCasa()
    {
        Assert.Equal(GitFailureKind.AccessMismatch, Assert.Throws<GitException>(() => Factory.For(GitUrl.Parse("git@github.com:acme/tienda.git"), SiteGit.AccessToken)).Failure.Kind);
    }

    [Fact]
    public void Ssh_ConUnaClaveQueNoEsta_LoDice()
    {
        var ex = Assert.Throws<RoostException>(() => Factory.For(GitUrl.Parse("git@github.com:acme/tienda.git"), SiteGit.AccessKey, "tienda"));

        Assert.Contains("roost git key new tienda", ex.Message);
    }

    [WindowsFact("Pendiente en Linux: Terminal y herramientas (L2).")]
    public void CarpetaLocal_SinNada()
    {
        var access = Factory.For(GitUrl.Parse(@"C:\repos\tienda.git"), SiteGit.AccessOwn);

        Assert.Empty(access.Config);
        Assert.Null(access.SshCommand);
    }

    [Fact]
    public async Task GitClient_PoneElAccesoEnElEntornoDeCadaOrden()
    {
        FakeKey("tienda");
        var url = GitUrl.Parse("git@github.com:acme/tienda.git");
        var access = Factory.For(url, SiteGit.AccessKey, "tienda");
        _runner.On("ls-remote --symref", stdout: "ref: refs/heads/main\tHEAD\nabc\tHEAD\nabc\trefs/heads/main\n");

        var probe = await new GitClient("git", _runner, new GitEnvironment(Paths)).With(access).ProbeAsync(url, null, CancellationToken.None);

        Assert.True(probe.Reachable);
        var env = _runner.Calls.Single().Environment!;
        Assert.Equal(access.SshCommand, env["GIT_SSH_COMMAND"]);
        Assert.Null(env["GIT_CONFIG_COUNT"]);   // sin configuracion por invocacion, se quita la heredada
    }

    [Fact]
    public async Task GitClient_ElHelperVaPorGitConfigCount()
    {
        var url = GitUrl.Parse("https://github.com/acme/tienda.git");
        var access = Factory.For(url, SiteGit.AccessToken);
        _runner.On("ls-remote --symref", stdout: "ref: refs/heads/main\tHEAD\nabc\tHEAD\nabc\trefs/heads/main\n");

        await new GitClient("git", _runner, new GitEnvironment(Paths), access).ProbeAsync(url, null, CancellationToken.None);

        var env = _runner.Calls.Single().Environment!;
        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("credential.helper", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("!'C:/Program Files/Roost/roost.exe' git-credential", env["GIT_CONFIG_VALUE_1"]);
        // Nada del acceso va en los argumentos.
        Assert.DoesNotContain(_runner.Calls.Single().Arguments, a => a.Contains("credential"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
