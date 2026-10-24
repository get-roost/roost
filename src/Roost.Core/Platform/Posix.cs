using System.Runtime.InteropServices;
using Roost.Core.Services;

namespace Roost.Core.Platform;

/// <summary>
/// Las pocas llamadas a libc que Roost necesita en Linux y macOS: una señal a un proceso
/// (<c>kill(2)</c>, con la que el supervisor pide a nginx y a php-fpm que paren limpios), separar al
/// daemon de la terminal que lo lanzó (<c>setsid</c>), mandar su stdio a un fichero (<c>dup2</c>), el
/// uid, si una carpeta es escribible (<c>access</c>) y la versión de glibc. Fuera de Windows solo; en
/// Windows todas devuelven «no» sin tocar nada. Ninguna lanza.
/// </summary>
public static class Posix
{
    public const int SIGHUP = 1;
    public const int SIGINT = 2;
    public const int SIGQUIT = 3;
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;

    private const int ESRCH = 3;   // no existe ese proceso
    private const int EPERM = 1;   // existe, pero no es nuestro
    private const int W_OK = 2;

    /// <summary>El número de señal con el que se pide parar según <see cref="StopSignal"/>.</summary>
    public static int SignalNumber(StopSignal signal) => signal switch
    {
        StopSignal.Term => SIGTERM,
        StopSignal.Quit => SIGQUIT,
        StopSignal.Int => SIGINT,
        _ => SIGKILL,
    };

    /// <summary>
    /// <c>kill(2)</c>: manda <paramref name="signal"/> a <paramref name="pid"/> (con un pid negativo, a todo
    /// su grupo de procesos). true si se mandó; false si el proceso no existe, no es nuestro o esto es Windows.
    /// </summary>
    public static bool Kill(int pid, int signal)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            return kill(pid, signal) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>true si existe un proceso con ese pid (señal 0: no le hace nada), aunque sea de otro usuario.</summary>
    public static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsWindows() || pid <= 0) return false;
        try
        {
            if (kill(pid, 0) == 0) return true;
            return Marshal.GetLastPInvokeError() == EPERM;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>setsid()</c>: el proceso pasa a su propia sesión, sin terminal de control, así cerrar la terminal
    /// desde la que se lanzó ya no le manda SIGHUP (ni a sus hijos). false si no se pudo (ya era líder de
    /// sesión, como cuando lo lanza systemd: entonces no hace falta) o en Windows.
    /// </summary>
    public static bool SetSid()
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            return setsid() != -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Manda stdout y stderr de este proceso al final de <paramref name="logFile"/> y stdin a <c>/dev/null</c>
    /// (<c>dup2</c>): lo que hace un daemon con lo que heredó de quien lo lanzó. Los descriptores que se
    /// abren se quedan abiertos para siempre (son los del proceso). false si no se pudo o en Windows.
    /// </summary>
    public static bool RedirectStdioTo(string logFile)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            var log = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var devNull = File.Open("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var ok = dup2((int)log.SafeFileHandle.DangerousGetHandle(), 1) != -1
                     & dup2((int)log.SafeFileHandle.DangerousGetHandle(), 2) != -1
                     & dup2((int)devNull.SafeFileHandle.DangerousGetHandle(), 0) != -1;
            // No se cierran: dup2 ya ha copiado los descriptores, pero así quedan vivos los originales también.
            GC.KeepAlive(log);
            GC.KeepAlive(devNull);
            _kept.Add(log);
            _kept.Add(devNull);
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static readonly List<IDisposable> _kept = [];

    /// <summary>El uid efectivo de este proceso; -1 en Windows.</summary>
    public static int Uid
    {
        get
        {
            if (OperatingSystem.IsWindows()) return -1;
            try { return (int)geteuid(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return -1; }
        }
    }

    /// <summary><c>access(path, W_OK)</c>: true si este usuario puede escribir en esa carpeta (o fichero). false en Windows.</summary>
    public static bool CanWrite(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            return access(path, W_OK) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>La versión de glibc de esta máquina («2.39»), o null si no es Linux con glibc (musl, Windows, macOS).</summary>
    public static Version? GlibcVersion()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var text = Marshal.PtrToStringAnsi(gnu_get_libc_version());
            return text is not null && Version.TryParse(text.Contains('.') ? text : text + ".0", out var version) ? version : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport("libc")]
    private static extern IntPtr gnu_get_libc_version();
}
