using System.Formats.Tar;
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
/// nodejs.org servidos en memoria) y el instalador con un paquete hecho aquí con el layout de este
/// sistema (el zip de Windows con <c>node.exe</c>; el tar.gz de Linux con <c>bin/node</c> y
/// <c>lib/node_modules</c>): el SHA-256, la caché, la carpeta envolvente, que no quede nada a medias.
/// </summary>
public class NodeInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-node-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private static readonly DateOnly Hoy = new(2026, 10, 24);

    private static bool Linux => OperatingSystem.IsLinux();

    /// <summary>Un paquete como el de nodejs.org para este sistema: todo dentro de node-v&lt;versión&gt;-win-x64/ (zip) o -linux-x64/ (tar.gz).</summary>
    internal static byte[] Archive(string version, bool npm = true)
    {
        var files = new List<(string Name, string Text)>();
        if (Linux)
        {
            files.Add(("bin/node", "ELF node de pega"));
            files.Add(("bin/npm", "#!/usr/bin/env node"));
            if (npm)
            {
                files.Add(("lib/node_modules/npm/bin/npm-cli.js", "// npm de pega"));
                files.Add(("lib/node_modules/npm/bin/npx-cli.js", "// npx de pega"));
                files.Add(("lib/node_modules/npm/package.json", """{ "name": "npm", "version": "11.19.1" }"""));
                files.Add(("lib/node_modules/corepack/dist/corepack.js", "// corepack de pega"));
            }
            return TarGz($"node-v{version}-linux-x64/", files);
        }
        files.Add(("node.exe", "MZ node de pega"));
        files.Add(("npm.cmd", "@ECHO OFF"));
        if (npm)
        {
            files.Add(("node_modules/npm/bin/npm-cli.js", "// npm de pega"));
            files.Add(("node_modules/npm/bin/npx-cli.js", "// npx de pega"));
            files.Add(("node_modules/npm/package.json", """{ "name": "npm", "version": "11.19.1" }"""));
            files.Add(("node_modules/corepack/dist/corepack.js", "// corepack de pega"));
        }
        return Zip($"node-v{version}-win-x64/", files);
    }

    private static byte[] Zip(string root, IEnumerable<(string Name, string Text)> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(root + name).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] TarGz(string root, IEnumerable<(string Name, string Text)> files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, root + name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    Mode = name.StartsWith("bin/", StringComparison.Ordinal)
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                tar.WriteEntry(entry);
            }
        }
        return buffer.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>El nombre del paquete de este sistema (node-v…-win-x64.zip o node-v…-linux-x64.tar.gz).</summary>
    private static string Name(string version) => NodeCatalog.ArchiveName(version);

    /// <summary>Una línea de SHASUMS256.txt por sistema: el de este lleva <paramref name="sha"/>; el otro, otra cosa.</summary>
    private static string Sums(string version, char sha) =>
        $"{new string('1', 64)}  node-v{version}-darwin-arm64.tar.gz\n"
        + $"{new string(Linux ? '9' : sha, 64)}  node-v{version}-win-x64.zip\n"
        + $"{new string(Linux ? sha : '9', 64)}  node-v{version}-linux-x64.tar.gz\n";

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
          {"version":"v26.12.1","date":"2026-10-21","files":["linux-x64","win-x64-zip","win-x64-msi"],"npm":"11.20.0","lts":false,"security":true},
          {"version":"v24.22.1","date":"2026-10-20","files":["linux-x64","win-x64-zip"],"npm":"11.19.1","lts":"Krypton","security":true},
          {"version":"v22.23.4","date":"2026-10-20","files":["linux-x64","win-x64-zip"],"npm":"10.9.9","lts":"Jod","security":true},
          {"version":"v20.20.2","date":"2026-03-24","files":["linux-x64","win-x64-zip","win-x86-zip"],"npm":"10.8.2","lts":"Iron","security":true},
          {"version":"v20.20.1","date":"2026-02-24","files":["linux-x64","win-x64-zip"],"npm":"10.8.2","lts":"Iron","security":false},
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
            Assert.Equal($"https://nodejs.org/dist/v{build.Version}/{Name(build.Version)}", build.Url.ToString());
            Assert.Equal(Name(build.Version), build.FileName);
            Assert.True(NodeCatalog.IsLts(build.Major, Hoy), build.Version);
            Assert.NotEqual(NodeBranchStatus.Eol, NodeCatalog.Status(build.Major, Hoy));
        }
        // Las dos listas fijadas, la misma version y npm en cada sistema, con paquetes distintos.
        foreach (var (windows, linux) in NodeCatalog.WindowsLts.Zip(NodeCatalog.LinuxLts))
        {
            Assert.Equal(windows.Version, linux.Version);
            Assert.Equal(windows.Npm, linux.Npm);
            Assert.NotEqual(windows.Sha256, linux.Sha256);
            Assert.EndsWith("-win-x64.zip", windows.FileName);
            Assert.EndsWith("-linux-x64.tar.gz", linux.FileName);
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
        var dist = new FakeDist().Add("https://nodejs.org/dist/v22.11.0/SHASUMS256.txt", Sums("22.11.0", 'A'));

        var build = await NodeCatalog.ResolveAsync(new HttpClient(dist), NodeSpec.Parse("v22.11.0"), today: Hoy);

        Assert.Equal("22.11.0", build.Version);
        Assert.False(build.Pinned);
        Assert.Equal(new string('a', 64), build.Sha256);   // en minúsculas, como lo calcula Roost, y el de este sistema
        Assert.Equal($"https://nodejs.org/dist/v22.11.0/{Name("22.11.0")}", build.Url.ToString());
        Assert.Equal(["https://nodejs.org/dist/v22.11.0/SHASUMS256.txt"], dist.Requests);   // sin index.json
    }

    [Fact]
    public async Task ResolveAsync_UnaRamaNoFijada_LaUltimaDelIndexConPaquete_YLuegoSuShasums()
    {
        var dist = new FakeDist()
            .Add("https://nodejs.org/dist/index.json", Index)
            .Add("https://nodejs.org/dist/v20.20.2/SHASUMS256.txt", Sums("20.20.2", 'b'))
            .Add("https://nodejs.org/dist/v26.12.1/SHASUMS256.txt", Sums("26.12.1", 'c').Replace("  node-v26.12.1", " *node-v26.12.1"));
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
        Assert.Contains($"no tiene ninguna version de Node para {NodeCatalog.PlatformLabel} que case con '19'", none.Message);
        var missing = await Assert.ThrowsAsync<RoostException>(() => NodeCatalog.ResolveAsync(http, NodeSpec.Parse("22.99.0"), today: Hoy));
        Assert.Contains("No existe Node 22.99.0", missing.Message);
        await Assert.ThrowsAsync<RoostException>(() => NodeCatalog.ResolveAsync(http, NodeSpec.Parse("system"), today: Hoy));
    }

    [Fact]
    public async Task ResolveAsync_UnaVersionSinPaqueteParaEsteSistema_LoDice()
    {
        // Solo el otro sistema: el SHASUMS no trae el paquete de este.
        var other = Linux ? "node-v21.0.0-win-x64.zip" : "node-v21.0.0-linux-x64.tar.gz";
        var dist = new FakeDist().Add("https://nodejs.org/dist/v21.0.0/SHASUMS256.txt", $"{new string('a', 64)}  {other}\n");

        var ex = await Assert.ThrowsAsync<RoostException>(() => NodeCatalog.ResolveAsync(new HttpClient(dist), NodeSpec.Parse("21.0.0"), today: Hoy));

        Assert.Contains(Name("21.0.0"), ex.Message);
        Assert.Contains(NodeCatalog.PlatformLabel, ex.Message);
    }

    [Fact]
    public void ParseIndex_SeSaltaLoQueNoEntiende_YSabeDeLosDosSistemas()
    {
        var releases = NodeCatalog.ParseIndex(Index);

        Assert.Equal(["26.12.1", "24.22.1", "22.23.4", "20.20.2", "20.20.1", "0.12.18"], releases.Select(r => r.Version));
        Assert.Equal("Krypton", releases[1].Lts);
        Assert.Null(releases[0].Lts);
        Assert.True(releases[0].HasWindowsZip);
        Assert.True(releases[0].HasLinuxX64);
        Assert.False(releases[^1].HasWindowsZip);
        Assert.False(releases[^1].HasLinuxX64);
        Assert.False(releases[^1].HasArchiveForThisOs);
        Assert.True(releases[0].Security);
        Assert.Throws<RoostException>(() => NodeCatalog.ParseIndex("<html>no</html>"));
        Assert.Null(NodeCatalog.ParseShasums("hola\n", "node-v1.0.0-win-x64.zip"));
        Assert.Equal(new string('a', 64), NodeCatalog.ParseShasums($"{new string('A', 64)}  node-v1.0.0-linux-x64.tar.gz\n", "node-v1.0.0-linux-x64.tar.gz"));
    }

    [Fact]
    public async Task Install_ExtraeLaCarpetaDelPaquete_YNoDejaTemporales()
    {
        var archive = Archive("24.22.1");
        var url = $"https://example.test/{Name("24.22.1")}";
        var dist = new FakeDist().Add(url, archive);
        var build = new NodeBuild("24.22.1", new Uri(url), Sha(archive)) { Pinned = true };
        var installer = new NodeInstaller(Paths, new HttpClient(dist));

        var node = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.NodeBin, "24.22.1"), node.Directory);
        Assert.True(File.Exists(node.Executable));
        Assert.Equal(Path.Combine(node.Directory, InstalledNode.ExecutableRelative), node.Executable);
        Assert.True(File.Exists(node.NpmCli));
        Assert.True(File.Exists(node.NpxCli));
        Assert.True(node.HasCorepack);
        Assert.Equal("11.19.1", node.NpmVersion);
        Assert.Equal(24, node.Major);
        Assert.True(Directory.Exists(node.BinDirectory));
        Assert.Empty(Directory.GetDirectories(Paths.NodeBin, ".*"));
        Assert.True(File.Exists(Path.Combine(Paths.Downloads, Name("24.22.1"))));
        Assert.Equal(node, installer.Find(build));
        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));   // ya está

        // Otra vez, sin la carpeta: el paquete de la caché vale y no se va a la red.
        Directory.Delete(node.Directory, recursive: true);
        await installer.InstallAsync(build);
        Assert.Single(dist.Requests);
    }

    [LinuxFact]
    public async Task Install_EnLinux_ConservaElPermisoDeEjecucionDeBinNode()
    {
        var archive = Archive("22.23.4");
        var url = $"https://example.test/{Name("22.23.4")}";
        var installer = new NodeInstaller(Paths, new HttpClient(new FakeDist().Add(url, archive)));

        var node = await installer.InstallAsync(new NodeBuild("22.23.4", new Uri(url), Sha(archive)));

        Assert.True(File.GetUnixFileMode(node.Executable).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(Path.Combine(node.Directory, "lib", "node_modules"), node.Modules);
    }

    [Fact]
    public async Task Install_ConElShaMal_OSinNpm_NoInstalaNada()
    {
        var ext = Linux ? ".tar.gz" : ".zip";
        var archive = Archive("22.23.4");
        var noNpm = Archive("22.23.4", npm: false);   // una sola vez: el gzip lleva fecha y dos iguales no dan el mismo SHA
        var dist = new FakeDist().Add($"https://example.test/a{ext}", archive).Add($"https://example.test/b{ext}", noNpm);
        var installer = new NodeInstaller(Paths, new HttpClient(dist));

        var bad = await Assert.ThrowsAsync<RoostException>(() =>
            installer.InstallAsync(new NodeBuild("22.23.4", new Uri($"https://example.test/a{ext}"), new string('0', 64))));
        Assert.Contains("SHA-256", bad.Message);
        Assert.Contains("roost node install 22.23.4", bad.Message);
        Assert.Empty(installer.ListInstalled());

        var broken = await Assert.ThrowsAsync<RoostException>(() =>
            installer.InstallAsync(new NodeBuild("22.23.4", new Uri($"https://example.test/b{ext}"), Sha(noNpm))));
        Assert.Contains("no trae npm", broken.Message);
        Assert.False(Directory.Exists(Path.Combine(Paths.NodeBin, "22.23.4")));
    }

    [Fact]
    public void ListInstalled_LaMasNuevaPrimero_YMatchPorRamaOVersion()
    {
        foreach (var v in new[] { "22.23.4", "24.22.1", "22.11.0" })
        {
            var exe = new InstalledNode(v, Path.Combine(Paths.NodeBin, v)).Executable;
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "");
        }
        Directory.CreateDirectory(Path.Combine(Paths.NodeBin, ".tmp-x"));
        Directory.CreateDirectory(Path.Combine(Paths.NodeBin, "26.0.0"));   // sin ejecutable: no cuenta
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
