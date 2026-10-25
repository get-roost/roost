using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Roost.Core.Git;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

// Las pantallas montan cada una su FakeRoost, que pone variables de entorno del proceso (el hosts de
// pruebas, la carpeta de Firefox): dos clases de tests a la vez se las pisarían.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Roost.Desktop.Tests;

/// <summary>
/// Git-app (oleada 4): las pantallas de git del diseño «git al estilo Plesk» (maquetas A-J) sobre la
/// raíz falsa y un git guionizado. Cada una deja su captura en <c>TestResults/snapshots/git-*.png</c>.
/// Nada va a la red, ni ejecuta git, ssh o ssh-keygen, ni toca el Administrador de credenciales, las
/// claves del usuario o un daemon.
/// </summary>
public class GitAppViewTests
{
    private static readonly string SnapshotDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");

    private static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake, GitFixture Git)> OpenAsync(bool running = true)
    {
        var fake = new FakeRoost();
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, version: FakeRoost.Version));
        main.Stack.Show(running ? FakeRoost.Running : null);
        var git = GitFixture.Use(main, fake);
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.Current.ShownAsync();
        main.NotifyConfigChanged();
        return (main, window, fake, git);
    }

    private static void Snapshot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(SnapshotDir);
#pragma warning disable CS0618 // el PNG por defecto basta para revisar la captura
        frame.Save(Path.Combine(SnapshotDir, name + ".png"));
