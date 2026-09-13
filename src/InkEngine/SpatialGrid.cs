namespace InkEngine;

/// <summary>
/// 桌面上的一张均匀网格，用来做"这一块附近有哪些笔画"的查询。
///
/// 橡皮擦和框选以前是每次指针移动都把所有笔画扫一遍；有了网格之后只看
/// 查询覆盖到的格子。同一个笔画可能落在多个格子里，用笔画自带的 stamp
/// 去重，所以查询过程不分配任何对象。
///
/// 这是**底层设施**，不属于笔迹优化：它不改变笔迹长什么样，只决定
/// "找得快不快"。所以它留在核心程序集里。
/// </summary>
internal sealed class SpatialGrid
{
    public const int CellSize = 256;

    private readonly Dictionary<long, List<Stroke>> _cells = new();
    private int _stamp;

    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    public void Clear() => _cells.Clear();

    public void Insert(Stroke s)
    {
        if (s.Bounds.IsEmpty) return;
        int x0 = (int)MathF.Floor(s.Bounds.MinX / CellSize);
        int y0 = (int)MathF.Floor(s.Bounds.MinY / CellSize);
        int x1 = (int)MathF.Floor(s.Bounds.MaxX / CellSize);
        int y1 = (int)MathF.Floor(s.Bounds.MaxY / CellSize);
        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                long k = Key(cx, cy);
                if (!_cells.TryGetValue(k, out var list))
                {
                    list = new List<Stroke>();
                    _cells[k] = list;
                }
                list.Add(s);
            }
    }

    public void Remove(Stroke s)
    {
        if (s.Bounds.IsEmpty) return;
        int x0 = (int)MathF.Floor(s.Bounds.MinX / CellSize);
        int y0 = (int)MathF.Floor(s.Bounds.MinY / CellSize);
        int x1 = (int)MathF.Floor(s.Bounds.MaxX / CellSize);
        int y1 = (int)MathF.Floor(s.Bounds.MaxY / CellSize);
        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (_cells.TryGetValue(Key(cx, cy), out var list)) list.Remove(s);
            }
    }

    /// <summary>把包围盒与矩形相交的候选笔画追加到 results 里（不清空调用方的其它状态）。</summary>
    public int Query(RectF r, List<Stroke> results)
    {
        results.Clear();
        if (r.IsEmpty) return 0;

        _stamp++;
        int x0 = (int)MathF.Floor(r.MinX / CellSize);
        int y0 = (int)MathF.Floor(r.MinY / CellSize);
        int x1 = (int)MathF.Floor(r.MaxX / CellSize);
        int y1 = (int)MathF.Floor(r.MaxY / CellSize);

        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var list)) continue;
                foreach (var s in list)
                {
                    if (s.QueryStamp == _stamp) continue;
                    s.QueryStamp = _stamp;
                    if (s.Bounds.Intersects(r)) results.Add(s);
                }
            }
        return results.Count;
    }

    public int CellCount => _cells.Count;
}
