namespace DnWModManager.Core;

public sealed record RepairOutcome(Diagnostic Diagnostic, bool Succeeded, string Message);

public sealed class RepairRunner
{
    private readonly GameInstall _install;
    private readonly PackageService _packages;

    public RepairRunner(GameInstall install, PackageService packages = null)
    {
        _install = install;
        _packages = packages;
    }

    public string LastQuarantineDirectory { get; private set; }

    public async Task<IReadOnlyList<RepairOutcome>> RunAsync(
        IEnumerable<Diagnostic> diagnostics,
        bool includeDestructive,
        IProgress<string> progress = null,
        CancellationToken cancel = default)
    {
        var quarantine = new Quarantine(_install);
        var context = new RepairContext
        {
            Install = _install,
            Quarantine = quarantine,
            Packages = _packages,
            Log = message => progress?.Report(message),
        };

        var outcomes = new List<RepairOutcome>();

        void Record(RepairOutcome outcome)
        {
            outcomes.Add(outcome);
            if (outcome.Succeeded) ManagerLog.Info(_install, "Fixed: " + outcome.Diagnostic.Title + ". " + outcome.Message);
            else ManagerLog.Warning(_install, "Could not fix: " + outcome.Diagnostic.Title + ". " + outcome.Message);
        }

        foreach (var diagnostic in Selected(diagnostics, includeDestructive))
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(diagnostic.Repair.Label + "...");

            try
            {
                string message = await diagnostic.Repair.Apply(context, cancel).ConfigureAwait(false);
                Record(new RepairOutcome(diagnostic, true, message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException e)
            {
                Record(new RepairOutcome(diagnostic, false,
                    "Access denied: " + e.Message + " Close the game and try again."));
            }
            catch (IOException e) when (IsFileInUse(e))
            {
                Record(new RepairOutcome(diagnostic, false,
                    "A file is in use: " + e.Message + " Close Drag'n Wash and try again."));
            }
            catch (Exception e)
            {
                Record(new RepairOutcome(diagnostic, false, e.Message));
            }
        }

        LastQuarantineDirectory = quarantine.UsedThisSession ? quarantine.SessionDirectory : null;
        return outcomes;
    }

    private static IEnumerable<Diagnostic> Selected(IEnumerable<Diagnostic> diagnostics, bool includeDestructive)
        => diagnostics
            .Where(d => d.CanRepair)
            .Where(d => includeDestructive || !d.Repair.IsDestructive)
            .OrderBy(Phase)
            .ToList();

    private static int Phase(Diagnostic diagnostic) => diagnostic.Code switch
    {
        "folder.permissions" => -1,
        "loader.missing" or "loader.proxy.missing" or "loader.runtime.missing" or "loader.runtime.incomplete" => 0,
        "doorstop.config.missing" or "doorstop.disabled" or "doorstop.foreign" => 1,
        var code when code.StartsWith("rival.", StringComparison.Ordinal) => 2,
        "stray.runtime" or "mod.bundled.runtime" => 3,
        _ => 4,
    };

    private static bool IsFileInUse(IOException e)
    {
        // ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).
        int code = e.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}
