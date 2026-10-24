namespace Roost.Core.Platform;

/// <summary>
/// Lo que Roost lee de <c>/proc</c> en Linux, con los parseos puros aparte para probarlos con ficheros
/// grabados: las conexiones TCP (<c>/proc/net/tcp</c> y <c>tcp6</c>: quién escucha en un puerto y qué
/// conexiones hay establecidas a él), el <c>stat</c> de cada proceso (padre, grupo, hora de arranque) y
/// el proceso dueño de un socket (<c>/proc/&lt;pid&gt;/fd</c>). Sin root solo se ven los descriptores de
/// los procesos propios: de un servicio del sistema se sabe que escucha, no quién es. Los lectores nunca
/// lanzan: fuera de Linux, o si no se puede leer, devuelven vacío o null.
/// </summary>
public static class ProcFs
{
    public const string Root = "/proc";

    /// <summary>Estado de una conexión en <c>/proc/net/tcp</c> (columna <c>st</c>).</summary>
    public const int Established = 0x01;
    public const int Listen = 0x0A;

    /// <summary>Una fila de <c>/proc/net/tcp</c>: puertos en decimal, estado y el inodo del socket.</summary>
    public sealed record TcpRow(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, int State, long Inode);

    /// <summary>
    /// PURO: las filas de un <c>/proc/net/tcp</c> o <c>tcp6</c>. La primera línea es la cabecera; cada fila
    /// es <c>sl local_address rem_address st tx_queue:rx_queue tr:tm-&gt;when retrnsmt uid timeout inode …</c>,
    /// con las direcciones como <c>hex:puerto-hex</c>. Las que no se entienden se saltan.
    /// </summary>
    public static IReadOnlyList<TcpRow> ParseTcp(string text)
    {
        var rows = new List<TcpRow>();
        foreach (var raw in text.Split('\n'))
        {
            var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || !fields[0].EndsWith(':')) continue;
            var local = fields[1].Split(':');
            var remote = fields[2].Split(':');
            if (local.Length != 2 || remote.Length != 2) continue;
            if (!int.TryParse(local[1], System.Globalization.NumberStyles.HexNumber, null, out var localPort)
                || !int.TryParse(remote[1], System.Globalization.NumberStyles.HexNumber, null, out var remotePort)
                || !int.TryParse(fields[3], System.Globalization.NumberStyles.HexNumber, null, out var state)
                || !long.TryParse(fields[9], out var inode))
                continue;
            rows.Add(new TcpRow(local[0], localPort, remote[0], remotePort, state, inode));
        }
        return rows;
    }

    /// <summary>PURO: los inodos de los sockets que escuchan en <paramref name="port"/> (en cualquier dirección).</summary>
    public static IReadOnlyList<long> ListeningInodes(string tcpText, int port) =>
        ParseTcp(tcpText).Where(r => r.State == Listen && r.LocalPort == port).Select(r => r.Inode).Distinct().ToList();

    /// <summary>PURO: los puertos de origen de las conexiones establecidas al puerto local <paramref name="port"/>.</summary>
    public static IReadOnlyCollection<int> EstablishedTo(string tcpText, int port) =>
        ParseTcp(tcpText).Where(r => r.State == Established && r.LocalPort == port).Select(r => r.RemotePort).ToHashSet();

    /// <summary>Lo que Roost usa de <c>/proc/&lt;pid&gt;/stat</c>.</summary>
    /// <param name="StartTime">El campo 22: ticks desde el arranque de la máquina. Un pid reutilizado nunca repite el suyo.</param>
    public sealed record StatInfo(int Pid, string Comm, char State, int ParentPid, int ProcessGroup, int Session, long StartTime);

    /// <summary>
    /// PURO: una línea de <c>/proc/&lt;pid&gt;/stat</c>. El nombre va entre paréntesis y puede llevar espacios
    /// y paréntesis («(Web Content)»), así que se corta por el ÚLTIMO «)». Después van, por orden: estado,
    /// ppid, pgrp, session, tty, tpgid, flags, minflt, cminflt, majflt, cmajflt, utime, stime, cutime,
    /// cstime, priority, nice, num_threads, itrealvalue y starttime (el campo 22). null si no se entiende.
    /// </summary>
    public static StatInfo? ParseStat(string line)
    {
        var open = line.IndexOf('(');
        var close = line.LastIndexOf(')');
        if (open < 1 || close < open) return null;
        if (!int.TryParse(line.AsSpan(0, open).Trim(), out var pid)) return null;
        var comm = line[(open + 1)..close];
        var rest = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 20 || rest[0].Length != 1) return null;
        if (!int.TryParse(rest[1], out var ppid) || !int.TryParse(rest[2], out var pgrp) || !int.TryParse(rest[3], out var session)
            || !long.TryParse(rest[19], out var start))
            return null;
        return new StatInfo(pid, comm, rest[0][0], ppid, pgrp, session, start);
    }

    // Lectores: solo en Linux; nunca lanzan.

    /// <summary>El <c>stat</c> de un proceso, o null si no existe o no es Linux.</summary>
    public static StatInfo? Stat(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0) return null;
        try
        {
            return ParseStat(File.ReadAllText($"{Root}/{pid}/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>La hora de arranque (campo 22) de un proceso, o null si no existe: con el pid identifica un proceso sin ambigüedad.</summary>
    public static long? StartTimeOf(int pid) => Stat(pid)?.StartTime;

    /// <summary>Los pids que hay ahora (las carpetas numéricas de <c>/proc</c>); vacío fuera de Linux.</summary>
    public static IReadOnlyList<int> Pids()
    {
        if (!OperatingSystem.IsLinux()) return [];
        try
        {
            var pids = new List<int>();
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (int.TryParse(Path.GetFileName(dir), out var pid)) pids.Add(pid);
            }
            return pids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Los hijos directos de <paramref name="pid"/> (por el ppid de su <c>stat</c>); vacío fuera de Linux.</summary>
    public static IReadOnlyList<int> ChildrenOf(int pid)
    {
        var children = new List<int>();
        foreach (var candidate in Pids())
        {
            if (candidate != pid && Stat(candidate) is { } stat && stat.ParentPid == pid) children.Add(candidate);
        }
        return children;
    }

    /// <summary>El nombre corto de un proceso (<c>/proc/&lt;pid&gt;/comm</c>), o null.</summary>
    public static string? CommOf(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0) return null;
        try
        {
            return File.ReadAllText($"{Root}/{pid}/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>/proc/net/tcp</c> y <c>tcp6</c> juntos (vacío si no se pueden leer).</summary>
    public static string ReadTcpTables()
    {
        if (!OperatingSystem.IsLinux()) return "";
        var text = new System.Text.StringBuilder();
        foreach (var file in new[] { $"{Root}/net/tcp", $"{Root}/net/tcp6" })
        {
            try { text.Append(File.ReadAllText(file)).Append('\n'); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sin IPv6, o sin /proc */ }
        }
        return text.ToString();
    }

    /// <summary>Los puertos de origen de las conexiones establecidas al puerto local <paramref name="port"/>, ahora.</summary>
    public static IReadOnlyCollection<int> EstablishedTo(int port) => EstablishedTo(ReadTcpTables(), port);

    /// <summary>El inodo del socket que escucha en <paramref name="port"/>, o null si nadie escucha.</summary>
    public static long? ListeningInode(int port) => ListeningInodes(ReadTcpTables(), port) is [var first, ..] ? first : null;

    /// <summary>
    /// El pid del proceso que tiene abierto el socket <paramref name="inode"/>, mirando <c>/proc/*/fd</c>.
    /// Sin root solo se pueden leer los descriptores de los procesos propios: null también si es de otro usuario.
    /// </summary>
    public static int? PidOfSocket(long inode)
    {
        var target = $"socket:[{inode}]";
        foreach (var pid in Pids())
        {
            try
            {
                foreach (var fd in Directory.EnumerateFiles($"{Root}/{pid}/fd"))
                {
                    if (new FileInfo(fd).LinkTarget == target) return pid;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // De otro usuario, o se ha ido mientras tanto.
            }
        }
        return null;
    }
}
