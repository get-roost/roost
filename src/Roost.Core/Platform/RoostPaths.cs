namespace Roost.Core.Platform;

/// <summary>
/// Dónde vive lo que gestiona Roost (ROOST.md §8): la raíz (binarios, configuración generada,
/// logs, pidfiles), la caché de descargas, el <c>roost.toml</c> del usuario y la carpeta de
/// proyectos, que es siempre <c>www</c> dentro de la raíz (<c>C:\roost\www</c>). La raíz se puede
/// cambiar con <c>[paths] root</c> en <c>roost.toml</c> (J2, ver <see cref="Default()"/>). La variable
/// de entorno <c>ROOST_HOME</c> lo mete todo bajo una sola carpeta (caché en <c>ROOST_HOME/cache</c>),
/// para probar sin tocar <c>C:\roost</c>.
/// </summary>
public sealed record RoostPaths(string Root, string Cache, string? ConfigDir = null, string? WwwDir = null)
{
    public const string HomeVariable = "ROOST_HOME";

    /// <summary>
    /// Lo que se añade a los nombres que son por usuario (la tubería y el mutex de roostd, los de
    /// la app): vacío con la raíz de siempre y <c>-&lt;8 hex&gt;</c> de la raíz con <c>ROOST_HOME</c>.
    /// Así una Roost de pruebas no habla con el daemon de la de verdad ni le quita la ventana.
    /// </summary>
    public static string InstanceTag(Func<string, string?>? env = null)
    {
        var home = (env ?? Environment.GetEnvironmentVariable)(HomeVariable);
        return string.IsNullOrWhiteSpace(home) ? "" : HashTag(home);
    }

