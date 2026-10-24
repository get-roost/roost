using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Sites;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>
/// <c>roost cert path &lt;sitio&gt;</c>: donde estan el certificado y la clave de un sitio (y la CA),
/// para servir en HTTPS otra cosa con el mismo nombre: el servidor de desarrollo de Vite
/// (<c>server.https</c>), webpack o un <c>php -S</c> detras de un proxy. Si aun no se ha emitido, lo
/// emite (lo mismo que haria roost up; no pide permisos). La ruta no cambia al renovarlo.
/// </summary>
public sealed class CertPathCommand : Command<CertPathCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SITIO>")]
        [Description("Nombre del sitio (el de <nombre>.test).")]
        public string Name { get; init; } = "";

        [CommandOption("--json")]
        [Description("Salida JSON para scripts (vite.config.js, un Makefile...).")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var site = new SiteManager(paths).Find(settings.Name);
            if (site is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No hay ningun sitio '{settings.Name}'.[/] Mira los registrados con: roost site list");
                return 1;
            }

            var ca = new LocalCa(paths);
            var existed = File.Exists(ca.SiteCertFile(site.Name)) && File.Exists(ca.SiteKeyFile(site.Name));
            var (certFile, keyFile) = ca.EnsureSiteCert(site);
            DateTime expires;
            using (var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certFile)))
                expires = cert.NotAfter;

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    site = site.Name,
                    host = site.Host,
                    cert = certFile,
                    key = keyFile,
                    ca = ca.CaCertFile,
                    names = new[] { site.Host, "*." + site.Host },
                    expires = expires.ToUniversalTime().ToString("yyyy-MM-dd"),
                    issuedNow = !existed,
                    caTrusted = ca.IsTrusted(),
                }, JsonOutput.Options));
                return 0;
            }

            if (!existed) AnsiConsole.MarkupLineInterpolated($"[green]Emitido ahora[/] el certificado de {site.Host}.");
            AnsiConsole.MarkupLineInterpolated($"[bold]Certificado:[/] {certFile}");
            AnsiConsole.MarkupLineInterpolated($"[bold]Clave:[/]       {keyFile}");
            AnsiConsole.MarkupLineInterpolated($"[bold]CA:[/]          {ca.CaCertFile}");
            AnsiConsole.MarkupLineInterpolated($"[grey]Vale para {site.Host} y *.{site.Host} hasta {expires:yyyy-MM-dd}. roost up lo renueva solo antes de caducar, en la misma ruta: reinicia el servidor de desarrollo para que lo vuelva a leer.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]La clave solo la puede leer tu usuario; no la copies a otro sitio ni la subas al repositorio.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Vite: server.https con esta key y este cert, server.host y server.hmr.host = '{site.Host}' (la guia esta en el README).[/]");
            SiteSupport.TrustHint(ca);
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
