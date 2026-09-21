using System.Text.RegularExpressions;

namespace DnWModManager.Core;

public static class ModVersion
{
    private static readonly Regex LeadingNumber = new(@"^\d+", RegexOptions.Compiled);
    private static readonly char[] Separators = { '-', '+', ' ' };

    public static bool TryParse(string text, out Version version)
    {
        version = null;
        if (string.IsNullOrEmpty(text)) return false;
        text = text.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        int cut = text.IndexOfAny(Separators);
        if (cut >= 0) text = text[..cut];

        var numbers = new List<int>();
        foreach (var part in text.Split('.'))
        {
            var match = LeadingNumber.Match(part);
            if (!match.Success || !int.TryParse(match.Value, out int n)) break;
            numbers.Add(n);
            if (numbers.Count == 4) break;
        }
        if (numbers.Count == 0) return false;
        while (numbers.Count < 2) numbers.Add(0);

        version = numbers.Count switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    public static Version ParseOrDefault(string text, Version fallback = null)
        => TryParse(text, out var v) ? v : fallback ?? new Version(0, 0);

    public static int Compare(Version a, Version b)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        int c = Clamp(a.Major).CompareTo(Clamp(b.Major)); if (c != 0) return c;
        c = Clamp(a.Minor).CompareTo(Clamp(b.Minor)); if (c != 0) return c;
        c = Clamp(a.Build).CompareTo(Clamp(b.Build)); if (c != 0) return c;
        return Clamp(a.Revision).CompareTo(Clamp(b.Revision));
    }

    public static int Compare(string a, string b) => Compare(ParseOrDefault(a), ParseOrDefault(b));

    public static bool IsNewer(string candidate, string installed)
        => TryParse(candidate, out var c) && Compare(c, ParseOrDefault(installed)) > 0;

    public static string Display(Version version)
    {
        if (version is null) return "?";
        if (version.Revision > 0) return version.ToString(4);
        return version.ToString(3);
    }

    private static int Clamp(int x) => x < 0 ? 0 : x;
}

public sealed class VersionConstraint
{
    // Longest first, ">=" must win over ">"
    private static readonly string[] Operators = { ">=", "<=", "==", ">", "<", "=", "^", "~" };

    private readonly string _op;
    private readonly Version _version;

    public string Text { get; }

    private VersionConstraint(string text, string op, Version version)
    {
        Text = text;
        _op = op;
        _version = version;
    }

    public static VersionConstraint Any { get; } = new("*", "*", null);

    public static bool TryParse(string text, out VersionConstraint constraint)
    {
        constraint = null;
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*") { constraint = Any; return true; }

        string rest = text.Trim();
        string op = ">=";
        foreach (var candidate in Operators)
        {
            if (!rest.StartsWith(candidate, StringComparison.Ordinal)) continue;
            op = candidate == "==" ? "=" : candidate;
            rest = rest[candidate.Length..].Trim();
            break;
        }
        if (!ModVersion.TryParse(rest, out var version)) return false;
        constraint = new VersionConstraint(text, op, version);
        return true;
    }

    public static VersionConstraint ParseOrAny(string text)
        => TryParse(text, out var c) ? c : Any;

    public bool Satisfies(Version actual)
    {
        if (_version is null) return true;
        if (actual is null) return false;
        int c = ModVersion.Compare(actual, _version);
        return _op switch
        {
            ">=" => c >= 0,
            ">" => c > 0,
            "<=" => c <= 0,
            "<" => c < 0,
            "=" => c == 0,
            "^" => c >= 0 && actual.Major == _version.Major,
            "~" => c >= 0 && actual.Major == _version.Major && actual.Minor == _version.Minor,
            _ => true,
        };
    }

    public bool Satisfies(string actual) => Satisfies(ModVersion.ParseOrDefault(actual));

    public override string ToString() => Text;
}
