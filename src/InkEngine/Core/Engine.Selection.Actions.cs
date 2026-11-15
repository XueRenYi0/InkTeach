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
    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
        => RunBarButton(SelectionHandles.BarButtonOf(index, BarSurfaceNow), frame, aabb);

    /// <summary>
    /// 执行一个操作条语义。**渲染、命中、`⋯` 菜单行都走这一份**，
    /// 不再用下标二次映射（2026-11-08 收口：隐藏动作没有下标）。
    /// </summary>
    private void RunBarButton(SelBarButton btn, in SelectionFrame frame, in RectF aabb)
    {
        // 下标 ↔ 语义只在 SelBarButton 里写一遍（渲染、命中、动作三处共用它）。
        bool textBoxOnly = SingleTextBoxSelection;
        if (textBoxOnly)
        {
            var tb = Doc.Selected[0].TextBox;
            if (btn == SelBarButton.Color && tb?.Role == TextBoxRole.AiQuestion)
            {
                SetInkStatus("题卡颜色固定为黑题红解析");
                _dirty = true;
                return;
            }
        }
        if (btn != SelBarButton.Ai && btn != SelBarButton.More) CloseSelectionMenu();
        switch (btn)
        {
            // **收起工具条**（用户 2026-09-16 更正：条首那个 ✕ 不是"取消选择"）。
            // 收起后只剩一个小圆钮，点它展开；框和手柄照旧，收起的只是"条"。
            case SelBarButton.Collapse:
                SelBarCollapsed = true;
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                CloseSelectionMenu();
                break;

            case SelBarButton.Beautify:
                BeautifySelection();
                break;

            case SelBarButton.Ai:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                if (SelAiMenuOpen) CloseSelectionMenu();
                else OpenSelectionMenu(ai: true);
                break;

            case SelBarButton.More:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                if (SelMoreMenuOpen) CloseSelectionMenu();
                else OpenSelectionMenu(ai: false);
                break;

            case SelBarButton.Color:
                // AI 题卡的颜色/粗细固定（题干黑、答案红），点开面板也调不了，直接不响应。
                if (Doc.Selected.Count == 1
                    && Doc.Selected[0].TextBox?.Role == TextBoxRole.AiQuestion)
                    break;
                if (SelPanelOpen == SelPanel.Ink)
                {
                    CloseCustomColor();                         // 关面板 = 色板一起关
                    SelPanelOpen = SelPanel.None;
                }
                else
                {
                    SelPanelOpen = SelPanel.Ink;
                }
                break;

            case SelBarButton.FontSize:
                // 只有文字条有这一格：开/关常用字号下拉（10/14/18/24/48/64）。
                CloseCustomColor();
                SelPanelOpen = SelPanelOpen == SelPanel.FontSize ? SelPanel.None : SelPanel.FontSize;
                break;

            case SelBarButton.Lock:
                ToggleSelectionLock();
                break;

            case SelBarButton.Layer:
                CloseCustomColor();                             // 换面板 = 色板跟着关
                SelPanelOpen = SelPanelOpen == SelPanel.Layer ? SelPanel.None : SelPanel.Layer;
                break;

            // `⋯` 里的置顶/置底：直接执行，不再开二级“层级”弹窗。
            case SelBarButton.Top:
                ReorderSelection(toFront: true);
                break;
            case SelBarButton.Bottom:
                ReorderSelection(toFront: false);
                break;

            case SelBarButton.Export:
                // **直接弹系统"另存为"**，格式在那个对话框的类型栏里选（见 ExportFormats）。
                //
                // 早先前这里先开一层我们自己的两格面板，理由是"系统那个下拉点不开"
                // （用户报的"选不到 jpg"）。那个真因后来查清了：**覆盖层每秒抢一次置顶**，
                // 把对话框的下拉挤掉了（见 ReassertTopmost）。真因修掉之后，
                // 用户 2026-09-17 提的那句就成立了——"那下面这两个图标就没有用了吧，
                // 用户都可以自己保存图片了"：两处选格式反而绕，删掉还少一步。
                ExportSelectionPref();
                break;

            case SelBarButton.Library:
            {
                // **收藏**：把选中的对象存成一个条目（磁盘那点事在 ShapeLibrary 里）。
                // 重复内容不写新文件；图标会从“星标＋”换成“实心星＋对勾”。
                int saved = SaveSelectionToLibrary();
                if (saved > 0)
                    Console.WriteLine($"已收藏（{saved} 个对象）");
                else if (LibraryFeedback == LibraryFeedbackKind.Error)
                    Console.WriteLine("收藏失败（选中为空或者写不进去）");
                break;
            }

            // 复制按钮 = **只再制**（原件保留 + 新副本错位）；2026-11-03 起不再写剪贴板。
            // 复制到剪贴板走「选择」上带右端的「复制」（触摸屏也能用）。
            case SelBarButton.Copy:
                CopySelectionAndDuplicate();
                break;

            // 翻转绕**框自己的轴**。框现在一律轴对齐，所以它就是屏幕的左右/上下翻——
            // 和"框永远正着"同一条规则（见 SelectionFrame 的注释）。
            case SelBarButton.FlipH:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: true)));
                break;
            case SelBarButton.FlipV:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: false)));
                break;

            case SelBarButton.Delete:
            {
                // 编辑中直接删：先把原生 EDIT 收掉，别让它留在屏幕上当"残字"。
                if (_textBoxEditing) EndTextBoxEditFromUi(false);
                int n = Doc.Selected.Count, locked = 0;
                foreach (var s in Doc.Selected) if (s.Locked) locked++;
                Doc.DeleteSelected();
                SelBarHover = -1;
                ClearEngineTooltip();
                Console.WriteLine(locked > 0
                    ? $"删除 {n - locked} 个对象（{locked} 个锁定，没删）"
                    : $"删除 {n} 个对象");
                break;
            }
        }
        Laser.Clear();
        _dirty = true;
    }

    /// <summary>
    /// 把"框坐标下的变换"翻译成"画布坐标下的变换"：<c>F⁻¹ · M · F</c>。
    ///
    /// 框是斜的时候，同一句"沿框的横轴放大两倍"在画布坐标里是个斜的缩放。
    /// 共轭就是在两套坐标之间翻译这件事——不用为"斜着的情况"另写一套公式。
    /// </summary>
    private static Matrix3x2 Conjugate(in Matrix3x2 frame, in Matrix3x2 localM)
    {
        if (frame.IsIdentity) return localM;
        if (!Matrix3x2.Invert(frame, out var inv)) return localM;
        return inv * localM * frame;
    }
}
