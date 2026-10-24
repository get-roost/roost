using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

namespace Roost.Desktop.Tests;

/// <summary>
/// Monta la ventana entera con cada pantalla y la dibuja. Falla si una vista no carga (un
/// binding roto, un recurso que no existe) y deja la captura en
/// <c>TestResults/snapshots/&lt;nombre&gt;.png</c> para revisarla a ojo.
/// </summary>
public class ViewSnapshotTests
{
    private static readonly string SnapshotDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");

    private static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake)> OpenAsync(bool running, bool localDns = false, string version = FakeRoost.Version)
    {
        var fake = new FakeRoost();
        if (localDns) Roost.Core.Config.RoostConfig.SetResolver(fake.Paths.ConfigFile, Roost.Core.Config.RoostConfig.ResolverDns);
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, version: version));
        var status = FakeRoost.Running;
        if (localDns) status = status with { Services = [new Roost.Daemon.ServiceDto("dns", "Running", status.DaemonPid, 0, 53, null), .. status.Services] };
        main.Stack.Show(running ? status : null);
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.Current.ShownAsync();
        main.NotifyConfigChanged();
        return (main, window, fake);
    }

    private static void Snapshot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(SnapshotDir);
#pragma warning disable CS0618 // el PNG por defecto basta para revisar la captura
        frame.Save(Path.Combine(SnapshotDir, name + ".png"));
