using System.Text.RegularExpressions;

namespace SunplusChannelStudio.Core;

[Flags]
public enum CleanupReason
{
    None = 0,
    Scrambled = 1,
    Placeholder = 2,
    NoStreams = 4,
    Duplicate = 8,
}

public sealed record CleanupItem(Channel Channel, ChannelKind List, int Number, CleanupReason Reasons);

/// <summary>Finds channels a free-to-air viewer usually does not want. Nothing is removed until <see cref="Remove"/>.</summary>
public static partial class Cleanup
{
    /// <summary>
    /// Names that are not real channels: test, spare and feed services, and names made only of digits,
    /// dates or separators (scans of unnamed services).
    /// </summary>
    [GeneratedRegex(@"\b(test|tests|spare|feed|dummy)\b|^[\s\d\\/.\-_#:]*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderName();

    public static bool IsPlaceholder(string name) => PlaceholderName().IsMatch(name.Trim());

    public static List<CleanupItem> Find(ChannelDatabase db)
    {
        var items = new List<CleanupItem>();
        foreach (var (list, kind) in new[] { (db.TvChannels, ChannelKind.Tv), (db.RadioChannels, ChannelKind.Radio) })
        {
            var seen = new HashSet<(int, int, int, ushort)>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var reasons = CleanupReason.None;
                if (c.Scrambled) reasons |= CleanupReason.Scrambled;
                if (IsPlaceholder(c.Name)) reasons |= CleanupReason.Placeholder;
                if (c.HasNoStreams) reasons |= CleanupReason.NoStreams;
                // The same service twice: same transponder frequency and polarization, same service id.
                var tp = db.TransponderOf(c);
                var key = (tp?.SatelliteIndex ?? -1, tp?.Frequency ?? -1, tp?.Polarization ?? -1, c.ServiceId);
                if (!seen.Add(key)) reasons |= CleanupReason.Duplicate;
                if (reasons != CleanupReason.None) items.Add(new CleanupItem(c, kind, i + 1, reasons));
            }
        }
        return items;
    }

    /// <summary>Removes the given channels. Transponders and satellites are left as they are.</summary>
    public static int Remove(ChannelDatabase db, IEnumerable<Channel> channels)
    {
        var drop = channels.ToHashSet();
        return db.TvChannels.RemoveAll(drop.Contains) + db.RadioChannels.RemoveAll(drop.Contains);
    }
}
