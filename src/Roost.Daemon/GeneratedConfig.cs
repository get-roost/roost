using Roost.Core.Platform;

namespace Roost.Daemon;

/// <summary>
/// Una foto de la configuración que genera <c>StackPlanner.Prepare</c> y que lee el stack en
/// marcha: el nginx.conf o el httpd.conf, los <c>sites/*.conf</c> de los dos y el my.ini de cada
/// base de datos. <c>Prepare</c> los escribe en su sitio, así que, con el stack en marcha, un
/// <c>up</c> los regenera antes de saber si la configuración nueva vale. Los leería la siguiente
/// recarga del servidor web (E2: un <c>Recargar</c>, un certificado renovado), el siguiente
/// relanzamiento (un <c>restart</c>, un php-cgi reciclado, una caída) y la parada limpia de la base
/// de datos (<c>mariadb-admin --defaults-file=my.ini shutdown</c> lee el puerto del my.ini). Por eso
/// el daemon hace esta foto antes de <c>Prepare</c> y la devuelve si rechaza el plan (también si los
/// pools de PHP nuevos no arrancan, antes de recargar nada). Al aplicar, la vuelve a poner solo
/// mientras para lo que se va o cambia y no es PHP ni el servidor web (la base de datos que cambia
/// de puerto se para con el suyo), y después deja los ficheros nuevos; la recarga del servidor web
/// va después, ya con ellos.
///
/// Fuera quedan los php.ini (su contenido no depende de nada que pueda fallar en la validación:
/// el que escribe un <c>up</c> rechazado es el mismo que escribiría uno bueno; si cambia, la huella
/// de <see cref="ServiceInputs"/> lo ve y el siguiente <c>up</c> reinicia ese pool) y los
/// certificados (uno renovado siempre es mejor que el viejo).
/// </summary>
internal sealed class GeneratedConfig
{
    private readonly RoostPaths _paths;
    private readonly Dictionary<string, byte[]> _files;

    private GeneratedConfig(RoostPaths paths, Dictionary<string, byte[]> files)
    {
        _paths = paths;
        _files = files;
    }

    /// <summary>Los ficheros de la foto (para los tests).</summary>
    public IReadOnlyCollection<string> Files => _files.Keys;

    public static GeneratedConfig Snapshot(RoostPaths paths)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Enumerate(paths))
        {
            try { files[file] = File.ReadAllBytes(file); }
            catch (IOException) { /* desaparecido entre el listado y la lectura: como si no estuviera */ }
        }
        return new GeneratedConfig(paths, files);
    }

    /// <summary>
    /// Deja los ficheros como en la foto: reescribe los que estaban y borra los que han
    /// aparecido después (un vhost nuevo, un <c>.conf.removed</c>, el my.ini de otra rama).
    /// </summary>
    public void Restore()
    {
        foreach (var file in Enumerate(_paths))
        {
            if (!_files.ContainsKey(file)) File.Delete(file);
        }
        foreach (var (file, content) in _files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
        }
    }

    /// <summary>Lo que genera Prepare y lee el stack: existan o no en este momento.</summary>
    private static IEnumerable<string> Enumerate(RoostPaths paths)
    {
        // El redis.conf (Redis lo relee al relanzarse) y el fragmento del correo de PHP (un php-cgi
        // reciclado lo leería con el puerto SMTP nuevo).
        foreach (var file in new[]
                 {
                     Path.Combine(paths.Etc, "nginx", "nginx.conf"), Path.Combine(paths.Etc, "apache", "httpd.conf"),
                     Roost.Core.Stack.RedisConf.ConfPath(paths), Path.Combine(paths.Etc, "php", "conf.d", Roost.Core.Stack.MailConf.PhpIniName),
                 })
        {
            if (File.Exists(file)) yield return file;
        }
        // Con las escuchas de compartir (oleada 6), que también genera Prepare.
        foreach (var dir in new[] { paths.NginxSites, paths.ApacheSites, paths.NginxShares, paths.ApacheShares })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir)) yield return file;
        }
        foreach (var engine in new[] { "mariadb", "mysql" })
        {
            var dir = Path.Combine(paths.Etc, engine);
            if (!Directory.Exists(dir)) continue;
            foreach (var branch in Directory.EnumerateDirectories(dir))
            {
                var ini = Path.Combine(branch, "my.ini");
                if (File.Exists(ini)) yield return ini;
            }
        }
    }
}
