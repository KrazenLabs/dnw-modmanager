using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace DnWModManager.Core;

public sealed record ReleaseInfo(string Version, string Tag, string PageUrl, string DownloadUrl, string AssetName, string Notes);

public sealed record AvailableUpdate(string CurrentVersion, ReleaseInfo Release)
{
    public string Version => Release.Version;
    public string DownloadUrl => Release.DownloadUrl;
    public string PageUrl => Release.PageUrl;
}

public sealed class StagedItem
{
    public required string SourcePath { get; init; }
    public required string RelativePath { get; init; }

    public ProbedAssembly Probe { get; init; }
    public bool IsPrimary { get; set; }
}

public enum PackageLayout
{
    ModFolder,
    GameOverlay,
    LoaderRelease,
}

public sealed class StagedPackage : IDisposable
{
    public required string Root { get; init; }
    public required bool IsTemporary { get; init; }
    public PackageLayout Layout { get; set; } = PackageLayout.ModFolder;

    public List<StagedItem> Mods { get; } = new();
    public List<StagedItem> Libraries { get; } = new();

    public List<string> InjectorFiles { get; } = new();

    public StagedItem Primary => Mods.FirstOrDefault(m => m.IsPrimary) ?? Mods.FirstOrDefault();

    public string Name => Primary?.Probe?.Name ?? Path.GetFileName(Root);
    public string Version => Primary?.Probe?.Version;
    public ModKind Kind => Primary?.Probe?.Kind ?? ModKind.Unknown;

    public bool IsEmpty => Mods.Count == 0;

    public void Dispose()
    {
        if (!IsTemporary) return;
        try { Directory.Delete(Root, recursive: true); }
        catch { }
    }
}

public sealed class InstallReport
{
    public List<string> Installed { get; } = new();
    public List<SkippedFile> Skipped { get; } = new();
    public IEnumerable<SkippedFile> Unsupported => Skipped.Where(s => s.Kind == SkipKind.Unsupported);
    public List<string> Replaced { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> KeptSettings { get; } = new();
    public List<(string From, string To)> Relocated { get; } = new();

    public int Written { get; set; }
    public int Unchanged { get; set; }
    public string BackupDirectory { get; set; }
}

public sealed class PackageService : IDisposable
{
    private readonly HttpClient _http;

