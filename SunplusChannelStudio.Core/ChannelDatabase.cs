using System.Buffers.Binary;

namespace SunplusChannelStudio.Core;

/// <summary>The three record layouts the original editor offers.</summary>
public enum DatabaseType
{
    NormalDkOtp = 92,
    MultistreamV1 = 96,
    MultistreamV2 = 100,
}

public static class DatabaseTypeInfo
{
    public static int ChannelSize(this DatabaseType type) => (int)type;

    public static string Label(this DatabaseType type) => type switch
    {
        DatabaseType.NormalDkOtp => "Normal DK-OTP",
        DatabaseType.MultistreamV1 => "Multistream V1",
        _ => "Multistream V2",
    };
}

public enum FormatProblem { NotDatabase, SizeMismatch, UnknownRecordSize, BadCount }

/// <summary>The file is not a database this library understands; <see cref="Problem"/> says why.</summary>
public sealed class DatabaseFormatException(FormatProblem problem, string message) : Exception(message)
{
    public FormatProblem Problem { get; } = problem;
}

/// <summary>
/// A Sunplus DK channel database ("db15"): a 24-byte header, then satellites, transponders,
/// TV channels and radio channels as packed fixed-size records.
/// </summary>
public sealed class ChannelDatabase
{
    const int HeaderSize = 24;
    static ReadOnlySpan<byte> Magic => "db15"u8;

    /// <summary>The header word after the magic; its meaning is unknown, so it is kept as read.</summary>
    public uint HeaderWord { get; private set; }

    public DatabaseType Type { get; set; }
    public List<Satellite> Satellites { get; } = [];
    public List<Transponder> Transponders { get; } = [];
    public List<Channel> TvChannels { get; } = [];
    public List<Channel> RadioChannels { get; } = [];

    public static ChannelDatabase Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>An empty database; 0x153 is the header word the receiver writes in its own saves.</summary>
    public static ChannelDatabase Create(DatabaseType type) => new() { HeaderWord = 0x153, Type = type };

    public static ChannelDatabase Parse(byte[] data)
    {
        if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new DatabaseFormatException(FormatProblem.NotDatabase, "This is not a Sunplus DK channel database (no db15 header).");

        uint word = ReadU32(data, 4);
        int tps = Count(data, 8), tv = Count(data, 12), radio = Count(data, 16), sats = Count(data, 20);

        long fixedPart = HeaderSize + (long)sats * Satellite.Size + (long)tps * Transponder.Size;
        int channels = tv + radio;
        long channelBytes = data.Length - fixedPart;
        if (channelBytes < 0 || (channels == 0 ? channelBytes != 0 : channelBytes % channels != 0))
            throw new DatabaseFormatException(FormatProblem.SizeMismatch, "The file size does not match the counts in its header.");

        int channelSize = channels == 0 ? DatabaseType.NormalDkOtp.ChannelSize() : (int)(channelBytes / channels);
        if (!Enum.IsDefined(typeof(DatabaseType), channelSize))
            throw new DatabaseFormatException(FormatProblem.UnknownRecordSize, $"Unknown channel record size ({channelSize} bytes).");

        var db = new ChannelDatabase { HeaderWord = word, Type = (DatabaseType)channelSize };
        int at = HeaderSize;
        for (int i = 0; i < sats; i++, at += Satellite.Size) db.Satellites.Add(new Satellite(Slice(data, at, Satellite.Size)));
        for (int i = 0; i < tps; i++, at += Transponder.Size) db.Transponders.Add(new Transponder(Slice(data, at, Transponder.Size)));
        for (int i = 0; i < tv; i++, at += channelSize) db.TvChannels.Add(new Channel(Slice(data, at, channelSize)));
        for (int i = 0; i < radio; i++, at += channelSize) db.RadioChannels.Add(new Channel(Slice(data, at, channelSize)));
        return db;
    }

    public byte[] ToBytes()
    {
        int channelSize = Type.ChannelSize();
        foreach (var c in TvChannels.Concat(RadioChannels)) c.Resize(channelSize);

        int length = HeaderSize + Satellites.Count * Satellite.Size + Transponders.Count * Transponder.Size
                     + (TvChannels.Count + RadioChannels.Count) * channelSize;
        var data = new byte[length];
        Magic.CopyTo(data);
        WriteU32(data, 4, HeaderWord);
        WriteU32(data, 8, (uint)Transponders.Count);
        WriteU32(data, 12, (uint)TvChannels.Count);
        WriteU32(data, 16, (uint)RadioChannels.Count);
        WriteU32(data, 20, (uint)Satellites.Count);

        int at = HeaderSize;
        foreach (var r in Satellites.Cast<RawRecord>().Concat(Transponders).Concat(TvChannels).Concat(RadioChannels))
        {
            r.Raw.CopyTo(data, at);
            at += r.Raw.Length;
        }
        return data;
    }

    /// <summary>Writes through a temporary file so a failed save never leaves a half-written database.</summary>
    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    public Transponder? TransponderOf(Channel c) =>
        c.TransponderIndex < Transponders.Count ? Transponders[c.TransponderIndex] : null;

    public Satellite? SatelliteOf(Transponder tp) =>
        tp.SatelliteIndex < Satellites.Count ? Satellites[tp.SatelliteIndex] : null;

    static uint ReadU32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
    static void WriteU32(byte[] d, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v);

    static int Count(byte[] d, int at)
    {
        uint n = ReadU32(d, at);
        if (n > 100_000) throw new DatabaseFormatException(FormatProblem.BadCount, "The header holds an impossible record count.");
        return (int)n;
    }

    static byte[] Slice(byte[] d, int at, int size) => d.AsSpan(at, size).ToArray();
}
