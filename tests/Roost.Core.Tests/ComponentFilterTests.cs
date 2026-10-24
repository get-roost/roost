using System.Text.Json;
using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Scanning;

namespace Roost.Core.Tests;

/// <summary><c>--only</c> de <c>roost audit</c> y <c>roost scan</c>: qué se acepta y qué queda.</summary>
public class ComponentFilterTests
{
    private static DetectedComponent Component(ComponentKind kind, string version) => new()
    {
        Kind = kind,
        Version = ComponentVersion.TryParse(version, out var v) ? v : default,
        ExecutablePath = $"/usr/bin/{kind}",
        Source = "Roost",
    };

    [Theory]
    [InlineData("php", "php")]
    [InlineData("PHP", "php")]
    [InlineData("mariadb,mysql", "mysql,mariadb")]        // en el orden de siempre
    [InlineData(" php , mysql ,", "php,mysql")]
    [InlineData("php,php", "php")]
    [InlineData("Apache,nginx,php", "php,nginx,apache")]   // K2
    public void SeAceptan_SinDistinguirMayusculas_YConEspacios(string text, string keys)
    {
        Assert.True(ComponentFilter.TryParse(text, out var filter, out var error));
        Assert.Null(error);
        Assert.Equal(keys.Split(','), filter!.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void Vacio_EsSinFiltro(string? text)
    {
        Assert.True(ComponentFilter.TryParse(text, out var filter, out _));
        Assert.Null(filter);
    }

    [Fact]
    public void UnTipoQueNoExiste_EsUnErrorQueLoNombra()
    {
        Assert.False(ComponentFilter.TryParse("php,postgres,redis", out var filter, out var error));
        Assert.Null(filter);
        Assert.Contains("postgres, redis", error);
        Assert.Contains("php, mysql, mariadb, nginx, apache", error);
    }

    [Fact]
    public void ConSoloPhp_ElMySqlEolDeLaImagenNoBloquea()
    {
        // La imagen windows-latest de GitHub trae MySQL 8.0, que ya es EOL.
        var found = new[] { Component(ComponentKind.Php, "8.4.27"), Component(ComponentKind.MySql, "8.0.46") };
        Assert.True(ComponentFilter.TryParse("php", out var only, out _));

        var kept = only!.Apply(found);
        var findings = new AuditEvaluator(AuditDataset.LoadEmbedded(), new DateOnly(2026, 10, 22)).EvaluateAll(kept).ToList();

        Assert.Equal(ComponentKind.Php, Assert.Single(kept).Kind);
        Assert.DoesNotContain(findings, f => f.IsBlocking);
        using var json = JsonDocument.Parse(AuditReport.ToJson(AuditDataset.LoadEmbedded(), findings, new JsonSerializerOptions(), only: only, skipped: 1));
        Assert.Equal("php", json.RootElement.GetProperty("only")[0].GetString());
        Assert.Equal(1, json.RootElement.GetProperty("skipped").GetInt32());
        Assert.False(json.RootElement.GetProperty("blocking").GetBoolean());
    }
}
