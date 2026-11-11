using System.Numerics;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>造一张 640×420 的浅灰“卷面图”，只为走通图片对象这条链路。</summary>
    private static ImageData MakePaperImageForTest(int w = 640, int h = 420)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                // 纸白 + 几道“横线”，让缩略图和真实卷面更像。
                bool line = (y % 60) is > 40 and < 44;
                byte v = (byte)(line ? 205 : 244);
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        }
        return ImageData.Adopt(w, h, px);
    }

    private Stroke InsertPaperImageForTest(ImageData img)
    {
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        float x = _virtualX + 80f;
        float y = _virtualY + 80f;
        s.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(s);
        Doc.SelectOnly(new[] { s });
        SettleFrames(160);
        return s;
    }

    /// <summary>从图片文件读一张卷面（live 用）。</summary>
    private static ImageData LoadPaperImageForTest(string path)
    {
        var r = ImageFile.Load(path);
        return r.Success ? ImageData.AdoptPremultiplied(r.Width, r.Height, r.Bgra) : null;
    }

    /// <summary>
    /// 自检：图片「识别成题卡」的**非网络**部分。
    /// 覆盖：JSON 抠取（数组 / 连写对象 / 非法转义）、AI 菜单两项、落卡排版、
    /// 摆位、生成答案行、一步撤销、结构化失败的富文本兜底。
    /// </summary>
    private void ImageCardTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图片识别成题卡（离线） ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        BoardOn = true;
        ShowHud = false;

        // ---- 1. JSON 抠取 ---------------------------------------------------
        var arr = AiQuestionClient.ParseCards(
            "[{\"stem\":\"1. 已知 $x^2=4$，求 $x$。\",\"options\":[],\"answer\":\"\",\"analysis\":\"\",\"need_review\":false}," +
            "{\"stem\":\"2. 求 $\\\\frac{1}{2}+\\\\frac{1}{3}$。\",\"options\":[\"A. 1\",\"B. 5/6\"],\"need_review\":true}]");
        Check("JSON 数组 → 2 题", arr.Count == 2, $"count={arr.Count}");
        Check("选项进数组", arr.Count == 2 && arr[1].Options.Count == 2,
              $"opts={arr.Count}");

        var concat = AiQuestionClient.ParseCards(
            "{\"stem\":\"题一\",\"options\":[]}\n{\"stem\":\"题二\",\"options\":[]}");
        Check("连写两个对象（Extra data 场景）→ 2 题", concat.Count == 2, $"count={concat.Count}");

        var badEsc = AiQuestionClient.ParseCards(
            "{\"stem\":\"$\\frac{1}{2}$ 的值\",\"options\":[]}");
        Check("LaTeX 非法转义能救回", badEsc.Count == 1 && badEsc[0].Stem.Contains("frac"),
              $"count={badEsc.Count}");

        // ---- 2. 图片 AI 菜单 ------------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var img = MakePaperImageForTest();
        var src = InsertPaperImageForTest(img);
        OpenSelectionMenuForTest(ai: true);
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/imgcard-ai-menu.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Check("图片 AI 菜单：2 行、Image 类型",
              AiMenuRows == 2 && AiMenuKindNow == AiMenuKind.Image,
              $"rows={AiMenuRows} kind={AiMenuKindNow}");
        SelAiMenuOpen = false;

        // ---- 3. 落卡 --------------------------------------------------------
        var items = new List<AiQuestionClient.CardItem>
        {
            new()
            {
                Stem = "1. 已知圆 $C: x^2+y^2=4$，求半径。",
                Options = { "A. 1", "B. 2" },
                NeedReview = false,
            },
            new()
            {
                Stem = "2. 求 $\\frac{1}{2}+\\frac{1}{3}$。",
                Options = { "A. $\\frac{5}{6}$", "B. 1" },
                NeedReview = true,
            },
        };
        var card = InsertImageCardForTest(src, items);
        SettleFrames(200);
        var tb = card?.TextBox;
        Check("落成题卡（AiQuestion）", tb != null && tb.Role == TextBoxRole.AiQuestion,
              $"role={tb?.Role}");
        Check("2 道题", tb != null && tb.Questions.Count == 2, $"n={tb?.Questions.Count}");
        Check("选项并进题干 + 需核对标记",
              tb != null && tb.Questions[1].Stem.Contains("B. 1")
              && tb.Questions[1].Stem.StartsWith("【请核对】", StringComparison.Ordinal),
              $"stem={ClipForTest(tb?.Questions[1].Stem, 30)}");
        Check("题干/答案默认：答案为空", tb != null
              && string.IsNullOrEmpty(tb.Questions[0].Answer),
              $"ans='{tb?.Questions[0].Answer}'");
        var imgBox = src.PaddedBounds;
        Check("摆在图片右侧、不压住原图",
              card != null && card.PaddedBounds.MinX >= imgBox.MaxX - 0.5f,
              $"cardX={card?.PaddedBounds.MinX:F0} imgRight={imgBox.MaxX:F0}");

        var lines = tb?.EnsureLayout();
        bool hasGen = false;
        if (lines != null)
            foreach (var l in lines)
                if (l.Kind == TextBoxLineKind.GenerateAnswers) { hasGen = true; break; }
        Check("有「生成答案（AI）」行", hasGen, $"lines={lines?.Count}");

        ScreenProbe.SaveBmp("reports/imgcard-result.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/imgcard-ai-menu.bmp / reports/imgcard-result.bmp");

        // 3b. 模拟“答案已生成”：排版必须立刻换成 答案/解析 两段（不再显示生成按钮）。
        ApplyAnswersForTest(card, 0, "B", "配方得圆心与半径，直接读出半径 2。");
        SettleFrames(200);
        bool hasAnswerLine = false, hasToggle = false;
        foreach (var l in tb.EnsureLayout())
        {
            if (l.Kind == TextBoxLineKind.Text && l.Text == "答案") hasAnswerLine = true;
            if (l.Kind == TextBoxLineKind.Toggle) hasToggle = true;
        }
        Check("填答案后：出现答案段、按钮变收起",
              hasAnswerLine && hasToggle && tb.LayoutHeight > 300f,
              $"答案行={hasAnswerLine} 收起={hasToggle} 高={tb.LayoutHeight:F0}");
        ScreenProbe.SaveBmp("reports/imgcard-answers.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/imgcard-answers.bmp");

        // ---- 4. 一步撤销 ----------------------------------------------------
        Doc.Undo();                       // 先撤“答案”
        SettleFrames(120);
        bool backToEmpty = tb.Questions[0].Answer.Length == 0 && !tb.ShowAnswers;
        Check("撤销答案：回到生成前、按钮回来", backToEmpty,
              $"answer='{tb.Questions[0].Answer}' show={tb.ShowAnswers}");

        int before = Doc.Strokes.Count;
        Doc.Undo();                       // 再撤“插卡”
        SettleFrames(120);
        Check("撤销 → 卡片消失", Doc.Strokes.Count == before - 1,
              $"{before} → {Doc.Strokes.Count}");

        // ---- 5. 兜底 --------------------------------------------------------
        var fb = InsertImageCardForTest(src, null,
            ocrText: "1. 已知 $x^2=4$，求 $x$。\n2. 求 $\\frac{1}{2}$。",
            fallback: true);
        SettleFrames(160);
        var ftb = fb?.TextBox;
        Check("兜底 = 富文本只读框（能渲染公式）",
              ftb != null && ftb.Role == TextBoxRole.Normal && ftb.Rich && ftb.ReadOnly
              && ftb.Text.Contains("x^2=4") && ftb.Text.Contains("识别原文"),
              $"role={ftb?.Role} rich={ftb?.Rich} ro={ftb?.ReadOnly}");
        ScreenProbe.SaveBmp("reports/imgcard-fallback.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        Console.WriteLine($"图片识别成题卡（离线）：{pass} 通过，{fail} 失败");
    }

    /// <summary>
    /// 自检（真实 API）：本地卷面照片 → qwen3.5-ocr → deepseek 结构化 → 落卡。
    /// 用法：--imgcardlive [图片路径]
    /// </summary>
    private void ImageCardLiveTest(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 图片识别成题卡（真实 API） ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        BoardOn = true;
        ShowHud = false;

        string file = string.IsNullOrWhiteSpace(path)
            ? @"C:\Users\LHL-XZX\Desktop\软件\数学题目图片\80e479696e68c214328872b1f908a433.jpg"
            : path;
        var img = LoadPaperImageForTest(file);
        if (img == null)
        {
            Console.WriteLine($"读不到图片：{file}");
            return;
        }
        Console.WriteLine($"样本：{file}（{img.Width}×{img.Height}）");

        Doc.Clear();
        Doc.ClearHistory();
        var src = InsertPaperImageForTest(img);
        Doc.SelectOnly(new[] { src });

        // 自检进程的 APPDATA 被换到临时目录，ai.json 不在那儿；真实 API 自检要显式指回用户的配置。
        string realCfg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Roaming", "InkTeach", "ai.json");
        if (File.Exists(realCfg) && !File.Exists(OcrClient.ConfigPath))
            OcrClient.ConfigPathOverride = realCfg;
        // 真实配置里没有能看图的模型时，才用仓库根 API-KEYS.local.json 拼一份临时配置，
        // 保证「看图用 qwen3.5-ocr、写作用 deepseek-flash」这条真实组合能跑起来。
        if (OcrProviders.PickVision(OcrProviders.Load()) == null)
        {
            string repoKeys = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop", "软件", "API-KEYS.local.json");
            string tempCfg = BuildTempAiConfigForTest(repoKeys);
            if (tempCfg != null) OcrClient.ConfigPathOverride = tempCfg;
        }
        Console.WriteLine($"AI 配置：{OcrClient.ConfigPath}（{(File.Exists(OcrClient.ConfigPath) ? "有" : "无")}）");

        var state = OcrProviders.Load();
        foreach (var p in state.Providers)
            Console.WriteLine($"  配置：{p.Id} model={p.Model} key={(string.IsNullOrWhiteSpace(p.Key) ? "空" : "有")}");
        var vision = OcrProviders.PickVision(state);
        var text = OcrProviders.PickText(state);
        Console.WriteLine($"看图模型：{vision?.Model}（{vision?.BaseUrl}）");
        Console.WriteLine($"整理模型：{text?.Model}（{text?.BaseUrl}）");

        double t0 = NowSeconds();
        InsertImageCardFromUi(src);
        int guard = 0;
        while (ImageCardBusy && guard++ < 900) SettleFrames(100);
        SettleFrames(200);
        double dt = NowSeconds() - t0;

        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox && !ReferenceEquals(s, src)) { card = s; break; }
        var tb = card?.TextBox;
        Console.WriteLine($"耗时 {dt:F1}s；结果：role={tb?.Role} 题数={tb?.Questions.Count} " +
                          $"rich={tb?.Rich} 标题={tb?.Title}");
        Console.WriteLine($"来源：{tb?.Meta}");
        if (tb != null)
        {
            for (int i = 0; i < Math.Min(3, tb.Questions.Count); i++)
                Console.WriteLine($"  第{i + 1}题：{ClipForTest(tb.Questions[i].Stem, 90)}");
        }
        ScreenProbe.SaveBmp("reports/imgcard-live.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/imgcard-live.bmp");

        // 可选：真的生成一次答案（--imgcardlive <图片> answers）
        if (tb != null && tb.Role == TextBoxRole.AiQuestion)
        {
            Console.WriteLine("点「生成答案」…");
            double t1 = NowSeconds();
            StartGenerateAnswers(card);
            guard = 0;
            while (CardAnswersBusy && guard++ < 900) SettleFrames(100);
            SettleFrames(200);
            Console.WriteLine($"答案耗时 {NowSeconds() - t1:F1}s；" +
                              $"第1题答案：{ClipForTest(tb.Questions[0].Answer, 80)}");
            Console.WriteLine($"第1题解析：{ClipForTest(tb.Questions[0].Analysis, 80)}");
            var ls = tb.EnsureLayout();
            var kinds = new System.Text.StringBuilder();
            foreach (var l in ls)
                kinds.Append(l.Kind).Append('(').Append(ClipForTest(l.Text, 6)).Append(") ");
            Console.WriteLine($"布局 {ls.Count} 行：{kinds}");
            Console.WriteLine($"ShowAnswers={tb.ShowAnswers} 高={tb.LayoutHeight:F0}");
            ScreenProbe.SaveBmp("reports/imgcard-live-answers.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine("出图：reports/imgcard-live-answers.bmp");
        }
    }

    /// <summary>
    /// 自检专用：把仓库根的 API-KEYS.local.json 变成一份临时 ai.json
    /// （只看图那家用 qwen3.5-ocr），这样真实 API 自检不依赖用户当前设置。
    /// 文件写到系统临时目录，**不把密钥写进仓库**。
    /// </summary>
    private static string BuildTempAiConfigForTest(string keysPath)
    {
        try
        {
            if (!File.Exists(keysPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(keysPath));
            if (!doc.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var providers = new List<object>();
            string active = null;
            foreach (var k in keys.EnumerateArray())
            {
                string id = k.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                string baseUrl = k.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "" : "";
                string key = k.TryGetProperty("key", out var kk) ? kk.GetString() ?? "" : "";
                string model = k.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                if (id.Length == 0 || key.Length == 0) continue;
                // 图片 OCR 用实测最稳的那个；文字整理仍用 DeepSeek。
                if (id == "dashscope") model = "qwen3.5-ocr";
                if (active == null && id.StartsWith("deepseek", StringComparison.Ordinal))
                    active = id;
                providers.Add(new
                {
                    id, name = id, kind = "custom",
                    baseUrl, key, model, thinking = "auto",
                });
            }
            if (providers.Count == 0) return null;
            string dir = Path.Combine(Path.GetTempPath(), "inkteach-imgcard");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "ai.json");
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
                new { activeId = active ?? "dashscope", providers },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static string ClipForTest(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…")
             .Replace("\r", "").Replace("\n", " / ");

    private static double NowSeconds() => Environment.TickCount64 / 1000.0;
}
