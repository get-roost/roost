using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Updates;
using Roost.Desktop.Controls;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Services;

/// <summary>
/// Si hay una Roost nueva: lo mismo que <c>roost update</c>, comprobado en segundo plano al abrir
/// la app y cada pocas horas (con la caché de 24 h, si no se ha desactivado en Ajustes ni con
/// <c>ROOST_NO_UPDATE_CHECK</c>) y cuando se pide. De aquí salen el aviso de la barra lateral, la
/// línea de la bandeja y lo que cuenta Ajustes; los tres abren el modal «Roost X disponible»
/// (<see cref="OpenAsync"/>), desde el que se actualiza (<see cref="UpdateFlow"/>).
/// </summary>
public sealed partial class UpdateState : ObservableObject
{
    private static readonly CultureInfo Es = Roost.Desktop.Services.Es.Culture;

    private readonly AppServices _app;
    private readonly UpdateChecker _checker;

    public UpdateState(AppServices app)
    {
        _app = app;
        _checker = new UpdateChecker(app.Paths, app.Http, app.Version);
        Installer = new UpdateInstaller(app.Paths, app.Http);
        Channel = UpdateInstructions.DetectCurrent();
        // Lo que ya se sabía (sin red): el aviso sale nada más abrir, aunque luego no haya conexión.
        Result = _checker.Cached();
    }

    /// <summary>La versión que se está ejecutando.</summary>
    public string Installed => _checker.Installed;

    /// <summary>Descarga, verifica y compone la línea del instalador (Core).</summary>
    public UpdateInstaller Installer { get; }

    /// <summary>Quien lanza el setup, el vigilante y la terminal. Los tests ponen uno de mentira.</summary>
    internal UpdateLauncher Launcher { get; set; } = UpdateLauncher.Default;

    /// <summary>Cómo sale la app de verdad para dejar paso al instalador; null = la salida normal de Avalonia (los tests ponen otra).</summary>
    internal Action? ExitApp { get; set; }

