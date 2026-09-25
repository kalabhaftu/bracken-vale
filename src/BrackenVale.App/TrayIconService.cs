using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BrackenVale.App;

internal sealed class TrayIconService : IDisposable
{
    private const uint WmSize = 0x0005, WmApp = 0x8000, WmContextMenu = 0x007B, WmLButtonUp = 0x0202, WmLButtonDoubleClick = 0x0203, WmRButtonUp = 0x0205;
    private const uint NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const uint SizeMinimized = 1, SwHide = 0, SwRestore = 9, ImageIcon = 1, LoadFromFile = 0x10;
    private const uint NimAdd = 0, NimDelete = 2, NimSetVersion = 4, NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint CallbackMessage = WmApp + 41;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly Action _restore;
    private readonly SubclassProcedure _procedure;
    private bool _disposed;

    public TrayIconService(IntPtr window, string iconPath, Action restore)
    {
        _window = window; _restore = restore; _procedure = WindowProcedure;
        _icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile);
        if (_icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the Bracken Vale tray icon.");
        if (!SetWindowSubclass(_window, _procedure, (UIntPtr)0xB4A7, UIntPtr.Zero))
        { DestroyIcon(_icon); throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the tray icon to the app window."); }
        var data = MakeData();
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            RemoveWindowSubclass(_window, _procedure, (UIntPtr)0xB4A7); DestroyIcon(_icon);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register the tray icon.");
        }
        data.uTimeoutOrVersion = 4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    public static void RestoreWindow(IntPtr window)
    {
        ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
    }

    private NotifyIconData MakeData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = _window, uID = 1,
        uFlags = NifMessage | NifIcon | NifTip, uCallbackMessage = CallbackMessage, hIcon = _icon,
        szTip = "Bracken Vale", szInfo = string.Empty, szInfoTitle = string.Empty
    };

    private IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmSize && wParam.ToUInt32() == SizeMinimized)
        {
            ShowWindow(window, SwHide);
            return IntPtr.Zero;
        }
        var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
        if (message == CallbackMessage && notification is WmContextMenu or WmLButtonUp or WmLButtonDoubleClick or WmRButtonUp or NinSelect or NinKeySelect)
        {
            _restore();
            return IntPtr.Zero;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = MakeData(); Shell_NotifyIcon(NimDelete, ref data);
        RemoveWindowSubclass(_window, _procedure, (UIntPtr)0xB4A7);
        DestroyIcon(_icon);
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
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr subclassId, UIntPtr referenceData);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr subclassId);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
