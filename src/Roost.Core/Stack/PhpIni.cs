using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Install;

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

/// <summary>El JSON de extensiones.json y de ajustes.json, generado al compilar (lo lee también el shim, con NativeAOT).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PhpExtensionChoice.Stored))]
[JsonSerializable(typeof(PhpSettings.Stored), TypeInfoPropertyName = "SettingsStored")]
internal sealed partial class PhpExtensionJson : JsonSerializerContext;

/// <summary>
/// El php.ini que genera Roost para cada instalación (ROOST.md §5.10): ajustes de desarrollo (los
/// rápidos, de <see cref="PhpSettings"/>), <c>extension_dir</c> apuntando a SU carpeta <c>ext/</c>,
/// solo las extensiones cuya DLL trae ese build y, si se ha pedido, Xdebug. Se regenera en cada
/// <c>roost up</c>; lo del usuario va en <c>conf.d/99-usuario.ini</c> (todas las versiones) y en
/// <c>conf.d/&lt;rama&gt;/</c> (solo esa rama), que PHP lee vía <c>PHP_INI_SCAN_DIR</c>, después de este,
/// y que sobreviven a las actualizaciones.
/// <para>
/// <b>En Linux</b> (los PHP de roost-builds, compilados estáticos) las extensiones van integradas en el
/// binario: no se escribe <c>extension=</c> de ninguna integrada (ni <c>zend_extension=opcache</c>) y
/// solo se cargan las compartidas de <c>ext/</c> (Xdebug). Las integradas las dice su <c>BUILD.json</c>
/// (<see cref="BuiltInExtensions"/>) y no se pueden quitar.
/// </para>
/// </summary>
public static class PhpIni
{
    /// <summary>Extensiones que se activan si el build trae su DLL (<c>gd2</c> es el nombre en PHP 7.x).</summary>
    public static readonly IReadOnlyList<string> WantedExtensions =
    [
        "bz2", "curl", "exif", "fileinfo", "gd", "gd2", "gettext", "intl", "mbstring",
        "mysqli", "openssl", "pdo_mysql", "pdo_sqlite", "sodium", "sqlite3", "zip",
    ];

    /// <summary>
    /// Las que se cargan con <c>zend_extension</c> y nunca con <c>extension</c>: Xdebug 3 se niega a
    /// arrancar como extensión normal («MUST be loaded as a Zend extension») y OPcache igual. Cada
    /// una va en su sección (<c>[opcache]</c>, <c>[xdebug]</c>), con sus ajustes.
    /// </summary>
    public static readonly IReadOnlyList<string> ZendExtensions = ["opcache", Xdebug];

    public const string Xdebug = "xdebug";

    public const string UserFragmentName = "99-usuario.ini";

    /// <summary>true si se carga con <c>zend_extension</c> (<see cref="ZendExtensions"/>).</summary>
    public static bool IsZendExtension(string name) => ZendExtensions.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Las que trae el build para los tests de PHP mismo: ni se ofrecen ni se cargan.</summary>
    private static readonly string[] TestExtensions = ["dl_test", "zend_test"];

    /// <summary>
    /// Las extensiones que se pueden cargar en una instalación: en Windows, las DLL de <c>ext/</c>
    /// (<c>php_curl.dll</c> → <c>curl</c>); en Linux, las integradas de su <c>BUILD.json</c> más las
    /// compartidas de <c>ext/</c> (<c>xdebug.so</c> → <c>xdebug</c>).
    /// </summary>
    public static IReadOnlySet<string> AvailableExtensions(string installDirectory)
    {
        var all = new HashSet<string>(BuiltInExtensions(installDirectory), StringComparer.OrdinalIgnoreCase);
        all.UnionWith(SharedExtensions(installDirectory));
        return all;
    }