    /// <summary>
    /// La misma etiqueta, para unas rutas concretas: vacía con las de siempre (<see cref="Default()"/> sin
    /// <c>ROOST_HOME</c>) y el hash de su raíz con cualquier otra. Así un cliente sobre una raíz de
    /// pruebas (la de un test, aunque no ponga <c>ROOST_HOME</c>) nunca habla con el daemon de verdad.
    /// </summary>
    public static string InstanceTag(RoostPaths paths)
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        if (string.IsNullOrWhiteSpace(home) && Normalize(paths.Root) == Normalize(Default().Root)) return "";
        return HashTag(paths.Root);
    }

    private static string HashTag(string root)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(root)));
        return "-" + Convert.ToHexStringLower(hash)[..8];
    }

    private static string Normalize(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>Una subcarpeta por cada PHP instalado: <c>bin/php/8.3.36-nts-x64</c>.</summary>
    public string PhpBin => Path.Combine(Root, "bin", "php");

    /// <summary>Una subcarpeta por cada nginx instalado: <c>bin/nginx/1.30.6</c>.</summary>
    public string NginxBin => Path.Combine(Root, "bin", "nginx");

    /// <summary>Una subcarpeta por cada Apache instalado: <c>bin/apache/2.4.69</c>.</summary>
    public string ApacheBin => Path.Combine(Root, "bin", "apache");

    /// <summary>Binarios de un motor de base de datos: <c>bin/mariadb/11.4.13</c>, <c>bin/mysql/8.4.11</c>.</summary>
    public string DbBin(string engineKey) => Path.Combine(Root, "bin", engineKey);

    /// <summary>El MinGit de Roost, una subcarpeta por versión: <c>bin/git/2.55.0.3</c>.</summary>
    public string GitBin => Path.Combine(Root, "bin", "git");

    /// <summary>El Composer de Roost, una subcarpeta por versión: <c>bin/composer/2.10.3/composer.phar</c>.</summary>
    public string ComposerBin => Path.Combine(Root, "bin", "composer");

    /// <summary>El Node de Roost, una subcarpeta por versión: <c>bin/node/24.22.1</c> (el zip de nodejs.org tal cual).</summary>
    public string NodeBin => Path.Combine(Root, "bin", "node");

    /// <summary>
    /// Lo que git necesita aparte de los repositorios: el <c>gitconfig</c> aislado de Roost, el
    /// historial de cada sitio (<c>state/git/&lt;sitio&gt;/history.json</c>) y, más adelante, las
    /// claves y el <c>known_hosts</c>.
    /// </summary>
    public string GitState => Path.Combine(State, "git");

    /// <summary>
    /// Datos de las bases de datos: <c>data/&lt;motor&gt;/&lt;rama&gt;</c>, uno por versión mayor y
    /// nunca compartido entre motores (ROOST.md §8). Roost nunca borra nada de aquí.
    /// </summary>
    public string Data => Path.Combine(Root, "data");

    /// <summary>Configuración que genera Roost (nginx.conf, un php.ini por instalación) y los fragmentos ini comunes.</summary>
    public string Etc => Path.Combine(Root, "etc");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Estado de Roost en JSON (los sitios registrados), decisión 12 de ROOST.md.</summary>
    public string State => Path.Combine(Root, "state");

    /// <summary>La CA local (<c>certs/ca</c>) y un certificado por sitio (<c>certs/sites</c>), ROOST.md §5.8.</summary>
    public string Certs => Path.Combine(Root, "certs");

    /// <summary>Un <c>&lt;sitio&gt;.conf</c> de nginx por sitio, editable fuera de las marcas de Roost.</summary>
    public string NginxSites => Path.Combine(Etc, "nginx", "sites");

    /// <summary>Un <c>&lt;sitio&gt;.conf</c> de Apache por sitio, editable fuera de las marcas de Roost.</summary>
    public string ApacheSites => Path.Combine(Etc, "apache", "sites");

    /// <summary>Pidfiles y temporales de los servicios.</summary>
    public string Run => Path.Combine(Root, "run");

    /// <summary>Zips ya verificados, que se reutilizan si se repite una instalación.</summary>
    public string Downloads => Path.Combine(Cache, "downloads");

    /// <summary>Configuración del usuario (ROOST.md §8). Puede no existir: valen los valores por defecto.</summary>
    public string ConfigFile => Path.Combine(ConfigDir ?? Root, "roost.toml");

    /// <summary>
    /// Carpeta que sirve <c>http://localhost/</c> (los proyectos van dentro): <c>&lt;raíz&gt;/www</c> salvo que
    /// <c>roost.toml</c> diga otra en <c>[paths] www</c> o que, en Linux y sin <c>ROOST_HOME</c>, exista
    /// <see cref="LinuxSharedWww"/> y sea escribible por el usuario (la crea <c>roost www init</c> con el helper).
    /// </summary>
    public string Www => WwwDir ?? Path.Combine(Root, "www");

    /// <summary>La www compartida de Linux, como en un servidor: <c>/var/www/roost</c> (decisión del plan de Linux).</summary>
    public const string LinuxSharedWww = "/var/www/roost";

    /// <summary>true si la www es la de respaldo bajo la raíz: en Linux, cuando <c>/var/www/roost</c> no existe o no es del usuario.</summary>
    public bool WwwIsFallback => WwwDir is null;

    /// <summary>
    /// Las rutas de una raíz concreta (la del shim, que vive en <c>&lt;raíz&gt;/bin/shims</c>): las
    /// de siempre si es la raíz por defecto, y si no, todo debajo de ella, como con <c>ROOST_HOME</c>.
    /// </summary>
    public static RoostPaths ForRoot(string root)
    {
        var fallback = Default();
        root = Path.GetFullPath(root);
        return string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(fallback.Root).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? fallback
            : new RoostPaths(root, Path.Combine(root, "cache"));
    }

    /// <summary>Las extensiones (ROOST.md §12.4): una carpeta por extensión, con su dll y sus dependencias.</summary>
    public string Extensions => Path.Combine(Root, "bin", "extensions");

    /// <summary>Donde van los shims de la terminal (<c>php</c>), que se ponen en el PATH del usuario.</summary>
    public string Shims => Path.Combine(Root, "bin", "shims");

    // Servicios (oleada 3): Redis, Mailpit y Adminer.

    /// <summary>El Redis de Roost, una subcarpeta por versión: <c>bin/redis/8.10.2</c>.</summary>
    public string RedisBin => Path.Combine(Root, "bin", "redis");

    /// <summary>Los datos de Redis (<c>dump.rdb</c> y <c>appendonlydir/</c>). Roost nunca borra nada de aquí.</summary>
    public string RedisData => Path.Combine(Data, "redis");

    /// <summary>El Mailpit de Roost, una subcarpeta por versión: <c>bin/mailpit/1.32.0</c>.</summary>
    public string MailpitBin => Path.Combine(Root, "bin", "mailpit");

    /// <summary>La bandeja de Mailpit (<c>mailpit.db</c>). Roost nunca borra nada de aquí.</summary>
    public string MailpitData => Path.Combine(Data, "mailpit");

    /// <summary>El Adminer de Roost, una subcarpeta por versión: <c>bin/adminer/6.2.0</c>.</summary>
    public string AdminerBin => Path.Combine(Root, "bin", "adminer");

    // Compartir (oleada 6): el cliente del túnel y las escuchas de compartir.

    /// <summary>El cloudflared de Roost (los túneles rápidos), una subcarpeta por versión: <c>bin/cloudflared/2026.10.3</c>.</summary>
    public string CloudflaredBin => Path.Combine(Root, "bin", "cloudflared");

    /// <summary>
    /// Las escuchas de nginx de lo que se está compartiendo (<c>&lt;sitio&gt;-tunel.conf</c>,
    /// <c>&lt;sitio&gt;-lan.conf</c>): las escribe cada <c>up</c> mientras dura y se borran al dejar de compartir.
    /// </summary>
    public string NginxShares => Path.Combine(Etc, "nginx", "share");

    /// <summary>Lo mismo que <see cref="NginxShares"/>, para Apache.</summary>
    public string ApacheShares => Path.Combine(Etc, "apache", "share");

    /// <summary>La raíz de siempre en Windows, sin <c>[paths] root</c> (ROOST.md §6: corta y sin espacios, nada de Program Files).</summary>
    public const string WindowsDefaultRoot = @"C:\roost";

    /// <summary>
    /// Las rutas de esta Roost. La raíz es la de siempre (<c>C:\roost</c>; en Linux y macOS, la de
    /// XDG) salvo que <c>roost.toml</c> diga otra en <c>[paths] root</c> (J2, se cambia desde
    /// Ajustes): ese fichero vive fuera de la raíz, en <c>%APPDATA%\Roost</c>, así que la raíz se
    /// puede mover sin perderlo.
    /// <para>
    /// Con <c>ROOST_HOME</c> manda la variable: esa carpeta hace de <c>%APPDATA%\Roost</c> y de
    /// <c>%LOCALAPPDATA%\Roost</c> (el <c>roost.toml</c> y la caché van ahí) y, sin <c>[paths] root</c>
    /// en su <c>roost.toml</c>, también de raíz, como siempre. El <c>%APPDATA%</c> de verdad no se lee
    /// nunca con la variable puesta.
    /// </para>
    /// </summary>
    public static RoostPaths Default() => Default(Environment.GetEnvironmentVariable);

    /// <summary><see cref="Default()"/> con otro entorno (los tests, para no cambiar las variables del proceso).</summary>
    public static RoostPaths Default(Func<string, string?> env) =>
        Default(env, dir => Directory.Exists(dir) && Posix.CanWrite(dir));

    /// <summary>
    /// <see cref="Default()"/> con otro entorno y otra forma de saber si <c>/var/www/roost</c> es escribible
    /// (los tests). Con <c>ROOST_HOME</c> la www compartida de Linux no se mira: todo va bajo esa carpeta
    /// (salvo que su <c>roost.toml</c> diga <c>[paths] www</c>).
    /// </summary>
    internal static RoostPaths Default(Func<string, string?> env, Func<string, bool> writableDirectory)
    {
        var home = env(HomeVariable);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var root = Path.GetFullPath(home);
            var toml = Path.Combine(root, "roost.toml");
            var www = Config.RoostConfig.ReadWww(toml);
            return Config.RoostConfig.ReadRoot(toml) is { } moved && !SameFolder(moved, root)
                ? new RoostPaths(moved, Path.Combine(root, "cache"), root, www)
                : new RoostPaths(root, Path.Combine(root, "cache"), WwwDir: www);
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roost");
            var toml = Path.Combine(roaming, "roost.toml");
            return new RoostPaths(
                Config.RoostConfig.ReadRoot(toml) ?? WindowsDefaultRoot,
                Path.Combine(local, "Roost", "cache"),
                roaming,
                Config.RoostConfig.ReadWww(toml));
        }

        // Linux/macOS: XDG, con sus valores por defecto si no están definidas. La www: [paths] www, o en
        // Linux /var/www/roost si existe y es del usuario (si no, ~/.local/share/roost/www, y up sugiere crearla).
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Path.Combine(Xdg(env, "XDG_CONFIG_HOME", Path.Combine(userHome, ".config")), "roost");
        var configToml = Path.Combine(config, "roost.toml");
        var shared = OperatingSystem.IsLinux() && writableDirectory(LinuxSharedWww) ? LinuxSharedWww : null;
        return new RoostPaths(
            Config.RoostConfig.ReadRoot(configToml) ?? Path.Combine(Xdg(env, "XDG_DATA_HOME", Path.Combine(userHome, ".local", "share")), "roost"),
            Path.Combine(Xdg(env, "XDG_CACHE_HOME", Path.Combine(userHome, ".cache")), "roost"),
            config,
            Config.RoostConfig.ReadWww(configToml) ?? shared);
    }

    /// <summary>
    /// La raíz que tendría esta Roost sin <c>[paths] root</c>: <c>ROOST_HOME</c> si está, y si no
    /// <c>C:\roost</c> (o la de XDG). Volver a ella quita la clave en vez de escribirla.
    /// </summary>
    public static string HomeRoot()
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(home)) return Path.GetFullPath(home);
        if (OperatingSystem.IsWindows()) return WindowsDefaultRoot;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(userHome, ".local", "share")), "roost");
    }

    /// <summary>Dos rutas a la misma carpeta (sin la barra final; en Windows, sin mirar mayúsculas).</summary>
    public static bool SameFolder(string a, string b) => Normalize(a) == Normalize(b);

    private static string Xdg(string variable, string fallback) => Xdg(Environment.GetEnvironmentVariable, variable, fallback);

    private static string Xdg(Func<string, string?> env, string variable, string fallback)
    {
        var value = env(variable);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
