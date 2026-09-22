using System.Windows.Media;
using DnWModManager.Core;

namespace DnWModManager.ViewModels;

// Row on the Issues page
public sealed class DiagnosticViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public Diagnostic Diagnostic { get; }

    public DiagnosticViewModel(MainViewModel main, Diagnostic diagnostic)
    {
        _main = main;
        Diagnostic = diagnostic;
    }

    public string Title => Diagnostic.Title;
    public string Detail => Diagnostic.Detail;
    public Brush SeverityBrush => Theme.ForSeverity(Diagnostic.Severity);

    public string Where => Diagnostic.Path is null || _main.Install is null
        ? null
        : _main.Install.Relative(Diagnostic.Path);

    public bool HasWhere => Where is not null;

    public bool CanRepair => Diagnostic.CanRepair;
    public string RepairLabel => Diagnostic.Repair?.Label;
    public string RepairDescription => Diagnostic.Repair?.Description;
}

// Row on the Get mods page
public sealed class CatalogItemViewModel : ObservableObject
{
    public CatalogMod Mod { get; }

    public InstalledMod Installed { get; }

    public CatalogItemViewModel(CatalogMod mod, InstalledMod installed, bool isNew)
    {
        Mod = mod;
        Installed = installed;
        IsNew = isNew;
    }

    public bool IsNew { get; }

    public string Name => Mod.Name ?? Mod.Id;
    public string Author => Mod.Author;
    public string Description => Mod.Description;
    public string Homepage => Mod.Homepage;
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Mod.Homepage);

    public string KindLabel => Mod.ExpectedKind.Label();

    public string Byline
    {
        get
        {
            var parts = new List<string> { KindLabel };
            if (!string.IsNullOrWhiteSpace(Author)) parts.Add("by " + Author);
            if (!Mod.FromOfficialList) parts.Add("from " + Mod.ListName);
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsInstalled => Installed is not null;

    public string StateText => IsInstalled ? "Installed " + Installed.Version : null;

    public bool CanInstall => !IsInstalled && Mod.IsReleased;
}

// Mod catalog entry
public sealed class CatalogListViewModel
{
    public CatalogSource Source { get; }

    public CatalogListViewModel(CatalogSource source) => Source = source;

    public string Url => Source.Url;
    public string Label => Source.Label;
    public bool IsOfficial => Source.IsOfficial;
    public bool CanRemove => !Source.IsOfficial;

    public string StatusText
    {
        get
        {
            int count = Source.Catalog?.Mods.Count(m => m.IsReleased) ?? 0;
            string mods = count + (count == 1 ? " mod" : " mods");

            if (Source.CachedAt is { } saved) return "Using the copy from " + saved.ToString("yyyy-MM-dd") + " (" + mods + ") because " + Source.Error;
            if (Source.Error is not null) return "Could not be loaded: " + Source.Error;
            return mods;
        }
    }

    public Brush StatusBrush => Theme.Brush(Source.Error is null ? Theme.Muted : Theme.Warning);
}
