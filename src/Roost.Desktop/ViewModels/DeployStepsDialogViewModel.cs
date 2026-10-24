using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Deploy;
using Roost.Core.Node;
using Roost.Core.Sites;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>Desde dónde se abren los pasos: cambia el título, los botones y lo que pasa si se dejan a medias.</summary>
public enum DeployDialogMode
{
    /// <summary>Justo después de clonar un sitio (Git-app llama a <c>OfferDeployStepsAsync</c>).</summary>
    AfterClone,

    /// <summary>«Pasos de instalación…» del panel de un sitio.</summary>
    Panel,

    /// <summary>Nuevo sitio con una plantilla: empieza solo, y si no se acaba, se deshace.</summary>
    Template,
}

/// <summary>Cómo va un paso en el diálogo.</summary>
public enum DeployRowState
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
    Canceled,
}

/// <summary>Cómo se cerró el diálogo de los pasos.</summary>
/// <param name="Ran">Se llegó a ejecutar algo.</param>
/// <param name="Succeeded">Se hicieron todos los marcados (bien o saltados) sin que fallara ninguno.</param>
public sealed record DeployDialogResult(bool Ran, bool Succeeded);

/// <summary>Un paso del diálogo: su casilla, su texto (con tildes) y cómo va.</summary>
public sealed partial class DeployStepRow : ObservableObject
{
    public DeployStepRow(DeployStep step, DeployTarget target)
    {
        Step = step;
        Label = DeployTexts.Label(step);
        Hint = DeployTexts.Hint(step, target);
        IsChecked = step.Checked && step.Available;
    }

    public DeployStep Step { get; }

    public string Label { get; }

    public string Hint { get; }

    /// <summary>Lo que se enseña como «&gt; …» en la salida: la orden, o lo que hace Roost.</summary>
    public string Header => Step.Command ?? Label;

    /// <summary>La etiqueta es una orden (<c>composer install</c>): va en mono.</summary>
    public bool IsCommand => Step.Command is not null;

    public bool Available => Step.Available;

    /// <summary>Lo que ahora no se puede (npm sin un Node de Roost; migrar con la base de datos parada), atenuado.</summary>
    public double RowOpacity => Step.Available ? 1 : 0.55;

    [ObservableProperty] public partial bool IsChecked { get; set; }

    /// <summary>Se puede marcar o desmarcar ahora (ni corriendo, ni si es obligatorio, ni si no se puede).</summary>
    [ObservableProperty] public partial bool Editable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped), nameof(ShowState), nameof(StateTone))]
    public partial DeployRowState State { get; set; }

    /// <summary>«hecho», «saltado: ya tenía APP_KEY», «ha fallado»…; vacío mientras está pendiente.</summary>
    [ObservableProperty] public partial string StateText { get; set; } = "";

    public bool IsActive => State == DeployRowState.Running;
    public bool IsDone => State is DeployRowState.Done or DeployRowState.Skipped;
    public bool IsFailed => State is DeployRowState.Failed or DeployRowState.Canceled;
    public bool IsSkipped => State == DeployRowState.Skipped;
    public bool ShowState => State != DeployRowState.Pending;

    public Tone StateTone => State switch
    {
        DeployRowState.Done => Tone.Ok,
        DeployRowState.Skipped => Tone.Neutral,
        DeployRowState.Failed => Tone.Bad,
        DeployRowState.Canceled => Tone.Warn,
        _ => Tone.Accent,
    };
}

/// <summary>
/// «Pasos tras clonar» (maqueta E del diseño de git) y «Pasos de instalación»: el proyecto detectado,
/// el PHP, una casilla por paso (<see cref="DeployPlan"/>), el aviso de que Composer y artisan ejecutan
/// código del proyecto, la salida en vivo de cada paso, «Cancelar», el resultado y, si un paso falla,
/// el texto del diseño con las últimas líneas y «Reintentar desde aquí». Con una plantilla empieza
/// solo y, si no se acaba, quien lo abrió deshace lo creado. No sabe nada de Composer ni de PHP:
/// ejecuta cada paso con lo que le den (en la app, <see cref="DeployRunner.RunStepAsync"/>).
/// </summary>
public sealed partial class DeployStepsDialogViewModel : DialogViewModel
{
    /// <summary>Las líneas que se pintan; las de antes se quitan (copiar las da todas).</summary>
    public const int MaxShown = 4000;

