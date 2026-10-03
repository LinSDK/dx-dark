using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DxDark.App.Infrastructure;
using DxDark.Capture;
using DxDark.Core;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;

namespace DxDark.App.ViewModels;

public sealed record MonitorChoice(string? DeviceName, string Label);

/// <summary>The settings strip: the preset dropdown and every picture setting of the selected preset.</summary>
public sealed class PresetViewModel : ObservableObject
{
    private readonly LightController _controller;

    public PresetViewModel(LightController controller)
    {
        _controller = controller;
        ProfileNames = new ObservableCollection<string>(Settings.Profiles.Select(p => p.Name));
        Monitors = [];
        RefreshMonitors();

        SaveAsCommand = new RelayCommand(SaveAs);
        RenameCommand = new RelayCommand(Rename);
        DeleteCommand = new RelayCommand(Delete, () => Settings.Profiles.Count > 1);
        ResetCommand = new RelayCommand(Reset);
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
    }

    private AppSettings Settings => _controller.Settings;

    private Profile Profile => Settings.GetActiveProfile();

    public ObservableCollection<string> ProfileNames { get; }

    public ObservableCollection<MonitorChoice> Monitors { get; }

    public ICommand SaveAsCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand ExportCommand { get; }

    public string? SelectedProfile
    {
        get => Settings.ActiveProfile;
        set
        {
            if (value is not null && value != Settings.ActiveProfile)
            {
                _controller.ActivateProfile(value);
                OnAllPropertiesChanged();
            }
        }
    }

    /// <summary>Values the reset buttons go back to: the built-in preset of the same name, else Balanced.</summary>
    public Profile Defaults => BuiltInProfiles.Find(Profile.Name) ?? BuiltInProfiles.Find(BuiltInProfiles.DefaultName)!;

    public MonitorChoice? SelectedMonitor
    {
        get => Monitors.FirstOrDefault(m => m.DeviceName == Settings.MonitorDeviceName) ?? Monitors.FirstOrDefault();
        set
        {
            if (value is not null && value.DeviceName != Settings.MonitorDeviceName)
            {
                _controller.SetMonitor(value.DeviceName);
                OnPropertyChanged();
            }
        }
    }

    public bool ShowMonitorChoice => Monitors.Count > 2;

    // ── Capture ────────────────────────────────────────────────────────────

    public double FrameRate { get => Profile.FrameRate; set => Edit(p => p.FrameRate = (int)Math.Round(value)); }

    public double ColorFocus { get => Profile.ColorFocus; set => Edit(p => p.ColorFocus = value); }

    public bool DetectBlackBars { get => Profile.DetectBlackBars; set => Edit(p => p.DetectBlackBars = value); }

    // ── Motion ─────────────────────────────────────────────────────────────

    public double Smoothing { get => Profile.Smoothing; set => Edit(p => p.Smoothing = value); }

    public double SpatialBlend { get => Profile.SpatialBlend; set => Edit(p => p.SpatialBlend = value); }

    // ── Color ──────────────────────────────────────────────────────────────

    public double Brightness { get => Profile.Brightness; set => Edit(p => p.Brightness = value); }

    public double Saturation { get => Profile.Saturation; set => Edit(p => p.Saturation = value); }

    public double Vibrance { get => Profile.Vibrance; set => Edit(p => p.Vibrance = value); }

    public double Contrast { get => Profile.Contrast; set => Edit(p => p.Contrast = value); }

    public double Gamma { get => Profile.Gamma; set => Edit(p => p.Gamma = value); }

    public double Temperature { get => Profile.Temperature; set => Edit(p => p.Temperature = value); }

    public double BlackThreshold { get => Profile.BlackThreshold; set => Edit(p => p.BlackThreshold = value); }

    // ── Filter ─────────────────────────────────────────────────────────────

    public double FilterBlur { get => Profile.FilterBlur; set => Edit(p => p.FilterBlur = value); }

    public double FilterPixelate { get => Profile.FilterPixelate; set => Edit(p => p.FilterPixelate = value); }

    public double FilterHueShift { get => Profile.FilterHueShift; set => Edit(p => p.FilterHueShift = value); }

    public double FilterPosterize
    {
        get => Profile.FilterPosterize;
        set => Edit(p => p.FilterPosterize = value < 1.5 ? 0 : (int)Math.Round(value));
    }

    public bool FilterInvert { get => Profile.FilterInvert; set => Edit(p => p.FilterInvert = value); }

    /// <summary>Re-reads everything (e.g. after the tray menu switched presets).</summary>
    public void Refresh()
    {
        SyncNames();
        OnAllPropertiesChanged();
    }

