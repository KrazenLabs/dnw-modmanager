using Mono.Cecil;

namespace DnWModManager.Core;

// Checks the game API for missing types, members, and patch targets
public sealed class GameApi : IDisposable
{
    private const int MaxDepth = 32;

    private const int Normal = 0, Getter = 1, Setter = 2, Constructor = 3, StaticConstructor = 4, Enumerator = 5, Async = 6;
    private const int Ref = 1, Out = 2, Pointer = 3;

    private readonly string _managedDirectory;
    private readonly Dictionary<string, GameAssembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _checked = new(StringComparer.OrdinalIgnoreCase);

    public GameApi(string managedDirectory) => _managedDirectory = managedDirectory;

    public List<string> FindMissing(ModuleDefinition module)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name)
        {
            if (seen.Add(name)) missing.Add(name);
        }

        foreach (var type in module.GetTypeReferences())
            if (IsChecked(type) && Find(type, out _) == Lookup.Missing
                && (type.DeclaringType is null || Find(type.DeclaringType, out _) != Lookup.Missing))
                Add(TypeName(type));

        foreach (var member in module.GetMemberReferences())
            if (!MemberExists(member))
                Add(MemberName(member.DeclaringType, member.Name));

        foreach (var target in PatchTargets(module))
            CheckPatchTarget(target, Add);

        return missing;
    }

    private enum Lookup
    {
        NotChecked,
        Missing,
        Found,
    }

    private sealed class GameAssembly
    {
        public ModuleDefinition Module { get; init; }
        public Dictionary<string, TypeDefinition> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Forwarders { get; } = new(StringComparer.Ordinal);
    }

    private sealed record PatchTarget(TypeReference DeclaringType, string MethodName, int? MethodType,
        TypeReference[] ArgumentTypes, int[] ArgumentVariations)
    {
        public PatchTarget Merge(PatchTarget other) => new(
            other.DeclaringType ?? DeclaringType,
            other.MethodName ?? MethodName,
            other.MethodType ?? MethodType,
            other.ArgumentTypes ?? ArgumentTypes,
            other.ArgumentVariations ?? ArgumentVariations);
    }

    private bool IsChecked(TypeReference type)
    {
        if (type is null || type is GenericParameter) return false;
        type = type.GetElementType();
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type.Scope is AssemblyNameReference assembly && IsChecked(assembly.Name);
    }

    private bool IsChecked(string assemblyName)
    {
        if (_checked.TryGetValue(assemblyName, out bool result)) return result;
        result = !IsFramework(assemblyName) && File.Exists(Path.Combine(_managedDirectory, assemblyName + ".dll"));
        _checked[assemblyName] = result;
        return result;
    }

    private static bool IsFramework(string name)
        => name is "mscorlib" or "netstandard" or "System"
           || name.StartsWith("System.", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.", StringComparison.Ordinal)
           || name.StartsWith("Mono.", StringComparison.Ordinal);

    private Lookup Find(TypeReference reference, out TypeDefinition definition)
    {
        definition = null;
        if (reference is null || reference is GenericParameter) return Lookup.NotChecked;
        reference = reference.GetElementType();
        if (reference is TypeDefinition own)
        {
            definition = own;
            return Lookup.Found;
        }

        var outer = reference;
        while (outer.DeclaringType is not null) outer = outer.DeclaringType;
        return outer.Scope is AssemblyNameReference assembly
            ? Find(assembly.Name, reference.FullName, 0, out definition)
            : Lookup.NotChecked;
    }

    private Lookup Find(string assemblyName, string fullName, int depth, out TypeDefinition definition)
    {
        definition = null;
        var assembly = Load(assemblyName);
        if (assembly is null) return Lookup.NotChecked;
        if (assembly.Types.TryGetValue(fullName, out definition)) return Lookup.Found;

        if (assembly.Forwarders.TryGetValue(fullName, out string target))
            return depth < 4 ? Find(target, fullName, depth + 1, out definition) : Lookup.NotChecked;

        return IsChecked(assemblyName) ? Lookup.Missing : Lookup.NotChecked;
    }

    private GameAssembly Load(string name)
    {
        if (_assemblies.TryGetValue(name, out var cached)) return cached;

        GameAssembly assembly = null;
        try
        {
            string path = Path.Combine(_managedDirectory, name + ".dll");
            if (File.Exists(path))
            {
                var module = ModuleDefinition.ReadModule(path, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Deferred,
                    InMemory = true,
                });
                assembly = new GameAssembly { Module = module };
                foreach (var type in module.Types) Index(type, assembly.Types);
                foreach (var exported in module.ExportedTypes)
                    if (exported.IsForwarder && exported.Scope is AssemblyNameReference forwardedTo)
                        assembly.Forwarders[exported.FullName] = forwardedTo.Name;
            }
        }
        catch
        {
            assembly = null;
        }

        _assemblies[name] = assembly;
        return assembly;
    }

    private static void Index(TypeDefinition type, Dictionary<string, TypeDefinition> types)
    {
        types[type.FullName] = type;
        foreach (var nested in type.NestedTypes) Index(nested, types);
    }

    private bool MemberExists(MemberReference member)
    {
        var declaring = member.DeclaringType;
        if (declaring is null || declaring is ArrayType || !IsChecked(declaring)) return true;
        if (Find(declaring, out var type) != Lookup.Found) return true;

        return member switch
        {
            FieldReference field => HasField(type, field),
            MethodReference method => HasMember(type, candidate => candidate.Name == method.Name && SameSignature(candidate, method)),
            _ => true,
        };
    }

    private bool HasField(TypeDefinition type, FieldReference field)
    {
        for (int depth = 0; type is not null && depth < MaxDepth; depth++)
        {
            if (type.Fields.Any(f => f.Name == field.Name && SameType(f.FieldType, field.FieldType))) return true;
            if (!TryBase(type, out type)) return true;
        }
        return false;
    }

    private bool HasMember(TypeDefinition type, Func<MethodDefinition, bool> match, bool inherited = true)
    {
        for (int depth = 0; type is not null && depth < MaxDepth; depth++)
        {
            if (type.Methods.Any(match)) return true;
            if (!inherited) return false;

            if (type.IsInterface)
            {
                foreach (var implemented in type.Interfaces)
                {
                    var lookup = Find(implemented.InterfaceType, out var parent);
                    if (lookup == Lookup.NotChecked || (lookup == Lookup.Found && HasMember(parent, match))) return true;
                }
                return false;
            }

            if (!TryBase(type, out type)) return true;
        }
        return false;
    }

    private bool TryBase(TypeDefinition type, out TypeDefinition baseType)
    {
        baseType = null;
        if (type.BaseType is null) return true;
        return Find(type.BaseType, out baseType) != Lookup.NotChecked;
    }

    private static bool SameSignature(MethodDefinition candidate, MethodReference reference)
    {
        if (candidate.HasThis != reference.HasThis
            || candidate.GenericParameters.Count != reference.GenericParameters.Count
            || candidate.Parameters.Count != reference.Parameters.Count
            || !SameType(candidate.ReturnType, reference.ReturnType))
            return false;

        for (int i = 0; i < candidate.Parameters.Count; i++)
            if (!SameType(candidate.Parameters[i].ParameterType, reference.Parameters[i].ParameterType)) return false;
        return true;
    }

    private static bool SameType(TypeReference a, TypeReference b)
    {
        a = Unwrap(a);
        b = Unwrap(b);
        if (a is null || b is null) return a is null && b is null;
        if (a is GenericParameter || b is GenericParameter) return true;

        switch (a)
        {
            case ByReferenceType byRef:
                return b is ByReferenceType otherByRef && SameType(byRef.ElementType, otherByRef.ElementType);
            case PointerType pointer:
                return b is PointerType otherPointer && SameType(pointer.ElementType, otherPointer.ElementType);
            case ArrayType array:
                return b is ArrayType otherArray && array.Rank == otherArray.Rank && SameType(array.ElementType, otherArray.ElementType);
            case GenericInstanceType generic:
                if (b is not GenericInstanceType otherGeneric
                    || generic.GenericArguments.Count != otherGeneric.GenericArguments.Count
                    || generic.ElementType.FullName != otherGeneric.ElementType.FullName)
                    return false;
                for (int i = 0; i < generic.GenericArguments.Count; i++)
                    if (!SameType(generic.GenericArguments[i], otherGeneric.GenericArguments[i])) return false;
                return true;
            case FunctionPointerType:
                return b is FunctionPointerType;
        }

        return b is not (ByReferenceType or PointerType or ArrayType or GenericInstanceType or FunctionPointerType)
               && a.FullName == b.FullName;
    }

    private static TypeReference Unwrap(TypeReference type)
    {
        while (type is IModifierType or PinnedType or SentinelType)
            type = ((TypeSpecification)type).ElementType;
        return type;
    }

    private static IEnumerable<PatchTarget> PatchTargets(ModuleDefinition module)
    {
        foreach (var type in AllTypes(module))
        {
            var container = ReadPatch(type.CustomAttributes);
            if (container is null || type.Methods.Any(IsDynamicTarget)) continue;

            var targets = new List<PatchTarget>();
            foreach (var method in type.Methods)
            {
                var info = ReadPatch(method.CustomAttributes);
                if (info is not null) targets.Add(container.Merge(info));
                else if (IsPatchMethod(method)) targets.Add(container);
            }
            if (targets.Count == 0) targets.Add(container);

            foreach (var target in targets.Distinct()) yield return target;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var pending = new Stack<TypeDefinition>(module.Types);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes) pending.Push(nested);
        }
    }

    private static bool IsDynamicTarget(MethodDefinition method)
        => method.IsStatic
           && (method.Name is "TargetMethod" or "TargetMethods"
               || method.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyTargetMethod" or "HarmonyTargetMethods"));

    private static bool IsPatchMethod(MethodDefinition method)
        => method.IsStatic
           && (method.Name is "Prefix" or "Postfix" or "Transpiler" or "Finalizer" or "ILManipulator"
               || method.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix"
                   or "HarmonyTranspiler" or "HarmonyFinalizer" or "HarmonyILManipulator" or "HarmonyReversePatch"));

    private static PatchTarget ReadPatch(IEnumerable<CustomAttribute> attributes)
    {
        PatchTarget result = null;
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName is not ("HarmonyLib.HarmonyPatch" or "Harmony.HarmonyPatch")) continue;

            TypeReference declaringType = null;
            var names = new List<string>();
            int? methodType = null;
            TypeReference[] argumentTypes = null;
            int[] variations = null;
            try
            {
                foreach (var argument in attribute.ConstructorArguments)
                {
                    string parameter = argument.Type.FullName;
                    if (argument.Value is TypeReference type) declaringType = type;
                    else if (argument.Value is string name) names.Add(name);
                    else if (parameter.EndsWith(".MethodType", StringComparison.Ordinal)) methodType = Convert.ToInt32(argument.Value);
                    else if (parameter == "System.Type[]" && argument.Value is CustomAttributeArgument[] types)
                        argumentTypes = types.Select(t => t.Value as TypeReference).ToArray();
                    else if (parameter.EndsWith(".ArgumentType[]", StringComparison.Ordinal) && argument.Value is CustomAttributeArgument[] kinds)
                        variations = kinds.Select(k => Convert.ToInt32(k.Value)).ToArray();
                }
            }
            catch
            {
                continue;
            }

            if (names.Count > 1 || argumentTypes?.Any(t => t is null) == true) continue;

            var target = new PatchTarget(declaringType, names.FirstOrDefault(), methodType, argumentTypes, variations);
            result = result is null ? target : result.Merge(target);
        }
        return result;
    }

    private void CheckPatchTarget(PatchTarget target, Action<string> report)
    {
        if (!IsChecked(target.DeclaringType)) return;

        var lookup = Find(target.DeclaringType, out var type);
        if (lookup == Lookup.Missing) report(TypeName(target.DeclaringType));
        if (lookup != Lookup.Found) return;

        string name = (target.MethodType ?? Normal) switch
        {
            Normal or Enumerator or Async => target.MethodName,
            Getter when target.MethodName is not null => "get_" + target.MethodName,
            Setter when target.MethodName is not null => "set_" + target.MethodName,
            Constructor => ".ctor",
            StaticConstructor => ".cctor",
            _ => null,
        };
        if (name is null) return;

        bool inherited = name is not (".ctor" or ".cctor");
        if (!HasMember(type, m => m.Name == name && SameArguments(m, target.ArgumentTypes, target.ArgumentVariations), inherited))
            report(MemberName(type, name));
    }

    private static bool SameArguments(MethodDefinition method, TypeReference[] arguments, int[] variations)
    {
        if (arguments is null) return true;
        if (method.Parameters.Count != arguments.Length) return false;

        for (int i = 0; i < arguments.Length; i++)
        {
            var parameter = method.Parameters[i].ParameterType;
            int variation = variations is not null && i < variations.Length ? variations[i] : Normal;
            bool same = variation switch
            {
                Ref or Out => parameter is ByReferenceType byRef && SameType(byRef.ElementType, arguments[i]),
                Pointer => parameter is PointerType pointer && SameType(pointer.ElementType, arguments[i]),
                _ => SameType(parameter, arguments[i]),
            };
            if (!same) return false;
        }
        return true;
    }

    private static string TypeName(TypeReference type)
    {
        type = type.GetElementType();
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return type.DeclaringType is null ? name : TypeName(type.DeclaringType) + "." + name;
    }

    private static string MemberName(TypeReference type, string member)
    {
        if (member is ".ctor" or ".cctor") return TypeName(type) + " constructor";
        foreach (var prefix in new[] { "get_", "set_", "add_", "remove_" })
            if (member.StartsWith(prefix, StringComparison.Ordinal) && member.Length > prefix.Length)
                return TypeName(type) + "." + member[prefix.Length..];
        return TypeName(type) + "." + member;
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies.Values) assembly?.Module.Dispose();
        _assemblies.Clear();
    }
}
