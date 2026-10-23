using Roost.Core.Updates;

namespace Roost.Core.Tests;

/// <summary>La comparación de versiones de Roost: la precedencia de SemVer 2.0, prereleases incluidas.</summary>
public class SemanticVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.1.9", "0.1.10")]
    [InlineData("1.9.9", "2.0.0")]
    // La cadena del ejemplo de semver.org: cada una precede a la siguiente.
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.2.0-beta.1", "0.2.0")]
    [InlineData("0.1.0", "0.2.0-beta.1")]
    public void Compara_SegunSemVer(string menor, string mayor)
    {
        var a = SemanticVersion.Parse(menor);
        var b = SemanticVersion.Parse(mayor);
        Assert.True(a < b, $"{menor} < {mayor}");
        Assert.True(b > a, $"{mayor} > {menor}");
        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("V0.2.0-beta.1", "0.2.0-beta.1")]
    [InlineData("0.1.0+918291e5c2a1", "0.1.0")]
    [InlineData("0.2.0-rc.1+build.7", "0.2.0-rc.1")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(" 3 ", "3.0.0")]
    public void Parse_AceptaLaVDeLaEtiqueta_YQuitaLaMetadata(string texto, string esperada)
    {
        Assert.True(SemanticVersion.TryParse(texto, out var version));
        Assert.Equal(esperada, version.ToString());
        Assert.Equal(SemanticVersion.Parse(esperada), version);
    }

    [Fact]
    public void LaMetadataDeBuild_NoCuentaAlComparar()
    {
        Assert.Equal(0, SemanticVersion.Parse("0.1.0+aaa").CompareTo(SemanticVersion.Parse("0.1.0+bbb")));
        Assert.False(SemanticVersion.Parse("0.1.0+aaa") < SemanticVersion.Parse("0.1.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("latest")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3.4")]
    [InlineData("1..3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-beta.01")]
    [InlineData("1.2.3-beta_1")]
    public void Parse_RechazaLoQueNoEsUnaVersion(string? texto)
    {
        Assert.False(SemanticVersion.TryParse(texto, out _));
    }
}
