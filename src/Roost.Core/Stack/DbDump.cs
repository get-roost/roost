using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Platform;

namespace Roost.Core.Stack;

/// <summary>
/// Los programas cliente de una instalación (la de Roost, o la de Laragon) y el fichero de
/// opciones con el que conectan: el my.ini de Roost, cuyo <c>[client]</c> lleva a root y su
/// contraseña, o uno temporal. La contraseña nunca va en la línea de órdenes.
/// </summary>
public sealed record DbTool(DbEngine Engine, ComponentVersion? Version, string Client, string Dump, string DefaultsFile)
{
    /// <summary>Los de una base de datos de Roost, con su my.ini.</summary>
    public static DbTool For(InstalledDb db, string iniPath) => new(db.Engine, db.Version, db.Client, db.Dump, iniPath);

    /// <summary>
    /// Los de la carpeta <c>bin</c> de otra instalación: los nombres de ahora (<c>mariadb.exe</c>,
    /// <c>mariadb-dump.exe</c>) si están y, si no, los de siempre (<c>mysql.exe</c>,
    /// <c>mysqldump.exe</c>, los únicos de una MariaDB anterior a la 10.5). null si falta alguno.
    /// </summary>
    public static DbTool? InFolder(DbEngine engine, ComponentVersion? version, string binDir, string defaultsFile)
    {
        string? Pick(params string[] names) => names.Select(n => Path.Combine(binDir, n)).FirstOrDefault(File.Exists);
        var client = engine == DbEngine.MariaDb ? Pick(OsNames.Exe("mariadb"), OsNames.Exe("mysql")) : Pick(OsNames.Exe("mysql"));
        var dump = engine == DbEngine.MariaDb ? Pick(OsNames.Exe("mariadb-dump"), OsNames.Exe("mysqldump")) : Pick(OsNames.Exe("mysqldump"));
        return client is null || dump is null ? null : new DbTool(engine, version, client, dump, defaultsFile);
    }

    public string Label => $"{Engine.DisplayName()}{(Version is { } v ? " " + v : "")}";
}

/// <summary>Cuánto se lleva de un fichero: los bytes leídos (o escritos) y el total, si se sabe.</summary>
public sealed record DbTransferProgress(long Bytes, long? Total)
{
    public double? Percent => Total is > 0 and var t ? Math.Min(100, Bytes * 100.0 / t) : null;
}

/// <summary>Lo que dejó una exportación.</summary>
public sealed record DbExportResult(string Database, string File, long Bytes, bool Compressed, TimeSpan Took);

/// <summary>Lo que dejó una importación, con lo que se adaptó o se quitó por el camino.</summary>
public sealed record DbImportResult(string Database, string File, long Bytes, TimeSpan Took, IReadOnlyList<string> Notes);

/// <summary>Cómo viene un fichero que se va a importar.</summary>
public enum DumpFormat
{
    Sql,
    Gzip,
    Zip,
}

/// <summary>
/// Exportar e importar un .sql a mano (BD-2, gratis): con el <c>mariadb-dump</c>/<c>mysqldump</c>
/// y el cliente de la instalación, sobre <see cref="ICommandRunner"/> (cancelar mata el árbol de
/// procesos). Exportar escribe en un temporal al lado y lo renombra al acabar, así que si falla o
/// se cancela no queda ningún fichero a medias; con <c>.gz</c> comprime al vuelo. Importar manda
/// el fichero por stdin en streaming (vale para varios GB), descomprime al vuelo un <c>.gz</c> o el
/// .sql de un <c>.zip</c>, cuenta los bytes leídos frente al tamaño y lo pasa por
/// <see cref="SqlImportFilter"/>. Los nombres se validan con el patrón cerrado de
/// <see cref="DbAdmin"/> y nunca se importa sobre una del sistema. Si el destino existe o tiene
/// tablas lo deciden la CLI y la app, que preguntan (<see cref="DbAdmin.Inspect"/>).
/// </summary>
public static partial class DbDump
{
    /// <summary>Cada cuánto se avisa del progreso (bytes), para no saturar la pantalla.</summary>
    public const int ProgressStep = 1 << 20;

    /// <summary>Lo que se lee del principio para saber de dónde viene y qué adaptar.</summary>
    public const int SniffBytes = 4 << 20;