#pragma warning restore CS0618
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    private static void Choose(MainViewModel main, string host) =>
        main.Sites.ChooseCommand.Execute(main.Sites.Rows.Single(r => r.Host == host));

    // ---- A, C, D, F: un repositorio público, de la dirección al sitio en marcha ----

    [AvaloniaFact]
    public async Task DesdeGit_Publico_ComprobarClonarYPanel()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);
        git.Runner.On("ls-remote --symref", stdout: GitFixture.LsRemote());

        var opening = main.Sites.FromGitCommand.ExecuteAsync(null);
        var wizard = Assert.IsType<CloneWizardViewModel>(main.Dialog);
        Assert.Equal("Desde un repositorio git. Paso 1 de 3 · Repositorio", wizard.HeaderSubtitle);
        Assert.False(wizard.CanNext);   // «Siguiente» desactivado hasta comprobar

        // Una dirección con credenciales dentro no se comprueba: texto 1.
        wizard.Url = "https://ana:secreto@github.com/octocat/Hello-World";
        await wizard.CheckCommand.ExecuteAsync(null);
        Assert.Equal(CloneCheck.Failed, wizard.CheckResult);
        Assert.StartsWith("Quita el usuario y la contraseña de la dirección", wizard.CheckTitle);
        Assert.Empty(git.Runner.Calls);

        wizard.Url = "https://github.com/octocat/Hello-World";
        await wizard.CheckCommand.ExecuteAsync(null);
        Assert.Equal(CloneCheck.Public, wizard.CheckResult);
        Assert.Equal("GitHub · octocat/Hello-World · público", wizard.CheckTitle);
        Assert.Equal("Rama por defecto: master · 3 ramas", wizard.CheckDetail);
        Assert.True(wizard.CanNext);
        Assert.Equal("ls-remote --symref -- https://github.com/octocat/Hello-World HEAD refs/heads/*", git.Runner.Commands.Single());
        Snapshot(window, "git-asistente-paso1");

        // Público: del paso 1 al 3, sin Acceso.
        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsStep3);
        Assert.Equal("Paso 3 de 3 · octocat/Hello-World · público", wizard.HeaderSubtitle);
        Assert.Equal("hello-world", wizard.Name);
        Assert.Equal(Path.Combine(fake.Paths.Www, "hello-world"), wizard.Folder);
        Assert.Equal("master (por defecto)", wizard.Branch?.ToString());
        Assert.Equal(["master", "octocat-patch-1", "test"], wizard.Branches.Select(b => b.Name));
        Assert.Equal("No existe: se crea al clonar.", wizard.FolderHint);
        Assert.True(wizard.CreateDb);   // hay MariaDB global
        Assert.Equal("Crear hello_world en MariaDB 11.4", wizard.DbText);
        Assert.Equal($"Clona master de octocat/Hello-World en {wizard.Folder}", wizard.StepClone);
        Snapshot(window, "git-asistente-paso3");

        // Una carpeta ocupada no vale (texto 10); el nombre de un sitio que ya existe, tampoco.
        wizard.Name = "tienda";
        Assert.False(wizard.NameOk);
        Assert.Equal("Ya hay un sitio tienda.test: elige otro nombre.", wizard.NameHint);
        Assert.False(wizard.FolderOk);
        Assert.Contains("ya existe y no está vacía", wizard.FolderHint);
        Assert.False(wizard.CanClone);
        wizard.Name = "hello-world";
        Assert.True(wizard.CanClone);
        wizard.CreateDb = false;   // crearla hablaría con un MariaDB de verdad

        // Clonando: se para a medias para la captura.
        var hold = new TaskCompletionSource();
        git.Runner
            .On("clone --progress", onRun: GitFixture.FakeClone, hold: hold, lines:
            [
                "Cloning into 'C:\\roost\\www\\.tmp-1'...",
                "remote: Enumerating objects: 1284, done.",
                "Receiving objects:  40% (514/1284), 12.40 MiB | 3.10 MiB/s",
            ])
            .On("log -1", stdout: GitFixture.Log(GitFixture.Head, "Merge pull request #6 from Spaceghost/patch-1"))
            .On("status --porcelain=v2", stdout: GitFixture.Status())
            .On("branch -r", stdout: GitFixture.RemoteBranches);
        wizard.CloneCommand.Execute(null);
        await UntilAsync(() => main.Dialog is GitTaskDialogViewModel { Percent: > 0 });
        var cloning = (GitTaskDialogViewModel)main.Dialog!;
        Assert.Equal("Clonando octocat/Hello-World", cloning.Title);
        Assert.Equal($"master → {Path.Combine(fake.Paths.Www, "hello-world")}", cloning.Subtitle);
        Assert.Equal(["Preparar git", "Comprobar el servidor", "Clonar", "Registrar el sitio", "Detectar el proyecto"], cloning.Steps.Select(s => s.Label));
        Assert.Equal(StepState.Done, cloning.Steps[1].State);
        Assert.Equal("github.com, público", cloning.Steps[1].Hint);
        // En Linux, con el git del sistema (GitSource.System, de Terminal L2), el paso dice «el de tu sistema: /usr/bin/git»; con el MinGit de pega, lo de Windows.
        if (OperatingSystem.IsWindows()) Assert.Equal($"{GitCatalog.Windows.Label}, SHA-256 verificado", cloning.Steps[0].Hint);
        else Assert.True(cloning.Steps[0].Hint == $"{GitCatalog.Windows.Label}, SHA-256 verificado" || cloning.Steps[0].Hint?.StartsWith("el de tu sistema: ", StringComparison.Ordinal) == true, cloning.Steps[0].Hint);
        Assert.True(cloning.Steps[2].IsActive);
        Assert.Equal("1.284 objetos", cloning.Steps[2].Hint);
        Assert.Equal("Recibiendo objetos", cloning.Phase);
        Assert.Equal("12.40 MiB | 3.10 MiB/s · 37 %", cloning.ProgressText);
        Assert.True(cloning.ShowCancel);
        Assert.True(cloning.ShowBackground);
        Assert.False(cloning.CanDismiss);
        Snapshot(window, "git-clonando");

        hold.SetResult();
        // Lo de siempre de Nuevo sitio: ofrecer la CA (que nació al dar de alta el sitio) antes del navegador.
        await UntilAsync(() => main.Dialog is TrustCaDialogViewModel);
        main.Dialog!.Close(false);   // «Ahora no»: nada toca el almacén de certificados
        await opening;

        // Registrado con su git, servido y abierto en el navegador.
        var site = new SiteRegistry(fake.Paths).Find("hello-world")!;
        Assert.Equal("https://github.com/octocat/Hello-World", site.Git!.Remote);
        Assert.Equal("master", site.Git.Branch);
        Assert.Null(main.Dialog);
        Assert.Equal(["https://hello-world.test/"], git.Opened);
        Assert.Contains("hello-world.test", File.ReadAllText(fake.HostsFile));
        var clone = git.Runner.Calls.Single(c => c.Arguments[0] == "clone");
        Assert.StartsWith(Path.Combine(fake.Paths.Www, ".tmp-"), clone.Arguments[^1]);   // en temporal

        // El panel (maqueta F) y la columna Git de la lista.
        await UntilAsync(() => main.Sites.Selected?.Host == "hello-world.test" && main.Sites.GitChipText == "al día");
        var sites = main.Sites;
        Assert.True(sites.HasGit);
        Assert.Equal("octocat/Hello-World · GitHub · público, sin credenciales", sites.GitRepoText);
        Assert.Equal("master", sites.GitBranch);
        Assert.Equal(["master", "octocat-patch-1", "test"], sites.GitBranches);
        Assert.Equal("7fd1a60", sites.GitCommitSha);
        Assert.Equal("«Merge pull request #6 from Spaceghost/patch-1»", sites.GitCommitSubject);
        Assert.EndsWith("· The Octocat", sites.GitCommitMeta);
        Assert.True(sites.GitCanUpdate);
        Assert.False(sites.GitUpdatePrimary);
        Assert.Equal("Mira si hay novedades cada 30 min con la app abierta; nunca descarga nada sin que lo pidas.", sites.GitCheckHint);
        Assert.Equal("al día", sites.Rows.Single(r => r.Host == "hello-world.test").GitChip);
        Assert.Equal("—", sites.Rows.Single(r => r.Host == "blog.test").GitChip);
        Snapshot(window, "sitios-git-panel");
    }

    [AvaloniaFact]
    public async Task Clonando_SiFalla_DiceElTextoDelDiseno_YNoQuedaNada()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);
        git.Runner.On("ls-remote --symref", stdout: GitFixture.LsRemote())
            .On("clone --progress", exitCode: 128, stderr: "Cloning into 'x'...\nerror: RPC failed; curl 56 Recv failure: Connection was reset\nfatal: early EOF\nfatal: fetch-pack: invalid index-pack output\n");

        var opening = main.Sites.FromGitCommand.ExecuteAsync(null);
        var wizard = (CloneWizardViewModel)main.Dialog!;
        wizard.Url = "https://github.com/octocat/Hello-World";
        await wizard.CheckCommand.ExecuteAsync(null);
        wizard.NextCommand.Execute(null);
        wizard.CreateDb = false;
        wizard.CloneCommand.Execute(null);
        await UntilAsync(() => main.Dialog is GitTaskDialogViewModel { Done: true });
        var cloning = (GitTaskDialogViewModel)main.Dialog!;

        Assert.True(cloning.Failed);
        Assert.Equal("Se cortó la conexión con github.com a medias. Prueba de nuevo.", cloning.Error);
        Assert.Equal("git: fatal: fetch-pack: invalid index-pack output", cloning.ErrorDetail);
        Assert.True(cloning.ShowLog);
        Assert.Equal(StepState.Failed, cloning.Steps[2].State);
        Assert.Empty(Directory.GetDirectories(fake.Paths.Www, ".tmp-*"));
        Assert.False(Directory.Exists(Path.Combine(fake.Paths.Www, "hello-world")));
        Assert.Null(new SiteRegistry(fake.Paths).Find("hello-world"));
        Snapshot(window, "git-clonando-fallo");

        cloning.CancelCommand.Execute(null);   // «Cerrar»
        await opening;
        Assert.Empty(git.Opened);
    }

    // ---- B: privado, con la clave SSH de Roost, el token y la huella ----

    private const string NotFound = "remote: Repository not found.\nfatal: repository 'https://github.com/acme/tienda/' not found\n";

    private const string KeyDenied = "git@github.com: Permission denied (publickey).\r\nfatal: Could not read from remote repository.\n\nPlease make sure you have the correct access rights\nand the repository exists.\n";

    [AvaloniaFact]
    public async Task DesdeGit_Privado_ClaveDeRoost_ClaveNoAutorizada()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);
        git.Runner.On("ls-remote --symref", exitCode: 128, stderr: NotFound);

        var opening = main.Sites.FromGitCommand.ExecuteAsync(null);
        var wizard = (CloneWizardViewModel)main.Dialog!;
        wizard.Url = "https://github.com/acme/tienda";
        await wizard.CheckCommand.ExecuteAsync(null);
        Assert.Equal(CloneCheck.Private, wizard.CheckResult);
        Assert.Equal("GitHub · acme/tienda · privado (o no existe)", wizard.CheckTitle);
        Assert.Equal("GitHub no distingue entre «no existe» y «no tienes acceso». En el paso siguiente eliges cómo entrar.", wizard.CheckDetail);
        Snapshot(window, "git-asistente-paso1-privado");

        wizard.NextCommand.Execute(null);
        Assert.True(wizard.IsStep2);
        Assert.Equal("Acceso a acme/tienda", wizard.HeaderTitle);
        Assert.Equal("Paso 2 de 3 · El repositorio es privado", wizard.HeaderSubtitle);
        Assert.True(wizard.IsTokenTab);   // por https, el token
        Assert.Equal("En GitHub el usuario da igual: vale el token.", wizard.TokenUserHint);
        Assert.Equal("Permiso que necesita: Contents: Read.", wizard.TokenPermissionText);
        Assert.Equal("Tu contraseña de GitHub no sirve: GitHub solo acepta tokens.", wizard.PasswordNote);
        Assert.Equal($"Se guarda en {OsTexts.CredentialStore}, cifrado para tu usuario, y solo vale para acme/tienda. Nunca va en la dirección del repositorio ni en los registros.", wizard.TokenStorageText);
        wizard.TokenText = "mi-contrasena";
        Assert.True(wizard.HasTokenWarning);   // no tiene pinta de token de GitHub
        Snapshot(window, "git-asistente-paso2-token");
        wizard.TokenText = "";

        // La clave de Roost: por SSH, con la huella de serie de GitHub.
        wizard.IsKeyTab = true;
        Assert.Equal(GitAccessTab.RoostKey, wizard.Tab);
        Assert.Equal("tienda-2", wizard.KeyName);   // ya hay un sitio tienda: la clave (y el sitio) se llaman tienda-2
        Assert.Equal(HostCheck.Published, wizard.HostState);
        Assert.Equal("Primera conexión con github.com", wizard.HostTitle);
        Assert.Equal("ED25519 SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU", wizard.HostFingerprint);
        Assert.Equal("Coincide con la que publica GitHub. Roost la recordará.", wizard.HostText);
        Assert.Equal(Tone.Ok, wizard.HostTone);
        Assert.Equal("2. En GitHub › Settings › Deploy keys › Add deploy key, pégala. Deja «Allow write access» sin marcar.", wizard.DeployKeySteps[1]);

        // Probar sin clave no va a la red.
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal("Genera la clave (o usa un fichero tuyo) y ponla como deploy key antes de probar.", wizard.AccessMessage);
        Assert.Single(git.Runner.Calls);

        git.Runner.On("-q -t ed25519", onRun: GitFixture.FakeKeygen);
        await wizard.GenerateKeyCommand.ExecuteAsync(null);
        Assert.True(wizard.HasPublicKey);
        Assert.StartsWith("ssh-ed25519 AAAAC3", wizard.PublicKey);
        Assert.EndsWith($"roost:tienda-2@{Environment.MachineName}", wizard.PublicKey);
        wizard.OpenDeployKeysCommand.Execute(null);
        Assert.Equal(["https://github.com/acme/tienda/settings/keys"], git.Opened);
        Snapshot(window, "git-asistente-paso2-clave");

        // GitHub no la acepta: texto 7, con el nombre de la clave y el aviso de reutilizarlas.
        git.Runner.On("ls-remote --symref", exitCode: 128, stderr: KeyDenied);
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal(CloneWizardViewModel.AccessCheck.Failed, wizard.AccessState);
        Assert.Equal("github.com no acepta la clave de tienda-2. Asegúrate de haber pegado la clave pública en las deploy keys de acme/tienda (y en ese repositorio, no en otro: GitHub no permite reutilizarlas).",
            wizard.AccessMessage);
        Assert.False(wizard.CanNext);
        var probe = git.Runner.Calls.Last();
        Assert.Equal("ls-remote --symref -- git@github.com:acme/tienda.git HEAD refs/heads/*", string.Join(' ', probe.Arguments));
        Assert.Contains("keys/tienda-2", probe.Environment!["GIT_SSH_COMMAND"]!.Replace('\\', '/'));
        Assert.Contains("StrictHostKeyChecking=yes", probe.Environment["GIT_SSH_COMMAND"]);
        Snapshot(window, "git-asistente-paso2-clave-rechazada");

        // Ya pegada: correcto, y el paso 3 sale con el nombre de la clave.
        git.Runner.On("ls-remote --symref", stdout: "ref: refs/heads/main\tHEAD\n" + GitFixture.Head + "\tHEAD\n" + GitFixture.Head + "\trefs/heads/main\n");
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal("correcto · main", wizard.AccessMessage);
        Assert.True(wizard.ShowNext2);
        wizard.NextCommand.Execute(null);
        Assert.Equal("tienda-2", wizard.Name);   // el de la clave
        Assert.Equal("Paso 3 de 3 · acme/tienda · acceso con clave SSH de Roost", wizard.HeaderSubtitle);

        // Cancelar: la clave generada se queda (puede que ya esté pegada en GitHub).
        wizard.CancelCommand.Execute(null);
        await opening;
        Assert.True(git.Context.Access.Keys.Exists("tienda-2"));
    }

    [AvaloniaFact]
    public async Task DesdeGit_TokenHttps_SeGuardaAlProbar_YSeDeshaceAlCancelar()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);
        git.Runner.On("ls-remote --symref", exitCode: 128, stderr: NotFound);

        var opening = main.Sites.FromGitCommand.ExecuteAsync(null);
        var wizard = (CloneWizardViewModel)main.Dialog!;
        wizard.Url = "https://github.com/acme/tienda";
        await wizard.CheckCommand.ExecuteAsync(null);
        wizard.NextCommand.Execute(null);

        // Un token que GitHub rechaza: texto 4.
        git.Runner.On("ls-remote --symref", exitCode: 128, stderr: "remote: Invalid username or token. Password authentication is not supported for Git operations.\nfatal: Authentication failed for 'https://github.com/acme/tienda/'\n");
        wizard.TokenText = "ghp_caducado0123456789";
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal("GitHub no acepta ese token. Puede haber caducado, ser de otra cuenta o no tener permiso en acme/tienda (necesita Contents: Read). Crea otro y sustitúyelo.", wizard.AccessMessage);
        var target = Assert.Single(git.Store.Items).Target;
        Assert.EndsWith("git:https://github.com/acme/tienda", target);
        Assert.Equal("", wizard.TokenText);   // ya guardado: el campo se vacía
        Assert.True(wizard.HasExistingToken);
        // Git lleva el helper de Roost, nunca el token.
        var probe = git.Runner.Calls.Last();
        Assert.DoesNotContain("ghp_caducado", string.Join(' ', probe.Arguments) + string.Join(' ', probe.Environment!.Values));

        // Uno bueno: correcto.
        git.Runner.On("ls-remote --symref", stdout: GitFixture.LsRemote());
        wizard.TokenText = "github_pat_bueno0123456789";
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal("correcto · master y 2 ramas más", wizard.AccessMessage);
        Assert.Equal("github_pat_bueno0123456789", git.Store.Read(target)!.Secret);

        // Cancelar el asistente: el token recién guardado no se queda sin sitio.
        wizard.CancelCommand.Execute(null);
        await opening;
        Assert.Empty(git.Store.Items);
    }

    [AvaloniaFact]
    public async Task DesdeGit_ServidorPropio_HuellaDesconocida_Confiar_YLuegoCambiada()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);

        var opening = main.Sites.FromGitCommand.ExecuteAsync(null);
        var wizard = (CloneWizardViewModel)main.Dialog!;
        wizard.Url = "git@git.acme.es:web/tienda.git";
        await wizard.CheckCommand.ExecuteAsync(null);
        Assert.Equal(CloneCheck.Ssh, wizard.CheckResult);
        Assert.Equal("git.acme.es · web/tienda · por SSH", wizard.CheckTitle);
        Assert.Empty(git.Runner.Calls);   // por SSH sin clave no se lanza nada

        git.Runner.On("-T 10 -t ed25519,ecdsa,rsa", stdout: $"git.acme.es ssh-ed25519 {GitFixture.OtherEd25519}\n");
        wizard.NextCommand.Execute(null);
        Assert.Equal("Paso 2 de 3 · Por SSH hace falta una clave", wizard.HeaderSubtitle);
        Assert.False(wizard.CanToken);   // de un servidor propio no se sabe la https
        Assert.True(wizard.IsKeyTab);
        await UntilAsync(() => wizard.HostState == HostCheck.Unknown);
        Assert.Equal("Primera conexión con git.acme.es", wizard.HostTitle);
        Assert.Equal($"Huella ED25519 {KnownHosts.Fingerprint(GitFixture.OtherEd25519)}", wizard.HostFingerprint);
        Assert.Equal("Roost no puede comprobar esta huella. Compárala con la que te dé quien administra git.acme.es y, si coincide, pulsa Confiar en este servidor.", wizard.HostText);
        Assert.True(wizard.CanTrustHost);
        Assert.Equal(Tone.Warn, wizard.HostTone);
        Snapshot(window, "git-asistente-paso2-huella-desconocida");

        git.Runner.On("-q -t ed25519", onRun: GitFixture.FakeKeygen);
        await wizard.GenerateKeyCommand.ExecuteAsync(null);
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal("Antes, compara la huella del servidor y pulsa Confiar en este servidor.", wizard.AccessMessage);

        wizard.TrustHostCommand.Execute(null);
        Assert.Equal(HostCheck.Trusted, wizard.HostState);
        Assert.Equal(HostTrust.Trusted, git.Context.Access.KnownHosts.Status("git.acme.es").Trust);

        // El servidor presenta otra clave: rojo, con las dos huellas (texto 8).
        git.Runner.On("ls-remote --symref", exitCode: 128, stderr: """
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @
            @@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@
            IT IS POSSIBLE THAT SOMEONE IS DOING SOMETHING NASTY!
            The fingerprint for the ED25519 key sent by the remote host is
            SHA256:9kLmAbCdEfGhIjKlMnOpQrStUvWxYz0123456789abc.
            Host key for git.acme.es has changed and you have requested strict checking.
            Host key verification failed.
            fatal: Could not read from remote repository.
            """);
        await wizard.TestAccessCommand.ExecuteAsync(null);
        Assert.Equal(HostCheck.Changed, wizard.HostState);
        Assert.Equal(Tone.Bad, wizard.HostTone);
        Assert.Equal($"La huella de git.acme.es ha cambiado respecto a la que Roost tenía guardada ({KnownHosts.Fingerprint(GitFixture.OtherEd25519)} → SHA256:9kLmAbCdEfGhIjKlMnOpQrStUvWxYz0123456789abc). "
            + "Puede ser que hayan cambiado la clave del servidor o que alguien se esté haciendo pasar por él. No se ha conectado. Si quien lo administra confirma el cambio, pulsa Olvidar en Ajustes › Git y vuelve a comprobar.",
            wizard.HostText);
        Snapshot(window, "git-asistente-paso2-huella-cambiada");

        // Mi clave SSH: solo los nombres de .ssh y el estado del ssh-agent.
        wizard.IsOwnTab = true;
        Assert.Equal("Encontrada id_ed25519 · servicio ssh-agent: parado", wizard.OwnFound);
        Snapshot(window, "git-asistente-paso2-mi-clave");

        wizard.CancelCommand.Execute(null);
        await opening;
    }

    // ---- F: los estados del bloque Git del panel y de la lista ----

    [AvaloniaFact]
    public async Task PanelGit_CambiosLocales_PorDetras_SinAcceso_YAdoptar()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        GitFixture.Register(fake, "hello", GitFixture.HelloGit());
        var adoptFolder = fake.Project("proyecto", ".git/", "index.php");
        new SiteRegistry(fake.Paths).Upsert(new Site { Name = "proyecto", Folder = adoptFolder });
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status(changed: ["app/Http/Kernel.php", ".env"]))
            .On("log -1", stdout: GitFixture.Log(GitFixture.Head, "Merge pull request #6 from Spaceghost/patch-1"))
            .On("branch -r", stdout: GitFixture.RemoteBranches)
            .On("config --get remote.origin.url", stdout: "git@github.com:acme/proyecto.git\n");

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        await UntilAsync(() => sites.Rows.Single(r => r.Host == "hello.test").GitChip == "2 ✎");
        Choose(main, "hello.test");
        await UntilAsync(() => sites.GitChipText == "2 cambios locales");
        Assert.Equal(Tone.Warn, sites.GitChipTone);
        Assert.Equal("Actualizar y cambiar de rama se desactivan hasta que los guardes o descartes.", sites.GitNote);
        Assert.False(sites.GitCanUpdate);
        Assert.False(sites.GitCanBranch);
        Assert.True(sites.GitDirty);
        Snapshot(window, "sitios-git-cambios-locales");

        // Tres commits por detrás de lo último que se trajo: Actualizar en primario.
        // «Comprobar ahora» lo pide el usuario: trae lo nuevo (fetch, sin tocar la carpeta) y cuenta.
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status(behind: 3)).On("fetch --progress --prune origin");
        await sites.GitCheckNowCommand.ExecuteAsync(null);
        Assert.Contains(git.Runner.Commands, c => c.StartsWith("fetch --progress --prune origin", StringComparison.Ordinal));
        Assert.DoesNotContain(git.Runner.Commands, c => c.StartsWith("pull", StringComparison.Ordinal));
        Assert.Equal("3 commits por detrás", sites.GitChipText);
        Assert.True(sites.GitUpdatePrimary);
        Assert.True(sites.GitCanUpdate);
        Assert.Equal("3 ↓", sites.Rows.Single(r => r.Host == "hello.test").GitChip);
        Snapshot(window, "sitios-git-por-detras");

        // Con un token que ya no vale: «sin acceso», Acceso… en primario.
        var location = CredentialLocation.For(GitUrl.Parse("https://github.com/octocat/Hello-World.git"))!;
        var target = git.Context.Access.Credentials.Save(location, "ghp_caducado0123456789");
        new SiteRegistry(fake.Paths).Upsert(new SiteRegistry(fake.Paths).Find("hello")! with { Git = GitFixture.HelloGit(SiteGit.AccessToken, target: target) });
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status())
            .On("ls-remote --symref", exitCode: 128, stderr: "remote: Invalid username or token.\nfatal: Authentication failed for 'https://github.com/octocat/Hello-World.git/'\n");
        main.NotifyConfigChanged();
        Assert.Equal(0, await sites.CheckGitUpdatesAsync(force: true));   // ninguno con novedades: uno sin acceso
        Assert.Null(main.Dialog);   // sin avisos
        Choose(main, "hello.test");
        await UntilAsync(() => sites.GitChipText == "sin acceso: el token no vale");
        Assert.Equal(Tone.Bad, sites.GitChipTone);
        Assert.True(sites.GitAccessPrimary);
        Assert.StartsWith("GitHub no acepta ese token.", sites.GitNote);
        Assert.Equal("sin acceso", sites.Rows.Single(r => r.Host == "hello.test").GitChip);
        Assert.Equal("token HTTPS", GitTexts.AccessLabel(new SiteRegistry(fake.Paths).Find("hello")!.Git!));
        Snapshot(window, "sitios-git-sin-acceso");

        // Un .git que Roost no gestiona: «repositorio detectado» y Adoptar.
        Choose(main, "proyecto.test");
        Assert.True(sites.GitDetected);
        Assert.False(sites.HasGit);
        await UntilAsync(() => sites.GitDetectedText == "repositorio detectado · acme/proyecto · GitHub");
        Assert.Equal("sin adoptar", sites.Rows.Single(r => r.Host == "proyecto.test").GitChip);
        Snapshot(window, "sitios-git-sin-adoptar");

        git.Runner.On("rev-parse --abbrev-ref HEAD", stdout: "main\n");
        await sites.GitAdoptCommand.ExecuteAsync(null);
        var adopted = new SiteRegistry(fake.Paths).Find("proyecto")!;
        Assert.Equal("git@github.com:acme/proyecto.git", adopted.Git!.Remote);
        Assert.Equal(SiteGit.AccessOwn, adopted.Git.Access);
    }

    // ---- Actualizar, cambiar de rama e historial (G, H) ----

    [AvaloniaFact]
    public async Task PanelGit_Actualizar_CambiarDeRama_EHistorial()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        GitFixture.Register(fake, "hello", GitFixture.HelloGit());
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status(behind: 1))
            .On("log -1", stdout: GitFixture.Log(GitFixture.Head, "Merge pull request #6 from Spaceghost/patch-1"))
            .On("branch -r", stdout: GitFixture.RemoteBranches);
        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        Choose(main, "hello.test");
        await UntilAsync(() => sites.GitChipText == "1 commit por detrás");

        // Actualizar: el diálogo de pasos, a medias para la captura.
        var hold = new TaskCompletionSource();
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status())
            .On("pull --ff-only", hold: hold, lines: ["remote: Counting objects: 100% (3/3), done.", "Receiving objects:  50% (1/2)"],
                onRun: _ => git.Runner.On("log -1", stdout: GitFixture.Log(GitFixture.Next, "Arregla el README")))
            .On("diff --name-only", stdout: "README\n")
            .On("rev-parse --abbrev-ref HEAD", stdout: "master\n");
        var updating = sites.GitUpdateCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is GitTaskDialogViewModel { Percent: > 0 });
        var dialog = (GitTaskDialogViewModel)main.Dialog!;
        Assert.Equal("Actualizando hello.test", dialog.Title);
        Assert.Equal(["Comprobar que no hay cambios locales", "Traer lo nuevo de origin", "Avanzar master"], dialog.Steps.Select(s => s.Label));
        Assert.True(dialog.Steps[1].IsActive);
        Assert.True(sites.GitBusy);
        Snapshot(window, "git-actualizar");
        hold.SetResult();
        await updating;
        Assert.Null(main.Dialog);
        Assert.Equal("hello.test: 7fd1a60 → b3cbd5b, 1 fichero", main.FlashText);
        await UntilAsync(() => sites.GitChipText == "al día");
        Assert.Equal("b3cbd5b", sites.GitCommitSha);

        // Cambiar de rama: confirmación (maqueta H) y Cancelar deja el desplegable como estaba.
        sites.GitBranch = "test";
        var confirm = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("Cambiar hello.test a test", confirm.Title);
        Assert.Equal("Se descarga la rama y se cambia el checkout. Las migraciones y las dependencias pueden cambiar: Roost te lo dirá al acabar.", confirm.Text);
        Snapshot(window, "git-cambiar-rama");
        confirm.Close(false);
        await UntilAsync(() => main.Dialog is null && sites.GitBranch == "master");

        // Un pull que no es fast-forward: texto 12, sin tocar nada.
        git.Runner.On("pull --ff-only", exitCode: 128, stderr: "hint: Diverging branches can't be fast-forwarded, you need to either:\nfatal: Not possible to fast-forward, aborting.\n");
        var failing = sites.GitUpdateCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is GitTaskDialogViewModel { Done: true });
        dialog = (GitTaskDialogViewModel)main.Dialog!;
        Assert.Equal("master en tu equipo tiene commits que no están en el remoto, así que no se puede actualizar sin fusionar. Hazlo desde la terminal (git pull --rebase, o merge).", dialog.Error);
        dialog.CancelCommand.Execute(null);
        await failing;

        // El historial: lo que hizo Roost, de lo más nuevo a lo más viejo (maqueta G).
        var showing = sites.GitHistoryCommand.ExecuteAsync(null);
        var history = Assert.IsType<GitHistoryDialogViewModel>(main.Dialog);
        Assert.Equal("Historial de hello.test", history.Title);
        Assert.Equal(["Actualizar", "Actualizar"], history.Rows.Select(r => r.What));
        Assert.False(history.Rows[0].Ok);
        // El fallo se cuenta por su tipo, con tildes (lo que apuntó Core va sin ellas).
        Assert.StartsWith("master en tu equipo tiene commits que no están en el remoto", history.Rows[0].Error);
        Assert.Equal("7fd1a60 → b3cbd5b (master)", history.Rows[1].Range);
        Assert.Equal("✓", history.Rows[1].Mark);
        Snapshot(window, "git-historial");
        history.FinishCommand.Execute(null);
        await showing;
    }

    // ---- I: Acceso… ----

    [AvaloniaFact]
    public async Task Acceso_ConLaClaveDeRoost_YConToken()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        git.Runner.On("-q -t ed25519", onRun: GitFixture.FakeKeygen);
        await git.Context.Access.Keys.GenerateAsync("tienda-git", CancellationToken.None);
        GitFixture.Register(fake, "tienda-git", GitFixture.HelloGit(SiteGit.AccessKey, "tienda-git", remote: "git@github.com:acme/tienda.git"));
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status())
            .On("log -1", stdout: GitFixture.Log(GitFixture.Head, "Ajusta el checkout"))
            .On("branch -r", stdout: GitFixture.RemoteBranches)
            .On("ls-remote --symref", stdout: GitFixture.LsRemote());
        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda-git.test");
        await UntilAsync(() => main.Sites.GitRepoText == "acme/tienda · GitHub · clave SSH de Roost");

        var opening = main.Sites.GitAccessCommand.ExecuteAsync(null);
        var access = Assert.IsType<GitAccessDialogViewModel>(main.Dialog);
        Assert.Equal("Acceso de tienda-git.test", access.Title);
        Assert.Equal("git@github.com:acme/tienda.git · clave SSH de Roost", access.Subtitle);
        Assert.True(access.IsKey);
        Assert.StartsWith("ssh-ed25519 AAAAC3", access.PublicKey);
        Assert.Equal("github.com · huella conocida (publicada por GitHub)", access.ServerText);
        Assert.Equal("sin comprobar en esta sesión", access.LastText);
        await access.TestCommand.ExecuteAsync(null);
        Assert.EndsWith("· correcto", access.LastText);
        Assert.Equal(Tone.Ok, access.LastTone);
        Snapshot(window, "git-acceso");
        access.FinishCommand.Execute(null);
        await opening;
        await UntilAsync(() => main.Sites.GitChipText == "al día");

        // Un sitio por https sin token: se puede guardar uno.
        GitFixture.Register(fake, "hello", GitFixture.HelloGit());
        main.NotifyConfigChanged();
        Choose(main, "hello.test");
        opening = main.Sites.GitAccessCommand.ExecuteAsync(null);
        access = Assert.IsType<GitAccessDialogViewModel>(main.Dialog);
        Assert.True(access.IsHttps);
        Assert.False(access.HasToken);
        Assert.Equal("No hay ningún token guardado para github.com/octocat/Hello-World: sin él, git entra como público.", access.TokenText);
        Assert.Equal("Guardar un token…", access.TokenLabel);
        var saving = access.ReplaceTokenCommand.ExecuteAsync(null);
        var token = Assert.IsType<GitTokenDialogViewModel>(main.Dialog);
        token.Token = "github_pat_nuevo0123456789";
        Snapshot(window, "git-acceso-token");
        token.SaveCommand.Execute(null);
        await saving;
        Assert.True(access.HasToken);
        Assert.StartsWith("Token de github.com/octocat/Hello-World (usuario x-access-token)", access.TokenText);
        Assert.Equal(SiteGit.AccessToken, new SiteRegistry(fake.Paths).Find("hello")!.Git!.Access);
        access.FinishCommand.Execute(null);
        await opening;
    }

    // ---- J: Ajustes › Git ----

    [AvaloniaFact]
    public async Task AjustesGit_BinarioClavesTokensServidoresYNovedades()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        git.Runner.On("-q -t ed25519", onRun: GitFixture.FakeKeygen)
            .On("-V", stderr: "OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2\n");
        await git.Context.Access.Keys.GenerateAsync("tienda", CancellationToken.None);
        GitFixture.Register(fake, "tienda-git", GitFixture.HelloGit(SiteGit.AccessKey, "tienda", remote: "git@github.com:acme/tienda.git"));
        git.Context.Access.Credentials.Save(CredentialLocation.For(GitUrl.Parse("https://github.com/acme/blog"))!, "github_pat_x0123456789");
        git.Context.Access.KnownHosts.Trust("git.acme.es", null, [new SshHostKey("ssh-ed25519", GitFixture.OtherEd25519)]);

        // La tarjeta Git de Ajustes lleva a la página.
        await main.NavigateToAsync(main.Settings);
        await main.Settings.GoGitCommand.ExecuteAsync(null);
        var settings = main.GitSettings;
        Assert.Same(settings, main.Current);
        Assert.True(main.RoostNav.Single(n => n.Label == "Ajustes").IsActive);
        Assert.True(settings.UseRoostGit);
        Assert.StartsWith($"{GitCatalog.Windows.Label} · ", settings.GitText);
        Assert.Equal("OpenSSH de Windows 9.5p1 · " + Path.Combine(fake.Paths.Root, "openssh-de-pega"), settings.SshText);
        Assert.Equal($"ssh-agent: parado · lo usa «Mi clave SSH» (las claves de {Path.Combine(fake.Paths.Root, "usuario-ssh")})", settings.AgentText);
        var key = Assert.Single(settings.Keys);
        Assert.Equal("tienda", key.Name);
        Assert.Equal("tienda-git.test · GitHub acme/tienda", key.Detail);
        var tokenRow = Assert.Single(settings.Tokens);
        Assert.Equal("github.com/acme/blog", tokenRow.Where);
        Assert.Equal(["git.acme.es", "github.com", "gitlab.com", "bitbucket.org"], settings.Hosts.Select(h => h.Token));
        Assert.StartsWith("aceptada el ", settings.Hosts[0].Source);
        Assert.Equal("de serie (GitHub)", settings.Hosts[1].Source);
        Assert.True(settings.CheckUpdates);
        Assert.Equal(30, settings.CheckMinutes);
        Snapshot(window, "ajustes-git");

        // Cada cuánto: se guarda y la comprobación se rearranca con el nuevo intervalo.
        settings.CheckMinutes = 60;
        Assert.Equal(60, main.App.Prefs.GitCheckMinutes);
        Assert.Equal("Mira si hay novedades cada 60 min con la app abierta; nunca descarga nada sin que lo pidas.", main.Sites.GitCheckHint);

        // Otro git que no existe no se guarda.
        settings.UseOtherGit = true;
        settings.OtherGit = @"C:\no\existe\git.exe";
        await settings.SaveGitCommand.ExecuteAsync(null);
        Assert.True(settings.HasGitError);
        Assert.Null(Roost.Core.Config.RoostConfig.Load(fake.Paths.ConfigFile).GitExe);
    }

    // ---- I8: comprobar novedades ----

    [AvaloniaFact]
    public async Task Novedades_LsRemote_NuevoSinContar_OContado_UnaALaVez()
    {
        var (main, window, fake, git) = await OpenAsync();
        using var _ = fake;
        GitFixture.Register(fake, "hello", GitFixture.HelloGit());
        GitFixture.Register(fake, "otro", GitFixture.HelloGit() with { CheckUpdates = false });
        git.Runner.On("status --porcelain=v2", stdout: GitFixture.Status())
            .On("log -1", stdout: GitFixture.Log(GitFixture.Head, "Merge pull request #6 from Spaceghost/patch-1"))
            .On("branch -r", stdout: GitFixture.RemoteBranches)
            .On("ls-remote --symref", stdout: GitFixture.LsRemote(GitFixture.Next))
            .On("cat-file -e", exitCode: 1, stderr: "fatal: Not a valid object name");
        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;

        Assert.Equal(1, await sites.CheckGitUpdatesAsync(force: true));
        Assert.Single(git.Runner.Commands, c => c.StartsWith("ls-remote", StringComparison.Ordinal));   // «otro» no quiere que se mire
        Assert.DoesNotContain(git.Runner.Commands, c => c.StartsWith("fetch", StringComparison.Ordinal) || c.StartsWith("pull", StringComparison.Ordinal));
        Assert.Equal("nuevo ↓", sites.Rows.Single(r => r.Host == "hello.test").GitChip);
        Choose(main, "hello.test");
        await UntilAsync(() => sites.GitChipText == "hay novedades");
        Assert.True(sites.GitUpdatePrimary);
        Snapshot(window, "sitios-git-novedades");

        // Con el commit ya traído, se cuentan: «1 por detrás».
        git.Runner.On("cat-file -e").On("rev-list --count", stdout: "1\n");
        await sites.CheckGitUpdatesAsync(force: true);
        Assert.Equal("1 ↓", sites.Rows.Single(r => r.Host == "hello.test").GitChip);
        await UntilAsync(() => sites.GitChipText == "1 commit por detrás");

        // Una sola a la vez: la segunda, mientras va la primera, no hace nada.
        var hold = new TaskCompletionSource();
        git.Runner.On("ls-remote --symref", stdout: GitFixture.LsRemote(), hold: hold);
        // Solo los ls-remote: el panel del sitio elegido puede refrescar su estado (status, log) a la vez.
        int LsRemotes() => git.Runner.Commands.Count(c => c.StartsWith("ls-remote", StringComparison.Ordinal));
        var before = LsRemotes();
        var first = sites.CheckGitUpdatesAsync(force: true);
        // Hasta que el ls-remote retenido esté lanzado (va en un hilo aparte: «en marcha» se pone un instante antes).
        await UntilAsync(() => sites.GitChecking && LsRemotes() == before + 1);
        var calls = LsRemotes();
        Assert.Equal(0, await sites.CheckGitUpdatesAsync(force: true));
        Assert.Equal(calls, LsRemotes());
        hold.SetResult();
        await first;
        Assert.EndsWith(": 1 sitio, ninguno con novedades.", sites.GitChecksText);

        // Sin red: el chip lo dice, sin diálogos.
        git.Runner.On("ls-remote --symref", exitCode: 128, stderr: "fatal: unable to access 'https://github.com/octocat/Hello-World.git/': Could not resolve host: github.com\n");
        await sites.CheckGitUpdatesAsync(force: true);
        Assert.Null(main.Dialog);
        await UntilAsync(() => sites.GitChipText == "no se pudo comprobar");
        Assert.Equal($"No se encuentra github.com. Comprueba el nombre y tu conexión. Si usas un proxy, Roost toma {(OperatingSystem.IsWindows() ? "el de Windows" : "el del sistema")}.", sites.GitNote);
    }

    // ---- 15: quitar un sitio con su clave y su token ----

    [AvaloniaFact]
    public async Task Quitar_ConClaveYToken_OfreceQuitarlos()
    {
        var (main, window, fake, git) = await OpenAsync(running: false);
        using var _ = fake;
        git.Runner.On("-q -t ed25519", onRun: GitFixture.FakeKeygen)
            .On("status --porcelain=v2", stdout: GitFixture.Status())
            .On("log -1", stdout: GitFixture.Log(GitFixture.Head, "x"))
            .On("branch -r", stdout: GitFixture.RemoteBranches);
        await git.Context.Access.Keys.GenerateAsync("tienda-git", CancellationToken.None);
        GitFixture.Register(fake, "tienda-git", GitFixture.HelloGit(SiteGit.AccessKey, "tienda-git", remote: "git@github.com:acme/tienda.git"));
        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda-git.test");

        var removing = main.Sites.RemoveCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<RemoveSiteDialogViewModel>(main.Dialog);
        Assert.Equal("Quitar tienda-git.test", dialog.Title);
        Assert.EndsWith($"La carpeta del proyecto ({Path.Combine(fake.Paths.Www, "tienda-git")}) no se toca.", dialog.Text);
        Assert.True(dialog.HasAccess);
        Assert.True(dialog.RemoveAccess);
        Assert.Equal(["Su clave SSH de Roost (tienda-git): quita también la pública de las deploy keys (https://github.com/acme/tienda/settings/keys)."], dialog.Removing);
        Snapshot(window, "sitios-quitar-con-acceso");
        dialog.CancelCommand.Execute(null);
        await removing;
        Assert.NotNull(new SiteRegistry(fake.Paths).Find("tienda-git"));   // cancelado: nada cambia
        Assert.True(git.Context.Access.Keys.Exists("tienda-git"));

        // La casilla dice lo que devuelve.
        var check = new RemoveSiteDialogViewModel("x.test", "", ["algo"], []);
        check.RemoveAccess = false;
        check.ConfirmCommand.Execute(null);
        Assert.Equal(false, await check.Closed);
        var none = new RemoveSiteDialogViewModel("x.test", "", [], []);
        none.ConfirmCommand.Execute(null);
        Assert.Equal(false, await none.Closed);

        // Un sitio sin git: el diálogo de siempre, sin casilla.
        Choose(main, "blog.test");
        removing = main.Sites.RemoveCommand.ExecuteAsync(null);
        Assert.False(Assert.IsType<RemoveSiteDialogViewModel>(main.Dialog).HasAccess);
        ((RemoveSiteDialogViewModel)main.Dialog!).CancelCommand.Execute(null);
        await removing;
    }

    // ---- Los textos del diseño (§2.4), con tildes ----

    [Fact]
    public void Textos_SeEligenPorElTipoDeFallo_ConTildes()
    {
        var url = GitUrl.Parse("https://github.com/acme/tienda");
        var ssh = GitUrl.Parse("git@git.acme.es:web/tienda.git");
        Assert.Equal($"No se encuentra github.com. Comprueba el nombre y tu conexión. Si usas un proxy, Roost toma {(OperatingSystem.IsWindows() ? "el de Windows" : "el del sistema")}.",
            GitTexts.Explain(new GitFailure(GitFailureKind.HostUnreachable, "", ""), url));
        Assert.Equal("acme/tienda pide acceso, o no existe: GitHub no distingue entre las dos cosas.",
            GitTexts.Explain(new GitFailure(GitFailureKind.AuthRequired, "", ""), url));
        Assert.Equal("El token existe pero la organización acme exige autorizarlo (SSO) o no tiene el permiso de contenido (Contents: Read). Ábrelo en GitHub y autorízalo.",
            GitTexts.Explain(new GitFailure(GitFailureKind.TokenForbidden, "", ""), url));
        Assert.Equal("GitHub ya no acepta la contraseña de la cuenta: hace falta un token. Créalo con el enlace de arriba.",
            GitTexts.Explain(new GitFailure(GitFailureKind.PasswordNotAccepted, "", ""), url));
        Assert.Equal("git.acme.es acepta la clave de tienda, pero con ella no se ve web/tienda: seguramente está en las deploy keys de otro repositorio. Ponla en las de web/tienda.",
            GitTexts.Explain(new GitFailure(GitFailureKind.KeyRejected, "", "ERROR: Repository not found."), ssh, "tienda"));
        Assert.Equal($"git.acme.es no acepta ninguna de tus claves SSH (las de ~/.ssh y {OsTexts.SshAgentKeys}). Pruébalo en tu terminal: ssh -T git@git.acme.es",
            GitTexts.Explain(new GitFailure(GitFailureKind.KeyRejected, "", "Permission denied (publickey)."), ssh, ownKey: true));
        Assert.Equal("Hay cambios sin guardar en 2 ficheros de tienda.test (app/Http/Kernel.php, .env). Roost no actualiza ni cambia de rama por encima de tu trabajo. Guárdalos (commit o stash) y vuelve a intentarlo.",
            GitTexts.LocalChanges("tienda.test", ["app/Http/Kernel.php", ".env"]));
        Assert.Equal(@"C:\roost\www\tienda ya existe y no está vacía. Elige otra carpeta o, si ya es ese proyecto, crea el sitio con la plantilla Vacía: Roost detectará su .git.",
            GitTexts.FolderBusy(@"C:\roost\www\tienda"));
        Assert.Equal($"No se pudo preparar git: el SHA-256 de {GitCatalog.Windows.FileName} no coincide con el esperado. No se instala nada. Prueba de nuevo o indica otro git en Ajustes › Git.",
            GitTexts.GitSetupFailed($"No se pudo preparar git: El SHA-256 de {GitCatalog.Windows.FileName} no coincide con el del catalogo (esperado a, obtenido b). No se instala nada. Prueba de nuevo o indica otro git en roost.toml ([git] exe)."));
        Assert.StartsWith("No se entiende esa dirección.", GitTexts.InvalidUrl(GitUrl.InvalidMessage));

        // La huella cambiada, con las dos huellas que calcula Core.
        var core = GitFailure.HostKeyChanged(ssh, "The fingerprint for the ED25519 key sent by the remote host is\nSHA256:nueva.\n",
            new GitAccess { HostKeys = [new KnownHostEntry(["git.acme.es"], new SshHostKey("ssh-ed25519", GitFixture.OtherEd25519), null)] });
        Assert.StartsWith($"La huella de git.acme.es ha cambiado respecto a la que Roost tenía guardada ({KnownHosts.Fingerprint(GitFixture.OtherEd25519)} → SHA256:nueva).", GitTexts.Explain(core, ssh));
        // De un servidor de serie, además, que lo trae Roost.
        var github = GitUrl.Parse("git@github.com:acme/tienda.git");
        Assert.EndsWith("Para github.com, Roost trae la que publica GitHub: si la ha cambiado, actualiza Roost.", GitTexts.Explain(new GitFailure(GitFailureKind.HostKeyChanged, "", ""), github));

        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).ToLocalTime();
        Assert.Equal($"hoy {now:HH:mm}", GitTexts.When(now, now));
        Assert.StartsWith("ayer ", GitTexts.When(now.AddDays(-1), now));
        Assert.Equal($"12 oct {now.AddDays(-12):HH:mm}", GitTexts.When(now.AddDays(-12), now));
        Assert.Equal("12 oct 2025", GitTexts.When(now.AddDays(-12).AddYears(-1), now));
        Assert.Equal("main y 3 ramas más", GitTexts.BranchesSummary("main", ["a", "b", "main", "c"]));
    }
}
