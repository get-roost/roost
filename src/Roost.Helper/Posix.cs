using System.Runtime.InteropServices;
using Roost.Core.Platform;

namespace Roost.Helper;

/// <summary>
/// Lo poco de libc que necesita el helper en Linux y que .NET no da: quien somos y quien nos invoca
/// (<c>geteuid</c>, <c>getuid</c>, <c>getpwuid</c>), el dueno y el tipo de un fichero (<c>statx</c>),
/// abrir sin seguir enlaces (<c>open</c> con <c>O_NOFOLLOW</c>), la ruta canonica (<c>realpath</c>) y
/// cambiar el dueno (<c>chown</c>). Cada llamada dice por que esa y no otra. Solo glibc (Debian, Ubuntu,
/// Fedora): <c>libc.so.6</c> por su nombre, sin depender de como resuelva .NET «libc».
/// </summary>
internal static class Posix
{
    private const string Libc = "libc.so.6";

    [DllImport(Libc)] private static extern uint geteuid();
    [DllImport(Libc)] private static extern uint getuid();

    /// <summary>true si el proceso corre como root (uid efectivo 0).</summary>
    public static bool IsRoot => OperatingSystem.IsLinux() && geteuid() == 0;

    /// <summary>
    /// El uid del usuario que ha pedido la operacion: <c>PKEXEC_UID</c> (lo pone pkexec, que ademas
    /// limpia el entorno), si no <c>SUDO_UID</c> (lo pone sudo) y, si no hay ninguno, el uid real de
    /// este proceso (root ejecutando el helper a mano). Son las unicas variables de entorno que lee el
    /// helper, y solo se admiten como un numero (<c>HelperChecks.TryParseUid</c>).
    /// </summary>
    public static uint InvokerUid()
    {
        foreach (var variable in new[] { "PKEXEC_UID", "SUDO_UID" })
        {
            if (HelperChecks.TryParseUid(Environment.GetEnvironmentVariable(variable), out var uid)) return uid;
        }
        return getuid();
    }

    /// <summary>Un usuario del sistema: lo que dice <c>getpwuid</c>.</summary>
    public sealed record User(uint Uid, uint Gid, string Name, string Home);

    // struct passwd de glibc: pw_name, pw_passwd, pw_uid, pw_gid, pw_gecos, pw_dir, pw_shell (punteros y dos uint:
    // la misma disposicion en x86_64 y en aarch64).
    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint Uid;
        public uint Gid;
        public IntPtr Gecos;
        public IntPtr Dir;
        public IntPtr Shell;
    }

    [DllImport(Libc, SetLastError = true)] private static extern IntPtr getpwuid(uint uid);

    /// <summary>
    /// El usuario de un uid por <c>getpwuid</c> (pasa por NSS: tambien LDAP y compania), NUNCA por
    /// <c>$HOME</c>: la home es la que dice el sistema, no la que diga el entorno de quien invoca. null
    /// si el uid no existe.
    /// </summary>
    public static User? UserOf(uint uid)
    {
        var entry = getpwuid(uid);
        if (entry == IntPtr.Zero) return null;
        var pw = Marshal.PtrToStructure<Passwd>(entry);
        return new User(pw.Uid, pw.Gid, Marshal.PtrToStringUTF8(pw.Name) ?? "", Marshal.PtrToStringUTF8(pw.Dir) ?? "");
    }

    /// <summary>Quien invoca, para el registro: «rl3 (uid 1003)».</summary>
    public static string InvokerName
    {
        get
        {
            var uid = InvokerUid();
            return UserOf(uid) is { } user ? $"{user.Name} (uid {uid})" : $"uid {uid}";
        }
    }

    // statx (Linux >= 4.11, glibc >= 2.28): al reves que struct stat, su struct es la misma en todas las
    // arquitecturas, asi que los desplazamientos de abajo valen en x86_64 y en aarch64.
    [DllImport(Libc, SetLastError = true)] private static extern int statx(int dirfd, string pathname, int flags, uint mask, byte[] buffer);

    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const int StatxSize = 256;

    /// <summary>El dueno, el grupo y el tipo de un fichero (de <c>stx_mode</c>).</summary>
    public sealed record FileStatus(uint Uid, uint Gid, ushort Mode)
    {
        public bool IsRegular => (Mode & 0xF000) == 0x8000;
        public bool IsDirectory => (Mode & 0xF000) == 0x4000;
        public bool IsSymlink => (Mode & 0xF000) == 0xA000;
    }

    /// <summary>El estado de una ruta SIN seguir un enlace final (como <c>lstat</c>); null si no existe.</summary>
    public static FileStatus? LStat(string path)
    {
        var buffer = new byte[StatxSize];
        return statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, buffer) == 0 ? Parse(buffer) : null;
    }

    /// <summary>El estado de lo que hay abierto en <paramref name="fd"/> (como <c>fstat</c>): el fichero de verdad, no lo que ahora haya en su ruta.</summary>
    public static FileStatus? FStat(int fd)
    {
        var buffer = new byte[StatxSize];
        return statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, buffer) == 0 ? Parse(buffer) : null;
    }

    // struct statx: stx_mask u32 @0, stx_blksize u32 @4, stx_attributes u64 @8, stx_nlink u32 @16,
    // stx_uid u32 @20, stx_gid u32 @24, stx_mode u16 @28, ...
    private static FileStatus Parse(byte[] b) => new(BitConverter.ToUInt32(b, 20), BitConverter.ToUInt32(b, 24), BitConverter.ToUInt16(b, 28));

    [DllImport(Libc, SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport(Libc)] private static extern int close(int fd);

    private const int O_RDONLY = 0;
    private const int O_NOFOLLOW = 0x20000;   // 0400000: si el ultimo componente es un enlace, ELOOP
    private const int O_CLOEXEC = 0x80000;    // 02000000: los hijos no lo heredan

    /// <summary>Abre para leer sin seguir un enlace en el ultimo componente; -1 si no se pudo (un enlace, no existe, sin permiso).</summary>
    public static int OpenNoFollow(string path) => open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);

    public static void Close(int fd)
    {
        if (fd >= 0) close(fd);
    }

    /// <summary>La ruta REAL de lo que hay abierto en <paramref name="fd"/> (lo que dice <c>/proc/self/fd/N</c>): ya sin enlaces ni <c>..</c>.</summary>
    public static string? RealPathOf(int fd)
    {
        try { return new FileInfo($"/proc/self/fd/{fd}").LinkTarget; }
        catch (IOException) { return null; }
    }

    [DllImport(Libc, SetLastError = true)] private static extern IntPtr realpath(string path, IntPtr resolved);
    [DllImport(Libc)] private static extern void free(IntPtr pointer);

    /// <summary>La ruta canonica (sin enlaces, ni <c>.</c>, ni <c>..</c>), o null si no existe.</summary>
    public static string? RealPath(string path)
    {
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { free(resolved); }
    }

    [DllImport(Libc, SetLastError = true)] private static extern int chown(string path, uint owner, uint group);

    /// <summary>Cambia el dueno y el grupo; false si no se pudo (<see cref="Errno"/>).</summary>
    public static bool Chown(string path, uint uid, uint gid) => chown(path, uid, gid) == 0;

    /// <summary>El errno de la ultima llamada que fallo.</summary>
    public static int Errno => Marshal.GetLastWin32Error();
}
