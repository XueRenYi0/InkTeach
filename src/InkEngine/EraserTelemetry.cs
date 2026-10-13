using System.Globalization;
using System.Text;

namespace InkEngine;

/// <summary>
/// 橡皮的实测采集（"橡皮手测台" --eraserlab 专用）。
///
/// 四条设计约束，都是踩出来的：
///
///   1. **平时是 null**：所有上报点写成 `EraserTelemetry?.Xxx(...)`，
///      正常使用不产生任何开销，也不需要在引擎里到处写 `if (logging)`。
///   2. **每条拖拽结束立刻追加一行 CSV 并落盘**：用户测到一半强杀进程、
///      或者程序崩了，前面测的那些也还在。汇总另外写一份（退出时才写）。
///   3. **只记能量化的东西**：时间、条数、距离、速度、耗时。
///      主观感受（"这个手感好"）不能伪造，也不该由程序猜。
///   4. 事件流水里专门记**"擦完多久按了撤销"**——"我擦错了"最直接的信号就是它，
///      比让用户事后回忆哪一擦不对劲可靠得多。
/// </summary>
internal sealed class EraserTelemetry
{
    private readonly string _csvPath;
    private readonly string _summaryPath;
    private readonly List<string> _rows = new();          // 每条拖拽一行（汇总要用）
    private readonly List<string> _events = new();
    private readonly List<string> _beats = new();

    // ---- 当前这条拖拽 ----
    private bool _inDrag;
    private Tool _tool;
    private double _startMs, _lastStepMs;
    private float _lastX, _lastY;
    private int _samples;
    private int _moves;                    // 收到的指针移动**消息**条数（≠擦除步数）
    private double _moveGapMax;            // 相邻两条移动消息的最大间隔（"跟不跟手"）
    private double _lastMoveMs;
    private double _lengthPx, _speedMax;
    private int _touched, _strokesAtStart, _intervalsAtStart;
    private double _computeSum, _computeMax;
    private int _frames;
    private double _patchSum, _patchMax, _frameSum;
    private double _itpSum, _itpMax;        // 输入 → 上屏（帧内）

    // ---- 会话 ----
    private int _index;
    private readonly double _sessionStart;
    private double _lastDragEnd = double.NaN;
    private double _nextBeat;
    private double _beatPatchSum, _beatFrameSum;
    private int _beatFrames;
    private double _beatFps;
    private int _undoSoon;                                 // 擦完 3 秒内按撤销的次数
    private readonly Dictionary<string, int> _toolDrags = new();

    /// <summary>已经记了多少条橡皮拖拽（面板上显示"记录中 N 条"，用户看得见）。</summary>
    public int DragCount => _index;

    public EraserTelemetry(string csvPath, string summaryPath, double nowMs)
    {
        _csvPath = csvPath;
        _summaryPath = summaryPath;
        _sessionStart = nowMs;
        _nextBeat = nowMs + 5000;
        TryWrite(csvPath,
            "drag,tool,start_s,duration_ms,samples,length_px,speed_avg_px_s,speed_max_px_s,"
            + "strokes_touched,intervals_added,strokes_removed,compute_sum_ms,compute_avg_ms,"
            + "compute_max_ms,frames,patch_sum_ms,patch_max_ms,frame_sum_ms,"
            + "doc_strokes,doc_intervals,undo_depth,moves,move_gap_max_ms,move_rate_hz,"
            + "input_present_avg_ms,input_present_max_ms\n");
    }

    /// <summary>指针按下、开始一次橡皮拖拽。</summary>
    public void BeginDrag(Tool tool, float x, float y, double now)
    {
        _inDrag = true;
        _tool = tool;
        _startMs = now;
        _lastStepMs = now;
        _lastX = x; _lastY = y;
        _samples = 0;
        _moves = 0; _moveGapMax = 0; _lastMoveMs = now;
        _lengthPx = 0; _speedMax = 0;
        _touched = 0;
        _computeSum = 0; _computeMax = 0;
        _frames = 0; _patchSum = 0; _patchMax = 0; _frameSum = 0;
    }

