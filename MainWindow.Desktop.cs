using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow
{
    IslandSettings _settings = SettingsStore.Load();
    HwndSource? _source;
    HotkeyManager? _hotkeys;
    ReservedStrip? _strip;
    TrayIcon? _tray;
    SettingsWindow? _settingsWindow;
    uint _taskbarCreated;
    bool _desktopReady, _desktopVisible = true, _reserved, _stripWarning, _saveWarning, _desktopDisposed;
    bool _dragCandidate, _dragging;
    Native.POINT _dragStart;
    WindowPlacement _dragOrigin;

    bool DesktopVisible => !_settings.Hidden && (!_hidden || _ringing);
    bool WantsStrip => DesktopVisible && !_hidden && _settings.Mode == LayoutMode.ReservedStrip && !_settings.UseSavedPosition;
    double DesktopScale => Math.Max(96, Native.GetDpiForWindow(_hwnd)) / 96.0;

    void InitializeDesktopControls()
    {
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(DesktopHook);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        _strip = new ReservedStrip(() => { if (_desktopReady) ApplyDesktopLayout(); });
        _hotkeys = new HotkeyManager(_hwnd);
        _tray = new TrayIcon(_hwnd, () => SetManualHidden(false), () => SetManualHidden(true),
            ToggleSavedPosition, ShowSettings, () => Exit_Click(this, new RoutedEventArgs()));
        if (!_hotkeys.TryApply(_settings.MoveHotkey, _settings.HideHotkey, out string error))
            Dispatcher.BeginInvoke(new Action(() => _tray.Warning(error + " Открой настройки через значок в трее.")));
    }

    IntPtr DesktopHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_HOTKEY)
        {
            switch (_hotkeys?.ActionFor(wParam.ToInt32()))
            {
                case HotkeyAction.Move: ToggleSavedPosition(); break;
                case HotkeyAction.Hide: SetManualHidden(!_settings.Hidden); break;
            }
            handled = true;
        }
        else if (message == Native.WM_TRAY)
        {
            _tray?.Handle(lParam);
            handled = true;
        }
        else if (message == Native.WM_DPICHANGED && _desktopReady)
            Dispatcher.BeginInvoke(new Action(ApplyDesktopLayout));
        else if (_taskbarCreated != 0 && (uint)message == _taskbarCreated)
        {
            _tray?.Add();
            _strip?.Release();
            if (_desktopReady) Dispatcher.BeginInvoke(new Action(ApplyDesktopLayout));
        }
        return IntPtr.Zero;
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Place);

    void ApplyDesktopLayout()
    {
        if (_desktopDisposed || _hwnd == IntPtr.Zero || _strip == null) return;
        _desktopReady = true;
        DesktopBounds screen = Native.PrimaryBounds();
        double scale = DesktopScale;
        bool reserve = WantsStrip;
        _reserved = reserve;
        bool didReserve = false;
        if (reserve)
        {
            didReserve = _strip.Reserve(screen, (int)Math.Ceiling(_settings.StripHeight * scale));
            if (!didReserve && !_stripWarning)
            {
                _stripWarning = true;
                _tray?.Warning("Windows не удалось выделить верхнюю полосу. Остров пока работает поверх окон.");
            }
        }
        else _strip.Release();

        WindowPlacement position = _settings.UseSavedPosition
            ? PositionMath.Saved(screen, Width * scale, Height * scale, _settings.SavedX, _settings.SavedY)
            : new(screen.Left + (int)Math.Round((screen.Width - Width * scale) / 2), didReserve ? _strip.Top : screen.Top);
        Native.MoveNoActivate(_hwnd, position.Left, position.Top);
        SetDesktopVisibility();
    }

    void RefreshDesktopVisibility()
    {
        if (!_desktopReady) return;
        if (_reserved != WantsStrip) ApplyDesktopLayout();
        else if (_desktopVisible != DesktopVisible) SetDesktopVisibility();
    }

    void SetDesktopVisibility()
    {
        bool show = DesktopVisible;
        if (_desktopVisible == show) return;
        _desktopVisible = show;
        Root.IsHitTestVisible = show;
        Native.ShowNoActivate(_hwnd, show);
        if (show) Native.KeepOnTop(_hwnd);
    }

    void ResetPointerState()
    {
        _dragCandidate = _dragging = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        if (_scrubbing)
        {
            EndScrub();
            SeekArea.ReleaseMouseCapture();
        }
        _hover = _pressed = _bubbleHover = _bubblePressed = false;
        _collapseTimer.Stop();
        _panel = Panel.None;
        UpdateView();
        SetTargets();
    }

    void ToggleSavedPosition()
    {
        ResetPointerState();
        _settings = _settings with { UseSavedPosition = !_settings.UseSavedPosition };
        SaveDesktopSettings();
        ApplyDesktopLayout();
    }

    void SetManualHidden(bool hidden)
    {
        ResetPointerState();
        _settings = _settings with { Hidden = hidden };
        SaveDesktopSettings();
        ApplyDesktopLayout();
    }

    void SaveDesktopSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex)
        {
            App.Log(ex);
            if (_saveWarning) return;
            _saveWarning = true;
            _tray?.Warning("Не удалось сохранить настройки. До перезапуска изменения работают.");
        }
    }

    void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    void ShowSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        ResetPointerState();
        _hotkeys?.Clear(); // the hotkey boxes must receive even the currently assigned shortcuts
        _settingsWindow = new SettingsWindow(_settings, TryApplySettings);
        try { _settingsWindow.ShowDialog(); }
        finally
        {
            _settingsWindow = null;
            if (!_desktopDisposed && _hotkeys != null && !_hotkeys.TryApply(_settings.MoveHotkey, _settings.HideHotkey, out string error))
                _tray?.Warning(error + " Настройки доступны через значок в трее.");
        }
    }

    string? TryApplySettings(IslandSettings candidate)
    {
        if (_hotkeys == null) return "Остров ещё запускается.";
        if (!_hotkeys.TryApply(candidate.MoveHotkey, candidate.HideHotkey, out string error)) return error;
        candidate = candidate with { Hidden = _settings.Hidden, UseSavedPosition = _settings.UseSavedPosition };
        candidate = candidate.Normalize();
        try { SettingsStore.Save(candidate); }
        catch (Exception ex)
        {
            App.Log(ex);
            _hotkeys.Clear();
            return "Не удалось сохранить настройки: " + ex.Message;
        }
        _settings = candidate;
        ApplyDesktopLayout();
        return null;
    }

    void Move_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || !Native.GetCursorPos(out _dragStart)) return;
        _dragOrigin = Native.WindowPosition(_hwnd);
        _dragCandidate = true;
        Root.CaptureMouse();
        e.Handled = true;
    }

    void Move_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed || !Native.GetCursorPos(out var cursor)) return;
        double dx = cursor.X - _dragStart.X, dy = cursor.Y - _dragStart.Y;
        double scale = DesktopScale;
        if (!_dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * scale
            && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * scale) return;
        _dragging = true;
        _collapseTimer.Stop();
        _panel = Panel.None;
        _hover = _pressed = _bubbleHover = _bubblePressed = false;
        var relative = PositionMath.Relative(Native.PrimaryBounds(), Width * scale, Height * scale,
            _dragOrigin.Left + dx, _dragOrigin.Top + dy);
        _settings = _settings with { UseSavedPosition = true, SavedX = relative.X, SavedY = relative.Y };
        ApplyDesktopLayout();
        UpdateView();
        SetTargets();
        e.Handled = true;
    }

    void Move_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragCandidate) return;
        FinishMove();
        e.Handled = true;
    }

    void Move_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragCandidate) FinishMove();
    }

    void FinishMove()
    {
        bool moved = _dragging;
        _dragCandidate = _dragging = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        _pressed = _bubblePressed = false;
        if (moved) SaveDesktopSettings();
        SetTargets();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) DisposeDesktopControls();
    }

    void DisposeDesktopControls()
    {
        if (_desktopDisposed) return;
        _desktopDisposed = true;
        _desktopReady = false;
        _tick.Stop();
        _transientTimer.Stop();
        _collapseTimer.Stop();
        _alarm.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _strip?.Dispose();
        if (_source is { IsDisposed: false }) _source.RemoveHook(DesktopHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeDesktopControls();
        base.OnClosed(e);
    }
}
