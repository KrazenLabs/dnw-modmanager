using System.Text;

namespace DnWModManager.Core;

public static class Report
{
    public static string Write(ScanResult scan, string managerVersion)
    {
        var text = new StringBuilder();

        text.AppendLine("DnW Mod Manager " + managerVersion + " - install report");
        text.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        text.AppendLine();

        var log = LogReader.ReadLatest(scan.Install);

        WriteInstall(text, scan, log);
        WriteLoader(text, scan);
        WriteMods(text, scan);
        WriteIssues(text, scan);
        WriteLog(text, scan, log);

        return text.ToString();
    }

    private static void WriteInstall(StringBuilder text, ScanResult scan, LogReader.LogFile log)
    {
        string version = GameVersion.FromGameFiles(scan.Install);
        string lastRun = GameVersion.FromLog(log);

        text.AppendLine("Game");
        text.AppendLine("  Folder:  " + scan.Install.GameDirectory);
        text.AppendLine("  Source:  " + scan.Install.Source.Label());
        text.AppendLine("  Version: " + (version, lastRun) switch
        {
            (null, null) => "unknown",
            (null, _) => lastRun + " (last run)",
            _ when lastRun is not null && lastRun != version => version + " (last run: " + lastRun + ")",
            _ => version,
        });
        text.AppendLine("  Writable: " + scan.GameFolderAccess switch
        {
            FolderAccess.Writable => "yes",
            FolderAccess.Denied => "NO",
            _ => "unknown",
        });
        if (Elevation.IsElevated) text.AppendLine("  Mod Manager: running as administrator");

        var steam = GameLauncher.ReadSteamLaunchOptions();
        if (scan.Install.Source == InstallSource.Steam)
            text.AppendLine("  Steam launch options: " + (steam.Readable ? Quote(steam.Value) : "could not be read")
                            + (steam.Readable && !steam.HasForceD3D11 ? "   <- no " + GameLauncher.ForceD3D11 : ""));
        text.AppendLine();
    }

    private static void WriteLoader(StringBuilder text, ScanResult scan)
    {
        var loader = scan.Loader;
        text.AppendLine("Loader");
        text.AppendLine("  Installed: " + (loader.Installed ? "yes" : "no"));
        if (loader.Version is not null) text.AppendLine("  Version:   " + loader.Version);
        text.AppendLine("  winhttp.dll:         " + YesNo(loader.DoorstopProxyPresent));
        text.AppendLine("  doorstop_config.ini: " + YesNo(loader.DoorstopConfigPresent));

        if (loader.Doorstop is not null)
        {
            text.AppendLine("    enabled:         " + loader.Doorstop.Enabled);
            text.AppendLine("    target_assembly: " + (loader.Doorstop.TargetAssembly ?? "(not set)")
                            + (loader.Doorstop.TargetsLoader ? "" : "   <- not this loader"));
        }

        if (loader.MissingRuntimeFiles.Count > 0)
            text.AppendLine("  Missing runtime files: " + string.Join(", ", loader.MissingRuntimeFiles));

        if (scan.Rivals.Count > 0)
            foreach (var rival in scan.Rivals)
                text.AppendLine("  CONFLICT: " + rival.Name + " - " + rival.Evidence);

        text.AppendLine();
    }

