using Roost.Core.Git;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>GitClient sobre el FakeCommandRunner: que ordene lo que toca, con el entorno aislado, y que entienda lo que vuelve.</summary>
public class GitClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-gitclient-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private GitClient Client => new(@"C:\roost\bin\git\2.55.0.3\cmd\git.exe", _runner, new GitEnvironment(new RoostPaths(_root, Path.Combine(_root, "cache"))));

    private static readonly GitUrl Url = GitUrl.Parse("https://github.com/octocat/Hello-World.git");

    private const string LsRemote =
        "ref: refs/heads/master\tHEAD\n7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\tHEAD\n7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\trefs/heads/master\nb3cbd5bbd7e81436d2eee04537ea2b4c0cad4cdf\trefs/heads/test\n";

    private const string Clean = "# branch.oid c3d39bb0013540ee370bcdfac8c6c9952f556790\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -1\n";
    private const string Dirty = Clean + "1 .M N... 100644 100644 100644 aaaa aaaa index.php\n";

    private static string Log(string sha, string subject) => $"{sha}\u001f{sha[..7]}\u001fAna\u001f2026-10-24T01:25:59+02:00\u001f{subject}\n";

    [Fact]
    public async Task Probe_PreguntaConLsRemote_ConElEntornoAislado_YEntiendeLasRamas()
    {
        _runner.On("ls-remote --symref", stdout: LsRemote);

        var probe = await Client.ProbeAsync(Url, null, CancellationToken.None);

        Assert.True(probe.Reachable);
        Assert.False(probe.NeedsAuth);
        Assert.Equal("master", probe.DefaultBranch);
        Assert.Equal(["master", "test"], probe.Branches);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["ls-remote", "--symref", "--", "https://github.com/octocat/Hello-World.git", "HEAD", "refs/heads/*"], call.Arguments);
        Assert.Equal("0", call.Environment!["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("1", call.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(GitClient.ProbeTimeout, call.Timeout);
        Assert.True(File.Exists(call.Environment["GIT_CONFIG_GLOBAL"]!));
    }

    [Fact]
    public async Task Probe_PrivadoOInexistente_NoLanza_DiceQuePideAcceso()
    {
        _runner.On("ls-remote", exitCode: 128, stderr: "fatal: could not read Username for 'https://github.com': terminal prompts disabled\n");

        var probe = await Client.ProbeAsync(Url, null, CancellationToken.None);

        Assert.False(probe.Reachable);
        Assert.True(probe.NeedsAuth);
        Assert.Equal(GitFailureKind.AuthRequired, probe.Failure?.Kind);
    }

    [Fact]
    public async Task Probe_SinRed_YPorTiempo()
    {
        _runner.On("ls-remote", exitCode: 128, stderr: "fatal: unable to access 'https://github.com/octocat/Hello-World.git/': Could not resolve host: github.com\n");
        Assert.Equal(GitFailureKind.HostUnreachable, (await Client.ProbeAsync(Url, null, CancellationToken.None)).Failure?.Kind);

        _runner.On("ls-remote", timedOut: true);
        var slow = await Client.ProbeAsync(Url, null, CancellationToken.None);
        Assert.False(slow.Reachable);
        Assert.Equal(GitFailureKind.TimedOut, slow.Failure?.Kind);
    }

    [Fact]
    public async Task Clone_PideLaRama_SinTimeout_YDaElProgresoEnPorcentaje()
    {
        _runner.On("clone --progress", stderr: "Cloning into 'x'...\nremote: Counting objects: 100% (7/7), done.\nReceiving objects:  40% (512/1284), 12.40 MiB | 3.10 MiB/s\nReceiving objects: 100% (1284/1284), done.\nResolving deltas: 100% (300/300), done.\n");
        var progress = new List<CloneProgress>();
        var lines = new List<string>();

        await Client.CloneAsync(Url, "master", @"C:\roost\www\.tmp-1", new SyncProgress<CloneProgress>(progress.Add), new SyncProgress<string>(lines.Add), CancellationToken.None);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["clone", "--progress", "--config", "core.autocrlf=false", "--branch", "master", "--", "https://github.com/octocat/Hello-World.git", @"C:\roost\www\.tmp-1"], call.Arguments);
        Assert.Null(call.Timeout);
        Assert.Equal([3, 37, 85, 95], progress.Select(p => p.Overall));
        Assert.Equal(5, lines.Count);
    }

    [Fact]
    public async Task Clone_QueFalla_LanzaClasificado()
    {
        _runner.On("clone", exitCode: 128, stderr: "fatal: destination path 'tienda' already exists and is not an empty directory.\n");

        var ex = await Assert.ThrowsAsync<GitException>(() => Client.CloneAsync(Url, null, @"C:\roost\www\tienda", null, null, CancellationToken.None));

        Assert.Equal(GitFailureKind.FolderBusy, ex.Failure.Kind);
        Assert.DoesNotContain("--branch", _runner.Commands.Single());
    }

    [Fact]
    public async Task Clone_RechazaUnNombreDeRamaQueParezcaUnaOpcion()
    {
        await Assert.ThrowsAsync<RoostException>(() => Client.CloneAsync(Url, "--upload-pack=calc", @"C:\x", null, null, CancellationToken.None));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Pull_ConCambiosLocales_NoLlamaAPull()
    {
        _runner.On("status --porcelain=v2", stdout: Dirty);

        var ex = await Assert.ThrowsAsync<GitException>(() => Client.PullFastForwardAsync(@"C:\roost\www\tienda", null, CancellationToken.None));

        Assert.Equal(GitFailureKind.LocalChanges, ex.Failure.Kind);
        Assert.Contains("index.php", ex.Message);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("pull"));
    }

    [Fact]
    public async Task Pull_Limpio_HaceFfOnly_YCuentaLosFicheros()
    {
        var before = "c3d39bb0013540ee370bcdfac8c6c9952f556790";
        var after = "e06ff2b0013540ee370bcdfac8c6c9952f556790";
        var pulled = false;
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("log -1", stdout: Log(before, "Version 2"), onRun: _ => { })
            .On("pull --ff-only --progress", stdout: "Updating c3d39bb..e06ff2b\nFast-forward\n", onRun: spec =>
            {
                pulled = true;
                _runner.On("log -1", stdout: Log(after, "Version 3"));
            })
            .On("diff --name-only", stdout: "index.php\nversion.txt\n");

        var result = await Client.PullFastForwardAsync(@"C:\roost\www\tienda", null, CancellationToken.None);

        Assert.True(pulled);
        Assert.True(result.Changed);
        Assert.Equal(before, result.From);
        Assert.Equal(after, result.To);
        Assert.Equal(2, result.ChangedFiles);
        Assert.All(_runner.Calls, c => Assert.Equal(@"C:\roost\www\tienda", c.WorkingDirectory));
    }

    [Fact]
    public async Task Pull_Divergido_LanzaDiverged()
    {
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("log -1", stdout: Log("021ad1bc5b439e239b23fe41a9688948b1840c61", "Cambio local"))
            .On("pull --ff-only", exitCode: 128, stderr: "fatal: Not possible to fast-forward, aborting.\n");

        var ex = await Assert.ThrowsAsync<GitException>(() => Client.PullFastForwardAsync(@"C:\roost\www\tienda", null, CancellationToken.None));

        Assert.Equal(GitFailureKind.Diverged, ex.Failure.Kind);
    }

    [Fact]
    public async Task Checkout_RamaInexistente_DiceLasQueHay()
    {
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("fetch --progress --prune origin")
            .On("checkout nope", exitCode: 1, stderr: "error: pathspec 'nope' did not match any file(s) known to git\n")
            .On("branch -r", stdout: "origin\norigin/feature/checkout\norigin/main\n");

        // El nombre del sitio sale de la carpeta: una ruta de este sistema.
        var folder = OperatingSystem.IsWindows() ? @"C:\roost\www\tienda" : "/var/www/roost/tienda";
        var ex = await Assert.ThrowsAsync<GitException>(() => Client.CheckoutAsync(folder, "nope", null, CancellationToken.None));

        Assert.Equal(GitFailureKind.BranchNotFound, ex.Failure.Kind);
        Assert.Equal("En tienda no hay ninguna rama 'nope'. Las que hay: feature/checkout, main.", ex.Message);
    }

    [Fact]
    public async Task Checkout_Limpio_HaceFetchYCheckout()
    {
        _runner.On("status --porcelain=v2", stdout: Clean)
            .On("fetch --progress --prune origin")
            .On("checkout feature/checkout", stderr: "branch 'feature/checkout' set up to track 'origin/feature/checkout'.\nSwitched to a new branch 'feature/checkout'\n");

        await Client.CheckoutAsync(@"C:\roost\www\tienda", "feature/checkout", null, CancellationToken.None);

        Assert.Equal(["status --porcelain=v2 --branch", "fetch --progress --prune origin", "checkout feature/checkout"], _runner.Commands);
    }

    [Fact]
    public async Task RemoteUrl_YRamaActual()
    {
        _runner.On("config --get remote.origin.url", stdout: "https://github.com/acme/tienda.git\n")
            .On("rev-parse --abbrev-ref HEAD", stdout: "main\n");

        Assert.Equal("https://github.com/acme/tienda.git", await Client.RemoteUrlAsync(@"C:\x", CancellationToken.None));
        Assert.Equal("main", await Client.CurrentBranchAsync(@"C:\x", CancellationToken.None));

        _runner.On("config --get remote.origin.url", exitCode: 1).On("rev-parse --abbrev-ref HEAD", stdout: "HEAD\n");
        Assert.Null(await Client.RemoteUrlAsync(@"C:\x", CancellationToken.None));
        Assert.Null(await Client.CurrentBranchAsync(@"C:\x", CancellationToken.None));
    }

    [Fact]
    public async Task Log_SinCommits_DaVacio_YFueraDeUnRepoLanza()
    {
        _runner.On("log -1", exitCode: 128, stderr: "fatal: your current branch 'main' does not have any commits yet\n");
        Assert.Empty(await Client.LogAsync(@"C:\x", 1, CancellationToken.None));

        _runner.On("log -1", exitCode: 128, stderr: "fatal: not a git repository (or any of the parent directories): .git\n");
        var ex = await Assert.ThrowsAsync<GitException>(() => Client.HeadAsync(@"C:\x", CancellationToken.None));
        Assert.Equal(GitFailureKind.NotARepository, ex.Failure.Kind);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Un IProgress sincrono (Progress&lt;T&gt; despacha por el SynchronizationContext y en los tests llegaria tarde).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
