using Roost.Core.Models;

namespace Roost.Core.Audit;

/// <summary>
/// Evalua rangos de versiones del dataset ("&gt;=8.3.0 &lt;8.3.36", "&lt;8.2.0").
/// Un rango es una conjuncion (AND) de restricciones separadas por espacios;
/// la version lo cumple si satisface TODAS. Comparacion SIEMPRE por numero,
/// reutilizando <see cref="ComponentVersion"/> del core (8.3.9 &lt; 8.3.36).
/// </summary>
public static class VersionRange
{
    public static bool Satisfies(ComponentVersion version, string range)
    {
        if (string.IsNullOrWhiteSpace(range)) return false;
        foreach (var token in range.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ConstraintMatches(version, token)) return false;
        }
        return true;
    }

    private static bool ConstraintMatches(ComponentVersion version, string token)
    {
        string op;
        string text;
        if (token.StartsWith(">=", StringComparison.Ordinal)) { op = ">="; text = token[2..]; }
        else if (token.StartsWith("<=", StringComparison.Ordinal)) { op = "<="; text = token[2..]; }
        else if (token.StartsWith("==", StringComparison.Ordinal)) { op = "=="; text = token[2..]; }
        else if (token.StartsWith(">", StringComparison.Ordinal)) { op = ">"; text = token[1..]; }
        else if (token.StartsWith("<", StringComparison.Ordinal)) { op = "<"; text = token[1..]; }
        else if (token.StartsWith("=", StringComparison.Ordinal)) { op = "=="; text = token[1..]; }
        else { op = "=="; text = token; }

        if (!ComponentVersion.TryParse(text, out var target)) return false;

        var c = version.CompareTo(target);
        return op switch
        {
            ">=" => c >= 0,
            "<=" => c <= 0,
            ">" => c > 0,
            "<" => c < 0,
            "==" => c == 0,
            _ => false,
        };
    }
}
