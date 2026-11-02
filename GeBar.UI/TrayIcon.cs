using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace GeBar.UI;

public sealed partial class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1;
    private const uint Add = 0, Modify = 1, Delete = 2;
    private const uint HasMessage = 0x01, HasIcon = 0x02, HasTip = 0x04, HasBalloon = 0x10;
    private const int LeftDoubleClick = 0x0203, RightButtonUp = 0x0205, BalloonClick = 0x0405;
    private const int PopupWindow = unchecked((int)0x80000000);
    public const int TooltipLimit = 127;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private NotifyIconData _data;
    private IntPtr _icon;
    private bool _hidden;
    private bool _disposed;

    public ContextMenu? Menu { get; set; }
    public event EventHandler? DoubleClicked;
    public event EventHandler? BalloonClicked;

    public TrayIcon(string tooltip)
    {
        _window = new HwndSource(0, PopupWindow, 0, 0, 0, 1, 1, nameof(TrayIcon), IntPtr.Zero);
        _window.AddHook(OnMessage);
        _taskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = HasMessage | HasTip,
            uCallbackMessage = CallbackMessage,
            szTip = Trim(tooltip, TooltipLimit),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
        Send(Add);
    }

    public string Tooltip
    {
        get => _data.szTip;
        set
        {
            string trimmed = Trim(value, TooltipLimit);
            if (string.Equals(_data.szTip, trimmed, StringComparison.Ordinal)) return;
            _data.szTip = trimmed;
            Update();
        }
    }

    public void SetIcon(IntPtr icon)
    {
        IntPtr previous = _icon;
        _icon = icon;
        _data.hIcon = icon;
        _data.uFlags = HasMessage | HasTip | HasIcon;
        Update();
        if (previous != IntPtr.Zero) DestroyIcon(previous);
    }

    public void ShowBalloon(string title, string text)
    {
        uint flags = _data.uFlags;
        _data.uFlags = flags | HasBalloon;
        _data.szInfoTitle = Trim(title, 63);
        _data.szInfo = Trim(text, 255);
        Update();
        _data.uFlags = flags;
        _data.szInfoTitle = string.Empty;
        _data.szInfo = string.Empty;
    }

    public void Hide()
    {
        if (_hidden) return;
        _hidden = true;
        Send(Delete);
    }

    private void Update()
    {
        if (!Send(Modify) && !_hidden) Send(Add);
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreated && !_hidden)
        {
            Send(Add);
            handled = true;
            return IntPtr.Zero;
        }
        if (message != CallbackMessage) return IntPtr.Zero;
        switch ((int)lParam & 0xFFFF)
        {
            case LeftDoubleClick: DoubleClicked?.Invoke(this, EventArgs.Empty); break;
            case RightButtonUp: OpenMenu(); break;
            case BalloonClick: BalloonClicked?.Invoke(this, EventArgs.Empty); break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void OpenMenu()
    {
        if (Menu is null) return;
        SetForegroundWindow(_window.Handle);
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
    }

    private bool Send(uint message)
    {
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NotifyIconData>());
        try
        {
            Marshal.StructureToPtr(_data, buffer, false);
            return ShellNotifyIcon(message, buffer);
        }
        finally
        {
            Marshal.DestroyStructure<NotifyIconData>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string Trim(string value, int limit) => value.Length <= limit ? value : value[..limit];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.RemoveHook(OnMessage);
        _window.Dispose();
    }

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
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);
}
