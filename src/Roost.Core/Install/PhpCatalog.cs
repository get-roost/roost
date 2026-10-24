namespace Roost.Core.Install;

/// <summary>
/// El catálogo de PHP del sistema en el que corre Roost: en Windows, el de windows.php.net
/// (<see cref="WindowsPhpCatalog"/>, leído de la red); en Linux, los builds de get-roost/roost-builds
/// fijados en el binario (<see cref="LinuxPhpCatalog"/>, sin red). Es lo que usan la CLI y la app,
/// que no distinguen uno del otro.
/// </summary>
public static class PhpCatalog
{
    /// <summary>De dónde salen los builds de este sistema, para los textos y la auditoría: «windows.php.net» o «roost-builds».</summary>
    public static string SourceName => OperatingSystem.IsWindows() ? "windows.php.net" : LinuxPhpCatalog.SourceName;

    /// <summary>true si el catálogo se lee de la red (Windows); el de Linux va fijado en Roost.</summary>
    public static bool IsOnline => OperatingSystem.IsWindows();

    /// <summary>Descarga (o devuelve, en Linux) el catálogo de este sistema.</summary>
    public static Task<IReadOnlyList<PhpBuild>> FetchAsync(HttpClient http, CancellationToken cancellation = default) =>
        OperatingSystem.IsWindows()
            ? WindowsPhpCatalog.FetchAsync(http, cancellation)
            : LinuxPhpCatalog.FetchAsync(http, cancellation);

    /// <summary>Elige el build que pide el usuario: una rama («8.3») o una versión exacta («8.3.36»).</summary>
    public static PhpBuild Resolve(IEnumerable<PhpBuild> builds, string spec, bool threadSafe = false, string architecture = "x64") =>
        OperatingSystem.IsWindows()
            ? WindowsPhpCatalog.Resolve(builds, spec, threadSafe, architecture)
            : LinuxPhpCatalog.Resolve(builds, spec, threadSafe, architecture);
}
