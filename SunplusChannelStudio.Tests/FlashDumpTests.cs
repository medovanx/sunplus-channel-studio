using System.Buffers.Binary;
using System.IO.Compression;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio.Tests;

public class FlashDumpTests
{
    static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
        return output.ToArray();
    }

    static void Blob(byte[] flash, int at, byte[] data)
    {
        var packed = Gzip(data);
        BinaryPrimitives.WriteUInt32LittleEndian(flash.AsSpan(at), (uint)packed.Length);
        packed.CopyTo(flash, at + 4);
    }

    /// <summary>A small flash image laid out like the receiver's: store block, order lists, then the two gzip tables.</summary>
    static byte[] FakeFlash()
    {
        var sample = ChannelDatabase.Parse(Samples.Database()); // 1 satellite, 1 transponder, TV "One", "Two", radio "Radio"
        var flash = new byte[0x40000];
        Array.Fill(flash, (byte)0xff, 0, 0x10000); // erased space before the store
        int store = 0x10000;
        BinaryPrimitives.WriteUInt32LittleEndian(flash.AsSpan(store), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(flash.AsSpan(store + 4), 0);
        foreach (var (v, i) in new[] { (1u, 4), (2u, 5), (1u, 6), (1u, 7) }) BinaryPrimitives.WriteUInt32LittleEndian(flash.AsSpan(store + i * 4), v);
        sample.Satellites[0].Raw.CopyTo(flash, store + 32);

        // Storage slots: 0 = "Two", 1 = "Radio", 2 = "One"; the TV order list says One then Two.
        var ch = new byte[10 * 92];
        sample.TvChannels[1].Raw.CopyTo(ch, 0); sample.RadioChannels[0].Raw.CopyTo(ch, 92); sample.TvChannels[0].Raw.CopyTo(ch, 184);
        void Order(int at, ushort tag, params ushort[] slots)
        {
            for (int k = 0; k < slots.Length; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(flash.AsSpan(at + k * 4), slots[k]);
                BinaryPrimitives.WriteUInt16LittleEndian(flash.AsSpan(at + k * 4 + 2), tag);
            }
        }
        Order(store + 0x400, 0x0102, 2, 0);
        Order(store + 0x800, 0x0202, 1);

        var tp = new byte[5 * 36];
        sample.Transponders[0].Raw.CopyTo(tp, 0);
        Blob(flash, 0x20000, tp);
        Blob(flash, 0x30000, ch);
        return flash;
    }

    [Fact]
    public void Reads_the_live_list_in_display_order()
    {
        var flash = FakeFlash();
        Assert.True(FlashDump.Looks(flash));
        var db = FlashDump.Read(flash);
        Assert.Equal(DatabaseType.NormalDkOtp, db.Type);
        Assert.Single(db.Satellites);
        Assert.Equal(11766, Assert.Single(db.Transponders).Frequency);
        Assert.Equal(["One", "Two"], db.TvChannels.Select(c => c.Name));
        Assert.Equal("Radio", Assert.Single(db.RadioChannels).Name);
        Assert.Equal(db.ToBytes(), ChannelDatabase.Parse(db.ToBytes()).ToBytes()); // saves as a normal receiver file
    }

    [Fact]
    public void A_plain_file_is_not_a_flash_read()
    {
        Assert.False(FlashDump.Looks(Samples.Database()));
        Assert.False(FlashDump.Looks(new byte[0x10000]));
        Assert.Throws<DatabaseFormatException>(() => FlashDump.Read(new byte[0x10000]));
    }

    /// <summary>
    /// On the user's machine only: their receiver's flash read holds the Arabic list it had loaded,
    /// and reading it must give that file back byte for byte (header word aside, which a new file writes as 0x153).
    /// </summary>
    [Fact]
    public void Real_dump_gives_back_the_loaded_list()
    {
        const string flashPath = @"M:\Mohamed\Programs\DK Channel Editor\_re\firmware\flash.bin";
        const string listPath = @"M:\Mohamed\Programs\DK Channel Editor\_re\test\arabic_db.bin";
        if (!File.Exists(flashPath) || !File.Exists(listPath)) return;
        var read = FlashDump.Read(File.ReadAllBytes(flashPath)).ToBytes();
        var expected = File.ReadAllBytes(listPath);
        Assert.Equal(expected.Length, read.Length);
        Assert.Equal(expected.AsSpan(8).ToArray(), read.AsSpan(8).ToArray());
    }
}
