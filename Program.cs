using System.Text.Json;
using DynamicIsland;

int passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

var defaults = new IslandSettings();
Check(defaults.Mode == LayoutMode.Floating && !defaults.Hidden && !defaults.UseSavedPosition, "first launch: visible, centered, floating");
Check(defaults.MoveHotkey.IsValid && defaults.HideHotkey.IsValid && defaults.MoveHotkey != defaults.HideHotkey,
    "different valid default shortcuts");
Check(!new HotkeySpec(0, 0x44).IsValid && !new HotkeySpec(4, 0x44).IsValid && !new HotkeySpec(3, 0x7B).IsValid,
    "plain typing, Shift-only and F12 cannot become shortcuts");
Check(new HotkeySpec(2, 0x70).IsValid && new HotkeySpec(8, 0x44).IsValid, "Ctrl+F1 and Win+D accepted for registration checks");
var invalid = new IslandSettings { Mode = (LayoutMode)30, SavedX = -10, SavedY = 100,
    StripHeight = 500, MoveHotkey = new HotkeySpec(0, 0), HideHotkey = null! }.Normalize();
Check(invalid.Mode == LayoutMode.Floating && invalid.SavedX == 0 && invalid.SavedY == 1 && invalid.StripHeight == 120,
    "out-of-range stored settings recovered");
Check(invalid.MoveHotkey == HotkeySpec.MoveDefault && invalid.HideHotkey == HotkeySpec.HideDefault,
    "invalid or null shortcuts recovered");
var duplicated = (defaults with { MoveHotkey = HotkeySpec.HideDefault }).Normalize();
Check(duplicated.MoveHotkey != duplicated.HideHotkey, "duplicate stored shortcuts repaired");
var nonFinite = (defaults with { SavedX = double.NaN, SavedY = double.PositiveInfinity, StripHeight = -1 }).Normalize();
Check(double.IsFinite(nonFinite.SavedX) && double.IsFinite(nonFinite.SavedY) && nonFinite.StripHeight == 46,
    "non-finite positions and too-short strips repaired");

var screen = new DesktopBounds(0, 0, 1920, 1080);
var right = PositionMath.Saved(screen, 680, 300, .85, .10);
Check(right.Left == 1054 && right.Top == 78, "saved position at 100% DPI");
var relative = PositionMath.Relative(screen, 680, 300, right.Left, right.Top);
Check(Math.Abs(relative.X - .85) < .001 && Math.Abs(relative.Y - .10) < .001, "dragging and placement round trip");
var clamped = PositionMath.Saved(screen, 680, 300, 20, -20);
Check(clamped.Left == 1240 && clamped.Top == 0, "island clamped to screen edges");
var scaled = PositionMath.Saved(screen, 1020, 450, 1, 1);
Check(scaled.Left + 1020 == 1920 && scaled.Top + 450 == 1080, "expanded host stays on screen at 150% DPI");
var small = PositionMath.Relative(new DesktopBounds(0, 0, 400, 200), 680, 300, 100, 100);
Check(double.IsFinite(small.X) && double.IsFinite(small.Y), "small displays do not divide by zero");
var shifted = PositionMath.Saved(new DesktopBounds(-1920, -100, 1920, 1080), 680, 300, .5, 0);
Check(shifted.Left == -1300 && shifted.Top == -100, "monitor origin included in placement");

var toggled = defaults with { UseSavedPosition = !defaults.UseSavedPosition };
toggled = toggled with { UseSavedPosition = !toggled.UseSavedPosition };
Check(toggled == defaults, "two position presses restore original state");
var hidden = defaults with { Hidden = true, UseSavedPosition = true };
var roundTrip = JsonSerializer.Deserialize<IslandSettings>(JsonSerializer.Serialize(hidden));
Check(roundTrip == hidden, "hidden state, custom position and shortcuts survive JSON round trip");
Console.WriteLine($"{passed} checks passed.");

namespace DynamicIsland
{
    static class App { public static void Log(Exception ex) => Console.Error.WriteLine(ex.Message); }
}
