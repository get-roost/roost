using System.Text.RegularExpressions;

namespace Roost.Core.Models;

/// <summary>
/// Versión semántica simplificada (Major.Minor.Patch) más el texto crudo del que
/// se extrajo. Se compara por número, NUNCA por texto: 8.3.9 &lt; 8.3.36.
/// Ese detalle es el corazón del audit (comparar contra la versión parcheada).
/// </summary>
public readonly record struct ComponentVersion(int Major, int Minor, int Patch, string Raw)
    : IComparable<ComponentVersion>
{
    private static readonly Regex Pattern = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>Extrae la primera X.Y.Z que aparezca en <paramref name="text"/>.</summary>
    public static bool TryParse(string? text, out ComponentVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern.Match(text);
        if (!m.Success) return false;
        version = new ComponentVersion(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value),
            text.Trim());
        return true;
    }

    public int CompareTo(ComponentVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ComponentVersion a, ComponentVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ComponentVersion a, ComponentVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ComponentVersion a, ComponentVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ComponentVersion a, ComponentVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
