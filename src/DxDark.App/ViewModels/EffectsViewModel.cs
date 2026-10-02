using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DxDark.App.Infrastructure;
using DxDark.Capture;
using DxDark.Core;
using DxDark.Core.Effects;
using DxDark.Core.Settings;

namespace DxDark.App.ViewModels;

/// <summary>One tile in the effect gallery.</summary>
public sealed class EffectTile : ObservableObject
{
    private readonly EffectsViewModel _owner;
    private ImageSource? _thumbnail;

    public EffectTile(EffectsViewModel owner, string id, string name, bool isVideo, bool usesColor)
    {
        _owner = owner;
        Id = id;
        Name = name;
        IsVideo = isVideo;
        UsesColor = usesColor;
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsVideo { get; }

    public bool UsesColor { get; }

    public ICommand RemoveCommand { get; }

    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

    public bool IsSelected
    {
        get => _owner.SelectedId == Id && _owner.IsEffectMode;
        set
        {
            if (value)
            {
                _owner.Select(this);
            }
        }
    }

    internal void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>
/// The effect gallery: built-in canvases plus the user's own videos. Selecting a tile switches the
/// strip to that effect, which is sampled exactly like the screen.
/// </summary>
public sealed class EffectsViewModel : ObservableObject
{
    private readonly LightController _controller;
    private readonly Dispatcher _ui;
    private bool _importing;

    public EffectsViewModel(LightController controller, Dispatcher ui)
    {
        _controller = controller;
        _ui = ui;
        Tiles = [];
        foreach (BuiltInEffect effect in BuiltInEffects.All)
        {
            var tile = new EffectTile(this, effect.Id, effect.Name, isVideo: false, effect.UsesColor);
            tile.Thumbnail = RenderBuiltIn(effect.Id);
            Tiles.Add(tile);
        }

        foreach (VideoEffect video in controller.Settings.Lighting.Videos.ToList())
        {
            AddVideoTile(video);
        }

        AddVideoCommand = new RelayCommand(AddVideo, () => !_importing);
    }

    public ObservableCollection<EffectTile> Tiles { get; }

    public ICommand AddVideoCommand { get; }

    public string SelectedId => _controller.Settings.Lighting.EffectId;

    public bool IsEffectMode => _controller.Settings.Lighting.Mode == LightMode.Effect;

    public EffectTile? SelectedTile => Tiles.FirstOrDefault(t => t.Id == SelectedId);

    public bool ShowColor => SelectedTile?.UsesColor == true;

    public double Speed
    {
        get => _controller.Settings.Lighting.EffectSpeed;
        set
        {
            _controller.SetEffectSpeed(value);
            OnPropertyChanged();
        }
    }

    public string SolidColor
    {
        get => _controller.Settings.Lighting.SolidColor;
        set
        {
            _controller.SetSolidColor(value);
            OnPropertyChanged();
            RefreshColorThumbnails();
        }
    }

    public IReadOnlyList<SwatchChoice> Swatches { get; } =
    [
        new("Warm white", "#FFB46B"),
        new("White", "#FFFFFF"),
        new("Red", "#FF1E1E"),
        new("Orange", "#FF6A00"),
        new("Yellow", "#FFE600"),
        new("Green", "#00E640"),
        new("Cyan", "#00D7FF"),
        new("Blue", "#1E50FF"),
        new("Purple", "#A030FF"),
        new("Pink", "#FF4F8B"),
    ];

    public ICommand SwatchCommand => new RelayCommand(p =>
    {
        if (p is SwatchChoice swatch)
        {
            SolidColor = swatch.Hex;
        }
    });

    internal void Select(EffectTile tile)
    {
        _controller.SetEffect(tile.Id);
        Refresh();
    }

    internal void Remove(EffectTile tile)
    {
        if (!tile.IsVideo || !Dialogs.Confirm("Remove video", $"Remove \"{tile.Name}\" from your effects?", "Remove", destructive: true))
        {
            return;
        }

        _controller.RemoveVideo(tile.Id);
        Tiles.Remove(tile);
        Refresh();
    }

    public void Refresh()
    {
        foreach (EffectTile tile in Tiles)
        {
            tile.RefreshSelection();
        }

        OnPropertyChanged(nameof(IsEffectMode));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(SolidColor));
    }

    private async void AddVideo()
    {
        string? path = FilePicker.Open(
            "Add a video as an effect", "Add", "Videos: MP4, MOV, M4V, WMV, AVI, MKV",
            [".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv"], "video", Environment.SpecialFolder.MyVideos, fileGlyph: "");
        if (path is null)
        {
            return;
        }

        _importing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            VideoEffect video = await Task.Run(() => _controller.AddVideo(path));
            EffectTile tile = AddVideoTile(video);
            Select(tile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Adding {path} failed: {ex.Message}");
            Dialogs.Info("Add video", $"Windows could not play this video, so it was not added.\n\n{ex.Message}");
        }
        finally
        {
            _importing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private EffectTile AddVideoTile(VideoEffect video)
    {
        var tile = new EffectTile(this, video.Id, video.Name, isVideo: true, usesColor: false);
        Tiles.Add(tile);
        string path = _controller.VideoPath(video);
        Task.Run(() =>
        {
            BitmapSource? thumbnail = ReadVideoThumbnail(path);
            if (thumbnail is not null)
            {
                _ui.BeginInvoke(() => tile.Thumbnail = thumbnail);
            }
        });
        return tile;
    }

    private void RefreshColorThumbnails()
    {
        foreach (EffectTile tile in Tiles.Where(t => t.UsesColor))
        {
            tile.Thumbnail = RenderBuiltIn(tile.Id);
        }
    }

    /// <summary>Rebuilds the video tiles (after the settings file was edited by hand).</summary>
    public void ReloadVideos()
    {
        foreach (EffectTile tile in Tiles.Where(t => t.IsVideo).ToList())
        {
            Tiles.Remove(tile);
        }

        foreach (VideoEffect video in _controller.Settings.Lighting.Videos.ToList())
        {
            AddVideoTile(video);
        }

        RefreshColorThumbnails();
        Refresh();
    }

    private BitmapSource RenderBuiltIn(string id)
    {
        Core.Color.ColorMath.TryParseHex(_controller.Settings.Lighting.SolidColor, out byte r, out byte g, out byte b);
        var frame = new CapturedFrame();
        BuiltInEffects.Render(id, frame, BuiltInEffects.ThumbnailTime(id), r, g, b, 160, 90);
        return ToBitmap(frame);
    }

    /// <summary>A frame from about a second into the video (off the UI thread).</summary>
    private static BitmapSource? ReadVideoThumbnail(string path)
    {
        try
        {
            using var reader = new VideoReader(path, 160);
            var frame = new CapturedFrame();
            double time = 0;
            for (int i = 0; i < 300 && time < 1.0; i++)
            {
                if (!reader.ReadFrame(frame, out time))
                {
                    break;
                }
            }

            return frame.Width > 0 ? ToBitmap(frame) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"No thumbnail for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static BitmapSource ToBitmap(CapturedFrame frame)
    {
        BitmapSource bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed record SwatchChoice(string Name, string Hex)
{
    public Brush Brush { get; } = CreateBrush(Hex);

    private static Brush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
