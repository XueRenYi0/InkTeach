using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;


public partial class InkEngine
{
    // ---------------------------------------------------------------------
    //  图形端点编辑：起手 / 每帧 / 提交
    // ---------------------------------------------------------------------

    /// <summary>
    /// 定义元素手柄按下：起一次"改几何"的手势。
    ///
    /// 复用 <see cref="DetachForDrag"/> 那套：**只在这一刻**把这条图形压过的块标脏一次、
    /// 并且让内容层在重画时跳过它——于是它原来待的地方当场就干净了，
    /// 之后整个手势期内容层一帧都不用再动（自检里"内容层一帧不重画"盯的就是这条）。
    /// </summary>
    private void BeginVertexDrag(Stroke s, ShapeHandle h, float x, float y)
    {
        _vertexDragging = true;
        _vertexTarget = s;
        _vertexHandle = h;
        // 格数**不能只看"现在有几个点"**：双曲线的第三个点（"曲线经过的那个点"）是**可缺的**
        // （刚起手、或迁移前的老对象就只有两个），而"拖曲线上的那个点"这个手柄照样要能按下——
        // 按下就得往第三格写，格数不够就是**数组越界**（自检里当场崩过一次）。
        // 提交时会把这第三格写进模型（ApplyGeometry），对象也就从两个点补成三个。
        _vertexPreviewLocal = new Vector2[Math.Max(s.Points.Count, Stroke.MinCurvePoints(s.Kind))];
        _vertexPrevBounds = RectF.Empty;
        _vertexPreviewCanvas = SelectionHandles.ShapeHandleCanvasPosition(s, h);
        // "这一拖不是拖格距"（0 = 没在拖；格距自己不许是 0，见那个字段的注释）。
        _vertexPreviewGridStep = 0f;
        // 预览初始就是原样（这个时候还没动，画面不该有任何变化）。
        WriteVertexLocalPoints(_vertexPreviewCanvas, snapping: false);
        UpdateVertexReadout();
        _vertexSnapped = false;
        _shapeSnap = ShapeSnapKind.None;
        _shapeSnapText = "";
        _shapeSnapAnchor = _vertexPreviewCanvas;
        // 坐标系轴端吸附网格时用的"起手格距"：拖动过程中格距可能跟着外框变（自动档），
        // 用起手那一刻的值才能吸得稳；真吸上了就把这一档钉住（见 WriteVertexLocalPoints）。
        _vertexAxisGridStep = s.Kind == StrokeKind.Coordinate && s.Grid
            ? s.AxisGridStepLocal() : 0f;
        _vertexPinGridStep = 0f;

        _dragHandle = SelectionHandles.SelHandleOf(h);
        _dragIsMove = false;
        _dragStartPoint = new Vector2(x, y);
        _dragTargets = new[] { s };
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        DetachForDrag();

        SelDragging = true;
        _dirty = true;
    }

