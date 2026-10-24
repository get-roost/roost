using Roost.Core.Platform;
using Roost.Core.Shims;

namespace Roost.Core.Tests;

/// <summary>
/// El shim de composer: que se ponga junto al de php (y se añada a unos shims viejos sin tocar el
/// PATH), y quién le gana en el PATH (solo la parte pura: ni el registro ni el disco de verdad).
/// </summary>
public class ShimComposerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-shim-composer-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private string Source()
    {
        var source = Path.Combine(_root, "app");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, OperatingSystem.IsWindows() ? "roost-shim.exe" : "roost-shim"), "shim");
        return source;
    }

    [Fact]
    public void Install_PonePhpYComposer()
    {
        var shims = new ShimInstaller(Paths);
        Assert.False(shims.Has("composer"));

        shims.Install(Source());

        Assert.True(shims.Has("php"));
        Assert.True(shims.Has("composer"));
        Assert.Equal("shim", File.ReadAllText(shims.ToolPath("composer")));
        Assert.Equal(["php", "composer"], ShimInstaller.Tools);
    }

    [Fact]
    public void AddMissing_AnadeComposerAUnosShimsViejos_YSinShimsNoHaceNada()
    {
        var shims = new ShimInstaller(Paths);
        var source = Source();
        Assert.Empty(shims.AddMissing(source));   // sin shims puestos no se pone nada
        Assert.False(Directory.Exists(Paths.Shims));

        // Unos shims de una versión de Roost anterior: solo php.
        Directory.CreateDirectory(Paths.Shims);
        File.WriteAllText(shims.ToolPath("php"), "viejo");

        Assert.Equal(["composer"], shims.AddMissing(source));
        Assert.True(shims.Has("composer"));
        Assert.Equal("viejo", File.ReadAllText(shims.ToolPath("php")));   // el de php no se toca
        Assert.Empty(shims.AddMissing(source));
    }

    private static Func<string, bool> Existing(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private const string Shims = @"C:\roost\bin\shims";

    [Fact]
    public void Shadowing_UnComposerBatEnElPathDelSistema_GanaAlShim()
    {
        var shadow = ShimShadowing.Find("composer", Shims,
            @"C:\Windows\system32;C:\ProgramData\ComposerSetup\bin;C:\php",
            Shims + @";C:\Users\yo\AppData\Roaming\Composer\vendor\bin",
            Existing(@"C:\ProgramData\ComposerSetup\bin\composer.bat", @"C:\ProgramData\ComposerSetup\bin\composer", @"C:\php\php.exe"));

        Assert.NotNull(shadow);
        Assert.Equal(@"C:\ProgramData\ComposerSetup\bin\composer.bat", shadow.Path);
        Assert.True(shadow.InSystemPath);

        var php = ShimShadowing.Find("php", Shims, @"C:\Windows\system32;C:\php", Shims, Existing(@"C:\php\php.exe"));
        Assert.Equal(@"C:\php\php.exe", php?.Path);
    }

    [Fact]
    public void Shadowing_LoQueVaDetrasDeLosShims_NoGana_YLoDeDelanteEnElDelUsuarioSi()
    {
        Assert.Null(ShimShadowing.Find("composer", Shims, @"C:\Windows\system32", Shims + @"\;C:\tools",
            Existing(@"C:\tools\composer.exe")));

        var before = ShimShadowing.Find("composer", Shims, @"C:\Windows\system32", @"C:\scoop\shims;" + Shims,
            Existing(@"C:\scoop\shims\composer.cmd"));
        Assert.NotNull(before);
        Assert.False(before.InSystemPath);

        // Un composer sin extensión (el script de bash de Composer-Setup) no lo ejecutan cmd ni PowerShell.
        Assert.Null(ShimShadowing.Find("composer", Shims, @"C:\ProgramData\ComposerSetup\bin", Shims,
            Existing(@"C:\ProgramData\ComposerSetup\bin\composer")));
        Assert.Null(ShimShadowing.Find("composer", Shims, null, null, Existing()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
