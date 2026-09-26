using DnWModManager.Core;

namespace DnWModManager.ViewModels;

public sealed class ResourceFolderViewModel : ObservableObject
{
    private int _count;
    private bool _isDropTarget;

    public ResourceFolderViewModel(InstalledMod mod, ResourceFolder folder)
    {
        Mod = mod;
        Folder = folder;
        _count = folder.CountFiles();
    }

    public InstalledMod Mod { get; }
    public ResourceFolder Folder { get; }

    public string Name => Folder.Name;
    public string ModName => Mod.Name;
    public string Description => Folder.Description;

    public string CountText => _count switch
    {
        0 => "empty",
        1 => "1 file",
        _ => _count + " files",
    };

    public string DropTypes => Folder.Extensions.Count == 0 ? "any files" : Folder.FileTypesLabel + " files";

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public void Recount()
    {
        _count = Folder.CountFiles();
        Raise(nameof(CountText));
    }
}
