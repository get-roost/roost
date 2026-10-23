using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>GitFailure.Classify con los stderr REALES grabados de MinGit 2.55.0.3 (y los documentados por GitHub).</summary>
public class GitFailureTests
{
    private static readonly GitUrl GitHub = GitUrl.Parse("https://github.com/acme/tienda.git");

    [Fact]
    public void Classify_NoSeResuelveElHost()
    {
        var failure = GitFailure.Classify(128, "fatal: unable to access 'https://no-existe.invalid/x.git/': Could not resolve host: no-existe.invalid\n", GitUrl.Parse("https://no-existe.invalid/x.git"));

        Assert.Equal(GitFailureKind.HostUnreachable, failure.Kind);
        Assert.StartsWith("No se encuentra no-existe.invalid.", failure.Message);
        Assert.Contains("Could not resolve host", failure.Detail);
    }

    [Fact]
    public void Classify_PrivadoOInexistenteEnGitHub_PideAcceso()
    {
        var failure = GitFailure.Classify(128, "fatal: could not read Username for 'https://github.com': terminal prompts disabled\n", GitHub);

        Assert.Equal(GitFailureKind.AuthRequired, failure.Kind);
        Assert.Equal("acme/tienda pide acceso, o no existe: GitHub no distingue entre las dos cosas.", failure.Message);
    }

    [Theory]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/acme/tienda.git/' not found\n")]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n")]
    [InlineData("remote: HTTP Basic: Access denied. The provided password or token is incorrect or your account has 2FA enabled and you must use a personal access token instead of a password.\nfatal: Authentication failed for 'https://gitlab.com/a/b.git/'\n")]
    [InlineData("fatal: unable to access 'https://github.com/acme/tienda.git/': The requested URL returned error: 403\n")]
    public void Classify_LasVariantesDeAcceso_SonAuthRequired(string stderr)
    {
        Assert.Equal(GitFailureKind.AuthRequired, GitFailure.Classify(128, stderr, GitHub).Kind);
    }

    [Fact]
    public void Classify_CarpetaLocalQueNoEsUnRepo()
    {
        var folder = OperatingSystem.IsWindows() ? @"C:\repos\no-existe.git" : "/srv/no-existe.git";
        var stderr = $"fatal: '{folder.Replace('\\', '/')}' does not appear to be a git repository\nfatal: Could not read from remote repository.\n\nPlease make sure you have the correct access rights\nand the repository exists.\n";

        var failure = GitFailure.Classify(128, stderr, GitUrl.Parse(folder));

        Assert.Equal(GitFailureKind.RepoNotFound, failure.Kind);
        Assert.Equal($"No hay ningun repositorio git en {Path.GetFullPath(folder)}.", failure.Message);
    }

    [Fact]
    public void Classify_RamaQueNoExiste_EnClonYEnCheckout()
    {
        var clone = GitFailure.Classify(128, "Cloning into 'otro'...\nfatal: Remote branch nope not found in upstream origin\n", GitHub);
        Assert.Equal(GitFailureKind.BranchNotFound, clone.Kind);
        Assert.Equal("En acme/tienda no hay ninguna rama 'nope'.", clone.Message);

        var checkout = GitFailure.Classify(1, "error: pathspec 'nope' did not match any file(s) known to git\n");
        Assert.Equal(GitFailureKind.BranchNotFound, checkout.Kind);
        Assert.Contains("'nope'", checkout.Message);

        var withList = GitFailure.BranchNotFound("acme/tienda", "develop", ["main", "feature/checkout", "release/1.2"]);
        Assert.Equal("En acme/tienda no hay ninguna rama 'develop'. Las que hay: main, feature/checkout, release/1.2.", withList.Message);
    }

    [Fact]
    public void Classify_CarpetaOcupada()
    {
        var failure = GitFailure.Classify(128, "fatal: destination path 'sitio' already exists and is not an empty directory.\n", GitHub, @"C:\roost\www\sitio");

        Assert.Equal(GitFailureKind.FolderBusy, failure.Kind);
        Assert.StartsWith("sitio ya existe y no esta vacia.", failure.Message);
        Assert.Contains("roost site add", failure.Message);
    }

