using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el instalador de MinGit SIN red, con un zip hecho en memoria: la verificacion del SHA-256,
/// que exija cmd/git.exe y el transporte HTTPS, y como GitTool elige entre el git de Roost y el de
/// roost.toml.
/// </summary>
public class GitInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-git-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write("x");
            }
        }
        return ms.ToArray();
    }

    private static readonly string[] MinGitEntries = ["cmd/git.exe", "mingw64/bin/git-remote-https.exe", "mingw64/bin/git.exe", "etc/gitconfig", "LICENSE.txt"];

    private (GitInstaller Installer, GitBuild Build) Setup(byte[] zip, string? sha256 = null)
    {
        var build = new GitBuild("2.55.0.3", new Uri("https://example.test/MinGit-2.55.0.3-64-bit.zip"), sha256 ?? Convert.ToHexStringLower(SHA256.HashData(zip)));
        return (new GitInstaller(Paths, new HttpClient(new FakeHandler(zip))), build);
    }

    [Fact]
    public async Task Install_DejaMinGitEnBinGit_YLocateLoEncuentra()
    {
        var (installer, build) = Setup(Zip(MinGitEntries));

        var installed = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.GitBin, "2.55.0.3", "cmd", "git.exe"), installed.Executable);
        Assert.True(File.Exists(installed.Executable));
        Assert.True(File.Exists(Path.Combine(installed.Directory, "mingw64", "bin", "git-remote-https.exe")));
        Assert.Equal(installed, installer.Find(build));
        Assert.Empty(Directory.GetDirectories(Paths.GitBin, ".*")); // sin temporales

        // En Linux, Locate da el git del sistema (el del PATH), no el de bin/git: esto es de Windows.
        if (!OperatingSystem.IsWindows()) return;
        var located = GitTool.Locate(Paths, new RoostConfig());
        Assert.NotNull(located);
        Assert.Equal(GitSource.Roost, located.Source);
        Assert.Equal(installed.Executable, located.Executable);
        Assert.Equal("2.55.0.3", located.Version);
    }

    [Fact]
    public async Task Install_ZipSinElTransporteHttps_NoInstala()
    {
        var (installer, build) = Setup(Zip("cmd/git.exe", "mingw64/bin/git.exe"));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Contains("git-remote-https.exe", ex.Message);
        Assert.StartsWith("No se pudo preparar git", ex.Message);
        Assert.Null(installer.Find(build));
        Assert.False(Directory.Exists(Paths.GitBin) && Directory.GetDirectories(Paths.GitBin).Length > 0);
    }

    [Fact]
    public async Task Install_ZipSinCmdGitExe_NoInstala()
    {
        var (installer, build) = Setup(Zip("mingw64/bin/git.exe", "mingw64/bin/git-remote-https.exe"));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Null(installer.Find(build));
        Assert.Empty(Directory.GetDirectories(Paths.GitBin));
    }

    [Fact]
    public async Task Install_ConElShaMal_NoInstalaYLoDice()
    {
        var (installer, build) = Setup(Zip(MinGitEntries), sha256: new string('0', 64));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains("[git] exe", ex.Message);
        Assert.Null(installer.Find(build));
        if (OperatingSystem.IsWindows()) Assert.Null(GitTool.Locate(Paths, new RoostConfig()));
    }

    [Fact]
    public void Locate_ConGitExeEnRoostToml_PrefiereEse_YSiNoExisteLoDice()
    {
        var mine = Path.Combine(_root, "mi-git", "git.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "");

        var located = GitTool.Locate(Paths, new RoostConfig { GitExe = mine });
        Assert.NotNull(located);
        Assert.Equal(GitSource.Configured, located.Source);
        Assert.Equal(mine, located.Executable);
        Assert.Null(located.Version);

        var ex = Assert.Throws<RoostException>(() => GitTool.Locate(Paths, new RoostConfig { GitExe = Path.Combine(_root, "no", "git.exe") }));
        Assert.Contains("[git] exe", ex.Message);
    }

    [WindowsFact("Solo en Windows: en Linux, sin [git] exe, Locate da el git del sistema (el del PATH).")]
    public void Locate_SinNada_DaNull()
    {
        Assert.Null(GitTool.Locate(Paths, new RoostConfig()));
    }

    [Fact]
    public void Locate_EnLinux_EsElGitDelPath_YSinGitLoDiceConSuDistro()
    {
        if (OperatingSystem.IsLinux())
        {
            // La 24.04 de pruebas y el runner tienen git: el del PATH, sin version (no es el de Roost).
            var located = GitTool.Locate(Paths, new RoostConfig());
            Assert.NotNull(located);
            Assert.Equal(GitSource.System, located.Source);
            Assert.Equal(GitTool.FindSystemGit(), located.Executable);
            Assert.Null(located.Version);
        }
        Assert.Equal("sudo apt install git", GitTool.SystemInstallHint("ID=ubuntu\nID_LIKE=debian\n"));
        Assert.Equal("sudo apt install git", GitTool.SystemInstallHint(null));
        Assert.Equal("sudo dnf install git", GitTool.SystemInstallHint("NAME=\"Fedora Linux\"\nID=fedora\n"));
        Assert.Equal("sudo dnf install git", GitTool.SystemInstallHint("ID=rocky\nID_LIKE=\"rhel centos fedora\"\n"));
        Assert.Equal("sudo pacman -S git", GitTool.SystemInstallHint("ID=arch\n"));
        Assert.Equal("sudo zypper install git", GitTool.SystemInstallHint("ID=\"opensuse-leap\"\nID_LIKE=\"suse opensuse\"\n"));
        Assert.Equal("sudo apk add git", GitTool.SystemInstallHint("ID=alpine\n"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
