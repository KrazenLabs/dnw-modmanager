using Newtonsoft.Json.Linq;

namespace DnWModManager.Core;

public sealed record ResourceFolderSpec(string Folder, string Name, string Description, IReadOnlyList<string> Extensions, bool Recursive)
{
    public static IReadOnlyList<ResourceFolderSpec> Read(JObject root)
    {
        if (root["resources"] is not JArray array) return Array.Empty<ResourceFolderSpec>();

        var result = new List<ResourceFolderSpec>();
        foreach (var token in array)
        {
            if (token is not JObject obj) continue;
            var extensions = obj["extensions"] is JArray list
                ? list.Where(t => t.Type == JTokenType.String).Select(t => (string)t).ToList()
                : new List<string>();
            bool recursive = obj["recursive"]?.Type != JTokenType.Boolean || (bool)obj["recursive"];
            result.Add(new ResourceFolderSpec(Text(obj, "folder"), Text(obj, "name"), Text(obj, "description"), extensions, recursive));
        }
        return result;
    }

    private static string Text(JObject obj, string key)
        => obj[key]?.Type == JTokenType.String ? (string)obj[key] : null;
}

public sealed class ResourceImportResult
{
    public List<string> Added { get; } = new();
    public int AlreadyThere { get; set; }
    public int Unsupported { get; set; }
    public int Failed { get; set; }
    public string FirstError { get; set; }
    public bool Truncated { get; set; }

    public int Total => Added.Count + AlreadyThere + Unsupported + Failed;
}

