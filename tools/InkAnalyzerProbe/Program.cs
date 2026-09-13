// Can a plain, unpackaged desktop app use Windows' own ink shape recognition?
// If yes, "draw a circle then snap it to a circle" does not need to be written
// from scratch. If no, we own that algorithm too.

using System.Numerics;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Analysis;

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("=== Windows InkAnalyzer 桌面可用性探测 ===");

static InkStroke MakeStroke(IEnumerable<Vector2> points)
{
    var builder = new InkStrokeBuilder();
    return builder.CreateStroke(points.Select(p => new Windows.Foundation.Point(p.X, p.Y)));
}

static async Task ProbeAsync(string label, Vector2[] pts)
{
    try
    {
        var analyzer = new InkAnalyzer();
        var stroke = MakeStroke(pts);
        analyzer.AddDataForStrokes(new[] { stroke });

        var result = await analyzer.AnalyzeAsync();
        Console.WriteLine($"  {label,-10} 分析状态: {result.Status}");

        int drawings = 0;
        foreach (var node in analyzer.AnalysisRoot.Children)
        {
            Console.WriteLine($"     节点: {node.Kind}");
            if (node is InkAnalysisInkDrawing d)
            {
                drawings++;
                Console.WriteLine($"       识别为绘图: {d.DrawingKind}  包围盒 {d.BoundingRect}");
            }
            else if (node is InkAnalysisInkWord w)
            {
                Console.WriteLine($"       识别为文字: {string.Join(",", w.TextAlternates)}");
            }
        }
        if (drawings == 0) Console.WriteLine("       （没有识别出绘图节点）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {label,-10} 失败: {ex.GetType().Name}: {ex.Message}");
    }
}

// A hand-drawn circle: 64 samples with a little noise, not a perfect circle.
var rnd = new Random(7);
var circle = new List<Vector2>();
for (int i = 0; i <= 64; i++)
{
    float a = i / 64f * MathF.PI * 2f;
    float r = 150f + (float)(rnd.NextDouble() - 0.5) * 10f;
    circle.Add(new Vector2(500 + MathF.Cos(a) * r, 500 + MathF.Sin(a) * r));
}

// A hand-drawn triangle.
var triangle = new List<Vector2>();
Vector2[] corners = { new(300, 300), new(600, 320), new(430, 620), new(300, 300) };
for (int e = 0; e < 3; e++)
{
    for (int i = 0; i < 20; i++)
    {
        float t = i / 20f;
        var p = Vector2.Lerp(corners[e], corners[e + 1], t);
        triangle.Add(p + new Vector2((float)(rnd.NextDouble() - 0.5) * 6f, (float)(rnd.NextDouble() - 0.5) * 6f));
    }
}

await ProbeAsync("圆形", circle.ToArray());
await ProbeAsync("三角形", triangle.ToArray());

Console.WriteLine();
Console.WriteLine("完成");
