using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Streams;
using Xunit.Abstractions;

namespace SunplusChannelStudio.Tests;

public class CategoryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("QURAN TV", ChannelCategory.Religious)]
    [InlineData("قنـــاة ~ إقـرأ", ChannelCategory.Religious)]
    [InlineData("Al Jazeera HD", ChannelCategory.News)]
    [InlineData("العربية", ChannelCategory.News)]
    [InlineData("beIN SPORTS 1", ChannelCategory.Sports)]
    [InlineData("الأهلي", ChannelCategory.Sports)]
    [InlineData("SPACETOON ARABIC", ChannelCategory.Kids)]
    [InlineData("Disney Junior HD", ChannelCategory.Kids)]
    [InlineData("Rotana Cinema KSA", ChannelCategory.Movies)]
    [InlineData("MBC Drama", ChannelCategory.Drama)]
    [InlineData("Rotana Clip", ChannelCategory.Music)]
    [InlineData("Nat Geo HD", ChannelCategory.Documentary)]
    [InlineData("MBC 1", ChannelCategory.General)]
    [InlineData("Testament TV", ChannelCategory.General)]
    public void Classifies_by_name(string name, ChannelCategory expected) =>
        Assert.Equal(expected, Categories.Classify(name));

    [Fact]
    public void Falls_back_to_the_catalogue_category()
    {
        var catalog = StreamCatalog.Parse(
            """[{"id":"Mbc1.ae","name":"MBC 1","alt_names":[],"categories":["general"]},{"id":"Cbc.eg","name":"CBC","alt_names":[],"categories":["entertainment","news"]},{"id":"Ten.eg","name":"Ten","alt_names":[],"categories":["news"]}]""",
            "[]");
        // The channel's first listed category decides: CBC is an entertainment channel that also carries news.
        Assert.Equal(ChannelCategory.General, Categories.Classify("CBC", catalog));
        Assert.Equal(ChannelCategory.News, Categories.Classify("Ten", catalog));
        Assert.Equal(ChannelCategory.General, Categories.Classify("MBC 1", catalog));
    }

    [Fact]
    public void Sorts_by_the_chosen_order_and_keeps_order_inside_a_category()
    {
        string[] names = ["MBC 1", "Al Jazeera", "QURAN TV", "beIN SPORTS 2", "Sky News", "Iqraa", "beIN SPORTS 1"];
        ChannelCategory[] order = [ChannelCategory.News, ChannelCategory.Religious, ChannelCategory.Sports];
        Assert.Equal(["Al Jazeera", "Sky News", "QURAN TV", "Iqraa", "beIN SPORTS 2", "beIN SPORTS 1", "MBC 1"],
            Categories.Sort(names, n => n, order, alphabetical: false));
        Assert.Equal(["Al Jazeera", "Sky News", "Iqraa", "QURAN TV", "beIN SPORTS 1", "beIN SPORTS 2", "MBC 1"],
            Categories.Sort(names, n => n, order, alphabetical: true));
    }

    /// <summary>Shows how the real lists are categorised, when they and the cached catalogue are on this machine.</summary>
    [Fact]
    public void Real_lists_by_category_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("SCS_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunplusChannelStudio", "iptv");
        StreamCatalog? catalog = File.Exists(Path.Combine(cache, "channels.json"))
            ? StreamCatalog.Parse(File.ReadAllText(Path.Combine(cache, "channels.json")), File.ReadAllText(Path.Combine(cache, "streams.json")))
            : null;
        foreach (var path in Directory.GetFiles(dir, "*_db.bin"))
        {
            var tv = ChannelDatabase.Load(path).TvChannels;
            output.WriteLine($"== {Path.GetFileName(path)} ({tv.Count} TV)");
            foreach (var g in tv.GroupBy(c => Categories.Classify(c.Name, catalog)).OrderBy(g => g.Key))
                output.WriteLine($"  {g.Key,-12} {g.Count(),4}  {string.Join(", ", g.Take(9).Select(c => c.Name.Trim()))}");
        }
    }
}
