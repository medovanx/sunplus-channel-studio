using System.Buffers.Binary;

namespace SunplusChannelStudio.Core;

/// <summary>Base for a fixed-size record kept as its raw bytes, so unknown fields survive a save untouched.</summary>
public abstract class RawRecord
{
    protected RawRecord(byte[] raw) => Raw = raw;

    public byte[] Raw { get; protected set; }

    protected ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(at));
    protected void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Raw.AsSpan(at), value);
    protected uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(at));
    protected void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Raw.AsSpan(at), value);

    protected string Text(int at, int size) => DvbText.Decode(Raw.AsSpan(at, size));

    protected bool Text(int at, int size, string value)
    {
        var field = DvbText.Encode(value, size, DvbText.SelectorOf(Raw.AsSpan(at, size)));
        if (field is null) return false;
        field.CopyTo(Raw, at);
        return true;
    }
}

/// <summary>Satellite, 36 bytes: flags, 20-byte name, position, LNB frequencies, options.</summary>
public sealed class Satellite(byte[] raw) : RawRecord(raw)
{
    public const int Size = 36;
    public const int NameSize = 20;

    public string Name => Text(4, NameSize);
    public bool TrySetName(string value) => Text(4, NameSize, value);

    /// <summary>Orbital position in tenths of a degree.</summary>
    public uint Position => U32(24);
    public ushort LnbLow => U16(28);
    public ushort LnbHigh => U16(30);
}

/// <summary>Transponder, 36 bytes: 16-byte name, frequency, symbol rate, satellite index, id, polarization.</summary>
public sealed class Transponder(byte[] raw) : RawRecord(raw)
{
    public const int Size = 36;
    public const int NameSize = 16;

    public string Name => Text(0, NameSize);
    public bool TrySetName(string value) => Text(0, NameSize, value);

    public ushort Frequency { get => U16(16); set => U16(16, value); }
    public ushort SymbolRate { get => U16(18); set => U16(18, value); }
    public ushort SatelliteIndex { get => U16(20); set => U16(20, value); }
    public ushort Id { get => U16(22); set => U16(22, value); }

    /// <summary>A new transponder copied from one on the same satellite, so the fields we do not decode stay valid.</summary>
    public static Transponder CloneFrom(Transponder template) => new(template.Raw.ToArray());

    /// <summary>0 is vertical, 1 is horizontal.</summary>
    public byte Polarization { get => Raw[24]; set => Raw[24] = value; }
}

public enum ChannelKind : byte { Tv = 1, Radio = 2 }

/// <summary>
/// Channel: flags (2), 42-byte name, then a service block whose first 48 bytes are shared by every DB type.
/// Multistream V1 and V2 add 4 or 8 bytes at the end.
/// </summary>
public sealed class Channel : RawRecord
{
    public const int NameSize = 42;
    const int Block = 44;

    public Channel(byte[] raw) : base(raw) { }

    public ChannelKind Kind => (ChannelKind)Raw[1];

    public string Name => Text(2, NameSize);
    public bool TrySetName(string value) => Text(2, NameSize, value);

    public ushort TransponderIndex => U16(Block + 0);
    public ushort ServiceId => U16(Block + 2);
    public ushort TransportStreamId => U16(Block + 4);
    public ushort NetworkId => U16(Block + 6);

    /// <summary>
    /// Favourite group (byte 52): 0 All, then one bit per group. Byte 53 holds the receiver's service flags,
    /// so setting a group never touches it.
    /// </summary>
    public byte Favorite { get => Raw[Block + 8]; set => Raw[Block + 8] = value; }

    /// <summary>
    /// The receiver's scan marks scrambled (pay) services with bit 0x10 of byte 53. Checked against the
    /// sample lists: every OSN and pay beIN channel has it, free channels such as Al Jazeera never do.
    /// </summary>
    public bool Scrambled
    {
        get => (Raw[Block + 9] & 0x10) != 0;
        set => Raw[Block + 9] = (byte)(value ? Raw[Block + 9] | 0x10 : Raw[Block + 9] & ~0x10);
    }

    /// <summary>True when the channel has neither a picture nor sound stream.</summary>
    public bool HasNoStreams
    {
        get
        {
            static bool None(ushort pid) => (pid & 0x1FFF) is 0 or 0x1FFF;
            bool silent = None(AudioPidA) && None(AudioPidB);
            return Kind == ChannelKind.Radio ? silent : silent && None(VideoPid);
        }
    }

    /// <summary>Bit 0 is set for H.264 (HD) video; the other bits are receiver flags.</summary>
    public ushort Definition => U16(Block + 10);

    public ushort VideoPid => U16(Block + 12);
    public uint VideoType => U32(Block + 16);
    public ushort AudioPidA => U16(Block + 20);
    public uint AudioTypeA => U32(Block + 24);
    public ushort AudioPidB => U16(Block + 28);
    public uint AudioTypeB => U32(Block + 32);
    public ushort PcrPid => U16(Block + 36);
    public ushort PmtPid => U16(Block + 38);

    /// <summary>A new, empty channel record of the given layout size.</summary>
    public static Channel Create(ChannelKind kind, int size)
    {
        var raw = new byte[size];
        raw[0] = 1;
        raw[1] = (byte)kind;
        return new Channel(raw);
    }

    public void SetService(ushort transponderIndex, ushort serviceId, ushort tsid, ushort onid)
    {
        U16(Block + 0, transponderIndex);
        U16(Block + 2, serviceId);
        U16(Block + 4, tsid);
        U16(Block + 6, onid);
    }

    /// <summary>
    /// Writes stream PIDs and types. Flag bits the receiver keeps above the 13-bit PIDs are preserved,
    /// and the HD flag follows the video codec, as the receiver's own scans set it (H.264 is HD).
    /// </summary>
    public void SetStreams(ushort video, byte videoType, ushort audioA, byte audioTypeA, ushort audioB, byte audioTypeB, ushort pcr, ushort pmt)
    {
        void Pid(int at, ushort pid) => U16(at, (ushort)((U16(at) & 0xE000) | (pid & 0x1FFF)));
        Pid(Block + 12, video);
        U32(Block + 16, videoType);
        Pid(Block + 20, audioA);
        U32(Block + 24, audioTypeA);
        Pid(Block + 28, audioB);
        U32(Block + 32, audioTypeB);
        Pid(Block + 36, pcr);
        if (pmt != 0) Pid(Block + 38, pmt);
        U16(Block + 10, (ushort)((Definition & ~1) | (videoType == 0x1B ? 1 : 0)));
    }

    /// <summary>Re-sizes the record for another DB type: the shared part is kept, the tail is cut or zero padded.</summary>
    public void Resize(int size)
    {
        if (Raw.Length == size) return;
        var next = new byte[size];
        Raw.AsSpan(0, Math.Min(size, Raw.Length)).CopyTo(next);
        Raw = next;
    }
}
