// [职责] 窗口整体透明度的 DirectComposition 合成期状态（每个覆盖窗口一份）。
// [入口] OverlayWindow 建根视觉时 Create()、RenderFrame 每帧 Apply()、重建设备资源时重建。
// [读写状态] 只持有 IDCompositionEffectGroup + 已提交的档位；真值在 InkEngine.WindowOpacity。
// [落盘 Key] 无（落盘在引擎的 "windowOpacity"）。
// [测试] --settingsaudit（设置页那一行）/ 真机目测

using System;
using Vortice.DirectComposition;

namespace InkEngine;

/// <summary>
/// **整个窗口的透明度**（用户 2026-11-25）。
///
/// 为什么是合成期属性、而不是"把 alpha 传进每个绘制函数"：要淡的东西有几十处
/// （笔迹、系统湿墨轨迹、工具栏、各种面板），一路传 alpha 得改十几处签名，
/// 而且**系统委托墨迹画在交换链之外**，绘制函数根本管不到它。
/// 挂在根视觉的效果组上，整棵子树的合成结果乘同一个不透明度，一次到位。
///
/// 为什么用 <c>IDCompositionEffectGroup</c> 而不是 <c>IDCompositionVisual3::SetOpacity</c>：
/// 后者要 Win10 1809+ 的 visual3；效果组从 Win8 就有，语义完全一样。
///
/// ⚠ 单个组合对象只在**一扇窗口**里用：效果组属于那扇窗口的 DComp 设备，
///   多屏时每扇窗各建一份（见 <see cref="OverlayWindow"/> 的用法）。
/// </summary>
internal sealed class WindowOpacityFx : IDisposable
{
    /// <summary>根视觉上的效果组（null = 这台机器建不出来，退化为永远不透明）。</summary>
    private IDCompositionEffectGroup _effect;
    /// <summary>已经提交给合成器的那一档（避免每帧无谓 Commit）。</summary>
    private float _applied = 1f;

    /// <summary>效果组建出来了没有（建不出来时 Apply 是空操作，功能退化为"不支持"）。</summary>
    public bool Available => _effect != null;

    /// <summary>已经提交的档位（自检用）。</summary>
    public float AppliedForTest => _applied;

    /// <summary>
    /// 在**根视觉**上挂效果组。必须在 <c>SetRoot</c> 之前或之后都行（DComp 是批处理），
    /// 但必须在同一个设备上下文里。返回 false = 这台机器不支持，调用方继续走全不透明。
    /// </summary>
    public bool Create(IDCompositionDevice dcomp, IDCompositionVisual root)
    {
        if (dcomp == null || root == null) return false;
        try
        {
            _effect?.Dispose();
            _effect = null;
            dcomp.CreateEffectGroup(out _effect);
            if (_effect == null) return false;
            root.SetEffect(_effect).CheckError();
            _applied = 1f;
            _effect.SetOpacity(_applied).CheckError();
            return true;
        }
        catch
        {
            // 取不到就算了：功能退化成"永远不透明"，绝不能因为一个外观开关影响启动。
            _effect = null;
            return false;
        }
    }

    /// <summary>
    /// 把这一档刷上去。**只在值真的变了时才提交**（DComp 是批处理，但每帧无谓改属性
    /// 会让合成器每帧重建一遍效果，白白占 GPU）。
    /// </summary>
    public void Apply(IDCompositionDevice dcomp, float opacity)
    {
        if (_effect == null || dcomp == null) return;
        if (MathF.Abs(_applied - opacity) <= 0.001f) return;
        try
        {
            _effect.SetOpacity(opacity).CheckError();
            dcomp.Commit().CheckError();
            _applied = opacity;
        }
        catch
        {
            // 合成器那边不接受（设备丢了 / 正在重建）：标成不可用，
            // 下一帧不再尝试；重建设备资源时会重新 Create。
            Reset();
        }
    }

    /// <summary>设备资源重建时调用：丢掉旧的（属于旧设备，不能跨设备复用）。</summary>
    public void Reset()
    {
        _effect?.Dispose();
        _effect = null;
        _applied = 1f;
    }

    public void Dispose() => Reset();
}
