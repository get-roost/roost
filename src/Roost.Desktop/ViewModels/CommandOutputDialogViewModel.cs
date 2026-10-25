using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Scanning;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// La salida de una orden en directo (Composer, y luego los pasos tras clonar, git y npm): el
/// título, las líneas según salen con un scroll que las sigue, «Cancelar» que mata el proceso y
/// todo lo que lanzó, y al acabar el resultado (bien, o el código con el que falló) con un botón
/// para copiar la salida entera. No sabe nada de Composer: recibe un <see cref="CommandSpec"/> y
/// lo ejecuta sobre el <see cref="ICommandRunner"/> que le den (los tests usan uno guionizado).
/// No se cierra solo al acabar, para que se pueda leer lo que dijo.
/// </summary>
public sealed partial class CommandOutputDialogViewModel : DialogViewModel
{
    /// <summary>Las líneas que se pintan; las de antes se quitan (copiar las da todas, hasta <see cref="MaxKept"/>).</summary>
    public const int MaxShown = 4000;

    /// <summary>Lo que se guarda para copiar.</summary>
    public const int MaxKept = 50_000;

    private readonly CommandSpec _spec;
    private readonly ICommandRunner _runner;
    private readonly Func<string, Task>? _copy;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<CommandResult?> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly List<string> _kept = [];
    private int _flushQueued;
    private int _dropped;

    /// <param name="copy">Lo que hace «Copiar la salida» (el portapapeles de la ventana); null lo oculta.</param>
    public CommandOutputDialogViewModel(string title, string subtitle, CommandSpec spec, ICommandRunner runner, Func<string, Task>? copy = null)
    {
        Title = title;
        Subtitle = subtitle;
        _spec = spec;
        _runner = runner;
        _copy = copy;
        CommandText = spec.Display;
    }

    public override bool Wide => true;

    /// <summary>Mientras corre, ni Escape ni el fondo lo cierran: o termina, o se cancela.</summary>
    public override bool CanDismiss => Done;

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>La orden como se enseña (<c>composer install</c>); por defecto, la de verdad entera.</summary>
    public string CommandText { get; init; }

    /// <summary>La carpeta donde se ejecuta.</summary>
    public string? Folder => _spec.WorkingDirectory;

    public bool HasFolder => Folder is not null;

    /// <summary>Un aviso fijo encima de la salida (que Composer ejecuta los scripts del proyecto); null lo oculta.</summary>
    public string? Note { get; init; }

    public bool HasNote => Note is not null;

