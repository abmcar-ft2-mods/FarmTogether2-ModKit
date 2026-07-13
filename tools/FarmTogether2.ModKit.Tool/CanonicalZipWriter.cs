using System.Text;

namespace FarmTogether2.ModKit.Tool;

internal static class CanonicalZipWriter
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const ushort Version20 = 20;
    private const ushort Utf8Flag = 1 << 11;
    private const ushort StoredMethod = 0;
    private const ushort FixedDosTime = 0;
    private const ushort FixedDosDate = ((2000 - 1980) << 9) | (1 << 5) | 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    public static byte[] Write(IReadOnlyDictionary<string, byte[]> entries)
    {
        using MemoryStream stream = new();
        Write(stream, entries);
        return stream.ToArray();
    }

    public static void Write(Stream output, IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(entries);
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0)
            throw new ArgumentException("Canonical ZIP output must be an empty, writable, seekable stream.", nameof(output));
        if (entries.Count >= ushort.MaxValue)
            throw new InvalidDataException("Canonical ZIP contains too many entries.");

        string[] paths = entries.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!paths.SequenceEqual(entries.Keys, StringComparer.Ordinal))
            throw new InvalidDataException("Canonical ZIP entries must be supplied in ordinal order.");

        List<CentralEntry> centralEntries = new(paths.Length);
        using BinaryWriter writer = new(output, StrictUtf8, leaveOpen: true);
        foreach (string path in paths)
        {
            if (IsUnsafePath(path))
                throw new InvalidDataException($"Canonical ZIP entry path is unsafe: {path}");
            byte[] content = entries[path] ?? throw new InvalidDataException($"Canonical ZIP entry is null: {path}");
            byte[] name = StrictUtf8.GetBytes(path);
            if (name.Length == 0 || name.Length > ushort.MaxValue)
                throw new InvalidDataException($"Canonical ZIP entry name has an invalid byte length: {path}");
            if (content.LongLength >= uint.MaxValue || output.Position >= uint.MaxValue)
                throw new InvalidDataException("Canonical ZIP requires ZIP64, which is not supported.");

            uint offset = checked((uint)output.Position);
            uint size = checked((uint)content.Length);
            uint crc32 = Crc32(content);
            WriteLocalHeader(writer, name, crc32, size);
            writer.Write(name);
            writer.Write(content);
            centralEntries.Add(new CentralEntry(name, crc32, size, offset));
        }

        if (output.Position >= uint.MaxValue)
            throw new InvalidDataException("Canonical ZIP requires ZIP64, which is not supported.");
        uint centralOffset = checked((uint)output.Position);
        foreach (CentralEntry entry in centralEntries)
        {
            WriteCentralHeader(writer, entry);
            writer.Write(entry.Name);
        }
        long centralLength = output.Position - centralOffset;
        if (centralLength >= uint.MaxValue || output.Position > uint.MaxValue - 22L)
            throw new InvalidDataException("Canonical ZIP requires ZIP64, which is not supported.");
        WriteEndOfCentralDirectory(writer, checked((ushort)centralEntries.Count), checked((uint)centralLength), centralOffset);
        writer.Flush();
    }

    private static void WriteLocalHeader(BinaryWriter writer, byte[] name, uint crc32, uint size)
    {
        writer.Write(LocalHeaderSignature);
        writer.Write(Version20);
        writer.Write(Utf8Flag);
        writer.Write(StoredMethod);
        writer.Write(FixedDosTime);
        writer.Write(FixedDosDate);
        writer.Write(crc32);
        writer.Write(size);
        writer.Write(size);
        writer.Write(checked((ushort)name.Length));
        writer.Write((ushort)0);
    }

    private static void WriteCentralHeader(BinaryWriter writer, CentralEntry entry)
    {
        writer.Write(CentralHeaderSignature);
        writer.Write(Version20); // Version 2.0, created on the canonical MS-DOS host.
        writer.Write(Version20);
        writer.Write(Utf8Flag);
        writer.Write(StoredMethod);
        writer.Write(FixedDosTime);
        writer.Write(FixedDosDate);
        writer.Write(entry.Crc32);
        writer.Write(entry.Size);
        writer.Write(entry.Size);
        writer.Write(checked((ushort)entry.Name.Length));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(entry.LocalHeaderOffset);
    }

    private static void WriteEndOfCentralDirectory(
        BinaryWriter writer,
        ushort entryCount,
        uint centralLength,
        uint centralOffset)
    {
        writer.Write(EndOfCentralDirectorySignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(entryCount);
        writer.Write(entryCount);
        writer.Write(centralLength);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
    }

    private static uint Crc32(ReadOnlySpan<byte> content)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in content)
            crc = Crc32Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : 0xedb88320u ^ (value >> 1);
            table[index] = value;
        }
        return table;
    }

    private static bool IsUnsafePath(string path) =>
        string.IsNullOrEmpty(path) || path.Contains('\0') || path.Contains('\\') ||
        path.StartsWith('/') || path.Contains(':') ||
        path.Split('/').Any(segment => segment is "" or "." or "..");

    private sealed record CentralEntry(byte[] Name, uint Crc32, uint Size, uint LocalHeaderOffset);
}
