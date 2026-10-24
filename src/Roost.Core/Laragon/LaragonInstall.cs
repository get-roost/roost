using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Stack;

namespace Roost.Core.Laragon;

/// <summary>
/// Un servidor de base de datos de Laragon: una carpeta de <c>bin\mysql</c> (Laragon pone ahí
/// también las MariaDB) o de <c>bin\mariadb</c>, con su datadir y su puerto.
/// </summary>
/// <param name="Folder">La carpeta de la versión: <c>C:\laragon\bin\mysql\mysql-8.4.3-winx64</c>.</param>
/// <param name="DataDir">Sus datos (del <c>my.ini</c> de la versión, o los de siempre de Laragon); null si no se encuentran.</param>
/// <param name="Active">La que usa Laragon (<c>usr\laragon.ini</c>).</param>
public sealed record LaragonDbServer(DbEngine Engine, ComponentVersion? Version, string Folder, string? DataDir, int Port, bool Active)
{
    public string BinDir => Path.Combine(Folder, "bin");

    /// <summary>El servidor: <c>mariadbd.exe</c> si está (MariaDB 10.5 y siguientes) o <c>mysqld.exe</c>.</summary>
    public string Server => Engine == DbEngine.MariaDb && File.Exists(Path.Combine(BinDir, "mariadbd.exe"))
        ? Path.Combine(BinDir, "mariadbd.exe")
        : Path.Combine(BinDir, "mysqld.exe");

    public string Name => Path.GetFileName(Folder);

    public string Label => $"{Engine.DisplayName()}{(Version is { } v ? " " + v : "")}";
}

/// <summary>Una base de datos de Laragon, leída de su carpeta en el datadir (sin arrancar nada).</summary>
/// <param name="Bytes">Lo que ocupan sus ficheros (aproximado: con InnoDB sin un fichero por tabla, lo gordo está en ibdata1).</param>
/// <param name="Importable">false si el nombre no vale en Roost (solo letras, números y _).</param>
public sealed record LaragonDatabase(string Name, long Bytes, bool Importable, LaragonDbServer Server);

/// <summary>
/// Una instalación de Laragon, solo para leer: dónde está, sus servidores de base de datos y lo
/// que hay en sus datadirs (BD-2, la bienvenida). Nada de aquí escribe en sus carpetas. Lo usa la
/// bienvenida («Importar desde Laragon…»), que antes la sacaba de la ruta de los PHP que encontraba
/// la auditoría. Para probar sin el Laragon del usuario, <see cref="RootVariable"/> apunta a uno de
/// pega con la misma estructura (<c>bin\mysql\…</c>, <c>data\mysql-8</c>, <c>usr\laragon.ini</c>).
/// </summary>
public sealed partial record LaragonInstall(string Root)
{
    /// <summary>La raíz de Laragon que se usa en vez de buscarla (pruebas).</summary>
    public const string RootVariable = "ROOST_LARAGON_DIR";

    /// <summary>El puerto de MySQL de siempre, si ni Laragon ni su my.ini dicen otro.</summary>
    public const int DefaultPort = 3306;

    public string Www => Path.Combine(Root, "www");

    public string Data => Path.Combine(Root, "data");

    /// <summary>El <c>laragon.ini</c> de sus preferencias (la versión de MySQL que usa y su puerto).</summary>
    public string IniFile => Path.Combine(Root, "usr", "laragon.ini");

    /// <summary>
    /// La raíz de Laragon: la de <see cref="RootVariable"/> si existe y, si no, la que sale de la
    /// ruta de un binario suyo (<c>C:\laragon\bin\php\…</c> → <c>C:\laragon</c>). null si no hay.
    /// </summary>
    public static LaragonInstall? Locate(IEnumerable<string> executablePaths, Func<string, string?>? env = null)
    {
        var forced = (env ?? Environment.GetEnvironmentVariable)(RootVariable);
        if (!string.IsNullOrWhiteSpace(forced))
            return Directory.Exists(forced) ? new LaragonInstall(Path.GetFullPath(forced)) : null;
        var root = executablePaths
            .Select(p => RootFromBinary().Match(p))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return root is null ? null : new LaragonInstall(root);
    }

