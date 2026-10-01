using System.Runtime.InteropServices;

namespace DynamicIsland;

static partial class Native
{
    public const int WM_HOTKEY = 0x0312, WM_DPICHANGED = 0x02E0;
    public const int WM_APPBAR = 0x8001, WM_TRAY = 0x8002;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public static DesktopBounds PrimaryBounds()
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTONEAREST), ref info))
            throw new InvalidOperationException("Не удалось определить размеры главного экрана.");
        RECT r = info.rcMonitor;
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static WindowPlacement WindowPosition(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out RECT rect);
        return new(rect.Left, rect.Top);
    }

    public static void MoveNoActivate(IntPtr hwnd, int left, int top) =>
        SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 0x0004);

    public static void ShowNoActivate(IntPtr hwnd, bool show) => ShowWindow(hwnd, show ? SW_SHOWNOACTIVATE : SW_HIDE);
}
