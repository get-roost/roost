using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Roost.Core.Deploy;

/// <summary>La base de datos que se escribe en el <c>wp-config.php</c>.</summary>
/// <param name="Host">Con el puerto si no es el 3306 (<c>127.0.0.1:3409</c>): WordPress lo entiende así.</param>
public sealed record WpDatabase(string Name, string User, string Password, string Host);

/// <summary>
/// El <c>wp-config.php</c> de WordPress hecho a partir de su <c>wp-config-sample.php</c>: la base de
/// datos de Roost y las ocho claves y salts nuevas, generadas aquí con
/// <see cref="RandomNumberGenerator"/> (como las de <c>api.wordpress.org/secret-key/</c>, sin ir a la
/// red). Solo cambia el valor de cada <c>define()</c>: el resto del fichero (comentarios, el
/// <c>$table_prefix</c>, <c>WP_DEBUG</c>, los finales de línea) sale tal cual. PURO salvo las salts.
/// </summary>
public static partial class WpConfig
{
    public const string FileName = "wp-config.php";
    public const string SampleFileName = "wp-config-sample.php";

    /// <summary>Las ocho claves y salts de WordPress, en el orden del ejemplo.</summary>
    public static readonly IReadOnlyList<string> SaltNames =
        ["AUTH_KEY", "SECURE_AUTH_KEY", "LOGGED_IN_KEY", "NONCE_KEY", "AUTH_SALT", "SECURE_AUTH_SALT", "LOGGED_IN_SALT", "NONCE_SALT"];

    /// <summary>
    /// Los caracteres de las salts: los de <c>wp_generate_password(64, true, true)</c> sin la comilla
    /// simple, la barra invertida, la comilla doble ni el acento grave, para que vayan tal cual entre
    /// comillas simples de PHP (y no den guerra al copiarlas).
    /// </summary>
    public const string SaltCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_ []{}<>~+=,.;:/?|";

    public const int SaltLength = 64;

    /// <summary>Lo que trae el ejemplo en cada salt: si queda alguno, no se ha cambiado.</summary>
    public const string SamplePhrase = "put your unique phrase here";

    // define( 'NOMBRE', 'valor' ); con cualquier espaciado y con comillas simples o dobles (y escapes dentro).
    [GeneratedRegex(@"define\s*\(\s*(?<q1>['""])(?<name>[A-Z_]+)\k<q1>\s*,\s*(?<literal>'(?:\\.|[^'\\])*'|""(?:\\.|[^""\\])*"")\s*\)")]
    private static partial Regex DefinePattern();

    /// <summary>Una salt nueva: 64 caracteres de <see cref="SaltCharacters"/> al azar (criptográfico).</summary>
    public static string NewSalt() => new(RandomNumberGenerator.GetItems<char>(SaltCharacters, SaltLength));

    /// <summary>Las ocho, nuevas.</summary>
    public static IReadOnlyDictionary<string, string> NewSalts() => SaltNames.ToDictionary(n => n, _ => NewSalt());

    /// <summary>
    /// PURO: el <c>wp-config.php</c> que sale de <paramref name="sample"/> con <paramref name="db"/> y
    /// <paramref name="salts"/> (las que falten en el diccionario, nuevas). Lanza
    /// <see cref="RoostException"/> si al ejemplo le falta alguno de los <c>DB_*</c>: no se adivina.
    /// Las salts que no estén en el ejemplo no se añaden (WordPress las guarda entonces en la base de datos).
    /// </summary>
    public static string FromSample(string sample, WpDatabase db, IReadOnlyDictionary<string, string>? salts = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DB_NAME"] = db.Name,
            ["DB_USER"] = db.User,
            ["DB_PASSWORD"] = db.Password,
            ["DB_HOST"] = db.Host,
        };
        foreach (var name in SaltNames) values[name] = salts is not null && salts.TryGetValue(name, out var salt) ? salt : NewSalt();

        var found = new HashSet<string>(StringComparer.Ordinal);
        var text = DefinePattern().Replace(sample, m =>
        {
            var name = m.Groups["name"].Value;
            if (!values.TryGetValue(name, out var value)) return m.Value;
            found.Add(name);
            // Solo el valor: el define, sus espacios y su punto y coma quedan como estaban.
            var literal = m.Groups["literal"];
            return m.Value[..(literal.Index - m.Index)] + Literal(value) + m.Value[(literal.Index - m.Index + literal.Length)..];
        });

        var missing = new[] { "DB_NAME", "DB_USER", "DB_PASSWORD", "DB_HOST" }.Where(n => !found.Contains(n)).ToList();
        if (missing.Count > 0)
            throw new RoostException(
                $"{SampleFileName} no tiene la linea de {string.Join(", ", missing)} (define('{missing[0]}', ...)): no se adivina. "
                + "Crea wp-config.php a mano o con el instalador de WordPress.");
        return text;
    }

    /// <summary>PURO: el valor de un <c>define()</c> del fichero (ya sin comillas), o null si no está.</summary>
    public static string? Read(string config, string name)
    {
        foreach (Match m in DefinePattern().Matches(config))
        {
            if (m.Groups["name"].Value != name) continue;
            var literal = m.Groups["literal"].Value;
            var inner = literal[1..^1];
            return literal[0] == '\'' ? inner.Replace("\\'", "'").Replace("\\\\", "\\") : Regex.Unescape(inner);
        }
        return null;
    }

    /// <summary>PURO: <paramref name="value"/> como cadena de PHP entre comillas simples (con <c>\</c> y <c>'</c> escapadas).</summary>
    public static string Literal(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>PURO: el host para <c>DB_HOST</c>: <c>127.0.0.1</c> con el puerto detrás si no es el de siempre.</summary>
    public static string Host(int port) => port == 3306 ? "127.0.0.1" : $"127.0.0.1:{port}";

    /// <summary>
    /// El mu-plugin que deja Roost en <c>wp-content/mu-plugins</c>: las peticiones de WordPress a los
    /// <c>.test</c> (la Salud del sitio, WP-Cron, el editor) usan el bundle de CA del php.ini de Roost
    /// (<c>curl.cainfo</c>, con la CA local) en vez del que trae WordPress, que no la conoce. Solo para
    /// los <c>.test</c>, y solo si el php.ini tiene ese bundle: nunca apaga la comprobación del certificado.
    /// </summary>
    public const string LoopbackPluginName = "roost-certificados-locales.php";

    public const string LoopbackPlugin = """
        <?php
        /**
         * Plugin Name: Roost: certificados locales
         * Description: Las peticiones de WordPress a sus propios .test (Salud del sitio, WP-Cron, el editor) usan el bundle de CA de Roost, que incluye la CA local. Lo puso Roost al crear el sitio; si lo borras, esas peticiones fallan con «cURL error 60».
         */

        add_filter('http_request_args', static function ($args, $url) {
            $host = parse_url($url, PHP_URL_HOST);
            $bundle = ini_get('curl.cainfo');
            if (is_string($host) && preg_match('/\.test$/i', $host) && is_string($bundle) && $bundle !== '' && is_readable($bundle)) {
                $args['sslcertificates'] = $bundle;
            }
            return $args;
        }, 10, 2);

        """;
}
