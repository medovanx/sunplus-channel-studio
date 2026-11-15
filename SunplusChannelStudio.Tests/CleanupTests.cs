using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;

namespace SunplusChannelStudio.Tests;

public class CleanupTests
{
    static ChannelDatabase Db() => ChannelDatabase.Parse(Samples.Database());

    [Fact]
    public void Setting_a_favourite_keeps_the_scrambled_flag()
    {
        var c = Db().TvChannels[0];
        c.Scrambled = true;
        c.Favorite = 0x04;
        Assert.True(c.Scrambled);
        Assert.Equal(0x04, c.Favorite);
        Assert.Equal(0x10, c.Raw[53]);
    }

    [Theory]
    [InlineData("Test 5661", true)]
    [InlineData("Spare 1702", true)]
    [InlineData("Majd Feed", true)]
    [InlineData("1 \\ 12 \\ 2023", true)]
    [InlineData("12345", true)]
    [InlineData("", true)]
    [InlineData("Al Jazeera", false)]
    [InlineData("Testament TV", false)]
    [InlineData("MBC 2", false)]
    [InlineData("قناة الناس", false)]
    public void Recognises_placeholder_names(string name, bool expected) =>
        Assert.Equal(expected, Cleanup.IsPlaceholder(name));

    [Fact]
    public void Finds_each_kind_and_removes_only_what_is_asked()
    {
        var db = Db();
        db.TvChannels[0].Scrambled = true;                      // "One"
        db.TvChannels[1].TrySetName("Test 1");                  // "Two"
        var copy = new Channel(db.TvChannels[1].Raw.ToArray()); // same transponder and SID as "Two"
        copy.TrySetName("Two again");
        db.TvChannels.Add(copy);
        db.RadioChannels[0].SetStreams(0x1FFF, 0, 0x100, StreamType.Mpeg1Audio, 0, 0, 0x1FFF, 0); // a real station
        var silent = Channel.Create(ChannelKind.Radio, 92);
        silent.TrySetName("Silent");
        db.RadioChannels.Add(silent);

        var items = Cleanup.Find(db);
        CleanupReason ReasonsOf(string name) => items.Single(i => i.Channel.Name == name).Reasons;
        Assert.Equal(CleanupReason.Scrambled, ReasonsOf("One"));
        Assert.Equal(CleanupReason.Placeholder, ReasonsOf("Test 1"));
        Assert.Equal(CleanupReason.Duplicate, ReasonsOf("Two again"));
        Assert.True(ReasonsOf("Silent").HasFlag(CleanupReason.NoStreams));
        Assert.DoesNotContain(items, i => i.Channel.Name == "Radio");

        int removed = Cleanup.Remove(db, items.Where(i => i.Reasons == CleanupReason.Scrambled).Select(i => i.Channel));
        Assert.Equal(1, removed);
        Assert.DoesNotContain(db.TvChannels, c => c.Name == "One");
        Assert.Contains(db.TvChannels, c => c.Name == "Test 1");
    }

    [Fact]
    public void Imported_pay_channels_are_marked_scrambled()
    {
        var db = Db();
        var list = ListReader.Parse("""
            eDVB services /4/
            transponders
            0dca0000:0002:0001
            	s 11766000:27500000:0:3:3530:2:0:1:2:0:2
            /
            end
            services
            0064:0dca0000:0002:0001:1:0
            Pay Channel
            p:Nilesat,c:000300,c:010301,C:0500
            end
            """);
        var changes = ImportPlanner.Plan(db, 0, list);
        foreach (var c in changes) c.Selected = true;
        ImportPlanner.Apply(db, 0, changes);
        Assert.True(db.TvChannels.Single(c => c.Name == "Pay Channel").Scrambled);
    }
}
