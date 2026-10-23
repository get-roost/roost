namespace Roost.Core.Models;

/// <summary>
/// Una instalación concreta de un componente encontrada en la máquina.
/// Inmutable: cada scanner los produce y el motor los agrega y deduplica.
/// </summary>
public sealed record DetectedComponent
{
    public required ComponentKind Kind { get; init; }

    public required ComponentVersion Version { get; init; }

    /// <summary>Ruta absoluta del ejecutable (php.exe / php / mysqld / mariadbd…).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>De dónde salió: "PATH", "Laragon", "XAMPP", "WAMP", "scoop", "system", "Homebrew"…</summary>
    public required string Source { get; init; }

    /// <summary>SAPI de PHP (cli, fpm, cgi) si aplica; null para el resto.</summary>
    public string? Sapi { get; init; }

    /// <summary>true si este ejecutable es el que resuelve el PATH (el "activo").</summary>
    public bool IsOnPath { get; init; }

    /// <summary>Arquitectura si se conoce: "x64", "x86", "arm64".</summary>
    public string? Architecture { get; init; }

    /// <summary>Datos extra libres por scanner, para no romper el modelo por un detalle.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>();
}
