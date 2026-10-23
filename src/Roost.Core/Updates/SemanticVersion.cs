using System.Diagnostics.CodeAnalysis;

namespace Roost.Core.Updates;

/// <summary>
/// Una versión de Roost en SemVer 2.0 (<c>0.2.0</c>, <c>0.2.0-beta.1</c>), que es como se
/// etiquetan las releases (<c>v0.2.0</c>). La precedencia es la de SemVer: una prerelease va
/// antes que su versión final (<c>0.2.0-beta.1</c> &lt; <c>0.2.0</c>), sus identificadores
/// numéricos se comparan como números (<c>beta.2</c> &lt; <c>beta.10</c>) y la metadata de
/// build (<c>+abc123</c>) no cuenta. No sirve para PHP ni MySQL: eso es <c>ComponentVersion</c>.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease = "") : IComparable<SemanticVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>
    /// Parsea con algo de manga ancha: acepta la <c>v</c> de las etiquetas, versiones de una o
    /// dos cifras (<c>1.2</c> es <c>1.2.0</c>) y descarta lo que va tras <c>+</c>. Rechaza lo que
    /// no es una versión (<c>dev</c>, <c>1.2.x</c>, identificadores vacíos o con ceros a la izquierda).
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : "";

        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!IsNumeric(parts[i]) || !int.TryParse(parts[i], out numbers[i])) return false;

        if (dash >= 0)
        {
            foreach (var id in pre.Split('.'))
            {
                if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                if (IsNumeric(id) && id.Length > 1 && id[0] == '0') return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"No es una version: {text}");

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // Sin prerelease gana: 0.2.0 > 0.2.0-rc.1.
        if (!IsPrerelease || !other.IsPrerelease) return other.IsPrerelease.CompareTo(IsPrerelease);

        var mine = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            c = CompareIdentifier(mine[i], theirs[i]);
            if (c != 0) return c;
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    // Numérico contra numérico, como números; numérico siempre antes que alfanumérico; si no, ASCII.
    private static int CompareIdentifier(string a, string b)
    {
        var an = IsNumeric(a);
        var bn = IsNumeric(b);
        if (an && bn)
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        if (an != bn) return an ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool IsNumeric(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
