using Avalonia.Headless.XUnit;
using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Daemon;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Core.Platform;

namespace Roost.Desktop.Tests;

/// <summary>
/// Ajustes y sistema (oleada 5): Auditoría (K1: filtros por tipo y origen, abrir el aviso, ignorar y
/// qué ha cambiado), la bandeja (J4: lo que enseña, leído fuera del hilo de la UI) y sus avisos
/// (J4 y K5: una vez por cambio). Sin escanear la máquina: los hallazgos se ponen con <c>AuditState.Show</c>.
/// </summary>
public class AuditoriaBandejaTests
{
    private static DetectedComponent C(ComponentKind kind, string version, string source, string path) => new()
    {
        Kind = kind,
        Version = ComponentVersion.TryParse(version, out var v) ? v : default,
        ExecutablePath = path,
        Source = source,
        Architecture = "x64",
        Extra = kind == ComponentKind.Php ? new Dictionary<string, string> { ["threadSafety"] = "nts" } : new Dictionary<string, string>(),
    };

    private static readonly DetectedComponent Php84 = C(ComponentKind.Php, "8.4.27", "Roost", @"C:\roost\bin\php\8.4.27-nts-x64\php.exe");
    private static readonly DetectedComponent Php74 = C(ComponentKind.Php, "7.4.33", "Roost", @"C:\roost\bin\php\7.4.33-nts-x64\php.exe");
    private static readonly DetectedComponent Laragon = C(ComponentKind.Php, "8.3.18", "Laragon", @"C:\laragon\bin\php\php-8.3.18-nts-Win32-vs16-x64\php.exe");
    private static readonly DetectedComponent LaragonFixed = C(ComponentKind.Php, "8.3.36", "Laragon", @"C:\laragon\bin\php\php-8.3.18-nts-Win32-vs16-x64\php.exe");
    private static readonly DetectedComponent Maria = C(ComponentKind.MariaDb, "11.4.13", "Roost", @"C:\roost\bin\mariadb\11.4.13\bin\mariadbd.exe");
    private static readonly DetectedComponent Xampp = C(ComponentKind.Php, "5.6.40", "XAMPP", @"C:\xampp\php\php.exe");

    private static void Show(MainViewModel main, DateTime when, params DetectedComponent[] found) =>
        main.Audit.Show(found.Select(c => main.Audit.Evaluate(c.Kind, c.Version, c.ExecutablePath, c.Source)).OrderByDescending(f => f.Color).ToList(), when);

    // K1

