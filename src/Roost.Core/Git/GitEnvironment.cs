using Roost.Core.Platform;

namespace Roost.Core.Git;

/// <summary>
/// El entorno aislado con el que Roost lanza git: no lee la configuración del sistema ni la del
/// usuario (<c>GIT_CONFIG_NOSYSTEM</c> + un <c>GIT_CONFIG_GLOBAL</c> propio en <c>state/git/gitconfig</c>),
/// nunca pregunta por consola (<c>GIT_TERMINAL_PROMPT=0</c>, sin askpass) y habla en inglés
/// (<c>LC_ALL=C</c>), para que <c>GitFailure</c> pueda clasificar sus mensajes. El repositorio
/// que deja es un repositorio normal: el usuario sigue trabajando en él con su propio git.
/// </summary>
public sealed class GitEnvironment(RoostPaths paths)
{
    /// <summary>El gitconfig de Roost. Se regenera cuando cambia la plantilla: no es del usuario.</summary>
    public string ConfigFile => Path.Combine(paths.GitState, "gitconfig");

    /// <summary>El <c>known_hosts</c> propio de Roost para SSH (lo rellena el paquete de claves y huellas).</summary>
    public string KnownHostsFile => Path.Combine(paths.GitState, "known_hosts");

    /// <summary>
    /// Lo que lee git en vez del gitconfig del sistema y del usuario, en este sistema: sin tocar los
    /// finales de línea (es un checkout para servir, no para editar con un editor de 1995), un token
    /// por repositorio (<c>useHttpPath</c>) y pull solo fast-forward. En Windows, además rutas largas,
    /// <c>fscache</c>, sin enlaces simbólicos y TLS por Schannel (el almacén de certificados de Windows:
    /// los proxies corporativos con CA propia funcionan). En Linux y macOS nada de eso: libcurl con el
    /// bundle de CA del sistema (<c>http.sslCAInfo</c>, si se encuentra uno de los de
    /// <see cref="CaBundleCandidates"/>; si no, el que lleve compilado git) y los enlaces como enlaces.
    /// El helper de credenciales de Roost se añade por invocación, con <see cref="Build"/>, no aquí.
    /// </summary>
    public static string ConfigTemplate => Template(OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? null : SystemCaBundle());

    private const string Head = """
        # Lo escribe Roost: es la configuracion del git con el que clona y actualiza los sitios.
        # Se usa con GIT_CONFIG_NOSYSTEM y en lugar de tu ~/.gitconfig, que no se lee ni se toca.
        # Se regenera: no lo edites. Para usar otro git: [git] exe en roost.toml.

        """;

    private const string WindowsCore = """
        [core]
        	longpaths = true
        	autocrlf = false
        	symlinks = false
        	fscache = true
        [http]
        	sslBackend = schannel

        """;

    private const string UnixCore = """
        [core]
        	autocrlf = false

        """;

    private const string Tail = """
        [credential]
        	useHttpPath = true
        	helper =
        [pull]
        	ff = only
        [fetch]
        	prune = true
        [gc]
        	autoDetach = false
        [advice]
        	detachedHead = false

        """;

    /// <summary>PURO: la plantilla de un sistema. <paramref name="caBundle"/> es el bundle de CA del sistema (solo fuera de Windows), o null.</summary>
    public static string Template(bool windows, string? caBundle)
    {
        var core = windows ? WindowsCore : UnixCore + (caBundle is null ? "" : "[http]\n\tsslCAInfo = " + caBundle + "\n");
        return (Head + core + Tail).Replace("\r\n", "\n");
    }

    /// <summary>
    /// Dónde guarda cada distro el bundle de CA del sistema (lo mismo que mira libcurl): Debian y
    /// Ubuntu, Fedora y RHEL, openSUSE, Alpine y macOS (Homebrew). <see cref="SystemCaBundle"/> da el
    /// primero que exista, o null.
    /// </summary>
    public static readonly IReadOnlyList<string> CaBundleCandidates =
    [
        "/etc/ssl/certs/ca-certificates.crt",
        "/etc/pki/tls/certs/ca-bundle.crt",
        "/etc/ssl/ca-bundle.pem",
        "/etc/ssl/cert.pem",
        "/usr/local/etc/openssl/cert.pem",
    ];

