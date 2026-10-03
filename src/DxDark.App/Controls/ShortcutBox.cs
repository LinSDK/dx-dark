using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DxDark.App.Infrastructure;

namespace DxDark.App.Controls;

/// <summary>
/// Shows a shortcut such as "Alt+F1"; click it and press new keys to change it. Backspace or
/// Delete removes it, Esc leaves it unchanged.
/// </summary>
public sealed class ShortcutBox : TextBox
{
    public static readonly DependencyProperty GestureProperty =
        DependencyProperty.Register(nameof(Gesture), typeof(string), typeof(ShortcutBox),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ShortcutBox)d).ShowGesture()));

    public ShortcutBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox)); // the app's dark text box look
        IsReadOnly = true;
        VerticalContentAlignment = VerticalAlignment.Center;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        IsUndoEnabled = false;
        ContextMenu = null;
        Loaded += (_, _) => ShowGesture();
    }

    /// <summary>Raised with true while any shortcut box waits for keys, so the shortcuts themselves can be paused.</summary>
    public static event Action<bool>? Capturing;

    public string Gesture { get => (string)GetValue(GestureProperty); set => SetValue(GestureProperty, value); }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Capturing?.Invoke(true);
        Text = "Press keys";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ShowGesture();
        Capturing?.Invoke(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return; // keep Tab for moving between controls
        }

        e.Handled = true;
        if (modifiers == ModifierKeys.None && key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            Gesture = "";
            Keyboard.ClearFocus();
            return;
        }

        // Plain keys would block typing everywhere; only function keys may go without Ctrl, Alt, Shift or Win.
        bool functionKey = key is >= Key.F1 and <= Key.F24;
        if (ShortcutKeys.Format(key, modifiers) is { } gesture && (modifiers != ModifierKeys.None || functionKey))
        {
            Gesture = gesture;
            Keyboard.ClearFocus();
        }
    }

    private void ShowGesture()
    {
        if (!IsKeyboardFocusWithin)
        {
            Text = string.IsNullOrEmpty(Gesture) ? "None" : Gesture;
        }
    }
}
