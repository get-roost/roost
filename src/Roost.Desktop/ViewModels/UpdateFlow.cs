using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Roost.Core;
using Roost.Core.Updates;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Actualizar Roost desde la app (ROOST.md, decisión 33). Con el instalador: el modal, el diálogo
/// de pasos (descargar, verificar el SHA-256 con el SHA256SUMS.txt de la release, comprobar la
/// firma), apuntar si el stack estaba en marcha, lanzar el setup en silencio con el vigilante
/// detrás, y salir de verdad (aunque «cerrar deja en la bandeja» esté activado). El instalador
/// hace <c>roost down</c>, sustituye los ficheros y relanza la app con <c>--updated</c>; al volver,
/// <see cref="HandleStartupAsync"/> lo cuenta y arranca el stack si tocaba.
/// </summary>
public sealed class UpdateFlow(MainViewModel main)
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private UpdateState Updates => main.Updates;

    private AppServices App => main.App;

    /// <summary>El modal «Roost X disponible» y, si se acepta, lo que toque en este canal.</summary>
    public async Task OpenAsync()
    {
        if (!Updates.HasUpdate || Updates.Latest is not { } latest)
        {
            await main.NavigateToAsync(main.Settings);
            return;
        }
        var modal = new UpdateDialogViewModel(Updates);
        if (await main.ShowDialogAsync(modal) is not true) return;

        switch (modal.Channel)
        {
            case InstallChannel.Installer when modal.Asset is { } setup:
                await InstallAsync(latest, setup);
                break;
            case InstallChannel.Scoop:
                await UpdateWithCommandAsync(latest, UpdateInstructions.ScoopCommand);
                break;
            case InstallChannel.Winget:
                await UpdateWithCommandAsync(latest, UpdateInstructions.WingetCommand);
                break;
            case InstallChannel.Manual when modal.Asset is { } zip:
                await DownloadZipAsync(latest, zip);
                break;
        }
    }

    private static string Mb(long bytes) => string.Format(Es, "{0:0.0} MB", bytes / 1048576.0);

    /// <summary>El canal del instalador: descargar, verificar, lanzar y salir.</summary>
    private async Task InstallAsync(ReleaseInfo latest, ReleaseAsset setup)
    {
        var installer = Updates.Installer;
        var steps = new List<InstallStep>
        {
            new($"Descargar {setup.Name}", setup.Size > 0 ? $"· {Mb(setup.Size)} desde {setup.Url.Host}" : $"· desde {setup.Url.Host}"),
            new("Verificar el SHA-256", "· con el SHA256SUMS.txt de la misma release"),
            new("Comprobar la firma digital", "· si el instalador va firmado, tiene que ser válida"),
            new("Lanzar el instalador", "· Roost se cierra un momento; Windows te pedirá permiso"),
        };
        var dialog = new UpdateProgressDialogViewModel(
            $"Actualizando a Roost {latest.Version}",
            $"de la {Updates.Installed} a la {latest.Version} · desde {setup.Url.Host}",
            setup.Size > 0 ? setup.Size : null,
            steps,
            "Roost se cerrará un momento y Windows te pedirá permiso una vez. Si algo falla antes, no se ejecuta nada y Roost sigue como está; si falla después, Roost vuelve y te lo dice.",
            async (d, cancel) =>
            {
                d.Step(0, "Descargando");
                var expected = await installer.ExpectedSha256Async(latest, setup, cancel);
                var download = await installer.DownloadAsync(setup, expected, new Progress<long>(d.Downloaded), cancel);
                d.Step(2, "Firma comprobada");
                d.Step(3, "Lanzando el instalador");

                // Lo que cuenta el sondeo (cada 3 s): si estaba en marcha, el instalador lo vuelve a arrancar.
                var running = main.Stack.IsRunning;
                UpdateMarker.Write(App.Paths, new UpdateMarker(Updates.Installed, latest.Version.ToString(), running, DateTimeOffset.Now, installer.SetupLogFile));
                Directory.CreateDirectory(App.Paths.Logs);
                var pid = Updates.Launcher.StartSetup(download.Path, UpdateInstaller.SetupArguments(relaunchApp: true, startStack: running, installer.SetupLogFile));
                Updates.Launcher.StartWatchdog(pid, AppExe);
                d.Phase = "Instalador en marcha: Roost se cierra y vuelve al terminar…";
                Exit();
            })
        { CloseOnSuccess = false };

        var run = dialog.RunAsync();
        await main.ShowDialogAsync(dialog);
        await run;
    }

    /// <summary>Scoop y winget: parar el stack, apuntar, salir y abrir una terminal que actualiza y relanza la app.</summary>
    private async Task UpdateWithCommandAsync(ReleaseInfo latest, string command)
    {
        await main.RunAsync("No se pudo preparar la actualización", async () =>
        {
            var running = main.Stack.IsRunning;
            if (running) await main.Stack.StopAsync();
            UpdateMarker.Write(App.Paths, new UpdateMarker(Updates.Installed, latest.Version.ToString(), running, DateTimeOffset.Now));
            Updates.Launcher.StartTerminal(TerminalScript(command, RelaunchExe, running));
            Exit();
        });
    }

    /// <summary>
    /// PURO: lo que ejecuta la terminal. Si el gestor de paquetes acaba bien, vuelve a abrir Roost
    /// (con <c>--start</c> si el stack estaba en marcha); si no, lo dice y deja la ventana para leer el error.
    /// </summary>
    public static string TerminalScript(string command, string appExe, bool startStack)
    {
        var args = startStack ? "'--updated','--start'" : "'--updated'";
        return $"{command}; if ($?) {{ Start-Process -FilePath '{appExe.Replace("'", "''")}' -ArgumentList {args} }} "
            + "else { Write-Host ''; Write-Host 'No se ha actualizado Roost. Cuando lo arregles, vuelve a abrir Roost.' -ForegroundColor Yellow }";
    }

    /// <summary>El zip: descargarlo verificado y decir qué hacer con él.</summary>
    private async Task DownloadZipAsync(ReleaseInfo latest, ReleaseAsset zip)
    {
        var installer = Updates.Installer;
        VerifiedDownload? downloaded = null;
        var dialog = new UpdateProgressDialogViewModel(
            $"Descargando Roost {latest.Version}",
            $"{zip.Name} · desde {zip.Url.Host}",
            zip.Size > 0 ? zip.Size : null,
            [
                new($"Descargar {zip.Name}", zip.Size > 0 ? $"· {Mb(zip.Size)}" : null),
                new("Verificar el SHA-256", "· con el SHA256SUMS.txt de la misma release"),
            ],
            "Solo se descarga y se verifica: nada se sustituye hasta que lo hagas tú, con Roost cerrado y el stack parado.",
            async (d, cancel) =>
            {
                d.Step(0, "Descargando");
                var expected = await installer.ExpectedSha256Async(latest, zip, cancel);
                downloaded = await installer.DownloadAsync(zip, expected, new Progress<long>(d.Downloaded), cancel);
                d.Step(1, "Verificado");
            });
        var run = dialog.RunAsync();
        await main.ShowDialogAsync(dialog);
        await run;
        if (downloaded is not { } file) return;

        var choice = await main.ShowDialogAsync(new MessageDialogViewModel
        {
            Title = $"{zip.Name} descargado y verificado",
            Subtitle = "SHA-256 comprobado con el de la release",
            Text = "Para actualizar: sal de Roost (clic derecho en el icono de la bandeja → «Salir de Roost», parando el stack), "
                   + $"descomprime el zip y sustituye con su contenido la carpeta de Roost. Después abre roost-desktop.exe. La actualización automática de este canal llegará más adelante.",
            Detail = $"Zip:     {file.Path}\nCarpeta: {AppServices.AppDirectory}",
            Tone = Tone.Ok,
            Buttons = [new DialogButton("Mostrar en el explorador", "reveal", Left: true), new DialogButton("Cerrar", null, "primary")],
        });
        if (choice is "reveal")
            await main.RunAsync("No se pudo abrir el explorador", () => { Shell.RevealFile(file.Path); return Task.CompletedTask; });
    }

    /// <summary>Al abrir la app: si venía de una actualización, decirlo (y arrancar el stack si tocaba).</summary>
    public async Task HandleStartupAsync(StartupArgs args)
    {
        var marker = UpdateMarker.TryRead(App.Paths);
        if (marker is null && !args.Updated && args.UpdateFailedCode is null) return;
        UpdateMarker.Delete(App.Paths);
        var current = Updates.Installed;

        var failed = args.UpdateFailedCode is not null
            || (!args.Updated && marker is not null && marker.Outcome(current) == UpdateOutcome.NotUpdated);
        if (failed)
        {
            var why = args.UpdateFailedCode is { } code ? UpdateLauncher.DescribeSetupExit(code) : "el instalador no llegó a ejecutarse";
            var log = marker?.SetupLog ?? Updates.Installer.SetupLogFile;
            var hasLog = File.Exists(log);
            var choice = await main.ShowDialogAsync(new MessageDialogViewModel
            {
                Title = "No se ha actualizado",
                Subtitle = $"Sigues en la {current}" + (marker is not null ? $"; la {marker.To} queda pendiente" : ""),
                Text = char.ToUpperInvariant(why[0]) + why[1..] + ". Nada ha cambiado: tus sitios y tu stack están como estaban. "
                       + "Puedes volver a intentarlo desde el aviso de la barra lateral o desde Ajustes.",
                Detail = hasLog ? $"Registro del instalador: {log}" : null,
                Tone = Tone.Warn,
                Buttons = hasLog
                    ? [new DialogButton("Ver el registro", "log", Left: true), new DialogButton("Cerrar", null, "primary")]
                    : [new DialogButton("Cerrar", null, "primary")],
            });
            if (choice is "log") await main.OpenLogAsync(Path.GetFileName(log));
            return;
        }

        main.Flash($"Roost actualizado a {current}");
        var start = (args.StartStack || marker?.StackWasRunning == true) && !main.Stack.IsRunning ? main.StartStackAsync() : Task.CompletedTask;
        var page = Updates.Latest is { } latest && latest.Version.ToString() == current ? latest.Page : null;
        var seen = await main.ShowDialogAsync(new MessageDialogViewModel
        {
            Title = $"Roost actualizado a la {current}",
            Subtitle = marker is not null ? $"Venías de la {marker.From}" : null,
            Text = "Tus sitios, tus PHP, tus bases de datos y tus datos siguen como estaban"
                   + (start != Task.CompletedTask ? ", y el stack se está arrancando como lo tenías." : "."),
            Tone = Tone.Ok,
            Buttons = page is not null
                ? [new DialogButton("Ver las novedades", "notes", Left: true), new DialogButton("Cerrar", null, "primary")]
                : [new DialogButton("Cerrar", null, "primary")],
        });
        if (seen is "notes" && page is not null)
            await main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(page.ToString()); return Task.CompletedTask; });
        await start;
    }

    /// <summary>La app de esta carpeta (la que relanza el vigilante).</summary>
    private static string AppExe => Path.Combine(AppServices.AppDirectory, OperatingSystem.IsWindows() ? "roost-desktop.exe" : "roost-desktop");

    /// <summary>
    /// La app que hay que relanzar tras Scoop o winget: con Scoop, la de <c>current\</c> (el enlace
    /// que Scoop apunta a la versión nueva), aunque esta corra desde la carpeta de su versión.
    /// </summary>
    private static string RelaunchExe
    {
        get
        {
            var exe = AppExe;
            var parts = exe.Split(Path.DirectorySeparatorChar);
            for (var i = 0; i + 2 < parts.Length; i++)
            {
                if (parts[i].Equals("apps", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("roost", StringComparison.OrdinalIgnoreCase))
                {
                    parts[i + 2] = "current";
                    return string.Join(Path.DirectorySeparatorChar, parts);
                }
            }
            return exe;
        }
    }

    /// <summary>Salir de verdad: ni a la bandeja ni preguntando por el stack (eso ya se decidió aquí).</summary>
    private void Exit()
    {
        if (Updates.ExitApp is { } exit)
        {
            exit();
            return;
        }
        main.ExitRequested = true;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
