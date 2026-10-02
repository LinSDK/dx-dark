using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DxDark.App.Infrastructure;

namespace DxDark.App.Views;

public sealed record PlaceEntry(string Name, string Path, string Glyph);

public sealed record FileEntry(string Name, string Path, bool IsFolder, string Glyph, Brush GlyphBrush, string Details);

/// <summary>
/// DX Dark's own file picker (choosing a video, importing or exporting a preset), in the app's
/// dark style instead of Windows' file dialog. Folders and matching files only; hidden and
/// system files are left out.
/// </summary>
public partial class FilePickerWindow : Window
{
    private static readonly Dictionary<string, string> LastFolders = new(StringComparer.Ordinal);

    private readonly bool _save;
    private readonly string[] _extensions;
    private readonly string _rememberKey;
    private readonly string _fileGlyph;
    private readonly Stack<string> _history = new();
    private string? _current;
    private bool _syncingPlaces;

    /// <param name="extensions">Allowed extensions with the dot (".mp4"); the first is added when saving without one.</param>
    /// <param name="rememberKey">Picks with the same key start in the folder used last time.</param>
    public FilePickerWindow(string title, string okText, string filterName, string[] extensions, bool save,
        string rememberKey, Environment.SpecialFolder startFolder, string? fileName = null, string fileGlyph = "")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        OkButton.Content = okText;
        FilterText.Text = filterName;
        NameBox.Text = fileName ?? "";
        _save = save;
        _extensions = extensions;
        _rememberKey = rememberKey;
        _fileGlyph = fileGlyph;
        Places.ItemsSource = BuildPlaces();

