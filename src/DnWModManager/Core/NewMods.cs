namespace DnWModManager.Core;

public sealed class NewMods
{
    private readonly ManagerSettings _settings;
    private readonly HashSet<string> _shownThisVisit = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _new = new(StringComparer.OrdinalIgnoreCase);

    public NewMods(ManagerSettings settings) => _settings = settings;

    public int UnseenCount { get; private set; }

    public bool IsNew(string id) => id is not null && _new.Contains(id);

    public void Update(IEnumerable<string> listed, IEnumerable<string> installed, bool officialLoaded, bool pageOpen)
    {
        var listedIds = listed.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var installedIds = new HashSet<string>(installed.Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);

        if (_settings.SeenMods is null)
        {
            _new = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UnseenCount = 0;
            if (!officialLoaded) return;

            _settings.SeenMods = listedIds;
            _settings.Save();
            return;
        }

        var seen = new HashSet<string>(_settings.SeenMods, StringComparer.OrdinalIgnoreCase);
        var unseen = listedIds.Where(id => !seen.Contains(id) && !installedIds.Contains(id)).ToList();

        var nowSeen = listedIds.Where(id => !seen.Contains(id) && (pageOpen || installedIds.Contains(id))).ToList();
        if (nowSeen.Count > 0)
        {
            _settings.SeenMods.AddRange(nowSeen);
            _settings.Save();
        }

        if (pageOpen)
        {
            _shownThisVisit.UnionWith(unseen);
            _new = new HashSet<string>(_shownThisVisit.Where(id => !installedIds.Contains(id)), StringComparer.OrdinalIgnoreCase);
            UnseenCount = 0;
        }
        else
        {
            _new = new HashSet<string>(unseen, StringComparer.OrdinalIgnoreCase);
            UnseenCount = unseen.Count;
        }
    }

    public void PageClosed() => _shownThisVisit.Clear();
}