    private readonly Func<DeployStep, IProgress<string>, CancellationToken, Task<DeployOutcome>> _run;
    private readonly Func<string, Task>? _copy;
    private readonly ConcurrentQueue<LogLine> _pending = new();
    private readonly List<string> _kept = [];
    private readonly string _site;
    private CancellationTokenSource? _cancel;
    private int _flushQueued;
    private int _failedIndex = -1;

    /// <param name="run">Ejecuta un paso con sus líneas (y lanza <see cref="OperationCanceledException"/> si se cancela).</param>
    /// <param name="copy">«Copiar la salida» (el portapapeles); null lo oculta.</param>
    public DeployStepsDialogViewModel(DeployDialogMode mode, ProjectKind kind, DeployTarget target, IReadOnlyList<DeployStep> steps,
        Func<DeployStep, IProgress<string>, CancellationToken, Task<DeployOutcome>> run, Func<string, Task>? copy = null)
    {
        Mode = mode;
        _run = run;
        _copy = copy;
        _site = target.SiteName;
        Rows = steps.Select(s => new DeployStepRow(s, target)).ToList();
        foreach (var row in Rows) row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DeployStepRow.IsChecked)) OnPropertyChanged(nameof(CanRun)); };

        var framework = kind.FrameworkLabel ?? (kind.HasComposerJson ? "con composer.json" : null);
        var evidence = kind.Evidence.Count > 0 ? $" ({string.Join(", ", kind.Evidence)})" : "";
        Title = mode switch
        {
            DeployDialogMode.AfterClone => $"{target.Host} está clonado",
            DeployDialogMode.Template => $"Creando {target.Host}",
            _ => $"Pasos de instalación · {target.Host}",
        };
        Subtitle = mode == DeployDialogMode.Template
            ? $"{DeployTexts.TemplateName(steps)} con PHP {target.Php.Version?.ToString() ?? target.Php.Name} · en {target.Folder}"
            : framework is null ? $"Una carpeta sin proyecto reconocido · {target.Folder}"
            : kind.FrameworkLabel is not null ? $"Parece un proyecto {framework}{evidence}" : $"Un proyecto {framework}";

        var version = target.Php.Version?.ToString() ?? target.Php.Name;
        var why = DeployTexts.PhpReason(target.PhpReason);
        PhpLine = mode == DeployDialogMode.Template ? ""
            : kind.PhpConstraint is { } constraint
                ? $"PHP: {version} {why} · composer.json pide {constraint} {(DeployPlan.PhpFits(constraint, target.Php.Version) switch { true => "✓", false => "✗ no lo cumple: cambia el PHP del sitio", _ => "" })}".TrimEnd()
                : $"PHP: {version} {why}";
        PhpFits = DeployPlan.PhpFits(kind.PhpConstraint, target.Php.Version) != false;

        Note = DeployTexts.Warning(steps, mode);
        ResultText = Rows.Count == 0 ? "No hay pasos que proponer. Se sirve tal cual." : "";
        UpdateEditable();
    }

    public override bool Wide => true;

    /// <summary>Mientras corre, ni Escape ni el fondo lo cierran: o termina, o se cancela.</summary>
    public override bool CanDismiss => !Running;

    public DeployDialogMode Mode { get; }

    public bool IsTemplate => Mode == DeployDialogMode.Template;

    public bool IsAfterClone => Mode == DeployDialogMode.AfterClone;

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>«PHP: 8.4.27 (el global) · composer.json pide ^8.2 ✓»; vacío con una plantilla.</summary>
    public string PhpLine { get; }

    public bool HasPhpLine => PhpLine.Length > 0;

    /// <summary>El PHP cumple lo que pide el composer.json (o no se sabe).</summary>
    public bool PhpFits { get; }

    public IReadOnlyList<DeployStepRow> Rows { get; }

    public bool HasSteps => Rows.Count > 0;

    public bool NoSteps => Rows.Count == 0;

    /// <summary>El aviso de que Composer y artisan ejecutan scripts del proyecto; null si ningún paso lo hace.</summary>
    public string? Note { get; }

    public bool HasNote => Note is not null;

    public string Intro => IsTemplate ? "Roost lo deja listo así:" : "Roost puede dejarlo listo. Marca lo que quieras:";

    public bool CanCopy => _copy is not null;

    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Todavía no se ha ejecutado nada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(ShowOffer))]
    public partial bool Idle { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOffer), nameof(ShowRetry), nameof(ShowClose), nameof(ShowUndo), nameof(CanRun))]
    public partial bool Running { get; private set; }

    /// <summary>Se ha ejecutado y han ido bien todos los marcados.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry), nameof(ShowClose), nameof(ShowUndo), nameof(CloseLabel))]
    public partial bool Succeeded { get; private set; }

    /// <summary>Con una plantilla acabada, cerrar es seguir hasta el navegador.</summary>
    public string CloseLabel => IsTemplate && Succeeded ? "Abrir el sitio" : "Cerrar";

    /// <summary>Un paso falló o se canceló: se puede reintentar desde ahí.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry), nameof(ShowClose), nameof(ShowUndo))]
    public partial bool Stopped { get; private set; }

    [ObservableProperty] public partial bool ShowOutput { get; private set; }

    [ObservableProperty] public partial string Status { get; private set; } = "";

    [ObservableProperty] public partial string ResultText { get; private set; }

    [ObservableProperty] public partial Tone ResultTone { get; private set; } = Tone.Accent;

    /// <summary>Las últimas líneas del paso que falló, en mono.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailureTail))]
    public partial string FailureTail { get; private set; } = "";

    public bool HasFailureTail => FailureTail.Length > 0;

    public bool HasResult => ResultText.Length > 0;

    partial void OnResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowNote));
    }

    /// <summary>«Omitir» y «Ejecutar»: antes de empezar (con una plantilla no, que empieza sola).</summary>
    public bool ShowOffer => Idle && !Running && !IsTemplate && HasSteps;

    public bool CanRun => Idle && !Running && Rows.Any(r => r.IsChecked && r.Available);

    public bool ShowRetry => Stopped && !Running;

    /// <summary>«Cerrar»: al acabar bien o, fuera de una plantilla, tras un fallo; y cuando no hay pasos.</summary>
    public bool ShowClose => !Running && (Succeeded || Stopped && !IsTemplate || NoSteps);

    /// <summary>Plantilla que no se ha acabado: «Deshacer y cerrar».</summary>
    public bool ShowUndo => Stopped && !Running && IsTemplate;

    public string RunLabel => IsAfterClone ? "Ejecutar los pasos" : "Ejecutar";

    /// <summary>Toda la salida, para copiarla.</summary>
    public string Output
    {
        get
        {
            FlushLines();
            lock (_kept) return string.Join(Environment.NewLine, _kept);
        }
    }

    /// <summary>Con una plantilla: empieza solo. Lanzarlo una vez, antes o después de enseñar el diálogo.</summary>
    public Task StartAsync() => RunFromAsync(0);

    [RelayCommand]
    private Task RunAsync() => CanRun ? RunFromAsync(0) : Task.CompletedTask;

    [RelayCommand]
    private Task RetryAsync() => _failedIndex >= 0 && !Running ? RunFromAsync(_failedIndex) : Task.CompletedTask;

    [RelayCommand]
    private void Skip()
    {
        if (!Running) Close(new DeployDialogResult(false, false));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Running)
        {
            Status = "Cancelando…";
            _cancel?.Cancel();
        }
        else
        {
            Close(Result);
        }
    }

    [RelayCommand]
    private void Finish()
    {
        if (!Running) Close(Result);
    }

    /// <summary>Plantilla a medias: se cierra sin acabar (quien lo abrió quita lo creado).</summary>
    [RelayCommand]
    private void Undo()
    {
        if (!Running) Close(new DeployDialogResult(true, false));
    }

    [RelayCommand]
    private Task CopyAsync() => _copy is null ? Task.CompletedTask : _copy(Output);

    private DeployDialogResult Result => new(!Idle, Succeeded);

    private async Task RunFromAsync(int start)
    {
        if (Running) return;
        Running = true;
        Idle = false;
        Stopped = false;
        ShowOutput = true;
        FailureTail = "";
        ResultText = "";
        _failedIndex = -1;
        UpdateEditable();
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        var watch = Stopwatch.StartNew();
        var done = 0;
        var skipped = 0;
        try
        {
            for (var i = start; i < Rows.Count; i++)
            {
                var row = Rows[i];
                if (!row.IsChecked || !row.Available) continue;
                row.State = DeployRowState.Running;
                row.StateText = "en marcha…";
                Status = $"{row.Label}…";
                Enqueue(new LogLine($"> {row.Header}", LogLevel.Muted));

                DeployOutcome outcome;
                try
                {
                    outcome = await _run(row.Step with { Checked = true }, new LineSink(this), cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    FlushLines();
                    row.State = DeployRowState.Canceled;
                    row.StateText = "cancelado";
                    _failedIndex = i;
                    Stopped = true;
                    (ResultText, ResultTone) = (DeployTexts.Canceled(row.Label, Mode), Tone.Warn);
                    return;
                }

                FlushLines();
                switch (outcome.Kind)
                {
                    case DeployOutcomeKind.Done:
                        row.State = DeployRowState.Done;
                        row.StateText = "hecho";
                        done++;
                        break;
                    case DeployOutcomeKind.Skipped:
                        row.State = DeployRowState.Skipped;
                        row.StateText = DeployTexts.Skipped(row.Step);
                        skipped++;
                        break;
                    default:
                        row.State = DeployRowState.Failed;
                        row.StateText = outcome.ExitCode is { } code ? $"ha fallado (código {code})" : "ha fallado";
                        _failedIndex = i;
                        Stopped = true;
                        FailureTail = string.Join(Environment.NewLine, outcome.Tail);
                        (ResultText, ResultTone) = (DeployTexts.Failure(row.Label, _site, Mode), Tone.Bad);
                        return;
                }
            }

            Succeeded = true;
            (ResultText, ResultTone) = (DeployTexts.Success(done, skipped, watch.Elapsed, Mode), Tone.Ok);
        }
        finally
        {
            Running = false;
            _cancel = null;
            Status = Succeeded ? "Terminado" : Stopped ? "Parado" : "";
            UpdateEditable();
        }
    }

    /// <summary>
    /// Antes de empezar se puede tocar cualquiera; tras un fallo, el que falló y los de después (para
    /// saltarse uno o añadir otro antes de «Reintentar desde aquí»); corriendo, ninguno.
    /// </summary>
    private void UpdateEditable()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            row.Editable = row.Available && !row.Step.Required && !Running && (Idle || Stopped && i >= _failedIndex);
        }
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ShowNote));
    }

    /// <summary>El aviso de los scripts, mientras no haya un resultado que contar.</summary>
    public bool ShowNote => HasNote && !HasResult;

    /// <summary>Pasa a <see cref="Lines"/> lo que ha llegado (en el hilo de la UI).</summary>
    internal void FlushLines()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        while (_pending.TryDequeue(out var line))
        {
            lock (_kept) _kept.Add(line.Text);
            if (Lines.Count > 0 && CommandOutputDialogViewModel.IsProgressUpdate(Lines[^1].Text, line.Text))
                Lines[^1] = line;
            else
                Lines.Add(line);
        }
        while (Lines.Count > MaxShown) Lines.RemoveAt(0);
    }

    private void Enqueue(LogLine line)
    {
        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            Dispatcher.UIThread.Post(FlushLines, DispatcherPriority.Background);
    }

    private sealed class LineSink(DeployStepsDialogViewModel owner) : IProgress<string>
    {
        public void Report(string value) => owner.Enqueue(new LogLine(value, CommandOutputDialogViewModel.LevelOf(value)));
    }
}

