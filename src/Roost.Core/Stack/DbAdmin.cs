using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Stack;

/// <summary>Un usuario del servidor (ni root ni las cuentas del sistema) y a qué bases de datos llega.</summary>
public sealed record DbUser(string Name, string Host, bool AllDatabases, IReadOnlyList<string> Databases)
{
    public string Account => $"{Name}@{Host}";

    /// <summary>«todas», «tienda, blog» o «ninguna».</summary>
    public string AccessText => AllDatabases ? "todas" : Databases.Count == 0 ? "ninguna" : string.Join(", ", Databases);
}

/// <summary>Lo que contesta un servidor recién instalado a <c>--version</c>, o qué suele faltar si no contesta.</summary>
public sealed record DbSmokeResult(string? VersionLine, string? MissingRedistributable, string? RedistributableUrl)
{
    public bool Ok => VersionLine is not null;
}

/// <summary>
/// Lo que se hace dentro del servidor en marcha: crear, listar y borrar bases de datos, la
/// contraseña de root y los usuarios. Usa el cliente de la propia instalación
/// (<c>mariadb.exe</c> / <c>mysql.exe</c>) con su my.ini, cuya sección <c>[client]</c> lleva a
/// root y su contraseña. Lo usan <c>roost db …</c> y la app. Todo lo que entra en el SQL se
/// valida antes con un patrón cerrado, así nunca hay que escapar nada.
/// </summary>
public static partial class DbAdmin
{
    /// <summary>Las del propio servidor, que no se enseñan como «tus» bases de datos ni se borran.</summary>
    public static readonly IReadOnlySet<string> SystemSchemas =
        new HashSet<string>(["information_schema", "mysql", "performance_schema", "sys"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Las cuentas del propio servidor (y root, que va aparte): no son «tus» usuarios.</summary>
    public static readonly IReadOnlySet<string> SystemUsers =
        new HashSet<string>(["root", "mariadb.sys", "mysql.sys", "mysql.session", "mysql.infoschema", "PUBLIC", ""], StringComparer.OrdinalIgnoreCase);

    public const string RootUser = "root";

    /// <summary>
    /// El host de los usuarios que crea Roost. Vale cualquiera porque el servidor solo escucha en
    /// 127.0.0.1, y así da igual si el proyecto conecta por <c>localhost</c> o por la IP.
    /// </summary>
    public const string AnyHost = "%";

    public const int MaxPasswordLength = 64;

    /// <summary>La regla de las contraseñas, para los errores y los diálogos.</summary>
    public const string PasswordRule = "letras, numeros y simbolos ASCII, sin espacios ni ' \" \\ ` (hasta 64)";

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.:%\-]{1,255}$")]
    private static partial Regex HostPattern();

    /// <summary>Solo letras, números y _ (hasta 64): así el nombre no necesita escaparse en el SQL.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotNameChar();

    /// <summary>
    /// PURO: la base de datos que se propone para un sitio: su nombre con lo que no sea letra, número
    /// o _ cambiado por _ (<c>mi-tienda</c> → <c>mi_tienda</c>) y hasta 64. Es la que crea Nuevo sitio,
    /// la del panel del sitio y la que Bases de datos reconoce como «parece de».
    /// </summary>
    public static string NameFor(string siteName)
    {
        var name = NotNameChar().Replace(siteName.Trim().ToLowerInvariant(), "_");
        if (name.Length > 64) name = name[..64];
        return name.Length == 0 ? "db" : name;
    }

    /// <summary>Un nombre de usuario: letras, números y _ (hasta 32, el máximo de MySQL).</summary>
    public static bool IsValidUserName(string user) => UserPattern().IsMatch(user);

    /// <summary>Un host de una cuenta (<c>%</c>, <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>), tal como sale de <c>mysql.user</c>.</summary>
    public static bool IsValidHost(string host) => HostPattern().IsMatch(host);

    /// <summary>
    /// ASCII imprimible sin espacio, <c>'</c>, <c>"</c>, <c>\</c> ni <c>`</c>: así va tal cual
    /// en el SQL, en la línea de órdenes y entre comillas en el my.ini. La vacía vale (es «sin
    /// contraseña», solo para root).
    /// </summary>
    public static bool IsValidPassword(string password) =>
        password.Length <= MaxPasswordLength
        && password.All(c => c is > ' ' and < '\x7f' && c is not ('\'' or '"' or '\\' or '`'));

    // --- Bases de datos ---

    /// <summary>Crea la base de datos (utf8mb4) si no existe. Lanza <see cref="RoostException"/> si no se puede.</summary>
    public static void Create(InstalledDb db, string iniPath, string name, IProcessRunner runner)
    {
        if (!IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre: usa solo letras, numeros y _ (hasta 64).");

        Run(db, iniPath, $"CREATE DATABASE IF NOT EXISTS `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", runner,
            $"No se pudo crear {name}", TimeSpan.FromSeconds(30));
    }

    /// <summary>Borra la base de datos y todas sus tablas. Nunca una del sistema.</summary>
    public static void Drop(InstalledDb db, string iniPath, string name, IProcessRunner runner)
    {
        if (!IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre: usa solo letras, numeros y _ (hasta 64).");
        if (SystemSchemas.Contains(name))
            throw new RoostException($"{name} es del propio servidor: no se borra.");

        Run(db, iniPath, $"DROP DATABASE `{name}`", runner, $"No se pudo borrar {name}", TimeSpan.FromMinutes(2));
    }

    /// <summary>Las bases de datos del usuario (sin las del sistema), por nombre. Lanza si el cliente no responde.</summary>
    public static IReadOnlyList<string> List(InstalledDb db, string iniPath, IProcessRunner runner)
    {
        var output = Run(db, iniPath, "SHOW DATABASES", runner, "No se pudo leer la lista de bases de datos", TimeSpan.FromSeconds(15));
        return output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line != Ok && IsValidName(line) && !SystemSchemas.Contains(line))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // --- La contraseña de root ---

    /// <summary>Los hosts de las cuentas root (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>…).</summary>
    public static IReadOnlyList<string> RootHosts(InstalledDb db, string iniPath, IProcessRunner runner)
    {
        var output = Run(db, iniPath, $"SELECT host FROM mysql.user WHERE user = '{RootUser}'", runner,
            "No se pudieron leer las cuentas de root", TimeSpan.FromSeconds(15));
        var hosts = output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line != Ok && IsValidHost(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hosts.Count == 0)
            throw new RoostException("El servidor no tiene ninguna cuenta root (o no se pudo leer mysql.user).");
        return hosts;
    }

    /// <summary>
    /// Pone (o quita, con la vacía) la contraseña de root en TODAS sus cuentas, con una sola
    /// sentencia por cuenta en la misma llamada. Solo cambia el servidor: lo que guarda Roost y el
    /// my.ini los deja <see cref="ChangeRootPassword"/>.
    /// </summary>
    public static void SetRootPassword(InstalledDb db, string iniPath, string password, IProcessRunner runner)
    {
        if (!IsValidPassword(password))
            throw new RoostException($"La contrasena no vale: {PasswordRule}.");

        var hosts = RootHosts(db, iniPath, runner);
        var sql = string.Join("; ", hosts.Select(host => $"ALTER USER '{RootUser}'@'{host}' IDENTIFIED BY '{password}'"));
        try
        {
            Run(db, iniPath, sql, runner, "No se pudo cambiar la contrasena de root", TimeSpan.FromSeconds(30));
        }
        catch (RoostException ex) when (ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new RoostException(ex.Message + " Si ya la cambiaste por fuera, dile a Roost cual es: roost db password --save-only (o «Solo guardarla» en la app).");
        }
    }

    /// <summary>
    /// Cambia la contraseña de root de principio a fin: en el servidor (salvo con
    /// <paramref name="saveOnly"/>, para cuando ya se cambió por fuera), en lo que guarda Roost y
    /// en el my.ini, y comprueba que root entra con ella. Si no entra, deja todo como estaba.
    /// La vacía es «volver a sin contraseña».
    /// </summary>
    public static void ChangeRootPassword(RoostPaths paths, InstalledDb db, int port, string password, bool saveOnly, IProcessRunner runner)
    {
        if (!IsValidPassword(password))
            throw new RoostException($"La contrasena no vale: {PasswordRule}.");

        var ini = DbIni.PathFor(paths, db);
        if (!saveOnly) SetRootPassword(db, ini, password, runner);

        var store = new DbPasswords(paths);
        var previous = store.For(db);
        store.Set(db, password);
        DbIni.Write(paths, db, port, password);

        if (Ping(db, ini, runner) is { } error)
        {
            store.Set(db, previous);
            DbIni.Write(paths, db, port, previous);
            throw new RoostException(saveOnly
                ? $"Con esa contrasena root no entra ({error}). Roost sigue con la de antes."
                : $"Se cambio en el servidor, pero root no entra con ella ({error}). Roost ha vuelto a la de antes; compruebalo con el cliente.");
        }
    }

    /// <summary>Un <c>SELECT</c> de prueba con el my.ini actual: null si root entra, o el motivo.</summary>
    public static string? Ping(InstalledDb db, string iniPath, IProcessRunner runner)
    {
        var output = runner.Run(db.Client, Arguments(iniPath, $"SELECT '{Ok}'"), TimeSpan.FromSeconds(15));
        return output is not null && output.Contains(Ok, StringComparison.Ordinal) ? null : Describe(output);
    }

    // --- Usuarios ---

    /// <summary>
    /// Los usuarios que no son del sistema, con su acceso: «todas» si tienen privilegios globales
    /// (en <c>mysql.user</c>) o las bases de datos de <c>mysql.db</c>.
    /// </summary>
    public static IReadOnlyList<DbUser> ListUsers(InstalledDb db, string iniPath, IProcessRunner runner)
    {
        const string sql =
            "SELECT u.user, u.host, "
            + "IF(u.Select_priv = 'Y' OR u.Insert_priv = 'Y' OR u.Update_priv = 'Y' OR u.Delete_priv = 'Y' OR u.Create_priv = 'Y' OR u.Drop_priv = 'Y', 'Y', 'N'), "
            + "IFNULL((SELECT GROUP_CONCAT(d.db ORDER BY d.db SEPARATOR ',') FROM mysql.db d WHERE d.user = u.user AND d.host = u.host), '') "
            + "FROM mysql.user u ORDER BY u.user, u.host";
        var output = Run(db, iniPath, sql, runner, "No se pudo leer la lista de usuarios", TimeSpan.FromSeconds(15));

        // Salida en lotes: una fila por línea, columnas separadas por tabulador.
        var users = new List<DbUser>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 4 || SystemUsers.Contains(parts[0]) || !IsValidHost(parts[1])) continue;
            var databases = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            users.Add(new DbUser(parts[0], parts[1], parts[2] == "Y", databases));
        }
        return users;
    }

    /// <summary>
    /// Crea <c>'usuario'@'%'</c> con todos los privilegios sobre una base de datos o, sin ella,
    /// sobre todas. Seguro porque el servidor solo escucha en este equipo.
    /// </summary>
    public static void CreateUser(InstalledDb db, string iniPath, string user, string password, string? database, IProcessRunner runner)
    {
        if (!IsValidUserName(user))
            throw new RoostException($"'{user}' no vale como usuario: usa solo letras, numeros y _ (hasta 32).");
        if (SystemUsers.Contains(user))
            throw new RoostException($"{user} es una cuenta del servidor: elige otro nombre.");
        if (password.Length == 0 || !IsValidPassword(password))
            throw new RoostException($"La contrasena no vale: {PasswordRule}, y no puede estar vacia.");
        if (database is not null && (!IsValidName(database) || SystemSchemas.Contains(database)))
            throw new RoostException($"'{database}' no vale como base de datos.");

        var scope = database is null ? "*.*" : $"`{database}`.*";
        Run(db, iniPath, $"CREATE USER '{user}'@'{AnyHost}' IDENTIFIED BY '{password}'; GRANT ALL PRIVILEGES ON {scope} TO '{user}'@'{AnyHost}'",
            runner, $"No se pudo crear el usuario {user}", TimeSpan.FromSeconds(30));
    }

    /// <summary>Cambia la contraseña de un usuario (de root no: eso es <see cref="ChangeRootPassword"/>).</summary>
    public static void SetUserPassword(InstalledDb db, string iniPath, string user, string host, string password, IProcessRunner runner)
    {
        CheckAccount(user, host);
        if (password.Length == 0 || !IsValidPassword(password))
            throw new RoostException($"La contrasena no vale: {PasswordRule}, y no puede estar vacia.");

        Run(db, iniPath, $"ALTER USER '{user}'@'{host}' IDENTIFIED BY '{password}'", runner,
            $"No se pudo cambiar la contrasena de {user}", TimeSpan.FromSeconds(30));
    }

    /// <summary>Borra la cuenta (y con ella sus permisos). Las bases de datos se quedan.</summary>
    public static void DropUser(InstalledDb db, string iniPath, string user, string host, IProcessRunner runner)
    {
        CheckAccount(user, host);
        Run(db, iniPath, $"DROP USER '{user}'@'{host}'", runner, $"No se pudo borrar el usuario {user}", TimeSpan.FromSeconds(30));
    }

    private static void CheckAccount(string user, string host)
    {
        if (!IsValidUserName(user))
            throw new RoostException($"'{user}' no vale como usuario: usa solo letras, numeros y _ (hasta 32).");
        if (SystemUsers.Contains(user))
            throw new RoostException(user.Equals(RootUser, StringComparison.OrdinalIgnoreCase)
                ? "root se cambia aparte: roost db password (o «Cambiar…» en la tarjeta de conexion)."
                : $"{user} es una cuenta del servidor: no se toca.");
        if (!IsValidHost(host))
            throw new RoostException($"'{host}' no vale como host de la cuenta.");
    }

    // --- Tras instalar ---

    /// <summary>
    /// Pregunta al servidor recién instalado su versión. Si no responde, casi siempre falta un
    /// Visual C++ Redistributable (MySQL 5.7 se compiló con Visual Studio 2013; el resto, con 2015-2022).
    /// </summary>
    public static DbSmokeResult SmokeTest(InstalledDb db, IProcessRunner runner)
    {
        var output = runner.Run(db.Server, "--version", TimeSpan.FromSeconds(15));
        var line = output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Ver", StringComparison.Ordinal));
        if (line is not null) return new DbSmokeResult(line, null, null);

        return db.Engine == DbEngine.MySql && db.Version is { Major: 5 }
            ? new DbSmokeResult(null, "Visual C++ 2013 (x64)", "https://aka.ms/highdpimfc2013x64enu")
            : new DbSmokeResult(null, "Visual C++ 2015-2022 (x64)", "https://aka.ms/vs/17/release/vc_redist.x64.exe");
    }

    /// <summary>Los argumentos de la consola SQL interactiva (opcionalmente ya dentro de <paramref name="database"/>).</summary>
    public static string ConsoleArguments(string iniPath, string? database) =>
        database is null ? $"--defaults-file=\"{iniPath}\"" : $"--defaults-file=\"{iniPath}\" {database}";

    private const string Ok = "roost-ok";

    /// <summary>
    /// Ejecuta el SQL y confirma con un <c>SELECT 'roost-ok'</c> final, porque
    /// <see cref="IProcessRunner"/> no da el código de salida; sin él, lanza con lo que dijo el cliente.
    /// </summary>
    private static string Run(InstalledDb db, string iniPath, string sql, IProcessRunner runner, string what, TimeSpan timeout)
    {
        var output = runner.Run(db.Client, Arguments(iniPath, $"{sql}; SELECT '{Ok}'"), timeout);
        if (output is null || !output.Contains(Ok, StringComparison.Ordinal))
            throw new RoostException($"{what}: {Describe(output)}");
        return output;
    }

    private static string Describe(string? output) =>
        string.IsNullOrWhiteSpace(output) ? "el cliente no respondio" : output.Trim();

    private static string Arguments(string iniPath, string sql) => $"--defaults-file=\"{iniPath}\" -N -e \"{sql}\"";
}
