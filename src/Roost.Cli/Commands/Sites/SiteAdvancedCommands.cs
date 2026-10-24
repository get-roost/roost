using System.ComponentModel;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>
/// Sitios avanzados (oleada 6): lo comun de renombrar, mover, alias, el .roost.toml y las carpetas
/// aparcadas. Lo usa tambien roost up (<see cref="PrintSync"/>).
/// </summary>
internal static class SiteSources
{
    /// <summary>
    /// Pone al dia el registro con el .roost.toml de cada sitio y las carpetas aparcadas, y lo
    /// cuenta. Con el hosts, dice que nombres faltan (no pide permiso: eso es roost site hosts).
    /// </summary>
    public static void PrintSync(RoostPaths paths)
    {
        var sites = new SiteManager(paths);
        var project = sites.SyncProjectFiles();
        foreach (var change in project.Changes)
            AnsiConsole.MarkupLineInterpolated($"[grey].roost.toml:[/] {change.Site}.test pasa a {change.What}");
        foreach (var warning in project.Warnings)
            AnsiConsole.MarkupLineInterpolated($"[yellow].roost.toml:[/] {warning}");
        var parked = sites.SyncParked();
        PrintPark(parked);
        if (parked.Added.Count > 0 || project.NamesChanged) WarnMissingHosts(paths);
    }

    public static void PrintPark(ParkSync sync)
    {
        foreach (var site in sync.Added)
            AnsiConsole.MarkupLineInterpolated($"[green]+[/] [bold]{site.Host}[/] [grey]{site.Folder}{(site.Php is { } php ? $" (PHP {php})" : "")}[/]");
        foreach (var site in sync.Removed)
            AnsiConsole.MarkupLineInterpolated($"[red]-[/] [bold]{site.Host}[/] [grey](su carpeta ya no esta)[/]");
        foreach (var skip in sync.Skipped)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Se salta[/] {skip.Folder}: {skip.Reason}");
    }

    /// <summary>Con el hosts, los nombres de los sitios que aun no estan en el (sin pedir permiso).</summary>
    public static void WarnMissingHosts(RoostPaths paths)
    {
        if (RoostConfig.Load(paths.ConfigFile).UsesLocalDns) return;
        var missing = SiteManager.HostNames(new SiteRegistry(paths).Load()).Except(new HostsUpdater().Current()).ToList();
        if (missing.Count > 0)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Faltan en el hosts:[/] {string.Join(", ", missing)}. [grey]Para anadirlos (un solo permiso): roost site hosts[/]");
    }

    public static int Fail(RoostException ex)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
        return 1;
    }
}

