namespace DxDark.Core.Settings;

/// <summary>What a keyboard shortcut does.</summary>
public enum ShortcutAction
{
    LightOnOff,
    CycleMode,
    NextPreset,
    BrightnessDown,
    BrightnessUp,
    ShowControlPanel,
}

/// <summary>One global keyboard shortcut, e.g. "Alt+F1". An empty gesture means none.</summary>
public sealed class Shortcut
{
    public ShortcutAction Action { get; set; }

    public string Gesture { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public Shortcut Clone() => (Shortcut)MemberwiseClone();
}

public static class ShortcutDefaults
{
    /// <summary>Every shortcut with its default keys, in the order the Shortcuts page lists them.</summary>
    public static IReadOnlyList<Shortcut> All { get; } =
    [
        new() { Action = ShortcutAction.LightOnOff, Gesture = "Alt+F1" },
        new() { Action = ShortcutAction.CycleMode, Gesture = "Alt+F2" },
        new() { Action = ShortcutAction.NextPreset, Gesture = "Alt+F3" },
        new() { Action = ShortcutAction.BrightnessDown, Gesture = "Alt+F5" },
        new() { Action = ShortcutAction.BrightnessUp, Gesture = "Alt+F6" },
        new() { Action = ShortcutAction.ShowControlPanel, Gesture = "Alt+F9" },
    ];

    public static string Name(ShortcutAction action) => action switch
    {
        ShortcutAction.LightOnOff => "Light on/off",
        ShortcutAction.CycleMode => "Next effect / screen",
        ShortcutAction.NextPreset => "Next preset",
        ShortcutAction.BrightnessDown => "Brightness down",
        ShortcutAction.BrightnessUp => "Brightness up",
        _ => "Open control panel",
    };

    /// <summary>The settings file key for an action.</summary>
    public static string Key(ShortcutAction action) => action.ToString();

    /// <summary>One entry per action, in the default order; missing ones get their defaults.</summary>
    public static List<Shortcut> Complete(IEnumerable<Shortcut>? shortcuts)
    {
        var list = new List<Shortcut>();
        foreach (Shortcut fallback in All)
        {
            Shortcut? found = shortcuts?.FirstOrDefault(s => s is not null && s.Action == fallback.Action);
            Shortcut item = (found ?? fallback).Clone();
            item.Gesture = (item.Gesture ?? "").Trim();
            list.Add(item);
        }

        return list;
    }
}
