using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DnWModManager.Core;

public static class GameVersion
{
    private const int PlayerSettingsClassId = 129;
    private const int MonoBehaviourClassId = 114;
    private const int MaxEntries = 100000;

    private static readonly Regex GameLine = new(@"^Game: .+ (?<version>\S+) by .+ \| Unity ", RegexOptions.Compiled);

    public static string FromGameFiles(GameInstall install)
    {
        try
        {
            byte[] file = File.ReadAllBytes(Path.Combine(install.DataDirectory, "globalgamemanagers"));
            var settings = FindObject(file, PlayerSettingsClassId);
            return settings is null
                ? null
                : BundleVersion(file.AsSpan(settings.Value.Start, settings.Value.Size), ProductName(install));
        }
        catch
        {
            return null;
        }
    }

    public static string FromLog(LogReader.LogFile log)
    {
        if (log is null) return null;
        for (int i = log.Entries.Count - 1; i >= 0; i--)
        {
            var entry = log.Entries[i];
            if (entry.Source != "Loader") continue;
            var match = GameLine.Match(entry.Message);
            if (match.Success) return match.Groups["version"].Value;
        }
        return null;
    }

    private static (int Start, int Size)? FindObject(byte[] file, int classId)
    {
        var reader = new Reader(file) { Position = 8 };
        uint version = reader.UInt32();
        long dataOffset = reader.UInt32();
        if (version < 17) return null;

        reader.Position = 16;
        if (reader.Byte() != 0) return null;

        reader.Position = 20;
        if (version >= 22)
        {
            reader.UInt32();
            reader.Int64();
            dataOffset = reader.Int64();
            reader.Int64();
        }

        reader.BigEndian = false;
        reader.CString();
        reader.Int32();
        if (reader.Byte() != 0) return null;

        int typeCount = reader.Int32();
        if (typeCount is < 0 or > MaxEntries) return null;
        var classIds = new int[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            classIds[i] = reader.Int32();
            reader.Skip(3);
            if (classIds[i] == MonoBehaviourClassId) reader.Skip(16);
            reader.Skip(16);
        }

        int objectCount = reader.Int32();
        if (objectCount is < 0 or > MaxEntries) return null;
        for (int i = 0; i < objectCount; i++)
        {
            reader.Align();
            reader.Int64();
            long start = version >= 22 ? reader.Int64() : reader.UInt32();
            uint size = reader.UInt32();
            int type = reader.Int32();
            if (type >= 0 && type < typeCount && classIds[type] == classId)
                return (checked((int)(dataOffset + start)), checked((int)size));
        }
        return null;
    }

    private static string BundleVersion(ReadOnlySpan<byte> data, string productName)
    {
        var strings = new List<(int Offset, int Next, string Text)>();
        for (int i = 0; i + 4 <= data.Length;)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[i..]);
            if (length is > 0 and <= 256 && length <= data.Length - i - 4 && IsText(data.Slice(i + 4, length)))
            {
                int next = (i + 4 + length + 3) & ~3;
                strings.Add((i, next, Encoding.UTF8.GetString(data.Slice(i + 4, length))));
                i = next;
            }
            else
            {
                i += 4;
            }
        }

        int product = strings.FindIndex(s => s.Text == productName);
        if (product < 0) product = 1;

        for (int first = product + 1; first + 1 < strings.Count; first++)
        {
            if (strings[first].Next != strings[first + 1].Offset) continue;
            int last = first + 1;
            while (last + 1 < strings.Count && strings[last].Next == strings[last + 1].Offset) last++;
            return strings[last].Text;
        }
        return null;
    }

    private static bool IsText(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b < 0x20 || b == 0x7F) return false;
        return true;
    }

    private static string ProductName(GameInstall install)
    {
        try
        {
            string[] lines = File.ReadAllLines(Path.Combine(install.DataDirectory, "app.info"));
            return lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _data;

        public Reader(byte[] data) => _data = data;

        public int Position { get; set; }
        public bool BigEndian { get; set; } = true;

        public byte Byte() => Take(1)[0];

        public int Int32() => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint UInt32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public long Int64() => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public void Skip(int count) => Take(count);

        public void Align() => Position = (Position + 3) & ~3;

        public string CString()
        {
            int end = Array.IndexOf(_data, (byte)0, Position);
            if (end < 0) throw new InvalidDataException("Unterminated string.");
            string text = Encoding.ASCII.GetString(_data, Position, end - Position);
            Position = end + 1;
            return text;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            var span = new ReadOnlySpan<byte>(_data, Position, count);
            Position += count;
            return span;
        }
    }
}