    /// <summary>
    /// 擦除一步。调用方负责先把 <paramref name="computeMs"/>（这一步的命中+切段耗时）
    /// 量出来；<paramref name="intervalsNow"/> / <paramref name="strokesNow"/> 是文档当前的总数。
    /// </summary>
    public void Step(int touched, int intervalsNow, int strokesNow,
                     double computeMs, float x, float y, double now)
    {
        if (!_inDrag) return;
        if (_samples == 0)
        {
            _strokesAtStart = strokesNow;
            _intervalsAtStart = intervalsNow;
        }
        _samples++;
        _touched += touched;
        _computeSum += computeMs;
        if (computeMs > _computeMax) _computeMax = computeMs;

        // 速度：这一步的位移 / 这一步的时间。用来分辨"快划"和"慢抹"两种用法。
        float dx = x - _lastX, dy = y - _lastY;
        double dist = Math.Sqrt((double)dx * dx + (double)dy * dy);
        double dt = Math.Max(0.001, now - _lastStepMs);
        double speed = dist / dt * 1000.0;                 // px/s
        if (speed > _speedMax) _speedMax = speed;
        _lengthPx += dist;
        _lastX = x; _lastY = y; _lastStepMs = now;
    }

    /// <summary>
    /// 收到一条指针移动消息。**和"擦除步数"分开记**：一条消息可能插值成好几步擦除，
    /// 而消息之间的间隔才是"跟不跟手"——间隔 50ms 的拖动，看着就是一跳一跳的。
    /// </summary>
    public void Move(double now)
    {
        if (!_inDrag) return;
        _moves++;
        double gap = now - _lastMoveMs;
        if (gap > _moveGapMax) _moveGapMax = gap;
        _lastMoveMs = now;
    }

    /// <summary>拖拽过程中的每一帧（只累计在拖拽里的那些帧）。</summary>
    public void Frame(double patchMs, double totalMs, double inputToPresentMs = 0)
    {
        _beatPatchSum += patchMs;
        _beatFrameSum += totalMs;
        _beatFrames++;

        if (!_inDrag) return;
        _frames++;
        _patchSum += patchMs;
        _frameSum += totalMs;
        if (patchMs > _patchMax) _patchMax = patchMs;
        if (inputToPresentMs > 0)
        {
            _itpSum += inputToPresentMs;
            if (inputToPresentMs > _itpMax) _itpMax = inputToPresentMs;
        }
    }

    /// <summary>一次拖拽结束：立刻把这一条写进 CSV（并 flush）。</summary>
    public void EndDrag(InkDocument doc, int undoDepth, double now)
    {
        if (!_inDrag) return;
        _inDrag = false;
        _index++;
        _lastDragEnd = now;

        double dur = Math.Max(0.001, now - _startMs);
        int intervalsAdded = Math.Max(0, doc.TotalIntervals - _intervalsAtStart);
        int removed = Math.Max(0, _strokesAtStart - doc.Strokes.Count);
        double speedAvg = _lengthPx / dur * 1000.0;

        string key = _tool == Tool.PixelEraser ? "像素橡皮" : "整笔橡皮";
        _toolDrags[key] = _toolDrags.TryGetValue(key, out var n) ? n + 1 : 1;

        var ci = CultureInfo.InvariantCulture;
        string row = string.Join(",",
            _index.ToString(ci),
            key,
            ((_startMs - _sessionStart) / 1000.0).ToString("F2", ci),
            dur.ToString("F1", ci),
            _samples.ToString(ci),
            _lengthPx.ToString("F0", ci),
            speedAvg.ToString("F0", ci),
            _speedMax.ToString("F0", ci),
            _touched.ToString(ci),
            intervalsAdded.ToString(ci),
            removed.ToString(ci),
            _computeSum.ToString("F2", ci),
            (_samples > 0 ? _computeSum / _samples : 0).ToString("F2", ci),
            _computeMax.ToString("F2", ci),
            _frames.ToString(ci),
            _patchSum.ToString("F1", ci),
            _patchMax.ToString("F1", ci),
            _frameSum.ToString("F1", ci),
            doc.Strokes.Count.ToString(ci),
            doc.TotalIntervals.ToString(ci),
            undoDepth.ToString(ci),
            _moves.ToString(ci),
            _moveGapMax.ToString("F1", ci),
            (dur > 0 ? _moves / dur * 1000.0 : 0).ToString("F1", ci),
            (_frames > 0 ? _itpSum / _frames : 0).ToString("F1", ci),
            _itpMax.ToString("F1", ci));

        _rows.Add(row);
        TryAppend(_csvPath, row + "\n");
    }

