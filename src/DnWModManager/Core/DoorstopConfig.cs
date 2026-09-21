using System.Text;

namespace DnWModManager.Core;

public sealed class DoorstopConfig
{
    public const string ExpectedTarget = @"DnWModLoader\DnWModLoader.dll";
    public const string ExpectedSearchPath = "DnWModLoader";

    private readonly List<string> _lines;

    public string Path { get; }

    private DoorstopConfig(string path, List<string> lines)
    {
        Path = path;
        _lines = lines;
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
        bool changed = Set("enabled", "true");
        changed |= Set("target_assembly", ExpectedTarget);
        changed |= Set("dll_search_path_override", ExpectedSearchPath);
        return changed;
    }

    public bool Set(string key, string value)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            if (!IsAssignmentFor(_lines[i], key)) continue;

            string replacement = key + " = " + value;
            if (_lines[i].Trim() == replacement) return false;
            _lines[i] = replacement;
            return true;
        }

        int general = _lines.FindIndex(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));
        if (general >= 0) _lines.Insert(general + 1, key + " = " + value);
        else _lines.Add(key + " = " + value);
        return true;
    }

    public void Save()
        => File.WriteAllLines(Path, _lines, new UTF8Encoding(false));

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
            return line[(equals + 1)..].Trim();
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
