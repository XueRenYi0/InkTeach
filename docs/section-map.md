# 段落地图（自动生成，勿手改）

> 生成：2026-11-16 ／ 脚本：`tools/gen-section-map.ps1` ／ 只列 >= 600 行的文件。
> 用法：``Read`` 大文件前先在这里找段落标题，再只读那一段（行号漂移后重跑脚本即可）。

## src/InkEngine/Ai/AiCard.cs（2,761 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `internal sealed class TextBoxQuestion` |
| 30 | `internal enum TextBoxRole` |
| 47 | `internal sealed class TextBoxColorSpan` |
| 54 | `internal sealed class TextBoxData` |
| 533 | `internal enum TextBoxLineKind` |
| 546 | `internal enum TextBoxInk` |
| 555 | `internal sealed class TextBoxLine` |
| 579 | `internal sealed class TextBoxRun` |
| 599 | `internal static class TextBoxLayout` |
| 1497 | `public partial class InkEngine` |
| 1558 | `private sealed class AiPlotDone` |
| 1762 | `private sealed class ImageCardDone` |
| 2152 | `private sealed class CardAnswersDone` |

| 行 | 段落 |
| --- | --- |
| 1760 | 图片 → 题卡（2026-11-10） |
| 2150 | 卡上「生成答案」（按需，默认不跑） |
| 2360 | 命中 / 拖动 |
| 2612 | 文字输入（原生 EDIT，中文/输入法交给系统） |

## src/InkEngine/Ai/OcrClient.cs（623 行）

| 行 | 顶层类型 |
| --- | --- |
| 27 | `internal static class OcrClient` |
| 37 | `internal sealed class OcrConfig` |
| 61 | `internal sealed class OcrResult` |
| 75 | `internal sealed class OcrLine` |
| 82 | `internal sealed class ModelListResult` |

## src/InkEngine/Classroom/Classroom.cs（2,104 行）

| 行 | 顶层类型 |
| --- | --- |
| 12 | `internal enum TimerZone` |
| 32 | `internal enum RollZone` |
| 49 | `internal static class WinScale` |
| 99 | `internal static class TimerWin` |
| 451 | `internal static class RollWin` |
| 705 | `public partial class InkEngine` |
| 712 | `private struct TimerRun` |

| 行 | 段落 |
| --- | --- |
| 106 | 版式常量（相对卡片左上角；数字后面那行是"凭什么这么定"） |
| 125 | 暂停统计（秒表） |
| 193 | 主区（环 + 数字 + 说明） |
| 212 | 秒表暂停统计 |
| 286 | 底部主按钮（带文字） |
| 325 | 改时长态 |
| 364 | 最小化态 |
| 407 | 文本格式（纯函数） |
| 480 | 名牌区 |
| 598 | 控制行（人数步进 + 抽奖） |
| 617 | 信息行（不重复池） |
| 628 | 顶栏 / 底行 |
| 650 | 无名单时的学号范围 |
| 707 | 计时器 |
| 1198 | 计时窗指针 |
| 1435 | 计时窗接输入小窗（穿透时） |
| 1545 | 点名 |
| 2011 | 点名名单（Names.txt） |
| 2078 | 自检钩子（开发期用） |

## src/InkEngine/Classroom/ClassroomDraw.cs（717 行）

| 行 | 顶层类型 |
| --- | --- |
| 25 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 80 | 通用小工具（卡片 / 按钮 / 名牌 / 环） |
| 163 | 计时窗 |
| 197 | 顶栏：左边分段（凹槽 + 选中药丸） |
| 227 | 顶栏：右上角三个窗口钮（收起 / 全屏 / 关闭） |
| 354 | 左栏：总时长 + 关键统计 |
| 397 | 右栏：分段表 |
| 475 | 点名窗 |
| 491 | 顶栏：右上角一个 ✕ |
| 496 | 名牌区 |
| 518 | 控制行：人数步进（左）＋ 抽奖（右） |
| 532 | 信息行：不重复池 |
| 537 | 底行：名单 / 学号范围 |
| 563 | 绘制小工具 |

## src/InkEngine/Core/Engine.cs（2,878 行）

| 行 | 顶层类型 |
| --- | --- |
| 20 | `public enum SelectMode` |
| 28 | `internal enum PassThroughMode` |
| 45 | `public partial class InkEngine` |
| 576 | `internal enum CaptureDrag { None, Move, L, R, T, B, TL, TR, BL, BR }` |
| 2213 | `internal enum VertexReadoutKind` |
| 2343 | `internal enum AlignSnapKind` |
| 2352 | `private enum AlignLineRole` |
| 2359 | `private struct AlignAxisCandidate` |
| 2461 | `private enum FrameDrive` |

| 行 | 段落 |
| --- | --- |
| 47 | document / interaction state |
| 141 | 自动存档（崩溃恢复） |
| 191 | 板书历史（**白板槽**的快照；见 BoardHistory，2026-10-14 加、10-16 改槽） |
| 335 | 历史清理（墨迹 B）：启动后**首个空闲帧**做一次 |
| 434 | 动态橡皮（8.3.4 起；8.3.6 换形状；8.3.7 定窗口与门槛） |
| 629 | 白板底纹（方格 / 横线） |
| 1101 | 选择手势（框选工具 = 选择工具） |
| 1195 | 停顿成型（手写一笔停一下 → 变成图形）。规格与实测数据见 计划-图形工具.md §四十二。 |
| 1239 | armed（幽灵期）"按住拖动"的两件事（**只给直线用**，见 ArmedStrokeMove） |
| 1317 | `--clean N`：入笔前的净化 |
| 1332 | 湿墨档位（2026-10-13 新增；2026-10-15 精简为两档：应用湿墨层撤除） |
| 1401 | 自绘预测尾（2026-10-15 接入：线性预测，Chromium 同族） |
| 1481 | 长度限速（在"像素"上做，治"跳跃的笔头"） |
| 1724 | 文档缩放（只缩文档，50/75/100/125/150/200/300） |
| 2026 | 交互矢量图的参数滑杆（v2.1） |
| 2074 | 图形定义元素编辑（直线 / 箭头 / 圆 / 椭圆）：和"拖对象"同一条快路 |
| 2332 | 对象吸附（2026-10-18） |
| 2486 | 压感采集与笔迹预测（实现见 Input/PenInput.cs、Prediction/InkPredictorLinear.cs） |
| 2509 | 书写期间的分配 / GC 仪表（低配机排查用） |
| 2592 | perf accounting |
| 2605 | 界面接入口 |
| 2613 | 工具带色位 / 最近颜色（2026-10-23 起；2026-10-28 扩成 8 个可改色位） |
| 2642 | 荧光笔的 5 个色位 / 5 档浓度（2026-10-28） |
| 2666 | 面板的"接输入小窗"（方案 B，见 计划-底层对接界面.md 4.1） |
| 2724 | 延时探针（见 Latency.cs） |

## src/InkEngine/Core/Engine.Host.cs（2,337 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 18 | Startup |
| 226 | 委托墨迹轨迹（笔 / 鼠标 / 触摸都喂） |
| 236 | 湿墨档位（0..1） |
| 274 | 笔迹预测：三档收口（2026-10-15） |
| 280 | 书写期间的 GC 低延迟档 |
| 293 | 压感 → 粗细 |
| 319 | 用户开关：压感粗细（2026-10-01，「更多 → 设置 → 书写」） |
| 329 | 精细笔迹（2026-10-07 加开关；**2026-10-09 晚用户定：开关撤掉、常开**） |
| 338 | 模拟压力与笔锋：**已全部停用**（2026-10-05，代码保留） |
| 348 | 中心线曲线化（过点 Catmull-Rom ＋ 角点保护） |
| 373 | 笔迹运动模型（2026-10-03 对照台 → 2026-10-05 收敛） |
| 504 | D1：亚像素输入（默认开；`--nohimetric` 退回 D0 对照） |
| 513 | 呈现节奏 |
| 608 | 荧光笔的 5 个色位 + 5 档浓度 |
| 983 | 原始输入"被哪道门挡住的"分项计数（诊断，2026-10-07 加） |
| 998 | "到底有没有中间点可捞"的自适应判据（2026-10-07 加） |
| 1209 | 放映期间的"临时全局热键"（2026-09-30 用户定方案①） |
| 1288 | Message loop |
| 1446 | 文档高清补丁：每帧同步一次（由引擎挑驱动窗口） |
| 1922 | Window procedure |

## src/InkEngine/Core/Engine.Selection.Gesture.cs（1,466 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `public partial class InkEngine` |
| 227 | `private enum AutoSelZone` |

## src/InkEngine/Core/Engine.Selection.Vertex.cs（1,052 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 19 | 图形端点编辑：起手 / 每帧 / 提交 |
| 439 | 四个轴端：**只改自己那一截**（2026-11-03） |
| 512 | 格距手柄（第 6 颗，开网格才有） |

## src/InkEngine/Core/Engine.SelectionBar.cs（731 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `public partial class InkEngine` |
| 619 | `internal enum AiMenuKind { PlotOnly, Hand, Image }` |

| 行 | 段落 |
| --- | --- |
| 19 | 操作条（九格）+ 浮动面板 + 提示条：状态与动作 |
| 412 | 自定义取色板（8.2.0：色相条 + 饱和度/明度方块 + 当前色预览） |

## src/InkEngine/Core/InkSerializer.cs（882 行）

| 行 | 顶层类型 |
| --- | --- |
| 10 | `internal enum BlockKind : byte` |
| 19 | `internal struct CanvasBlock` |
| 49 | `internal static class InkSerializer` |

| 行 | 段落 |
| --- | --- |
| 210 | 画布块 |
| 288 | v2：图像像素 |
| 303 | v4：擦除区间表 |
| 312 | v5：锁定标记 |
| 315 | v8：线型 |
| 321 | v9：坐标系网格 |
| 324 | v10：这一笔有没有真实压感（见 Stroke.HasPressure） |
| 328 | v11：曲线的朝向（见 Stroke.CurveAxis） |
| 334 | v12：双曲线画不画那两条虚线渐近线（见 Stroke.ShowAsymptotes） |
| 338 | v17：棱柱底面几边形（见 Stroke.PrismSides） |
| 342 | v23：椭圆（带焦点）的两个字段（见 Stroke.FocusTriangle / FocusPointU） |
| 350 | v24：坐标系网格的格距（见 Stroke.AxisGridStep） |
| 356 | v25 / v26：文本框内容（只有 Kind == TextBox 才写；其它对象一个字节都不加） |
| 420 | v34：交互矢量图（只有 Kind == Figure 才写） |
| 581 | v5：锁定标记（老文件没有这一位，一律"不锁"） |
| 584 | v8：线型（老文件没有这一位，一律"实线"） |
| 592 | v9：坐标系网格（老文件没有这一位，一律"不要网格"） |
| 595 | v10：这一笔有没有真实压感（老文件没有这一位，一律"没有" = 等宽描边） |
| 598 | v11：曲线的朝向（老文件没有这一位，读进来就是字段默认值 0） |
| 614 | v12：渐近线开关（老文件没有这一位，一律"画"） |
| 618 | v17：棱柱底面几边形（老文件没有这一位，一律四棱柱） |
| 627 | v23：椭圆（带焦点）的"画不画焦点三角形" ＋ "P 的参数角" |
| 636 | v24：坐标系网格的**格距**（见 Stroke.AxisGridStep） |
| 647 | v25 / v26：文本框内容（只有 Kind == TextBox 才有） |
| 786 | v34：交互矢量图 |
| 793 | v13：抛物线的第二个点**换了含义**，老文件要迁移一次 |
| 816 | v14：双曲线**拆成两套半轴**，老文件要把第三个点补出来 |

## src/InkEngine/Core/Model.EditActions.cs（920 行）