    /// <summary>事件流水：换工具、撤销、重做、清空……带"距上一次擦完多久"。</summary>
    public void Note(string what, double now)
    {
        double since = double.IsNaN(_lastDragEnd) ? -1 : (now - _lastDragEnd) / 1000.0;
        string tail = since < 0 ? "（还没擦过）"
                    : since <= 3.0 && what.StartsWith("撤销") ? $"　← 擦完才 {since:F1}s"
                    : since <= 30.0 ? $"　（距上次擦完 {since:F1}s）" : "";
        if (since >= 0 && since <= 3.0 && what.StartsWith("撤销")) _undoSoon++;
        _events.Add($"[{(now - _sessionStart) / 1000.0,7:F1}s] {what}{tail}");
    }

    /// <summary>每 5 秒记一行心跳（帧率、重画耗时、内存、文档规模）。</summary>
    public void Beat(double now, double fps, InkDocument doc)
    {
        if (now < _nextBeat) return;
        _nextBeat = now + 5000;
        double patchAvg = _beatFrames > 0 ? _beatPatchSum / _beatFrames : 0;
        double frameAvg = _beatFrames > 0 ? _beatFrameSum / _beatFrames : 0;
        _beatFrames = 0; _beatPatchSum = 0; _beatFrameSum = 0;
        if (fps > 0) _beatFps = fps;
        _beats.Add($"[{(now - _sessionStart) / 1000.0,7:F1}s] fps {_beatFps,6:F1}"
                 + $"　帧 {frameAvg,5:F2}ms（重画 {patchAvg,5:F2}）"
                 + $"　内存 {Mem.Priv(),7:F1}MB"
                 + $"　对象 {doc.Strokes.Count,5}　擦除区间 {doc.TotalIntervals,5}");
    }

    /// <summary>退出时写汇总。</summary>
    public void Close(InkDocument doc, double now)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        double minutes = (now - _sessionStart) / 60000.0;

        sb.AppendLine("=== 橡皮手测台 · 会话汇总 ===");
        sb.AppendLine($"会话时长 {minutes:F1} 分钟；擦除拖拽共 {_rows.Count} 次"
                    + $"（{string.Join("、", _toolDrags.Select(kv => $"{kv.Key} {kv.Value} 次"))}）");
        sb.AppendLine($"结束时文档：对象 {doc.Strokes.Count}，擦除区间 {doc.TotalIntervals} 段，"
                    + $"点数 {doc.TotalPoints}，撤销栈 {doc.UndoDepth}");
        sb.AppendLine();

