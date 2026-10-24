using System.IO.Compression;
using System.Text;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// BD-2: exportar e importar un .sql sin binarios reales. El volcador y el cliente son
/// <see cref="FakeCommandRunner"/> (o uno asíncrono) que escriben en el StdoutStream o leen el
/// StdinStream de la orden, como harían de verdad; el gzip, el zip y el progreso van en memoria
/// y en una carpeta temporal.
/// </summary>
public sealed class DbDumpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-dbdump-" + Guid.NewGuid().ToString("N"));

    private static readonly DbTool MariaDb = new(DbEngine.MariaDb, ComponentVersion.TryParse("11.4.13", out var v) ? v : null,
        @"C:\roost\bin\mariadb\11.4.13\bin\mariadb.exe", @"C:\roost\bin\mariadb\11.4.13\bin\mariadb-dump.exe", @"C:\roost\etc\mariadb\11.4\my.ini");

    private static DbTool MySql(string version) => new(DbEngine.MySql, ComponentVersion.TryParse(version, out var v) ? v : null,
        @"C:\roost\bin\mysql\bin\mysql.exe", @"C:\roost\bin\mysql\bin\mysqldump.exe", @"C:\roost\etc\mysql\my.ini");

    private const string Dump = """
        /*M!999999\- enable the sandbox mode */
        -- MariaDB dump 10.19-11.4.13-MariaDB, for Win64 (AMD64)
        --
        -- Host: 127.0.0.1    Database: tienda
        -- ------------------------------------------------------
        -- Server version	11.4.13-MariaDB

        DROP TABLE IF EXISTS `clientes`;
        CREATE TABLE `clientes` (
          `id` int(11) NOT NULL AUTO_INCREMENT,
          `nombre` varchar(100) COLLATE utf8mb4_unicode_ci NOT NULL,
          PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO `clientes` VALUES (1,'Ana Núñez'),(2,'José');

        """;

    public DbDumpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Reports : IProgress<DbTransferProgress>
    {
        public List<DbTransferProgress> Items { get; } = [];
        public void Report(DbTransferProgress value) { lock (Items) Items.Add(value); }
    }

    /// <summary>Lo que le llega al cliente por stdin, leído como lo haría el runner real (asíncrono, por bloques).</summary>
    private sealed class ReadingRunner(int exitCode = 0, string stderr = "") : ICommandRunner
    {
        public List<CommandSpec> Calls { get; } = [];
        public byte[] Received { get; private set; } = [];

        public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
        {
            Calls.Add(spec);
            using var copy = new MemoryStream();
            var buffer = new byte[7919];   // un tamaño raro: los cortes caen en mitad de las líneas
            int n;
            while ((n = await spec.StdinStream!.ReadAsync(buffer, cancellation)) > 0) copy.Write(buffer, 0, n);
            Received = copy.ToArray();
            if (stderr.Length > 0) lines?.Report(stderr);
            return new CommandResult(exitCode, "", stderr, false);
        }
    }

    private string Write(string name, string text) => WriteBytes(name, Encoding.UTF8.GetBytes(text));

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private static string Gunzip(string file)
    {
        using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // --- Las órdenes ---

    [Fact]
    public void ExportCommand_ElMyIniPrimero_YUnaFotoCoherenteConRutinasTriggersYEventos()
    {
        var spec = DbDump.ExportCommand(MariaDb, "tienda");

        Assert.Equal(MariaDb.Dump, spec.Exe);
        Assert.Equal(@"--defaults-file=C:\roost\etc\mariadb\11.4\my.ini", spec.Arguments[0]);
        Assert.Equal(["--single-transaction", "--routines", "--triggers", "--events", "--default-character-set=utf8mb4", "--hex-blob"], spec.Arguments.Skip(1).Take(6));
        Assert.Equal("tienda", spec.Arguments[^1]);
        Assert.DoesNotContain("--set-gtid-purged=OFF", spec.Arguments);   // eso es de MySQL
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportCommand_EnMySql_SinGtidNiHistogramas_YEnLa57SinHistogramas()
    {
        var mysql84 = DbDump.ExportCommand(MySql("8.4.11"), "tienda").Arguments;
        var mysql57 = DbDump.ExportCommand(MySql("5.7.44"), "tienda").Arguments;

        Assert.Contains("--set-gtid-purged=OFF", mysql84);
        Assert.Contains("--skip-column-statistics", mysql84);
        Assert.Contains("--set-gtid-purged=OFF", mysql57);
        Assert.DoesNotContain("--skip-column-statistics", mysql57);   // el mysqldump de la 5.7 no conoce la opción
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("SYS")]
    [InlineData("information_schema")]
    [InlineData("tienda; DROP DATABASE mysql")]
    [InlineData("mi-tienda")]
    [InlineData("")]
    public void LasOrdenes_NuncaConUnaDelSistemaNiConNombresRaros(string name)
    {
        Assert.Throws<RoostException>(() => DbDump.ExportCommand(MariaDb, name));
        Assert.Throws<RoostException>(() => DbDump.ImportCommand(MariaDb, name));
    }

    [Fact]
    public void ImportCommand_PorStdin_ConElLimiteDelClienteAlto_YSinComandosDelFichero()
    {
        var maria = DbDump.ImportCommand(MariaDb, "tienda");
        var mysql84 = DbDump.ImportCommand(MySql("8.4.11"), "tienda");
        var mysql57 = DbDump.ImportCommand(MySql("5.7.44"), "tienda");

        Assert.Equal(MariaDb.Client, maria.Exe);
        Assert.Equal(@"--defaults-file=C:\roost\etc\mariadb\11.4\my.ini", maria.Arguments[0]);
        Assert.Contains("--default-character-set=utf8mb4", maria.Arguments);
        Assert.Contains("--max-allowed-packet=1G", maria.Arguments);
        Assert.Contains("--sandbox", maria.Arguments);
        Assert.Contains("--skip-system-command", mysql84.Arguments);
        Assert.DoesNotContain("--skip-system-command", mysql57.Arguments);
        Assert.Equal("tienda", maria.Arguments[^1]);
    }

    [Theory]
    [InlineData(DbEngine.MariaDb, "11.4.13", true)]
    [InlineData(DbEngine.MariaDb, "11.4.1", false)]
    [InlineData(DbEngine.MariaDb, "10.11.19", true)]
    [InlineData(DbEngine.MariaDb, "10.4.32", false)]
    [InlineData(DbEngine.MariaDb, "12.3.3", true)]
    [InlineData(DbEngine.MySql, "8.0.46", true)]
    [InlineData(DbEngine.MySql, "8.0.30", false)]
    [InlineData(DbEngine.MySql, "8.4.11", true)]
    [InlineData(DbEngine.MySql, "9.1.0", true)]
    [InlineData(DbEngine.MySql, "5.7.44", false)]
    public void HasClientSandbox_SegunLaVersion(DbEngine engine, string version, bool expected)
    {
        Assert.Equal(expected, SqlCompat.HasClientSandbox(engine, ComponentVersion.TryParse(version, out var v) ? v : null));
    }

    // --- Exportar ---

    [Fact]
    public async Task Export_EscribeElVolcado_SinDejarTemporales_YCuentaLoEscrito()
    {
        var runner = new FakeCommandRunner().On("--defaults-file", onRun: spec => spec.StdoutStream!.Write(Encoding.UTF8.GetBytes(Dump)));
        var file = Path.Combine(_dir, "sub", "tienda.sql");
        var reports = new Reports();

        var result = await DbDump.ExportAsync(MariaDb, "tienda", file, runner, reports, null, CancellationToken.None);

        Assert.Equal(Dump, File.ReadAllText(file));
        Assert.Equal(file, result.File);
        Assert.False(result.Compressed);
        Assert.Equal(new FileInfo(file).Length, result.Bytes);
        Assert.Equal(result.Bytes, reports.Items[^1].Bytes);
        Assert.Null(reports.Items[^1].Total);
        Assert.Equal(["tienda.sql"], Directory.GetFiles(Path.Combine(_dir, "sub")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Export_ConGz_ComprimeAlVuelo()
    {
        var runner = new FakeCommandRunner().On("--defaults-file", onRun: spec => spec.StdoutStream!.Write(Encoding.UTF8.GetBytes(Dump)));
        var file = Path.Combine(_dir, "tienda.sql.gz");

        var result = await DbDump.ExportAsync(MariaDb, "tienda", file, runner, null, null, CancellationToken.None);

        Assert.True(result.Compressed);
        Assert.Equal(Dump, Gunzip(file));
    }

    [Fact]
    public async Task Export_SiFalla_NoDejaNada_YDiceQuePaso()
    {
        var runner = new FakeCommandRunner().On("--defaults-file", exitCode: 2,
            stderr: "mariadb-dump: Got error: 1049: \"Unknown database 'tienda'\" when selecting the database",
            onRun: spec => spec.StdoutStream!.Write("-- a medias"u8));
        var file = Path.Combine(_dir, "tienda.sql");
        File.WriteAllText(file, "el de antes");

        var ex = await Assert.ThrowsAsync<RoostException>(() => DbDump.ExportAsync(MariaDb, "tienda", file, runner, null, null, CancellationToken.None));

        Assert.Equal("No existe la base de datos tienda en MariaDB 11.4.13.", ex.Message);
        Assert.Equal("el de antes", File.ReadAllText(file));   // el de antes no se pisa con uno a medias
        Assert.Equal(["tienda.sql"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Export_Cancelado_NoDejaNada()
    {
        var runner = new FakeCommandRunner().On("--defaults-file", onRun: spec =>
        {
            spec.StdoutStream!.Write("-- a medias"u8);
            throw new OperationCanceledException();
        });
        var file = Path.Combine(_dir, "tienda.sql.gz");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DbDump.ExportAsync(MariaDb, "tienda", file, runner, null, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    // --- Importar ---

    [Fact]
    public async Task Import_MandaElFicheroPorStdin_YCuentaLoLeidoFrenteAlTamano()
    {
        var file = Write("tienda.sql", Dump);
        var runner = new ReadingRunner();
        var reports = new Reports();

        var result = await DbDump.ImportAsync(MariaDb, "tienda_copia", file, null, runner, reports, null, CancellationToken.None);

        // Sin plan, MariaDB lo recibe tal cual (también su línea de sandbox).
        Assert.Equal(File.ReadAllBytes(file), runner.Received);
        Assert.Equal("tienda_copia", runner.Calls[0].Arguments[^1]);
        Assert.Equal(new FileInfo(file).Length, result.Bytes);
        Assert.Equal(new DbTransferProgress(result.Bytes, result.Bytes), reports.Items[^1]);
        Assert.Equal(100, reports.Items[^1].Percent);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task Import_UnFicheroGrande_AvisaPorEscalones_SinCargarloEntero()
    {
        // 12 MB de INSERT: el progreso sale cada MB y el último dice el total.
        var row = "INSERT INTO `t` VALUES " + string.Join(",", Enumerable.Range(0, 200).Select(i => $"({i},'fila {i}')")) + ";\n";
        var text = new StringBuilder();
        while (text.Length < 12 << 20) text.Append(row);
        var file = Write("grande.sql", text.ToString());
        var reports = new Reports();

        await DbDump.ImportAsync(MariaDb, "tienda", file, null, new ReadingRunner(), reports, null, CancellationToken.None);

        Assert.InRange(reports.Items.Count, 10, 14);
        Assert.Equal(new FileInfo(file).Length, reports.Items[^1].Bytes);
        Assert.True(reports.Items.Zip(reports.Items.Skip(1)).All(p => p.First.Bytes < p.Second.Bytes));
    }

    [Fact]
    public async Task Import_ConGz_LoDescomprimeAlVuelo_YElProgresoEsDelComprimido()
    {
        var file = WriteBytes("tienda.sql.gz", Gzip(Dump));
        var runner = new ReadingRunner();
        var reports = new Reports();

        var result = await DbDump.ImportAsync(MariaDb, "tienda", file, null, runner, reports, null, CancellationToken.None);

        Assert.Equal(Dump, Encoding.UTF8.GetString(runner.Received));
        Assert.Equal(new FileInfo(file).Length, reports.Items[^1].Bytes);
        Assert.Equal(new FileInfo(file).Length, result.Bytes);
    }

    [Fact]
    public async Task Import_UnGzSinLaExtension_SeReconocePorSusBytes()
    {
        var file = WriteBytes("tienda.sql", Gzip(Dump));
        var runner = new ReadingRunner();

        await DbDump.ImportAsync(MariaDb, "tienda", file, null, runner, null, null, CancellationToken.None);

        Assert.Equal(Dump, Encoding.UTF8.GetString(runner.Received));
    }

    [Fact]
    public async Task Import_UnZipConUnSoloSql_LoAbre_YConDos_SeNiega()
    {
        var one = Path.Combine(_dir, "uno.zip");
        using (var zip = ZipFile.Open(one, ZipArchiveMode.Create))
        {
            using (var entry = new StreamWriter(zip.CreateEntry("tienda.sql").Open())) entry.Write(Dump);
            using (var readme = new StreamWriter(zip.CreateEntry("LEEME.txt").Open())) readme.Write("hola");
        }
        var two = Path.Combine(_dir, "dos.zip");
        using (var zip = ZipFile.Open(two, ZipArchiveMode.Create))
        {
            zip.CreateEntry("a.sql");
            zip.CreateEntry("b.sql");
        }
        var runner = new ReadingRunner();

        await DbDump.ImportAsync(MariaDb, "tienda", one, null, runner, null, null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "tienda", two, null, new ReadingRunner(), null, null, CancellationToken.None));

        Assert.Equal(Dump, Encoding.UTF8.GetString(runner.Received));
        Assert.Contains("trae 2 ficheros .sql (a.sql, b.sql)", ex.Message);
    }

    [Fact]
    public async Task Import_OtrosComprimidos_ONoExiste_LoDiceClaro()
    {
        var bz = WriteBytes("tienda.sql.bz2", [(byte)'B', (byte)'Z', (byte)'h', (byte)'9', 1, 2, 3]);

        var compressed = await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "tienda", bz, null, new ReadingRunner(), null, null, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "tienda", Path.Combine(_dir, "no.sql"), null, new ReadingRunner(), null, null, CancellationToken.None));

        Assert.Contains("bzip2, xz o 7z", compressed.Message);
        Assert.StartsWith("No existe", missing.Message);
    }

    [Fact]
    public async Task Import_NuncaSobreUnaDelSistema_NiLlegaAlCliente()
    {
        var file = Write("tienda.sql", Dump);
        var runner = new ReadingRunner();

        await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "mysql", file, null, runner, null, null, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Import_SiElClienteFalla_LoExplica_YPorUnaColacion_SugiereAdaptarlo()
    {
        var file = Write("tienda.sql", Dump);
        var runner = new ReadingRunner(1, "ERROR 1273 (HY000) at line 25: Unknown collation: 'utf8mb4_0900_ai_ci'");

        var ex = await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "tienda", file, null, runner, null, null, CancellationToken.None));

        Assert.Contains("colacion que MariaDB 11.4.13 no conoce (linea 25 del fichero)", ex.Message);
        Assert.Contains("--compat", ex.Message);
    }

    [Fact]
    public async Task Import_UnVolcadoConCreateDatabaseYUse_VaEnteroALaElegida()
    {
        var file = Write("heidi.sql", """
            -- HeidiSQL Versión:             12.6.0.6765
            CREATE DATABASE IF NOT EXISTS `tienda` /*!40100 DEFAULT CHARACTER SET utf8mb4 */;
            USE `tienda`;
            CREATE TABLE IF NOT EXISTS `t` (`id` int);
            INSERT INTO `t` VALUES (1);

            """);
        var runner = new ReadingRunner();

        var result = await DbDump.ImportAsync(MariaDb, "tienda_copia", file, null, runner, null, null, CancellationToken.None);

        var sent = Encoding.UTF8.GetString(runner.Received);
        Assert.Contains("-- roost: CREATE DATABASE IF NOT EXISTS `tienda`", sent);
        Assert.Contains("-- roost: USE `tienda`;", sent);
        Assert.Contains("\nINSERT INTO `t` VALUES (1);", sent);
        Assert.Equal("El fichero traia CREATE DATABASE/USE de tienda: se han quitado y todo ha ido a tienda_copia.", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task Import_UnVolcadoDeVariasBasesDeDatos_SeNiegaASeguir()
    {
        var file = Write("todas.sql", "CREATE DATABASE `a`;\nUSE `a`;\nCREATE TABLE t (id int);\nCREATE DATABASE `b`;\nUSE `b`;\n");

        var ex = await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "tienda", file, null, new ReadingRunner(), null, null, CancellationToken.None));

        Assert.Contains("mas de una base de datos (a y b)", ex.Message);
    }

    [Fact]
    public async Task Import_UnVolcadoDeTodoElServidor_SeNiegaDesdeLaPrimera()
    {
        var file = Write("todo.sql", "-- MySQL dump\n/*!40000 DROP DATABASE IF EXISTS `mysql`*/;\nCREATE DATABASE /*!32312 IF NOT EXISTS*/ `mysql`;\nUSE `mysql`;\n");

        var ex = await Assert.ThrowsAsync<RoostException>(() => DbDump.ImportAsync(MariaDb, "tienda", file, null, new ReadingRunner(), null, null, CancellationToken.None));

        Assert.Contains("todo el servidor", ex.Message);
    }

    // --- El filtro, línea a línea ---

    /// <summary>Lee el filtro de un byte en un byte: así cada corte cae en un sitio distinto de cada línea.</summary>
    private sealed class Trickle(byte[] bytes) : Stream
    {
        private int _position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= bytes.Length || count == 0) return 0;
            buffer[offset] = bytes[_position++];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (string Text, SqlImportFilter Filter) Filter(string sql, CompatPlan? plan = null, bool trickle = false)
    {
        var (bytes, filter) = FilterBytes(Encoding.UTF8.GetBytes(sql), plan, trickle);
        return (Encoding.UTF8.GetString(bytes), filter);
    }

    private static (byte[] Bytes, SqlImportFilter Filter) FilterBytes(byte[] sql, CompatPlan? plan = null, bool trickle = false)
    {
        var filter = new SqlImportFilter(trickle ? new Trickle(sql) : new MemoryStream(sql), plan);
        using var output = new MemoryStream();
        filter.CopyTo(output, 333);
        return (output.ToArray(), filter);
    }

    private static readonly IReadOnlySet<string> MariaDb104Collations = new HashSet<string>(["utf8mb4_unicode_ci", "utf8mb4_general_ci", "utf8mb4_bin", "utf8mb3_unicode_ci", "utf8mb3_bin", "latin1_swedish_ci"], StringComparer.OrdinalIgnoreCase);

    private const string MySql8 = """
        -- MySQL dump 10.13  Distrib 8.4.11, for Win64 (x86_64)
        --
        -- Host: 127.0.0.1    Database: blog_laragon
        -- ------------------------------------------------------
        -- Server version	8.4.11

        /*!50503 SET NAMES utf8mb4 */;
        CREATE TABLE `posts` (
          `id` int NOT NULL AUTO_INCREMENT,
          `titulo` varchar(200) COLLATE utf8mb4_es_0900_ai_ci NOT NULL,
          `slug` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs DEFAULT NULL,
          PRIMARY KEY (`id`)
        ) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
        INSERT INTO `posts` VALUES (1,'Hablo de utf8mb4_0900_ai_ci y COLLATE utf8mb4_0900_ai_ci','x');
        /*!50001 SET collation_connection      = utf8mb4_0900_ai_ci */;

        """;

    [Fact]
    public void Filtro_SinPlan_DejaTodoIgual_SalvoElBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(MySql8)).ToArray();

        var (text, filter) = FilterBytes(bytes, trickle: true);

        Assert.Equal(MySql8, Encoding.UTF8.GetString(text));
        Assert.Empty(filter.Notes("blog"));
    }

    [Fact]
    public void Filtro_AdaptaLasColacionesQueNoConoceElDestino_PeroNuncaEnLosDatos()
    {
        var plan = SqlCompat.PlanFor(DbEngine.MariaDb, ComponentVersion.TryParse("10.4.32", out var v) ? v : null, MariaDb104Collations);

        foreach (var trickle in new[] { false, true })
        {
            var (text, filter) = Filter(MySql8, plan, trickle);

            Assert.Contains("`titulo` varchar(200) COLLATE utf8mb4_unicode_ci NOT NULL,", text);
            Assert.Contains("CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL,", text);
            Assert.Contains("DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;", text);
            Assert.Contains("SET collation_connection      = utf8mb4_unicode_ci */;", text);
            // Los datos pasan sin tocar, aunque hablen de colaciones.
            Assert.Contains("(1,'Hablo de utf8mb4_0900_ai_ci y COLLATE utf8mb4_0900_ai_ci','x');", text);
            Assert.Equal(("utf8mb4_unicode_ci", 2), filter.MappedCollations["utf8mb4_0900_ai_ci"]);
            Assert.Contains("Colacion utf8mb4_es_0900_ai_ci cambiada por utf8mb4_unicode_ci (1 vez).", filter.Notes("blog"));
        }
    }

    [Fact]
    public void Filtro_LoQueElDestinoConoce_NoSeToca()
    {
        // MariaDB 11.4.13 sí tiene utf8mb4_0900_ai_ci y utf8mb4_0900_as_cs (comprobado en real), pero no las de idioma.
        var known = new HashSet<string>(MariaDb104Collations.Concat(["utf8mb4_0900_ai_ci", "utf8mb4_0900_as_cs"]), StringComparer.OrdinalIgnoreCase);
        var plan = SqlCompat.PlanFor(DbEngine.MariaDb, null, known);

        var (text, filter) = Filter(MySql8, plan);

        Assert.Contains("COLLATE=utf8mb4_0900_ai_ci;", text);
        Assert.Contains("COLLATE utf8mb4_0900_as_cs", text);
        Assert.Contains("COLLATE utf8mb4_unicode_ci NOT NULL", text);   // la de idioma, sí
        Assert.Single(filter.MappedCollations);
    }

    [Fact]
    public void Filtro_EnMySql8_QuitaNoAutoCreateUserDelSqlMode_YLaLineaDeSandbox()
    {
        var plan = SqlCompat.PlanFor(DbEngine.MySql, ComponentVersion.TryParse("8.4.11", out var v) ? v : null, null);
        const string maria = """
            /*M!999999\- enable the sandbox mode */
            -- MariaDB dump 10.19-11.4.13-MariaDB, for Win64 (AMD64)
            /*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
            /*!50003 SET sql_mode              = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION' */ ;
            INSERT INTO `t` VALUES ('sql_mode = ''NO_AUTO_CREATE_USER''');

            """;

        var (text, filter) = Filter(maria, plan, trickle: true);

        Assert.StartsWith(@"-- roost: /*M!999999\- enable the sandbox mode */", text);
        Assert.Contains("SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;", text);
        Assert.Contains("SET sql_mode              = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION' */ ;", text);
        Assert.Contains("INSERT INTO `t` VALUES ('sql_mode = ''NO_AUTO_CREATE_USER''');", text);
        Assert.Equal(1, filter.DroppedSqlModes["NO_AUTO_CREATE_USER"]);
        Assert.True(filter.DroppedSandboxLine);
        Assert.Contains("Quitado NO_AUTO_CREATE_USER del sql_mode (1 vez).", filter.Notes("t"));
    }

    [Fact]
    public void Filtro_UnaLineaDeDatosEnorme_PasaIdentica_InclusoConBytesQueNoSonUtf8()
    {
        var head = Encoding.ASCII.GetBytes("INSERT INTO `adjuntos` VALUES (1,'");
        var blob = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i % 251 == 10 ? 11 : i % 251)).ToArray();   // sin \n dentro
        var sql = head.Concat(blob).Concat("');\nDROP TABLE IF EXISTS `x`;\n"u8.ToArray()).ToArray();
        var plan = SqlCompat.PlanFor(DbEngine.MySql, null, MariaDb104Collations);

        var (bytes, _) = FilterBytes(sql, plan);

        Assert.Equal(sql, bytes);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1);", true)]
    [InlineData("  insert into t values (1);", true)]
    [InlineData("REPLACE INTO t VALUES (1);", true)]
    [InlineData("\t(2, 'fila de phpMyAdmin'),", true)]
    [InlineData("INSERTAR", false)]
    [InlineData("CREATE TABLE t (", false)]
    [InlineData("  `id` int NOT NULL,", false)]
    [InlineData(") ENGINE=InnoDB;", false)]
    public void IsDataLine_SoloLasDeDatos(string line, bool data)
    {
        Assert.Equal(data, SqlImportFilter.IsDataLine(Encoding.ASCII.GetBytes(line)));
    }

    [Theory]
    [InlineData("utf8mb4_0900_ai_ci", "utf8mb4_unicode_ci")]
    [InlineData("utf8mb4_0900_as_ci", "utf8mb4_unicode_ci")]
    [InlineData("utf8mb4_0900_as_cs", "utf8mb4_bin")]
    [InlineData("utf8mb4_0900_bin", "utf8mb4_bin")]
    [InlineData("utf8mb4_es_0900_ai_ci", "utf8mb4_unicode_ci")]
    [InlineData("utf8mb4_uca1400_ai_ci", "utf8mb4_unicode_ci")]
    [InlineData("utf8mb3_uca1400_as_cs", "utf8mb3_bin")]
    [InlineData("latin1_swedish_ci", "latin1_swedish_ci")]
    public void MapCollation_ElMismoJuegoDeCaracteres(string from, string to)
    {
        Assert.Equal(to, SqlCompat.MapCollation(from));
    }

    [Fact]
    public void PlanFor_SoloMySqlPierdeSqlModesYLaLineaDeSandbox()
    {
        var mysql8 = SqlCompat.PlanFor(DbEngine.MySql, ComponentVersion.TryParse("8.0.46", out var a) ? a : null, null);
        var mysql57 = SqlCompat.PlanFor(DbEngine.MySql, ComponentVersion.TryParse("5.7.44", out var b) ? b : null, null);
        var maria = SqlCompat.PlanFor(DbEngine.MariaDb, null, null);

        Assert.Contains("NO_AUTO_CREATE_USER", mysql8.DropSqlModes);
        Assert.Contains("EMPTY_STRING_IS_NULL", mysql8.DropSqlModes);
        Assert.DoesNotContain("NO_AUTO_CREATE_USER", mysql57.DropSqlModes);   // la 5.7 aún lo tiene
        Assert.True(mysql8.DropSandboxLine);
        Assert.True(maria.IsEmpty);
    }

    // --- La cabecera ---

    [Fact]
    public void Sniff_UnVolcadoDeMySql8_DiceDeDondeVieneYQueColacionesUsa()
    {
        var info = DbDump.Sniff(Encoding.UTF8.GetBytes(MySql8));

        Assert.Equal(DbEngine.MySql, info.SourceEngine);
        Assert.Equal("8.4.11", info.SourceVersion);
        Assert.Equal("blog_laragon", info.SourceDatabase);
        Assert.Equal("un volcado de MySQL 8.4.11", info.Origin);
        Assert.Equal(["utf8mb4_0900_ai_ci", "utf8mb4_0900_as_cs", "utf8mb4_es_0900_ai_ci"], info.Collations.Order(StringComparer.Ordinal));
        Assert.True(info.LooksLikeSql);

        var issues = SqlCompat.Issues(info, SqlCompat.PlanFor(DbEngine.MariaDb, null, MariaDb104Collations));
        Assert.Equal("Colaciones que este servidor no conoce: utf8mb4_0900_ai_ci, utf8mb4_0900_as_cs, utf8mb4_es_0900_ai_ci", Assert.Single(issues).What);
    }

    [Fact]
    public void Sniff_DeMariaDb_DePhpMyAdminYDeHeidi()
    {
        var maria = DbDump.Sniff(Encoding.UTF8.GetBytes(Dump));
        var pma = DbDump.Sniff(Encoding.UTF8.GetBytes("""
            -- phpMyAdmin SQL Dump
            -- version 5.2.1
            -- Servidor: 127.0.0.1
            -- Versión del servidor: 10.4.32-MariaDB
            -- Base de datos: `crm`
            SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";

            """));
        var heidi = DbDump.Sniff(Encoding.UTF8.GetBytes("""
            -- Server version:             8.0.30 - MySQL Community Server - GPL
            CREATE DATABASE IF NOT EXISTS `tienda` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */;
            USE `tienda`;

            """));

        Assert.Equal((DbEngine.MariaDb, "11.4.13", "tienda"), (maria.SourceEngine!.Value, maria.SourceVersion, maria.SourceDatabase));
        Assert.Equal((DbEngine.MariaDb, "10.4.32", "crm"), (pma.SourceEngine!.Value, pma.SourceVersion, pma.SourceDatabase));
        Assert.Equal((DbEngine.MySql, "8.0.30"), (heidi.SourceEngine!.Value, heidi.SourceVersion));
        Assert.Equal(["tienda"], heidi.Databases);
        Assert.Contains("utf8mb4_0900_ai_ci", heidi.Collations);
    }

    [Fact]
    public void Sniff_UnBinario_NoPareceSql()
    {
        var info = DbDump.Sniff(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 });

        Assert.False(info.LooksLikeSql);
        Assert.Null(info.Origin);
    }

    [Fact]
    public async Task SniffAsync_LeeElPrincipioDeUnGz()
    {
        var file = WriteBytes("blog.sql.gz", Gzip(MySql8));

        var info = await DbDump.SniffAsync(file);

        Assert.Equal("8.4.11", info.SourceVersion);
    }

    // --- Los errores ---

    [Fact]
    public void Errores_SeLeeElCodigoLaLineaYElMensaje()
    {
        var error = DbDumpErrors.Parse("ERROR 1231 (42000) at line 146: Variable 'sql_mode' can't be set to the value of 'NO_AUTO_CREATE_USER'");

        Assert.Equal(new DbDumpErrors.ClientError(1231, 146, "Variable 'sql_mode' can't be set to the value of 'NO_AUTO_CREATE_USER'"), error);
        Assert.True(DbDumpErrors.IsCompatProblem("ERROR 1231 (42000) at line 146: x"));
        Assert.False(DbDumpErrors.IsCompatProblem("ERROR 1064 (42000) at line 2: x"));
        Assert.Null(DbDumpErrors.Parse("todo bien"));
    }

    [Fact]
    public void Errores_ElPaqueteGrande_RemiteAUsuarioCnf()
    {
        var text = DbDumpErrors.ExplainImport("tienda", new CommandResult(1, "", "ERROR 2006 (HY000) at line 900: MySQL server has gone away", false), MariaDb);

        Assert.Contains("max_allowed_packet = 256M", text);
        Assert.Contains("usuario.cnf", text);
    }

    [Fact]
    public void SuggestedFileName_ConLaFecha()
    {
        Assert.Equal("tienda-20261024-1830.sql", DbDump.SuggestedFileName("tienda", new DateTime(2026, 10, 24, 18, 30, 5)));
        Assert.Equal("tienda-20261024-1830.sql.gz", DbDump.SuggestedFileName("tienda", new DateTime(2026, 10, 24, 18, 30, 5), gzip: true));
    }
}
