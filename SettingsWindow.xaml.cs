using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DynamicIsland;

public partial class SettingsWindow : Window
{
    readonly IslandSettings _original;
    readonly Func<IslandSettings, string?> _apply;
    HotkeySpec _move = HotkeySpec.MoveDefault, _hide = HotkeySpec.HideDefault;

    public SettingsWindow(IslandSettings settings, Func<IslandSettings, string?> apply)
    {
        InitializeComponent();
        _original = settings;
        _apply = apply;
        Populate(settings);
    }

    void Populate(IslandSettings settings)
    {
        FloatingRadio.IsChecked = settings.Mode == LayoutMode.Floating;
        StripRadio.IsChecked = settings.Mode == LayoutMode.ReservedStrip;
        HeightSlider.Value = settings.StripHeight;
        XSlider.Value = settings.SavedX * 100;
        YSlider.Value = settings.SavedY * 100;
        _move = settings.MoveHotkey;
        _hide = settings.HideHotkey;
        UpdateLabels();
        Mode_Changed(this, new RoutedEventArgs());
    }

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (StripControls == null) return;
        StripControls.IsEnabled = StripRadio.IsChecked == true;
        StripControls.Opacity = StripControls.IsEnabled ? 1 : 0.35;
    }

    void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return;
        ModifierKeys mods = Keyboard.Modifiers;
        uint flags = 0;
        if ((mods & ModifierKeys.Alt) != 0) flags |= 1;
        if ((mods & ModifierKeys.Control) != 0) flags |= 2;
        if ((mods & ModifierKeys.Shift) != 0) flags |= 4;
        if ((mods & ModifierKeys.Windows) != 0) flags |= 8;
        var shortcut = new HotkeySpec(flags, KeyInterop.VirtualKeyFromKey(key));
        if (!shortcut.IsValid)
        {
            ShowError("Нужна обычная клавиша вместе с Ctrl, Alt или Win. F12 зарезервирована Windows.");
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(sender, MoveKeyBox)) _move = shortcut;
        else _hide = shortcut;
        UpdateLabels();
    }

    void UpdateLabels()
    {
        MoveKeyBox.Text = HotkeyManager.Label(_move);
        HideKeyBox.Text = HotkeyManager.Label(_hide);
    }

    void RightPreset_Click(object sender, RoutedEventArgs e) { XSlider.Value = 85; YSlider.Value = 10; }
    void LeftPreset_Click(object sender, RoutedEventArgs e) { XSlider.Value = 15; YSlider.Value = 10; }
    void CenterPreset_Click(object sender, RoutedEventArgs e) { XSlider.Value = 50; YSlider.Value = 10; }
    void Defaults_Click(object sender, RoutedEventArgs e) => Populate(new IslandSettings());

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var candidate = _original with
        {
            Mode = StripRadio.IsChecked == true ? LayoutMode.ReservedStrip : LayoutMode.Floating,
            StripHeight = (int)Math.Round(HeightSlider.Value),
            SavedX = XSlider.Value / 100,
            SavedY = YSlider.Value / 100,
            MoveHotkey = _move,
            HideHotkey = _hide,
        };
        string? error = _apply(candidate);
        if (error != null) { ShowError(error); return; }
        DialogResult = true;
    }

    void ShowError(string error)
    {
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        ErrorText.BringIntoView();
    }
}
