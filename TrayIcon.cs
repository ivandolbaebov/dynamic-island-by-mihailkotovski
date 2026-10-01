using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DynamicIsland;

sealed class TrayIcon : IDisposable
{
    readonly IntPtr _hwnd, _icon;
    readonly Action _show, _hide, _move, _settings, _exit;
    ContextMenu? _menu;
    bool _disposed;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")]
    static extern IntPtr CreateIcon(IntPtr instance, int width, int height, byte planes, byte bits, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    public TrayIcon(IntPtr hwnd, Action show, Action hide, Action move, Action settings, Action exit)
    {
        _hwnd = hwnd; _show = show; _hide = hide; _move = move; _settings = settings; _exit = exit;
        const int size = 32;
        var pixels = new byte[size * size * 4];
        var mask = Enumerable.Repeat((byte)255, size * 4).ToArray();
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            bool pill = y >= 10 && y <= 21 && x >= 3 && x <= 28;
            if (pill && (x < 9 || x > 22))
            {
                double cx = x < 9 ? 9 : 22;
                pill = Math.Pow(x - cx, 2) + Math.Pow(y - 15.5, 2) <= 36;
            }
            if (!pill) continue;
            int at = (y * size + x) * 4;
            pixels[at] = pixels[at + 1] = pixels[at + 2] = 240;
            pixels[at + 3] = 255;
            mask[y * 4 + x / 8] &= (byte)~(0x80 >> (x % 8));
        }
        _icon = CreateIcon(IntPtr.Zero, size, size, 1, 32, mask, pixels);
        Add();
    }

    NOTIFYICONDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1,
        uFlags = 1 | 2 | 4, uCallbackMessage = Native.WM_TRAY, hIcon = _icon,
        szTip = "Dynamic Island — нажми, чтобы показать; ПКМ — настройки", szInfo = "", szInfoTitle = "",
    };

    public void Add()
    {
        if (_disposed) return;
        var data = Data();
        Shell_NotifyIcon(0, ref data);
    }

    public void Warning(string message)
    {
        var data = Data();
        data.uFlags = 16;
        data.szInfoTitle = "Dynamic Island";
        data.szInfo = message.Length < 256 ? message : message[..255];
        data.dwInfoFlags = 2;
        Shell_NotifyIcon(1, ref data);
    }

    public void Handle(IntPtr lParam)
    {
        int message = (int)(lParam.ToInt64() & 0xFFFF);
        if (message == 0x0202) _show(); // left release
        else if (message == 0x0205 || message == 0x007B) // right release / context menu
        {
            _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            AddItem("Показать остров", _show);
            AddItem("Скрыть остров", _hide);
            AddItem("Центр ↔ сохранённое положение", _move);
            _menu.Items.Add(new Separator());
            AddItem("Настройки…", _settings);
            _menu.Items.Add(new Separator());
            AddItem("Выход", _exit);
            SetForegroundWindow(_hwnd);
            _menu.IsOpen = true;
        }
    }

    void AddItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        _menu!.Items.Add(item);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_menu != null) _menu.IsOpen = false;
        var data = Data();
        Shell_NotifyIcon(2, ref data);
        DestroyIcon(_icon);
        _disposed = true;
    }
}
