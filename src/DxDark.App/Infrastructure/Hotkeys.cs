using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using DxDark.Core;
using DxDark.Core.Settings;

namespace DxDark.App.Infrastructure;

/// <summary>Turns key combinations into text such as "Ctrl+Alt+F1" and back.</summary>
public static class ShortcutKeys
{
    /// <summary>The text for a key press, or null for a modifier on its own.</summary>
    public static string? Format(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>Reads "Alt+F1"; false if the text is not a key combination.</summary>
    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key != Key.None || !TryKey(part, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        return key != Key.None;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Return => "Enter",
        _ => key.ToString(),
    };

    private static bool TryKey(string name, out Key key)
    {
        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            key = Key.D0 + (name[0] - '0');
            return true;
        }

        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3]))
        {
            key = Key.NumPad0 + (name[3] - '0');
            return true;
        }

        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }
}

/// <summary>
/// System-wide keyboard shortcuts (they work while any program has focus), registered with
/// Windows on a hidden message window. Raises <see cref="Pressed"/> on the UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _window;
    private readonly Dictionary<int, ShortcutAction> _registered = [];

    public HotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("DX Dark shortcuts")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: never shown
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _window.AddHook(OnMessage);
    }

    public event Action<ShortcutAction>? Pressed;

    /// <summary>
    /// Registers the enabled shortcuts (replacing earlier ones). Returns the actions whose keys
    /// another program already uses.
    /// </summary>
    public HashSet<ShortcutAction> Apply(IEnumerable<Shortcut> shortcuts)
    {
        Clear();
        var taken = new HashSet<ShortcutAction>();
        int id = 1;
        foreach (Shortcut shortcut in shortcuts)
        {
            if (!shortcut.Enabled || !ShortcutKeys.TryParse(shortcut.Gesture, out Key key, out ModifierKeys modifiers))
            {
                continue;
            }

            uint mods = ModNoRepeat
                | (modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
                | (modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
                | (modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
                | (modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);
            if (RegisterHotKey(_window.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key)))
            {
                _registered[id] = shortcut.Action;
            }
            else
            {
                taken.Add(shortcut.Action);
                Log.Warn($"Shortcut {shortcut.Gesture} is already used by another program");
            }

            id++;
        }

        return taken;
    }

    /// <summary>Unregisters every shortcut (e.g. while a new one is being typed).</summary>
    public void Clear()
    {
        foreach (int id in _registered.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }

        _registered.Clear();
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out ShortcutAction action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
