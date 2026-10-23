using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core.Platform;
using Roost.Core.Updates;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost update</c>: pregunta ya a GitHub (sin mirar la cache) si hay una Roost nueva, y dice
/// cual tienes, cual es la ultima, sus novedades en corto y como actualizar segun como la
/// instalaste. No descarga ni cambia nada. Codigo de salida: 0 si se pudo comprobar, 1 si no.
/// </summary>
public sealed class UpdateCommand : AsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
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

        var advice = UpdateInstructions.For(UpdateInstructions.DetectCurrent(), check.Latest, OperatingSystem.IsWindows());
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(Json(check, advice), JsonOutput.Options));
            return check.Status == UpdateStatus.Failed ? 1 : 0;
        }

        Write(check, advice);
        return check.Status == UpdateStatus.Failed ? 1 : 0;
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
        if (UpdateChecker.DisabledByEnvironment) return;
        var cached = new UpdateChecker(paths, RoostHttp.Client).Cached();
        if (cached is not { UpdateAvailable: true, Latest: { } latest }) return;
        AnsiConsole.MarkupLineInterpolated($"[yellow]Hay una Roost nueva: {latest.Version}[/] [grey](tienes la {cached.Installed}). Como actualizar: roost update[/]");
    }
}
