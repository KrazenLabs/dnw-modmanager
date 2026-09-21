using Newtonsoft.Json.Linq;

namespace DnWModManager.Core;

public sealed class ModSource
{
    // github, url, or none
    public string Type { get; init; } = "none";
    // For github: owner/repo
    public string Repo { get; init; }
    // Github release
    public string Asset { get; init; }
    public string Url { get; init; }
    public string Version { get; init; }
    public bool CanUpdate => Type is "github" or "url";

    public static ModSource From(JObject token)
    {
        if (token is null) return new ModSource();
        return new ModSource
        {
            Type = (string)token["type"] ?? "none",
            Repo = (string)token["repo"],
            Asset = (string)token["asset"],
            Url = (string)token["url"],
            Version = (string)token["version"],
        };
    }
}

public sealed class CatalogMod
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Author { get; init; }
    public string Description { get; init; }
    public string Homepage { get; init; }

    public string Kind { get; init; } = "dnw";
    public ModSource Source { get; init; } = new();
    public string Folder { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public string ListName { get; init; }
    public bool FromOfficialList { get; init; } = true;

    public bool IsReleased => Source?.Type switch
    {
        "github" => !string.IsNullOrWhiteSpace(Source.Repo),
        "url" => !string.IsNullOrWhiteSpace(Source.Url),
        _ => false,
    };

    public ModKind ExpectedKind => Kind?.ToLowerInvariant() switch
    {
        "bepinex" => ModKind.BepInExPlugin,
        "melon" or "melonloader" => ModKind.MelonMod,
        _ => ModKind.DnwMod,
    };

    public override string ToString() => Name ?? Id;
}

public sealed class CatalogSource
{
    public required string Url { get; init; }
    public required bool IsOfficial { get; init; }
    public ModCatalog Catalog { get; init; }
    public string Error { get; init; }
    public DateTime? CachedAt { get; init; }

    public string Label => Catalog?.Name is { Length: > 0 } name ? name : ModCatalog.LabelFor(Url, IsOfficial);
}

public sealed class ModCatalog
{
    public const string DefaultUrl =
        "https://raw.githubusercontent.com/KrazenLabs/dnw-modmanager/main/index/mods.json";

    public static readonly IReadOnlyList<string> RetiredOfficialUrls = new[]
    {
        "https://raw.githubusercontent.com/KrazenLabs/dnw-modloader/main/index/mods.json",
    };

    public static bool IsOfficialUrl(string url)
        => !string.IsNullOrWhiteSpace(url)
           && (string.Equals(url.Trim(), DefaultUrl, StringComparison.OrdinalIgnoreCase)
               || RetiredOfficialUrls.Any(r => string.Equals(url.Trim(), r, StringComparison.OrdinalIgnoreCase)));

    public int SchemaVersion { get; private init; }
    public DateTimeOffset? UpdatedUtc { get; private init; }
    public string Name { get; private init; }
    public ModSource LoaderSource { get; private init; } = DefaultLoaderSource();
    public ModSource ManagerSource { get; private init; } = DefaultManagerSource();
    public IReadOnlyList<CatalogMod> Mods { get; private init; } = Array.Empty<CatalogMod>();
    public IReadOnlyList<CatalogSource> Sources { get; private init; } = Array.Empty<CatalogSource>();

    public static ModCatalog Empty() => new();

    public static ModCatalog Parse(string json, string url, bool isOfficial)
    {
        var root = JObject.Parse(json);
        string name = (string)root["name"];
        string listName = string.IsNullOrWhiteSpace(name) ? LabelFor(url, isOfficial) : name.Trim();

        var mods = new List<CatalogMod>();
        if (root["mods"] is JArray array)
        {
            foreach (var token in array.OfType<JObject>())
            {
                string id = (string)token["id"];
                if (string.IsNullOrWhiteSpace(id)) continue;

                mods.Add(new CatalogMod
                {
                    Id = id.Trim(),
                    Name = (string)token["name"] ?? id,
                    Author = (string)token["author"],
                    Description = (string)token["description"],
                    Homepage = (string)token["homepage"],
                    Kind = (string)token["kind"] ?? "dnw",
                    Folder = (string)token["folder"],
                    Source = ModSource.From(token["source"] as JObject),
                    Aliases = StringArray(token, "aliases"),
                    Dependencies = StringArray(token, "dependencies"),
                    ListName = listName,
                    FromOfficialList = isOfficial,
                });
            }
        }

        return new ModCatalog
        {
            SchemaVersion = (int?)root["schemaVersion"] ?? 1,
            UpdatedUtc = (DateTimeOffset?)root["updatedUtc"],
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            LoaderSource = SourceIn(root, "loader") ?? DefaultLoaderSource(),
            ManagerSource = SourceIn(root, "manager") ?? DefaultManagerSource(),
            Mods = mods,
        };
    }

    private static ModSource SourceIn(JObject root, string key)
        => root[key] is JObject section ? ModSource.From(section["source"] as JObject ?? section) : null;

    private static ModSource DefaultLoaderSource()
        => new() { Type = "github", Repo = "KrazenLabs/dnw-modloader" };

    private static ModSource DefaultManagerSource()
        => new() { Type = "github", Repo = "KrazenLabs/dnw-modmanager", Asset = ManagerUpdater.ExecutableName };

    public static ModCatalog Merge(IReadOnlyList<CatalogSource> sources)
    {
        var mods = new List<CatalogMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources.OrderByDescending(s => s.IsOfficial))
        {
            if (source.Catalog is null) continue;
            foreach (var mod in source.Catalog.Mods)
                if (seen.Add(mod.Id)) mods.Add(mod);
        }

        var official = sources.FirstOrDefault(s => s.IsOfficial)?.Catalog;
        return new ModCatalog
        {
            SchemaVersion = official?.SchemaVersion ?? 1,
            UpdatedUtc = official?.UpdatedUtc,
            Name = official?.Name,
            LoaderSource = official?.LoaderSource ?? DefaultLoaderSource(),
            ManagerSource = official?.ManagerSource ?? DefaultManagerSource(),
            Mods = mods,
            Sources = sources,
        };
    }

    public static string LabelFor(string url, bool isOfficial)
    {
        if (isOfficial) return "KrazenLabs mod repository";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.Host;
        try { return Path.GetFileName(url); }
        catch { return url; }
    }

    public CatalogMod Match(InstalledMod mod)
    {
        if (mod is null) return null;

        var byId = Mods.FirstOrDefault(c => string.Equals(c.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var byAlias = Mods.FirstOrDefault(c =>
            c.Aliases.Any(alias => string.Equals(alias, mod.Id, StringComparison.OrdinalIgnoreCase)));
        if (byAlias is not null) return byAlias;

        string fileName = Path.GetFileName(mod.AssemblyPath);
        return Mods.FirstOrDefault(c =>
            c.Aliases.Any(alias => string.Equals(alias, fileName, StringComparison.OrdinalIgnoreCase)));
    }

    public CatalogMod ById(string id)
        => Mods.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> StringArray(JObject token, string key)
    {
        if (token[key] is not JArray array) return Array.Empty<string>();
        return array.Where(t => t.Type == JTokenType.String)
            .Select(t => (string)t)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