    /// <summary>
    /// 拖动中：**模型一个字不改**——只在
    /// <see cref="_vertexPreviewLocal"/> / <see cref="_vertexPreviewCanvas"/> 里攒出
    /// "临时几何"，由浮动层每帧画出来（<see cref="OverlayWindow"/> 的 DrawVertexPreview）。
    ///
    /// 坐标系有两层，错一层元素就会飞：指针和手柄在**画布坐标**，
    /// 而定义元素存在对象的**局部坐标**里，中间隔着 `Transform`（旋转过的图形必须过
    /// `Transform⁻¹` 才能改点）。
    ///
    /// 吸附有**两套**，各管各的：
    ///   · 直线 / 箭头 → **方向**吸附（绕另一个端点转、保持长度）；
    ///   · 椭圆 / 三角形 / 平行四边形 → **特殊形状**吸附（正圆 / 等腰 / 等边 / 直角 /
    ///     菱形 / 矩形），在 <see cref="WriteVertexLocalPoints"/> 里做，
    ///     语义是"把被拖的那个点修正到恰好满足约束的位置"（规格 9.6）。
    /// 两套都可能同时"吸住"，所以胶囊上的字由 <see cref="ShapeSnapKind"/> 与
    /// α 读数分别负责，互不覆盖。
    /// </summary>
    private void UpdateVertexDrag(float x, float y)
    {
        var s = _vertexTarget;
        if (s == null) return;

        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        var canvasPoint = new Vector2(x, y);
        bool snapped = false;
        if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
        {
            // "对面那个端点"：拖哪一头就绕另一头转，才符合"我刚拖的这头动、那头不动"。
            var fixedHandle = _vertexHandle == ShapeHandle.Anchor ? ShapeHandle.Rim : ShapeHandle.Anchor;
            var fixedPoint = SelectionHandles.ShapeHandleCanvasPosition(s, fixedHandle);
            canvasPoint = SelectionHandles.SnapEndPoint(fixedPoint, canvasPoint, shift, alt, out snapped);
        }

        // 脏区只标"被拖的那个元素**走过的那一小段**"：旧、新两个小矩形分开加
        // （上一帧 ∪ 这一帧，和拖动预览同一套账）。整条线的包围盒横跨屏幕时几乎是整屏，
        // 而这里动的其实只有一个元素——按整条算等于每帧白重画一大片。
        if (!_vertexPrevBounds.IsEmpty) Doc.Dirty.Add(_vertexPrevBounds);
        var prevCanvas = _vertexPreviewCanvas;
        _vertexPreviewCanvas = canvasPoint;
        _vertexSnapped = snapped;
        _shapeSnapAnchor = canvasPoint;         // 吸住时胶囊贴在被拖的那个元素上
        WriteVertexLocalPoints(canvasPoint);    // 里面顺手判"特殊形状吸附"（见 9.6）
        _vertexPrevBounds = EndpointDirtyRect(prevCanvas, canvasPoint, s.Width * 0.5f + 2f);
        if (!_vertexPrevBounds.IsEmpty) Doc.Dirty.Add(_vertexPrevBounds);

        UpdateVertexReadout();

        if (!_selDragMoved
            && Vector2.Distance(new Vector2(x, y), _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;
        _dirty = true;
    }

    /// <summary>
    /// 松手：把攒了一路的临时几何**提交成一步撤销**（改的是几何，不是变换）。
    ///
    /// 一点没移动就不提交——按一下端点手柄本来会多出一条"原样"的撤销记录。
    /// </summary>
    private void CommitVertexDrag()
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        bool moved = _selDragMoved;
        // 这一拖是不是"拖焦点三角形的顶点 P"（见 _vertexPreviewFocusU）——
        // 取值要在**清字段之前**，而且只有真的动过才算数。
        float focusU = _vertexPreviewFocusU;
        bool focusDrag = moved && s != null && !float.IsNaN(focusU);
        // 同一个道理：这一拖是不是"拖坐标系的格距手柄"（见 _vertexPreviewGridStep）。
        float gridStep = _vertexPreviewGridStep;
        bool gridDrag = moved && s != null && gridStep > 0f;
        // 轴端吸到网格时要把起手那一档格距钉住（见 WriteVertexLocalPoints）。
        float pinGridStep = _vertexPinGridStep;
        bool pinGrid = moved && s != null && pinGridStep > 0f && !gridDrag;

        _vertexDragging = false;
        _vertexTarget = null;
        _vertexPreviewLocal = null;
        _vertexPreviewFocusU = float.NaN;       // 手势结束：预览字段归位
        _vertexPreviewGridStep = 0f;
        _vertexPrevBounds = RectF.Empty;
        _dragTargets = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _selDragMoved = false;
        _shapeSnap = ShapeSnapKind.None;        // 手势结束：胶囊跟着消失
        _shapeSnapText = "";
        _vertexAxisGridStep = 0f;
        _vertexPinGridStep = 0f;

        // 拖 P：**点表一个字没变**（P 不是控制点），只有那个参数角要写
        //（撤销要不要跟着回，见 SetStrokeGeometryAction 的 newFocusU）。
        // ⚠ 传的是 `float?`：只有"真的在拖 P"才带值，别的改几何动作传 null（一个字都不动 P）。
        if (focusDrag) Doc.ApplyGeometry(s, pts, focusU);
        // 拖格距：同一个套路——点表一个字没变（格距不是控制点），只有那一个数要写。
        else if (gridDrag) Doc.ApplyGeometry(s, pts, null, gridStep);
        // 拖轴端且吸到了网格：点表和格距一起写（格距钉住，网格线才不会跑掉）。
        else if (pinGrid) Doc.ApplyGeometry(s, pts, null, pinGridStep);
        else if (moved && s != null && pts != null) Doc.ApplyGeometry(s, pts);
        // 一点没动（只是点了一下端点手柄松手）：**也必须把它压过的那块重画一次**——
        // 起手那一下它已经从内容层摘出去了，不重画的话这一块就一直是"没有这条线"，
        // 屏幕上的线会当场消失（松手后模型其实什么都没变）。
        // 走 InvalidateContent 而不是 Dirty.Add：内容层只在**版本号变了**时才消费脏区
        // （见那个函数的注释），少抬一次版本号就等于白标。
        else if (s != null) Doc.InvalidateContent(s.PaddedBounds);

        // 回到内容层：提交那一步已经把旧位与新位都标脏了，所以这一帧的重画一定会
        // 带上它（而不是继续画预览）。
        ReattachAfterDrag();
        _dirty = true;
    }

    /// <summary>
    /// 把"被拖的定义元素落在画布坐标 <paramref name="canvasPoint"/>"写进预览的局部点。
    ///
    /// 这里是**全部拖动语义**所在：同一个函数要伺候六种对象
    /// （直线/箭头改端点、圆改半径、圆心平移、椭圆改一条半轴、三角形/平行四边形改顶点），
    /// 所以按 `(Kind, 手柄)` 分派，而不是"替换第 N 个点"。
    ///
    /// 三条硬规矩：
    ///   · 指针在**画布坐标**、点存在**局部坐标**，必须过 `Transform⁻¹`（旋转过的对象尤其）；
    ///   · 拖圆心/中心时**两个点一起平移**——半径 / 半轴因此逐位不变
    ///     （这也是"拖圆心 = 平移"的判据）；
    ///   · **特殊形状吸附**（规格 9.6）只在这条路上做：它改的是"被拖的那个点"，
    ///     而不是形状的种类。`Alt` 传进去就变成完全自由。
    /// </summary>
    private void WriteVertexLocalPoints(Vector2 canvasPoint, bool snapping = true)
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        // 拷的是"模型里**现在有的**点"，而 `pts` 可能**比它长**（双曲线那个可缺的第三个点，
        // 见 BeginVertexDrag）——长出来的那几格先留零向量，由下面的分支按需填。
        for (int i = 0; i < pts.Length; i++)
            pts[i] = i < s.Points.Count ? new Vector2(s.Points[i].X, s.Points[i].Y) : Vector2.Zero;

        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        float lenTol = SelectionHandles.ShapeSnapLengthToleranceLogical * DpiScale;
        _shapeSnap = ShapeSnapKind.None;        // 每帧重判：不吸的帧必须回到"没有"
        _shapeSnapText = "";
        _vertexPinGridStep = 0f;

        // **坐标系原点吸附**（2026-11-03）：画布中心 / 四条边，横竖各判各的。
        // 在**画布坐标**里吸，过了这一关才逆变换回局部坐标——否则旋转 / 缩放过的
        // 坐标系会吸到一个和屏幕上看到的完全无关的位置。
        if (snapping && !alt && s.Kind == StrokeKind.Coordinate
            && _vertexHandle == ShapeHandle.Vertex2)
        {
            canvasPoint = SnapCoordinateOrigin(s, canvasPoint, out _shapeSnap, out string snapText);
            _shapeSnapText = snapText;
            _shapeSnapAnchor = canvasPoint;
        }
        else if (snapping && !alt && s.Kind == StrokeKind.SpaceCoordinate
                 && _vertexHandle == ShapeHandle.SpaceOrigin)
        {
            canvasPoint = SnapSpaceOrigin(canvasPoint, out _shapeSnap, out string snapText);
            _shapeSnapText = snapText;
            _shapeSnapAnchor = canvasPoint;
        }

        var local = SelectionHandles.ToLocalPoint(canvasPoint, s.Transform);
        var c = s.ShapeCenterLocal;
        float minAxis = ShapeMinAxisLogical * DpiScale;

        switch (s.Kind)
        {
            case StrokeKind.Trapezoid:
            {
                float minW = ShapeMinAxisLogical * DpiScale;
                float minH = ShapeMinAxisLogical * DpiScale;
                if (pts.Length < 4) break;

                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= 4) break;
                if (idx <= 1)
                {
                    // 下底：两个底点的 y 一起走，保证底边水平。
                    float topY = MathF.Min(pts[2].Y, pts[3].Y);
                    pts[0].Y = pts[1].Y = MathF.Max(local.Y, topY + minH);
                    pts[idx].X = local.X;
                    // 拖过另一个底点：自动交换左右身份，仍是梯形，不交叉。
                    if (pts[0].X > pts[1].X)
                    {
                        (pts[0], pts[1]) = (pts[1], pts[0]);
                        _vertexHandle = _vertexHandle == ShapeHandle.Vertex0
                            ? ShapeHandle.Vertex1 : ShapeHandle.Vertex0;
                        idx = SelectionHandles.VertexIndex(_vertexHandle);
                    }
                    if (pts[1].X - pts[0].X < minW)
                    {
                        if (idx == 0) pts[1].X = pts[0].X + minW;
                        else pts[0].X = pts[1].X - minW;
                    }                }
                else
                {
                    // 上底：两个顶点的 y 一起走，且不能压到下底。
                    float bottomY = MathF.Max(pts[0].Y, pts[1].Y);
                    pts[2].Y = pts[3].Y = MathF.Min(local.Y, bottomY - minH);
                    pts[idx].X = local.X;
                    if (pts[2].X < pts[3].X)
                    {
                        (pts[2], pts[3]) = (pts[3], pts[2]);
                        _vertexHandle = _vertexHandle == ShapeHandle.Vertex2
                            ? ShapeHandle.Vertex3 : ShapeHandle.Vertex2;
                        idx = SelectionHandles.VertexIndex(_vertexHandle);
                    }
                    if (pts[2].X - pts[3].X < minW)
                    {
                        if (idx == 2) pts[3].X = pts[2].X - minW;
                        else pts[2].X = pts[3].X + minW;
                    }
                }
                if (!alt)
                {
                    float mb = (pts[0].X + pts[1].X) * 0.5f;
                    float mt = (pts[2].X + pts[3].X) * 0.5f;
                    float wb = pts[1].X - pts[0].X;
                    float wt = pts[2].X - pts[3].X;
                    if (MathF.Abs(wt - wb) <= lenTol)
                    {
                        float half = wb * 0.5f;
                        if (MathF.Abs(mb - mt) <= lenTol)
                        {
                            float mid = (mb + mt) * 0.5f;
                            pts[2].X = mid + half; pts[3].X = mid - half;
                            _shapeSnap = ShapeSnapKind.Rectangle;
                        }
                        else
                        {
                            pts[2].X = mt + half; pts[3].X = mt - half;
                            _shapeSnap = ShapeSnapKind.ParallelogramTrapezoid;
                        }
                    }
                    else if (MathF.Abs(mb - mt) <= lenTol)
                    {
                        float d = mb - mt;
                        pts[2].X += d; pts[3].X += d;
                        _shapeSnap = ShapeSnapKind.Isosceles;
                    }
                    else
                    {
                        int ti = SelectionHandles.VertexIndex(_vertexHandle);
                        bool right = (ti == 2 && MathF.Abs(pts[2].X - pts[1].X) <= lenTol)
                                  || (ti == 3 && MathF.Abs(pts[3].X - pts[0].X) <= lenTol)
                                  || (ti == 1 && MathF.Abs(pts[1].X - pts[2].X) <= lenTol)
                                  || (ti == 0 && MathF.Abs(pts[0].X - pts[3].X) <= lenTol);
                        if (right)
                        {
                            if (ti == 2) pts[2].X = pts[1].X;
                            else if (ti == 3) pts[3].X = pts[0].X;
                            else if (ti == 1) pts[1].X = pts[2].X;
                            else pts[0].X = pts[3].X;
                            _shapeSnap = ShapeSnapKind.RightAngle;
                        }
                    }
                }
                break;
            }

            case StrokeKind.Sector:
            {
                if (pts.Length < 3) break;
                var center = new Vector2(pts[0].X, pts[0].Y);
                float a0 = MathF.Atan2(pts[1].Y - center.Y, pts[1].X - center.X);
                float sweep = s.SectorSweepDegrees();
                if (_vertexHandle == ShapeHandle.Rim)
                {
                    float rOld = Vector2.Distance(center, new Vector2(pts[1].X, pts[1].Y));
                    float r = shift ? MathF.Max(minAxis, rOld)
                                    : MathF.Max(minAxis, Vector2.Distance(center, local));
                    if (!shift)
                        a0 = MathF.Atan2(local.Y - center.Y, local.X - center.X);
                    pts[1] = center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r;
                    pts[2] = center + new Vector2(MathF.Cos(a0 + sweep * MathF.PI / 180f),
                                                  MathF.Sin(a0 + sweep * MathF.PI / 180f)) * r;
                    break;
                }
                if (_vertexHandle == ShapeHandle.FocusPoint)
                {
                    float rOld = Vector2.Distance(center, new Vector2(pts[1].X, pts[1].Y));
                    float r = shift ? MathF.Max(minAxis, rOld)
                                    : MathF.Max(minAxis, Vector2.Distance(center, local));
                    float a1 = MathF.Atan2(local.Y - center.Y, local.X - center.X);
                    float d = (a1 - a0) * 180f / MathF.PI;
                    while (d < 0f) d += 360f;
                    if (!alt)
                    {
                        float snapped = MathF.Round(d / 15f) * 15f;
                        if (MathF.Abs(snapped - d) <= 3f) d = snapped;
                    }
                    sweep = Math.Clamp(d, 5f, 359f);
                    pts[1] = center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r;
                    pts[2] = center + new Vector2(MathF.Cos(a0 + sweep * MathF.PI / 180f),
                                                  MathF.Sin(a0 + sweep * MathF.PI / 180f)) * r;
                }
                break;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
            {
                // 顶点：**拖哪个只动哪个**，第四个角（平行四边形）永远是算出来的。
                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= pts.Length) break;
                pts[idx] = SelectionHandles.SnapPolygonVertex(
                    s.Kind, idx, pts[0], pts[1], pts[2], local, lenTol, alt, out _shapeSnap);
                break;
            }

            case StrokeKind.SpaceCoordinate:
            {
                // 2026-11-03：四个手柄各管一件事。O = 整组平移（否则轴会歪，
                // 就不再是"直角坐标系"）；三个轴端只改自己那一截的长度。
                if (pts.Length < 4) break;
                var o = new Vector2(pts[0].X, pts[0].Y);
                if (_vertexHandle == ShapeHandle.SpaceOrigin)
                {
                    var delta = local - o;
                    for (int i = 0; i < 4; i++) pts[i] += delta;
                    break;
                }
                if (snapping && _vertexHandle is ShapeHandle.SpaceX or ShapeHandle.SpaceY)
                {
                    var snapped = SnapSpaceAxis(s, _vertexHandle, o, local, shift, alt,
                                                out ShapeSnapKind sk, out string st);
                    if (sk != ShapeSnapKind.None)
                    {
                        local = snapped;
                        _shapeSnap = sk;
                        _shapeSnapText = st;
                        _shapeSnapAnchor = Vector2.Transform(local, s.Transform);
                    }
                }
                if (_vertexHandle == ShapeHandle.SpaceX)
                {
                    // x 轴端**自由拖**：位置同时决定长度和角度（有些题的投影夹角不是 60°）。
                    // 只夹最小长度，方向完全跟指针；Z 仍保持向上。
                    var d = local - o;
                    float len = d.Length();
                    var dir = len > 1e-3f ? d / len : new Vector2(1f, 0f);
                    len = MathF.Max(minAxis, len);
                    pts[1] = o + dir * len;
                    break;
                }
                if (_vertexHandle == ShapeHandle.SpaceZ)
                {
                    // z 永远向上（用户："Z 轴一般都是会向上"）：往下拖只缩到最小。
                    float len = MathF.Max(minAxis, o.Y - local.Y);
                    pts[3] = o + new Vector2(0f, -len);
                    break;
                }
                if (_vertexHandle == ShapeHandle.SpaceY)
                {
                    // y 轴端同样**自由拖**：长度和夹角都能调；z 不动。
                    var d = local - o;
                    float len = d.Length();
                    var dir = len > 1e-3f ? d / len : Stroke.SpaceAxisYDirection;
                    len = MathF.Max(minAxis, len);
                    pts[2] = o + dir * len;
                    break;
                }
                break;
            }

            case StrokeKind.Coordinate:
            {
                if (pts.Length < 3) break;
                var o = new Vector2(pts[2].X, pts[2].Y);
                float minLen = ShapeMinAxisLogical * DpiScale;

                // ---- 四个轴端：**只改自己那一截**（2026-11-03）----
                // 0 = 外框的 min 那一侧，1 = 外框的 max 那一侧；原点、另外三个端点都不动。
                if (_vertexHandle is ShapeHandle.AxisRight or ShapeHandle.AxisLeft
                                    or ShapeHandle.AxisTop or ShapeHandle.AxisBottom)
                {
                    bool horizontal = _vertexHandle is ShapeHandle.AxisRight or ShapeHandle.AxisLeft;
                    bool positive = _vertexHandle is ShapeHandle.AxisRight or ShapeHandle.AxisBottom;
                    var (minX, minY, maxX, maxY) = s.AxisFrameLocal();
                    float value = horizontal ? local.X : local.Y;

                    // 画布边吸附（和原点同一套 8 逻辑像素；边在原点外侧才接受）。
                    if (snapping && !alt)
                    {
                        float tol = CoordinateSnapRadiusLogical * DpiScale;
                        var vp = ViewportCanvas;
                        float edgeCanvas = horizontal
                            ? (positive ? vp.MaxX : vp.MinX)
                            : (positive ? vp.MaxY : vp.MinY);
                        // 画布边 → 局部坐标的同一个轴（坐标系不给旋转柄；这里只是把
                        // 多选整体缩放过的情形也一并覆盖）。
                        var edgeLocal = SelectionHandles.ToLocalPoint(
                            horizontal ? new Vector2(edgeCanvas, canvasPoint.Y)
                                       : new Vector2(canvasPoint.X, edgeCanvas), s.Transform);
                        float edge = horizontal ? edgeLocal.X : edgeLocal.Y;
                        bool edgeUsable = positive ? edge >= (horizontal ? o.X : o.Y) + minLen
                                                   : edge <= (horizontal ? o.X : o.Y) - minLen;
                        if (edgeUsable && MathF.Abs(value - edge) <= tol)
                        {
                            value = edge;
                            _shapeSnap = ShapeSnapKind.CanvasEdge;
                            _shapeSnapText = "画布边";
                        }
                        else if (s.Grid && _vertexAxisGridStep > 0f)
                        {
                            // 自己的网格：长度吸到格距的整数倍（1 格起）。
                            float scale = MathF.Sqrt(MathF.Abs(
                                s.Transform.M11 * s.Transform.M22 - s.Transform.M12 * s.Transform.M21));
                            if (scale < 1e-3f) scale = 1f;
                            float originOnAxis = horizontal ? o.X : o.Y;
                            float len = MathF.Abs(value - originOnAxis);
                            float k = MathF.Round(len / _vertexAxisGridStep);
                            float snapped = k * _vertexAxisGridStep;
                            if (k >= 1f && MathF.Abs(len - snapped) <= tol / scale)
                            {
                                value = originOnAxis + (positive ? snapped : -snapped);
                                _shapeSnap = ShapeSnapKind.GridStep;
                                _shapeSnapText = "网格";
                                // 自动格距会随外框短边变；吸上了就把当前这一档钉住，
                                // 否则松手后网格线又跑开，读数就成了假的。
                                _vertexPinGridStep = _vertexAxisGridStep;
                            }
                        }
                    }

                    // 夹最小值：轴端不许越过原点到另一侧（否则"正 / 负半轴"就翻面了）。
                    if (horizontal)
                    {
                        value = positive ? MathF.Max(value, o.X + minLen)
                                         : MathF.Min(value, o.X - minLen);
                        if (positive) maxX = value; else minX = value;
                    }
                    else
                    {
                        value = positive ? MathF.Max(value, o.Y + minLen)
                                         : MathF.Min(value, o.Y - minLen);
                        if (positive) maxY = value; else minY = value;
                    }
                    // 外框统一写回 min/max 两个角（顺序不承载语义，见 AxisFrameLocal）。
                    pts[0] = new Vector2(minX, minY);
                    pts[1] = new Vector2(maxX, maxY);
                    break;
                }

                // ---- 格距手柄（第 6 颗，开网格才有）----
                // 它**不是控制点**，而且**一个点都不动**——外框、原点都保持原样，改的只有格距。
                if (_vertexHandle == ShapeHandle.Vertex3)
                {
                    // 手柄落在"原点 + (格距, −格距)"上，所以两个方向的偏移量都等于格距；
                    // 取平均（= 打到那条对角线上的投影）而不是单看某一轴：拖动方向偏一点也不会跳。
                    var d = local - o;
                    float want = (MathF.Abs(d.X) + MathF.Abs(d.Y)) * 0.5f;
                    _vertexPreviewGridStep = Math.Clamp(want,
                        Stroke.AxisMinGridStepLocal,
                        Stroke.AxisMaxGridStepLocal(pts[0], pts[1]));
                    break;
                }

                if (_vertexHandle == ShapeHandle.Vertex2)
                {
                    // **原点：在框里自由挪**（2026-11-03 用户纠正："原本他的原点是可以拖动
                    // 位置的，比如刚开始在最中心，可以拖到左下角"）。
                    // 夹在框内是必须的：跑到框外，两条轴就都不在框里交叉了，
                    // 画出来是个说不清的东西（而贴在框角上正是想要的那种用法）。
                    // 特殊位置（框中心 / 四角 / 画布中心 / 画布边）的吸附在
                    // SnapCoordinateOrigin 里已经做完了，这里只负责夹取。
                    float fx0 = MathF.Min(pts[0].X, pts[1].X), fx1 = MathF.Max(pts[0].X, pts[1].X);
                    float fy0 = MathF.Min(pts[0].Y, pts[1].Y), fy1 = MathF.Max(pts[0].Y, pts[1].Y);
                    pts[2] = new Vector2(Math.Clamp(local.X, fx0, fx1),
                                         Math.Clamp(local.Y, fy0, fy1));
                    break;
                }
                break;
            }

            case StrokeKind.NumberLine:
            {
                // **数轴的两个端点**：只改 x，y 一律保持 —— 往斜上方拖也还是水平线
                // （数轴歪了就不是数轴了）。同时不许互相越过：越过去"左端/右端"
                // 这两个名字就撒谎了，手柄也会互换位置。
                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= pts.Length) break;
                float minLen = ShapeMinAxisLogical * DpiScale;
                float x = local.X;
                if (idx == 0) x = MathF.Min(x, pts[1].X - minLen);
                else x = MathF.Max(x, pts[0].X + minLen);
                pts[idx] = new Vector2(x, pts[0].Y);
                break;
            }

