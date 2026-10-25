using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// J2: cambiar la raíz de Roost. <c>[paths] root</c> en roost.toml (y con <c>ROOST_HOME</c>, en el
/// suyo), y mover lo que hay: copia comprobada, enlaces recreados (nunca lo de detrás) y ACL
/// protegidas que se conservan. Todo en carpetas temporales.
/// </summary>
public sealed class RootMoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-raiz-" + Guid.NewGuid().ToString("N"));

    public RootMoverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { RootMover.DeleteTree(_dir); } catch { /* best effort */ }
    }

    private string P(string relative) => Path.Combine(_dir, relative);

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // [paths] root

    /// <summary>Una raíz completa de este sistema (<c>D:\roost</c> o <c>/srv/roost</c>): el TOML la lleva con comillas simples, sin escapar.</summary>
    private static string Full(string windows, string posix) => OperatingSystem.IsWindows() ? windows : posix;

    [Fact]
    public void ReadRoot_LaLee_YNuncaLanza()
    {
        var toml = P("roost.toml");
        var root = Full(@"D:\roost", "/srv/roost");
        Assert.Null(RoostConfig.ReadRoot(toml));   // no existe
        Write(toml, $"[paths]\nroot = '{root}'\n");
        Assert.Equal(Path.GetFullPath(root), RoostConfig.ReadRoot(toml));
        Write(toml, "[paths]\nroot = 'relativa'\n");
        Assert.Null(RoostConfig.ReadRoot(toml));   // solo rutas completas
        Write(toml, $"[paths\nroot = '{root}'\n");
        Assert.Null(RoostConfig.ReadRoot(toml));   // TOML roto: sin raíz (y sin lanzar)
        Write(toml, "[paths]\nroot = 3\n");
        Assert.Null(RoostConfig.ReadRoot(toml));
    }

    [Fact]
    public void SetRoot_LaPoneYLaQuita_SinTocarElResto()
    {
        var toml = P("roost.toml");
        var root = Full(@"E:\dev\roost", "/srv/dev/roost");
        Write(toml, "# mio\n[php]\nversion = \"8.4\"\n");
        RoostConfig.SetRoot(toml, root);
        Assert.Equal(root, RoostConfig.ReadRoot(toml));
        Assert.Equal("8.4", RoostConfig.Load(toml).PhpVersion);
        Assert.StartsWith("# mio", File.ReadAllText(toml));
        RoostConfig.SetRoot(toml, null);
        Assert.Null(RoostConfig.ReadRoot(toml));

        // Sin fichero: la plantilla, que ya cuenta [paths] comentado.
        var fresh = P(Path.Combine("nuevo", "roost.toml"));
        var other = Full(@"E:\roost", "/srv/roost");
        RoostConfig.SetRoot(fresh, other);
        Assert.Contains("[paths]", File.ReadAllText(fresh));
        Assert.Equal(other, RoostConfig.ReadRoot(fresh));
    }

    [Fact]
    public void ConRoostHome_LaRaizPuedeIrFuera_YElTomlYLaCacheSeQuedan()
    {
        var home = P("casa");
        Directory.CreateDirectory(home);
        string? Env(string name) => name == RoostPaths.HomeVariable ? home : null;

        var plain = RoostPaths.Default(Env);
        Assert.Equal(home, plain.Root);
        Assert.Equal(Path.Combine(home, "roost.toml"), plain.ConfigFile);

        RoostConfig.SetRoot(Path.Combine(home, "roost.toml"), P("raiz-b"));
        var moved = RoostPaths.Default(Env);
        Assert.Equal(P("raiz-b"), moved.Root);
        Assert.Equal(Path.Combine(home, "roost.toml"), moved.ConfigFile);   // el toml no se mueve
        Assert.Equal(Path.Combine(home, "cache"), moved.Cache);
        Assert.Equal(Path.Combine(P("raiz-b"), "www"), moved.Www);

        // Apuntar a la misma casa es como no apuntar.
        RoostConfig.SetRoot(Path.Combine(home, "roost.toml"), home);
        Assert.Null(RoostPaths.Default(Env).ConfigDir);
    }

    // Mover

    [Fact]
    public void Copy_CopiaComprueba_YDejaElOrigenIntacto()
    {
        var from = P("raiz-a");
        Write(Path.Combine(from, "bin", "php", "8.4.27-nts-x64", OsNames.Exe("php")), "MZ php");
        Write(Path.Combine(from, "data", "mariadb", "11.4", "ibdata1"), new string('x', 200_000));
        Write(Path.Combine(from, "www", "tienda", "index.php"), "<?php echo 1;");
        Directory.CreateDirectory(Path.Combine(from, "www", "vacia"));
        Write(Path.Combine(from, "roost.toml"), "[php]\n");   // como con ROOST_HOME: no se copia
        Write(Path.Combine(from, "cache", "downloads", "php.zip"), "zip");
        var to = P(Path.Combine("otro", "raiz-b"));

        var progress = new List<RootMoveProgress>();
        var size = RootMover.Measure(from, ["roost.toml", "cache"]);
        Assert.Equal(3, size.Files);
        RootMover.Copy(from, to, ["roost.toml", "cache"], new SyncProgress<RootMoveProgress>(progress.Add));

        Assert.Equal("<?php echo 1;", File.ReadAllText(Path.Combine(to, "www", "tienda", "index.php")));
        Assert.Equal(200_000, new FileInfo(Path.Combine(to, "data", "mariadb", "11.4", "ibdata1")).Length);
        Assert.True(Directory.Exists(Path.Combine(to, "www", "vacia")));
        Assert.False(File.Exists(Path.Combine(to, "roost.toml")));
        Assert.False(Directory.Exists(Path.Combine(to, "cache")));
        Assert.True(File.Exists(Path.Combine(from, "www", "tienda", "index.php")));   // el origen sigue
        Assert.Empty(Directory.GetDirectories(P("otro"), ".*"));                     // sin carpeta de trabajo
        Assert.Contains(progress, p => p.Step == RootMoveStep.Verifying);
        Assert.Equal(RootMoveStep.Placing, progress[^1].Step);
    }

    [Fact]
    public void Copy_SiElDestinoTieneCosas_NoHaceNada()
    {
        Write(P(Path.Combine("a", "www", "x.txt")), "1");
        Write(P(Path.Combine("b", "algo.txt")), "2");
        var ex = Assert.Throws<RoostException>(() => RootMover.Copy(P("a"), P("b"), []));
        Assert.Contains("tiene que estar vacia", ex.Message);
        Assert.Equal(["algo.txt"], Directory.GetFileSystemEntries(P("b")).Select(Path.GetFileName));
    }

    [Fact]
    public void Copy_UnFicheroQueNoSePuedeLeer_DejaTodoComoEstaba()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write(P(@"a\bin\uno.txt"), "1");
        Write(P(@"a\data\abierto.ibd"), "en uso");
        using var locked = new FileStream(P(@"a\data\abierto.ibd"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var ex = Assert.Throws<RoostException>(() => RootMover.Copy(P("a"), P("b"), []));
        Assert.Contains("abierto.ibd", ex.Message);
        Assert.False(Directory.Exists(P("b")));
        Assert.Empty(Directory.GetDirectories(_dir, ".b.tmp-*"));
        Assert.True(File.Exists(P(@"a\bin\uno.txt")));
    }

    [Fact]
    public void Copy_LosEnlacesSeRecrean_YLosDeDentroApuntanALaNueva()
    {
        if (!OperatingSystem.IsWindows()) return;
        var from = P("a");
        Write(Path.Combine(from, "bin", "php", "8.4", "php.exe"), "php");
        Write(P(@"fuera\proyecto\index.php"), "fuera");
        Directory.CreateDirectory(Path.Combine(from, "www"));
        RootMover.CreateJunction(Path.Combine(from, "www", "proyecto"), P(@"fuera\proyecto"));   // un proyecto en otro sitio
        RootMover.CreateJunction(Path.Combine(from, "www", "php-actual"), Path.Combine(from, "bin", "php", "8.4"));   // dentro de la raíz
        var to = P("b");

        var size = RootMover.Measure(from, []);
        Assert.Equal(2, size.Links.Count);
        Assert.Equal(1, size.Files);   // lo de detrás de los enlaces no cuenta
        RootMover.Copy(from, to, []);

        var outside = new DirectoryInfo(Path.Combine(to, "www", "proyecto"));
        Assert.Equal(P(@"fuera\proyecto"), outside.LinkTarget);
        Assert.Equal("fuera", File.ReadAllText(Path.Combine(to, "www", "proyecto", "index.php")));
        Assert.Equal(Path.Combine(to, "bin", "php", "8.4"), new DirectoryInfo(Path.Combine(to, "www", "php-actual")).LinkTarget);

        // Borrar la raíz vieja sin seguir enlaces: el proyecto de fuera sigue ahí.
        RootMover.DeleteTree(from);
        Assert.False(Directory.Exists(from));
        Assert.Equal("fuera", File.ReadAllText(P(@"fuera\proyecto\index.php")));
    }

    [Fact]
    public void Copy_LasClavesPrivadasSiguenSoloParaElUsuario()
    {
        if (!OperatingSystem.IsWindows()) return;
        var from = P("a");
        Write(Path.Combine(from, "certs", "ca", "roost-ca.key"), "clave");
        PrivateFile.Restrict(Path.Combine(from, "certs", "ca", "roost-ca.key"));
        PrivateFile.EnsureDirectory(Path.Combine(from, "state", "git", "keys", "tienda"));
        Write(Path.Combine(from, "state", "git", "keys", "tienda", "id_ed25519"), "ssh");

        RootMover.Copy(from, P("b"), []);
        Assert.True(PrivateFile.IsRestrictedToCurrentUser(P(@"b\certs\ca\roost-ca.key")));
        Assert.True(new DirectoryInfo(P(@"b\state\git\keys\tienda")).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void Retire_LaRenombra_YSiEstaEnUsoLaDeja()
    {
        Write(P(Path.Combine("a", "logs", "roostd.log")), "x");
        var retired = RootMover.Retire(P("a"), new DateTime(2026, 10, 24, 9, 5, 7));
        Assert.Equal(P("a.antigua-20261024-090507"), retired);
        Assert.False(Directory.Exists(P("a")));

        // En Linux renombrar una carpeta con un fichero abierto dentro es normal: solo Windows lo impide.
        if (!OperatingSystem.IsWindows()) return;
        Write(P(@"c\logs\roostd.log"), "x");
        using (new FileStream(P(@"c\logs\roostd.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Null(RootMover.Retire(P("c"), DateTime.Now));
        Assert.True(File.Exists(P(@"c\logs\roostd.log")));
    }

    [Fact]
    public void Rebase_LosSitiosDeLaWwwYElGitDeDentro()
    {
        var to = P("b");
        var paths = new RoostPaths(to, P("cache"), ConfigDir: P("config"));
        var registry = new SiteRegistry(paths);
        registry.Upsert(new Site { Name = "tienda", Folder = Path.Combine(P("a"), "www", "tienda") });
        registry.Upsert(new Site { Name = "legacy", Folder = P(@"proyectos\legacy") });
        RoostConfig.SetGitExe(paths.ConfigFile, Path.Combine(P("a"), "bin", "git", "cmd", "git.exe"));

        Assert.Equal(1, RootMover.Rebase(paths, P("a"), to));
        Assert.Equal(Path.Combine(to, "www", "tienda"), registry.Find("tienda")!.Folder);
        Assert.Equal(P(@"proyectos\legacy"), registry.Find("legacy")!.Folder);   // fuera: no se toca
        Assert.Equal(Path.Combine(to, "bin", "git", "cmd", "git.exe"), RoostConfig.Load(paths.ConfigFile).GitExe);
        Assert.Equal(P(@"a2\x"), RootMover.Retarget(P(@"a2\x"), P("a"), to));   // «a2» no está dentro de «a»
        Assert.Equal(to, RootMover.Retarget(P("a"), P("a"), to));
        Assert.Equal(@"..\relativa", RootMover.Retarget(@"..\relativa", P("a"), to));
    }

    // La app en Linux (L2): enlaces simbólicos y modos 0600/0700 en vez de uniones y ACL.

    [LinuxFact]
    public void Copy_Linux_LosEnlacesSimbolicosSeRecrean_YLosDeDentroApuntanALaNueva()
    {
        var from = P("a");
        Write(Path.Combine(from, "bin", "php", "8.4", "bin", "php"), "php");
        Write(P("fuera/proyecto/index.php"), "fuera");
        Directory.CreateDirectory(Path.Combine(from, "www"));
        Directory.CreateSymbolicLink(Path.Combine(from, "www", "proyecto"), P("fuera/proyecto"));             // un proyecto en otro sitio (absoluto)
        Directory.CreateSymbolicLink(Path.Combine(from, "www", "php-actual"), Path.Combine(from, "bin", "php", "8.4"));   // dentro de la raíz
        File.CreateSymbolicLink(Path.Combine(from, "bin", "php", "actual"), "8.4/bin/php");               // relativo: se deja tal cual
        var to = P("b");

        var size = RootMover.Measure(from, []);
        Assert.Equal(3, size.Links.Count);
        Assert.Equal(1, size.Files);   // lo de detrás de los enlaces no cuenta
        RootMover.Copy(from, to, []);

        Assert.Equal(P("fuera/proyecto"), new DirectoryInfo(Path.Combine(to, "www", "proyecto")).LinkTarget);
        Assert.Equal("fuera", File.ReadAllText(Path.Combine(to, "www", "proyecto", "index.php")));
        Assert.Equal(Path.Combine(to, "bin", "php", "8.4"), new DirectoryInfo(Path.Combine(to, "www", "php-actual")).LinkTarget);
        Assert.Equal("8.4/bin/php", new FileInfo(Path.Combine(to, "bin", "php", "actual")).LinkTarget);
        Assert.Equal("php", File.ReadAllText(Path.Combine(to, "bin", "php", "actual")));

        // Borrar la raíz vieja sin seguir enlaces: el proyecto de fuera sigue ahí.
        RootMover.DeleteTree(from);
        Assert.False(Directory.Exists(from));
        Assert.Equal("fuera", File.ReadAllText(P("fuera/proyecto/index.php")));
    }

    [LinuxFact]
    public void Copy_Linux_LosModos0600Y0700SeConservan()
    {
        var from = P("a");
        Directory.CreateDirectory(Path.Combine(from, "certs", "ca"));
        PrivateFile.Write(Path.Combine(from, "certs", "ca", "roost-ca.key"), "clave");
        PrivateFile.EnsureDirectory(Path.Combine(from, "state", "git", "keys", "tienda"));
        PrivateFile.Write(Path.Combine(from, "state", "git", "keys", "tienda", "id_ed25519"), "ssh");
        Write(Path.Combine(from, "www", "index.php"), "normal");
        File.SetUnixFileMode(Path.Combine(from, "www", "index.php"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        RootMover.Copy(from, P("b"), []);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(P("b/certs/ca/roost-ca.key")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(P("b/state/git/keys/tienda/id_ed25519")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(P("b/state/git/keys/tienda")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(P("b/www/index.php")));
    }

    /// <summary>Un IProgress que llama en el acto (el de .NET va por el contexto de sincronización).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