    [AvaloniaFact]
    public async Task Auditoria_FiltraPorTipoYOrigen_IgnoraYDiceQueHaCambiado()
    {
        var (main, window, fake) = await AjustesSistemaTests.OpenAsync();
        using var _ = fake;
        Show(main, new DateTime(2026, 10, 22, 18, 5, 0), Php84, Php74, Laragon, Maria);
        await main.NavigateToAsync(main.AuditPage);
        var audit = main.AuditPage;
        Assert.False(audit.HasChanges);   // la primera vez no hay con qué comparar

        // La segunda: XAMPP aparece (rojo) y el de Laragon se ha actualizado en su sitio (resuelto).
        Show(main, new DateTime(2026, 10, 23, 9, 30, 0), Php84, Php74, LaragonFixed, Maria, Xampp);
        Assert.True(audit.HasChanges);
        Assert.Equal("Desde la auditoría anterior (22-oct, 18:05): 1 nuevo o que empeora · 1 resuelto.", audit.ChangesText);
        Assert.Equal(Tone.Bad, audit.ChangesTone);
        Assert.Equal(["Nuevo: PHP 5.6.40 · XAMPP · EOL", "Resuelto: PHP 8.3.18 · Laragon"], audit.ChangeLines.Select(l => l.Text));
        Assert.Single(main.Audit.Changes!.NewRed);

        // Filtros.
        Assert.Equal([AuditViewModel.AllKinds, "PHP", "MariaDB"], audit.KindChoices);
        Assert.Equal([AuditViewModel.AllSources, "Laragon", "Roost", "XAMPP"], audit.SourceChoices);
        audit.KindFilter = "MariaDB";
        Assert.Equal(["MariaDB"], audit.Rows.Select(r => r.Name));
        audit.KindFilter = AuditViewModel.AllKinds;
        audit.SourceFilter = "XAMPP";
        Assert.Equal(["5.6.40"], audit.Rows.Select(r => r.Version));
        audit.SourceFilter = AuditViewModel.AllSources;

        // Ignorar el PHP 5.6 de XAMPP: sale aparte, no cuenta en rojo ni en la barra lateral, y se guarda.
        var red = audit.Red;
        audit.ChooseCommand.Execute(audit.Rows.Single(r => r.Source == "XAMPP"));
        Assert.Equal("Ignorar", audit.IgnoreLabel);
        audit.ToggleIgnoreCommand.Execute(null);
        Assert.DoesNotContain(audit.Rows, r => r.Source == "XAMPP");
        Assert.Equal(["5.6.40"], audit.IgnoredRows.Select(r => r.Version));
        Assert.Equal("Ignorados (1)", audit.IgnoredTitle);
        Assert.Equal(red - 1, audit.Red);
        Assert.Equal(main.Audit.RedCount, main.RoostNav[0].Badge);
        Assert.True(audit.SelectedIgnored);   // sigue elegido, ahora como ignorado
        Assert.Equal("Dejar de ignorar", audit.IgnoreLabel);
        Assert.Single(AuditHistory.LoadIgnored(fake.Paths));
        AjustesSistemaTests.Snapshot(window, "auditoria-cambios-filtros");
        AjustesSistemaTests.ScrollTo(window, 560);
        AjustesSistemaTests.Snapshot(window, "auditoria-ignorados");

        // La auditoría siguiente no lo cuenta como nuevo.
        Show(main, new DateTime(2026, 10, 23, 12, 0, 0), Php84, Php74, LaragonFixed, Maria, Xampp);
        Assert.Equal("Sin cambios desde la auditoría anterior (23-oct, 09:30).", audit.ChangesText);
        Assert.Empty(main.Audit.Changes!.NewRed);

        // Se puede deshacer, desde la lista de ignorados.
        audit.UnignoreCommand.Execute(audit.IgnoredRows.Single());
        Assert.False(audit.HasIgnored);
        Assert.Contains(audit.Rows, r => r.Source == "XAMPP");
        Assert.Equal(red, audit.Red);
        Assert.Empty(AuditHistory.LoadIgnored(fake.Paths));
    }

    [Fact]
    public void Auditoria_LaPaginaDelAviso()
    {
        Assert.Equal("https://github.com/php/php-src/security/advisories/GHSA-abcd-1234-wxyz",
            AuditViewModel.AdvisoryUrl(new Advisory { Id = "CVE-2026-1", Aliases = ["GHSA-abcd-1234-wxyz"], Refs = ["https://www.php.net/releases/", "https://github.com/php/php-src/security/advisories/GHSA-abcd-1234-wxyz"] }));
        Assert.Equal("https://github.com/advisories/GHSA-abcd-1234-wxyz",
            AuditViewModel.AdvisoryUrl(new Advisory { Id = "GHSA-abcd-1234-wxyz", Refs = ["https://www.php.net/releases/"] }));
        Assert.Equal("https://www.cve.org/CVERecord?id=CVE-2026-109618",
            AuditViewModel.AdvisoryUrl(new Advisory { Id = "cve-2026-109618", Aliases = ["PHP2Shell"], Refs = ["https://www.php.net/releases/"] }));
        Assert.Null(AuditViewModel.AdvisoryUrl(new Advisory { Id = "ROOST-1" }));
    }

