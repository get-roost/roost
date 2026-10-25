using System.Diagnostics;
using Avalonia.Threading;
using Tmds.DBus.Protocol;

namespace Roost.Desktop.Services;

/// <summary>
/// Los avisos de escritorio en Linux (J4, K5): <c>org.freedesktop.Notifications</c> por el bus de
/// sesión, que es lo que atienden GNOME, KDE, XFCE, dunst, mako… El aviso lleva una acción
/// <c>default</c> para que al pulsarlo (<c>ActionInvoked</c>) la app abra la pantalla que toca, y el
/// demonio dice con <c>NotificationClosed</c> cuándo se ha ido. Si el bus no contesta, se intenta
/// <c>notify-send</c> (sin clic); si tampoco, el aviso se queda solo en el log de la app.
/// </summary>
public sealed class FreedesktopNotifier : ITrayNotifier
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private DBusConnection? _connection;
    private IDisposable? _actions;
    private IDisposable? _closed;
    private uint _current;
    private Action? _onClick;

    /// <summary>Lo que lanza <c>notify-send</c>: los tests apuntan la orden sin lanzar nada.</summary>
    internal Action<ProcessStartInfo> Launcher { get; init; } = start => Process.Start(start)?.Dispose();

    public void Show(TrayAlert alert, Action onClick)
    {
        if (!OperatingSystem.IsLinux()) return;
        _ = ShowAsync(alert, onClick);
    }

    private async Task ShowAsync(TrayAlert alert, Action onClick)
    {
        try
        {
            var connection = await ConnectAsync();
            _onClick = onClick;
            _current = await connection.CallMethodAsync(Notify(connection, alert, _current), static (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null)
                .WaitAsync(TimeSpan.FromSeconds(5));
            AppLog.Info($"Aviso de escritorio: {alert.Title} (#{_current})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn($"No se pudo enseñar el aviso por DBus ({ex.Message}); se prueba con notify-send");
            Disconnect();
            Fallback(alert);
        }
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        if (_connection is not null) return _connection;
        var address = DBusAddress.Session ?? throw new InvalidOperationException("no hay bus de sesión (DBUS_SESSION_BUS_ADDRESS)");
        var connection = new DBusConnection(address);
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        // Al pulsar el aviso (la acción default) se abre lo que toca; al cerrarse, se olvida el clic.
        _actions = await connection.WatchSignalAsync(null, ObjectPath, Service, "ActionInvoked",
            static (Message m, object? _) => { var r = m.GetBodyReader(); return (Id: r.ReadUInt32(), Action: r.ReadString()); },
            (Notification<(uint Id, string Action)> n) => { if (n.HasValue) OnAction(n.Value.Id, n.Value.Action); },
            ObserverFlags.None, false, null);
        _closed = await connection.WatchSignalAsync(null, ObjectPath, Service, "NotificationClosed",
            static (Message m, object? _) => m.GetBodyReader().ReadUInt32(),
            (Notification<uint> n) => { if (n.HasValue) OnClosed(n.Value); },
            ObserverFlags.None, false, null);
        return _connection = connection;
    }

    private void OnAction(uint id, string action)
    {
        if (id != _current) return;
        var click = _onClick;
        _onClick = null;
        if (click is not null) Dispatcher.UIThread.Post(click);
    }

    private void OnClosed(uint id)
    {
        if (id == _current) _onClick = null;
    }

    private static MessageBuffer Notify(DBusConnection connection, TrayAlert alert, uint replaces)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Notify", "susssasa{sv}i");
        writer.WriteString("Roost");
        writer.WriteUInt32(replaces);
        writer.WriteString("roost");   // el icono del tema (el del .desktop), si está instalado
        writer.WriteString(alert.Title);
        writer.WriteString(alert.Text);
        var actions = writer.WriteArrayStart(DBusType.String);
        writer.WriteString("default");
        writer.WriteString("Abrir Roost");
        writer.WriteArrayEnd(actions);
        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("urgency");
        writer.WriteVariantByte(alert.Kind == TrayAlertKind.ServiceDown ? (byte)2 : (byte)1);   // crítico (no caduca) o normal
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString("roost");
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(-1);   // lo que diga el demonio
        return writer.CreateMessage();
    }

    /// <summary>PURO: la orden de <c>notify-send</c> equivalente (sin clic).</summary>
    internal static ProcessStartInfo NotifySend(TrayAlert alert)
    {
        var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--app-name=Roost", "--icon=roost", alert.Kind == TrayAlertKind.ServiceDown ? "--urgency=critical" : "--urgency=normal", alert.Title, alert.Text })
            start.ArgumentList.Add(arg);
        return start;
    }

    private void Fallback(TrayAlert alert)
    {
        try
        {
            Launcher(NotifySend(alert));
            AppLog.Info($"Aviso de escritorio (notify-send): {alert.Title}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Tampoco hay notify-send: el aviso «{alert.Title}» se queda en este registro ({ex.Message})");
        }
    }

    private void Disconnect()
    {
        _actions?.Dispose();
        _closed?.Dispose();
        _connection?.Dispose();
        _actions = null;
        _closed = null;
        _connection = null;
    }

    public void Dispose() => Disconnect();
}
