using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Roost.Cli.Commands.Db;
using Roost.Cli.Commands.Services;
using Roost.Cli.Commands.Sites;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Share;
using Roost.Core.Sites;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Share;

/// <summary>Lo comun de los comandos de compartir: cloudflared, los avisos, el portapapeles y la regla del cortafuegos.</summary>
internal static class ShareSupport
{
    /// <summary>El cloudflared de Roost; si no hay ninguno, instala el de esta version (con el progreso en pantalla).</summary>
    public static async Task<InstalledCloudflared> EnsureCloudflaredAsync(RoostPaths paths, CancellationToken cancellation)
    {
        if (new CloudflaredInstaller(paths).Locate() is { } found) return found;
        var build = CloudflaredCatalog.Current;
        var installer = new CloudflaredInstaller(paths, RoostHttp.Client);
        return await ServiceSetup.InstallAsync(build.Label, build.Url, (p, ct) => installer.InstallAsync(build, p, ct), cancellation);
    }

    /// <summary>El sitio por su nombre (tambien «tienda.test»), o un error que dice cuales hay.</summary>
    public static Site FindSite(RoostPaths paths, string name)
    {
        var wanted = name.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        var sites = new SiteRegistry(paths).Load();
        return sites.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new RoostException(sites.Count == 0
                ? $"No hay ningun sitio llamado {name}: aun no hay sitios (roost site add <nombre>)."
                : $"No hay ningun sitio llamado {name}. Los que hay: {string.Join(", ", sites.Select(s => s.Name))}.");
    }

    /// <summary>Los avisos de compartir por internet, sin tildes (los ensena la CLI).</summary>
    public static IReadOnlyList<string> TunnelWarnings(Site site, ShareCheck check)
    {
        var warnings = new List<string>
        {
            $"Cualquiera que tenga el enlace podra ver {site.Host} mientras dure. Nada se comparte solo: se para con Ctrl+C, con roost share stop {site.Name} o con roost down.",
            check.Type == ProjectType.WordPress
                ? "WordPress guarda sus URLs (siteurl y home) en la base de datos y siguen en .test: fuera de tu equipo, los enlaces y el CSS pueden no cargar."
                : $"{(check.AppUrl is { } url ? $"APP_URL ({url})" : "APP_URL")} y las URLs que guarde la aplicacion siguen apuntando a {site.Host}: lo que las use no funcionara fuera de tu equipo (las que arma con la peticion, si).",
            "Cloudflare ve lo que pasa por el tunel; los tuneles rapidos son para pruebas (sin garantia de que duren) y van con sus condiciones: https://www.cloudflare.com/website-terms/",
        };
        if (check.Debug) warnings.Insert(1, DebugWarning(check));
        return warnings;
    }

    /// <summary>Los avisos de compartir en la red local, sin tildes.</summary>
    public static IReadOnlyList<string> LanWarnings(Site site, ShareCheck check, string url)
    {
        var warnings = new List<string>
        {
            $"Cualquier equipo de tu red local (la misma wifi) podra ver {site.Host} en {url} mientras dure. Nada se comparte solo: se para con Ctrl+C, con roost share stop {site.Name} --lan o con roost down.",
            "El movil no se fia de la CA de Roost: el navegador avisara de que la conexion no es privada (pulsa \"Avanzado\" y \"Continuar\"). Si quieres que no avise, instala la CA en el movil desde "
            + url.TrimEnd('/') + ShareConf.CaPath + " (Android: Ajustes > Seguridad > Instalar certificado de CA; iPhone: instalar el perfil y activarlo en Ajustes > General > Informacion > Ajustes de confianza de certificados). Quitala al acabar: quien tenga la clave de la CA podria suplantar webs en ese movil.",
            $"APP_URL y las URLs que guarde la aplicacion siguen apuntando a {site.Host}.",
        };
        if (check.Debug) warnings.Insert(1, DebugWarning(check));
        return warnings;
    }

    private static string DebugWarning(ShareCheck check) => check.Type switch
    {
        ProjectType.Laravel => "Tiene APP_DEBUG=true: una pagina de error ensenaria rutas, la configuracion y datos del .env (contrasenas incluidas) a quien tenga el enlace. Ponlo a false mientras compartes.",
        ProjectType.Symfony => "Esta en APP_ENV=dev: la barra de depuracion y el profiler (con las peticiones y la configuracion) quedan a la vista de quien tenga el enlace.",
        ProjectType.WordPress => "Tiene WP_DEBUG activo: los errores de PHP salen en la pagina, con rutas del equipo.",
        _ => "Tiene el modo de depuracion activo.",
    };

