using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Scanning;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Un script de npm que se queda en marcha (<c>npm run dev</c> con Vite, un «watch»), lanzado desde el
/// panel del sitio. No es un diálogo que espera a que acabe: sigue en marcha aunque se cierre su
/// ventana, mientras Roost esté abierto (también en la bandeja), y el panel lo dice con «Ver salida» y
/// «Parar». Guarda su salida (la del diálogo de salida en vivo, con el mismo color por línea) y saca de
/// ella la dirección que anuncia Vite («Local: https://tienda.test:5173/»). Al salir de Roost, se para
/// solo: no queda un node suelto con el puerto cogido.
/// </summary>
public sealed partial class NodeDevSession : ObservableObject
{
    /// <summary>Las líneas que se pintan; las de antes se quitan (copiar da hasta <see cref="MaxKept"/>).</summary>
    public const int MaxShown = 2000;

    public const int MaxKept = 20_000;

    private static readonly List<NodeDevSession> Live = [];

    private readonly CommandSpec _spec;
    private readonly ICommandRunner _runner;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly List<string> _kept = [];
    private int _flushQueued;
    private Task? _run;

    static NodeDevSession()
    {
        // Al salir de Roost, fuera los que sigan en marcha (y lo que hayan lanzado: esbuild, el watcher).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll(TimeSpan.FromSeconds(3));
    }

    public NodeDevSession(string site, string host, string display, string subtitle, CommandSpec spec, ICommandRunner runner)
    {
        Site = site;
        Host = host;
        Display = display;
        Subtitle = subtitle;
        _spec = spec;
        _runner = runner;
    }

    public string Site { get; }

    public string Host { get; }

    /// <summary>La orden como se enseña: «npm run dev».</summary>
    public string Display { get; }

    public string Subtitle { get; }

    public string? Folder => _spec.WorkingDirectory;

