using System.Text;

namespace DnWModManager.Core;

public static class Vdf
{
    private enum Token
    {
        End,
        Open,
        Close,
        Text,
    }

    public static Dictionary<string, object> Parse(string text)
    {
        int index = 0;
        return ReadBlock(text ?? "", ref index);
    }

    public static Dictionary<string, object> Block(Dictionary<string, object> root, params string[] path)
    {
        var current = root;
        foreach (string key in path)
        {
            if (current is null || !current.TryGetValue(key, out var next) || next is not Dictionary<string, object> block) return null;
            current = block;
        }
        return current;
    }

    public static string Value(Dictionary<string, object> block, string key)
        => block is not null && block.TryGetValue(key, out var value) ? value as string : null;

    private static Dictionary<string, object> ReadBlock(string text, ref int index)
    {
        var block = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var kind = Next(text, ref index, out string key);
            if (kind is Token.End or Token.Close) return block;
            if (kind == Token.Open) continue;

            kind = Next(text, ref index, out string value);
            if (kind == Token.Open) block[key] = ReadBlock(text, ref index);
            else if (kind == Token.Text) block[key] = value;
            else return block;
        }
    }

    private static Token Next(string text, ref int index, out string value)
    {
        value = null;
        while (index < text.Length)
        {
            char c = text[index];
            if (char.IsWhiteSpace(c)) { index++; continue; }
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }
            break;
        }
        if (index >= text.Length) return Token.End;

        char first = text[index];
        if (first == '{') { index++; return Token.Open; }
        if (first == '}') { index++; return Token.Close; }

        var result = new StringBuilder();
        if (first == '"')
        {
            index++;
            while (index < text.Length && text[index] != '"')
            {
                char c = text[index++];
                if (c == '\\' && index < text.Length)
                {
                    char escaped = text[index++];
                    result.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                }
                else result.Append(c);
            }
            index++;
        }
        else
        {
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"'))
                result.Append(text[index++]);
        }
        value = result.ToString();
        return Token.Text;
    }
}
