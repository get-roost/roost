using System.Text;
using System.Text.RegularExpressions;

namespace Roost.Core.Sites;

/// <summary>
/// El bloque de Roost en el fichero <c>hosts</c> (ROOST.md §5.7): entre <c># roost:begin</c> y
/// <c># roost:end</c>, una línea <c>127.0.0.1 &lt;nombre&gt;.test</c> por nombre (cada sitio, sus
/// alias y sus subdominios, como <c>api.tienda.test</c>). Lo de fuera del bloque no se toca nunca.
///
/// Este fichero se compila TAMBIÉN dentro de roost-helper (el único ejecutable que corre con
/// privilegios), así que tiene que ser puro y no depender de nada más de Roost: es lo que decide
/// qué puede escribir el helper en el hosts del sistema.
/// </summary>
internal static partial class HostsBlock
{
    public const string Begin = "# roost:begin";
    public const string End = "# roost:end";

    /// <summary>Etiquetas como mucho delante de <c>.test</c>: <c>tienda.test</c> tiene una y <c>v1.api.tienda.test</c>, tres.</summary>
    public const int MaxLabels = 4;

    /// <summary>Longitud máxima de un nombre, la de DNS (RFC 1035).</summary>
    public const int MaxHostLength = 253;

    // Solo nombres .test de 1 a 4 etiquetas DNS en ASCII (minúsculas, números y guiones; sin guion al
    // principio ni al final): nada de dominios reales, ni IPs, ni espacios, ni saltos de línea.
    // \z y no $: en .NET, $ también casa antes de un \n final.
    [GeneratedRegex(@"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.){1,4}test\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    public static bool IsValidHost(string host) => host.Length <= MaxHostLength && HostPattern().IsMatch(host);

    /// <summary>Los nombres que hay ahora en el bloque de Roost (vacío si no hay bloque).</summary>
    public static IReadOnlyList<string> ReadHosts(string hosts)
    {
        var lines = SplitLines(hosts);
        var (begin, end) = FindBlock(lines);
        if (begin < 0) return [];

        return lines.Skip(begin + 1).Take(end - begin - 1)
            .Select(l => l.Split('#')[0].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[1])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// PURO: el hosts con el bloque de Roost reescrito con <paramref name="hostNames"/> (o sin
    /// bloque si no hay ninguno), en el mismo sitio y con los mismos saltos de línea. Lanza
    /// <see cref="ArgumentException"/> si algún nombre no es un <c>.test</c> válido, y
    /// <see cref="InvalidDataException"/> si el bloque está roto (empieza y no termina).
    /// </summary>
    public static string Apply(string hosts, IEnumerable<string> hostNames)
    {
        var names = hostNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (names.FirstOrDefault(n => !IsValidHost(n)) is { } bad)
            throw new ArgumentException($"'{bad}' no es un nombre .test valido.");

        var newline = hosts.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : (hosts.Length == 0 ? "\r\n" : "\n");
        var lines = SplitLines(hosts);
        var (begin, end) = FindBlock(lines);

        var block = new List<string>();
        if (names.Count > 0)
        {
            block.Add($"{Begin}  (lo gestiona Roost: no edites estas lineas)");
            block.AddRange(names.Select(n => $"127.0.0.1 {n}"));
            block.Add(End);
        }

        if (begin >= 0)
        {
            lines.RemoveRange(begin, end - begin + 1);
            lines.InsertRange(begin, block);
            // Al quitar el bloque del final, se va también la línea en blanco que lo separaba (la puso Roost):
            // poner y quitar deja el hosts como estaba, byte a byte.
            if (block.Count == 0 && begin == lines.Count && begin > 0 && lines[begin - 1].Trim().Length == 0)
                lines.RemoveAt(begin - 1);
        }
        else if (block.Count > 0)
        {
            // Al final, separado de lo anterior por una línea en blanco.
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(block);
        }

        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append(newline);
        return text.ToString();
    }

    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // el salto final
        return lines;
    }

    private static (int Begin, int End) FindBlock(List<string> lines)
    {
        var begin = lines.FindIndex(l => l.TrimStart().StartsWith(Begin, StringComparison.Ordinal));
        if (begin < 0) return (-1, -1);
        var end = lines.FindIndex(begin + 1, l => l.TrimStart().StartsWith(End, StringComparison.Ordinal));
        if (end < 0)
            throw new InvalidDataException($"El hosts tiene '{Begin}' sin '{End}': arreglalo a mano antes de que Roost lo toque.");
        return (begin, end);
    }
}