    public static void PrintWarnings(string title, IEnumerable<string> warnings)
    {
        AnsiConsole.MarkupLineInterpolated($"[yellow]{title}[/]");
        foreach (var warning in warnings) AnsiConsole.MarkupLineInterpolated($"  [yellow]-[/] {warning}");
    }

    /// <summary>
    /// Copia al portapapeles (clip.exe en Windows; wl-copy o xclip en Linux: <see cref="Clipboard"/>); si no se ha
    /// podido, false y por que, para decirlo en gris (no pasa nada: la URL sale igual). Con <paramref name="skip"/> (--no-copy), nada.
    /// </summary>
    public static async Task<(bool Copied, string? WhyNot)> CopyAsync(string text, bool skip, CancellationToken cancellation) =>
        skip ? (false, null) : await Clipboard.CopyAsync(text, CommandRunner.Default, cancellation);

    /// <summary>Lo que va tras la URL: «(copiada al portapapeles)» o, si no se ha podido, por que.</summary>
    public static string CopiedNote((bool Copied, string? WhyNot) copy) =>
        copy.Copied ? " [grey](copiada al portapapeles)[/]" : copy.WhyNot is { } why ? $" [grey](sin copiar al portapapeles: {Markup.Escape(why)})[/]" : "";

    /// <summary>Como se pide el permiso de administrador en este sistema, para los avisos.</summary>
    public static string AdminPrompt => OperatingSystem.IsWindows() ? "Windows pedira permiso de administrador" : "pedira permiso de administrador (pkexec o sudo)";

    /// <summary>Quita la regla del cortafuegos de un puerto (UAC), contando lo que pasa; nunca lanza.</summary>
    public static void RemoveFirewallRule(int port)
    {
        AnsiConsole.MarkupLineInterpolated($"Quitando la regla del cortafuegos [grey]«{LanFirewall.RuleName(port)}»[/] ({AdminPrompt})...");
        try
        {
            LanFirewall.Remove(SiteSupport.HelperExe, port);
            AnsiConsole.MarkupLine("[green]Regla quitada.[/]");
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{ex.Message}[/]");
        }
    }

    /// <summary>Lo compartido ahora, o nada si el daemon no esta.</summary>
    public static async Task<IReadOnlyList<ShareDto>> CurrentAsync(DaemonClient client, CancellationToken cancellation)
    {
        if (await client.TryStatusAsync(cancellation) is null) return [];
        try { return (await client.SharesAsync(cancellation)).Shares; }
        catch (HttpRequestException) { return []; }   // un daemon de antes de compartir
    }

    /// <summary>Tras roost down: lo compartido en la red local ya no escucha; se quita su regla del cortafuegos.</summary>
    public static void AfterDown(IReadOnlyList<ShareDto> before)
    {
        foreach (var port in before.Where(s => !s.IsTunnel).Select(s => s.Port).Distinct())
            RemoveFirewallRule(port);
    }

    public static void PrintApplied(IReadOnlyList<string> applied)
    {
        if (applied.Count > 0) AnsiConsole.MarkupLineInterpolated($"[grey]Aplicado: {string.Join("; ", applied)}.[/]");
    }
}

