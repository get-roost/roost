using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Deploy;

/// <summary>
/// Mira cómo está la base de datos de un sitio antes de proponer los pasos: si existe y cuántas
/// tablas tiene (así «migrate» va marcado solo si está vacía). Con el cliente de la propia
/// instalación y su my.ini, como <see cref="DbAdmin"/>. Nunca lanza: lo que no puede mirar queda
/// como <see cref="DbState.Unknown"/>.
/// </summary>
public static class DeployProbe
{
    private const string Ok = "roost-ok";

    /// <summary>La base de datos con su <see cref="DeployDatabase.State"/> y sus tablas (fuera del hilo de quien llama).</summary>
    public static Task<DeployDatabase> ProbeAsync(DeployDatabase db, IProcessRunner runner, CancellationToken cancellation = default) =>
        Task.Run(() => Probe(db, runner), cancellation);

    /// <summary>Lo mismo, en este hilo.</summary>
    public static DeployDatabase Probe(DeployDatabase db, IProcessRunner runner)
    {
        if (db.State == DbState.Stopped || !DbAdmin.IsValidName(db.Name)) return db;
        // El nombre ya está validado ([A-Za-z0-9_]): va tal cual entre comillas simples.
        var sql = $"SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '{db.Name}'; "
                  + $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{db.Name}'; SELECT '{Ok}'";
        var output = runner.Run(db.Db.Client, $"--defaults-file=\"{db.IniPath}\" -N -e \"{sql}\"", TimeSpan.FromSeconds(15));
        return Parse(output) is { } found
            ? db with { State = !found.Exists ? DbState.Missing : found.Tables == 0 ? DbState.Empty : DbState.HasTables, Tables = found.Tables }
            : db with { State = DbState.Unknown };
    }

    /// <summary>PURO: lo que contesta el cliente a la consulta de <see cref="Probe"/> (dos números y la marca), o null si no se entiende.</summary>
    public static (bool Exists, int Tables)? Parse(string? output)
    {
        if (output is null || !output.Contains(Ok, StringComparison.Ordinal)) return null;
        var numbers = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.All(char.IsAsciiDigit)).ToList();
        if (numbers.Count < 2) return null;
        return (int.Parse(numbers[0]) > 0, int.Parse(numbers[1]));
    }
}
