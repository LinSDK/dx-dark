using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using DxDark.App.Infrastructure;
using DxDark.Core;
using DxDark.Core.Platform;
using DxDark.Core.Settings;

namespace DxDark.App.ViewModels;

public sealed record ExitChoice(ExitAction Action, string Name);

/// <summary>The settings sheet: startup, lock/sleep behavior, power limit, speed test, files.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly LightController _controller;
    private string _speedTestText = "";
    private bool _speedTestRunning;

    public SettingsViewModel(LightController controller)
    {
        _controller = controller;
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile);
        OpenSettingsFolderCommand = new RelayCommand(() => OpenPath(_controller.Store.Directory));
        OpenLogCommand = new RelayCommand(() => OpenPath(Log.FilePath ?? _controller.Store.Directory));
        SpeedTestCommand = new RelayCommand(RunSpeedTest, () => !_speedTestRunning && _controller.IsConnected);
    }

    private GeneralSettings General => _controller.Settings.General;

    public IReadOnlyList<ExitChoice> ExitChoices { get; } =
    [
        new(ExitAction.TurnOff, "Turn the lights off"),
        new(ExitAction.KeepLastColors, "Leave the lights as they are"),
    ];

    public ICommand OpenConfigFileCommand { get; }

    public ICommand OpenSettingsFolderCommand { get; }

    public ICommand OpenLogCommand { get; }

    public ICommand SpeedTestCommand { get; }

    /// <summary>The settings file (DXDark.ini).</summary>
    public string ConfigFilePath => _controller.Store.FilePath;

    /// <summary>Stored in DXDark.ini; the Startup-folder shortcut follows it.</summary>
    public bool StartWithWindows
    {
        get => General.StartWithWindows == true;
        set
        {
            General.StartWithWindows = value;
            Save();
            StartupShortcut.Apply(value, Environment.ProcessPath ?? "");
            if (StartupShortcut.IsEnabled() != value)
            {
                Dialogs.Info("Start with Windows", "Windows did not allow the change to the Startup folder. The log has the details.");
            }

            OnPropertyChanged();
        }
    }

    public ExitChoice OnExit
    {
        get => ExitChoices.FirstOrDefault(c => c.Action == General.OnExit) ?? ExitChoices[0];
        set
        {
            General.OnExit = value?.Action ?? ExitAction.TurnOff;
            Save();
        }
    }

    public bool OffWhenLocked { get => General.OffWhenLocked; set { General.OffWhenLocked = value; Save(); } }

    public bool OffWhenDisplayOff { get => General.OffWhenDisplayOff; set { General.OffWhenDisplayOff = value; Save(); } }

    public bool WarnAboutDxLight { get => General.WarnAboutDxLight; set { General.WarnAboutDxLight = value; Save(); } }

    public double PowerLimit
    {
        get => _controller.Settings.Calibration.PowerLimit;
        set
        {
            _controller.Settings.Calibration.PowerLimit = Math.Clamp(value, 0.2, 1);
            _controller.CalibrationChanged();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PowerWarning));
        }
    }

    public bool PowerWarning => _controller.Settings.Calibration.PowerLimit > 0.34;

    public string SpeedTestText { get => _speedTestText; private set => Set(ref _speedTestText, value); }

    public string SettingsFolder => _controller.Store.Directory;

    public string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? SettingsStore.Version;

    public void Refresh() => OnAllPropertiesChanged();

    private void Save() => _controller.Store.SaveSoon();

    /// <summary>Opens DXDark.ini in the program Windows uses for .ini files (Notepad unless changed).</summary>
    private void OpenConfigFile()
    {
        _controller.Store.SaveNow(); // so the file shows the latest changes
        OpenPath(_controller.Store.FilePath);
    }

    private async void RunSpeedTest()
    {
        _speedTestRunning = true;
        CommandManager.InvalidateRequerySuggested();
        SpeedTestText = "Testing…";
        try
        {
            SpeedTestResult? result = await _controller.RunSpeedTestAsync();
            SpeedTestText = result is null
                ? "No strip connected."
                : $"{result.FramesPerSecond:0} frames/s with {result.Zones} zones (test limit 120) · slowest {result.SlowestFrameMs:0.0} ms" +
                  (result.StillResponding ? "" : " · the strip stopped responding: unplug it and plug it back in");
        }
        catch (Exception ex)
        {
            SpeedTestText = $"The test failed: {ex.Message}";
        }
        finally
        {
            _speedTestRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path}: {ex.Message}");
        }
    }
}