            case StrokeKind.Circle when _vertexHandle == ShapeHandle.Rim:
            {
                // 圆的圆周点：**只改半径，圆心钉住**。半径在局部坐标里量（两点距离），
                // 方向照指针走；太小就夹到最小值（不然圆退化成一点，看不见还点不中）。
                var dir = local - c;
                float r = dir.Length();
                if (r < minAxis)
                    local = c + (r > 1e-3f ? dir / r : new Vector2(1f, 0f)) * minAxis;
                pts[1] = local;
                break;
            }

            case StrokeKind.Circle when _vertexHandle == ShapeHandle.Anchor:
            {
                // 圆心：**整条平移**（半径逐位不变）。
                // 正常路径上拖它走的是"整体拖动"（改变换矩阵，见 TryBeginSelectionGesture），
                // 这里只是兜底——万一哪天真从这条路进来，语义也必须是"平移"而不是"改一个点"。
                //
                // `when` 必须写在这里：`case A: case B when 条件:` 的条件**只管 B**，
                // A 会无条件命中（这一条在下面 sine/cosine 那段也专门提醒过）。
                var d = local - c;
                pts[0] = c + d;
                pts[1] = s.RimLocalPoint() + d;
                break;
            }

            case StrokeKind.Ellipse when _vertexHandle == ShapeHandle.AxisRight:
            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.AxisRight:
            {
                // 右端点：**只改 a**，b 保持（这就是"每次只动那一条"）。
                // 往左拖过中心也照样缩（负数被下面的 minAxis 卡住），
                // 所以"想缩左边"不需要另一个手柄——抓右端点一路往左拖就行。
                // ⚠ 两个 case 都带 `when`：`case A: case B when 条件:` 的条件**只管 B**（老账）。
                float a = MathF.Max(minAxis, local.X - c.X);
                // 正圆吸附（规格 9.6）：|a − b| 在容差内就取成 b —— 于是 a、b 逐位相等。
                a = SelectionHandles.SnapEllipseAxis(a, s.SemiAxisBLocal, lenTol, alt, out bool snapA);
                if (snapA) _shapeSnap = ShapeSnapKind.Circle;
                pts[1] = new Vector2(c.X + a, c.Y + s.SemiAxisBLocal);
                break;
            }

