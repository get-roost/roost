using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Commands.Git;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>Lo comun de los comandos de sitios con git: el SiteManager con el git de Roost y como contar el estado.</summary>
internal static class SiteGitSupport
{
    /// <summary>Un SiteManager que sabe de git (instala MinGit la primera vez) y del acceso de cada sitio (claves, huellas y tokens).</summary>
    public static async Task<SiteManager> ManagerAsync(RoostPaths paths, CancellationToken cancellation)
    {
        var git = await GitSetup.EnsureAsync(paths, cancellation);
        return new SiteManager(paths, new GitClient(git.Executable, CommandRunner.Default, new GitEnvironment(paths)))
        {
            Access = GitAccessSupport.Factory(paths),
        };
    }

    /// <summary>El chip del panel: al dia, N por detras, cambios locales...</summary>
    public static (string Text, string Color) State(RepoStatus status, bool fetched)
    {
        if (!status.IsClean) return ($"{status.Changed.Count} cambio(s) local(es)", "yellow");
        if (status.IsDetached) return ("HEAD suelto (sin rama)", "yellow");
        if (status.Behind > 0 && status.Ahead > 0) return ($"{status.Ahead} por delante y {status.Behind} por detras (divergente)", "red");
        if (status.Behind > 0) return ($"{status.Behind} commit(s) por detras", "yellow");
        if (status.Ahead > 0) return ($"{status.Ahead} commit(s) por delante del remoto", "yellow");
        if (!status.HasUpstream) return ("sin rama remota", "yellow");
        return (fetched ? "al dia" : "al dia (segun lo ultimo que se trajo)", "green");
    }

    public static string Describe(GitCommit? commit) =>
        commit is null ? "(sin commits)" : $"{commit.ShortSha} \"{commit.Subject}\" {commit.When.ToLocalTime():yyyy-MM-dd HH:mm} - {commit.Author}";

    public static object Json(GitCommit? commit) =>
        commit is null ? null! : new { sha = commit.Sha, shortSha = commit.ShortSha, author = commit.Author, when = commit.When, subject = commit.Subject };

    public static bool Interactive => AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;
}

