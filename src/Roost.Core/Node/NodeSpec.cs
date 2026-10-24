using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Models;

namespace Roost.Core.Node;

/// <summary>
/// Un rango de versiones como los de npm (<c>engines.node</c>, y lo que admiten nvm y fnm en un
/// <c>.nvmrc</c>): alternativas separadas por <c>||</c>, y en cada una restricciones separadas por
/// espacios que tienen que cumplirse todas. Entiende <c>22</c>, <c>v22.11</c>, <c>22.11.0</c>,
/// <c>22.x</c>, <c>*</c>, <c>^20.11</c>, <c>~22.1</c>, <c>&gt;=18 &lt;23</c>, <c>18 - 20</c> y
/// <c>20 || 22</c>. Sin prereleases: Roost solo instala versiones publicadas. PURO.
/// </summary>
public sealed partial class NodeRange
{
    private enum Op { Lt, Le, Gt, Ge, Eq }

    private readonly IReadOnlyList<IReadOnlyList<(Op Op, ComponentVersion Version)>> _sets;

    private NodeRange(string text, IReadOnlyList<IReadOnlyList<(Op, ComponentVersion)>> sets)
    {
        Text = text;
        _sets = sets;
    }

    /// <summary>Tal como se escribió.</summary>
    public string Text { get; }

    /// <summary>La versión exacta si el rango es una sola (<c>22.11.0</c>, <c>=v22.11.0</c>); null si no.</summary>
    public ComponentVersion? Exact =>
        _sets is [[(Op.Eq, var v)]] ? v : null;

    public bool Satisfies(ComponentVersion version) =>
        _sets.Any(set => set.All(c => c.Op switch
        {
            Op.Lt => version < c.Version,
            Op.Le => version <= c.Version,
            Op.Gt => version > c.Version,
            Op.Ge => version >= c.Version,
            _ => version.CompareTo(c.Version) == 0,
        }));

    public override string ToString() => Text;

    [GeneratedRegex(@"^\s*(\S+)\s+-\s+(\S+)\s*$")]
    private static partial Regex HyphenPattern();

    [GeneratedRegex(@"(<=|>=|<|>|=|\^|~)\s+")]
    private static partial Regex OperatorSpacePattern();

    [GeneratedRegex(@"^(<=|>=|<|>|=|\^|~>?)?[vV=]?(\*|[xX]|\d+)(?:\.(\*|[xX]|\d+))?(?:\.(\*|[xX]|\d+))?(?:[-+][0-9A-Za-z.\-+]*)?$")]
    private static partial Regex ComparatorPattern();

