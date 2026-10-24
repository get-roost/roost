using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Helper;

// roost-helper: lo UNICO de Roost que se ejecuta con privilegios (ROOST.md §5.1, decision 5). Una
// lista cerrada de operaciones, sin nada libre que llegue al sistema: ni rutas de escritura que
// vengan de fuera (todas son fijas), ni variables de entorno (solo PKEXEC_UID / SUDO_UID, para saber
// que usuario lo invoca), ni dependencias (solo la BCL y unos ficheros de Core enlazados como fuente,
// que son los que deciden que se admite). Cualquier operacion nueva tiene que ser igual de cerrada.
//
// En Windows, elevado con el UAC (Verb = runas):
//
//   roost-helper hosts-sync <fichero>
//     <fichero> trae un nombre por linea. Solo valen nombres .test de 1 a 4 etiquetas DNS en ASCII
//     (tienda.test, api.tienda.test), de 253 caracteres como mucho (HostsBlock.IsValidHost); la
//     peticion, 64 KB y 1000 nombres como mucho. En el hosts del sistema (ruta fija) solo se
//     reescribe el bloque "# roost:begin" / "# roost:end" con lineas "127.0.0.1 <nombre>"; lo demas
//     queda intacto byte a byte (Latin-1 lee y escribe cada byte tal cual).
//
//   roost-helper dns-enable | dns-disable
//     Pone o quita la regla NRPT ".test" -> 127.53.0.1 (el DNS local de Roost, ROOST.md §5.7). Sin
//     argumentos: el script es fijo (NrptScripts) y lo ejecuta el PowerShell del sistema, por su ruta.
//
//   roost-helper firewall-allow <puerto> | firewall-remove <puerto>
//     Pone o quita la regla del cortafuegos "Roost LAN <puerto>" de roost share --lan: entrada TCP a ese
//     puerto, solo en el perfil privado y solo desde la subred local. Lo unico que llega de fuera es el
//     puerto, validado (FirewallRule.TryParsePort: 80, 443 o de 1024 a 65535, sin los de administracion
//     remota ni los de bases de datos); el script es fijo (FirewallRule).
//
// En Linux, como root (lo lanza roost con pkexec y la politica dev.roost.helper, o con sudo):
//
//   roost-helper hosts-sync
//     Los nombres llegan por STDIN, uno por linea (nada de rutas: el helper no lee ficheros del
//     usuario), con las mismas reglas que en Windows. El hosts es /etc/hosts (ruta fija): solo el bloque
//     de Roost, escrito en un temporal del mismo /etc y renombrado encima (nunca queda a medias), con \n.
//
//   roost-helper dns-enable | dns-disable
//     Pone o quita el drop-in fijo /etc/systemd/resolved.conf.d/roost.conf (DNS=127.53.0.1:5353,
//     Domains=~test; ResolvedDropIn) y reinicia systemd-resolved. Sin systemd-resolved, 71; si otro
//     fichero de resolved ya enruta "test", 69 y no se toca nada.
//
//   roost-helper setcap-nginx <ruta>
//     setcap cap_net_bind_service=+ep sobre el nginx de Roost, para que escuche en el 80 y el 443 sin ser
//     root. Solo si <ruta> es absoluta y limpia, se llama nginx, se abre sin seguir enlaces, su ruta real
//     es esa misma (ningun enlace en el camino), cuelga de la home del usuario que invoca (getpwuid de
//     PKEXEC_UID/SUDO_UID, nunca $HOME), es un fichero normal de ese usuario y empieza por la firma ELF.
//     Todo se comprueba sobre el descriptor ya abierto, y setcap se aplica a ese mismo descriptor.
//
//   roost-helper www-init
//     Crea /var/www/roost (0755) con dueno el usuario que invoca. Si ya existe y es suyo, nada; si es de
//     otro, 73. No se acepta otra ruta.
//
//   roost-helper ca-trust | ca-untrust
//     La CA del sistema, opcional (curl, wget, el PHP de la distro). ca-trust recibe el PEM por STDIN y
//     solo lo instala si es una CA local de Roost (CaCertRules: X.509 autofirmado, CA:TRUE, KeyCertSign,
//     CN "Roost Local CA (...", 10 anos como mucho), en /usr/local/share/ca-certificates/roost-<uid>.crt,
//     y lanza update-ca-certificates. ca-untrust quita ese fichero y vuelve a lanzarlo.
//
//   roost-helper firewall-allow <puerto> | firewall-remove <puerto>
//     Con ufw o firewalld, y solo si estan activos (si no, 0 y nada): entrada TCP a ese puerto desde las
//     redes privadas (10/8, 172.16/12, 192.168/16), con el comentario "Roost LAN <puerto>".
//
// Cada ejecucion deja una linea en un registro de ruta fija: %ProgramData%\Roost\roost-helper.log en
// Windows y /var/log/roost/roost-helper.log en Linux. Lo rechazado se apunta sin copiar el contenido.
//
// Codigos de salida (HelperExit): 0 bien; 64 uso; 65 peticion invalida; 69 ya hay una regla de .test
// que no es de Roost; 70 fallo la orden (PowerShell, systemctl, setcap, update-ca-certificates, ufw);
// 71 sin systemd-resolved; 73 ya existe y es de otro; 74 error de E/S (no esta elevado); 77 no es root.

