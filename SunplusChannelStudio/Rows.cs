using System.ComponentModel;
using System.Runtime.CompilerServices;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio;

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the user changes the underlying record.</summary>
    public static event Action? Edited;

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected static void MarkEdited() => Edited?.Invoke();

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>Some lists store line breaks or tabs in names; they show as spaces.</summary>
    protected static string Clean(string name) =>
        string.Create(name.Length, name, (span, s) => { for (int i = 0; i < s.Length; i++) span[i] = char.IsControl(s[i]) ? ' ' : s[i]; }).TrimEnd();
}

public sealed record FavoriteGroup(ushort Value, string Key)
{
    /// <summary>The values the original editor recognises; any other value shows as none.</summary>
    public static readonly FavoriteGroup[] All =
    [
        new(0x00, "Fav.All"), new(0x01, "Fav.News"), new(0x02, "Fav.Science"), new(0x04, "Fav.Sports"), new(0x08, "Fav.Movies"),
        new(0x10, "Fav.Kids"), new(0x20, "Fav.Music"), new(0x40, "Fav.Fashion"), new(0x80, "Fav.Financial"),
    ];

    public string Label => Loc.Get(Key);

    public static string LabelOf(ushort value) => All.FirstOrDefault(g => g.Value == value)?.Label ?? Loc.Get("Fav.None");
}

public sealed class ChannelRow(Channel channel, ChannelDatabase db) : Row
{
    public Channel Channel { get; } = channel;

    int _number;
    public int Number { get => _number; set { if (_number != value) { _number = value; Changed(); } } }

    public string Name
    {
        get => Clean(Channel.Name);
        set
        {
            value = value.Trim();
            if (value == Clean(Channel.Name)) return;
            if (value.Length == 0) throw new ArgumentException(Loc.Get("Val.NeedName"));
            if (!Channel.TrySetName(value))
                throw new ArgumentException(Loc.Format("Val.TooLong", Core.Channel.NameSize - 2));
            Changed();
            MarkEdited();
        }
    }

    public string Favorite => FavoriteGroup.LabelOf(Channel.Favorite);

    public void SetFavorite(ushort value)
    {
        if (Channel.Favorite == value) return;
        Channel.Favorite = value;
        Changed(nameof(Favorite));
        MarkEdited();
    }

    public string Definition => Channel.Definition switch { 0 => "SD", 1 => "HD", _ => "TR" };

    Transponder? Tp => db.TransponderOf(Channel);
    public string Frequency => Tp is { } tp ? $"{tp.Frequency} {(tp.Polarization == 0 ? "V" : "H")} {tp.SymbolRate}" : "?";
    public string Satellite => Tp is { } tp ? db.SatelliteOf(tp)?.Name ?? "?" : "?";

    public ushort ServiceId => Channel.ServiceId;
    public string VideoPid => Pid(Channel.VideoPid);
    public string AudioPidA => Pid(Channel.AudioPidA);
    public string AudioPidB => Pid(Channel.AudioPidB);
    public string PcrPid => Pid(Channel.PcrPid);
    public string PmtPid => Pid(Channel.PmtPid);

    // A PID is 13 bits; the receiver keeps flags in the top 3. 0x1FFF is the null PID (no stream).
    static string Pid(ushort raw)
    {
        int pid = raw & 0x1FFF;
        return pid is 0 or 0x1FFF ? "" : pid.ToString();
    }
}

public sealed class TransponderRow(Transponder tp, int index, ChannelDatabase db) : Row
{
    public Transponder Transponder { get; } = tp;
    public int Index { get; } = index;
    public int Channels { get; set; }

    public string Name
    {
        get => Clean(Transponder.Name);
        set
        {
            value = value.Trim();
            if (value == Clean(Transponder.Name)) return;
            if (!Transponder.TrySetName(value))
                throw new ArgumentException(Loc.Format("Val.TooLong", Transponder.NameSize - 2));
            Changed();
            MarkEdited();
        }
    }

    public ushort Frequency
    {
        get => Transponder.Frequency;
        set
        {
            if (value is < 2000 or > 15000) throw new ArgumentException(Loc.Get("Val.Frequency"));
            if (value == Transponder.Frequency) return;
            Transponder.Frequency = value;
            Changed();
            MarkEdited();
        }
    }

    public ushort SymbolRate
    {
        get => Transponder.SymbolRate;
        set
        {
            if (value is < 1000 or > 45000) throw new ArgumentException(Loc.Get("Val.SymbolRate"));
            if (value == Transponder.SymbolRate) return;
            Transponder.SymbolRate = value;
            Changed();
            MarkEdited();
        }
    }

    public static string[] Polarizations => [Loc.Get("Pol.V"), Loc.Get("Pol.H")];

    public string Polarization
    {
        get => Loc.Get(Transponder.Polarization == 0 ? "Pol.V" : "Pol.H");
        set
        {
            byte next = value == Loc.Get("Pol.V") ? (byte)0 : (byte)1;
            if (next == Transponder.Polarization) return;
            Transponder.Polarization = next;
            Changed();
            MarkEdited();
        }
    }

    public string Satellite => db.SatelliteOf(Transponder)?.Name ?? "?";
}

public sealed class SatelliteRow(Satellite sat, int index) : Row
{
    public Satellite Satellite { get; } = sat;
    public int Index { get; } = index;
    public int Transponders { get; set; }

    public string Name
    {
        get => Clean(Satellite.Name);
        set
        {
            value = value.Trim();
            if (value == Clean(Satellite.Name)) return;
            if (value.Length == 0) throw new ArgumentException(Loc.Get("Val.NeedName"));
            if (!Satellite.TrySetName(value))
                throw new ArgumentException(Loc.Format("Val.TooLong", Core.Satellite.NameSize - 2));
            Changed();
            MarkEdited();
        }
    }

    public string Position => Satellite.Position == 0 ? "" : $"{Satellite.Position / 10.0:0.0}°";
    public string Lnb => Satellite.LnbLow == Satellite.LnbHigh
        ? $"{Satellite.LnbLow} MHz"
        : $"{Satellite.LnbLow} / {Satellite.LnbHigh} MHz";
}
