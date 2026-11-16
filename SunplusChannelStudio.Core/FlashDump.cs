using System.Buffers.Binary;
using System.IO.Compression;

namespace SunplusChannelStudio.Core;

/// <summary>
/// Reads the channel list a receiver is holding right now out of a full read of its SPI flash chip.
/// Checked on a 4 MiB 1506F dump, where it rebuilt the loaded list byte for byte.
///
/// The live store is a block marked DE AD BE EF (stored little endian as EF BE AD DE) holding the counts,
/// the satellite records and the display order lists. Two gzip blobs, each behind a 4-byte length, hold the
/// transponder table (36-byte records) and the channel table (records of the database's size).
/// The channel table keeps channels in storage slots; the order lists, pairs of (slot, tag) with tag
/// 0x0102 for TV and 0x0202 for radio, give the order the receiver shows them in.
/// Everything is found by its signature, so a dump with a different layout still reads.
/// </summary>
public static class FlashDump
{
    const uint Marker = 0xDEADBEEF;
    const ushort TvTag = 0x0102, RadioTag = 0x0202;
    const int Step = 0x1000; // flash sectors; the blocks start on sector boundaries

    /// <summary>True when the bytes look like a flash read with a live channel store in it.</summary>
    public static bool Looks(byte[] flash) => FindStore(flash) >= 0;

    public static ChannelDatabase Read(byte[] flash)
    {
        int store = FindStore(flash);
        if (store < 0) throw new DatabaseFormatException(FormatProblem.NotDatabase, "No channel store was found in this flash read.");

        uint tps = U32(flash, store + 16), tv = U32(flash, store + 20), radio = U32(flash, store + 24), sats = U32(flash, store + 28);
        if (tps > 10000 || tv > 20000 || radio > 20000 || sats is 0 or > 200)
            throw new DatabaseFormatException(FormatProblem.BadCount, "The channel store's counts are not plausible.");

        // The two tables are gzip blobs whose sizes and contents fit their record lengths. They are searched for
        // across the whole read: on the checked dump the transponder table sits before the store and the channel
        // table after it.
        byte[]? tpTable = null, chTable = null;
        int channelSize = 0;
        for (int at = 0; at + 8 < flash.Length && (tpTable is null || chTable is null); at += Step)
        {
            if (Gunzip(flash, at) is not { } blob) continue;
            if (tpTable is null && blob.Length % Transponder.Size == 0 && blob.Length >= tps * Transponder.Size && LooksLikeTransponders(blob, (int)tps)) { tpTable = blob; continue; }
            if (chTable is null && ChannelRecordSize(blob, (int)(tv + radio)) is var size and > 0) { chTable = blob; channelSize = size; }
        }
        if (tpTable is null || chTable is null)
            throw new DatabaseFormatException(FormatProblem.NotDatabase, "The transponder or channel table was not found in this flash read.");

        var tvOrder = FindOrder(flash, store, TvTag, (int)tv);
        var radioOrder = FindOrder(flash, store, RadioTag, (int)radio);

        var db = ChannelDatabase.Create((DatabaseType)channelSize);
        for (int i = 0; i < sats; i++) db.Satellites.Add(new Satellite(Slice(flash, store + 32 + i * Satellite.Size, Satellite.Size)));
        for (int i = 0; i < tps; i++) db.Transponders.Add(new Transponder(Slice(tpTable, i * Transponder.Size, Transponder.Size)));
        int slots = chTable.Length / channelSize;
        Channel At(int slot) => slot < slots ? new Channel(Slice(chTable, slot * channelSize, channelSize))
            : throw new DatabaseFormatException(FormatProblem.BadCount, "An order list points past the channel table.");
        db.TvChannels.AddRange(tvOrder.Select(At));
        db.RadioChannels.AddRange(radioOrder.Select(At));
        return db;
    }

    static int FindStore(byte[] flash)
    {
        for (int at = 0; at + 64 <= flash.Length; at += Step)
            if (U32(flash, at) == Marker && U32(flash, at + 4) == 0 && U32(flash, at + 28) is > 0 and < 200) return at;
        return -1;
    }

    /// <summary>The order list for one kind: count pairs of (slot, tag), searched for inside the store's sectors.</summary>
    static List<int> FindOrder(byte[] flash, int store, ushort tag, int count)
    {
        if (count == 0) return [];
        int end = Math.Min(flash.Length - 4, store + 0x20000);
        for (int at = store + 32; at + count * 4 <= end; at += 4)
        {
            if (U16(flash, at + 2) != tag || (at >= 4 && U16(flash, at - 2) == tag)) continue; // start of a run only
            var slots = new List<int>(count);
            for (int k = 0; k < count && U16(flash, at + k * 4 + 2) == tag; k++) slots.Add(U16(flash, at + k * 4));
            if (slots.Count == count) return slots;
        }
        throw new DatabaseFormatException(FormatProblem.BadCount, $"The order list for {(tag == TvTag ? "TV" : "radio")} channels was not found.");
    }

    static bool LooksLikeTransponders(byte[] blob, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ushort f = U16(blob, i * Transponder.Size + 16), sr = U16(blob, i * Transponder.Size + 18);
            if (f is < 2000 or > 15000 || sr is < 500 or > 50000 || blob[i * Transponder.Size + 24] > 1) return false;
        }
        return true;
    }

    /// <summary>The record size (92, 96 or 100) at which the first <paramref name="used"/> records all have a channel kind.</summary>
    static int ChannelRecordSize(byte[] blob, int used)
    {
        foreach (var type in Enum.GetValues<DatabaseType>())
        {
            int size = type.ChannelSize();
            if (blob.Length % size != 0 || blob.Length / size < used) continue;
            int kinds = 0;
            for (int i = 0; i < blob.Length / size; i++) if (blob[i * size + 1] is 1 or 2) kinds++;
            if (kinds == used) return size;
        }
        return 0;
    }

    /// <summary>A gzip blob behind a 4-byte little-endian length at <paramref name="at"/>, or null.</summary>
    static byte[]? Gunzip(byte[] flash, int at)
    {
        int length = (int)U32(flash, at);
        if (length is < 20 or > 0x400000 || at + 4 + length > flash.Length) return null;
        if (flash[at + 4] != 0x1f || flash[at + 5] != 0x8b || flash[at + 6] != 0x08) return null;
        try
        {
            using var gz = new GZipStream(new MemoryStream(flash, at + 4, length), CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    static byte[] Slice(byte[] d, int at, int n) => d.AsSpan(at, n).ToArray();
    static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
    static ushort U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
}
