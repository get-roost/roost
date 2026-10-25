namespace Roost.Desktop.Services;

/// <summary>
/// Las frases de la app que cambian según el sistema: en Windows el permiso lo pide el UAC, los tokens
/// van al Administrador de credenciales y los ficheros los abre «el programa asociado»; en Linux el
/// permiso lo pide polkit (o sudo), y lo demás tiene otro nombre. Todo con tildes, como el resto de la app.
/// </summary>
public static class OsTexts
{
    public static bool Windows => OperatingSystem.IsWindows();

    /// <summary>El nombre del sistema para las frases («Windows», «el sistema»).</summary>
    public static string System => Windows ? "Windows" : "el sistema";

    /// <summary>«Windows pedirá permiso de administrador» / «Se pedirá permiso de administrador (polkit o sudo)».</summary>
    public static string WillAskAdmin => Windows ? "Windows pedirá permiso de administrador" : "Se pedirá permiso de administrador (polkit, o sudo)";

    /// <summary>«Windows volverá a pedir permiso» / «se volverá a pedir permiso».</summary>
    public static string WillAskAgain => Windows ? "Windows volverá a pedir permiso" : "se volverá a pedir permiso";

    /// <summary>«Windows pide permiso» (entre paréntesis en las frases).</summary>
    public static string AsksPermission => Windows ? "Windows pide permiso" : "pide permiso de administrador";

    /// <summary>Quién corta un permiso: «el permiso de Windows» / «el permiso».</summary>
    public static string ThePermission => Windows ? "el permiso de Windows" : "el permiso";

    /// <summary>Dónde se guardan los tokens de git.</summary>
    public static string CredentialStore => Windows ? "el Administrador de credenciales de Windows" : "el almacén de Roost (solo tu usuario puede leerlo)";

    /// <summary>«el Administrador de credenciales» a secas.</summary>
    public static string CredentialStoreShort => Windows ? "el Administrador de credenciales" : "el almacén de Roost";

    /// <summary>El OpenSSH con el que se entra con «Mi clave».</summary>
    public static string OpenSsh => Windows ? "el OpenSSH de Windows" : "el OpenSSH del sistema";

    /// <summary>El ssh-agent del usuario.</summary>
    public static string SshAgent => Windows ? "el ssh-agent de Windows" : "tu ssh-agent (SSH_AUTH_SOCK)";

    /// <summary>«El programa asociado de Windows» / «El programa asociado (xdg-open)».</summary>
    public static string AssociatedProgram => Windows ? "El programa asociado de Windows" : "El programa asociado (xdg-open)";

    /// <summary>«El predeterminado de Windows» / «El predeterminado del sistema (xdg-open)».</summary>
    public static string DefaultBrowser => Windows ? "El predeterminado de Windows" : "El predeterminado del sistema (xdg-open)";

    /// <summary>El almacén de certificados que lee el navegador.</summary>
    public static string TrustStore => Windows ? "los certificados de confianza de tu usuario" : "los almacenes NSS de tu usuario (Chrome, Chromium y cada perfil de Firefox)";

    /// <summary>Los servicios del sistema que se quedan puertos.</summary>
    public static string SystemServices => Windows ? "los servicios de Windows" : "los servicios de systemd";

    /// <summary>«Servicios de Windows» como nombre propio del panel / «systemd».</summary>
    public static string ServicesPanel => Windows ? "Servicios de Windows" : "systemd";

    /// <summary>«Arranca con Windows» / «Arranca con el sistema».</summary>
    public static string StartsWithSystem => Windows ? "Arranca con Windows" : "Arranca con el sistema";

    /// <summary>Cómo dejar un servicio del sistema sin arrancar.</summary>
    public static string LeaveServiceManual(string name) => Windows
        ? $"Déjalo en Manual en Servicios de Windows para que no se lo quede."
        : $"Desactívalo (sudo systemctl disable --now {name}) para que no se lo quede.";

    /// <summary>La unión / el enlace simbólico de public/storage.</summary>
    public static string StorageLink => Windows ? "una unión de Windows: no pide permisos" : "un enlace simbólico";

    /// <summary>Dónde está la carpeta de proyectos por defecto.</summary>
    public static string ProjectsRoot(string www) => www;
}
