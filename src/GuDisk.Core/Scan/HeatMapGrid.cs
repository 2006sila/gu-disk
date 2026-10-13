namespace GuDisk.Core.Scan;

/// <summary>
/// 把逐桶扫描结果降采样成**固定格数**的显示网格。
///
/// 为什么必须有这一层：热力图的格数若跟着扫描量走，1 GB 是 1024 格没问题，
/// 但 100 GB 就是 10 万格、4 TB 机械盘就是 400 万格 —— WPF 会直接卡死。
/// 参考软件的做法也是固定格数（64 × 24 ≈ 1536），每格代表一段可变的容量。
///
/// **计数仍按真实桶数统计**（一个桶 = 1 MiB），只有显示被合并：
/// 一格里只要有一个坏块，那格就显示最差等级。所以"损坏 3"永远是 3 个 1 MiB 块，
/// 不会因为降采样而变成"3 格"。
/// </summary>
public static class HeatMapGrid
{
    /// <summary>显示格数上限，与参考软件同量级（64 列 × 24 行）。</summary>
    public const int MaxCells = 64 * 24;

    /// <param name="StartBytes">该格覆盖的起始偏移。</param>
    /// <param name="BucketCount">该格合并了多少个原始桶（1 = 未降采样）。</param>
    public sealed record Cell(long StartBytes, long EndBytes, int Level,
        double WorstMibPerSecond, int BucketCount);

    /// <param name="levelOf">把桶映射到等级索引（阈值由界面层给）。</param>
    /// <param name="levelCount">等级档数，用于计数数组。</param>
    public static (List<Cell> Cells, int[] Counts) Build(
        IReadOnlyList<ScanBucket> map, Func<ScanBucket, int> levelOf, int levelCount,
        int maxCells = MaxCells)
    {
        var counts = new int[Math.Max(1, levelCount)];
        if (map.Count == 0) return ([], counts);

        // 计数走真实桶 —— 与显示降采样无关
        var levels = new int[map.Count];
        for (int i = 0; i < map.Count; i++)
        {
            int lv = levelOf(map[i]);
            if ((uint)lv >= (uint)counts.Length) lv = counts.Length - 1;
            levels[i] = lv;
            counts[lv]++;
        }

        int step = Math.Max(1, (int)Math.Ceiling(map.Count / (double)Math.Max(1, maxCells)));
        var cells = new List<Cell>(Math.Min(map.Count, Math.Max(1, maxCells)));
        for (int i = 0; i < map.Count; i += step)
        {
            int end = Math.Min(i + step, map.Count);
            int worst = -1, worstIdx = i;
            for (int j = i; j < end; j++)
                if (levels[j] > worst) { worst = levels[j]; worstIdx = j; }

            var last = map[end - 1];
            cells.Add(new Cell(map[i].OffsetBytes, last.OffsetBytes + last.LengthBytes,
                worst, map[worstIdx].MibPerSecond, end - i));
        }
        return (cells, counts);
    }
}
