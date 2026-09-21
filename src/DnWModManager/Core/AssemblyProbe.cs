using Mono.Cecil;

namespace DnWModManager.Core;

public sealed class ProbedAssembly
{
    public string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public ModKind Kind { get; set; } = ModKind.Unknown;

    public string Id { get; set; }

    public string Name { get; set; }
    public string Version { get; set; }
    public string Author { get; set; }
    public string Description { get; set; }
    public string Url { get; set; }

    public string EntryType { get; set; }
    public List<string> Dependencies { get; } = new();
    public List<string> MissingReferences { get; } = new();
    // Assembly runtime dll or mod
    public List<string> References { get; } = new();
    public string ReadError { get; set; }
}

public sealed class AssemblyProbe : IDisposable
{
    private const string BepInExPluginBase = "BepInEx.BaseUnityPlugin";
    private const string MelonPluginBase = "MelonLoader.MelonPlugin";
    private const string DnwModBase = "DnWModLoader.Mod";

    public static readonly HashSet<string> LoaderRuntimeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "DnWModLoader.dll",
        "BepInEx.dll", "BepInEx.Preloader.dll", "BepInEx.Harmony.dll", "BepInEx.Core.dll",
        "BepInEx.Unity.dll", "BepInEx.Unity.Mono.dll", "BepInEx.Unity.Mono.Preloader.dll",
        "MelonLoader.dll", "MelonLoader.ModHandler.dll",
        "0Harmony.dll", "0Harmony20.dll", "HarmonyXInterop.dll",
        "MonoMod.RuntimeDetour.dll", "MonoMod.Core.dll", "MonoMod.Utils.dll",
        "MonoMod.Backports.dll", "MonoMod.ILHelpers.dll", "MonoMod.Iced.dll",
        "MonoMod.Common.dll",
        "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll", "Mono.Cecil.Rocks.dll",
        "Tomlet.dll", "System.ValueTuple.dll",
    };

    private readonly DefaultAssemblyResolver _resolver = new();
    private readonly List<string> _searchDirectories = new();

    public AssemblyProbe(IEnumerable<string> searchDirectories)
    {
        foreach (var existing in _resolver.GetSearchDirectories()) _resolver.RemoveSearchDirectory(existing);
        foreach (var directory in searchDirectories ?? Enumerable.Empty<string>()) AddSearchDirectory(directory);
    }

    public void AddSearchDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        if (_searchDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase)) return;
        _searchDirectories.Add(directory);
        _resolver.AddSearchDirectory(directory);
    }

    public ProbedAssembly Probe(string path)
    {
        var probed = new ProbedAssembly { Path = path };

        if (LoaderRuntimeFiles.Contains(Path.GetFileName(path)))
        {
            probed.Kind = ModKind.LoaderRuntime;
            probed.Name = Path.GetFileNameWithoutExtension(path);
            probed.Version = TryReadFileVersion(path);
            return probed;
        }

        AssemblyDefinition definition = null;
        try
        {
            // The mod's own folder resolves its private dependencies
            AddSearchDirectory(Path.GetDirectoryName(path));

            definition = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                AssemblyResolver = _resolver,
                InMemory = true,                     // never hold a lock on a file
                ReadingMode = ReadingMode.Deferred,
            });

            var module = definition.MainModule;
            foreach (var reference in module.AssemblyReferences) probed.References.Add(reference.Name);

            probed.Name = definition.Name.Name;
            probed.Version = definition.Name.Version?.ToString();
            probed.Description = AssemblyAttribute(definition, "System.Reflection.AssemblyDescriptionAttribute");

            string company = AssemblyAttribute(definition, "System.Reflection.AssemblyCompanyAttribute");
            probed.Author = string.Equals(company, definition.Name.Name, StringComparison.OrdinalIgnoreCase)
                ? null
                : company;

            if (!ReadMelon(definition, probed) && !ReadBepInEx(module, probed) && !ReadDnwMod(module, probed))
                probed.Kind = ModKind.Library;

            if (probed.Kind.IsRunnable() || probed.Kind is ModKind.BepInExPatcher or ModKind.MelonPlugin)
                CollectMissingReferences(module, probed);
        }
        catch (BadImageFormatException)
        {
            probed.ReadError = "not a .NET assembly";
            probed.Kind = ModKind.Unknown;
        }
        catch (Exception e)
        {
            probed.ReadError = e.Message;
            probed.Kind = ModKind.Unknown;
        }
        finally
        {
            definition?.Dispose();
        }

        return probed;
    }

    private bool ReadMelon(AssemblyDefinition definition, ProbedAssembly probed)
    {
        var info = definition.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName is "MelonLoader.MelonInfoAttribute" or "MelonLoader.MelonModInfoAttribute");

        if (info is null)
        {
            if (!definition.MainModule.AssemblyReferences.Any(r => r.Name == "MelonLoader")) return false;
            probed.Kind = ModKind.MelonMod;
            probed.ReadError = "references MelonLoader but is missing [assembly: MelonInfo]";
            probed.Id = MakeMelonId(null, probed.Name);
            return true;
        }

        var args = info.ConstructorArguments;
        var melonType = args.Count > 0 ? args[0].Value as TypeReference : null;
        string name = args.Count > 1 ? args[1].Value as string : null;
        string version = args.Count > 2 ? args[2].Value as string : null;
        string author = args.Count > 3 ? args[3].Value as string : null;
        string downloadLink = args.Count > 4 ? args[4].Value as string : null;

        probed.Kind = IsSubclassOf(melonType, MelonPluginBase) ? ModKind.MelonPlugin : ModKind.MelonMod;
        if (!string.IsNullOrEmpty(name)) probed.Name = name;
        if (!string.IsNullOrEmpty(version)) probed.Version = version;
        if (!string.IsNullOrEmpty(author)) probed.Author = author;
        probed.Url = downloadLink;
        probed.EntryType = melonType?.FullName;

        probed.Id = MakeMelonId(author, probed.Name);

        if (melonType is null) probed.ReadError = "names no melon type in [assembly: MelonInfo]";

        foreach (var value in definition.CustomAttributes
                     .Where(a => a.AttributeType.FullName is "MelonLoader.MelonOptionalDependenciesAttribute"
                         or "MelonLoader.MelonAdditionalDependenciesAttribute")
                     .SelectMany(a => a.ConstructorArguments)
                     .Select(a => a.Value)
                     .OfType<CustomAttributeArgument[]>()
                     .SelectMany(values => values)
                     .Select(v => v.Value as string)
                     .Where(v => !string.IsNullOrEmpty(v)))
            probed.Dependencies.Add(value);

        return true;
    }

    public static string MakeMelonId(string author, string name)
    {
        string a = Slug(author);
        string n = Slug(name);
        if (string.IsNullOrEmpty(n)) n = "melon";
        return string.IsNullOrEmpty(a) ? "melon." + n : a + "." + n;
    }

    private static string Slug(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = text.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private bool ReadBepInEx(ModuleDefinition module, ProbedAssembly probed)
    {
        bool referencesBepInEx = module.AssemblyReferences.Any(r => r.Name is "BepInEx" or "BepInEx.Core" or "BepInEx.Unity.Mono");
        bool referencesPreloader = module.AssemblyReferences.Any(r => r.Name is "BepInEx.Preloader" or "BepInEx.Unity.Mono.Preloader");
        if (!referencesBepInEx && !referencesPreloader) return false;

        TypeDefinition pluginType = null;
        CustomAttribute metadata = null;
        foreach (var type in module.Types)
        {
            if (type.IsInterface || type.IsAbstract) continue;
            var attribute = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
            if (attribute is null && !IsSubclassOf(type, BepInExPluginBase)) continue;
            pluginType = type;
            metadata = attribute;
            if (attribute is not null) break;
        }

        if (pluginType is null)
        {
            probed.Kind = LooksLikePatcher(module) ? ModKind.BepInExPatcher : ModKind.Library;
            return probed.Kind != ModKind.Library;
        }

        probed.Kind = ModKind.BepInExPlugin;
        probed.EntryType = pluginType.FullName;

        if (metadata is not null && metadata.ConstructorArguments.Count >= 3)
        {
            probed.Id = metadata.ConstructorArguments[0].Value as string;
            probed.Name = metadata.ConstructorArguments[1].Value as string ?? probed.Name;
            probed.Version = metadata.ConstructorArguments[2].Value as string ?? probed.Version;
        }
        else
        {
            probed.ReadError = "has a BaseUnityPlugin but no [BepInPlugin] attribute";
            probed.Id ??= Path.GetFileNameWithoutExtension(probed.Path);
        }

        foreach (var attribute in AttributesWithInheritance(pluginType)
                     .Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency"))
        {
            if (attribute.ConstructorArguments.Count > 0 && attribute.ConstructorArguments[0].Value is string guid)
                probed.Dependencies.Add(guid);
        }

        return true;
    }

    private static bool LooksLikePatcher(ModuleDefinition module)
        => module.Types.Any(type =>
            type.Methods.Any(m => m.IsStatic && m.Name == "Patch")
            && (type.Properties.Any(p => p.Name == "TargetDLLs") || type.Fields.Any(f => f.Name == "TargetDLLs")));

    private bool ReadDnwMod(ModuleDefinition module, ProbedAssembly probed)
    {
        if (!module.AssemblyReferences.Any(r => r.Name == "DnWModLoader")) return false;

        var entry = module.Types.FirstOrDefault(t => !t.IsInterface && !t.IsAbstract && IsSubclassOf(t, DnwModBase));
        probed.Kind = ModKind.DnwMod;
        probed.EntryType = entry?.FullName;

        // Read ModInfo if json config is missing
        var info = entry?.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "DnWModLoader.ModInfoAttribute");
        if (info is not null)
        {
            var args = info.ConstructorArguments;
            if (args.Count > 0) probed.Id = args[0].Value as string;
            if (args.Count > 1 && args[1].Value is string name && !string.IsNullOrEmpty(name)) probed.Name = name;
            if (args.Count > 2 && args[2].Value is string version && !string.IsNullOrEmpty(version)) probed.Version = version;
            foreach (var property in info.Properties)
            {
                if (property.Name == "Author") probed.Author = property.Argument.Value as string ?? probed.Author;
                if (property.Name == "Description") probed.Description = property.Argument.Value as string ?? probed.Description;
            }
        }

        if (entry is null) probed.ReadError = "references DnWModLoader but contains no Mod subclass";
        return true;
    }

    private bool IsSubclassOf(TypeReference type, string baseTypeFullName)
    {
        var resolved = TryResolve(type);
        return resolved is not null && IsSubclassOf(resolved, baseTypeFullName);
    }

    private bool IsSubclassOf(TypeDefinition type, string baseTypeFullName)
    {
        var current = type?.BaseType;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.FullName == baseTypeFullName) return true;
            var resolved = TryResolve(current);
            if (resolved is null) return false;
            current = resolved.BaseType;
        }
        return false;
    }

    private IEnumerable<CustomAttribute> AttributesWithInheritance(TypeDefinition type)
    {
        for (int depth = 0; type is not null && depth < 32; depth++)
        {
            foreach (var attribute in type.CustomAttributes) yield return attribute;
            if (type.BaseType is null || type.BaseType.FullName == BepInExPluginBase) yield break;
            type = TryResolve(type.BaseType);
        }
    }

    private TypeDefinition TryResolve(TypeReference type)
    {
        try { return type?.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
        catch (Exception) { return null; }
    }

    private void CollectMissingReferences(ModuleDefinition module, ProbedAssembly probed)
    {
        foreach (var reference in module.AssemblyReferences)
        {
            if (IsFrameworkAssembly(reference.Name)) continue;
            if (_searchDirectories.Any(directory => File.Exists(Path.Combine(directory, reference.Name + ".dll")))) continue;
            try
            {
                _resolver.Resolve(reference);
            }
            catch (AssemblyResolutionException)
            {
                probed.MissingReferences.Add(reference.Name);
            }
            catch (Exception)
            {
                // A resolver failure is not evidence the reference is missing
            }
        }
    }

    private static bool IsFrameworkAssembly(string name)
        => name is "mscorlib" or "System" or "netstandard"
           || name.StartsWith("System.", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name.StartsWith("UnityEngine", StringComparison.Ordinal)
           || name.StartsWith("Unity.", StringComparison.Ordinal);

    private static string AssemblyAttribute(AssemblyDefinition definition, string attributeFullName)
    {
        var attribute = definition.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == attributeFullName);
        if (attribute is null || attribute.ConstructorArguments.Count != 1) return null;
        string value = attribute.ConstructorArguments[0].Value as string;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string TryReadFileVersion(string path)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch { return null; }
    }

    public void Dispose() => _resolver.Dispose();
}
