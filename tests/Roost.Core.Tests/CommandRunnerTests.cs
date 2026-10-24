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

    // BD-2: stdin y stdout en streaming (los volcados de varios GB)

    /// <summary>Una fuente que da <paramref name="bytes"/> y después falla, como un .gz roto a mitad.</summary>
    private sealed class FailingSource(int bytes) : Stream
    {
        private int _given;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_given >= bytes) throw new InvalidDataException("gz roto");
            var n = Math.Min(count, bytes - _given);
            for (var i = 0; i < n; i++) buffer[offset + i] = (byte)(i % 50 == 49 ? '\n' : 'x');
            _given += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Un destino que se llena: falla al escribir, como un disco sin sitio.</summary>
    private sealed class FullDisk : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("No queda espacio en el disco.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException("No queda espacio en el disco.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("No queda espacio en el disco.");
    }

    [Fact]
    public async Task Run_StdinStream_LeLlegaEntero_PorBloques()
    {
        // 20 000 líneas (unos 200 KB, más que el buffer de una tubería): la orden las cuenta.
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"linea {i}\n"));
        using var source = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text));
        var spec = Shell("sort") with { StdinStream = source };

        var result = await CommandRunner.Default.RunAsync(spec, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Stderr);
        Assert.Equal(20_000, result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(source.Length, source.Position);
    }

    [Fact]
    public async Task Run_StdinStream_SiLaFuenteFalla_MataLaOrdenYLanzaEseError()
    {
        var watch = Stopwatch.StartNew();
        // La orden espera más entrada para siempre: si no se la matara, esto no acabaría.
        var spec = Shell("sort") with { StdinStream = new FailingSource(300_000) };

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CommandRunner.Default.RunAsync(spec, null, CancellationToken.None));

        Assert.Equal("gz roto", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"tardo {watch.Elapsed}");
    }

    [Fact]
    public async Task Run_StdinStream_SiLaOrdenAcabaAntes_NoEsUnErrorNuestro()
    {
        // `exit 2` no lee nada: escribir en su stdin da IOException, y lo que cuenta es su código.
        using var source = new MemoryStream(new byte[2_000_000]);
        var spec = Shell("exit 2") with { StdinStream = source };

        var result = await CommandRunner.Default.RunAsync(spec, null, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Run_StdinStream_Cancelado_LanzaYNoDejaElProcesoColgado()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        var spec = Shell(Sleep(20)) with { StdinStream = new MemoryStream(new byte[100]) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CommandRunner.Default.RunAsync(spec, null, cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"tardo {watch.Elapsed}");
    }

    [Fact]
    public async Task Run_StdoutStream_RecibeLosBytesTalCual_YNoVanAlResultado()
    {
        using var sink = new MemoryStream();
        var lines = new Lines();
        var spec = Shell("echo hola& echo aviso 1>&2") with { StdoutStream = sink };
        if (!OperatingSystem.IsWindows()) spec = Shell("echo hola; echo aviso 1>&2") with { StdoutStream = sink };

        var result = await CommandRunner.Default.RunAsync(spec, lines, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("hola", System.Text.Encoding.ASCII.GetString(sink.ToArray()).Trim());
        Assert.Equal("", result.Stdout);
        Assert.Equal("aviso", result.Stderr.Trim());
        Assert.DoesNotContain("hola", lines.Items);   // stdout no sale por líneas; stderr sí
        Assert.Contains("aviso", lines.Items.Select(l => l.Trim()));
    }

    [Fact]
    public async Task Run_StdoutStream_SiElDestinoFalla_MataLaOrdenYLanzaEseError()
    {
        var watch = Stopwatch.StartNew();
        var script = OperatingSystem.IsWindows() ? "for /l %i in (1,1,2000000) do @echo una linea bastante larga de volcado" : "yes una linea bastante larga de volcado";
        var spec = Shell(script) with { StdoutStream = new FullDisk() };

        var ex = await Assert.ThrowsAsync<IOException>(() => CommandRunner.Default.RunAsync(spec, null, CancellationToken.None));

        Assert.Contains("espacio", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), $"tardo {watch.Elapsed}");
    }

    [Fact]
    public void Display_EntrecomillaSoloLoQueHaceFalta()
    {
        var spec = new CommandSpec(@"C:\roost\bin\git\cmd\git.exe", ["clone", "--progress", "https://example.test/a.git", @"C:\mi carpeta\tienda"]);

        Assert.Equal(@"C:\roost\bin\git\cmd\git.exe clone --progress https://example.test/a.git ""C:\mi carpeta\tienda""", spec.Display);
    }
}