    /// <summary>
    /// Sus servidores de base de datos, la que usa Laragon primero y luego de la más nueva a la
    /// más vieja. Nunca lanza: una carpeta rara se salta.
    /// </summary>
    public IReadOnlyList<LaragonDbServer> DbServers()
    {
        var ini = ReadIni(IniFile);
        var active = ini.TryGetValue("mysql", out var mysql) && mysql.TryGetValue("Version", out var v) ? v : null;
        var activePort = mysql is not null && mysql.TryGetValue("Port", out var p) && int.TryParse(p, out var port) ? port : (int?)null;

        var servers = new List<LaragonDbServer>();
        foreach (var parent in new[] { Path.Combine(Root, "bin", "mysql"), Path.Combine(Root, "bin", "mariadb") })
        {
            IEnumerable<string> folders;
            try { folders = Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in folders)
            {
                var bin = Path.Combine(folder, "bin");
                var name = Path.GetFileName(folder);
                var mariaDb = name.StartsWith("mariadb", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(bin, "mariadbd.exe"))
                              || parent.EndsWith("mariadb", StringComparison.OrdinalIgnoreCase);
                if (!File.Exists(Path.Combine(bin, "mysqld.exe")) && !File.Exists(Path.Combine(bin, "mariadbd.exe"))) continue;

                var engine = mariaDb ? DbEngine.MariaDb : DbEngine.MySql;
                ComponentVersion? version = VersionInName().Match(name) is { Success: true } m && ComponentVersion.TryParse(m.Value, out var parsed) ? parsed : null;
                var myIni = ReadIni(Path.Combine(folder, "my.ini"));
                var isActive = active is not null && active.Equals(name, StringComparison.OrdinalIgnoreCase);
                var portFromIni = myIni.TryGetValue("mysqld", out var mysqld) && mysqld.TryGetValue("port", out var ip) && int.TryParse(ip, out var iniPort) ? iniPort : (int?)null;
                var dataDir = DataDirFor(engine, version, mysqld);
                servers.Add(new LaragonDbServer(engine, version, folder, dataDir, portFromIni ?? (isActive ? activePort : null) ?? DefaultPort, isActive));
            }
        }

        // Sin laragon.ini: la más nueva con datos hace de «la que usa».
        if (!servers.Any(s => s.Active) && servers.FirstOrDefault(s => s.DataDir is not null) is not null)
        {
            var newest = servers.Where(s => s.DataDir is not null).OrderByDescending(s => s.Version).First();
            servers[servers.IndexOf(newest)] = newest with { Active = true };
        }
        return servers.OrderByDescending(s => s.Active).ThenByDescending(s => s.Version).ToList();
    }

    /// <summary>
    /// Las bases de datos del usuario en el datadir de ese servidor: sus carpetas, sin las del
    /// sistema ni las de trabajo (<c>#innodb_temp</c>…), con el nombre ya decodificado
    /// (<c>mi@002dtienda</c> → <c>mi-tienda</c>). Nunca lanza.
    /// </summary>
    public static IReadOnlyList<LaragonDatabase> Databases(LaragonDbServer server)
    {
        if (server.DataDir is not { } dir || !Directory.Exists(dir)) return [];
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Select(d => (Dir: d, Name: DecodeName(Path.GetFileName(d))))
                .Where(x => !x.Name.StartsWith('#') && !x.Name.StartsWith('.') && !DbAdmin.SystemSchemas.Contains(x.Name)
                            && !x.Name.Equals("lost+found", StringComparison.OrdinalIgnoreCase))
                .Select(x => new LaragonDatabase(x.Name, SizeOf(x.Dir), DbAdmin.IsValidName(x.Name), server))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>PURO: el nombre de una carpeta de base de datos tal como lo codifica MySQL (<c>@002d</c> = <c>-</c>).</summary>
    public static string DecodeName(string folder) =>
        EncodedChar().Replace(folder, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>
    /// El datadir: el del <c>my.ini</c> de la versión si existe y, si no, el primero de los de
    /// siempre de Laragon que tenga la base de datos <c>mysql</c> dentro.
    /// </summary>
    private string? DataDirFor(DbEngine engine, ComponentVersion? version, Dictionary<string, string>? mysqld)
    {
        if (mysqld is not null && mysqld.TryGetValue("datadir", out var configured) && configured.Trim().Trim('"', '\'') is { Length: > 0 } path)
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            full = Path.GetFullPath(full.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(full)) return full;
        }
        var major = version?.Major;
        var candidates = engine == DbEngine.MySql
            ? new[] { $"mysql-{major}", "mysql" }
            : new[] { $"mariadb-{major}", "mariadb", "mysql" };
        return candidates
            .Select(c => Path.Combine(Data, c))
            .FirstOrDefault(c => Directory.Exists(Path.Combine(c, "mysql")));
    }

    private static long SizeOf(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Sum(f => new FileInfo(f).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>PURO: un .ini en secciones (claves sin distinguir mayúsculas); lo que no se entiende se salta.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        result[""] = current;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim();
                if (!result.TryGetValue(section, out current!))
                    result[section] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string file)
    {
        try { return File.Exists(file) ? ParseIni(File.ReadAllText(file)) : new(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    [GeneratedRegex(@"^(.*?\\laragon)\\bin\\", RegexOptions.IgnoreCase)]
    private static partial Regex RootFromBinary();

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionInName();

    [GeneratedRegex("@([0-9a-fA-F]{4})")]
    private static partial Regex EncodedChar();
}