    /// <summary>Sets the brightness of the active preset (used by the tray menu).</summary>
    public void SetBrightness(double value)
    {
        Profile.Brightness = Math.Clamp(value, 0, 1);
        _controller.ProfileChanged();
        OnPropertyChanged(nameof(Brightness));
    }

    private void Edit(Action<Profile> change, [CallerMemberName] string? property = null)
    {
        change(Profile);
        _controller.ProfileChanged();
        OnPropertyChanged(property);
    }

    /// <summary>Saves the current settings under a new name and switches to it.</summary>
    private void SaveAs()
    {
        // The current name is offered first: keeping it saves over the current preset (after asking),
        // a new name saves a new preset.
        string? name = Dialogs.Prompt("Save preset", "", Profile.Name);
        if (name is null)
        {
            return;
        }

        Profile? existing = Settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            AddProfile(Profile.CloneAs(name));
            return;
        }

        if (!Dialogs.Confirm("Overwrite preset", $"Overwrite the preset \"{existing.Name}\" with the current settings?", "Overwrite", destructive: true))
        {
            return;
        }

        if (existing != Profile)
        {
            Settings.Profiles[Settings.Profiles.IndexOf(existing)] = Profile.CloneAs(existing.Name);
            _controller.ActivateProfile(existing.Name);
        }

        _controller.ProfileChanged();
        _controller.Store.SaveNow();
        SyncNames();
        OnAllPropertiesChanged();
    }

    private void AddProfile(Profile profile)
    {
        Settings.Profiles.Add(profile);
        SyncNames();
        _controller.ActivateProfile(profile.Name);
        OnAllPropertiesChanged();
    }

    private void Rename()
    {
        string? name = Dialogs.Prompt("Rename preset", "", Profile.Name, "Rename");
        if (name is null || name == Profile.Name)
        {
            return;
        }

        if (Settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Info("Rename preset", $"A preset called \"{name}\" already exists.");
            return;
        }

        Profile.Name = name;
        Settings.ActiveProfile = name;
        _controller.ProfileChanged();
        SyncNames();
        OnAllPropertiesChanged();
    }

    private void Delete()
    {
        if (Settings.Profiles.Count <= 1 || !Dialogs.Confirm("Delete preset", $"Delete the preset \"{Profile.Name}\"?", "Delete", destructive: true))
        {
            return;
        }

        Settings.Profiles.Remove(Profile);
        _controller.ActivateProfile(Settings.Profiles[0].Name);
        SyncNames();
        OnAllPropertiesChanged();
    }

    private void Reset()
    {
        string name = Profile.Name;
        int index = Settings.Profiles.IndexOf(Profile);
        Settings.Profiles[index] = Defaults.CloneAs(name);
        _controller.ProfileChanged();
        OnAllPropertiesChanged();
    }

    private void Import()
    {
        string? path = FilePicker.Open("Import preset", "Import", "DX Dark presets (.ini, .json)", [".ini", ".json"], "preset", Environment.SpecialFolder.MyDocuments);
        if (path is null)
        {
            return;
        }

        try
        {
            Profile? profile = SettingsStore.ImportProfile(path);
            if (profile is null)
            {
                Dialogs.Info("Import preset", "That file doesn't contain a DX Dark preset.");
                return;
            }

            profile.Name = UniqueName(profile.Name);
            AddProfile(profile);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Dialogs.Info("Import preset", $"That file could not be read as a preset.\n\n{ex.Message}");
        }
    }

    private void Export()
    {
        string? path = FilePicker.Save(
            "Export preset", "DX Dark preset (.ini)", ".ini", $"{string.Concat(Profile.Name.Split(Path.GetInvalidFileNameChars()))}.ini",
            "preset", Environment.SpecialFolder.MyDocuments);
        if (path is null)
        {
            return;
        }

        try
        {
            SettingsStore.ExportProfile(Profile, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Info("Export preset", $"The preset could not be saved there.\n\n{ex.Message}");
        }
    }


    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int i = 2; Settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName} {i}";
        }

        return name;
    }

    private void SyncNames()
    {
        var names = Settings.Profiles.Select(p => p.Name).ToList();
        if (!names.SequenceEqual(ProfileNames))
        {
            ProfileNames.Clear();
            foreach (string name in names)
            {
                ProfileNames.Add(name);
            }
        }

        OnPropertyChanged(nameof(SelectedProfile));
    }

    public void RefreshMonitors()
    {
        Monitors.Clear();
        Monitors.Add(new MonitorChoice(null, "Primary monitor"));
        try
        {
            foreach (MonitorInfo monitor in MonitorEnumerator.List())
            {
                Monitors.Add(new MonitorChoice(monitor.DeviceName, monitor.Label));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list monitors: {ex.Message}");
        }

        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(ShowMonitorChoice));
    }
}
