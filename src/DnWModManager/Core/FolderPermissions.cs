using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DnWModManager.Core;

public enum FolderAccess
{
    Unknown,
    Writable,
    Denied,
}

public static class FolderPermissions
{
    public const string GrantArgument = "--grant-access";

    private const int NotAGameFolderExitCode = 1;
    private const int FailedExitCode = 2;

    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;

    public static FolderAccess Check(GameInstall install)
    {
        if (install is null || !Directory.Exists(install.GameDirectory)) return FolderAccess.Unknown;

        using var token = Elevation.IsElevated ? Elevation.OpenStandardUserToken() : null;

        var result = FolderAccess.Writable;
        foreach (string folder in CheckedFolders(install).Where(Directory.Exists))
        {
            var access = token is null ? Probe(folder) : CheckAccess(folder, token);
            if (access == FolderAccess.Denied) return FolderAccess.Denied;
            if (access == FolderAccess.Unknown) result = FolderAccess.Unknown;
        }
        return result;
    }

    public static async Task<bool> FixAsync(GameInstall install)
    {
        if (await Task.Run(() => Check(install)).ConfigureAwait(false) != FolderAccess.Denied) return true;

        if (Elevation.IsElevated)
        {
            await Task.Run(() => Grant(install.GameDirectory)).ConfigureAwait(false);
        }
        else
        {
            int? exitCode = await Elevation.RunElevatedAsync(new[] { GrantArgument, install.GameDirectory }).ConfigureAwait(false);
            if (exitCode is null) return false;
            if (exitCode != 0) throw new IOException(DescribeExitCode(exitCode.Value));
        }

        if (await Task.Run(() => Check(install)).ConfigureAwait(false) == FolderAccess.Denied)
            throw new IOException("Windows is blocking changes to the game folder.");

        ManagerLog.Info(install, "Game folder permissions set.");
        return true;
    }

    public static int GrantFromCommandLine(string gameDirectory)
    {
        try
        {
            Grant(gameDirectory);
            return 0;
        }
        catch (ArgumentException)
        {
            return NotAGameFolderExitCode;
        }
        catch (Exception e)
        {
            return e.HResult is 0 or NotAGameFolderExitCode ? FailedExitCode : e.HResult;
        }
    }

    public static void Grant(string gameDirectory)
    {
        if (!GameInstall.LooksLikeGameDirectory(gameDirectory))
            throw new ArgumentException(gameDirectory + " is not a Drag'n Wash folder.", nameof(gameDirectory));

        var install = GameInstall.At(gameDirectory);
        var rule = new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        AddRule(new DirectoryInfo(install.GameDirectory), rule);

        foreach (string tree in new[] { install.ModsDirectory, install.LoaderDirectory, install.BepInExDirectory, install.UserDataDirectory })
        {
            if (!Directory.Exists(tree)) continue;
            foreach (var directory in WithSubdirectories(new DirectoryInfo(tree)))
                if (directory.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected)
                    AddRule(directory, rule);
        }
    }

    internal static FolderAccess Probe(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return FolderAccess.Unknown;

        string probe = Path.Combine(directory, ".dnwmm-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return FolderAccess.Writable;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderAccess.Denied;
        }
        catch (IOException)
        {
            return FolderAccess.Unknown;
        }
    }

    internal static FolderAccess CheckAccess(string directory, SafeAccessTokenHandle token)
    {
        try
        {
            byte[] descriptor = new DirectoryInfo(directory)
                .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
                .GetSecurityDescriptorBinaryForm();

            var mapping = new GenericMapping
            {
                GenericRead = 0x120089,
                GenericWrite = 0x120116,
                GenericExecute = 0x1200A0,
                GenericAll = 0x1F01FF,
            };
            var privileges = new byte[256];
            uint privilegesLength = (uint)privileges.Length;

            if (!AccessCheck(descriptor, token, FileAddFile | FileAddSubdirectory, ref mapping,
                    privileges, ref privilegesLength, out _, out bool granted))
                return FolderAccess.Unknown;

            return granted ? FolderAccess.Writable : FolderAccess.Denied;
        }
        catch
        {
            return FolderAccess.Unknown;
        }
    }

    private static IEnumerable<string> CheckedFolders(GameInstall install) => new[]
    {
        install.GameDirectory,
        install.ModsDirectory,
        install.ModConfigDirectory,
        install.LoaderDirectory,
        install.BepInExDirectory,
        install.BepInExConfigDirectory,
        install.BepInExPluginsDirectory,
        install.UserDataDirectory,
    };

    private static void AddRule(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        var security = directory.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
    }

    private static IEnumerable<DirectoryInfo> WithSubdirectories(DirectoryInfo root)
    {
        yield return root;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        foreach (var directory in root.EnumerateDirectories("*", options))
            yield return directory;
    }

    private static string DescribeExitCode(int exitCode)
    {
        if (exitCode == NotAGameFolderExitCode) return "This folder is not a Drag'n Wash folder.";
        if ((exitCode & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)) return new Win32Exception(exitCode & 0xFFFF).Message;
        return "Error 0x" + exitCode.ToString("X8") + ".";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AccessCheck(byte[] securityDescriptor, SafeAccessTokenHandle clientToken, uint desiredAccess,
        ref GenericMapping genericMapping, byte[] privilegeSet, ref uint privilegeSetLength,
        out uint grantedAccess, out bool accessStatus);
}
