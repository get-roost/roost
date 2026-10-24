using System.Globalization;
using Roost.Core.Stack;

namespace Roost.Core.Audit;

/// <summary>
/// Lo que PHP aplica de verdad, sacado de <c>php -i</c> (K4): el valor local de cada directiva y los
/// ficheros que ha leído, en orden (el php.ini y los de <c>PHP_INI_SCAN_DIR</c>). Es lo que manda,
/// mejor que juntar los ficheros a mano: un <c>99-usuario.ini</c> que pisa algo, o una directiva que
/// el build no conoce, salen solos.
/// </summary>
public sealed record PhpIniSnapshot(IReadOnlyDictionary<string, string> Values, string? LoadedFile, IReadOnlyList<string> AdditionalFiles)
{
    /// <summary>Los ficheros en el orden en que PHP los lee (lo de después manda).</summary>
    public IReadOnlyList<string> Files => LoadedFile is null ? AdditionalFiles : [LoadedFile, .. AdditionalFiles];

    /// <summary>
    /// PURO: la salida de <c>php -i</c> (CLI, texto). Cada directiva es «clave => local => maestro»;
    /// «no value» es vacío. «Additional .ini files parsed» va partido en varias líneas que acaban en
    /// coma. Null si no parece un phpinfo (php.exe no ha arrancado).
    /// </summary>
    public static PhpIniSnapshot? Parse(string? phpI)
    {
        if (string.IsNullOrWhiteSpace(phpI) || !phpI.Contains("PHP Version", StringComparison.Ordinal)) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? loaded = null;
        var additional = new List<string>();
        var lines = phpI.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var parts = line.Split(" => ");
            if (parts.Length == 2 && parts[0] == "Loaded Configuration File")
            {
                loaded = parts[1].Trim() is "(none)" or "" ? null : parts[1].Trim();
                continue;
            }
            if (parts.Length == 2 && parts[0] == "Additional .ini files parsed")
            {
                var text = parts[1].Trim();
                while (text.EndsWith(',') && i + 1 < lines.Count) text += lines[++i].Trim();
                if (text != "(none)")
                    additional.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                continue;
            }
            if (parts.Length != 3 || parts[0].Length == 0 || parts[0].Contains(' ') || parts[0] == "Directive") continue;
            values.TryAdd(parts[0].Trim(), parts[1].Trim() == "no value" ? "" : parts[1].Trim());
        }
        return new PhpIniSnapshot(values, loaded, additional);
    }
}

/// <summary>
/// PURO: si una mitigación se cumple con el valor que aplica PHP. Los sí/no se comparan por lo que
/// significan (On, 1, true y yes son lo mismo; Off, 0, false, no y vacío, también), los tamaños en
/// bytes (128M es 134217728) y lo demás como texto, sin distinguir mayúsculas.
/// </summary>
public static class MitigationCheck
{
    /// <summary>
    /// Lo que el PHP de la terminal fija siempre, diga lo que diga el php.ini: desde ahí no se puede
    /// saber lo que aplica el servidor web.
    /// </summary>
    public static readonly IReadOnlySet<string> CliForced = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "html_errors", "implicit_flush", "max_execution_time", "register_argc_argv", "output_buffering", "max_input_time",
    };

    public static MitigationState Check(Mitigation mitigation, PhpIniSnapshot snapshot)
    {
        if (CliForced.Contains(mitigation.Ini))
            return new MitigationState(mitigation, MitigationStatus.Unknown, null, "el PHP de la terminal fija este ajuste siempre: desde aqui no se puede comprobar");
        if (!snapshot.Values.TryGetValue(mitigation.Ini, out var effective))
            return new MitigationState(mitigation, MitigationStatus.Unknown, null, "php -i no da esta directiva (puede que la extension no este cargada)");
        return Satisfies(mitigation.Compare, mitigation.Value, effective) switch
        {
            true => new MitigationState(mitigation, MitigationStatus.Mitigated, effective),
            false => new MitigationState(mitigation, MitigationStatus.Unmitigated, effective),
            null => new MitigationState(mitigation, MitigationStatus.Unknown, effective, $"no se puede comparar '{effective}' con '{mitigation.Value}'"),
        };
    }

    /// <summary>true si <paramref name="effective"/> cumple; null si no se pueden comparar.</summary>
    public static bool? Satisfies(string? compare, string expected, string effective)
    {
        var want = Unquote(expected);
        var have = Unquote(effective);
        switch ((compare ?? "=").Trim().ToLowerInvariant())
        {
            case "=":
                if (Flag(want) is { } w && Flag(have) is { } h) return w == h;
                if (Size(want) is { } ws && Size(have) is { } hs) return ws == hs;
                return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
            case "<=":
                return Order(want, have) is { } c ? c <= 0 : null;
            case ">=":
                return Order(want, have) is { } c2 ? c2 >= 0 : null;
            case "includes":
                var items = List(have);
                return List(want).All(items.Contains);
            default:
                return null;
        }
    }

    /// <summary>El efectivo frente al esperado: negativo si es menor. «-1» (sin límite) es más que todo.</summary>
    private static int? Order(string want, string have)
    {
        if (have == "-1") return want == "-1" ? 0 : 1;
        if (want == "-1") return -1;
        if (Size(want) is { } ws && Size(have) is { } hs) return hs.CompareTo(ws);
        if (double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var wn)
            && double.TryParse(have, NumberStyles.Float, CultureInfo.InvariantCulture, out var hn))
            return hn.CompareTo(wn);
        return null;
    }

    private static readonly string[] On = ["1", "on", "true", "yes"];
    private static readonly string[] Off = ["0", "off", "false", "no", "none", ""];

    private static bool? Flag(string value) =>
        On.Contains(value, StringComparer.OrdinalIgnoreCase) ? true
        : Off.Contains(value, StringComparer.OrdinalIgnoreCase) ? false
        : null;

    /// <summary>
    /// Un tamaño de php.ini en bytes (<c>128M</c>, <c>1g</c>, <c>134217728</c>), o null. Como
    /// <see cref="PhpSettings.Bytes"/>, pero sin su tope de 7 cifras: <c>php -i</c> puede dar bytes.
    /// </summary>
    private static long? Size(string value)
    {
        if (value.Length is 0 or > 16) return null;
        var unit = char.ToUpperInvariant(value[^1]);
        var digits = unit is 'K' or 'M' or 'G' ? value[..^1] : value;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return null;
        try
        {
            return checked(unit switch { 'K' => n * 1024, 'M' => n * 1024 * 1024, 'G' => n * 1024 * 1024 * 1024, _ => n });
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static HashSet<string> List(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Unquote(string value)
    {
        var v = value.Trim();
        return v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;
    }
}
