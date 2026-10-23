using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>Firefox y la CA de Roost, siempre sobre perfiles falsos en una carpeta temporal (nunca el Firefox real).</summary>
public class FirefoxTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-firefox-" + Guid.NewGuid().ToString("N"));

    private string FakeFirefox()
    {
        var release = Path.Combine(_root, "Profiles", "abcd.default-release");
        var otro = Path.Combine(_root, "otro-sitio", "trabajo");
        Directory.CreateDirectory(release);
        Directory.CreateDirectory(otro);
        File.WriteAllText(Path.Combine(_root, "profiles.ini"), $"""
            [Install308046B0AF4A39CB]
            Default=Profiles/abcd.default-release
            Locked=1

            [Profile1]
            Name=trabajo
            IsRelative=0
            Path={otro}

            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/abcd.default-release
            Default=1

            [Profile2]
            Name=borrado
            IsRelative=1
            Path=Profiles/no-existe

            [General]
            StartWithLastProfile=1
            Version=2
            """);
        return release;
    }

    [Fact]
    public void Profiles_LosDeProfilesIni_RelativosYAbsolutos_SoloLosQueExisten()
    {
        FakeFirefox();
        var profiles = FirefoxTrust.Profiles(_root);
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.EndsWith("abcd.default-release", StringComparison.Ordinal));
        Assert.Contains(profiles, p => p.EndsWith("trabajo", StringComparison.Ordinal));
        Assert.Empty(FirefoxTrust.Profiles(Path.Combine(_root, "sin-firefox")));
    }

    [Fact]
    public void EnableYDisable_SoloElBloqueDeRoost_RespetandoElUserJsDelUsuario()
    {
        var profile = FakeFirefox();
        var userJs = Path.Combine(profile, "user.js");
        File.WriteAllText(userJs, "user_pref(\"browser.startup.page\", 3);\n");

        FirefoxTrust.Enable(profile);
        FirefoxTrust.Enable(profile);   // dos veces: un solo bloque
        var text = File.ReadAllText(userJs);
        Assert.True(FirefoxTrust.IsEnabled(profile));
        Assert.StartsWith("user_pref(\"browser.startup.page\", 3);", text);
        Assert.Single(text.Split(FirefoxTrust.Begin)[1..]);
        Assert.Contains("user_pref(\"security.enterprise_roots.enabled\", true);", text);

        FirefoxTrust.Disable(profile);
        Assert.False(FirefoxTrust.IsEnabled(profile));
        Assert.Equal("user_pref(\"browser.startup.page\", 3);\n", File.ReadAllText(userJs));
    }

    [Fact]
    public void Disable_SinNadaMas_BorraElUserJsQuePusoRoost()
    {
        var profile = FakeFirefox();
        FirefoxTrust.Enable(profile);
        FirefoxTrust.Disable(profile);
        Assert.False(File.Exists(Path.Combine(profile, "user.js")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
