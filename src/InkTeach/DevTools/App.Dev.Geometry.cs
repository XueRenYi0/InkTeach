using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;


internal sealed partial class App
{
    private static int MaxInkColumn(byte[] bgra, int w, int h, int minRun)
    {
        for (int i = w - 1; i >= 0; i--)
        {
            int run = 0;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                bool magenta = bgra[o] > 200 && bgra[o + 1] < 90 && bgra[o + 2] > 200;
                if (magenta) { if (++run >= minRun) return i; }
                else run = 0;
            }
        }
        return -1;
    }

    private void CurveShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        float x0 = _virtualX + 200f, y0 = _virtualY + 180f;
        const float pitch = 620f;                            // 格子间距（横竖一样，看着是网格）

        // 造一条曲线：只有 BeginShapeAt 铺的那一个占位点，之后交给 Set*Box
        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }
        // 每格的左上角
        float CellX(int col) => x0 + col * pitch;
        float CellY(int row) => y0 + row * pitch;

        // ---- 第一行：抛物线的四种开口（**先选朝向，再用"顶点 + 经过点"两点画**） ----
        // 这就是用户 2026-09-20 定的口径：方向是"选"出来的（CurveAxis），大小由"经过那个点"反解。
        var dirs = new[] { CurveAxis.OpenUp, CurveAxis.OpenDown, CurveAxis.OpenRight, CurveAxis.OpenLeft };
        for (int col = 0; col < dirs.Length; col++)
        {
            float cx = CellX(col), cy = CellY(0);
            var v = new Vector2(cx + 280f, cy + 280f);
            var s = Make(Tool.Parabola, StrokeKind.Parabola, v.X, v.Y);
            s.CurveAxis = dirs[col];                              // ① 先选开口方向
            s.SetParabolaVertex(v.X, v.Y);                        // ② 定顶点
            // ③ 定"曲线经过的点"：取 `u = 1` 处的曲线点（`s = p/2、t = p`），
            //    反解出来正好是 p = 140（见 Stroke.ParabolaPThroughPoint），一个格子装得下。
            var (dir, perp) = Stroke.ParabolaBasis(dirs[col]);
            var q = v + perp * 140f + dir * 70f;
            s.SetParabolaThroughPoint(q.X, q.Y);
            Doc.AddStroke(s);
        }

        // ---- 第二行：双曲线两个方向 ＋ 正弦 / 余弦各一个周期 ----
        // 双曲线按真实的**两步**出图（第一步拖出**渐近线框**、第二步定"曲线过哪个点"），
        // 这样图上这两条跟老师真画出来的完全一致（朝向也是那一步定的）。
        // 渐近线框是**拖到哪就是哪**：`A = |dx|、B = |dy|`（2026-09-20 改成不再打对折）。
        var hX = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(0) + 280f, CellY(1) + 280f);
        hX.SetHyperbolaFromAsymptote(CellX(0) + 280f, CellY(1) + 280f,
                                     CellX(0) + 280f + 300f, CellY(1) + 280f + 120f, 8f);   // 斜率 0.4
        hX.SetHyperbolaThroughPoint(CellX(0) + 280f + 300f, CellY(1) + 280f + 104f);
        Doc.AddStroke(hX);          // 点更横 → 焦点在 x 轴（左右双曲线）

        var hY = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(1) + 280f, CellY(1) + 280f);
        hY.SetHyperbolaFromAsymptote(CellX(1) + 280f, CellY(1) + 280f,
                                     CellX(1) + 280f + 120f, CellY(1) + 280f + 300f, 8f);   // 斜率 2.5
        hY.SetHyperbolaThroughPoint(CellX(1) + 280f + 104f, CellY(1) + 280f + 300f);
        Doc.AddStroke(hY);          // 点更竖 → 焦点在 y 轴（上下双曲线）

        // 正弦：起点在轴上（第一个零点），**往上拖 = 先上后下**（就是课本的 y = sin x）。
        // ⚠ 2026-09-20 第十六批：**框宽就是一个周期**（用户："正弦和余弦用一个周期的图"）——
        // 所以这一张拖出来的是**一个周期**（300 宽、A = 75）。多周期那件事在下一格「波浪线」。
        // ⚠ 这一行的**纵向位置都写成 `CellY(1) + 本行内偏移`**：格子间距是 620，
        // 第三行（立体）从 `CellY(1) + 600` 就开始了 —— 偏移超过 600 就压在立体上了
        //（2026-09-20 第十六批把波浪线摆到 +760、当场压住圆台和球，出图才发现）。
        float sinX = x0 + 1250f;
        var sin = Make(Tool.Sine, StrokeKind.Sine, sinX, CellY(1) + 300f);
        sin.SetWaveBox(sinX, CellY(1) + 300f, sinX + 300f, CellY(1) + 225f, 8f);   // dy = −75 → A = 75
        Doc.AddStroke(sin);

        // 余弦：起点在**峰顶**，**往下拖 = 从峰顶往下**（就是课本的 y = cos x）。
        // 同样是**一个周期**（纵拖 = 峰 → 谷 = 2A，所以同样 75 的振幅要拖 150 高）。
        float cosX = x0 + 1600f;
        var cos = Make(Tool.Cosine, StrokeKind.Cosine, cosX, CellY(1) + 225f);
        cos.SetWaveBox(cosX, CellY(1) + 225f, cosX + 300f, CellY(1) + 375f, 8f);
        Doc.AddStroke(cos);

        // 波浪线（第十六批）：**多周期的正弦波**——横向拖的是"要画多长"、周期由振幅定
        //（`T = 1 × A`，用户定的"振幅 = 一个周期"）。这里 A = 70、画 210 宽 → **3 个整周期**，
        // 正是用户要的"很多个周期的波浪线"。
        // 摆在正弦 / 余弦下面一点（同一行里错开，三张并排看"一个周期 vs 多周期"最直观）。
        float wavX = x0 + 1950f;
        var wav = Make(Tool.Wave, StrokeKind.Wave, wavX, CellY(1) + 400f);
        wav.SetWaveBox(wavX, CellY(1) + 400f, wavX + 210f, CellY(1) + 330f, 8f);
        Doc.AddStroke(wav);

        // 正切（2026-09-20 第十五批）：**一支**，按下 = 原点、拖出以它为中心的框
        //（横向 = 半支长、纵向 = 可视半高，两条渐近线就落在框的左右两边）。
        // ⚠ 视觉半高有**下限比例 3:1**（见 Stroke.TangentMinAspect）：这里故意只拖 60 高，
        // 画出来会被顶到 240 —— 图上要看的正是"随手一拖就贴着渐近线"这件事。
        float tanX = x0 + 2560f, tanY = CellY(1) + 330f;
        var tan = Make(Tool.Tangent, StrokeKind.Tangent, tanX, tanY);
        tan.SetTangentBox(tanX, tanY, tanX + 80f, tanY + 60f, 8f);
        Doc.AddStroke(tan);

        // ---- 第三行：立体图形（2026-09-20 第五批，照 InkClass 的 case 6/7）----
        // **旋转体那一族**：一次拖出**外接矩形**就成（椭圆由矩形派生，
        // 扁率 = `Stroke.SolidEllipseRatio`），被挡住的那半圈是**细虚线**（辅助几何槽）。
        // 位置贴着第三行的格线上沿：再往上抬就会和第二行（双曲线 / 正弦）叠在一起，
        // 再往下压出图（1800 高）就会被裁掉——两头都试过，这个位置刚好。
        //
        // ⚠ 这一行原来放的是**圆柱 / 圆锥 / 长方体 / 四面体**——后两个 2026-09-20 第十二批
        // 撤了面板入口（见 ShapeRows），所以这里换成**用户现在真能拖出来的四个**：
        // 圆柱 / 圆锥 / 圆台 / 球。（长方体 / 四面体的画法没删，`--curvetest` 里那些
        // 几何断言照旧在管着它们，旧板书也照样打开。）
        float sy0 = CellY(2) - 20f;
        var cyl = Make(Tool.Cylinder, StrokeKind.Cylinder, CellX(0) + 130f, sy0);
        cyl.SetSolidBox(CellX(0) + 130f, sy0, CellX(0) + 430f, sy0 + 350f);
        Doc.AddStroke(cyl);

        var cone = Make(Tool.Cone, StrokeKind.Cone, CellX(1) + 130f, sy0);
        cone.SetSolidBox(CellX(1) + 130f, sy0, CellX(1) + 430f, sy0 + 350f);
        Doc.AddStroke(cone);

        // 圆台（2026-09-20 第十三批）：同一个外接矩形，上底是一圈**小一圈**的椭圆。
        var cfz = Make(Tool.ConeFrustum, StrokeKind.ConeFrustum, CellX(2) + 130f, sy0);
        cfz.SetSolidBox(CellX(2) + 130f, sy0, CellX(2) + 430f, sy0 + 350f);
        Doc.AddStroke(cfz);

        // 球（第十四批）：半径 = min(半宽, 半高) 的内切正圆 ＋ 赤道椭圆（近侧实线）。
        // 这里**故意拖一个正方**（350×350）——球本来就该是个圆。
        var sph = Make(Tool.Sphere, StrokeKind.Sphere, CellX(3) + 130f, sy0);
        sph.SetSolidBox(CellX(3) + 130f, sy0, CellX(3) + 480f, sy0 + 350f);
        Doc.AddStroke(sph);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 出图（2026-09-22）：**双曲线两档 ＋ 椭圆（带焦点）两档**摆成一张四格的图。
    /// 用法 `--conicshow [路径]`，默认 `reports/圆锥曲线-两档.bmp`。
    ///
    /// 为什么单开一张、不塞进 `--curveshow`：那一张的三行已经满了（第二行末尾离立体
    /// 只有 600 像素），塞进去会叠在一起（第十六批就这么出过一次错）。而这一批要看的
    /// 正是"**两档之间的差别**"——四格并排、同一套尺寸，一眼就能对比：
    ///   上排：双曲线**有 / 无**渐近线；下排：椭圆**有焦点三角形 / 只有焦点**。
    /// 椭圆的 P 用**默认位置**（椭圆左上方那个点，见 `Stroke.DefaultFocusPointU`）——
    /// 图上要看的正是"顶点**在椭圆上**、三角形在上半边"，和面板图标同一个位置。
    /// </summary>
    private void ConicShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        const float colPitch = 1440f, rowPitch = 720f;
        float x0 = _virtualX + 720f, y0 = _virtualY + 300f;

        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }

        // ---- 上排：双曲线两档（**同一个几何**，只差那两条虚线在不在）----
        // 画法按真实的两步来：第一步拖出**渐近线框**（+300, +120，斜率 0.4），
        // 第二步定"曲线经过哪个点"（更横 → 焦点在 x 轴）。
        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0;
            var h = Make(Tool.Hyperbola, StrokeKind.Hyperbola, cx, cy);
            h.SetHyperbolaFromAsymptote(cx, cy, cx + 300f, cy + 120f, 8f);
            h.SetHyperbolaThroughPoint(cx + 300f, cy + 104f);
            h.ShowAsymptotes = col == 0;                 // 左：有渐近线；右：无渐近线
            Doc.AddStroke(h);
        }

        // ---- 下排：椭圆（带焦点）两档（**同一个椭圆**，只差那两条边在不在）----
        // 中心 ＋ 外角点拖 300 × 160 → 宽椭圆，焦点在横轴上、离中心 ±254。
        // 左：有焦点三角形（**P 用默认位置**，也就是"顶点在椭圆上"的那个样子）；
        // 右：只有两个焦点。
        // ⚠ P 的默认角是**左上方**（120°，见 `Stroke.DefaultFocusPointU`）——图上看的就是
        // "三角形的顶点贴着椭圆那条线、在上半边"，和面板图标上画的是同一个位置。
        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0 + rowPitch;
            var e = Make(Tool.ConicEllipse, StrokeKind.ConicEllipse, cx, cy);
            // ⚠ **不要用 `SetShapeBox`**：那是"三角形 / 平行四边形"的外框写法（会写三个顶点），
            // 而椭圆的定义是"**中心 ＋ 外角点**"两个点 —— 用错的话画出来是个又细又高的怪东西
            //（第一版出图时就是这么翻的：a 和 b 全错了）。正确写法是"起手点当中心、再拖出外角点"。
            e.SetEnd(cx + 300f, cy + 160f);
            e.FocusTriangle = col == 0;
            Doc.AddStroke(e);
        }

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }
    private void PrismShowcase(string path)
    {
        BoardOn = true;                       // 白底：虚线压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);

        void Put(Tool tool, StrokeKind kind, int sides, float bx, float by, float dx, float dy)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink,
                Width = 6f * DpiScale, PrismSides = sides,
            };
            s.AddPoint(bx, by, 1f, 0);
            s.SetPrismBase(bx, by, bx + 340f, by + 140f);        // 底面外接框 340×140（压扁比 ≈ 0.41）
            s.SetPrismApex(bx + 170f + dx, by + 70f + dy);       // 顶上那个中心（dx/dy 决定直还是斜）
            Doc.AddStroke(s);
        }

        // 三行：棱柱 / 棱锥 / 棱台，每行都是**三/四/五/六**、都是直的（顶上那个中心正对底心上方 240）
        var rows = new (Tool tool, StrokeKind kind)[]
        {
            (Tool.Prism, StrokeKind.Prism),
            (Tool.Pyramid, StrokeKind.Pyramid),
            (Tool.Frustum, StrokeKind.Frustum),
        };
        float[] colX = { 200f, 820f, 1440f, 2060f };
        int[] colSides = { 3, 4, 5, 6 };
        for (int r = 0; r < rows.Length; r++)
        {
            float by = _virtualY + 420f + r * 400f;
            for (int c = 0; c < colX.Length; c++)
                Put(rows[r].tool, rows[r].kind, colSides[c], _virtualX + colX[c], by, 0f, -240f);
        }

        // 最后一行：**直 vs 斜**，三族各一对（同一个四棱，斜的那个顶心往右挪 200）
        float lastY = _virtualY + 420f + rows.Length * 400f;
        var pairs = new (Tool tool, StrokeKind kind, float x, bool slant)[]
        {
            (Tool.Prism, StrokeKind.Prism, 200f, false),
            (Tool.Prism, StrokeKind.Prism, 760f, true),
            (Tool.Pyramid, StrokeKind.Pyramid, 1320f, false),
            (Tool.Pyramid, StrokeKind.Pyramid, 1880f, true),
            (Tool.Frustum, StrokeKind.Frustum, 2440f, false),
        };
        foreach (var (tool, kind, x, slant) in pairs)
            Put(tool, kind, 4, _virtualX + x, lastY,
                slant ? 200f : 0f, slant ? -200f : -240f);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok
            ? $"  已保存 {path}（上三行：三/四/五/六 的 棱柱 / 棱锥 / 棱台，都是直的；末行：直 vs 斜）"
            : "  保存失败");
        _quit = true;
    }

    private void RenderInto(ID2D1Bitmap1 target, int strokeCount)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (n++ >= strokeCount) break;
            _windows[0].DrawStrokeForTest(s);
        }
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  RenderInto EndDraw 失败: " + hr.Description);
        try { ctx.Flush(out _, out _); } catch { /* 见报告说明：CreateBitmap 目标上 Flush 会报状态错误，四种分辨率一致，不影响横向对比 */ }
        ctx.Target = null;
    }

    private void PatchInto(ID2D1Bitmap1 target, RectF r)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
            Vortice.Direct2D1.AntialiasMode.Aliased);
        _windows[0].ClearRectForTest(r);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (!s.IntersectsRect(r)) continue;
            _windows[0].DrawStrokeForTest(s);
            if (++n > 400) break;
        }
        ctx.PopAxisAlignedClip();
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr2 = ctx.EndDraw();
        if (hr2.Failure) Console.WriteLine("  PatchInto EndDraw 失败: " + hr2.Description);
        try { ctx.Flush(out _, out _); } catch { /* 有意忽略：日志失败不能再抛（ADR-005 D5） */ }
        ctx.Target = null;
    }
    private IntPtr _syntheticPen;

    // ---- 合成触摸（自检注入用）------------------------------------------
    //
    // 为什么触摸需要单独一份：**POINTER_TYPE_INFO 真实定义里中间是一个 union，
    // 最大成员是 POINTER_TOUCH_INFO（144 字节）**，而我们那个结构体只声明了 pen 分支
    // （120 字节）加 24 字节填充 → 152 字节，与真实步长（4 + 4 对齐 + 144）一致；
    // 而且两个分支的**开头都是同一份 POINTER_INFO**，
    // 所以按 pen 的字段名填、把 type 设成 PT_TOUCH 就能造出一个触摸触点。
    private IntPtr _syntheticTouch;

    private bool EnsureSyntheticTouch()
    {
        if (_syntheticTouch != IntPtr.Zero) return true;
        // maxCount = 10：触摸**可以同时有多个触点**——"第二根手指守卫"、≥3 指擦、
        // 双指手势的自检都要一次注入 2~5 个触点。
        _syntheticTouch = Native.CreateSyntheticPointerDevice(
            Native.PT_TOUCH, 10, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticTouch != IntPtr.Zero;
    }

    /// <summary>
    /// 注入合成触摸（**不带面积**）。`points` 是"这一刻**所有**按在屏上的触点"，
    /// 下标就是触点序号（系统按 pointerId 跟踪，所以每次调用都要把还按着的触点一起带上）。
    /// `contact=false` 表示全部抬起。
    /// </summary>
    private void SendTouches(bool contact, params (float x, float y)[] points)
    {
        var sized = new (float x, float y, float size)[points.Length];
        for (int i = 0; i < points.Length; i++) sized[i] = (points[i].x, points[i].y, 0f);
        SendTouchesSized(contact, sized);
    }

    /// <summary>
    /// 注入合成触摸（**可带接触面积**）：`size` = 接触矩形边长（物理像素），0 = 不报面积。
    /// 手掌擦的判据靠它（见 Touch.cs 的自适应基线），所以"手掌"用例必须能造出大面积。
    ///
    /// ⚠ 步长 = **托管结构体大小**（= 原生步长 152，见 Native 的注释）；
    /// `rcContact` 等偏移**运行时用 `Marshal.OffsetOf` 算**——不写魔法数字。
    /// （历史坑：漏掉 `touchFlags/touchMask` 时按 144 排、第二个触点整体错位丢失。）
    /// </summary>
    private unsafe void SendTouchesSized(bool contact, params (float x, float y, float size)[] points)
    {
        int stride = Marshal.SizeOf<Native.POINTER_TYPE_INFO>();
        int unionOff = (int)Marshal.OffsetOf<Native.POINTER_TYPE_INFO>(nameof(Native.POINTER_TYPE_INFO.pen));
        int maskOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.touchMask));
        int rcOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContact));
        int rcRawOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContactRaw));

        int n = Math.Max(1, points.Length);
        IntPtr buf = Marshal.AllocHGlobal(stride * n + 16);
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var one = new Native.POINTER_TYPE_INFO();
                one.type = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerType = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerId = (uint)(i + 1);
                one.pen.pointerInfo.pointerFlags =
                    Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE
                    | (contact ? Native.POINTER_FLAG_INCONTACT : 0u)
                    | (i == 0 ? Native.POINTER_FLAG_PRIMARY : 0u);
                one.pen.pointerInfo.ptPixelLocationX = (int)points[i].x;
                one.pen.pointerInfo.ptPixelLocationY = (int)points[i].y;
                one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

                IntPtr dst = buf + i * stride;
                Marshal.StructureToPtr(one, dst, false);
                if (points[i].size > 0f)
                {
                    // 报面积：touchMask 置位 + rcContact / rcContactRaw 都填矩形。
                    float half = points[i].size * 0.5f;
                    int l = (int)(points[i].x - half), t = (int)(points[i].y - half);
                    int r = (int)(points[i].x + half), b = (int)(points[i].y + half);
                    Marshal.WriteInt32(dst + maskOff, (int)Native.TOUCH_MASK_CONTACTAREA);
                    Marshal.WriteInt32(dst + rcOff + 0, l);
                    Marshal.WriteInt32(dst + rcOff + 4, t);
                    Marshal.WriteInt32(dst + rcOff + 8, r);
                    Marshal.WriteInt32(dst + rcOff + 12, b);
                    Marshal.WriteInt32(dst + rcRawOff + 0, l);
                    Marshal.WriteInt32(dst + rcRawOff + 4, t);
                    Marshal.WriteInt32(dst + rcRawOff + 8, r);
                    Marshal.WriteInt32(dst + rcRawOff + 12, b);
                }
            }
            Native.InjectSyntheticPointerInput(_syntheticTouch, buf, (uint)points.Length);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool EnsureSyntheticPen()
    {
        if (_syntheticPen != IntPtr.Zero) return true;
        _syntheticPen = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticPen != IntPtr.Zero;
    }

    // ---- 真实触摸注入（自检 `--touchtest` 用）-----------------------------
    //
    // 为什么不用上面的合成触摸：合成设备（CreateSyntheticPointerDevice）虽然也走
    // WM_POINTER，但**系统手势/系统级触摸链路不认它**。要回答"真触摸屏捏合到底行不行"，
    // 只能用 Windows 的触摸注入通道（InitializeTouchInjection / InjectTouchInput）——
    // 它把自己伪装成真实数字化器的一批触点（可多点、带接触面积）。
    // 不支持就 SKIP（无触摸设备的机器、部分虚拟机、锁屏/安全桌面）。

    private bool _touchInjectReady;
    private string _touchInjectNote = "";

    /// <summary>初始化触摸注入（一次）。失败时 <paramref name="why"/> 是给 SKIP 看的原因。</summary>
    private bool EnsureTouchInjection(out string why)
    {
        if (_touchInjectReady) { why = _touchInjectNote; return true; }
        if (Native.InitializeTouchInjection(10, Native.TOUCH_FEEDBACK_DEFAULT))
        {
            _touchInjectReady = true;
            _touchInjectNote = "ok";
            why = _touchInjectNote;
            return true;
        }
        int err = Marshal.GetLastWin32Error();
        _touchInjectNote = $"InitializeTouchInjection 失败（Win32 {err}："
                         + $"{new System.ComponentModel.Win32Exception(err).Message}）";
        why = _touchInjectNote;
        return false;
    }

    /// <summary>
    /// 注入一批真实触点。<paramref name="phase"/>：0 = 落下（DOWN）、1 = 移动（UPDATE）、
    /// 2 = 抬起（UP）。`points` 是"这一刻所有还按着的触点"，下标即 pointerId（0..n-1）。
    /// 返回 false = 系统拒收（此时调用方按 SKIP 处理，不判失败）。
    /// </summary>
    private unsafe bool InjectTouches(int phase, out int win32Err, params (float x, float y)[] points)
    {
        win32Err = 0;
        int stride = Marshal.SizeOf<Native.POINTER_TOUCH_INFO>();
        int n = Math.Max(1, points.Length);
        IntPtr buf = Marshal.AllocHGlobal(stride * n + 16);
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var c = new Native.POINTER_TOUCH_INFO();
                c.pointerInfo.pointerType = Native.PT_TOUCH;
                // pointerId 从 0 起（官方 InjectTouch 样例与 fMBT 都是 0/1）——注入通道按
                // "第几个触点"跟踪，用 1..n 也合法，但 0 起最贴近样例，少一个变量。
                c.pointerInfo.pointerId = (uint)i;
                uint flags = Native.POINTER_FLAG_INRANGE;
                if (phase == 0)
                    flags |= Native.POINTER_FLAG_DOWN | Native.POINTER_FLAG_INCONTACT;
                else if (phase == 1)
                    flags |= Native.POINTER_FLAG_UPDATE | Native.POINTER_FLAG_INCONTACT;
                else
                    flags |= Native.POINTER_FLAG_UP;
                c.pointerInfo.pointerFlags = flags;
                c.pointerInfo.ptPixelLocationX = (int)points[i].x;
                c.pointerInfo.ptPixelLocationY = (int)points[i].y;
                // ⚠ hwndTarget **必须留空**：注入通道自己按屏幕坐标做命中测试，
                // 传任何窗口句柄都会被判成 ERROR_INVALID_PARAMETER（2026-11-16 实测：
                // 同一份触点 hwndTarget = 前台窗口 → FALSE 87，= NULL → TRUE）。
                c.pointerInfo.hwndTarget = IntPtr.Zero;

                // 接触面积/朝向/压力都给上：真实触摸屏就是这么报的，手势层也用得到。
                const int half = 12;
                var rc = new Native.RECT
                {
                    Left = (int)points[i].x - half,
                    Top = (int)points[i].y - half,
                    Right = (int)points[i].x + half,
                    Bottom = (int)points[i].y + half,
                };
                c.touchFlags = Native.TOUCH_FLAG_NONE;
                c.touchMask = Native.TOUCH_MASK_CONTACTAREA
                            | Native.TOUCH_MASK_ORIENTATION | Native.TOUCH_MASK_PRESSURE;
                c.rcContact = rc;
                // rcContactRaw 留 0：官方 InjectTouch 样例只填 rcContact（实测填了也能过，
                // 但注入只需要"预测接触区"，原始区由系统自己算，少一个可能被校验的字段）。
                c.orientation = 90;
                c.pressure = 32000;

                Marshal.StructureToPtr(c, buf + i * stride, false);
            }
            bool ok = Native.InjectTouchInput((uint)points.Length, buf);
            if (!ok) win32Err = Marshal.GetLastWin32Error();   // 必须紧跟着读（下面还有别的调用）
            return ok;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>注入一个合成笔采样点，走的是和真笔同一条 WM_POINTER 路径。</summary>
    private unsafe void SendPenPoint(float x, float y, uint pressure, bool contact, bool first)
    {
        uint flags = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE;
        if (contact) flags |= Native.POINTER_FLAG_INCONTACT;
        if (first) flags |= Native.POINTER_FLAG_NEW | Native.POINTER_FLAG_PRIMARY | Native.POINTER_FLAG_FIRSTBUTTON;

        var one = new Native.POINTER_TYPE_INFO();
        one.type = Native.PT_PEN;
        one.pen.pointerInfo.pointerType = Native.PT_PEN;
        one.pen.pointerInfo.pointerFlags = flags;
        one.pen.pointerInfo.ptPixelLocationX = (int)x;
        one.pen.pointerInfo.ptPixelLocationY = (int)y;
        one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        one.pen.penFlags = 0;
        one.pen.penMask = Native.PEN_MASK_PRESSURE;
        one.pen.pressure = pressure;

        // 单点：按**托管结构体步长**排（= 原生步长 152；单点其实无所谓，但别再写死旧数字）。
        IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.POINTER_TYPE_INFO>() + 16);
        try
        {
            Marshal.StructureToPtr(one, buf, false);
            Native.InjectSyntheticPointerInput(_syntheticPen, buf, 1);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
