using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Roost.Core.Config;

/// <summary>
/// Configuración global de Roost, en <c>roost.toml</c> (TOML, decisión 12 de ROOST.md). Es
/// opcional: sin fichero valen los valores por defecto. Cuando un comando cambia una clave
/// (<c>roost php use</c>, <c>roost db use</c>), Roost edita solo esa clave sobre el árbol de
/// sintaxis y respeta el resto del fichero, comentarios incluidos.
/// </summary>
public sealed record RoostConfig
{
    public const int DefaultPhpPool = 4;
    public const int DefaultHttpPort = 80;
    public const int DefaultHttpsPort = 443;
    public const int DefaultDbPort = 3306;

    /// <summary>Valor de <c>[db] engine</c> para no arrancar ninguna base de datos.</summary>
    public const string NoDb = "none";

    /// <summary><c>[web] server</c>: nginx, con los pools de php-cgi (el de siempre).</summary>
    public const string WebNginx = "nginx";

    /// <summary><c>[web] server</c>: Apache, con los mismos pools por mod_proxy_fcgi y los .htaccess.</summary>
    public const string WebApache = "apache";

    /// <summary><c>[sites] resolver</c>: los <c>.test</c> por el fichero hosts (UAC en cada alta o baja).</summary>
    public const string ResolverHosts = "hosts";

    /// <summary><c>[sites] resolver</c>: los <c>.test</c> por el DNS local de Roost (un solo UAC, al ponerlo).</summary>
    public const string ResolverDns = "dns";

    /// <summary>
    /// PHP global: rama ("8.4"), versión ("8.4.27") o instalación ("8.4.27-nts-x64").
    /// null = el PHP más nuevo que haya instalado Roost.
    /// </summary>
    public string? PhpVersion { get; init; }

    /// <summary>Procesos php-cgi por versión (Windows no tiene php-fpm, ROOST.md §5.5).</summary>
    public int PhpPool { get; init; } = DefaultPhpPool;

    /// <summary>El servidor web que arranca <c>roost up</c>: <see cref="WebNginx"/> o <see cref="WebApache"/>.</summary>
    public string WebServer { get; init; } = WebNginx;

    public bool UsesApache => WebServer == WebApache;

    /// <summary>Puerto HTTP en el que escucha el servidor web (solo en 127.0.0.1).</summary>
    public int HttpPort { get; init; } = DefaultHttpPort;

    /// <summary>Puerto HTTPS de los sitios <c>*.test</c> (solo en 127.0.0.1).</summary>
    public int HttpsPort { get; init; } = DefaultHttpsPort;

    /// <summary>
    /// Motor de la base de datos global: "mariadb", "mysql" o <see cref="NoDb"/>. null = la
    /// más nueva que haya instalado Roost (MariaDB antes que MySQL), si hay alguna.
    /// </summary>
    public string? DbEngine { get; init; }

    /// <summary>Versión de la base de datos global: mayor ("8"), rama ("11.4") o exacta.</summary>
    public string? DbVersion { get; init; }

    public int DbPort { get; init; } = DefaultDbPort;

    /// <summary>Cómo se resuelven los <c>.test</c>: <see cref="ResolverHosts"/> o <see cref="ResolverDns"/>.</summary>
    public string SitesResolver { get; init; } = ResolverHosts;

    public bool UsesLocalDns => SitesResolver == ResolverDns;

    /// <summary>
    /// <c>[git] exe</c>: otro git en vez del MinGit que instala Roost (el de Git for Windows, por
    /// ejemplo). null = el de Roost. Se usa con el mismo entorno aislado.
    /// </summary>
    public string? GitExe { get; init; }

    /// <summary>Lo que se escribe la primera vez que un comando crea el fichero.</summary>
    private static string Template(string? phpVersion, string? dbEngine, string? dbVersion) => $"""
        # Configuracion de Roost. Puedes editarla a mano: cuando un comando cambia una
        # clave (por ejemplo `roost php use`), Roost toca solo esa clave y respeta el resto.

        [php]
        # PHP global: rama (8.4), version (8.4.27) o instalacion (8.4.27-nts-x64).
        # Sin esta clave, Roost usa el PHP mas nuevo que haya instalado.
        {Line("version", phpVersion, "8.4")}
        # Procesos php-cgi por version (1 a 10).
        pool = {DefaultPhpPool}

        [web]
        # Servidor web: "nginx" o "apache" (lee los .htaccess). Los dos usan los mismos PHP.
        server = "{WebNginx}"
        # Puertos HTTP y HTTPS (escucha solo en 127.0.0.1).
        port = {DefaultHttpPort}
        https_port = {DefaultHttpsPort}

        [db]
        # Base de datos global: engine = "mariadb", "mysql" o "none" (ninguna) y su version
        # (11.4, 8, 5.7.44...). Sin estas claves, Roost usa la mas nueva que haya instalado.
        {Line("engine", dbEngine, "mariadb")}
        {Line("version", dbVersion, "11.4")}
        # Puerto (escucha solo en 127.0.0.1). El usuario es root, sin contrasena.
        port = {DefaultDbPort}

        [sites]
        # Como se resuelven los .test: "hosts" (el fichero hosts del sistema; pide permiso de
        # administrador en cada alta o baja) o "dns" (el DNS local de Roost: roost dns install).
        resolver = "{ResolverHosts}"

        [git]
        # Otro git en vez del MinGit que instala Roost (roost git install), por ejemplo el de Git for Windows.
        # exe = 'C:\Program Files\Git\cmd\git.exe'

        """;

