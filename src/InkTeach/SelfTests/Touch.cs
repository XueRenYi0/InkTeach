// 本文件由 App.cs 拆出（2026-10-07）：Touch 这一组。
// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。
// 拆开的目的：产品代码与自检代码互不干扰，人和 AI 读代码时不必互相穿插。

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

    /// <summary>
    /// **触摸手势自检**（8.4.0）：合成触摸注入（可带接触面积、可同时 3 点）走真链路。
    /// 规格见 调研-触摸手势-学校大屏.md；判据是"文档/相机/撤销栈有没有按预期动"，
    /// 不看像素（触摸没有光标，屏幕上看不出对错）。
    /// </summary>
    private void TouchTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 触摸手势自检（合成触摸：单指写 / 双指漫游翻页 / 手掌三指擦 / 长按选 / 选中变换）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        if (SkipIfNoSyntheticInput("触摸手势（需要合成触摸/鼠标）")) { _quit = true; return; }
        if (!EnsureSyntheticTouch())
        {
            Console.WriteLine("  SKIP: 拿不到合成触摸设备（CreateSyntheticPointerDevice(PT_TOUCH) 失败）");
            _quit = true; return;
        }

        float dpi = DpiScale;
        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(200);

        void TouchWrite(float x0, float y0, float x1, float y1, float size = 24f)
        {
            SendTouchesSized(true, (x0, y0, size));
            SettleFrames(30);
            for (int i = 1; i <= 4; i++)
                SendTouchesSized(true, (x0 + (x1 - x0) * i / 4f, y0 + (y1 - y0) * i / 4f, size));
            SettleFrames(30);
            SendTouchesSized(false, (x1, y1, size));
            SettleFrames(80);
        }

        // 多指落下**要一个一个来**：系统对"一次注入里出现两个新触点"只认第一个
        //（实测：同一批塞两个新触点 → 只收到 1 根手指；分两次注入就对了）。
        // 真实手指也是先后落下的，30ms 间隔仍在"干净开始"的 150ms 窗口里。
        void Touch2Down(float x1, float y1, float x2, float y2, float size = 24f)
        {
            SendTouchesSized(true, (x1, y1, size));
            SettleFrames(30);
            SendTouchesSized(true, (x1, y1, size), (x2, y2, size));
            SettleFrames(30);
        }
        void Touch2Move(float x1, float y1, float x2, float y2, float size = 24f)
            => SendTouchesSized(true, (x1, y1, size), (x2, y2, size));
        void Touch2Up(float x1, float y1, float x2, float y2, float size = 24f)
            => SendTouchesSized(false, (x1, y1, size), (x2, y2, size));
        void Touch3Down(float x1, float y1, float x2, float y2, float x3, float y3, float size = 24f)
        {
            SendTouchesSized(true, (x1, y1, size));
            SettleFrames(30);
            SendTouchesSized(true, (x1, y1, size), (x2, y2, size));
            SettleFrames(30);
            SendTouchesSized(true, (x1, y1, size), (x2, y2, size), (x3, y3, size));
            SettleFrames(30);
        }

        // ---- ① 不报面积的屏：单指一律当"写"（不误擦）----
        {
            int before = Doc.Strokes.Count;
            TouchWrite(cx - 120, cy, cx + 120, cy, size: 0f);
            Check("不报面积：单指 = 写字（不误判成手掌擦）",
                  Doc.Strokes.Count == before + 1 && Doc.Strokes[^1].Points.Count >= 3,
                  $"笔画 {before} → {Doc.Strokes.Count}，点数 {Doc.Strokes[^1].Points.Count}");
        }

        // ---- ② 双指纵滑 = 漫游：相机动、墨迹坐标一个没变 ----
        // 方向：**手指往上滑**（内容上移、相机偏移变负）——从顶往下滑会被正确夹住，
        // 那是产品行为而不是 bug（旧用例方向反了，2026-10-05 修）。
        {
            float cam0 = ViewOffsetY;
            var p0 = Doc.Strokes[^1].Points[0];
            SendTouchesSized(true, (cx - 260, cy - 60, 24f), (cx - 60, cy - 60, 24f));
            SettleFrames(40);
            Console.WriteLine($"      [探针] 两指按下后：模式 = {TouchModeForTest}，触点 = {TouchCountForTest}，相机 = {ViewOffsetY:F0}");
            SendTouchesSized(true, (cx - 260, cy - 110, 24f), (cx - 60, cy - 110, 24f));
            SettleFrames(40);
            Console.WriteLine($"      [探针] 滑了 50px：模式 = {TouchModeForTest}，相机 = {ViewOffsetY:F0}，轴 = {_touchDebugAxis}");
            SettleFrames(40);
            SendTouchesSized(true, (cx - 260, cy - 200, 24f), (cx - 60, cy - 200, 24f));
            SettleFrames(120);
            SendTouchesSized(false, (cx - 260, cy - 200, 24f), (cx - 60, cy - 200, 24f));
            SettleFrames(120);
            var p1 = Doc.Strokes[^1].Points[0];
            Check("双指纵滑 = 漫游（相机动、墨迹坐标一个没变）",
                  MathF.Abs(ViewOffsetY - cam0) > 60f && MathF.Abs(p1.X - p0.X) < 0.01f && MathF.Abs(p1.Y - p0.Y) < 0.01f,
                  $"相机 {cam0:F0} → {ViewOffsetY:F0}；首点 ({p0.X:F0},{p0.Y:F0}) → ({p1.X:F0},{p1.Y:F0})");
        }

        // ---- ③ 双指横滑 = 翻页（一次手势只翻一页）----
        {
            int idx0 = ScreenIndex;
            float cam0 = ViewOffsetY;
            // 方向：**往左滑 = 下一页**（和产品一致；旧用例向右滑，翻的是上一页）。
            Touch2Down(cx + 300, cy - 200, cx + 300, cy - 60);
            for (int i = 1; i <= 4; i++)
                Touch2Move(cx + 300 - i * 60, cy - 200, cx + 300 - i * 60, cy - 60);
            SettleFrames(80);
            Touch2Move(cx - 200, cy - 200, cx - 200, cy - 60);   // 继续滑：不该翻第二页
            SettleFrames(80);
            Touch2Up(cx - 200, cy - 200, cx - 200, cy - 60);
            SettleFrames(300);
            Check("双指横滑 = 翻一页（一次手势只翻一页）",
                  ScreenIndex == idx0 + 1 && MathF.Abs(ViewOffsetY - cam0) > 100f,
                  $"屏号 {idx0} → {ScreenIndex}（期望 +1），相机 {cam0:F0} → {ViewOffsetY:F0}");
            // 回第一屏，别把后面的用例带跑
            ViewOffsetY = 0f;
            ClampViewOffset();
            SettleFrames(80);
        }

        // ---- ④ 异步第二指（>150ms）= 忽略，不抢正在写的那一笔 ----
        {
            int before = Doc.Strokes.Count;
            SendTouchesSized(true, (cx - 300, cy + 80, 24f));
            SettleFrames(250);              // 真等过 150ms 的"干净开始"窗口（SettleFrames 的单位是**毫秒**）
            SendTouchesSized(true, (cx - 300, cy + 80, 24f), (cx - 100, cy + 80, 200f));   // 手掌晚到
            SettleFrames(80);
            SendTouchesSized(true, (cx - 260, cy + 80, 24f), (cx - 100, cy + 80, 200f));
            SettleFrames(80);
            SendTouchesSized(false, (cx - 260, cy + 80, 24f), (cx - 100, cy + 80, 200f));
            SettleFrames(120);
            Check("写字中途来的手掌 = 忽略（不抢笔、不误擦）",
                  Doc.Strokes.Count == before + 1 && TouchModeForTest == TouchMode.None,
                  $"笔画 {before} → {Doc.Strokes.Count}，模式 {TouchModeForTest}");
        }

        // ---- ⑤ 快速两指 = 手势：刚起头那一小笔被撤掉 ----
        {
            int before = Doc.Strokes.Count;
            int undo0 = Doc.UndoDepth;
            SendTouchesSized(true, (cx + 260, cy - 180, 24f));
            SendTouchesSized(true, (cx + 260, cy - 180, 24f), (cx + 400, cy - 180, 24f));   // 150ms 内第二指（第一指原地不动）
            SettleFrames(40);
            SendTouchesSized(true, (cx + 200, cy - 120, 24f), (cx + 400, cy - 120, 24f));
            SettleFrames(60);
            SendTouchesSized(false, (cx + 200, cy - 120, 24f), (cx + 400, cy - 120, 24f));
            SettleFrames(120);
            Check("快速两指 = 手势：刚起头那一小笔被撤掉（0 笔、撤销栈不涨）",
                  Doc.Strokes.Count == before && Doc.UndoDepth == undo0,
                  $"笔画 {before} → {Doc.Strokes.Count}，撤销深度 {undo0} → {Doc.UndoDepth}");
        }

        // ---- ⑥ 角色判定表（**面积不参与**）：1 指写 / 2 指手势 / ≥3 指擦 ----
        // 走"直接喂判定"的口子（`TouchClassifyForTest`）：合成触摸的 `rcContact` 系统不认，
        // 而判定本来也只看"触点数 + 干净开始"。
        {
            TouchResetForTest();
            var v1 = TouchClassifyForTest(1, cx, cy, 24f);
            TouchResetForTest();
            var v2a = TouchClassifyForTest(1, cx, cy, 24f);
            var v2b = TouchClassifyForTest(2, cx + 200, cy, 24f);
            TouchResetForTest();
            var v3a = TouchClassifyForTest(1, cx, cy, 24f);
            var v3b = TouchClassifyForTest(2, cx + 150, cy, 24f);
            var v3c = TouchClassifyForTest(3, cx + 300, cy, 24f);
            TouchResetForTest();
            Check("角色判定：1 指 = 写、2 指 = 手势、≥3 指 = 擦（面积不参与）",
                  v1 == TouchVerdict.Write && v2a == TouchVerdict.Write && v2b == TouchVerdict.Gesture2
                  && v3a == TouchVerdict.Write && v3b == TouchVerdict.Gesture2 && v3c == TouchVerdict.Erase,
                  $"1 指 {v1}，2 指 {v2b}，3 指 {v3c}");
        }

        // ---- ⑦ 三指一起落下 = 擦（不依赖面积；鼠标那把橡皮：整笔擦；落点 = 三指中心）----
        {
            EraserKindForTest = Tool.Eraser;     // 固定种类，免得受本机偏好影响
            Doc.Clear();
            Doc.ClearHistory();
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s);
            Doc.InvalidateAll();
            SettleFrames(150);

            Touch3Down(cx - 300, cy - 10, cx - 180, cy - 10, cx - 60, cy - 10);
            SendTouchesSized(true, (cx - 60, cy - 10, 24f), (cx + 60, cy - 10, 24f), (cx + 180, cy - 10, 24f));
            SettleFrames(150);
            SendTouchesSized(false, (cx - 60, cy - 10, 24f), (cx + 60, cy - 10, 24f), (cx + 180, cy - 10, 24f));
            SettleFrames(200);
            Check("三指一起落下 = 擦（包围盒扫过，整条被擦掉）",
                  Doc.Strokes.Count == 0, $"笔画 {Doc.Strokes.Count}（期望 0）");
            Doc.Undo();
            SettleFrames(150);
        }

        // ---- ⑧ 长按 0.5 秒 = 进入选择（长按在对象上 = 点选它）----
        {
            TouchResetForTest();
            Doc.Clear();
            Doc.ClearHistory();
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s);
            Doc.InvalidateAll();
            SettleFrames(150);

            SendTouchesSized(true, (cx, cy, 24f));
            // 长按要按住 500ms；**合成触点久不"喂"会被系统自动抬起**（实测 ~0.5s），
            // 一抬手就把 SelDrag 清回 None（后面的拖动当场变成写字）。
            // 所以按住期间每 100ms 原地补一针（0 位移，不影响"从头到尾没画出去"那条判据）。
            for (int k = 0; k < 8; k++) { SettleFrames(100); SendTouchesSized(true, (cx, cy, 24f)); }
            Check("长按 0.5 秒 = 进入选择（对象上 = 点选）",
                  Doc.Selected.Count == 1 && TouchModeForTest == TouchMode.SelDrag,
                  $"选中 {Doc.Selected.Count}，模式 {TouchModeForTest}，"
                  + $"拖动中 = {SelDragging}，丢了捕获 {_cntCaptureLost} 次");

            // 选中后：单指拖 = 移动（一步撤销）
            var before = s.WorldBounds;
            SendTouchesSized(true, (cx + 60, cy + 40, 24f));
            SettleFrames(60);
            SendTouchesSized(true, (cx + 160, cy + 120, 24f));
            SettleFrames(60);
            SendTouchesSized(false, (cx + 160, cy + 120, 24f));
            SettleFrames(200);
            var after = s.WorldBounds;
            Check("选中后单指拖 = 移动（位置变了、一步撤销）",
                  MathF.Abs(after.MinX - before.MinX) > 40f && MathF.Abs(after.MinY - before.MinY) > 20f,
                  $"({before.MinX:F0},{before.MinY:F0}) → ({after.MinX:F0},{after.MinY:F0})");
            Doc.Undo();
            SettleFrames(150);
            var back = s.WorldBounds;
            Check("移动 = 一步撤销（撤销回原位）",
                  MathF.Abs(back.MinX - before.MinX) < 2f && MathF.Abs(back.MinY - before.MinY) < 2f,
                  $"({back.MinX:F0},{back.MinY:F0}) vs ({before.MinX:F0},{before.MinY:F0})");
        }

        // ---- ⑨ 选中后：双指 = **只缩放**（不含平移、不含旋转；旋转走拖手柄，见 ⑲）----
        {
            Doc.Selected.Clear();
            Doc.Selected.Add(Doc.Strokes[^1]);
            SettleFrames(80);
            var before = Doc.Strokes[^1].WorldBounds;
            float w0 = before.MaxX - before.MinX;
            float cxm = (before.MinX + before.MaxX) * 0.5f, cym = (before.MinY + before.MaxY) * 0.5f;

            // 两指从 200px 宽张到 400px（放大一倍）；两指中点**故意整体右移 100px**——
            // 平移已经和缩放分开，中点移动不该带走对象。**旋转已不在双指里**（走旋转手柄）。
            Touch2Down(cxm - 100, cym, cxm + 100, cym);
            Touch2Move(cxm - 100, cym, cxm + 300, cym);
            SettleFrames(80);
            bool rotating = SelRotating;                 // 双指不该产生度数胶囊
            Touch2Up(cxm - 100, cym, cxm + 300, cym);
            SettleFrames(250);
            var after = Doc.Strokes[^1].WorldBounds;
            float w1 = after.MaxX - after.MinX;
            float h1 = after.MaxY - after.MinY;
            float cx1 = (after.MinX + after.MaxX) * 0.5f, cy1 = (after.MinY + after.MaxY) * 0.5f;
            // 原始笔画是**零高度的横线**：只放大不变向 → 宽度翻倍、高度仍然约 0。
            Check("选中后双指 = 只缩放（不旋转、不平移、无度数胶囊）",
                  w1 > w0 * 1.6f && h1 < w0 * 0.2f && !rotating
                  && MathF.Abs(cx1 - cxm) < 6f && MathF.Abs(cy1 - cym) < 6f,
                  $"宽 {w0:F0} → {w1:F0}，高 {h1:F0}，中心 ({cxm:F0},{cym:F0}) → ({cx1:F0},{cy1:F0})，旋转中 = {rotating}");
            Doc.Undo();
            SettleFrames(150);
            var back = Doc.Strokes[^1].WorldBounds;
            Check("选中变换 = 一步撤销（撤销回原尺寸）",
                  MathF.Abs((back.MaxX - back.MinX) - w0) < 4f,
                  $"宽 {back.MaxX - back.MinX:F0} vs {w0:F0}");
        }

        // ---- ⑩ 漫游开关：单指拖 = 漫游（不落墨）----
        {
            SetUiPref("touch.roam", "1");
            LoadTouchPrefs();
            TouchResetForTest();                     // 清掉上一用例可能残留的合成触点
            int before = Doc.Strokes.Count;
            float cam0 = ViewOffsetY;
            SendTouchesSized(true, (cx - 100, cy - 100, 24f));
            SettleFrames(40);
            // 两段：合成注入偶尔把"第一针移动"当成新 down（重锚），第二针就能正常滚
            SendTouchesSized(true, (cx - 100, cy - 180, 24f));
            SettleFrames(40);
            SendTouchesSized(true, (cx - 100, cy - 260, 24f));
            SettleFrames(60);
            Console.WriteLine($"      [探针] 漫游拖后：模式 = {TouchModeForTest}，相机 = {ViewOffsetY:F0}，拖动中 = {SelDragging}");
            SendTouchesSized(false, (cx - 100, cy - 260, 24f));
            SettleFrames(150);
            Check("漫游开关：单指拖 = 漫游（相机动、不落墨）",
                  Doc.Strokes.Count == before && MathF.Abs(ViewOffsetY - cam0) > 40f,
                  $"笔画 {before} → {Doc.Strokes.Count}，相机 {cam0:F0} → {ViewOffsetY:F0}");
            SetUiPref("touch.roam", "0");
            LoadTouchPrefs();
            ViewOffsetY = 0f;
            ClampViewOffset();
            SettleFrames(80);
        }

        // ---- ⑪ 收场干净：全抬起后状态复位，接着单指还能写 ----
        {
            TouchWrite(cx - 260, cy + 200, cx - 60, cy + 200);
            Check("收场干净：全抬起后模式复位，接着单指还能写",
                  TouchModeForTest == TouchMode.None && TouchCountForTest == 0 && Doc.Strokes.Count > 0,
                  $"模式 {TouchModeForTest}，触点 {TouchCountForTest}，笔画 {Doc.Strokes.Count}");
        }

        // ---- ⑫ 总开关关掉：只剩单指书写（内核开关还在；设置里那一行 2026-10-09 晚已撤，
        //          这里直接调内核开关验行为）----
        {
            SetTouchGesturesFromUi(false);
            TouchResetForTest();
            int before = Doc.Strokes.Count;
            // 双指一起落：不许进手势（也不许擦）——第一根手指仍照常写，第二根被忽略
            SendTouchesSized(true, (cx - 120, cy + 240, 24f), (cx + 120, cy + 240, 24f));
            SettleFrames(120);
            bool modeOK = TouchModeForTest == TouchMode.Write;   // 还在"单指写"，不是 Gesture2 / Erase
            string modeAtTwin = TouchModeForTest.ToString();     // 提示文案用按下那一刻的读数
            SendTouchesSized(true, (cx - 120, cy + 280, 24f), (cx + 120, cy + 280, 24f));
            SettleFrames(80);
            SendTouchesSized(false, (cx - 120, cy + 280, 24f), (cx + 120, cy + 280, 24f));
            SettleFrames(150);
            int mid = Doc.Strokes.Count;                          // 只多了第一根手指写的那一笔
            TouchWrite(cx - 200, cy + 320, cx - 60, cy + 320);    // 单指照样写
            Check("总开关关掉：双指不进手势、单指照样写（保险丝）",
                  modeOK && mid == before + 1 && Doc.Strokes.Count == mid + 1,
                  $"双指时模式 {modeAtTwin}（应 Write——还在写），笔画 {before} → {mid} → {Doc.Strokes.Count}");
            SetTouchGesturesFromUi(true);
            TouchResetForTest();
        }

        // ---- ⑬ 两指长按 = 呼出盘；划向右松手 = 红笔 ----
        {
            static bool SameC(Color4 a, Color4 b) =>
                MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;
            TouchResetForTest();
            RadialCancelForTest("用例起手");
            Tool = Tool.Pen;
            var red = InkPalette.PenBand[1].Color;
            float by = cy + 420;                        // 挑一块空处（别和上面的用例重叠）
            SendTouchesSized(true, (cx - 60, by, 24f), (cx + 60, by, 24f));
            // 长按要 500ms；合成触点久不喂会被系统自动抬起——每 100ms 原地补一针。
            for (int k = 0; k < 8; k++) { SettleFrames(100); SendTouchesSized(true, (cx - 60, by, 24f), (cx + 60, by, 24f)); }
            bool opened = RadialPaletteActive;
            // 划向右（正东 = 红）：两指一起右移两段，再松开
            SendTouchesSized(true, (cx + 60, by, 24f), (cx + 180, by, 24f));
            SettleFrames(60);
            SendTouchesSized(true, (cx + 180, by, 24f), (cx + 300, by, 24f));
            SettleFrames(60);
            SendTouchesSized(false, (cx + 180, by, 24f), (cx + 300, by, 24f));
            SettleFrames(150);
            Check("两指长按 = 呼出盘；划向右松手 = 红笔",
                  opened && !RadialPaletteActive && Tool == Tool.Pen && SameC(CurrentColor, red),
                  $"opened={opened}，active={RadialPaletteActive}，色=({CurrentColor.R:F2},{CurrentColor.G:F2},{CurrentColor.B:F2})");
        }

        // ---- ⑭ 两指轻点 = 呼出盘（留在盘上）；点东南扇区 = 蓝笔并关闭 ----
        {
            static bool SameC(Color4 a, Color4 b) =>
                MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;
            TouchResetForTest();
            RadialCancelForTest("用例起手");
            Tool = Tool.Pen;
            var blue = InkPalette.PenBand[2].Color;
            float by = cy + 420;
            SendTouchesSized(true, (cx - 60, by, 24f), (cx + 60, by, 24f));
            SettleFrames(60);
            SendTouchesSized(false, (cx - 60, by, 24f), (cx + 60, by, 24f));
            SettleFrames(150);
            bool opened = RadialPaletteActive;           // 轻点 = 留在盘上等点选
            SettleFrames(150);                           // 出盘延迟 120ms 之后再点
            // 点"东南"扇区（蓝）：径向距离要大于死区、小于半径（96 逻辑 × 2 倍屏 = 192 物理）
            SendTouches(true, (cx + 130, by + 130));
            SettleFrames(120);
            Check("两指轻点 = 呼出盘；点扇区 = 应用并关闭",
                  opened && !RadialPaletteActive && Tool == Tool.Pen && SameC(CurrentColor, blue),
                  $"opened={opened}，active={RadialPaletteActive}，色=({CurrentColor.R:F2},{CurrentColor.G:F2},{CurrentColor.B:F2})");
        }

        // ---- ⑮ 手掌（大面积）= 擦：**相对基线**判定；按住不动不擦、移动才擦 ----
        {
            TouchResetForTest();
            EraserKindForTest = Tool.Eraser;     // 同 ⑦：固定成整笔擦
            RadialCancelForTest("用例起手");
            Doc.Clear();
            Doc.ClearHistory();
            var target = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) target.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(target);
            Doc.InvalidateAll();
            SettleFrames(150);
            // 先喂一根"正常手指"（size 24）建立手指基线——放远处，别碰目标线
            TouchWrite(cx - 260, cy - 420, cx - 160, cy - 420, size: 24f);
            int before = Doc.Strokes.Count;                 // = 2（目标线 + 刚才那笔）
            // 手掌（size 240 > 基线 24 × 3）按在目标线上：**原地按住 → 不该擦**
            SendTouchesSized(true, (cx - 200, cy, 240f));
            SettleFrames(120);
            bool inErase = TouchModeForTest == TouchMode.Erase;
            int afterHold = Doc.Strokes.Count;
            // 沿目标线移动 → 才擦
            SendTouchesSized(true, (cx - 100, cy, 240f));
            SettleFrames(50);
            SendTouchesSized(true, (cx, cy, 240f));
            SettleFrames(50);
            SendTouchesSized(true, (cx + 100, cy, 240f));
            SettleFrames(50);
            SendTouchesSized(true, (cx + 200, cy, 240f));
            SettleFrames(50);
            SendTouchesSized(false, (cx + 200, cy, 240f));
            SettleFrames(150);
            Check("手掌（大面积）= 擦：相对基线判定；按住不动不擦、移动才擦",
                  inErase && afterHold == before && !Doc.Strokes.Contains(target) && Doc.Strokes.Count == before - 1,
                  $"模式 {inErase}，按住后 {before}→{afterHold}，最终笔画 {Doc.Strokes.Count}（期望 {before - 1}）");
            SetUiPref("touch.palm", "0");
            LoadTouchPrefs();
            SetUiPref("touch.palm", null);
            LoadTouchPrefs();
            TouchResetForTest();
        }

        // ---- ⑯ 触摸 + 橡皮工具：落点反馈 = 鼠标同款（按着出现、松手消失）----
        {
            TouchResetForTest();
            Tool = Tool.Eraser;                  // 整笔擦 → 圆环
            SettleFrames(60);
            bool hover = DrawnCursor != ToolCursorShape.None;     // 触摸悬停不该有落点（会留在屏上）
            SendTouchesSized(true, (cx - 320, cy + 360, 24f));
            SettleFrames(60);
            bool ring = DrawnCursor == ToolCursorShape.Ring;
            SendTouchesSized(false, (cx - 320, cy + 360, 24f));
            SettleFrames(80);
            bool gone = DrawnCursor == ToolCursorShape.None;

            Tool = Tool.PixelEraser;             // 面积擦 → 同款矩形
            SettleFrames(60);
            SendTouchesSized(true, (cx - 320, cy + 360, 24f));
            SettleFrames(60);
            bool rect = DrawnCursor == ToolCursorShape.Rect;
            SendTouchesSized(false, (cx - 320, cy + 360, 24f));
            SettleFrames(80);
            bool gone2 = DrawnCursor == ToolCursorShape.None;
            Tool = Tool.Pen;
            TouchResetForTest();

            Check("触摸 + 橡皮工具：落点反馈 = 鼠标同款（按着出现、松手消失）",
                  !hover && ring && gone && rect && gone2,
                  $"悬停有落点 = {hover}，整笔擦圆环 = {ring}，面积擦矩形 = {rect}，松手后无 = {gone}/{gone2}");
        }

        // ---- ⑰ 选中后：双指放在**别处**也能变换（真机反馈的场景）----
        {
            // 场景 1：走真机的路子——长按选中，再在白板空处放两指转 90°
            TouchResetForTest();
            Doc.Clear();
            Doc.ClearHistory();
            var s2 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s2.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s2);
            Doc.InvalidateAll();
            Tool = Tool.Pen;
            SettleFrames(150);

            SendTouchesSized(true, (cx, cy, 24f));
            for (int k = 0; k < 8; k++) { SettleFrames(100); SendTouchesSized(true, (cx, cy, 24f)); }
            SendTouchesSized(false, (cx, cy, 24f));
            SettleFrames(150);
            bool selOK = Doc.Selected.Count == 1;

            float w2 = s2.WorldBounds.MaxX - s2.WorldBounds.MinX;
            float fx = cx + 320, fy = cy + 300;
            // 双指放在别处：间隔 200 张开到 300（放大 1.5 倍）——只缩放，不旋转
            Touch2Down(fx, fy - 100, fx, fy + 100);
            Touch2Move(fx - 150, fy, fx + 150, fy);
            SettleFrames(80);
            bool rot2 = SelRotating;                     // 双指不该产生旋转
            Touch2Up(fx - 150, fy, fx + 150, fy);
            SettleFrames(250);
            var after2 = s2.WorldBounds;
            float w2a = after2.MaxX - after2.MinX;
            float c2x = (after2.MinX + after2.MaxX) * 0.5f, c2y = (after2.MinY + after2.MaxY) * 0.5f;
            Check("选中后双指放在别处 = 缩放（真机路径：长按选中；不旋转）",
                  selOK && !rot2 && w2a > w2 * 1.3f
                  && MathF.Abs(c2x - cx) < 6f && MathF.Abs(c2y - cy) < 6f,
                  $"选中 = {selOK}，旋转中 = {rot2}，宽 {w2:F0} → {w2a:F0}，"
                  + $"中心 ({cx:F0},{cy:F0}) → ({c2x:F0},{c2y:F0})");

            // 场景 2：图形工具下按在框外会先"收起选区"（原逻辑）——双指手势要把它救回来
            var s3 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s3.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s3);
            Doc.InvalidateAll();
            Doc.Selected.Clear();
            Doc.Selected.Add(s3);
            Tool = Tool.Line;
            SettleFrames(100);
            float w3 = s3.WorldBounds.MaxX - s3.WorldBounds.MinX;
            Touch2Down(fx, fy - 100, fx, fy + 100);
            Touch2Move(fx - 150, fy, fx + 150, fy);
            SettleFrames(80);
            bool rot3 = SelRotating;
            Touch2Up(fx - 150, fy, fx + 150, fy);
            SettleFrames(250);
            var after3 = s3.WorldBounds;
            float w3a = after3.MaxX - after3.MinX;
            Check("图形工具下按下把选区清了 → 双指手势仍能变换（选区救回）",
                  !rot3 && w3a > w3 * 1.3f,
                  $"旋转中 = {rot3}（应 False），宽 {w3:F0} → {w3a:F0}");

            // 场景 3：框选工具下双指放别处——第一根手指起的框要被手势撤掉，不能留着
            var s4 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s4.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s4);
            Doc.InvalidateAll();
            Doc.Selected.Clear();
            Doc.Selected.Add(s4);
            Tool = Tool.Marquee;
            SettleFrames(100);
            float w4 = s4.WorldBounds.MaxX - s4.WorldBounds.MinX;
            Touch2Down(fx, fy - 100, fx, fy + 100);
            Touch2Move(fx - 150, fy, fx + 150, fy);
            SettleFrames(80);
            bool rot4 = SelRotating;
            bool mq = MarqueeActive;             // 手势期间不该还挂着框
            Touch2Up(fx - 150, fy, fx + 150, fy);
            SettleFrames(250);
            var after4 = s4.WorldBounds;
            float w4a = after4.MaxX - after4.MinX;
            Check("框选工具下双指放别处 = 缩放（误起的框被撤掉）",
                  !rot4 && !mq && w4a > w4 * 1.3f,
                  $"旋转中 = {rot4}（应 False），框还在 = {mq}，宽 {w4:F0} → {w4a:F0}");
            Tool = Tool.Pen;
            TouchResetForTest();
        }

        // ---- ⑱ 长按选中后：再按住选中的东西拖 = 移动；按框外拖 = 照常写字 ----
        {
            TouchResetForTest();
            Doc.Clear();
            Doc.ClearHistory();
            var s5 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s5.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s5);
            Doc.InvalidateAll();
            Tool = Tool.Pen;
            SettleFrames(150);

            // 长按选中（点选）
            SendTouchesSized(true, (cx, cy, 24f));
            for (int k = 0; k < 8; k++) { SettleFrames(100); SendTouchesSized(true, (cx, cy, 24f)); }
            SendTouchesSized(false, (cx, cy, 24f));
            SettleFrames(150);
            bool selOK2 = Doc.Selected.Count == 1;

            // **抬手之后再按住**选中的东西拖 = 移动（不是再画一笔）
            var b5 = s5.WorldBounds;
            int strokes0 = Doc.Strokes.Count;
            SendTouchesSized(true, (cx + 40, cy, 24f));      // 按在框内
            SettleFrames(60);
            SendTouchesSized(true, (cx + 140, cy + 80, 24f));
            SettleFrames(60);
            SendTouchesSized(false, (cx + 140, cy + 80, 24f));
            SettleFrames(200);
            var a5 = s5.WorldBounds;
            Check("长按选中后，再按住选中的东西拖 = 移动（不再落墨、状态不残留）",
                  selOK2 && Doc.Strokes.Count == strokes0 && !SelDragging
                  && MathF.Abs(a5.MinX - b5.MinX) > 60f && MathF.Abs(a5.MinY - b5.MinY) > 40f,
                  $"选中 = {selOK2}，笔画 {strokes0} → {Doc.Strokes.Count}，拖动中 = {SelDragging}，"
                  + $"位置 ({b5.MinX:F0},{b5.MinY:F0}) → ({a5.MinX:F0},{a5.MinY:F0})");

            // 按框外拖 = 照常写字（画笔没有"卡住"）
            int strokes1 = Doc.Strokes.Count;
            SendTouchesSized(true, (cx - 350, cy + 380, 24f));
            SettleFrames(60);
            SendTouchesSized(true, (cx - 250, cy + 420, 24f));
            SettleFrames(60);
            SendTouchesSized(false, (cx - 250, cy + 420, 24f));
            SettleFrames(200);
            bool cleared = Doc.Selected.Count == 0;
            Check("按框外拖 = 照常写字（选区收起、画笔不卡）",
                  Doc.Strokes.Count == strokes1 + 1 && cleared,
                  $"笔画 {strokes1} → {Doc.Strokes.Count}，选区已收 = {cleared}");
            TouchResetForTest();
        }

        // ---- ⑲ 选中后：拖**旋转手柄** = 旋转（触摸走鼠标同一条路，带度数胶囊）----
        {
            TouchResetForTest();
            Doc.Clear();
            Doc.ClearHistory();
            var s6 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 30f * dpi };
            for (int i = 0; i <= 20; i++) s6.AddPoint(cx - 200 + i * 20, cy, 0.9f, i);
            Doc.AddStroke(s6);
            Doc.InvalidateAll();
            Tool = Tool.Pen;
            SettleFrames(150);

            // 长按选中（点选）
            SendTouchesSized(true, (cx, cy, 24f));
            for (int k = 0; k < 8; k++) { SettleFrames(100); SendTouchesSized(true, (cx, cy, 24f)); }
            SendTouchesSized(false, (cx, cy, 24f));
            SettleFrames(150);
            bool sel6 = Doc.Selected.Count == 1;
            float w6 = s6.WorldBounds.MaxX - s6.WorldBounds.MinX;

            // 旋转手柄的位置：和服务端**同一份算法**（画与命中同源，见 SelectionHandles.CanvasPosition）
            var frame6 = SelectionHandles.FrameOf(Doc.Selected);
            var handle6 = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame6, dpi);
            float radius6 = Vector2.Distance(handle6, new Vector2(cx, cy));
            // 绕选区中心把"正上方"转到"正左方" = 逆时针 90°
            SendTouchesSized(true, (handle6.X, handle6.Y, 24f));
            SettleFrames(60);
            SendTouchesSized(true, (cx - radius6, cy, 24f));
            SettleFrames(80);
            bool rot6 = SelRotating;
            float deg6 = SelRotationDegrees;
            SendTouchesSized(false, (cx - radius6, cy, 24f));
            SettleFrames(250);
            var after6 = s6.WorldBounds;
            Check("拖旋转手柄 = 旋转（触摸同鼠标一条路，带度数）",
                  sel6 && rot6 && MathF.Abs(deg6) > 60f
                  && (after6.MaxY - after6.MinY) > w6 * 0.5f,
                  $"选中 = {sel6}，旋转中 = {rot6}，读数 {deg6:F0}°，"
                  + $"纵向 {(after6.MaxY - after6.MinY):F0}（原横长 {w6:F0}）");
            Doc.Undo();
            SettleFrames(150);
            TouchResetForTest();
        }

        // ---- ⑳ 三指擦 + 面积橡皮 = 动态大小（快扫变大、松手回基准）----
        {
            TouchResetForTest();
            Doc.Selected.Clear();
            EraserKindForTest = Tool.PixelEraser;
            bool savedDyn = DynamicEraserForTest;
            DynamicEraserForTest = true;
            float baseW = PixelEraserHalfWidthPx;
            // 三指落下 → 擦会话（面积擦），落点在 cx-380 一带
            Touch3Down(cx - 420, cy - 200, cx - 380, cy - 200, cx - 340, cy - 200);
            SettleFrames(30);
            // 连续三段快扫（每段质心 200px / 50ms ≈ 4px/ms）：系数一路涨（生长时间常数 220ms）
            SendTouchesSized(true, (cx - 220, cy - 200, 24f), (cx - 180, cy - 200, 24f), (cx - 140, cy - 200, 24f));
            SettleFrames(50);
            SendTouchesSized(true, (cx - 20, cy - 200, 24f), (cx + 20, cy - 200, 24f), (cx + 60, cy - 200, 24f));
            SettleFrames(50);
            SendTouchesSized(true, (cx + 180, cy - 200, 24f), (cx + 220, cy - 200, 24f), (cx + 260, cy - 200, 24f));
            SettleFrames(50);
            bool pixelDragging = PixelEraseDragging;
            float duringW = PixelEraserCursorHalfWidthPx;
            Check("三指擦（面积橡皮）= 动态大小（快扫变大）",
                  pixelDragging && duringW > baseW * 1.25f,
                  $"基准 {baseW:F0} → 拖动中 {duringW:F0}（×{duringW / baseW:F2}），拖动中标志 = {pixelDragging}");

            // **只抬前两根手指**（合成注入按数组序给 id 1..n；抬 1、2 → 剩 3 号还按着）。
            // 会话不能断、动态不能关、尺寸不能冻回基准
            //（2026-10-05 真机反馈："三指会变大，但剩一根手指接着擦时就不变了"）
            SendTouchesSized(false, (cx + 220, cy - 200, 24f), (cx + 260, cy - 200, 24f));
            SettleFrames(60);
            bool stillErase = TouchModeForTest == TouchMode.Erase;
            bool stillDyn = PixelEraseDragging;
            float oneFingerW = PixelEraserCursorHalfWidthPx;
            Check("三指擦中途剩一根手指按着：会话不断、动态大小还在（不再冻住）",
                  stillErase && stillDyn && oneFingerW > baseW * 1.2f,
                  $"模式 = {(stillErase ? "Erase" : "变了")}，动态标志 = {stillDyn}，"
                  + $"剩一指时尺寸 {oneFingerW:F0}（基准 {baseW:F0}，应还在放大档）");

            // 全抬起：动态归位（尺寸回基准）
            SendTouchesSized(false, (cx + 260, cy - 200, 24f), (cx + 260, cy - 200, 24f), (cx + 260, cy - 200, 24f));
            SettleFrames(200);
            bool goneDyn = !PixelEraseDragging;
            float afterW = PixelEraserCursorHalfWidthPx;
            Check("三指擦全抬起：动态归位（尺寸回基准）",
                  goneDyn && MathF.Abs(afterW - baseW) < 0.5f,
                  $"松手后 {afterW:F0}（基准 {baseW:F0}），拖动中标志 {pixelDragging} → {PixelEraseDragging}");
            DynamicEraserForTest = savedDyn;
            EraserKindForTest = Tool.Eraser;
            TouchResetForTest();
        }

        // 收尾
        TouchResetForTest();
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(150);

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 触摸手势全通（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }


    // =====================================================================
    //  压感采集与笔迹预测的自检
    // =====================================================================

    /// <summary>
    /// 笔迹预测自检（**纯算法**：不需要真笔、不需要屏幕、不画东西）。
    /// 把 `调研-压感与预测-原理.md` 第三节里那些"别甩墨"的约束逐条变成断言。
    /// </summary>
    /* [删除 2026-10-05] 预测算法自检 + 预测尾自检：随老预测系统移除
       （原文备份见 `.revert/2026-10-05-渲染减法/`；恢复见 `已停用-渲染实验.md`）。
    private void PredictorTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 笔迹预测自检（纯算法）===");
        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {what,-36} {detail}");
            if (ok) pass++; else fail++;
        }

        var pred = new PredictedPoint[8];
        static InkPredictor New(double horizonMs = 10.0)
        {
            var p = new InkPredictor { HorizonMs = horizonMs };
            p.ClampHorizon();
            return p;
        }

        // 1) 直线匀速：v = 1 px/ms，地平线 10 ms，阻尼 0.7 → 最远点应在 7 px 处
        {
            var p = New();
            p.Damping = 0.70f;                                  // 显式给值，别跟默认值耦合
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(10, 0, 10);
            int n = p.Predict(pred);
            float dx = n > 0 ? pred[n - 1].X - 10f : float.NaN;
            Check("直线匀速：给出预测点且在前方", n >= 1 && dx > 0, $"点数 {n}，位移 {dx:F2} px");
            Check("直线匀速：位移 = v·τ·阻尼", n >= 1 && MathF.Abs(dx - 7f) < 0.5f, $"期望 7.00，实得 {dx:F2}");
            Check("直线匀速：时间单调递增且不超过地平线",
                  n >= 2 && pred[0].TimeMs > 10 && pred[n - 1].TimeMs <= 20.01 &&
                  pred[1].TimeMs > pred[0].TimeMs,
                  n >= 2 ? $"{pred[0].TimeMs:F1} → {pred[n - 1].TimeMs:F1} ms（最后一点 = 起点 + 10）" : "点不够");
            Check("默认阻尼就是数据扫出来的 0.80", MathF.Abs(new InkPredictor().Damping - 0.80f) < 1e-6f,
                  $"{new InkPredictor().Damping:F2}");
        }

        // 2) 匀加速：二阶项应让它比"只用速度"更远（但被 AccelDamping 收着）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(11, 0, 10);   // v: 1 → 1.2，a = 0.04 px/ms²
            int n = p.Predict(pred);
            float dx = n > 0 ? pred[n - 1].X - 11f : float.NaN;
            float firstOrder = 1.2f * 10f * 0.7f;               // 只算速度项
            Check("匀加速：比一阶更远（加速度项生效）", n >= 1 && dx > firstOrder + 0.2f,
                  $"二阶 {dx:F2} > 一阶 {firstOrder:F2}");
            Check("匀加速：加速度被衰减，不会失控", n >= 1 && dx < firstOrder + 3f, $"二阶 {dx:F2}");
        }

        // 3) 慢速不预测：速度低于阈值时返回 0 个点（慢写时预测只有抖动没有收益）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(0.05f, 0, 5);                 // v = 0.01 px/ms < 0.02
            int n = p.Predict(pred);
            Check("慢速：不预测", n == 0, $"速度 {p.Speed:F3} px/ms → 点数 {n}");
        }

        // 4) 断笔重置：相邻采样间隔 > 20 ms 就当新的一笔（Chromium 的 kMaxTimeDelta）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(5, 0, 5);
            p.Add(100, 0, 105);                                 // 间隔 100 ms
            int n = p.Predict(pred);
            Check("断笔：间隔 100 ms 后重置", p.Count == 1 && n == 0, $"队列长度 {p.Count}，点数 {n}");
        }

        // 5) 反向/急转：新点与当前速度反向 → 丢掉速度，这一帧不预测（拐弯处最容易甩墨）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(10, 0, 10);                   // v = +1
            p.Add(-1, 0, 20);                                   // 立刻反向
            int n = p.Predict(pred);
            Check("急转：反向时丢掉速度", MathF.Abs(p.Speed) < 0.001f && n == 0,
                  $"速度 {p.Speed:F3} px/ms，点数 {n}");
        }

        // 6) 限幅：极快速度下，预测段长度被 MaxDistance 截住（兜底，防长尾）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(500, 0, 5);                   // v = 100 px/ms
            int n = p.Predict(pred);
            float dx = n > 0 ? pred[n - 1].X - 500f : float.NaN;
            Check("限幅：位移不超过 MaxDistance", n >= 1 && dx <= p.MaxDistance + 0.01f,
                  $"位移 {dx:F2} px（上限 {p.MaxDistance}）");
        }

        // 7) 地平线夹取：命令行传进来的值会被收进 8~硬上限。
        //    **别把推荐区间（8~15）当夹取区间**：真机调手感时要能把地平线调到远超推荐值，
        //    否则"过头有多难受"这件事根本看不出来（2026-09-22 用户要 --predictms 100 时才拆开）。
        {
            var lo = New(1.0); var hi = New(100.0); var over = New(1e6);
            Check("地平线：低于 8 ms 收到 8", Math.Abs(lo.HorizonMs - 8) < 0.001, $"{lo.HorizonMs}");
            Check("地平线：100 ms 照收（推荐区间之外仍然可调）",
                  Math.Abs(hi.HorizonMs - 100) < 0.001, $"{hi.HorizonMs}");
            Check($"地平线：超过硬上限 {InkPredictor.HardMaxHorizonMs:F0} ms 才收住",
                  Math.Abs(over.HorizonMs - InkPredictor.HardMaxHorizonMs) < 0.001, $"{over.HorizonMs}");
        }

        // 8) 点数与上限：按采样间隔铺满地平线；**最后一点必须落在正地平线上**
        //    （实测教训：只铺到"离地平线最近的那个整数倍"，5 ms 采样 + 8 ms 地平线会少补 3 ms）
        {
            var p = New(10);
            p.Add(0, 0, 0); p.Add(3, 0, 3); p.Add(6, 0, 6);      // 间隔 3 ms
            int n = p.Predict(pred);
            Check("点数：3 ms 间隔 + 10 ms 地平线 → 铺到地平线",
                  n == 4 && Math.Abs(pred[n - 1].TimeMs - 16.0) < 1e-6,
                  $"点数 {n}，最后一点 {pred[n - 1].TimeMs:F1} ms（应 = 6 + 10）");

            var p8 = New(8);
            p8.Add(0, 0, 0); p8.Add(5, 0, 5); p8.Add(10, 0, 10); // 间隔 5 ms
            int n8 = p8.Predict(pred);
            Check("地平线不被采样间隔截短（8 ms + 5 ms 采样）",
                  n8 == 2 && Math.Abs(pred[n8 - 1].TimeMs - 18.0) < 1e-6,
                  $"点数 {n8}，最后一点 {pred[n8 - 1].TimeMs:F1} ms（应 = 10 + 8）");

            var p2 = New(15);
            p2.MaxPoints = 4;
            for (int i = 0; i < 8; i++) p2.Add(i * 1.0f, 0, i * 1.0);   // 1 ms 间隔
            int n2 = p2.Predict(pred);
            Check("点数：不超过 MaxPoints", n2 <= p2.MaxPoints, $"点数 {n2}（上限 {p2.MaxPoints}）");
        }

        // 9) 成本：预测必须是"顺手就做了"，不能进性能预算
        {
            var p = New();
            const int N = 200_000;
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(10, 0, 10);
            var sw = Stopwatch.StartNew();
            int acc = 0;
            for (int i = 0; i < N; i++)
            {
                p.Add(10 + i * 0.001f, 0, 10 + i * 0.005);
                acc += p.Predict(pred);
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
            Check("成本：每次 < 5 µs", us < 5.0, $"{us:F3} µs/次（累计预测 {acc} 个点）");
        }

        Console.WriteLine($"  合计：{pass} 项通过，{fail} 项失败");
        Console.WriteLine(fail == 0 ? "  PASS: 预测器自检全部通过" : "  FAIL: 预测器自检有失败项");
        _quit = true;
    }

    // ---- 预测尾（鼠标 / 触摸那条路）--------------------------------------
    /// <summary>
    /// 预测尾自检：验"鼠标 / 触摸写出来的墨，末端回到了现在"这条链路的四件事。
    ///
    ///   ① 几何层：尾巴真的接进了几何；**清掉之后几何要回到原样**——这一条专门盯
    ///      几何缓存的键（键里少了"尾版本"，清尾之后会把带尾的旧几何还回来）；
    ///   ② 模型层：尾不进点数、不进包围盒，收笔后也不留在文档里（存档/导出都看不见它）；
    ///   ③ **压感变宽那条渲染路（ID2D1Ink）也要带上尾**：真笔必报压感，走的正是这一路。
    ///      这一条是 2026-09-22 补的——原来只测了"无压感 → 等宽描边"那条，结果
    ///      "鼠标甩得出来、手写板毫无反应"这个 bug 全绿通过（漏的就是这里）；
    ///   ④ 引擎层：真实鼠标拖一笔，`POINTER_INFO` 那条新分支真的在跑，读到的点一个不丢；
    ///   ⑤ 屏幕层：同一位置做"开预测 / 关预测"的 A/B——开着时前方那一带的墨明显更多，
    ///      多出来的就是尾巴（顺便证明尾收掉之后那几个像素被擦干净了）。
    ///
    /// 为什么用 SendInput 的真鼠标、而不是合成笔：这条路的用户场景就是"手写板没开
    /// Windows Ink（设备以 `PT_MOUSE` 上报）"和"触摸屏"，而合成笔走的是 `PT_PEN` 分支，
    /// 验不到这里。真鼠标还会真的产生合并点（一批连发、中间不抽消息），这正是 ③ 要的。
    /// </summary>
    private void PredictTailTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 预测尾自检（鼠标 / 触摸那条路）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-40} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        Doc.InvalidateAll();
        // 相机归零：屏幕层的像素判据要按"画布 → 屏幕"换算，相机不动才换得准。
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        SettleFrames(150);

        // ---- ① / ② 几何与模型：不依赖任何输入设备 ----
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 0f, 1f),
                Width = 6f,
            };
            s.AddPoint(100, 100, 0.5f, 0);
            s.AddPoint(200, 100, 0.5f, 1);

            var b0 = s.BuildGeometry(Gfx.D2DFactory).GetWidenedBounds(6f, Gfx.Round, 0.25f);

            var tail = new List<Vector2> { new(240f, 100f) };
            s.SetRenderTail(tail);
            var b1 = s.BuildGeometry(Gfx.D2DFactory).GetWidenedBounds(6f, Gfx.Round, 0.25f);
            Check("渲染尾接进了几何（右边界往外长了约 40 px）",
                  b1.Right > b0.Right + 30f,
                  $"无尾 {b0.Right:F1} → 有尾 {b1.Right:F1}");

            s.SetRenderTail(null);
            var b2 = s.BuildGeometry(Gfx.D2DFactory).GetWidenedBounds(6f, Gfx.Round, 0.25f);
            Check("尾清掉之后几何回到原样（几何缓存的键带了尾版本，不会还回旧几何）",
                  MathF.Abs(b2.Right - b0.Right) < 0.5f,
                  $"清掉后 {b2.Right:F1}，原本 {b0.Right:F1}");

            Check("渲染尾不进模型：点数还是 2、最后一个点还在 200",
                  s.Points.Count == 2 && MathF.Abs(s.Points[^1].X - 200f) < 0.01f,
                  $"点数 {s.Points.Count}，末点 x {s.Points[^1].X:F2}");
        }

        // ---- ③ 压感变宽那条渲染路（ID2D1Ink）也要带上尾 ----
        //
        // 为什么单开这一条：**真笔必然报压感**，于是一定走 DrawPressureInk 那条路，
        // 而它当初只按 `s.Points` 建 ink 对象——尾被整个丢掉。上面 ① 走的是
        // "无压感 → 等宽描边"，所以照不出这个漏（2026-09-22 用户实测抓到的原话：
        // "还是鼠标甩，手写板不甩"，而两边的 [笔画] 都报"预测尾=有（最多 100 px）"）。
        {
            var probe = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 0f, 1f), Width = 12f,
                HasPressure = true,                       // ← 关键：这一位决定走哪条渲染路
            };
            probe.AddPoint(0f, 0f, 0.3f, 0);
            probe.AddPoint(40f, 0f, 0.6f, 8);
            probe.AddPoint(80f, 0f, 0.9f, 16);
            probe.SetRenderTail(new List<Vector2> { new(120f, 0f), new(160f, 0f) });

            // 离屏渲染（和导出/剪贴板同一条 DrawStroke），取景框把尾那一段包进来。
            var reg = new RectF { MinX = -30, MinY = -30, MaxX = 190, MaxY = 30 };
            int draws0 = OverlayWindow.PressureInkDraws;
            var bgra = _windows.Count > 0
                ? _windows[0].RenderStrokesToBgra(new[] { probe }, reg, 220, 60) : null;
            Check("有压感的笔迹确实走了变宽那条路（ID2D1Ink）",
                  OverlayWindow.PressureInkDraws > draws0,
                  $"变宽绘制次数 {draws0} → {OverlayWindow.PressureInkDraws}");

            // 取景框 1:1 → 像素 x = 画布 x + 30。只数"最后一个真实点(80)之外"那一段：
            // 从像素 130（画布 100，已经越过末点的圆头）到 215。
            int tailRed = 0;
            if (bgra != null)
                for (int y = 0; y < 60; y++)
                    for (int x = 130; x < 215; x++)
                    {
                        int i = (y * 220 + x) * 4;
                        if (i + 3 < bgra.Length && bgra[i + 2] > 170 && bgra[i + 1] < 110 && bgra[i] < 110)
                            tailRed++;
                    }
            Check("有压感的笔迹，预测尾也画得出来（这条以前漏掉了）",
                  tailRed > 0,
                  $"末点之外那一段红像素 {tailRed} 个（漏掉时是 0）");
        }

        // ---- ④ / ⑤ 真鼠标拖一笔 ----
        {
            var oldColor = CurrentColor;
            CurrentColor = new Color4(1f, 0f, 1f, 1f);      // 品红：屏幕上好数
            int msg0 = PtrMessages, samp0 = PtrSamples;

            float x0 = _virtualX + _virtualW * 0.34f;
            float y0 = _virtualY + _virtualH * 0.34f;
            const int Burst = 40;                            // 一口气连发的点数
            float stepX = 8f;

            SendMouse((int)x0, (int)y0, 0);
            SettleFrames(80);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(40);

            // 一批连发、**中间一次消息都不抽**，看这条新分支收到多少点。
            for (int i = 1; i <= Burst; i++) SendMouse((int)(x0 + stepX * i), (int)y0, 0);
            SettleFrames(30);
            int msg1 = PtrMessages, samp1 = PtrSamples;
            int moves = _cntMove;                  // 引擎自己记的"收到几条移动消息"

            // 判据只说我们自己的代码该保证的事：**这条新分支真的在跑，而且读到的点一个不丢**。
            //
            // 这里刻意**不**拿"采样点数 > 消息数"当判据：鼠标几乎不产生合并点——系统对
            // 鼠标输入只保留最新位置（实测连发 40 个点，应用只收到 2 条消息），
            // 合并点是触摸与笔才有的现象。触摸屏上能拿到多少合并点，在真机上跑
            // `--penlive` 看 `[笔画] ... 合并(N 条消息 → M 个采样点)` 那一行即可。
            Check("鼠标这条路在读 POINTER_INFO（新分支真的在跑）",
                  samp1 - samp0 > 0,
                  $"{msg1 - msg0} 条消息 → {samp1 - samp0} 个采样点（系统只投递了 {moves} 条移动）");

            var live = ActiveStroke;
            Check("读到的采样点一个不丢地进了这一笔",
                  live != null && live.Points.Count >= samp1 - samp0 && samp1 - samp0 > 0,
                  $"笔画点数 {live?.Points.Count ?? 0}，读到采样点 {samp1 - samp0}");

            // **A 组之前显式把预测打开**：2026-09-29 起预测默认关（自绘尾会"突突"跳），
            // 自检不能依赖启动默认值——B 组会显式关掉、再复原成开。
            PredictEnabled = true;

            // 再走几小批，让"帧与帧之间"有速度 → 预测器才肯出点（慢速它会主动不给）。
            //
            // **必须"等到这一刻模型里真的有尾"再量像素**：指针偶尔会送来一个"没动"的
            // 采样点，速度掉到 MinSpeed 之下 → 预测器主动收尾（那是正确行为）。
            // 第一次写这段时就撞上了：尾长量到 0、A 组红像素 0 个，看着像"功能没生效"。
            int tailMax = 0;
            float leadMax = 0;
            float x1 = x0 + stepX * Burst;
            int inkOn = -1, ax = 0, ay = 0;
            float leadOn = 0;
            for (int b = 1; b <= 30 && inkOn < 0; b++)
            {
                for (int k = 1; k <= 4; k++)
                    SendMouse((int)(x1 + 10f * ((b - 1) * 4 + k)), (int)(y0 + 3f * b), 0);
                SettleFrames(17);
                if (RenderTailPoints > tailMax) { tailMax = RenderTailPoints; leadMax = PredictedTailLead; }
                if (RenderTailPoints > 0)
                {
                    leadOn = PredictedTailLead;
                    inkOn = MagentaAhead(out ax, out ay);   // A 组：这一刻屏上"末点前方"有多少墨
                }
            }
            Check("拖动过程中预测尾真的产生了", tailMax > 0,
                  $"尾最多 {tailMax} 个点、最长 {leadMax:F1} px");

            // ⑤ 屏幕层。麻烦在于"最后一个真实点前方 5~15 px"那一带**不止尾巴**：
            //    落点反馈圆环也画在指针那儿。所以这里做 **A/B**：
            //       A = 开着预测（那一带有圆环 + 尾巴）
            //       B = 关掉预测再走一小批（那一带只剩圆环）
            //    A 明显多于 B，多出来的就只能是尾巴。顺带把脏区扩边也测了：
            //    尾收掉之后旧尾巴那几个像素必须被擦干净，擦不掉的话 B 会跟 A 一样多。
            int MagentaAhead(out int ox, out int oy)
            {
                ox = oy = 0;
                var st = ActiveStroke;
                if (st == null || st.Points.Count < 2) return -1;
                var a = st.Points[^2];
                var b = st.Points[^1];
                float dx = b.X - a.X, dy = b.Y - a.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 0.01f) return -1;
                // 沿运动方向往前 11 px：真实采样点最远只到 0，这一段只可能是画出来的东西。
                ox = (int)(b.X + dx / len * 11f);
                oy = (int)(b.Y + dy / len * 11f + ViewOffsetY);
                var buf = ScreenProbe.CaptureRegion(ox - 5, oy - 5, 11, 11);
                int ink = 0;
                for (int i = 0; i + 3 < buf.Length; i += 4)
                    if (buf[i + 2] > 190 && buf[i + 1] < 90 && buf[i] > 190) ink++;   // BGRA 里的品红
                return ink;
            }

            // ---- B 组：关掉预测，同一套动作再来一次 ----
            PredictEnabled = false;
            int inkOff = -1, bx = 0, by = 0;
            for (int b = 1; b <= 12 && inkOff < 0; b++)
            {
                for (int k = 1; k <= 4; k++)
                    SendMouse((int)(x1 + 400f + 10f * ((b - 1) * 4 + k)), (int)(y0 + 34f), 0);
                SettleFrames(17);
                if (RenderTailPoints == 0) inkOff = MagentaAhead(out bx, out by);
            }
            PredictEnabled = true;                        // ---- 复原 ----

            Check("最后一个真实点的前方有墨，而且关掉预测就没那么多 = 那些墨就是尾巴",
                  inkOn > 0 && inkOff >= 0 && inkOn > inkOff + 8,
                  $"开预测 ({ax},{ay}) {inkOn} 个 → 关预测 ({bx},{by}) {inkOff} 个；尾长 {leadOn:F1} px");

            SendMouse((int)(x1 + 400f), (int)(y0 + 40f), Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);

            var committed = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            Check("收笔后这一条没有渲染尾（尾不会留在文档里）",
                  committed != null && committed.RenderTail == null,
                  committed == null ? "文档里没有笔画" : $"尾点数 {committed.RenderTail?.Count ?? 0}");

            CurrentColor = oldColor;
        }

        Console.WriteLine();
        Console.WriteLine($"  合计：{pass} 项通过，{fail} 项失败");
        Console.WriteLine(fail == 0 ? "  PASS: 预测尾自检全部通过" : "  FAIL: 预测尾自检有失败项");
        Doc.Clear();
        Doc.ClearHistory();
        Doc.InvalidateAll();
        SettleFrames(120);
        _quit = true;
    }

    /// <summary>
    /// 触摸自检（合成触摸注入）。单开这一条的理由：**触摸这条路只有触摸踩得到**——
    /// 鼠标和笔一次只有一个指针，于是"多指 / 掌根"那一类分支从来没被任何自检走到过。
    /// 2026-09-22 就是在这一片里抓到"第二根手指抢走正在写的那一笔"（写一半的字凭空消失）。
    ///
    /// 四件事：
    ///   ① 合成触摸以 `PT_TOUCH` 真进来（不是被当成鼠标）；
    ///   ② 触摸这一笔建得起来、点收得下；
    ///   ③ 触摸照样有预测尾（触摸没压感 → 走等宽描边那条渲染路，与真笔的 ink 那条不同）；
    ///   ④ **第二根手指按下时，正在写的那一笔不能被换掉**，而且第一根手指还能接着写。
    /// </summary>
    */

    private void TouchGuardTest()
    {        Console.WriteLine();
        Console.WriteLine("=== 触摸自检（合成触摸注入）===");
        if (!EnsureSyntheticTouch())
        {
            Console.WriteLine("  SKIP: 拿不到合成触摸设备（CreateSyntheticPointerDevice(PT_TOUCH) 失败）");
            Console.WriteLine("        这条只能在真触摸屏上手工验：写一笔的同时用另一根手指碰屏，"
                              + "正在写的字不许消失、笔也不许被抢走。");
            _quit = true;
            return;
        }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-40} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        Doc.InvalidateAll();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        var oldColor = CurrentColor;
        CurrentColor = new Color4(1f, 0f, 1f, 1f);
        SettleFrames(150);

        float x0 = _virtualX + _virtualW * 0.34f;
        float y0 = _virtualY + _virtualH * 0.45f;

        // ---- ① 单指按下，看它是不是以 PT_TOUCH 进来 ----
        TouchHud = true;                 // 触点诊断先开着（它只跟"开关打开之后"的指针事件）
        SendTouches(true, (x0, y0));
        // 合成注入是**异步**的：负载重时可能晚几十毫秒才到（2026-10-05 连跑偶发过：
        // 30ms 读取时还没到、下一项多等 40ms 就读到了）。多等一会儿，别冤枉它。
        SettleFrames(120);
        Check("合成触摸以 PT_TOUCH 进来", LastPointerType == Native.PT_TOUCH,
              $"LastPointerType = {DeviceName(LastPointerType)}");

        // ---- ①b 触点诊断（8.3.3）：数得到触点数 ----
        SettleFrames(40);
        Check("触点诊断：数得到触点数（并标出「触摸」）",
              TouchHudNow == 1 && (TouchHudText ?? "").Contains("触摸"),
              $"当前 {TouchHudNow} 指，最多 {TouchHudMax} 指");

        // ---- ② 走一段（预测尾已随老预测系统删除，这里只验点在往里进）----
        float cx = x0;
        for (int i = 1; i <= 14; i++)
        {
            cx = x0 + 14f * i;
            SendTouches(true, (cx, y0));
            SettleFrames(17);
        }
        var live = ActiveStroke;
        Check("触摸这一笔建起来了、点在往里进",
              live != null && live.Points.Count > 5,
              live == null ? "ActiveStroke 为 null" : $"笔画点数 {live.Points.Count}");
        // [删除 2026-10-05] "触摸也有预测尾"检查：随老预测系统移除。

        // ---- ④ 第二根手指按下：正在写的那一笔不许被换掉 ----
        var before = ActiveStroke;
        int downBefore = _cntDown;
        int ptsBefore = before?.Points.Count ?? 0;
        SendTouches(true, (cx, y0), (x0, y0 + 200f));      // ← 第二根手指落屏
        SettleFrames(30);

        Check("第二根手指的按下真的到了引擎（不然下一条会假绿）",
              _cntDown > downBefore, $"_cntDown {downBefore} → {_cntDown}");
        // 注：合成注入的第二根手指**不以 WM_POINTERDOWN 到达**（系统把它并成 UPDATE），
        // 所以"最多几指"这项在注入环境里量不准——真机上才是准的（诊断浮层就是干这个的）。
        Check("第二根手指不许抢走正在写的那一笔",
              ReferenceEquals(ActiveStroke, before), "ActiveStroke 被换成了新对象就是抢走了");
        Check("那一笔已经写下的点一个都没丢",
              before != null && before.Points.Count >= ptsBefore,
              $"{ptsBefore} → {before?.Points.Count ?? 0}");

        // 第一根手指还得能接着写（指针没被抢走）
        cx += 14f;
        SendTouches(true, (cx, y0), (x0, y0 + 200f));
        SettleFrames(30);
        Check("第二根手指按着的时候，第一根手指还能接着写",
              ReferenceEquals(ActiveStroke, before) && before != null && before.Points.Count > ptsBefore,
              $"{ptsBefore} → {before?.Points.Count ?? 0}");

        // ---- 收尾：两根一起抬，交进文档 ----
        SendTouches(false, (cx, y0), (x0, y0 + 200f));
        SettleFrames(250);
        var committed = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        Check("松手后这一笔进了文档",
              committed != null,
              committed == null ? "文档里没有笔画" : $"点数 {committed.Points.Count}");

        CurrentColor = oldColor;
        TouchHud = false;
        Console.WriteLine();
        Console.WriteLine($"  合计：{pass} 项通过，{fail} 项失败");
        Console.WriteLine(fail == 0 ? "  PASS: 触摸自检全部通过" : "  FAIL: 触摸自检有失败项");
        Doc.Clear();
        Doc.ClearHistory();
        Doc.InvalidateAll();
        SettleFrames(120);
        _quit = true;
    }

}
