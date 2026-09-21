namespace DnWModManager.Core;

public enum ModLocation
{
    // Loose DLL
    ModsRoot,
    // Mod with folder
    ModsSubfolder,
    BepInExPlugins,
    BepInExPatchers,
    MelonPlugins,
    GameRoot,
    TooDeep,
}

public static class ModLocationInfo
{
    public static string Label(this ModLocation location) => location switch
    {
        ModLocation.ModsRoot => "Mods",
        ModLocation.ModsSubfolder => "Mods folder",
        ModLocation.BepInExPlugins => "BepInEx/plugins",
        ModLocation.BepInExPatchers => "BepInEx/patchers",
        ModLocation.MelonPlugins => "Plugins",
        ModLocation.GameRoot => "game folder",
        ModLocation.TooDeep => "nested sub-folder",
        _ => "?",
    };
}

public sealed class InstalledMod
{
    public string AssemblyPath { get; init; }
    public string Directory { get; init; }

    public ModLocation Location { get; init; }
    public ProbedAssembly Probe { get; init; }
    public ModManifestFile Manifest { get; init; }
    public List<ProbedAssembly> Companions { get; } = new();
    public ModKind Kind => Probe?.Kind ?? ModKind.Unknown;

    public string Id => Kind switch
    {
        ModKind.BepInExPlugin or ModKind.MelonMod or ModKind.MelonPlugin
            => Probe?.Id ?? Manifest?.Id ?? Path.GetFileNameWithoutExtension(AssemblyPath),
        _ => Manifest?.Id ?? Probe?.Id ?? Path.GetFileNameWithoutExtension(AssemblyPath),
    };

    public string Name
    {
        get
        {
            string name = Manifest?.Name ?? Probe?.Name;
            return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(AssemblyPath) : name;
        }
    }

    public string Version => Manifest?.Version ?? Probe?.Version ?? "?";
    public string Author => Manifest?.Author ?? Probe?.Author;
    public string Description => Manifest?.Description ?? Probe?.Description;
    public string Url => Manifest?.Url ?? Probe?.Url;

    public bool Enabled { get; set; } = true;
    public bool DisabledByManifest => Manifest is { Enabled: false };

    public CatalogMod Catalog { get; set; }
    public AvailableUpdate Update { get; set; }
    public bool HasUpdate => Update is not null;

    public List<Diagnostic> Issues { get; } = new();

    public bool IsRunnable => Kind.IsRunnable() && LocationWorks;

    // Is the current location valid (enough)?
    public bool LocationWorks => Kind switch
    {
        ModKind.DnwMod => Location is ModLocation.ModsRoot or ModLocation.ModsSubfolder,
        ModKind.BepInExPlugin => Location is ModLocation.BepInExPlugins
                                 || (Location is ModLocation.ModsRoot or ModLocation.ModsSubfolder && Manifest is null),
        ModKind.MelonMod => Location is ModLocation.ModsRoot or ModLocation.ModsSubfolder && Manifest is null,
        _ => false,
    };

    public string CanonicalDirectory(GameInstall install) => Kind switch
    {
        ModKind.DnwMod => Path.Combine(install.ModsDirectory, FolderName),
        ModKind.BepInExPlugin => Path.Combine(install.BepInExPluginsDirectory, FolderName),
        ModKind.MelonMod => install.ModsDirectory,
        _ => null,
    };

    // Create a safe folder name if needed
    public string FolderName
    {
        get
        {
            string source = Path.GetFileNameWithoutExtension(AssemblyPath);
            if (string.IsNullOrWhiteSpace(source)) source = Name;
            var cleaned = new string(source.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            return string.IsNullOrEmpty(cleaned) ? "Mod" : cleaned;
        }
    }

    public override string ToString() => Name + " " + Version + " (" + Id + ")";
}
