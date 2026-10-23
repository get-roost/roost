using System.Text;

namespace Roost.Core.Updates;

/// <summary>Lo que cambia en una versión: su número, la fecha (o «en preparación») y sus puntos.</summary>
public sealed record ChangelogEntry(string Version, string? Date, IReadOnlyList<string> Items)
{
    /// <summary>La sección tal cual, en Markdown, para las notas de la release.</summary>
    public string Markdown => string.Join("\n", Items.Select(i => "- " + i));
}

/// <summary>
/// Las novedades de cada versión, de <c>CHANGELOG.md</c> (en la raíz del repo, empaquetado en
/// Roost.Core). Lo enseña la pantalla Novedades y lo usan las notas de cada release. Cada versión
/// es un <c>## 0.1.3 — 24-oct-2026</c> seguido de sus puntos (<c>- …</c>); un test exige que la
/// versión que se compila tenga el suyo.
/// </summary>
public static class Changelog
{
    /// <summary>PURO: las versiones del Markdown, en el orden del fichero (la más nueva primero).</summary>
    public static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        string? version = null;
        string? date = null;
        var items = new List<string>();
        StringBuilder? item = null;

        void FlushItem()
        {
            if (item is not null) items.Add(item.ToString().Trim());
            item = null;
        }

        void FlushEntry()
        {
            FlushItem();
            if (version is not null) entries.Add(new ChangelogEntry(version, date, items.ToList()));
            items.Clear();
            version = null;
            date = null;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushEntry();
                var title = line[3..].Trim();
                var dash = title.IndexOfAny(['—', '–']);
                version = (dash < 0 ? title : title[..dash]).Trim().TrimStart('v');
                date = dash < 0 ? null : title[(dash + 1)..].Trim();
            }
            else if (version is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushItem();
                item = new StringBuilder(line[2..]);
            }
            else if (item is not null && line.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0)
            {
                item.Append(' ').Append(line.Trim());   // un punto que sigue en la línea de abajo
            }
        }
        FlushEntry();
        return entries;
    }

    /// <summary>Las novedades empaquetadas en esta Roost.</summary>
    public static IReadOnlyList<ChangelogEntry> Embedded()
    {
        var asm = typeof(Changelog).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
        if (name is null) return [];
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>La entrada de <paramref name="version"/> («0.1.3», «v0.1.3» o «0.1.3+abc»), o null.</summary>
    public static ChangelogEntry? For(IReadOnlyList<ChangelogEntry> entries, string version)
    {
        if (!SemanticVersion.TryParse(version, out var wanted)) return null;
        return entries.FirstOrDefault(e => SemanticVersion.TryParse(e.Version, out var v) && v == wanted);
    }
}
