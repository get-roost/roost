using System.Text.Json;
using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Core.Tests;

/// <summary>Testea el informe de la auditoría (el JSON de roost audit y el Markdown de la app) con el dataset real.</summary>
public class AuditReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    private static DetectedComponent Php(string version, string source) => new()
    {
        Kind = ComponentKind.Php,
        Version = ComponentVersion.TryParse(version, out var v) ? v : throw new ArgumentException(version),
        ExecutablePath = $@"C:\{source}\php-{version}\php.exe",
        Source = source,
    };

    private static IReadOnlyList<AuditFinding> Findings()
    {
        var evaluator = new AuditEvaluator(AuditDataset.LoadEmbedded(), Today);
        return evaluator.EvaluateAll([Php("8.4.27", "Roost"), Php("8.3.18", "Laragon"), Php("7.4.33", "Roost")]).ToList();
    }

    [Fact]
    public void Ordered_PonePrimeroLoRojo()
    {
        var ordered = AuditReport.Ordered(Findings()).ToList();

        Assert.Equal(AuditColor.Red, ordered[0].Color);
        Assert.Equal(AuditColor.Green, ordered[^1].Color);
    }

    [Fact]
    public void ToJson_TraeElResumenYLosAvisos()
    {
        var json = AuditReport.ToJson(AuditDataset.LoadEmbedded(), Findings(), new JsonSerializerOptions());

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("blocking").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("summary").GetProperty("red").GetInt32());
        var first = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("Php", first.GetProperty("component").GetProperty("kind").GetString());
    }

    [Fact]
    public void ToMarkdown_TablaConTodo_YDetalleDeLoQueNoEstaEnVerde()
    {
        var md = AuditReport.ToMarkdown(AuditDataset.LoadEmbedded(), Findings(), new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero), "PORTATIL");

        Assert.StartsWith("# Auditoria de Roost: PORTATIL", md);
        Assert.Contains("**2 en rojo** · 0 en ambar · 1 en verde", md);
        Assert.Contains("| Php 8.3.18 | Laragon | VULNERABLE |", md);
        Assert.Contains("## Php 8.3.18 (Laragon): VULNERABLE", md);
        Assert.DoesNotContain("## Php 8.4.27", md);   // en verde: solo en la tabla
    }

    [Fact]
    public void Merge_SumaAvisos_SustituyeLosDeMismoId_YMezclaRamas()
    {
        var mine = AuditDataset.LoadEmbedded();
        var extra = new AuditDataset
        {
            Source = "feed ampliado",
            Generated = mine.Generated.AddDays(1),
            Products =
            {
                ["php"] = new ProductInfo { Branches = { ["8.4"] = new BranchInfo { Latest = "8.4.28", Status = "active" } } },
                ["redis"] = new ProductInfo { DisplayName = "Redis" },
            },
            Advisories =
            [
                new Advisory { Id = "CVE-2026-109618", Product = "php", Title = "sustituido", Affected = ["<8.0.0"] },
                new Advisory { Id = "EXTRA-1", Product = "php", Title = "nuevo" },
            ],
        };

        var merged = mine.Merge(extra);

        Assert.Equal(mine.Advisories.Count + 1, merged.Advisories.Count);
        Assert.Equal("sustituido", merged.Advisories.Single(a => a.Id == "CVE-2026-109618").Title);
        Assert.Equal("8.4.28", merged.Products["php"].Branches["8.4"].Latest);
        Assert.Equal(mine.Products["php"].Branches["8.3"].Latest, merged.Products["php"].Branches["8.3"].Latest);
        Assert.True(merged.Products.ContainsKey("redis"));
        Assert.Equal(extra.Generated, merged.Generated);
        Assert.Equal("8.4.27", mine.Products["php"].Branches["8.4"].Latest);   // el original no cambia
    }
}
