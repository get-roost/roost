using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>
/// Sitios avanzados (C5): los registros de cada vhost, en <c>logs/sites/&lt;sitio&gt;-access.log</c> y
/// <c>-error.log</c>, los mismos con nginx y con Apache (solo hay uno en marcha). Las peticiones de un
/// sitio ya no van al <c>nginx-access.log</c> general, que se queda con las de localhost.
/// </summary>
public static class SiteLogs
{
    public static string Directory(RoostPaths paths) => Path.Combine(paths.Logs, "sites");

    public static string Access(RoostPaths paths, string site) => Path.Combine(Directory(paths), site + "-access.log");

    public static string Error(RoostPaths paths, string site) => Path.Combine(Directory(paths), site + "-error.log");

    /// <summary>De qué sitio es un fichero de <c>logs/sites</c> (<c>tienda-access.log</c> → tienda), o null si no es de ninguno.</summary>
    public static string? SiteOf(string fileName)
    {
        foreach (var suffix in new[] { "-access.log", "-error.log" })
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fileName.Length > suffix.Length)
                return fileName[..^suffix.Length];
        }
        return null;
    }

    /// <summary>
    /// Renombra los registros de un sitio (al renombrarlo). Lo que no se pueda (lo tiene abierto el
    /// servidor web) se queda con el nombre de antes: son registros, no se pierde nada.
    /// </summary>
    public static IReadOnlyList<string> Rename(RoostPaths paths, string from, string to)
    {
        var moved = new List<string>();
        foreach (var (old, @new) in new[] { (Access(paths, from), Access(paths, to)), (Error(paths, from), Error(paths, to)) })
        {
            if (!File.Exists(old) || File.Exists(@new)) continue;
            try
            {
                File.Move(old, @new);
                moved.Add(@new);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // abierto por nginx o Apache: se queda como estaba
            }
        }
        return moved;
    }
}
