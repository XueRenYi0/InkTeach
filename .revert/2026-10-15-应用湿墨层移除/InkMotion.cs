// 鏈枃浠剁敱 App.cs 鎷嗗嚭锛?026-10-07锛夛細InkMotion 杩欎竴缁勩€?// **绾惉瀹讹紝閫昏緫涓€瀛楁湭鏀?* 鈥斺€?闈?partial class 鍏变韩 App 鐨勭鏈夋垚鍛樸€?// 鎷嗗紑鐨勭洰鐨勶細浜у搧浠ｇ爜涓庤嚜妫€浠ｇ爜浜掍笉骞叉壈锛屼汉鍜?AI 璇讳唬鐮佹椂涓嶅繀浜掔浉绌挎彃銆?
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

    private void CurveTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 鍥涚鏇茬嚎鑷锛堟姏鐗╃嚎 / 鍙屾洸绾?/ 姝ｅ鸡 / 浣欏鸡锛?==");
        Console.WriteLine($"  鏈満 DPI 缂╂斁 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "閫氳繃" : "澶辫触")}  {name,-38} {detail}");
        }
        bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
        // 鏂瑰悜姣旇緝锛氫袱涓崟浣嶅悜閲忋€屽嚑涔庣浉鍚屻€嶏紙鐢ㄤ簬"鏂瑰悜娌″彉 / 鍙樺埌鍝幓浜?杩欑被鏂█锛夈€?        bool NearDir(Vector2 a, Vector2 b) => Vector2.Distance(a, b) <= 0.01f;

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        float minAxis = ShapeMinAxisLogical * DpiScale;
        var ink = new Color4(1f, 0f, 1f, 1f);            // 鍝佺孩锛氬睆骞曚笂濂芥暟
        Host.Commands.SetColor(ink);

        // 閫犱竴鏉?鍒氳捣鎵?鐨勬洸绾匡紙鍙湁 BeginShapeAt 閾虹殑閭ｄ竴涓崰浣嶇偣锛夛紝
        // 鍐嶈蛋 Set*Box 鈥斺€?鍜岀湡鏈烘嫋鍔ㄨ蛋鐨勬槸鍚屼竴鏉¤矾銆?        Stroke NewCurve(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke { Tool = tool, Kind = kind, Color = ink, Width = 4f * DpiScale };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }
        // 鐐瑰埌绾挎鐨勮窛绂伙紙楠?涓ゆ敮涔嬮棿鏈夋病鏈夊菇鐏电嚎娈?鐢級銆?        float DistPointSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        // ================= 鈶?鐢绘硶锛氱湡鏈?*涓€绗旀嫋**鐢讳竴鏉℃姏鐗╃嚎 =================
        // 鍙ｅ緞锛堢敤鎴?2026-09-20 鐓?InkClass 鐨?`case 20/21`锛夛細**椤剁偣 = 鎸変笅閭ｄ釜鐐?*锛?        // 涓€娆℃嫋鍒?鏇茬嚎瑕佺粡杩囩殑鍦版柟"銆佹澗鎵嬪氨鎴愶紱鏈濆悜鏄敾涔嬪墠鍦ㄥ浘褰㈤潰鏉夸笂閫夊ソ鐨?        // 锛堣繖閲岃蛋榛樿鐨?寮€鍙ｅ悜涓?锛夈€?        Doc.Clear();
        Doc.ClearHistory();
        SetToolFromUi(Tool.Parabola);
        float px = _virtualX + 900f, py = _virtualY + 900f;

        // 鎸変笅锛堣繕娌℃嫋锛夛細鍗婃垚鍝佷笉杩涙枃妗ｃ€?        SendMouse((int)px, (int)py, 0);                            SettleFrames(60);
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
        Check("鎶涚墿绾匡細鎸変笅锛堝畾椤剁偣锛変箣鍚?*杩樻病鎻愪氦**锛堝崐鎴愬搧涓嶈繘鏂囨。锛?,
              Doc.Strokes.Count == 0, $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 0锛?);

        // 鎷栧埌椤剁偣鍙充笂鏂?120 脳 100锛氭洸绾?*缁忚繃杩欎釜鐐?*鈥斺€?        // 鏈濅笂寮€鍙ｆ椂 `s = 100銆乼 = 120` 鈫?`p = t虏/(2s) = 14400/200 = 72`銆?        SendMouse((int)(px + 120f), (int)(py - 100f), 0);   SettleFrames(240);
        Check("鎶涚墿绾匡細鎷栧姩鐨勬椂鍊欐洸绾?*缁忚繃鎸囬拡閭ｄ釜鐐?*锛坧 = t虏/2s = 72锛夛紙卤2锛?,
              ActiveStroke != null && Near(ActiveStroke.ParabolaPLocal(), 72f, 2f),
              $"p {(ActiveStroke == null ? -1f : ActiveStroke.ParabolaPLocal()):F1}锛堟湡鏈?72锛?);

        // 鏉炬墜锛氬畬鎴愶紙鎻愪氦灏卞彂鐢熷湪杩欎竴鍒伙級銆?        SendMouse((int)(px + 120f), (int)(py - 100f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);

        var pbDraw = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("鎶涚墿绾匡細**鏉炬墜涔嬪悗**瀵硅薄鎵嶅嚭鏉ワ紙涓€鏉℃姏鐗╃嚎锛?,
              pbDraw != null && pbDraw.Kind == StrokeKind.Parabola,
              pbDraw == null ? $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 1锛? : $"Kind={pbDraw.Kind}");
        if (pbDraw == null) { Console.WriteLine("  锛堝悗闈㈠嚑椤规病娉曢獙锛?); _quit = true; return; }

        Check("鎶涚墿绾匡細涓や釜瀹氫箟鍏冪礌锛堥《鐐?锛?鏇茬嚎涓婄殑閭ｄ釜鐐癸級",
              pbDraw.Points.Count == 2, $"鎺у埗鐐?{pbDraw.Points.Count} 涓?);
        Check("鎶涚墿绾匡細**椤剁偣 = 鎸変笅閭ｄ釜鐐?*锛埪? 鍍忕礌锛?,
              Near(pbDraw.CurvePointLocal(0).X, px, 2f) && Near(pbDraw.CurvePointLocal(0).Y, py, 2f),
              $"椤剁偣 ({pbDraw.CurvePointLocal(0).X:F0},{pbDraw.CurvePointLocal(0).Y:F0})"
              + $" 鏈熸湜 ({px:F0},{py:F0})");
        Check("鎶涚墿绾匡細**缁忚繃鐐?= 鏈€鍚庣偣鐨勯偅涓綅缃?*锛埪? 鍍忕礌锛?,
              Near(pbDraw.CurvePointLocal(1).X, px + 120f, 2f)
              && Near(pbDraw.CurvePointLocal(1).Y, py - 100f, 2f),
              $"缁忚繃鐐?({pbDraw.CurvePointLocal(1).X:F0},{pbDraw.CurvePointLocal(1).Y:F0})"
              + $" 鏈熸湜 ({px + 120f:F0},{py - 100f:F0})");
        Check("鎶涚墿绾匡細鏈濆悜 = **寮€鍙ｅ悜涓?*锛堥粯璁ゅ€硷紝涓嶆槸浠庝袱鐐规帹鐨勶級",
              pbDraw.EffectiveAxis == CurveAxis.OpenUp && Near(pbDraw.ParabolaDirLocal().Y, -1f, .01f),
              $"{pbDraw.EffectiveAxis} / {pbDraw.ParabolaDirLocal()}");
        Check("鎶涚墿绾匡細p = 72锛埪?锛?, Near(pbDraw.ParabolaPLocal(), 72f, 4f),
              $"p {pbDraw.ParabolaPLocal():F1}");
        Check("鎶涚墿绾匡細涓€绗旀嫋鍒板簳 = **涓€姝ユ挙閿€**锛堜笉鏄袱姝ワ級",
              Doc.UndoDepth == 1, $"鎾ら攢鏍?{Doc.UndoDepth} 姝?);

        // ================= 鈶?鍑犱綍绠楀紡锛堢洿鎺ユ憜鐐癸紝涓嶈蛋宸ュ叿锛?=================
        Doc.Clear();
        Doc.ClearHistory();

        // ---- 鎶涚墿绾匡細椤剁偣 (400,400)銆?*寮€鍙ｅ悜涓?*銆佺粡杩囩偣 (700,250) 鈫?p = 300 ----
        // 缁忚繃鐐瑰彇鐨勬槸 `u = 1` 澶勭殑鏇茬嚎鐐癸紙涔熷氨鏄€氬緞绔偣锛夛細`s = p/2 = 150銆乼 = p = 300`
        // 鈫?鍙嶈В `p = t虏/(2s) = 300`銆傚畠鍜屼笂涓€鐗?椤剁偣 + 寮犲彛鐐?(400,250)"鐢诲嚭鏉ョ殑鏄?        // **鍚屼竴鏉℃洸绾?*锛屾墍浠ヤ笅闈㈤偅浜涚揣妗?/ 鑴忓尯鏂█涓€涓兘涓嶇敤鏀广€?        var vb = NewCurve(Tool.Parabola, StrokeKind.Parabola, 400f, 400f);
        vb.CurveAxis = CurveAxis.OpenUp;
        vb.SetParabolaVertex(400f, 400f);
        vb.SetParabolaThroughPoint(700f, 250f);
        Check("鎶涚墿绾匡細椤剁偣 (400,400)銆佺粡杩囩偣 (700,250)锛埪?.5锛?,
              Near(vb.CurvePointLocal(0).X, 400f, .5f) && Near(vb.CurvePointLocal(0).Y, 400f, .5f)
              && Near(vb.CurvePointLocal(1).X, 700f, .5f) && Near(vb.CurvePointLocal(1).Y, 250f, .5f),
              $"椤剁偣 ({vb.CurvePointLocal(0).X:F1},{vb.CurvePointLocal(0).Y:F1})"
              + $" 缁忚繃鐐?({vb.CurvePointLocal(1).X:F1},{vb.CurvePointLocal(1).Y:F1})");
        Check("鎶涚墿绾匡細鏈濆悜 = **閫夊嚭鏉ョ殑寮€鍙ｅ悜涓?*锛堜笉鏄粠涓ょ偣鎺ㄧ殑锛?,
              vb.EffectiveAxis == CurveAxis.OpenUp
              && Near(vb.ParabolaDirLocal().X, 0f, .01f) && Near(vb.ParabolaDirLocal().Y, -1f, .01f),
              $"{vb.EffectiveAxis} / {vb.ParabolaDirLocal()}");
        Check("鎶涚墿绾匡細p 鐢辩粡杩囩偣鍙嶈В = t虏/(2s) = 300虏/300 = 300锛埪?.5锛?,
              Near(vb.ParabolaPLocal(), 300f, .5f), $"p {vb.ParabolaPLocal():F1}");
        var vbT0 = vb.ParabolaPointAt(0f);
        var vbT1 = vb.ParabolaPointAt(1f);
        Check("鎶涚墿绾匡細t=0 钀藉湪椤剁偣銆乼=卤1 钀藉湪**閫氬緞绔偣**锛? 鎴戜滑缁欑殑閭ｄ釜缁忚繃鐐癸級涓婏紙卤0.5锛?,
              Near(vbT0.X, 400f, .5f) && Near(vbT0.Y, 400f, .5f)
              && Near(vbT1.X, 700f, .5f) && Near(vbT1.Y, 250f, .5f),
              $"t0 ({vbT0.X:F1},{vbT0.Y:F1})  t1 ({vbT1.X:F1},{vbT1.Y:F1}) 鏈熸湜 (400,400) / (700,250)");
        var vbBox = vb.CurveBoxLocal();
        // 绱ф = **"椤剁偣 鈫?缁忚繃鐐?閭ｄ釜鐭╁舰**锛堢収 InkClass锛氱敾鍒版嫋鍒扮殑閭ｄ釜鐐逛负姝級鈥斺€?        // 浜庢槸閭ｄ釜鐐?*姝ｅソ钀藉湪妗嗚涓?*锛氭í璺?卤300銆佹部杞?0鈥?50銆?        Check("鎶涚墿绾匡細绱ф = **鎷栧姩閭ｄ釜鐭╁舰**锛堢粡杩囩偣姝ｅソ钀藉湪妗嗚涓婏級锛埪?.5锛?,
              Near(vbBox.MinX, 100f, .5f) && Near(vbBox.MaxX, 700f, .5f)
              && Near(vbBox.MinY, 250f, .5f) && Near(vbBox.MaxY, 400f, .5f),
              $"妗?({vbBox.MinX:F1},{vbBox.MinY:F1})..({vbBox.MaxX:F1},{vbBox.MaxY:F1})"
              + " 鏈熸湜 (100,250)..(700,400)");
        var vbPad = vb.PaddedBounds;
        Check("鎶涚墿绾匡細鑴忓尯妗?= 绱ф 锛?鍗婄瑪瀹?锛?2锛埪?.05锛?,
              Near(vbPad.MinX, vbBox.MinX - vb.Width * .5f - 2f, .05f)
              && Near(vbPad.MaxY, 400f + vb.Width * .5f + 2f, .05f),
              $"({vbPad.MinX:F2},{vbPad.MinY:F2})..({vbPad.MaxX:F2},{vbPad.MaxY:F2})"
              + $" 绗斿 {vb.Width:F1}");

        // ---- 鍙屾洸绾匡細涓績 (1200,500)锛岀涓€姝?鎷栧嚭娓愯繎绾挎 (320,240)" ----
        //      **鎷栧埌鍝氨鏄摢**锛欰 = 320銆丅 = 240锛?026-09-20 鏀规垚涓嶅啀鎵撳鎶橈紝
        //      鍥犱负娓愯繎绾胯嚜宸卞氨鏄銆佷笉鍐嶉渶瑕?鐢诲埌 2a銆?b"閭ｇ鏀惧ぇ锛夈€?        //      绗簩姝ワ紙绗笁涓偣锛? "**鏇茬嚎瑕佺粡杩囩殑閭ｄ釜鐐?*"锛?1370,560)锛岀浉瀵逛腑蹇?`dx = 170銆乨y = 60`
        //      鈫?鏇茬嚎鐨?x 鏂瑰悜鍗婂 `a = 鈭?170虏 鈭?60虏路(4/3)虏) = 鈭?2500 = 150`銆?        //      y 鏂瑰悜鍗婇珮 `b = a 脳 B/A = 112.5`銆?*杩欎竴姝ユ笎杩戠嚎妗嗕竴涓瓧閮戒笉鍔?*锛堣繖娆℃敼鍔ㄧ殑鏍稿績锛夈€?        var hy = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1200f, 500f);
        hy.SetHyperbolaFromAsymptote(1200f, 500f, 1520f, 740f, minAxis);
        hy.SetHyperbolaThroughPoint(1370f, 560f);
        Check("鍙屾洸绾匡細绗竴姝?A = 320銆丅 = 240锛?*鎷栧埌鍝氨鏄摢**锛夛紙卤0.5锛?,
              Near(hy.HyperbolaALocal(), 320f, .5f) && Near(hy.HyperbolaBLocal(), 240f, .5f),
              $"A {hy.HyperbolaALocal():F1}  B {hy.HyperbolaBLocal():F1}");
        Check("鍙屾洸绾匡細娓愯繎绾挎枩鐜?= B/A = 240/320 = 0.75锛堝氨鏄?InkClass 鐨?|dy/dx|锛夛紙卤0.001锛?,
              Near(hy.HyperbolaBLocal() / hy.HyperbolaALocal(), 0.75f, .001f),
              $"{hy.HyperbolaBLocal() / hy.HyperbolaALocal():F4}锛堟湡鏈?0.7500锛?);
        // **绗簩姝ュ彧鍐欑涓変釜鐐?*锛氭洸绾跨殑鍗婅酱鐢卞畠鍙嶈В锛屾笎杩戠嚎妗嗭紙A / B锛?*鍘熷皝涓嶅姩**銆?        Check("鍙屾洸绾匡細绗簩姝ョ殑鐐瑰弽瑙ｅ嚭鏇茬嚎鐨勫崐杞达紙a = 150銆乥 = 112.5锛夛紝妗嗕笉鍔紙卤0.5锛?,
              Near(hy.HyperbolaCurveALocal(), 150f, .5f) && Near(hy.HyperbolaCurveBLocal(), 112.5f, .5f)
              && Near(hy.HyperbolaALocal(), 320f, .5f) && Near(hy.HyperbolaBLocal(), 240f, .5f),
              $"鏇茬嚎 a {hy.HyperbolaCurveALocal():F1}銆乥 {hy.HyperbolaCurveBLocal():F1}锛?
              + $"妗?A {hy.HyperbolaALocal():F1}銆丅 {hy.HyperbolaBLocal():F1}");
        var hyBox = hy.CurveBoxLocal();
        // 绱ф = **娓愯繎绾跨殑閭ｄ釜鐭╁舰**锛堢敤鎴?2026-09-20 瀹氾細"鎶婂弻鏇茬嚎闄愬埗鍦ㄦ笎杩戠嚎鐨勭煩褰㈡鍐?锛夈€?        // hy锛氫腑蹇?(1200,500)銆丄 = 320銆丅 = 240 鈫?妗?= 卤320 脳 卤240銆?        Check("鍙屾洸绾匡細绱ф = 卤A 脳 卤B锛堝氨鏄笎杩戠嚎妗嗭級锛埪?.5锛?,
              Near(hyBox.MinX, 1200f - 320f, .5f) && Near(hyBox.MaxX, 1200f + 320f, .5f)
              && Near(hyBox.MinY, 500f - 240f, .5f) && Near(hyBox.MaxY, 500f + 240f, .5f),
              $"妗?({hyBox.MinX:F1},{hyBox.MinY:F1})..({hyBox.MaxX:F1},{hyBox.MaxY:F1})"
              + $" 鏈熸湜 (880,260)..(1520,740)");
        var hyApex = hy.HyperbolaVertexLocal(1);
        Check("鍙屾洸绾匡細椤剁偣鍦?(涓績.x 锛?a, 涓績.y) = (1350, 500)锛埪?.5锛?,
              Near(hyApex.X, 1350f, .5f) && Near(hyApex.Y, 500f, .5f),
              $"({hyApex.X:F1},{hyApex.Y:F1}) 鏈熸湜 (1350,500)");
        // 涓ゆ敮涔嬮棿鐨?骞界伒绾挎"锛氳疆寤撴姌绾挎槸琚€愭鍒や氦娑堣垂鐨勶紙姗＄毊 / 濂楃储锛夛紝
        // 涓棿閭ｆ潯妯┛鐨勮繛绾夸細璁?姗＄毊浠庝袱鏀箣闂村垝杩?= 鏁存潯琚垹"銆?        var hyOutline = hy.ShapeOutline();
        float nearest = float.MaxValue;
        int breaks = 0;
        for (int i = 1; i < hyOutline.Count; i++)
        {
            if (Stroke.IsOutlineBreak(hyOutline[i - 1]))
            {
                breaks++;                 // 鎶瑪鏍囪锛堣 Stroke.OutlineBreak锛夛細涓€鏉℃帴鍦ㄥ畠鍚庨潰
                continue;
            }
            if (Stroke.IsOutlineBreak(hyOutline[i])) continue;      // 鍚屼笂涓€鏉＄殑灏惧反
            nearest = MathF.Min(nearest, DistPointSeg(new Vector2(1200f, 500f),
                                                      hyOutline[i - 1], hyOutline[i]));
        }
        Check("鍙屾洸绾匡細杞粨閲屾伆濂戒竴涓?*鎶瑪鏍囪**锛堜袱鏀垎寮€銆佷笉杩炵嚎锛?, breaks == 1, $"{breaks} 澶勬姮绗?);
        Check("鍙屾洸绾匡細杞粨閲?*娌℃湁妯┛涓ゆ敮涔嬮棿**鐨勫菇鐏电嚎娈碉紙涓績鍒颁换涓€娈?鈮?0.9a锛?,
              nearest >= 150f * 0.9f, $"鏈€杩戣窛绂?{nearest:F1}锛坅 = 150锛岃 鈮?135锛?);

        // **鏇茬嚎鏁存潯鏀跺湪娓愯繎绾挎閲?*锛堢敤鎴?2026-09-20 鐨勮瘔姹傦級锛?        // 娓愯繎绾挎 = 卤A 脳 卤B = 卤320 脳 卤240锛岃€屾洸绾?**鍑烘灏卞仠绗?*"锛堣 Stroke.HyperbolaTMaxOf锛夆€斺€?        // 鍏堝埌鍝潯杈瑰氨鍋滃湪鍝潯杈广€俬y 鐨?`2a = 300 < A = 320`锛屾墍浠ュ畠**鍏堟挒涓婇偅鏉?鏈€澶氱敾鍒?2a"鐨勪笂闄?*
        // 锛坄cosh t = 2`锛岃 HyperbolaDrawT锛夛紝妯悜鍋滃湪 300銆佺旱鍚戝仠鍦?`b鈭? 鈮?194.9`銆?        float hMaxX = 0f, hMaxY = 0f;
        foreach (var q in hyOutline)
        {
            if (Stroke.IsOutlineBreak(q)) continue;
            hMaxX = MathF.Max(hMaxX, MathF.Abs(q.X - 1200f));
            hMaxY = MathF.Max(hMaxY, MathF.Abs(q.Y - 500f));
        }
        Check("鍙屾洸绾匡細**鏁存潯鏇茬嚎鏀跺湪娓愯繎绾挎鍐?*锛坾x| 鈮?A銆亅y| 鈮?B锛夛紙卤0.5锛?,
              hMaxX <= 320f + .5f && hMaxY <= 240f + .5f,
              $"鏇茬嚎鏈€杩?({hMaxX:F1},{hMaxY:F1})锛屾 (卤320,卤240)");
        Check("鍙屾洸绾匡細妯悜**鐢诲埌鎷栧埌鐨勯偅涓偣涓烘**锛堟渶杩?x = 170锛岀収 InkClass锛夛紙卤0.5锛?,
              Near(hMaxX, 170f, .5f), $"{hMaxX:F1}锛堟湡鏈?170锛?);
        Check("鍙屾洸绾匡細鍙︿竴鏉¤酱鍙埌 b路sinh(t)锛堚増59.8锛岄偅涓偣鍦ㄩ偅鍎匡級锛埪?.5锛?,
              Near(hMaxY, 60f, .5f), $"{hMaxY:F1}锛堟湡鏈?鈮?0锛?);

        // ---- 姝ｅ鸡锛氳捣鐐?(300,1000)锛涙嫋鍑?**480 瀹?脳 100 楂?= 涓€涓懆鏈?+ 鎸箙** ----
        // 鈿?2026-09-20 绗崄鍏壒锛氭寮?/ 浣欏鸡鍥炲埌"**妗嗗灏辨槸涓€涓懆鏈?*"
        //锛堢敤鎴凤細"姝ｅ鸡鍜屼綑寮︾敤涓€涓懆鏈熺殑鍥?锛夆€斺€斿鍛ㄦ湡閭ｄ欢浜嬩氦缁?*娉㈡氮绾?*閭ｄ竴鏍?        //锛堟í鍚?= 瑕佺敾澶氶暱锛岃涓嬮潰 鈶?娈碉級銆?        //   A = 100锛堟寮︼細绾垫嫋 = 鎸箙锛夆啋 **T = 妗嗗 = 480** 鈫?鐢?1 涓懆鏈熴€?        var sn = NewCurve(Tool.Sine, StrokeKind.Sine, 300f, 1000f);
        sn.SetWaveBox(300f, 1000f, 780f, 900f, minAxis);
        Check("姝ｅ鸡锛氳捣鐐?= 鎸変笅鐐广€佺粓鐐?= 璧风偣锛?闀垮害, 楂樺害)锛埪?.5锛?,
              Near(sn.Points[0].X, 300f, .5f) && Near(sn.Points[0].Y, 1000f, .5f)
              && Near(sn.Points[1].X, 780f, .5f) && Near(sn.Points[1].Y, 900f, .5f),
              $"璧风偣 ({sn.Points[0].X:F1},{sn.Points[0].Y:F1})"
              + $" 缁堢偣 ({sn.Points[1].X:F1},{sn.Points[1].Y:F1})");
        Check("姝ｅ鸡锛氫袱涓畾涔夊厓绱狅紙璧风偣 锛?缁堢偣锛屾瀬鍊肩偣鏄幇鎺ㄧ殑锛?,
              sn.Points.Count == 2, $"{sn.Points.Count} 涓?);
        Check("姝ｅ鸡锛欰 = 100銆?*T = 妗嗗 = 480**銆佹濂界敾 1 涓懆鏈燂紙卤0.5锛?,
              Near(sn.WaveAmplitudeLocal(), 100f, .5f)
              && Near(sn.WavePeriodLocal(), 480f, .5f)
              && Near(sn.WaveLengthLocal(), 480f, .5f)
              && Near(sn.WaveCyclesLocal(), 1f, .01f),
              $"A {sn.WaveAmplitudeLocal():F1}  T {sn.WavePeriodLocal():F1}"
              + $"  闀?{sn.WaveLengthLocal():F1}  鍛ㄦ湡鏁?{sn.WaveCyclesLocal():F2}锛堟湡鏈?1.00锛?);
        var snQ = sn.WavePointAt(0.25f);
        var snH = sn.WavePointAt(0.5f);
        var snT = sn.WavePointAt(0.75f);
        Check("姝ｅ鸡锛歶=0 鍦ㄨ酱涓娿€乽=0.25 鍒板嘲銆乽=0.5 鍥炶酱銆乽=0.75 鍒拌胺锛埪?.5锛?,
              Near(sn.WavePointAt(0f).Y, 1000f, .5f) && Near(snQ.Y, 900f, .5f)
              && Near(snH.Y, 1000f, .5f) && Near(snT.Y, 1100f, .5f),
              $"0:{sn.WavePointAt(0f).Y:F1} 1/4:{snQ.Y:F1} 1/2:{snH.Y:F1} 3/4:{snT.Y:F1}");
        var snBox = sn.CurveBoxLocal();
        Check("姝ｅ鸡锛氱揣妗?= 涓€涓懆鏈熷 脳 卤A锛堝嘲鍜岃胺閮藉湪閲岄潰锛夛紙卤0.5锛?,
              Near(snBox.MinX, 300f, .5f) && Near(snBox.MaxX, 780f, .5f)
              && Near(snBox.MinY, 900f, .5f) && Near(snBox.MaxY, 1100f, .5f),
              $"妗?({snBox.MinX:F1},{snBox.MinY:F1})..({snBox.MaxX:F1},{snBox.MaxY:F1})");

        // ---- 浣欏鸡锛氳捣鐐?(300,1400)锛?*宄伴《**锛夛紱鎷栧嚭 480 瀹?脳 200 楂?= 涓€涓懆鏈?----
        // 绾靛悜鎷栫殑鏄?宄?鈫?璋?锛?2A锛堣繖鏄綑寮﹀拰姝ｅ鸡鍞竴鐨勪笉瀵圭О锛岃 WaveAmplitudeOf锛夛紝
        // 妯悜閭ｄ竴鎷栧悓鏍锋槸**涓€涓懆鏈?*銆?        var cs = NewCurve(Tool.Cosine, StrokeKind.Cosine, 300f, 1400f);
        cs.SetWaveBox(300f, 1400f, 780f, 1600f, minAxis);   // 绾垫嫋 = 宄扳啋璋?= 2A 鈫?A = 100
        Check("浣欏鸡锛氳捣鐐规槸**宄伴《**銆佺粓鐐瑰湪**璋?*閭ｄ竴渚э紙卤0.5锛?,
              Near(cs.Points[0].Y, 1400f, .5f) && Near(cs.Points[1].Y, 1600f, .5f)
              && Near(cs.WaveTroughLocal().X, 540f, .5f) && Near(cs.WaveTroughLocal().Y, 1600f, .5f),
              $"璧风偣 y {cs.Points[0].Y:F1}  缁堢偣 y {cs.Points[1].Y:F1}"
              + $"  璋?({cs.WaveTroughLocal().X:F1},{cs.WaveTroughLocal().Y:F1})"
              + "锛堟湡鏈?x=540 = 300 锛?鍗婁釜鍛ㄦ湡 240锛?);
        Check("浣欏鸡锛欰 = 100銆乀 = 妗嗗 = 480銆佹濂?1 涓懆鏈燂紙鍜屾寮﹀悓涓€鏉＄畻寮忥級锛埪?.5锛?,
              Near(cs.WaveAmplitudeLocal(), 100f, .5f) && Near(cs.WavePeriodLocal(), 480f, .5f)
              && Near(cs.WaveCyclesLocal(), 1f, .01f),
              $"A {cs.WaveAmplitudeLocal():F1}  T {cs.WavePeriodLocal():F1}"
              + $"  鍛ㄦ湡鏁?{cs.WaveCyclesLocal():F2}锛堟湡鏈?1.00锛?);
        Check("浣欏鸡锛歶=0 鍦ㄥ嘲椤躲€乽=0.25 鍒颁腑绾裤€乽=0.5 鍒拌胺搴曪紙卤0.5锛?,
              Near(cs.WavePointAt(0f).Y, 1400f, .5f) && Near(cs.WavePointAt(0.25f).Y, 1500f, .5f)
              && Near(cs.WavePointAt(0.5f).Y, 1600f, .5f),
              $"0:{cs.WavePointAt(0f).Y:F1} 1/4:{cs.WavePointAt(0.25f).Y:F1} 1/2:{cs.WavePointAt(0.5f).Y:F1}");
        var csBox = cs.CurveBoxLocal();
        Check("浣欏鸡锛氱揣妗?= 涓€涓懆鏈熷 脳 (璧风偣 鈫?璋峰簳 2A)锛埪?.5锛?,
              Near(csBox.MinX, 300f, .5f) && Near(csBox.MaxX, 780f, .5f)
              && Near(csBox.MinY, 1400f, .5f) && Near(csBox.MaxY, 1600f, .5f),
              $"妗?({csBox.MinX:F1},{csBox.MinY:F1})..({csBox.MaxX:F1},{csBox.MaxY:F1})");

        // ---- 娉㈡氮绾匡紙2026-09-20 绗崄鍏壒锛岀敤鎴凤細"杩樻湁涓€涓彟澶栫殑寰堝鍛ㄦ湡鐨勬尝娴殑寮﹀嚱鏁扮嚎"锛?---
        // 鍜屾寮︽槸**鍚屼竴鏉℃洸绾?*锛屽樊鍒彧鍦?*杩欎竴鎷栫浠€涔?*锛氭寮︾殑妗嗗 = 涓€涓懆鏈熴€?        // 娉㈡氮绾跨殑妗嗗 = **瑕佺敾澶氶暱**锛堝懆鏈熺敱鎸箙瀹氾細**`T = 1 脳 A`** 鈥斺€?鐢ㄦ埛 2026-09-20 瀹氱殑
        // "鎸箙 = 涓€涓懆鏈?锛岃 WavePeriodPerAmplitude锛夈€?        // 鎵€浠ヤ笅闈㈣繖鍑犳潯灏辨槸**"澶氬懆鏈?杩欎欢浜嬬殑姝ｉ潰鏂█**锛?        //   路 鎷栧緱瀹?鈫?鍛ㄦ湡鏁拌窡鐫€娑紝鑰?**T 鍜?A 涓€涓瓧涓嶅彉**锛堟尝褰笉浼氳鎷夊彉褰級锛?        //   路 鎷栧緱涓嶈冻涓€涓懆鏈?鈫?绱ф璺熺潃鏀讹紙宄拌繕娌″埌锛屼笉鑳芥拺鍒?卤A锛夈€?        var wv = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv.SetWaveBox(300f, 1000f, 600f, 900f, minAxis);          // 300 瀹姐€丄 = 100
        Check("娉㈡氮绾匡細A = 100銆?*T = 1脳A = 100**銆佺敾 300 瀹?鈫?3 涓暣鍛ㄦ湡锛埪?.5锛?,
              Near(wv.WaveAmplitudeLocal(), 100f, .5f)
              && Near(wv.WavePeriodLocal(), 100f, .5f)
              && Near(wv.WaveLengthLocal(), 300f, .5f)
              && Near(wv.WaveCyclesLocal(), 3f, .02f),
              $"A {wv.WaveAmplitudeLocal():F1}  T {wv.WavePeriodLocal():F1}"
              + $"  闀?{wv.WaveLengthLocal():F1}  鍛ㄦ湡鏁?{wv.WaveCyclesLocal():F2}锛堟湡鏈?3.00锛?);
        // **鎷栧涓€鍊?*锛氬懆鏈熸暟缈诲€嶏紝鑰屾尝褰竴涓瓧涓嶅彉锛?鐢诲闀跨敾澶氶暱"杩欎欢浜嬬殑姝ｉ潰鏂█锛夈€?        var wv2 = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv2.SetWaveBox(300f, 1000f, 900f, 900f, minAxis);        // 鍚屾牱 A = 100銆佸涓€鍊嶏紙600锛?        Check("娉㈡氮绾匡細**鍚屼竴涓珮搴︽嫋瀹戒竴鍊嶏紙300 鈫?600锛夆啋 鍛ㄦ湡鏁扮炕鍊嶏紙3.0 鈫?6.0锛?*锛?
              + "鑰?T 鍜?A 涓€涓瓧娌″彉锛堜笉鍙樺舰锛?,
              Near(wv2.WaveCyclesLocal(), 6f, .02f)
              && Near(wv2.WaveAmplitudeLocal(), 100f, .5f)
              && Near(wv2.WavePeriodLocal(), 100f, .5f),
              $"A {wv2.WaveAmplitudeLocal():F1}  T {wv2.WavePeriodLocal():F1}"
              + $"  鍛ㄦ湡鏁?{wv2.WaveCyclesLocal():F2}锛堟湡鏈?6.00锛?);
        Check("娉㈡氮绾匡細瀹戒竴鍊嶇殑绱ф = 闀垮害涔熺炕鍊嶃€?*涓婁笅涓ゆ潯绾胯繕鏄?卤A**锛堟尝褰㈡病琚媺鎵侊級",
              Near(wv2.CurveBoxLocal().MinX, 300f, .5f) && Near(wv2.CurveBoxLocal().MaxX, 900f, .5f)
              && Near(wv2.CurveBoxLocal().MinY, 900f, .5f) && Near(wv2.CurveBoxLocal().MaxY, 1100f, .5f),
              $"妗?({wv2.CurveBoxLocal().MinX:F1},{wv2.CurveBoxLocal().MinY:F1})"
              + $"..({wv2.CurveBoxLocal().MaxX:F1},{wv2.CurveBoxLocal().MaxY:F1})");
        // **鎷栧緱鐭?*锛堜笉瓒充竴涓懆鏈燂級涔熻鑳界敾锛氶偅鏃剁揣妗嗕笉璇ユ拺鍒?卤A锛堝嘲杩樻病鍒帮級
        var wv3 = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv3.SetWaveBox(300f, 1000f, 320f, 900f, minAxis);         // 闀?20銆丄 = 100 鈫?0.2 涓懆鏈?        Check("娉㈡氮绾匡細鎷栧緱**涓嶈冻涓€涓懆鏈?*鏃剁揣妗嗚窡鐫€鏀讹紙涓嶆拺鍒?卤A锛屽嘲鏍规湰杩樻病鍒帮級锛埪?.5锛?,
              Near(wv3.WaveCyclesLocal(), 0.2f, .01f)
              && Near(wv3.CurveBoxLocal().MinY, 1000f - 100f * MathF.Sin(MathF.Tau * 0.2f), .5f)
              && wv3.CurveBoxLocal().MinY > 900.5f,
              $"鍛ㄦ湡鏁?{wv3.WaveCyclesLocal():F3}  妗嗕笂娌?{wv3.CurveBoxLocal().MinY:F1}"
              + $"锛堟湡鏈?{1000f - 100f * MathF.Sin(MathF.Tau * 0.2f):F1}锛?
              + "婊″嘲鎵嶆槸 900 鈥斺€?杩欓噷宄拌繕娌″埌锛屾璺熺潃鏀讹級");
        // 涓ょ娉㈡氮**鍚屼竴涓鎷栧嚭鏉ョ殑涓滆タ涓嶄竴鏍?*锛堣繖鏄?鍒嗕袱鏍?鐨勬剰涔夋墍鍦級锛?        // 鎷?*鍚屼竴涓**锛堟寮﹂偅涓€鏉★細480 瀹?脳 100 楂橈級姣?鈥斺€?姝ｅ鸡鏄?*涓€涓懆鏈?*锛圱 = 480锛夈€?        // 娉㈡氮绾挎槸 **4.8 涓懆鏈?*锛圱 = 100 = A锛夈€?        var wv4 = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv4.SetWaveBox(300f, 1000f, 780f, 900f, minAxis);         // 鍜屼笂闈?sn **涓€妯′竴鏍风殑涓€鎷?*
        Check("姝ｅ鸡 / 娉㈡氮绾?*鍚屼竴涓鎷栧嚭鏉ヤ笉鏄悓涓€鏉?*锛歍 480锛堜竴涓懆鏈燂級vs T 100锛?.8 涓懆鏈燂級",
              Near(sn.WavePeriodLocal(), 480f, .5f) && Near(sn.WaveCyclesLocal(), 1f, .01f)
              && Near(wv4.WavePeriodLocal(), 100f, .5f) && Near(wv4.WaveCyclesLocal(), 4.8f, .02f),
              $"姝ｅ鸡 T {sn.WavePeriodLocal():F0}/鍛ㄦ湡鏁?{sn.WaveCyclesLocal():F2}銆?
              + $"娉㈡氮绾?T {wv4.WavePeriodLocal():F0}/鍛ㄦ湡鏁?{wv4.WaveCyclesLocal():F2}");

        // ---- 姝ｅ垏锛?026-09-20 绗崄浜旀壒锛岀敤鎴凤細"鍙互鐢绘鍒?锛?---
        // 鍜屾寮?/ 浣欏鸡鍚屾棌锛屼絾**涓€绗?*锛氭寜涓?= 鍘熺偣锛堣繖涓€鏀殑涓績锛屼篃鏄浘璞′笌 x 杞寸殑浜ょ偣锛夛紝
        // 鎷栧嚭鍘?= **浠ュ畠涓轰腑蹇冪殑妗?*鈥斺€旀í鍚?= 鍗婃敮闀匡紙涓ゆ潯娓愯繎绾挎濂借惤鍦ㄦ鐨勫乏鍙宠竟锛夈€?        // 绾靛悜 = 鍙鍗婇珮锛堟洸绾垮啿鍒伴偅鍎垮氨鎴柇锛夈€?        //
        // 鈿?涓ゆ潯鏈€瑕佺揣鐨勶細
        //   鈶?**瀹冧笉璇ヨ鎷栧彉褰?*锛氭í绔栧叡鐢ㄤ竴涓崟浣嶏紙`鍗婃敮闀?鈫?蟺/2`锛夛紝鎵€浠ユ鎷栨垚浠€涔堝舰鐘讹紝
        //      鐢诲嚭鏉ョ殑閮芥槸鍚屼竴涓鍒囷紙鍙槸鐪嬪緱瑙佺殑閮ㄥ垎澶氬皯涓嶅悓锛夛紱
        //   鈶?**鍙鍗婇珮鏈変釜涓嬮檺**锛坄TangentMinAspect = 3`锛夛細妯珫鏃㈢劧鍚屼竴涓昂搴︼紝
        //      "鐪嬪緱瑙佸楂?灏辩洿鎺ュ喅瀹?涓ゅご绂绘笎杩戠嚎杩樻湁澶氳繙" 鈥斺€?妗嗗お鐭椂鏇茬嚎鍙敾鍒颁竴鍗婂氨鎴柇锛?        //      鐪嬬潃鏄?*涓€鏉℃枩绾?*锛堢敤鎴?2026-09-20 涓婃墜灏辨槸杩欎釜鍗拌薄锛氫粬寰楁晠鎰忔嫋寰楀張楂樺張绐勬墠鍍忥級銆?        //      鎵€浠ュ紩鎿庢妸楂樺害椤跺埌"璐寸潃娓愯繎绾?鐨勯偅涓渶灏忔瘮渚嬶紝**闅忔墜涓€鎷栧氨鏄鏈殑鏍峰瓙**銆?        var tg = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg.SetTangentBox(300f, 2400f, 420f, 2540f, minAxis);      // 鍗婃敮闀?120銆佹嫋鐨勫崐楂樺彧鏈?140
        Check("姝ｅ垏锛氫袱涓畾涔夊厓绱狅紙鍘熺偣 锛?鎷栧嚭鍘婚偅涓锛夛紝鏄?*涓€绗?*鍥惧舰",
              tg.Points.Count == 2 && ShapeStepCount(Tool.Tangent) == 1,
              $"{tg.Points.Count} 涓帶鍒剁偣銆亄ShapeStepCount(Tool.Tangent)} 绗?
              + "锛堟湡鏈?2 涓?/ 1 绗旓級");
        Check("姝ｅ垏锛氬師鐐?= 鎸変笅鐐癸紱鍗婃敮闀?120锛涘彲瑙嗗崐楂?*琚《鍒颁笅闄?360 = 3 脳 120**锛堟嫋鐨勬槸 140锛夛紙卤0.5锛?,
              Near(tg.TangentOriginLocal().X, 300f, .5f) && Near(tg.TangentOriginLocal().Y, 2400f, .5f)
              && Near(tg.TangentHalfSpanLocal(), 120f, .5f)
              && Near(tg.TangentHalfHeightLocal(), 3f * 120f, .5f),
              $"鍘熺偣 ({tg.TangentOriginLocal().X:F1},{tg.TangentOriginLocal().Y:F1})"
              + $"  鍗婃敮闀?{tg.TangentHalfSpanLocal():F1}  鍙鍗婇珮 {tg.TangentHalfHeightLocal():F1}"
              + $"锛堟嫋鐨?{MathF.Abs(tg.TangentEndLocal().Y - tg.TangentOriginLocal().Y):F1}锛?);
        // 鍗曚綅 = 鍗婃敮闀?梅 蟺/2 鈥斺€?**杩欎竴鏉″氨鏄?妯珫鍚屼竴涓昂搴?鐨勫畾涔?*锛堜笉鍙樺舰鐨勬牴锛?        Check($"姝ｅ垏锛氭í绔?*鍏辩敤涓€涓崟浣?*锛堝崐鏀暱 120 梅 蟺/2 鈮?{120f / (MathF.PI * .5f):F1}锛夛紙卤0.5锛?,
              Near(tg.TangentUnitLocal(), 120f / (MathF.PI * 0.5f), .5f),
              $"{tg.TangentUnitLocal():F2}锛堟湡鏈?{120f / (MathF.PI * 0.5f):F2}锛?);
        Check("姝ｅ垏锛毼?0 杩囧師鐐广€佄?蟺/4 闄?*涓€涓崟浣?*锛坱an 45掳 = 1锛夛紙卤0.5锛?,
              Near(tg.TangentPointAtTheta(0f).X, 300f, .5f)
              && Near(tg.TangentPointAtTheta(0f).Y, 2400f, .5f)
              && Near(tg.TangentPointAtTheta(MathF.PI * .25f).Y, 2400f - tg.TangentUnitLocal(), .5f),
              $"胃=0 鈫?({tg.TangentPointAtTheta(0f).X:F1},{tg.TangentPointAtTheta(0f).Y:F1})"
              + $"  胃=蟺/4 鈫?y {tg.TangentPointAtTheta(MathF.PI * .25f).Y:F1}"
              + $"锛堟湡鏈?{2400f - tg.TangentUnitLocal():F1}锛?);
        // 鎴柇锛氭洸绾?*鍐插埌妗嗙殑涓婁笅杈瑰氨鏀剁瑪**锛堟澶栫殑涓嶇敾锛夛紝鎵€浠ラ鏈袱鐐圭殑 y 姝ｅソ鏄?卤鍗婇珮
        Check("姝ｅ垏锛氫竴鏀殑**棣栨湯涓ょ偣姝ｅソ钀藉湪妗嗙殑涓婁笅杈?*锛堝啿鍑烘灏辨埅鏂級锛埪?.5锛?,
              Near(tg.TangentTracedPointAt(0f).Y, 2400f + 360f, .5f)
              && Near(tg.TangentTracedPointAt(1f).Y, 2400f - 360f, .5f)
              && tg.TangentTracedPointAt(0f).X < 300f && tg.TangentTracedPointAt(1f).X > 300f,
              $"绔ご ({tg.TangentTracedPointAt(0f).X:F1},{tg.TangentTracedPointAt(0f).Y:F1})"
              + $" 鍜?({tg.TangentTracedPointAt(1f).X:F1},{tg.TangentTracedPointAt(1f).Y:F1})");
        // **"璐寸潃娓愯繎绾?鐨勬闈㈠垽鎹?*锛氭洸绾夸袱澶村仠鍦ㄧ娓愯繎绾?13% 鐨勫湴鏂癸紙胃 璧板埌 蟺/2 鐨?87%锛?        // 鈥斺€?杩欎竴鏉″氨鏄敤鎴烽偅鍙?寰堢獎寰堥珮鐨勬椂鍊欐墠璐寸潃娓愯繎绾?瑕佸仛鎴愰粯璁よ涓虹殑閭ｄ欢浜嬨€?        float tgGap = 1f - MathF.Abs(300f - tg.TangentTracedPointAt(1f).X) / tg.TangentHalfSpanLocal();
        Check("姝ｅ垏锛氫袱澶?*璐寸潃娓愯繎绾?*锛堝彧宸?13% 鐨勫崐鏀暱锛浳?璧板埌 蟺/2 鐨?87%锛夛紙卤0.02锛?,
              Near(tgGap, 0.133f, .02f)
              && Near(tg.TangentThetaMaxLocal() / (MathF.PI * .5f), 0.867f, .02f),
              $"绂绘笎杩戠嚎杩樺樊 {tgGap * 100f:F1}%锛堟湡鏈?13.3锛夛紝"
              + $"胃 璧板埌 蟺/2 鐨?{tg.TangentThetaMaxLocal() / (MathF.PI * .5f) * 100f:F1}%");
        // 涓ゆ潯娓愯繎绾匡細x = 卤鍗婃敮闀匡紙**姝ｅソ钀藉湪妗嗙殑宸﹀彸杈?*锛夈€佷笂涓嬩粠 鈭掑崐楂?鍒?锛嬪崐楂?        var (aFrom, aTo) = tg.TangentAsymptoteLocal(1);
        Check("姝ｅ垏锛氫袱鏉℃笎杩戠嚎鍦?x = 鍘熺偣 卤 鍗婃敮闀匡紙灏辫惤鍦ㄦ鐨勫乏鍙宠竟涓婏級锛埪?.5锛?,
              Near(tg.TangentAsymptoteLocal(-1).From.X, 180f, .5f)
              && Near(aFrom.X, 420f, .5f) && Near(aFrom.Y, 2400f - 360f, .5f)
              && Near(aTo.Y, 2400f + 360f, .5f),
              $"宸?x {tg.TangentAsymptoteLocal(-1).From.X:F1}锛堟湡鏈?180锛?
              + $"  鍙?x {aFrom.X:F1}锛堟湡鏈?420锛? 涓婁笅 {aFrom.Y:F1}..{aTo.Y:F1}锛堟湡鏈?2040..2760锛?);
        // 绱ф = **閭ｄ釜妗嗘湰韬?*锛堜笉鐢ㄩ噰鏍锋眰鏋佸€硷細鏇茬嚎鐨勮寖鍥村氨鏄?卤鍗婃敮闀?/ 卤鍗婇珮锛?        var tgBox = tg.CurveBoxLocal();
        Check("姝ｅ垏锛氱揣妗?= 閭ｄ釜妗嗘湰韬?(180,2040)..(420,2760)锛埪?.5锛?,
              Near(tgBox.MinX, 180f, .5f) && Near(tgBox.MaxX, 420f, .5f)
              && Near(tgBox.MinY, 2400f - 360f, .5f) && Near(tgBox.MaxY, 2400f + 360f, .5f),
              $"妗?({tgBox.MinX:F1},{tgBox.MinY:F1})..({tgBox.MaxX:F1},{tgBox.MaxY:F1})");
        // == 涓嶅彉褰細**鍚屼竴涓瘮渚嬫斁澶т竴鍊?* 鈫?鏇茬嚎閫愮偣涔熸斁澶т竴鍊嶏紙褰㈢姸涓€涓瓧涓嶅彉锛?==
        // 杩欐潯姣?鐪嬬潃鍍?纭緱澶氾細瀹冭瘉鐨勬槸妯珫**鍚屼竴涓?*鍗曚綅锛屼篃灏辨槸璇?        // "妗嗘嫋鎴愪粈涔堝舰鐘堕兘涓€鏍凤紝鐢诲嚭鏉ョ殑杩樻槸閭ｄ釜姝ｅ垏"锛堝皯浜嗚繖鏉★紝"鎷栧灏辨媺骞?浼氭簻杩囧幓锛夈€?        var tg2 = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg2.SetTangentBox(300f, 2400f, 300f + 240f, 2400f + 280f, minAxis);   // 姣斾緥鐩稿悓銆佹斁澶т竴鍊?        bool tgScaleOk = true; string tgScaleNote = "";
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            var pa = tg.TangentTracedPointAt(t);
            var pb = tg2.TangentTracedPointAt(t);
            if (!Near(pb.X - 300f, (pa.X - 300f) * 2f, .5f)
                || !Near(pb.Y - 2400f, (pa.Y - 2400f) * 2f, .5f))
            {
                tgScaleOk = false;
                tgScaleNote = $"t={t:F2}锛?鐩稿鍘熺偣) ({pa.X - 300f:F1},{pa.Y - 2400f:F1})"
                            + $" 鈫?({pb.X - 300f:F1},{pb.Y - 2400f:F1})锛堟湡鏈?脳2锛?;
                break;
            }
        }
        Check("姝ｅ垏锛氭**鎸夊悓涓€姣斾緥鏀惧ぇ涓€鍊?* 鈫?鏇茬嚎閫愮偣鏀惧ぇ涓€鍊嶏紙鎷栦笉鍙樺舰锛?, tgScaleOk,
              tgScaleOk ? "5 涓噰鏍风偣鍏ㄦ槸 脳2锛堝舰鐘朵竴涓瓧娌″彉锛? : tgScaleNote);
        // 鎷栧緱**鍙堝鍙堢煯**锛?80 脳 70锛夛細楂樺害鐓ф牱琚《鍒?3 鍊嶄笅闄愶紝浜庢槸鐢诲嚭鏉ョ殑杩樻槸
        // "璐寸潃娓愯繎绾跨殑閭ｄ竴鏀?鈥斺€?*杩欏氨鏄繖鏉′笅闄愬瓨鍦ㄧ殑鐞嗙敱**锛堜笉鐒跺畠鏄竴鏉℃枩绾匡級銆?        // 娉ㄦ剰 胃 鐨勪笂闄愬拰鍗婃敮闀?*鏃犲叧**锛坄tan 胃max = 3路蟺/2`锛夛紝鎵€浠ュ畠鍜?tg 璧板埌鍚屼竴涓?胃銆?        var tg3 = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg3.SetTangentBox(300f, 2400f, 300f + 480f, 2400f + 70f, minAxis);   // 鍙堝鍙堢煯
        Check("姝ｅ垏锛?*鍙堝鍙堢煯**鐨勬涔熻椤跺埌鍚屼竴鏉′笅闄愶紙胃 涓婇檺鍜?tg 涓€鏍枫€佷笉璧版垚鏂滅嚎锛?,
              Near(tg3.CurveBoxLocal().MaxX - tg3.TangentOriginLocal().X, 480f, .5f)
              && Near(tg3.TangentHalfHeightLocal(), 3f * 480f, .5f)
              && Near(tg3.TangentThetaMaxLocal(), tg.TangentThetaMaxLocal(), .01f)
              && tg3.TangentThetaMaxLocal() < MathF.PI * .5f,
              $"鍗婃敮闀?480銆佹嫋鐨勫崐楂?70 鈫?瀹為檯鍗婇珮 {tg3.TangentHalfHeightLocal():F0}銆?
              + $"胃 涓婇檺 {tg3.TangentThetaMaxLocal():F3}锛坱g 鏄?{tg.TangentThetaMaxLocal():F3}锛?);
        // 鍙嶈繃鏉ヨ锛?*鎷栧緱姣斾笅闄愭洿楂樺氨鐓т綘鐨勬潵**锛堣秺楂樿鍘嬪埌瓒婅繎锛夈€?        var tg4 = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg4.SetTangentBox(300f, 2400f, 300f + 120f, 2400f + 700f, minAxis);   // 鍗婃敮闀?120銆佸崐楂?700
        Check("姝ｅ垏锛氭嫋寰?*姣斾笅闄愭洿楂?*灏辩収浣犵殑鏉ワ紙鍗婇珮 700 > 360 鈫?胃 涓婇檺鏇村ぇ銆佽创寰楁洿杩戯級",
              Near(tg4.TangentHalfHeightLocal(), 700f, .5f)
              && tg4.TangentThetaMaxLocal() > tg.TangentThetaMaxLocal() + 0.05f,
              $"鍗婃敮闀?120銆佹嫋鐨勫崐楂?700 鈫?胃 涓婇檺 {tg4.TangentThetaMaxLocal():F3} rad"
              + $"锛堜笅闄愰偅涓€妗ｆ槸 {tg.TangentThetaMaxLocal():F3}锛屜€/2 鏄?{MathF.PI * .5f:F3}锛?);

        // ---- 鍦嗘煴 / 鍦嗛敟锛?026-09-20 绗簲鎵癸細鐓?InkClass 鐨?case 6/7锛?---
        // 涓や釜鎺у埗鐐瑰氨鏄?*澶栨帴鐭╁舰**锛堟嫋鍒板摢灏辨槸鍝級锛屾き鍦嗙敱瀹冩淳鐢燂細
        // `rx = 瀹?2 = 200`銆乣ry = rx / 2.646 鈮?75.59`銆?        // 鍦嗗績浠庣煩褰笂涓嬭竟**寰€閲岀缉涓€涓?ry**锛屼簬鏄き鍦嗘濂戒笌鐭╁舰鐩稿垏锛堣寖鍥村ソ绠椼€佺揣妗嗕笉婕忥級銆?        var cy = NewCurve(Tool.Cylinder, StrokeKind.Cylinder, 200f, 500f);
        cy.SetSolidBox(200f, 500f, 600f, 900f);
        var (ccx, cTop, cBot, crx, cry) = cy.SolidEllipsesLocal();
        Check("鍦嗘煴锛氬鎺ョ煩褰?= 鎷栧嚭鏉ョ殑閭ｄ釜锛?00,500)..(600,900)锛埪?.5锛?,
              Near(cy.SolidRectLocal().X0, 200f, .5f) && Near(cy.SolidRectLocal().Y1, 900f, .5f),
              $"({cy.SolidRectLocal().X0:F0},{cy.SolidRectLocal().Y0:F0})"
              + $"..({cy.SolidRectLocal().X1:F0},{cy.SolidRectLocal().Y1:F0})");
        // 鏈熸湜鍊?*璇诲紩鎿庨偅涓父閲?*锛屼笉鍦ㄨ繖閲屽啀鎶勪竴閬?2.646鈥斺€斿啓姝荤殑甯告暟浼氶殢瀹冩敼鑰岄潤榛樺け鏁?        //锛堜粨搴撻噷涓鸿繖绫讳簨鏍借繃锛岃 璁″垝-鍥惧舰宸ュ叿.md 鐨勬暀璁偅鍑犳潯锛夈€?        Check($"鍦嗘煴锛歳x = 200銆乺y = rx 脳 鎵佺巼 鈮?{200f * Stroke.SolidEllipseRatio:F1}锛堢収浠栫殑甯告暟锛夛紙卤0.5锛?,
              Near(crx, 200f, .5f) && Near(cry, 200f * Stroke.SolidEllipseRatio, .5f),
              $"rx {crx:F1}  ry {cry:F1}锛堟湡鏈?200 / {200f * Stroke.SolidEllipseRatio:F1}锛?);
        Check("鍦嗘煴锛氫笂涓嬩袱涓渾蹇冧粠鐭╁舰杈?*寰€閲岀缉涓€涓?ry**锛堢浉鍒囷紝涓嶅啋鍑哄幓锛夛紙卤0.5锛?,
              Near(ccx, 400f, .5f) && Near(cTop, 500f + cry, .5f) && Near(cBot, 900f - cry, .5f),
              $"cx {ccx:F1}  涓婂渾蹇?y {cTop:F1}锛堟湡鏈?{500f + cry:F1}锛?
              + $"  涓嬪渾蹇?y {cBot:F1}锛堟湡鏈?{900f - cry:F1}锛?);
        Check("鍦嗘煴锛氱揣妗?= 澶栨帴鐭╁舰鏈韩锛堟帶鍒剁偣澶栨帴灏卞锛屾き鍦嗕笉浼氬啋鍑哄幓锛夛紙卤0.5锛?,
              Near(cy.Bounds.MinX, 200f, .5f) && Near(cy.Bounds.MaxX, 600f, .5f)
              && Near(cy.Bounds.MinY, 500f, .5f) && Near(cy.Bounds.MaxY, 900f, .5f),
              $"妗?({cy.Bounds.MinX:F0},{cy.Bounds.MinY:F0})..({cy.Bounds.MaxX:F0},{cy.Bounds.MaxY:F0})");
        // 杞粨鐨?*鍒嗘缁撴瀯**锛氬簳闈竴鍦?/ 椤堕潰涓€鍦?/ 涓ゆ潯姣嶇嚎锛?026-09-20 琛ヤ簡姣嶇嚎鈥斺€?        // 姗＄毊鐨勭矖绛涙寜杩欎唤杞粨鍒?澶熶笉澶熷緱鐫€"锛屽皯浜嗘瘝绾垮氨"鏄庢槑鐢荤潃绾垮嵈鎿︿笉鎺?锛夈€?        var cyParts = Stroke.SplitOutlineParts(cy.ShapeOutline());
        Check("鍦嗘煴锛氳疆寤撳垎鎴?4 娈碉紙搴曞湀 / 椤跺湀 / 涓ゆ潯姣嶇嚎锛夛紝娈典笌娈典箣闂撮潬鎶瑪鏍囪闅斿紑",
              cyParts.Count == 4 && cyParts[1].Count > 8 && cyParts[2].Count == 2 && cyParts[3].Count == 2,
              $"{cyParts.Count} 娈碉紝鐐规暟 {string.Join("/", cyParts.ConvertAll(p => p.Count))}"
              + "锛堟湡鏈?4 娈碉細鍦?鍦?2/2锛?);

        var co = NewCurve(Tool.Cone, StrokeKind.Cone, 200f, 500f);
        co.SetSolidBox(200f, 500f, 600f, 900f);
        Check("鍦嗛敟锛氶《鐐?= 澶栨帴鐭╁舰鐨?*涓婅竟涓偣** (400, 500)锛堢収 InkClass锛夛紙卤0.5锛?,
              Near(co.ConeApexLocal().X, 400f, .5f) && Near(co.ConeApexLocal().Y, 500f, .5f),
              $"({co.ConeApexLocal().X:F0},{co.ConeApexLocal().Y:F0}) 鏈熸湜 (400,500)");

        // ---- 鍦嗗彴锛?026-09-20 绗崄涓夋壒锛岀敤鎴凤細"鍐嶅姞涓€涓渾鍙?锛?---
        // 瀹冨拰鍦嗘煴 / 鍦嗛敟**鍏辩敤鍚屼竴浠界敾娉?*锛坄Stroke.SolidPieces`锛氫笂搴曟暣鍦?锛?涓嬪簳涓嬪崐鍦?锛?        // 涓ゆ潯姣嶇嚎锛岃鎸＄殑鏄笅搴曚笂鍗婂湀锛夛紝宸埆鍙湪**涓婂簳灏忎竴鍦?*銆傛墍浠ヨ繖涓€鑺傚彧閽夐偅涓€鐐癸細
        // 涓婂簳鐨勬瘮渚?/ 鐩稿垏銆佹瘝绾?*寰€涓棿鏀?*銆佽疆寤撹繕鏄?4 娈点€?        var cf = NewCurve(Tool.ConeFrustum, StrokeKind.ConeFrustum, 200f, 500f);
        cf.SetSolidBox(200f, 500f, 600f, 900f);
        var (fTop, frx, fry) = cf.ConeFrustumTopLocal();
        float fk = ShapeSpec.FrustumTopScale;
        Check($"鍦嗗彴锛氫笂搴?= 涓嬪簳**鍚屽舰缂?{fk:F2}**锛堜袱涓崐杞撮兘涔橈紝鍜屾１鍙板悓涓€涓暟锛夛紙卤0.5锛?,
              Near(frx, crx * fk, .5f) && Near(fry, cry * fk, .5f),
              $"涓婂簳 ({frx:F1}, {fry:F1})锛堟湡鏈?({crx * fk:F1}, {cry * fk:F1})锛?);
        Check("鍦嗗彴锛氫笂搴曚笌鐭╁舰涓婅竟**鐩稿垏**锛堝渾蹇冨線閲岀缉鐨勬槸 ry脳姣斾緥锛屼笉鏄?ry锛夛紙卤0.5锛?,
              Near(fTop.Y - fry, 500f, .5f),
              $"涓婂簳涓婃部 {fTop.Y - fry:F1}锛堟湡鏈?500 = 鐭╁舰涓婅竟锛?);
        var cfParts = Stroke.SplitOutlineParts(cf.ShapeOutline());
        Check("鍦嗗彴锛氳疆寤撳垎 4 娈碉紙涓嬪簳鍦?/ 涓婂簳鍦?/ 涓ゆ潯姣嶇嚎锛夆€斺€斿拰鍦嗘煴鍚屼竴涓粨鏋?,
              cfParts.Count == 4 && cfParts[1].Count > 8 && cfParts[2].Count == 2 && cfParts[3].Count == 2,
              $"{cfParts.Count} 娈碉紝鐐规暟 {string.Join("/", cfParts.ConvertAll(p => p.Count))}"
              + "锛堟湡鏈?4 娈碉細鍦?鍦?2/2锛?);
        // 姣嶇嚎**涓嶆槸绔栫殑**锛堟槸绔栫殑灏辨垚鍦嗘煴浜嗭級锛涜€屼笖涓婄**姝ｅソ钀藉湪涓婂簳宸﹀彸绔偣**銆?        Check("鍦嗗彴锛氫袱鏉℃瘝绾?*寰€涓棿鏀?*锛屼笂绔惤鍦ㄤ笂搴曞乏鍙崇鐐癸紙涓嶆槸绔栫洿鐨勶級",
              cfParts.Count == 4
              && Near(cfParts[2][0].X, 200f, .5f) && Near(cfParts[2][1].X, fTop.X - frx, .5f)
              && Near(cfParts[3][0].X, 600f, .5f) && Near(cfParts[3][1].X, fTop.X + frx, .5f)
              && cfParts[2][1].X > cfParts[2][0].X && cfParts[3][1].X < cfParts[3][0].X,
              cfParts.Count == 4
                  ? $"宸?{cfParts[2][0].X:F0}鈫抺cfParts[2][1].X:F0}銆佸彸 {cfParts[3][0].X:F0}鈫抺cfParts[3][1].X:F0}"
                    + $"锛堟湡鏈?200鈫抺fTop.X - frx:F0}銆?00鈫抺fTop.X + frx:F0}锛?
                  : "锛堣疆寤撴病鍒嗘垚 4 娈碉級");
        Check("鍦嗗彴锛氱揣妗?= 澶栨帴鐭╁舰鏈韩锛堜笂搴曠缉灏忓悗涓嶄細鍐掑嚭鍘伙級锛埪?.5锛?,
              Near(cf.Bounds.MinX, 200f, .5f) && Near(cf.Bounds.MaxX, 600f, .5f)
              && Near(cf.Bounds.MinY, 500f, .5f) && Near(cf.Bounds.MaxY, 900f, .5f),
              $"妗?({cf.Bounds.MinX:F0},{cf.Bounds.MinY:F0})..({cf.Bounds.MaxX:F0},{cf.Bounds.MaxY:F0})");

        // ---- 鐞冿紙2026-09-20 绗崄鍥涙壒锛岀敤鎴凤細"鍦ㄥ姞鍏ョ悆"锛?---
        // 瀹冩湁涓変欢浜嬪拰鍒殑绔嬩綋涓嶅悓锛岄兘寰楅拤浣忥紙涓嶇劧"鐞?浼氶€€鍖栨垚"鍦?锛夛細
        //   鈶?鎶曞奖姘歌繙鏄?*鍦?*锛堝彇鐭╁舰閲屽唴鍒囩殑鍦嗭紝鎷栭暱浜嗕篃涓嶈鐢绘垚鎵佺悆锛夛紱
        //   鈶?蹇呴』鐢?*璧ら亾**閭ｄ釜妞渾锛堝惁鍒欏拰銆屽渾銆嶉偅涓€鏍奸暱寰椾竴鏍凤紝鐪嬩笉鍑烘槸涓悆锛夛紱
        //   鈶?璧ら亾鐨勮繎渚э紙涓嬪崐鍦堬級瀹炵嚎銆佽繙渚э紙涓婂崐鍦堬級铏氱嚎銆?        // 鐗规剰鐢?*闀挎柟褰?* 400脳200 鏉ユ嫋锛堜笉鏄鏂瑰舰锛夛細杩欐牱"鍐呭垏姝ｅ渾"杩欐潯鎵嶄細鐪熺殑琚獙鍒般€?        var sp = NewCurve(Tool.Sphere, StrokeKind.Sphere, 200f, 500f);
        sp.SetSolidBox(200f, 500f, 600f, 700f);                      // 400脳200
        var (spC, spR) = sp.SphereLocal();
        var (spEqC, spEqRx, spEqRy) = sp.SphereEquatorLocal();
        Check("鐞冿細鎶曞奖鏄?*鍐呭垏姝ｅ渾**鈥斺€斿崐寰?= min(鍗婂, 鍗婇珮) = 100锛堜笉鏄?200锛夈€佸渾蹇?= 鐭╁舰涓績锛埪?.5锛?,
              Near(spR, 100f, .5f) && Near(spC.X, 400f, .5f) && Near(spC.Y, 600f, .5f),
              $"鍗婂緞 {spR:F1}锛堟湡鏈?100锛夈€佸渾蹇?({spC.X:F0},{spC.Y:F0})锛堟湡鏈?(400,600)锛?);
        Check("鐞冿細璧ら亾妞渾 鈥斺€?闀垮崐杞?= 鍗婂緞銆佸渾蹇?= 鐞冨績銆佺煭鍗婅酱 = 鍗婂緞 脳 閭ｄ釜淇锛埪?.5锛?,
              Near(spEqRx, spR, .5f) && Near(spEqRy, spR * Stroke.SolidEllipseRatio, .5f)
              && Near(spEqC.X, spC.X, .5f) && Near(spEqC.Y, spC.Y, .5f),
              $"璧ら亾 ({spEqRx:F1}, {spEqRy:F1})锛堟湡鏈?({spR:F1}, {spR * Stroke.SolidEllipseRatio:F1})锛夛紝"
              + $"鍦嗗績 ({spEqC.X:F0},{spEqC.Y:F0})");
        Check("鐞冿細璧ら亾**涓嶆槸姝ｅ渾**锛堢煭鍗婅酱 < 闀垮崐杞?鈥斺€?閭ｄ釜銆屼粠涓婂線涓嬬湅銆嶇殑淇锛?,
              spEqRy < spEqRx - 1f, $"鐭崐杞?{spEqRy:F1} vs 闀垮崐杞?{spEqRx:F1}");
        var spParts = Stroke.SplitOutlineParts(sp.ShapeOutline());
        Check("鐞冿細杞粨鍒?2 娈碉紙澶栧湀 / 璧ら亾鍦堬級鈥斺€旇丹閬撶殑涓婁笅涓ゅ崐閮借鍒楄繘鏉ワ紙姗＄毊寰楀寰楃潃锛?,
              spParts.Count == 2 && spParts[0].Count > 8 && spParts[1].Count > 8,
              $"{spParts.Count} 娈碉紝鐐规暟 {string.Join("/", spParts.ConvertAll(p => p.Count))}"
              + "锛堟湡鏈?2 娈碉細鍦?鍦堬級");
        Check("鐞冿細绱ф = **閭ｄ釜鍦嗙殑澶栨帴鏂瑰舰**锛堝渾蹇?卤 鍗婂緞锛夛紝涓嶆槸鎷栧嚭鏉ョ殑 400脳200 鐭╁舰锛埪?.5锛?,
              Near(sp.Bounds.MinX, 300f, .5f) && Near(sp.Bounds.MaxX, 500f, .5f)
              && Near(sp.Bounds.MinY, 500f, .5f) && Near(sp.Bounds.MaxY, 700f, .5f),
              $"妗?({sp.Bounds.MinX:F0},{sp.Bounds.MinY:F0})..({sp.Bounds.MaxX:F0},{sp.Bounds.MaxY:F0})"
              + "锛堟湡鏈?(300,500)..(500,700)锛?);
        // 澧ㄨ抗妗嗭紙鑴忓尯/鍛戒腑鐢級蹇呴』璺熺潃**鐪熷疄鍑犱綍**璧帮紝涓嶈兘鏄鎺ョ煩褰⑩€斺€?        // 杩欏拰妫辨煴閭ｆ鐨?澶栨帴鐭╁舰涓婇潰婕忎竴鍧?鏄悓涓€绫绘瘺鐥咃紝鍙槸鏂瑰悜鐩稿弽锛堣繖閲屾槸铏氳儢锛夈€?        Check("鐞冿細澧ㄨ抗妗嗕篃鎸夌湡瀹炲嚑浣曠畻锛? 鍦嗙殑澶栨帴鏂瑰舰 锛?鍗婁釜绗斿锛屼笉鏄煩褰?锛?鍗婁釜绗斿锛?,
              Near(sp.WorldInkBounds.MaxY - sp.WorldInkBounds.MinY,
                   (500f - 300f) + sp.Width, 1.5f),
              $"澧ㄨ抗妗嗛珮 {sp.WorldInkBounds.MaxY - sp.WorldInkBounds.MinY:F1}"
              + $"锛堟湡鏈?{(500f - 300f) + sp.Width:F1} = 200 + 绗斿 {sp.Width:F1}锛?);

        // ---- 闀挎柟浣?/ 鍥涢潰浣擄紙2026-09-20 绗簲鎵癸細鐓?InkClass 鐨?case 9/26锛?*涓ょ瑪**锛?---
        // 闀挎柟浣擄細绗?1 绗旀闈㈢煩褰?(200,500)..(600,900)锛岀 2 绗旀嫋鍒?y=650 鈫?娣卞害 d = |500 鈭?650| = 150銆?        var cu = NewCurve(Tool.Cuboid, StrokeKind.Cuboid, 200f, 500f);
        cu.SetCuboidFront(200f, 500f, 600f, 900f);
        cu.SetCuboidDepth(500f, 650f);
        Check("闀挎柟浣擄細娣卞害 = |姝ｉ潰涓婅竟 鈭?鎸囬拡 y| = 150锛堢収浠栫殑鍙ｅ緞锛夛紙卤0.5锛?,
              Near(cu.CuboidDepthLocal(), 150f, .5f), $"{cu.CuboidDepthLocal():F1}锛堟湡鏈?150锛?);
        Check("闀挎柟浣擄細绗?3 涓帶鍒剁偣 = **鑳岄潰鍙充笅瑙?* (600+150, 900鈭?50) = (750, 750)锛埪?.5锛?,
              Near(cu.CurvePointLocal(2).X, 750f, .5f) && Near(cu.CurvePointLocal(2).Y, 750f, .5f),
              $"({cu.CurvePointLocal(2).X:F0},{cu.CurvePointLocal(2).Y:F0}) 鏈熸湜 (750,750)");
        Check("闀挎柟浣擄細涓変釜鎺у埗鐐癸紙姝ｉ潰涓よ 锛?鑳岄潰鍙充笅瑙掞級鈥斺€?澶氱瑪鐘舵€佹満瑕佸畠鍑戦綈鎵嶇畻鐢诲畬",
              cu.Points.Count == 3 && Stroke.MinCurvePoints(StrokeKind.Cuboid) == 3,
              $"鎺у埗鐐?{cu.Points.Count} 涓€丮inCurvePoints = {Stroke.MinCurvePoints(StrokeKind.Cuboid)}");
        Check("闀挎柟浣擄細绱ф = 姝ｉ潰 鈭?鑳岄潰锛堟帶鍒剁偣澶栨帴姝ｅソ灏辨槸鐢诲嚭鏉ョ殑鑼冨洿锛夛紙卤0.5锛?,
              Near(cu.Bounds.MinX, 200f, .5f) && Near(cu.Bounds.MaxX, 750f, .5f)
              && Near(cu.Bounds.MinY, 350f, .5f) && Near(cu.Bounds.MaxY, 900f, .5f),
              $"妗?({cu.Bounds.MinX:F0},{cu.Bounds.MinY:F0})..({cu.Bounds.MaxX:F0},{cu.Bounds.MaxY:F0})"
              + " 鏈熸湜 (200,350)..(750,900)");

        // 鍥涢潰浣擄細绗?1 绗斿鎺ョ煩褰?(200,500)..(600,900) 鈫?搴曢潰涓夎褰紙搴曡竟鍦ㄤ笅銆侀《鐐瑰眳涓湪涓婏級锛?        // 绗?2 绗旀嫋鍒?(300, 200) = 椤剁偣銆?        var te = NewCurve(Tool.Tetrahedron, StrokeKind.Tetrahedron, 200f, 500f);
        te.SetTetraBase(200f, 500f, 600f, 900f);
        te.SetTetraApex(300f, 200f);
        Check("鍥涢潰浣擄細搴曢潰涓夎褰?= 搴曞乏(200,900) / 搴曞彸(600,900) / 搴曢《(400,500)锛堢収浠栵級锛埪?.5锛?,
              Near(te.CurvePointLocal(0).X, 200f, .5f) && Near(te.CurvePointLocal(0).Y, 900f, .5f)
              && Near(te.CurvePointLocal(1).X, 600f, .5f) && Near(te.CurvePointLocal(1).Y, 900f, .5f)
              && Near(te.CurvePointLocal(2).X, 400f, .5f) && Near(te.CurvePointLocal(2).Y, 500f, .5f),
              $"({te.CurvePointLocal(0).X:F0},{te.CurvePointLocal(0).Y:F0}) / "
              + $"({te.CurvePointLocal(1).X:F0},{te.CurvePointLocal(1).Y:F0}) / "
              + $"({te.CurvePointLocal(2).X:F0},{te.CurvePointLocal(2).Y:F0})");
        Check("鍥涢潰浣擄細椤剁偣 = 绗?2 绗旀嫋鍒扮殑鍦版柟 (300, 200)锛埪?.5锛?,
              Near(te.TetraApexLocal().X, 300f, .5f) && Near(te.TetraApexLocal().Y, 200f, .5f),
              $"({te.TetraApexLocal().X:F0},{te.TetraApexLocal().Y:F0}) 鏈熸湜 (300,200)");
        Check("鍥涢潰浣擄細鍥涗釜鎺у埗鐐?锛?杞粨 = 鍏潯妫憋紙鍚勬涔嬮棿鎶瑪锛屼笉杩炲嚭鍋囩嚎锛?,
              te.Points.Count == 4 && Stroke.MinCurvePoints(StrokeKind.Tetrahedron) == 4
              && te.ShapeOutline().Count == 6 * 2 + 5,
              $"鎺у埗鐐?{te.Points.Count} 涓紱杞粨 {te.ShapeOutline().Count} 鐐?
              + $"锛? 娈?脳 2 鐐?锛?5 涓姮绗?= 17锛?);

        // ================= 鈶?鎵嬫焺锛氬洓绉嶆洸绾胯蛋**甯歌鎿嶄綔**锛?026-09-20 绗簲鎵癸級========
        // 鐢ㄦ埛锛?鎶涚墿绾裤€佸弻鏇茬嚎銆佹寮︺€佷綑寮︼紝鎶婄壒娈婄偣鐮嶆帀锛涢€氶€氭寜甯歌鎿嶄綔 鈥斺€?        //       缁欐搷浣滄焺鍜屾棆杞紝鍜屾甯哥殑涓€鏍枫€?
        Span<ShapeHandle> hs = stackalloc ShapeHandle[5];
        int nPb = SelectionHandles.ShapeHandlesOf(vb, hs);
        int nHy = SelectionHandles.ShapeHandlesOf(hy, hs);
        int nSn = SelectionHandles.ShapeHandlesOf(sn, hs);
        int nCs = SelectionHandles.ShapeHandlesOf(cs, hs);
        Check("鎵嬫焺锛氬洓绉嶆洸绾?*涓€涓壒娈婄偣閮戒笉缁?*锛堝拰鐭╁舰 / 绔嬩綋鍥惧舰涓€鏍凤紝璧伴€氱敤妗嗭級",
              nPb == 0 && nHy == 0 && nSn == 0 && nCs == 0,
              $"鎶涚墿绾?{nPb}銆佸弻鏇茬嚎 {nHy}銆佹寮?{nSn}銆佷綑寮?{nCs}锛堥兘璇ユ槸 0锛?);
        Check("鎵嬫焺锛氬洓绉嶆洸绾?*閮界粰鏃嬭浆鏌?*锛堬紳甯歌鎿嶄綔锛氱缉鏀?锛?鏃嬭浆 锛?骞崇Щ锛?,
              SelectionHandles.RotateHandleVisible(vb) && SelectionHandles.RotateHandleVisible(hy)
              && SelectionHandles.RotateHandleVisible(sn) && SelectionHandles.RotateHandleVisible(cs),
              "鎶涚墿绾?/ 鍙屾洸绾?/ 姝ｅ鸡 / 浣欏鸡 閮芥湁鏃嬭浆鏌?);
        Check("鎵嬫焺锛氭洸绾夸粛鐒舵槸**鍥惧舰**锛堥€変腑 / 绉诲姩 / 瀛樻。閮借蛋鍥惧舰閭ｄ竴濂楋級",
              Stroke.IsShapeKind(vb.Kind) && Stroke.IsShapeKind(hy.Kind)
              && Stroke.IsShapeKind(sn.Kind) && Stroke.IsShapeKind(cs.Kind),
              "鍥涚鏇茬嚎閮藉湪 IsShapeKind 閲?);

        // **閫氱敤鍏墜鏌勭湡鐨勭粰鍒颁簡**锛堢敤鎴?2026-09-20 闂?娌℃湁鐗规畩鐐圭殑鍥惧舰鈥︹€﹂偅鍏釜鏄笉鏄簲璇ラ兘缁?锛夈€?        //
        // 淇殑鏄竴澶?*涓ゅご绌?*锛歄verlay 鍜?HitTest 鍘熸潵閮藉垽"**鏄笉鏄浘褰?*"锛坄ShapeEditable`锛夛紝
        // 鑰屽洓绉嶆洸绾?/ 鍥涗釜绔嬩綋鍥惧舰**鏄浘褰絾娌℃湁鐗规畩鐐?* 鈥斺€?浜庢槸鏃㈢敾涓嶅嚭鐗规畩鎵嬫焺銆?        // 鍙堟嬁涓嶅埌閫氱敤鍏墜鏌勶紝**鐢讳笉鍑烘潵涔熺偣涓嶄腑**锛堢湡鏈轰笂灏卞彧鑳芥暣浣撴嫋锛屾媺涓嶅姩澶у皬锛夈€?        // 鍒ゆ嵁鎹㈡垚"**鏈夋病鏈夌壒娈婃墜鏌?*"涔嬪悗锛屽畠浠拰鐭╁舰璧板悓涓€鏉¤矾銆?        //
        // 杩欓噷瀵瑰叓涓璞″悇鐐逛竴娆?妗嗙殑鍙充笅瑙?锛?*鐐瑰緱涓?*鎵嶇畻缁欎簡锛堢敾鐨勯偅涓€鍗婇潬鍑哄浘鐪嬶紝
        // 鎷栫殑閭ｄ竴鍗婄敱涓嬮潰 鈶?楠屸€斺€旈偅杈硅繕瑕佹眰妗?*鐪熺殑琚媺澶?*锛屼笉鐒?鏁翠綋鎸竴涓?涔熻兘楠楄繃鍘伙級銆?        {
            var noSpecial = new (Stroke st, string name)[]
            {
                (vb, "鎶涚墿绾?), (hy, "鍙屾洸绾?), (sn, "姝ｅ鸡"), (cs, "浣欏鸡"),
                (cy, "鍦嗘煴"), (co, "鍦嗛敟"), (cf, "鍦嗗彴"), (sp, "鐞?),
                (cu, "闀挎柟浣?), (te, "鍥涢潰浣?),
            };
            int hitOk = 0;
            var missed = new List<string>();
            foreach (var (st, nm) in noSpecial)
            {
                var one = new[] { st };
                var f2 = SelectionHandles.FrameOf(one);
                var c2 = SelectionHandles.CanvasPosition(SelHandle.BottomRight, f2, DpiScale);
                if (SelectionHandles.HitTest(c2.X, c2.Y, one, f2, DpiScale) == SelHandle.BottomRight)
                    hitOk++;
                else missed.Add(nm);
            }
            Check("鎵嬫焺锛氬洓绉嶆洸绾?锛?鍏釜绔嬩綋鍥惧舰**閮界偣寰椾腑閫氱敤妗嗙殑瑙?*锛堬紳鍏釜缂╂斁鏌勭湡鐨勭粰浜嗭級",
                  hitOk == noSpecial.Length,
                  $"鐐瑰緱涓?{hitOk}/{noSpecial.Length}"
                  + (missed.Count > 0 ? $"锛岀偣涓嶄腑锛歿string.Join("/", missed)}" : ""));
        }

        // ================= 鈶?缂栬緫锛氭洸绾胯蛋**閫氱敤妗?*锛堝彧鍔ㄥ彉鎹紝涓嶅姩鍑犱綍锛?================
        // 鏇茬嚎涓嶅啀鏈?鎷栬繖涓偣鍙敼閭ｄ釜閲?鐨勫叆鍙ｂ€斺€旀ā鍨嬪眰閭ｅ嚑涓嚱鏁帮紙SetParabola鈥?/
        // SetHyperbola鈥?/ SetWave鈥︼級鍙湪**鐢荤殑鏃跺€?*鐢ㄤ竴娆★紙瑙?搂19銆伮?2锛夈€?        // 杩欓噷閽変綇閫氱敤妗嗛偅鏉¤矾锛氭嫋瑙掑彧鏀?`Transform`锛?*灞€閮ㄥ嚑浣曚竴涓偣閮戒笉鍔?*锛?        // 鏈濆悜 / p 杩欎簺"鏇茬嚎鑷繁鐨勫弬鏁?涔熶竴涓瓧涓嶅姩锛堣繖姝ｆ槸"绾垮涓嶅彉"閭ｆ潯璺殑鍓嶆彁锛?        // 瑙?Stroke.KeepsWidth锛夈€?        Doc.Clear();
        Doc.AddStroke(vb);
        Doc.ClearHistory();
        Doc.SelectOnly(new[] { vb });
        var pbP0 = new Vector2(vb.Points[0].X, vb.Points[0].Y);
        var pbP1 = new Vector2(vb.Points[1].X, vb.Points[1].Y);
        float pbPBefore = vb.ParabolaPLocal();
        var pbAxisBefore = vb.EffectiveAxis;
        var vbFrame = SelectionHandles.FrameOf(Doc.Selected);
        var vbCorner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, vbFrame, DpiScale);
        float vbW0 = vbFrame.CanvasAabb.MaxX - vbFrame.CanvasAabb.MinX;
        float vbH0 = vbFrame.CanvasAabb.MaxY - vbFrame.CanvasAabb.MinY;
        // 鍏堢‘璁?鎸夌殑鏄?*鎵嬫焺**锛屼笉鏄鍐呯殑绌虹櫧"鈥斺€斾笉纭鐨勮瘽锛屾寜涓嬩細琚綋鎴?鏁翠綋鎷栧姩"锛?        // 涓嬮潰閭ｆ潯 `!Transform.IsIdentity` 鐓ф牱鎴愮珛锛堟暣浣撴尓涓€涓嬩篃鏀瑰彉鎹級锛?*鏂█绛変簬鐧藉啓**锛?        // 杩欐鏄繖涓€鏉″師鏉ユ紡鎺夌殑閭ｅ崐锛堢敤鎴?2026-09-20 闂嚭鏉ョ殑閭ｄ欢浜嬶級銆?        Check("鏇茬嚎鎷栭€氱敤妗嗙殑瑙掞細鎸変笅鍘诲懡涓?*鍙充笅瑙掓墜鏌?*锛堜笉鏄鍐呯┖鐧?鈫?涓嶆槸鏁翠綋鎷栧姩锛?,
              SelectionHandles.HitTest(vbCorner.X, vbCorner.Y, Doc.Selected, vbFrame, DpiScale)
                  == SelHandle.BottomRight,
              $"{SelectionHandles.HitTest(vbCorner.X, vbCorner.Y, Doc.Selected, vbFrame, DpiScale)}"
              + "锛堟湡鏈?BottomRight锛?);
        bool vbTook = SelectionGestureForTest(vbCorner.X, vbCorner.Y);
        UpdateSelectionGestureForTest(vbCorner.X + 200f, vbCorner.Y + 120f);
        SettleFrames(40);
        EndSelectionGestureForTest();
        SettleFrames(60);
        Check("鏇茬嚎鎷栭€氱敤妗嗙殑瑙掞細**鍙敼 Transform锛屽嚑浣曠偣涓€涓兘娌″姩**",
              vbTook && !vb.Transform.IsIdentity
              && vb.Points[0].X == pbP0.X && vb.Points[0].Y == pbP0.Y
              && vb.Points[1].X == pbP1.X && vb.Points[1].Y == pbP1.Y,
              $"鎺ヤ綇={vbTook}锛屽彉鎹?{(vb.Transform.IsIdentity ? "鍗曚綅锛堟病鍔級" : "鍙樹簡")}锛?
              + $"涓や釜鎺у埗鐐?{(vb.Points[0].X == pbP0.X && vb.Points[0].Y == pbP0.Y ? "鍘熸牱" : "琚敼浜嗭紒")}");
        // **妗嗙湡鐨勮鎷夊ぇ浜?*鎵嶇畻"鍏釜缂╂斁鏌勮兘鐢?锛氬線鍙充笅瑙掓嫋 200脳120锛?        // 妗嗙殑瀹介珮璇ヨ窡鐫€闀库€斺€?鏁翠綋鎸竴涓?鏀圭殑鏄綅缃€佸楂樹竴鐐逛笉鍙橈紝杩欎竴鏉¤兘鎶婂畠绛涙帀銆?        var vbFrameAfter = SelectionHandles.FrameOf(Doc.Selected);
        float vbW1 = vbFrameAfter.CanvasAabb.MaxX - vbFrameAfter.CanvasAabb.MinX;
        float vbH1 = vbFrameAfter.CanvasAabb.MaxY - vbFrameAfter.CanvasAabb.MinY;
        Check("鏇茬嚎鎷栭€氱敤妗嗙殑瑙掞細妗?*鐪熺殑琚媺澶т簡**锛堜笉鏄暣浣撴尓浜嗕竴涓嬶級",
              vbW1 > vbW0 + 40f && vbH1 > vbH0 + 20f,
              $"妗?{vbW0:F0}脳{vbH0:F0} 鈫?{vbW1:F0}脳{vbH1:F0}");
        Check("鏇茬嚎鎷栭€氱敤妗嗙殑瑙掞細**鏈濆悜 涓?p 閮戒笉鍙楀奖鍝?*锛堝畠浠槸鏇茬嚎鑷繁鐨勫弬鏁帮級",
              vb.EffectiveAxis == pbAxisBefore && Near(vb.ParabolaPLocal(), pbPBefore, .01f),
              $"{vb.EffectiveAxis}锛宲 {vb.ParabolaPLocal():F1}锛堟嫋涔嬪墠 {pbPBefore:F1}锛?);
        Doc.SelectOnly(Array.Empty<Stroke>());

        // 鈶 **鍥涜竟涓偣涔熻鑳芥媺浼?*锛堢敤鎴?2026-09-20锛?閭ｅ叓涓偣鍑虹幇浜嗭紝浣嗘槸鎴戠湅鐫€鍙湁瀵硅绾?        //     鑳芥嫋鍔ㄦ斁缂╋紵鎴戣寰楀乏鍙虫媺浼镐篃鍙互缁?锛夈€?        //
        // 鏍瑰洜锛歚Top / Bottom / Left / Right` 杩欏洓涓悕瀛楁槸**涓ょ敤**鐨勨€斺€斿妞渾 / 鍧愭爣绯?/ 鏁拌酱
        // 瀹冧滑鏄?瀹氫箟鍏冪礌"锛屽娌℃湁瀹氫箟鍏冪礌鎵嬫焺鐨勫璞″畠浠氨鏄€氱敤妗嗙殑**鍥涜竟涓偣缂╂斁鏌?*銆?        // 鎵嬪娍鍒嗘祦閭ｉ噷鍘熸潵鏃犳潯浠舵妸瀹冧滑鎶规垚 `None`锛屼簬鏄偅绫诲璞?鍥涜鑳芥媺銆佸洓杈逛腑鐐瑰彧鑳芥暣浣撴嫋"銆?        // 杩欓噷鎷?*鍙宠竟涓偣**锛氬璇ュ彉澶с€?*楂樹竴涓偣涓嶈鍙?*锛堟暣浣撴嫋鍔ㄦ槸瀹介珮閮戒笉鍙橈紝
        // 绛夋瘮缂╂斁鏄楂樹竴璧峰彉鈥斺€斾袱澶撮兘鑳芥妸"鍏跺疄娌″湪缂╂斁"绛涘嚭鏉ワ級銆?        Doc.SelectOnly(new[] { vb });        // 鈶?灏惧反涓婃妸閫夋嫨娓呯┖浜嗭紝妗嗚浠?閫変腑瀹?閲嶆柊绠?        var vbF2 = SelectionHandles.FrameOf(Doc.Selected);
        float vbW2a = vbF2.CanvasAabb.MaxX - vbF2.CanvasAabb.MinX;
        float vbH2a = vbF2.CanvasAabb.MaxY - vbF2.CanvasAabb.MinY;
        var vbMid = SelectionHandles.CanvasPosition(SelHandle.Right, vbF2, DpiScale);
        Check("鏇茬嚎鎷栭€氱敤妗嗭細**鍙宠竟涓偣**鎸変笅鍘诲懡涓殑鏄缉鏀炬焺锛堜笉鏄鍐呯┖鐧斤級",
              SelectionHandles.HitTest(vbMid.X, vbMid.Y, Doc.Selected, vbF2, DpiScale) == SelHandle.Right,
              $"{SelectionHandles.HitTest(vbMid.X, vbMid.Y, Doc.Selected, vbF2, DpiScale)}锛堟湡鏈?Right锛?);
        bool vbTookMid = SelectionGestureForTest(vbMid.X, vbMid.Y);
        UpdateSelectionGestureForTest(vbMid.X + 200f, vbMid.Y);
        SettleFrames(40);
        EndSelectionGestureForTest();
        SettleFrames(60);
        var vbF3 = SelectionHandles.FrameOf(Doc.Selected);
        float vbW2b = vbF3.CanvasAabb.MaxX - vbF3.CanvasAabb.MinX;
        float vbH2b = vbF3.CanvasAabb.MaxY - vbF3.CanvasAabb.MinY;
        Check("鏇茬嚎鎷栭€氱敤妗嗙殑**鍙宠竟涓偣**锛氭í鍚戞媺闀匡紙瀹藉彉澶с€侀珮鍩烘湰涓嶅彉锛?,
              vbTookMid && MathF.Abs(vbW2b - (vbW2a + 200f)) < 6f && MathF.Abs(vbH2b - vbH2a) < 2f,
              $"鎺ヤ綇={vbTookMid}锛屾 {vbW2a:F0}脳{vbH2a:F0} 鈫?{vbW2b:F0}脳{vbH2b:F0}"
              + $"锛堟湡鏈?瀹?{(vbW2a + 200f):F0}銆侀珮 {vbH2a:F0}锛?);
        // 鍒ゆ嵁鑷唇锛?*鏈?*瀹氫箟鍏冪礌鎵嬫焺鐨勶紙鐩寸嚎锛変负鐪熴€?*娌℃湁**鐨勶紙鏇茬嚎 / 绔嬩綋鍥惧舰 / 鐭╁舰锛変负鍋囥€?        // 涓夊璋冪敤鐐癸紙Overlay 鐢绘墜鏌勩€丠itTest 鍛戒腑銆佹墜鍔垮垎娴佽涓嶈鍥涜竟涓偣锛夐棶鐨勯兘鏄畠锛?        // 鎵€浠ュ畠閿欎簡灏辨槸涓夊涓€璧烽敊鈥斺€斾竴鏉℃柇瑷€鐩綇涓夌鎴愬垎銆?        var vbLine = new Stroke { Tool = Tool.Line, Kind = StrokeKind.Line };
        vbLine.AddPoint(0f, 0f, 1f, 0);
        vbLine.AddPoint(10f, 0f, 1f, 0);
        var vbRect = new Stroke { Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle };
        vbRect.AddPoint(0f, 0f, 1f, 0);
        vbRect.AddPoint(10f, 10f, 1f, 0);
        Check("鍒ゆ嵁鑷唇锛欻asShapeHandles 鈥斺€?鐩寸嚎 true锛屾洸绾?/ 绔嬩綋鍥惧舰 / 鐭╁舰 false",
              SelectionHandles.HasShapeHandles(vbLine)
              && !SelectionHandles.HasShapeHandles(vb)
              && !SelectionHandles.HasShapeHandles(cy)
              && !SelectionHandles.HasShapeHandles(cu)
              && !SelectionHandles.HasShapeHandles(vbRect),
              "鐩寸嚎璇?true锛涘洓绉嶆洸绾?/ 绔嬩綋鍥惧舰 / 鐭╁舰閮借 false");
        Doc.SelectOnly(Array.Empty<Stroke>());

        // 鈶?鏈濆悜锛?*閫夊嚭鏉ョ殑**锛堜笉鍐嶉潬鎷栧姩瑙掑害鎺級
        //
        // 杩欐槸 2026-09-20 杩欎竴鐗堢殑鏍稿績鏀瑰姩锛氭柟鍚戜粠"鎷栧嚭鏉ョ殑"鍙樻垚"閫夊嚭鏉ョ殑"锛?        // 浜庢槸"鏂瑰悜"鍜?澶у皬"涓や釜鍙傛暟褰诲簳鍒嗗紑銆佸悇绠″悇鐨勨€斺€旂敤鎴风殑鍘熻瘽鏄?        // "寮€鍙ｇ殑 4 绉嶈繕瑕佸悓鏃跺吋椤惧ぇ灏忥紝杩欎袱涓弬鏁板緢涓嶅ソ璋冩暣"銆?        Doc.Clear();
        var pb2 = NewCurve(Tool.Parabola, StrokeKind.Parabola, 500f, 500f);
        pb2.CurveAxis = CurveAxis.OpenUp;                        // 閫夛細寮€鍙ｅ悜涓?        pb2.SetParabolaVertex(500f, 500f);
        pb2.SetParabolaThroughPoint(500f + 240f, 500f - 120f);   // s = 120銆乼 = 240 鈫?p = 240
        Doc.AddStroke(pb2);
        Doc.ClearHistory();
        Doc.SelectOnly(new[] { pb2 });
        Check("鏈濆悜锛氶€夈€屽紑鍙ｅ悜涓娿€嶁啋 鏂瑰悜 = (0,鈭?)锛沺 鐢辩粡杩囩偣鍙嶈В = 240锛埪?锛?,
              NearDir(pb2.ParabolaDirLocal(), new Vector2(0f, -1f)) && Near(pb2.ParabolaPLocal(), 240f, 1f),
              $"{pb2.ParabolaDirLocal()}锛宲 {pb2.ParabolaPLocal():F0}");

        // 鍥涗釜鏈濆悜鍚勬潵涓€娆★細鏂瑰悜瀵瑰緱涓婏紝鑰屼笖 **p 涓€鐐逛笉鍙?*锛堟崲鏈濆悜鍙浆鏂瑰悜銆佷笉鏀瑰舰鐘垛€斺€?        // 瀹炵幇涓婃槸鎶?缁忚繃鐐?鐨?(s, t) 鍒嗛噺璺熺潃鏂板熀閲嶇粍锛岃 Stroke.SetParabolaAxis锛夈€?        var axisCases = new (CurveAxis axis, Vector2 dir)[]
        {
            (CurveAxis.OpenUp, new Vector2(0f, -1f)),
            (CurveAxis.OpenDown, new Vector2(0f, 1f)),
            (CurveAxis.OpenRight, new Vector2(1f, 0f)),
            (CurveAxis.OpenLeft, new Vector2(-1f, 0f)),
        };
        bool axisOk = true, pKept = true;
        foreach (var (ax, dir) in axisCases)
        {
            pb2.SetParabolaAxis(ax);
            if (!NearDir(pb2.ParabolaDirLocal(), dir)) axisOk = false;
            if (!Near(pb2.ParabolaPLocal(), 240f, .5f)) pKept = false;
        }
        Check("鏈濆悜锛氬洓涓柟鍚戝垏涓€鍦?鈥斺€?鏂瑰悜閮藉寰椾笂锛?*p 濮嬬粓鏄?240**锛堟崲鏈濆悜鏇茬嚎涓嶇槳锛?,
              axisOk && pKept, $"鏈€鍚?{pb2.ParabolaDirLocal()}锛宲 {pb2.ParabolaPLocal():F1}");
        // 鍧忓€硷紙姣斿璇诲埌鍒殑鏃忕殑鍊硷級瑕佽褰掍竴鎴?寮€鍙ｅ悜涓?锛屼笉鑳藉甫鐫€瀹冨幓绠楀嚑浣曘€?        // **鍦ㄧ嫭绔嬪璞′笂璇?*锛氱洿鎺ョ粰 pb2 鍐欎釜鍧忓瓧娈典細鎶婂畠鐨勫嚑浣曚篃甯︽
        //锛堝瓧娈佃"寮€鍙ｅ悜涓?銆佺偣浣嶅嵈鏄寜鏃ф湞鍚戞憜鐨勶級锛屽悗闈㈠嚑鏉℃柇瑷€鍏ㄨ窡鐫€鍑洪敊銆?        var badAxis = NewCurve(Tool.Parabola, StrokeKind.Parabola, 2000f, 700f);
        badAxis.CurveAxis = CurveAxis.TransverseY;
        badAxis.SetParabolaVertex(2000f, 700f);
        Check("鏈濆悜锛氱粰鍒板埆鐨勬棌鐨勫€?鈫?褰掍竴鎴?*寮€鍙ｅ悜涓?*锛堝潖瀛楄妭涓嶈甯﹁繘鍑犱綍锛?,
              badAxis.EffectiveAxis == CurveAxis.OpenUp, $"{badAxis.EffectiveAxis}");
        pb2.SetParabolaAxis(CurveAxis.OpenUp);                   // 杞洖鍚戜笂锛屼笅闈㈡帴鐫€鐢?        Check("鏈濆悜锛氳浆鍥炲悜涓婁箣鍚庝粛鐒?p = 240锛埪?.5锛?,
              NearDir(pb2.ParabolaDirLocal(), new Vector2(0f, -1f)) && Near(pb2.ParabolaPLocal(), 240f, .5f),
              $"{pb2.ParabolaDirLocal()}锛宲 {pb2.ParabolaPLocal():F1}");

        // 鏇茬嚎鍙傛暟鐢?*鐢荤殑閭ｄ袱绗?*瀹氾紝缂栬緫鏃朵笉鍐嶆湁"鎷栬繖涓偣"鐨勫叆鍙ｏ紙瑙?鈶?/ 鈶ｏ級銆?        // 杩欓噷鐢ㄤ竴涓?*鐙珛瀵硅薄**鐩存帴璋冩ā鍨嬪眰閭ｄ竴浠界畻寮忥紝楠?缁忚繃鐐?鈫?p 鍙嶈В"杩欐潯鍙ｅ緞娌″彉
        //锛堢敾鐨勬椂鍊欑敤瀹冿紝瑙?搂19锛涙嫋鎵嬫焺閭ｆ潯璺凡缁忓垹鎺夛級鈥斺€斾笉纰?pb2锛?        // 鍏嶅緱鎶婁笅闈?p 杩樻槸 240"閭ｅ嚑鏉℃柇瑷€甯︽銆?        var pbParam = NewCurve(Tool.Parabola, StrokeKind.Parabola, 500f, 500f);
        pbParam.CurveAxis = CurveAxis.OpenUp;
        pbParam.SetParabolaVertex(500f, 500f);
        pbParam.SetParabolaThroughPoint(500f + 300f, 500f - 240f);   // s = 240銆乼 = 300
        Check("鍙傛暟锛氱粡杩囩偣 (300, 鈭?40) 鈫?p = t虏/2s = 187.5锛埪?锛?,
              Near(pbParam.ParabolaPLocal(), 300f * 300f / 480f, 2f),
              $"p {pbParam.ParabolaPLocal():F1}锛堟湡鏈?{300f * 300f / 480f:F1}锛?);
        Check("鍙傛暟锛氭崲缁忚繃鐐?*涓嶅姩鏈濆悜**锛堟湞鍚戞槸閫夊嚭鏉ョ殑锛?,
              NearDir(pbParam.ParabolaDirLocal(), new Vector2(0f, -1f)), $"{pbParam.ParabolaDirLocal()}");

        // 鎹㈡湞鍚戣繖浠朵簨锛?026-09-20 娣卞**浠庢搷浣滄潯鎸埌浜嗗浘褰㈤潰鏉?*锛堢敤鎴凤細"閫変腑妗嗙殑鎶涚墿绾挎寜閽?        // 鍔熻兘鍙栨秷鍝︼紝鎴戜笉鎵撶畻浠庤繖涓浆鎶涚墿绾垮紑鍙?锛夛細鐜板湪鏉′笂**娌℃湁銆屽紑鍙ｆ柟鍚戙€嶈繖涓€鏍?*锛?        // 鎹㈡湞鍚戣蛋鐨勬槸 `Engine.CycleParabolaAxis`锛堢敾涔嬪墠瀹氾紝瑙?--shapebandtest 閲岄偅涓ゆ潯锛夈€?        // 鈿?鏁板瓧 10 = 涔濇牸 锛?**2026-09-22 鍔犵殑绗崄鏍笺€屽浘搴撱€?*锛堣 `SelBarButton` 鍜?        //   `BarButtonCount` 鐨勬敞閲婏級銆傝繖鏉℃棭鍏堝啓姝?9锛屾槸閭ｅぉ涔嬪悗**娌¤窡鐫€鏀?*鐨勯仐鐣欌€斺€?        //   姝ｅソ鍙堟槸"鍐欐甯搁噺浼氶殢鍔熻兘绉讳綅闈欓粯澶辨晥"閭ｄ釜鍧戯紙鑷褰撳満绾紝鎵嶆病婕忚繃鍘伙級銆?        Check("鎿嶄綔鏉★細娌℃湁銆屽紑鍙ｆ柟鍚戙€嶉偅涓€鏍硷紙涔濇牸 锛?鍥惧簱 = 10锛?,
              SelectionHandles.BarButtonCount == 10,
              $"鏍煎瓙鏁?{SelectionHandles.BarButtonCount}");
        // 鎸蛋鐨勫彧鏄?鍏ュ彛"锛岃兘鍔涙湰韬病鍔細`Stroke.SetParabolaAxis` 浠嶇劧**淇濆舰鐘跺湴杞柟鍚?*
        //锛堣繖鏉″墠闈⑩懀鐨勫洓涓湞鍚戝惊鐜凡缁忓湪楠屼簡锛岃繖閲屽彧琛ヤ竴鍙?鍏ュ彛鎹簡銆佺畻寮忔病鎹?锛夈€?        pb2.SetParabolaAxis(CurveAxis.OpenRight);
        Check("鏈濆悜锛氬叆鍙ｆ崲浜嗕箣鍚庯紝鎹㈡湞鍚戜粛鐒?*淇濆舰鐘?*锛坧 涓嶅彉锛夛紙卤0.5锛?,
              pb2.EffectiveAxis == CurveAxis.OpenRight && Near(pb2.ParabolaPLocal(), 240f, .5f),
              $"{pb2.EffectiveAxis}锛宲 {pb2.ParabolaPLocal():F1}锛堟湡鏈?240锛?);
        pb2.SetParabolaAxis(CurveAxis.OpenUp);
        SettleFrames(60);

        // 瀹炶酱娌?y 鐨勫弻鏇茬嚎锛氳疆寤撳悓鏍?*涓嶈兘鏈夋í绌夸袱鏀箣闂寸殑骞界伒绾挎**銆?        // 涓ゆ璧版潵閫狅細绗竴姝ユ嫋鍑?*娓愯繎绾挎**锛堟嫋鍒板摢灏辨槸鍝細A = 300銆丅 = 600 鈫?鏂滅巼 m = 2锛夛紱
        // 绗簩姝ユ嫋涓€涓?**姣旀笎杩戠嚎鏇寸珫**"鐨勭偣 (200, 500)锛坢路|dx| = 400 < 500锛?        // 鈫?鐒︾偣钀藉湪 y 杞达紝涔熷氨鏄?*涓婁笅鍙屾洸绾?*銆?        var hy2 = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1600f, 500f);
        hy2.SetHyperbolaFromAsymptote(1600f, 500f, 1600f + 300f, 500f + 600f, minAxis);
        hy2.SetHyperbolaThroughPoint(1600f + 200f, 500f + 500f);
        Check("鍙屾洸绾匡細绗簩姝ョ殑鐐规洿绔?鈫?**鐒︾偣鍦?y 杞?*锛堜笂涓嬪弻鏇茬嚎锛夛紝鍗婅酱 300 / 150锛埪?.5锛?,
              hy2.CurveAxis == CurveAxis.TransverseY
              && Near(hy2.HyperbolaRealLocal(), 300f, .5f) && Near(hy2.HyperbolaImagLocal(), 150f, .5f),
              $"{hy2.CurveAxis}锛屽疄鍗婅酱 {hy2.HyperbolaRealLocal():F1}銆佽櫄鍗婅酱 {hy2.HyperbolaImagLocal():F1}");
        // 杩欎竴姝ョ殑**鏍稿績**锛氭洸绾垮繀椤?*姝ｅソ缁忚繃閭ｄ釜鐐?*锛堜笉鏄?鎶曞奖鍒板疄杞?閭ｇ杩戜技锛夈€?        // 瀹炶酱娌?y 鏃舵爣鍑嗗紡鏄?`y虏/b虏 鈭?x虏/a虏 = 1`锛屾妸 (200, 500) 浠ｈ繘鍘诲簲褰撶瓑浜?1銆?        // 寮忓瓙閲岃鐢?*鏇茬嚎鑷繁鐨?* x / y 鏂瑰悜绯绘暟锛坄HyperbolaCurveALocal/BLocal`锛夛紝
        // **涓嶆槸**娓愯繎绾挎鐨?`HyperbolaALocal/BLocal` 鈥斺€?2026-09-20 鎷嗗紑涔嬪悗杩欎袱涓埆鎷块敊銆?        float c2A = hy2.HyperbolaCurveALocal(), c2B = hy2.HyperbolaCurveBLocal();
        float hy2Fit = 500f * 500f / (c2B * c2B) - 200f * 200f / (c2A * c2A);
        Check("鍙屾洸绾匡細鏇茬嚎**姝ｅソ缁忚繃閭ｄ釜鐐?*锛坹虏/b虏 鈭?x虏/a虏 = 1锛夛紙卤0.01锛?,
              Near(hy2Fit, 1f, .01f), $"浠ｅ叆寰?{hy2Fit:F4}锛堟湡鏈?1锛?);
        var hy2Outline = hy2.ShapeOutline();
        float nearest2 = float.MaxValue;
        for (int i = 1; i < hy2Outline.Count; i++)
        {
            if (Stroke.IsOutlineBreak(hy2Outline[i - 1]) || Stroke.IsOutlineBreak(hy2Outline[i])) continue;
            nearest2 = MathF.Min(nearest2, DistPointSeg(new Vector2(1600f, 500f),
                                                        hy2Outline[i - 1], hy2Outline[i]));
        }
        Check("鍙屾洸绾匡紙瀹炶酱娌?y锛夛細杞粨涔熸病鏈夊菇鐏电嚎娈碉紙涓績鍒颁换涓€娈?鈮?0.9a锛?,
              nearest2 >= hy2.HyperbolaRealLocal() * 0.9f,
              $"{hy2.CurveAxis}锛屾渶杩戣窛绂?{nearest2:F1}锛堝疄鍗婅酱 {hy2.HyperbolaRealLocal():F1}锛?);
        var hy2Box = hy2.CurveBoxLocal();
        Check("鍙屾洸绾匡紙瀹炶酱娌?y锛夛細绱ф = 卤A 脳 卤B锛堝氨鏄笎杩戠嚎妗嗭級锛埪?.5锛?,
              Near(hy2Box.MinX, 1600f - 300f, .5f) && Near(hy2Box.MaxX, 1600f + 300f, .5f)
              && Near(hy2Box.MinY, 500f - 600f, .5f) && Near(hy2Box.MaxY, 500f + 600f, .5f),
              $"妗?({hy2Box.MinX:F1},{hy2Box.MinY:F1})..({hy2Box.MaxX:F1},{hy2Box.MaxY:F1})"
              + " 鏈熸湜 (1300,-100)..(1900,1100)");
        var (asFrom2, asTo2) = hy2.HyperbolaAsymptoteLocal(1);
        Check("鍙屾洸绾匡細**娓愯繎绾跨殑绔偣灏辨槸绱ф鐨勮**锛堜袱涓湞鍚戦兘鎴愮珛锛夛紙榛樿 ShowAsymptotes = 鐢伙級",
              hy2.ShowAsymptotes
              && Near(asTo2.X, 1600f + 300f, .5f) && Near(asTo2.Y, 500f + 600f, .5f)
              && Near(asTo2.X, hy2Box.MaxX, .5f) && Near(asTo2.Y, hy2Box.MaxY, .5f),
              $"({asFrom2.X:F1},{asFrom2.Y:F1})..({asTo2.X:F1},{asTo2.Y:F1})"
              + $" vs 妗嗚 ({hy2Box.MaxX:F1},{hy2Box.MaxY:F1})");

        // ============ 鈶 鍥涚寮€鍙ｏ細**鐪熸満鎷栧嚭鏉?*锛堢敤鎴?2026-09-20 闂?鏄笉鏄簲璇ユ湁鍥涚"锛?==
        //
        // 闈㈡澘閭ｄ竴鏍煎彧鏈?*涓ゆ。**锛堜笂涓嬫姏鐗?/ 宸﹀彸鎶涚墿锛夛紝**鍏蜂綋鏈濆摢杈圭敱閭ｄ竴鎷栫殑绗﹀彿瀹?*
        //锛堣 `Stroke.ParabolaAxisOfDrag`锛夆€斺€旀墍浠ュ洓绉嶅紑鍙ｆ湰鏉ュ氨鏄敾寰楀嚭鏉ョ殑锛?        // 鍙槸"鏈濅笂杩樻槸鏈濅笅"杩欎欢浜嬮潰鏉挎浛涓嶄簡浣犲洖绛旓紙鏂瑰悜琚潰鏉块€夋鏃讹紝
        // "閫夌潃鍚戜笂銆佹墜鍗村線涓嬫嫋"浼氱敾鍑鸿窡鎵嬫寚鍙嶈繃鏉ョ殑鏇茬嚎锛夈€?        //
        // 涓婇潰 鈶?鍙拤浜?*妯″瀷灞?*锛堢洿鎺ヨ皟 `SetParabolaAxis`锛夛紝杩欎竴娈佃ˉ**鐪熸満**锛?        // 鎸変綇椤剁偣寰€鍥涗釜鏂瑰悜鍚勬嫋涓€娆★紝鐪嬭惤鐐归偅涓€鏉＄殑鏈濆悜瀵逛笉瀵广€?        // 鍒ゆ嵁鏄?`EffectiveAxis`锛堣绔粺涓€璧板畠锛屼笉鏄偅涓瓨涓嬫潵鐨勫瓧娈碉級銆?        Console.WriteLine("  -- 鈶. 鍥涚寮€鍙ｏ細鐪熸満鎷栧嚭鏉ョ殑鏂瑰悜 --");
        {
            Doc.Clear();
            Doc.ClearHistory();

            // 鎶?鍝竴瀵?鍒囧埌鎸囧畾妗ｏ紙闈㈡澘閭ｄ竴鏍肩偣绗簩涓嬪氨鏄共杩欎釜鐨勶級銆?            void SetParaPair(CurveAxis pair)
            {
                for (int k = 0; k < 2 && Host.State.ParabolaAxis != pair; k++)
                    Host.Commands.CycleParabolaAxis();
                SettleFrames(80);
            }
            // 鎸変綇椤剁偣 (vx,vy) 鎷栧埌 (vx+dx, vy+dy)锛屾澗鎵嬪氨鎴愪竴鏉℃姏鐗╃嚎銆?            void DragParaAxis(float vx, float vy, float dx, float dy)
            {
                SendMouse((int)vx, (int)vy, 0);                                    SettleFrames(60);
                SendMouse((int)vx, (int)vy, Native.MOUSEEVENTF_LEFTDOWN);          SettleFrames(60);
                SendMouse((int)(vx + dx * 0.5f), (int)(vy + dy * 0.5f), 0);        SettleFrames(80);
                SendMouse((int)(vx + dx), (int)(vy + dy), 0);                      SettleFrames(160);
                SendMouse((int)(vx + dx), (int)(vy + dy), Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(200);
            }
            void SetParaTool()
            {
                SetToolFromUi(Tool.Parabola);
                SettleFrames(80);
            }

            float pvx = _virtualX + 700f, pvy = _virtualY + 1050f;
            // 鍥涗釜鏂瑰悜閮芥嫋 (卤300, 卤300)锛歚s = t = 300` 鈫?`p = 300虏/600 = 150`锛屼竴鏍艰寰椾笅銆?            var openCases = new (CurveAxis pair, float dx, float dy, CurveAxis want, string how)[]
            {
                (CurveAxis.OpenUp,     300f, -300f, CurveAxis.OpenUp,    "寰€涓婃嫋"),
                (CurveAxis.OpenUp,     300f,  300f, CurveAxis.OpenDown,  "寰€涓嬫嫋"),
                (CurveAxis.OpenRight,  300f,  300f, CurveAxis.OpenRight, "寰€鍙虫嫋"),
                (CurveAxis.OpenRight, -300f,  300f, CurveAxis.OpenLeft,  "寰€宸︽嫋"),
            };
            foreach (var oc in openCases)
            {
                Doc.Clear();
                Doc.ClearHistory();
                SetParaTool();
                SetParaPair(oc.pair);
                DragParaAxis(pvx, pvy, oc.dx, oc.dy);

                var ps = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
                Check($"鎶涚墿绾柯穥oc.how}锛堥潰鏉裤€寋(oc.pair == CurveAxis.OpenUp ? "涓婁笅" : "宸﹀彸")}鎶涚墿銆嶆。锛?
                      + $" 鈫?寮€鍙?{oc.want}",
                      ps != null && ps.Kind == StrokeKind.Parabola && ps.EffectiveAxis == oc.want,
                      $"瀵硅薄 {Doc.Strokes.Count} 鏉★紝"
                      + $"鏈濆悜 {(ps == null ? "锛堟病鐢诲嚭鏉ワ級" : ps.EffectiveAxis.ToString())}锛堟湡鏈?{oc.want}锛?);
            }
            Doc.Clear();
            Doc.ClearHistory();
            SetParaPair(CurveAxis.OpenUp);       // 鏀跺熬锛氱粰鍒汉鐣欎笅涓€妗ｅ共鍑€鐨勶紙鍚庨潰鍑犳瑕佺敤锛?        }

        // ================= 鈶. 姝ｅ垏锛氱湡鏈轰竴绗旀嫋鍑烘潵 =================
        //
        // 涓婇潰閭ｄ竴娈靛彧閽変簡**妯″瀷灞?*锛堢洿鎺ヨ皟 `SetTangentBox`锛夈€傝繖涓€娈佃ˉ**鐪熸満**锛?        // 鐢ㄦ埛鎸変笅閭ｄ竴鐐广€佹嫋鍑洪偅涓€绗旓紝**寮曟搸鑷繁鍐欏嚭鏉ョ殑**鏄笉鏄垜浠鐨勯偅浠藉畾涔?        //锛堟暀璁細鑷繁 new 涓€涓璞°€佸瓧娈佃嚜宸卞～锛?*涓嶇畻娴?*鈥斺€旈偅鏉¤矾缁曡繃浜嗗紩鎿庯級銆?        //
        // 瑕佺洴鐨勪袱涓暟锛氬師鐐?= **鎸変笅閭ｄ竴鐐?*锛堜笉鏄嫋鍑哄幓鐨勯偅涓锛佹鍒囧拰姝ｅ鸡鍦ㄨ繖鐐逛笂
        // 姝ｅソ鐩稿弽锛氭寮︽妸璧锋墜鐐圭暀鍦ㄥ師鍦板綋璧风偣锛屾鍒囨槸**浠ヨ捣鎵嬬偣涓轰腑蹇?*寰€鍥涘懆闀匡級锛?        // 鍗婃敮闀?/ 鍙鍗婇珮 = 杩欎竴绗旂殑 |dx| / |dy|銆?        Console.WriteLine("  -- 鈶. 姝ｅ垏锛氱湡鏈轰竴绗旀嫋鍑烘潵锛堟寜涓?= 鍘熺偣锛?-");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Tangent);
            SettleFrames(80);

            float ox = _virtualX + 700f, oy = _virtualY + 1000f;
            SendMouse((int)ox, (int)oy, 0);                                        SettleFrames(60);
            SendMouse((int)ox, (int)oy, Native.MOUSEEVENTF_LEFTDOWN);              SettleFrames(60);
            SendMouse((int)(ox + 80f), (int)(oy + 90f), 0);                        SettleFrames(80);
            SendMouse((int)(ox + 160f), (int)(oy + 180f), 0);                      SettleFrames(160);
            SendMouse((int)(ox + 160f), (int)(oy + 180f), Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);

            var ts = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("姝ｅ垏路鐪熸満涓€绗旓細鐢诲嚭鏉ョ殑鏄?*姝ｅ垏**涓€涓璞?,
                  ts != null && ts.Kind == StrokeKind.Tangent,
                  $"瀵硅薄 {Doc.Strokes.Count} 鏉★紝绉嶇被 {(ts == null ? "锛堟病鐢诲嚭鏉ワ級" : ts.Kind.ToString())}");
            Check("姝ｅ垏路鐪熸満涓€绗旓細**鍘熺偣灏辨槸鎸変笅閭ｄ竴鐐?*锛堜笉鏄嫋鍑哄幓鐨勯偅涓锛夛紙卤2锛?,
                  ts != null && Near(ts.TangentOriginLocal().X, ox, 2f)
                  && Near(ts.TangentOriginLocal().Y, oy, 2f),
                  ts == null ? "锛堟病鐢诲嚭鏉ワ級"
                  : $"鍘熺偣 ({ts.TangentOriginLocal().X:F0},{ts.TangentOriginLocal().Y:F0})"
                    + $"锛堟寜涓嬬殑 ({ox:F0},{oy:F0})锛?);
            Check("姝ｅ垏路鐪熸満涓€绗旓細鍗婃敮闀?= |dx| = 160锛涘彲瑙嗗崐楂?*琚《鍒颁笅闄?480**锛堟嫋鐨勬槸 180锛夛紙卤2锛?,
                  ts != null && Near(ts.TangentHalfSpanLocal(), 160f, 2f)
                  && Near(ts.TangentHalfHeightLocal(), 3f * 160f, 2f),
                  ts == null ? "锛堟病鐢诲嚭鏉ワ級"
                  : $"鍗婃敮闀?{ts.TangentHalfSpanLocal():F0}銆佸彲瑙嗗崐楂?{ts.TangentHalfHeightLocal():F0}");

            // 灞忓箷涓婁篃**鐪熺殑鐪嬪緱瑙?*锛堜笉鏄?鍙繘浜嗘枃妗ｃ€佹病娓叉煋"锛夛細绗旇壊鏄搧绾紝鏁伴偅涓€鐗囩殑鍝佺孩鍍忕礌
            //锛堣繖涓€娈电殑鎺㈤拡鍙ｅ緞鍜屼笂闈㈠嚑娈典竴鏍凤紝瑙?`CountMagenta`锛夈€?            Doc.InvalidateAll();
            SettleFrames(200);
            int inkOn = ScreenProbe.CountMagenta((int)(ox - 180f), (int)(oy - 560f), 360, 1120);
            Check("姝ｅ垏路鐪熸満涓€绗旓細灞忓箷涓婄湡鏈夊ⅷ锛堜笉鏄彧杩涗簡鏂囨。娌＄敾锛?, inkOn > 300,
                  $"{inkOn} 涓搧绾㈠儚绱狅紙闂ㄦ 300锛?);
        }

        // ================= 鈶. 娉㈡氮绾匡細鐪熸満涓€绗旀嫋鍑烘潵锛堟嫋澶氶暱鐢诲闀匡級 =================
        //
        // 杩欎竴鏍煎拰銆屾寮︺€嶇殑宸埆鍏ㄥ湪"杩欎竴鎷栫浠€涔?涓婏紝鎵€浠ョ湡鏈鸿繖涓€绗旇鐩綇鐨勫氨涓€浠朵簨锛?        // **妯悜鎷栧杩?鈫?鐢诲嚭鏉ュ嚑涓懆鏈?*锛坄妗嗗 梅 鎸箙`锛屽洜涓虹敤鎴峰畾鐨?鎸箙 = 涓€涓懆鏈?锛夛紝
        // 鑰屼笉鏄?鎷栧嚭鏉ョ殑妗嗗氨鏄竴涓懆鏈?銆?        Console.WriteLine("  -- 鈶. 娉㈡氮绾匡細鐪熸満涓€绗旀嫋鍑烘潵锛堟嫋澶氶暱鐢诲闀匡級--");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Wave);
            SettleFrames(80);

            float wx = _virtualX + 700f, wy = _virtualY + 900f;
            SendMouse((int)wx, (int)wy, 0);                                        SettleFrames(60);
            SendMouse((int)wx, (int)wy, Native.MOUSEEVENTF_LEFTDOWN);              SettleFrames(60);
            SendMouse((int)(wx + 150f), (int)(wy - 50f), 0);                       SettleFrames(80);
            SendMouse((int)(wx + 300f), (int)(wy - 100f), 0);                      SettleFrames(160);
            SendMouse((int)(wx + 300f), (int)(wy - 100f), Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);

            var ws = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("娉㈡氮绾柯风湡鏈轰竴绗旓細鐢诲嚭鏉ョ殑鏄?*娉㈡氮绾?*涓€涓璞?,
                  ws != null && ws.Kind == StrokeKind.Wave,
                  $"瀵硅薄 {Doc.Strokes.Count} 鏉★紝绉嶇被 {(ws == null ? "锛堟病鐢诲嚭鏉ワ級" : ws.Kind.ToString())}");
            Check("娉㈡氮绾柯风湡鏈轰竴绗旓細A = 100銆乀 = 100銆佺敾 300 瀹?鈫?**3 涓暣鍛ㄦ湡**锛埪?锛?,
                  ws != null && Near(ws.WaveAmplitudeLocal(), 100f, 2f)
                  && Near(ws.WavePeriodLocal(), 100f, 2f)
                  && Near(ws.WaveCyclesLocal(), 3f, .02f),
                  ws == null ? "锛堟病鐢诲嚭鏉ワ級"
                  : $"A {ws.WaveAmplitudeLocal():F0}  T {ws.WavePeriodLocal():F0}"
                    + $"  鍛ㄦ湡鏁?{ws.WaveCyclesLocal():F2}锛堟湡鏈?3.00锛?);
            Check("娉㈡氮绾柯风湡鏈轰竴绗旓細涓や釜瀹氫箟鍏冪礌锛堣捣鐐?锛?缁堢偣锛屾瀬鍊肩偣鏄幇鎺ㄧ殑锛?,
                  ws != null && ws.Points.Count == 2,
                  $"{ws?.Points.Count ?? -1} 涓帶鍒剁偣锛堟湡鏈?2锛?);
        }

        // ================= 鈶. 妞渾锛堝甫鐒︾偣锛夛細鐪熸満涓€绗?锛?鐒︾偣 锛?鎷?P =================
        //
        // 鐢ㄦ埛 2026-09-22 瀹氱殑鍙ｅ緞锛?        //   路 绗簩琛屽姞涓€鏍?*妞渾锛堝甫鐒︾偣锛?*锛堝拰绗竴琛岄偅涓?绾き鍦?**骞跺瓨**锛屼袱涓伐鍏凤級锛?        //   路 **涓ゆ。**锛氭湁鐒︾偣涓夎褰?/ 娌℃湁鐒︾偣涓夎褰⑩€斺€?*涓や釜鐒︾偣涓ゆ。閮界敾**锛?        //     "鏃?閭ｄ竴妗ｅ彧鏄?*涓嶈繛** F鈧丳銆丗鈧侾锛堝師璇濓細"杩樻槸鐢荤劍鐐癸紝鍙槸涓嶈繛涓夎褰?锛夛紱
        //   路 鐒︾偣涓夎褰㈢殑**椤剁偣 P 鍦ㄦき鍦嗕笂銆佸彲浠ユ嫋鐫€璧?*銆?        //
        // 鎵€浠ヨ繖涓€娈电洴浜斾欢浜嬶紙绗?鈶?鈶?鏉℃槸纭垽鎹紝涓嶆槸"鐪嬭捣鏉ュ"锛夛細
        //   鈶?鐪熸満涓€绗旂敾鍑烘潵鐨勬槸瀹冿紱鈶?涓や釜鐒︾偣婊¤冻 `c虏 = a虏 鈭?b虏`銆佸湪闀胯酱涓婏紱
        //   鈶?**P 涓€瀹氬湪妞渾涓?*鈥斺€旂敤妞渾鐨勫畾涔夊垽锛歚|PF鈧亅 + |PF鈧倈 = 2a`锛堟瘮"鍙嶈В鍙傛暟瑙?纭緱澶氾級锛?        //   鈶?鎷?P锛氳鍙樹簡銆?鍜?鐓ф牱 2a銆?*涓€姝ユ挙閿€鍥炲緱鍘?*锛?        //   鈶?涓ゆ。锛氭湁涓夎褰㈡椂澶氫竴绗斻€佹棤涓夎褰㈡椂瀹冩病浜?*浣嗕袱涓劍鐐硅繕鍦?*锛?        //      鑰屼笖閮戒笉璧拌櫄绾胯緟鍔╂Ы锛坄Aux == false`锛岀劍鐐逛笁瑙掑舰鏄富浣撲笉鏄緟鍔╃嚎锛夈€?        Console.WriteLine("  -- 鈶. 妞渾锛堝甫鐒︾偣锛夛細鐪熸満涓€绗?锛?鐒︾偣涓夎褰?锛?鎷?P --");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.ConicEllipse);
            // 褰掑埌"鏈夌劍鐐逛笁瑙掑舰"閭ｄ竴妗ｏ細涓嶅啓姝?杩涙潵鏃朵竴瀹氭槸榛樿妗?锛堝墠闈㈠嚑娈靛彲鑳界偣杩囧畠锛夈€?            for (int k = 0; k < 4 && !Host.State.EllipseFocusTriangle; k++)
                Host.Commands.CycleEllipseFocusTriangle();
            SettleFrames(80);

            // 鎸変笅 = **涓績**銆佹嫋鍑哄幓 = **澶栬鐐?*锛堝拰绗竴琛岄偅涓き鍦嗗悓涓€濂楀畾涔夛級锛?            // 杩欓噷鎷?260 脳 120 鈫?a = 260銆乥 = 120锛屼簬鏄?c = 鈭?260虏 鈭?120虏) 鈮?230.65銆?            float ex = _virtualX + 1100f, ey = _virtualY + 1000f;
            const float wa = 260f, wb = 120f;
            SendMouse((int)ex, (int)ey, 0);                                        SettleFrames(60);
            SendMouse((int)ex, (int)ey, Native.MOUSEEVENTF_LEFTDOWN);              SettleFrames(60);
            SendMouse((int)(ex + wa), (int)(ey + wb / 2f), 0);                     SettleFrames(80);
            SendMouse((int)(ex + wa), (int)(ey + wb), 0);                          SettleFrames(160);
            SendMouse((int)(ex + wa), (int)(ey + wb), Native.MOUSEEVENTF_LEFTUP);  SettleFrames(250);

            var ce = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("甯︾劍鐐规き鍦喡风湡鏈轰竴绗旓細鐢诲嚭鏉ョ殑鏄?*妞渾锛堝甫鐒︾偣锛?*涓€涓璞?,
                  ce != null && ce.Kind == StrokeKind.ConicEllipse,
                  $"瀵硅薄 {Doc.Strokes.Count} 鏉★紝绉嶇被 {(ce == null ? "锛堟病鐢诲嚭鏉ワ級" : ce.Kind.ToString())}");
            if (ce == null)
            {
                Console.WriteLine("  锛堝悗闈㈠嚑椤规病娉曢獙锛?);
            }
            else
            {
                Check("甯︾劍鐐规き鍦嗭細涓や釜瀹氫箟鍏冪礌锛堜腑蹇?锛?澶栬鐐癸紝鍜岀涓€琛岄偅涓き鍦嗗悓涓€濂楋級",
                      ce.Points.Count == 2, $"{ce.Points.Count} 涓帶鍒剁偣锛堟湡鏈?2锛?);
                Check("甯︾劍鐐规き鍦嗭細涓績 = 鎸変笅閭ｄ竴鐐广€乤 = 260銆乥 = 120锛埪?锛?,
                      Near(ce.ShapeCenterLocal.X, ex, 2f) && Near(ce.ShapeCenterLocal.Y, ey, 2f)
                      && Near(ce.SemiAxisALocal, wa, 2f) && Near(ce.SemiAxisBLocal, wb, 2f),
                      $"涓績 ({ce.ShapeCenterLocal.X:F0},{ce.ShapeCenterLocal.Y:F0})"
                      + $"锛堟寜涓?({ex:F0},{ey:F0})锛夈€乤 {ce.SemiAxisALocal:F0}銆乥 {ce.SemiAxisBLocal:F0}");

                // ---- 鈶?涓や釜鐒︾偣锛歝虏 = a虏 鈭?b虏锛屼笖钀藉湪**闀胯酱**涓?----
                float cc = MathF.Sqrt(wa * wa - wb * wb);                   // 鈮?230.65
                var (f1, f2) = ce.ConicEllipseFociLocal();
                Check($"甯︾劍鐐规き鍦嗭細鐒︾偣鍦ㄩ暱杞翠笂銆乧 = 鈭?a虏鈭抌虏) 鈮?{cc:F0}锛埪?锛?,
                      Near(f1.X, ex - cc, 2f) && Near(f1.Y, ey, 2f)
                      && Near(f2.X, ex + cc, 2f) && Near(f2.Y, ey, 2f),
                      $"F1 ({f1.X:F0},{f1.Y:F0})銆丗2 ({f2.X:F0},{f2.Y:F0})锛堟湡鏈?y = {ey:F0}锛寈 = 鈭搟cc:F0}锛?);

                // ---- 鈶?P 鍦ㄦき鍦嗕笂锛殀PF鈧亅 + |PF鈧倈 = 2a锛堟き鍦嗙殑瀹氫箟锛?----
                // 杩欐槸"P 涓€瀹氬湪妞渾涓?鏈€纭殑涓€鏉″垽鎹細瀹冧笉渚濊禆鎴戜滑瀵瑰弬鏁拌鐨勭悊瑙ｅ涓嶅銆?                float SumOfDists(Vector2 p) => Vector2.Distance(p, f1) + Vector2.Distance(p, f2);
                var p0 = ce.ConicEllipsePointLocal();
                Check($"甯︾劍鐐规き鍦嗭細榛樿鐨?P 鍦ㄦき鍦嗕笂锛坾PF鈧亅+|PF鈧倈 = 2a = {2 * wa:F0}锛夛紙卤1锛?,
                      Near(SumOfDists(p0), 2f * wa, 1f),
                      $"鍜?= {SumOfDists(p0):F1}锛堟湡鏈?{2 * wa:F0}锛?);
                // 榛樿瑙掓槸**宸︿笂鏂?*锛?20掳锛岃 Stroke.DefaultFocusPointU锛夛細
                // 鐢ㄦ埛 2026-09-22 鐨勫彛寰?鐒︾偣涓夎褰㈤偅涓《鐐光€︹€﹀紕鍦ㄦき鍦嗕笂闈?銆?                Check("甯︾劍鐐规き鍦嗭細榛樿鐨?P 鍦ㄦき鍦嗙殑**宸︿笂鏂?*锛?20掳 閭ｄ釜鐐癸級",
                      Near(p0.X, ex + wa * MathF.Cos(Stroke.DefaultFocusPointU), 2f)
                      && Near(p0.Y, ey + wb * MathF.Sin(Stroke.DefaultFocusPointU), 2f),
                      $"P ({p0.X:F0},{p0.Y:F0})锛堟湡鏈?({ex + wa * MathF.Cos(Stroke.DefaultFocusPointU):F0},"
                      + $"{ey + wb * MathF.Sin(Stroke.DefaultFocusPointU):F0})锛?);
                Check("甯︾劍鐐规き鍦嗭細榛樿鐨?P 涓嶈窡涓や釜鐒︾偣鍏辩嚎锛堜笁瑙掑舰涓嶄細閫€鍖栨垚涓€鏉＄嚎娈碉級",
                      MathF.Abs(p0.Y - ey) > 1f && MathF.Abs(p0.X - ex) > 1f,
                      $"P 鐩稿涓績 ({p0.X - ex:F0},{p0.Y - ey:F0})锛岀劍鐐瑰湪 y = {ey:F0} 杩欐潯绾夸笂");
                Check("甯︾劍鐐规き鍦嗭細榛樿妗?= **鏈夌劍鐐逛笁瑙掑舰**",
                      ce.FocusTriangle, $"FocusTriangle = {ce.FocusTriangle}");

                // ---- 鈶?涓ゆ。锛氭姌绾跨瑪鏁帮紙鈶?妞渾 鈶♀憿 涓夎褰㈢殑涓ゆ潯杈?鈶ｂ懁 涓や釜鐒︾偣锛夛紝涓旈兘涓嶈蛋杈呭姪妲?----
                int Pieces() => ce.InkPieces().Count(pc => pc.Pts.Count >= 2);
                int AuxPieces() => ce.InkPieces().Count(pc => pc.Aux);
                int piecesWithTri = Pieces();
                Check("甯︾劍鐐规き鍦喡锋湁涓夎褰㈡。锛氱敾鍑烘潵 5 绗旓紙妞渾 锛?涓ゆ潯杈?锛?涓や釜鐒︾偣灏忓渾鐐癸級",
                      piecesWithTri == 5, $"{piecesWithTri} 绗旓紙鏈熸湜 5锛?);
                Check("甯︾劍鐐规き鍦嗭細**涓€绗旈兘涓嶈蛋铏氱嚎杈呭姪妲?*锛堢劍鐐逛笁瑙掑舰鏄富浣擄紝涓嶆槸杈呭姪绾匡級",
                      AuxPieces() == 0, $"{AuxPieces()} 绗旀槸 Aux锛堟湡鏈?0锛?);

                ce.SetFocusTriangle(false);
                int piecesNoTri = Pieces();
                Check("甯︾劍鐐规き鍦喡锋棤涓夎褰㈡。锛氬皯涓€绗旓紙妞渾 锛?涓や釜鐒︾偣灏忓渾鐐?= 3 绗旓級銆佺劍鐐硅繕鍦?,
                      piecesNoTri == 3 && AuxPieces() == 0, $"{piecesNoTri} 绗旓紙鏈熸湜 3锛?);
                ce.SetFocusTriangle(true);                     // 鍚庨潰鐨勬柇瑷€鎺ョ潃鎸?鏈?閭ｄ竴妗ｈ蛋

                // ---- 鈶?鎷?P锛氭ā鍨嬮噷鐨勮鍙樹簡銆?鍜?鐓ф牱 2a銆佷竴姝ユ挙閿€鍥炲緱鍘?----
                Doc.SelectOnly(new[] { ce });
                SettleFrames(300);
                var pHandleFrom = SelectionHandles.ShapeHandleCanvasPosition(ce, ShapeHandle.FocusPoint);
                // 鐩爣锛氭妸 P 鎷栧埌"鍙傛暟瑙?45掳"閭ｄ釜鏂瑰悜涓婄殑鐐癸紙妞渾涓婇偅涓€鐐癸細
                // center + (a路cos45掳, b路sin45掳) 鈥斺€?娉ㄦ剰**鎷栧埌鍝効閮借**锛屾ā鍨嬩細鎶婃柟鍚戞姌鎴愯锛夈€?                var pTarget = new Vector2(ex + wa * 0.70711f, ey + wb * 0.70711f);
                bool tookP = SelectionGestureForTest(pHandleFrom.X, pHandleFrom.Y);
                UpdateSelectionGestureForTest(pTarget.X, pTarget.Y);
                SettleFrames(140);
                EndSelectionGestureForTest();
                SettleFrames(250);

                Check("甯︾劍鐐规き鍦喡锋嫋 P锛氱湡鐨勬姄浣忎簡閭ｄ釜鍦嗙偣锛堟墜鏌勫湪 P 涓婏級",
                      tookP, $"鎺ヤ綇 = {tookP}");
                Check("甯︾劍鐐规き鍦喡锋嫋 P锛氳鍙樻垚 45掳锛埪?.03 寮у害锛?,
                      !float.IsNaN(ce.FocusPointU) && Near(ce.FocusPointU, MathF.PI / 4f, .03f),
                      $"u = {(float.IsNaN(ce.FocusPointU) ? "NaN锛堟病鍐欒繘鍘伙級" : ce.FocusPointU.ToString("F4"))}"
                      + $"锛堟湡鏈?{MathF.PI / 4f:F4}锛?);
                var p1 = ce.ConicEllipsePointLocal();
                Check($"甯︾劍鐐规き鍦喡锋嫋 P 涔嬪悗锛歅 **杩樺湪妞渾涓?*锛坾PF鈧亅+|PF鈧倈 = 2a = {2 * wa:F0}锛夛紙卤1锛?,
                      Near(SumOfDists(p1), 2f * wa, 1f),
                      $"P ({p1.X:F0},{p1.Y:F0})锛屽拰 = {SumOfDists(p1):F1}");
                Check("甯︾劍鐐规き鍦喡锋嫋 P锛?*妞渾鏈韩涓€鍔ㄦ病鍔?*锛堝彧鏈夐偅涓鍙樹簡锛?,
                      Near(ce.SemiAxisALocal, wa, 2f) && Near(ce.SemiAxisBLocal, wb, 2f),
                      $"a {ce.SemiAxisALocal:F0}銆乥 {ce.SemiAxisBLocal:F0}");

                Doc.Undo();
                SettleFrames(200);
                Check("甯︾劍鐐规き鍦喡锋嫋 P锛氫竴姝ユ挙閿€鍥炲埌銆岃繕娌℃嫋杩囥€嶇殑鐘舵€侊紙P 鍥為粯璁ら偅涓€绔級",
                      float.IsNaN(ce.FocusPointU)
                      || Near(ce.ConicEllipsePointLocal().X, p0.X, 2f)
                      && Near(ce.ConicEllipsePointLocal().Y, p0.Y, 2f),
                      $"FocusPointU = {(float.IsNaN(ce.FocusPointU) ? "NaN锛堣嚜鍔級" : ce.FocusPointU.ToString("F4"))}"
                      + $"锛孭 = ({ce.ConicEllipsePointLocal().X:F0},{ce.ConicEllipsePointLocal().Y:F0})"
                      + $"锛堥粯璁?({p0.X:F0},{p0.Y:F0})锛?);
                Doc.Selected.Clear();
                SettleFrames(120);
            }
        }

        // ================= 鈶?瀛樻。锛氬線杩?锛?鐪?v11 鑰佹枃浠?=================
        Doc.Clear();
        Doc.ClearHistory();
        var saveMe = NewCurve(Tool.Parabola, StrokeKind.Parabola, 700f, 700f);
        saveMe.CurveAxis = CurveAxis.OpenLeft;                             // 閫夛細寮€鍙ｅ悜宸?        saveMe.SetParabolaVertex(700f, 700f);
        saveMe.SetParabolaThroughPoint(560f, 420f);                        // s = 140銆乼 = 280 鈫?p = 280
        var saveHy = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1400f, 700f);
        // 涓ゆ锛氱涓€姝ユ嫋鍑?*娓愯繎绾挎**锛圓 = 160銆丅 = 320 鈫?鏂滅巼 2锛夛紝
        // 绗簩姝ユ嫋涓€涓洿绔栫殑鐐?鈫?鐒︾偣鍦?y 杞达紙涓婁笅鍙屾洸绾匡級銆?        saveHy.SetHyperbolaFromAsymptote(1400f, 700f, 1560f, 1020f, minAxis);
        saveHy.SetHyperbolaThroughPoint(1400f + 100f, 700f + 250f);
        var saveSin = NewCurve(Tool.Sine, StrokeKind.Sine, 300f, 700f);
        saveSin.SetWaveBox(300f, 700f, 780f, 600f, minAxis);
        // 姝ｅ垏锛坴21 鏂板鐨勫彇鍊硷級锛氫竴绗旓紝瀛樼殑灏辨槸"鍘熺偣 锛?閭ｄ釜瑙?锛屽舰鐘跺叏鐢辫繖涓や釜鐐规淳鐢熴€?        // 鈿?瀛樼殑鏄?*鎷栧嚭鏉ョ殑閭ｄ釜瑙?*锛?80锛夛紝鐢荤殑鏃跺€欏崐楂樹細琚笅闄愰《鍒?480 鈥斺€?涓や釜閮借璇诲洖鏉ャ€?        var saveTan = NewCurve(Tool.Tangent, StrokeKind.Tangent, 900f, 1200f);
        saveTan.SetTangentBox(900f, 1200f, 1060f, 1380f, minAxis);          // 鍗婃敮闀?160銆佹嫋鐨勫崐楂?180
        // 娉㈡氮绾匡紙v22 鏂板鐨勫彇鍊硷級锛氬拰姝ｅ鸡鍚屼竴濂?璧风偣 锛?缁堢偣"锛屽樊鍒彧鏄?*杩欎竴鎷栫浠€涔?*銆?        var saveWave = NewCurve(Tool.Wave, StrokeKind.Wave, 900f, 1700f);
        saveWave.SetWaveBox(900f, 1700f, 1200f, 1600f, minAxis);            // A = 100 鈫?3 涓懆鏈?        // 妞渾锛堝甫鐒︾偣锛夛紙v23 鏂板鐨勫彇鍊?锛?涓や釜鏂板瓧娈碉級锛氬瓨"**鏃犵劍鐐逛笁瑙掑舰**閭ｄ竴妗?锛?        // 涓€涓?*鎷栬繃鐨?P**"锛岃繖鏍蜂袱涓瓧娈甸兘琚湡姝ｈ蛋鍒帮紙涓嶆槸闈犻粯璁ゅ€艰挋杩囧幓锛夈€?        var saveCe = NewCurve(Tool.ConicEllipse, StrokeKind.ConicEllipse, 400f, 1200f);
        saveCe.SetEnd(660f, 1320f);                                        // a = 260銆乥 = 120
        saveCe.FocusTriangle = false;
        saveCe.SetConicEllipsePointAngle(MathF.PI / 3f);                    // 60掳锛堜笉鐢ㄩ粯璁ゅ€硷級
        Doc.AddStroke(saveMe);
        Doc.AddStroke(saveHy);
        Doc.AddStroke(saveSin);
        Doc.AddStroke(saveTan);
        Doc.AddStroke(saveWave);
        Doc.AddStroke(saveCe);

        var blob = InkSerializer.Save(Doc);
        var back = new InkDocument();
        InkSerializer.LoadInto(back, blob);
        bool roundTrip = back.Strokes.Count == 6
                         && back.Strokes[0].Kind == StrokeKind.Parabola
                         && Near(back.Strokes[0].ParabolaDirLocal().X, -1f, .01f)
                         && back.Strokes[1].Kind == StrokeKind.Hyperbola
                         && back.Strokes[1].CurveAxis == CurveAxis.TransverseY
                         && back.Strokes[1].ShowAsymptotes
                         && back.Strokes[2].Kind == StrokeKind.Sine
                         && back.Strokes[3].Kind == StrokeKind.Tangent
                         && Near(back.Strokes[3].TangentHalfSpanLocal(), 160f, .5f)
                         && Near(back.Strokes[3].TangentHalfHeightLocal(), 3f * 160f, .5f)
                         && back.Strokes[4].Kind == StrokeKind.Wave
                         && Near(back.Strokes[4].WaveCyclesLocal(), 3f, .02f)
                         && back.Strokes[5].Kind == StrokeKind.ConicEllipse
                          && Near(back.Strokes[5].SemiAxisALocal, 260f, .5f)
                          && Near(back.Strokes[5].SemiAxisBLocal, 120f, .5f)
                          && !back.Strokes[5].FocusTriangle
                          && Near(back.Strokes[5].FocusPointU, MathF.PI / 3f, .001f);
        Check("瀛樻。锛氫簲绉嶆洸绾?锛?妞渾锛堝甫鐒︾偣锛夛紜 鏈濆悜 锛?娓愯繎绾垮紑鍏抽兘鍥炴潵浜嗭紙鍚?v21 姝ｅ垏 / v22 娉㈡氮绾?/ v23 妞渾锛?,
              roundTrip,
              back.Strokes.Count == 5
                  ? $"{back.Strokes[0].Kind}/鏂瑰悜 {back.Strokes[0].ParabolaDirLocal()}銆?
                    + $"{back.Strokes[1].Kind}/{back.Strokes[1].CurveAxis}/娓愯繎绾?{back.Strokes[1].ShowAsymptotes}銆?
                    + $"{back.Strokes[2].Kind}銆?
                    + $"{back.Strokes[3].Kind}锛堝崐鏀暱 {back.Strokes[3].TangentHalfSpanLocal():F0}锛夈€?
                    + $"{back.Strokes[4].Kind}锛堝懆鏈熸暟 {back.Strokes[4].WaveCyclesLocal():F2}锛?
                  : back.Strokes.Count == 6
                    ? $"{back.Strokes[5].Kind}锛堢劍鐐逛笁瑙掑舰 {back.Strokes[5].FocusTriangle}銆?
                      + $"P 鐨勮 {back.Strokes[5].FocusPointU:F4} 鈥斺€?鏈熸湜 False / {MathF.PI / 3f:F4}锛?
                    : $"鍙鍥?{back.Strokes.Count} 鏉?);

        // 鐪熉穠22 鑰佹枃浠讹細**涓€鏉″甫鐒︾偣妞渾**鐨勬湯灏惧皯 5 涓瓧鑺傦紙v23 鏂板鐨勯偅涓や綅锛?        // 1 瀛楄妭寮€鍏?锛?4 瀛楄妭 float锛夈€傝绔繀椤婚€€鍒?鐢讳笁瑙掑舰 锛?P 杩樻病瀹氳繃锛圢aN锛?銆?        // `MakeLegacyFile` 鏄?浠庢暣涓枃浠舵湯灏剧爫 N 涓瓧鑺?锛屾墍浠ヨ繖閲屽彧鑳芥槸**鍗曟潯瀵硅薄**鐨勬枃浠躲€?        var legacyCeDoc = new InkDocument();
        var legacyCe = NewCurve(Tool.ConicEllipse, StrokeKind.ConicEllipse, 600f, 900f);
        legacyCe.SetEnd(860f, 1020f);
        legacyCeDoc.AddStroke(legacyCe);
        var oldCe = new InkDocument();
        InkSerializer.LoadInto(oldCe, MakeLegacyFile(legacyCeDoc, 22, 5));
        Check("瀛樻。锛氱湡 v22 鑰佹枃浠惰寰楄繘鏉ワ紙1 鏉″璞★級", oldCe.Strokes.Count == 1, $"{oldCe.Strokes.Count} 鏉?);
        if (oldCe.Strokes.Count == 1)
        {
            var oc = oldCe.Strokes[0];
            Check("瀛樻。锛歷22 鑰佹枃浠堕噷娌℃湁閭ｄ袱浣?鈫?鐒︾偣涓夎褰㈤€€鍒伴粯璁わ紙鐢伙級銆丳 閫€鍒般€岃繕娌″畾杩囥€?,
                  oc.Kind == StrokeKind.ConicEllipse && oc.FocusTriangle && float.IsNaN(oc.FocusPointU),
                  $"Kind {oc.Kind}銆丗ocusTriangle {oc.FocusTriangle}銆?
                  + $"FocusPointU {(float.IsNaN(oc.FocusPointU) ? "NaN锛? 鑷姩鎽嗭級" : oc.FocusPointU.ToString())}");
        }


        // 鐪熉穠11 鑰佹枃浠讹細**涓€鏉″璞?*鐨勯偅鏉＄瑪鐢绘湯灏惧皯 1 涓瓧鑺傦紙灏辨槸 v12 鐨?娓愯繎绾?閭ｄ竴浣嶏級銆?        // 娉ㄦ剰 `MakeLegacyFile` 鏄?浠庢暣涓枃浠舵湯灏剧爫 N 涓瓧鑺?锛屾墍浠ュ畠鍙**鍗曟潯绗旂敾**鎴愮珛
        // 锛堝鏉℃椂鍙湁鏈€鍚庝竴鏉¤鐮嶅埌锛屼腑闂村嚑鏉′細鏁翠綋閿欎綅锛夆€斺€旇繖鏄偅涓伐鍏风殑鐢ㄦ硶绾﹀畾銆?        var legacyDoc = new InkDocument();
        var legacyHy = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1400f, 700f);
        // **鏁呮剰鎸?v13 鍙婁互鍓嶇殑璇箟閫?*锛氶偅鏃跺弻鏇茬嚎鍙湁**涓や釜**鎺у埗鐐癸紝绗簩涓偣鍚屾椂鏄?        // "娓愯繎绾挎"鍜?鏇茬嚎鍗婅酱"锛堟笎杩戠嚎鐢诲埌 卤2a銆伮?b锛夆€斺€旇繖姝ｆ槸鑰佹枃浠堕噷鐨勬牱瀛愩€?        // 璇荤閭ｆ潯杩佺Щ锛坄version <= 13`锛夊繀椤昏兘浠庡畠鎭㈠鍑?*褰㈢姸涓嶅彉**鐨勬柊琛ㄧず銆?        // 杩欓噷濉炵殑鏄€佺畻娉曚笅鐨?瑙掔偣"锛?1400,700) 鈫?(1475,850)锛屼簬鏄崐杞?a = 75銆乥 = 150銆佹枩鐜?2銆?        legacyHy.SetHyperbolaFromAsymptote(1400f, 700f, 1400f + 75f, 700f + 150f, minAxis);
        legacyHy.CurveAxis = CurveAxis.TransverseY;                              // 鑰佹枃浠堕噷鏈濆悜鏄瓨鐨?        // 杩佺Щ鍚庡簲褰撴槸锛氭笎杩戠嚎妗?A = 150銆丅 = 300锛屾洸绾跨粡杩?`u = 1` 閭ｄ釜鐐?鈫?**瀹炲崐杞磋繕鏄?150**銆?        legacyDoc.AddStroke(legacyHy);
        var old = new InkDocument();
        InkSerializer.LoadInto(old, MakeLegacyFile(legacyDoc, 11, 1));
        Check("瀛樻。锛氱湡 v11 鑰佹枃浠惰寰楄繘鏉ワ紙1 鏉″璞★級", old.Strokes.Count == 1, $"{old.Strokes.Count} 鏉?);
        if (old.Strokes.Count == 1)
        {
            Check("瀛樻。锛氳€佹枃浠堕噷鏈濆悜鐓ф棫锛堝疄杞存部 y锛夈€佹笎杩戠嚎閫€鍒伴粯璁わ紙鐢伙級",
                  old.Strokes[0].CurveAxis == CurveAxis.TransverseY
                  && old.Strokes[0].ShowAsymptotes
                  && Near(old.Strokes[0].HyperbolaRealLocal(), 150f, .5f),
                  $"{old.Strokes[0].CurveAxis}锛屾笎杩戠嚎 {old.Strokes[0].ShowAsymptotes}"
                  + $"锛屽疄鍗婅酱 {old.Strokes[0].HyperbolaRealLocal():F1}锛堟湡鏈?150锛?);
        }

        // 鐪熉穠12 鑰佹枃浠堕噷鐨?*鎶涚墿绾?*锛氳繖涓€鏉′笓鐩?v13 閭ｆ"鎹㈠惈涔?鐨勮縼绉汇€?        // v12 瀛樼殑鏄?椤剁偣 + **寮犲彛鐐?*锛堣惤鍦ㄥ绉拌酱涓娿€佺椤剁偣 p/2锛?锛屾柊璇荤蹇呴』鎶婂畠鎹㈡垚
        // "鏇茬嚎涓婄殑涓€涓偣"锛屽惁鍒欏弽瑙ｄ笉鍑哄紶鍙ｃ€佹洸绾夸細缂╁埌鏈€灏忊€斺€旇€屼笖**涓嶆姤閿?*锛?        // 鍙槸"涓€瀛樹竴璇诲氨鐦簡"銆?        //
        // 娉ㄦ剰杩欓噷鏄?*鏁呮剰**鐢ㄤ竴涓?钀藉湪瀵圭О杞翠笂"鐨勭偣鏉ユā鎷熻€佹枃浠讹細
        // 鏂板啓娉?`SetParabolaThroughPoint` 鏈潵鏄鏀炬洸绾夸笂鐨勭偣锛屼絾鑰佹枃浠剁殑绗簩浣嶅氨鏄紶鍙ｇ偣锛?        // 鍙湁杩欎箞閫犳墠鏄揣鐪熶环瀹炵殑 v12 璇箟锛坧 = 2 脳 |椤剁偣鈫掑紶鍙ｇ偣| = 280锛夈€?        var legacyPara = NewCurve(Tool.Parabola, StrokeKind.Parabola, 700f, 700f);
        legacyPara.CurveAxis = CurveAxis.OpenLeft;             // 寮€鍙ｅ悜宸︼細鏂瑰悜 = (鈭?, 0)
        legacyPara.SetParabolaVertex(700f, 700f);
        legacyPara.SetParabolaThroughPoint(560f, 700f);        // 寮犲彛鐐?= 椤剁偣 鈭?(140, 0) 鈫?p = 280
        var legacyParaDoc = new InkDocument();
        legacyParaDoc.AddStroke(legacyPara);
        var oldPara = new InkDocument();
        // 鐮?0 浣嶏細**v13 娌″線绗旂敾鍚庨潰鍔犲瓧鑺?*锛寁12 鐨勬枃浠跺拰瀹冧竴鏍烽暱锛堝彧宸増鏈彿锛夈€?        InkSerializer.LoadInto(oldPara, MakeLegacyFile(legacyParaDoc, 12, 0));
        Check("瀛樻。锛氱湡 v12 鑰佹枃浠堕噷鐨勬姏鐗╃嚎**璇诲洖鏉ヨ繕鏄?p = 280**锛堣縼绉昏繃鍘汇€佸舰鐘朵笉鍙橈級锛埪?.5锛?,
              oldPara.Strokes.Count == 1 && Near(oldPara.Strokes[0].ParabolaPLocal(), 280f, .5f)
              && oldPara.Strokes[0].CurveAxis == CurveAxis.OpenLeft,
              oldPara.Strokes.Count == 1
                  ? $"p {oldPara.Strokes[0].ParabolaPLocal():F1}锛堟湡鏈?280锛夈€?
                    + $"鏈濆悜 {oldPara.Strokes[0].CurveAxis}"
                  : $"鍙鍥?{oldPara.Strokes.Count} 鏉?);
        // 杩佺Щ涔嬪悗閭ｄ釜鐐瑰繀椤?*鐪熺殑钀藉湪鏇茬嚎涓?*锛堜笉鏄殢渚挎尓涓€涓級锛?        // `t 鈮?0` 鎵嶆湁淇℃伅瀹氬紶鍙ｏ紝鑰屼笖浠ｈ繘鍙傛暟鏂圭▼搴斿綋鑳藉涓娿€?        if (oldPara.Strokes.Count == 1)
        {
            var op = oldPara.Strokes[0];
            var (odir, operp) = Stroke.ParabolaBasis(op.EffectiveAxis);
            var od = op.CurvePointLocal(1) - op.CurvePointLocal(0);
            float os = Vector2.Dot(od, odir), ot = Vector2.Dot(od, operp);
            Check("瀛樻。锛氳縼绉诲悗閭ｄ釜鐐瑰湪**鏇茬嚎璇ュ湪鐨勫湴鏂?*锛坰 = p/2銆乼 = p = 280锛夛紙卤0.5锛?,
                  Near(os, 140f, .5f) && Near(ot, 280f, .5f), $"s {os:F1}銆乼 {ot:F1}锛堟湡鏈?140 / 280锛?);
        }

        // ============ 鈶?鍒犻櫎 / 鎹㈡柟鍚戜箣鍚庝笉鐣欐畫褰憋紙鐢ㄦ埛 2026-09-20 鎶ョ殑锛?===========
        //
        // 鐢ㄦ埛鍘熻瘽锛?鎶涚墿绾垮垹闄や細鐣欐畫褰?銆傛畫褰辫繖涓€绫?bug 鐨勭壒鐐规槸**鑷鍏ㄧ豢涔熷彲鑳芥湁**
        // 锛堝瓧娈点€佸寘鍥寸洅鍏ㄥ锛屽彧鏄睆骞曚笂娌℃摝骞插噣锛夛紝鎵€浠ュ彧鑳?*鏁板睆骞曚笂鐨勫儚绱?*锛?        // 鐢讳竴鏉℃姏鐗╃嚎 鈫?閲忛偅鍧楀ⅷ 鈫?鍒犳帀 鈫?鍐嶉噺鍚屼竴鍧楋紝蹇呴』涓€涓儚绱犻兘涓嶅墿銆?        // 涓ゆ潯璺兘瑕佽蛋涓€閬嶏細
        //   鈶?鐩存帴鐢诲畬灏卞垹锛?        //   鈶?**鏀硅繃涓€娆″舰鐘朵箣鍚庡啀鍒?*鈥斺€旀敼鍑犱綍璧扮殑鏄彟涓€鏉″姩浣滆矾寰?        //      锛堣 SetStrokeGeometryAction锛氬畠蹇呴』椤烘墜閲嶇畻 Bounds锛?        //       鍚﹀垯 WorldBounds 浼氭嬁鏃ф鍘诲鏂版洸绾裤€佹摝涓嶅共鍑€锛夈€?        Console.WriteLine("  -- G. 鍒犻櫎涔嬪悗涓嶇暀娈嬪奖锛堟暟灞忓箷鍍忕礌锛?-");
        {
            var ink2 = new Color4(1f, 0f, 1f, 1f);
            Host.Commands.SetColor(ink2);
            // 閲?杩欐潯鏇茬嚎澧ㄨ抗妗嗗啀寰€澶?60 鍍忕礌"閭ｄ竴鍧楅噷鐨勫搧绾㈠儚绱犮€?            // 澶氱暀 60 鏄洜涓烘搷浣滄潯灏辨寕鍦ㄦ涓嬫柟鈥斺€旈偅鏉℃畫褰变篃鍦ㄨ繖涓€鍧楅噷銆?            int InkAround(Stroke s)
            {
                var b = s.PaddedBounds;
                return ScreenProbe.CountMagenta((int)b.MinX - 12, (int)b.MinY - 12,
                                                (int)(b.MaxX - b.MinX) + 24,
                                                (int)(b.MaxY - b.MinY) + 84);
            }
            // 鎶涚墿绾挎槸**涓€绗旀嫋**锛堥《鐐?= 鎸変笅閭ｄ釜鐐癸紝鐓?InkClass 鐨?`case 20/21`锛夛細
            // 鎸変綇椤剁偣鎷栧埌"鏇茬嚎缁忚繃鐨勭偣"锛屾澗鎵嬪氨鎴愩€?            // 缁忚繃鐐瑰彇鍙充笂鏂?`(gw, 鈭抔w)`锛歚s = t = gw` 鈫?`p = gw虏/(2gw) = gw/2`銆?            void DragParabola(float gx, float gy, float gw)
            {
                SendMouse((int)gx, (int)gy, 0);                             SettleFrames(60);
                SendMouse((int)gx, (int)gy, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
                SendMouse((int)(gx + gw), (int)(gy - gw), 0);               SettleFrames(240);
                SendMouse((int)(gx + gw), (int)(gy - gw), Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(240);
            }

            for (int round = 0; round < 2; round++)
            {
                bool flipFirst = round == 1;
                Doc.Clear();
                Doc.ClearHistory();
                SetToolFromUi(Tool.Parabola);
                float gx = _virtualX + 700f, gy = _virtualY + 1250f;
                DragParabola(gx, gy, 400f);             // p = 200锛堟洸绾垮崐瀹?300銆佹部杞?225锛?                var g = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
                if (g == null) { Check("鈶?鎶涚墿绾挎病鐢诲嚭鏉ワ紙鍚庨潰涓ら」娌℃硶楠岋級", false, "瀵硅薄 0 涓?); break; }

                // 鈶?閭ｄ竴杞細鍏?*鏀逛竴涓嬭繖鏉℃洸绾?*鍐嶆挙閿€鈥斺€旇蛋涓€閬嶅拰"鐢诲畬灏卞垹"涓嶅悓鐨勫姩浣滆矾寰勩€?                //
                // 鈿?杩欓噷鍘熸潵鏄?鎷栨姏鐗╃嚎鐨?Rim 鎵嬫焺鏀?p"锛?*2026-09-20 绗叚鎵规妸鍥涚鏇茬嚎鐨?                //   鐗规畩鎵嬫焺鍏ㄧ爫浜?*锛堟敼璧伴€氱敤妗嗭級锛岄偅涓墜鏌勫悕褰撳満澶辨晥銆佽繖涓€杞氨闈欓粯閫€鍖栨垚
                //   round 0 鐨勫鍒跺搧锛堟柇瑷€鐓ф牱缁匡級鈥斺€旀鏄?鍐欐鐨勬墜鏌勫悕浼氶殢鍔熻兘绉讳綅鑰屽け鏁?
                //   閭ｆ潯鏁欒銆傜幇鍦ㄦ敼鎷?*閫氱敤妗嗙殑鍙充笅瑙?*锛堢缉鏀撅級锛氬畠鏀圭殑鏄?`Transform`锛?                //   璧?`SetStrokeTransformAction`锛屼笉鏄嚑浣曢偅鏉¤矾銆?                //   鍝ぉ鐪熸妸鏇茬嚎鎵嬫焺鍔犲洖鏉ワ紝杩欓噷涔熻璺熺潃鏀瑰洖鏉ャ€?                if (flipFirst)
                {
                    Doc.SelectOnly(new[] { g });
                    SettleFrames(60);
                    var gf = SelectionHandles.FrameOf(Doc.Selected);
                    var gc = SelectionHandles.CanvasPosition(SelHandle.BottomRight, gf, DpiScale);
                    bool got = SelectionGestureForTest(gc.X, gc.Y);
                    UpdateSelectionGestureForTest(gc.X + 120f, gc.Y + 80f);
                    SettleFrames(80);
                    EndSelectionGestureForTest();
                    SettleFrames(140);
                    // 鍏堣瘉鏄?鐪熺殑鏀逛簡"鈥斺€斾笉鐒朵笅闈㈤偅鏉?鏀硅繃褰㈢姸涔嬪悗鍒犻櫎"鏄亣鐨?                    Check("鈶?绗簩杞細鎷栭€氱敤妗嗙湡鐨勫姩浜嗚繖鏉℃姏鐗╃嚎锛堜笉鐒朵笅闈㈤偅鏉℃柇瑷€鏄亣鐨勶級",
                          got && !g.Transform.IsIdentity,
                          $"鎺ヤ綇={got}锛屽彉鎹?{(g.Transform.IsIdentity ? "鍗曚綅锛堟病鍔級" : "鍙樹簡")}");
                    Doc.Undo();                     // 鎾ら攢閭ｄ竴娆℃敼鍔紙鍐嶈蛋涓€閬嶅啓鍥炶矾寰勶級
                    SettleFrames(140);
                    Doc.SelectOnly(new[] { g });
                    SettleFrames(120);
                }

                Doc.SelectOnly(new[] { g });
                SettleFrames(120);
                int before = InkAround(g);
                RunBarActionForTest((int)SelBarButton.Delete);
                SettleFrames(300);
                int after = InkAround(g);
                Check(flipFirst
                        ? "鈶?鏀硅繃褰㈢姸涔嬪悗鍒犻櫎锛氶偅鍧楀湴鏂逛竴涓ⅷ鐐归兘涓嶅墿锛堟畫褰憋級"
                        : "鈶?鐩存帴鍒犻櫎锛氶偅鍧楀湴鏂逛竴涓ⅷ鐐归兘涓嶅墿锛堟畫褰憋級",
                      before > 200 && after == 0,
                      $"鍒犲墠 {before} 鍍忕礌 鈫?鍒犲悗 {after}锛堟柟鍚?{(flipFirst ? "鎹㈣繃" : "娌℃崲")}锛?);
            }
        }

        // ============ 鈶?鍙屾洸绾?*涓ょ瑪鎷栧姩寮?* ============
        //
        // 鍙ｅ緞锛堢敤鎴?2026-09-20 瀹氾細"浠栫殑鍙屾洸绾挎槸鎷栧姩涓ゆ鍚楋紵鎸夌収浠栫殑杩欎釜瑙勫垯澶嶅埢"锛夛細
        // **涓€绗?= 鎸変笅-鎷?鏉炬墜锛屾澗鎵嬫帹杩涗笅涓€绗?*鈥斺€旂収 InkClass 鐨?        // `drawMultiStepShapeCurrentStep` 锛?MouseUp 鎺ㄨ繘锛坄MW_ShapeDrawing.cs:1814-1822`锛夛細
        //   绗?1 绗?鈫?**浠庝腑蹇冩嫋鍑烘笎杩戠嚎**锛堟嫋鍒板摢灏辨槸鍝級鈫?鏉炬墜**閿佷綇**锛屾鍚庝笉鍐嶅彉锛?        //   绗?2 绗?鈫?**鍐嶆嫋涓€涓?*锛氭嫋鍒板摢銆?*鏇茬嚎灏辩粡杩囧摢**锛堜腑蹇冩部鐢ㄧ 1 绗旈偅涓紝
        //             InkClass 鐨勭浜岀瑪鍒绘剰涓嶉噸璁?`iniP`锛宍:1971-1977`锛夛紱
        // 涓€旀澗鎵?*涓嶈繘鏂囨。**锛堝崐鎴愬搧鐣欏湪 ActiveStroke锛夛紝涓ょ瑪鍚堣捣鏉?*鍙湁涓€鏉℃挙閿€璁板綍**銆?        //
        // 鍜屼笂涓€鐗堬紙涓変笅鐐瑰嚮 + 涓棿鎮仠锛夌殑鍖哄埆锛?*鍑犱綍鍙湪鎸変綇鎷栧姩鏃舵洿鏂?*锛屾寚閽堜笉鎸夐敭鏃?        // 鍗婃垚鍝佷竴鍔ㄤ笉鍔紙InkClass 灏辨槸杩欐牱锛夆€斺€旇繖涔熸槸瑙︽懜灞忚兘鐢ㄧ殑鏍瑰瓙锛氭墜鎸囨病鏈?鎮仠"锛?        // 鑰屾瘡涓€姝ユ湰鏉ュ氨鏄?鎸変綇鎷?鏉炬墜"锛岀瑪 / 榧犳爣 / 鎵嬫寚璧扮殑鏄悓涓€鏉¤矾銆?        Console.WriteLine("  -- H. 鍙屾洸绾夸袱绗旀嫋鍔ㄥ紡锛堜粠涓績鎷栧嚭娓愯繎绾?鈫?鍐嶆嫋涓€涓嬪畾鏇茬嚎锛?-");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Hyperbola);
            float hpx = _virtualX + 900f, hpy = _virtualY + 1300f;

            // 涓€娆?鎸変綇鎷?锛氭寜涓?鈫?鎸埌鐩爣 鈫?鎶捣銆?            void Drag(float x0, float y0, float x1, float y1)
            {
                SendMouse((int)x0, (int)y0, 0);                            SettleFrames(60);
                SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
                SendMouse((int)x1, (int)y1, 0);                            SettleFrames(240);
                SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(180);
            }

            // 绗?1 绗旓細浠庝腑蹇?(hpx,hpy) 鎷栧埌鍙充笅鏂?(hpx+400, hpy+160) 鈫?娓愯繎绾挎 A = 400銆丅 = 160
            //锛堟枩鐜?0.4锛屽拰 InkClass 鐨?`k = |dy/dx|` 鍚屼竴涓級銆?            SendMouse((int)hpx, (int)hpy, 0);                            SettleFrames(60);
            SendMouse((int)hpx, (int)hpy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            Check("涓ょ瑪寮忥細绗?1 绗旀寜涓嬩箣鍚?*杩樻病鎻愪氦**锛堝崐鎴愬搧涓嶈繘鏂囨。锛?,
                  Doc.Strokes.Count == 0, $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 0锛?);

            SendMouse((int)(hpx + 200f), (int)(hpy + 80f), 0);   SettleFrames(60);
            SendMouse((int)(hpx + 400f), (int)(hpy + 160f), 0);  SettleFrames(240);
            Check("涓ょ瑪寮忥細绗?1 绗旀嫋鍔ㄤ腑鏄?*鍙敾娓愯繎绾?*鐨勯樁娈碉紙鐘舵€侊級",
                  HyperAsymptotePreviewOnly && ActiveStroke != null,
                  $"鍙敾娓愯繎绾?{HyperAsymptotePreviewOnly}锛屽崐鎴愬搧={(ActiveStroke != null)}");
            // 骞朵笖瑕佸湪灞忓箷涓婄湅寰楄杩欐潯宸埆锛氭洸绾夸笂鏈変竴灏忓潡**娓愯繎绾挎牴鏈笉浼氱粡杩?*鐨勫湴鏂?            //锛堜腑蹇冨彸涓嬫柟 205..260 脳 30..70锛屾笎杩戠嚎鍦ㄩ偅鍎挎槸 y = 0.4x 鈭?[82, 104]锛夈€?            int inkAux = ScreenProbe.CountMagenta((int)hpx + 205, (int)hpy + 30, 55, 40);

            // 鏉炬墜 = **绗?1 绗斿畬鎴?*锛氭笎杩戠嚎閿佷綇锛屼絾瀵硅薄**杩樹笉杩涙枃妗?*锛堣繕鏈夌 2 绗旓級銆?            SendMouse((int)(hpx + 400f), (int)(hpy + 160f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
            Check("涓ょ瑪寮忥細绗?1 绗旀澗鎵嬩箣鍚?*浠嶇劧娌℃彁浜?*锛堣繕鏈夌 2 绗旓級",
                  Doc.Strokes.Count == 0, $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 0锛?);

            // **鏉炬墜鍚庢檭榧犳爣锛堜笉鎸夐敭锛夛細鍗婃垚鍝佷竴鍔ㄤ笉鍔?*鈥斺€旇繖鏄繖涓€鐗堝拰涓婁竴鐗堟渶澶х殑鎵嬫劅宸埆锛?            // 涔熸槸瑙︽懜灞忎笂"娌℃湁鎮仠"杩欎欢浜嬭兘鎴愮珛鐨勫師鍥犮€?            SendMouse((int)(hpx + 900f), (int)(hpy + 600f), 0);   SettleFrames(240);
            Check("涓ょ瑪寮忥細鏉炬墜鍚?*鏅冮紶鏍囦笉鎸夐敭**锛屾笎杩戠嚎妗嗕竴鍔ㄤ笉鍔紙A 400銆丅 160锛夛紙卤0.5锛?,
                  ActiveStroke != null
                  && Near(ActiveStroke.HyperbolaALocal(), 400f, .5f)
                  && Near(ActiveStroke.HyperbolaBLocal(), 160f, .5f),
                  ActiveStroke == null ? "鍗婃垚鍝佹病浜?
                      : $"A {ActiveStroke.HyperbolaALocal():F1}  B {ActiveStroke.HyperbolaBLocal():F1}");

            // 绗?2 绗旓細**鍐嶆嫋涓€涓?*鈥斺€斿厛鎺犺繃涓€涓?鑳借瘉鏄庢洸绾垮嚭鏉ヤ簡"鐨勪綅缃?(220, 37)锛?            // 鍐嶆嫋鍒版渶缁堣惤鐐?(500, 150)锛堜腑蹇冨彸涓嬫柟銆佹瘮娓愯繎绾挎洿妯級
            // 鈫?鏇茬嚎**姝ｅソ缁忚繃杩欎釜鐐?*锛屾湞鍚?= 鐒︾偣鍦?x 杞达紙宸﹀彸鍙屾洸绾匡級銆?            SendMouse((int)hpx, (int)hpy, 0);                            SettleFrames(60);
            SendMouse((int)hpx, (int)hpy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            // 鍏堝仠鍦?(220,37)锛氭洸绾挎鍒绘濂界┛杩囬偅涓€灏忓潡锛坅 鐢辫繖涓偣鍙嶈В 鈮?200锛夛紝
            // 浜庢槸"鍚屼竴鍧楀湴鏂?鐨勫ⅷ浠?0 鍙樻垚"鏈夋洸绾?鈥斺€旇瘉鏄庢洸绾挎槸**绗?2 绗旀墠鍑烘潵**鐨勩€?            SendMouse((int)(hpx + 220f), (int)(hpy + 37f), 0);           SettleFrames(240);
            int inkCurve = ScreenProbe.CountMagenta((int)hpx + 205, (int)hpy + 30, 55, 40);
            Check("涓ょ瑪寮忥細鏇茬嚎鏄?*绗?2 绗旀嫋鍔ㄦ椂鎵嶅嚭鐜?*鐨勶紙鍚屼竴鍧楀湴鏂癸細0 澧?鈫?鏈夋洸绾匡級",
                  inkAux == 0 && inkCurve > 50, $"鍙嚭娓愯繎绾挎椂 {inkAux} 鍍忕礌 鈫?鍑烘洸绾垮悗 {inkCurve} 鍍忕礌");
            // 鍐嶆嫋鍒版渶缁堣惤鐐癸紱鎸埌涓€鍗婃椂鏇茬嚎涔熻窡鐫€璧帮紙鍚﹀垯鏉炬墜浼氳烦锛夈€?            SendMouse((int)(hpx + 500f), (int)(hpy + 150f), 0);          SettleFrames(240);
            Check("涓ょ瑪寮忥細绗?2 绗旀嫋鍔ㄤ腑**浠嶇劧娌℃彁浜?*锛堟澗鎵嬫墠绠楀畬锛?,
                  Doc.Strokes.Count == 0, $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 0锛?);

            // 绗?2 绗旀澗鎵?= 瀹氱锛歛 鐢遍偅涓偣鍙嶈В 鈥斺€?a虏 = 500虏 鈭?150虏/0.4虏 = 109375銆?            float k3 = 0.4f, dx3 = 500f, dy3 = 150f;
            float wantA = MathF.Sqrt(dx3 * dx3 - dy3 * dy3 / (k3 * k3));    // 鈮?330.72
            float wantB = wantA * k3;                                       // 鈮?132.29
            SendMouse((int)(hpx + dx3), (int)(hpy + dy3), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);

            var hyp = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("涓ょ瑪寮忥細绗?2 绗旀澗鎵嬩箣鍚庡璞℃墠鍑烘潵锛堜竴鏉″弻鏇茬嚎銆佸疄杞存部 x锛?,
                  hyp != null && hyp.Kind == StrokeKind.Hyperbola
                  && hyp.CurveAxis == CurveAxis.TransverseX,
                  hyp == null ? $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 1锛?
                              : $"Kind={hyp.Kind} 鏈濆悜={hyp.CurveAxis}");
            if (hyp == null) { Console.WriteLine("  锛堝悗闈㈠嚑椤规病娉曢獙锛?); }
            else
            {
                Check("涓ょ瑪寮忥細鍗婅酱鐢遍偅涓偣鍙嶈В 鈥斺€?瀹炲崐杞?330.7銆佽櫄鍗婅酱 132.3锛埪?锛?,
                      Near(hyp.HyperbolaRealLocal(), wantA, 2f)
                      && Near(hyp.HyperbolaImagLocal(), wantB, 2f),
                      $"瀹炲崐杞?{hyp.HyperbolaRealLocal():F1}锛堟湡鏈?{wantA:F1}锛夈€?
                      + $"铏氬崐杞?{hyp.HyperbolaImagLocal():F1}锛堟湡鏈?{wantB:F1}锛?);
                // 杩欎竴姝ョ殑**鏍稿績鏂█**锛氭妸閭ｄ釜鐐逛唬杩涙爣鍑嗗紡锛屽繀椤?*姝ｅソ绛変簬 1**锛堟洸绾跨┛杩囧畠锛夈€?                // 寮忓瓙閲岃鐢?*鏇茬嚎鑷繁鐨?* x / y 鏂瑰悜绯绘暟锛坄HyperbolaCurveALocal/BLocal`锛夛紝
                // **涓嶆槸**娓愯繎绾挎鐨?`HyperbolaALocal/BLocal`锛堥偅涓や釜鏄锛屾洸绾块殢鏃跺彲鑳芥瘮瀹冨皬锛夈€?                float fit = dx3 * dx3 / (hyp.HyperbolaCurveALocal() * hyp.HyperbolaCurveALocal())
                          - dy3 * dy3 / (hyp.HyperbolaCurveBLocal() * hyp.HyperbolaCurveBLocal());
                Check("涓ょ瑪寮忥細鏇茬嚎**姝ｅソ缁忚繃閭ｄ釜鐐?*锛坸虏/a虏 鈭?y虏/b虏 = 1锛夛紙卤0.01锛?,
                      Near(fit, 1f, .01f), $"浠ｅ叆寰?{fit:F4}锛堟湡鏈?1锛?);
                Check("涓ょ瑪寮忥細**娓愯繎绾挎枩鐜囨病鍔?*锛堣繕鏄?0.4锛夛紙卤0.005锛?,
                      Near(hyp.HyperbolaBLocal() / hyp.HyperbolaALocal(), k3, .005f),
                      $"{hyp.HyperbolaBLocal() / hyp.HyperbolaALocal():F4}锛堟湡鏈?{k3:F4}锛?);
                // **杩欎竴鐗堢殑姝ｉ**锛氱 2 绗?瀹氭洸绾?涓嶈纰扮 1 绗旈攣浣忕殑閭ｄ釜妗?                //锛堢敤鎴峰師璇濓細"娓愯繎绾跨敾濂戒互鍚庡ぇ灏忓畬鍏ㄤ笉鍔紝闀垮害涔熶笉鍔?锛夈€?                Check("涓ょ瑪寮忥細**娓愯繎绾挎涓€鍔ㄩ兘娌″姩**锛圓 400銆丅 160 鈥斺€?灏辨槸绗?1 绗旀嫋鍑烘潵鐨勯偅涓級锛埪?.5锛?,
                      Near(hyp.HyperbolaALocal(), 400f, .5f) && Near(hyp.HyperbolaBLocal(), 160f, .5f),
                      $"A {hyp.HyperbolaALocal():F1}锛堟湡鏈?400锛? B {hyp.HyperbolaBLocal():F1}锛堟湡鏈?160锛?);
                Check("涓ょ瑪寮忥細涓ょ瑪 = **涓€鏉?*鎾ら攢璁板綍锛堜笉鏄袱鏉★級",
                      Doc.UndoDepth == 1, $"鎾ら攢鏍?{Doc.UndoDepth} 姝?);
                Doc.Undo();
                Check("涓ょ瑪寮忥細鎾ら攢涓€姝?鈫?鏁存潯娑堝け",
                      Doc.Strokes.Count == 0, $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 0锛?);
            }

            // ---- 鎹㈠伐鍏?= 鍗婃垚鍝佷綔搴燂細閭ｆ潯鐢讳簡涓€鍗婄殑蹇呴』**褰撳満涓㈡帀** ----
            //
            // 涓轰粈涔堝€煎緱鍗曠嫭楠岋細閭ｅ崐鏉″弻鏇茬嚎**涓嶅湪鏂囨。閲?*锛屽嵈涓€鐩磋娓叉煋锛圓ctiveStroke 鏄?            // 鏃犳潯浠剁敾鐨勶級銆備笉涓㈢殑璇濆畠浼氭案杩滄寕鍦ㄥ睆骞曚笂锛岃€屼笖**姗＄毊涔熸摝涓嶆帀**鈥斺€斿畠鏍规湰涓嶆槸
            // 鏂囨。閲岀殑瀵硅薄銆傛墍浠ヨ繖閲岀敤"鏁板睆骞曚笂鐨勫ⅷ"鏉ラ獙锛岃€屼笉鏄暟瀵硅薄涓暟銆?            SetToolFromUi(Tool.Hyperbola);
            float qx = _virtualX + 1500f, qy = _virtualY + 1300f;
            // 绗?1 绗旀嫋鍒?(200, 80)锛氭笎杩戠嚎妗?A = 200銆丅 = 80锛堣櫄绾垮氨鐢诲埌 卤A銆伮盉锛夈€?            SendMouse((int)qx, (int)qy, 0);                            SettleFrames(60);
            SendMouse((int)qx, (int)qy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)(qx + 200f), (int)(qy + 80f), 0);           SettleFrames(240);
            int inkBefore = ScreenProbe.CountMagenta((int)qx - 260, (int)qy - 110, 520, 220);
            SendMouse((int)(qx + 200f), (int)(qy + 80f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
            SetToolFromUi(Tool.Line);                             // 鎹㈠伐鍏?鈫?浣滃簾
            SettleFrames(360);
            int inkAfter = ScreenProbe.CountMagenta((int)qx - 260, (int)qy - 110, 520, 220);
            Check("涓ょ瑪寮忥細鎹㈠伐鍏锋妸鍗婃垚鍝?*涓㈡帀**锛堝睆骞曚笂涓嶇暀鐥曪級",
                  inkBefore > 200 && inkAfter == 0,
                  $"鎹㈠伐鍏峰墠 {inkBefore} 鍍忕礌 鈫?鍚?{inkAfter}");
            SetToolFromUi(Tool.Hyperbola);

            // ---- 鎹㈠伐鍏蜂箣鍚庨噸鏂扮敾 = **鏂扮殑涓€鏉?*锛堜笉鏄帴鐫€涓婁竴鏉＄殑澶у皬鏀癸級----
            // 绗?1 绗旓細浠?(qx,qy) 鎷栧嚭娓愯繎绾挎锛圓 = 200銆丅 = 80锛夛紱
            // 绗?2 绗旓細鎷栧埌 (300, 0)锛?*鍦ㄦ鍐?*銆佷笖鍦ㄥ疄杞翠笂锛夆啋 瀹炲崐杞村氨姝ｅソ鏄?300銆?            Doc.ClearHistory();
            Drag(qx, qy, qx + 200f, qy + 80f);                    // 绗?1 绗旓細娓愯繎绾?            Drag(qx, qy, qx + 300f, qy);                          // 绗?2 绗旓細鏇茬嚎杩?(300,0)
            Check("涓ょ瑪寮忥細鎹㈣繃宸ュ叿涔嬪悗锛屽啀鐢讳竴鏉℃槸**鏂板璞?*锛堜笉鏄敼涓婁竴鏉★級",
                  Doc.Strokes.Count == 1 && Near(Doc.Strokes[0].HyperbolaRealLocal(), 300f, 6f),
                  $"瀵硅薄 {Doc.Strokes.Count} 鏉★紝瀹炲崐杞?"
                  + $"{(Doc.Strokes.Count > 0 ? Doc.Strokes[0].HyperbolaRealLocal() : -1f):F1}锛堟湡鏈?1 鏉?/ 300锛?);

            // ---- 绗?2 绗旀嫋鍒?鏇寸珫"鐨勪綅缃?鈫?**涓婁笅鍙屾洸绾?*锛堢劍鐐瑰湪 y 杞达級----
            //
            // 杩欎竴鏉℃鏄敤鎴疯鐨?濡傛灉鎴戦€夌殑鏄乏鍙筹紝灏辨槸宸﹀彸锛涘鏋滄槸涓婁笅锛屽氨鏄笂涓?锛?            // 娓愯繎绾挎枩鐜?m = 480/240 = 2锛岀 2 绗旀嫋鍒?(200, 500) 姣旀笎杩戠嚎**鏇寸珫**
            //锛坢路|dx| = 400 < 500锛夆啋 鐒︾偣钀藉湪 y 杞达紱瀹炲崐杞寸敤**瀹炶酱娌?y**鐨勫紡瀛愬弽瑙ｏ細
            // a = 鈭?500虏路(240/480)虏 鈭?200虏) = 鈭?62500 鈭?40000) = 150銆乥 = a 脳 2 = 300銆?            // 鎶ュ嚭鏉ョ殑"瀹炲崐杞?灏辨槸 b = 300锛堝疄杞存部 y 鏃跺疄鍗婅酱鏄旱鍚戦偅涓紝瑙?HyperbolaRealLocal锛夈€?            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Hyperbola);
            float ux = _virtualX + 1500f, uy = _virtualY + 1300f;
            Drag(ux, uy, ux + 240f, uy + 480f);                   // 绗?1 绗旓細娓愯繎绾匡紙m = 2锛?            Drag(ux, uy, ux + 200f, uy + 500f);                   // 绗?2 绗旓細鏇寸珫 鈫?鐒︾偣鍦?y 杞?
            var hypY = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("涓ょ瑪寮忥細绗?2 绗旀嫋鍒版洿绔栫殑浣嶇疆 鈫?**涓婁笅鍙屾洸绾?*锛堝疄杞存部 y锛夛紝鍗婅酱 300 / 150锛埪?锛?,
                  hypY != null && hypY.CurveAxis == CurveAxis.TransverseY
                  && Near(hypY.HyperbolaRealLocal(), 300f, 2f)
                  && Near(hypY.HyperbolaImagLocal(), 150f, 2f),
                  hypY == null ? $"瀵硅薄 {Doc.Strokes.Count} 鏉★紙鏈熸湜 1锛?
                               : $"{hypY.CurveAxis}锛屽疄鍗婅酱 {hypY.HyperbolaRealLocal():F1}銆?
                                 + $"铏氬崐杞?{hypY.HyperbolaImagLocal():F1}");
            if (hypY != null)
            {
                // 鍚屾牱瑕佺敤**鏇茬嚎鐨?* x / y 鏂瑰悜绯绘暟锛堜笉鏄笎杩戠嚎妗嗙殑 A / B锛夈€?                float fitY = 500f * 500f / (hypY.HyperbolaCurveBLocal() * hypY.HyperbolaCurveBLocal())
                           - 200f * 200f / (hypY.HyperbolaCurveALocal() * hypY.HyperbolaCurveALocal());
                // 瀹瑰樊姣斾笂闈㈤偅鏉″锛氶紶鏍囧潗鏍囨槸**鍙栨暣**鐨勶紝涓€涓儚绱犵殑鍋忓樊鍦?a 鍙湁 150 鏃?                // 鐩稿璇樊鏄?a=330 鏃剁殑涓ゅ€嶅锛堜笂闈㈤偅鏉?卤0.01 灏卞锛岃繖鏉¤鏀惧鍒?卤0.05锛夈€?                Check("涓ょ瑪寮忥細涓婁笅鍙屾洸绾夸篃**姝ｅソ缁忚繃閭ｄ釜鐐?*锛坹虏/b虏 鈭?x虏/a虏 = 1锛夛紙卤0.05锛?,
                      Near(fitY, 1f, .05f), $"浠ｅ叆寰?{fitY:F4}锛堟湡鏈?1锛?);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  鍚堣 {pass + fail} 椤癸細閫氳繃 {pass}锛屽け璐?{fail}");
        Console.WriteLine();
        _quit = true;
    }


    /// <summary>
    /// `--wetdrytest`锛?*婀垮ⅷ锛堟姮绗斿墠锛変笌骞插ⅷ锛堟姮绗斿悗锛夋帴涓嶆帴寰椾笂**锛?026-09-28 瀹氱殑绗竴鏉¤皟鏌ワ級銆?    ///
    /// 鑳屾櫙锛氱湡绗?鍚堟垚绗斿啓瀛楁椂锛屽睆骞曚笂閭ｄ竴绗旀槸 **DWM 濮旀墭杞ㄨ抗**鐢荤殑锛涙姮绗旈偅涓€鍒绘崲鎴?    /// 鎴戜滑鑷繁鐢荤殑 `ID2D1Ink`銆備袱杈瑰彧瑕?*绮楃粏**鎴?*涓績绾?*宸竴鐐癸紝姣忎竴绗旀敹灏惧氨浼?    /// "杞昏交娌変竴涓?鈥斺€旇繖姝ｆ槸"璇翠笉娓呬絾灏辨槸宸竴鐐?鐨勫ご鍙峰珜鐤戙€?    ///
    /// 鍒ゆ嵁锛氭寜浣忎笉鏀剧殑鏃跺€欏厛鎷嶄竴寮狅紙绗斿皷鍚?100~260px 閭ｄ竴娈碉紝鑲畾宸茬粡琚建杩圭敾涓婏級锛?    /// 鎶瑪銆佺瓑杞ㄨ抗鏀舵帀涔嬪悗鍐嶆媿鍚屼竴鍧楋紝閫愬垪姣?*澧ㄥ**鍜?*涓績绾?*銆?    /// 鍏堣姹?婀垮ⅷ閭ｅ紶鐪熺殑鏈夊ⅷ"锛屽惁鍒?娌＄敾鍑烘潵"浼氳璇垽鎴?娌℃湁宸埆"銆?    /// </summary>
    private void WetDryTest(bool live, double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 婀垮ⅷ / 骞插ⅷ 浜ゆ帴娴嬮噺锛堢湡绗旓級===");
        Console.WriteLine($"  濮旀墭澧ㄨ抗杞ㄨ抗: 鍙敤={OverlayWindow.InkTrailAvailable}锛屽惎鐢?{OverlayWindow.InkTrailEnabled}"
                          + $"锛坽OverlayWindow.InkTrailNote}锛?);

        if (!live)
        {
            Console.WriteLine();
            Console.WriteLine("  杩欐潯**鍙兘鎷跨湡绗旂敾**锛氬悎鎴愮瑪涓嶄細琚?DWM 鐢绘垚婀垮ⅷ锛?-wetinktest 瀹炴祴濡傛锛夛紝");
            Console.WriteLine("  鎷垮悎鎴愮瑪閲忓埌鐨勨€滄箍澧ㄢ€濆叾瀹炴槸鎴戜滑鑷繁鐢荤殑閭ｄ竴绗旓紝姣斿嚭鏉ュ繀鐒朵竴鏍凤紙鍋囩豢锛夈€?);
            Console.WriteLine("  鐢ㄦ硶锛氭嬁鎵嬪啓绗斿湪灞忓箷涓棿閭ｆ潯娴呯伆绾夸箣闂寸敾涓€鏉℃í绾匡紙鍘嬪姏浠庤交鍒伴噸锛夛紝鐒跺悗鏉炬墜锛?);
            Console.WriteLine("      InkTeach.exe --wetdrytest --live 20");
            Console.WriteLine();

            // 骞茶窇鏍￠獙锛氳瘉鏄庨噰闆嗙紦鍐叉槸澶嶇敤鐨勩€傜涓€鐗堟瘡娆℃姄灞忛兘 new 2MB锛堣繘 LOH锛夛紝
            // 涓€娆＄瑪鐢昏兘鍒峰嚭 64 娆?gen2 鈥斺€?閭ｉ噺鐨勬槸宸ュ叿鑷繁锛屼笉鏄骇鍝併€傝繖鏉¤鐩綇銆?            int tw = Math.Min(1400, _virtualW - 200), th = Math.Min(360, _virtualH - 200);
            var buf = new byte[tw * th * 4];
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            int c0 = GC.CollectionCount(0), c2 = GC.CollectionCount(2);
            int shots = 0;
            for (int i = 0; i < 75; i++)
            {
                if (ScreenProbe.CaptureRegionInto(buf, _virtualX + 50, _virtualY + 50, tw, th)) shots++;
                SettleFrames(1);
            }
            long used = GC.GetAllocatedBytesForCurrentThread() - a0;
            Console.WriteLine($"  骞茶窇鏍￠獙锛氭姄 {shots} 娆★紙{tw}脳{th}锛?*澶嶇敤鍚屼竴鍧楃紦鍐?*锛夊伐鍏疯嚜韬垎閰?{used / 1024.0:F0} KB锛?
                              + $"GC {GC.CollectionCount(0) - c0}/{GC.CollectionCount(2) - c2}"
                              + "鈥斺€旀帴杩?0 鎵嶇畻宸ュ叿骞插噣");
            _quit = true; return;
        }
        if (!OverlayWindow.InkTrailEnabled || !OverlayWindow.InkTrailAvailable)
        {
            Console.WriteLine("  SKIP: 娌℃湁濮旀墭杞ㄨ抗锛岃繖鏉℃祴閲忔病鏈夋剰涔夆€斺€斿畠姣旂殑灏辨槸"
                              + "鈥滅郴缁熺敾鐨勬箍澧ㄢ€濅笌鈥滄垜浠敾鐨勫共澧ㄢ€?);
            _quit = true; return;
        }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        Doc.InvalidateAll();

        int bandW = Math.Min(1400, _virtualW - 200);
        int bandH = Math.Min(360, _virtualH - 200);
        int bandX = _virtualX + (_virtualW - bandW) / 2;
        int bandY = _virtualY + (_virtualH - bandH) / 2;
        float gy = bandY + bandH * 0.5f;

        // 娴呯伆寮曞绾匡紙**鏁呮剰娴呭埌杩涗笉浜嗗ⅷ鐨勫垽鎹?*锛氱櫧搴曞垽鎹槸 RGB 涔嬪拰 < 600锛?        // 0.85 鐏版槸 651锛屼笉浼氳褰撴垚澧級銆備袱绔姞涓や釜灏忕珫鏍囪锛屽憡璇夌敤鎴风敾鍦ㄥ摢鍎裤€?        var guide = new Color4(0.85f, 0.86f, 0.88f, 1f);
        void Guide(float x1, float y1, float x2, float y2)
        {
            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = guide, Width = 2f * DpiScale };
            s.AddPoint(x1, y1, 0.5f, 0);
            s.AddPoint(x2, y2, 0.5f, 0);
            Doc.AddStroke(s);
        }
        Guide(bandX + 120f, gy, bandX + bandW - 120f, gy);
        Guide(bandX + 120f, gy - 40f, bandX + 120f, gy + 40f);
        Guide(bandX + bandW - 120f, gy - 40f, bandX + bandW - 120f, gy + 40f);
        SettleFrames(150);

        Console.WriteLine();
        Console.WriteLine($"  璇峰湪涓ゆ潯**绔栨爣璁颁箣闂?*鐢ㄧ瑪浠庡乏鐢诲埌鍙筹紙鍘嬪姏浠庤交鍒伴噸锛夛紝{seconds:F0} 绉掑唴銆佷竴绗旂敾瀹屻€?);
        Console.WriteLine("  鏉炬墜鍚庝細鑷姩鍑虹粨鏋溿€?);
        Console.WriteLine();

        byte[] wetBuf = new byte[bandW * bandH * 4];
        byte[] dryBuf = new byte[bandW * bandH * 4];
        bool gotWet = false;
        double wetAt = 0, lastCap = 0, t0 = NowMs;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            if (_dirty || _drawing) { if (OverlayWindow.VBlankPaced) Native.DwmFlush(); RenderAll(); _dirty = false; }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (ActiveStroke != null)
            {
                // 绗旇繕鎺ヨЕ鐫€锛氳繖涓€寮犲氨鏄?*鐢ㄦ埛褰撲笅鐪嬪埌鐨勬箍澧?*锛堝鎵樿建杩?鈭?鎴戜滑鑷繁鐢荤殑閭ｄ竴绗旓級銆?                // 鈿?缂撳啿蹇呴』澶嶇敤锛坄CaptureRegionInto`锛夛細姣忓抚 new 2MB 浼氳繘 LOH锛?                //   瀹炴祴鑳芥妸涓€娆＄瑪鐢诲埛鍑?64 娆?gen2 鈥斺€?閭ｉ噺鐨勬槸娴嬮噺宸ュ叿鑷繁銆?                // 闄愬埌 ~25 娆?绉掞細澶熸嬁鍒?鏉炬墜鍓嶆渶鍚庝竴甯?锛屽張涓嶇粰娴嬮噺鏈韩鍔犺礋鎷呫€?                if (NowMs - lastCap >= 40 &&
                    ScreenProbe.CaptureRegionInto(wetBuf, bandX, bandY, bandW, bandH))
                {
                    gotWet = true;
                    wetAt = NowMs;
                    lastCap = NowMs;
                }
            }
            else if (gotWet && NowMs - wetAt > 300)
            {
                break;                              // 鎶瑪 300ms 浜嗭紝杞ㄨ抗璇ユ敹鎺変簡
            }
        }

        if (!gotWet)
        {
            Console.WriteLine("  FAIL: 鏁翠釜杩囩▼娌℃祴鍒颁换浣曠瑪杩癸紙绗旀帴瑙︽椂鎵嶄細閲囨牱锛?);
            _quit = true; return;
        }
        SettleFrames(400);
        if (!ScreenProbe.CaptureRegionInto(dryBuf, bandX, bandY, bandW, bandH))
        {
            Console.WriteLine("  FAIL: 鎶撳睆澶辫触"); _quit = true; return;
        }
        ScreenProbe.SaveBuffer("reports/wetdry-wet.bmp", wetBuf, bandW, bandH);
        ScreenProbe.SaveBuffer("reports/wetdry-dry.bmp", dryBuf, bandW, bandH);

        var (widthW, centerW) = InkProfile(wetBuf, bandW, bandH);
        var (widthD, centerD) = InkProfile(dryBuf, bandW, bandH);
        int colsWet = 0, colsBoth = 0;
        var dw = new List<float>();
        var dc = new List<float>();
        float wMin = float.MaxValue, wMax = 0f, dMin = float.MaxValue, dMax = 0f;
        for (int i = 0; i < bandW; i++)
        {
            if (widthW[i] > 0) { colsWet++; wMin = MathF.Min(wMin, widthW[i]); wMax = MathF.Max(wMax, widthW[i]); }
            if (widthD[i] > 0) { dMin = MathF.Min(dMin, widthD[i]); dMax = MathF.Max(dMax, widthD[i]); }
            if (widthW[i] > 0 && widthD[i] > 0)
            {
                colsBoth++;
                dw.Add(MathF.Abs(widthW[i] - widthD[i]));
                dc.Add(MathF.Abs(centerW[i] - centerD[i]));
            }
        }
        dw.Sort();
        dc.Sort();
        float Med(List<float> l) => l.Count == 0 ? -1f : l[l.Count / 2];
        float Max(List<float> l) => l.Count == 0 ? -1f : l[^1];

        Console.WriteLine($"  婀垮ⅷ鏈夊ⅷ鐨勫垪锛歿colsWet}/{bandW}锛堟箍澧ㄧ嚎瀹?{wMin:F0}~{wMax:F0}px锛?);
        Console.WriteLine($"  骞插ⅷ鏈夊ⅷ鐨勫垪  绾垮 {dMin:F0}~{dMax:F0}px锛涗袱杈归兘鏈夊ⅷ鐨勫垪 {colsBoth}");
        Console.WriteLine($"  绾垮宸紙婀?vs 骞诧級锛氫腑浣?{Med(dw):F1}px锛屾渶澶?{Max(dw):F1}px");
        Console.WriteLine($"  涓績绾垮亸绉?       锛氫腑浣?{Med(dc):F1}px锛屾渶澶?{Max(dc):F1}px");

        // 娌跨瑪杩规柟鍚戠殑鏈€浣冲榻愪綅绉伙細鎶婂共澧ㄧ殑瀹藉害搴忓垪鐩稿婀垮ⅷ骞崇Щ k锛屽彇"骞冲潎宸渶灏?鐨勯偅涓?k銆?        // 鈿?鍒敤"鏈€鎬ュ彉鍖栧"閭ｇ argmax 鍒ゆ嵁鈥斺€斾袱涓嚑涔庝竴鏍风殑鏇茬嚎锛屾渶楂樺嘲宸?2px 灏变細缈诲埌鍒
        //   锛?026-09-28 瀹炴祴锛氫腑浣嶅樊 0.0px 鐨勪竴鏉＄瑪锛屽畠鎶ュ嚭"宸?+361px"锛岀函灞炲櫔澹帮級銆?        int bestShift = 0;
        float bestErr = float.MaxValue;
        for (int k = -40; k <= 40; k++)
        {
            float sum = 0f;
            int cnt = 0;
            for (int i = 0; i < bandW; i++)
            {
                int j = i + k;
                if (j < 0 || j >= bandW) continue;
                if (widthW[i] <= 0 || widthD[j] <= 0) continue;
                sum += MathF.Abs(widthW[i] - widthD[j]);
                cnt++;
            }
            if (cnt < bandW / 3) continue;
            float err = sum / cnt;
            if (err < bestErr) { bestErr = err; bestShift = k; }
        }
        Console.WriteLine($"  娌跨瑪杩规柟鍚戠殑鏈€浣冲榻愪綅绉伙細{bestShift:+0;-0;0}px"
                          + $"锛堝榻愬悗骞冲潎绾垮宸?{bestErr:F2}px锛?
                          + (Math.Abs(bestShift) <= 2 ? "鈥斺€旀病鏈夋粸鍚? : "鈥斺€斾袱杈瑰湪绗旇抗鏂瑰悜涓婇敊寮€浜?));
        Console.WriteLine("  鍑哄浘锛歳eports/wetdry-wet.bmp / wetdry-dry.bmp锛堜汉鐪煎鐓э級");

        bool ok = colsBoth > bandW / 4 && colsWet > bandW / 4
                  && Med(dw) <= 2.5f && Med(dc) <= 1.5f && Math.Abs(bestShift) <= 2;
        Console.WriteLine(ok
            ? $"  PASS: 婀垮ⅷ鍜屽共澧ㄦ帴寰椾笂锛堢嚎瀹藉樊涓綅 {Med(dw):F1}px銆佷腑蹇冪嚎涓綅 {Med(dc):F1}px銆?
              + $"瀵归綈浣嶇Щ {bestShift}px锛?
            : colsWet <= bandW / 4
                ? "  FAIL: 婀垮ⅷ閭ｅ紶鍑犱箮娌℃湁澧ㄢ€斺€旇建杩规病鐢诲嚭鏉ワ紝娴嬮噺鏃犳晥锛堝埆褰撴垚閫氳繃锛?
                : $"  FAIL: 鎶瑪鍓嶅悗瀵逛笉涓婏紙绾垮宸腑浣?{Med(dw):F1}px銆佸榻愪綅绉?{bestShift}px锛夆€斺€旀敹绗斾細鈥滄矇涓€涓嬧€?);
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 涓€鍧?BGRA 鍥鹃噷锛?*閫愬垪**鐨勫ⅷ瀹戒笌涓績绾匡紙鐧藉簳鍒ゆ嵁锛氫笉鏄櫧灏辩畻澧級銆?    /// 杩斿洖鐨勪袱涓暟缁勯暱搴?= 鍥惧锛涙煇涓€鍒楁病鏈夊ⅷ鏃惰鍒?= -1銆?    /// </summary>


    private void InkTrailTest(bool withTouch = false)
    {
        Console.WriteLine();
        Console.WriteLine(withTouch
            ? "=== 濮旀墭澧ㄨ抗杞ㄨ抗娴嬭瘯锛堝悎鎴?*瑙︽懜**锛屽叧鎺夎嚜宸辩敾鐨勬箍澧級==="
            : "=== 濮旀墭澧ㄨ抗杞ㄨ抗娴嬭瘯锛堝悎鎴?*榧犳爣**锛屽叧鎺夎嚜宸辩敾鐨勬箍澧級===");
        // 鏈祴璇曚笓闂ㄩ獙绯荤粺杞ㄨ抗锛氫笉绠″綋鍓嶆。浣嶆槸浠€涔堬紙榛樿妗?0 鏄叧鐨勶級锛岄兘寮哄埗鎵撳紑銆?        OverlayWindow.InkTrailEnabled = true;
        Console.WriteLine($"  鎺ュ彛鐘舵€? {OverlayWindow.InkTrailNote}");

        if (withTouch && !EnsureSyntheticTouch())
        {
            Console.WriteLine("  SKIP: 鎷夸笉鍒板悎鎴愯Е鎽歌澶囷紙CreateSyntheticPointerDevice(PT_TOUCH) 澶辫触锛?);
            _quit = true; return;
        }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;

        SettleFrames(300);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int bandX = (int)(cx - 500);
        int bandY = (int)(cy - 140);

        // 榧犳爣鍜岃Е鎽歌蛋鍚屼竴涓?鎷栧姩 + 閫愬抚閲囨牱"楠ㄦ灦锛屽彧鏈夋敞鍏ユ柟寮忎笉鍚?        //锛堣Е鎽告病鏈夋偓鍋滐紱鎺ヨЕ闈㈢粰 24锛屽拰 --touchtest 鐨勫彛寰勪竴鑷达級銆?        void Down(float x, float y)
        {
            if (withTouch) SendTouchesSized(true, (x, y, 24f));
            else SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);
        }
        void MoveTo(float x, float y)
        {
            if (withTouch) SendTouchesSized(true, (x, y, 24f));
            else SendMouse((int)x, (int)y, 0);
        }
        void Up(float x, float y)
        {
            if (withTouch) SendTouchesSized(false, (x, y, 24f));
            else SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);
        }

        if (!withTouch)
        {
            SendMouse((int)(cx - 400), (int)cy, 0);   // 榧犳爣鍏堟偓鍋滃埌浣嶏紙瑙︽懜娌℃湁杩欎竴姝ワ級
            SettleFrames(120);
        }

        // 鎸変綇涓嶆斁锛屾參鎱㈢敾涓€娈碉紱**鎷栧姩涓€愬抚閲囨牱**鈥斺€?        // 杩欐潯閫氶亾鐢荤殑鏄?杩樻病涓婂睆鐨勯偅涓€灏忔"锛屽彧瀛樻椿涓€涓ゅ抚锛岄噰鏍风█浜嗕細婕忥紙韪╄繃锛夈€?        Down(cx - 400, cy);
        SettleFrames(100);
        int peakDuringDrag = 0;
        bool shotSaved = false;
        for (int i = 1; i <= 40; i++)
        {
            float t = i / 40f;
            MoveTo(cx - 400 + t * 800, cy + MathF.Sin(t * 6.28f) * 80);
            for (int f = 0; f < 18; f++)
            {
                SettleFrames(1);
                int c = ScreenProbe.CountRed(bandX, bandY, 1000, 280);
                if (c > peakDuringDrag) peakDuringDrag = c;
                if (c > 200 && !shotSaved)
                {
                    shotSaved = true;
                    ScreenProbe.SaveBmp(Path.Combine("reports", "shots", "trail-first-seen.bmp"),
                                        _virtualX, _virtualY, _virtualW, _virtualH);
                    Console.WriteLine($"  [鎷栧姩涓?{i}/40] 棣栨瑙佸埌澧細{c} 鍍忕礌锛堝凡瀛?trail-first-seen.bmp锛?);
                }
            }
            if (i % 10 == 0)
                Console.WriteLine($"  [鎷栧姩涓?{i}/40] 绱宄板€?= {peakDuringDrag}");
        }
        string shotDrag = Path.Combine("reports", "shots", "trail-during-drag.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shotDrag));
        ScreenProbe.SaveBmp(shotDrag, _virtualX, _virtualY, _virtualW, _virtualH);
        Console.WriteLine("  鈥斺€?鍋滀綇锛堜粛鎸夌潃锛夛紝涓嬮潰姣忎竴妗ｇ害 0.8 绉掞紝鐪嬭建杩逛細涓嶄細鑷繁娑堝け 鈥斺€?);
        for (int k = 1; k <= 5; k++)
        {
            SettleFrames(50);
            Console.WriteLine($"  [鎸変綇涓嶅姩 {k}] 澧ㄨ壊鍍忕礌 = {ScreenProbe.CountRed(bandX, bandY, 1000, 280)}");
        }

        int whileDown = ScreenProbe.CountRed(bandX, bandY, 1000, 280);
        string shot1 = Path.Combine("reports", "shots", "trail-while-down.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot1));
        ScreenProbe.SaveBmp(shot1, _virtualX, _virtualY, _virtualW, _virtualH);

        Up(cx + 400, cy);
        SettleFrames(600);
        int afterUp = ScreenProbe.CountRed(bandX, bandY, 1000, 280);

        Console.WriteLine($"  鎷栧姩涓嘲鍊硷紙鍒ゅ畾鐢級    : {peakDuringDrag}锛堟寜浣忔湡闂达紝鍙兘鏄郴缁熺敾鐨勶級");
        Console.WriteLine($"  鍋滀綇 4 绉掑悗锛堟澗鎵嬪墠锛?   : {whileDown}");
        Console.WriteLine($"  鏉炬墜涔嬪悗绗旇抗鍍忕礌        : {afterUp}锛堟垜浠嚜宸辩殑绗旂敾鎺ョ锛?);
        Console.WriteLine($"  杞ㄨ抗璋冭瘯          : {OverlayWindow.InkTrailDebug}");
        Console.WriteLine($"  鎴浘: {Path.GetFullPath(shotDrag)} / {Path.GetFullPath(shot1)}");

        string inputName = withTouch ? "瑙︽懜" : "榧犳爣";
        bool penRun = LastPointerType == Native.PT_PEN;
        // 鍒ゅ畾锛?026-10-11 鏍″噯锛夛細杩欐潯閫氶亾鐢荤殑鏄?*"杩樻病涓婂睆鐨勯偅涓€灏忔"**鈥斺€旀垜浠嚜宸辩殑甯?        // 涓€鎻愪氦灏辨妸瀹?娑堝寲"鎺夛紝鎵€浠ラ噺鍒扮殑鏄煭闂紙榧犳爣/瑙︽懜瀹炴祴宄板€肩害 163 鍍忕礌锛夈€?        //   鏁存潯灏惧反 >800       鈫?PASS锛堢湡绗旈偅涓€璺€氬父鍑犲崈锛?        //   瑙佸埌浠讳綍澧?>0       鈫?PASS锛堥€氶亾纭疄鍦ㄧ敾杩欑杈撳叆锛?        //   鎺ュ彛涓嶅彲鐢?         鈫?SKIP锛圵in10/鑰佺郴缁熸病鏈夎繖鏉￠€氶亾锛屽睘姝ｅ父鐜宸紓锛?        //   鍏朵綑                鈫?SKIP锛堝熬宸村彧娲讳竴涓ゅ抚銆佹病璧朵笂涓嶇畻澶辫触锛涚湡绗旀病鏈夋墠绠?FAIL锛?        if (peakDuringDrag > 800)
            Console.WriteLine($"  PASS: 濮旀墭澧ㄨ抗杞ㄨ抗鐢熸晥锛坽inputName}鐢诲嚭浜嗘暣鏉″熬宸达級");
        else if (peakDuringDrag > 0)
            Console.WriteLine($"  PASS: 绯荤粺鍦ㄧ敾锛坽inputName}灏惧反宄板€?{peakDuringDrag} 鍍忕礌鈥斺€斿彧娲诲埌涓嬩竴娆′笂灞忥紝灞為鏈燂級");
        else if (!OverlayWindow.InkTrailAvailable)
            Console.WriteLine($"  SKIP: 绯荤粺娌℃湁杩欐潯閫氶亾锛坽OverlayWindow.InkTrailNote}锛夆€斺€擶in10/鑰佺郴缁熷睘姝ｅ父");
        else if (penRun)
            Console.WriteLine("  FAIL: 鎵嬩笂鏄湡绗斿嵈娌＄湅鍒扮郴缁熺敾鐨勮建杩?);
        else
            Console.WriteLine($"  SKIP: 杩欐娌￠噺鍒皗inputName}杞ㄨ抗锛堢獥鍙ｅ彧鏈変竴涓ゅ抚锛屽彲鑳芥病璧朵笂锛夆€斺€斾笉绠楀け璐?);
        _quit = true;
    }


    /// <summary>
    /// 婀垮ⅷ杞ㄨ抗锛堝鎵樺ⅷ杩癸級瀹炴祴锛?*鍙绯荤粺鐢?*锛坄SuppressActiveStroke`锛夛紝鐢ㄥ悎鎴愮瑪璧颁竴閬嶏紝
    /// 鍐嶄粠灞忓箷涓婃暟澧ㄨ壊鍍忕礌銆?    ///
    /// 涓轰粈涔堝繀椤绘祴杩欎竴鏉★細棰勬祴**鍙杺杩欐潯閫氶亾**鈥斺€斿畠鐢荤殑鏄?姝ｅ湪鍐欑殑杩欎竴绗?鐨勬渶鍚庝竴灏忔銆?    /// 濡傛灉杩欐潯閫氶亾鍦ㄦ煇鍙版満鍣ㄤ笂鏍规湰娌′笂灞忥紙API 璋冪敤鎴愬姛銆佽繑鍥炰簡 generationId锛?    /// 涓嶇瓑浜?DWM 鐪熺殑鐢诲嚭鏉ワ級锛岄偅棰勬祴灏变笉鍙兘鏈変换浣曞彲瑙佹晥鏋滐紝
    /// "寮€/鍏抽兘涓€鏍?灏变細鏈夊畬鍏ㄤ笉鍚岀殑瑙ｉ噴銆?    /// </summary>
    private void WetInkTest(bool activate = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 婀垮ⅷ杞ㄨ抗瀹炴祴锛氬彧璁╃郴缁熺敾锛堣嚜宸变笉鐢伙級===");
        // 鏈祴璇曚笓闂ㄩ獙绯荤粺杞ㄨ抗锛氫笉绠″綋鍓嶆。浣嶆槸浠€涔堬紝閮藉己鍒舵妸瀹冩墦寮€銆?        OverlayWindow.InkTrailEnabled = true;
        Console.WriteLine($"  鍙樹綋锛歿(activate ? "鎶婅鐩栧眰寮哄埗婵€娲伙紙鍘绘帀 NOACTIVATE + 鎶㈠墠鍙帮級" : "鎸変骇鍝佸師鏍凤紙缃《 + 涓嶆姠鐒︾偣锛?)}");
        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 鎷夸笉鍒板悎鎴愮瑪璁惧");
            _quit = true; return;
        }

        if (activate && _windows.Count > 0)
        {
            // 鍋囪锛欴WM 鍙兘鍙粰"鍓嶅彴绐楀彛"鐢诲鎵樻箍澧ㄣ€傝繖閲屾妸 NOACTIVATE 鎽樻帀骞舵姠涓€娆″墠鍙帮紝
            // 鐪嬭建杩逛細涓嶄細绐佺劧鍑虹幇鈥斺€旂函绮规槸瀵圭収瀹為獙锛屼骇鍝佸舰鎬佷笉浼氳繖涔堝仛銆?            var w0 = _windows[0];
            long ex = (long)Native.GetWindowLongPtr(w0.Hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(w0.Hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex & ~(long)Native.WS_EX_NOACTIVATE));
            Native.SetForegroundWindow(w0.Hwnd);
            Console.WriteLine($"  宸叉縺娲伙細{w0.Hwnd}");
        }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;      // 鎴戜滑鑷繁閭ｄ竴绗斾笉鐢伙紝灞忓箷涓婄暀鐨勫氨鍙兘鏄郴缁熺敾鐨?        SettleFrames(200);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int boxX = (int)(cx - 400), boxY = (int)(cy - 160);

        SendPenPoint(cx - 300, cy, 400, contact: true, first: true);
        for (int i = 1; i <= 30; i++)
        {
            SendPenPoint(cx - 300 + i * 20, cy + MathF.Sin(i / 30f * 6.28f) * 60,
                         400, contact: true, first: false);
            SettleFrames(4);
        }
        SettleFrames(60);                 // 绗斾粛鐒舵寜鐫€

        int whileDown = ScreenProbe.CountRed(boxX, boxY, 800, 320);
        int whileDownFull = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
        string shot = Path.Combine("reports", "shots", "wetink-down.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot));
        ScreenProbe.SaveBmp(shot, boxX, boxY, 800, 320);
        string shotFull = Path.Combine("reports", "shots", "wetink-down-full.bmp");
        ScreenProbe.SaveBmp(shotFull, _virtualX, _virtualY, _virtualW, _virtualH);

        SendPenPoint(cx + 300, cy, 400, contact: false, first: false);
        SettleFrames(300);
        int afterUp = ScreenProbe.CountRed(boxX, boxY, 800, 320);

        SuppressActiveStroke = false;
        Console.WriteLine($"  鎸変綇涓嶆斁鏃跺ⅷ鑹插儚绱狅細{whileDown}锛堣繖涓€绗旀垜浠嚜宸辨病鐢伙紝鍙兘鏄郴缁熺敾鐨勶級");
        Console.WriteLine($"  鍚屼笂浣嗘壂鍏ㄥ睆      锛歿whileDownFull}锛堟帓闄ゃ€岀敾鍦ㄥ埆澶勩€嶏級");
        Console.WriteLine($"  鎶瑪涔嬪悗澧ㄨ壊鍍忕礌  锛歿afterUp}锛堝鎵樿建杩瑰簲褰撹鏀跺洖锛?);
        Console.WriteLine($"  杞ㄨ抗璋冭瘯          锛歿OverlayWindow.InkTrailDebug}");
        Console.WriteLine($"  鎴浘              锛歿Path.GetFullPath(shot)}");
        Console.WriteLine($"  鍏ㄥ睆鎴浘          锛歿Path.GetFullPath(shotFull)}");
        // 閲嶈鏁欒锛?*鍚堟垚绗斾笉琚?DWM 璁?*锛堝疄娴嬶細鍚堟垚绗斾笅鎸変綇鏃?0 涓ⅷ鑹插儚绱狅紝
        // 鎹㈡垚鐪熺瑪鍚屾牱娴佺▼鏄?1.4 涓囦釜锛夈€傛墍浠ュ悎鎴愬垎鏀彧鑳藉垽"鎴戜滑鐨?API 璋冪敤鎴愪笉鎴愬姛"锛?        // 鍒や笉浜?绯荤粺鐢绘病鐢?鈥斺€旂湡绗旇鐢?--wetinktest --live N锛堜汉鏉ュ啓锛夈€?        Console.WriteLine(whileDown > 500
            ? "  PASS: 濮旀墭澧ㄨ抗杞ㄨ抗纭疄涓婁簡灞?鈥斺€?棰勬祴鏈夊彲瑙侀€氶亾"
            : "  涓嶅彲鍒ゅ畾锛氬悎鎴愮瑪涓嶄細琚?DWM 鐢绘垚婀垮ⅷ锛堝疄娴嬪姝わ級銆傝鍒よ繖涓€鏉¤鐢ㄧ湡绗旓細--wetinktest --live 15");
        Console.WriteLine($"  鎶瑪鍚庣殑澧ㄦ槸鎴戜滑鑷繁鎻愪氦杩涙枃妗ｇ殑閭ｄ竴绗旓紙涓庤建杩规棤鍏筹級锛岃繖閲屽彧浣滆褰曪細{afterUp} 鍍忕礌");
        _quit = true;
    }


    /// <summary>
    /// 婀垮ⅷ杞ㄨ抗銆岀湡绗?+ 鑷繁涓嶇敾銆嶅疄娴嬶細浣犲啓 N 绉掞紝鎴戞瘡 100 ms 閲囦竴娆″睆锛?    /// 璁颁笅**鏈€澶?*鐪嬪埌澶氬皯澧ㄨ壊鍍忕礌銆?    ///
    /// 涓轰粈涔堣浜哄啓锛氬悎鎴愮瑪璧扮殑鏄悓涓€鏉?WM_POINTER 璺緞锛屼絾 DWM 鍙兘鍙鐪熷疄鏁板瓧鍖栦华鐨?    /// 鎸囬拡锛堜笂涓€杞悎鎴愮瑪涓嬭建杩逛竴涓儚绱犻兘娌℃湁锛夈€傝繖涓€杞氨鏄敤鏉ュ垽杩欎竴鏉＄殑銆?    /// </summary>
    private void WetInkLiveTest(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 婀垮ⅷ杞ㄨ抗瀹炴祴锛堢湡绗?路 鍙绯荤粺鐢伙級===");
        // 鏈祴璇曚笓闂ㄩ獙绯荤粺杞ㄨ抗锛氫笉绠″綋鍓嶆。浣嶆槸浠€涔堬紝閮藉己鍒舵妸瀹冩墦寮€銆?        OverlayWindow.InkTrailEnabled = true;
        Console.WriteLine("  娉ㄦ剰锛氳繖涓€杞綘鑷繁鍐欑殑鏃跺€欏睆骞曚笂**涓嶄細**鍑虹幇绗旂敾锛堟垜浠嚜宸遍偅灞傚叧鎺変簡锛夛紝");
        Console.WriteLine("        鎶瑪涔嬪悗閭ｄ竴绗旀墠浼氬嚭鐜般€傚睆骞曚笂涓€斿嚭鐜扮殑浠讳綍澧紝閮藉彧鑳芥槸绯荤粺鐢荤殑濮旀墭杞ㄨ抗銆?);
        Console.WriteLine($"  鐜板湪寮€濮嬪啓 {seconds:F0} 绉掆€︹€?);
        Console.WriteLine();

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;
        SettleFrames(120);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int boxX = (int)(cx - 700), boxY = (int)(cy - 350);
        int maxBand = 0, maxBandDown = 0, maxFull = 0, samples = 0, downSamples = 0;
        int maxRise = 0, bandAtDown = 0;
        bool wasDown = false;
        int beforePoints = PenTotalPoints;
        long fedBefore = OverlayWindow.InkTrailPointsFed;
        long removedBefore = OverlayWindow.InkTrailPointsRemoved;

        double t0 = NowMs;
        double nextSampleAt = t0;
        int tick = 0;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            if (_dirty || _drawing) { if (OverlayWindow.VBlankPaced) Native.DwmFlush(); RenderAll(); _dirty = false; }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= nextSampleAt)
            {
                nextSampleAt = NowMs + 100;
                samples++;
                int band = ScreenProbe.CountRed(boxX, boxY, 1400, 700);
                if (band > maxBand) maxBand = band;
                // 鍒ゆ嵁鐢?鎸変笅鏈熼棿鍍忕礌**澧為噺**"鑰屼笉鏄粷瀵瑰€硷細鎶瑪鍚庣殑骞插ⅷ銆佸睆骞曚笅闈㈢殑妗岄潰
                // 鍐呭閮戒細杩涚粷瀵瑰€硷紝鍙湁"鎸変笅杩欎竴涓嬭澧ㄦ按鍙樺"鎵嶈瘉鏄庣郴缁熻建杩圭湡鐨勫湪鐢?                //锛?026-10-05 涓ゆ韪╁潙鍚庢敼鎴愯繖鏍凤級銆?                bool down = _drawing;
                if (down && !wasDown) bandAtDown = band;
                if (down)
                {
                    downSamples++;
                    if (band > maxBandDown) maxBandDown = band;
                    int rise = band - bandAtDown;
                    if (rise > maxRise) maxRise = rise;
                }
                wasDown = down;
                if (++tick % 10 == 0)              // 鍏ㄥ睆姣忕涓€娆★紝鎺掗櫎"鐢诲湪鍒"
                {
                    int full = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
                    if (full > maxFull) maxFull = full;
                }
            }
        }

        SuppressActiveStroke = false;
        int penSamples = PenTotalPoints - beforePoints;
        long fed = OverlayWindow.InkTrailPointsFed - fedBefore;
        long removed = OverlayWindow.InkTrailPointsRemoved - removedBefore;
        Console.WriteLine();
        Console.WriteLine($"  杩?{seconds:F0} 绉掗噷鏀跺埌鐪熺瑪閲囨牱鐐癸細{penSamples}锛? 璇存槑娌″啓杩涙潵锛?);
        Console.WriteLine($"  鎸変笅鏈熼棿澧ㄨ壊鍍忕礌**澧為噺**    锛歿maxRise}锛堝垽瀹氱敤锛涢噰浜?{downSamples} 娆★級");
        Console.WriteLine($"  鎸変笅鏈熼棿缁濆鍊?/ 鍏ㄧ▼鏈€澶? 锛歿maxBandDown} / {maxBand}锛堝惈鎶瑪鍚庣殑骞插ⅷ锛屼粎鍙傝€冿級");
        Console.WriteLine($"  鍠傝繘绯荤粺杞ㄨ抗鐨勭偣鏁?       锛歿fed}锛堝凡缁撹处鍒犻櫎 {removed}鈥斺€旀湰杞垜浠笉鑷粯锛屾墍浠ユ槸 0锛?);
        Console.WriteLine($"  鍏ㄥ睆鐨勬渶澶уⅷ鑹插儚绱?       锛歿maxFull}");
        Console.WriteLine($"  杞ㄨ抗璋冭瘯                  锛歿OverlayWindow.InkTrailDebug}");
        bool pass = penSamples > 0 && fed > 0 && maxRise > 200;
        string why = penSamples == 0 ? "娌℃敹鍒扮湡绗旈噰鏍凤紙榧犳爣/鍏煎妯″紡锛?
                   : fed == 0 ? "杞ㄨ抗涓€涓偣閮芥病鍠傝繘绯荤粺"
                   : maxRise <= 200 ? "鎸変笅鏈熼棿灞忓箷涓婄殑澧ㄦ病鏈夊澶?鈥斺€?绯荤粺杞ㄨ抗娌＄敾"
                   : "绯荤粺杞ㄨ抗姝ｅ父";
        Console.WriteLine(pass
            ? "  PASS: 鐪熺瑪涓嬬郴缁熺‘瀹炲湪鐢诲鎵樿建杩?
            : $"  FAIL: {why} 鈥斺€?濮旀墭杞ㄨ抗杩欏彴鏈哄櫒/杩欎釜璁惧涓婁笉鐢熸晥");
        SettleFrames(120);
        _quit = true;
    }

    /// <summary>
    /// 搴旂敤渚ф箍澧紙FastWet锛夊睆骞曡嚜妫€锛?*鎶婄郴缁熷鎵樿建杩瑰叧鎺?*锛屽睆骞曚笂鎸変綇鏈熼棿
    /// 鍑虹幇鐨勫ⅷ灏卞彧鑳芥槸 FastWet 鐢荤殑銆傛煡涓や欢鐪熸満涓婅俯杩囩殑鍥炲綊锛?    ///   鈶?鎱㈠啓鏂ⅷ锛欴Comp 鐨勬洿鏂扮煩褰?*涓嶄繚鐣欐棫鍐呭**锛屾棭鍏?鎿︽暣鍧椼€佸彧鐢绘柊澧炴"鐨?    ///      瀹炵幇浼氭妸绗斿皷鍚庨潰宸茬粡鐢诲ソ鐨勯偅涓€鎴摝鎺夆€斺€旀闀?3~16px 鏃惰〃鐜颁负"寰堝瘑闆嗙殑
    ///      鏂ⅷ"锛?026-10-13 鐢ㄦ埛鎶ョ殑灏辨槸瀹冿級锛?    ///   鈶?鐧芥澘闅愯韩锛氭箍澧ㄨ瑙夋棭鍏堝姞鍦ㄤ氦鎹㈤摼**涓嬪眰**锛屽紑浜嗕笉閫忔槑鐧芥澘灏变竴涓箍澧ㄥ儚绱?    ///      閮界湅涓嶈锛?026-10-13 瀹炴祴锛氭闈㈡甯搞€佺櫧鏉?0 鍍忕礌锛夈€?    ///      `--fastwettest --board` 涓撹窇杩欎竴鏉°€?    /// </summary>
    private void FastWetTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 搴旂敤渚ф箍澧紙FastWet锛夎嚜妫€锛氬叧鎺夌郴缁熷鎵樿建杩?===");
        // 鏈祴璇曟湡闂寸郴缁熻建杩逛笉鐢伙紙灞忓箷涓婄暀鐨勫ⅷ鍙兘鏄?FastWet锛夈€?        OverlayWindow.InkTrailEnabled = false;
        OverlayWindow.FastWetFallbackEnabled = true;

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;     // 涓讳氦鎹㈤摼涓嶇敾"姝ｅ湪鍐欑殑閭ｄ竴绗?锛屽垽鎹墠骞插噣
        SettleFrames(200);

        Console.WriteLine($"  鐧芥澘  : {(BoardOn ? "寮€锛堜笉閫忔槑鐧藉簳鈥斺€旀箍澧ㄥ繀椤绘诞鍦ㄥ畠涓婇潰锛? : "鍏筹紙妗岄潰閫忓嚭鏉ワ級")}");

        // **鍝佺孩**澧細妗岄潰鑳屾櫙閲屽嚑涔庝笉浼氬嚭鐜拌繖涓鑹诧紙绾?钃?榛戦兘甯歌锛夛紝
        // 鍒ゆ嵁鐢ㄧ殑鏄?鏁板搧绾㈠儚绱?锛屼笉浼氭妸妗岄潰涓婄殑绾㈠瓧銆佺孩鍥炬爣绠楁垚鎴戜滑鐨勫ⅷ銆?        CurrentColor = new Color4(1f, 0f, 1f, 1f);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float x0 = cx - 300f;
        const int stepPx = 8;            // 8px 涓€姝ワ細姝ｆ槸鏃у疄鐜板嚭"瀵嗛泦鏂彛"鐨勬闀垮尯闂?        const int steps = 60;            // 480px 闀跨殑涓€鏉?
        SendMouse((int)x0, (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)x0, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(30);

        int peak = 0;
        for (int i = 1; i <= steps; i++)
        {
            SendMouse((int)(x0 + i * stepPx), (int)cy, 0);
            SettleFrames(2);
            int n = ScreenProbe.CountMagenta((int)x0 - 24, (int)cy - 24, steps * stepPx + 48, 48);
            if (n > peak) peak = n;
        }
        SettleFrames(10);

        int tipX = (int)(x0 + steps * stepPx);
        // 绗斿皷鍚?10px 閭ｄ竴鏉★紙鏃у疄鐜扮殑"鏈€鏂版柇鍙?灏卞湪杩欓噷锛夛細鏁存閲嶇敾鍚庡簲褰撴湁澧ㄣ€?        int holeInk = ScreenProbe.CountMagenta(tipX - 11, (int)cy - 14, 6, 28);

        // 閫愬垪鍒嗘瀽宸茬粡鐢昏繃鐨勯偅涓€娈碉紙绗斿皷鍚庨潰 10~130px锛夛細鏃у疄鐜颁細鎶婅繖涓€娈靛埛鎴愪竴鑺備竴鑺傜殑
        // 铏氱嚎锛堟瘡涓洿鏂扮煩褰㈡摝鎺夊畠宸﹁竟鍑犲儚绱犵殑鏃уⅷ銆佸張鍙噸鐢绘柊澧炴锛夆€斺€?绌哄垪"灏辨槸鏂彛鐨勯噺搴︺€?        int gapCols = CountEmptyColumnsInk(tipX - 130, (int)cy - 14, 120, 28, out int medCol);

        // ---- 鎶樿繑 + 浜ゅ弶锛堝悓涓€绗旈噷缁х画鐢伙級锛氫粠妯嚎鏈涓€澶ц烦鍥炲埌宸︿笂鏂广€佸啀绔栫洿鍚戜笅绌胯繃妯嚎銆?        //      鏃у疄鐜伴噷杩欎竴璺崇殑鏇存柊鐭╁舰浼氭í璺?妯嚎鐨勪竴澶ф"锛屾摝鎺変箣鍚庡彧閲嶇敾鏈€鍚庝袱娈?        //      鈥斺€?妯嚎涓鏁存琚摝绌猴紙鐪熸満涓婂氨鏄?鍐欑潃鍐欑潃澧ㄦ柇浜?鏈€鍑剁殑涓€绉嶅舰鎬侊級銆?        //      `SendMouse` 涓€鍙戝氨鏄竴璺筹紝鎭板ソ钀藉湪"涓€涓洿鏂扮煩褰㈣法杩囧凡鏈夊ⅷ"鐨勫満鏅笂銆?        int crossX = tipX - 200;
        SendMouse(crossX, (int)cy - 80, 0);
        SettleFrames(4);
        for (int i = 1; i <= 20; i++)          // 绔栫嚎鍚戜笅 8px/姝ワ紝绌胯繃妯嚎
        {
            SendMouse(crossX, (int)cy - 80 + i * 8, 0);
            SettleFrames(2);
        }
        SettleFrames(10);

        // 鎶樿繑閭ｄ竴澶ц烦涔嬪悗锛屾í绾夸腑娈碉紙crossX+16 .. tipX-16锛夊湪鏃у疄鐜伴噷浼氳鏁存鎿︽帀銆?        int jumpW = Math.Max(8, tipX - 16 - (crossX + 16));
        int jumpGapCols = CountEmptyColumnsInk(crossX + 16, (int)cy - 14, jumpW, 28, out int medCol2);
        // 绔栫嚎绌胯繃妯嚎鐨勪氦鍙夌偣涓や晶锛堟棫瀹炵幇姣忎晶缂?~3.5px锛夈€?        int crossGapCols = CountEmptyColumnsInk(crossX - 14, (int)cy - 14, 28, 28, out int medCol3);

        string shot = Path.Combine("reports", "shots",
            BoardOn ? "fastwet-board.bmp" : "fastwet-desktop.bmp");
        ScreenProbe.SaveBmp(shot, (int)x0 - 40, (int)cy - 120, steps * stepPx + 80, 240);

        SendMouse(crossX, (int)(cy + 80), Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);
        int afterUp = ScreenProbe.CountMagenta((int)x0 - 24, (int)cy - 24, steps * stepPx + 48, 48);

        Console.WriteLine($"  鎷栧姩涓ⅷ鑹插儚绱狅紙鍏ㄧ▼宄板€硷級   : {peak}锛?0 = FastWet 鍦ㄧ敾锛?);
        Console.WriteLine($"  绗斿皷鍚?6px 鏂彛鎺㈡祴          : {holeInk}锛堟棫瀹炵幇鈮?锛屽簲 鈮?0锛?);
        Console.WriteLine($"  宸茬敾娈电┖鍒?/ 涓綅鍒楀ⅷ        : {gapCols} / {medCol}锛堟棫瀹炵幇鍑犲崄鏉＄┖鍒楋紳瀵嗛泦鏂ⅷ锛?);
        Console.WriteLine($"  鎶樿繑澶ц烦鍚?妯嚎涓绌哄垪      : {jumpGapCols} / {jumpW}锛堜腑浣嶅垪澧?{medCol2}锛涙棫瀹炵幇鈮堟暣娈佃鎿︾┖锛?);
        Console.WriteLine($"  浜ゅ弶鐐逛袱渚х┖鍒?              : {crossGapCols} / 28锛堟棫瀹炵幇姣忎晶缂?~3.5px锛涗腑浣嶅垪澧?{medCol3}锛?);
        Console.WriteLine($"  鏉炬墜鍚庣瑪杩瑰儚绱?              : {afterUp}锛堝共澧ㄦ帴绠★紝浠呬緵鍙傝€冿級");
        Console.WriteLine($"  鎴浘                         : {Path.GetFullPath(shot)}");

        bool ok = peak > 40 && holeInk >= 20 && gapCols <= 8 && jumpGapCols <= 8 && crossGapCols <= 6;
        Console.WriteLine(ok
            ? "  PASS: FastWet 鎸変綇鏈熼棿鎸佺画鍙锛涙參鍐?鎶樿繑/浜ゅ弶閮芥病鏈夋柇澧?
            : "  FAIL: " + (peak <= 40
                ? "鎸変綇鏈熼棿鍑犱箮娌℃湁婀垮ⅷ鈥斺€擣astWet 娌＄敾鍑烘潵鎴栬涓婂眰鐩栦綇锛堢櫧鏉夸笅鍏堟煡瑙嗚灞傚簭锛?
                : holeInk < 20
                    ? "绗斿皷鍚庨潰鍑虹幇鏂彛鈥斺€旀洿鏂扮煩褰㈤噷娌℃湁鎶婃暣绗旈噸鐢?
                    : "宸茬粡鐢昏繃鐨勬箍澧ㄨ鏇存柊鐭╁舰鎿︽帀娌¤ˉ鍥炴潵锛堟姌杩?浜ゅ弶澶勬柇澧級"));
        SettleFrames(60);
        _quit = true;
    }

    /// <summary>
    /// 鏁颁竴鍧楀睆骞曞尯鍩熼噷**鍑犱箮娌℃湁澧ㄧ殑鍒?*锛堟柇鍙ｇ殑閲忓害锛夛紝杩斿洖绌哄垪鏁帮紱
    /// <paramref name="medCol"/> 缁欏嚭鍒楀ⅷ鍍忕礌鏁扮殑**涓綅**锛堢嚎瀹界殑閲忓害锛夈€?    ///
    /// 鍩哄噯蹇呴』鐢ㄤ腑浣嶃€佷笉鑳界敤鏈€澶э細浜ゅ弶鐐归偅涓€鍒椾細鍚屾椂鍘嬬潃妯嚎鍜岀珫绾匡紙澧ㄩ噺缈诲嚑鍊嶏級锛?    /// 鎷垮畠褰?婊″垪"浼氭妸鎵€鏈夋甯稿垪閮借鍒ゆ垚绌哄垪锛?026-10-13 绗竴鐗堟寚鏍囧氨杩欎箞閿欒繃涓€娆★級銆?    /// 澧ㄨ壊鍒ゆ嵁鐢?*鍝佺孩**锛堣嚜妫€澧ㄨ壊锛岃 FastWetTest锛夆€斺€旀闈笂鐨勭孩/钃濆唴瀹逛笉浼氭贩杩涙潵銆?    /// </summary>
    private static int CountEmptyColumnsInk(int x, int y, int w, int h, out int medCol)
    {
        medCol = 0;
        if (w <= 0 || h <= 0) return 0;
        var buf = ScreenProbe.CaptureRegion(x, y, w, h);
        if (buf.Length < w * h * 4) return 0;
        var col = new int[w];
        for (int yy = 0; yy < h; yy++)
        {
            int row = yy * w * 4;
            for (int xx = 0; xx < w; xx++)
            {
                int o = row + xx * 4;
                byte b = buf[o], g = buf[o + 1], r = buf[o + 2];
                if (b > 200 && g < 90 && r > 200) col[xx]++;
            }
        }
        var sorted = (int[])col.Clone();
        Array.Sort(sorted);
        medCol = sorted[w / 2];
        int need = Math.Max(1, (int)(medCol * 0.4f));
        int gaps = 0;
        foreach (var c in col) if (c < need) gaps++;
        return gaps;
    }

    // =====================================================================
    //  婀垮ⅷ寤舵椂瀵圭収锛?-lagtest锛?    // =====================================================================

    /// <summary>
    /// 婀垮ⅷ寤舵椂瀵圭収锛堝悎鎴愰紶鏍囷級锛氭妸鍑犳潯婀垮ⅷ鏄剧ず璺緞**闅旂鎴愬崟鍙橀噺**锛岄噺涓や釜瀹㈣鍙ｅ緞锛?    ///   鈶?鎷栧姩鏃?灞忓箷涓婄殑澧ㄥ皷钀藉悗鎸囬拡"澶氬皯鍍忕礌锛埫烽€熷害鎹㈢畻鎴愭绉掞級锛?    ///   鈶?鎸囬拡鐬烦鍚庯紝鏂颁綅缃涔呭嚭鐜板ⅷ锛堟绉掞級銆?    ///
    /// 鍥涙。妯″紡锛堝悓涓€杩涚▼閲屾潵鍥炲垏锛屽埆鐨勫彉閲忛兘涓嶅姩锛夛細
    ///   路 涓讳氦鎹㈤摼鑷粯 鈥斺€?鍏宠建杩广€佸叧 FastWet锛? 2026-10 浠ュ墠鐨勮€佽矾寰勶紝鍩虹嚎锛夛紱
    ///   路 FastWet      鈥斺€?鍏宠建杩广€佸帇鎺変富鑷粯锛堝睆骞曚笂鍙墿搴旂敤渚х嫭绔嬫箍澧ㄥ眰锛夛紱
    ///   路 绯荤粺杞ㄨ抗      鈥斺€?鍘嬫帀涓昏嚜缁樸€佸叧 FastWet锛堝睆骞曚笂鍙墿绯荤粺鐢荤殑鐭熬锛夛紱
    ///   路 浜у搧缁勫悎      鈥斺€?杞ㄨ抗 + 涓昏嚜缁橈紙Win11 榛樿灏辨槸瀹冿級銆?    ///
    /// 姣忔。璺?*涓や釜璐熻浇**锛岃繖鏄暣涓鐓х殑鍏抽敭锛?    ///   路 姝ｅ父甯э細涓夋潯璺緞搴斿綋閮解増0 钀藉悗鈥斺€?鍘熸潵閭ｅ鍏跺疄涔熻窡鎵?鍦ㄨ繖妗ｄ笂浼氬緱鍒拌瘉瀹烇紱
    ///   路 鎱㈠抚+50ms锛氭覆鏌撲箣鍓嶆晠鎰忓繖 50ms锛堟ā鎷?杩欎竴甯у緢閲?锛夈€備富浜ゆ崲閾剧殑澧ㄨ绛?    ///     杩欎竴甯х敾瀹?+ Present 鎵嶅姩锛涚郴缁熻建杩?/ FastWet 鍦ㄨ緭鍏ュ鐞嗛偅涓€鍒诲氨鎻愪氦缁?    ///     DComp锛屼笉缁忚繃浜ゆ崲閾?Present鈥斺€?*鎱㈠抚涓€?*閭ｄ竴鍒楋紙婀垮ⅷ宸蹭笂灞忋€佷富缁樿繕娌＄敾锛?    ///     灏辨槸杩欎袱灞傜湡姝ｄ拱鍒扮殑涓滆タ銆?    /// </summary>
    private void WetLagTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 婀垮ⅷ寤舵椂瀵圭収锛堝悎鎴愰紶鏍囷紱鍚屼竴杩涚▼閲屽垏妯″紡锛?==");
        Console.WriteLine($"  绯荤粺濮旀墭杞ㄨ抗: {OverlayWindow.InkTrailNote}"
                          + (OverlayWindow.InkTrailAvailable ? "锛堝彲鐢級" : "锛堜笉鍙敤锛?));
        Console.WriteLine($"  鑺傚: {(OverlayWindow.VBlankPaced ? "绛夊悎鎴愯竟鐣岋紙浜у搧鑺傚锛? : "涓嶇瓑杈圭晫锛?-nopace锛?)}锛?
                          + "澧ㄨ壊=鍝佺孩锛堟闈㈣儗鏅笉浼氭贩杩涙潵锛?);
        Console.WriteLine("  鍙ｅ緞: 涓変釜閲囨牱鏃跺埢 = 鏃?涓嬩竴鎷嶈緭鍏ュ墠) / 涓€?鎱㈠抚蹇欑殑閭?50ms 閲? / 鏂?鍒氭彁浜ゅ悗)锛?);
        Console.WriteLine("        鏁板瓧 = 澧ㄥ皷钀藉悗鎸囬拡澶氬皯 px锛堟嫭鍙烽噷鏄?梅1200px/s 鐨勬绉掞級锛涜烦鍙?= 鐬烦鍚庢柊浣嶇疆鍑哄ⅷ ms銆?);
        Console.WriteLine();

        CurrentColor = new Color4(1f, 0f, 1f, 1f);
        Tool = Tool.Pen;

        var modes = new (string Name, bool Trail, bool FastWet, bool SuppressMain)[]
        {
            ("涓讳氦鎹㈤摼鑷粯", false, false, false),
            ("FastWet",      false, true,  true),
            ("绯荤粺杞ㄨ抗",      true,  false, true),
            ("浜у搧缁勫悎",      true,  false, false),
        };

        const int bandW = 340, bandH = 40;
        // 鈽?棰勫垎閰嶅鐢紙閫愬抚 new 浼氳繘 LOH锛岄噺鍒扮殑鏄祴閲忓伐鍏疯嚜宸扁€斺€旇 CaptureRegionInto 鐨勬敞閲婏級銆?        var buf = new byte[bandW * bandH * 4];

        foreach (var (name, trail, fastWet, suppressMain) in modes)
        {
            if (trail && !OverlayWindow.InkTrailAvailable)
            {
                Console.WriteLine($"  {name,-12} SKIP锛堣繖鍙版満鍣ㄦ病鏈夌郴缁熻建杩规帴鍙ｏ級");
                continue;
            }

            SetWetModeForLagTest(trail, fastWet, suppressMain);
            Console.WriteLine($"  {name,-12}锛堣建杩瑰杺鐐?鐢绘病鐢荤湅涓嬮潰鐨刓"鏈夊ⅷ\"锛?);

            int fedTotal = 0;
            foreach (int delayMs in new[] { 0, 50 })
            {
                Doc.Clear(); Doc.InvalidateAll();
                SettleFrames(150);

                var aged = new List<float>();
                var mid = new List<float>();
                LagDragProbe(1200, delayMs, buf, bandW, bandH, aged, mid,
                             out int nSamples, out int inkAged, out int midTotal, out int inkMid, out int fed);
                fedTotal += fed;

                var jumps = LagJumpProbe(delayMs, buf, out int tries, out int hits);

                double aP50 = Median(aged);
                double mP50 = Median(mid);
                double jP50 = Median(jumps);
                string tag = delayMs == 0 ? "姝ｅ父甯?  " : "鎱㈠抚+50ms";
                string midPart = delayMs == 0
                    ? ""
                    : $" 涓€?{mP50,5:F1}px锛堟湁澧?{inkMid}/{midTotal}锛?;
                Console.WriteLine($"    {tag} 鎷栧姩1200px/s: 钀藉悗 鏃?{aP50,5:F1}px 鈮?{aP50 / 1.2,4:F0}ms锛堟湁澧?{inkAged}/{nSamples}锛?
                                  + midPart
                                  + $" | 璺冲彉 {(double.IsNaN(jP50) ? "鏈鍑? : jP50.ToString("F0") + "ms")}"
                                  + (double.IsNaN(jP50) ? "" : $"锛坽hits}/{tries}锛?)
                                  + (trail ? $" | [杞ㄨ抗] 鍠?{fed} 鐐? : ""));

            }

            if (trail)
            {
                // 閰嶉€熷惊鐜噷杞ㄨ抗鐨勫ⅷ甯稿父鐪嬩笉鍒帮紙瀹冨彧娲诲湪"鍠傜偣鈫掍笅涓€甯?涔嬮棿鐨勭煭绐楀彛閲岋級锛?                // 鎵€浠ュ崟鐙窇涓€娆?*涓嶉厤閫熴€侀€愬抚閲囨牱**鐨勫彲瑙佹€у彛寰勶紙鍜?`--trailtest` 鍚屼竴濂楋級锛?                // 鐢ㄥ畠鍥炵瓟"瀹冨埌搴曠敾娌＄敾"锛涘杺鐐规暟鍙鏄庨€氶亾鍦ㄨ窇锛屼笉浠ｈ〃涓婁簡灞忋€?                int vis = LagTrailVisibilityProbe();
                Console.WriteLine($"    [杞ㄨ抗] 涓ゆ。涓€鍏卞杺杩?{fedTotal} 涓偣锛堥€氶亾鍦ㄨ窇锛夛紱"
                                  + $"涓嶉厤閫熼€愬抚鍙ｅ緞鐨勫彲瑙佹€у嘲鍊?= {vis} px"
                                  + (vis > 0 ? "锛堝睆骞曚笂纭疄鐢讳簡锛? : "鈿?灞忓箷涓婃病鐢烩€斺€旂湡绗旇鐢?--wetinktest --live N 澶嶆牳"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("  鎬庝箞鐪嬶細");
        Console.WriteLine("   路 姝ｅ父甯ч偅琛屽鏋滈兘鈮? 钀藉悗锛氳鏄嶾"鍘熸潵閭ｅ涔熷緢璺熸墜\"鏄湡鐨勨€斺€旇交杞戒笅杩欏嚑鏉¤矾寰勫垎涓嶅嚭鏉ワ紱");
        Console.WriteLine("   路 鎱㈠抚+50ms 琛岋細涓讳氦鎹㈤摼鐨刓"涓€擻"杩樿惤鍚庝竴澶ф埅锛堝畠鍦ㄧ瓑浣犳妸杩欎竴甯х敾瀹岋級锛?
                          + "婀垮ⅷ灞傜殑\"涓€擻"鏄庢樉鏇村皬鈥斺€旇繖灏辨槸瀹冧滑涔板埌鐨勪笢瑗匡紱");
        Console.WriteLine("   路 绯荤粺杞ㄨ抗妗ｇ殑\"鏈夊ⅷ\"鎺ヨ繎鏍锋湰鏁?= 瀹冪湡鐨勫湪鐢伙紱鍠備簡鐐逛絾鍏ㄦ槸 0 = 瀹冩病鐢熸晥锛?);
        Console.WriteLine("   路 \"浜у搧缁勫悎\" = Win11 榛樿璁剧疆涓嬩綘瀹為檯鐪嬪埌鐨勬晥鏋滐紙杞ㄨ抗鈭富缁橈級銆?);

        foreach (var w in _windows) { w.EndInkTrail(); w.EndFastWet(); }
        SuppressActiveStroke = false;
        SettleFrames(80);
        _quit = true;
    }

    /// <summary>鎶婃箍澧ㄥ垏鎴愬崟鍙橀噺锛氳繖涓€妗ｅ厑璁稿摢涓€灞傚嚭鐜般€傛崲妗ｅ厛鎶婁笂涓€妗ｇ殑婀垮ⅷ鏀跺共鍑€銆?/summary>
    private void SetWetModeForLagTest(bool trail, bool fastWet, bool suppressMain)
    {
        foreach (var w in _windows) { w.EndInkTrail(); w.EndFastWet(); }
        OverlayWindow.InkTrailEnabled = trail;
        OverlayWindow.FastWetFallbackEnabled = fastWet;
        SuppressActiveStroke = suppressMain;
    }

    /// <summary>
    /// 鍖€閫熸嫋鍔ㄤ竴妗ｏ細閲?澧ㄥ皷钀藉悗绗斿皷"鐨勬牱鏈€傚弬鑰冧綅缃敤**鎴戜滑鍒氬彂鍑哄幓鐨勬寚閽堝潗鏍?*
    /// 锛堝悎鎴愰紶鏍囩殑鎸囬拡鏄灛鏃跺埌浣嶇殑锛屽畠灏辨槸"绗斿皷鐪熷€?锛夛紱澧ㄥ皷 = 閲囨牱甯﹂噷鏈€鍙崇殑鍝佺孩鍍忕礌銆?    ///
    /// <paramref name="delayMs"/> 鏄?鎱㈠抚"妯℃嫙锛氳緭鍏ュ鐞嗗畬涔嬪悗銆佹覆鏌撲箣鍓嶆晠鎰忓繖杩欎箞涔咃紱
    /// 蹇欑殑**涓棿**鍐嶉噰涓€娆★紙<paramref name="mid"/>锛夆€斺€旈偅鏄?婀垮ⅷ宸蹭笂灞忋€佷富缁樿繕娌＄敾"鐨勬椂鍒伙紝
    /// 婀垮ⅷ灞傚埌搴曚拱鍒颁簡浠€涔堬紝灏辩湅杩欎竴鍒椼€?    /// </summary>
    private void LagDragProbe(int speedPxPerSec, int delayMs, byte[] buf, int bw, int bh,
                              List<float> aged, List<float> mid,
                              out int samples, out int inkAged, out int midTotal, out int inkMid,
                              out int fedPoints)
    {
        samples = 0; inkAged = 0; midTotal = 0; inkMid = 0;
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float startX = cx - 520f;
        float y = cy;
        const double durMs = 650;

        SendMouse((int)startX, (int)y, 0);
        SettleFrames(120);
        SendMouse((int)startX, (int)y, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        long fed0 = OverlayWindow.InkTrailPointsFed;
        double t0 = _clock.Elapsed.TotalMilliseconds;
        float lastTx = startX;
        while (true)
        {
            double el = _clock.Elapsed.TotalMilliseconds - t0;
            if (el > durMs) break;
            float tx = startX + (float)(speedPxPerSec * el / 1000.0);
            if (tx >= lastTx + 0.5f) { SendMouse((int)tx, (int)y, 0); lastTx = tx; }

            // 浜у搧鑺傚锛氱瓑鍒板悎鎴愯竟鐣?鈫?鎶借緭鍏ワ紙鍠傝建杩?FastWet锛夆啋 鐢汇€?            if (OverlayWindow.VBlankPaced) Native.DwmFlush();
            DrainMessages();

            // 閲囨牱鈶?鏃?锛氳繖涓€鎷嶇殑杈撳叆鍒氬彂鍑哄幓銆佽繖涓€甯ц繕娌＄敾锛屽睆骞曚笂杩樺彧鏈変笂涓€杞殑澧ㄣ€?            bool sampleNow = el >= 150;
            int bx = (int)lastTx - (bw - 12), by = (int)y - bh / 2;
            if (sampleNow && ScreenProbe.CaptureRegionInto(buf, bx, by, bw, bh))
            {
                samples++;
                int mx = MaxMagentaX(buf, bw, bh);
                if (mx >= 0) { aged.Add(lastTx - (bx + mx)); inkAged++; }
            }

            // 鎱㈠抚妯℃嫙锛氭覆鏌撲箣鍓嶆晠鎰忓繖锛涘繖鍒颁竴鍗婇噰涓€娆?涓€?銆?            if (delayMs > 0)
            {
                Thread.Sleep(delayMs / 2);
                if (sampleNow && ScreenProbe.CaptureRegionInto(buf, bx, by, bw, bh))
                {
                    midTotal++;
                    int mx2 = MaxMagentaX(buf, bw, bh);
                    if (mx2 >= 0) { mid.Add(lastTx - (bx + mx2)); inkMid++; }
                }
                Thread.Sleep(delayMs - delayMs / 2);
            }

            if (_dirty || _drawing) { RenderAll(); _dirty = false; }
        }
        SendMouse((int)lastTx, (int)y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(150);
        fedPoints = (int)(OverlayWindow.InkTrailPointsFed - fed0);
    }

    /// <summary>
    /// 璺冲彉鍝嶅簲锛氭寚閽堢灛璺?130px锛岄噺"鏂颁綅缃嚭鐜板ⅷ"鐢ㄤ簡澶氬皯姣銆?    /// 杞閲屾妸 <paramref name="delayMs"/> 鐨?鎱㈠抚"鎷嗘垚 5ms 灏忕墖銆佹瘡鐗囦箣鍚庨兘閲囨牱鈥斺€?    /// 婀垮ⅷ灞備細鍦ㄥ繖鐨勮繖娈垫椂闂撮噷鍏堜笂灞忥紝涓讳氦鎹㈤摼瑕佺瓑鏁村抚鐢诲畬鎵嶅姩銆?    /// </summary>
    private List<double> LagJumpProbe(int delayMs, byte[] buf, out int tries, out int hits)
    {
        var res = new List<double>();
        tries = 0; hits = 0;
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float y = cy;
        float x = cx - 600f;
        SendMouse((int)x, (int)y, 0);
        SettleFrames(120);
        SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        const int jumpPx = 130, jumpCount = 10, sw = 26, sh = 30;
        for (int k = 0; k < jumpCount; k++)
        {
            x += jumpPx;
            tries++;
            double t0 = _clock.Elapsed.TotalMilliseconds;
            SendMouse((int)x, (int)y, 0);
            double dt = -1;
            while (_clock.Elapsed.TotalMilliseconds - t0 < 250)
            {
                int bx = (int)x - 14, by = (int)y - sh / 2;

                if (InkHere(buf, bx, by, sw, sh)) { dt = _clock.Elapsed.TotalMilliseconds - t0; break; }

                DrainMessages();
                // 鎱㈠抚妯℃嫙锛氬垎鐗囧繖锛屾瘡鐗囦箣鍚庣湅涓€鐪硷紙婀垮ⅷ灞備細鍏堜笂灞忥級銆?                int remain = delayMs;
                while (remain > 0)
                {
                    Thread.Sleep(Math.Min(5, remain));
                    remain -= 5;
                    if (InkHere(buf, bx, by, sw, sh)) { dt = _clock.Elapsed.TotalMilliseconds - t0; break; }
                }
                if (dt >= 0) break;

                if (_dirty || _drawing) { RenderAll(); _dirty = false; }
                if (InkHere(buf, bx, by, sw, sh)) { dt = _clock.Elapsed.TotalMilliseconds - t0; break; }
                Thread.Sleep(1);
            }
            if (dt >= 0) { res.Add(dt); hits++; }
            Thread.Sleep(35);                       // 鍋滅暀杩滃皬浜?400ms锛氫笉瑙﹀彂鍋滈】鎴愬瀷
        }
        SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(150);
        return res;
    }

    /// <summary>杩欏潡灏忓尯鍩熼噷鏈夋病鏈夊搧绾㈠ⅷ銆?/summary>
    private static bool InkHere(byte[] buf, int x, int y, int w, int h)
        => ScreenProbe.CaptureRegionInto(buf, x, y, w, h) && HasMagenta(buf, w * h * 4);

    /// <summary>
    /// 杞ㄨ抗鍙鎬э紙鍜?`--trailtest` 鍚屼竴濂楀彛寰勶級锛氫笉閰嶉€熴€侀€愬抚閲囨牱锛屾姤鎸変綇鏈熼棿鐨勫嘲鍊煎ⅷ鍍忕礌銆?    /// 鐢ㄥ畠鍥炵瓟"绯荤粺杞ㄨ抗鍒板簳鐢绘病鐢?鈥斺€斿杺鐐规暟鍙鏄庨€氶亾鍦ㄨ窇锛屼笉浠ｈ〃涓婁簡灞忋€?    /// </summary>
    private int LagTrailVisibilityProbe()
    {
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        SendMouse((int)(cx - 400), (int)cy, 0);
        SettleFrames(150);
        SendMouse((int)(cx - 400), (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(80);

        int peak = 0;
        for (int i = 1; i <= 30; i++)
        {
            SendMouse((int)(cx - 400 + i * 20), (int)(cy + MathF.Sin(i / 30f * 6.28f) * 60f), 0);
            for (int f = 0; f < 12; f++)
            {
                SettleFrames(1);
                int c = ScreenProbe.CountMagenta((int)cx - 500, (int)cy - 140, 1000, 280);
                if (c > peak) peak = c;
            }
        }
        SendMouse((int)(cx + 200), (int)cy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);
        Doc.Clear(); Doc.InvalidateAll();
        SettleFrames(120);
        return peak;
    }

    // =====================================================================
    //  鎵嬫満 120Hz 褰曞儚瀵圭収锛?-camlag锛?    // =====================================================================

    /// <summary>
    /// 鎵嬫満 120Hz 褰曞儚瀵圭収锛氬睆骞曚笂鍚屾椂鏈?*绯荤粺鍏夋爣**锛堟枩绗?= 杈撳叆鐪熷€硷紝鐑偣灏辨槸绗斿皷锛?    /// 鍜?*婀垮ⅷ**鈥斺€旀墜鏈烘媿鍒扮殑鏄鍒扮鐪熷€硷紙鍚?DWM 鍚堟垚 + 鎵弿杈撳嚭锛夛紝姣旇蒋浠舵姄灞忔洿纭€?    /// 姣忎竴妗ｇ敤**鍚屼竴濂楄剼鏈寲鍖€閫熸嫋鍔?*锛堝悓閫熷害銆佸悓璺濈锛夛紝閫愬抚閲?鍏夋爣灏?鈫?澧ㄥ皷"鐨?    /// 鍍忕礌璺濈锛岄櫎浠ラ€熷害灏辨槸姣鈥斺€斿悇妗ｄ箣闂村彲浠ョ洿鎺ユí姣斻€?    ///
    /// 灞忎笂鏍囪锛氬乏涓婅 N 鏉¤摑妯潯 = 绗?N 妗ｏ紱鎵嚎涓嬫柟姣?50 鐗╃悊鍍忕礌涓€涓伆鑹插埢搴︼紙灏哄瓙锛夈€?    /// 妗ｄ綅锛?=涓讳氦鎹㈤摼鑷粯 2=FastWet 3=绯荤粺杞ㄨ抗 4=浜у搧缁勫悎锛沗all` = 鍏ㄩ儴璺戜竴閬嶃€?    /// `--live N` 鏃朵笉鍋氬悎鎴愭嫋鍔紝鏀规垚"浣犺嚜宸辩敤绗斿啓 N 绉?锛堟墜鍐欐澘 / 鐪熺瑪鐢級銆?    /// </summary>
    private void CamLagTest(string modeArg, int speed, int sweeps, double liveSecs)
    {
        var modes = new (string Name, bool Trail, bool FastWet, bool SuppressMain)[]
        {
            ("涓讳氦鎹㈤摼鑷粯", false, false, false),
            ("FastWet",      false, true,  true),
            ("绯荤粺杞ㄨ抗",      true,  false, true),
            ("浜у搧缁勫悎",      true,  false, false),
        };

        Console.WriteLine();
        Console.WriteLine("=== 鎵嬫満 120Hz 褰曞儚瀵圭収锛?-camlag锛?==");
        Console.WriteLine("  灞忎笂鏍囪锛氬乏涓婅 N 鏉¤摑妯潯 = 绗?N 妗ｏ紱鎵嚎涓嬫柟姣?50 鐗╃悊鍍忕礌涓€涓伆鑹插埢搴︺€?);
        for (int i = 0; i < modes.Length; i++)
            Console.WriteLine($"    绗?{i + 1} 妗ｏ細{modes[i].Name}");
        Console.WriteLine("  鍏夋爣锛氱瑪宸ュ叿鍦ㄩ紶鏍?/ 鎵嬪啓鏉夸笅鏄剧ず**绯荤粺鏂滅瑪**锛堢儹鐐瑰氨鏄瑪灏栵級鈥斺€?
                          + "褰曞儚閲岄噺鈥滄枩绗斿皷 鈫?澧ㄥ皷鈥濈殑鍍忕礌璺濈锛屆?閫熷害 = 姣銆?);
        Console.WriteLine(liveSecs > 0
            ? $"  鏈鏄?--live锛氭瘡涓€妗ｄ綘鍐?{liveSecs:F0} 绉掞紙灏介噺鍖€閫熸潵鍥烇級銆?
            : $"  鎷栧姩锛氬悎鎴愰紶鏍?{speed} 鐗╃悊鍍忕礌/绉?脳 {sweeps} 瓒燂紙鍚岄€熷害銆佸悓璺濈锛屽悇妗ｅ彲姣旓級銆?);
        Console.WriteLine();

        CurrentColor = new Color4(1f, 0f, 1f, 1f);   // 鍝佺孩锛氬綍鍍忛噷鏈€濂借
        Tool = Tool.Pen;

        var pick = new List<int>();
        if (string.IsNullOrEmpty(modeArg) || modeArg == "all")
        {
            for (int i = 1; i <= modes.Length; i++) pick.Add(i);
        }
        else if (int.TryParse(modeArg, out int one) && one >= 1 && one <= modes.Length)
        {
            pick.Add(one);
        }
        else
        {
            Console.WriteLine($"  妗ｄ綅鍙傛暟涓嶈锛歿modeArg}锛堢敤 all 鎴?1~4锛?);
            _quit = true;
            return;
        }

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float leftX = cx - 450f, rightX = cx + 450f;
        float y = cy;

        foreach (int mi in pick)
        {
            var (name, trail, fastWet, suppressMain) = modes[mi - 1];
            if (trail && !OverlayWindow.InkTrailAvailable)
            {
                Console.WriteLine($"  绗?{mi} 妗?{name}锛歋KIP锛堣繖鍙版満鍣ㄦ病鏈夌郴缁熻建杩规帴鍙ｏ級");
                continue;
            }

            SetWetModeForLagTest(trail, fastWet, suppressMain);
            Doc.Clear();
            DrawCamMarkers(mi, leftX, rightX, y);
            Doc.ClearHistory();
            Doc.InvalidateAll();
            SettleFrames(250);

            // 瀛樹竴寮?鏍囪灏变綅"鐨勫浘锛堣嚜璇佸睆涓婃爣璁扮湡鐨勭敾鍑烘潵浜嗭紱鎴浘涓嶅惈绯荤粺鍏夋爣锛夈€?            ScreenProbe.SaveBmp(Path.Combine("reports", "shots", $"camlag-{mi}-markers.bmp"),
                                (int)(leftX - 160), (int)(y - 420), 1400, 720);

            Console.WriteLine($"== 绗?{mi} 妗ｏ細{name} ==");
            if (liveSecs > 0)
            {
                Console.WriteLine($"   璇风敤绗斿寑閫熸潵鍥炲啓 {liveSecs:F0} 绉掞紙鍏夋爣瑕佷竴璺窡鐫€绗斿皷锛夆€︹€?);
                double t0 = _clock.Elapsed.TotalMilliseconds;
                while (!_quit && _clock.Elapsed.TotalMilliseconds - t0 < liveSecs * 1000)
                {
                    if (OverlayWindow.VBlankPaced) Native.DwmFlush();
                    DrainMessages();
                    if (_dirty || _drawing) { RenderAll(); _dirty = false; }
                    else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, Native.QS_ALLINPUT, 0);
                }
            }
            else
            {
                RunCamSweeps(speed, sweeps, leftX, rightX, y, mi);
            }
            SettleFrames(400);
            Console.WriteLine($"   绗?{mi} 妗ｇ粨鏉熴€?);
        }

        foreach (var w in _windows) { w.EndInkTrail(); w.EndFastWet(); }
        SuppressActiveStroke = false;
        SettleFrames(100);
        Console.WriteLine("  鍏ㄩ儴妗ｄ綅缁撴潫锛堟墜鏈哄彲浠ュ仠浜嗭級銆?);
        _quit = true;
    }

    /// <summary>灞忎笂鏍囪锛氬乏涓婅 N 鏉¤摑妯潯锛? 绗?N 妗ｏ級+ 鎵嚎涓嬫柟姣?50 鐗╃悊鍍忕礌涓€涓埢搴︺€?/summary>
    private void DrawCamMarkers(int modeIndex, float leftX, float rightX, float y)
    {
        float dpi = DpiScale;
        var blue = new Color4(0.12f, 0.55f, 0.95f, 1f);
        var gray = new Color4(0.40f, 0.40f, 0.45f, 1f);

        // 妯″紡妯潯锛堝乏涓婅锛氱 N 妗ｅ氨 N 鏉★級
        for (int i = 0; i < modeIndex; i++)
            AddCamLine(leftX - 40f, y - 320f + i * 44f, leftX + 210f, y - 320f + i * 44f, blue, 14f * dpi);

        // 灏哄瓙锛氬熀绾?+ 姣?50 鐗╃悊鍍忕礌涓€涓埢搴︼紙姣?10 鏍煎姞闀匡紝鏂逛究鏁板ぇ鏁帮級
        AddCamLine(leftX - 60f, y + 40f, rightX + 60f, y + 40f, gray, 2f * dpi);
        int k = 0;
        for (float x = leftX; x <= rightX + 0.5f; x += 50f, k++)
        {
            float tick = (k % 10 == 0) ? 24f : 12f;
            AddCamLine(x, y + 40f - tick * 0.5f, x, y + 40f + tick * 0.5f, gray, 2f * dpi);
        }

        // 鎵嚎涓ょ鍚勪竴鏉＄煭绔栫嚎锛堟柟渚垮湪褰曞儚閲屽榻?璧风偣/缁堢偣"锛?        AddCamLine(leftX, y - 26f, leftX, y + 26f, gray, 2f * dpi);
        AddCamLine(rightX, y - 26f, rightX, y + 26f, gray, 2f * dpi);
    }

    /// <summary>鎶婁竴娈电煭绾夸綔涓哄共澧ㄥ姞杩涙枃妗ｏ紙缁欏睆涓婃爣璁扮敤锛夈€?/summary>
    private void AddCamLine(float x0, float y0, float x1, float y1, Color4 color, float width)
    {
        var s = new Stroke
        {
            Tool = Tool.Pen,
            Kind = StrokeKind.Freehand,
            Color = color,
            Width = width,
        };
        s.AddPoint(x0, y0, 1f, NowMs);
        s.AddPoint(x1, y1, 1f, NowMs);
        Doc.AppendStroke(s);
    }

    /// <summary>鑴氭湰鍖栧寑閫熸嫋鍔細浠庝竴绔埌鍙︿竴绔紝鏉ュ洖 `sweeps` 瓒燂紙鍚岄€熷害銆佸悓璺濈锛夈€?/summary>
    private void RunCamSweeps(int speed, int sweeps, float leftX, float rightX, float y, int modeIndex)
    {
        Console.WriteLine($"   寮€濮嬪綍鍍忥細鍚堟垚榧犳爣 {speed} 鍍忕礌/绉?脳 {sweeps} 瓒燂紙宸︹啋鍙斥啋宸︹€︼級銆?);
        SendMouse((int)leftX, (int)y, 0);
        SettleFrames(200);
        SendMouse((int)leftX, (int)y, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(150);

        bool right = true;
        for (int s = 0; s < sweeps; s++)
        {
            float from = right ? leftX : rightX;
            float to = right ? rightX : leftX;
            double durMs = MathF.Abs(to - from) / MathF.Max(1, speed) * 1000.0;
            double t0 = _clock.Elapsed.TotalMilliseconds;
            while (true)
            {
                double el = _clock.Elapsed.TotalMilliseconds - t0;
                if (el > durMs) break;
                float x = from + (to - from) * (float)(el / durMs);
                SendMouse((int)x, (int)y, 0);
                if (OverlayWindow.VBlankPaced) Native.DwmFlush();
                DrainMessages();
                if (_dirty || _drawing) { RenderAll(); _dirty = false; }
            }
            SendMouse((int)to, (int)y, 0);
            if (s == 0)
                ScreenProbe.SaveBmp(Path.Combine("reports", "shots", $"camlag-{modeIndex}-sweep.bmp"),
                                    (int)(leftX - 160), (int)(y - 420), 1400, 720);
            SettleFrames(120);                 // 瓒熼棿灏忓仠锛堣繙灏忎簬 400ms锛屼笉瑙﹀彂鍋滈】鎴愬瀷锛?            Console.WriteLine($"     绗?{s + 1}/{sweeps} 瓒熷畬鎴愶紙{(right ? "宸︹啋鍙? : "鍙斥啋宸?)}锛?);
            right = !right;
        }
        SendMouse((int)(right ? leftX : rightX), (int)y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(250);
    }

    /// <summary>鏈€鍙充竴涓搧绾㈠儚绱犵殑鍒楀彿锛?1 = 杩欏潡閲屾病鏈夊ⅷ锛夈€?/summary>
    private static int MaxMagentaX(byte[] buf, int w, int h)
    {
        int best = -1;
        for (int xx = w - 1; xx > best; xx--)
        {
            for (int yy = 0; yy < h; yy++)
            {
                int o = (yy * w + xx) * 4;
                if (buf[o] > 200 && buf[o + 1] < 90 && buf[o + 2] > 200) { best = xx; break; }
            }
        }
        return best;
    }

    /// <summary>杩欏潡缂撳啿閲屾湁娌℃湁鍝佺孩鍍忕礌銆?/summary>
    private static bool HasMagenta(byte[] buf, int len)
    {
        for (int o = 0; o + 3 < len; o += 4)
            if (buf[o] > 200 && buf[o + 1] < 90 && buf[o + 2] > 200) return true;
        return false;
    }

    private static double Median(IReadOnlyList<float> xs) => Quantile(xs, 0.5);
    private static double P90(IReadOnlyList<float> xs) => Quantile(xs, 0.9);
    private static double Median(IReadOnlyList<double> xs) => Quantile(xs, 0.5);
    private static double P90(IReadOnlyList<double> xs) => Quantile(xs, 0.9);

    private static double Quantile(IReadOnlyList<float> xs, double q)
    {
        if (xs.Count == 0) return double.NaN;
        var a = new float[xs.Count];
        for (int i = 0; i < a.Length; i++) a[i] = xs[i];
        Array.Sort(a);
        return a[Math.Clamp((int)Math.Ceiling(q * a.Length) - 1, 0, a.Length - 1)];
    }

    private static double Quantile(IReadOnlyList<double> xs, double q)
    {
        if (xs.Count == 0) return double.NaN;
        var a = new double[xs.Count];
        for (int i = 0; i < a.Length; i++) a[i] = xs[i];
        Array.Sort(a);
        return a[Math.Clamp((int)Math.Ceiling(q * a.Length) - 1, 0, a.Length - 1)];
    }

}
