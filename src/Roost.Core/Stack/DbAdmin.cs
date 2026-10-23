using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Scanning;

namespace Roost.Core.Stack;

/// <summary>
/// Lo que se hace dentro del servidor en marcha: crear y listar bases de datos. Usa el cliente
/// de la propia instalación (<c>mariadb.exe</c> / <c>mysql.exe</c>) con su my.ini, como root sin
/// contraseña. Lo usan <c>roost db create</c> y la app.
/// </summary>
public static partial class DbAdmin
{
    /// <summary>Las del propio servidor, que no se enseñan como «tus» bases de datos.</summary>
    public static readonly IReadOnlySet<string> SystemSchemas =
        new HashSet<string>(["information_schema", "mysql", "performance_schema", "sys"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex NamePattern();

    /// <summary>Solo letras, números y _ (hasta 64): así el nombre no necesita escaparse en el SQL.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>Crea la base de datos (utf8mb4) si no existe. Lanza <see cref="RoostException"/> si no se puede.</summary>
    public static void Create(InstalledDb db, string iniPath, string name, IProcessRunner runner)
    {
        if (!IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre: usa solo letras, numeros y _ (hasta 64).");

        // El SELECT final confirma que todo fue bien: ProcessRunner no da el código de salida.
        var sql = $"CREATE DATABASE IF NOT EXISTS `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; SELECT 'roost-ok'";
        var output = runner.Run(db.Client, Arguments(iniPath, sql), TimeSpan.FromSeconds(30));
        if (output is null || !output.Contains("roost-ok", StringComparison.Ordinal))
            throw new RoostException($"No se pudo crear {name}: {output?.Trim() ?? "el cliente no respondio"}");
    }

    /// <summary>Las bases de datos del usuario (sin las del sistema), por nombre. Lanza si el cliente no responde.</summary>
    public static IReadOnlyList<string> List(InstalledDb db, string iniPath, IProcessRunner runner)
    {
        var output = runner.Run(db.Client, Arguments(iniPath, "SHOW DATABASES; SELECT 'roost-ok'"), TimeSpan.FromSeconds(15));
        if (output is null || !output.Contains("roost-ok", StringComparison.Ordinal))
            throw new RoostException($"No se pudo leer la lista de bases de datos: {output?.Trim() ?? "el cliente no respondio"}");

        return output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line != "roost-ok" && IsValidName(line) && !SystemSchemas.Contains(line))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Los argumentos de la consola SQL interactiva (opcionalmente ya dentro de <paramref name="database"/>).</summary>
    public static string ConsoleArguments(string iniPath, string? database) =>
        database is null ? $"--defaults-file=\"{iniPath}\"" : $"--defaults-file=\"{iniPath}\" {database}";

    private static string Arguments(string iniPath, string sql) => $"--defaults-file=\"{iniPath}\" -N -e \"{sql}\"";
}
