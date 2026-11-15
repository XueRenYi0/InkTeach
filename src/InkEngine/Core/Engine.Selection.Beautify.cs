// [职责] 选中图片的本地美化（应用/回退）。
// [测试] --inkbeautify

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
    /// <summary>
    /// 操作条按钮。下标与 SelectionHandles.BarButtonAt 的返回值和渲染时的
    /// 图标数组一一对应：0 复制 / 1 删除 / 2 左右翻转 / 3 上下翻转 / 4 旋转。
    /// </summary>
    /// <summary>
    /// 图片选中条上的「美化」：去黄 / 提亮 / 纸白 / 轻锐化，一步撤销。
    /// 只换图片对象的像素，不改尺寸、方向、位置；透明图/已经够白的图直接跳过。
    /// </summary>
    private void BeautifySelection()
    {
        if (_beautifyBusy)
        {
            SetInkStatus("正在美化，请稍等…");
            _dirty = true;
            return;
        }
        if (Doc.Selected.Count == 0) return;
        var todo = new List<Stroke>();
        int already = 0, notImage = 0;
        foreach (var s in Doc.Selected)
        {
            if (!s.IsImage || s.Image == null) { notImage++; continue; }
            // 已美化且还留着原图 → 这一次就是“恢复原图”；原图丢了才跳过。
            if (s.Image.Beautified && s.Image.BeautifyOriginal == null) { already++; continue; }
            todo.Add(s);
        }
        if (todo.Count == 0)
        {
            SetInkStatus(notImage > 0 && already == 0
                ? "选中的不是图片"
                : already > 0 ? "这张已经美化过（原图未保留）" : "没有可美化的图片");
            _dirty = true;
            return;
        }

        // 自检要确定性：直接同步跑；产品里放后台，点下去按钮/画布不卡。
        if (SelfCheckMode)
        {
            var results = BuildBeautifyResults(todo, out int skip);
            ApplyBeautifyResults(results, skip);
            return;
        }

        _beautifyBusy = true;
        SetInkStatus(Loc.T("正在美化 {0} 张…", todo.Count));
        _dirty = true;
        NotifyUiStateChanged();
        var items = todo.ToArray();
        Task.Run(() =>
        {
            try
            {
                var results = BuildBeautifyResults(items, out int skip);
                PostUi(() => ApplyBeautifyResults(results, skip));
            }
            catch (Exception ex)
            {
                PostUi(() =>
                {
                    _beautifyBusy = false;
                    SetInkStatus("美化失败：" + ex.Message);
                    _dirty = true;
                    NotifyUiStateChanged();
                });
            }
        });
    }

    private List<(Stroke Stroke, ImageData Old, ImageData New, bool Reverted)> BuildBeautifyResults(
        IReadOnlyList<Stroke> todo, out int skip)
    {
        var results = new List<(Stroke, ImageData, ImageData, bool)>(todo.Count);
        skip = 0;
        foreach (var s in todo)
        {
            var old = s.Image;
            if (old == null) { skip++; continue; }
            if (old.Beautified)
            {
                // 再点一次 = 恢复原图；原图只在本次进程里保留。
                if (old.BeautifyOriginal != null)
                    results.Add((s, old, old.BeautifyOriginal, true));
                else
                    skip++;
                continue;
            }
            var pixels = PhotoBeautify.Process(old.Bgra, old.Width, old.Height);
            if (pixels == null) { skip++; continue; }
            var enhanced = ImageData.AdoptPremultiplied(old.Width, old.Height, pixels);
            if (enhanced == null) { skip++; continue; }
            enhanced.MarkBeautified(old);
            results.Add((s, old, enhanced, false));
        }
        return results;
    }

    private void ApplyBeautifyResults(
        List<(Stroke Stroke, ImageData Old, ImageData New, bool Reverted)> results, int skip)
    {
        _beautifyBusy = false;
        if (_quit)
        {
            foreach (var r in results)
                if (!r.Reverted) r.New.Release();
            return;
        }

        var compound = new CompoundAction();
        int done = 0, back = 0;
        foreach (var r in results)
        {
            // 后台算的时候用户可能把图删了/换了：对不上就丢掉，不要往新文档里硬塞。
            if (!Doc.Strokes.Contains(r.Stroke) || !ReferenceEquals(r.Stroke.Image, r.Old))
            {
                if (!r.Reverted) r.New.Release();   // 恢复原图那份不在这里释放
                skip++;
                continue;
            }
            compound.Steps.Add(new ReplaceImageAction(r.Stroke, r.Old, r.New));
            if (r.Reverted) back++; else done++;
        }

        if (compound.Steps.Count == 0)
        {
            SetInkStatus(skip > 0 ? "这张已经是白纸 / 透明图，不需要再美化" : "没有可美化的图片");
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }

        compound.Redo(Doc);                 // 先把新像素换上去，再进撤销栈
        Doc.CommitInteractive(compound);
        _dirty = true;
        NotifyUiStateChanged();
        string msg = done > 0 && back > 0
            ? $"已美化 {done} 张、恢复原图 {back} 张；Ctrl+Z 可撤销"
            : back > 0
                ? $"已恢复原图 {back} 张；Ctrl+Z 可撤销"
                : $"已美化 {done} 张；再点一次可恢复原图，Ctrl+Z 可撤销";
        if (skip > 0) msg += $"（{skip} 张跳过）";
        SetInkStatus(msg);
        Console.WriteLine($"[图片] 美化 {done} 张，恢复原图 {back} 张，跳过 {skip} 张");
    }

}