/// <summary>
/// <c>roost site rename</c>: cambia el nombre de un sitio (tienda.test -> shop.test): registro,
/// certificado, hosts, sus subdominios, su git y sus registros. La base de datos se queda como
/// estaba, salvo con <c>--db copy</c>, que la copia al nombre nuevo (la de antes no se borra).
/// </summary>
public sealed class SiteRenameCommand : AsyncCommand<SiteRenameCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VIEJO>")]
        [Description("Nombre del sitio ahora (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "<NUEVO>")]
        [Description("Nombre nuevo (minusculas, numeros y guiones).")]
        public string NewName { get; init; } = "";

        [CommandOption("--db <QUE>")]
        [Description("Su base de datos: keep (la deja como esta, el sitio la sigue usando) o copy (la copia al nombre nuevo y el sitio usa la copia; la de antes se queda). Sin terminal, keep.")]
        public string? Db { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site rename")) return 1;
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var site = sites.Find(settings.Name)
                ?? throw new RoostException($"No hay ningun sitio '{settings.Name}'. Mira los registrados con: roost site list");
            var newName = settings.NewName.Trim().ToLowerInvariant();
            if (newName.EndsWith(".test", StringComparison.Ordinal)) newName = newName[..^".test".Length];

            var oldDb = SiteManager.DatabaseOf(site);
            var newDb = DbAdmin.NameFor(newName);
            var copy = settings.Db?.Trim().ToLowerInvariant() switch
            {
                null => site.Database is null && oldDb != newDb && SiteGitSupport.Interactive
                    && AnsiConsole.Confirm($"Su base de datos es {oldDb}. Copiarla a {newDb} y que el sitio use la copia? (la de antes se queda)", defaultValue: false),
                "keep" => false,
                "copy" => true,
                _ => throw new RoostException($"--db tiene que ser keep o copy (vale '{settings.Db}')."),
            };

            // La copia primero: si falla, el sitio no se ha tocado.
            SiteDatabaseCopied? copied = null;
            if (copy)
            {
                if (oldDb == newDb) throw new RoostException($"La base de datos ya se llama {newDb}: no hay nada que copiar.");
                var (db, ini) = RunningDb(paths);
                AnsiConsole.MarkupLineInterpolated($"Copiando {oldDb} a {newDb} en {db.Label}...");
                copied = await SiteDatabaseCopy.CopyAsync(paths, db, ini, oldDb, newDb, CommandRunner.Default, ProcessRunner.Default, null, cancellation);
                AnsiConsole.MarkupLineInterpolated($"[green]Copiada[/] {oldDb} a {newDb} [grey]({copied.Bytes / 1024} KB volcados). {oldDb} se queda; borrala cuando lo compruebes: roost db drop {oldDb}[/]");
            }

            var renamed = sites.Rename(site.Name, newName);
            if (copied is not null)
            {
                sites.SetDatabase(renamed.Site.Name, copied.To == DbAdmin.NameFor(renamed.Site.Name) ? null : copied.To);
                if (SiteDatabaseCopy.UpdateEnv(renamed.Site.Folder, copied.From, copied.To) is { } env)
                    AnsiConsole.MarkupLineInterpolated($"[grey]DB_DATABASE={copied.To} en {env}[/]");
            }

            AnsiConsole.MarkupLineInterpolated($"[green]Renombrado[/] {renamed.Old.Host} -> [bold]{renamed.Site.Host}[/] [grey]({renamed.Site.Folder})[/]");
            foreach (var (from, to) in renamed.AliasesMoved)
                AnsiConsole.MarkupLineInterpolated($"[grey]Subdominio {from} -> {to}[/]");
            if (copied is null)
                AnsiConsole.MarkupLineInterpolated($"[grey]Su base de datos sigue siendo {SiteManager.DatabaseOf(renamed.Site)}.[/]");
            foreach (var conf in renamed.ConfsKept)
                AnsiConsole.MarkupLineInterpolated($"[grey]El vhost de antes queda en {conf} (si tenia cambios tuyos, copialos al nuevo tras roost up).[/]");
            if (renamed.Site.Git?.KeyName is { } key && key != renamed.Site.Name)
                AnsiConsole.MarkupLineInterpolated($"[grey]Su clave SSH sigue siendo la de Roost llamada {key}.[/]");

            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            var config = RoostConfig.Load(paths.ConfigFile);
            return await StackApply.IfRunningAsync(paths, _ => StackPlanner.SiteUrl(renamed.Site, config.HttpsPort), cancellation);
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }

    /// <summary>La base de datos global, que tiene que estar en marcha para copiar.</summary>
    internal static (InstalledDb Db, string Ini) RunningDb(RoostPaths paths)
    {
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var config = RoostConfig.Load(paths.ConfigFile);
        var db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled())
            ?? throw new RoostException("No hay ninguna base de datos global: no hay nada que copiar (deja --db keep).");
        if (!StackPlanner.AcceptsConnectionsAsync(config.DbPort).GetAwaiter().GetResult())
            throw new RoostException($"{db.Label} no esta en marcha (127.0.0.1:{config.DbPort}): arrancala con roost up y vuelve a probar.");
        return (db, planner.DbIniPath(db));
    }
}

