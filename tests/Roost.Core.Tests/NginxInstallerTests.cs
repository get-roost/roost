using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el instalador de nginx SIN red. La diferencia con PHP: el zip oficial lo mete todo
/// en una carpeta nginx-&lt;versión&gt;/, y lo que se instala es su contenido.
/// </summary>
public class NginxInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-nginx-" + Guid.NewGuid().ToString("N"));

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

    private (NginxInstaller Installer, NginxBuild Build, RoostPaths Paths) Setup(byte[] zip)
    {
        var paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        var build = new NginxBuild("1.30.6", new Uri("https://example.test/nginx-1.30.6.zip"), Convert.ToHexStringLower(SHA256.HashData(zip)));
        return (new NginxInstaller(paths, new HttpClient(new FakeHandler(zip))), build, paths);
    }

    [Fact]
    public async Task Install_QuitaLaCarpetaEnvolvente_YFindLoEncuentra()
    {
        var (installer, build, paths) = Setup(Zip("nginx-1.30.6/nginx.exe", "nginx-1.30.6/conf/mime.types", "nginx-1.30.6/conf/fastcgi_params"));

        var installed = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(paths.NginxBin, "1.30.6", "nginx.exe"), installed.Executable);
        Assert.True(File.Exists(installed.Executable));
        Assert.True(File.Exists(installed.FastCgiParams));
        Assert.Equal(installed, installer.Find(build));
        Assert.Empty(Directory.GetDirectories(paths.NginxBin, ".*")); // sin temporales
    }

    [Fact]
    public async Task Install_ZipSinNginxExe_NoInstala()
    {
        var (installer, build, paths) = Setup(Zip("nginx-1.30.6/README"));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Null(installer.Find(build));
        Assert.Empty(Directory.GetDirectories(paths.NginxBin));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
