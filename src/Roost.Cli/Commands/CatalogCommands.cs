using Roost.Cli.Net;
using Roost.Core.Audit;
using Roost.Core.Platform;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>Lo comun del feed de avisos para la CLI: el cliente con el HttpClient de Roost y la carga para evaluar.</summary>
internal static class CliFeed
{
    public static AdvisoryFeed Create(RoostPaths paths) => new(paths, RoostHttp.Client);

    /// <summary>
    /// Los avisos con los que evaluar. Con <paramref name="update"/>, antes pregunta por un feed nuevo
    /// (si el de la cache tiene mas de 24 h), con un limite de 15 s; si no se puede, sigue con lo que hay
    /// y lo dice en la nota.
    /// </summary>
    public static async Task<(AuditDataset Dataset, DatasetOrigin Origin)> LoadAsync(RoostPaths paths, bool update, CancellationToken cancellation)
    {
        var feed = Create(paths);
        FeedUpdate? result = null;
        if (update)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            result = await feed.UpdateAsync(cancellation: timeout.Token);
        }
        var (dataset, origin) = feed.LoadBest();
        if (result is { Ok: false, Detail: { } detail })
            origin = origin with { Note = origin.Note is null ? detail : $"{origin.Note} ({detail})" };
        return (dataset, origin);
    }
}

/// <summary><c>roost catalog status</c>: que feed hay en la cache, cual va empaquetado y cual se usa.</summary>
public sealed class CatalogStatusCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var feed = CliFeed.Create(RoostPaths.Default());
        var now = DateTimeOffset.UtcNow;
        var embedded = AuditDataset.LoadEmbedded();
        var cached = feed.LoadCached(out var problem);
        var (dataset, origin) = feed.LoadBest(embedded);

        AnsiConsole.MarkupLineInterpolated($"Feed: {feed.Url}");
        if (cached is null)
            AnsiConsole.MarkupLineInterpolated($"En cache: [yellow]{problem}[/]");
        else
        {
            var checkedAt = feed.LastChecked is { } at ? $", comprobado el {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "";
            AnsiConsole.MarkupLineInterpolated($"En cache: [green]{new DatasetOrigin(DatasetKind.Feed).Describe(cached, now)}[/]{checkedAt}");
        }
        AnsiConsole.MarkupLineInterpolated($"Empaquetado: {new DatasetOrigin(DatasetKind.Embedded).Describe(embedded, now)}");
        AnsiConsole.MarkupLineInterpolated($"Se usa: [bold]{origin.Describe(dataset, now)}[/] ({dataset.Advisories.Count} avisos)");
        return 0;
    }
}

/// <summary><c>roost catalog update</c>: pregunta ya por un feed nuevo, aunque el de la cache sea de hoy.</summary>
public sealed class CatalogUpdateCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var feed = CliFeed.Create(RoostPaths.Default());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await feed.UpdateAsync(force: true, cancellation: timeout.Token);
        var (dataset, origin) = feed.LoadBest();
        var now = DateTimeOffset.UtcNow;
        switch (result.Status)
        {
            case FeedUpdateStatus.Updated:
                AnsiConsole.MarkupLineInterpolated($"[green]Feed actualizado:[/] {origin.Describe(dataset, now)}, {dataset.Advisories.Count} avisos.");
                return 0;
            case FeedUpdateStatus.NotModified:
                AnsiConsole.MarkupLineInterpolated($"[green]Ya estaba al dia:[/] {origin.Describe(dataset, now)}.");
                return 0;
            default:
                AnsiConsole.MarkupLineInterpolated($"[red]No se pudo actualizar:[/] {result.Detail}");
                AnsiConsole.MarkupLineInterpolated($"Se sigue usando: {origin.Describe(dataset, now)}.");
                return 1;
        }
    }
}
