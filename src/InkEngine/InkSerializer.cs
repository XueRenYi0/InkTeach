using System.Numerics;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>画布块的种类。见计划文档第十二节：冻结截图 / 白板 / 翻页是同一个东西。</summary>
internal enum BlockKind : byte
{
    /// <summary>空白（白板或黑板，底色由文档决定）。</summary>
    Blank = 0,
    /// <summary>冻结的屏幕截图。</summary>
    Screenshot = 1,
}

/// <summary>一块画布。现在是"一页"，将来是纵向序列里的一块。</summary>
internal struct CanvasBlock
{
    public BlockKind Kind;
    /// <summary>这块画布在画布坐标里的范围（纵向排列，所以主要是 Y 在变）。</summary>
    public RectF Area;
    /// <summary>底图引用（文件名或内容哈希）。空白块为空串。</summary>
    public string Source;

    public static CanvasBlock Default => new()
    {
        Kind = BlockKind.Blank,
        Area = new RectF { MinX = 0, MinY = 0, MaxX = 0, MaxY = 0 },
        Source = "",
    };
}

/// <summary>
/// 文档的序列化。**同一份字节既是保存文件，也是剪贴板的私有格式**——
/// 于是"保存"和"复制粘贴"共用一套代码，粘回批注里还是可编辑的对象而不是一张图片。
///
/// 为什么是自定义二进制而不是 JSON：
///   ① 体积和速度。一堂课五百笔约 400 KB，JSON 要两三兆而且慢；
///   ② 不引依赖。System.Text.Json 的反射序列化路径在 NativeAOT 下是雷
///      （见计划文档第二十六节），要用它就得再来一套 source generator；
///   ③ 这个格式将来要直接塞进剪贴板，字节数组最省事。
///
/// **存的就是原始采样点**：笔迹长什么样完全由采样点 + 笔宽决定
/// （引擎只有一条画法：中心线 + D2D 原生描边），
/// 所以文件里不需要、也不该存任何"渲染结果"。
/// </summary>
internal static class InkSerializer
{
    /// <summary>文件头魔数。文件坏了 / 拿错文件时能立刻认出来，不用等解析到一半。</summary>
    private static readonly byte[] Magic = { (byte)'I', (byte)'N', (byte)'K', (byte)'B' };

