using System.Runtime.InteropServices;

namespace Roost.Daemon;

/// <summary>
/// Las conexiones TCP establecidas a un puerto de 127.0.0.1 (las peticiones que tiene un php-cgi, en
/// curso o esperando en su cola), cada una por su puerto de origen. Lo usa el reinicio uno a uno de
/// un pool: antes de parar un php-cgi se espera a que acaben las que ya tenía, entre ellas las que le
/// pasó el servidor web al reiniciar el anterior, que no se volverían a intentar. Solo en Windows
/// (GetExtendedTcpTable, IPv4: php-cgi escucha en 127.0.0.1); en el resto, o si falla, vacío. Nunca
/// lanza.
/// </summary>
internal static class TcpConnections
{
    public static IReadOnlyCollection<int> To(int port)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
            if (size == 0) return [];

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0) return [];

                // MIB_TCPTABLE_OWNER_PID: un DWORD con el número de filas y las filas de 6 DWORD
                // (estado, ip local, puerto local, ip remota, puerto remoto, pid); los puertos, en orden de red.
                var rows = Marshal.ReadInt32(buffer);
                var remotes = new HashSet<int>();
                for (var i = 0; i < rows; i++)
                {
                    var row = buffer + 4 + (i * 24);
                    if (Marshal.ReadInt32(row) != Established) continue;
                    if (Port(Marshal.ReadInt32(row + 8)) != port) continue;
                    remotes.Add(Port(Marshal.ReadInt32(row + 16)));
                }
                return remotes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return [];
        }
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const int Established = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
