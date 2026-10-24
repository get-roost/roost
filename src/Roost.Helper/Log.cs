namespace Roost.Helper;

/// <summary>
/// El registro del helper, en una ruta FIJA (el helper no acepta rutas para escribir):
/// <c>%ProgramData%\Roost\roost-helper.log</c> en Windows y <c>/var/log/roost/roost-helper.log</c> en
/// Linux (legible por todos, para que la app lo ensene en Registros). Sin registro no se deja de
/// hacer el trabajo.
/// </summary>
internal static class Log
{
    public static string File => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roost", "roost-helper.log")
        : "/var/log/roost/roost-helper.log";

    public static void Write(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(File)!;
            Directory.CreateDirectory(dir);
            var existed = System.IO.File.Exists(File);
            System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Who()}: {message}{Environment.NewLine}");
            if (!existed && !OperatingSystem.IsWindows())
            {
                System.IO.File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                System.IO.File.SetUnixFileMode(File, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
        catch
        {
            // sin registro no se deja de hacer el trabajo
        }
    }

    /// <summary>Quien pide la operacion: el usuario de Windows, o en Linux el que hay detras de pkexec o sudo (no root).</summary>
    private static string Who() => OperatingSystem.IsWindows() ? Environment.UserName : Posix.InvokerName;
}
