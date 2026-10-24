using System.Net;
using System.Net.Sockets;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Laragon;

/// <summary>Por dónde va la copia de las bases de datos de Laragon.</summary>
public enum LaragonStage
{
    /// <summary>Mirando si su servidor contesta en su puerto (y si es el suyo).</summary>
    Checking,
    /// <summary>Copiando su datadir a una carpeta de Roost (con el servidor parado).</summary>
    Copying,
    /// <summary>Arrancando su mismo servidor sobre la copia, solo en 127.0.0.1.</summary>
    Starting,
    /// <summary>Volcando una base de datos.</summary>
    Dumping,
    /// <summary>Parando la copia y borrándola.</summary>
    Cleaning,
}

/// <summary>Un paso de la copia, con lo que lleva (bytes copiados o volcados).</summary>
public sealed record LaragonProgress(LaragonStage Stage, string? Database = null, long Bytes = 0, long? Total = null, int? Port = null);

/// <summary>Cómo se han volcado: del servidor de Laragon en marcha, o de una copia de sus datos.</summary>
public enum LaragonMode
{
    Live,
    Copy,
}

/// <summary>Laragon pide contraseña para root (o la que se le ha dado no vale).</summary>
public sealed class LaragonPasswordException(string message) : RoostException(message);

/// <summary>Los volcados que ha dejado <see cref="LaragonDumper"/> en su carpeta de trabajo; al soltarlos, se borran.</summary>
public sealed class LaragonDumps(string folder, LaragonMode mode, IReadOnlyDictionary<string, string> files) : IDisposable
{
    public string Folder { get; } = folder;
    public LaragonMode Mode { get; } = mode;

    /// <summary>Base de datos → su .sql.</summary>
    public IReadOnlyDictionary<string, string> Files { get; } = files;

    public void Dispose() => LaragonDumper.DeleteFolder(Folder);
}

/// <summary>
/// Vuelca bases de datos de Laragon sin escribir nunca en sus carpetas (BD-2):
/// <list type="bullet">
/// <item>si su servidor contesta en su puerto y su <c>@@datadir</c> es el de Laragon (así no se
/// confunde con otro MySQL en el mismo puerto), con su propio <c>mysqldump</c>/<c>mariadb-dump</c>
/// y root (sin contraseña, o la que se le dé), con <c>--single-transaction</c>: solo lee;</item>
/// <item>si está parado (sus ficheros no están abiertos por nadie), copia su datadir a una carpeta
/// de Roost (<c>run\laragon-…</c>), arranca su mismo <c>mysqld</c> (el binario de Laragon, que
/// solo se lee) sobre la copia con <c>--no-defaults</c>, en un puerto libre y solo en 127.0.0.1,
/// vuelca, lo para y borra la copia.</item>
/// </list>
/// Los volcados quedan en la carpeta de trabajo hasta que se sueltan (<see cref="LaragonDumps"/>).
/// </summary>
public sealed class LaragonDumper(RoostPaths paths, ICommandRunner runner)
{
    /// <summary>¿Contesta algo en ese puerto de 127.0.0.1? (los tests lo cambian).</summary>
    public Func<int, CancellationToken, Task<bool>> AcceptsConnections { get; init; } = StackPlanner.AcceptsConnectionsAsync;

    /// <summary>¿Tiene un proceso abiertos para escribir los ficheros de ese datadir? (los tests lo cambian).</summary>
    public Func<string, bool> DataDirInUse { get; init; } = IsDataDirInUse;

    /// <summary>Un puerto libre de 127.0.0.1 para la copia.</summary>
    public Func<int> FreePort { get; init; } = PickFreePort;

    /// <summary>Lo que se espera a que la copia arranque (un MySQL 8 sobre muchos datos tarda).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Vuelca <paramref name="databases"/> de <paramref name="server"/> a ficheros .sql de la
    /// carpeta de trabajo. Lanza <see cref="LaragonPasswordException"/> si root no entra y
    /// <see cref="RoostException"/> con el motivo si no se puede; en los dos casos, sin dejar nada.
    /// </summary>
    public async Task<LaragonDumps> DumpAsync(LaragonDbServer server, IReadOnlyList<string> databases, string? password,
        IProgress<LaragonProgress>? progress, IProgress<string>? lines, CancellationToken cancellation)
    {
        if (server.DataDir is null) throw new RoostException($"No se encuentran los datos de {server.Label} de Laragon.");
        foreach (var db in databases) DbDump.CheckName(db, "volcar");
        if (password is not null && !DbAdmin.IsValidPassword(password))
            throw new LaragonPasswordException($"Esa contrasena no la puede usar Roost: {DbAdmin.PasswordRule}.");
        if (!File.Exists(server.Server)) throw new RoostException($"No esta {server.Server}: Roost necesita el servidor de Laragon para leer sus datos.");

        var work = Path.Combine(paths.Run, $"laragon-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new LaragonProgress(LaragonStage.Checking, Port: server.Port));
            var mode = await LiveAsync(server, password, work, cancellation) ? LaragonMode.Live : LaragonMode.Copy;
            var files = mode == LaragonMode.Live
                ? await DumpAllAsync(server, databases, server.Port, password, work, progress, lines, cancellation)
                : await DumpFromCopyAsync(server, databases, password, work, progress, lines, cancellation);
            return new LaragonDumps(work, mode, files);
        }
        catch
        {
            DeleteFolder(work);
            throw;
        }
    }

