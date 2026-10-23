using CommunityToolkit.Mvvm.Input;
using Roost.Core.Config;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Un puerto ocupado, contado para personas: quién lo tiene y qué suele ser.</summary>
public sealed record BusyPortView(BusyPort Port, string Headline, string Owner, string? UseLabel, string? UseHint);

/// <summary>
/// «No se pudo arrancar…»: qué ha fallado, el final de los logs y lo que se puede hacer
/// (usar un puerto libre, ir a Ajustes, reintentar). Tras un up fallido no queda nada a medias.
/// </summary>
public sealed partial class StartErrorDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;

    public StartErrorDialogViewModel(MainViewModel main, StartFailure failure)
    {
        _main = main;
        Title = failure.Title;
        // El mensaje del daemon trae el final de los logs detrás de la primera línea.
        var newline = failure.Message.IndexOf('\n');
        Message = newline < 0 ? failure.Message : failure.Message[..newline];
        LogTail = newline < 0 ? null : failure.Message[(newline + 1)..].Trim();
        Ports = failure.BusyPorts.Select(Describe).ToList();
    }

    public override bool Wide => true;

    public string Title { get; }

    public string Subtitle => "No se ha arrancado nada: Roost no deja el stack a medias.";

    public string Message { get; }

    public string? LogTail { get; }

    public bool HasLogTail => LogTail is not null;

    public IReadOnlyList<BusyPortView> Ports { get; }

    public bool HasPorts => Ports.Count > 0;

    private BusyPortView Describe(BusyPort port)
    {
        var owner = port.Owner is { } o
            ? $"Lo tiene {o.ProcessName ?? "otro proceso"} (PID {o.Pid}){Hint(o.ProcessName)}."
            : "Lo tiene otro proceso, que Windows no deja identificar.";
        string? useLabel = null, useHint = null;
        if (port.Suggestion is { } free)
        {
            useLabel = $"Usar el puerto {free}";
            useHint = port.ConfigKey switch
            {
                "[web] port" => $"localhost pasaría a http://localhost:{free}/ (el {free} está libre)",
                "[web] https_port" => $"Los sitios pasarían a https://<sitio>.test:{free} (el {free} está libre)",
                _ => $"Las aplicaciones tendrán que conectar al {free} (está libre)",
            };
        }
        return new BusyPortView(port, $"El puerto {port.Port} ({port.Role}) ya está en uso", owner, useLabel, useHint);
    }

    private static string Hint(string? process) => process?.ToLowerInvariant() switch
    {
        "system" => ": suele ser http.sys, de IIS o de otro servicio de Windows",
        "mysqld" or "mariadbd" => ": otro MySQL o MariaDB, quizá un servicio de Windows",
        "httpd" => ": un Apache, quizá de XAMPP o Laragon",
        "nginx" => ": otro nginx, quizá de Laragon",
        "php" => ": un servidor de PHP (php -S)",
        "putty" => ": un túnel de PuTTY",
        "skype" or "teams" => ": Skype o Teams",
        "vmware-hostd" or "vmware-authd" => ": VMware",
        _ => "",
    };

    [RelayCommand]
    private async Task UsePortAsync(BusyPortView view)
    {
        if (view.Port.Suggestion is not { } free) return;
        await _main.RunAsync("No se pudo cambiar el puerto", async () =>
        {
            var file = _main.App.Paths.ConfigFile;
            var config = RoostConfig.Load(file);
            switch (view.Port.ConfigKey)
            {
                case "[web] port": RoostConfig.SetWebPorts(file, free, config.HttpsPort); break;
                case "[web] https_port": RoostConfig.SetWebPorts(file, config.HttpPort, free); break;
                default: RoostConfig.SetDbPort(file, free); break;
            }
            Close();
            await _main.StartStackAsync();
        });
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        Close();
        await _main.StartStackAsync();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        Close();
        await _main.NavigateToAsync(_main.Settings);
    }

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        Close();
        await _main.NavigateToAsync(_main.Logs);
    }
}
