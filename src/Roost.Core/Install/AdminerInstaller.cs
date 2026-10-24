using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>El <c>adminer.php</c> de una release, con el SHA-256 que tiene que dar.</summary>
public sealed record AdminerBuild(string Version, Uri Url, string Sha256)
{
    /// <summary>En la caché, tal como se publica: <c>adminer-6.2.0-mysql.php</c>.</summary>
    public string FileName => Path.GetFileName(Url.AbsolutePath);

    public string Label => $"Adminer {Version}";
}

/// <summary>
/// Adminer fijado a mano: un solo fichero PHP de las releases de <c>vrana/adminer</c> en GitHub, la
/// variante solo MySQL/MariaDB (<c>-mysql</c>, con todos los idiomas): Roost no tiene otras bases de
/// datos, y así no lleva drivers que no se usan.
/// </summary>
/// <remarks>
/// Para cambiar de versión: en <c>https://api.github.com/repos/vrana/adminer/releases/latest</c>, el
/// asset <c>adminer-&lt;versión&gt;-mysql.php</c> y su <c>digest</c>; bajarlo a una carpeta vacía de
/// GitHub y también de <c>https://www.adminer.org/static/download/&lt;versión&gt;/adminer-&lt;versión&gt;-mysql.php</c>,
/// y comprobar que los tres SHA-256 coinciden. Adminer saca arreglos de seguridad: hay que subirlo con
/// Roost. Si cambia cómo se personaliza (<c>adminer_object</c>, <c>Adminer\Adminer::login</c>), hay que
/// revisar <see cref="AdminerEntry"/>.
/// </remarks>
public static class AdminerCatalog
{
    // adminer-6.2.0-mysql.php (439.622 bytes), release v6.2.0 del 23-oct-2026, verificado el 24-oct-2026:
    // el digest de la API de GitHub, el fichero de la release y el de adminer.org dan el mismo SHA-256.
    public static readonly AdminerBuild Current = new(
        "6.2.0",
        new Uri("https://github.com/vrana/adminer/releases/download/v6.2.0/adminer-6.2.0-mysql.php"),
        "7492bf2b158fee5b7ddc9e13255322c4601b1f51b797896e303882626bf1e5af");
}

/// <summary>Un Adminer instalado por Roost: <c>bin/adminer/&lt;versión&gt;/adminer.php</c> y su entrada <c>index.php</c>.</summary>
public sealed record InstalledAdminer(string Version, string Directory)
{
    public const string ScriptName = "adminer.php";

    /// <summary>El fichero de la release, tal cual.</summary>
    public string Script => Path.Combine(Directory, ScriptName);

    /// <summary>La entrada que genera Roost (<see cref="AdminerEntry"/>): es lo único que ejecuta el servidor web.</summary>
    public string Entry => Path.Combine(Directory, AdminerEntry.FileName);

    public string Label => $"Adminer {Version}";
}

/// <summary>
/// La entrada de Adminer que genera Roost (<c>index.php</c>, al lado del <c>adminer.php</c> de la
/// release, que no se toca). El servidor web manda ahí todo lo de <c>/adminer/</c>.
/// </summary>
public static class AdminerEntry
{
    public const string FileName = "index.php";

    /// <summary>
    /// PURO: el texto de la entrada. Tres cambios sobre el Adminer de serie:
    /// solo atiende a <c>localhost</c> (con otro Host, como el de un DNS rebinding desde una web,
    /// responde 403); deja entrar sin contraseña, porque el root de Roost no la tiene hasta que se le
    /// pone y Adminer lo impide de serie (quien decide si vale es la base de datos); y no consulta
    /// adminer.org por versiones nuevas, porque la versión la fija Roost.
    /// </summary>
    public const string Text = """
        <?php
        // Generado por Roost: la entrada de Adminer en http://localhost/adminer. NO lo edites: se
        // regenera en cada `roost up`. Carga adminer.php (el de la release, sin tocar) con tres cambios:
        // - solo atiende a localhost: con otro Host (un DNS rebinding desde una web) responde 403;
        // - deja entrar sin contrasena: el root de Roost no tiene hasta que se le pone, y Adminer lo
        //   impide de serie; si la contrasena vale lo decide la base de datos;
        // - no pregunta a adminer.org por versiones nuevas: la version la fija Roost.
        $roostHost = strtolower(preg_replace('~:\d+$~', '', isset($_SERVER['HTTP_HOST']) ? $_SERVER['HTTP_HOST'] : ''));
        if (!in_array($roostHost, array('localhost', '127.0.0.1', '[::1]'), true)) {
            header('HTTP/1.1 403 Forbidden');
            header('Content-Type: text/plain; charset=utf-8');
            exit("Adminer de Roost: solo desde http://localhost/adminer\n");
        }

        function adminer_object()
        {
            class RoostAdminer extends \Adminer\Adminer
            {
                public function login($login, $password)
                {
                    return true;
                }

                public function verifyVersion()
                {
                    return false;
                }
            }

            return new RoostAdminer();
        }

        require __DIR__ . '/adminer.php';

        """;

