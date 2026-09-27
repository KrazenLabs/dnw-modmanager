using System.Diagnostics;
using System.IO.Compression;

namespace DnWModManager.Core;

public static class ManagerUpdater
{
    public const string WindowsExecutableName = "DnWModManager.exe";
    public const string LinuxExecutableName = "DnWModManager";
    public const string LinuxReleaseAssetName = "DnWModManager-linux-x64.zip";
    public const string ProductName = "DnW Mod Manager";
    public const string UpdatedArgument = "--updated";
    public const string VersionArgument = "--version";

    private const string BackupSuffix = ".old";
    private const int CleanupAttempts = 40;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(20);

    public static string ExecutableName => Platform.IsWindows ? WindowsExecutableName : LinuxExecutableName;

    public static string ReleaseAssetName => Platform.IsWindows ? WindowsExecutableName : LinuxReleaseAssetName;

    public static string ExecutablePath => Environment.ProcessPath;

    public static string VersionLine(string version) => ProductName + " " + version;

    public static ModSource ForThisPlatform(ModSource source)
    {
        if (source is null || Platform.IsWindows) return source;
        return source.PlatformAssets.TryGetValue(Platform.RuntimeId, out string asset) && !string.IsNullOrWhiteSpace(asset)
            ? source
            : source.WithAsset(ReleaseAssetName);
    }

    public static bool IsManagerExecutable(string path)
        => !string.IsNullOrEmpty(path) && File.Exists(path) && IsManagerProduct(ReadProductName(path));

    public static string Prepare(string download, string currentVersion)
    {
        string executable = Path.GetExtension(download).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? ExtractExecutable(download)
            : download;
        Platform.MakeExecutable(executable);

        string product = ReadProductName(executable);
        if (!IsManagerProduct(product))
            throw new InvalidDataException("The downloaded file is not the DnW Mod Manager"
                                           + (string.IsNullOrEmpty(product) ? "." : " (it is " + product + ")."));

        string version = ReadVersion(executable);
        if (!ModVersion.IsNewer(version, currentVersion))
            throw new InvalidDataException("The downloaded file is version " + (version ?? "?")
                                           + ", which is not newer than the installed " + currentVersion + ".");

        return executable;
    }

    private static string ReadProductName(string path)
    {
        if (!Platform.IsWindows) return ProbeVersionLine(path) is { } line && line.StartsWith(ProductName + " ", StringComparison.Ordinal) ? ProductName : null;
        try { return FileVersionInfo.GetVersionInfo(path).ProductName?.Trim(); }
        catch { return null; }
    }

    private static string ReadVersion(string path)
    {
        if (Platform.IsWindows) return ModScanner.ReadAssemblyVersion(path);
        string line = ProbeVersionLine(path);
        return line is not null && line.StartsWith(ProductName + " ", StringComparison.Ordinal) ? line[(ProductName.Length + 1)..].Trim() : null;
    }

    private static string ProbeVersionLine(string path)
    {
        try
        {
            if (!Platform.IsExecutable(path)) return null;
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(VersionArgument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(VersionProbeTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return output.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch
        {
            return null;
        }
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
            Platform.MakeExecutable(target);
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
                UseShellExecute = Platform.IsWindows,
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
