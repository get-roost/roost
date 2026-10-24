using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Roost.Cli.Commands.Git;
using Roost.Cli.Rendering;
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

/// <summary>Lo comun de los comandos de sitios: el hosts (via roost-helper) y como contar URLs.</summary>
internal static class SiteSupport
{
    /// <summary>roost-helper.exe va junto a roost.exe.</summary>
    public static string HelperExe => Path.Combine(AppContext.BaseDirectory, "roost-helper.exe");

    /// <summary>
    /// Deja en el hosts los .test de <paramref name="sites"/>. Solo pide permiso de administrador
    /// (UAC) si el bloque de Roost cambia de verdad.
    /// </summary>
    public static void SyncHosts(RoostPaths paths, IReadOnlyList<Site> sites)
    {
        if (RoostConfig.Load(paths.ConfigFile).UsesLocalDns)
        {
            AnsiConsole.MarkupLine("[grey]DNS local: los .test ya resuelven, no hace falta tocar el hosts.[/]");
            return;
        }

        var hosts = new HostsUpdater();
        // Cada sitio, sus alias y sus subdominios con nombre (C7).
        var names = SiteManager.HostNames(sites);
        if (hosts.InSync(names))
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]hosts al dia ({hosts.HostsFile})[/]");
            return;
        }

        if (hosts.IsSystemFile)
            AnsiConsole.MarkupLine("Windows va a pedir permiso de administrador para actualizar el fichero hosts (solo las lineas .test de Roost)...");
        hosts.Sync(names, HelperExe, Path.Combine(paths.Run, "hosts-request.txt"));
        AnsiConsole.MarkupLineInterpolated($"[green]hosts actualizado[/] [grey]({hosts.HostsFile})[/]");
    }

    public static string HttpUrl(Site site, int httpPort) =>
        httpPort == 80 ? $"http://{site.Host}/" : $"http://{site.Host}:{httpPort}/";

    public static void TrustHint(LocalCa ca)
    {
        if (!ca.IsTrusted())
            AnsiConsole.MarkupLine("[yellow]Para que el navegador confie en https://*.test:[/] roost cert trust [grey](una sola vez)[/]");
    }
}

