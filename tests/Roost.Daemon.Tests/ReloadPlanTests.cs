using Roost.Core.Services;
using Roost.Daemon;
using Roost.Core.Platform;

namespace Roost.Daemon.Tests;

/// <summary>
/// La comparación pura de la recarga selectiva (<see cref="ReloadPlan"/>): con definiciones hechas a
/// mano y huellas inventadas, qué se arranca, qué se reinicia, qué se recarga y qué no se toca.
/// </summary>
public class ReloadPlanTests
{
    private static ServiceDefinition Db(int port = 3407) => new()
    {
        Name = "mariadb-11.4",
        Executable = OsNames.Exe("mariadbd"),
        Arguments = "--defaults-file=\"my.ini\" --console",
        Port = port,
        StopCommand = new ServiceDefinition { Name = "mariadb-11.4-shutdown", Executable = OsNames.Exe("mariadb-admin") },
    };

    private static ServiceDefinition Php(string branch, int i, string install = "8.4.27") => new()
    {
        Name = $"php-{branch}-{i}",
        Executable = $"{install}\\php-cgi.exe",
        Arguments = $"-b 127.0.0.1:{9000 + i}",
        Environment = new Dictionary<string, string> { ["PHP_FCGI_MAX_REQUESTS"] = "500" },
        Port = 9000 + i,
    };

    private static ServiceDefinition Web(string name = "nginx", int port = 8765) => new() { Name = name, Executable = name + ".exe", Port = port };

    private static List<ServiceDefinition> Stack(params ServiceDefinition[] services) => [.. services];

    private static Dictionary<string, string> Inputs(IEnumerable<ServiceDefinition> services, params (string Name, string Hash)[] changed)
    {
        var inputs = services.ToDictionary(s => s.Name, _ => "igual");
        foreach (var (name, hash) in changed) inputs[name] = hash;
        return inputs;
    }

    private static ReloadPlan Compute(List<ServiceDefinition> running, List<ServiceDefinition> next, params (string Name, string Hash)[] changed) =>
        ReloadPlan.Compute(running, running.Last().Name, Inputs(running), next, next.Last().Name, Inputs(next, changed));