    public bool CanCopy => _copy is not null;

    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Running), nameof(Failed))]
    public partial bool Done { get; private set; }

    public bool Running => !Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Failed))]
    public partial bool Succeeded { get; private set; }

    /// <summary>Terminó y no fue bien (falló, se cortó por tiempo, se canceló o no arrancó).</summary>
    public bool Failed => Done && !Succeeded;

    [ObservableProperty] public partial bool Canceled { get; private set; }

    [ObservableProperty] public partial string Status { get; private set; } = "Arrancando…";

    [ObservableProperty] public partial string ResultText { get; private set; } = "";

    [ObservableProperty] public partial Tone ResultTone { get; private set; } = Tone.Neutral;

    /// <summary>El código de salida al terminar; null mientras corre, si se canceló o si no llegó a arrancar.</summary>
    [ObservableProperty] public partial int? ExitCode { get; private set; }

    /// <summary>Lo que dejó la orden (null si se canceló o no arrancó). Se cumple al terminar, aunque el diálogo ya esté cerrado.</summary>
    public Task<CommandResult?> Finished => _finished.Task;

    /// <summary>Toda la salida que se ha visto, línea a línea (lo que copia «Copiar la salida»).</summary>
    public string Output
    {
        get
        {
            FlushLines();
            lock (_kept) return string.Join(Environment.NewLine, _kept);
        }
    }

    /// <summary>Ejecuta la orden. Lanzarlo una vez, antes o después de enseñar el diálogo.</summary>
    public async Task RunAsync()
    {
        var watch = Stopwatch.StartNew();
        Status = "En marcha…";
        CommandResult? result = null;
        try
        {
            result = await _runner.RunAsync(_spec, new LineSink(this), _cancel.Token);
            FlushLines();
            ExitCode = result.ExitCode;
            Succeeded = result.Succeeded;
            var took = Took(watch.Elapsed);
            (ResultText, ResultTone) = result.TimedOut
                ? ($"Se ha pasado del tiempo y se ha parado (tras {took}).", Tone.Bad)
                : result.Succeeded
                    ? ($"Terminado bien en {took} (código 0).", Tone.Ok)
                    : ($"Ha fallado con el código {result.ExitCode} (tras {took}). Las últimas líneas suelen decir por qué.", Tone.Bad);
        }
        catch (OperationCanceledException)
        {
            FlushLines();
            Canceled = true;
            (ResultText, ResultTone) = ("Cancelado: se ha parado el proceso y todo lo que había lanzado. Lo que llegó a hacer, hecho está.", Tone.Warn);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            FlushLines();
            (ResultText, ResultTone) = ($"No se pudo ejecutar: {ex.Message}", Tone.Bad);
        }
        finally
        {
            Status = Canceled ? "Cancelado" : Succeeded ? "Terminado" : "Terminado con error";
            Done = true;
            _finished.TrySetResult(result);
        }
    }

    private static string Took(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? string.Format(Roost.Desktop.Services.Es.Culture, "{0:0.0} s", elapsed.TotalSeconds)
            : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";

    [RelayCommand]
    private void Cancel()
    {
        if (Done) Close(Succeeded);
        else
        {
            Status = "Cancelando…";
            _cancel.Cancel();
        }
    }

    [RelayCommand]
    private void Finish()
    {
        if (Done) Close(Succeeded);
    }

    [RelayCommand]
    private Task CopyAsync() => _copy is null ? Task.CompletedTask : _copy(Output);

    /// <summary>Pasa a <see cref="Lines"/> lo que ha llegado (en el hilo de la UI). Lo llaman la propia cola y, al acabar, <see cref="RunAsync"/>.</summary>
    internal void FlushLines()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var added = false;
        while (_pending.TryDequeue(out var line))
        {
            lock (_kept)
            {
                if (_kept.Count >= MaxKept) _kept.RemoveAt(0);
                _kept.Add(line);
            }

            // Una línea de progreso que se reescribe (git: «Receiving objects:  45%») sustituye a la anterior.
            if (Lines.Count > 0 && IsProgressUpdate(Lines[^1].Text, line))
                Lines[^1] = new LogLine(line, LevelOf(line));
            else
                Lines.Add(new LogLine(line, LevelOf(line)));
            added = true;
        }
        if (!added) return;

        while (Lines.Count > MaxShown)
        {
            Lines.RemoveAt(0);
            _dropped++;
        }
        if (Running)
            Status = _dropped > 0 ? $"En marcha… (se ven las últimas {Lines.Count} líneas)" : $"En marcha… {Lines.Count} {(Lines.Count == 1 ? "línea" : "líneas")}";
    }

    // Cada línea llega del hilo que lee la tubería: se encola y se pasa a la UI de una vez.
    private void Enqueue(string line)
    {
        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            Dispatcher.UIThread.Post(FlushLines, DispatcherPriority.Background);
    }

    private sealed class LineSink(CommandOutputDialogViewModel owner) : IProgress<string>
    {
        public void Report(string value) => owner.Enqueue(value);
    }

    [GeneratedRegex(@"^(\D{3,}?)\s*\d{1,3}%")]
    private static partial Regex ProgressPattern();

    /// <summary>
    /// PURO: <paramref name="next"/> es la misma línea de progreso que <paramref name="previous"/> con
    /// otro porcentaje (git la reescribe con <c>\r</c>; el runner las da como líneas sueltas).
    /// </summary>
    public static bool IsProgressUpdate(string previous, string next)
    {
        var a = ProgressPattern().Match(previous);
        var b = ProgressPattern().Match(next);
        return a.Success && b.Success && a.Groups[1].Value == b.Groups[1].Value;
    }

    [GeneratedRegex(@"\b(error|errors|fatal|failed)\b|exception|could not be resolved|^\s*Problem \d+|npm ERR!", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\bwarning\b|deprecat|npm WARN|^\s*abandoned", RegexOptions.IgnoreCase)]
    private static partial Regex WarnPattern();

    /// <summary>PURO: el color de una línea de salida (los mismos que Registros).</summary>
    public static LogLevel LevelOf(string line) =>
        ErrorPattern().IsMatch(line) ? LogLevel.Error
        : WarnPattern().IsMatch(line) ? LogLevel.Warn
        : LogLevel.Normal;
}
