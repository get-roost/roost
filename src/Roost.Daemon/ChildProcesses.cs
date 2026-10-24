using System.Runtime.InteropServices;

namespace Roost.Daemon;

/// <summary>
/// Los procesos hijos de un proceso (los workers de nginx, el hijo de Apache), con una foto de
/// Toolhelp: es lo que dice cuándo ha acabado una recarga en caliente (<see cref="WebReload"/>),
/// porque los dos cambian de proceso hijo al leer la configuración nueva. Solo en Windows; en el
/// resto, o si falla, vacío. Nunca lanza.
/// </summary>
internal static class ChildProcesses
{
    public static IReadOnlyList<int> Of(int pid)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle) return [];
        try
        {
            var children = new List<int>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID == (uint)pid && entry.th32ProcessID != (uint)pid)
                    children.Add((int)entry.th32ProcessID);
            }
            return children;
        }
        catch
        {
            return [];
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
