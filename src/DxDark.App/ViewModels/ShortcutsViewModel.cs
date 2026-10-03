using System.Collections.ObjectModel;
using DxDark.App.Infrastructure;
using DxDark.Core;
using DxDark.Core.Settings;

namespace DxDark.App.ViewModels;

/// <summary>One row of the Shortcuts page.</summary>
public sealed class ShortcutRow(ShortcutsViewModel owner, Shortcut model) : ObservableObject
{
    private bool _isTaken;

    public Shortcut Model { get; } = model;

    public string Name => ShortcutDefaults.Name(Model.Action);

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled != value)
            {
                Model.Enabled = value;
                OnPropertyChanged();
                owner.Changed();
            }
        }
    }

    public string Gesture
    {
        get => Model.Gesture;
        set
        {
            value ??= "";
            if (Model.Gesture != value)
            {
                owner.ReleaseGesture(value, this);
                Model.Gesture = value;
                OnPropertyChanged();
                owner.Changed();
            }
        }
    }

    /// <summary>Another program already uses these keys.</summary>
    public bool IsTaken { get => _isTaken; internal set => Set(ref _isTaken, value); }

    internal void Clear()
    {
        Model.Gesture = "";
        OnPropertyChanged(nameof(Gesture));
    }
}

/// <summary>The Shortcuts page: each action, whether it is on, and its keys.</summary>
public sealed class ShortcutsViewModel : ObservableObject
{
    private readonly LightController _controller;

    public ShortcutsViewModel(LightController controller)
    {
        _controller = controller;
        Rows = [];
        Reload();
    }

    /// <summary>The shortcuts changed: register them again.</summary>
    public event Action? ShortcutsChanged;

    public ObservableCollection<ShortcutRow> Rows { get; }

    public void Reload()
    {
        Rows.Clear();
        foreach (Shortcut shortcut in _controller.Settings.Shortcuts)
        {
            Rows.Add(new ShortcutRow(this, shortcut));
        }
    }

    /// <summary>Marks the rows whose keys another program already uses.</summary>
    public void ShowTaken(ISet<ShortcutAction> taken)
    {
        foreach (ShortcutRow row in Rows)
        {
            row.IsTaken = taken.Contains(row.Model.Action);
        }
    }

    internal void Changed()
    {
        _controller.Store.SaveSoon();
        ShortcutsChanged?.Invoke();
    }

    /// <summary>The same keys can only do one thing: another row using them loses them.</summary>
    internal void ReleaseGesture(string gesture, ShortcutRow keeper)
    {
        if (gesture.Length == 0)
        {
            return;
        }

        foreach (ShortcutRow row in Rows.Where(r => r != keeper && string.Equals(r.Gesture, gesture, StringComparison.OrdinalIgnoreCase)))
        {
            row.Clear();
        }
    }
}