/// <summary>
/// <c>roost site move</c>: el sitio pasa a servirse desde otra carpeta. Sin <c>--move-files</c> solo
/// apunta a una que ya existe; con el, Roost mueve alli la carpeta del proyecto (misma unidad).
/// </summary>
public sealed class SiteMoveCommand : AsyncCommand<SiteMoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SITIO>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "<CARPETA>")]
        [Description("La carpeta nueva (en relativo, dentro de la www).")]
        public string Folder { get; init; } = "";

        [CommandOption("--move-files")]
        [Description("Mueve tambien la carpeta del proyecto alli (tiene que no existir o estar vacia, en la misma unidad). Sin esta opcion, solo se apunta a una carpeta que ya existe.")]
        public bool MoveFiles { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("site move")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var moved = new SiteManager(paths).Move(settings.Name, settings.Folder, settings.MoveFiles);
            if (moved.FilesMoved)
                AnsiConsole.MarkupLineInterpolated($"[green]Movido[/] {moved.Old.Folder} -> [bold]{moved.Site.Folder}[/]: {moved.Site.Host} se sirve desde alli.");
            else
                AnsiConsole.MarkupLineInterpolated($"[green]{moved.Site.Host}[/] se sirve ahora desde [bold]{moved.Site.Folder}[/] [grey](la carpeta de antes, {moved.Old.Folder}, no se ha tocado)[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Raiz web: {moved.Site.DocumentRoot}[/]");
            if (moved.Old.Git is not null && moved.Site.Git is null)
                AnsiConsole.MarkupLine("[grey]Ya no es el checkout de su repositorio: si la carpeta nueva tiene .git, adoptalo con roost site git <sitio> --adopt.[/]");
            return await StackApply.IfRunningAsync(paths, _ => $"{moved.Site.Host} -> {moved.Site.DocumentRoot}", cancellation);
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }
}

/// <summary>
/// <c>roost site alias</c>: los alias de un sitio (otros nombres .test) y sus subdominios con nombre
/// (<c>api.tienda.test</c>). Sin accion, los lista.
/// </summary>
public sealed class SiteAliasCommand : AsyncCommand<SiteAliasCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SITIO>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "[ACCION]")]
        [Description("add o remove. Sin ella, lista los alias del sitio.")]
        public string? Action { get; init; }

        [CommandArgument(2, "[NOMBRE]")]
        [Description("El alias (tienda-alias o tienda-alias.test) o el subdominio (api.tienda.test).")]
        public string? Alias { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var site = sites.Find(settings.Name)
                ?? throw new RoostException($"No hay ningun sitio '{settings.Name}'. Mira los registrados con: roost site list");
            var action = settings.Action?.Trim().ToLowerInvariant();
            if (action is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[bold]{site.Host}[/] responde tambien por [grey]*.{site.Host}[/] (el certificado lo cubre).");
                if (site.Aliases is not { Count: > 0 } list)
                    AnsiConsole.MarkupLineInterpolated($"Sin alias. Para anadir uno: roost site alias {site.Name} add {site.Name}-alias.test (o un subdominio: api.{site.Host})");
                else
                    foreach (var alias in list)
                        AnsiConsole.MarkupLineInterpolated($"  {alias} [grey]{(SiteAliases.IsSubdomainOf(alias, site.Host) ? "(subdominio)" : "(alias)")}[/]");
                return 0;
            }

            if (action is not ("add" or "remove") || string.IsNullOrWhiteSpace(settings.Alias))
                throw new RoostException("Uso: roost site alias <sitio> add|remove <nombre>");

            var changed = action == "add" ? sites.AddAlias(site.Name, settings.Alias) : sites.RemoveAlias(site.Name, settings.Alias);
            var name = SiteAliases.Normalize(settings.Alias);
            if (action == "add")
                AnsiConsole.MarkupLineInterpolated($"[green]Anadido[/] {name} a {changed.Host}");
            else
                AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {name} de {changed.Host}");
            try
            {
                if (ProjectFile.Read(changed.Folder)?.Aliases is not null)
                    AnsiConsole.MarkupLine("[grey](tambien en su .roost.toml)[/]");
            }
            catch (RoostException) { /* un .roost.toml roto lo dira el siguiente up */ }
            if (ProjectKind.Detect(changed.Folder) is { Type: ProjectType.WordPress } && action == "add" && SiteAliases.IsSubdomainOf(name, changed.Host))
                AnsiConsole.MarkupLine("[grey]WordPress multisite con subdominios: cada subdominio necesita su linea en el hosts, o pasa al DNS local (roost dns install), que los resuelve todos.[/]");

            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            return await StackApply.IfRunningAsync(paths, _ => string.Join(", ", changed.HostNames), cancellation);
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }
}

