using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WetInkTrailTest;

/// <summary>
/// 湿墨迹 → DComp 的"委托墨迹轨迹"通道（IDCompositionDelegatedInkTrail）。
///
/// 这条通道回答的问题就是本测试要验证的："正在写的那一笔"能不能交给系统合成器画，
/// 而文档模型、干墨、界面全是你自己的。设计用途是画"还没上屏的那一小段"：
/// 喂点 → 系统立刻画 → 你下一帧把同样的点自己画上去 → 把轨迹里已上屏的点结账删掉。
///
/// 两个必须照做的接法（缺一不可，2026-10-11 真笔实测踩过）：
/// ① 轨迹对象必须**挂进 DComp 视觉树**（作视觉内容、叠在交换链之上）——
///    只 CreateDelegatedInkTrailForSwapChain 不挂树：COM 调用全成功、generation
///    正常递增，但屏幕上永远没有墨。
/// ② 喂进去的点要**每帧结账**（RemoveTrailPoints）——系统单条轨迹有上限
///    （Chromium kMaximumNumberOfDelegatedInkPoints = 128），只喂不删，
///    长笔画超过上限后旧点被系统擦掉，症状"先出墨、中途变透明、抬笔才补全"。
///
/// 版本边界：Win10 没有这条通道；Win11 21H2~23H2 有已知"顶三分之一屏闪烁"
/// （24H2 修）；24H2+ 完整可用。取不到接口时本类安静降级，功能不受影响。
/// </summary>
internal sealed class DelegatedInkTrail
{
    private IDCompositionInkTrailDevice _trailDevice;
    private IDCompositionDelegatedInkTrail _trail;
    private IDCompositionDevice _dcomp;

    /// <summary>喂点后立刻 Commit（试验开关：验证 DComp 批处理是否挡住轨迹更新）。</summary>
    public bool CommitAfterFeed = false;

    /// <summary>通道是否真的可用（接口 + 挂视觉树都成功）。</summary>
    public bool Available { get; private set; }

    /// <summary>探测结果的一句话说明（打到控制台）。</summary>
    public string Note { get; private set; } = "未探测";

    /// <summary>本条轨迹是否进行中。</summary>
    public bool Active { get; private set; }

    /// <summary>诊断：喂进系统的点数 / 已结账删掉的点数（默认模式下应基本相等）。</summary>
    public long PointsFed { get; private set; }
    public long PointsSettled { get; private set; }

    private uint _generation;         // AddTrailPoints 返回的"代"（该代之前的点都已上屏时按它结账）
    private uint _settledGeneration;  // 已结账到哪一代
    private int _outstanding;         // 喂了还没结账的点数（诊断口径）
    private float _radius;

    /// <summary>
    /// 探测并接入通道。<paramref name="rootVisual"/> 是根视觉（纯容器），
    /// <paramref name="swapVisual"/> 是交换链内容视觉——轨迹视觉显式插到它**上面**。
    /// ⚠ 层序要显式给出参照（AddVisual(inkVisual, true, swapVisual)）：
    /// 若轨迹视觉落到交换链**下面**，不透明背景一挡就是"调用全成功但屏幕无墨"。
    /// </summary>
    public void Attach(IDCompositionDevice dcomp, IDXGISwapChain1 swapChain,
                       IDCompositionVisual rootVisual, IDCompositionVisual swapVisual)
    {
        _dcomp = dcomp;
        try
        {
            _trailDevice = dcomp.QueryInterfaceOrNull<IDCompositionInkTrailDevice>();
            if (_trailDevice == null)
            {
                Note = "合成设备不支持 IDCompositionInkTrailDevice（Win10 / 老版 Win11 无此通道）";
                return;
            }

            _trail = _trailDevice.CreateDelegatedInkTrailForSwapChain(swapChain);
            if (_trail == null) { Note = "接口可用但 CreateDelegatedInkTrailForSwapChain 失败"; return; }

            // ★ 接法①：挂视觉树（Chromium 同款）。漏了这步 = 调用全成功但屏幕无墨。
            var inkVisual = dcomp.CreateVisual();
            inkVisual.SetContent(_trail).CheckError();
            rootVisual.AddVisual(inkVisual, true, swapVisual).CheckError();   // 显式盖在交换链之上
            dcomp.Commit().CheckError();

            Available = true;
            Note = "可用（已挂视觉树，显式叠在交换链之上）";
        }
        catch (Exception ex)
        {
            Available = false;
            _trail = null;
            Note = "探测异常: " + ex.Message;
        }
    }

    /// <summary>起笔：开一条新轨迹（颜色 = 笔色，粗细 = 半径，单位是交换链像素）。</summary>
    public void Begin(Color4 color, float radius)
    {
        if (_trail == null || !Available) return;
        if (Active) EndNow();
        try
        {
            _radius = MathF.Max(0.5f, radius);
            _trail.StartNewTrail(color);
            Active = true;
            _generation = 0;
            _settledGeneration = 0;
            _outstanding = 0;
        }
        catch (Exception ex) { Active = false; _trail = null; Note = "StartNewTrail 异常: " + ex.Message; }
    }

    /// <summary>喂一个采样点（交换链坐标，即窗口客户区坐标）。</summary>
    public void AddPoint(float x, float y, float radius = -1f)
    {
        if (!Active || _trail == null) return;
        try
        {
            var p = new DCompositionInkTrailPoint
            {
                X = x,
                Y = y,
                Radius = radius > 0 ? MathF.Max(0.5f, radius) : _radius,
            };
            _generation = _trail.AddTrailPoints(new[] { p }, 1);
            PointsFed += 1;
            _outstanding += 1;
            if (CommitAfterFeed)
            {
                try { _dcomp.Commit(); } catch { }
            }
        }
        catch (Exception ex) { Active = false; Note = "AddTrailPoints 异常: " + ex.Message; }
    }

    /// <summary>
    /// 结账：我们自己已经把这一段画上屏了（Present 之后调用），把轨迹里同样的点删掉。
    /// 接法②——只喂不删会撞 128 点上限。**只在自绘过时调用**；
    /// 如果湿墨完全交给系统（自己不画），就不能删（删了就没了）。
    /// </summary>
    public void SettleAsDrawn()
    {
        if (!Active || _trail == null) return;
        if (_generation == 0 || _generation == _settledGeneration) return;
        try
        {
            _trail.RemoveTrailPoints(_generation);
            _settledGeneration = _generation;
            PointsSettled += _outstanding;
            _outstanding = 0;
        }
        catch (Exception ex) { Active = false; Note = "RemoveTrailPoints 异常: " + ex.Message; }
    }

    /// <summary>收笔：撤轨迹。</summary>
    public void EndNow()
    {
        if (!Active) return;
        Active = false;
        if (_trail == null) return;
        try { if (_generation != 0) _trail.RemoveTrailPoints(_generation); }
        catch { }
        PointsSettled += _outstanding;
        _outstanding = 0;
    }
}
