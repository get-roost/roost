using Roost.Core.Platform;
using Roost.WebPanel;

namespace Roost.Panel.Tests;

/// <summary><c>run/panel.json</c>: lo que escribe el panel y lo que leen <c>up</c>, <c>status</c>, <c>url</c> y <c>down</c>.</summary>
public sealed class PanelStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-panel-tests-" + Guid.NewGuid().ToString("n"));
    private readonly RoostPaths _paths;

    public PanelStateTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Escribe_Lee_YBorra()
    {
        var info = new PanelInfo(Environment.ProcessId, 5556, PanelState.NewToken(), DateTimeOffset.UtcNow);
        PanelState.Write(_paths, info);
        var file = PanelState.File(_paths);
        Assert.True(File.Exists(file));
        var read = PanelState.Read(_paths);
        Assert.NotNull(read);
        Assert.Equal(info.Pid, read.Pid);
        Assert.Equal(info.Port, read.Port);
        Assert.Equal(info.Token, read.Token);
        Assert.Equal($"http://127.0.0.1:5556/?t={info.Token}", read.Url);
        Assert.Equal("http://127.0.0.1:5556/", read.PlainUrl);
        PanelState.Delete(_paths);
        Assert.Null(PanelState.Read(_paths));
    }

    [Fact]
    public void SinFichero_OIlegible_EsNull()
    {
        Assert.Null(PanelState.Read(_paths));
        Directory.CreateDirectory(_paths.Run);
        File.WriteAllText(PanelState.File(_paths), "{ esto no es json");
        Assert.Null(PanelState.Read(_paths));
        Assert.Null(PanelState.ReadAlive(_paths));
    }

    [Fact]
    public void ReadAlive_ConEsteProceso_EsVivo_YConUnPidMuerto_No()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var alive = new PanelInfo(me.Id, 5556, PanelState.NewToken(), new DateTimeOffset(me.StartTime.ToUniversalTime()));
        PanelState.Write(_paths, alive);
        Assert.NotNull(PanelState.ReadAlive(_paths));

        // El mismo pid, pero «arrancado» hace una hora: es otro proceso que reutilizó el pid.
        PanelState.Write(_paths, alive with { Started = DateTimeOffset.UtcNow.AddHours(-1) });
        Assert.Null(PanelState.ReadAlive(_paths));

        // Un pid que no existe.
        PanelState.Write(_paths, alive with { Pid = 999_999 });
        Assert.Null(PanelState.ReadAlive(_paths));
    }

    [LinuxFact]
    public void EnLinux_ElFicheroNace0600()
    {
        PanelState.Write(_paths, new PanelInfo(1, 5556, PanelState.NewToken(), DateTimeOffset.UtcNow));
        var mode = File.GetUnixFileMode(PanelState.File(_paths));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void LosEstaticosEmbebidos_EstanTodos()
    {
        var files = PanelHost.StaticFiles;
        foreach (var expected in new[] { "index.html", "app.css", "icon.svg", "js/app.js", "js/api.js", "js/ui.js", "js/dialogs.js", "js/pages/home.js", "js/pages/sites.js", "js/pages/php.js", "js/pages/web.js", "js/pages/db.js", "js/pages/audit.js", "js/pages/logs.js", "js/pages/settings.js", "js/pages/news.js", "js/pages/soon.js" })
            Assert.Contains(expected, files);
        using var index = PanelHost.OpenStatic("index.html");
        Assert.NotNull(index);
        var html = new StreamReader(index).ReadToEnd();
        // CSP: sin scripts ni estilos en línea; el módulo va en su fichero.
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("onclick=", html);
        Assert.DoesNotContain("style=", html);
        Assert.Contains("js/app.js", html);
    }
}
