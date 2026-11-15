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
    public ushort SatelliteIndex => U16(20);
    public ushort Id => U16(22);

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

    /// <summary>Favourite group: 0 All, then one bit per group; any other value shows as none.</summary>
    public ushort Favorite { get => U16(Block + 8); set => U16(Block + 8, value); }

    /// <summary>0 SD, 1 HD, other values TR.</summary>
    public ushort Definition => U16(Block + 10);

    public ushort VideoPid => U16(Block + 12);
    public ushort AudioPidA => U16(Block + 20);
    public ushort AudioPidB => U16(Block + 28);
    public ushort PcrPid => U16(Block + 36);
    public ushort PmtPid => U16(Block + 38);

    /// <summary>Re-sizes the record for another DB type: the shared part is kept, the tail is cut or zero padded.</summary>
    public void Resize(int size)
    {
        if (Raw.Length == size) return;
        var next = new byte[size];
        Raw.AsSpan(0, Math.Min(size, Raw.Length)).CopyTo(next);
        Raw = next;
    }
}
