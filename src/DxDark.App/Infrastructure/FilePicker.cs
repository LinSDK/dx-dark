using DxDark.App.Views;

namespace DxDark.App.Infrastructure;

/// <summary>Choosing files in DX Dark's own file picker (see <see cref="FilePickerWindow"/>).</summary>
public static class FilePicker
{
    public static string? Open(string title, string okText, string filterName, string[] extensions, string rememberKey,
        Environment.SpecialFolder startFolder, string fileGlyph = "")
    {
        var picker = new FilePickerWindow(title, okText, filterName, extensions, save: false, rememberKey, startFolder, fileGlyph: fileGlyph);
        picker.PlaceOver(ModalPlacement.OwnerWindow());
        return picker.ShowDialog() == true ? picker.SelectedPath : null;
    }

    public static string? Save(string title, string filterName, string extension, string fileName, string rememberKey,
        Environment.SpecialFolder startFolder)
    {
        var picker = new FilePickerWindow(title, "Save", filterName, [extension], save: true, rememberKey, startFolder, fileName);
        picker.PlaceOver(ModalPlacement.OwnerWindow());
        return picker.ShowDialog() == true ? picker.SelectedPath : null;
    }
}
