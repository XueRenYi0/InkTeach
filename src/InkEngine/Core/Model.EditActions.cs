using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

internal abstract class EditAction
{
    public abstract void Undo(InkDocument doc);
    public abstract void Redo(InkDocument doc);

    /// <summary>这次改动**之前**对象占了哪块（画布坐标）。</summary>
    public virtual RectF AffectedBefore => RectF.Empty;

    /// <summary>改动**之后**占哪块。</summary>
    public virtual RectF AffectedAfter => RectF.Empty;

    /// <summary>要重绘的区域 = 两者并集。旧位置要擦、新位置要画，缺一个就留残影。</summary>
    public RectF AffectedUnion
    {
        get { var r = AffectedBefore; r.Add(AffectedAfter); return r; }
    }

    /// <summary>
    /// 这条动作**自己往 `doc.Dirty` 里加矩形**，不要 `Commit` / `Undo` / `Redo`
    /// 再用 <see cref="AffectedUnion"/> 补一次。默认 false。
    ///
    /// 现在只有"改几何"（拖直线端点）那一条为真：它要加的是"旧位、新位**各一个**矩形"，
    /// 而不是两者的**并集**——对一条横跨屏幕的直线，并集几乎等于整屏（≈96 块），
    /// 而旧、新各一个各只压十几块，差约 8 倍（见 计划-图形工具.md 8.1②）。
    /// </summary>
    public virtual bool SelfManagesDirty => false;

    /// <summary>
    /// 这条动作**引用着多少条笔画**。撤销栈的内存成本主要就在这里：
    /// "删掉/清空"之后笔画并没有消失，只是从文档挪到了撤销栈里——撤销能还原，
    /// 代价就是这些笔画必须一直活着。
    /// </summary>
    public virtual int HeldStrokes => 0;
}

/// <summary>一组对象占的总区域。加入、移除、改位置都只是"方向不同"，算法一样。</summary>
internal static class EditRegion
{
    public static RectF Of(IEnumerable<Stroke> strokes)
    {
        var r = RectF.Empty;
        foreach (var s in strokes) r.Add(s.PaddedBounds);
        return r;
    }
}

internal sealed class AddStrokesAction : EditAction
{
    public readonly List<Stroke> Strokes = new();
    public override int HeldStrokes => Strokes.Count;
    public override void Undo(InkDocument doc) { foreach (var s in Strokes) doc.RemoveStroke(s); }
    public override void Redo(InkDocument doc) { foreach (var s in Strokes) doc.AppendStroke(s); }
    public override RectF AffectedAfter => EditRegion.Of(Strokes);
}

/// <summary>
/// **停顿成型**：把用户手画的那一笔换成规整图形，**一步撤销、撤了回到手绘原迹**
///（见 计划-图形工具.md §四十二）。
///
/// 为什么不用 <see cref="AddStrokesAction"/>：那条路撤销之后是"图形没了、什么都不剩"——
/// 用户手画的那一笔就永远找不回来了。而"停顿变"是**默认开**的功能，变的又不是用户明确
/// 要的形状（他只是在写字 / 画草图），所以必须留一条"这是我看错了"的退路：
/// 按一次 Ctrl+Z 回到**自己画的那一笔**，而不是回到空白。
/// （InkClass 的"替换型历史"就是干这个的，见 §42.1。）
///
/// 注意手绘原迹**从来没进过文档**（停顿是在笔还按着的时候就触发、当场把它换掉的），
/// 所以它只活在这条记录里 —— `HeldStrokes` 因此是 2。
/// </summary>
internal sealed class DwellShapeAction : EditAction
{
    public Stroke Shape;      // 变出来的图形（已经在文档里）
    public Stroke Ink;        // 手绘原迹
    public int Index;         // 提交时的层序：撤销要把原迹放回**同一个位置**

    /// <summary>**两笔成型**时的**第一笔原迹**（用户 2026-09-25 定的"画两支就是双曲线"）。
    /// 非 null 时：撤销要**一步把两笔都放回去**（"一步回两笔手绘"），重做要**一步把两笔都收走**。
    /// 为什么不开一个新动作类：这条路和"一笔 → 一个图形"**是同一条路**，
    /// 只多了一笔原迹 —— 新开一个类等于把同一段记账抄一遍（同一个坑这个仓库踩过好几次）。
    /// </summary>
    public Stroke Ink2;
    public int Index2;

    public override int HeldStrokes => Ink2 == null ? 2 : 3;

    public override void Undo(InkDocument doc)
    {
        doc.RemoveStroke(Shape);
        // 先放第二笔、再放第一笔 —— 和提交时"先收第一笔"的顺序**反着来**，
        // 这样两笔回到文档里仍是原来那个相对次序。
        if (Ink2 != null) doc.InsertStroke(Index2, Ink2);
        doc.InsertStroke(Index, Ink);
    }

    public override void Redo(InkDocument doc)
    {
        doc.RemoveStroke(Ink);
        if (Ink2 != null) doc.RemoveStroke(Ink2);
        doc.InsertStroke(Index, Shape);
    }

