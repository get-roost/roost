using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Roost.Core.Sites;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;

namespace Roost.Desktop.Tests;

/// <summary>
/// Sitios avanzados (oleada 6): «Editar…» (renombrar y mover), «Alias…», las carpetas aparcadas con
/// su vigilante, el <c>.roost.toml</c> al abrir la app y el filtro por sitio de Registros. Todo sobre
/// <see cref="FakeRoost"/> y su hosts de pruebas: nada pide permiso de administrador ni habla con un
/// daemon de verdad (sin stack en marcha, aplicar no hace nada). Capturas en
/// <c>TestResults/snapshots/sitios-avanzados-*.png</c> y <c>registros-por-sitio.png</c>.
/// </summary>
public class SitiosAvanzadosViewTests
{
    private static Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake)> OpenAsync(bool running = false) =>
        AjustesSistemaTests.OpenAsync(running);

    private static void Snapshot(MainWindow window, string name) => AjustesSistemaTests.Snapshot(window, name);

    private static void Choose(MainViewModel main, string host) =>
        main.Sites.ChooseCommand.Execute(main.Sites.Rows.Single(r => r.Host == host));

    private static async Task UntilAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    private static string Hosts(FakeRoost fake) => File.ReadAllText(fake.HostsFile);

    [AvaloniaFact(Skip = "Pendiente en Linux: la app (L2).", SkipUnless = nameof(Os.IsWindows), SkipType = typeof(Os))]
    public async Task Panel_AliasRoostTomlYCarpetasAparcadas()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var tienda = Path.Combine(fake.Paths.Www, "tienda");
        File.WriteAllText(ProjectFile.PathIn(tienda), "# del equipo\nphp = \"8.4\"\nroot = \"public\"\naliases = [\"api.tienda.test\", \"tienda-alias.test\"]\n");
        var sites = new SiteManager(fake.Paths);
        sites.SetAliases("tienda", ["api.tienda.test", "tienda-alias.test"]);
        var parked = Path.Combine(Path.GetDirectoryName(fake.Paths.Root)!, "proyectos");
        foreach (var dir in new[] { "alfa", "beta", "Mal Nombre" }) Directory.CreateDirectory(Path.Combine(parked, dir));
        sites.Park(parked);

        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda.test");
        var page = main.Sites;
        Assert.Equal($"api.tienda.test{Environment.NewLine}tienda-alias.test", page.AliasesText);
        Assert.Equal("Y cualquier *.tienda.test.", page.AliasesHint);
        Assert.Equal("Tiene .roost.toml: php 8.4, raíz public, 2 alias.", page.ProjectFileText);
        Assert.Contains("tienda-alias.test", page.CertDetail);
        var row = Assert.Single(page.ParkedRows);
        Assert.Equal((parked, 2, "2 sitios"), (row.Folder, row.Count, row.Detail));
        Assert.True(page.Rows.Single(r => r.Host == "alfa.test").IsParked);
        // Lo que falta en el hosts, junto: los alias de tienda, los aparcados y legacy.
        Assert.Equal("Faltan en el hosts 5 nombres: alfa.test, api.tienda.test, beta.test, legacy.test, tienda-alias.test.", page.HostsPending);
        Snapshot(window, "sitios-avanzados-panel");

        Choose(main, "alfa.test");
        Assert.Equal($"Sale de la carpeta aparcada {parked}", page.ParkedText);
        Assert.Equal("", page.ProjectFileText);
        Assert.Equal("Ninguno; ya responde por cualquier *.alfa.test.", page.AliasesHint);
    }

    [AvaloniaFact]
    public async Task Editar_Renombra_PreguntaPorLaBaseDeDatos_YMueveLaCarpeta()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda.test");

        var editing = main.Sites.EditCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<EditSiteDialogViewModel>(main.Dialog);
        Assert.Equal("Editar tienda.test", dialog.Title);
        Assert.False(dialog.CanConfirm);   // sin cambios
        dialog.Name = "blog";
        Assert.True(dialog.NameIsError);
        Assert.Equal("Ya hay un sitio blog.test.", dialog.NameHint);
        dialog.Name = "Shop";
        Assert.False(dialog.NameIsError);
        Assert.True(dialog.ShowDatabase);
        Assert.Equal(("tienda", "shop"), (dialog.OldDb, dialog.NewDb));
        Assert.False(dialog.DbRunning);   // parada: copiar no se puede
        Assert.Equal("Pasa a llamarse shop.test; sigue con la base de datos tienda.", dialog.Summary);
        Snapshot(window, "sitios-avanzados-editar");

        dialog.ConfirmCommand.Execute(null);
        await editing;
        Assert.Null(main.Dialog);   // sin nada que contar: solo el aviso de abajo
        var shop = new SiteRegistry(fake.Paths).Find("shop")!;
        Assert.Null(new SiteRegistry(fake.Paths).Find("tienda"));
        Assert.Equal("tienda", shop.Database);   // la de antes, fijada
        Assert.Contains("127.0.0.1 shop.test", Hosts(fake));
        Assert.DoesNotContain("tienda.test", Hosts(fake));
        Assert.Equal("shop.test", main.Sites.Selected!.Host);

        // Mover la carpeta: a una que no existe, con «Mover la carpeta allí».
        var target = Path.Combine(Path.GetDirectoryName(fake.Paths.Root)!, "proyectos", "shop");
        editing = main.Sites.EditCommand.ExecuteAsync(null);
        dialog = Assert.IsType<EditSiteDialogViewModel>(main.Dialog);
        dialog.Folder = target;
        Assert.True(dialog.FolderChanged);
        Assert.True(dialog.MoveFiles);
        Assert.False(dialog.FolderIsError);
        dialog.PointOnly = true;
        Assert.True(dialog.FolderIsError);   // solo apuntar a una que no existe, no
        dialog.MoveFiles = true;
        Snapshot(window, "sitios-avanzados-mover");
        dialog.ConfirmCommand.Execute(null);
        await editing;
        Assert.True(File.Exists(Path.Combine(target, "composer.json")));
        Assert.False(Directory.Exists(Path.Combine(fake.Paths.Www, "tienda")));
        Assert.Equal(target, new SiteRegistry(fake.Paths).Find("shop")!.Folder);
    }

    [AvaloniaFact]
    public async Task Alias_ElDialogoValida_YAlGuardarVanAlHosts()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda.test");

        var editing = main.Sites.AliasesCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<SiteAliasesDialogViewModel>(main.Dialog);
        Assert.False(dialog.HasAliases);
        Assert.False(dialog.ByDns);
        dialog.NewAlias = "blog";
        Assert.True(dialog.HintIsError);
        Assert.Equal("blog.test ya es de blog.test.", dialog.Hint);
        dialog.NewAlias = "api.blog.test";
        Assert.Equal("Es un subdominio de blog.test, que es de blog.test: lo serviría ese sitio.", dialog.Hint);
        dialog.NewAlias = "tienda test";
        Assert.StartsWith("Tiene que acabar en .test", dialog.Hint);
        dialog.NewAlias = "api.tienda.test";
        Assert.True(dialog.CanAdd);
        Assert.StartsWith("api.tienda.test: un subdominio.", dialog.Hint);
        dialog.AddCommand.Execute(null);
        dialog.NewAlias = "Tienda-Alias";
        dialog.AddCommand.Execute(null);
        dialog.NewAlias = "tienda-alias.test";
        Assert.Equal("Ya está en la lista.", dialog.Hint);
        dialog.NewAlias = "";
        Assert.Equal(["api.tienda.test", "tienda-alias.test"], dialog.Aliases.Select(a => a.Name));
        Assert.Equal(["subdominio", "alias"], dialog.Aliases.Select(a => a.Kind));
        Snapshot(window, "sitios-avanzados-alias");

        dialog.ConfirmCommand.Execute(null);
        await editing;
        Assert.Equal(["api.tienda.test", "tienda-alias.test"], new SiteRegistry(fake.Paths).Find("tienda")!.Aliases);
        Assert.Contains("127.0.0.1 api.tienda.test", Hosts(fake));
        Assert.Contains("127.0.0.1 tienda-alias.test", Hosts(fake));
        Assert.Equal($"api.tienda.test{Environment.NewLine}tienda-alias.test", main.Sites.AliasesText);

        // Quitar uno.
        editing = main.Sites.AliasesCommand.ExecuteAsync(null);
        dialog = Assert.IsType<SiteAliasesDialogViewModel>(main.Dialog);
        dialog.RemoveCommand.Execute(dialog.Aliases[0]);
        dialog.ConfirmCommand.Execute(null);
        await editing;
        Assert.Equal(["tienda-alias.test"], new SiteRegistry(fake.Paths).Find("tienda")!.Aliases);
        Assert.DoesNotContain("api.tienda.test", Hosts(fake));
    }

    [AvaloniaFact]
    public async Task Alias_ConWordPress_AvisaDelMultisite()
    {
        var (main, _, fake) = await OpenAsync();
        using var __ = fake;
        File.WriteAllText(Path.Combine(fake.Paths.Www, "tienda", "wp-config.php"), "<?php");
        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda.test");
        var editing = main.Sites.AliasesCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<SiteAliasesDialogViewModel>(main.Dialog);
        Assert.True(dialog.IsWordPress);
        dialog.DismissCommand.Execute(null);
        await editing;
    }

    [AvaloniaFact]
    public async Task Aparcar_DesdeLaApp_YElVigilanteMeteYSaca()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        var parked = Path.Combine(Path.GetDirectoryName(fake.Paths.Root)!, "proyectos");
        foreach (var dir in new[] { "alfa", "beta", "gamma", "Mal Nombre" }) Directory.CreateDirectory(Path.Combine(parked, dir));
        await main.NavigateToAsync(main.Sites);

        var parking = main.Sites.ParkAsync(parked);
        await UntilAsync(() => main.Dialog is MessageDialogViewModel);
        var info = (MessageDialogViewModel)main.Dialog!;
        Assert.Equal($"Aparcada {parked}", info.Title);
        Assert.StartsWith("3 sitios nuevos: alfa.test, beta.test, gamma.test.", info.Text);
        Assert.Contains("Mal Nombre: su nombre no vale como .test", info.Detail);
        Snapshot(window, "sitios-avanzados-aparcar");
        info.ChooseCommand.Execute(info.Buttons[0]);
        await parking;
        // Los tres al hosts de una vez.
        foreach (var host in new[] { "alfa.test", "beta.test", "gamma.test" }) Assert.Contains($"127.0.0.1 {host}", Hosts(fake));

        // Lo que haría el vigilante: una carpeta nueva entra y una borrada sale.
        Directory.CreateDirectory(Path.Combine(parked, "delta"));
        Directory.Delete(Path.Combine(parked, "beta"));
        await main.Sites.SyncSourcesAsync(onOpen: false);
        var registry = new SiteRegistry(fake.Paths);
        Assert.NotNull(registry.Find("delta"));
        Assert.Null(registry.Find("beta"));
        Assert.Equal("delta.test nuevo · beta.test quitado · faltan en el hosts", main.FlashText);
        Assert.Contains("delta.test", main.Sites.HostsPending);

        // Y con el vigilante de verdad (la app abierta).
        main.Sites.StartParkWatch();
        try
        {
            Directory.CreateDirectory(Path.Combine(parked, "epsilon"));
            await UntilAsync(() => new SiteRegistry(fake.Paths).Find("epsilon") is not null, seconds: 15);
        }
        finally
        {
            main.Sites.StopParkWatch();
        }
        Assert.Equal([parked], main.Sites.ParkedRows.Select(r => r.Folder));

        // Dejar de aparcar: quita sus sitios, no las carpetas.
        var unparking = main.Sites.UnparkCommand.ExecuteAsync(main.Sites.ParkedRows[0]);
        var confirm = Assert.IsType<MessageDialogViewModel>(main.Dialog);
        confirm.ChooseCommand.Execute(confirm.Buttons.Single(b => b.Result is true));
        await unparking;
        Assert.Null(new SiteRegistry(fake.Paths).Find("alfa"));
        Assert.NotNull(new SiteRegistry(fake.Paths).Find("blog"));
        Assert.True(Directory.Exists(Path.Combine(parked, "alfa")));
        Assert.False(main.Sites.HasParked);
    }

    [AvaloniaFact]
    public async Task RoostToml_AlAbrirLaApp_PoneAlDiaElSitio_YLoQueNoValeSeDice()
    {
        var (main, _, fake) = await OpenAsync();
        using var __ = fake;
        File.WriteAllText(ProjectFile.PathIn(Path.Combine(fake.Paths.Www, "tienda")), "php = \"7.4\"\n");
        await main.NavigateToAsync(main.Sites);

        await main.Sites.SyncSourcesAsync(onOpen: true);
        Assert.Equal("7.4", new SiteRegistry(fake.Paths).Find("tienda")!.Php);
        Assert.Equal("tienda.test al día con su .roost.toml", main.FlashText);
        Assert.False(main.Sites.HasSourcesWarning);

        // Cambiar el PHP desde la app lo guarda también en el .roost.toml (si no, el siguiente up lo desharía).
        Choose(main, "tienda.test");
        Assert.Equal("Tiene .roost.toml: php 7.4.", main.Sites.ProjectFileText);

        File.WriteAllText(ProjectFile.PathIn(Path.Combine(fake.Paths.Www, "tienda")), "php = \"5.6\"\n");
        await main.Sites.SyncSourcesAsync(onOpen: true);
        Assert.True(main.Sites.HasSourcesWarning);
        Assert.Contains("roost php install 5.6", main.Sites.SourcesWarning);
        main.Sites.DismissSourcesWarningCommand.Execute(null);
        Assert.False(main.Sites.HasSourcesWarning);

        // «Guardar en .roost.toml» escribe lo que tiene el sitio.
        File.Delete(ProjectFile.PathIn(Path.Combine(fake.Paths.Www, "tienda")));
        main.Sites.SaveProjectFileCommand.Execute(null);
        Assert.Equal(("7.4", "public"), (ProjectFile.Read(Path.Combine(fake.Paths.Www, "tienda"))!.Php, ProjectFile.Read(Path.Combine(fake.Paths.Www, "tienda"))!.Root));
    }

    [AvaloniaFact]
    public async Task Registros_FiltroPorSitio_YDesdeElPanel()
    {
        var (main, window, fake) = await OpenAsync(running: true);
        using var _ = fake;
        var dir = SiteLogs.Directory(fake.Paths);
        Directory.CreateDirectory(dir);
        File.WriteAllText(SiteLogs.Access(fake.Paths, "tienda"),
            "127.0.0.1 - - [24/Oct/2026:10:01:12 +0200] \"GET / HTTP/2.0\" 200 1843 \"-\" \"curl/8.9\"\n"
            + "127.0.0.1 - - [24/Oct/2026:10:01:13 +0200] \"GET /api/productos HTTP/2.0\" 200 512 \"-\" \"curl/8.9\"\n");
        File.WriteAllText(SiteLogs.Error(fake.Paths, "tienda"),
            "2026/10/24 10:02:40 [error] 31532#40188: *9 FastCGI sent in stderr: \"PHP message: PHP Warning:  Undefined variable $total\" while reading response header from upstream, server: tienda.test\n");
        File.WriteAllText(SiteLogs.Access(fake.Paths, "blog"), "127.0.0.1 - - [24/Oct/2026:10:00:00 +0200] \"GET / HTTP/2.0\" 200 100\n");

        await main.NavigateToAsync(main.Logs);
        var logs = main.Logs;
        Assert.Contains(logs.Files, f => f.Title == "tienda.test: accesos");
        Assert.Contains(logs.Files, f => f.Title == "blog.test: accesos");
        Assert.Equal(["Todos los registros", "blog.test", "legacy.test", "tienda.test"], logs.SiteFilters.Select(f => f.Label));

        // Desde el panel del sitio: Registros solo con los suyos, los errores primero.
        await main.NavigateToAsync(main.Sites);
        Choose(main, "tienda.test");
        await main.Sites.ViewLogsCommand.ExecuteAsync(null);
        Assert.Same(main.Logs, main.Current);
        Assert.Equal("tienda", logs.SiteFilter!.Site);
        Assert.Equal(["tienda.test: errores", "tienda.test: accesos"], logs.Files.Select(f => f.Title));
        Assert.Equal("1 error", logs.Files[0].Badge);
        Snapshot(window, "registros-por-sitio");

        // Un sitio sin registros lo dice; «Todos» vuelve a todo.
        logs.SiteFilter = logs.SiteFilters.Single(f => f.Site == "legacy");
        Assert.Empty(logs.Files);
        Assert.StartsWith("Aún no hay registros de legacy.test", logs.Empty);
        logs.SiteFilter = logs.SiteFilters[0];
        Assert.Contains(logs.Files, f => f.Display == "roostd.log");
    }
}
