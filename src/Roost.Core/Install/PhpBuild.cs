using Roost.Core.Models;

namespace Roost.Core.Install;

/// <summary>El sistema para el que está compilado un <see cref="PhpBuild"/>.</summary>
public enum PhpOs
{
    /// <summary>Los zips oficiales de windows.php.net (<c>php.exe</c> en la raíz del zip).</summary>
    Windows,

    /// <summary>Los tar.gz de get-roost/roost-builds (static-php-cli, glibc 2.17; <c>bin/php</c>, <c>bin/php-fpm</c>, <c>ext/xdebug.so</c>).</summary>
    Linux,
}

/// <summary>
/// Un paquete de PHP que se puede descargar de un catálogo: en Windows, un zip del releases.json
/// de windows.php.net; en Linux, un tar.gz de las releases de get-roost/roost-builds
/// (<see cref="LinuxPhpCatalog"/>). Lleva la versión, la variante (TS/NTS, compilador,
/// arquitectura), la URL y el SHA-256 que tiene que dar el fichero descargado.
/// </summary>
public sealed record PhpBuild
{
    public required ComponentVersion Version { get; init; }

    /// <summary>Rama tal como la agrupa el catálogo: "8.3".</summary>
    public required string Branch { get; init; }

    /// <summary>true = TS (thread-safe, para Apache/mod_php); false = NTS (para nginx/FastCGI y php-fpm).</summary>
    public required bool ThreadSafe { get; init; }

    /// <summary>
    /// Compilador del build: en Windows, vc15, vs16 o vs17 (decide qué VC++ Redistributable hace
    /// falta); en Linux, <see cref="LinuxPhpCatalog.Compiler"/> (la glibc mínima con la que corre).
    /// </summary>
    public required string Compiler { get; init; }

    /// <summary>"x64" o "x86".</summary>
    public required string Architecture { get; init; }

    /// <summary>Nombre del fichero en el catálogo: <c>php-8.3.36-nts-Win32-vs16-x64.zip</c> o <c>php-8.4.27-nts-linux-x86_64.tar.gz</c>.</summary>
    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    /// <summary>SHA-256 en hexadecimal y minúsculas.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Tamaño tal como lo escribe el catálogo ("32.52MB"); solo informativo.</summary>
    public string? SizeText { get; init; }

    /// <summary>Tamaño exacto en bytes, cuando el catálogo lo fija (roost-builds); null si solo hay <see cref="SizeText"/>.</summary>
    public long? Size { get; init; }

    /// <summary>El sistema del build. Los de Windows son zips; los de Linux, tar.gz con otro layout.</summary>
    public PhpOs Os { get; init; } = PhpOs.Windows;

    /// <summary>Carpeta de instalación: <c>8.3.36-nts-x64</c> (ROOST.md §5.4), igual en los dos sistemas.</summary>
    public string InstallName => $"{Version}-{(ThreadSafe ? "ts" : "nts")}-{Architecture}";

    /// <summary>
    /// El ejecutable de PHP dentro del paquete, relativo a la carpeta de instalación: <c>php.exe</c>
    /// en la raíz del zip de Windows y <c>bin/php</c> en el tar.gz de roost-builds.
    /// </summary>
    public string ExecutableRelativePath => Os == PhpOs.Linux ? Path.Combine("bin", "php") : "php.exe";
}
