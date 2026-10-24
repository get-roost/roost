using System.Text;
using System.Text.RegularExpressions;
using Roost.Core.Audit;

namespace Roost.Core.Stack;

/// <summary>
/// Las mitigaciones que aplica la auditoría (K4), en un fichero de Roost en la conf.d de cada rama:
/// <c>etc/php/conf.d/&lt;rama&gt;/50-roost-mitigaciones.ini</c>. PHP lo lee después del php.ini generado
/// y de <c>conf.d/99-usuario.ini</c>, y antes de <c>conf.d/&lt;rama&gt;/99-usuario.ini</c>: lo que ponga el
/// usuario en el de su rama sigue mandando, y nunca se escribe en los suyos. Cada línea lleva encima
/// los avisos por los que está, para poder deshacerla. Vale para todas las instalaciones de la rama.
/// </summary>
public static partial class PhpMitigations
{
    public const string FileName = "50-roost-mitigaciones.ini";

    /// <summary>Una línea del fichero: el ajuste, su valor y los avisos por los que está («CVE-2026-109619»).</summary>
    public sealed record Entry(string Key, string Value, string Reason);

    public static string PathFor(StackPlanner planner, Install.InstalledPhp php) => Path.Combine(planner.PhpBranchConfD(php), FileName);

    /// <summary>
    /// true si Roost puede escribirla: un nombre de directiva normal y un valor sin saltos de línea,
    /// comillas ni nada que el php.ini interprete (<c>;</c>, <c>$</c>, <c>{</c>…). Viene del feed firmado,
    /// pero un php.ini roto deja sin PHP a todo el pool.
    /// </summary>
    public static bool IsWritable(Mitigation mitigation) =>
        IniKey().IsMatch(mitigation.Ini) && SafeValue().IsMatch(mitigation.Value.Trim())
        && Mitigation.CompareModes.Contains(mitigation.Compare ?? "=", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PURO: lo que se escribe para cumplirla: el valor tal cual, salvo en <c>includes</c>, que es lo
    /// que ya había (<paramref name="effective"/>) más lo que falta («exec» en un disable_functions que
    /// ya tenía «system» da «system,exec»).
    /// </summary>
    public static string ValueToWrite(Mitigation mitigation, string? effective)
    {
        if (!string.Equals(mitigation.Compare, "includes", StringComparison.OrdinalIgnoreCase)) return mitigation.Value.Trim();
        var items = (effective ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var item in mitigation.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!items.Contains(item, StringComparer.OrdinalIgnoreCase)) items.Add(item);
        return string.Join(',', items);
    }

    /// <summary>PURO: las líneas del fichero (lo que no es «clave = valor» se ignora).</summary>
    public static IReadOnlyList<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        string reason = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("; por: ", StringComparison.Ordinal))
            {
                reason = line["; por: ".Length..].Trim();
                continue;
            }
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            entries.RemoveAll(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            entries.Add(new Entry(key, value, reason));
            reason = "";
        }
        return entries;
    }

    /// <summary>PURO: el fichero entero, con su cabecera.</summary>
    public static string Render(string branch, IReadOnlyList<Entry> entries)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            ; Mitigaciones de la auditoria de Roost para PHP {branch} (roost audit --apply-mitigations,
            ; o "Aplicar" en la pantalla Auditoria). NO lo edites: Roost lo reescribe al aplicar o deshacer
            ; (roost audit --undo-mitigations). Se lee despues de conf.d\99-usuario.ini y antes de
            ; conf.d\{branch}\99-usuario.ini, que manda sobre esto. Son un tapon: actualizar PHP es el arreglo.

            """);
        foreach (var e in entries)
        {
            sb.Append('\n');
            if (e.Reason.Length > 0) sb.Append($"; por: {e.Reason}\n");
            sb.Append($"{e.Key} = {Quote(e.Value)}\n");
        }
        return sb.ToString();
    }

    public static IReadOnlyList<Entry> Read(string file)
    {
        try { return File.Exists(file) ? Parse(File.ReadAllText(file)) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Escribe (o borra, si no queda ninguna) el fichero de la rama, de una vez: a un temporal en la
    /// misma carpeta y renombrado, para que un pool que arranque a la vez no lea medio fichero.
    /// </summary>
    public static void Write(string file, string branch, IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, Render(branch, entries));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>PURO: las entradas con estas puestas (la misma clave se sustituye) o quitadas.</summary>
    public static IReadOnlyList<Entry> With(IReadOnlyList<Entry> entries, IEnumerable<Entry> added)
    {
        var list = entries.ToList();
        foreach (var e in added)
        {
            var old = list.FindIndex(x => x.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase));
            if (old >= 0) list[old] = e;
            else list.Add(e);
        }
        return list;
    }

    public static IReadOnlyList<Entry> Without(IReadOnlyList<Entry> entries, IEnumerable<string> keys)
    {
        var drop = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(e => !drop.Contains(e.Key)).ToList();
    }

    /// <summary>
    /// PURO: el último fichero de <paramref name="later"/> que pone <paramref name="key"/> (los que PHP lee
    /// después del de Roost), para decir quién pisa una mitigación aplicada. Null si ninguno.
    /// </summary>
    public static string? WhoOverrides(string key, IEnumerable<(string File, string Text)> later)
    {
        string? who = null;
        foreach (var (file, text) in later)
            if (Parse(text).Any(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                who = file;
        return who;
    }

    private static string Quote(string value) => BareValue().IsMatch(value) ? value : $"\"{value}\"";

    // Las directivas de PHP: letras, cifras, «_» y «.» (session.upload_progress.enabled).
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.]*$")]
    private static partial Regex IniKey();

    // Lo que se escribe sin comillas: sin espacios ni nada que el php.ini interprete.
    [GeneratedRegex(@"^[A-Za-z0-9_.,:/\\-]*$")]
    private static partial Regex BareValue();

    // Lo que se admite (entre comillas si hace falta): sin saltos de línea, comillas, «;», «$», llaves, «=»…
    [GeneratedRegex(@"^[A-Za-z0-9_.,:/\\\- ]*$")]
    private static partial Regex SafeValue();
}
