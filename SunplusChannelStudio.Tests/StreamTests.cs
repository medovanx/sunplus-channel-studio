using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Streams;
using Xunit.Abstractions;

namespace SunplusChannelStudio.Tests;

public class StreamTests(ITestOutputHelper output)
{
    const string Channels = """
        [
          {"id":"AlJazeera.qa","name":"Al Jazeera","alt_names":["الجزيرة"],"country":"QA"},
          {"id":"Iqraa.sa","name":"Iqraa TV","alt_names":["قناة إقرأ"],"country":"SA"},
          {"id":"NoStream.eg","name":"No Stream","alt_names":[],"country":"EG"},
          {"id":"Pirate.xx","name":"Pirate","alt_names":[],"country":"XX"}
        ]
        """;

    const string Streams = """
        [
          {"channel":"AlJazeera.qa","title":"Al Jazeera","url":"https://live.example.com/aj/720.m3u8","quality":"720p","labels":[]},
          {"channel":"AlJazeera.qa","title":"Al Jazeera","url":"https://live.example.com/aj/1080.m3u8","quality":"1080p","labels":[]},
          {"channel":"AlJazeera.qa","title":"Al Jazeera","url":"https://cdn.example.com/aj.m3u8","quality":"1080p","labels":["Geo-blocked"]},
          {"channel":"AlJazeera.qa","title":"Al Jazeera","url":"http://185.9.2.18/aj.m3u8","quality":"1080p","labels":[]},
          {"channel":"AlJazeera.qa","title":"Al Jazeera","url":"https://x.dens.tv/aj.m3u8","quality":"1080p","labels":[]},
          {"channel":"Iqraa.sa","title":"Iqraa","url":"https://iqraa.example.com/live.m3u8","user_agent":"Agent/1","referrer":"https://iqraa.example.com/","labels":[]},
          {"channel":"Pirate.xx","title":"Pirate","url":"https://pirate.example.com/live.m3u8","labels":[]}
        ]
        """;

    const string Blocklist = """[{"channel":"Pirate.xx","reason":"dmca"}]""";

    static StreamCatalog Catalog() => StreamCatalog.Parse(Channels, Streams, Blocklist);

    [Theory]
    [InlineData("Al Jazeera", "al jazeera")]
    [InlineData("AL JAZEERA HD", "al jazeera")]
    [InlineData("قنـــاة ~ إقـرأ", "اقرا")]
    [InlineData("الجزيرة", "الجزيره")]
    public void Normalizes_names(string name, string key) => Assert.Equal(key, StreamCatalog.Normalize(name));

    [Fact]
    public void Lists_every_channel_with_a_stream_for_browsing()
    {
        var c = Catalog();
        // No Stream has nothing to play and Pirate is on the blocklist; the rest come A to Z with their country.
        Assert.Equal([("Al Jazeera", "QA"), ("Iqraa TV", "SA")], c.Channels.Select(x => (x.Name, x.Country)));
        var aj = c.StreamsOf(c.Channels[0]);
        Assert.Equal(c.Find("Al Jazeera").Select(s => s.Url), aj.Select(s => s.Url)); // same best-first order as Find
    }

    [Fact]
    public void Matches_english_and_arabic_names()
    {
        var c = Catalog();
        Assert.NotEmpty(c.Find("AL JAZEERA HD"));
        Assert.NotEmpty(c.Find("الجزيرة"));
        Assert.NotEmpty(c.Find("قنـــاة ~ إقـرأ"));
        Assert.Empty(c.Find("Unknown Channel"));
        Assert.Empty(c.Find("No Stream"));
    }

    [Fact]
    public void Orders_best_first_and_drops_unsafe_sources()
    {
        var found = Catalog().Find("Al Jazeera");
        Assert.Equal(["https://live.example.com/aj/1080.m3u8", "https://live.example.com/aj/720.m3u8", "https://cdn.example.com/aj.m3u8"],
            found.Select(s => s.Url));
        Assert.DoesNotContain(found, s => s.Host.Contains("dens.tv") || s.Host.StartsWith("185."));
    }

    [Fact]
    public void Keeps_stream_headers_and_honours_the_blocklist()
    {
        var c = Catalog();
        var iqraa = c.Find("Iqraa").Single();
        Assert.Equal(("Agent/1", "https://iqraa.example.com/"), (iqraa.UserAgent, iqraa.Referrer));
        Assert.Empty(c.Find("Pirate"));
    }

    /// <summary>Reports how many channels of the real databases find a stream, when both are on this machine.</summary>
    [Fact]
    public void Real_catalogue_coverage_when_available()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunplusChannelStudio", "iptv");
        var samples = Environment.GetEnvironmentVariable("SCS_SAMPLES");
        if (!File.Exists(Path.Combine(cache, "streams.json")) || string.IsNullOrEmpty(samples) || !Directory.Exists(samples)) return;
        var catalog = StreamCatalog.Parse(File.ReadAllText(Path.Combine(cache, "channels.json")),
            File.ReadAllText(Path.Combine(cache, "streams.json")), File.ReadAllText(Path.Combine(cache, "blocklist.json")));
        output.WriteLine($"catalogue: {catalog.ChannelCount} channels with {catalog.StreamCount} usable streams");
        foreach (var path in Directory.GetFiles(samples, "*_db.bin"))
        {
            var db = ChannelDatabase.Load(path);
            var tv = db.TvChannels.Where(c => !c.Scrambled).ToList();
            var hits = tv.Where(c => catalog.Find(c.Name).Count > 0).ToList();
            output.WriteLine($"{Path.GetFileName(path)}: {hits.Count} of {tv.Count} free TV channels found");
            output.WriteLine("  e.g. " + string.Join(", ", hits.Take(25).Select(c => c.Name)));
        }
    }
}
