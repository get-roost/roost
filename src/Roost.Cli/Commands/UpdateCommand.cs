using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Updates;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost update</c>: pregunta ya a GitHub (sin mirar la cache) si hay una Roost nueva, y dice
/// cual tienes, cual es la ultima, sus novedades en corto y como actualizar segun como la
/// instalaste. Con <c>--install</c>, si Roost se instalo con el instalador de Windows, hace lo
/// mismo que la app: descarga el setup nuevo, verifica su SHA-256 con el SHA256SUMS.txt de la
/// release (y su firma, si lleva) y lo lanza en silencio; roost sale para dejarle sustituir sus
/// ficheros. Codigo de salida: 0 si se pudo comprobar (o instalar), 1 si no.
/// </summary>
public sealed class UpdateCommand : AsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }

        [CommandOption("--install")]
        [Description("Si hay una nueva y Roost se instalo con el instalador de Windows: la descarga, la verifica y lanza el instalador (silencioso, un solo permiso de administrador).")]
        public bool Install { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var checker = new UpdateChecker(RoostPaths.Default(), RoostHttp.Client);
        UpdateCheck check = null!;
        if (settings.Json)
            check = await checker.CheckAsync(force: true, cancellation);
        else
            await AnsiConsole.Status().StartAsync("Buscando la ultima version de Roost...", async _ =>
                check = await checker.CheckAsync(force: true, cancellation));

        var channel = UpdateInstructions.DetectCurrent();
        var advice = UpdateInstructions.For(channel, check.Latest, OperatingSystem.IsWindows());
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(Json(check, advice), JsonOutput.Options));
            return check.Status == UpdateStatus.Failed ? 1 : 0;
        }

        Write(check, advice);
        if (check.Status == UpdateStatus.Failed) return 1;
        return settings.Install ? await InstallAsync(check, channel, advice, cancellation) : 0;
    }

    /// <summary><c>--install</c>: lo mismo que «Actualizar ahora» en la app, para el canal del instalador.</summary>
    private static async Task<int> InstallAsync(UpdateCheck check, InstallChannel channel, UpdateAdvice advice, CancellationToken cancellation)
    {
        if (!check.UpdateAvailable || check.Latest is not { } latest) return 0;
        AnsiConsole.WriteLine();
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[red]roost update --install solo funciona en Windows:[/] en Linux no hay instalador todavia.");
            return 1;
        }
        if (channel != InstallChannel.Installer)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]roost update --install solo vale para la Roost del instalador de Windows.[/] {advice.Text}.");
            return 1;
        }
        if (latest.Installer is not { } setup)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]La release {latest.Name} no trae el instalador de Windows:[/] mira que ha pasado en {latest.Page}");
            return 1;
        }
        if (Process.GetProcessesByName("roost-desktop").Length > 0)
        {
            AnsiConsole.MarkupLine("[yellow]La app de Roost esta abierta.[/] Actualiza desde ella (el aviso de la barra lateral: Actualizar ahora), "
                + "o cierrala del todo (clic derecho en el icono de la bandeja > Salir de Roost) y repite.");
            return 1;
        }

        var paths = RoostPaths.Default();
        var installer = new UpdateInstaller(paths, RoostHttp.Client);
        try
        {
            using var daemon = new DaemonClient(paths);
            var running = await daemon.TryStatusAsync(cancellation) is { Running: true };

            VerifiedDownload download = null!;
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Descargando {setup.Name}", maxValue: setup.Size > 0 ? setup.Size : 1);
                    var expected = await installer.ExpectedSha256Async(latest, setup, cancellation);
                    download = await installer.DownloadAsync(setup, expected, new Progress<long>(b => task.Value = Math.Min(b, task.MaxValue)), cancellation);
                    task.Value = task.MaxValue;
                });
            var signature = download.Signature switch
            {
                SignatureStatus.Valid => "firma digital valida",
                SignatureStatus.Unsigned => "sin firma digital (Roost aun no tiene certificado)",
                _ => "firma no comprobada",
            };
            AnsiConsole.MarkupLineInterpolated($"[green]Verificado[/] {setup.Name}: SHA-256 el de SHA256SUMS.txt, {signature}.");

            Directory.CreateDirectory(paths.Logs);
            UpdateMarker.Write(paths, new UpdateMarker(check.Installed, latest.Version.ToString(), running, DateTimeOffset.Now, installer.SetupLogFile));
            var pid = UpdateLauncher.Default.StartSetup(download.Path, UpdateInstaller.SetupArguments(relaunchApp: false, startStack: running, installer.SetupLogFile));

            AnsiConsole.MarkupLineInterpolated($"[bold]Instalador en marcha[/] (pid {pid}): Windows pedira permiso de administrador una vez.");
            AnsiConsole.MarkupLineInterpolated($"  roost sale ahora para que pueda sustituir sus ficheros{(running ? "; el stack se para y el instalador lo vuelve a arrancar al terminar" : "")}.");
            AnsiConsole.MarkupLineInterpolated($"  Cuando acabe, roost --version dira {latest.Version}. Registro del instalador: {installer.SetupLogFile}");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    private static void Write(UpdateCheck check, UpdateAdvice advice)
    {
        var latest = check.Latest;
        switch (check.Status)
        {
            case UpdateStatus.Available:
                AnsiConsole.MarkupLineInterpolated($"[yellow]Hay una Roost nueva:[/] [bold]{latest!.Version}[/] [grey](tienes la {check.Installed})[/]");
                break;
            case UpdateStatus.UpToDate when check.InstalledIsNewer:
                AnsiConsole.MarkupLineInterpolated($"[green]Roost {check.Installed} es mas nueva que la ultima publicada[/] ({latest!.Version}): es una build de desarrollo.");
                break;
            case UpdateStatus.UpToDate:
                AnsiConsole.MarkupLineInterpolated($"[green]Roost {check.Installed} esta al dia:[/] es la ultima publicada.");
                break;
            case UpdateStatus.NoReleases:
                AnsiConsole.MarkupLineInterpolated($"Aun no hay ninguna version de Roost publicada en GitHub [grey](tienes la {check.Installed})[/].");
                return;
            default:
                AnsiConsole.MarkupLineInterpolated($"[red]No se pudo comprobar si hay una Roost nueva:[/] {check.Error}");
                if (latest is null) return;
                AnsiConsole.MarkupLineInterpolated($"[grey]La ultima que se conocia ({Date(check.CheckedAt)}) es la {latest.Version}; tienes la {check.Installed}.[/]");
                if (!check.UpdateAvailable) return;
                break;
        }
        if (latest is null) return;

        AnsiConsole.MarkupLineInterpolated($"  {latest.Name}{(latest.PublishedAt is { } at ? $", del {Date(at)}" : "")} [grey]· {latest.Page}[/]");
        if (!check.UpdateAvailable) return;

        var notes = GitHubReleases.Summarize(latest.Notes);
        if (notes.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Novedades[/]");
            foreach (var line in notes) AnsiConsole.MarkupLineInterpolated($"  {line}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Para actualizar[/]");
        AnsiConsole.MarkupLineInterpolated($"  {advice.Text}");
        if (advice.Channel == InstallChannel.Installer)
            AnsiConsole.MarkupLine("  [grey]Con roost update --install (o Actualizar ahora en la app), el instalador para el stack, actualiza y lo vuelve a arrancar: tus datos y tus sitios no se tocan.[/]");
        else
            AnsiConsole.MarkupLine("  [grey]Antes, cierra la app y para el stack (roost down): mientras corren, los ficheros de Roost estan en uso.[/]");
    }

    private static string Date(DateTimeOffset? at) =>
        at is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "?";

    private static object Json(UpdateCheck check, UpdateAdvice advice) => new
    {
        installed = check.Installed,
        status = check.Status switch
        {
            UpdateStatus.UpToDate => "up-to-date",
            UpdateStatus.Available => "available",
            UpdateStatus.NoReleases => "no-releases",
            _ => "failed",
        },
        updateAvailable = check.UpdateAvailable,
        latest = check.Latest is { } l
            ? new
            {
                version = l.Version.ToString(),
                tag = l.Tag,
                name = l.Name,
                url = l.Page.ToString(),
                publishedAt = l.PublishedAt,
                notes = l.Notes,
                assets = l.Assets.Select(a => new { name = a.Name, url = a.Url.ToString(), size = a.Size }),
            }
            : null,
        checkedAt = check.CheckedAt,
        fromCache = check.FromCache,
        error = check.Error,
        howToUpdate = new
        {
            channel = advice.Channel.ToString().ToLowerInvariant(),
            command = advice.Command,
            url = advice.Url?.ToString(),
            package = advice.Package,
            text = advice.Text,
            canInstall = advice.Channel == InstallChannel.Installer && check.Latest?.Installer is not null,
        },
    };
}

/// <summary>
/// La linea de <c>roost status</c> cuando hay una Roost nueva: solo con lo que diga la cache
/// (la deja <c>roost update</c> o la app), sin ir a la red. Nada con <c>ROOST_NO_UPDATE_CHECK</c>.
/// </summary>
internal static class UpdateNotice
{
    public static void FromCache(RoostPaths paths)
    {
        if (UpdateChecker.AutomaticChecksOff) return;
        var cached = new UpdateChecker(paths, RoostHttp.Client).Cached();
        if (cached is not { UpdateAvailable: true, Latest: { } latest }) return;
        AnsiConsole.MarkupLineInterpolated($"[yellow]Hay una Roost nueva: {latest.Version}[/] [grey](tienes la {cached.Installed}). Como actualizar: roost update[/]");
    }
}
