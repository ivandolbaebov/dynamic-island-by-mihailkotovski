using System.ComponentModel;
using System.Windows.Input;

namespace DynamicIsland;

enum HotkeyAction { Move, Hide }

sealed class HotkeyManager(IntPtr hwnd) : IDisposable
{
    readonly record struct Registration(int Id, HotkeySpec Shortcut, HotkeyAction Action);
    List<Registration> _registered = [];
    int _nextId = 100;

    public bool TryApply(HotkeySpec move, HotkeySpec hide, out string error)
    {
        error = "";
        if (!move.IsValid || !hide.IsValid || move == hide)
        {
            error = "Выбери два разных сочетания с Ctrl, Alt или Win. Клавиша F12 зарезервирована Windows.";
            return false;
        }

        var next = new List<Registration>();
        var added = new List<int>();
        foreach (var (key, action) in new[] { (move, HotkeyAction.Move), (hide, HotkeyAction.Hide) })
        {
            Registration? existing = _registered.Where(r => r.Shortcut == key).Select(r => (Registration?)r).FirstOrDefault();
            if (existing is { } same)
            {
                next.Add(same with { Action = action });
                continue;
            }
            if (_nextId >= 0xBFFF) _nextId = 100;
            int id = _nextId++;
            if (!Native.RegisterHotKey(hwnd, id, key.Modifiers | 0x4000, (uint)key.Key)) // MOD_NOREPEAT
            {
                string cause = new Win32Exception().Message;
                foreach (int pending in added) Native.UnregisterHotKey(hwnd, pending);
                error = $"Не удалось назначить {Label(key)}. Возможно, сочетание занято другой программой.\n{cause}";
                return false;
            }
            added.Add(id);
            next.Add(new(id, key, action));
        }
        foreach (Registration previous in _registered)
            if (!next.Any(r => r.Id == previous.Id)) Native.UnregisterHotKey(hwnd, previous.Id);
        _registered = next;
        return true;
    }

    public HotkeyAction? ActionFor(int id) => _registered.Where(r => r.Id == id)
        .Select(r => (HotkeyAction?)r.Action).FirstOrDefault();

    public static string Label(HotkeySpec key)
    {
        var parts = new List<string>();
        if ((key.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((key.Modifiers & 1) != 0) parts.Add("Alt");
        if ((key.Modifiers & 4) != 0) parts.Add("Shift");
        if ((key.Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey(key.Key).ToString());
        return string.Join(" + ", parts);
    }

    public void Clear()
    {
        foreach (Registration registration in _registered) Native.UnregisterHotKey(hwnd, registration.Id);
        _registered.Clear();
    }

    public void Dispose() => Clear();
}
