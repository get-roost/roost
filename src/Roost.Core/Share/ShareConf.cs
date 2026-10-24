using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Share;

/// <summary>Cómo se comparte un sitio: por internet con un túnel, o en la red local.</summary>
public enum ShareKind
{
    /// <summary>Un túnel rápido de Cloudflare (<c>https://&lt;algo&gt;.trycloudflare.com</c>) hacia una escucha propia en loopback.</summary>
    Tunnel,

    /// <summary>Una escucha HTTPS en la IP de la red local, con un certificado que lleva esa IP.</summary>
    Lan,
}

/// <summary>
/// Una escucha más del servidor web mientras se comparte un sitio: la del túnel
/// (<c>127.0.0.1:&lt;puerto libre&gt;</c>, solo la ve cloudflared) o la de la red local
/// (<c>&lt;IP de la LAN&gt;:&lt;puerto&gt;</c>). Solo existe a petición del usuario y mientras dura: la
/// lleva el daemon en memoria, nunca se guarda en disco, así que un <c>up</c> después de reiniciar
/// Roost no vuelve a abrir nada.
/// </summary>
public sealed record ShareListener(string Site, ShareKind Kind, string Address, int Port)
{
    /// <summary>Su fichero en <c>etc/&lt;servidor&gt;/share</c>: <c>tienda-tunnel.conf</c>, <c>tienda-lan.conf</c>.</summary>
    public string FileName => $"{Site}-{KindKey(Kind)}.conf";

    /// <summary>«tunnel» o «lan»: como va en la API del daemon y en el JSON de la CLI.</summary>
    public static string KindKey(ShareKind kind) => kind == ShareKind.Tunnel ? "tunnel" : "lan";