    [Fact]
    public void Historial_NuevoEsLoQueEmpeora_YResueltoLoQueYaNoEstaEnRojoNiAmbar()
    {
        var eval = new AuditState();
        AuditFinding F(DetectedComponent c) => eval.Evaluate(c.Kind, c.Version, c.ExecutablePath, c.Source);
        var before = new AuditRecord(new DateTime(2026, 10, 1), [AuditHistory.MarkOf(F(Laragon)), AuditHistory.MarkOf(F(Php84)), AuditHistory.MarkOf(F(Php74))]);

        // Laragon resuelto (actualizado en su sitio), 7.4 quitado (resuelto), XAMPP nuevo; 8.4 sigue igual.
        var changes = AuditHistory.Compare(before, [F(LaragonFixed), F(Php84), F(Xampp)], _ => false)!;
        Assert.Equal([Xampp.ExecutablePath], changes.New.Select(m => m.Path));
        Assert.Equal([Laragon.ExecutablePath, Php74.ExecutablePath], changes.Resolved.Select(m => m.Path));

        // Lo ignorado no es ni nuevo ni resuelto.
        changes = AuditHistory.Compare(before, [F(Laragon), F(Php84), F(Php74), F(Xampp)], f => f.Component.Source == "XAMPP")!;
        Assert.False(changes.Any);
        Assert.Null(AuditHistory.Compare(null, [F(Xampp)], _ => false));
        Assert.Equal(AuditHistory.KeyOf(F(Laragon)), AuditHistory.KeyOf(F(LaragonFixed)));
        Assert.NotEqual(AuditHistory.IgnoreIdOf(F(Laragon)), AuditHistory.IgnoreIdOf(F(LaragonFixed)));
    }

