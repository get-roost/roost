namespace Roost.Core.Platform;

/// <summary>
/// Utilidades sobre la variable de entorno <c>PATH</c>: enumerar sus directorios y
/// resolver cuál es el ejecutable "activo" (el que encontraría <c>which</c>/<c>where</c>),
/// que es lo que marca <see cref="Models.DetectedComponent.IsOnPath"/>.
/// </summary>
public static class PathProbe
{
    /// <summary>Directorios del <c>PATH</c>, en orden, sin vacíos.</summary>
    public static IEnumerable<string> Directories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
                yield return dir.Trim();
        }
    }

    /// <summary>
    /// Primer ejecutable existente en el <c>PATH</c> probando <paramref name="executableNames"/>
    /// en cada directorio, en orden. Es el "activo". Devuelve su ruta absoluta o <c>null</c>.
    /// </summary>
    public static string? ResolveActive(IReadOnlyList<string> executableNames)
    {
        foreach (var dir in Directories())
        {
            foreach (var name in executableNames)
            {
                try
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return Path.GetFullPath(full);
                }
                catch
                {
                    // Directorio inválido en el PATH: lo saltamos.
                }
            }
        }

        return null;
    }
}