    public static bool TryParseKind(string? text, out ShareKind kind)
    {
        switch (text)
        {
            case "tunnel":
                kind = ShareKind.Tunnel;
                return true;
            case "lan":
                kind = ShareKind.Lan;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>
/// El vhost de una escucha de compartir (<see cref="ShareListener"/>), aparte del <c>sites/&lt;sitio&gt;.conf</c>
/// (que no se toca): sirve la misma raíz web con el mismo pool de PHP y el controlador frontal de
/// Roost, y es el único servidor de su dirección y su puerto, así que atiende cualquier
/// <c>Host</c>. Por eso a PHP le llega el nombre con el que entra el visitante
/// (<c>xxx.trycloudflare.com</c> o <c>192.168.1.20:8443</c>) y Laravel o Symfony arman bien sus URLs;
/// lo que no se arregla así son las URLs guardadas (<c>APP_URL</c>, las de WordPress en su base de
/// datos), que siguen en <c>.test</c>. Lo editado en el vhost del sitio, fuera de las marcas, no
/// cuenta aquí.
/// <list type="bullet">
/// <item>Túnel: HTTP en loopback (lo cifra Cloudflare hasta el visitante); a PHP se le dice que la
/// petición es HTTPS en el 443 (así no salen URLs <c>http://</c> en una página <c>https://</c>) y la IP
/// del visitante (<c>CF-Connecting-IP</c>, que pone Cloudflare), para que lo que solo se enseña a
/// 127.0.0.1 no se le enseñe a internet. Lleva <c>X-Robots-Tag: noindex</c> y quita el
/// <c>X-Powered-By</c> de PHP (su versión no se anuncia a internet).</item>
/// <item>Red local: HTTPS con el certificado del sitio que lleva la IP, y la CA (solo su certificado)
/// en <see cref="CaPath"/>, para instalarla en el móvil si se quiere.</item>
/// </list>
/// </summary>
public static class ShareConf
{
    /// <summary>Donde se sirve la CA (solo su parte pública) en la escucha de la red local.</summary>
    public const string CaPath = "/__roost/roost-ca.crt";

    /// <summary>Lo que necesita el vhost; todas las rutas, absolutas.</summary>
    /// <param name="Upstream">El pool de PHP del sitio: <c>php-8.4</c>.</param>
    /// <param name="FastCgiParams">Solo con nginx: su <c>fastcgi_params</c>.</param>
    /// <param name="CertFile">Solo en la red local: el certificado del sitio con la IP.</param>
    /// <param name="CaCertFile">Solo en la red local: la CA, que se sirve en <see cref="CaPath"/>.</param>
    public sealed record Input(
        ShareListener Listener,
        Site Site,
        string Upstream,
        string? FastCgiParams = null,
        string? CertFile = null,
        string? KeyFile = null,
        string? CaCertFile = null);

    /// <summary>PURO: el <c>share/&lt;sitio&gt;-&lt;tipo&gt;.conf</c> de nginx.</summary>
    public static string RenderNginx(Input input)
    {
        var listener = input.Listener;
        var tunnel = listener.Kind == ShareKind.Tunnel;
        var fastCgiParams = NginxConf.Nginx(input.FastCgiParams ?? throw new ArgumentException("nginx necesita su fastcgi_params.", nameof(input)));

        var head = tunnel
            ? """
                  add_header X-Robots-Tag "noindex, nofollow" always;
                  # Por internet no se dice que PHP hay detras (con expose_php, PHP lo anuncia en cada respuesta).
                  fastcgi_hide_header X-Powered-By;

              """
            : $$"""
                  http2 on;
                  ssl_certificate "{{NginxConf.Nginx(Required(input.CertFile))}}";
                  ssl_certificate_key "{{NginxConf.Nginx(Required(input.KeyFile))}}";

                  # La CA de Roost (solo su certificado, sin la clave), por si quieres instalarla en el movil.
                  location = {{CaPath}} {
                      alias "{{NginxConf.Nginx(Required(input.CaCertFile))}}";
                      default_type application/x-x509-ca-cert;
                  }

              """;
        // Lo del tunel va DESPUES del include: con un parametro repetido, php-cgi se queda con el ultimo.
        var php = tunnel
            ? """

                      # Para PHP es HTTPS en el 443 (lo cifra Cloudflare hasta el visitante), y REMOTE_ADDR
                      # es la IP del visitante, no 127.0.0.1.
                      fastcgi_param HTTPS on;
                      fastcgi_param REQUEST_SCHEME https;
                      fastcgi_param SERVER_PORT 443;
                      fastcgi_param REMOTE_ADDR $http_cf_connecting_ip if_not_empty;
              """
            : "";

        return $$"""
            # Generado por Roost mientras compartes {{input.Site.Host}} {{Where(listener)}}.
            # NO lo edites: se borra al dejar de compartir (roost share stop {{input.Site.Name}}).
            server {
                listen {{listener.Address}}:{{listener.Port}}{{(tunnel ? "" : " ssl")}};
                server_name {{input.Site.Host}};
                root "{{NginxConf.Nginx(input.Site.DocumentRoot)}}";
                index index.php index.html index.htm;
            {{head}}
                location ~ \.php$ {
                    try_files $uri =404;
                    fastcgi_pass {{input.Upstream}};
                    fastcgi_index index.php;
                    fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
                    include "{{fastCgiParams}}";{{php}}
                }

                location / {
                    try_files $uri $uri/ /index.php?$query_string;
                }
            }

            """;
    }

    /// <summary>PURO: el <c>share/&lt;sitio&gt;-&lt;tipo&gt;.conf</c> de Apache, con su propio <c>Listen</c>.</summary>
    public static string RenderApache(Input input)
    {
        var listener = input.Listener;
        var tunnel = listener.Kind == ShareKind.Tunnel;
        var root = ApacheConf.Path(input.Site.DocumentRoot);
        var extra = tunnel
            ? """
                  Header always set X-Robots-Tag "noindex, nofollow"
                  # Por internet no se dice que PHP hay detras (con expose_php, PHP lo anuncia en cada respuesta).
                  # "always": Apache guarda las cabeceras que da PHP por FastCGI en la tabla de los errores.
                  Header always unset X-Powered-By
                  Header unset X-Powered-By
                  # Para PHP es HTTPS en el 443 (lo cifra Cloudflare hasta el visitante), y REMOTE_ADDR
                  # es la IP del visitante, no 127.0.0.1.
                  ProxyFCGISetEnvIf "true" HTTPS "on"
                  ProxyFCGISetEnvIf "true" REQUEST_SCHEME "https"
                  ProxyFCGISetEnvIf "true" SERVER_PORT "443"
                  ProxyFCGISetEnvIf "-n req('CF-Connecting-IP')" REMOTE_ADDR "%{req:CF-Connecting-IP}"
              """
            : $$"""
                  SSLEngine on
                  SSLCertificateFile "{{ApacheConf.Path(Required(input.CertFile))}}"
                  SSLCertificateKeyFile "{{ApacheConf.Path(Required(input.KeyFile))}}"
                  # La CA de Roost (solo su certificado, sin la clave), por si quieres instalarla en el movil.
                  Alias "{{CaPath}}" "{{ApacheConf.Path(Required(input.CaCertFile))}}"
                  <Location "{{CaPath}}">
                      Require all granted
                      ForceType application/x-x509-ca-cert
                  </Location>
              """;

        return $$"""
            # Generado por Roost mientras compartes {{input.Site.Host}} {{Where(listener)}}.
            # NO lo edites: se borra al dejar de compartir (roost share stop {{input.Site.Name}}).
            Listen {{listener.Address}}:{{listener.Port}}
            <VirtualHost {{listener.Address}}:{{listener.Port}}>
                ServerName {{input.Site.Host}}
                DocumentRoot "{{root}}"
                <Directory "{{root}}">
                    Options Indexes FollowSymLinks
                    AllowOverride All
                    Require all granted
                </Directory>
            {{ApacheConf.PhpHandler(input.Upstream)}}
            {{extra}}
            </VirtualHost>

            """;
    }

    private static string Where(ShareListener listener) =>
        listener.Kind == ShareKind.Tunnel ? "por internet (aqui llega el tunel)" : "en la red local";

    private static string Required(string? path) =>
        path ?? throw new ArgumentException("La escucha de la red local necesita su certificado, su clave y la CA.");
}
