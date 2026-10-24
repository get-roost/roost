using Roost.Core.Config;

namespace Roost.Core.Tests;

/// <summary>Testea roost.toml: valores por defecto, errores claros y ediciones que respetan el fichero.</summary>
public class RoostConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigFile => Path.Combine(_dir, "roost.toml");

    private void Write(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigFile, text);
    }

    [Fact]
    public void Load_SinFichero_DaLosValoresPorDefecto()
    {
        var config = RoostConfig.Load(ConfigFile);

        Assert.Null(config.PhpVersion);
        Assert.Equal(RoostConfig.DefaultPhpPool, config.PhpPool);
        Assert.Equal(RoostConfig.DefaultHttpPort, config.HttpPort);
    }

    [Fact]
    public void Load_LeeVersionPoolYPuerto()
    {
        Write("[php]\nversion = \"8.3\"\npool = 2\n\n[web]\nport = 8080\n");

        var config = RoostConfig.Load(ConfigFile);

        Assert.Equal("8.3", config.PhpVersion);
        Assert.Equal(2, config.PhpPool);
        Assert.Equal(8080, config.HttpPort);
    }

    [Theory]
    [InlineData("[web]\nport = 70000\n")]   // fuera de rango
    [InlineData("[php]\npool = 0\n")]       // pool imposible
    [InlineData("[web]\nport = \"80\"\n")]  // tipo equivocado
    [InlineData("[php\nversion = 8.3\n")]   // TOML roto
    public void Load_ValorInvalido_FallaConMensajeQueNombraElFichero(string toml)
    {
        Write(toml);

        var ex = Assert.Throws<RoostException>(() => RoostConfig.Load(ConfigFile));

        Assert.Contains(ConfigFile, ex.Message);
    }

    [Fact]
    public void SetPhpVersion_SinFichero_CreaLaPlantillaConLaVersion()
    {
        RoostConfig.SetPhpVersion(ConfigFile, "8.4");

        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal("8.4", config.PhpVersion);
        Assert.Equal(RoostConfig.DefaultHttpPort, config.HttpPort);
        Assert.StartsWith("# Configuracion de Roost", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void SetPhpVersion_CambiaSoloEsaClave_YRespetaComentariosYElResto()
    {
        Write("# mi config\n[php]\n# la global\nversion = \"8.3\"\npool = 2\n\n[web]\n# puerto libre\nport = 8765\n");

        RoostConfig.SetPhpVersion(ConfigFile, "8.4");

        var text = File.ReadAllText(ConfigFile);
        Assert.Contains("# mi config", text);
        Assert.Contains("# la global", text);
        Assert.Contains("# puerto libre", text);
        Assert.Contains("version = \"8.4\"", text);
        Assert.DoesNotContain("8.3", text);

        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal(2, config.PhpPool);
        Assert.Equal(8765, config.HttpPort);
    }

    [Fact]
    public void SetPhpVersion_SinTablaPhp_LaAnade()
    {
        Write("[web]\nport = 8765\n");

        RoostConfig.SetPhpVersion(ConfigFile, "8.4");

        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal("8.4", config.PhpVersion);
        Assert.Equal(8765, config.HttpPort);
    }

    [Fact]
    public void SetDb_SinFichero_CreaLaPlantillaConMotorYVersion()
    {
        RoostConfig.SetDb(ConfigFile, "mysql", "8.4");

        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal("mysql", config.DbEngine);
        Assert.Equal("8.4", config.DbVersion);
        Assert.Equal(RoostConfig.DefaultDbPort, config.DbPort);
        Assert.Null(config.PhpVersion); // en la plantilla va comentada
    }

    [Fact]
    public void SetDb_ConNone_QuitaLaVersion_YRespetaElPuerto()
    {
        Write("[db]\nengine = \"mariadb\"\nversion = \"11.4\"\nport = 3407\n");

        RoostConfig.SetDb(ConfigFile, RoostConfig.NoDb, null);

        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal(RoostConfig.NoDb, config.DbEngine);
        Assert.Null(config.DbVersion);
        Assert.Equal(3407, config.DbPort);
    }

    [Fact]
    public void SetWebPorts_CambiaLosPuertos_YRespetaComentariosYElResto()
    {
        Write("# mis puertos\n[php]\nversion = \"8.3\"\n\n[web]\nport = 80 # el de siempre\n");

        RoostConfig.SetWebPorts(ConfigFile, 8080, 8443);

        var text = File.ReadAllText(ConfigFile);
        Assert.Contains("# mis puertos", text);
        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal(8080, config.HttpPort);
        Assert.Equal(8443, config.HttpsPort);
        Assert.Equal("8.3", config.PhpVersion);
    }

    [Fact]
    public void GitExe_SeLeeDeGitExe_YSetGitExeLoPoneYLoQuita()
    {
        Assert.Null(RoostConfig.Load(ConfigFile).GitExe);
        Write("[git]\nexe = 'C:\\Program Files\\Git\\cmd\\git.exe'\n");
        Assert.Equal(@"C:\Program Files\Git\cmd\git.exe", RoostConfig.Load(ConfigFile).GitExe);

        Write("[git]\nexe = \"\"\n");
        Assert.Null(RoostConfig.Load(ConfigFile).GitExe);   // vacio = el de Roost

        RoostConfig.SetGitExe(ConfigFile, @"D:\herramientas\git.exe");
        Assert.Equal(@"D:\herramientas\git.exe", RoostConfig.Load(ConfigFile).GitExe);
        RoostConfig.SetGitExe(ConfigFile, null);
        Assert.Null(RoostConfig.Load(ConfigFile).GitExe);
    }

    [Fact]
    public void SetDbPortYPool_SinFichero_ParteDeLaPlantilla()
    {
        RoostConfig.SetDbPort(ConfigFile, 3407);
        RoostConfig.SetPhpPool(ConfigFile, 2);

        var config = RoostConfig.Load(ConfigFile);
        Assert.Equal(3407, config.DbPort);
        Assert.Equal(2, config.PhpPool);
        Assert.Equal(RoostConfig.DefaultHttpPort, config.HttpPort);
        Assert.Contains("# Configuracion de Roost", File.ReadAllText(ConfigFile));
    }

    [Theory]
    [InlineData(8080, 8080)]
    [InlineData(0, 443)]
    [InlineData(80, 70000)]
    public void SetWebPorts_Invalidos_FallanSinTocarElFichero(int http, int https)
    {
        Write("[web]\nport = 80\n");

        Assert.Throws<RoostException>(() => RoostConfig.SetWebPorts(ConfigFile, http, https));
        Assert.Equal("[web]\nport = 80\n", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void Load_MotorDesconocido_Falla()
    {
        Write("[db]\nengine = \"postgres\"\n");

        Assert.Throws<RoostException>(() => RoostConfig.Load(ConfigFile));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
