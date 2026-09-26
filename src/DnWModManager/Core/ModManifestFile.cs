using Newtonsoft.Json.Linq;

namespace DnWModManager.Core;

public sealed record ManifestDependency(string Id, string Version, bool Optional)
{
    public override string ToString()
        => Id + (string.IsNullOrEmpty(Version) ? "" : " " + Version) + (Optional ? " (optional)" : "");
}

public sealed class ModManifestFile
{
    public string Path { get; private init; }
    public JObject Raw { get; private init; }

    public string Id { get; private init; }
    public string Name { get; private init; }
    public string Version { get; private init; }
    public string Author { get; private init; }
    public string Description { get; private init; }
    public string Assembly { get; private init; }
    public string EntryType { get; private init; }
    public string Url { get; private init; }
    public bool Enabled { get; private init; } = true;
    public string LoaderVersion { get; private init; }

    public IReadOnlyList<ManifestDependency> Dependencies { get; private init; } = Array.Empty<ManifestDependency>();
    public IReadOnlyList<string> LoadAfter { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> LoadBefore { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<ResourceFolderSpec> Resources { get; private init; } = Array.Empty<ResourceFolderSpec>();

    public string ParseError { get; private init; }

    public static ModManifestFile Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            return new ModManifestFile { Path = path, Raw = new JObject(), ParseError = e.Message };
        }

        JObject root;
        try
        {
            root = JObject.Parse(text);
        }
        catch (Exception e)
        {
            return new ModManifestFile { Path = path, Raw = new JObject(), ParseError = e.Message };
        }

        return new ModManifestFile
        {
            Path = path,
            Raw = root,
            Id = Str(root, "id"),
            Name = Str(root, "name"),
            Version = Str(root, "version") ?? "1.0.0",
            Author = Str(root, "author"),
            Description = Str(root, "description"),
            Assembly = Str(root, "assembly"),
            EntryType = Str(root, "entryType"),
            Url = Str(root, "url"),
            LoaderVersion = Str(root, "loaderVersion"),
            Enabled = root["enabled"]?.Type != JTokenType.Boolean || (bool)root["enabled"],
            Dependencies = ReadDependencies(root),
            LoadAfter = ReadStringArray(root, "loadAfter"),
            LoadBefore = ReadStringArray(root, "loadBefore"),
            Resources = ResourceFolderSpec.Read(root),
        };
    }

    public static bool IsValidId(string id)
        => !string.IsNullOrEmpty(id)
           && id.Length <= 128
           && char.IsLetterOrDigit(id[0])
           && id.ToLowerInvariant() == id
           && id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string Str(JObject root, string key)
    {
        var token = root[key];
        if (token is null || token.Type != JTokenType.String) return null;
        string value = (string)token;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static IReadOnlyList<string> ReadStringArray(JObject root, string key)
    {
        if (root[key] is not JArray array) return Array.Empty<string>();
        return array.Where(t => t.Type == JTokenType.String)
            .Select(t => (string)t)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static IReadOnlyList<ManifestDependency> ReadDependencies(JObject root)
    {
        if (root["dependencies"] is not JArray array) return Array.Empty<ManifestDependency>();

        var result = new List<ManifestDependency>();
        foreach (var token in array)
        {
            if (token.Type == JTokenType.String)
            {
                string id = (string)token;
                if (!string.IsNullOrWhiteSpace(id)) result.Add(new ManifestDependency(id, null, false));
            }
            else if (token is JObject obj)
            {
                string id = Str(obj, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                bool optional = obj["optional"]?.Type == JTokenType.Boolean && (bool)obj["optional"];
                result.Add(new ManifestDependency(id, Str(obj, "version"), optional));
            }
        }
        return result;
    }
}
