using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Roost.Core.Platform;

namespace Roost.Core.Stack;

/// <summary>
/// El <c>etc/redis/redis.conf</c> que genera Roost (ROOST.md §5.5, como el my.ini): solo en
/// 127.0.0.1, los datos en <c>data/redis</c> con AOF (se pierde como mucho un segundo) y, al
/// final, el <c>conf.d/99-usuario.conf</c> del usuario, que manda.
/// </summary>
/// <remarks>
/// La build MSYS2 de Redis entiende en el <c>.conf</c> rutas con letra de unidad y <c>/</c>, pero en
/// la línea de órdenes solo las de Cygwin (<c>/c/roost/...</c>) o las relativas: por eso el servicio
/// arranca desde <c>etc/redis</c> con <c>redis-server redis.conf</c>. Dentro del <c>.conf</c>, las
/// rutas son absolutas: Redis hace <c>chdir</c> al leer <c>dir</c>, y un include relativo que vaya
/// después se buscaría en la carpeta de datos (probado en real).
/// </remarks>
public static class RedisConf
{
    public const string FileName = "redis.conf";
    public const string UserFragmentName = "99-usuario.conf";

    /// <summary>La carpeta de la configuración de Redis: <c>etc/redis</c> (el servicio arranca desde aquí).</summary>
    public static string Directory(RoostPaths paths) => Path.Combine(paths.Etc, "redis");

    public static string ConfPath(RoostPaths paths) => Path.Combine(Directory(paths), FileName);

    public static string UserFragmentPath(RoostPaths paths) => Path.Combine(Directory(paths), "conf.d", UserFragmentName);

    /// <summary>PURO: el texto del redis.conf.</summary>
    public static string Render(string version, int port, string dataDirectory, string userFragment) => $"""
        # Generado por Roost para Redis {version}. NO lo edites: se regenera en cada `roost up`.
        # Lo tuyo va en conf.d/{UserFragmentName} (se incluye al final, asi que manda).
        bind 127.0.0.1
        port {port}
        protected-mode yes
        daemonize no
        databases 16
        # Windows le deja unos 3.200 descriptores: con los 10.000 de serie, Redis avisa en cada arranque.
        maxclients 1000
        dir "{Slashes(dataDirectory)}"
        dbfilename dump.rdb
        save 3600 1 300 100 60 10000
        # AOF: con everysec se pierde como mucho un segundo, sin depender de BGSAVE (que en
        # Windows va por un fork() emulado y es lento).
        appendonly yes
        appendfsync everysec
        appenddirname appendonlydir
        # El log a la salida estandar: lo guarda Roost en logs/redis.log.
        logfile ""
        loglevel notice
        include "{Slashes(userFragment)}"

        """;

    /// <summary>Contenido inicial del fragmento del usuario (solo se escribe si no existe).</summary>
    public const string UserFragmentTemplate = """
        # Tus ajustes de Redis. Roost no toca este fichero y lo incluye al final de redis.conf,
        # asi que lo que pongas aqui manda. Se aplica al reiniciar Redis (roost restart redis).
        # Ejemplos:
        # notify-keyspace-events Ex
        # maxmemory 256mb
        # maxmemory-policy allkeys-lru
        # requirepass una-contrasena   (y REDIS_PASSWORD en el .env de tus proyectos)

        """;

    /// <summary>
    /// Escribe el redis.conf (se pisa siempre), crea el fragmento del usuario si no existe y la
    /// carpeta de datos, y devuelve la ruta del redis.conf. <c>data/redis</c> no se borra nunca.
    /// </summary>
    public static string Write(RoostPaths paths, string version, int port)
    {
        var userFile = UserFragmentPath(paths);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        if (!File.Exists(userFile)) File.WriteAllText(userFile, UserFragmentTemplate);
        System.IO.Directory.CreateDirectory(paths.RedisData);

        var conf = ConfPath(paths);
        File.WriteAllText(conf, Render(version, port, paths.RedisData, userFile));
        return conf;
    }

    /// <summary>
    /// PURO: la contraseña que pone el usuario con <c>requirepass</c> en su fragmento (la última
    /// que cuenta), o null. La necesita <c>redis-cli shutdown</c> para parar limpio.
    /// </summary>
    public static string? UserPassword(string? userFragment)
    {
        if (userFragment is null) return null;
        string? password = null;
        foreach (var raw in userFragment.Split('\n'))
        {
            var match = Regex.Match(raw.Trim(), @"^requirepass\s+(?:""(?<q>[^""]*)""|'(?<q>[^']*)'|(?<p>\S+))", RegexOptions.IgnoreCase);
            if (match.Success) password = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["p"].Value;
        }
        return string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>Lo que lee <see cref="UserPassword(string?)"/> del fragmento en disco; null si no hay o no se puede leer.</summary>
    public static string? UserPassword(RoostPaths paths)
    {
        try { return UserPassword(File.Exists(UserFragmentPath(paths)) ? File.ReadAllText(UserFragmentPath(paths)) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>PURO: las líneas para el <c>.env</c> de Laravel (con predis: phpredis no viene en los PHP de windows.php.net).</summary>
    public static string EnvLines(int port) =>
        $"REDIS_CLIENT=predis\nREDIS_HOST=127.0.0.1\nREDIS_PASSWORD=null\nREDIS_PORT={port}";

    /// <summary>Redis en el .conf: unidad y barras <c>/</c> (<c>C:/roost/data/redis</c>).</summary>
    public static string Slashes(string path) => path.Replace('\\', '/');
}

/// <summary>
/// Pregunta a Redis si está vivo con un <c>PING</c> por TCP (RESP), sin <c>redis-cli</c>: 1 s como
/// mucho y nunca lanza. Lo usan <c>roost redis status</c> y la app.
/// </summary>
public static class RedisProbe
{
    /// <summary>La primera línea que contesta (<c>+PONG</c>, <c>-NOAUTH …</c>), o null si no contesta.</summary>
    public static async Task<string?> PingAsync(int port, CancellationToken cancellation = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync("*1\r\n$4\r\nPING\r\n"u8.ToArray(), timeout.Token);
            var buffer = new byte[256];
            var line = new StringBuilder();
            while (!line.ToString().Contains('\n'))
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                line.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            var text = line.ToString().Split('\n')[0].TrimEnd('\r');
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PURO: si la respuesta es la de un Redis sano.</summary>
    public static bool IsPong(string? reply) => reply == "+PONG";

    /// <summary>PURO: si contesta pero pide contraseña (<c>requirepass</c>).</summary>
    public static bool NeedsPassword(string? reply) => reply?.StartsWith("-NOAUTH", StringComparison.Ordinal) == true;
}
