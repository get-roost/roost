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