/// <summary>
/// <c>roost site clone</c>: un sitio nuevo desde un repositorio git, publico o privado. El acceso se
/// elige con --token, --ssh, --ssh-key o --own-ssh; sin ninguno, si el remoto lo pide y hay terminal,
/// se ofrece; con --json o sin terminal nunca se pregunta: falla con el texto que toca.
/// </summary>
public sealed class SiteCloneCommand : AsyncCommand<SiteCloneCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<URL>")]
        [Description("La direccion del repositorio: https://github.com/acme/tienda, git@servidor:grupo/repo.git o una carpeta local.")]
        public string Url { get; init; } = "";

        [CommandOption("--name <NOMBRE>")]
        [Description("Nombre del sitio (<nombre>.test). Por defecto, el del repositorio.")]
        public string? Name { get; init; }

        [CommandOption("--branch <RAMA>")]
        [Description("Rama que se clona. Por defecto, la rama por defecto del remoto.")]
        public string? Branch { get; init; }

        [CommandOption("--path <CARPETA>")]
        [Description("Carpeta del proyecto. Por defecto, <www>/<nombre>. Tiene que no existir o estar vacia.")]
        public string? Path { get; init; }

        [CommandOption("--php <VERSION>")]
        [Description("PHP del sitio (rama, version o instalacion). Por defecto, el global.")]
        public string? Php { get; init; }

        [CommandOption("--token")]
        [Description("Entra con un token HTTPS: usa el guardado para el repositorio o lo pide (sin eco, o una linea por stdin). Se guarda en el Administrador de credenciales de Windows.")]
        public bool Token { get; init; }

        [CommandOption("--user <USUARIO>")]
        [Description("Con --token: el usuario que va con el token, si el servidor lo pide (GitHub, GitLab y Bitbucket no lo usan).")]
        public string? User { get; init; }

        [CommandOption("--ssh")]
        [Description("Entra con la clave SSH de Roost para este sitio: la crea si no la tiene y la ensena para ponerla como deploy key.")]
        public bool Ssh { get; init; }

        [CommandOption("--ssh-key <FICHERO>")]
        [Description("Entra con una clave privada tuya, sin contrasena: Roost guarda una copia solo para tu usuario (la tuya no se toca).")]
        public string? SshKey { get; init; }

        [CommandOption("--own-ssh")]
        [Description("Entra con tus claves (~/.ssh y el ssh-agent de Windows), como en tu terminal. Roost no guarda nada.")]
        public bool OwnSsh { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No pide confirmacion (solo acepta las huellas SSH de serie: GitHub, GitLab y Bitbucket).")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI (nunca pregunta: sin confirmacion ni progreso).")]
        public bool Json { get; init; }

        public override ValidationResult Validate()
        {
            var chosen = new[] { Token, Ssh, SshKey is not null, OwnSsh }.Count(b => b);
            if (chosen > 1) return ValidationResult.Error("Elige una sola forma de entrar: --token, --ssh, --ssh-key o --own-ssh.");
            if (User is not null && !Token) return ValidationResult.Error("--user va con --token.");
            return ValidationResult.Success();
        }
    }

    /// <summary>Lo que hay que deshacer si el clonado no acaba: un token que se acaba de guardar, una clave que se acaba de copiar.</summary>
    private sealed class Undo
    {
        private readonly List<(string What, Action Action)> _steps = [];

        public void Add(string what, Action action) => _steps.Add((what, action));

        public void Clear() => _steps.Clear();

        public void Run()
        {
            foreach (var (_, action) in _steps)
            {
                try { action(); } catch (RoostException) { /* lo que no se pueda deshacer se queda; ya se dijo el fallo */ }
            }
            _steps.Clear();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site clone")) return 1;

        var paths = RoostPaths.Default();
        var undo = new Undo();
        SiteManager? sites = null;
        GitUrl? url = null;
        string? siteName = null;
        try
        {
            url = AdaptUrl(GitUrl.Parse(settings.Url), settings);
            sites = await SiteGitSupport.ManagerAsync(paths, cancellation);
            var factory = sites.Access!;
            var request = new CloneRequest(url, settings.Name, settings.Branch, settings.Path, settings.Php);
            // Lo que no cuadra (nombre cogido, carpeta ocupada, PHP sin instalar) se dice antes de ir a la red.
            var plan = sites.PlanClone(request);
            siteName = plan.Name;
            var ask = !settings.Json && GitAccessSupport.Interactive;

            var access = await PrepareAccessAsync(settings, factory, url, plan.Name, undo, ask, cancellation);
            RemoteProbe probe;
            while (true)
            {
                if (url.Scheme == GitScheme.Ssh && access is not null && url.Host is { } host)
                {
                    var why = await GitAccessSupport.EnsureTrustedAsync(factory.KnownHosts, host, url.Port, settings.Yes || settings.Json, null, quiet: settings.Json, cancellation);
                    if (why is not null) throw new GitException(new GitFailure(GitFailureKind.HostKeyUnverified, why, ""));
                }

                if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"Comprobando {url.Normalized}{(access is null ? "" : $" [grey]({AccessLabel(access, plan.Name)})[/]")}...");
                probe = await ProbeAsync(sites, url, access, cancellation);
                if (probe.Reachable) break;

                var failure = probe.Failure!;
                // Sin acceso elegido y con terminal: el remoto pide acceso, se ofrece como entrar.
                if (access is null && ask && failure.Kind is GitFailureKind.AuthRequired or GitFailureKind.KeyRequired)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]{failure.Message}[/]");
                    var choice = ChooseAccess(url);
                    if (choice is null) return Cancelled(sites);
                    url = choice.Value.Url;
                    access = await PrepareAccessAsync(choice.Value.Settings, factory, url, plan.Name, undo, ask, cancellation);
                    continue;
                }
                // Con la clave de Roost recien puesta: el usuario puede estar pegandola todavia.
                if (access?.Kind == SiteGit.AccessKey && settings.SshKey is null && ask && failure.Kind == GitFailureKind.KeyRejected)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]{failure.Message}[/]");
                    if (!AnsiConsole.Confirm("Volver a probar?", defaultValue: true)) return Fail(failure, settings, factory, url, plan.Name, undo);
                    continue;
                }
                return Fail(failure, settings, factory, url, plan.Name, undo);
            }

            request = request with { Url = url, Probe = probe, Access = access };
            var branch = plan.Branch ?? probe.DefaultBranch;
            if (!settings.Json)
            {
                var visibility = url.IsLocal ? "" : access is null ? " - publico" : $" - {AccessLabel(access, plan.Name)}";
                var what = url.IsLocal ? $"Repositorio local {url.Label}" : $"{url.ProviderName} - {url.Label}{visibility}";
                AnsiConsole.MarkupLineInterpolated($"[green]{what}[/] [grey](rama por defecto: {probe.DefaultBranch ?? "ninguna"}; {probe.Branches.Count} rama(s))[/]");
                AnsiConsole.MarkupLineInterpolated($"Se clonara [bold]{branch ?? "?"}[/] de {url.Label} en {plan.Folder} y se servira en [bold]https://{plan.Name}.test/[/] [grey](PHP {settings.Php ?? "global"})[/]");
                if (!settings.Yes && ask && !AnsiConsole.Confirm("Seguir?"))
                    return Cancelled(sites, undo);
            }

            CloneResult? result = null;
            if (settings.Json)
            {
                result = await sites.CloneAsync(request, null, null, cancellation);
            }
            else
            {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask($"Clonando {Markup.Escape(url.Label)}", maxValue: 100);
                        var progress = new Progress<CloneProgress>(p =>
                        {
                            task.Value = Math.Max(task.Value, p.Overall);
                            task.Description = $"{p.Stage} {p.Percent}%" + (p.Detail is null ? "" : $" [grey]{Markup.Escape(p.Detail)}[/]");
                        });
                        result = await sites.CloneAsync(request, progress, null, cancellation);
                        task.Value = 100;
                        task.Description = $"Clonado {Markup.Escape(url.Label)}";
                    });
            }
            undo.Clear();   // ya es del sitio

            var site = result!.Change.Site;
            var config = RoostConfig.Load(paths.ConfigFile);
            if (settings.Json)
            {
                // Sin texto por medio: el hosts se sincroniza en silencio y el stack se aplica en el proximo up.
                var hostsChanged = sites.SyncHosts(SiteSupport.HelperExe);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    site = site.Name,
                    host = site.Host,
                    url = StackPlanner.SiteUrl(site, config.HttpsPort),
                    folder = site.Folder,
                    documentRoot = site.DocumentRoot,
                    php = site.Php ?? "global",
                    remote = site.Git!.Remote,
                    provider = site.Git.Provider.ToString().ToLowerInvariant(),
                    access = site.Git.Access,
                    keyName = site.Git.KeyName,
                    credential = site.Git.CredentialTarget is { } t ? factory.Credentials.Describe(t) : null,
                    branch = result.Branch,
                    head = SiteGitSupport.Json(result.Head),
                    seconds = Math.Round(result.Elapsed.TotalSeconds, 1),
                    hostsChanged,
                    stackApplied = false,
                    log = sites.Log.FilePath(site.Name),
                }, JsonOutput.Options));
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[green]Sitio registrado:[/] [bold]{site.Host}[/] -> {site.DocumentRoot} [grey](PHP {site.Php ?? "global"}; {result.Elapsed.TotalSeconds:0.0} s)[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]{result.Branch}: {SiteGitSupport.Describe(result.Head)}[/]");
            if (site.Root is not null) AnsiConsole.MarkupLineInterpolated($"[grey]Raiz web detectada: {site.Root}/[/]");
            if (File.Exists(Path.Combine(site.Folder, "composer.json")))
                AnsiConsole.MarkupLine("[grey]Hay un composer.json: recuerda composer install (los pasos tras clonar llegaran en una version proxima).[/]");

            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            SiteSupport.TrustHint(new LocalCa(paths));
            return await StackApply.IfRunningAsync(paths,
                _ => $"{StackPlanner.SiteUrl(site, config.HttpsPort)} y {SiteSupport.HttpUrl(site, config.HttpPort)}", cancellation);
        }
        catch (RoostException ex)
        {
            undo.Run();
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message, kind = (ex as GitException)?.Failure.Kind.ToString(), detail = (ex as GitException)?.Failure.Detail }, JsonOutput.Options));
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            if (ex is GitException { Failure.Detail.Length: > 0 } g) AnsiConsole.MarkupLineInterpolated($"[grey]git: {g.Failure.Detail}[/]");
            if (settings.Ssh) KeptKeyHint(sites, siteName);
            return 1;
        }
        catch (OperationCanceledException)
        {
            undo.Run();
            AnsiConsole.MarkupLine("[yellow]Cancelado.[/] No queda nada a medias: el clonado se hace en una carpeta temporal.");
            return 130;
        }
    }

    /// <summary>Un token solo va por https y una clave solo por SSH: si la direccion es la otra y el proveedor es conocido, se cambia.</summary>
    private static GitUrl AdaptUrl(GitUrl url, Settings settings)
    {
        if (url.IsLocal) return url;
        if (settings.Token && url.Scheme == GitScheme.Ssh)
            return url.ToHttps() ?? throw new GitException(GitFailure.TokenNeedsHttps(url));
        if ((settings.Ssh || settings.SshKey is not null || settings.OwnSsh) && url.Scheme == GitScheme.Https)
            return url.ToSsh() ?? throw new GitException(GitFailure.KeyNeedsSsh(url));
        return url;
    }

    private static Task<RemoteProbe> ProbeAsync(SiteManager sites, GitUrl url, SiteAccess? access, CancellationToken cancellation)
    {
        try
        {
            return sites.ProbeAsync(url, access, cancellation);
        }
        catch (GitException ex)
        {
            // SSH sin acceso elegido: no se lanza nada, se cuenta como lo que es.
            return Task.FromResult(new RemoteProbe(false, true, null, [], ex.Failure));
        }
    }

    /// <summary>
    /// El acceso que piden las opciones, ya preparado: el token guardado o pedido (y guardado), la
    /// clave de Roost creada y ensenada, la clave del usuario copiada. null si no se pidio ninguno.
    /// </summary>
    private static async Task<SiteAccess?> PrepareAccessAsync(Settings settings, GitAccessFactory factory, GitUrl url, string name, Undo undo, bool ask, CancellationToken cancellation)
    {
        if (url.IsLocal) return null;

        if (settings.Token)
        {
            var location = CredentialLocation.For(url)!;
            if (factory.Credentials.Find(location) is { } saved)
            {
                if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"[grey]Uso el token guardado para {factory.Credentials.Describe(saved.Target)}.[/]");
                return SiteAccess.Token;
            }
            if (!Console.IsInputRedirected && !ask)
                throw new GitException(new GitFailure(GitFailureKind.AuthRequired,
                    $"{url.Label} necesita un token y no hay ninguno guardado. Guardalo antes con roost git token set {location.Key[(location.Protocol.Length + 3)..]} (o pasalo por stdin).", ""));
            if (!settings.Json && !Console.IsInputRedirected)
            {
                if (url.NewTokenUrl is { } create)
                    AnsiConsole.MarkupLineInterpolated($"Crea un token en [link]{create}[/] con el permiso justo: {url.TokenPermission}.");
                if (url.Provider is GitProvider.GitHub or GitProvider.GitLab or GitProvider.Bitbucket)
                    AnsiConsole.MarkupLineInterpolated($"[grey]Tu contrasena de {url.ProviderName} no sirve: solo acepta tokens.[/]");
            }
            var token = GitAccessSupport.ReadToken($"Token de {url.Label}") ?? throw new RoostException("Sin token: pegalo cuando lo pida o pasalo por stdin (una linea).");
            if (GitCredentials.LooksLikePassword(location.Host.Split(':')[0], token) is { } warning && !settings.Json)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {warning}");
            var target = factory.Credentials.Save(location, token, settings.User);
            undo.Add("token", () => factory.Credentials.Remove(target));
            if (!settings.Json)
                AnsiConsole.MarkupLineInterpolated($"[grey]Guardado en el Administrador de credenciales de Windows, cifrado para tu usuario; solo vale para {location.Key}.[/]");
            return SiteAccess.Token;
        }

        if (settings.Ssh)
        {
            var key = factory.Keys.Find(name);
            if (key is null)
            {
                key = await factory.Keys.GenerateAsync(name, cancellation);
                if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"[green]Clave SSH creada para {name}[/] [grey](ed25519, sin contrasena: una deploy key de solo lectura, una por repositorio)[/]");
            }
            else if (!settings.Json)
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]Uso la clave SSH de Roost que ya tiene {name}.[/]");
            }
            if (!settings.Json)
            {
                GitAccessSupport.ShowPublicKey(key, url);
                if (ask)
                    AnsiConsole.Prompt(new TextPrompt<string>($"Pulsa Intro cuando la hayas puesto como deploy key de {url.Label}").AllowEmpty());
            }
            return SiteAccess.Key(name);
        }

        if (settings.SshKey is { } file)
        {
            var imported = await factory.Keys.ImportAsync(name, file, cancellation);
            undo.Add("clave", () => factory.Keys.Remove(name));
            if (!settings.Json)
                AnsiConsole.MarkupLineInterpolated($"[grey]Uso la clave {Path.GetFullPath(file)} ({imported.Algorithm} {imported.Fingerprint}): copia solo para tu usuario en {imported.PrivateFile}.[/]");
            return SiteAccess.Key(name);
        }

        if (settings.OwnSsh) return SiteAccess.Own;
        return null;
    }

    /// <summary>El menu cuando el remoto pide acceso y no se dijo como entrar (solo con terminal).</summary>
    private static (GitUrl Url, Settings Settings)? ChooseAccess(GitUrl url)
    {
        var https = url.ToHttps();
        var ssh = url.ToSsh();
        var options = new List<(string Label, Func<(GitUrl, Settings)>? Pick)>();
        if (https is not null) options.Add(("Token HTTPS", () => (https, new Settings { Token = true })));
        if (ssh is not null)
        {
            options.Add(("Clave SSH de Roost para este sitio (la creo y la pegas como deploy key)", () => (ssh, new Settings { Ssh = true })));
            options.Add(("Un fichero de clave privada (sin contrasena)", () =>
            {
                var file = AnsiConsole.Prompt(new TextPrompt<string>("Ruta de la clave privada:"));
                return (ssh, new Settings { SshKey = file.Trim().Trim('"') });
            }));
            options.Add(("Mis claves SSH (~/.ssh y el ssh-agent de Windows)", () => (ssh, new Settings { OwnSsh = true })));
        }
        options.Add(("Cancelar", null));

        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Como entro?").AddChoices(options.Select(o => o.Label)));
        return options.First(o => o.Label == picked).Pick?.Invoke();
    }

    private static int Fail(GitFailure failure, Settings settings, GitAccessFactory factory, GitUrl url, string name, Undo undo)
    {
        undo.Run();
        var key = failure.Kind == GitFailureKind.KeyRejected && settings.SshKey is null ? factory.Keys.Find(name) : null;
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = failure.Message,
                kind = failure.Kind.ToString(),
                detail = failure.Detail,
                publicKey = key?.PublicKey,
                deployKeysUrl = key is null ? null : url.DeployKeysUrl,
                newTokenUrl = failure.Kind is GitFailureKind.AuthRequired or GitFailureKind.TokenRejected or GitFailureKind.PasswordNotAccepted ? url.NewTokenUrl : null,
            }, JsonOutput.Options));
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[red]{failure.Message}[/]");
        if (failure.Detail.Length > 0) AnsiConsole.MarkupLineInterpolated($"[grey]git: {failure.Detail}[/]");
        if (failure.Kind is GitFailureKind.AuthRequired or GitFailureKind.KeyRequired && settings is { Token: false, Ssh: false, SshKey: null, OwnSsh: false })
            AnsiConsole.MarkupLine("[grey]Elige como entrar: --token (HTTPS), --ssh (una clave de Roost para este sitio), --ssh-key <fichero> o --own-ssh (las tuyas).[/]");
        if (key is not null)
            AnsiConsole.MarkupLineInterpolated($"[grey]La clave de {name} se queda en {key.PrivateFile} para la proxima vez (roost git key show {name}; para quitarla, roost git key remove {name}).[/]");
        return 1;
    }

    private static int Cancelled(SiteManager sites, Undo? undo = null)
    {
        undo?.Run();
        AnsiConsole.MarkupLine("[grey]Cancelado. No se ha clonado nada.[/]");
        return 1;
    }

    private static void KeptKeyHint(SiteManager? sites, string? name)
    {
        if (sites?.Access is not { } factory || name is null || factory.Keys.Find(name) is not { } key) return;
        AnsiConsole.MarkupLineInterpolated($"[grey]La clave SSH de {name} se queda en {key.PrivateFile} (roost git key remove {name} para quitarla).[/]");
    }

    private static string AccessLabel(SiteAccess access, string name) => access.Kind switch
    {
        SiteGit.AccessToken => "token HTTPS",
        SiteGit.AccessKey => $"clave SSH de Roost de {access.KeyName ?? name}",
        SiteGit.AccessOwn => "tus claves SSH",
        _ => "sin credenciales",
    };
}

