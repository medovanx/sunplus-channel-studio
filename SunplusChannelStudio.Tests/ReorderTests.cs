using SunplusChannelStudio.Core;

namespace SunplusChannelStudio.Tests;

public class ReorderTests
{
    static List<string> List() => ["A", "B", "C", "D", "E", "F"];

    /// <summary>Drop gap N means "before the item that is at N now"; gap Count is the end.</summary>
    static string Drop(string[] picked, int gap)
    {
        var list = List();
        int index = Reorder.FinalIndex(list, picked, gap);
        if (!Reorder.IsNoOp(list, picked, index)) Reorder.MoveBlock(list, picked, index);
        return string.Concat(list);
    }

    [Theory]
    [InlineData(new[] { "A" }, 4, "BCDAEF")]        // first row dropped below D
    [InlineData(new[] { "E" }, 0, "EABCDF")]        // dropped above the first row
    [InlineData(new[] { "C" }, 6, "ABDEFC")]        // dropped below the last row
    [InlineData(new[] { "C" }, 2, "ABCDEF")]        // onto its own top edge
    [InlineData(new[] { "C" }, 3, "ABCDEF")]        // onto its own bottom edge
    [InlineData(new[] { "B", "E" }, 4, "ACDBEF")]   // scattered pick, gap inside the selection
    [InlineData(new[] { "E", "B" }, 0, "BEACDF")]   // pick order does not matter, list order is kept
    [InlineData(new[] { "B", "C" }, 6, "ADEFBC")]   // contiguous block to the end
    public void Drag_and_drop_lands_where_the_line_shows(string[] picked, int gap, string expected) =>
        Assert.Equal(expected, Drop(picked, gap));

    [Fact]
    public void Move_to_position_clamps_to_the_list()
    {
        var list = List();
        Reorder.MoveBlock(list, ["A"], 99);
        Assert.Equal("BCDEFA", string.Concat(list));
    }
}
