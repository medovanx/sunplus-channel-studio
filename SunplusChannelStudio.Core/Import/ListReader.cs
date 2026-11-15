using System.Globalization;
using System.Text;

namespace SunplusChannelStudio.Core.Import;

public enum ImportProblem { UnknownFormat, NoTransponders }

public sealed class ImportFormatException(ImportProblem problem, string message) : Exception(message)
{
    public ImportProblem Problem { get; } = problem;
}

/// <summary>
/// Reads channel and transponder lists from other software:
/// Enigma2 lamedb (version 4) and lamedb5 with channels and PIDs, the KingOfSat .ini download,
/// and dtv-scan-tables (dvbv5) transponder files.
/// </summary>
public static class ListReader
{
    public static ImportedList Read(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static ImportedList Parse(string text)
    {
        if (text.Contains("eDVB services /4/")) return Lamedb4(text);
        if (text.Contains("eDVB services /5/")) return Lamedb5(text);
        if (text.Contains("[DVB]", StringComparison.OrdinalIgnoreCase)) return KingOfSat(text);
        if (text.Contains("[CHANNEL]", StringComparison.OrdinalIgnoreCase)) return ScanTable(text);
        throw new ImportFormatException(ImportProblem.UnknownFormat, "Unknown list format.");
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- KingOfSat ----------

    // [SATTYPE] 1=3530 (position, 3600 minus west), [DVB] 1=10723,V,32114,12,S2,QPSK
    static ImportedList KingOfSat(string text)
    {
        var list = new ImportedList { Format = "KingOfSat" };
        int? position = null;
        string section = "";
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.ToUpperInvariant(); continue; }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq], value = line[(eq + 1)..];
            if (section == "[SATTYPE]" && key == "1" && int.TryParse(value, Inv, out int p)) position = FromEnigmaPosition(p);
            if (section != "[DVB]" || key == "0") continue;
            var f = value.Split(',');
            if (f.Length < 3 || !int.TryParse(f[0], Inv, out int freq) || !int.TryParse(f[2], Inv, out int sr)) continue;
            if (Pol(f[1]) is not { } pol) continue;
            list.Transponders.Add(new ImportedTransponder(freq, pol, sr, position));
        }
        return Checked(list);
    }

    // ---------- dtv-scan-tables (dvbv5) ----------

    static ImportedList ScanTable(string text)
    {
        var list = new ImportedList { Format = "dtv-scan-tables" };
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (fields.TryGetValue("FREQUENCY", out var f) && fields.TryGetValue("SYMBOL_RATE", out var s)
                && fields.TryGetValue("POLARIZATION", out var p) && Pol(p) is { } pol
                && long.TryParse(f, Inv, out long khz) && long.TryParse(s, Inv, out long sps))
                list.Transponders.Add(new ImportedTransponder((int)Math.Round(khz / 1000.0), pol, (int)Math.Round(sps / 1000.0)));
            fields.Clear();
        }
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[')) { Flush(); continue; }
            int eq = line.IndexOf('=');
            if (eq > 0) fields[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        Flush();
        return Checked(list);
    }

    // ---------- Enigma2 lamedb ----------

    // Transponder key "namespace:tsid:onid" (hex), satellite data "s freq:sr:pol:fec:position:...".
    sealed record LameTp(ImportedTransponder Tp, ushort Tsid, ushort Onid);

    static ImportedList Lamedb4(string text)
    {
        var lines = Lines(text).ToList();
        var tps = new Dictionary<string, LameTp>(StringComparer.OrdinalIgnoreCase);
        var list = new ImportedList { Format = "Enigma2 lamedb" };
        int i = lines.FindIndex(l => l.Trim() == "transponders") + 1;
        if (i == 0) throw new ImportFormatException(ImportProblem.NoTransponders, "The lamedb file has no transponder section.");
        for (; i < lines.Count && lines[i].Trim() != "end"; i++)
        {
            var key = lines[i].Trim();
            if (key.Length == 0 || key == "/") continue;
            var data = i + 1 < lines.Count ? lines[i + 1].Trim() : "";
            if (data.StartsWith("s ")) AddLameTp(tps, list, key, data[2..]);
            // skip to the closing "/" of this transponder
            while (i < lines.Count && lines[i].Trim() != "/") i++;
        }
        i = lines.FindIndex(i, l => l.Trim() == "services") + 1;
        if (i > 0)
        {
            for (; i + 2 < lines.Count && lines[i].Trim() != "end"; i += 3)
                AddLameService(tps, list, lines[i].Trim(), lines[i + 1], lines[i + 2].Trim());
        }
        return Checked(list);
    }

    // lamedb5: one item per line, "t:ns:tsid:onid,s:freq:..." and "s:sid:ns:tsid:onid:type:number,"Name",p:...".
    static ImportedList Lamedb5(string text)
    {
        var tps = new Dictionary<string, LameTp>(StringComparer.OrdinalIgnoreCase);
        var list = new ImportedList { Format = "Enigma2 lamedb5" };
        var services = new List<string>();
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("t:"))
            {
                int comma = line.IndexOf(',');
                if (comma > 0 && line[(comma + 1)..].StartsWith("s:")) AddLameTp(tps, list, line[2..comma], line[(comma + 3)..]);
            }
            else if (line.StartsWith("s:")) services.Add(line);
        }
        foreach (var line in services)
        {
            // s:<ref>,"<name>",<provider data>
            int q1 = line.IndexOf(",\"", StringComparison.Ordinal);
            if (q1 < 0) continue;
            int q2 = line.IndexOf('"', q1 + 2);
            if (q2 < 0) continue;
            var reference = line[2..q1];
            var name = line[(q1 + 2)..q2];
            var rest = q2 + 2 <= line.Length ? line[Math.Min(q2 + 2, line.Length)..] : "";
            AddLameService(tps, list, reference, name, rest);
        }
        return Checked(list);
    }

    static void AddLameTp(Dictionary<string, LameTp> tps, ImportedList list, string key, string data)
    {
        var k = key.Split(':');
        var f = data.Split(':');
        if (k.Length < 3 || f.Length < 5) return;
        if (!long.TryParse(f[0], Inv, out long khz) || !long.TryParse(f[1], Inv, out long sps)) return;
        if (!int.TryParse(f[2], Inv, out int pol) || pol > 1) return; // 0 horizontal, 1 vertical; circular is not used here
        if (!int.TryParse(f[4], Inv, out int position)) return;
        var tp = new ImportedTransponder((int)Math.Round(khz / 1000.0), pol == 0 ? Polarity.Horizontal : Polarity.Vertical,
            (int)Math.Round(sps / 1000.0), FromEnigmaPosition(position));
        tps[$"{Norm(k[0])}:{Norm(k[1])}:{Norm(k[2])}"] = new LameTp(tp, Hex(k[1]), Hex(k[2]));
        list.Transponders.Add(tp);
    }

    static void AddLameService(Dictionary<string, LameTp> tps, ImportedList list, string reference, string name, string data)
    {
        // sid:namespace:tsid:onid:type:number
        var r = reference.Split(':');
        if (r.Length < 5) return;
        if (!tps.TryGetValue($"{Norm(r[1])}:{Norm(r[2])}:{Norm(r[3])}", out var tp)) return;
        int type = int.TryParse(r[4], Inv, out int t) ? t : 1;
        // Enigma2 service types: 2 and 10 are radio, everything else here is TV.
        var kind = type is 2 or 10 ? ChannelKind.Radio : ChannelKind.Tv;

        var cache = new Dictionary<int, int>();
        foreach (var part in data.Split(','))
        {
            // c:TTVVVV, TT = cache id, VVVV = value, both hex
            if (part.StartsWith("c:") && part.Length >= 8
                && int.TryParse(part.AsSpan(2, 2), NumberStyles.HexNumber, Inv, out int id)
                && int.TryParse(part.AsSpan(4), NumberStyles.HexNumber, Inv, out int value))
                cache[id] = value;
        }
        // Enigma2 cache ids: 0 video, 1 MPEG audio, 3 PCR, 4 AC-3, 5 video codec, 12 AAC-HE, 14 AAC, 16 PMT.
        ushort Get(int id) => cache.TryGetValue(id, out int v) ? (ushort)(v & 0x1FFF) : (ushort)0;
        byte videoType = cache.GetValueOrDefault(5) switch { 1 => StreamType.H264, 2 => StreamType.Mpeg1Video, 7 => StreamType.Hevc, _ => StreamType.Mpeg2Video };

        var audio = new List<(ushort Pid, byte Type)>();
        if (Get(1) != 0) audio.Add((Get(1), StreamType.Mpeg1Audio));
        if (Get(4) != 0) audio.Add((Get(4), StreamType.Ac3));
        if (Get(14) != 0) audio.Add((Get(14), StreamType.Aac));
        if (Get(12) != 0) audio.Add((Get(12), StreamType.AacLatm));

        ushort video = kind == ChannelKind.Radio ? (ushort)0x1FFF : Get(0);
        list.Services.Add(new ImportedService(
            name.Trim(), kind, tp.Tp, Hex(r[0]), tp.Tsid, tp.Onid,
            video, kind == ChannelKind.Radio ? (byte)0 : videoType,
            audio.ElementAtOrDefault(0).Pid, audio.ElementAtOrDefault(0).Type,
            audio.ElementAtOrDefault(1).Pid, audio.ElementAtOrDefault(1).Type,
            Get(3) != 0 ? Get(3) : video, Get(16),
            // Upper-case "C:" entries are conditional access system ids: the channel is scrambled.
            Scrambled: data.Split(',').Any(part => part.StartsWith("C:", StringComparison.Ordinal))));
    }

    // ---------- Helpers ----------

    /// <summary>Enigma2 and KingOfSat write west as 3600 minus the angle (3530 is 7.0W) or as a negative number.</summary>
    static int FromEnigmaPosition(int p) => p > 1800 ? p - 3600 : p;

    static Polarity? Pol(string s) => s.Trim().ToUpperInvariant() switch
    {
        "V" or "VERTICAL" => Polarity.Vertical,
        "H" or "HORIZONTAL" => Polarity.Horizontal,
        _ => null,
    };

    static ushort Hex(string s) => ushort.TryParse(s, NumberStyles.HexNumber, Inv, out ushort v) ? v : (ushort)0;
    static string Norm(string s) => s.TrimStart('0') is { Length: > 0 } t ? t.ToLowerInvariant() : "0";
    static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    static ImportedList Checked(ImportedList list) =>
        list.Transponders.Count > 0 ? list : throw new ImportFormatException(ImportProblem.NoTransponders, "The file has no satellite transponders.");
}
