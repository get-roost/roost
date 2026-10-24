using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Roost.Desktop.Services;

/// <summary>Quien enseña los avisos de la bandeja (los tests ponen uno que solo los apunta).</summary>
public interface ITrayNotifier : IDisposable
{
    /// <param name="onClick">Al pulsar el aviso (abre la ventana en la pantalla que toca).</param>
    void Show(TrayAlert alert, Action onClick);
}

/// <summary>
/// Los avisos de la bandeja de verdad (J4, K5): un globo de <c>Shell_NotifyIcon</c>, que Windows 10
/// y 11 enseñan como una notificación (y dejan en el centro de notificaciones). Avalonia no da
/// globos en su <c>TrayIcon</c>, así que se pone un icono propio y efímero, con su ventana de
/// mensajes solo para recibir el clic, y se quita en cuanto el globo se cierra (o a los 30 s).
/// En Windows 11 puede verse un segundo icono de Roost en la bandeja mientras dura el aviso.
/// Todo va en el hilo de la UI, que es el que tiene el bucle de mensajes de Win32.
/// </summary>
public sealed class TrayBalloon : ITrayNotifier
{
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const uint NiifInfo = 0x1, NiifWarning = 0x2, NiifError = 0x3;
    private const uint NotifyIconVersion4 = 4;
    private const uint CallbackMessage = 0x8000 + 0x52;   // WM_APP + algo
    private const int NinSelect = 0x400, NinBalloonHide = 0x403, NinBalloonTimeout = 0x404, NinBalloonUserClick = 0x405;
    private const int WmLButtonUp = 0x202;
    private const uint IconId = 0x5257;   // «RW»: lo único que distingue este icono del de Avalonia

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProc _proc;   // que el recolector no se lo lleve mientras Windows lo llama
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _shown;
    private Action? _onClick;
    private DispatcherTimer? _timeout;

    public TrayBalloon() => _proc = OnMessage;

    public void Show(TrayAlert alert, Action onClick)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(alert, onClick));
            return;
        }
        try
        {
            EnsureWindow();
            _onClick = onClick;
            var data = Data();
            data.uFlags = NifMessage | NifIcon | NifTip | NifInfo;
            data.szInfoTitle = Cut(alert.Title, 63);
            data.szInfo = Cut(alert.Text, 255);
            data.dwInfoFlags = alert.Kind == TrayAlertKind.ServiceDown ? NiifError : NiifWarning;
            if (_shown)
            {
                Shell_NotifyIconW(NimModify, ref data);
            }
            else
            {
                _shown = Shell_NotifyIconW(NimAdd, ref data);
                data.uVersion = NotifyIconVersion4;
                Shell_NotifyIconW(NimSetVersion, ref data);
            }
            AppLog.Info($"Aviso en la bandeja: {alert.Title}");

            _timeout?.Stop();
            _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timeout.Tick += (_, _) => Hide();
            _timeout.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            AppLog.Warn($"No se pudo enseñar el aviso de la bandeja: {ex.Message}");
        }
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        var module = GetModuleHandleW(null);
        var className = "RoostTrayBalloon-" + Environment.ProcessId;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = module,
            lpszClassName = className,
        };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, className, "Roost", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, module, IntPtr.Zero);
        var exe = Environment.ProcessPath;
        if (exe is not null && ExtractIconExW(exe, 0, IntPtr.Zero, out var small, 1) > 0) _icon = small;
        if (_icon == IntPtr.Zero) _icon = LoadIconW(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
    }

    private NotifyIconData Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = IconId,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "Roost",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            // Con NOTIFYICON_VERSION_4, el evento va en la palabra baja de lParam.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NinBalloonUserClick or NinSelect or WmLButtonUp:
                    var click = _onClick;
                    Hide();
                    click?.Invoke();
                    break;
                case NinBalloonTimeout or NinBalloonHide:
                    Hide();
                    break;
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Hide()
    {
        _timeout?.Stop();
        _onClick = null;
        if (!_shown) return;
        var data = Data();
        Shell_NotifyIconW(NimDelete, ref data);
        _shown = false;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        Hide();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // Win32

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;   // unión con uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr large, out IntPtr small, uint icons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? module);
}
