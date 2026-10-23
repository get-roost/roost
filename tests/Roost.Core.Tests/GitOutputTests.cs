using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>
/// Los parsers de GitOutput con salidas REALES grabadas de MinGit 2.55.0.3 (un repositorio local
/// hecho con git init --bare, y un ls-remote contra github.com/octocat/Hello-World).
/// </summary>
public class GitOutputTests
{
    private const string LsRemoteLocal =
        "ref: refs/heads/main\tHEAD\n" +
        "c3d39bb0013540ee370bcdfac8c6c9952f556790\tHEAD\n" +
        "9d01eb601ad574cf17f5933e91d0c84a891e5740\trefs/heads/feature/checkout\n" +
        "c3d39bb0013540ee370bcdfac8c6c9952f556790\trefs/heads/main\n";

    private const string LsRemoteGitHub =
        "ref: refs/heads/master\tHEAD\n" +
        "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\tHEAD\n" +
        "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d\trefs/heads/master\n" +
        "b1b3f9723831141a31a1a7252a213e216ea76e56\trefs/heads/octocat-patch-1\n" +
        "b3cbd5bbd7e81436d2eee04537ea2b4c0cad4cdf\trefs/heads/test\n";

    [Fact]
    public void ParseLsRemote_DaLaRamaPorDefectoYLasRamas()
    {
        var local = GitOutput.ParseLsRemote(LsRemoteLocal);
        Assert.Equal("main", local.DefaultBranch);
        Assert.Equal(["feature/checkout", "main"], local.Branches);

        var github = GitOutput.ParseLsRemote(LsRemoteGitHub);
        Assert.Equal("master", github.DefaultBranch);
        Assert.Equal(["master", "octocat-patch-1", "test"], github.Branches);
    }

    [Fact]
    public void ParseLsRemote_IgnoraEtiquetasYPeeled()
    {
        var refs = GitOutput.ParseLsRemote("abc\trefs/tags/v1\nabc\trefs/tags/v1^{}\ndef\trefs/heads/main^{}\nfed\trefs/heads/dev\n");

        Assert.Null(refs.DefaultBranch);
        Assert.Equal(["dev"], refs.Branches);
    }

    [Fact]
    public void ParseStatusV2_Limpio_YAlDia()
    {
        var status = GitOutput.ParseStatusV2(
            "# branch.oid c3d39bb0013540ee370bcdfac8c6c9952f556790\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0\n");

        Assert.Equal("main", status.Branch);
        Assert.Equal("c3d39bb0013540ee370bcdfac8c6c9952f556790", status.Oid);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.True(status.IsClean);
        Assert.False(status.IsDetached);
        Assert.True(status.HasUpstream);
    }

    [Fact]
    public void ParseStatusV2_PorDetrasYConCambios_SeparaLosSeguidosDeLosSinSeguir()
    {
        var status = GitOutput.ParseStatusV2(
            "# branch.oid c3d39bb0013540ee370bcdfac8c6c9952f556790\n" +
            "# branch.head main\n" +
            "# branch.upstream origin/main\n" +
            "# branch.ab +0 -1\n" +
            "1 .M N... 100644 100644 100644 2a64391404d6c6980e09369742df64cca2136031 2a64391404d6c6980e09369742df64cca2136031 index.php\n" +
            "2 R. N... 100644 100644 100644 9766475a4185a151dc9d56d614ffb9aaea3bfd42 9766475a4185a151dc9d56d614ffb9aaea3bfd42 R100 public/robots.md\tpublic/robots.txt\n" +
            "? nuevo.txt\n");

        Assert.Equal(1, status.Behind);
        Assert.Equal(["index.php", "public/robots.md"], status.Changed);
        Assert.Equal(["nuevo.txt"], status.Untracked);
        Assert.False(status.IsClean);
    }

    [Fact]
    public void ParseStatusV2_DivergidoYDetached()
    {
        var diverged = GitOutput.ParseStatusV2("# branch.oid 021ad1bc5b439e239b23fe41a9688948b1840c61\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +1 -1\n");
        Assert.Equal(1, diverged.Ahead);
        Assert.Equal(1, diverged.Behind);

        var detached = GitOutput.ParseStatusV2("# branch.oid 021ad1bc5b439e239b23fe41a9688948b1840c61\n# branch.head (detached)\n");
        Assert.True(detached.IsDetached);
        Assert.False(detached.HasUpstream);

        var initial = GitOutput.ParseStatusV2("# branch.oid (initial)\n# branch.head main\n");
        Assert.Null(initial.Oid);
    }

