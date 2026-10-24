using System.Net;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Composer;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Testea Composer SIN red ni PHP de verdad: el instalador con un phar en memoria (el SHA-256, la
/// caché, que no quede nada a medias), qué Composer y qué PHP elige, la orden que arma (el php.ini de
/// Roost en el entorno y sin preguntas), la validación de los paquetes y la línea del shim.
/// </summary>
public class ComposerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-composer-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"), WwwDir: Path.Combine(_root, "www"));

    private static readonly byte[] Phar = Encoding.UTF8.GetBytes("#!/usr/bin/env php\n<?php // composer.phar de pega\n__HALT_COMPILER();");

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static ComposerBuild Build(string version = "2.10.3", string? sha256 = null) =>
        new(version, new Uri($"https://example.test/download/{version}/composer.phar"),
            sha256 ?? Convert.ToHexStringLower(SHA256.HashData(Phar)), new ComponentVersion(7, 2, 5, "7.2.5"));

    private InstalledPhp Php(string name)
    {
        var dir = Path.Combine(Paths.PhpBin, name);
        Directory.CreateDirectory(Path.Combine(dir, "ext"));
        File.WriteAllText(Path.Combine(dir, "php.exe"), "");
        foreach (var ext in new[] { "curl", "mbstring", "openssl", "zip" })
            File.WriteAllText(Path.Combine(dir, "ext", $"php_{ext}.dll"), "");
        return new PhpInstaller(Paths).ListInstalled().Single(p => p.Name == name);
    }

    private void FakeComposer(string version)
    {
        var dir = Path.Combine(Paths.ComposerBin, version);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, InstalledComposer.PharName), Phar);
    }

    [Fact]
    public async Task Install_DejaElPharEnBinComposer_YLocateLoEncuentra()
    {
        var handler = new FakeHandler(Phar);
        var installer = new ComposerInstaller(Paths, new HttpClient(handler));
        var build = Build();

        var installed = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(Paths.ComposerBin, "2.10.3", "composer.phar"), installed.Phar);
        Assert.Equal(Phar, File.ReadAllBytes(installed.Phar));
        Assert.Equal(installed, installer.Find(build));
        Assert.Empty(Directory.GetDirectories(Paths.ComposerBin, ".*"));   // sin temporales
        Assert.True(File.Exists(Path.Combine(Paths.Downloads, "composer-2.10.3.phar")));   // en la caché, con su versión

        // Otra vez, sin la carpeta: la descarga verificada de la caché vale y no se va a la red.
        Directory.Delete(installed.Directory, recursive: true);
        await installer.InstallAsync(build);
        Assert.Equal(1, handler.Requests);
        await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));   // ya está: no se pisa
    }

    [Fact]
    public async Task Install_ConElShaMal_NoInstalaNada_YLoDice()
    {
        var installer = new ComposerInstaller(Paths, new HttpClient(new FakeHandler(Phar)));
        var build = Build(sha256: new string('0', 64));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.StartsWith("No se pudo preparar Composer", ex.Message);
        Assert.Contains("SHA-256", ex.Message);
        Assert.Null(installer.Find(build));
        Assert.Empty(installer.ListInstalled());
        Assert.False(Directory.Exists(Paths.ComposerBin) && Directory.GetDirectories(Paths.ComposerBin).Length > 0);
        Assert.False(File.Exists(Path.Combine(Paths.Downloads, "composer-2.10.3.phar")));
    }

    [Fact]
    public async Task Locate_PrefiereElDeEstaVersion_YSiNoElMasNuevoDeLosQueHay()
    {
        Assert.Null(ComposerTool.Locate(Paths));

        FakeComposer("2.8.12");
        FakeComposer("2.9.8");
        Directory.CreateDirectory(Path.Combine(Paths.ComposerBin, ".tmp-abc"));   // de una instalación a medias
        Assert.Equal(["2.9.8", "2.8.12"], new ComposerInstaller(Paths).ListInstalled().Select(c => c.Version));
        Assert.Equal("2.9.8", ComposerTool.Locate(Paths)?.Version);
        Assert.False(ComposerTool.IsCurrentInstalled(Paths));

        FakeComposer(ComposerCatalog.Stable.Version);
        Assert.Equal(ComposerCatalog.Stable.Version, ComposerTool.Locate(Paths)?.Version);
        Assert.True(ComposerTool.IsCurrentInstalled(Paths));
        // Con el de esta versión ya puesto, EnsureAsync no va a la red.
        var handler = new FakeHandler([]);
        Assert.Equal(ComposerCatalog.Stable.Version, (await ComposerTool.EnsureAsync(Paths, new HttpClient(handler))).Version);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public void ElDeRoost_EsUnaVersion2Estable_ConSuSha256_DeGetcomposerOrg()
    {
        var build = ComposerCatalog.Stable;
        Assert.Matches(@"^2\.\d+\.\d+$", build.Version);
        Assert.Matches("^[0-9a-f]{64}$", build.Sha256);
        Assert.Equal($"https://getcomposer.org/download/{build.Version}/composer.phar", build.Url.ToString());
        Assert.True(build.MinPhp <= new ComponentVersion(7, 4, 0, "7.4.0"));   // vale para todos los PHP de Roost
    }

    [Fact]
    public void Command_UsaElPhpDeLaCarpeta_ConElPhpIniDeRoost_YSinPreguntas()
    {
        var php = Php("8.4.27-nts-x64");
        FakeComposer(ComposerCatalog.Stable.Version);
        var composer = ComposerTool.Locate(Paths)!;
        var project = Path.Combine(_root, "www", "tienda");
        Directory.CreateDirectory(project);

        var spec = ComposerTool.Command(Paths, php, composer, ComposerTool.Arguments(ComposerAction.Install), project);

        Assert.Equal(php.Executable, spec.Exe);
        Assert.Equal([composer.Phar, "install"], spec.Arguments);
        Assert.Equal(project, spec.WorkingDirectory);
        Assert.Null(spec.Timeout);   // un install largo no se corta
        var env = spec.Environment!;
        Assert.Equal(Path.Combine(Paths.Etc, "php", "8.4.27-nts-x64"), env["PHPRC"]);
        Assert.Equal(Path.Combine(Paths.Etc, "php", "conf.d"), env["PHP_INI_SCAN_DIR"]);
        Assert.Equal("1", env[ComposerTool.NoInteractionVariable]);
        Assert.False(env.ContainsKey("COMPOSER_HOME"));   // la caché y el auth.json del usuario siguen valiendo

        // El php.ini que lee es el de Roost: con openssl y el bundle de CA para curl.
        var ini = File.ReadAllText(Path.Combine(env["PHPRC"]!, "php.ini"));
        Assert.Contains("extension=openssl", ini);
        Assert.Contains("curl.cainfo", ini);

        var interactive = ComposerTool.Command(Paths, php, composer, ["--version"], project, interactive: true);
        Assert.False(interactive.Environment!.ContainsKey(ComposerTool.NoInteractionVariable));
    }

    [Fact]
    public async Task Command_SeEjecutaSobreElRunner_ConSusLineas()
    {
        var php = Php("8.4.27-nts-x64");
        FakeComposer(ComposerCatalog.Stable.Version);
        var composer = ComposerTool.Locate(Paths)!;
        var runner = new FakeCommandRunner().On(composer.Phar + " require psr/log:^3", stderr: "./composer.json has been updated\nLock file operations: 1 install\n  - Locking psr/log (3.0.2)\n");
        var lines = new List<string>();

        var spec = ComposerTool.Command(Paths, php, composer, ComposerTool.Arguments(ComposerAction.Require, "psr/log:^3"), _root);
        var result = await runner.RunAsync(spec, new SyncProgress(lines.Add), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["./composer.json has been updated", "Lock file operations: 1 install", "  - Locking psr/log (3.0.2)"], lines);
        Assert.Equal(php.Executable, runner.Calls.Single().Exe);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public void PhpFor_EsElDeLaTerminal_YCheckPhpRechazaUnoDemasiadoViejo()
    {
        Php("8.4.27-nts-x64");
        var legacy = Php("7.4.33-nts-x64");
        var project = Path.Combine(_root, "www", "legacy");
        Directory.CreateDirectory(project);
        new SiteRegistry(Paths).Upsert(new Site { Name = "legacy", Folder = project, Php = "7.4" });

        var choice = ComposerTool.PhpFor(Paths, project);
        Assert.Equal("7.4.33-nts-x64", choice.Php.Name);
        Assert.Contains("legacy.test", choice.Reason);

        ComposerTool.CheckPhp(legacy);   // 7.4 >= 7.2.5: vale
        var old = new InstalledPhp("7.1.33-nts-x64", _root, Path.Combine(_root, "php.exe"));
        var ex = Assert.Throws<RoostException>(() => ComposerTool.CheckPhp(old));
        Assert.Contains("7.2.5", ex.Message);
    }

    [Theory]
    [InlineData("monolog/monolog", "monolog/monolog")]
    [InlineData("  Monolog/Monolog ", "monolog/monolog")]
    [InlineData("psr/log:^3.0", "psr/log:^3.0")]
    [InlineData("symfony/console:6.4.*", "symfony/console:6.4.*")]
    [InlineData("laravel/framework:^11.0 || ^12.0", "laravel/framework:^11.0 || ^12.0")]
    [InlineData("foo/bar:dev-main", "foo/bar:dev-main")]
    [InlineData("a/b:>=1.0 <2.0", "a/b:>=1.0 <2.0")]
    [InlineData("vendor/my-package.v2:~2.1@beta", "vendor/my-package.v2:~2.1@beta")]
    [InlineData("doctrine/doctrine-bundle:2.13.0-rc1", "doctrine/doctrine-bundle:2.13.0-rc1")]
    public void NormalizePackage_AceptaLoQueAceptaComposer(string spec, string expected)
    {
        Assert.Equal(expected, ComposerTool.NormalizePackage(spec));
        Assert.True(ComposerTool.IsValidPackage(spec));
    }

    [Theory]
    [InlineData("")]
    [InlineData("monolog")]
    [InlineData("vendor/")]
    [InlineData("/paquete")]
    [InlineData("-x/y")]
    [InlineData("vendor/paquete:")]
    [InlineData("vendor/paquete:--evil")]
    [InlineData("vendor/paquete;calc")]
    [InlineData("vendor/paquete:^1 & calc")]
    [InlineData("vendor/paquete/otro")]
    [InlineData("ext-gd")]
    [InlineData("php:^8.2")]
    public void NormalizePackage_RechazaLoDemas(string spec)
    {
        Assert.False(ComposerTool.IsValidPackage(spec));
        Assert.Throws<RoostException>(() => ComposerTool.NormalizePackage(spec));
    }

    [Fact]
    public void Arguments_DeCadaAccion()
    {
        Assert.Equal(["install"], ComposerTool.Arguments(ComposerAction.Install));
        Assert.Equal(["update"], ComposerTool.Arguments(ComposerAction.Update));
        Assert.Equal(["require", "psr/log"], ComposerTool.Arguments(ComposerAction.Require, "PSR/log"));
        Assert.Equal(["require", "--dev", "phpunit/phpunit:^11"], ComposerTool.Arguments(ComposerAction.Require, "phpunit/phpunit:^11", dev: true));
        Assert.Throws<RoostException>(() => ComposerTool.Arguments(ComposerAction.Require, null));
        Assert.Equal("composer require --dev phpunit/phpunit", ComposerTool.Display(["require", "--dev", "phpunit/phpunit"]));
    }

    [Theory]
    [InlineData(@"C:\roost\bin\composer\2.10.3\composer.phar", "install --no-dev", "\"C:\\roost\\bin\\composer\\2.10.3\\composer.phar\" install --no-dev")]
    [InlineData(@"C:\Mis cosas\roost\composer.phar", "", "\"C:\\Mis cosas\\roost\\composer.phar\"")]
    [InlineData(@"C:\roost\composer.phar", "require \"vendor/x:^1 || ^2\"", "\"C:\\roost\\composer.phar\" require \"vendor/x:^1 || ^2\"")]
    public void ShimArguments_ElPharEntrecomillado_YLoDemasTalCual(string phar, string rest, string expected)
    {
        Assert.Equal(expected, ComposerTool.ShimArguments(phar, rest));
    }

    [Theory]
    [InlineData("Composer version 2.10.3 2026-08-27 13:38:22\nPHP version 8.4.27 (C:\\roost\\bin\\php\\8.4.27-nts-x64\\php.exe)", "2.10.3")]
    [InlineData("Composer 2.9.8 2026-05-01", "2.9.8")]
    [InlineData("Could not open input file: composer.phar", null)]
    [InlineData(null, null)]
    public void ParseVersion_DeComposerVersion(string? output, string? expected)
    {
        Assert.Equal(expected, ComposerTool.ParseVersion(output));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
