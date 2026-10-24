using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// Xdebug, la conf.d por rama, los ajustes rápidos y activar o quitar extensiones (oleada 3). Sin
/// red ni binarios: la DLL es un array de bytes servido por un handler falso y php.exe es un
/// <see cref="FakeCommandRunner"/> con salidas grabadas de PHP 8.4 y 7.4 de verdad.
/// </summary>
public class PhpXdebugTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-xdebug-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"), WwwDir: Path.Combine(_root, "www"));

    private static readonly byte[] Dll = "MZ esta no es la DLL de Xdebug: son los bytes de los tests de Roost"u8.ToArray();

    // Lo que dice php -v de verdad (8.4.27 con Xdebug 3.5.3).
    private const string PhpV84 = """
        PHP 8.4.27 (cli) (built: Oct 20 2026 04:19:45) (NTS Visual C++ 2022 x64)
        Copyright (c) The PHP Group
        Built by The PHP Group
        Zend Engine v4.4.27, Copyright (c) Zend Technologies
            with Zend OPcache v8.4.27, Copyright (c), by Zend Technologies
            with Xdebug v3.5.3, Copyright (c) 2002-2026, by Derick Rethans
        """;

    private const string PhpV84SinXdebug = """
        PHP 8.4.27 (cli) (built: Oct 20 2026 04:19:45) (NTS Visual C++ 2022 x64)
        Copyright (c) The PHP Group
        Zend Engine v4.4.27, Copyright (c) Zend Technologies
            with Zend OPcache v8.4.27, Copyright (c), by Zend Technologies
        """;

    private sealed class FakeHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    private InstalledPhp Php(string name, params string[] extensions)
    {
        var dir = Path.Combine(Paths.PhpBin, name);
        Directory.CreateDirectory(Path.Combine(dir, "ext"));
        File.WriteAllText(Path.Combine(dir, OsNames.Exe("php")), "");
        File.WriteAllText(Path.Combine(dir, OsNames.Exe("php-cgi")), "");
        foreach (var ext in extensions.DefaultIfEmpty("curl").Concat(["opcache"]))
            File.WriteAllText(Path.Combine(dir, "ext", $"php_{ext}.dll"), "");
        return new InstalledPhp(name, dir, Path.Combine(dir, OsNames.Exe("php")));
    }

    /// <summary>El build de la 8.4 del catálogo, pero con el SHA-256 de los bytes de pega.</summary>
    private static XdebugBuild Fake84(string version = "3.5.3") =>
        XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.4") with
        {
            Version = version,
            FileName = $"php_xdebug-{version}-8.4-nts-vs17-x86_64.dll",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Dll)),
        };

    private string FakeDll(XdebugBuild build)
    {
        var dll = new XdebugInstaller(Paths).DllPath(build);
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, Dll);
        return dll;
    }

    // A12: las zend extensions

    [Fact]
    public void PhpIni_Xdebug_VaComoZendExtensionConSuRuta_DespuesDeOpcache_YConLosAjustesDeSerie()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curl", "opcache" };
        var dll = @"C:\roost\bin\xdebug\3.5.3\php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll";

        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\php", available, @"C:\confd", @"C:\log",
            PhpExtensionChoice.None.With("xdebug", enabled: true), xdebugDll: dll);

        Assert.Contains($"zend_extension=\"{dll}\"\n", ini);
        Assert.DoesNotContain("extension=xdebug", ini.Replace("zend_extension", ""));
        Assert.True(ini.IndexOf("zend_extension=opcache", StringComparison.Ordinal) < ini.IndexOf("[xdebug]", StringComparison.Ordinal));
        Assert.Contains("xdebug.mode = debug,develop\n", ini);
        Assert.Contains("xdebug.start_with_request = trigger\n", ini);
        Assert.Contains("xdebug.client_host = 127.0.0.1\n", ini);
        Assert.Contains("xdebug.client_port = 9003\n", ini);
    }

    [Fact]
    public void PhpIni_UnPhpXdebugDllPuestoAMano_EnExt_NoSeColaComoExtension()
    {
        // Lo que pasaba antes: la app lo enseñaba como una extensión más y escribía extension=xdebug,
        // que Xdebug 3 rechaza («MUST be loaded as a Zend extension»).
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curl", "opcache", "xdebug" };

        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\php", available, @"C:\confd", @"C:\log", PhpExtensionChoice.None.With("xdebug", enabled: true));

        Assert.Contains("zend_extension=xdebug\n", ini);
        Assert.DoesNotContain("\nextension=xdebug", ini);
        Assert.DoesNotContain("\nextension=opcache", ini);
        Assert.All(PhpIni.ZendExtensions, name => Assert.True(PhpIni.IsZendExtension(name.ToUpperInvariant())));
    }

    [Fact]
    public void PhpIni_SinActivarlo_XdebugNoSale_AunqueEsteSuDll()
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curl" };

        var ini = PhpIni.Render("8.4.27-nts-x64", @"C:\php", available, @"C:\confd", @"C:\log", xdebugDll: @"C:\x\php_xdebug.dll");

        Assert.DoesNotContain("xdebug", ini, StringComparison.OrdinalIgnoreCase);
    }

    // F1: el catálogo y el instalador

    [Fact]
    public void Catalogo_UnaDllNtsX64PorRama_DeLa74ALa85_ConElVsDeWindowsPhpNet()
    {
        Assert.Equal(["7.4", "8.0", "8.1", "8.2", "8.3", "8.4", "8.5"], XdebugCatalog.Windows.Select(b => b.PhpBranch));
        Assert.All(XdebugCatalog.Windows, b =>
        {
            Assert.Matches("^[0-9a-f]{64}$", b.Sha256);
            Assert.Contains($"-{b.PhpBranch}-", b.FileName);
            Assert.Contains("-nts-", b.FileName);
            Assert.Contains(b.Compiler, b.FileName);
            Assert.EndsWith("x86_64.dll", b.FileName);
            Assert.Equal($"https://xdebug.org/files/{b.FileName}", b.Url.ToString());
            Assert.InRange(b.Size, 100_000, 2_000_000);
        });
        // La 7.4 se quedó en Xdebug 3.1; las demás, con la última. El VS es el del PHP de esa rama.
        Assert.Equal("3.1.6", XdebugCatalog.Windows[0].Version);
        Assert.Equal("vc15", XdebugCatalog.Windows[0].Compiler);
        Assert.Equal("vs16", XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.3").Compiler);
        Assert.Equal("vs17", XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.4").Compiler);
        Assert.Single(XdebugCatalog.Windows.Where(b => b.PhpBranch != "7.4").Select(b => b.Version).Distinct());
    }

    [WindowsFact("Solo en Windows: la DLL de xdebug.org (en Linux Xdebug va dentro de cada PHP de roost-builds).")]
    public void Catalogo_SoloNtsX64_YDiceElPorQue()
    {
        Assert.Equal("3.1.6", XdebugCatalog.For(Php("7.4.33-nts-x64"))?.Version);
        Assert.Equal("8.4", XdebugCatalog.For(Php("8.4.27-nts-x64"))?.PhpBranch);
        Assert.Null(XdebugCatalog.For(Php("8.4.27-ts-x64")));
        Assert.Contains("TS", XdebugCatalog.WhyNot(Php("8.4.27-ts-x64")));
        Assert.Contains("64 bits", XdebugCatalog.WhyNot(Php("8.4.27-nts-x86")));
        Assert.Contains("7.4 a la 8.5", XdebugCatalog.WhyNot(Php("7.3.33-nts-x64")));
    }

    [Fact]
    public async Task Instalador_BajaVerificaYDejaLaDllEnBinXdebugVersion_YNoVuelveABajarla()
    {
        var build = Fake84();
        var handler = new FakeHandler(Dll);
        var installer = new XdebugInstaller(Paths, new HttpClient(handler));

        var dll = await installer.InstallAsync(build);

        Assert.Equal(Path.Combine(_root, "bin", "xdebug", "3.5.3", build.FileName), dll);
        Assert.Equal(Dll, File.ReadAllBytes(dll));
        Assert.Equal([build.Url], handler.Requests);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(dll)!));   // ni .tmp-…
        Assert.Equal(dll, installer.Find(build));

        await installer.InstallAsync(build);
        Assert.Single(handler.Requests);   // ya estaba y cuadra: no baja nada
    }

    [Fact]
    public async Task Instalador_ConOtroSha256_NoDejaNada()
    {
        var build = Fake84() with { Sha256 = new string('0', 64) };
        var installer = new XdebugInstaller(Paths, new HttpClient(new FakeHandler(Dll)));

        var ex = await Assert.ThrowsAsync<RoostException>(() => installer.InstallAsync(build));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Null(installer.Find(build));
        Assert.False(Directory.Exists(Paths.Downloads) && Directory.EnumerateFiles(Paths.Downloads).Any());
        var dir = Path.Combine(_root, "bin", "xdebug", "3.5.3");
        Assert.True(!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any());
    }

    [WindowsFact("Solo en Windows: la DLL de xdebug.org (en Linux Xdebug va dentro de cada PHP de roost-builds).")]
    public void Locate_LaFijada_OLaMasNuevaDeSuRama_NuncaParaUnaTs()
    {
        var php = Php("8.4.27-nts-x64");
        var installer = new XdebugInstaller(Paths);
        Assert.Null(installer.Locate(php));

        // Una Roost nueva fija otra versión: la vieja de la 8.4 se sigue usando hasta que se baje la nueva.
        var old = FakeDll(Fake84("3.4.7"));
        FakeDll(Fake84("3.4.7") with { PhpBranch = "8.3", FileName = "php_xdebug-3.4.7-8.3-nts-vs16-x86_64.dll" });
        Assert.Equal(old, installer.Locate(php));

        var pinned = FakeDll(XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.4"));
        Assert.Equal(pinned, installer.Locate(php));
        Assert.Null(installer.Locate(Php("8.4.27-ts-x64")));
        Assert.Null(installer.Locate(Php("8.2.30-nts-x64")));
    }

    // F2: la conf.d de la rama y los ajustes rápidos

    [Fact]
    public void ConfDDeLaRama_EnElPoolYEnLaTerminal_DespuesDeLaComun()
    {
        var php84 = Php("8.4.27-nts-x64");
        var nginx = new InstalledNginx("1.30.6", Path.Combine(_root, "bin", "nginx", "1.30.6"));
        var planner = new StackPlanner(Paths, new NginxTestOk());
        var common = Path.Combine(Paths.Etc, "php", "conf.d");
        var branch = Path.Combine(common, "8.4");
        // PHP separa las carpetas de PHP_INI_SCAN_DIR con «;» en Windows y con «:» en Linux.
        var scanDir = common + Path.PathSeparator + branch;

        var plan = planner.Prepare(new RoostConfig { HttpPort = 8765, PhpPool = 1 }, php84, nginx);

        Assert.Equal(scanDir, planner.PhpScanDir(php84));
        Assert.Equal(scanDir, plan.Services.Single(s => s.Name == "php-8.4-0").Environment["PHP_INI_SCAN_DIR"]);
        Assert.Equal(scanDir, TerminalPhp.Environment(Paths, php84)["PHP_INI_SCAN_DIR"]);
        Assert.True(File.Exists(Path.Combine(common, PhpIni.UserFragmentName)));
        Assert.Contains("solo para PHP 8.4", File.ReadAllText(Path.Combine(branch, PhpIni.UserFragmentName)));
        Assert.Contains(Path.Combine(branch, PhpIni.UserFragmentName), File.ReadAllText(planner.PhpIniPath(php84)));
    }

    [Fact]
    public void ConfDDeLaRama_ElFragmentoDelUsuarioNoSeTocaSiYaExiste()
    {
        var php = Php("7.4.33-nts-x64");
        var planner = new StackPlanner(Paths, ProcessRunner.Default);
        var file = planner.EnsureBranchUserIni(php);
        File.WriteAllText(file, "extension=redis\n");

        planner.WritePhpIni(php);

        Assert.Equal(Path.Combine(Paths.Etc, "php", "conf.d", "7.4", PhpIni.UserFragmentName), file);
        Assert.Equal("extension=redis\n", File.ReadAllText(file));
    }

    [Fact]
    public void AjustesRapidos_SeValidan_PostMaxSizeCoherenteConLaSubida()
    {
        Assert.Null(PhpSettings.Default.Validate());
        Assert.Null(new PhpSettings("-1", "2G", "0", 0).Validate());          // sin límites
        Assert.Null(new PhpSettings("1g", "256m", "256M", 300).Validate());   // unidades en minúscula
        Assert.Contains("memory_limit", new PhpSettings("mucho", "128M", "128M", 120).Validate());
        Assert.Contains("32M", new PhpSettings("16M", "8M", "8M", 120).Validate());
        Assert.Contains("upload_max_filesize", new PhpSettings("512M", "0", "128M", 120).Validate());
        Assert.Contains("al menos upload_max_filesize", new PhpSettings("512M", "300M", "128M", 120).Validate());
        Assert.Contains("max_execution_time", new PhpSettings("512M", "128M", "128M", -1).Validate());
        Assert.Contains("max_execution_time", new PhpSettings("512M", "128M", "128M", 100_000).Validate());
        Assert.Equal(2L * 1024 * 1024 * 1024, PhpSettings.Bytes("2g"));
        Assert.Equal(4096, PhpSettings.Bytes("4096"));
        Assert.Null(PhpSettings.Bytes("1.5G"));
        Assert.Null(PhpSettings.Bytes("99999999G"));
    }

    [Fact]
    public void AjustesRapidos_SoloGuardaLoQueSeAparta_VanAlPhpIni_YDeSerieBorraElFichero()
    {
        var php = Php("8.4.27-nts-x64");
        var configurator = new PhpConfigurator(Paths, new FakeCommandRunner());
        var file = configurator.Planner.PhpSettingsPath(php);

        var ini = File.ReadAllText(configurator.SetSettings(php, PhpSettings.Default with { MemoryLimit = "1g", UploadMaxFilesize = "256M", PostMaxSize = "256M" }));

        Assert.Contains("memory_limit = 1G", ini);
        Assert.Contains("upload_max_filesize = 256M", ini);
        Assert.Contains("post_max_size = 256M", ini);
        Assert.Contains("max_execution_time = 120", ini);
        var stored = File.ReadAllText(file);
        Assert.Contains("\"memory_limit\": \"1G\"", stored);
        Assert.DoesNotContain("max_execution_time", stored);
        Assert.Equal("1G", configurator.Settings(php).MemoryLimit);

        Assert.Throws<RoostException>(() => configurator.SetSettings(php, PhpSettings.Default with { PostMaxSize = "1M" }));
        Assert.Equal("1G", configurator.Settings(php).MemoryLimit);   // lo que no valida no se guarda

        configurator.SetSettings(php, PhpSettings.Default);
        Assert.False(File.Exists(file));
        File.WriteAllText(file, "{ roto");
        Assert.Equal(PhpSettings.Default, configurator.Settings(php));
    }

    // F3: activar y quitar, comprobando con php -v

    [Fact]
    public async Task Extension_SeActivaSiPhpLaCarga_YSeQuita()
    {
        var php = Php("8.4.27-nts-x64", "curl", "soap");
        var runner = new FakeCommandRunner().On("-v", stdout: PhpV84SinXdebug);
        var configurator = new PhpConfigurator(Paths, runner);

        var check = await configurator.SetExtensionAsync(php, "php_soap.dll", enabled: true);

        Assert.True(check.Ran);
        Assert.Empty(check.Warnings);
        Assert.Contains("extension=soap\n", File.ReadAllText(configurator.Planner.PhpIniPath(php)));
        Assert.Equal(["soap"], configurator.Choice(php).On);
        var spec = Assert.Single(runner.Calls);
        Assert.Equal(php.Executable, spec.Exe);
        Assert.Equal(configurator.Planner.PhpScanDir(php), spec.Environment!["PHP_INI_SCAN_DIR"]);

        await configurator.SetExtensionAsync(php, "soap", enabled: false);
        Assert.DoesNotContain("soap", File.ReadAllText(configurator.Planner.PhpIniPath(php)));
        Assert.Empty(configurator.Choice(php).On);
    }

    [Fact]
    public async Task Extension_QuePhpNoPuedeCargar_SeDejaComoEstaba_YSeDiceElPorQue()
    {
        var php = Php("8.4.27-nts-x64", "curl", "pdo_firebird");
        // Lo que dice PHP 8.4 si a php_pdo_firebird.dll le falta fbclient.dll.
        var runner = new FakeCommandRunner().On("-v",
            stdout: "\nWarning: PHP Startup: Unable to load dynamic library 'pdo_firebird' (tried: C:\\php\\ext\\pdo_firebird (No se puede encontrar el módulo especificado), C:\\php\\ext\\php_pdo_firebird.dll (No se puede encontrar el módulo especificado)) in Unknown on line 0\n" + PhpV84SinXdebug,
            stderr: "PHP Warning:  PHP Startup: Unable to load dynamic library 'pdo_firebird' (tried: C:\\php\\ext\\pdo_firebird (No se puede encontrar el módulo especificado), C:\\php\\ext\\php_pdo_firebird.dll (No se puede encontrar el módulo especificado)) in Unknown on line 0\n");
        var configurator = new PhpConfigurator(Paths, runner);

        var ex = await Assert.ThrowsAsync<RoostException>(() => configurator.SetExtensionAsync(php, "pdo_firebird", enabled: true));

        Assert.Contains("no puede cargar pdo_firebird", ex.Message);
        Assert.Contains("Unable to load dynamic library 'pdo_firebird'", ex.Message);
        Assert.Empty(configurator.Choice(php).On);
        Assert.DoesNotContain("firebird", File.ReadAllText(configurator.Planner.PhpIniPath(php)));
    }

    [Fact]
    public async Task Extension_LaQueNoTraeElBuild_OXdebug_NoVanPorAqui()
    {
        var php = Php("8.4.27-nts-x64", "curl");
        var configurator = new PhpConfigurator(Paths, new FakeCommandRunner());

        var missing = await Assert.ThrowsAsync<RoostException>(() => configurator.SetExtensionAsync(php, "ldap", enabled: true));
        Assert.Contains("no trae la extension 'ldap'", missing.Message);
        Assert.Contains("curl, opcache", missing.Message);
        var xdebug = await Assert.ThrowsAsync<RoostException>(() => configurator.SetExtensionAsync(php, "xdebug", enabled: true));
        Assert.Contains("roost php xdebug", xdebug.Message);
    }

    [WindowsFact("Solo en Windows: la DLL de xdebug.org (en Linux Xdebug va dentro de cada PHP de roost-builds).")]
    public async Task Xdebug_SeEnciendeSiCarga_YSeApaga()
    {
        var php = Php("8.4.27-nts-x64");
        var dll = FakeDll(XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.4"));
        var configurator = new PhpConfigurator(Paths, new FakeCommandRunner().On("-v", stdout: PhpV84));

        var check = await configurator.SetXdebugAsync(php, enabled: true);

        Assert.Equal("3.5.3", check.XdebugVersion);
        Assert.True(configurator.XdebugOn(php));
        Assert.Contains($"zend_extension=\"{dll}\"", File.ReadAllText(configurator.Planner.PhpIniPath(php)));
        Assert.Contains(configurator.Extensions(php), e => e is { Name: "xdebug", On: true, Zend: true, ByDefault: false });

        await configurator.SetXdebugAsync(php, enabled: false);
        Assert.False(configurator.XdebugOn(php));
        var ini = File.ReadAllText(configurator.Planner.PhpIniPath(php));
        Assert.DoesNotContain("[xdebug]", ini);
        Assert.DoesNotContain(dll, ini);
        Assert.DoesNotContain("xdebug.mode", ini);
    }

    [WindowsFact("Solo en Windows: la DLL de xdebug.org (en Linux Xdebug va dentro de cada PHP de roost-builds).")]
    public async Task Xdebug_SinSuDll_SiNoCarga_OEnUnaTs_SeQuedaApagado()
    {
        var php = Php("8.4.27-nts-x64");
        var runner = new FakeCommandRunner().On("-v", stdout: PhpV84SinXdebug);
        var configurator = new PhpConfigurator(Paths, runner);

        var notInstalled = await Assert.ThrowsAsync<RoostException>(() => configurator.SetXdebugAsync(php, enabled: true));
        Assert.Contains("roost php xdebug on lo baja", notInstalled.Message);

        // php -v no lo nombra: se deshace.
        FakeDll(XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.4"));
        var notLoaded = await Assert.ThrowsAsync<RoostException>(() => configurator.SetXdebugAsync(php, enabled: true));
        Assert.Contains("no ha cargado Xdebug", notLoaded.Message);
        Assert.False(configurator.XdebugOn(php));
        Assert.DoesNotContain("[xdebug]", File.ReadAllText(configurator.Planner.PhpIniPath(php)));

        // PHP dice que no puede cargar la DLL: se deshace con su mensaje.
        runner.On("-v", stdout: PhpV84SinXdebug, stderr: @"Failed loading C:\roost\bin\xdebug\3.5.3\php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll:  (null)" + "\n");
        var failed = await Assert.ThrowsAsync<RoostException>(() => configurator.SetXdebugAsync(php, enabled: true));
        Assert.Contains("no puede cargar xdebug", failed.Message);
        Assert.Contains("Failed loading", failed.Message);
        Assert.False(configurator.XdebugOn(php));

        var ts = await Assert.ThrowsAsync<RoostException>(() => configurator.SetXdebugAsync(Php("8.4.27-ts-x64"), enabled: true));
        Assert.Contains("TS", ts.Message);
    }

    [Fact]
    public async Task SiPhpExeNoArranca_ElCambioSeQueda_PeroNoSeDaPorComprobado()
    {
        var php = Php("8.4.27-nts-x64", "curl", "soap");
        var configurator = new PhpConfigurator(Paths, new FakeCommandRunner().On("-v", exitCode: -1, timedOut: true));

        var check = await configurator.SetExtensionAsync(php, "soap", enabled: true);

        Assert.False(check.Ran);
        Assert.Equal(["soap"], configurator.Choice(php).On);
    }

    // Los parsers, con lo que escriben PHP 8.4 y 7.4 de verdad

    [Fact]
    public void StartupErrors_Los8x_Y_Los7_4_SinRepetir_NiPrefijos()
    {
        var output = """

            Warning: PHP Startup: Unable to load dynamic library 'noexiste' (tried: C:\php\ext\noexiste (No se puede encontrar el módulo especificado)) in Unknown on line 0
            PHP Warning:  PHP Startup: Unable to load dynamic library 'noexiste' (tried: C:\php\ext\noexiste (No se puede encontrar el módulo especificado)) in Unknown on line 0
            Failed loading C:\roost\bin\xdebug\3.5.3\php_xdebug.dll:  (null)
            PHP Warning:  Failed to load C:\roost\bin\xdebug\3.1.6\php_xdebug.dll, El sistema no puede encontrar el archivo especificado.
             in Unknown on line 0
            PHP Warning:  Xdebug MUST be loaded as a Zend extension in Unknown on line 0
            PHP 8.4.27 (cli)
            """;

        var errors = PhpConfigurator.StartupErrors(output);

        Assert.Equal(
        [
            @"Unable to load dynamic library 'noexiste' (tried: C:\php\ext\noexiste (No se puede encontrar el módulo especificado))",
            @"Failed loading C:\roost\bin\xdebug\3.5.3\php_xdebug.dll:  (null)",
            @"Failed to load C:\roost\bin\xdebug\3.1.6\php_xdebug.dll, El sistema no puede encontrar el archivo especificado.",
            "Xdebug MUST be loaded as a Zend extension",
        ], errors);
        Assert.Empty(PhpConfigurator.StartupErrors(PhpV84));
    }

    [Fact]
    public void XdebugVersion_YModulos_DeLaSalidaDePhp()
    {
        Assert.Equal("3.5.3", PhpConfigurator.XdebugVersion(PhpV84));
        Assert.Equal("3.1.6", PhpConfigurator.XdebugVersion("    with Xdebug v3.1.6, Copyright (c) 2002-2022, by Derick Rethans"));
        Assert.Null(PhpConfigurator.XdebugVersion(PhpV84SinXdebug));
        Assert.Equal(["Core", "curl", "xdebug", "Zend OPcache"],
            PhpConfigurator.Modules("[PHP Modules]\r\nCore\r\ncurl\r\nxdebug\r\nZend OPcache\r\n\r\n[Zend Modules]\r\nXdebug\r\nZend OPcache\r\n"));
        Assert.Equal("soap", PhpConfigurator.NormalizeName(" PHP_Soap.DLL "));
    }

    [Fact]
    public void LaunchJson_DeVsCode_EscuchaEn9003_YLanzaElScriptConElPhpDeRoost()
    {
        var php = Php("8.4.27-nts-x64");
        var json = new PhpConfigurator(Paths, new FakeCommandRunner()).VsCodeLaunchJson(php);

        using var doc = JsonDocument.Parse(json);
        var configs = doc.RootElement.GetProperty("configurations").EnumerateArray().ToList();
        Assert.Equal(2, configs.Count);
        Assert.Equal(9003, configs[0].GetProperty("port").GetInt32());
        Assert.False(configs[0].TryGetProperty("pathMappings", out _));   // todo es local
        Assert.Equal("${file}", configs[1].GetProperty("program").GetString());
        Assert.Equal(php.Executable, configs[1].GetProperty("runtimeExecutable").GetString());
        var env = configs[1].GetProperty("env");
        Assert.Equal(Path.Combine(Paths.Etc, "php", "8.4.27-nts-x64"), env.GetProperty("PHPRC").GetString());
        Assert.Equal(new StackPlanner(Paths, ProcessRunner.Default).PhpScanDir(php), env.GetProperty("PHP_INI_SCAN_DIR").GetString());
        Assert.Equal("client_port=${port}", env.GetProperty("XDEBUG_CONFIG").GetString());
    }

    /// <summary>nginx -t falso: siempre da por buena la configuración.</summary>
    private sealed class NginxTestOk : IProcessRunner
    {
        public string? Run(string executablePath, string arguments, TimeSpan timeout) => "nginx: configuration file test is successful";
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
