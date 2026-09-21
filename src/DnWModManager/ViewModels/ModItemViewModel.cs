using System.Windows.Media;
using DnWModManager.Core;

namespace DnWModManager.ViewModels;

public sealed class ModItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public InstalledMod Mod { get; }

    public ModItemViewModel(MainViewModel main, InstalledMod mod)
    {
        _main = main;
        Mod = mod;
    }

    public string Name => Mod.Name;
    public string Id => Mod.Id;
    public string Version => Mod.Version;
    public string Author => string.IsNullOrWhiteSpace(Mod.Author) ? null : Mod.Author;
    public string KindLabel => Mod.Kind.Label();
    public string Location => _main.Install is null ? Mod.Directory : _main.Install.Relative(Mod.AssemblyPath);

    public string Description
    {
        get
        {
            string description = Mod.Description ?? Mod.Catalog?.Description;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
    }

    public string Url
    {
        get
        {
            string url = Mod.Url ?? Mod.Catalog?.Homepage;
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
    }

    public bool HasUrl => Url is not null;

    public string Byline
    {
        get
        {
            var parts = new List<string> { KindLabel };
            if (Author is not null) parts.Add("by " + Author);
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsEnabled
    {
        get => Mod.Enabled;
        set
        {
            if (Mod.Enabled == value) return;
            if (!CanToggle) return;

            try
            {
                _main.SetModEnabled(Mod, value);
                Mod.Enabled = value;
                Raise();
                Raise(nameof(StatusText));
                Raise(nameof(StatusBrush));
            }
            catch (Exception e)
            {
                _main.ReportError("Could not change " + Name, e);
                Raise();
            }
        }
    }

    public bool CanToggle => Mod.Kind.IsRunnable()
                             && !Mod.DisabledByManifest
                             && !string.IsNullOrWhiteSpace(Mod.Id)
                             && _main.CanEditLoaderConfig;

    public string ToggleTooltip
    {
        get
        {
            if (!Mod.Kind.IsRunnable()) return "The loader cannot run a " + KindLabel + ".";
            if (Mod.DisabledByManifest) return "This mod is disabled in its own mod.json.";
            if (string.IsNullOrWhiteSpace(Mod.Id)) return "This mod has no id, so it cannot be disabled individually.";
            if (!_main.CanEditLoaderConfig) return "Mods\\ModLoader.json could not be read, so it will not be written to.";
            return IsEnabled ? "Loaded when the game starts" : "Skipped when the game starts";
        }
    }

    public bool HasError => Mod.Issues.Any(i => i.Severity == Severity.Error);
    public bool HasWarning => Mod.Issues.Any(i => i.Severity == Severity.Warning);

    public string StatusText
    {
        get
        {
            if (Mod.Kind == ModKind.Unknown) return "Broken";
            if (!Mod.Kind.IsRunnable()) return "Not supported";
            if (!Mod.LocationWorks) return "Wrong place";
            if (HasError) return "Issue";
            if (Mod.DisabledByManifest) return "Off (mod.json)";
            return Mod.Enabled ? "On" : "Off";
        }
    }

    public Brush StatusBrush => Theme.Brush(
        !Mod.Kind.IsRunnable() ? Theme.Muted
        : !Mod.LocationWorks || HasError ? Theme.Danger
        : Mod.Enabled ? Theme.Success
        : Theme.Muted);

    public IReadOnlyList<Diagnostic> Issues => Mod.Issues;
    public bool HasIssues => Mod.Issues.Count > 0;

    public bool HasUpdate => Mod.Update is not null;

    public string UpdateText => Mod.Update is null ? null : "Update to " + Mod.Update.Version;

    public string UpdateTooltip => Mod.Update is null
        ? null
        : "Installed " + Version + ", available " + Mod.Update.Version + ". Downloads from " + Mod.Update.PageUrl;

    public bool CanCheckForUpdates => Mod.Catalog?.Source?.CanUpdate == true;

    public void RefreshAll()
    {
        foreach (var property in new[]
                 {
                     nameof(IsEnabled), nameof(StatusText), nameof(StatusBrush), nameof(HasUpdate),
                     nameof(UpdateText), nameof(UpdateTooltip), nameof(HasError), nameof(HasIssues),
                     nameof(Description), nameof(Url), nameof(HasUrl),
                 })
            Raise(property);
    }
}