    /// <summary>Cómo se instaló esta Roost (Scoop, winget, un zip, el instalador, compilada). Los tests de vistas lo cambian.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Advice), nameof(HowText), nameof(Command), nameof(HasCommand),
        nameof(DownloadUrl), nameof(DownloadText), nameof(HasDownload), nameof(CanInstall), nameof(UpdateLabel))]
    public partial InstallChannel Channel { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(Latest), nameof(LatestVersion), nameof(SideText), nameof(BannerTitle),
        nameof(NotesLines), nameof(HasNotes), nameof(Advice), nameof(HowText), nameof(Command), nameof(HasCommand),
        nameof(DownloadUrl), nameof(DownloadText), nameof(HasDownload), nameof(StatusText), nameof(StatusTone), nameof(CanInstall), nameof(UpdateLabel))]
    public partial UpdateCheck? Result { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusTone), nameof(CheckLabel))]
    public partial bool Checking { get; private set; }

    public bool HasUpdate => Result?.UpdateAvailable == true;

    public ReleaseInfo? Latest => Result?.Latest;

    public string? LatestVersion => Latest?.Version.ToString();

    /// <summary>El aviso de la barra lateral.</summary>
    public string SideText => $"Roost {LatestVersion} disponible";

    public string BannerTitle => Latest is { } l
        ? $"Roost {l.Version} ya está disponible{(l.PublishedAt is { } at ? $" desde el {at.ToLocalTime().ToString("d-MMM-yyyy", Es)}" : "")}"
        : "";

    /// <summary>Las notas de la release en corto (las enteras, en el modal y en su página).</summary>
    public IReadOnlyList<string> NotesLines => HasUpdate ? GitHubReleases.Summarize(Latest?.Notes, maxLines: 4) : [];

    public bool HasNotes => NotesLines.Count > 0;

    public UpdateAdvice Advice => UpdateInstructions.For(Channel, Latest, OperatingSystem.IsWindows());

    /// <summary>Con el instalador y una release que lo trae, «Actualizar ahora» lo hace todo desde la app.</summary>
    public bool CanInstall => HasUpdate && UpdateInstaller.InstallsFromApp(Channel) && Latest?.Installer is not null;

    /// <summary>El botón del aviso de Ajustes: lo que hará el modal en este canal.</summary>
    public string UpdateLabel => Channel switch
    {
        InstallChannel.Manual => "Descargar y verificar…",
        InstallChannel.Source => "Ver cómo actualizar",
        _ => "Actualizar ahora…",
    };

    /// <summary>Cómo actualizar, según cómo se instaló.</summary>
    public string HowText => Advice switch
    {
        { Channel: InstallChannel.Scoop } => "La instalaste con Scoop. «Actualizar ahora» para el stack, cierra Roost y abre una terminal que ejecuta esto y vuelve a abrir Roost:",
        { Channel: InstallChannel.Winget } => "La instalaste con winget. «Actualizar ahora» para el stack, cierra Roost y abre una terminal que ejecuta esto y vuelve a abrir Roost:",
        { Channel: InstallChannel.Source } => "La compilaste desde el código. Para actualizarla, para el stack y ejecuta en la carpeta del repositorio:",
        { Channel: InstallChannel.Installer, Package: not null } => "La instalaste con el instalador. «Actualizar ahora» descarga el nuevo, lo verifica y lo ejecuta: Roost se cierra un momento, Windows pide permiso una vez y vuelve solo. Tus datos y tus sitios no se tocan.",
        { Channel: InstallChannel.Installer } => "La instalaste con el instalador. Esta versión no trae instalador: descarga el nuevo de su página y ejecútalo. Tus datos y tus sitios no se tocan.",
        { Package: { } package } => $"La descomprimiste de un zip. Roost descarga {package} y lo verifica; después, con Roost cerrado y el stack parado, sustituye con su contenido la carpeta de Roost.",
        _ => "Descárgala de su página y, con Roost cerrado y el stack parado, sustituye con ella la carpeta de Roost.",
    };

    public string? Command => Advice.Command;

    public bool HasCommand => HasUpdate && Command is not null;

    /// <summary>Lo que hay que bajar a mano: el zip de esta release si Roost se descomprimió de uno (el instalador lo baja la app).</summary>
    public Uri? DownloadUrl => Advice.Channel == InstallChannel.Manual ? Latest?.PackageFor(OperatingSystem.IsWindows())?.Url : null;

    public string DownloadText => Advice.Package is { } package ? $"Descargar {package}" : "Descargar";

    public bool HasDownload => HasUpdate && DownloadUrl is not null;

    public string CheckLabel => Checking ? "Buscando…" : "Buscar actualizaciones";

    /// <summary>La línea de estado de «Acerca de Roost».</summary>
    public string StatusText
    {
        get
        {
            if (Checking) return "Buscando la última versión en GitHub…";
            if (Result is not { } r) return "Aún no se ha comprobado si hay una versión nueva.";
            var when = r.CheckedAt is { } at ? $" · comprobado {Ago(at, DateTimeOffset.Now)}" : "";
            return r switch
            {
                { Status: UpdateStatus.Failed } => $"No se pudo comprobar: {r.Error?.TrimEnd('.')}."
                    + (r.Latest is { } l ? $" La última que se conocía es la {l.Version}{when}." : ""),
                { Status: UpdateStatus.NoReleases } => $"Aún no hay ninguna versión publicada{when}",
                { UpdateAvailable: true } => $"Hay una versión nueva: {r.Latest!.Name}{when}",
                { InstalledIsNewer: true } => $"Esta build es más nueva que la última publicada ({r.Latest!.Version}){when}",
                _ => $"Estás en la última versión{when}",
            };
        }
    }

    public Tone StatusTone => Checking ? Tone.Neutral : Result switch
    {
        { UpdateAvailable: true } => Tone.Accent,
        { Status: UpdateStatus.Failed } => Tone.Warn,
        { Status: UpdateStatus.UpToDate } => Tone.Ok,
        _ => Tone.Neutral,
    };

    /// <summary>Rehace «comprobado hace…» (al entrar en Ajustes: la app puede llevar días en la bandeja).</summary>
    public void RefreshText() => OnPropertyChanged(nameof(StatusText));

    /// <summary>Al abrir la app y cada pocas horas: con la caché de 24 h, y solo si no se ha desactivado.</summary>
    public Task AutoCheckAsync() =>
        _app.Prefs.CheckUpdates && !UpdateChecker.DisabledByEnvironment ? CheckAsync(force: false) : Task.CompletedTask;

    private Task<UpdateCheck>? _check;

    /// <summary>Comprueba (nunca lanza). Si ya hay una comprobación en curso, espera a esa.</summary>
    public Task<UpdateCheck> CheckAsync(bool force) => Checking && _check is not null ? _check : _check = CheckCoreAsync(force);

    private async Task<UpdateCheck> CheckCoreAsync(bool force)
    {
        Checking = true;
        try
        {
            var result = await Task.Run(() => _checker.CheckAsync(force));
            Result = result;
            return result;
        }
        finally
        {
            Checking = false;
        }
    }

    /// <summary>El modal «Roost X disponible» y, si se acepta, la actualización del canal que toque.</summary>
    public Task OpenAsync(MainViewModel main) => new UpdateFlow(main).OpenAsync();

    /// <summary>Al abrir la app: «Roost actualizado a X» o «No se ha actualizado», según cómo vuelva.</summary>
    public Task HandleStartupAsync(MainViewModel main, StartupArgs args) => new UpdateFlow(main).HandleStartupAsync(args);

    /// <summary>«ahora mismo», «hace 5 min», «hace 3 h», «ayer», «hace 4 días».</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "ahora mismo";
        if (age < TimeSpan.FromHours(1)) return $"hace {(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromHours(24)) return $"hace {(int)age.TotalHours} h";
        if (age < TimeSpan.FromHours(48)) return "ayer";
        return $"hace {(int)age.TotalDays} días";
    }
}
