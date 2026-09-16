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
    /// </summary>
    public const int FormatVersion = 5;

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
