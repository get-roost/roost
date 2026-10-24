using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>Los servicios de Windows que le quitan los puertos a Roost (lo puro: sin registro ni sc).</summary>
public class WindowsServicesTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\MariaDB 10.6\\bin\\mysqld.exe\" \"--defaults-file=C:\\Program Files\\MariaDB 10.6\\data\\my.ini\" \"MariaDB\"", "C:\\Program Files\\MariaDB 10.6\\bin\\mysqld.exe")]
    [InlineData("C:\\Apache24\\bin\\httpd.exe -k runservice", "C:\\Apache24\\bin\\httpd.exe")]
    [InlineData("C:\\Windows\\system32\\svchost.exe -k iissvcs", "C:\\Windows\\system32\\svchost.exe")]
    [InlineData("", null)]
    public void ExecutableOf_ConComillasConArgumentosYSinNada(string imagePath, string? expected) =>
        Assert.Equal(expected, WindowsServices.ExecutableOf(imagePath));

    [WindowsTheory("Solo en Windows: los servicios de Windows.")]
    [InlineData("MariaDB", "C:\\Program Files\\MariaDB 10.6\\bin\\mysqld.exe", "MariaDB")]   // MariaDB vieja con mysqld.exe
    [InlineData("MySQL80", "C:\\Program Files\\MySQL\\MySQL Server 8.0\\bin\\mysqld.exe", "MySQL")]
    [InlineData("MariaDB", "C:\\Program Files\\MariaDB 11.4\\bin\\mariadbd.exe", "MariaDB")]
    [InlineData("Apache2.4", "C:\\Apache24\\bin\\httpd.exe", "Apache")]
    [InlineData("W3SVC", "C:\\Windows\\system32\\svchost.exe", "IIS")]
    [InlineData("GameInputRedistService", "C:\\Program Files\\Microsoft GameInput\\x64\\GameInputRedistService.exe", null)]   // «Redist» no es Redis
    [InlineData("Memurai", "C:\\Program Files\\Memurai\\memurai.exe", "Redis")]
    public void Classify_PorElEjecutable_NoPorElNombre(string name, string exe, string? kind) =>
        Assert.Equal(kind, WindowsServices.Classify(name, exe));

    [Fact]
    public void IsRunning_YStartMode_DeScYDelRegistro()
    {
        Assert.True(WindowsServices.IsRunning("SERVICE_NAME: MariaDB\r\n        TYPE               : 10  WIN32_OWN_PROCESS\r\n        STATE              : 4  RUNNING\r\n"));
        Assert.False(WindowsServices.IsRunning("        STATE              : 1  STOPPED\r\n"));
        Assert.False(WindowsServices.IsRunning(null));
        Assert.Equal(ServiceStartMode.Automatic, WindowsServices.StartModeOf(2));
        Assert.Equal(ServiceStartMode.Disabled, WindowsServices.StartModeOf(4));
    }

    [Fact]
    public void Conflicting_LosQueArrancanConWindowsOEstanEnMarcha_YUsanUnPuertoDeRoost()
    {
        WindowsService[] services =
        [
            new("MariaDB", "MariaDB", "MariaDB", null, ServiceStartMode.Automatic, Running: false),   // parado, pero mañana arranca
            new("MySQL57", "MySQL57", "MySQL", null, ServiceStartMode.Manual, Running: false),        // manual y parado: no molesta
            new("MySQL80", "MySQL80", "MySQL", null, ServiceStartMode.Disabled, Running: false),      // deshabilitado
            new("Apache2.4", "Apache2.4", "Apache", null, ServiceStartMode.Manual, Running: true),    // en marcha
        ];

        Assert.Equal(["MariaDB", "Apache2.4"], WindowsServices.Conflicting(services, [80, 443, 3306]).Select(s => s.Name));
        Assert.Equal(["MariaDB"], WindowsServices.Conflicting(services, [8080, 8443, 3306]).Select(s => s.Name));   // web en otros puertos
        Assert.Empty(WindowsServices.Conflicting(services, [8080, 8443, 3307]));
    }
}