            case StrokeKind.Ellipse when _vertexHandle == ShapeHandle.AxisTop:
            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.AxisTop:
            {
                // 上端点：**只改 b**，a 保持（同理，往下拖过中心也能缩）。
                float b = MathF.Max(minAxis, c.Y - local.Y);
                // 正圆吸附（规格 9.6）：|b − a| 在容差内就取成 a。
                b = SelectionHandles.SnapEllipseAxis(b, s.SemiAxisALocal, lenTol, alt, out bool snapB);
                if (snapB) _shapeSnap = ShapeSnapKind.Circle;
                pts[1] = new Vector2(c.X + s.SemiAxisALocal, c.Y + b);
                break;
            }

            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.FocusPoint:
            {
                // **焦点三角形的顶点 P**：它**不在 pts 里**（P 是参数角算出来的，
                // 不是控制点，见 ShapeHandle.FocusPoint），所以这里一个点都不写，
                // 只把"拖到哪儿"折成参数角记到预览字段里——浮动层拿它画预览，
                // 松手由 CommitVertexDrag 写进对象（那一步才进撤销栈）。
                _vertexPreviewFocusU = s.ConicEllipseAngleOf(local);
                break;
            }

            // 四种曲线（抛物线 / 双曲线 / 正弦 / 余弦）的特殊点手柄 2026-09-20 全砍了
            //（用户："通通按常规操作，给操作柄和旋转"），所以这里也没有它们的写回分支：
            // 那些"拖这个点 → 只改 p / 只改 a / 只改周期＋振幅"的算式一起删掉了。
            // 曲线现在走**通用框**（纯变换：缩放 / 旋转 / 平移），模型层一个字都不用改。
            default:
                // 直线 / 箭头：改哪一头就是哪一头（首点或末点）。
                pts[_vertexHandle == ShapeHandle.Anchor ? 0 : pts.Length - 1] = local;
                break;
        }
    }

    /// <summary>
    /// 坐标系**原点**的吸附：外框自己的中心 / 四边（＝四角）＋ 画布中心 / 四条边；
    /// 横竖各判各的（可以只吸 x 不吸 y）。
    ///
    /// 为什么先吸**外框**：老师最常用的动作是"原点拖到框的左下角，只留第一象限"。
    /// 外框角离得最近、又正是那个位置，先判它；画布中心 / 边是第二层，
    /// 用来把整个坐标系摆到屏幕的固定位置上。
    /// 为什么允许分轴吸：只吸 x 或只吸 y 也是常见摆法（贴左边、垂直居中），
    /// 两轴绑成一个点反而难摆。
    ///
    /// 半径 = <see cref="CoordinateSnapRadiusLogical"/>；`Alt` 在调用处已经排除。
    /// 返回的是**画布坐标**，文案给胶囊用（为空 = 没吸住）。
    /// </summary>
    private Vector2 SnapCoordinateOrigin(Stroke s, Vector2 p,
                                         out ShapeSnapKind kind, out string text)
    {
        kind = ShapeSnapKind.None;
        text = "";
        var vp = ViewportCanvas;
        float tol = CoordinateSnapRadiusLogical * DpiScale;
        float cx = (vp.MinX + vp.MaxX) * 0.5f;
        float cy = (vp.MinY + vp.MaxY) * 0.5f;
        float x = p.X, y = p.Y;
        string sx = "", sy = "";
        bool hitX = false, hitY = false;
        bool frameX = false, frameY = false;
        bool centerX = false, centerY = false;
        float bestX = tol, bestY = tol;

        // 外框在**画布坐标**里的中心 / 四条边（坐标系本身不可旋转，但可能被整体缩放）。
        var (minX, minY, maxX, maxY) = s.AxisFrameLocal();
        float fcx = (minX + maxX) * 0.5f, fcy = (minY + maxY) * 0.5f;
        var fCenter = Vector2.Transform(new Vector2(fcx, fcy), s.Transform);
        var fMin = Vector2.Transform(new Vector2(minX, minY), s.Transform);
        var fMax = Vector2.Transform(new Vector2(maxX, maxY), s.Transform);

        void TakeX(float target, string label, bool isFrame, bool isCenter)
        {
            float d = MathF.Abs(p.X - target);
            if (d <= tol && (!hitX || d < bestX))
            {
                x = target; sx = label; bestX = d; hitX = true;
                frameX = isFrame; centerX = isCenter;
            }
        }
        void TakeY(float target, string label, bool isFrame, bool isCenter)
        {
            float d = MathF.Abs(p.Y - target);
            if (d <= tol && (!hitY || d < bestY))
            {
                y = target; sy = label; bestY = d; hitY = true;
                frameY = isFrame; centerY = isCenter;
            }
        }

        // 第一层：外框中心 / 四条边（＝四个角）。
        TakeX(fCenter.X, "框内水平居中", true, true);
        TakeX(fMin.X, "框左边缘", true, false);
        TakeX(fMax.X, "框右边缘", true, false);
        TakeY(fCenter.Y, "框内垂直居中", true, true);
        TakeY(fMin.Y, "框上边缘", true, false);
        TakeY(fMax.Y, "框下边缘", true, false);

        // 第二层：画布中心 / 四条边。
        TakeX(cx, "画布水平居中", false, true);
        TakeX(vp.MinX, "画布左边缘", false, false);
        TakeX(vp.MaxX, "画布右边缘", false, false);
        TakeY(cy, "画布垂直居中", false, true);
        TakeY(vp.MinY, "画布上边缘", false, false);
        TakeY(vp.MaxY, "画布下边缘", false, false);

        if (!hitX && !hitY) return p;

        string Join() => sx.Length > 0 && sy.Length > 0 ? sx + " + " + sy
                        : sx.Length > 0 ? sx : sy;

        if (frameX && frameY)
        {
            kind = ShapeSnapKind.FrameAnchor;
            if (centerX && centerY) text = "框中心";
            else if (sx == "框左边缘" && sy == "框下边缘") text = "左下角";
            else if (sx == "框右边缘" && sy == "框下边缘") text = "右下角";
            else if (sx == "框左边缘" && sy == "框上边缘") text = "左上角";
            else if (sx == "框右边缘" && sy == "框上边缘") text = "右上角";
            else text = Join();
        }
        else if (frameX || frameY)
        {
            kind = ShapeSnapKind.FrameAnchor;
            text = Join();
        }
        else if (centerX && centerY)
        {
            kind = ShapeSnapKind.CanvasCenter;
            text = "画布中心";
        }
        else
        {
            kind = ShapeSnapKind.CanvasEdge;
            text = Join();
        }
        return new Vector2(x, y);
    }

    /// <summary>
    /// 空间坐标系**轴端**的角度吸附（2026-11-03）：
    ///   · **单根轴**吸到特殊角（<see cref="SelectionHandles.SpecialInclinationDegrees"/>：
    ///     0/30/45/60/90/120/135/150；`Shift` 是 15° 硬档，`Alt` 关闭）；
    ///   · **两轴夹角**也吸到同一组特殊角（拖 X 时 Y 固定，拖 Y 时 X 固定）。
    /// 两个候选都算出来，取**离指针更近**的那个；平手时优先“夹角”，因为数学题更关心夹角。
    /// 返回**局部坐标**；文案由这里一起给出，胶囊上直接写吸到了什么。
    /// </summary>
    private Vector2 SnapSpaceAxis(Stroke s, ShapeHandle handle, Vector2 o, Vector2 p,
                                  bool shift, bool alt,
                                  out ShapeSnapKind kind, out string text)
    {
        kind = ShapeSnapKind.None;
        text = "";
        if (alt) return p;
        float len = Vector2.Distance(o, p);
        if (len < 1e-3f) return p;

        float pointerAng = MathF.Atan2(p.Y - o.Y, p.X - o.X);
        float AngleOf(Vector2 q) => MathF.Atan2(q.Y - o.Y, q.X - o.X);
        float DeltaTo(Vector2 q)
        {
            float d = SelectionHandles.NormalizeDegrees((AngleOf(q) - pointerAng) * 180f / MathF.PI);
            return MathF.Min(d, 360f - d);
        }

        // 候选一：单根轴吸到特殊角（SnapEndPoint 已经处理 Shift / 软吸附 / 方向侧）。
        var absPt = SelectionHandles.SnapEndPoint(o, p, shift, alt, out bool absSnap);
        float absDelta = absSnap ? DeltaTo(absPt) : float.MaxValue;

        // 候选二：两轴夹角吸到特殊角（x–y 取 0~180 的较小角）。
        var other = handle == ShapeHandle.SpaceX
            ? new Vector2(s.Points[2].X, s.Points[2].Y)
            : new Vector2(s.Points[1].X, s.Points[1].Y);
        var ov = other - o;
        bool relSnap = false;
        float relDeg = 0f;
        Vector2 relPt = p;
        float relDelta = float.MaxValue;
        if (ov.LengthSquared() > 1e-6f)
        {
            float raw = (pointerAng - AngleOf(other)) * 180f / MathF.PI;
            raw = SelectionHandles.NormalizeDegrees(raw);
            if (raw > 180f) raw -= 360f;               // -180..180
            float absRaw = MathF.Abs(raw);
            if (absRaw > 0.5f && absRaw < 179.5f)
            {
                float snapped = SelectionHandles.SnapFoldedDegrees(
                    absRaw, SelectionHandles.SpecialInclinationDegrees, shift, alt, out relSnap);
                if (relSnap && snapped > 0.5f && snapped < 179.5f)
                {
                    float sign = raw < 0f ? -1f : 1f;
                    float a = AngleOf(other) + sign * snapped * MathF.PI / 180f;
                    relPt = o + new Vector2(MathF.Cos(a), MathF.Sin(a)) * len;
                    relDeg = snapped;
                    relDelta = DeltaTo(relPt);
                }
            }
        }

        bool useRel = relSnap && (!absSnap || relDelta <= absDelta);
        if (!useRel && !absSnap) return p;

        var result = useRel ? relPt : absPt;
        kind = ShapeSnapKind.AxisAngle;
        if (useRel)
            text = $"x–y {relDeg:F0}°";
        else
        {
            // 显示按“数学角”（y 向上）：右 0°、上 90°、左 180°、下 270°。
            float vx = result.X - o.X, vy = result.Y - o.Y;
            float display = SelectionHandles.NormalizeDegrees(
                -MathF.Atan2(vy, vx) * 180f / MathF.PI);
            text = (handle == ShapeHandle.SpaceX ? "x 轴 " : "y 轴 ") + $"{display:F0}°";
        }
        return result;
    }


    /// 8 逻辑像素，横竖分开判，`Alt` 在调用处已经排除。
    /// </summary>
    /// <summary>
    /// 空间坐标系**原点**的吸附：只吸画布中心 / 四条边（没有"自己的框"——
    /// 正半轴坐标系的框是三个轴端现算的，吸框角没有意义）。
    /// 8 逻辑像素，横竖分开判，`Alt` 在调用处已经排除。
    /// </summary>
    private Vector2 SnapSpaceOrigin(Vector2 p, out ShapeSnapKind kind, out string text)
    {
        kind = ShapeSnapKind.None;
        text = "";
        var vp = ViewportCanvas;
        float tol = CoordinateSnapRadiusLogical * DpiScale;
        float cx = (vp.MinX + vp.MaxX) * 0.5f;
        float cy = (vp.MinY + vp.MaxY) * 0.5f;
        float x = p.X, y = p.Y;
        string sx = "", sy = "";
        bool centerX = false, centerY = false;

        if (MathF.Abs(p.X - cx) <= tol) { x = cx; sx = "画布水平居中"; centerX = true; }
        else if (MathF.Abs(p.X - vp.MinX) <= tol) { x = vp.MinX; sx = "画布左边缘"; }
        else if (MathF.Abs(p.X - vp.MaxX) <= tol) { x = vp.MaxX; sx = "画布右边缘"; }

        if (MathF.Abs(p.Y - cy) <= tol) { y = cy; sy = "画布垂直居中"; centerY = true; }
        else if (MathF.Abs(p.Y - vp.MinY) <= tol) { y = vp.MinY; sy = "画布上边缘"; }
        else if (MathF.Abs(p.Y - vp.MaxY) <= tol) { y = vp.MaxY; sy = "画布下边缘"; }

        if (sx.Length == 0 && sy.Length == 0) return p;
        kind = centerX && centerY ? ShapeSnapKind.CanvasCenter : ShapeSnapKind.CanvasEdge;
        text = centerX && centerY ? "画布中心"
             : sx.Length > 0 && sy.Length > 0 ? sx + " + " + sy
             : sx.Length > 0 ? sx : sy;
        return new Vector2(x, y);
    }

    ///
    /// 量的都是**用户看到的那个量**：
    ///   · 直线的 α 按**画布坐标**量（旋转过的线，局部角不等于屏幕角）；
    ///   · 圆的 r 按**画布坐标**量（屏幕上这个圆多大）；
    ///   · 椭圆的 a / b 按**局部坐标**量 —— 那是椭圆自己的半轴
    ///     （它转到哪个方向，长轴都是那么长）。
    /// </summary>
    private void UpdateVertexReadout()
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        if (s == null || pts == null || pts.Length < 2)
        {
            _vertexReadout = VertexReadoutKind.None;
            return;
        }

        var c = Vector2.Transform(pts[0], s.Transform);
        var e = Vector2.Transform(pts[^1], s.Transform);
        switch (s.Kind)
        {
            case StrokeKind.Circle:
            {
                float r = Vector2.Distance(c, e);
                _vertexReadout = VertexReadoutKind.Radius;
                _vertexReadoutValue = r;
                _vertexReadoutSecondary = r * 2f;
                break;
            }

            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.FocusPoint:
            {
                // 拖**焦点三角形的顶点 P**：这一拖没有读数。
                // 理由同三角形顶点那条：P 本身不是老师要看的那个数，
                // 而"|PF₁| + |PF₂| = 2a"是**另一个**读数（不在这一批里，别把 α 报出来充数）。
                _vertexReadout = VertexReadoutKind.None;
                break;
            }

            case StrokeKind.Ellipse:
            case StrokeKind.ConicEllipse:
            {
                bool vertical = _vertexHandle == ShapeHandle.AxisTop;
                var d = pts[^1] - pts[0];
                _vertexReadout = vertical ? VertexReadoutKind.AxisB : VertexReadoutKind.AxisA;
                _vertexReadoutValue = vertical ? MathF.Abs(d.Y) : MathF.Abs(d.X);
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
            case StrokeKind.Trapezoid:
                // 三角形 / 平行四边形 / 梯形**这一轮没有读数**：它们要显示的是内角 / 夹角，
                // 那是第③轮（9.7）的事；现在改顶点时只有"吸到了什么"那一颗胶囊
                // （见 ShapeSnapKind）。**不要**落到 default 去报一个 α：
                // 三个顶点之间根本没有"倾斜角"这个量。
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            case StrokeKind.Sector:
            {
                if (_vertexHandle == ShapeHandle.Rim)
                {
                    float r = Vector2.Distance(pts[0], pts[1]);
                    _vertexReadout = VertexReadoutKind.Radius;
                    _vertexReadoutValue = r;
                    _vertexReadoutSecondary = r * 2f;
                }
                else
                {
                    float a0 = MathF.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X);
                    float a1 = MathF.Atan2(pts[2].Y - pts[0].Y, pts[2].X - pts[0].X);
                    float deg = (a1 - a0) * 180f / MathF.PI;
                    while (deg < 0f) deg += 360f;
                    _vertexReadout = VertexReadoutKind.SectorAngle;
                    _vertexReadoutValue = Math.Clamp(deg, 0f, 359f);
                    _vertexReadoutSecondary = 0f;
                }
                break;
            }

            // 四种曲线的读数（p / 实半轴 / 虚半轴 / 周期＋振幅）2026-09-20 随手柄一起删了：
            // 读数挂在手柄上，手柄没了，那个量就没人拖得动它们了（见 ShapeHandlesOf）。
            case StrokeKind.SpaceCoordinate:
            {
                if (_vertexHandle is ShapeHandle.SpaceX or ShapeHandle.SpaceY)
                {
                    var xv = pts[1] - pts[0];
                    var yv = pts[2] - pts[0];
                    float lx = xv.Length(), ly = yv.Length();
                    if (lx > 1e-3f && ly > 1e-3f)
                    {
                        float cos = Math.Clamp(Vector2.Dot(xv, yv) / (lx * ly), -1f, 1f);
                        _vertexReadout = VertexReadoutKind.SpaceAngle;
                        _vertexReadoutValue = MathF.Acos(cos) * 180f / MathF.PI;
                        _vertexReadoutSecondary = 0f;
                        break;
                    }
                }
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.Coordinate:
            {
                float fx0 = MathF.Min(pts[0].X, pts[1].X);
                float fx1 = MathF.Max(pts[0].X, pts[1].X);
                float fy0 = MathF.Min(pts[0].Y, pts[1].Y);
                float fy1 = MathF.Max(pts[0].Y, pts[1].Y);
                var o = pts[2];
                switch (_vertexHandle)
                {
                    case ShapeHandle.AxisRight:
                    case ShapeHandle.AxisLeft:
                        _vertexReadout = VertexReadoutKind.AxisXLength;
                        _vertexReadoutValue = _vertexHandle == ShapeHandle.AxisRight
                            ? fx1 - o.X : o.X - fx0;
                        break;
                    case ShapeHandle.AxisTop:
                    case ShapeHandle.AxisBottom:
                        _vertexReadout = VertexReadoutKind.AxisYLength;
                        _vertexReadoutValue = _vertexHandle == ShapeHandle.AxisTop
                            ? o.Y - fy0 : fy1 - o.Y;
                        break;
                    case ShapeHandle.Vertex3:
                        // 格距手柄：预览字段在拖动中才有值；没拖时显示当前格距。
                        _vertexReadout = VertexReadoutKind.GridStep;
                        _vertexReadoutValue = _vertexPreviewGridStep > 0f
                            ? _vertexPreviewGridStep : s.AxisGridStepLocal();
                        break;
                    default:
                        // 原点：这一拖只有"吸到了什么"那颗胶囊，没有长度读数。
                        _vertexReadout = VertexReadoutKind.None;
                        break;
                }
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.NumberLine:
                // 数轴同样**没有角度读数**：两个定义元素之间没有"倾斜角"可言
                // （轴永远是水平的，刻度间距也只是个长度）。
                // 落到 default 去报 α 的话，拖端点会给出一个毫无意义的度数。
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            default:
                _vertexReadout = VertexReadoutKind.Inclination;
                _vertexReadoutValue = SelectionHandles.InclinationDegrees(c, e);
                // 直线 / 箭头再报一个**长度**（2026-10-05 用户：和 α 同一个逻辑）。
                _vertexReadoutSecondary = Vector2.Distance(c, e);
                break;
        }
    }

    /// <summary>
    /// 多边形读数（规格 9.7）：三角形 = **三个内角**，平行四边形 = **它自己那两个夹角**。
    /// 两个都**只在拖顶点时显示**。
    ///
    /// **2026-09-20 用户定**："三角形应该在拖动的时候再显示角度，要不然看起来也乱"——
    /// 在那之前三角形是"一选中就显示三个角"，静止摆着也一直挂着三颗角标。
    /// 现在它和平行四边形**走同一条判据**（平行四边形从 2026-09-19 起就是这样）。
    /// 顺带也没了"整体移动 / 旋转三角形时显示角度"：转一个三角形并不会改变它的内角，
    /// 那几个数在那两件事里都是噪音。
    ///
    /// 两个输出：<paramref name="vertices"/> 是那些角的顶点（**画布坐标**，标签贴在它外侧），
    /// <paramref name="degrees"/> 是角度（度）。返回要显示几个角（0 = 这一帧没有这套读数）。
    ///
    /// 顶点取**这一帧屏幕上那个几何**：拖顶点中模型一个字没改，所以走临时几何
    /// （`_vertexPreviewLocal`）乘上它自己的 `Transform`。
    /// **绘制与脏区都只走这一个函数**：两边各算一份的话，标签会按一个位置擦、按另一个位置画，
    /// 拖动久了屏幕上就留一条擦不掉的边（选中框那一套踩过这个坑）。
    /// </summary>
    internal int FillAngleReadout(Span<Vector2> vertices, Span<float> degrees)
    {
        // **只有"拖顶点中"才出读数**（见上面那段）：不是拖顶点这一件事 → 这一帧没有读数。
        if (!_vertexDragging || _vertexTarget == null
            || _vertexTarget.Kind is not (StrokeKind.Triangle or StrokeKind.Parallelogram)
            || _vertexPreviewLocal == null)
            return 0;

        var s = _vertexTarget;
        int n = s.Kind == StrokeKind.Triangle ? 3 : 4;
        if (vertices.Length < n || degrees.Length < 3) return 0;

        var m = s.Transform;
        for (int i = 0; i < 3; i++) vertices[i] = Vector2.Transform(_vertexPreviewLocal[i], m);
        // 第四个顶点**不在临时点表里**（平行四边形存三个点），现推一个——
        // 少了它，报出来的那个角就少了一条边，角度会算错。
        if (n == 4)
            vertices[3] = Vector2.Transform(Stroke.ParallelogramFourth(
                _vertexPreviewLocal[0], _vertexPreviewLocal[1], _vertexPreviewLocal[2]), m);
        return SelectionHandles.PolygonAngles(s.Kind, vertices[..n], degrees);
    }

}