    /// <summary>
    /// true si su servidor está en marcha y es el de Laragon. Si en el puerto contesta otro (su
    /// datadir no es el de Laragon), false: entonces se usa la copia, si se puede.
    /// </summary>
    private async Task<bool> LiveAsync(LaragonDbServer server, string? password, string work, CancellationToken cancellation)
    {
        if (!await AcceptsConnections(server.Port, cancellation)) return false;

        var tool = Tool(server, ClientFile(work, server.Port, password));
        var result = await runner.RunAsync(new CommandSpec(tool.Client, [$"--defaults-file={tool.DefaultsFile}", "-N", "-e", "SELECT @@datadir"], Timeout: TimeSpan.FromSeconds(15)), null, cancellation);
        if (!result.Succeeded)
        {
            var error = DbDumpErrors.Parse(result.Stderr + "\n" + result.Stdout);
            if (error?.Code == 1045)
                throw new LaragonPasswordException(password is null or ""
                    ? $"El {server.Label} de Laragon pide la contrasena de root."
                    : $"La contrasena de root no vale para el {server.Label} de Laragon.");
            // Contesta algo que no es un MySQL al que podamos entrar: no es el de Laragon.
            return false;
        }
        return SamePath(result.Stdout.Trim(), server.DataDir!);
    }

