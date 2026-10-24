using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Los motores de base de datos que instala Roost.</summary>
public enum DbEngine
{
    MariaDb,
    MySql,
}

/// <summary>Lo que cambia entre motores: nombres, ejecutables y cómo se inicializa un datadir.</summary>
public static class DbEngines
{
    public static readonly IReadOnlyList<DbEngine> All = [DbEngine.MariaDb, DbEngine.MySql];

    /// <summary>Clave en <c>motor@versión</c>, en <c>roost.toml</c> y en las rutas: "mariadb", "mysql".</summary>
    public static string Key(this DbEngine engine) => engine == DbEngine.MariaDb ? "mariadb" : "mysql";

    public static string DisplayName(this DbEngine engine) => engine == DbEngine.MariaDb ? "MariaDB" : "MySQL";

    public static ComponentKind Kind(this DbEngine engine) => engine == DbEngine.MariaDb ? ComponentKind.MariaDb : ComponentKind.MySql;

    /// <summary>El servidor: <c>mariadbd.exe</c> (nombre moderno, desde la 10.5) o <c>mysqld.exe</c>.</summary>
    public static string ServerExe(this DbEngine engine) => OsNames.Exe(engine == DbEngine.MariaDb ? "mariadbd" : "mysqld");

    /// <summary>La herramienta con la que se le pide un apagado limpio (<c>shutdown</c>).</summary>
    public static string AdminExe(this DbEngine engine) => OsNames.Exe(engine == DbEngine.MariaDb ? "mariadb-admin" : "mysqladmin");

    /// <summary>El cliente de línea de comandos.</summary>
    public static string ClientExe(this DbEngine engine) => OsNames.Exe(engine == DbEngine.MariaDb ? "mariadb" : "mysql");

    /// <summary>El que vuelca una base de datos a SQL (BD-2): <c>mariadb-dump.exe</c> o <c>mysqldump.exe</c>.</summary>
    public static string DumpExe(this DbEngine engine) => OsNames.Exe(engine == DbEngine.MariaDb ? "mariadb-dump" : "mysqldump");

    public static bool TryParse(string? text, out DbEngine engine)
    {
        engine = default;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "mariadb": engine = DbEngine.MariaDb; return true;
            case "mysql": engine = DbEngine.MySql; return true;
            default: return false;
        }
    }
}

/// <summary>Un zip de MariaDB o MySQL para Windows, con el SHA-256 que tiene que dar.</summary>
public sealed record DbBuild
{
    public required DbEngine Engine { get; init; }

    public required ComponentVersion Version { get; init; }

    /// <summary>Rama: "11.4", "8.4". Los datos van por rama (ROOST.md §8).</summary>
    public required string Branch { get; init; }

    /// <summary>true si la rama es de soporte largo (las LTS de MariaDB, MySQL 8.4).</summary>
    public bool Lts { get; init; }

    public required string FileName { get; init; }

    /// <summary>URLs en orden de preferencia; vale la primera que no dé 404.</summary>
    public required IReadOnlyList<Uri> Urls { get; init; }

    /// <summary>SHA-256 en hexadecimal y minúsculas.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Carpeta de instalación bajo <c>bin/&lt;motor&gt;</c>: la versión, <c>11.4.13</c>.</summary>
    public string InstallName => Version.ToString();

    /// <summary>Como lo escribe el usuario: <c>mariadb@11.4</c>.</summary>
    public string Spec => $"{Engine.Key()}@{Branch}";

    public string Label => $"{Engine.DisplayName()} {Version}";
}

/// <summary>Una base de datos instalada por Roost en <c>bin/&lt;motor&gt;/&lt;versión&gt;</c>.</summary>
public sealed record InstalledDb(DbEngine Engine, string Name, string Directory)
{
    public ComponentVersion? Version => ComponentVersion.TryParse(Name, out var v) ? v : null;

    public string? Branch => Version is { } v ? $"{v.Major}.{v.Minor}" : null;

    public string Server => Path.Combine(Directory, "bin", Engine.ServerExe());
    public string Admin => Path.Combine(Directory, "bin", Engine.AdminExe());
    public string Client => Path.Combine(Directory, "bin", Engine.ClientExe());
    public string Dump => Path.Combine(Directory, "bin", Engine.DumpExe());

    /// <summary>Como lo escribe el usuario: <c>mariadb@11.4.13</c>.</summary>
    public string Spec => $"{Engine.Key()}@{Name}";

    public string Label => $"{Engine.DisplayName()} {Name}";
}