/// <summary><c>roost site pull</c>: trae lo nuevo del remoto, solo fast-forward y solo con el arbol limpio.</summary>
public sealed class SitePullCommand : AsyncCommand<SitePullCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site pull")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            var sites = await SiteGitSupport.ManagerAsync(paths, cancellation);
            var site = sites.Find(settings.Name) ?? throw new RoostException($"No hay ningun sitio '{settings.Name}'. Mira los registrados con: roost site list");
            if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"Actualizando [bold]{site.Host}[/] desde {site.Git?.Remote ?? "?"}...");

            PullResult? result = null;
            if (settings.Json)
                result = await sites.PullAsync(site.Name, null, cancellation);
            else
                await AnsiConsole.Status().StartAsync("Actualizando...", async ctx =>
                {
                    var lines = new Progress<string>(line => { if (GitOutput.ParseProgress(line) is { } p) ctx.Status($"{p.Stage} {p.Percent}%"); });
                    result = await sites.PullAsync(site.Name, lines, cancellation);
                });

            var head = sites.Find(site.Name)?.Git?.LastCommit;
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, site = site.Name, changed = result!.Changed, from = result.From, to = result.To, changedFiles = result.ChangedFiles, head = SiteGitSupport.Json(head) }, JsonOutput.Options));
                return 0;
            }

            if (result!.Changed)
                AnsiConsole.MarkupLineInterpolated($"[green]Actualizado:[/] {Short(result.From)} -> {Short(result.To)} [grey]({result.ChangedFiles} fichero(s) cambiado(s))[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[green]Ya estaba al dia[/] [grey]({head?.ShortSha ?? "?"})[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]{SiteGitSupport.Describe(head)}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return Fail(ex, settings.Json);
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    internal static int Fail(RoostException ex, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message, kind = (ex as GitException)?.Failure.Kind.ToString() }, JsonOutput.Options));
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
        if (ex is GitException { Failure.Detail.Length: > 0 } g) AnsiConsole.MarkupLineInterpolated($"[grey]git: {g.Failure.Detail}[/]");
        return 1;
    }
}

