using System.Text.Json;
using Roost.Core.Platform;

namespace Roost.Desktop.Services;

/// <summary>
/// Preferencias de la app de escritorio, en <c>state/desktop.json</c>. Lo que afecta al stack
/// (puertos, versiones) no va aquí sino en roost.toml, para que la CLI y la app vean lo mismo.
/// </summary>
public sealed record DesktopPrefs
{
    /// <summary>"system", "light" o "dark".</summary>
    public string Theme { get; init; } = "system";

    /// <summary>Cerrar la ventana la oculta en la bandeja; el stack sigue en marcha.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Ya se ha dicho, la primera vez que la X dejó Roost en la bandeja, dónde está y cómo salir.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Al abrir la app, arrancar el stack.</summary>
    public bool StartStackOnLaunch { get; init; }

    /// <summary>Ya se ha visto la bienvenida del primer arranque.</summary>
    public bool WelcomeDone { get; init; }

    /// <summary>Mirar si hay una Roost nueva al abrir la app y mientras sigue abierta (una vez al día: la caché dura 24 h).</summary>
    public bool CheckUpdates { get; init; } = true;

    // Git-app (oleada 4): Ajustes › Git › Novedades.

    /// <summary>Con la app abierta, mirar cada <see cref="GitCheckMinutes"/> si los sitios de git tienen commits nuevos (solo ls-remote; nunca descarga).</summary>
    public bool GitCheckUpdates { get; init; } = true;

    /// <summary>Cada cuántos minutos se mira si hay novedades en los repositorios de los sitios.</summary>
    public int GitCheckMinutes { get; init; } = 30;

    // Ajustes y sistema (oleada 5): los programas preferidos (J3 + C4) y los avisos de la bandeja (J4, K5).

    /// <summary>
    /// El editor con el que se abren las carpetas y los ficheros (<see cref="ShellApp.Id"/>: <c>vscode</c>,
    /// <c>cursor</c>, <c>phpstorm</c>, <c>sublime</c>). Vacío = el primero que haya; <c>asociado</c> = el programa asociado de Windows.
    /// </summary>
    public string Editor { get; init; } = ShellApps.Auto;

    /// <summary>La terminal (<c>wt</c>, <c>pwsh</c>, <c>powershell</c>, <c>gitbash</c>, <c>cmd</c>). Vacío = PowerShell 7 o, si no está, el de Windows.</summary>
    public string Terminal { get; init; } = ShellApps.Auto;

    /// <summary>El navegador, por su ejecutable (<c>chrome.exe</c>, <c>firefox.exe</c>). Vacío = el predeterminado de Windows.</summary>
    public string Browser { get; init; } = ShellApps.Auto;

    /// <summary>Avisar en la bandeja si un servicio del stack se cae (pasa a Failed).</summary>
    public bool NotifyServiceDown { get; init; } = true;

    /// <summary>Avisar en la bandeja si la auditoría pasa a rojo (una vez por cambio).</summary>
    public bool NotifyAuditRed { get; init; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string FilePath(RoostPaths paths) => Path.Combine(paths.State, "desktop.json");

    /// <summary>Las preferencias guardadas; las de por defecto si no hay fichero o no se puede leer.</summary>
    public static DesktopPrefs Load(RoostPaths paths)
    {
        try
        {
            var file = FilePath(paths);
            return File.Exists(file) ? JsonSerializer.Deserialize<DesktopPrefs>(File.ReadAllText(file), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(RoostPaths paths)
    {
        var file = FilePath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }
}
