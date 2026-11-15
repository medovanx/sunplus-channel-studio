using System.Net;
using System.Text;
using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;

namespace SunplusChannelStudio.Tests;

public class ScanTableSourceTests
{
    [Theory]
    [InlineData("Nilesat-201-7.0W", -70)]
    [InlineData("Hotbird-13G-13.0E", 130)]
    [InlineData("Eutelsat-117-West-B-117.0W", -1170)]
    [InlineData("Astra-19.2E", 192)]
    [InlineData("README", null)]
    public void Reads_the_position_from_file_names(string name, int? position) =>
        Assert.Equal(position, ScanTableSource.PositionOf(name));

    [Fact]
    public void Names_and_kingofsat_links()
    {
        Assert.Equal("Nilesat 201", ScanTableSource.SatelliteName("Nilesat-201-7.0W"));
        Assert.Equal("https://en.kingofsat.net/dl.php?pos=7W&fkhz=0", ScanTableSource.KingOfSatDownload(-70));
        Assert.Equal("https://en.kingofsat.net/dl.php?pos=19.2E&fkhz=0", ScanTableSource.KingOfSatDownload(192));
    }

    [Fact]
    public void Satellite_direction_comes_from_the_first_byte()
    {
        var db = ChannelDatabase.Parse(Samples.Database());
        Assert.True(db.Satellites[0].West);
        Assert.Equal(-70, db.Satellites[0].SignedPosition);
    }

    /// <summary>Answers the GitHub listing and two Nilesat files, and fails anything else.</summary>
    sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            string? body = url == ScanTableSource.ListUrl
                ? """[{"name":"Nilesat-201-7.0W","type":"file"},{"name":"Nilesat-301-7.0W","type":"file"},{"name":"Hotbird-13G-13.0E","type":"file"},{"name":"Amos-7.0E","type":"file"}]"""
                : url.EndsWith("Nilesat-201-7.0W")
                    ? "# Channel table for Nilesat-201-7.0W\n[OSN TV, CHANNEL]\n\tDELIVERY_SYSTEM = DVBS\n\tFREQUENCY = 11747000\n\tPOLARIZATION = VERTICAL\n\tSYMBOL_RATE = 27500000\n"
                    : url.EndsWith("Nilesat-301-7.0W")
                        ? "[Nilesat]\n\tDELIVERY_SYSTEM = DVBS2\n\tFREQUENCY = 10971000\n\tPOLARIZATION = HORIZONTAL\n\tSYMBOL_RATE = 27500000\n" +
                          "[Nilesat]\n\tDELIVERY_SYSTEM = DVBS2\n\tFREQUENCY = 12188000\n\tPOLARIZATION = HORIZONTAL\n\tSYMBOL_RATE = 27500000\n"
                        : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    [Fact]
    public async Task Fetches_only_the_wanted_position_and_merges_its_satellites()
    {
        var github = new FakeGitHub();
        var list = await ScanTableSource.FetchAsync(new HttpClient(github), [-70]);

        Assert.Equal([(11747, Polarity.Vertical, -70), (10971, Polarity.Horizontal, -70), (12188, Polarity.Horizontal, -70)],
            list.Transponders.Select(t => (t.Frequency, t.Polarization, t.Position!.Value)));
        Assert.Equal("Nilesat 301, Nilesat 201 (7.0W)", list.Label(-70)); // the satellite with more transponders first
        Assert.DoesNotContain(github.Asked, u => u.Contains("Amos") || u.Contains("Hotbird")); // 7.0E is not 7.0W
        Assert.False(list.GroupsAreSatelliteIndexes);
    }

    /// <summary>Against the real repository; runs only with SCS_ONLINE=1 so the normal suite needs no network.</summary>
    [Fact]
    public async Task Online_nilesat_lists_load()
    {
        if (Environment.GetEnvironmentVariable("SCS_ONLINE") != "1") return;
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SunplusChannelStudio-tests");
        var list = await ScanTableSource.FetchAsync(http, [-70]);
        Assert.True(list.Transponders.Count > 10, $"only {list.Transponders.Count} transponders");
        Assert.Contains("Nilesat", list.Label(-70));
        Assert.Contains(list.Transponders, t => t.Frequency == 11747 && t.Polarization == Polarity.Vertical);
    }

    [Fact]
    public async Task A_position_without_lists_is_reported()
    {
        var error = await Assert.ThrowsAsync<ImportFormatException>(() => ScanTableSource.FetchAsync(new HttpClient(new FakeGitHub()), [450]));
        Assert.Equal(ImportProblem.NoTransponders, error.Problem);
    }
}

public class DatabaseBuilderTests
{
    static ImportedList NilesatList()
    {
        var list = new ImportedList { Format = "dtv-scan-tables" };
        list.PositionNames[-70] = "Eutelsat 7 West A, Nilesat 201 (7.0W)";
        list.Transponders.AddRange([
            new ImportedTransponder(11747, Polarity.Vertical, 27500, -70),
            new ImportedTransponder(10971, Polarity.Horizontal, 27500, -70),
            new ImportedTransponder(10971, Polarity.Horizontal, 27500, -70), // listed by both satellites
        ]);
        return list;
    }

    [Fact]
    public void Builds_a_database_the_reader_accepts()
    {
        var db = DatabaseBuilder.Create(NilesatList(), [-70], DatabaseType.MultistreamV1);
        var again = ChannelDatabase.Parse(db.ToBytes());

        var sat = Assert.Single(again.Satellites);
        Assert.Equal("Eutelsat 7 Wes 7.0W", sat.Name); // one satellite per family: the first, shortened so the position fits
        Assert.Equal(-70, sat.SignedPosition);
        Assert.Equal((9750, 10600), (sat.LnbLow, sat.LnbHigh));
        Assert.Equal(0x1e, sat.Raw[0]);

        Assert.Equal([(10971, (byte)1), (11747, (byte)0)], again.Transponders.Select(t => ((int)t.Frequency, t.Polarization)).Order());
        Assert.All(again.Transponders, t =>
        {
            Assert.Equal(0, t.SatelliteIndex);
            Assert.Equal(27500, t.SymbolRate);
            Assert.Equal(1, t.Raw[27]);                    // as in the receiver's own "New TP" records
            Assert.Equal(0xffff, BitConverter.ToUInt16(t.Raw, 30));
        });
        Assert.Equal([0, 1], again.Transponders.Select(t => (int)t.Id).Order());
        Assert.Empty(again.TvChannels);
        Assert.Equal(DatabaseType.NormalDkOtp, again.Type); // with no channels the size cannot tell; Normal is the reading default
    }

    [Fact]
    public void Short_names_keep_the_position()
    {
        var list = NilesatList();
        list.PositionNames[-70] = "Eutelsat 7 West A, Nilesat 201, Nilesat 301 (7.0W)";
        Assert.Equal("Nilesat 7.0W", DatabaseBuilder.ShortName(list, -70)); // two of three are Nilesat
        list.PositionNames[192] = "Astra 1KR (19.2E)";
        Assert.Equal("Astra 1KR 19.2E", DatabaseBuilder.ShortName(list, 192));
        Assert.Equal("13.0E", DatabaseBuilder.ShortName(list, 130));
    }
}
