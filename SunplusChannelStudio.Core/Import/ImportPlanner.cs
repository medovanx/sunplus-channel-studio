namespace SunplusChannelStudio.Core.Import;

public enum ChangeKind { NewTransponder, SymbolRate, NewChannel, Pids, Rename }

/// <summary>One proposed change. Nothing is touched until <see cref="ImportPlanner.Apply"/> runs the selected ones.</summary>
public sealed class ImportChange
{
    public required ChangeKind Kind { get; init; }
    public required string Name { get; init; }
    public string Before { get; init; } = "";
    public required string After { get; init; }
    public bool Selected { get; set; }

    /// <summary>True for channels the list marks as scrambled; a free-to-air receiver cannot show them.</summary>
    public bool Scrambled { get; init; }

    internal ImportedTransponder? Transponder { get; init; }
    internal ImportedService? Service { get; init; }
    internal Channel? Existing { get; init; }
    internal int TransponderIndex { get; init; } = -1;
}

/// <summary>
/// Compares an imported list with the open database for one satellite and proposes changes.
/// Channels are never deleted, and renames start unticked so the user's own names are kept. A new symbol rate
/// for a transponder that already carries channels also starts unticked, and one the source itself disagrees
/// on is not offered.
/// </summary>
public static class ImportPlanner
{
    /// <summary>Lists drift by a few MHz for the same transponder.</summary>
    const int FrequencyTolerance = 3;
    /// <summary>Smaller symbol rate differences are measurement noise in scanned lists.</summary>
    const int SymbolRateTolerance = 50;

    public static List<ImportChange> Plan(ChannelDatabase db, int satelliteIndex, ImportedList list)
    {
        var changes = new List<ImportChange>();
        var newTps = new List<ImportedTransponder>();

        var channels = db.TvChannels.Concat(db.RadioChannels).ToList();
        var inUse = channels.Select(c => (int)c.TransponderIndex).ToHashSet();

        foreach (var tp in Distinct(list.Transponders))
        {
            // A source that lists the same transponder with different rates (such as an old and a new
            // satellite file at one position) cannot say which rate is right.
            bool disputed = list.Transponders.Any(o => Same(o, tp) && Math.Abs(o.SymbolRate - tp.SymbolRate) > SymbolRateTolerance);
            int idx = FindTransponder(db, satelliteIndex, tp);
            if (idx < 0)
            {
                newTps.Add(tp);
                changes.Add(new ImportChange
                {
                    Kind = ChangeKind.NewTransponder, Name = Describe(tp), After = $"{tp.SymbolRate} kS/s",
                    Selected = !disputed, Transponder = tp,
                });
            }
            else if (!disputed && Math.Abs(db.Transponders[idx].SymbolRate - tp.SymbolRate) > SymbolRateTolerance)
            {
                // Channels on it mean the receiver already locks on at the stored rate, so the change is opt-in.
                changes.Add(new ImportChange
                {
                    Kind = ChangeKind.SymbolRate, Name = Describe(tp),
                    Before = $"{db.Transponders[idx].SymbolRate}", After = $"{tp.SymbolRate}",
                    Selected = !MatchingTransponderIndexes(db, satelliteIndex, tp).Any(inUse.Contains), Transponder = tp, TransponderIndex = idx,
                });
            }
        }

        var seen = new List<(ImportedTransponder Tp, ushort Sid)>(); // the source can list a service twice too
        foreach (var s in list.Services)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) continue;
            if (seen.Any(x => x.Sid == s.ServiceId && Same(x.Tp, s.Transponder))) continue;
            seen.Add((s.Transponder, s.ServiceId));
            if (s.Kind == ChannelKind.Tv && s.VideoPid == 0) continue; // data services, not watchable
            int idx = FindTransponder(db, satelliteIndex, s.Transponder);
            bool onNewTp = idx < 0 && newTps.Any(t => Same(t, s.Transponder));
            if (idx < 0 && !onNewTp) continue;