    private async Task<IReadOnlyDictionary<string, string>> DumpAllAsync(LaragonDbServer server, IReadOnlyList<string> databases, int port, string? password,
        string work, IProgress<LaragonProgress>? progress, IProgress<string>? lines, CancellationToken cancellation)
    {
        var tool = Tool(server, ClientFile(work, port, password));
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in databases)
        {
            progress?.Report(new LaragonProgress(LaragonStage.Dumping, db));
            var file = Path.Combine(work, db + ".sql");
            var written = progress is null ? null : new Progress<DbTransferProgress>(p => progress.Report(new LaragonProgress(LaragonStage.Dumping, db, p.Bytes)));
            try
            {
                await DbDump.ExportAsync(tool, db, file, runner, written, lines, cancellation);
            }
            catch (RoostException ex) when (ex.Message.Contains("no deja entrar", StringComparison.Ordinal))
            {
                throw new LaragonPasswordException($"El {server.Label} de Laragon pide la contrasena de root.");
            }
            files[db] = file;
        }
        return files;
    }

    private async Task<IReadOnlyDictionary<string, string>> DumpFromCopyAsync(LaragonDbServer server, IReadOnlyList<string> databases, string? password,
        string work, IProgress<LaragonProgress>? progress, IProgress<string>? lines, CancellationToken cancellation)
    {
        if (DataDirInUse(server.DataDir!))
            throw new RoostException($"El {server.Label} de Laragon parece en marcha (tiene sus datos abiertos), pero no contesta en el puerto {server.Port}. Paralo en Laragon o dejalo en marcha en su puerto, y repite.");

        // 1. La copia, con el servidor de Laragon parado: sus ficheros no se tocan, solo se leen.
        var copy = Path.Combine(work, "data");
        var total = FolderSize(server.DataDir!);
        progress?.Report(new LaragonProgress(LaragonStage.Copying, Bytes: 0, Total: total));
        await Task.Run(() => CopyFolder(server.DataDir!, copy, total, progress, cancellation), cancellation);
        Directory.CreateDirectory(Path.Combine(work, "tmp"));

        // 2. Su mismo servidor sobre la copia, sin leer ningún fichero de opciones, solo en 127.0.0.1.
        var port = FreePort();
        progress?.Report(new LaragonProgress(LaragonStage.Starting, Port: port));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serverLines = new List<string>();
        var running = runner.RunAsync(CopyServerCommand(server, copy, work, port), new LineCollector(serverLines, lines), stop.Token);
        try
        {
            await WaitForStartAsync(server, port, running, serverLines, cancellation);
            // 3. Volcar, como si fuera el de verdad.
            return await DumpAllAsync(server, databases, port, password, work, progress, lines, cancellation);
        }
        finally
        {
            // 4. Pararla (es una copia: se mata su árbol) y borrar sus datos.
            progress?.Report(new LaragonProgress(LaragonStage.Cleaning));
            stop.Cancel();
            try { await running; } catch { /* matada a propósito */ }
            DeleteFolder(copy);
        }
    }

    /// <summary>
    /// PURO: el servidor de Laragon sobre la copia. <c>--no-defaults</c> va el primero: así no lee
    /// su my.ini (que apunta a sus datos de verdad) ni ningún otro.
    /// </summary>
    public static CommandSpec CopyServerCommand(LaragonDbServer server, string dataDir, string work, int port)
    {
        var args = new List<string>
        {
            "--no-defaults",
            $"--basedir={server.Folder}",
            $"--datadir={dataDir}",
            $"--port={port}",
            "--bind-address=127.0.0.1",
            $"--tmpdir={Path.Combine(work, "tmp")}",
            $"--pid-file={Path.Combine(work, "copia.pid")}",
            // Que un evento programado no cambie nada mientras se vuelca.
            "--event-scheduler=OFF",
            "--console",
        };
        if (server.Engine == DbEngine.MySql && server.Version is not { Major: < 8 })
            args.AddRange(["--mysqlx=OFF", "--skip-log-bin"]);
        return new CommandSpec(server.Server, args, WorkingDirectory: work);
    }

    private async Task WaitForStartAsync(LaragonDbServer server, int port, Task<CommandResult> running, List<string> serverLines, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            if (running.IsCompleted)
            {
                string last;
                lock (serverLines) last = string.Join(" ", serverLines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).TakeLast(2));
                throw new RoostException($"La copia del {server.Label} de Laragon no ha arrancado{(last.Length > 0 ? ": " + last : ".")}");
            }
            if (await AcceptsConnections(port, cancellation)) return;
            await Task.Delay(300, cancellation);
        }
        throw new RoostException($"La copia del {server.Label} de Laragon no ha arrancado en {StartTimeout.TotalSeconds:0} s.");
    }

    private static DbTool Tool(LaragonDbServer server, string defaultsFile) =>
        DbTool.InFolder(server.Engine, server.Version, server.BinDir, defaultsFile)
        ?? throw new RoostException($"En {server.BinDir} no estan el cliente y el volcador de {server.Label}.");

    /// <summary>El fichero de opciones para conectar, en la carpeta de trabajo y solo para el usuario: la contraseña nunca va en la línea de órdenes.</summary>
    private static string ClientFile(string work, int port, string? password)
    {
        var file = Path.Combine(work, $"cliente-{port}.cnf");
        var text = $"[client]\nhost = 127.0.0.1\nport = {port}\nuser = root\n{(string.IsNullOrEmpty(password) ? "" : $"password = \"{password}\"\n")}";
        PrivateFile.Write(file, text);
        return file;
    }

    /// <summary>
    /// true si algún proceso tiene abiertos para escribir los ficheros que un servidor en marcha
    /// no suelta (<c>ibdata1</c>, el <c>mysql.ibd</c> de MySQL 8, el <c>aria_log_control</c> de
    /// MariaDB): se intentan abrir sin dejar escribir a nadie más. Copiarlos así daría una copia rota.
    /// </summary>
    public static bool IsDataDirInUse(string dataDir)
    {
        foreach (var name in new[] { "ibdata1", "mysql.ibd", "aria_log_control" })
        {
            var file = Path.Combine(dataDir, name);
            if (!File.Exists(file)) continue;
            try
            {
                using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }

    private static long FolderSize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    /// <summary>Copia el datadir leyendo cada fichero sin dejar que nadie escriba mientras (si alguien lo tiene abierto, falla y no se usa una copia rota).</summary>
    private static void CopyFolder(string from, string to, long total, IProgress<LaragonProgress>? progress, CancellationToken cancellation)
    {
        long copied = 0, reported = 0;
        var buffer = new byte[1 << 20];
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).Prepend(from))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, dir));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                cancellation.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                // Los logs de errores y el pid del servidor no hacen falta, y el pid confundiría a la copia.
                if (ext.Equals(".pid", StringComparison.OrdinalIgnoreCase) || ext.Equals(".err", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    using var output = new FileStream(Path.Combine(target, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        copied += n;
                        if (copied - reported >= 8 << 20)
                        {
                            reported = copied;
                            progress?.Report(new LaragonProgress(LaragonStage.Copying, Bytes: copied, Total: total));
                        }
                    }
                }
                catch (IOException ex) when (ex is not FileNotFoundException)
                {
                    throw new RoostException($"No se pudo copiar {file} (¿Laragon esta arrancando su base de datos?): {ex.Message}");
                }
            }
        }
        progress?.Report(new LaragonProgress(LaragonStage.Copying, Bytes: copied, Total: total));
    }

    /// <summary>Borra una carpeta de trabajo (con reintentos: el servidor recién matado puede tardar en soltar sus ficheros).</summary>
    internal static void DeleteFolder(string folder)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool SamePath(string a, string b)
    {
        static string Normalize(string p) => Path.GetFullPath(p.Replace("\\\\", "\\").Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
        try { return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Guarda las líneas de la copia (para decir por qué no arrancó) y las pasa a quien quiera verlas.</summary>
    private sealed class LineCollector(List<string> sink, IProgress<string>? next) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (sink)
            {
                if (sink.Count > 200) sink.RemoveAt(0);
                sink.Add(value);
            }
            next?.Report(value);
        }
    }
}
