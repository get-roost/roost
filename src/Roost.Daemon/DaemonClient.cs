using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.Sockets;
using Roost.Core;
using Roost.Core.Platform;

namespace Roost.Daemon;

/// <summary>
/// Cliente de la API del daemon para la CLI. <see cref="EnsureRunningAsync"/> lo arranca si no
/// responde. Un daemon que no está en marcha no es un error: <see cref="TryStatusAsync"/>
/// devuelve null.
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private readonly RoostPaths _paths;
    private readonly HttpClient _http;

    public DaemonClient(RoostPaths paths)
    {
        _paths = paths;
        var handler = new SocketsHttpHandler { ConnectCallback = (_, cancellation) => ConnectAsync(paths, cancellation) };
        // up puede tardar: valida nginx y espera a que todo acepte conexiones.
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://roost/"), Timeout = TimeSpan.FromSeconds(90) };
    }

    private static async ValueTask<Stream> ConnectAsync(RoostPaths paths, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            // CurrentUserOnly: solo se habla con una pipe cuyo dueño es este mismo usuario.
            var pipe = new NamedPipeClientStream(
                ".", DaemonHost.PipeName(paths), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(1000, cancellation);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(DaemonHost.SocketPath(paths)), cancellation);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Estado del daemon, o null si no está en marcha.</summary>
    public async Task<StatusResponse?> TryStatusAsync(CancellationToken cancellation = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<StatusResponse>("status", cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException
                                   && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Devuelve el estado del daemon, arrancándolo con <paramref name="launch"/> si no responde.</summary>
    public async Task<StatusResponse> EnsureRunningAsync(ProcessStartInfo launch, CancellationToken cancellation = default)
    {
        if (await TryStatusAsync(cancellation) is { } running) return running;

        var process = Process.Start(launch);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, cancellation);
                if (await TryStatusAsync(cancellation) is { } status) return status;
                // El lanzado se ha ido sin llegar a contestar: pasa con un `roost up` justo tras un
                // `roost down`, cuando el daemon anterior aún no ha soltado el mutex. Se lanza otro.
                if (process is { HasExited: true })
                {
                    process.Dispose();
                    process = Process.Start(launch);
                }
            }
        }
        finally
        {
            process?.Dispose();
        }

        throw new RoostException($"El daemon de Roost no responde. Mira {DaemonHost.LogFile(_paths)}.");
    }

    public Task<CommandResponse> UpAsync(CancellationToken cancellation = default) => PostAsync("up", cancellation);

    public Task<CommandResponse> DownAsync(CancellationToken cancellation = default) => PostAsync("down", cancellation);

    /// <summary>Reinicia un servicio o grupo (<c>nginx</c>, <c>php-8.4</c>, <c>mariadb-11.4</c>).</summary>
    public Task<CommandResponse> RestartAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("restart/" + Uri.EscapeDataString(service), cancellation);

    /// <summary>
    /// Recarga un servicio sin cortar: el servidor web (<c>nginx</c>, <c>apache</c>) relee su
    /// configuración en caliente, después de validarla; un pool de PHP se reinicia uno a uno.
    /// </summary>
    public Task<CommandResponse> ReloadAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("reload/" + Uri.EscapeDataString(service), cancellation);

    /// <summary>Reinicia todo el stack con el mismo plan: para en orden inverso y arranca en orden, sin regenerar nada.</summary>
    public Task<CommandResponse> RestartAllAsync(CancellationToken cancellation = default) => PostAsync("restart", cancellation);

    /// <summary>Para un servicio o grupo sin tocar el resto; queda parado hasta <see cref="StartServiceAsync"/> o un <c>up</c>.</summary>
    public Task<CommandResponse> StopServiceAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("stop/" + Uri.EscapeDataString(service), cancellation);

    /// <summary>Vuelve a arrancar un servicio o grupo parado (o que dejó de relanzarse).</summary>
    public Task<CommandResponse> StartServiceAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("start/" + Uri.EscapeDataString(service), cancellation);

    // Compartir (oleada 6)

    /// <summary>Lo que se está compartiendo ahora (<c>GET /shares</c>).</summary>
    public async Task<SharesResponse> SharesAsync(CancellationToken cancellation = default) =>
        await _http.GetFromJsonAsync<SharesResponse>("shares", cancellation)
        ?? throw new RoostException("El daemon no devolvio nada en /shares.");

    /// <summary>Comparte un sitio por internet con un túnel rápido: espera a la URL (hasta medio minuto).</summary>
    public Task<ShareResponse> ShareTunnelAsync(string site, CancellationToken cancellation = default) =>
        PostShareAsync($"share/{Uri.EscapeDataString(site)}/tunnel", null, cancellation);

    /// <summary>Comparte un sitio en la red local en esa IP y ese puerto (la regla del cortafuegos va antes, aparte).</summary>
    public Task<ShareResponse> ShareLanAsync(string site, string address, int port, CancellationToken cancellation = default) =>
        PostShareAsync($"share/{Uri.EscapeDataString(site)}/lan", new ShareLanRequest(address, port), cancellation);

    /// <summary>Deja de compartir (<paramref name="kind"/>: «tunnel» o «lan»).</summary>
    public Task<ShareResponse> StopShareAsync(string site, string kind, CancellationToken cancellation = default) =>
        PostShareAsync($"share/{Uri.EscapeDataString(site)}/{Uri.EscapeDataString(kind)}/stop", null, cancellation);

    /// <summary>
    /// Espera, sin límite de tiempo, a que se deje de compartir, y devuelve el motivo. Si se cancela
    /// (o este proceso termina), el daemon deja de compartirlo: así nada se queda público porque
    /// quien lo abrió ya no está.
    /// </summary>
    public async Task<ShareResponse> HoldShareAsync(string site, string kind, CancellationToken cancellation = default)
    {
        using var response = await HoldClient.PostAsync($"share/{Uri.EscapeDataString(site)}/{Uri.EscapeDataString(kind)}/hold", content: null, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShareResponse>(cancellation)
               ?? throw new RoostException("El daemon no devolvio nada al dejar de compartir.");
    }

    private HttpClient? _hold;

    /// <summary>Otro cliente para las esperas largas: el normal corta a los 90 s.</summary>
    private HttpClient HoldClient
    {
        get
        {
            lock (_http)
            {
                return _hold ??= new HttpClient(new SocketsHttpHandler { ConnectCallback = (_, cancellation) => ConnectAsync(_paths, cancellation) })
                {
                    BaseAddress = new Uri("http://roost/"),
                    Timeout = Timeout.InfiniteTimeSpan,
                };
            }
        }
    }

    private async Task<ShareResponse> PostShareAsync(string path, ShareLanRequest? body, CancellationToken cancellation)
    {
        using var response = body is null
            ? await _http.PostAsync(path, content: null, cancellation)
            : await _http.PostAsJsonAsync(path, body, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShareResponse>(cancellation)
               ?? throw new RoostException($"El daemon no devolvio nada en /{path}.");
    }

    private async Task<CommandResponse> PostAsync(string path, CancellationToken cancellation)
    {
        using var response = await _http.PostAsync(path, content: null, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CommandResponse>(cancellation)
               ?? throw new RoostException($"El daemon no devolvio nada en /{path}.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _hold?.Dispose();
    }
}
