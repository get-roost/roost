using System.Text;
using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Models;

namespace Roost.Core.Stack;

/// <summary>
/// Lo que hay que adaptar al vuelo para que un volcado de otro motor entre en este servidor
/// (BD-2). Vacío = el fichero pasa tal cual (salvo los <c>USE</c>/<c>CREATE DATABASE</c>, que
/// <see cref="SqlImportFilter"/> neutraliza siempre).
/// </summary>
/// <param name="TargetCollations">Las colaciones que conoce el destino; las que no conozca se cambian (null = no se toca ninguna).</param>
/// <param name="DropSqlModes">Las opciones de <c>sql_mode</c> que el destino no admite y se quitan.</param>
/// <param name="DropSandboxLine">Quitar la primera línea de los volcados de MariaDB 11 (<c>/*M!999999\- enable the sandbox mode */</c>), que un cliente de MySQL viejo no entiende.</param>
public sealed record CompatPlan(IReadOnlySet<string>? TargetCollations, IReadOnlySet<string> DropSqlModes, bool DropSandboxLine)
{
    public static readonly CompatPlan None = new(null, new HashSet<string>(), false);

    public bool IsEmpty => TargetCollations is null && DropSqlModes.Count == 0 && !DropSandboxLine;
}

/// <summary>Un problema de compatibilidad encontrado en la cabecera de un volcado, con lo que se haría para arreglarlo.</summary>
public sealed record CompatIssue(string What, string Fix);

