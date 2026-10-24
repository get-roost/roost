using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// SiteManager con git, sobre el FakeCommandRunner (el «clone» crea ficheros en la carpeta destino,
/// como el de verdad): el clonado en temporal con renombrado atomico, que un fallo o una cancelacion
/// no dejen carpeta ni sitio, el pull que no pisa cambios, el cambio de rama, adoptar un .git, el
/// historial y el log con las credenciales tapadas. Sin red, sin git real, sin tocar el hosts.
/// </summary>
public class SiteGitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-sitegit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));
    private SiteManager Sites => new(Paths, new GitClient("git", _runner, new GitEnvironment(Paths)));

    private static readonly GitUrl Url = GitUrl.Parse("https://github.com/octocat/Hello-World.git");

    private const string LsRemote = "ref: refs/heads/master\tHEAD\n7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\tHEAD\n7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\trefs/heads/master\nb3cbd5bbd7e81436d2eee04537ea2b4c0cad4cdf\trefs/heads/test\n";
    private const string Clean = "# branch.oid 7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\n# branch.head master\n# branch.upstream origin/master\n# branch.ab +0 -0\n";
    private const string Dirty = Clean + "1 .M N... 100644 100644 100644 aaaa aaaa README\n";

    private static string Log(string sha, string subject) => $"{sha}\u001f{sha[..7]}\u001fThe Octocat\u001f2012-03-06T23:06:50-08:00\u001f{subject}\n";
    private const string Head = "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d";
    private const string Next = "b3cbd5bbd7e81436d2eee04537ea2b4c0cad4cdf";

    /// <summary>Lo que hace un clone de verdad: deja un .git (con un objeto de solo lectura) y los ficheros del proyecto.</summary>
    private static void FakeClone(CommandSpec spec, bool withPublic = true)
    {
        var target = spec.Arguments[^1];
        Directory.CreateDirectory(Path.Combine(target, ".git", "objects", "7f"));
        var blob = Path.Combine(target, ".git", "objects", "7f", "d1a60b");
        File.WriteAllText(blob, "x");
        File.SetAttributes(blob, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(target, "README"), "Hello World!");
        if (withPublic)
        {
            Directory.CreateDirectory(Path.Combine(target, "public"));
            File.WriteAllText(Path.Combine(target, "public", "index.php"), "<?php");
        }
    }

    private FakeCommandRunner ScriptClone(int cloneExit = 0, string cloneStderr = "", Action<CommandSpec>? onClone = null) =>
        _runner.On("ls-remote --symref", stdout: LsRemote)
            .On("clone --progress", exitCode: cloneExit, stderr: cloneStderr, onRun: onClone ?? (spec => FakeClone(spec)))
            .On("log -1", stdout: Log(Head, "Merge pull request #6 from Spaceghost/patch-1"));

    [Fact]
    public async Task Clone_ClonaEnTemporal_RenombraAlFinal_YRegistraElSitioConSuGit()
    {
        ScriptClone();
        var lines = new List<string>();

        var result = await Sites.CloneAsync(new CloneRequest(Url), null, new SyncProgress(lines.Add), CancellationToken.None);

        var site = result.Change.Site;
        Assert.Equal("hello-world", site.Name);
        Assert.Equal(Path.Combine(Paths.Www, "hello-world"), site.Folder);
        Assert.Equal("public", site.Root);                                      // Add detecta public/
        Assert.True(File.Exists(Path.Combine(site.Folder, "README")));
        Assert.Empty(Directory.GetDirectories(Paths.Www, ".*"));              // sin temporales
        Assert.Equal("master", result.Branch);
        Assert.Equal("7fd1a60", result.Head?.ShortSha);

        var saved = Sites.Find("hello-world")!;
        Assert.NotNull(saved.Git);
        Assert.Equal("https://github.com/octocat/Hello-World.git", saved.Git.Remote);
        Assert.Equal("master", saved.Git.Branch);
        Assert.Equal(GitProvider.GitHub, saved.Git.Provider);
        Assert.Equal(SiteGit.AccessNone, saved.Git.Access);
        Assert.Equal("7fd1a60", saved.Git.LastCommit?.ShortSha);
        Assert.NotNull(saved.Git.LastPullUtc);

        // git clono en la carpeta temporal, no en la definitiva, y con la rama pedida.
        var clone = _runner.Calls.Single(c => c.Arguments[0] == "clone");
        Assert.StartsWith(Path.Combine(Paths.Www, ".tmp-"), clone.Arguments[^1]);
        Assert.Contains("--branch", clone.Arguments);
        Assert.Equal("master", clone.Arguments[clone.Arguments.ToList().IndexOf("--branch") + 1]);

        var history = Sites.History.Load("hello-world");
        var entry = Assert.Single(history);
        Assert.Equal(GitHistory.Clone, entry.Kind);
        Assert.True(entry.Ok);
        Assert.Equal("7fd1a60", entry.To);
        Assert.True(File.Exists(Sites.Log.FilePath("hello-world")));
        Assert.Contains("clone https://github.com/octocat/Hello-World.git", File.ReadAllText(Sites.Log.FilePath("hello-world")));
    }

    [Fact]
    public async Task Clone_ConNombreRamaYPhpGlobal_LosRespeta()
    {
        ScriptClone(onClone: spec => FakeClone(spec, withPublic: false));

        var result = await Sites.CloneAsync(new CloneRequest(Url, Name: "Tienda.test", Branch: "test", Php: "global"), null, null, CancellationToken.None);

        Assert.Equal("tienda", result.Change.Site.Name);
        Assert.Null(result.Change.Site.Root);
        Assert.Null(result.Change.Site.Php);
        Assert.Equal("test", result.Change.Site.Git?.Branch);
    }

    [Fact]
    public async Task Clone_SiGitFalla_NoQuedaNiCarpetaNiSitio_PeroSiElHistorial()
    {
        ScriptClone(cloneExit: 128, cloneStderr: "Cloning into 'x'...\nerror: RPC failed; curl 18 transfer closed\nfatal: early EOF\n", onClone: spec => FakeClone(spec));

        var ex = await Assert.ThrowsAsync<GitException>(() => Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.NetworkInterrupted, ex.Failure.Kind);
        Assert.False(Directory.Exists(Path.Combine(Paths.Www, "hello-world")));
        Assert.Empty(Directory.GetDirectories(Paths.Www));                      // ni el .tmp- con sus ficheros de solo lectura
        Assert.Null(Sites.Find("hello-world"));
        var entry = Assert.Single(Sites.History.Load("hello-world"));
        Assert.False(entry.Ok);
        Assert.Contains("Se corto la conexion", entry.Error);
        Assert.Contains("FALLO", File.ReadAllText(Sites.Log.FilePath("hello-world")));
    }

    [Fact]
    public async Task Clone_LimpiaSusTemporalesViejos_PeroNoLasCarpetasDelUsuario()
    {
        var leftover = Directory.CreateDirectory(Path.Combine(Paths.Www, ".tmp-" + Guid.NewGuid().ToString("N"))).FullName;
        var mine = Directory.CreateDirectory(Path.Combine(Paths.Www, ".tmp-proyecto")).FullName;
        File.WriteAllText(Path.Combine(mine, "index.php"), "<?php // del usuario");
        ScriptClone(onClone: spec => FakeClone(spec));

        await Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None);

        Assert.False(Directory.Exists(leftover));
        Assert.True(File.Exists(Path.Combine(mine, "index.php")));
    }

    [Fact]
    public async Task Clone_Cancelado_NoDejaNada()
    {
        using var cts = new CancellationTokenSource();
        ScriptClone(onClone: spec => { FakeClone(spec); cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sites.CloneAsync(new CloneRequest(Url), null, null, cts.Token));

        Assert.Empty(Directory.GetDirectories(Paths.Www));
        Assert.Null(Sites.Find("hello-world"));
        Assert.Equal("Cancelado.", Assert.Single(Sites.History.Load("hello-world")).Error);
    }

    [Fact]
    public async Task Clone_CarpetaOcupadaONombreCogido_FallaAntesDeIrALaRed()
    {
        var busy = Path.Combine(Paths.Www, "hello-world");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "index.php"), "");

        var ex = await Assert.ThrowsAsync<RoostException>(() => Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None));
        Assert.Contains("ya existe y no esta vacia", ex.Message);
        Assert.Contains("roost site add", ex.Message);
        Assert.Empty(_runner.Calls);

        Directory.Delete(busy, recursive: true);
        Sites.Add("hello-world");
        var taken = await Assert.ThrowsAsync<RoostException>(() => Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None));
        Assert.Contains("Ya hay un sitio 'hello-world'", taken.Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Clone_EnUnaCarpetaVaciaQueYaExiste_LaUsa()
    {
        ScriptClone();
        var empty = Path.Combine(_root, "proyectos", "hw");
        Directory.CreateDirectory(empty);

        var result = await Sites.CloneAsync(new CloneRequest(Url, Folder: empty), null, null, CancellationToken.None);

        Assert.Equal(empty, result.Change.Site.Folder);
        Assert.True(File.Exists(Path.Combine(empty, "README")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "proyectos"), ".*"));
    }

    [Fact]
    public async Task Clone_RamaInexistente_DiceLasQueHay_YNoClona()
    {
        ScriptClone();

        var ex = await Assert.ThrowsAsync<GitException>(() => Sites.CloneAsync(new CloneRequest(Url, Branch: "develop"), null, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.BranchNotFound, ex.Failure.Kind);
        Assert.Equal("En octocat/Hello-World no hay ninguna rama 'develop'. Las que hay: master, test.", ex.Message);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("clone"));
        Assert.Empty(Directory.Exists(Paths.Www) ? Directory.GetDirectories(Paths.Www) : []);
    }

    [Fact]
    public async Task Clone_PrivadoOInexistente_Falla_YLoApunta()
    {
        _runner.On("ls-remote", exitCode: 128, stderr: "fatal: could not read Username for 'https://github.com': terminal prompts disabled\n");

        var ex = await Assert.ThrowsAsync<GitException>(() => Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.AuthRequired, ex.Failure.Kind);
        Assert.Null(Sites.Find("hello-world"));
        Assert.False(Assert.Single(Sites.History.Load("hello-world")).Ok);
    }

    [Fact]
    public async Task Clone_ConUnProbeYaHecho_NoVuelveAPreguntar()
    {
        _runner.On("clone --progress", onRun: spec => FakeClone(spec)).On("log -1", stdout: Log(Head, "x"));
        var probe = new RemoteProbe(true, false, "master", ["master"], null);

        await Sites.CloneAsync(new CloneRequest(Url, Probe: probe), null, null, CancellationToken.None);

        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("ls-remote"));
    }

    private async Task<Site> ClonedAsync()
    {
        ScriptClone();
        var result = await Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None);
        return result.Change.Site;
    }

    [Fact]
    public async Task Pull_ConCambiosLocales_NoTocaNada_YLoApunta()
    {
        var site = await ClonedAsync();
        _runner.On("status --porcelain=v2", stdout: Dirty);

        var ex = await Assert.ThrowsAsync<GitException>(() => Sites.PullAsync(site.Name, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.LocalChanges, ex.Failure.Kind);
        Assert.Contains("README", ex.Message);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("pull"));
        var last = Sites.History.Load(site.Name)[0];
        Assert.Equal(GitHistory.Pull, last.Kind);
        Assert.False(last.Ok);
    }

    [Fact]
    public async Task Pull_FastForward_ActualizaElUltimoCommitYLaFecha()
    {
        var site = await ClonedAsync();
        var pulledAt = Sites.Find(site.Name)!.Git!.LastPullUtc;
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("pull --ff-only", stdout: "Updating 7fd1a60..b3cbd5b\nFast-forward\n", onRun: _ => _runner.On("log -1", stdout: Log(Next, "Siguiente")))
            .On("diff --name-only", stdout: "README\n")
            .On("rev-parse --abbrev-ref HEAD", stdout: "master\n");

        var result = await Sites.PullAsync(site.Name, null, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(1, result.ChangedFiles);
        var updated = Sites.Find(site.Name)!.Git!;
        Assert.Equal("b3cbd5b", updated.LastCommit?.ShortSha);
        Assert.True(updated.LastPullUtc >= pulledAt);
        var last = Sites.History.Load(site.Name)[0];
        Assert.Equal(GitHistory.Pull, last.Kind);
        Assert.True(last.Ok);
        Assert.Equal("7fd1a60", last.From);
        Assert.Equal("b3cbd5b", last.To);
    }

    [Fact]
    public async Task Checkout_CambiaLaRamaGuardada()
    {
        var site = await ClonedAsync();
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("fetch --progress --prune origin")
            .On("checkout test", onRun: _ => _runner.On("log -1", stdout: Log(Next, "En test")))
            .On("rev-parse --abbrev-ref HEAD", stdout: "test\n");

        var changed = await Sites.CheckoutAsync(site.Name, "test", null, CancellationToken.None);

        Assert.Equal("test", changed.Git?.Branch);
        Assert.Equal("b3cbd5b", changed.Git?.LastCommit?.ShortSha);
        Assert.Equal("test", Sites.Find(site.Name)!.Git!.Branch);
        var last = Sites.History.Load(site.Name)[0];
        Assert.Equal(GitHistory.Checkout, last.Kind);
        Assert.Equal("test", last.Branch);
    }

    [Fact]
    public async Task GitStatus_DaElEstado_YSeEnteraDeLoQueHizoElUsuarioConSuGit()
    {
        var site = await ClonedAsync();
        _runner.On("status --porcelain=v2", stdout: "# branch.oid " + Next + "\n# branch.head test\n# branch.upstream origin/test\n# branch.ab +0 -2\n? nuevo.txt\n")
            .On("log -1", stdout: Log(Next, "Trabajo del usuario"));

        var status = await Sites.GitStatusAsync(site.Name, fetch: false, CancellationToken.None);

        Assert.Equal(2, status.Status.Behind);
        Assert.True(status.Status.IsClean);
        Assert.Equal(["nuevo.txt"], status.Status.Untracked);
        Assert.Equal("test", status.Git.Branch);                       // lo que Roost recuerda se pone al dia
        Assert.Equal("b3cbd5b", Sites.Find(site.Name)!.Git!.LastCommit?.ShortSha);
        Assert.Single(status.History);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("fetch"));

        _runner.On("fetch --progress --prune origin");
        var fetched = await Sites.GitStatusAsync(site.Name, fetch: true, CancellationToken.None);
        Assert.True(fetched.Fetched);
        Assert.Contains(_runner.Commands, c => c.StartsWith("fetch"));
    }

    [Fact]
    public async Task UnSitioSinGit_LoDice_YSiTieneDotGitProponeAdoptarlo()
    {
        var plain = Sites.Add("legacy").Site;
        var ex = await Assert.ThrowsAsync<RoostException>(() => Sites.PullAsync("legacy", null, CancellationToken.None));
        Assert.Contains("roost site clone", ex.Message);

        Directory.CreateDirectory(Path.Combine(plain.Folder, ".git"));
        var adopt = await Assert.ThrowsAsync<RoostException>(() => Sites.PullAsync("legacy", null, CancellationToken.None));
        Assert.Contains("roost site git legacy --adopt", adopt.Message);
    }

    [Fact]
    public async Task Adopt_LeeElRemotoYLaRama_SinCredenciales()
    {
        var plain = Sites.Add("legacy").Site;
        Directory.CreateDirectory(Path.Combine(plain.Folder, ".git"));
        _runner.On("config --get remote.origin.url", stdout: "git@gitlab.com:acme/legacy.git\n")
            .On("rev-parse --abbrev-ref HEAD", stdout: "develop\n")
            .On("log -1", stdout: Log(Head, "Legacy"));

        var adopted = await Sites.AdoptGitAsync("legacy.test", CancellationToken.None);

        Assert.Equal("git@gitlab.com:acme/legacy.git", adopted.Git?.Remote);
        Assert.Equal("develop", adopted.Git?.Branch);
        Assert.Equal(GitProvider.GitLab, adopted.Git?.Provider);
        Assert.Equal(SiteGit.AccessOwn, adopted.Git?.Access);
        Assert.Equal(GitHistory.Adopt, Sites.History.Load("legacy")[0].Kind);

        _runner.On("config --get remote.origin.url", exitCode: 1);
        var ex = await Assert.ThrowsAsync<RoostException>(() => Sites.AdoptGitAsync("legacy", CancellationToken.None));
        Assert.Contains("remoto origin", ex.Message);
    }

    [Fact]
    public async Task Add_Repetido_ConservaElGit_YConOtraCarpetaLoSuelta()
    {
        var site = await ClonedAsync();

        var again = Sites.Add(site.Name, php: "global");
        Assert.NotNull(again.Site.Git);

        var elsewhere = Path.Combine(_root, "otra");
        Directory.CreateDirectory(elsewhere);
        Assert.Null(Sites.Add(site.Name, elsewhere).Site.Git);
    }

    [Fact]
    public void Registry_GuardaYLeeElGit_YLosSitiosViejosSiguenSinEl()
    {
        var registry = new SiteRegistry(Paths);
        registry.Upsert(new Site { Name = "viejo", Folder = @"C:\www\viejo" });
        registry.Upsert(new Site
        {
            Name = "nuevo",
            Folder = @"C:\www\nuevo",
            Git = new SiteGit { Remote = "https://github.com/a/b.git", Branch = "main", Provider = GitProvider.GitHub, LastCommit = new GitCommit("abc", "abc", "Ana", DateTimeOffset.Parse("2026-10-24T10:00:00+02:00"), "Hola") },
        });

        var sites = registry.Load();

        Assert.Null(sites.Single(s => s.Name == "viejo").Git);
        var nuevo = sites.Single(s => s.Name == "nuevo").Git!;
        Assert.Equal("main", nuevo.Branch);
        Assert.Equal(GitProvider.GitHub, nuevo.Provider);
        Assert.Equal("Hola", nuevo.LastCommit?.Subject);
        Assert.Contains("\"provider\": \"GitHub\"", File.ReadAllText(registry.FilePath));   // legible, no un numero
    }

    [Theory]
    [InlineData("https://ana:tok3n@github.com/acme/tienda.git", "https://***@github.com/acme/tienda.git")]
    [InlineData("fatal: unable to access 'https://ana@github.com/acme/tienda.git/': 403", "fatal: unable to access 'https://***@github.com/acme/tienda.git/': 403")]
    [InlineData("https://github.com/acme/tienda.git", "https://github.com/acme/tienda.git")]
    [InlineData("git@github.com:acme/tienda.git", "git@github.com:acme/tienda.git")]
    public void Log_TapaLoQueVaEntreLasBarrasYLaArroba(string line, string expected)
    {
        Assert.Equal(expected, GitLog.Redact(line));
    }

    [Fact]
    public async Task Log_NoGuardaCredencialesAunqueGitLasImprima()
    {
        ScriptClone(cloneExit: 128, cloneStderr: "fatal: unable to access 'https://ana:tok3n@github.com/octocat/Hello-World.git/': The requested URL returned error: 403\n", onClone: _ => { });

        await Assert.ThrowsAsync<GitException>(() => Sites.CloneAsync(new CloneRequest(Url), null, null, CancellationToken.None));

        var log = File.ReadAllText(Sites.Log.FilePath("hello-world"));
        Assert.DoesNotContain("tok3n", log);
        Assert.Contains("https://***@github.com", log);
    }

    public void Dispose()
    {
        Install.InstallFiles.TryDeleteDirectory(_root);
    }

    private sealed class SyncProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