| 行 | 顶层类型 |
| --- | --- |
| 11 | `internal abstract class EditAction` |
| 47 | `internal static class EditRegion` |
| 57 | `internal sealed class AddStrokesAction : EditAction` |
| 79 | `internal sealed class DwellShapeAction : EditAction` |
| 124 | `internal sealed class CompoundAction : EditAction` |
| 154 | `internal sealed class RemoveStrokesAction : EditAction` |
| 175 | `internal sealed class EraseIntervalsAction : EditAction` |
| 177 | `internal sealed class Item` |
| 285 | `internal sealed class SplitErasedAction : EditAction` |
| 287 | `internal sealed class Item` |
| 352 | `internal sealed class ClearAction : EditAction` |
| 387 | `internal sealed class ClearSlidesAction : EditAction` |
| 431 | `internal sealed class SetStrokePropAction : EditAction` |
| 433 | `public enum Prop { Color, Width, Lock, Dash, Grid }` |
| 611 | `internal sealed class ReplaceImageAction : EditAction` |
| 639 | `internal sealed class ReorderStrokesAction : EditAction` |
| 686 | `internal sealed class TransformObjectsAction : EditAction` |
| 770 | `internal sealed class GlyphEraseAction : EditAction` |
| 805 | `internal sealed class SetStrokeGeometryAction : EditAction` |
| 889 | `internal sealed class SetFigureParamsAction : EditAction` |

## src/InkEngine/Core/Model.InkDocument.cs（2,012 行）

| 行 | 顶层类型 |
| --- | --- |
| 22 | `internal sealed class PageSlot` |
| 32 | `internal sealed class InkDocument` |

| 行 | 段落 |
| --- | --- |
| 34 | 页（每页一套内容） |
| 148 | 页（每页一套内容） |
| 326 | mutation primitives (no history; the actions below drive these) |
| 429 | user operations |
| 1633 | 属性编辑（颜色 / 粗细 / 锁定）与层级 |

## src/InkEngine/Core/Model.Stroke.cs（971 行）

| 行 | 顶层类型 |
| --- | --- |
| 13 | `internal sealed partial class Stroke` |
| 385 | `public sealed class InkPiece` |

| 行 | 段落 |
| --- | --- |
| 469 | 圆柱 / 圆锥 / 圆台 / 球：可见的几笔 ＋ 被挡住的那半圈（细虚线） |
| 479 | 长方体：九条看得见的棱 ＋ 三条被挡住的棱（细虚线） |
| 486 | 棱柱 / 棱锥 / 棱台：看得见的棱 ＋ 被挡住的（细虚线）——实虚由通用判据分 |
| 496 | 四面体：六条棱，全实线 |
| 502 | 坐标系 / 数轴 / 空间坐标系：轴线 ＋ 箭头（＋ 网格） |
| 510 | 正切：一支（一条开曲线）＋ 两条竖直渐近线（细虚线） |
| 523 | 双曲线：两支（中间有抬笔）＋ 两条渐近线（细虚线） |
| 538 | 其余（直线 / 箭头 / 矩形 / 椭圆 / 圆 / 三角形 / 平行四边形 / |

## src/InkEngine/Core/Model.Stroke.Curves.cs（1,130 行）

| 行 | 顶层类型 |
| --- | --- |
| 11 | `internal sealed partial class Stroke` |

| 行 | 段落 |
| --- | --- |
| 13 | 坐标系 / 数轴（2026-09-19 第三批） |
| 257 | 四种曲线（2026-09-20 第四批）：抛物线 / 双曲线 / 正弦 / 余弦 |

## src/InkEngine/Core/Model.Stroke.Solids.cs（884 行）

| 行 | 顶层类型 |
| --- | --- |
| 11 | `internal sealed partial class Stroke` |

| 行 | 段落 |
| --- | --- |
| 13 | 立体图形（2026-09-20 第五批：照 InkClass 的 case 6/7 搬过来） |
| 118 | 长方体（两笔：正面矩形 → 深度） |
| 164 | 四面体（两笔：底面三角形 → 顶点） |
| 188 | 棱柱 / 棱锥 / 棱台（两笔：底面正 n 边形 → 顶上那个中心） |

## src/InkEngine/Core/Model.Stroke.Tangent.cs（2,566 行）

| 行 | 顶层类型 |
| --- | --- |
| 11 | `internal sealed partial class Stroke` |

| 行 | 段落 |
| --- | --- |
| 13 | **正切 y = tan x**（2026-09-20 第十五批） |
| 1698 | 空间坐标系：三根从原点出发的轴（抬笔分段，避免连出假线） |
| 2290 | 空间直角坐标系：原点 → 三个正半轴端点，各带一个箭头 |
| 2314 | 数轴：一条水平线 + 右端一个箭头 |
| 2323 | 坐标系：十字轴 + 两个箭头 |

## src/InkEngine/Core/Model.Types.cs（853 行）

| 行 | 顶层类型 |
| --- | --- |
| 11 | `public enum Tool` |
| 269 | `public struct RectF` |
| 316 | `internal sealed class DirtyRegion` |
| 395 | `internal enum StrokeKind` |
| 722 | `public enum CurveAxis` |
| 754 | `public enum StrokeDash` |
| 805 | `internal static class PressureWidth` |
| 847 | `internal struct InkPoint` |

## src/InkEngine/Core/Selection.cs（3,377 行）

| 行 | 顶层类型 |
| --- | --- |
| 10 | `internal enum SelHandle` |
| 66 | `internal enum ShapeHandle` |
| 137 | `internal enum SelBarButton` |
| 190 | `public enum SelBarSurface` |
| 206 | `internal enum ShapeSnapKind` |
| 256 | `internal enum SelPanel` |
| 292 | `internal struct SelectionFrame` |
| 350 | `internal static class SelectionHandles` |
| 2655 | `internal enum PickPart` |
| 3104 | `internal enum PanelPart` |

| 行 | 段落 |
| --- | --- |
| 519 | 直线 / 箭头：两个端点手柄 |
| 548 | 图形的**定义元素**手柄（直线 / 箭头 / 圆 / 椭圆） |
| 1101 | 倾斜角 α 与画线吸附 |
| 1369 | 一族"特殊形状"吸附（规格 9.6） |
| 1698 | 读数（规格 9.7）：图形的**姿态角** / 三角形的**内角** / 平行四边形的**夹角** |
| 2265 | 操作条（复制 / 删除 / 左右翻转 / 上下翻转 / 旋转） |
| 2583 | 浮层尺寸 token（8.2.0：三处浮层共用这一套） |
| 2638 | 自定义取色板（8.2.0，色相条 + 饱和度/明度方块）的尺寸（逻辑像素） |
| 3299 | 选中条弹层（AI / `⋯` 共用一套几何，2026-11-08） |

## src/InkEngine/Doc/DocImport.cs（715 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 39 | 底部页码条（"像打开 PPT 一样"——2026-10-07 用户定，见 计划-文档模式-状态模型.md） |
| 152 | 文档页空间（墨迹和桌面板书 / PPT 批注互不污染；相机各自记忆） |

## src/InkEngine/Doc/DocPages.cs（602 行）

| 行 | 顶层类型 |
| --- | --- |
| 31 | `internal sealed class DocPages` |
| 37 | `public sealed class Spec` |
| 77 | `public sealed class Page` |

| 行 | 段落 |
| --- | --- |
| 137 | 诊断计数（自检判据；HUD 的补充口径） |
| 147 | 打开 / 关闭 |
| 227 | 后台渲染线程（worker） |
| 334 | 视口窗口：生成与回收 |
| 545 | 查询（翻页 / 相机范围 / 渲染） |

## src/InkEngine/Doc/DocPatch.cs（800 行）

| 行 | 顶层类型 |
| --- | --- |
| 40 | `internal sealed class DocPatchCache : IDisposable` |
| 55 | `internal sealed class Patch` |
| 69 | `private sealed class Request` |

| 行 | 段落 |
| --- | --- |
| 172 | 查询 |
| 242 | 倍率 / 让开 / 关闭 |
| 366 | 每帧同步（UI 线程） |
| 646 | 后台渲染线程 |
| 771 | 几何小工具 |

## src/InkEngine/Export/Export.cs（1,178 行）

| 行 | 顶层类型 |
| --- | --- |
| 26 | `internal static class ExportFormats` |
| 88 | `internal static class ExportImages` |
| 118 | `internal static class BmpWriter` |
| 172 | `internal static class JpegWriter` |
| 228 | `internal static class PngWriter` |
| 364 | `internal static class ExportFileDialog` |
| 367 | `private class OpenFileName` |
| 465 | `private struct CREATESTRUCT_W` |

| 行 | 段落 |
| --- | --- |
| 444 | 对话框"第一帧就在中间"：WH_CBT 钩子 |
| 903 | 墨迹文件（.inkb）的保存 / 打开（墨迹 A，2026-10-01） |
| 1109 | "打开文档…"（图片批量 / PDF；2026-10-07，图片与 PDF 导入第一步） |

## src/InkEngine/Input/Engine.Input.cs（2,028 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 18 | Pointer input |
| 700 | 停顿成型：计时 / 触发 / 按住调整 / 定型（规格见 计划-图形工具.md §四十二） |
| 1129 | 自检入口（`--dwelltest` 用；四条都是"薄封装"，不含任何逻辑） |
| 1161 | 黑匣子（`--reclive` / `--replayinput`）用的薄钩子 |

## src/InkEngine/Input/Engine.TouchGestures.cs（751 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 19 | 触摸手势（8.4.0）——规格与调研见 调研-触摸手势-学校大屏.md |
| 44 | 双指纵滑的惯性（2026-10-28） |

## src/InkEngine/Input/GoogleInkSlidingWindow.cs（670 行）

| 行 | 顶层类型 |
| --- | --- |
| 28 | `internal enum GiToolType : byte { Unknown, Mouse, Touch, Stylus }` |
| 31 | `internal struct GiStrokeInput` |
| 62 | `internal sealed class GiStrokeInputBatch` |
| 103 | `internal struct GiModeledStrokeInput` |
| 117 | `internal struct GiInputMetrics` |
| 124 | `internal sealed class GiInputModelerState` |
| 137 | `internal static class GiMath` |
| 177 | `internal interface IGiInputModel` |
| 185 | `internal sealed class GiPassthroughInputModel : IGiInputModel` |
| 233 | `internal sealed class GiSlidingWindowInputModel : IGiInputModel` |
| 237 | `private sealed class Integrals` |
| 299 | `private enum DerivativeField { Position, Velocity }` |
| 567 | `internal enum GiInputModelKind { Passthrough, SlidingWindow }` |
| 569 | `internal sealed class GiStrokeInputModel` |
| 646 | `internal static class GiSlidingWindowConfig` |

| 行 | 段落 |
| --- | --- |
| 6 | google/ink 的 SlidingWindowInputModeler 1:1 移植（C#） |
| 26 | 基础类型（stroke_input.h / modeled_stroke_input.h） |
| 135 | 数学工具（geometry/internal/lerp.h、angle、distance、vec 的用到的部分） |
| 175 | InputModel 接口（input_model_impl.h） |
| 183 | PassthroughInputModeler（1:1） |
| 231 | SlidingWindowInputModeler（1:1） |
| 565 | StrokeInputModeler（1:1，编排层） |

## src/InkEngine/Input/InkStrokeModeler.cs（1,321 行）

| 行 | 顶层类型 |
| --- | --- |
| 37 | `internal struct InkVec2 : IEquatable<InkVec2>` |
| 77 | `internal enum InkEventType` |
| 85 | `internal struct InkInput : IEquatable<InkInput>` |
| 114 | `internal struct InkResult` |
| 133 | `internal struct InkTipState` |
| 142 | `internal struct InkStylusState` |
| 153 | `internal sealed class InkStrokeModelException : Exception` |
| 163 | `internal sealed class InkLoopContractionMitigationParams` |
| 174 | `internal sealed class InkPositionModelerParams` |
| 182 | `internal sealed class InkSamplingParams` |
| 192 | `internal sealed class InkStylusStateModelerParams` |
| 198 | `internal sealed class InkWobbleSmootherParams` |
| 207 | `internal enum InkPredictionKind` |
| 215 | `internal sealed class InkStrokeModelParams` |
| 325 | `internal static class InkModelMath` |
| 457 | `internal sealed class InkWobbleSmoother` |
| 459 | `private struct Sample` |
| 468 | `private sealed class State` |
| 555 | `internal sealed class InkPositionModeler` |
| 663 | `internal sealed class InkLoopContractionMitigationModeler` |
| 665 | `private struct SpeedSample` |
| 739 | `internal struct InkRawInputProjection` |
| 749 | `internal sealed class InkStylusStateModeler` |
| 751 | `private sealed class ModelerState` |
| 986 | `internal interface IInkInputPredictor` |
| 995 | `internal sealed class InkStrokeEndPredictor : IInkInputPredictor` |
| 1039 | `internal sealed class InkStrokeModel` |
| 1041 | `private struct InputAndCorrectedPosition` |

