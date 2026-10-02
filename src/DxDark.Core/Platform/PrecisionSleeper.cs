using System.Runtime.InteropServices;

namespace DxDark.Core.Platform;

/// <summary>
/// Sleeps with sub-millisecond precision using a high-resolution waitable timer (Windows 10 1803+),
/// without changing the system-wide timer resolution. Falls back to Thread.Sleep.
/// </summary>
public sealed class PrecisionSleeper : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private IntPtr _timer;

    public PrecisionSleeper()
    {
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    public void Sleep(double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        if (_timer != IntPtr.Zero)
        {
            long dueTime = -(long)(milliseconds * 10_000); // relative, in 100 ns units
            if (SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer, Infinite);
                return;
            }
        }

        Thread.Sleep(TimeSpan.FromMilliseconds(milliseconds));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
        {
            CloseHandle(_timer);
            _timer = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argToCompletionRoutine, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
