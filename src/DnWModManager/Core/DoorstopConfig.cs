using System.Text;

namespace DnWModManager.Core;

public sealed class DoorstopConfig
{
    public const string ExpectedTarget = @"DnWModLoader\DnWModLoader.dll";
    public const string ExpectedScriptTarget = "DnWModLoader/DnWModLoader.dll";
    public const string ExpectedSearchPath = "DnWModLoader";

    private readonly List<string> _lines;

    public string Path { get; }

    public bool IsShellScript { get; }

    private DoorstopConfig(string path, List<string> lines)
    {
        Path = path;
        _lines = lines;
        IsShellScript = path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
    }

    public static DoorstopConfig Load(string path)
    {
        try { return new DoorstopConfig(path, File.ReadAllLines(path).ToList()); }
        catch { return null; }
    }

    public bool Enabled => ReadBool("enabled", true);

    public string TargetAssembly => Read("target_assembly");

    public string SearchPathOverride => Read("dll_search_path_override");

    public bool TargetsLoader
    {
        get
        {
            string target = TargetAssembly;
            if (string.IsNullOrWhiteSpace(target)) return false;
            return Normalise(target).EndsWith("dnwmodloader/dnwmodloader.dll", StringComparison.Ordinal);
        }
    }

    public string ForeignTargetName
    {
        get
        {
            string target = TargetAssembly;
            if (string.IsNullOrWhiteSpace(target) || TargetsLoader) return null;
            string normalised = Normalise(target);
            if (normalised.Contains("bepinex")) return "BepInEx";
            if (normalised.Contains("melonloader")) return "MelonLoader";
            return target.Trim();
        }
    }

    private static string Normalise(string value)
        => value.Trim().Replace('\\', '/').ToLowerInvariant();

    public bool RepairForLoader()
    {
        bool changed = Set("enabled", IsShellScript ? "1" : "true");
        changed |= Set("target_assembly", IsShellScript ? ExpectedScriptTarget : ExpectedTarget);
        changed |= Set("dll_search_path_override", ExpectedSearchPath);
        return changed;
    }

    public bool Set(string key, string value)
    {
        string replacement = IsShellScript ? key + "=\"" + value + "\"" : key + " = " + value;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (!IsAssignmentFor(_lines[i], key)) continue;

            if (_lines[i].Trim() == replacement) return false;
            _lines[i] = replacement;
            return true;
        }

        if (IsShellScript)
        {
            int body = _lines.Count > 0 && _lines[0].StartsWith("#!", StringComparison.Ordinal) ? 1 : 0;
            _lines.Insert(body, replacement);
            return true;
        }

        int general = _lines.FindIndex(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));
        if (general >= 0) _lines.Insert(general + 1, replacement);
        else _lines.Add(replacement);
        return true;
    }

    public void Save()
    {
        if (IsShellScript) File.WriteAllText(Path, string.Join("\n", _lines) + "\n", new UTF8Encoding(false));
        else File.WriteAllLines(Path, _lines, new UTF8Encoding(false));
    }

    public static void WriteDefault(string path)
    {
        const string text = """
            # UnityDoorstop configuration for the DnW Mod Loader.
            # winhttp.dll (UnityDoorstop) reads this file when the game starts.
            # Delete winhttp.dll (or set enabled = false) to start the game without mods.

            [General]

            enabled = true

            target_assembly = DnWModLoader\DnWModLoader.dll

            # If true, Unity's output log is redirected to <game folder>\output_log.txt
            redirect_output_log = false

            # Overrides the default boot.config file path
            boot_config_override =

            # If enabled, the DOORSTOP_DISABLE environment variable is ignored
            ignore_disable_switch = false

            [UnityMono]

            # Extra folder Mono searches for assemblies
            dll_search_path_override = DnWModLoader

            # Optional Mono debugger server (for attaching an IDE to mods)
            debug_enabled = false
            debug_address = 127.0.0.1:10000
            debug_suspend = false
            """;
        File.WriteAllText(path, text.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
    }

    private string Read(string key)
    {
        foreach (var line in _lines)
        {
            if (!IsAssignmentFor(line, key)) continue;
            int equals = line.IndexOf('=');
            string value = line[(equals + 1)..].Trim();
            if (IsShellScript && value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            return value;
        }
        return null;
    }

    private bool ReadBool(string key, bool fallback)
    {
        string value = Read(key);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static bool IsAssignmentFor(string line, string key)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) return false;

        int equals = trimmed.IndexOf('=');
        if (equals < 0) return false;

        return trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }
}
