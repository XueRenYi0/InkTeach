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
        // 索引的是**带笔宽外扩的世界包围盒**（画布坐标）：
        //   · 对象被移动/缩放之后占的格子会变，所以每次变换都要重插一遍；
        //   · 用的是 PaddedBounds 而不是 WorldBounds —— 笔迹是画在中心线
        //     两侧的，只索引中心线的范围，会让"中心线在格子外、笔身伸进
        //     格子里"的粗笔画查不到。内容层分块之后这条更关键：漏一条，
        //     块边界上就会缺一块墨（表现为笔迹被削掉一条边）。
        //     索引放大一点只会多给几个候选，不会改变任何结果。
        var b = s.PaddedBounds;
        if (b.IsEmpty) return;
        int x0 = (int)MathF.Floor(b.MinX / CellSize);
        int y0 = (int)MathF.Floor(b.MinY / CellSize);
        int x1 = (int)MathF.Floor(b.MaxX / CellSize);
        int y1 = (int)MathF.Floor(b.MaxY / CellSize);
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
        var b = s.PaddedBounds;
        if (b.IsEmpty) return;
        int x0 = (int)MathF.Floor(b.MinX / CellSize);
        int y0 = (int)MathF.Floor(b.MinY / CellSize);
        int x1 = (int)MathF.Floor(b.MaxX / CellSize);
        int y1 = (int)MathF.Floor(b.MaxY / CellSize);
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
                    // 和 Insert 用同一套范围：带笔宽外扩。
                    if (s.PaddedBounds.Intersects(r)) results.Add(s);
                }
            }
        return results.Count;
    }

    public int CellCount => _cells.Count;
}
