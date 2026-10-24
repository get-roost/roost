using System.Text;
using System.Text.RegularExpressions;

namespace Roost.Core.Deploy;

/// <summary>Qué pasó con una clave al fusionar un .env (<see cref="EnvFile.Merge"/>).</summary>
public enum EnvChange
{
    /// <summary>No estaba: se ha añadido (o se ha descomentado la línea <c># CLAVE=…</c> que había).</summary>
    Added,

    /// <summary>Estaba con otro valor y se ha cambiado.</summary>
    Changed,

    /// <summary>Estaba con otro valor y se ha dejado el suyo (sin pisar).</summary>
    Kept,

    /// <summary>Ya tenía justo ese valor: nada que hacer.</summary>
    Same,
}

/// <summary>
/// Un <c>.env</c> (el de Laravel, el de Symfony: el formato de phpdotenv y de symfony/dotenv) leído
/// línea a línea para cambiar valores sin perder nada: los comentarios, las líneas en blanco, el
/// orden, cómo va entrecomillado cada valor, el <c>export</c> de delante, el comentario de detrás,
/// los finales de línea (LF o CRLF, línea a línea) y si acababa con salto de línea. Lo que no se
/// toca sale byte a byte como estaba. PURO: el fichero lo leen y lo escriben <see cref="Load"/> y
/// <see cref="Save"/>, que además conservan el BOM.
/// </summary>
/// <remarks>
/// Lo que entiende de cada línea:
/// <list type="bullet">
/// <item><c>CLAVE=valor</c>, con espacios alrededor del <c>=</c> o sin ellos, y con <c>export</c> delante;</item>
/// <item>valores sin comillas (un <c>#</c> pegado es parte del valor; con un espacio delante, empieza un comentario),
/// entre comillas dobles (con <c>\"</c>, <c>\\</c>, <c>\$</c>, <c>\n</c>…) o simples (literales), también de varias líneas;</item>
/// <item><c># CLAVE=valor</c>: una clave comentada, que <see cref="Set"/> descomenta en su sitio en vez
/// de añadirla al final (así quedan los <c>DB_*</c> del <c>.env.example</c> de Laravel 11 y siguientes);</item>
/// <item>todo lo demás (comentarios, líneas en blanco, líneas que no se entienden) se deja tal cual.</item>
/// </list>
/// </remarks>
public sealed partial class EnvFile
{
    private readonly List<Entry> _entries;
    private readonly string _newline;

    private EnvFile(List<Entry> entries, string newline)
    {
        _entries = entries;
        _newline = newline;
    }

    /// <summary>Una línea (o varias, si es un valor entre comillas que sigue en la siguiente) con su salto de línea.</summary>
    private sealed class Entry
    {
        /// <summary>El texto tal cual, con sus saltos de línea.</summary>
        public required string Raw { get; set; }

        /// <summary>La clave, si es una asignación (también si está comentada).</summary>
        public string? Key { get; init; }

        /// <summary>Es <c># CLAVE=valor</c>: una clave comentada.</summary>
        public bool Commented { get; init; }

        /// <summary>Lo de delante de la clave: la sangría y el <c>export </c>.</summary>
        public string Prefix { get; init; } = "";

        /// <summary>El valor ya leído (sin comillas y con los escapes resueltos).</summary>
        public string Value { get; set; } = "";

        /// <summary>El comentario que va detrás del valor, con su espacio delante (<c> # la url</c>), o vacío.</summary>
        public string Trailing { get; init; } = "";

        /// <summary>El salto de línea con que acaba (vacío en la última línea si el fichero no acaba en uno).</summary>
        public string Ending { get; set; } = "";

        public bool IsActive => Key is not null && !Commented;
    }

    // CLAVE=, con export delante opcional. Las claves de phpdotenv y de Symfony: letras, números, _ y . (sin empezar por número).
    [GeneratedRegex(@"^(?<prefix>[ \t]*(?:export[ \t]+)?)(?<key>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*=[ \t]*")]
    private static partial Regex AssignmentPattern();

    // # CLAVE=valor: una clave comentada (sin espacios en la clave: «# Esto es una nota=sí» no lo es).
    [GeneratedRegex(@"^[ \t]*#+[ \t]*(?<key>[A-Za-z_][A-Za-z0-9_.]*)=")]
    private static partial Regex CommentedPattern();

