using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Roost.Core.Platform;

/// <summary>Un proceso escuchando en un puerto TCP local.</summary>
/// <param name="Pid">0 si se sabe que alguien escucha pero no quién (<see cref="IsForeign"/>).</param>
public sealed record PortOwner(int Port, int Pid, string? ProcessName)
{
    /// <summary>
    /// true si alguien escucha pero no se ve quién: en Linux, sin root, solo se ven los descriptores de los
    /// procesos propios, y un servicio del sistema (el apache2 de la distro) es de otro usuario.
    /// </summary>
    public bool IsForeign => Pid <= 0;
}

/// <summary>
/// Quién escucha en un puerto TCP: para decir «el 80 lo tiene System (PID 4)» en vez de solo
/// «está ocupado». En Windows, GetExtendedTcpTable (IPv4 e IPv6); en Linux, <c>/proc/net/tcp</c> y
/// <c>tcp6</c> (el inodo del socket que escucha) y <c>/proc/*/fd</c> (quién lo tiene abierto: solo los
/// procesos propios sin root, y entonces <see cref="PortOwner.IsForeign"/>). En el resto, o si la
/// llamada falla, devuelve null. Como la detección, nunca lanza.
/// </summary>
public static class PortOwners
{
    public static PortOwner? Find(int port)
    {
        if (OperatingSystem.IsLinux()) return FindLinux(port);
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var pid = FindPid(port, AfInet) ?? FindPid(port, AfInet6);
            return pid is null ? null : new PortOwner(port, pid.Value, ProcessName(pid.Value));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PURO: «nginx (PID 1234)», o lo que se sabe de un dueño que no se ve (sin tildes).</summary>
    public static string Describe(PortOwner owner) => owner.IsForeign
        ? "otro proceso que no es de este usuario (en Linux solo se ven los propios; miralo con: sudo ss -ltnp)"
        : $"{owner.ProcessName ?? "otro proceso"} (PID {owner.Pid})";

    private static PortOwner? FindLinux(int port)
    {
        try
        {
            if (ProcFs.ListeningInode(port) is not { } inode) return null;
            var pid = ProcFs.PidOfSocket(inode);
            return pid is null ? new PortOwner(port, 0, null) : new PortOwner(port, pid.Value, ProcFs.CommOf(pid.Value));
        }
        catch
        {
            return null;
        }
    }

    private static string? ProcessName(int pid)
    {
        if (pid == 4) return "System";
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    private static int? FindPid(int port, int family)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size == 0) return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0) return null;

            // MIB_TCPTABLE_OWNER_PID / MIB_TCP6TABLE_OWNER_PID: un DWORD con el número de filas y las filas.
            var rows = Marshal.ReadInt32(buffer);
            // Fila IPv4: estado, ip local, puerto local, ip remota, puerto remoto, pid (6 DWORD).
            // Fila IPv6: ip local (16), scope, puerto local, ip remota (16), scope, puerto remoto, estado, pid.
            var (rowSize, portOffset, pidOffset) = family == AfInet ? (24, 8, 20) : (56, 20, 52);
            for (var i = 0; i < rows; i++)
            {
                var row = buffer + 4 + (i * rowSize);
                // El puerto viene en orden de red en los 2 bytes bajos del DWORD.
                var raw = Marshal.ReadInt32(row + portOffset);
                var rowPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                if (rowPort == port) return Marshal.ReadInt32(row + pidOffset);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