    /// <summary>Las integradas en el binario (compilado estático): las de su <c>BUILD.json</c>. Vacío en Windows, donde todas son DLL.</summary>
    public static IReadOnlySet<string> BuiltInExtensions(string installDirectory) =>
        PhpBuildInfo.Read(installDirectory)?.Extensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las que son un fichero en <c>ext/</c>: <c>php_*.dll</c> en Windows y <c>*.so</c> en Linux.</summary>
    public static IReadOnlySet<string> SharedExtensions(string installDirectory)
    {
        var ext = Path.Combine(installDirectory, "ext");
        if (!Directory.Exists(ext)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dlls = Directory.EnumerateFiles(ext, "php_*.dll")
            .Select(f => Path.GetFileNameWithoutExtension(f)["php_".Length..]);
        var sos = Directory.EnumerateFiles(ext, "*.so")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name!.StartsWith("php_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name);
        return dlls.Concat(sos)
            .Where(name => !TestExtensions.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PURO: el nombre de una extensión tal como lo lista <c>php -m</c> («Zend OPcache», «PDO»,
    /// «SimpleXML») en el nombre con el que se configura (<c>opcache</c>, <c>pdo</c>, <c>simplexml</c>).
    /// </summary>
    public static string ModuleName(string module)
    {
        var name = module.Trim();
        if (name.Equals("Zend OPcache", StringComparison.OrdinalIgnoreCase)) return "opcache";
        return name.ToLowerInvariant();
    }

    /// <summary>true si la extensión se activa de serie (sin elección del usuario) cuando el build la trae.</summary>
    public static bool EnabledByDefault(string name) =>
        name.Equals("opcache", StringComparison.OrdinalIgnoreCase) || WantedExtensions.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PURO: las extensiones que se cargan. Las de serie que trae el build, más las que el
    /// usuario activa y menos las que quita; nunca una cuya DLL no está.
    /// </summary>
    public static IReadOnlySet<string> Enabled(IReadOnlySet<string> availableExtensions, PhpExtensionChoice choice) =>
        Enabled(availableExtensions, choice, builtIn: null);

    /// <summary>
    /// PURO: lo mismo, con las integradas (<paramref name="builtIn"/>) siempre dentro: van en el
    /// binario y la elección del usuario no puede quitarlas.
    /// </summary>
    public static IReadOnlySet<string> Enabled(IReadOnlySet<string> availableExtensions, PhpExtensionChoice choice, IReadOnlySet<string>? builtIn) =>
        availableExtensions
            .Where(name => (builtIn is not null && builtIn.Contains(name))
                           || ((EnabledByDefault(name) || choice.On.Contains(name)) && !choice.Off.Contains(name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lo que lleva la sección <c>[xdebug]</c> además de la DLL (ver <see cref="Render"/>).</summary>
    public static readonly IReadOnlyList<(string Key, string Value)> XdebugDefaults =
    [
        ("xdebug.mode", "debug,develop"),
        ("xdebug.start_with_request", "trigger"),
        ("xdebug.client_host", "127.0.0.1"),
        ("xdebug.client_port", "9003"),
    ];

    /// <summary>
    /// PURO: genera el texto del php.ini a partir de lo que trae el build y lo que ha elegido el usuario.
    /// </summary>
    /// <param name="settings">Los ajustes rápidos; null son los de serie.</param>
    /// <param name="xdebugDll">
    /// El módulo de Xdebug (la DLL de Roost en <c>bin/xdebug/…</c>, o el <c>ext/xdebug.so</c> del propio
    /// PHP en Linux), con el que Xdebug cuenta como disponible. Si es null y el build trae
    /// <c>ext/php_xdebug.dll</c> (puesta a mano), se carga esa por su nombre. Solo se carga si el
    /// usuario la ha activado (<paramref name="choice"/>).
    /// </param>
    /// <param name="branchConfD">La carpeta conf.d de su rama, para decirlo en la cabecera.</param>
    /// <param name="builtIn">
    /// Las extensiones integradas en el binario (Linux, del <c>BUILD.json</c>): siempre activas y nunca
    /// escritas como <c>extension=</c> ni <c>zend_extension=</c>.
    /// </param>
    /// <param name="opensslCaFile">
    /// Poner también <c>openssl.cafile</c> (Linux): sin él, los streams (<c>file_get_contents</c> con https)
    /// de un PHP estático no se fían de nada, porque no hay almacén del sistema que consultar.
    /// </param>
    public static string Render(
        string installName,
        string installDirectory,
        IReadOnlySet<string> availableExtensions,
        string confD,
        string errorLog,
        PhpExtensionChoice? choice = null,
        string? caBundle = null,
        PhpSettings? settings = null,
        string? xdebugDll = null,
        string? branchConfD = null,
        IReadOnlySet<string>? builtIn = null,
        bool opensslCaFile = false)
    {
        choice ??= PhpExtensionChoice.None;
        builtIn ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        settings = (settings ?? PhpSettings.Default).Normalized();
        if (xdebugDll is not null && !availableExtensions.Contains(Xdebug))
            availableExtensions = availableExtensions.Append(Xdebug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = Enabled(availableExtensions, choice, builtIn);
        var userFiles = branchConfD is null
            ? $"; Tus ajustes van en {Path.Combine(confD, UserFragmentName)}, que vale para todas las versiones."
            : $"""
              ; Tus ajustes van en {Path.Combine(confD, UserFragmentName)} (todas las versiones)
              ; o en {Path.Combine(branchConfD, UserFragmentName)} (solo esta rama). Se leen despues y mandan.
              """;
        var sb = new StringBuilder();
        sb.Append($"""
            ; Generado por Roost para PHP {installName}. NO lo edites: se regenera en cada `roost up`.
            {userFiles}

            [PHP]
            memory_limit = {settings.MemoryLimit}
            max_execution_time = {settings.MaxExecutionTime}
            post_max_size = {settings.PostMaxSize}
            upload_max_filesize = {settings.UploadMaxFilesize}
            display_errors = On
            display_startup_errors = On
            error_reporting = E_ALL
            log_errors = On
            error_log = "{errorLog}"
            extension_dir = "{Path.Combine(installDirectory, "ext")}"

            """);
        // Sin esto, la extensión curl (Guzzle, Laravel, WordPress) no confía en nada (ver CaBundle).
        // En Windows openssl.cafile no se pone: sin él, los streams (file_get_contents) usan el almacén
        // de Windows, que también tiene la CA de Roost si se confía en ella y las de una empresa. En
        // Linux no hay almacén que consultar desde un PHP estático: se pone también.
        if (caBundle is not null)
        {
            sb.Append($"curl.cainfo = \"{caBundle}\"\n");
            if (opensslCaFile) sb.Append($"openssl.cafile = \"{caBundle}\"\n");
        }
        sb.Append('\n');

        // Primero las de serie, en su orden; luego las que ha activado el usuario. Las zend
        // (OPcache, Xdebug) nunca aquí: van en su sección, con zend_extension. Las integradas en el
        // binario (Linux) tampoco: ya están dentro.
        var loadable = enabled.Where(name => !builtIn.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensions = WantedExtensions.Where(loadable.Contains)
            .Concat(loadable.Where(name => !EnabledByDefault(name) && !IsZendExtension(name)).Order(StringComparer.Ordinal));
        foreach (var name in extensions)
            sb.Append($"extension={name}\n");

        sb.Append("\n[opcache]\n");
        // Desde PHP 8.5 OPcache va dentro del binario y no hay DLL que cargar; en los builds estáticos de Linux, siempre.
        if (loadable.Contains("opcache"))
            sb.Append("zend_extension=opcache\n");
        sb.Append(choice.Off.Contains("opcache") ? "opcache.enable = 0\n" : "opcache.enable = 1\n");
        sb.Append("opcache.validate_timestamps = 1\n");
        sb.Append("opcache.revalidate_freq = 0\n");

        // Xdebug después de OPcache, como pide Xdebug. Con trigger solo depura (y solo intenta
        // conectar con el editor) la petición que lo pide: el resto va a la velocidad de siempre.
        if (enabled.Contains(Xdebug))
        {
            sb.Append("\n[xdebug]\n");
            sb.Append(xdebugDll is null ? "zend_extension=xdebug\n" : $"zend_extension=\"{xdebugDll}\"\n");
            sb.Append("; Se depura la peticion que trae XDEBUG_TRIGGER (o XDEBUG_SESSION): cookie, GET, POST o entorno.\n");
            foreach (var (key, value) in XdebugDefaults)
                sb.Append($"{key} = {value}\n");
        }
        return sb.ToString();
    }

    /// <summary>Contenido inicial del fragmento del usuario (solo se escribe si no existe).</summary>
    public static string UserFragmentTemplate => $"""
        ; Tus ajustes de PHP. Roost no toca este fichero y lo carga en TODAS las versiones
        ; (va despues del php.ini generado, asi que lo que pongas aqui manda).
        ; Lo que solo valga para una rama (un zend_extension, una extension de PECL) va en su carpeta:
        ; {Path.Combine("conf.d", "8.4", UserFragmentName)}, {Path.Combine("conf.d", "7.4", UserFragmentName)}...
        ; Ejemplo:
        ; memory_limit = 1G
        ; date.timezone = Europe/Madrid

        """;

    /// <summary>Contenido inicial del fragmento del usuario de una rama (<c>conf.d/&lt;rama&gt;/99-usuario.ini</c>).</summary>
    public static string BranchFragmentTemplate(string branch) => $"""
        ; Tus ajustes solo para PHP {branch}. Roost no toca este fichero: lo carga en las instalaciones
        ; de la {branch}, despues del php.ini generado y de {Path.Combine("conf.d", UserFragmentName)} (lo de aqui manda).
        ; Es el sitio para lo que no vale en otras ramas: una extension de PECL compilada para la {branch}...
        ; Ejemplo:
        ; extension=redis
        ; memory_limit = 2G

        """;
}