    // Un valor que va sin comillas sin cambiar de significado en phpdotenv ni en symfony/dotenv.
    [GeneratedRegex(@"^[A-Za-z0-9_./:@%+,=\-]*$")]
    private static partial Regex BarePattern();

    /// <summary>El salto de línea de las líneas que se añaden: el que más usa el fichero (LF si no tiene ninguno).</summary>
    public string NewLine => _newline;

    /// <summary>Lee el texto de un .env. Nunca lanza: lo que no entiende lo conserva tal cual.</summary>
    public static EnvFile Parse(string text)
    {
        var lines = SplitLines(text);
        var crlf = lines.Count(l => l.Ending == "\r\n");
        var lf = lines.Count(l => l.Ending == "\n");
        var entries = new List<Entry>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (line, ending) = lines[i];
            var assignment = AssignmentPattern().Match(line);
            if (assignment.Success)
            {
                var rest = line[assignment.Length..];
                var raw = new StringBuilder(line).Append(ending);
                var last = ending;
                // Un valor entre comillas que no se cierra en esta línea sigue en las siguientes.
                if (rest.Length > 0 && rest[0] is '"' or '\'' && FindClosingQuote(rest, rest[0]) < 0)
                {
                    var j = i + 1;
                    var value = new StringBuilder(rest).Append('\n');
                    while (j < lines.Count)
                    {
                        value.Append(lines[j].Line);
                        raw.Append(lines[j].Line).Append(lines[j].Ending);
                        last = lines[j].Ending;
                        if (FindClosingQuote(value.ToString(), rest[0]) >= 0) break;
                        value.Append('\n');
                        j++;
                    }
                    if (j < lines.Count)
                    {
                        // Cerrado unas líneas más abajo: es un valor de varias líneas.
                        rest = value.ToString();
                        i = j;
                    }
                    else
                    {
                        // Nunca se cierra: no se adivina, se deja la línea tal cual y se sigue con la siguiente.
                        raw = new StringBuilder(line).Append(ending);
                        last = ending;
                    }
                }

                var (parsed, trailing) = ParseValue(rest);
                entries.Add(new Entry
                {
                    Raw = raw.ToString(),
                    Key = assignment.Groups["key"].Value,
                    Prefix = assignment.Groups["prefix"].Value,
                    Value = parsed,
                    Trailing = trailing,
                    Ending = last,
                });
                continue;
            }

            var commented = CommentedPattern().Match(line);
            entries.Add(commented.Success
                ? new Entry { Raw = line + ending, Key = commented.Groups["key"].Value, Commented = true, Ending = ending }
                : new Entry { Raw = line + ending, Ending = ending });
        }
        return new EnvFile(entries, crlf > lf ? "\r\n" : "\n");
    }

    /// <summary>Las claves que tienen valor (sin las comentadas), en el orden en que aparecen y sin repetir.</summary>
    public IReadOnlyList<string> Keys => _entries.Where(e => e.IsActive).Select(e => e.Key!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>true si la clave está (sin comentar), aunque sea vacía.</summary>
    public bool Has(string key) => _entries.Any(e => e.IsActive && e.Key == key);

    /// <summary>true si la clave solo está comentada (<c># CLAVE=…</c>).</summary>
    public bool IsCommented(string key) => !Has(key) && _entries.Any(e => e.Commented && e.Key == key);

    /// <summary>
    /// El valor de la clave, ya sin comillas, o null si no está (o solo está comentada). Si aparece
    /// varias veces, el de la primera: es la que gana en Laravel (phpdotenv no pisa lo que ya leyó).
    /// </summary>
    public string? Get(string key) => _entries.FirstOrDefault(e => e.IsActive && e.Key == key)?.Value;

    /// <summary>
    /// Pone <paramref name="value"/> en la clave. Si ya está, cambia su valor en su sitio (en todas
    /// las veces que aparezca), con el <c>export</c> y el comentario de detrás que tuviera; si ya
    /// tenía ese valor, no toca la línea. Si solo está comentada, descomenta la primera en su sitio.
    /// Si no, la añade tras la última de su grupo (las que empiezan igual hasta el primer <c>_</c>:
    /// <c>DB_</c>, <c>MAIL_</c>) o, si no hay grupo, al final. Devuelve lo que ha pasado.
    /// </summary>
    public EnvChange Set(string key, string value)
    {
        CheckKey(key);
        var active = _entries.Where(e => e.IsActive && e.Key == key).ToList();
        if (active.Count > 0)
        {
            if (active.All(e => e.Value == value)) return EnvChange.Same;
            foreach (var entry in active.Where(e => e.Value != value))
            {
                entry.Raw = entry.Prefix + key + "=" + Quote(value) + entry.Trailing + entry.Ending;
                entry.Value = value;
            }
            return EnvChange.Changed;
        }

        var line = key + "=" + Quote(value);
        var commented = _entries.FindIndex(e => e.Commented && e.Key == key);
        if (commented >= 0)
        {
            var old = _entries[commented];
            _entries[commented] = new Entry { Raw = line + old.Ending, Key = key, Value = value, Ending = old.Ending };
            return EnvChange.Added;
        }

        Insert(key, line, value);
        return EnvChange.Added;
    }

    /// <summary>
    /// Fusiona <paramref name="values"/> en orden. Con <paramref name="overwrite"/>, pone cada uno
    /// (<see cref="Set"/>); sin él, solo añade las claves que no están y deja las que el usuario ya
    /// tiene, aunque tengan otro valor o estén vacías (una clave comentada cuenta como que no está).
    /// Devuelve qué ha pasado con cada clave.
    /// </summary>
    public IReadOnlyList<(string Key, EnvChange Change)> Merge(IEnumerable<KeyValuePair<string, string>> values, bool overwrite)
    {
        var changes = new List<(string, EnvChange)>();
        foreach (var (key, value) in values)
        {
            if (!overwrite && Has(key))
                changes.Add((key, Get(key) == value ? EnvChange.Same : EnvChange.Kept));
            else
                changes.Add((key, Set(key, value)));
        }
        return changes;
    }

    /// <summary>PURO: el texto de <paramref name="text"/> con <paramref name="values"/> fusionados (ver <see cref="Merge"/>).</summary>
    public static (string Text, IReadOnlyList<(string Key, EnvChange Change)> Changes) Apply(string text, IEnumerable<KeyValuePair<string, string>> values, bool overwrite)
    {
        var env = Parse(text);
        var changes = env.Merge(values, overwrite);
        return (env.ToString(), changes);
    }

    /// <summary>El texto entero: lo que no se ha tocado sale tal cual estaba.</summary>
    public override string ToString() => string.Concat(_entries.Select(e => e.Raw));

    /// <summary>
    /// PURO: el valor tal como se escribe detrás del <c>=</c>. Sin comillas si no hace falta
    /// (letras, números y <c>_ . / : @ % + , = -</c>); si no, entre comillas dobles y, si dentro hay
    /// algo que en ellas se interpretaría (<c>$</c> de una variable, <c>\</c>, <c>"</c>), entre simples,
    /// que son literales. Solo si lleva comillas simples y uno de esos, dobles con escapes.
    /// </summary>
    public static string Quote(string value)
    {
        if (BarePattern().IsMatch(value)) return value;
        var multiline = value.Contains('\n') || value.Contains('\r');
        if (!multiline && value.IndexOfAny(['"', '\\', '$']) < 0) return "\"" + value + "\"";
        if (!multiline && !value.Contains('\'')) return "'" + value + "'";
        var escaped = new StringBuilder("\"");
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '$' => "\\$",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => c.ToString(),
            });
        }
        return escaped.Append('"').ToString();
    }

    /// <summary>Lee un .env de disco (UTF-8, con o sin BOM). Devuelve también si llevaba BOM, para <see cref="Save"/>.</summary>
    public static (EnvFile Env, bool Bom) Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return (Parse(text), bom);
    }

    /// <summary>
    /// Escribe el .env en <paramref name="path"/> (UTF-8, con BOM solo si <paramref name="bom"/>),
    /// primero en un temporal al lado y luego en su sitio: un corte a medias no deja un .env roto.
    /// </summary>
    public void Save(string path, bool bom = false)
    {
        var temp = path + ".roost-tmp";
        File.WriteAllText(temp, ToString(), new UTF8Encoding(bom));
        File.Move(temp, path, overwrite: true);
    }

    private static void CheckKey(string key)
    {
        if (!Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_.]*$"))
            throw new ArgumentException($"'{key}' no vale como clave de un .env.", nameof(key));
    }

    /// <summary>Añade la línea tras la última de su grupo (o de su grupo comentado), o al final.</summary>
    private void Insert(string key, string line, string value)
    {
        var underscore = key.IndexOf('_');
        var group = underscore > 0 ? key[..(underscore + 1)] : null;
        var after = group is null ? -1 : _entries.FindLastIndex(e => e.IsActive && e.Key!.StartsWith(group, StringComparison.Ordinal));
        if (after < 0 && group is not null) after = _entries.FindLastIndex(e => e.Key?.StartsWith(group, StringComparison.Ordinal) == true);

        var entry = new Entry { Raw = line + _newline, Key = key, Value = value, Ending = _newline };
        if (after >= 0)
        {
            EnsureEnding(after);
            _entries.Insert(after + 1, entry);
            return;
        }

        // Al final: si el fichero no acababa en salto de línea, se le pone antes; y un grupo nuevo
        // va separado por una línea en blanco, como los del .env.example.
        if (_entries.Count > 0)
        {
            EnsureEnding(_entries.Count - 1);
            var lastText = _entries[^1].Raw.TrimEnd('\r', '\n');
            if (lastText.Trim().Length > 0) _entries.Add(new Entry { Raw = _newline, Ending = _newline });
        }
        _entries.Add(entry);
    }

    /// <summary>Si la entrada no acaba en salto de línea (la última del fichero), se le pone.</summary>
    private void EnsureEnding(int index)
    {
        var entry = _entries[index];
        if (entry.Ending.Length > 0) return;
        entry.Raw += _newline;
        entry.Ending = _newline;
    }

    /// <summary>Las líneas con su salto de línea (<c>\r\n</c>, <c>\n</c> o, en la última, nada).</summary>
    private static List<(string Line, string Ending)> SplitLines(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                lines.Add((text[start..], ""));
                break;
            }
            var crlf = nl > start && text[nl - 1] == '\r';
            lines.Add((text[start..(crlf ? nl - 1 : nl)], crlf ? "\r\n" : "\n"));
            start = nl + 1;
        }
        return lines;
    }

    /// <summary>Dónde se cierra la comilla que abre <paramref name="text"/> (en las dobles, saltando las escapadas); -1 si no se cierra.</summary>
    private static int FindClosingQuote(string text, char quote)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\') { i++; continue; }
            if (text[i] == quote) return i;
        }
        return -1;
    }

    /// <summary>El valor (sin comillas, con los escapes resueltos) y el comentario que va detrás.</summary>
    private static (string Value, string Trailing) ParseValue(string rest)
    {
        if (rest.Length > 0 && rest[0] is '"' or '\'')
        {
            var quote = rest[0];
            var close = FindClosingQuote(rest, quote);
            if (close > 0)
            {
                var inner = rest[1..close];
                var after = rest[(close + 1)..];
                var trailing = after.TrimStart().StartsWith('#') ? after : "";
                return (quote == '"' ? Unescape(inner) : inner, trailing);
            }
        }

        // Sin comillas: un # con un espacio (o un tabulador) delante empieza un comentario.
        var comment = Regex.Match(rest, @"[ \t]+#");
        var value = comment.Success ? rest[..comment.Index] : rest;
        return (value.Trim(), comment.Success ? rest[comment.Index..] : "");
    }

    private static string Unescape(string inner)
    {
        if (!inner.Contains('\\')) return inner;
        var result = new StringBuilder();
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i == inner.Length - 1)
            {
                result.Append(inner[i]);
                continue;
            }
            var next = inner[++i];
            result.Append(next switch
            {
                'n' => "\n",
                'r' => "\r",
                't' => "\t",
                '"' => "\"",
                '\\' => "\\",
                '$' => "$",
                _ => "\\" + next,
            });
        }
        return result.ToString();
    }
}