public sealed class ResourceFolder
{
    public const int MaxCountedFiles = 20000;
    public const int MaxImportFiles = 2000;
    public const int MaxScannedEntries = 100000;
    private const string ImportExtension = ".dnwimport";
    private const int MaxNameSuffix = 9999;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".reg", ".cpl", ".hta", ".jar", ".sys", ".ocx", ImportExtension,
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] ForbiddenPathChars =
        Path.GetInvalidPathChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    private readonly HashSet<string> _extensions;

    private ResourceFolder(string modDirectory, string folder, string fullPath, ResourceFolderSpec spec, string[] extensions)
    {
        ModDirectory = modDirectory;
        Folder = folder;
        FullPath = fullPath;
        Name = string.IsNullOrWhiteSpace(spec.Name) ? DefaultName(folder) : spec.Name.Trim();
        Description = string.IsNullOrWhiteSpace(spec.Description) ? null : spec.Description.Trim();
        Recursive = spec.Recursive;
        Extensions = extensions;
        _extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
    }

    public string ModDirectory { get; }
    public string Folder { get; }
    public string FullPath { get; }
    public string Name { get; }
    public string Description { get; }
    public bool Recursive { get; }
    public IReadOnlyList<string> Extensions { get; }

    public string FileTypesLabel => Extensions.Count == 0 ? "any file" : string.Join(", ", Extensions);

    public string DialogFilter => Extensions.Count == 0
        ? "All files (*.*)|*.*"
        : Name + " (" + Patterns + ")|" + Patterns;

    private string Patterns => string.Join(";", Extensions.Select(e => "*" + e));

    public static IReadOnlyList<ResourceFolder> ForMod(InstalledMod mod)
    {
        var specs = mod.Manifest?.Resources;
        if (mod.Kind != ModKind.DnwMod || specs is null || specs.Count == 0 || mod.Location != ModLocation.ModsSubfolder)
            return Array.Empty<ResourceFolder>();

        string modDirectory;
        try
        {
            modDirectory = TrimSeparators(Path.GetFullPath(mod.Directory));
        }
        catch (Exception)
        {
            return Array.Empty<ResourceFolder>();
        }

        var result = new List<ResourceFolder>();
        foreach (var spec in specs)
        {
            if (!TryResolve(modDirectory, spec.Folder, out string folder, out string fullPath, out _)) continue;
            if (!TryNormalizeExtensions(spec.Extensions, out string[] extensions)) continue;
            if (result.Any(f => string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new ResourceFolder(modDirectory, folder, fullPath, spec, extensions));
        }
        return result;
    }

    public bool Accepts(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (BlockedExtensions.Contains(extension)) return false;
        return _extensions.Count == 0 || _extensions.Contains(extension);
    }

    public int CountFiles()
    {
        if (!Directory.Exists(FullPath)) return 0;
        int count = 0;
        var pending = new Stack<string>();
        pending.Push(FullPath);
        while (pending.Count > 0)
        {
            var directory = new DirectoryInfo(pending.Pop());
            try
            {
                foreach (var file in directory.EnumerateFiles())
                {
                    if (IsVisible(file.Attributes) && Accepts(file.Name) && ++count >= MaxCountedFiles) return count;
                }
                if (!Recursive) continue;
                foreach (var sub in directory.EnumerateDirectories())
                {
                    if (IsVisible(sub.Attributes) && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(sub.FullName);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }

    public bool EnsureExists(out string error)
    {
        error = null;
        if (HasLinkBetween(ModDirectory, FullPath))
        {
            error = "Links are not allowed";
            return false;
        }
        if (Directory.Exists(FullPath)) return true;
        if (File.Exists(FullPath))
        {
            error = "this file already exists";
            return false;
        }
        try
        {
            Directory.CreateDirectory(FullPath);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public ResourceImportResult Import(IReadOnlyCollection<string> sources)
    {
        var result = new ResourceImportResult();
        if (!EnsureExists(out string error))
        {
            result.Failed = Math.Max(1, sources.Count);
            result.FirstError = error;
            return result;
        }

        var plan = new List<(string Source, string Relative)>();
        int scanned = 0;
        foreach (string raw in sources)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            string source;
            try
            {
                source = TrimSeparators(Path.GetFullPath(raw));
            }
            catch (Exception e)
            {
                Fail(result, raw, e.Message);
                continue;
            }
            if (Directory.Exists(source)) CollectFolder(source, plan, result, ref scanned);
            else if (File.Exists(source)) Plan(source, Path.GetFileName(source), plan, result);
            else Fail(result, source, "not found");
            if (result.Truncated) break;
        }

        foreach (var (source, relative) in plan) Copy(source, relative, result);
        return result;
    }

    public string Describe(ResourceImportResult result)
    {
        if (result.Total == 0 && !result.Truncated) return "Nothing to import.";
        bool onlyAlreadyThere = result.Added.Count == 0 && result.AlreadyThere > 0 && result.Unsupported == 0 && result.Failed == 0;
        var parts = new List<string>();
        if (result.Added.Count > 0) parts.Add("Imported " + Files(result.Added.Count) + ".");
        else if (onlyAlreadyThere) parts.Add(result.AlreadyThere == 1 ? "This file already exists." : "Those files already exist.");
        else parts.Add("Nothing imported.");
        if (result.AlreadyThere > 0 && !onlyAlreadyThere) parts.Add(result.AlreadyThere + (result.AlreadyThere == 1 ? " already exists." : " already exist."));
        if (result.Unsupported > 0)
            parts.Add(Files(result.Unsupported) + " skipped" + (Extensions.Count == 0 ? " (programs and scripts are not allowed)." : ", only " + FileTypesLabel + " files are allowed."));
        if (result.Failed > 0) parts.Add(Files(result.Failed) + " could not be imported: " + result.FirstError);
        if (result.Truncated) parts.Add("Stopped early, too many files.");
        return string.Join(" ", parts);
    }

    private static string Files(int count) => count == 1 ? "1 file" : count + " files";

    private void CollectFolder(string source, List<(string, string)> plan, ResourceImportResult result, ref int scanned)
    {
        string baseName = Path.GetFileName(source);
        if (string.IsNullOrEmpty(baseName)) baseName = source.TrimEnd('\\', '/', ':');
        string prefix = source.EndsWith('\\') ? source : source + "\\";

        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            FileInfo[] files;
            DirectoryInfo[] subdirectories;
            try
            {
                var info = new DirectoryInfo(directory);
                files = info.GetFiles();
                subdirectories = info.GetDirectories();
            }
            catch (Exception e)
            {
                result.FirstError ??= directory + ": " + e.Message;
                continue;
            }

            Array.Sort(files, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            foreach (var file in files)
            {
                if (++scanned > MaxScannedEntries)
                {
                    result.Truncated = true;
                    return;
                }
                if (!Visible(file)) continue;
                string relative = Recursive ? Path.Combine(baseName, file.FullName[prefix.Length..]) : file.Name;
                Plan(file.FullName, relative, plan, result);
                if (result.Truncated) return;
            }

            Array.Sort(subdirectories, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(b.Name, a.Name));
            foreach (var subdirectory in subdirectories)
            {
                if (++scanned > MaxScannedEntries)
                {
                    result.Truncated = true;
                    return;
                }
                try
                {
                    var attributes = subdirectory.Attributes;
                    if (IsVisible(attributes) && !attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(subdirectory.FullName);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Visible(FileInfo file)
    {
        try
        {
            return IsVisible(file.Attributes);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Plan(string source, string relative, List<(string, string)> plan, ResourceImportResult result)
    {
        if (!Accepts(source))
        {
            result.Unsupported++;
            return;
        }
        if (IsInside(FullPath, source))
        {
            result.AlreadyThere++;
            return;
        }
        if (plan.Count >= MaxImportFiles)
        {
            result.Truncated = true;
            return;
        }
        plan.Add((source, relative));
    }

    private void Copy(string source, string relative, ResourceImportResult result)
    {
        string temp = null;
        try
        {
            string target = TrimSeparators(Path.GetFullPath(Path.Combine(FullPath, relative)));
            if (!IsInside(FullPath, target) || string.Equals(FullPath, target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Leads outside mod root");
            string directory = Path.GetDirectoryName(target)!;
            if (HasLinkBetween(FullPath, directory)) throw new IOException("Links are not allowed");
            Directory.CreateDirectory(directory);

            if (File.Exists(target))
            {
                if (SameContent(source, target))
                {
                    result.AlreadyThere++;
                    return;
                }
                target = UniqueName(target);
            }

            temp = target + ImportExtension;
            File.Copy(source, temp, true);
            var attributes = File.GetAttributes(temp);
            if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(temp, attributes & ~FileAttributes.ReadOnly);
            if (File.Exists(target) || Directory.Exists(target)) target = UniqueName(target);
            File.Move(temp, target);
            temp = null;
            result.Added.Add(target);
        }
        catch (Exception e)
        {
            Fail(result, source, e.Message);
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception) { }
            }
        }
    }

    private static void Fail(ResourceImportResult result, string source, string message)
    {
        result.Failed++;
        result.FirstError ??= Path.GetFileName(source) + ": " + message;
    }

    private static string UniqueName(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int n = 2; n <= MaxNameSuffix; n++)
        {
            string candidate = Path.Combine(directory, stem + " (" + n + ")" + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("too many files named " + stem + extension);
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bufferA = new byte[64 * 1024];
        var bufferB = new byte[64 * 1024];
        while (true)
        {
            int readA = first.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            int readB = second.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
        }
    }

    private static bool IsVisible(FileAttributes attributes)
        => (attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    public static bool TryResolve(string modDirectory, string folder, out string normalized, out string fullPath, out string error)
    {
        normalized = null;
        fullPath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "\"folder\" is missing";
            return false;
        }
        string text = folder.Trim().Replace('/', '\\');
        if (text.IndexOfAny(ForbiddenPathChars) >= 0 || text.StartsWith('\\') || Path.IsPathRooted(text))
        {
            error = "\"folder\" must be a relative path inside mod root";
            return false;
        }

        var parts = new List<string>();
        foreach (string part in text.Split('\\'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                error = "\"folder\" must not leave mod root";
                return false;
            }
            if (part.EndsWith('.') || part.EndsWith(' ') || part.StartsWith(' '))
            {
                error = "folder names must not start or end with a space or dot";
                return false;
            }
            if (ReservedNames.Contains(part.Split('.')[0]))
            {
                error = part + " is a reserved name on Windows";
                return false;
            }
            parts.Add(part);
        }

        string root;
        string full;
        try
        {
            root = TrimSeparators(Path.GetFullPath(modDirectory));
            normalized = parts.Count == 0 ? "." : string.Join("\\", parts);
            full = parts.Count == 0 ? root : TrimSeparators(Path.GetFullPath(Path.Combine(root, normalized)));
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        if (!IsInside(root, full))
        {
            error = "\"folder\" must stay inside mod root";
            return false;
        }
        if (HasLinkBetween(root, full))
        {
            error = "\"folder\" is a link";
            return false;
        }
        fullPath = full;
        return true;
    }

    public static bool TryNormalizeExtensions(IReadOnlyList<string> declared, out string[] extensions)
    {
        extensions = Array.Empty<string>();
        if (declared is null || declared.Count == 0) return true;

        var list = new List<string>();
        foreach (string raw in declared)
        {
            string text = (raw ?? "").Trim().ToLowerInvariant();
            if (text.StartsWith('*')) text = text[1..];
            if (!text.StartsWith('.')) text = "." + text;
            if (!IsValidExtension(text) || BlockedExtensions.Contains(text)) continue;
            if (!list.Contains(text)) list.Add(text);
        }
        if (list.Count == 0) return false;
        extensions = list.ToArray();
        return true;
    }

    private static bool IsValidExtension(string extension)
        => extension.Length is >= 2 and <= 17
           && extension[0] == '.'
           && extension.Skip(1).All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    public static bool IsInside(string root, string path)
    {
        root = TrimSeparators(root);
        path = TrimSeparators(path);
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasLinkBetween(string root, string path)
    {
        root = TrimSeparators(root);
        string current = TrimSeparators(path);
        while (current is not null && current.Length > root.Length && IsInside(root, current))
        {
            try
            {
                if ((Directory.Exists(current) || File.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (Exception)
            {
                return true;
            }
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static string TrimSeparators(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    private static string DefaultName(string folder)
    {
        if (folder == ".") return "Files";
        int slash = folder.LastIndexOf('\\');
        return slash >= 0 ? folder[(slash + 1)..] : folder;
    }
}
