namespace SunplusChannelStudio.Core.Import;

/// <summary>
/// Builds a new receiver database from a transponder list, for users who have no database yet. Each
/// position becomes a satellite and gets its transponders through the import planner, so a source that
/// disagrees with itself is handled the same way as in an import. There are no channels: the receiver
/// finds them with a scan, or a lamedb import adds them.
/// </summary>
public static class DatabaseBuilder
{
    public static ChannelDatabase Create(ImportedList list, IReadOnlyList<int> positions, DatabaseType type)
    {
        var db = ChannelDatabase.Create(type);
        for (int i = 0; i < positions.Count; i++)
        {
            int position = positions[i];
            db.Satellites.Add(Satellite.Create(ShortName(list, position), position, first: i == 0));
            ImportPlanner.Apply(db, i, ImportPlanner.Plan(db, i, list.At(position)));
        }
        return db;
    }

    /// <summary>The name field holds 20 bytes; one is kept free for the end marker.</summary>
    const int NameRoom = Satellite.NameSize - 1;

    /// <summary>
    /// The satellite and the position, such as "Astra 1KR 19.2E". Where several satellites share a slot, the
    /// family most of them belong to names it ("Nilesat 7.0W" for Eutelsat 7 West A, Nilesat 201 and 301).
    /// The satellite part is shortened when needed so the position always fits.
    /// </summary>
    public static string ShortName(ImportedList list, int position)
    {
        var label = ImportedList.PositionLabel(position);
        var all = list.PositionNames.TryGetValue(position, out var names)
            ? names.Split(" (")[0].Split(", ", StringSplitOptions.RemoveEmptyEntries)
            : [];
        var families = all.GroupBy(n => n.Split(' ')[0], StringComparer.OrdinalIgnoreCase).ToList();
        var top = families.OrderByDescending(g => g.Count()).FirstOrDefault(); // ties keep the listed order
        string first = top is null ? "" : top.Count() > 1 ? top.Key : top.First();
        int room = NameRoom - label.Length - 1;
        if (first.Length > room) first = first[..Math.Max(room, 0)].TrimEnd();
        return first.Length > 0 ? $"{first} {label}" : label;
    }
}
