using DxDark.App.Views;

namespace DxDark.App.Infrastructure;

/// <summary>Messages, questions and text entry in DX Dark's own dark dialog, over the window they belong to.</summary>
public static class Dialogs
{
    /// <summary>Asks for a line of text; returns null when cancelled or left empty.</summary>
    public static string? Prompt(string title, string message, string initial, string okText = "Save")
    {
        var dialog = new DialogWindow(title, message, okText, "Cancel", destructive: false, input: initial);
        return Show(dialog) && !string.IsNullOrWhiteSpace(dialog.InputText) ? dialog.InputText.Trim() : null;
    }

    public static bool Confirm(string title, string message, string confirmText = "OK", bool destructive = false) =>
        Show(new DialogWindow(title, message, confirmText, "Cancel", destructive, input: null));

    public static void Info(string title, string message) =>
        Show(new DialogWindow(title, message, "OK", cancelText: null, destructive: false, input: null));

    private static bool Show(DialogWindow dialog)
    {
        dialog.PlaceOver(ModalPlacement.OwnerWindow());
        return dialog.ShowDialog() == true;
    }
}
