using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
        var names = sites.Select(s => s.Host).ToList();
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
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost site solo funciona en Windows por ahora[/] (ROOST.md: fase 1, Windows primero).");
            return 1;
        }

        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var change = sites.Add(settings.Name, settings.Path, settings.Root, settings.Php);
            var site = change.Site;
            if (change.FolderCreated)
                AnsiConsole.MarkupLineInterpolated($"[grey]Carpeta creada: {site.Folder}[/]");
            AnsiConsole.MarkupLineInterpolated($"[green]{(change.Created ? "Sitio registrado" : "Sitio actualizado")}:[/] [bold]{site.Host}[/] -> {site.DocumentRoot} [grey](PHP {site.Php ?? "global"})[/]");

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

/// <summary><c>roost site remove</c>: quita un sitio. La carpeta del proyecto no se toca nunca.</summary>
public sealed class SiteRemoveCommand : AsyncCommand<SiteRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var sites = new SiteManager(paths);
        try
        {
            var site = sites.Remove(settings.Name);
            if (site is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                return 1;
            }

            AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {site.Host}. [grey]La carpeta del proyecto ({site.Folder}) no se ha tocado.[/]");
            SiteSupport.SyncHosts(paths, sites.Registry.Load());
            return await StackApply.IfRunningAsync(paths, s => $"{s.Sites.Count} sitio(s) en marcha.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
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
                    certExpires = expires,
                    inHosts = inHosts.Contains(site.Host),
                };
            }).ToList();

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { caTrusted = ca.IsTrusted(), resolver = config.SitesResolver, hostsFile = new HostsUpdater().HostsFile, sites = rows }, JsonOutput.Options));
                return 0;
            }

            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine("Ningun sitio todavia. Prueba: roost site add tienda");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Sitio"); table.AddColumn("Carpeta"); table.AddColumn("PHP"); table.AddColumn("Certificado"); table.AddColumn("hosts");
            foreach (var r in rows)
            {
                table.AddRow(
                    Markup.Escape(r.url),
                    Markup.Escape(r.documentRoot),
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
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost cert trust solo funciona en Windows por ahora.[/]");
            return 1;
        }

        var paths = RoostPaths.Default();
        var ca = new LocalCa(paths);
        try
        {
            using (var cert = ca.LoadOrCreate())
            {
                if (ca.IsTrusted())
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]Ya confias en la CA de Roost[/] ({cert.Subject}).");
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
            AnsiConsole.MarkupLine("[green]Hecho:[/] Chrome y Edge ya confian en https://*.test. [grey]Firefox usa su propio almacen: activa security.enterprise_roots.enabled en about:config.[/]");
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
