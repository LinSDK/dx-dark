using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace DxDark.Core.Platform;

/// <summary>
/// "Start with Windows" as a shortcut in the user's Startup folder (shell:startup), which can be
/// seen and removed like any other file. The setting itself lives in DXDark.ini; nothing is
/// written to the registry.
/// </summary>
public static class StartupShortcut
{
    public const string MinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DX Dark";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DX Dark.lnk");

    public static bool IsEnabled(string? linkPath = null) => File.Exists(linkPath ?? DefaultPath);

    public static void SetEnabled(bool enabled, string executablePath, string? linkPath = null)
    {
        string path = linkPath ?? DefaultPath;
        if (enabled)
        {
            Create(path, executablePath, MinimizedArgument);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Makes Windows startup match the setting in DXDark.ini: creates the shortcut (pointing at
    /// <paramref name="executablePath"/>) or deletes it. Also removes every registry value earlier
    /// versions left behind. <paramref name="wanted"/> null means "not set yet": keep whatever
    /// earlier versions had. Returns the setting now in effect.
    /// </summary>
    public static bool Apply(bool? wanted, string executablePath)
    {
        bool hadRunEntry = false;
        try
        {
            hadRunEntry = RemoveRegistryEntries();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Could not remove old registry entries: {ex.Message}");
        }

        bool enable = wanted ?? (hadRunEntry || IsEnabled());
        try
        {
            if (enable && !string.Equals(ReadTarget(DefaultPath), executablePath, StringComparison.OrdinalIgnoreCase))
            {
                Create(DefaultPath, executablePath, MinimizedArgument);
                Log.Info("Startup shortcut created");
            }
            else if (!enable && IsEnabled())
            {
                File.Delete(DefaultPath);
                RemoveApprovalEntry("StartupFolder", "DX Dark.lnk");
                Log.Info("Startup shortcut removed");
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not update the startup shortcut: {ex.Message}");
        }

        return enable;
    }

    public static void Create(string linkPath, string targetPath, string arguments)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
            link.SetDescription("DX Dark");
            link.SetIconLocation(targetPath, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The program a shortcut starts, or null if it cannot be read.</summary>
    public static string? ReadTarget(string linkPath)
    {
        if (!File.Exists(linkPath))
        {
            return null;
        }

        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.ToString();
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// Deletes the Run value versions 0.0.1–0.0.2 used for starting with Windows, and the record
    /// Windows keeps of it for Task Manager. True if the Run value was there.
    /// </summary>
    private static bool RemoveRegistryEntries()
    {
        bool had = false;
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                had = true;
                Log.Info("Removed the old Start with Windows entry from the registry");
            }
        }

        RemoveApprovalEntry("Run", RunValueName);
        return had;
    }

    private static void RemoveApprovalEntry(string list, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{list}", writable: true);
        if (key?.GetValue(name) is not null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