    /// <summary>
    /// PURO: la URL que abre la app, con el servidor y el usuario ya puestos en el formulario
    /// (<c>http://localhost/adminer/?server=127.0.0.1:3306&amp;username=root</c>).
    /// </summary>
    public static string Url(int httpPort, int dbPort, string user = "root") =>
        $"http://localhost{(httpPort == 80 ? "" : $":{httpPort}")}/adminer/?server=127.0.0.1:{dbPort}&username={Uri.EscapeDataString(user)}";
}

/// <summary>
/// Instala Adminer con las reglas de siempre (ver <see cref="InstallFiles"/>): el fichero se baja a
/// la caché y se verifica antes de nada, y se copia, con la entrada de Roost, a una carpeta
/// <c>.tmp-…</c> que se renombra al final. No va a la www, que es del usuario: el servidor web lo
/// sirve en <c>/adminer</c> con un alias a su carpeta.
/// </summary>
public sealed class AdminerInstaller
{
    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public AdminerInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    /// <summary>Donde va (o iría) una versión: la ruta del alias de <c>/adminer</c>.</summary>
    public string InstallDirectory(AdminerBuild build) => Path.Combine(_paths.AdminerBin, build.Version);

    public InstalledAdminer? Find(AdminerBuild build)
    {
        var dir = InstallDirectory(build);
        return File.Exists(Path.Combine(dir, InstalledAdminer.ScriptName)) ? new InstalledAdminer(build.Version, dir) : null;
    }

    /// <summary>
    /// Deja la entrada (<c>index.php</c>) como la genera esta versión de Roost; la reescribe solo si
    /// ha cambiado. true si la ha escrito.
    /// </summary>
    public static bool EnsureEntry(InstalledAdminer adminer)
    {
        if (File.Exists(adminer.Entry) && File.ReadAllText(adminer.Entry) == AdminerEntry.Text) return false;
        File.WriteAllText(adminer.Entry, AdminerEntry.Text);
        return true;
    }

    public async Task<InstalledAdminer> InstallAsync(
        AdminerBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("AdminerInstaller sin HttpClient no puede instalar.");
        var target = InstallDirectory(build);
        if (Find(build) is not null)
            throw new RoostException($"Adminer {build.Version} ya esta instalado en {target}.");

        var temp = Path.Combine(_paths.AdminerBin, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            InstallFiles.CleanLeftovers(_paths.AdminerBin);
            if (Directory.Exists(target))
                InstallFiles.RemoveAtomic(target, _paths.AdminerBin, $"la carpeta incompleta de Adminer {build.Version}");

            var script = await InstallFiles.DownloadVerifiedAsync(
                http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

            Directory.CreateDirectory(temp);
            File.Copy(script, Path.Combine(temp, InstalledAdminer.ScriptName));
            File.WriteAllText(Path.Combine(temp, AdminerEntry.FileName), AdminerEntry.Text);
            // Renombrar es atómico dentro del mismo volumen: o queda instalado entero o no queda.
            InstallFiles.MoveDirectory(temp, target);
            return new InstalledAdminer(build.Version, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo instalar Adminer: {ex.Message}", ex);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }

    /// <summary>Lo instalado, el más nuevo primero.</summary>
    public IReadOnlyList<InstalledAdminer> ListInstalled()
    {
        if (!Directory.Exists(_paths.AdminerBin)) return [];
        return Directory.EnumerateDirectories(_paths.AdminerBin)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Where(x => !x.Name.StartsWith('.') && File.Exists(Path.Combine(x.Dir, InstalledAdminer.ScriptName)))
            .Select(x => (x.Dir, x.Name, Version: ComponentVersion.TryParse(x.Name, out var v) ? v : default))
            .OrderByDescending(x => x.Version)
            .Select(x => new InstalledAdminer(x.Name, x.Dir))
            .ToList();
    }
}
