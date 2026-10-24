using System.Globalization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Laragon;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// BD-2: «Exportar…» por base de datos, «Importar…» (a una nueva o a una que existe) y traer las de
/// Laragon desde la bienvenida, todo con el diálogo de progreso en vivo y Cancelar
/// (<see cref="DbTransferDialogViewModel"/>). Lo hace Core: <see cref="DbDump"/> y <see cref="LaragonDumper"/>.
/// </summary>
public sealed partial class DatabasesViewModel
{
    /// <summary>El volcador y el cliente (los tests ponen uno de pega).</summary>
    internal ICommandRunner Runner { get; set; } = CommandRunner.Default;

    /// <summary>El cliente para las consultas cortas: listar, crear, mirar una base de datos (los tests lo cambian).</summary>
    internal IProcessRunner DbClient { get; set; } = ProcessRunner.Default;

    /// <summary>Dónde guardar una exportación (los tests lo cambian; por defecto, «Guardar como» del sistema).</summary>
    internal Func<string, Task<string?>>? PickExportFile { get; set; }

    /// <summary>Qué fichero importar (los tests lo cambian; por defecto, «Abrir» del sistema).</summary>
    internal Func<Task<string?>>? PickImportFile { get; set; }

    /// <summary>Lo que hace Laragon (los tests ponen uno con sondas de pega).</summary>
    internal Func<LaragonDumper>? LaragonDumperFactory { get; set; }

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private string ToolName => _global is null ? "el volcador" : Path.GetFileName(_global.Dump);

    // --- Exportar ---

    [RelayCommand]
    private async Task ExportAsync(DatabaseLine line)
    {
        if (_global is null || !DbRunning)
        {
            await Main.InfoAsync("La base de datos no está en marcha", "Se exporta con ella arrancada: pulsa Iniciar todo.", Tone.Warn);
            return;
        }
        var file = await (PickExportFile ?? DefaultPickExportFile)(line.Name);
        if (file is null) return;

        var db = _global;
        var tool = DbTool.For(db, IniPath);
        var compressed = DbDump.IsGzipName(file);
        var dialog = new DbTransferDialogViewModel(
            $"Exportando {line.Name}",
            $"{db.Label} → {file}",
            [
                new InstallStep($"Volcar {line.Name} con {ToolName}", "· tablas, vistas, rutinas, triggers y eventos"),
                new InstallStep($"Guardar {Path.GetFileName(file)}", compressed ? "· comprimido al vuelo" : null),
            ],
            async (d, cancel) =>
            {
                d.Step(0, "Volcando");
                var written = new Progress<DbTransferProgress>(p => d.Progress(p, "volcados"));
                var result = await DbDump.ExportAsync(tool, line.Name, file, Runner, written, new Progress<string>(d.Line), cancel);
                d.Step(1, "Guardado");
                return $"Exportada {line.Name} a {Path.GetFileName(file)}: {DbTransferDialogViewModel.Size(result.Bytes, Es)}{(result.Compressed ? " comprimido" : "")}";
            })
        {
            Note = "Si cancelas o falla, no queda ningún fichero a medias: se escribe aparte y se renombra al acabar.",
            CanceledText = "Cancelado: se ha parado el volcado y no ha quedado ningún fichero a medias.",
        };
        var run = dialog.RunAsync();
        await Main.ShowDialogAsync(dialog);
        await run;
        if (dialog.Succeeded) Main.Flash($"Exportada {line.Name} a {file}");
    }

    private Task<string?> DefaultPickExportFile(string database) =>
        Main.SaveFileAsync($"Exportar {database}", DbDump.SuggestedFileName(database, DateTime.Now),
            [("Volcado SQL", "sql"), ("Volcado SQL comprimido", "gz")]);

    // --- Importar ---

