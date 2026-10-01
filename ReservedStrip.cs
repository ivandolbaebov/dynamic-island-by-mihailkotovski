using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>A separate transparent appbar reserves space without stretching the island's input window.</summary>
sealed class ReservedStrip : IDisposable
{
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    const uint ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    readonly HwndSource _window;
    readonly Action _changed;
    bool _registered, _positioning, _queued, _disposed;
    DesktopBounds _screen;
    int _height;
    public int Top { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    public ReservedStrip(Action changed)
    {
        _changed = changed;
        _window = new HwndSource(new HwndSourceParameters("DynamicIsland.ReservedStrip")
        {
            Width = 1, Height = 1,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x080800A0, // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT
        });
        SetLayeredWindowAttributes(_window.Handle, 0, 0, 2); // fully transparent, clicks go through
        _window.AddHook(Hook);
    }

    APPBARDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = _window.Handle,
        uCallbackMessage = Native.WM_APPBAR,
        uEdge = 1, // ABE_TOP
    };

    public bool Reserve(DesktopBounds screen, int height)
    {
        if (_disposed) return false;
        if (!_registered)
        {
            var data = Data();
            _registered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
            if (!_registered) return false;
        }
        if (_screen == screen && _height == height) return true;
        _screen = screen;
        _height = height;
        Position();
        return true;
    }

    void Position()
    {
        if (!_registered || _positioning) return;
        _positioning = true;
        try
        {
            var data = Data();
            data.rc = new RECT { Left = _screen.Left, Top = _screen.Top,
                Right = _screen.Left + _screen.Width, Bottom = _screen.Top + _height };
            SHAppBarMessage(ABM_QUERYPOS, ref data);
            data.rc.Bottom = data.rc.Top + _height;
            SHAppBarMessage(ABM_SETPOS, ref data);
            Top = data.rc.Top;
            SetWindowPos(_window.Handle, IntPtr.Zero, data.rc.Left, data.rc.Top,
                data.rc.Right - data.rc.Left, data.rc.Bottom - data.rc.Top, 0x0010 | 0x0004 | 0x0040);
        }
        finally { _positioning = false; }
    }

    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_APPBAR && wParam.ToInt64() == 1 && _registered && !_positioning && !_queued)
        {
            _queued = true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _queued = false;
                if (!_registered || _disposed) return;
                int previousTop = Top;
                Position();
                if (previousTop != Top) _changed();
            }));
            handled = true;
        }
        else if (_registered && (message == 0x0006 || message == 0x0047)) // ACTIVATE / WINDOWPOSCHANGED
        {
            var data = Data();
            data.lParam = wParam;
            SHAppBarMessage(message == 0x0006 ? ABM_ACTIVATE : ABM_WINDOWPOSCHANGED, ref data);
        }
        return IntPtr.Zero;
    }

    public void Release()
    {
        if (_registered)
        {
            _registered = false; // synchronous shell notifications must not register again
            var data = Data();
            SHAppBarMessage(ABM_REMOVE, ref data);
        }
        _height = 0;
        if (!_disposed) Native.ShowNoActivate(_window.Handle, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Release();
        _disposed = true;
        _window.RemoveHook(Hook);
        _window.Dispose();
    }
}
