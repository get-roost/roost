using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el instalador SIN red: un <see cref="HttpMessageHandler"/> falso sirve un zip
/// construido en memoria, y la raíz de Roost es una carpeta temporal.
/// </summary>
public class PhpInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly RoostPaths _paths;

    public PhpInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "roost-install-" + Guid.NewGuid().ToString("N"));
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
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

    private static PhpBuild Build(byte[] zip, string? sha = null) => new()
    {
        Version = new ComponentVersion(8, 3, 36, "8.3.36"),
        Branch = "8.3",
        ThreadSafe = false,
        Compiler = "vs16",
        Architecture = "x64",
        FileName = "php-8.3.36-nts-Win32-vs16-x64.zip",
        Url = new Uri("https://example.test/php-8.3.36-nts-Win32-vs16-x64.zip"),
        Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(zip)),
    };

    private PhpInstaller Installer(byte[] zip, out FakeHandler handler)
    {
        handler = new FakeHandler(zip);
        return new PhpInstaller(_paths, new HttpClient(handler));
    }

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Install_VerificaYExtrae_EnSuCarpetaPorVersion()
    {
        var zip = Zip("php.exe", "php.ini-development", "ext/php_curl.dll");
        var installer = Installer(zip, out _);

        var installed = await installer.InstallAsync(Build(zip));

        Assert.Equal("8.3.36-nts-x64", installed.Name);
        Assert.True(File.Exists(Path.Combine(_paths.PhpBin, "8.3.36-nts-x64", "php.exe")));
        Assert.True(File.Exists(Path.Combine(_paths.PhpBin, "8.3.36-nts-x64", "ext", "php_curl.dll")));
        Assert.Single(installer.ListInstalled());
        Assert.Empty(Directory.GetDirectories(_paths.PhpBin, ".*")); // sin temporales
    }

    [Fact]
    public async Task Install_ConShaQueNoCuadra_NoInstalaNiDejaRestos()
    {
        var zip = Zip("php.exe");
        var installer = Installer(zip, out _);

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(Build(zip, sha: new string('0', 64))));

        Assert.False(Directory.Exists(Path.Combine(_paths.PhpBin, "8.3.36-nts-x64")));
        Assert.Empty(Directory.GetFiles(_paths.Downloads)); // ni el .part ni el zip
    }

    [Fact]
    public async Task Install_ZipSinPhpExe_NoInstala()
    {
        var zip = Zip("readme.txt");
        var installer = Installer(zip, out _);

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(Build(zip)));

        Assert.Empty(installer.ListInstalled());
        Assert.Empty(Directory.GetDirectories(_paths.PhpBin));
    }

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Install_NuncaPisaUnaInstalacionExistente()
    {
        var zip = Zip("php.exe");
        var installer = Installer(zip, out _);
        await installer.InstallAsync(Build(zip));

        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(Build(zip)));
    }

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Install_ReutilizaLaDescargaVerificadaDeLaCache()
    {
        var zip = Zip("php.exe");
        var installer = Installer(zip, out var handler);

        installer.Remove(await installer.InstallAsync(Build(zip)));
        await installer.InstallAsync(Build(zip));

        Assert.Equal(1, handler.Requests);
    }

    [WindowsFact("Solo en Windows: zips de Windows.")]
    public async Task Find_CasaPorNombreVersionYRama_YRemoveQuitaLaCarpeta()
    {
        var zip = Zip("php.exe");
        var installer = Installer(zip, out _);
        var installed = await installer.InstallAsync(Build(zip));

        Assert.Single(installer.Find("8.3.36-nts-x64"));
        Assert.Single(installer.Find("8.3.36"));
        Assert.Single(installer.Find("8.3"));
        Assert.Empty(installer.Find("8.4"));

        installer.Remove(installed);

        Assert.Empty(installer.ListInstalled());
        Assert.False(Directory.Exists(installed.Directory));
    }

    [Fact]
    public void Remove_NoTocaNadaFueraDeLaRaizDeRoost()
    {
        var installer = Installer(Zip("php.exe"), out _);
        var laragon = Path.Combine(_root + "-laragon", "bin", "php", "php-8.3.18");
        var foreign = new InstalledPhp("php-8.3.18", laragon, Path.Combine(laragon, "php.exe"));

        Assert.Throws<RoostException>(() => installer.Remove(foreign));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