/// <summary><c>roost site hosts</c>: deja en el hosts los nombres de todos los sitios (un solo permiso, y solo si cambia).</summary>
public sealed class SiteHostsCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            SiteSupport.SyncHosts(paths, new SiteRegistry(paths).Load());
            return 0;
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }
}

/// <summary>
/// <c>roost park</c>: aparca una carpeta (como el <c>valet park</c> de Valet): cada subcarpeta es un
/// sitio &lt;subcarpeta&gt;.test, ahora y cuando aparezcan o desaparezcan (la app y el daemon la vigilan).
/// Repetirlo sobre la misma carpeta la vuelve a sincronizar.
/// </summary>
public sealed class ParkCommand : AsyncCommand<ParkCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[CARPETA]")]
        [Description("La carpeta que se aparca. Por defecto, la actual.")]
        public string? Folder { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("park")) return 1;
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var folder = Path.GetFullPath(settings.Folder ?? Environment.CurrentDirectory);
            var sync = sites.Park(folder);
            AnsiConsole.MarkupLineInterpolated($"[green]Aparcada[/] {folder}: cada subcarpeta es un sitio <subcarpeta>.test.");
            SiteSources.PrintPark(sync);
            if (!sync.Changed) AnsiConsole.MarkupLine("[grey]Ningun sitio nuevo.[/]");
            AnsiConsole.MarkupLine("[grey]Con la app abierta o el stack en marcha, una subcarpeta nueva es un sitio nuevo, y una borrada se quita.[/]");
            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            return sync.Changed ? await StackApply.IfRunningAsync(paths, s => $"{s.Sites.Count} sitio(s) en marcha.", cancellation) : 0;
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }
}

/// <summary><c>roost unpark</c>: deja de aparcar una carpeta y quita sus sitios (las carpetas no se tocan).</summary>
public sealed class UnparkCommand : AsyncCommand<UnparkCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[CARPETA]")]
        [Description("La carpeta aparcada. Por defecto, la actual.")]
        public string? Folder { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var folder = Path.GetFullPath(settings.Folder ?? Environment.CurrentDirectory);
            var removed = sites.Unpark(folder);
            AnsiConsole.MarkupLineInterpolated($"[green]Ya no esta aparcada[/] {folder}. [grey]Las carpetas no se han tocado.[/]");
            foreach (var site in removed)
                AnsiConsole.MarkupLineInterpolated($"[red]-[/] {site.Host}");
            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            return removed.Count > 0 ? await StackApply.IfRunningAsync(paths, s => $"{s.Sites.Count} sitio(s) en marcha.", cancellation) : 0;
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }
}

/// <summary><c>roost parked</c>: las carpetas aparcadas y sus sitios.</summary>
public sealed class ParkedCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var folders = new ParkedFolders(paths).Load();
            if (folders.Count == 0)
            {
                AnsiConsole.MarkupLine("Ninguna carpeta aparcada. Prueba: roost park C:\\proyectos");
                return 0;
            }
            var sites = new SiteRegistry(paths).Load();
            foreach (var folder in folders)
            {
                var mine = sites.Where(s => s.ParkedIn is { } p && RoostPaths.SameFolder(p, folder)).ToList();
                AnsiConsole.MarkupLineInterpolated($"[bold]{folder}[/]{(Directory.Exists(folder) ? "" : " [yellow](no esta ahora)[/]")} [grey]{mine.Count} sitio(s)[/]");
                foreach (var site in mine)
                    AnsiConsole.MarkupLineInterpolated($"  {site.Host} [grey]{Path.GetFileName(site.Folder)}{(site.Php is { } php ? $" · PHP {php}" : "")}[/]");
            }
            return 0;
        }
        catch (RoostException ex)
        {
            return SiteSources.Fail(ex);
        }
    }
}
