using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland;

public enum LayoutMode { Floating, ReservedStrip }

public sealed record HotkeySpec(uint Modifiers, int Key)
{
    public static readonly HotkeySpec MoveDefault = new(3, 0x44); // Ctrl+Alt+D
    public static readonly HotkeySpec HideDefault = new(3, 0x48); // Ctrl+Alt+H

    // A global shortcut must not consume normal typing. F12 is reserved by Windows.
    public bool IsValid => (Modifiers & ~15u) == 0 && (Modifiers & 11u) != 0
        && Key is > 0 and <= 255 && Key != 0x7B
        && Key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C);
}

public sealed record IslandSettings
{
    public LayoutMode Mode { get; init; } = LayoutMode.Floating;
    public int StripHeight { get; init; } = 50;
    public double SavedX { get; init; } = 0.85;
    public double SavedY { get; init; } = 0.10;
    public bool UseSavedPosition { get; init; }
    public bool Hidden { get; init; }
    public HotkeySpec MoveHotkey { get; init; } = HotkeySpec.MoveDefault;
    public HotkeySpec HideHotkey { get; init; } = HotkeySpec.HideDefault;

    public IslandSettings Normalize()
    {
        var move = MoveHotkey is { IsValid: true } ? MoveHotkey : HotkeySpec.MoveDefault;
        var hide = HideHotkey is { IsValid: true } ? HideHotkey : HotkeySpec.HideDefault;
        if (move == hide) { move = HotkeySpec.MoveDefault; hide = HotkeySpec.HideDefault; }
        return this with
        {
            Mode = Enum.IsDefined(Mode) ? Mode : LayoutMode.Floating,
            StripHeight = Math.Clamp(StripHeight, 46, 120),
            SavedX = double.IsFinite(SavedX) ? Math.Clamp(SavedX, 0, 1) : 0.85,
            SavedY = double.IsFinite(SavedY) ? Math.Clamp(SavedY, 0, 1) : 0.10,
            MoveHotkey = move,
            HideHotkey = hide,
        };
    }
}

static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "settings.json");

    public static IslandSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? (JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath), Json) ?? new()).Normalize()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            App.Log(ex);
            return new();
        }
    }

    public static void Save(IslandSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), Json));
        File.Move(temp, FilePath, true);
    }
}

readonly record struct DesktopBounds(int Left, int Top, int Width, int Height);
readonly record struct WindowPlacement(int Left, int Top);

static class PositionMath
{
    public static WindowPlacement Saved(DesktopBounds screen, double width, double height, double x, double y)
    {
        double freeX = Math.Max(0, screen.Width - width);
        double freeY = Math.Max(0, screen.Height - height);
        return new(
            (int)Math.Round(screen.Left + freeX * Math.Clamp(x, 0, 1)),
            (int)Math.Round(screen.Top + freeY * Math.Clamp(y, 0, 1)));
    }

    public static (double X, double Y) Relative(DesktopBounds screen, double width, double height, double left, double top)
    {
        double freeX = Math.Max(0, screen.Width - width);
        double freeY = Math.Max(0, screen.Height - height);
        return (freeX == 0 ? 0.5 : Math.Clamp((left - screen.Left) / freeX, 0, 1),
            freeY == 0 ? 0 : Math.Clamp((top - screen.Top) / freeY, 0, 1));
    }
}
