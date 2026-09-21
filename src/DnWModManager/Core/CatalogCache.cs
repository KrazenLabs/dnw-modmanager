using System.Security.Cryptography;
using System.Text;

namespace DnWModManager.Core;

public sealed class CatalogCache
{
    public CatalogCache(string folder) => Folder = folder;

    public string Folder { get; }

    public static CatalogCache Default
        => new(Path.Combine(Path.GetDirectoryName(ManagerSettings.DefaultPath) ?? "", "repositories"));

    public void Save(string url, string json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = PathFor(url);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch { }
    }

    public bool TryLoad(string url, out string json, out DateTime saved)
    {
        json = null;
        saved = default;
        try
        {
            string path = PathFor(url);
            if (!File.Exists(path)) return false;

            json = File.ReadAllText(path);
            saved = File.GetLastWriteTime(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Remove(string url)
    {
        try { File.Delete(PathFor(url)); }
        catch { }
    }

    public string PathFor(string url)
    {
        if (ModCatalog.IsOfficialUrl(url)) return Path.Combine(Folder, "official.json");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Path.Combine(Folder, Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".json");
    }
}