| 行 | 段落 |
| --- | --- |
| 6 | ink-stroke-modeler 的 1:1 移植（C#） |
| 32 | types.h / types.cc |
| 158 | params.h |
| 321 | internal/utils.h / utils.cc（数学工具） |
| 449 | internal/wobble_smoother.h / .cc |
| 550 | internal/position_modeler.h / .cc |
| 655 | internal/loop_contraction_mitigation_modeler.h / .cc |
| 734 | internal/stylus_state_modeler.h / .cc |
| 982 | internal/prediction/input_predictor.h + stroke_end_predictor.h / .cc |
| 1030 | stroke_modeler.h / .cc（总装） |

## src/InkEngine/Input/StrokeMotion.cs（837 行）

| 行 | 顶层类型 |
| --- | --- |
| 26 | `internal enum StrokeMotionMode` |
| 40 | `internal static class StrokeMotion` |
| 123 | `private struct GaussEvent` |
| 130 | `private sealed class MotionCache` |
| 315 | `internal sealed class OneEuroAxis` |

| 行 | 段落 |
| --- | --- |
| 76 | 参数（命令行可调；默认值分别来自各自上游/文献） |
| 93 | M6g（2026-10-08 新增）：高斯权 + 速度自适应 σ |
| 117 | 静态复用输出缓冲 |
| 129 | 每笔缓存 |
| 265 | M2：google/ink 滑动窗 |
| 312 | M4：1€ 滤波 |
| 364 | M5：固定窗口算术平均（Xournal++/GIMP 思路的自实现） |
| 384 | M5+（mean2）：距离窗平均 + 收笔追赶 |
| 470 | 速度：最近 ~6 点的路径速度（px/ms）→ σ、λ 的目标值 |
| 501 | 高斯权平均 |
| 515 | 可选：向原始末点混一点（λ 随速度） |
| 524 | M7：Xournal++ VelocityGaussian（速度高斯权重平均 + 收笔二次样条） |
| 695 | 输出缓冲与时间轴 |
| 774 | 命令行 |
| 789 | M6g（2026-10-08）：高斯权 + 速度自适应 |

## src/InkEngine/Platform/DiagLog.cs（1,270 行）

| 行 | 顶层类型 |
| --- | --- |
| 31 | `internal static class DiagLog` |
| 510 | `sealed class Tee : TextWriter` |
| 624 | `struct MEMORYSTATUSEX` |
| 643 | `public struct PerfSnapshot` |
| 1010 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 33 | 自检的口子（同 Recovery / BoardHistory 那套套路） |
| 54 | 环境 / 会话（2026-10-17 加：日志从"只有文本"升级成"可机读"） |
| 200 | 导出（「更多 → 设置 → 导出日志…」走这里） |
| 372 | 内部：行装配 / 限流 / 落盘 |
| 508 | tee：接住 Console 输出（原样转发 + 记一份） |
| 567 | 本机信息 / 性能快照（2026-10-17 用户定） |
| 640 | 性能快照（写进日志，排查"卡顿"用） |
| 734 | session.json（zip 里给分析工具读的机读摘要） |
| 805 | 设备卡（台数 / 型号 / 活跃统计；老师零操作） |
| 975 | 异步上传（2026-10-17）：点「日志」时界面绝不能被网络卡住 |
| 1041 | 日志上云（2026-10-17 用户定） |

## src/InkEngine/Platform/Engine.Hotkey.cs（625 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 18 | Hotkeys |
| 169 | 连续微调会话（2026-10-05） |

## src/InkEngine/Platform/Engine.Keyboard.cs（628 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |
| 123 | `private sealed class KeyGesture` |

| 行 | 段落 |
| --- | --- |
| 18 | 批注键盘模式 |
| 107 | 工具键的"手势"状态机 |
| 292 | 荧光笔的 5 个色位 / 5 档浓度（2026-10-28） |
| 365 | 笔的 8 个色位 / 最近颜色（2026-10-23 起；2026-10-28 扩成可改色位） |
| 487 | 滚轮 / ↑↓：同一类"阅读视野"输入（放在键盘这一份，避免再开 partial） |

## src/InkEngine/Platform/Engine.Radial.cs（674 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 18 | 呼出盘（Ctrl+Alt+Shift+Q）：按住 → 划向扇区 → 松手 |
| 20 | 来龙去脉：《调研-笔键方案.md》附录 C/D（键盘呼出版；笔身键版留待真机实测硬件）。 |
| 442 | 呼出盘自检钩子（--radialtest / --radialshow） |

## src/InkEngine/Platform/Engine.Update.cs（1,124 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 18 | 自动更新（2026-09-29） |
| 20 | 分工：**网络全在后台线程**，主线程只做两件事——把结果变成状态文字、 |
| 691 | 面板的"接输入小窗"（方案 B） |
| 693 | 界面回调的统一入口：**都是防弹的** |

## src/InkEngine/Platform/Native.cs（1,451 行）

| 行 | 顶层类型 |
| --- | --- |
| 8 | `internal static class Native` |
| 164 | `public struct TRACKMOUSEEVENT` |
| 173 | `public struct ICONINFO` |
| 227 | `public struct DEVMODE` |
| 249 | `public struct CHARFORMAT2W` |
| 275 | `public struct PARAFORMAT2` |
| 305 | `public struct RECT` |
| 313 | `public struct POINT` |
| 319 | `public struct CHARRANGE` |
| 328 | `public struct MONITORINFO` |
| 337 | `public struct WNDCLASSEX` |
| 354 | `public struct MSG` |
| 365 | `public struct POINTER_INFO` |
| 386 | `public struct POINTER_PEN_INFO` |
| 407 | `public struct POINTER_TOUCH_INFO` |
| 609 | `public struct GESTURECONFIG` |
| 677 | `public struct RAWINPUTDEVICE` |
| 701 | `public struct RAWINPUTHEADER` |
| 710 | `public struct RAWMOUSE` |
| 728 | `public struct RAWINPUT` |
| 792 | `public struct PROCESS_MEMORY_COUNTERS_EX2` |
| 961 | `public struct PAINTSTRUCT` |
| 1191 | `public struct POINTER_TYPE_INFO` |
| 1327 | `public struct POINTER_DEVICE_INFO` |
| 1373 | `public struct MOUSEINPUT` |
| 1381 | `public struct INPUT` |
| 1403 | `public struct KEYBDINPUT` |
| 1411 | `public struct INPUT_KBD` |
| 1430 | `public struct BITMAPINFOHEADER` |
| 1446 | `public struct BITMAPINFO` |

| 行 | 段落 |
| --- | --- |
| 10 | window messages |
| 97 | window styles |
| 119 | hotkey modifiers |
| 125 | pointer |
| 243 | dpi |
| 246 | structs |
| 421 | user32 |
| 576 | 系统手势：在本窗口内关掉"按住不动 = 右键" |
| 662 | pointer input |
| 667 | 原始输入（Raw Input）：`--rawprobe` 用来数"设备到底报了多少条" |
| 760 | kernel32 |
| 816 | keyboard/mouse state (used for modifier checks) |
| 859 | 线程消息（PPT 轮询线程 → 主线程的唤醒） |
| 871 | 子窗口排查（导出对话框探针用） |
| 958 | painting |
| 977 | screen capture (verification only) |
| 1062 | 剪贴板（截图落到剪贴板、粘贴图片） |
| 1123 | DWM：合成配速 |
| 1151 | 合成笔输入（仅开发期实验用） |
| 1153 | 目的：验证"委托墨迹轨迹"（IDCompositionDelegatedInkTrail）这条通道到底 |
| 1210 | 注册表（只读，用来诊断"系统笔设置"） |
| 1363 | synthetic input (used by the automated input-path test) |
| 1390 | 合成键盘输入（自检用：验证全局热键真的能"按"出来） |

## src/InkEngine/Platform/Update.cs（951 行）

| 行 | 顶层类型 |
| --- | --- |
| 11 | `public enum UpdateStage` |
| 53 | `internal static class UpdateFeed` |
| 259 | `private sealed class Probe` |
| 443 | `internal sealed class Manifest` |
| 935 | `public static class ProductInfo` |

| 行 | 段落 |
| --- | --- |
| 168 | 同时发出，谁先答完谁先被处理（完成顺序 = 响应快慢） |
| 222 | 下载候选排序（2026-10-03 起"优先国内"） |
| 454 | 版本比较 |
| 488 | 取清单 |
| 587 | 下载与校验 |

## src/InkEngine/Ppt/Ppt.cs（1,844 行）

| 行 | 顶层类型 |
| --- | --- |
| 41 | `internal static class PptStore` |
| 163 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 10 | PPT 模式：引擎侧的状态机与存取 |
| 218 | PPT 直映（2026-10-09）：「打开文档…」里选了 PPT → 借一次前台 → 让系统里现成的 |
| 243 | "上线保障"：启动放映后的 8 秒里，放映窗起来了却没在前台 → 提一把 |
| 513 | 存取（参考 ICC：进入读回、退出写盘；我们多一条"翻页就先存上一页"） |
| 598 | 翻页命令（给底部那条 + 菜单用） |
| 627 | 底部那一条（PptBar）：翻页 / 页码 / 菜单 |
| 639 | ---- 位置（可拖动 + 持久化） |
| 641 | 位置（可拖动 + 持久化） |
| 671 | 交互状态 |
| 709 | 穿透模式下的"接输入小窗"（条那一块） |
| 738 | 菜单里的开关与确认 |
| 802 | 文档缩放档位浮层（2026-11-16）：点百分比格弹出来 |
| 1223 | 按下之后移动够了 = **直接拖动**（不用先长按——用户 2026-09-26 定的 |
| 1239 | 拖动中：条跟着指针走 |
| 1248 | 缩放条：同一套"点击 vs 拖动"（百分比格按住拖 = 挪缩放条） |
| 1251 | 悬停高亮（给绘制用） |
| 1428 | 穿透模式下"PPT 条那一块"的接输入小窗 |
| 1785 | 位置的持久化 |

## src/InkEngine/Rendering/Engine.Cursor.cs（800 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |
| 33 | `internal enum ToolCursorShape` |

| 行 | 段落 |
| --- | --- |
| 18 | 指针形状（光标） |
| 495 | 滚动条（拖动 + 悬停） |
| 523 | 整屏翻页（"一屏 = 一页"） |

## src/InkEngine/Rendering/Engine.UiBridge.cs（994 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 18 | 界面接入（引擎边界） |
| 313 | 自检钩子（测试要驱动"换工具/操作条按钮/拖动中移动"这些私有路径） |
| 909 | 浮层主题（选中操作条 / 小面板用它画） |
| 927 | 界面偏好的存取（引擎只当仓库，不解释内容） |

## src/InkEngine/Rendering/Overlay.Capture.cs（789 行）

| 行 | 顶层类型 |
| --- | --- |
| 21 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 23 | 截图模式（8.3.0）：遮罩/挖洞（洞里是冻结帧）/取景框/准线/读数/调整手柄 |
| 52 | ⓪ 底：**冻结帧铺满整屏**（8.3.1）。 |
| 72 | ① 遮罩：42% 黑铺满整个屏幕，框里挖洞（EvenOdd） |
| 99 | ② 全屏准线（8.3.1：**不再自绘小十字**——系统十字在外面跟着， |
| 124 | ③ 取景框：白垫 + 琥珀线 + 四角小方块（有框才画） |
| 142 | ④ 尺寸读数：框右下角外侧（贴边翻到上方/夹进屏幕）； |
| 171 | ⑤ 顶部提示（模式名 + 怎么操作） |
| 186 | ⑥ 调整阶段：8 个手柄 + 「✓ 完成 / ✕ 取消」；待机/拖框中：右上角「✕ 取消」 |
| 292 | 跟着鼠标的预览：放大格在上，色块/HEX 在下 |
| 360 | 冻结帧位图缓存（每个窗口一份，按 buffer 引用判"换没换"） |
| 517 | 触点诊断浮层（8.3.3，默认关） |
| 568 | 橡皮读数浮层（8.3.7，默认关；调参用） |