/// <summary>Los textos de los pasos en la app (con tildes): los de Core son los de la CLI.</summary>
internal static class DeployTexts
{
    public static string Label(DeployStep step) => step.Kind switch
    {
        DeployStepKind.WpLoopback => "Que WordPress se fíe de los certificados .test",
        _ => step.Label,
    };

    public static string Hint(DeployStep step, DeployTarget target)
    {
        var db = target.Database;
        var root = db?.Password is null ? "root sin contraseña" : "root con la contraseña que guarda Roost";
        return step.Kind switch
        {
            DeployStepKind.CreateProject when step.Id == "create" && step.Command is null =>
                $"La última versión de wordpress.org en {target.Folder}, comprobada con su SHA-1 antes de descomprimir.",
            DeployStepKind.CreateProject when step.Label.Contains("symfony", StringComparison.Ordinal) =>
                $"{(target.Php.Version is { } v && (v.Major > 8 || v.Major == 8 && v.Minor >= 4) ? "Symfony 8.1" : "Symfony 7.4 (LTS)")} en {target.Folder}"
                + (step.Label.Contains("webapp", StringComparison.Ordinal) ? ", con webapp (Twig, Doctrine, formularios y seguridad)." : "."),
            DeployStepKind.CreateProject => $"{ProjectTemplates.Laravel.Version} en {target.Folder}. Los paquetes vienen de Packagist.",
            DeployStepKind.Database => $"En {db?.Label} (utf8mb4).",
            DeployStepKind.ComposerInstall => step.HasVendor ? "vendor/ ya está: márcalo solo si quieres repetirlo."
                : step.HasLock ? "Instala las dependencias (hay composer.lock)."
                : "No hay composer.lock: resuelve las versiones y lo crea.",
            DeployStepKind.Env when step.EnvMode == EnvMode.Complete => step.MissingKeys.Count == 0
                ? $"Ya tiene todo lo de Roost: no hay nada que añadir."
                : $"Ya hay {step.File}: se añaden {Keys(step.MissingKeys)}; lo que ya tiene no se toca.",
            DeployStepKind.Env when step.File == ".env.local" =>
                (db is null ? $"DEFAULT_URI={target.Url}" : $"DATABASE_URL=mysql://root@127.0.0.1:{db.Port}/{db.Name} ({db.Label})")
                + (target.MailSmtpPort is { } smtp ? $"; MAILER_DSN a Mailpit ({smtp})" : "") + ". .env.local no se sube a git.",
            DeployStepKind.Env => (step.EnvMode == EnvMode.New ? "No hay .env.example: uno mínimo con " : "Con ")
                + (db is null ? "" : $"la base de datos de Roost: 127.0.0.1:{db.Port}, {root}, {db.Name}; ")
                + $"APP_URL={target.Url}"
                + (target.MailSmtpPort is not null ? "; el correo a Mailpit" : "")
                + (target.RedisPort is not null ? step.PredisMissing ? "; Redis con predis (el proyecto aún no lo tiene: composer require predis/predis)" : "; Redis con predis" : "")
                + ".",
            DeployStepKind.AppKey => target.Fresh
                ? "La clave de la aplicación (si create-project ya la ha puesto, se deja)."
                : "La clave de la aplicación (APP_KEY está vacía).",
            DeployStepKind.Migrate => step.DbState switch
            {
                DbState.Stopped => $"La base de datos está parada: arranca Roost para migrar {db?.Name}.",
                DbState.Missing => $"En {db?.Name}, recién creada.",
                DbState.Empty => $"La base de datos {db?.Name} está vacía.",
                DbState.HasTables => $"{db?.Name} ya tiene {step.Tables} {(step.Tables == 1 ? "tabla" : "tablas")}: márcalo solo si de verdad quieres migrar.",
                _ => $"No se ha podido mirar si {db?.Name} está vacía: márcalo si quieres migrar.",
            },
            DeployStepKind.StorageLink => "Enlaza public/storage con storage/app/public (una unión de Windows: no pide permisos).",
            DeployStepKind.WpConfig => db is null
                ? "No hay base de datos en Roost: el instalador de WordPress te la pedirá."
                : $"{db.Name} en {WpConfig.Host(db.Port)}, {root}; las 8 claves y salts, nuevas.",
            DeployStepKind.WpLoopback => "Un mu-plugin: las peticiones de WordPress a su propio https://….test (Salud del sitio, WP-Cron) usan el bundle de CA de Roost.",
            DeployStepKind.Npm => NodeTexts.DeployHint(step, target),
            _ => step.Hint,
        };
    }