            // Lists often hold the same transponder more than once, so look on every matching one.
            var onTps = MatchingTransponders(db, satelliteIndex, s.Transponder);
            var existing = channels.FirstOrDefault(c => onTps.Contains(c.TransponderIndex) && c.ServiceId == s.ServiceId);
            if (existing is null)
            {
                changes.Add(new ImportChange
                {
                    Kind = ChangeKind.NewChannel, Name = s.Name, After = $"{Describe(s.Transponder)}, SID {s.ServiceId}",
                    Selected = !s.Scrambled, Scrambled = s.Scrambled, Service = s,
                });
                continue;
            }

            string Pids(ushort v, ushort a, ushort pcr) => $"V {v}, A {a}, PCR {pcr}";
            bool videoChanged = s.Kind == ChannelKind.Tv && s.VideoPid != 0 && (existing.VideoPid & 0x1FFF) != s.VideoPid;
            bool audioChanged = s.AudioPid != 0 && (existing.AudioPidA & 0x1FFF) != s.AudioPid;
            if (videoChanged || audioChanged)
            {
                changes.Add(new ImportChange
                {
                    Kind = ChangeKind.Pids, Name = existing.Name,
                    Before = Pids((ushort)(existing.VideoPid & 0x1FFF), (ushort)(existing.AudioPidA & 0x1FFF), (ushort)(existing.PcrPid & 0x1FFF)),
                    After = Pids(s.VideoPid, s.AudioPid, s.PcrPid),
                    Selected = true, Service = s, Existing = existing,
                });
            }
            if (!string.Equals(Simplify(existing.Name), Simplify(s.Name), StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new ImportChange
                {
                    Kind = ChangeKind.Rename, Name = existing.Name, Before = existing.Name, After = s.Name,
                    Selected = false, Service = s, Existing = existing,
                });
            }
        }
        return changes;
    }

    /// <summary>Applies the selected changes. Returns how many were applied.</summary>
    public static int Apply(ChannelDatabase db, int satelliteIndex, IEnumerable<ImportChange> changes)
    {
        var picked = changes.Where(c => c.Selected).ToList();
        int applied = 0;

        // Transponders first, so new channels can point at them.
        foreach (var c in picked.Where(c => c.Kind == ChangeKind.NewTransponder))
        {
            var template = db.Transponders.FirstOrDefault(t => t.SatelliteIndex == satelliteIndex) ?? db.Transponders.FirstOrDefault();
            var tp = template is null ? new Transponder(new byte[Transponder.Size]) : Transponder.CloneFrom(template);
            var src = c.Transponder!;
            tp.TrySetName($"{src.Frequency} {(src.Polarization == Polarity.Vertical ? "V" : "H")}");
            tp.Frequency = (ushort)src.Frequency;
            tp.SymbolRate = (ushort)src.SymbolRate;
            tp.Polarization = src.Polarization == Polarity.Vertical ? (byte)0 : (byte)1;
            tp.SatelliteIndex = (ushort)satelliteIndex;
            tp.Id = (ushort)(db.Transponders.Count == 0 ? 0 : db.Transponders.Max(t => t.Id) + 1);
            db.Transponders.Add(tp);
            applied++;
        }

        foreach (var c in picked)
        {
            switch (c.Kind)
            {
                case ChangeKind.SymbolRate:
                    db.Transponders[c.TransponderIndex].SymbolRate = (ushort)c.Transponder!.SymbolRate;
                    applied++;
                    break;

                case ChangeKind.Pids:
                    SetStreams(c.Existing!, c.Service!);
                    applied++;
                    break;

                case ChangeKind.Rename:
                    if (FitName(c.Existing!, c.Service!.Name)) applied++;
                    break;

                case ChangeKind.NewChannel:
                    var s = c.Service!;
                    int idx = FindTransponder(db, satelliteIndex, s.Transponder);
                    if (idx < 0) break; // its transponder was not imported
                    var list = s.Kind == ChannelKind.Radio ? db.RadioChannels : db.TvChannels;
                    Channel channel;
                    if (s.SourceRecord is { } raw)
                    {
                        // Merging another database: keep its whole record, only the transponder link changes.
                        channel = new Channel(raw.ToArray());
                        channel.Resize(db.Type.ChannelSize());
                        channel.SetService((ushort)idx, s.ServiceId, s.TransportStreamId, s.NetworkId);
                    }
                    else
                    {
                        channel = Channel.Create(s.Kind, db.Type.ChannelSize());
                        if (!FitName(channel, s.Name)) break;
                        channel.SetService((ushort)idx, s.ServiceId, s.TransportStreamId, s.NetworkId);
                        SetStreams(channel, s);
                        channel.Scrambled = s.Scrambled;
                    }
                    list.Add(channel);
                    applied++;
                    break;
            }
        }
        return applied;
    }

    static void SetStreams(Channel channel, ImportedService s) =>
        channel.SetStreams(s.VideoPid, s.VideoType, s.AudioPid, s.AudioType, s.AudioPidB, s.AudioTypeB,
            s.PcrPid != 0 ? s.PcrPid : s.VideoPid, s.PmtPid);

    /// <summary>Writes the name, shortening it or dropping characters the receiver cannot show when needed.</summary>
    static bool FitName(Channel channel, string name)
    {
        name = name.Trim();
        for (int len = Math.Min(name.Length, Channel.NameSize); len > 0; len--)
            if (channel.TrySetName(name[..len].TrimEnd())) return true;
        var plain = new string(name.Where(ch => ch is >= ' ' and <= 'ÿ' or >= '؀' and <= 'ۿ').ToArray()).Trim();
        for (int len = Math.Min(plain.Length, Channel.NameSize); len > 0; len--)
            if (channel.TrySetName(plain[..len].TrimEnd())) return true;
        return false;
    }

    static HashSet<int> MatchingTransponders(ChannelDatabase db, int satelliteIndex, ImportedTransponder tp) =>
        Enumerable.Range(0, db.Transponders.Count).Where(i => Matches(db.Transponders[i], satelliteIndex, tp)).ToHashSet();

    static bool Matches(Transponder t, int satelliteIndex, ImportedTransponder tp) =>
        t.SatelliteIndex == satelliteIndex && (t.Polarization == 0) == (tp.Polarization == Polarity.Vertical)
        && Math.Abs(t.Frequency - tp.Frequency) <= FrequencyTolerance;

    /// <summary>Every entry on the satellite within tolerance; receivers often keep near-duplicates of one transponder.</summary>
    static IEnumerable<int> MatchingTransponderIndexes(ChannelDatabase db, int satelliteIndex, ImportedTransponder tp)
    {
        for (int i = 0; i < db.Transponders.Count; i++)
        {
            var t = db.Transponders[i];
            if (t.SatelliteIndex == satelliteIndex && (t.Polarization == 0) == (tp.Polarization == Polarity.Vertical)
                && Math.Abs(t.Frequency - tp.Frequency) <= FrequencyTolerance)
                yield return i;
        }
    }

    static int FindTransponder(ChannelDatabase db, int satelliteIndex, ImportedTransponder tp)
    {
        int best = -1, bestDiff = int.MaxValue;
        for (int i = 0; i < db.Transponders.Count; i++)
        {
            var t = db.Transponders[i];
            if (t.SatelliteIndex != satelliteIndex) continue;
            if ((t.Polarization == 0) != (tp.Polarization == Polarity.Vertical)) continue;
            int diff = Math.Abs(t.Frequency - tp.Frequency);
            if (diff <= FrequencyTolerance && diff < bestDiff) (best, bestDiff) = (i, diff);
        }
        return best;
    }

    static bool Same(ImportedTransponder a, ImportedTransponder b) =>
        a.Polarization == b.Polarization && Math.Abs(a.Frequency - b.Frequency) <= FrequencyTolerance;

    static IEnumerable<ImportedTransponder> Distinct(IEnumerable<ImportedTransponder> tps)
    {
        var kept = new List<ImportedTransponder>();
        foreach (var tp in tps.OrderBy(t => t.Frequency))
            if (!kept.Any(k => Same(k, tp))) kept.Add(tp);
        return kept;
    }

    static string Describe(ImportedTransponder tp) => $"{tp.Frequency} {(tp.Polarization == Polarity.Vertical ? "V" : "H")}";

    static string Simplify(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());
}
