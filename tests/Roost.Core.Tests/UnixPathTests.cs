using Roost.Core.Platform;
using Roost.Core.Shims;

namespace Roost.Core.Tests;

/// <summary>
/// Los shims en Linux: los enlaces de <c>~/.local/bin</c> a <c>bin/shims</c> (sobre una HOME temporal, nunca
/// la del usuario) y el bloque del perfil de la shell (puro). Lo que crea enlaces simbólicos solo va en
/// Linux; lo del texto, en cualquier sistema.
/// </summary>
public class UnixPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-unixpath-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");
    private RoostPaths Paths => new(Path.Combine(_root, "roost"), Path.Combine(_root, "cache"));

    private UnixPath Links(Func<string, string?>? env = null) => new(Home, env ?? (_ => null));

    [Fact]
    public void Bloque_SeAnadeUnaVez_SeSustituye_YSeQuitaSinTocarElResto()
    {
        var profile = "# ~/.profile\nif [ -d \"$HOME/bin\" ] ; then\n    PATH=\"$HOME/bin:$PATH\"\nfi\n";

        var once = UnixPath.WithBlock(profile, UnixPath.ShellBlock);
        Assert.StartsWith(profile, once);
        Assert.Contains(UnixPath.BeginMarker, once);
        Assert.Contains("PATH=\"$HOME/.local/bin:$PATH\"", once);
        Assert.EndsWith(UnixPath.EndMarker + "\n", once);
        Assert.Equal(1, Count(once, UnixPath.BeginMarker));

        // Otra vez: el mismo bloque, no dos.
        var twice = UnixPath.WithBlock(once, UnixPath.ShellBlock);
        Assert.Equal(once, twice);

        // Si el bloque cambia de texto, se sustituye el que había.
        var changed = UnixPath.WithBlock(once.Replace("$HOME/.local/bin", "/otro"), UnixPath.ShellBlock);
        Assert.Equal(once, changed);

        // Quitarlo deja el perfil como estaba, byte a byte.
        Assert.Equal(profile, UnixPath.WithoutBlock(once));
        Assert.Equal(profile, UnixPath.WithoutBlock(profile));   // sin bloque, tal cual
        Assert.False(UnixPath.HasBlock(profile));
    }

    [Fact]
    public void Bloque_EnUnPerfilVacio_YUnoAbiertoSinCierreNoSeToca()
    {
        var alone = UnixPath.WithBlock("", UnixPath.ShellBlock);
        Assert.Equal(UnixPath.ShellBlock + "\n", alone);
        Assert.Equal("", UnixPath.WithoutBlock(alone));

        var broken = "x\n" + UnixPath.BeginMarker + "\nPATH=lo-que-sea\n";
        Assert.Equal(broken, UnixPath.WithoutBlock(broken));
    }

    [Fact]
    public void ElBloqueDeFish_UsaFishAddPath_YElDeShellLaGuardaDelCase()
    {
        Assert.Contains("fish_add_path --global --prepend --move $HOME/.local/bin", UnixPath.FishBlock);
        Assert.Contains("case \":$PATH:\" in *\":$HOME/.local/bin:\"*) ;;", UnixPath.ShellBlock);
        Assert.DoesNotContain("\r", UnixPath.ShellBlock);
    }

    [Theory]
    [InlineData("/home/ana/.local/bin:/usr/bin", true, 0)]
    [InlineData("/usr/bin:/home/ana/.local/bin/", true, 1)]
    [InlineData("/usr/bin:/bin", false, -1)]
    [InlineData("", false, -1)]
    [InlineData(null, false, -1)]
    public void Contains_EIndexOf_EnUnPathDeUnix(string? path, bool contains, int index)
    {
        Assert.Equal(contains, UnixPath.Contains(path, "/home/ana/.local/bin"));
        Assert.Equal(index, UnixPath.IndexOf(path, "/home/ana/.local/bin"));
    }

    [Fact]
    public void PerfilesQueTocan_BashProfileSiExiste_ZshSiEsLaShell_FishSiTieneCarpeta()
    {
        Directory.CreateDirectory(Home);
        var links = Links(name => name == "SHELL" ? "/usr/bin/bash" : null);
        Assert.Equal([(Path.Combine(Home, ".profile"), UnixPath.ShellBlock)], links.ProfileFiles());

        File.WriteAllText(Path.Combine(Home, ".bash_profile"), "");
        Assert.Equal(Path.Combine(Home, ".bash_profile"), links.ProfileFiles().Single().File);

        var zsh = Links(name => name == "SHELL" ? "/bin/zsh" : null);
        Assert.Contains(zsh.ProfileFiles(), f => f.File == Path.Combine(Home, ".zprofile"));

        Directory.CreateDirectory(Path.Combine(Home, ".config", "fish"));
        var fish = Links(name => name == "SHELL" ? "/usr/bin/bash" : null).ProfileFiles();
        Assert.Contains(fish, f => f.File == Path.Combine(Home, ".config", "fish", "conf.d", UnixPath.FishFileName) && f.Block == UnixPath.FishBlock);
    }

    [Fact]
    public void InstallProfile_EscribeElBloque_YRemoveProfileLoQuita_TambienElFicheroDeFish()
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(Home, ".profile"), "# mio\n");
        Directory.CreateDirectory(Path.Combine(Home, ".config", "fish"));
        var links = Links(name => name == "SHELL" ? "/usr/bin/fish" : null);

        var touched = links.InstallProfile();

        Assert.Equal(2, touched.Count);
        Assert.Equal("# mio\n\n" + UnixPath.ShellBlock + "\n", File.ReadAllText(Path.Combine(Home, ".profile")));
        Assert.True(File.Exists(links.FishFile));
        Assert.Empty(links.InstallProfile());   // idempotente
        Assert.Equal(2, links.ProfilesWithBlock().Count);

        var removed = links.RemoveProfile();

        Assert.Equal(2, removed.Count);
        Assert.Equal("# mio\n", File.ReadAllText(Path.Combine(Home, ".profile")));
        Assert.False(File.Exists(links.FishFile));
        Assert.Empty(links.ProfilesWithBlock());
    }

    [LinuxFact]
    public void Link_EnlazaLosShims_RespetaLoDelUsuario_YUnlinkSoloQuitaLosDeRoost()
    {
        var shims = Path.Combine(Paths.Shims);
        Directory.CreateDirectory(shims);
        File.WriteAllText(Path.Combine(shims, "roost-shim"), "shim");
        foreach (var tool in new[] { "php", "composer", "node" }) File.CreateSymbolicLink(Path.Combine(shims, tool), "roost-shim");
        var links = Links();
        Directory.CreateDirectory(links.LinkDirectory);
        File.WriteAllText(links.LinkPath("node"), "#!/bin/sh\necho mi node\n");   // uno del usuario

        var (linked, skipped) = links.Link(["php", "composer", "node"], shims);

        Assert.Equal(["php", "composer"], linked);
        Assert.Equal(["node"], skipped);
        Assert.Equal(Path.Combine(shims, "php"), new FileInfo(links.LinkPath("php")).LinkTarget);
        Assert.True(links.IsRoostLink("php", shims));
        Assert.False(links.IsRoostLink("node", shims));
        Assert.Equal(links.LinkPath("node"), links.Foreign("node", shims));
        Assert.Null(links.Foreign("php", shims));
        Assert.Equal(["php", "composer"], links.Linked(shims, ShimInstaller.Tools));

        // Otra vez: igual (se renuevan sin fallar).
        Assert.Equal(["php", "composer"], links.Link(["php", "composer"], shims).Linked);

        var removed = links.Unlink(shims, ShimInstaller.Tools);

        Assert.Equal(["php", "composer"], removed);
        Assert.False(File.Exists(links.LinkPath("php")));
        Assert.Equal("#!/bin/sh\necho mi node\n", File.ReadAllText(links.LinkPath("node")));   // lo del usuario se queda
    }

    [LinuxFact]
    public void ShimInstaller_EnLinux_UnaCopiaYEnlaces_YEnableDisableConLosDeLocalBin()
    {
        var source = Path.Combine(_root, "app");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "roost-shim"), "shim");
        var shims = new ShimInstaller(Paths) { Links = Links() };

        var activation = shims.Enable(source);

        Assert.True(shims.IsInstalled);
        Assert.True(shims.IsEnabled);
        Assert.Equal(["php"], activation.Tools);
        Assert.True(activation.PathChanged);
        Assert.Equal("roost-shim", new FileInfo(shims.ToolPath("php")).LinkTarget);   // relativo: la carpeta se puede mover
        Assert.Equal("shim", File.ReadAllText(shims.UnixShim));
        Assert.Equal(shims.ToolPath("php"), new FileInfo(shims.Links.LinkPath("php")).LinkTarget);
        Assert.Equal(shims.Links.LinkDirectory, shims.PathDirectory);
        Assert.False(shims.IsOutdated(source));

        // Un Node de Roost: AddMissing enlaza también en ~/.local/bin, porque php ya lo estaba.
        var node = new InstalledNodeFixture(Paths, "24.22.1");
        node.Create();
        Assert.Equal(["node", "npm", "npx"], shims.AddMissing(source));
        Assert.True(shims.Links.IsRoostLink("npm", shims.Directory));

        shims.Disable();

        Assert.False(shims.IsInstalled);
        Assert.False(shims.IsEnabled);
        Assert.False(File.Exists(shims.Links.LinkPath("php")));
        Assert.False(File.Exists(shims.Links.LinkPath("npm")));
    }

    [Fact]
    public void QuienGanaEnUnPathDeUnix_HastaLocalBin_YDeQuienEs()
    {
        var local = "/home/ana/.local/bin";
        bool Exists(string p) => p is "/home/ana/.nvm/versions/node/v22.11.0/bin/node" or "/usr/bin/php" or "/usr/bin/node" or "/snap/bin/node";

        // nvm (del ~/.bashrc) va antes que ~/.local/bin: gana.
        var nvm = ShimShadowing.FindUnix("node", local, $"/home/ana/.nvm/versions/node/v22.11.0/bin:{local}:/usr/bin", Exists);
        Assert.Equal("/home/ana/.nvm/versions/node/v22.11.0/bin/node", nvm?.Path);
        Assert.False(nvm!.InSystemPath);
        Assert.True(nvm.ShimsInPath);
        Assert.Equal("nvm", ShimShadowing.UnixOwner(nvm.Path));
        Assert.Equal("nvm", ShimShadowing.NodeOwner(nvm.Path));

        // ~/.local/bin el primero: nada gana.
        Assert.Null(ShimShadowing.FindUnix("php", local, $"{local}:/usr/bin:/bin", Exists));
        Assert.Null(ShimShadowing.FindUnix("node", local, $"{local}/:/usr/bin", Exists));   // con barra final

        // Sin ~/.local/bin en el PATH: gana el de la distro, y se dice que los shims no estan.
        var distro = ShimShadowing.FindUnix("php", local, "/usr/local/bin:/usr/bin:/bin", Exists);
        Assert.Equal("/usr/bin/php", distro?.Path);
        Assert.True(distro!.InSystemPath);
        Assert.False(distro.ShimsInPath);
        Assert.Equal("el paquete de la distro", ShimShadowing.UnixOwner(distro.Path));

        Assert.Equal("snap", ShimShadowing.UnixOwner("/snap/bin/node"));
        Assert.Equal("fnm", ShimShadowing.UnixOwner("/home/ana/.local/share/fnm/node-versions/v22/installation/bin/node"));
        Assert.Equal("Volta", ShimShadowing.UnixOwner("/home/ana/.volta/bin/node"));
        Assert.Equal("asdf", ShimShadowing.UnixOwner("/home/ana/.asdf/shims/node"));
        Assert.Equal("mise", ShimShadowing.UnixOwner("/home/ana/.local/share/mise/installs/node/22/bin/node"));
        Assert.Equal("Homebrew", ShimShadowing.UnixOwner("/home/linuxbrew/.linuxbrew/bin/php"));
        Assert.Equal("un programa de Windows, por el interop de WSL", ShimShadowing.UnixOwner("/mnt/c/Program Files/nodejs/node"));
        Assert.Equal("algo instalado a mano en /usr/local", ShimShadowing.UnixOwner("/usr/local/bin/php"));
        Assert.Null(ShimShadowing.UnixOwner("/home/ana/herramientas/php"));
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Un Node de Roost «instalado» con el layout de este sistema (sin ejecutar nada).</summary>
internal sealed class InstalledNodeFixture(RoostPaths paths, string version)
{
    public Roost.Core.Install.InstalledNode Node => new(version, Path.Combine(paths.NodeBin, version));

    public void Create()
    {
        var node = Node;
        Directory.CreateDirectory(Path.GetDirectoryName(node.Executable)!);
        File.WriteAllText(node.Executable, "");
        Directory.CreateDirectory(Path.GetDirectoryName(node.NpmCli)!);
        File.WriteAllText(node.NpmCli, "");
    }
}