## src/InkEngine/Rendering/Overlay.Content.cs（756 行）

| 行 | 顶层类型 |
| --- | --- |
| 21 | `internal sealed partial class OverlayWindow` |
| 621 | `private enum PillPlace` |

| 行 | 段落 |
| --- | --- |
| 23 | Content layer |

## src/InkEngine/Rendering/Overlay.cs（1,417 行）

| 行 | 顶层类型 |
| --- | --- |
| 20 | `internal static class Mem` |
| 52 | `internal static class Gfx` |
| 270 | `internal sealed partial class OverlayWindow : IDisposable` |
| 338 | `internal struct ScrollBarLayout` |

| 行 | 段落 |
| --- | --- |
| 421 | 内容层（画布空间分块缓存） |
| 465 | 界面缓存 |
| 480 | 脏区上屏状态 |
| 751 | 延时探针 |
| 806 | 委托墨迹轨迹（微软的低延迟湿墨通道） |

## src/InkEngine/Rendering/Overlay.Frame.cs（1,662 行）

| 行 | 顶层类型 |
| --- | --- |
| 21 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 23 | Frame |

## src/InkEngine/Rendering/Overlay.PptBar.cs（785 行）

| 行 | 顶层类型 |
| --- | --- |
| 22 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 373 | 回放（墨迹 C） |

## src/InkEngine/Rendering/Overlay.Radial.cs（657 行）

| 行 | 顶层类型 |
| --- | --- |
| 21 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 23 | 呼出盘（Ctrl+Q）：按住 → 划向扇区 → 松手 |
| 25 | 和落点反馈一样画在**浮动层**、坐标和指针同源（画布坐标 + CanvasToWindow）， |

## src/InkEngine/Rendering/Overlay.Readout.cs（1,572 行）

| 行 | 顶层类型 |
| --- | --- |
| 21 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 23 | 多边形读数（规格 9.7）：三角形的三个内角 / 平行四边形的两个夹角 |
| 1460 | 文档恢复提示卡（"上次看到第 N 页 · 55%" / 继续 / 从头） |

## src/InkEngine/Rendering/Overlay.SelectionBar.cs（1,065 行）

| 行 | 顶层类型 |
| --- | --- |
| 22 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 472 | ① 粗细滑条（连续；拖动实时数值；右端笔尖预览） |
| 547 | ②③ 线型：实线 / 虚线 / 点线 |
| 588 | ④ 色板 |
| 689 | ① 饱和度（横）／明度（纵）方块 |
| 704 | ② 竖直色相条 |
| 721 | ③ 当前色预览 |
| 737 | 取色板两张缓存位图（色相/尺寸变了才重画） |

## src/InkEngine/Rendering/Overlay.Ui.cs（1,445 行）

| 行 | 顶层类型 |
| --- | --- |
| 21 | `internal sealed partial class OverlayWindow` |

| 行 | 段落 |
| --- | --- |
| 23 | 界面层 |

## src/InkEngine/Rendering/Ui.cs（1,675 行）

| 行 | 顶层类型 |
| --- | --- |
| 23 | `public interface IOverlayUi` |
| 183 | `public interface IUiHost` |
| 310 | `public enum TimerMode` |
| 321 | `public interface IEngineCommands` |
| 823 | `public static class InkPalette` |
| 964 | `public static class Hsv` |
| 1016 | `public static class ShapeSpec` |
| 1160 | `public static class LibraryCategoryIds` |
| 1538 | `public enum UiTouchScrollPhase` |
| 1574 | `public static class UiTouchFling` |
| 1661 | `public sealed class NullUi : IOverlayUi` |

## src/InkEngine/Shapes/Engine.Library.cs（1,105 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |
| 29 | `internal enum LibraryFeedbackKind` |

| 行 | 段落 |
| --- | --- |
| 18 | 图库（"我的图形"）：把选中的对象存起来，之后从图形面板最后那一段取回来 |
| 190 | 整理模式的框选 / 排序 |
| 203 | 触摸上下滚动（单指拖卡片区；点一下仍是选中） |
| 212 | 页签拖动 / 滚动 |

## src/InkEngine/Shapes/Engine.Library.Input.cs（1,013 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `public partial class InkEngine` |

| 行 | 段落 |
| --- | --- |
| 453 | 整理模式 |
| 490 | 回收站（使用态） |
| 598 | 指针移动 / 抬起（整理、页签、滚动条） |
| 777 | 页签拖动 / 点击 |
| 845 | 整理模式：点击 / 框选 |
| 906 | 整理模式：拖把手排序 |

## src/InkEngine/Shapes/Engine.ShapeTools.cs（1,573 行）

| 行 | 顶层类型 |
| --- | --- |
| 16 | `public partial class InkEngine` |
| 77 | `private sealed class StepPlan` |

| 行 | 段落 |
| --- | --- |
| 18 | 图形工具：直线 / 矩形 / 椭圆 / 箭头 |
| 874 | 鼠标 / 触摸：同一个道理，读 POINTER_INFO 的合并点 |
| 903 | 读不到合并点：退回"一个消息一个点"的老路（行为与以前完全一致） |

## src/InkEngine/Shapes/ShapeLibrary.cs（829 行）

| 行 | 顶层类型 |
| --- | --- |
| 27 | `internal static class ShapeLibrary` |
| 55 | `public sealed class Category` |
| 214 | `public sealed class ImportResult` |
| 427 | `public sealed class Entry` |
| 449 | `public sealed class SaveResult` |
| 458 | `public sealed class ReplaceResult` |

| 行 | 段落 |
| --- | --- |
| 688 | 边车 / 迁移 |

## src/InkEngine/Shapes/ShapeRecognize.cs（2,925 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `internal struct ShapeGuess` |
| 107 | `internal static class ShapeRecognize` |

| 行 | 段落 |
| --- | --- |
| 109 | 门槛常量。**每个数都要能说出出处**（写死一个"看着差不多"的数， |
| 828 | 对外入口 |
| 959 | 主判据。**顺序是有讲究的**，每条都说明为什么排在这儿。 |
| 1327 | 二次函数（四种开口）—— 计划 §43.4.4（一），用户 2026-09-24 定的第一族 |
| 1373 | 正弦 / 余弦（计划-图形工具.md §43.4.4 三） |
| 2183 | 几何工具（每个都短小、可单独解释） |

## src/InkEngine/Text/HandwritingOptimize.cs（848 行）

| 行 | 顶层类型 |
| --- | --- |
| 20 | `public partial class InkEngine` |
| 22 | `private enum HandOcrMode { Handwriting, TextBox, Plot }` |
| 24 | `private sealed class HandOcrDone` |

## src/InkEngine/Text/TextBoxEditing.cs（2,677 行）

| 行 | 顶层类型 |
| --- | --- |
| 18 | `public partial class InkEngine` |
| 2525 | `internal sealed class TextBoxTextAction : EditAction` |
| 2623 | `internal sealed class TextBoxAnswersAction : EditAction` |

| 行 | 段落 |
| --- | --- |
| 574 | 触摸拖动 + 惯性 |

## src/InkEngine/Text/WindowlessTextControl.cs（1,098 行）

| 行 | 顶层类型 |
| --- | --- |
| 69 | `private struct HostObject` |
| 76 | `private struct RECTL` |
| 82 | `private struct SIZEL` |
| 88 | `private struct CHARFORMATW` |
| 103 | `private struct PARAFORMAT` |
| 119 | `private struct CREATESTRUCTW` |

| 行 | 段落 |
| --- | --- |
| 598 | window proc |
| 726 | ITextHost vtable |
| 1075 | native helpers |

## src/InkTeach/DevTools/App.Dev.AiShow.cs（1,038 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 59 | 拖动 / 改宽 / 点答案（走和真机同一条指针路径） |
| 135 | 普通文本框：同一套对象/存档/8 手柄 |

## src/InkTeach/DevTools/App.Dev.PageShow.cs（689 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `internal sealed partial class App` |

## src/InkTeach/DevTools/App.Dev.Smooth.cs（624 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `internal sealed partial class App` |

## src/InkTeach/DevTools/DwellProbe.cs（809 行）

| 行 | 顶层类型 |
| --- | --- |
| 23 | `internal static class DwellProbe` |

## src/InkTeach/DevTools/RecoProbe.cs（2,188 行）

| 行 | 顶层类型 |
| --- | --- |
| 34 | `internal static class RecoProbe` |
| 46 | `private struct Case` |

| 行 | 段落 |
| --- | --- |
| 662 | 语料：合成"手画的"笔迹 |
| 1600 | 语料工具 |
| 1659 | 多笔语料 |
| 1710 | 两笔 → 双曲线：语料与辅助量（2026-09-26 三轮实测用的） |
| 1992 | 小工具 |

## src/InkTeach/Host/App.Modes.Ink.cs（614 行）

| 行 | 顶层类型 |
| --- | --- |
| 15 | `internal sealed partial class App` |

## src/InkTeach/SelfTests/E2E/AppClassroomTests.cs（629 行）

| 行 | 顶层类型 |
| --- | --- |
| 18 | `internal sealed partial class App` |

## src/InkTeach/SelfTests/E2E/BoardAndLaser.cs（1,377 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 116 | 代价：只有"换底纹那一下"要整层重铺，之后每帧都不花钱 |
| 147 | 白板透明度（用户 2026-09-17："隐约看见下面的题目，但是书写有干净"） |
| 269 | ① 一直写：整条留着，最早那个点不许被淘汰 |
| 288 | ② 松手：先停 2 秒（一点不淡），之后才开始淡，再淡完就没 |
| 333 | ③ 多条并存 ＋ **整批共用一个计时**（用户 2026-09-27 报的那个 bug） |
| 366 | ④ 粗细滑条真的管用：量屏幕上那条红带多宽 |
| 396 | ⑤ 不是笔迹 |
| 408 | ⑥ 真按一遍鼠标：按下 → 拖 → 抬手（走引擎那条真路） |
| 435 | 出图：摆样好不好看，自检判不了（摆三笔不同粗细，给人看） |
| 540 | 默认档（2026-10-16 用户拍板）：**三支笔一律系统斜笔** |
| 559 | 以下整段按**退回档**跑（`--nosyscursor`）：藏光标＋自绘落点逐条保持原样 |
| 562 | 输入设备 × 工具 |
| 601 | 笔尾橡皮（倒持的笔）：悬停就要按橡皮显示（用户 2026-09-27 报的） |
| 640 | 截图 vs 框选/图形：光标怎么分 |
| 661 | 书写中：**手写板的光标不能消失**（用户 2026-09-27 报的手感 bug） |
| 691 | 粗细 → 落点反馈 |
| 747 | 切粗细改的是**当前工具**那一档（这条是 bug 回归） |
| 772 | 选中框：八个手柄 + 旋转 + 整体拖动 + 操作条 |
| 815 | 操作条**整块**都是"界面"（用户 2026-09-17 报的） |
| 858 | 图库面板（自绘界面块）：光标**不许取决于"来路"**（2026-09-27 修） |
| 890 | 拖拽中：用按下那一刻的语义，不重新命中测试 |
| 904 | 滚动条 / 穿透 |
| 913 | 自绘光标能不能真的建出来 |
| 920 | 斜笔（系统 IDC_PEN）：句柄能取到、热点真在笔尖 |
| 935 | 光标"设过就不重设"的缓存**必须会被作废**（用户 2026-09-27 报的 bug） |
| 971 | 恢复默认档（三支笔系统斜笔）：后面的用例按产品默认路径走 |
| 1031 | 1) 粗横线压在横向块边界上 |
| 1040 | 2) 粗竖线压在纵向块边界上 |
| 1047 | 3) 跨块的长笔画：一整条都得在 |
| 1074 | 4) 滚下去再滚回来：墨量必须一模一样，而且回程不重画 |
| 1088 | 5) 常驻块数不超过预算 |
| 1306 | Self test |

## src/InkTeach/SelfTests/E2E/Diagnostics.cs（3,295 行）

