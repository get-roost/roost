using System.Net;
using System.Net.NetworkInformation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Roost.Core.Share;
using Roost.Daemon;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

namespace Roost.Desktop.Tests;

/// <summary>
/// Compartir (oleada 6) en la app: el bloque del panel del sitio, el diálogo de avisos antes de
/// publicar, la URL con «Copiar», «Abrir» y «Dejar de compartir», la red local con su regla del
/// cortafuegos y lo que pasa al salir. Todo sobre un <see cref="ShareSystem"/> de pega: nada abre un
/// túnel, escucha en la red local, pide UAC ni toca el cortafuegos. Capturas en
/// <c>TestResults/snapshots/compartir-*.png</c>.
/// </summary>
public class CompartirViewTests
{
    private static readonly string SnapshotDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");

    private const string TunnelUrl = "https://seasons-hollow-zoning-tall.trycloudflare.com";

    private static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake, FakeSharing Sharing)> OpenAsync(NetworkCategory category = NetworkCategory.Private)
    {
        var fake = new FakeRoost();
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, version: FakeRoost.Version));
        var sharing = new FakeSharing { Category = category };
        main.Sites.Sharing = sharing;
        main.Stack.Show(FakeRoost.Running);
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.Current.ShownAsync();
        main.NotifyConfigChanged();
        return (main, window, fake, sharing);
    }

    /// <summary>El tienda de FakeRoost como un Laravel con APP_DEBUG=true (para el aviso del modo de depuración).</summary>
    private static void LaravelConDebug(FakeRoost fake)
    {
        var folder = Path.Combine(fake.Paths.Www, "tienda");
        File.WriteAllText(Path.Combine(folder, "artisan"), "");
        File.WriteAllText(Path.Combine(folder, ".env"), "APP_NAME=Tienda\nAPP_DEBUG=true\nAPP_URL=https://tienda.test\n");
    }

    private static async Task<SitesViewModel> TiendaAsync(MainViewModel main)
    {
        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        return sites;
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
            await Task.Delay(30);
        }
        Assert.True(condition());
    }

    /// <summary>Baja el scroll de la pantalla para que se vea el bloque Compartir.</summary>
    private static void ScrollPage(MainWindow window, object page, double y)
    {
        Dispatcher.UIThread.RunJobs();
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.DataContext == page);
        scroll.Offset = new Avalonia.Vector(0, y);
    }

    [AvaloniaFact]
    public async Task Compartir_SinCompartirNada_ElBloqueLoExplica_YOfreceLosDos()
    {
        var (main, window, fake, sharing) = await OpenAsync();
        using var _ = fake;

        var sites = await TiendaAsync(main);
        await UntilAsync(() => sharing.Lists > 0);

        Assert.False(sites.IsSharing);
        Assert.True(sites.ShareIntroVisible);
        Assert.False(sites.ShareStackStopped);
        Assert.True(sites.ShareTunnelCommand.CanExecute(null));
        Assert.True(sites.ShareLanCommand.CanExecute(null));
        ScrollPage(window, sites, 520);
        Snapshot(window, "compartir-panel");
    }

    [AvaloniaFact]
    public async Task Compartir_PorInternet_AvisaAntesDeNada_YLuegoEnseñaElEnlace()
    {
        var (main, window, fake, sharing) = await OpenAsync();
        using var _ = fake;
        LaravelConDebug(fake);
        var sites = await TiendaAsync(main);

        var running = sites.ShareTunnelCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ShareDialogViewModel);
        var dialog = (ShareDialogViewModel)main.Dialog!;

        // Antes de publicar: público en internet, el modo de depuración, las URLs en .test y Cloudflare.
        Assert.Equal("Compartir tienda.test por internet", dialog.Title);
        Assert.Equal("Compartir por internet", dialog.ConfirmLabel);
        Assert.StartsWith("Queda público en internet mientras lo compartas", dialog.Warnings[0].Text);
        Assert.Equal(Tone.Bad, dialog.Warnings[0].Tone);
        Assert.StartsWith("Tiene APP_DEBUG=true", dialog.Warnings[1].Text);
        Assert.StartsWith("APP_URL (https://tienda.test) y las URLs absolutas", dialog.Warnings[2].Text);
        Assert.Contains("Cloudflare ve lo que pasa por el túnel", dialog.Warnings[3].Text);
        Assert.Empty(sharing.Tunnels);   // nada se comparte sin confirmar
        Snapshot(window, "compartir-avisos-internet");

        dialog.ConfirmCommand.Execute(null);
        await running;

        Assert.Equal(["tienda"], sharing.Tunnels);
        Assert.True(sites.HasSiteTunnel);
        Assert.Equal(TunnelUrl, sites.SiteTunnel!.Url);
        Assert.Equal("público en internet", sites.ShareChip);
        Assert.Equal(Tone.Bad, sites.ShareChipTone);
        Assert.StartsWith("Público en internet desde las ", sites.TunnelHeadline);
        Assert.True(sites.ShowShareDebug);
        Assert.StartsWith("APP_URL (https://tienda.test)", sites.ShareUrlsHint);
        Assert.True(sites.HasOwnShares);
        Assert.Single(sharing.Holds);   // la app lo sujeta: si sale, se deja de compartir
        ScrollPage(window, sites, 760);
        Snapshot(window, "compartir-panel-internet");

        // Dejar de compartir.
        await sites.StopTunnelCommand.ExecuteAsync(null);
        Assert.Equal([("tienda", "tunnel")], sharing.Stops);
        Assert.False(sites.HasSiteTunnel);
        Assert.Empty(sharing.FirewallRemoved);
    }

    [AvaloniaFact]
    public async Task Compartir_PorInternet_SiSeCancelaElDialogo_NoSeHaceNada()
    {
        var (main, _, fake, sharing) = await OpenAsync();
        using var __ = fake;
        var sites = await TiendaAsync(main);

        var running = sites.ShareTunnelCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ShareDialogViewModel);
        ((ShareDialogViewModel)main.Dialog!).CancelCommand.Execute(null);
        await running;

        Assert.Empty(sharing.Tunnels);
        Assert.False(sites.IsSharing);
    }

    [AvaloniaFact]
    public async Task Compartir_PorInternet_SiElDaemonNoPuede_LoDice()
    {
        var (main, _, fake, sharing) = await OpenAsync();
        using var __ = fake;
        sharing.TunnelError = "No se ha podido hablar con trycloudflare.com: mira la conexion a internet.";
        var sites = await TiendaAsync(main);

        var running = sites.ShareTunnelCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ShareDialogViewModel);
        ((ShareDialogViewModel)main.Dialog!).ConfirmCommand.Execute(null);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var info = (MessageDialogViewModel)main.Dialog!;

        Assert.Equal("No se ha podido compartir tienda.test", info.Title);
        Assert.StartsWith("No se ha podido hablar con trycloudflare.com", info.Text);
        info.ChooseCommand.Execute(info.Buttons[0]);
        await running;
        Assert.False(sites.IsSharing);
    }

    [AvaloniaFact]
    public async Task Compartir_EnLaRedLocal_PoneLaReglaAntes_DaLaDireccionConLaIp_YLaQuitaAlParar()
    {
        var (main, window, fake, sharing) = await OpenAsync();
        using var _ = fake;
        var sites = await TiendaAsync(main);

        var running = sites.ShareLanCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ShareDialogViewModel);
        var dialog = (ShareDialogViewModel)main.Dialog!;

        // FakeRoost sirve el HTTPS en el 443: la dirección va sin puerto.
        Assert.Equal("Compartir tienda.test en la red local", dialog.Title);
        Assert.True(dialog.IsLan);
        Assert.Equal("https://192.168.1.20/   ·   Wi-Fi (192.168.1.20), puerto 443", dialog.Detail);
        Assert.Contains(dialog.Warnings, w => w.Text.StartsWith("Cualquier equipo de tu red local (la misma wifi) podrá ver tienda.test", StringComparison.Ordinal));
        Assert.Contains(dialog.Warnings, w => w.Text.StartsWith($"{OsTexts.WillAskPermission} para poner la regla «Roost LAN 443»", StringComparison.Ordinal));
        Assert.Contains(dialog.Warnings, w => w.Text.Contains("https://192.168.1.20/__roost/roost-ca.crt", StringComparison.Ordinal));
        Assert.Empty(sharing.FirewallAllowed);
        Snapshot(window, "compartir-avisos-red-local");

        dialog.ConfirmCommand.Execute(null);
        await running;

        Assert.Equal([443], sharing.FirewallAllowed);
        Assert.Equal([("tienda", "192.168.1.20", 443)], sharing.Lans);
        Assert.True(sites.HasSiteLan);
        Assert.Equal("https://192.168.1.20/", sites.SiteLan!.Url);
        Assert.Equal("en la red local", sites.ShareChip);
        Assert.StartsWith("El móvil no conoce la CA de Roost", sites.LanCertHint);
        ScrollPage(window, sites, 700);
        Snapshot(window, "compartir-panel-red-local");

        await sites.StopLanCommand.ExecuteAsync(null);
        Assert.Equal([("tienda", "lan")], sharing.Stops);
        Assert.Equal([443], sharing.FirewallRemoved);
        Assert.False(sites.HasSiteLan);
    }

    [AvaloniaFact]
    public async Task Compartir_EnLaRedLocal_ConLaReglaYaPuesta_NoPideOtraVez()
    {
        var (main, _, fake, sharing) = await OpenAsync();
        using var __ = fake;
        sharing.RulePorts = [443];
        var sites = await TiendaAsync(main);

        var running = sites.ShareLanCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ShareDialogViewModel);
        var dialog = (ShareDialogViewModel)main.Dialog!;
        Assert.Contains(dialog.Warnings, w => w.Text.StartsWith("La regla «Roost LAN 443» del cortafuegos ya está puesta", StringComparison.Ordinal));
        dialog.ConfirmCommand.Execute(null);
        await running;

        Assert.Empty(sharing.FirewallAllowed);
        Assert.True(sites.HasSiteLan);
    }

    [AvaloniaFact]
    public async Task Compartir_EnLaRedLocal_ConLaRedPublica_NoComparte_YDiceQueHacer()
    {
        var (main, window, fake, sharing) = await OpenAsync(NetworkCategory.Public);
        using var _ = fake;
        var sites = await TiendaAsync(main);

        var running = sites.ShareLanCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var info = (MessageDialogViewModel)main.Dialog!;

        Assert.Equal("Tu red está marcada como pública", info.Title);
        Assert.Contains("Tipo de perfil de red: Privada", info.Text);
        Snapshot(window, "compartir-red-publica");
        info.ChooseCommand.Execute(info.Buttons[0]);
        await running;
        Assert.Empty(sharing.FirewallAllowed);
        Assert.Empty(sharing.Lans);
    }

    [AvaloniaFact]
    public async Task Compartir_EnLaRedLocal_SiElDaemonFalla_QuitaLaReglaQueAcabaDePoner()
    {
        var (main, _, fake, sharing) = await OpenAsync();
        using var __ = fake;
        sharing.LanError = "El puerto 443 ya lo usa otro programa en 192.168.1.20.";
        var sites = await TiendaAsync(main);

        var running = sites.ShareLanCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ShareDialogViewModel);
        ((ShareDialogViewModel)main.Dialog!).ConfirmCommand.Execute(null);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var info = (MessageDialogViewModel)main.Dialog!;
        info.ChooseCommand.Execute(info.Buttons[0]);
        await running;

        Assert.Equal([443], sharing.FirewallAllowed);
        Assert.Equal([443], sharing.FirewallRemoved);
        Assert.False(sites.IsSharing);
    }

    [AvaloniaFact]
    public async Task Compartir_LoAbiertoDesdeLaCli_SaleEnElPanel_YNoSeCierraAlSalir()
    {
        var (main, window, fake, sharing) = await OpenAsync();
        using var _ = fake;
        var sites = await TiendaAsync(main);

        sites.ShowShares([new ShareDto("tienda", "tunnel", TunnelUrl, "127.0.0.1", 49731, DateTimeOffset.Now, Connected: false)]);

        Assert.True(sites.HasSiteTunnel);
        Assert.True(sites.TunnelConnecting);
        Assert.False(sites.HasOwnShares);
        await sites.StopOwnSharesAsync();
        Assert.Empty(sharing.Stops);
    }

    [AvaloniaFact]
    public async Task Compartir_AlSalirDeRoost_DejaDeCompartirLoQueAbrioLaApp_YQuitaLaRegla()
    {
        var (main, _, fake, sharing) = await OpenAsync();
        using var __ = fake;
        var sites = await TiendaAsync(main);
        foreach (var command in new[] { sites.ShareTunnelCommand, sites.ShareLanCommand })
        {
            var running = command.ExecuteAsync(null);
            await UntilAsync(() => main.Dialog is ShareDialogViewModel);
            ((ShareDialogViewModel)main.Dialog!).ConfirmCommand.Execute(null);
            await running;
        }
        Assert.True(sites.HasSiteTunnel && sites.HasSiteLan);

        await sites.StopOwnSharesAsync();

        Assert.Equal([("tienda", "lan"), ("tienda", "tunnel")], sharing.Stops.Order());
        Assert.Equal([443], sharing.FirewallRemoved);
        Assert.False(sites.HasOwnShares);
        Assert.All(sharing.Holds, h => Assert.True(h.IsCancellationRequested));
    }

    [AvaloniaFact]
    public async Task Compartir_ConElStackParado_OfreceIniciarlo()
    {
        var (main, window, fake, _) = await OpenAsync();
        using var __ = fake;
        main.Stack.Show(null);
        var sites = await TiendaAsync(main);

        Assert.True(sites.ShareStackStopped);
        Assert.False(sites.IsSharing);
    }

    // Doble

    /// <summary>El daemon, la red y el cortafuegos de mentira: apunta lo que se le pide.</summary>
    private sealed class FakeSharing : ShareSystem
    {
        private readonly List<ShareDto> _shares = [];
        private readonly Dictionary<(string, string), TaskCompletionSource<string>> _ended = [];

        public NetworkCategory Category { get; init; }
        public IReadOnlyList<int> RulePorts { get; set; } = [];
        public string? TunnelError { get; set; }
        public string? LanError { get; set; }

        public int Lists { get; private set; }
        public List<string> Tunnels { get; } = [];
        public List<(string Site, string Address, int Port)> Lans { get; } = [];
        public List<(string Site, string Kind)> Stops { get; } = [];
        public List<int> FirewallAllowed { get; } = [];
        public List<int> FirewallRemoved { get; } = [];
        public List<CancellationToken> Holds { get; } = [];

        public override Task<IReadOnlyList<ShareDto>> ListAsync(DaemonClient daemon, Roost.Core.Platform.RoostPaths paths, CancellationToken cancellation = default)
        {
            Lists++;
            return Task.FromResult<IReadOnlyList<ShareDto>>([.. _shares]);
        }

        public override Task<ShareResponse> TunnelAsync(DaemonClient daemon, string site)
        {
            Tunnels.Add(site);
            if (TunnelError is { } error) return Task.FromResult(new ShareResponse(false, error, null));
            return Task.FromResult(Add(new ShareDto(site, "tunnel", TunnelUrl, "127.0.0.1", 49731, DateTimeOffset.Now)));
        }

        public override Task<ShareResponse> LanAsync(DaemonClient daemon, string site, string address, int port)
        {
            Lans.Add((site, address, port));
            if (LanError is { } error) return Task.FromResult(new ShareResponse(false, error, null));
            return Task.FromResult(Add(new ShareDto(site, "lan", LanNetwork.Url(address, port), address, port, DateTimeOffset.Now)));
        }

        private ShareResponse Add(ShareDto share)
        {
            _shares.Add(share);
            _ended[(share.Site, share.Kind)] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ShareResponse(true, null, share) { Applied = ["recargado nginx"] };
        }

        public override Task<ShareResponse> StopAsync(DaemonClient daemon, string site, string kind)
        {
            Stops.Add((site, kind));
            _shares.RemoveAll(s => s.Site == site && s.Kind == kind);
            if (_ended.Remove((site, kind), out var ended)) ended.TrySetResult("Se ha dejado de compartir.");
            return Task.FromResult(new ShareResponse(true, null, null));
        }

        public override async Task<ShareResponse> HoldAsync(DaemonClient daemon, string site, string kind, CancellationToken cancellation)
        {
            Holds.Add(cancellation);
            var ended = _ended[(site, kind)];
            return new ShareResponse(true, await ended.Task.WaitAsync(cancellation), null);
        }

        public override IReadOnlyList<LanCandidate> Candidates() =>
        [
            new(IPAddress.Parse("172.29.48.1"), 40, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false),
            new(IPAddress.Parse("192.168.1.20"), 5, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211", NetworkInterfaceType.Wireless80211, true),
        ];

        public override Task<LanProbeResult> ProbeAsync() =>
            Task.FromResult(new LanProbeResult(new Dictionary<int, NetworkCategory> { [5] = Category }, RulePorts));

        public override bool CanBind(IPAddress address, int port) => true;

        public override void AllowFirewall(int port) => FirewallAllowed.Add(port);

        public override void RemoveFirewall(int port) => FirewallRemoved.Add(port);

        public override Roost.Core.Install.InstalledCloudflared? Cloudflared(Roost.Core.Platform.RoostPaths paths) =>
            new("2026.10.3", Path.Combine(paths.CloudflaredBin, "2026.10.3"));
    }
}
