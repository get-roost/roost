using Roost.Core.Install;
using Roost.Core.Node;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;

namespace Roost.Core.Tests;

/// <summary>
/// Qué Node usa una carpeta (los shims node, npm y npx, la app y los pasos): la variable, el
/// <c>.nvmrc</c> o el <c>.node-version</c> hacia arriba, <c>engines.node</c> y el global; el entorno con
/// el que se lanza; las órdenes de npm, pnpm y yarn; el package.json; y los shims de Node. Todo sobre
/// una raíz temporal: nada se ejecuta.
/// </summary>
public class TerminalNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-terminal-node-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"), WwwDir: Path.Combine(_root, "www"));

    private static readonly DateOnly Hoy = new(2026, 10, 24);

    private void Node(params string[] versions)
    {
        foreach (var version in versions)
        {
            var dir = Path.Combine(Paths.NodeBin, version);
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "npm", "bin"));
            File.WriteAllText(Path.Combine(dir, OsNames.Exe("node")), "");
            File.WriteAllText(Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js"), "");
        }
    }

    private string Folder(params string[] parts)
    {
        var dir = Path.Combine([_root, "www", .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private NodeChoice Resolve(string dir, string? pinned = null) => TerminalNode.Resolve(Paths, dir, pinned, Hoy);

    [Fact]
    public void SinNada_ElMasNuevoInstalado_YConGlobalElDeRoostToml()
    {
        Node("22.23.4", "24.22.1");

        var choice = Resolve(Folder("x"));
        Assert.Equal("24.22.1", choice.Node.Version);
        Assert.Equal(NodeChoiceSource.Global, choice.Source);
        Assert.Equal("el Node global (el mas nuevo instalado)", choice.Reason);

        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.ConfigFile, "# mi configuracion\n[php]\nversion = \"8.4\"\n");
        NodeConfig.SetVersion(Paths.ConfigFile, "22");
        Assert.Equal("22", NodeConfig.ReadVersion(Paths.ConfigFile));
        Assert.Contains("# mi configuracion", File.ReadAllText(Paths.ConfigFile));   // lo demás se queda
        Assert.Equal("8.4", Roost.Core.Config.RoostConfig.Load(Paths.ConfigFile).PhpVersion);
        choice = Resolve(Folder("x"));
        Assert.Equal("22.23.4", choice.Node.Version);
        Assert.StartsWith("el Node global (22, en ", choice.Reason);
        Assert.Equal("22", choice.Spec);

        NodeConfig.SetVersion(Paths.ConfigFile, "20");
        var ex = Assert.Throws<RoostException>(() => Resolve(Folder("x")));
        Assert.Contains("El Node global es 20", ex.Message);
        Assert.Contains("roost node install 20", ex.Message);
        NodeConfig.SetVersion(Paths.ConfigFile, null);
        Assert.Null(NodeConfig.ReadVersion(Paths.ConfigFile));
    }

    [Fact]
    public void Nvmrc_HaciaArriba_GanaAlGlobal_YConLosDosEnLaMismaCarpetaElNvmrc()
    {
        Node("22.23.4", "24.22.1");
        var project = Folder("tienda");
        var deep = Folder("tienda", "resources", "js");
        File.WriteAllText(Path.Combine(project, ".nvmrc"), "# la del servidor\nv22 # Jod\n");

        var choice = Resolve(deep);
        Assert.Equal("22.23.4", choice.Node.Version);
        Assert.Equal(NodeChoiceSource.VersionFile, choice.Source);
        Assert.Equal(Path.Combine(project, ".nvmrc"), choice.File);
        Assert.Equal("v22", choice.Spec);
        Assert.Equal($"{Path.Combine(project, ".nvmrc")} pide v22", choice.Reason);

        File.WriteAllText(Path.Combine(project, ".node-version"), "24\n");
        Assert.Equal("22.23.4", Resolve(deep).Node.Version);   // el .nvmrc va antes
        File.WriteAllText(Path.Combine(deep, ".node-version"), "nodejs 24.22.1\n");
        Assert.Equal("24.22.1", Resolve(deep).Node.Version);   // el más cercano

        // La variable gana a todo.
        Assert.Equal("22.23.4", Resolve(deep, "lts/jod").Node.Version);
        Assert.Equal(NodeChoiceSource.Variable, Resolve(deep, "22").Source);
    }

    [Theory]
    [InlineData("lts/*", "24.22.1")]
    [InlineData("lts/-1", "22.23.4")]
    [InlineData("node", "24.22.1")]
    [InlineData("22.23", "22.23.4")]
    [InlineData("\uFEFF22\r\n", "22.23.4")]
    public void Nvmrc_EntiendeLoDeNvmYFnm(string content, string version)
    {
        Node("22.23.4", "24.22.1");
        var project = Folder("p");
        File.WriteAllText(Path.Combine(project, ".nvmrc"), content);

        Assert.Equal(version, Resolve(project).Node.Version);
    }

    [Fact]
    public void LoQuePideElProyecto_SiNoEstaInstalado_EsUnErrorQueDiceQueInstalar()
    {
        Node("24.22.1");
        var project = Folder("legacy");
        File.WriteAllText(Path.Combine(project, ".nvmrc"), "18\n");

        var ex = Assert.Throws<RoostException>(() => Resolve(project));
        Assert.Contains(".nvmrc pide 18, pero no hay ningun Node de Roost que case. Instalalo: roost node install 18", ex.Message);
        Assert.Equal("18", TerminalNode.MissingSpec(Paths, project));
        Assert.Null(TerminalNode.TryResolve(Paths, project, out var problem, Hoy));
        Assert.Equal(ex.Message, problem);

        File.WriteAllText(Path.Combine(project, ".nvmrc"), "system\n");
        Assert.Contains("Roost no lo usa", Assert.Throws<RoostException>(() => Resolve(project)).Message);
        Assert.Null(TerminalNode.MissingSpec(Paths, project));

        File.WriteAllText(Path.Combine(project, ".nvmrc"), "\n# nada\n");
        Assert.Contains("esta vacio", Assert.Throws<RoostException>(() => Resolve(project)).Message);

        File.WriteAllText(Path.Combine(project, ".nvmrc"), "la buena\n");
        Assert.Contains("no se entiende", Assert.Throws<RoostException>(() => Resolve(project)).Message);
    }

    [Fact]
    public void SinNingunNode_LoDice_YOfreceLaLtsDeRoost()
    {
        var ex = Assert.Throws<RoostException>(() => Resolve(Folder("x")));
        Assert.Equal("No hay ningun Node instalado por Roost. Instala uno: roost node install 24", ex.Message);
        Assert.Equal("24", TerminalNode.MissingSpec(Paths, Folder("x")));
    }

    [Fact]
    public void EnginesNode_SiElGlobalLoCumpleElGlobal_SiNoElMasNuevoQueLoCumpla()
    {
        Node("22.23.4", "24.22.1");
        var project = Folder("app");
        var src = Folder("app", "src");

        File.WriteAllText(Path.Combine(project, "package.json"), """{ "engines": { "node": ">=20" } }""");
        var choice = Resolve(src);
        Assert.Equal("24.22.1", choice.Node.Version);
        Assert.Equal(NodeChoiceSource.Global, choice.Source);
        Assert.Equal(">=20", choice.Engines);
        Assert.True(choice.EnginesMet);
        Assert.Contains("que cumple engines.node (>=20)", choice.Reason);

        File.WriteAllText(Path.Combine(project, "package.json"), """{ "engines": { "node": "^22.11" } }""");
        choice = Resolve(src);
        Assert.Equal("22.23.4", choice.Node.Version);
        Assert.Equal(NodeChoiceSource.Engines, choice.Source);
        Assert.Contains("pide node ^22.11 en engines, y el global (24.22.1) no lo cumple", choice.Reason);

        File.WriteAllText(Path.Combine(project, "package.json"), """{ "engines": { "node": "18.x" } }""");
        choice = Resolve(src);
        Assert.Equal("24.22.1", choice.Node.Version);
        Assert.False(choice.EnginesMet);
        Assert.Contains("ningun Node de Roost lo cumple (roost node install 18.x)", choice.Reason);

        // Un .nvmrc manda sobre engines.
        File.WriteAllText(Path.Combine(project, ".nvmrc"), "24");
        Assert.Equal(NodeChoiceSource.VersionFile, Resolve(src).Source);
    }

    [WindowsFact("Pendiente en Linux: Terminal y herramientas (L2).")]
    public void Entorno_SuCarpetaPrimeroEnElPath_LaCaDeRoost_YSinColoresNiPreguntasEnLaApp()
    {
        Node("24.22.1");
        var node = new NodeInstaller(Paths).ListInstalled().Single();
        var ca = new Roost.Core.Sites.LocalCa(Paths).CaCertFile;
        var env = new Dictionary<string, string?> { ["PATH"] = @"C:\Windows\system32;C:\Program Files\nodejs" };

        var terminal = TerminalNode.Environment(Paths, node, interactive: true, name => env.GetValueOrDefault(name));
        Assert.Equal(node.Directory + @";C:\Windows\system32;C:\Program Files\nodejs", terminal["PATH"]);
        Assert.False(terminal.ContainsKey("NODE_EXTRA_CA_CERTS"));   // aún no hay CA
        Assert.False(terminal.ContainsKey("NO_COLOR"));

        Directory.CreateDirectory(Path.GetDirectoryName(ca)!);
        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----");
        var app = TerminalNode.Environment(Paths, node, interactive: false, name => env.GetValueOrDefault(name));
        Assert.Equal(ca, app["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal("1", app["NO_COLOR"]);
        Assert.Equal("0", app["COREPACK_ENABLE_DOWNLOAD_PROMPT"]);
        Assert.Equal("0", app["COREPACK_ENABLE_AUTO_PIN"]);
        Assert.DoesNotContain(app.Keys, k => k.Contains("cache", StringComparison.OrdinalIgnoreCase) || k.Contains("userconfig", StringComparison.OrdinalIgnoreCase));

        // La del usuario no se pisa.
        env["NODE_EXTRA_CA_CERTS"] = @"C:\mis\cas.pem";
        Assert.False(TerminalNode.Environment(Paths, node, true, name => env.GetValueOrDefault(name)).ContainsKey("NODE_EXTRA_CA_CERTS"));
    }

    [WindowsFact("Pendiente en Linux: Terminal y herramientas (L2).")]
    public void Ordenes_NpmConSuNpmCli_PnpmYYarnConCorepack_YBunNo()
    {
        Node("24.22.1");
        var node = new NodeInstaller(Paths).ListInstalled().Single();
        var folder = Folder("tienda");
        var npm = new NodeProject(folder, true) { LockFile = "package-lock.json" };
        var pnpm = new NodeProject(folder, true) { Manager = NodeManager.Pnpm, LockFile = "pnpm-lock.yaml" };

        var spec = NodeTool.Command(Paths, node, NodeManager.Npm, NodeTool.InstallArguments(npm, clean: true), folder);
        Assert.Equal(node.Executable, spec.Exe);
        Assert.Equal([node.NpmCli, "ci"], spec.Arguments);
        Assert.Equal(folder, spec.WorkingDirectory);
        Assert.StartsWith(node.Directory + ";", spec.Environment!["PATH"]);
        Assert.Equal("1", spec.Environment["NO_COLOR"]);
        Assert.Equal(["install"], NodeTool.InstallArguments(npm, clean: false));
        Assert.Equal(["install"], NodeTool.InstallArguments(npm with { LockFile = null }, clean: true));
        Assert.Equal("npm ci", NodeTool.Display(NodeManager.Npm, ["ci"]));
        Assert.Equal("npm run build", NodeTool.Display(NodeManager.Npm, NodeTool.RunArguments("build")));

        // pnpm, con corepack: este Node de pega no lo trae.
        Assert.Contains("corepack", NodeTool.WhyNot(NodeManager.Pnpm, node));
        Assert.Throws<RoostException>(() => NodeTool.Command(Paths, node, NodeManager.Pnpm, ["install"], folder));
        Directory.CreateDirectory(Path.GetDirectoryName(node.CorepackJs)!);
        File.WriteAllText(node.CorepackJs, "");
        Assert.Null(NodeTool.WhyNot(NodeManager.Pnpm, node));
        var install = NodeTool.InstallArguments(pnpm, clean: true);
        Assert.Equal(["install", "--frozen-lockfile"], install);
        Assert.Equal([node.CorepackJs, "pnpm", "install", "--frozen-lockfile"], NodeTool.Command(Paths, node, NodeManager.Pnpm, install, folder).Arguments);
        Assert.Equal("pnpm install --frozen-lockfile", NodeTool.Display(NodeManager.Pnpm, install));
        Assert.Contains("Roost no trae bun", NodeTool.WhyNot(NodeManager.Bun, node));
    }

    [Fact]
    public void PackageJson_ScriptsEnOrden_EnginesGestorYLockfile()
    {
        var folder = Folder("laravel");
        File.WriteAllText(Path.Combine(folder, "package.json"), """
            {
              // comentario: npm no los admite, pero no rompe nada
              "name": "laravel",
              "private": true,
              "type": "module",
              "scripts": { "build": "vite build", "dev": "vite", "lint": 42, "preview": "vite preview" },
              "engines": { "node": ">=20" },
              "devDependencies": { "laravel-vite-plugin": "^2.0", "vite": "^7.0" },
            }
            """);
        File.WriteAllText(Path.Combine(folder, "package-lock.json"), "{}");

        var project = NodeProject.Read(folder);
        Assert.True(project.HasPackageJson);
        Assert.False(project.Unreadable);
        Assert.Equal("laravel", project.Name);
        Assert.Equal(["build", "dev", "preview"], project.Scripts.Select(s => s.Name));   // «lint» no es texto
        Assert.Equal([false, true, true], project.Scripts.Select(s => s.LongRunning));
        Assert.Equal(">=20", project.EnginesNode);
        Assert.Equal(NodeManager.Npm, project.Manager);
        Assert.True(project.HasNpmLock);
        Assert.True(project.UsesVite);
        Assert.True(project.NeedsInstall);
        Assert.True(project.HasScript("build"));

        File.WriteAllText(Path.Combine(folder, "yarn.lock"), "");
        Assert.Equal(NodeManager.Yarn, NodeProject.Read(folder).Manager);
        File.WriteAllText(Path.Combine(folder, "package.json"), """{ "packageManager": "pnpm@9.15.0+sha512.abc" }""");
        Directory.CreateDirectory(Path.Combine(folder, "node_modules"));
        project = NodeProject.Read(folder);
        Assert.Equal(NodeManager.Pnpm, project.Manager);   // packageManager manda sobre el lockfile
        Assert.Equal("pnpm@9.15.0+sha512.abc", project.PackageManagerField);
        Assert.False(project.NeedsInstall);

        File.WriteAllText(Path.Combine(folder, "package.json"), "{ esto no es JSON");
        Assert.True(NodeProject.Read(folder).Unreadable);
        Assert.Empty(NodeProject.Read(folder).Scripts);
        Assert.False(NodeProject.Read(Folder("vacia")).HasPackageJson);
    }

    [Theory]
    [InlineData("dev", "vite", true)]
    [InlineData("build", "vite build", false)]
    [InlineData("serve", "node server.js", true)]
    [InlineData("start", "node index.js", true)]
    [InlineData("dev:ssr", "vite --ssr", true)]
    [InlineData("front", "vite", true)]
    [InlineData("front", "vite build --watch", true)]
    [InlineData("css", "tailwindcss -i in.css -o out.css --watch", true)]
    [InlineData("api", "nodemon api.js", true)]
    [InlineData("site", "next dev", true)]
    [InlineData("site", "next build", false)]
    [InlineData("test", "vitest run", false)]
    [InlineData("lint", "eslint .", false)]
    [InlineData("prod", "vite build && cp -r dist ../public", false)]
    [InlineData("todo", "concurrently \"npm:dev:*\"", true)]
    public void ScriptsQueSeQuedanEnMarcha(string name, string command, bool expected) =>
        Assert.Equal(expected, NodeProject.IsLongRunning(name, command));

    [WindowsFact("Pendiente en Linux: Terminal y herramientas (L2).")]
    public void Shims_DeNodeSoloConUnNodeDeRoost_YAlQuitarElUltimoSeQuitan()
    {
        var source = Path.Combine(_root, "app");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "roost-shim.exe"), "shim");
        var shims = new ShimInstaller(Paths);

        shims.Install(source);
        Assert.Equal(["php"], shims.Wanted);
        Assert.False(shims.Has("node"));

        Node("24.22.1");
        Assert.Equal(["php", "node", "npm", "npx"], shims.Wanted);
        Assert.Equal(["node", "npm", "npx"], shims.AddMissing(source));
        Assert.True(shims.Has("npx"));
        Assert.Empty(shims.RemoveUnwanted());

        new NodeInstaller(Paths).Remove(new NodeInstaller(Paths).ListInstalled().Single());
        Assert.Equal(["node", "npm", "npx"], shims.RemoveUnwanted());
        Assert.False(shims.Has("node"));
        Assert.True(shims.Has("php"));
    }

    [WindowsFact("Pendiente en Linux: Terminal y herramientas (L2).")]
    public void QuienLeGanaAlShimDeNode_PorSuNombre()
    {
        var env = new Dictionary<string, string?> { ["NVM_SYMLINK"] = @"C:\Program Files\nodejs", ["NVM_HOME"] = @"C:\Users\yo\AppData\Local\nvm" };
        string? Owner(string path, bool nvm = true) => ShimShadowing.NodeOwner(path, name => nvm ? env.GetValueOrDefault(name) : null);

        Assert.Equal("nvm-windows", Owner(@"C:\Program Files\nodejs\node.exe"));
        Assert.Equal("el instalador de Node (nodejs.org)", Owner(@"C:\Program Files\nodejs\node.exe", nvm: false));
        Assert.Equal("nvm-windows", Owner(@"C:\Users\yo\AppData\Local\nvm\v22.11.0\node.exe"));
        Assert.Equal("Volta", Owner(@"C:\Users\yo\AppData\Local\Volta\bin\node.exe"));
        Assert.Equal("fnm", Owner(@"C:\Users\yo\AppData\Local\fnm_multishells\1234_5678\node.exe"));
        Assert.Equal("el nvm de Herd", Owner(@"C:\Users\yo\.config\herd\bin\nvm\nodejs\node.exe", nvm: false));
        Assert.Equal("Scoop", Owner(@"C:\Users\yo\scoop\shims\node.exe"));
        Assert.Equal("Laragon", Owner(@"C:\laragon\bin\nodejs\node-v18\node.exe"));
        Assert.Null(Owner(@"C:\herramientas\node.exe", nvm: false));

        // En la sesión: fnm pone su carpeta delante en cada terminal, y eso no está en el registro.
        var shims = @"C:\roost\bin\shims";
        var session = ShimShadowing.FindInSession("node", shims, @"C:\Users\yo\AppData\Local\fnm_multishells\1_2;" + shims,
            p => p == @"C:\Users\yo\AppData\Local\fnm_multishells\1_2\node.exe");
        Assert.Equal(@"C:\Users\yo\AppData\Local\fnm_multishells\1_2\node.exe", session?.Path);
        Assert.Null(ShimShadowing.FindInSession("node", shims, shims + @";C:\Program Files\nodejs", _ => true));
    }

    [Fact]
    public void ShimArguments_ElScriptEntreComillas_YLaLineaTalCual() =>
        Assert.Equal("\"C:\\roost\\bin\\node\\24.22.1\\node_modules\\npm\\bin\\npm-cli.js\" run dev -- --host \"tienda.test\"",
            TerminalNode.ShimArguments(@"C:\roost\bin\node\24.22.1\node_modules\npm\bin\npm-cli.js", "run dev -- --host \"tienda.test\""));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
