using System.Globalization;
using System.Text;

namespace Roost.Core.Stack;

/// <summary>
/// El <c>php-fpm.conf</c> que genera Roost para cada instalación de PHP en Linux (<c>etc/php/&lt;instalación&gt;/php-fpm.conf</c>).
/// Un solo pool, <c>www</c>, que escucha por TCP en 127.0.0.1 en el primer puerto del pool de esa
/// rama (<see cref="PhpPool.Ports"/>): así el <c>upstream</c> de nginx, <c>ReloadPlan</c>, <c>roost status</c>
/// y la app ven un miembro <c>php-&lt;rama&gt;-0</c> con su puerto, como con php-cgi en Windows. Se arranca con
/// <c>php-fpm -F -y &lt;conf&gt; -c &lt;php.ini&gt;</c> (en primer plano, el supervisor lo vigila) y se para con
/// SIGQUIT (termina lo que esté sirviendo).
/// <list type="bullet">
/// <item><b>Sin <c>user</c> ni <c>group</c>:</b> php-fpm corre como el usuario, y como usuario rechaza
/// esas directivas (solo valen como root).</item>
/// <item><c>clear_env = no</c>: los workers heredan el entorno del servicio (<c>PHP_INI_SCAN_DIR</c>, el
/// PATH del usuario), como php-cgi.</item>
/// <item><c>catch_workers_output = yes</c>: lo que un script escribe por stderr va al log de FPM, no se pierde.</item>
/// <item><c>pm = dynamic</c> dimensionado desde <c>[php] pool</c> (<see cref="Sizing"/>) y <c>pm.max_requests</c>
/// para reciclar los workers (<see cref="PhpPool.MaxRequests"/>), como el <c>PHP_FCGI_MAX_REQUESTS</c> de php-cgi.</item>
/// </list>
/// </summary>
public static class PhpFpmConf
{
    public const string FileName = "php-fpm.conf";

    /// <summary>Lo que se deja a los workers para acabar cuando el master recibe SIGQUIT antes de matarlos.</summary>
    public const int ProcessControlTimeoutSeconds = 10;

    /// <summary>
    /// PURO: el tamaño del pool dinámico a partir de <c>[php] pool</c> (los php-cgi que habría en
    /// Windows, de 1 a 10): arrancan <paramref name="poolSize"/> workers, se dejan entre la mitad y el
    /// doble de reserva, y nunca más de cuatro veces el pool. Cumple lo que exige FPM:
    /// <c>min_spare ≤ start ≤ max_spare ≤ max_children</c>.
    /// </summary>
    public static (int MaxChildren, int StartServers, int MinSpare, int MaxSpare) Sizing(int poolSize)
    {
        var start = Math.Clamp(poolSize, 1, 10);
        return (MaxChildren: start * 4, StartServers: start, MinSpare: Math.Max(1, start / 2), MaxSpare: start * 2);
    }

    /// <summary>
    /// PURO: genera el fichero. Las rutas van entre comillas (admiten espacios); los textos de los
    /// comentarios, sin tildes, como todo lo que genera Roost.
    /// </summary>
    /// <param name="installName">La instalación (<c>8.4.27-nts-x64</c>), para la cabecera.</param>
    /// <param name="pidFile">El pid del master, en <c>run/</c>.</param>
    /// <param name="errorLog">El log de FPM (el master y, con <c>catch_workers_output</c>, el stderr de los workers), en <c>logs/</c>.</param>
    /// <param name="port">El puerto TCP en 127.0.0.1: el primero del pool de la rama.</param>
    /// <param name="poolSize"><c>[php] pool</c>.</param>
    /// <param name="maxRequests">Tras cuántas peticiones se recicla cada worker.</param>
    public static string Render(string installName, string pidFile, string errorLog, int port, int poolSize, int maxRequests)
    {
        var (maxChildren, start, minSpare, maxSpare) = Sizing(poolSize);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"""
            ; Generado por Roost para PHP {installName}. NO lo edites: se regenera en cada `roost up`.
            ; php-fpm corre como tu usuario, en primer plano (-F), vigilado por roostd; nginx le habla por
            ; FastCGI en 127.0.0.1:{port}. Sin user/group: como usuario, php-fpm los rechaza.

            [global]
            pid = "{pidFile}"
            error_log = "{errorLog}"
            daemonize = no
            process_control_timeout = {ProcessControlTimeoutSeconds}s

            [www]
            listen = 127.0.0.1:{port}
            listen.allowed_clients = 127.0.0.1
            pm = dynamic
            pm.max_children = {maxChildren}
            pm.start_servers = {start}
            pm.min_spare_servers = {minSpare}
            pm.max_spare_servers = {maxSpare}
            pm.max_requests = {maxRequests}
            catch_workers_output = yes
            decorate_workers_output = no
            clear_env = no

            """);
        return sb.ToString();
    }

    /// <summary>PURO: los argumentos del servicio: en primer plano, con esta conf y este php.ini.</summary>
    public static string Arguments(string confFile, string phpIni) => $"-F -y \"{confFile}\" -c \"{phpIni}\"";
}