    /// <summary>
    /// 格式版本。**读的时候必须按它分支**：以后加字段就升版本，老文件永远能读。
    /// 否则用户存了一学期的批注会因为一次升级全部打不开。
    /// </summary>
    /// <summary>
    /// 格式版本。读的时候按版本分支，老文件永远能读。
    ///   · v1：最初的样子；
    ///   · v2：对象可以带**图像像素**（截图 / 粘贴的图）；
    ///   · v3：去掉了每条笔画的"笔锋预设"字节（手写美化整层已删除，
    ///         但读 v1/v2 时仍要把那个字节消费掉，否则后面的字段会错位）。
    ///   · v4：每条笔画多一段**擦除区间表**（像素橡皮擦掉了哪几段，见 Stroke.Erased）。
    ///         这是"一条笔迹上记区间"而不是把笔迹拆成几个对象的关键——
    ///         存下来之后，一块被擦掉中间一段的板书重开还是**一条**笔迹。
    ///   · v5：每条笔画多一个**锁定标记**（见 Stroke.Locked）。老文件（≤ v4）读进来
    ///         一律"不锁"，所以版本闸只往上抬、不需要迁移代码。
    ///   · v6：新增**圆**这个图形种类（`StrokeKind.Circle`，2026-09-19）。
    ///         它和直线同构（`Points` = 圆心 + 圆周点），**格式本身没变**——
    ///         `Kind` 一直是一个字节，多一个取值不需要加字段。
    ///         升版本是因为：含圆的文件在 v5 的老程序里会画不出来（它不认识 6），
    ///         抬一版让老程序直接说"请升级程序"，比让它画出个四不像好。
    ///   · v7：新增**三角形 / 平行四边形**（2026-09-19 第二批第②步）。
    ///         同样**格式本身没变**：三角形是三个顶点、平行四边形是三个顶点
    ///         （第四个现推），都只是 `Points` 里多一个点和一个新的 `Kind` 取值。
    ///         升版本的理由和 v6 一模一样：老程序不认识 7 / 8，会画出个残缺的形状。
    ///         **读老文件不受影响**（≤ v6 的文件里不会出现这两个 Kind）。
    ///   · v8：每条笔画多一个**线型**字节（实线 / 虚线 / 点线，见 StrokeDash）。
    ///         这是 v5 之后**第一次真的往每条笔画后面追加字段**——所以读的时候
    ///         必须卡在 `version >= 8` 上，否则读 v7 的文件会去读下一个字节（越界/错位）。
    ///         老文件（≤ v7）读进来一律"实线"，正是它们本来的样子，不需要迁移代码。
    ///   · v9：新增**坐标系 / 数轴**两个种类，每条笔画再多一个**"坐标系网格"字节**
    ///         （见 Stroke.Grid；别的种类恒 0）。网格为什么跟着对象走、
    ///         而不是做成一个全局开关，见 Stroke.Grid 的注释——一句话：对象要自包含，
    ///         存到文件 / 复制粘贴 / 发给别人之后，样子都不该变。
    ///         同样是**只在 `version >= 9` 时读那一位**，老文件的闸门错一档就整体错位。
    ///   · v10：每条笔画多一个**"这一笔有真实压感"标记**（见 Stroke.HasPressure，2026-09-20）。
    ///         存它是因为渲染要按它分流：有压感 + 实线走 D2D 原生变宽墨迹，没有就走等宽描边。
    ///         不存的话会出现最难解释的一种 bug——**当场看着是好的，存盘重开就变等宽了**
    ///         （压力值本来就逐点存着，所以只差这一位"设备报没报压力"）。
    ///         老文件（≤ v9）读进来一律 false = 等宽，正是它们本来的样子。
    ///   · v11：新增**四种曲线**（抛物线 / 双曲线 / 正弦 / 余弦，`StrokeKind` 11~14），
    ///         并且每条笔画多一个**"曲线朝向"字节**（见 <see cref="Stroke.CurveAxis"/>；
    ///         别的种类恒 0）。
    ///         加字段的理由和 v8 / v9 一样：只在 `version >= 11` 时读那一位，
    ///         老文件少读一位就会整体错位。
    ///         升版本本身的理由和 v6 / v7 一样：老程序不认识 11~14 这几个 `Kind`，
    ///         会画出个残缺的形状（抛物线画成一条线），直接说"请升级"比装作看得懂好。
    ///         **读老文件不受影响**（≤ v10 的文件里不会出现这几个 Kind，也没有那一位）。
    ///   · v12：双曲线多一个**"画不画渐近线"字节**（<see cref="Stroke.ShowAsymptotes"/>，
    ///         默认画：课本上双曲线就是要配两条虚线渐近线）。
    ///         加字段的理由同 v11；升版本的理由同 v6 / v7——v11 的程序读到 v12 的
    ///         文件不会报错，但它会把渐近线那一位当成下一条笔画的起点，
    ///         **整个文件从第一条之后全错位**，所以必须说"请升级"。
    ///   · v13：抛物线的**第二个点换了含义**（用户 2026-09-20 的口径："先设定开口，
    ///         再用两点画出来"）：老文件里它是"**张口点**"（落在对称轴上）、
    ///         新写法里它是"**曲线经过的一个点**"（`p` 由它反解）。
    ///         **这一版没往笔画后面加任何字节**，格式长度和 v12 一样——
    ///         但**必须升版本号**：v12 的程序读到 v13 的文件不会报错，
    ///         却会把新含义的点当成"张口点"，画出一条**又短又瘪**的抛物线，
    ///         而且看不出哪里错了（比"报错"难查得多）。
    ///         读老文件时见下面的迁移（`version &lt;= 12` 的抛物线要把那个点换掉）。
    ///   · v14：双曲线**把一个点拆成两个**（用户 2026-09-20 深夜："渐近线画好以后大小完全
    ///         不动，长度也不动……我画图的时候感觉很不适应"）：老写法里"渐近线框"和
    ///         "曲线半轴"是同一件事（渐近线画到 `±2a、±2b`），所以第二个点一变、
    ///         渐近线就跟着变；新写法把它拆成**渐近线框（第二个点，锁定后不动）**
    ///         ＋ **曲线经过的点（第三个点）**。
    ///         同样**没加字节**（`Points` 本来是变长的，多用一格而已），
    ///         同样必须升版本——老程序会把第三个点当成"多出来的怪点"。
    ///         读老文件时见下面的迁移（补第三个点）。
    ///   · v15：新增两种**立体图形**（圆柱 / 圆锥，用户 2026-09-20："
    ///         他那边的立体图形也可以搬过来了"）。`Kind` 是多出来的两个取值
    ///         （15 / 16），**没加字段、也没改任何已有字节**。
    ///         升版本的理由同前面几条：老程序读到这两个值会当成"不认识的种类"
    ///         掉进自由笔迹那条兜底（画出来是一坨线），不如直接让它说"请升级"。
    ///         读老文件**不需要迁移**（老文件里本来就没有这两种）。
    ///   · v16：同上，再加两种**立体图形**（长方体 / 四面体，取值 17 / 18）。
    ///         同样没加字节、同样不用迁移。
    ///   · v17：新增**棱柱**（取值 19），并且每条笔画多一个
    ///         **"底面几边形"字节**（见 <see cref="Stroke.PrismSides"/>；别的种类恒 4）。
    ///         加字段的理由同 v8 / v9 / v11：**必须卡在 `version >= 17` 上读那一位**，
    ///         否则读 v16 的文件会去读下一个字节（整体错位）。
    ///         升版本的理由同 v6 / v7 / v15：老程序不认识 19 这个 `Kind`。
    ///         读老文件**不需要迁移**（≤ v16 的文件里既没有棱柱、也没有那一位）。
    ///   · v18：新增**棱锥 / 棱台**（取值 20 / 21）。`Kind` 多两个取值，
    ///         **没加任何字节**——它们和棱柱是同一族，共用 v17 就已经有的那个
    ///         "底面几边形"字节（写端本来就是无条件写的，所以这里一行都不用改）。
    ///         升版本的理由同前面几条：老程序读到 20 / 21 会当成"不认识的种类"
    ///         掉进自由笔迹那条兜底（画出来是一坨线），不如直接让它说"请升级"。
    ///         读老文件**不需要迁移**（老文件里本来就没有这两种）。
    ///   · v19：新增**圆台**（取值 22）。同样**没加任何字节**（它是一笔画完的旋转体，
    ///         和圆柱 / 圆锥一样只有两个控制点），升版本的理由也同上。
    ///         读老文件同样**不需要迁移**。
    ///   · v20：新增**球**（取值 23）。同样没加字节、同样不用迁移
    ///         （和圆台一样是一笔画完的旋转体）。
    ///   · v21：新增**正切**（取值 24）。同样没加字节、同样不用迁移
    ///         （它和正弦 / 余弦一样是两个控制点：起手点 ＋ 拖出去那个角点）。
    ///
    /// ⚠ **面板入口可以撤，`Kind` 的取值一个都不许删**（2026-09-20 第十二批撤了长方体 /
    /// 四面体的入口）：存档里存的是**一个字节**，删了就是"打开旧板书少一条"
    /// （同 2026-09-19 撤「数轴」入口那条规矩，见 计划-图形工具.md 11.2）。
    /// </summary>
    public const int FormatVersion = 21;