        string start = LastFolders.TryGetValue(rememberKey, out string? last) && Directory.Exists(last) ? last : Environment.GetFolderPath(startFolder);
        if (!Directory.Exists(start))
        {
            start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            Navigate(start, record: false);
            if (save)
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
            else
            {
                Entries.Focus();
            }
        };
    }

    /// <summary>The chosen file, once the picker closed with OK.</summary>
    public string? SelectedPath { get; private set; }

    public void PlaceOver(Window? owner) => ModalPlacement.Cover(this, Scrim, owner);

    /// <summary>Shows <paramref name="folder"/> (used for UI snapshots).</summary>
    internal void ShowFolder(string folder) => Navigate(folder, record: false);

    private void Navigate(string folder, bool record = true)
    {
        string full = Path.GetFullPath(folder);
        var entries = new List<FileEntry>();
        string? problem = null;
        try
        {
            var directory = new DirectoryInfo(full);
            var folderBrush = (Brush)FindResource("WarningBrush");
            var fileBrush = (Brush)FindResource("TextSecondaryBrush");
            foreach (DirectoryInfo d in directory.EnumerateDirectories().Where(d => !IsHidden(d)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                entries.Add(new FileEntry(d.Name, d.FullName, true, "", folderBrush, ""));
            }

            foreach (FileInfo f in directory.EnumerateFiles().Where(f => !IsHidden(f) && Matches(f.Name)).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                entries.Add(new FileEntry(f.Name, f.FullName, false, _fileGlyph, fileBrush, $"{FormatSize(f.Length)}     {f.LastWriteTime:yyyy-MM-dd}"));
            }
        }
        catch (UnauthorizedAccessException)
        {
            problem = "Windows doesn't allow DX Dark to open this folder.";
        }
        catch (IOException ex)
        {
            problem = ex.Message;
        }

        if (record && _current is not null && !string.Equals(_current, full, StringComparison.OrdinalIgnoreCase))
        {
            _history.Push(_current);
        }

        _current = full;
        PathBox.Text = full;
        Entries.ItemsSource = entries;
        EmptyText.Text = problem ?? (_save ? "This folder is empty." : "No matching files in this folder.");
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _history.Count > 0;
        UpButton.IsEnabled = Directory.GetParent(full) is not null;

        // Highlight the place this folder is (or is inside of), without navigating again.
        _syncingPlaces = true;
        Places.SelectedItem = ((IEnumerable<PlaceEntry>)Places.ItemsSource)
            .Where(p => full.StartsWith(p.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Path.Length)
            .FirstOrDefault();
        _syncingPlaces = false;
    }

    /// <summary>A path typed into the folder box: go there, or pick it if it is a file.</summary>
    private void NavigateTyped()
    {
        string text = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
        if (Directory.Exists(text))
        {
            Navigate(text);
            Entries.Focus();
        }
        else if (!_save && File.Exists(text) && Matches(text))
        {
            Accept(Path.GetFullPath(text));
        }
        else
        {
            Dialogs.Info(TitleText.Text, $"\"{text}\" wasn't found.");
            PathBox.Text = _current;
        }
    }

    private void Activate(FileEntry entry)
    {
        if (entry.IsFolder)
        {
            Navigate(entry.Path);
        }
        else
        {
            NameBox.Text = entry.Name;
            OnOk(this, new RoutedEventArgs());
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim().Trim('"');
        if (name.Length == 0)
        {
            if (Entries.SelectedItem is FileEntry { IsFolder: true } folder)
            {
                Navigate(folder.Path);
            }

            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(_current ?? "", name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Dialogs.Info(TitleText.Text, "That isn't a valid file name.");
            return;
        }

        if (Directory.Exists(path))
        {
            Navigate(path);
            NameBox.Text = "";
            return;
        }

        if (_save)
        {
            if (!Matches(path))
            {
                path += _extensions[0];
            }

            if (File.Exists(path) && !Dialogs.Confirm("Replace file", $"\"{Path.GetFileName(path)}\" already exists. Replace it?", "Replace"))
            {
                return;
            }

            Accept(path);
        }
        else if (File.Exists(path))
        {
            Accept(path);
        }
        else
        {
            Dialogs.Info(TitleText.Text, $"\"{name}\" wasn't found in this folder.");
        }
    }

    private void Accept(string path)
    {
        SelectedPath = path;
        if (Path.GetDirectoryName(path) is { } folder)
        {
            LastFolders[_rememberKey] = folder;
        }

        DialogResult = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (PathBox.IsKeyboardFocusWithin)
            {
                NavigateTyped();
            }
            else if (Entries.IsKeyboardFocusWithin && Entries.SelectedItem is FileEntry entry)
            {
                Activate(entry);
            }
            else
            {
                OnOk(this, new RoutedEventArgs());
            }

            e.Handled = true;
        }
        else if ((e.Key == Key.Back && Entries.IsKeyboardFocusWithin) || (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            OnUp(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            OnBack(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_history.TryPop(out string? previous))
        {
            Navigate(previous, record: false);
        }
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (_current is not null && Directory.GetParent(_current) is { } parent)
        {
            Navigate(parent.FullName);
        }
    }

    private void OnPlaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingPlaces && Places.SelectedItem is PlaceEntry place)
        {
            Navigate(place.Path);
        }
    }

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (Entries.SelectedItem is FileEntry { IsFolder: false } file)
        {
            NameBox.Text = file.Name;
        }
    }

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Entries, source) is ListBoxItem { DataContext: FileEntry entry })
        {
            Activate(entry);
        }
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private bool Matches(string name) =>
        _extensions.Length == 0 || _extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private static bool IsHidden(FileSystemInfo info) => (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };

    private static List<PlaceEntry> BuildPlaces()
    {
        var places = new List<PlaceEntry>();
        void Add(string name, string? path, string glyph)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !places.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                places.Add(new PlaceEntry(name, path, glyph));
            }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Home", home, "");
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "");
        Add("Downloads", DownloadsFolder() ?? Path.Combine(home, "Downloads"), "");
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "");
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "");
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "");
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                {
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.DriveType == DriveType.Removable ? "USB drive" : "Local disk"
                        : drive.VolumeLabel;
                    Add($"{label} ({drive.Name.TrimEnd('\\')})", drive.RootDirectory.FullName, "");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return places;
    }

    private static string? DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        return SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out string path) == 0 ? path : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);
}
