using System.Text;
using Newtonsoft.Json;

namespace DnWModManager.Core;

public sealed class ManagerSettings
{
    [JsonProperty("gameDirectory")]
    public string GameDirectory { get; set; }

    [JsonProperty("extraCatalogs")]
    public List<string> ExtraCatalogs { get; set; } = new();

    [JsonProperty("catalogUrl", NullValueHandling = NullValueHandling.Ignore)]
    public string LegacyCatalogUrl { get; set; }

    [JsonProperty("checkForUpdatesOnStart")]
    public bool CheckForUpdatesOnStart { get; set; } = true;

    [JsonProperty("showSafetyWarnings")]
    public bool ShowSafetyWarnings { get; set; } = true;

    [JsonProperty("launchMode")]
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Direct;

    [JsonProperty("extraLaunchArguments")]
    public string ExtraLaunchArguments { get; set; } = "";

    [JsonProperty("closeOnLaunch")]
    public bool CloseOnLaunch { get; set; }

    [JsonProperty("permissionFixDeclinedFor", NullValueHandling = NullValueHandling.Ignore)]
    public string PermissionFixDeclinedFor { get; set; }

    [JsonProperty("seenMods", NullValueHandling = NullValueHandling.Ignore)]
    public List<string> SeenMods { get; set; }

    [JsonIgnore]
    public string Path { get; private set; }

    public static string OverridePath { get; set; }

    public static string DefaultPath => OverridePath ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DnWModManager", "settings.json");

    public static ManagerSettings Load(string path = null)
    {
        path ??= DefaultPath;

        ManagerSettings settings = null;
        try
        {
            if (File.Exists(path))
                settings = JsonConvert.DeserializeObject<ManagerSettings>(File.ReadAllText(path));
        }
        catch { }

        settings ??= new ManagerSettings();
        settings.Path = path;
        settings.ExtraCatalogs ??= new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.LegacyCatalogUrl))
        {
            string legacy = settings.LegacyCatalogUrl.Trim();
            if (!ModCatalog.IsOfficialUrl(legacy)
                && !settings.ExtraCatalogs.Contains(legacy, StringComparer.OrdinalIgnoreCase))
                settings.ExtraCatalogs.Add(legacy);
            settings.LegacyCatalogUrl = null;
        }

        settings.ExtraCatalogs.RemoveAll(url => string.IsNullOrWhiteSpace(url) || ModCatalog.IsOfficialUrl(url));
        return settings;
    }

    public void Save()
    {
        try
        {
            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
        catch { }
    }
}
