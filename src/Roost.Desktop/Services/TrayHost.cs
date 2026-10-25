using Tmds.DBus.Protocol;

namespace Roost.Desktop.Services;

/// <summary>Si este escritorio tiene bandeja para Roost, y si no, por qué.</summary>
/// <param name="Available">true si hay dónde poner el icono (Windows siempre; Linux, con un anfitrión StatusNotifier).</param>
/// <param name="Reason">Por qué no la hay, para decirlo en Ajustes; null si la hay.</param>
public sealed record TrayHostStatus(bool Available, string? Reason)
{
    public static readonly TrayHostStatus Present = new(true, null);
}

/// <summary>
/// La bandeja del sistema en Linux es el protocolo StatusNotifierItem por DBus (KDE, XFCE, Cinnamon,
/// MATE, LXQt…), que es lo que usa el <c>TrayIcon</c> de Avalonia. Sin un anfitrión que lo atienda
/// (GNOME sin la extensión AppIndicator, un WSLg, una sesión sin bus) el icono no sale en ningún sitio,
/// y «cerrar deja Roost en la bandeja» dejaría la app en ninguna parte: entonces la X cierra la app, y
/// Ajustes lo dice. Se pregunta al bus de sesión una vez al arrancar: si existe
/// <c>org.kde.StatusNotifierWatcher</c> y tiene algún anfitrión registrado
/// (<c>IsStatusNotifierHostRegistered</c>). Nunca lanza.
/// </summary>
public static class TrayHost
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher";

    /// <summary>Lo que se sabe (lo deja <see cref="DetectAsync"/>; hasta entonces, como si hubiera bandeja).</summary>
    public static TrayHostStatus Status { get; private set; } = TrayHostStatus.Present;

    /// <summary>true si cerrar la ventana puede dejar la app en la bandeja: la preferencia y que haya dónde.</summary>
    public static bool CanCloseToTray(DesktopPrefs prefs) => prefs.CloseToTray && Status.Available;

    /// <summary>Mira si hay bandeja en este escritorio y lo deja en <see cref="Status"/>.</summary>
    public static async Task<TrayHostStatus> DetectAsync()
    {
        if (OperatingSystem.IsWindows()) return Status = TrayHostStatus.Present;
        if (!OperatingSystem.IsLinux()) return Status = new TrayHostStatus(false, "En este sistema Roost aún no tiene bandeja.");
        Status = await DetectLinuxAsync();
        if (!Status.Available) AppLog.Info($"Sin bandeja: {Status.Reason}");
        return Status;
    }

    private static async Task<TrayHostStatus> DetectLinuxAsync()
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
            return new TrayHostStatus(false, "No hay bus de sesión (DBUS_SESSION_BUS_ADDRESS): sin él no hay bandeja ni avisos de escritorio.");
        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (!await connection.CallMethodAsync(NameHasOwner(connection), static (Message m, object? _) => m.GetBodyReader().ReadBool(), null).WaitAsync(TimeSpan.FromSeconds(3)))
                return new TrayHostStatus(false, Explain(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")));
            var hosted = await connection.CallMethodAsync(HostRegistered(connection), static (Message m, object? _) => m.GetBodyReader().ReadVariantValue().GetBool(), null).WaitAsync(TimeSpan.FromSeconds(3));
            return hosted ? TrayHostStatus.Present
                : new TrayHostStatus(false, "Hay un StatusNotifierWatcher, pero ningún panel se ha registrado como anfitrión: el icono no se vería en ningún sitio.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TrayHostStatus(false, $"No se ha podido preguntar al bus de sesión por la bandeja ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>PURO: por qué no hay StatusNotifierWatcher, según el escritorio (<c>XDG_CURRENT_DESKTOP</c>).</summary>
    public static string Explain(string? desktop)
    {
        var d = desktop ?? "";
        if (d.Contains("GNOME", StringComparison.OrdinalIgnoreCase) || d.Contains("ubuntu", StringComparison.OrdinalIgnoreCase))
            return "GNOME no tiene bandeja de serie: instala la extensión AppIndicator (gnome-shell-extension-appindicator) y vuelve a entrar en la sesión.";
        if (d.Length == 0)
            return "No hay ningún panel con bandeja (StatusNotifierWatcher) en esta sesión; en WSLg no la hay.";
        return $"El escritorio ({d}) no tiene ningún panel con bandeja (StatusNotifierWatcher).";
    }

    private static MessageBuffer NameHasOwner(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s");
        writer.WriteString(WatcherName);
        return writer.CreateMessage();
    }

    private static MessageBuffer HostRegistered(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(WatcherName, "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties", "Get", "ss");
        writer.WriteString(WatcherName);
        writer.WriteString("IsStatusNotifierHostRegistered");
        return writer.CreateMessage();
    }
}
