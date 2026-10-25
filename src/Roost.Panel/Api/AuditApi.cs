using Microsoft.AspNetCore.Builder;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;

namespace Roost.WebPanel.Api;

/// <summary>
/// Auditoría: escanear la máquina (una tarea larga) y evaluar cada componente contra el feed, como
/// <c>roost audit</c>; los filtros los pone la página. «Exportar» da el mismo Markdown o JSON que
/// <c>roost audit --json</c>. Lo ignorado va en <c>state/panel-audit-ignored.json</c>.
/// </summary>
public static class AuditApi
{
    public sealed record IgnoreRequest(string Key, bool Ignore);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/audit", () => PanelRoutes.Guard(() => PanelRoutes.Ok(Get(app))));
        api.MapPost("/audit/scan", (bool? feed) => PanelRoutes.Guard(() => PanelRoutes.Started(Scan(app, feed == true))));
        api.MapPost("/audit/ignore", (IgnoreRequest request) => PanelRoutes.Guard(() =>
        {
            var ignored = Ignored(app).ToHashSet(StringComparer.Ordinal);
            if (request.Ignore) ignored.Add(request.Key); else ignored.Remove(request.Key);
            Directory.CreateDirectory(app.Paths.State);
            File.WriteAllText(IgnoredFile(app), JsonSerializer.Serialize(ignored.Order().ToList()));
            return PanelRoutes.Ok(Get(app));
        }));
        api.MapGet("/audit/export", (string? format) => PanelRoutes.Guard(() =>
        {
            var scan = app.LastScan ?? throw new RoostException("Aun no se ha escaneado.");
            var (dataset, origin) = app.Dataset ?? app.LoadDataset();
            var ordered = AuditReport.Ordered(scan.Findings).ToList();
            if (format == "json")
                return Results.Text(AuditReport.ToJson(dataset, ordered, PanelApp.JsonOptions, origin), "application/json; charset=utf-8");
            return Results.Text(AuditReport.ToMarkdown(dataset, ordered, scan.When, Environment.MachineName, origin), "text/markdown; charset=utf-8");
        }));
    }

    private static string IgnoredFile(PanelApp app) => Path.Combine(app.Paths.State, "panel-audit-ignored.json");

    private static IReadOnlyList<string> Ignored(PanelApp app)
    {
        try
        {
            var file = IgnoredFile(app);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }

    public static string KeyOf(AuditFinding f) => $"{f.Component.Kind}|{f.Component.Version}|{f.Component.ExecutablePath}";

    public static object Get(PanelApp app)
    {
        var (dataset, origin) = app.Dataset ?? app.LoadDataset();
        var scan = app.LastScan;
        var ignored = Ignored(app);
        var roostPhp = new PhpInstaller(app.Paths).ListInstalled();
        var findings = scan is null ? [] : AuditReport.Ordered(scan.Findings).Select(f =>
        {
            var key = KeyOf(f);
            var isRoost = f.Component.Source == "Roost";
            // La acción que toca: un PHP de Roost con una versión más nueva en su rama → «Instalar X».
            string? action = null;
            if (isRoost && f.Component.Kind == ComponentKind.Php && f.BranchLatest is { } latest && ComponentVersion.TryParse(latest, out var lv) && lv > f.Component.Version && f.Color != AuditColor.Green)
                action = latest;
            return new
            {
                key, kind = StackApi.Kind(f.Component.Kind), version = f.Component.Version.ToString(), source = f.Component.Source, path = f.Component.ExecutablePath,
                verdict = f.Verdict.ToString(), chip = StackApi.Chip(f), tone = StackApi.Tone(f.Color), color = f.Color.ToString().ToLowerInvariant(),
                headline = f.Headline, eol = f.Eol, branchLatest = f.BranchLatest, uncertain = f.Uncertain, mitigated = f.Mitigated, isRoost,
                ignored = ignored.Contains(key), action, notes = f.Notes,
                advisories = f.MatchedAdvisories.Select(a => new { id = a.Id, title = a.Title, cvss = a.Cvss, severity = a.Severity, kev = a.Kev, url = a.Refs.FirstOrDefault() ?? $"https://github.com/advisories?query={Uri.EscapeDataString(a.Id)}", fixedIn = a.FixedIn }).ToList(),
                mitigations = AuditReport.DistinctMitigations(f).Select(m => new { ini = m.Ini, value = m.Value, note = m.Note, status = f.StateOf(m).StatusLabel }).ToList(),
            };
        }).ToList();
        var counted = findings.Where(f => !f.ignored).ToList();
        return new
        {
            scanned = scan is not null, when = scan?.When, os = scan?.OsDescription,
            red = counted.Count(f => f.color == "red"), amber = counted.Count(f => f.color == "amber"), green = counted.Count(f => f.color == "green"),
            dataset = new { generated = dataset.Generated, origin = origin.Kind.ToString(), originText = origin.Kind == DatasetKind.Feed ? "Feed firmado · en local" : "Incluidos en Roost · en local", note = origin.Note },
            findings, kinds = findings.Select(f => f.kind).Distinct().Order().ToList(), sources = findings.Select(f => f.source).Distinct().Order().ToList(),
            jobs = app.Jobs.Running().Where(j => j.Title.StartsWith("Escaneando", StringComparison.Ordinal)).ToList(),
        };
    }

    private static Job Scan(PanelApp app, bool updateFeed) =>
        app.Jobs.Start("Escaneando este equipo", "PHP, MySQL, MariaDB, nginx y Apache, de Roost o no", [("Buscar instalaciones", null), ("Evaluar contra los avisos", null)], async ctx =>
        {
            if (updateFeed)
            {
                ctx.Message("Comprobando el feed de avisos…");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await app.Feed.UpdateAsync(cancellation: timeout.Token);
                }
                catch (OperationCanceledException) when (!ctx.Cancellation.IsCancellationRequested) { }
                app.LoadDataset();
            }
            ctx.Begin(0, "Buscando instalaciones…");
            var result = await Task.Run(() => EnvironmentScanner.CreateDefault().ScanAll(), ctx.Cancellation);
            ctx.Begin(1, "Evaluando…");
            var findings = app.Evaluator.EvaluateAll(result.Components).ToList();
            app.LastScan = new AuditScan(DateTimeOffset.Now, result.OsDescription, findings);
            ctx.Finish($"{findings.Count} componentes.");
            return new { count = findings.Count };
        }, canCancel: false);
}