    /// <summary>«Laravel», «Symfony» o «WordPress», por el paso que crea el proyecto.</summary>
    public static string TemplateName(IReadOnlyList<DeployStep> steps) =>
        steps.FirstOrDefault(s => s.Kind == DeployStepKind.CreateProject) is { } create
            ? create.Command is null ? "WordPress" : create.Label.Contains("symfony", StringComparison.Ordinal) ? "Symfony" : "Laravel"
            : "Proyecto";

    public static string PhpReason(string reason) =>
        reason.Contains("el sitio", StringComparison.Ordinal) && reason.Contains("usa PHP", StringComparison.Ordinal) ? "(el del sitio)"
        : reason.Contains(".php-version", StringComparison.Ordinal) ? "(el de .php-version)"
        : reason.Contains("elegido", StringComparison.Ordinal) ? "(el elegido)"
        : "(el global)";

    /// <summary>El aviso de los scripts, según qué ejecuten los pasos; null si ninguno ejecuta código del proyecto.</summary>
    public static string? Warning(IReadOnlyList<DeployStep> steps, DeployDialogMode mode)
    {
        if (!steps.Any(s => s.RunsProjectCode)) return null;
        if (mode == DeployDialogMode.Template)
            return "Composer baja los paquetes de Packagist y ejecuta los scripts de la plantilla (los de su composer.json), como al hacerlo a mano.";
        var who = new List<string>();
        if (steps.Any(s => s.Kind == DeployStepKind.ComposerInstall)) who.Add("composer install");
        if (steps.Any(s => s.Kind == DeployStepKind.Npm && s.Available)) who.Add(NodeTool.Name(steps.First(s => s.Kind == DeployStepKind.Npm).NodeManager));
        if (steps.Any(s => s.Command?.StartsWith("php artisan", StringComparison.Ordinal) == true)) who.Add("php artisan");
        if (steps.Any(s => s.Command?.StartsWith("php bin/console", StringComparison.Ordinal) == true)) who.Add("bin/console");
        var subject = who.Count switch
        {
            0 => "Estos pasos ejecutan",
            1 => $"{who[0]} ejecuta",
            _ => $"{string.Join(", ", who.Take(who.Count - 1))} y {who[^1]} ejecutan",
        };
        return $"{subject} scripts del propio proyecto. Hazlo solo con repositorios de confianza.";
    }

