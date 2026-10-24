using System.Text.Json;
using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>
/// K4: las mitigaciones frente al php.ini efectivo. <c>php -i</c> está grabado de un PHP 8.5.12 de
/// Roost (solo cambian las rutas y el nombre del equipo); el php.exe es un fichero vacío y
/// <see cref="FakeCommandRunner"/> contesta por él, así que nada se ejecuta.
/// </summary>
public class AuditMitigationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-mitig-" + Guid.NewGuid().ToString("N"));
    private readonly RoostPaths _paths;
    private readonly string _php85;

    public AuditMitigationsTests()
    {
        _paths = new RoostPaths(Path.Combine(_root, "roost"), Path.Combine(_root, "cache"));
        _php85 = Path.Combine(_paths.PhpBin, "8.5.12-nts-x64", OsNames.Exe("php"));
        Directory.CreateDirectory(Path.GetDirectoryName(_php85)!);
        File.WriteAllText(_php85, "");
    }

    /// <summary>El <c>php -i</c> grabado (recortado a lo que importa), con las rutas de <see cref="_paths"/>.</summary>
    private string PhpI(string uploadProgress = "On", string exposePhp = "On", string memory = "512M", bool withOurs = false, bool withUser = true)
    {
        var etc = Path.Combine(_paths.Root, "etc", "php");
        var files = new List<string> { Path.Combine(etc, "conf.d", "99-usuario.ini") };
        if (withOurs) files.Add(Path.Combine(etc, "conf.d", "8.5", PhpMitigations.FileName));
        if (withUser) files.Add(Path.Combine(etc, "conf.d", "8.5", "99-usuario.ini"));
        return $"""
            phpinfo()
            PHP Version => 8.5.12

            System => Windows NT PORTATIL 10.0 build 26200 (Windows 11) AMD64
            Build Date => Oct 20 2026 04:19:56
            Compiler => Visual C++ 2022
            Architecture => x64
            Server API => Command Line Interface
            Configuration File (php.ini) Path =>
            Loaded Configuration File => {Path.Combine(etc, "8.5.12-nts-x64", "php.ini")}
            Scan this dir for additional .ini files => {Path.Combine(etc, "conf.d")};{Path.Combine(etc, "conf.d", "8.5")}
            Additional .ini files parsed => {string.Join(",\r\n", files)}

            PHP API => 20250925

            Core

            PHP Version => 8.5.12

            Directive => Local Value => Master Value
            allow_url_include => Off => Off
            disable_functions => no value => no value
            display_errors => STDOUT => STDOUT
            expose_php => {exposePhp} => {exposePhp}
            max_execution_time => 0 => 0
            memory_limit => {memory} => {memory}
            post_max_size => 128M => 128M
            upload_max_filesize => 128M => 128M

            session

            Session Support => enabled
            Registered save handlers => files user

            Directive => Local Value => Master Value
            session.cookie_httponly => Off => Off
            session.upload_progress.enabled => {uploadProgress} => {uploadProgress}
            session.upload_progress.name => PHP_SESSION_UPLOAD_PROGRESS => PHP_SESSION_UPLOAD_PROGRESS
            """.Replace("\n", "\r\n");
    }

    // ---- php -i --------------------------------------------------------------------------------

    [WindowsFact("Pendiente en Linux: Builds y PHP (L1).")]
    public void PhpI_DaLosValoresLocales_YLosFicherosEnElOrdenEnQuePhpLosLee()
    {
        var s = PhpIniSnapshot.Parse(PhpI(withOurs: true))!;

        Assert.Equal("On", s.Values["session.upload_progress.enabled"]);
        Assert.Equal("512M", s.Values["memory_limit"]);
        Assert.Equal("", s.Values["disable_functions"]);   // «no value»
        Assert.False(s.Values.ContainsKey("Directive"));
        Assert.False(s.Values.ContainsKey("Session Support"));
        Assert.Equal(
            ["8.5.12-nts-x64\\php.ini", "conf.d\\99-usuario.ini", "conf.d\\8.5\\50-roost-mitigaciones.ini", "conf.d\\8.5\\99-usuario.ini"],
            s.Files.Select(f => Path.GetRelativePath(Path.Combine(_paths.Root, "etc", "php"), f)));
        Assert.Null(PhpIniSnapshot.Parse("'php' no se reconoce como un comando"));
        Assert.Empty(PhpIniSnapshot.Parse("PHP Version => 8.5.12\nLoaded Configuration File => (none)\nAdditional .ini files parsed => (none)\n")!.Files);
    }

    [Theory]
    [InlineData("=", "0", "Off", true)]
    [InlineData("=", "0", "On", false)]
    [InlineData("=", "Off", "", true)]          // un sí/no apagado puede salir vacío
    [InlineData("=", "1", "On", true)]
    [InlineData("=", "Lax", "lax", true)]
    [InlineData("=", "128M", "134217728", true)]   // tamaños en bytes
    [InlineData("<=", "128M", "512M", false)]
    [InlineData("<=", "128M", "128M", true)]
    [InlineData("<=", "128M", "64M", true)]
    [InlineData("<=", "128M", "-1", false)]     // sin límite es más que cualquier cosa
    [InlineData(">=", "30", "-1", true)]
    [InlineData(">=", "30", "10", false)]
    [InlineData("includes", "exec,system", "exec, shell_exec,system", true)]
    [InlineData("includes", "exec", "", false)]
    public void Satisface_ComparaSiNoComoSiNoYTamanosEnBytes(string compare, string expected, string effective, bool ok)
        => Assert.Equal(ok, MitigationCheck.Satisfies(compare, expected, effective));

    [Theory]
    [InlineData("<=", "mucho", "512M")]
    [InlineData("raro", "x", "x")]
    public void Satisface_LoQueNoSePuedeComparar_NoSeSabe(string compare, string expected, string effective)
        => Assert.Null(MitigationCheck.Satisfies(compare, expected, effective));

    [Fact]
    public void LoQueLaTerminalFijaOLoQueNoExiste_NoSeSabe()
    {
        var s = PhpIniSnapshot.Parse(PhpI())!;

        Assert.Equal(MitigationStatus.Unknown, MitigationCheck.Check(new Mitigation { Ini = "max_execution_time", Value = "30", Compare = "<=" }, s).Status);
        var missing = MitigationCheck.Check(new Mitigation { Ini = "xdebug.mode", Value = "off" }, s);
        Assert.Equal(MitigationStatus.Unknown, missing.Status);
        Assert.Contains("php -i no da esta directiva", missing.Detail);
    }

    // ---- El fichero de Roost en el conf.d de la rama ------------------------------------------

    [Fact]
    public void ElFicheroDeMitigaciones_SeLeeComoSeEscribe_YLleva()
    {
        var entries = new List<PhpMitigations.Entry>
        {
            new("session.upload_progress.enabled", "0", "CVE-2026-109619 (PHP2Shell)"),
            new("disable_functions", "exec,system", "CVE-1"),
            new("error_log", "C:\\ruta con espacios\\php.log", ""),
        };
        var text = PhpMitigations.Render("8.5", entries);

        Assert.Contains("conf.d\\8.5\\99-usuario.ini, que manda sobre esto", text);
        Assert.Contains("; por: CVE-2026-109619 (PHP2Shell)\nsession.upload_progress.enabled = 0\n", text);
        Assert.Contains("error_log = \"C:\\ruta con espacios\\php.log\"", text);
        Assert.Equal(entries, PhpMitigations.Parse(text));
    }

    [Theory]
    [InlineData("session.upload_progress.enabled", "0", null, true)]
    [InlineData("memory_limit", "128M", "<=", true)]
    [InlineData("disable_functions", "exec,system", "includes", true)]
    [InlineData("memory_limit", "128M (razonable)", null, false)]   // eso es un texto, no un valor
    [InlineData("memory_limit", "1\nextension=evil", null, false)]
    [InlineData("memory_limit", "1; extension=evil", null, false)]
    [InlineData("open_basedir", "${HOME}", null, false)]
    [InlineData("x\"y", "1", null, false)]
    [InlineData("memory_limit", "128M", "~", false)]
    public void SoloSeEscribeLoQueNoRompeUnPhpIni(string ini, string value, string? compare, bool ok)
        => Assert.Equal(ok, PhpMitigations.IsWritable(new Mitigation { Ini = ini, Value = value, Compare = compare }));

    [Fact]
    public void Includes_SeEscribeLoQueHabiaMasLoQueFalta()
    {
        var m = new Mitigation { Ini = "disable_functions", Value = "exec,system", Compare = "includes" };
        Assert.Equal("passthru,system,exec", PhpMitigations.ValueToWrite(m, "passthru, system"));
        Assert.Equal("exec,system", PhpMitigations.ValueToWrite(m, ""));
    }

    // ---- Comprobar, aplicar y deshacer ----------------------------------------------------------

    private static readonly DateOnly Today = new(2026, 10, 24);

    /// <summary>Un aviso de prueba que afecta a la 8.5.12 (la última), con dos mitigaciones.</summary>
    private static AuditDataset Dataset(bool kev = false) => new()
    {
        Products = new() { ["php"] = new ProductInfo { Branches = new() { ["8.5"] = new BranchInfo { Latest = "8.5.13", Status = "active" } } } },
        Advisories =
        [
            new Advisory
            {
                Id = "CVE-2026-777", Aliases = ["PRUEBA"], Product = "php", Affected = [">=8.5.0 <8.5.13"], Cvss = 8.1, Kev = kev,
                FixedIn = new() { ["8.5"] = "8.5.13" },
                Mitigations =
                [
                    new Mitigation { Ini = "session.upload_progress.enabled", Value = "0" },
                    new Mitigation { Ini = "expose_php", Value = "Off" },
                ],
            },
        ],
    };

    private AuditFinding Finding(string path, string source, bool kev = false) =>
        new AuditEvaluator(Dataset(kev), Today).Evaluate(new DetectedComponent
        {
            Kind = ComponentKind.Php,
            Version = new ComponentVersion(8, 5, 12, "PHP 8.5.12 (cli)"),
            ExecutablePath = path,
            Source = source,
        });

    [Fact]
    public async Task EnUnPhpDeRoost_SeComparaConPhpI_SeAplicaEnSuRama_YSeDeshace()
    {
        var runner = new FakeCommandRunner().On("-i", stdout: PhpI());
        var mitigations = new AuditMitigations(_paths, runner);

        // Sin mitigar: rojo, y las dos se pueden aplicar.
        var red = await mitigations.CheckAsync(Finding(_php85, "Roost"));
        Assert.Equal(AuditColor.Red, red.Color);
        Assert.True(red.IsBlocking);
        Assert.All(red.MitigationStates, s => Assert.Equal((MitigationStatus.Unmitigated, true, false), (s.Status, s.CanApply, s.AppliedByRoost)));
        Assert.Equal("On", red.StateOf(red.Mitigations[0]).Effective);
        Assert.Equal(3, red.IniFiles.Count);   // el php.ini generado y los 99-usuario.ini de las dos conf.d
        var spec = Assert.Single(runner.Calls);
        Assert.Equal(_php85, spec.Exe);
        Assert.Equal(Path.Combine(_paths.Root, "etc", "php", "8.5.12-nts-x64"), spec.Environment!["PHPRC"]);   // con el entorno de Roost
        Assert.EndsWith(Path.Combine("conf.d", "8.5"), spec.Environment["PHP_INI_SCAN_DIR"]);

        // Aplicar: en conf.d\8.5\50-roost-mitigaciones.ini, nunca en un 99-usuario.ini.
        var userIni = Path.Combine(_paths.Root, "etc", "php", "conf.d", "8.5", "99-usuario.ini");
        var userBefore = File.ReadAllText(userIni);
        var change = mitigations.Apply(red);
        Assert.Equal(Path.Combine(_paths.Root, "etc", "php", "conf.d", "8.5", PhpMitigations.FileName), change.File);
        Assert.Equal(["session.upload_progress.enabled", "expose_php"], change.Keys);
        Assert.Equal("8.5", change.Branch);
        Assert.Equal(userBefore, File.ReadAllText(userIni));
        Assert.Equal(
            [new PhpMitigations.Entry("session.upload_progress.enabled", "0", "CVE-2026-777 (PRUEBA)"), new PhpMitigations.Entry("expose_php", "Off", "CVE-2026-777 (PRUEBA)")],
            PhpMitigations.Read(change.File));

        // Volver a comprobar: PHP ya las aplica, todo el aviso está mitigado y baja a ámbar.
        runner.On("-i", stdout: PhpI(uploadProgress: "Off", exposePhp: "Off", withOurs: true));
        var amber = await mitigations.CheckAsync(red);
        Assert.Equal(AuditColor.Amber, amber.Color);
        Assert.True(amber.Mitigated);
        Assert.False(amber.IsBlocking);
        Assert.StartsWith("Mitigada por configuracion", amber.Headline);
        Assert.All(amber.MitigationStates, s => Assert.Equal((MitigationStatus.Mitigated, false, true), (s.Status, s.CanApply, s.AppliedByRoost)));
        Assert.Same(red.BeforeMitigations, amber.BeforeMitigations);

        // El usuario la pisa en el 99-usuario.ini de su rama: sin mitigar, y dice quién.
        File.AppendAllText(userIni, "session.upload_progress.enabled = On\n");
        runner.On("-i", stdout: PhpI(uploadProgress: "On", exposePhp: "Off", withOurs: true));
        var overridden = await mitigations.CheckAsync(amber);
        Assert.Equal(AuditColor.Red, overridden.Color);
        var state = overridden.StateOf(overridden.Mitigations[0]);
        Assert.Equal((MitigationStatus.Unmitigated, false, true), (state.Status, state.CanApply, state.AppliedByRoost));
        Assert.Equal($"aplicada por Roost, pero la pisa {userIni}", state.Detail);

        // Deshacer: quita las suyas y, sin ninguna, el fichero.
        var undo = mitigations.Undo(overridden);
        Assert.Equal(["session.upload_progress.enabled", "expose_php"], undo.Keys);
        Assert.False(File.Exists(change.File));
    }

    [Fact]
    public async Task UnoEnKev_SigueEnRojoAunqueEsteMitigado()
    {
        var runner = new FakeCommandRunner().On("-i", stdout: PhpI(uploadProgress: "Off", exposePhp: "Off"));

        var f = await new AuditMitigations(_paths, runner).CheckAsync(Finding(_php85, "Roost", kev: true));

        Assert.All(f.MitigationStates, s => Assert.Equal(MitigationStatus.Mitigated, s.Status));
        Assert.Equal(AuditColor.Red, f.Color);
        Assert.False(f.Mitigated);
        Assert.Contains(f.Notes, n => n.Contains("KEV") && n.Contains("CVE-2026-777"));
    }

    [Fact]
    public async Task UnPhpDeFuera_SoloSeLee_ConSuPhpIni_YNoBajaDeRojo()
    {
        var laragon = Path.Combine(_root, "laragon", "bin", "php", "php-8.5.12-nts-Win32-vs17-x64", OsNames.Exe("php"));
        Directory.CreateDirectory(Path.GetDirectoryName(laragon)!);
        File.WriteAllText(laragon, "");
        var runner = new FakeCommandRunner().On("-i", stdout: PhpI(uploadProgress: "Off", exposePhp: "Off"));
        var mitigations = new AuditMitigations(_paths, runner);

        var f = await mitigations.CheckAsync(Finding(laragon, "Laragon"));

        var spec = Assert.Single(runner.Calls);
        Assert.Equal(laragon, spec.Exe);
        Assert.Equal(Path.GetDirectoryName(laragon), spec.WorkingDirectory);   // ahí busca su php.ini
        Assert.True(spec.Environment!.ContainsKey("PHPRC") && spec.Environment["PHPRC"] is null);
        Assert.True(spec.Environment.ContainsKey("PHP_INI_SCAN_DIR") && spec.Environment["PHP_INI_SCAN_DIR"] is null);
        Assert.All(f.MitigationStates, s => Assert.Equal((MitigationStatus.Mitigated, false), (s.Status, s.CanApply)));
        Assert.Equal(AuditColor.Red, f.Color);   // el servidor web de Laragon puede leer otro php.ini
        Assert.Contains(f.Notes, n => n.Contains("terminal"));
        Assert.Throws<RoostException>(() => mitigations.Apply(f));
        Assert.False(Directory.Exists(Path.Combine(_paths.Root, "etc", "php", "conf.d")));   // nada escrito
    }

    [Fact]
    public async Task SiPhpNoContesta_QuedanSinComprobar_YSeDicePorQue()
    {
        var runner = new FakeCommandRunner().On("-i", exitCode: -1, timedOut: true);

        var f = await new AuditMitigations(_paths, runner).CheckAsync(Finding(_php85, "Roost"));

        Assert.All(f.MitigationStates, s => Assert.Equal(MitigationStatus.Unchecked, s.Status));
        Assert.Equal("php -i no ha contestado a tiempo", f.MitigationStates[0].Detail);
        Assert.Equal(AuditColor.Red, f.Color);
        Assert.Empty(f.IniFiles);
    }

    [Fact]
    public async Task ElJson_AnadeElEstado_SinQuitarNadaDeLoDeAntes()
    {
        var runner = new FakeCommandRunner().On("-i", stdout: PhpI(exposePhp: "Off"));
        var f = await new AuditMitigations(_paths, runner).CheckAsync(Finding(_php85, "Roost"));

        using var doc = JsonDocument.Parse(AuditReport.ToJson(Dataset(), [f], new JsonSerializerOptions()));
        var finding = doc.RootElement.GetProperty("findings")[0];
        var m = finding.GetProperty("mitigations");
        Assert.Equal("session.upload_progress.enabled", m[0].GetProperty("ini").GetString());   // lo de antes
        Assert.Equal("0", m[0].GetProperty("value").GetString());
        Assert.Equal("unmitigated", m[0].GetProperty("status").GetString());
        Assert.Equal("On", m[0].GetProperty("effective").GetString());
        Assert.True(m[0].GetProperty("canApply").GetBoolean());
        Assert.Equal("mitigated", m[1].GetProperty("status").GetString());
        Assert.Equal("=", m[1].GetProperty("compare").GetString());
        Assert.False(finding.GetProperty("mitigated").GetBoolean());
        Assert.Equal(3, finding.GetProperty("iniFiles").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("blocking").GetBoolean());

        var md = AuditReport.ToMarkdown(Dataset(), [f], new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero), "PORTATIL");
        Assert.Contains("- `session.upload_progress.enabled = 0` · **sin mitigar** (vale `On`)", md);
        Assert.Contains("- `expose_php = Off` · **mitigada** (vale `Off`)", md);
        Assert.Contains("PHP lee, en este orden:", md);
    }

    [Fact]
    public void ElDatasetEmpaquetado_TraeLaMitigacionDeMemoriaComparable()
    {
        var memory = AuditDataset.LoadEmbedded().Advisories.Single(a => a.Id == "CVE-2026-109618").Mitigations.Single();
        Assert.Equal(("memory_limit", "128M", "<="), (memory.Ini, memory.Value, memory.Compare));
        Assert.True(PhpMitigations.IsWritable(memory));
        Assert.Equal("memory_limit <= 128M", memory.Setting);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