        if (_rows.Count == 0)
        {
            sb.AppendLine("（这一轮没有记录到任何橡皮拖拽——要么没擦，要么没走鼠标左键）");
        }
        else
        {
            // 逐条重新解析一遍，比在记录时维护一堆累加器清楚
            var dur = new List<double>(); var comp = new List<double>(); var patch = new List<double>();
            var speed = new List<double>(); var touched = new List<int>();
            var rate = new List<double>(); var gapMax = new List<double>();
            var itp = new List<double>(); var itpMaxList = new List<double>();
            foreach (var r in _rows)
            {
                var f = r.Split(',');
                if (f.Length < 21) continue;
                dur.Add(double.Parse(f[3], ci));
                speed.Add(double.Parse(f[6], ci));
                touched.Add(int.Parse(f[8], ci));
                comp.Add(double.Parse(f[11], ci));
                patch.Add(double.Parse(f[15], ci));
                if (f.Length >= 24)
                {
                    rate.Add(double.Parse(f[23], ci));
                    gapMax.Add(double.Parse(f[22], ci));
                }
                if (f.Length >= 26)
                {
                    itp.Add(double.Parse(f[24], ci));
                    itpMaxList.Add(double.Parse(f[25], ci));
                }
            }
            double P(List<double> xs, double q)
            {
                if (xs.Count == 0) return 0;
                var s = new List<double>(xs); s.Sort();
                return s[Math.Clamp((int)((s.Count - 1) * q), 0, s.Count - 1)];
            }

            sb.AppendLine("【一次拖拽的持续时间】单位 ms");
            sb.AppendLine($"  中位 {P(dur, 0.5):F0}　p90 {P(dur, 0.9):F0}　最长 {P(dur, 1):F0}");
            sb.AppendLine("【擦除本身（命中 + 切段）每条拖拽累计耗时】单位 ms");
            sb.AppendLine($"  中位 {P(comp, 0.5):F1}　p90 {P(comp, 0.9):F1}　最大 {P(comp, 1):F1}");
            sb.AppendLine("【重画（脏区光栅+上屏）每条拖拽累计耗时】单位 ms");
            sb.AppendLine($"  中位 {P(patch, 0.5):F1}　p90 {P(patch, 0.9):F1}　最大 {P(patch, 1):F1}");
            sb.AppendLine("【指针速度】单位 像素/秒（快划和慢抹是两种用法，分开看）");
            sb.AppendLine($"  中位 {P(speed, 0.5):F0}　p90 {P(speed, 0.9):F0}　最快 {P(speed, 1):F0}");
            sb.AppendLine("【一次拖拽碰到的笔画数】");
            sb.AppendLine($"  中位 {P(touched.ConvertAll(x => (double)x), 0.5):F0}　"
                        + $"最多 {P(touched.ConvertAll(x => (double)x), 1):F0}");
            sb.AppendLine("【指针消息率 / 相邻两条消息的最大间隔】");
            sb.AppendLine($"  中位 {P(rate, 0.5):F0} Hz　最低 {P(rate, 0):F0} Hz"
                        + $"　最大间隔 中位 {P(gapMax, 0.5):F0} ms　最差 {P(gapMax, 1):F0} ms");
            sb.AppendLine("【输入 → 上屏】从收到指针消息，到像素真的变了——跟不跟手就看它");
            sb.AppendLine($"  中位 {P(itp, 0.5):F1} ms　p90 {P(itp, 0.9):F1} ms　"
                        + $"单帧最差 {P(itpMaxList, 1):F1} ms（一个刷新周期 = 16.7 ms）");
            sb.AppendLine();
            sb.AppendLine($"【误擦信号】擦完 3 秒内按撤销：{_undoSoon} 次"
                        + ( _undoSoon == 0 ? "（没有——要么都擦对了，要么撤销不方便）" : ""));
        }

        sb.AppendLine();
        sb.AppendLine("【事件流水】换工具 / 撤销 / 重做 / 清空（时间轴）");
        foreach (var e in _events) sb.AppendLine("  " + e);
        sb.AppendLine();
        sb.AppendLine("【心跳】每 5 秒");
        foreach (var b in _beats) sb.AppendLine("  " + b);
        sb.AppendLine();
        sb.AppendLine($"逐条数据：{Path.GetFullPath(_csvPath)}");

        TryWrite(_summaryPath, sb.ToString());
    }

    // ---- 落盘：宁可丢日志，也不能因为写文件把程序搞崩 ----
    private static void TryWrite(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex) { Console.WriteLine($"手测台写文件失败（{path}）：{ex.Message}"); }
    }

    private static void TryAppend(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex) { Console.WriteLine($"手测台追加失败（{path}）：{ex.Message}"); }
    }
}