var windows = OperatingSystem.IsWindows();
switch (args)
{
    case ["hosts-sync", var requestFile] when windows:
        return Hosts.Sync(Hosts.ReadRequestFile(requestFile));
    case ["hosts-sync"] when !windows:
        return AsRoot(() => Hosts.Sync(Hosts.ReadRequestStdin()));

    case ["dns-enable"]:
        return windows ? WindowsOps.Nrpt("dns-enable", NrptScripts.Enable) : AsRoot(LinuxOps.DnsEnable);
    case ["dns-disable"]:
        return windows ? WindowsOps.Nrpt("dns-disable", NrptScripts.Disable) : AsRoot(LinuxOps.DnsDisable);

    case ["firewall-allow", var port]:
        return windows ? WindowsOps.Firewall("firewall-allow", port, allow: true) : AsRoot(() => LinuxOps.Firewall("firewall-allow", port, allow: true));
    case ["firewall-remove", var port]:
        return windows ? WindowsOps.Firewall("firewall-remove", port, allow: false) : AsRoot(() => LinuxOps.Firewall("firewall-remove", port, allow: false));

    case ["setcap-nginx", var nginx] when !windows:
        return AsRoot(() => LinuxOps.SetcapNginx(nginx));
    case ["www-init"] when !windows:
        return AsRoot(LinuxOps.WwwInit);
    case ["ca-trust"] when !windows:
        return AsRoot(LinuxOps.CaTrust);
    case ["ca-untrust"] when !windows:
        return AsRoot(LinuxOps.CaUntrust);

    default:
        Console.Error.WriteLine(windows
            ? "Uso: roost-helper hosts-sync <fichero con un nombre .test por linea> | dns-enable | dns-disable | firewall-allow <puerto> | firewall-remove <puerto>"
            : "Uso: roost-helper hosts-sync (nombres .test por stdin) | dns-enable | dns-disable | setcap-nginx <ruta> | www-init | ca-trust (PEM por stdin) | ca-untrust | firewall-allow <puerto> | firewall-remove <puerto>");
        return HelperExit.Usage;
}

// En Linux nada se intenta sin ser root: asi un fallo de permisos no se confunde con una peticion mala.
static int AsRoot(Func<int> operation)
{
    if (!Posix.IsRoot)
    {
        Console.Error.WriteLine("roost-helper tiene que ejecutarse como root: roost lo lanza con pkexec o con sudo.");
        return HelperExit.NotRoot;
    }
    return operation();
}
