namespace Roost.Core.Platform;

/// <summary>
/// La ruta real de un fichero, con los enlaces simbólicos de <b>todos</b> sus componentes
/// resueltos, para quitar duplicados del escaneo: en una distro con usrmerge, <c>/bin</c> es un
/// enlace a <c>usr/bin</c>, así que <c>/bin/php</c> y <c>/usr/bin/php</c> son el mismo binario.
/// En Windows basta con la ruta completa (y se compara sin distinguir mayúsculas). Nunca lanza.
/// </summary>
public static class CanonicalPath
{
    /// <summary>Cómo se comparan rutas en este SO.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>La ruta real de <paramref name="path"/>; si no se puede resolver, la ruta completa.</summary>
    public static string Of(string path)
    {
        var full = Full(path);
        return OperatingSystem.IsWindows() ? full : Resolve(full, ReadLink);
    }

    /// <summary>
    /// PURO: resuelve una ruta POSIX absoluta componente a componente. <paramref name="readLink"/>
    /// da el destino de un enlace (absoluto, o relativo a su carpeta) o null si no lo es. Con un
    /// bucle de enlaces (más de <paramref name="maxLinks"/>), devuelve la ruta tal cual.
    /// </summary>
    public static string Resolve(string absolutePath, Func<string, string?> readLink, int maxLinks = 40)
    {
        if (!absolutePath.StartsWith('/')) return absolutePath;
        var pending = new LinkedList<string>(absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var resolved = new List<string>();
        var links = 0;
        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var part = node.Value;
            if (part == ".") continue;
            if (part == "..")
            {
                if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var candidate = "/" + string.Join('/', resolved.Append(part));
            string? target;
            try { target = readLink(candidate); }
            catch { target = null; }
            if (string.IsNullOrEmpty(target))
            {
                resolved.Add(part);
                continue;
            }
            if (++links > maxLinks) return absolutePath;

            // El destino sustituye a este componente; lo que quedaba va detrás.
            if (target.StartsWith('/')) resolved.Clear();
            var targetParts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = targetParts.Length - 1; i >= 0; i--)
                pending.AddFirst(targetParts[i]);
        }
        return "/" + string.Join('/', resolved);
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string? ReadLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch
        {
            return null;
        }
    }
}
