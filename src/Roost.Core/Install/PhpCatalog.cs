namespace Roost.Core.Install;

/// <summary>
/// El catálogo de PHP del sistema en el que corre Roost: en Windows, el de windows.php.net
/// (<see cref="WindowsPhpCatalog"/>). Es lo que usan la CLI y la app, para que el de Linux (los builds
/// de get-roost/roost-builds) entre aquí sin tocarlas.
/// </summary>
public static class PhpCatalog
{
    /// <summary>Descarga y parsea el catálogo de este sistema.</summary>
    public static Task<IReadOnlyList<PhpBuild>> FetchAsync(HttpClient http, CancellationToken cancellation = default) =>
        WindowsPhpCatalog.FetchAsync(http, cancellation);

    /// <summary>Elige el build que pide el usuario: una rama («8.3») o una versión exacta («8.3.36»).</summary>
    public static PhpBuild Resolve(IEnumerable<PhpBuild> builds, string spec, bool threadSafe = false, string architecture = "x64") =>
        WindowsPhpCatalog.Resolve(builds, spec, threadSafe, architecture);
}
