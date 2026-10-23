using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Core.Tests;

/// <summary>Tests del parser de rangos: comparacion por numero, nunca por texto.</summary>
public class VersionRangeTests
{
    private static ComponentVersion V(int major, int minor, int patch)
        => new(major, minor, patch, $"{major}.{minor}.{patch}");

    [Theory]
    [InlineData(8, 3, 18, ">=8.3.0 <8.3.36", true)]   // dentro del rango afectado
    [InlineData(8, 3, 36, ">=8.3.0 <8.3.36", false)]  // justo en la version corregida
    [InlineData(8, 3, 35, ">=8.3.0 <8.3.36", true)]
    [InlineData(8, 2, 34, ">=8.3.0 <8.3.36", false)]  // otra rama
    [InlineData(7, 4, 33, "<8.2.0", true)]            // todo lo anterior a 8.2
    [InlineData(8, 2, 0, "<8.2.0", false)]
    [InlineData(8, 3, 36, ">=8.3.36", true)]
    [InlineData(8, 3, 9, "==8.3.9", true)]
    public void Satisfies_Compara_Por_Numero(int major, int minor, int patch, string range, bool expected)
        => Assert.Equal(expected, VersionRange.Satisfies(V(major, minor, patch), range));

    [Fact]
    public void Texto_No_Enganiaria_Al_Comparador()
    {
        // "8.3.9" como texto es > "8.3.36"; como numero es MENOR. Debe caer dentro.
        Assert.True(VersionRange.Satisfies(V(8, 3, 9), ">=8.3.0 <8.3.36"));
    }

    [Fact]
    public void Rango_Vacio_No_Se_Cumple()
        => Assert.False(VersionRange.Satisfies(V(8, 3, 9), ""));
}