    /// <summary>PURO: la orden que vuelca <paramref name="database"/> por stdout.</summary>
    public static CommandSpec ExportCommand(DbTool tool, string database)
    {
        CheckName(database, "exportar");
        var args = new List<string>
        {
            // El fichero de opciones tiene que ir el primero, o no lo lee.
            $"--defaults-file={tool.DefaultsFile}",
            // Una foto coherente de las tablas InnoDB sin bloquearlas, y también lo que no son tablas.
            "--single-transaction", "--routines", "--triggers", "--events",
            "--default-character-set=utf8mb4",
            // Los binarios en hexadecimal: el fichero queda en texto y entra igual en cualquier servidor.
            "--hex-blob",
        };
        if (tool.Engine == DbEngine.MySql && tool.Version is not { Major: 5, Minor: < 6 })
            args.Add("--set-gtid-purged=OFF");    // si no, un servidor con GTID mete SET @@GLOBAL.GTID_PURGED, que no entra en otro
        if (tool.Engine == DbEngine.MySql && tool.Version is not { Major: < 8 })
            args.Add("--skip-column-statistics");   // ANALYZE ... UPDATE HISTOGRAM es solo de MySQL 8
        args.Add(database);
        return new CommandSpec(tool.Dump, args);
    }

    /// <summary>
    /// PURO: la orden que importa por stdin en <paramref name="database"/>, que ya existe. Si el
    /// cliente lo admite, sin poder ejecutar comandos del propio fichero (<c>\!</c>, <c>system</c>).
    /// </summary>
    public static CommandSpec ImportCommand(DbTool tool, string database)
    {
        CheckTarget(database);
        var args = new List<string>
        {
            $"--defaults-file={tool.DefaultsFile}",
            "--default-character-set=utf8mb4",
            // El límite del cliente; el del servidor es max_allowed_packet en usuario.cnf.
            "--max-allowed-packet=1G",
        };
        if (SqlCompat.HasClientSandbox(tool.Engine, tool.Version))
            args.Add(tool.Engine == DbEngine.MariaDb ? "--sandbox" : "--skip-system-command");
        args.Add(database);
        return new CommandSpec(tool.Client, args);
    }

