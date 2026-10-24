using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Sites;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>
/// <c>roost www init</c> (Linux): crea la carpeta de proyectos compartida, <c>/var/www/roost</c>, con tu
/// usuario como dueno, una sola vez y con permiso de administrador (<c>roost-helper www-init</c>). A
/// partir de ahi Roost la usa como www sin pedir nada mas. En Windows no hace falta: la www es la de
/// la raiz de Roost.
/// </summary>
public sealed class WwwInitCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLineInterpolated($"En Windows la carpeta de proyectos es {paths.Www} y no hace falta crearla con permisos.");
            return 0;
        }

        try
        {
            if (WwwFolder.IsUsable())
            {
                AnsiConsole.MarkupLineInterpolated($"[green]{WwwFolder.Path} ya existe y puedes escribir en ella.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine(SiteSupport.ElevationNotice($"crear {WwwFolder.Path} con tu usuario como dueno"));
            WwwFolder.Init(SiteSupport.HelperExe);
            AnsiConsole.MarkupLineInterpolated($"[green]Creada[/] {WwwFolder.Path} (0755, dueno {Environment.UserName}).");
            AnsiConsole.MarkupLine("[grey]Desde ahora es la carpeta de proyectos; los sitios de antes se quedan donde estan. Para usar otra: [[paths]] www en roost.toml.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
