using System.Buffers.Binary;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio.Tests;

public class DatabaseTests
{
    [Theory]
    [InlineData(92, DatabaseType.NormalDkOtp)]
    [InlineData(96, DatabaseType.MultistreamV1)]
    [InlineData(100, DatabaseType.MultistreamV2)]
    public void Detects_type_and_round_trips_byte_for_byte(int size, DatabaseType expected)
    {
        var data = Samples.Database(size);
        var db = ChannelDatabase.Parse(data);
        Assert.Equal(expected, db.Type);
        Assert.Equal(data, db.ToBytes());
    }

    [Fact]
    public void Reads_fields()
    {
        var db = ChannelDatabase.Parse(Samples.Database());
        Assert.Equal("Nilesat", db.Satellites[0].Name);
        Assert.Equal(70u, db.Satellites[0].Position);
        Assert.Equal(11766, db.Transponders[0].Frequency);
        Assert.Equal(1, db.Transponders[0].Polarization);
        Assert.Equal(["One", "Two"], db.TvChannels.Select(c => c.Name));
        Assert.Equal(ChannelKind.Radio, db.RadioChannels[0].Kind);
        Assert.Equal(20, db.TvChannels[1].ServiceId);
    }

    [Fact]
    public void Converting_types_keeps_the_shared_bytes()
    {
        var original = Samples.Database();
        var db = ChannelDatabase.Parse(original);
        db.Type = DatabaseType.MultistreamV2;
        var wide = ChannelDatabase.Parse(db.ToBytes());
        Assert.Equal(DatabaseType.MultistreamV2, wide.Type);
        wide.Type = DatabaseType.NormalDkOtp;
        Assert.Equal(original, wide.ToBytes());
    }

    [Fact]
    public void Header_counts_follow_edits()
    {
        var db = ChannelDatabase.Parse(Samples.Database());
        db.TvChannels.RemoveAt(0);
        var saved = ChannelDatabase.Parse(db.ToBytes());
        Assert.Equal(["Two"], saved.TvChannels.Select(c => c.Name));
        Assert.Single(saved.RadioChannels);
    }

    [Fact]
    public void Rejects_files_that_are_not_databases()
    {
        Assert.Throws<DatabaseFormatException>(() => ChannelDatabase.Parse("not a database at all"u8.ToArray()));
        var truncated = Samples.Database()[..^5];
        Assert.Throws<DatabaseFormatException>(() => ChannelDatabase.Parse(truncated));
    }

    [Fact]
    public void Arabic_names_use_the_iso_8859_6_selector()
    {
        var db = ChannelDatabase.Parse(Samples.Database());
        var channel = db.TvChannels[0];
        Assert.True(channel.TrySetName("نايل سات"));
        Assert.Equal(0x02, channel.Raw[2]);
        Assert.Equal("نايل سات", channel.Name);
    }

    [Fact]
    public void Renaming_keeps_an_existing_selector()
    {
        var db = ChannelDatabase.Parse(Samples.Database());
        var channel = db.TvChannels[0];
        channel.Raw[2] = 0x03; // Greek table selector in front of Latin text, as some real lists have
        "Nessma"u8.CopyTo(channel.Raw.AsSpan(3));
        Assert.True(channel.TrySetName("Nessma TV"));
        Assert.Equal(0x03, channel.Raw[2]);
        Assert.Equal("Nessma TV", channel.Name);
    }

    [Fact]
    public void Names_that_do_not_fit_are_refused_untouched()
    {
        var db = ChannelDatabase.Parse(Samples.Database());
        var channel = db.TvChannels[0];
        var before = channel.Raw.ToArray();
        Assert.False(channel.TrySetName(new string('x', Channel.NameSize)));
        Assert.False(channel.TrySetName("中文"));
        Assert.Equal(before, channel.Raw);
    }

    /// <summary>
    /// Round trips real receiver databases when SCS_SAMPLES points at a folder of *_db.bin files.
    /// Real channel lists are not kept in the repository.
    /// </summary>
    [Fact]
    public void Real_databases_round_trip_when_provided()
    {
        var dir = Environment.GetEnvironmentVariable("SCS_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*_db.bin");
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var original = File.ReadAllBytes(path);
            var db = ChannelDatabase.Parse(original);
            Assert.Equal(original, db.ToBytes());
            foreach (var type in Enum.GetValues<DatabaseType>())
            {
                var copy = ChannelDatabase.Parse(original);
                copy.Type = type;
                var back = ChannelDatabase.Parse(copy.ToBytes());
                back.Type = db.Type;
                Assert.Equal(original, back.ToBytes());
            }
            foreach (var c in db.TvChannels.Concat(db.RadioChannels))
            {
                var before = c.Raw.ToArray();
                Assert.True(c.TrySetName(c.Name), $"{Path.GetFileName(path)}: {c.Name}");
                int len = Array.IndexOf(before, (byte)0, 2) - 2;
                if (len < 0) len = Channel.NameSize;
                Assert.Equal(before.AsSpan(2, len).ToArray(), c.Raw.AsSpan(2, len).ToArray());
            }
        }
    }
}
