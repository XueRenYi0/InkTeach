using InkEngine;

namespace InkTeach;

/// <summary>
/// 文字输入宿主后台自检（A2 第 1 步）：不显示窗口、不抢前台。
/// 只验证 Begin/UpdateRect/Commit/Cancel、校验失败保持编辑、单行/多行。
/// 真 IME/光标/选区仍要发布前手测。
/// </summary>
internal static class TextInputAudit
{
    public static int Run()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        var engine = new global::InkEngine.InkEngine();
        ITextInputService input = engine.TextInput;
        Check("初始未编辑", !input.IsEditing && input.EditingId == "", $"editing={input.IsEditing}");

        string committed = null;
        input.Begin(new TextInputRequest
        {
            Id = "audit.single",
            Rect = new RectF { MinX = 10, MinY = 10, MaxX = 210, MaxY = 44 },
            InitialText = "hello",
            Mode = TextInputMode.SingleLine,
            Visible = false,
            Activate = false,
            OnCommit = t => committed = t,
        });
        Check("Begin 进入编辑", input.IsEditing && input.EditingId == "audit.single",
              $"editing={input.IsEditing} id={input.EditingId}");

        input.UpdateRect(new RectF { MinX = 20, MinY = 20, MaxX = 240, MaxY = 60 });
        input.Commit();
        Check("提交文本", committed == "hello" && !input.IsEditing,
              $"text={committed} editing={input.IsEditing}");

        string error = null;
        bool canceled = false;
        input.Begin(new TextInputRequest
        {
            Id = "audit.validate",
            Rect = new RectF { MinX = 10, MinY = 10, MaxX = 210, MaxY = 44 },
            InitialText = "   ",
            Mode = TextInputMode.SingleLine,
            Visible = false,
            Activate = false,
            Validate = t => string.IsNullOrWhiteSpace(t) ? "不能为空" : null,
            OnError = e => error = e,
            OnCancel = () => canceled = true,
        });
        input.Commit();
        Check("校验失败保持编辑", input.IsEditing && error == "不能为空",
              $"editing={input.IsEditing} error={error}");
        input.Cancel();
        Check("取消编辑", !input.IsEditing && canceled, $"editing={input.IsEditing} canceled={canceled}");

        string multi = null;
        input.Begin(new TextInputRequest
        {
            Id = "audit.multi",
            Rect = new RectF { MinX = 10, MinY = 10, MaxX = 310, MaxY = 160 },
            InitialText = "a\r\nb",
            Mode = TextInputMode.MultiLine,
            Visible = false,
            Activate = false,
            OnCommit = t => multi = t,
        });
        bool multiEditing = input.IsEditing;
        input.Commit();
        Check("多行提交", multi == "a\r\nb" && !input.IsEditing,
              $"editingBefore={multiEditing} text={(multi == null ? "null" : multi.Replace("\r", "\\r").Replace("\n", "\\n"))}");

        if (input is IDisposable disposable) disposable.Dispose();
        Console.WriteLine($"  合计：{pass} 通过 / {fail} 失败");
        return fail == 0 ? 0 : 1;
    }
}
