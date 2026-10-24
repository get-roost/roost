namespace Roost.Core.Platform;

/// <summary>
/// Dónde está <c>roost-helper</c>, lo único de Roost que corre con privilegios: junto al ejecutable
/// (Windows, y el tar.gz de Linux) o, en Linux, el del paquete <c>.deb</c> en
/// <c>/usr/libexec/roost/roost-helper</c>, una ruta de root a la que apunta la política de polkit
/// (<c>dev.roost.helper.policy</c>, <c>exec.path</c>). Si están los dos, manda el de al lado: es el de
/// la misma compilación que el <c>roost</c> que corre.
/// </summary>
public static class HelperLocator
{
    /// <summary>El helper del paquete de Linux: solo root escribe ahí, y es el único al que vale la política de polkit.</summary>
    public const string LinuxSystemPath = "/usr/libexec/roost/roost-helper";

    /// <summary><c>roost-helper.exe</c> en Windows y <c>roost-helper</c> en Linux.</summary>
    public static string FileName => OsNames.Exe("roost-helper");

    /// <summary>La ruta del helper para el <c>roost</c> que vive en <paramref name="appDirectory"/>. Si no hay ninguno, la de al lado (para que el error diga dónde se esperaba).</summary>
    public static string Find(string appDirectory) => Find(appDirectory, File.Exists);

    /// <summary><see cref="Find(string)"/> con otro «existe» (los tests).</summary>
    public static string Find(string appDirectory, Func<string, bool> exists)
    {
        var beside = Path.Combine(appDirectory, FileName);
        if (exists(beside)) return beside;
        if (OperatingSystem.IsLinux() && exists(LinuxSystemPath)) return LinuxSystemPath;
        return beside;
    }

    /// <summary>true si es el helper del paquete, el que tiene política de polkit (con el otro, pkexec pide permiso cada vez y con un texto genérico).</summary>
    public static bool IsSystemInstall(string helperExe) => helperExe == LinuxSystemPath;
}
