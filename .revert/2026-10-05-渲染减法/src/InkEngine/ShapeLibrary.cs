using System;
using System.Collections.Generic;
using System.IO;

namespace InkEngine;

/// <summary>
/// **图形库**（用户 2026-09-22 要的"图像收藏"）：把选中的对象存成文件，随时再取出来用。
///
/// 入口有两个，都在界面那一侧：
///   · **保存**：选中对象 → 操作条上那颗「存入图库」（见 <see cref="SelBarButton.Library"/>）；
///   · **取用**：图形面板那一格的最后一段「图库」→ 弹出缩略图面板 → 点一张 → 到画布上落笔插入。
///
/// 三条设计口径（都是对着参考实现 InkClass 的 `MW_CustomShapes.cs` 定的）：
///
/// 1. **存的是对象，不是图片**（用户 2026-09-19 就拍过这一条）。参考实现存的是 WPF 的 ISF
///    墨迹二进制——它那边"一切都是笔迹"，所以能那么存；我们的图形是对象（Kind ＋ 几何参数 ＋
///    线型/颜色/粗细），ISF 那套套不上。
///    这里复用**我们自己的存档格式**（<see cref="InkSerializer"/>）：把对象塞进一个临时
///    <see cref="InkDocument"/> 再 <see cref="InkSerializer.Save"/>，和"复制到剪贴板"
///    （<see cref="ClipboardInk.Serialize"/>）走的是同一条路——**一个字节的格式都不新造**，
///    以后加字段（新的图形、新的线型）自动跟着走。
/// 2. **缩略图不落盘**，每次打开现画（见 Overlay 的图库面板）：所以图库条目永远和对象一致，
///    DPI 再高也不糊。
/// 3. **一个条目 = 一个文件**（`&lt;guid&gt;.ink`，内容不透明、只我们自己读），
///    放在 `%APPDATA%\InkTeach\Library\`——和设置文件同一个根（见 <see cref="InkSettings"/>：
///    教室机上程序目录往往只读，所以**绝不能放程序目录旁边**）。
/// </summary>
internal static class ShapeLibrary
{
    /// <summary>自检用：把图库目录指到临时目录，别动用户真正的收藏（同 <see cref="InkSettings.PathOverride"/>）。</summary>
    public static string DirOverride;

    /// <summary>图库目录。没有就建（存的时候建；列的时候没有＝空库）。</summary>
    public static string Dir => DirOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InkTeach", "Library");

    /// <summary>条目文件后缀。挑一个和"页面存档"能区分开的，方便用户自己进目录看/备份。</summary>
    public const string FileExt = ".ink";

    /// <summary>
    /// 一批对象存成一个图库条目。成功返回文件路径，失败返回 null（调用方只管"成没成"）。
    ///
    /// 空集合直接拒：存一个空条目出来，图库里会多一张永远画不出东西的格子。
    /// </summary>
    public static string Save(IReadOnlyList<Stroke> items)
    {
        if (items == null || items.Count == 0) return null;
        try
        {
            byte[] bytes = ClipboardInk.Serialize(items);
            if (bytes == null || bytes.Length == 0) return null;

            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, Guid.NewGuid().ToString("N") + FileExt);
            File.WriteAllBytes(file, bytes);
            Console.WriteLine($"存入图库 {items.Count} 个对象：{file}");
            return file;
        }
        catch (Exception ex)
        {
            //存不进去不是致命错（磁盘满 / 权限），报一行就好，别把正在写的板书打断
            Console.WriteLine("存入图库失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 列出图库里所有条目（**旧的在前面**，新存的排后面——和参考实现一致，
    /// 用户的直觉是"新东西出现在老东西后面"）。读不出来的文件跳过并报一行。
    /// </summary>
    public static List<Entry> List()
    {
        var list = new List<Entry>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            var files = new List<string>(Directory.GetFiles(Dir, "*" + FileExt));
            //按最后写入时间排：文件名的 GUID 是乱序的，时间才是"先后"
            files.Sort((a, b) => File.GetLastWriteTime(a).CompareTo(File.GetLastWriteTime(b)));

            foreach (string f in files)
            {
                var strokes = Load(f);
                if (strokes == null || strokes.Count == 0) continue;
                list.Add(new Entry { Path = f, Strokes = strokes });
            }
        }
        catch (Exception ex)
        {
            //和参考实现吃过的那个亏一样：**异常不能静默吞掉**，不然"图库空了"分不清是
            //真没存过还是读挂了（它那边曾经因为吞异常把"整理后图全消失"误判成空库）。
            Console.WriteLine("[图库] 列目录失败：" + ex.Message);
        }
        return list;
    }

    /// <summary>
    /// 读一个条目。返回的对象**身份已归零**（Id = 0）：文件里的 Id 是当时那批对象的，
    /// 直接插进文档会和现在的对象撞号，撤销/多选就会指错（同 <see cref="ClipboardInk"/>）。
    /// </summary>
    public static List<Stroke> Load(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tmp = new InkDocument();
            InkSerializer.LoadInto(tmp, bytes);
            var items = tmp.Strokes;
            foreach (var s in items) s.Id = 0;
            return items;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[图库] 读不出来（跳过）：" + path + " —— " + ex.Message);
            return null;
        }
    }

    /// <summary>删一个条目（面板上"整理模式"点 × 走这里）。</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[图库] 删除失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>图库里的一个条目：一个文件 ＋ 它装的那批对象。</summary>
    public sealed class Entry
    {
        public string Path;
        public List<Stroke> Strokes;
    }
}
