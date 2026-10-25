using Roost.Core.Platform;
using Roost.Daemon;
using Roost.Extensibility;

namespace Roost.Desktop.Services;

/// <summary>
/// Lo que comparten todas las pantallas: rutas de Roost, el único HttpClient, el cliente del
/// daemon y las preferencias. Se crea una vez al arrancar la app.
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <param name="http">Para los tests: un HttpClient con un handler falso (sin red).</param>
    /// <param name="extensions">Para los tests: extensiones ya hechas; si no, las de bin/extensions.</param>
    /// <param name="version">Para los tests: la versión instalada que se compara con la última publicada.</param>
    public AppServices(RoostPaths? paths = null, HttpClient? http = null, ExtensionSet? extensions = null, string? version = null)
    {
        Paths = paths ?? RoostPaths.Default();
        Http = http ?? CreateHttp();
        Extensions = extensions ?? ExtensionSet.Load(Paths);
        Daemon = new DaemonClient(Paths);
        Prefs = DesktopPrefs.Load(Paths);
        Version = version ?? Roost.Core.Updates.RoostVersion.Current;
        // Ajustes y sistema (J3 + C4): Shell abre con el editor, la terminal y el navegador elegidos.
        Shell.Preferences = () => Prefs;
        // E4: la tarea al iniciar sesión. Con unas rutas dadas (los tests) nunca se pregunta al
        // Programador de tareas de verdad: «no está», salvo que el test ponga la suya.
        LogonTask = new LogonTask(paths is null ? Roost.Core.Scanning.CommandRunner.Default : NoTasks.Instance);
    }

    /// <summary>E4: la tarea que arranca el stack al iniciar sesión, sin la app (Ajustes › General).</summary>
    public LogonTask LogonTask { get; internal set; }

    /// <summary>Un schtasks que no está: todo sale con 1 («no existe») sin ejecutar nada.</summary>
    private sealed class NoTasks : Roost.Core.Scanning.ICommandRunner
    {
        public static readonly NoTasks Instance = new();

        public Task<Roost.Core.Scanning.CommandResult> RunAsync(Roost.Core.Scanning.CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation) =>
            Task.FromResult(new Roost.Core.Scanning.CommandResult(1, "", "", false));
    }

    public RoostPaths Paths { get; }

    /// <summary>La versión de Roost que se está ejecutando (<c>0.1.0</c>).</summary>
    public string Version { get; }

    public DaemonClient Daemon { get; }

    public DesktopPrefs Prefs { get; private set; }

    /// <summary>Las extensiones cargadas (ROOST.md §12.4); una que no carga sale en Ajustes, no rompe la app.</summary>
    public ExtensionSet Extensions { get; }

    /// <summary>
    /// Para catálogos y descargas. El timeout cubre hasta las cabeceras (los instaladores leen
    /// el cuerpo aparte), así que un zip grande con una conexión lenta no se corta.
    /// </summary>
    public HttpClient Http { get; }

    /// <summary>La carpeta de la app: a su lado van roost.exe (el daemon) y roost-helper.exe.</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>El helper que pide permiso: el de al lado de la app o, en Linux, el del paquete (<c>/usr/libexec/roost</c>, con política de polkit).</summary>
    public static string HelperExe => HelperLocator.Find(AppDirectory);

    /// <summary>
    /// Cómo pide permiso la app: en Windows el UAC; en Linux pkexec sin su agente de texto y <c>sudo -n</c>. La
    /// app nunca tiene terminal en la que pkexec o sudo puedan preguntar (lanzada desde una consola, pkexec
    /// preguntaría en ella y se quedaría esperando): si no hay agente de polkit ni sudo sin contraseña,
    /// Core lanza <see cref="ElevationNeededException"/> con la orden exacta y la app ofrece «Copiar».
    /// </summary>
    public static Elevation Elevation { get; } = new(hasTerminal: () => false);

    public void SavePrefs(DesktopPrefs prefs)
    {
        Prefs = prefs;
        try
        {
            prefs.Save(Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unas preferencias que no se guardan no impiden usar la app.
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("roost-desktop/" + Roost.Core.Updates.RoostVersion.Current);
        return http;
    }

    public void Dispose()
    {
        Daemon.Dispose();
        Http.Dispose();
    }
}
