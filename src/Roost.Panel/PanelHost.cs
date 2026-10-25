using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roost.Core;
using Roost.Core.Platform;
using Roost.WebPanel.Api;

namespace Roost.WebPanel;

/// <summary>
/// El servidor del panel: Kestrel solo en <c>127.0.0.1</c> y <c>[::1]</c>, la interfaz embebida y la API
/// JSON, con las reglas de <see cref="PanelSecurity"/> delante de todo. Lo lanza <c>roost panel up</c>
/// como un proceso aparte (<c>roost panel serve</c>) y apunta en <c>run/panel.json</c> dónde está.
/// </summary>
public static class PanelHost
{
    public static string LogFile(RoostPaths paths) => Path.Combine(paths.Logs, "panel.log");

    private static PosixSignalRegistration? _hangup;

    /// <summary><c>roost panel serve</c>: el proceso del panel, hasta que lo paren.</summary>
    public static async Task<int> RunAsync(int port, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        if (!OperatingSystem.IsWindows())
        {
            // Como el daemon: su propia sesión (cerrar la terminal no lo mata), SIGHUP ignorado y la salida a su log.
            Posix.SetSid();
            _hangup ??= PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
            Directory.CreateDirectory(paths.Logs);
            Posix.RedirectStdioTo(LogFile(paths));
        }

        Directory.CreateDirectory(paths.Logs);
        var log = new PanelLog(LogFile(paths));
        using var app = new PanelApp(paths) { Log = log.Write };
        var token = PanelState.NewToken();
        try
        {
            using var server = await StartAsync(app, port, token, log.Write, cancellation);
            var info = new PanelInfo(Environment.ProcessId, server.Port, token, DateTimeOffset.UtcNow);
            PanelState.Write(paths, info);
            // Nunca el token en el log.
            log.Write($"panel en marcha (pid {info.Pid}, {info.PlainUrl}, raiz {paths.Root})");
            try
            {
                await server.App.WaitForShutdownAsync(cancellation);
            }
            finally
            {
                // Solo si el fichero sigue siendo el nuestro (otro panel pudo arrancar tras un kill -9).
                if (PanelState.Read(paths) is { } current && current.Pid == info.Pid) PanelState.Delete(paths);
                log.Write("panel parado");
            }
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Write("el panel no pudo arrancar: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Un servidor en marcha (para <c>serve</c> y para los tests, que piden el puerto 0).</summary>
    public sealed class Server(WebApplication app, int port) : IDisposable
    {
        public WebApplication App { get; } = app;
        public int Port { get; } = port;
        public string BaseUrl => $"http://127.0.0.1:{Port}/";

        public void Dispose()
        {
            try { App.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException) { }
            App.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Arranca Kestrel en loopback (IPv4 y, si la máquina lo tiene, IPv6) con el <paramref name="token"/>
    /// dado. Con <paramref name="port"/> 0 elige uno libre (los tests). Nunca <c>0.0.0.0</c>.
    /// </summary>
    public static async Task<Server> StartAsync(PanelApp app, int port, string token, Action<string> log, CancellationToken cancellation = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = PanelApp.JsonOptions.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = PanelApp.JsonOptions.DefaultIgnoreCondition;
            foreach (var converter in PanelApp.JsonOptions.Converters) o.SerializerOptions.Converters.Add(converter);
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Solo loopback: 127.0.0.1 y, si la máquina tiene IPv6, [::1] en el mismo puerto. Nunca 0.0.0.0.
            kestrel.Listen(IPAddress.Loopback, port);
            if (port != 0 && Loopback.HasIpv6) kestrel.Listen(IPAddress.IPv6Loopback, port);
        });

        var web = builder.Build();
        // El puerto real se sabe al arrancar; la comprobación de Host lo necesita, así que se lee tarde.
        var state = new ServerState(token);

        web.Use(async (context, next) =>
        {
            // 1. Host: solo el propio (contra el DNS rebinding). Antes que nada, también antes de la cookie.
            if (!PanelSecurity.IsAllowedHost(context.Request.Host.Value, state.Port))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.Headers.Connection = "close";
                await context.Response.WriteAsync("Host no admitido.");
                return;
            }

            var isApi = context.Request.Path.StartsWithSegments("/api");
            PanelSecurity.AddHeaders(context.Response.Headers, isApi);

            // 2. El token de la URL se cambia por la cookie y se redirige sin él (que no quede en el historial).
            if (context.Request.Path == "/" && context.Request.Query.TryGetValue(PanelSecurity.TokenQuery, out var presented))
            {
                if (!PanelSecurity.TokenMatches(presented.ToString(), state.Token))
                {
                    await Unauthorized(context);
                    return;
                }
                context.Response.Cookies.Append(PanelSecurity.CookieName, state.Token, PanelSecurity.CookieOptions());
                context.Response.Redirect("/", permanent: false);
                return;
            }

            // 3. Sin la cookie, nada: ni la interfaz ni la API.
            context.Request.Cookies.TryGetValue(PanelSecurity.CookieName, out var cookie);
            if (!PanelSecurity.TokenMatches(cookie, state.Token))
            {
                await Unauthorized(context);
                return;
            }

            // 4. Lo que escribe tiene que venir de la propia página (Origin + cabecera anti-CSRF).
            if (!PanelSecurity.IsSameOriginWrite(context.Request.Method, context.Request.Host.Value,
                    context.Request.Headers.Origin, context.Request.Headers[PanelSecurity.CsrfHeader]))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "La petición no viene del panel (Origin o cabecera anti-CSRF)." });
                return;
            }

            await next(context);
        });