    [RelayCommand]
    private async Task ImportAsync(string? database)
    {
        if (_global is null || !DbRunning)
        {
            await Main.InfoAsync("La base de datos no está en marcha", "Se importa con ella arrancada: pulsa Iniciar todo.", Tone.Warn);
            return;
        }
        var file = await (PickImportFile ?? DefaultPickImportFile)();
        if (file is null) return;

        var db = _global;
        var ini = IniPath;
        DumpInfo info;
        CompatPlan plan;
        try
        {
            info = await DbDump.SniffAsync(file);
            if (!info.LooksLikeSql)
                throw new RoostException($"{Path.GetFileName(file)} no parece un volcado SQL: Roost importa .sql, .sql.gz o un .zip con un solo .sql dentro.");
            IReadOnlySet<string>? collations = null;
            try { collations = await Task.Run(() => DbAdmin.Collations(db, ini, DbClient)); } catch (RoostException) { }
            plan = SqlCompat.PlanFor(db.Engine, db.Version, collations);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Main.InfoAsync($"No se puede importar {Path.GetFileName(file)}", ex.Message, Tone.Bad);
            return;
        }

        var dialog = new DbImportDialogViewModel(file, new FileInfo(file).Length, info, db.Label, Databases.Select(d => d.Name).ToList(),
            SqlCompat.Issues(info, plan), database);
        if (await Main.ShowDialogAsync(dialog) is not DbImportChoice choice) return;

        if (!choice.Create)
        {
            DbSchemaInfo target;
            try { target = await Task.Run(() => DbAdmin.Inspect(db, ini, choice.Target, DbClient)); }
            catch (RoostException ex)
            {
                await Main.InfoAsync($"No se pudo mirar {choice.Target}", ex.Message, Tone.Bad);
                return;
            }
            if (!target.IsEmpty && !await Main.ConfirmAsync($"{choice.Target} ya tiene {target.Tables} {(target.Tables == 1 ? "tabla" : "tablas")}",
                    "Las que también vengan en el fichero se sustituyen (el volcado las borra antes de crearlas) y el resto se queda. No se puede deshacer.",
                    "Importar encima", danger: true))
                return;
        }

        await RunImportAsync(db, ini, file, choice, choice.Compat ? plan : null);
    }

    private async Task RunImportAsync(InstalledDb db, string ini, string file, DbImportChoice choice, CompatPlan? plan)
    {
        var tool = DbTool.For(db, ini);
        var steps = new List<InstallStep>();
        if (choice.Create) steps.Add(new InstallStep($"Crear {choice.Target}", "· utf8mb4"));
        steps.Add(new InstallStep($"Importar {Path.GetFileName(file)} en {choice.Target}", plan is null ? "· por el cliente, en streaming" : "· adaptándolo al vuelo"));
        steps.Add(new InstallStep("Comprobar lo importado", null));
        var importStep = choice.Create ? 1 : 0;

        var dialog = new DbTransferDialogViewModel($"Importando en {choice.Target}", $"{Path.GetFileName(file)} → {db.Label}", steps, async (d, cancel) =>
        {
            var created = false;
            try
            {
                if (choice.Create)
                {
                    d.Step(0, "Creando");
                    await Task.Run(() => DbAdmin.Create(db, ini, choice.Target, DbClient), cancel);
                    created = true;
                }
                d.Step(importStep, "Importando");
                var read = new Progress<DbTransferProgress>(p => d.Progress(p));
                var result = await DbDump.ImportAsync(tool, choice.Target, file, plan, Runner, read, new Progress<string>(d.Line), cancel);
                foreach (var note in result.Notes) d.AddNote(note);
                d.Step(importStep + 1, "Comprobando");
                var tables = await Task.Run(() => DbAdmin.Inspect(db, ini, choice.Target, DbClient).Tables);
                return $"Importado {Path.GetFileName(file)} en {choice.Target}: {tables} {(tables == 1 ? "tabla" : "tablas")}";
            }
            catch (Exception ex) when (ex is OperationCanceledException or RoostException or IOException or InvalidDataException)
            {
                var undo = await UndoImportAsync(db, ini, choice.Target, created);
                d.CanceledText = $"Cancelado: se ha parado el cliente. {undo}";
                if (ex is OperationCanceledException) throw;
                if (plan is null && ex.Message.Contains("--compat", StringComparison.Ordinal))
                    d.AddNote("Vuelve a importarlo con «Adaptarlo al vuelo» marcado.");
                d.AddNote(undo);
                throw;
            }
        })
        {
            Note = choice.Create
                ? "Si falla o cancelas, la base de datos nueva se borra: no queda nada a medias."
                : "Si falla o cancelas, lo que ya se haya importado se queda.",
        };

        var run = dialog.RunAsync();
        await Main.ShowDialogAsync(dialog);
        await run;
        await LoadServerAsync();
        if (dialog.Succeeded) Main.Flash($"Importado en {choice.Target}");
    }

    /// <summary>Si se creó para esto, se borra (no queda nada a medias); si ya existía, lo dice.</summary>
    private async Task<string> UndoImportAsync(InstalledDb db, string ini, string target, bool created)
    {
        if (!created) return $"{target} puede haber quedado a medias: lo que llegó a importarse, ahí está.";
        try
        {
            await Task.Run(() => DbAdmin.Drop(db, ini, target, DbClient));
            return $"Se ha borrado {target}, que se había creado para esto: no queda nada a medias.";
        }
        catch (RoostException)
        {
            return $"{target} se había creado para esto y no se ha podido borrar: bórrala desde la lista.";
        }
    }

