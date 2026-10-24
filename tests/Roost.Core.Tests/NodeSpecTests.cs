using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Node;

namespace Roost.Core.Tests;

/// <summary>
/// Lo que entiende Roost de un <c>.nvmrc</c>, un <c>.node-version</c>, <c>engines.node</c> y
/// <c>roost node install|use</c>: las versiones y rangos de npm y los alias de nvm. PURO.
/// </summary>
public class NodeSpecTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 24);

    private static ComponentVersion V(string text) => ComponentVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    private static bool Cumple(string range, string version) =>
        NodeRange.TryParse(range, out var r) ? r.Satisfies(V(version)) : throw new ArgumentException($"no se entiende: {range}");

    [Theory]
    [InlineData("22", "22.0.0", true)]
    [InlineData("22", "22.23.4", true)]
    [InlineData("22", "23.0.0", false)]
    [InlineData("v22", "22.11.0", true)]
    [InlineData("22.11", "22.11.9", true)]
    [InlineData("22.11", "22.12.0", false)]
    [InlineData("22.11.0", "22.11.0", true)]
    [InlineData("v22.11.0", "22.11.1", false)]
    [InlineData("=22.11.0", "22.11.0", true)]
    [InlineData("22.x", "22.5.0", true)]
    [InlineData("22.x.x", "21.9.9", false)]
    [InlineData("*", "18.0.0", true)]
    [InlineData("x", "26.12.1", true)]
    [InlineData("^20.11", "20.20.2", true)]
    [InlineData("^20.11", "20.10.9", false)]
    [InlineData("^20.11", "21.0.0", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("~22.1", "22.1.7", true)]
    [InlineData("~22.1", "22.2.0", false)]
    [InlineData("~22", "22.9.0", true)]
    [InlineData(">=18", "24.22.1", true)]
    [InlineData(">= 18", "17.9.1", false)]
    [InlineData(">=18 <23", "22.23.4", true)]
    [InlineData(">=18 <23", "24.22.1", false)]
    [InlineData(">18", "18.20.8", false)]
    [InlineData(">18", "19.0.0", true)]
    [InlineData(">18.2.0", "18.2.1", true)]
    [InlineData("<=22", "22.99.0", true)]
    [InlineData("<=22", "23.0.0", false)]
    [InlineData("<22.11.0", "22.10.9", true)]
    [InlineData("18 - 20", "20.20.2", true)]
    [InlineData("18 - 20", "21.0.0", false)]
    [InlineData("18.2 - 20.1.0", "18.1.9", false)]
    [InlineData("20 || 22", "22.1.0", true)]
    [InlineData("20 || 22", "24.0.0", false)]
    [InlineData("^20.9.0 || >=22", "26.12.1", true)]
    [InlineData(">=20.0.0-rc.1", "20.0.0", true)]
    public void Rangos_ComoLosDeNpm(string range, string version, bool expected) =>
        Assert.Equal(expected, Cumple(range, version));

    [Theory]
    [InlineData("hola")]
    [InlineData("22.x.3")]
    [InlineData(">=")]
    [InlineData("^^22")]
    [InlineData("22..1")]
    public void Rangos_QueNoSeEntienden_NoSeAdivinan(string range) =>
        Assert.False(NodeRange.TryParse(range, out _));

    [Fact]
    public void Exacta_SoloConUnaVersionCompleta()
    {
        Assert.Equal("22.11.0", NodeSpec.Parse("v22.11.0").Exact?.ToString());
        Assert.Null(NodeSpec.Parse("22").Exact);
        Assert.Null(NodeSpec.Parse(">=22.11.0").Exact);
        Assert.Null(NodeSpec.Parse("22.11.0 || 24").Exact);
    }

    [Fact]
    public void AliasDeNvm_LtsNombreDeLtsYLaMasNueva()
    {
        var installed = new[] { V("26.12.1"), V("24.22.1"), V("22.23.4"), V("20.20.2"), V("25.9.0") };

        // El 24-oct-2026 la 26 aún no es LTS (lo es el 28): lts/* es la 24.
        Assert.Equal(V("24.22.1"), NodeSpec.Parse("lts/*").Pick(installed, Hoy));
        Assert.Equal(V("26.12.1"), NodeSpec.Parse("lts/*").Pick(installed, new DateOnly(2026, 10, 28)));
        Assert.Equal(V("24.22.1"), NodeSpec.Parse("LTS").Pick(installed, Hoy));
        Assert.Equal(V("22.23.4"), NodeSpec.Parse("lts/jod").Pick(installed, Hoy));
        Assert.Equal(V("20.20.2"), NodeSpec.Parse("lts/Iron").Pick(installed, Hoy));
        Assert.Equal(V("22.23.4"), NodeSpec.Parse("lts/-1").Pick(installed, Hoy));
        Assert.Equal(V("24.22.1"), NodeSpec.Parse("lts/-0").Pick(installed, Hoy));
        Assert.Equal(V("26.12.1"), NodeSpec.Parse("node").Pick(installed, Hoy));
        Assert.Equal(V("26.12.1"), NodeSpec.Parse("latest").Pick(installed, Hoy));
        Assert.Null(NodeSpec.Parse("lts/hydrogen").Pick(installed, Hoy));   // la 18 no está
        Assert.Equal(NodeSpecKind.System, NodeSpec.Parse("system").Kind);
        Assert.Null(NodeSpec.Parse("system").Pick(installed, Hoy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lts/argonauta")]
    [InlineData("iojs-v3")]
    [InlineData("ultima")]
    public void LoQueNoEsUnaVersion_DaUnErrorQueLoDice(string text)
    {
        Assert.False(NodeSpec.TryParse(text, out _, out var error));
        Assert.NotNull(error);
        Assert.Throws<RoostException>(() => NodeSpec.Parse(text));
    }

    [Fact]
    public void Calendario_LtsMantenimientoYEol()
    {
        Assert.Equal(NodeBranchStatus.MaintenanceLts, NodeCatalog.Status(24, Hoy));   // desde el 20-oct-2026
        Assert.Equal(NodeBranchStatus.MaintenanceLts, NodeCatalog.Status(22, Hoy));
        Assert.Equal(NodeBranchStatus.Current, NodeCatalog.Status(26, Hoy));
        Assert.Equal(NodeBranchStatus.ActiveLts, NodeCatalog.Status(26, new DateOnly(2026, 11, 1)));
        Assert.Equal(NodeBranchStatus.Eol, NodeCatalog.Status(20, Hoy));
        Assert.Equal(NodeBranchStatus.Eol, NodeCatalog.Status(25, Hoy));
        Assert.Null(NodeCatalog.Status(28, Hoy));
        Assert.True(NodeCatalog.IsLts(28, Hoy));    // una par que esta Roost no conoce
        Assert.False(NodeCatalog.IsLts(27, Hoy));
        Assert.Equal("EOL desde el 2026-04-30: sin parches de seguridad", NodeCatalog.StatusText(20, Hoy));
        Assert.Equal("LTS (Jod), en mantenimiento hasta el 2027-04-30", NodeCatalog.StatusText(22, Hoy));
        Assert.Equal("Current: pasa a LTS el 2026-10-28", NodeCatalog.StatusText(26, Hoy));
    }
}