    // 两个方向都要重绘（旧位置擦、新位置画），所以前后**都**算上：
    // 少了任何一半，屏幕上都会留一条"擦不掉的旧墨"或"看不见的新墨"。
    public override RectF AffectedBefore =>
        Ink2 == null ? EditRegion.Of(new[] { Ink }) : EditRegion.Of(new[] { Ink, Ink2 });
    public override RectF AffectedAfter => EditRegion.Of(new[] { Shape });
}

/// <summary>
/// 把几步合成**一步撤销**。现在只有一个用处：**拖出副本**（插入副本 + 移动副本）——
/// 用户拖错了一下，按一次撤销就该回到"什么都没发生"。
///
/// 约定和别的动作一样：**效果已经应用过了**，这里只负责记账；撤销时反着放回去。
/// </summary>
internal sealed class CompoundAction : EditAction
{
    public readonly List<EditAction> Steps = new();

    public override int HeldStrokes
    {
        get { int n = 0; foreach (var a in Steps) n += a.HeldStrokes; return n; }
    }

    public override RectF AffectedBefore
    {
        get { var r = RectF.Empty; foreach (var a in Steps) r.Add(a.AffectedBefore); return r; }
    }

    public override RectF AffectedAfter
    {
        get { var r = RectF.Empty; foreach (var a in Steps) r.Add(a.AffectedAfter); return r; }
    }

    public override void Undo(InkDocument doc)
    {
        for (int i = Steps.Count - 1; i >= 0; i--) Steps[i].Undo(doc);
    }

    public override void Redo(InkDocument doc)
    {
        foreach (var a in Steps) a.Redo(doc);
    }
}

internal sealed class RemoveStrokesAction : EditAction
{
    public readonly List<(int index, Stroke stroke)> Items = new();
    public override int HeldStrokes => Items.Count;
    public override void Undo(InkDocument doc) { foreach (var it in Items) doc.InsertStroke(it.index, it.stroke); }
    public override void Redo(InkDocument doc) { foreach (var it in Items) doc.RemoveStroke(it.stroke); }
    public override RectF AffectedBefore => EditRegion.Of(Items.Select(it => it.stroke));
}

/// <summary>
/// 像素橡皮：往笔画的**擦除区间表**里加区间（见 <see cref="Stroke.Erased"/>）。
///
/// 一条记录 = 一条**原本就存在**的笔画，两种情况：
///   · 还在（只是被擦掉了几段）→ 撤销就是把区间表换回 Before、重做换回 After；
///   · 整条被擦没了（图形、或细笔整条落在块里）→ 从文档里拿走，撤销时按 Anchor 放回去。
///
/// 一次拖拽 = 一条记录（不管擦到几条、擦了几刀），所以"擦一次 = 一步撤销"照旧。
///
/// 和上一版（把笔迹拆成几个新对象）比，这里少了一大堆麻烦：对象不动，就没有
/// "碎片又被人擦到要并回去""撤销时把上一版碎片放回来"这些事情，`Id` 也保持不变。
/// </summary>
internal sealed class EraseIntervalsAction : EditAction
{
    internal sealed class Item
    {
        public Stroke S;
        /// <summary>这一笔**原本**的擦除区间表（撤销时换回来）。</summary>
        public List<(float a, float b)> Before = new();
        /// <summary>这一笔**擦完之后**的擦除区间表（重做时换回来）。</summary>
        public List<(float a, float b)> After = new();
        /// <summary>整条被擦没了 → 从文档里拿走（撤销要放回去），Anchor 是它的位置。</summary>
        public bool Removed;
        public int Anchor;
        /// <summary>这一次真正变了的那一块（撤销/重做也用它当脏区，别整条重画）。</summary>
        public RectF Paint = RectF.Empty;

        /// <summary>
        /// 撤销时要还原成什么。默认就是 <see cref="S"/>；**图形被熔成笔迹时记的是原来那个图形**
        /// （S 已经换成熔出来的笔迹了）。
        /// </summary>
        public Stroke UndoOriginal;

        /// <summary>
        /// 图形熔成墨时**熔出来的那一整组**（<see cref="S"/> 只是其中的第一笔，只为让
        /// 调用方能用 <c>Strokes.IndexOf</c> 找到位置）。null = 这条记录不是"熔图形"来的。
        ///
        /// 为什么要一整组：一个图形可能对应屏幕上好几笔（长方体 12 条棱、圆柱 5 笔、
        /// 双曲线两支＋两条渐近线），熔出来必须逐笔对应才不会"虚线变实线 / 凭空多出线"
        /// （见 <see cref="Stroke.MeltToInkParts"/>）。
        /// </summary>
        public List<Stroke> Melted;
    }

    public readonly List<Item> Items = new();

    /// <summary>这一批真正需要重画的范围（画布坐标，由 EraseRectAt 累加）。</summary>
    public RectF Paint = RectF.Empty;

    public Item FindItem(Stroke s)
    {
        for (int i = 0; i < Items.Count; i++)
            if (ReferenceEquals(Items[i].S, s)) return Items[i];
        return null;
    }

    /// <summary>一次拖拽里同一笔被擦了好几刀：并到同一条记录上（Before 保持最初那份）。</summary>
    public Item Touch(Stroke s)
    {
        var it = FindItem(s);
        if (it != null) return it;
        it = new Item { S = s, Before = new List<(float a, float b)>(s.Erased) };
        Items.Add(it);
        return it;
    }

