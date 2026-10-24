using System.Text;
using Roost.Core.Models;

namespace Roost.Core.Stack;

/// <summary>
/// Puertos del pool de php-cgi de una rama (ROOST.md §5.5): <c>9000 + mayor·100 + menor·10 + i</c>,
/// así la 8.3 usa 9830, 9831… y dos ramas no chocan mientras el pool sea de 10 o menos.
/// </summary>
public static class PhpPool
{
    public static IReadOnlyList<int> Ports(ComponentVersion version, int size) =>
        Enumerable.Range(0, size).Select(i => 9000 + version.Major * 100 + version.Minor * 10 + i).ToList();

    /// <summary>
    /// Tras cuántas peticiones sale el php-cgi <paramref name="index"/> del pool (y el supervisor lo
    /// relanza): 500, 561, 622… Con el mismo número para todos y el reparto por turnos, llegaban a la
    /// vez y el pool entero se reciclaba de golpe (502 con carga); con periodos distintos se separan.
    /// </summary>
    public static int MaxRequests(int index) => 500 + index * 61;

    /// <summary>Nombre del bloque <c>upstream</c> de nginx para una rama: <c>php-8.3</c>.</summary>
    public static string UpstreamName(ComponentVersion version) => $"php-{version.Major}.{version.Minor}";
}

/// <summary>Un bloque <c>upstream</c>: un nombre y los puertos de su pool de php-cgi.</summary>
public sealed record NginxUpstream(string Name, IReadOnlyList<int> Ports);

/// <summary>Lo que necesita <see cref="NginxConf.Render"/>; todas las rutas, absolutas.</summary>
/// <param name="Ipv6">Escuchar también en <c>[::1]</c> (false en una máquina sin IPv6 en loopback).</param>
public sealed record NginxConfInput(
    string MimeTypes,
    string FastCgiParams,
    string RunDirectory,
    string LogsDirectory,
    string Www,
    int HttpPort,
    IReadOnlyList<NginxUpstream> Upstreams,
    string DefaultUpstream,
    string SitesDirectory,
    bool Ipv6 = true)
{
    /// <summary>
    /// La carpeta de Adminer (<c>bin/adminer/&lt;versión&gt;</c>) que se sirve en <c>/adminer</c> con el
    /// PHP global; null = sin Adminer (el usuario tiene su propia <c>www/adminer</c>).
    /// </summary>
    public string? AdminerDirectory { get; init; }
}

/// <summary>
/// El nginx.conf base que genera Roost (ROOST.md §5.6): un <c>upstream php-X.Y</c> por cada
/// pool, el sitio por defecto en <c>http://localhost/</c> (la carpeta www) y un
/// <c>include sites/*.conf</c> con los vhosts <c>*.test</c> (<see cref="SiteConf"/>). Escucha
/// solo en loopback (127.0.0.1 y, si lo hay, [::1]): es un entorno de desarrollo y no debe verse
/// desde la red. Lo de [::1] importa porque Windows resuelve <c>localhost</c> primero a <c>::1</c>:
/// sin ello, <c>http://localhost</c> podía acabar en otro servidor que escuchase en <c>[::]:80</c>.
/// </summary>
public static class NginxConf
{
    /// <summary>Las directivas <c>listen</c> de un puerto: 127.0.0.1 y, con IPv6, también [::1].</summary>
    public static string Listen(int port, bool ipv6, string indent, string suffix = "") =>
        $"{indent}listen 127.0.0.1:{port}{suffix};\n" + (ipv6 ? $"{indent}listen [::1]:{port}{suffix};\n" : "");

    /// <summary>PURO: genera el texto del nginx.conf.</summary>
    public static string Render(NginxConfInput input)
    {
        var run = Nginx(input.RunDirectory);
        var logs = Nginx(input.LogsDirectory);

        // max_fails=0: un php-cgi que se reinicia (o que se cae) no deja su servidor «caído» 10 s.
        // Con lo de serie, al reiniciar el pool uno a uno nginx acababa con todos marcados y daba
        // 502 («no live upstreams») aunque los demás ya estuvieran en marcha: así, si uno no
        // contesta, la petición pasa al siguiente sin más (probado en real con carga).
        var upstreams = new StringBuilder();
        foreach (var upstream in input.Upstreams)
        {
            upstreams.Append($"    upstream {upstream.Name} {{\n");
            foreach (var port in upstream.Ports)
                upstreams.Append($"        server 127.0.0.1:{port} max_fails=0;\n");
            upstreams.Append("    }\n\n");
        }

        return $$"""
            # Generado por Roost: NO lo edites, se regenera en cada `roost up`.
            worker_processes 1;
            pid "{{run}}/nginx.pid";
            error_log "{{logs}}/nginx-error.log" warn;

            events {
                worker_connections 1024;
            }

            http {
                include "{{Nginx(input.MimeTypes)}}";
                default_type application/octet-stream;
                sendfile on;
                keepalive_timeout 65;
                client_max_body_size 128m;
                # Lo que se espera a php-cgi (60 s por defecto): parado en un breakpoint de Xdebug o
                # con una migración larga, el navegador no recibe un 504. Lo heredan todos los sitios.
                fastcgi_read_timeout 600s;
                fastcgi_send_timeout 600s;
                access_log "{{logs}}/nginx-access.log";

                client_body_temp_path "{{run}}/nginx-temp/client_body";
                proxy_temp_path "{{run}}/nginx-temp/proxy";
                fastcgi_temp_path "{{run}}/nginx-temp/fastcgi";
                uwsgi_temp_path "{{run}}/nginx-temp/uwsgi";
                scgi_temp_path "{{run}}/nginx-temp/scgi";

            {{upstreams}}    server {
            {{Listen(input.HttpPort, input.Ipv6, "        ")}}        server_name localhost;
                    root "{{Nginx(input.Www)}}";
                    index index.php index.html index.htm;
                    autoindex on;
            {{Adminer(input)}}
                    location ~ \.php$ {
                        try_files $uri =404;
                        fastcgi_pass {{input.DefaultUpstream}};
                        fastcgi_index index.php;
                        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
                        include "{{Nginx(input.FastCgiParams)}}";
                    }
                }

                # Un fichero por sitio *.test, editable fuera de las marcas de Roost.
                include "{{Nginx(input.SitesDirectory)}}/*.conf";
            }

            """;
    }

    /// <summary>
    /// PURO: <c>/adminer</c> en el bloque de localhost. Todo lo de <c>/adminer/</c> va a la entrada de
    /// Roost (<c>index.php</c>, que carga <c>adminer.php</c>; Adminer sirve también su CSS y su JS por
    /// <c>?file=</c>), así que ningún otro fichero de esa carpeta se ejecuta ni se descarga. El
    /// <c>^~</c> hace que no pase por el <c>location ~ \.php$</c> de la www.
    /// </summary>
    public static string Adminer(NginxConfInput input) => input.AdminerDirectory is not { } dir ? "" : $$"""

                    # Adminer de Roost (bin\adminer), con el PHP global. Solo atiende a localhost.
                    location = /adminer {
                        return 301 /adminer/;
                    }
                    location ^~ /adminer/ {
                        fastcgi_pass {{input.DefaultUpstream}};
                        fastcgi_param SCRIPT_FILENAME "{{Nginx(dir)}}/index.php";
                        include "{{Nginx(input.FastCgiParams)}}";
                    }

            """;

    /// <summary>nginx en Windows acepta '/' y así no hay que escapar las '\' dentro de comillas.</summary>
    public static string Nginx(string path) => path.Replace('\\', '/');
}
