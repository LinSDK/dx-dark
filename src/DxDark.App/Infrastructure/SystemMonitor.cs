using System.Runtime.InteropServices;
using System.Windows.Interop;
using DxDark.Core;
using Microsoft.Win32;

namespace DxDark.App.Infrastructure;

/// <summary>
/// Pauses the lights while the PC is locked, asleep or the display is off, and resumes afterwards.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly LightController _controller;
    private readonly HwndSource _window;
    private readonly IntPtr _displayNotification;

    public SystemMonitor(LightController controller)
    {
        _controller = controller;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // A hidden window to receive display on/off notifications.
        _window = new HwndSource(new HwndSourceParameters("DX Dark power events") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        Guid setting = ConsoleDisplayState;
        _displayNotification = RegisterPowerSettingNotification(_window.Handle, ref setting, 0);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
                if (_controller.Settings.General.OffWhenLocked)
                {
                    _controller.Suspend("locked");
                }

                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
                _controller.Resume("locked");
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _controller.Suspend("sleep");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _controller.Resume("sleep");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmPowerBroadcast && wParam.ToInt32() == PbtPowerSettingChange && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID (16 bytes), DataLength (4), Data (4): 0 off, 1 on, 2 dimmed.
            var guid = Marshal.PtrToStructure<Guid>(lParam);
            if (guid == ConsoleDisplayState)
            {
                int state = Marshal.ReadInt32(lParam, 20);
                if (state == 0 && _controller.Settings.General.OffWhenDisplayOff)
                {
                    _controller.Suspend("display off");
                }
                else if (state != 0)
                {
                    _controller.Resume("display off");
                }
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_displayNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_displayNotification);
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