    /// <summary>
    /// 算完发现"这一刀其实什么也没改变"（粗筛进来的、或者这一段早就被擦过了）：
    /// 把**刚刚新建**的这条记录撤掉，别让撤销栈里多出一步空操作。
    /// 调用方负责只在"这条记录是这一刀才建的"时候调——已经改过的那种不能丢。
    /// </summary>
    public void Drop(Item it) => Items.Remove(it);

    public override int HeldStrokes => Items.Count;

    /// <summary>
    /// 只报"真正变化的那一块"。基类默认拿动过的对象的包围盒并集当脏区，那太粗了：
    /// 一条笔迹被擦掉中间一小段，框外的墨一点没变，按整条标脏会把这条墨跨过的每一块
    /// 都拖去重画（实测一万笔时每步重画 11.3 块，而橡皮只盖住 2~4 块）。
    /// </summary>
    public override RectF AffectedBefore => Paint;
    public override RectF AffectedAfter => Paint;

    public override void Undo(InkDocument doc) => Apply(doc, restored: true);
    public override void Redo(InkDocument doc) => Apply(doc, restored: false);

    private void Apply(InkDocument doc, bool restored)
    {
        // ① 区间表换回去（撤销）/ 换回来（重做）。
        foreach (var it in Items)
            doc.SetErased(it.S, restored ? it.Before : it.After, it.Paint);

        // ② 整条被擦没的那些：撤销时放回去、重做时再拿走。
        //
        // 位置用 Anchor——"排在这一笔前面的、没被本批拿走的笔画有几条"。下标在拖动过程中
        // 一直在变（每拿走一条，后面的就少 1），只有这个数不变，所以按它插回去不会错位。
        _removed.Clear();
        foreach (var it in Items) if (it.Removed) _removed.Add(it);
        if (_removed.Count == 0) return;

        _removed.Sort((x, y) => x.Anchor.CompareTo(y.Anchor));
        if (restored)
        {
            int inserted = 0;
            foreach (var it in _removed)
                doc.InsertStroke(it.Anchor + inserted++, it.S, it.Paint);
        }
        else
        {
            foreach (var it in _removed) doc.RemoveStroke(it.S, it.Paint);
        }
    }

    private readonly List<Item> _removed = new();
}

/// <summary>
/// "把擦断的笔迹拆成独立对象"这一步的撤销记录：一条原笔迹 → 它的每一段各成一个对象。
///
/// 只在**用户选中了它**的时候才发生（见 <see cref="InkDocument.SplitErasedSelection"/>）。
/// 平时像素橡皮只往区间表里加区间，不拆——那样自交不变深、对象数不涨、身份也不变。
/// </summary>
internal sealed class SplitErasedAction : EditAction
{
    internal sealed class Item
    {
        /// <summary>原笔迹在列表里的下标（**拖拽前那一套坐标系**，见 InkDocument.EndErase）。</summary>
        public int Index;
        /// <summary>擦之前的那一条（整条进撤销栈，回退时原样回来）。</summary>
        public Stroke Original;
        /// <summary>它**原本**的擦除区间表（老存档里可能就有区间；回退时要一并还原）。</summary>
        public List<(float a, float b)> Before = new();
        /// <summary>擦剩下的几段，各自是独立对象。**空 = 整条被擦没了**。</summary>
        public readonly List<Stroke> Parts = new();
    }

    public readonly List<Item> Items = new();

    /// <summary>
    /// 这一次真正变化的那一块（画布坐标）。给了就用它当脏区，不给就退回"动过的对象的
    /// 包围盒并集"（保守但很粗，见 EraseIntervalsAction 的注释）。
    /// </summary>
    public RectF Paint = RectF.Empty;

    public override int HeldStrokes
    {
        get { int n = 0; foreach (var it in Items) n += 1 + it.Parts.Count; return n; }
    }

    public override RectF AffectedBefore => Paint.IsEmpty ? EditRegion.Of(Items.Select(it => it.Original)) : Paint;
    public override RectF AffectedAfter => Paint.IsEmpty ? EditRegion.Of(Items.SelectMany(it => it.Parts)) : Paint;

    public override void Undo(InkDocument doc) => Apply(doc, restored: true);
    public override void Redo(InkDocument doc) => Apply(doc, restored: false);

    private void Apply(InkDocument doc, bool restored)
    {
        // 先把本批涉及的都拿出来（按引用删，不看下标）。
        foreach (var it in Items)
        {
            doc.RemoveStroke(it.Original);
            for (int i = 0; i < it.Parts.Count; i++) doc.RemoveStroke(it.Parts[i]);
        }

        // 再按下标升序放回去。插入点 = 原下标 - 已经处理过的条数 + 前面已经放回去的笔画数
        // （登记时用的是同一套下标：拆分那一步是**按下标降序**逐条处理的，前面的处理不会
        // 动到后面还没处理的下标）。
        var order = Items.OrderBy(it => it.Index).ToList();
        int handled = 0, placed = 0;
        foreach (var it in order)
        {
            int pos = it.Index - handled + placed;
            if (restored)
            {
                doc.InsertStroke(pos, it.Original);
                // 老存档里的区间要跟着还原（新擦的笔迹 Before 是空的）。
                doc.SetErased(it.Original, it.Before, Paint.IsEmpty ? null : Paint);
                placed += 1;
            }
            else
            {
                for (int i = 0; i < it.Parts.Count; i++) doc.InsertStroke(pos + i, it.Parts[i]);
                placed += it.Parts.Count;
            }
            handled++;
        }
    }
}

