using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Install;
using Roost.Core.Node;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Node SIN red ni binarios de verdad: el catálogo (lo fijado, el SHASUMS256.txt y el index.json de
/// nodejs.org servidos en memoria) y el instalador con un zip hecho aquí (el SHA-256, la caché, la
/// carpeta envolvente del zip, que no quede nada a medias).
/// </summary>
public class NodeInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-node-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private static readonly DateOnly Hoy = new(2026, 10, 24);

    /// <summary>Un zip como el de nodejs.org: todo dentro de node-v&lt;versión&gt;-win-x64/.</summary>
    internal static byte[] Zip(string version, bool npm = true)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = $"node-v{version}-win-x64/";
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(root + name).Open());
                writer.Write(text);
            }
            Add("node.exe", "MZ node de pega");
            Add("npm.cmd", "@ECHO OFF");
            if (npm)
            {
                Add("node_modules/npm/bin/npm-cli.js", "// npm de pega");
                Add("node_modules/npm/bin/npx-cli.js", "// npx de pega");
                Add("node_modules/npm/package.json", """{ "name": "npm", "version": "11.19.1" }""");
                Add("node_modules/corepack/dist/corepack.js", "// corepack de pega");
            }
        }
        return buffer.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>nodejs.org en memoria: lo que se registre por URL; lo demás, 404.</summary>
    private sealed class FakeDist : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public List<string> Requests { get; } = [];

        public FakeDist Add(string url, string text) => Add(url, Encoding.UTF8.GetBytes(text));

        public FakeDist Add(string url, byte[] bytes)
        {
            Files[url] = bytes;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(Files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string Index = """
        [
          {"version":"v26.12.1","date":"2026-10-21","files":["win-x64-zip","win-x64-msi"],"npm":"11.20.0","lts":false,"security":true},
          {"version":"v24.22.1","date":"2026-10-20","files":["win-x64-zip"],"npm":"11.19.1","lts":"Krypton","security":true},
          {"version":"v22.23.4","date":"2026-10-20","files":["win-x64-zip"],"npm":"10.9.9","lts":"Jod","security":true},
          {"version":"v20.20.2","date":"2026-03-24","files":["win-x64-zip","win-x86-zip"],"npm":"10.8.2","lts":"Iron","security":true},
          {"version":"v20.20.1","date":"2026-02-24","files":["win-x64-zip"],"npm":"10.8.2","lts":"Iron","security":false},
          {"version":"v0.12.18","date":"2017-02-22","files":["win-x86-exe"],"npm":"2.15.11","lts":false,"security":false},
          {"version":"rara"},
          42
        ]
        """;

    [Fact]
    public void LoFijado_SonLasLtsDeNodejsOrg_ConSuSha_YLaMasNuevaPrimero()
    {
        Assert.Equal(["24.22.1", "22.23.4"], NodeCatalog.Lts.Select(b => b.Version));
        Assert.Same(NodeCatalog.V24, NodeCatalog.Default);
        foreach (var build in NodeCatalog.Lts)
        {
            Assert.True(build.Pinned);
            Assert.Matches("^[0-9a-f]{64}$", build.Sha256);
            Assert.Equal($"https://nodejs.org/dist/v{build.Version}/node-v{build.Version}-win-x64.zip", build.Url.ToString());
            Assert.Equal($"node-v{build.Version}-win-x64.zip", build.FileName);
            Assert.True(NodeCatalog.IsLts(build.Major, Hoy), build.Version);
            Assert.NotEqual(NodeBranchStatus.Eol, NodeCatalog.Status(build.Major, Hoy));
        }
    }

    [Theory]
    [InlineData("24", "24.22.1")]
    [InlineData("v24", "24.22.1")]
    [InlineData("lts", "24.22.1")]
    [InlineData("lts/*", "24.22.1")]
    [InlineData("lts/krypton", "24.22.1")]
    [InlineData("22", "22.23.4")]
    [InlineData("lts/jod", "22.23.4")]
    [InlineData("22.23.4", "22.23.4")]
    [InlineData(">=22 <24", "22.23.4")]
    [InlineData(">=20", "24.22.1")]
    public void ResolvePinned_SinIrALaRed(string spec, string version) =>
        Assert.Equal(version, NodeCatalog.ResolvePinned(NodeSpec.Parse(spec), Hoy)?.Version);

    [Theory]
    [InlineData("20")]
    [InlineData("22.11.0")]
    [InlineData("node")]
    [InlineData("lts/iron")]
    public void ResolvePinned_LoQueRoostNoFija_EsNull(string spec) =>
        Assert.Null(NodeCatalog.ResolvePinned(NodeSpec.Parse(spec), Hoy));

    [Fact]
    public async Task ResolveAsync_UnaExactaNoFijada_ConElShasumsDeEsaVersion()
    {
        var dist = new FakeDist().Add("https://nodejs.org/dist/v22.11.0/SHASUMS256.txt",
            $"{new string('1', 64)}  node-v22.11.0-darwin-arm64.tar.gz\n{new string('A', 64)}  node-v22.11.0-win-x64.zip\n");

        var build = await NodeCatalog.ResolveAsync(new HttpClient(dist), NodeSpec.Parse("v22.11.0"), today: Hoy);

        Assert.Equal("22.11.0", build.Version);
        Assert.False(build.Pinned);
        Assert.Equal(new string('a', 64), build.Sha256);   // en minúsculas, como lo calcula Roost
        Assert.Equal("https://nodejs.org/dist/v22.11.0/node-v22.11.0-win-x64.zip", build.Url.ToString());
        Assert.Equal(["https://nodejs.org/dist/v22.11.0/SHASUMS256.txt"], dist.Requests);   // sin index.json
    }

    [Fact]
    public async Task ResolveAsync_UnaRamaNoFijada_LaUltimaDelIndexConZip_YLuegoSuShasums()
    {
        var dist = new FakeDist()
            .Add("https://nodejs.org/dist/index.json", Index)
            .Add("https://nodejs.org/dist/v20.20.2/SHASUMS256.txt", $"{new string('b', 64)}  node-v20.20.2-win-x64.zip\n")
            .Add("https://nodejs.org/dist/v26.12.1/SHASUMS256.txt", $"{new string('c', 64)} *node-v26.12.1-win-x64.zip\n");
        var http = new HttpClient(dist);

        var iron = await NodeCatalog.ResolveAsync(http, NodeSpec.Parse("lts/iron"), today: Hoy);
        Assert.Equal("20.20.2", iron.Version);
        Assert.Equal("10.8.2", iron.Npm);
        Assert.Equal(new string('b', 64), iron.Sha256);
        Assert.Equal("20.20.2", (await NodeCatalog.ResolveAsync(http, NodeSpec.Parse("^20.11"), today: Hoy)).Version);
        Assert.Equal("26.12.1", (await NodeCatalog.ResolveAsync(http, NodeSpec.Parse("latest"), today: Hoy)).Version);

        // lts/* nunca va a la red: es la fijada.
        dist.Requests.Clear();
        Assert.Same(NodeCatalog.V24, await NodeCatalog.ResolveAsync(http, NodeSpec.Parse("lts/*"), today: Hoy));
        Assert.Empty(dist.Requests);

        var none = await Assert.ThrowsAsync<RoostException>(() => NodeCatalog.ResolveAsync(http, NodeSpec.Parse("19"), today: Hoy));
        Assert.Contains("no tiene ninguna version de Node para Windows x64 que case con '19'", none.Message);
        var missing = await Assert.ThrowsAsync<RoostException>(() => NodeCatalog.ResolveAsync(http, NodeSpec.Parse("22.99.0"), today: Hoy));
        Assert.Contains("No existe Node 22.99.0", missing.Message);
        await Assert.ThrowsAsync<RoostException>(() => NodeCatalog.ResolveAsync(http, NodeSpec.Parse("system"), today: Hoy));
    }

    [Fact]
    public void ParseIndex_SeSaltaLoQueNoEntiende()
    {
        var releases = NodeCatalog.ParseIndex(Index);

        Assert.Equal(["26.12.1", "24.22.1", "22.23.4", "20.20.2", "20.20.1", "0.12.18"], releases.Select(r => r.Version));
        Assert.Equal("Krypton", releases[1].Lts);
        Assert.Null(releases[0].Lts);
        Assert.False(releases[^1].HasWindowsZip);
        Assert.True(releases[0].Security);
        Assert.Throws<RoostException>(() => NodeCatalog.ParseIndex("<html>no</html>"));
        Assert.Null(NodeCatalog.ParseShasums("hola\n", "node-v1.0.0-win-x64.zip"));
    }

    [Fact]
    public async Task Install_ExtraeLaCarpetaDelZip_YNoDejaTemporales()
    {
        var zip = Zip("24.22.1");
        var dist = new FakeDist().Add("https://example.test/node-v24.22.1-win-x64.zip", zip);
        var build = new NodeBuild("24.22.1", new Uri("https://example.test/node-v24.22.1-win-x64.zip"), Sha(zip)) { Pinned = true };
        var installer = new NodeInstaller(Paths, new HttpClient(dist));

        var node = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.NodeBin, "24.22.1"), node.Directory);
        Assert.True(File.Exists(node.Executable));
        Assert.True(File.Exists(node.NpmCli));
        Assert.True(File.Exists(node.NpxCli));
        Assert.True(node.HasCorepack);
        Assert.Equal("11.19.1", node.NpmVersion);
        Assert.Equal(24, node.Major);
        Assert.Empty(Directory.GetDirectories(Paths.NodeBin, ".*"));
        Assert.True(File.Exists(Path.Combine(Paths.Downloads, "node-v24.22.1-win-x64.zip")));
        Assert.Equal(node, installer.Find(build));
        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));   // ya está

        // Otra vez, sin la carpeta: el zip de la caché vale y no se va a la red.
        Directory.Delete(node.Directory, recursive: true);
        await installer.InstallAsync(build);
        Assert.Single(dist.Requests);
    }

    [Fact]
    public async Task Install_ConElShaMal_OSinNpm_NoInstalaNada()
    {
        var zip = Zip("22.23.4");
        var dist = new FakeDist().Add("https://example.test/a.zip", zip).Add("https://example.test/b.zip", Zip("22.23.4", npm: false));
        var installer = new NodeInstaller(Paths, new HttpClient(dist));

        var bad = await Assert.ThrowsAsync<RoostException>(() =>
            installer.InstallAsync(new NodeBuild("22.23.4", new Uri("https://example.test/a.zip"), new string('0', 64))));
        Assert.Contains("SHA-256", bad.Message);
        Assert.Contains("roost node install 22.23.4", bad.Message);
        Assert.Empty(installer.ListInstalled());

        var noNpm = Zip("22.23.4", npm: false);
        var broken = await Assert.ThrowsAsync<RoostException>(() =>
            installer.InstallAsync(new NodeBuild("22.23.4", new Uri("https://example.test/b.zip"), Sha(noNpm))));
        Assert.Contains("no trae npm", broken.Message);
        Assert.False(Directory.Exists(Path.Combine(Paths.NodeBin, "22.23.4")));
    }

    [Fact]
    public void ListInstalled_LaMasNuevaPrimero_YMatchPorRamaOVersion()
    {
        foreach (var v in new[] { "22.23.4", "24.22.1", "22.11.0" })
        {
            Directory.CreateDirectory(Path.Combine(Paths.NodeBin, v));
            File.WriteAllText(Path.Combine(Paths.NodeBin, v, "node.exe"), "");
        }
        Directory.CreateDirectory(Path.Combine(Paths.NodeBin, ".tmp-x"));
        Directory.CreateDirectory(Path.Combine(Paths.NodeBin, "26.0.0"));   // sin node.exe: no cuenta
        Directory.CreateDirectory(Path.Combine(Paths.NodeBin, "otra"));

        var installer = new NodeInstaller(Paths);
        var installed = installer.ListInstalled();

        Assert.Equal(["24.22.1", "22.23.4", "22.11.0"], installed.Select(n => n.Version));
        Assert.Equal(["22.23.4", "22.11.0"], NodeInstaller.Match(installed, NodeSpec.Parse("22"), Hoy).Select(n => n.Version));
        Assert.Equal(["22.11.0"], NodeInstaller.Match(installed, NodeSpec.Parse("22.11"), Hoy).Select(n => n.Version));
        Assert.Equal(["24.22.1"], NodeInstaller.Match(installed, NodeSpec.Parse("lts/krypton"), Hoy).Select(n => n.Version));

        installer.Remove(installed[0]);
        Assert.Equal(["22.23.4", "22.11.0"], installer.ListInstalled().Select(n => n.Version));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
