using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Roost.WebPanel;

/// <summary>
/// Las reglas de seguridad del panel (las de ROOST.md, decisión 42), puras para probarlas sin servidor:
/// <list type="bullet">
/// <item>solo se escucha en <c>127.0.0.1</c> y <c>[::1]</c>; nunca en <c>0.0.0.0</c> (desde otra máquina, <c>ssh -L</c>);</item>
/// <item>la cabecera <c>Host</c> tiene que ser <c>127.0.0.1:&lt;puerto&gt;</c>, <c>localhost:&lt;puerto&gt;</c> o
/// <c>[::1]:&lt;puerto&gt;</c>: cualquier web abierta en el navegador puede intentar un DNS rebinding;</item>
/// <item>el token de la URL (<c>?t=</c>) se cambia por una cookie <c>HttpOnly</c>, <c>SameSite=Strict</c>, y sin
/// ella todo es 401 (la URL con el token no se queda en el historial: se redirige sin él);</item>
/// <item>lo que no es GET exige <c>Origin</c> igual al propio y la cabecera <c>X-Roost-Panel</c> (anti-CSRF: un
/// formulario de otra web no puede ponerla, y no hay CORS que la deje pasar);</item>
/// <item>CSP estricta (<c>default-src 'self'</c>, sin scripts en línea), sin CORS, sin caché de la API.</item>
/// </list>
/// </summary>
public static class PanelSecurity
{
    public const string CookieName = "roost_panel";
    public const string CsrfHeader = "X-Roost-Panel";
    public const string TokenQuery = "t";

    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    /// <summary>Los valores de <c>Host</c> que valen para el puerto dado (y nada más).</summary>
    public static IReadOnlyList<string> AllowedHosts(int port) =>
        [$"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}"];

    public static bool IsAllowedHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var allowed in AllowedHosts(port))
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>true si el token presentado es el de esta sesión (comparación en tiempo constante).</summary>
    public static bool TokenMatches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Lo que no es GET ni HEAD tiene que venir de la propia página: <c>Origin</c> igual a
    /// <c>http://&lt;Host&gt;</c> (el navegador lo pone siempre en los POST) y la cabecera anti-CSRF.
    /// </summary>
    public static bool IsSameOriginWrite(string method, string? host, string? origin, string? csrfHeader)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) return true;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(origin)) return false;
        if (!string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase)) return false;
        return csrfHeader == "1";
    }

    /// <summary>Las cabeceras que llevan todas las respuestas.</summary>
    public static void AddHeaders(IHeaderDictionary headers, bool api)
    {
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        if (api) headers["Cache-Control"] = "no-store";
    }

    public static CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = false,   // solo loopback: no hay TLS, y Secure impediría la cookie en http://127.0.0.1
        Path = "/",
        IsEssential = true,
    };
}
