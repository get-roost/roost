using Avalonia.Headless.XUnit;
using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Stack;
using Roost.Daemon;
using Roost.Desktop.Controls;
using Roost.Desktop.ViewModels;
using Roost.Core.Platform;

namespace Roost.Desktop.Tests;

/// <summary>
/// Auditoría (oleada 6): nginx y Apache en el inventario con sus filtros (K2) y, en el detalle, cada
/// mitigación frente a lo que aplica PHP, con «Aplicar» y «Deshacer» (K4). Sin escanear la máquina
/// (<c>AuditState.Show</c>), sin ejecutar nada (<see cref="CannedCommandRunner"/> contesta por
/// <c>php -i</c>) y sin daemon (<see cref="AuditViewModel.RestartService"/> apunta los reinicios).
/// Las capturas van a <c>TestResults/snapshots/auditoria-*.png</c>.
/// </summary>
public class AuditoriaMitigacionesTests
{
    private static readonly DateOnly Today = new(2026, 10, 24);

    /// <summary>El empaquetado más avisos de nginx y Apache y uno de PHP mitigable (sin KEV), como los del feed.</summary>
    private static AuditDataset Dataset() => AuditDataset.LoadEmbedded().Merge(new AuditDataset
    {
        Source = "pruebas",
        Advisories =
        [
            new Advisory
            {
                Id = "CVE-2026-15176", Product = "nginx", Title = "Buffer overflow when using alias with rewrite", Severity = "medium",
                Affected = [">=0.1.29 <1.30.6", ">=1.31.0 <=1.31.6"], FixedIn = new() { ["1.30"] = "1.30.6", ["1.31"] = "1.31.7" },
                Refs = ["https://www.cve.org/CVERecord?id=CVE-2026-15176"],
            },
            new Advisory
            {
                Id = "CVE-2026-57941", Product = "apache", Title = "mod_http2 use-after-free", Severity = "high",
                Affected = [">=2.4.17 <2.4.69"], FixedIn = new() { ["2.4"] = "2.4.69" },
            },
            new Advisory
            {
                Id = "ROOST-PRUEBA-1", Aliases = ["PRUEBA"], Product = "php", Title = "Aviso de prueba mitigable por configuración", Cvss = 8.1,
                Affected = [">=8.4.0 <8.4.28"], FixedIn = new() { ["8.4"] = "8.4.28" },
                Mitigations =
                [
                    new Mitigation { Ini = "session.upload_progress.enabled", Value = "0", Note = "y reinicia todos los workers" },
                    new Mitigation { Ini = "expose_php", Value = "Off" },
                ],
            },
        ],
    });

    private static DetectedComponent C(ComponentKind kind, string version, string source, string path) => new()
    {
        Kind = kind,
        Version = ComponentVersion.TryParse(version, out var v) ? v : default,
        ExecutablePath = path,
        Source = source,
        Architecture = "x64",
    };

    private static List<AuditFinding> Sorted(IEnumerable<AuditFinding> findings) =>
        findings.OrderByDescending(f => f.Color).ThenBy(f => f.Component.Kind).ToList();

    // K2

