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
    /// <summary>Un SiteManager que sabe de git (instala MinGit la primera vez).</summary>
    public static async Task<SiteManager> ManagerAsync(RoostPaths paths, CancellationToken cancellation)
    {
        var git = await GitSetup.EnsureAsync(paths, cancellation);
        return new SiteManager(paths, new GitClient(git.Executable, CommandRunner.Default, new GitEnvironment(paths)));
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

/// <summary><c>roost site clone</c>: un sitio nuevo desde un repositorio git (de momento, publico o local).</summary>
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

        [CommandOption("-y|--yes")]
        [Description("No pide confirmacion.")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI (sin confirmacion ni progreso).")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site clone")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            var url = GitUrl.Parse(settings.Url);
            var sites = await SiteGitSupport.ManagerAsync(paths, cancellation);
            var request = new CloneRequest(url, settings.Name, settings.Branch, settings.Path, settings.Php);
            // Lo que no cuadra (nombre cogido, carpeta ocupada, PHP sin instalar) se dice antes de ir a la red.
            var plan = sites.PlanClone(request);

            if (!settings.Json) AnsiConsole.MarkupLineInterpolated($"Comprobando {url.Normalized}...");
            var probe = await sites.ProbeAsync(url, cancellation);
            if (!probe.Reachable)
            {
                if (settings.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = probe.Failure!.Message, kind = probe.Failure.Kind.ToString(), detail = probe.Failure.Detail }, JsonOutput.Options));
                    return 1;
                }
                AnsiConsole.MarkupLineInterpolated($"[red]{probe.Failure!.Message}[/]");
                if (probe.NeedsAuth)
                    AnsiConsole.MarkupLine("[grey]El acceso a repositorios privados (token HTTPS o clave SSH) llega en una version proxima de Roost. De momento, repositorios publicos y carpetas locales.[/]");
                if (probe.Failure.Detail.Length > 0) AnsiConsole.MarkupLineInterpolated($"[grey]git: {probe.Failure.Detail}[/]");
                return 1;
            }

            var branch = plan.Branch ?? probe.DefaultBranch;
            if (!settings.Json)
            {
                var what = url.IsLocal ? $"Repositorio local {url.Label}" : $"{url.ProviderName} - {url.Label} - publico";
                AnsiConsole.MarkupLineInterpolated($"[green]{what}[/] [grey](rama por defecto: {probe.DefaultBranch ?? "ninguna"}; {probe.Branches.Count} rama(s))[/]");
                AnsiConsole.MarkupLineInterpolated($"Se clonara [bold]{branch ?? "?"}[/] de {url.Label} en {plan.Folder} y se servira en [bold]https://{plan.Name}.test/[/] [grey](PHP {settings.Php ?? "global"})[/]");
                if (!settings.Yes && SiteGitSupport.Interactive && !AnsiConsole.Confirm("Seguir?"))
                {
                    AnsiConsole.MarkupLine("[grey]Cancelado. No se ha tocado nada.[/]");
                    return 1;
                }
            }

            CloneResult? result = null;
            if (settings.Json)
            {
                result = await sites.CloneAsync(request with { Probe = probe }, null, null, cancellation);
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
                        result = await sites.CloneAsync(request with { Probe = probe }, progress, null, cancellation);
                        task.Value = 100;
                        task.Description = $"Clonado {Markup.Escape(url.Label)}";
                    });
            }

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
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message, kind = (ex as GitException)?.Failure.Kind.ToString() }, JsonOutput.Options));
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            if (ex is GitException { Failure.Detail.Length: > 0 } g) AnsiConsole.MarkupLineInterpolated($"[grey]git: {g.Failure.Detail}[/]");
            return 1;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelado.[/] No queda nada a medias: el clonado se hace en una carpeta temporal.");
            return 130;
        }
    }
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
            AnsiConsole.MarkupLineInterpolated($"Remoto     {status.Git.Remote} [grey]({provider}; acceso: {AccessLabel(status.Git.Access)})[/]");
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

    private static string AccessLabel(string access) => access switch
    {
        SiteGit.AccessNone => "publico, sin credenciales",
        SiteGit.AccessToken => "token HTTPS",
        SiteGit.AccessKey => "clave SSH de Roost",
        SiteGit.AccessOwn => "el tuyo",
        _ => access,
    };
}