| 行 | 顶层类型 |
| --- | --- |
| 20 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 308 | 真拖一笔（和 --predicttailtest 同一套注入） |
| 337 | ③ 超时退回：等过保持窗口，并持续渲染（Tick 挂在每帧渲染里） |
| 353 | 合成笔（自检注入用） |
| 834 | 延时实测：笔尖动 → 像素亮 |
| 836 | 为什么要一个专门的模式：性能面板上的"输入到上屏"是一个 EMA，看得见趋势 |
| 903 | 时间轴口径（纯函数，先钉死再进会话） |
| 931 | ① 时间轴：开始 / 各时刻的"出完条数 + 前缀进度" |
| 1005 | ② 暂停 / 倍速 / 播完自动停 |
| 1036 | ③ 只读 ＋ 点画布 = 暂停/继续（不落墨） |
| 1052 | ④ 控制条：逐热区 |
| 1093 | ⑤ 编辑动作 / 翻页：先退出回放 |
| 1099 | ⑥ PPT 回放（当前页）＋ 与 PPT 条/菜单的冲突 |
| 1262 | 签名：往返一致要比"每一条的对象"逐项相同，不是只数条数 |
| 1356 | 保存 → 清空 → 打开：逐项一致 |
| 1369 | 打开前备份：改一下再打开，备份里必须是"打开前"那一份 |
| 1392 | 坏文件 / 版本过新：明确失败、文档一个字节不动 |
| 1410 | 空板书：保存被拒（不产生空文件） |
| 1417 | 备份轮转：最多 5 份 |
| 1421 | 历史清理（墨迹 B）：按保留期清缓存；**永不动当前板书** |
| 1473 | 放映中：两条都置灰（PPT 有自己的存取） |
| 1492 | 「保存…/打开…」的分派（2026-10-16 合并入口）：按扩展名走对路 |
| 1562 | ① 映射：纯函数先钉死 |
| 1631 | ② 上屏：对齐用户在 WPF 里画的那三条线 |
| 1702 | 下面四条是**对着用户那三条线**比的（这就是"和 Windows Ink 对齐了没有"） |
| 1746 | ②.5 压感开关（2026-10-01，批次 0.2） |
| 1809 | ③ 命中与回退：按"这一笔自己的最粗处"算 |
| 1872 | ④ 存档：v10 往返 + 真·v9 老文件 |
| 2243 | ① 画法：真机拖一个外框 |
| 2280 | ② 手柄：四轴端 + 原点、无旋转柄、不留后门 |
| 2307 | ③ 屏幕：轴真的上屏、框里是空的 |
| 2319 | 拖原点：**整组平移**（2026-11-03 用户确认；四根半轴长度一个都不变） |
| 2361 | 四个轴端：各自只改自己那一截（正负半轴可以不一样长） |
| 2425 | 紧框就是"定义点的外接 + 半笔宽"（**刻度取消之后不该再虚报那一截**） |
| 2438 | ③b 原点吸附：画布中心 / 边（2026-11-03） |
| 2516 | ④ 网格：开着比关着**屏幕上真的多了那些格线** |
| 2550 | ⑤ 数轴：水平锁 + 右端箭头 |
| 2608 | ⑤ 格距手柄（**开网格才有**）：拖动 / 夹范围 / 一步撤销 |
| 2691 | ⑥ 存档：v9 往返 + 真·v8 老文件 |
| 3113 | ⑥ 运行日志（2026-10-16 用户定） |

## src/InkTeach/SelfTests/E2E/DocMem.cs（792 行）

| 行 | 顶层类型 |
| --- | --- |
| 17 | `internal sealed partial class App` |
| 24 | `private sealed class DocMemSample` |
| 55 | `private sealed class DocMemPeakSampler : IDisposable` |

| 行 | 段落 |
| --- | --- |
| 19 | 数据结构 |
| 116 | 主流程 |
| 300 | 读数 |
| 424 | 报告 |
| 775 | 小工具 |

## src/InkTeach/SelfTests/E2E/Documents.cs（1,463 行）

| 行 | 顶层类型 |
| --- | --- |
| 25 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 230 | ⚠ 探针读的是**屏幕合成结果**。老版这里靠"开白板当不透明底"—— |
| 309 | 造一张 100×50 的"左红右蓝"旗子：好认旋转方向。 |
| 1135 | 契约（用户 2026-11-15 定）：文档模式 = 放映—— |
| 1227 | 真机数据（122MB 扫描型 PDF）：一页要 270~900ms。这条线程就是为那种页存在的。 |

## src/InkTeach/SelfTests/E2E/Erasers.cs（1,003 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 112 | 0. 精细档（2026-11-03）：两种橡皮的最前面各多一档 |
| 122 | 0. 形状与落点反馈 |
| 142 | 1. 一条横线抹一下：应该变成两段，切口停在框外 |
| 183 | 2. 撤销 / 重做：同一个对象，区间表清空 / 回来 |
| 196 | 2b. 同一笔擦两刀 / 重复擦已经擦过的地方 |
| 229 | 3. 整条在框里 / 完全没碰到 |
| 254 | 4. 图形：碰到就**熔成笔迹再切**（用户 2026-09-15 定：断开也要单独算） |
| 294 | 4b. 图形的"熔"必须是**一整组、外形逐笔不变** |
| 340 | 4c. 双曲线：两支之间**不许连出假线**，渐近线熔完还是虚线 |
| 381 | 4d. 圆柱：熔出来是 5 笔（顶圈 / 下半圈 / 两条母线 / 虚线半圈） |
| 407 | 4e. 擦在**空白处**（轮廓够不着）→ 图形原封不动，连熔都不熔 |
| 427 | 4b. **双曲线的渐近线：橡皮必须擦得到**（用户 2026-09-21 报的 bug） |
| 470 | 5. 一次拖拽扫过 5 条 = 一步撤销 |
| 498 | 6. 上屏像素 |
| 499 | 6. 擦断之后是两条独立对象（**已知取舍：自交处会叠色**） |
| 536 | 7. 上屏像素（抓屏拍不到我们这层时会跳过） |
| 574 | 8. 手测台（--eraserlab）的记录链路：写进去的必须是"能算的数" |
| 599 | 9. 擦断之后"两截各自独立"（用户 2026-09-15 定的语义） |
| 661 | 10. 图像：两种橡皮都不碰（原则：图像是"内容"，不是笔画） |
| 705 | ① 曲线（下限 0.7 / 死区到 0.8 / 斜坡 / 封顶 2.5） |
| 717 | ② 后门：--eraserfixed 关掉动态 → 系数恒 1 |
| 724 | ②b 慢速/常规速度不忽大忽小（用户两轮反馈） |
| 742 | ②c 速度窗口：逐次估法很抖，窗口算出来要稳 |
| 762 | ②d 真拖那三条的"逻辑版"（不依赖鼠标/抓屏，跑哪儿都验） |
| 780 | ②e 停住 → 缓释（用户报的"停住不回落、再轻动一下猛变小"） |
| 813 | ③ 看的框 = 擦的范围（同一份尺寸：拖动中跟速度、悬停回基准） |
| 827 | ③④⑤ 真机拖动（合成鼠标，走产品的指针路径） |
| 922 | ⑤ 整笔擦不受速度影响 |
| 950 | ⑥ 严丝合缝：框有多大，墨就擦到哪儿（系数钉成 2.5，不受手速影响） |

## src/InkTeach/SelfTests/E2E/InkMotion.cs（2,598 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 62 | ① 画法：真机**一笔拖**画一条抛物线 |
| 112 | ② 几何算式（直接摆点，不走工具） |
| 116 | 抛物线：顶点 (400,400)、**开口向上**、经过点 (700,250) → p = 300 |
| 156 | 双曲线：中心 (1200,500)，第一步"拖出渐近线框 (320,240)" |
| 228 | 正弦：起点 (300,1000)；拖出 **480 宽 × 100 高 = 一个周期 + 振幅** |
| 262 | 余弦：起点 (300,1400)（**峰顶**）；拖出 480 宽 × 200 高 = 一个周期 |
| 288 | 波浪线（2026-09-20 第十六批，用户："还有一个另外的很多周期的波浪的弦函数线"） |
| 340 | 正切（2026-09-20 第十五批，用户："可以画正切"） |
| 405 | 不变形：**同一个比例放大一倍** → 曲线逐点也放大一倍（形状一个字不变） |
| 448 | 圆柱 / 圆锥（2026-09-20 第五批：照 InkClass 的 case 6/7） |
| 486 | 圆台（2026-09-20 第十三批，用户："再加一个圆台"） |
| 520 | 球（2026-09-20 第十四批，用户："在加入球"） |
| 558 | 长方体 / 四面体（2026-09-20 第五批：照 InkClass 的 case 9/26，**两笔**） |
| 598 | ③ 手柄：四种曲线走**常规操作**（2026-09-20 第五批） |
| 651 | ④ 编辑：曲线走**通用框**（只动变换，不动几何） |
| 870 | ⑤b 四种开口：**真机拖出来**（用户 2026-09-20 问"是不是应该有四种"） |
| 937 | ⑤c. 正切：真机一笔拖出来 |
| 986 | ⑤d. 波浪线：真机一笔拖出来（拖多长画多长） |
| 1022 | ⑤e. 椭圆（带焦点）：真机一笔 ＋ 焦点 ＋ 拖 P |
| 1074 | ② 两个焦点：c² = a² − b²，且落在**长轴**上 |
| 1082 | ③ P 在椭圆上：\|PF₁\| + \|PF₂\| = 2a（椭圆的定义） |
| 1102 | ⑤ 两档：折线笔数（① 椭圆 ②③ 三角形的两条边 ④⑤ 两个焦点），且都不走辅助槽 |
| 1117 | ④ 拖 P：模型里的角变了、"和"照样 2a、一步撤销回得去 |
| 1158 | ⑥ 存档：往返 ＋ 真 v11 老文件 |
| 1307 | ⑦ 删除 / 换方向之后不留残影（用户 2026-09-20 报的） |
| 1396 | ⑧ 双曲线**两笔拖动式** |
| 1514 | 换工具 = 半成品作废：那条画了一半的必须**当场丢掉** |
| 1535 | 换工具之后重新画 = **新的一条**（不是接着上一条的大小改） |
| 1546 | 第 2 笔拖到"更竖"的位置 → **上下双曲线**（焦点在 y 轴） |
| 2087 | 湿墨延时对照（--lagtest） |
| 2367 | 手机 120Hz 录像对照（--camlag） |

## src/InkTeach/SelfTests/E2E/InkShape.cs（1,532 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 390 | 墨不能超出脏区（残影防线） |
| 405 | 兜底：90° 折角在屏幕上真的画出来了（白板 + 拐角窗口） |
| 477 | ① 单击：一个采样点 → 一个圆 |
| 488 | ② 短拖（2 个采样点）：两端都是半圆 |
| 509 | ③ 起笔连报重复坐标：方向不能翻车 |
| 525 | ④ 真的画到屏幕上再数一遍像素 |
| 684 | ① 屏幕层：三种线型上屏后的墨量 |
| 726 | ② 交互层：面板那一行真的能改、该跳过的会跳过 |
| 770 | 点那一行：**全程走真路** |
| 845 | ②b 画布上点一条线能不能选中（实线 / 虚线都试） |
| 870 | ②c 笔的线型开关：画之前选 vs 画之后改 |
| 915 | ③ 存档层：写文件读回来线型还在 |
| 992 | 棱柱自检（2026-09-20 第十一批，用户提的，见 计划-图形工具.md §32） |
| 1077 | ①②③ 数学层：四个 n 各造一个 |
| 1131 | 用户 2026-09-20 要求的三条（每一条都是一个"看着不对"的现场） |
| 1167 | ④ 棱锥 / 棱台：和棱柱**同族**，只换"顶面怎么来" |
| 1190 | 顶面那一份点 |
| 1218 | 棱的条数：棱锥少一圈顶面，所以是 2n 而不是 3n |
| 1227 | 被挡住的条数：和棱柱**同一张定值表**（判据是同一条） |
| 1233 | ⑤ 真机：第 2 笔的直 / 斜 |
| 1297 | 棱锥 / 棱台：**同一套动作、同一个吸附**，只是胶囊上的字和"顶上那点"不同 |
| 1340 | ⑥ 档位 ＋ 长方体没被碰 |
| 1431 | 棱柱出图（2026-09-20 第十一批）：`--prismshow <路径>` |

