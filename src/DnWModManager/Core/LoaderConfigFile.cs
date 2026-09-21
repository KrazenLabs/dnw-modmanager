using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DnWModManager.Core;

public sealed class LoaderConfigFile
{
    private const string DisabledModsKey = "disabledMods";

    private readonly JObject _root;
    public string Path { get; }
    public string ParseError { get; private set; }
    public bool Existed { get; }

    private LoaderConfigFile(string path, JObject root, bool existed, string parseError)
    {
        Path = path;
        _root = root;
        Existed = existed;
        ParseError = parseError;
    }

    public static LoaderConfigFile Load(string path)
    {
        if (!File.Exists(path)) return new LoaderConfigFile(path, new JObject(), false, null);

        try
        {
            string text = File.ReadAllText(path);
            var root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
            return new LoaderConfigFile(path, root, true, null);
        }
        catch (Exception e)
        {
            return new LoaderConfigFile(path, new JObject(), true, e.Message);
        }
    }

    public bool CanWrite => ParseError is null;

    public IReadOnlyList<string> DisabledMods
    {
        get
        {
            var array = _root[DisabledModsKey] as JArray;
            if (array is null) return Array.Empty<string>();
            return array.Select(token => token.Type == JTokenType.String ? (string)token : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
        }
    }

    public bool IsDisabled(string modId)
        => !string.IsNullOrEmpty(modId)
           && DisabledMods.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));

    public bool SetDisabled(string modId, bool disabled)
    {
        if (string.IsNullOrEmpty(modId)) return false;

        var current = DisabledMods.ToList();
        bool present = current.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
        if (present == disabled) return false;

        current.RemoveAll(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
        if (disabled) current.Add(modId);

        _root[DisabledModsKey] = new JArray(current.Cast<object>().ToArray());
        return true;
    }

    public string GetString(string key, string fallback = null)
        => _root[key]?.Type == JTokenType.String ? (string)_root[key] : fallback;

    public bool GetBool(string key, bool fallback)
        => _root[key]?.Type == JTokenType.Boolean ? (bool)_root[key] : fallback;

    public void Set(string key, JToken value) => _root[key] = value;

    public string OverlayHotkey => GetString("overlayHotkey", "F10");
    public bool CheckForUpdates => GetBool("checkForUpdates", true);
    public string LogLevel => GetString("logLevel", "Debug");

    public void Save()
    {
        if (!CanWrite)
            throw new InvalidOperationException(
                System.IO.Path.GetFileName(Path) + " could not be parsed (" + ParseError + ").");

        string directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // UTF-8 without BOM
        File.WriteAllText(Path, JsonConvert.SerializeObject(_root, Formatting.Indented), new UTF8Encoding(false));
    }
}
