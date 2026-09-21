using System.Text;
using Newtonsoft.Json;

namespace DnWModManager.Core;

public sealed class InstallReceipt
{
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonProperty("package")] public string Package { get; set; }
    [JsonProperty("installedUtc")] public DateTime InstalledUtc { get; set; }
    [JsonProperty("mods")] public List<ReceiptMod> Mods { get; set; } = new();
    [JsonProperty("files")] public List<string> Files { get; set; } = new();
    [JsonProperty("settings")] public List<string> Settings { get; set; } = new();

    [JsonIgnore] public string FilePath { get; private set; }

    public bool Covers(string relativeAssemblyPath)
        => Mods.Any(m => string.Equals(m.Assembly, relativeAssemblyPath, StringComparison.OrdinalIgnoreCase));

    public bool CoversId(string id)
        => !string.IsNullOrEmpty(id) && Mods.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string StoreDirectory(GameInstall install)
        => Path.Combine(install.ModsDirectory, "_manager", "installed");

    public static List<InstallReceipt> LoadAll(GameInstall install)
    {
        var receipts = new List<InstallReceipt>();
        string directory = StoreDirectory(install);
        if (!Directory.Exists(directory)) return receipts;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var receipt = JsonConvert.DeserializeObject<InstallReceipt>(File.ReadAllText(file));
                if (receipt is null) continue;
                receipt.FilePath = file;
                receipt.Mods ??= new List<ReceiptMod>();
                receipt.Files ??= new List<string>();
                receipt.Settings ??= new List<string>();
                receipts.Add(receipt);
            }
            catch { }
        }
        return receipts;
    }

    public static InstallReceipt For(GameInstall install, string assemblyPath)
    {
        string relative = install.Relative(assemblyPath);
        return LoadAll(install).FirstOrDefault(r => r.Covers(relative));
    }

    public void Save(GameInstall install)
    {
        string directory = StoreDirectory(install);
        Directory.CreateDirectory(directory);

        string name = Mods.FirstOrDefault()?.Id ?? Path.GetFileNameWithoutExtension(Package) ?? "package";
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        FilePath = Path.Combine(directory, name + ".json");
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
    }

    public void Delete()
    {
        try
        {
            if (FilePath is not null && File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { }
    }
}

public sealed class ReceiptMod
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("version")] public string Version { get; set; }
    [JsonProperty("kind")] public string Kind { get; set; }
    [JsonProperty("assembly")] public string Assembly { get; set; }
}
