using Roost.Core.Install;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>El PHP que pide el composer.json de un proyecto, para el alta de sitios y la bienvenida.</summary>
public class ComposerJsonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-composer-" + Guid.NewGuid().ToString("N"));

    private static InstalledPhp Php(string name) => new(name, $@"C:\roost\bin\php\{name}", $@"C:\roost\bin\php\{name}\php.exe");

    private static readonly IReadOnlyList<InstalledPhp> Installed =
        [Php("7.4.33-nts-x64"), Php("8.2.35-nts-x64"), Php("8.4.27-nts-x64"), Php("8.4.27-ts-x64")];

    [Fact]
    public void PhpConstraint_LeeRequirePhp_YNullSiNoHayONoVale()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(ComposerJson.PhpConstraint(_dir));   // sin composer.json

        File.WriteAllText(Path.Combine(_dir, "composer.json"), """{ "require": { "php": "^8.2", "laravel/framework": "^11.0" } }""");
        Assert.Equal("^8.2", ComposerJson.PhpConstraint(_dir));

        File.WriteAllText(Path.Combine(_dir, "composer.json"), """{ "require": { "laravel/framework": "^11.0" } }""");
        Assert.Null(ComposerJson.PhpConstraint(_dir));

        File.WriteAllText(Path.Combine(_dir, "composer.json"), "{ esto no es json");
        Assert.Null(ComposerJson.PhpConstraint(_dir));
    }

    [Theory]
    [InlineData("^8.2", "8.4.27-nts-x64")]    // la más nueva de la 8
    [InlineData(">=7.4", "8.4.27-nts-x64")]   // sin ^, vale cualquier mayor
    [InlineData("^7.4", "7.4.33-nts-x64")]    // con ^, la misma mayor
    [InlineData("~7.4.0", "8.4.27-nts-x64")]  // solo se mira el mínimo y el ^
    [InlineData("^8.5", null)]                // ninguna llega
    [InlineData("*", null)]                   // sin versión
    public void Recommended_LaMasNuevaQueCumple_SinTs(string constraint, string? expected) =>
        Assert.Equal(expected, ComposerJson.Recommended(constraint, Installed)?.Name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
