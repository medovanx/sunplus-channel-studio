using System.Text.RegularExpressions;
using SunplusChannelStudio.Core.Streams;

namespace SunplusChannelStudio.Core;

public enum ChannelCategory { Religious, News, Sports, Kids, Movies, Drama, Music, Documentary, General }

/// <summary>
/// Puts channels into categories, from name keywords (English and Arabic) first and the iptv-org
/// catalogue's categories second, so the list can be grouped in an order the user picks.
/// </summary>
public static partial class Categories
{
    // Checked in this order, so "Disney Movies" is Kids and "Quran Radio" is Religious.
    static readonly (ChannelCategory Category, Regex Pattern)[] Rules =
    [
        (ChannelCategory.Religious, Words(@"quran|qoran|koran|kareem|sunnah?|sonnah?|islam\w*|iqraa?|majd|azhari?|rahma|huda|makkah|mecca|madinah?|hekma|resala|nas|fath|mustafa|zad|dawaa?h|nada|kawthar|قرآن|قران|سنه|سنة|اسلام|إسلام|مكه|مكة|المجد|الرحمه|الرحمة|اقرأ|إقرأ|اقرا|ازهري|أزهري|الرساله|الرسالة|الناس|الفتح|صفا|المصطفي|المصطفى|الحق|الكوثر|دعوه|دعوة|الزاد|الندي|الندى")),
        (ChannelCategory.Kids, Words(@"kids?|cartoon|toons?|spacetoon|toyor|baby|junior|majid|nick\w*|disney|jeem|tofola|cn arabia|براعم|اطفال|أطفال|طفوله|طفولة|كرتون|طيور|سبيستون|سبيس تون|ماجد|جيم")),
        (ChannelCategory.Sports, Words(@"sports?|bein|on ?time|ahly|zamalek|koora|kass|yas|heddaf|ssc|رياضه|رياضة|الرياضيه|الرياضية|الاهلي|الأهلي|الزمالك|كوره|كورة")),
        (ChannelCategory.Documentary, Words(@"documentary|discovery|nat ?geo|national geographic|history|h2|wathaeqya|animal|science|educ\w*|madras\w*|وثائقي|الوثائقيه|الوثائقية|ناشيونال|التعليميه|التعليمية|مدرستنا")),
        (ChannelCategory.News, Words(@"news|akhbar|jazeera|arabiya|hadath|sky|bbc|cnn|france ?24|rt|asharq|alhurra|hurra|extra|ghad|اخبار|أخبار|الاخباريه|الإخبارية|الجزيره|الجزيرة|العربيه|العربية|الحدث|الغد|الحره|الحرة|اكسترا")),
        (ChannelCategory.Movies, Words(@"cinema|movies?|films?|aflam|cima|action|bollywood|سينما|افلام|أفلام|سيما")),
        (ChannelCategory.Drama, Words(@"drama|series|mosalsalat|moslslat|hekayat|دراما|مسلسلات|حكايات")),
        (ChannelCategory.Music, Words(@"music|clip|mazzika|melody|tarab|songs|aghani|radio|fm|موسيقي|موسيقى|اغاني|أغاني|كليب|طرب|مزيكا|ميلودي|راديو|اذاعه|إذاعة")),
    ];

    static Regex Words(string alternatives) =>
        // Whole words, but a number may follow ("Al-Majd3", "Drama 2").
        new($@"(?<![\p{{L}}\p{{N}}])(?:{alternatives})(?![\p{{L}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>iptv-org category names mapped to ours; the channel's first listed category decides.</summary>
    static readonly Dictionary<string, ChannelCategory> CatalogMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = ChannelCategory.General, ["entertainment"] = ChannelCategory.General, ["family"] = ChannelCategory.General,
        ["lifestyle"] = ChannelCategory.General, ["culture"] = ChannelCategory.General, ["comedy"] = ChannelCategory.General,
        ["cooking"] = ChannelCategory.General, ["auto"] = ChannelCategory.General, ["shop"] = ChannelCategory.General,
        ["religious"] = ChannelCategory.Religious,
        ["news"] = ChannelCategory.News, ["business"] = ChannelCategory.News, ["weather"] = ChannelCategory.News,
        ["sports"] = ChannelCategory.Sports, ["outdoor"] = ChannelCategory.Sports,
        ["kids"] = ChannelCategory.Kids, ["animation"] = ChannelCategory.Kids,
        ["movies"] = ChannelCategory.Movies,
        ["series"] = ChannelCategory.Drama,
        ["music"] = ChannelCategory.Music,
        ["documentary"] = ChannelCategory.Documentary, ["science"] = ChannelCategory.Documentary, ["classic"] = ChannelCategory.Movies,
        ["education"] = ChannelCategory.Documentary, ["travel"] = ChannelCategory.Documentary,
    };

    public static ChannelCategory Classify(string name, StreamCatalog? catalog = null)
    {
        var plain = StreamCatalog.Normalize(name);
        foreach (var (category, pattern) in Rules)
            if (pattern.IsMatch(plain) || pattern.IsMatch(name)) return category;
        if (catalog?.CategoriesOf(name) is { } tags)
            foreach (var tag in tags)
                if (CatalogMap.TryGetValue(tag, out var c)) return c;
        return ChannelCategory.General;
    }

    /// <summary>
    /// Stable sort by the given category order: channels keep their current order inside a category,
    /// or go A to Z when <paramref name="alphabetical"/> is set. Categories missing from the order go last.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> name, IReadOnlyList<ChannelCategory> order,
        bool alphabetical, StreamCatalog? catalog = null)
    {
        int Rank(ChannelCategory c) { int i = order.ToList().IndexOf(c); return i < 0 ? int.MaxValue : i; }
        var keyed = items.Select((item, index) => (item, index, rank: Rank(Classify(name(item), catalog)))).ToList();
        var sorted = alphabetical
            ? keyed.OrderBy(k => k.rank).ThenBy(k => name(k.item).Trim(), StringComparer.CurrentCultureIgnoreCase).ThenBy(k => k.index)
            : keyed.OrderBy(k => k.rank).ThenBy(k => k.index);
        return sorted.Select(k => k.item).ToList();
    }
}
