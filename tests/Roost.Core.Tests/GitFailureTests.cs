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
    [InlineData("@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\nHost key verification failed.\nfatal: Could not read from remote repository.\n", GitFailureKind.HostKeyChanged)]
    [InlineData("No ED25519 host key is known for git.acme.es and you have requested strict checking.\nHost key verification failed.\nfatal: Could not read from remote repository.\n", GitFailureKind.HostKeyUnverified)]
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

    // --- Con el acceso que se intento (Git-2): token, clave de Roost, «Mi clave SSH», huellas. ---

    private static GitAccess WithToken(GitUrl url) => new() { Url = url, Kind = Sites.SiteGit.AccessToken, CredentialTarget = "Roost:git:https://github.com/acme/tienda" };

    private static readonly GitUrl GitHubSsh = GitUrl.Parse("git@github.com:acme/tienda.git");

    [Theory]
    // Lo que contesta GitHub hoy a un token caducado (y a una contrasena): no basta para decir que era una contrasena.
    [InlineData("remote: Invalid username or token. Password authentication is not supported for Git operations.\nfatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n", GitFailureKind.TokenRejected)]
    [InlineData("remote: Support for password authentication was removed on August 13, 2021.\nfatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n", GitFailureKind.PasswordNotAccepted)]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n", GitFailureKind.TokenRejected)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/acme/tienda.git/' not found\n", GitFailureKind.TokenRejected)]
    [InlineData("remote: The 'acme' organization has enabled or enforced SAML SSO.\nfatal: unable to access 'https://github.com/acme/tienda.git/': The requested URL returned error: 403\n", GitFailureKind.TokenForbidden)]
    [InlineData("fatal: unable to access 'https://github.com/acme/tienda.git/': The requested URL returned error: 403\n", GitFailureKind.TokenForbidden)]
    public void ConUnTokenGuardado_PideAccesoEsQueLoRechazo(string stderr, GitFailureKind kind)
    {
        var failure = GitFailure.Classify(128, stderr, GitHub, access: WithToken(GitHub));

        Assert.Equal(kind, failure.Kind);
        // Sin token, lo mismo es «pide acceso»: lo distingue el llamador, que sabe si habia credencial.
        Assert.Equal(GitFailureKind.AuthRequired, GitFailure.Classify(128, stderr, GitHub).Kind);
    }

    [Fact]
    public void TokenRechazado_ElTextoDelDiseno()
    {
        var failure = GitFailure.Classify(128, "fatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n", GitHub, access: WithToken(GitHub));

        Assert.Equal("GitHub no acepta ese token. Puede haber caducado, ser de otra cuenta o no tener permiso en acme/tienda (necesita Contents: Read). Crea otro y sustituyelo.", failure.Message);
    }

    [Fact]
    public void TokenConSso_ElTextoDelDiseno()
    {
        var failure = GitFailure.Classify(128, "fatal: unable to access 'x': The requested URL returned error: 403\n", GitHub, access: WithToken(GitHub));

        Assert.Equal("El token existe pero la organizacion acme exige autorizarlo (SSO) o no tiene el permiso de contenido (Contents: Read). Abrelo en GitHub y autorizalo.", failure.Message);
    }

    [Fact]
    public void ContrasenaEnVezDeToken_ElTextoDelDiseno()
    {
        var failure = GitFailure.Classify(128, "remote: Support for password authentication was removed on August 13, 2021.\n", GitHub, access: WithToken(GitHub));

        Assert.Equal("GitHub ya no acepta la contrasena de la cuenta: hace falta un token. Crealo en https://github.com/settings/personal-access-tokens/new (permiso Contents: Read).", failure.Message);
    }

    [Fact]
    public void LoGuardadoNoTieneFormaDeToken_ElRechazoEsPorContrasena()
    {
        var access = WithToken(GitHub) with { CredentialLooksLikePassword = true };

        var failure = GitFailure.Classify(128, "remote: Invalid username or token. Password authentication is not supported for Git operations.\nfatal: Authentication failed for 'https://github.com/acme/tienda.git/'\n", GitHub, access: access);

        Assert.Equal(GitFailureKind.PasswordNotAccepted, failure.Kind);
    }

    [Fact]
    public void ClaveDeRoostNoAutorizada_NombraLaClaveYLasDeployKeys()
    {
        var access = new GitAccess { Url = GitHubSsh, Kind = Sites.SiteGit.AccessKey, KeyName = "tienda" };

        var failure = GitFailure.Classify(128, "git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.\n", null, access: access);

        Assert.Equal(GitFailureKind.KeyRejected, failure.Kind);
        Assert.Equal("github.com no acepta la clave de tienda. Asegurate de haber pegado la clave publica en las deploy keys de acme/tienda (y en ese repositorio, no en otro: GitHub no permite reutilizarlas). https://github.com/acme/tienda/settings/keys", failure.Message);
    }

    [Fact]
    public void ClaveDeRoostDeOtroRepositorio_EntraPeroNoLoVe()
    {
        var access = new GitAccess { Url = GitHubSsh, Kind = Sites.SiteGit.AccessKey, KeyName = "tienda" };

        var failure = GitFailure.Classify(128, "ERROR: Repository not found.\nfatal: Could not read from remote repository.\n", GitHubSsh, access: access);

        Assert.Equal(GitFailureKind.KeyRejected, failure.Kind);
        Assert.Contains("deploy keys de otro repositorio", failure.Message);
    }

    [Fact]
    public void MiClaveNoAutorizada_RemiteASuTerminal()
    {
        var failure = GitFailure.Classify(128, "git@github.com: Permission denied (publickey).\n", GitHubSsh, access: new GitAccess { Kind = Sites.SiteGit.AccessOwn });

        Assert.Equal("github.com no acepta ninguna de tus claves SSH (las de ~/.ssh y las del ssh-agent de Windows). Pruebalo en tu terminal: ssh -T git@github.com", failure.Message);
    }

    [Fact]
    public void ClaveConPermisosDeMas_KeyUnusable()
    {
        const string stderr = "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@         WARNING: UNPROTECTED PRIVATE KEY FILE!          @\n@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\nPermissions for 'C:/roost/state/git/keys/tienda' are too open.\nLoad key \"C:/roost/state/git/keys/tienda\": bad permissions\ngit@github.com: Permission denied (publickey).\n";

        Assert.Equal(GitFailureKind.KeyUnusable, GitFailure.Classify(128, stderr, GitHubSsh, access: new GitAccess { Kind = Sites.SiteGit.AccessKey, KeyName = "tienda" }).Kind);
    }

    [Fact]
    public void ServidorDesconocido_DiceComoAceptarlo()
    {
        var failure = GitFailure.Classify(128, "No ED25519 host key is known for [git.acme.es]:2222 and you have requested strict checking.\nHost key verification failed.\n", GitUrl.Parse("ssh://git@git.acme.es:2222/web/tienda.git"));

        Assert.Equal(GitFailureKind.HostKeyUnverified, failure.Kind);
        Assert.EndsWith("roost git hosts add [git.acme.es]:2222", failure.Message);
    }

    [Fact]
    public void HuellaCambiada_DiceLaDeAntesYLaDeAhora_YQueNoSeHaConectado()
    {
        // El aviso de OpenSSH, con la huella que presenta el servidor.
        const string stderr = "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\n@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\n"
            + "IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!\nSomeone could be eavesdropping on you right now (man-in-the-middle attack)!\nIt is also possible that a host key has just been changed.\n"
            + "The fingerprint for the ED25519 key sent by the remote host is\nSHA256:9kLmNoPqRsTuVwXyZaBcDeFgHiJkLmNoPqRsTuVwXyZ.\nPlease contact your system administrator.\n"
            + "Add correct host key in C:/roost/state/git/known_hosts to get rid of this message.\nOffending ED25519 key in C:/roost/state/git/known_hosts:5\n"
            + "Host key for git.acme.es has changed and you have requested strict checking.\nHost key verification failed.\nfatal: Could not read from remote repository.\n";
        var stored = KnownHosts.ParseLine("git.acme.es ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL+pitVcgStKA+2SdBhY6HXfTF8cs0wddhi72/OCTOP6 roost:aceptada-2026-10-12")!;
        var url = GitUrl.Parse("git@git.acme.es:web/tienda.git");
        var access = new GitAccess { Url = url, Kind = Sites.SiteGit.AccessKey, KeyName = "tienda", HostKeys = [stored] };

        var failure = GitFailure.Classify(128, stderr, url, access: access);

        Assert.Equal(GitFailureKind.HostKeyChanged, failure.Kind);
        Assert.Equal($"La huella de git.acme.es ha cambiado respecto a la que Roost tenia guardada (ED25519 {stored.Key.Fingerprint} -> SHA256:9kLmNoPqRsTuVwXyZaBcDeFgHiJkLmNoPqRsTuVwXyZ). "
            + "Puede ser que hayan cambiado la clave del servidor o que alguien se este haciendo pasar por el. No se ha conectado. "
            + "Si quien lo administra confirma el cambio, olvida la huella guardada (roost git hosts remove git.acme.es) y vuelve a comprobar.", failure.Message);
    }

    [Fact]
    public void HuellaCambiada_ConLaSalidaRealDelOpenSshDeWindows()
    {
        // OpenSSH_for_Windows_9.5p2 contra github.com con la ED25519 cambiada a mano en el known_hosts
        // de Roost (grabado el 24-oct-2026; la ruta, cambiada por la de una instalacion normal).
        const string stderr = "@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @\r\n@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@\r\n"
            + "IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!\r\nSomeone could be eavesdropping on you right now (man-in-the-middle attack)!\r\nIt is also possible that a host key has just been changed.\r\n"
            + "The fingerprint for the ED25519 key sent by the remote host is\r\nSHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU.\r\nPlease contact your system administrator.\r\n"
            + "Add correct host key in C:/roost/state/git/known_hosts to get rid of this message.\r\nOffending RSA key in C:/roost/state/git/known_hosts:6\r\n"
            + "Host key for github.com has changed and you have requested strict checking.\r\nHost key verification failed.\r\nfatal: Could not read from remote repository.\r\n";
        var stored = KnownHosts.ParseLine("github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL+pitVcgStKA+2SdBhY6HXfTF8cs0wddhi72/OCTOP6")!;
        var access = new GitAccess { Url = GitHubSsh, Kind = Sites.SiteGit.AccessKey, KeyName = "tienda", HostKeys = [stored], PublishedHost = KnownHosts.BuiltIn[0] };

        var failure = GitFailure.Classify(128, stderr, null, access: access);

        Assert.Equal(GitFailureKind.HostKeyChanged, failure.Kind);
        Assert.Contains($"(ED25519 {stored.Key.Fingerprint} -> SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU)", failure.Message);
        Assert.Equal("Host key for github.com has changed and you have requested strict checking.", failure.Detail);
    }

    [Fact]
    public void HuellaCambiadaDeUnServidorDeSerie_RecuerdaActualizarRoost()
    {
        var access = new GitAccess { Url = GitHubSsh, Kind = Sites.SiteGit.AccessKey, KeyName = "tienda", PublishedHost = KnownHosts.BuiltIn[0] };

        var failure = GitFailure.Classify(128, "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!\nHost key verification failed.\n", null, access: access);

        Assert.Equal(GitFailureKind.HostKeyChanged, failure.Kind);
        Assert.EndsWith("Para github.com, Roost trae la que publica GitHub: si la ha cambiado, actualiza Roost.", failure.Message);
    }
}
