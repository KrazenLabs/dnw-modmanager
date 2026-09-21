using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DnWModManager.Core;

public static class Elevation
{
    private const int ErrorCancelled = 1223;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationTypeFull = 2;

    public static bool IsElevated { get; } = ReadElevation();

    private static bool ReadElevation()
    {
        try { return Environment.IsPrivilegedProcess; }
        catch { return false; }
    }

    public static async Task<int?> RunElevatedAsync(IEnumerable<string> arguments)
    {
        string executable = Environment.ProcessPath;
        if (!ManagerUpdater.IsManagerExecutable(executable))
            throw new InvalidOperationException("The running program is not the DnW Mod Manager.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable),
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }

        if (process is null) throw new InvalidOperationException("Failed to start the Mod Manager as administrator.");
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    public static SafeAccessTokenHandle OpenStandardUserToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var own)) return null;
        using (own)
        {
            if (!GetTokenInformation(own, TokenElevationTypeClass, out int type, sizeof(int), out _)
                || type != TokenElevationTypeFull)
                return null;

            if (!GetLinkedToken(own, TokenLinkedTokenClass, out IntPtr linked, IntPtr.Size, out _)) return null;
            return new SafeAccessTokenHandle(linked);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    private static extern bool GetLinkedToken(SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        out IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}
