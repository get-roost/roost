using Roost.Core.Platform;
using Roost.Core.Shims;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Testea qué PHP elige el shim de la terminal en cada carpeta, el PATH del usuario (solo la
/// parte pura: nada toca el registro) y la copia de los shims. Todo sobre una raíz temporal.
/// </summary>
public class TerminalPhpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-shims-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"), WwwDir: Path.Combine(_root, "www"));

    private void Php(params string[] names)
    {
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(Paths.PhpBin, name));
            File.WriteAllText(Path.Combine(Paths.PhpBin, name, "php.exe"), "");
        }
    }

    private string Folder(params string[] parts)
    {
        var dir = Path.Combine([_root, "www", .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SinNadaMas_UsaElGlobalDeRoostToml()
    {
        Php("8.4.27-nts-x64", "8.3.36-nts-x64");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.ConfigFile, "[php]\nversion = \"8.3\"\n");

        var choice = TerminalPhp.Resolve(Paths, Folder("cualquiera"), null);

        Assert.Equal("8.3.36-nts-x64", choice.Php.Name);
        Assert.StartsWith("el PHP global (8.3", choice.Reason);
    }

    [Fact]
    public void SinGlobalFijado_ElMasNuevo_YPrefiereNts()
    {
        Php("8.4.27-ts-x64", "8.4.27-nts-x64", "7.4.33-nts-x64");

        Assert.Equal("8.4.27-nts-x64", TerminalPhp.Resolve(Paths, Folder("x"), null).Php.Name);
    }

    [Fact]
    public void PhpVersionEnUnaCarpetaDeArriba_GanaAlSitioYAlGlobal()
    {
        Php("8.4.27-nts-x64", "7.4.33-nts-x64");
        var project = Folder("legacy");
        File.WriteAllText(Path.Combine(project, ".php-version"), "# fijado para el CRM\nphp 7.4\n");
        new SiteRegistry(Paths).Upsert(new Site { Name = "legacy", Folder = project, Php = "8.4" });

        var choice = TerminalPhp.Resolve(Paths, Folder("legacy", "app", "Http"), null);

        Assert.Equal("7.4.33-nts-x64", choice.Php.Name);
        Assert.Contains(".php-version pide 7.4", choice.Reason);
    }

    [Fact]
    public void DentroDeUnSitio_UsaSuPhp_ElMasProfundoSiHayAnidados()
    {
        Php("8.4.27-nts-x64", "8.3.36-nts-x64", "7.4.33-nts-x64");
        var registry = new SiteRegistry(Paths);
        registry.Upsert(new Site { Name = "tienda", Folder = Folder("tienda"), Php = "8.3" });
        registry.Upsert(new Site { Name = "viejo", Folder = Folder("tienda", "viejo"), Php = "7.4" });
        registry.Upsert(new Site { Name = "blog", Folder = Folder("blog") });

        Assert.Equal("8.3.36-nts-x64", TerminalPhp.Resolve(Paths, Folder("tienda", "src"), null).Php.Name);
        Assert.Equal("7.4.33-nts-x64", TerminalPhp.Resolve(Paths, Folder("tienda", "viejo"), null).Php.Name);
        var blog = TerminalPhp.Resolve(Paths, Folder("blog"), null);
        Assert.Equal("8.4.27-nts-x64", blog.Php.Name);
        Assert.StartsWith("el sitio blog.test usa el PHP global", blog.Reason);
        // «tiendas» empieza igual que «tienda», pero no está dentro.
        Assert.Equal("8.4.27-nts-x64", TerminalPhp.Resolve(Paths, Folder("tiendas"), null).Php.Name);
    }

    [Fact]
    public void LaVariable_GanaATodo()
    {
        Php("8.4.27-nts-x64", "7.4.33-nts-x64");
        var project = Folder("legacy");
        File.WriteAllText(Path.Combine(project, ".php-version"), "7.4");

        var choice = TerminalPhp.Resolve(Paths, project, "8.4.27");

        Assert.Equal("8.4.27-nts-x64", choice.Php.Name);
        Assert.Equal("ROOST_PHP_VERSION=8.4.27", choice.Reason);
    }

    [Fact]
    public void LoPedidoQueNoEstaInstalado_EsUnError_NoOtroPhp()
    {
        Php("8.4.27-nts-x64");
        var project = Folder("legacy");
        File.WriteAllText(Path.Combine(project, ".php-version"), "7.4");

        var ex = Assert.Throws<RoostException>(() => TerminalPhp.Resolve(Paths, project, null));
        Assert.Contains("roost php install 7.4", ex.Message);
    }

    [Fact]
    public void SinNingunPhp_LoDiceConComoInstalarlo()
    {
        var ex = Assert.Throws<RoostException>(() => TerminalPhp.Resolve(Paths, Folder("x"), null));
        Assert.Contains("roost php install", ex.Message);
    }

    [Fact]
    public void Environment_GeneraElPhpIniSiFalta_YApuntaAEl()
    {
        Php("8.4.27-nts-x64");
        var php = new Install.PhpInstaller(Paths).ListInstalled()[0];

        var env = TerminalPhp.Environment(Paths, php);

        Assert.True(File.Exists(Path.Combine(env["PHPRC"], "php.ini")));
        // La conf.d común y la de su rama, como el pool de php-cgi.
        var confD = Path.Combine(Paths.Etc, "php", "conf.d");
        Assert.Equal(confD + Path.PathSeparator + Path.Combine(confD, "8.4"), env["PHP_INI_SCAN_DIR"]);
        Assert.True(File.Exists(Path.Combine(confD, "99-usuario.ini")));
        Assert.True(File.Exists(Path.Combine(confD, "8.4", "99-usuario.ini")));
    }

    [Theory]
    [InlineData("\"C:\\roost\\bin\\shims\\php.exe\" -r \"echo 'a b';\"", "-r \"echo 'a b';\"")]
    [InlineData("php artisan  migrate --seed", "artisan  migrate --seed")]
    [InlineData("php", "")]
    [InlineData("\"C:\\Program Files\\x\\php.exe\"", "")]
    public void ArgumentsAfterProgram_NoReentrecomilla(string commandLine, string expected)
    {
        Assert.Equal(expected, TerminalPhp.ArgumentsAfterProgram(commandLine));
    }

    [Fact]
    public void UserPath_PoneLaCarpetaLaPrimera_SinRepetirla_YLaQuita()
    {
        const string path = @"%USERPROFILE%\.dotnet\tools;C:\roost\bin\shims\;C:\Tools";

        var added = UserPath.WithEntryFirst(path, @"C:\roost\bin\shims");

        Assert.Equal(@"C:\roost\bin\shims;%USERPROFILE%\.dotnet\tools;C:\Tools", added);
        Assert.Equal(@"%USERPROFILE%\.dotnet\tools;C:\Tools", UserPath.WithoutEntry(added, @"c:\ROOST\bin\shims\"));
        Assert.True(UserPath.Contains(path, @"C:\roost\bin\shims"));
    }

    [Fact]
    public void ShimInstaller_CopiaElShimComoPhpConLoQueNecesitaAlLado()
    {
        var source = Path.Combine(_root, "app");
        Directory.CreateDirectory(source);
        foreach (var file in new[] { "roost-shim.exe", "roost-shim.dll", "roost-shim.runtimeconfig.json", "Roost.Core.dll", "Tomlyn.dll", "Otra.dll" })
            File.WriteAllText(Path.Combine(source, file), file);
        File.WriteAllText(Path.Combine(source, "roost-shim.deps.json"), """
            { "targets": { ".NETCoreApp,Version=v10.0": {
              "roost-shim/1.0.0": { "runtime": { "roost-shim.dll": {} } },
              "Tomlyn/2.10.1": { "runtime": { "lib/net8.0/Tomlyn.dll": {} } },
              "Roost.Core/1.0.0": { "runtime": { "Roost.Core.dll": {} } } } } }
            """);
        var shims = new ShimInstaller(Paths);

        shims.Install(source);
        shims.Install(source);   // repetirlo sustituye la carpeta entera

        Assert.True(shims.IsInstalled);
        Assert.Equal("roost-shim.exe", File.ReadAllText(Path.Combine(Paths.Shims, OperatingSystem.IsWindows() ? "php.exe" : "php")));
        Assert.True(File.Exists(Path.Combine(Paths.Shims, "Tomlyn.dll")));
        Assert.True(File.Exists(Path.Combine(Paths.Shims, "roost-shim.runtimeconfig.json")));
        Assert.False(File.Exists(Path.Combine(Paths.Shims, "Otra.dll")));   // no la pide el deps.json
        shims.Remove();
        Assert.False(shims.IsInstalled);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
