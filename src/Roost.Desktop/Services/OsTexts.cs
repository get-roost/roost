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

    /// <summary>«Windows pedirá permiso» / «Se pedirá permiso de administrador» (al principio de una frase).</summary>
    public static string WillAskPermission => Windows ? "Windows pedirá permiso" : "Se pedirá permiso de administrador";

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

    /// <summary>«las del ssh-agent de Windows» / «las de tu ssh-agent» (las claves que ofrece).</summary>
    public static string SshAgentKeys => Windows ? "las del ssh-agent de Windows" : "las de tu ssh-agent (SSH_AUTH_SOCK)";

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

    // Textos de las vistas (x:Static desde el XAML)

    /// <summary>Nuevo sitio: «… al hosts: Windows pedirá permiso de administrador» (en medio de la frase).</summary>
    public static string AddToHostsSuffix => Windows ? " al hosts: Windows pedirá permiso de administrador" : " al hosts: se pedirá permiso de administrador (polkit, o sudo)";

    /// <summary>true en Windows (para esconder desde el XAML lo que solo tiene sentido allí: TS/x86 de windows.php.net, Servicios de Windows).</summary>
    public static bool IsWindows => Windows;

    /// <summary>PHP: de dónde salen los builds («windows.php.net» / «roost-builds»).</summary>
    public static string PhpCatalogSource => Windows ? "windows.php.net" : "roost-builds";

    /// <summary>PHP: el subtítulo de la pantalla.</summary>
    public static string PhpSubtitle => Windows
        ? "Builds oficiales de windows.php.net, cada versión en su carpeta. Actualizar nunca pisa la anterior."
        : "Builds de get-roost/roost-builds (PHP compilado estático, con php-fpm y Xdebug dentro), cada versión en su carpeta. Actualizar nunca pisa la anterior.";

    /// <summary>PHP: la etiqueta del pool («Pool php-cgi» / «Pool php-fpm»).</summary>
    public static string PhpPoolLabel => Windows ? "Pool php-cgi" : "Pool php-fpm";

    /// <summary>PHP: por qué la NTS.</summary>
    public static string PhpNtsHint => Windows
        ? "NTS: la que usa Roost (php-cgi, con nginx o Apache). Recomendada."
        : "NTS: la que usa Roost (php-fpm, con nginx o Apache). En Linux solo hay NTS x64.";

    /// <summary>Cambiar la raíz: el ejemplo de ruta («D:\roost» / «/srv/roost»).</summary>
    public static string RootExample => Windows ? @"D:\roost" : "/srv/roost";

    /// <summary>Ajustes: la clave de la CA, con los separadores del sistema.</summary>
    public static string CaKeyRelative => global::System.IO.Path.Combine("certs", "ca", "roost-ca.key");

    /// <summary>Ajustes › Nombres .test: qué pide el fichero hosts.</summary>
    public static string HostsFileHint => Windows
        ? "Pide permiso de administrador (UAC) al añadir o quitar un sitio. Lo hace roost-helper, que solo toca su bloque."
        : "Pide permiso de administrador (polkit, o sudo) al añadir o quitar un sitio. Lo hace roost-helper, que solo toca su bloque de /etc/hosts.";

    /// <summary>La bienvenida: los permisos que va a pedir.</summary>
    public static string WelcomePermissionsNote => Windows
        ? "Windows pedirá dos permisos: uno para el fichero hosts (los nombres .test) y otro para confiar en la CA local (el candado de https)."
        : "Se pedirá permiso de administrador (polkit, o sudo) para el fichero hosts (los nombres .test); la CA local entra en los almacenes NSS de tu usuario (Chrome y Firefox) sin pedir nada.";

    /// <summary>La bienvenida: el título del aviso de servicios.</summary>
    public static string WelcomeServicesTitle => Windows ? "Servicios de Windows que le quitarían un puerto a Roost" : "Servicios de systemd que le quitarían un puerto a Roost";

    /// <summary>Sitios: el botón «Añadir al hosts» de todos los que faltan.</summary>
    public static string AddAllToHostsTip => $"Todos de una vez: {AsksPermission} una sola vez";

    /// <summary>El panel del sitio: falta en el hosts.</summary>
    public static string MissingFromHostsText => $"Falta en el hosts: no resolverá hasta que se añada ({AsksPermission}).";

    /// <summary>Compartir: dejar de escuchar en la red local.</summary>
    public static string StopLanTip => $"Deja de escuchar en la red local y quita la regla del cortafuegos ({AsksPermission})";

    /// <summary>Acceso «Mi clave SSH».</summary>
    public static string OwnSshAccessText => $"Tus claves SSH (las de ~/.ssh y {SshAgent}), como en tu terminal. Roost no guarda nada.";

    /// <summary>Ajustes › Nombres .test: qué pone el DNS local.</summary>
    public static string DnsRuleHint => Windows
        ? "Un solo permiso, al ponerlo: una regla de Windows manda los .test al DNS de Roost, que los responde mientras el stack está en marcha."
        : "Un solo permiso, al ponerlo: un drop-in de systemd-resolved manda los .test al DNS de Roost (127.53.0.1:5353), que los responde mientras el stack está en marcha.";

    /// <summary>El diálogo de la CA: lo que pasa al pulsar «Confiar» (termina en «el nombre es », que sigue con «Roost Local CA»).</summary>
    public static string TrustCaNotice => Windows
        ? "Windows te enseñará a continuación un «Aviso de seguridad» preguntando si instalas este certificado. Comprueba que el nombre es "
        : "La CA entra con certutil en los almacenes NSS de tu usuario (Chrome, Chromium y cada perfil de Firefox), sin pedir permiso; si Firefox está abierto, ciérralo antes. El nombre es ";

    /// <summary>Lo que viene detrás del nombre en ese aviso.</summary>
    public static string TrustCaNoticeEnd => Windows ? " y pulsa " : ", y en el navegador se verá como una CA de confianza. Si cambias de idea: Ajustes › ";

    /// <summary>La palabra resaltada al final («Sí» / «Dejar de confiar»).</summary>
    public static string TrustCaNoticeButton => Windows ? "Sí" : "Dejar de confiar";

    /// <summary>Auditoría: la tarjeta de los servicios del sistema.</summary>
    public static string ServicesTitle => Windows ? "Servicios de Windows" : "Servicios de systemd";

    public static string ServicesIntro => Windows
        ? "Un MySQL o una MariaDB instalados con su instalador, Apache, IIS u otro Redis arrancan con Windows y se quedan el puerto antes que Roost. Roost no cambia servicios de Windows: te dice cuáles son y cómo dejarlos en Manual."
        : "Un MariaDB, MySQL, Apache, nginx o Redis instalados con apt arrancan con el sistema y se quedan el puerto antes que Roost. Roost no cambia unidades de systemd: te dice cuáles son y cómo desactivarlas.";

    /// <summary>La etiqueta de la orden de cada servicio («En Manual:» / «Desactivar:»).</summary>
    public static string ServiceCommandLabel => Windows ? "En Manual:" : "Desactivar:";

    public static string ServiceCommandTip => Windows
        ? "Para una consola de administrador; o hazlo en Servicios de Windows (Tipo de inicio: Manual)"
        : "Para una terminal: systemctl pedirá tu contraseña con sudo";

    /// <summary>true si hay un panel de servicios que abrir (services.msc); en Linux no lo hay.</summary>
    public static bool HasServicesPanel => Windows;
}
