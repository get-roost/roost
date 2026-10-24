using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// SiteManager con acceso (Git-2): cada operacion (clone, pull, checkout, fetch, status) monta su
/// acceso a partir de site.Git; el clonado guarda el tipo, la clave y el token; SSH sin clave no
/// lanza nada; y quitar un sitio quita su clave y su token salvo lo que compartan otros (I9). Sin
/// red, sin git real y con el almacen de credenciales en memoria.
/// </summary>
public class SiteGitAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-siteaccess-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private readonly InMemoryCredentialStore _store = new();
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private GitAccessFactory Factory => new(Paths, _store, _runner, @"C:\Roost\roost.exe", new OpenSshTools(@"C:\Windows\System32\OpenSSH"), GitCredentials.ProductionPrefix);

    private SiteManager Sites => new(Paths, new GitClient("git", _runner, new GitEnvironment(Paths))) { Access = Factory };

    private const string LsRemote = "ref: refs/heads/main\tHEAD\n7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\tHEAD\n7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\trefs/heads/main\n";
    private const string Head = "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d";
    private static string Log(string sha) => $"{sha}\u001f{sha[..7]}\u001fAna\u001f2026-10-24T10:00:00+02:00\u001fPrimer commit\n";
    private const string Clean = "# branch.oid 7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0\n";

    private static void FakeClone(CommandSpec spec)
    {
        var target = spec.Arguments[^1];
        Directory.CreateDirectory(Path.Combine(target, ".git"));
        File.WriteAllText(Path.Combine(target, "index.php"), "<?php");
    }

    private void ScriptClone() =>
        _runner.On("ls-remote --symref", stdout: LsRemote)
            .On("clone --progress", onRun: FakeClone)
            .On("log -1", stdout: Log(Head));

    private void FakeKey(string name)
    {
        var keys = Path.Combine(Paths.GitState, "keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, name), "privada de pega");
        File.WriteAllText(Path.Combine(keys, name + ".pub"), "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL+pitVcgStKA+2SdBhY6HXfTF8cs0wddhi72/OCTOP6 roost:" + name);
    }

    private string SaveToken(string location, string token = "github_pat_de_pega") =>
        Factory.Credentials.Save(CredentialLocation.TryParse(location, out var l, out _) ? l : throw new InvalidOperationException(), token);

    [Fact]
    public async Task Clone_ConToken_GuardaElTipoYElTarget_YGitLlevaElHelper()
    {
        var target = SaveToken("github.com/acme/tienda");
        ScriptClone();

        var result = await Sites.CloneAsync(new CloneRequest(GitUrl.Parse("https://github.com/acme/tienda.git"), Access: SiteAccess.Token), null, null, CancellationToken.None);

        var git = result.Change.Site.Git!;
        Assert.Equal(SiteGit.AccessToken, git.Access);
        Assert.Equal(target, git.CredentialTarget);
        Assert.Null(git.KeyName);
        var clone = _runner.Calls.Single(c => c.Arguments[0] == "clone");
        Assert.Equal("2", clone.Environment!["GIT_CONFIG_COUNT"]);
        Assert.Equal("!'C:/Roost/roost.exe' git-credential", clone.Environment["GIT_CONFIG_VALUE_1"]);
        // El token no va ni en los argumentos ni en el entorno ni en sites.json.
        Assert.DoesNotContain(clone.Arguments, a => a.Contains("github_pat"));
        Assert.DoesNotContain(clone.Environment.Values, v => v?.Contains("github_pat") == true);
        Assert.DoesNotContain("github_pat", File.ReadAllText(Sites.Registry.FilePath));
    }

    [Fact]
    public async Task Clone_ConLaClaveDeRoost_GitLlevaSuSsh_YSeGuardaLaClave()
    {
        FakeKey("tienda");
        ScriptClone();

        var result = await Sites.CloneAsync(new CloneRequest(GitUrl.Parse("git@github.com:acme/tienda.git"), Access: SiteAccess.Key("tienda")), null, null, CancellationToken.None);

        var git = result.Change.Site.Git!;
        Assert.Equal(SiteGit.AccessKey, git.Access);
        Assert.Equal("tienda", git.KeyName);
        var factory = Factory;
        var expected = OpenSsh.KeyCommand(factory.Ssh, factory.Keys.PrivateFile("tienda"), factory.KnownHosts.FilePath);
        Assert.All(_runner.Calls.Where(c => c.Arguments[0] is "ls-remote" or "clone"), c => Assert.Equal(expected, c.Environment!["GIT_SSH_COMMAND"]));
    }

    [Fact]
    public async Task Clone_PorSshSinAcceso_NoLanzaNada_NiDejaHistorial()
    {
        var ex = await Assert.ThrowsAsync<GitException>(() => Sites.CloneAsync(new CloneRequest(GitUrl.Parse("git@github.com:acme/tienda.git")), null, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.KeyRequired, ex.Failure.Kind);
        Assert.Empty(_runner.Calls);
        Assert.Empty(Sites.History.Load("tienda"));
        Assert.Null(Sites.Find("tienda"));
    }

    [Fact]
    public async Task Clone_ConElTokenRechazado_LoDiceComoTokenRechazado()
    {
        SaveToken("github.com/acme/tienda");
        _runner.On("ls-remote --symref", exitCode: 128, stderr: "remote: Invalid username or token. Password authentication is not supported for Git operations.\nfatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n");

        var ex = await Assert.ThrowsAsync<GitException>(() => Sites.CloneAsync(new CloneRequest(GitUrl.Parse("https://github.com/acme/tienda.git"), Access: SiteAccess.Token), null, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.TokenRejected, ex.Failure.Kind);
        Assert.Null(Sites.Find("tienda"));
    }

    [Fact]
    public async Task Probe_SinToken_PideAcceso_YConTokenEsQueLoRechazo()
    {
        var url = GitUrl.Parse("https://github.com/acme/tienda.git");
        _runner.On("ls-remote --symref", exitCode: 128, stderr: "fatal: could not read Username for 'https://github.com': terminal prompts disabled\n");

        var without = await Sites.ProbeAsync(url, null, CancellationToken.None);
        SaveToken("github.com");
        var with = await Sites.ProbeAsync(url, null, CancellationToken.None);   // el helper va siempre: vale el de todo github.com

        Assert.Equal(GitFailureKind.AuthRequired, without.Failure!.Kind);
        Assert.True(without.NeedsAuth);
        Assert.Equal(GitFailureKind.TokenRejected, with.Failure!.Kind);
    }

    [Fact]
    public async Task PullYCheckoutYStatus_UsanElAccesoDelSitio()
    {
        FakeKey("tienda");
        ScriptClone();
        await Sites.CloneAsync(new CloneRequest(GitUrl.Parse("git@github.com:acme/tienda.git"), Access: SiteAccess.Key("tienda")), null, null, CancellationToken.None);
        _runner.Calls.Clear();
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("pull --ff-only", stdout: "")
            .On("fetch --progress", stdout: "")
            .On("rev-parse --abbrev-ref", stdout: "main\n");

        await Sites.PullAsync("tienda", null, CancellationToken.None);
        await Sites.GitStatusAsync("tienda", fetch: true, CancellationToken.None);

        var factory = Factory;
        var expected = OpenSsh.KeyCommand(factory.Ssh, factory.Keys.PrivateFile("tienda"), factory.KnownHosts.FilePath);
        Assert.Contains(_runner.Calls, c => c.Arguments[0] == "pull");
        Assert.Contains(_runner.Calls, c => c.Arguments[0] == "fetch");
        Assert.All(_runner.Calls, c => Assert.Equal(expected, c.Environment!["GIT_SSH_COMMAND"]));
    }

    [Fact]
    public async Task Pull_ConLaClaveQuitada_LoDiceSinLanzarGit()
    {
        FakeKey("tienda");
        ScriptClone();
        await Sites.CloneAsync(new CloneRequest(GitUrl.Parse("git@github.com:acme/tienda.git"), Access: SiteAccess.Key("tienda")), null, null, CancellationToken.None);
        Factory.Keys.Remove("tienda");
        _runner.Calls.Clear();

        var ex = await Assert.ThrowsAsync<RoostException>(() => Sites.PullAsync("tienda", null, CancellationToken.None));

        Assert.Contains("roost git key new tienda", ex.Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task StatusSinFetch_EsLocal_YNoNecesitaLaClave()
    {
        FakeKey("tienda");
        ScriptClone();
        await Sites.CloneAsync(new CloneRequest(GitUrl.Parse("git@github.com:acme/tienda.git"), Access: SiteAccess.Key("tienda")), null, null, CancellationToken.None);
        Factory.Keys.Remove("tienda");
        _runner.On("status --porcelain=v2", stdout: Clean);

        var status = await Sites.GitStatusAsync("tienda", fetch: false, CancellationToken.None);

        Assert.True(status.Status.IsClean);
    }

    [Fact]
    public async Task ProbeSite_PreguntaAlRemotoConElAccesoDelSitio()
    {
        FakeKey("tienda");
        ScriptClone();
        await Sites.CloneAsync(new CloneRequest(GitUrl.Parse("git@github.com:acme/tienda.git"), Access: SiteAccess.Key("tienda")), null, null, CancellationToken.None);
        _runner.Calls.Clear();

        var probe = await Sites.ProbeSiteAsync("tienda.test", CancellationToken.None);

        Assert.True(probe.Reachable);
        var call = _runner.Calls.Single();
        Assert.Equal(["ls-remote", "--symref", "--", "git@github.com:acme/tienda.git", "HEAD", "refs/heads/*"], call.Arguments);
        Assert.Contains("IdentityFile=", call.Environment!["GIT_SSH_COMMAND"]);
    }

    // --- I9: quitar un sitio quita tambien su acceso ---

    private void Register(string name, string remote, string access, string? key = null, string? target = null)
    {
        Directory.CreateDirectory(Path.Combine(Paths.Www, name));
        new SiteRegistry(Paths).Upsert(new Site
        {
            Name = name,
            Folder = Path.Combine(Paths.Www, name),
            Git = new SiteGit { Remote = GitUrl.Parse(remote).Normalized, Branch = "main", Access = access, KeyName = key, CredentialTarget = target },
        });
    }

    [Fact]
    public void RemoveAccess_QuitaSuClaveYSuToken_YDejaElHistorial()
    {
        FakeKey("tienda");
        Register("tienda", "git@github.com:acme/tienda.git", SiteGit.AccessKey, key: "tienda");
        var sites = Sites;
        sites.History.Append("tienda", new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Clone, "main", null, "7fd1a60", true, 1, null));
        var site = sites.Remove("tienda")!;

        var removal = sites.RemoveAccess(site);

        Assert.True(removal.KeyRemoved);
        Assert.False(Factory.Keys.Exists("tienda"));
        Assert.Equal("https://github.com/acme/tienda/settings/keys", removal.DeployKeysUrl);
        Assert.True(File.Exists(removal.HistoryFile));
        Assert.Equal(sites.Log.FilePath("tienda"), removal.LogFile);
    }

    [Fact]
    public void RemoveAccess_SuTokenSeBorra_PeroUnoCompartidoNo()
    {
        var own = SaveToken("github.com/acme/tienda");
        var shared = SaveToken("github.com/acme");
        Register("tienda", "https://github.com/acme/tienda.git", SiteGit.AccessToken, target: own);
        Register("blog", "https://github.com/acme/blog.git", SiteGit.AccessToken, target: shared);
        Register("web", "https://github.com/acme/web.git", SiteGit.AccessNone);   // sin target, pero el de acme le vale
        var sites = Sites;

        var first = sites.RemoveAccess(sites.Remove("tienda")!);
        var second = sites.RemoveAccess(sites.Remove("blog")!);

        Assert.True(first.TokenRemoved);
        Assert.Null(_store.Read(own));
        Assert.Equal(shared, second.Token);
        Assert.True(second.TokenShared);                         // web lo usa: git se lo daria
        Assert.False(second.TokenRemoved);
        Assert.NotNull(_store.Read(shared));
    }

    [Fact]
    public void RemoveAccess_ElTokenDeSuRepositorio_AunqueNoLoGuardaraAlClonar()
    {
        var exact = SaveToken("github.com/acme/tienda");
        var host = SaveToken("github.com");
        Register("tienda", "https://github.com/acme/tienda.git", SiteGit.AccessNone);
        var sites = Sites;

        var removal = sites.RemoveAccess(sites.Remove("tienda")!);

        Assert.Equal(exact, removal.Token);
        Assert.True(removal.TokenRemoved);
        Assert.NotNull(_store.Read(host));   // el de todo el servidor no es suyo
    }

    [Fact]
    public void RemoveAccess_UnaClaveQueUsaOtroSitio_SeQueda()
    {
        FakeKey("tienda");
        Register("tienda", "git@github.com:acme/tienda.git", SiteGit.AccessKey, key: "tienda");
        Register("tienda-copia", "git@github.com:acme/tienda.git", SiteGit.AccessKey, key: "tienda");
        var sites = Sites;

        var removal = sites.RemoveAccess(sites.Remove("tienda")!);

        Assert.True(removal.KeyShared);
        Assert.False(removal.KeyRemoved);
        Assert.True(Factory.Keys.Exists("tienda"));
    }

    [Fact]
    public void RemoveAccess_EnSeco_NoBorraNada()
    {
        FakeKey("tienda");
        var target = SaveToken("github.com/acme/tienda");
        Register("tienda", "https://github.com/acme/tienda.git", SiteGit.AccessToken, target: target);
        var sites = Sites;

        var plan = sites.RemoveAccess(sites.Find("tienda")!, dryRun: true);

        Assert.True(plan.HasAccess);
        Assert.Equal("tienda", plan.KeyName);          // la clave con su nombre es suya
        Assert.Equal(target, plan.Token);
        Assert.False(plan.KeyRemoved || plan.TokenRemoved);
        Assert.True(Factory.Keys.Exists("tienda"));
        Assert.NotNull(_store.Read(target));
    }

    [Fact]
    public void RemoveAccess_UnSitioSinNada_NoTieneAcceso()
    {
        Directory.CreateDirectory(Path.Combine(Paths.Www, "plano"));
        new SiteRegistry(Paths).Upsert(new Site { Name = "plano", Folder = Path.Combine(Paths.Www, "plano") });
        var sites = Sites;

        Assert.False(sites.RemoveAccess(sites.Remove("plano")!).HasAccess);
    }

    public void Dispose()
    {
        Install.InstallFiles.TryDeleteDirectory(_root);
    }
}
