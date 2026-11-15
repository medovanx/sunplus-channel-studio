using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SunplusChannelStudio.Core.Streams;

namespace SunplusChannelStudio.Core;

public enum ChannelCategory { Religious, News, Sports, Kids, Movies, Drama, Music, Documentary, General }

public sealed class CategoryRulesException(string message) : Exception(message);

/// <summary>
/// Name rules for guessing a channel's category, read from a JSON rules file (see category-rules.json).
/// The program holds no channel names itself: the shipped file has defaults and users can extend their own copy.
/// </summary>
public sealed class CategoryRules
{
    readonly List<(ChannelCategory Category, Regex Pattern)> _rules = [];
    readonly Dictionary<string, ChannelCategory> _catalog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rules shipped with the program.</summary>
    public static CategoryRules Default { get; } = Parse(DefaultJson());

    public static string DefaultJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SunplusChannelStudio.Core.category-rules.json")
            ?? throw new InvalidOperationException("The built-in category rules are missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static CategoryRules Parse(string json)
    {
        var rules = new CategoryRules();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("rules", out var list))
            {
                foreach (var rule in list.EnumerateArray())
                {
                    var category = ParseCategory(rule.GetProperty("category").GetString());
                    var words = rule.GetProperty("words").EnumerateArray().Select(w => w.GetString()).OfType<string>()
                        .Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
                    if (words.Count > 0) rules._rules.Add((category, Pattern(words)));
                }
            }
            if (doc.RootElement.TryGetProperty("catalog", out var map))
                foreach (var p in map.EnumerateObject())
                    rules._catalog[p.Name] = ParseCategory(p.Value.GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CategoryRulesException(ex.Message);
        }
        return rules;
    }

    static ChannelCategory ParseCategory(string? name) =>
        Enum.TryParse<ChannelCategory>(name, ignoreCase: true, out var c)
            ? c
            : throw new CategoryRulesException($"Unknown category \"{name}\". Use one of: {string.Join(", ", Enum.GetNames<ChannelCategory>())}.");

    /// <summary>Whole words; a trailing * matches any ending, a number may follow, spaces match any spacing.</summary>
    static Regex Pattern(IEnumerable<string> words)
    {
        var parts = words.Select(w =>
        {
            bool wild = w.EndsWith('*');
            var core = Regex.Escape(wild ? w[..^1] : w).Replace("\\ ", "\\s*");
            return wild ? core + "\\w*" : core;
        });
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', parts)})(?![\p{{L}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The category the rules give a name, or null when no rule matches.</summary>
    public ChannelCategory? Match(string name)
    {
        var plain = StreamCatalog.Normalize(name);
        foreach (var (category, pattern) in _rules)
            if (pattern.IsMatch(plain) || pattern.IsMatch(name)) return category;
        return null;
    }

    /// <summary>The catalogue's first listed category, mapped to ours, or null.</summary>
    public ChannelCategory? FromCatalog(IReadOnlyList<string>? tags) =>
        tags?.FirstOrDefault(_catalog.ContainsKey) is { } t ? _catalog[t] : null;
}

/// <summary>
/// Decides a channel's category: the user's own choice first (remembered by the channel's service identity,
/// so it holds in any list), then the name rules, then the stream catalogue, else General.
/// </summary>
public sealed class Categorizer(CategoryRules rules, IReadOnlyDictionary<string, ChannelCategory>? choices = null, StreamCatalog? catalog = null)
{
    public CategoryRules Rules { get; } = rules;
    public StreamCatalog? Catalog { get; } = catalog;

    /// <summary>
    /// A channel's identity across lists: original network, transport stream and service ids, which a
    /// broadcaster keeps stable. Lists without network ids fall back to the name.
    /// </summary>
    public static string KeyOf(Channel c) =>
        c.NetworkId == 0 && c.TransportStreamId == 0
            ? "name:" + StreamCatalog.Normalize(c.Name)
            : $"{c.NetworkId}:{c.TransportStreamId}:{c.ServiceId}";

    public bool IsChosen(Channel c) => choices?.ContainsKey(KeyOf(c)) == true;

    public ChannelCategory Classify(Channel c) =>
        choices is not null && choices.TryGetValue(KeyOf(c), out var chosen) ? chosen : Guess(c.Name);

    public ChannelCategory Guess(string name) =>
        Rules.Match(name) ?? Rules.FromCatalog(Catalog?.CategoriesOf(name)) ?? ChannelCategory.General;
}

public static class Categories
{
    /// <summary>The shipped rules plus the catalogue, without user choices; mostly for tests and quick checks.</summary>
    public static ChannelCategory Classify(string name, StreamCatalog? catalog = null) =>
        new Categorizer(CategoryRules.Default, null, catalog).Guess(name);

    /// <summary>
    /// Stable sort by the given category order: items keep their current order inside a category,
    /// or go A to Z when <paramref name="alphabetical"/> is set. Categories missing from the order go last.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> name, Func<T, ChannelCategory> category,
        IReadOnlyList<ChannelCategory> order, bool alphabetical)
    {
        var ranks = order.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var keyed = items.Select((item, index) => (item, index, rank: ranks.GetValueOrDefault(category(item), int.MaxValue))).ToList();
        var sorted = alphabetical
            ? keyed.OrderBy(k => k.rank).ThenBy(k => name(k.item).Trim(), StringComparer.CurrentCultureIgnoreCase).ThenBy(k => k.index)
            : keyed.OrderBy(k => k.rank).ThenBy(k => k.index);
        return sorted.Select(k => k.item).ToList();
    }

    /// <summary>Convenience overload that classifies by name with the shipped rules.</summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> name, IReadOnlyList<ChannelCategory> order,
        bool alphabetical, StreamCatalog? catalog = null) =>
        Sort(items, name, item => Classify(name(item), catalog), order, alphabetical);
}