    // Una clave con valor, o comentada con un ejemplo si no lo tiene.
    private static string Line(string key, string? value, string example) =>
        value is null ? $"# {key} = \"{example}\"" : $"{key} = \"{value}\"";

    /// <summary>Lee <paramref name="path"/>; si no existe, devuelve los valores por defecto.</summary>
    public static RoostConfig Load(string path)
    {
        if (!File.Exists(path)) return new RoostConfig();

        var doc = Parse(File.ReadAllText(path), path);
        var config = new RoostConfig
        {
            PhpVersion = ReadString(doc, "php", "version", path),
            PhpPool = ReadInteger(doc, "php", "pool", path) ?? DefaultPhpPool,
            WebServer = ReadString(doc, "web", "server", path)?.Trim().ToLowerInvariant() ?? WebNginx,
            HttpPort = ReadInteger(doc, "web", "port", path) ?? DefaultHttpPort,
            HttpsPort = ReadInteger(doc, "web", "https_port", path) ?? DefaultHttpsPort,
            DbEngine = ReadString(doc, "db", "engine", path)?.Trim().ToLowerInvariant(),
            DbVersion = ReadString(doc, "db", "version", path),
            DbPort = ReadInteger(doc, "db", "port", path) ?? DefaultDbPort,
            SitesResolver = ReadString(doc, "sites", "resolver", path)?.Trim().ToLowerInvariant() ?? ResolverHosts,
            GitExe = ReadString(doc, "git", "exe", path) is { } gitExe && !string.IsNullOrWhiteSpace(gitExe) ? gitExe.Trim() : null,
        };

        if (config.PhpPool is < 1 or > 10)
            throw new RoostException($"{path}: [php] pool tiene que estar entre 1 y 10 (vale {config.PhpPool}).");
        if (config.WebServer is not (WebNginx or WebApache))
            throw new RoostException($"{path}: [web] server tiene que ser \"nginx\" o \"apache\" (vale \"{config.WebServer}\").");
        if (config.HttpPort is < 1 or > 65535)
            throw new RoostException($"{path}: [web] port tiene que estar entre 1 y 65535 (vale {config.HttpPort}).");
        if (config.HttpsPort is < 1 or > 65535 || config.HttpsPort == config.HttpPort)
            throw new RoostException($"{path}: [web] https_port tiene que estar entre 1 y 65535 y ser distinto de port (vale {config.HttpsPort}).");
        if (config.DbPort is < 1 or > 65535)
            throw new RoostException($"{path}: [db] port tiene que estar entre 1 y 65535 (vale {config.DbPort}).");
        if (config.SitesResolver is not (ResolverHosts or ResolverDns))
            throw new RoostException($"{path}: [sites] resolver tiene que ser \"hosts\" o \"dns\" (vale \"{config.SitesResolver}\").");
        if (config.DbEngine is not (null or "mariadb" or "mysql" or NoDb))
            throw new RoostException($"{path}: [db] engine tiene que ser \"mariadb\", \"mysql\" o \"none\" (vale \"{config.DbEngine}\").");
        return config;
    }

    /// <summary>Fija <c>[php] version</c>, creando el fichero o la tabla si no existen.</summary>
    public static void SetPhpVersion(string path, string version) =>
        Set(path, "php", [("version", Text(version))], () => Template(version, null, null));

    /// <summary>
    /// Fija <c>[db] engine</c> y <c>[db] version</c>. Con <paramref name="version"/> null se
    /// quita la versión (motor "none", o "la más nueva de ese motor").
    /// </summary>
    public static void SetDb(string path, string engine, string? version) =>
        Set(path, "db", [("engine", Text(engine)), ("version", Text(version))], () => Template(null, engine, version));

    /// <summary>Fija <c>[sites] resolver</c>.</summary>
    public static void SetResolver(string path, string resolver)
    {
        if (resolver is not (ResolverHosts or ResolverDns))
            throw new RoostException($"[sites] resolver tiene que ser \"hosts\" o \"dns\" (vale \"{resolver}\").");
        Set(path, "sites", [("resolver", Text(resolver))]);
    }

