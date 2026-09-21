namespace DnWModManager.Core;

public enum Severity
{
    Info,
    Warning,
    Error,
}

public sealed class Repair
{
    public string Label { get; init; }
    public string Description { get; init; }

    public Func<RepairContext, CancellationToken, Task<string>> Apply { get; init; }

    public bool IsDestructive { get; init; }
    public bool NeedsNetwork { get; init; }
}

public sealed class RepairContext
{
    public required GameInstall Install { get; init; }
    public required Quarantine Quarantine { get; init; }

    // Downloads and unpacks releases
    public PackageService Packages { get; init; }

    public Action<string> Log { get; init; } = _ => { };
}

public sealed class Diagnostic
{
    // Rule identifier
    public required string Code { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; }
    public string Path { get; init; }
    public InstalledMod Mod { get; init; }
    public Repair Repair { get; init; }
    public bool CanRepair => Repair is not null;

    public override string ToString() => Severity + ": " + Title;
}
