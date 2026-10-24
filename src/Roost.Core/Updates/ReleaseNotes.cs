using System.Text;

namespace Roost.Core.Updates;

public enum NotesBlockKind
{
    Heading,
    Paragraph,
    Bullet,
    Code,
}

/// <summary>Un trozo de las notas ya legible: título, párrafo, viñeta (con su nivel) o bloque de código.</summary>
public sealed record NotesBlock(NotesBlockKind Kind, string Text, int Level = 0);

/// <summary>
/// Las notas de una release (Markdown de GitHub) convertidas a bloques para enseñarlas con
/// formato en la app, sin un motor de Markdown: títulos, párrafos (las líneas seguidas se juntan),
/// viñetas con su sangría, y los bloques de código tal cual. La sintaxis en línea (negritas,
/// código, enlaces) se quita y queda el texto; las tablas se saltan. Si las notas traen
/// <see cref="EndMarker"/> (lo pone el workflow Release tras la sección del CHANGELOG), lo de
/// después (descargas, firma, lo que añade GitHub) no son novedades y no se enseña. PURO.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>Hasta aquí, las novedades: lo escribe <c>.github/workflows/release.yml</c> en las notas.</summary>
    public const string EndMarker = "<!-- roost:fin-novedades -->";

    public static IReadOnlyList<NotesBlock> Parse(string? markdown)
    {
        var blocks = new List<NotesBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var paragraph = new StringBuilder();
        StringBuilder? code = null;
        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, paragraph.ToString().Trim()));
            paragraph.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (code is null)
                {
                    FlushParagraph();
                    code = new StringBuilder();
                }
                else
                {
                    blocks.Add(new NotesBlock(NotesBlockKind.Code, code.ToString().TrimEnd()));
                    code = null;
                }
                continue;
            }
            if (code is not null)
            {
                code.AppendLine(line);
                continue;
            }

            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal)) break;
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            // Las tablas (la de descargas) no se leen bien sin rejilla: fuera.
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                continue;
            }
            // Comentarios HTML, etiquetas sueltas (<details>, <img …>) y separadores: no son texto.
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal) || (trimmed.StartsWith('<') && trimmed.EndsWith('>'))) continue;
            if (trimmed.Length >= 3 && trimmed.All(c => c is '-' or '=' or '*' or '_' or ' ')) continue;

            if (trimmed.StartsWith('#'))
            {
                FlushParagraph();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var text = GitHubReleases.Plain(trimmed.TrimStart('#').Trim().TrimEnd('#').Trim());
                if (text.Length > 0) blocks.Add(new NotesBlock(NotesBlockKind.Heading, text, Math.Min(level, 3)));
                continue;
            }

            var indent = line.Length - trimmed.Length;
            if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                FlushParagraph();
                var text = GitHubReleases.Plain(trimmed[2..].Trim());
                if (text.Length > 0) blocks.Add(new NotesBlock(NotesBlockKind.Bullet, text, Math.Min(indent / 2, 2)));
                continue;
            }
            if (NumberedPrefix(trimmed) is > 0 and var skip)
            {
                FlushParagraph();
                var text = GitHubReleases.Plain(trimmed[skip..].Trim());
                if (text.Length > 0) blocks.Add(new NotesBlock(NotesBlockKind.Bullet, text, Math.Min(indent / 2, 2)));
                continue;
            }

            var plain = GitHubReleases.Plain(trimmed.StartsWith('>') ? trimmed.TrimStart('>').Trim() : trimmed);
            if (plain.Length == 0) continue;
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(plain);
        }

        if (code is not null) blocks.Add(new NotesBlock(NotesBlockKind.Code, code.ToString().TrimEnd()));
        FlushParagraph();
        return blocks;
    }

    // «1. texto» o «12) texto»: cuántos caracteres ocupa el número con su separador (0 si no lo es).
    private static int NumberedPrefix(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == 0 || i > 3 || i + 1 >= line.Length) return 0;
        return line[i] is '.' or ')' && line[i + 1] == ' ' ? i + 2 : 0;
    }
}
