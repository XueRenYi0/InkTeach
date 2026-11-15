// 日志与反馈：用户在上传日志前写一句“遇到了什么问题”。
//
// 2026-11-08（A2.2）：从“自己建 EDIT”迁到 TextInputService——中文/输入法/候选框/软键盘
// 仍交给系统，但宿主只保留一套；日志页不再维护第二套焦点/DPI/关闭逻辑。
using Vortice.Mathematics;

namespace InkEngine;

public partial class InkEngine
{
    /// <summary>用户填的问题描述；上传/导出时写进 zip 的 `问题.txt`。</summary>
    internal string LogNoteText = "";

    /// <summary>最近一次清屏的摘要（自动写进日志上下文，方便定位“清屏没清干净”这类问题）。</summary>
    internal string LastClearSummary = "";

    private bool _logNoteEditing;

    internal bool LogNoteEditing => _logNoteEditing;

    /// <summary>自检用：把文字直接塞进当前文字宿主，走真实的“读取输入”路径。</summary>
    internal void SetLogNoteTextForTest(string text)
    {
        LogNoteText = text ?? "";
        if (_textInput != null) _textInput.SetTextForTest(LogNoteText);
    }

    internal void BeginLogNoteEditFromUi(float x, float y, float w, float h, bool forTest = false)
    {
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        HideAiPromptEdit();                 // 两个原生输入框不叠在一起

        var input = TextInput;
        if (input == null) return;

        _logNoteEditing = true;
        input.Begin(new TextInputRequest
        {
            Id = "log.note",
            Rect = new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h },
            InitialText = LogNoteText ?? "",
            Mode = TextInputMode.MultiLine,
            MaxLength = 240,
            Visible = !forTest,
            Activate = !forTest,
            Visual = new TextInputVisual
            {
                Background = Opaque(FloatingTheme.Panel),
                Text = FloatingTheme.Text,
                FontSize = 13f,
                Padding = 8f,
            },
            OnCommit = text =>
            {
                string t = (text ?? "").Trim();
                if (t.Length > 240) t = t[..240];
                LogNoteText = t;
                _logNoteEditing = false;
                _dirty = true;
                NotifyUiStateChanged();
            },
            OnCancel = () =>
            {
                _logNoteEditing = false;
                _dirty = true;
                NotifyUiStateChanged();
            },
        });
        _logNoteEditing = input.IsEditing;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void EndLogNoteEditFromUi(bool accept)
    {
        if (!_logNoteEditing && TextInput?.IsEditing != true) return;
        if (TextInput == null)
        {
            _logNoteEditing = false;
            NotifyUiStateChanged();
            return;
        }

        if (accept) TextInput.Commit();
        else TextInput.Cancel();
    }

    internal void BeginLogNoteEditForTest(RectF rect)
        => BeginLogNoteEditFromUi(rect.MinX, rect.MinY,
                                  rect.MaxX - rect.MinX, rect.MaxY - rect.MinY, forTest: true);

    private static Color4 Opaque(Color4 c) => new(c.R, c.G, c.B, 1f);
}