/// <summary><c>roost site branch</c>: sin rama, lista las del remoto; con rama, cambia a ella (solo con el arbol limpio).</summary>
public sealed class SiteBranchCommand : AsyncCommand<SiteBranchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "[RAMA]")]
        [Description("La rama a la que cambiar. Sin ella, lista las ramas del remoto.")]
        public string? Branch { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site branch")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            var sites = await SiteGitSupport.ManagerAsync(paths, cancellation);
            var site = sites.Find(settings.Name) ?? throw new RoostException($"No hay ningun sitio '{settings.Name}'. Mira los registrados con: roost site list");

            if (settings.Branch is null)
            {
                if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"Consultando las ramas de {site.Git?.Remote ?? site.Host}...");
                var branches = await sites.RemoteBranchesAsync(site.Name, cancellation);
                var current = (await sites.GitStatusAsync(site.Name, fetch: false, cancellation)).Status.Branch;
                if (settings.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { site = site.Name, current, branches }, JsonOutput.Options));
                    return 0;
                }
                foreach (var branch in branches)
                {
                    if (branch == current) AnsiConsole.MarkupLineInterpolated($"[green]* {branch}[/] [grey](actual)[/]");
                    else AnsiConsole.MarkupLineInterpolated($"  {branch}");
                }
                if (branches.Count == 0) AnsiConsole.MarkupLine("[yellow]El remoto no tiene ramas.[/]");
                AnsiConsole.MarkupLineInterpolated($"[grey]Para cambiar: roost site branch {site.Name} <rama>[/]");
                return 0;
            }

            if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"Cambiando [bold]{site.Host}[/] a {settings.Branch}...");
            var changed = await sites.CheckoutAsync(site.Name, settings.Branch, null, cancellation);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, site = site.Name, branch = changed.Git!.Branch, head = SiteGitSupport.Json(changed.Git.LastCommit) }, JsonOutput.Options));
                return 0;
            }
            AnsiConsole.MarkupLineInterpolated($"[green]{site.Host} esta ahora en {changed.Git!.Branch}[/] [grey]{SiteGitSupport.Describe(changed.Git.LastCommit)}[/]");
            if (File.Exists(Path.Combine(site.Folder, "composer.json")))
                AnsiConsole.MarkupLine("[grey]Las dependencias y las migraciones pueden cambiar de una rama a otra: revisa composer install y las migraciones.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return SitePullCommand.Fail(ex, settings.Json);
        }
    }
}

