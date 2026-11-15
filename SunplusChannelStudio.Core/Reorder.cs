namespace SunplusChannelStudio.Core;

/// <summary>Moves a selection of list items as one block, keeping their order.</summary>
public static class Reorder
{
    /// <summary>
    /// Converts a drop position (the gap before index <paramref name="insertAt"/> in the current list)
    /// into the block's final start index, since removing the picked items shifts later gaps up.
    /// </summary>
    public static int FinalIndex<T>(IList<T> list, IEnumerable<T> picked, int insertAt) =>
        insertAt - picked.Count(item => list.IndexOf(item) < insertAt);

    /// <summary>Moves the picked items so the block starts at <paramref name="finalIndex"/>. Returns the block in list order.</summary>
    public static List<T> MoveBlock<T>(IList<T> list, IEnumerable<T> picked, int finalIndex)
    {
        var block = picked.Distinct().OrderBy(list.IndexOf).ToList();
        foreach (var item in block) list.Remove(item);
        finalIndex = Math.Clamp(finalIndex, 0, list.Count);
        for (int i = 0; i < block.Count; i++) list.Insert(finalIndex + i, block[i]);
        return block;
    }

    /// <summary>True when the drop would leave the list unchanged.</summary>
    public static bool IsNoOp<T>(IList<T> list, IEnumerable<T> picked, int finalIndex)
    {
        var positions = picked.Select(list.IndexOf).OrderBy(i => i).ToList();
        return positions.Count > 0 && positions[0] == finalIndex
               && positions.Select((p, i) => p == finalIndex + i).All(x => x);
    }
}
