using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Share;
using Roost.Core.Sites;
using Roost.Daemon;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Lo que necesita el bloque Compartir de fuera de la app: el daemon (que lleva el túnel y la escucha
/// de la red local), las interfaces de red, lo que dice Windows de las redes y del cortafuegos, y
/// <c>roost-helper</c> para la regla (UAC). Los tests lo cambian por uno de pega: nunca abren un túnel,
/// ni escuchan en la red local, ni tocan el cortafuegos.
/// </summary>
public class ShareSystem
{
    public virtual async Task<IReadOnlyList<ShareDto>> ListAsync(DaemonClient daemon, CancellationToken cancellation = default)
    {
        try { return (await daemon.SharesAsync(cancellation)).Shares; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException or RoostException) { return []; }
    }

    public virtual Task<ShareResponse> TunnelAsync(DaemonClient daemon, string site) => daemon.ShareTunnelAsync(site);

    public virtual Task<ShareResponse> LanAsync(DaemonClient daemon, string site, string address, int port) => daemon.ShareLanAsync(site, address, port);

    public virtual Task<ShareResponse> StopAsync(DaemonClient daemon, string site, string kind) => daemon.StopShareAsync(site, kind);

    /// <summary>Espera a que se deje de compartir; si se cancela (o la app sale), el daemon lo para.</summary>
    public virtual Task<ShareResponse> HoldAsync(DaemonClient daemon, string site, string kind, CancellationToken cancellation) =>
        daemon.HoldShareAsync(site, kind, cancellation);

    public virtual IReadOnlyList<LanCandidate> Candidates() => LanNetwork.Candidates();

    public virtual Task<LanProbeResult> ProbeAsync() => LanProbe.ReadAsync(CommandRunner.Default);

    public virtual bool CanBind(IPAddress address, int port) => LanNetwork.CanBind(address, port);

    /// <summary>Pone la regla del cortafuegos con <c>roost-helper</c> elevado (Windows pide permiso).</summary>
    public virtual void AllowFirewall(int port) => LanFirewall.Allow(AppServices.HelperExe, port);

    public virtual void RemoveFirewall(int port) => LanFirewall.Remove(AppServices.HelperExe, port);

    public virtual InstalledCloudflared? Cloudflared(Roost.Core.Platform.RoostPaths paths) => new CloudflaredInstaller(paths).Locate();
}

/// <summary>
/// El bloque Compartir del panel del sitio (oleada 6, N3 y L1): «Compartir por internet…» (un túnel
/// rápido de Cloudflare, sin cuenta) y «En la red local…» (un móvil en la misma wifi), cada uno con su
/// diálogo de avisos antes de nada (<see cref="ShareDialogViewModel"/>); compartiendo, la URL con
/// «Copiar» y «Abrir» y «Dejar de compartir». Lo lleva el daemon; la app «sujeta» lo que abre ella
/// (<see cref="ShareSystem.HoldAsync"/>), así que al salir de Roost se deja de compartir, y lo de la red
/// local quita su regla del cortafuegos. Lo compartido desde la CLI también sale aquí.
/// </summary>
public sealed partial class SitesViewModel
{
    /// <summary>El daemon, la red y el cortafuegos; los tests lo cambian.</summary>
    internal ShareSystem Sharing { get; set; } = new();

    private IReadOnlyList<ShareDto> _shares = [];
    private bool _sharesLoaded;

    /// <summary>Lo que ha abierto la app y sujeta mientras dura, por sitio y tipo (con el puerto de su regla si es de la red local).</summary>
    private readonly Dictionary<(string Site, string Kind), (CancellationTokenSource Hold, int? FirewallPort)> _ownShares = [];

    /// <summary>Lo que el usuario está parando desde aquí: al acabar, su espera no lo cuenta como algo inesperado.</summary>
    private readonly HashSet<(string Site, string Kind)> _stopping = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSiteTunnel), nameof(IsSharing), nameof(ShareChip), nameof(ShareChipTone), nameof(TunnelHeadline), nameof(TunnelConnecting), nameof(ShowShareDebug), nameof(ShareIntroVisible))]
    public partial ShareDto? SiteTunnel { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSiteLan), nameof(IsSharing), nameof(ShareChip), nameof(ShareChipTone), nameof(LanHeadline), nameof(LanCertHint), nameof(ShowShareDebug), nameof(ShareIntroVisible))]
    public partial ShareDto? SiteLan { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareIdle))]
    public partial bool ShareBusy { get; private set; }

    [ObservableProperty] public partial string ShareBusyText { get; private set; } = "";

    /// <summary>El modo de depuración del proyecto, si está puesto (con él, una página de error enseña de más).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowShareDebug))]
    public partial string ShareDebugText { get; private set; } = "";

    [ObservableProperty] public partial string ShareUrlsHint { get; private set; } = "";

    public bool HasSiteTunnel => SiteTunnel is not null;

    public bool HasSiteLan => SiteLan is not null;

    public bool IsSharing => HasSiteTunnel || HasSiteLan;

    public bool ShareIntroVisible => !IsSharing;

    public bool ShareIdle => !ShareBusy;

    /// <summary>Sin el stack en marcha no hay nada que enseñar: el bloque ofrece «Iniciar todo».</summary>
    public bool ShareStackStopped => !Main.Stack.IsRunning;

    public bool ShowShareDebug => IsSharing && ShareDebugText.Length > 0;

    public string ShareChip => HasSiteTunnel ? "público en internet" : HasSiteLan ? "en la red local" : "";

    public Tone ShareChipTone => HasSiteTunnel ? Tone.Bad : Tone.Warn;

    public string TunnelHeadline => SiteTunnel is { } tunnel
        ? $"Público en internet desde las {tunnel.Since.ToLocalTime():HH:mm}: cualquiera con el enlace puede verlo."
        : "";

    public bool TunnelConnecting => SiteTunnel is { Connected: false };

    public string LanHeadline => SiteLan is { } lan
        ? $"En tu red local desde las {lan.Since.ToLocalTime():HH:mm}: lo ve cualquier equipo de la misma wifi."
        : "";

    public string LanCertHint => SiteLan is { } lan
        ? $"El móvil no conoce la CA de Roost: avisará del certificado («Avanzado» › «Continuar»). Para que no avise, instala la CA desde {lan.Url.TrimEnd('/')}{ShareConf.CaPath} y quítala al acabar."
        : "";

    private void PickShare(Site site)
    {
        SiteTunnel = _shares.FirstOrDefault(s => s.IsTunnel && string.Equals(s.Site, site.Name, StringComparison.OrdinalIgnoreCase));
        SiteLan = _shares.FirstOrDefault(s => !s.IsTunnel && string.Equals(s.Site, site.Name, StringComparison.OrdinalIgnoreCase));
        var check = ShareCheck.Inspect(site);
        ShareDebugText = ShareTexts.Debug(check) ?? "";
        ShareUrlsHint = ShareTexts.Urls(site, check);
        OnPropertyChanged(nameof(ShareStackStopped));
    }

    /// <summary>Lee del daemon lo que se está compartiendo (solo con el stack en marcha: si no, no hay nada).</summary>
    private async Task RefreshSharesAsync()
    {
        if (!Main.Stack.IsRunning)
        {
            if (_shares.Count == 0 && _sharesLoaded) return;
            _shares = [];
        }
        else
        {
            _shares = await Sharing.ListAsync(Main.App.Daemon);
        }
        _sharesLoaded = true;
        if (Selected is { } row) PickShare(row.Site);
    }

    /// <summary>Para los tests: lo que diría el daemon, sin preguntarle.</summary>
    internal void ShowShares(IReadOnlyList<ShareDto> shares)
    {
        _shares = shares;
        _sharesLoaded = true;
        if (Selected is { } row) PickShare(row.Site);
    }

    // Por internet

    [RelayCommand]
    private Task ShareTunnelAsync() => Selected is not { } row ? Task.CompletedTask : Main.RunAsync($"No se ha podido compartir {row.Host}", async () =>
    {
        if (!Main.Stack.IsRunning)
        {
            await Main.InfoAsync("Inicia el stack para compartir", "Compartir enseña lo que sirve Roost: pulsa Iniciar todo y vuelve a probar.", Tone.Warn);
            return;
        }
        var dialog = ShareDialogViewModel.ForTunnel(row.Site, ShareCheck.Inspect(row.Site));
        if (await Main.ShowDialogAsync(dialog) is not true) return;
        if (Sharing.Cloudflared(Main.App.Paths) is null && !await InstallCloudflaredAsync()) return;

        ShareBusy = true;
        ShareBusyText = "Abriendo el túnel con Cloudflare… (unos segundos)";
        try
        {
            var response = await Sharing.TunnelAsync(Main.App.Daemon, row.Site.Name);
            if (!response.Ok || response.Share is not { } share)
            {
                await Main.InfoAsync($"No se ha podido compartir {row.Host}", response.Error ?? "El daemon no ha dicho por qué.", Tone.Bad);
                return;
            }
            Own(row.Site.Name, share.Kind, firewallPort: null);
            await RefreshSharesAsync();
            await Main.CopyAsync(share.Url, $"{row.Host} está en internet: enlace copiado");
        }
        finally
        {
            ShareBusy = false;
        }
    });

    /// <summary>cloudflared con el diálogo Instalando de siempre (descarga, SHA-256 antes de nada, carpeta atómica).</summary>
    private async Task<bool> InstallCloudflaredAsync()
    {
        var build = CloudflaredCatalog.Windows;
        var installer = new CloudflaredInstaller(Main.App.Paths, Main.App.Http);
        var target = installer.InstallDirectory(build);
        var dialog = new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"el cliente de Cloudflare Tunnel (Apache-2.0) · desde {build.Url.Host}",
            55_329_336,
            [
                new("Versión fijada en Roost", "· el mismo SHA-256 en la API de GitHub y en la release, y firmado por Cloudflare"),
                new("Descargando cloudflared.exe", null),
                new("Verificar el SHA-256", "· antes de copiar nada"),
                new($"Copiar a {target}", null),
            ],
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Copiando");
            });
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        if (shown is null && dialog.Failed)
            await Main.InfoAsync($"No se instaló {build.Label}", dialog.Error!, Tone.Bad);
        return await dialog.Finished;
    }

    // En la red local

    [RelayCommand]
    private Task ShareLanAsync() => Selected is not { } row ? Task.CompletedTask : Main.RunAsync($"No se ha podido compartir {row.Host} en la red local", async () =>
    {
        if (!Main.Stack.IsRunning)
        {
            await Main.InfoAsync("Inicia el stack para compartir", "Compartir enseña lo que sirve Roost: pulsa Iniciar todo y vuelve a probar.", Tone.Warn);
            return;
        }

        var nic = LanNetwork.Choose(Sharing.Candidates(), null);
        var probe = await Sharing.ProbeAsync();
        var category = probe.CategoryOf(nic.InterfaceIndex);
        if (category == NetworkCategory.Public)
        {
            await Main.InfoAsync("Tu red está marcada como pública", ShareTexts.PublicNetwork(nic), Tone.Warn);
            return;
        }

        RoostConfig config;
        try { config = RoostConfig.Load(Main.App.Paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        var address = nic.Address.ToString();
        var port = LanNetwork.ChoosePort(config.HttpsPort, p => _shares.Any(s => s.Address == address && s.Port == p) || !Sharing.CanBind(nic.Address, p));
        var url = LanNetwork.Url(address, port);
        var ruleExists = probe.RulePorts.Contains(port);

        var dialog = ShareDialogViewModel.ForLan(row.Site, ShareCheck.Inspect(row.Site), nic, port, url, category, ruleExists);
        if (await Main.ShowDialogAsync(dialog) is not true) return;

        ShareBusy = true;
        var addedRule = false;
        try
        {
            if (!ruleExists)
            {
                // Antes de escuchar: sin la regla, Windows no deja entrar (y avisaría del servidor web).
                ShareBusyText = $"Poniendo la regla «{LanFirewall.RuleName(port)}» del cortafuegos (Windows pide permiso)…";
                await Task.Run(() => Sharing.AllowFirewall(port));
                addedRule = true;
            }
            ShareBusyText = "Abriendo la escucha en la red local…";
            var response = await Sharing.LanAsync(Main.App.Daemon, row.Site.Name, address, port);
            if (!response.Ok || response.Share is not { } share)
            {
                if (addedRule) await RemoveRuleAsync(port, quiet: true);
                await Main.InfoAsync($"No se ha podido compartir {row.Host} en la red local", response.Error ?? "El daemon no ha dicho por qué.", Tone.Bad);
                return;
            }
            Own(row.Site.Name, share.Kind, port);
            await RefreshSharesAsync();
            await Main.CopyAsync(share.Url, $"{row.Host} está en tu red local: dirección copiada");
        }
        finally
        {
            ShareBusy = false;
        }
    });

    // Compartiendo

    [RelayCommand]
    private Task CopyTunnelUrlAsync() => SiteTunnel is { } tunnel ? Main.CopyAsync(tunnel.Url, "Enlace copiado") : Task.CompletedTask;

    [RelayCommand]
    private Task OpenTunnelUrlAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        if (SiteTunnel is { } tunnel) Shell.OpenUrl(tunnel.Url);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task CopyLanUrlAsync() => SiteLan is { } lan ? Main.CopyAsync(lan.Url, "Dirección copiada") : Task.CompletedTask;

    [RelayCommand]
    private Task OpenLanUrlAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        if (SiteLan is { } lan) Shell.OpenUrl(lan.Url);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task StopTunnelAsync() => SiteTunnel is { } tunnel ? StopShareAsync(tunnel) : Task.CompletedTask;

    [RelayCommand]
    private Task StopLanAsync() => SiteLan is { } lan ? StopShareAsync(lan) : Task.CompletedTask;

    [RelayCommand]
    private Task ShareStartStackAsync() => Main.StartStackAsync();

    private Task StopShareAsync(ShareDto share) => Main.RunAsync($"No se ha podido dejar de compartir {share.Site}", async () =>
    {
        var key = (share.Site, share.Kind);
        _stopping.Add(key);
        ShareBusy = true;
        ShareBusyText = "Dejando de compartir…";
        try
        {
            var response = await Sharing.StopAsync(Main.App.Daemon, share.Site, share.Kind);
            // Lo de la red local quita su regla (Windows vuelve a pedir permiso), sea de la app o de la CLI.
            if (!share.IsTunnel) await RemoveRuleAsync(share.Port, quiet: false);
            await RefreshSharesAsync();
            if (response.Ok) Main.Flash($"{share.Site}.test ya no se comparte {(share.IsTunnel ? "por internet" : "en la red local")}");
            else if (IsSharingNow(share)) await Main.InfoAsync($"No se ha podido dejar de compartir {share.Site}", response.Error ?? "", Tone.Bad);
        }
        finally
        {
            ShareBusy = false;
            _stopping.Remove(key);
        }
    });

    private bool IsSharingNow(ShareDto share) => _shares.Any(s => s.Site == share.Site && s.Kind == share.Kind);

    /// <summary>Quita la regla del cortafuegos (UAC). Si se cancela o falla, se dice (salvo <paramref name="quiet"/>) y sigue puesta.</summary>
    private async Task RemoveRuleAsync(int port, bool quiet)
    {
        try
        {
            await Task.Run(() => Sharing.RemoveFirewall(port));
        }
        catch (RoostException ex)
        {
            AppLog.Warn($"cortafuegos: {ex.Message}");
            if (!quiet)
                await Main.InfoAsync($"La regla «{LanFirewall.RuleName(port)}» sigue en el cortafuegos", ShareTexts.RuleLeft(port, ex.Message), Tone.Warn);
        }
    }

    /// <summary>
    /// Lo abre la app, así que lo sujeta: si la app sale (aunque sea de golpe), el daemon deja de
    /// compartirlo. Cuando acaba por otra cosa (un <c>roost share stop</c>, un <c>roost down</c>, el túnel
    /// que se cae), el panel se pone al día y lo dice.
    /// </summary>
    private void Own(string site, string kind, int? firewallPort)
    {
        var key = (site, kind);
        if (_ownShares.TryGetValue(key, out var old)) old.Hold.Cancel();
        var hold = new CancellationTokenSource();
        _ownShares[key] = (hold, firewallPort);
        _ = WatchOwnAsync(key, hold);
    }

    private async Task WatchOwnAsync((string Site, string Kind) key, CancellationTokenSource hold)
    {
        string? reason = null;
        try
        {
            reason = (await Sharing.HoldAsync(Main.App.Daemon, key.Site, key.Kind, hold.Token)).Error;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or RoostException)
        {
            // Cancelado (la app sale) o sin daemon: no hay nada más que esperar.
        }
        if (_ownShares.TryGetValue(key, out var own) && own.Hold == hold) _ownShares.Remove(key);
        if (hold.IsCancellationRequested) return;

        await RefreshSharesAsync();
        if (!_stopping.Contains(key) && reason is not null)
            Main.Flash($"{key.Site}.test ya no se comparte: {reason}");
    }

    /// <summary>
    /// Al salir de Roost: lo que abrió la app deja de compartirse (y lo de la red local quita su regla;
    /// Windows pide permiso). Lo abierto desde la CLI con <c>--detach</c> sigue, porque así se pidió.
    /// </summary>
    public async Task StopOwnSharesAsync()
    {
        foreach (var ((site, kind), (hold, port)) in _ownShares.ToList())
        {
            _stopping.Add((site, kind));
            try { await Sharing.StopAsync(Main.App.Daemon, site, kind); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { /* sin daemon no queda nada compartido */ }
            if (port is { } rule) await RemoveRuleAsync(rule, quiet: true);
            hold.Cancel();
        }
        _ownShares.Clear();
    }

    /// <summary>Lo que ha abierto la app y sigue compartido (para la salida de Roost).</summary>
    public bool HasOwnShares => _ownShares.Count > 0;
}

/// <summary>Un aviso del diálogo de compartir, con su color.</summary>
public sealed record ShareWarning(string Text, Tone Tone);

/// <summary>
/// El diálogo antes de compartir: lo que pasa al compartir (público en internet o en la red local),
/// lo que no va a funcionar fuera (las URLs guardadas en <c>.test</c>), el modo de depuración si está
/// puesto y, en la red local, la IP, el puerto y la regla del cortafuegos. Nada se comparte sin pasar
/// por aquí. Devuelve true si se confirma.
/// </summary>
public sealed partial class ShareDialogViewModel : DialogViewModel
{
    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required string ConfirmLabel { get; init; }

    public required IReadOnlyList<ShareWarning> Warnings { get; init; }

    /// <summary>Solo en la red local: la IP, el puerto y la URL que se va a dar.</summary>
    public string? Detail { get; init; }

    public bool HasDetail => Detail is not null;

    public bool IsLan { get; init; }

    public override bool Wide => false;

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close();

    public static ShareDialogViewModel ForTunnel(Site site, ShareCheck check)
    {
        var warnings = new List<ShareWarning>
        {
            new($"Queda público en internet mientras lo compartas: cualquiera que tenga el enlace puede ver {site.Host}. Nada se comparte solo, y se para con «Dejar de compartir», al salir de Roost o al pararlo todo.", Tone.Bad),
        };
        if (ShareTexts.Debug(check) is { } debug) warnings.Add(new(debug, Tone.Bad));
        warnings.Add(new(ShareTexts.Urls(site, check), Tone.Warn));
        warnings.Add(new("Cloudflare ve lo que pasa por el túnel. Los túneles rápidos son para probar: sin cuenta, sin garantía de que duren y con sus condiciones de uso (cloudflare.com/website-terms).", Tone.Neutral));
        return new ShareDialogViewModel
        {
            Title = $"Compartir {site.Host} por internet",
            Subtitle = "Con un túnel rápido de Cloudflare (trycloudflare.com), sin cuenta: un enlace temporal que lleva a tu equipo.",
            ConfirmLabel = "Compartir por internet",
            Warnings = warnings,
        };
    }

    public static ShareDialogViewModel ForLan(Site site, ShareCheck check, LanCandidate nic, int port, string url, NetworkCategory category, bool ruleExists)
    {
        var warnings = new List<ShareWarning>
        {
            new($"Cualquier equipo de tu red local (la misma wifi) podrá ver {site.Host} en {url} mientras lo compartas. Se para con «Dejar de compartir», al salir de Roost o al pararlo todo.", Tone.Warn),
        };
        if (ShareTexts.Debug(check) is { } debug) warnings.Add(new(debug, Tone.Bad));
        if (category == NetworkCategory.Unknown)
            warnings.Add(new("Windows no dice si esta red es privada o pública: si la tiene como pública, el móvil no llegará (la regla de Roost es solo para redes privadas).", Tone.Warn));
        warnings.Add(new(ruleExists
            ? $"La regla «{LanFirewall.RuleName(port)}» del cortafuegos ya está puesta: solo TCP {port}, solo en redes privadas y solo desde tu subred."
            : $"Windows pedirá permiso para poner la regla «{LanFirewall.RuleName(port)}» del cortafuegos: solo TCP {port}, solo en redes privadas y solo desde tu subred. Roost la quita al dejar de compartir.", Tone.Neutral));
        warnings.Add(new($"El móvil no conoce la CA de Roost: el navegador avisará de que la conexión no es privada («Avanzado» › «Continuar»). Para que no avise, instala la CA desde {url.TrimEnd('/')}{ShareConf.CaPath} y quítala al acabar.", Tone.Neutral));
        warnings.Add(new(ShareTexts.Urls(site, check), Tone.Warn));
        return new ShareDialogViewModel
        {
            Title = $"Compartir {site.Host} en la red local",
            Subtitle = "Para verlo desde el móvil u otro equipo de la misma wifi, con HTTPS y un certificado que lleva la IP.",
            ConfirmLabel = "Compartir en la red local",
            Warnings = warnings,
            Detail = $"{url}   ·   {nic.Name} ({nic.Address}), puerto {port}",
            IsLan = true,
        };
    }
}

/// <summary>Los textos de compartir de la app, con tildes (los de la CLI van sin ellas, en <c>ShareSupport</c>).</summary>
internal static class ShareTexts
{
    public static string? Debug(ShareCheck check) => !check.Debug ? null : check.Type switch
    {
        ProjectType.Laravel => "Tiene APP_DEBUG=true: una página de error enseñaría rutas, la configuración y datos del .env (contraseñas incluidas) a quien tenga el enlace. Ponlo a false mientras compartes.",
        ProjectType.Symfony => "Está en APP_ENV=dev: la barra de depuración y el profiler (con las peticiones y la configuración) quedan a la vista de quien tenga el enlace.",
        ProjectType.WordPress => "Tiene WP_DEBUG activo: los errores de PHP salen en la página, con rutas de tu equipo.",
        _ => "Tiene el modo de depuración activo: una página de error enseñaría de más.",
    };

    public static string Urls(Site site, ShareCheck check) => check.Type == ProjectType.WordPress
        ? $"WordPress guarda sus URLs (siteurl y home) en la base de datos y siguen en {site.Host}: fuera de tu equipo, los enlaces y el CSS pueden no cargar."
        : $"{(check.AppUrl is { } url ? $"APP_URL ({url})" : "APP_URL")} y las URLs absolutas que guarde la aplicación siguen apuntando a {site.Host}: lo que las use no cargará fuera de tu equipo. Las que se arman con la petición (Laravel, Symfony) sí salen bien.";

    public static string PublicNetwork(LanCandidate nic) =>
        $"La red de {nic.Name} ({nic.Address}) está marcada como pública, y Windows no deja entrar a nada desde una red pública (la regla de Roost es solo para redes privadas).\n\n"
        + $"Si es tu red de casa o de la oficina, márcala como privada en Configuración › Red e Internet › {nic.Name} › Tipo de perfil de red: Privada, y vuelve a probar. Si no, compártelo por internet.";

    public static string RuleLeft(int port, string why) =>
        $"{why}\n\nSolo deja entrar al puerto {port} desde tu red local, y ya no escucha nada en él. Se quitará la próxima vez que dejes de compartir en la red local, o con «roost share stop --lan».";
}