internal sealed class ClearAction : EditAction
{
    public readonly List<Stroke> Removed = new();
    public override int HeldStrokes => Removed.Count;

    /// <summary>
    /// 撤销"清空"：**按原位置顺序插回去**（不再是 AppendStroke 堆到最上层）。
    ///
    /// 为什么（2026-10-04 对照上游后改）：`AppendStroke` 会把整批放到最上层，
    /// 如果清空之后又写过新笔画，撤销后旧墨压在它们的上面——半透明叠色和清空前
    /// 不一样，"撤销 = 回到原样"就不成立了。上游（Rnote 保留 render comp、
    /// MyPaint 用 COW 快照）都不改 z 序。插中间会让分块走"结构变了、整块重画"，
    /// 正确，代价有界（只碰这些笔画覆盖的块）。
    /// </summary>
    public override void Undo(InkDocument doc)
    {
        for (int i = 0; i < Removed.Count; i++) doc.InsertStroke(i, Removed[i]);
    }

    public override void Redo(InkDocument doc) { doc.ClearStrokes(); }
    public override RectF AffectedBefore => EditRegion.Of(Removed);
}

/// <summary>
/// 「清除全部墨迹」在**分页文档**（PPT）里的那一步：把除 0 号页（桌面/板书）以外的
/// **所有页**一次清掉，**一步撤销**能整份回来。
///
/// 为什么单独一个动作：撤销栈本来就是**按页**的（每个页槽一份 `Undo`），
/// 而"一份演示文稿的所有页"跨多个页槽——只能自己记住每一页的原内容，
/// 撤销时逐页装回去（走动 <see cref="InkDocument.LoadPageContent"/>）。
///
/// 2026-10-14 定的是“可撤销”；2026-10-25 又把入口拆开：
/// **只有翻页条菜单**还走这个整份清空；橡皮带里的「清空墨迹」只清当前页
/// （见 <see cref="InkDocument.ClearRect"/>），两者都必须可撤销。
/// </summary>
internal sealed class ClearSlidesAction : EditAction
{
    /// <summary>页号 → 被清掉的（原顺序）对象表。</summary>
    public readonly Dictionary<int, List<Stroke>> Removed = new();

    public override int HeldStrokes
    {
        get { int n = 0; foreach (var l in Removed.Values) n += l.Count; return n; }
    }

    /// <summary>脏区自己管：走的是"每一页"，逐页由 LoadPageContent / InvalidateAll 负责。</summary>
    public override bool SelfManagesDirty => true;

    public override void Undo(InkDocument doc)
    {
        foreach (var kv in Removed) doc.LoadPageContent(kv.Key, kv.Value, 0);
        doc.InvalidateAll();
    }

    public override void Redo(InkDocument doc)
    {
        foreach (var key in Removed.Keys) doc.LoadPageContent(key, new List<Stroke>(), 0);
        doc.InvalidateAll();
    }
}

/// <summary>
/// 对一组对象施加同一个变换。移动 / 缩放 / 旋转 / 镜像**都是它**，不是四套代码。
///
/// 只改矩阵、不碰几何，所以：
///   · 代价是 O(对象数)，**与每一笔有多少个点无关**；
///   · 撤销就是乘逆矩阵；
///   · 几何缓存完全不用失效（缓存的是局部坐标下的几何）。
///
/// 逆矩阵现算而不存一份：省内存，而且"改变换"本来就是可逆运算，不需要额外状态。
/// </summary>
/// <summary>
/// 给一批对象改**一个属性**（颜色 / 粗细 / 锁定）。三条共用一个动作：
/// 都是"记下旧值 → 改 → 撤销时写回"，差别只在写哪个字段。
///
/// 脏区按"改前 ∪ 改后"取并集（粗细变了墨会往外长一圈），和 TransformObjectsAction
/// 同一个模式；改完置 StructureChangedSinceRender——内容层的像素真的变了，
/// 不能走"只补画新笔画"那条快路径。
/// </summary>
internal sealed class SetStrokePropAction : EditAction
{
    public enum Prop { Color, Width, Lock, Dash, Grid }

    private readonly Stroke[] _targets;
    private readonly Prop _prop;
    private readonly Color4[] _oldColor;
    private readonly Color4[] _newColor;      // 每条各自的"新颜色"（荧光笔要转半透明，见下）
    /// <summary>改色前的逐行墨色（识别到文本框）；手动改色 = 覆盖逐行原色，撤销时还原。</summary>
    private readonly List<Color4>[] _oldLineColors;
    private readonly List<TextBoxColorSpan>[] _oldColorSpans;
    private readonly float[] _oldWidth;
    private readonly bool[] _oldLock;
    private readonly bool[] _oldGrid;
    private readonly StrokeDash[] _oldDash;
    private float _width;                     // 拖动中会被 RetargetWidth 改（8.2.0）
    private readonly bool _lock;
    private readonly bool _grid;
    private readonly StrokeDash _dash;
    private readonly RectF _before;

