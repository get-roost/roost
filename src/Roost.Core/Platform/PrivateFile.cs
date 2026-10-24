using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Roost.Core.Platform;

/// <summary>
/// Ficheros y carpetas que solo puede leer el usuario actual: las claves privadas de la CA local
/// (<c>LocalCa</c>) y las claves SSH de los sitios (<c>SshKeys</c>). En Windows, una ACL protegida
/// (sin permisos heredados) con un único permiso, el del SID actual; OpenSSH rechaza una clave
/// que otros puedan leer («UNPROTECTED PRIVATE KEY FILE»). En Linux y macOS, 0600 / 0700.
/// </summary>
public static class PrivateFile
{
    /// <summary>Escribe <paramref name="content"/> en <paramref name="path"/> y lo deja solo para el usuario actual.</summary>
    public static void Write(string path, string content)
    {
        File.WriteAllText(path, content);
        Restrict(path);
    }

    /// <summary>Deja un fichero que ya existe solo para el usuario actual.</summary>
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) RestrictToCurrentUser(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Crea la carpeta si falta y la deja solo para el usuario actual, con herencia: lo que se cree
    /// dentro nace ya protegido, sin el rato en que tendría los permisos de la carpeta de arriba
    /// (en <c>C:\</c>, los usuarios autenticados pueden leer lo que cuelga de una carpeta nueva).
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows()) RestrictDirectoryToCurrentUser(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>true si el fichero tiene una ACL protegida con un solo permiso, del usuario actual (para los tests y para avisar).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsRestrictedToCurrentUser(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;
        return rules.Count > 0 && rules.All(r => r.AccessControlType == AccessControlType.Allow && me.Equals(r.IdentityReference));
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // sin permisos heredados
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictDirectoryToCurrentUser(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