        PanelRoutes.Map(web, app);
        MapStatic(web);

        await web.StartAsync(cancellation);
        var address = web.Urls.FirstOrDefault(u => u.Contains("127.0.0.1", StringComparison.Ordinal)) ?? web.Urls.FirstOrDefault()
                      ?? throw new RoostException("Kestrel no dice en que puerto escucha.");
        var actual = new Uri(address).Port;
        state.Port = actual;
        log($"escuchando en 127.0.0.1:{actual}{(port != 0 && Loopback.HasIpv6 ? $" y [::1]:{actual}" : "")}");
        return new Server(web, actual);
    }

    private sealed class ServerState(string token)
    {
        public string Token { get; } = token;
        public int Port { get; set; }
    }

    private static Task Unauthorized(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (context.Request.Path.StartsWithSegments("/api"))
            return context.Response.WriteAsJsonAsync(new { error = "Sesión no válida: abre el panel con la URL que da roost panel url." });
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(
            "<!doctype html><meta charset=\"utf-8\"><title>Roost</title>" +
            "<body style=\"font-family:system-ui;background:#15181d;color:#e6eaef;padding:40px\">" +
            "<h1 style=\"font-size:20px\">Roost</h1><p>Esta sesión no vale. Abre el panel con la dirección que te da <code>roost panel url</code> " +
            "(lleva un código de un solo uso por arranque).</p></body>");
    }

    // La interfaz: los ficheros de wwwroot embebidos en este ensamblado.

    private static readonly Lazy<Dictionary<string, string>> Resources = new(() =>
    {
        var assembly = typeof(PanelHost).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("wwwroot/", StringComparison.Ordinal) && !name.StartsWith("wwwroot\\", StringComparison.Ordinal)) continue;
            var path = name["wwwroot/".Length..].Replace('\\', '/');
            map[path] = name;
        }
        return map;
    });

    /// <summary>Los ficheros embebidos de la interfaz (rutas relativas con <c>/</c>).</summary>
    public static IReadOnlyCollection<string> StaticFiles => Resources.Value.Keys;

    public static Stream? OpenStatic(string path) =>
        Resources.Value.TryGetValue(path, out var name) ? typeof(PanelHost).Assembly.GetManifestResourceStream(name) : null;

    private static void MapStatic(WebApplication web)
    {
        web.MapGet("/", () => Static("index.html"));
        web.MapGet("/{**path}", (string path) => path.StartsWith("api/", StringComparison.Ordinal) ? Results.NotFound() : Static(path));
    }

    private static IResult Static(string path)
    {
        var stream = OpenStatic(path);
        if (stream is null) return Results.NotFound();
        return Results.Stream(stream, ContentType(path));
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };
}

/// <summary>El log del panel: una línea por evento, con hora. Nunca el token.</summary>
internal sealed class PanelLog(string path)
{
    private readonly object _lock = new();

    public void Write(string message)
    {
        lock (_lock)
        {
            try { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
            catch { /* el log no tumba el panel */ }
        }
    }
}