    [Fact]
    public void ElMismoPlan_NoHayNadaQueHacer()
    {
        // Con definiciones nuevas (otro diccionario de entorno) pero lo mismo dentro.
        var plan = Compute(Stack(Db(), Php("8.4", 0), Php("8.4", 1), Web()), Stack(Db(), Php("8.4", 0), Php("8.4", 1), Web()));

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Describe());
        Assert.Equal(["mariadb-11.4", "nginx", "php-8.4-0", "php-8.4-1"], plan.Unchanged.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnSitioNuevo_SoloRecargaElServidorWeb()
    {
        var running = Stack(Db(), Php("8.4", 0), Web());

        var plan = Compute(running, Stack(Db(), Php("8.4", 0), Web()), ("nginx", "con el vhost de blog"));

        Assert.Equal(WebChange.Reload, plan.Web);
        Assert.Equal(["recargado nginx"], plan.Describe());
        Assert.Contains("mariadb-11.4", plan.Unchanged);
    }

    [Fact]
    public void OtroPuertoParaLaBaseDeDatos_LaParaYLaArranca_ConCadaDefinicion()
    {
        var plan = Compute(Stack(Db(3407), Php("8.4", 0), Web()), Stack(Db(3408), Php("8.4", 0), Web()));

        Assert.Equal(3407, Assert.Single(plan.Stop).Port);    // se para la que corre
        Assert.Equal(3408, Assert.Single(plan.Start).Port);   // y arranca la nueva
        Assert.Equal(WebChange.None, plan.Web);
        Assert.Equal(["reiniciado mariadb-11.4"], plan.Describe());
    }

    [Fact]
    public void UnPhpNuevoParaUnSitio_SeArrancaSuPool_YSeRecargaElServidorWeb()
    {
        var plan = Compute(
            Stack(Db(), Php("8.4", 0), Php("8.4", 1), Web()),
            Stack(Db(), Php("8.4", 0), Php("8.4", 1), Php("7.4", 0, "7.4.33"), Php("7.4", 1, "7.4.33"), Web()),
            ("nginx", "con el upstream de la 7.4"));

        Assert.Equal(["php-7.4-0", "php-7.4-1"], plan.AddPhp.Select(s => s.Name));
        Assert.Equal(["recargado nginx", "arrancado php-7.4"], plan.Describe());
    }

    [Fact]
    public void OtroPhpGlobal_ArrancaElNuevo_YQuitaElViejo()
    {
        var plan = Compute(
            Stack(Db(), Php("8.4", 0), Php("8.4", 1), Web()),
            Stack(Db(), Php("8.3", 0, "8.3.36"), Php("8.3", 1, "8.3.36"), Web()),
            ("nginx", "con el upstream de la 8.3"));

        Assert.Equal(["php-8.3-0", "php-8.3-1"], plan.AddPhp.Select(s => s.Name));
        Assert.Equal(["php-8.4-0", "php-8.4-1"], plan.RemovePhp.Select(s => s.Name));
        Assert.Empty(plan.Stop);
        Assert.Equal(["recargado nginx", "arrancado php-8.3", "quitado php-8.4"], plan.Describe());
    }

    [Fact]
    public void UnPoolMasGrande_ArrancaSoloLoQueFalta()
    {
        var plan = Compute(
            Stack(Php("8.4", 0), Php("8.4", 1), Web()),
            Stack(Php("8.4", 0), Php("8.4", 1), Php("8.4", 2), Web()),
            ("nginx", "con tres"));

        Assert.Equal(["recargado nginx", "arrancado php-8.4-2"], plan.Describe());
        var smaller = Compute(Stack(Php("8.4", 0), Php("8.4", 1), Php("8.4", 2), Web()), Stack(Php("8.4", 0), Php("8.4", 1), Web()), ("nginx", "con dos"));
        Assert.Equal(["recargado nginx", "quitado php-8.4-2"], smaller.Describe());
    }

    [Fact]
    public void OtraVersionDeLaRama_OUnPhpIniCambiado_ReiniciaElPoolUnoAUno()
    {
        var running = Stack(Php("8.4", 0), Php("8.4", 1), Web());

        var upgraded = Compute(running, Stack(Php("8.4", 0, "8.4.28"), Php("8.4", 1, "8.4.28"), Web()));
        var ini = Compute(running, Stack(Php("8.4", 0), Php("8.4", 1), Web()), ("php-8.4-0", "memory_limit"), ("php-8.4-1", "memory_limit"));

        Assert.Equal([true, true], upgraded.RestartPhp.Select(r => r.Replace));    // con su definición nueva
        Assert.Equal([false, false], ini.RestartPhp.Select(r => r.Replace));       // la misma, que lea lo nuevo
        Assert.Equal(["reiniciado php-8.4 (uno a uno)"], ini.Describe());
        Assert.Equal(WebChange.None, ini.Web);
    }

    [Fact]
    public void OtroServidorWeb_OOtroPuerto()
    {
        var switched = Compute(Stack(Php("8.4", 0), Web("nginx")), Stack(Php("8.4", 0), Web("apache")));
        var port = Compute(Stack(Php("8.4", 0), Web("nginx", 8765)), Stack(Php("8.4", 0), Web("nginx", 8766)));

        Assert.Equal(WebChange.Switch, switched.Web);
        Assert.Equal(["cambiado nginx por apache"], switched.Describe());
        Assert.Empty(switched.Stop);   // el servidor web va aparte, no con la base de datos
        Assert.Equal(WebChange.Restart, port.Web);
        Assert.Equal(["reiniciado nginx"], port.Describe());
    }

    [Fact]
    public void RedisQueSeVaYMailpitQueLlega()
    {
        var redis = new ServiceDefinition { Name = "redis", Executable = OsNames.Exe("redis-server"), Port = 6379 };
        var mailpit = new ServiceDefinition { Name = "mailpit", Executable = OsNames.Exe("mailpit"), Port = 1025 };

        var plan = Compute(Stack(Db(), redis, Php("8.4", 0), Web()), Stack(Db(), mailpit, Php("8.4", 0), Web()));

        Assert.Equal(["quitado redis", "arrancado mailpit"], plan.Describe());
    }

    [Fact]
    public void ServerSections_QuitaElClientDelMyIni_PeroNoLoDelServidorNiElInclude()
    {
        var withPassword = "[mysqld]\nport = 3407\n\n[client]\nport = 3407\nuser = root\npassword = \"secreta\"\n\n!include C:/roost/etc/mariadb/11.4/usuario.cnf\n";
        var without = withPassword.Replace("password = \"secreta\"\n", "", StringComparison.Ordinal);

        Assert.Equal(ServiceInputs.ServerSections(without), ServiceInputs.ServerSections(withPassword));
        Assert.Contains("!include C:/roost/etc/mariadb/11.4/usuario.cnf", ServiceInputs.ServerSections(withPassword));
        Assert.NotEqual(ServiceInputs.ServerSections(withPassword), ServiceInputs.ServerSections(withPassword.Replace("port = 3407\n\n[client]", "port = 3408\n\n[client]", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("php-8.4-0", true, "php-8.4")]
    [InlineData("php-10.1-12", true, "php-10.1")]
    [InlineData("php-8.4", false, "php-8.4")]
    [InlineData("mariadb-11.4", false, "mariadb-11.4")]
    [InlineData("php-extension-0", false, "php-extension-0")]
    public void IsPhpMember_YPool(string name, bool member, string pool)
    {
        Assert.Equal(member, ReloadPlan.IsPhpMember(name));
        Assert.Equal(pool, ReloadPlan.Pool(name));
    }
}
