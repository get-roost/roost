using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Daemon;
using Roost.Core.Platform;

namespace Roost.Daemon.Tests;

/// <summary>
/// La recarga en caliente del servidor web (<see cref="WebReload"/>) sin servidores de verdad: lo
/// que se le pide a nginx, cómo se lee su respuesta y cuándo está listo el hijo nuevo de Apache.
/// </summary>
public class WebReloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-reload-" + Guid.NewGuid().ToString("N"));

    public WebReloadTests() => Directory.CreateDirectory(_dir);

    private sealed class Runner(string? output) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add(arguments);
            return output;
        }
    }

    private static ServiceDefinition Nginx => new()
    {
        Name = "nginx",
        Executable = OsNames.Exe("nginx"),
        Arguments = "-p \"C:/roost/\" -c \"C:/roost/etc/nginx/nginx.conf\"",
    };

    [Fact]
    public void Nginx_SeLePideConElMismoPrefijoYConfiguracion_YSinSalidaEsQueHaIdoBien()
    {
        var runner = new Runner("");

        Assert.Null(WebReload.Signal(runner, Nginx, 1234));
        Assert.Equal("-s reload -p \"C:/roost/\" -c \"C:/roost/etc/nginx/nginx.conf\"", Assert.Single(runner.Calls));
    }

    [Theory]
    [InlineData("nginx: [error] OpenEvent(\"Global\\ngx_reload_1234\") failed (2: The system cannot find the file specified)")]
    [InlineData("nginx: [emerg] unknown directive \"servr\"")]
    [InlineData(null)]
    public void Nginx_UnErrorOQueNoResponda_SeDice(string? output) =>
        Assert.NotNull(WebReload.Signal(new Runner(output), Nginx, 1234));

    [Fact]
    public void OtroServicio_NoSeSabeRecargar() =>
        Assert.Contains("no se sabe recargar", WebReload.Signal(new Runner(""), new ServiceDefinition { Name = "mailpit", Executable = OsNames.Exe("mailpit") }, 1));

    [Fact]
    public void ApacheChildStarted_BuscaElAH00354DeEseHijo()
    {
        var log = Path.Combine(_dir, "apache-error.log");
        File.WriteAllText(log,
            "[Sat Oct 24 04:15:05.946 2026] [mpm_winnt:notice] [pid 31432:tid 368] AH00424: Parent: Received restart signal -- Restarting the server.\n"
            + "[Sat Oct 24 04:15:05.973 2026] [mpm_winnt:notice] [pid 31432:tid 368] AH00418: Parent: Created child process 46980\n"
            + "[Sat Oct 24 04:14:46.301 2026] [mpm_winnt:notice] [pid 49520:tid 368] AH00354: Child: Starting 64 worker threads.\n");

        Assert.True(WebReload.ApacheChildStarted(log, 49520));    // el viejo, de antes
        Assert.False(WebReload.ApacheChildStarted(log, 46980));   // el nuevo aún no

        File.AppendAllText(log, "[Sat Oct 24 04:15:06.946 2026] [mpm_winnt:notice] [pid 46980:tid 356] AH00354: Child: Starting 64 worker threads.\n");
        Assert.True(WebReload.ApacheChildStarted(log, 46980));
        Assert.False(WebReload.ApacheChildStarted(Path.Combine(_dir, "no-existe.log"), 46980));
    }

    [Fact]
    public void ApacheRestartEvent_EsElDeMpmWinnt() => Assert.Equal("ap31432_restart", WebReload.ApacheRestartEvent(31432));

    [Fact]
    public void ApacheResumed_BuscaElAH00489_SoloDesdeElOffsetDeLaRecarga()
    {
        var log = Path.Combine(_dir, "apache-error.log");
        File.WriteAllText(log, "[notice] AH00489: Apache/2.4.58 (Ubuntu) configured -- resuming normal operations\n");
        var offset = new FileInfo(log).Length;

        Assert.True(WebReload.ApacheResumed(log, 0));
        Assert.False(WebReload.ApacheResumed(log, offset));   // lo del arranque, antes de la recarga, no cuenta
        File.AppendAllText(log, "[notice] AH00493: SIGUSR1 received.  Doing graceful restart\n");
        Assert.False(WebReload.ApacheResumed(log, offset));
        File.AppendAllText(log, "[notice] AH00489: Apache/2.4.58 (Ubuntu) configured -- resuming normal operations\n");
        Assert.True(WebReload.ApacheResumed(log, offset));
        Assert.False(WebReload.ApacheResumed(Path.Combine(_dir, "no-existe.log"), 0));
    }

    [LinuxFact]
    public void Apache_EnLinux_SeLeMandaSIGUSR1_YSiNoSePuedeSeDice()
    {
        var calls = new List<(int Pid, int Signal)>();
        var before = WebReload.KillSignal;
        try
        {
            WebReload.KillSignal = (pid, signal) => { calls.Add((pid, signal)); return pid == 4321; };
            var apache = new ServiceDefinition { Name = "apache", Executable = "/usr/sbin/apache2" };

            Assert.Null(WebReload.Signal(new Runner(""), apache, 4321));
            Assert.Equal((4321, 10), Assert.Single(calls));
            Assert.Contains("SIGUSR1", WebReload.Signal(new Runner(""), apache, 1));
        }
        finally
        {
            WebReload.KillSignal = before;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