/// <summary><c>roost site git</c>: el estado del repositorio de un sitio (rama, por delante o por detras, cambios, ultimo commit, historial).</summary>
public sealed class SiteGitCommand : AsyncCommand<SiteGitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandOption("--check")]
        [Description("Pregunta antes al remoto si hay commits nuevos (fetch; no cambia nada en la carpeta).")]
        public bool Check { get; init; }

        [CommandOption("--adopt")]
        [Description("Adopta el .git que ya tiene la carpeta del sitio (lee su remoto y su rama).")]
        public bool Adopt { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site git")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            var sites = await SiteGitSupport.ManagerAsync(paths, cancellation);
            if (settings.Adopt)
            {
                var adopted = await sites.AdoptGitAsync(settings.Name, cancellation);
                if (!settings.Json)
                    AnsiConsole.MarkupLineInterpolated($"[green]Adoptado:[/] {adopted.Host} sigue {adopted.Git!.Remote} ({adopted.Git.Branch}) [grey](acceso: el tuyo; Roost no guarda credenciales)[/]");
            }

            var status = await sites.GitStatusAsync(settings.Name, settings.Check, cancellation);
            var (state, color) = SiteGitSupport.State(status.Status, status.Fetched);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    site = status.Site.Name,
                    folder = status.Site.Folder,
                    remote = status.Git.Remote,
                    provider = status.Git.Provider.ToString().ToLowerInvariant(),
                    access = status.Git.Access,
                    keyName = status.Git.KeyName,
                    credential = status.Git.CredentialTarget is { } target ? sites.Access?.Credentials.Describe(target) : null,
                    branch = status.Status.Branch,
                    upstream = status.Status.Upstream,
                    ahead = status.Status.Ahead,
                    behind = status.Status.Behind,
                    fetched = status.Fetched,
                    changed = status.Status.Changed,
                    untracked = status.Status.Untracked,
                    state,
                    head = SiteGitSupport.Json(status.Head),
                    lastPullUtc = status.Git.LastPullUtc,
                    history = status.History.Take(10).Select(h => new { when = h.When, kind = h.Kind, branch = h.Branch, from = h.From, to = h.To, ok = h.Ok, seconds = Math.Round(h.Seconds, 1), error = h.Error }),
                    log = sites.Log.FilePath(status.Site.Name),
                }, JsonOutput.Options));
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[bold]{status.Site.Host}[/] [grey]{status.Site.Folder}[/]");
            var provider = GitUrl.TryParse(status.Git.Remote, out var u, out _) ? u.ProviderName : status.Git.Provider.ToString();
            AnsiConsole.MarkupLineInterpolated($"Remoto     {status.Git.Remote} [grey]({provider}; acceso: {AccessLabel(status.Git)})[/]");
            AnsiConsole.MarkupLineInterpolated($"Rama       {status.Status.Branch ?? "(HEAD suelto)"}  [{color}]{state}[/]");
            AnsiConsole.MarkupLineInterpolated($"Commit     {SiteGitSupport.Describe(status.Head)}");
            AnsiConsole.MarkupLineInterpolated($"Actualizado {(status.Git.LastPullUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "nunca por Roost")}");
            if (!status.Status.IsClean)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Cambios locales:[/] {string.Join(", ", status.Status.Changed.Take(8))}{(status.Status.Changed.Count > 8 ? "..." : "")} [grey](Actualizar y cambiar de rama se desactivan hasta que los guardes o descartes)[/]");
            if (!status.Fetched) AnsiConsole.MarkupLine("[grey]Con --check pregunta al remoto si hay commits nuevos.[/]");

            if (status.History.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Historial[/]");
                foreach (var h in status.History.Take(5))
                {
                    var what = h.Kind switch { GitHistory.Clone => "Clonar", GitHistory.Pull => "Actualizar", GitHistory.Checkout => "Cambiar rama", GitHistory.Adopt => "Adoptar", _ => h.Kind };
                    var range = h.From is null ? h.To ?? "" : $"{h.From} -> {h.To ?? "?"}";
                    var mark = h.Ok ? "[green]ok[/]" : "[red]fallo[/]";
                    AnsiConsole.MarkupLineInterpolated($"  {h.When.ToLocalTime():yyyy-MM-dd HH:mm}  {what,-12} {range} ({h.Branch}) {h.Seconds:0.0} s ");
                    AnsiConsole.MarkupLine("    " + mark + (h.Error is null ? "" : " [grey]" + Markup.Escape(h.Error) + "[/]"));
                }
            }
            AnsiConsole.MarkupLineInterpolated($"[grey]Registro completo: {sites.Log.FilePath(status.Site.Name)}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return SitePullCommand.Fail(ex, settings.Json);
        }
    }

    private static string AccessLabel(SiteGit git) => git.Access switch
    {
        SiteGit.AccessNone => "publico, sin credenciales",
        SiteGit.AccessToken => "token HTTPS",
        SiteGit.AccessKey => $"clave SSH de Roost ({git.KeyName})",
        SiteGit.AccessOwn => "el tuyo (tus claves SSH)",
        _ => git.Access,
    };
}
