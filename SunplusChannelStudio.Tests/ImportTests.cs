using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;

namespace SunplusChannelStudio.Tests;

public class ImportTests
{
    // The sample database has one transponder (11766 H, 27500) on satellite 0 with channels
    // "One" (SID 10, video 1302 with a flag bit), "Two" (SID 20) and the radio "Radio" (SID 30).

    const string Lamedb4 = """
        eDVB services /4/
        transponders
        0dca0000:0002:0001
        	s 11766000:27500000:0:3:3530:2:0:1:2:0:2
        /
        0dca0000:0003:0001
        	s 12034000:27500000:0:3:3530:2:0:1:2:0:2
        /
        end
        services
        000a:0dca0000:0002:0001:1:0
        One
        p:Nilesat,c:000600,c:010601,c:030600,c:050001,c:100100
        0014:0dca0000:0002:0001:1:0
        Two HD
        p:Nilesat,c:000016,c:010017,c:030016
        0063:0dca0000:0003:0001:1:0
        Brand New
        p:Nilesat,c:000200,c:040201,c:030200,c:050001
        0064:0dca0000:0003:0001:1:0
        Pay Channel
        p:Nilesat,c:000300,c:010301,C:0500
        end
        """;

    const string KingOfSatIni = """
        [SATTYPE]
        1=3530
        2=7W
        [DVB]
        0=2
        1=11766,H,30000,56,S2,8PSK
        2=10723,V,32114,12,S2,QPSK
        """;

    const string ScanTable = """
        # Source: example
        [CHANNEL]
        	DELIVERY_SYSTEM = DVBS2
        	FREQUENCY = 10723600
        	POLARIZATION = VERTICAL
        	SYMBOL_RATE = 32114000
        """;

    static ChannelDatabase Db() => ChannelDatabase.Parse(Samples.Database());

    [Fact]
    public void Reads_lamedb4_transponders_and_channels()
    {
        var list = ListReader.Parse(Lamedb4);
        Assert.Equal(2, list.Transponders.Count);
        Assert.Equal([-70], list.Positions);
        var one = list.Services.Single(s => s.Name == "One");
        Assert.Equal((ushort)10, one.ServiceId);
        Assert.Equal((ushort)0x600, one.VideoPid);
        Assert.Equal(StreamType.H264, one.VideoType);
        Assert.Equal((ushort)0x601, one.AudioPid);
        Assert.Equal((ushort)0x100, one.PmtPid);
        Assert.True(list.Services.Single(s => s.Name == "Pay Channel").Scrambled);
        Assert.Equal(StreamType.Ac3, list.Services.Single(s => s.Name == "Brand New").AudioType);
    }

    [Fact]
    public void Reads_lamedb5()
    {
        var list = ListReader.Parse("""
            eDVB services /5/
            t:0dca0000:0002:0001,s:11766000:27500000:0:3:3530:2:0:1:2:0:2
            s:000a:0dca0000:0002:0001:1:0:0,"One",p:Nilesat,c:000600,c:010601
            """);
        Assert.Single(list.Transponders);
        Assert.Equal("One", list.Services.Single().Name);
        Assert.Equal((ushort)0x600, list.Services.Single().VideoPid);
    }

    [Fact]
    public void Reads_kingofsat_and_scan_tables()
    {
        var kos = ListReader.Parse(KingOfSatIni);
        Assert.Equal(2, kos.Transponders.Count);
        Assert.Equal(new ImportedTransponder(11766, Polarity.Horizontal, 30000, -70), kos.Transponders[0]);

        var dvb = ListReader.Parse(ScanTable);
        Assert.Equal(new ImportedTransponder(10724, Polarity.Vertical, 32114), dvb.Transponders.Single());
    }

    [Fact]
    public void Unknown_files_are_refused()
    {
        Assert.Throws<ImportFormatException>(() => ListReader.Parse("hello"));
    }

    [Fact]
    public void Plans_transponder_changes()
    {
        var changes = ImportPlanner.Plan(Db(), 0, ListReader.Parse(KingOfSatIni));
        Assert.Contains(changes, c => c.Kind == ChangeKind.SymbolRate && c.Before == "27500" && c.After == "30000");
        Assert.Contains(changes, c => c.Kind == ChangeKind.NewTransponder && c.Name == "10723 V");
    }

