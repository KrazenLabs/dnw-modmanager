namespace DnWModManager.Core;

public enum ModKind
{
    Unknown,
    DnwMod,
    BepInExPlugin,
    BepInExPatcher,
    MelonMod,
    MelonPlugin,
    LoaderRuntime,
    Library,
    Il2CppBuild,
}

public static class ModKindInfo
{
    public static string Label(this ModKind kind) => kind switch
    {
        ModKind.DnwMod => "DnW mod",
        ModKind.BepInExPlugin => "BepInEx plugin",
        ModKind.BepInExPatcher => "BepInEx patcher",
        ModKind.MelonMod => "MelonLoader mod",
        ModKind.MelonPlugin => "MelonLoader plugin",
        ModKind.LoaderRuntime => "loader runtime",
        ModKind.Library => "library",
        ModKind.Il2CppBuild => "build for IL2CPP games",
        _ => "unknown",
    };

    public static bool IsRunnable(this ModKind kind)
        => kind is ModKind.DnwMod or ModKind.BepInExPlugin or ModKind.MelonMod;

    public static bool IsUnsupportedMod(this ModKind kind)
        => kind is ModKind.BepInExPatcher or ModKind.MelonPlugin or ModKind.Il2CppBuild;

    public static string UnsupportedReason(this ModKind kind) => kind switch
    {
        ModKind.BepInExPatcher =>
            "BepInEx preloader patchers are not yet supported.",
        ModKind.MelonPlugin =>
            "MelonLoader plugins are not yet supported.",
        ModKind.Il2CppBuild =>
            "This build is for IL2CPP games; Drag'n Wash is a mono game. Use the Mono build instead.",
        _ => null,
    };
}
