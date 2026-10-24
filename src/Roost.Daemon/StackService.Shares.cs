using System.Net;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Share;
using Roost.Core.Sites;

namespace Roost.Daemon;

/// <summary>
/// Compartir (oleada 6, N3 y L1): un sitio por internet con un túnel rápido de Cloudflare, o en la
/// red local. Lo lleva el daemon, no la app ni la CLI, para que <c>roost down</c> (y la salida del
/// daemon, por el Job Object) lo pare todo, y para que la app vea lo que se compartió desde la CLI.
/// <list type="bullet">
/// <item>Cada cosa compartida es una escucha más del servidor web (<see cref="ShareListener"/>), que se
/// abre y se cierra con un <c>up</c> con el cerrojo cogido: valida antes de tocar nada y, si solo
/// cambia eso, solo recarga el servidor web (E2), sin cortar a nadie.</item>
/// <item>Nada se guarda en disco: un daemon nuevo empieza sin compartir nada.</item>
/// <item>Quien lo pide puede «sujetarlo» (<see cref="HoldShareAsync"/>): si se va (Ctrl+C, la terminal
/// cerrada, la app que sale), se deja de compartir solo.</item>
/// </list>
/// </summary>
internal sealed partial class StackService
{
    private readonly object _sharesLock = new();

    /// <summary>Lo compartido que está en marcha, por sitio y tipo. Se cambia con el cerrojo; se lee con <see cref="_sharesLock"/>.</summary>
    private readonly Dictionary<(string Site, ShareKind Kind), ActiveShare> _shares = [];

    /// <summary>La escucha que se está abriendo ahora mismo (aún sin URL): va en el plan, pero no en <see cref="Shares"/>.</summary>
    private ShareListener? _opening;

    private sealed class ActiveShare(ShareListener listener, string url, TunnelSession? tunnel)
    {
        public ShareListener Listener { get; } = listener;
        public string Url { get; } = url;
        public TunnelSession? Tunnel { get; } = tunnel;
        public DateTimeOffset Since { get; } = DateTimeOffset.Now;

        /// <summary>Se completa al dejar de compartir, con el motivo en palabras.</summary>
        public TaskCompletionSource<string> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ShareDto Dto => new(Listener.Site, ShareListener.KindKey(Listener.Kind), Url, Listener.Address, Listener.Port, Since, Tunnel?.Connected ?? true);
    }

    /// <summary><c>GET /shares</c>: lo que se está compartiendo ahora.</summary>
    public SharesResponse Shares()
    {
        lock (_sharesLock)
            return new SharesResponse(_shares.Values.OrderBy(s => s.Listener.Site, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Listener.Kind).Select(s => s.Dto).ToList());
    }

    /// <summary>
    /// Las escuchas que lleva el plan de un <c>up</c>: las de lo compartido y la que se está abriendo.
    /// Antes, lo compartido en una IP que ya no es de este equipo (otra wifi) deja de compartirse: si
    /// no, el <c>-t</c> del servidor web no podría abrirla y ningún <c>up</c> se aplicaría.
    /// </summary>
    private IReadOnlyList<ShareListener> ShareListenersLocked()
    {
        lock (_sharesLock)
        {
            foreach (var lost in _shares.Values.Where(s => s.Listener.Kind == ShareKind.Lan
                                                         && !(IPAddress.TryParse(s.Listener.Address, out var ip) && _env.Shares.IsLocalAddress(ip))).ToList())
            {
                _shares.Remove((Key(lost.Listener.Site), lost.Listener.Kind));
                new LocalCa(paths).RemoveLanCert(lost.Listener.Site);
                lost.Ended.TrySetResult($"La IP {lost.Listener.Address} ya no es de este equipo (has cambiado de red): se ha dejado de compartir en la red local.");
                log($"compartir {lost.Listener.Site}: la IP {lost.Listener.Address} ya no es de este equipo; se deja de compartir en la red local");
            }

            var listeners = _shares.Values.Select(s => s.Listener).ToList();
            if (_opening is { } opening) listeners.Add(opening);
            return listeners;
        }
    }

