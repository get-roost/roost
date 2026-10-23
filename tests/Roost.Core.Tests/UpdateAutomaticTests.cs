using Roost.Core.Updates;

namespace Roost.Core.Tests;

/// <summary>Cuándo no se hacen las comprobaciones de versión que nadie ha pedido (daemon y línea de aviso).</summary>
public class UpdateAutomaticTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        => name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void SinNada_SeComprueba()
        => Assert.False(UpdateChecker.AutomaticChecksOffIn(Env()));

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(" YES ")]
    [InlineData("on")]
    public void ConRoostNoUpdateCheck_NoSeComprueba(string value)
    {
        Assert.True(UpdateChecker.DisabledIn(Env(("ROOST_NO_UPDATE_CHECK", value))));
        Assert.True(UpdateChecker.AutomaticChecksOffIn(Env(("ROOST_NO_UPDATE_CHECK", value))));
    }

    [Theory]
    [InlineData("CI", "true")]
    [InlineData("GITHUB_ACTIONS", "true")]
    public void EnCi_NoSeCompruebaSolo_PeroRoostUpdateSigueFuncionando(string name, string value)
    {
        Assert.True(UpdateChecker.AutomaticChecksOffIn(Env((name, value))));
        Assert.False(UpdateChecker.DisabledIn(Env((name, value))));   // lo que mira roost update y Ajustes
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public void UnValorQueNoEsSi_NoApaga(string value)
        => Assert.False(UpdateChecker.AutomaticChecksOffIn(Env(("ROOST_NO_UPDATE_CHECK", value))));
}
