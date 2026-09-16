using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 自检用的最小界面（开发期专用，不是产品界面）。
///
/// 它只做三件事，每件都是为了验证"引擎↔界面"这条通路上的一条规矩：
///
///   1. **上屏**：画一块蓝底面板 + 一个黄块，好让自检靠数像素确认"界面真的画上去了"；
///   2. **收事件**：数下压/移动/抬起各收到几次，并记下最后一次的坐标
///      —— 坐标必须是**逻辑屏幕坐标**，不是画布坐标（相机一滚就能验出来）；
///   3. **改引擎状态**：面板左半按下 = 走一遍 IEngineCommands，把工具切成橡皮；
///      面板**右半故意返回 false**（"我看见了但我不吃这一下"），
///      用来验证引擎在"矩形命中但界面没消费"时按穿透语义处理。
///
/// 面板在屏幕上的位置由自检算好（<see cref="BoundsPhysical"/>），
/// 因为它要盖住另一个进程的窗口，才能验证"穿透时点击到底归谁"。
/// </summary>
internal sealed class UiProbe : IOverlayUi
{
    private IUiHost _host;
    private RectF _bounds;      // 逻辑屏幕坐标（引擎按 DPI 换算后的那一套）
    private RectF _hole;        // 面板右半边：故意不消费

    /// <summary>面板要盖住的**物理**屏幕矩形，由自检在接入前写好。</summary>
    public RectF BoundsPhysical;

    public string Name => "自检界面";
    public bool Visible => true;

    // ---- 自检要读的计数 ----
    public int Down, Move, Up, StateCalls;
    public float LastX = float.NaN, LastY = float.NaN;
    public Tool LastToolFromState = Tool.Pen;

    public void Attach(IUiHost host) => _host = host;

    public RectF Layout(RectF screen, float dpiScale)
    {
        _bounds = new RectF
        {
            MinX = BoundsPhysical.MinX / dpiScale,
            MinY = BoundsPhysical.MinY / dpiScale,
            MaxX = BoundsPhysical.MaxX / dpiScale,
            MaxY = BoundsPhysical.MaxY / dpiScale,
        };
        float mid = (_bounds.MinX + _bounds.MaxX) * 0.5f;
        _hole = new RectF
        {
            MinX = mid, MinY = _bounds.MinY, MaxX = _bounds.MaxX, MaxY = _bounds.MaxY,
        };
        return _bounds;
    }

    public RectF QueryBounds() => _bounds;

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_bounds.IsEmpty) return;
        // 只画两块实心色：够自检数像素，也够肉眼一眼看出面板在哪。
        using var panel = ctx.CreateSolidColorBrush(new Color4(0.16f, 0.42f, 0.86f, 0.85f), null);
        using var button = ctx.CreateSolidColorBrush(new Color4(0.98f, 0.82f, 0.12f, 1f), null);
        ctx.FillRectangle(new Vortice.RawRectF(_bounds.MinX, _bounds.MinY, _bounds.MaxX, _bounds.MaxY), panel);
        ctx.FillRectangle(new Vortice.RawRectF(_bounds.MinX + 10, _bounds.MinY + 10,
                                               _hole.MinX - 10, _bounds.MaxY - 10), button);
    }

    public bool PointerDown(in UiPointerEvent e)
    {
        Down++;
        LastX = e.X; LastY = e.Y;
        if (!_bounds.Contains(e.X, e.Y)) return false;
        if (_hole.Contains(e.X, e.Y)) return false;     // 看见了但不吃：交给引擎
        _host?.Commands.SetTool(Tool.Eraser);           // 走一遍真正的命令通道
        return true;
    }

    public bool PointerMove(in UiPointerEvent e)
    {
        Move++;
        LastX = e.X; LastY = e.Y;
        return _bounds.Contains(e.X, e.Y);
    }

    public bool PointerUp(in UiPointerEvent e)
    {
        Up++;
        return _bounds.Contains(e.X, e.Y);
    }

    public void OnStateChanged(in UiState state)
    {
        StateCalls++;
        LastToolFromState = state.Tool;
    }
}
