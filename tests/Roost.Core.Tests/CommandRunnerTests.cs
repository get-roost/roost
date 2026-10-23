using System.Diagnostics;
using Roost.Core.Scanning;

namespace Roost.Core.Tests;

/// <summary>
/// Testea <see cref="CommandRunner"/> con ordenes reales pero triviales del sistema (cmd en Windows,
/// sh en el resto): echo, sort, el codigo de salida, el entorno, la carpeta, el timeout y la
/// cancelacion. Son rapidas y no necesitan red.
/// </summary>
public class CommandRunnerTests
{
    private static CommandSpec Shell(string script, string? cwd = null, IReadOnlyDictionary<string, string?>? env = null, TimeSpan? timeout = null, string? stdin = null) =>
        OperatingSystem.IsWindows()
            ? new CommandSpec("cmd", ["/d", "/c", script], cwd, env, timeout, stdin)
            : new CommandSpec("sh", ["-c", script], cwd, env, timeout, stdin);

    private static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 > nul" : $"sleep {seconds}";

    private sealed class Lines : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) { lock (Items) Items.Add(value); }
    }

    [Fact]
    public async Task Run_DaElCodigoDeSalida_StdoutYLasLineasEnVivo()
    {
        var lines = new Lines();

        var result = await CommandRunner.Default.RunAsync(Shell("echo hola"), lines, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hola", result.Stdout.Trim());
        Assert.Equal("", result.Stderr);
        Assert.Contains("hola", lines.Items);
    }

    [Fact]
    public async Task Run_UnCodigoDistintoDeCero_NoEsExito()
    {
        var result = await CommandRunner.Default.RunAsync(Shell("exit 3"), null, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Run_StderrVaAparte_YTambienEnLasLineas()
    {
        var lines = new Lines();

        var result = await CommandRunner.Default.RunAsync(Shell("echo fallo 1>&2"), lines, CancellationToken.None);

        Assert.Equal("fallo", result.Stderr.Trim());
        Assert.Equal("", result.Stdout.Trim());
        Assert.Contains("fallo", lines.Items.Select(l => l.Trim()));
    }

    [Fact]
    public async Task Run_EscribeElStdin()
    {
        var result = await CommandRunner.Default.RunAsync(Shell("sort", stdin: "pera\nmanzana\n"), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["manzana", "pera"], result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    }

    [Fact]
    public async Task Run_AplicaElEntorno_YUnNullQuitaLaVariable()
    {
        var script = OperatingSystem.IsWindows() ? "echo [%ROOST_PRUEBA%][%PATH%]" : "echo \"[$ROOST_PRUEBA][$PATH]\"";
        var env = new Dictionary<string, string?> { ["ROOST_PRUEBA"] = "valor", ["PATH"] = null };

        var result = await CommandRunner.Default.RunAsync(Shell(script, env: env), null, CancellationToken.None);

        var expectedEmpty = OperatingSystem.IsWindows() ? "[%PATH%]" : "[]";
        Assert.Equal($"[valor]{expectedEmpty}", result.Stdout.Trim());
    }

    [Fact]
    public async Task Run_RespetaLaCarpetaDeTrabajo()
    {
        var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var result = await CommandRunner.Default.RunAsync(Shell(OperatingSystem.IsWindows() ? "cd" : "pwd", cwd: dir), null, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(result.Stdout.Trim()).TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
    }

    [Fact]
    public async Task Run_LosArgumentosConEspaciosLleganEnteros()
    {
        var spec = OperatingSystem.IsWindows()
            ? new CommandSpec("cmd", ["/d", "/c", "echo", "con espacios"])
            : new CommandSpec("sh", ["-c", "echo \"$1\"", "sh", "con espacios"]);

        var result = await CommandRunner.Default.RunAsync(spec, null, CancellationToken.None);

        Assert.Contains("con espacios", result.Stdout);
    }

    [Fact]
    public async Task Run_SePasaDelTiempo_MataElArbolYLoDice()
    {
        var watch = Stopwatch.StartNew();

        var result = await CommandRunner.Default.RunAsync(Shell(Sleep(20), timeout: TimeSpan.FromMilliseconds(700)), null, CancellationToken.None);

        watch.Stop();
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        // Si el hijo (ping / sleep) siguiera vivo, las tuberias no se cerrarian y esto tardaria los 20 s.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"tardo {watch.Elapsed}");
    }

    [Fact]
    public async Task Run_Cancelado_LanzaYNoDejaElProcesoColgado()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommandRunner.Default.RunAsync(Shell(Sleep(20)), null, cts.Token));

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"tardo {watch.Elapsed}");
    }

    [Fact]
    public async Task Run_EjecutableInexistente_LanzaRoostException()
    {
        var spec = new CommandSpec(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".exe"), []);

        await Assert.ThrowsAsync<RoostException>(() => CommandRunner.Default.RunAsync(spec, null, CancellationToken.None));
    }

    [Fact]
    public void Display_EntrecomillaSoloLoQueHaceFalta()
    {
        var spec = new CommandSpec(@"C:\roost\bin\git\cmd\git.exe", ["clone", "--progress", "https://example.test/a.git", @"C:\mi carpeta\tienda"]);

        Assert.Equal(@"C:\roost\bin\git\cmd\git.exe clone --progress https://example.test/a.git ""C:\mi carpeta\tienda""", spec.Display);
    }
}
