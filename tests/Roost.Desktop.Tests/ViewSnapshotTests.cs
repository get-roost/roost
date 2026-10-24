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

        // El aviso de la barra lateral abre el modal (Actualizar_*); en Ajustes siguen las novedades y cómo actualizar.
        await main.NavigateToAsync(main.Settings);
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

    private sealed class MemcachedPage(MainViewModel main) : PageViewModel(main)
    {
        public override string Title => "Memcached de prueba";
    }

    /// <summary>Una extensión con una pantalla (Redis ya es de Roost: la de pega es un Memcached).</summary>
    private sealed class MemcachedExtension : Roost.Desktop.Extensibility.IDesktopExtension
    {
        public string Id => "memcached";
        public string Name => "Memcached de prueba";
        public string Version => "0.1.0";

        public IEnumerable<Roost.Desktop.Extensibility.DesktopPage> Pages(MainViewModel main) =>
        [
            new("Memcached de prueba", Roost.Desktop.Extensibility.DesktopSection.Components,
                "M4 6 A8 3 0 1 0 20 6 A8 3 0 1 0 4 6 Z M4 6 V18 C4 19.7 7.6 21 12 21 C16.4 21 20 19.7 20 18 V6",
                new MemcachedPage(main),
                () => new Avalonia.Controls.TextBlock { Text = "Pantalla de una extensión", Margin = new Avalonia.Thickness(24) }),
        ];
    }

    [AvaloniaFact]
    public async Task Extension_AnadeSuPantallaALaBarraLateral()
    {
        var fake = new FakeRoost();
        using var _ = fake;
        var main = new MainViewModel(new AppServices(fake.Paths, fake.Http, new Roost.Extensibility.ExtensionSet([new MemcachedExtension()])));
        var window = new MainWindow { DataContext = main };
        window.Show();

        // Las de Roost primero (Redis y Correo ya son suyas); las de las extensiones, al final de su sección.
        Assert.Equal(["PHP", "Servidor web", "Bases de datos", "Redis", "Correo", "Memcached de prueba"], main.ComponentNav.Select(n => n.Label));
        Assert.Equal(["Ajustes", "Novedades"], main.RoostNav.TakeLast(2).Select(n => n.Label));
        Assert.NotNull(main.ComponentNav[^1].Icon);

        await main.NavigateToAsync(main.ComponentNav[^1].Page);
        Snapshot(window, "extension");
        Assert.Contains("Memcached de prueba 0.1.0", main.Settings.ExtensionsText);
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
        Assert.Contains("sin cortar", dialog.Text);
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

    // ---- Actualizador ----

    /// <summary>Un lanzador que no lanza nada: apunta el setup y sus argumentos, el vigilante y la terminal.</summary>
    private sealed class FakeLauncher : Roost.Core.Updates.UpdateLauncher
    {
        public string? Setup { get; private set; }
        public string? Arguments { get; private set; }
        public int? WatchedPid { get; private set; }
        public string? WatchedApp { get; private set; }
        public string? Terminal { get; private set; }

        public override int StartSetup(string setupPath, string arguments)
        {
            Setup = setupPath;
            Arguments = arguments;
            return 4321;
        }

        public override void StartWatchdog(int setupPid, string appExe)
        {
            WatchedPid = setupPid;
            WatchedApp = appExe;
        }

        public override void StartTerminal(string script) => Terminal = script;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Se ha esperado demasiado a: {what}");
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>La app con una 0.2.0 disponible, un lanzador de mentira y una salida que no sale.</summary>
    private static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake, FakeLauncher Launcher, Func<bool> Exited)> OpenWithUpdateAsync(
        bool running, Roost.Core.Updates.InstallChannel channel)
    {
        Environment.SetEnvironmentVariable(Roost.Core.Updates.UpdateChecker.DisableVariable, null);
        var (main, window, fake) = await OpenAsync(running);
        await main.Updates.AutoCheckAsync();
        Assert.True(main.Updates.HasUpdate);
        main.Updates.Channel = channel;
        var launcher = new FakeLauncher();
        main.Updates.Launcher = launcher;
        var exited = false;
        main.Updates.ExitApp = () => exited = true;
        return (main, window, fake, launcher, () => exited);
    }

    [AvaloniaFact]
    public async Task Actualizar_ElModalYLosPasos_HastaLanzarElInstalador()
    {
        var (main, window, fake, launcher, exited) = await OpenWithUpdateAsync(running: true, Roost.Core.Updates.InstallChannel.Installer);
        using var _ = fake;

        // El aviso de la barra lateral (también la bandeja y Ajustes) abre el modal «Roost 0.2.0 disponible».
        var flow = main.OpenUpdatesCommand.ExecuteAsync(null);
        var modal = Assert.IsType<UpdateDialogViewModel>(main.Dialog);
        Assert.Equal("Roost 0.2.0 disponible", modal.Title);
        Assert.StartsWith("Publicada el 2-nov-2026 · tienes la 0.1.0", modal.Subtitle);
        Assert.Equal(("0.1.0", "0.2.0"), (modal.Installed, modal.Version));
        Assert.True(modal.HasSize);
        Assert.Equal(
            ["nginx 1.30.7, con el arreglo de CVE-2026-1234.", "MySQL 8.4.12 y el dataset de avisos de noviembre.", "roost update y el aviso de versiones nuevas en la app."],
            modal.Notes.Select(n => n.Text));
        Assert.All(modal.Notes, n => Assert.True(n.IsBullet));   // el «## Novedades» de las notas sobra: el modal ya lo pone
        Assert.StartsWith("roost-0.2.0-win-x64-setup.exe · ", modal.DownloadText);
        Assert.Equal("Actualizar ahora", modal.PrimaryLabel);
        Assert.Contains("Windows pedirá permiso una vez", modal.Explanation);
        Snapshot(window, "actualizar-modal");

        // «Actualizar ahora»: descarga el setup de mentira, verifica su SHA-256 con el SHA256SUMS.txt
        // de la release, mira la firma (no lleva), lo «lanza» con el vigilante detrás y sale.
        modal.PrimaryCommand.Execute(null);
        await WaitUntilAsync(exited, "que la app salga tras lanzar el instalador");
        var progress = Assert.IsType<UpdateProgressDialogViewModel>(main.Dialog);
        Assert.True(progress.Succeeded);
        Assert.All(progress.Steps, s => Assert.Equal(StepState.Done, s.State));
        Assert.StartsWith("Instalador en marcha", progress.Phase);
        // El setup de mentira no va firmado (como las releases de hoy): se dice, y vale el SHA-256.
        Assert.Contains("vale el SHA-256", progress.Steps[2].Hint);
        Snapshot(window, "actualizar-pasos");

        Assert.Equal(Path.Combine(fake.Paths.Downloads, "roost-0.2.0-win-x64-setup.exe"), launcher.Setup);
        Assert.Equal(FakeUpstream.RoostSetup, await File.ReadAllTextAsync(launcher.Setup!));
        var log = Path.Combine(fake.Paths.Logs, "roost-setup.log");
        Assert.Equal($"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /ROOSTRELAUNCH=1 /ROOSTSTART=1 /LOG=\"{log}\"", launcher.Arguments);
        Assert.Equal(4321, launcher.WatchedPid);
        Assert.EndsWith("roost-desktop.exe", launcher.WatchedApp);
        var marker = Roost.Core.Updates.UpdateMarker.TryRead(fake.Paths);
        Assert.NotNull(marker);
        Assert.Equal(("0.1.0", "0.2.0", true), (marker.From, marker.To, marker.StackWasRunning));
        progress.Close();
        await flow;
    }

    [AvaloniaFact]
    public async Task Actualizar_ConUnShaQueNoCuadra_NoLanzaNada()
    {
        var (main, window, fake, launcher, exited) = await OpenWithUpdateAsync(running: false, Roost.Core.Updates.InstallChannel.Installer);
        using var _ = fake;
        fake.Upstream.CorruptSums = true;

        var flow = main.OpenUpdatesCommand.ExecuteAsync(null);
        Assert.IsType<UpdateDialogViewModel>(main.Dialog).PrimaryCommand.Execute(null);
        await WaitUntilAsync(() => main.Dialog is UpdateProgressDialogViewModel { Done: true }, "que la actualización falle");

        var progress = (UpdateProgressDialogViewModel)main.Dialog!;
        Assert.True(progress.Failed);
        Assert.Contains("SHA-256", progress.Error);
        Assert.Contains("No se ejecuta nada", progress.Error);
        Assert.Equal("No se ha actualizado", progress.Phase);
        Assert.Equal(StepState.Done, progress.Steps[0].State);     // se bajó entero...
        Assert.Equal(StepState.Failed, progress.Steps[1].State);   // ...y falla al verificarlo
        Snapshot(window, "actualizar-fallo");

        Assert.Null(launcher.Setup);
        Assert.False(exited());
        Assert.Null(Roost.Core.Updates.UpdateMarker.TryRead(fake.Paths));
        Assert.False(File.Exists(Path.Combine(fake.Paths.Downloads, "roost-0.2.0-win-x64-setup.exe")));
        progress.CancelCommand.Execute(null);   // «Cerrar»
        await flow;
    }

    [AvaloniaFact]
    public async Task Actualizar_ConScoop_SaleYAbreUnaTerminalQueActualizaYRelanza()
    {
        var (main, window, fake, launcher, exited) = await OpenWithUpdateAsync(running: false, Roost.Core.Updates.InstallChannel.Scoop);
        using var _ = fake;

        var flow = main.OpenUpdatesCommand.ExecuteAsync(null);
        var modal = Assert.IsType<UpdateDialogViewModel>(main.Dialog);
        Assert.Equal("Actualizar ahora", modal.PrimaryLabel);
        Assert.Contains("scoop update roost", modal.Explanation);
        Assert.Null(modal.DownloadText);
        Snapshot(window, "actualizar-modal-scoop");

        modal.PrimaryCommand.Execute(null);
        await flow;
        Assert.True(exited());
        // Espera a que la app y el daemon hayan salido (Scoop no actualiza una app en marcha), actualiza y la relanza.
        var lines = launcher.Terminal!.Split('\n');
        Assert.StartsWith("$dir = '", lines[0]);
        Assert.StartsWith("while ((Get-Process roost, roost-desktop", lines[2]);
        Assert.Equal("scoop update roost", lines[3]);
        Assert.StartsWith("if ($?) { Start-Process -FilePath '", lines[4]);
        Assert.EndsWith("-ArgumentList '--updated' }", lines[4]);
        Assert.Equal(("0.1.0", "0.2.0", false), Roost.Core.Updates.UpdateMarker.TryRead(fake.Paths) is { } m ? (m.From, m.To, m.StackWasRunning) : default);
        var script = UpdateFlow.TerminalScript("scoop update roost", @"C:\Users\Ana\scoop\apps\roost\current\roost-desktop.exe", startStack: true, @"C:\Users\Ana\scoop\apps\roost");
        Assert.Contains("'--updated','--start'", script);
        Assert.StartsWith(@"$dir = 'C:\Users\Ana\scoop\apps\roost\'", script);
        Assert.Contains("-FilePath 'C:\\Users\\Ana\\scoop\\apps\\roost\\current\\roost-desktop.exe'", script);
        Assert.Contains(@"'C:\O''Brien\'", UpdateFlow.TerminalScript("x", @"C:\a.exe", false, @"C:\O'Brien"));

        // Compilada desde el código no hay nada que la app pueda hacer: solo explicarlo.
        main.Updates.Channel = Roost.Core.Updates.InstallChannel.Source;
        Assert.Null(new UpdateDialogViewModel(main.Updates).PrimaryLabel);
        Assert.Equal("Ver cómo actualizar", main.Updates.UpdateLabel);
    }

    [AvaloniaFact]
    public async Task AlVolverDeActualizar_LoDice_YSiNoSeActualizoTambien()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        // Vuelve con --updated y el marcador: «Roost actualizado a la 0.1.0» (el stack ya está en marcha: no se arranca nada).
        Roost.Core.Updates.UpdateMarker.Write(fake.Paths, new("0.0.9", "0.1.0", StackWasRunning: true, DateTimeOffset.Now));
        var handling = main.Updates.HandleStartupAsync(main, StartupArgs.Parse(["--updated", "--start"]));
        var dialog = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("Roost actualizado a la 0.1.0", dialog.Title);
        Assert.Equal("Venías de la 0.0.9", dialog.Subtitle);
        Assert.Equal("Roost actualizado a 0.1.0", main.FlashText);
        Snapshot(window, "actualizado");
        dialog.Close();
        await handling;
        Assert.Null(Roost.Core.Updates.UpdateMarker.TryRead(fake.Paths));

        // El vigilante la relanza con --update-failed=2 (UAC rechazado): «No se ha actualizado».
        Roost.Core.Updates.UpdateMarker.Write(fake.Paths, new("0.1.0", "0.2.0", StackWasRunning: false, DateTimeOffset.Now));
        handling = main.Updates.HandleStartupAsync(main, StartupArgs.Parse(["--update-failed=2"]));
        dialog = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("No se ha actualizado", dialog.Title);
        Assert.Equal("Sigues en la 0.1.0; la 0.2.0 queda pendiente", dialog.Subtitle);
        Assert.StartsWith("Windows no dio permiso", dialog.Text);
        Snapshot(window, "no-actualizado");
        dialog.Close();
        await handling;

        // Se abre a mano con el marcador de una actualización que no llegó: también lo dice.
        Roost.Core.Updates.UpdateMarker.Write(fake.Paths, new("0.1.0", "0.2.0", StackWasRunning: false, DateTimeOffset.Now));
        handling = main.Updates.HandleStartupAsync(main, StartupArgs.None);
        dialog = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        Assert.Equal("No se ha actualizado", dialog.Title);
        Assert.StartsWith("La actualización no llegó a terminar", dialog.Text);
        dialog.Close();
        await handling;

        // Sin nada pendiente, nada que decir.
        await main.Updates.HandleStartupAsync(main, StartupArgs.None);
        Assert.Null(main.Dialog);

        Assert.Equal(new StartupArgs(true, true, true, 5), StartupArgs.Parse(["--minimized", "--updated", "--start", "--update-failed=5"]));
        Assert.Equal(StartupArgs.None, StartupArgs.Parse(null));
    }

    [AvaloniaFact]
    public async Task ServidorWeb_InstalarEnSegundoPlano_SiFallaLoDice()
    {
        // A6: instalar nginx, pasarlo a segundo plano y que falle la descarga: se avisa igual que en PHP y BD.
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        Directory.Delete(fake.Paths.NginxBin, recursive: true);
        await main.NavigateToAsync(main.Web);
        main.Web.StackChanged();
        Assert.False(main.Web.NginxInstalled);
        var hold = new TaskCompletionSource();
        fake.Upstream.HoldMissing = hold;

        var install = main.Web.InstallNginxCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => main.Dialog is InstallDialogViewModel or MessageDialogViewModel, "el diálogo de instalación");
        // En una máquina sin el VC++ sale antes su aviso: se sigue igualmente.
        if (main.Dialog is MessageDialogViewModel vc)
        {
            Assert.Contains("Visual C++", vc.Title);
            vc.Close("continue");
            await WaitUntilAsync(() => main.Dialog is InstallDialogViewModel, "el diálogo de instalación");
        }
        var dialog = Assert.IsType<InstallDialogViewModel>(main.Dialog);
        dialog.BackgroundCommand.Execute(null);
        await WaitUntilAsync(() => main.Dialog is null, "que se cierre al pasar a segundo plano");

        hold.SetResult();   // nginx.org da 404
        await WaitUntilAsync(() => main.Dialog is MessageDialogViewModel, "el aviso del fallo");
        var info = (MessageDialogViewModel)main.Dialog!;
        Assert.Equal($"No se instaló nginx {Roost.Core.Install.NginxCatalog.Windows.Version}", info.Title);
        Assert.Equal(Roost.Desktop.Controls.Tone.Bad, info.Tone);
        Snapshot(window, "servidor-web-instalar-fallo");
        info.Close();
        await install;
        Assert.False(main.Web.NginxInstalled);
    }

    // Daemon-1: parar e iniciar un servicio suelto (E3 + B5), y «Reiniciar todo».

    /// <summary>Lo que cuenta roostd con la base de datos parada a mano (roost stop mariadb-11.4): el resto sigue.</summary>
    private static Roost.Daemon.StatusResponse RunningWithDbStopped()
    {
        var status = FakeRoost.Running;
        return status with
        {
            Services = status.Services.Select(s => s.Name == "mariadb-11.4" ? s with { State = "Stopped", Pid = null } : s).ToList(),
        };
    }

    [AvaloniaFact]
    public async Task Inicio_ConLaBaseDeDatosParadaAMano_OfreceIniciarla_YRestaurarTodo()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        // Con todo en marcha: Parar y Reiniciar en cada fila, y Reiniciar todo arriba.
        Assert.True(main.Home.IsRunning);
        Assert.All(main.Home.Services, r => { Assert.True(r.CanStop); Assert.False(r.CanStart); Assert.True(r.CanRestart); });

        main.Stack.Show(RunningWithDbStopped());
        Dispatcher.UIThread.RunJobs();

        var db = main.Home.Services.Single(r => r.Name == "MariaDB");
        Assert.Equal("Parado", db.State);
        Assert.True(db.CanStart);
        Assert.False(db.CanStop);
        Assert.True(db.CanRestart);
        Assert.True(main.Home.Services.Single(r => r.Name == "nginx").CanStop);
        Assert.True(main.Home.IsRunning);
        Assert.StartsWith("En marcha, con mariadb-11.4 parado a mano", main.Home.Subtitle);
        Snapshot(window, "inicio-servicio-parado");

        // Parado del todo, no hay nada que parar, iniciar ni reiniciar.
        main.Stack.Show(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(main.Home.IsRunning);
        Assert.All(main.Home.Services, r => { Assert.False(r.CanStop); Assert.False(r.CanStart); Assert.False(r.CanRestart); });
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_Parada_OfreceIniciar_SinCambiarLaConfiguracion()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        Assert.True(db.DbRunning);
        Assert.False(db.CanStartDb);   // en marcha: el botón es Parar

        main.Stack.Show(RunningWithDbStopped());
        Dispatcher.UIThread.RunJobs();

        Assert.False(db.DbRunning);
        Assert.True(db.CanStartDb);    // parada con el stack en marcha: el botón es Iniciar
        Assert.True(db.HasGlobal);     // y la base de datos global sigue siendo la misma (no es db use none)
        Assert.Contains("engine = \"mariadb\"", File.ReadAllText(fake.Paths.ConfigFile));
        Snapshot(window, "bases-de-datos-parada");

        // Con el stack parado del todo no se puede iniciar solo la base de datos: es Iniciar todo.
        main.Stack.Show(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(db.CanStartDb);
    }

    // Daemon-1: un cambio rechazado antes de parar nada (E1) se cuenta como tal en el diálogo.

    [AvaloniaFact]
    public async Task ArranqueFallido_ConElCambioRechazado_DiceQueSigueEnMarchaLoDeAntes()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        // Lo que devuelve StackController cuando el daemon rechaza el cambio con el stack en marcha.
        var failure = new StartFailure(
            "No se ha aplicado el cambio",
            "No se ha aplicado el cambio: El puerto 8445 (HTTPS de los sitios .test) ya esta en uso en esta maquina por powershell (PID 4242) (IIS, VMware, Skype u otro servidor?). "
            + "Paralo, o cambia [web] https_port en roost.toml.\nSigue en marcha lo de antes, con la configuracion anterior.",
            [new BusyPort(8445, "HTTPS de los sitios .test", new Roost.Core.Platform.PortOwner(8445, 4242, "powershell"), 9443, "[web] https_port")],
            WasRunning: true,
            StillRunning: true);
        var opening = main.ShowDialogAsync(new StartErrorDialogViewModel(main, failure));
        await UntilAsync(() => main.Dialog is StartErrorDialogViewModel);
        var dialog = Assert.IsType<StartErrorDialogViewModel>(main.Dialog);

        Assert.Contains("sigue en marcha, con la configuración de antes", dialog.Subtitle);
        Assert.StartsWith("No se ha aplicado el cambio: El puerto 8445", dialog.Message);
        Assert.Contains("Sigue en marcha lo de antes", dialog.LogTail);
        Assert.Single(dialog.Ports);
        Assert.Equal("Usar el puerto 9443", dialog.Ports[0].UseLabel);
        Snapshot(window, "arranque-fallido-cambio-rechazado");

        dialog.Close();
        await opening;
        await UntilAsync(() => main.Dialog is null);
        Assert.True(main.Stack.IsRunning);   // y la app lo sigue viendo en marcha
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
        Assert.Equal([phar, "install", "--no-progress"], spec.Arguments);
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

    // PHP (oleada 3): Xdebug, la conf.d de la rama, los ajustes rápidos, el php.ini, phpinfo y los módulos.

    /// <summary>Lleva el scroll de la pantalla PHP hasta el detalle de la versión elegida (Xdebug, ajustes, extensiones).</summary>
    private static void ScrollPhpTo(MainWindow window, double y)
    {
        Dispatcher.UIThread.RunJobs();
        var scroll = window.GetVisualDescendants().OfType<PhpView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Avalonia.Vector(0, y);
        Dispatcher.UIThread.RunJobs();
    }

    private const string PhpV84ConXdebug = "PHP 8.4.27 (cli) (built: Oct 20 2026 04:19:45) (NTS Visual C++ 2022 x64)\nZend Engine v4.4.27, Copyright (c) Zend Technologies\n    with Zend OPcache v8.4.27, Copyright (c), by Zend Technologies\n    with Xdebug v3.5.3, Copyright (c) 2002-2026, by Derick Rethans\n";

    private const string PhpV84SinXdebug = "PHP 8.4.27 (cli) (built: Oct 20 2026 04:19:45) (NTS Visual C++ 2022 x64)\nZend Engine v4.4.27, Copyright (c) Zend Technologies\n    with Zend OPcache v8.4.27, Copyright (c), by Zend Technologies\n";

    /// <summary>Una DLL de Xdebug «instalada» (de pega) para la 8.4: con ella, la app no la baja.</summary>
    private static string FakeXdebugDll(FakeRoost fake)
    {
        var build = Roost.Core.Install.XdebugCatalog.Windows.Single(b => b.PhpBranch == "8.4");
        var dll = new Roost.Core.Install.XdebugInstaller(fake.Paths).DllPath(build);
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllText(dll, "MZ de pega");
        return dll;
    }

    [AvaloniaFact]
    public async Task Php_DetalleConXdebugConfDDeLaRamaYAjustesRapidos()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        // Un php_xdebug.dll puesto a mano en ext/ no sale entre las extensiones: Xdebug tiene su interruptor.
        File.WriteAllText(Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", "ext", "php_xdebug.dll"), "");

        await main.NavigateToAsync(main.Php);
        var php = main.Php;
        Assert.Equal("8.4.27", php.Selected?.Version);
        Assert.DoesNotContain(php.Extensions, e => e.Name == "xdebug");
        Assert.True(php.XdebugAvailable);
        Assert.False(php.XdebugOn);
        Assert.StartsWith("Apagado. La primera vez se baja Xdebug 3.5.3 de xdebug.org", php.XdebugText);
        Assert.Equal(@"conf.d\8.4\99-usuario.ini", php.BranchIniText);
        Assert.Equal(["512M", "128M", "128M", "120"], new[] { php.MemoryLimit, php.UploadMaxFilesize, php.PostMaxSize, php.MaxExecutionTime });
        Assert.False(php.CanSaveSettings);
        ScrollPhpTo(window, 250);
        Snapshot(window, "php-detalle");

        // Una subida más grande que post_max_size: se dice al teclear y no se puede guardar.
        php.UploadMaxFilesize = "300M";
        Assert.Contains("post_max_size (128M) tiene que ser al menos upload_max_filesize (300M)", php.SettingsError);
        Assert.False(php.CanSaveSettings);
        Snapshot(window, "php-ajustes-error");
        php.MaxExecutionTime = "mucho";
        Assert.StartsWith("max_execution_time: pon segundos", php.SettingsError);

        php.PostMaxSize = "300m";
        php.MaxExecutionTime = "300";
        Assert.Null(php.SettingsError);
        Assert.True(php.CanSaveSettings);
        await php.SaveSettingsCommand.ExecuteAsync(null);   // parado: no hay pool que reiniciar
        var ini = File.ReadAllText(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", "php.ini"));
        Assert.Contains("upload_max_filesize = 300M", ini);
        Assert.Contains("post_max_size = 300M", ini);
        Assert.Contains("max_execution_time = 300", ini);
        Assert.Equal("300M", php.PostMaxSize);
        Assert.False(php.CanSaveSettings);

        // Otra versión enseña los suyos (de serie) y su conf.d; al volver, siguen los guardados.
        php.PickCommand.Execute(php.Installed.Single(c => c.Version == "7.4.33"));
        Assert.Equal(@"conf.d\7.4\99-usuario.ini", php.BranchIniText);
        Assert.Equal("128M", php.UploadMaxFilesize);
        Assert.Contains("3.1.6", php.XdebugText);
        php.PickCommand.Execute(php.Installed.Single(c => c.Version == "8.4.27"));
        Assert.Equal("300M", php.UploadMaxFilesize);

        php.DefaultSettingsCommand.Execute(null);
        Assert.Equal("128M", php.UploadMaxFilesize);
        Assert.True(php.CanSaveSettings);   // vuelve a lo de serie, pero hay que guardarlo
    }

    [AvaloniaFact]
    public async Task Php_XdebugConSuDll_SeApagaYSeEnciendeComprobandoConPhpV()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        var dll = FakeXdebugDll(fake);
        Roost.Core.Stack.PhpExtensionChoice.None.With("xdebug", enabled: true)
            .Save(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", Roost.Core.Stack.PhpExtensionChoice.FileName));
        var runner = new CannedCommandRunner().On("-v", PhpV84SinXdebug);

        await main.NavigateToAsync(main.Php);
        var php = main.Php;
        php.Runner = runner;
        Assert.True(php.XdebugOn);
        Assert.Equal("Xdebug 3.5.3 · depura la petición que trae XDEBUG_TRIGGER · el editor escucha en 127.0.0.1:9003", php.XdebugText);
        ScrollPhpTo(window, 250);
        Snapshot(window, "php-xdebug-encendido");

        await php.ToggleXdebugCommand.ExecuteAsync(null);
        Assert.False(php.XdebugOn);
        var ini = Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", "php.ini");
        Assert.DoesNotContain("[xdebug]", File.ReadAllText(ini));
        Assert.StartsWith("Apagado (Xdebug 3.5.3 ya está en bin\\xdebug)", php.XdebugText);

        runner.On("-v", PhpV84ConXdebug);
        await php.ToggleXdebugCommand.ExecuteAsync(null);
        Assert.True(php.XdebugOn);
        Assert.Contains($"zend_extension=\"{dll}\"", File.ReadAllText(ini));
        Assert.Equal("Xdebug 3.5.3 activado en PHP 8.4.27", main.FlashText);
        Assert.Equal(["-v"], runner.Calls[^1].Arguments);
        Assert.Contains(";", runner.Calls[^1].Environment!["PHP_INI_SCAN_DIR"]);   // la común y la de la rama
        Assert.Null(main.Dialog);
    }

    [AvaloniaFact]
    public async Task Php_XdebugLaPrimeraVez_LoBajaConElDialogoInstalando()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        var hold = new TaskCompletionSource();
        fake.Upstream.HoldMissing = hold;   // xdebug.org no está en la «red» de los tests: el 404 espera
        var runner = new CannedCommandRunner();

        await main.NavigateToAsync(main.Php);
        main.Php.Runner = runner;
        var toggling = main.Php.ToggleXdebugCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is InstallDialogViewModel);
        var dialog = (InstallDialogViewModel)main.Dialog!;
        Assert.Equal("Instalando Xdebug 3.5.3", dialog.Title);
        Assert.Equal("Para PHP 8.4 · NTS · x64 · vs17 · desde xdebug.org", dialog.Subtitle);
        Assert.Equal(331_776, dialog.TotalBytes);
        Assert.Equal("Verificar el SHA-256", dialog.Steps[2].Label);
        Snapshot(window, "php-xdebug-instalando");

        hold.SetResult();
        await UntilAsync(() => dialog.Done);
        Assert.True(dialog.Failed);
        dialog.CancelCommand.Execute(null);   // «Cerrar»
        await toggling;
        Assert.False(main.Php.XdebugOn);       // el interruptor vuelve: no hay DLL
        Assert.Empty(runner.Calls);            // y php.exe ni se ha tocado
        var choice = Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", "extensiones.json");
        Assert.True(!File.Exists(choice) || !File.ReadAllText(choice).Contains("xdebug"));
    }

    [AvaloniaFact]
    public async Task Php_VerPhpIniGenerado_DeSoloLectura_ConBusqueda()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;

        await main.NavigateToAsync(main.Php);
        var showing = main.Php.ShowIniCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is PhpTextDialogViewModel { Loading: false });
        var dialog = (PhpTextDialogViewModel)main.Dialog!;
        Assert.Equal("php.ini de PHP 8.4.27", dialog.Title);
        Assert.Equal(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", "php.ini"), dialog.Path);
        Assert.True(dialog.CanOpenFolder);
        Assert.Contains(dialog.Lines, l => l.Text == "[opcache]");
        Assert.Contains(dialog.Lines, l => l.IsMuted);   // los comentarios, apagados
        dialog.Query = "memory";
        Assert.Equal(["memory_limit = 512M"], dialog.Lines.Select(l => l.Text));
        Assert.EndsWith("líneas con «memory»", dialog.CountText);
        dialog.Query = "";
        Snapshot(window, "php-ver-php-ini");

        dialog.FinishCommand.Execute(null);
        await showing;
        Assert.Null(main.Dialog);
    }

    [AvaloniaFact]
    public async Task Php_PhpinfoYModulos_ConElPhpIniDeRoost_YBusqueda()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        var runner = new CannedCommandRunner()
            .On("-i", "phpinfo()\nPHP Version => 8.4.27\n\nLoaded Configuration File => C:\\roost\\etc\\php\\8.4.27-nts-x64\\php.ini\n"
                      + "Scan this dir for additional .ini files => C:\\roost\\etc\\php\\conf.d;C:\\roost\\etc\\php\\conf.d\\8.4\n\nxdebug\n\nVersion => 3.5.3\nxdebug.mode => debug,develop => debug,develop\nxdebug.client_port => 9003 => 9003\nmemory_limit => 512M => 512M\n",
                "PHP Warning:  PHP Startup: Unable to load dynamic library 'oci8_19' (tried: C:\\php\\ext\\php_oci8_19.dll (No se puede encontrar el módulo especificado)) in Unknown on line 0\n")
            .On("-m", "[PHP Modules]\nCore\ncurl\nmbstring\nxdebug\nZend OPcache\n\n[Zend Modules]\nXdebug\nZend OPcache\n");

        await main.NavigateToAsync(main.Php);
        main.Php.Runner = runner;
        var info = main.Php.PhpInfoCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is PhpTextDialogViewModel { Loading: false });
        var dialog = (PhpTextDialogViewModel)main.Dialog!;
        Assert.Equal("phpinfo de PHP 8.4.27", dialog.Title);
        Assert.False(dialog.HasPath);
        Assert.True(dialog.Lines[0].IsWarn);   // el aviso de arranque, arriba y en ámbar
        Assert.StartsWith("Aviso al arrancar: Unable to load dynamic library 'oci8_19'", dialog.Lines[0].Text);
        dialog.Query = "XDEBUG.";
        Assert.Equal(["xdebug.mode => debug,develop => debug,develop", "xdebug.client_port => 9003 => 9003"], dialog.Lines.Select(l => l.Text));
        var spec = runner.Calls.Single();
        Assert.Equal(Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", "php.exe"), spec.Exe);
        Assert.Equal(["-i"], spec.Arguments);
        Assert.Equal(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64"), spec.Environment!["PHPRC"]);
        Assert.Equal(Path.Combine(fake.Paths.Etc, "php", "conf.d") + ";" + Path.Combine(fake.Paths.Etc, "php", "conf.d", "8.4"), spec.Environment["PHP_INI_SCAN_DIR"]);
        Snapshot(window, "php-phpinfo");
        dialog.FinishCommand.Execute(null);
        await info;

        var modules = main.Php.ModulesCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is PhpTextDialogViewModel { Loading: false });
        dialog = (PhpTextDialogViewModel)main.Dialog!;
        Assert.Equal("Módulos de PHP 8.4.27", dialog.Title);
        Assert.Contains(dialog.Lines, l => l.Text == "Zend OPcache");
        Snapshot(window, "php-modulos");
        dialog.FinishCommand.Execute(null);
        await modules;
    }

    [AvaloniaFact]
    public async Task Php_UnaExtensionQuePhpNoCarga_SeQuedaApagada_YDiceElPorQue()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        var runner = new CannedCommandRunner().On("-v", PhpV84SinXdebug,
            "PHP Warning:  PHP Startup: Unable to load dynamic library 'ldap' (tried: C:\\php\\ext\\php_ldap.dll (No se puede encontrar el módulo especificado)) in Unknown on line 0\n");

        await main.NavigateToAsync(main.Php);
        var php = main.Php;
        php.Runner = runner;
        var ldap = php.Extensions.Single(e => e.Name == "ldap");
        var toggling = php.ToggleExtensionCommand.ExecuteAsync(ldap);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var error = (MessageDialogViewModel)main.Dialog!;
        Assert.Equal("No se pudo cambiar ldap", error.Title);
        Assert.Contains("no puede cargar ldap; se deja como estaba", error.Text ?? error.Detail);
        error.Close();
        await toggling;
        Assert.False(ldap.IsOn);
        Assert.DoesNotContain("extension=ldap", File.ReadAllText(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", "php.ini")));

        // La que sí carga se activa.
        runner.On("-v", PhpV84SinXdebug);
        var soap = php.Extensions.Single(e => e.Name == "soap");
        await php.ToggleExtensionCommand.ExecuteAsync(soap);
        Assert.True(soap.IsOn);
        Assert.Contains("extension=soap", File.ReadAllText(Path.Combine(fake.Paths.Etc, "php", "8.4.27-nts-x64", "php.ini")));
    }

    // Servicios (oleada 3): Redis, Correo, Adminer y los servicios de Windows

    /// <summary>Un Redis de mentira en loopback que contesta +PONG a cada PING (para la línea del PING).</summary>
    private static (int Port, System.Net.Sockets.TcpListener Listener) FakeRedis()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    // El PING de RedisProbe cabe en una lectura (14 bytes); lo que lea da igual.
                    _ = await stream.ReadAtLeastAsync(new byte[64], 1, throwOnEndOfStream: false);
                    await stream.WriteAsync("+PONG\r\n"u8.ToArray());
                }
            }
            catch (Exception) { /* parado al acabar el test */ }
        });
        return (((System.Net.IPEndPoint)listener.LocalEndpoint).Port, listener);
    }

    [AvaloniaFact]
    public async Task Inicio_ConRedisYMailpit_SonDeRoost_YVanTrasLaBaseDeDatos()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        fake.InstallFakeServices(redisPort: 6380, smtpPort: 1026, webPort: 8026);

        main.Stack.Show(FakeRoost.RunningWithServices(6380, 1026, 8026));
        main.NotifyConfigChanged();
        Dispatcher.UIThread.RunJobs();

        var rows = main.Home.Services;
        Assert.Equal(["MariaDB", "Redis", "Mailpit", "PHP 8.4", "PHP 7.4", "nginx"], rows.Select(r => r.Name));
        var redis = rows.Single(r => r.Name == "Redis");
        Assert.Null(redis.Tag);   // ya no sale como «extensión»
        Assert.Equal("8.10.2", redis.Version);
        Assert.Equal("127.0.0.1:6380", redis.Listen);
        Assert.Equal("redis", redis.RestartKey);
        Assert.True(redis.CanStop);
        var mail = rows.Single(r => r.Name == "Mailpit");
        Assert.Equal(":1026 · :8026", mail.Listen);
        Assert.Contains("http://localhost:8026", mail.Detail);
        Snapshot(window, "inicio-con-redis-y-mailpit");

        // Parado: siguen en la lista, en su sitio, sin botones.
        main.Stack.Show(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["MariaDB", "Redis", "Mailpit"], main.Home.Services.Take(3).Select(r => r.Name));
        Assert.All(main.Home.Services.Take(3), r => Assert.False(r.CanRestart));
    }

    [AvaloniaFact]
    public async Task Redis_SinInstalar_OfreceInstalarlo_YEstaEnComponentes()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        Assert.Equal(["PHP", "Servidor web", "Bases de datos", "Redis", "Correo"], main.ComponentNav.Select(n => n.Label));
        await main.NavigateToAsync(main.Redis);
        var redis = main.Redis;
        Assert.False(redis.Installed);
        Assert.True(redis.NotInstalled);
        Assert.False(redis.InStack);
        Assert.Equal("sin instalar", redis.StateText);
        Assert.Equal("6379", redis.PortText);
        Assert.Equal("8.10.2", redis.CatalogVersion);
        Assert.Contains("REDIS_CLIENT=predis", redis.EnvText);
        Assert.False(redis.OpenCliCommand.CanExecute(null) && redis.Running);
        Snapshot(window, "redis-sin-instalar");
    }

    [AvaloniaFact]
    public async Task Redis_EnMarcha_ConElPing_ElPuertoYLosOtrosRedisDelEquipo()
    {
        var (port, listener) = FakeRedis();
        try
        {
            var (main, window, fake) = await OpenAsync(running: true);
            using var _ = fake;
            fake.InstallFakeServices(redisPort: port);
            main.MachineServices.Show(
            [
                new Roost.Core.Platform.WindowsService("Memurai", "Memurai", "Redis", @"C:\Program Files\Memurai\memurai.exe", Roost.Core.Platform.ServiceStartMode.Automatic, Running: false),
            ]);
            main.Stack.Show(FakeRoost.RunningWithServices(redisPort: port));
            main.NotifyConfigChanged();

            await main.NavigateToAsync(main.Redis);
            var redis = main.Redis;
            Assert.True(redis.Installed);
            Assert.True(redis.InStack);
            Assert.True(redis.Running);
            Assert.Equal("en marcha", redis.StateText);
            Assert.Equal("Contesta al PING (PONG)", redis.PingText);
            Assert.Equal("en uso por Roost", redis.PortChip);
            Assert.False(redis.CanApplyPort);
            Assert.Contains($"REDIS_PORT={port}", redis.EnvText);
            Assert.Single(redis.Others);
            Assert.Contains("Memurai", redis.Others[0]);
            Assert.Contains("Roost no lo usa ni lo toca", redis.Others[0]);

            // Un puerto que choca con otro de Roost (el de la base de datos) no se puede aplicar.
            redis.PortText = "3308";
            Assert.Equal("es de otro servicio de Roost", redis.PortChip);
            Assert.False(redis.CanApplyPort);
            redis.PortText = "abc";
            Assert.Equal("no válido", redis.PortChip);
            redis.PortText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Snapshot(window, "redis");
        }
        finally
        {
            listener.Stop();
        }
    }

    [AvaloniaFact]
    public async Task Correo_EnMarcha_ConLaBandejaYElMailDePhp()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        fake.InstallFakeServices(smtpPort: 1026, webPort: 8026);
        Roost.Core.Stack.MailConf.SyncPhpIni(Path.Combine(fake.Paths.Etc, "php", "conf.d"), Roost.Core.Config.RoostConfig.Load(fake.Paths.ConfigFile));
        main.Stack.Show(FakeRoost.RunningWithServices(smtpPort: 1026, webPort: 8026));
        main.NotifyConfigChanged();

        await main.NavigateToAsync(main.Mail);
        var mail = main.Mail;
        Assert.Equal("Correo", mail.Title);
        Assert.True(mail.Installed);
        Assert.True(mail.InStack);
        Assert.True(mail.Running);
        Assert.Equal("http://localhost:8026/", mail.InboxUrl);
        Assert.True(mail.OpenInboxCommand.CanExecute(null));
        Assert.Equal("en uso por Roost", mail.SmtpChip);
        Assert.StartsWith("mail() de PHP va a Mailpit (SMTP 127.0.0.1:1026)", mail.PhpText);
        Assert.Contains("MAIL_PORT=1026", mail.EnvText);

        mail.WebText = "1026";
        Assert.Equal("igual que el SMTP", mail.WebChip);
        Assert.False(mail.CanApplyPorts);
        mail.WebText = "8026";
        Snapshot(window, "correo");
    }

    [AvaloniaFact]
    public async Task Correo_SinInstalar_OfreceInstalarlo()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;

        await main.NavigateToAsync(main.Mail);
        Assert.False(main.Mail.Installed);
        Assert.Equal("sin instalar", main.Mail.StateText);
        Assert.Equal("1025", main.Mail.SmtpText);
        Assert.Equal("8025", main.Mail.WebText);
        Assert.StartsWith("Con Mailpit fuera del stack", main.Mail.PhpText);
        Snapshot(window, "correo-sin-instalar");
    }

    [AvaloniaFact]
    public async Task BasesDeDatos_AbrirAdminer_LaPrimeraVezLoInstala_YSiFallaNoAbreNada()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Databases);
        var db = main.Databases;
        Assert.StartsWith("Instala Adminer 6.2.0", db.AdminerTip);
        Snapshot(window, "bases-de-datos-adminer");

        // FakeUpstream no sirve adminer.php (404): el diálogo de instalación lo dice y no se abre el navegador.
        var opening = db.OpenAdminerCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<InstallDialogViewModel>(main.Dialog);
        Assert.Equal("Instalando Adminer 6.2.0", dialog.Title);
        Assert.False(await dialog.Finished);
        Assert.Contains("No se pudo descargar", dialog.Error);
        dialog.CancelCommand.Execute(null);
        await opening;
        Assert.Null(new Roost.Core.Install.AdminerInstaller(fake.Paths).Find(Roost.Core.Install.AdminerCatalog.Current));
    }

    [AvaloniaFact]
    public async Task Auditoria_ServiciosDeWindows_LaMariaDbQueArrancaConWindows_YComoDejarlaEnManual()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        ShowFindings(main);
        Roost.Core.Config.RoostConfig.SetDbPort(fake.Paths.ConfigFile, 3306);   // el caso del usuario: la de Roost también en el 3306
        main.MachineServices.Show(
        [
            new Roost.Core.Platform.WindowsService("MariaDB", "MariaDB 10.6", "MariaDB", @"C:\Program Files\MariaDB 10.6\bin\mysqld.exe", Roost.Core.Platform.ServiceStartMode.Automatic, Running: true),
            new Roost.Core.Platform.WindowsService("Apache2.4", "Apache2.4", "Apache", @"C:\Apache24\bin\httpd.exe", Roost.Core.Platform.ServiceStartMode.Manual, Running: false),
        ]);

        await main.NavigateToAsync(main.AuditPage);
        var audit = main.AuditPage;
        Assert.Equal("1 servicio puede quitarle un puerto a Roost", audit.ServicesSummary);
        Assert.Equal(Roost.Desktop.Controls.Tone.Bad, audit.ServicesTone);
        var maria = audit.ServiceRows[0];
        Assert.Equal("MariaDB 10.6 (MariaDB)", maria.Title);
        Assert.Equal("Arranca con Windows y está en marcha", maria.State);
        Assert.Contains("el de la base de datos de Roost", maria.Detail);
        Assert.Equal("sc config \"MariaDB\" start= demand", maria.Command);
        var apache = audit.ServiceRows[1];
        Assert.Equal(Roost.Desktop.Controls.Tone.Neutral, apache.Tone);   // manual y parado: no molesta
        Assert.Equal("Manual y parado", apache.State);
        // La sección va al final de la pantalla: se baja hasta ella para la captura.
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<AuditView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        Snapshot(window, "auditoria-servicios-de-windows");
    }

    [AvaloniaFact]
    public async Task Auditoria_SinServiciosDeWindows_LoDice()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        main.MachineServices.Show([]);

        await main.NavigateToAsync(main.AuditPage);

        Assert.Empty(main.AuditPage.ServiceRows);
        Assert.Equal("Ninguno: nada le quita los puertos a Roost", main.AuditPage.ServicesSummary);
    }

    [AvaloniaFact]
    public async Task Bienvenida_AvisaDeLosServiciosDeWindowsQueSeQuedanLosPuertos()
    {
        var (main, window, fake) = await OpenAsync(running: false);
        using var _ = fake;
        ShowFindings(main);
        Roost.Core.Config.RoostConfig.SetDbPort(fake.Paths.ConfigFile, 3306);
        main.MachineServices.Show(
        [
            new Roost.Core.Platform.WindowsService("MariaDB", "MariaDB 10.6", "MariaDB", @"C:\Program Files\MariaDB 10.6\bin\mysqld.exe", Roost.Core.Platform.ServiceStartMode.Automatic, Running: false),
            new Roost.Core.Platform.WindowsService("W3SVC", "World Wide Web Publishing Service", "IIS", @"C:\Windows\system32\svchost.exe", Roost.Core.Platform.ServiceStartMode.Automatic, Running: true),
        ]);

        var showing = main.ShowWelcomeAsync(force: true);
        var welcome = Assert.IsType<WelcomeDialogViewModel>(main.Dialog);
        await welcome.LoadAsync();

        Assert.True(welcome.HasServiceWarnings);
        Assert.Equal(2, welcome.ServiceWarnings.Count);
        Assert.Contains(welcome.ServiceWarnings, w => w.StartsWith("MariaDB 10.6 (MariaDB): arranca con Windows (ahora está parado)", StringComparison.Ordinal) && w.Contains("el de la base de datos"));
        Assert.Contains(welcome.ServiceWarnings, w => w.StartsWith("World Wide Web Publishing Service (W3SVC)", StringComparison.Ordinal) && w.Contains("el de la web"));
        Snapshot(window, "bienvenida-servicios-de-windows");

        welcome.SkipCommand.Execute(null);
        await showing;
    }

    // ---- Pasos y plantillas (oleada 4) ----
    // Los pasos tras clonar, «Pasos de instalación…» del panel y Nuevo sitio con una plantilla. Composer
    // y artisan van sobre un runner guionizado y la base de datos sobre un cliente falso: nada se
    // ejecuta, nada va a la red y ningún test llega a aplicar el stack.

    [AvaloniaFact]
    public async Task NuevoSitio_DesdeGit_AbreElAsistente()
    {
        var (main, _, fake) = await OpenAsync(running: true);
        using var _f = fake;

        await main.NavigateToAsync(main.Sites);
        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        dialog.PickTemplateCommand.Execute(dialog.Templates[4]);

        await UntilAsync(() => main.Dialog is CloneWizardViewModel);
        ((CloneWizardViewModel)main.Dialog!).Close();
        await opening;
        Assert.Null(main.Dialog);
    }

    [AvaloniaFact]
    public async Task NuevoSitio_PlantillaLaravel_SoloLosPhpQueValen_YUnaCarpetaNueva()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;

        await main.NavigateToAsync(main.Sites);
        var opening = main.Sites.NewSiteCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        Assert.Equal(["Carpeta (lo de siempre)", "Laravel", "WordPress", "Symfony", "Desde git…"], dialog.Templates.Select(t => t.Label));
        Assert.True(dialog.Templates[0].IsSelected);
        Assert.True(dialog.Templates[4].Available);   // «Desde git…» abre el asistente de clonar (NuevoSitio_DesdeGit_AbreElAsistente)

        dialog.Name = "shop";
        dialog.PickTemplateCommand.Execute(dialog.Templates[1]);
        Assert.True(dialog.IsProjectTemplate);
        Assert.True(dialog.Templates[1].IsSelected);
        Assert.False(dialog.Templates[0].IsSelected);
        // Laravel 13 pide PHP 8.3: la 7.4 no sale.
        Assert.Equal(["Global (ahora 8.4.27)", "8.4.27", "8.3.36"], dialog.PhpOptions.Select(o => o.Label));
        Assert.Equal("Laravel 13 pide PHP 8.3 o más nuevo: solo salen los que valen.", dialog.PhpHint);
        Assert.Equal("public", dialog.Root);
        Assert.False(dialog.RootEditable);
        Assert.Equal("No existe: Roost crea aquí el proyecto Laravel.", dialog.FolderHint);
        Assert.True(dialog.DbIsCreate);
        Assert.Equal("shop", dialog.DbName);
        Assert.Contains("composer create-project laravel/laravel", dialog.StepFolder);
        Assert.Equal("Deja el .env con la base de datos shop y https://shop.test, y hace migrate y storage:link, con la salida en vivo", dialog.TemplateStep);
        Assert.True(dialog.CanCreate);
        Snapshot(window, "nuevo-sitio-plantilla-laravel");

        // Una carpeta con algo dentro no vale para una plantilla; un sitio que ya existe, tampoco.
        dialog.Folder = Path.Combine(fake.Paths.Www, "tienda");
        Assert.False(dialog.FolderValid);
        Assert.False(dialog.CanCreate);
        Assert.StartsWith("Ya existe y no está vacía", dialog.FolderHint);
        dialog.Folder = Path.Combine(fake.Paths.Www, "shop");
        Assert.True(dialog.FolderValid);
        dialog.Name = "blog";
        Assert.False(dialog.NameValid);
        Assert.StartsWith("Ya existe blog.test: una plantilla crea un sitio nuevo", dialog.NameHint);
        dialog.Name = "shop";

        // WordPress: vale con la 7.4, se sirve desde su carpeta y sin base de datos no.
        dialog.PickTemplateCommand.Execute(dialog.Templates[2]);
        Assert.Contains(dialog.PhpOptions, o => o.Label.StartsWith("7.4.33", StringComparison.Ordinal));
        Assert.Equal("", dialog.Root);
        dialog.DbIsNone = true;
        Assert.False(dialog.DbValid);
        Assert.StartsWith("WordPress necesita una base de datos", dialog.DbHint);
        dialog.DbIsCreate = true;
        Assert.True(dialog.CanCreate);

        // Symfony: sin base de datos, salvo con webapp.
        dialog.PickTemplateCommand.Execute(dialog.Templates[3]);
        Assert.True(dialog.IsSymfony);
        Assert.True(dialog.DbIsNone);
        dialog.Webapp = true;
        Assert.True(dialog.DbIsCreate);
        Assert.Contains("composer require webapp", dialog.StepFolder);
        Assert.Contains("DATABASE_URL a shop", dialog.TemplateStep);
        Snapshot(window, "nuevo-sitio-plantilla-symfony");

        // De vuelta a la carpeta de siempre, todo como antes.
        dialog.PickTemplateCommand.Execute(dialog.Templates[0]);
        Assert.True(dialog.RootEditable);
        Assert.True(dialog.FolderValid);
        Assert.Equal("", dialog.TemplateStep);
        Assert.Equal("", dialog.PhpHint);

        dialog.Close();   // sin crear: crear aplicaría al stack
        await opening;
    }

    [AvaloniaFact]
    public async Task NuevoSitio_PlantillaLaravel_LosPasosEmpiezanSolos_YSiFallaNoQuedaNada()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        fake.InstallFakeComposer();
        var runner = new ScriptedCommandRunner(
            "Creating a \"laravel/laravel\" project at \"./.tmp-1f2e3d\"",
            "Installing laravel/laravel (v13.11.0)",
            "  - Downloading laravel/laravel (v13.11.0)",
            "  - Installing laravel/laravel (v13.11.0): Extracting archive",
            "Created project in C:\\roost\\www\\.tmp-1f2e3d",
            "Loading composer repositories with package information",
            "Updating dependencies",
            "Lock file operations: 111 installs, 0 updates, 0 removals");
        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.DbRunner = new FakeDbClient(exists: false);

        var creating = sites.NewSiteCommand.ExecuteAsync(null);
        var form = Assert.IsType<NewSiteDialogViewModel>(main.Dialog);
        form.Name = "shop";
        form.PickTemplateCommand.Execute(form.Templates[1]);
        form.CreateCommand.Execute(null);
        await UntilAsync(() => main.Dialog is DeployStepsDialogViewModel && runner.Calls.Count == 1);
        var dialog = (DeployStepsDialogViewModel)main.Dialog!;
        dialog.FlushLines();

        Assert.True(dialog.IsTemplate);
        Assert.Equal("Creando shop.test", dialog.Title);
        Assert.Equal($"Laravel con PHP 8.4.27 · en {Path.Combine(fake.Paths.Www, "shop")}", dialog.Subtitle);
        Assert.True(dialog.Running);
        Assert.False(dialog.CanDismiss);
        Assert.False(dialog.ShowOffer);   // empieza solo
        Assert.Equal(["create", "db", "env", "key", "migrate", "storage-link", "npm"], dialog.Rows.Select(r => r.Step.Id));
        Assert.False(dialog.Rows[0].Editable);   // crear el proyecto no se desmarca
        Assert.True(dialog.Rows[0].IsActive);
        var spec = runner.Calls[0];
        Assert.Equal(Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", "php.exe"), spec.Exe);
        Assert.Equal(["create-project", "laravel/laravel"], spec.Arguments.Skip(1).Take(2));
        Assert.StartsWith(Path.Combine(fake.Paths.Www, ".tmp-"), spec.Arguments[3]);
        Assert.Equal(fake.Paths.Www, spec.WorkingDirectory);
        Snapshot(window, "plantilla-laravel-en-marcha");

        runner.Report("");
        runner.Report("  Problem 1");
        runner.Report("    - Root composer.json requires laravel/framework ^13.0 -> satisfiable by laravel/framework[v13.0.0, ..., v13.38.0].");
        runner.Report("  Your requirements could not be resolved to an installable set of packages.");
        runner.Finish(2);
        await UntilAsync(() => dialog.Stopped);

        Assert.True(dialog.ShowRetry);
        Assert.True(dialog.ShowUndo);
        Assert.False(dialog.ShowClose);
        Assert.StartsWith("composer create-project laravel/laravel ha fallado (ver las últimas líneas).", dialog.ResultText);
        Assert.Contains("la carpeta nueva, el sitio y su base de datos", dialog.ResultText);
        Assert.Contains("could not be resolved", dialog.FailureTail);
        Assert.Equal(DeployRowState.Failed, dialog.Rows[0].State);
        Assert.Equal("ha fallado (código 2)", dialog.Rows[0].StateText);
        Assert.Equal(DeployRowState.Pending, dialog.Rows[1].State);
        Snapshot(window, "plantilla-laravel-fallo");

        // «Deshacer y cerrar»: no queda ni la carpeta, ni el temporal, ni el sitio.
        dialog.UndoCommand.Execute(null);
        await creating;
        Assert.Null(main.Dialog);
        Assert.False(Directory.Exists(Path.Combine(fake.Paths.Www, "shop")));
        Assert.Empty(Directory.GetDirectories(fake.Paths.Www, ".tmp-*"));
        Assert.Null(new Roost.Core.Sites.SiteRegistry(fake.Paths).Find("shop"));
    }

    [AvaloniaFact]
    public async Task PasosTrasClonar_MarcaLoQueHaceFalta_YLosHaceConLaSalidaEnVivo()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        fake.InstallFakeComposer();
        var folder = fake.Project("shop", "artisan", "composer.json", "composer.lock", ".env.example", "public/", "package.json");
        var site = new Roost.Core.Sites.SiteManager(fake.Paths).Add("shop", folder).Site;
        var runner = new ScriptedCommandRunner(
            "Installing dependencies from lock file (including require-dev)",
            "Verifying lock file contents can be installed on current platform.",
            "Package operations: 112 installs, 0 updates, 0 removals",
            "  - Installing doctrine/inflector (2.0.10): Extracting archive",
            "  - Installing laravel/framework (v13.38.0): Extracting archive");
        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.DbRunner = new FakeDbClient(exists: true, tables: 0);

        var offering = sites.OfferDeployStepsAsync(site);
        await UntilAsync(() => main.Dialog is DeployStepsDialogViewModel);
        var dialog = (DeployStepsDialogViewModel)main.Dialog!;

        Assert.Equal("shop.test está clonado", dialog.Title);
        Assert.Equal("Parece un proyecto Laravel 11 (composer.json, artisan)", dialog.Subtitle);
        Assert.Equal("PHP: 8.4.27 (el global) · composer.json pide ^8.2 ✓", dialog.PhpLine);
        Assert.Equal(["composer", "env", "key", "migrate", "storage-link", "npm"], dialog.Rows.Select(r => r.Step.Id));
        Assert.All(dialog.Rows.Take(5), r => Assert.True(r.IsChecked));
        Assert.False(dialog.Rows[^1].IsChecked);
        Assert.False(dialog.Rows[^1].Editable);   // npm: llega con Node en Roost
        Assert.Equal("Llega con Node en Roost.", dialog.Rows[^1].Hint);
        Assert.Equal("Instala las dependencias (hay composer.lock).", dialog.Rows[0].Hint);
        Assert.Equal("Con la base de datos de Roost: 127.0.0.1:3308, root sin contraseña, shop; APP_URL=https://shop.test.", dialog.Rows[1].Hint);
        Assert.Equal("La base de datos shop está vacía.", dialog.Rows[3].Hint);
        Assert.Equal("composer install y php artisan ejecutan scripts del propio proyecto. Hazlo solo con repositorios de confianza.", dialog.Note);
        Assert.True(dialog.ShowOffer);
        Assert.True(dialog.CanRun);
        Snapshot(window, "pasos-tras-clonar");

        dialog.RunCommand.Execute(null);
        await UntilAsync(() => runner.Calls.Count == 1);
        dialog.FlushLines();
        Assert.True(dialog.Running);
        Assert.False(dialog.CanDismiss);
        Assert.True(dialog.Rows[0].IsActive);
        Assert.False(dialog.Rows[1].Editable);   // corriendo, no se tocan las casillas
        Assert.Equal("> composer install", dialog.Lines[0].Text);
        Assert.True(dialog.Lines[0].IsMuted);
        Assert.Equal(Path.Combine(fake.Paths.PhpBin, "8.4.27-nts-x64", "php.exe"), runner.Calls[0].Exe);
        Assert.Equal(folder, runner.Calls[0].WorkingDirectory);
        Snapshot(window, "pasos-tras-clonar-en-marcha");

        runner.Report("Generating optimized autoload files");
        runner.Finish(0);
        await UntilAsync(() => dialog.Succeeded);

        Assert.All(dialog.Rows.Take(5), r => Assert.Equal(DeployRowState.Done, r.State));
        Assert.StartsWith("Listo: 5 pasos en ", dialog.ResultText);
        Assert.Equal(4, runner.Calls.Count);   // composer, key:generate, migrate y storage:link (el .env lo escribe Roost)
        Assert.Equal(["artisan", "key:generate", "--no-interaction"], runner.Calls[1].Arguments);
        var env = Roost.Core.Deploy.EnvFile.Load(Path.Combine(folder, ".env")).Env;
        Assert.Equal("shop", env.Get("DB_DATABASE"));
        Assert.Equal("3308", env.Get("DB_PORT"));
        Assert.Equal("https://shop.test", env.Get("APP_URL"));
        Assert.True(dialog.ShowClose);
        Snapshot(window, "pasos-tras-clonar-hecho");

        dialog.FinishCommand.Execute(null);
        await offering;
        Assert.Null(main.Dialog);
    }

    [AvaloniaFact]
    public async Task PasosDeInstalacion_DesdeElPanel_UnPasoQueFalla_YReintentarDesdeAhi()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        fake.InstallFakeComposer();
        var folder = fake.Project("shop", "artisan", "composer.json", "composer.lock", ".env.example", "public/");
        new Roost.Core.Sites.SiteManager(fake.Paths).Add("shop", folder);
        var runner = new SequenceCommandRunner(
            (2,
            [
                "Installing dependencies from lock file (including require-dev)",
                "Verifying lock file contents can be installed on current platform.",
                "Your lock file does not contain a compatible set of packages. Please run composer update.",
                "",
                "  Problem 1",
                "    - laravel/framework is locked to version v13.38.0 and an update of this package was not requested.",
                "    - laravel/framework v13.38.0 requires ext-intl * -> it is missing from your system. Install or enable PHP's intl extension.",
            ]),
            (0, ["Installing dependencies from lock file (including require-dev)", "Generating optimized autoload files"]));
        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.DbRunner = new FakeDbClient(exists: true, tables: 7);
        main.NotifyConfigChanged();
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "shop.test"));

        Assert.True(sites.HasDeploySteps);
        Assert.Equal("Laravel 11", sites.DeployProjectText);
        Assert.StartsWith("composer install, el .env con la base de datos de Roost", sites.DeployStepsText);
        ScrollPage(window, sites, 560);
        Snapshot(window, "sitios-panel-pasos-de-instalacion");

        var showing = sites.DeployStepsCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is DeployStepsDialogViewModel);
        var dialog = (DeployStepsDialogViewModel)main.Dialog!;
        Assert.Equal("Pasos de instalación · shop.test", dialog.Title);
        var migrate = dialog.Rows.Single(r => r.Step.Id == "migrate");
        Assert.False(migrate.IsChecked);   // con tablas, apagado
        Assert.True(migrate.Editable);
        Assert.Equal("shop ya tiene 7 tablas: márcalo solo si de verdad quieres migrar.", migrate.Hint);
        dialog.Rows.Single(r => r.Step.Id == "storage-link").IsChecked = false;

        dialog.RunCommand.Execute(null);
        await UntilAsync(() => dialog.Stopped);
        Assert.True(dialog.ShowRetry);
        Assert.True(dialog.ShowClose);
        Assert.False(dialog.ShowUndo);
        Assert.Equal("composer install ha fallado en shop (ver las últimas líneas). El sitio sigue registrado: arréglalo y repite el paso con «Reintentar desde aquí».", dialog.ResultText);
        Assert.Contains("ext-intl", dialog.FailureTail);
        Assert.Equal("ha fallado (código 2)", dialog.Rows[0].StateText);
        Assert.False(File.Exists(Path.Combine(folder, ".env")));   // se para en el primero que falla
        Snapshot(window, "pasos-de-instalacion-fallo");

        dialog.RetryCommand.Execute(null);
        await UntilAsync(() => dialog.Succeeded);
        Assert.True(File.Exists(Path.Combine(folder, ".env")));
        Assert.Equal(DeployRowState.Pending, dialog.Rows.Single(r => r.Step.Id == "storage-link").State);
        Assert.Equal(DeployRowState.Pending, migrate.State);
        Assert.Equal(3, runner.Calls.Count);   // composer (falla), composer otra vez y key:generate
        Assert.Equal("", dialog.FailureTail);

        // Cerrado con la X (o Escape) también vale: el panel vuelve a mirar el proyecto.
        dialog.DismissCommand.Execute(null);
        await showing;
        Assert.Null(main.Dialog);
        Assert.True(sites.Project!.HasEnv);
    }

    [AvaloniaFact]
    public async Task PasosTrasClonar_UnaCarpetaSinProyecto_NiAbreElDialogo()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var folder = fake.Project("estatica", "index.php");
        var site = new Roost.Core.Sites.SiteManager(fake.Paths).Add("estatica", folder).Site;

        await main.Sites.OfferDeployStepsAsync(site);   // vuelve en el acto: nada que proponer

        Assert.Null(main.Dialog);
        Assert.Equal("estatica.test: no hay pasos de instalación que hacer", main.FlashText);
    }

    [AvaloniaFact]
    public async Task PasosDeInstalacion_SinPasos_ElDialogoLoDice()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var target = new Roost.Core.Deploy.DeployTarget
        {
            SiteName = "estatica",
            Folder = fake.Paths.Www,
            Url = "https://estatica.test",
            Php = new Roost.Core.Install.InstalledPhp("8.4.27-nts-x64", fake.Paths.PhpBin, "php.exe"),
        };
        var dialog = new DeployStepsDialogViewModel(DeployDialogMode.Panel, new Roost.Core.Sites.ProjectKind(Roost.Core.Sites.ProjectType.Plain, null, false, false, false),
            target, [], (_, _, _) => Task.FromResult(Roost.Core.Deploy.DeployOutcome.Done()));

        Assert.True(dialog.NoSteps);
        Assert.Equal("No hay pasos que proponer. Se sirve tal cual.", dialog.ResultText);
        Assert.True(dialog.ShowClose);
        Assert.False(dialog.ShowOffer);
        Assert.False(dialog.CanRun);
        var showing = main.ShowDialogAsync(dialog);
        dialog.FinishCommand.Execute(null);
        Assert.IsType<DeployDialogResult>(await showing);
    }
}
