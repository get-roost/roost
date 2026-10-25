using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Roost.Core.Install;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;
using Roost.Desktop.Views;
using Roost.Core.Platform;

namespace Roost.Desktop.Tests;

/// <summary>
/// Node (oleada 5): el bloque Node del panel del sitio, npm en el diálogo de salida en vivo,
/// <c>npm run dev</c> aparte con «Parar», instalar Node desde el panel y el paso npm de los pasos de
/// instalación. Los Node son ficheros vacíos y npm va sobre un runner guionizado: nada ejecuta node
/// ni npm, ni va a nodejs.org (el zip de la instalación se sirve en memoria). Capturas en
/// <c>TestResults/snapshots/node-*.png</c>.
/// </summary>
public class NodeViewTests
{
    private static readonly string SnapshotDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");

    private const string VitePackage = """
        {
          "private": true,
          "type": "module",
          "scripts": { "build": "vite build", "dev": "vite" },
          "devDependencies": { "laravel-vite-plugin": "^2.0", "vite": "^7.0" }
        }
        """;

    private static async Task<(MainViewModel Main, MainWindow Window, FakeRoost Fake)> OpenAsync(HttpMessageHandler? http = null)
    {
        var fake = new FakeRoost();
        var main = new MainViewModel(new AppServices(fake.Paths, http is null ? fake.Http : new HttpClient(http), version: FakeRoost.Version));
        main.Stack.Show(FakeRoost.Running);
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

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.True(condition());
    }

