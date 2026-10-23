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

    /// <summary>Nombre del bloque <c>upstream</c> de nginx para una rama: <c>php-8.3</c>.</summary>
    public static string UpstreamName(ComponentVersion version) => $"php-{version.Major}.{version.Minor}";
}

/// <summary>Un bloque <c>upstream</c>: un nombre y los puertos de su pool de php-cgi.</summary>
public sealed record NginxUpstream(string Name, IReadOnlyList<int> Ports);

/// <summary>Lo que necesita <see cref="NginxConf.Render"/>; todas las rutas, absolutas.</summary>
public sealed record NginxConfInput(
    string MimeTypes,
    string FastCgiParams,
    string RunDirectory,
    string LogsDirectory,
    string Www,
    int HttpPort,
    IReadOnlyList<NginxUpstream> Upstreams,
    string DefaultUpstream,
    string SitesDirectory);

/// <summary>
/// El nginx.conf base que genera Roost (ROOST.md §5.6): un <c>upstream php-X.Y</c> por cada
/// pool, el sitio por defecto en <c>http://localhost/</c> (la carpeta www) y un
/// <c>include sites/*.conf</c> con los vhosts <c>*.test</c> (<see cref="SiteConf"/>). Escucha
/// solo en 127.0.0.1: es un entorno de desarrollo y no debe verse desde la red.
/// </summary>
public static class NginxConf
{
    /// <summary>PURO: genera el texto del nginx.conf.</summary>
    public static string Render(NginxConfInput input)
    {
        var run = Nginx(input.RunDirectory);
        var logs = Nginx(input.LogsDirectory);

        var upstreams = new StringBuilder();
        foreach (var upstream in input.Upstreams)
        {
            upstreams.Append($"    upstream {upstream.Name} {{\n");
            foreach (var port in upstream.Ports)
                upstreams.Append($"        server 127.0.0.1:{port};\n");
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
                access_log "{{logs}}/nginx-access.log";

                client_body_temp_path "{{run}}/nginx-temp/client_body";
                proxy_temp_path "{{run}}/nginx-temp/proxy";
                fastcgi_temp_path "{{run}}/nginx-temp/fastcgi";
                uwsgi_temp_path "{{run}}/nginx-temp/uwsgi";
                scgi_temp_path "{{run}}/nginx-temp/scgi";

            {{upstreams}}    server {
                    listen 127.0.0.1:{{input.HttpPort}};
                    server_name localhost;
                    root "{{Nginx(input.Www)}}";
                    index index.php index.html index.htm;
                    autoindex on;

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

    /// <summary>nginx en Windows acepta '/' y así no hay que escapar las '\' dentro de comillas.</summary>
    public static string Nginx(string path) => path.Replace('\\', '/');
}
