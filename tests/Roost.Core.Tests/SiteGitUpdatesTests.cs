using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// La comprobación de novedades (Git-app, I8): <c>ls-remote</c> con el commit de cada rama, comparado
/// con el checkout sin descargar nada; si el commit del remoto ya está aquí, se cuentan los que faltan.
/// Sobre el FakeCommandRunner: sin red ni git real.
/// </summary>
public class SiteGitUpdatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-gitupdates-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));
    private SiteManager Sites => new(Paths, new GitClient("git", _runner, new GitEnvironment(Paths)));

    private const string Head = "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d";
    private const string Next = "b3cbd5bbd7e81436d2eee04537ea2b4c0cad4cdf";

    private static string LsRemote(string master) =>
        $"ref: refs/heads/master\tHEAD\n{master}\tHEAD\n{master}\trefs/heads/master\n{Next}\trefs/heads/test\n";

    private static string Status(string oid) => $"# branch.oid {oid}\n# branch.head master\n# branch.upstream origin/master\n# branch.ab +0 -0\n";

    private Site Register()
    {
        var folder = Path.Combine(Paths.Www, "hello");
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        var site = new Site
        {
            Name = "hello",
            Folder = folder,
            Git = new SiteGit { Remote = "https://github.com/octocat/Hello-World.git", Branch = "master", Provider = GitProvider.GitHub },
        };
        new SiteRegistry(Paths).Upsert(site);
        return site;
    }

    [Fact]
    public void ParseLsRemote_DaElCommitDeCadaRama()
    {
        var refs = GitOutput.ParseLsRemote(LsRemote(Head) + "abc\trefs/tags/v1\n" + Next + "\trefs/heads/main^{}\n");

        Assert.Equal(Head, refs.Heads["master"]);
        Assert.Equal(Next, refs.Heads["test"]);
        Assert.False(refs.Heads.ContainsKey("main"));   // lo «peeled» no es una rama
        Assert.Equal(2, refs.Heads.Count);
    }

    [Fact]
    public async Task Probe_LlevaElCommitDeCadaRama()
    {
        _runner.On("ls-remote --symref", stdout: LsRemote(Head));
        var probe = await Sites.ProbeAsync(GitUrl.Parse("https://github.com/octocat/Hello-World.git"), CancellationToken.None);
        Assert.Equal(Head, probe.Heads["master"]);
    }

    [Fact]
    public async Task CheckUpdates_MismoCommit_AlDia_YNoDescargaNada()
    {
        Register();
        _runner.On("ls-remote --symref", stdout: LsRemote(Head)).On("status --porcelain=v2", stdout: Status(Head));

        var check = await Sites.CheckUpdatesAsync("hello", CancellationToken.None);

        Assert.True(check.Ok);
        Assert.True(check.UpToDate);
        Assert.False(check.HasNews);
        Assert.Equal(0, check.Behind);
        Assert.Equal(["master", "test"], check.Branches);
        Assert.True(check.Status?.IsClean);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("fetch") || c.StartsWith("pull"));
    }

    [Fact]
    public async Task CheckUpdates_UnCommitQueNoEstaAqui_HayNovedadesSinContar()
    {
        Register();
        _runner.On("ls-remote --symref", stdout: LsRemote(Next))
            .On("status --porcelain=v2", stdout: Status(Head))
            .On("cat-file -e", exitCode: 1, stderr: "fatal: Not a valid object name");

        var check = await Sites.CheckUpdatesAsync("hello", CancellationToken.None);

        Assert.True(check.HasNews);
        Assert.Null(check.Behind);
        Assert.Equal(Next, check.RemoteSha);
        Assert.Equal(Head, check.LocalSha);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("rev-list"));
        Assert.Equal($"cat-file -e {Next}^{{commit}}", _runner.Commands.Single(c => c.StartsWith("cat-file")));
    }

    [Fact]
    public async Task CheckUpdates_ConElCommitYaTraido_CuentaLosQueFaltan()
    {
        Register();
        _runner.On("ls-remote --symref", stdout: LsRemote(Next))
            .On("status --porcelain=v2", stdout: Status(Head))
            .On("cat-file -e")
            .On("rev-list --count", stdout: "3\n");

        var check = await Sites.CheckUpdatesAsync("hello", CancellationToken.None);

        Assert.True(check.HasNews);
        Assert.Equal(3, check.Behind);
        Assert.Equal($"rev-list --count {Head}..{Next}", _runner.Commands.Single(c => c.StartsWith("rev-list")));
    }

    [Fact]
    public async Task CheckUpdates_SoloCommitsPropiosPorDelante_NoSonNovedades()
    {
        Register();
        _runner.On("ls-remote --symref", stdout: LsRemote(Next))
            .On("status --porcelain=v2", stdout: Status(Head))
            .On("cat-file -e")
            .On("rev-list --count", stdout: "0\n");

        var check = await Sites.CheckUpdatesAsync("hello", CancellationToken.None);

        Assert.True(check.Ok);
        Assert.False(check.HasNews);
        Assert.False(check.UpToDate);
    }

    [Fact]
    public async Task CheckUpdates_SinAcceso_LoDiceSinLanzar()
    {
        Register();
        _runner.On("ls-remote --symref", exitCode: 128, stderr: "remote: Repository not found.\nfatal: repository 'https://github.com/octocat/Hello-World.git/' not found\n");

        var check = await Sites.CheckUpdatesAsync("hello", CancellationToken.None);

        Assert.False(check.Ok);
        Assert.Equal(GitFailureKind.AuthRequired, check.Failure?.Kind);
        Assert.False(check.HasNews);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("status"));
    }

    [Fact]
    public async Task CheckUpdates_UnShaRaro_NoLlegaAGit()
    {
        var client = new GitClient("git", _runner, new GitEnvironment(Paths));
        await Assert.ThrowsAsync<RoostException>(() => client.HasCommitAsync(_root, "--upload-pack=calc", CancellationToken.None));
        await Assert.ThrowsAsync<RoostException>(() => client.CountCommitsAsync(_root, Head, "HEAD~1", CancellationToken.None));
        Assert.Empty(_runner.Calls);
    }

    public void Dispose()
    {
        Install.InstallFiles.TryDeleteDirectory(_root);
    }
}
