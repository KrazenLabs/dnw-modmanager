namespace DnWModManager.Core;

public sealed class Quarantine
{
    private readonly GameInstall _install;
    private string _session;

    public Quarantine(GameInstall install) => _install = install;

    public string SessionDirectory
    {
        get
        {
            if (_session is not null) return _session;
            _session = Path.Combine(_install.QuarantineDirectory, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(_session);
            WriteReadme(_session);
            return _session;
        }
    }

    public bool UsedThisSession => _session is not null;

    public string TakeFile(string path)
    {
        string destination = Reserve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(path, destination);
        return destination;
    }

    public string TakeDirectory(string path)
    {
        string destination = Reserve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(path, destination);
        }
        catch (IOException)
        {
            CopyDirectory(path, destination);
            Directory.Delete(path, recursive: true);
        }
        return destination;
    }

    private string Reserve(string source)
    {
        string relative = _install.Relative(source);
        if (Path.IsPathRooted(relative)) relative = Path.GetFileName(relative);

        string candidate = Path.Combine(SessionDirectory, relative);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        string directory = Path.GetDirectoryName(candidate)!;
        string stem = Path.GetFileNameWithoutExtension(candidate);
        string extension = Path.GetExtension(candidate);
        for (int n = 2; n < 1000; n++)
        {
            string attempt = Path.Combine(directory, stem + " (" + n + ")" + extension);
            if (!File.Exists(attempt) && !Directory.Exists(attempt)) return attempt;
        }
        throw new IOException("Could not find a free name in " + directory + ".");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var child in Directory.GetDirectories(source))
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    private static void WriteReadme(string directory)
    {
        const string text = """
            Quarantine - DnW Mod Manager
            ============================

            Everything in this folder was moved out of the game by the DnW Mod Manager, either
            because it was incorrectly installed or because it conflicted
            with something else.

            If the game works and you do not miss anything, this folder is safe to delete.
            """;
        try
        {
            File.WriteAllText(Path.Combine(directory, "README.txt"), text);
        }
        catch { }
    }
}