## src/InkTeach/SelfTests/E2E/Navigation.cs（1,154 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 22 | 直角掉色自检 |
| 201 | ① 页高 |
| 208 | ② 翻一屏 = 相机正好走一屏 |
| 221 | ③ 往下永远还有一屏（连翻 6 次都成） |
| 232 | ④ 回来；到顶后再上翻必须"什么都不做" |
| 242 | ⑤ 翻页前后，文档里一个坐标都不变（证明"没做分页"） |
| 288 | ⑥ 滚轮仍是细粒度（一格 72 逻辑像素，不是一格一屏） |
| 302 | ⑥.5 ←→：**没选中时也是翻页**（2026-10-05 用户定） |
| 319 | ⑦ 翻完屏幕上真的换了内容（屏幕取点） |
| 383 | ⑧ 清空之后**留在原地**（用户 2026-09-17 问："如果我的焦点在第 12 页， |
| 911 | 上一笔 A：左侧场景是"一竖"（跨过第一列分块边界 x=256，带小摆动）， |
| 937 | 基准：A 落定之后的整块区域 |
| 952 | 【关键】两缓冲比对：同一块**静止**内容，强制渲染两帧（连续两次 Present |
| 974 | 下一笔 B：默认从 A 的左上斜穿到右下；左侧场景在 A 左边再竖写一条 |
| 1078 | 滚动阶段（--left）：内容垫够两屏 → 滚 5 格，按"S 像素整体平移"逐行对； |

## src/InkTeach/SelfTests/E2E/PanelAndKeys.cs（6,598 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 61 | 投影曲线：**没有"袋状"硬边**（用户 2026-10-01 报的"菜单栏周围半透明黑影袋子"） |
| 93 | ⓪ 贴边隐藏 + **刚启动**：不许一上来就收（用户 2026-09-17） |
| 136 | 露头状态下"写一笔"：面板**不许自己蹦回来**（用户 2026-09-18 报的） |
| 160 | 反过来那一半也要钉住：**面板开着的时候写字，不许它自己收** |
| 181 | 贴边隐藏**只认底边**（用户 2026-09-30："只要拖动到底边或者离底边很近才贴边隐"） |
| 317 | ① 启动默认态：**展开 ＋ 贴在工作区底边、水平居中**（用户 2026-09-27 定） |
| 352 | ①b 收起态：球真的在屏幕上，而且是个 48 的方（圆） |
| 359 | 收起球里那两个比例：**笔图标不许碰到色圈** |
| 394 | ② 点球展开：动画期间必须连续出帧 |
| 418 | ③ 展开后的尺寸：算出来的宽，不是一个拍脑袋的数 |
| 446 | ④ 点"笔"那一格：引擎状态真的变了（走的是命令通道） |
| 453 | ⑤ 输入拦截：面板内不落墨，面板外照常落墨 |
| 468 | ⑥ 上带：色片 / 滑条 / 分段（都走命令通道） |
| 548 | ⑥.0 自定义色槽（2026-10-23）：白色格换成可持久化的自定义色 |
| 703 | ⑥.1 色带条上的**虚实线切换**（用户 2026-09-19 第 2 件） |
| 797 | ⑥.2 滑条**按工具路由**：橡皮终于能调大小了 |
| 893 | ⑥.3 穿透与工具**互斥** |
| 913 | ⑥.3c 穿透：色带只收成那条线，不许张开（2026-10-02 用户口径） |
| 997 | ⑥.3d 写字期间：色带**冻结**（2026-10-09 用户："写到附近该保持不动"） |
| 1033 | ⑥.3e 硬规则（2026-10-09 二轮）：收起时必须是色带；抬手后要"离开一次"才许变卡片 |
| 1090 | ⑥.3f 方案 A（2026-10-09）：点开快路——明确点击立即响应，不等悬停/余温 |
| 1151 | ⑥.3g "两张纸"（2026-10-09）：文档开着时白板格 = 开板/关板，板盖在文档上面 |
| 1213 | ⑥.3b 主条那几格"选中显示选中什么"（2026-09-26） |
| 1315 | ⑥.4 截图那一格真的接上了（用户 2026-09-17："截图功能还没有接进来"） |
| 1430 | ⑥.5 主条垃圾桶（清空当前页）与选择条的全选 |
| 1527 | ⑥.6 撤销/重做的**灰度**（用户 2026-09-17："撤销重做灰度"） |
| 1559 | ⑥.8 白板色带：翻页器 ＋ 板色 ＋ 底纹 ＋ 间距（2026-09-27 重排） |
| 1664 | ⑥.9 极简档：白板色带自动退到**紧凑布局** |
| 1736 | ⑥.10 白板格：**点三下是一个来回**（用户 2026-09-27 第三次定） |
| 1803 | 设置条最右端那个 ✕ 仍然在（一眼看得见的关板键） |
| 1825 | ⑥.11 笔 / 荧光笔：**已经是它了，再点一下换下一个颜色**（用户 2026-09-27 定） |
| 1884 | 荧光笔：独立的那张亮色表 |
| 1921 | ⑥.12 选择格：**已经是它了，再点一下换下一档** ＋ **双击 = 全选**（用户 2026-09-27 定） |
| 1993 | 双击 = 全选 |
| 2026 | ⑥.7 白板和穿透**互斥**（用户 2026-09-17 问："鼠标和白板是不是也应该互斥"） |
| 2045 | ⑥.7b 退出穿透时的板态恢复（2026-09-30 用户拍板） |
| 2079 | ⑥.7c 穿透模式下工具键一律不响应（2026-09-30 用户拍板） |
| 2162 | ⑥.1 白板那一格的滑条 = **板面不透明度**（用户 2026-09-17 要的） |
| 2190 | ⑥.5 白板翻页：上带上的 [‹] / [›] |
| 2236 | ⑥.6 放映时白板那一格**仍然只管白板**（用户 2026-09-26 更正： |
| 2242 | ⑥.5 进放映那一刻：面板**自动展开 ＋ 回到默认位置**（用户 2026-09-27 定） |
| 2311 | ⑦ 「更多」面板：开合、页签、深色主题、贴边隐藏 |
| 2343 | 启动器：格子/底栏逐格点得通（"加一格漏一处"用这条兜住） |
| 2386 | 「最近文档」：0 / 1 / 3 / 5+ 四种状态、命中、跳过缺失、点一行打开 |
| 2746 | ⑦.1 模态的收口：点面板外＝关、这一下**不落墨**；关掉后画布立刻能画 |
| 2797 | ⑦.15 球贴左/右边：那里**藏不动**，所以必须还是球（不是纯色把手） |
| 2852 | ⑦.5 界面档位与钉住 |
| 3099 | ⑥ 收起来，然后空闲必须 0 帧 |
| 3119 | ⑥.6 悬停提示（Tooltip；2026-10-02） |
| 3158 | 收窄后的清单不变量：该有的有、该没有的没有（2026-10-02 第二轮） |
| 3220 | 触摸长按（2026-10-02 第二轮）：主流四步手势 |
| 3285 | ⑦ 拖动**不吸附**（用户 2026-09-30："拖到任务栏下面自动靠底边这种不用了"） |
| 3310 | ⑦.2 锚点：**球不动、带子往右长**；顶到右边就整条夹回屏幕内 |
| 3386 | ⑧ 「更多」面板里的「重启软件」：先暂存板书，再拉起新进程（这里只记一笔，不真拉） |
| 3390 | ⑨ 偏好落盘：改过的写进 settings.json，重开界面读得回来 |
| 3692 | AI 出题：入口开发中置灰；面板本身仍从自检入口直接打开 |
| 3779 | 文本工具：橡皮前入口、点画布输入、自动换行、空框清理、更多兜底 |
| 4429 | A. 每一段都点得到 |
| 4506 | 指针停在**任何一行**上，带子都得留得住（不许收） |
| 4587 | 名单一致性：三份"图形"判据必须一一对应 |
| 4662 | A2. 抛物线那格：**再点一次 = 换一档（上下 / 左右）** |
| 4707 | A3. 直线那一段：**再点一次 = 换一档线型** |
| 4807 | A4. 双曲线 / 椭圆（带焦点）那两格：**再点一次换一档** |
| 4959 | 椭圆（带焦点）：两档 = 有 / 无焦点三角形 |
| 5027 | A5. 坐标系那格：**再点一次 = 换一档（带网格 / 不带网格）** |
| 5120 | B. 图形**一个热键都没有**（用户 2026-09-19 定） |
| 5155 | C. **每一个有入口的图形**都真拖一笔，画出来就是那一种 |
| 5209 | D. 主条那一格的图标跟着种类变 |
| 5227 | D2. 没选中图形工具时，这一格画的是**通用图标** |
| 5364 | ① 悬停要能到界面 |
| 5371 | ② 悬停坐标必须是"逻辑屏幕"，不能是画布坐标 |
| 5388 | ③ 不穿透：面板内点击归界面，且不许画出一笔 |
| 5402 | ④ 不穿透：界面"看见了但不吃"的那一半也不该落墨 |
| 5409 | ⑤ 不穿透：面板外照常画线 |
| 5418 | ⑥ 穿透：面板内点击必须由界面收下（这就是这次要修的那条） |
| 5438 | ⑥.5 重新武装（2026-10-09 收编老红） |
| 5447 | ⑦ 穿透：面板是界面的地盘，"看见但不吃"也不许漏给下层 |
| 5458 | ⑧ 穿透：面板外交下层 |
| 5467 | ⑨ 界面驱动的动画必须拿到连续帧，停下之后必须回到零帧 |
| 5498 | ⑩ 命令通道：新加的三条命令走一遍，状态读得回来 |
| 5525 | ⑪ 界面看到的"屏幕"必须和 IUiHost.Screen 是同一个 |
| 5536 | ⑫ 界面抛异常不许留死局：重建 → 重启 → 兜底，而且板书不许丢 |
| 5600 | 诊断（不计红绿）：穿透的两种实现方式各自牺牲了什么 |
| 5680 | 1. 默认表 |
| 5705 | 1.5 2026-10-05 新增键位：白板开关 / 选中缩放 / 重做别名 |
| 5724 | 截图键 2026-10-16 用户定：`Ctrl+S`（全世界的"保存"）→ `Ctrl+Shift+A` |
| 5777 | 2. 按键解析 |
| 5790 | 3. 冲突检测 |
| 5814 | 4. 落盘 / 读回 / 坏文件 |
| 5855 | 5. 真机注册结果 |
| 5860 | 6. 快捷键总表：文档不许落后于代码 |
| 5872 | 6. 动作路由：id ↔ 动作 ↔ 键 一一对应 |
| 5889 | 7. 批注内真按一次键（走窗口消息 → 键盘模式 → 动作） |
| 5925 | 8. 白板开关 Ctrl+B：和点白板格同一条命令（含"开板顺手退穿透"） |
| 6047 | ① 基线：还没截图，键盘在我们这儿 |
| 6052 | ② 隐藏窗口截图：进屋那一瞬藏过窗口（漏要前台的正是这一步） |
| 6064 | ③ 隐藏窗口截图：拖框 + 真按 Enter 确认 |
| 6080 | ④ 截图（连批注）：同一套 |
| 6091 | ⑤ 焦点被抢走 → 回到批注态要得回来 |
| 6111 | ⑥ 真按 Ctrl+=：OEM 等号键 + 选中缩放（2026-10-05 新增） |
| 6273 | ① 打开：盘心 = 按下那一刻的指针 |
| 6280 | ② 死区（没移动）松手 = 取消 |
| 6290 | ③ 划向正东 =「下一色」：红 → 蓝 |
| 6303 | ④ 在别的工具上点北（红笔）= 切回笔 + 红 |
| 6315 | ⑤ 北格固定红：已经是红笔时再点，还是红（不循环） |
| 6324 | ⑥ 其余扇区（v3：东北=荧光、南=橡皮、西=上一色、东南=框选、西北=撤销） |
| 6352 | ⑥.1 西北 = 撤销一步 |
| 6373 | ⑥.2 西南 = 清屏：停不满 0.8s 不执行，停满才清当前页 |
| 6395 | ⑥.5 v3 排序与扇面几何（2026-10-25 定稿） |
| 6411 | ⑦ 跨扇区滞回：出界 9° 以内不跳扇区 |
| 6425 | ⑧ 穿透里照样能呼出：这是"把笔从下层抢回来"的入口（2026-10-04 用户定） |
| 6465 | ⑨ 写字中（笔尖在屏上）不响应 |
| 6472 | ⑩ 松键轮询：放映那条路没有 KeyUp，靠每帧 GetAsyncKeyState |
| 6480 | ⑪ 呼出盘现在是常驻全局键（Ctrl+Alt+Shift+Q），放映临时表里不重复挂 |
| 6493 | ⑫ 真键盘 + 真鼠标：全局热键那条路（RegisterHotKey → WM_HOTKEY → 打开） |
| 6529 | ⑬ 真键盘：先松修饰键（盘还在）→ 松开 Alt/Shift 后按 Esc 取消 |
| 6554 | ⑭ 真键盘 + 穿透：开着穿透按 Ctrl+Alt+Shift+Q 也能调出笔来 |