    [AvaloniaFact]
    public async Task Auditoria_NginxYApache_SalenConSusFiltrosYSuArreglo()
    {
        var (main, window, fake) = await AjustesSistemaTests.OpenAsync();
        using var _ = fake;
        var evaluator = new AuditEvaluator(Dataset(), Today);
        main.Audit.Show(Sorted(evaluator.EvaluateAll(
        [
            C(ComponentKind.Nginx, "1.30.6", "Roost", Path.Combine(fake.Paths.NginxBin, "1.30.6", OsNames.Exe("nginx"))),
            C(ComponentKind.Nginx, "1.27.4", "Laragon", @"C:\laragon\bin\nginx\nginx-1.27.4\nginx.exe"),
            C(ComponentKind.Apache, "2.4.69", "Roost", Path.Combine(fake.Paths.Root, "bin", "apache", "2.4.69", "bin", "httpd.exe")),
            C(ComponentKind.Apache, "2.4.58", "XAMPP", @"C:\xampp\apache\bin\httpd.exe"),
            C(ComponentKind.Php, "8.4.28", "Roost", Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", OsNames.Exe("php"))),
        ])));
        await main.NavigateToAsync(main.AuditPage);
        var audit = main.AuditPage;

        Assert.Equal([AuditViewModel.AllKinds, "PHP", "nginx", "Apache"], audit.KindChoices);
        audit.KindFilter = "nginx";
        Assert.Equal(["Laragon", "Roost"], audit.Rows.Select(r => r.Source));
        var laragon = audit.Rows.Single(r => r.Source == "Laragon");
        Assert.Equal(("EOL", Tone.Bad), (laragon.Verdict, laragon.Tone));
        Assert.Equal("al día", audit.Rows.Single(r => r.Source == "Roost").Verdict);
        audit.KindFilter = AuditViewModel.AllKinds;
        // Un PHP más nuevo que la última que conoce el dataset no dice «hay 8.4.27».
        Assert.Equal("al día", audit.Rows.Single(r => r.Name == "PHP").Verdict);

        var xampp = audit.Rows.Single(r => r.Source == "XAMPP");
        audit.ChooseCommand.Execute(xampp);
        Assert.Equal("Apache 2.4.58 · XAMPP", audit.SelectedTitle);
        Assert.Equal("VULNERABLE", xampp.Verdict);
        Assert.Equal("Arreglo real: Apache 2.4.69 o posterior", audit.RealFix);
        Assert.Null(audit.ActionLabel);   // el de XAMPP no lo actualiza Roost
        Assert.Equal("https://www.cve.org/CVERecord?id=CVE-2026-57941", audit.Advisories.Single().Url);
        AjustesSistemaTests.Snapshot(window, "auditoria-nginx-apache");
    }

    // K4

    /// <summary>Un <c>php -i</c> recortado de un PHP 8.4.27 de Roost, con las rutas de <paramref name="fake"/>.</summary>
    private static string PhpI(FakeRoost fake, string uploadProgress = "On", string exposePhp = "On", bool withOurs = false)
    {
        var etc = Path.Combine(fake.Paths.Etc, "php");
        var files = new List<string> { Path.Combine(etc, "conf.d", "99-usuario.ini") };
        if (withOurs) files.Add(Path.Combine(etc, "conf.d", "8.4", PhpMitigations.FileName));
        files.Add(Path.Combine(etc, "conf.d", "8.4", "99-usuario.ini"));
        return $"""
            phpinfo()
            PHP Version => 8.4.27

            Loaded Configuration File => {Path.Combine(etc, "8.4.27-nts-x64", "php.ini")}
            Additional .ini files parsed => {string.Join(",\n", files)}

            Directive => Local Value => Master Value
            expose_php => {exposePhp} => {exposePhp}
            memory_limit => 512M => 512M
            session.upload_progress.enabled => {uploadProgress} => {uploadProgress}
            """;
    }

    [AvaloniaFact]
    public async Task Auditoria_Mitigaciones_SeComparanSeAplicanYSeDeshacen_SoloEnLosDeRoost()
    {
        var (main, window, fake) = await AjustesSistemaTests.OpenAsync();   // en marcha: el pool php-8.4 también
        using var _ = fake;
        var runner = new CannedCommandRunner().On("-i", PhpI(fake));
        main.Audit.Runner = runner;
        var restarts = new List<string>();
        main.AuditPage.RestartService = service =>
        {
            restarts.Add(service);
            return Task.FromResult(new CommandResponse(true, null, FakeRoost.Running));
        };
        var roost = Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", OsNames.Exe("php"));
        var laragon = @"C:\laragon\bin\php\php-8.4.20-nts-Win32-vs17-x64\php.exe";
        var evaluator = new AuditEvaluator(Dataset(), Today);
        var found = evaluator.EvaluateAll([C(ComponentKind.Php, "8.4.27", "Roost", roost), C(ComponentKind.Php, "8.4.20", "Laragon", laragon)]).ToList();
        main.Audit.Show(Sorted(await main.Audit.Mitigations!.CheckAllAsync(found)));
        await main.NavigateToAsync(main.AuditPage);
        var audit = main.AuditPage;

        // Sin mitigar: las dos se pueden aplicar, y dice dónde y qué lee PHP.
        audit.ChooseCommand.Execute(audit.Rows.Single(r => r.Source == "Roost"));
        Assert.Equal(["Sin mitigar", "Sin mitigar"], audit.Mitigations.Select(l => l.Status));
        Assert.All(audit.Mitigations, l => Assert.True(l.CanApply && !l.CanUndo));
        Assert.Equal("PHP aplica «On»", audit.Mitigations[0].Detail);
        Assert.True(audit.CanApplyAll);
        Assert.Contains(Path.Combine("conf.d", "8.4", "50-roost-mitigaciones.ini"), audit.MitigationNote);
        Assert.StartsWith($"PHP lee, en este orden: {Path.Combine("8.4.27-nts-x64", "php.ini")} → {Path.Combine("conf.d", "99-usuario.ini")} → {Path.Combine("conf.d", "8.4", "99-usuario.ini")}", audit.IniFilesText);
        var red = audit.Red;
        AjustesSistemaTests.ScrollTo(window, 380);
        AjustesSistemaTests.Snapshot(window, "auditoria-mitigaciones");

        // Aplicar una: va al fichero de la rama, reinicia solo php-8.4 y vuelve a comprobar.
        runner.On("-i", PhpI(fake, exposePhp: "Off", withOurs: true));
        await audit.ApplyMitigationCommand.ExecuteAsync(audit.Mitigations.Single(l => l.Mitigation.Ini == "expose_php"));
        var file = Path.Combine(fake.Paths.Etc, "php", "conf.d", "8.4", PhpMitigations.FileName);
        Assert.Equal(["expose_php"], PhpMitigations.Read(file).Select(e => e.Key));
        Assert.Equal(["php-8.4"], restarts);
        var expose = audit.Mitigations.Single(l => l.Mitigation.Ini == "expose_php");
        Assert.Equal(("Mitigada", Tone.Ok, true, false), (expose.Status, expose.Tone, expose.CanUndo, expose.CanApply));
        Assert.Equal("PHP aplica «Off» · la aplicó Roost", expose.Detail);
        Assert.Equal(red, audit.Red);   // falta la otra: el aviso sigue abierto
        Assert.Equal("Roost", audit.Selected!.Source);

        // Aplicar todas: el aviso queda mitigado y baja a ámbar (no está en KEV).
        runner.On("-i", PhpI(fake, uploadProgress: "Off", exposePhp: "Off", withOurs: true));
        await audit.ApplyAllMitigationsCommand.ExecuteAsync(null);
        Assert.Equal(["session.upload_progress.enabled", "expose_php"], PhpMitigations.Read(file).Select(e => e.Key).Order().Reverse());
        Assert.Equal(("VULNERABLE · mitigada", Tone.Warn), (audit.Selected!.Verdict, audit.Selected.Tone));
        Assert.Equal(red - 1, audit.Red);
        Assert.Equal(main.Audit.RedCount, main.RoostNav[0].Badge);
        Assert.All(audit.Mitigations, l => Assert.Equal("Mitigada", l.Status));
        Assert.StartsWith("Mitigada por configuracion", audit.LongText);
        AjustesSistemaTests.ScrollTo(window, 380);
        AjustesSistemaTests.Snapshot(window, "auditoria-mitigaciones-aplicadas");

        // Deshacer una: sale del fichero (la otra se queda) y vuelve a rojo.
        runner.On("-i", PhpI(fake, uploadProgress: "Off", withOurs: true));
        await audit.UndoMitigationCommand.ExecuteAsync(audit.Mitigations.Single(l => l.Mitigation.Ini == "expose_php"));
        Assert.Equal(["session.upload_progress.enabled"], PhpMitigations.Read(file).Select(e => e.Key));
        Assert.Equal(3, restarts.Count);
        Assert.Equal(red, audit.Red);
        Assert.Equal(("Sin mitigar", true), (audit.Mitigations.Single(l => l.Mitigation.Ini == "expose_php").Status, audit.Mitigations.Single(l => l.Mitigation.Ini == "expose_php").CanApply));

        // El de Laragon solo se lee: ni «Aplicar» ni «Deshacer», y dice dónde cambiarlo.
        audit.ChooseCommand.Execute(audit.Rows.Single(r => r.Source == "Laragon"));
        Assert.All(audit.Mitigations, l => Assert.False(l.HasActions));
        Assert.StartsWith("Este PHP es de Laragon: Roost solo lo lee.", audit.MitigationNote);
        Assert.False(File.Exists(Path.Combine(@"C:\laragon\bin\php\php-8.4.20-nts-Win32-vs17-x64", PhpMitigations.FileName)));
    }

    /// <summary>
    /// Solo con <c>ROOST_TEST_AUDIT_HOME</c> (una <c>ROOST_HOME</c> de pruebas): la pantalla con un
    /// escaneo de verdad de la máquina (solo <c>-v</c> y <c>php -i</c>: nada se arranca ni se cambia) y,
    /// si se da <c>ROOST_TEST_AUDIT_DATASET</c>, los PHP evaluados con ese dataset y comparados con su
    /// php -i real. Sin red: el feed da 404. Deja <c>real-auditoria*.png</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task EnReal_ElEscaneoDeLaMaquina_ConNginxApacheYMitigaciones()
    {
        var home = Environment.GetEnvironmentVariable("ROOST_TEST_AUDIT_HOME");
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            Assert.Skip("Define ROOST_TEST_AUDIT_HOME=<una ROOST_HOME de pruebas> para escanear la maquina de verdad.");
        var before = Environment.GetEnvironmentVariable(Roost.Core.Platform.RoostPaths.HomeVariable);
        Environment.SetEnvironmentVariable(Roost.Core.Platform.RoostPaths.HomeVariable, home);
        try
        {
            using var http = new HttpClient(new FakeUpstream());
            var main = new MainViewModel(new Roost.Desktop.Services.AppServices(Roost.Core.Platform.RoostPaths.Default(), http, version: FakeRoost.Version));
            main.Stack.Show(null);
            var window = new Roost.Desktop.Views.MainWindow { DataContext = main };
            window.Show();
            await main.Current.ShownAsync();
            await main.NavigateToAsync(main.AuditPage);
            await main.Audit.ScanAsync();
            Assert.Contains(main.Audit.Findings, f => f.Component.Kind == ComponentKind.Nginx);
            Assert.Contains(main.Audit.Findings, f => f.Component.Kind == ComponentKind.Apache);
            AjustesSistemaTests.ScrollTo(window, 300);
            AjustesSistemaTests.Snapshot(window, "real-auditoria");

            if (Environment.GetEnvironmentVariable("ROOST_TEST_AUDIT_DATASET") is { Length: > 0 } file && File.Exists(file))
            {
                var evaluator = new AuditEvaluator(AuditDataset.LoadFile(file));
                var findings = evaluator.EvaluateAll(main.Audit.Findings.Select(f => f.Component)).ToList();
                main.Audit.Show(Sorted(await main.Audit.Mitigations!.CheckAllAsync(findings)));
                var audit = main.AuditPage;
                audit.ChooseCommand.Execute(audit.Rows.First(r => r.Name == "PHP" && r.Version.StartsWith("8.5", StringComparison.Ordinal)));
                Assert.NotEmpty(audit.Mitigations);
                Assert.All(audit.Mitigations, l => Assert.NotEqual("Sin comprobar", l.Status));
                AjustesSistemaTests.ScrollTo(window, 900);
                AjustesSistemaTests.Snapshot(window, "real-auditoria-mitigaciones");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Roost.Core.Platform.RoostPaths.HomeVariable, before);
        }
    }

    [Fact]
    public void LaLineaDeUnaMitigacion_DiceSuEstadoConSusTextos()
    {
        var m = new Mitigation { Ini = "session.upload_progress.enabled", Value = "0" };

        var pisada = AuditViewModel.LineOf(new MitigationState(m, MitigationStatus.Unmitigated, "On") { AppliedByRoost = true, OverriddenBy = @"C:\r\etc\php\conf.d\8.4\99-usuario.ini" });
        Assert.Equal(("Sin mitigar", Tone.Bad, false, true), (pisada.Status, pisada.Tone, pisada.CanApply, pisada.CanUndo));
        Assert.Equal(@"La aplicó Roost, pero la pisa C:\r\etc\php\conf.d\8.4\99-usuario.ini, que PHP lee después", pisada.Detail);

        Assert.Equal("El PHP de la terminal fija este ajuste: desde aquí no se puede comprobar.",
            AuditViewModel.LineOf(new MitigationState(new Mitigation { Ini = "max_execution_time", Value = "30", Compare = "<=" }, MitigationStatus.Unknown)).Detail);
        Assert.Equal("php -i no da esta directiva (¿la extensión no está cargada?).", AuditViewModel.LineOf(new MitigationState(m, MitigationStatus.Unknown)).Detail);
        Assert.Equal("Sin comprobar", AuditViewModel.LineOf(new MitigationState(m, MitigationStatus.Unchecked)).Status);
        Assert.Equal("PHP aplica «» (vacío)", AuditViewModel.LineOf(new MitigationState(m, MitigationStatus.Mitigated, "")).Detail);
        Assert.Equal("memory_limit <= 128M", AuditViewModel.LineOf(new MitigationState(new Mitigation { Ini = "memory_limit", Value = "128M", Compare = "<=" }, MitigationStatus.Unchecked)).Setting);
    }
}