/// <summary><c>roost site add</c>: registra un sitio <c>https://&lt;nombre&gt;.test/</c> (repetirlo lo actualiza).</summary>
public sealed class SiteAddCommand : AsyncCommand<SiteAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio: se sirve en https://<nombre>.test/ (minusculas, numeros y guiones).")]
        public string Name { get; init; } = "";

        [CommandOption("--path <CARPETA>")]
        [Description("Carpeta del proyecto. Por defecto, <www>/<nombre> (se crea si no existe).")]
        public string? Path { get; init; }

        [CommandOption("--root <RUTA>")]
        [Description("Raiz web dentro de la carpeta (por ejemplo public). Por defecto, public si existe.")]
        public string? Root { get; init; }

        [CommandOption("--php <VERSION>")]
        [Description("PHP del sitio (rama, version o instalacion), o global para volver al PHP global. Por defecto, el global.")]
        public string? Php { get; init; }

        [CommandOption("--save")]
        [Description("Guarda el PHP, la raiz, la base de datos y los alias del sitio en el .roost.toml de la carpeta (lo crea si no existe), para versionarlo con el proyecto.")]
        public bool Save { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("site add")) return 1;

        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var change = sites.Add(settings.Name, settings.Path, settings.Root, settings.Php);
            var site = change.Site;
            if (change.FolderCreated)
                AnsiConsole.MarkupLineInterpolated($"[grey]Carpeta creada: {site.Folder}[/]");
            if (change.Project is { IsEmpty: false })
                AnsiConsole.MarkupLineInterpolated($"[grey]Lee {ProjectFile.PathIn(site.Folder)}[/]");
            AnsiConsole.MarkupLineInterpolated($"[green]{(change.Created ? "Sitio registrado" : "Sitio actualizado")}:[/] [bold]{site.Host}[/] -> {site.DocumentRoot} [grey](PHP {site.Php ?? "global"})[/]");
            if (site.Aliases is { Count: > 0 } aliases)
                AnsiConsole.MarkupLineInterpolated($"[grey]Alias: {string.Join(", ", aliases)}[/]");
            // Lo que se ha pedido a mano tambien va al .roost.toml, si lo tiene (o si se pide --save).
            if (settings.Save)
                AnsiConsole.MarkupLineInterpolated($"[green]Guardado[/] en {sites.SaveProjectFile(site.Name)}");
            else
                sites.KeepProjectFile(site, (settings.Php is null ? ProjectKeys.None : ProjectKeys.Php) | (settings.Root is null ? ProjectKeys.None : ProjectKeys.Root));

            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            SiteSupport.TrustHint(new LocalCa(paths));

            var config = RoostConfig.Load(paths.ConfigFile);
            return await StackApply.IfRunningAsync(paths,
                _ => $"{StackPlanner.SiteUrl(site, config.HttpsPort)} y {SiteSupport.HttpUrl(site, config.HttpPort)}", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary>
/// <c>roost site remove</c>: quita un sitio. La carpeta del proyecto no se toca nunca. Ofrece quitar
/// tambien su acceso (su clave SSH de Roost y su token), salvo lo que compartan otros sitios; el
/// historial y el registro de git se quedan.
/// </summary>
public sealed class SiteRemoveCommand : AsyncCommand<SiteRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandOption("--keep-access")]
        [Description("Deja su clave SSH de Roost y su token (por defecto se quitan con el sitio, salvo los que usen otros sitios).")]
        public bool KeepAccess { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No pregunta si quitar tambien la clave y el token: los quita (sin terminal, igual).")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths) { Access = GitAccessSupport.Factory(paths) };
        try
        {
            var found = sites.Find(settings.Name);
            if (found is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                return 1;
            }

            // Lo que se quitaria, antes de tocar nada: para preguntarlo.
            var plan = sites.RemoveAccess(found, dryRun: true);
            var removable = (plan.KeyName is not null && !plan.KeyShared) || (plan.Token is not null && !plan.TokenShared);
            var removeAccess = removable && !settings.KeepAccess;
            if (removeAccess && !settings.Yes && GitAccessSupport.Interactive)
            {
                AnsiConsole.MarkupLineInterpolated($"{found.Host} tiene {DescribeAccess(sites, plan)}.");
                removeAccess = AnsiConsole.Confirm("Quitar tambien su clave SSH y su token?", defaultValue: true);
            }

            var site = sites.Remove(settings.Name);
            if (site is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                return 1;
            }

            AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {site.Host}. [grey]La carpeta del proyecto ({site.Folder}) no se ha tocado.[/]");
            ReportAccess(sites, removeAccess ? sites.RemoveAccess(site) : plan, removeAccess);
            if (site.Git is not null || File.Exists(plan.HistoryFile) || File.Exists(plan.LogFile))
                AnsiConsole.MarkupLineInterpolated($"[grey]El historial y el registro de git se quedan: {plan.HistoryFile} y {plan.LogFile}.[/]");
            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            return await StackApply.IfRunningAsync(paths, s => $"{s.Sites.Count} sitio(s) en marcha.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    private static string DescribeAccess(SiteManager sites, SiteAccessRemoval plan)
    {
        var parts = new List<string>();
        if (plan.KeyName is not null && !plan.KeyShared) parts.Add($"su clave SSH de Roost ({plan.KeyName})");
        if (plan.Token is not null && !plan.TokenShared) parts.Add($"un token ({sites.Access!.Credentials.Describe(plan.Token)})");
        return string.Join(" y ", parts);
    }

    private static void ReportAccess(SiteManager sites, SiteAccessRemoval access, bool removed)
    {
        var credentials = sites.Access!.Credentials;
        if (access.KeyName is not null)
        {
            if (access.KeyShared)
                AnsiConsole.MarkupLineInterpolated($"[grey]Su clave SSH ({access.KeyName}) se queda: la usa otro sitio.[/]");
            else if (removed && access.KeyRemoved)
                AnsiConsole.MarkupLineInterpolated($"[green]Quitada[/] su clave SSH de Roost ({access.KeyName}). [grey]Quita tambien la publica de las deploy keys del repositorio{(access.DeployKeysUrl is { } page ? $": {page}" : ".")}[/]");
            else if (!removed)
                AnsiConsole.MarkupLineInterpolated($"[grey]Su clave SSH ({access.KeyName}) se queda; para quitarla: roost git key remove {access.KeyName}[/]");
        }
        if (access.Token is not null)
        {
            var where = credentials.Describe(access.Token);
            if (access.TokenShared)
                AnsiConsole.MarkupLineInterpolated($"[grey]Su token ({where}) se queda: tambien vale para otro sitio.[/]");
            else if (removed && access.TokenRemoved)
                AnsiConsole.MarkupLineInterpolated($"[green]Borrado[/] su token ({where}) del Administrador de credenciales.");
            else if (!removed)
                AnsiConsole.MarkupLineInterpolated($"[grey]Su token ({where}) se queda; para borrarlo: roost git token remove {where}[/]");
        }
    }
}

/// <summary><c>roost site root</c>: ensena o cambia la raiz web de un sitio (la subcarpeta del proyecto que se sirve).</summary>
public sealed class SiteRootCommand : AsyncCommand<SiteRootCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandArgument(1, "[CARPETA]")]
        [Description("La raiz web nueva: una subcarpeta del proyecto (public, web, dist...) o . para la carpeta del proyecto. Sin ella, ensena la actual.")]
        public string? Root { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            if (settings.Root is null)
            {
                var site = sites.Find(settings.Name);
                if (site is null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                    return 1;
                }

                AnsiConsole.MarkupLineInterpolated($"[bold]{site.Host}[/] sirve {Describe(site)}: [grey]{site.DocumentRoot}[/]");
                if (!Directory.Exists(site.DocumentRoot))
                    AnsiConsole.MarkupLine("[yellow]Esa carpeta ya no existe:[/] el sitio dara error hasta que vuelva o cambies la raiz.");
                AnsiConsole.MarkupLineInterpolated($"[grey]Para cambiarla: roost site root {site.Name} <subcarpeta> (o . para la carpeta del proyecto).[/]");
                return 0;
            }

            var changed = sites.SetRoot(settings.Name, settings.Root);
            AnsiConsole.MarkupLineInterpolated($"[green]{changed.Host}[/] sirve ahora {Describe(changed)}: [grey]{changed.DocumentRoot}[/]");
            return await StackApply.IfRunningAsync(paths, _ => $"{changed.Host} -> {changed.DocumentRoot}", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    private static string Describe(Site site) => site.Root is null ? "la carpeta del proyecto" : $"la subcarpeta {site.Root}/";
}

/// <summary><c>roost site list</c>: los sitios, su carpeta, su PHP, su certificado y si estan en el hosts.</summary>
public sealed class SiteListCommand : Command<SiteListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar de la tabla.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var sites = new SiteRegistry(paths).Load();
            var config = RoostConfig.Load(paths.ConfigFile);
            var ca = new LocalCa(paths);
            var inHosts = new HostsUpdater().Current().ToHashSet();
            var planner = new StackPlanner(paths, ProcessRunner.Default);
            var installed = new PhpInstaller(paths).ListInstalled();
            InstalledPhp? global = null;
            try { global = planner.ResolvePhp(config, installed); } catch (RoostException) { /* sin PHP: se dice por sitio */ }

            var rows = sites.Select(site =>
            {
                string php;
                try { php = global is null ? "(sin PHP)" : planner.ResolveSitePhp(site, global, installed).Name; }
                catch (RoostException) { php = $"{site.Php} (no instalado)"; }
                string? expires = null;
                if (File.Exists(ca.SiteCertFile(site.Name)))
                {
                    using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(ca.SiteCertFile(site.Name)));
                    expires = cert.NotAfter.ToString("yyyy-MM-dd");
                }
                return new
                {
                    name = site.Name,
                    url = StackPlanner.SiteUrl(site, config.HttpsPort),
                    documentRoot = site.DocumentRoot,
                    php,
                    phpFixed = site.Php is not null,
                    database = SiteManager.DatabaseOf(site),
                    certExpires = expires,
                    inHosts = site.HostNames.All(inHosts.Contains),
                    aliases = site.Aliases ?? [],
                    parkedIn = site.ParkedIn,
                    projectFile = ProjectFile.ExistsIn(site.Folder) ? ProjectFile.PathIn(site.Folder) : null,
                };
            }).ToList();

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { caTrusted = ca.IsTrusted(), resolver = config.SitesResolver, hostsFile = new HostsUpdater().HostsFile, sites = rows }, JsonOutput.Options));
                return 0;
            }

            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine("Ningun sitio todavia." + PlatformGate.WindowsHint(" Prueba: roost site add tienda"));
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Sitio"); table.AddColumn("Carpeta"); table.AddColumn("PHP"); table.AddColumn("Certificado"); table.AddColumn("hosts");
            foreach (var r in rows)
            {
                table.AddRow(
                    Markup.Escape(r.url) + string.Concat(r.aliases.Select(a => $"\n[grey]+ {Markup.Escape(a)}[/]")),
                    Markup.Escape(r.documentRoot) + (r.parkedIn is null ? "" : "\n[grey](aparcado)[/]"),
                    Markup.Escape(r.php + (r.phpFixed ? "" : " (global)")),
                    r.certExpires is null ? "[grey]se emite en roost up[/]" : $"hasta {r.certExpires}",
                    config.UsesLocalDns ? "[green]DNS[/]" : r.inHosts ? "[green]si[/]" : "[red]no[/]");
            }
            AnsiConsole.Write(table);
            SiteSupport.TrustHint(ca);
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost cert trust</c>: anade la CA de Roost a los certificados de confianza del usuario.</summary>
public sealed class CertTrustCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("cert trust")) return 1;

        var paths = RoostPaths.Default();
        var ca = new LocalCa(paths);
        try
        {
            using (var cert = ca.LoadOrCreate())
            {
                if (ca.IsTrusted())
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]Ya confias en la CA de Roost[/] ({cert.Subject}).");
                    ConfigureFirefox();
                    return 0;
                }

                AnsiConsole.MarkupLineInterpolated($"Windows va a pedirte que confirmes que confias en [bold]{cert.GetNameInfo(X509NameType.SimpleName, false)}[/].");
                AnsiConsole.MarkupLineInterpolated($"[grey]Es la CA que firma los certificados de tus sitios .test. Esta solo en este equipo y su clave privada ({ca.CaKeyFile}) solo la puede leer tu usuario.[/]");
            }

            ca.Trust();
            if (!ca.IsTrusted())
            {
                AnsiConsole.MarkupLine("[red]La CA no se ha anadido[/] (rechazaste el aviso de Windows?).");
                return 1;
            }
            AnsiConsole.MarkupLine("[green]Hecho:[/] Chrome y Edge ya confian en https://*.test.");
            ConfigureFirefox();
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    /// <summary>
    /// Firefox usa su propio almacen: con security.enterprise_roots.enabled en el user.js de cada
    /// perfil lee las CA de confianza de Windows (las del usuario incluidas). Sin Firefox, nada.
    /// </summary>
    private static void ConfigureFirefox()
    {
        var profiles = FirefoxTrust.Profiles(FirefoxTrust.DefaultRoot());
        if (profiles.Count == 0) return;
        foreach (var profile in profiles) FirefoxTrust.Enable(profile);
        AnsiConsole.MarkupLineInterpolated($"[green]Firefox tambien[/] ({profiles.Count} perfil{(profiles.Count == 1 ? "" : "es")}): [grey]cierralo y vuelve a abrirlo para que lo aplique.[/]");
    }
}

