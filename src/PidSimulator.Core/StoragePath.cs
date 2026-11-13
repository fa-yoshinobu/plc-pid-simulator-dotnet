using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PidSimulator.Core;

internal static class StoragePath
{
    // Packaged launchers can redirect AppData. Shells outside that package need the physical path.
    public static string ResolveExisting(string path)
    {
        if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= result.Capacity) throw new IOException("保存先のパスが長すぎます。");
        string fullPath = result.ToString();
        return fullPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
            ? @"\\" + fullPath[8..]
            : fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath[4..] : fullPath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