## src/InkTeach/SelfTests/E2E/PptStorage.cs（1,783 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 454 | ① 进放映 |
| 461 | ①.2 危险顺序：**面板先开着，PptMode 才打开**——第一次定位不许被顶走 |
| 496 | ①.5 放映临时全局键 × 穿透：穿透期间让给下层（用户 2026-09-30 定） |
| 515 | ①.6 悬停提示：页码格 / ◀ / ☰ / 菜单（2026-10-02，2026-11-15 加 ☰） |
| 568 | ② 每页一套 + 页内滚动 |
| 593 | ③b 橡皮「清空墨迹」= 只清**当前页**（2026-10-25 用户定） |
| 616 | ④ 清除全部墨迹 = 清掉**所有页** + 一步撤销（2026-10-14 用户定的统一口径） |
| 629 | 撤销栈仍按页（参考 InkClass 的 TimeMachineHistories[页]） |
| 650 | ⑥ 退出放映：写盘 + 回桌面 |
| 665 | ⑨ 没有 PPT 时：一个字都不发生 |
| 677 | ⑦ 再进放映：从盘里读回（模拟关掉软件再打开） |
| 688 | ⑧ 界面按钮那条路：命令走 PPT、页跟着变 |
| 698 | ⑧.5 PPT 控件条（PptBar）：2026-09-26 定形态，2026-11-15 拆出 ☰ |
| 823 | 页号面板：**每一个格子的悬停都要准**（用户 2026-09-27 报的） |
| 993 | **拖起来了，松手就不许弹菜单**（用户 2026-09-27 报过"拖页码时弹菜单"） |
| 1008 | PPT 浮层（条 / 菜单 / 页号面板）上的光标：一律箭头 |
| 1029 | 菜单：**点最右 ☰ 开**（2026-11-15）；四项逐项点一遍 |
| 1065 | 穿透开着时，条仍然归我们 |
| 1207 | 兜底：读不到 SlideID 时用负页码当键（参考 Ultra 的 GetStrokeCacheKey） |
| 1214 | 白板独立纸 × PPT：背景翻页 / 结束放映都不打断白板 |
| 1457 | 老文件兼容：v2（每条笔画多一个"笔锋预设"字节）必须还能打开 |
| 1580 | 0. 没选中时不该动剪贴板 |
| 1586 | 1. 造三条要复制的东西 |
| 1613 | 2. 复制 → 读回对象 |
| 1645 | 3. 同一份剪贴板里那张图（给外部程序的兜底） |
| 1668 | 4. 智能粘贴：有对象 → 粘成对象 |
| 1710 | 4.5 翻到别处再粘：落在当前屏，不把人拽回原件（2026-11-03） |
| 1728 | 5. 只有图（没有对象格式）→ 退回"当图粘" |

## src/InkTeach/SelfTests/E2E/Selection.cs（2,848 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 656 | 方案 A 的另一半：旋转中**留**旋转柄与度数标签 |
| 708 | 多选拖动：框是**轴对齐并集**，拖动中同样要跟着内容走 |
| 736 | 缩放手势也走同一条"摘出去"的路，但**装饰不收** |
| 760 | 单选一个**转过角度**的对象：框也必须是轴对齐的正矩形 |
| 823 | 操作条第二轮（2026-09-16）：九格 / 收起 / 提示条 / 颜色面板 / 锁定 / 层级 |
| 1098 | 选中逻辑三连测（用户点名要的）：一条 / 多条 / 里面混着图片 |
| 1184 | ⑩ 键盘微调 / 缩放（2026-10-05）：连续动作合并成一步撤销 |
| 1321 | ② 现在的拖动路径（方案 B：拖动预览） |
| 1427 | 0. 切换方式 |
| 1436 | 1. 80% 边界（79% 不选 / 81% 选） |
| 1471 | 2. 贴边＝无限延伸 |
| 1492 | 3. 图形按**轮廓**判，不按两个端点 |
| 1519 | 4. 图像按四个角判 |
| 1531 | 5. 被擦掉的那一段不算墨 |
| 1546 | 6. Shift 加选 / Alt 减选 |
| 1563 | 7. 引擎那条路（按下 → 拖 → 松手） |
| 1578 | 8. 拽着不放时屏幕上真有那根线（差分判据，抓屏拍不到就跳过） |
| 1636 | 9. 切回矩形：同一个手势变成"碰到就选" |
| 1739 | 旋转 90°：宽高应该互换 |
| 1757 | 操作条命中 |
| 1776 | 导出：**点一下直接走导出，不再开自己的格式面板** |
| 1877 | 小对象：最小操作框（2026-10-05 用户："太小还用这么大的点不合适"） |
| 1919 | 四角拖动：锚点不动，被拖的角跟手 |
| 1937 | 边中点：只动一个轴（这就是"左右拉伸 / 上下拉伸"） |
| 1944 | 拖过头：停在**最小尺寸**（12 逻辑像素短边），不翻转、不缩成小点 |
| 1955 | Shift 等比 |
| 1960 | 旋转：绕选区中心转，长度不变 |
| 1969 | 旋转吸附 |
| 1978 | 镜像：只能从矩阵入口来 |
| 2015 | A. 数学层 |
| 2099 | B. 真机层 |
| 2200 | C. 方向 + 不设上限（用户 2026-09-15 定的） |
| 2622 | 0. 进入取景（8.3.1，照微信）：点格子只出模式条；点模式段 = 进屋 |
| 2650 | 0b 右上角「✕ 取消」：触摸屏没有 Esc / 右键，得有颗点得到的出口 |
| 2734 | ①b Esc = 退出取景、**还给进来之前的工具**（不落图，8.3.1） |
| 2746 | ①c 调整：拖右下角手柄 → 确认出来的图就是框最后的大小 |
| 2762 | ①d 双击框内 = 完成（微信的手感，8.3.1） |
| 2779 | ①e 方向键微调框（8.3.1；投影上鼠标很难微调一格） |
| 2790 | ② 第二种模式：**截图**（连批注一起拍） |

## src/InkTeach/SelfTests/E2E/ShapeLibrary.cs（4,475 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 255 | A. 画一个矩形，把它存进图库 |
| 310 | B. 从主条「收藏」格打开面板（更多里不再放重复入口） |
| 327 | C. 面板里的缩略图真的画出来了吗（数屏幕像素） |
| 346 | D. 使用态：点卡=选中，点画布=放置 |
| 509 | E. 整理模式：勾选、删除、回收站 |
| 566 | F. 分类管理：改名 / 删除 / 新建 |
| 653 | G. 内容滚动（36 条，不再分页） |
| 726 | H. 收藏只留在主条；取消钉住后更多里不再有重复入口 |
| 750 | I. 页签 / 拖动 / 缩放 / 2×2 |
| 982 | J. 整理：整组移动 / 拖把手排序 / 全屏隐藏 |
| 1110 | K. 框选 |
| 1133 | L. 导入透明图片 / 贴纸（保留 alpha，往返不二次预乘） |
| 1230 | A. 四种工具各拖一次 |
| 1284 | A2. 画完自动选中 ＋ 那个框只"收起来"，别的都不变 |
| 1419 | B. 拖动太短不产生对象 |
| 1446 | C. 吸附（数学层） |
| 1499 | 展开角（可以超过 180°、也可以是负数）的吸附：特殊角按 180° 周期 |
| 1549 | D. 拖端点（真机层） |
| 1640 | D2. 只点一下端点手柄（不移动）：线不许从屏幕上消失 |
| 1672 | D3. 拖端点中的框：与静止态**同一口径**（每边不再多 2 像素） |
| 1745 | E. 旋转过的直线：端点仍落在指针位置 |
| 1791 | F. 画线过程中也显示 α（真机层） |
| 1842 | G. 单选直线拖旋转柄：读数是 α（不是 Δ） |
| 1925 | 「可以无限转下去」那一组（用户 2026-09-18 澄清） |
| 2086 | G2. 姿态角（图形）：提取 / 吸附 0-90 / 矩阵与标签同角 |
| 2169 | 真机①：一个**转过 8° 的矩形**拖旋转柄 → 吸到 0°（水平） |
| 2209 | 真机②：一个**转过 52° 的椭圆**拖旋转柄 → 吸到 90°（竖的） |
| 2242 | 镜像过的图形：读数仍然以**眼睛看到的**为准（和矩阵同一个角） |
| 2271 | 直线仍然读 α（不是姿态角）；圆连姿态角这一档都进不去 |
| 2322 | 图像 / 笔迹 / 多选：仍然读转过的 Δ，而且**通用旋转的 ±3° 口径一个字没改** |
| 2392 | H. 直线/箭头的选中框：紧框（端点口径） |
| 2428 | 一条 **45° 斜线**，四个姿态：0°（没转过）/ 30° / 60° / 90° |
| 2487 | 上一轮那个现场那组数（同一条线、同一个角） |
| 2517 | 箭头：框必须包住头部的两个翅膀尖（它们不在两端之间） |
| 2546 | 不变的：矩形 / 图像（各转 30°） |
| 2577 | I. 圆：画法 / 手柄 / 紧框 / 命中 |
| 2585 | 画一个圆（真机：按下拖出半径） |
| 2618 | 造一个已知的圆，验手柄 / 紧框 / 命中 |
| 2667 | 拖圆内（含圆心位置）= 平移；圆心手柄已删，这里走的是框内整体拖动 |
| 2688 | 拖圆周点 = 只改半径（圆心逐位不变） |
| 2718 | J. 椭圆：画法变更 / 四个轴端点 / 紧框公式 |
| 2727 | 画一个椭圆（真机：按下=中心，横纵一起定半轴） |
| 2759 | 造一个已知的椭圆：手柄 / 只改一条半轴 / 最小值 / 紧框 |
| 2837 | 旋转 30° 后的紧框 = 参数化外接公式 |
| 2881 | K. 三角形：画法 / 三个顶点手柄 / 紧框 / 命中 |
| 2890 | 画一个三角形（真机：按下外框左上角 → 拖到右下角） |
| 2927 | 造一个已知的三角形：手柄 / 紧框 / 命中 / 拖顶点 |
| 2988 | 拖一个顶点：只动它、走快路、一步撤销 |
| 3028 | L. 平行四边形：三个顶点 / 第四个现推 / 紧框 / 命中 |
| 3037 | 画一个平行四边形（真机） |
| 3084 | 造一个已知的平行四边形：手柄 / 第四个角没有手柄 / 紧框 / 命中 |
| 3140 | 拖一个顶点：只动它，第四个角**仍逐位等于公式** |
| 3179 | L2. 读数：三角形三个内角 / 平行四边形两个夹角（规格 9.7） |
| 3203 | 数学层：一个**已知角度**的 30/60/90 三角形 |
| 3237 | 数学层：平行四边形两个夹角（互补） |
| 3254 | 真机：三角形**也改成"只在拖顶点时显示"**（用户 2026-09-20 定） |
| 3357 | 真机：平行四边形只**拖顶点时**显示两个夹角 |
| 3401 | M. 一族"特殊形状"吸附（规格 9.6） |
| 3422 | 三角形 · 等腰 |
| 3450 | 三角形 · 等边 |
| 3482 | 三角形 · 直角（被拖的那个角） |
| 3513 | 三角形 · 直角（**固定的**那个角） |
| 3526 | 椭圆 · 正圆 |
| 3543 | 平行四边形 · 菱形 |
| 3570 | 平行四边形 · 矩形（邻边垂直） |
| 3597 | 矩形 · 正方形（拖四角：改的是矩阵，不是点） |
| 3680 | 真机层：吸住时接在那条路上，而且提交的几何**精确**满足约束 |
| 3681 | 三角形拖顶点（真机）：等腰 |
| 3718 | 椭圆拖轴端点（真机）：正圆 |
| 3750 | 矩形拖四角（真机）：正方形 |
| 3780 | 整体移动 / 旋转**不吸**（规格 9.6 第一条硬要求） |
| 3880 | N. 存档：圆 / 椭圆 / 三角形 / 平行四边形读回来逐位一致 |
| 3998 | O. 拉伸中的形体完整性（缺块 / 残影） |
| 4158 | 顺手检查：三角形 / 平行四边形拖顶点、矩形拖角，有没有同类问题 |
| 4362 | E. 圆的圆心点：是墨迹，始终存在；圆只留圆周点手柄；椭圆没有圆心点 |

