using System.Diagnostics;
using System.IO.Compression;

namespace DnWModManager.Core;

public static class ManagerUpdater
{
    public const string ExecutableName = "DnWModManager.exe";
    public const string ProductName = "DnW Mod Manager";
    public const string UpdatedArgument = "--updated";

    private const string BackupSuffix = ".old";
    private const int CleanupAttempts = 40;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMilliseconds(500);

    public static string ExecutablePath => Environment.ProcessPath;

    public static bool IsManagerExecutable(string path)
        => !string.IsNullOrEmpty(path) && File.Exists(path) && IsManagerProduct(ReadProductName(path));

    public static string Prepare(string download, string currentVersion)
    {
        string executable = Path.GetExtension(download).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? ExtractExecutable(download)
            : download;

        string product = ReadProductName(executable);
        if (!IsManagerProduct(product))
            throw new InvalidDataException("The downloaded file is not the DnW Mod Manager"
                                           + (string.IsNullOrEmpty(product) ? "." : " (it is " + product + ")."));

        string version = ModScanner.ReadAssemblyVersion(executable);
        if (!ModVersion.IsNewer(version, currentVersion))
            throw new InvalidDataException("The downloaded file is version " + (version ?? "?")
                                           + ", which is not newer than the installed " + currentVersion + ".");

        return executable;
    }

    private static string ReadProductName(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductName?.Trim(); }
        catch { return null; }
    }

    private static bool IsManagerProduct(string productName)
        => string.Equals(productName, ProductName, StringComparison.OrdinalIgnoreCase);

    private static string ExtractExecutable(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, ExecutableName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The downloaded package does not contain " + ExecutableName + ".");

        string target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zip))!, ExecutableName);
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    public static void Apply(string newExecutable, string currentVersion)
        => Apply(newExecutable, ExecutablePath, currentVersion, relaunch: true);

    public static void Apply(string newExecutable, string target, string currentVersion, bool relaunch)
    {
        if (!IsManagerExecutable(target))
            throw new InvalidOperationException("The running program is not the DnW Mod Manager.");

        string backup = target + BackupSuffix;
        if (File.Exists(backup))
        {
            if (!IsManagerExecutable(backup))
                throw new IOException(Path.GetFileName(backup) + " needs to be removed to proceed.");
            File.Delete(backup);
        }

        File.Move(target, backup);
        try
        {
            File.Copy(newExecutable, target);
        }
        catch
        {
            Restore(backup, target);
            throw;
        }

        if (!relaunch) return;
        try
        {
            Shell.Starter(new ProcessStartInfo(target, UpdatedArgument + " " + currentVersion)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target),
            });
        }
        catch
        {
            Restore(backup, target);
            throw;
        }
    }

    private static void Restore(string backup, string target)
    {
        try
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(backup, target);
        }
        catch { }
    }

    public static void RemoveLeftovers() => RemoveLeftovers(ExecutablePath);

    public static void RemoveLeftovers(string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        string backup = target + BackupSuffix;
        if (!IsManagerExecutable(backup)) return;

        for (int attempt = 0; attempt < CleanupAttempts; attempt++)
        {
            try
            {
                File.Delete(backup);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(CleanupInterval);
        }
    }
}