    /// <summary>Baja el scroll de la pantalla para que se vea lo de abajo del panel.</summary>
    private static void ScrollPage(MainWindow window, object page, double y)
    {
        Dispatcher.UIThread.RunJobs();
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.DataContext == page);
        scroll.Offset = new Avalonia.Vector(0, y);
    }

    /// <summary>El tienda de FakeRoost (con composer.json y public/), ahora con un package.json de Vite y su lockfile.</summary>
    private static string ViteProject(FakeRoost fake, params (string File, string Text)[] extra)
    {
        var folder = Path.Combine(fake.Paths.Www, "tienda");
        File.WriteAllText(Path.Combine(folder, "package.json"), VitePackage);
        File.WriteAllText(Path.Combine(folder, "package-lock.json"), "{}");
        foreach (var (file, text) in extra) File.WriteAllText(Path.Combine(folder, file), text);
        return folder;
    }

    [AvaloniaFact]
    public async Task SitiosNode_SinNodeDeRoost_OfreceInstalarLaLts()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        ViteProject(fake);

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));

        Assert.True(sites.HasNode);
        Assert.False(sites.NodeReady);
        Assert.True(sites.NodeUsesVite);
        Assert.Equal("falta node_modules/", sites.NodeChip);
        Assert.StartsWith("Node no está instalado en Roost.", sites.NodeProblemText);
        Assert.True(sites.CanInstallNode);
        Assert.Equal("Instalar Node 24 (LTS)", sites.NodeInstallNodeLabel);
        Assert.Equal("npm · package-lock.json", sites.NodeManagerText);
        Assert.Equal(["npm run build", "npm run dev"], sites.NodeScripts.Select(s => s.Label));
        ScrollPage(window, sites, 420);
        Snapshot(window, "node-sitios-sin-node");

        // Un sitio sin package.json no tiene bloque Node.
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "blog.test"));
        Assert.False(sites.HasNode);
    }

    [AvaloniaFact]
    public async Task SitiosNode_ElNodeDeLaCarpetaYPorQue_LosScripts_YElServerDeVite()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        fake.InstallFakeNode("22.23.4");
        fake.InstallFakeNode("24.22.1");
        ViteProject(fake, (".nvmrc", "22\n"));

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));

        Assert.True(sites.NodeReady);
        Assert.Equal("Node 22.23.4 · el que pide .nvmrc (22)", sites.NodeText);
        Assert.False(sites.HasNodeProblem);
        Assert.True(sites.NodeCanInstallDeps);
        Assert.True(sites.NodeNeedsInstall);
        Assert.Equal("npm install", sites.NodeInstallLabel);
        Assert.Equal([false, true], sites.NodeScripts.Select(s => s.Script.LongRunning));
        Assert.Contains("se para con «Parar»", sites.NodeScripts[1].Tip);
        ScrollPage(window, sites, 420);
        Snapshot(window, "node-sitios");

        // Sin .nvmrc, el global (el más nuevo); con node_modules, el aviso se va.
        File.Delete(Path.Combine(sites.Selected!.Site.Folder, ".nvmrc"));
        Directory.CreateDirectory(Path.Combine(sites.Selected.Site.Folder, "node_modules"));
        main.NotifyConfigChanged();
        Assert.Equal("Node 24.22.1 · el global (el más nuevo instalado)", sites.NodeText);
        Assert.False(sites.NodeNeedsInstall);
        Assert.Equal("node_modules instalado", sites.NodeChip);

        // pnpm sin corepack en ese Node: lo dice, y no ofrece instalar.
        File.WriteAllText(Path.Combine(sites.Selected.Site.Folder, "pnpm-lock.yaml"), "");
        File.Delete(Path.Combine(fake.Paths.NodeBin, "24.22.1", "node_modules", "corepack", "dist", "corepack.js"));
        main.NotifyConfigChanged();
        Assert.False(sites.NodeCanInstallDeps);
        Assert.Contains("va con corepack, y Node 24.22.1 no lo trae", sites.NodeManagerText);
    }

    [AvaloniaFact]
    public async Task SitiosNode_NpmInstall_EnElDialogoDeSalidaEnVivo_ConElNodeDeRoost()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        var node = fake.InstallFakeNode("22.23.4");
        var folder = ViteProject(fake, (".nvmrc", "lts/jod\n"));
        var runner = new ScriptedCommandRunner(
            "npm warn deprecated inflight@1.0.6: This module is not supported, and leaks memory.",
            "",
            "added 97 packages, and audited 98 packages in 6s");

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        var running = sites.NodeInstallDepsCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is CommandOutputDialogViewModel);
        var dialog = (CommandOutputDialogViewModel)main.Dialog!;
        dialog.FlushLines();

        var spec = runner.Calls.Single();
        Assert.Equal(node.Executable, spec.Exe);   // el node.exe de Roost, nunca uno del sistema
        Assert.Equal([node.NpmCli, "install"], spec.Arguments);
        Assert.Equal(folder, spec.WorkingDirectory);
        Assert.StartsWith(node.Directory + Path.PathSeparator, spec.Environment!["PATH"]);
        Assert.Equal("1", spec.Environment["NO_COLOR"]);
        Assert.Equal("npm install", dialog.CommandText);
        Assert.Equal("npm install · tienda.test", dialog.Title);
        Assert.True(dialog.Lines[0].IsWarn);
        Assert.True(sites.NodeBusy);
        Snapshot(window, "node-salida-npm-install");

        Directory.CreateDirectory(Path.Combine(folder, "node_modules"));
        runner.Finish(0);
        await UntilAsync(() => dialog.Done);
        Assert.True(dialog.Succeeded);
        dialog.FinishCommand.Execute(null);
        await running;
        Assert.False(sites.NodeBusy);
        Assert.Equal("node_modules instalado", sites.NodeChip);   // el panel vuelve a mirar
        Assert.Equal("npm install terminado en tienda.test", main.FlashText);
    }

    [AvaloniaFact]
    public async Task SitiosNode_NpmRunDev_SigueEnMarchaAunqueSeCierre_YSeParaDesdeElPanel()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        var node = fake.InstallFakeNode("24.22.1");
        ViteProject(fake);
        var runner = new ScriptedCommandRunner(
            "",
            "> dev",
            "> vite",
            "",
            "\u001b[32m  VITE v7.1.3\u001b[39m  ready in 412 ms",
            "",
            "  ➜  Local:   https://127.0.0.1:5173/",
            "  ➜  Local:   https://tienda.test:5173/",
            "  ➜  Network: use --host to expose",
            "",
            "  LARAVEL v12.40.0  plugin v2.0.1",
            "",
            "  ➜  APP_URL: https://tienda.test");

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        var dev = sites.NodeScripts.Single(s => s.Script.Name == "dev");
        var showing = sites.NodeRunScriptCommand.ExecuteAsync(dev);
        await UntilAsync(() => main.Dialog is NodeDevServerDialogViewModel && runner.Calls.Count == 1);
        var dialog = (NodeDevServerDialogViewModel)main.Dialog!;
        var session = dialog.Session;
        // Las líneas de npm run dev llegan desde otro hilo: se espera a la de la URL.
        await UntilAsync(() =>
        {
            session.FlushLines();
            return session.Url is not null;
        });

        Assert.Equal([node.NpmCli, "run", "dev"], runner.Calls[0].Arguments);
        Assert.True(session.Running);
        Assert.True(dialog.CanDismiss);   // cerrarlo no lo para
        Assert.Equal("https://tienda.test:5173/", session.Url);
        Assert.Equal("  VITE v7.1.3  ready in 412 ms", session.Lines[4].Text);   // sin los colores de la terminal
        Assert.StartsWith("npm run dev en marcha desde las ", session.StatusText);
        Assert.EndsWith(" · https://tienda.test:5173/", session.StatusText);
        Assert.Equal("npm run dev · tienda.test", dialog.Title);
        Assert.True(session.CanOpen);
        Assert.False(session.HasUrlWarning);   // en https: nada que avisar
        Snapshot(window, "node-npm-run-dev");

        // «Dejar en marcha»: se cierra la ventana, el proceso sigue y el panel lo dice.
        dialog.FinishCommand.Execute(null);
        await showing;
        Assert.Null(main.Dialog);
        Assert.False(runner.WasCanceled);
        Assert.True(sites.HasNodeDev);
        Assert.Same(session, sites.NodeDev);
        ScrollPage(window, sites, 420);
        Snapshot(window, "node-sitios-dev-en-marcha");

        // Sigue ahí al volver al sitio, y «Ver salida» abre otra ventana sobre la misma sesión.
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "blog.test"));
        Assert.False(sites.HasNodeDev);
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        Assert.Same(session, sites.NodeDev);
        var again = sites.NodeShowDevCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is NodeDevServerDialogViewModel);
        Assert.Same(session, ((NodeDevServerDialogViewModel)main.Dialog!).Session);

        // «Parar» mata el proceso; la ventana se queda para leer cómo acabó. El public/hot que dejó
        // laravel-vite-plugin (sin Ctrl+C no lo quita él) se borra: la página vuelve a los assets compilados.
        var hot = Path.Combine(sites.Selected!.Site.Folder, "public", "hot");
        File.WriteAllText(hot, "https://tienda.test:5173");
        ((NodeDevServerDialogViewModel)main.Dialog!).StopCommand.Execute(null);
        await UntilAsync(() => !session.Running);
        Assert.False(File.Exists(hot));
        Assert.True(runner.WasCanceled);
        Assert.True(session.Stopped);
        Assert.Equal("npm run dev parado", session.StatusText);
        Assert.False(session.CanOpen);
        Assert.StartsWith("Parado", session.ResultText);
        Assert.Contains("VITE v7.1.3", session.Output);
        ((NodeDevServerDialogViewModel)main.Dialog!).FinishCommand.Execute(null);
        await again;
        Assert.Equal("npm run dev parado", sites.NodeDev!.StatusText);
    }

    [AvaloniaFact]
    public async Task SitiosNode_ElNvmrcPideUnoQueNoEsta_LoInstalaDesdeElPanel_Verificado()
    {
        // nodejs.org de pega con los dos paquetes: el zip de Windows y el tar.gz de Linux (NodeCatalog elige por sistema).
        var zip = NodeZip("20.20.2");
        var tar = NodeTar("20.20.2");
        var dist = new NodeDist(new FakeUpstream())
        {
            ["https://nodejs.org/dist/index.json"] = Encoding.UTF8.GetBytes("""[{"version":"v20.20.2","files":["win-x64-zip","linux-x64"],"npm":"10.8.2","lts":"Iron","security":true}]"""),
            ["https://nodejs.org/dist/v20.20.2/SHASUMS256.txt"] = Encoding.UTF8.GetBytes(
                $"{Convert.ToHexStringLower(SHA256.HashData(zip))}  node-v20.20.2-win-x64.zip\n{Convert.ToHexStringLower(SHA256.HashData(tar))}  node-v20.20.2-linux-x64.tar.gz\n"),
            ["https://nodejs.org/dist/v20.20.2/node-v20.20.2-win-x64.zip"] = zip,
            ["https://nodejs.org/dist/v20.20.2/node-v20.20.2-linux-x64.tar.gz"] = tar,
        };
        var (main, window, fake) = await OpenAsync(dist);
        using var _ = fake;
        fake.InstallFakeNode("24.22.1");
        ViteProject(fake, (".nvmrc", "20\n"));

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        Assert.Equal(".nvmrc pide Node 20, que no está instalado en Roost.", sites.NodeProblemText);
        Assert.Equal("Instalar Node 20", sites.NodeInstallNodeLabel);

        var installing = sites.NodeInstallNodeCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is InstallDialogViewModel || installing.IsCompleted);
        if (main.Dialog is InstallDialogViewModel install)
        {
            Assert.Equal("Instalando Node 20.20.2", install.Title);
            Assert.Equal("Verificar el SHA-256", install.Steps[2].Label);
        }
        await installing;

        Assert.True(File.Exists(Path.Combine(fake.Paths.NodeBin, "20.20.2", InstalledNode.ExecutableRelative)));   // node.exe o bin/node
        Assert.True(sites.NodeReady);
        Assert.Equal("Node 20.20.2 · el que pide .nvmrc (20)", sites.NodeText);
        Assert.Equal("Node 20.20.2 instalado", main.FlashText);
        Assert.Contains("https://nodejs.org/dist/v20.20.2/SHASUMS256.txt", dist.Requests);
    }

    [AvaloniaFact]
    public async Task PasosDeInstalacion_ConNodeDeRoost_ElPasoNpmVaEncendido()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        fake.InstallFakeNode("24.22.1");
        var folder = fake.Project("shop", "artisan", "composer.json", "vendor/", "public/", ".env.example");
        File.WriteAllText(Path.Combine(folder, "package.json"), VitePackage);
        File.WriteAllText(Path.Combine(folder, "package-lock.json"), "{}");
        new Roost.Core.Sites.SiteManager(fake.Paths).Add("shop", folder);
        main.NotifyConfigChanged();

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.DbRunner = new FakeDbClient(exists: true);
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "shop.test"));
        var showing = sites.DeployStepsCommand.ExecuteAsync(null);
        await UntilAsync(() => main.Dialog is DeployStepsDialogViewModel);
        var dialog = (DeployStepsDialogViewModel)main.Dialog!;

        var npm = dialog.Rows.Single(r => r.Step.Id == "npm");
        Assert.True(npm.Available);
        Assert.True(npm.IsChecked);
        Assert.Equal("npm ci y npm run build", npm.Label);
        Assert.Equal("Con Node 24.22.1, el global (el más nuevo instalado): lo que fija el lockfile, y luego el build.", npm.Hint);
        Assert.Contains("npm", dialog.Note);
        Snapshot(window, "node-pasos-con-npm");

        dialog.DismissCommand.Execute(null);
        await showing;
    }

    [AvaloniaFact]
    public async Task SitiosNode_ViteEnHttp_AvisaDelContenidoMixto()
    {
        var (main, window, fake) = await OpenAsync();
        using var _ = fake;
        fake.InstallFakeNode("24.22.1");
        ViteProject(fake);
        var runner = new ScriptedCommandRunner("  VITE v8.3.5  ready in 271 ms", "", "  ➜  Local:   http://localhost:5173/");

        await main.NavigateToAsync(main.Sites);
        var sites = main.Sites;
        sites.Runner = runner;
        sites.ChooseCommand.Execute(sites.Rows.Single(r => r.Host == "tienda.test"));
        var showing = sites.NodeRunScriptCommand.ExecuteAsync(sites.NodeScripts.Single(s => s.Script.Name == "dev"));
        await UntilAsync(() => main.Dialog is NodeDevServerDialogViewModel);
        var dialog = (NodeDevServerDialogViewModel)main.Dialog!;
        // npm run dev va fuera del hilo de la UI: sus líneas pueden llegar después de abrirse la ventana.
        await UntilAsync(() =>
        {
            dialog.Session.FlushLines();
            return dialog.Session.Url is not null;
        });

        Assert.Equal("http://localhost:5173/", dialog.Session.Url);
        Assert.StartsWith("Sirve en http: desde https://tienda.test el navegador bloquea", dialog.Session.UrlWarning);
        Snapshot(window, "node-vite-en-http");

        dialog.StopCommand.Execute(null);
        await UntilAsync(() => dialog.Session.Done);
        Assert.False(dialog.Session.HasUrlWarning);
        dialog.FinishCommand.Execute(null);
        await showing;
    }

    [Fact]
    public void ServerDeVite_ConElCertificadoDelSitio_RutasConBarraYCorsDeSuPagina()
    {
        var snippet = SitesViewModel.ViteServer("tienda.test", @"C:\roost\certs\sites\tienda.key", @"C:\roost\certs\sites\tienda.crt", "https://tienda.test:8444");

        Assert.StartsWith("server: {", snippet);
        Assert.Contains("host: 'tienda.test',", snippet);
        Assert.Contains("key: fs.readFileSync('C:/roost/certs/sites/tienda.key'),", snippet);
        Assert.Contains("cert: fs.readFileSync('C:/roost/certs/sites/tienda.crt'),", snippet);
        Assert.Contains("hmr: { host: 'tienda.test' },", snippet);
        Assert.Contains("cors: { origin: 'https://tienda.test:8444' },", snippet);
    }

    /// <summary>Un zip como el de nodejs.org (todo dentro de node-v&lt;versión&gt;-win-x64/), con npm.</summary>
    private static byte[] NodeZip(string version)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { OsNames.Exe("node"), "node_modules/npm/bin/npm-cli.js", "node_modules/npm/bin/npx-cli.js", "node_modules/corepack/dist/corepack.js" })
            {
                using var writer = new StreamWriter(zip.CreateEntry($"node-v{version}-win-x64/{name}").Open());
                writer.Write("// de pega");
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Un tar.gz como el de nodejs.org para Linux (todo dentro de node-v&lt;versión&gt;-linux-x64/: bin/node y lib/node_modules), con npm.</summary>
    private static byte[] NodeTar(string version)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var name in new[] { "bin/node", "lib/node_modules/npm/bin/npm-cli.js", "lib/node_modules/npm/bin/npx-cli.js", "lib/node_modules/corepack/dist/corepack.js" })
            {
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"node-v{version}-linux-x64/{name}")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("// de pega")),
                    Mode = name == "bin/node"
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                tar.WriteEntry(entry);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>nodejs.org en memoria delante de la «red» de los tests (FakeUpstream).</summary>
    private sealed class NodeDist(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public List<string> Requests { get; } = [];

        public byte[] this[string url]
        {
            set => _files[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return _files.TryGetValue(url, out var body)
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request })
                : base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>Node «instalado» en la raíz de pega (ficheros vacíos: nada lo ejecuta).</summary>
internal static class FakeRoostNode
{
    public static InstalledNode InstallFakeNode(this FakeRoost fake, string version, bool corepack = true)
    {
        var dir = Path.Combine(fake.Paths.NodeBin, version);
        foreach (var file in new[] { OsNames.Exe("node"), Path.Combine("node_modules", "npm", "bin", "npm-cli.js"), Path.Combine("node_modules", "npm", "bin", "npx-cli.js") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
            File.WriteAllText(Path.Combine(dir, file), "");
        }
        File.WriteAllText(Path.Combine(dir, "node_modules", "npm", "package.json"), """{ "version": "11.19.1" }""");
        if (corepack)
        {
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "corepack", "dist"));
            File.WriteAllText(Path.Combine(dir, "node_modules", "corepack", "dist", "corepack.js"), "");
        }
        return new InstalledNode(version, dir);
    }
}
