using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;
using Xunit.Abstractions;

namespace SunplusChannelStudio.Tests;

public class MergeTests(ITestOutputHelper output)
{
    static ChannelDatabase Db() => ChannelDatabase.Parse(Samples.Database());

    [Fact]
    public void Merging_adds_only_new_channels_and_keeps_their_record()
    {
        var target = Db();
        var source = Db();
        var extra = new Channel(source.TvChannels[0].Raw.ToArray());
        extra.TrySetName("Extra");
        extra.SetService(0, 77, 1, 1);
        extra.Favorite = 0x04;
        extra.Scrambled = true;
        source.TvChannels.Add(extra);
        source.TvChannels.Add(new Channel(extra.Raw.ToArray())); // listed twice in the source

        var changes = ImportPlanner.Plan(target, 0, ImportedList.FromDatabase(source, "source"));
        var added = changes.Where(c => c.Kind == ChangeKind.NewChannel).ToList();
        Assert.Equal("Extra", Assert.Single(added).Name);
        Assert.DoesNotContain(changes, c => c.Kind is ChangeKind.Pids or ChangeKind.Rename);

        foreach (var c in changes) c.Selected = true;
        ImportPlanner.Apply(target, 0, changes);
        var merged = target.TvChannels.Single(c => c.Name == "Extra");
        Assert.Equal((0x04, true, (ushort)77), (merged.Favorite, merged.Scrambled, merged.ServiceId));

        // Merging the same file again finds nothing new.
        Assert.DoesNotContain(ImportPlanner.Plan(target, 0, ImportedList.FromDatabase(source, "source")),
            c => c.Kind == ChangeKind.NewChannel);
    }

    [Fact]
    public void Merging_converts_records_to_the_target_type()
    {
        var target = ChannelDatabase.Parse(Samples.Database(100));
        var source = Db();
        var extra = new Channel(source.TvChannels[0].Raw.ToArray());
        extra.TrySetName("Extra");
        extra.SetService(0, 77, 1, 1);
        source.TvChannels.Add(extra);
        var changes = ImportPlanner.Plan(target, 0, ImportedList.FromDatabase(source, "source"));
        ImportPlanner.Apply(target, 0, changes);
        var reloaded = ChannelDatabase.Parse(target.ToBytes());
        Assert.Equal(DatabaseType.MultistreamV2, reloaded.Type);
        Assert.Contains(reloaded.TvChannels, c => c.Name == "Extra");
    }

    /// <summary>Merges english2 into english when the real files are on this machine, and checks it is idempotent.</summary>
    [Fact]
    public void Real_merge_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("SCS_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "english_db.bin"))) return;
        var target = ChannelDatabase.Load(Path.Combine(dir, "english_db.bin"));
        var source = ChannelDatabase.Load(Path.Combine(dir, "english2_db.bin"));
        int before = target.TvChannels.Count + target.RadioChannels.Count;

        var changes = ImportPlanner.Plan(target, 0, ImportedList.FromDatabase(source, "english2"));
        foreach (var g in changes.GroupBy(c => c.Kind)) output.WriteLine($"{g.Key}: {g.Count()} ({g.Count(c => c.Selected)} ticked)");
        int applied = ImportPlanner.Apply(target, 0, changes);
        int after = target.TvChannels.Count + target.RadioChannels.Count;
        output.WriteLine($"channels {before} -> {after}, {applied} changes applied");

        var saved = ChannelDatabase.Parse(target.ToBytes());
        Assert.Equal(after, saved.TvChannels.Count + saved.RadioChannels.Count);
        Assert.DoesNotContain(ImportPlanner.Plan(saved, 0, ImportedList.FromDatabase(source, "english2")),
            c => c.Kind is ChangeKind.NewChannel or ChangeKind.NewTransponder && c.Selected);
    }
}