/// <summary>
/// PURO: lo que separa a MySQL de MariaDB al importar un volcado del otro. Lo que se investigó
/// en real (BD-2): MariaDB 11.4.13 ya conoce <c>utf8mb4_0900_ai_ci</c> y sus hermanas (las de
/// MySQL 8), pero no las de idioma (<c>utf8mb4_es_0900_ai_ci</c>) ni una MariaDB más vieja; MySQL
/// no conoce las <c>uca1400</c> de MariaDB 11 y rechaza <c>NO_AUTO_CREATE_USER</c> en el
/// <c>sql_mode</c> de los triggers y las rutinas que vuelca MariaDB (ERROR 1231).
/// </summary>
public static partial class SqlCompat
{
    /// <summary>Las de <c>sql_mode</c> que MySQL 8 quitó: con ellas, <c>SET sql_mode</c> falla.</summary>
    public static readonly IReadOnlySet<string> RemovedInMySql8 = new HashSet<string>(
        ["NO_AUTO_CREATE_USER", "DB2", "MAXDB", "MSSQL", "MYSQL323", "MYSQL40", "ORACLE", "POSTGRESQL", "NO_FIELD_OPTIONS", "NO_KEY_OPTIONS", "NO_TABLE_OPTIONS"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Las que solo tiene MariaDB.</summary>
    public static readonly IReadOnlySet<string> MariaDbOnly = new HashSet<string>(
        ["EMPTY_STRING_IS_NULL", "SIMULTANEOUS_ASSIGNMENT", "TIME_ROUND_FRACTIONAL"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lo que se adapta para importar en <paramref name="target"/>: las colaciones que no conozca
    /// (si se saben), las opciones de <c>sql_mode</c> que no admita y, en MySQL, la línea de
    /// sandbox de MariaDB 11.
    /// </summary>
    public static CompatPlan PlanFor(DbEngine target, ComponentVersion? version, IReadOnlySet<string>? targetCollations)
    {
        var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (target == DbEngine.MySql)
        {
            modes.UnionWith(MariaDbOnly);
            if (version is not { Major: < 8 }) modes.UnionWith(RemovedInMySql8);
        }
        return new CompatPlan(targetCollations, modes, target == DbEngine.MySql);
    }

    /// <summary>
    /// Lo que pasaría con este volcado en ese servidor, mirando su cabecera: vacío si entra tal cual.
    /// </summary>
    public static IReadOnlyList<CompatIssue> Issues(DumpInfo info, CompatPlan plan)
    {
        var issues = new List<CompatIssue>();
        if (plan.TargetCollations is { } known)
        {
            var unknown = info.Collations.Where(c => !known.Contains(c)).Order(StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                issues.Add(new CompatIssue(
                    $"Colaciones que este servidor no conoce: {string.Join(", ", unknown)}",
                    string.Join(", ", unknown.Select(c => $"{c} -> {MapCollation(c, known)}"))));
        }
        var modes = info.SqlModes.Where(plan.DropSqlModes.Contains).Order(StringComparer.Ordinal).ToList();
        if (modes.Count > 0)
            issues.Add(new CompatIssue($"sql_mode con {string.Join(", ", modes)}, que este servidor no admite", "se quitan de cada sql_mode"));
        return issues;
    }

    /// <summary>
    /// La colación que se usa en lugar de una que el destino no conoce, del mismo juego de
    /// caracteres: <c>_bin</c> para las binarias o sensibles a mayúsculas, <c>_unicode_ci</c> para
    /// el resto. Si no es de utf8 (o el destino tampoco tiene esa), se deja como estaba.
    /// </summary>
    public static string MapCollation(string name, IReadOnlySet<string>? known = null)
    {
        var lower = name.ToLowerInvariant();
        var charset = lower.Split('_')[0];
        if (charset is not ("utf8mb4" or "utf8mb3" or "utf8")) return name;
        var mapped = lower.EndsWith("_bin", StringComparison.Ordinal) || lower.EndsWith("_cs", StringComparison.Ordinal)
            ? $"{charset}_bin"
            : $"{charset}_unicode_ci";
        return known is null || known.Contains(mapped) ? mapped : name;
    }

    /// <summary>Quita de una lista de <c>sql_mode</c> las opciones que no admite el destino.</summary>
    public static string FilterSqlMode(string modes, IReadOnlySet<string> drop, out int removed)
    {
        var parts = modes.Split(',', StringSplitOptions.TrimEntries);
        var kept = parts.Where(p => p.Length > 0 && !drop.Contains(p)).ToList();
        removed = parts.Count(p => p.Length > 0) - kept.Count;
        return string.Join(",", kept);
    }

    /// <summary>
    /// true si el cliente de esa versión tiene la opción que le impide ejecutar comandos del
    /// propio fichero (<c>\!</c>, <c>system</c>): <c>--sandbox</c> en MariaDB (10.5.25, 10.6.18,
    /// 10.11.8, 11.0.6, 11.1.5, 11.2.4, 11.4.2 y siguientes) y <c>--skip-system-command</c> en
    /// MySQL (8.0.40, 8.4.3, 9.1).
    /// </summary>
    public static bool HasClientSandbox(DbEngine engine, ComponentVersion? version)
    {
        if (version is not { } v) return false;
        (int Major, int Minor, int Patch)[] since = engine == DbEngine.MariaDb
            ? [(10, 5, 25), (10, 6, 18), (10, 11, 8), (11, 0, 6), (11, 1, 5), (11, 2, 4), (11, 4, 2)]
            : [(8, 0, 40), (8, 4, 3)];
        var newest = engine == DbEngine.MariaDb ? (11, 5) : (9, 1);
        if (v.Major > newest.Item1 || (v.Major == newest.Item1 && v.Minor >= newest.Item2)) return true;
        return since.Any(s => s.Major == v.Major && s.Minor == v.Minor && v.Patch >= s.Patch);
    }

    // Un nombre de colación detrás de COLLATE o de collation_connection = (nunca un identificador suelto).
    [GeneratedRegex(@"(\bCOLLATE\s*=?\s*'?|\bcollation_(?:connection|server|database)\s*=\s*'?)([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    internal static partial Regex CollationPattern();

    [GeneratedRegex(@"(\bsql_mode\s*=\s*')([^']*)(')", RegexOptions.IgnoreCase)]
    internal static partial Regex SqlModePattern();
}

/// <summary>Lo que se sabe de un volcado por su principio (<see cref="DbDump.SniffAsync"/>).</summary>
public sealed record DumpInfo
{
    /// <summary>El motor que lo hizo, si lo dice la cabecera (mysqldump, mariadb-dump, phpMyAdmin, HeidiSQL).</summary>
    public DbEngine? SourceEngine { get; init; }

    /// <summary>La versión del servidor del que salió, si lo dice («8.4.11», «10.4.32»).</summary>
    public string? SourceVersion { get; init; }

    /// <summary>La base de datos que volcó, si lo dice la cabecera.</summary>
    public string? SourceDatabase { get; init; }

    /// <summary>Las bases de datos de sus <c>CREATE DATABASE</c> y <c>USE</c> (las que se neutralizan).</summary>
    public IReadOnlyList<string> Databases { get; init; } = [];

    /// <summary>Las colaciones que aparecen tras <c>COLLATE</c> o <c>collation_connection</c>, en minúsculas.</summary>
    public IReadOnlySet<string> Collations { get; init; } = new HashSet<string>();

    /// <summary>Las opciones de <c>sql_mode</c> que aparecen, en mayúsculas.</summary>
    public IReadOnlySet<string> SqlModes { get; init; } = new HashSet<string>();

    /// <summary>false si no parece texto SQL (un binario, un .zip dentro de otro…).</summary>
    public bool LooksLikeSql { get; init; } = true;

    /// <summary>«un volcado de MySQL 8.4.11», o null si no se sabe.</summary>
    public string? Origin => SourceEngine is { } e ? $"un volcado de {e.DisplayName()}{(SourceVersion is null ? "" : " " + SourceVersion)}" : null;
}

/// <summary>
/// El .sql camino del cliente, en streaming y línea a línea, sin cargarlo entero (vale para varios
/// GB). Siempre neutraliza, convirtiéndolos en comentarios, los <c>USE</c>, <c>CREATE DATABASE</c>
/// y <c>DROP DATABASE</c> de principio de línea, así todo va a la base de datos elegida y nada toca
/// otra; si el fichero trae dos bases de datos distintas, se niega a seguir (lanza
/// <see cref="RoostException"/>, y el runner mata al cliente). Con un <see cref="CompatPlan"/>
/// cambia además las colaciones y el <c>sql_mode</c> que el destino no entiende, solo fuera de las
/// líneas de datos (<c>INSERT</c>, <c>REPLACE</c> y las filas que empiezan por <c>(</c>), que pasan
/// tal cual y sin mirarlas. Quita también el BOM del principio. Los bytes se leen como Latin-1 para
/// que lo que no se cambia salga idéntico.
/// </summary>
public sealed partial class SqlImportFilter : Stream
{
    /// <summary>Lo que se mira del principio de una línea para saber si es de datos.</summary>
    private const int HeadBytes = 32;

    /// <summary>Una línea que no es de datos y pasa de esto se deja pasar sin tocarla.</summary>
    public const int MaxLineBytes = 4 << 20;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly Stream _source;
    private readonly CompatPlan _plan;
    private readonly byte[] _in = new byte[1 << 16];
    private byte[] _line = new byte[1 << 12];
    private int _lineLength;
    private byte[] _out = new byte[1 << 17];
    private int _outStart;
    private int _outEnd;
    private Mode _mode = Mode.LineStart;
    private bool _first = true;
    private bool _eof;
    private readonly Dictionary<string, (string To, int Count)> _collations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sqlModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _databases = [];

    private enum Mode
    {
        /// <summary>Juntando el principio de una línea para clasificarla.</summary>
        LineStart,
        /// <summary>Una línea que no es de datos: entera, para mirarla al acabar.</summary>
        Line,
        /// <summary>Hasta el final de la línea, tal cual.</summary>
        Pass,
    }

    public SqlImportFilter(Stream source, CompatPlan? plan = null)
    {
        _source = source;
        _plan = plan ?? CompatPlan.None;
    }

    /// <summary>Cuántos <c>USE</c>/<c>CREATE DATABASE</c>/<c>DROP DATABASE</c> se han convertido en comentarios.</summary>
    public int Neutralized { get; private set; }

    /// <summary>La base de datos de los <c>USE</c> y <c>CREATE DATABASE</c> del fichero (la primera), si traía.</summary>
    public string? SourceDatabase => _databases.FirstOrDefault();

    public bool DroppedSandboxLine { get; private set; }

    /// <summary>Colación del fichero → la que se ha usado, y cuántas veces.</summary>
    public IReadOnlyDictionary<string, (string To, int Count)> MappedCollations => _collations;

    /// <summary>Opción de <c>sql_mode</c> → cuántas veces se ha quitado.</summary>
    public IReadOnlyDictionary<string, int> DroppedSqlModes => _sqlModes;

    /// <summary>Lo que se ha cambiado, para contarlo al acabar (sin tildes: lo enseña también la CLI).</summary>
    public IReadOnlyList<string> Notes(string target)
    {
        var notes = new List<string>();
        if (Neutralized > 0)
            notes.Add(SourceDatabase is { } db && !db.Equals(target, StringComparison.OrdinalIgnoreCase)
                ? $"El fichero traia CREATE DATABASE/USE de {db}: se han quitado y todo ha ido a {target}."
                : $"El fichero traia CREATE DATABASE/USE: se han quitado y todo ha ido a {target}.");
        foreach (var (from, (to, count)) in _collations.OrderBy(c => c.Key, StringComparer.Ordinal))
            notes.Add($"Colacion {from} cambiada por {to} ({count} {(count == 1 ? "vez" : "veces")}).");
        foreach (var (mode, count) in _sqlModes.OrderBy(m => m.Key, StringComparer.Ordinal))
            notes.Add($"Quitado {mode} del sql_mode ({count} {(count == 1 ? "vez" : "veces")}).");
        if (DroppedSandboxLine) notes.Add("Quitada la linea de sandbox de MariaDB 11 (/*M!999999\\- ... */).");
        return notes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            var n = Drain(buffer);
            if (n > 0 || (_eof && _outStart == _outEnd)) return n;
            var read = _source.Read(_in, 0, _in.Length);
            Process(read);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var n = Drain(buffer.Span);
            if (n > 0 || (_eof && _outStart == _outEnd)) return n;
            var read = await _source.ReadAsync(_in, cancellationToken);
            Process(read);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await base.DisposeAsync();
    }

    private int Drain(Span<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _outEnd - _outStart);
        if (n == 0) return 0;
        _out.AsSpan(_outStart, n).CopyTo(buffer);
        _outStart += n;
        if (_outStart == _outEnd) _outStart = _outEnd = 0;
        return n;
    }

    private void Process(int read)
    {
        if (read == 0)
        {
            _eof = true;
            StripBom();
            if (_lineLength > 0)
            {
                if (_mode == Mode.Pass) Emit(_line.AsSpan(0, _lineLength));
                else EmitLine(_line.AsSpan(0, _lineLength));
                _lineLength = 0;
            }
            return;
        }

        var input = _in.AsSpan(0, read);

        while (input.Length > 0)
        {
            var newline = input.IndexOf((byte)'\n');
            var take = newline < 0 ? input.Length : newline + 1;
            var chunk = input[..take];
            input = input[take..];

            if (_mode == Mode.Pass)
            {
                Emit(chunk);
                if (newline >= 0) _mode = Mode.LineStart;
                continue;
            }

            Accumulate(chunk);
            if (_mode == Mode.LineStart && (newline >= 0 || _lineLength >= HeadBytes))
            {
                StripBom();
                if (IsDataLine(_line.AsSpan(0, _lineLength)))
                {
                    Emit(_line.AsSpan(0, _lineLength));
                    _lineLength = 0;
                    _mode = newline >= 0 ? Mode.LineStart : Mode.Pass;
                    continue;
                }
                _mode = Mode.Line;
            }

            if (_mode == Mode.Line)
            {
                if (newline >= 0)
                {
                    EmitLine(_line.AsSpan(0, _lineLength));
                    _lineLength = 0;
                    _mode = Mode.LineStart;
                }
                else if (_lineLength > MaxLineBytes)
                {
                    Emit(_line.AsSpan(0, _lineLength));
                    _lineLength = 0;
                    _mode = Mode.Pass;
                }
            }
        }
    }

    /// <summary>El BOM de UTF-8 del principio del fichero (el cliente no lo entiende): se quita al clasificar la primera línea.</summary>
    private void StripBom()
    {
        if (!_first) return;
        _first = false;
        if (_line.AsSpan(0, _lineLength).StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            _line.AsSpan(3, _lineLength - 3).CopyTo(_line);
            _lineLength -= 3;
        }
    }

    /// <summary>PURO: la línea empieza por <c>INSERT</c>, <c>REPLACE</c> o <c>(</c> (tras espacios): datos, que no se miran.</summary>
    public static bool IsDataLine(ReadOnlySpan<byte> line)
    {
        var i = 0;
        while (i < line.Length && line[i] is (byte)' ' or (byte)'\t' or (byte)'\r') i++;
        var rest = line[i..];
        if (rest.Length > 0 && rest[0] == (byte)'(') return true;
        return StartsWithWord(rest, "INSERT"u8) || StartsWithWord(rest, "REPLACE"u8);
    }

    private static bool StartsWithWord(ReadOnlySpan<byte> text, ReadOnlySpan<byte> word)
    {
        if (text.Length < word.Length) return false;
        for (var i = 0; i < word.Length; i++)
            if ((text[i] | 0x20) != (word[i] | 0x20)) return false;
        return text.Length == word.Length || text[word.Length] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'`' or (byte)'/';
    }

    private void Accumulate(ReadOnlySpan<byte> chunk)
    {
        if (_lineLength + chunk.Length > _line.Length)
            Array.Resize(ref _line, Math.Max(_line.Length * 2, _lineLength + chunk.Length));
        chunk.CopyTo(_line.AsSpan(_lineLength));
        _lineLength += chunk.Length;
    }

    private void Emit(ReadOnlySpan<byte> bytes)
    {
        if (_outEnd + bytes.Length > _out.Length)
        {
            // Lo pendiente se lleva al principio; si aun así no cabe, se agranda.
            var pending = _outEnd - _outStart;
            if (pending + bytes.Length > _out.Length)
            {
                var bigger = new byte[Math.Max(_out.Length * 2, pending + bytes.Length)];
                _out.AsSpan(_outStart, pending).CopyTo(bigger);
                _out = bigger;
            }
            else
            {
                _out.AsSpan(_outStart, pending).CopyTo(_out);
            }
            _outStart = 0;
            _outEnd = pending;
        }
        bytes.CopyTo(_out.AsSpan(_outEnd));
        _outEnd += bytes.Length;
    }

    private void EmitLine(ReadOnlySpan<byte> bytes)
    {
        var text = Latin1.GetString(bytes);
        var changed = Transform(text);
        if (ReferenceEquals(changed, text)) Emit(bytes);
        else Emit(Latin1.GetBytes(changed));
    }

    /// <summary>Lo que se hace con una línea que no es de datos; devuelve la misma instancia si no cambia.</summary>
    private string Transform(string line)
    {
        var body = line.TrimEnd('\r', '\n');
        var ending = line[body.Length..];
        var trimmed = body.TrimStart(' ', '\t');

        if (_plan.DropSandboxLine && trimmed.StartsWith(@"/*M!999999\-", StringComparison.Ordinal))
        {
            DroppedSandboxLine = true;
            return "-- roost: " + body + ending;
        }

        if (DatabaseStatement().Match(trimmed) is { Success: true } statement)
        {
            var name = statement.Groups["name"].Value;
            if (name.Length > 0)
            {
                // Un volcado de todo el servidor (--all-databases) empieza por la base de datos mysql:
                // sus tablas de usuarios acabarían como tablas sueltas en la elegida.
                if (DbAdmin.SystemSchemas.Contains(name))
                    throw new RoostException($"El fichero trae la base de datos {name}, que es del propio servidor: parece un volcado de todo el servidor. Vuelca solo la tuya (mysqldump <base_de_datos>) e importa ese.");
                if (_databases.Count > 0 && !_databases[0].Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !statement.Groups["verb"].Value.StartsWith("DROP", StringComparison.OrdinalIgnoreCase))
                    throw new RoostException($"El fichero trae mas de una base de datos ({_databases[0]} y {name}): importalas por separado, cada una a la suya.");
                if (_databases.Count == 0) _databases.Add(name);
            }
            Neutralized++;
            return "-- roost: " + body + ending;
        }

        var result = body;
        if (_plan.DropSqlModes.Count > 0 && result.Contains("sql_mode", StringComparison.OrdinalIgnoreCase))
        {
            result = SqlCompat.SqlModePattern().Replace(result, m =>
            {
                var filtered = SqlCompat.FilterSqlMode(m.Groups[2].Value, _plan.DropSqlModes, out var removed);
                if (removed == 0) return m.Value;
                foreach (var mode in m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries).Where(_plan.DropSqlModes.Contains))
                    _sqlModes[mode.ToUpperInvariant()] = _sqlModes.GetValueOrDefault(mode.ToUpperInvariant()) + 1;
                return m.Groups[1].Value + filtered + m.Groups[3].Value;
            });
        }

        if (_plan.TargetCollations is { } known && result.Contains("collat", StringComparison.OrdinalIgnoreCase))
        {
            result = SqlCompat.CollationPattern().Replace(result, m =>
            {
                var name = m.Groups[2].Value;
                if (known.Contains(name)) return m.Value;
                var mapped = SqlCompat.MapCollation(name, known);
                if (mapped.Equals(name, StringComparison.OrdinalIgnoreCase)) return m.Value;
                var key = name.ToLowerInvariant();
                _collations[key] = (mapped, _collations.TryGetValue(key, out var seen) ? seen.Count + 1 : 1);
                return m.Groups[1].Value + mapped;
            });
        }

        return ReferenceEquals(result, body) || result == body ? line : result + ending;
    }

    // USE `x`; CREATE DATABASE [IF NOT EXISTS] `x` ...; DROP DATABASE [IF EXISTS] `x`; también tras /*!40000 .
    [GeneratedRegex(@"^(?:/\*!\d{5,6}\s*)?(?<verb>USE|CREATE\s+(?:DATABASE|SCHEMA)|DROP\s+(?:DATABASE|SCHEMA))\b(?:\s*/\*!\d{5,6}\s*IF\s+(?:NOT\s+)?EXISTS\s*\*/|\s+IF\s+(?:NOT\s+)?EXISTS)?\s*`?(?<name>[^`;\s*/]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseStatement();
}
