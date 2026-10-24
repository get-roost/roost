using System.Globalization;
using System.Text.RegularExpressions;

namespace Roost.Core.Git;

/// <summary>Un commit, lo justo para enseñarlo: sha, sha corto, autor, fecha y asunto.</summary>
public sealed record GitCommit(string Sha, string ShortSha, string Author, DateTimeOffset When, string Subject);

/// <summary>Lo que cuenta <c>ls-remote --symref</c>: la rama por defecto del remoto y sus ramas.</summary>
public sealed record RemoteRefs(string? DefaultBranch, IReadOnlyList<string> Branches);

/// <summary>
/// Lo que cuenta <c>status --porcelain=v2 --branch</c>. <see cref="Changed"/> son los ficheros con
/// cambios seguidos por git (modificados, renombrados, borrados, en conflicto); los sin seguir van
/// aparte, en <see cref="Untracked"/>, y no bloquean nada (un <c>.env</c> ignorado ni sale).
/// </summary>
public sealed record RepoStatus(string? Branch, string? Oid, string? Upstream, int Ahead, int Behind, IReadOnlyList<string> Changed, IReadOnlyList<string> Untracked)
{
    public bool IsDetached => Branch is null;

    /// <summary>true si no hay cambios en ficheros que git sigue (los sin seguir no cuentan).</summary>
    public bool IsClean => Changed.Count == 0;

    public bool HasUpstream => Upstream is not null;
}

/// <summary>Una línea de progreso de <c>clone --progress</c> o <c>fetch --progress</c>, ya entendida.</summary>
/// <param name="Stage">La fase, en castellano: «Recibiendo objetos», «Resolviendo deltas»…</param>
/// <param name="Percent">El porcentaje de esa fase.</param>
/// <param name="Overall">El porcentaje del clonado entero (las fases van ponderadas: recibir objetos es lo que tarda).</param>
/// <param name="Detail">Lo de después del contador: <c>12.40 MiB | 3.10 MiB/s</c>, o null.</param>
public sealed record CloneProgress(string Stage, int Percent, int Overall, string? Detail);

/// <summary>Lo que dejó un pull: de qué commit a cuál, y cuántos ficheros cambiaron.</summary>
public sealed record PullResult(string From, string To, int ChangedFiles)
{
    public bool Changed => !string.Equals(From, To, StringComparison.Ordinal);
}

/// <summary>Los parsers de las salidas de git (PUROS, probados con salidas reales grabadas).</summary>
public static partial class GitOutput
{
    public const string LogFormat = "%H%x1f%h%x1f%an%x1f%aI%x1f%s";

    private const string HeadsPrefix = "refs/heads/";

    /// <summary>
    /// <c>ls-remote --symref &lt;url&gt; HEAD refs/heads/*</c>: la línea <c>ref: refs/heads/main\tHEAD</c>
    /// da la rama por defecto, y cada <c>&lt;sha&gt;\trefs/heads/x</c>, una rama. Las etiquetas y los
    /// <c>^{}</c> no cuentan.
    /// </summary>
    public static RemoteRefs ParseLsRemote(string stdout)
    {
        string? defaultBranch = null;
        var branches = new List<string>();
        foreach (var raw in Lines(stdout))
        {
            var parts = raw.Split('\t', 2);
            if (parts.Length != 2) continue;
            var (left, right) = (parts[0].Trim(), parts[1].Trim());
            if (left.StartsWith("ref: ", StringComparison.Ordinal) && right == "HEAD" && left.AsSpan(5).StartsWith(HeadsPrefix))
            {
                defaultBranch = left[(5 + HeadsPrefix.Length)..];
            }
            else if (right.StartsWith(HeadsPrefix, StringComparison.Ordinal) && !right.EndsWith("^{}", StringComparison.Ordinal))
            {
                var name = right[HeadsPrefix.Length..];
                if (!branches.Contains(name)) branches.Add(name);
            }
        }
        return new RemoteRefs(defaultBranch, branches);
    }

