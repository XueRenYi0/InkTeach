using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;

namespace InkEngine;

/// <summary>
/// **收藏条目的内容指纹**（2026-10-25，收藏功能第一批）。
///
/// 目的只有一个：同一张图 / 同一个图标 / 同一段墨迹不要被收两次。
/// 不走"文件字节"那条路——文件里带 Id、时间戳、位置，同一个东西挪一下就不一样了。
///
/// 规则（和用户过的口径）：
///   · **全是图片**：只认像素 + 宽高，忽略位置/缩放/旋转——同一张图就是同一张；
///   · **其他（含图片+墨迹的混合组）**：先把整组平移到包围盒原点，保留旋转/缩放/
///     翻转/相对布局；同一个东西挪个位置会被拦住，故意转一下/放大算新变体；
///   · 每个对象单独序列化成我们自己的存档字节（复用 <see cref="InkSerializer"/>，
///     新字段自动跟着走），排序后再整体哈希——所以选择顺序不影响。
///
/// ⚠ Id / Locked / 点的时间戳不进指纹：它们是"这一次的身份/状态"，不是"长什么样"。
/// ⚠ 这里会临时 Clone 一份再改，**绝不改调用方手里的对象**。
/// </summary>
internal static class CollectionFingerprint
{
    /// <summary>空集合 / 没法算时返回 null（调用方按"不能收藏"处理）。</summary>
    public static string Of(IReadOnlyList<Stroke> items)
    {
        if (items == null || items.Count == 0) return null;

        var clones = new List<Stroke>(items.Count);
        foreach (var s in items)
        {
            if (s == null) continue;
            var c = s.Clone();
            c.Id = 0;
            c.Locked = false;                       // 锁不锁不影响"内容"
            c.HasPressure = s.HasPressure;           // Clone 没带这两项，但渲染需要
            c.AxisGridStep = s.AxisGridStep;
            for (int i = 0; i < c.Points.Count; i++)
            {
                var p = c.Points[i];
                p.T = 0;                             // 时间戳不是内容
                c.Points[i] = p;
            }
            clones.Add(c);
        }
        if (clones.Count == 0) return null;

        bool allImages = true;
        foreach (var c in clones) if (!c.IsImage) { allImages = false; break; }

        if (allImages)
        {
            // 同一张图：位置/大小/旋转都不算"不同"。
            foreach (var c in clones) c.Transform = Matrix3x2.Identity;
        }
        else
        {
            // 整组平移到原点：挪动不改变指纹，相对布局保留。
            var box = EditRegion.Of(clones);
            if (!box.IsEmpty)
            {
                var shift = Matrix3x2.CreateTranslation(-box.MinX, -box.MinY);
                foreach (var c in clones) c.Transform *= shift;
            }
        }

        var parts = new List<byte[]>(clones.Count);
        foreach (var c in clones)
        {
            byte[] bytes = InkSerializer.SaveStrokes(new[] { c });
            parts.Add(SHA256.HashData(bytes));
        }
        // 选择顺序不影响：按每个对象的哈希排序。
        parts.Sort(CompareBytes);

        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            // 带长度前缀，避免两个对象拼起来产生歧义。
            ms.Write(BitConverter.GetBytes(part.Length), 0, sizeof(int));
            ms.Write(part, 0, part.Length);
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i].CompareTo(b[i]);
            if (d != 0) return d;
        }
        return a.Length.CompareTo(b.Length);
    }
}