/// <summary>
/// <c>roost share &lt;sitio&gt;</c>: ensena un sitio fuera del equipo, por internet con un tunel rapido de
/// Cloudflare (sin cuenta) o, con <c>--lan</c>, en la red local. Lo lleva el daemon: el comando se queda
/// esperando y Ctrl+C lo para (si se cierra la terminal, el daemon tambien lo para). Con
/// <c>--detach</c> o <c>--json</c> vuelve enseguida y sigue hasta <c>roost share stop</c> o <c>roost down</c>.
/// </summary>
public sealed class ShareCommand : AsyncCommand<ShareCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[SITIO]")]
        [Description("El sitio que compartir (su nombre, o nombre.test).")]
        public string? Site { get; init; }

        [CommandOption("--lan")]
        [Description("En la red local (un movil en la misma wifi) en vez de por internet: HTTPS en la IP del equipo, con una regla del cortafuegos solo para tu red privada (pide permiso de administrador; en Linux, solo si hay ufw o firewalld).")]
        public bool Lan { get; init; }

        [CommandOption("--ip <IP>")]
        [Description("Con --lan: la IP de este equipo en la que escuchar (por defecto, la de la wifi o el cable con salida a internet).")]
        public string? Ip { get; init; }

        [CommandOption("--detach")]
        [Description("No se queda esperando: sigue compartido hasta roost share stop <sitio> o roost down.")]
        public bool Detach { get; init; }

        [CommandOption("--no-copy")]
        [Description("No copia la URL al portapapeles.")]
        public bool NoCopy { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No pregunta antes de publicar (sin terminal, o con --json, hace falta).")]
        public bool Yes { get; init; }

        [CommandOption("--json")]
        [Description("Salida JSON para maquinas (implica --detach).")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        if (string.IsNullOrWhiteSpace(settings.Site))
        {
            var names = new SiteRegistry(paths).Load().Select(s => s.Name).ToList();
            AnsiConsole.MarkupLine("Que sitio? [bold]roost share <sitio>[/] (por internet) o [bold]roost share <sitio> --lan[/] (en tu red local).");
            AnsiConsole.MarkupLineInterpolated($"[grey]{(names.Count == 0 ? "Aun no hay sitios (roost site add <nombre>)." : "Sitios: " + string.Join(", ", names) + ".")} Lo que se comparte ahora: roost share status[/]");
            return 1;
        }
        try
        {
            var site = ShareSupport.FindSite(paths, settings.Site);
            using var client = new DaemonClient(paths);
            if (await client.TryStatusAsync(cancellation) is not { Running: true })
                throw new RoostException("El stack no esta en marcha: arrancalo con roost up y vuelve a compartir.");

            var check = ShareCheck.Inspect(site);
            return settings.Lan
                ? await ShareLanAsync(paths, client, site, check, settings, cancellation)
                : await ShareTunnelAsync(paths, client, site, check, settings, cancellation);
        }
        catch (RoostException ex)
        {
            return Fail(settings, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail(settings, $"No se ha podido hablar con el daemon ({ex.Message}). Prueba roost up.");
        }
    }

    private static async Task<int> ShareTunnelAsync(RoostPaths paths, DaemonClient client, Site site, ShareCheck check, Settings settings, CancellationToken cancellation)
    {
        var warnings = ShareSupport.TunnelWarnings(site, check);
        if (!settings.Json)
        {
            ShareSupport.PrintWarnings($"Vas a publicar {site.Host} en internet con un tunel rapido de Cloudflare (trycloudflare.com, sin cuenta):", warnings);
            if (!DbSession.Confirm($"Publicar {site.Host} en internet?", settings.Yes)) return 1;
        }
        else if (!settings.Yes)
        {
            return Fail(settings, "Publicar en internet hay que confirmarlo: repitelo con --yes.");
        }

        await ShareSupport.EnsureCloudflaredAsync(paths, cancellation);
        ShareResponse response = null!;
        if (settings.Json) response = await client.ShareTunnelAsync(site.Name, cancellation);
        else await AnsiConsole.Status().StartAsync("Abriendo el tunel (puede tardar unos segundos)...", async _ => response = await client.ShareTunnelAsync(site.Name, cancellation));
        if (!response.Ok || response.Share is not { } share) return Fail(settings, response.Error ?? "El daemon no ha dicho por que.");

        var copy = await ShareSupport.CopyAsync(share.Url, settings.NoCopy, cancellation);
        if (settings.Json) return Json(share, copy.Copied, warnings);

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(site.Host)} esta en internet:[/] [bold]{Markup.Escape(share.Url)}[/]{ShareSupport.CopiedNote(copy)}");
        if (!share.Connected) AnsiConsole.MarkupLine("[grey]Cloudflare aun esta preparando la conexion: si da error, prueba en unos segundos.[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]Log del tunel: {QuickTunnel.LogFile(paths, site.Name)}[/]");
        ShareSupport.PrintApplied(response.Applied);
        return await HoldAsync(client, site, ShareKind.Tunnel, settings, firewallPort: null);
    }

    private static async Task<int> ShareLanAsync(RoostPaths paths, DaemonClient client, Site site, ShareCheck check, Settings settings, CancellationToken cancellation)
    {
        var config = RoostConfig.Load(paths.ConfigFile);
        var candidates = LanNetwork.Candidates();
        var nic = LanNetwork.Choose(candidates, settings.Ip);
        var probe = await LanProbe.ReadAsync(CommandRunner.Default, cancellation);
        var category = probe.CategoryOf(nic.InterfaceIndex);
        if (category == NetworkCategory.Public && OperatingSystem.IsWindows())
        {
            throw new RoostException(
                $"La red de {nic.Name} ({nic.Address}) esta marcada como publica, y Windows no deja entrar a nada desde una red publica (la regla de Roost es solo para redes privadas). "
                + $"Si es tu red de casa o de la oficina, marcala como privada (Configuracion > Red e Internet > {nic.Name} > Tipo de perfil de red: Privada) y vuelve a probar; si no, comparte por internet: roost share {site.Name}");
        }

        var current = await ShareSupport.CurrentAsync(client, cancellation);
        if (current.FirstOrDefault(s => !s.IsTunnel && s.Site == site.Name) is { } already)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]{site.Host} ya se comparte en la red local:[/] [bold]{already.Url}[/]");
            return 0;
        }
        var address = nic.Address.ToString();
        var port = LanNetwork.ChoosePort(config.HttpsPort,
            p => current.Any(s => s.Address == address && s.Port == p) || !LanNetwork.CanBind(nic.Address, p));
        var url = LanNetwork.Url(address, port);
        var warnings = LanWarnings(site, check, url, category);

        if (!settings.Json)
        {
            ShareSupport.PrintWarnings($"Vas a ensenar {site.Host} en tu red local ({nic.Name}, {address}):", warnings);
            if (!DbSession.Confirm($"Compartir {site.Host} en la red local?", settings.Yes)) return 1;
        }
        else if (!settings.Yes)
        {
            return Fail(settings, "Compartir en la red local hay que confirmarlo: repitelo con --yes.");
        }

        // Primero la regla del cortafuegos (UAC): sin ella, Windows no deja entrar y avisaria del servidor web.
        // En Linux la pone el helper en ufw o firewalld si estan activos (y apunta el puerto); sin cortafuegos, nada.
        var addedRule = false;
        var rulePorts = OperatingSystem.IsWindows() ? probe.RulePorts : await LanFirewall.RulePortsAsync(CommandRunner.Default, cancellation);
        if (!rulePorts.Contains(port) && LanFirewall.Needed)
        {
            if (!settings.Json)
                AnsiConsole.MarkupLineInterpolated($"Poniendo la regla del cortafuegos [grey]«{LanFirewall.RuleName(port)}»[/] (TCP {port}, {(OperatingSystem.IsWindows() ? "solo red privada y tu subred" : "solo desde redes privadas")}; {ShareSupport.AdminPrompt})...");
            LanFirewall.Allow(SiteSupport.HelperExe, port);
            addedRule = true;
        }

        ShareResponse response = null!;
        if (settings.Json) response = await client.ShareLanAsync(site.Name, address, port, cancellation);
        else await AnsiConsole.Status().StartAsync("Abriendo la escucha en la red local...", async _ => response = await client.ShareLanAsync(site.Name, address, port, cancellation));
        if (!response.Ok || response.Share is not { } share)
        {
            if (addedRule && !settings.Json) ShareSupport.RemoveFirewallRule(port);
            else if (addedRule) TryRemoveRuleQuietly(port);
            return Fail(settings, response.Error ?? "El daemon no ha dicho por que.");
        }

        var copy = await ShareSupport.CopyAsync(share.Url, settings.NoCopy, cancellation);
        if (settings.Json) return Json(share, copy.Copied, warnings);

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(site.Host)} esta en tu red local:[/] [bold]{Markup.Escape(share.Url)}[/]{ShareSupport.CopiedNote(copy)}");
        AnsiConsole.MarkupLine("[grey]Abrela en el movil (conectado a la misma wifi); el navegador avisara del certificado la primera vez.[/]");
        ShareSupport.PrintApplied(response.Applied);
        return await HoldAsync(client, site, ShareKind.Lan, settings, firewallPort: port);
    }

    private static IReadOnlyList<string> LanWarnings(Site site, ShareCheck check, string url, NetworkCategory category)
    {
        var warnings = ShareSupport.LanWarnings(site, check, url).ToList();
        // En Linux no hay «tipo de red» (es cosa del cortafuegos de Windows): el tipo es desconocido y no se avisa de eso.
        if (category == NetworkCategory.Unknown && OperatingSystem.IsWindows())
            warnings.Add("No se sabe si esta red es privada o publica: si Windows la tiene como publica, el movil no llegara (la regla de Roost es solo para redes privadas).");
        return warnings;
    }

    /// <summary>
    /// Espera a que se deje de compartir: con Ctrl+C se para aqui mismo; si se cierra la terminal, el
    /// daemon lo nota y lo para. Al acabar, en la red local, se quita la regla del cortafuegos.
    /// </summary>
    private static async Task<int> HoldAsync(DaemonClient client, Site site, ShareKind kind, Settings settings, int? firewallPort)
    {
        var key = ShareListener.KindKey(kind);
        if (settings.Detach)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Sigue compartido hasta: roost share stop {site.Name}{(kind == ShareKind.Lan ? " --lan" : "")} (o roost down).[/]");
            return 0;
        }

        using var stop = new TaskCompletionSourceHolder();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;   // no se mata el proceso: se para bien
            stop.Signal();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            AnsiConsole.MarkupLine("[grey]Pulsa Ctrl+C para dejar de compartir.[/]");
            using var holding = new CancellationTokenSource();
            var hold = client.HoldShareAsync(site.Name, key, holding.Token);
            var first = await Task.WhenAny(hold, stop.Task);
            string reason;
            if (first == stop.Task)
            {
                var stopped = await client.StopShareAsync(site.Name, key);
                reason = stopped.Ok ? "Se ha dejado de compartir." : stopped.Error ?? "Se ha dejado de compartir.";
                try { reason = (await hold.WaitAsync(TimeSpan.FromSeconds(10))).Error ?? reason; }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException or IOException) { holding.Cancel(); }
            }
            else
            {
                reason = (await hold).Error ?? "Se ha dejado de compartir.";
            }
            AnsiConsole.MarkupLineInterpolated($"{reason}");
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Se ha perdido el contacto con el daemon ({ex.Message}).[/]");
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        if (firewallPort is { } port) ShareSupport.RemoveFirewallRule(port);
        return 0;
    }

    private static void TryRemoveRuleQuietly(int port)
    {
        try { LanFirewall.Remove(SiteSupport.HelperExe, port); }
        catch (RoostException) { /* sigue puesta: roost share stop --lan la quita */ }
    }

    private static int Json(ShareDto share, bool copied, IReadOnlyList<string> warnings)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            site = share.Site,
            kind = share.Kind,
            url = share.Url,
            address = share.Address,
            port = share.Port,
            connected = share.Connected,
            since = share.Since,
            copied,
            warnings,
        }, JsonOutput.Options));
        return 0;
    }

    private static int Fail(Settings settings, string message)
    {
        if (settings.Json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }, JsonOutput.Options));
        else AnsiConsole.MarkupLineInterpolated($"[red]{message}[/]");
        return 1;
    }

    /// <summary>Un aviso de una sola vez que se puede esperar (el Ctrl+C).</summary>
    private sealed class TaskCompletionSourceHolder : IDisposable
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => _tcs.Task;
        public void Signal() => _tcs.TrySetResult();
        public void Dispose() => _tcs.TrySetCanceled();
    }
}

