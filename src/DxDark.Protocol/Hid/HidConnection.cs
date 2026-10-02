using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static DxDark.Protocol.Hid.NativeMethods;

namespace DxDark.Protocol.Hid;

/// <summary>
/// An open HID collection using overlapped I/O: writes have timeouts, and a background thread
/// reads input reports until the connection is disposed or the device disappears.
/// </summary>
public sealed unsafe class HidConnection : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _writeLock = new();
    private readonly ManualResetEvent _writeEvent = new(false);
    private readonly ManualResetEvent _readEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly NativeOverlapped* _writeOverlapped;
    private readonly NativeOverlapped* _readOverlapped;
    private readonly byte* _writeBuffer;
    private readonly byte* _readBuffer;
    private readonly Thread _readThread;
    private int _disposed;
    private int _failed;

    private HidConnection(string path, SafeFileHandle handle, int inputReportLength, int outputReportLength)
    {
        Path = path;
        _handle = handle;
        InputReportLength = inputReportLength;
        OutputReportLength = outputReportLength;
        _writeOverlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _readOverlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _writeBuffer = (byte*)NativeMemory.AllocZeroed((nuint)outputReportLength);
        _readBuffer = (byte*)NativeMemory.AllocZeroed((nuint)inputReportLength);
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "HID input" };
        _readThread.Start();
    }

    public string Path { get; }

    public int InputReportLength { get; }

    public int OutputReportLength { get; }

    /// <summary>False once disposed or after an I/O error such as the device being unplugged.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _failed) == 0;

    /// <summary>Description of the most recent failed write ("timeout" or a Win32 error), for diagnostics.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Raised on the input thread for each report; byte 0 is the report ID. Handlers must return
    /// quickly and must not dispose this connection synchronously.
    /// </summary>
    public event Action<byte[]>? ReportReceived;

    /// <summary>Raised once, on a thread-pool thread, when I/O fails (usually the device was unplugged).</summary>
    public event Action? Disconnected;

    public static HidConnection Open(string path)
    {
        SafeFileHandle handle = CreateFile(
            path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open the light strip ({new Win32Exception(error).Message}).");
        }

        try
        {
            int input = Packet.ReportDataLength + 1;
            int output = Packet.ReportDataLength + 1;
            if (HidEnumerator.TryGetCaps(handle, out HidpCaps caps))
            {
                input = Math.Max(2, (int)caps.InputReportByteLength);
                output = Math.Max(2, (int)caps.OutputReportByteLength);
            }

            HidD_SetNumInputBuffers(handle, 64);
            return new HidConnection(path, handle, input, output);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Writes one output report (report ID first). Shorter reports are zero-padded.</summary>
    public bool Write(ReadOnlySpan<byte> report, int timeoutMs = 1000)
    {
        lock (_writeLock)
        {
            if (!IsOpen)
            {
                return false;
            }

            int length = OutputReportLength;
            var buffer = new Span<byte>(_writeBuffer, length);
            buffer.Clear();
            report[..Math.Min(report.Length, length)].CopyTo(buffer);

            *_writeOverlapped = default;
            _writeOverlapped->EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle();
            _writeEvent.Reset();

            if (!WriteFile(_handle, _writeBuffer, length, IntPtr.Zero, _writeOverlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    LastError = $"WriteFile error {error}: {new Win32Exception(error).Message}";
                    Fail();
                    return false;
                }

                if (!_writeEvent.WaitOne(timeoutMs))
                {
                    // Cancel, then wait for the cancellation so the buffer is no longer in use.
                    CancelIoEx(_handle, _writeOverlapped);
                    GetOverlappedResult(_handle, _writeOverlapped, out _, true);
                    LastError = $"timeout: the strip did not accept a report within {timeoutMs} ms";
                    return false;
                }
            }

            if (!GetOverlappedResult(_handle, _writeOverlapped, out int written, false))
            {
                int error = Marshal.GetLastWin32Error();
                LastError = $"write error {error}: {new Win32Exception(error).Message}";
                Fail();
                return false;
            }

            return written == length;
        }
    }

    private void ReadLoop()
    {
        var waitHandles = new WaitHandle[] { _readEvent, _stopEvent };
        int length = InputReportLength;

        while (Volatile.Read(ref _disposed) == 0)
        {
            *_readOverlapped = default;
            _readOverlapped->EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle();
            _readEvent.Reset();

            if (!ReadFile(_handle, _readBuffer, length, IntPtr.Zero, _readOverlapped))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    Fail();
                    return;
                }

                if (WaitHandle.WaitAny(waitHandles) == 1)
                {
                    CancelIoEx(_handle, _readOverlapped);
                    GetOverlappedResult(_handle, _readOverlapped, out _, true);
                    return;
                }
            }

            if (!GetOverlappedResult(_handle, _readOverlapped, out int read, false))
            {
                if (Marshal.GetLastWin32Error() != ErrorOperationAborted || Volatile.Read(ref _disposed) == 0)
                {
                    Fail();
                }

                return;
            }

            if (read <= 0)
            {
                continue;
            }

            byte[] report = new ReadOnlySpan<byte>(_readBuffer, read).ToArray();
            try
            {
                ReportReceived?.Invoke(report);
            }
            catch
            {
                // A faulty handler must not stop input processing.
            }
        }
    }

    private void Fail()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        _stopEvent.Set();
        ThreadPool.QueueUserWorkItem(_ => Disconnected?.Invoke());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopEvent.Set();
        if (Environment.CurrentManagedThreadId != _readThread.ManagedThreadId)
        {
            _readThread.Join();
        }

        // Taking the write lock waits for any write in flight. A Dispose from a ReportReceived
        // handler is also safe: the input loop touches no native memory after its handler returns.
        lock (_writeLock)
        {
            _handle.Dispose();
            NativeMemory.Free(_writeOverlapped);
            NativeMemory.Free(_readOverlapped);
            NativeMemory.Free(_writeBuffer);
            NativeMemory.Free(_readBuffer);
        }

        _writeEvent.Dispose();
        _readEvent.Dispose();
        _stopEvent.Dispose();
    }
}