    /// <summary>Lee un rango; false si no se entiende (no adivina).</summary>
    public static bool TryParse(string? text, out NodeRange range)
    {
        range = null!;
        if (text is null) return false;
        var trimmed = text.Trim();
        var sets = new List<IReadOnlyList<(Op, ComponentVersion)>>();
        foreach (var raw in trimmed.Split("||"))
        {
            var part = raw.Trim();
            var set = new List<(Op, ComponentVersion)>();
            if (HyphenPattern().Match(part) is { Success: true } hyphen)
            {
                if (!Partial(hyphen.Groups[1].Value, out var from) || !Partial(hyphen.Groups[2].Value, out var to)) return false;
                if (from.Major is { } fm) set.Add((Op.Ge, Low(fm, from.Minor, from.Patch)));
                if (to.Major is { } tm) set.Add(to.Patch is not null ? (Op.Le, new ComponentVersion(tm, to.Minor!.Value, to.Patch.Value, "")) : (Op.Lt, Next(tm, to.Minor)));
            }
            else if (part.Length > 0)
            {
                foreach (var token in OperatorSpacePattern().Replace(part, "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (!Comparator(token, set)) return false;
            }
            // Una alternativa vacía (o «*») es «cualquiera»: un conjunto sin restricciones.
            sets.Add(set);
        }
        if (sets.Count == 0) return false;
        range = new NodeRange(trimmed, sets);
        return true;
    }

    private readonly record struct Parts(int? Major, int? Minor, int? Patch);

    private static bool Partial(string text, out Parts parts)
    {
        parts = default;
        var m = ComparatorPattern().Match(text);
        if (!m.Success || m.Groups[1].Success) return false;
        parts = new Parts(Number(m.Groups[2]), Number(m.Groups[3]), Number(m.Groups[4]));
        return Valid(parts);
    }

    private static int? Number(Group group) =>
        group.Success && int.TryParse(group.Value, out var n) ? n : null;

    // «1.x.3» no vale: lo que va detrás de un comodín tiene que ser comodín.
    private static bool Valid(Parts p) => !(p.Major is null && (p.Minor is not null || p.Patch is not null)) && !(p.Minor is null && p.Patch is not null);

    private static ComponentVersion Low(int major, int? minor, int? patch) => new(major, minor ?? 0, patch ?? 0, "");

    /// <summary>La primera versión que ya no entra en <c>major[.minor]</c>.</summary>
    private static ComponentVersion Next(int major, int? minor) => minor is { } m ? new(major, m + 1, 0, "") : new(major + 1, 0, 0, "");

    private static bool Comparator(string token, List<(Op, ComponentVersion)> set)
    {
        var m = ComparatorPattern().Match(token);
        if (!m.Success) return false;
        var op = m.Groups[1].Success ? m.Groups[1].Value : "";
        var p = new Parts(Number(m.Groups[2]), Number(m.Groups[3]), Number(m.Groups[4]));
        if (!Valid(p)) return false;
        if (p.Major is not { } major)
        {
            // «*», «x», «>=*»: cualquiera. «<*» y «>*» no casan con nada (como npm).
            if (op is "<" or ">") set.Add((Op.Lt, new ComponentVersion(0, 0, 0, "")));
            return true;
        }

        switch (op)
        {
            case "" or "=":
                if (p.Patch is { } patch) set.Add((Op.Eq, new ComponentVersion(major, p.Minor!.Value, patch, "")));
                else
                {
                    set.Add((Op.Ge, Low(major, p.Minor, null)));
                    set.Add((Op.Lt, Next(major, p.Minor)));
                }
                return true;
            case "^":
                set.Add((Op.Ge, Low(major, p.Minor, p.Patch)));
                // ^1.2.3 hasta la 2; ^0.2.3 hasta la 0.3; ^0.0.3 solo la 0.0.3 (como npm).
                set.Add((Op.Lt, major > 0 || p.Minor is null ? new ComponentVersion(major + 1, 0, 0, "")
                    : p.Minor > 0 || p.Patch is null ? new ComponentVersion(0, p.Minor.Value + 1, 0, "")
                    : new ComponentVersion(0, 0, p.Patch.Value + 1, "")));
                return true;
            case "~" or "~>":
                set.Add((Op.Ge, Low(major, p.Minor, p.Patch)));
                set.Add((Op.Lt, Next(major, p.Minor)));
                return true;
            case ">":
                set.Add(p.Patch is { } gt ? (Op.Gt, new ComponentVersion(major, p.Minor!.Value, gt, "")) : (Op.Ge, Next(major, p.Minor)));
                return true;
            case ">=":
                set.Add((Op.Ge, Low(major, p.Minor, p.Patch)));
                return true;
            case "<":
                set.Add((Op.Lt, Low(major, p.Minor, p.Patch)));
                return true;
            case "<=":
                set.Add(p.Patch is { } le ? (Op.Le, new ComponentVersion(major, p.Minor!.Value, le, "")) : (Op.Lt, Next(major, p.Minor)));
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Qué clase de cosa pide una versión de Node.</summary>
public enum NodeSpecKind
{
    /// <summary>Una versión o un rango: <c>22</c>, <c>22.11.0</c>, <c>^20.11</c>, <c>&gt;=18</c>.</summary>
    Range,

    /// <summary><c>lts/*</c> (o <c>lts</c>): la LTS más nueva.</summary>
    LtsLatest,

    /// <summary><c>lts/jod</c>: la LTS con ese nombre (22).</summary>
    LtsCodename,

    /// <summary><c>lts/-1</c>: la LTS de antes de la más nueva.</summary>
    LtsOffset,

    /// <summary><c>node</c>, <c>latest</c>, <c>current</c> o <c>stable</c>: la más nueva.</summary>
    Newest,

    /// <summary><c>system</c>: el Node que ya tenga la máquina (nvm). Roost no lo usa.</summary>
    System,
}

/// <summary>
/// Lo que pide un <c>.nvmrc</c>, un <c>.node-version</c>, <c>engines.node</c> o <c>roost node
/// install|use</c>: una versión o un rango (<see cref="NodeRange"/>), o los alias de nvm y fnm
/// (<c>lts/*</c>, <c>lts/jod</c>, <c>lts/-1</c>, <c>node</c>, <c>system</c>). PURO; las LTS salen
/// del calendario de <see cref="NodeCatalog.Branches"/>.
/// </summary>
public sealed record NodeSpec
{
    private NodeSpec(string text, NodeSpecKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }

    public NodeSpecKind Kind { get; }

    public NodeRange? Range { get; private init; }

    /// <summary>Con <see cref="NodeSpecKind.LtsCodename"/>: la versión mayor de esa LTS.</summary>
    public int? CodenameMajor { get; private init; }

    /// <summary>Con <see cref="NodeSpecKind.LtsOffset"/>: cuántas LTS hacia atrás (1 en <c>lts/-1</c>).</summary>
    public int Offset { get; private init; }

    /// <summary>La versión exacta que pide (<c>22.11.0</c>), o null.</summary>
    public ComponentVersion? Exact => Range?.Exact;

    public override string ToString() => Text;

    /// <summary>Lee lo que se pide; lanza <see cref="RoostException"/> si no se entiende.</summary>
    public static NodeSpec Parse(string? text) =>
        TryParse(text, out var spec, out var error) ? spec : throw new RoostException(error!);

    public static bool TryParse(string? text, out NodeSpec spec, out string? error)
    {
        spec = null!;
        error = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            error = "Falta la version de Node (22, 24.22.1, lts/*).";
            return false;
        }

        var lower = value.ToLowerInvariant();
        switch (lower)
        {
            case "lts" or "lts/*" or "lts/latest":
                spec = new NodeSpec(value, NodeSpecKind.LtsLatest);
                return true;
            case "node" or "latest" or "current" or "stable":
                spec = new NodeSpec(value, NodeSpecKind.Newest);
                return true;
            case "system":
                spec = new NodeSpec(value, NodeSpecKind.System);
                return true;
        }

        if (lower.StartsWith("lts/", StringComparison.Ordinal))
        {
            var rest = lower[4..];
            if (rest.StartsWith('-') && int.TryParse(rest[1..], out var back) && back >= 0)
            {
                spec = new NodeSpec(value, NodeSpecKind.LtsOffset) { Offset = back };
                return true;
            }
            if (NodeCatalog.MajorOfCodename(rest) is { } major)
            {
                spec = new NodeSpec(value, NodeSpecKind.LtsCodename) { CodenameMajor = major };
                return true;
            }
            error = $"'{value}' no es una LTS de Node que Roost conozca (las LTS se llaman jod, krypton...; o lts/* para la mas nueva).";
            return false;
        }

        // «iojs» y compañía: nada que ver con Node actual.
        if (lower.StartsWith("iojs", StringComparison.Ordinal))
        {
            error = $"'{value}' es io.js: Roost solo instala Node.";
            return false;
        }

        if (!NodeRange.TryParse(value, out var range))
        {
            error = $"'{value}' no es una version de Node que Roost entienda (22, 22.11.0, ^20.11, >=18, lts/*).";
            return false;
        }
        spec = new NodeSpec(value, NodeSpecKind.Range) { Range = range };
        return true;
    }

    /// <summary>true si <paramref name="version"/> vale para lo que se pide (<see cref="NodeSpecKind.System"/>: nunca).</summary>
    public bool Matches(ComponentVersion version, DateOnly today) => Kind switch
    {
        NodeSpecKind.Range => Range!.Satisfies(version),
        NodeSpecKind.LtsLatest => NodeCatalog.IsLts(version.Major, today),
        NodeSpecKind.LtsCodename => version.Major == CodenameMajor,
        NodeSpecKind.LtsOffset => version.Major == OffsetMajor(today),
        NodeSpecKind.Newest => true,
        _ => false,
    };

    /// <summary>
    /// De <paramref name="versions"/>, la que se usa: la más nueva que vale. <c>lts/*</c> es la de la
    /// LTS más nueva que haya, como en nvm (pero entre las que se le dan, no las de internet).
    /// </summary>
    public ComponentVersion? Pick(IEnumerable<ComponentVersion> versions, DateOnly today)
    {
        var matching = versions.Where(v => Matches(v, today)).OrderByDescending(v => v).ToList();
        return matching.Count == 0 ? null : matching[0];
    }

    /// <summary>La versión mayor de la LTS <see cref="Offset"/> veces anterior a la más nueva, según el calendario.</summary>
    private int? OffsetMajor(DateOnly today)
    {
        var lts = NodeCatalog.Branches.Where(b => b.IsLtsOn(today)).Select(b => b.Major).OrderByDescending(m => m).ToList();
        return Offset < lts.Count ? lts[Offset] : null;
    }
}
