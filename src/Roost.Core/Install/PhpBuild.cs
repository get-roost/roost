using Roost.Core.Models;

namespace Roost.Core.Install;

/// <summary>
/// Un zip de PHP que se puede descargar de un catálogo (hoy, el releases.json de
/// windows.php.net): versión, variante (TS/NTS, compilador, arquitectura), URL y el
/// SHA-256 que tiene que dar el fichero descargado.
/// </summary>
public sealed record PhpBuild
{
    public required ComponentVersion Version { get; init; }

    /// <summary>Rama tal como la agrupa el catálogo: "8.3".</summary>
    public required string Branch { get; init; }

    /// <summary>true = TS (thread-safe, para Apache/mod_php); false = NTS (para nginx/FastCGI).</summary>
    public required bool ThreadSafe { get; init; }

    /// <summary>Compilador del build (vc15, vs16, vs17): decide qué VC++ Redistributable hace falta.</summary>
    public required string Compiler { get; init; }

    /// <summary>"x64" o "x86".</summary>
    public required string Architecture { get; init; }

    /// <summary>Nombre del zip en el catálogo: <c>php-8.3.36-nts-Win32-vs16-x64.zip</c>.</summary>
    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    /// <summary>SHA-256 en hexadecimal y minúsculas.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Tamaño tal como lo escribe el catálogo ("32.52MB"); solo informativo.</summary>
    public string? SizeText { get; init; }

    /// <summary>Carpeta de instalación: <c>8.3.36-nts-x64</c> (ROOST.md §5.4).</summary>
    public string InstallName => $"{Version}-{(ThreadSafe ? "ts" : "nts")}-{Architecture}";
}