    /// <summary><c>status --porcelain=v2 --branch</c>.</summary>
    public static RepoStatus ParseStatusV2(string stdout)
    {
        string? branch = null, oid = null, upstream = null;
        var ahead = 0;
        var behind = 0;
        var changed = new List<string>();
        var untracked = new List<string>();

        foreach (var line in Lines(stdout))
        {
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var value = line["# branch.oid ".Length..].Trim();
                oid = value == "(initial)" ? null : value;
            }
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var value = line["# branch.head ".Length..].Trim();
                branch = value == "(detached)" ? null : value;
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line["# branch.upstream ".Length..].Trim();
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var ab = AheadBehind().Match(line);
                if (ab.Success)
                {
                    ahead = int.Parse(ab.Groups["a"].Value, CultureInfo.InvariantCulture);
                    behind = int.Parse(ab.Groups["b"].Value, CultureInfo.InvariantCulture);
                }
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal))
            {
                // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                var fields = line.Split(' ', 9);
                if (fields.Length == 9) changed.Add(fields[8]);
            }
            else if (line.StartsWith("2 ", StringComparison.Ordinal))
            {
                // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>\t<origPath>
                var fields = line.Split(' ', 10);
                if (fields.Length == 10) changed.Add(fields[9].Split('\t', 2)[0]);
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                var fields = line.Split(' ', 11);
                if (fields.Length == 11) changed.Add(fields[10]);
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                untracked.Add(line[2..]);
            }
        }

        return new RepoStatus(branch, oid, upstream, ahead, behind, changed, untracked);
    }

    /// <summary><c>log --format=&lt;<see cref="LogFormat"/>&gt;</c>: un commit por línea, con los campos separados por <c>\x1f</c>.</summary>
    public static IReadOnlyList<GitCommit> ParseLog(string stdout)
    {
        var commits = new List<GitCommit>();
        foreach (var line in Lines(stdout))
        {
            var parts = line.Split('\x1f');
            if (parts.Length < 5) continue;
            if (!DateTimeOffset.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
            commits.Add(new GitCommit(parts[0], parts[1], parts[2], when, string.Join('\x1f', parts[4..]).TrimEnd('\r')));
        }
        return commits;
    }

    /// <summary><c>branch -r --format=%(refname:short)</c>: las ramas de <c>origin</c>, sin el prefijo y sin su HEAD.</summary>
    public static IReadOnlyList<string> ParseRemoteBranches(string stdout)
    {
        var branches = new List<string>();
        foreach (var line in Lines(stdout))
        {
            var name = line.Trim();
            if (!name.StartsWith("origin/", StringComparison.Ordinal)) continue;
            name = name["origin/".Length..];
            if (name == "HEAD" || name.Length == 0 || branches.Contains(name)) continue;
            branches.Add(name);
        }
        return branches;
    }

    /// <summary>
    /// Una línea de <c>--progress</c> (<c>Receiving objects:  40% (512/1284), 12.40 MiB | 3.10 MiB/s</c>),
    /// o null si la línea no es de progreso. Las fases del servidor (<c>remote: Counting…</c>) valen
    /// poco en el total; recibir objetos es casi todo.
    /// </summary>
    public static CloneProgress? ParseProgress(string line)
    {
        var match = ProgressPattern().Match(line);
        if (!match.Success) return null;

        var percent = Math.Clamp(int.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture), 0, 100);
        var (stage, from, to) = match.Groups["stage"].Value switch
        {
            "Enumerating objects" => ("Contando objetos", 0, 2),
            "Counting objects" => ("Contando objetos", 0, 3),
            "Compressing objects" => ("Comprimiendo objetos", 3, 5),
            "Receiving objects" => ("Recibiendo objetos", 5, 85),
            "Resolving deltas" => ("Resolviendo deltas", 85, 95),
            "Filtering content" => ("Filtrando contenido", 95, 97),
            _ => ("Actualizando ficheros", 95, 100), // Updating files / Checking out files
        };
        var rest = match.Groups["rest"].Value.Trim().TrimStart(',').Trim();
        if (rest.Equals("done.", StringComparison.Ordinal)) rest = "";
        if (rest.EndsWith(", done.", StringComparison.Ordinal)) rest = rest[..^", done.".Length];
        return new CloneProgress(stage, percent, from + (to - from) * percent / 100, rest.Length == 0 ? null : rest);
    }

    /// <summary>La última línea con contenido de un stderr, preferiblemente una de <c>fatal:</c> o <c>error:</c>.</summary>
    public static string Headline(string stderr)
    {
        var lines = Lines(stderr).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.LastOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
            ?? lines.LastOrDefault(l => !l.StartsWith("hint:", StringComparison.Ordinal))
            ?? lines.LastOrDefault()
            ?? "";
    }

    internal static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    [GeneratedRegex(@"^# branch\.ab \+(?<a>\d+) -(?<b>\d+)")]
    private static partial Regex AheadBehind();

    [GeneratedRegex(@"^(?:remote: )?(?<stage>Enumerating objects|Counting objects|Compressing objects|Receiving objects|Resolving deltas|Updating files|Checking out files|Filtering content):\s*(?<pct>\d+)%\s*\((?<done>\d+)/(?<total>\d+)\)(?<rest>.*)$")]
    private static partial Regex ProgressPattern();
}