    private async Task<string?> DefaultPickImportFile()
    {
        if (Main.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar un volcado SQL",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Volcados SQL (.sql, .sql.gz, .zip)") { Patterns = ["*.sql", "*.sql.gz", "*.gz", "*.zip"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    // --- Las de Laragon (la bienvenida) ---

    /// <summary>
    /// Trae a la base de datos de Roost las bases de datos elegidas de Laragon: las vuelca sin tocar
    /// Laragon (<see cref="LaragonDumper"/>) y las importa, cada una con su nombre. Si alguna ya
    /// existe, pregunta. Si no se puede (Roost sin base de datos en marcha, Laragon que pide
    /// contraseña…), lo dice y vuelve: la bienvenida sigue.
    /// </summary>
    public async Task<bool> ImportFromLaragonAsync(IReadOnlyList<LaragonDatabase> databases, string? password)
    {
        Build();
        if (databases.Count == 0) return true;
        if (_global is null || !DbRunning)
        {
            await Main.InfoAsync("No se han traído las bases de datos de Laragon",
                _global is null
                    ? "Roost no tiene ninguna base de datos todavía. Instala una en Bases de datos y luego tráelas desde Ajustes › Importar desde Laragon…"
                    : "La base de datos de Roost no está en marcha. Cuando lo esté, tráelas desde Ajustes › Importar desde Laragon…",
                Tone.Warn);
            return false;
        }

        var db = _global;
        var ini = IniPath;
        var names = databases.Select(d => d.Name).ToList();
        List<string> existing;
        try { existing = (await Task.Run(() => names.Where(n => DbAdmin.Inspect(db, ini, n, DbClient).Exists).ToList())); }
        catch (RoostException ex)
        {
            await Main.InfoAsync("No se han traído las bases de datos de Laragon", ex.Message, Tone.Bad);
            return false;
        }

        var replace = false;
        if (existing.Count > 0)
        {
            var one = existing.Count == 1;
            var answer = await Main.ShowDialogAsync(new MessageDialogViewModel
            {
                Title = one ? $"{existing[0]} ya existe en Roost" : $"Ya existen en Roost: {string.Join(", ", existing)}",
                Subtitle = $"En {db.Label}",
                Text = one
                    ? "Puedes saltarla (se queda la de Roost) o reemplazarla por la de Laragon: se borra la de Roost, ya volcada la de Laragon, y se trae esa. Lo de Laragon no se toca nunca."
                    : "Puedes saltarlas (se quedan las de Roost) o reemplazarlas por las de Laragon: se borran las de Roost, ya volcadas las de Laragon, y se traen esas. Lo de Laragon no se toca nunca.",
                Tone = Tone.Warn,
                Buttons = [new DialogButton("Cancelar", null, Left: true), new DialogButton(one ? "Saltarla" : "Saltarlas", "skip", "primary"), new DialogButton(one ? "Reemplazarla" : "Reemplazarlas", "replace", "danger-fill")],
            });
            if (answer is null) return false;
            replace = answer is "replace";
            if (!replace) databases = databases.Where(d => !existing.Contains(d.Name)).ToList();
        }
        if (databases.Count == 0)
        {
            Main.Flash("No había nada que traer de Laragon");
            return true;
        }

        var server = databases[0].Server;
        var dumper = LaragonDumperFactory?.Invoke() ?? new LaragonDumper(Main.App.Paths, Runner);
        var collations = await Task.Run(() => { try { return DbAdmin.Collations(db, ini, DbClient); } catch (RoostException) { return null; } });
        var plan = SqlCompat.PlanFor(db.Engine, db.Version, collations);
        var tool = DbTool.For(db, ini);
        var done = new List<string>();
        var skipped = existing.Where(_ => !replace).ToList();

        var dialog = new DbTransferDialogViewModel(
            "Trayendo las bases de datos de Laragon",
            $"{server.Label} de Laragon → {db.Label}: {string.Join(", ", databases.Select(d => d.Name))}",
            [new InstallStep($"Ver si el {server.Label} de Laragon está en marcha", $"· en el puerto {server.Port}")],
            async (d, cancel) =>
            {
                d.Step(0, "Mirando");
                var current = 0;
                void Next(string label, string? hint, string phase)
                {
                    current = d.AddStep(label, hint);
                    d.Step(current, phase);
                    d.ResetProgress();
                }
                // Cada etapa abre su paso una sola vez y en orden; los avisos de progreso que llegan
                // tarde (de otro hilo) solo mueven la barra si siguen en su etapa.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stage = "check";
                var progress = new UiProgress<LaragonProgress>(p =>
                {
                    var key = p.Stage switch
                    {
                        LaragonStage.Copying => "copy",
                        LaragonStage.Starting => "start",
                        LaragonStage.Dumping => "dump:" + p.Database,
                        LaragonStage.Cleaning => "clean",
                        _ => "check",
                    };
                    if (seen.Add(key))
                    {
                        stage = key;
                        switch (p.Stage)
                        {
                            case LaragonStage.Copying: Next("Copiar sus datos a una carpeta de Roost", "· Laragon está parado: se lee una copia", "Copiando"); break;
                            case LaragonStage.Starting: Next($"Arrancar su {server.Label} sobre la copia", $"· solo en 127.0.0.1:{p.Port}", "Arrancando la copia"); break;
                            case LaragonStage.Dumping: Next($"Volcar {p.Database}", null, "Volcando"); break;
                            case LaragonStage.Cleaning: Next("Parar la copia y borrarla", null, "Limpiando"); break;
                        }
                    }
                    if (key != stage) return;
                    if (p.Stage == LaragonStage.Copying && p.Total is > 0) d.Progress(new DbTransferProgress(p.Bytes, p.Total));
                    else if (p.Stage == LaragonStage.Dumping && p.Bytes > 0) d.Progress(new DbTransferProgress(p.Bytes, null), "volcados");
                });

                LaragonDumps dumps;
                try
                {
                    dumps = await dumper.DumpAsync(server, databases.Select(x => x.Name).ToList(), password, progress, new Progress<string>(d.Line), cancel);
                }
                catch (LaragonPasswordException)
                {
                    throw new RoostException(string.IsNullOrEmpty(password)
                        ? $"El {server.Label} de Laragon pide la contraseña de root. Vuelve a Ajustes › Importar desde Laragon… y escríbela en «Contraseña de root de Laragon»."
                        : $"La contraseña de root no vale para el {server.Label} de Laragon. Vuelve a Ajustes › Importar desde Laragon… y escríbela otra vez.");
                }
                using var _ = dumps;
                d.AddNote(dumps.Mode == LaragonMode.Live
                    ? $"Laragon estaba en marcha: se ha volcado de su servidor, solo leyendo."
                    : $"Laragon estaba parado: se ha volcado de una copia de sus datos, ya borrada.");

                foreach (var name in databases.Select(x => x.Name))
                {
                    stage = "import:" + name;
                    Next($"Importar {name} en Roost", null, "Importando");
                    var file = dumps.Files[name];
                    // El volcado ya está hecho entero: solo ahora se quita la de Roost que se reemplaza.
                    if (replace && existing.Contains(name)) await Task.Run(() => DbAdmin.Drop(db, ini, name, DbClient), cancel);
                    var created = false;
                    try
                    {
                        await Task.Run(() => DbAdmin.Create(db, ini, name, DbClient), cancel);
                        created = true;
                        var info = await DbDump.SniffAsync(file, cancel);
                        var issues = SqlCompat.Issues(info, plan);
                        var result = await DbDump.ImportAsync(tool, name, file, issues.Count > 0 ? plan : null, Runner,
                            new Progress<DbTransferProgress>(p => d.Progress(p)), new Progress<string>(d.Line), cancel);
                        foreach (var note in result.Notes) d.AddNote($"{name}: {note}");
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or RoostException or IOException or InvalidDataException)
                    {
                        var undo = await UndoImportAsync(db, ini, name, created);
                        if (done.Count > 0) d.AddNote($"Ya traídas: {string.Join(", ", done)}.");
                        d.AddNote(undo);
                        throw;
                    }
                    var tables = await Task.Run(() => DbAdmin.Inspect(db, ini, name, DbClient).Tables);
                    done.Add($"{name} ({(tables == 0 ? "vacía" : tables == 1 ? "1 tabla" : $"{tables} tablas")})");
                }
                return $"Traídas de Laragon: {string.Join(", ", done)}{(skipped.Count > 0 ? $". Saltadas, porque ya estaban: {string.Join(", ", skipped)}" : "")}";
            })
        {
            Note = "Laragon no se toca: si está en marcha, solo se leen sus datos; si no, se lee una copia que se borra al acabar.",
            FailureText = null,
        };
        dialog.CanceledText = "Cancelado: se ha parado lo que estaba en marcha (y la copia de Laragon, si la había, se ha borrado).";

        var run = dialog.RunAsync();
        await Main.ShowDialogAsync(dialog);
        await run;
        await LoadServerAsync();
        return dialog.Succeeded;
    }
}
