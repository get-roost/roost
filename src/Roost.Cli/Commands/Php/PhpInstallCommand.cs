using System.ComponentModel;
using Roost.Core.Audit;
using Roost.Cli.Net;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// <c>roost php install</c>: descarga un PHP oficial de windows.php.net, verifica su SHA-256
/// e instala en <c>bin/php/&lt;version&gt;-&lt;nts|ts&gt;-&lt;arq&gt;</c>. Avisa si la rama ya
/// es EOL (instalar una 7.4 para un proyecto viejo es legitimo, pero que se sepa).
/// </summary>
public sealed class PhpInstallCommand : AsyncCommand<PhpInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VERSION>")]
        [Description("Rama (8.3: la ultima de esa rama) o version exacta (8.3.36).")]
        public string Version { get; init; } = "";

        [CommandOption("--ts")]
        [Description("Build thread-safe (para Apache/mod_php). Por defecto NTS, el que usa nginx.")]
        public bool ThreadSafe { get; init; }

        [CommandOption("--arch <ARQ>")]
        [Description("Arquitectura: x64 (por defecto) o x86.")]
        [DefaultValue("x64")]
        public string Architecture { get; init; } = "x64";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost php install solo funciona en Windows por ahora:[/] en Linux no hay "
                + "binarios oficiales de PHP y los builds propios aun no existen (ROOST.md seccion 5.4).");
            return 1;
        }

        var installer = new PhpInstaller(RoostPaths.Default(), RoostHttp.Client);
        try
        {
            var catalog = await WindowsPhpCatalog.FetchAsync(RoostHttp.Client, cancellation);
            var build = WindowsPhpCatalog.Resolve(catalog, settings.Version, settings.ThreadSafe, settings.Architecture);

            if (installer.IsInstalled(build))
            {
                AnsiConsole.MarkupLineInterpolated($"[green]PHP {build.InstallName} ya esta instalado[/] en {installer.InstallDirectory(build)}.");
                return 0;
            }

            var finding = CatalogAudit.Evaluate(build.Version, build.FileName, "windows.php.net");
            if (finding.Color != AuditColor.Green)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Ojo:[/] {finding.Headline}");
            // Sin el VC++ Redistributable, php.exe no arranca: mejor saberlo antes de descargar.
            if (VcRedist.Warning(VcRedist.Detect(), forApache: false) is { } vc)
                AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {vc}");

            AnsiConsole.MarkupLineInterpolated($"Descargando [bold]{build.FileName}[/] ({build.SizeText ?? "?"}) de {build.Url.Host}");
            InstalledPhp? installed = null;
            await AnsiConsole.Status().StartAsync($"Instalando PHP {build.InstallName}...", async ctx =>
            {
                var progress = new Progress<long>(bytes =>
                    ctx.Status($"Instalando PHP {build.InstallName}... {bytes / 1048576.0:0.0} MB descargados"));
                installed = await installer.InstallAsync(build, progress, cancellation);
            });

            AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] PHP {installed!.Name} en {installed.Directory} [grey](SHA-256 verificado)[/]");
            SmokeTest(installed, build);
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo leer el catalogo de windows.php.net:[/] {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Arranca el php.exe recien instalado. Si no responde, casi siempre es que falta el
    /// Visual C++ Redistributable con el que se compilo (vc15/vs16/vs17: el 2015-2022 vale para todos).
    /// </summary>
    private static void SmokeTest(InstalledPhp installed, PhpBuild build)
    {
        var output = ProcessRunner.Default.Run(installed.Executable, "-v", TimeSpan.FromSeconds(15));
        var firstLine = output?.Split('\n').FirstOrDefault(l => l.StartsWith("PHP ", StringComparison.Ordinal))?.Trim();
        if (firstLine is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]{firstLine}[/]");
            return;
        }

        var redist = $"https://aka.ms/vc14/vc_redist.{build.Architecture}.exe";
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]Aviso:[/] {installed.Executable} no responde a -v. Suele faltar el Visual C++ Redistributable ({build.Compiler}, {build.Architecture}): {redist}");
    }
}
