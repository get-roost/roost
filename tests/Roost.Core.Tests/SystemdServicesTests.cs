using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Tests;

/// <summary>Los servicios de systemd que le quitan puertos a Roost en Linux, con la salida de systemctl grabada (sin systemd real).</summary>
public class SystemdServicesTests
{
    // systemctl list-units --type=service --all --plain --no-legend --no-pager, en una Ubuntu 24.04 con apache2 y mysql de apt.
    private const string ListUnits = """
        apache2.service                        loaded    active   running The Apache HTTP Server
        mysql.service                          loaded    inactive dead    MySQL Community Server
        nginx.service                          not-found inactive dead    nginx.service
        php8.3-fpm.service                     loaded    active   running The PHP 8.3 FastCGI Process Manager
        redis-server.service                   loaded    active   running Advanced key-value store
        ssh.service                            loaded    active   running OpenBSD Secure Shell server
        systemd-resolved.service               loaded    active   running Network Name Resolution

        """;

    private sealed class FakeRunner(Func<string, string?> isEnabled) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add($"{executablePath} {arguments}");
            if (executablePath != "systemctl") return null;
            if (arguments.StartsWith("list-units", StringComparison.Ordinal)) return ListUnits;
            if (arguments.StartsWith("is-enabled ", StringComparison.Ordinal)) return isEnabled(arguments["is-enabled ".Length..]);
            return null;
        }
    }

    [Fact]
    public void ParseListUnits_LeeNombreEstadoYDescripcion_YSaltaLoQueNoEsUnaUnidad()
    {
        var units = SystemdServices.ParseListUnits(ListUnits + "System has not been booted with systemd as init system (PID 1). Can't operate.\n");

        Assert.Equal(7, units.Count);
        Assert.Equal("apache2.service", units[0].Name);
        Assert.True(units[0].Loaded);
        Assert.True(units[0].Running);
        Assert.Equal("The Apache HTTP Server", units[0].Description);
        Assert.False(units[1].Running);
        Assert.False(units[2].Loaded);   // not-found
    }

    [Theory]
    [InlineData("apache2.service", "Apache")]
    [InlineData("httpd", "Apache")]
    [InlineData("nginx.service", "nginx")]
    [InlineData("mysql.service", "MySQL")]
    [InlineData("mariadb.service", "MariaDB")]
    [InlineData("redis-server.service", "Redis")]
    [InlineData("php8.3-fpm.service", "PHP-FPM")]
    [InlineData("php-fpm.service", "PHP-FPM")]
    [InlineData("ssh.service", null)]
    [InlineData("systemd-resolved.service", null)]
    public void Classify_PorElNombreDeLaUnidad(string unit, string? kind) => Assert.Equal(kind, SystemdServices.Classify(unit));

    [Theory]
    [InlineData("enabled\n", ServiceStartMode.Automatic)]
    [InlineData("disabled", ServiceStartMode.Manual)]
    [InlineData("masked", ServiceStartMode.Disabled)]
    [InlineData("static", ServiceStartMode.Other)]
    [InlineData(null, ServiceStartMode.Other)]
    public void StartModeOf_LoQueDiceIsEnabled(string? output, ServiceStartMode expected) =>
        Assert.Equal(expected, SystemdServices.StartModeOf(output));

    [Fact]
    public void Find_SoloLasQueMolestan_ConSuArranqueYSiEstanEnMarcha()
    {
        var runner = new FakeRunner(unit => unit.StartsWith("apache2", StringComparison.Ordinal) ? "enabled" : "disabled");

        var found = new SystemdServices(enabled: true).Find(runner);

        Assert.Equal(["apache2.service", "mysql.service", "php8.3-fpm.service", "redis-server.service"], found.Select(s => s.Name));
        var apache = found[0];
        Assert.Equal("Apache", apache.Kind);
        Assert.Equal("The Apache HTTP Server", apache.DisplayName);
        Assert.True(apache.StartsWithWindows);
        Assert.True(apache.Running);
        Assert.False(found[1].Running);
        Assert.Equal(ServiceStartMode.Manual, found[1].Start);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("ssh.service", StringComparison.Ordinal));   // no se pregunta por lo que no molesta
    }

    [Fact]
    public void Find_SinSystemd_NoHayServicios()
    {
        var runner = new FakeRunner(_ => null);
        Assert.Empty(new SystemdServices(enabled: false).Find(runner));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Conflicts_ConLosPuertosDelStack_UsaLosMismosPuros()
    {
        WindowsService[] services =
        [
            new("apache2.service", "The Apache HTTP Server", "Apache", null, ServiceStartMode.Automatic, Running: true),
            new("php8.3-fpm.service", "PHP 8.3 FPM", "PHP-FPM", null, ServiceStartMode.Automatic, Running: true),
        ];

        Assert.Equal(["apache2.service"], WindowsServices.Conflicting(services, [80, 443]).Select(s => s.Name));
        Assert.Empty(WindowsServices.Conflicting(services, [8080, 8443]));   // el php-fpm de la distro va por socket o por el 9000
    }

    [Fact]
    public void Textos_DescribeHowToFreeYPortHint_SinTildes()
    {
        var systemd = new SystemdServices(enabled: true);
        var apache = new WindowsService("apache2.service", "The Apache HTTP Server", "Apache", null, ServiceStartMode.Automatic, Running: true);

        Assert.Equal("el servicio del sistema \"apache2.service\" (The Apache HTTP Server) arranca con el sistema y esta en marcha", systemd.Describe(apache));
        Assert.Equal("Quitalo del arranque y paralo: sudo systemctl disable --now apache2", systemd.HowToFree(apache));
        Assert.Equal("Roost no toca los servicios del sistema.", systemd.Disclaimer);

        var foreign = systemd.PortHint(new PortOwner(80, 0, null), apache);
        Assert.StartsWith("El puerto 80 lo tiene un proceso de otro usuario", foreign);
        Assert.Contains("seguramente el servicio del sistema \"apache2.service\"", foreign);
        Assert.Contains("sudo ss -ltnp", foreign);

        var own = systemd.PortHint(new PortOwner(80, 1234, "apache2"), apache);
        Assert.StartsWith("El puerto 80 lo tiene el servicio del sistema \"apache2.service\"", own);
        Assert.DoesNotContain("otro usuario", own);
    }

    [Fact]
    public void OwnerOf_PorElNombreDelProceso_OPorElPuertoSiNoSeVe()
    {
        var systemd = new SystemdServices(enabled: true);
        WindowsService[] services =
        [
            new("apache2.service", "Apache", "Apache", null, ServiceStartMode.Automatic, Running: true),
            new("mysql.service", "MySQL", "MySQL", null, ServiceStartMode.Automatic, Running: true),
            new("mariadb.service", "MariaDB", "MariaDB", null, ServiceStartMode.Manual, Running: false),
        ];

        Assert.Equal("mysql.service", systemd.OwnerOf(new PortOwner(3306, 500, "mysqld"), services)?.Name);
        Assert.Equal("apache2.service", systemd.OwnerOf(new PortOwner(80, 0, null), services)?.Name);     // no se ve: el que suele usar el 80
        Assert.Null(systemd.OwnerOf(new PortOwner(6379, 0, null), services));                              // nadie en marcha usa el 6379
        Assert.Null(systemd.OwnerOf(new PortOwner(80, 77, "php"), services));                              // un proceso nuestro que no es un servicio
        Assert.Null(systemd.OwnerOf(null, services));
    }

    [Fact]
    public void SystemServicesCurrent_EsElDeEsteSistema()
    {
        var current = SystemServices.Current;
        if (OperatingSystem.IsWindows()) Assert.Equal("Roost no toca servicios de Windows.", current.Disclaimer);
        else if (OperatingSystem.IsLinux()) Assert.IsType<SystemdServices>(current);
    }
}