    public static string? SystemCaBundle(Func<string, bool>? exists = null) =>
        CaBundleCandidates.FirstOrDefault(exists ?? File.Exists);

    /// <summary>El ssh sin acceso de Roost: el del sistema, sin preguntas (falla en vez de esperar).</summary>
    public const string DefaultSshCommand = "ssh -o BatchMode=yes";

    /// <summary>
    /// Las variables para lanzar git (un valor null quita la heredada). Escribe el gitconfig si
    /// falta o cambió. <paramref name="extraConfig"/> son pares <c>clave=valor</c> solo para esta
    /// invocación (<c>GIT_CONFIG_COUNT</c>): por ahí irá el helper de credenciales.
    /// <paramref name="sshCommand"/> es el <c>GIT_SSH_COMMAND</c> del acceso por clave; sin él, el que
    /// hubiera heredado se cambia por el <c>ssh</c> de siempre en modo no interactivo (lo elige Roost,
    /// no el entorno de quien lo lanza): nunca se queda esperando una pregunta (la huella, una
    /// contraseña) que nadie va a contestar.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Build(IReadOnlyList<(string Key, string Value)>? extraConfig = null, string? sshCommand = null)
    {
        EnsureConfig();

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = ConfigFile,
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_SSH_COMMAND"] = sshCommand ?? DefaultSshCommand,
            // OpenSSH 8.4+: nunca un askpass, ni aunque haya DISPLAY (en Linux, ssh abriría un diálogo).
            ["SSH_ASKPASS_REQUIRE"] = "never",
            // Nada de lo que el entorno de quien lanza Roost pudiera inyectar en git.
            ["GIT_ASKPASS"] = null,
            ["SSH_ASKPASS"] = null,
            ["GIT_CONFIG_PARAMETERS"] = null,
            ["GIT_CONFIG_COUNT"] = null,
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
            ["GIT_OBJECT_DIRECTORY"] = null,
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
            ["GIT_NAMESPACE"] = null,
            ["GIT_EXEC_PATH"] = null,
            ["GIT_TEMPLATE_DIR"] = null,
        };

        if (extraConfig is { Count: > 0 })
        {
            env["GIT_CONFIG_COUNT"] = extraConfig.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < extraConfig.Count; i++)
            {
                env[$"GIT_CONFIG_KEY_{i}"] = extraConfig[i].Key;
                env[$"GIT_CONFIG_VALUE_{i}"] = extraConfig[i].Value;
            }
        }

        return env;
    }

    // La plantilla de este sistema, calculada una vez (el bundle de CA no cambia mientras corre Roost).
    private static readonly string Current = ConfigTemplate;

    // Cada orden de git pasa por aquí, y la app lanza varias a la vez (pone al día el git de un sitio
    // mientras mira las novedades de otro): dentro del proceso, por turnos. Entre procesos (la CLI y la
    // app) el temporal es único y basta con que, al acabar, el gitconfig diga lo que tiene que decir.
    private static readonly Lock Gate = new();

    /// <summary>Deja el gitconfig de Roost como dice la plantilla (escritura atómica).</summary>
    public void EnsureConfig()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(paths.GitState);
            if (IsCurrent()) return;
            var temp = $"{ConfigFile}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, Current);
            try
            {
                File.Move(temp, ConfigFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (IOException) { /* lo limpia la siguiente vez */ }
                if (!IsCurrent()) throw;   // otro proceso lo estaba sustituyendo por lo mismo
            }
        }
    }

    // Leído dejando que otro lo sustituya mientras tanto (sin FileShare.Delete, ese otro fallaría).
    private bool IsCurrent()
    {
        try
        {
            using var stream = new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd() == Current;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