    /// <summary>Fija <c>[php] pool</c>, los procesos php-cgi por versión.</summary>
    public static void SetPhpPool(string path, int pool)
    {
        if (pool is < 1 or > 10)
            throw new RoostException($"El pool de PHP tiene que estar entre 1 y 10 (vale {pool}).");
        Set(path, "php", [("pool", Number(pool))]);
    }

    /// <summary>Fija <c>[web] server</c>: <see cref="WebNginx"/> o <see cref="WebApache"/>.</summary>
    public static void SetWebServer(string path, string server)
    {
        if (server is not (WebNginx or WebApache))
            throw new RoostException($"[web] server tiene que ser \"nginx\" o \"apache\" (vale \"{server}\").");
        Set(path, "web", [("server", Text(server))]);
    }

    /// <summary>Fija <c>[web] port</c> y <c>[web] https_port</c>.</summary>
    public static void SetWebPorts(string path, int httpPort, int httpsPort)
    {
        if (httpPort is < 1 or > 65535 || httpsPort is < 1 or > 65535)
            throw new RoostException("Los puertos tienen que estar entre 1 y 65535.");
        if (httpPort == httpsPort)
            throw new RoostException("El puerto HTTP y el HTTPS tienen que ser distintos.");
        Set(path, "web", [("port", Number(httpPort)), ("https_port", Number(httpsPort))]);
    }

    /// <summary>Fija <c>[db] port</c>.</summary>
    public static void SetDbPort(string path, int port)
    {
        if (port is < 1 or > 65535)
            throw new RoostException("El puerto de la base de datos tiene que estar entre 1 y 65535.");
        Set(path, "db", [("port", Number(port))]);
    }

    /// <summary>Fija <c>[git] exe</c>; null vuelve al git de Roost.</summary>
    public static void SetGitExe(string path, string? exe) =>
        Set(path, "git", [("exe", Text(string.IsNullOrWhiteSpace(exe) ? null : exe.Trim()))]);

    private static StringValueSyntax? Text(string? value) => value is null ? null : new StringValueSyntax(value);

    private static IntegerValueSyntax Number(int value) => new(value);

    /// <summary>
    /// Fija claves de una tabla; un valor null quita la clave. Si el fichero no existe, se
    /// escribe <paramref name="template"/>, que ya lleva los valores; sin plantilla propia, se
    /// parte de la de por defecto y se editan las claves sobre ella.
    /// </summary>
    private static void Set(string path, string tableName, (string Key, ValueSyntax? Value)[] values, Func<string>? template = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, (template ?? (() => Template(null, null, null)))());
            if (template is not null) return;
        }

        var doc = Parse(File.ReadAllText(path), path);
        var table = FindTable(doc, tableName);
        if (table is null)
        {
            table = new TableSyntax(tableName);
            doc.Tables.Add(table);
        }

        foreach (var (key, value) in values)
        {
            var existing = table.Items.OfType<KeyValueSyntax>().FirstOrDefault(k => KeyName(k) == key);
            if (value is null)
            {
                if (existing is not null) table.Items.RemoveChild(existing);
            }
            else if (existing is null)
            {
                table.Items.Add(new KeyValueSyntax(key, value));
            }
            else
            {
                existing.Value = value;
            }
        }

        File.WriteAllText(path, doc.ToString());
    }

    private static DocumentSyntax Parse(string text, string path)
    {
        var doc = SyntaxParser.Parse(text, path, false);
        if (doc.HasErrors)
            throw new RoostException($"{path} no es TOML valido: {string.Join("; ", doc.Diagnostics)}");
        return doc;
    }

    private static TableSyntax? FindTable(DocumentSyntax doc, string name) =>
        doc.Tables.OfType<TableSyntax>().FirstOrDefault(t => t.Name?.ToString().Trim() == name);

    private static string KeyName(KeyValueSyntax kv) => kv.Key?.ToString().Trim() ?? "";

    private static ValueSyntax? ReadValue(DocumentSyntax doc, string table, string key) =>
        FindTable(doc, table)?.Items.OfType<KeyValueSyntax>().FirstOrDefault(k => KeyName(k) == key)?.Value;

    private static string? ReadString(DocumentSyntax doc, string table, string key, string path) =>
        ReadValue(doc, table, key) switch
        {
            null => null,
            StringValueSyntax s => s.Value,
            _ => throw new RoostException($"{path}: [{table}] {key} tiene que ser un texto entre comillas."),
        };

    private static int? ReadInteger(DocumentSyntax doc, string table, string key, string path) =>
        ReadValue(doc, table, key) switch
        {
            null => null,
            IntegerValueSyntax i when i.Value is >= int.MinValue and <= int.MaxValue => (int)i.Value,
            _ => throw new RoostException($"{path}: [{table}] {key} tiene que ser un numero."),
        };
}
