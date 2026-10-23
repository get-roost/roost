using Roost.Core.Updates;

namespace Roost.Core.Tests;

/// <summary>Roost instalado con el instalador de Windows: el aviso propone el instalador nuevo, no el zip.</summary>
public class InstallerChannelTests
{
    private static ReleaseInfo Release(bool withInstaller)
    {
        var assets = new List<ReleaseAsset>
        {
            new("roost-0.2.0-win-x64.zip", new Uri("https://github.com/get-roost/roost/releases/download/v0.2.0/roost-0.2.0-win-x64.zip"), 60_000_000),
            new("SHA256SUMS.txt", new Uri("https://github.com/get-roost/roost/releases/download/v0.2.0/SHA256SUMS.txt"), 300),
        };
        if (withInstaller)
            assets.Add(new("roost-0.2.0-win-x64-setup.exe", new Uri("https://github.com/get-roost/roost/releases/download/v0.2.0/roost-0.2.0-win-x64-setup.exe"), 45_000_000));
        return new ReleaseInfo
        {
            Tag = "v0.2.0",
            Version = SemanticVersion.TryParse("0.2.0", out var v) ? v : throw new InvalidOperationException(),
            Name = "Roost 0.2.0",
            Page = new Uri("https://github.com/get-roost/roost/releases/tag/v0.2.0"),
            Assets = assets,
        };
    }

    [Fact]
    public void ConElInstalador_SeProponeBajarElNuevo()
    {
        var advice = UpdateInstructions.For(InstallChannel.Installer, Release(withInstaller: true), windows: true);

        Assert.Null(advice.Command);
        Assert.Equal("roost-0.2.0-win-x64-setup.exe", advice.Package);
        Assert.EndsWith("/roost-0.2.0-win-x64-setup.exe", advice.Url!.ToString());
        Assert.Contains("ejecutalo", advice.Text);
    }

    [Fact]
    public void UnaReleaseSinInstalador_LlevaASuPagina()
    {
        var advice = UpdateInstructions.For(InstallChannel.Installer, Release(withInstaller: false), windows: true);

        Assert.Null(advice.Package);
        Assert.Equal("https://github.com/get-roost/roost/releases/tag/v0.2.0", advice.Url!.ToString());
    }

    [Fact]
    public void ElZipYElInstalador_NoSeConfunden()
    {
        var release = Release(withInstaller: true);

        Assert.Equal("roost-0.2.0-win-x64.zip", release.PackageFor(windows: true)!.Name);
        Assert.Equal("roost-0.2.0-win-x64-setup.exe", release.Installer!.Name);
    }
}