    /// <summary>
    /// <c>POST /share/{sitio}/tunnel</c>: abre la escucha del túnel en un puerto libre de 127.0.0.1
    /// (con un <c>up</c>: solo recarga el servidor web), lanza cloudflared hacia ella y espera a su URL
    /// (y, un poco más, a su primera conexión). Si ya se compartía, devuelve lo que hay.
    /// </summary>
    public async Task<ShareResponse> ShareTunnelAsync(string site)
    {
        await _gate.WaitAsync();
        try
        {
            if (Precheck(site) is { } problem) return Fail(problem);
            if (Find(site, ShareKind.Tunnel) is { } existing) return new ShareResponse(true, null, existing.Dto);
            if (_env.Shares.CloudflaredExe(paths) is not { } cloudflared)
                return Fail("cloudflared no esta instalado. Instalalo con: roost share install (roost share lo hace solo la primera vez).");

            var listener = new ShareListener(site, ShareKind.Tunnel, IPAddress.Loopback.ToString(), _env.Shares.FreeLoopbackPort());
            var (opened, applied) = await OpenListenerLocked(listener);
            if (opened is not null) return Fail(opened);

            var spec = new CommandSpec(
                cloudflared,
                QuickTunnel.Arguments(listener.Port, QuickTunnel.EnsureConfigFile(paths)),
                WorkingDirectory: Path.GetDirectoryName(cloudflared),
                Environment: QuickTunnel.Environment());
            var tunnel = TunnelSession.Start(_env.Shares.Commands, spec, QuickTunnel.LogFile(paths, site), site);
            var url = await tunnel.WaitForUrlAsync(_env.Shares.UrlTimeout);
            if (url is null)
            {
                await tunnel.StopAsync();
                var why = tunnel.Failure;
                await CloseListenerLocked(listener);
                log($"compartir {site}: el tunel no ha llegado a abrirse ({why})");
                return Fail(why + $" (detalle en {QuickTunnel.LogFile(paths, site)})");
            }
            await tunnel.WaitConnectedAsync(_env.Shares.ConnectTimeout);

            var share = Activate(new ActiveShare(listener, url, tunnel));
            _ = WatchTunnelAsync(share);
            log($"compartir {site}: por internet en {url} (cloudflared hacia 127.0.0.1:{listener.Port})");
            return new ShareResponse(true, null, share.Dto) { Applied = applied };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>POST /share/{sitio}/lan</c>: abre la escucha HTTPS del sitio en <paramref name="address"/>
    /// (una IPv4 privada de este equipo; nunca 0.0.0.0) y <paramref name="port"/>, con su certificado
    /// con la IP. La regla del cortafuegos la pone antes el cliente (UAC), que es quien está delante.
    /// </summary>
    public async Task<ShareResponse> ShareLanAsync(string site, string address, int port)
    {
        await _gate.WaitAsync();
        try
        {
            if (Precheck(site) is { } problem) return Fail(problem);
            if (!IPAddress.TryParse(address, out var ip) || !LanNetwork.IsPrivate(ip))
                return Fail($"{address} no es una direccion de red local (10.x, 172.16-31.x o 192.168.x).");
            if (!LanFirewall.IsAllowedPort(port))
                return Fail($"El puerto {port} no vale para compartir en la red local.");
            if (Find(site, ShareKind.Lan) is { } existing)
            {
                return existing.Listener.Address == ip.ToString() && existing.Listener.Port == port
                    ? new ShareResponse(true, null, existing.Dto)
                    : Fail($"{site} ya se comparte en la red local en {existing.Url}. Para cambiar de IP o de puerto, deja antes de compartirlo.");
            }
            if (!_env.Shares.IsLocalAddress(ip))
                return Fail($"{ip} no es una direccion de este equipo (puede que hayas cambiado de red).");
            lock (_sharesLock)
            {
                if (_shares.Values.FirstOrDefault(s => s.Listener.Address == ip.ToString() && s.Listener.Port == port) is { } taken)
                    return Fail($"{ip}:{port} ya lo usa {taken.Listener.Site}.");
            }
            if (!_env.Shares.CanBind(ip, port))
                return Fail($"El puerto {port} ya lo usa otro programa en {ip}.");

            var listener = new ShareListener(site, ShareKind.Lan, ip.ToString(), port);
            var (opened, applied) = await OpenListenerLocked(listener);
            if (opened is not null) return Fail(opened);

            var share = Activate(new ActiveShare(listener, LanNetwork.Url(listener.Address, port), null));
            log($"compartir {site}: en la red local en {share.Url}");
            return new ShareResponse(true, null, share.Dto) { Applied = applied };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>POST /share/{sitio}/{tipo}/stop</c>: deja de compartir (para cloudflared y recarga el servidor web sin la escucha).</summary>
    public async Task<ShareResponse> StopShareAsync(string site, ShareKind kind)
    {
        await _gate.WaitAsync();
        try
        {
            if (Find(site, kind) is not { } share)
                return Fail(kind == ShareKind.Tunnel ? $"{site} no se esta compartiendo por internet." : $"{site} no se esta compartiendo en la red local.");
            var applied = await EndShareLocked(share, "Se ha dejado de compartir.", reload: true);
            log($"compartir {site}: {(kind == ShareKind.Tunnel ? "tunel cerrado" : "red local cerrada")}");
            return new ShareResponse(true, null, share.Dto) { Applied = applied };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>POST /share/{sitio}/{tipo}/hold</c>: contesta cuando se deja de compartir (con el motivo). Si
    /// quien espera se va antes (<paramref name="aborted"/>: un Ctrl+C, la terminal cerrada, la app que
    /// sale), se deja de compartir: nada se queda público porque alguien se olvidó de pararlo.
    /// </summary>
    public async Task<ShareResponse> HoldShareAsync(string site, ShareKind kind, CancellationToken aborted)
    {
        ActiveShare? share;
        lock (_sharesLock) share = _shares.GetValueOrDefault((Key(site), kind));
        if (share is null) return Fail($"{site} no se esta compartiendo.");

        try
        {
            var reason = await share.Ended.Task.WaitAsync(aborted);
            return new ShareResponse(true, reason, share.Dto);
        }
        catch (OperationCanceledException)
        {
            await _gate.WaitAsync();
            try
            {
                // Solo si sigue siendo lo mismo: otro cliente puede haberlo parado y vuelto a abrir.
                if (Find(site, kind) == share)
                {
                    log($"compartir {site}: quien lo pidio se ha ido (Ctrl+C, terminal cerrada o la app); se deja de compartir");
                    await EndShareLocked(share, "Quien lo pidio ya no esta: se ha dejado de compartir.", reload: true);
                }
            }
            finally
            {
                _gate.Release();
            }
            return new ShareResponse(true, "Se ha dejado de compartir.", share.Dto);
        }
    }

    /// <summary>Lo de un sitio que ya no está en el stack (o todo, si el stack no está en marcha) deja de compartirse. Con el cerrojo.</summary>
    private async Task PruneSharesLockedAsync()
    {
        List<ActiveShare> gone;
        lock (_sharesLock)
        {
            gone = _shares.Values.Where(s => _supervisor is null || _plan is not { } plan
                                             || !plan.Sites.Any(p => string.Equals(p.Site.Name, s.Listener.Site, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        foreach (var share in gone)
        {
            log($"compartir {share.Listener.Site}: el sitio ya no esta en el stack; se deja de compartir");
            // Prepare ya no la ha escrito: no hace falta recargar nada.
            await EndShareLocked(share, $"{share.Listener.Site} ya no esta en el stack: se ha dejado de compartir.", reload: false);
        }
    }

    /// <summary>Al parar el stack: todo lo compartido se cierra sin recargar nada (el servidor web se va a parar).</summary>
    private async Task StopAllSharesLockedAsync(string reason)
    {
        List<ActiveShare> all;
        lock (_sharesLock) all = [.. _shares.Values];
        foreach (var share in all) await EndShareLocked(share, reason, reload: false);
        if (all.Count > 0) log($"compartir: cerrado todo ({all.Count}) al parar el stack");
    }

    /// <summary>Por qué no se puede compartir ahora ese sitio, o null.</summary>
    private string? Precheck(string site)
    {
        if (_supervisor is null || _plan is not { } plan) return "El stack no esta en marcha: arrancalo con roost up y vuelve a compartir.";
        if (!plan.Sites.Any(s => string.Equals(s.Site.Name, site, StringComparison.OrdinalIgnoreCase)))
            return $"El sitio {site} no esta en el stack en marcha (roost site list).";
        return null;
    }

    /// <summary>Abre una escucha con un <c>up</c> (validado antes; si solo cambia eso, solo se recarga el servidor web). null si ha ido bien, o por qué no.</summary>
    private async Task<(string? Problem, IReadOnlyList<string> Applied)> OpenListenerLocked(ShareListener listener)
    {
        lock (_sharesLock) _opening = listener;
        CommandResponse applied;
        try
        {
            applied = await UpLockedAsync();
        }
        finally
        {
            lock (_sharesLock) _opening = null;
        }
        if (applied.Ok && _plan?.Shares.Contains(listener) == true) return (null, applied.Applied);

        // Rechazado: el up ya ha devuelto los ficheros y lo de antes sigue en marcha. Si se aplicó a
        // medias (o el sitio ya no estaba), otro up sin ella la quita ya.
        if (_supervisor is not null && (applied.Ok || applied.Applied.Count > 0)) await UpLockedAsync();
        return ($"No se ha podido abrir la escucha para compartir: {applied.Error ?? "el sitio ya no esta en el stack"}", applied.Applied);
    }

    /// <summary>Quita una escucha que no llegó a compartirse (con otro <c>up</c>, que solo recarga el servidor web).</summary>
    private async Task CloseListenerLocked(ShareListener listener)
    {
        var response = await UpLockedAsync();
        if (!response.Ok) log($"compartir {listener.Site}: no se pudo quitar la escucha ({response.Error}); la quitara el siguiente up");
    }

    private ActiveShare Activate(ActiveShare share)
    {
        lock (_sharesLock) _shares[(Key(share.Listener.Site), share.Listener.Kind)] = share;
        return share;
    }

    /// <summary>Deja de compartir: para cloudflared, quita la escucha (recargando si se pide y el stack sigue) y avisa a quien espera.</summary>
    private async Task<IReadOnlyList<string>> EndShareLocked(ActiveShare share, string reason, bool reload)
    {
        lock (_sharesLock) _shares.Remove((Key(share.Listener.Site), share.Listener.Kind));
        if (share.Tunnel is { } tunnel) await tunnel.StopAsync();

        IReadOnlyList<string> applied = [];
        if (reload && _supervisor is not null)
        {
            var response = await UpLockedAsync();
            applied = response.Applied;
            if (!response.Ok) log($"compartir {share.Listener.Site}: no se pudo recargar sin la escucha ({response.Error}); la quitara el siguiente up");
        }
        if (share.Listener.Kind == ShareKind.Lan) new LocalCa(paths).RemoveLanCert(share.Listener.Site);
        share.Ended.TrySetResult(reason);
        return applied;
    }

    /// <summary>Si cloudflared se cierra solo (sin red, Cloudflare lo corta), se deja de compartir y se dice por qué.</summary>
    private async Task WatchTunnelAsync(ActiveShare share)
    {
        await share.Tunnel!.Exited;
        if (share.Tunnel.Stopping) return;

        await _gate.WaitAsync();
        try
        {
            if (Find(share.Listener.Site, ShareKind.Tunnel) != share) return;
            var why = share.Tunnel.ClosedReason;
            log($"compartir {share.Listener.Site}: cloudflared se ha cerrado solo ({why})");
            await EndShareLocked(share, $"El tunel se ha cerrado: {why}", reload: true);
        }
        catch (Exception ex)
        {
            log($"compartir {share.Listener.Site}: error al cerrar el tunel caido ({ex.Message})");
        }
        finally
        {
            _gate.Release();
        }
    }

    private ActiveShare? Find(string site, ShareKind kind)
    {
        lock (_sharesLock) return _shares.GetValueOrDefault((Key(site), kind));
    }

    private static string Key(string site) => site.ToLowerInvariant();

    private ShareResponse Fail(string error) => new(false, error, null);
}