    [Fact]
    public void Plans_channel_changes_without_touching_anything()
    {
        var db = Db();
        var before = db.ToBytes();
        var changes = ImportPlanner.Plan(db, 0, ListReader.Parse(Lamedb4));

        Assert.Contains(changes, c => c.Kind == ChangeKind.Pids && c.Name == "One");
        var rename = changes.Single(c => c.Kind == ChangeKind.Rename);
        Assert.Equal(("Two", "Two HD", false), (rename.Before, rename.After, rename.Selected));
        Assert.Contains(changes, c => c.Kind == ChangeKind.NewChannel && c.Name == "Brand New" && c.Selected);
        var pay = changes.Single(c => c.Name == "Pay Channel");
        Assert.True(pay.Scrambled);
        Assert.False(pay.Selected);
        Assert.Equal(before, db.ToBytes());
    }

    [Fact]
    public void Applies_selected_changes()
    {
        var db = Db();
        var changes = ImportPlanner.Plan(db, 0, ListReader.Parse(Lamedb4));
        int applied = ImportPlanner.Apply(db, 0, changes);
        Assert.Equal(changes.Count(c => c.Selected), applied);

        var saved = ChannelDatabase.Parse(db.ToBytes());
        Assert.Equal(2, saved.Transponders.Count);
        var newTp = saved.Transponders[1];
        Assert.Equal((12034, 1, 0), (newTp.Frequency, newTp.Polarization, newTp.SatelliteIndex));

        var one = saved.TvChannels.Single(c => c.Name == "One");
        Assert.Equal(0x600, one.VideoPid & 0x1FFF);
        Assert.Equal(0x4000, one.VideoPid & 0xE000); // receiver flag bits kept
        Assert.Equal(1, one.Definition & 1);           // H.264 is HD

        Assert.Equal("Two", saved.TvChannels.Single(c => c.ServiceId == 20).Name); // rename was unticked
        var added = saved.TvChannels.Single(c => c.Name == "Brand New");
        Assert.Equal((1, 99, 0x200, 0x201), (added.TransponderIndex, added.ServiceId, added.VideoPid, added.AudioPidA));
        Assert.Equal((uint)StreamType.Ac3, added.AudioTypeA);
        Assert.DoesNotContain(saved.TvChannels, c => c.Name == "Pay Channel");
    }

    [Fact]
    public void Finds_channels_on_duplicate_transponders()
    {
        // Real lists can hold the same transponder twice; the channel sits on the first copy
        // while the import's frequency is closer to the second.
        var db = Db();
        var copy = Transponder.CloneFrom(db.Transponders[0]);
        copy.Frequency = 11768;
        db.Transponders.Add(copy);
        var list = ListReader.Parse(Lamedb4.Replace("11766000", "11768000"));
        var changes = ImportPlanner.Plan(db, 0, list);
        Assert.DoesNotContain(changes, c => c.Kind == ChangeKind.NewChannel && c.Name is "One" or "Two HD");
        Assert.Contains(changes, c => c.Kind == ChangeKind.Pids && c.Name == "One");
    }

    [Fact]
    public void Nothing_is_ever_deleted()
    {
        var db = Db();
        int channels = db.TvChannels.Count + db.RadioChannels.Count;
        var changes = ImportPlanner.Plan(db, 0, ListReader.Parse(KingOfSatIni));
        foreach (var c in changes) c.Selected = true;
        ImportPlanner.Apply(db, 0, changes);
        Assert.Equal(channels, db.TvChannels.Count + db.RadioChannels.Count);
    }

    [Fact]
    public void Doubtful_symbol_rates_are_not_ticked()
    {
        // The sample's 11766 H carries channels. One source line moves it to 30000; another source pair
        // disagrees about 12000 V, as an old and a new satellite file at one position can.
        var list = new ImportedList { Format = "test" };
        list.Transponders.AddRange([
            new ImportedTransponder(11766, Polarity.Horizontal, 30000),
            new ImportedTransponder(12000, Polarity.Vertical, 27500),
            new ImportedTransponder(12001, Polarity.Vertical, 5000),
            new ImportedTransponder(12500, Polarity.Vertical, 27500),
        ]);
        var changes = ImportPlanner.Plan(Db(), 0, list);

        var rate = Assert.Single(changes, c => c.Kind == ChangeKind.SymbolRate);
        Assert.False(rate.Selected); // channels already play at the stored rate
        Assert.False(Assert.Single(changes, c => c.Name.StartsWith("1200")).Selected); // disputed
        Assert.True(Assert.Single(changes, c => c.Name == "12500 V").Selected);
    }

    [Fact]
    public void A_disputed_transponder_gets_no_symbol_rate_change()
    {
        var list = new ImportedList { Format = "test" };
        list.Transponders.AddRange([new ImportedTransponder(11766, Polarity.Horizontal, 27500), new ImportedTransponder(11767, Polarity.Horizontal, 5000)]);
        Assert.DoesNotContain(ImportPlanner.Plan(Db(), 0, list), c => c.Kind == ChangeKind.SymbolRate);
    }
}