    public static string Skipped(DeployStep step) => step.Kind switch
    {
        DeployStepKind.AppKey => "saltado: ya tenía APP_KEY",
        DeployStepKind.StorageLink => "saltado: public/storage ya estaba",
        DeployStepKind.WpConfig => "saltado: ya había wp-config.php",
        DeployStepKind.Database => "saltado: ya existía",
        DeployStepKind.Env => "saltado: ya tenía todo",
        DeployStepKind.WpLoopback => "saltado: ya estaba",
        _ => "saltado",
    };

    /// <summary>El texto del diseño para un paso que falla (con tildes).</summary>
    public static string Failure(string step, string site, DeployDialogMode mode) => mode switch
    {
        DeployDialogMode.AfterClone => $"{step} ha fallado en {site} (ver las últimas líneas). El sitio está clonado y registrado: arréglalo y repite el paso con «Reintentar desde aquí» (o más tarde, con «Pasos de instalación…» en su panel).",
        DeployDialogMode.Template => $"{step} ha fallado (ver las últimas líneas). Arréglalo y pulsa «Reintentar desde aquí»; si lo dejas, Roost quita lo que ha creado en este intento: la carpeta nueva, el sitio y su base de datos.",
        _ => $"{step} ha fallado en {site} (ver las últimas líneas). El sitio sigue registrado: arréglalo y repite el paso con «Reintentar desde aquí».",
    };

