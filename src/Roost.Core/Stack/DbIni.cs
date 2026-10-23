using Roost.Core.Install;
using Roost.Core.Models;

namespace Roost.Core.Stack;

/// <summary>
/// El my.ini que genera Roost para la base de datos global (<c>etc/&lt;motor&gt;/&lt;rama&gt;/my.ini</c>):
/// solo en 127.0.0.1, utf8mb4 y root sin contraseña, como en Laragon (ROOST.md §8). Lo leen el
/// servidor (<c>--defaults-file</c>) y también el cliente y el admin, por la sección <c>[client]</c>.
/// </summary>
public static class DbIni
{
    /// <summary>PURO: genera el texto del my.ini.</summary>
    public static string Render(DbEngine engine, ComponentVersion version, string basedir, string datadir, int port)
    {
        // MySQL 8 abre además el protocolo X en el 33060; la 5.7 y MariaDB no conocen la opción.
        var mysqlx = engine == DbEngine.MySql && version.Major >= 8 ? "mysqlx = OFF\n" : "";
        return $"""
            # Generado por Roost para {engine.DisplayName()} {version}. NO lo edites: se regenera en cada `roost up`.
            [mysqld]
            basedir = "{Slashes(basedir)}"
            datadir = "{Slashes(datadir)}"
            port = {port}
            bind-address = 127.0.0.1
            character-set-server = utf8mb4
            collation-server = utf8mb4_unicode_ci
            {mysqlx}
            [client]
            host = 127.0.0.1
            port = {port}
            user = root

            """;
    }

    /// <summary>En los ficheros de opciones '\' es un carácter de escape: rutas con '/'.</summary>
    public static string Slashes(string path) => path.Replace('\\', '/');
}
