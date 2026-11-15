using System.Globalization;
using System.Text;

namespace InkEngine;

/// <summary>
/// 触摸面积的**只统计**采样（2026-10-24）。
///
/// 背景：学校红外屏没有真正的笔，杆子和手指同源；以前用“接触面积 &gt; 手指基线×3”
/// 触发手掌橡皮，结果同一块屏上笔迹/橡皮来回切。产品已取消面积橡皮，只保留
/// 三指及以上擦。这里继续记录面积、基线和“按旧规则会不会判成手掌”，
/// 以后再决定面积到底该怎么用。
///
/// 只在触点落下时记一条，不逐帧刷日志；session.json / 设备卡也会带一份汇总。
/// </summary>
internal static class TouchStats
{
    static readonly object _gate = new();

    static int _downs, _one, _two, _three, _fourPlus;
    static int _write, _gesture, _erase, _ignore;
    static int _singleMode, _gestureMode;
    static int _wouldPalm;
    static long _sizeSum;
    static int _sizeMin = int.MaxValue, _sizeMax;
    static int _maxContacts;
    static readonly int[] _hist = new int[6];

    /// <summary>触点落下：记一条明细并累计汇总。</summary>
    public static void NoteDown(int contacts, int contactW, int contactH,
                                float sizePx, float baseline, float factor,
                                bool wouldPalm, string verdict,
                                bool gestureMode, Tool tool)
    {
        if (contacts < 1) contacts = 1;
        lock (_gate)
        {
            _downs++;
            if (contacts == 1) _one++;
            else if (contacts == 2) _two++;
            else if (contacts == 3) _three++;
            else _fourPlus++;
            if (contacts > _maxContacts) _maxContacts = contacts;

            if (gestureMode) _gestureMode++; else _singleMode++;
            switch (verdict)
            {
                case "Write": _write++; break;
                case "Gesture2": _gesture++; break;
                case "Erase": _erase++; break;
                default: _ignore++; break;
            }
            if (wouldPalm) _wouldPalm++;

            if (sizePx > 0f)
            {
                int s = (int)MathF.Round(sizePx);
                _sizeSum += s;
                if (s < _sizeMin) _sizeMin = s;
                if (s > _sizeMax) _sizeMax = s;
                int h = s < 20 ? 0 : s < 40 ? 1 : s < 80 ? 2 : s < 120 ? 3 : s < 200 ? 4 : 5;
                _hist[h]++;
            }
        }

        // 明细只写一行；日志上传后可以直接看 size / baseline / wouldPalm。
        Console.WriteLine(
            $"[touch] mode={(gestureMode ? "gesture" : "single")} contacts={contacts}"
            + $" size={(sizePx > 0 ? sizePx.ToString("F0", CultureInfo.InvariantCulture) : "0")}"
            + $" rect={contactW}x{contactH}"
            + $" base={(baseline > 0 ? baseline.ToString("F0", CultureInfo.InvariantCulture) : "0")}"
            + $" factor={factor.ToString("F1", CultureInfo.InvariantCulture)}"
            + $" wouldPalm={(wouldPalm ? 1 : 0)} verdict={verdict}"
            + $" tool={ToolNames.Of(tool)}");
    }

    /// <summary>退出前打一条汇总，确保即使不导出日志也能在 inkteach.log 里看到。</summary>
    public static void WriteSummary()
    {
        string line = SummaryText();
        if (line.Length > 0) Console.WriteLine("[touch] 汇总 " + line);
    }

    public static string SummaryText()
    {
        lock (_gate)
        {
            if (_downs == 0) return "0 次触点";
            int n = _hist[0] + _hist[1] + _hist[2] + _hist[3] + _hist[4] + _hist[5];
            string avg = n > 0
                ? Math.Round(_sizeSum / (double)n, 0).ToString("F0", CultureInfo.InvariantCulture)
                : "0";
            int min = _sizeMin == int.MaxValue ? 0 : _sizeMin;
            return $"{_downs} 次触点；1/2/3/4+={_one}/{_two}/{_three}/{_fourPlus}；"
                 + $"Write/Gesture/Erase/Ignore={_write}/{_gesture}/{_erase}/{_ignore}；"
                 + $"面积 min/max/avg={min}/{_sizeMax}/{avg}；"
                 + $"旧规则判手掌={_wouldPalm}；模式 手势/单指={_gestureMode}/{_singleMode}";
        }
    }

    /// <summary>给 session.json / 设备卡用的紧凑 JSON（不依赖 JSON 库）。</summary>
    public static string Json()
    {
        lock (_gate)
        {
            int n = _hist[0] + _hist[1] + _hist[2] + _hist[3] + _hist[4] + _hist[5];
            string avg = n > 0
                ? Math.Round(_sizeSum / (double)n, 1).ToString("F1", CultureInfo.InvariantCulture)
                : "0";
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"downs\":").Append(_downs);
            sb.Append(",\"contacts\":{\"one\":").Append(_one)
              .Append(",\"two\":").Append(_two)
              .Append(",\"three\":").Append(_three)
              .Append(",\"fourPlus\":").Append(_fourPlus)
              .Append(",\"max\":").Append(_maxContacts).Append('}');
            sb.Append(",\"verdict\":{\"write\":").Append(_write)
              .Append(",\"gesture\":").Append(_gesture)
              .Append(",\"erase\":").Append(_erase)
              .Append(",\"ignore\":").Append(_ignore).Append('}');
            sb.Append(",\"size\":{\"min\":").Append(_sizeMin == int.MaxValue ? 0 : _sizeMin)
              .Append(",\"max\":").Append(_sizeMax)
              .Append(",\"avg\":").Append(avg)
              .Append(",\"hist\":[").Append(_hist[0]);
            for (int i = 1; i < _hist.Length; i++) sb.Append(',').Append(_hist[i]);
            sb.Append("]}");
            sb.Append(",\"wouldPalm\":").Append(_wouldPalm);
            sb.Append(",\"modes\":{\"gesture\":").Append(_gestureMode)
              .Append(",\"single\":").Append(_singleMode).Append('}');
            sb.Append('}');
            return sb.ToString();
        }
    }
}
