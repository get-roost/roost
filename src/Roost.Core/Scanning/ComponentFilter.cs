using Roost.Core.Models;

namespace Roost.Core.Scanning;

/// <summary>
/// <c>--only php,mysql</c> de <c>roost audit</c> y <c>roost scan</c>: qué tipos de componente
/// cuentan (puro). Sirve, sobre todo, en CI: la imagen <c>windows-latest</c> de GitHub trae su
/// propio MySQL 8.0, ya fuera de soporte, y un proyecto PHP no debería fallar por él.
/// </summary>
public sealed class ComponentFilter
{
    /// <summary>Los nombres que se admiten, en el orden en que se enseñan.</summary>
    public static readonly IReadOnlyList<(string Name, ComponentKind Kind)> Names =
    [
        ("php", ComponentKind.Php),
        ("mysql", ComponentKind.MySql),
        ("mariadb", ComponentKind.MariaDb),
        ("nginx", ComponentKind.Nginx),
        ("apache", ComponentKind.Apache),
    ];

    private readonly HashSet<ComponentKind> _kinds;

    private ComponentFilter(HashSet<ComponentKind> kinds) => _kinds = kinds;

    /// <summary>Los tipos elegidos, como se escriben («php», «mysql»), en el orden de <see cref="Names"/>.</summary>
    public IReadOnlyList<string> Keys => Names.Where(n => _kinds.Contains(n.Kind)).Select(n => n.Name).ToList();

    public bool Includes(ComponentKind kind) => _kinds.Contains(kind);

    /// <summary>Lo que cuenta de <paramref name="components"/>, en el mismo orden.</summary>
    public IReadOnlyList<DetectedComponent> Apply(IEnumerable<DetectedComponent> components)
        => components.Where(c => Includes(c.Kind)).ToList();

    /// <summary>
    /// «php», «MySQL,mariadb», «php, mysql»… Vacío o null: sin filtro (<paramref name="filter"/>
    /// null, y devuelve true). Un nombre que no existe: false y el motivo en
    /// <paramref name="error"/> (texto para el usuario, sin tildes).
    /// </summary>
    public static bool TryParse(string? text, out ComponentFilter? filter, out string? error)
    {
        filter = null;
        error = null;
        var parts = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return true;

        var kinds = new HashSet<ComponentKind>();
        var unknown = new List<string>();
        foreach (var part in parts)
        {
            var match = Names.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null) unknown.Add(part);
            else kinds.Add(match.Kind);
        }
        if (unknown.Count > 0)
        {
            error = $"--only solo admite {string.Join(", ", Names.Select(n => n.Name))} (separados por comas), no: {string.Join(", ", unknown)}";
            return false;
        }
        filter = new ComponentFilter(kinds);
        return true;
    }
}