/// <summary>
/// <c>roost share stop [&lt;sitio&gt;] [--lan|--tunnel]</c>: deja de compartir (sin sitio, todo). Con lo de la
/// red local quita tambien la regla del cortafuegos; <c>roost share stop --lan</c> sin nada compartido
/// quita las reglas «Roost LAN» que hayan quedado.
/// </summary>
public sealed class ShareStopCommand : AsyncCommand<ShareStopCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[SITIO]")]
        [Description("El sitio; sin el, todo lo que se este compartiendo.")]
        public string? Site { get; init; }

        [CommandOption("--lan")]
        [Description("Solo lo de la red local (y las reglas del cortafuegos de Roost que hayan quedado).")]
        public bool Lan { get; init; }

        [CommandOption("--tunnel")]
        [Description("Solo el tunel por internet.")]
        public bool Tunnel { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var site = settings.Site is { } name ? ShareSupport.FindSite(paths, name).Name : null;
            using var client = new DaemonClient(paths);
            var current = await ShareSupport.CurrentAsync(client, cancellation);
            var targets = current
                .Where(s => site is null || s.Site == site)
                .Where(s => settings.Lan == settings.Tunnel || (settings.Lan ? !s.IsTunnel : s.IsTunnel))
                .ToList();

            foreach (var share in targets)
            {
                var response = await client.StopShareAsync(share.Site, share.Kind, cancellation);
                if (response.Ok)
                    AnsiConsole.MarkupLineInterpolated($"[green]Dejado de compartir[/] {share.Site} {(share.IsTunnel ? "por internet" : "en la red local")} [grey]({share.Url})[/].");
                else
                    AnsiConsole.MarkupLineInterpolated($"[yellow]{share.Site}: {response.Error}[/]");
            }

            // Las reglas del cortafuegos: las de lo que se acaba de parar y, con --lan, las que hayan quedado.
            var ports = targets.Where(s => !s.IsTunnel).Select(s => s.Port).ToHashSet();
            if (settings.Lan && site is null)
            {
                var stillShared = current.Except(targets).Where(s => !s.IsTunnel).Select(s => s.Port).ToHashSet();
                foreach (var port in await LanFirewall.RulePortsAsync(CommandRunner.Default, cancellation))
                    if (!stillShared.Contains(port)) ports.Add(port);
            }
            foreach (var port in ports.Order()) ShareSupport.RemoveFirewallRule(port);

            if (targets.Count == 0 && ports.Count == 0)
                AnsiConsole.MarkupLine(site is null ? "No se estaba compartiendo nada." : $"{site} no se estaba compartiendo.");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}