    /// <summary>注册到系统的剪贴板格式名（RegisterClipboardFormat）。</summary>
    public const string ClipboardFormatName = "InkTeach.InkObjects";

    /// <summary>建议的文件扩展名。</summary>
    public const string FileExtension = ".inkb";

    // =====================================================================
    //  写
    // =====================================================================

    public static byte[] Save(InkDocument doc)
    {
        using var ms = new MemoryStream(64 * 1024);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(0);                       // flags，留给以后

            // ---- 画布块 ----
            // 现在只写一块，但**按多块的结构写**：以后加页、加白板不用改格式。
            w.Write(doc.Blocks.Count);
            foreach (var b in doc.Blocks)
            {
                w.Write((byte)b.Kind);
                w.Write(b.Area.MinX); w.Write(b.Area.MinY);
                w.Write(b.Area.MaxX); w.Write(b.Area.MaxY);
                w.Write(b.Source ?? "");
            }

            // ---- 对象 ----
            w.Write(doc.Strokes.Count);
            foreach (var s in doc.Strokes) WriteStroke(w, s);
        }
        return ms.ToArray();
    }

    private static void WriteStroke(BinaryWriter w, Stroke s)
    {
        w.Write(s.Id);
        w.Write((byte)s.Tool);
        w.Write((byte)s.Kind);

        w.Write(s.Color.R); w.Write(s.Color.G); w.Write(s.Color.B); w.Write(s.Color.A);
        w.Write(s.Width);

        // 变换：6 个数。**绝对不把变换烘焙进点坐标**——存进去的话，
        // 反复保存/打开的缩放会累积误差，而且"放大再缩小"回不到原样。
        w.Write(s.Transform.M11); w.Write(s.Transform.M12);
        w.Write(s.Transform.M21); w.Write(s.Transform.M22);
        w.Write(s.Transform.M31); w.Write(s.Transform.M32);

        w.Write(s.Points.Count);
        if (s.Points.Count > 0)
        {
            // 时间戳存"相对第一个点的毫秒偏移"，32 位够用：一笔最长也就几分钟。
            // 绝对时间戳要 8 字节，占整条点数据的 40%，而渲染根本不用它。
            double t0 = s.Points[0].T;
            w.Write(t0);
            foreach (var p in s.Points)
            {
                w.Write(p.X);
                w.Write(p.Y);
                w.Write(p.P);
                w.Write((float)(p.T - t0));
            }
        }

        // ---- v2：图像像素 ----
        // 有图就写 1 + 尺寸 + 原始 BGRA。**不压缩**：
        //   · 教室场景里一块截图约 1~3MB，一节课几十张，文件几十兆——可接受；
        //   · 压缩要引编码器（见 ImageData 的注释：核心不引依赖），
        //     而"保存"这条路现在还没有界面在用。等真要用的时候，
        //     格式里已经留好了长度字段，加一层压缩不会破坏兼容。
        w.Write((byte)(s.Image != null ? 1 : 0));
        if (s.Image != null)
        {
            w.Write(s.Image.Width);
            w.Write(s.Image.Height);
            w.Write(s.Image.Bgra.Length);
            w.Write(s.Image.Bgra);
        }

        // ---- v4：擦除区间表 ----
        // 参数 = 点序号（可以带小数），成对写。绝大多数笔画是 0 对，所以只多 4 个字节。
        w.Write(s.Erased.Count);
        foreach (var (a, b) in s.Erased)
        {
            w.Write(a);
            w.Write(b);
        }

        // ---- v5：锁定标记 ----
        w.Write((byte)(s.Locked ? 1 : 0));

        // ---- v8：线型 ----
        // 一个字节，存的就是 <see cref="StrokeDash"/> 的取值（0 实线 / 1 虚线 / 2 点线）。
        // 只有"图形"用得上，自由笔迹恒为 0——**照样写**：写一位比"按 Kind 判断要不要写"
        // 省事得多，而且读的时候不用再复现一遍同样的判断（两处判断迟早会不一致）。
        w.Write((byte)s.Dash);

        // ---- v9：坐标系网格 ----
        w.Write((byte)(s.Grid ? 1 : 0));

        // ---- v10：这一笔有没有真实压感（见 Stroke.HasPressure）----
        // 只写一位，读端卡在 `version >= 10`：老文件没有这一位，读进来是等宽（本来的样子）。
        w.Write((byte)(s.HasPressure ? 1 : 0));

        // ---- v11：曲线的朝向（见 Stroke.CurveAxis）----
        // 一个字节。**现在只有双曲线用得上**（实轴沿 x / 沿 y；抛物线的朝向已由
        // "焦点在顶点哪一侧"现推），别的种类恒 0——照样写，理由和线型那一位一样：
        // 写一位比"按 Kind 判断要不要写"省事，读端也不用再复现一遍同样的判断。
        w.Write((byte)s.CurveAxis);

        // ---- v12：双曲线画不画那两条虚线渐近线（见 Stroke.ShowAsymptotes）----
        // 同样一个字节、同样不按 Kind 判断要不要写。
        w.Write((byte)(s.ShowAsymptotes ? 1 : 0));

        // ---- v17：棱柱底面几边形（见 Stroke.PrismSides）----
        // 一个字节（3~6；别的种类恒 DefaultPrismSides）。读端卡在 `version >= 17`。
        w.Write((byte)s.PrismSidesClamped);
    }

    // =====================================================================
    //  读
    // =====================================================================

    /// <summary>只检查文件头，用来判断"这堆字节是不是我们的格式"（剪贴板上会有别人的数据）。</summary>
    public static bool LooksLikeInk(byte[] data)
    {
        if (data == null || data.Length < 8) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (data[i] != Magic[i]) return false;
        return true;
    }

    /// <summary>
    /// 把字节读进文档（**清空原有内容与撤销历史**——这是一份新文档）。
    ///
    /// 失败要明确：格式不对就抛 <see cref="InvalidDataException"/>，由调用方决定
    /// 怎么提示。而且要**先全部解析成功、再动文档**，否则会留下一个半截的文档。
    /// </summary>
    public static void LoadInto(InkDocument doc, byte[] data)
    {
        if (!LooksLikeInk(data))
            throw new InvalidDataException("不是 InkTeach 的批注数据（文件头不对）。");

        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        r.ReadBytes(Magic.Length);
        int version = r.ReadInt32();
        if (version <= 0 || version > FormatVersion)
            throw new InvalidDataException(
                $"批注数据版本是 {version}，这个程序只认到 {FormatVersion}。请升级程序再打开。");
        r.ReadInt32();                        // flags

        int blockCount = r.ReadInt32();
        if (blockCount < 0 || blockCount > 1_000_000)
            throw new InvalidDataException($"画布块数量不合理：{blockCount}。");

        var blocks = new List<CanvasBlock>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            blocks.Add(new CanvasBlock
            {
                Kind = (BlockKind)r.ReadByte(),
                Area = new RectF
                {
                    MinX = r.ReadSingle(), MinY = r.ReadSingle(),
                    MaxX = r.ReadSingle(), MaxY = r.ReadSingle(),
                },
                Source = ReadString(r),
            });
        }

        int objCount = r.ReadInt32();
        if (objCount < 0 || objCount > 10_000_000)
            throw new InvalidDataException($"对象数量不合理：{objCount}。");

        var strokes = new List<Stroke>(objCount);
        int maxId = 0;
        for (int i = 0; i < objCount; i++)
        {
            var s = ReadStroke(r, version);
            if (s.Id > maxId) maxId = s.Id;
            strokes.Add(s);
        }

        // 走到这里才算读成功——中途抛异常时文档原样不动。
        doc.ReplaceAll(blocks, strokes, maxId);
    }

    private static Stroke ReadStroke(BinaryReader r, int version)
    {
        var s = new Stroke
        {
            Id = r.ReadInt32(),
            Tool = (Tool)r.ReadByte(),
            Kind = (StrokeKind)r.ReadByte(),
        };

        s.Color = new Color4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.Width = r.ReadSingle();
        // v1/v2 在这里还有"笔锋预设"一个字节。那一层已经删了，但**必须读掉**，
        // 否则后面的变换、点数据全部错位——老文件就打不开了。
        if (version < 3) r.ReadByte();

        s.Transform = new Matrix3x2(
            r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle());

        int n = r.ReadInt32();
        if (n < 0 || n > 10_000_000)
            throw new InvalidDataException($"笔画点数不合理：{n}。");
        if (n > 0)
        {
            double t0 = r.ReadDouble();
            for (int i = 0; i < n; i++)
            {
                float x = r.ReadSingle();
                float y = r.ReadSingle();
                float p = r.ReadSingle();
                float dt = r.ReadSingle();
                s.AddPoint(x, y, p, t0 + dt);
            }
        }

        if (version >= 2)
        {
            byte hasImage = r.ReadByte();
            if (hasImage != 0)
            {
                int iw = r.ReadInt32();
                int ih = r.ReadInt32();
                int len = r.ReadInt32();
                // 长度要核对：坏文件里的长度字段会让我们分配几个 G（和 ReadString 同一条教训）。
                if (iw <= 0 || ih <= 0 || len < 0 || (long)iw * ih * 4 != len)
                    throw new InvalidDataException($"图像尺寸与像素长度对不上：{iw}×{ih} / {len}。");
                var pix = r.ReadBytes(len);
                if (pix.Length != len) throw new InvalidDataException("图像像素数据不完整。");
                s.Image = ImageData.Adopt(iw, ih, pix, hasAlpha: true);
            }
        }

        if (version >= 4)
        {
            int nErased = r.ReadInt32();
            if (nErased < 0 || nErased > 1_000_000)
                throw new InvalidDataException($"擦除区间数量不合理：{nErased}。");
            for (int i = 0; i < nErased; i++)
            {
                float a = r.ReadSingle();
                float b = r.ReadSingle();
                if (b < a) (a, b) = (b, a);
                s.Erased.Add((a, b));       // 文件里的表本来就是有序的，原样收下
            }
        }

        // ---- v5：锁定标记（老文件没有这一位，一律"不锁"）----
        if (version >= 5) s.Locked = r.ReadByte() != 0;

        // ---- v8：线型（老文件没有这一位，一律"实线"）----
        // 越界值当实线处理而不是抛异常：线型只是**外观**，一个坏字节不值得让整个文件读不进来。
        if (version >= 8)
        {
            byte d = r.ReadByte();
            s.Dash = d is 1 or 2 ? (StrokeDash)d : StrokeDash.Solid;
        }

        // ---- v9：坐标系网格（老文件没有这一位，一律"不要网格"）----
        if (version >= 9) s.Grid = r.ReadByte() != 0;

        // ---- v10：这一笔有没有真实压感（老文件没有这一位，一律"没有" = 等宽描边）----
        if (version >= 10) s.HasPressure = r.ReadByte() != 0;

        // ---- v11：曲线的朝向（老文件没有这一位，读进来就是字段默认值 0）----
        // 两道处理：
        //   · 越界值退到 0（理由和线型那一位一样：朝向只是"这一条怎么长"，
        //     一个坏字节不该让整个文件读不进来）；
        //   · **再按种类归一**（<see cref="Stroke.NormalizeAxis"/>）。这一条**不分版本**：
        //     `CurveAxis` 是抛物线和双曲线共用的一位，0 对双曲线来说是"另一族"的值——
        //     不归一的话，"一条老文件里的双曲线"会带着 0 进来，几何按"没有定义的那一档"去算
        //     （自检里就是这么抓到的：v10 老文件读进来朝向是 OpenUp）。
        //     `Kind` 在本函数开头已经读过了，所以这里拿得到种类。
        if (version >= 11)
        {
            byte ca = r.ReadByte();
            s.CurveAxis = ca <= (byte)CurveAxis.TransverseY ? (CurveAxis)ca : CurveAxis.OpenUp;
        }
        s.CurveAxis = Stroke.NormalizeAxis(s.Kind, s.CurveAxis);

        // ---- v12：渐近线开关（老文件没有这一位，一律"画"）----
        // 默认值就是 true，所以老文件读进来正好是"像课本那样画出渐近线"。
        if (version >= 12) s.ShowAsymptotes = r.ReadByte() != 0;

        // ---- v17：棱柱底面几边形（老文件没有这一位，一律四棱柱）----
        // 越界值退到默认档（理由同线型 / 朝向那两位：一个坏字节不该让整个文件读不进来）。
        if (version >= 17)
        {
            byte ps = r.ReadByte();
            s.PrismSides = ps >= Stroke.MinPrismSides && ps <= Stroke.MaxPrismSides
                ? ps : Stroke.DefaultPrismSides;
        }

        // ---- v13：抛物线的第二个点**换了含义**，老文件要迁移一次 ----
        //
        //   · v12 及以前：`Points[1]` 是"**张口点**"——它**落在对称轴上**，
        //     旧算法是 `p = 2 × |顶点→张口点|`；
        //   · v13 起：`Points[1]` 是"**曲线经过的一个点**"，`p` 由它反解
        //     （`p = t²/(2s)`，`t` 是它到对称轴的距离）。
        //
        // 不迁移会出事，而且**不会报错**：老那个点正在对称轴上（`t = 0`），
        // 反解的分母里有 `t`，于是张口解不出来、曲线缩到最小值
        // ——用户看到的是"我的抛物线一存一读就瘪了"。
        //
        // 迁移办法：拿**旧的 `p`** 反算一个真的落在曲线上的点换上（取 `u = 1` 那个点，
        // 也就是通径端点：`s = p/2、t = p` → 反解回来**还是这个 p**）。
        // 于是形状一模一样，只是表示法换了一套。
        if (version <= 12 && s.Kind == StrokeKind.Parabola && s.Points.Count >= 2)
        {
            var v = new Vector2(s.Points[0].X, s.Points[0].Y);
            var c = new Vector2(s.Points[1].X, s.Points[1].Y);
            float p = 2f * Vector2.Distance(v, c);                    // 旧算法
            var (dir, perp) = Stroke.ParabolaBasis(s.CurveAxis);      // 朝向上面已经归一过了
            s.SetPoint(1, v + perp * p + dir * (p * 0.5f));
        }

        // ---- v14：双曲线**拆成两套半轴**，老文件要把第三个点补出来 ----
        //
        // v13 及以前：双曲线只有两个控制点（中心 ＋ 外角点），而那个外角点**同时**是
        // "渐近线框"和"曲线半轴"——两者本来就是同一件事（渐近线画到 `±2a、±2b`），
        // 所以"定曲线大小"那一步会顺手把渐近线也改了。
        // 从 v14 起：第二个点专管**渐近线框**（`±A × ±B`，锁定后不动），
        // 第三个点才是"**曲线经过的点**"（半轴由它反解）。
        //
        // 不迁移会出事，而且**不报错**：老对象只有两个点，"曲线经过的点"取不到，
        // 曲线会退化成"顶到框的默认大小"（见 `HyperbolaCurveALocal` 的兜底）——
        // 形状跟你当初画的**不一样**，但看上去仍是一条正常的双曲线，最难查的那一类。
        //
        // 迁移办法（**保住老文件的视觉**）：
        //   · 渐近线框取 `A = 2a、B = 2b` —— 老文件里那两条虚线画的正是 `±(2a, 2b)`，一模一样；
        //   · 曲线经过的点取**曲线上 `u = 1` 处那个点**（`HyperbolaPoint(..., t: 1)`），
        //     反解回来 `a` 恰好还是原来的 `a`（`cosh²1 − sinh²1 = 1`）→ 曲线形状一模一样。
        if (version <= 13 && s.Kind == StrokeKind.Hyperbola && s.Points.Count >= 2)
        {
            var o = new Vector2(s.Points[0].X, s.Points[0].Y);
            var e = new Vector2(s.Points[1].X, s.Points[1].Y);
            float a = MathF.Abs(e.X - o.X), b = MathF.Abs(e.Y - o.Y);
            if (s.Points.Count < 3)
            {
                var q = Stroke.HyperbolaPoint(o, a, b, s.EffectiveAxis, 0, 1f);
                s.AddPoint(q.X, q.Y, 1f, 0);
            }
            s.SetPoint(1, new Vector2(o.X + 2f * a, o.Y + 2f * b));
        }
        return s;
    }

    /// <summary>
    /// 读长度前缀字符串，但**带长度上限**。
    /// 直接用 BinaryReader.ReadString 的话，一个损坏的长度字段会让它试图分配
    /// 几个 G——坏文件不该把程序拖死，只该被拒绝。
    /// </summary>
    private static string ReadString(BinaryReader r, int maxLen = 4096)
    {
        int n = r.Read7BitEncodedInt();
        if (n < 0 || n > maxLen)
            throw new InvalidDataException($"字符串长度不合理：{n}。");
        return System.Text.Encoding.UTF8.GetString(r.ReadBytes(n));
    }
}
