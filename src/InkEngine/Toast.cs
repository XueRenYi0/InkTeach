namespace InkEngine;

/// <summary>
/// 屏幕上的轻提示（toast）。**只负责显示，不参与命中**：
///   · 不抢焦点、不拦鼠标/笔/触摸，提示期间照常写字、点按钮；
///   · 默认 2 秒（用户 2026-11-08 定："1.5 秒有点短，2 秒可以"），到点自动消失；
///   · 同一时间只显示最后一条，新的覆盖旧的（不排队，避免连续识别时越积越多）。
///
/// 为什么不做成对话框 / 状态条：识别到文本框是"已经完成"的反馈，
/// 用户下一笔就要接着写；任何需要点一下才能继续的东西都会打断课堂。
/// </summary>
public partial class InkEngine
{
    /// <summary>当前提示文字；空串 = 没有提示。</summary>
    internal string ToastText { get; private set; } = "";

    private double _toastUntilMs;

    /// <summary>提示还在显示吗（绘制 / 脏区 / 自检都读这一句）。</summary>
    internal bool ToastVisible => ToastText.Length > 0 && NowMs < _toastUntilMs;

    /// <summary>弹一条 2 秒的轻提示；不抢焦点、不吞输入。</summary>
    internal void ShowToast(string text, int ms = 2000)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ToastText = text;
        // 下限 0.6s，避免调用方传 0 时"刚设进去就过期"，屏幕上闪一下白。
        _toastUntilMs = NowMs + Math.Max(600, ms);
        _dirty = true;
    }

    /// <summary>每帧推进：到点清掉并标脏（旧提示那块要被擦掉）。</summary>
    internal void StepToast()
    {
        if (_toastUntilMs <= 0 || NowMs < _toastUntilMs) return;
        _toastUntilMs = 0;
        ToastText = "";
        _dirty = true;
    }
}
