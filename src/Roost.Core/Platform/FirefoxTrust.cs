namespace Roost.Core.Platform;

/// <summary>
/// Que Firefox se fíe de la CA local de Roost. Firefox no usa el almacén de Windows salvo con
/// <c>security.enterprise_roots.enabled</c>; con esa preferencia lee las CA de confianza del sistema
/// (las del usuario incluidas), así que basta con confiar en la CA en Windows (<c>roost cert trust</c>)
/// y fijarla en cada perfil. Se fija en el <c>user.js</c> del perfil, que Firefox aplica en cada
/// arranque, entre las marcas de Roost: se puede quitar sin tocar lo demás. No pide permisos.
/// </summary>
public static class FirefoxTrust
{
    public const string Begin = "// roost:begin";
    public const string End = "// roost:end";
    public const string Pref = "security.enterprise_roots.enabled";

    private const string Block = Begin + " (lo escribe Roost para que Firefox se fie de su CA local; borralo con Ajustes o roost cert untrust)\n"
                                 + "user_pref(\"" + Pref + "\", true);\n"
                                 + End + "\n";

    /// <summary>Para probar sin tocar el Firefox de verdad: otra carpeta con su <c>profiles.ini</c>.</summary>
    public const string DirVariable = "ROOST_FIREFOX_DIR";

    /// <summary>La carpeta de Firefox con <c>profiles.ini</c>: <c>%APPDATA%\Mozilla\Firefox</c> (o la de <see cref="DirVariable"/>).</summary>
    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");

    /// <summary>Los perfiles de <c>profiles.ini</c> que existen en disco; vacío si no hay Firefox.</summary>
    public static IReadOnlyList<string> Profiles(string firefoxRoot)
    {
        var ini = Path.Combine(firefoxRoot, "profiles.ini");
        if (!File.Exists(ini)) return [];

        var profiles = new List<string>();
        string? path = null;
        var relative = true;
        var inProfile = false;
        void Flush()
        {
            if (inProfile && path is not null)
            {
                var dir = relative ? Path.Combine(firefoxRoot, path.Replace('/', Path.DirectorySeparatorChar)) : path;
                if (Directory.Exists(dir)) profiles.Add(Path.GetFullPath(dir));
            }
            path = null;
            relative = true;
        }

        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                inProfile = line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (!inProfile || eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase)) path = value;
            else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase)) relative = value != "0";
        }
        Flush();
        return profiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>true si el <c>user.js</c> del perfil ya lleva el bloque de Roost.</summary>
    public static bool IsEnabled(string profile) =>
        File.Exists(UserJs(profile)) && File.ReadAllText(UserJs(profile)).Contains(Begin, StringComparison.Ordinal);

    /// <summary>Pone el bloque de Roost en el <c>user.js</c> del perfil (o lo renueva), sin tocar lo demás.</summary>
    public static void Enable(string profile) => File.WriteAllText(UserJs(profile), With(Read(profile)));

    /// <summary>Quita el bloque de Roost del <c>user.js</c>; si no queda nada más, borra el fichero.</summary>
    public static void Disable(string profile)
    {
        var file = UserJs(profile);
        if (!File.Exists(file)) return;
        var rest = Without(File.ReadAllText(file));
        if (rest.Trim().Length == 0) File.Delete(file);
        else File.WriteAllText(file, rest);
    }

    /// <summary>PURO: el texto con el bloque de Roost al final (y sin uno anterior).</summary>
    public static string With(string userJs)
    {
        var rest = Without(userJs).TrimEnd();
        return (rest.Length == 0 ? "" : rest + "\n\n") + Block;
    }

    /// <summary>PURO: el texto sin el bloque de Roost.</summary>
    public static string Without(string userJs)
    {
        var text = userJs.Replace("\r\n", "\n");
        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        if (begin < 0) return text;
        var end = text.IndexOf(End, begin, StringComparison.Ordinal);
        if (end < 0) return text;
        end += End.Length;
        if (end < text.Length && text[end] == '\n') end++;
        return (text[..begin].TrimEnd() + "\n" + text[end..]).TrimStart('\n');
    }

    private static string UserJs(string profile) => Path.Combine(profile, "user.js");

    private static string Read(string profile) => File.Exists(UserJs(profile)) ? File.ReadAllText(UserJs(profile)) : "";
}