    [WindowsFact("Pendiente en Linux: la app (L2).")]
    public void Historial_AlMoverLaRaiz_LoDeRoostSigueSiendoLoMismo()
    {
        var root = Path.Combine(Path.GetTempPath(), "roost-historial-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(root, "a");
        var to = Path.Combine(root, "b");
        try
        {
            var eval = new AuditState();
            var before = C(ComponentKind.Php, "7.4.33", "Roost", Path.Combine(from, @"bin\php\7.4.33-nts-x64\php.exe"));
            var after = before with { ExecutablePath = Path.Combine(to, @"bin\php\7.4.33-nts-x64\php.exe") };
            var moved = new Roost.Core.Platform.RoostPaths(to, Path.Combine(root, "cache"));
            var finding = eval.Evaluate(before.Kind, before.Version, before.ExecutablePath, before.Source);
            AuditHistory.SaveLast(moved, new AuditRecord(DateTime.Now, [AuditHistory.MarkOf(finding)]));
            AuditHistory.SaveIgnored(moved, [new IgnoredFinding(AuditHistory.IgnoreIdOf(finding), "Php", "7.4.33", "Roost", before.ExecutablePath, DateTime.Now)]);

            AuditHistory.Rebase(moved, from, to);

            var now = eval.Evaluate(after.Kind, after.Version, after.ExecutablePath, after.Source);
            Assert.False(AuditHistory.Compare(AuditHistory.LoadLast(moved), [now], _ => false)!.Any);   // ni nuevo ni resuelto
            Assert.Equal(AuditHistory.IgnoreIdOf(now), AuditHistory.LoadIgnored(moved).Single().Id);    // y sigue ignorado
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // J4 y K5: los avisos de la bandeja

    private static StatusResponse With(params (string Name, string State, string? Error)[] services) => FakeRoost.Running with
    {
        Services = [.. services.Select(s => new ServiceDto(s.Name, s.State, 1, 0, null, s.Error))],
    };

    [Fact]
    public void AvisoServicio_UnaVezPorCaida_YLosPoolsCuentanComoUno()
    {
        var alerts = new TrayAlerts();
        Assert.Null(alerts.ServicesDown(With(("nginx", "Running", null), ("php-8.4-0", "Running", null))));

        var down = alerts.ServicesDown(With(("nginx", "Running", null), ("php-8.4-0", "Failed", "salio con 255"), ("php-8.4-1", "Failed", null)));
        Assert.Equal(TrayAlertKind.ServiceDown, down!.Kind);
        Assert.Equal("Se ha caído PHP 8.4", down.Title);
        Assert.Equal("salio con 255. Roost lo ha dejado parado: mira su registro.", down.Text);

        // Mientras siga caído no se repite (el sondeo de 3 s no da la lata).
        Assert.Null(alerts.ServicesDown(With(("nginx", "Running", null), ("php-8.4-0", "Failed", null), ("php-8.4-1", "Failed", null))));

        // Otro que cae: se avisa de ese.
        var second = alerts.ServicesDown(With(("nginx", "Failed", null), ("php-8.4-0", "Failed", null), ("mariadb-11.4", "Failed", null)));
        Assert.Equal("Se han caído nginx y MariaDB 11.4", second!.Title);

        // Vuelve a estar en marcha y se cae otra vez: otro aviso.
        Assert.Null(alerts.ServicesDown(With(("nginx", "Running", null))));
        Assert.Equal("Se ha caído nginx", alerts.ServicesDown(With(("nginx", "Failed", null)))!.Title);
        Assert.Null(alerts.ServicesDown(null));
        Assert.NotNull(alerts.ServicesDown(With(("nginx", "Failed", null))));   // sin daemon se olvida todo
    }

    [Fact]
    public void AvisoAuditoria_SoloSiAlgoPasaARojo()
    {
        var eval = new AuditState();
        AuditMark M(DetectedComponent c) => AuditHistory.MarkOf(eval.Evaluate(c.Kind, c.Version, c.ExecutablePath, c.Source));
        Assert.Null(TrayAlerts.AuditRed(null));
        Assert.Null(TrayAlerts.AuditRed(new AuditChanges(DateTime.Now, [], [M(Laragon)])));
        var one = TrayAlerts.AuditRed(new AuditChanges(DateTime.Now, [M(Xampp)], []))!;
        Assert.Equal(TrayAlertKind.AuditRed, one.Kind);
        Assert.Equal("La auditoría pasa a rojo: PHP 5.6.40 (XAMPP)", one.Title);
        var two = TrayAlerts.AuditRed(new AuditChanges(DateTime.Now, [M(Xampp), M(Laragon)], []))!;
        Assert.Equal("La auditoría pasa a rojo: 2 componentes", two.Title);
        Assert.StartsWith("PHP 5.6.40 (XAMPP) y 1 más.", two.Text);
    }

    [AvaloniaFact]
    public async Task Bandeja_LeeElPhpExactoElServidorWebYLosSitios_FueraDeLaUi()
    {
        var (main, _, fake) = await AjustesSistemaTests.OpenAsync();
        using var __ = fake;

        var ui = TraySnapshot.Capture(main);
        var snapshot = await Task.Run(() => TraySnapshot.Read(fake.Paths, ui, v => v.Major < 8));
        Assert.Equal(["8.4.27", "8.3.36", "7.4.33 (EOL)"], snapshot.Php.Select(p => p.Label));
        // La mejor de su rama guarda la rama (entran solas las nuevas), como «Usar como global».
        Assert.Equal(["8.4", "8.3", "7.4"], snapshot.Php.Select(p => p.Spec));
        Assert.Equal("8.4.27-nts-x64", snapshot.GlobalPhp);
        Assert.Equal("8.4.27", snapshot.GlobalPhpLabel);
        Assert.Equal("nginx", snapshot.WebLabel);
        Assert.Equal("nginx", snapshot.WebService);
        Assert.False(snapshot.ApacheInstalled);
        Assert.Equal("http://localhost/", snapshot.WebUrl);
        Assert.Equal(3, snapshot.Sites.Count);
        Assert.Equal("En marcha · PHP 8.4.27 · MariaDB 11.4", snapshot.Headline);
        Assert.True(snapshot.Running);

        // Dos instalaciones de la misma rama: la vieja se elige exacta.
        Directory.CreateDirectory(Path.Combine(fake.Paths.PhpBin, "8.4.20-nts-x64"));
        File.WriteAllText(Path.Combine(fake.Paths.PhpBin, "8.4.20-nts-x64", OsNames.Exe("php")), "");
        File.WriteAllText(Path.Combine(fake.Paths.PhpBin, "8.4.20-nts-x64", OsNames.Exe("php-cgi")), "");
        snapshot = TraySnapshot.Read(fake.Paths, ui, _ => false);
        Assert.Equal("8.4.20-nts-x64", snapshot.Php.Single(p => p.Label == "8.4.20").Spec);
        Assert.Equal("8.4", snapshot.Php.Single(p => p.Label == "8.4.27").Spec);

        // Parado y con Apache en roost.toml.
        Roost.Core.Config.RoostConfig.SetWebServer(fake.Paths.ConfigFile, "apache");
        main.Stack.Show(null);
        var stopped = TraySnapshot.Read(fake.Paths, TraySnapshot.Capture(main), _ => false);
        Assert.Equal("Parado", stopped.Headline);
        Assert.Equal("Apache", stopped.WebLabel);
        Assert.Equal("apache", stopped.WebService);
        Assert.NotEqual(snapshot.Signature, stopped.Signature);
    }
}