#pragma warning restore CS0618
    }

    [AvaloniaFact]
    public async Task Inicio_EnMarcha()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        var rows = main.Home.Services;
        Assert.Equal(["MariaDB", "PHP 8.4", "PHP 7.4", "nginx"], rows.Select(r => r.Name));
        Assert.Equal("9840–9843", rows[1].Listen);
        Assert.Equal("global", rows[1].Tag);
        Assert.Equal("EOL", rows[2].Tag);
        Assert.Equal(3, main.Home.SiteLines.Count);
        Snapshot(window, "inicio-en-marcha");
    }

    [AvaloniaFact]
    public async Task Inicio_Parado()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;

        Assert.All(main.Home.Services, r => Assert.Equal("Parado", r.State));
        Assert.Contains(main.Home.Services, r => r.Name == "PHP 7.4");
        Snapshot(window, "inicio-parado");
    }

    [AvaloniaFact]
    public async Task Php_ConCatalogoYExtensiones()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Php);
        var php = main.Php;
        Assert.Equal(["8.4.27", "8.3.36", "7.4.33"], php.Installed.Select(c => c.Version));
        Assert.True(php.Installed[0].IsGlobal);
        Assert.Equal("EOL", php.Installed[2].Verdict);
        Assert.Equal("legacy.test", php.Installed[2].Usage);
        Assert.Equal(php.Installed[0], php.Selected);
        Assert.Contains(php.Extensions, e => e.Name == "curl" && e.IsOn);
        Assert.Contains(php.Extensions, e => e.Name == "soap" && !e.IsOn);
        Assert.Equal(["8.5.12", "8.4.27", "8.3.36", "8.2.35", "8.1.34", "7.4.33"], php.Catalog.Select(r => r.Version));
        Assert.False(php.Catalog[0].Installed);
        Assert.True(php.Catalog[1].Installed);
        Snapshot(window, "php");

        php.ThreadSafe = true;
        Assert.All(php.Catalog, r => Assert.False(r.Installed)); // solo hay NTS instaladas
    }

    [AvaloniaFact]
    public async Task Php_QuitarUnaVersionEnUso_PreguntaQueHacerConSusSitios()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;

        await main.NavigateToAsync(main.Php);
        main.Php.PickCommand.Execute(main.Php.Installed.Single(c => c.Version == "7.4.33"));
        var removing = main.Php.RemoveCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<RemovePhpDialogViewModel>(main.Dialog);
        Assert.Equal("PHP 7.4.33 está en uso", dialog.Title);
        Assert.Equal(["legacy"], dialog.Sites.Select(s => s.Name));
        Snapshot(window, "php-quitar-en-uso");
        dialog.Close(false);
        await removing;
        Assert.Contains(main.Php.Installed, c => c.Version == "7.4.33"); // cancelado: sigue instalada
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_ConLaGlobalCatalogoYPuerto()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        Assert.True(db.DbRunning);
        Assert.Equal(3308, db.Port);
        Assert.Equal("DB_HOST=127.0.0.1  DB_PORT=3308  DB_USERNAME=root  DB_PASSWORD=", db.EnvLine);
        Assert.Equal(["11.4.13"], db.Installed.Select(r => r.Version));
        Assert.True(db.Installed[0].InUse);
        Assert.StartsWith("LTS hasta", db.Installed[0].Support);
        Assert.Equal(["12.3.3", "11.8.6", "11.4.13", "10.11.19"], db.Catalog.Select(r => r.Version));
        Assert.True(db.Catalog.Single(r => r.Version == "11.4.13").Installed);
        Assert.StartsWith("LTS hasta", db.Catalog[0].Support);
        Snapshot(window, "bases-de-datos");

        db.ShowMySql = true;
        Assert.Equal(["8.4.11", "8.0.46", "5.7.44"], db.Catalog.Select(r => r.Version));
        Assert.StartsWith("EOL", db.Catalog.Single(r => r.Version == "5.7.44").Support);

        db.PortText = "abc";
        Assert.False(db.CanApplyPort);
        Assert.Equal("Puerto no válido", db.PortTitle);
        db.PortText = "80";
        Assert.Equal("El 80 es del servidor web", db.PortTitle);
    }

    [AvaloniaFact]
    public async Task ServidorWeb_ConNginxYPuertos()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Web);
        var web = main.Web;
        Assert.True(web.NginxInstalled);
        Assert.True(web.NginxRunning);
        Assert.Equal("80", web.HttpText);
        Assert.Equal("443", web.HttpsText);
        Assert.Equal("en uso por Roost", web.HttpChip);
        Assert.False(web.CanApplyPorts);
        Snapshot(window, "servidor-web");

        web.HttpsText = "80";
        Assert.False(web.CanApplyPorts);
        web.HttpText = "3308";
        Assert.Equal("es de la base de datos", web.HttpChip);

        web.ShowApache = true;
        Assert.True(web.UseNginx);                         // el activo sigue siendo nginx
        Assert.False(web.ApacheInstalled);
        Assert.Equal("se instala al elegirlo", web.ApacheState);
        Assert.Equal(Roost.Core.Install.ApacheCatalog.Windows.Version, web.ApacheVersion);
        Snapshot(window, "servidor-web-apache");

        // Ya instalado (pero sin elegir), la pestaña enseña el detalle.
        var httpd = Path.Combine(fake.Paths.ApacheBin, Roost.Core.Install.ApacheCatalog.Windows.Version, "bin", "httpd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(httpd)!);
        File.WriteAllText(httpd, "");
        web.StackChanged();
        Assert.True(web.ApacheInstalled);
        Assert.Equal("instalado", web.ApacheState);
        Snapshot(window, "servidor-web-apache-instalado");
    }

    [AvaloniaFact]
    public async Task Registros_ListaYColorea()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.OpenLogAsync("nginx-error.log");
        var logs = main.Logs;
        // El de roost-helper es de la máquina (%ProgramData%): sale si existe.
        Assert.Equal(["roostd.log", "nginx-error.log", "nginx-access.log", "php-8.4-error.log"],
            logs.Files.Select(f => f.Display).Where(d => !d.StartsWith('%')));
        Assert.Equal("nginx-error.log", logs.Selected?.Display);
        Assert.Equal("1 error", logs.Files[1].Badge);
        Assert.Equal("1 aviso", logs.Files[0].Badge);
        Assert.Equal([LogLevel.Warn, LogLevel.Error], logs.Lines.Select(l => l.Level));

        logs.ChooseCommand.Execute(logs.Files[0]);
        Snapshot(window, "registros");

        logs.Filter = "php-7.4";
        Assert.Equal(2, logs.Lines.Count);
        logs.ChooseCommand.Execute(logs.Files[3]);
        logs.Filter = "";
        Assert.Equal(["(vacío)"], logs.Lines.Select(l => l.Text));
        main.Logs.Hidden();
    }

    [AvaloniaFact]
    public async Task Sitios_ListaDetalleYHosts()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        Assert.Equal("3 sitios", sites.Count);
        Assert.Equal(["blog.test", "legacy.test", "tienda.test"], sites.Rows.Select(r => r.Host));
        var legacy = sites.Rows.Single(r => r.Host == "legacy.test");
        Assert.False(legacy.InHosts);
        Assert.Equal("7.4 · EOL", legacy.Php);
        Assert.Equal("8.4 (global)", sites.Rows[0].Php);

        sites.ChooseCommand.Execute(legacy);
        Assert.True(sites.MissingFromHosts);
        Assert.Equal("7.4", sites.SelectedPhp?.Spec);
        Assert.Equal("(la carpeta)", sites.RootText);
        Snapshot(window, "sitios");

        sites.Search = "tien";
        Assert.Equal(["tienda.test"], sites.Rows.Select(r => r.Host));
        Assert.Equal("public/", sites.RootText);
    }

    [AvaloniaFact]
    public async Task ConDnsLocal_NadaPideElHosts()
    {
        var (main, window, fake) = await OpenAsync(running: true, localDns: true);
        using var _ = fake;

        var dns = main.Home.Services[0];
        Assert.Equal("DNS local", dns.Name);
        Assert.Equal("127.53.0.1:53", dns.Listen);
        Assert.StartsWith("DNS local", main.HostsText);
        Snapshot(window, "inicio-dns-local");

        await main.NavigateToAsync(main.Sites);
        Assert.All(main.Sites.Rows, r => Assert.Equal("DNS", r.Hosts));
        main.Sites.ChooseCommand.Execute(main.Sites.Rows.Single(r => r.Host == "legacy.test"));
        Assert.False(main.Sites.MissingFromHosts);   // con el hosts, legacy.test faltaba

        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        Assert.True(dialog.UsesLocalDns);
        dialog.Name = "tienda";
        Snapshot(window, "nuevo-sitio-dns-local");
        dialog.Close();
        await opening;

        await main.NavigateToAsync(main.Settings);
        Assert.True(main.Settings.ResolverDns);
        Assert.False(main.Settings.ResolverHosts);
        Assert.NotEmpty(main.Settings.DnsStateText);
        Snapshot(window, "ajustes-dns-local");
    }

    [AvaloniaFact]
    public async Task NuevoSitio_RecomiendaElPhpDelComposerJson()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        Assert.False(dialog.NameValid);

        dialog.Name = "Mi Tienda";
        Assert.False(dialog.NameValid);
        dialog.Name = "tienda";
        Assert.True(dialog.NameValid);
        Assert.Contains("se actualizará", dialog.NameHint);
        Assert.EndsWith(Path.Combine("www", "tienda"), dialog.Folder);
        Assert.StartsWith("8.4.27 · recomendada por composer.json (^8.2)", dialog.Php?.Label);
        Assert.True(dialog.CreateDatabase);
        Assert.Equal("tienda", dialog.DbName);
        Snapshot(window, "nuevo-sitio");

        dialog.Name = "mi-blog";
        Assert.Equal("mi_blog", dialog.DbName);
        Assert.Contains("No existe", dialog.FolderHint);
        Assert.Null(dialog.Php?.Spec); // sin composer.json: el global
        dialog.Close();
        await opening;
    }

    [AvaloniaFact]
    public async Task ConfiarCa_EnsenaLaHuellaYLaClave()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;

        new Roost.Core.Sites.LocalCa(fake.Paths).LoadOrCreate();
        var offering = main.OfferTrustAsync(force: true);
        var dialog = Assert.IsType<TrustCaDialogViewModel>(main.Dialog);
        Assert.StartsWith("Roost Local CA", dialog.Subject);
        Assert.StartsWith("SHA-1 ", dialog.Thumbprint);
        Snapshot(window, "confiar-ca");
        dialog.Close(); // «Ahora no»: nada toca el almacén de certificados
        await offering;
    }

    private static void ShowFindings(MainViewModel main)
    {
        Roost.Core.Models.DetectedComponent C(Roost.Core.Models.ComponentKind kind, string version, string source, string path) => new()
        {
            Kind = kind,
            Version = Roost.Core.Models.ComponentVersion.TryParse(version, out var v) ? v : default,
            ExecutablePath = path,
            Source = source,
            Architecture = "x64",
            Extra = kind == Roost.Core.Models.ComponentKind.Php ? new Dictionary<string, string> { ["threadSafety"] = "nts" } : new Dictionary<string, string>(),
        };
        var found = new[]
        {
            C(Roost.Core.Models.ComponentKind.Php, "8.4.27", "Roost", @"C:\roost\bin\php\8.4.27-nts-x64\php.exe"),
            C(Roost.Core.Models.ComponentKind.Php, "7.4.33", "Roost", @"C:\roost\bin\php\7.4.33-nts-x64\php.exe"),
            C(Roost.Core.Models.ComponentKind.Php, "8.3.18", "Laragon", @"C:\laragon\bin\php\php-8.3.18-nts-Win32-vs16-x64\php.exe"),
            C(Roost.Core.Models.ComponentKind.MariaDb, "11.4.13", "Roost", @"C:\roost\bin\mariadb\11.4.13\bin\mariadbd.exe"),
            C(Roost.Core.Models.ComponentKind.MariaDb, "10.6.28", "Program Files", @"C:\Program Files\MariaDB 10.6\bin\mysqld.exe"),
        };
        main.Audit.Show(found.Select(c => main.Audit.Evaluate(c.Kind, c.Version, c.ExecutablePath, c.Source))
            .OrderByDescending(f => f.Color).ToList());
    }

    [AvaloniaFact]
    public async Task Auditoria_ResumenFiltrosYDetalle()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        ShowFindings(main);

        await main.NavigateToAsync(main.AuditPage);
        var audit = main.AuditPage;
        Assert.Equal(3, audit.Red);
        Assert.Equal(2, audit.Green);
        Assert.Equal(3, main.RoostNav[0].Badge);   // el contador rojo de la barra lateral
        var laragon = audit.Rows.Single(r => r.Source == "Laragon");
        audit.ChooseCommand.Execute(laragon);
        Assert.Equal("VULNERABLE · KEV", laragon.Verdict);
        Assert.Equal("Instalar 8.3.36", audit.ActionLabel);
        Assert.True(audit.HasAdvisories);
        Assert.True(audit.HasMitigations);
        Assert.Contains("8.3.36", audit.RealFix);
        Snapshot(window, "auditoria");

        audit.ShowGreen = true;
        Assert.All(audit.Rows, r => Assert.Equal(Roost.Desktop.Controls.Tone.Ok, r.Tone));
        audit.ShowAll = true;
        audit.IncludeOutside = false;
        Assert.All(audit.Rows, r => Assert.True(r.IsRoost));
    }

    [AvaloniaFact]
    public async Task Ajustes_PuertosTemaYCa()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Settings);
        var settings = main.Settings;
        Assert.Equal("80", settings.HttpText);
        Assert.Equal("4", settings.PoolText);
        Assert.False(settings.CaExists);
        Snapshot(window, "ajustes");

        // El puerto de la base de datos ya no se edita aquí (BD-1): solo se evita pisarlo.
        settings.HttpsText = "3308";
        await settings.ApplyPortsCommand.ExecuteAsync(null);
        Assert.Equal("El 3308 es el de la base de datos (se cambia en Bases de datos).", settings.PortsError);
        settings.HttpsText = "443";

        // El tema oscuro repinta todo y se guarda en state/desktop.json.
        settings.ThemeDark = true;
        Assert.Equal("dark", main.App.Prefs.Theme);
        await main.NavigateToAsync(main.Home);
        Snapshot(window, "inicio-oscuro");
        App.ApplyTheme("system");
    }

    [AvaloniaFact]
    public async Task Bienvenida_ProponeLasRamasQueSeUsan()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        ShowFindings(main);
        Directory.Delete(Path.Combine(fake.Paths.PhpBin, "8.3.36-nts-x64"), recursive: true);

        var showing = main.ShowWelcomeAsync(force: true);
        var welcome = Assert.IsType<WelcomeDialogViewModel>(main.Dialog);
        await welcome.LoadAsync();
        Assert.Contains(welcome.Found, g => g.Source == "Laragon" && g.Chip == "1 vulnerable");
        Assert.Contains(welcome.Found, g => g.Source == "Program Files" && g.Chip == "1 EOL");
        // 8.3 es la rama que se usa fuera y no está en Roost; la 8.4 ya está.
        Assert.Equal("Instalar en Roost la última de cada rama de PHP que usas (8.3.36)", welcome.PhpText);
        Assert.True(welcome.InstallPhp);
        Assert.Equal("Y hacer global la 8.3. Las de Laragon siguen donde están.", welcome.PhpHint);
        Assert.False(welcome.InstallDb);   // ya hay MariaDB en Roost
        Assert.Equal("mi-proyecto-2", WelcomeDialogViewModel.SiteName("Mi Proyecto_2"));
        Snapshot(window, "bienvenida");

        welcome.SkipCommand.Execute(null);
        await showing;
        Assert.True(main.App.Prefs.WelcomeDone);
    }

    [AvaloniaFact]
    public async Task Actualizaciones_AvisaDeUnaVersionNueva()
    {
        // Por si quien lanza los tests ha desactivado el aviso (CI): aquí se prueba justo eso.
        Environment.SetEnvironmentVariable(Roost.Core.Updates.UpdateChecker.DisableVariable, null);
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        Assert.False(main.Updates.HasUpdate);   // sin caché todavía no se sabe nada

        // Lo que hace la app al abrirse: FakeUpstream hace de GitHub con una 0.2.0.
        await main.Updates.AutoCheckAsync();
        Assert.Equal(1, fake.Upstream.RoostLatestRequests);
        Assert.True(main.Updates.HasUpdate);
        Assert.Equal("0.2.0", main.Updates.LatestVersion);
        Assert.Equal("Roost 0.2.0 disponible", main.Updates.SideText);
        Assert.True(File.Exists(Path.Combine(fake.Paths.Cache, "update-check.json")));
        Snapshot(window, "aviso-actualizacion");

        // El aviso de la barra lateral lleva a Ajustes, con las novedades y cómo actualizar.
        await main.OpenUpdatesCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(main.Current);
        Assert.Equal("0.1.0", settings.Version);
        Assert.Equal(
            ["- nginx 1.30.7, con el arreglo de CVE-2026-1234.", "- MySQL 8.4.12 y el dataset de avisos de noviembre.", "- roost update y el aviso de versiones nuevas en la app."],
            settings.Updates.NotesLines);
        Assert.StartsWith("Roost 0.2.0 ya está disponible desde el ", settings.Updates.BannerTitle);
        Assert.StartsWith("Hay una versión nueva: Roost 0.2.0 · comprobado ", settings.Updates.StatusText);
        Assert.True(settings.CheckUpdatesOnLaunch);
        Snapshot(window, "ajustes-actualizacion");

        // Cómo actualizar depende de cómo se instaló (aquí, compilada: el test corre en bin\Debug).
        main.Updates.Channel = Roost.Core.Updates.InstallChannel.Manual;
        Assert.False(main.Updates.HasCommand);
        Assert.True(main.Updates.HasDownload);
        Assert.Equal("Descargar roost-0.2.0-win-x64.zip", main.Updates.DownloadText);
        Snapshot(window, "ajustes-actualizacion-zip");
        main.Updates.Channel = Roost.Core.Updates.InstallChannel.Scoop;
        Assert.Equal("scoop update roost", main.Updates.Command);
        Assert.False(main.Updates.HasDownload);

        Dispatcher.UIThread.RunJobs();   // que el aviso ya tenga su alto antes de bajar hasta el final
        window.GetVisualDescendants().OfType<SettingsView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        Snapshot(window, "ajustes-acerca-de");

        // La casilla se guarda en state/desktop.json y, quitada, la app ya no pregunta al abrirse.
        settings.CheckUpdatesOnLaunch = false;
        Assert.False(DesktopPrefs.Load(fake.Paths).CheckUpdates);
        var reopened = new MainViewModel(new AppServices(fake.Paths, fake.Http, version: FakeRoost.Version));
        Assert.True(reopened.Updates.HasUpdate);   // de la caché, sin red
        await reopened.Updates.AutoCheckAsync();
        Assert.Equal(1, fake.Upstream.RoostLatestRequests);

        // «Buscar actualizaciones» pregunta aunque la caché sea de hace un momento.
        await settings.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(2, fake.Upstream.RoostLatestRequests);
    }

    [AvaloniaFact]
    public async Task Actualizaciones_AlDia_SinAviso()
    {
        var (main, window, fake) = await OpenAsync(running: false, version: "0.2.0");
        using var _ = fake;

        await main.NavigateToAsync(main.Settings);
        await main.Settings.CheckUpdatesCommand.ExecuteAsync(null);

        Assert.False(main.Updates.HasUpdate);
        Assert.Equal("Roost 0.2.0 está al día", main.FlashText);
        Assert.StartsWith("Estás en la última versión · comprobado ", main.Updates.StatusText);
        Assert.Equal(Roost.Desktop.Controls.Tone.Ok, main.Updates.StatusTone);
    }

    private sealed class RedisPage(MainViewModel main) : PageViewModel(main)
    {
        public override string Title => "Redis";
    }

    /// <summary>Una extensión con una pantalla, como sería un Redis de Roost Pro.</summary>
    private sealed class RedisExtension : Roost.Desktop.Extensibility.IDesktopExtension
    {
        public string Id => "redis";
        public string Name => "Redis de prueba";
        public string Version => "0.1.0";

        public IEnumerable<Roost.Desktop.Extensibility.DesktopPage> Pages(MainViewModel main) =>
        [
            new("Redis", Roost.Desktop.Extensibility.DesktopSection.Components,
                "M4 6 A8 3 0 1 0 20 6 A8 3 0 1 0 4 6 Z M4 6 V18 C4 19.7 7.6 21 12 21 C16.4 21 20 19.7 20 18 V6",
                new RedisPage(main),
                () => new Avalonia.Controls.TextBlock { Text = "Pantalla de una extensión", Margin = new Avalonia.Thickness(24) }),
        ];
    }

    [AvaloniaFact]
    public async Task Extension_AnadeSuPantallaALaBarraLateral()
    {
        var fake = new FakeRoost();
        using var _ = fake;
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, new Roost.Extensibility.ExtensionSet([new RedisExtension()])));
        var window = new MainWindow { DataContext = main };
        window.Show();

        Assert.Equal(["PHP", "Servidor web", "Bases de datos", "Redis"], main.ComponentNav.Select(n => n.Label));
        Assert.Equal(["Ajustes", "Novedades"], main.RoostNav.TakeLast(2).Select(n => n.Label));
        Assert.NotNull(main.ComponentNav[^1].Icon);

        await main.NavigateToAsync(main.ComponentNav[^1].Page);
        Snapshot(window, "extension");
        Assert.Contains("Redis de prueba 0.1.0", main.Settings.ExtensionsText);
    }

    // BD-1

    [AvaloniaFact]
    public async Task BasesDeDatos_ConContrasenaDeRootGuardada_LaEnsenaEnmascarada_YEnElEnv()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var db11 = new Roost.Core.Install.InstalledDb(Roost.Core.Install.DbEngine.MariaDb, "11.4.13", Path.Combine(fake.Paths.DbBin("mariadb"), "11.4.13"));
        new Roost.Core.Stack.DbPasswords(fake.Paths).Set(db11, "S3cret!");
        main.NotifyConfigChanged();

        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        Assert.True(db.HasPassword);
        Assert.Equal("••••••••", db.PasswordMask);
        Assert.Equal("DB_HOST=127.0.0.1  DB_PORT=3308  DB_USERNAME=root  DB_PASSWORD=S3cret!", db.EnvLine);
        Assert.EndsWith(Path.Combine("etc", "mariadb", "11.4", "usuario.cnf"), db.UserFilePath);
        Assert.Equal("11.4", db.Installed[0].UseSpec);   // una sola de la rama: «Usar» guarda la rama
        Assert.Empty(db.Clients);                        // en este equipo no hay HeidiSQL, TablePlus ni DBeaver
        Assert.Equal("El 3308 lo usa Roost", db.PortTitle);
        Snapshot(window, "bases-de-datos-con-contrasena");

        // Más abajo: las bases de datos del servidor y los usuarios (sin cliente real, con su aviso).
        Assert.True(db.HasDatabasesError);
        Assert.True(db.HasUsersError);
        Assert.Empty(db.Users);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.DataContext == db);
        scroll.Offset = new Avalonia.Vector(0, 2000);
        Snapshot(window, "bases-de-datos-usuarios");

        // Sin contraseña: la máscara dice que está vacía y el .env la lleva vacía.
        new Roost.Core.Stack.DbPasswords(fake.Paths).Set(db11, null);
        main.NotifyConfigChanged();
        Assert.False(db.HasPassword);
        Assert.Equal("(vacía)", db.PasswordMask);
        Assert.EndsWith("DB_PASSWORD=", db.EnvLine);
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_CambiarContrasenaDeRoot_AbreElDialogo()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Databases);
        var changing = main.Databases.ChangePasswordCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<DbPasswordDialogViewModel>(main.Dialog);
        Assert.Equal("Contraseña de root", dialog.Title);
        Assert.True(dialog.ForRoot);
        Assert.True(dialog.CanConfirm);              // vacía = quitar la contraseña
        Assert.Equal("Quitar la contraseña", dialog.ConfirmLabel);

        dialog.Password = "con espacio";
        Assert.False(dialog.CanConfirm);
        Assert.StartsWith("Solo letras", dialog.Hint);
        dialog.Password = "S3cret!";
        dialog.Repeat = "S3cret";
        Assert.Equal("No coinciden.", dialog.Hint);
        dialog.Repeat = "S3cret!";
        Assert.True(dialog.CanConfirm);
        Assert.Equal("Cambiar", dialog.ConfirmLabel);
        dialog.SaveOnly = true;
        Assert.Equal("Guardar en Roost", dialog.ConfirmLabel);
        dialog.Show = true;
        Snapshot(window, "bases-de-datos-contrasena");

        dialog.Close(null);
        await changing;
        Assert.False(main.Databases.HasPassword);   // cancelado: nada cambia
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_NuevoUsuario_AbreElDialogoConElAcceso()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Databases);
        var creating = main.Databases.NewUserCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<DbUserDialogViewModel>(main.Dialog);
        Assert.Equal("Todas las bases de datos", dialog.Options[0].Label);
        Assert.Null(dialog.Access.Database);
        Assert.False(dialog.CanConfirm);

        dialog.Name = "mi-tienda";
        Assert.Equal("Solo letras, números y _ (hasta 32).", dialog.NameHint);
        dialog.Name = "root";
        Assert.Contains("cuenta del servidor", dialog.NameHint);
        dialog.Name = "tienda";
        dialog.Password = "S3cret!";
        dialog.Repeat = "S3cret!";
        Assert.True(dialog.CanConfirm);
        Assert.StartsWith("Todos los privilegios sobre todas", dialog.AccessHint);
        Snapshot(window, "bases-de-datos-usuario");

        dialog.Close(null);
        await creating;
    }

    [AvaloniaFact]
    public async Task Ajustes_ElPuertoDeLaBaseDeDatos_RemiteABasesDeDatos()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Settings);
        await main.Settings.GoDatabasesCommand.ExecuteAsync(null);

        Assert.Same(main.Databases, main.Current);
        Assert.Equal("3308", main.Databases.PortText);
        Assert.False(main.Databases.CanApplyPort);
        Snapshot(window, "bases-de-datos");
    }

    // Sitios-1: la raíz web, los avisos de carpetas que faltan y la confirmación del PHP.
    // Nada de aquí llega a ApplyAsync: con el estado falso hablaría con el daemon de verdad.

    [AvaloniaFact]
    public async Task Sitios_RaizWeb_AccesosRapidos_YAvisoDeCarpetaQueFalta()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        var tienda = sites.Rows.Single(r => r.Host == "tienda.test");
        sites.ChooseCommand.Execute(tienda);
        Assert.Equal("public/", sites.RootText);
        Assert.True(sites.HasSubRoot);
        Assert.True(sites.FolderExists);
        Assert.False(sites.HasPathWarning);
        Assert.Empty(sites.RootChoices);   // public/ ya es la raíz: no se ofrece

        // Aparecen web/ y dist/ en el proyecto: se ofrecen como accesos rápidos, en el orden de siempre.
        Directory.CreateDirectory(Path.Combine(tienda.Site.Folder, "dist"));
        Directory.CreateDirectory(Path.Combine(tienda.Site.Folder, "web"));
        main.NotifyConfigChanged();
        Assert.Equal(["web", "dist"], sites.RootChoices.Select(c => c.Root));
        Assert.Equal("web/", sites.RootChoices[0].Label);
        Assert.Equal("tienda.test", sites.Selected?.Host);
        Snapshot(window, "sitios-raiz-web");

        // La carpeta de legacy no existe: se avisa y no hay nada que elegir.
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "legacy.test"));
        Assert.Contains("ya no existe", sites.PathWarning);
        Assert.False(sites.FolderExists);
        Assert.False(sites.HasSubRoot);
        Assert.Empty(sites.RootChoices);
        Snapshot(window, "sitios-carpeta-que-falta");

        // La raíz web de tienda desaparece: otro aviso, con el botón de volver a la carpeta.
        Directory.Delete(Path.Combine(tienda.Site.Folder, "public"), recursive: true);
        main.NotifyConfigChanged();
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        Assert.Contains("raíz web public/ ya no existe", sites.PathWarning);
        Assert.True(sites.HasSubRoot);
        Assert.Equal(["web", "dist"], sites.RootChoices.Select(c => c.Root));
    }

    [AvaloniaFact]
    public async Task Sitios_CambiarElPhp_PideConfirmacion_YSiSeCancelaVuelveAlDeAntes()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "blog.test"));
        Assert.Null(sites.SelectedPhp?.Spec);   // el global

        sites.SelectedPhp = sites.PhpOptions.Single(o => o.Spec == "7.4");
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var dialog = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("Cambiar el PHP de blog.test", dialog.Title);
        Assert.Contains("rearranca Roost", dialog.Text);
        Snapshot(window, "sitios-confirmar-php");

        dialog.Close(false);   // Cancelar
        await UntilAsync(() => main.Dialog is null && sites.SelectedPhp?.Spec is null);
        Assert.Null(new Roost.Core.Sites.SiteRegistry(fake.Paths).Find("blog")!.Php);   // el registro no se tocó
    }

    [AvaloniaFact]
    public async Task NuevoSitio_RaizWeb_SeRellenaConPublic_YSoloAdmiteSubcarpetasQueExisten()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);

        dialog.Name = "tienda";   // www\tienda existe y tiene public/
        Assert.True(dialog.FolderExists);
        Assert.Equal("public", dialog.Root);
        Assert.True(dialog.RootValid);
        Assert.StartsWith("Se sirve public/", dialog.RootHint);
        Assert.EndsWith("(raíz web: public/)", dialog.StepFolder);
        Assert.True(dialog.CanCreate);
        Snapshot(window, "nuevo-sitio-raiz-web");

        dialog.Root = "dist";
        Assert.False(dialog.RootValid);
        Assert.False(dialog.CanCreate);
        Assert.Contains("No existe dist/", dialog.RootHint);
        dialog.Root = "..";
        Assert.Contains("subcarpeta", dialog.RootHint);
        dialog.Root = "";
        Assert.True(dialog.CanCreate);
        Assert.DoesNotContain("raíz web", dialog.StepFolder);

        // Otra carpeta, que no existe: la raíz vuelve a proponerse (vacía) y una tecleada no vale.
        dialog.Name = "mi-blog";
        Assert.Equal("", dialog.Root);
        Assert.True(dialog.CanCreate);
        dialog.Root = "public";
        Assert.False(dialog.CanCreate);
        Assert.Contains("no existe aún", dialog.RootHint);

        // Y de vuelta a una carpeta con public/, se vuelve a rellenar sola.
        dialog.Folder = Path.Combine(fake.Paths.Www, "tienda");
        Assert.Equal("public", dialog.Root);
        Assert.True(dialog.CanCreate);

        dialog.Close();   // sin crear: crear aplicaría al stack
        await opening;
    }

    /// <summary>Espera, dejando correr el hilo de UI, a que se cumpla <paramref name="condition"/> (como mucho 5 s).</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Novedades_DebajoDeAjustes_ConLaVersionQueTienes()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        Assert.Equal(["Ajustes", "Novedades"], main.RoostNav.TakeLast(2).Select(n => n.Label));
        await main.NavigateToAsync(main.News);
        var news = main.News;
        Assert.NotEmpty(news.Releases);
        Assert.Single(news.Releases, r => r.IsCurrent);
        Assert.All(news.Releases, r => Assert.NotEmpty(r.Items));
        Assert.Contains(news.Releases.SelectMany(r => r.Items), i => i.Title == "Apache, además de nginx: ");
        Snapshot(window, "novedades");
    }

    // Composer y Sitios-2: el bloque Composer y la base de datos del panel del sitio, el diálogo de
    // salida en vivo y Nuevo sitio (proyecto detectado, base de datos, otra versión de PHP). Composer
    // se ejecuta sobre un runner guionizado: nada llega a PHP ni a la red.

    /// <summary>Baja el scroll de la pantalla para que se vea lo de abajo del panel.</summary>
    private static void ScrollPage(MainWindow window, object page, double y)
    {
        Dispatcher.UIThread.RunJobs();
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.DataContext == page);
        scroll.Offset = new Avalonia.Vector(0, y);
    }

    [AvaloniaFact]
    public async Task SitiosComposer_PanelConComposerYSuBaseDeDatos()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        Assert.True(sites.HasComposer);
        Assert.True(sites.ComposerNeedsInstall);   // composer.json sin vendor/
        Assert.Equal("falta vendor/", sites.ComposerChip);
        Assert.Contains("PHP 8.4.27", sites.ComposerText);
        Assert.Contains("se instala la primera vez", sites.ComposerText);

        // Su base de datos: la del nombre, en la global en marcha. Sin cliente de verdad no se sabe si
        // existe, así que se ofrecen las dos cosas.
        Assert.Equal("tienda", sites.SiteDbName);
        Assert.True(sites.SiteDbRunning);
        Assert.Equal("en MariaDB 11.4.13 · 127.0.0.1:3308", sites.SiteDbWhere);
        Assert.True(sites.SiteDbCanCreate);
        Assert.True(sites.SiteDbCanOpen);
        Assert.Equal("Crear tienda", sites.CreateSiteDbLabel);
        Assert.Equal("DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT=3308\nDB_DATABASE=tienda\nDB_USERNAME=root\nDB_PASSWORD=", sites.SiteEnv());
        ScrollPage(window, sites, 260);
        Snapshot(window, "sitios-composer-y-bd");

        // Con vendor/, el aviso se va.
        Directory.CreateDirectory(Path.Combine(sites.Selected!.Site.Folder, "vendor"));
        main.NotifyConfigChanged();
        Assert.False(sites.ComposerNeedsInstall);
        Assert.Equal("vendor/ instalado", sites.ComposerChip);

        // Un sitio sin composer.json no tiene bloque Composer; uno con su base de datos fijada, la enseña.
        new Roost.Core.Sites.SiteManager(fake.Paths).SetDatabase("blog", "wp_blog");
        main.NotifyConfigChanged();
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "blog.test"));
        Assert.False(sites.HasComposer);
        Assert.Equal("wp_blog", sites.SiteDbName);

        // Parado: el panel lo dice y ofrece arrancar, en vez de fallar al pulsar.
        main.Stack.Show(null);
        main.NotifyConfigChanged();
        Assert.True(sites.SiteDbStopped);
        Assert.False(sites.SiteDbCanCreate);
        Assert.False(sites.SiteDbCanOpen);
        Assert.Contains("está parada", sites.SiteDbHint);
        ScrollPage(window, sites, 260);
        Snapshot(window, "sitios-bd-parada");

        // Sin ninguna base de datos instalada: a Bases de datos.
        Directory.Delete(fake.Paths.DbBin("mariadb"), recursive: true);
        main.NotifyConfigChanged();
        Assert.True(sites.SiteDbMissing);
        Assert.False(sites.HasSiteDbEnv);
        Assert.StartsWith("No hay ninguna base de datos instalada", sites.SiteDbHint);
    }

    [AvaloniaFact]
    public async Task SitiosComposer_InstalarDependencias_SalidaEnVivo_YTerminaConFallo()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var phar = fake.InstallFakeComposer();
        var runner = new ScriptedCommandRunner(
            "Installing dependencies from lock file (including require-dev)",
            "Verifying lock file contents can be installed on current platform.",
            "Package operations: 112 installs, 0 updates, 0 removals",
            "  - Installing doctrine/inflector (2.0.10): Extracting archive",
            "  - Installing symfony/polyfill-mbstring (v1.31.0): Extracting archive",
            "  - Installing laravel/framework (v11.31.0): Extracting archive");

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        var running = sites.ComposerInstallCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is CommandOutputDialogViewModel);
        var dialog = (CommandOutputDialogViewModel)main.Dialog!;
        dialog.FlushLines();

        // La orden: el PHP del sitio con el composer.phar de Roost, en su carpeta y sin preguntas.
        var spec = runner.Calls.Single();
        Assert.Equal(Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", "php.exe"), spec.Exe);
        Assert.Equal([phar, "install"], spec.Arguments);
        Assert.Equal(sites.Selected!.Site.Folder, spec.WorkingDirectory);
        Assert.Equal("1", spec.Environment!["COMPOSER_NO_INTERACTION"]);
        Assert.Equal(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64"), spec.Environment["PHPRC"]);
        Assert.True(dialog.Running);
        Assert.False(dialog.CanDismiss);   // ni Escape ni el fondo lo cierran a medias
        Assert.Equal("composer install", dialog.CommandText);
        Assert.Equal(6, dialog.Lines.Count);
        Assert.True(sites.ComposerBusy);
        Snapshot(window, "salida-en-vivo-en-marcha");

        runner.Report("Generating optimized autoload files");
        runner.Report("> @php artisan package:discover --ansi");
        runner.Report("");
        runner.Report("In ProviderRepository.php line 205:");
        runner.Report("  The bootstrap/cache directory must be present and writable.");
        runner.Report("Script @php artisan package:discover --ansi handling the post-autoload-dump event returned with error code 1");
        runner.Finish(1);
        await UntilAsync(() => dialog.Done);

        Assert.True(dialog.Failed);
        Assert.Equal(1, dialog.ExitCode);
        Assert.StartsWith("Ha fallado con el código 1", dialog.ResultText);
        Assert.True(dialog.Lines[^1].IsError);
        Assert.Contains("laravel/framework", dialog.Output);
        Assert.True(dialog.CanDismiss);
        Snapshot(window, "salida-en-vivo-fallo");

        dialog.FinishCommand.Execute(null);
        await running;
        Assert.False(sites.ComposerBusy);
        Assert.Null(main.Dialog);
    }

    [AvaloniaFact]
    public async Task SalidaEnVivo_CancelarParaElProceso_YElProgresoNoLlenaLaSalida()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        var runner = new ScriptedCommandRunner("Cloning into 'tienda'...");
        var spec = new Roost.Core.Scanning.CommandSpec("git.exe", ["clone", "--progress", "https://example.test/acme/tienda.git"], fake.Paths.Www);
        var dialog = new CommandOutputDialogViewModel("Clonar acme/tienda", "git", spec, runner) { CommandText = "git clone acme/tienda" };

        var run = dialog.RunAsync();
        var shown = main.ShowDialogAsync(dialog);
        runner.Report("Receiving objects:  10% (10/100)");
        runner.Report("Receiving objects:  55% (55/100), 1.20 MiB | 2.40 MiB/s");
        runner.Report("Receiving objects: 100% (100/100), 2.10 MiB | 2.40 MiB/s, done.");
        runner.Report("Resolving deltas:  50% (5/10)");
        dialog.FlushLines();
        Assert.Equal(["Cloning into 'tienda'...", "Receiving objects: 100% (100/100), 2.10 MiB | 2.40 MiB/s, done.", "Resolving deltas:  50% (5/10)"],
            dialog.Lines.Select(l => l.Text));
        Assert.False(dialog.CanCopy);   // sin portapapeles, sin botón

        dialog.CancelCommand.Execute(null);
        await run;
        Assert.True(runner.WasCanceled);
        Assert.True(dialog.Canceled);
        Assert.True(dialog.Failed);
        Assert.Null(dialog.ExitCode);
        Assert.StartsWith("Cancelado", dialog.ResultText);
        Assert.Null(await dialog.Finished);
        dialog.CancelCommand.Execute(null);   // ya terminado: «Cancelar» cierra
        await shown;
    }

    [AvaloniaFact]
    public async Task SitiosComposer_AnadirPaquete_ValidaElNombre()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        var adding = sites.ComposerRequireCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is ComposerRequireDialogViewModel);
        var dialog = (ComposerRequireDialogViewModel)main.Dialog!;
        Assert.Equal("En tienda.test · con PHP 8.4.27", dialog.Subtitle);
        Assert.False(dialog.CanConfirm);

        dialog.Package = "monolog";
        Assert.False(dialog.CanConfirm);
        Assert.True(dialog.HintIsError);
        dialog.Package = "Monolog/Monolog:^3";
        Assert.True(dialog.CanConfirm);
        Assert.Equal("composer require monolog/monolog:^3", dialog.Hint);
        dialog.Dev = true;
        Assert.Equal("composer require --dev monolog/monolog:^3", dialog.Hint);
        Snapshot(window, "sitios-composer-anadir-paquete");

        dialog.Close(null);   // sin añadir: nada se ejecuta
        await adding;
        Assert.Null(main.Dialog);
    }

    [AvaloniaFact]
    public async Task NuevoSitio_DetectaLaravel_YSacaElNombreDeLaCarpeta()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var laravel = fake.Project("Tienda Laravel", "artisan", "composer.json", "public/", "bootstrap/");

        await main.NavigateToAsync(main.Sites);
        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        dialog.Folder = laravel;   // como «Elegir carpeta» con el nombre aún vacío

        Assert.Equal("tienda-laravel", dialog.Name);
        Assert.True(dialog.NameValid);
        Assert.Equal("Proyecto Laravel", dialog.ProjectLabel);
        Assert.Equal("Proyecto Laravel: raíz web public/.", dialog.FolderHint);
        Assert.Equal("public", dialog.Root);
        Assert.True(dialog.NeedsComposerInstall);
        Assert.StartsWith("8.4.27 · recomendada por composer.json (^8.2)", dialog.Php?.Label);
        Assert.Equal("tienda_laravel", dialog.DbName);
        Assert.True(dialog.DbIsCreate);
        Assert.True(dialog.CanCreate);
        Snapshot(window, "nuevo-sitio-laravel");

        // La base de datos: una que ya existe (sin lista, porque no hay cliente: se teclea) o ninguna.
        dialog.DbIsExisting = true;
        Assert.True(dialog.ShowExistingText);
        Assert.False(dialog.CanCreate);
        dialog.ExistingDb = "mysql";
        Assert.False(dialog.DbValid);
        dialog.ExistingDb = "tienda_vieja";
        Assert.True(dialog.CanCreate);
        Assert.Equal("tienda_vieja", dialog.ChosenDb);
        Assert.False(dialog.CreateDatabase);
        Snapshot(window, "nuevo-sitio-bd-existente");
        dialog.DbIsNone = true;
        Assert.Null(dialog.ChosenDb);
        dialog.DbIsCreate = true;
        dialog.DbName = "mi base";
        Assert.False(dialog.CanCreate);
        dialog.DbName = "shop";
        Assert.True(dialog.CanCreate);
        Assert.Equal("shop", dialog.ChosenDb);

        // Tecleado el nombre, elegir otra carpeta ya no lo cambia. WordPress se sirve desde su carpeta.
        dialog.Name = "mi-wp";
        dialog.Folder = fake.Project("blog-viejo", "wp-load.php", "wp-includes/");
        Assert.Equal("mi-wp", dialog.Name);
        Assert.Equal("Proyecto WordPress", dialog.ProjectLabel);
        Assert.Equal("Proyecto WordPress: se sirve la carpeta.", dialog.FolderHint);
        Assert.Equal("", dialog.Root);
        Assert.False(dialog.NeedsComposerInstall);

        dialog.Close();   // sin crear: crear aplicaría al stack
        await opening;
    }

    [AvaloniaFact]
    public async Task NuevoSitio_InstalarOtraVersion_SinCerrarNiPerderLoTecleado()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        dialog.Name = "crm";

        await dialog.InstallOtherCommand.ExecuteAsync(null);

        Assert.Same(dialog, main.Dialog);   // sigue abierto (antes se cerraba y llevaba a PHP)
        Assert.Equal("crm", dialog.Name);
        Assert.True(dialog.ShowInstallChoices);
        // Las ramas del catálogo que no están instaladas (8.4, 8.3 y 7.4 ya están), con la EOL marcada.
        Assert.Equal(["8.5.12", "8.2.35", "8.1.34 · EOL"], dialog.InstallChoices.Select(c => c.Label));
        Assert.StartsWith("Elige una", dialog.InstallChoicesHint);
        Snapshot(window, "nuevo-sitio-instalar-php");

        dialog.Close();
        await opening;
    }
}