    public PackageService(string userAgent)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public async Task<ModCatalog> FetchCatalogsAsync(IEnumerable<string> extraUrls, CatalogCache cache, CancellationToken cancel)
    {
        var urls = new List<(string Url, bool Official)> { (ModCatalog.DefaultUrl, true) };
        foreach (var url in extraUrls ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(url) || ModCatalog.IsOfficialUrl(url)) continue;
            if (urls.Any(u => string.Equals(u.Url, url.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            urls.Add((url.Trim(), false));
        }

        var sources = await Task.WhenAll(urls.Select(u => FetchCatalogAsync(u.Url, u.Official, cache, cancel))).ConfigureAwait(false);
        return ModCatalog.Merge(sources);
    }

    private async Task<CatalogSource> FetchCatalogAsync(string url, bool official, CatalogCache cache, CancellationToken cancel)
    {
        try
        {
            // Allow local paths for testing
            string json = File.Exists(url)
                ? await File.ReadAllTextAsync(url, cancel).ConfigureAwait(false)
                : await _http.GetStringAsync(url, cancel).ConfigureAwait(false);

            var catalog = ModCatalog.Parse(json, url, official);
            cache?.Save(url, json);
            return new CatalogSource { Url = url, IsOfficial = official, Catalog = catalog };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var cached = CachedCatalog(cache, url, official, out var savedAt);
            return new CatalogSource
            {
                Url = url,
                IsOfficial = official,
                Catalog = cached,
                CachedAt = cached is null ? null : savedAt,
                Error = Explain(e, official),
            };
        }
    }

    private static ModCatalog CachedCatalog(CatalogCache cache, string url, bool official, out DateTime savedAt)
    {
        savedAt = default;
        if (cache is null || !cache.TryLoad(url, out string json, out savedAt)) return null;

        try { return ModCatalog.Parse(json, url, official); }
        catch { return null; }
    }

    private static string Explain(Exception e, bool official) => e switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            official ? "the online mod repository has not been published yet" : "nothing was found at that address",
        HttpRequestException { StatusCode: not null } http => "the server answered " + (int)http.StatusCode!,
        HttpRequestException => "there was no internet connection",
        TaskCanceledException => "the download timed out",
        Newtonsoft.Json.JsonException => "that address does not contain a mod repository",
        _ => e.Message,
    };

    public async Task<ReleaseInfo> LatestReleaseAsync(ModSource source, CancellationToken cancel)
    {
        if (source is null) return null;

        return source.Type switch
        {
            "github" => await LatestGitHubReleaseAsync(source, cancel).ConfigureAwait(false),
            "url" => string.IsNullOrWhiteSpace(source.Url)
                ? null
                : new ReleaseInfo(source.Version, source.Version, source.Url, source.Url, Path.GetFileName(source.Url), null),
            _ => null,
        };
    }

    private async Task<ReleaseInfo> LatestGitHubReleaseAsync(ModSource source, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(source.Repo)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" + source.Repo.Trim('/') + "/releases/latest");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var release = JObject.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        if ((bool?)release["draft"] == true || (bool?)release["prerelease"] == true) return null;

        string tag = (string)release["tag_name"];
        if (!ModVersion.TryParse(tag, out var version)) return null;

        var asset = PickAsset(release["assets"] as JArray, source.Asset);

        return new ReleaseInfo(
            Version: ModVersion.Display(version),
            Tag: tag,
            PageUrl: (string)release["html_url"] ?? "https://github.com/" + source.Repo + "/releases",
            DownloadUrl: (string)asset?["browser_download_url"],
            AssetName: (string)asset?["name"],
            Notes: (string)release["body"]);
    }

    private static JToken PickAsset(JArray assets, string pattern)
    {
        if (assets is null || assets.Count == 0) return null;

        var zips = assets.Where(a => ((string)a["name"] ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var matched = zips.Concat(assets).FirstOrDefault(a => GlobMatch((string)a["name"] ?? "", pattern));
            if (matched is not null) return matched;
        }

        return zips.FirstOrDefault() ?? assets.FirstOrDefault();
    }

    private static bool GlobMatch(string name, string pattern)
    {
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public async Task<string> DownloadAsync(string url, string suggestedName, IProgress<double> progress, CancellationToken cancel)
    {
        string directory = Path.Combine(Path.GetTempPath(), "DnWModManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string fileName = string.IsNullOrWhiteSpace(suggestedName) ? "download.zip" : Path.GetFileName(suggestedName);
        string destination = Path.Combine(directory, fileName);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            written += read;
            if (total is > 0) progress?.Report((double)written / total.Value);
        }

        return destination;
    }

    public StagedPackage Stage(string zipOrFolder, GameInstall install)
    {
        string root;
        bool temporary;

        if (Directory.Exists(zipOrFolder))
        {
            root = zipOrFolder;
            temporary = false;
        }
        else
        {
            root = Path.Combine(Path.GetTempPath(), "DnWModManager", "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            ExtractSafely(zipOrFolder, root);
            temporary = true;
        }

        var package = new StagedPackage { Root = root, IsTemporary = temporary };
        Analyse(package, install);
        return package;
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;

            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The archive contains an entry that would be written outside the target folder ("
                                      + entry.FullName + "). Skipping.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void Analyse(StagedPackage package, GameInstall install)
    {
        using var probe = new AssemblyProbe(new[] { install.LoaderDirectory, install.ManagedDirectory });

        string[] files;
        try { files = Directory.GetFiles(package.Root, "*", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var file in files)
        {
            string relative = Path.GetRelativePath(package.Root, file);
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(file).ToLowerInvariant();

            // Never install injectors
            if (IsInjectorFile(relative, name))
            {
                package.InjectorFiles.Add(relative);
                continue;
            }

            if (extension == ".dll")
            {
                var probed = probe.Probe(file);
                var item = new StagedItem { SourcePath = file, RelativePath = relative, Probe = probed };

                if (probed.Kind is ModKind.LoaderRuntime) package.InjectorFiles.Add(relative);
                else if (probed.Kind.IsRunnable() || probed.Kind.IsUnsupportedMod()) package.Mods.Add(item);
                else package.Libraries.Add(item);
                continue;
            }

        }

        package.Layout = DetectLayout(package);

        var runnable = package.Mods.Where(m => m.Probe.Kind.IsRunnable()).ToList();
        var primary = runnable.FirstOrDefault(m =>
                          string.Equals(Path.GetFileNameWithoutExtension(m.SourcePath), Path.GetFileName(package.Root), StringComparison.OrdinalIgnoreCase))
                      ?? runnable.FirstOrDefault();
        if (primary is not null) primary.IsPrimary = true;
    }

    private static PackageLayout DetectLayout(StagedPackage package)
    {
        string overlay = InstallPlanner.FindOverlayRoot(package.Root, package, out _);
        if (overlay is not null && Directory.Exists(Path.Combine(overlay, "DnWModLoader"))) return PackageLayout.LoaderRelease;
        return overlay is null ? PackageLayout.ModFolder : PackageLayout.GameOverlay;
    }

    private static bool IsInjectorFile(string relativePath, string name)
    {
        if (name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("version.dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("dobby.dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("doorstop_libs", StringComparison.OrdinalIgnoreCase)) return true;

        string normalised = relativePath.Replace('\\', '/');
        if (normalised.StartsWith("MelonLoader/", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalised.Contains("/MelonLoader/", StringComparison.OrdinalIgnoreCase)) return true;

        bool inBepInExCore = normalised.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)
                             || normalised.Contains("/BepInEx/core/", StringComparison.OrdinalIgnoreCase);
        if (inBepInExCore && InstallPlanner.IsBepInExCoreFile(name)) return true;

        return false;
    }

    public void Dispose() => _http.Dispose();
}
