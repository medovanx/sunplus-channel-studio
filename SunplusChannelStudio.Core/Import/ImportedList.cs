namespace SunplusChannelStudio.Core.Import;

public enum Polarity { Vertical, Horizontal }

/// <summary>A transponder from an imported list. Frequency in MHz, symbol rate in kS/s.</summary>
public sealed record ImportedTransponder(int Frequency, Polarity Polarization, int SymbolRate, int? Position = null);

/// <summary>A channel from an imported list. PIDs are 0 when the list does not give them.</summary>
public sealed record ImportedService(
    string Name,
    ChannelKind Kind,
    ImportedTransponder Transponder,
    ushort ServiceId,
    ushort TransportStreamId,
    ushort NetworkId,
    ushort VideoPid,
    byte VideoType,
    ushort AudioPid,
    byte AudioType,
    ushort AudioPidB,
    byte AudioTypeB,
    ushort PcrPid,
    ushort PmtPid,
    bool Scrambled = false);

/// <summary>What a file gave us: transponders always, channels only from formats that carry them.</summary>
public sealed class ImportedList
{
    public required string Format { get; init; }
    public List<ImportedTransponder> Transponders { get; } = [];
    public List<ImportedService> Services { get; } = [];

    /// <summary>Orbital positions in the file, in tenths of a degree, east positive. Empty when the file has none.</summary>
    public IReadOnlyList<int> Positions =>
        Transponders.Select(t => t.Position).OfType<int>().Distinct().Order().ToList();

    /// <summary>Only the part of the list at one orbital position.</summary>
    public ImportedList At(int? position)
    {
        if (position is null) return this;
        var list = new ImportedList { Format = Format };
        list.Transponders.AddRange(Transponders.Where(t => t.Position == position));
        list.Services.AddRange(Services.Where(s => s.Transponder.Position == position));
        return list;
    }

    /// <summary>"7.0W", "26.0E" for a position in tenths of a degree.</summary>
    public static string PositionLabel(int position) =>
        position < 0 ? $"{-position / 10.0:0.0}W" : $"{position / 10.0:0.0}E";
}

/// <summary>DVB stream type codes, as stored by the receiver (ISO/IEC 13818-1).</summary>
public static class StreamType
{
    public const byte Mpeg1Video = 0x01, Mpeg2Video = 0x02, H264 = 0x1B, Hevc = 0x24;
    public const byte Mpeg1Audio = 0x03, Mpeg2Audio = 0x04, Aac = 0x0F, AacLatm = 0x11, Ac3 = 0x81;
}