    public DateTimeOffset Started { get; private set; }

    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Done), nameof(StatusText), nameof(ResultText), nameof(ResultTone))]
    public partial bool Running { get; private set; }

    public bool Done => !Running;

    /// <summary>Lo ha parado el usuario (con «Parar» o al salir de Roost).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ResultText), nameof(ResultTone))]
    public partial bool Stopped { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ResultText), nameof(ResultTone))]
    public partial int? ExitCode { get; private set; }

    /// <summary>No llegó a arrancar: por qué.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ResultText), nameof(ResultTone))]
    public partial string? Error { get; private set; }

    /// <summary>La dirección que anuncia el servidor de desarrollo (la de Vite), en cuanto la dice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUrl), nameof(StatusText))]
    public partial string? Url { get; private set; }

    public bool HasUrl => Url is not null;

    /// <summary>Una línea para el panel y la cabecera del diálogo.</summary>
    public string StatusText =>
        Running ? $"{Display} en marcha desde las {Started.ToLocalTime():HH:mm}" + (Url is null ? "" : $" · {Url}")
        : Error is not null ? $"{Display} no arrancó"
        : Stopped ? $"{Display} parado"
        : ExitCode == 0 ? $"{Display} terminó (código 0)"
        : $"{Display} terminó con el código {ExitCode}";

    public string ResultText =>
        Running ? "Sigue en marcha aunque cierres esta ventana (y con Roost en la bandeja). «Parar» lo termina; al salir de Roost, se para solo."
        : Error is not null ? $"No se pudo ejecutar: {Error}"
        : Stopped ? "Parado: se ha terminado el proceso y todo lo que había lanzado."
        : ExitCode == 0 ? "Ha terminado solo, con el código 0."
        : $"Ha terminado solo con el código {ExitCode}: las últimas líneas suelen decir por qué (un puerto ocupado, una dependencia que falta).";

    public Tone ResultTone => Running ? Tone.Accent : Stopped || ExitCode == 0 ? Tone.Neutral : Tone.Bad;

    /// <summary>Toda la salida que se ha visto, línea a línea (lo que copia «Copiar la salida»).</summary>
    public string Output
    {
        get
        {
            FlushLines();
            lock (_kept) return string.Join(Environment.NewLine, _kept);
        }
    }

    /// <summary>Se cumple cuando el proceso acaba (o se para).</summary>
    public Task Completion => _run ?? Task.CompletedTask;

    /// <summary>Lo lanza. Una sola vez.</summary>
    public void Start()
    {
        if (_run is not null) return;
        Started = DateTimeOffset.Now;
        Running = true;
        lock (Live) Live.Add(this);
        // Fuera del hilo de la UI: así, al salir de Roost (con la UI ya parada), la cancelación llega a
        // matar el proceso; con el contexto de la UI, la continuación que lo mata no se ejecutaría nunca.
        _run = Task.Run(RunAsync);
    }

    /// <summary>Lo para (mata el árbol de procesos). Si ya terminó, no hace nada.</summary>
    public void Stop()
    {
        if (!Running) return;
        Stopped = true;
        _cancel.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            var result = await _runner.RunAsync(_spec, new LineSink(this), _cancel.Token);
            Dispatcher.UIThread.Post(() => ExitCode = result.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // Parado con «Parar» o al salir.
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => Error = ex.Message);
        }
        finally
        {
            lock (Live) Live.Remove(this);
            Dispatcher.UIThread.Post(() =>
            {
                FlushLines();
                Running = false;
            });
        }
    }

    /// <summary>Para todos los que sigan en marcha y espera un poco a que acaben (al salir de Roost).</summary>
    public static void StopAll(TimeSpan wait)
    {
        NodeDevSession[] running;
        lock (Live) running = [.. Live];
        foreach (var session in running) session._cancel.Cancel();
        try { Task.WaitAll([.. running.Select(s => s.Completion)], wait); }
        catch (AggregateException) { /* ya se han ido, o no da tiempo: no hay más que hacer */ }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"\bLocal:\s+(https?://\S+)")]
    private static partial Regex UrlPattern();

    /// <summary>PURO: la línea sin los códigos de color de la terminal (hay herramientas que los ponen igual).</summary>
    public static string Clean(string line) => AnsiPattern().Replace(line, "");

    /// <summary>PURO: la dirección de «Local: https://tienda.test:5173/» (Vite, y otros como él), o null.</summary>
    public static string? UrlIn(string line) => UrlPattern().Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Pasa a <see cref="Lines"/> lo que ha llegado (en el hilo de la UI).</summary>
    internal void FlushLines()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        while (_pending.TryDequeue(out var raw))
        {
            var line = Clean(raw);
            lock (_kept)
            {
                if (_kept.Count >= MaxKept) _kept.RemoveAt(0);
                _kept.Add(line);
            }
            if (Url is null && UrlIn(line) is { } url) Url = url;
            if (Lines.Count > 0 && CommandOutputDialogViewModel.IsProgressUpdate(Lines[^1].Text, line))
                Lines[^1] = new LogLine(line, CommandOutputDialogViewModel.LevelOf(line));
            else
                Lines.Add(new LogLine(line, CommandOutputDialogViewModel.LevelOf(line)));
        }
        while (Lines.Count > MaxShown) Lines.RemoveAt(0);
    }

    private void Enqueue(string line)
    {
        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            Dispatcher.UIThread.Post(FlushLines, DispatcherPriority.Background);
    }

    private sealed class LineSink(NodeDevSession owner) : IProgress<string>
    {
        public void Report(string value) => owner.Enqueue(value);
    }
}

/// <summary>
/// La ventana de un <see cref="NodeDevSession"/>: su salida en vivo, «Parar», «Abrir» la dirección que
/// anuncia y «Dejar en marcha», que la cierra sin pararlo. Se puede abrir otra vez desde el panel
/// («Ver salida»): cada vez es un diálogo nuevo sobre la misma sesión.
/// </summary>
public sealed partial class NodeDevServerDialogViewModel(NodeDevSession session, Func<string, Task>? copy = null, Action<string>? open = null) : DialogViewModel
{
    public override bool Wide => true;

    public NodeDevSession Session { get; } = session;

    public string Title => $"{Session.Display} · {Session.Host}";

    public string Subtitle => Session.Subtitle;

    public bool CanCopy => copy is not null;

    [RelayCommand]
    private void Stop() => Session.Stop();

    [RelayCommand]
    private void Open()
    {
        if (Session.Url is { } url) open?.Invoke(url);
    }

    [RelayCommand]
    private Task CopyAsync() => copy is null ? Task.CompletedTask : copy(Session.Output);

    /// <summary>«Dejar en marcha» o, si ya ha terminado, «Cerrar»: cierra la ventana, no el proceso.</summary>
    [RelayCommand]
    private void Finish() => Close(Session.Running);
}
