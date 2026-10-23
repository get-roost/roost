using Roost.Core.Sites;

namespace Roost.Core.Stack;

/// <summary>
/// El <c>sites/&lt;nombre&gt;.conf</c> de nginx de cada sitio (ROOST.md §5.6). El usuario puede
/// editarlo, pero solo FUERA de las marcas <c># roost:begin</c> / <c># roost:end</c>: Roost
/// regenera lo de dentro en cada <c>roost up</c> y respeta el resto.
/// </summary>
public static class SiteConf
{
    public const string Begin = "# roost:begin";
    public const string End = "# roost:end";

    /// <summary>Lo que necesita el bloque de Roost; todas las rutas, absolutas.</summary>
    public sealed record Input(
        Site Site,
        string Upstream,
        int HttpPort,
        int HttpsPort,
        string CertFile,
        string KeyFile,
        string FastCgiParams);

    /// <summary>PURO: el bloque que regenera Roost, marcas incluidas, sangrado para ir dentro de <c>server { }</c>.</summary>
    public static string RenderBlock(Input input)
    {
        var host = input.Site.Host;
        return $$"""
                {{Begin}}  (lo regenera Roost en cada roost up; edita fuera de las marcas)
                listen 127.0.0.1:{{input.HttpPort}};
                listen 127.0.0.1:{{input.HttpsPort}} ssl;
                http2 on;
                server_name {{host}} *.{{host}};
                root "{{NginxConf.Nginx(input.Site.DocumentRoot)}}";
                index index.php index.html index.htm;
                ssl_certificate "{{NginxConf.Nginx(input.CertFile)}}";
                ssl_certificate_key "{{NginxConf.Nginx(input.KeyFile)}}";

                location ~ \.php$ {
                    try_files $uri =404;
                    fastcgi_pass {{input.Upstream}};
                    fastcgi_index index.php;
                    fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
                    include "{{NginxConf.Nginx(input.FastCgiParams)}}";
                }
                {{End}}
            """;
    }

    /// <summary>PURO: el fichero entero, la primera vez.</summary>
    public static string RenderNew(Input input) => $$"""
        # Sitio {{input.Site.Name}} de Roost: https://{{input.Site.Host}}/
        # Puedes editar este fichero FUERA de las marcas roost:begin / roost:end (por ejemplo el
        # location / de abajo, o reglas propias): Roost solo regenera lo que hay entre ellas.
        server {
        {{RenderBlock(input)}}

            # Controlador frontal (Laravel, Symfony, WordPress...): lo que no es un fichero va a index.php.
            location / {
                try_files $uri $uri/ /index.php?$query_string;
            }
        }

        """;

    /// <summary>
    /// PURO: <paramref name="existing"/> con el bloque de Roost sustituido por el actual. Si el
    /// usuario ha borrado las marcas, no se adivina: se lanza <see cref="RoostException"/>.
    /// </summary>
    public static string Merge(string existing, Input input, string path)
    {
        var begin = existing.IndexOf(Begin, StringComparison.Ordinal);
        var end = begin < 0 ? -1 : existing.IndexOf(End, begin, StringComparison.Ordinal);
        if (begin < 0 || end < 0)
        {
            throw new RoostException(
                $"{path} ya no tiene las marcas {Begin} / {End}, asi que Roost no sabe que parte regenerar. "
                + "Vuelve a ponerlas, o borra el fichero y Roost lo creara de nuevo.");
        }

        // Desde el principio de la línea de la marca de inicio hasta el final de la de cierre.
        var lineStart = existing.LastIndexOf('\n', begin) + 1;
        var lineEnd = end + End.Length;
        return existing[..lineStart] + RenderBlock(input) + existing[lineEnd..];
    }
}