/// <summary>
/// <c>roost cert untrust</c>: quita la CA de Roost de los certificados de confianza del usuario
/// (Windows pide confirmarlo) y lo que puso en Firefox. La CA y los certificados siguen en disco.
/// </summary>
public sealed class CertUntrustCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("cert untrust")) return 1;
        var ca = new LocalCa(RoostPaths.Default());
        try
        {
            var firefox = FirefoxTrust.Profiles(FirefoxTrust.DefaultRoot()).Where(FirefoxTrust.IsEnabled).ToList();
            if (ca.IsTrusted())
            {
                AnsiConsole.MarkupLine("Windows va a pedirte que confirmes que quitas la CA de Roost de tus certificados de confianza.");
                ca.Untrust();
                if (ca.IsTrusted())
                {
                    AnsiConsole.MarkupLine("[red]La CA sigue en tus certificados de confianza[/] (rechazaste el aviso de Windows?).");
                    return 1;
                }
                AnsiConsole.MarkupLine("[green]Hecho:[/] el navegador volvera a avisar en tus sitios .test. [grey]Para volver a confiar: roost cert trust[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("La CA de Roost no estaba en tus certificados de confianza.");
            }

            // Lo de Firefox lo puso Roost para esta CA: se quita también.
            foreach (var profile in firefox) FirefoxTrust.Disable(profile);
            if (firefox.Count > 0)
                AnsiConsole.MarkupLineInterpolated($"[green]Firefox tambien[/] ({firefox.Count} perfil{(firefox.Count == 1 ? "" : "es")}): [grey]cierralo y vuelve a abrirlo para que lo aplique.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost cert renew</c>: vuelve a emitir el certificado de todos los sitios.</summary>
public sealed class CertRenewCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var ca = new LocalCa(paths);
        var sites = new SiteRegistry(paths).Load();
        foreach (var site in sites)
        {
            ca.EnsureSiteCert(site, force: true);
            AnsiConsole.MarkupLineInterpolated($"[green]Renovado[/] {site.Host} [grey](hasta {DateTime.UtcNow + LocalCa.SiteValidity:yyyy-MM-dd})[/]");
        }
        if (sites.Count == 0) AnsiConsole.MarkupLine("No hay sitios: nada que renovar.");
        return sites.Count == 0 ? 0 : await StackApply.IfRunningAsync(paths, s => $"{s.Sites.Count} sitio(s) con su certificado nuevo.", cancellation);
    }
}
