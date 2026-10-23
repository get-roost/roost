using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roost.Core.Stack;

/// <summary>
/// Lo que el usuario ha cambiado de las extensiones de una instalación respecto a las de
/// serie (<see cref="PhpIni.WantedExtensions"/>): las que activa y las que quita. Se guarda en
/// <c>etc/php/&lt;instalación&gt;/extensiones.json</c>, junto al php.ini que se genera con ella.
/// </summary>
public sealed record PhpExtensionChoice(IReadOnlySet<string> On, IReadOnlySet<string> Off)
{
    public static readonly PhpExtensionChoice None = new(new HashSet<string>(), new HashSet<string>());

    public const string FileName = "extensiones.json";

    internal sealed record Stored(List<string>? On, List<string>? Off);

    /// <summary>La elección guardada; ninguna si no hay fichero (o si está roto: se vuelve a las de serie).</summary>
    public static PhpExtensionChoice Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return None;
            var stored = JsonSerializer.Deserialize(File.ReadAllText(file), PhpExtensionJson.Default.Stored);
            return new PhpExtensionChoice(
                (stored?.On ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
                (stored?.Off ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var stored = new Stored([.. On.Order(StringComparer.Ordinal)], [.. Off.Order(StringComparer.Ordinal)]);
        File.WriteAllText(file, JsonSerializer.Serialize(stored, PhpExtensionJson.Default.Stored));
    }

    /// <summary>La misma elección con <paramref name="name"/> encendida o apagada.</summary>
    public PhpExtensionChoice With(string name, bool enabled)
    {
        var on = new HashSet<string>(On, StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(Off, StringComparer.OrdinalIgnoreCase);
        on.Remove(name);
        off.Remove(name);
        // Solo se apunta lo que se aparta de lo de serie.
        var byDefault = PhpIni.EnabledByDefault(name);
        if (enabled && !byDefault) on.Add(name);
        if (!enabled && byDefault) off.Add(name);
        return new PhpExtensionChoice(on, off);
    }
}

/// <summary>
/// El php.ini que genera Roost para cada instalación (ROOST.md §5.10): ajustes de desarrollo,
/// <c>extension_dir</c> apuntando a SU carpeta <c>ext/</c> y solo las extensiones cuya DLL
/// trae ese build. Se regenera en cada <c>roost up</c>; lo del usuario va en
/// <c>conf.d/99-usuario.ini</c>, que PHP lee vía <c>PHP_INI_SCAN_DIR</c> y sobrevive a las
/// actualizaciones.
/// </summary>
/// <summary>El JSON de extensiones.json, generado al compilar (lo lee también el shim, con NativeAOT).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PhpExtensionChoice.Stored))]
internal sealed partial class PhpExtensionJson : JsonSerializerContext;

public static class PhpIni
{
    /// <summary>Extensiones que se activan si el build trae su DLL (<c>gd2</c> es el nombre en PHP 7.x).</summary>
    public static readonly IReadOnlyList<string> WantedExtensions =
    [
        "bz2", "curl", "exif", "fileinfo", "gd", "gd2", "gettext", "intl", "mbstring",
        "mysqli", "openssl", "pdo_mysql", "pdo_sqlite", "sodium", "sqlite3", "zip",
    ];

    public const string UserFragmentName = "99-usuario.ini";

    /// <summary>Las que trae el build para los tests de PHP mismo: ni se ofrecen ni se cargan.</summary>
    private static readonly string[] TestExtensions = ["dl_test", "zend_test"];

    /// <summary>Nombres de las DLL de <c>ext/</c> (<c>php_curl.dll</c> → <c>curl</c>).</summary>
    public static IReadOnlySet<string> AvailableExtensions(string installDirectory)
    {
        var ext = Path.Combine(installDirectory, "ext");
        if (!Directory.Exists(ext)) return new HashSet<string>();

        return Directory.EnumerateFiles(ext, "php_*.dll")
            .Select(f => Path.GetFileNameWithoutExtension(f)["php_".Length..])
            .Where(name => !TestExtensions.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>true si la extensión se activa de serie (sin elección del usuario) cuando el build la trae.</summary>
    public static bool EnabledByDefault(string name) =>
        name.Equals("opcache", StringComparison.OrdinalIgnoreCase) || WantedExtensions.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PURO: las extensiones que se cargan. Las de serie que trae el build, más las que el
    /// usuario activa y menos las que quita; nunca una cuya DLL no está.
    /// </summary>
    public static IReadOnlySet<string> Enabled(IReadOnlySet<string> availableExtensions, PhpExtensionChoice choice) =>
        availableExtensions
            .Where(name => (EnabledByDefault(name) || choice.On.Contains(name)) && !choice.Off.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>PURO: genera el texto del php.ini a partir de lo que trae el build y lo que ha elegido el usuario.</summary>
    public static string Render(
        string installName,
        string installDirectory,
        IReadOnlySet<string> availableExtensions,
        string confD,
        string errorLog,
        PhpExtensionChoice? choice = null,
        string? caBundle = null)
    {
        choice ??= PhpExtensionChoice.None;
        var enabled = Enabled(availableExtensions, choice);
        var sb = new StringBuilder();
        sb.Append($"""
            ; Generado por Roost para PHP {installName}. NO lo edites: se regenera en cada `roost up`.
            ; Tus ajustes van en {Path.Combine(confD, UserFragmentName)}, que vale para todas las versiones.

            [PHP]
            memory_limit = 512M
            max_execution_time = 120
            post_max_size = 128M
            upload_max_filesize = 128M
            display_errors = On
            display_startup_errors = On
            error_reporting = E_ALL
            log_errors = On
            error_log = "{errorLog}"
            extension_dir = "{Path.Combine(installDirectory, "ext")}"

            """);
        // Sin esto, la extensión curl (Guzzle, Laravel, WordPress) no confía en nada (ver CaBundle).
        // openssl.cafile no se pone: sin él, los streams (file_get_contents) usan el almacén de
        // Windows, que también tiene la CA de Roost si se confía en ella y las de una empresa.
        if (caBundle is not null)
            sb.Append($"curl.cainfo = \"{caBundle}\"\n");
        sb.Append('\n');

        // Primero las de serie, en su orden; luego las que ha activado el usuario.
        var extensions = WantedExtensions.Where(enabled.Contains)
            .Concat(enabled.Where(name => !EnabledByDefault(name)).Order(StringComparer.Ordinal));
        foreach (var name in extensions)
            sb.Append($"extension={name}\n");

        sb.Append("\n[opcache]\n");
        // Desde PHP 8.5 OPcache va dentro del binario y no hay DLL que cargar.
        if (enabled.Contains("opcache"))
            sb.Append("zend_extension=opcache\n");
        sb.Append(choice.Off.Contains("opcache") ? "opcache.enable = 0\n" : "opcache.enable = 1\n");
        sb.Append("opcache.validate_timestamps = 1\n");
        sb.Append("opcache.revalidate_freq = 0\n");
        return sb.ToString();
    }

    /// <summary>Contenido inicial del fragmento del usuario (solo se escribe si no existe).</summary>
    public const string UserFragmentTemplate = """
        ; Tus ajustes de PHP. Roost no toca este fichero y lo carga en TODAS las versiones
        ; (va despues del php.ini generado, asi que lo que pongas aqui manda).
        ; Ejemplo:
        ; memory_limit = 1G
        ; date.timezone = Europe/Madrid

        """;
}
