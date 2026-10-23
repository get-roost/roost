using Roost.Core.Models;

namespace Roost.Core.Tests;

/// <summary>
/// El corazón del audit es comparar versiones por NÚMERO, no por texto: 8.3.9 &lt; 8.3.36.
/// Si esto falla, un PHP vulnerable pasaría por parcheado.
/// </summary>
public class ComponentVersionTests
{
    [Fact]
    public void TryParse_ExtraeLaPrimeraVersionDeUnBannerPhp()
    {
        var ok = ComponentVersion.TryParse(
            "PHP 8.3.14 (cli) (built: Dec 17 2024 22:59:40) (ZTS Visual C++ 2019 x64)",
            out var v);

        Assert.True(ok);
        Assert.Equal(8, v.Major);
        Assert.Equal(3, v.Minor);
        Assert.Equal(14, v.Patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no hay version aqui")]
    [InlineData("PHP 8.3 sin patch")]
    public void TryParse_SinVersionLegible_DevuelveFalse(string? texto)
    {
        Assert.False(ComponentVersion.TryParse(texto, out _));
    }

    [Fact]
    public void Comparar_ElPatchSeOrdenaPorNumeroNoPorTexto()
    {
        ComponentVersion.TryParse("8.3.9", out var menor);
        ComponentVersion.TryParse("8.3.36", out var mayor);

        // Por texto "9" > "36"; por número 9 < 36. Tiene que ganar el número.
        Assert.True(menor < mayor);
        Assert.False(menor > mayor);
        Assert.True(menor.CompareTo(mayor) < 0);
    }

    [Fact]
    public void Comparar_MajorYMinorMandanSobreElPatch()
    {
        ComponentVersion.TryParse("8.2.99", out var v8299);
        ComponentVersion.TryParse("8.3.0", out var v830);
        ComponentVersion.TryParse("7.4.33", out var v7433);

        Assert.True(v8299 < v830);   // 8.2.x < 8.3.x aunque el patch sea mayor
        Assert.True(v7433 < v8299);  // 7.x < 8.x
    }

    [Fact]
    public void Operadores_DeIgualdadYRango()
    {
        ComponentVersion.TryParse("8.4.27", out var a);
        ComponentVersion.TryParse("8.4.27", out var b);
        ComponentVersion.TryParse("8.4.26", out var anterior);

        Assert.True(a >= b);
        Assert.True(a <= b);
        Assert.True(a > anterior);
        Assert.True(anterior < a);
        Assert.Equal(0, a.CompareTo(b));
    }
}