    [Fact]
    public void Classify_CambiosLocales_NombraLosFicheros()
    {
        var failure = GitFailure.Classify(1, "Updating c3d39bb..e06ff2b\nerror: Your local changes to the following files would be overwritten by merge:\n\tindex.php\n\tapp/Http/Kernel.php\nPlease commit your changes or stash them before you merge.\nAborting\n", null, @"C:\roost\www\tienda");

        Assert.Equal(GitFailureKind.LocalChanges, failure.Kind);
        Assert.Equal(@"Hay cambios sin guardar en C:\roost\www\tienda (index.php, app/Http/Kernel.php). Roost no actualiza ni cambia de rama por encima de tu trabajo. Guardalos (commit o stash) y vuelve a intentarlo.", failure.Message);

        var own = GitFailure.LocalChanges("tienda", ["a.php", "b.php"]);
        Assert.Equal(GitFailureKind.LocalChanges, own.Kind);
        Assert.StartsWith("Hay cambios sin guardar en 2 fichero(s) de tienda (a.php, b.php).", own.Message);
    }

    [Fact]
    public void Classify_HistoriaDivergente()
    {
        var failure = GitFailure.Classify(128, "hint: Diverging branches can't be fast-forwarded, you need to either:\nhint:\nhint: \tgit merge --no-ff\nhint:\nhint: or:\nhint:\nhint: \tgit rebase\nhint:\nhint: Disable this message with \"git config set advice.diverging false\"\nfatal: Not possible to fast-forward, aborting.\n", null, "tienda");

        Assert.Equal(GitFailureKind.Diverged, failure.Kind);
        Assert.Contains("git pull --rebase", failure.Message);
        Assert.Equal("fatal: Not possible to fast-forward, aborting.", failure.Detail);
    }

    [Fact]
    public void Classify_NoEsUnRepositorio()
    {
        var failure = GitFailure.Classify(128, "fatal: not a git repository (or any of the parent directories): .git\n", null, @"C:\roost\www\legacy");

        Assert.Equal(GitFailureKind.NotARepository, failure.Kind);
        Assert.Equal(@"C:\roost\www\legacy no es un repositorio git (no tiene .git).", failure.Message);
    }

    [Theory]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.\n", GitFailureKind.KeyRejected)]
    [InlineData("@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\nHost key verification failed.\nfatal: Could not read from remote repository.\n", GitFailureKind.HostKeyUnverified)]
    [InlineData("fatal: unable to access 'https://git.acme.es/a/b.git/': SSL certificate problem: unable to get local issuer certificate\n", GitFailureKind.TlsError)]
    [InlineData("error: RPC failed; curl 18 transfer closed with outstanding read data remaining\nfatal: early EOF\nfatal: fetch-pack: invalid index-pack output\n", GitFailureKind.NetworkInterrupted)]
    [InlineData("fatal: detected dubious ownership in repository at 'C:/roost/www/tienda'\n", GitFailureKind.DubiousOwnership)]
    public void Classify_ElResto(string stderr, GitFailureKind kind)
    {
        Assert.Equal(kind, GitFailure.Classify(128, stderr, GitHub).Kind);
    }

    [Fact]
    public void Classify_LoDesconocido_DaElCodigoYLaUltimaLineaFatal()
    {
        var failure = GitFailure.Classify(128, "warning: algo\nfatal: cosa rara que no conocemos\n");

        Assert.Equal(GitFailureKind.Unknown, failure.Kind);
        Assert.Equal("git ha fallado (codigo 128): fatal: cosa rara que no conocemos", failure.Message);
    }

    [Fact]
    public void TimedOut_DiceCuantoEsperoYElHost()
    {
        var failure = GitFailure.TimedOut(TimeSpan.FromSeconds(60), GitHub);

        Assert.Equal(GitFailureKind.TimedOut, failure.Kind);
        Assert.StartsWith("github.com no ha contestado en 60 s", failure.Message);
    }

    [Fact]
    public void GitException_LlevaElMensajeDelFallo()
    {
        var ex = new GitException(GitFailure.Classify(128, "fatal: not a git repository\n", null, "x"));

        Assert.IsAssignableFrom<RoostException>(ex);
        Assert.Equal(GitFailureKind.NotARepository, ex.Failure.Kind);
        Assert.Equal(ex.Failure.Message, ex.Message);
    }
}
