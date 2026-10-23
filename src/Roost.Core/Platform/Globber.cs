namespace Roost.Core.Platform;

/// <summary>
/// Expansión de patrones de ruta con comodines (<c>*</c>, <c>?</c>) tipo
/// <c>C:\laragon\bin\php\*\php.exe</c> o <c>/opt/*/bin/php</c>. Equivale a lo que
/// el script Python de referencia hace con <c>glob.glob</c>, pero sin lanzar nunca:
/// directorios inexistentes, sin permisos o rutas demasiado largas se ignoran.
/// Devuelve solo FICHEROS existentes.
/// </summary>
public static class Globber
{
    /// <summary>Expande un patrón a las rutas de fichero reales que casan con él.</summary>
    public static IEnumerable<string> Expand(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return [];

        // Partimos por cualquiera de los dos separadores: los patrones vienen escritos
        // con '\' en Windows y '/' en POSIX.
        var parts = pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return [];

        // Determinamos la raíz desde la que caminar.
        string root;
        int start;
        if (OperatingSystem.IsWindows() && parts[0].EndsWith(':'))
        {
            root = parts[0] + Path.DirectorySeparatorChar; // "C:" -> "C:\"
            start = 1;
        }
        else if (pattern.StartsWith('/'))
        {
            root = "/";
            start = 0;
        }
        else
        {
            root = parts[0];
            start = 1;
        }

        var current = new List<string> { root };
        for (var i = start; i < parts.Length; i++)
        {
            var segment = parts[i];
            var last = i == parts.Length - 1;
            var wildcard = segment.Contains('*') || segment.Contains('?');
            var next = new List<string>();

            foreach (var dir in current)
            {
                try
                {
                    if (wildcard)
                    {
                        // Comodín: enumeramos entradas reales que casen con el patrón.
                        next.AddRange(last
                            ? Directory.EnumerateFileSystemEntries(dir, segment)
                            : Directory.EnumerateDirectories(dir, segment));
                    }
                    else
                    {
                        var combined = Path.Combine(dir, segment);
                        if (last)
                        {
                            if (File.Exists(combined) || Directory.Exists(combined)) next.Add(combined);
                        }
                        else if (Directory.Exists(combined))
                        {
                            next.Add(combined);
                        }
                    }
                }
                catch
                {
                    // Permisos, ruta larga, disco desconectado… ese candidato se cae y seguimos.
                }
            }

            current = next;
            if (current.Count == 0) break;
        }

        // Solo nos quedamos con ficheros: el patrón apunta a ejecutables.
        return current.Where(p =>
        {
            try { return File.Exists(p); }
            catch { return false; }
        });
    }
}