    /// <summary>
    /// Vuelca <paramref name="database"/> en <paramref name="file"/> (comprimido si acaba en
    /// <c>.gz</c>). Escribe en un temporal al lado y lo renombra al acabar, pisando el que hubiera:
    /// quien llama ya ha preguntado. Si falla o se cancela, borra el temporal y lanza.
    /// </summary>
    /// <param name="written">Los bytes volcados hasta ahora (sin comprimir: lo que lleva el volcador).</param>
    /// <param name="lines">Lo que dice el volcador por stderr (avisos, errores).</param>
    public static async Task<DbExportResult> ExportAsync(DbTool tool, string database, string file, ICommandRunner runner,
        IProgress<DbTransferProgress>? written, IProgress<string>? lines, CancellationToken cancellation)
    {
        var spec = ExportCommand(tool, database);
        var full = Path.GetFullPath(file);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var compress = IsGzipName(full);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.part-{Guid.NewGuid().ToString("N")[..8]}");
        var watch = Stopwatch.StartNew();
        try
        {
            CommandResult result;
            await using (var disk = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                // Lo que se cuenta es lo que sale del volcador, antes de comprimirlo.
                var gzip = compress ? new GZipStream(disk, CompressionLevel.Optimal, leaveOpen: true) : null;
                await using var counted = new ProgressStream(gzip ?? (Stream)disk, null, written, leaveOpen: true);
                try
                {
                    result = await runner.RunAsync(spec with { StdoutStream = counted }, lines, cancellation);
                    counted.ReportNow();
                }
                finally
                {
                    if (gzip is not null) await gzip.DisposeAsync();   // el final del .gz
                }
                await disk.FlushAsync(cancellation);
            }
            if (!result.Succeeded)
                throw new RoostException(DbDumpErrors.ExplainExport(database, result, tool));
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return new DbExportResult(database, full, new FileInfo(full).Length, compress, watch.Elapsed);
    }

    /// <summary>
    /// Importa <paramref name="file"/> en <paramref name="database"/>, que ya tiene que existir.
    /// Lanza <see cref="RoostException"/> con lo que dijo el cliente, explicado, si falla.
    /// </summary>
    /// <param name="read">Los bytes leídos del fichero (comprimidos, si lo está) frente a su tamaño.</param>
    public static async Task<DbImportResult> ImportAsync(DbTool tool, string database, string file, CompatPlan? plan, ICommandRunner runner,
        IProgress<DbTransferProgress>? read, IProgress<string>? lines, CancellationToken cancellation)
    {
        var spec = ImportCommand(tool, database);
        var watch = Stopwatch.StartNew();
        await using var source = OpenSql(file, read);
        await using var filter = new SqlImportFilter(source.Text, plan);
        var result = await runner.RunAsync(spec with { StdinStream = filter }, lines, cancellation);
        source.Progress.ReportNow();
        if (!result.Succeeded)
            throw new RoostException(DbDumpErrors.ExplainImport(database, result, tool));
        return new DbImportResult(database, Path.GetFullPath(file), source.Length, watch.Elapsed, filter.Notes(database));
    }

    /// <summary>Un fichero abierto para importar: el texto SQL (ya descomprimido) y lo leído del fichero.</summary>
    public sealed class SqlSource(Stream text, ProgressStream progress, long length, DumpFormat format, IDisposable? owner) : IAsyncDisposable, IDisposable
    {
        public Stream Text { get; } = text;
        public ProgressStream Progress { get; } = progress;
        public long Length { get; } = length;
        public DumpFormat Format { get; } = format;

        public void Dispose()
        {
            Text.Dispose();
            owner?.Dispose();
            Progress.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Abre un .sql, un .sql.gz o un .zip con un solo .sql dentro, por lo que es (sus primeros
    /// bytes) y no por cómo se llama. Lanza <see cref="RoostException"/> si no existe o es otra cosa.
    /// </summary>
    public static SqlSource OpenSql(string file, IProgress<DbTransferProgress>? read = null)
    {
        if (!File.Exists(file)) throw new RoostException($"No existe {file}.");
        var disk = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var length = disk.Length;
            Span<byte> magic = stackalloc byte[4];
            var n = disk.Read(magic);
            disk.Position = 0;
            var counted = new ProgressStream(disk, length, read);
            if (n >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
                return new SqlSource(new GZipStream(counted, CompressionMode.Decompress), counted, length, DumpFormat.Gzip, null);
            if (n == 4 && magic[0] == (byte)'P' && magic[1] == (byte)'K' && magic[2] == 3 && magic[3] == 4)
            {
                var zip = new ZipArchive(counted, ZipArchiveMode.Read, leaveOpen: true);
                var sql = zip.Entries.Where(e => e.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).ToList();
                if (sql.Count != 1)
                {
                    zip.Dispose();
                    throw new RoostException(sql.Count == 0
                        ? $"{Path.GetFileName(file)} es un .zip sin ningun .sql dentro."
                        : $"{Path.GetFileName(file)} trae {sql.Count} ficheros .sql ({string.Join(", ", sql.Take(4).Select(e => e.FullName))}): descomprimelo e importa cada uno a su base de datos.");
                }
                return new SqlSource(sql[0].Open(), counted, length, DumpFormat.Zip, zip);
            }
            if (n >= 3 && (magic[..3].SequenceEqual("BZh"u8) || magic.SequenceEqual((ReadOnlySpan<byte>)[0xFD, (byte)'7', (byte)'z', (byte)'X']) || magic.SequenceEqual((ReadOnlySpan<byte>)[(byte)'7', (byte)'z', 0xBC, 0xAF])))
                throw new RoostException($"{Path.GetFileName(file)} esta comprimido en un formato que Roost no abre (bzip2, xz o 7z): descomprimelo primero, o usa .sql.gz o .zip.");
            return new SqlSource(counted, counted, length, DumpFormat.Sql, null);
        }
        catch
        {
            disk.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Mira el principio del fichero (hasta <see cref="SniffBytes"/>, ya descomprimido) para saber
    /// de qué motor viene, qué colaciones y qué <c>sql_mode</c> usa y si trae <c>CREATE DATABASE</c>.
    /// </summary>
    public static async Task<DumpInfo> SniffAsync(string file, CancellationToken cancellation = default)
    {
        await using var source = OpenSql(file);
        var buffer = new byte[SniffBytes];
        var total = 0;
        try
        {
            int n;
            while (total < buffer.Length && (n = await source.Text.ReadAsync(buffer.AsMemory(total), cancellation)) > 0) total += n;
        }
        catch (InvalidDataException ex)
        {
            throw new RoostException($"{Path.GetFileName(file)} esta roto: {ex.Message}");
        }
        return Sniff(buffer.AsSpan(0, total));
    }

    /// <summary>PURO: lo mismo sobre unos bytes ya leídos.</summary>
    public static DumpInfo Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) head = head[3..];
        var looksLikeSql = head.Length > 0 && head[..Math.Min(head.Length, 1 << 16)].IndexOf((byte)0) < 0;
        var text = Encoding.Latin1.GetString(head);
        DbEngine? engine = null;
        string? version = null, database = null;
        var databases = new List<string>();
        var collations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        // La última puede estar cortada: no se mira.
        foreach (var raw in lines.Take(lines.Length > 1 ? lines.Length - 1 : 1))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith('#'))
            {
                if (line.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)) engine = DbEngine.MariaDb;
                else if (engine is null && line.Contains("MySQL dump", StringComparison.OrdinalIgnoreCase)) engine = DbEngine.MySql;
                if (version is null && ServerVersion().Match(line) is { Success: true } v)
                {
                    version = v.Groups[1].Value;
                    if (engine is null) engine = line.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? DbEngine.MariaDb : DbEngine.MySql;
                }
                if (database is null && HeaderDatabase().Match(line) is { Success: true } d) database = d.Groups[1].Value;
                continue;
            }
            if (SqlImportFilter.IsDataLine(Encoding.Latin1.GetBytes(line.Length > 32 ? line[..32] : line))) continue;
            if (DatabaseLine().Match(line.TrimStart()) is { Success: true } db && db.Groups[1].Value.Length > 0 && !databases.Contains(db.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                databases.Add(db.Groups[1].Value);
            foreach (Match c in SqlCompat.CollationPattern().Matches(line)) collations.Add(c.Groups[2].Value.ToLowerInvariant());
            foreach (Match m in SqlCompat.SqlModePattern().Matches(line))
                foreach (var mode in m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    modes.Add(mode.ToUpperInvariant());
        }
        return new DumpInfo
        {
            SourceEngine = engine,
            SourceVersion = version,
            SourceDatabase = database,
            Databases = databases,
            Collations = collations,
            SqlModes = modes,
            LooksLikeSql = looksLikeSql,
        };
    }

    /// <summary>PURO: <c>.gz</c> (también <c>.sql.gz</c>) = comprimir al exportar.</summary>
    public static bool IsGzipName(string file) => file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>PURO: el nombre que se propone para exportar: <c>tienda-20261024-1830.sql</c>.</summary>
    public static string SuggestedFileName(string database, DateTime now, bool gzip = false) =>
        $"{database}-{now:yyyyMMdd-HHmm}.sql{(gzip ? ".gz" : "")}";

    internal static void CheckName(string database, string what)
    {
        if (!DbAdmin.IsValidName(database))
            throw new RoostException($"'{database}' no vale como nombre: usa solo letras, numeros y _ (hasta 64).");
        if (DbAdmin.SystemSchemas.Contains(database))
            throw new RoostException($"{database} es del propio servidor: no se puede {what}.");
    }

    /// <summary>El destino de una importación: un nombre válido y nunca una base de datos del sistema.</summary>
    public static void CheckTarget(string database) => CheckName(database, "importar encima");

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"(?:Server version|Versi\S*n del servidor|Distrib)[:\s]+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServerVersion();

    [GeneratedRegex(@"(?:\bDatabase|Base de datos)\s*:\s*`?([A-Za-z0-9_$\-]+)`?", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderDatabase();

    [GeneratedRegex(@"^(?:/\*!\d{5,6}\s*)?(?:USE|CREATE\s+(?:DATABASE|SCHEMA))\b(?:\s*/\*!\d{5,6}\s*IF\s+NOT\s+EXISTS\s*\*/|\s+IF\s+NOT\s+EXISTS)?\s*`?([^`;\s*/]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseLine();
}

/// <summary>
/// Un flujo que pasa todo tal cual y cuenta lo que lee o escribe (su posición), avisando cada
/// <see cref="DbDump.ProgressStep"/> bytes. Lo usan la exportación (lo escrito en el disco) y la
/// importación (lo leído del fichero, aunque vaya comprimido).
/// </summary>
public sealed class ProgressStream(Stream inner, long? total, IProgress<DbTransferProgress>? progress, bool leaveOpen = false) : Stream
{
    private long _bytes;
    private long _reported = -1;

    /// <summary>Los bytes del fichero recorridos hasta ahora.</summary>
    public long Bytes => _bytes;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            inner.Position = value;
            Count(0);
        }
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = inner.Seek(offset, origin);
        Count(0);
        return position;
    }

    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Count(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Count(buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        Count(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Count(buffer.Length);
    }

    /// <summary>Avisa ya de lo que lleva (al acabar), aunque no haya llegado al siguiente escalón.</summary>
    public void ReportNow()
    {
        if (_reported == _bytes) return;
        _reported = _bytes;
        progress?.Report(new DbTransferProgress(_bytes, total));
    }

    private int Count(int n)
    {
        // Leyendo, la posición (un .zip salta a su índice antes de los datos); escribiendo, lo escrito.
        _bytes = inner.CanSeek && !inner.CanWrite ? inner.Position : _bytes + n;
        if (progress is not null && _bytes - _reported >= DbDump.ProgressStep) ReportNow();
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// PURO: lo que dijo el cliente o el volcador al fallar, explicado para el usuario (sin tildes:
/// lo enseña también la CLI). Reconoce los errores típicos de importar un volcado de otro motor.
/// </summary>
public static partial class DbDumpErrors
{
    /// <summary>El error de MySQL/MariaDB de la salida: código, línea y mensaje.</summary>
    public sealed record ClientError(int Code, int? Line, string Message);

    public static ClientError? Parse(string output)
    {
        var match = ErrorLine().Match(output);
        if (!match.Success) return null;
        return new ClientError(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : null, match.Groups[3].Value.Trim());
    }

    /// <summary>true si el fallo es de los que <c>--compat</c> (adaptar al vuelo) puede arreglar.</summary>
    public static bool IsCompatProblem(string output) => Parse(output)?.Code is 1273 or 1231 or 1115;

    public static string ExplainImport(string database, CommandResult result, DbTool tool)
    {
        if (result.TimedOut) return $"La importacion en {database} se ha pasado del tiempo y se ha parado.";
        var output = (result.Stderr + "\n" + result.Stdout).Trim();
        if (Parse(output) is not { } error)
            return output.Length == 0 ? $"El cliente ha salido con el codigo {result.ExitCode} sin decir por que." : LastLines(output);
        var at = error.Line is { } line ? $" (linea {line} del fichero)" : "";
        var server = tool.Label;
        return error.Code switch
        {
            1273 => $"El fichero usa una colacion que {server} no conoce{at}: {error.Message}. Suele ser un volcado de MySQL 8 en una MariaDB vieja, o de MariaDB 11 en MySQL: repitelo adaptandolo al vuelo (--compat en la CLI), que la cambia por utf8mb4_unicode_ci.",
            1115 => $"El fichero usa un juego de caracteres que {server} no conoce{at}: {error.Message}.",
            1231 => $"El fichero pone un sql_mode que {server} no admite{at}: {error.Message}. Suele ser NO_AUTO_CREATE_USER de un volcado de MariaDB en MySQL 8: repitelo adaptandolo al vuelo (--compat en la CLI).",
            1050 => $"Una tabla del fichero ya existe en {database} y el volcado no la borra antes{at}: {error.Message}. Importalo en una base de datos vacia.",
            1064 => $"El fichero tiene algo que {server} no entiende{at}: {error.Message}. Si viene de otro motor u otra version, puede que no sea compatible.",
            1044 or 1045 or 1142 or 1227 => $"Sin permiso{at}: {error.Message}.",
            1153 or 2006 or 2013 => $"El servidor ha cortado la conexion{at}: {error.Message}. Suele ser una sentencia mas grande que max_allowed_packet: subelo en usuario.cnf (max_allowed_packet = 256M), reinicia la base de datos y repite.",
            2002 or 2003 => $"No se pudo conectar con {server}: {error.Message}. ¿Esta en marcha?",
            _ => $"{error.Message}{at} (error {error.Code}).",
        };
    }

    public static string ExplainExport(string database, CommandResult result, DbTool tool)
    {
        if (result.TimedOut) return $"La exportacion de {database} se ha pasado del tiempo y se ha parado.";
        var output = result.Stderr.Trim();
        if (output.Contains("Unknown database", StringComparison.OrdinalIgnoreCase) || output.Contains(": 1049", StringComparison.Ordinal))
            return $"No existe la base de datos {database} en {tool.Label}.";
        if (output.Contains("Can't connect", StringComparison.OrdinalIgnoreCase) || output.Contains("2002", StringComparison.Ordinal) || output.Contains("2003", StringComparison.Ordinal))
            return $"No se pudo conectar con {tool.Label}: ¿esta en marcha?";
        if (output.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            return $"{tool.Label} no deja entrar a root: {LastLines(output)}";
        return output.Length == 0 ? $"El volcado ha salido con el codigo {result.ExitCode} sin decir por que." : LastLines(output);
    }

    private static string LastLines(string output) =>
        string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));

    [GeneratedRegex(@"ERROR (\d+) \([0-9A-Z]{5}\)(?: at line (\d+))?[^:]*:\s*(.*)")]
    private static partial Regex ErrorLine();
}