    /// <summary>
    /// 改颜色。**新颜色按条给**（不是统一一个值）：荧光笔要保留半透明
    /// （`InkPalette.ToHighlighter`），普通墨迹用不透明的基色——同一批里两种笔混着选时，
    /// 一个色值套不下去。
    /// </summary>
    public SetStrokePropAction(IReadOnlyList<Stroke> targets, Color4[] newColors)
        : this(targets)
    {
        _prop = Prop.Color;
        _oldColor = new Color4[_targets.Length];
        _newColor = new Color4[_targets.Length];
        _oldLineColors = new List<Color4>[_targets.Length];
        _oldColorSpans = new List<TextBoxColorSpan>[_targets.Length];
        for (int i = 0; i < _targets.Length; i++)
        {
            _oldColor[i] = _targets[i].Color;
            _newColor[i] = i < newColors.Length ? newColors[i] : newColors[^1];
            if (_targets[i].TextBox is { Role: TextBoxRole.Normal } box)
            {
                _oldLineColors[i] = new List<Color4>(box.LineColors);
                _oldColorSpans[i] = new List<TextBoxColorSpan>(box.ColorSpans);
            }
        }
    }

    public SetStrokePropAction(IReadOnlyList<Stroke> targets, float width)
        : this(targets) { _prop = Prop.Width; _width = width; _oldWidth = new float[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldWidth[i] = _targets[i].Width; }

    public SetStrokePropAction(IReadOnlyList<Stroke> targets, bool locked)
        : this(targets) { _prop = Prop.Lock; _lock = locked; _oldLock = new bool[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldLock[i] = _targets[i].Locked; }

    /// <summary>
    /// 改线型（<see cref="StrokeDash"/>）。
    /// **按条记旧值**：一批里可能实线/虚线混着选，撤销时要各自回到各自的原样。
    /// </summary>
    public SetStrokePropAction(IReadOnlyList<Stroke> targets, StrokeDash dash)
        : this(targets) { _prop = Prop.Dash; _dash = dash; _oldDash = new StrokeDash[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldDash[i] = _targets[i].Dash; }

    /// <summary>
    /// 改**坐标系网格**开关（见 <see cref="Stroke.Grid"/>）。
    ///
    /// **为什么要多传一个 <paramref name="tag"/>**：C# 的重载只看参数类型，
    /// 而"锁定"那条也是 `(targets, bool)`——两个语义撞在同一个签名上，编译器分不出来。
    /// 传一个哑参数逼调用方把意图写清楚，免得"点了一下网格，结果对象被锁上了"。
    /// </summary>
    public SetStrokePropAction(IReadOnlyList<Stroke> targets, bool grid, Prop tag)
        : this(targets) { _prop = tag; _grid = grid; _oldGrid = new bool[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldGrid[i] = _targets[i].Grid; }

    private SetStrokePropAction(IReadOnlyList<Stroke> targets)
    {
        _targets = new Stroke[targets.Count];
        for (int i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _before = EditRegion.Of(_targets);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => EditRegion.Of(_targets);

    /// <summary>这一批里有几个对象（控制台留痕 / 自检用）。</summary>
    public int TargetCount => _targets.Length;

    /// <summary>
    /// **拖动中更新目标粗细**（8.2.0 面板滑条：同一个动作反复 `Redo`、松手才提交——
    /// 「一次拖拽 = 一步撤销」）。用同一个动作对象，才能保证撤销回到**按下那一刻**的值。
    /// </summary>
    public void RetargetWidth(float width) => _width = width;

    /// <summary>
    /// **拖动中更新目标颜色**（自定义取色板用，同 <see cref="RetargetWidth"/> 那一套）。
    /// **按条重算**：荧光笔要保留半透明（见 `InkPalette.ForStroke`），不能一个色值套下去。
    /// </summary>
    public void RetargetColor(Color4 baseColor)
    {
        for (int i = 0; i < _targets.Length; i++)
            _newColor[i] = InkPalette.ForStroke(baseColor, _targets[i]);
    }

    /// <summary>
    /// 这个动作**真的会改变什么吗**（拖拽松手时用它决定要不要进撤销栈：
    /// 按下去没动、或者选的就是同一个值，不该留一步空撤销）。
    /// </summary>
    public bool HasChange
    {
        get
        {
            switch (_prop)
            {
                case Prop.Width:
                    foreach (float old in _oldWidth)
                        if (MathF.Abs(old - _width) > 0.001f) return true;
                    return false;
                case Prop.Color:
                    for (int i = 0; i < _targets.Length; i++)
                        if (!_oldColor[i].Equals(_newColor[i])) return true;
                    return false;
                default:
                    return true;
            }
        }
    }

    public override void Redo(InkDocument doc) => Apply(doc, old: false);
    public override void Undo(InkDocument doc) => Apply(doc, old: true);

    private void Apply(InkDocument doc, bool old)
    {
        for (int i = 0; i < _targets.Length; i++)
        {
            var s = _targets[i];
            doc.Dirty.Add(s.PaddedBounds);                 // 旧样子要擦
            switch (_prop)
            {
                case Prop.Color:
                    s.Color = old ? _oldColor[i] : _newColor[i];
                    // 普通文本框：公式是位图，改色必须重排重烘；文字本来就跟 s.Color 走。
                    if (s.TextBox is { Role: TextBoxRole.Normal } box)
                    {
                        box.Ink = s.Color;
                        // 手动改色覆盖逐行/逐字原色；撤销时把识别结果的红/蓝行还原。
                        box.LineColors.Clear();
                        box.ColorSpans.Clear();
                        if (old && _oldLineColors?[i] is { } saved)
                            box.LineColors.AddRange(saved);
                        if (old && _oldColorSpans?[i] is { } savedSpans)
                            box.ColorSpans.AddRange(savedSpans);
                        box.InvalidateLayout();
                        s.SyncTextBoxBounds();
                        doc.RefreshStrokeGrid(s);
                    }
                    break;
                case Prop.Width: s.Width = old ? _oldWidth[i] : _width; break;
                case Prop.Lock:  s.Locked = old ? _oldLock[i] : _lock; break;
                case Prop.Dash:  s.Dash = old ? _oldDash[i] : _dash; break;
                case Prop.Grid:  s.Grid = old ? _oldGrid[i] : _grid; break;
            }
            // 颜色/粗细会改墨迹范围（半宽），必须让 InkBounds 的缓存失效；
            // **网格也会**：它改的是形状本身（多画一堆线），几何得重建；
            // 锁定不改外观、线型也不改形状（只改"怎么描这条边"），这两样不用重算。
            if (_prop is Prop.Color or Prop.Width or Prop.Grid) s.InvalidateMetrics();
            doc.Dirty.Add(s.PaddedBounds);                 // 新样子要画
        }
        doc.StructureChangedSinceRender = true;
        doc.Version++;
    }
}

/// <summary>
/// 层级：把选中的这一批整体移到**最上**（置顶）或**最下**（置底），内部相对顺序不变。
/// 只做这两个动作，不做"上移/下移一格"（见 调研-选中框-反馈-导出-层级-属性.md 第三节）。
/// 撤销 = 按原下标插回去。
/// </summary>
/// <summary>
/// 图片美化：把一条图片对象换成/换回另一份 <see cref="ImageData"/>。
/// 旧图仍留在撤销栈里（撤销能回原图）；非活动那份只释放 D2D 位图，Bgra 还留着。
/// </summary>
internal sealed class ReplaceImageAction : EditAction
{
    private readonly Stroke _stroke;
    private readonly ImageData _old;
    private readonly ImageData _new;

    public ReplaceImageAction(Stroke stroke, ImageData oldImage, ImageData newImage)
    {
        _stroke = stroke;
        _old = oldImage;
        _new = newImage;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _stroke.PaddedBounds;
    public override RectF AffectedAfter => _stroke.PaddedBounds;

    public override void Undo(InkDocument doc) => Apply(doc, _old, _new);
    public override void Redo(InkDocument doc) => Apply(doc, _new, _old);

    private void Apply(InkDocument doc, ImageData image, ImageData release)
    {
        doc.Dirty.Add(_stroke.PaddedBounds);
        _stroke.Image = image;
        release?.Release();
    }
}

internal sealed class ReorderStrokesAction : EditAction
{
    private readonly Stroke[] _targets;
    private readonly bool _toFront;
    private readonly List<(int index, Stroke s)> _old = new();
    private readonly RectF _bounds;

    public ReorderStrokesAction(IReadOnlyList<Stroke> targets, bool toFront)
    {
        _toFront = toFront;
        _targets = new Stroke[targets.Count];
        for (int i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _bounds = EditRegion.Of(_targets);
    }

    public override RectF AffectedBefore => _bounds;
    public override RectF AffectedAfter => _bounds;

    public override void Redo(InkDocument doc)
    {
        _old.Clear();
        foreach (var s in _targets) _old.Add((doc.Strokes.IndexOf(s), s));
        _old.Sort((a, b) => a.index.CompareTo(b.index));

        foreach (var it in _old) doc.Strokes.Remove(it.s);
        if (_toFront) { foreach (var it in _old) doc.Strokes.Add(it.s); }
        else { for (int i = _old.Count - 1; i >= 0; i--) doc.Strokes.Insert(0, _old[i].s); }
        Touch(doc);
    }

    public override void Undo(InkDocument doc)
    {
        foreach (var it in _old) doc.Strokes.Remove(it.s);
        foreach (var it in _old)                       // 从小到大插，下标才不会互相挤
            doc.Strokes.Insert(Math.Min(it.index, doc.Strokes.Count), it.s);
        Touch(doc);
    }

    private void Touch(InkDocument doc)
    {
        // 顺序变了：内容层"只差几条新笔画"的前提不成立，碰到的块要整块重画。
        doc.StructureChangedSinceRender = true;
        doc.Version++;
        doc.Dirty.Add(_bounds);                        // 重叠的那些像素才可能变
    }
}

internal sealed class TransformObjectsAction : EditAction
{
    private readonly Stroke[] _targets;
    /// <summary>累计增量。2026-10-05 起**可变**：连续微调会话把新一步叠上去（见 TryAppendDelta），
    /// 于是"按住方向键 / 连按缩放"只留一条撤销记录。</summary>
    private Matrix3x2 _delta;
    private readonly RectF _before;
    /// <summary>最近一次落到文档上的版本号（见 TryAppendDelta）。</summary>
    private int _lastVersion;

    public TransformObjectsAction(IReadOnlyList<Stroke> targets, Matrix3x2 delta)
    {
        _targets = new Stroke[targets.Count];
        for (int i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _delta = delta;
        _before = EditRegion.Of(_targets);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => EditRegion.Of(_targets);

    public override void Redo(InkDocument doc)
    {
        Shift(doc, _delta);
        _lastVersion = doc.Version;
    }

    public override void Undo(InkDocument doc)
    {
        // 退化矩阵（比如缩放成 0）不可逆，那就什么都不做，别把对象搞成 NaN。
        if (!Matrix3x2.Invert(_delta, out var inv)) return;
        Shift(doc, inv);
    }

    private void Shift(InkDocument doc, in Matrix3x2 m)
    {
        foreach (var s in _targets) doc.ApplyTransformCore(s, m);
        doc.Version++;
    }

    /// <summary>会话里的目标还是不是这一批（顺序不论；按引用比）。</summary>
    internal bool SameTargets(IReadOnlyList<Stroke> list)
    {
        if (list.Count != _targets.Length) return false;
        foreach (var t in _targets)
        {
            bool hit = false;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], t)) { hit = true; break; }
            if (!hit) return false;
        }
        return true;
    }

    /// <summary>
    /// **连续微调会话**：把新一步的增量叠在已有动作后面（不新开撤销记录）。
    /// 返回 false = 这中间文档被别的动作改过（版本号对不上），调用方应当另开一条——
    /// 否则"撤销这一条"会把中间那些改动一起算进去，数学上就不对了。
    /// </summary>
    internal bool TryAppendDelta(InkDocument doc, in Matrix3x2 m)
    {
        if (doc.Version != _lastVersion) return false;
        Shift(doc, m);
        _delta = _delta * m;              // 行向量约定：先老增量、后新增量
        _lastVersion = doc.Version;
        // 这条动作**没有**走 Commit（不是新记录），脏区得自己加；口径和 Commit 一致。
        doc.Dirty.Add(AffectedUnion);
        return true;
    }
}

/// <summary>
/// **改几何**：把一条图形的控制点整批换掉（这一轮只服务"拖直线 / 箭头的端点"）。
///
/// 和 <see cref="TransformObjectsAction"/> 是**两条不许混的路**（见 硬约束）：
///   · 那边只改 `Transform` 矩阵，几何一个点都不动 → GPU 几何缓存不用失效；
///   · 这边改的是几何本身 → `Revision` 跟着变，Direct2D 几何缓存**必须**重建，
///     空间索引也要重摆（端点挪走了，它占的格子变了）。
///
/// 三个实现上的要点（都在 计划-图形工具.md 8.1）：
///   ① 拖动期不碰模型，只有**松手**才构造并应用这条动作 → 一次拖拽 = 一步撤销；
///   ② 脏区加"旧位、新位**各一个**矩形"，不用并集（见 SelfManagesDirty 的注释）；
///   ③ 端点存的是**局部坐标**，所以外面提交之前要先过 Transform⁻¹（见 Selection.ToLocalPoint）。
/// </summary>
internal sealed class GlyphEraseAction : EditAction
{
    private readonly Stroke _target;
    private readonly List<InkPoint> _oldPoints;
    private readonly List<InkPoint> _newPoints;
    private readonly RectF _before;
    private readonly RectF _after;

    public override bool SelfManagesDirty => true;

    public GlyphEraseAction(Stroke target, IReadOnlyList<InkPoint> newPoints)
    {
        _target = target;
        _oldPoints = new List<InkPoint>(target.Points);
        _newPoints = new List<InkPoint>(newPoints ?? Array.Empty<InkPoint>());
        _before = target.PaddedBounds;
        var after = RectF.Empty;
        foreach (var p in _newPoints) after.Add(p.X, p.Y);
        _after = after.IsEmpty ? _before : after.Inflate(2f);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => _after;

    public override void Undo(InkDocument doc) => Apply(doc, _oldPoints);
    public override void Redo(InkDocument doc) => Apply(doc, _newPoints);

    private void Apply(InkDocument doc, List<InkPoint> pts)
    {
        doc.ApplyGlyphPoints(_target, pts);
        doc.Dirty.Add(_before);
        doc.Dirty.Add(_after);
    }
}

internal sealed class SetStrokeGeometryAction : EditAction
{
    private readonly Stroke _target;
    private readonly Vector2[] _newPoints;
    private readonly Vector2[] _oldPoints;
    /// <summary>焦点三角形顶点 P 的参数角（旧 / 新）。`null` = 这次改几何与它无关。</summary>
    private readonly float? _oldFocusU;
    private readonly float? _newFocusU;
    /// <summary>坐标系网格的格距（旧 / 新）。`null` = 这次改几何与它无关（见构造函数的注释）。</summary>
    private readonly float? _oldGridStep;
    private readonly float? _newGridStep;
    private readonly RectF _before;
    private readonly RectF _after;

    /// <summary>脏区自己管：要两个矩形，不要并集（见 EditAction.SelfManagesDirty）。</summary>
    public override bool SelfManagesDirty => true;

    /// <param name="newFocusU">
    /// 拖**焦点三角形的顶点 P** 时那个新的参数角；别的改几何动作传 `null`（默认）。
    /// **为什么用可空的 float、不拿 `NaN` 当"没有"**：`NaN` 本身是 P 的一个**合法值**
    ///（= "还没拖过、按短半轴那一端自动摆"），两种含义挤在一个数里，
    /// 撤销那一路就会把"该恢复成 NaN"误判成"这次不关它的事"——
    /// 表现是"拖完 P 按撤销，椭圆回去了、P 还停在新位置"（自检当场抓到过）。
    /// </param>
    /// <param name="newGridStep">
    /// 拖**坐标系的格距手柄**时那个新的格距（局部坐标）；别的改几何动作传 `null`（默认）。
    /// 理由和 P 一模一样：**`0` 是格距的一个合法值**（= 自动，见 <see cref="Stroke.AxisGridStep"/>），
    /// 拿它当"没有"就会"撤销回不到自动那一档"。
    /// </param>
    public SetStrokeGeometryAction(Stroke target, IReadOnlyList<Vector2> newPoints,
                                   float? newFocusU = null, float? newGridStep = null)
    {
        _target = target;
        _newPoints = new Vector2[newPoints.Count];
        for (int i = 0; i < newPoints.Count; i++) _newPoints[i] = newPoints[i];
        _oldPoints = LocalPoints(target);
        _newFocusU = newFocusU;
        // 旧值**永远要记**（哪怕是个 NaN）：撤销时要原样写回去。
        _oldFocusU = newFocusU.HasValue ? target.FocusPointU : null;
        _newGridStep = newGridStep;
        _oldGridStep = newGridStep.HasValue ? target.AxisGridStep : null;
        // **两个包围盒都在动手之前算**：改完之后旧位置就再也问不出来了。
        // 平行四边形的第四个顶点不在点表里，靠 kind 让它现推（脏区不能漏它）；
        // 曲线还要多传一个**朝向**（抛物线开哪个口 / 双曲线哪条是实轴）——
        // 朝向变了也是"改几何"，那条路同样要从这里拿框。
        _before = Stroke.PaddedBoundsOf(_oldPoints, target.Transform, target.Width, target.Kind, target.CurveAxis);
        _after = Stroke.PaddedBoundsOf(_newPoints, target.Transform, target.Width, target.Kind, target.CurveAxis);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => _after;

    public override void Undo(InkDocument doc) => Apply(doc, _oldPoints, _oldFocusU, _oldGridStep);
    public override void Redo(InkDocument doc) => Apply(doc, _newPoints, _newFocusU, _newGridStep);

    private void Apply(InkDocument doc, Vector2[] pts, float? focusU, float? gridStep)
    {
        doc.ApplyGeometryCore(_target, pts);
        // P 的位置：这一拖带了它就写它（**该是 NaN 就写 NaN**，见构造函数的注释）；没带就一个字不动。
        // **框不用重算**：P 在椭圆上、两个焦点在椭圆里，所以"P 动"不会把紧框撑出去
        // （`_before` / `_after` 那份已然覆盖了整条椭圆）。
        if (focusU.HasValue) _target.SetConicEllipsePointAngle(focusU.Value);
        // 格距：同一个套路（`0` = 自动，是个合法值，所以"带没带"只能看可空）。
        // **框也不用重算**：网格线一律夹在外框里（见 AxisGridSegments），格距再大也撑不出框。
        if (gridStep.HasValue) _target.AxisGridStep = gridStep.Value;
        doc.Dirty.Add(_before);                 // 旧位要擦
        doc.Dirty.Add(_after);                  // 新位要画（**分开两个矩形**，见类注释 ②）
    }

    /// <summary>把对象当前的控制点抄成局部坐标数组（撤销要原样放回去）。</summary>
    private static Vector2[] LocalPoints(Stroke s)
    {
        var pts = new Vector2[s.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return pts;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// 交互矢量图参数拖动（一次拖动 = 一步撤销）。只改 <see cref="Stroke.Figure"/> 的
/// 参数默认值，不碰曲线采样/点坐标；Undo/Redo 用旧/新 JSON 整份换回。
/// </summary>
internal sealed class SetFigureParamsAction : EditAction
{
    private readonly Stroke _target;
    private readonly string _oldJson;
    private readonly string _newJson;
    private readonly RectF _bounds;

    public override bool SelfManagesDirty => true;

    public SetFigureParamsAction(Stroke target, string oldJson, string newJson)
    {
        _target = target;
        _oldJson = oldJson ?? "";
        _newJson = newJson ?? "";
        _bounds = target.PaddedBounds;
    }

    public override void Undo(InkDocument doc) => Apply(doc, _oldJson);
    public override void Redo(InkDocument doc) => Apply(doc, _newJson);

    private void Apply(InkDocument doc, string json)
    {
        _target.Figure = PlotSpecJson.Read(json);
        _target.InvalidateMetrics();
        doc.Dirty.Add(_bounds);
        doc.StructureChangedSinceRender = true;
        doc.Version++;
    }
}

// ---------------------------------------------------------------------------