/// <summary><c>roost share status</c>: lo que se esta compartiendo, cloudflared y las reglas del cortafuegos de Roost.</summary>
public sealed class ShareStatusCommand : AsyncCommand<ShareStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        using var client = new DaemonClient(paths);
        var shares = await ShareSupport.CurrentAsync(client, cancellation);
        var cloudflared = new CloudflaredInstaller(paths).Locate();
        var rules = await LanFirewall.RulePortsAsync(CommandRunner.Default, cancellation);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                shares,
                cloudflared = new
                {
                    installed = cloudflared is not null,
                    version = cloudflared?.Version,
                    executable = cloudflared?.Executable,
                    roost = new { version = CloudflaredCatalog.Current.Version, url = CloudflaredCatalog.Current.Url.ToString(), sha256 = CloudflaredCatalog.Current.Sha256 },
                },
                firewallRules = rules.Select(p => new { port = p, name = LanFirewall.RuleName(p) }),
            }, JsonOutput.Options));
            return 0;
        }

        if (shares.Count == 0) AnsiConsole.MarkupLine("No se esta compartiendo nada.");
        foreach (var share in shares)
        {
            var where = share.IsTunnel ? "por internet" : "en la red local";
            var connecting = share.Connected ? "" : ", conectando";
            AnsiConsole.MarkupLineInterpolated($"[yellow]{share.Site}[/] {where}: [bold]{share.Url}[/] [grey](desde {share.Since:HH:mm}{connecting})[/]");
        }
        if (cloudflared is null)
            AnsiConsole.MarkupLineInterpolated($"[grey]cloudflared: sin instalar (roost share lo instala la primera vez: {CloudflaredCatalog.Current.Label}).[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"[grey]cloudflared: {cloudflared.Label} ({cloudflared.Executable}).[/]");
        if (rules.Count > 0)
        {
            var orphan = rules.Where(p => !shares.Any(s => !s.IsTunnel && s.Port == p)).ToList();
            AnsiConsole.MarkupLineInterpolated($"[grey]Reglas del cortafuegos de Roost: {string.Join(", ", rules.Select(LanFirewall.RuleName))}.[/]");
            if (orphan.Count > 0) AnsiConsole.MarkupLine("[yellow]Hay reglas sin nada compartido:[/] quitalas con roost share stop --lan");
        }
        return 0;
    }
}

/// <summary><c>roost share install</c>: baja cloudflared verificado (roost share lo hace solo la primera vez).</summary>
public sealed class ShareInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var build = CloudflaredCatalog.Current;
            var installer = new CloudflaredInstaller(paths, RoostHttp.Client);
            if (installer.Find(build) is { } found)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]{build.Label} ya esta instalado:[/] {found.Directory}");
                return 0;
            }
            await ServiceSetup.InstallAsync(build.Label, build.Url, (p, ct) => installer.InstallAsync(build, p, ct), cancellation);
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}
