using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SunplusChannelStudio.Core.Streams;

/// <summary>A public live stream for a channel, with the headers its host expects.</summary>
public sealed record LiveStream(string Url, string Title, string? Quality, string? UserAgent, string? Referrer, IReadOnlyList<string> Labels)
{
    public bool GeoBlocked => Labels.Contains("Geo-blocked");
    public bool NotAlwaysOn => Labels.Contains("Not 24/7");
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : "";
}

/// <summary>A catalogue channel that has at least one stream: its iptv-org id, name, country code and categories.</summary>
public sealed record CatalogChannel(string Id, string Name, string? Country, IReadOnlyList<string> Categories);

/// <summary>
/// Matches channel names to public live streams from the iptv-org catalogue
/// (https://github.com/iptv-org/iptv, public domain data). The app only links to streams;
/// it does not host or record anything.
/// </summary>
public sealed class StreamCatalog
{
    public const string ChannelsUrl = "https://iptv-org.github.io/api/channels.json";
    public const string StreamsUrl = "https://iptv-org.github.io/api/streams.json";
    public const string BlocklistUrl = "https://iptv-org.github.io/api/blocklist.json";

    /// <summary>
    /// Hosts that re-stream other broadcasters' channels rather than serve their own, so they are never offered.
    /// </summary>
    static readonly string[] BlockedHosts = ["dens.tv"];

    readonly Dictionary<string, List<string>> _idsByName = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<LiveStream>> _streamsById = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string[]> _categoriesByName = new(StringComparer.Ordinal);
    readonly List<CatalogChannel> _channels = [];

    /// <summary>Every channel with a stream, A to Z, for browsing without a channel database.</summary>
    public IReadOnlyList<CatalogChannel> Channels => _channels;

    public int ChannelCount { get; private set; }
    public int StreamCount { get; private set; }

    /// <summary>Builds the catalogue from the three iptv-org JSON files.</summary>
    public static StreamCatalog Parse(string channelsJson, string streamsJson, string? blocklistJson = null)
    {
        var catalog = new StreamCatalog();
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (blocklistJson is not null)
        {
            using var doc = JsonDocument.Parse(blocklistJson);
            foreach (var e in doc.RootElement.EnumerateArray())
                if (Str(e, "channel") is { } id) blocked.Add(id);
        }

        using (var doc = JsonDocument.Parse(streamsJson))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (Str(e, "channel") is not { } id || blocked.Contains(id)) continue;
                if (Str(e, "url") is not { } url || !Acceptable(url)) continue;
                var labels = e.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                    ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                var stream = new LiveStream(url, Str(e, "title") ?? id, Str(e, "quality"), Str(e, "user_agent"), Str(e, "referrer"), labels);
                if (!catalog._streamsById.TryGetValue(id, out var list)) catalog._streamsById[id] = list = [];
                list.Add(stream);
                catalog.StreamCount++;
            }
        }

        using (var doc = JsonDocument.Parse(channelsJson))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (Str(e, "id") is not { } id || blocked.Contains(id)) continue;
                var names = new List<string>();
                if (Str(e, "name") is { } name) names.Add(name);
                if (e.TryGetProperty("alt_names", out var alts) && alts.ValueKind == JsonValueKind.Array)
                    names.AddRange(alts.EnumerateArray().Select(a => a.GetString()).OfType<string>());

                // Categories are kept for every channel, streams or not, for sorting by category.
                if (e.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                {
                    var tags = cats.EnumerateArray().Select(c => c.GetString()).OfType<string>().ToArray();
                    if (tags.Length > 0)
                        foreach (var key in names.Select(Normalize).Where(k => k.Length > 1))
                            catalog._categoriesByName.TryAdd(key, tags);
                }

                if (!catalog._streamsById.ContainsKey(id)) continue;
                catalog.ChannelCount++;
                if (names.Count > 0)
                    catalog._channels.Add(new CatalogChannel(id, names[0], Str(e, "country"),
                        e.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array
                            ? c.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                            : []));
                foreach (var key in names.Select(Normalize).Where(k => k.Length > 1).Distinct())
                {
                    if (!catalog._idsByName.TryGetValue(key, out var ids)) catalog._idsByName[key] = ids = [];
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
        }
        catalog._channels.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return catalog;
    }

    /// <summary>A catalogue channel's streams, best first.</summary>
    public IReadOnlyList<LiveStream> StreamsOf(CatalogChannel channel) =>
        _streamsById.TryGetValue(channel.Id, out var list) ? Best(list) : [];

    /// <summary>
    /// Streams for a channel name, best first: always-on before part-time, not geo-blocked first, higher quality first.
    /// Empty when nothing matches.
    /// </summary>
    public IReadOnlyList<LiveStream> Find(string channelName)
    {
        var key = Normalize(channelName);
        if (key.Length < 2 || !_idsByName.TryGetValue(key, out var ids)) return [];
        return Best(ids.SelectMany(id => _streamsById[id]));
    }

    /// <summary>Always-on before part-time, not geo-blocked first, higher quality first.</summary>
    static List<LiveStream> Best(IEnumerable<LiveStream> streams) =>
        streams.OrderBy(s => s.NotAlwaysOn)
            .ThenBy(s => s.GeoBlocked)
            .ThenByDescending(s => QualityRank(s.Quality))
            .ToList();

    /// <summary>The catalogue's categories for a channel name (such as "news" or "religious"), or null.</summary>
    public IReadOnlyList<string>? CategoriesOf(string channelName) =>
        _categoriesByName.TryGetValue(Normalize(channelName), out var tags) ? tags : null;

    /// <summary>
    /// Folds a channel name to a matching key: case, Arabic letter variants, tatweel, diacritics,
    /// punctuation and words like "HD", "TV" or "قناة" are dropped.
    /// </summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || ch == 'ـ') continue; // diacritics, tatweel
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                _ => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ',
            });
        }
        var noise = new HashSet<string> { "hd", "sd", "fhd", "uhd", "4k", "tv", "channel", "قناه", "قنوات", "تلفزيون", "the", "ال" };
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !noise.Contains(w));
        return string.Join(' ', words);
    }

    /// <summary>Raw-IP hosts and known re-streamers are left out; only http and https are played.</summary>
    static bool Acceptable(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        if (IPAddress.TryParse(u.Host, out _)) return false;
        return !BlockedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    static int QualityRank(string? q) => q is null ? 0 : int.TryParse(new string(q.TakeWhile(char.IsDigit).ToArray()), out int p) ? p : 0;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
