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

        Process.Start(launch)?.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, cancellation);
            if (await TryStatusAsync(cancellation) is { } status) return status;
        }

        throw new RoostException($"El daemon de Roost no responde. Mira {DaemonHost.LogFile(_paths)}.");
    }

    public Task<CommandResponse> UpAsync(CancellationToken cancellation = default) => PostAsync("up", cancellation);

    public Task<CommandResponse> DownAsync(CancellationToken cancellation = default) => PostAsync("down", cancellation);

    /// <summary>Reinicia un servicio o grupo (<c>nginx</c>, <c>php-8.4</c>, <c>mariadb-11.4</c>).</summary>
    public Task<CommandResponse> RestartAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("restart/" + Uri.EscapeDataString(service), cancellation);

    /// <summary>Reinicia todo el stack con el mismo plan: para en orden inverso y arranca en orden, sin regenerar nada.</summary>
    public Task<CommandResponse> RestartAllAsync(CancellationToken cancellation = default) => PostAsync("restart", cancellation);

    /// <summary>Para un servicio o grupo sin tocar el resto; queda parado hasta <see cref="StartServiceAsync"/> o un <c>up</c>.</summary>
    public Task<CommandResponse> StopServiceAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("stop/" + Uri.EscapeDataString(service), cancellation);

    /// <summary>Vuelve a arrancar un servicio o grupo parado (o que dejó de relanzarse).</summary>
    public Task<CommandResponse> StartServiceAsync(string service, CancellationToken cancellation = default) =>
        PostAsync("start/" + Uri.EscapeDataString(service), cancellation);

    private async Task<CommandResponse> PostAsync(string path, CancellationToken cancellation)
    {
        using var response = await _http.PostAsync(path, content: null, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CommandResponse>(cancellation)
               ?? throw new RoostException($"El daemon no devolvio nada en /{path}.");
    }

    public void Dispose() => _http.Dispose();
}
