using System.Globalization;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace SunplusChannelStudio.Core.Import;

/// <summary>
/// Transponder lists from the tvheadend dtv-scan-tables project on GitHub (GPL-2.0 data, read at run time,
/// not shipped). Files are named like "Nilesat-201-7.0W", one per satellite, several per orbital position.
/// </summary>
public static partial class ScanTableSource
{
    public const string ListUrl = "https://api.github.com/repos/tvheadend/dtv-scan-tables/contents/dvb-s?ref=tvheadend";
    public const string RawBase = "https://raw.githubusercontent.com/tvheadend/dtv-scan-tables/tvheadend/dvb-s/";

    [GeneratedRegex(@"-(\d+(?:\.\d)?)([EW])$")]
    private static partial Regex PositionSuffix();

    /// <summary>The position a file name ends with, in tenths of a degree, east positive; null if none.</summary>
    public static int? PositionOf(string fileName)
    {
        var m = PositionSuffix().Match(fileName);
        if (!m.Success) return null;
        int tenths = (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 10);
        return m.Groups[2].Value == "W" ? -tenths : tenths;
    }

    /// <summary>"Nilesat 201" from "Nilesat-201-7.0W".</summary>
    public static string SatelliteName(string fileName) => PositionSuffix().Replace(fileName, "").Replace('-', ' ');

    sealed record Entry(string Name, string Type);

    /// <summary>Every position with a list, west to east, with the satellites listed there.</summary>
    public static async Task<IReadOnlyList<(int Position, IReadOnlyList<string> Satellites)>> ListPositionsAsync(
        HttpClient http, CancellationToken cancel = default) =>
        (await http.GetFromJsonAsync<List<Entry>>(ListUrl, cancel) ?? [])
            .Where(e => e.Type == "file" && PositionOf(e.Name) is not null)
            .GroupBy(e => PositionOf(e.Name)!.Value)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(e => SatelliteName(e.Name)).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

    /// <summary>
    /// Downloads every file at the given positions and returns them as one list, tagged by position, so the
    /// import window can offer each position. Satellites sharing a position are merged.
    /// </summary>
    public static async Task<ImportedList> FetchAsync(HttpClient http, IEnumerable<int> positions, CancellationToken cancel = default)
    {
        var wanted = positions.ToHashSet();
        var files = (await http.GetFromJsonAsync<List<Entry>>(ListUrl, cancel) ?? [])
            .Where(e => e.Type == "file" && PositionOf(e.Name) is { } p && wanted.Contains(p))
            .Select(e => e.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) throw new ImportFormatException(ImportProblem.NoTransponders, "No list for this position.");

        var list = new ImportedList { Format = "dtv-scan-tables" };
        var names = new Dictionary<int, List<(string Name, int Count)>>();
        foreach (var file in files)
        {
            int position = PositionOf(file)!.Value;
            ImportedList part;
            try { part = ListReader.Parse(await http.GetStringAsync(RawBase + Uri.EscapeDataString(file), cancel)); }
            catch (ImportFormatException) { continue; } // an empty or odd file should not sink the rest
            list.Transponders.AddRange(part.Transponders.Select(t => t with { Position = position }));
            (names.TryGetValue(position, out var n) ? n : names[position] = []).Add((SatelliteName(file), part.Transponders.Count));
        }
        if (list.Transponders.Count == 0) throw new ImportFormatException(ImportProblem.NoTransponders, "The lists had no transponders.");
        // The satellite with the most transponders comes first; it is the one people know the position by.
        foreach (var (position, n) in names)
            list.PositionNames[position] = $"{string.Join(", ", n.OrderByDescending(x => x.Count).ThenBy(x => x.Name).Select(x => x.Name))} ({ImportedList.PositionLabel(position)})";
        return list;
    }

    /// <summary>KingOfSat's own download of a position's transponders, such as 7W, for the user's browser.</summary>
    public static string KingOfSatDownload(int position) =>
        $"https://en.kingofsat.net/dl.php?pos={(Math.Abs(position) / 10.0).ToString("0.#", CultureInfo.InvariantCulture)}{(position < 0 ? "W" : "E")}&fkhz=0";
}