## src/InkTeach/SelfTests/E2E/ShapePanel.cs（845 行）

| 行 | 顶层类型 |
| --- | --- |
| 15 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 736 | 图形页：拖窄 + 拖矮（故意低于 5 行） |
| 769 | 收藏页：高度是另一份；缩到 1 行后照样记得 |
| 798 | 两页高度各一份：切回图形页仍是刚才的矮档 |
| 817 | 白板滚动后切页签：面板必须还钉在屏幕同一位置 |

## src/InkTeach/SelfTests/E2E/Touch.cs（1,271 行）

| 行 | 顶层类型 |
| --- | --- |
| 19 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 87 | ① 不报面积的屏：单指一律当"写"（不误擦） |
| 96 | ② 双指纵滑 = 漫游：相机动、墨迹坐标一个没变 |
| 128 | ③ 双指横滑 = 翻页（一次手势只翻一页） |
| 150 | ④ 写字中途晚到的掌根（>300ms）= 忽略 |
| 166 | ④b 第一指已经画出去，第二指仍能升级（真机日志的主场景） |
| 188 | ④c 第二指 250ms 晚到、第一笔还短 → 仍升级（旧规则会 Ignore） |
| 207 | ⑤ 快速两指 = 手势：刚起头那一小笔被撤掉 |
| 223 | ⑥ 角色判定表（**面积不参与**）：1 指写 / 2 指手势 / ≥3 指擦 |
| 243 | ⑦ 三指一起落下 = 擦（不依赖面积；鼠标那把橡皮：整笔擦；落点 = 三指中心） |
| 265 | ⑧ 长按 0.5 秒 = 进入选择（长按在对象上 = 点选它） |
| 306 | ⑨ 选中后：双指 = **只缩放**（不含平移、不含旋转；旋转走拖手柄，见 ⑲） |
| 340 | ⑩ 单指漫游已取消：单指仍然是写字，不再拖动画布 |
| 363 | ⑪ 收场干净：全抬起后状态复位，接着单指还能写 |
| 371 | ⑫ 总开关关掉：只剩单指书写（内核开关还在；设置里那一行 2026-10-09 晚已撤， |
| 410 | ⑬ 两指长按 = 呼出盘；划向右松手 = 红笔 |
| 435 | ⑭ 两指轻点 = 呼出盘（留在盘上）；点东南扇区 = 蓝笔并关闭 |
| 459 | ⑮ 大面积触点：2026-10-24 起**只统计、不再触发橡皮** |
| 487 | ⑯ 触摸 + 橡皮工具：落点反馈 = 鼠标同款（按着出现、松手消失） |
| 516 | ⑰ 选中后：双指放在**别处**也能变换（真机反馈的场景） |
| 601 | ⑱ 长按选中后：再按住选中的东西拖 = 移动；按框外拖 = 照常写字 |
| 651 | ⑲ 选中后：拖**旋转手柄** = 旋转（触摸走鼠标同一条路，带度数胶囊） |
| 695 | ⑳ 三指擦 + 面积橡皮 = 动态大小（快扫变大、松手回基准） |
| 757 | 压感采集与笔迹预测的自检 |
| 910 | 预测尾（鼠标 / 触摸那条路） |
| 948 | ① / ② 几何与模型：不依赖任何输入设备 |
| 980 | ③ 压感变宽那条渲染路（ID2D1Ink）也要带上尾 |
| 1023 | ④ / ⑤ 真鼠标拖一笔 |
| 1115 | B 组：关掉预测，同一套动作再来一次 |
| 1197 | ① 单指按下，看它是不是以 PT_TOUCH 进来 |
| 1206 | ①b 触点诊断（8.3.3）：数得到触点数 |
| 1212 | ② 走一段（预测尾已随老预测系统删除，这里只验点在往里进） |
| 1226 | ④ 第二根手指按下：正在写的那一笔不许被换掉 |
| 1251 | 收尾：两根一起抬，交进文档 |

## src/InkTeach/SelfTests/E2E/Zoom.cs（797 行）

| 行 | 顶层类型 |
| --- | --- |
| 15 | `internal sealed partial class App` |

## src/InkTeach/SelfTests/E2E/ZoomShots.cs（776 行）

| 行 | 顶层类型 |
| --- | --- |
| 14 | `internal sealed partial class App` |

| 行 | 段落 |
| --- | --- |
| 310 | J 组：高清补丁（PDF >100% 的可见区设备分辨率小图） |
| 556 | J2 组：驱动窗口 / 换倍率不闪 / 渲到一半关文档（2026-11-16 评审 P0/P1） |

## src/InkUi/Ai/FullUi.ApiSettings.cs（757 行）

| 行 | 顶层类型 |
| --- | --- |
| 13 | `public sealed partial class FullUi` |

| 行 | 段落 |
| --- | --- |
| 15 | AI 服务页（A2.1） |
| 729 | A2.1 自检钩子 |

## src/InkUi/Design/IconAtlas.cs（1,940 行）

| 行 | 顶层类型 |
| --- | --- |
| 24 | `internal static class IconAtlas` |

| 行 | 段落 |
| --- | --- |
| 498 | 四种曲线的图标（2026-09-20 第五批，全部自绘） |
| 1124 | 2026-09-26 定稿（用户挑的 A′）：**一支大笔 ＋ 两道明显断开的虚线 ＋ 一个点** |
| 1140 | 2026-09-27：照 ClassIn 的"**笔头 ＋ 一个发光提示**" |

## src/InkUi/Panels/FullUi.MorePanel.cs（2,381 行）

| 行 | 顶层类型 |
| --- | --- |
| 26 | `public sealed partial class FullUi` |

| 行 | 段落 |
| --- | --- |
| 80 | 内容是**两段式**的：形状先长，内容再交叉换 |
| 169 | 颜色（深色主题只是一整套换过来，形状一个都不动） |
| 1342 | 悬停提示（Tooltip；2026-10-02） |
| 1495 | 「更多」面板（编号 = 1000 + 命中码；2026-10-02 第二批） |
| 1732 | 自检钩子（开发期用；产品代码不碰） |
| 1934 | A0 动作表/稳定命中的自检钩子（2026-11-08） |
| 1969 | A1 PageHost 自检钩子（2026-11-08） |
| 2056 | 设置页连续滚动的自检钩子（2026-11-07） |
| 2218 | 自检用：悬停提示 |

## src/InkUi/Panels/FullUi.Pages.cs（704 行）

| 行 | 顶层类型 |
| --- | --- |
| 12 | `public sealed partial class FullUi` |
| 297 | `private abstract class MorePageBase : IPage` |
| 343 | `private sealed class MoreHomePage : MorePageBase` |
| 412 | `private sealed class SettingsPage : MorePageBase` |
| 526 | `private sealed class HelpPage : MorePageBase` |
| 584 | `private sealed class LogPage : MorePageBase` |
| 694 | `private sealed class ApiPage : MorePageBase` |

| 行 | 段落 |
| --- | --- |
| 47 | 「最近文档」（2026-11-15） |
| 265 | 「最近文档」自检 / 出图钩子 |

## src/InkUi/Panels/FullUi.Settings.cs（3,046 行）

| 行 | 顶层类型 |
| --- | --- |
| 26 | `public sealed partial class FullUi` |
| 40 | `private enum BandAction { None = 0, SelectAll, Copy, Paste, CloseBoard, PasteImage, TextDone, AiMenu }` |
| 2256 | `private enum SettingsCategory { Toolbar = 0, General = 1, Writing = 2, Ink = 3, Ai = 4, Backup = 5, About = 6 }` |
| 2331 | `private enum MorePage { Home = 0, Settings = 1, Help = 2, Log = 3, Api = 4 }` |

| 行 | 段落 |
| --- | --- |
| 28 | 上带右端的"动作"按钮（照假面板） |
| 305 | 粗细预览：数字 ＋ **真实大小** |
| 505 | 写字与色带的边界（2026-10-09 二轮定稿，用户原话： |
| 855 | 色位取色板（左边 HSV / 吸管，右边常用色 + 最近；荧光笔多一段浓度） |
| 1236 | 卡片外：先看是不是色片，再关 |
| 1445 | 设置页键盘焦点（A0，2026-11-08） |
| 1655 | 右栏：常用色 + 最近（荧光笔再多一段 5 档浓度） |
| 1890 | 档位与钉住（「更多」面板「工具条」那一组在用） |
| 1957 | UI 动作表：设置行（A0） |
| 2277 | UI 动作表（A0，2026-11-08） |
| 2319 | 语言下拉（2026-11-13） |
| 2447 | A1：PageHost 适配（现有绘制/命中仍读 _morePage） |
| 2524 | 日志与反馈页 |
| 2874 | 设置页双指纵滑（UI 自己记两根手指；见字段注释） |

## src/InkUi/Shell/FullUi.cs（1,883 行）

| 行 | 顶层类型 |
| --- | --- |
| 25 | `public sealed partial class FullUi : IOverlayUi` |
| 42 | `private enum Cell` |
| 135 | `private enum Profile { Mini = 0, Custom = 1, Full = 2 }` |
| 219 | `private enum BandDensity { Minimal = 0, Compact = 1, Regular = 2, Full = 3 }` |
| 419 | `private enum Row { DarkTheme, AutoHide, RailPin, UiSize, Language, DwellShape, Pressure, Predict, TouchMode, ClearOnArrow, Privacy, RestoreInk, PptAutoSave, HistoryDays, BackupAll, RestoreAll, AutoStart, AiApi }` |
| 1330 | `private enum BoardSegKind { PageUp, PageLabel, PageDown, Color, Pattern, Step }` |

| 行 | 段落 |
| --- | --- |
| 27 | 性能归因开关（开发期） |
| 36 | 按钮表 |
| 125 | 界面档位 |
| 279 | 悬停提示（Tooltip；2026-10-02） |
| 300 | 工具带自定义色取色板（2026-10-23） |
| 367 | 笔的虚实线切换（用户 2026-09-19 第 2 件） |
| 494 | 静态底缓存：**想清楚了再做，现在没做** |
| 630 | 界面自己的偏好（存哪儿、怎么存由引擎负责，这里只管键的含义） |
| 1121 | 上带（当前工具的设置条） |
| 1309 | 白板那一格的色带（2026-09-27 重排） |
| 1480 | 字号菜单（点按钮弹出） |

## src/InkUi/Shell/FullUi.Interaction.cs（2,308 行）

| 行 | 顶层类型 |
| --- | --- |
| 26 | `public sealed partial class FullUi` |

| 行 | 段落 |
| --- | --- |
| 28 | 启动器几何 |
| 129 | 设置子页几何 |
| 197 | 语言下拉（几何 / 命中 / 开合） |
| 368 | 稳定命中目标（A0，2026-11-08） |
| 526 | 开/关与换页 |
| 739 | 启动器绘制 |
| 877 | 设置子页绘制 |
| 1135 | 贴边隐藏 |
| 1448 | 触摸/笔长按（2026-10-02 第二轮） |
| 1901 | 触摸/笔长按的收尾（2026-10-02 第二轮） |
| 2230 | 编辑文本框：把底部文字色带张开（选中文字后点色片改色） |
| 2249 | 收藏弹窗开合：**不切换、不展开色带** |
| 2260 | 穿透开的那一刻：上带收回那条 10 像素色线（2026-10-02 用户口径） |
| 2284 | 进放映的那一刻：**展开 + 回到默认位置**（用户 2026-09-27 定） |

---

共 85 个文件。重跑：`powershell -ExecutionPolicy Bypass -File tools/gen-section-map.ps1`
