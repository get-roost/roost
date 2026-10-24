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

    // Servicios (oleada 3): Redis y el correo de pruebas (Mailpit).

    public const int DefaultRedisPort = 6379;
    public const int DefaultMailSmtpPort = 1025;
    public const int DefaultMailWebPort = 8025;

    /// <summary>
    /// <c>[redis] enabled</c>: Redis arranca con el stack, después de la base de datos. Lo pone
    /// <c>roost redis install</c> (o <c>enable</c>); sin la clave, no arranca.
    /// </summary>
    public bool RedisEnabled { get; init; }

    /// <summary><c>[redis] port</c>, solo en 127.0.0.1.</summary>
    public int RedisPort { get; init; } = DefaultRedisPort;

    /// <summary>
    /// <c>[mail] enabled</c>: Mailpit arranca con el stack y el <c>mail()</c> de PHP le manda el
    /// correo (<c>etc/php/conf.d/20-roost-mail.ini</c>). Lo pone <c>roost mail install</c>.
    /// </summary>
    public bool MailEnabled { get; init; }

    /// <summary><c>[mail] smtp_port</c>: donde recibe Mailpit el correo, solo en 127.0.0.1.</summary>
    public int MailSmtpPort { get; init; } = DefaultMailSmtpPort;

    /// <summary><c>[mail] web_port</c>: la bandeja de Mailpit (web y API), solo en 127.0.0.1.</summary>
    public int MailWebPort { get; init; } = DefaultMailWebPort;

    /// <summary>La URL de la bandeja de Mailpit.</summary>
    public string MailWebUrl => $"http://localhost:{MailWebPort}/";

    /// <summary>
    /// Los puertos que va a coger el stack con esta configuración, sin los del pool de PHP: la web,
    /// el HTTPS si hay sitios, la base de datos si hay una, Redis y Mailpit si van. Lo usan los avisos
    /// de los servicios de Windows que se quedan los puertos.
    /// </summary>
    public IReadOnlyList<int> PlannedPorts(bool withSites, bool withDb)
    {
        var ports = new List<int> { HttpPort };
        if (withSites) ports.Add(HttpsPort);
        if (withDb) ports.Add(DbPort);
        if (RedisEnabled) ports.Add(RedisPort);
        if (MailEnabled) ports.AddRange([MailSmtpPort, MailWebPort]);
        return ports.Distinct().ToList();
    }

    /// <summary>Lo que se escribe la primera vez que un comando crea el fichero.</summary>
    private static string Template(string? phpVersion, string? dbEngine, string? dbVersion) => $"""
        # Configuracion de Roost. Puedes editarla a mano: cuando un comando cambia una
        # clave (por ejemplo `roost php use`), Roost toca solo esa clave y respeta el resto.

        [paths]
        # Carpeta raiz de Roost: los PHP y las bases de datos instalados, sus datos, la CA, los
        # registros y los proyectos (www). Sin esta clave, C:\roost. Cambiala desde la app (Ajustes,
        # Rutas, Cambiar...): mueve lo que hay y lo comprueba. A mano, Roost empezaria de cero ahi.
        # root = 'D:\roost'

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

        [redis]
        # Redis con el stack (roost redis install): enabled = true y su puerto (solo en 127.0.0.1).
        enabled = false
        port = {DefaultRedisPort}

        [mail]
        # Mailpit con el stack (roost mail install): el correo de PHP se queda en su bandeja.
        enabled = false
        smtp_port = {DefaultMailSmtpPort}
        web_port = {DefaultMailWebPort}

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
            RedisEnabled = ReadBool(doc, "redis", "enabled", path) ?? false,
            RedisPort = ReadInteger(doc, "redis", "port", path) ?? DefaultRedisPort,
            MailEnabled = ReadBool(doc, "mail", "enabled", path) ?? false,
            MailSmtpPort = ReadInteger(doc, "mail", "smtp_port", path) ?? DefaultMailSmtpPort,
            MailWebPort = ReadInteger(doc, "mail", "web_port", path) ?? DefaultMailWebPort,
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
        if (config.ServicePortClash() is { } clash)
            throw new RoostException($"{path}: {clash}");
        return config;
    }

    /// <summary>
    /// Por qué no valen los puertos de Redis y del correo: fuera de rango, o el mismo que otro
    /// puerto del stack (el de la web, el HTTPS, la base de datos o el otro de Mailpit). Solo se
    /// miran los que van en el stack. null si valen.
    /// </summary>
    public string? ServicePortClash()
    {
        var taken = new List<(string Key, int Port)> { ("[web] port", HttpPort), ("[web] https_port", HttpsPort) };
        if (DbEngine != NoDb) taken.Add(("[db] port", DbPort));
        var wanted = new List<(string Key, int Port)>();
        if (RedisEnabled) wanted.Add(("[redis] port", RedisPort));
        if (MailEnabled)
        {
            wanted.Add(("[mail] smtp_port", MailSmtpPort));
            wanted.Add(("[mail] web_port", MailWebPort));
        }

        foreach (var (key, port) in wanted)
        {
            if (port is < 1 or > 65535) return $"{key} tiene que estar entre 1 y 65535 (vale {port}).";
            if (taken.FirstOrDefault(t => t.Port == port) is { Key: not null } other)
                return $"{key} ({port}) es el mismo puerto que {other.Key}: tienen que ser distintos.";
            taken.Add((key, port));
        }
        return null;
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

    // Ajustes y sistema (J2): la raíz de Roost, fuera de ella (en %APPDATA%\Roost\roost.toml).

    /// <summary>
    /// <c>[paths] root</c>, la raíz de Roost, si la hay y es una ruta completa; null si no. Lo lee
    /// <see cref="Platform.RoostPaths.Default()"/> en cada arranque, así que <b>nunca lanza</b>: un
    /// fichero que no se puede leer o que no es TOML válido no tiene raíz (y el comando que lo cargue
    /// después ya dirá qué le pasa).
    /// </summary>
    public static string? ReadRoot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = SyntaxParser.Parse(File.ReadAllText(path), path, false);
            if (doc.HasErrors || ReadValue(doc, "paths", "root") is not StringValueSyntax { Value: { } root }) return null;
            root = root.Trim();
            return root.Length > 0 && Path.IsPathFullyQualified(root) ? Path.GetFullPath(root) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Fija <c>[paths] root</c>; null la quita (vuelve a la de siempre).</summary>
    public static void SetRoot(string path, string? root) =>
        Set(path, "paths", [("root", Text(string.IsNullOrWhiteSpace(root) ? null : root.Trim()))]);

    /// <summary>Fija <c>[git] exe</c>; null vuelve al git de Roost.</summary>
    public static void SetGitExe(string path, string? exe) =>
        Set(path, "git", [("exe", Text(string.IsNullOrWhiteSpace(exe) ? null : exe.Trim()))]);

    /// <summary>
    /// Fija <c>[redis] enabled</c> y, si se da, <c>[redis] port</c>. Valida el puerto contra los
    /// demás del stack antes de escribir nada.
    /// </summary>
    public static void SetRedis(string path, bool? enabled = null, int? port = null)
    {
        var current = Load(path);
        var next = current with { RedisEnabled = enabled ?? current.RedisEnabled, RedisPort = port ?? current.RedisPort };
        // Un puerto nuevo se valida aunque Redis no vaya en el stack: valdrá el día que se active.
        if ((port is null ? next : next with { RedisEnabled = true }).ServicePortClash() is { } clash) throw new RoostException(clash);
        var values = new List<(string, ValueSyntax?)>();
        if (enabled is { } e) values.Add(("enabled", Flag(e)));
        if (port is { } value) values.Add(("port", Number(value)));
        Set(path, "redis", [.. values]);
    }

    /// <summary>Fija <c>[mail] enabled</c> y, si se dan, <c>smtp_port</c> y <c>web_port</c>.</summary>
    public static void SetMail(string path, bool? enabled = null, int? smtpPort = null, int? webPort = null)
    {
        var current = Load(path);
        var next = current with
        {
            MailEnabled = enabled ?? current.MailEnabled,
            MailSmtpPort = smtpPort ?? current.MailSmtpPort,
            MailWebPort = webPort ?? current.MailWebPort,
        };
        if ((smtpPort is null && webPort is null ? next : next with { MailEnabled = true }).ServicePortClash() is { } clash)
            throw new RoostException(clash);
        var values = new List<(string, ValueSyntax?)>();
        if (enabled is { } e) values.Add(("enabled", Flag(e)));
        if (smtpPort is { } smtp) values.Add(("smtp_port", Number(smtp)));
        if (webPort is { } web) values.Add(("web_port", Number(web)));
        Set(path, "mail", [.. values]);
    }

    private static BooleanValueSyntax Flag(bool value) => new(value);

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

    private static bool? ReadBool(DocumentSyntax doc, string table, string key, string path) =>
        ReadValue(doc, table, key) switch
        {
            null => null,
            BooleanValueSyntax b => b.Value,
            _ => throw new RoostException($"{path}: [{table}] {key} tiene que ser true o false."),
        };

    private static int? ReadInteger(DocumentSyntax doc, string table, string key, string path) =>
        ReadValue(doc, table, key) switch
        {
            null => null,
            IntegerValueSyntax i when i.Value is >= int.MinValue and <= int.MaxValue => (int)i.Value,
            _ => throw new RoostException($"{path}: [{table}] {key} tiene que ser un numero."),
        };
}
