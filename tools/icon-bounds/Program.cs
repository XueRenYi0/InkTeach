using System.Text.RegularExpressions;

// 量每个 Fluent 图标在 24x24 画布里的实际墨迹范围。
// 用途：工具条上"图标看起来偏小"通常不是画布小，而是图标自己在画布里留白多。
// 知道真实边界之后，就能按墨迹大小对齐，而不是按画布对齐。

var file = args.Length > 0 ? args[0] : "src/InkUi/FluentIcons.Paths.cs";
var text = File.ReadAllText(file);

foreach (Match m in Regex.Matches(text,
    @"private const string (Path_\w+) =\s*\r?\n\s*""([^""]+)"";"))
{
    string name = m.Groups[1].Value;
    var bounds = Measure(m.Groups[2].Value);
    Console.WriteLine($"{name,-18} x[{bounds.MinX,6:F2},{bounds.MaxX,6:F2}] "
                    + $"y[{bounds.MinY,6:F2},{bounds.MaxY,6:F2}] "
                    + $"墨迹 {bounds.MaxX - bounds.MinX,5:F2} x {bounds.MaxY - bounds.MinY,5:F2}");
}

static (float MinX, float MinY, float MaxX, float MaxY) Measure(string d)
{
    float minX = float.MaxValue, minY = float.MaxValue;
    float maxX = float.MinValue, maxY = float.MinValue;
    var nums = new List<float>();
    var tokens = new List<(char Cmd, float[] Args)>();

    int i = 0;
    char cmd = '\0';
    float cx = 0, cy = 0;

    while (i < d.Length)
    {
        while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
        if (i >= d.Length) break;
        if (char.IsLetter(d[i])) { cmd = d[i]; i++; }

        char op = char.ToUpperInvariant(cmd);
        bool rel = char.IsLower(cmd);
        int count = op switch
        {
            'M' or 'L' or 'T' => 2,
            'H' or 'V' => 1,
            'C' => 6,
            'S' or 'Q' => 4,
            'Z' => 0,
            _ => -1,
        };
        if (count <= 0) { if (op == 'Z') continue; break; }

        nums.Clear();
        for (int k = 0; k < count; k++)
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
            int s = i;
            if (i < d.Length && (d[i] == '+' || d[i] == '-')) i++;
            while (i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] == '.')) i++;
            nums.Add(float.Parse(d.Substring(s, i - s),
                System.Globalization.CultureInfo.InvariantCulture));
        }

        void Add(float x, float y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        if (op == 'M' || op == 'L')
        {
            cx = rel ? cx + nums[0] : nums[0];
            cy = rel ? cy + nums[1] : nums[1];
            Add(cx, cy);
        }
        else if (op == 'H')
        {
            cx = rel ? cx + nums[0] : nums[0];
            Add(cx, cy);
        }
        else if (op == 'V')
        {
            cy = rel ? cy + nums[0] : nums[0];
            Add(cx, cy);
        }
        else
        {
            // 曲线：把控制点也算进去，估的是保守边界，对齐够用。
            for (int k = 0; k + 1 < nums.Count; k += 2)
            {
                float x = rel ? cx + nums[k] : nums[k];
                float y = rel ? cy + nums[k + 1] : nums[k + 1];
                Add(x, y);
                cx = x; cy = y;
            }
        }
    }

    return (minX, minY, maxX, maxY);
}