    private static void WriteMods(StringBuilder text, ScanResult scan)
    {
        text.AppendLine("Mods (" + scan.Mods.Count + ")");

        if (scan.Mods.Count == 0)
        {
            text.AppendLine("  none found");
            text.AppendLine();
            return;
        }

        foreach (var mod in scan.Mods)
        {
            string state = mod.Kind == ModKind.Unknown ? "BROKEN"
                : !mod.Kind.IsRunnable() ? "UNSUPPORTED"
                : !mod.LocationWorks ? "WRONG PLACE"
                : mod.Enabled ? "on"
                : "off";

            text.AppendLine("  [" + state.PadRight(11) + "] " + mod.Name + " " + mod.Version);
            text.AppendLine("      id:    " + mod.Id);
            text.AppendLine("      kind:  " + mod.Kind.Label());
            text.AppendLine("      where: " + scan.Install.Relative(mod.AssemblyPath));

            if (mod.Probe?.MissingReferences.Count > 0)
                text.AppendLine("      missing: " + string.Join(", ", mod.Probe.MissingReferences));
            if (mod.Probe?.MissingFromGame.Count > 0)
                text.AppendLine("      game lacks: " + string.Join(", ", mod.Probe.MissingFromGame.Take(12))
                                + (mod.Probe.MissingFromGame.Count > 12 ? " and " + (mod.Probe.MissingFromGame.Count - 12) + " more" : ""));
            if (mod.Probe?.ReadError is not null)
                text.AppendLine("      note:  " + mod.Probe.ReadError);
            foreach (var companion in mod.Companions.Where(c => c.Kind == ModKind.LoaderRuntime))
                text.AppendLine("      BUNDLED LOADER FILE: " + companion.FileName);
        }
        text.AppendLine();
    }

    private static void WriteIssues(StringBuilder text, ScanResult scan)
    {
        int errors = scan.Diagnostics.Count(d => d.Severity == Severity.Error);
        int warnings = scan.Diagnostics.Count(d => d.Severity == Severity.Warning);

        text.AppendLine("Issues (" + errors + " error(s), " + warnings + " warning(s))");
        if (scan.Diagnostics.Count == 0)
        {
            text.AppendLine("  none - this install looks correct");
            text.AppendLine();
            return;
        }

        foreach (var diagnostic in scan.Diagnostics)
        {
            text.AppendLine("  " + diagnostic.Severity.ToString().ToUpperInvariant() + ": " + diagnostic.Title);
            if (diagnostic.Detail is not null) text.AppendLine("      " + Wrap(diagnostic.Detail, 94, "      "));
            if (diagnostic.Path is not null) text.AppendLine("      at " + scan.Install.Relative(diagnostic.Path));
            if (diagnostic.CanRepair) text.AppendLine("      fix: " + diagnostic.Repair.Label);
        }
        text.AppendLine();
    }

    private static void WriteLog(StringBuilder text, ScanResult scan, LogReader.LogFile log)
    {
        text.AppendLine("Last run (" + (log.Exists ? LogName(scan.Install, log.Path) + ", " + log.LastWrite : "no log yet") + ")");

        if (!log.Exists)
        {
            text.AppendLine(scan.GameFolderAccess == FolderAccess.Denied
                ? "  The game folder is write-protected, so the loader may not have been able to write a log."
                : "  The game has not been started with the loader yet.");
            return;
        }

        var problems = log.Problems.ToList();
        if (problems.Count == 0)
        {
            text.AppendLine("  no warnings or errors (" + log.Entries.Count + " lines)");
            return;
        }

        foreach (var entry in problems.Take(60))
            text.AppendLine("  " + entry);
        if (problems.Count > 60) text.AppendLine("  ... and " + (problems.Count - 60) + " more");
    }

    private static string YesNo(bool value) => value ? "yes" : "NO";

    private static string LogName(GameInstall install, string path)
    {
        if (string.Equals(Path.GetDirectoryName(path), install.ModsDirectory, StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(path);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile) && path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }

    private static string Quote(string value)
        => string.IsNullOrEmpty(value) ? "(none set)" : "\"" + value + "\"";

    private static string Wrap(string text, int width, string indent)
    {
        var result = new StringBuilder();
        int lineLength = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (lineLength > 0 && lineLength + word.Length + 1 > width)
            {
                result.AppendLine();
                result.Append(indent);
                lineLength = 0;
            }
            else if (lineLength > 0)
            {
                result.Append(' ');
                lineLength++;
            }
            result.Append(word);
            lineLength += word.Length;
        }
        return result.ToString();
    }
}