    public static string Canceled(string step, DeployDialogMode mode) => mode == DeployDialogMode.Template
        ? $"Cancelado en «{step}»: se ha parado el proceso. «Reintentar desde aquí» sigue por ese paso; si lo dejas, Roost quita lo que ha creado en este intento."
        : $"Cancelado en «{step}»: se ha parado el proceso y todo lo que había lanzado. Lo que llegó a hacer, hecho está. «Reintentar desde aquí» sigue por ese paso.";

    public static string Success(int done, int skipped, TimeSpan elapsed, DeployDialogMode mode)
    {
        var took = elapsed.TotalSeconds < 60
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} s", elapsed.TotalSeconds).Replace('.', ',')
            : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";
        var what = skipped == 0
            ? $"{done} {(done == 1 ? "paso" : "pasos")}"
            : $"{done} {(done == 1 ? "paso hecho" : "pasos hechos")} y {skipped} {(skipped == 1 ? "saltado" : "saltados")}";
        return mode == DeployDialogMode.Template ? $"Listo: {what} en {took}. Ahora se abre en el navegador." : $"Listo: {what} en {took}.";
    }

    private static string Keys(IReadOnlyList<string> keys) =>
        keys.Count <= 3 ? string.Join(", ", keys) : $"{string.Join(", ", keys.Take(2))} y {keys.Count - 2} más";
}
