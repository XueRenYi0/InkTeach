using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：真实走一遍「选中 → 渲染 PNG → HTTP OCR → 就地替换 → 撤销」。
    /// 识别服务用本机 mock（不联网、不花钱）：
    ///   --handocr &lt;板书.inkb&gt; &lt;ocr.json&gt; &lt;输出png&gt; [文字区左边界]
    /// </summary>
    private void HandOcrPng(string inPath, string ocrPath, string outPath, float textLeft)
    {
        Console.WriteLine();
        Console.WriteLine("=== 手写优化 OCR 闭环（本机 mock） ===");
        if (!File.Exists(inPath) || !File.Exists(ocrPath))
        {
            Console.WriteLine("文件不存在");
            ExitCode = 1;
            return;
        }

        string text = string.Join("\n", LoadOcrLines(ocrPath).Select(CleanOcrLine));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() => ServeOne(listener, text));
        Console.WriteLine($"mock OCR：127.0.0.1:{port}");

        InkSerializer.LoadInto(Doc, File.ReadAllBytes(inPath));
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes)
        {
            var b = s.WorldInkBounds;
            if (!b.IsEmpty && b.MaxX >= textLeft) Doc.Selected.Add(s);
        }
        int before = Doc.Strokes.Count;
        Console.WriteLine($"载入 {before} 个对象，选中 {Doc.Selected.Count} 个文字候选");

        OcrClient.TestOverride = new OcrClient.OcrConfig
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Key = "test",
            Model = "mock-vlm",
            Thinking = "none",
        };

        OptimizeHandwritingFromUi();
        var timeout = Environment.TickCount64 + 20_000;
        while (HandOcrBusy && Environment.TickCount64 < timeout)
            SettleFrames(50);

        string status = HandOcrStatus;
        Console.WriteLine($"状态：{status}");
        bool ok = status.StartsWith("已优化", StringComparison.Ordinal) && !HandOcrBusy;
        Console.WriteLine($"替换后：{Doc.Strokes.Count} 个对象，选中 {Doc.Selected.Count} 个");

        var box = EditRegion.Of(Doc.Strokes);
        if (!box.IsEmpty)
        {
            box = box.Inflate(24f * DpiScale);
            int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            var bgra = _windows[0].RenderStrokesToBgra(Doc.Strokes, box, w, h);
            if (bgra != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out string tag);
                if (bytes != null)
                {
                    File.WriteAllBytes(outPath, bytes);
                    Console.WriteLine($"写出：{outPath}（{w}×{h}，{tag}）");
                }
            }
        }

        Doc.Undo();
        bool undoOk = Doc.Strokes.Count == before;
        Console.WriteLine(undoOk ? $"一步撤销：OK（回到 {before}）" : $"一步撤销：FAIL（{Doc.Strokes.Count} ≠ {before}）");
        if (!undoOk) ok = false;

        OcrClient.TestOverride = null;
        listener.Stop();
        try { server.Wait(2000); } catch { }
        Console.WriteLine(ok ? "结论：OK" : "结论：FAIL");
        ExitCode = ok ? 0 : 1;
    }

    /// <summary>最小 HTTP 服务：收一个请求，回一个 OpenAI 兼容 JSON。</summary>
    private static void ServeOne(TcpListener listener, string text)
    {
        using var client = listener.AcceptTcpClient();
        using var stream = client.GetStream();
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 64 * 1024)
        {
            int n = stream.Read(one, 0, 1);
            if (n <= 0) break;
            head.Add(one[0]);
            int c = head.Count;
            if (c >= 4 && head[c - 4] == '\r' && head[c - 3] == '\n' &&
                head[c - 2] == '\r' && head[c - 1] == '\n')
                break;
        }
        string headers = Encoding.ASCII.GetString(head.ToArray());
        int len = 0;
        foreach (var line in headers.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(line.Substring(15).Trim(), out len);
        }
        var body = new byte[Math.Max(0, len)];
        int read = 0;
        while (read < body.Length)
        {
            int n = stream.Read(body, read, body.Length - read);
            if (n <= 0) break;
            read += n;
        }

        string json = JsonSerializer.Serialize(new
        {
            id = "mock",
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" },
            },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 },
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        byte[] payload = Encoding.UTF8.GetBytes(json);
        string resp = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n" +
                      $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(resp);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}