    [Fact]
    public void ParseStatusV2_RutasConEspaciosYConflictos()
    {
        var status = GitOutput.ParseStatusV2(
            "1 .M N... 100644 100644 100644 aaaa aaaa mi carpeta/mi fichero.php\n" +
            "u UU N... 100644 100644 100644 100644 aaaa bbbb cccc app/Kernel.php\n");

        Assert.Equal(["mi carpeta/mi fichero.php", "app/Kernel.php"], status.Changed);
    }

    [Fact]
    public void ParseLog_UnCommitPorLinea_ConFechaYAcentos()
    {
        var log = GitOutput.ParseLog(
            "c3d39bb0013540ee370bcdfac8c6c9952f556790\u001fc3d39bb\u001fAna Perez\u001f2026-10-24T01:25:59+02:00\u001fVersion 2: añade version.txt\n" +
            "2b343d627daba772576458a68ea1e0193f83dabf\u001f2b343d6\u001fAna Perez\u001f2026-10-24T01:25:59+02:00\u001fPrimer commit\n");

        Assert.Equal(2, log.Count);
        Assert.Equal("c3d39bb", log[0].ShortSha);
        Assert.Equal("c3d39bb0013540ee370bcdfac8c6c9952f556790", log[0].Sha);
        Assert.Equal("Ana Perez", log[0].Author);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 1, 25, 59, TimeSpan.FromHours(2)), log[0].When);
        Assert.Equal("Version 2: añade version.txt", log[0].Subject);
        Assert.Equal("Primer commit", log[1].Subject);
        Assert.Empty(GitOutput.ParseLog(""));
    }

    [Fact]
    public void ParseRemoteBranches_QuitaOriginYSuHead()
    {
        var branches = GitOutput.ParseRemoteBranches("origin\norigin/feature/checkout\norigin/main\norigin/HEAD\nupstream/main\n");

        Assert.Equal(["feature/checkout", "main"], branches);
    }

    [Theory]
    [InlineData("Receiving objects:  40% (512/1284), 12.40 MiB | 3.10 MiB/s", "Recibiendo objetos", 40, 37, "12.40 MiB | 3.10 MiB/s")]
    [InlineData("Receiving objects: 100% (1284/1284), 31.02 MiB | 3.10 MiB/s, done.", "Recibiendo objetos", 100, 85, "31.02 MiB | 3.10 MiB/s")]
    [InlineData("Resolving deltas:  50% (150/300)", "Resolviendo deltas", 50, 90, null)]
    [InlineData("Resolving deltas: 100% (300/300), done.", "Resolviendo deltas", 100, 95, null)]
    [InlineData("Updating files:  50% (10/20)", "Actualizando ficheros", 50, 97, null)]
    [InlineData("remote: Counting objects:  57% (4/7)        ", "Contando objetos", 57, 1, null)]
    [InlineData("remote: Compressing objects: 100% (2/2), done.        ", "Comprimiendo objetos", 100, 5, null)]
    public void ParseProgress_EntiendeCadaFase(string line, string stage, int percent, int overall, string? detail)
    {
        var progress = GitOutput.ParseProgress(line);

        Assert.NotNull(progress);
        Assert.Equal(stage, progress.Stage);
        Assert.Equal(percent, progress.Percent);
        Assert.Equal(overall, progress.Overall);
        Assert.Equal(detail, progress.Detail);
    }

    [Theory]
    [InlineData("Cloning into 'sitio'...")]
    [InlineData("remote: Enumerating objects: 7, done.")]
    [InlineData("done.")]
    [InlineData("")]
    public void ParseProgress_LoQueNoEsProgreso_DaNull(string line)
    {
        Assert.Null(GitOutput.ParseProgress(line));
    }

    [Fact]
    public void Headline_PrefiereLaLineaFatal()
    {
        var stderr = "hint: Diverging branches can't be fast-forwarded, you need to either:\nhint:\nhint: \tgit merge --no-ff\nfatal: Not possible to fast-forward, aborting.\n";

        Assert.Equal("fatal: Not possible to fast-forward, aborting.", GitOutput.Headline(stderr));
        Assert.Equal("error: Your local changes to the following files would be overwritten by merge:", GitOutput.Headline("Updating c3d39bb..e06ff2b\nerror: Your local changes to the following files would be overwritten by merge:\n\tindex.php\nPlease commit your changes or stash them before you merge.\nAborting\n"));
        Assert.Equal("Aborting", GitOutput.Headline("Please commit your changes.\nAborting\n"));   // sin fatal ni error: la ultima
        Assert.Equal("", GitOutput.Headline(""));
    }
}
