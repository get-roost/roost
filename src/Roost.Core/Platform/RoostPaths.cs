namespace Roost.Core.Platform;

/// <summary>
/// Dónde vive lo que gestiona Roost (ROOST.md §8): la raíz (binarios, configuración generada,
/// logs, pidfiles), la caché de descargas, el <c>roost.toml</c> del usuario y la carpeta de
/// proyectos, que es siempre <c>www</c> dentro de la raíz (<c>C:\roost\www</c>). La variable de
/// entorno <c>ROOST_HOME</c> lo mete todo bajo una sola carpeta (caché en <c>ROOST_HOME/cache</c>),
/// para probar sin tocar <c>C:\roost</c>.
/// </summary>
public sealed record RoostPaths(string Root, string Cache, string? ConfigDir = null, string? WwwDir = null)
{
    public const string HomeVariable = "ROOST_HOME";

    /// <summary>Una subcarpeta por cada PHP instalado: <c>bin/php/8.3.36-nts-x64</c>.</summary>
    public string PhpBin => Path.Combine(Root, "bin", "php");

    /// <summary>Una subcarpeta por cada nginx instalado: <c>bin/nginx/1.30.6</c>.</summary>
    public string NginxBin => Path.Combine(Root, "bin", "nginx");

    /// <summary>Una subcarpeta por cada Apache instalado: <c>bin/apache/2.4.69</c>.</summary>
    public string ApacheBin => Path.Combine(Root, "bin", "apache");

    /// <summary>Binarios de un motor de base de datos: <c>bin/mariadb/11.4.13</c>, <c>bin/mysql/8.4.11</c>.</summary>
    public string DbBin(string engineKey) => Path.Combine(Root, "bin", engineKey);

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

    /// <summary>Carpeta que sirve <c>http://localhost/</c> (los proyectos van dentro).</summary>
    public string Www => WwwDir ?? Path.Combine(Root, "www");

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

    public static RoostPaths Default()
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var root = Path.GetFullPath(home);
            return new RoostPaths(root, Path.Combine(root, "cache"));
        }

        if (OperatingSystem.IsWindows())
        {
            // Raíces cortas y sin espacios (ROOST.md §6): nada de Program Files.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new RoostPaths(
                @"C:\roost",
                Path.Combine(local, "Roost", "cache"),
                Path.Combine(roaming, "Roost"));
        }

        // Linux/macOS: XDG, con sus valores por defecto si no están definidas.
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new RoostPaths(
            Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(userHome, ".local", "share")), "roost"),
            Path.Combine(Xdg("XDG_CACHE_HOME", Path.Combine(userHome, ".cache")), "roost"),
            Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(userHome, ".config")), "roost"));
    }

    private static string Xdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
