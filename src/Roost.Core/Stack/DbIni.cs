using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Stack;

/// <summary>
/// El my.ini que genera Roost para la base de datos global (<c>etc/&lt;motor&gt;/&lt;rama&gt;/my.ini</c>):
/// solo en 127.0.0.1, utf8mb4 y root con la contraseña que tenga guardada Roost (ninguna, el
/// primer día), como en Laragon (ROOST.md §8). Lo leen el servidor (<c>--defaults-file</c>) y
/// también el cliente y el admin, por la sección <c>[client]</c>. Al final incluye
/// <c>usuario.cnf</c>, el fichero del usuario, que Roost crea una vez y no vuelve a tocar.
/// </summary>
public static class DbIni
{
    /// <summary>Los ajustes del usuario, junto al my.ini: lo que ponga ahí manda sobre lo generado.</summary>
    public const string UserFileName = "usuario.cnf";

    public const string UserFileTemplate = """
        # Tus ajustes de la base de datos. Roost no toca este fichero: el my.ini de al lado lo
        # incluye al final, asi que lo que pongas aqui manda sobre lo generado.
        # Se aplican al reiniciar la base de datos (Reiniciar en la app, o roost restart).
        [mysqld]
        # Proyectos viejos que chocan con el modo estricto (WordPress antiguo, PHP legacy):
        # sql_mode = NO_ENGINE_SUBSTITUTION
        # Volcados grandes que no entran:
        # max_allowed_packet = 256M
        # Mas memoria para InnoDB si la maquina la tiene de sobra:
        # innodb_buffer_pool_size = 1G

        """;

    /// <summary>PURO: genera el texto del my.ini.</summary>
    /// <param name="password">La contraseña de root para la sección <c>[client]</c>; null o vacía = sin contraseña.</param>
    /// <param name="userFile">El fichero del usuario que se incluye al final; null = sin <c>!include</c>.</param>
    /// <param name="socket">
    /// Linux: el socket Unix del servidor (<see cref="SocketPath"/>), en el <c>[mysqld]</c> y en el <c>[client]</c>;
    /// sin él, el servidor abriría <c>/tmp/mysql.sock</c> y chocaría con cualquier otro MySQL de la máquina.
    /// null = sin socket (Windows). El cliente sigue entrando por TCP (<c>host = 127.0.0.1</c>).
    /// </param>
    public static string Render(DbEngine engine, ComponentVersion version, string basedir, string datadir, int port,
        string? password = null, string? userFile = null, string? socket = null)
    {
        // MySQL 8 abre además el protocolo X en el 33060; la 5.7 y MariaDB no conocen la opción.
        var mysqlx = engine == DbEngine.MySql && version.Major >= 8 ? "mysqlx = OFF\n" : "";
        var serverSocket = socket is null ? "" : $"socket = \"{Slashes(socket)}\"\n";
        // Entre comillas, un # dentro de la contraseña no empieza un comentario; las comillas, la
        // barra invertida y los espacios no llegan aquí (DbAdmin.IsValidPassword).
        var client = string.IsNullOrEmpty(password) ? "" : $"password = \"{password}\"\n";
        var include = userFile is null ? "" : $"\n# Tus ajustes (sql_mode, max_allowed_packet...): en este fichero, que Roost no toca.\n!include {Slashes(userFile)}\n";
        return $"""
            # Generado por Roost para {engine.DisplayName()} {version}. NO lo edites: se regenera en cada `roost up`.
            [mysqld]
            basedir = "{Slashes(basedir)}"
            datadir = "{Slashes(datadir)}"
            port = {port}
            bind-address = 127.0.0.1
            {serverSocket}character-set-server = utf8mb4
            collation-server = utf8mb4_unicode_ci
            {mysqlx}
            [client]
            host = 127.0.0.1
            port = {port}
            {serverSocket}user = root
            {client}{include}
            """;
    }

    /// <summary>Linux: el socket Unix de la base de datos de una rama, en <c>run/</c>: <c>run/mariadb-11.4.sock</c>.</summary>
    public static string SocketPath(RoostPaths paths, InstalledDb db) => Path.Combine(paths.Run, $"{db.Engine.Key()}-{db.Branch}.sock");

    /// <summary>my.ini de la base de datos de una rama: <c>etc/&lt;motor&gt;/&lt;rama&gt;/my.ini</c> (lo mismo que <c>StackPlanner.DbIniPath</c>).</summary>
    public static string PathFor(RoostPaths paths, InstalledDb db) => Path.Combine(paths.Etc, db.Engine.Key(), db.Branch!, "my.ini");

    /// <summary>El fichero de ajustes del usuario, junto al my.ini.</summary>
    public static string UserFilePath(RoostPaths paths, InstalledDb db) => Path.Combine(paths.Etc, db.Engine.Key(), db.Branch!, UserFileName);

    /// <summary>Los datos de la rama: <c>data/&lt;motor&gt;/&lt;rama&gt;</c>.</summary>
    public static string DataDir(RoostPaths paths, InstalledDb db) => Path.Combine(paths.Data, db.Engine.Key(), db.Branch!);

    /// <summary>
    /// Escribe el my.ini de la instalación (y crea <c>usuario.cnf</c> con ejemplos si no existe)
    /// y devuelve su ruta. Lo hace <c>PrepareDb</c> en cada <c>up</c>, y también el cambio de
    /// contraseña de root, para que el <c>[client]</c> lleve la nueva al momento.
    /// </summary>
    public static string Write(RoostPaths paths, InstalledDb db, int port, string? password)
    {
        if (db.Version is not { } version || db.Branch is null)
            throw new RoostException($"No se reconoce la version de la instalacion {db.Label}.");

        var ini = PathFor(paths, db);
        var userFile = UserFilePath(paths, db);
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        if (!File.Exists(userFile)) File.WriteAllText(userFile, UserFileTemplate);
        var socket = OperatingSystem.IsWindows() ? null : SocketPath(paths, db);
        File.WriteAllText(ini, Render(db.Engine, version, db.Directory, DataDir(paths, db), port, password, userFile, socket));
        return ini;
    }

    /// <summary>En los ficheros de opciones '\' es un carácter de escape: rutas con '/'.</summary>
    public static string Slashes(string path) => path.Replace('\\', '/');
}
