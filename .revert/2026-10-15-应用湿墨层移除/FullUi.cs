using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 瀹屾暣鐣岄潰锛氭敹璧锋€佹槸**涓€涓悆**锛岀偣寮€鏄竴鏉?*鎸夐挳甯?*锛涚悆灏辨槸甯﹀瓙鏈€宸﹂偅涓€鏍笺€?///
/// 杩欐槸绗竴鐗堬紝鍒绘剰鍙仛"鎸夐挳 锛?鍛戒腑娴嬭瘯"锛堣壊甯︺€佺矖缁嗘粦鍧椼€併€屾洿澶氥€嶆娊灞夐兘杩樻病鏈夛級锛?/// 鍏堟妸**鍧愭爣 / DPI / 鑴忓尯 / 杈撳叆鎷︽埅**杩欏洓浠舵渶瀹规槗鍑洪敊鐨勪簨楠屽锛?/// 鍚庨潰鎼壊甯﹀拰鎶藉眽閮藉彧鏄爢浠ｇ爜锛堣 璁″垝-搴曞眰瀵规帴鐣岄潰.md 闃舵 2锛夈€?///
/// 瀹冨彧璁ゅ紩鎿庣殑涓変釜鎺ュ彛锛氱敾鑷繁銆佽В閲婄偣鍑汇€佹敼鐘舵€佽蛋鍛戒护銆?/// </summary>
public sealed class FullUi : IOverlayUi
{
    // ---- 鎬ц兘褰掑洜寮€鍏筹紙寮€鍙戞湡锛?-------------------------------------------
    //
    // 闈㈡澘姣忓抚鍒板簳璐靛湪鍝紝**涓嶈兘鐚?*锛氭妸杩欏嚑鍧楀垎鍒叧鎺夈€佸悓涓€浠藉唴瀹瑰悇閲忎竴閬嶏紝
    // 鎵嶇煡閬撹浼樺寲璋侊紙瀹炴祴锛氬畬鏁村睍寮€鎬佹瘮涓嶆寕鐣岄潰璐?2.44 姣锛岃秴杩?1.5 鐨勭孩绾匡級銆?    internal static bool PerfSkipShadow;
    internal static bool PerfSkipChrome;        // 鍑规Ы 锛?椤堕儴鍐呴珮鍏?    internal static bool PerfSkipSwatchDetail;  // 鑹茬墖鐨勫唴楂樺厜涓庨€変腑鐜?    internal static bool PerfSkipIcons;

    // ---- 鎸夐挳琛?-------------------------------------------------------------
    //
    // index 0 鏄?鏀惰捣鏍?锛堝氨鏄偅涓悆缂╁埌甯﹀瓙閲岋級锛?..N 鏄伐鍏枫€?    // 鍒嗙粍鐢?GroupEnd 鏍囧嚭鏉ワ細缁勯棿鐨勯棿闅旇鏄庢樉澶т簬缁勫唴锛岀溂鐫涙墠鍒嗗緱寮€鍧椼€?    private static readonly (string Icon, string Filled, string Tip)[] Cells =
    {
        ("pen",       "penFilled",       "鏀惰捣"),
        ("cursorArrow", "cursorArrowFilled", "榧犳爣锛堢┛閫忕偣鍑伙級"),
        ("board",     "board",           "鐧芥澘"),
        ("pen",       "penFilled",       "绗?),
        ("highlighter","highlighterFilled","鑽у厜绗?),
        ("laser",     "laserFilled",     "婵€鍏夌瑪"),
        ("eraser",    "eraserFilled",    "姗＄毊鎿?),
        ("select",    "selectFilled",    "閫夋嫨"),
        ("shapes",    "shapesFilled",    "鍥惧舰"),
        ("capture",   "captureFilled",   "鎴睆"),
        ("undo",      "undoFilled",      "鍚庢挙"),
        ("redo",      "redoFilled",      "閲嶅仛"),
        ("more",      "moreFilled",      "鏇村"),
    };

    /// <summary>姣忎竴缁勭殑鏈€鍚庝竴鏍硷紙涓嬫爣锛夈€傜粍涔嬮棿鐢讳竴鏉″垎闅旂嚎銆?/summary>
    private static readonly int[] GroupEnds = { 0, 2, 6, 9, 12 };

    // ---- 鐣岄潰妗ｄ綅 -----------------------------------------------------------
    //
    // 涓夋。锛堢敤鎴峰畾鐨勯粯璁ゆ槸"瀹屾暣妗?锛夛細
    //   路 鏋佺畝 = 涓€鏉?*鐭兌鍥?*锛屽彧鐣欐渶甯哥敤鐨勫叚鏍硷紱
    //   路 鑷畾涔?= 鑰佸笀鑷繁閽夌殑閭ｄ唤闆嗗悎锛堥粯璁わ紳鍏ㄩ儴锛岀瓑浜庡畬鏁存。锛屾敼杩囦箣鍚庢墠涓嶄竴鏍凤級锛?    //   路 瀹屾暣 = 鍏ㄩ儴鍗佷簩鏍笺€?    // 瑙勫垯涓€鏉★細**鏋佺畝涓庤嚜瀹氫箟閮藉繀椤绘槸瀹屾暣妗ｉ『搴忕殑瀛愬簭鍒?*鈥斺€斿悓涓€涓伐鍏峰湪鍝竴妗ｉ噷
    // 閮藉湪鍚屼竴涓浉瀵逛綅缃紝鏉ュ洖鍒囨。涓嶇敤閲嶆柊鎵俱€?
    /// <summary>妗ｄ綅銆?/summary>
    private enum Profile { Mini = 0, Custom = 1, Full = 2 }

    /// <summary>
    /// 鏋佺畝妗ｇ暀鍝嚑鏍笺€?*鐓ф妱鍋囬潰鏉跨殑缁撹**锛堥紶鏍?鐧芥澘/绗?姗＄毊/鍚庢挙/鏇村锛夛細
    /// 琛?榧犳爣"鏄洜涓虹湅 PPT 鏃惰鑳界偣涓嬪眰锛岃ˉ"鍚庢挙"鏄洜涓鸿鐢讳竴绗旇鏈夋晳銆?    /// </summary>
    private static readonly int[] MiniCells = { 1, 2, 3, 6, 10, 12 };

    private Profile _profile = Profile.Full;

    /// <summary>鑷畾涔夋。閽変簡鍝簺鏍硷紙涓嬫爣瀵归綈 Cells锛? 鍙?鏀惰捣鏍?姘歌繙鍦紝涓嶅弬涓庨拤锛夈€?/summary>
    private readonly bool[] _pinned = CreateAllPinned();

    /// <summary>绗斻€佹鐨€併€屾洿澶氥€嶆槸**瀹夊叏椤?*锛氬彇娑堥拤浣忎箣鍚庡氨娌℃硶鐢ㄤ簡锛屾墍浠ヤ笉璁稿彇娑堛€?/summary>
    private static bool CanUnpin(int cell) => cell is not (3 or 6 or 12);

    private static bool[] CreateAllPinned()
    {
        var a = new bool[Cells.Length];
        for (int i = 1; i < a.Length; i++) a[i] = true;
        return a;
    }

    /// <summary>
    /// 杩欎竴妗ｅ疄闄呮樉绀虹殑鏍煎瓙锛堝惈 0 鍙锋敹璧锋牸锛夛紝**濮嬬粓鎸夊畬鏁存。鐨勯『搴忔帓鍒?*銆?    /// 甯冨眬銆佸懡涓€佺粯鍒朵笁澶勯兘鍙杩欎釜鍒楄〃鈥斺€斾笉杩欎箞缁熶竴锛屽垏妗ｆ椂鎬绘湁涓€澶勫繕浜嗚窡鐫€鍙樸€?    /// </summary>
    private int[] VisibleCells()
    {
        var list = new List<int> { 0 };
        switch (_profile)
        {
            case Profile.Mini:
                list.AddRange(MiniCells);
                break;
            case Profile.Custom:
                for (int i = 1; i < Cells.Length; i++) if (_pinned[i]) list.Add(i);
                break;
            default:
                for (int i = 1; i < Cells.Length; i++) list.Add(i);
                break;
        }
        return list.ToArray();
    }

    private IUiHost _host;
    private Widgets _widgets;
    private RectF _screen;                 // 閫昏緫铏氭嫙妗岄潰锛圠ayout 缁欑殑锛?    /// <summary>
    /// 閫昏緫**涓诲睆宸ヤ綔鍖?*锛堝睆骞曞噺鎺変换鍔℃爮锛夈€傚彧鐢ㄥ湪**榛樿浣嶇疆**涓婏紙瑙?<see cref="RawAnchor"/>锛夛細
    /// 鐢ㄦ埛 2026-09-27 瑕?闈㈡澘绱ц创浠诲姟鏍忎笂鏂广€佷袱鑰呬笉閲嶅彔"銆?    ///
    /// 鈿?鍒嬁瀹冩浛浠?<see cref="_screen"/>锛氬す鍙栥€佽创杈归殣钘忕殑浣嶇Щ銆佸惛闄勯兘杩樺緱鎸?*灞忓箷**绠椻€斺€?    /// 璐磋竟闅愯棌閭ｆ潯娉ㄩ噴鍐欏緱寰堟竻妤氾紙闈㈡澘涓嶄細"钘忓埌浠诲姟鏍忓悗闈?锛屽彧鏈夋帹鍑哄睆骞曟墠鐪熺殑鐪嬩笉瑙侊級銆?    /// </summary>
    private RectF _work;
    // 鎷栬繃涔嬪悗鐨勪綅缃細**X = 涓绘潯宸︾锛孻 = 涓绘潯涓婅竟**銆俷ull = 杩樻病鎷栬繃锛堢敤榛樿浣嶇疆锛夈€?    //
    // 杩欎竴鏍?2026-09-17 鏉ュ洖鏀硅繃涓€娆★紝鏈€鍚?*鍥炲埌鍋囬潰鏉跨殑瑙勫垯**锛岀悊鐢辫鍦?RawAnchor锛?    //   路 绗竴鐗堬細娌℃嫋杩囨寜"鏁存潯甯﹀瓙灞呬腑"绠椼€佹嫋杩囦箣鍚庢寜宸︿笂瑙掔畻 鈫?鍚屼竴涓墜鍔夸袱绉嶅弽搴旓紱
    //   路 涓棿璇曡繃"閿氱悆蹇冦€佷袱杈归暱"鈫?鐞冧細婊戣蛋锛堢偣瀹冩敹璧锋潵瑕侀噸鏂扮瀯锛夛紱
    //   路 鐜板湪锛?*閿?灞曞紑鍚庡甫瀛愮殑宸︾"锛岃€屼笖閭ｆ潯甯﹀瓙榛樿灞忓箷灞呬腑**銆?    //     鐞冨仠鍦ㄥ甫瀛愬乏绔笉鍔紝灞曞紑锛濆線鍙抽暱锛屽睍寮€鍚庢暣鏉″甫瀛愭濂藉眳涓€斺€旂敤鎴疯鐨勫氨鏄繖涓€?    //
    // 杩欓噷鐗规剰涓嶇敤 RectF 鏉ヨ〃绀?娌℃嫋杩?锛歚RectF` 鐨勯粯璁ゅ€兼槸鍏?0锛岃€?    // `IsEmpty` 鍒ょ殑鏄?`MaxX < MinX`鈥斺€斿叏 0 鐨勭煩褰?*涓嶇畻绌?*锛?    // 浜庢槸"娌℃嫋杩?浼氳褰撴垚"鎷栧埌浜?(0,0)"锛堣嚜妫€褰撳満鎶撳埌杩囪繖涓€鏉★級銆?    private Vector2? _anchor;
    private readonly Anim _expand;         // 0 = 鐞冿紝1 = 甯﹀瓙

    private int _hover = -1;               // -2 = 鐞冿紝0.. = 鏍煎瓙锛?1 = 娌℃湁
    private int _press = -1;
    private Vector2 _pressPos;
    private Vector2 _dragStartAnchor;
    private bool _dragging;                // 宸茬粡瓒呰繃鎷栧姩闃堝€?
    // ---- 鎮仠鎻愮ず锛圱ooltip锛?026-10-02锛?-------------------------------------
    //
    // 琛屼负锛氶紶鏍囧仠鍦ㄥ悓涓€涓笢瑗夸笂 **500ms**锛圱okens.TipDelayMs锛岀収 WPF 榛樿 400 鍙栨澗涓€妗ｏ級锛?    // 娴嚭涓€寮犲皬鍗＄墖锛堝悕绉?+ 褰撳墠閿綅 + 涓€鍙ヨ鏄庯級锛涚Щ寮€/鎸変笅/鎷栧姩绔嬪埢鏀惰捣銆?    //
    // 鐩爣缂栧彿**娌跨敤 `HoverAt` 閭ｄ竴濂?*锛?2 鐞?/ 0..12 鏍?/ 100+ 鑹茬墖 / 200+ 鍒嗘 /
    // 300 婊戞潯 / 400 鍔ㄤ綔鎸夐挳 / 500 铏氬疄绾匡級鈥斺€斿悓涓€浠界紪鍙锋棦绠￠珮浜篃绠℃彁绀猴紝
    // 涓嶅彟鍐欎竴濂?鍝潡绠楀摢鍧?鐨勬槧灏勶紙閭ｈ繜鏃╀細鍜岄珮浜紓绉伙級銆?    //
    // 瑙﹀睆/鎵嬪啓绗?*娌℃湁鎮仠浜嬩欢**锛堜粨搴撹€佽鐭╋細鍥惧舰宸ュ叿鐨?鎮仠棰勮"灏辨槸鍥犳鏀规垚
    // "鎸変綇鎷?鐨勶級锛屾墍浠ユ彁绀哄彧褰撳寮猴紝闈㈡澘涓婄殑甯告樉鏂囧瓧涓€涓瓧閮戒笉鍔ㄣ€?    private bool _tipEnabled = true;          // 寮€鍏冲湪銆屾洿澶?鈫?璁剧疆 鈫?澶栬銆嶏紝榛樿寮€
    private const int TipNoTarget = int.MinValue;
    private int _tipTarget = TipNoTarget;     // 鐜板湪鍋滃湪鍝紙娌℃湁 = 涓嶅嚭鎻愮ず锛?    private double _tipSinceMs;               // 浠€涔堟椂鍊欏仠涓婂幓鐨勶紙杩囦簡寤惰繜鎵嶆樉绀猴級
    private bool _tipShown;                   // 宸茬粡杩囦簡寤惰繜銆佹鍦ㄦ樉绀猴紙闈欐€侊紝涓嶅啀鐑у抚锛?    private readonly Anim _tipFade;           // 0鈫? 娣″叆

    // 瑙︽懜/绗旈暱鎸夛紙2026-10-02 绗簩杞級锛氭墜鎸囨寜浣忎笉鍔?600ms 鍑烘彁绀猴紝**鏉炬墜杩欎竴娆′笉鎵ц**銆?    // 鍜屾偓鍋滅殑鍏崇郴瑙併€婅皟鐮?鎮仠鎻愮ず-Tooltip.md銆?0.3.1锛?鍋滃湪瀹冧笂闈?闂畠锛屾病鐪熺偣=娌″彂鐢?銆?    // 榧犳爣**姘镐笉杩涘叆杩欐潯**锛堥紶鏍囨寜浣?鎷栧姩锛夛紱绗旀帴瑙﹀拰鎵嬫寚涓€鏍风畻锛堟湁鎮仠鐨勭瑪骞虫椂璧版偓鍋滐級銆?    private double _tipHoldStart = double.NegativeInfinity;   // 闀挎寜璁℃椂璧风偣
    private int _tipHoldTarget = TipNoTarget;                 // 鎸変綇鐨勬彁绀虹洰鏍?    private bool _tipHoldFired;                               // 宸插脊鎻愮ず锛氳繖娆℃寜涓嬩綔搴?    private double _tipLingerUntil;                           // 鏉炬墜鍚庢彁绀哄仠鍒颁粈涔堟椂鍊欙紙0=涓嶅仠锛?    private uint _pressId;                                    // 鎸変笅鐨勬寚閽?id锛堟尅鍒殑鎸囬拡鎼呭眬锛?
    /// <summary>
    /// 涓婂甫锛堣缃潯锛夌幇鍦ㄦ樉绀鸿皝鐨勮缃€傞粯璁ゆ槸"绗?銆?    /// 涓婂甫璺熺潃**褰撳墠宸ュ叿**璧帮細閿洏鎹㈠伐鍏锋椂闈?<see cref="OnStateChanged"/> 閲岄偅鏉?    /// 鍚屾瑙勫垯鎶婂畠鎺板洖鏉ワ紝涓嶇劧浼氬嚭鐜?鎷跨潃姗＄毊銆佷笂甯﹁繕鏄壊鏉?銆?    /// </summary>
    private int _bandCell = 3;

    /// <summary>涓婁竴娆＄湅鍒扮殑宸ュ叿銆傜敤瀹冨垽鏂?宸ュ叿鐪熺殑鎹簡"锛岃€屼笉鏄?甯﹀瓙瀵逛笉瀵?銆?/summary>
    private Tool _lastTool = Tool.Pen;

    /// <summary>
    /// 涓婁竴娆＄湅鍒扮殑"鍦ㄦ斁鏄犲悧"銆傜敤瀹冭**杩涙斁鏄犵殑閭ｄ竴鍒?*锛堣竟娌匡級锛?    /// 瑙?<see cref="OnStateChanged"/> 閲岄偅涓€娈点€?    /// </summary>
    private bool _lastPpt;

    /// <summary>涓婁竴娆＄湅鍒扮殑绌块€忕姸鎬併€傜敤瀹冭**杩涚┛閫忕殑閭ｄ竴鍒?*锛堣竟娌匡級鈥斺€?    /// 杩涚┛閫忔椂鎶婁笂甯︽敹鍥為偅鏉?10 鍍忕礌鑹茬嚎锛岀┛閫忔湡闂翠笉璁稿啀寮狅紝瑙?<see cref="OnStateChanged"/>銆?/summary>
    private bool _lastPass;

    private bool _sliderDragging;

    // ---- 绗旂殑铏氬疄绾垮垏鎹紙鐢ㄦ埛 2026-09-19 绗?2 浠讹級--------------------------
    /// <summary>
    /// 鑹插甫鏉′笂閭ｄ釜**铏氬疄绾垮垏鎹?*鐨勬崲鎸″姩鐢伙紙0 鈫?1 = "鏂扮嚎鍨嬫贰鍏?锛夈€?    ///
    /// 涓轰粈涔堢粰瀹冧竴涓姩鐢伙細杩欎竴鏍肩敾鐨勬槸**涓€灏忔绾?*锛堝疄绾?铏氱嚎/鐐圭嚎鐨勬牱瀛愶級锛?    /// 鐩存帴鎹細鍍忛棯涓€涓嬪睆銆佽€屼笖鐪嬩笉鍑?鍒氭墠鎸夊埌浜嗘病鏈?鈥斺€?6脳26 鐨勫皬鏍煎瓙閲岋紝
    /// 棰滆壊涓庡舰鐘堕兘鏄竴娆℃€ц烦鍙樼殑銆傚仛娉曪細鏃х嚎鍨嬫贰鍑恒€佹柊绾垮瀷娣″叆锛?    /// 鍚屾椂寮鸿皟鑹叉弿杈归棯涓€涓嬶紱鏃堕暱澶嶇敤 <see cref="Tokens.RailMs"/>锛堝拰鑹插甫寮€鍚堝悓涓€濂楋級銆?    /// </summary>
    private readonly Anim _dashFade;
    /// <summary>鎹㈡尅鍔ㄧ敾閲?浠庡摢涓€妗ｆ潵"锛堟贰鍑虹殑閭ｄ竴鏉＄嚎鐢荤殑鏄畠锛夈€?/summary>
    private StrokeDash _dashFadeFrom = StrokeDash.Solid;

    /// <summary>娣辫壊涓婚锛氭墜鍔ㄥ紑鍏筹紙鐢ㄦ埛瀹氱殑锛夛紝搴曡壊/鍥炬爣/鎻忚竟鏁村鎹€?/summary>
    private bool _dark;

    /// <summary>璐磋竟闅愯棌锛氶粯璁ゅ叧锛堢敤鎴峰畾鐨勶級銆傚紑浜嗕互鍚庤创杈规椂鍙湶 8 鍍忕礌鐨勫ご銆?/summary>
    private bool _hideEnabled;
    private readonly Anim _peek;           // 0 = 鍙墿闇插ご锛? = 瀹屽叏鏄剧ず
    private readonly Anim _rail;           // 0 = 骞虫椂閭ｆ潯 10 鍍忕礌鑹茬嚎锛? = 瀹屾暣璁剧疆鏉?    /// <summary>鑹插甫甯稿紑锛堛€岃缃?鈫?澶栬 鈫?鑹插甫甯稿紑銆嶏紝2026-10-05锛夛細涓€鐩存憡鐫€锛屼笉鍙備笌鎮仠鏀舵斁銆?/summary>
    private bool _railPinned;
    /// <summary>瑙︽懜鎶婅缃潯"甯﹀嚭鏉?鍚庣殑淇濇寔鎴锛堣Е鎽告病鏈夋偓鍋滐紝涓嶈兘闈犳寚閽堜綅缃淮鎸佸睍寮€锛夈€?/summary>
    private double _railTouchHoldUntilMs = double.NegativeInfinity;
    private bool _railHover;

    /// <summary>鎮仠鎰忓浘鐨勪袱涓椂鍒伙細杩涚儹鍖?120ms 鎵嶅睍寮€銆佺寮€ 220ms 鎵嶆敹鍥炩€斺€旇矾杩囦笉绠楁暟銆?/summary>
    private double _railEnterAtMs = double.NegativeInfinity;
    private double _railExitAtMs = double.NegativeInfinity;
    /// <summary>鍐欏瓧涓庤壊甯︾殑杈圭晫锛?026-10-09 浜岃疆纭鍒欙級锛氫笂涓€甯ф槸鍚﹀湪鍐欏瓧锛堣竟娌挎娴嬶級銆?/summary>
    private bool _railWasDrawing;
    /// <summary>鏈€杩戜竴绗旂殑鎶墜鏃跺埢锛?浣欐俯"鐢ㄥ畠锛堟姮鎵嬪悗 <see cref="Tokens.RailRestMs"/> 鍐呬笉璁稿紑濮嬪紶寮€锛夈€?/summary>
    private double _railStrokeEndMs = double.NegativeInfinity;
    /// <summary>鍐欏瓧鍚?*蹇呴』璁╂寚閽堢寮€鍒ゅ畾鍖轰竴娆?*銆佸啀鍋滃洖鏉ワ紝鎵嶅厑璁稿紶寮€锛堥槻"鍐欏畬杩樺湪闄勮繎灏卞脊鍗＄墖"锛夈€?/summary>
    private bool _railRequireLeave;
    /// <summary>鏈€杩戜竴娆?鎸囬拡浣嶇疆"鏇存柊鐨勬椂鍒伙紙PointerMove/PointerDown 閲屽埛锛夆€斺€?    /// 鍒ゅ畾"鎸囬拡鐪熺殑绂诲紑杩?瑕佺敤瀹冿細**娌℃湁鏂颁綅缃氨鍒綋浣滅寮€**锛堣惤绗旀湡闂村紩鎿庝笉杞彂鎮仠锛?    /// railHover 浼氭槸鏃у€硷紱鎷挎棫鍊兼竻 requireLeave 浼氳瑙勫垯褰㈠悓铏氳锛夈€?/summary>
    private double _railHoverStamp = double.NegativeInfinity;
    private bool _hoverInside;             // 鎸囬拡鍦?鐪嬪緱瑙佺殑閭ｄ竴鍧?閲?    private double _leftAtMs = double.NegativeInfinity;
    /// <summary>瑙︽懜/绗斿敜鍑鸿创杈归潰鏉垮悗鐨?鍋滅暀鎴"锛氭澗鎵嬪悗鍒?0.7s 灏辨敹锛堟墜鎸囪繕瑕佸啀鐐瑰伐鍏凤級銆?    /// 鍙"鎸夊湪闈㈡澘涓?鐨勯偅涓€娆¤Е鎽哥敓鏁堬紱榧犳爣閭ｅ涓嶅彉銆?/summary>
    private double _peekHoldUntilMs = double.NegativeInfinity;
    /// <summary>
    /// "鍙互寮€濮嬭嚜鍔ㄦ敹璧锋潵浜?鐨勫紑鍏筹細鎸囬拡纰拌繃闈㈡澘涓€娆′箣鍚庢墠缃湡銆?    /// 娌＄杩囦箣鍓嶄竴寰嬩繚鎸佸畬鏁存樉绀衡€斺€斿惎鍔ㄦ椂涓嶈涓€涓婃潵灏辨敹鎴愬睆骞曞簳杈归偅鏉￠湶澶达紙瑙?UpdatePeek锛夈€?    /// </summary>
    private bool _peekArmed;

    /// <summary>璁剧疆瀛愰〉閲岀殑琛岋紙鍚姩鍣ㄧ殑搴曟爮涓嶅湪杩欏紶琛ㄩ噷锛夈€?*椤哄簭鎸変笁鍒楅噷鐨勫竷灞€璧?*锛?    /// 澶栬锛?锛? 涔﹀啓锛?锛? 澧ㄨ抗锛?锛変笁鍒楀苟鎺掆€斺€旇 <see cref="MoreRowRect"/>銆?/summary>
    private enum Row { DarkTheme, AutoHide, RailPin, Tooltip, UiSize, DwellShape, Pressure, Predict, WetMode, RestoreInk, PptAutoSave, HistoryDays }

    /// <summary>
    /// 琛岃〃锛?*缁樺埗 / 鍛戒腑 / 鎵ц / 鑷閮借杩欎竴浠?*锛堟湰浠?鍚屼竴浠藉悕鍗曞啓涓ゅ蹇呮紡涓€澶?鐨勮€佹瘺鐥咃級銆?    /// `Hint` 鏄爣绛句笅闈㈤偅琛?11px 灏忕伆瀛楋紝鍙湁闇€瑕佽В閲婄殑琛屾墠缁欍€?    /// </summary>
    private static readonly (Row Kind, string Label, bool Dangerous, bool Gray, string Hint)[] Rows =
    {
        (Row.DarkTheme, "娣辫壊涓婚", false, false, ""),
        (Row.AutoHide, "璐磋竟闅愯棌", false, false, ""),
        // 鑹插甫甯稿紑锛?026-10-05 鐢ㄦ埛瀹炴祴鐐瑰悕锛夛細璁剧疆鏉′竴鐩存憡鐫€銆傝Е鎽稿睆涓婇偅鏉?10 鍍忕礌鐨勮壊绾垮緢闅剧偣涓紝
        // 瑙︽懜鐐瑰伐鍏锋牸涔熶細鑷姩甯﹀嚭鏉ワ紙鑷€傚簲锛岃 UpdateRail 鐨?_railTouchHoldUntilMs锛夈€?        (Row.RailPin, "鑹插甫甯稿紑", false, false, "璁剧疆鏉′竴鐩存憡寮€锛堣Е鎽稿睆涓嶇敤鍘荤閭ｆ潯鑹茬嚎锛?),
        // 鎮仠鎻愮ず锛?026-10-02锛夛細榧犳爣鍋滀綇鍗婄銆佹墜鎸囬暱鎸夛紝娴嚭"鍚嶇О + 蹇嵎閿?+ 璇存槑"銆?        // 榛樿寮€锛涜Е灞忔病鏈夋偓鍋滐紝鎵€浠ラ暱鎸夋槸瀹冨湪瑙︽懜涓婄殑绛変环鐗╋紙瑙?Tooltip 閭ｄ竴鑺傦級銆?        // **鑼冨洿鏄敹绐勮繃鐨?*锛氬彧缁欏浘鏍?only / 甯﹀揩鎹烽敭 / 闅愯棌鎵嬪娍锛屽埆鐨勶紙鑹插甫銆佹枃瀛楁銆?        // 鐐逛竴涓嬪氨瑙佺粨鏋滅殑锛夐兘涓嶉厤鈥斺€斿垽鎹銆婅皟鐮?鎮仠鎻愮ず-Tooltip.md銆?0.8銆?        (Row.Tooltip, "鍔熻兘鎻愮ず", false, false, "榧犳爣 / 绗旀偓鍋溿€佹墜鎸囬暱鎸夛紝鏄剧ず鍚嶇О涓庡揩鎹烽敭"),
        // 鐣岄潰缂╂斁锛?026-10-10 鐢ㄦ埛鎷嶆澘"鍋?A锛氭。浣嶅紡鍏ㄥ眬缂╂斁"锛夛細浜旀。 0.8/0.9/1/1.15/1.3锛?        // 鐐逛竴涓嬫崲涓€妗ｃ€佸埌椤跺洖澶淬€?*缂╃殑鏄晫闈㈣嚜宸?*锛堝伐鍏峰甫 / 鑹插甫 / 鎶藉眽 / 璁剧疆椤?/ 鍥惧簱闈㈡澘锛夛紱
        // 绗斿銆佺敾甯冨唴瀹硅繖浜?绾镐笂鐨勫昂"涓嶈窡鐫€鍔ㄣ€?        (Row.UiSize, "鐣岄潰澶у皬", false, false, "宸ュ叿甯﹀拰闈㈡澘鐨勫ぇ灏忥紱鐐逛竴涓嬫崲涓€妗ｏ紙0.8锝?.3锛屼簲妗ｏ級"),
        // 鍋滈】鎴愬瀷锛?026-09-23 绗簩鍗佹壒锛岃 璁″垝-鍥惧舰宸ュ叿.md 搂鍥涘崄浜岋級锛?        // 鎵嬪啓涓€绗斿仠浣?400ms 鈫?鎶婂畠鍙樻垚瑙勬暣鍥惧舰銆?*榛樿寮€**锛堢敤鎴峰畾鐨勶細
        // "鍥犱负鏄仠椤垮彉锛屾墍浠ラ粯璁ゅ紑"锛夛紝鎵€浠ヨ繖涓€琛岀殑寮€鍏冲垵濮嬪氨鏄€屽紑銆嶃€?        // 鍏虫帀 = 浠ュ悗鐢荤殑閭ｄ簺涓嶅啀鍙備笌锛涘凡缁忓彉鍑烘潵鐨勫浘褰笉鍙楀奖鍝嶏紙閭ｆ槸鎾ら攢鐨勪簨锛夈€?        (Row.DwellShape, "鍋滈】鍙樺浘褰?, false, false, ""),
        // 鍘嬫劅绮楃粏锛?026-10-01锛屾壒娆?0.2锛夛細榛樿寮€锛涘叧鎺?= 鏁村潡鏉跨瓑瀹斤紝
        // 鎵嬪啓鏉跨殑娴佺晠 / 棰勬祴涓嶅彈褰卞搷锛堟覆鏌撴湡寮€鍏筹紝鏂囨。閲岀殑鍘嬪姏鏁版嵁涓嶅姩锛夈€?        (Row.Pressure, "鍘嬫劅绮楃粏", false, false, "鍏虫帀鍚庢墍鏈夌瑪杩圭瓑瀹斤紙鎵嬪啓鏉跨収鏍锋祦鐣咃級"),
        // 澧ㄨ抗棰勬祴锛?026-10-09 鍥炲綊锛?*2026-10-14 鍙戝竷娴嬭瘯鐗堣捣榛樿寮€**锛?026-10-15锛?        // 鍞竴绠楁硶 = 绾挎€ч娴嬶紙Chromium 鍚屾棌锛夛紝涓?*鍙墿杩欎竴涓紑鍏?*鈥斺€斻€岄娴嬫椂闀裤€?        // 闅忔绉掕皟鑺備竴璧锋挙闄わ紱鎯冲叧锛氳缃噷鐐规帀杩欎竴琛?/ `--nopredict2`锛夈€?        // 閾捐矾锛氳琛?鈫?ActivateRow 鈫?IsOn 鈫?SavePrefs锛?鍏宠繃"鎵嶅啓 "0"锛夈€?        (Row.Predict, "澧ㄨ抗棰勬祴", false, false, "璁╁ⅷ灏炬洿璺熸墜涓€鐐癸紙榛樿寮€锛夛紱鍏充簡绗斿熬鏇寸ǔ褰?),
        // 婀垮ⅷ妗ｄ綅锛?026-10-13 鍔狅紝鐢ㄦ埛鐐瑰悕瑕?鍦ㄨ缃噷鑳界湅鍒板埌浜嗗摢涓€妗?锛夛細
        // **鐐逛竴涓嬫崲涓€妗?*锛堝拰銆岀晫闈㈠ぇ灏忋€嶅悓涓€涓墜鎰燂紝鍒伴《鍥炲ご锛夛紝鍙充晶鏄剧ず褰撳墠妗ｃ€?        // 鍒ゆ嵁/钀界洏閮藉湪寮曟搸锛堣 Engine.SetWetModeFromUi锛夛紱鐣岄潰鍙樉绀?Host.State.WetMode銆?        // 鍥涙。锛? 鍘熻矾寰勶紙榛樿锛屼粈涔堥兘涓嶅姞锛? 1 +绯荤粺濮旀墭杞ㄨ抗 / 2 +搴旂敤婀垮ⅷ灞?/ 3 鍏ㄥ紑銆?        (Row.WetMode, "婀垮ⅷ妗ｄ綅", false, false,
         "鐐逛竴涓嬫崲涓€妗ｏ細0 鍘熻矾寰勶紙榛樿锛? 1 +绯荤粺濮旀墭杞ㄨ抗锛堟帹鑽愶級/ 2 瀹為獙锛氬簲鐢ㄦ箍澧ㄥ眰 / "
         + "3 +杞ㄨ抗涓庡厹搴曘€傚彧鍔犱笉鍑忥紝鍏冲洖 0 涓庡師鏉ュ畬鍏ㄤ竴鏍凤紱"
         + "鎵嬫満褰曞儚瀹炴祴锛氱郴缁熻建杩规渶璺熸墜锛圵in11 鎵嶆湁锛夛紝搴旂敤婀垮ⅷ灞備笌鍘熻矾寰勫熀鏈浉褰?),
        // 澧ㄨ抗涓夋潯鍋忓ソ锛堝師鏈湪銆屽ⅷ杩广€嶉〉锛?026-10-02 鍚姩鍣ㄦ敼鐗堝悗鎼繘璁剧疆瀛愰〉锛夈€?        (Row.RestoreInk, "鑷姩鎭㈠涓婃鏉夸功", false, false, "涓嬫鍚姩鎺ヤ笂杩欐鐨勬澘涔?),
        // 鈿?鍚嶇О 2026-10-10 鏀癸細杩欎釜寮€鍏?*涓嶅彧绠?PPT**鈥斺€旀枃妗ｏ紙PDF/鍥剧墖锛夋壒娉ㄧ殑鎸夐〉
        //   鑷姩淇濆瓨 / 瑁呰浇涔熻蛋瀹冿紙DocImport 涓夊鍒?PptAutoSaveOn锛夈€傚彨"璇句欢"鏈€璐达細
        //   PPT 鍜屾枃妗ｉ兘鏄€佸笀鐨勮浠讹紙"鏂囨。"鍦ㄦ垜浠晫闈㈣姹囬噷涓撴寚 PDF/鍥剧墖閭ｇ被锛夈€?        (Row.PptAutoSave, "璇句欢澧ㄨ抗鑷姩淇濆瓨", false, false, "PPT 鏀炬槧 / 鏂囨。鎵撳紑鏃讹紝鎵规敞鎸夐〉鑷姩淇濆瓨銆佷笅娆¤嚜鍔ㄨ杞?),
        (Row.HistoryDays, "鍘嗗彶娓呯悊", false, false, "杩囨湡 PPT 缂撳瓨 / 鎵撳紑鍓嶅浠?/ 鍘嗗彶鏉夸功锛屽惎鍔ㄦ椂娓呮帀"),
        // 鏉夸功鍘嗗彶鏈韩娌℃湁寮€鍏筹紙榛樿鐣欍€侀粯璁や笉鏄剧ず锛夛紱涓婇檺鏄?鎬婚噺 1 GB銆佷唤鏁颁笉闄?锛?        // 瑙?BoardHistory銆傝€佸笀鎯虫竻绌哄氨鍒?`%LOCALAPPDATA%\InkTeach\History`锛屾垨鎶婅繖涓€琛岃鎴?7 澶┿€?        // 鈿?銆岀簿缁嗙瑪杩广€嶃€岃Е鎽告墜鍔裤€嶄袱涓紑鍏?2026-10-09 鏅?*宸叉挙**锛堢敤鎴风湡鏈鸿瘯绋筹細
        //   "寮€鍏虫嬁鎺夛紝榛樿寮€"锛夆€斺€斾袱鏉¤涓洪兘甯稿紑浜嗭紝鍙墿寮€鍙戝鐓у弬鏁?        //   锛?-norawinput / --notouch锛夛紝涓嶅啀鍗犵敤璁剧疆椤点€?    };

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    /// <summary>褰㈠彉鏈熼棿鏁寸墖鍐呭娣″叆鐢ㄧ殑鍥惧眰锛堟瘡甯х幇寤虹幇閿€锛屼笉缂撳瓨锛氱悊鐢辫 BeginFade锛夈€?/summary>
    private ID2D1Layer _fadeLayer;

    // ---- 闈欐€佸簳缂撳瓨锛?*鎯虫竻妤氫簡鍐嶅仛锛岀幇鍦ㄦ病鍋?* -----------------------------
    //
    // 瀹炴祴锛?-panelperf锛屽悓涓€浠藉唴瀹?200 绗旓級锛氬畬鏁村睍寮€鎬佹瘮涓嶆寕鐣岄潰璐电害 1.5锝?.6 ms/甯э紝
    // 鑰屽綊鍥犳樉绀洪槾褰卞彧鍗?0.37銆佸嚬妲?0.16銆佽壊鐗囩粏鑺?0.34銆佸浘鏍?0.32 鈥斺€?    // 鍓╀笅鐨勯挶鑺卞湪**姣忓抚鍑犲崄娆″皬缁樺埗璋冪敤鏈韩**锛堜笉鏄～鍏呴潰绉級銆?    // 鏁欑涔﹁В娉曟槸"鎶婇潤鎬佸簳鐢昏繘涓€寮犱綅鍥撅紝姣忓抚鍙创涓€娆?锛堝紩鎿庨噷鐨勬€ц兘闈㈡澘灏辨槸杩欎箞骞茬殑锛?    // 4.3 ms 鈫?0.05 ms锛夈€備絾**杩欐潯璺湁涓墠鎻?*锛氬埛缂撳瓨蹇呴』鍦?杩欎竴甯у紑濮嬬敾"涔嬪墠鍋氥€?    //
    // 鎴戣瘯杩囧湪 Render() 閲屽埛锛堜篃灏辨槸鍦ㄥ紩鎿?BeginDraw 閲岄潰鍒囨崲娓叉煋鐩爣鍐?BeginDraw锛夛紝
    // Direct2D 涓嶅厑璁歌繖涔堝仛 鈥斺€?鐜拌薄鏄?*鏁村潡闈㈡澘鐩存帴鍙樼┖鐧?*锛堝嚭鍥惧綋鍦虹湅鍑烘潵鐨勶紱
    // 鍑犱綍涓庡懡涓祴璇曠殑鑷鍏ㄧ豢锛屽洜涓洪偅閮ㄥ垎娌″潖锛夈€?    // 鎵€浠ュ畠闇€瑕佸紩鎿庡湪 `RenderFrame` 閲屻€丅eginDraw 涔嬪墠寮€涓€涓?鐣岄潰绂诲睆鍑嗗"鐨勯挬瀛?    // 锛堝紩鎿庤嚜宸辩殑 PrepareHud 灏辨寕鍦ㄩ偅鍎匡級銆?*鍏堣璐︼紝涓嶆敼**锛堣璁″垝 搂10.2锛夈€?
    public FullUi()
    {
        _expand = new Anim(0f);
        _peek = new Anim(1f);
        _rail = new Anim(0f);
        _dashFade = new Anim(1f);      // 1 = 鎹㈡尅鍔ㄧ敾宸茬粡缁撴潫锛堝钩鏃跺氨鏄柊妗ｇ殑鏍峰瓙锛?        _more = new Anim(0f);          // 銆屾洿澶氥€嶉潰鏉匡細0 = 鍏炽€? = 鍏ㄥ紑锛堝吋浣滈伄缃╅€忔槑搴︼級
        _moreH = new Anim(0f);         // 闈㈡澘楂樺害锛氭崲椤垫椂鍔ㄧ敾锛? = 杩樻病閲忚繃锛屾寜鐩爣绠楋級
        _tipFade = new Anim(0f);       // 鎮仠鎻愮ず鐨勬贰鍏?    }

    public string Name => "瀹屾暣鐣岄潰";
    public bool Visible => true;
    public bool IsAnimating
    {
        get
        {
            // 璐磋竟闅愯棌閭ｆ潯"绂诲紑涓€浼氬効鎵嶆敹"鏄潬**缁х画瑕佸抚**瀹炵幇鐨勶細
            // 娌℃湁甯у氨娌℃湁鏃舵満鍘绘敹锛堝紩鎿庡彧鍦ㄦ湁鑴忓尯鎴栫晫闈㈣瑕佸姩鏃舵墠娓叉煋锛夈€?            if (_peek.Running) return true;
            if (_rail.Running) return true;
            if (_dashFade.Running) return true;      // 鎹㈡尅閭ｄ竴涓嬭鎶婃贰鍏ユ贰鍑虹敾瀹?            if (_more.Running) return true;          // 銆屾洿澶氥€嶉潰鏉垮紑鍚堬紙閬僵娣″叆娣″嚭璺熺潃瀹冿級
            if (_moreH.Running) return true;         // 闈㈡澘鎹㈤〉鏃剁殑楂樺害鍔ㄧ敾
            // 鎸変綇娓呯┖銆佹垨鑰呭垰鎸夊畬閭ｄ竴涓嬬殑闂厜锛氶兘瑕佺户缁粰甯э紝鍚﹀垯杩涘害鏉′笉璧般€?            // 涔熸案杩滃埌涓嶄簡 0.8 绉掗偅涓偣锛?鎸変綇涓嶆斁"杩欐潯鍏ㄩ潬甯у湪鎺ㄨ繘锛夈€?            if (ActionHolding) return true;
            if (_host != null && _host.NowMs < _actionFlashUntil) return true;
            // 鑹插甫姝ｅ湪"绛夋偓鍋滄剰鍥?鏃朵篃寰楃粰甯э細涓嶇劧 120 姣鍒颁簡娌′汉鍘诲睍寮€瀹冿紝
            // 鎴栬€?220 姣鍒颁簡娌′汉鍘绘敹瀹冦€傚睍寮€/鏀惰捣涓€鏃﹀畬鎴愶紝杩欎袱涓潯浠剁珛鍒讳负鍋?鈫?绌洪棽鍥炲埌 0 甯с€?            if (BandVisible()
                && ((_railHover && _rail.Value < 0.5f) || (!_railHover && _rail.Value > 0f)))
                return true;
            if (_hideEnabled && _peekPendingCollapse && _peek.Value > 0f) return true;
            // 鎮仠鎻愮ず锛氳繕鍦ㄧ瓑 500ms 寤惰繜銆佹垨娣″叆娌¤蛋瀹岋紝閮借缁х画缁欏抚鈥斺€?            // 涓嶇粰甯х殑璇?鍒扮偣浜?娌′汉鍘荤敾瀹冦€傛樉绀哄畬涔嬪悗瀹冩槸闈欐€佺殑锛屼笉鍐嶇儳甯с€?            if (TipWanted && (!_tipShown || _tipFade.Running)) return true;
            // 瑙︽懜/绗旈暱鎸夛細杩樺湪璁℃椂锛堝埌鐐硅鐐逛寒锛夛紝鎴栨彁绀哄仠鐣欐湡娌″埌鐐癸紙鍒扮偣瑕佹敹锛夈€?            if (_tipHoldStart > double.NegativeInfinity && !_tipHoldFired) return true;
            if (_tipShown && _tipLingerUntil > 0 && _host.NowMs < _tipLingerUntil) return true;
            return _expand.Running;
        }
    }

    public void Attach(IUiHost host)
    {
        _host = host;
        _widgets = new Widgets(host);
        IconAtlas.Init(host.PathFactory);
        _expand.Bind(host);        // 鏃堕挓蹇呴』鎺ョ湡鐨勯偅涓紙瑙?Anim.Bind 鐨勬敞閲婏級
        _peek.Bind(host);
        _rail.Bind(host);
        _dashFade.Bind(host);
        _more.Bind(host);
        _moreH.Bind(host);
        _tipFade.Bind(host);
        _moreH.Jump(MoreTargetH());
        _lastTool = host.State.Tool;
        // **鍚姩鍗冲睍寮€**锛堢敤鎴?2026-09-27 瀹氾細"绗竴娆℃墦寮€浠ュ悗锛岄粯璁ゅ氨灞曞紑"锛夈€?        //
        // 鍘熸潵鏄?`Jump(0f)`鈥斺€斿惎鍔ㄦ槸涓€棰楃悆锛屽緱鍏堢偣涓€涓嬫墠灞曞紑銆傜敤鎴蜂笂鎵嬪悗鍙戠幇"姣忔寮€鏈?        // 閮借鍏堢偣閭ｄ竴涓?鏄浣欑殑锛氬伐鍏风殑鍏ュ彛鏈潵灏辫鎽嗗湪閭ｅ効銆?        // 鏀惰捣浠嶇劧鑳界敤锛堢偣涓€涓嬫潯鑷繁灏辨敹鎴愮悆锛夛紝鍙槸**涓嶅啀鏄粯璁ゆ€?*銆?        _expand.Jump(1f);
        _peek.Jump(1f);
        _rail.Jump(0f);
        _dashFade.Jump(1f);
        _lastPpt = host.State.PptMode;
        _lastPass = host.State.PassThrough;
        LoadPrefs();
        Layout(host.Screen, host.DpiScale);
        PushFloatingTheme();       // 娴眰锛堟搷浣滄潯/灏忛潰鏉?鏃嬭浆璇绘暟锛夎窡鐫€璧板悓涓€濂椾护鐗?    }

    /// <summary>
    /// 鎶?*鍚屼竴濂椾护鐗?*鎺ㄧ粰寮曟搸锛岃閫変腑鎿嶄綔鏉°€侀鑹?灞傜骇/瀵煎嚭闈㈡澘銆佹棆杞鏁颁篃鐢ㄨ繖濂?    /// 棰滆壊涓庢姇褰便€?    ///
    /// 涓轰粈涔堣鏈夎繖涓€姝ワ細閭ｄ簺涓滆タ鐢诲湪**寮曟搸**鐨勬诞灞備笂锛堝畠浠窡鐫€閫夊尯璧帮紝涓嶅睘浜庡伐鍏锋潯锛夛紝
    /// 鑰岄鑹?鎶曞奖鐨勬暟瀛?*鍙簲璇ユ湁涓€浠?*銆備互鍓嶅紩鎿庨噷鍙﹀啓浜嗕竴浠?`UiTheme.Default`锛?    /// 浜庢槸娴呰壊涓婚鏀瑰畬涔嬪悗锛屽伐鍏锋潯鏈変簡鍐风伆鎻忚竟鍜屼簲灞傛姇褰憋紝鎿嶄綔鏉¤繕鏄摑鐏版弿杈广€?    /// 涓€灞傛姇褰遍兘娌℃湁鈥斺€旂敤鎴蜂竴鐪煎氨鐪嬪嚭鏉?涓嶆槸涓€濂?銆?    ///
    /// 寮曟搸涓嶈В閲婂亸濂斤細璋佽鐢ㄦ繁鑹叉槸鐣岄潰鐨勪簨锛岀晫闈㈡崲涓婚灏辨帹涓€娆°€?    /// </summary>
    private void PushFloatingTheme()
    {
        var layers = _dark ? Tokens.ShadowDark : Tokens.ShadowLight;
        var ramp = new UiShadowLayer[layers.Length];
        for (int i = 0; i < layers.Length; i++)
            ramp[i] = new UiShadowLayer(layers[i].Inflate, layers[i].Dy, layers[i].Color);

        _host.SetFloatingTheme(new UiTheme(
            panel: _dark ? Tokens.PanelDark : Tokens.PanelLight,
            panelBorder: _dark ? Tokens.BorderDark : Tokens.BorderLight,
            text: _dark ? Tokens.InkDark : Tokens.InkLight,
            textMuted: _dark ? Tokens.InkMutedDark : Tokens.InkMutedLight,
            hover: _dark ? Tokens.HoverDark : Tokens.HoverLight,
            activeBg: Tokens.Accent,
            activeText: Tokens.AccentInk,
            cornerRadius: Tokens.FloatingCorner,
            // 搴曟部閭ｉ亾"鍗疯竟"鍙湁娴呰壊鐢細娣辫壊闈?鎻愪寒琛ㄩ潰"璇诲眰娆★紝缁欏畠涓€鏉℃殫杈瑰彧浼氭樉鑴?            edgeBottom: _dark ? new Color4(0f, 0f, 0f, 0f) : Tokens.EdgeLightBottom,
            shadow: ramp));
    }

    // ---- 鐣岄潰鑷繁鐨勫亸濂斤紙瀛樺摢鍎裤€佹€庝箞瀛樼敱寮曟搸璐熻矗锛岃繖閲屽彧绠￠敭鐨勫惈涔夛級------
    //
    // 鍥涙牱涓滆タ鑳借浣忥細娣辫壊涓婚銆佽创杈归殣钘忋€佹。浣嶃€侀拤浣忥紙鍙栨秷浜嗗摢鍑犳牸锛夈€?    // **闈㈡澘浣嶇疆涓嶈**鈥斺€旂敤鎴峰畾鐨?姣忔鍚姩閮藉湪鍥哄畾浣嶇疆"銆?    // 鍙啓"鍜岄粯璁や笉涓€鏍?鐨勯」锛氶粯璁ゆ。浣嶏紙瀹屾暣锛夈€侀粯璁ゅ叏閽変綇銆侀粯璁ゆ祬鑹蹭笉闅愯棌閮戒笉杩涢厤缃枃浠?    // 锛堜互鍚庨粯璁ゅ€兼敼浜嗭紝鑰侀厤缃笉浼氭妸鏂伴粯璁ら《鎺夛級銆?
    private void LoadPrefs()
    {
        _dark = _host.GetPref("dark") == "1";
        _hideEnabled = _host.GetPref("hide") == "1";
        // 鑹插甫甯稿紑锛?026-10-05锛夛細璁剧疆鏉′竴鐩存憡鐫€鈥斺€旇Е鎽稿睆涓婁笉鐢ㄥ幓纰伴偅鏉?10 鍍忕礌鐨勮壊绾裤€?        _railPinned = _host.GetPref("railPin") == "1";
        // 鎮仠鎻愮ず锛?*榛樿寮€**锛屽彧鍐?鍏宠繃鐨?閭ｄ竴浠斤紙鍜?dwellShape / pressure 鍚屼竴鏉¤鐭╋級銆?        // 寮曟搸鑷繁鐢荤殑娴眰锛堟搷浣滄潯 / PPT 鏉★級璇讳笉鍒扮晫闈㈠亸濂斤紝杩欓噷鎺ㄤ竴娆″紑鍏宠繃鍘汇€?        _tipEnabled = _host.GetPref("tooltip") != "0";
        _host.Commands.SetTooltips(_tipEnabled);

        string prof = _host.GetPref("profile");
        _profile = prof == "mini" ? Profile.Mini
                 : prof == "custom" ? Profile.Custom
                 : Profile.Full;

        for (int i = 1; i < _pinned.Length; i++) _pinned[i] = true;
        foreach (var s in (_host.GetPref("unpinned") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s, out int cell) && cell > 0 && cell < Cells.Length && CanUnpin(cell))
                _pinned[cell] = false;

        // 鐧芥澘搴曠汗鏄?*寮曟搸鐘舵€?*锛堢敾杩涘垎鍧楃紦瀛樼殑锛夛紝鐣岄潰杩欒竟鍙槸瀹冪殑"瀛樺偍鍣?锛?        // 鍚姩鏃舵妸涓婃鐨勬。浣嶆帹缁欏紩鎿庝竴娆°€傛暟鍊间笉鍚堟硶灏卞綋榛樿銆?        // 鈿?闂磋窛瑕?*鍚搁檮鍒版渶杩戠殑妗ｄ綅**锛氭。浣嶈〃 2026-09-27 鎹㈣繃锛堝師 24/40/64 鈫?鐜?20/30/40/64/96锛夛紝
        //   鑰侀厤缃噷瀛樼殑 24 涓嶅湪鏂拌〃閲岋紝涓嶅惛闄勭殑璇濇。浣嶇偣浼氭寚閿欎竴鏍笺€?        int pat = int.TryParse(_host.GetPref("boardPattern"), out int p) ? p : 0;
        float step = float.TryParse(_host.GetPref("boardStep"), out float stepPref) ? stepPref : 40f;
        _host.Commands.SetBoardPattern(pat, SnapPatternStep(step));
        float op = float.TryParse(_host.GetPref("boardOpacity"), out float opPref) ? opPref : BoardOpacityMax;
        _host.Commands.SetBoardOpacity(op);

        // 鍧愭爣绯荤綉鏍间篃鏄?*寮曟搸鐘舵€?*锛堝畠鍐冲畾鏂扮敾鐨勫潗鏍囩郴甯︿笉甯︽牸锛夛紝鍚屼竴濂楀仛娉曪細
        // 鍚姩鏃舵帹涓€娆°€傞粯璁?*鍏?*锛堣 Engine.CoordGridDefault 鐨勬敞閲婏級銆?        _host.Commands.SetCoordGridDefault(_host.GetPref("coordGrid") == "1");

        // 鍋滈】鎴愬瀷鍚屾牱鏄紩鎿庣姸鎬侊紝鑰屼笖**榛樿寮€**锛堢敤鎴?2026-09-23 瀹氾級銆?        // 鎵€浠ヨ繖閲岃鐨勬槸"鍏宠繃鐨?閭ｄ竴浠斤細鍙湁鏄庣‘鍐欑潃 "0" 鎵嶅叧锛屾病鏈夎繖涓€椤瑰氨鏄紑銆?        _host.Commands.SetDwellShape(_host.GetPref("dwellShape") != "0");

        // **榛樿妗ｄ綅 = 瀹屾暣**锛堢敤鎴?2026-10-05 瀹氾細"鍒氬紑濮嬬殑鏂拌蒋浠惰瀹屾暣鐨勫浘鏍囬偅绉?锛夈€?        // 浠ュ墠杩欓噷鍦ㄦ病鏈?profile"鍋忓ソ鏃舵寜閫昏緫灞忓鑷姩钀芥瀬绠€锛?1300 鈫?鏋佺畝锛夆€斺€斿湪 200% 缂╂斁鐨?        // 楂樺垎灞忎笂绗竴鐪煎氨灏戜竴鍗婂浘鏍囷紙"鍍忕己浜嗗浘"锛夈€傜幇鍦?*绗竴鐪兼案杩滄槸瀹屾暣妗?*锛?        // 鎯宠鏋佺畝鐨勮€佸笀鍦ㄣ€屾洿澶?鈫?妗ｄ綅銆嶅垏涓€娆★紙SavePrefs 浼氬啓 "profile"锛夛紝浠ュ悗姘歌繙鍚粬鐨勩€?        // 锛堜笂闈㈣ "profile" 閭ｄ竴娈靛凡缁忔妸 mini / custom 璇诲洖鏉ヤ簡锛涙病鏈夎繖涓€椤瑰氨鏄畬鏁淬€傦級
        // 璇惧爞宸ュ叿锛堣鏃?鐐瑰悕鐨勯璁句笌鍋忓ソ锛夊湪寮曟搸渚э紝鐣岄潰涓嶅啀瀛樺壇鏈€斺€旇 Classroom.cs銆?    }

    private void SavePrefs()
    {
        _host.SetPref("dark", _dark ? "1" : null);
        _host.SetPref("hide", _hideEnabled ? "1" : null);
        _host.SetPref("profile", _profile == Profile.Full ? null
                               : _profile == Profile.Mini ? "mini" : "custom");
        // 鐧芥澘搴曠汗锛氶粯璁わ紙鏃?/ 40锛変笉鍐欙紝鍙啓鏀硅繃鐨?        var st = _host.State;
        _host.SetPref("boardPattern", st.BoardPattern == 0 ? null : st.BoardPattern.ToString());
        _host.SetPref("boardStep", MathF.Abs(st.BoardPatternStep - 40f) < 0.5f
                                   ? null : st.BoardPatternStep.ToString("F0"));
        _host.SetPref("boardOpacity", st.BoardOpacity >= BoardOpacityMax - 0.005f
                                      ? null : st.BoardOpacity.ToString("F2"));
        // 鍧愭爣绯荤綉鏍硷細榛樿鍏筹紝鍙啓"寮€浜?杩欎竴绉嶆儏鍐点€?        _host.SetPref("coordGrid", st.CoordGridDefault ? "1" : null);
        // 鍋滈】鎴愬瀷锛?*榛樿寮€**锛屾墍浠ュ彧鍐?鍏充簡"杩欎竴绉嶆儏鍐碉紙鍐欐垚 "0"锛夈€?        _host.SetPref("dwellShape", st.DwellShapeOn ? null : "0");
        // 鍘嬫劅绮楃粏锛?*榛樿寮€**锛屽悓鏍峰彧鍐?鍏充簡"杩欎竴绉嶆儏鍐碉紙寮曟搸鍚姩鏃惰嚜宸辫瀹冿級銆?        _host.SetPref("pressure", st.PressureOn ? null : "0");
        // 锛堢簿缁嗙瑪杩?/ 瑙︽懜鎵嬪娍鐨勫亸濂藉啓鍏?2026-10-09 鏅氶殢寮€鍏充竴璧锋挙鈥斺€斾袱椤归兘甯稿紑锛屾病鏈夊彲鍐欑殑鐘舵€併€傦級
        // 澧ㄨ抗棰勬祴锛?*榛樿寮€**锛?026-10-14 娴嬭瘯鐗堣捣锛夛紝鍙啓"鍏宠繃"鐨勯偅涓€绉嶆儏鍐点€?        _host.SetPref("predict2", st.PredictTailOn ? null : "0");
        // 鎮仠鎻愮ず锛?*榛樿寮€**锛屽彧鍐?鍏充簡"杩欎竴绉嶆儏鍐点€?        _host.SetPref("tooltip", _tipEnabled ? null : "0");

        var off = new List<string>();
        for (int i = 1; i < _pinned.Length; i++) if (!_pinned[i]) off.Add(i.ToString());
        _host.SetPref("unpinned", off.Count == 0 ? null : string.Join(",", off));
        // 鑹插甫甯稿紑锛氶粯璁ゅ叧锛屽彧鍐?寮€浜?杩欎竴绉嶆儏鍐点€?        _host.SetPref("railPin", _railPinned ? "1" : null);
    }

    // ---- 甯冨眬 ---------------------------------------------------------------

    public RectF Layout(RectF screen, float dpiScale)
    {
        _screen = screen;
        // 宸ヤ綔鍖哄湪**姣忔甯冨眬**鏃堕噸鍙栦竴娆★紙灞忓箷/DPI 鍙樹簡浼氳蛋鍒拌繖閲岋紝Layout 鍥犳鏄ぉ鐒剁殑鍒锋柊鐐癸級銆?        // 璇讳笉鍒版垨缁欎簡绌虹煩褰㈠氨閫€鍥炲睆骞曗€斺€?*缁濅笉鐣欎笅涓€涓?0脳0 鐨?宸ヤ綔鍖?**锛?        // 閭ｄ細璁╅粯璁や綅缃妸闈㈡澘澶瑰埌灞忓箷宸︿笂瑙掋€?        var w = _host?.WorkArea ?? default;
        _work = (w.MaxX > w.MinX && w.MaxY > w.MinY) ? w : screen;
        return QueryBounds();
    }

    public RectF QueryBounds()
    {
        // 銆屾洿澶氥€嶉潰鏉挎墦寮€锛堝惈寮€鍚堝姩鐢伙級鏃讹細鎴戜滑鍗?*鏁村潡灞忓箷**鈥斺€旇繖鏄叏灞忔ā鎬佺殑鏍?        //锛堝懡涓祴璇曘€佽鍓€佹帴杈撳叆灏忕獥閮芥寜杩欎唤鐭╁舰璧帮紝鐞嗙敱瑙併€屾洿澶氥€嶉潰鏉块偅涓€鑺傦級銆?        // 鍏虫帀锛堝姩鐢讳篃璺戝畬锛夌珛鍒诲洖鍒颁富鏉￠偅涓€鏉★紝涓€甯ч兘涓嶅鍗犮€?        if (_moreOpen || _more.Running) return _screen;

        // 鍗犵敤鐭╁舰蹇呴』璺熺潃"瀹為檯鐢诲嚭鏉ョ殑涓滆タ"璧帮細璐磋竟闅愯棌鏃跺畠灏卞彧鍓╅湶澶撮偅涓€鏉★紝
        // 杈撳叆灏忕獥涔熻窡鐫€缂┾€斺€旇繖鏍锋寚閽堟壂杩囬湶澶存墠绠?纰板埌闈㈡澘"锛屽叾浣欎綅缃収鏃х┛閫?鐢荤嚎銆?        var u = UnionRect();
        // **绮楃粏棰勮浼氱敾鍒伴潰鏉垮闈?*锛堝甫瀛愰偅涓€渚х殑涓?涓嬫柟锛夛細鍙鑼冨洿涔熷緱璺熷嚭鍘伙紝
        // 鍚﹀垯寮曟搸浼氭妸瓒呭嚭 QueryBounds 鐨勯儴鍒嗚鎺夛紙寮曟搸鏄寜杩欎唤鐭╁舰鍋氳鍓殑锛夛紝
        // 琛ㄧ幇灏辨槸"棰勮娌＄敾鍑烘潵"銆傚畠鍙湪鎷栨粦鏉?鎮仠婊戞潯鏃跺嚭鐜帮紝骞虫椂杩欎唤鐭╁舰涓嶅彉銆?        if (SizePreviewVisible && _host != null) u = Union(u, SizePreviewRect(_host.State));
        var s = Shift();
        u = new RectF
        {
            MinX = u.MinX + s.X, MinY = u.MinY + s.Y,
            MaxX = u.MaxX + s.X, MaxY = u.MaxY + s.Y,
        };
        // 闇插ご蹇呴』鐣欏湪灞忓箷鍐咃紙缁濅笉鎶婄獥鍙ｆ尓鍑哄睆骞曟潵瀹炵幇闅愯棌鈥斺€旈偅鏍峰氨鍐嶄篃鏀朵笉鍒拌緭鍏ヤ簡锛?        u.MinX = Math.Max(u.MinX, _screen.MinX);
        u.MinY = Math.Max(u.MinY, _screen.MinY);
        u.MaxX = Math.Min(u.MaxX, _screen.MaxX);
        u.MaxY = Math.Min(u.MaxY, _screen.MaxY);

        // 鏀惰捣鎬佽创杈归殣钘忥細闇插嚭鏉ョ殑閭ｄ竴鏉?*鐢绘垚涓€鏉＄煭鎶婃墜**锛堣 DrawPeekTab锛夛紝
        // 鎵€浠?鐐瑰緱鍒扮殑鍦版柟"涔熷緱璺熺潃鏀剁煭鈥斺€斾笉鐒舵妸鎵嬩袱杈瑰悇鐣欎竴鎴湅涓嶈銆?        // 鍗翠細鍚冩帀鐐瑰嚮鐨勭┖鍦帮紙"鐪嬪緱瑙佺殑鍦版柟灏辩偣寰楀埌"锛岃繖鍙ヨ涓ゅご閮芥垚绔嬶級銆?        if (PeekTabShown && _expand.Value < 0.5f)
        {
            bool horiz = (u.MaxX - u.MinX) >= (u.MaxY - u.MinY);
            float len = MathF.Min(Tokens.PeekTab, horiz ? u.MaxX - u.MinX : u.MaxY - u.MinY);
            if (horiz)
            {
                float c = (u.MinX + u.MaxX) * 0.5f;
                u.MinX = c - len * 0.5f; u.MaxX = c + len * 0.5f;
            }
            else
            {
                float c = (u.MinY + u.MaxY) * 0.5f;
                u.MinY = c - len * 0.5f; u.MaxY = c + len * 0.5f;
            }
        }
        return u;
    }

    /// <summary>
    /// 璐磋竟闅愯棌鏄笉鏄凡缁忔矇鍒?璇ユ敼鐢ㄦ妸鎵?鐨勫湴姝ヤ簡銆?    ///
    /// 鍒ゆ嵁鏄?*闇插嚭鏉ュ鍘?*锛氶湶澶?= 8 锛?40 脳 peek锛?0 = 鐞冪殑鐩村緞 鈭?闇插ご锛夛紝
    /// 闇插嚭涓嶅埌 12 鍍忕礌灏辨崲銆傛崲鏃╀簡涓嶈鈥斺€旂悆閲岀殑鑹茬幆鍜岀瑪鍥炬爣浼氭彁鍓嶆秷澶憋紝
    /// 閭ｆ鏄敤鎴风偣杩囧悕鐨?璺?銆?    /// </summary>
    /// <summary>
    /// 璐磋竟闅愯棌"杩欎竴鍒荤湡鐨勫湪璧蜂綔鐢?鍚楋細寮€鍏冲紑鐫€锛岃€屼笖闈㈡澘**澶熷緱鍒板睆骞曞簳杈?*
    ///锛堝垽鎹拰 <see cref="Shift"/> 瀹屽叏鍚屼竴浠斤細**鍙湁搴曡竟鎵嶈棌**锛屽乏鍙?涓婅竟涓€寰嬩笉钘忥級銆?    ///
    /// 鈿?杩欎竴鏉?2026-10-01 琛ワ細浠ュ墠 `PeekTabShown` 鍙湅 `_peek` 鏀舵病鏀讹紝浜庢槸鎶婄悆鎷栧埌
    /// **宸?鍙宠竟缂?*锛堥偅閲?`Shift` 鎭掍负 0銆佹牴鏈棌涓嶅姩锛夋椂锛岀悆浼氳鐢绘垚閭ｆ潯"闇插ご鎶婃墜"
    /// 鈥斺€斾竴涓?*绾瑪鑹茬殑瀹炲績鍦?*锛岃€屼笖姘歌繙"鍚镐笉杩涘幓"锛堢敤鎴锋姤鐨勬鏄繖涓級銆?    /// </summary>
    private bool DockHideEngaged =>
        _hideEnabled && (_screen.MaxY - UnionRect().MaxY) <= Tokens.DockHideDistance;

    private bool PeekTabShown =>
        DockHideEngaged && Tokens.DockPeek + (Tokens.Ball - Tokens.DockPeek) * _peek.Value <= 12f;

    /// <summary>
    /// 鐜板湪鏄?灞曞紑鐨勬潯"杩樻槸"鐞?銆?.5 杩欐潯绾垮叏宸ョ▼鍏辩敤锛堣创杈归殣钘忚涓嶈鏀躲€佸唴瀹圭敾鍝竴濂楋級銆?    /// </summary>
    private bool Expanded => _expand.Value > 0.5f;

    /// <summary>
    /// 鐢诲埌鍗犵敤鐭╁舰**澶栭潰**鐨勯偅涓€鍦堬紙鎶曞奖锛夛紝鍛婅瘔寮曟搸鍒妸瀹冭鎺夈€?    /// 鍙奖鍝嶈鍓笌鑴忓尯锛?*涓嶅弬涓庡懡涓祴璇?*鈥斺€旀墍浠ラ潰鏉挎梺杈圭収鏍疯兘鐢荤嚎銆?    /// </summary>
    /// <summary>
    /// 鐣岄潰浼氱敾鍒板崰鐢ㄧ煩褰㈠闈㈢殑閭ｄ竴鍦?= 鎶曞奖鏈€杩滆儉鍒板灏戯紱
    /// **鎮仠鎻愮ず鏈夊彲鑳借鏇村ぇ**锛氬畠鐢诲湪闈㈡澘澶栭潰锛堜笂鏂规垨涓嬫柟锛夛紝
    /// 鎵€浠?鍋滃湪涓€涓湁鎻愮ず鐨勪笢瑗夸笂"鏈熼棿涓存椂鎶婁綑閲忔斁澶э紙`Tokens.TipPaintMargin`锛夈€?    /// 鍙奖鍝嶈鍓笌鑴忓尯锛屼笉鍙備笌鍛戒腑娴嬭瘯鈥斺€斾笉浼氬嚭鐜?鎻愮ず鏃佽竟鐐逛笉鍔?銆?    /// </summary>
    public float PaintMargin
        => TipWanted || _tipFade.Value > 0f ? Tokens.TipPaintMargin : Tokens.PaintMargin;

    private RectF BarRect()
    {
        var a = Anchor();
        return new RectF { MinX = a.X, MinY = a.Y, MaxX = a.X + Width(), MaxY = a.Y + Tokens.BarHeight };
    }

    /// <summary>涓婂甫锛氳创鍦ㄤ富鏉′笅闈?4 鍍忕礌銆傞珮搴﹂殢灞曞紑鍔ㄧ敾闀垮嚭鏉ワ紙涓嶅仛绗簩濂楀姩鐢伙級銆?/summary>
    private RectF BandRect()
    {
        var bar = BarRect();
        float h = BandHeightFull() * BandProgress();
        // 甯﹀瓙**鏈濆睆骞曚腑蹇冮偅涓€渚?*闀匡細
        //   路 闈㈡澘璐村簳锛堥粯璁や綅缃級鈫?甯﹀瓙鍦ㄤ笂闈細搴曡竟閿氬畾涓嶅姩锛屼綘鍒氱偣鐨勬寜閽綅缃篃涓嶅姩
        //     锛堣繖鏄亣闈㈡澘褰撳勾鐨勫仛娉曪紝浠ｇ爜娉ㄩ噴鍐欑潃"璐村簳鏃朵笅闈㈡病鏈夌┖闂?锛夛紱
        //   路 闈㈡澘鎷栧埌涓婂崐灞?鈫?甯﹀瓙鍦ㄤ笅闈紝鏈濆唴瀹归暱銆?        // 涓€寮€濮嬫垜鍐欐垚"姘歌繙鍦ㄤ富鏉′笅闈?锛岄偅鏄?*涓嶇粡鎰忕殑鍋忓樊**锛氶潰鏉垮簳杈归敋瀹?+ 甯﹀瓙鍦ㄤ笅闈?        // = 灞曞紑鏃舵寜閽甫鏁翠綋涓婅烦 38 鍍忕礌锛屾鏄璁℃枃绋块噷鐐硅繃鍚嶇殑"鍒囧伐鍏蜂細璺?銆?        if (BandAbove())
            return new RectF
            {
                MinX = bar.MinX, MinY = bar.MinY - BandGap - h,
                MaxX = bar.MaxX, MaxY = bar.MinY - BandGap,
            };
        return new RectF
        {
            MinX = bar.MinX, MinY = bar.MaxY + BandGap,
            MaxX = bar.MaxX, MaxY = bar.MaxY + BandGap + h,
        };
    }

    /// <summary>
    /// 鑹插甫鍜屼富鏉′箣闂?*涓嶇暀缂?*锛氬亣闈㈡澘閲屽畠浠槸**鍚屼竴鍧楅潰鏉?*锛堜竴涓渾瑙掑寘浣忎袱琛岋級锛?    /// 鎴戝師鏉ュ仛鎴愪袱寮犲垎寮€鐨勫崱鐗?锛?4 鍍忕礌缂濓紝鐪嬬潃灏辨槸"涓や釜鍙犺捣鏉ョ殑鑽父"锛?    /// 涓嶅瀹冩暣鍧楋紙鐢ㄦ埛 2026-09-16 鎻愮殑"鑹插甫涓嶅鍋囬潰鏉跨編瑙?锛屼富瑕佸氨鏄繖涓€鏉★級銆?    /// </summary>
    private const float BandGap = 0f;
    private float BandProgress() => _expand.Value;

    /// <summary>涓婂甫杩欎竴鍒荤殑楂樺害锛氬钩鏃?10 鍍忕礌鐨勮壊绾匡紝纰板埌浜嗛暱鎴?34 鍍忕礌鐨勮缃潯銆?/summary>
    private float BandHeightFull() =>
        Tokens.BandLine + (BandHeightLogical() - Tokens.BandLine) * _rail.Value;

    /// <summary>
    /// 杩欎竴鏍肩殑涓婂甫**瀹屽叏寮犲紑**鏃舵湁澶氶珮锛堥€昏緫鍍忕礌锛夈€?    ///
    /// 浠?2026-09-20 璧峰畠**鎸夋牸瀛愮畻**銆佷笉鏄竴涓父閲忥細鍥惧舰閭ｄ竴鏍兼槸**涓夎**
    /// 锛堢涓€琛?8 涓珮棰戝浘褰€佺浜岃 6 涓洸绾裤€佺涓夎 7 涓珛浣擄級锛屾墍浠ュ畠姣斿埆鐨勬牸瀛愰珮涓ゆ暣琛屻€?    /// 甯﹀瓙鏈濆睆骞曚腑蹇冮偅涓€渚ч暱锛堣 <see cref="BandRect"/>锛夛紝
    /// 鎵€浠ュ彉楂樻槸寰€涓?寰€涓嬮暱锛屼笉浼氭妸涓绘潯椤惰蛋銆?    ///
    /// 鈿?**"甯﹀瓙澶氶珮"鍙兘闂繖涓€澶?*锛?026-09-20 绗崄鍏壒鏀剁殑鍙ｅ瓙锛夛細浠ュ墠鏈夊洓澶勫悇鍐欎竴閬?    /// `Tokens.BandHeight`锛堝垽瀹氬尯 / 澶瑰彇 / 鎬婚珮 / 寮瑰嚭闈㈡澘瀹氫綅锛夛紝鑰岄偅涓€鏍间粠绗崄涓夋壒璧峰氨鏄琛岋紝
    /// 浜庢槸鍒ゅ畾鍖哄彧鐩栦綇**鏈€涓嬮潰閭ｄ竴琛?* 鈥斺€?鐢ㄦ埛 2026-09-20 鎶ョ殑姝ｆ槸杩欎釜锛?    /// "**榧犳爣绉诲姩鍒扮涓€琛岀殑浠讳綍鍥惧舰浣嶇疆锛岃壊甯︿細鏀惰捣鏉?*"锛堟寚閽堜竴鎸埌涓婇潰閭ｈ灏辫鍒ゆ垚
    /// "绂诲紑闈㈡澘"锛?20 姣鍚庡甫瀛愭敹鍥炲幓锛夈€傚洓澶勯兘鏀归棶杩欎釜鍑芥暟涔嬪悗锛?    /// 鍔犺/鍔犲浘褰㈤兘涓嶇敤鍐嶆潵琛ヤ竴閬嶃€?    /// </summary>
    private float BandHeightLogical()
        => _bandCell == ShapeCell && ShapeBandRows > 1
            ? Tokens.BandHeight
              + (ShapeBandRows - 1) * (Tokens.SegmentHeight + ShapeRowGap)
            : Tokens.BandHeight;

    /// <summary>
    /// 涓婂甫**瀹屽叏寮犲紑**鏃堕偅涓€鏁村潡鍗犲楂橈紙鍚畠涓庝富鏉′箣闂寸殑缂濓級鈥斺€?    /// 澶瑰彇灞忓箷銆佸垽瀹氬尯閮介棶瀹冿紙瑙?<see cref="BandHeightLogical"/> 閭ｆ潯娉ㄩ噴锛夈€?    /// </summary>
    private float BandBlockFull() => BandGap + BandHeightLogical();

    /// <summary>涓婂甫**杩欎竴鍒?*鍗犲楂橈紙璺熺潃"灞曞紑"閭ｄ竴妗ｅ姩鐢婚暱/鏀讹級銆?/summary>
    private float BandBlockNow() => BandBlockFull() * BandProgress();

    /// <summary>鑹茬嚎 / 璁剧疆鏉★細鏁板€煎澶т簡鎵嶆寜"璁剧疆鏉?閭ｅ鐢讳笌鍛戒腑锛堜腑闂存€佸綊鐭殑杩欒竟锛夈€?/summary>
    private bool RailOpen => _rail.Value >= 0.5f;

    /// <summary>涓婂甫杩欎竴鍒绘槸涓嶆槸鐪熺殑鐢诲嚭鏉ヤ簡锛堥暱鍑烘潵涔嬪墠涓嶅弬涓庡懡涓級銆?/summary>
    private bool BandVisible() => BandProgress() > 0.6f;

    /// <summary>
    /// 涓婂甫杩欎竴鍒绘槸涓嶆槸**寮犲紑鎴愪簡璁剧疆鏉?*锛氬紶寮€瀹屾垚銆佽€屼笖涓嶅湪绌块€忛噷銆?    ///
    /// 绌块€忛噷姘歌繙涓嶆垚绔嬶紙2026-10-02 鐢ㄦ埛鍙ｅ緞锛夛細鐐圭┛閫忓悗鑹插甫鍙槸**鏀跺洖骞虫椂閭ｆ潯
    /// 10 鍍忕礌鑹茬嚎**锛屼笉鏄秷澶扁€斺€旈潰鏉块珮搴︿笉鍙樸€佽创杈归殣钘忛湶鍑烘潵鐨勮繕鏄畠锛?    /// 浣嗙粷涓嶅儚鍒殑鏍煎瓙閭ｆ牱寮犵潃璁剧疆鏉★紙绌块€忔病鏈夎缃彲鏀撅級銆?    /// 缁樺埗閭ｅ嚑澶勪篃璺熺潃瀹冭璺細鎶樺彔鍔ㄧ敾杩涜鍒颁竴鍗婃椂鏃у唴瀹瑰氨宸茬粡鐐逛笉鍒颁簡銆?    /// </summary>
    private bool BandOpen() => BandVisible() && RailOpen && !_host.State.PassThrough;

    /// <summary>杩欎竴鏍煎睘浜庣鍑犵粍锛堝垎闅旂嚎鐢诲湪"缁勫彉浜?鐨勪袱涓浉閭绘牸涔嬮棿锛夈€?/summary>
    private static int GroupOf(int cell)
    {
        for (int i = 0; i < GroupEnds.Length; i++) if (cell <= GroupEnds[i]) return i;
        return GroupEnds.Length - 1;
    }

    /// <summary>甯﹀瓙瀹屽叏灞曞紑鏃剁殑瀹姐€?*鎸夎繖涓€鍒绘樉绀虹殑鏍煎瓙绠?*锛屾敼妗ｄ綅/鏍兼暟閮戒笉鐢ㄥ姩鏁板瓧銆?/summary>
    private float ExpandedWidth()
    {
        var vis = VisibleCells();
        float w = Tokens.BarPad * 2f;
        for (int k = 0; k < vis.Length; k++)
        {
            w += Tokens.Button;
            if (k + 1 < vis.Length)
                w += GroupOf(vis[k]) == GroupOf(vis[k + 1])
                    ? Tokens.GapInGroup : Tokens.GroupDivider;
        }
        return w;
    }

    private float Height() => Tokens.BarHeight;

    /// <summary>鏁翠釜闈㈡澘鐜板湪鏈夊楂橈紙涓绘潯 锛?涓婂甫锛夈€傞粯璁や綅缃寜瀹冪畻锛屾墍浠ユ槸寰€涓婇暱銆?/summary>
    private float TotalHeight() => Tokens.BarHeight + BandBlockNow();

    private float Width()
    {
        float e = _expand.Value;
        return Tokens.Ball + (ExpandedWidth() - Tokens.Ball) * e;
    }

    /// <summary>
    /// 宸︿笂瑙掋€傛病鎷栬繃灏辫创鍦?*涓嬭竟灞呬腑**锛堢杈?12锛夛紱鎷栬繃灏辩敤鎷栧埌鐨勪綅缃€?    /// 鎷栧姩鍙敼浣嶇疆銆佷笉鏀瑰昂瀵糕€斺€斾袱濂楀姩鐢讳簰鐩告媺鎵槸鏈€闅炬煡鐨勪竴绫绘€浉銆?    /// </summary>
    private Vector2 Anchor()
    {
        float w = Width();
        float h = TotalHeight();
        var a = RawAnchor();
        return Clamp(a, w, h);
    }

    /// <summary>
    /// 涓绘潯宸︿笂瑙掋€?*閿氱殑鏄?灞曞紑鍚庨偅鏉″甫瀛愮殑宸︾"**鈥斺€旂悆灏遍暱鍦ㄨ繖涓綅缃笂锛?    /// 鎵€浠ュ紑鍚堜箣闂?*鐞冧竴鍔ㄤ笉鍔?*锛堢偣瀹冨睍寮€銆佸啀鐐瑰畠鏀惰捣锛屼笉鐢ㄩ噸鏂扮瀯锛涜垂鑼ㄥ畾寰嬶級銆?    ///
    /// 娌℃嫋杩?鈫?閭ｆ潯**灞曞紑鍚庣殑甯﹀瓙璐寸潃宸ヤ綔鍖哄簳杈瑰眳涓?*锛坄x = 宸ヤ綔鍖轰腑蹇?- 灞曞紑瀹藉害/2`锛夛紝
    /// 浜庢槸鏀惰捣鏃剁悆鍋滃湪灞忓箷涓績**鍋忓乏**锛堝樊鍗婁釜甯﹀瓙瀹斤級锛岀偣寮€浠ュ悗鏁存潯甯﹀瓙姝ｅソ灞呬腑銆?    /// **杩欏氨鏄亣闈㈡澘褰撳勾鐨勫仛娉?*锛坄MockWindow.ApplyLayout`锛?    /// `_anchorLeft = wa.Left + (wa.Width - BarContentWidth)/2`锛?    /// 娉ㄩ噴鍐欑潃"閿氱殑鏄潰鏉胯嚜宸辩殑宸︿笅瑙掞紝鎵€浠ユ娊灞夊睍寮€鏃剁獥鍙ｅ線宸︿笂闀裤€侀潰鏉挎湰韬笉鍔?锛夈€?    ///
    /// **涓轰粈涔堢敤宸ヤ綔鍖鸿€屼笉鏄睆骞?*锛堢敤鎴?2026-09-27锛夛細浠栬鐨勬槸"璐寸潃浠诲姟鏍忎笂鏂逛竴鐐圭偣銆?    /// 涓よ€呬笉閲嶅彔"銆傚伐浣滃尯灏辨槸"灞忓箷鍑忔帀浠诲姟鏍?閭ｅ潡锛屾寜瀹冪畻鍑烘潵鐨勪綅缃ぉ鐒舵弧瓒宠繖涓€鏉★紱
    /// 鎸夊睆骞曞簳绠楃殑璇濓紝闈㈡澘浼?*鍘嬪湪浠诲姟鏍忎笂**锛堣鐩栧眰缃《锛屼换鍔℃爮鎸′笉浣忓畠锛岃€屼笖闈㈡澘鐭╁舰
    /// 鏄?鎴戜滑鐨勫湴鐩?銆侀偅涓€鍧楃殑浠诲姟鏍忎篃灏辩偣涓嶅埌浜嗭級銆?    /// 浠诲姟鏍忓湪搴曢儴/宸︿晶/椤堕儴锛屽伐浣滃尯閮戒細璺熺潃璁╁紑锛屼笉鐢ㄤ负瀹冨崟鍐欏垎鏀€?    ///
    /// 涓轰粈涔堜笉鏄?鐞冨眳涓€佸線涓よ竟闀?锛堜腑闂磋瘯杩囦竴鐗堬級锛氶偅鏍风悆浼氶殢鐫€瀹藉害婊戣蛋锛?    /// 鏀惰捣鏃惰閲嶆柊鎵惧畠銆備负浠€涔堜笉鏄?鐞冨眳涓€佸彧寰€鍙抽暱"锛氬睍寮€鍚庢暣鏉″甫瀛愪細鍋忓埌鍙宠竟鍘汇€?    ///
    /// 璐村埌鍙宠竟鎬庝箞鍔烇細鐞冩嫋鍒板彸杈圭紭鏃讹紝寰€鍙抽暱浼氶《鍑哄睆骞曪紝鐢?<see cref="Clamp"/>
    /// 鎶?*鏁存潯甯﹀瓙澶瑰洖灞忓箷鍐?*锛堢湅鐫€灏辨槸"璐翠綇鍙宠竟銆佸線宸﹂摵寮€"锛夈€?    ///
    /// 绾靛悜锛氫富鏉＄殑涓婅竟涓哄噯锛堝甫瀛愰暱鍦ㄤ笂闈紝璐村簳鏃舵寜閽笉浼氳烦锛夈€?    /// </summary>
    private Vector2 RawAnchor()
    {
        float top = _anchor?.Y ?? (_work.MaxY - Tokens.EdgeMargin - Tokens.BarHeight);
        // 娌℃嫋杩囷細鎸?*灞曞紑鍚庣殑瀹藉害**灞呬腑锛堜笉鏄綋鍓嶅搴︼級鈥斺€旇繖鏍锋敹璧锋€佸拰灞曞紑鎬?        // 宸﹀彸涓ょ閮戒笉浼氳烦锛屽彧鍦?鏁存潯甯﹀瓙"杩欎竴绾у榻愩€?        float x = _anchor?.X
                ?? _work.MinX + (_work.MaxX - _work.MinX - ExpandedWidth()) * 0.5f;
        return new Vector2(x, top);
    }

    /// <summary>
    /// 甯﹀瓙闀垮湪鍝竴渚э細**鏈濆睆骞曚腑蹇?*銆傞潰鏉垮湪涓嬪崐灞忓氨鏈濅笂闀匡紙璐村簳鏃朵笅闈㈡湰鏉ヤ篃娌＄┖闂达級锛?    /// 鎷栧埌涓婂崐灞忓氨鏈濅笅闀裤€傚垽鎹敤"涓绘潯涓績 vs 灞忓箷涓績"锛屼笉绠￠潰鏉挎€庝箞鎷栭兘鎴愮珛銆?    /// </summary>
    private bool BandAbove()
    {
        var a = RawAnchor();
        float center = a.Y + Tokens.BarHeight * 0.5f;
        return center >= (_screen.MinY + _screen.MaxY) * 0.5f;
    }

    /// <summary>澶瑰湪鍙鍖哄煙鍐咃紙涓€鏈熷氨澶瑰湪鍗曞潡灞忛噷锛岃法灞忔€庝箞鐢昏繕娌￠獙璇佽繃锛夈€?/summary>
    private Vector2 Clamp(Vector2 a, float w, float h)
    {
        // 澶瑰彇瑕佹寜**鏁翠釜闈㈡澘**锛堜富鏉?锛?甯﹀瓙锛夌畻锛歛 鏄富鏉″乏涓婅锛?        // 甯﹀瓙鍦ㄤ笂闈㈡椂鏁村潡鐨勯《杈瑰湪 a.Y 涔嬩笂銆傚甫瀛愰偅涓€鍧楃殑楂樺害**闂?`BandBlockNow`**銆?        // 涓嶅啓瀛楅潰閲忥細鍥惧舰閭ｄ竴鏍兼槸涓夎锛屽啓涓€琛岄珮浼氳鏁村潡琚す杩涘睆骞曢噷涓€鏍硷紙瑙侀偅澶勭殑娉ㄩ噴锛夈€?        float bandH = BandBlockNow();
        float top = BandAbove() ? a.Y - bandH : a.Y;
        float bottom = top + h;

        float x = Math.Clamp(a.X, _screen.MinX + Tokens.DockGap,
                                    _screen.MaxX - Tokens.DockGap - w);
        float y = a.Y;
        if (top < _screen.MinY + Tokens.DockGap) y += _screen.MinY + Tokens.DockGap - top;
        if (bottom > _screen.MaxY - Tokens.DockGap) y -= bottom - (_screen.MaxY - Tokens.DockGap);
        return new Vector2(x, y);
    }

    /// <summary>
    /// 绗?pos 涓?*鏄剧ず鍑烘潵**鐨勬牸瀛愮殑鐭╁舰锛堥€昏緫鍧愭爣锛宲os 鏄?褰撳墠妗ｄ綅閲岀殑搴忓彿"锛夈€?    /// 鍒囨。鎹㈢殑鏄繖浠藉垪琛紝缁樺埗涓庡懡涓兘璧板畠锛屾墍浠ヤ笉浼氬嚭鐜?鐢讳竴濂椼€佺偣鍙︿竴濂?銆?    /// </summary>
    private RectF CellRect(int pos)
    {
        var vis = VisibleCells();
        if (pos < 0 || pos >= vis.Length) return RectF.Empty;

        var a = Anchor();
        float x = a.X + Tokens.BarPad;
        for (int k = 0; k <= pos; k++)
        {
            if (k == pos)
                return new RectF { MinX = x, MinY = a.Y, MaxX = x + Tokens.Button, MaxY = a.Y + Height() };
            x += Tokens.Button;
            x += GroupOf(vis[k]) == GroupOf(vis[k + 1]) ? Tokens.GapInGroup : Tokens.GroupDivider;
        }
        return RectF.Empty;
    }

    /// <summary>鏌愪竴鏍硷紙鎸夊畬鏁存。鐨勪笅鏍囷級鍦ㄥ綋鍓嶄綅缃噷鐨勫簭鍙凤紱涓嶅湪杩欎竴妗ｅ氨鏄?-1銆?/summary>
    private int PosOf(int cell)
    {
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++) if (vis[k] == cell) return k;
        return -1;
    }

    /// <summary>鏀惰捣鎬侀偅涓悆锛堝拰甯﹀瓙绗竴鏍煎悓涓€涓綅缃紝鏉ュ洖閮戒笉鐢ㄩ噸鏂扮瀯鍑嗭級銆?/summary>
    private RectF BallRect()
    {
        var a = Anchor();
        return new RectF { MinX = a.X, MinY = a.Y, MaxX = a.X + Tokens.Ball, MaxY = a.Y + Tokens.Ball };
    }

    // ---- 涓婂甫锛堝綋鍓嶅伐鍏风殑璁剧疆鏉★級-------------------------------------------
    //
    // 涓€鏉¤鍒欒疮绌垮埌搴曪細**涓婂甫鏄剧ず"鐜板湪杩欎釜鎸夐挳鐨勮缃?**銆?    // 绗?鑽у厜绗?= 12 鑹茬墖 锛?绮楃粏锛涙縺鍏?= 鍏夌偣澶у皬锛涙鐨?= 鏁寸瑪/闈㈢Н 锛?澶у皬锛?    // 閫夋嫨 = 鐭╁舰/濂楃储锛涚櫧鏉?= 涓夌鏉胯壊锛涘浘褰?= 鏁村潡鍥惧舰闈㈡澘锛堣 ShapeRows锛夈€?    // 娌℃湁璁剧疆椤圭殑锛堝悗鎾?閲嶅仛/鏇村/鎴睆锛夊氨涓嶉暱涓婂甫鈥斺€斾笉鍋氫竴鎺掔┖鎸夐挳銆?
    private float BandCenterY() => (BandRect().MinY + BandRect().MaxY) * 0.5f;
    private float BandContentLeft() => BandRect().MinX + BarInset();

    private RectF SwatchRect(int i)
    {
        // **鑹茬墖鎸夊彲鐢ㄥ搴﹀钩鍒嗭紝閾烘弧鏁存潯**锛堢収鍋囬潰鏉匡細cw = (avail - gap*(n-1)) / n锛夈€?        // 鏃╁厛鎴戝啓鐨勬槸鍥哄畾 26 瀹?锛?6 缂濓紝缁撴灉鍙宠竟绌哄嚭涓€澶у潡锛岃窡鍋囬潰鏉夸竴姣斿氨闇查浜嗐€?        var band = BandRect();
        int n = SwatchCount;
        float gap = 4f;
        float avail = band.MaxX - band.MinX - BarInset() * 2f
                    - (BandHasSlider ? SliderTrackW + 14f : 0f)    // 缁欏彸绔殑绮楃粏婊戞潯璁╀綅
                    - (BandHasDashToggle ? DashToggleW + DashToggleGap : 0f)   // 缁欒櫄瀹炵嚎閭ｄ竴鏍艰浣?                    - ActionReserve;                                // 缁欐渶鍙崇鐨勫姩浣滄寜閽浣?        float w = (avail - gap * (n - 1)) / n;
        float h = SwatchHeight();
        float x = band.MinX + BarInset() + i * (w + gap);
        float y = BandCenterY() - h * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>涓婂甫宸﹀彸鐨勫唴杈硅窛锛堢収鍋囬潰鏉跨殑 inset = 16锛夈€?/summary>
    private static float BarInset() => 16f;

    /// <summary>鑹茬墖鐨勯珮锛氭渶澶?26锛屽甫瀛愮煯鐨勬椂鍊欐寜姣斾緥缂╋紙鍋囬潰鏉匡細min(26, band * 0.8)锛夈€?/summary>
    private float SwatchHeight() => MathF.Min(Tokens.Swatch, BandHeightFull() * 0.8f);

    /// <summary>铏氬疄绾块偅涓€鏍肩殑瀹斤紙閫昏緫鍍忕礌锛夛細46 = 閲岄潰杩樼敾寰椾笅涓€灏忔绾?+ 宸﹀彸鍚勭暀 8銆?/summary>
    private const float DashToggleW = 46f;
    /// <summary>瀹冨拰宸﹁竟鑹茬墖銆佸彸杈规粦鏉′箣闂寸殑缂濄€?/summary>
    private const float DashToggleGap = 8f;

    /// <summary>
    /// **铏氬疄绾垮垏鎹㈤偅涓€鏍?*锛氫粠婊戞潯宸﹁竟寰€宸﹁鍑?涓€鏍?+ 涓€閬撶紳"绠楀嚭鏉ャ€?    ///
    /// 涓轰粈涔堥敋鍦ㄦ粦鏉′笂銆佷笉閿氬湪鑹茬墖涓婏細婊戞潯鏄?*璐寸潃闈㈡澘鍙虫部**鎽嗙殑锛堜綅缃敱 BarInset 瀹氭锛夛紝
    /// 鎵€浠ヤ粠瀹冨線宸﹂噺鍑烘潵鐨勮繖涓€鏍?*涓嶄細闅忚壊鐗囦釜鏁板彉**鈥斺€旀瀬绠€妗?4 涓壊鐗囥€佸畬鏁存。 12 涓紝
    /// 瀹冮兘涓嶄細璺炽€傝壊鐗囬偅杈瑰啀璁╁紑鍚屼竴瀵瑰父閲忥紙瑙?<see cref="SwatchRect"/>锛夛紝涓よ竟鍚屾簮銆?    /// </summary>
    private RectF DashToggleRect()
    {
        var band = BandRect();
        float cy = (band.MinY + band.MaxY) * 0.5f;
        float right = SliderRect().MinX - DashToggleGap;
        return new RectF
        {
            MinX = right - DashToggleW, MinY = cy - Tokens.SegmentHeight * 0.5f,
            MaxX = right, MaxY = cy + Tokens.SegmentHeight * 0.5f,
        };
    }

    /// <summary>鎸囬拡鍦ㄤ笉鍦ㄩ偅涓€鏍间笂锛?*鐢讳笌鍛戒腑鍚屾簮**锛屽拰鍒殑鏍煎瓙涓€涓鐭╋級銆?/summary>
    private bool HitDashToggle(float x, float y)
        => BandHasDashToggle && RailOpen && DashToggleRect().Contains(x, y);

    /// <summary>绮楃粏婊戞潯鐨勮建閬撳锛堥€昏緫鍍忕礌锛夈€?/summary>
    private const float SliderTrackW = 132f;

    /// <summary>
    /// 绮楃粏婊戞潯锛?*闀垮湪璁剧疆鏉￠噷闈€侀潬鍙崇**锛堢敤鎴?2026-09-17锛?绗斻€佽崸鍏夌瑪鐨勮皟鑺傚ぇ灏?    /// 杩樻槸鏀惧埌鑹插甫涓婃潵"锛夈€?    ///
    /// 瀹冨師鏉ュ湪**闈㈡澘鏈€涓嬫部閭ｄ竴鏁存潯**涓婏紙鐓у亣闈㈡澘鐨?groove锛夈€傛斁鍦ㄩ偅鍎挎湁涓や釜闂锛?    ///   鈶?鍜屽伐鍏锋牸鐨勪笅鍗婃埅閲嶅彔锛屽懡涓『搴忓緱闈?婊戞潯鍏堝垽"杩欑鎶€宸у厹鐫€锛?    ///   鈶?瀹冪"璁剧疆"杩欎欢浜嬪お杩溾€斺€旇€佸笀鐪嬪埌鐨勬槸"闈㈡澘搴曚笅鏈夋潯妲?锛屼笉鐭ラ亾瀹冩槸骞蹭粈涔堢殑銆?    /// 鎸繘璁剧疆鏉′箣鍚庯紝鑹茬墖锛堥鑹诧級鍜屾粦鏉★紙绮楃粏锛夊湪鍚屼竴琛岄噷锛屽氨鏄?杩欎釜宸ュ叿鐨勮缃?銆?    ///
    /// 鍛戒腑鍖轰笂涓嬪悇缁?9 鍍忕礌浣欓噺锛堟墜鎸囨瘮榧犳爣闅剧瀯锛夈€?    /// </summary>
    private RectF SliderRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset() - ActionReserve;   // 鏈€鍙崇鐣欑粰鍔ㄤ綔鎸夐挳
        float cy = (band.MinY + band.MaxY) * 0.5f;
        return new RectF
        {
            MinX = right - SliderTrackW, MinY = cy - 9f,
            MaxX = right, MaxY = cy + 9f,
        };
    }

    private RectF SegmentRect(int i, int count)
    {
        var band = BandRect();
        // 鍜岃壊鐗囦竴鏍凤細鍙崇鏈夋粦鏉＄殑鏃跺€欒缁欐粦鏉¤浣嶏紝鍚﹀垯鍒嗘浼氬拰婊戞潯鍙犲湪涓€璧?        float total = band.MaxX - band.MinX - BarInset() * 2
                    - (BandHasSlider ? SliderTrackW + 14f : 0f)
                    - ActionReserve;
        // 鐧芥澘閭ｄ竴鏍硷細**鎸夊唴瀹瑰畾瀹界殑鍑犳**锛堣 BoardBand锛夛紝鏀句笉涓嬪氨鏁翠綋绛夋瘮缂┿€?        // 鍘熸潵杩欓噷鏄?5 娈靛浐瀹?70 瀹?锛岀粨鏋滄瀬绠€妗ｉ偅鏉″甫瀛愬彧鏈?315 瀹斤紝5脳70 鐩存帴鎶?        // 鑹插甫鎾戠垎锛堟粦鏉″帇鍦ㄩ粦鏉夸笂銆侀〉鐮佽鎸ゅ嚭闈㈡澘锛夆€斺€?026-09-27 閲嶆帓鏃朵慨鎺夈€?        if (_bandCell == 2)
        {
            var segs = BoardBand;
            // 鈿?涔熻缁欐渶鍙崇鐨勫姩浣滄寜閽紙鍏抽棴鐧芥澘閭ｄ釜 鉁曪級璁╀綅鈥斺€旀紡浜嗚繖涓€椤圭殑璇濓紝
            //    娈典細閾哄埌鎸夐挳搴曚笅鍘伙紙2026-09-27 鍔犲叧闂寜閽椂琛ョ殑锛夈€?            float avail = band.MaxX - band.MinX - BarInset() * 2
                        - (BandHasSlider ? SliderTrackW + 14f : 0f)
                        - ActionReserve;
            float need = BoardBandWidth(segs);
            float k = need <= 0f || avail >= need ? 1f : avail / need;   // 鏀句笉涓嬬瓑姣旂缉锛岀粷涓嶈秺鐣?            float sx = BandContentLeft();
            for (int j = 0; j < i && j < segs.Length; j++)
                sx += (BoardSegW(segs[j].Kind) + BoardSegGap(segs[j].Kind, segs[j + 1].Kind)) * k;
            float bw = (i < segs.Length ? BoardSegW(segs[i].Kind) : 0f) * k;
            float by = BandCenterY() - Tokens.SegmentHeight * 0.5f;
            return new RectF { MinX = sx, MinY = by, MaxX = sx + bw, MaxY = by + Tokens.SegmentHeight };
        }
        // **鍥惧舰閭ｄ竴鏍兼槸濂藉嚑琛?*锛?026-09-20 绗崄涓夋壒璧锋槸 3 琛岋紝瑙?`ShapeRows`锛夈€?        // 琛?鍒椾粠"杩欎竴娈垫帓绗嚑"鎺ㄥ嚭鏉ワ紙`ShapeSegmentRow` / `ShapeSegmentCol`锛夛紝
        // 姣忚鐨勬瀹藉悇鑷寜"鍙敤瀹藉害 梅 **鏈**娈垫暟"绠椻€斺€旀墍浠?4 娈电殑鏇茬嚎閭ｈ鍙嶈€屾洿瀹姐€?        // 鏁村潡**浠ュ甫瀛愪腑绾夸负涓績**涓婁笅鎽婂紑锛圢=2 鏃跺氨鏄€佸啓娉?鍚勮鍑哄崐涓闂磋窛"锛?        // 鎵€浠ラ偅涓€鐗堢殑浣嶇疆涓€涓儚绱犳病鍔級锛岃闂磋窛鍜屾闂磋窛鏄悓涓€涓?6銆?        if (_bandCell == ShapeCell)
        {
            int row = ShapeSegmentRow(i);
            int col = ShapeSegmentCol(i);
            int cols = ShapeRowCount(row);
            int rows = ShapeBandRows;
            float sw = Math.Min(120f, (total - (cols - 1) * 6f) / cols);
            float sx = BandContentLeft() + col * (sw + 6f);
            float rowsTop = BandCenterY()
                          - (rows * Tokens.SegmentHeight + (rows - 1) * ShapeRowGap) * 0.5f;
            float sy = rowsTop + row * (Tokens.SegmentHeight + ShapeRowGap);
            return new RectF { MinX = sx, MinY = sy, MaxX = sx + sw, MaxY = sy + Tokens.SegmentHeight };
        }
        float w = Math.Min(120f, (total - (count - 1) * 6f) / count);
        float x = BandContentLeft() + i * (w + 6f);
        float y = BandCenterY() - Tokens.SegmentHeight * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + Tokens.SegmentHeight };
    }

    /// <summary>鍥惧舰閭ｄ竴鏍间袱琛屼箣闂寸殑闂磋窛锛堜篃鏄涓庢涔嬮棿鐨?6锛岃 <see cref="SegmentRect"/>锛夈€?/summary>
    private const float ShapeRowGap = 6f;

    // ---- 鐧芥澘閭ｄ竴鏍肩殑鑹插甫锛?026-09-27 閲嶆帓锛?-------------------------------
    //
    // 杩欎竴鏍煎師鏉ュ彧鏈?`[涓婁竴灞廬[鐧絔[缁縘[榛慮[涓嬩竴灞廬` 锛?鍙宠竟涓€鍧?绗?N 灞?鏂囧瓧锛?    // 娈靛**鍐欐 70**銆傜敤鎴?2026-09-27 鎻愪簡涓や欢浜嬶紝澶栧姞椤哄甫鏌ュ嚭鏉ョ殑涓€浠惰€?bug锛?    //   鈶?"涓婁笅缈婚〉鍜岄〉鐮佸簲璇ユ尐鐫€锛? 鈥斺€?鍘熸潵鏄笂涓€灞忓湪鏈€宸︺€佷笅涓€灞忓湪绗?4 娈典箣鍚庯紝
    //      涓棿琚笁涓澘鑹插妶寮€锛岄〉鐮佸闆堕浂璐村湪"涓嬩竴灞?鍙宠竟
    //      鈫?鏀规垚 `[鈥筣 绗?N 灞?[鈥篯` **涓変欢鎸ㄧ潃**锛屾憜鍦ㄥ甫瀛愭渶宸︼紱
    //   鈶?"璋冩暣閫忔槑鐨勭殑婊戝潡浼间箮鍚庨潰璺熺潃涓€涓皬鍦嗙偣锛? 鈥斺€?閭ｄ釜鐐规槸**绗斿棰勮鐐?*锛?    //      鎷跨殑杩樻槸褰撳墠绗旇壊锛堟墍浠ュ湪鐧芥澘涓婃槸涓€棰楃孩鐐癸級锛岃繖涓€鏍煎畠娌℃湁鎰忎箟
    //      鈫?鐧芥澘涓嶇敾瀹冿紙瑙?DrawBandSlider锛夛紱
    //   鈶?鍐欐鐨?5脳70 鍦?*鏋佺畝妗?*锛堝甫瀛愬彧鏈?315 瀹斤級浼氭妸鑹插甫鎾戠垎锛氭粦鏉″帇鍦ㄩ粦鏉夸笂銆?    //      椤电爜琚尋鍑洪潰鏉匡紙鐜板満灏辨槸鍑哄浘 `reports/_b-mini.png`锛?    //      鈫?娈靛鏀规垚"鎸夊唴瀹瑰畾瀹?锛?鏀句笉涓嬪氨绛夋瘮缂?锛屽苟涓旀斁涓嶄笅鏃堕€€鍒扮揣鍑戝竷灞€銆?    //
    // 鍚屾椂鎶娿€屾洿澶氥€嶆娊灞夐噷鐨勩€岀櫧鏉垮簳绾?/ 搴曠汗闂磋窛銆嶆惉浜嗚繃鏉ワ紙鐢ㄦ埛锛?鎴戠幇鍦ㄨ鎶婃洿澶氶噷闈㈢殑
    // 鐧芥澘搴曠汗锛屽簳绾归棿璺濊繖浜涜缃Щ鍔ㄥ埌鐧芥澘鐨勫睍寮€鑹插甫閲岄潰"锛夈€傚畠浠湰鏉ュ氨鏄?*寰幆妗?*
    // 锛堢偣涓€涓嬫崲涓嬩竴妗ｏ級锛屾惉杩囨潵淇濇寔杩欎釜鎵嬫劅锛屽彟澶栬ˉ涓?涓€鍏卞嚑妗ｃ€佺幇鍦ㄧ鍑犳。"鐨勬。浣嶇偣銆?    // 鈿?鍙惉**鐣岄潰鍏ュ彛**锛氬紩鎿庢帴鍙?`SetBoardPattern` 鍜屽亸濂介敭 `boardPattern/boardStep`
    //   涓€涓病鍔紝鎵€浠?*瀛樻。鏍煎紡涓嶅彉**銆?
    /// <summary>鐧芥澘鑹插甫涓婄殑涓€娈垫槸骞蹭粈涔堢殑銆?*鍚嶅崟鍙湁杩欎竴浠?*锛氬竷灞€ / 缁樺埗 / 鍛戒腑 / 婵€娲婚兘闂畠銆?/summary>
    private enum BoardSegKind { PageUp, PageLabel, PageDown, Color, Pattern, Step }

    /// <summary>
    /// 鐧芥澘鑹插甫鐨勬琛ㄣ€俙Idx` 鍙湁 <see cref="BoardSegKind.Color"/> 鐢ㄥ緱涓?    /// 锛?/1/2 锛?鐧?缁?榛戯紝灏辨槸 `InkPalette.BoardPresets` 鐨勪笅鏍囷級銆?    ///
    /// 涓や唤甯冨眬鏄?*鍚屼竴濂楅€昏緫鐨勪袱绉嶈鍓?*锛屼笉鏄袱濂椾唬鐮侊細
    ///   路 瀹屾暣锛氱炕椤靛櫒 锛?鏉胯壊 锛?搴曠汗 锛?闂磋窛锛堝甫瀛愬瀹芥椂鐢ㄨ繖浠斤級锛?    ///   路 绱у噾锛氭澘鑹?锛?搴曠汗 锛?闂磋窛锛堟瀬绠€妗ｇ敤鈥斺€旂敤鎴?2026-09-27 瀹氱殑鍘熻瘽锛?    ///     "缈婚〉涓嶈锛屼繚鐣欓粦鐧借壊锛屽簳绾?闂磋窛"锛夈€?    /// </summary>
    private static readonly (BoardSegKind Kind, int Idx)[] BoardBandFull =
    {
        (BoardSegKind.PageUp, 0), (BoardSegKind.PageLabel, 0), (BoardSegKind.PageDown, 0),
        (BoardSegKind.Color, 0), (BoardSegKind.Color, 1), (BoardSegKind.Color, 2),
        (BoardSegKind.Pattern, 0), (BoardSegKind.Step, 0),
    };
    private static readonly (BoardSegKind Kind, int Idx)[] BoardBandCompact =
    {
        (BoardSegKind.Color, 0), (BoardSegKind.Color, 1), (BoardSegKind.Color, 2),
        (BoardSegKind.Pattern, 0), (BoardSegKind.Step, 0),
    };

    /// <summary>
    /// 姣忎竴娈靛瀹斤紙閫昏緫鍍忕礌锛夈€?*鎸夊唴瀹圭粰**銆佷笉骞虫憡锛氱炕椤甸偅涓や釜绠ご鍙涓€鏍肩獎鐨勶紝
    /// "绗?N 灞?鍜?搴曠汗 / 闂磋窛"瑕佹斁寰椾笅涓や笁涓瓧銆?    /// </summary>
    private static float BoardSegW(BoardSegKind k) => k switch
    {
        BoardSegKind.PageUp or BoardSegKind.PageDown => 34f,
        BoardSegKind.PageLabel => 58f,
        BoardSegKind.Color => 44f,        // 鐢ㄦ埛 2026-09-27锛?浣犳妸棰滆壊缂╃煭锛岄鑹蹭笉鐢ㄩ偅涔堝"
        _ => 58f,                          // 搴曠汗 / 闂磋窛
    };

    /// <summary>
    /// 娈典笌娈典箣闂寸殑缂濄€?*缈婚〉鍣ㄩ偅涓変欢璐寸揣锛?锛?*锛屽叾浣?6锛涚粍涓庣粍涔嬮棿璁╁紑 12
    /// 鈥斺€?绗?N 灞?澶瑰湪涓や釜绠ご涓棿涓嶈创绱х殑璇濓紝鐪嬬潃杩樻槸涓夊潡涓滆タ锛屼笉鏄?涓€涓炕椤靛櫒"銆?    /// </summary>
    private static float BoardSegGap(BoardSegKind a, BoardSegKind b) => (a, b) switch
    {
        (BoardSegKind.PageUp, BoardSegKind.PageLabel) => 2f,
        (BoardSegKind.PageLabel, BoardSegKind.PageDown) => 2f,
        (BoardSegKind.PageDown, BoardSegKind.Color) => 12f,
        (BoardSegKind.Color, BoardSegKind.Pattern) => 12f,
        _ => 6f,
    };

    /// <summary>杩欎竴浠芥琛ㄤ竴鍏辫鍗犲瀹斤紙鍚紳锛夈€?/summary>
    private static float BoardBandWidth((BoardSegKind Kind, int Idx)[] segs)
    {
        float w = 0;
        for (int i = 0; i < segs.Length; i++)
        {
            w += BoardSegW(segs[i].Kind);
            if (i + 1 < segs.Length) w += BoardSegGap(segs[i].Kind, segs[i + 1].Kind);
        }
        return w;
    }

    /// <summary>
    /// 瀹屾暣甯冨眬**鏈€灏戣鍗犺繖涔堝姣斾緥鐨勫彲鐢ㄥ搴?*鎵嶈繕鐣欑潃瀹冿紝鍚﹀垯閫€鍒扮揣鍑戝竷灞€銆?    ///
    /// 涓轰粈涔堜笉鍐欐垚"鏀句笉涓嬫墠閫€"锛堜篃灏辨槸姣斾緥 = 1.0锛夛細瀹屾暣妗?623 瀹芥椂鍙敤 445锛岃€屽畬鏁村竷灞€
    /// 瑕?420鈥斺€斿彧鏈?25 鐨勪綑閲忋€傝嚜瀹氫箟妗?*鍙鍙栨秷閽変綇涓€鏍?*锛堝皯 ~44 瀹斤級灏变細鎺夊埌 420 浠ヤ笅锛?    /// 閭ｄ竴鏍煎氨浼氱獊鐒朵粠"鏈夌炕椤靛櫒銆佹湁閫忔槑搴︽粦鏉?璺虫垚"閮芥病鏈?锛岀湅鐫€鍍忎釜 bug銆?    /// 璁╁畠鍦?85% 浠ヤ笂閮借繕璧板畬鏁村竷灞€锛堝樊鐨勯偅鐐归潬 `SegmentRect` 閲岀殑绛夋瘮缂╄ˉ涓婏級锛?    /// 灏卞钩婊戝浜嗭紱鍐嶇獎涓嬪幓鏂囧瓧璇ョ杈逛簡锛岄偅鏃跺€欓€€绱у噾鎵嶆槸瀵圭殑銆?    /// </summary>
    private const float BoardBandMinSqueeze = 0.85f;

    /// <summary>
    /// 鐧芥澘杩欎竴鏍肩敤**绱у噾**甯冨眬鍚楋紵
    ///
    /// 鍒ゆ嵁鏄?*鐪熺殑閲忎竴涓嬫斁涓嶆斁寰椾笅**锛堜笉鏄啓姝?鏋佺畝妗?锛夛細瀹屾暣甯冨眬瑕佸崰"娈垫€诲 锛?婊戞潯 锛?    /// 鏈€鍙崇閭ｄ釜鍔ㄤ綔鎸夐挳锛堝叧闂櫧鏉?鉁曪紝2026-09-27 鍔狅級锛?涓や晶鍐呰竟璺?锛屾斁涓嶄笅灏遍€€鍒扮揣鍑戝竷灞€銆?    /// 杩欐牱"鑷畾涔夋。閲屾妸鏍煎瓙鍙栨秷閽夊緱鍙墿鍑犳牸"涔熺収鏍蜂笉浼氭拺鐖嗏€斺€旈偅姝ｆ槸杩欎竴鏍煎師鏉ュ潖鎺夌殑鏍瑰洜銆?    /// 鈿?閲忕殑鏄?<see cref="ExpandedWidth"/>锛堝睍寮€鍚庣殑鏁存潯瀹斤級锛屼笉鏄綋鍓嶅姩鐢讳腑鐨勫锛?    ///    璺熺潃鍔ㄧ敾璧扮殑璇濓紝鑹茬嚎寮犲紑鍒颁竴鍗婂氨浼氫粠瀹屾暣甯冨眬璺虫垚绱у噾甯冨眬锛岀湅鐫€鍍忛棯浜嗕竴涓嬨€?    /// 鈿?婊戞潯閭ｄ竴椤硅繖閲?*鍐欐甯搁噺銆佷笉璧?`BandHasSlider`**锛氶偅涓睘鎬ц鍏堥棶 `BoardCompactBand`锛?    ///    鍙嶈繃鏉ラ棶瀹冨氨鏄嚜宸卞挰鑷繁锛堟棤闄愰€掑綊锛夈€傚畬鏁村竷灞€涓嬫粦鏉′竴瀹氬湪锛堣 `BandHasSlider`锛夛紝
    ///    鎵€浠ュ啓姝绘槸瀵圭殑銆傚姩浣滄寜閽偅杈瑰彲浠ョ洿鎺ョ敤 `ActionReserve`鈥斺€斿畠鍙湅 `_bandCell`锛屼笉鍥炵幆銆?    /// </summary>
    private bool BoardCompactBand =>
        ExpandedWidth() - BarInset() * 2f - (SliderTrackW + 14f) - ActionReserve
        < BoardBandWidth(BoardBandFull) * BoardBandMinSqueeze;

    private (BoardSegKind Kind, int Idx)[] BoardBand =>
        BoardCompactBand ? BoardBandCompact : BoardBandFull;

    /// <summary>绗?i 娈垫槸浠€涔堬紙瓒婄晫缁?鍙鏂囧瓧"閭ｇ鏃犲姩浣滅殑锛岀渷寰楄皟鐢ㄥ鍒ょ┖锛夈€?/summary>
    private (BoardSegKind Kind, int Idx) BoardSegAt(int i)
    {
        var segs = BoardBand;
        return i >= 0 && i < segs.Length ? segs[i] : (BoardSegKind.PageLabel, 0);
    }

    /// <summary>
    /// 鐧芥澘閭ｄ袱鏍硷紙搴曠汗 / 闂磋窛锛夊悇鏈夊嚑妗ｃ€佺幇鍦ㄧ鍑犳。锛堢粰 <see cref="DrawPips"/> 鐢級銆?    /// 鍜屽浘褰㈤偅涓€鏍肩殑 <see cref="PipsOf"/> 鍚屼竴涓剰鎬濓紝鍙槸杩欎袱鏍间笉鏄?宸ュ叿"銆佹槸鐧芥澘鐨勭姸鎬併€?    /// </summary>
    private static (int Count, int Current) BoardPips(BoardSegKind kind, in UiState st)
    {
        if (kind == BoardSegKind.Pattern)
            return (PatternNames.Length, Math.Clamp(st.BoardPattern, 0, PatternNames.Length - 1));
        // 鈿?鍏堟妸鍊兼妱鍒板眬閮ㄥ啀杩?lambda锛歚in` 鍙傛暟涓嶈琚?lambda 鎹曡幏锛圕S1628锛夈€?        float step = st.BoardPatternStep;
        int idx = Array.FindIndex(PatternSteps, v => MathF.Abs(v - step) < 0.5f);
        return (PatternSteps.Length, idx < 0 ? 0 : idx);
    }

    /// <summary>
    /// 鎶婄洏涓婅鍥炴潵鐨勯棿璺?*鍚搁檮鍒版渶杩戠殑妗ｄ綅**銆?    /// 妗ｄ綅琛ㄦ槸鎹㈣繃鐨勶紙鍘熸潵鏄?24/40/64锛?026-09-27 鏀规垚 20/30/40/64/96锛夛紝
    /// 鑰侀厤缃噷瀛樼殑 24 涓嶅湪鏂拌〃閲屸€斺€斾笉鍚搁檮鐨勮瘽妗ｄ綅鐐逛細鎸囧埌绗?1 妗ｄ笂锛岀瓑浜庢樉绀轰竴涓皫銆?    /// </summary>
    private static float SnapPatternStep(float v)
    {
        float best = PatternSteps[0], bestD = float.MaxValue;
        foreach (var s in PatternSteps)
        {
            float d = MathF.Abs(s - v);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    private bool BandHasSwatches => _bandCell is 3 or 4;
    /// <summary>
    /// 杩欎竴鏍肩殑璁剧疆鏉′笂瑕佷笉瑕侀偅涓?*铏氬疄绾垮垏鎹?*锛堝す鍦ㄨ壊鐗囧拰绮楃粏婊戞潯涔嬮棿锛?    /// 鐢ㄦ埛 2026-09-19 瀹氱殑浣嶇疆锛?鍔犲湪'璋冩寜閽ぇ灏?鍜?棰滆壊甯?涓棿"锛夈€?    ///
    /// **鍙湁瀹屾暣妗ｇ殑绗旀湁**锛屼袱鏉＄悊鐢憋細
    ///   路 鑽у厜绗?/ 婵€鍏夌瑪鐨勮建杩圭敾鎴愯櫄绾挎病鏈夋剰涔夛紙瑙?Engine.PenDash锛夛紝鍥惧舰鐨勭嚎鍨嬪巻鏉?    ///     鏄?閫変腑涔嬪悗鍦ㄦ搷浣滄潯闈㈡澘閲屾敼"鈥斺€旀墍浠ュ彧鏈夌 3 鏍硷紱
    ///   路 鏋佺畝妗ｉ偅鏉″甫瀛愬彧鏈?~333 瀹斤紝4 涓壊鐗?+ 绮楃粏婊戞潯宸茬粡鍚冩帀 155锛屽啀濉炰竴鏍?    ///     灏辨妸鑹茬墖鍘嬪埌 30 鍍忕礌浠ヤ笅鈥斺€旇€?鑹茬墖鎸ゅ埌鐪嬩笉娓?绛変簬杩欎釜鍏ュ彛娌℃湁
    ///     锛堣嚜妫€閲?姣忎釜鑹茬墖 鈮?0 瀹?閭ｆ潯灏辨槸杩欎釜鎰忔€濓級銆傛瀬绠€妗ｆ兂瑕佽櫄绾垮氨鍒囧洖瀹屾暣妗ｃ€?    /// </summary>
    private bool BandHasDashToggle => _bandCell == 3 && _profile != Profile.Mini;
    /// <summary>
    /// 鍝嚑鏍肩殑璁剧疆鏉″彸杈规湁婊戞潯銆?    ///
    /// 鐧芥澘閭ｄ竴鏍?*鐪嬪竷灞€**锛氬畠鎺у埗鐨勬槸"鏉块潰涓嶉€忔槑搴?锛堢敤鎴?2026-09-17锛?澧炲姞涓€涓€忔槑搴︾殑
    /// 鎷栧姩鍔熻兘锛岃繖鏍峰彲浠ユ壒娉ㄧ殑鏃跺€欓殣绾︾湅瑙佷笅闈㈢殑棰樼洰"锛夛紝浣嗙揣鍑戝竷灞€锛堟瀬绠€妗ｉ偅鏉″甫瀛愬彧鏈?    /// 315 瀹斤級閲?鏉胯壊 + 搴曠汗 + 闂磋窛"宸茬粡鍗犳弧锛屽涓嶄笅 146 瀹界殑婊戞潯鈥斺€旀墍浠ラ€€鍒扮揣鍑戝竷灞€鏃?    /// 婊戞潯涓€璧峰幓鎺夛紙鐢ㄦ埛 2026-09-27 閫夌殑鍙ｅ緞锛氭瀬绠€妗?缈婚〉涓嶈锛屼繚鐣欓粦鐧借壊锛屽簳绾?闂磋窛"锛夈€?    /// </summary>
    private bool BandHasSlider => _bandCell == 2 ? !BoardCompactBand : _bandCell is 3 or 4 or 5 or 6;
    /// <summary>
    /// 涓婂甫閲屾湁鍑犳銆傜櫧鏉块偅涓€鏍煎彇鑷?<see cref="BoardBand"/>锛堝畬鏁?8 娈?/ 绱у噾 5 娈碉紝
    /// **涓嶅啓姝绘暟瀛?*鈥斺€?026-09-27 閲嶆帓鏃跺氨鏄潬杩欐潯鎶?5 娈靛啓姝?鐨勬棫璐﹁繕鎺夌殑锛夈€?    /// 鎴浘閭ｄ竴鏍兼槸 3 娈碉細**[鐩存帴鎴彇][闅愯棌鐣岄潰][绮樿创鍥剧墖]**鈥斺€斿墠涓ゆ鐓?InkClass 鐨勪袱椤硅彍鍗曪紝
    /// 绗笁娈垫槸鐢ㄦ埛 2026-09-17 瑕佺殑锛?绮樿创鍔熻兘锛屽洜涓哄叾浠栧湴鏂逛娇鐢ㄥ鍒跺姛鑳藉彲浠ュ埌鍓创鏉匡紝
    /// 浣嗗鏋滄槸瑙︽懜灞忔垨鑰呮墜鍐欐澘鍙兘娌℃湁閿洏"锛堢瓑浜庢妸 `Ctrl+V` 鎼埌灞忓箷涓婏級銆?    ///
    /// 鍥惧舰閭ｄ竴鏍兼槸**鏁村潡鍥惧舰闈㈡澘**锛?8 娈点€佷笁琛岋級锛氭鏁板拰椤哄簭閮藉彇鑷?    /// <see cref="ShapeRows"/>锛屼笉鍐嶅啓姝绘暟瀛椻€斺€旀瀹芥槸鎸?鍙敤瀹藉害 梅 **鏈**娈垫暟"绠楃殑
    /// 锛堣 <see cref="SegmentRect"/>锛夛紝鍔犲浘褰笉鐢ㄥ啀鍔ㄥ竷灞€锛屼篃涓嶄細鎸ゆ垚涓€鍥?    /// 锛堟尋涓嶄笅浼氭湁 `--shapebandtest` 閲?姣忔 鈮?60 瀹?閭ｆ潯鍏滅潃锛夈€?    /// </summary>
    private int BandSegmentCount => _bandCell switch
    {
        2 => BoardBand.Length, 6 => 2, 7 => 2, 8 => ShapeBandSegments, 9 => 2, _ => 0,
    };

    /// <summary>
    /// **鍥惧舰閭ｄ竴鏍肩殑娈佃〃**锛堜竴琛屼竴涓暟缁勶紝浠庝笂鍒颁笅銆佷粠宸﹀埌鍙筹級鈥斺€旇繖鏄暣涓潰鏉块噷
    /// **鍞竴涓€浠?*"鐐瑰摢涓€娈靛垏浠€涔堝伐鍏?/ 鍝楂樹寒 / 鐢诲摢寮犲浘鏍?鐨勬潵婧?    /// 锛坄ShapeToolAt` / `ActivateSegment` / `IsSegmentActive` / `ShapeIconFor` 閮借瀹冿級銆?    /// 鍚勫啓涓€浠界殑璇濓紝鍔犱竴绉嶅浘褰㈠氨浼氭紡鎺変竴澶勨€斺€旇繖鏉℃暀璁粨搴撻噷鍚冭繃濂藉嚑娆°€?    ///
    /// **涓夎**锛?026-09-20 绗崄涓夋壒瀹氱殑锛岃 璁″垝-鍥惧舰宸ュ叿.md 搂35锛夛細
    ///   鈶?**楂橀鍥惧舰 8 涓?*锛堢敤鎴?2026-09-19 閭ｆ壒锛?*浣嶇疆涓€涓兘涓嶈鍔?*鈥斺€旇倢鑲夎蹇嗭級锛?    ///   鈶?**4 绉嶆洸绾?*锛?026-09-20 绗簲鎵癸細"鍥惧舰妗嗛噷鍔犵浜屽垪锛屾暀甯堝疄闄呬娇鐢ㄦ椂楂橀鐨勫彧瑕佷竴琛?锛夛紱
    ///   鈶?**6 涓珛浣撳浘褰?*锛堢浜旀壒鎼潵鍦嗘煴 / 鍦嗛敟锛岀鍗佷竴/鍗佷簩鎵瑰姞妫辨煴 / 妫遍敟 / 妫卞彴锛?    ///      绗崄涓夋壒鍔犲渾鍙帮級銆?    ///
    /// **涓轰粈涔堜粠"涓よ"鏀规垚"涓夎"**锛氱涓夎鍑戝埌 6 涓箣鍚庯紝绗簩琛屼細鏈?10 娈碉紝
    /// 姣忔瀹藉害鎺夊埌 **55 閫昏緫鍍忕礌**鈥斺€旇€?姣忔 鈮?60"鏄噺鍑烘潵鐨勯棬妲涳紙鍥炬爣 18 锛?鍥涘懆鐣欑櫧锛夛紝
    /// `--shapebandtest` 閲岄偅鏉℃柇瑷€褰撳満灏辩孩浜嗐€備笁琛屼箣鍚庢瘡琛?8 / 6 / 7 娈碉紝
    /// 姣忔 70 / 96 / 81锛岄兘杩樺湪闂ㄦ涔嬩笂锛涜€屼笖璇绘硶鏇撮『锛?*鏇茬嚎涓€琛屻€佹棆杞綋涓€琛屻€佹１鏌变綋涓€琛?*銆?    /// 浠ｄ环鏄紑甯︽椂闈㈡澘楂樹竴鏁磋锛堝彧鍦ㄦ寚閽堝仠鍦ㄩ潰鏉夸笂鏃讹紝鐢荤殑鏃跺€欎笉鍙楀奖鍝嶏級銆?    ///
    /// 绗竴琛岄偅鍏釜鐨?*鐩稿娆″簭涓€涓病鍔?*锛堢洿绾?< 鐭╁舰 < 妞渾 < 绠ご 鏄€佸笀鏈€鏃╃殑鑲岃倝璁板繂锛?    /// 2026-09-19 鍙堟帴浜嗗潗鏍囩郴锛夛紝杩欐潯瑙勭煩浠庣涓€鐗堣捣灏辨病鐮磋繃銆?    /// </summary>
    private static readonly Tool[][] ShapeRows =
    {
        // 鈶?楂橀鍥惧舰锛堢涓€琛岋紝浣嶇疆鍐荤粨锛?        new[]
        {
            Tool.Line, Tool.Rectangle, Tool.Ellipse, Tool.Circle,
            Tool.Triangle, Tool.Parallelogram, Tool.Arrow,
            Tool.Coordinate,     // 2026-09-19 绗簩鎵规帴鍦ㄦ湯灏撅紙鍙挙浜嗗畠鍚庨潰鐨?鏁拌酱"閭ｄ竴娈碉級
        },
        // 鈶?鏇茬嚎锛堢浜岃锛夛細**妞渾锛堝甫鐒︾偣锛? 鍙屾洸绾?/ 鎶涚墿绾?* / 姝ｅ鸡 / 浣欏鸡 / 娉㈡氮绾?/ 姝ｅ垏銆?        //
        // 鈿?**2026-09-22 鐢ㄦ埛閲嶆帓浜嗚繖涓€琛岀殑澶翠笁鏍?*锛堝師璇濓細"妞渾鎺掑湪绗簩琛岀殑鏈€鍓嶉潰锛?        //   鐒跺悗鏄€︹€﹀浘鏍囧墠涓変釜鏄き鍦嗭紝鍙屾洸绾匡紝鎶涚墿绾?锛夆€斺€斾篃灏辨槸鎶娿€屾き鍦嗭紙甯︾劍鐐癸級銆嶆斁鍒拌棣栥€?        //   鎶涚墿绾挎尓鍒扮涓夈€傜涓€琛岄偅鍏釜鐨?*浣嶇疆浠嶇劧鍐荤粨**锛堥偅鏉¤鐭╂病鐮达紝鐢ㄦ埛鍙浜嗙浜岃锛夈€?        new[]
        {
            Tool.ConicEllipse, Tool.Hyperbola, Tool.Parabola,
            Tool.Sine, Tool.Cosine, Tool.Wave, Tool.Tangent,
        },
        // 鈶?绔嬩綋锛堢涓夎锛夛細**鏃嬭浆浣?4 锛?妫辨煴浣?3**銆?        //   路 鏃嬭浆浣擄細涓€娆℃嫋鍑?*澶栨帴鐭╁舰**銆佷竴绗旂敾瀹屻€佽鎸′綇鐨勬槸"杩滀晶閭ｄ竴鍦?鍗婂湀"
        //     锛堝渾鏌?/ 鍦嗛敟 / 鍦嗗彴 / **鐞?*锛涚悆澶氱敾涓€涓丹閬撴き鍦嗭紝涓嶇劧瀹冨拰銆屽渾銆嶉暱寰椾竴鏍凤級锛?        //   路 妫辨煴浣擄細**涓ょ瑪**锛堝簳闈㈠鎺ユ 鈫?椤朵笂閭ｄ釜涓績锛夈€?/4/5/6 妗ｃ€?鐩?閭ｆ。鏈夎交寰惛闄勩€?        // 鈿?**鐞冪揣璺熺潃鍦嗗彴**锛堢敤鎴?2026-09-20锛?鎶婄悆鏀惧湪鏃嬭浆浣撳悗闈?锛夆€斺€旀棆杞綋閭ｄ竴缁?        //   "鏌?/ 閿?/ 鍙?/ 鐞?杩炵潃鎺掞紝鍚庨潰鎵嶆槸妫辨煴浣撻偅涓€缁勩€?        // 鈿?**闀挎柟浣?/ 鍥涢潰浣撲笉鍦ㄨ繖寮犺〃閲屼簡**锛?026-09-20 绗崄浜屾壒鎾ょ殑**鍏ュ彛**锛夛細
        // 鍥涙１鏌憋紙鐩达級灏辨槸闀挎柟浣撱€佷笁妫遍敟灏辨槸鍥涢潰浣擄紝琚笂闈㈤偅鍑犳瑕嗙洊浜嗐€?        // 鎾ょ殑鏄叆鍙ｏ紝涓嶆槸鐢绘硶鈥斺€擿Tool.Cuboid` / `StrokeKind.Cuboid` 涓庢暣鏉＄敾娉曢兘鐣欑潃锛?        // 鏃ф澘涔﹂噷鐨勯暱鏂逛綋鐓ф牱鑳芥墦寮€銆佽兘閫変腑銆佽兘鍒狅紙鍚?2026-09-19 鎾ゃ€屾暟杞淬€嶇殑瑙勭煩锛岃 11.2锛夈€?        new[]
        {
            Tool.Cylinder, Tool.Cone, Tool.ConeFrustum, Tool.Sphere,
            Tool.Prism, Tool.Pyramid, Tool.Frustum,
        },
    };

    /// <summary>鍥惧舰閭ｄ竴鏍煎湪涓婂甫閲岀殑涓嬫爣锛堜笁琛岄兘鍦ㄨ繖涓€涓牸瀛愰噷锛夈€?/summary>
    private const int ShapeCell = 8;

    /// <summary>
    /// 鍥惧舰閭ｄ竴鏍?*鏈€鍚庨偅涓€娈?*锛氥€屽浘搴撱€嶏紙鎴戠殑鍥惧舰锛?026-09-22 鍔狅紝鐢ㄦ埛瑕佺殑"鍥惧儚鏀惰棌"锛夈€?    ///
    /// 瀹?*涓嶆槸鍥惧舰宸ュ叿銆佹槸鍔ㄤ綔**锛堢偣寮€鍥惧簱闈㈡澘锛夛紝鎵€浠?*涓嶈繘 <see cref="ShapeRows"/>**锛?    /// 閭ｅ紶琛ㄦ槸"娈?鈫?宸ュ叿"鐨勫敮涓€鏉ユ簮锛屽涓€涓潪宸ュ叿杩涘幓锛宍ShapeToolAt`銆佹楂樹寒銆?    /// 浠ュ強 `--shapebandtest` 閭ｆ潯"姣忎釜鏈夊叆鍙ｇ殑鍥惧舰閮借鐪熸嫋涓€绗?鍏ㄩ兘浼氳窡鐫€閿欍€?    /// 瀹冨崟鐙崰**绗洓琛?*涓€娈碉紙鏀惧湪鎵€鏈夊浘褰箣鍚?= 涓嶅奖鍝嶄换浣曠幇鏈夋鍙凤紝鑲岃倝璁板繂涓嶅姩锛夈€?    ///
    /// 绾緥锛?*鍔犲浘褰㈠彧鍔?`ShapeRows`锛涘姞鍔ㄤ綔鎵嶅姩杩欓噷**銆?    /// </summary>
    private static int LibrarySegment => ShapeSegmentCount;

    /// <summary>銆屽浘搴撱€嶉偅涓€娈靛湪绗嚑琛岋紙宸ュ叿琛ㄤ箣鍚庣揣鎸ㄧ潃鐨勪竴琛岋級銆?/summary>
    private static int LibraryBandRow => ShapeRows.Length;

    /// <summary>
    /// 涓婂甫鍥惧舰閭ｄ竴鏍间竴鍏卞嚑琛岋細**宸ュ叿琛ㄩ偅鍑犺 锛?鏈€鍚庨偅娈点€屽浘搴撱€嶈嚜宸变竴琛?*銆?    /// 甯冨眬锛坄SegmentRect` 鐨勮楂?灞呬腑锛夊拰甯﹀瓙鎬婚珮锛坄BandHeightLogical`锛夐兘闂畠鈥斺€?    /// 灏戠畻涓€琛岀殑璇濓紝鏈€鍚庨偅涓€娈典細**鐢诲埌甯﹀瓙澶栭潰鍘?*锛堣惤鍦ㄤ富鏉′笂锛夛紝鐪嬬潃"鐐逛笉鍔?銆?    /// </summary>
    private static int ShapeBandRows => ShapeRows.Length + 1;

    /// <summary>鍥惧舰閭ｄ竴鏍间竴鍏卞嚑娈碉紙宸ュ叿娈?锛?鏈€鍚庨偅娈点€屽浘搴撱€嶏級銆傚懡涓笌缁樺埗閮界敤瀹冦€?/summary>
    private static int ShapeBandSegments => ShapeSegmentCount + 1;

    /// <summary>鍥惧舰閭ｄ竴鏍间竴鍏卞嚑娈碉紙鍚勮鍔犺捣鏉ワ級鈥斺€斿懡涓笌缁樺埗鐨勫惊鐜兘鐢ㄥ畠銆?/summary>
    private static int ShapeSegmentCount
    {
        get
        {
            int sum = 0;
            foreach (var row in ShapeRows) sum += row.Length;
            return sum;
        }
    }

    /// <summary>绗?`row` 琛屾湁鍑犳锛堝喅瀹氭湰琛岀殑娈靛锛夈€傘€屽浘搴撱€嶉偅涓€琛屽彧鏈変竴娈点€?/summary>
    private static int ShapeRowCount(int row)
    {
        if (row == LibraryBandRow) return 1;
        return row >= 0 && row < ShapeRows.Length ? ShapeRows[row].Length : 0;
    }

    /// <summary>
    /// 绗?`i` 娈碉紙鍦ㄥ浘褰㈤偅涓€鏍奸噷锛?*鍚勮浠庝笂鍒颁笅銆佽鍐呬粠宸﹀埌鍙虫媺骞崇紪鍙?*锛?    /// 鍦ㄧ鍑犺銆傝秺鐣屽洖绗?0 琛屻€?    /// </summary>
    private static int ShapeSegmentRow(int i)
    {
        if (i < 0) return 0;
        if (i >= ShapeSegmentCount) return LibraryBandRow;   // 鏈€鍚庨偅涓€娈点€屽浘搴撱€嶈嚜宸变竴琛?        int seen = 0;
        for (int r = 0; r < ShapeRows.Length; r++)
        {
            seen += ShapeRows[r].Length;
            if (i < seen) return r;
        }
        return ShapeRows.Length - 1;
    }

    /// <summary>绗?`i` 娈靛湪**瀹冮偅涓€琛岄噷**鎺掔鍑狅紙鐢?鍛戒腑鐨?x 鐢ㄥ畠锛夈€?/summary>
    private static int ShapeSegmentCol(int i)
    {
        int row = ShapeSegmentRow(i);
        int before = 0;
        for (int r = 0; r < row; r++) before += ShapeRows[r].Length;
        return i - before;
    }

    /// <summary>
    /// 绗?`i` 娈靛搴斿摢涓伐鍏枫€傝秺鐣屽洖绗竴娈碘€斺€斿畞鍙敾閿欎竴涓浘鏍囷紝涔熶笉璁╀笅鏍囪秺鐣屻€?    /// </summary>
    private static Tool ShapeToolAt(int i)
    {
        // 鏈€鍚庨偅涓€娈垫槸**鍔ㄤ綔**锛堝浘搴擄級锛屾病鏈夊伐鍏凤細璋冪敤澶勯兘鍏堝垽杩?`i >= ShapeSegmentCount`锛?        // 璧板埌杩欏効璇存槑鏈夎皝婕忓垽浜嗏€斺€斿洖绗竴涓伐鍏凤紝瀹佸彲鐢婚敊涓€涓浘鏍囦篃涓嶈秺鐣屻€?        if (i >= ShapeSegmentCount) return ShapeRows[0][0];
        int row = ShapeSegmentRow(i);
        int col = ShapeSegmentCol(i);
        var cells = ShapeRows[row];
        return col >= 0 && col < cells.Length ? cells[col] : ShapeRows[0][0];
    }

    /// <summary>
    /// 杩欎釜宸ュ叿**鍦ㄥ浘褰㈤潰鏉块噷鏈夋病鏈夊叆鍙?*锛堢偣涓绘潯閭ｄ竴鏍兼椂鐢ㄥ畠鍒ゆ柇"瑕佷笉瑕佸伔鍋锋崲宸ュ叿"锛夈€?    ///
    /// **鍚嶅瓧璇存槑鐧界偣**锛歚Engine` 閲屼篃鏈変竴涓?`IsShapeTool`锛屽垽鐨勬槸"**杩欎釜宸ュ叿鐢诲嚭鏉ョ殑鏄?    /// 鍥惧舰杩樻槸鑷敱绗旇抗**"锛堝惈鏁拌酱鈥斺€斿畠娌″叆鍙ｄ絾鐢绘硶杩樺湪锛岃€佸瓨妗ｉ噷閭ｄ簺鏁拌酱瑕佽兘閫変腑銆佽兘鍒狅級銆?    /// 涓や釜鍚嶅瓧鎾炵潃銆佽涔変笉鍚岋紝2026-09-20 椤烘墜鎶婅繖涓敼鎴?`HasShapeEntry`锛?    /// **鏈夊叆鍙?* 鈯?**鑳界敾**锛屽樊鐨勫氨鏄暟杞撮偅涓€涓€?    ///
    /// 鍒ゆ嵁鏄?*閭ｅ紶娈佃〃**锛坄ShapeRows`锛屼竴琛屼竴涓暟缁勶級鈥斺€斿畠鏄?鐐瑰摢涓€娈靛垏浠€涔?    /// 宸ュ叿 / 鍝楂樹寒 / 鐢诲摢寮犲浘鏍?鐨勫敮涓€鏉ユ簮锛屾墍浠ヨ繖閲屾壂涓€涓嬪氨澶燂紝
    /// 鍔犲浘褰㈠彧鏀硅〃锛堣 2026-09-19 閭ｄ竴杞細杩欓噷鏇剧粡鏄?涓婂甫涓冩 **鎴?* 鍧愭爣绯?鏁拌酱閭ｄ竴娈?锛?    /// 涓よ€呭悎娴佷箣鍚庢敹鏁涘洖涓€鍙ワ級銆?    /// </summary>
    private static bool HasShapeEntry(Tool t)
    {
        foreach (var row in ShapeRows)
            if (Array.IndexOf(row, t) >= 0) return true;
        return false;
    }

    /// <summary>杩欎釜宸ュ叿鐨勭矖缁嗚寖鍥淬€?*鐣岄潰绠¤寖鍥达紝寮曟搸绠￠挸浣?*銆?    ///
    /// **2026-10-09 鏅氳捣锛氳寖鍥?= 璇ュ伐鍏锋。琛ㄧ殑棣栧熬**锛堟粦鏉′袱绔濂芥槸鏈€缁?鏈€绮楅偅涓€妗ｂ€斺€?    /// 鎷栧姩鍚搁檮涔嬪悗锛岃寖鍥村鍒颁笉浜嗐€佷篃涓嶈鍒帮級銆備袱寮犳暟瀛楄瀵逛笂寮曟搸鐨勬。琛紝
    /// 闈?--paneltest 鍗′綇锛堣 WidthGrades锛夈€?    /// </summary>
    private (float Min, float Max) WidthRange(Tool tool) => tool switch
    {
        Tool.Highlighter => (8f, 32f),     // = HighlighterWidthGrades 棣栧熬
        Tool.Laser => (4f, 22f),           // = LaserWidthGrades 棣栧熬
        Tool.Eraser => (12f, 34f),         // 鏁寸瑪姗＄毊鐨?*钀界偣鍗婂緞** = EraserWidthGrades 棣栧熬
        Tool.PixelEraser => (46f, 150f),   // 闈㈢Н姗＄毊鐨?*妯竟**锛堥珮 = 妯竟 脳 1.618锛夛紳 棣栧熬
        _ => (1f, 40f),                    // 鐢荤瑪 = PenWidthGrades 棣栧熬
    };

    /// <summary>
    /// 鍚勫伐鍏风殑**绮楃粏妗ｄ綅**锛堥€昏緫鍍忕礌锛夈€?*2026-10-09 鏅氱敤鎴锋敼娉?*锛?    /// 婊戞潯涓?*涓嶅啀鐢荤偣**锛涙嫋鍔?*鍚搁檮鍒版渶杩戜竴妗?*锛堟嬁鍒版墜鐨勪竴瀹氭槸琛ㄩ噷鐨勬暟锛夈€?    ///
    /// 鍜屽紩鎿庨噷閭ｄ簲寮犺〃鍚勭暀涓€浠斤紝闈?--paneltest 閫愰」姣斿鍗′綇锛堝悓 WidthRange 鐨勮€佽鐭╋級锛?    /// 鐣岄潰鍙鍏紑濂戠害銆佹嬁涓嶅埌寮曟搸 internal 鏄晠鎰忕殑銆?    /// 鏂拌〃锛堝父鐢ㄦ鍔犲瘑涓€鍊?+ 绮楃灏侀《锛夛細
    ///   绗?1/1.5/2/2.5/3/3.5/4/5/6/7/8/9/10/20/40锛?5 妗ｏ級锛?    ///   鑽у厜绗?8/13/18/25/32锛涙縺鍏?4/6/8/11/14/18/22锛?    ///   鏁寸瑪姗＄毊 12/17/22/28/34锛堝浐瀹?5 妗ｏ級锛涢潰绉鐨?46/70/93/122/150锛堝浐瀹?5 妗ｏ級銆?    /// </summary>
    private static readonly float[] PenWidthGrades = { 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 20f, 40f };
    private static readonly float[] HighlighterWidthGrades = { 8f, 13f, 18f, 25f, 32f };
    private static readonly float[] LaserWidthGrades = { 4f, 6f, 8f, 11f, 14f, 18f, 22f };
    private static readonly float[] EraserWidthGrades = { 12f, 17f, 22f, 28f, 34f };
    private static readonly float[] PixelEraserWidthGrades = { 46f, 70f, 93f, 122f, 150f };

    private float[] WidthGrades(Tool tool) => tool switch
    {
        Tool.Highlighter => HighlighterWidthGrades,
        Tool.Laser => LaserWidthGrades,
        Tool.Eraser => EraserWidthGrades,
        Tool.PixelEraser => PixelEraserWidthGrades,
        _ => PenWidthGrades,
    };

    private float SliderT(in UiState st)
    {
        // 鐧芥澘閭ｄ竴鏍硷細婊戞潯 = **鏉块潰涓嶉€忔槑搴?*锛堜笉鏄矖缁嗭級銆?        // 宸﹁竟鐨勬暟瀛楄鍜屽紩鎿庨噷鐨?BoardOpacityMin/Max 涓€鑷达紙鐣岄潰鎷夸笉鍒板紩鎿庣殑 internal 甯搁噺锛?        // 闈?--paneltest 鎶婃粦鏉℃嫋鍒颁袱绔潵鍗′綇杩欎袱杈癸級銆?        if (_bandCell == 2)
            return Math.Clamp((st.BoardOpacity - BoardOpacityMin) / (BoardOpacityMax - BoardOpacityMin), 0f, 1f);
        var (min, max) = WidthRange(st.Tool);
        if (max <= min) return 0f;
        return Math.Clamp((st.Width - min) / (max - min), 0f, 1f);
    }

    /// <summary>鏉块潰閫忔槑搴︾殑鍙皟鑼冨洿锛堣鍜屽紩鎿庨噷閭ｄ袱涓父閲忎竴鑷达級銆?/summary>
    private const float BoardOpacityMin = 0.35f, BoardOpacityMax = 1f;

    /// <summary>鎷栨粦鏉★細鍙湪**鍊肩湡鐨勫彉浜?*鐨勬椂鍊欐彁浜わ紙姣忓抚鍑犲崄娆?SetWidth 浼氳繛甯﹀姩鐢讳笌閲嶇敾锛夈€?/summary>
    private void DragSlider(float x)
    {
        var (left, right) = SliderTrackRange();
        float t = right <= left ? 0f : Math.Clamp((x - left) / (right - left), 0f, 1f);

        if (_bandCell == 2)
        {
            float wantOpacity = BoardOpacityMin + (BoardOpacityMax - BoardOpacityMin) * t;
            if (MathF.Abs(wantOpacity - _host.State.BoardOpacity) < 0.005f) return;
            _host.Commands.SetBoardOpacity(wantOpacity);
            return;
        }
        var (min, max) = WidthRange(_host.State.Tool);
        float want = min + (max - min) * t;
        if (MathF.Abs(want - _host.State.Width) < 0.5f) return;   // 娌″彉灏变笉鎻愪氦
        _host.Commands.SetWidth(want);
    }

    /// <summary>婊戞潯鍙崇鐣欑粰"绮楃粏棰勮鐐?鐨勫搴︺€?/summary>
    private const float SliderPreviewW = 18f;

    /// <summary>
    /// 婊戞潯**杞ㄩ亾**鐨勫乏鍙崇锛堟粦閽渾蹇冭兘璧板埌鍝効锛夈€?    /// 鐢诲拰鎷栧叡鐢ㄨ繖涓€浠解€斺€斿悇绠椾竴浠界殑璇濓紝杩熸棭浼氬嚭鐜?鐪嬬潃鍦ㄤ腑闂淬€佺偣鍑烘潵鍋忎竴鎴?銆?    /// </summary>
    private (float Left, float Right) SliderTrackRange()
    {
        var box = SliderRect();
        float left = box.MinX + Tokens.SliderKnob * 0.5f;
        float right = box.MaxX - SliderPreviewW - Tokens.SliderKnob * 0.5f;
        return (left, right);
    }

    // ---- 涓婂甫鍙崇鐨?鍔ㄤ綔"鎸夐挳锛堢収鍋囬潰鏉匡級------------------------------------
    //
    // 鍋囬潰鏉块噷锛?*娓呯┖**鎸傚湪姗＄毊閭ｆ潯鐨勫彸绔紙鎿︿竴鐐?/ 鎿︿竴鍧?/ 鍏ㄦ摝鎺夛紝璇箟鏄竴璺殑锛夛紝
    // 鑰屼笖**鎸変綇 0.8 绉掓墠绠楁暟**鈥斺€旀竻绌烘湰韬彲鎾ら攢锛屼絾浠ｄ环澶э紝闃茶瑙︼紱
    // **鍏ㄩ€?*鎸傚湪閫夋嫨閭ｆ潯鐨勫彸绔紝鐐逛竴涓嬪氨鎵ц銆?    // 杩欎袱鏉℃垜浠竴鐩寸己锛堣鍒?10.1 鐨勭 1 鏉★級锛?026-09-17 鐢ㄦ埛鐐瑰悕瑕併€?    //
    // **鍏抽棴鐧芥澘**锛?026-09-27 鐢ㄦ埛瀹氱殑锛夋寕鐧芥澘閭ｆ潯鐨勫彸绔紝鐐逛竴涓嬪氨鎵ц銆?    // 瀹冩槸"鐧芥澘鏍肩偣绗竴涓嬩笉鍐嶅叧鏉?琛ヤ笂鐨勯偅涓€鍗婏細鍏虫澘浠庢**鍙湁杩欎竴涓墜鍔ㄥ叆鍙?*
    //锛堝彟涓€鏉℃槸"寮€绌块€忎細鑷姩鍏虫澘"鐨勪簰鏂ワ紝閭ｆ槸鑷姩鐨勩€佷笉鏄叆鍙ｏ級銆?    //
    // 浣嶇疆锛氫笂甯?*鏈€鍙崇**銆傜矖缁嗘粦鏉′篃鍦ㄥ彸绔紝鎵€浠ユ鐨偅鏉℃槸
    // `[鏁寸瑪鎿[闈㈢Н鎿 鈥︹€?[绮楃粏婊戞潯][娓呯┖]`鈥斺€斿姩浣滄案杩滆创鍦ㄦ渶澶栨部銆?    private enum BandAction { None = 0, Clear, SelectAll, CloseBoard, PasteImage }

    private BandAction ActionOf(int bandCell) => bandCell switch
    {
        2 => BandAction.CloseBoard,   // 鐧芥澘閭ｆ潯锛氭渶鍙崇涓€涓?鍏抽棴鐧芥澘"
        6 => BandAction.Clear,        // 娓呯┖ 鈮?"鍏ㄦ摝鎺?锛屽拰涓ょ姗＄毊鎺掍竴鏉?        7 => BandAction.SelectAll,    // 鍏ㄩ€?鈮?"鎶婅鎿嶄綔鐨勪笢瑗夸竴娆￠€変笂"锛屽綊閫夋嫨杩欐潯
        9 => BandAction.PasteImage,   // 鎴浘閭ｆ潯锛氭妸鍓创鏉块噷鐨勪笢瑗跨矘杩涙潵锛?.3.0 浠庡垎娈甸噷鎷嗗嚭鏉ワ級
        _ => BandAction.None,
    };
    private BandAction CurAction => ActionOf(_bandCell);

    private const float ActionW = 92f;
    /// <summary>
    /// **绐勭殑閭ｄ釜鍔ㄤ綔鎸夐挳**锛堢櫧鏉跨殑"鍏抽棴鐧芥澘"鐢ㄥ畠锛屽彧鏈夊浘鏍囥€佹病鏈夋枃瀛楋級銆?    ///
    /// 涓轰粈涔堝崟鐙粰涓€涓搴︼細鐧芥澘閭ｆ潯甯﹀瓙鏈潵灏辨弧鈥斺€? 娈碉紙缈婚〉鍣?锛?涓夎壊 锛?搴曠汗 锛?闂磋窛锛?    /// 瑕?420锛屽姞涓?146 鐨勯€忔槑搴︽粦鏉″彧鍓?25 鐨勪綑閲忥紱鍐嶅涓€涓?92 瀹界殑鍔ㄤ綔鎸夐挳锛?    /// 娈典細琚尋鍒?89% 缂╂斁锛坄SegmentRect` 閲岀殑绛夋瘮缂╋級銆?鍏抽棴"杩欎欢浜嬩竴涓?鉁?灏卞锛?    /// 46 瀹藉垰濂斤紙鍥炬爣 18 锛?宸﹀彸鍚?14锛夛紝娈靛氨涓嶇敤缂╅偅涔堢嫚銆?    /// </summary>
    private const float ActionWNarrow = 46f;

    /// <summary>
    /// 銆屾竻闄ゅ叏閮ㄥⅷ杩广€嶉偅涓€妗ｇ殑瀹斤紙**鍏釜瀛?* + 鍥炬爣锛?026-10-14 浠?娓呯┖"鏀瑰悕鍚庡姞鐨勶級銆?    /// 瀹戒竴鐐逛笉浼氭尋鍧忓甫瀛愶細娈靛拰婊戞潯鏄寜鍓╀綑瀹藉害绛夋瘮缂╃殑锛堣 SegmentRect锛夈€?    /// </summary>
    private const float ActionWClear = 128f;

    private static float ActionWidthOf(BandAction a) => a switch
    {
        BandAction.CloseBoard => ActionWNarrow,
        BandAction.Clear => ActionWClear,
        _ => ActionW,
    };

    private const double ClearHoldMs = 800;

    private RectF ActionRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset();
        float cy = (band.MinY + band.MaxY) * 0.5f;
        float w = ActionWidthOf(CurAction);
        return new RectF
        {
            MinX = right - w, MinY = cy - Tokens.SegmentHeight * 0.5f,
            MaxX = right, MaxY = cy + Tokens.SegmentHeight * 0.5f,
        };
    }

    /// <summary>鍔ㄤ綔鎸夐挳瑕佸崰鐨勬í鍚戠┖闂达紙鑹茬墖 / 鍒嗘 / 婊戞潯閮藉緱璁╀綅锛夈€?/summary>
    private float ActionReserve =>
        CurAction == BandAction.None ? 0f : ActionWidthOf(CurAction) + 10f;

    /// <summary>娓呯┖鐨勬寜浣忚鏃讹紙-inf = 娌″湪鎸夛級涓?鍒氭寜瀹岄棯涓€涓?鐨勬椂鍒汇€?/summary>
    private double _actionHoldFrom = double.NegativeInfinity;
    private double _actionFlashUntil = double.NegativeInfinity;
    private bool ActionHolding => !double.IsNegativeInfinity(_actionHoldFrom);

    /// <summary>
    /// **鍙屽嚮閫夋嫨鏍?= 鍏ㄩ€?*锛堢敤鎴?2026-09-27 瀹氾級銆傜収鐨勬槸 InkClass 閭ｄ釜"缁忓吀浜や簰"锛?    /// 瀹冮偅杈规槸 500ms 鍐呭弻鍑婚€夋嫨鍥炬爣 = 鍏ㄩ€夛紙`MW_FloatBar.cs` 鐨?500ms 鍙屽嚮妫€娴嬶級銆?    ///
    /// 鈿?**鍙屽嚮鑳借窡"鍗曞嚮鎹㈡。"鍏卞瓨锛屽叏闈?鍙湁涓ゆ。"**锛氳繛鐐逛袱涓嬫濂借浆涓ゆ牸銆佸洖鍒板師妗ｏ紝
    ///   鎵€浠ュ弻鍑荤殑鍑€鏁堟灉灏辨槸"鍏ㄩ€夛紝妯″紡娌″姩"锛堣 `Activate` 閲?case 7 閭ｄ竴鏀級銆?    ///   鍝ぉ閫夋嫨鏂瑰紡鍔犲埌**涓夋。**锛岃繖鏉″氨涓嶆垚绔嬩簡锛堣繛鐐逛袱涓嬩細鍑€閫€涓€鏍硷級鈥斺€?    ///   閭ｆ椂鍊欒涔堝幓鎺夊弻鍑诲叏閫夛紝瑕佷箞鍔犲垽瀹氬欢杩燂紙閭ｆ椂鍗曞嚮鎹㈡。浼氳繜閽濓紝涓嶅垝绠楋級銆?    /// </summary>
    private const double SelectDoubleClickMs = 500;
    private double _lastSelCellClickMs = double.NegativeInfinity;
    /// <summary>
    /// 鍙屽嚮鐨?*绗竴涓嬩箣鍓?*閭ｄ竴妗ｉ€夋嫨鏂瑰紡銆?    /// 绗簩涓嬬敤瀹冩妸妗ｄ綅鎶靛洖鍘伙紙绗竴涓嬭嫢宸茬粡鍒囪繃妗ｏ紝杩欓噷姝ｅソ鎶垫帀锛夆€斺€?    /// 杩欐牱"浠庡埆鐨勫伐鍏峰弻鍑昏繘鏉?鍜?宸茬粡鍦ㄩ€夋嫨宸ュ叿涓婂弻鍑?涓ょ鎯呭喌鐨勭粨鏋滀竴鑷达細
    /// **鍏ㄩ€?+ 妯″紡鍥炲埌鍙屽嚮鍓?*銆?    /// </summary>
    private SelectMode _selModeBeforeDoubleClick = SelectMode.Rect;

    /// <summary>鎸変綇杩涘害 0..1锛堢敾閭ｄ釜浠庡乏寰€鍙崇殑濉厖锛夈€?/summary>
    private float HoldProgress()
    {
        if (!ActionHolding || _host == null) return 0f;
        return (float)Math.Clamp((_host.NowMs - _actionHoldFrom) / ClearHoldMs, 0.0, 1.0);
    }

    /// <summary>
    /// 姣忓抚鎺ㄨ繘涓€娆?鎸変綇娓呯┖"銆?*蹇呴』鍦ㄦ瘡甯ц皟**锛堝拰闈㈡澘鍔ㄧ敾鍚屼竴濂楄妭濂忥級锛?    /// 鍙湪 PointerUp 閲屽垽鏃堕棿鐨勮瘽锛岃€佸笀鎸変綇涓嶆斁銆佹澗鎵嬪墠閭ｄ竴涓嬫案杩滀笉浼氳Е鍙戙€?    /// </summary>
    private void UpdateBandAction()
    {
        if (!ActionHolding || _host == null) return;
        if (_host.NowMs - _actionHoldFrom < ClearHoldMs) return;
        _actionHoldFrom = double.NegativeInfinity;
        _actionFlashUntil = _host.NowMs + 260;
        _host.Commands.Clear();
        Invalidate();
    }

    /// <summary>
    /// 鍔ㄤ綔鎸夐挳锛氬浘鏍?锛?鏂囧瓧锛涙竻绌洪偅鏉℃寜浣忔椂浠庡乏杈瑰線鍙冲～杩涘害銆?    ///
    /// 鐧芥澘閭ｆ潯鐨勩€屽叧闂櫧鏉裤€嶅彧鏈?*涓€涓?鉁?*锛堢獎鐗堬紝娌℃湁鏂囧瓧锛岃 <see cref="ActionWNarrow"/>锛夛紝
    /// 鑰屼笖**鏉垮凡缁忓叧鐫€鏃舵暣鍧楀帇鏆椼€佺偣浜嗕篃涓嶅姩**鈥斺€旈偅鏃跺€欏叧鏉挎病鏈夋剰涔?    ///锛堝帇鏆楃殑鍐欐硶娌跨敤"鍒伴《鏃朵笂涓€灞?閭ｄ竴濂楋細鍚岃壊闄嶅埌 30% 涓嶉€忔槑搴︼級銆?    /// </summary>
    private void DrawBandAction(ID2D1DeviceContext ctx)
    {
        var a = CurAction;
        if (a == BandAction.None || !RailOpen) return;
        var r = ActionRect();
        bool clear = a == BandAction.Clear;
        bool closeBoard = a == BandAction.CloseBoard;
        bool paste = a == BandAction.PasteImage;
        bool dim = closeBoard && (_host == null || !_host.State.Board);
        var ink = dim ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f) : InkCol;
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
        var tint = clear ? new Color4(0.90f, 0.35f, 0.25f, 1f) : Tokens.Accent;

        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(tint.R, tint.G, tint.B, (dim ? 0.03f : 0.08f))));
        float t = HoldProgress();
        if (t > 0.002f)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY,
                                     r.MinX + (r.MaxX - r.MinX) * t, r.MaxY), 7f, 7f),
                Brush(ctx, new Color4(tint.R, tint.G, tint.B, 0.45f)));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        // 绐勭増锛堝叧闂櫧鏉匡級锛氬浘鏍囧眳涓崰婊★紝娌℃湁鏂囧瓧閭ｄ竴鏍?        if (closeBoard)
        {
            IconAtlas.DrawCentered(ctx, "dismiss", r, 16f, Brush(ctx, ink));
            if (_host != null && _host.NowMs < _actionFlashUntil)
                ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
            return;
        }

        var iconBox = new RectF { MinX = r.MinX + 6f, MinY = r.MinY, MaxX = r.MinX + 28f, MaxY = r.MaxY };
        IconAtlas.DrawCentered(ctx, paste ? "image" : clear ? "broom" : "selectAll", iconBox, 16f, Brush(ctx, ink));
        var labelBox = new RectF { MinX = r.MinX + 28f, MinY = r.MinY, MaxX = r.MaxX - 6f, MaxY = r.MaxY };
        _widgets.Text(ctx, paste ? "绮樿创鍥剧墖" : clear ? "娓呴櫎鍏ㄩ儴澧ㄨ抗" : "鍏ㄩ€?,
                      labelBox, 12.5f, Brush(ctx, InkCol));

        if (_host != null && _host.NowMs < _actionFlashUntil)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
    }

    // ---- 绮楃粏棰勮锛氭暟瀛?锛?**鐪熷疄澶у皬** --------------------------------------
    //
    // 鐢ㄦ埛 2026-09-17 鐨勪笁鍙ヨ瘽锛?    //   鈶?绗斿拰婵€鍏夌瑪璋冭妭澶у皬鐨勬椂鍊欏彲涓嶅彲浠ユ樉绀虹瑪鍙?锛堣涓€涓鏁帮級锛?    //   鈶?璋冭妭澶у皬鐨勬椂鍊欓偅涓偣鍜屽疄闄呭ぇ灏忔槸涓嶆槸搴旇涓€鏍峰ぇ锛屼絾鏄お澶т簡瑁呬笉涓嬶紝
    //      鎴戝張涓嶅笇鏈涙敼鍔ㄧ晫闈㈡€庝箞鍔?锛?    //   鈶?闈㈢Н姗＄毊鎿︾殑澶у皬璋冩暣鐨勬椂鍊欎篃鑳介瑙堝疄闄呭ぇ灏?銆?    //
    // 鐭涚浘鍦ㄤ簬锛氭粦鏉″彸绔偅涓瑙堢偣鏈€澶氬彧鑳藉崰甯﹀瓙楂橈紙34 鍍忕礌锛夛紝鑰岀瑪鑳藉埌 40銆?    // 鏁寸瑪姗＄毊鐨勫湀鑳藉埌 96銆侀潰绉鐨殑鍧楅珮鑳藉埌 259 鈥斺€?瑁呬笉涓嬨€?    //
    // 瑙ｆ硶锛?*棰勮鐢诲埌甯﹀瓙澶栭潰鍘?*锛堝甫瀛愬湪涓婂氨寰€涓婄敾锛屽湪涓嬪氨寰€涓嬬敾锛夛紝
    // 骞朵笖鎶婂畠绠楄繘 `QueryBounds()`鈥斺€斿紩鎿庢槸鎸夐偅浠界煩褰㈣鍓晫闈㈢敾鐨勶紝
    // 涓嶈繖涔堝仛鐢诲嚭鍘荤殑閮ㄥ垎浼氳瑁佹帀銆傞潰鏉垮昂瀵搞€佸竷灞€涓€涓儚绱犻兘涓嶇敤鏀癸紝
    // 鑰屼笖瀹冨彧鍦?鎷栨粦鏉?/ 鎸囬拡鍋滃湪婊戞潯涓?鏃跺嚭鐜帮紝骞虫椂閭ｄ唤鐭╁舰涓€鐐归兘涓嶅彉銆?
    // **姗＄毊涓嶇粰棰勮**锛?026-10-09 鏅氱敤鎴凤細"姗＄毊鎿︽粦鍔ㄧ殑鏃跺€欎細鏄剧ず'鍑犵櫨涔樺嚑鐧?鐨勯瑙堬紝
    // 閭ｄ釜鏄笉鏄篃涓嶉渶瑕?锛夆€斺€旀鐨殑鍧楀ご鐢辫惤鐐硅嚜宸辫璇濓紝婊戞潯涓婁笉鍐嶅脊灏哄姘旀场锛?    // 绗?/ 鑽у厜绗?/ 婵€鍏?/ 鐧芥澘锛堟澘鑹茬墖锛夌収鏃с€?    private bool SizePreviewVisible =>
        BandOpen() && BandHasSlider
        && (_bandCell == 2 || _host == null
            || _host.State.Tool is not (Tool.Eraser or Tool.PixelEraser))
        && (_sliderDragging || _hover == 300);

    /// <summary>鐪熷疄钀界偣鐨勫楂橈紙閫昏緫鍍忕礌锛夈€?*鍜屽紩鎿庨噷閭ｅ钀界偣鍚屾簮**锛堥兘璇?st.Width锛夈€?/summary>
    private (float W, float H) TrueSize(in UiState st)
    {
        // 鐧芥澘閭ｄ竴鏍硷細棰勮鐨勪笉鏄?钀界偣"锛岃€屾槸**鏉胯壊鍦ㄨ繖涓笉閫忔槑搴︿笅鐨勬牱瀛?*锛堜竴鍧楀皬鑹茬墖锛?        if (_bandCell == 2) return (56f, 34f);
        return st.Tool switch
        {
            Tool.Eraser => (st.Width * 2f, st.Width * 2f),      // 寮曟搸缁欑殑鏄崐寰?鈫?鐢诲嚭鏉ユ槸鐩村緞
            Tool.PixelEraser => (st.Width, st.Width * 1.618f),  // 榛勯噾姣斾緥鐭╁舰锛堝拰钀界偣涓€妯′竴鏍凤級
            _ => (st.Width, st.Width),                          // 绗?/ 鑽у厜绗?/ 婵€鍏?= 绾垮
        };
    }

    private RectF SizePreviewRect(in UiState st)
    {
        var (w, h) = TrueSize(st);
        var band = BandRect();
        var (left, right) = SliderTrackRange();
        float kx = left + (right - left) * SliderT(st);
        float bw = MathF.Max(w, 58f) + 20f;      // 褰㈢姸 + 宸﹀彸鐣欑櫧
        float bh = h + 36f;                      // 涓婏細褰㈢姸锛涗笅锛氭暟瀛?        float top = BandAbove() ? band.MinY - 8f - bh : band.MaxY + 8f;
        // 妯悜澶瑰湪灞忓箷閲岋細婊戦挳鍦ㄦ渶宸?鏈€鍙虫椂锛屾皵娉′細琚帹鍒板睆骞曞鐪嬩笉瑙?        float x0 = kx - bw * 0.5f, x1 = kx + bw * 0.5f;
        float lo = _screen.MinX + 4f, hi = _screen.MaxX - 4f;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }
        return new RectF
        {
            MinX = x0, MinY = top,
            MaxX = x1, MaxY = top + bh,
        };
    }

    /// <summary>璇绘暟銆傚叏鏄?*閫昏緫鍍忕礌**锛屽拰寮曟搸閲岄偅涓€煎悓婧愶紙闈㈢Н姗＄毊鎶?瀹矫楅珮"锛夈€?/summary>
    private string SizeLabel(in UiState st)
    {
        if (_bandCell == 2) return $"{st.BoardOpacity * 100f:F0}%";
        return st.Tool switch
        {
            Tool.PixelEraser => $"{st.Width:F0}脳{st.Width * 1.618f:F0}",
            Tool.Eraser => $"{st.Width * 2f:F0}",
            _ => $"{st.Width:F0}",
        };
    }

    private void DrawSizePreview(ID2D1DeviceContext ctx, in UiState st)
    {
        if (!SizePreviewVisible) return;

        var box = SizePreviewRect(st);
        var (w, h) = TrueSize(st);
        var bg = new RoundedRectangle(new Vortice.RawRectF(box.MinX, box.MinY, box.MaxX, box.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(bg, Brush(ctx, _dark ? Tokens.PanelDark : Tokens.PanelLight));
        ctx.DrawRoundedRectangle(bg, Brush(ctx, BorderCol), 1f);

        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = box.MinY + 6f + h * 0.5f;
        DrawTrueSizeShape(ctx, st, new Vector2(cx, cy), w, h);

        var textBox = new RectF
        {
            MinX = box.MinX + 4f, MinY = box.MinY + 6f + h,
            MaxX = box.MaxX - 4f, MaxY = box.MaxY - 3f,
        };
        _widgets.Text(ctx, SizeLabel(st), textBox, 12f, Brush(ctx, InkCol));
    }

    /// <summary>鎸夌湡瀹炲昂瀵哥敾涓€涓?杩欎竴绗?杩欎竴鍧楁湁澶氬ぇ"鐨勬牱瀛愩€傞厤鑹插拰钀界偣鍏夋爣涓€鑷淬€?/summary>
    private void DrawTrueSizeShape(ID2D1DeviceContext ctx, in UiState st, Vector2 c, float w, float h)
    {
        var white = new Color4(1f, 1f, 1f, 0.85f);
        var dark = new Color4(0.22f, 0.28f, 0.38f, 0.9f);

        // 鐧芥澘閭ｄ竴鏍硷細涓€鍧?*鏉胯壊鍦ㄨ繖涓笉閫忔槑搴︿笅鐨勮壊鐗?*鈥斺€旀墍瑙佸嵆鎵€寰?        if (_bandCell == 2)
        {
            var r = new Vortice.RawRectF(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
            var b = st.BoardColor;
            ctx.FillRoundedRectangle(new RoundedRectangle(r, 6f, 6f),
                                     Brush(ctx, new Color4(b.R, b.G, b.B, b.A * st.BoardOpacity)));
            ctx.DrawRoundedRectangle(new RoundedRectangle(r, 6f, 6f), Brush(ctx, dark), 1.4f);
            return;
        }
        switch (st.Tool)
        {
            case Tool.Eraser:                       // 鍜屾暣绗旀鐨殑钀界偣鍦嗙幆鍚屼竴濂?                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f),
                                Brush(ctx, new Color4(0.35f, 0.55f, 0.95f, 0.10f)));
                ctx.DrawEllipse(new Ellipse(c, w * 0.5f + 0.9f, w * 0.5f + 0.9f), Brush(ctx, white), 1.8f);
                ctx.DrawEllipse(new Ellipse(c, w * 0.5f, w * 0.5f), Brush(ctx, dark), 1.8f);
                break;

            case Tool.PixelEraser:                  // 鍜岄潰绉鐨殑钀界偣鐭╁舰鍚屼竴濂?            {
                var r = new Vortice.RawRectF(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
                ctx.FillRectangle(r, Brush(ctx, new Color4(0.35f, 0.55f, 0.95f, 0.22f)));
                ctx.DrawRectangle(r, Brush(ctx, dark), 1.8f);
                break;
            }

            case Tool.Laser:                        // 鍜屾縺鍏夎惤鐐瑰悓涓€涓孩鐐?                ctx.FillEllipse(new Ellipse(c, w * 0.5f + 1f, w * 0.5f + 1f), Brush(ctx, white));
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f),
                                Brush(ctx, new Color4(1f, 0.16f, 0.16f, 0.95f)));
                break;

            case Tool.Highlighter:
            {
                // **鍦嗙洏**锛屽拰灞忓箷涓婄殑钀界偣涓€妯′竴鏍凤紙`Overlay.DrawHighlighterDisc`锛?                // 鍗婂緞 = 鍗婁釜绗斿锛屽～鍏呯敤鑽у厜绗旀湰鑹诧紝澶栭潰濂楃櫧锛忔繁涓ゅ眰鎻忚竟锛夈€?                //
                // 鎴戠涓€鐗堝湪杩欏効鐢讳簡"涓€鏍归偅涔堢矖鐨勭煭鏉?锛岀悊鐢辨槸"鑽у厜绗旂敾鍑烘潵灏辨槸涓€澶ф潯"鈥斺€?                // 閭ｆ槸**鎯冲綋鐒?*锛氱偣涓€涓嬬殑钀界偣灏辨槸涓渾鐩橈紝鐭潯鍙嶈€屽拰鐪熷疄钀界偣瀵逛笉涓婏紝
                // 鐢ㄦ埛涓€鐪煎氨鐪嬪嚭鏉ヤ簡锛?鑽у厜绗旂殑棰勮鎬庝箞涓嶆槸鍦嗙殑"锛夈€?                // 瑙勭煩鍙湁涓€鏉★細**棰勮 = 閭ｄ釜宸ュ叿鍦ㄥ睆骞曚笂鐨勮惤鐐?*銆?                float r = w * 0.5f;
                ctx.FillEllipse(new Ellipse(c, r, r),
                                Brush(ctx, new Color4(st.HighlighterColor.R, st.HighlighterColor.G,
                                                      st.HighlighterColor.B, 0.22f)));
                ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f), Brush(ctx, white), 1.5f);
                ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f),
                                Brush(ctx, new Color4(0.22f, 0.28f, 0.38f, 0.55f)), 1.5f);
                break;
            }

            default:                                // 绗旓細涓€涓疄蹇冨渾 = 杩欎竴绗旀湁澶氱矖锛堢敤**褰撳墠绗旇壊**锛?                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f), Brush(ctx, st.Color));
                break;
        }
    }

    private int HitSwatch(float x, float y)
    {
        if (!BandHasSwatches) return -1;
        if (!RailOpen) return -1;      // 杩樻槸涓€鏉¤壊绾挎椂涓嶅悆鐐瑰嚮锛堟寚閽堜竴闈犺繎瀹冨氨浼氬紶寮€锛?        for (int i = 0; i < SwatchCount; i++)
            if (SwatchRect(i).Contains(x, y)) return i;
        return -1;
    }

    private int HitSegment(float x, float y)
    {
        int n = BandSegmentCount;
        if (!RailOpen) return -1;
        for (int i = 0; i < n; i++)
        {
            // 鐧芥澘閭ｄ竴鏍间腑闂撮偅鍧椼€岀 N 灞忋€嶆槸**鍙**鐨勶紙涓嶆槸鎸夐挳锛夛細瀹冨す鍦ㄤ袱涓炕椤电澶?            // 涓棿銆佸拰瀹冧滑璐村湪涓€璧风粍鎴愪竴涓?缈婚〉鍣?锛岀偣瀹冧粈涔堥兘涓嶈鍙戠敓銆?            if (_bandCell == 2 && BoardSegAt(i).Kind == BoardSegKind.PageLabel) continue;
            if (SegmentRect(i, n).Contains(x, y)) return i;
        }
        return -1;
    }

    /// <summary>鑹茬嚎 / 璁剧疆鏉¤涓嶈寮犲紑銆傚垽鎹細鎸囬拡纰板埌瀹冦€佹垨鑰呮鍦ㄦ嫋婊戞潯銆?/summary>
    private void UpdateRail()
    {
        if (!BandVisible())
        {
            _rail.To(0f, 0);
            _railHover = false;
            return;
        }

        // ---- 鍐欏瓧涓庤壊甯︾殑杈圭晫锛?026-10-09 浜岃疆瀹氱锛岀敤鎴峰師璇濓細
        //      "鍙鏀惰捣鏉ワ紝瀹冨氨涓€瀹氭槸鑹插甫锛屼笉鑳芥槸閭ｇ鏍煎瓙鐘剁殑锛涘彧鏈夐€€鍑烘潵浠ュ悗鎵嶈兘鍙樻垚鍗＄墖"锛?---
        // 涓夋潯纭鍒欙細
        //   鈶?钀界瑪閭ｄ竴鍒伙細**娌℃敹骞插噣鐨勪竴寰嬫敹鍥炶壊绾?*鈥斺€斿寘鎷?姝ｅ湪寮犲紑鐨勫崐璺?
        //      锛堝彧鍐荤粨涓嶆媺鍥炵殑璇濓紝寮犲紑鍔ㄧ敾浼氳嚜宸辫蛋瀹岋紝鍐欑潃鍐欑潃灏卞彉鎴愬崱鐗囦簡鈥斺€旇繖姝ｆ槸
        //       鐢ㄦ埛浜岃疆閲岃鐨?鍋跺皵杩樻槸浼氶€€鍖栨垚鍗＄墖"锛夈€傚凡缁忓畬鍏ㄥ睍寮€锛堚墺0.999锛夌殑淇濇寔涓嶅姩銆?        //   鈶?鏁寸瑪鏈熼棿锛氬喕缁擄紙涓嶅紶涓嶆敹锛夈€?        //   鈶?鎶墜涔嬪悗锛氳繘"浣欐俯"锛屼笖**蹇呴』璁╂寚閽堢寮€鍒ゅ畾鍖轰竴娆?*锛屽啀鍋滃洖鏉ワ紙骞惰窛鎶墜 >
        //      RailRestMs锛夛紝鎵嶅厑璁搁噸鏂板紶寮€鈥斺€斿啓瀛楅棿姝囩殑鍋滈】涓嶅啀鎶婂崱鐗囧脊鍑烘潵銆?        bool drawingNow = _host.State.IsDrawing;
        double now = _host.NowMs;
        if (drawingNow && !_railWasDrawing)
        {
            _railRequireLeave = true;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value < 0.999f) _rail.To(0f, Tokens.RailMs);
        }
        if (!drawingNow && _railWasDrawing) _railStrokeEndMs = now;
        _railWasDrawing = drawingNow;
        if (drawingNow)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            return;
        }

        // 绌块€忥細涓婂甫**姘歌繙淇濇寔鏀惰捣鐨勯偅鏉¤壊绾?*锛屼笉璁稿紶鎴愯缃潯
        //锛?026-10-02 鐢ㄦ埛鍙ｅ緞锛氱偣绌块€忓彧鏄妸瀹?鏀惰捣鏉?锛屼笉鏄暣鏉℃秷澶扁€斺€旈潰鏉块珮搴︿笉鍙橈紝
        //  璐磋竟闅愯棌闇插嚭鏉ョ殑杩樻槸瀹冿紱浣嗕篃涓嶅啀鍍忓埆鐨勬牸瀛愰偅鏍峰紶鐫€璁剧疆鏉★級銆?        // 杩涚┛閫忛偅涓€涓嬪凡缁忓湪 OnStateChanged 閲屽惎鍔ㄦ姌鍙犲姩鐢伙紱杩欓噷鍏滀綇"涔嬪悗鍙堣璋佸己琛屽紶寮€"
        //锛堟祴璇曢挬瀛愩€佷互鍙婃偓鍋滄剰鍥炬畫鐣欙級锛屽苟璁╅偅涓や釜璁℃椂鍣ㄥけ鏁堛€?        if (_host.State.PassThrough)
        {
            _railHover = false;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value >= 0.5f) _rail.To(0f, Tokens.RailMs);
            return;
        }

        // 姝ｅ湪鎷栨粦鏉★細涓€鐩村紑鐫€锛屼笉鍙備笌鎮仠閭ｅ璁℃椂
        // 锛堟墜婊戝埌杞ㄩ亾澶栭潰涓€鐐圭偣涓嶈璁╄缃潯鏀舵帀鈥斺€旀嫋鍒颁竴鍗婃敹鎺夋槸鏈€姘斾汉鐨勪竴绉嶏級
        if (_sliderDragging)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 鑹插甫甯稿紑锛?026-10-05 鐢ㄦ埛鐐瑰悕鐨勫紑鍏筹級锛氫竴鐩存憡鐫€锛屼笉鍙備笌鎮仠閭ｅ鏀舵斁銆?        if (_railPinned)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 瑙︽懜娌℃湁鎮仠锛氭墜鎸囩偣鍦?*闈㈡澘浠绘剰澶?*锛堢偣宸ュ叿鏍间篃绠楋級灏辨妸璁剧疆鏉″紶寮€锛屽苟淇濇寔涓€娈?        // 锛堟澗鎵嬪悗 2.5s 鍐呬笉鏀讹級鈥斺€斾笉鐒惰Е鎽哥敤鎴峰彧鑳藉幓鐐归偅鏉¤壊绾匡紙10 鍍忕礌锛夛紝寰堥毦鐐逛腑
        // 锛?026-10-05 鐢ㄦ埛瀹炴祴锛?鐐瑰嚮鍥炬爣鑹插甫涓嶄細灞曞紑锛屽繀椤荤偣鑹插甫浣嶇疆"锛岃Е鎽稿睆涓婂お楹荤儲锛夈€?        if (now < _railTouchHoldUntilMs)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        if (_railHover)
        {
            // 鍐欏瓧浣欐俯 + "绂诲紑涓€娆?纭鍒欙紙瑙佷笂闈?鈶級锛氭姮鎵嬪悗椹笂鍋滃洖鏉ヤ篃涓嶅脊銆?            if (_railRequireLeave || now - _railStrokeEndMs < Tokens.RailRestMs)
            {
                _railEnterAtMs = double.NegativeInfinity;
                return;
            }
            _railExitAtMs = double.NegativeInfinity;
            // **姝ｅ湪鏀舵椂鎸囬拡鍥炴潵锛氱珛鍒诲弽鐫€寮犲紑**锛?026-10-09 娌夌ǔ妗ｇ殑瀵瑰伓锛夈€?            // 浠ュ墠瑕佺瓑瀹冩敹鍒?0.5 浠ヤ笅鎵嶈偗閲嶅紑锛堟敹灏?+ 240ms 寤惰繜 + 230ms 鍔ㄧ敾锛屾渶鍧?~0.7s锛夆€斺€?            // 鎵嬫劅鏄?鎴戦兘鎶婃寚閽堟斁鍥炲幓浜嗗畠杩樺湪鏀?锛涜嚜妫€閲囨牱涔熶細鎶撳湪鍔ㄧ敾涓棿锛坙ayout 鍒ゆ嵁鍏ㄤ贡锛夈€?            if (_rail.Running && _rail.Target < 0.5f)
            {
                _rail.To(1f, Tokens.RailMs);
                _railEnterAtMs = double.NegativeInfinity;
                return;
            }
            if (_rail.Value >= 0.5f) { _railEnterAtMs = double.NegativeInfinity; return; }
            if (double.IsNegativeInfinity(_railEnterAtMs)) _railEnterAtMs = now;
            else if (now - _railEnterAtMs >= Tokens.RailShowDelayMs) _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 鎸囬拡绂诲紑鍒ゅ畾鍖衡€斺€?*鍙湁"鏂扮殑浣嶇疆鏇存柊"鎵嶇畻鏁?*锛堣 _railHoverStamp 娉ㄩ噴锛夛紱
        // 杩欐潯鍚屾椂鍏戠幇鍐欏瓧鍚庣殑"绂诲紑涓€娆?銆?        if (!_railHover && _railHoverStamp > _railStrokeEndMs) _railRequireLeave = false;
        _railEnterAtMs = double.NegativeInfinity;
        if (_rail.Value <= 0.001f) { _railExitAtMs = double.NegativeInfinity; return; }
        if (double.IsNegativeInfinity(_railExitAtMs)) _railExitAtMs = now;
        else if (now - _railExitAtMs >= Tokens.RailHideDelayMs) _rail.To(0f, Tokens.RailMs);
    }

    /// <summary>
    /// 鑹茬嚎鐨?纰板埌"鍒ゅ畾鍖猴細**鎸夊紶寮€鍚庣殑楂樺害**绠椼€?    ///
    /// 涓嶈繖涔堝仛鐨勮瘽浼氭潵鍥炴姈锛氱嚎涓€寮犲紑锛屽畠鐨勭煩褰㈠氨寰€涓婇暱浜?28 鍍忕礌锛?    /// 鎸囬拡锛堣繕鍋滃湪鍘熸潵閭ｆ潯绾跨殑浣嶇疆锛夌珛鍒昏惤鍒板垽瀹氬尯澶栭潰 鈫?鍙堟敹鍥炲幓 鈫?鍐嶅紶寮€鈥︹€?    /// </summary>
    private RectF RailZone()
    {
        var bar = BarRect();
        // 鈿?杩欓噷浠ュ墠鏄?`Tokens.BandHeight`锛?*涓€琛?*鐨勯珮锛夛紝鍥惧舰閭ｄ竴鏍兼敼鎴愬琛屼箣鍚庡氨鐭簡涓€鎴細
        // 鎸囬拡鎸埌涓婇潰閭ｅ嚑琛屼細琚垽鎴?绂诲紑闈㈡澘"銆佸甫瀛愬綋鍦烘敹锛堢敤鎴?2026-09-20 鎶ョ殑 bug锛夈€?        float h = BandBlockFull() + Tokens.RailHoverPad * 2f;
        return BandAbove()
            ? new RectF
            {
                MinX = bar.MinX, MinY = bar.MinY - BandGap - h,
                MaxX = bar.MaxX, MaxY = bar.MinY - BandGap + Tokens.RailHoverPad,
            }
            : new RectF
            {
                MinX = bar.MinX, MinY = bar.MaxY + BandGap - Tokens.RailHoverPad,
                MaxX = bar.MaxX, MaxY = bar.MaxY + BandGap + h,
            };
    }

    /// <summary>
    /// "鐒︾偣鍦ㄦ偓娴涓?鐨勫垽瀹氬尯 = **涓绘潯 鈭?璁剧疆鏉?*锛堢敤鎴?2026-09-17 瀹氱殑璇箟锛夈€?    ///
    /// 浠ュ墠鍙湁**璁剧疆鏉¤嚜宸遍偅涓€鏉?*绠楋紝浜庢槸鑰佸笀鎯虫妸榧犳爣浠庝富鏉℃尓鍒拌壊鐗囦笂鏈変袱娈佃矾锛?    /// 鍏堢鍒拌壊绾裤€佺瓑 120 姣寮犲紑銆佸啀鐐瑰伐鍏烽拤浣忥紝閽変綇浜嗘墠鏁㈡妸鎸囬拡鎸笂鍘汇€?    /// 鐜板湪鏁村潡闈㈡澘閮界畻"鐒︾偣鍦ㄩ潰鏉夸笂"锛?*鎸囬拡鍦ㄩ潰鏉夸笂瀹冨氨寮犲紑锛屾寚閽堢寮€灏辨敹鎴愯壊绾?*锛?    /// 涓棿涓嶇敤鍐嶇偣涓€娆°€佷篃涓嶄細璧颁袱姝ュ氨鏀跺洖鍘汇€?    ///
    /// 娉ㄦ剰鍒ゆ嵁鐢ㄧ殑鏄?*涓绘潯 鈭?鑹插甫**鑰屼笉鏄?鏁翠釜 UnionRect"锛氳创杈归殣钘忔椂 UnionRect
    /// 浼氳澶瑰埌鍙墿 8 鍍忕礌鐨勯湶澶达紝鐢ㄥ畠褰撳垽瀹氬尯浼氳"璐磋竟鐨勯潰鏉?鍦ㄦ寚閽堟病闈犺繎鏃朵篃灞曞紑銆?    /// </summary>
    private RectF RailHoverZone()
    {
        var bar = BarRect();
        var rail = RailZone();
        return new RectF
        {
            MinX = MathF.Min(bar.MinX, rail.MinX),
            MinY = MathF.Min(bar.MinY, rail.MinY),
            MaxX = MathF.Max(bar.MaxX, rail.MaxX),
            MaxY = MathF.Max(bar.MaxY, rail.MaxY),
        };
    }

    /// <summary>
    /// 杩欎竴妗ｆ樉绀哄摢鍑犱釜鑹茬墖锛堣繑鍥?*鑹茬墖琛?*閲岀殑涓嬫爣锛夈€?    ///
    /// 涓夊紶鏄犲皠鍚勬湁鍚勭殑鐢ㄦ硶锛堣〃鏈韩鍦?<see cref="Tokens"/> 閲岋級锛?    ///   路 **鑽у厜绗?*锛坄HlSwatchIdx`锛夛細鐢?`Tokens.HighlighterPalette` 閭ｅ紶浜壊琛紝
    ///     5 涓叏缁欌€斺€?026-09-27 鐢ㄦ埛瀹氱殑"鐙珛涓€寮犱寒鑹茶〃"锛?    ///   路 **鏋佺畝妗ｇ殑绗?*鍙粰 4 涓細鐭兌鍥婏紙359 瀹斤級閲屽 12 涓壊鐗囥€佸啀鍑忔帀婊戞潯鍗犵殑閭?146锛?    ///     姣忎釜鍙墿 11 鍍忕礌瀹解€斺€旂偣閮界偣涓嶅噯锛堢敤鎴?2026-09-17锛?鏋佺畝妯″紡鐨勮壊甯﹀睍寮€鏍忛噷闈㈢殑
    ///     鍐呭鎺掑竷鏈夌偣闂"锛夈€? 涓殑璇濇瘡涓?42 鍍忕礌锛屽拰瀹屾暣妗ｄ竴涓墜鎰熴€?    ///     棰滆壊鐓у亣闈㈡澘瀹氱殑锛?*绾?/ 榛?/ 钃?/ 鐧?*锛堣璇炬椂鏈€甯哥敤鐨勫洓鏀級锛?    ///   路 **瀹屾暣妗ｇ殑绗?*锛?2 涓叏缁欍€?    ///
    /// 鈿?鏋佺畝妗ｉ噷**娌℃湁鑽у厜绗旈偅涓€鏍?*锛堣 `MiniCells`锛夛紝鎵€浠ラ偅閲屼笉浼氱敤鍒拌崸鍏夌瑪琛紱
    ///    浣嗘槧灏勮繕鏄粰鍏ㄤ簡鈥斺€斿摢澶╂瀬绠€妗ｅ姞浜嗚崸鍏夌瑪鏍硷紝杩欓噷涓嶇敤鍐嶈ˉ銆?    /// 鈿?`MiniSwatchIdx` 閲岄偅鍑犱釜鏁版槸**鏂拌壊琛ㄧ殑涓嬫爣**锛?026-09-27 鎹㈣〃鏃堕鑹蹭綅娆″姩杩?    ///   锛堢豢鍜岃摑瀵硅皟銆佺伆/闈?绮?妫曟嬁鎺夛級锛屾墍浠ヨ繖涓€琛岃窡鐫€鏀规垚浜?{ 1 绾? 0 榛? 2 钃? 7 鐧?}銆?    /// </summary>
    private static readonly int[] MiniSwatchIdx = { 1, 0, 2, 7 };      // 绾?/ 榛?/ 钃?/ 鐧?    private static readonly int[] FullSwatchIdx = CreateAllIdx(Tokens.Palette.Length);
    private static readonly int[] HlSwatchIdx = CreateAllIdx(Tokens.HighlighterPalette.Length);

    private static int[] CreateAllIdx(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    /// <summary>杩欎竴鍒荤敤鐨勬槸鑽у厜绗旈偅寮犺〃鍚楋紙鑹茬墖琛?/ 鍒囪壊寰幆閮界湅瀹冿級銆?/summary>
    private bool UsingHighlighterSwatches => _host != null && _host.State.Tool == Tool.Highlighter;

    /// <summary>杩欎竴鍒荤敓鏁堢殑鑹茬墖琛紙绗旈偅寮?/ 鑽у厜绗旈偅寮狅級銆?/summary>
    private (string Name, Color4 Color)[] SwatchTable =>
        UsingHighlighterSwatches ? Tokens.HighlighterPalette : Tokens.Palette;

    private int[] SwatchIdx => UsingHighlighterSwatches
        ? HlSwatchIdx
        : (_profile == Profile.Mini ? MiniSwatchIdx : FullSwatchIdx);
    private int SwatchCount => SwatchIdx.Length;
    private Color4 SwatchColor(int i) => SwatchTable[SwatchIdx[i]].Color;

    private void ActivateSwatch(int i) => _host.Commands.SetColor(SwatchColor(i));

    /// <summary>
    /// **宸茬粡鏄畠浜嗭紝鍐嶇偣涓€涓?= 鎹笅涓€涓鑹?*锛堢敤鎴?2026-09-27 瀹氱殑锛夈€?    ///
    /// 鑰佸笀鐢ㄧ潃绗旀兂鎹釜鑹诧紝鍘熸潵鏄?鎶婃寚閽堟尓鍒拌壊甯︿笂銆佺瀯鍑嗘煇涓壊鐗?锛堣繕寰楀厛绛夎壊甯﹀紶寮€锛夛紱
    /// 鐜板湪鐩存帴鍦ㄥ伐鍏锋牸涓婅繛鐐瑰氨琛屸€斺€?*鎵嬩笉鐢ㄧ寮€閭ｄ竴鏍?*銆傝崸鍏夌瑪鍚岀悊銆?    ///
    /// 涓夋潯瑙勭煩锛?    ///   路 寰幆鑼冨洿 = **鑹插甫涓婄湅寰楄鐨勯偅鎺掕壊鐗?*锛坄SwatchIdx`锛夛細鎵€瑙佸嵆鎵€寰楋紝涓嶄細鍒囧埌涓€涓?    ///     鑹插甫涓婃病鏈夌殑棰滆壊锛涜壊鐗囩殑楂樹寒鍦堝氨鏄?鎴戠幇鍦ㄦ槸鍝釜鑹?鐨勬寚绀哄櫒锛?    ///   路 椤哄簭 = 鑹茬墖琛ㄩ『搴忥紙甯哥敤鐨勬帓鍓嶉潰锛岃 `Tokens.Palette` 鐨勬敞閲婏級锛?    ///   路 褰撳墠鑹蹭笉鍦ㄨ〃閲岋紙姣斿浠庨€変腑闈㈡澘鏀硅繃鑹诧級鈫?钀藉埌**绗?1 涓?*銆?    ///
    /// 鈿?**鍙湪"鑹插甫鏈潵灏卞湪杩欎竴鏍?鏃舵墠鍒囪壊**锛堣皟鐢ㄥ鐨勫垽鎹級锛氳€佸笀浠庡浘褰㈤潰鏉跨偣鍥炵瑪閭ｄ竴鏍硷紝
    ///   鎰忔€濇槸"鎶婄瑪鎷垮洖鏉?锛岄偅鏃跺€欐倓鎮勬崲涓鑹叉槸鏈€姘斾汉鐨勶紙鍚屽浘褰㈤偅涓€鏍?涓嶅姩宸ュ叿"鐨勮鐭╋級銆?    /// </summary>
    private void CycleColor()
    {
        int n = SwatchCount;
        if (n == 0) return;
        var cur = _host.State.PaletteBase;
        int at = -1;
        for (int i = 0; i < n; i++)
            if (SameColor(SwatchColor(i), cur)) { at = i; break; }
        _host.Commands.SetColor(SwatchColor((at + 1) % n));
        Invalidate();
    }

    /// <summary>
    /// 涓や釜棰滆壊绠椾笉绠楀悓涓€涓紙闃堝€?0.02锛夈€?    /// **鑹茬墖楂樹寒鍜屽垏鑹叉壘浣嶇疆鍏辩敤杩欎竴鎶婂昂瀛?*鈥斺€斿悇鍐欎竴浠界殑璇濊繜鏃╀笉涓€鑷达細
    /// 浼氬嚭鐜?楂樹寒鐪嬬潃鍦ㄨ繖涓壊涓婏紝鐐逛竴涓嬪嵈浠庝笅涓€涓壊寮€濮嬪垏"杩欑浜嬨€?    /// </summary>
    private static bool SameColor(in Color4 a, in Color4 b) =>
        MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;

    /// <summary>
    // [2026-10-05 鐢ㄦ埛瀹歖 `CycleSelectMode`锛堢偣閫夋嫨鏍?鍐嶆寜 Ctrl+M 鍒囩煩褰⑩啍濂楃储锛夊凡鍒犻櫎锛?    // 鐖辩敤鐭╁舰鐨勪竴鐩寸敤鐭╁舰銆佺埍鐢ㄥ绱㈢殑涓€鐩寸敤濂楃储锛涘瓙绫诲瀷鍦ㄩ潰鏉夸笂甯﹂偅涓ゆ閲岄€夛紙瑙?Activate case 7锛夈€?
    /// <summary>
    /// 鐐逛簡涓€涓嬭櫄瀹炵嚎閭ｄ竴鏍硷細**涓夋。杞祦**锛堝疄绾?鈫?铏氱嚎 鈫?鐐圭嚎 鈫?瀹炵嚎锛夈€?    ///
    /// 鍜屾鐨偅涓€鏍?鐐逛竴涓嬫崲涓€娆?鏄悓涓€涓害瀹氣€斺€斾笂甯﹂噷鎽嗕笉涓嬩笅鎷夋锛?*鐐瑰氨鏄垏**锛?    /// 涓夋。鑰屼笉鏄袱妗ｏ紝鏄洜涓哄紩鎿庨偅杈圭嚎鍨嬫湰鏉ュ氨鏄笁妗ｏ紙瑙?StrokeDash锛夛紝
    /// 灏戠粰涓€妗ｇ瓑浜庢妸"鐐圭嚎"钘忚捣鏉ャ€?    ///
    /// 椤烘墜璧蜂竴娆℃崲鎸″姩鐢伙紙瑙?<see cref="_dashFade"/>锛夛細**鏃ф。瑕佸厛褰撳満璁颁笅鏉?*锛?    /// 涓嶇劧鍛戒护涓€鍙戙€佺姸鎬佸氨鍙樹簡锛屽姩鐢诲紑濮嬩箣鍚庡啀涔熼棶涓嶅嚭"浠庡摢涓€妗ｆ潵鐨?銆?    /// </summary>
    private void CycleDash()
    {
        var cur = _host.State.Dash;
        var next = cur switch
        {
            StrokeDash.Solid => StrokeDash.Dashed,
            StrokeDash.Dashed => StrokeDash.Dotted,
            _ => StrokeDash.Solid,
        };
        _dashFadeFrom = cur;
        _dashFade.Jump(0f);
        _dashFade.To(1f, Tokens.RailMs);
        _host.Commands.SetDash(next);
        Invalidate();
    }

    private void ActivateSegment(int i)
    {
        // 鑹插甫涓婄殑鍔ㄩ潤涔熺畻"绂诲紑浜嗛偅涓€鏍?锛氬弻鍑诲簭鍒楁柇鎺夛紙鍚?`Activate` 閲岄偅涓€鍙ワ級銆?        _lastSelCellClickMs = double.NegativeInfinity;
        switch (_bandCell)
        {
            case 2:                       // 鐧芥澘锛歔鈥筣 绗?N 灞?[鈥篯 鈹?[鐧絔[缁縘[榛慮 鈹?[搴曠汗][闂磋窛]
            {
                // 杩欎竴鏍?*鍙鐧芥澘鐨勪簨**锛氱炕椤垫案杩滄槸鐧芥澘鐨?涓婁竴灞?/ 涓嬩竴灞?銆?                //
                // 鈿?2026-09-26 鏀瑰洖鏉ヨ繃涓€娆★細涓棿鏇捐瀹?鏀炬槧鏃跺彉鎴?PPT 鐨勪笂涓€椤?涓嬩竴椤?锛?                // 鐢ㄦ埛鐨勭粨璁烘槸**涓嶈**鈥斺€斿師璇?鍙栨秷鎺夌櫧鏉垮尯鐨勭炕椤碉紝鐧芥澘鍖轰笉闇€瑕?ppt 缈婚〉"銆?                // 璇箟涓婁篃鏄鐨勶細杩欎竴鏍兼槸"杩欏潡鏉挎€庝箞鎽?锛屼笉璇ョ骞荤伅鐗囩炕鍒扮鍑犲紶銆?                // PPT 鐨勭炕椤靛叆鍙ｅ彧鏈?*搴曢儴閭ｄ袱鏉?*锛坄PptBar`锛夛紜 PPT 鑷繁鐨勯仴鎺у櫒/閿洏銆?                //
                // 娈靛簭**涓嶅啓姝讳笅鏍?*锛屼竴寰嬮棶 `BoardSegAt`锛堟琛ㄥ彧鏈?BoardBand 閭ｄ竴浠斤紱
                // 2026-09-27 閲嶆帓涔嬪墠杩欓噷鍐欑殑鏄?`i == 0` / `i == 4`锛屾搴忎竴鍔ㄥ氨浼氶潤榛樼偣閿欙級銆?                var seg = BoardSegAt(i);
                switch (seg.Kind)
                {
                    case BoardSegKind.PageUp: _host.Commands.FlipPage(false); break;
                    case BoardSegKind.PageDown: _host.Commands.FlipPage(true); break;
                    case BoardSegKind.PageLabel: break;    // 鍙鐨勪竴鍧楋紝鐐瑰畠浠€涔堥兘涓嶅仛

                    // 鏉胯壊 / 搴曠汗 / 闂磋窛锛氶兘鏄?瑕佺敤鏉?鐨勬剰鎬濓紝鎵€浠ラ兘椤烘墜鎶婃澘鎵撳紑
                    //锛堝簳绾瑰彧鍦ㄦ澘闈笂鐢诲緱鍑烘潵锛屼笉鎵撳紑灏辩瓑浜庢敼浜嗙湅涓嶈鈥斺€旇繖涓€鏉″師鏉ユ槸闈?                    //  "鐧芥澘娌″紑灏辨妸閭ｄ袱琛屽帇鏆?鏉ュ畧鐨勶紝鎼繘鑹插甫涔嬪悗鏀规垚"椤烘墜鎵撳紑"鏇村ソ鐢級銆?                    case BoardSegKind.Color:
                        _host.Commands.SetBoardColor(InkPalette.BoardPresets[seg.Idx].Color);
                        _host.Commands.SetBoard(true);
                        break;
                    case BoardSegKind.Pattern:
                    {
                        var st = _host.State;
                        int next = (st.BoardPattern + 1) % PatternNames.Length;
                        _host.Commands.SetBoardPattern(next, st.BoardPatternStep);
                        _host.Commands.SetBoard(true);
                        SavePrefs();
                        Invalidate();
                        break;
                    }
                    case BoardSegKind.Step:
                    {
                        var st = _host.State;
                        int idx = Array.FindIndex(PatternSteps, v => MathF.Abs(v - st.BoardPatternStep) < 0.5f);
                        float next = PatternSteps[(idx + 1 + PatternSteps.Length) % PatternSteps.Length];
                        _host.Commands.SetBoardPattern(st.BoardPattern, next);
                        _host.Commands.SetBoard(true);
                        SavePrefs();
                        Invalidate();
                        break;
                    }
                }
                break;
            }
            case 6:                       // 鏁寸瑪鎿?/ 闈㈢Н鎿?鈥斺€?寮曟搸閲屾槸**涓や釜宸ュ叿**
                _host.Commands.SetTool(i == 0 ? Tool.Eraser : Tool.PixelEraser);
                break;
            case 7:                       // 鐭╁舰妗嗛€?/ 鑷敱濂楃储
                _host.Commands.SetSelectMode(i == 0 ? SelectMode.Rect : SelectMode.Lasso);
                break;
            case 8:                       // 鍥惧舰閭ｄ竴鏍硷細**涓よ**鎷夊钩缂栧彿锛岄『搴忚涓ゅ紶琛?                if (i >= 0 && i < ShapeBandSegments)
                {
                    // 鏈€鍚庨偅涓€娈垫槸**鍔ㄤ綔**锛氱偣寮€鍥惧簱闈㈡澘锛?鎴戠殑鍥惧舰"锛?026-09-22锛夈€?                    // 瀹冧笉杩?ShapeRows锛堥偅寮犺〃鏄伐鍏疯〃锛夛紝鎵€浠ヨ繖閲?*蹇呴』鍏堝垽**銆?                    if (i >= ShapeSegmentCount) { _host.Commands.ToggleLibraryPanel(); break; }

                    var picked = ShapeToolAt(i);
                    // **鍚屼竴涓浘褰㈡牸鍐嶇偣涓€娆?= 鎹竴妗?*銆傚彧瀵逛笁绉嶅浘褰㈣繖鏍凤紝鍒殑鐓ф棫"鍐嶇偣=閫変腑瀹?锛?                    //   路 鎶涚墿绾匡細鎹㈠紑鍙ｆ柟鍚戯紙鐢ㄦ埛 2026-09-20 瀹氾細"閫変腑妗嗙殑鎶涚墿绾挎寜閽彇娑堬紝
                    //     鎴戜笉鎵撶畻浠庤繖涓浆寮€鍙? 鈫?鏈濆悜鏀瑰埌**鐢讳箣鍓?*瀹氾級锛?                    //   路 鐩寸嚎锛氭崲绾垮瀷锛堢敤鎴?2026-09-20 瀹氾細"鐐瑰嚮鐩寸嚎鐨勫浘鏍囷紝瀹冧細鍙樻垚铏氱嚎锛?                    //     鍐嶇偣鍑诲彉鎴愮偣铏氱嚎锛屽啀鐐瑰嚮鍙堝彉鎴愮洿绾库€︹€﹁繖鏍峰氨鐪佷簡濂藉嚑涓┖闂存牸"锛夛紱
                    //   路 妫辨煴 / 妫遍敟 / 妫卞彴锛氭崲搴曢潰鍑犺竟褰紙鐢ㄦ埛 2026-09-20 瀹氾細"鎴戞兂鎯宠兘涓嶈兘鍋氭垚
                    //     鍍忕洿绾垮垏鎹㈤偅鏍峰垏鎹笁鍥涗簲鍏?锛夈€?*涓夋牸鍚勮鍚勭殑妗?*锛堟崲璋佸彧鍔ㄨ皝锛夛紝
                    //     鍒ゆ嵁鍙湁 `ShapeSpec.HasSideCount` 涓€澶勨€斺€斿姞妫遍敟 / 妫卞彴鏃惰繖閲屽樊鐐规紡鎺夈€?                    if (picked == Tool.Parabola && _host.State.Tool == Tool.Parabola)
                        _host.Commands.CycleParabolaAxis();
                    else if (picked == Tool.Line && _host.State.Tool == Tool.Line)
                        _host.Commands.CycleLineDash();
                    // 鍙屾洸绾匡細鎹?鐢讳笉鐢绘笎杩戠嚎"锛?026-09-22锛岀敤鎴凤細"澧炲姞涓ゆ尅锛屾湁娓愯繎绾垮拰鏃犳笎杩戠嚎
                    // 鈥︹€﹀浘鏍囧氨鎸夌収鏈夋笎杩戠嚎鍜屾棤娓愯繎绾?锛夈€?                    else if (picked == Tool.Hyperbola && _host.State.Tool == Tool.Hyperbola)
                        _host.Commands.CycleHyperbolaAsymptotes();
                    // 妞渾锛堝甫鐒︾偣锛夛細鎹?鐢讳笉鐢荤劍鐐逛笁瑙掑舰"锛?026-09-22锛岀敤鎴凤細"妞渾涔熸湁涓ゆ。"锛夈€?                    else if (picked == Tool.ConicEllipse && _host.State.Tool == Tool.ConicEllipse)
                        _host.Commands.CycleEllipseFocusTriangle();
                    // **鍧愭爣绯伙細鎹?瑕佷笉瑕佺綉鏍?**锛堢敤鎴?2026-09-24锛?鎴戞墦绠楁妸瀹冩尓鍒板浘褰㈤噷闈㈢殑閭ｄ釜
                    // 鍧愭爣绯烩€︹€︾偣涓€涓嬪垏鎹㈡垚缃戞牸锛岀偣涓€涓嬬綉鏍兼病浜?锛夈€?                    // 鍘熸潵杩欎竴妗ｅ湪銆屾洿澶氥€嶆娊灞夐噷锛?鍧愭爣绯荤綉鏍?閭ｄ竴琛岋級锛屾尓鍒拌繖涓€鏍间箣鍚?                    // **鍜屾姏鐗╃嚎/鐩寸嚎/鍙屾洸绾夸竴鏍锋槸"鍚屼竴涓牸鍐嶇偣涓€娆℃崲涓€妗?**鈥斺€斾竴鑷达紝鑰屼笖
                    // 鐢讳箣鍓嶆墜鎸囧氨鍦ㄨ繖涓€鏍间笂锛屼笉鐢ㄥ啀鍘绘娊灞夐噷鎵俱€?                    // 钀界洏鍙湅"鏀圭殑鏄笉鏄柊鐢荤殑榛樿鍊?锛堣繑鍥炲€?0 = 鏄紱鏀瑰凡鐢荤殑瀵硅薄涓嶇畻鍋忓ソ锛夈€?                    else if (picked == Tool.Coordinate && _host.State.Tool == Tool.Coordinate)
                    {
                        if (_host.Commands.ToggleCoordGrid() == 0) SavePrefs();
                    }
                    else if (ShapeSpec.HasSideCount(picked) && _host.State.Tool == picked)
                        _host.Commands.CycleSolidSides();
                    else
                        _host.Commands.SetTool(picked);
                }
                break;
            case 9:                       // 鎴浘锛歔鎴浘][闅愯棌绐楀彛鎴浘]锛?绮樿创鍥剧墖"宸叉尓鍒板彸绔殑鍔ㄤ綔鎸夐挳锛?                // 鍚嶅瓧鐓у井淇￠偅濂楋紙8.3.1锛夛紱**鐐瑰摢涓€娈靛氨鐢ㄥ摢绉嶆埅娉曡繘灞?*鈥斺€?                // 娈垫湰韬槸"鍏ュ彛"锛歚EnterCapture` 涓€杩涘幓灏辨暣灞忕伆涓嬫潵锛堣瑙?IEngineCommands锛夈€?                // 浠ュ墠鐐规牸瀛愬氨鍙妸宸ュ叿閫変腑銆佷粈涔堥兘涓嶅彂鐢燂紝"浠垮井淇′豢寰椾笉鍍?灏辨槸杩欐潯銆?                _host.Commands.SetCaptureHideInk(i == 1);
                _host.Commands.EnterCapture();
                break;
        }
    }

    // ---- 妗ｄ綅涓庨拤浣忥紙銆屾洿澶氥€嶉潰鏉裤€屽伐鍏锋潯銆嶉偅涓€缁勫湪鐢級--------------------

    private int ProfileIndex() => (int)_profile;

    /// <summary>涓绘潯 锛?涓婂甫銆?/summary>
    private RectF PanelRect()
    {
        var bar = BarRect();
        var band = BandRect();
        if (band.MaxY - band.MinY < 2f) return bar;
        return new RectF
        {
            MinX = bar.MinX, MinY = Math.Min(bar.MinY, band.MinY),
            MaxX = bar.MaxX, MaxY = Math.Max(bar.MaxY, band.MaxY),
        };
    }

    private bool IsToggleRow(int i)
        => Rows[i].Kind is Row.DarkTheme or Row.AutoHide or Row.RailPin or Row.Tooltip or Row.DwellShape
           or Row.Pressure or Row.Predict or Row.RestoreInk or Row.PptAutoSave;

    /// <summary>
    /// **鑷鐢?*锛氳繖涓€琛屽埌搴曟湁娌℃湁鐢诲紑鍏炽€?    ///
    /// 涓轰粈涔堜笓闂ㄥ紑涓€涓嚭鍙ｏ細2026-10-07 鍔犮€岀簿缁嗙瑪杩广€嶉偅涓€琛屾椂锛?    /// 鎴戞妸琛屻€佷綅缃€佺姸鎬併€佺偣鍑汇€佽惤鐩樺叏鎺ュソ浜嗭紝**鍞嫭婕忎簡鎶婂畠鍔犺繘 <see cref="IsToggleRow"/>**
    /// 鈥斺€?浜庢槸**鏍囩鐢诲嚭鏉ヤ簡銆佸紑鍏虫病鐢?*锛岀敤鎴风湅鍒扮殑鏄?杩欎竴琛屾€庝箞娌℃湁寮€鍏?銆?    ///
    /// 鑰屽綋鏃剁殑鑷鍙仛"鐐逛竴涓?鈫?鐘舵€佺炕杞?锛?*鐐瑰嚮鍒ゅ畾鐪嬬殑鏄暣琛岀煩褰?*锛?    /// 鎵€浠ュ紑鍏崇敾娌＄敾瀹冮兘鑳借繃 鈥斺€?**娴嬭瘯娌＄洊浣忕湡姝ｉ敊鐨勫湴鏂?*銆?    /// 鐜板湪鎶?鏈夋病鏈夊紑鍏?鍙樻垚涓€鏉″彲鏂█鐨勪簨瀹烇細浠ュ悗鍔犲紑鍏宠婕忔帀杩欎竴澶勶紝鑷灏变細绾€?    /// </summary>
    internal bool IsToggleRowForTest(int i) => IsToggleRow(i);

    /// <summary>鑷鐢細鎸夋爣绛鹃棶"杩欎竴琛屾湁娌℃湁寮€鍏?锛堟壘涓嶅埌鏍囩杩斿洖 false锛夈€?/summary>
    internal bool IsToggleRowByLabelForTest(string label)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label == label) return IsToggleRow(i);
        return false;
    }

    /// <summary>
    /// 杩欎竴琛岀幇鍦ㄦ槸涓嶆槸鍘嬫殫锛堢偣浜嗘病鍙嶅簲锛夈€?    /// 鐩墠鍙湁涓€绉嶆潵婧愶細琛ㄩ噷鍐欐鐨勶紙妫€鏌ユ洿鏂拌繕娌″仛锛夈€?    ///
    /// 鈿?2026-09-27 涔嬪墠杩樻湁涓€鏉?鐧芥澘娌″紑鏃跺簳绾逛袱琛屽帇鏆?鈥斺€旈偅涓よ宸茬粡鎼繘鐧芥澘鑹插甫
    /// 锛堣 <see cref="BoardSegKind"/>锛夛紝鎵€浠ヨ繖鏉＄壒渚嬭窡鐫€鍒犱簡銆?    /// </summary>
    private bool IsGrayRow(int i) => Rows[i].Gray;

    /// <summary>搴曠汗涓夋。鐨勫悕瀛楋紙0/1/2锛夛紝鍜屽紩鎿庨偅杈圭殑鍙栧€间竴涓€瀵瑰簲銆?/summary>
    private static readonly string[] PatternNames = { "鏃?, "鏂规牸", "妯嚎" };
    /// <summary>
    /// 搴曠汗闂磋窛鐨勬。浣嶏紙閫昏緫鍍忕礌锛夈€?*浜旀。**锛?026-09-27 鐢ㄦ埛锛?闂磋窛鍙互妗ｄ綅锛屼絾闇€瑕佸鍑犳。"锛夛細
    /// 20 鏈€缁嗭紙瀵嗛泦鏍煎瓙褰撳潗鏍囩焊锛夈€?0 鏄粯璁わ紙锛濆紩鎿庨粯璁ゅ€硷紝鍑哄巶灏卞湪姝ｄ腑闂达級銆?6 鏈€绮楋紙褰撴í绾跨焊锛夈€?    /// 鈿?寮曟搸閭ｈ竟澶瑰湪 8锝?40 涔嬮棿锛坄Engine.SetBoardPatternFromUi`锛夛紝杩欏嚑涓€奸兘鍦ㄩ噷闈€?    /// 鈿?**鏈€澶氬叚妗?*锛氭。浣嶇偣鏄珫鎺掔殑锛堢偣璺?5銆佹楂樺彧鏈?26锛夛紝绗竷涓氨婕忓嚭娈靛浜嗐€?    /// </summary>
    private static readonly float[] PatternSteps = { 20f, 30f, 40f, 64f, 96f };

    private static string PatternName(int p) =>
        PatternNames[Math.Clamp(p, 0, PatternNames.Length - 1)];

    private void ActivateRow(int i)
    {
        switch (Rows[i].Kind)
        {
            case Row.DarkTheme:
                _dark = !_dark;
                SavePrefs();
                PushFloatingTheme();       // 娴眰锛堟搷浣滄潯/灏忛潰鏉匡級涔熷緱璺熺潃鎹?                break;
            case Row.AutoHide:
                _hideEnabled = !_hideEnabled;
                _peek.Jump(1f);          // 鍒氭墦寮€鏃跺厛缁欎釜瀹屾暣鐨勶紝鍒竴寮€灏辩缉璧锋潵
                SavePrefs();
                break;

            // 鑹插甫甯稿紑锛?026-10-05锛夛細鎵撳紑鏃剁珛鍒绘妸瀹冩憡寮€锛涘叧鎺夊悗浜ゅ洖鎮仠閭ｅ锛堣Е鎽镐粛鏈夎嚜閫傚簲锛夈€?            case Row.RailPin:
                _railPinned = !_railPinned;
                if (_railPinned) _rail.To(1f, Tokens.RailMs);
                SavePrefs();
                break;

            // 鎮仠鎻愮ず锛?026-10-02锛夛細鍙奖鍝嶇晫闈㈣嚜宸憋紝钀界洏璧?"tooltip" 閭ｄ竴椤广€?            // 鍏虫帀鏃堕『鎵嬫妸宸茬粡鏄剧ず鐨勬彁绀烘竻鎺夛紙涓嶇劧瀹冭绛変笅涓€娆＄Щ寮€鎵嶆秷澶憋級銆?            case Row.Tooltip:
                _tipEnabled = !_tipEnabled;
                if (!_tipEnabled) HideTip();
                _host.Commands.SetTooltips(_tipEnabled);   // 寮曟搸鑷繁鐢荤殑娴眰璺熺潃寮€鍏?                SavePrefs();
                break;

            // 鐣岄潰澶у皬锛?026-10-10锛夛細浜旀。寰幆銆佸埌椤跺洖澶淬€傚紩鎿庣珛鍒荤敓鏁堬紙閲嶆帓 + 閲嶇敾锛夛紝
            // 鐣岄潰钀界洏 "uiscale"鈥斺€?*鏍囧噯妗ｄ笉鍐欓」**锛堝彧鍐欏拰榛樿涓嶄竴鏍风殑锛岃€佽鐭╋級銆?            case Row.UiSize:
            {
                float next = UiScalePresets.Next(_host.UiScale);
                _host.SetPref("uiscale", UiScalePresets.StoreValue(next));
                _host.Commands.SetUiScale(next);
                break;
            }

            // 鍋滈】鎴愬瀷锛氱炕杞紑鍏?鈫?鎺ㄧ粰寮曟搸 鈫?钀界洏锛?*鍙啓"鍏宠繃鐨?閭ｄ竴浠?*锛?            // 閰嶇疆閲屾病鏈夎繖涓€椤瑰氨鏄粯璁ゅ紑锛屼互鍚庨粯璁ゅ€兼敼浜嗚€侀厤缃笉浼氭妸鏂伴粯璁ら《鎺夛級銆?            case Row.DwellShape:
                _host.Commands.SetDwellShape(!(_host.State.DwellShapeOn));
                SavePrefs();
                break;

            // 鍘嬫劅绮楃粏锛?026-10-01锛夛細鍚屼竴鏉¤鐭┾€斺€斿紩鎿庢槸鏉冨▉锛岀晫闈㈢炕杞悗钀界洏銆?            // 鍏虫帀鏄?*娓叉煋鏈?*鐨勶細鏁村潡鏉跨珛鍒荤瓑瀹斤紝鏂囨。閲岀殑鍘嬪姏鏁版嵁涓嶅姩銆?            case Row.Pressure:
                _host.Commands.SetPressure(!_host.State.PressureOn);
                SavePrefs();
                break;

            // 婀垮ⅷ妗ｄ綅锛?026-10-13 鍔狅級锛氱偣涓€涓嬫崲涓€妗ｏ紙0鈫?鈫?鈫?鈫?锛夈€?            // 寮曟搸鏄潈濞侊細瀹冪珛鍒荤敓鏁?+ 钀界洏锛堣缃噷鐨?"wetmode"锛夛紝鐣岄潰涓嬩竴甯т粠
            // `Host.State.WetMode` 璇诲洖鏉ユ樉绀衡€斺€旂晫闈?*鑷繁涓嶈妗?*锛堥伩鍏嶄袱浠界姸鎬侊級銆?            case Row.WetMode:
                _host.Commands.SetWetMode((_host.State.WetMode + 1) & 3);
                break;

            // 澧ㄨ抗棰勬祴锛?026-10-09 鍔狅紱2026-10-14 娴嬭瘯鐗堣捣榛樿寮€锛夛細
            // 寮曟搸鏄潈濞侊紝鐣岄潰缈昏浆鍚庤惤鐩?"predict2"锛堝叧杩囨墠鍐?"0"锛夈€?            case Row.Predict:
                _host.Commands.SetPredict(!_host.State.PredictTailOn);
                SavePrefs();
                break;

            // 澧ㄨ抗涓夋潯鍋忓ソ锛堝師鏉ュ湪銆屽ⅷ杩广€嶉〉锛夛細鍙啓 `ui.*`锛?            // 榛樿鍊间笉钀界洏锛坮estoreInk 榛樿鍏冲彧鍐?"1"銆乸ptAutoSave 榛樿寮€鍙啓 "0"銆?            // historyDays 榛樿姘镐箙鍐欐垚 null = 鍒犻」锛夈€?            case Row.RestoreInk:
                _host.SetPref("restoreInk", _host.GetPref("restoreInk") == "1" ? null : "1");
                break;
            case Row.PptAutoSave:
                _host.SetPref("pptAutoSave", _host.GetPref("pptAutoSave") == "0" ? null : "0");
                break;
            case Row.HistoryDays:
                _host.SetPref("historyDays", _host.GetPref("historyDays") switch
                {
                    "90" => "30", "30" => "7", "7" => null, _ => "90",
                });
                break;
        }
        Invalidate();
    }

    /// <summary>鍒囨。銆傚垏瀹岃妫€鏌?褰撳墠宸ュ叿杩樺湪涓嶅湪杩欎竴妗ｉ噷"鈥斺€斾笉鍦ㄥ氨钀藉埌绗斻€?/summary>
    private void SetProfile(Profile p)
    {
        _profile = p;
        if (PosOf(CellForTool(_host.State.Tool)) < 0)
            _host.Commands.SetTool(Tool.Pen);
        _hover = -1;
        _press = -1;
        SavePrefs();
        Invalidate();
    }

    /// <summary>閽変綇 / 鍙栨秷閽変綇銆傜瑪銆佹鐨€併€屾洿澶氥€嶆槸瀹夊叏椤癸紙鍙栨秷浜嗗氨娌℃硶鐢級锛屼笉璁稿姩銆?/summary>
    private void TogglePin(int cell)
    {
        // 涓嬫爣瓒婄晫鍦ㄨ繖閲岀洿鎺ユ尅浣忥細鍛戒腑缂栧彿涓€鏃︿覆娈碉紙鍘嗗彶涓婄湡鍙戠敓杩囦竴娆★級锛?        // 杩欓噷鏄渶鍚庝竴閬撻槻绾库€斺€旂晫闈㈢粍浠朵笉璇ュ洜涓轰竴涓笅鏍囨妸灏忓懡涓㈢粰寮曟搸鐨勫紓甯搁樁姊€?        if (cell <= 0 || cell >= Cells.Length) return;
        if (!CanUnpin(cell)) return;
        _pinned[cell] = !_pinned[cell];
        SetProfile(Profile.Custom);
    }

    private bool _moreOpen;
    private readonly Anim _more;          // 0 = 鍏炽€? = 鍏ㄥ紑锛堝吋閬僵閫忔槑搴︼級
    private readonly Anim _moreH;         // 闈㈡澘楂樺害锛氭崲椤垫椂鍔ㄧ敾鍒扮洰鏍囬珮锛堜笉璺筹級
    private MorePage _morePage = MorePage.Home;
    private int _moreHover = MoreHitNone;
    private int _morePress = -1;
    private string _hubHint = "";         // 鍚姩鍣ㄧ姸鎬佽鐨勪复鏃舵彁绀猴紙鐐圭疆鐏版牸/棰勭暀鏍兼椂鍐欙級

    /// <summary>闈㈡澘涓ら〉锛氬惎鍔ㄥ櫒锛堜富椤碉級/ 璁剧疆瀛愰〉銆?*娌℃湁椤电**鈥斺€斿簳鏍忎笌杩斿洖绠ご瀵艰埅銆?/summary>
    private enum MorePage { Home = 0, Settings = 1 }

    // 灏哄害锛堥€昏緫鍍忕礌锛夈€倀ile 76銆佸簳鏍忓悓娆撅紱闈㈡澘瀹?= min(640, 55% 宸ヤ綔瀹?銆?    // 瑙勭煩瑙併€婅鑼?鍔熻兘鍗?md銆嬄т簩/搂3.4銆?    private const float MorePad = 20f;
    private const float MoreHeaderH = 44f;
    private const float MoreGroupGap = 14f;
    private const float MoreGroupHeadH = 26f;
    private const float MoreTile = 76f;
    private const float MoreTileGap = 10f;
    private const float MoreStatusH = 20f;
    private const float MoreProfileH = 34f;
    private const float MoreChipH = 40f;
    private const float MoreChipGap = 8f;
    private const int MoreChipCols = 6;
    private const float MoreRowH = 48f;
    /// <summary>
    /// 璁剧疆椤典笁鍒楀悇鍑犺銆?*鍔犺鏃朵笁澶勪竴璧锋敼**锛氳繖閲岀殑鏁板瓧銆乣Rows` 琛ㄣ€乣MoreRowRect`銆?    /// 2026-10-02 鍔犮€屾偓鍋滄彁绀恒€嶉偅涓€琛屾椂灏辨槸杩欎箞鏀圭殑锛堝師鏉ヨ繖涓や釜鏁板啓姝诲湪
    /// `SetWriteHeadRect` 鍜?`MoreLowerH` 閲岋紝涓ゅ鍚勫啓涓€閬嶈繜鏃╂紡涓€澶勶級銆?    /// 2026-10-09锛氫袱鍒楋紙宸﹀垪鍫?澶栬锛嬩功鍐?锛夆啋 **涓夊垪骞舵帓**锛堝瑙?/ 涔﹀啓 / 澧ㄨ抗 鍚勪竴鍒楋級鈥斺€?    /// 鐢ㄦ埛璇?璁剧疆鐜板湪澶暱浜?銆傚師鏉ュ乏鍒楀爢 9 琛岋紙缁勫ご 26 + 9脳48 + 8 鈮?476锛夛紝涓夊垪鍚庢渶楂?5 琛屻€?    /// </summary>
    private const int LookRowCount = 5;    // 澶栬锛氭繁鑹蹭富棰?/ 璐磋竟闅愯棌 / 鑹插甫甯稿紑 / 鎮仠鎻愮ず / 鐣岄潰澶у皬
    private const int WriteRowCount = 3;   // 涔﹀啓锛氬仠椤垮彉鍥惧舰 / 鍘嬫劅绮楃粏 / 澧ㄨ抗棰勬祴锛堛€岄娴嬫椂闀裤€?026-10-15 闅忔绉掕皟鑺傛挙闄わ紱绮剧粏绗旇抗銆佽Е鎽告墜鍔?2026-10-09 鎾ゅ紑鍏炽€佹敼甯稿紑锛?    private const int InkRowCount = 4;     // 澧ㄨ抗锛氭箍澧ㄦ。浣?/ 鑷姩鎭㈠涓婃鏉夸功 / PPT 澧ㄨ抗榛樿鑷姩淇濆瓨 / 鍘嗗彶娓呯悊
    private const float MoreColumnGap = 16f;
    private const float MoreSwitchW = 44f;
    private const float MoreSwitchH = 26f;
    private const float MoreCloseSize = 44f;
    private const float MoreProfileGap = 8f;
    private const float MoreChipTopGap = 10f;

    // 鍛戒腑缂栧彿锛氫竴鏁村潡鐢?*涓€涓暣鏁?*缂栫爜锛涚粯鍒?鍛戒腑/鎵ц閮借鍚屼竴涓彿銆?    private const int MoreHitNone = -1;
    private const int MoreHitInside = 0;      // 闈㈡澘绌虹櫧锛氬悆鎺夛紝浣嗕笉鍏抽潰鏉?    private const int MoreHitClose = 1;
    private const int MoreHitBack = 2;        // 璁剧疆瀛愰〉杩斿洖鍚姩鍣?    private const int MoreHitProfile = 20;    // 20..22锛堟。浣嶏級
    private const int MoreHitChip = 30;       // 30+cell锛堥拤浣忓鏍硷紱鍙湁鑷畾涔夋。鏄剧ず锛?    private const int MoreHitRow = 60;        // 60+Rows 涓嬫爣锛堣缃瓙椤电殑琛岋級
    private const int MoreHitTile = 140;      // 140+鍚姩鍣ㄦ牸瀛愶紙0..5锛氳鏃?鐐瑰悕/鍒嗙粍 淇濆瓨/鎵撳紑/鍥炴斁锛?    private const int MoreHitBottom = 150;    // 150+搴曟爮锛?..3锛氳缃?妫€鏌ユ洿鏂?閲嶅惎/閫€鍑猴級

    /// <summary>闈㈡澘瀹斤細**min(640, 55% 宸ヤ綔瀹?**锛?026-10-02 鎷嶆澘鐨勫垎杈ㄧ巼瑙勮寖鍖?B锛夈€?/summary>
    private float MoreWidth()
    {
        float workW = _work.MaxX - _work.MinX;
        return Math.Clamp(workW * 0.55f, 380f, 640f);
    }

    private float MoreTargetH() => _morePage == MorePage.Home ? MoreHomeH() : MoreSettingsH();

    /// <summary>鍚姩鍣ㄩ珮锛氫袱缁勶紙璇惧爞/澧ㄨ抗锛夊悇鑻ュ共琛?tile 锛?鐘舵€佽 锛?搴曟爮涓€琛屻€?    /// 鈿?姣忕粍鐨勮鏁拌鐢?<see cref="HubRows"/>锛堝搴﹀喅瀹氾級鐪熷疄绠椻€斺€旀棫寮忓瓙鎸?    /// `(澶撮珮+鏍奸珮+缁勮窛)` 浼扮殑銆佹紡浜嗘瘡琛岀殑**鏍艰窛**锛圡oreTileGap锛夛紝闈㈡澘姣斿唴瀹圭煯
    /// 2脳鏍艰窛锛屽簳琛屾濂借创姝诲湪闈㈡澘搴曡竟锛?026-10-09 鐢ㄦ埛鎶?鏈€涓嬮潰涓€琛岃创鐫€搴曡竟銆?    /// 涓嬭竟妗嗚鍒?锛屽嚭鍥鹃噺鍑轰笅杈硅窛=0锛夈€?/summary>
    private float MoreHomeH()
        => MorePad * 2 + MoreHeaderH + 8
           + MoreGroupHeadH + HubRows(0) * (MoreTile + MoreTileGap) + MoreGroupGap
           + MoreGroupHeadH + HubRows(1) * (MoreTile + MoreTileGap) + MoreGroupGap
           + MoreStatusH + 8 + MoreTile;

    /// <summary>璁剧疆瀛愰〉楂橈細宸ュ叿鏉＄粍 锛?涓嬪崐涓夋爮锛堝瑙?/ 涔﹀啓 / 澧ㄨ抗 骞舵帓锛夈€?/summary>
    private float MoreSettingsH()
        => MorePad * 2 + MoreHeaderH + 8
           + MoreGroupHeadH + MoreProfileH + MoreChipTopGap
           + (ChipsShown ? 2 * MoreChipH + MoreChipGap : 42f)
           + MoreGroupGap + MoreLowerH;

    /// <summary>涓嬪崐鍖洪珮搴?= 鏈€楂橀偅涓€鍒楋紙涓夊垪骞舵帓锛屼笁涓粍澶撮兘鍦ㄥ悓涓€琛岋級銆?/summary>
    private static float MoreLowerH
        => MoreGroupHeadH + MathF.Max(LookRowCount, MathF.Max(WriteRowCount, InkRowCount)) * MoreRowH;

    /// <summary>閽変綇瀹牸**鍙湪鑷畾涔夋。**鏄剧ず锛堢敤鎴?2026-10-02锛?鑰佹槸鍗犲湴鏂?锛夈€?/summary>
    private bool ChipsShown => _profile == Profile.Custom;

    /// <summary>闈㈡澘鐭╁舰锛氬伐浣滃尯姝ｄ腑銆佸す杩涘睆骞曪紱楂樺害鐢?`_moreH` 鍔ㄧ敾锛堟崲椤典笉璺筹級銆?/summary>
    private RectF MoreRect()
    {
        float h = MathF.Min(_moreH.Value <= 0f ? MoreTargetH() : _moreH.Value,
                            MathF.Max(200f, (_work.MaxY - _work.MinY) - 16f));
        float w = MathF.Min(MoreWidth(), MathF.Max(240f, (_work.MaxX - _work.MinX) - 16f));
        float cx = (_work.MinX + _work.MaxX) * 0.5f;
        float cy = (_work.MinY + _work.MaxY) * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };
        const float m = 8f;
        if (r.MinX < _screen.MinX + m) { r.MaxX += _screen.MinX + m - r.MinX; r.MinX = _screen.MinX + m; }
        if (r.MaxX > _screen.MaxX - m) { r.MinX -= r.MaxX - (_screen.MaxX - m); r.MaxX = _screen.MaxX - m; }
        if (r.MinY < _screen.MinY + m) { r.MaxY += _screen.MinY + m - r.MinY; r.MinY = _screen.MinY + m; }
        if (r.MaxY > _screen.MaxY - m) { r.MinY -= r.MaxY - (_screen.MaxY - m); r.MaxY = _screen.MaxY - m; }
        return r;
    }

    private float MoreContentW() => MoreRect().MaxX - MoreRect().MinX - MorePad * 2f;
    private float MoreLeftX() => MoreRect().MinX + MorePad;

    private RectF MoreHeaderRect() => new()
    {
        MinX = MoreLeftX(), MinY = MoreRect().MinY + MorePad,
        MaxX = MoreLeftX() + MoreContentW(), MaxY = MoreRect().MinY + MorePad + MoreHeaderH,
    };

    private RectF MoreCloseRect() => new()
    {
        MinX = MoreHeaderRect().MaxX - MoreCloseSize, MinY = MoreHeaderRect().MinY,
        MaxX = MoreHeaderRect().MaxX, MaxY = MoreHeaderRect().MinY + MoreCloseSize,
    };

    private RectF MoreBackRect() => new()
    {
        MinX = MoreHeaderRect().MinX, MinY = MoreHeaderRect().MinY,
        MaxX = MoreHeaderRect().MinX + MoreCloseSize, MaxY = MoreHeaderRect().MinY + MoreCloseSize,
    };

    // ---- 鍚姩鍣ㄥ嚑浣?----------------------------------------------------------

    /// <summary>绗?0 缁勶紙璇惧爞锛夌殑缁勫ご y銆?/summary>
    private float HubSec0Y() => MoreRect().MinY + MorePad + MoreHeaderH + 8f;

    private int HubRows(int section)
    {
        int count = HubCount(section);
        return Math.Max(1, (count + TileCols() - 1) / TileCols());
    }

    private int HubCount(int section) => HubCounts[Math.Clamp(section, 0, HubCounts.Length - 1)];

    /// <summary>
    /// 姣忎釜鍒嗙粍鐨勬牸瀛愭暟锛氳鍫?3 涓紙璁℃椂 / 鐐瑰悕 / 闅忔満涓€浜猴級锛涘ⅷ杩?**7 涓?*
    /// 锛堜繚瀛樺ⅷ杩?/ 鎵撳紑澧ㄨ抗 / 澧ㄨ抗鍥炴斁 / 淇濆瓨鍥剧墖 / 鎵撳紑鏂囨。 / 鍏抽棴鏂囨。 /
    /// **鍘嗗彶鏉夸功**锛屾渶鍚庝竴涓槸 2026-10-14 鍔犵殑锛氭壘鍥炰互鍓嶅啓杩囧張娓呮帀/鍏虫帀鐨勬澘涔︼級銆?    /// 鏍煎瓙缂栧彿锛坄HubTileCode`锛変粠杩欓噷绠楀嚭鏉ワ紝**涓嶅啓姝?3**鈥斺€斿姞鏍煎瓙涓嶄細鍐嶄覆娈点€?    /// </summary>
    private static readonly int[] HubCounts = { 3, 7 };

    /// <summary>绗?section 缁勭殑缁勫ご鐭╁舰銆?/summary>
    private RectF HubHeadRect(int section)
    {
        float y = HubSec0Y();
        for (int s = 0; s < section; s++)
            y += MoreGroupHeadH + HubRows(s) * (MoreTile + MoreTileGap) + MoreGroupGap;
        return new RectF { MinX = MoreLeftX(), MinY = y, MaxX = MoreLeftX() + MoreContentW(), MaxY = y + MoreGroupHeadH };
    }

    private RectF HubTileRect(int section, int index)
    {
        var head = HubHeadRect(section);
        int cols = TileCols();
        int col = index % cols, row = index / cols;
        float x = MoreLeftX() + col * (MoreTile + MoreTileGap);
        float y = head.MaxY + row * (MoreTile + MoreTileGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + MoreTile, MaxY = y + MoreTile };
    }

    /// <summary>绗?section 缁勭 index 涓牸瀛愮殑鍏ㄥ眬缂栧彿锛堜粠 <see cref="HubCounts"/> 绱姞锛?    /// 鍔犱竴缁?鍔犱竴鏍奸兘涓嶇敤鏀硅繖閲岋級銆?/summary>
    private int HubTileCode(int section, int index)
    {
        int code = 0;
        for (int s = 0; s < section && s < HubCounts.Length; s++) code += HubCounts[s];
        return code + index;
    }

    /// <summary>褰撳墠宸ヤ綔鍖哄搴︿笅鑳芥帓鍑犲垪鏍煎瓙銆?*鍙敤宸ヤ綔鍖?闈㈡澘瀹界畻**锛屼笉缁忚繃
    /// `MoreRect()`鈥斺€旈偅閲屾湁寮€鍚堝姩鐢讳腑鐨勯珮搴﹀€硷紝甯冨眬璁＄畻锛圡oreHomeH鈫扝ubRows锛夌瀹?    /// 浼氶€掑綊銆傚紡瀛愪笌 MoreRect 鐨勫疄闄呭搴﹀悓婧愶細min(MoreWidth, max(240, 宸ヤ綔瀹?16))銆?</summary>
    private int TileCols()
    {
        float w = MathF.Min(MoreWidth(), MathF.Max(240f, (_work.MaxX - _work.MinX) - 16f));
        float contentW = w - MorePad * 2f;
        return Math.Clamp((int)((contentW + MoreTileGap) / (MoreTile + MoreTileGap)), 3, 6);
    }

    private RectF HubStatusRect() => new()
    {
        MinX = MoreLeftX(), MaxY = HubBottomRect(0).MinY - 8f,
        MaxX = MoreLeftX() + MoreContentW(), MinY = HubBottomRect(0).MinY - 8f - MoreStatusH,
    };

    private RectF HubBottomRect(int i)
    {
        var head = HubHeadRect(1);
        float y = head.MaxY + HubRows(1) * (MoreTile + MoreTileGap) + MoreGroupGap + MoreStatusH + 8f;
        float x = MoreLeftX() + i * (MoreTile + MoreTileGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + MoreTile, MaxY = y + MoreTile };
    }

    // ---- 璁剧疆瀛愰〉鍑犱綍 --------------------------------------------------------

    private float SetToolHeadY() => MoreRect().MinY + MorePad + MoreHeaderH + 8f;

    private RectF MoreProfileRect(int i)
    {
        float w = (MoreContentW() - MoreProfileGap * 2f) / 3f;
        float x = MoreLeftX() + i * (w + MoreProfileGap);
        float y = SetToolHeadY() + MoreGroupHeadH;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + MoreProfileH };
    }

    private RectF MoreChipRect(int cell)
    {
        int idx = cell - 1;
        float w = (MoreContentW() - (MoreChipCols - 1) * MoreChipGap) / MoreChipCols;
        int col = idx % MoreChipCols, row = idx / MoreChipCols;
        float y = MoreProfileRect(0).MaxY + MoreChipTopGap + row * (MoreChipH + MoreChipGap);
        float x = MoreLeftX() + col * (w + MoreChipGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + MoreChipH };
    }

    private float SetLowerTop()
        => MoreProfileRect(0).MaxY + MoreChipTopGap
           + (ChipsShown ? 2 * MoreChipH + MoreChipGap : 42f) + MoreGroupGap;

    /// <summary>涓夊垪骞舵帓鏃舵瘡鍒楃殑瀹斤紙鍐呭瀹藉噺涓ら亾鍒楄窛锛屽啀涓夌瓑鍒嗭級銆?/summary>
    private float SetColW() => MathF.Max(110f, (MoreContentW() - MoreColumnGap * 2f) / 3f);
    private float SetColX(SetColKind col) => MoreLeftX() + (int)col * (SetColW() + MoreColumnGap);

    private RectF SetColHeadRect(SetColKind col) => new()
    {
        MinX = SetColX(col), MinY = SetLowerTop(),
        MaxX = SetColX(col) + SetColW(), MaxY = SetLowerTop() + MoreGroupHeadH,
    };
    private RectF SetLookHeadRect() => SetColHeadRect(SetColKind.Look);
    private RectF SetWriteHeadRect() => SetColHeadRect(SetColKind.Write);
    private RectF SetInkHeadRect() => SetColHeadRect(SetColKind.Ink);

    /// <summary>
    /// Rows 鍦ㄨ缃〉涓夊垪閲岀殑浣嶇疆锛氬瑙傦紙4锛? 涔﹀啓锛?锛? 澧ㄨ抗锛?锛?*骞舵帓**銆?    ///
    /// 2026-10-02 鍔犮€屾偓鍋滄彁绀恒€嶉偅涓€琛屾椂锛岃鏁?*浠庡父閲忔潵**锛坄LookRowCount` /
    /// `WriteRowCount`锛夛紝涓嶅啀鍦?`SetWriteHeadRect` 閲屽啓姝?`2 * MoreRowH`鈥斺€?    /// 鍔犺蹇樻敼涓€澶勶紝鍚庨潰鐨勭粍澶村氨浼氬帇鍒颁笂涓€琛屼笂锛堜粨搴撳湪鎶藉眽鏃朵唬韪╄繃"鍐欐涓嬫爣"鐨勫潙锛夈€?    /// 2026-10-09锛氫笁鍒楀苟鎺掆€斺€旇楂樹笉鍐嶅彔涓ょ粍锛屽彧鐪嬫渶楂樼殑閭ｄ竴鍒楋紙`InkRowCount` 涔熷叆甯搁噺锛夈€?    /// </summary>
    private RectF MoreRowRect(int i) => Rows[i].Kind switch
    {
        Row.DarkTheme => SetColRow(SetColKind.Look, 0),
        Row.AutoHide => SetColRow(SetColKind.Look, 1),
        Row.RailPin => SetColRow(SetColKind.Look, 2),
        Row.Tooltip => SetColRow(SetColKind.Look, 3),
        Row.UiSize => SetColRow(SetColKind.Look, 4),
        Row.DwellShape => SetColRow(SetColKind.Write, 0),
        Row.Pressure => SetColRow(SetColKind.Write, 1),
        Row.Predict => SetColRow(SetColKind.Write, 2),
        Row.WetMode => SetColRow(SetColKind.Ink, 0),      // 2026-10-13 鍔狅細澧ㄨ抗鍒楃涓€琛岋紙鏈€濂芥壘锛?        Row.RestoreInk => SetColRow(SetColKind.Ink, 1),
        Row.PptAutoSave => SetColRow(SetColKind.Ink, 2),
        Row.HistoryDays => SetColRow(SetColKind.Ink, 3),
        _ => SetColRow(SetColKind.Ink, 3),
    };

    /// <summary>璁剧疆椤典笅鍗婄殑涓夊垪锛氬瑙?/ 涔﹀啓 / 澧ㄨ抗锛堝苟鎺掞紱搴忓彿 = 鍒楀簭锛宍SetColX` 鐢ㄥ畠锛夈€?/summary>
    private enum SetColKind { Look = 0, Write = 1, Ink = 2 }

    private RectF SetColRow(SetColKind col, int idx)
    {
        float headBottom = SetColHeadRect(col).MaxY;   // 涓夊垪缁勫ご鍚岄珮 鈫?琛岃捣鐐逛篃涓€鏍?        float x = SetColX(col);
        float y = headBottom + idx * MoreRowH;
        return new RectF { MinX = x, MinY = y, MaxX = x + SetColW(), MaxY = y + MoreRowH };
    }

    private RectF MoreSwitchRect(int i) => new()
    {
        MinX = MoreRowRect(i).MaxX - MoreSwitchW - 4f,
        MinY = (MoreRowRect(i).MinY + MoreRowRect(i).MaxY) * 0.5f - MoreSwitchH * 0.5f,
        MaxX = MoreRowRect(i).MaxX - 4f,
        MaxY = (MoreRowRect(i).MinY + MoreRowRect(i).MaxY) * 0.5f + MoreSwitchH * 0.5f,
    };

    /// <summary>鍘嗗彶娓呯悊閭ｄ竴琛屽彸渚х殑鍊笺€?/summary>
    private string HistoryDaysName(string pref) => pref switch
    {
        "90" => "90 澶?, "30" => "30 澶?, "7" => "7 澶?, _ => "姘镐箙",
    };

    /// <summary>
    /// 銆屾箍澧ㄦ。浣嶃€嶉偅涓€琛屽彸渚х殑鍊硷紙鐭悕锛涘洓妗ｇ殑瀹屾暣瑙ｉ噴鍦ㄦ偓鍋滄彁绀洪噷锛夈€?    /// 鍜屽紩鎿庣殑 `WetModeName` **鍒绘剰涓嶅悓**锛氳繖閲岃濉炶繘绐勭獎鐨勪竴鍒楋紝鐢ㄧ煭鍚嶃€?    /// </summary>
    private static string WetModeShortName(int m) => m switch
    {
        1 => "1 绯荤粺杞ㄨ抗",
        2 => "2 搴旂敤婀垮ⅷ灞?,
        3 => "3 鍏ㄥ紑",
        _ => "0 鍘熻矾寰?,
    };

    // ---- 鍛戒腑 ----------------------------------------------------------------

    private int MoreHitAt(float x, float y)
    {
        if (!_moreOpen) return MoreHitNone;
        if (!MoreRect().Contains(x, y)) return MoreHitNone;
        if (MoreCloseRect().Contains(x, y)) return MoreHitClose;
        if (_morePage == MorePage.Settings && MoreBackRect().Contains(x, y)) return MoreHitBack;

        if (_morePage == MorePage.Home)
        {
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < HubCount(s); i++)
                    if (HubTileRect(s, i).Contains(x, y)) return MoreHitTile + HubTileCode(s, i);
            for (int i = 0; i < 4; i++)
                if (HubBottomRect(i).Contains(x, y)) return MoreHitBottom + i;
            return MoreHitInside;
        }

        for (int i = 0; i < 3; i++)
            if (MoreProfileRect(i).Contains(x, y)) return MoreHitProfile + i;
        if (ChipsShown)
            for (int cell = 1; cell < Cells.Length; cell++)
                if (MoreChipRect(cell).Contains(x, y)) return MoreHitChip + cell;
        for (int i = 0; i < Rows.Length; i++)
            if (MoreRowRect(i).Contains(x, y)) return MoreHitRow + i;
        return MoreHitInside;
    }

    // ---- 寮€/鍏充笌鎹㈤〉 ---------------------------------------------------------

    /// <summary>鎵撳紑闈㈡澘锛堝叆鍙ｅ彧鏈変富鏉°€屸€︺€嶉偅涓€鏍硷級銆?/summary>
    private void OpenMore()
    {
        _railHover = false;
        _rail.To(0f, Tokens.RailMs);

        _moreOpen = true;
        _morePage = MorePage.Home;          // 姣忔鎵撳紑閮藉洖鍚姩鍣紙鑲岃倝璁板繂锛氬簳鏍忔案杩滃悓涓€澶勶級
        _moreHover = MoreHitNone;
        _morePress = -1;
        _hubHint = "";
        _more.Jump(0f);
        _more.To(1f, Tokens.MoreOpenMs);
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>鍏抽棴闈㈡澘銆傚姩鐢绘湡闂?QueryBounds 浠嶆姤鏁村睆锛堥伄缃╄娣″嚭鍘伙級銆?/summary>
    private void CloseMore()
    {
        if (!_moreOpen) return;
        _moreOpen = false;
        _moreHover = MoreHitNone;
        _morePress = -1;
        HideTip();              // 闈㈡澘閲岀殑鎻愮ず璺熺潃鏀讹紙瑙︽懜闀挎寜鍊欓€変篃浣滃簾锛?        CancelTipHold();
        _more.To(0f, Tokens.MoreCloseMs);
        Invalidate();
    }

    private void UpdateMoreHeight()
    {
        float target = MoreTargetH();
        if (MathF.Abs(_moreH.Value - target) > 0.5f) _moreH.To(target, 167);
    }

    /// <summary>鐢婚潰鏉匡細閬僵锛堝叏灞忥級锛?鍗＄墖 锛?鎸夐〉鍒囧唴瀹广€?*鏈€鍚庣敾**锛屽帇浣忎竴鍒囥€?/summary>
    private void DrawMorePanel(ID2D1DeviceContext ctx)
    {
        float a = Math.Clamp(_more.Value, 0f, 1f);
        if (a <= 0.003f) return;
        UpdateMoreHeight();

        var scrim = _dark ? Tokens.ScrimDark : Tokens.ScrimLight;
        ctx.FillRectangle(
            new Vortice.RawRectF(_screen.MinX, _screen.MinY, _screen.MaxX, _screen.MaxY),
            Brush(ctx, new Color4(scrim.R, scrim.G, scrim.B, QA(scrim.A * a))));

        var r = MoreRect();
        var saved = ctx.Transform;
        float k = 0.97f + 0.03f * a;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        ctx.Transform = Matrix3x2.CreateTranslation(-c.X, -c.Y)
                      * Matrix3x2.CreateScale(k, k)
                      * Matrix3x2.CreateTranslation(c.X, c.Y)
                      * saved;
        try { DrawMorePanelCore(ctx); }
        finally { ctx.Transform = saved; }
    }

    private void DrawMorePanelCore(ID2D1DeviceContext ctx)
    {
        var r = MoreRect();
        var card = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 20f, 20f);
        ctx.FillRoundedRectangle(card, Brush(ctx, _dark ? Tokens.PanelDark : Tokens.PanelLight));
        ctx.DrawRoundedRectangle(card, Brush(ctx, BorderCol), 1f);

        // 鏍囬锛堝惎鍔ㄥ櫒銆屾洿澶氥€? 瀛愰〉銆岃缃€嶏級锛?杩斿洖 锛?鍏抽棴
        string title = _morePage == MorePage.Home ? "鏇村" : "璁剧疆";
        _widgets.Text(ctx, title,
                      new RectF { MinX = MoreHeaderRect().MinX + (_morePage == MorePage.Home ? 0f : MoreCloseSize + 8f),
                                  MinY = MoreHeaderRect().MinY, MaxX = MoreHeaderRect().MaxX, MaxY = MoreHeaderRect().MaxY },
                      17f, Brush(ctx, InkCol), center: false);
        if (_morePage == MorePage.Settings)
        {
            _widgets.Text(ctx, "鈥?, MoreBackRect(), 24f,
                          Brush(ctx, _moreHover == MoreHitBack ? InkCol : MutedCol));
        }
        _widgets.Text(ctx, "鉁?, MoreCloseRect(), 15f,
                      Brush(ctx, _moreHover == MoreHitClose ? InkCol : MutedCol));

        if (_morePage == MorePage.Home) DrawMoreHome(ctx);
        else DrawMoreSettings(ctx);
    }

    // ---- 鍚姩鍣ㄧ粯鍒?----------------------------------------------------------

    /// <summary>鍚姩鍣ㄦ牸瀛愭€绘暟锛堝悇鍒嗙粍鐩稿姞锛涖€屽姞鏍煎瓙蹇樹簡鍒銆嶇敤杩欎竴鏉″厹浣忥級銆?/summary>
    private static int HubTileTotal
    {
        get
        {
            int n = 0;
            foreach (int c in HubCounts) n += c;
            return n;
        }
    }

    /// <summary>
    /// 鍚姩鍣ㄦ牸瀛愮殑琛細`Hint` **涓嶅啀鐢诲湪鏍煎瓙閲?*锛?026-10-02 绗簩鎵癸細鎼繘鎮仠/闀挎寜鎻愮ず锛夛紝
    /// 浣嗗畠鏄彁绀虹殑**鍞竴鏂囨鏉ユ簮**鈥斺€旂粯鍒躲€佸懡涓€佹彁绀洪兘璇昏繖涓€浠姐€?    /// 鏍煎瓙缂栧彿锛氳鍫?0..2锛堣鏃?/ 鐐瑰悕 / 闅忔満涓€浜猴級銆佸ⅷ杩?3..6锛堜繚瀛樺ⅷ杩?/ 鎵撳紑澧ㄨ抗 /
    /// 澧ㄨ抗鍥炴斁 / 淇濆瓨鍥剧墖锛夈€?    /// </summary>
    private (string Label, string Hint, bool Enabled, bool Danger) HubTileInfo(int code) => code switch
    {
        0 => ("璁℃椂鍣?, "鍊掕鏃?/ 姝ｈ鏃?/ 绉掕〃锛涘崱鐗囧彲鎷栧姩銆佸弻鍑绘斁澶?, true, false),
        1 => ("鐐瑰悕", "鍏ㄥ悕鍗曪紝鎶借繃鐨勪笉閲嶅锛涜寖鍥村彲璁?, true, false),
        2 => ("闅忔満涓€浜?, "鑷姩鎶戒竴涓紝1.5 绉掕嚜鍔ㄥ叧", true, false),
        3 => ("淇濆瓨澧ㄨ抗", "鎶婃暣浠芥澘涔﹀瓨鎴?.inkb 鏂囦欢", _host != null && !_host.State.PptMode && _host.State.StrokeCount > 0, false),
        4 => ("鎵撳紑澧ㄨ抗", "鎵撳紑涓€浠?.inkb锛屾浛鎹㈠綋鍓嶆澘涔︼紙鍏堝浠斤級", _host != null && !_host.State.PptMode, false),
        5 => ("澧ㄨ抗鍥炴斁", "鎶婅繖涓€灞忕殑鏉夸功閲嶆紨涓€閬嶏紙鍙锛屼笉鍔ㄦ澘涔︼級", _host != null && (_host.State.ReplayActive || _host.State.StrokeCount > 0), false),
        6 => ("淇濆瓨鍥剧墖", "鎶婃暣鍧楁澘涔﹀瓨鎴愬浘鐗囷紙png / jpg锛屽ソ鍙戝井淇★級", _host != null && !_host.State.PptMode && _host.State.StrokeCount > 0, false),
        7 => ("鎵撳紑鏂囨。", "鍥剧墖/PDF 閾烘垚椤垫壒娉紱PPT 璧?Office/WPS 鐩存帴杩涙斁鏄?, _host != null && !_host.State.PptMode, false),
        8 => ("鍏抽棴鏂囨。",
              _host != null && _host.State.DocOpen
                  ? $"鍏虫帀銆妠_host.State.DocInfo}銆嬶紱鎵规敞鐣欏湪鐢诲竷涓?
                  : "鍏虫帀褰撳墠鏂囨。锛涙壒娉ㄧ暀鍦ㄧ敾甯冧笂",
              _host != null && _host.State.DocOpen, false),
        // 鍘嗗彶鏉夸功锛?026-10-14 鍔狅級锛氬紩鎿庤嚜鍔ㄧ暀鐨勬澘涔︾敾甯冨揩鐓э紙涓嶅惈鎴浘锛夛紝杩欓噷鏄壘鍥炲叆鍙ｃ€?        9 => ("鍘嗗彶鏉夸功",
              "鎵惧洖浠ュ墠鍐欏湪鏉夸功 / 妗岄潰涓婄殑澧ㄨ抗锛堣嚜鍔ㄧ暀瀛橈紝鍋滄墜鏃跺啓锛涗笉鍚埅鍥撅級",
              _host != null && !_host.State.PptMode && _host.State.HistoryCount > 0, false),
        _ => ("", "", false, false),
    };

    /// <summary>
    /// 搴曟爮鍥哄畾鍥涙牸鐨勫敮涓€涓€浠借〃锛?026-10-02 绗簩鎵癸細灏忓瓧鎼繘鎻愮ず锛屾牸瀛愪笂鍙暀鏍囬锛夈€?    /// 缁樺埗銆佸懡涓€佹彁绀洪兘璇诲畠銆?    /// </summary>
    private static readonly (string Label, string Hint, bool Danger)[] MoreBottomTiles =
    {
        ("璁剧疆", "澶栬 / 涔﹀啓 / 澧ㄨ抗 / 宸ュ叿鏉★紝閮藉湪閲岄潰", false),
        ("妫€鏌ユ洿鏂?, $"褰撳墠 v{ProductInfo.Version}锛涙湁鏂扮増鏈細鎻愮ず锛屼篃鍙互鐩存帴搴旂敤鏇存柊", false),
        ("閲嶅惎杞欢", "鍍忕數鑴戦噸鍚細涓嶆仮澶嶆湰娆℃澘涔︼紙鑷姩瀛樻。杩樺湪锛?, false),
        ("閫€鍑?, "鍏虫帀鎵规敞锛堜細鍏堟妸閿綅钀界洏锛?, true),
    };

    /// <summary>鐢讳竴涓惎鍔ㄥ櫒鏍煎瓙锛?*鍙湁鏍囬**锛堝皬瀛?2026-10-02 鎼繘浜嗘彁绀猴級銆?    /// 鎮仠/闀挎寜鐢?FullUi 鐨勬彁绀虹郴缁熻ˉ銆?/summary>
    private void DrawHubTile(ID2D1DeviceContext ctx, in RectF t, string label, bool enabled, bool danger, int hitCode)
    {
        var rr = new RoundedRectangle(new Vortice.RawRectF(t.MinX, t.MinY, t.MaxX, t.MaxY), 12f, 12f);
        if (_moreHover == hitCode) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
        Color4 ink = !enabled ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.35f)
                   : danger ? new Color4(0.85f, 0.22f, 0.22f, 1f)
                   : InkCol;
        _widgets.Text(ctx, label, t, 14f, Brush(ctx, ink));
    }

    private void DrawMoreHome(ID2D1DeviceContext ctx)
    {
        string[] heads = { "璇惧爞", "澧ㄨ抗" };
        for (int s = 0; s < 2; s++)
        {
            DrawMoreGroupHead(ctx, HubHeadRect(s), heads[s]);
            for (int i = 0; i < HubCount(s); i++)
            {
                int code = HubTileCode(s, i);
                var info = HubTileInfo(code);
                DrawHubTile(ctx, HubTileRect(s, i), info.Label, info.Enabled, info.Danger,
                            MoreHitTile + code);
            }
        }

        // 鐘舵€佽锛氬懡浠ゅ弽棣堬紙淇濆瓨/鎵撳紑/鏇存柊锛夆€斺€斾骇鍝侀噷涓嶅脊绐楋紝缁撴灉閮借惤鍦ㄨ繖鍎裤€?        // 娌℃湁瑕佽鐨勫氨鏄剧ず**鐗堟湰鍙?*锛堢敤鎴?2026-10-08锛?杞欢閲屾渶濂借兘鐪嬪埌鐗堟湰鍙?锛夈€?        var st = _host.State;
        string status = !string.IsNullOrEmpty(_hubHint) ? _hubHint
                      : !string.IsNullOrEmpty(st.InkStatus) ? st.InkStatus
                      : st.UpdateStage != UpdateStage.Idle && !string.IsNullOrEmpty(st.UpdateText) ? st.UpdateText
                      : $"v{ProductInfo.Version}";
        var sr = HubStatusRect();
        if (status.Length != 0)
            _widgets.Text(ctx, status, sr, 11.5f, Brush(ctx, MutedCol), center: false);

        // 搴曟爮鍥哄畾鍥涙牸锛堣〃鍦ㄤ笂闈紝缁樺埗/鍛戒腑/鎻愮ず鍏辩敤锛?        for (int i = 0; i < MoreBottomTiles.Length; i++)
            DrawHubTile(ctx, HubBottomRect(i), MoreBottomTiles[i].Label, true, MoreBottomTiles[i].Danger,
                        MoreHitBottom + i);
    }

    private void DrawMoreGroupHead(ID2D1DeviceContext ctx, RectF r, string label)
        => _widgets.Text(ctx, label, r, 12.5f, Brush(ctx, MutedCol), center: false);

    // ---- 璁剧疆瀛愰〉缁樺埗 --------------------------------------------------------

    private void DrawMoreSettings(ID2D1DeviceContext ctx)
    {
        DrawMoreGroupHead(ctx, new RectF { MinX = MoreLeftX(), MinY = SetToolHeadY(),
                                           MaxX = MoreLeftX() + MoreContentW(), MaxY = SetToolHeadY() + MoreGroupHeadH },
                          "宸ュ叿鏉?);
        for (int i = 0; i < 3; i++)
        {
            var s = MoreProfileRect(i);
            bool active = ProfileIndex() == i;
            var rr = new RoundedRectangle(new Vortice.RawRectF(s.MinX, s.MinY, s.MaxX, s.MaxY), 8f, 8f);
            if (active) ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
            else if (_moreHover == MoreHitProfile + i) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);
            _widgets.Text(ctx, ProfileIndex() == i ? ProfileName(i) : ProfileName(i), s, 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        }
        if (ChipsShown)
        {
            for (int cell = 1; cell < Cells.Length; cell++)
            {
                var c = MoreChipRect(cell);
                bool pinned = _pinned[cell];
                var rr = new RoundedRectangle(new Vortice.RawRectF(c.MinX, c.MinY, c.MaxX, c.MaxY), 8f, 8f);
                if (_moreHover == MoreHitChip + cell) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, pinned ? Tokens.Accent : BorderCol), 1f);
                DrawCellIcon(ctx, cell, c, Tokens.Icon,
                             new Color4(InkCol.R, InkCol.G, InkCol.B, pinned ? 1f : 0.35f),
                             pinned, _host.State);
                if (!pinned)
                    _widgets.Text(ctx, "+", new RectF { MinX = c.MaxX - 18f, MinY = c.MinY + 2f, MaxX = c.MaxX - 3f, MaxY = c.MinY + 18f },
                                  12f, Brush(ctx, MutedCol));
            }
        }
        else
        {
            _widgets.Text(ctx, "鎸戝伐鍏凤細鍒囧埌銆岃嚜瀹氫箟銆嶆。锛岃繖閲屽氨鑳介拤 / 鍙栨秷閽?,
                          new RectF { MinX = MoreLeftX(), MinY = MoreProfileRect(0).MaxY + MoreChipTopGap,
                                      MaxX = MoreLeftX() + MoreContentW(),
                                      MaxY = MoreProfileRect(0).MaxY + MoreChipTopGap + 32f },
                          11.5f, Brush(ctx, MutedCol), center: false);
        }

        DrawMoreGroupHead(ctx, SetLookHeadRect(), "澶栬");
        DrawMoreGroupHead(ctx, SetWriteHeadRect(), "涔﹀啓");
        DrawMoreGroupHead(ctx, SetInkHeadRect(), "澧ㄨ抗");
        for (int i = 0; i < Rows.Length; i++) DrawMoreRow(ctx, i);
    }

    private void DrawMoreRow(ID2D1DeviceContext ctx, int i)
    {
        var r = MoreRowRect(i);
        if (_moreHover == MoreHitRow + i)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f),
                Brush(ctx, HoverCol));

        Color4 ink = Rows[i].Dangerous ? new Color4(0.85f, 0.22f, 0.22f, 1f) : InkCol;

        // 鍙充晶鐨?鍊?锛堝巻鍙叉竻鐞?/ 鐣岄潰澶у皬锛夛細**鍜屾爣绛惧悓涓€琛?*銆佹寜瀹炴祴瀹藉害璐村彸銆?        // 鈿?鐢诲畠蹇呴』 `center: false`鈥斺€擶idgets.Text 鐨勫眳涓弬鏁版槸**涓よ酱閮藉眳涓?*锛?        //   鍊煎瀭鐩翠篃灞呬腑銆佽€屾爣绛炬槸椤堕儴瀵归綈锛岀湅璧锋潵灏卞儚"鎺夊埌绗簩琛?
        //   锛堢敤鎴?2026-10-10 鎸囧嚭锛岄『鎵嬫妸"鍘嗗彶娓呯悊"鐨?姘镐箙"涔熷綊浣嶏級銆?        string value = Rows[i].Kind switch
        {
            Row.HistoryDays => HistoryDaysName(_host.GetPref("historyDays")),
            Row.UiSize => UiScalePresets.Name(_host.UiScale),
            Row.WetMode => WetModeShortName(_host.State.WetMode),
            _ => null,
        };
        float valueW = value != null ? _widgets.Measure(value, 12.5f) : 0f;

        var label = new RectF
        {
            MinX = r.MinX + 4f, MinY = r.MinY,
            MaxX = r.MaxX - (IsToggleRow(i) ? MoreSwitchW + 10f
                             : value != null ? valueW + 10f : 4f),
            MaxY = r.MaxY,
        };
        // 2026-10-02 绗簩鎵癸細琛屼笅閭ｈ 11px 灏忕伆瀛楋紙Hint锛?*涓嶅啀鐢?*鈥斺€旀惉杩涙偓鍋?闀挎寜鎻愮ず锛?        // 琛屼笂鍙暀鏍囩锛屾暣琛屼竴鏉＄嚎锛屾竻鐖姐€侶int 浠嶆槸鎻愮ず鐨勬枃妗堟潵婧愶紙Rows 琛ㄩ偅涓€浠斤級銆?        // **鍨傜洿灞呬腑**锛圱extLeftMiddle锛夛細鍙充晶寮€鍏虫槸灞呬腑鐨勶紝鏍囩璐撮《灏变細宸崐琛?        //锛堢敤鎴?2026-10-10 鎶ョ殑"涓嶅湪涓€鏉＄嚎涓?锛夈€?        _widgets.TextLeftMiddle(ctx, Rows[i].Label, label, 13f, Brush(ctx, ink));
        if (IsToggleRow(i)) DrawSwitch(ctx, MoreSwitchRect(i), IsOn(i));
        else if (value != null)
            _widgets.TextLeftMiddle(ctx, value,
                          new RectF { MinX = r.MaxX - 4f - valueW, MinY = r.MinY, MaxX = r.MaxX - 4f, MaxY = r.MaxY },
                          12.5f, Brush(ctx, InkCol));
    }

    // ---- 鎵ц ----------------------------------------------------------------

    private void ActivateHub(int code)
    {
        switch (code)
        {
            case 0: CloseMore(); _host.Commands.OpenTimerCard(); break;
            case 1: CloseMore(); _host.Commands.OpenRollCard(); break;
            case 2: CloseMore(); _host.Commands.OpenRollOne(); break;
            case 3: CloseMore(); _host.Commands.SaveInkFile(); break;
            case 4: CloseMore(); _host.Commands.OpenInkFile(); break;
            case 5:
                CloseMore();
                if (_host.State.ReplayActive) _host.Commands.StopReplay();
                else _host.Commands.StartReplay();
                break;
            case 6:                                   // 淇濆瓨鍥剧墖锛?026-10-02锛?                CloseMore();
                _host.Commands.SaveBoardImage();
                break;
            case 7:                                   // 鎵撳紑鏂囨。锛?026-10-07锛?                CloseMore();
                _host.Commands.OpenDocument();
                break;
            case 9:                                   // 鍘嗗彶鏉夸功锛?026-10-14锛?                CloseMore();
                _host.Commands.OpenInkHistory();
                break;
            case 8:                                   // 鍏抽棴鏂囨。
                CloseMore();
                _host.Commands.CloseDocument();
                break;
        }
        Invalidate();
    }

    private void ActivateBottom(int i)
    {
        switch (i)
        {
            case 0: _morePage = MorePage.Settings; _moreHover = MoreHitNone; Invalidate(); break;
            case 1:
                var stage = _host.State.UpdateStage;
                if (stage == UpdateStage.Available) _host.Commands.ApplyUpdate();
                else if (stage != UpdateStage.Downloading && stage != UpdateStage.Ready)
                    _host.Commands.CheckUpdate();
                Invalidate();
                break;
            case 2: _host.Commands.Restart(); break;   // 鍍忕數鑴戦噸鍚細涓嶅啓浼氳瘽鏆傚瓨锛屾澘涔︿笉鎺ュ洖鏉ワ紙寮曟搸璇箟瑙?RestartFromUi锛?            case 3: _host.Commands.Quit(); break;
        }
    }


    // ---- 璐磋竟闅愯棌 -----------------------------------------------------------

    /// <summary>鏁村潡锛堜富鏉?锛?涓婂甫锛?*鏈钩绉?*鐨勭煩褰€?/summary>
    private RectF UnionRect() => PanelRect();

    private static RectF Union(in RectF a, in RectF b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX), MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX), MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    /// <summary>
    /// 璐磋竟闅愯棌鐨勪綅绉伙細**鍙湁"璐村簳"鎵嶈棌**锛堢敤鎴?2026-09-30 瀹氾紝涓よ疆鏀剁揣鍚庣殑鐜拌鍙ｅ緞锛夈€?    ///
    /// 鐜拌瑙勫垯锛?026-09-30 涓嬪崍锛夛細**闈㈡澘搴曡竟绂诲睆骞曞簳杈?鈮?`DockHideDistance`(10)** 鎵嶈棌锛?    /// 鍏朵粬杈癸紙宸﹀彸銆佷笂锛変竴寰嬩笉钘忥紱杩?*宸ヤ綔鍖哄簳锛堜换鍔℃爮涓婃部锛変篃涓嶇畻**鈥斺€?    /// 榛樿浣嶇疆绂诲睆骞曞簳 52锛堜换鍔℃爮 48 + 绂讳换鍔℃爮 4锛夛紝涓嶈Е鍙戯紱瑕佽棌灏卞緱鎶婂畠鎷栧埌灞忓箷鏈€搴曚笅銆?    ///
    /// 娌块潻锛氭渶鏃╁洓鏉¤竟閮藉垽锛堟敹璧锋€佸洓杈硅棌銆佸睍寮€鎬佷笂涓嬭棌锛夆啋 涓婂崍鏀规垚"鍙搴曡竟銆佺宸ヤ綔鍖哄簳 鈮?0"
    /// 鈫?涓嬪崍鐢ㄦ埛鐐瑰悕"鏀规垚鍒板睆骞曞簳杈?鈮?0锛屽叾浠栦笉鍙?銆?    ///
    /// 浣嶇Щ閲忕収鏍锋寜**灞忓箷**搴曡竟绠楋細瑕嗙洊灞傛槸鍏ㄥ睆缃《鐨勶紝涓嶆帹鍑哄睆骞曞氨钘忎笉鎺?    ///锛堥湶澶村繀椤昏惤鍦ㄥ睆骞曟渶搴曢偅 8 鍍忕礌涓婏紝杩欎篃鏄敤鎴疯鐨?淇濇寔鐜扮姸"锛夈€?    ///
    /// 鏀朵笉鏀剁敱 `_peek` 閭ｄ釜鐘舵€佹満绠★紙瑙?<see cref="UpdatePeek"/>锛夛紱杩欓噷鍙洖绛?    /// "璇ヤ笉璇ョЩ鍔ㄣ€佸線鍝Щ"銆?    /// </summary>
    private Vector2 Shift()
    {
        // 闈㈡澘寮€鐫€锛堟垨寮€鍚堝姩鐢讳腑锛変竴寰嬩笉骞崇Щ锛氭ā鎬侀潰鏉垮繀椤荤汗涓濅笉鍔紝
        // 鑰屼笖瀹冩鍒诲崰鐨勫氨鏄暣鍧楀睆骞曗€斺€旇窡鐫€璐磋竟浣嶇Щ浼氱珛鍒昏澶瑰洖锛岀湅鐫€浼氳烦銆?        if (_moreOpen || _more.Running) return Vector2.Zero;
        if (!_hideEnabled) return Vector2.Zero;
        float t = 1f - _peek.Value;
        if (t <= 0.001f) return Vector2.Zero;

        var u = UnionRect();
        float h = u.MaxY - u.MinY;
        // 鍒ゅ畾锛氶潰鏉垮簳杈圭**灞忓箷**搴曡竟杩樻湁澶氬皯锛堣礋鏁?= 宸茬粡鍘嬭繃灞忓箷搴曪紝涔熺畻璐村簳锛夈€?        float db = _screen.MaxY - u.MaxY;
        if (db > Tokens.DockHideDistance) return Vector2.Zero;      // 涓嶅杩戯細涓嶈棌

        // 浣嶇Щ閲忔寜**灞忓箷**搴曡竟锛堣涓婇潰閭ｆ璁鸿瘉锛夈€?        float sb = _screen.MaxY - u.MaxY;
        return new Vector2(0, (h - Tokens.DockPeek + sb) * t);
    }

    /// <summary>
    /// 姣忓抚鏇存柊"璇ヤ笉璇ユ敹璧锋潵"銆傚啓寰楀儚涓皬鐘舵€佹満锛屽洜涓鸿鍒欏氨涓夋潯锛?    /// 鍐欏瓧涓笉璁稿姩銆佹寚閽堝湪閲岄潰/姝ｆ寜鐫€/銆屾洿澶氥€嶉潰鏉垮紑鐫€涓嶈鏀躲€佸垰绂诲紑瑕佺瓑涓€浼氬効锛堥槻璇Е锛夈€?    /// </summary>
    /// <summary>
    /// 璐磋竟闅愯棌锛?鐪熺殑鍦ㄧ瓑鏀跺悎"鎵嶇户缁甯с€?    ///
    /// 鈿?**涓轰粈涔堜笉鑳界洿鎺ョ敤 `! _hoverInside && _peek.Value > 0`**锛?026-10-07 淇級锛?    /// 閭ｆ潯鍦?**闈㈡澘鏍规湰涓嶄細鏀?*"鐨勬椂鍊欎篃鎴愮珛锛屼簬鏄?*姘歌繙瑕佸抚**鈥斺€?    /// 鏈€鍏稿瀷鐨勪竴绉嶏細寮€浜嗚创杈归殣钘忎絾鎸囬拡**杩樻病纰拌繃闈㈡澘**锛坄_peekArmed == false`锛?    /// 瑙?`UpdatePeek` 寮€澶达紝姝ゆ椂闈㈡澘鎸夎璁′繚鎸佸叏寮€銆乣_peek.Value == 1`锛夈€?    /// 瀹炴祴鍚庢灉锛?*寰呮満 45.5 fps 鎸佺画娓叉煋銆佸崟鏍?4~5%锛屾案涓嶅仠姝?*鈥斺€?    /// 鍏虫帀杩欎釜寮€鍏虫垨鍒犳帀璁剧疆灏辨槸 0 fps銆傜瑪璁版湰涓婂氨鏄竴鐩磋€楃數銆佷竴鐩村彂鐑€?    ///
    /// 鐜板湪鍙湁"armed 涔嬪悗銆佷笖褰撳墠涓嶅浜?keepOpen銆佷笖娌″湪鍐欏瓧"鎵嶇畻"鍦ㄧ瓑鏀跺悎"锛?    /// 涓夌鎯呭喌閮戒細鏀舵暃鍒?0 鈥斺€?鏀讹紙`_peek.Value 鈫?0`锛夈€佹垨 keepOpen锛坄_peekPendingCollapse`
    /// 绔嬪埢鍙樺亣锛夈€佹垨娌?arm锛堝悓鏍蜂负鍋囷級銆?    /// </summary>
    private bool _peekPendingCollapse;

    private void UpdatePeek()
    {
        if (!_hideEnabled) { _peek.To(1f, 0); _peekPendingCollapse = false; return; }

        // **鍚姩涔嬪悗鍏堜笉钘?*锛堢敤鎴?2026-09-17锛?鍦ㄨ创杈归殣钘忕殑鎯呭喌涓嬶紝鍒氬惎鍔ㄨ蒋浠剁殑鏃跺€欎笉瑕侀殣钘?锛夈€?        //
        // 鐞嗙敱寰堝疄鍦細`_leftAtMs` 鍒濆€兼槸璐熸棤绌凤紝鎵€浠ョ涓€甯у氨婊¤冻"绂诲紑澶熶箙浜?鈥斺€?        // 涓€寮€鏈洪潰鏉跨珛鍒绘敹鎴愬睆骞曞簳杈归偅鏉?8 鍍忕礌鐨勯湶澶达紙鑰屼笖闇插ご姝ｅソ鍘嬪湪浠诲姟鏍忎笂锛夛紝
        // 鑰佸笀鏍规湰鎵句笉鍒板畠銆傜幇鍦ㄦ敼鎴愶細**鍏堥湶鐫€**锛岀瓑鎸囬拡纰拌繃闈㈡澘涓€娆★紙`_peekArmed`锛?        // 鎵嶅厑璁?绂诲紑灏辨敹"鈥斺€旈偅鏃跺€欎粬宸茬粡鐭ラ亾涓滆タ鍦ㄥ摢鍎夸簡銆?        if (!_peekArmed) { _peek.To(1f, 0); _peekPendingCollapse = false; return; }

        // **鍐欏瓧涓細浠€涔堥兘涓嶅仛**锛堝師鏍疯繑鍥烇級锛屼笉鏄?寮哄埗灞曞紑"銆?        //
        // 杩欐潯瑙勫垯鐨勬湰鎰忎竴鐩存槸"鍐欏瓧鐨勬椂鍊欎笉璁告敹"鈥斺€旇€佸笀鍐欏埌灞忓箷杈逛笂锛屽伐鍏锋潯涓嶈兘鑷繁缂╁洖鍘汇€?        // 浣嗗畠鍘熸潵鍜屼笅闈?`keepOpen` 鐢ㄧ殑鏄悓涓€鍙?`_peek.To(1f)`锛屼簬鏄?涓嶈鏀?鍙樻垚浜?蹇呴』灞曞紑"锛?        // **钘忓ソ鐨勯湶澶翠竴钀界瑪灏辫鎷藉嚭鏉?*锛堢敤鎴?2026-09-18 鎶ョ殑"璐磋竟闅愯棌浠ュ悗鎴戜竴鍐欏畠灏卞彇娑堣创杈逛簡"锛夈€?        // 鐜板湪鍗曠嫭鎷庡嚭鏉?*鍘熸牱杩斿洖**锛氭湰鏉ュ彧鍓╅湶澶村氨缁х画闇插ご锛屾湰鏉ュ氨寮€鐫€灏辩户缁紑鐫€銆?        if (_host.State.IsDrawing)
        {
            // 鍐欏瓧杩欐鏃堕棿涓嶇畻"绂诲紑"锛屽啓瀹岃繕瑕佺瓑婊?700 姣鎵嶅厑璁告敹锛堥槻璇Е閭ｆ潯瑙勫垯鐓ф棫锛夈€?            _leftAtMs = _host.NowMs;
            _peekPendingCollapse = false;
            return;
        }

        bool keepOpen = _hoverInside || _press != -1 || _sliderDragging || _moreOpen
                        || _host.NowMs < _peekHoldUntilMs;
        _peekPendingCollapse = !keepOpen && _peek.Value > 0f;   // 鍙湁杩欎竴妗ｆ墠闇€瑕佺户缁甯?        if (keepOpen)
        {
            _leftAtMs = _host.NowMs;
            _peek.To(1f, Tokens.SnapMs);
            return;
        }
        if (_host.NowMs - _leftAtMs < 700) return;               // 鍒氱寮€锛氬啀绛夌瓑锛堥槻璇Е锛?        _peek.To(0f, Tokens.SnapMs);
    }

    /// <summary>
    /// 璐磋竟闅愯棌鐨?鎸囬拡杩樺湪闈㈡澘涓婂悧"鍒ゅ畾锛?*闃叉姈杩熸粸**锛?026-10-05 淇?璐磋竟缈婚〉鏃堕棯璺?锛夛細
    ///
    ///   路 瀹屽叏鏀惰捣鏃讹細鍙闇插ご閭ｄ竴鏉★紙鎸囬拡鎵繃"闈㈡澘鏈潵鐨勪綅缃?涓嶄細鍑┖鍙敜瀹冿級锛?    ///   路 灞曞紑/鏀惰捣/鍔ㄧ敾鏈熼棿锛氬垽瀹氬尯鍙?**鐩爣灞曞紑鍚庣殑瀹屾暣闈㈡澘 鈭?褰撳墠鍙鑼冨洿**銆?    ///
    /// 涓轰粈涔堜笉鑳界敤"褰撳墠鍔ㄧ敾涓殑鐭╁舰"锛堝師鏉ュ氨鏄級锛氶潰鏉夸竴杈归暱銆佸垽瀹氬尯涓€杈硅窡鐫€璺戯紝
    /// 鎸囬拡浼氳"鐢╁嚭"鍒ゅ畾鍖?鈫?鏀惰捣 鈫?闇插ご鍙堝洖鍒版寚閽堜笅闈?鈫?鍐嶅睍寮€鈥︹€︿竴甯т竴甯у湴璺炽€?    /// 杩欏拰 Windows 浠诲姟鏍忚嚜鍔ㄩ殣钘忕殑杩熸粸鏄悓涓€涓亾鐞嗭細**灞曞紑鍚庣殑鍦扮洏鍏堢畻杩涙潵**锛?    /// 闅愯棌鍐嶆參涓€姝ワ紙瑙?Tokens.RailHideDelayMs 涓庢湰绫荤殑 700ms 闃茶瑙︼級銆?    /// </summary>
    private bool HoverInsideForPeek(float x, float y)
    {
        if (_peek.Value > 0.01f)
        {
            var full = UnionRect();
            float pad = Tokens.RailHoverPad;
            if (x >= full.MinX - pad && x <= full.MaxX + pad
                && y >= full.MinY - pad && y <= full.MaxY + pad) return true;
        }
        return QueryBounds().Contains(x, y);
    }

    // ---- 杈撳叆 ---------------------------------------------------------------

    public bool PointerDown(in UiPointerEvent e)
    {
        var p = Local(e);
        _pressId = e.PointerId;                // 杩欎竴鎸夋槸璋佹寜鐨勶紙闀挎寜鏈熼棿鎸″埆鐨勬寚閽堬級
        _press = -1;
        _dragging = false;
        HideTip();                         // 鎸変笅 = 鏂板姩浣滃紑濮嬶紝鎻愮ず鍏堟敹
        CancelTipHold();                   // 涓婁竴娆＄殑闀挎寜鍊欓€変篃浣滃簾锛堟柊鎸変笅閲嶆柊璁℃椂锛?        // **"鎸変笅浜?涓嶇瓑浜?鎸夊湪闈㈡澘涓?**銆傚紩鎿庝細鎶?*姣忎竴娆?*鎸変笅閮借浆缁欑晫闈?        // 锛堢晫闈㈡湁鏉冨喅瀹氬悆涓嶅悆锛夛紝鎵€浠ヨ繖閲屽繀椤昏嚜宸卞垽涓€娆′綅缃€?        // 鏃犳潯浠剁疆 true 鐨勫悗鏋滐紙鐢ㄦ埛 2026-09-18 鎶ョ殑"璐磋竟闅愯棌浠ュ悗鎴戜竴鍐欏畠灏卞彇娑堣创杈逛簡"锛夛細
        // 鍦ㄧ敾甯冧笂钀界瑪 鈫?杩欓噷缃?true銆侀殢鍚庤繑鍥?false锛堣繖涓€绗斿綊鐢诲竷锛夆啋 浣?true 鐣欎簡涓嬫潵锛?        // 鑰?*鍐欏瓧鏈熼棿寮曟搸涓嶈浆鍙?PointerMove**锛堥偅涓€绗斿凡缁忓綊鐢诲竷浜嗭級锛屾病浜哄幓鎶婂畠鏀瑰洖鏉?鈫?        // `UpdatePeek` 涓€鐩翠互涓?鎸囬拡杩樺湪闈㈡澘涓? 鈫?鎶婅棌濂界殑闇插ご閲嶆柊鎷藉嚭鏉ャ€?        _hoverInside = HoverInsideForPeek(e.X, e.Y);
        _peekArmed = true;                 // 纰拌繃浜?鈫?涔嬪悗鍏佽"绂诲紑灏辨敹"
        _leftAtMs = _host.NowMs;
        _pressPos = p;
        // 鎷栧姩璁扮殑鏄?*宸︿笂瑙?*锛堝拰 RawAnchor 鍚屼竴濂楄涔夛細閿?甯﹀瓙鐨勫乏绔?锛?        _dragStartAnchor = Anchor();

        // **鎸変笅涔熺畻"鐒︾偣鍦ㄩ潰鏉夸笂"**锛氭墜鍐欑瑪鍜岃Е鎽告病鏈夋偓鍋滈偅涓€娈碉紝
        // 鍙湪 PointerMove 閲屾洿鏂?_railHover 鐨勮瘽锛岃€佸笀鐢ㄧ瑪鐐归潰鏉挎椂璁剧疆鏉℃牴鏈笉浼氬紶寮€
        // 锛堥紶鏍囪兘寮犲紑銆佺瑪涓嶈兘鈥斺€旇繖绫?鍙湪涓€绉嶈澶囦笂鍧?鐨?bug 鏈€闅炬煡锛夈€?        _railHover = !_host.State.PassThrough && BandVisible() && RailHoverZone().Contains(p.X, p.Y);
        _railHoverStamp = _host.NowMs;    // 浣嶇疆鏇存柊鎵撴椂闂存埑锛堝啓瀛楀悗"绂诲紑涓€娆?鐨勫垽鎹敤锛?        bool touchLike = e.FromTouch || e.FromPen;   // 鎵嬫寚/绗旀帴瑙︼紙榧犳爣涓嶅弬涓庨暱鎸夛級

        // 璐磋竟闅愯棌鐨?*瑙﹀睆鑺傚**锛?026-10-05锛夛細鎵嬫寚/绗旀妸闈㈡澘鎸夊嚭鏉ュ悗锛岀粰涓€娈?澶熷緱鐫€"鐨?        // 鍋滅暀鏃堕棿锛堟澗鎵嬪悗鍒?0.7s 灏辨敹鈥斺€旀墜鎸囪繕寰楀啀鐐逛竴涓嬪伐鍏凤紱2500ms 鏄?鐪嬩竴鐪肩偣寰椾腑"鐨勯噺绾э級銆?        // 椤烘墜鎶?*璁剧疆鏉?*涔熷甫鍑烘潵锛氳Е鎽告病鏈夋偓鍋滐紝鐐瑰伐鍏锋牸 = 鎯崇湅杩欎釜宸ュ叿鐨勮缃潯銆?        if (touchLike && _hoverInside)
        {
            _peekHoldUntilMs = _host.NowMs + 2500;
            _railTouchHoldUntilMs = _host.NowMs + 2500;
        }

        // 銆屾洿澶氥€嶉潰鏉匡細鍏ㄥ睆妯℃€侊紝鍏堜簬涓€鍒囧叾瀹冨懡涓紙瀹冪洊浣忔暣鍧楀睆骞曪級銆?        // 鐐归潰鏉垮 = 鍏抽棴锛岃€屼笖杩欎竴涓?*涓嶈惤澧?*锛堟秷璐规帀锛涜繖涔熸槸鑷瑕侀拤鐨勪竴鏉★級銆?        if (_moreOpen)
        {
            int hit = MoreHitAt(p.X, p.Y);
            if (hit == MoreHitNone || hit == MoreHitClose) { CloseMore(); return true; }
            if (hit == MoreHitBack) { _morePage = MorePage.Home; Invalidate(); return true; }
            if (hit >= MoreHitProfile && hit < MoreHitProfile + 3)
            { SetProfile((Profile)(hit - MoreHitProfile)); return true; }
            // 鍖洪棿蹇呴』**鎸夎〃鐨勯暱搴︽敹鍙?*锛氬鏍?30+1..30+12銆佽 60+0..60+8銆佸惎鍔ㄥ櫒鏍煎瓙 140+0..140+5銆?            // 鍐欏浜嗗氨浼氫覆娈碘€斺€斿巻鍙蹭笂瀹牸鍐欐垚 +32 鍚冭繃琛岀殑 60/61锛圱ogglePin 瓒婄晫鎶涘紓甯革紝鑷褰撳満绾級銆?            if (hit >= MoreHitChip && hit <= MoreHitChip + Cells.Length - 1)
            { TogglePin(hit - MoreHitChip); return true; }
            // 琛?鏍煎瓙鎸変笅鏃?*瀛樺師濮嬪懡涓爜**锛屾姮璧锋椂姣斿鍚屼竴涓爜锛堝悓涓€濂楁墜鎰燂級銆?            // 瑙︽懜/绗旀寜浣忎笉鍔?= 闀挎寜鍊欓€夛紙鍒扮偣鍑烘彁绀恒€佹澗鎵嬩笉鎵ц锛夛紱榧犳爣鐓ф棫銆?            bool pressRow = false;
            if (hit >= MoreHitRow && hit <= MoreHitRow + Rows.Length - 1)
            { _morePress = hit; pressRow = true; }
            if (hit >= MoreHitTile && hit <= MoreHitTile + HubTileTotal - 1)
            { _morePress = hit; pressRow = true; }
            if (hit >= MoreHitBottom && hit <= MoreHitBottom + MoreBottomTiles.Length - 1)
            { _morePress = hit; pressRow = true; }
            if (pressRow)
            {
                if (touchLike && TipContent(1000 + hit).Title != null) ArmTipHold(1000 + hit);
                Invalidate();
                return true;
            }
            return true;                    // 闈㈡澘閲岀殑绌虹櫧锛氬悆鎺夛紝浣嗕笉鍏?        }

        if (_expand.Value < 0.5f)
        {
            if (!BallRect().Contains(p.X, p.Y)) return false;
            _press = -2;                    // 鐞?            return true;
        }

        // ---- 瑙︽懜/绗旈暱鎸夛紙2026-10-02 绗簩杞級----
        //
        // 榧犳爣**涓嶈蛋杩欓噷**锛堥紶鏍囨寜浣?= 鎷栧姩/鍒掓粦鏉★紝璇箟涓€涓瓧涓嶅彉锛夈€傝繖涓€鍧楀彧鏈嶅姟
        // "鎸変笅鍗崇敓鏁堛€佸繀椤绘敼鎴?鎸変綇鍊欓€夈€佹姮璧风敓鏁?鎵嶈兘闀挎寜"鐨勫嚑涓細铏氬疄绾挎牸銆?        // 鍥惧舰娈电殑 22 鏍笺€佸叧闂櫧鏉?鉁曘€傚埆鐨勫厓绱犲悇鑷湁瀹夋帓锛?        //   路 宸ュ叿鏍兼湰鏉ュ氨鍦ㄦ姮璧锋椂鎵ц锛堣 PointerUp锛夛紝鍦ㄤ笅闈?idx 鍒嗘敮姝﹁锛?        //   路 娓呯┖ = 鎸変綇 0.8 绉掓竻绌恒€佹粦鏉?= 鎷栧姩銆丳PT 椤电爜鏍?= 闀挎寜鑿滃崟 鈫?**鎺掗櫎**锛?        //   路 鑹茬墖 / 鏂囧瓧娈垫病鏈夋彁绀猴紙鏀剁獎娓呭崟锛夆啋 涓嶆瑁咃紝鎸変笅鍗崇敓鏁堢収鏃с€?        if (touchLike && BandOpen())
        {
            int holdTarget = -1;
            if (HitDashToggle(p.X, p.Y)) holdTarget = 500;
            else if (_bandCell == ShapeCell)
            {
                int sg = HitSegment(p.X, p.Y);
                if (sg >= 0) holdTarget = 200 + sg;
            }
            else if (CurAction == BandAction.CloseBoard && ActionRect().Contains(p.X, p.Y))
                holdTarget = 400;
            if (holdTarget >= 0 && TipContent(holdTarget).Title != null)
            {
                ArmTipHold(holdTarget);
                return true;      // 鎶捣鎵嶆墽琛岋紱涓€旈暱鎸夊嚭鎻愮ず鍒欒繖娆′笉鎵ц
            }
        }

        // **婊戞潯瑕佹帓鍦ㄥ伐鍏锋牸鍓嶉潰**锛氬畠鍦ㄩ潰鏉挎渶涓嬫部锛屽拰宸ュ叿鏍肩殑鐭╁舰鏄噸鍙犵殑銆?        // 鎺掑湪鍚庨潰鐨勮瘽锛屾寜鏈€涓嬫部閭ｄ竴鏉′細琚綋鎴?鐐逛簡鏌愪釜宸ュ叿"锛堝亣闈㈡澘閲?groove 涔熸槸鍏堝垽鐨勶級銆?        // 鍔ㄤ綔鎸夐挳锛堟竻绌?鍏ㄩ€夛級鎺掑湪鏈€鍓嶉潰锛氬畠璐村湪涓婂甫鏈€澶栨部锛屽拰璋侀兘鎸ㄧ潃銆?        if (BandOpen() && CurAction != BandAction.None
            && ActionRect().Contains(p.X, p.Y))
        {
            if (CurAction == BandAction.Clear)
            {
                // 娓呯┖锛?*鎸変綇鎵嶇畻鏁?*锛?.8 绉掞級锛屾澗鎵嬪嵆鍙栨秷銆傝繘搴︾敱 UpdateBandAction 姣忓抚鎺ㄨ繘銆?                _actionHoldFrom = _host.NowMs;
                _press = 2000;
            }
            else if (CurAction == BandAction.CloseBoard)
            {
                // 鍏抽棴鐧芥澘锛?*鐐逛竴涓嬪氨鎵ц**锛堝拰鍏ㄩ€変竴鏍凤級鈥斺€斿叧鏉挎槸鍙€嗙殑锛堝啀鐐圭櫧鏉挎牸灏卞紑鍥炴潵锛夛紝
                // 涓嶅儚娓呯┖閭ｆ牱浠ｄ环澶э紝鎵€浠ヤ笉蹇呮寜浣忋€?                // 鈿?鏉垮凡缁忓叧鐫€鏃惰繖涓€涓嬩粈涔堥兘涓嶅仛锛堟寜閽篃鏄帇鏆楃殑锛岃 DrawBandAction锛夈€?                if (_host.State.Board)
                {
                    _host.Commands.SetBoard(false);
                    _actionFlashUntil = _host.NowMs + 260;
                }
            }
            else if (CurAction == BandAction.PasteImage)
            {
                // 绮樿创鍥剧墖锛氱偣涓€涓嬪氨鎵ц鈥斺€斿氨鏄壒娉ㄥ唴鐨?Ctrl+V銆?                // 瀹冧互鍓嶆槸鎴浘閭ｆ牸鐨勭涓夋锛堝垎娈?妯″紡锛屽畠=鍔ㄤ綔锛屾贩鐫€姘歌繙涓嶄寒锛夛紱
                // 8.3.0 鎷嗗埌杩欓噷涔嬪悗锛岃Е鎽稿睆/鎵嬪啓鏉挎病閿洏鐨勮€佸笀鐓ф牱澶熷緱鐫€銆?                _host.Commands.Paste();
                _actionFlashUntil = _host.NowMs + 260;
            }
            else
            {
                _host.Commands.SelectAll();          // 鍏ㄩ€夛細鐐逛竴涓嬪氨鎵ц
                _actionFlashUntil = _host.NowMs + 260;
            }
            Invalidate();
            return true;
        }

        if (BandOpen() && BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(p.X, p.Y))
        {
            _sliderDragging = true;
            DragSlider(p.X);       // 鎸変笅鍗宠烦鍒拌浣嶇疆锛堝紩鎿庡惛闄勫埌鏈€杩戜竴妗ｏ級锛屽彲鎺ョ潃鎷?            return true;
        }

        int idx = HitCell(p.X, p.Y);
        if (idx >= 0)
        {
            _press = idx;
            // 宸ュ叿鏍硷細鎶捣鎵ц锛涜Е鎽?绗旀寜浣忎笉鍔?= 闀挎寜鍊欓€夛紙榧犳爣涓嶆瑁咃級
            if (touchLike && TipContent(idx).Title != null) ArmTipHold(idx);
            return true;
        }

        // 涓婂甫锛氳壊鐗?/ 鍒嗘锛堟粦鏉″凡缁忓湪涓婇潰鍒よ繃浜嗭級
        if (BandOpen())
        {
            // 铏氬疄绾块偅涓€鏍硷細**鎸変笅鍗崇敓鏁?*锛堝拰鑹茬墖鍚屼竴涓墜鎰熲€斺€旂偣涓€涓嬪氨璇ョ湅瑙佺粨鏋滐紝
            // 涓嶇敤绛夋姮鎵嬶紱鎶墜閭ｄ竴涓嬭繕瑕佺粰"鎷栧姩闈㈡澘"璁╄矾锛夈€?            if (HitDashToggle(p.X, p.Y)) { CycleDash(); return true; }
            int sw = HitSwatch(p.X, p.Y);
            if (sw >= 0) { ActivateSwatch(sw); return true; }
            int sg = HitSegment(p.X, p.Y);
            if (sg >= 0) { ActivateSegment(sg); return true; }
        }

        // 鏀惰捣鐨勮壊绾匡細**鐐逛竴涓嬪氨寮犲紑**锛?026-10-09 鏂规 A鈶狅紝瑙?璋冪爺-鑹插甫-鍏ㄧ洏澶嶆煡涓庝紭鍖?md锛夈€?        // 鏄庣‘鍔ㄤ綔绔嬪嵆鍝嶅簲鈥斺€旇烦杩囨偓鍋滃欢杩熴€佸啓瀛椾綑娓┿€?绂诲紑涓€娆?杩欎笁閬撻棬妲涳紱
        // 鎮仠閭ｆ潯璺繚鎸?鎱竴姝?鐨勬矇绋筹紙240ms + 浣欐俯锛屼竴涓瓧涓嶅姩锛夈€?        // 绌块€忛噷闄ゅ锛堝彛寰勫悓 UpdateRail锛?姘歌繙淇濇寔鏀惰捣鐨勯偅鏉¤壊绾?锛夈€?        // 鈿?鍛戒腑鐢?*褰撳墠閭ｆ潯鑹茬嚎鐨勭煩褰?*锛堟敹璧锋€?= 10px锛夛紝涓嶈兘鐢?RailZone锛堥偅鏄?寮犲紑鍚庡叏楂?锛?        //    鍥惧舰鏍艰兘鍒?~110px鈥斺€斾細鎶婇潰鏉挎涓婃柟鐢诲竷涓婄殑钀界瑪涔熷悆鎺夛級銆備笂涓嬪悇鏀惧 10px 濂界偣銆?        if (!BandOpen() && !_host.State.PassThrough && BandVisible())
        {
            var bandNow = BandRect();
            var bandHit = new RectF
            {
                MinX = bandNow.MinX, MinY = bandNow.MinY - Tokens.RailHoverPad,
                MaxX = bandNow.MaxX, MaxY = bandNow.MaxY + Tokens.RailHoverPad,
            };
            if (bandHit.Contains(p.X, p.Y))
            {
                _railRequireLeave = false;  // 鏄庣‘鐐瑰嚮 = 鑰佸笀涓诲姩"鍥炴潵"浜嗭紝涓嶅啀瑕佹眰鍏堢寮€涓€娆?                _railEnterAtMs = double.NegativeInfinity;
                _rail.To(1f, Tokens.RailMs);
                return true;
            }
        }

        return false;                       // 甯﹀瓙/涓婂甫閲岀殑绌虹櫧锛堜袱绔唴杈硅窛锛夛細涓嶅悆锛屽紩鎿庢寜"鍦扮洏"鍚炴帀
    }

    /// <summary>灞忓箷鍧愭爣 鈫?"娌℃湁骞崇Щ杩囩殑"甯冨眬鍧愭爣锛堣创杈归殣钘忎細鏁翠綋骞崇Щ涓€娆★級銆?/summary>
    private Vector2 Local(in UiPointerEvent e)
    {
        var s = Shift();
        return new Vector2(e.X - s.X, e.Y - s.Y);
    }

    public bool PointerMove(in UiPointerEvent e)
    {
        // 瑙︽懜闀挎寜璁℃椂 / 鎻愮ず鍋滅暀鏈熼棿锛?*鍒殑鎸囬拡**锛堝仠鐫€鐨勯紶鏍囥€佺瑪鎮仠锛夌殑绉诲姩涓嶈鎼呭眬銆?        // 寮曟搸浼氭妸绐楀彛鏀跺埌鐨勬墍鏈夌Щ鍔ㄩ兘杞粰鐣岄潰锛圲iCapturing 鍒嗘敮锛夛紝灏戜簡杩欎竴閬擄紝
        // 榧犳爣闅忔墜鍔ㄤ竴涓嬪氨琚綋鎴?鎵嬫寚婊戣蛋浜?锛?026-10-02 鑷瀹炴祴 569px 鍋囩Щ鍔級銆?        if ((_tipHoldStart > double.NegativeInfinity || _tipHoldFired)
            && e.PointerId != _pressId)
            return true;
        _hoverInside = HoverInsideForPeek(e.X, e.Y);
        if (_hoverInside) _peekArmed = true;   // 鎸囬拡杩涜繃闈㈡澘 鈫?涔嬪悗鍏佽"绂诲紑灏辨敹"
        _leftAtMs = _host.NowMs;
        var p = Local(e);
        _railHover = !_host.State.PassThrough && BandVisible() && RailHoverZone().Contains(p.X, p.Y);
        _railHoverStamp = _host.NowMs;    // 浣嶇疆鏇存柊鎵撴椂闂存埑锛堝啓瀛楀悗"绂诲紑涓€娆?鐨勫垽鎹敤锛?
        // 銆屾洿澶氥€嶉潰鏉垮紑鐫€鏃讹紝鎸囬拡鍙杺缁欓潰鏉匡細鏇存柊鎮仠銆佸埆鍐嶇涓绘潯鐨勬偓鍋?鎷栧姩鐘舵€?        if (_moreOpen)
        {
            int hit = MoreHitAt(p.X, p.Y);
            if (hit != _moreHover) { _moreHover = hit; Invalidate(); }
            // 闈㈡澘閲岀殑鎻愮ず锛氶紶鏍囨偓鍋滃嚭銆佹墜鎸?绗旈暱鎸変篃鍑猴紙2026-10-02 绗簩鎵癸級
            SetTipTarget(MoreTipTargetAt(hit));
            // 闀挎寜鍊欓€夛細鍦ㄩ潰鏉夸笂婊戣蛋锛堣秴杩囨嫋鍔ㄩ槇鍊硷級灏卞彇娑?            if (_tipHoldStart > double.NegativeInfinity
                && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
                CancelTipHold();
            return true;
        }

        if (_sliderDragging)
        {
            HideTip();                 // 鎷栨粦鏉℃椂"绮楃粏棰勮"閭ｅ紶鍗″湪鐢伙紝鍒彔鎻愮ず
            DragSlider(p.X);
            return true;
        }

        if (_press != -1)
        {
            // 瑙︽懜/绗旈暱鎸夊凡缁忓脊杩囨彁绀猴細杩欎竴娆℃寜涓嬩綔搴熲€斺€斾笉鎷栧姩銆佷篃涓嶆墽琛岋紙绛夋澗鎵嬶級銆?            if (_tipHoldFired) return true;
            // **鍔ㄤ綔鎸夐挳涓嶅弬涓庢嫋鍔?*銆?            //
            // 鐢ㄦ埛瀹炴祴鎶ョ殑 bug锛?鎸変綇娓呯┖鐨勬椂鍊欙紝鎵嬩竴鎶栧氨鎶婃暣涓潰鏉挎嫋璧颁簡"鈥斺€?            // 闈㈡澘涓€璧帮紝鎸夐挳灏变笉鍦ㄦ寚閽堜笅闈簡锛岀湅鐫€灏辨槸"娓呯┖娌″弽搴?銆?            // 鍙湁鐐瑰湪**涓绘潯涓?*锛堢悆鎴栬€呭伐鍏锋牸锛夋墠绠?鎶撲綇闈㈡澘"銆?            // 锛堛€屾洿澶氥€嶉潰鏉跨殑琛屾湁瀹冭嚜宸辩殑 `_morePress`锛岃蛋涓嶅埌杩欓噷鈥斺€斿畠鏄叏灞忔ā鎬併€傦級
            bool draggable = _press != 2000 && _press < 1000;
            if (draggable && !_dragging && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
            {
                _dragging = true;
                CancelTipHold();        // 绉诲姩灏辨槸鎷栧姩锛氶暱鎸夊€欓€変綔搴燂紙涓绘祦娑堟锛氱Щ鍔?鎷栥€佷笉鍔?闀挎寜锛?            }
            if (_dragging)
            {
                _anchor = _dragStartAnchor + (p - _pressPos);
                Invalidate();               // 浣嶇疆涓€鍙樺氨瑕侀噸鐢伙紱寮曟搸閭ｈ竟姣忓抚閮戒細鍔犺繘鑴忓尯
            }

            // 鎸変綇娓呯┖鐨勬椂鍊欐寚閽堟粦鍑烘寜閽?= 绠椾簡锛堝悇瀹舵寜閽兘鏄繖涓害瀹氾級銆?            // 鍒ゅ畾鍖虹粰 8 鍍忕礌浣欓噺锛屽厤寰楁墜鎶栦竴涓ゅ儚绱犲氨鍙栨秷銆?            if (_press == 2000)
            {
                var hit = ActionRect();
                if (p.X < hit.MinX - 8f || p.X > hit.MaxX + 8f
                    || p.Y < hit.MinY - 8f || p.Y > hit.MaxY + 8f)
                {
                    _actionHoldFrom = double.NegativeInfinity;
                    _press = -1;
                    Invalidate();
                }
            }
            return true;
        }

        // 瑙︽懜闀挎寜宸茬粡寮硅繃鎻愮ず锛堟/绾垮瀷/鍏虫澘閭ｆ潯璺級锛氭寜浣忔湡闂寸Щ鍔ㄤ粈涔堥兘涓嶅仛銆?        if (_tipHoldFired) return true;
        // 瑙︽懜闀挎寜杩樺湪璁℃椂锛屼絾鎵嬫寚宸茬粡婊戣蛋锛氳繖娆℃寜涓嬩笉鎵ц銆佷篃涓嶅脊鎻愮ず锛堥噸鏂版寜锛夈€?        if (_tipHoldStart > double.NegativeInfinity
            && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
        {
            CancelTipHold();
            Invalidate();
            return true;
        }

        int hover = _expand.Value < 0.5f
            ? (BallRect().Contains(p.X, p.Y) ? -2 : -1)
            : HoverAt(p.X, p.Y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        SetTipTarget(hover);
        return hover != -1;
    }

    public void PointerLeave()
    {
        // 瑙︽懜闀挎寜锛氭墜鎸囪繕鎸夌潃鐨勬椂鍊欙紝"绂诲紑"澶氬崐鏄皬绐?涓荤獥鍒囨崲鐨勫亣鍔ㄤ綔鈥斺€?        // 鎸変綇璁℃椂 / 宸茬粡寮瑰嚭鐨勬彁绀?*涓嶅湪杩欓噷鏀?*锛堟澗鎵嬫垨鍋滅暀鍒版湡鑷細鏀讹級銆?        if (_tipHoldStart == double.NegativeInfinity && !_tipHoldFired)
        {
            HideTip();
            CancelTipHold();
        }
        // 闈㈡澘寮€鐫€鏃舵寚閽堝彧浼?绂诲紑鏁村潡灞忓箷"锛堝崰鐢?= 鍏ㄥ睆锛夛細鎶婇潰鏉跨殑鎮仠/鎸変笅涔熸竻鎺?        if (_moreOpen)
        {
            _moreHover = MoreHitNone;
            _morePress = -1;
            Invalidate();
        }
        if (!_hoverInside) return;
        _hoverInside = false;
        _railHover = false;              // 鎸囬拡绂诲紑闈㈡澘 = 鐒︾偣涓嶅湪浜嗭紝璁剧疆鏉¤鏀讹紙璧?ExitDelay锛?        _leftAtMs = _host?.NowMs ?? 0;
        Invalidate();
    }

    /// <summary>
    /// 杩欎竴鍒绘寚閽堣惤鍦ㄤ粈涔堜笂闈€備笂甯﹂噷鐨勪笢瑗跨敤鍋忕Щ鐨勭紪鍙凤細
    /// 100锛嬭壊鐗囦笅鏍囥€?00锛嬪垎娈典笅鏍囥€?00 = 婊戞潯銆?*鐢讳笌鍛戒腑鍚屾簮**锛?    /// 涓嶈繖涔堢紪鍙风殑璇?鎮仠鎬?鍜?鍛戒腑"浼氬悇鍐欎竴浠藉垽鏂紝鐒跺悗鎱㈡參涓嶄竴鑷淬€?    /// </summary>
    private int HoverAt(float x, float y)
    {
        int idx = HitCell(x, y);
        if (idx >= 0) return idx;
        if (!BandOpen()) return -1;
        // 鍔ㄤ綔鎸夐挳锛堟竻绌?鍏ㄩ€夛級锛氱紪鍙?400锛屽拰鑹茬墖 100銆佸垎娈?200銆佹粦鏉?300 鎺掓垚涓€濂?        if (CurAction != BandAction.None && RailOpen && ActionRect().Contains(x, y)) return 400;
        if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(x, y)) return 300;
        if (HitDashToggle(x, y)) return 500;                       // 铏氬疄绾块偅涓€鏍?        int sw = HitSwatch(x, y);
        if (sw >= 0) return 100 + sw;
        int sg = HitSegment(x, y);
        if (sg >= 0) return 200 + sg;
        return -1;
    }

    public bool PointerUp(in UiPointerEvent e)
    {
        // 瑙︽懜闀挎寜/鎻愮ず鍋滅暀鏈熼棿锛屽彧鏈?*鎸変笅鐨勯偅鏍规墜鎸?*鐨勬姮璧锋墠绠楁暟锛堝埆鐨勬寚閽堟姮璧峰拷鐣ワ級銆?        if ((_tipHoldStart > double.NegativeInfinity || _tipHoldFired)
            && e.PointerId != _pressId)
            return true;
        // 銆屾洿澶氥€嶉潰鏉匡細鎸変笅鍜屾姮璧疯惤鍦?*鍚屼竴琛?*鎵嶇畻涓€娆℃墽琛岋紙鍜屾娊灞夊悓涓€濂楁墜鎰燂級
        if (_moreOpen)
        {
            // 瑙︽懜/绗旈暱鎸夊凡缁忓脊杩囨彁绀猴細杩欎竴娆℃澗鎵?*涓嶆墽琛?*锛堟彁绀哄仠涓€浼氬効鑷繁鏀讹級鈥斺€?            // 鍜屼富鏉′笂閭ｆ潯瑙勫垯瀹屽叏涓€鑷达紙Windows/Material 鎯緥锛氶暱鎸夋槸"鐪?涓嶆槸"鐐?锛夈€?            if (_tipHoldFired)
            {
                CancelTipHold();
                _tipLingerUntil = _host.NowMs + Tokens.TipLingerMs;
                _morePress = -1;
                Invalidate();
                return true;
            }
            int hit = MoreHitAt(e.X, e.Y);
            int pressed = _morePress;
            _morePress = -1;
            if (pressed >= MoreHitRow && pressed <= MoreHitRow + Rows.Length - 1 && hit == pressed)
                ActivateRow(pressed - MoreHitRow);
            else if (pressed >= MoreHitTile && pressed <= MoreHitTile + HubTileTotal - 1 && hit == pressed)
                ActivateHub(pressed - MoreHitTile);
            else if (pressed >= MoreHitBottom && pressed <= MoreHitBottom + 3 && hit == pressed)
                ActivateBottom(pressed - MoreHitBottom);
            Invalidate();
            return true;
        }
        if (_morePress >= 0) { _morePress = -1; return true; }   // 闈㈡澘鍒氬叧鎺夌殑閭ｄ竴涓嬶細宸叉秷鍖?
        if (_sliderDragging)
        {
            _sliderDragging = false;
            return true;
        }

        // ---- 瑙︽懜/绗旈暱鎸夌殑鏀跺熬锛?026-10-02 绗簩杞級----
        if (_tipHoldFired)
        {
            // 闀挎寜宸茬粡寮硅繃鎻愮ず锛氳繖涓€娆℃澗鎵?*涓嶆墽琛屼换浣曞姛鑳?*锛圵indows/Material 鎯緥锛?            // 闀挎寜鏄?鐪?涓嶆槸"鐐?锛夛紱鎻愮ず缁х画鍋滅暀 TipLingerMs 鍚庤嚜鍔ㄦ敹銆?            CancelTipHold();
            _tipLingerUntil = _host.NowMs + Tokens.TipLingerMs;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
            Invalidate();
            return true;
        }
        if (_press == -1 && _tipHoldStart > double.NegativeInfinity)
        {
            // "鎸変笅鍗崇敓鏁?鐨勯偅鍑犳牸锛岃Е鎽?绗旀敼鎴?*鎶捣鎵嶇敓鏁?*锛堜腑閫旈暱鎸夊嚭鎻愮ず鍒欎笉鐢熸晥锛夛細
            // 铏氬疄绾挎牸銆佸浘褰㈡銆佸叧闂櫧鏉?鉁曘€?            int target = _tipHoldTarget;
            CancelTipHold();
            if (target == 500) CycleDash();
            else if (target == 400)
            {
                if (_host.State.Board)
                {
                    _host.Commands.SetBoard(false);
                    _actionFlashUntil = _host.NowMs + 260;
                }
            }
            else if (target >= 200 && target < 200 + BandSegmentCount)
                ActivateSegment(target - 200);
            Invalidate();
            return true;
        }
        CancelTipHold();        // 宸ュ叿鏍奸偅鏉¤矾锛氬€欓€変綔搴燂紝鍘熸牱璧版姮璧锋墽琛?
        int idx = _press;
        bool dragged = _dragging;
        _press = -1;
        _dragging = false;
        if (idx == -1) return false;

        if (dragged)
        {
            // **鏉炬墜涓嶅惛闄?*锛堢敤鎴?2026-09-30锛?鎴戯紙璇寸殑锛夊惛闄勬槸姣斿鎷栧埌浠诲姟鏍忎笅銆佸畠鑷姩闈犲簳杈癸紝
            // 杩欑鐨勪笉鐢ㄤ簡"锛夛細鎷栧埌鍝効灏卞仠鍦ㄥ摢鍎裤€?            // 鎷栧姩杩囩▼鏈韩宸茬粡鎶婂畠澶瑰湪灞忓箷鍐咃紙姣忔璇?`Anchor()` 閮借繃 `Clamp`锛夛紝
            // 鎵€浠?鎷栧嚭鍘绘壘涓嶅洖鏉?杩欐潯搴曠嚎浠嶇劧鍦紱鍙槸涓嶅啀鑷姩璐磋竟銆?            // 璐磋竟**闅愯棌**鏄彟涓€鍥炰簨锛氬彧璁ゅ簳杈广€佺搴曡竟澶熻繎鎵嶈Е鍙戯紙瑙?Shift锛夈€?            return true;
        }

        // 鎸変綇娓呯┖锛氭澗鎵嬪嵆鍙栨秷锛堝 0.8 绉掔殑閭ｄ竴娆″凡缁忓湪 UpdateBandAction 閲屾墽琛岃繃浜嗭級
        if (idx == 2000)
        {
            _actionHoldFrom = double.NegativeInfinity;
            Invalidate();
            return true;
        }
        if (idx == -2) { Toggle(); return true; }        // 鐐圭悆锛氬睍寮€
        if (idx == 0) { Toggle(); return true; }         // 鐐瑰甫瀛愭渶宸﹂偅鏍硷細鏀惰捣
        Activate(idx);
        return true;
    }

    private int HitCell(float x, float y)
    {
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++)
            if (CellRect(k).Contains(x, y)) return vis[k];      // 杩旇繕瀹屾暣妗ｇ殑涓嬫爣
        return -1;
    }

    private void Toggle()
    {
        bool collapse = _expand.Value > 0.5f;
        // 绯荤粺鍏虫帀鍔ㄧ敾鏃剁洿鎺ヨ烦缁堟€侊紙鏁欏閲岃€佹満鍣ㄤ笂寰堝父瑙侊級
        _expand.To(collapse ? 0f : 1f, collapse ? Tokens.CollapseMs : Tokens.ExpandMs);

        // **鏀惰捣鏃跺繀椤绘妸涓存椂鐘舵€佹竻骞插噣**锛氶潰鏉裤€佹偓鍋溿€佹寜涓嬬殑鏍笺€佸湪鎷栫殑婊戞潯銆?        // 涓嶆竻鐨勮瘽锛岀缉鍥炰竴涓悆涔嬪悗閭ｄ簺鐘舵€佽繕鎸傚湪閭ｅ効锛屼笅娆″睍寮€鏃朵細鑷繁鍐掑嚭鏉?        //锛堝亣闈㈡澘褰撳勾灏辨槸鏍藉湪杩欐潯涓婏細"鐐规洿澶氣啋鐐规敹璧封啋鍐嶅睍寮€锛屾娊灞夎嚜宸卞啋鍑烘潵"锛夈€?        if (collapse)
        {
            // 闈㈡澘鐞嗚涓婃墦涓嶅紑锛堝畠瑕佷富鏉″睍寮€鎵嶆湁鍏ュ彛锛夛紝浣嗙姸鎬佹満鏀跺彛鏃朵竴骞舵竻鎺夛紝
            // 鍏嶅緱浠ュ悗鍝ぉ鍔犱簡鍒殑鍏ュ彛锛岀缉鐞冩椂闈㈡澘杩樻寕鍦ㄩ偅鍎裤€?            _moreOpen = false;
            _more.Jump(0f);
            _moreHover = MoreHitNone;
            _morePress = -1;
            _hover = -1;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
            CancelTipHold();
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
        }
        Invalidate();
    }

    /// <summary>
    /// 鐐规煇涓€鏍笺€?*鍙蛋鍛戒护閫氶亾**锛氱晫闈笉璁哥洿鎺ユ敼寮曟搸鐘舵€侊紙鎾ら攢鏍堛€佺┖闂寸储寮曘€?    /// 鑴忓尯閮介潬寮曟搸缁存姢锛岃秺鏉冨氨浼氱牬鍧忚繖浜涗笉鍙橀噺锛夈€?    /// </summary>
    private void Activate(int idx)
    {
        var cmd = _host.Commands;
        var st = _host.State;

        // **鐐逛箣鍓?*涓婂甫鍋滃湪鍝竴鏍笺€傜瑪 / 鑽у厜绗旈偅涓€鏍艰鐢ㄥ畠鍒?杩欎竴涓嬫槸鍒囪壊銆佽繕鏄彧鏄妸璁剧疆鏉℃嬁杩囨潵"
        //锛坄_bandCell` 鍦ㄤ笅闈?`HasBand` 閭ｄ竴鍧楅噷浼氳鏀规垚 idx锛屾敼瀹屽氨闂笉鍑?鍘熸潵鍦ㄥ摢"浜嗭級銆?        int prevBand = _bandCell;
        // **鐐逛箣鍓嶆槸涓嶆槸鍦ㄧ┛閫?*锛氱┛閫忎笅鐐圭瑪 / 鑽у厜绗旀牸 = "鎴戣鍥炴潵鍐欏瓧"锛岃繖涓€娆?*涓嶈椤烘墜鎹㈣壊**
        //锛堝拰閿洏 Ctrl+P 鍚屼竴鏉★細绌块€忓厛閫€鍑恒€佺浜屾鎵嶈皥鎹㈣壊銆?026-10-05 鐢ㄦ埛鎶ョ殑 bug锛夈€?        bool wasPassThrough = st.PassThrough;

        // **鐐逛簡鍒殑鏍煎瓙 = 閫夋嫨鏍奸偅娆?鍙屽嚮"搴忓垪鍒版涓烘**銆?        // 涓嶈繖涔堝仛鐨勮瘽锛?閫夋嫨鏍?鈫掞紙200ms锛夌瑪鏍?鈫掞紙200ms锛夐€夋嫨鏍?浼氳绠楁垚瀵归€夋嫨鏍肩殑鍙屽嚮锛?        // 浜庢槸鑰佸笀鍙槸鍦ㄤ袱涓牸瀛愪箣闂存潵鍥炵湅涓€鐪硷紝灏辫鍏ㄩ€変簡銆?        if (idx != 7) _lastSelCellClickMs = double.NegativeInfinity;

        // 鐐瑰伐鍏锋牸鏃讹紝涓婂甫璺熺潃鎹㈡垚杩欎釜宸ュ叿鐨勮缃紙"涓婂甫锛濊繖涓寜閽殑璁剧疆鏉?锛夛紝
        // 骞朵笖**閽変綇灞曞紑**鈥斺€斾笉閽夌殑璇濇寚閽堜竴绉诲紑灏辨敹浜嗭紝閫夐」鏉ヤ笉鍙婇€夈€?        //
        // 鍐嶇偣涓€娆?*鍚屼竴涓?*宸ュ叿 = 鏀惰捣瀹冭嚜宸辩殑璁剧疆鏉★紙鍋囬潰鏉跨殑鐢ㄦ硶锛屼篃鏄悇瀹堕€氫緥锛夈€?        if (HasBand(idx))
        {
            // 璁剧疆鏉℃樉绀?*杩欎竴鏍肩殑璁剧疆**锛氱偣鐧芥澘灏辩湅鏉胯壊锛嬬炕椤碉紝鐐规鐨氨鐪嬫暣绗旀摝/闈㈢Н鎿︺€?            //
            // 杩欓噷浠ュ墠杩樻湁涓€濂?鐐逛竴娆￠拤浣忋€佸啀鐐瑰悓涓€涓伐鍏锋敹璧?鐨勭姸鎬侊紙鐓у亣闈㈡澘鎶勭殑锛夈€?            // 2026-09-17 鐢ㄦ埛瀹氫簡鏂扮殑璇箟锛?*寮犱笉寮犲紑鍙湅鐒︾偣鍦ㄤ笉鍦ㄩ潰鏉夸笂**
            // 锛堣 RailHoverZone锛夈€備簬鏄?鍐嶇偣涓€娆℃敹璧?杩欎竴鏀繀椤诲垹鎺夆€斺€?            // 鎸囬拡杩樺仠鍦ㄩ潰鏉夸笂锛屾敹涓嬪幓浼氱珛鍒诲張寮犲紑锛屾槸涓や釜瑙勫垯鎵撴灦銆?            // 鎸囬拡涓€绂诲紑闈㈡澘瀹冭嚜宸卞氨鏀讹紙璧?800 姣鐨勯€€鍑哄欢杩燂級锛屼笉鐢ㄨ€佸笀鍐嶇偣涓€娆°€?            //
            // 2026-10-09 鏂规 A鈶★細鐐瑰伐鍏锋牸 = **閭ｆ牸鐨勮缃潯绔嬪嵆寮犲紑**锛堟槑纭姩浣滅珛鍗冲搷搴旓紝
            // 瑙?璋冪爺-鑹插甫-鍏ㄧ洏澶嶆煡涓庝紭鍖?md锛夈€傛棫浠ｇ爜杩欓噷鎶?`_railEnterAtMs` 閲嶇疆鎴?-鈭烇紝
            // 娉ㄩ噴鍐?涓嶇敤鍐嶇瓑寮€闂ㄩ偅 120 姣"锛屽疄闄呮晥鏋滃嵈鏄?*鍊掕鏃朵粠澶村啀鏉?*鈥斺€?            // 蹇€熺偣鏍煎弽鑰屾瘮鍙偓鍋滄洿鏅氫寒锛屼笌娉ㄩ噴鎰忓浘鐩稿弽銆?            _bandCell = idx;
            _railEnterAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
        }

        switch (idx)
        {
            case 1: cmd.SetPassThrough(!st.PassThrough); break;
            // 鐧芥澘閭ｄ竴鏍硷細**鐐逛笁涓嬫槸涓€涓潵鍥?*锛堢敤鎴?2026-09-27 绗笁娆″畾鐨勶級銆?            //
            //   鈶?鏉垮叧鐫€               鈫?寮€鏉匡紙椤烘墜鎶婅缃潯鎷胯繃鏉ワ級
            //   鈶?鏉垮紑鐫€銆佽壊甯﹀湪鍒     鈫?**鍙妸鑹插甫鎷胯繃鏉ワ紝鏉夸竴鍔ㄤ笉鍔?*
            //   鈶?鏉垮紑鐫€銆佽壊甯︿篃鍦ㄨ繖涓€鏍?鈫?**鍏虫澘**
            //   鈶?鍐嶇偣涓€涓?鈫?鍙堝洖鍒?鈶狅紙寮€鏉匡級锛屽線鍚庡氨杩欎笁涓嬪惊鐜?            //
            // 涓轰粈涔堣鏀癸紙鐢ㄦ埛鍘熻瘽锛?閭ｄ釜鐧芥澘鎴戣瘯浜嗗嚑閬嶏紝鎰熻鍗曠嫭寮勪竴涓紑鍏宠繕鏄笉涔犳儻"锛夛細
            // 涓婁竴鐗堟妸"鍏虫澘"鎸幓浜嗚缃潯鏈€鍙崇鐨?鉁曪紝鏍煎瓙涓婃€庝箞鐐归兘鍏充笉鎺夆€斺€旂敤璧锋潵
            // 鍙嶈€屽埆鎵€傜幇鍦ㄨ繖涓€涓牸瀛愯嚜宸卞氨鏄偅涓紑鍏筹紝鉁?鐣欑潃褰?涓€鐪肩湅寰楄鐨勫叧鏉块敭"銆?            //
            // 鍚屾椂**鍘绘帀浜?鍐嶇偣涓€涓嬫崲涓嬩竴涓澘鑹?**锛堢敤鎴凤細"鍗曞嚮鍒囨崲涓婇潰鐨勭櫧鏉块鑹诧紝
            // 鎴戞劅瑙変笉闇€瑕佷簡"锛夛細鎹㈡澘鑹茶壊甯︿笂灏辨憜鐫€涓夋牸锛堢櫧/缁?榛戯級锛屼竴鐐瑰氨鍒帮紱
            // 鑰岃繖涓€涓牸瀛愬埌搴曟槸"寮€"杩樻槸"鍏?鎵嶆槸澶у鐐瑰畠鐨勬湰鎰忋€?            //
            // 鈶?閭ｆ潯鍒ゆ嵁蹇呴』甯?`prevBand == 2`锛氳€佸笀浠庡浘褰㈤潰鏉跨偣鍥炵櫧鏉块偅涓€鏍硷紝
            // 鎰忔€濇槸"鎶婃澘鎷垮洖鏉ョ敤"锛岄偅鏃跺€欎笉璇ラ『鎵嬫妸瀹冨叧鎺夛紙鍚岀瑪 / 鑽у厜绗?/ 鍥惧舰閭ｅ嚑鏍硷級銆?            case 2:
                // "涓ゅ紶绾?妯″瀷锛?026-10-09 鐢ㄦ埛瀹氾級锛氱櫧鏉跨敾鍦ㄦ枃妗ｄ笂闈€傛枃妗ｅ紑鐫€鏃惰繖涓€鏍煎氨鏄?                // **寮€鏉?鍏虫澘**鈥斺€斿紑鏉匡紳鐧芥澘鐩栦綇 PDF锛堥〉鍥?鏉¤浣嶏級锛屽叧鏉匡紳鍥炲埌鏂囨。銆?                // 鎵撳紑鏂囨。鐨勯偅涓€涓嬩細鎶婄櫧鏉胯嚜鍔ㄦ敹鎺夛紙鍏堣浣犵湅瑙佹枃妗ｏ紝瑙?DocImport.StartDocView锛夛紝
                // 鎵€浠ュ垰寮€瀹屾枃妗ｈ繖閲屽繀鐒舵槸"鐏?鐨勶紝鐐逛竴涓嬫墠鎶婄櫧鏉块摵涓婃潵銆?                if (!st.Board) cmd.SetBoard(true);             // 鈶?寮€鏉?                else if (prevBand == 2) cmd.SetBoard(false);   // 鈶?鍏虫澘
                break;                                         // 鈶?鍙妸鑹插甫鎷胯繃鏉ワ紝鏉夸笉鍔?            // 绗?/ 鑽у厜绗旈偅涓ゆ牸锛?*宸茬粡鏄畠銆佽€屼笖鑹插甫鏈潵灏卞湪杩欎竴鏍?鈫?鎹笅涓€涓鑹?*
            //锛堢敤鎴?2026-09-27 瀹氱殑"宸茬粡鏄畠浜嗭紝鐐瑰嚮鍒囨崲棰滆壊"锛夈€?            //
            // 鈿?鍒ゆ嵁蹇呴』甯︿笂鍓嶄竴鎻?鑹插甫鏈潵灏卞湪杩欎竴鏍?锛氳€佸笀浠庡浘褰㈤潰鏉跨偣鍥炵瑪閭ｄ竴鏍硷紝
            //   鎰忔€濇槸"鎶婄瑪鎷垮洖鏉?锛岄偅鏃跺€欎笉鑳芥倓鎮勬妸棰滆壊涔熸崲浜嗭紱鑰岃€佸笀涓€鐩村湪鐢ㄧ瑪鏃?            //   鑹插甫鏈潵灏卞仠鍦ㄧ瑪閭ｄ竴鏍硷紝杩炵偣灏辨槸杩炵潃鎹㈣壊鈥斺€旀濂芥槸鎯宠鐨勬墜鎰熴€?            //
            // 鈿?**鍒囪壊閭ｆ潯璺笂涔熻璧颁竴娆?`SetTool`**锛氬紩鎿庣殑 `SwitchTool` 閲岃繕鍏肩潃
            //   "椤烘墜鎶婄┛閫忓叧鎺?锛堢┛閫忓拰宸ュ叿浜掓枼锛岃 Engine.SwitchTool锛夈€傛紡浜嗗畠灏变細鍑虹幇
            //   "鐐圭瑪鏍兼崲浜嗚壊銆佷絾杩樺湪绌块€?鈥斺€旇嚜妫€閲?鐐瑰伐鍏锋牸锛濋『鎵嬪叧鎺夌┛閫?閭ｄ袱鏉″綋鍦哄氨绾€?            //   `SetTool` 鏄箓绛夌殑锛堝伐鍏锋病鍙樻椂鍙仛娓呯悊锛夛紝閲嶅璋冩病鏈夊壇浣滅敤銆?            case 3:
                cmd.SetTool(Tool.Pen);
                if (prevBand == 3 && !wasPassThrough) CycleColor();
                break;
            case 4:
                cmd.SetTool(Tool.Highlighter);
                if (prevBand == 4 && !wasPassThrough) CycleColor();
                break;
            case 5: cmd.SetTool(Tool.Laser); break;
            case 6:
                // [2026-10-05 鐢ㄦ埛瀹歖 姗＄毊瀛愮被鍨嬩笉鍐?鐐逛竴涓嬫崲涓€娆?锛氱偣杩欎竴鏍?= 杩涙鐨?                //锛堜笂涓€娆＄敤鏁寸瑪灏辨暣绗斻€佺敤闈㈢Н灏遍潰绉級锛涙暣绗?闈㈢Н鍘讳笂甯﹂偅涓ゆ閲岄€夈€?                cmd.SetEraserPreferred();
                break;
            case 7:
                // 閫夋嫨閭ｄ竴鏍硷細**涓嶅啀"鍐嶇偣涓€涓嬫崲妗?**锛堢煩褰?濂楃储鍘讳笂甯﹂偅涓ゆ閲岄€夛紝2026-10-05锛夈€?                //
                // **鍙屽嚮 = 鍏ㄩ€?*锛?00ms 鍐呬袱鍑伙紝鐓?InkClass 閭ｄ釜缁忓吀浜や簰锛夈€?                // 娌℃湁"鍐嶇偣鎹㈡。"涔嬪悗锛屼袱鍑荤殑鍑€鏁堟灉灏辨槸"閫変腑宸ュ叿 + 鍏ㄩ€?鈥斺€旀瘮鍘熸潵鏇寸洿鐧姐€?                if (_host.NowMs - _lastSelCellClickMs < SelectDoubleClickMs)
                {
                    _lastSelCellClickMs = double.NegativeInfinity;   // 杩欎竴娆″簭鍒楀埌姝や负姝?                    cmd.SetTool(Tool.Marquee);
                    cmd.SetSelectMode(_selModeBeforeDoubleClick);
                    cmd.SelectAll();
                    // 闂竴涓嬶細闂殑鏄壊甯︿笂閭ｄ釜銆屽叏閫夈€嶆寜閽紙鍜岀偣瀹冭嚜宸变竴鏍凤紝
                    // 瑙佽皟鐮?鐣岄潰-涓婁笅鏂囪缃潯.md 閲?鐐逛竴涓?鈫?鍏ㄩ儴杩涢€変腑妗?鈫?鎸夐挳闂竴涓?锛夈€?                    _actionFlashUntil = _host.NowMs + 260;
                    break;
                }
                _lastSelCellClickMs = _host.NowMs;
                _selModeBeforeDoubleClick = st.SelectMode;           // 璁颁笅"鍙屽嚮鍓?鐨勬。
                cmd.SetTool(Tool.Marquee);
                break;
            case 8:
                // 涓冪鍥惧舰涔嬪悗**涓嶈兘鍐?涓ゆ。瀵瑰垏"**浜嗭紙浠ュ墠鏄洿绾?鈫?鐭╁舰锛夈€?                //
                // 鐜板湪鐨勮鍒欙細宸茬粡鏄浘褰㈠伐鍏峰氨**涓嶅姩宸ュ叿**鈥斺€旂敤鎴峰彧鏄兂鐪嬩笂甯︼紙鎸囬拡灏卞湪
                // 闈㈡澘涓婏紝涓嬩竴鎵嬬偣鍝竴娈甸兘琛岋級锛涘伔鍋锋崲鎴愮煩褰㈡槸鏈€姘斾汉鐨勶紝鐢诲埌涓€鍗婄殑
                // 涓夎褰細琚崲鎺夈€備笉鏄浘褰㈠伐鍏锋墠缁欎竴涓粯璁ょ绫伙紙鐭╁舰锛屾部鐢ㄨ€佽涓猴級銆?                if (!HasShapeEntry(st.Tool)) cmd.SetTool(Tool.Rectangle);
                break;
            case 9: cmd.SetTool(Tool.Capture); break;
            case 10: cmd.Undo(); break;
            case 11: cmd.Redo(); break;
            case 12:
                // 銆屾洿澶氥€嶏細鎵撳紑灞忓箷涓ぎ鐨勯潰鏉匡紙2026-10-01 璧凤紱鎶藉眽浠庝骇鍝佽矾寰勯€€鍦猴級
                OpenMore();
                break;
        }
        Invalidate();
    }

    /// <summary>鍝簺鏍煎瓙鏈変笂甯︺€傛病鏈夌殑锛堝悗鎾?閲嶅仛/鏇村/鎴睆锛夌偣浜嗕笉闀垮嚭涓€鏉＄┖甯﹀瓙銆?/summary>
    private static bool HasBand(int cell) => cell is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9;

    /// <summary>褰撳墠宸ュ叿瀵瑰簲鐨勬牸瀛愨€斺€旈敭鐩樻崲宸ュ叿鏃剁敤瀹冩妸涓婂甫鎺板洖鏉ャ€?/summary>
    private static int CellForTool(Tool t)
    {
        // 涓冪鍥惧舰鍏辩敤绗?8 鏍硷紙鍝竴绉嶆槸鍝竴娈电敱涓婂甫閲岀殑楂樹寒鏍囧嚭鏉ワ級銆?        // 鍒ゆ嵁璧?HasShapeEntry 鑰屼笉鏄啀鍒椾竴閬嶅浘褰㈠悕瀛楋細鍔犱竴绉嶅浘褰㈠彧鏀?ShapeRows銆?        if (HasShapeEntry(t)) return 8;
        return t switch
        {
            Tool.Highlighter => 4,
            Tool.Laser => 5,
            Tool.Eraser or Tool.PixelEraser => 6,
            Tool.Marquee => 7,
            Tool.Capture => 9,
            _ => 3,
        };
    }

    public void OnStateChanged(in UiState state)
    {
        // 鍥炴斁涓€寮€濮嬶細鎶娿€屾洿澶氥€嶉潰鏉挎敹鎺夈€傚畠鏄?*鍏ㄥ睆妯℃€?*锛屽紑鐫€浼氭尅浣忔帶鍒舵潯鍜岀敾甯冿紱
        // 鑰屽紩鎿庨偅杈?鐐圭晫闈?= 鍏堥€€鍑哄洖鏀?鈥斺€斾笉鏀舵帀鐨勮瘽锛岃€佸笀鎯冲叧闈㈡澘閭ｄ竴鍒诲洖鏀惧氨娌′簡銆?        if (state.ReplayActive && _moreOpen) CloseMore();

        // **宸ュ叿鍙樹簡灏辫窡鐫€鎹笂甯?*锛堥敭鐩樼儹閿槸鑰佸笀鏇村父鐢ㄧ殑閭ｆ潯璺級銆?        // 鍒ゆ嵁鏄?宸ュ叿鐪熺殑鎹簡"锛屼笉鏄?褰撳墠甯﹀瓙瀵逛笉瀵?锛氱偣鐧芥澘涔嬪悗甯﹀瓙鏄剧ず鐨勬槸鏉胯壊锛?        // 閭ｆ椂宸ュ叿娌″彉銆佸甫瀛愪篃涓嶈琚幇璧帮紱鑰屼竴鏃︽崲鎴愬埆鐨勫伐鍏凤紝甯﹀瓙蹇呴』璺熶笂锛?        // 涓嶇劧浼氬嚭鐜?鎵嬮噷鏄鐨€佷笂甯﹁繕鏄壊鏉?銆?        if (state.Tool != _lastTool)
        {
            _lastTool = state.Tool;
            int cell = CellForTool(state.Tool);
            if (HasBand(cell)) _bandCell = cell;
        }

        // ---- 绌块€忓紑鐨勯偅涓€鍒伙細涓婂甫鏀跺洖閭ｆ潯 10 鍍忕礌鑹茬嚎锛?026-10-02 鐢ㄦ埛鍙ｅ緞锛?---
        //
        // 鐢ㄦ埛鍘熻瘽锛堢浜屾婢勬竻锛夛細"鐐瑰嚮绌块€忎互鍚庯紝鑹插甫鏄í璧锋潵鐨勶紙鏀惰捣鏉ワ級銆備笉鏄鎴戠偣浜嗕釜
        // 绌块€忥紝鑹插甫灏卞畬鍏ㄦ病鏈変簡銆傛垜璇寸殑鑹插甫娑堝け锛屽氨鏄妸瀹冩姌鍙犺捣鏉ワ紝鑰屼笉鏄儚鍏朵粬涓€鏍凤紝
        // 鐐硅繃鏉ヤ互鍚庤繕鏄睍寮€鐨勩€?鈥斺€旀墍浠ワ細**闈㈡澘楂樺害涓嶅彉**锛堣创杈归殣钘忛湶鍑烘潵鐨勮繕鏄偅鏉＄嚎锛夛紝
        // 鍙槸鎶婅缃潯鏀跺洖鍘汇€佽€屼笖绌块€忔湡闂翠笉璁稿啀寮犲紑锛堟病鏈夎缃彲鏀撅級銆?        // **涓嶅姩 `_bandCell`**锛氶€€鍑哄悗甯﹀瓙杩樻槸鍥炲埌"鍒氭墠閭ｄ釜宸ュ叿"閭ｄ竴鏍尖€斺€旇€佸笀鍥炲埌鍒氭墠鐨勬椿銆?        //
        // "鎸囬拡杩樺仠鍦ㄩ潰鏉夸笂"涔熷繀椤绘敹锛氳€佸笀鍒氱偣鐨勫氨鏄┛閫忔牸锛屼笉娓呮偓鍋滄剰鍥剧殑璇濓紝120 姣鍚?        // 鏃ц缃潯鍙堣嚜宸卞脊寮€锛堣嚜妫€閲岄拤鐫€瀹冿級銆?        if (state.PassThrough != _lastPass)
        {
            _lastPass = state.PassThrough;
            if (state.PassThrough)
            {
                _railHover = false;
                _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
                _rail.To(0f, Tokens.RailMs);     // 璁剧疆鏉?鈫?閭ｆ潯 10 鍍忕礌鑹茬嚎
                Invalidate();
            }
        }

        // ---- 杩涙斁鏄犵殑閭ｄ竴鍒伙細**灞曞紑 + 鍥炲埌榛樿浣嶇疆**锛堢敤鎴?2026-09-27 瀹氾級----
        //
        // 鐢ㄦ埛鍘熻瘽锛?PPT 寮€濮嬫挱鏀句互鍚庯紝鎶婂畠鎵撳紑鏀惧埌灞呬腑闈犲簳閮ㄣ€?
        // 浠栬鐨勬槸"涓€鏀剧墖锛岀瑪灏辫嚜宸卞嚭鏉?鈥斺€旇€佸笀涓婅鐨勭涓€浠朵簨寰€寰€鏄嬁璧风瑪锛?        // 涓嶈鍏堝幓鎵鹃偅棰楃悆銆佹垨鑰呭幓鎶婁笂娆℃嫋鍒拌钀界殑闈㈡澘鎷藉洖鏉ャ€?        //
        // 鈿?**鍙湪杈规部鍋氫竴娆?*锛坄!state.PptMode` 涔嬪鐨勯偅涓€涓嬶級锛氳繖涓€娈佃鏄啓鎴?        // "鏀炬槧涓氨褰掍綅"锛岄偅涔堜粬鏀炬槧涓妸闈㈡澘鎷栧埌涓€杈广€佺炕涓€椤碉紙鐘舵€佸彉鍖?鈫?鍙堣蛋杩欓噷锛?        // 灏变細琚?*鎷藉洖搴曢儴灞呬腑**鈥斺€旈偅鏄瘮"涓嶅綊浣?鏇寸儲浜虹殑琛屼负銆?        // 鈿?**閫€鍑烘斁鏄犱笉杩樺師**锛堢敤鎴?2026-09-27 閫夌殑锛夛細闈㈡澘淇濇寔灞曞紑銆佷綅缃笉鍔ㄣ€?        // 鐞嗙敱鏄粬鍒氱敤瀹岀瑪锛屽洖妗岄潰杩樿鎺ョ潃鍐欐澘涔︼紝鍐嶆敹璧锋潵绛変簬澶氫竴姝ャ€?        if (state.PptMode && !_lastPpt)
        {
            _anchor = null;                       // 鍥為粯璁や綅缃紙宸ヤ綔鍖哄簳杈瑰眳涓級
            _peek.Jump(1f);                       // 璐磋竟闅愯棌寮€鐫€鐨勮瘽锛屼篃鍏堝畬鏁撮湶鍑烘潵
            _leftAtMs = _host.NowMs;              // 杩欐"闇查潰"閲嶆柊璁＄寮€鏃堕棿
            if (_expand.Value < 0.5f) _expand.To(1f, Tokens.ExpandMs);
            else _expand.Jump(1f);
        }
        _lastPpt = state.PptMode;

        Invalidate();
    }

    // ---- 缁樺埗 ---------------------------------------------------------------

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null) return;
        UpdatePeek();                    // 姣忓抚闂竴娆?璇ヤ笉璇ユ敹璧锋潵"锛堣创杈归殣钘忥級
        UpdateRail();                    // 鑹茬嚎璇ヤ笉璇ラ暱鎴愯缃潯
        UpdateBandAction();              // "鎸変綇娓呯┖"澶?0.8 绉掓病鏈夛紙姣忓抚鎺ㄨ繘锛?        UpdateTip();                     // 鎮仠鎻愮ず"鍒扮偣娌℃湁"锛堟瘡甯ф帹杩涳級
        RenderShifted(ctx);
    }

    private void RenderShifted(ID2D1DeviceContext ctx)
    {
        var saved = ctx.Transform;
        var shift = Shift();
        if (shift != Vector2.Zero)
            ctx.Transform = Matrix3x2.CreateTranslation(shift) * saved;
        try
        {
            RenderCore(ctx);
            DrawTip(ctx);               // 鎮仠鎻愮ず鐢诲湪鏈€涓婇潰锛堜富鏉?甯﹀瓙/銆屾洿澶氥€嶉潰鏉夸箣涓婏級
        }
        finally
        {
            ctx.Transform = saved;
        }
    }

    private void RenderCore(ID2D1DeviceContext ctx)
    {
        float e = _expand.Value;
        var bar = BarRect();
        var panel = UnionRect();

        // **鏁村潡闈㈡澘涓€寮犲崱鐗?*锛堜富鏉?锛?鑹插甫鍏辩敤涓€涓渾瑙掞級锛氬渾瑙掗殢灞曞紑浠?24 鏀跺埌 18锛?        // 鍜屽亣闈㈡澘涓€鏍凤紙瀹冨啓鐨勬槸 L.Radius = 24 - 6 * e锛夈€?        float radius = Tokens.PillRadius(Tokens.BarHeight) - 6f * e;
        // **鏀惰捣鎬佽创杈归殣钘忔椂锛岀悆鏈韩閭ｅ紶鍗＄墖涓嶇敾**锛氱敾浜嗙殑璇濆畠鐨勫渾鍜屾姇褰变細涓€璧烽湶鍑烘潵锛?        // 鎶婃墜涓嬮潰杩樺帇鐫€涓€鍥紙闇插嚭 8 鍍忕礌鏃剁湅寰楄閭ｄ竴鐗囧姬鈥斺€旀鏄鏀舵嬀鐨勯偅涓鎰燂級銆?        // 杩欎竴鏉″繀椤诲湪杩欓噷鍒わ細涓嬮潰閭ｄ釜 `e < 0.5` 鍒嗘敮閲屽啀 return 宸茬粡鏅氫簡銆?        if (!(e < 0.5f && PeekTabShown)) DrawCard(ctx, panel, radius);

        var st = _host.State;

        // 璐磋竟闅愯棌娌変笅鍘讳箣鍚庯紙闇插ご宸茬粡鍙墿鍗佹潵涓儚绱狅級锛?*涓嶅啀闇茬悆鐨勯偅涓€灏忕墖寮?*锛?        // 鏀规垚涓€鏉＄洿鐨勬妸鎵嬶紙瑙?Tokens.PeekTab锛氶偅涓€鐗囧姬鐪嬬潃鍍忔畫褰憋級銆?        // 鎹㈢殑鏃舵満瑙?PeekTabShown锛氬啀鏃╂崲锛岀悆閲岀殑鑹茬幆鍜岀瑪鍥炬爣浼氭彁鍓嶆秷澶便€?        if (e < 0.5f && PeekTabShown)
        {
            DrawPeekTab(ctx);
            return;
        }

        // ---- 鍐呭鏄?*涓ゆ寮?*鐨勶細褰㈢姸鍏堥暱锛屽唴瀹瑰啀浜ゅ弶鎹?----
        //
        // 鍘熸潵鏄湪 e = 0.5 涓婄‖鍒囷紙鐞冨湪 e<0.5 鐢汇€?3 鏍煎湪 e鈮?.5 鐢伙級銆傚嚭鍥惧綋鍦虹湅鍑轰袱涓瘺鐥?        // 锛坄--panelshow <鍥? --expand 0.15` / `0.5`锛夛細
        //   鈶?鑹茬幆鍜岀瑪鍥炬爣鏄寜**闈㈡澘鐨勪竴鍗婂**绠楃殑锛岄潰鏉夸竴闀垮畠浠氨琚拺鎴愪竴鏁翠釜澶у浘鏍囷紝
        //      鑰屼笖杩樿窡鐫€闈㈡澘涓績寰€鍙宠窇锛?        //   鈶?纭垏閭ｄ竴涓?13 鏍?鍟?鍦板嚭鐜帮紝鑰屼笖**娌¤闈㈡澘瑁佷綇**鈥斺€斿彸杈瑰嚑涓伐鍏疯窇鍒伴潰鏉垮闈€?        // 鐜板湪锛氬舰鐘剁収鏃ф寜 e 闀匡紙200ms銆乪ase-out锛夛紝
        //   鐞冪殑鍐呭锛歟 0.05鈫?.45 娣″嚭锛? 13 鏍硷細e 0.45鈫?.80 娣″叆銆?        // 涓棿閭ｆ涓よ€呭悓鏃跺湪锛屼綅缃篃鏄悓涓€涓紙鐞冮偅涓€鏍硷級锛岃璧锋潵灏辨槸"鐞冩憡寮€鎴愭牸瀛?銆?        float ballA = 1f - Smooth01((e - 0.05f) / 0.40f);
        float cellsA = Smooth01((e - 0.45f) / 0.35f);

        if (ballA > 0.004f) DrawBallContent(ctx, st, e, ballA);

        // 鑹插甫閭ｆ潯**鍑规Ы**锛氭妸鑹插甫閭ｄ竴鍧楄鍑烘潵銆佸～涓€灞傛贰娣＄殑鏆楄壊锛堢収鍋囬潰鏉匡細
        // 瑁佽繘闈㈡澘鐨勫渾瑙掑舰鐘讹紝椤堕儴涓や釜瑙掕嚜鐒惰窡鐫€鍦嗭級銆?        var band = BandRect();
        if (cellsA > 0.004f)
        {
            // 褰㈠彉鏈熼棿**鎶婂唴瀹硅杩涢潰鏉?*锛氶潰鏉胯繕娌￠暱鍒板叏瀹芥椂锛屽彸杈归偅鍑犳牸浼氭暟鍒伴潰鏉垮闈㈠幓銆?            // 骞虫椂锛坋鈮?锛変竴娆″浣欑殑瑁佸壀閮戒笉鍋氥€?            bool clip = e < 0.999f;
            if (clip)
                ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY),
                                        AntialiasMode.Aliased);
            // 娣″叆鏁村眰鍋氾紙涓嶉€忔槑搴﹀浘灞傦級锛岃€屼笉鏄妸 alpha 涓€璺紶杩涙瘡涓粯鍒跺嚱鏁帮細
            // 瑕佹贰鐨勪笢瑗挎湁鍑犲崄澶勶紙13 鏍肩殑鍥炬爣銆佺粍鍒嗛殧绾裤€佽壊鐗囥€佹粦鏉♀€︼級锛屼紶 alpha 寰楁敼鍗佸嚑澶勭鍚嶃€?            bool layered = cellsA < 0.996f && BeginFade(ctx, cellsA);
            DrawExpandedContent(ctx, st, panel, band, radius);
            if (layered) EndFade(ctx);
            if (clip) ctx.PopAxisAlignedClip();
        }

        // 闈㈡澘涓?涓嬫部閭ｉ亾 1 鍍忕礌鐨?鏀惰竟"銆?*涓や釜涓婚璧扮浉鍙嶇殑鏂瑰悜**锛堢悊鐢辫 Tokens锛夛細
        //   娣辫壊 鈫?椤舵部涓€閬撴瀬娣＄殑鐧介珮鍏夛紙璇?鎺ュ厜"锛夛紱
        //   娴呰壊 鈫?搴曟部涓€閬撴瀬娣＄殑鏆楃嚎锛堣"鍗疯竟"锛夈€傜櫧楂樺厜鐢诲湪鐧介潰鏉夸笂绛変簬娌＄敾銆?        //
        // 鐢绘硶鏄?瑁佸嚭涓€鏉?1.5 鍍忕礌楂樼殑妯甫銆佸甫瀛愰噷鎻忎竴閬嶅渾瑙掔煩褰?锛氳繖鏍风嚎鍒颁袱绔?        // 鑷劧椤虹潃鍦嗚鏀惰繘鍘汇€傝€佷唬鐮佹槸鍦ㄥ甫瀛愰噷濉竴鏉?*鐩?*鐭╁舰锛屼袱绔細鎴冲嚭鍦嗚澶?        // 鍗佸嚑鍍忕礌锛堟祬鑹查偅鏉℃槸鐧介珮鍏夛紝钀藉湪浜儗鏅笂灏辨槸涓€鏉＄湅寰楄鐨勯杈癸級銆?        if (!PerfSkipChrome && band.MaxY - band.MinY > 20f)
        {
            bool top = _dark;
            float y = top ? panel.MinY + 1f : panel.MaxY - 1f;
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, y - 0.75f, panel.MaxX, y + 0.75f),
                                    AntialiasMode.Aliased);
            var edge = new Vortice.RawRectF(panel.MinX + 0.5f, panel.MinY + 0.5f,
                                            panel.MaxX - 0.5f, panel.MaxY - 0.5f);
            ctx.DrawRoundedRectangle(new RoundedRectangle(edge, radius - 0.5f, radius - 0.5f),
                                     Brush(ctx, top ? Tokens.TopSheenDark : Tokens.EdgeLightBottom), 1f);
            ctx.PopAxisAlignedClip();

            // 鑹插甫閭ｆ潯鍑规Ы**鏈濅富鏉＄殑閭ｄ竴渚?*鍐嶅帇涓€閬?1 鍍忕礌鏆楃嚎锛堝彧鏈夋祬鑹诧級銆?            // 鍑规Ы鐨勫～鍏呭凡缁忛檷鍒伴粦 6%锛屽厜闈犲畠鑷繁鍙槸涓€鏉℃贰鐏板甫锛涜繖涓€閬撶嚎鎵嶆槸
            // "杩欓亾妲芥槸鍒昏繘闈㈡澘鐨?鐨勫嚟鎹紙Windows 鐨勫彛寰勶細娴呰壊鐢ㄦ弿杈瑰畾褰級銆?            // 浣嶇疆璺熺潃甯﹀瓙鍦ㄤ富鏉＄殑鍝竴渚ц蛋锛氶潰鏉胯创搴曟椂甯﹀瓙鍦ㄤ笂闈紝绾垮氨钀藉湪甯﹀瓙涓嬫部銆?            //
            // 杩欐潯绾?*鍙互鏄竴鏉＄洿妯嚎**锛堜笉鍍忎笂闈㈤偅鏉℃敹杈硅椤虹潃鍦嗚璧帮級锛氬畠鍦ㄩ潰鏉夸腑闂达紝
            // 鑰屼笂闈㈤偅涓?`> 20` 鐨勯棬妲涗繚璇佷簡瀹冪闈㈡澘閭ｆ潯杈硅嚦灏?20 鍍忕礌锛?            // 鍚屼竴鍒荤殑鍦嗚涔熷彧鏈?20 鍑哄ご鈥斺€斾袱绔几鍑哄幓鐨勪笉鍒板崐涓儚绱狅紝閲忎笉鍑烘潵銆?            if (!_dark)
            {
                float wy = BandAbove() ? band.MaxY - 0.5f : band.MinY + 0.5f;
                ctx.FillRectangle(new Vortice.RawRectF(panel.MinX + 1f, wy - 0.5f,
                                                       panel.MaxX - 1f, wy + 0.5f),
                                  Brush(ctx, Tokens.WellEdgeLight));
            }
        }

        // 杩欓噷鍘熸潵杩樼敾涓€鏉?韪㈣剼绾?锛坄DrawGroove`锛氶潰鏉夸笅娌?6 鍍忕礌澶勩€佺瑪鑹?25% 鐨?2 鍍忕礌妯嚎锛夈€?        // 2026-09-18 鍒犱簡锛氬畠褰撳勾鐨勪换鍔℃槸"璁╅潰鏉夸笅娌挎案杩滄湁涓滆タ锛岀湅鐫€鏄潡瀹屾暣鐨勬澘瀛?锛?        // 鑰岀幇鍦ㄤ笅娌挎湁**鎶曞奖 锛?鎻忚竟 锛?搴曟部鍐呴槾褰?*涓夋潯锛岃繖鍙ヨ瘽涓嶇敤瀹冩壙鎷呬簡锛?        // 鐣欎笅鐨勫潖澶勬洿瀹炲湪鈥斺€旈鑹茶窡鐫€绗旇蛋锛堢孩绗旀椂瀹冩槸鏁村潡鐧介潰鏉夸笂鏈€鍝嶇殑涓滆タ锛屽儚鏍硅繘搴︽潯锛夛紝
        // 鑰屽畠鎯宠〃杈剧殑"鐜板湪鎷跨殑鏄摢鏀瑪"锛岀悆閲岄偅閬撹壊鐜凡缁忚浜嗐€佽繕鏇村噯銆?        // 绮楃粏棰勮**鏈€鍚庣敾**锛氬畠鍙兘浼稿埌闈㈡澘澶栭潰锛屽帇鍦ㄤ笂闈㈢殑涓滆タ寰楄繃瀹冧竴灞?        DrawSizePreview(ctx, st);
        // 銆屾洿澶氥€嶉潰鏉?*姣斾竴鍒囬兘鍚?*锛氶伄缃╄鍘嬩綇涓绘潯鍜岀矖缁嗛瑙堬紝闈㈡澘鍐嶅帇浣忛伄缃┿€?        // 瀹冩墦寮€鏃?QueryBounds 宸茬粡鏄暣鍧楀睆骞曪紝涓嶄細琚紩鎿庤鎺夈€?        if (_moreOpen || _more.Running) DrawMorePanel(ctx);
    }

    // ---- 棰滆壊锛堟繁鑹蹭富棰樺彧鏄竴鏁村鎹㈣繃鏉ワ紝褰㈢姸涓€涓兘涓嶅姩锛?-----------------

    /// <summary>
    /// 灞曞紑鎬侀偅涓€鐗囧唴瀹癸細鍑规Ы 锛?宸ュ叿鏍?锛?璁剧疆鏉°€傛娊鍑烘潵鏄负浜嗚兘鍦ㄥ舰鍙樻湡闂?*鏁村眰娣″叆**
    /// 锛堢敤涓€涓笉閫忔槑搴﹀浘灞傚寘涓€娆″氨澶燂紝涓嶅繀鎶?alpha 浼犺繘鍗佸嚑涓粯鍒跺嚱鏁帮級銆?    /// </summary>
    private void DrawExpandedContent(ID2D1DeviceContext ctx, in UiState st,
                                     in RectF panel, in RectF band, float radius)
    {
        if (!PerfSkipChrome && band.MaxY - band.MinY >= 2f)
        {
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(band.MinX, band.MinY, band.MaxX, band.MaxY),
                                    AntialiasMode.Aliased);
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius),
                Brush(ctx, _dark ? Tokens.TroughDark : Tokens.TroughLight));
            ctx.PopAxisAlignedClip();
        }

        // 鐞冪缉杩涙渶宸︿竴鏍硷紝鍙宠竟鏄繖涓€妗ｇ殑宸ュ叿鏍硷紙鏋佺畝妗ｅ氨鍙湁鍏牸锛?        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++) DrawCell(ctx, k, st);

        if (BandVisible()) DrawBand(ctx, st);
    }

    /// <summary>
    /// 鏀惰捣鎬佺悆閲岄潰鐨勪笢瑗匡細涓€鍦堝綋鍓嶇瑪鑹?锛?涓€涓瑪鍥炬爣銆?    ///
    /// 涓ゅ**蹇呴』鎸夌悆绠椼€佷笉鑳芥寜闈㈡澘绠?*鈥斺€旇€佷唬鐮佹槸鎸?闈㈡澘鐨勪竴鍗婂"绠楃殑锛岄潰鏉夸竴闀?    /// 鍥炬爣灏辫鎾戞垚涓€鏁翠釜澶у浘鏍囷紙鍑哄浘 `--expand 0.15` 涓€鐪煎氨鐪嬭锛夛細
    ///   路 澶у皬 = 鐞冪殑鍗婂緞 脳 绯绘暟锛堢収鍋囬潰鏉跨殑鏁板瓧锛夛紱
    ///   路 浣嶇疆 = **鐞冮偅涓€鏍肩殑涓績**銆傞潰鏉挎槸寰€鍙抽暱鐨勶紝鑰岀悆鍦ㄥ睍寮€鎬佹槸绗?0 鏍硷紝
    ///     绗?0 鏍肩殑涓績姣旂悆鐨勪腑蹇冮潬鍙?4 鍍忕礌锛圔arPad 8 锛?鎸夐挳 40 鐨勪竴鍗?鈭?鐞冨崐寰?24锛夛紝
    ///     鎵€浠ユ寜 e 鎻掕繃鍘烩€斺€旈『鎵嬫妸"鐞冪缉杩涙渶宸︿竴鏍?杩欎欢浜嬩篃婕斾簡鍑烘潵銆?    /// </summary>
    private void DrawBallContent(ID2D1DeviceContext ctx, in UiState st, float e, float alpha)
    {
        var bar = BarRect();
        float ballR = Tokens.Ball * 0.5f;
        float cx = bar.MinX + ballR + (Tokens.BarPad + Tokens.Button * 0.5f - ballR) * e;
        var c = new Vector2(cx, (bar.MinY + bar.MaxY) * 0.5f);
        float a = QA(alpha);

        // 鏁板瓧鐓ф妱鍋囬潰鏉匡細鍦嗗唴閭ｅ湀鍗婂緞 = 0.76 脳 鐞冪殑鍗婂緞銆佺嚎瀹?2.5锛?        // 涓棿閭ｄ釜绗斿浘鏍?= 0.70 脳 鐞冪殑鍗婂緞銆?*涓や釜姣斾緥閮藉湪 Tokens 閲?*锛圔allRing / BallIcon锛夛紝
        // 鏀逛竴涓暟鐨勫嚑浣曞叧绯诲啓鍦ㄩ偅鍎库€斺€斾骇鍝佸師鏉ユ妸 0.70 鍐欐垚 1.40锛岀瑪姝ｅソ椤跺埌鑹插湀鍐呮部銆?        var ink = st.PaletteBase;
        ctx.DrawEllipse(new Ellipse(c, ballR * Tokens.BallRing, ballR * Tokens.BallRing),
                        Brush(ctx, new Color4(ink.R, ink.G, ink.B, a)), 2.5f);

        var iconBox = new RectF
        {
            MinX = c.X - ballR, MinY = c.Y - ballR, MaxX = c.X + ballR, MaxY = c.Y + ballR,
        };
        var iconInk = InkCol;
        IconAtlas.DrawCentered(ctx, "pen", iconBox, ballR * Tokens.BallIcon,
                               Brush(ctx, new Color4(iconInk.R, iconInk.G, iconInk.B, iconInk.A * a)));
    }

    /// <summary>
    /// 寮€涓€涓笉閫忔槑搴﹀浘灞傦紙褰㈠彉鏈熼棿鏁寸墖鍐呭娣″叆鐢級銆?*寤轰笉鍑烘潵灏辫繑鍥?false**锛?    /// 璁╄皟鐢ㄦ柟鐓у父鐢烩€斺€斾笉鑳藉洜涓轰竴涓鎰熸妸杩欎竴甯т涪鎺夛紙鐣岄潰杩炴姏涓夋浼氳寮曟搸鏁翠綋鍋滅敤锛夈€?    /// 鍥惧眰**姣忓抚鐜板缓鐜伴攢**锛屼笉缂撳瓨锛氳澶囦涪浜嗕箣鍚庢墜閲屽氨鏄繃鏈熺殑 COM 瀵硅薄锛?    /// 鑰岃繖鏉¤矾鍙湪褰㈠彉閭?200 姣閲岃蛋锛屼唬浠峰彲浠ュ拷鐣ワ紙瀹夐潤鏃朵竴娆￠兘涓嶈蛋锛夈€?    /// </summary>
    private bool BeginFade(ID2D1DeviceContext ctx, float opacity)
    {
        try { _fadeLayer = ctx.CreateLayer(null); }
        catch { _fadeLayer = null; }
        if (_fadeLayer == null) return false;

        var p = new LayerParameters1
        {
            // 鍐呭鐩掔粰"鏃犻檺澶?锛氱粰灏忎簡 D2D 浼氱収瀹冭锛屽唴瀹瑰氨缂轰竴鍧?            ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
            Opacity = QA(opacity),
        };
        ctx.PushLayer(ref p, _fadeLayer);
        return true;
    }

    private void EndFade(ID2D1DeviceContext ctx)
    {
        ctx.PopLayer();
        _fadeLayer?.Dispose();
        _fadeLayer = null;
    }

    /// <summary>
    /// 鎶?0..1 涔嬪鐨勬埅鎺夛紝閲岄潰鐢?smoothstep锛堜袱绔€熷害涓?0锛夆€斺€斾氦鍙夋贰鍏ョ殑涓ゅご鎵嶄笉璧锋１銆?    /// </summary>
    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// 鎶婇€忔槑搴﹂噺鍖栧埌 1/32 涓€妗ｃ€?*鐢诲埛鏄寜棰滆壊缂撳瓨鐨勶紙寤轰簡灏辩暀鐫€锛?*锛岄€愬抚鍠傝繛缁€?    /// 浼氬線缂撳瓨閲屽鍑犵櫨涓敾鍒凤紱閲忓寲涔嬪悗鏈€澶氬 32 涓紝鑰?1/32 鐨勫彴闃剁湅涓嶅嚭鏉ャ€?    /// </summary>
    private static float QA(float a) => MathF.Round(Math.Clamp(a, 0f, 1f) * 32f) / 32f;


    private Color4 PanelFill => _dark ? Tokens.PanelDark : Tokens.PanelLight;
    private Color4 BorderCol => _dark ? Tokens.BorderDark : Tokens.BorderLight;
    private Color4 InkCol => _dark ? Tokens.InkDark : Tokens.InkLight;
    private Color4 MutedCol => _dark ? Tokens.InkMutedDark : Tokens.InkMutedLight;
    private Color4 HoverCol => _dark ? Tokens.HoverDark : Tokens.HoverLight;

    /// <summary>
    /// 涓€寮?鍗＄墖"锛氭姇褰憋紙閫愬眰寰€澶栬儉锛夛紜 搴?锛?1px 鎻忚竟锛堟病鏈夎繖閬撹竟锛屽渾瑙掍細绯婅繘鑳屾櫙閲岋級銆?    ///
    /// 鎶曞奖鏈夊嚑灞傘€佹瘡灞傝儉澶氬皯銆佸娣憋紝鍏ㄥ湪 `Tokens.ShadowLight` / `ShadowDark` 閲岋紝
    /// 杩欓噷鍙礋璐?鐓у崟瀛愪竴灞傚眰鐢?銆?*浠庢渶澶х殑涓€灞傚線鏈€灏忕殑涓€灞傜敾**锛氬ぇ鐨勫厛閾恒€佸皬鐨勫悗鐩栵紝
    /// 闈犺繎闈㈡澘鐨勫湴鏂瑰彔寰楁渶鍘氾紝寰€澶栭€愭笎鍙樿杽鈥斺€旇惤宸氨鏄繖涔堟潵鐨勩€?    /// </summary>
    private void DrawCard(ID2D1DeviceContext ctx, RectF r, float radius)
    {
        if (!PerfSkipShadow)
        {
            var layers = _dark ? Tokens.ShadowDark : Tokens.ShadowLight;
            for (int i = layers.Length - 1; i >= 0; i--)
            {
                var s = layers[i];
                // 鍥涜竟涓€璧峰線澶栬儉锛屽渾瑙掍篃鍔犱笂鍚屾牱鐨勯噺锛氳繖鏍锋瘡涓€灞傞兘鍜岄潰鏉?*鍚屽績**銆?                // 鍙儉鐭╁舰銆佷笉鍔犲ぇ鍦嗚鐨勮瘽锛屽洓涓浆瑙掍細缂轰竴鍧楋紙灏栬鎴冲嚭鏉ワ級銆?                var layerRect = new Vortice.RawRectF(r.MinX - s.Inflate, r.MinY - s.Inflate + s.Dy,
                                                     r.MaxX + s.Inflate, r.MaxY + s.Inflate + s.Dy);
                float layerRad = radius + s.Inflate;
                ctx.FillRoundedRectangle(new RoundedRectangle(layerRect, layerRad, layerRad),
                                         Brush(ctx, s.Color));
            }
        }

        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, PanelFill));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
    }

    /// <summary>
    /// 璐磋竟闅愯棌鏃堕湶鍑烘潵鐨勯偅鏉?*鎶婃墜**锛氫竴鏉＄洿鐨勮嵂涓革紙鏀惰捣鎬佷笓鐢紝瑙?Tokens.PeekTab锛夈€?    ///
    /// 浣嶇疆**鐩存帴鍙栧紩鎿庣畻缁欐垜浠殑鍗犵敤鐭╁舰**锛?see cref="QueryBounds"/>锛夆€斺€旈偅姝ｆ槸灞忓箷涓?    /// 鐪熸闇插嚭鏉ョ殑閭ｄ竴鏉★紝宸茬粡鍚簡骞崇Щ鍜?澶瑰湪灞忓箷鍐?銆傚ソ澶勬槸**鐪嬪緱瑙佺殑**鍜?*鐐瑰緱鍒扮殑**
    /// 澶╃劧鏄悓涓€涓湴鏂癸紝涓嶄細鍑虹幇"鐪嬭涓€鏉″嵈鐐逛笉鍒?銆?    ///
    /// 璐寸潃宸﹀彸杈规椂闇插嚭鏉ョ殑閭ｆ潯鏄?*绔栫殑**锛屾墍浠ラ暱杈硅鎸夋柟鍚戦€夈€?    /// </summary>
    private void DrawPeekTab(ID2D1DeviceContext ctx)
    {
        var vis = QueryBounds();
        if (vis.IsEmpty) return;

        // 娓叉煋鏃跺闈㈠鐫€涓€灞?璐磋竟骞崇Щ"鐨勫彉鎹紙瑙?RenderShifted锛夛紝杩欓噷鍏堝噺鎺夛紝
        // 鐢诲嚭鏉ョ殑浣嶇疆鎵嶅拰鍗犵敤鐭╁舰瀵瑰緱涓娿€?        var s = Shift();
        var sliver = new RectF
        {
            MinX = vis.MinX - s.X, MinY = vis.MinY - s.Y,
            MaxX = vis.MaxX - s.X, MaxY = vis.MaxY - s.Y,
        };

        bool horiz = (sliver.MaxX - sliver.MinX) >= (sliver.MaxY - sliver.MinY);
        float cx = (sliver.MinX + sliver.MaxX) * 0.5f;
        float cy = (sliver.MinY + sliver.MaxY) * 0.5f;
        // 闀胯竟 = 鎶婃墜闀垮害锛堜絾涓嶆瘮闇插ご閭ｄ竴鏉℃湰韬洿闀匡級锛涚煭杈?= 闇插ご鏈夊鍘氾紙灏卞～婊″畠锛?        float half = MathF.Min(Tokens.PeekTab,
                               horiz ? sliver.MaxX - sliver.MinX : sliver.MaxY - sliver.MinY) * 0.5f;
        float halfT = (horiz ? sliver.MaxY - sliver.MinY : sliver.MaxX - sliver.MinX) * 0.5f;

        var box = horiz
            ? new Vortice.RawRectF(cx - half, cy - halfT, cx + half, cy + halfT)
            : new Vortice.RawRectF(cx - halfT, cy - half, cx + halfT, cy + half);
        float rad = MathF.Min(halfT, half);
        var shape = new RoundedRectangle(box, rad, rad);

        // **棰滆壊 = 褰撳墠绗旇壊**锛屽拰灞曞紑鎬侀湶鍑烘潵鐨勯偅鏉¤壊绾夸竴妯′竴鏍凤紙鐢ㄦ埛 2026-09-18锛?        // "灞曞紑鏉¤创杈逛互鍚庢湁棰滆壊鐨勶紝杩欎釜灏忓渾鐞冭创杈规病棰滆壊"锛夈€?        // 瀹冮『鎵嬫妸"鎵嬮噷鏄摢鏀瑪"杩欎欢浜嬩篃璇翠簡鈥斺€旈湶澶存椂鐞冮噷鐨勮壊鐜槸鐪嬩笉瑙佺殑銆?        var ink = _host.State.PaletteBase;
        ctx.FillRoundedRectangle(shape, Brush(ctx, new Color4(ink.R, ink.G, ink.B, 1f)));

        // 杩戠櫧鐨勬椂鍊欒ˉ涓€閬撴殫杈癸細鐧界瑪鐨勬妸鎵嬭惤鍦ㄦ祬鑹叉闈笂浼?*浠€涔堥兘鐪嬩笉瑙?*銆?        // 杩欐潯瑙勭煩鍜岃壊绾匡紙`DrawBandLine`锛夋槸鍚屼竴鏉♀€斺€旈偅杈规棭灏辫繖涔堝共浜嗐€?        // 娣辫壊涓婚涓嶇敤绠★細鐧藉湪鏆楀簳涓婃湰鏉ュ氨鐪嬪緱瑙併€?        if (!_dark && ink.R > 0.9f && ink.G > 0.9f && ink.B > 0.9f)
            ctx.DrawRoundedRectangle(shape, Brush(ctx, new Color4(0f, 0f, 0f, 0.2f)), 1f);
    }

    /// <summary>鐢讳笂甯︾殑鍐呭銆傛瘡涓€椤归兘瀵瑰簲寮曟搸閲岀湡瀹炲瓨鍦ㄧ殑鑳藉姏锛屾憜涓嶅嚭鏉ョ殑灏变笉鎽嗐€?/summary>
    private void DrawBand(ID2D1DeviceContext ctx, in UiState st)
    {
        // 骞虫椂灏辨槸涓€鏉?**10 鍍忕礌鐨勮壊绾匡紝鏁存潯鐢ㄥ綋鍓嶇瑪鑹?*锛堢収鍋囬潰鏉匡細涓嶅垎娈点€佹病鏈夋枃瀛楋級锛?        // 纰板埌鎵嶉暱鎴愬畬鏁寸殑璁剧疆鏉°€備腑闂撮偅涓€娈垫槸"绾挎贰鍑恒€佹帶浠舵贰鍏?鐨勮繃娓?        // 鈥斺€斿亣闈㈡澘鐨勫叕寮忥細t = (甯﹂珮 - 12) / 14锛? = 杩樻槸涓€鏉＄嚎锛? = 瀹屽叏鏄帶浠躲€?        float t = Math.Clamp((BandHeightFull() - 12f) / 14f, 0f, 1f);
        if (t < 0.999f) DrawBandLine(ctx, st, 1f - t);
        if (t <= 0.001f) return;

        if (!RailOpen) return;

        if (BandHasSwatches)
        {
            for (int i = 0; i < SwatchCount; i++)
            {
                var r = SwatchRect(i);
                var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                var rr = new RoundedRectangle(box, 7f, 7f);
                ctx.FillRoundedRectangle(rr, Brush(ctx, SwatchColor(i)));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                // 涓€閬撳唴楂樺厜锛氳壊鐗囨墠鏈?瀹炰綋鎰?锛堝亣闈㈡澘閲屽啓鐫€"棰滃€间笂鏈€渚垮疁鐨勪竴绗?锛?                if (!PerfSkipSwatchDetail)
                {
                    var hl = new Vortice.RawRectF(r.MinX + 2, r.MinY + 1, r.MaxX - 2, r.MinY + 1 + (r.MaxY - r.MinY) * 0.26f);
                    ctx.FillRoundedRectangle(new RoundedRectangle(hl, 3f, 3f),
                        Brush(ctx, new Color4(1f, 1f, 1f, _dark ? 0.13f : 0.35f)));
                }

                if (!PerfSkipSwatchDetail && IsSwatchActive(st, i))
                {
                    // 閫変腑鐨勮壊鍧楄鏈?*绗簩閲嶆爣璁?*锛堝彧闈犻鑹插尯鍒嗕笉绗﹀悎鏃犻殰纰嶈姹傦級锛?                    // 寰€閲岀缉 2 鍍忕礌鍐嶆弿涓€鍦堬紙鐓у亣闈㈡澘鐨?1.6 绾垮锛夈€?                    var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 5f, 5f), Brush(ctx, InkCol), 1.6f);
                }
                else if (_hover == 100 + i)
                {
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, InkCol), 1.5f);
                }
            }
        }

        DrawDashToggle(ctx, st);

        int n = BandSegmentCount;
        for (int i = 0; i < n; i++) DrawSegment(ctx, i, n, st);

        if (BandHasSlider) DrawBandSlider(ctx, st);
        DrawBandAction(ctx);

        // 鈿?銆岀 N 灞忋€嶅師鏉ユ槸**鍗曠敾鍦ㄥ甫瀛愬彸杈?*鐨勪竴娈垫枃瀛楋紙鐢ㄧ殑鏄啓姝荤殑 `SegmentRect(4, 5)`锛夛紝
        // 2026-09-27 鏀规垚缈婚〉鍣ㄤ腑闂撮偅涓€娈碉紙瑙?<see cref="BoardSegKind.PageLabel"/>锛夆€斺€?        // 閭ｅ潡灏辨槸鍘熸潵閭ｄ釜鍏ュ彛锛屽彧鏄惉鍒颁簡涓や釜绠ご涓棿锛屾垚浜?缈婚〉鍣?鐨勪竴閮ㄥ垎銆?    }

    /// <summary>
    /// 璁剧疆鏉″彸绔殑**绮楃粏婊戞潯**锛氬簳杞?锛?宸查€夋锛堢敤褰撳墠棰滆壊锛夛紜 婊戦挳 锛?鍙崇涓€涓?    /// 璺熺潃鍙樺ぇ鐨勭瑪灏栭瑙堬紙绮楃粏涓€鐪肩湅寰楄锛屾瘮鏁板瓧鐩磋锛夈€?    ///
    /// 婊戦挳**涓€鐩寸敾**銆佷笉鍐?鍙湁鎷栧姩/鎮仠鎵嶆诞鍑烘潵"锛氳缃潯鏈潵灏卞彧鍦?    /// "鐒︾偣鍦ㄩ潰鏉夸笂"鏃舵墠鍑虹幇锛堣 RailHoverZone锛夛紝绛変簬姘歌繙澶勫湪鎮仠鎬併€?    /// 鍙湪鎷栧姩鏃舵墠鍑虹幇鐨勮瘽锛岃€佸笀浼氫互涓洪偅鏄釜闈欐€佺殑鍒嗛殧绗︺€?    /// </summary>
    private void DrawBandSlider(ID2D1DeviceContext ctx, in UiState st)
    {
        var box = SliderRect();
        var (left, right) = SliderTrackRange();
        float cy = (box.MinY + box.MaxY) * 0.5f;
        float t = SliderT(st);
        var ink = st.PaletteBase;

        float h = _sliderDragging ? 6f : 5f;
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(left, cy - h * 0.5f, right, cy + h * 0.5f),
                                 h * 0.5f, h * 0.5f),
            Brush(ctx, _dark ? Tokens.TrackDark : Tokens.TrackLight));

        float kx = left + (right - left) * t;
        if (kx - left > 0.5f)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(left, cy - h * 0.5f, kx, cy + h * 0.5f),
                                     h * 0.5f, h * 0.5f),
                Brush(ctx, new Color4(ink.R, ink.G, ink.B, _sliderDragging ? 0.85f : 0.55f)));

        // 锛堟。浣嶇偣 2026-10-09 鐧藉ぉ鍔犮€佸綋鏅氭挙锛氱敤鎴?涓嶅ぇ濂界湅锛岀洿鎺ユ粦鍔ㄥ彇澶у皬"鈥斺€?        //   鐜板湪鎷栧姩鍚搁檮鍒版渶杩戜竴妗ｏ紝婊戞潯涓婁笉鍐嶇敾灏忕偣銆傦級

        ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 7f, 7f), Brush(ctx, Tokens.AccentInk));
        ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 7f, 7f),
                        Brush(ctx, new Color4(0.19f, 0.20f, 0.24f, 1f)), 1f);

        // 鍙崇閭ｄ釜"绗斿皷棰勮"锛?*鐧芥澘閭ｄ竴鏍间笉鐢?*銆?        // 瀹冪敾鐨勬槸"杩欎竴绗旀湁澶氱矖"锛堝崐寰勮窡鐫€婊戞潯浣嶇疆鍙橈級锛岃€岀櫧鏉胯繖涓€鏍兼嫋鐨勬槸**鏉块潰涓嶉€忔槑搴?*锛?        // 璺熺瑪瀹芥病鏈夊崐鐐瑰叧绯烩€斺€斿畠杩樹細鎷?*褰撳墠绗旇壊**锛堟墍浠ュ湪鐧芥澘涓婃槸棰楃孩鐐癸級銆?        // 鐢ㄦ埛 2026-09-27 灏辨槸鍦ㄥ浘涓婄湅瑙佸畠鎵嶉棶鐨勶細"璋冩暣閫忔槑鐨勭殑婊戝潡浼间箮鍚庨潰璺熺潃涓€涓皬鍦嗙偣锛?
        // 鎷栨粦鏉?/ 鎮仠鏃舵湰鏉ュ氨鏈夋皵娉″湪鏄剧ず鏉胯壊鍜岀櫨鍒嗘瘮锛圖rawSizePreview锛夛紝淇℃伅涓嶇己銆?        if (_bandCell != 2)
        {
            float pr = Math.Clamp(2f + t * 6.5f, 2f, 8.5f);
            ctx.FillEllipse(new Ellipse(new Vector2(box.MaxX - SliderPreviewW * 0.5f - 2f, cy), pr, pr),
                            Brush(ctx, ink));
        }
    }

    /// <summary>
    /// 铏氬疄绾垮垏鎹㈤偅涓€鏍笺€?*鐢荤殑鏄?涓嬩竴绗斾細闀夸粈涔堟牱"**锛氫竴灏忔鎸夊綋鍓嶇嚎鍨嬬敾鍑烘潵鐨勭嚎
    /// 锛堝拰閫変腑闈㈡澘閲岄偅涓夋牸鍚屼竴涓瑷€鈥斺€旇€佸笀鐪嬬殑鏄嚎鏈韩锛屼笉鏄枃瀛楋級銆?    ///
    /// 鏍锋湰绾跨殑鑺傞暱姣斾緥鐓ф妱寮曟搸閭ｄ袱鏉?D2D dash 鍥炬锛堣櫄绾?鑺?3 缂?2"銆佺偣绾?鑺傗増0 缂?2"锛?    /// 鍗曚綅閮芥槸绗斿锛岃 <c>Gfx</c>锛夛紝浣?*涓嶈兘鐩存帴鐢ㄩ偅鏉℃弿杈规牱寮?*锛?    /// 瀹冪殑鑺傞暱鎸?*鐪熷疄绗斿**绠楋紝46脳26 鐨勫皬鏍煎瓙閲屼細纰庢垚涓€涓茬湅涓嶆竻鐨勭偣銆?    /// 杩欓噷鎸夎繖涓€鏍艰嚜宸辩殑鏍锋湰绾垮绠楄妭闀匡紝鐪嬬潃鎵嶆槸"缂╁皬鐨勮櫄绾?銆?    /// </summary>
    private void DrawDashToggle(ID2D1DeviceContext ctx, in UiState st)
    {
        if (!BandHasDashToggle || !RailOpen) return;
        var r = DashToggleRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);

        if (_hover == 500) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        // 鏍锋湰绾跨敤**褰撳墠绗旇壊**锛氳繖涓€鏍奸『鎵嬫妸"涓嬩竴绗旀槸浠€涔堥鑹?涔熻浜嗕竴閬嶃€?        float lx0 = r.MinX + 9f, lx1 = r.MaxX - 9f;
        float ly = (r.MinY + r.MaxY) * 0.5f;
        float v = _dashFade.Value;                    // 0 = 鍏ㄦ槸鏃ф。锛? = 鍏ㄦ槸鏂版。
        if (v < 0.999f) DrawDashSample(ctx, _dashFadeFrom, lx0, lx1, ly, st.PaletteBase, 1f - v);
        DrawDashSample(ctx, st.Dash, lx0, lx1, ly, st.PaletteBase, v);

        // 鎹㈡尅閭ｄ竴鐬棿鎻忎竴閬撳己璋冭壊锛氫笁妗ｇ殑鏍锋湰绾垮湪 46 鍍忕礌閲屽樊鍒笉澶э紝
        // 鍏夌湅"绾垮彉浜?涓嶅鏄撶‘璁?鎴戝垰鎵嶆寜鍒颁簡娌℃湁"銆?        if (v < 0.999f)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, Alpha(Tokens.Accent, 1f - v)), 1.6f);
    }

    /// <summary>
    /// 鎸夌嚎鍨嬬敾涓€灏忔鏍锋湰銆?paramref name="alpha"/> 鈮?0.01 灏辨暣娈典笉鐢伙紙鎹㈡尅鍔ㄧ敾鐨勬贰鍑洪偅鍗婄▼璧拌繖鏉★級銆?    /// </summary>
    private void DrawDashSample(ID2D1DeviceContext ctx, StrokeDash dash, float x0, float x1, float y,
                                in Color4 ink, float alpha)
    {
        if (alpha <= 0.01f) return;
        var brush = Brush(ctx, Alpha(ink, alpha));
        const float w = 2.4f;                  // 鏍锋湰绾垮锛堝拰閫変腑闈㈡澘閭ｄ笁鏍肩殑 2.6 涓€涓噺绾э級
        switch (dash)
        {
            case StrokeDash.Dashed:
                // 鑺?3w銆佺紳 2w 鈥斺€?鍜?Gfx 閲岄偅瀵规暟瀛楀悓涓€涓瘮渚?                for (float x = x0; x < x1; x += w * 5f)
                    ctx.DrawLine(new Vector2(x, y),
                                 new Vector2(MathF.Min(x + w * 3f, x1), y), brush, w);
                break;
            case StrokeDash.Dotted:
                // 缂?2w銆佹瘡涓偣鏄竴涓?鐩村緞 = w"鐨勫渾锛堝紩鎿庨偅杈归潬鍦嗗ご dash cap 榧撳嚭鏉ワ紝鍚屼竴涓牱瀛愶級
                for (float x = x0 + w * 0.5f; x < x1; x += w * 2f)
                    ctx.FillEllipse(new Ellipse(new Vector2(x, y), w * 0.5f, w * 0.5f), brush);
                break;
            default:
                ctx.DrawLine(new Vector2(x0, y), new Vector2(x1, y), brush, w);
                break;
        }
    }

    /// <summary>
    /// 鎹釜閫忔槑搴︼紙鎹㈡尅鍔ㄧ敾瑕佹贰鍏ユ贰鍑虹敤锛夈€?    ///
    /// **閫忔槑搴︽寜 1/32 閲忓寲**锛?see cref="Brush"/> 閭ｄ釜缂撳瓨鏄寜棰滆壊鏌ョ殑锛?    /// 姣忓抚涓€涓柊閫忔槑搴﹀氨鏄竴"鎶?鏂扮瑪鍒凤紙167 姣 脳 姣忓抚涓ゆ 鈮?鍑犲崄鎶婏級銆?    /// 閲忓寲涔嬪悗涓€瓒熷姩鐢绘渶澶氱敤 33 涓€尖€斺€旇倝鐪肩湅涓嶅嚭宸埆锛岀紦瀛樹篃涓嶅啀琚姩鐢绘拺澶с€?    /// </summary>
    private static Color4 Alpha(in Color4 c, float a)
        => new(c.R, c.G, c.B, Math.Clamp(MathF.Round(a * 32f) / 32f, 0f, 1f));

    /// <summary>杩欎竴鐗囪壊鐗囨槸涓嶆槸"褰撳墠鑹?锛堟瘮杈冨昂搴﹁ <see cref="SameColor"/>锛屽拰鍒囪壊鍏辩敤涓€鎶婏級銆?/summary>
    private bool IsSwatchActive(in UiState st, int i) => SameColor(SwatchColor(i), st.PaletteBase);

    /// <summary>
    /// 骞虫椂閭ｆ潯鑹茬嚎锛?*鏁存潯鐢ㄥ綋鍓嶇瑪鑹?*锛屼笉鍒嗘銆佹病鏈夋枃瀛楋紙鐓у亣闈㈡澘锛夈€?    /// `alpha` 鏄繃娓＄敤鐨勨€斺€斿畠闀挎垚璁剧疆鏉＄殑杩囩▼涓紝杩欐潯绾挎贰鍑恒€?    ///
    /// 涓€澶勭粏鑺傦細**鐧界瑪鍦ㄦ祬搴曚笂浼氱湅涓嶈**锛屾墍浠ョ粰鐧界嚎琛ヤ竴閬撴瀬娣＄殑鎻忚竟鍏滃簳
    /// 锛堝亣闈㈡澘閲屼笓闂ㄤ负杩欎竴绉嶆儏鍐靛啓浜嗚繖涓垎鏀級銆?    /// </summary>
    private void DrawBandLine(ID2D1DeviceContext ctx, in UiState st, float alpha)
    {
        var r = BandRect();
        if (r.MaxY - r.MinY < 0.5f) return;

        var panel = UnionRect();
        float radius = Tokens.PillRadius(Tokens.BarHeight) - 6f * _expand.Value;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius);
        var line = st.PaletteBase;

        // **璐寸潃闈㈡澘椤惰竟鐨勫渾瑙掕蛋**锛堢収鍋囬潰鏉匡級锛氭暣鏉＄嚎鐨勪袱绔窡鐫€闈㈡澘鐨勫渾瑙掓敹杩涘幓锛?        // 鑰屼笉鏄湪闈㈡澘閲岀缉杩涗竴鏉＄嫭绔嬬殑灏忕嚎鈥斺€旈偅鏍风湅鐫€鍍?璐翠簡寮犵焊鏉?銆?        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                                AntialiasMode.Aliased);
        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(line.R, line.G, line.B, alpha)));
        ctx.PopAxisAlignedClip();

        bool nearWhite = line.R > 0.9f && line.G > 0.9f && line.B > 0.9f;
        if (nearWhite && !_dark)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, new Color4(0f, 0f, 0f, 0.2f * alpha)), 1f);
    }

    /// <summary>
    /// 鐢荤櫧鏉胯壊甯︿笂鐨勪竴娈碉紙娈佃〃瑙?<see cref="BoardBand"/>锛夈€傚叚绉嶆鍚勬湁鍚勭殑鐢绘硶锛?    ///   路 **涓婁竴灞?/ 涓嬩竴灞?*锛氬渾瑙掑皬鏍?锛?涓婁笅绠ご锛涘埌椤朵簡"涓婁竴灞?鍘嬫殫锛堝弽棣堝湪杩欓噷缁欙級锛?    ///   路 **绗?N 灞?*锛?*鍏夊啓瀛楋紝涓嶇敾妗嗐€佷笉鍝嶅簲鎮仠**鈥斺€斿畠澶瑰湪涓や釜绠ご涓棿锛屽拰瀹冧滑鏄竴浣撶殑锛?    ///   路 **鏉胯壊**锛氱洿鎺ユ妸閭ｅ潡鏉跨殑棰滆壊閾哄嚭鏉ワ紙棰滆壊鏈韩灏辨槸鍐呭锛屽啓瀛楀弽鑰屽浣欙級锛?    ///     褰撳墠鏉胯壊鎻忎竴鍦堝搧鐗岃壊 锛?閲岄潰鍐嶅涓€鍦堢櫧锛堢浜岄噸鏍囪锛屼笉闈犻鑹插崟鐙尯鍒嗭級锛?    ///   路 **搴曠汗 / 闂磋窛**锛氭枃瀛楀啓"鐜板湪鍦ㄥ摢涓€妗? 锛?鍙宠竟涓€绔栧垪**妗ｄ綅鐐?*銆?    ///
    /// 搴曠汗 / 闂磋窛杩欎袱鏍兼槸**寰幆妗?*锛堢偣涓€涓嬫崲涓嬩竴妗ｏ級锛岃繖濂楁墜鎰熸槸瀹冧滑鍘熸潵鍦ㄣ€屾洿澶氥€嶆娊灞夐噷
    /// 灏辨湁鐨勶紝鎼繃鏉ユ病鏀广€傛。浣嶇偣鏄?2026-09-20 鍦ㄥ浘褰㈤偅涓€鏍间笂瀹氱殑瑙勭煩锛?    /// **鍙崲鏂囧瓧涓嶇粰鐐圭殑璇濓紝"杩欎竴鏍艰繕鑳界偣"鏄殣褰㈢殑**銆?    /// </summary>
    private void DrawBoardSegment(ID2D1DeviceContext ctx, int i, in RectF r, in UiState st)
    {
        var seg = BoardSegAt(i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);
        bool hover = _hover == 200 + i;

        switch (seg.Kind)
        {
            case BoardSegKind.PageUp:
            case BoardSegKind.PageDown:
            {
                // 杩欐潯鍒ゆ嵁**涓庢斁鏄犵姸鎬佹棤鍏?*鈥斺€旇繖涓€鏍煎彧绠＄櫧鏉匡紙瑙?ActivateSegment 鐨勮鏄庯級銆?                bool enabled = seg.Kind == BoardSegKind.PageDown || _host.State.CanFlipPageUp;
                var fg = enabled ? InkCol : new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f);
                if (hover && enabled) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
                IconAtlas.DrawCentered(ctx,
                    seg.Kind == BoardSegKind.PageUp ? "chevronUp" : "chevronDown",
                    r, 16f, Brush(ctx, fg));
                return;
            }

            case BoardSegKind.PageLabel:
                _widgets.Text(ctx, $"绗?{st.ScreenIndex} 灞?, r, 12.5f, Brush(ctx, InkCol));
                return;

            case BoardSegKind.Color:
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, InkPalette.BoardPresets[seg.Idx].Color));
                bool active = BoardColorIs(st, seg.Idx);
                ctx.DrawRoundedRectangle(rr, active ? Brush(ctx, Tokens.Accent) : Brush(ctx, BorderCol),
                                         active ? 2f : 1f);
                if (active)
                {
                    var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 6f, 6f),
                                             Brush(ctx, Tokens.AccentInk), 1.5f);
                }
                return;
            }

            default:                       // 搴曠汗 / 闂磋窛
            {
                if (hover) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                // 鍙宠竟鐣欎竴鏉＄粰绔栨帓鐨勬。浣嶇偣锛堝搴﹀拰鍥惧舰閭ｄ竴鏍煎悓涓€涓?12锛岃 DrawSegment锛?                const float PipStripW = 12f;
                var textBox = new RectF
                {
                    MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX - PipStripW, MaxY = r.MaxY,
                };
                string label = seg.Kind == BoardSegKind.Pattern
                    ? PatternName(st.BoardPattern)
                    : $"{st.BoardPatternStep:F0}";
                _widgets.Text(ctx, label, textBox, 12.5f, Brush(ctx, InkCol));

                var (count, cur) = BoardPips(seg.Kind, st);
                DrawPips(ctx, r.MaxX - PipStripW * 0.5f, (r.MinY + r.MaxY) * 0.5f, cur, count, InkCol);
                return;
            }
        }
    }

    private void DrawSegment(ID2D1DeviceContext ctx, int i, int count, in UiState st)
    {
        var r = SegmentRect(i, count);
        bool active = IsSegmentActive(st, i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);

        // 鐧芥澘閭ｄ竴鏍硷細娈佃〃璇翠簡绠楋紙瑙?BoardBand / DrawBoardSegment锛?        if (_bandCell == 2) { DrawBoardSegment(ctx, i, r, st); return; }

        if (active)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
        }
        else if (_hover == 200 + i || _press == 200 + i)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        }
        ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);

        string label = SegmentLabel(i);
        if (label.Length > 0)
        {
            _widgets.Text(ctx, label, r, (r.MaxX - r.MinX) < 62f ? 11f : 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
            return;
        }

        var segInk = active ? Tokens.AccentInk : InkCol;
        // **鏈夋。浣嶇殑閭ｅ嚑鏍?*锛堢偣瀹冧竴涓嬫崲涓€妗ｏ紝瑙?ActivateSegment 鐨?case 8锛夛細
        //   路 銆岀洿绾裤€? 3 妗ｇ嚎鍨嬶紙瀹?/ 铏?/ 鐐癸級锛?        //   路 銆屾姏鐗╃嚎銆? 2 妗ｏ紙涓婁笅 / 宸﹀彸锛夛紱
        //   路 銆屽弻鏇茬嚎銆? 2 妗ｏ紙鏈?/ 鏃犳笎杩戠嚎锛夈€併€屾き鍦嗐€? 2 妗ｏ紙鏈?/ 鏃犵劍鐐逛笁瑙掑舰锛夛紱
        //   路 銆屽潗鏍囩郴銆? 2 妗ｏ紙甯︾綉鏍?/ 涓嶅甫缃戞牸锛?026-09-24 浠庛€屾洿澶氥€嶆娊灞夋尓杩涙潵鐨勶級锛?        //   路 銆屾１鏌?/ 妫遍敟 / 妫卞彴銆? 4 妗ｈ竟鏁帮紙涓?/ 鍥?/ 浜?/ 鍏級銆?        // 鍥炬爣鐓ф棫鐢诲綋鍓嶉偅涓€妗ｏ紝**鍙宠竟鍐嶅姞涓€绔栧垪妗ｄ綅鐐?*鈥斺€斿ぇ鑰屾祿鐨勯偅涓槸褰撳墠妗ｃ€?        // 鐢ㄦ埛 2026-09-20 瀹氾細鍙崲鍥炬爣鐨勮瘽锛岃€佸笀"涓嶇煡閬撹繖涓€鏍艰繕鑳界偣"锛堝彲閫夌殑鐘舵€佹槸闅愬舰鐨勶級銆?        // 鎶涚墿绾块偅鏍兼槸**鍚屼竴澶╃◢鍚庤ˉ涓婄殑**锛堢敤鎴凤細"鎶涚墿绾块〉澧炲姞灏忓渾鐐?锛夆€斺€?        // 瀹冧粠鍔犺繘鏉ラ偅澶╄捣灏辨槸"鍐嶇偣涓€娆℃崲涓€妗?锛屽嵈涓€鐩存病缁欑偣锛屾槸婕忕殑銆?        //
        // 鈿?**涓夋牸鍚勬湁鍚勭殑妗?*锛坄st.SidesOf(tool)`锛夛細绗竴鐗堜笁鏍煎叡鐢ㄤ竴涓暟锛岀粨鏋滄槸
        // "鐐规１閿ラ偅涓€鏍硷紝妫辨煴銆佹１鍙扮殑鐐硅窡鐫€涓€璧峰姩"鈥斺€旂敤鎴蜂笂鎵嬪氨鎶ヤ簡杩欎釜銆?        // 妗ｄ綅鐐圭敾鐨勬槸**杩欎竴鏍艰嚜宸辩殑妗?*锛屾墍浠ヨ繖閲屽繀椤婚棶 `SidesOf(segTool)`锛?        // 鑰屼笉鏄殢鎵嬭涓€涓?`st.PrismSides`銆?        //
        // **鐐逛粠"鍥炬爣涓嬮潰"鎸埌浜?鍥炬爣鍙宠竟"**锛堣繕鏄?2026-09-20锛岀敤鎴风湅鍑烘潵鐨勶級锛?        // 鍥惧舰娈垫槸"瀹?脳 26"鐨勯暱鏂瑰舰锛岃€屽浘鏍囧彧鍗?18 鈥斺€?妯帓鏀惧湪涓嬮潰鐨勬椂鍊欙紝涓轰簡鎸ゅ嚭
        // 閭?7 鍍忕礌楂橈紝鍥炬爣寰?*鍘嬪埌 16 骞舵暣浣撲笂绉?3.5**锛涚珫鐫€鏀惧埌鍙宠竟涔嬪悗閭ｄ竴鍒楃偣
        // 鍙崰绾?4 瀹斤紝鍥炬爣灏辫兘鍥炲埌 18 骞剁暀鍦ㄦ涓€?        //
        // **"杩欎竴鏍兼湁鍑犳。銆佺幇鍦ㄦ槸绗嚑妗?鍙湁 `PipsOf` 閭ｄ竴澶?*锛堢粯鍒朵笌鑷鍏辩敤锛夛細
        // 鍦ㄨ繖閲屽啀鍒椾竴閬嶅伐鍏峰悕锛屽姞涓€绉嶅浘褰㈠氨浼氭紡涓€澶勩€?        // 鏈€鍚庨偅涓€娈垫槸**鍔ㄤ綔**涓嶆槸鍥惧舰锛氱敾涔︽灦鍥炬爣锛堣嚜缁橈紝瑙?`IconAtlas.DrawLibrary`锛夛紝
        // 娌℃湁妗ｄ綅鐐广€佷篃涓嶅弬涓?鍝釜鍥惧舰閫変腑浜?鐨勯珮浜€?        if (i >= ShapeSegmentCount)
        {
            IconAtlas.DrawCentered(ctx, "library", r, 18f, Brush(ctx, segInk));
            return;
        }

        var segTool = ShapeToolAt(i);
        var (pipCount, pipCur) = PipsOf(segTool, st);
        if (pipCount > 0)
        {
            // 鍙宠竟璁╁嚭杩欎箞瀹界殑涓€鏉＄粰绔栨帓鐨勭偣锛堢偣璺?鍗婂緞鍦?`DrawPips` 閲岋紝鏈€娴撶殑閭ｄ釜鍗婂緞 2锛?            //  鎵€浠ヨ繖涓€鍒楀疄闄呭崰 ~4 瀹姐€佸眳涓湪杩欐潯甯︾殑涓棿锛夈€?            const float PipStripW = 12f;
            var iconBox = new RectF
            {
                MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX - PipStripW, MaxY = r.MaxY,
            };
            IconAtlas.DrawCentered(ctx, ShapeIcon(segTool), iconBox, 18f, Brush(ctx, segInk));
            DrawPips(ctx, r.MaxX - PipStripW * 0.5f, (r.MinY + r.MaxY) * 0.5f, pipCur, pipCount, segInk);
            return;
        }

        IconAtlas.DrawCentered(ctx, ShapeIcon(segTool), r, 18f, Brush(ctx, segInk));
    }

    /// <summary>
    /// 杩欎竴鏍煎彸杈硅鐐?*鍑犱釜妗ｄ綅鐐?*銆佺鍑犱釜鏄綋鍓嶆。锛坄Count = 0` 锛?杩欎竴鏍兼病鏈夋。浣嶏紝
    /// 缁樺埗閭ｈ竟灏辩収鍘熸牱鐢诲ぇ鍥炬爣锛夈€?*鍒ゆ嵁鍙湁杩欎竴澶?*鈥斺€旂粯鍒跺拰鑷閮介棶瀹冿細
    ///   路 銆岀洿绾裤€? 3 妗ｇ嚎鍨嬶紝褰撳墠妗ｅ氨鏄?<c>LineDash</c>锛?    ///   路 銆屾姏鐗╃嚎銆? **2 妗?*锛堜笂涓?/ 宸﹀彸锛夛紝褰撳墠妗ｆ寜 <see cref="ParabolaAxisIndex"/> 鎶樼畻
    ///     鈥斺€旀敞鎰?`CurveAxis` 鏈韩鏄洓涓€硷紙涓婁笅宸﹀彸鍚勬湁涓ゅ悜锛夛紝闈㈡澘杩欎竴鏍煎彧绠?鍝竴瀵?锛?    ///   路 銆屾１鏌?/ 妫遍敟 / 妫卞彴銆? 4 妗ｈ竟鏁帮紝**涓夋牸鍚勮鍚勭殑**锛堣 `UiState.SidesOf`锛夛紱
    ///   路 鍒殑鍥惧舰 0锛堝畠浠繕娌℃湁绗簩妗ｏ級銆?    /// 妗ｄ綅鑼冨洿**鏉ヨ嚜寮曟搸**锛坄st.SolidMin/MaxSides`锛夆€斺€擿Stroke` 鏄紩鎿庡唴閮ㄧ被鍨嬶紝
    /// 鐣岄潰鐪嬩笉鍒板畠锛屼篃涓嶈鍦ㄨ繖閲屽啓姝讳竴浠?3/6銆?    /// </summary>
    private static (int Count, int Current) PipsOf(Tool tool, in UiState st)
    {
        if (tool == Tool.Line) return (3, (int)st.LineDash);
        if (tool == Tool.Parabola) return (2, ParabolaAxisIndex(st.ParabolaAxis));
        // **鍙屾洸绾?*锛?026-09-22锛夛細2 妗?= 鏈?/ 鏃犳笎杩戠嚎銆?        // 椤哄簭鎸?鎾斁鍏堝悗"鎺掞細绗?1 妗?= 鏈夋笎杩戠嚎锛堥粯璁わ級銆佺 2 妗?= 鏃犮€?        if (tool == Tool.Hyperbola) return (2, st.HyperbolaAsymptotes ? 0 : 1);
        // **妞渾锛堝甫鐒︾偣锛?*锛?026-09-22锛夛細2 妗?= 鏈?/ 鏃犵劍鐐逛笁瑙掑舰锛堥粯璁ゆ湁锛夈€?        if (tool == Tool.ConicEllipse) return (2, st.EllipseFocusTriangle ? 0 : 1);
        // **鍧愭爣绯?*锛?026-09-24锛夛細2 妗?= 甯︾綉鏍?/ 涓嶅甫缃戞牸銆?        // 鐢ㄦ埛閭ｅぉ鍘熻瘽锛?鎴戞墦绠楁妸瀹冩尓鍒板浘褰㈤噷闈㈢殑閭ｄ釜鍧愭爣绯烩€︹€︾偣涓€涓嬪垏鎹㈡垚缃戞牸锛岀偣涓€涓嬬綉鏍兼病浜?锛?        // 绱ф帴鐫€鍙堣"瀹冩。浣嶄箣闂存槸鏈夊垏鎹㈡寜閽殑锛屼綘鍙互鍙傜収涓€涓嬪叾浠栭偅涓垏鎹㈤€昏緫"鈥斺€?        // 璇寸殑灏辨槸杩欎釜**妗ｄ綅鐐?*锛氬彧鎹㈠浘鏍囦笉缁欑偣鐨勮瘽锛?杩欎竴鏍艰繕鑳界偣"鏄殣褰㈢殑
        //锛堢敤鎴?2026-09-20 灏变负鎶涚墿绾胯ˉ杩囦竴娆★紝瑙佷笂闈㈤偅娈碉級銆?        // 椤哄簭鎸?鎾斁鍏堝悗"鎺掞細绗?1 妗?= 涓嶅甫缃戞牸锛堥粯璁わ紝灏辨槸鐢诲嚭鏉ユ病鏍煎瓙鐨勯偅涓級銆佺 2 妗?= 甯︾綉鏍笺€?        if (tool == Tool.Coordinate) return (2, st.CoordGridDefault ? 1 : 0);
        if (ShapeSpec.HasSideCount(tool))
            return (st.SolidMaxSides - st.SolidMinSides + 1,
                    Math.Clamp(st.SidesOf(tool), st.SolidMinSides, st.SolidMaxSides) - st.SolidMinSides);
        return (0, 0);
    }

    /// <summary>
    /// 鐢讳竴**绔栧垪妗ｄ綅鐐?*锛氬憡璇夎€佸笀"杩欎竴鏍兼湁鍑犳。銆佺幇鍦ㄦ槸绗嚑妗?锛堜粠涓婂線涓?= 绗?1 妗?鈫?绗?n 妗ｏ級銆?    ///
    /// 褰撳墠妗ｇ敤**澶?+ 娴?*涓や釜宸埆锛屽叾浣欑殑灏忎竴鍗娿€侀€忔槑搴?35%锛?    /// 鐢?澶у皬"鑰屼笉鏄彧鐢ㄩ鑹诧紝鏄洜涓鸿繖涓€娈靛彲鑳芥槸**閫変腑鎬?*锛堟暣涓牸瀛愰摵鐫€鍝佺墝鑹层€?    /// 鍓嶆櫙鏄櫧鐨勶級锛岄偅鏃跺€欑敤棰滆壊鍖哄垎灏卞畬鍏ㄥけ鏁堜簡銆?    ///
    /// 鐐硅窛 5銆佸崐寰?2 / 1.5锛? 涓偣绔栨帓杩炶捣鏉ュ崰 19 楂橈紝娈甸珮 26 閲屽寰椾笅锛堜笂涓嬪悇浣?3.5锛夛紝
    /// 妯潃鍙崰 4 瀹解€斺€旇繖灏辨槸"鐐规尓鍒板浘鏍囧彸杈?鑳界渷鍑烘潵缁欏浘鏍囩殑閭ｇ偣鍦版柟锛堣 `DrawSegment`锛夈€?    /// </summary>
    private void DrawPips(ID2D1DeviceContext ctx, float cx, float cy,
                          int cur, int count, Color4 fg)
    {
        const float gap = 5f;
        float y0 = cy - (count - 1) * gap * 0.5f;
        for (int k = 0; k < count; k++)
        {
            bool on = k == cur;
            var c = on ? fg : new Color4(fg.R, fg.G, fg.B, 0.35f);
            float rad = on ? 2f : 1.5f;
            ctx.FillEllipse(new Ellipse(new Vector2(cx, y0 + k * gap), rad, rad), Brush(ctx, c));
        }
    }

    /// <summary>
    /// 鍥惧舰閭ｄ竴鎺?*鐢ㄥ浘鏍囦笉鐢ㄦ枃瀛?*锛堢収鍋囬潰鏉匡級锛氬浘褰㈢殑杞粨姣?鐩寸嚎/鐭╁舰/妞渾"鍑犱釜瀛?    /// 涓€鐪煎緱澶氾紝鑰屼笖涓嶇敤涓哄嚑涓瓧鍘婚噺瀹藉害锛堜竷娈典箣鍚庢瘡娈靛彧鏈?~79 鍍忕礌锛屽啓"骞宠鍥涜竟褰?
    /// 鍥涗釜瀛楁牴鏈斁涓嶄笅锛夈€?    /// </summary>
    private string ShapeIcon(int i) => ShapeIcon(ShapeToolAt(i));

    /// <summary>
    /// **鍥惧舰绉嶇被 鈫?鍥炬爣鍚?*锛堝甫涓婄姸鎬佺殑閭ｄ竴浠斤級锛氱洰鍓嶅嚑澶勮窡鐘舵€佹湁鍏斥€斺€?    /// 鎶涚墿绾胯**杞垚褰撳墠寮€鍙ｆ柟鍚?*锛堣 <see cref="ParabolaIconName"/>锛夈€?    /// 鐩寸嚎瑕?*鎹㈡垚褰撳墠绾垮瀷**锛堣 <see cref="LineIconName"/>锛夈€?    /// 妫辨煴 / 妫遍敟 / 妫卞彴瑕?*鎹㈡垚褰撳墠妗ｇ殑杈规暟**锛堣 <see cref="SolidIconName"/>锛夈€?    /// 鍧愭爣绯昏**鎹㈡垚"甯︿笉甯︾綉鏍?**锛?026-09-24锛岃涓嬮潰閭ｄ竴琛岋級銆?    ///
    /// 涓轰粈涔堥潪璺熺姸鎬佷笉鍙細杩欏嚑鏍?鐐圭浜屼笅鎹竴妗?锛屽浘鏍囦笉璺熺潃鎹㈢殑璇濓紝
    /// 鑰佸笀鐪嬩笉鍑洪偅涓€涓嬪埌搴曟湁娌℃湁鐢熸晥锛堜笁澶勯兘鏄敤鎴?2026-09-20 瀹氱殑锛夈€?    /// </summary>
    private string ShapeIcon(Tool t) => t switch
    {
        Tool.Parabola => ParabolaIconName(_host.State.ParabolaAxis),
        Tool.Line => LineIconName(_host.State.LineDash),
        // 鍙屾洸绾匡細鏈?/ 鏃犳笎杩戠嚎涓ゅ紶锛?026-09-22锛夆€斺€旈偅涓€鏍?鍐嶇偣涓€娆℃崲涓€妗?锛?        // 鍥炬爣涓嶈窡鐫€鎹㈢殑璇濓紝鑰佸笀鐪嬩笉鍑鸿繖涓€绗斿埌搴曚細涓嶄細甯﹂偅涓ゆ潯铏氱嚎銆?        Tool.Hyperbola => _host.State.HyperbolaAsymptotes ? "hyperbola" : "hyperbolaNoAsym",
        // 妞渾锛堝甫鐒︾偣锛夛細鏈?/ 鏃犵劍鐐逛笁瑙掑舰涓ゅ紶锛?026-09-22锛夛紝鐞嗙敱鍚屼笂銆?        Tool.ConicEllipse => _host.State.EllipseFocusTriangle ? "ovalFocusTri" : "ovalFocus",
        // 鍧愭爣绯伙細甯︾綉鏍?/ 涓嶅甫缃戞牸涓ゅ紶锛?026-09-24锛夆€斺€旇繖涓€妗ｄ粠銆屾洿澶氥€嶆娊灞夋尓鍒颁簡杩欎竴鏍?        //锛堢敤鎴凤細"鐐逛竴涓嬪垏鎹㈡垚缃戞牸锛岀偣涓€涓嬬綉鏍兼病浜?锛夛紝鍥炬爣鍚屾牱蹇呴』璺熺潃鎹紝鍚﹀垯
        // "杩欎竴绗旂敾鍑烘潵甯︿笉甯︽牸瀛?鐪嬩笉鍑烘潵銆傛樉绀虹殑鏄?*"浠ュ悗鏂扮敾鐨勯偅浜?**閭ｄ竴妗?        //锛堝凡缁忕敾鍦ㄦ澘涓婄殑鍚勫瓨鍚勭殑锛岃 Stroke.Grid锛夈€?        Tool.Coordinate => _host.State.CoordGridDefault ? "axesGrid" : "axes",
        _ when ShapeSpec.HasSideCount(t) => SolidIconName(t, _host.State.SidesOf(t)),
        _ => ShapeIconFor(t),
    };

    /// <summary>
    /// 绔嬩綋鍥惧舰閭ｄ竴鏃忕殑鍥炬爣鍚嶏細`prism3` 锝?`frustum6`锛堝墠缂€鎸夊伐鍏枫€佸悗缂€鎸夊綋鍓嶆。杈规暟锛夈€?    ///
    /// 鍜岀洿绾?/ 鎶涚墿绾垮悓涓€鏉＄悊鐢憋細閭ｅ嚑鏍?鍐嶇偣涓€娆℃崲涓€妗?锛屽浘鏍囦笉璺熺潃鎹㈠氨鐪嬩笉鍑烘潵銆?    /// 鈿?**18 鍍忕礌涓?浜?鍜?鍏?銆佷互鍙婃１鏌?/ 妫卞彴**鍙兘涓嶅お鍒嗗緱寮€锛屾墍浠ラ偅涓€鏍煎彸杈?    /// 杩樻湁 **4 涓。浣嶇偣**鍏滃簳锛堣 `DrawPips`锛夆€斺€旀暟鐐规瘮鏁拌竟鍙潬銆?    /// </summary>
    private static string SolidIconName(Tool t, int sides)
    {
        string stem = t switch
        {
            Tool.Pyramid => "pyramid",
            Tool.Frustum => "frustum",
            _ => "prism",                              // 妫辨煴锛堜篃鏄厹搴曪級
        };
        return stem + Math.Clamp(sides, 3, 6);
    }

    /// <summary>
    /// 鎶涚墿绾跨殑鍥炬爣鍚嶆寜**褰撳墠妗ｄ綅**鎹紙`parabola` = 涓婁笅鎶涚墿 / `parabolaRight` = 宸﹀彸鎶涚墿锛?    /// 瑙?<see cref="IconAtlas.Draw"/>锛夈€?    ///
    /// 涓轰粈涔堥潪寰楀彉锛氱敤鎴?2026-09-20 鎶?涓婁笅杩樻槸宸﹀彸"浠庨€変腑妗嗘尓鍒颁簡**鐢讳箣鍓?*瀹?    /// 锛堥偅鏍煎凡缁忛€変腑鎶涚墿绾挎椂鍐嶇偣涓€娆★紝瑙?`ActivateSegment` 鐨?case 8锛夈€?    /// 鍥炬爣涓嶈窡鐫€鎹㈢殑璇濓紝"鐐圭浜屼笅鍒板簳鏈夋病鏈夌敓鏁?灏辨病娉曠湅鍑烘潵銆?    ///
    /// 鍙湁涓や釜鍚嶅瓧锛?*鍏蜂綋鏈濆摢杈圭敱鐢荤殑鏃跺€欓偅涓€鎷栧畾**锛堢敤鎴?2026-09-20 鏇存櫄鐨勫彛寰勶細
    /// "鎰熻涓嶅锛岃繕鏄弬鑰冧粬鐨勯€昏緫"锛汭nkClass 涔熸槸涓や釜鎸夐挳 `case 20/21`锛夈€?    /// </summary>
    private static string ParabolaIconName(CurveAxis axis)
        => ParabolaAxisIndex(axis) == 1 ? "parabolaRight" : "parabola";

    /// <summary>
    /// 鎶涚墿绾块偅涓€鏍?*涓ゆ。**閲岀殑绗嚑妗ｏ紙0 = 涓婁笅鎶涚墿銆? = 宸﹀彸鎶涚墿锛夈€?    ///
    /// 涓轰粈涔堣鏈夊畠锛歚CurveAxis` 鏄?*鍥涗釜鍊?*锛堜笂涓嬪乏鍙冲悇涓€涓柟鍚戯紝鍏蜂綋鏈濆摢杈圭敱鐢荤殑鏃跺€?    /// 閭ｄ竴鎷栧畾锛夛紝鑰岄潰鏉胯繖涓€鏍煎彧绠?**鍝竴瀵?*"銆傛墍浠?鍥炬爣鐢诲摢寮?鍜?妗ｄ綅鐐圭偣绗嚑涓?
    /// 閮藉緱鍏堟妸鍥涗釜鍊兼姌鎴愪袱妗ｂ€斺€?*杩欎釜鎶樼畻鍙湁杩欎竴澶?*锛屼袱澶勫悇鍐欎竴閬嶈繜鏃╁涓嶄笂
    /// 锛堣〃鐜版槸"鍥炬爣鎹簡銆佺偣娌¤窡鐫€鍔?锛夈€?    /// </summary>
    private static int ParabolaAxisIndex(CurveAxis axis)
        => axis is CurveAxis.OpenRight or CurveAxis.OpenLeft ? 1 : 0;

    /// <summary>
    /// 鐩寸嚎鐨勫浘鏍囧悕鎸?*褰撳墠绾垮瀷**鎹紙`line` / `lineDash` / `lineDot`锛岃 <see cref="IconAtlas.Draw"/>锛夈€?    ///
    /// 鍜屾姏鐗╃嚎閭ｆ潯鍚屼竴涓悊鐢憋細閭ｄ竴鏍?鍐嶇偣涓€娆℃崲涓€妗?锛堢敤鎴?2026-09-20 瀹氾細
    /// "鐐瑰嚮鐩寸嚎鐨勫浘鏍囷紝瀹冧細鍙樻垚铏氱嚎锛屽啀鐐瑰嚮鍙樻垚鐐硅櫄绾匡紝鍐嶇偣鍑诲張鍙樻垚鐩寸嚎鈥︹€︾渷浜嗗ソ鍑犱釜绌洪棿鏍?锛夛紝
    /// 鍥炬爣涓嶈窡鐫€鎹㈢殑璇濓紝鑰佸笀鐐瑰畬鐪嬩笉鍑轰笅涓€绗斾細鏄疄绾胯繕鏄櫄绾裤€?    ///
    /// 涓夊紶閮芥槸**鑷粯**鐨勶紙`IconAtlas` 閲屽悓涓€鏍圭嚎鍙崲鐢绘硶锛夆€斺€斾笂娓稿浘鏍囧簱閲屼竴涓櫄绾夸笓鍚嶉兘娌℃湁
    /// 锛堣繛 `lineWeight` 閮藉彧鏈夊疄绾块偅涓€寮狅級锛岃€屼笖涓夊紶蹇呴』**涓€鐪肩湅鍑烘槸鍚屼竴鏍圭嚎鐨勪笁妗?*锛?    /// 鍑戜笂娓哥殑涓夊紶鍥惧弽鑰屼細鑺便€?    /// </summary>
    private static string LineIconName(StrokeDash dash) => dash switch
    {
        StrokeDash.Dashed => "lineDash",
        StrokeDash.Dotted => "lineDot",
        _ => "line",                               // 瀹炵嚎锛堜篃鏄厹搴曪級
    };

    /// <summary>
    /// **鍥惧舰绉嶇被 鈫?鍥炬爣鍚?*銆備笂甯﹂偅鍑犳鍜屼富鏉?鍥惧舰"閭ｄ竴鏍?*鍏辩敤杩欎竴浠?*锛?    /// 涓绘潯涓婃樉绀虹殑蹇呴』灏辨槸褰撳墠绉嶇被鐨勫舰鐘讹紝涓ゅ鍚勫啓涓€浠借繜鏃╁涓嶄笂
    /// 锛堣〃鐜版槸"涓婂甫閲岀偣浜嗕笁瑙掑舰锛屼富鏉￠偅鏍艰繕鏄煩褰?锛夈€?    ///
    /// 鍚嶅瓧瀵逛笉涓婄殑閭ｅ嚑涓槸鑷粯鐨勶紙`oval` / `parallelogram` / `axes` / `numberline`銆?    /// 2026-09-20 鍔犵殑鍥涚鏇茬嚎 `parabola` / `hyperbola` / `sine` / `cosine`锛?    /// 浠ュ強鐩寸嚎鐨勪笁妗ｇ嚎鍨?`line` / `lineDash` / `lineDot`锛夛紝
    /// 瑙?<see cref="IconAtlas.Draw"/>锛氫笂娓稿浘鏍囧簱閲屾病鏈夎繖浜涗笓鍚嶃€?    /// </summary>
    private static string ShapeIconFor(Tool t) => t switch
    {
        Tool.Rectangle => "square",
        Tool.Ellipse => "oval",                    // 鑷粯锛欶luent 鍙湁姝ｅ渾
        Tool.Circle => "circle",
        Tool.Triangle => "triangle",
        Tool.Parallelogram => "parallelogram",     // 鑷粯锛欶luent 娌℃湁杩欎釜涓撳悕
        // 鍧愭爣绯?/ 鏁拌酱涔熸槸鑷粯鐨勶紙瑙?IconAtlas.DrawAxes / DrawNumberLine锛夛細
        // 涓婃父鍥炬爣搴撻噷娌℃湁"涓ゆ潯杞?鍜?涓€鏉″甫鍒诲害鐨勮酱"杩欎袱涓笓鍚嶃€?        Tool.Coordinate => "axes",
        Tool.NumberLine => "numberline",
        // 鍥涚鏇茬嚎锛氬悓鏍疯嚜缁橈紙瑙?IconAtlas.DrawParabola / DrawHyperbola / DrawWave锛夈€?        // 鍥炬爣鐢荤殑鏄?鐞嗘兂鏍峰瓙"锛堝紑鍙ｅ悜涓婄殑鎶涚墿绾?/ a = b 鐨勫弻鏇茬嚎 / 涓€涓懆鏈燂級銆?        // **鎶涚墿绾块偅涓€寮犱細璺熺潃褰撳墠寮€鍙ｆ柟鍚戣浆**鈥斺€斿畠涓嶆槸杩欎竴涓悕瀛楃殑浜嬶細
        // `ShapeIcon(Tool)` 浼氭浛瀹冩崲鎴?`parabolaRight` / `parabolaDown` / `parabolaLeft`锛?        // 鎵€浠ヨ繖閲岀粰鐨勬槸"榛樿锛堝紑鍙ｅ悜涓婏級"閭ｄ竴妗ｏ紝涔熸槸鍚嶅瓧涓嶅鏃剁殑鍏滃簳銆?        Tool.Parabola => "parabola",
        Tool.Hyperbola => "hyperbola",
        // 妞渾锛堝甫鐒︾偣锛夛紙2026-09-22锛夛細鑷粯锛堣 IconAtlas.DrawOvalFocus锛夆€斺€旀き鍦?锛?涓ょ劍鐐?        // 锛嬶紙榛樿閭ｄ竴妗ｏ級鐒︾偣涓夎褰€傚叿浣撳摢涓€寮犵敱 `ShapeIcon(Tool)` 鎸夊綋鍓嶆。鎹?        //锛堣閭ｉ噷鐨勬敞閲婏級锛岃繖閲屾槸**榛樿锛堟湁鐒︾偣涓夎褰級**閭ｅ紶锛屼篃鏄涓嶅嚭鏉ユ椂鐨勫厹搴曘€?        Tool.ConicEllipse => "ovalFocusTri",
        Tool.Sine => "sine",
        Tool.Cosine => "cosine",
        // 娉㈡氮绾匡紙绗崄鍏壒锛夛細鑷粯锛堣 IconAtlas.DrawWaveLine锛夆€斺€?*濂藉嚑涓懆鏈?*鐨勬寮︽尝锛?        // 鍜屻€屾寮︺€嶉偅涓€寮狅紙涓€涓懆鏈燂級涓€鐪艰兘鍒嗗紑銆?        Tool.Wave => "wave",
        // 姝ｅ垏锛堢鍗佷簲鎵癸級锛氳嚜缁橈紙瑙?IconAtlas.DrawTangent锛夆€斺€斾竴鏀洸绾?锛?涓ゆ潯娓愯繎绾裤€?        Tool.Tangent => "tangent",
        // 绔嬩綋鍥惧舰锛?026-09-20 绗簲鎵癸級锛氬悓鏍疯嚜缁橈紙瑙?IconAtlas.DrawCylinder / DrawCone 绛夛級銆?        Tool.Cylinder => "cylinder",
        Tool.Cone => "cone",
        // 鍦嗗彴锛堢鍗佷笁鎵癸級锛氳嚜缁樼殑绗笁寮犳棆杞綋鍥炬爣锛堣 IconAtlas.DrawConeFrustum锛夈€?        Tool.ConeFrustum => "conefrustum",
        // 鐞冿紙绗崄鍥涙壒锛夛細鑷粯锛堣 IconAtlas.DrawSphere锛夆€斺€旇疆寤撳渾 锛?璧ら亾妞渾銆?        Tool.Sphere => "sphere",
        // 鈿?闀挎柟浣?/ 鍥涢潰浣?2026-09-20 绗崄浜屾壒**鎾や簡闈㈡澘鍏ュ彛**锛堣 ShapeRows 閲岀珛浣撻偅琛岋級锛?        // 浣嗗畠浠殑涓ゅ紶鍥炬爣鐣欑潃鈥斺€斾富鏉￠偅涓€鏍煎湪"閫変腑鐨勬槸鏃ф澘涔﹂噷鐨勪竴涓暱鏂逛綋"鏃惰繕瑕佺敾瀹冦€?        Tool.Cuboid => "cuboid",
        Tool.Tetrahedron => "tetrahedron",
        // 妫辨煴 / 妫遍敟 / 妫卞彴锛氬叿浣撳摢涓€寮犵敱 `ShapeIcon(Tool)` 鎸夊綋鍓嶆。鎹紙瑙?SolidIconName锛夈€?        // 杩欓噷鏄?*榛樿锛堝洓妫憋級**閭ｅ紶锛屼篃鏄涓嶅嚭鏉ョ殑鍏滃簳銆?        Tool.Prism => "prism4",
        Tool.Pyramid => "pyramid4",
        Tool.Frustum => "frustum4",
        Tool.Arrow => "arrowRight",
        // 鐩寸嚎锛氳嚜缁樹笁寮狅紙瀹炵嚎 / 铏氱嚎 / 鐐圭嚎锛岃 IconAtlas锛夈€傝繖閲岀粰鐨勬槸**瀹炵嚎**閭ｄ竴寮狅紝
        // 鍏蜂綋鍝竴寮犵敱 `ShapeIcon(Tool)` 鎸夊綋鍓嶇嚎鍨嬫崲锛堣 LineIconName锛夈€?        Tool.Line => "line",
        _ => "lineWeight",                         // 璁や笉鍑烘潵鐨勫厹搴?    };

    private string SegmentLabel(int i) => _bandCell switch
    {
        6 => i == 0 ? "鏁寸瑪鎿? : "闈㈢Н鎿?,
        7 => i == 0 ? "鐭╁舰" : "濂楃储",
        // 鎴浘锛氬悕瀛楃収寰俊閭ｅ锛?.3.1锛夆€斺€?鎴浘" = 灞忓箷鍘熸牱锛堣繛鎴戜滑鐨勬澘涔︿竴璧凤級锛?        // "闅愯棌绐楀彛鎴浘" = 鍏堟妸鎴戜滑杩欏眰钘忚捣鏉ワ紝鎷嶅埌鐨勫彧鏈変笅灞傚唴瀹广€?        9 => i == 0 ? "鎴浘" : "闅愯棌绐楀彛鎴浘",
        8 => "",                                   // 鍥惧舰锛氱敾鍥炬爣锛堣 ShapeIcon锛?        _ => "",
    };

    private bool IsSegmentActive(in UiState st, int i) => _bandCell switch
    {
        2 => BoardSegAt(i).Kind == BoardSegKind.Color && BoardColorIs(st, BoardSegAt(i).Idx),
        6 => i == 0 ? st.Tool == Tool.Eraser : st.Tool == Tool.PixelEraser,
        7 => i == 0 ? st.SelectMode == SelectMode.Rect : st.SelectMode == SelectMode.Lasso,
        8 => i >= 0 && i < ShapeSegmentCount && st.Tool == ShapeToolAt(i),
        9 => i == (st.CaptureHideInk ? 1 : 0),     // 鎴浘锛氬綋鍓嶆槸鍝竴绉嶆埅娉曞氨楂樹寒鍝竴娈?        _ => false,
    };

    private static bool BoardColorIs(in UiState st, int i)
    {
        var c = InkPalette.BoardPresets[i].Color;
        return MathF.Abs(c.R - st.BoardColor.R) < 0.02f
            && MathF.Abs(c.G - st.BoardColor.G) < 0.02f
            && MathF.Abs(c.B - st.BoardColor.B) < 0.02f;
    }

    private bool IsOn(int i) => Rows[i].Kind switch
    {
        Row.DarkTheme => _dark,
        // 鑹插甫甯稿紑锛?026-10-05锛夛細鍚岃创杈归殣钘忥紝鐣岄潰鑷繁鐨勫亸濂姐€?        Row.RailPin => _railPinned,
        // 鎮仠鎻愮ず锛堢晫闈㈣嚜宸辩殑鍋忓ソ锛岄粯璁ゅ紑锛涜瑙佸瓧娈靛尯閭ｄ竴娈碉級
        Row.Tooltip => _tipEnabled,
        // 鍋滈】鎴愬瀷锛?*榛樿寮€**锛屾墍浠ラ厤缃噷娌℃湁杩欎竴椤规椂鏄剧ず鐨勫氨鏄?寮€"
        //锛堣 LoadPrefs 閲岄偅涓€琛岋細鍙湁璇诲埌 "0" 鎵嶅叧锛夈€?        Row.DwellShape => _host == null || _host.State.DwellShapeOn,
        // 鍘嬫劅绮楃粏锛氱姸鎬佸湪**寮曟搸**锛堟覆鏌撴湡寮€鍏筹級锛岀晫闈㈠彧鏄樉绀哄畠
        Row.Pressure => _host == null || _host.State.PressureOn,
        // 澧ㄨ抗棰勬祴锛氬悓鏍峰湪寮曟搸锛堥粯璁ゅ紑锛夛紝鐣岄潰鍙槸鏄剧ず瀹?        Row.Predict => _host == null || _host.State.PredictTailOn,
        // 澧ㄨ抗涓ゆ潯寮€鍏筹細璇诲亸濂斤紙restoreInk 榛樿鍏炽€乸ptAutoSave 榛樿寮€锛夈€?        Row.RestoreInk => _host.GetPref("restoreInk") == "1",
        Row.PptAutoSave => _host.GetPref("pptAutoSave") != "0",
        _ => _hideEnabled,
    };

    private static string ProfileName(int i) => i switch { 0 => "鏋佺畝", 1 => "鑷畾涔?, _ => "瀹屾暣" };

    /// <summary>寮€鍏筹細鎵撳紑鐨勭敤寮鸿皟鑹诧紝鍏崇殑鏄祬搴?+ 鎻忚竟锛涙粦閽湪鍙?宸︺€?/summary>
    private void DrawSwitch(ID2D1DeviceContext ctx, RectF r, bool on)
    {
        float radius = (r.MaxY - r.MinY) * 0.5f;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, on ? Tokens.Accent : HoverCol));
        if (!on) ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        float k = 14f;
        float cx = on ? r.MaxX - k * 0.5f - 3f : r.MinX + k * 0.5f + 3f;
        var c = new Vector2(cx, (r.MinY + r.MaxY) * 0.5f);
        ctx.FillEllipse(new Ellipse(c, k * 0.5f, k * 0.5f), Brush(ctx, Tokens.AccentInk));
    }

    /// <summary>鐢荤 pos 涓樉绀哄嚭鏉ョ殑鏍煎瓙锛坧os 鏄?褰撳墠妗ｄ綅閲岀殑搴忓彿"锛宨 鏄畬鏁存。涓嬫爣锛夈€?/summary>
    private void DrawCell(ID2D1DeviceContext ctx, int pos, in UiState st)
    {
        var vis = VisibleCells();
        int i = vis[pos];
        var r = CellRect(pos);
        bool active = IsActive(i, st);
        bool hover = _hover == i || _press == i;

        if (i > 0)
        {
            // 鍒嗛殧绾跨敾鍦?缁勫彉浜?鐨勫湴鏂光€斺€旀寜**鏄剧ず鍑烘潵鐨勯偦灞?*鍒わ紝涓嶆槸鎸夊畬鏁存。鐨勪笅鏍?            if (pos + 1 < vis.Length && GroupOf(i) != GroupOf(vis[pos + 1]))
            {
                float x = r.MaxX + Tokens.GroupDivider * 0.5f;
                ctx.DrawLine(new Vector2(x, r.MinY + 10), new Vector2(x, r.MaxY - 10),
                             Brush(ctx, BorderCol), 1f);
            }

            if (active)
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, Tokens.Accent));
            }
            else if (hover && !CellUnavailable(i, st))
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, HoverCol));
            }
        }

        if (i == 0)
        {
            // 鏀惰捣鏍硷細涓€涓皬鐞?+ 褰撳墠绗旇壊閭ｅ湀锛堝拰鏀惰捣鎬侀偅涓悆鏄悓涓€涓笢瑗匡紝鍙槸鍙樺皬锛?            var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
            float d = 32f;
            var ring = new Ellipse(c, d * 0.5f, d * 0.5f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 2.5f);
            IconAtlas.DrawCentered(ctx, "pen", r, 16f, Brush(ctx, InkCol));
            return;
        }

        // **鍥惧舰閭ｄ竴鏍硷紙8锛夌殑鍥炬爣**锛?*杩欎竴鏍间寒鐫€锛堟墜閲屽氨鏄煇绉嶅浘褰級鎵嶈窡鐫€褰撳墠绉嶇被璧?*锛?        // 娌′寒鐨勬椂鍊欑敾 Fluent 閭ｄ釜閫氱敤鐨?涓や釜鍥惧舰鍙犲湪涓€璧?锛坄Cells[8].Icon`锛夈€?        //
        // 涓轰粈涔堝姞浜嗗悗鍗婁釜鏉′欢锛堢敤鎴?2026-09-22 瀹氾級锛氳繖涓€鏍煎師鏉?*浠讳綍鏃跺€?*閮界敾褰撳墠绉嶇被锛?        // 浜庢槸"涓婃鐢ㄨ繃鐭╁舰"涔嬪悗锛屾墜閲屾槑鏄庢槸绗旓紝鍥炬爣杩樼敾鐫€涓€涓煩褰⑩€斺€旀病閫変腑灏辨槸閫氱敤鐨?        // 鍥惧舰鍏ュ彛锛岀敾鎴愭煇涓€绉嶅叿浣撶殑鍥惧舰锛屼笉鐔熸倝鐨勪汉浼氫互涓?杩欎竴鏍煎氨鏄煩褰?銆?        // 閫変腑涔嬪悗璺熺潃绉嶇被鍙樼殑閭ｅ崐鏉＄収鏃э紙2026-09-19 瀹氱殑鐞嗙敱锛夛細涓冪鍥惧舰鎸ゅ湪涓€鏍奸噷锛?        // "shapes" 閭ｄ釜閫氱敤鍥炬爣浠€涔堜篃娌¤鈥斺€旀墜閲屾槸涓夎褰㈣繕鏄钩琛屽洓杈瑰舰锛屽彧鑳芥墦寮€涓婂甫鎵嶇煡閬撱€?        // 鎵€浠ョ幇鍦ㄦ槸**褰撳墠閭ｄ竴绉?*锛堝拰涓婂甫閲岄珮浜殑閭ｄ竴娈靛悓涓€寮犲浘锛屽叡鐢?ShapeIconFor锛夈€?        //
        // 浠ｄ环锛堟槑纭帴鍙楋級锛氫竷绉嶅浘褰?*閮芥病鏈?filled 鍙樹綋**锛團luent 琛ㄩ噷娌℃湁鐢熸垚锛夛紝
        // 鎵€浠ラ€変腑鎬佷笉鍐嶅儚鍒殑鏍奸偅鏍峰彉瀹炲績锛岃€屾槸"鍚屼竴涓疆寤?+ 寮鸿皟鑹插簳 + 鐧藉浘鏍?
        // 鈥斺€斿拰涓婂甫閲岄€変腑鐨勯偅涓€娈垫槸鍚屼竴绉嶇敾娉曘€?        var ink = active ? Tokens.AccentInk : InkCol;
        // **鎾ら攢/閲嶅仛鏍堢┖ 鈫?鍘嬫殫**锛堢敤鎴?2026-09-17锛?鎾ら攢閲嶅仛鐏板害"锛夈€?        // 寮曟搸鏃╁氨鎶?UndoDepth / RedoDepth 閫掔粰鐣岄潰浜嗭紝鍙槸鐣岄潰涓€鐩存病鐢ㄣ€?        // 鍘嬫殫鑰屼笉鏄棌璧锋潵锛氫綅缃浐瀹氥€佽€佸笀涓嶇敤鍘绘壘锛涚偣瀹冧篃娌′簨锛堝紩鎿庨偅杈规槸绌烘搷浣滐級銆?        if (CellUnavailable(i, st)) ink = new Color4(ink.R, ink.G, ink.B, 0.30f);
        if (PerfSkipIcons) return;
        DrawCellIcon(ctx, i, r, Tokens.Icon, ink, active, st);
    }

    /// <summary>
    /// **鐢绘煇涓€鏍肩殑鍥炬爣**鈥斺€斾富鏉★紙<see cref="DrawCell"/>锛夊拰銆屾洿澶氥€嶉潰鏉块噷閭ｆ帓閽変綇瀹牸
    /// **鍏辩敤杩欎竴澶?*銆?    ///
    /// 涓轰粈涔堝繀椤绘敹鎴愪竴澶勶細鐢ㄦ埛 2026-09-26 鐗规剰鎻愰啋"鏇村閲岄潰鏈変竴涓缃紝閭ｉ噷闈㈢殑鍥炬爣涔熻鍚屾璧锋潵"銆?    /// 褰撳勾涓ゅ鍚勫啓浜嗕竴閬嶏紙寮瑰嚭闈㈡澘閭ｅ彞鍙 `Cells[cell].Icon`锛夛紝浜庢槸鐧芥澘閭ｄ竴鏍煎湪甯﹀瓙涓婃槸鑷粯鐨勬澘銆?    /// 鍦ㄩ潰鏉块噷杩樻槸 Fluent 閭ｄ釜"绐楀彛甯冨眬"锛涙鐨?/ 閫夋嫨杩欎竴杞崲浜嗗浘鏍囦箣鍚庝篃浼氱珛鍒诲啀鐘竴娆°€?    /// 浠撳簱閲?鍚屼竴涓悕鍗曞啓涓ゅ銆佹敼涓€澶勫繀婕忎竴澶?宸茬粡鏍借繃濂藉嚑娆★紙瑙?鏋舵瀯-鍒嗗眰涓庤鍒?md 浜?7锛夛紝
    /// 鎵€浠ヨ繖涓嶆槸"椤烘墜鍚堝苟"锛屾槸淇偅涓瘺鐥呮湰韬€?    ///
    /// <paramref name="active"/>锛氳繖涓€鏍间寒涓嶄寒锛堥潰鏉块噷鎭掍负 false鈥斺€旈偅閲岀殑鏍奸兘涓嶆槸閫変腑鎬侊級銆?    /// </summary>
    private void DrawCellIcon(ID2D1DeviceContext ctx, int cell, RectF r, float size,
                              in Color4 ink, bool active, in UiState st)
    {
        var brush = Brush(ctx, ink);
        switch (cell)
        {
            // 鐧芥澘锛氭澘寮€鐫€鐨勬椂鍊欐澘闈㈠～鎴?*杩欏潡鏉跨殑棰滆壊**锛堣 IconAtlas.DrawBoard锛?            case 2:
                IconAtlas.DrawBoard(ctx, r, size, brush, active ? st.BoardColor : null);
                return;
            // 婵€鍏夌瑪锛氶偅涓ゅ紶锛堣 IconAtlas.DrawLaserCell锛?            case 5:
                IconAtlas.DrawLaserCell(ctx, r, size, brush, active);
                return;
            default:
                IconAtlas.DrawCentered(ctx, CellIconName(cell, st), r, size, brush);
                return;
        }
    }

    /// <summary>
    /// **杩欎竴鏍肩敾鍝釜鍥炬爣鍚?*鈥斺€?鏍煎瓙 鈫?鍥炬爣"鐨?*鍞竴涓€澶勫垽鎹?*锛?    /// 缁樺埗锛?see cref="DrawCellIcon"/>锛夊拰鑷锛?see cref="CellIconForTest"/>锛夐兘闂畠锛?    /// 鍏嶅緱鍙堝嚭鐜?鑷璇寸敾鐨勬槸 A銆佸睆骞曚笂鍏跺疄鏄?B"锛堥偅鏉?鈿?瑙?CellIconForTest锛夈€?    /// 鐧芥澘 / 婵€鍏夐偅涓ゆ牸瑕侀澶栧弬鏁帮紙鏉胯壊銆佷袱鎬侊級锛屼笉鍦ㄨ繖寮犺〃閲岋紝鐢?DrawCellIcon 鐩存帴璋冪粯鍒躲€?    ///
    /// 2026-09-26 鐢ㄦ埛鎸戠殑杩欎竴杞細
    ///   路 **姗＄毊锛?锛?*锛氭湭閫変腑锛漙eraserPure`锛?*Radix 閭ｅ紶绾鐨?*锛孧IT锛?5 缃戞牸锛?    ///     鐢ㄦ埛 2026-09-26 浠庡€欓€夐噷鎸戠殑 B2鈥斺€旂悊鐢辨槸杩欎竴鏃忛噷瀹冭矾寰勪腑**涓€鏍规í绾块兘娌℃湁**锛夛紱
    ///     閫変腑锛濊窡鐫€褰撳墠鏄摢绉嶆鐨崲锛?    ///     鏁寸瑪鎿︼紳`eraser`锛團luent 鍘熺増锛屽甫妯嚎锛夈€侀潰绉摝锛漙eraserMedium`锛堟鐨紜涓€涓ぇ鍦堬級锛?    ///   路 **閫夋嫨锛?锛?*锛氭湭閫変腑锛漙lucideSelectPointer`锛圠ucide 铏氱嚎妗嗭紜鎸囬拡锛夛紱
    ///     閫変腑锛濇閫?`mdiSelection`锛圡DI 鍥涜鎷彿锛嬫柇缁竟锛? 濂楃储 `lasso`锛團luent锛夈€?    /// </summary>
    private string CellIconName(int cell, in UiState st) => cell switch
    {
        // 鍥惧舰閭ｄ竴鏍硷細浜潃锛堟墜閲屽氨鏄煇绉嶅浘褰級鎵嶈窡鐫€褰撳墠绉嶇被璧帮紝娌′寒鐢婚€氱敤鐨勯偅寮狅紙瑙?DrawCell 閭ｆ锛?        8 => IsActive(8, st) ? ShapeIcon(st.Tool) : Cells[8].Icon,
        6 => IsActive(6, st)
                ? (st.Tool == Tool.PixelEraser ? "eraserMedium" : "eraser")
                : "eraserPure",
        7 => IsActive(7, st)
                ? (st.SelectMode == SelectMode.Lasso ? "lasso" : "mdiSelection")
                : "lucideSelectPointer",
        _ => IsActive(cell, st) ? Cells[cell].Filled : Cells[cell].Icon,
    };

    /// <summary>
    /// 杩欎竴鏍肩幇鍦ㄧ畻涓嶇畻"閫変腑鐨?銆?    ///
    /// **绌块€忓拰宸ュ叿鏄簰鏂ョ殑**锛堢敤鎴?2026-09-17 瀹氾級锛氱┛閫忓紑鐫€鐨勬椂鍊欑偣鍑昏惤鍒颁笅灞傜▼搴忎笂锛?    /// 鐢诲竷鏍规湰鏀朵笉鍒扮瑪銆傛墍浠ヨ繖鏃跺€欏伐鍏锋牸涓€寰嬩笉楂樹寒鈥斺€斾笉鐒朵細鍑虹幇"榧犳爣"鍜?绗?鍚屾椂浜潃锛?    /// 鑰佸笀浠ヤ负鍦ㄥ啓瀛椼€佸啓鍑烘潵涓€涓瓧閮芥病鏈夈€?    ///
    /// 鐧芥澘锛?锛夋槸渚嬪锛氬畠琛ㄧず鐨勬槸"鏉垮紑鐫€"杩欎釜浜嬪疄锛屽拰鑳戒笉鑳藉啓瀛楁棤鍏筹紝绌块€忔椂鐓у父鏄剧ず銆?    /// 寮曟搸閭ｈ竟閰嶅悎鐫€鏀逛簡锛?*鎹㈠伐鍏蜂細鑷姩鍏虫帀绌块€?*锛坄InkEngine.SwitchTool`锛夛紝
    /// 鎵€浠ョ偣涓€涓嬪伐鍏锋牸灏辫兘绔嬪埢鍐欏瓧锛屼笉鐢ㄥ厛鍘荤偣"榧犳爣"鎶婂畠鍏虫帀銆?    /// </summary>
    private bool IsActive(int i, in UiState st)
    {
        if (i == 1) return st.PassThrough;
        if (i == 2) return st.Board;
        if (st.PassThrough) return false;
        return i switch
        {
            3 => st.Tool == Tool.Pen,
            4 => st.Tool == Tool.Highlighter,
            5 => st.Tool == Tool.Laser,
            6 => st.Tool is Tool.Eraser or Tool.PixelEraser,
            7 => st.Tool == Tool.Marquee,
            8 => HasShapeEntry(st.Tool),
            9 => st.Tool == Tool.Capture,
            _ => false,
        };
    }

    /// <summary>
    /// 杩欎竴鏍肩幇鍦ㄦ槸涓嶆槸"涓嶅彲鐢?锛堟殏鏃跺帇鏆楃殑閭ｇ锛夈€?    ///
    /// 鐜板湪鍙湁涓€绉嶏細**鎾ら攢/閲嶅仛鏍堢┖**銆傜敤鎴?2026-09-17 瑕佺殑"鐏板害"銆?    /// 鍒ゆ嵁鐩存帴鐢ㄥ紩鎿庣粰鐨?UndoDepth / RedoDepth鈥斺€旂晫闈笉鍘荤寽"涓婁竴娆℃搷浣滄槸涓嶆槸鑳芥挙閿€"锛?    /// 閭ｇ鐚滄硶杩熸棭鍜屽紩鎿庡涓嶄笂銆?    /// </summary>
    private static bool CellUnavailable(int i, in UiState st) =>
        (i == 10 && st.UndoDepth == 0) || (i == 11 && st.RedoDepth == 0);

    /// <summary>
    /// 鐢诲埛鎸夐鑹茬紦瀛樸€?*涓嶈兘姣忓抚閲嶅缓**锛堟€ц兘璐﹂噷鐐硅繃鍚嶏細鍑犱綍涓庣敾鍒烽兘瑕佺紦瀛橈級銆?    /// 娉ㄦ剰锛氳澶囦涪澶憋紙Gfx 閲嶅缓锛変箣鍚庤繖浠界紦瀛樿閲嶅缓鈥斺€斿眾鏃跺簲閲嶆柊 SetUiFactory 鎸備竴閬嶇晫闈€?    /// </summary>
    private ID2D1SolidColorBrush Brush(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    private void Invalidate()
    {
        _host?.InvalidateUi();
    }

    // ---- 鎮仠鎻愮ず锛圱ooltip锛?026-10-02锛?-------------------------------------
    //
    // 璁捐瑙勬牸涓庤皟鐮旇銆婅皟鐮?鎮仠鎻愮ず-Tooltip.md銆嬶細
    //   路 **鍋滅暀 500ms 鎵嶅嚭**锛圵PF `ToolTipService.InitialShowDelay` 榛樿 400ms锛?    //     鍙栨澗涓€妗ｂ€斺€旀壂杩囦竴鎺掓牸瀛愪笉闂級锛涚Щ寮€ / 鎸変笅 / 鎷栧姩绔嬪埢鏀讹紱
    //   路 鍐呭 = **鍚嶇О + 褰撳墠閿綅 + 涓€鍙ヨ鏄?*锛涢敭浣嶄粠寮曟搸鏌ワ紙`IUiHost.KeyText`锛夛紝
    //     鐣岄潰涓嶆妱绗簩浠斤紝鐢ㄦ埛鏀逛簡 settings.json 鎻愮ず璺熺潃鍙橈紱
    //   路 鐢诲湪鏁村潡闈㈡澘鐨勬梺杈癸紙榛樿涓婃柟 8px锛岄《鍒板睆骞曞氨缈诲埌涓嬫柟锛夛紝**涓嶈窡鎵嬨€佷笉閬寚閽?*锛?    //   路 **瑙﹀睆娌℃湁鎮仠**锛屾墍浠ュ畠鍙槸澧炲己锛氶潰鏉夸笂鐨勫父鏄炬枃瀛椾竴涓瓧閮戒笉鍔ㄣ€?
    /// <summary>
    /// 杩欎竴鍒?璇ヤ笉璇ュ嚭鐜版彁绀?锛?*鎮仠閭ｆ潯璺?*锛涜Е鎽搁暱鎸夋湁鑷繁鐨勫紑鍏筹紝瑙?UpdateTip锛夈€?    /// 鎮仠瑕佹眰锛氬紑鍏冲紑鐫€銆佹湁鐩爣銆佹湁鏂囨銆佹病鍦ㄦ寜/鎷栥€佷篃娌℃湁瑙︽懜闀挎寜姝ｅ湪璁℃椂銆?    /// </summary>
    private bool TipWanted
        => _tipEnabled && _tipTarget != TipNoTarget
           && (_tipHoldFired
               || (_press == -1 && !_dragging && !_sliderDragging
                   && _tipHoldStart == double.NegativeInfinity))
           && TipContent(_tipTarget).Title != null;

    /// <summary>
    /// 鎵嬫寚/绗旀寜浣忎笉鍔?= 闀挎寜鍊欓€夛紙榧犳爣姘镐笉杩涜繖閲岋級銆傚埌 600ms 鐢?<see cref="UpdateTip"/> 鐐逛寒銆?    /// 鍙粰"鏈夋彁绀烘枃妗堛€佷笖闀挎寜娌℃湁琚埆鐨勫姛鑳藉崰鐢?鐨勫厓绱犳瑁咃細娓呯┖锛堟寜浣忔竻绌猴級銆佹粦鏉°€?    /// PPT 椤电爜鏍硷紙闀挎寜鑿滃崟锛夐兘涓嶄細璧板埌杩欏効锛堣皟鐢ㄧ偣宸茬粡鎺掗櫎锛夈€?    /// </summary>
    private void ArmTipHold(int target)
    {
        _tipHoldStart = _host.NowMs;
        _tipHoldTarget = target;
        _tipHoldFired = false;
        _tipLingerUntil = 0;
    }

    /// <summary>鎾ら攢闀挎寜鍊欓€夛紙绉诲姩瓒呴槇鍊笺€佺Щ寮€銆佸紑鍏冲叧鎺夈€佹柊鎸変笅閮借蛋瀹冿級銆?/summary>
    private void CancelTipHold()
    {
        _tipHoldStart = double.NegativeInfinity;
        _tipHoldTarget = TipNoTarget;
        _tipHoldFired = false;
    }

    /// <summary>銆屾洿澶氥€嶉潰鏉块噷杩欎竴鍒荤殑鎻愮ず鐩爣锛堢┖鐧?鏃?= TipNoTarget锛夈€?    /// 缂栧彿 = 1000 + 鍛戒腑鐮侊紝鍜?`MoreHitAt` 涓€涓€瀵瑰簲锛堢敾銆佸懡涓€佹彁绀哄悓婧愶級銆?/summary>
    private static int MoreTipTargetAt(int hit)
        => hit == MoreHitNone || hit == MoreHitInside ? TipNoTarget : 1000 + hit;

    /// <summary>鎸囬拡鍋滃埌鏌愪釜鐩爣涓婏紙target 鐢?`HoverAt` 閭ｅ缂栧彿锛涙病鏈夊氨浼?TipNoTarget锛夈€?/summary>
    private void SetTipTarget(int target)
    {
        if (target == TipNoTarget) { HideTip(); return; }
        if (target == _tipTarget) return;
        _tipTarget = target;
        _tipSinceMs = _host?.NowMs ?? 0;
        _tipShown = false;
        _tipFade.Jump(0f);
        Invalidate();   // "绛?500ms"浠庤繖涓€鍒昏捣绠楋紝瑕佷竴甯у抚璺熷埌鐐?    }

    /// <summary>鏀惰捣鎻愮ず锛堢Щ寮€銆佹寜涓嬨€佹嫋鍔ㄣ€佸叧寮€鍏抽兘鐢ㄥ畠锛夈€?/summary>
    private void HideTip()
    {
        bool hadLinger = _tipLingerUntil > 0;
        _tipLingerUntil = 0;
        if (_tipTarget == TipNoTarget && !_tipShown && _tipFade.Value <= 0f)
        {
            if (hadLinger) Invalidate();
            return;
        }
        _tipTarget = TipNoTarget;
        _tipShown = false;
        _tipFade.Jump(0f);
        Invalidate();
    }

    /// <summary>
    /// 姣忓抚鎺ㄨ繘鎻愮ず鐘舵€侊紙Render 寮€澶磋皟涓€娆★紝鍜?UpdatePeek / UpdateRail 鍚屼竴涓綅缃級銆?    /// 涓夋潯璺細鈶?瑙︽懜/绗旈暱鎸夊埌鐐?鈫?鐩存帴鐐逛寒锛涒憽 鍋滅暀鏈熷埌鐐?鈫?鏀讹紱
    /// 鈶?鎮仠 500ms 寤惰繜锛堝師鏉ョ殑閭ｆ潯锛夈€?    /// </summary>
    private void UpdateTip()
    {
        // 鈶?闀挎寜鍒扮偣锛氳繖涓€娆℃寜涓嬪氨绠?浣滃簾"浜嗭紙鏉炬墜涓嶆墽琛岋紝瑙?PointerUp锛?        if (_tipHoldStart > double.NegativeInfinity && !_tipHoldFired
            && _host.NowMs - _tipHoldStart >= Tokens.TipHoldMs)
        {
            _tipHoldFired = true;
            SetTipTarget(_tipHoldTarget);
            _tipShown = true;
            _tipFade.To(1f, Tokens.TipFadeMs);
            Invalidate();
        }

        // 鈶?鏉炬墜鍚庣殑鍋滅暀鍒版湡 鈫?鏀讹紙瑙︽懜闀挎寜涓撶敤锛涙偓鍋滄彁绀烘槸绉诲紑鍗虫敹锛?        if (_tipShown && _tipLingerUntil > 0 && _host.NowMs >= _tipLingerUntil)
        {
            HideTip();
            return;
        }

        if (!TipWanted)
        {
            if (_tipShown || _tipFade.Value > 0f)
            {
                _tipShown = false;
                _tipFade.Jump(0f);
            }
            return;
        }
        if (!_tipShown && _host.NowMs - _tipSinceMs >= Tokens.TipDelayMs)
        {
            _tipShown = true;
            _tipFade.To(1f, Tokens.TipFadeMs);
        }
    }

    /// <summary>鏌愪釜鍔ㄤ綔褰撳墠鐨勯敭浣嶆枃鏈紙鏌ュ紩鎿庣殑閿綅琛紱娌℃湁缁戝畾灏辩┖瀛楃涓诧級銆?/summary>
    private string Key(KeyAction a) => _host?.KeyText(a) ?? "";

    /// <summary>
    /// 鎻愮ず鏂囨锛?*鍚嶇О + 閿綅 + 涓€鍙ヨ鏄?*銆俙Title == null` = 杩欎釜涓滆タ涓嶅嚭鎻愮ず銆?    ///
    /// **鑼冨洿鏄敹绐勮繃鐨?*锛?026-10-02 绗簩杞紝瑙併€婅皟鐮?鎮仠鎻愮ず-Tooltip.md銆?0.2/10.8锛夛細
    /// 鍙湁"鍥炬爣-only / 甯﹀揩鎹烽敭 / 闅愯棌鎵嬪娍 / 璁や笉鍑烘潵"鐨勬墠閰嶏紱宸茬粡鍐欎笂瀛椼€佺偣涓€涓嬪綋鍦?    /// 瑙佺粨鏋滅殑涓€寰嬩笉閰嶁€斺€旇Е鎽搁暱鎸変細鍚炴帀閭ｄ竴娆＄偣鍑伙紝闆朵俊鎭殑鎻愮ず涓嶅€艰繖涓唬浠枫€傚垽鎹細
    /// **杩欐潯鎻愮ず鏈夋病鏈夊甫鏉ユ柊淇℃伅**銆?    ///
    /// 鍚嶅瓧鍙?鍞竴鏉ユ簮"锛氬浘褰㈠悕璇?`ToolNames.Of`锛堝紩鎿庡拰鐣岄潰鍏辩敤锛夛紝閿綅璇?    /// `IUiHost.KeyText`鈥斺€旂晫闈㈤噷涓嶅啓姝讳换浣曚竴涓敭銆?    /// </summary>
    private (string Title, string Key, string Note) TipContent(int id)
    {
        if (!_tipEnabled) return default;

        // ---- 銆屾洿澶氥€嶉潰鏉匡紙缂栧彿 = 1000 + 鍛戒腑鐮侊紱2026-10-02 绗簩鎵癸級----------------
        // 闈㈡澘鏄Е鎽镐富鍦猴紝鎵€浠ヨ繖浜涙彁绀?*榧犳爣鎮仠鍜屾墜鎸囬暱鎸夐兘鑳藉嚭**銆?        // 鍚姩鍣ㄦ牸瀛愬拰璁剧疆琛屽師鏉ラ偅琛屽皬鐏板瓧宸蹭粠鐢婚潰鎾ゆ帀锛岃繖閲屽氨鏄畠浠殑鍘诲銆?        if (id >= 1000)
        {
            int hit = id - 1000;
            if (hit >= MoreHitTile && hit <= MoreHitTile + HubTileTotal - 1)
            {
                var (label, hint, _, _) = HubTileInfo(hit - MoreHitTile);
                return hint.Length == 0 ? default : (label, "", hint);
            }
            if (hit >= MoreHitBottom && hit < MoreHitBottom + MoreBottomTiles.Length)
            {
                var t = MoreBottomTiles[hit - MoreHitBottom];
                return (t.Label, "", t.Hint);
            }
            if (hit >= MoreHitRow && hit < MoreHitRow + Rows.Length)
            {
                int i = hit - MoreHitRow;
                return (Rows[i].Label, "", Rows[i].Hint);
            }
            if (hit >= MoreHitChip && hit < MoreHitChip + Cells.Length)
            {
                int cell = hit - MoreHitChip;
                return (Cells[cell].Tip, "",
                        _pinned[cell] ? "宸查拤鍦ㄥ伐鍏锋潯涓婏紙鐐逛竴涓嬪彇娑堬級" : "鐐逛竴涓嬮拤鍒板伐鍏锋潯涓?);
            }
            if (hit >= MoreHitProfile && hit < MoreHitProfile + 3)
                return (ProfileName(hit - MoreHitProfile), "", "鍒囧埌杩欎竴妗?);
            if (hit == MoreHitBack) return ("杩斿洖", "", "鍥炲埌鍚姩鍣ㄤ富椤?);
            if (hit == MoreHitClose) return ("鍏抽棴", "", "鐐归潰鏉垮涔熻兘鍏?);
            return default;
        }

        if (id >= 0 && id < Cells.Length)
        {
            var c = Cells[id];
            return id switch
            {
                // 0 鍙锋敹璧锋牸銆佺悆锛?2锛夛細鑷В閲婏紝涓嶉厤锛堟敹绐勶級
                1 => (c.Tip, Key(KeyAction.TogglePassThrough), "鎵撳紑鍚庣偣鍑昏惤鍒板簳涓嬬殑绋嬪簭"),
                2 => (c.Tip, Key(KeyAction.ToggleBoard), $"{Key(KeyAction.FlipPageUp)} / {Key(KeyAction.FlipPageDown)} 缈诲睆"),
                3 => (c.Tip, Key(KeyAction.ToolPen), "宸茬粡鏄瑪 鈫?鍐嶆寜鎹㈤鑹?),
                4 => (c.Tip, Key(KeyAction.ToolHighlighter), ""),
                5 => (c.Tip, Key(KeyAction.ToolLaser), ""),
                6 => (c.Tip, Key(KeyAction.ToolEraser), "鍐嶆寜鍒囥€屾暣绗?/ 闈㈢Н銆?),
                7 => (c.Tip, Key(KeyAction.ToolMarquee), "鍐嶆寜鍒囥€岀煩褰?/ 濂楃储銆嶏紝鍙屽嚮 = 鍏ㄩ€?),
                8 => (c.Tip, "", "22 绉嶅浘褰㈤兘鍦ㄤ笂甯﹂噷鎸?),
                9 => (c.Tip, Key(KeyAction.ToolCapture), ""),
                10 => (c.Tip, Key(KeyAction.Undo), ""),
                11 => (c.Tip, Key(KeyAction.Redo), ""),
                12 => (c.Tip, "", "璇惧爞宸ュ叿銆佸ⅷ杩广€佽缃兘鍦ㄨ繖鍎?),
                _ => default,
            };
        }

        // 鑹茬墖锛?00+锛夛細棰滆壊涓€鐪煎氨鎳傦紝涓嶉厤锛堟敹绐勶紱鍚嶅瓧琛ㄨ繕鐣欑潃锛岀敾鐨勬椂鍊欒鐢級

        if (id >= 200 && id < 200 + BandSegmentCount) return SegmentTip(id - 200);

        if (id == 400) return CurAction switch
        {
            // 娓呴櫎鍏ㄩ儴澧ㄨ抗锛?026-10-14 缁熶竴鍛藉悕锛夛細鍜岀炕椤垫潯鑿滃崟閭ｄ竴椤规槸鍚屼竴涓姩浣溿€?            BandAction.Clear => ("娓呴櫎鍏ㄩ儴澧ㄨ抗", Key(KeyAction.Clear),
                                 "鎸変綇 0.8 绉掞紱鏁翠唤鏂囨。 / 婕旂ず鏂囩 / 鏁村紶鏉夸功鐨勫叏閮ㄦ壒娉紝鍙挙閿€"),
            // 鍏ㄩ€?/ 绮樿创鍥剧墖锛氭涓婂凡缁忓啓浜嗗瓧銆佺偣涓€涓嬪氨鐭ラ亾锛屼笉閰嶏紙鏀剁獎锛?            BandAction.CloseBoard => ("鍏抽棴鐧芥澘", Key(KeyAction.ToggleBoard), "鍙叧鐧芥澘锛屽ⅷ杩圭暀鐫€"),
            _ => default,
        };

        if (id == 500) return ("绾垮瀷", "", "瀹炵嚎 / 铏氱嚎 / 鐐圭嚎锛岀偣涓€涓嬫崲");
        return default;      // 婊戞潯锛?00锛変笉鍑虹浜屽紶鍗★紝瑙佷笂鏂?    }

    private (string Title, string Key, string Note) SegmentTip(int i)
    {
        switch (_bandCell)
        {
            case 2:
            {
                // 鐧芥澘锛氬彧鐣欑炕椤典袱涓紙鏈夐敭锛夛紱椤电爜璇绘暟銆佹澘鑹层€佸簳绾广€侀棿璺濋兘"鐐逛竴涓嬪氨鐭ラ亾"锛屼笉閰嶏紙鏀剁獎锛?                var seg = BoardSegAt(i);
                return seg.Kind switch
                {
                    BoardSegKind.PageUp => ("涓婁竴灞?, Key(KeyAction.FlipPageUp), ""),
                    BoardSegKind.PageDown => ("涓嬩竴灞?, Key(KeyAction.FlipPageDown), ""),
                    _ => default,
                };
            }
            // 姗＄毊 / 妗嗛€?/ 鎴浘鐨勫垎娈典笂宸茬粡鍐欑潃"鏁寸瑪鎿?/ 闈㈢Н鎿?/ 鐭╁舰 / 濂楃储 / 鎴浘鈥?锛?            // 涓嶉厤锛堟敹绐勶級锛涘彧鏈夊浘褰㈤偅 22 娈垫槸绾浘鏍囷紝鍏ㄩ厤銆?            case 8:
                if (i >= ShapeSegmentCount) return ("鍥惧簱", "", "鏀掍笅鐨勫浘褰紝鐐瑰紑鎸戜竴涓?);
                return (ToolNames.Of(ShapeToolAt(i)), "", "");
        }
        return default;
    }

    /// <summary>鏌愪釜鎻愮ず鐩爣璐寸潃鍝潡鐢伙紙鏈湴甯冨眬鍧愭爣锛涙病鏈夊氨绌虹煩褰級銆?/summary>
    private RectF TipAnchor(int id)
    {
        if (id == -2) return BallRect();
        if (id >= 0 && id < Cells.Length)
        {
            int pos = PosOf(id);
            return pos < 0 ? RectF.Empty : CellRect(pos);
        }
        if (id >= 100 && id < 100 + SwatchCount) return SwatchRect(id - 100);
        if (id >= 200 && id < 200 + BandSegmentCount) return SegmentRect(id - 200, BandSegmentCount);
        if (id == 400 && CurAction != BandAction.None) return ActionRect();
        if (id == 500) return DashToggleRect();
        return RectF.Empty;
    }

    /// <summary>
    /// 鎻愮ず鍗¤繖涓€鍒荤殑鐭╁舰锛堟湰鍦板竷灞€鍧愭爣锛涚┖ = 娌″緱鐢伙級銆?    /// 妯悜澶瑰湪灞忓箷閲岋紱绾靛悜涓?*韬插紑鏁村潡闈㈡澘**锛堝甫瀛愬湪涓绘潯涓婃柟鏃讹紝鍗＄敾鍦ㄥ甫瀛愪笂闈紝
    /// 涓嶅帇浣忚壊鐗囷級锛屼笂鏂规斁涓嶄笅灏辩炕鍒伴潰鏉夸笅鏂广€?    /// </summary>
    private RectF TipBox()
    {
        var (title, key, note) = TipContent(_tipTarget);
        if (title == null) return RectF.Empty;
        var anchor = TipAnchor(_tipTarget);
        if (anchor.IsEmpty) return RectF.Empty;

        float titleOnly = _widgets.Measure(title, Tokens.TipTitleSize);
        float titleW = titleOnly + (key.Length == 0 ? 0f
                                  : 12f + _widgets.Measure(key, Tokens.TipTitleSize));
        float noteW = note.Length == 0 ? 0f : _widgets.Measure(note, Tokens.TipNoteSize);
        float w = MathF.Max(titleW, noteW) + Tokens.TipPadX * 2f;
        float h = note.Length == 0
            ? Tokens.TipTitleSize + Tokens.TipPadY * 2f + 4f
            : Tokens.TipTitleSize + 2f + Tokens.TipNoteSize + Tokens.TipPadY * 2f + 4f;

        float cx = (anchor.MinX + anchor.MaxX) * 0.5f;
        float x0 = cx - w * 0.5f, x1 = cx + w * 0.5f;
        float lo = _screen.MinX + 4f, hi = _screen.MaxX - 4f;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }

        var panel = PanelRect();
        float gap = Tokens.TipGap + (1f - _tipFade.Value) * 3f;    // 娣″叆鏃朵笂娴?3px
        float top = panel.MinY - gap - h;
        if (top < _screen.MinY + 4f) top = panel.MaxY + gap;
        return new RectF { MinX = x0, MinY = top, MaxX = x1, MaxY = top + h };
    }

    /// <summary>鐢绘彁绀哄崱銆傛案杩滄渶鍚庣敾锛堝帇鍦ㄤ富鏉?甯﹀瓙/鏇村闈㈡澘涔嬩笂锛夈€?/summary>
    private void DrawTip(ID2D1DeviceContext ctx)
    {
        if (_tipFade.Value <= 0.001f) return;
        var (title, key, note) = TipContent(_tipTarget);
        if (title == null) return;
        var box = TipBox();
        if (box.IsEmpty) return;

        bool layered = BeginFade(ctx, _tipFade.Value);
        DrawCard(ctx, box, 8f);

        var line1 = new RectF
        {
            MinX = box.MinX + Tokens.TipPadX, MinY = box.MinY + Tokens.TipPadY,
            MaxX = box.MaxX - Tokens.TipPadX, MaxY = box.MinY + Tokens.TipPadY + Tokens.TipTitleSize + 2f,
        };
        _widgets.Text(ctx, title, line1, Tokens.TipTitleSize, Brush(ctx, InkCol), center: false);
        if (key.Length != 0)
        {
            float titleOnly = _widgets.Measure(title, Tokens.TipTitleSize);
            _widgets.Text(ctx, key,
                          new RectF { MinX = line1.MinX + titleOnly + 12f, MinY = line1.MinY,
                                      MaxX = line1.MaxX, MaxY = line1.MaxY },
                          Tokens.TipTitleSize, Brush(ctx, MutedCol), center: false);
        }
        if (note.Length != 0)
            _widgets.Text(ctx, note,
                          new RectF { MinX = line1.MinX, MinY = line1.MaxY,
                                      MaxX = line1.MaxX, MaxY = box.MaxY - Tokens.TipPadY },
                          Tokens.TipNoteSize, Brush(ctx, MutedCol), center: false);
        if (layered) EndFade(ctx);
    }

    // ---- 鑷閽╁瓙锛堝紑鍙戞湡鐢紱浜у搧浠ｇ爜涓嶇锛?-------------------------------

    /// <summary>
    /// 鑷鐢細鏌愪竴鏍硷紙鎸?*瀹屾暣妗ｇ殑涓嬫爣**锛夌殑閫昏緫鐭╁舰銆?    /// 娉ㄦ剰鍙傛暟鏄?鏍煎瓙鐨勭紪鍙?涓嶆槸"绗嚑涓?鈥斺€旀。浣嶄竴鎹紝鏄剧ず鐨勬牸瀛愭暟灏卞彉浜嗭紝
    /// 鎸夊簭鍙峰彇浼氳窇鍒扮晫澶栵紙鑷绗竴鐗堝氨鏄繖涔堢偣绌轰簡鏁存暣涓€鏉＄敤渚嬶級銆?    /// </summary>
    internal RectF CellRectForTest(int cell) => CellRect(PosOf(cell));

    /// <summary>鑷鐢細涓婂甫杩欎竴鍒荤殑鐭╁舰锛堟病闀垮嚭鏉ュ氨鏄┖锛涚┛閫忛噷鏄偅鏉?10 鍍忕礌鑹茬嚎锛屼笉涓虹┖锛夈€?/summary>
    internal RectF BandRectForTest => BandVisible() ? BandRect() : RectF.Empty;

    /// <summary>鑷鐢細璁剧疆鏉″紶寮€鍒颁粈涔堢▼搴︼紙0 = 骞虫椂閭ｆ潯鑹茬嚎锛? = 瀹屾暣璁剧疆鏉★級銆?    /// "绌块€忓彧鏀舵垚绾裤€佷笉璁稿紶寮€"杩欐潯鑷闈犲畠鈥斺€斿垽鐨勬槸鎶樺彔鍔ㄧ敾鐪熺殑璧板畬浜嗐€?/summary>
    internal float RailValueForTest => _rail.Value;

    /// <summary>鑷鐢細涓绘潯锛堜笉鍚笂甯︼級鐨勭煩褰€?/summary>
    internal RectF BarRectForTest => BarRect();

    /// <summary>鑷鐢細绗?i 涓壊鐗囩殑鐭╁舰銆?/summary>
    internal RectF SwatchRectForTest(int i) => SwatchRect(i);

    /// <summary>
    /// 鑷鐢細杩欎竴鏍肩殑璁剧疆鏉′笂**鏈夋病鏈?*閭ｄ釜铏氬疄绾垮垏鎹紙绗?+ 瀹屾暣妗ｆ墠鏈夛紝瑙?<see cref="BandHasDashToggle"/>锛夈€?    /// 鏋佺畝妗ｉ偅涓€鏉℃柇瑷€闈犲畠鈥斺€?鎸や笉涓嬫墍浠ヤ笉缁?杩欎欢浜嬪繀椤婚獙寰楀嚭鏉ワ紝涓嶇劧涓嬩竴杞姞瀹戒簡浼氶潤榛樺彉鏍枫€?    /// </summary>
    internal bool DashToggleVisibleForTest => BandHasDashToggle;

    /// <summary>鑷鐢細铏氬疄绾块偅涓€鏍肩殑鐭╁舰锛堢偣瀹冭鎸夌墿鐞嗗儚绱狅級銆?/summary>
    internal RectF DashToggleRectForTest => DashToggleRect();

    /// <summary>鑷鐢細鎹㈡尅鍔ㄧ敾鏄笉鏄繕鍦ㄨ窇锛堟崲鎸″畬浜嗚鍋滐紝绌洪棽甯ц鍥?0锛夈€?/summary>
    internal bool DashFadeRunningForTest => _dashFade.Running;

    /// <summary>鑷鐢細涓婂甫閲岀 i 涓垎娈电殑鐭╁舰銆?/summary>
    internal RectF SegmentRectForTest(int i) => SegmentRect(i, BandSegmentCount);

    /// <summary>
    /// 鑷鐢細涓婂甫鐜板湪鏈夊嚑娈点€?    /// 鑷瑕佹柇瑷€"鍥惧舰閭ｆ牸鏄竷娈?锛屼絾**娈垫暟鍙兘浠庣粯鍒?鍛戒腑鐢ㄧ殑杩欎竴浠界湡鍊奸噷璇?*锛?    /// <see cref="SegmentRectForTest"/> 涓嶆牎楠屼笅鏍囷紙瓒婄晫鐨勪笅鏍囩収鏍风畻寰楀嚭涓€涓煩褰級锛?    /// 鍏夐潬瀹冩暟涓嶅嚭涓婄晫銆?    /// </summary>
    internal int BandSegmentCountForTest => BandSegmentCount;

    /// <summary>
    /// 鑷鐢細鐧芥澘鑹插甫涓?鏌愪竴绉嶆"鎺掔鍑犱釜锛堟壘涓嶅埌 -1锛夈€傚悕瀛楋細缈婚〉閭ｄ袱涓槸 `PageUp` /
    /// `PageDown`銆侀〉鐮佹槸 `PageLabel`銆佸簳绾?`Pattern`銆侀棿璺?`Step`锛涙澘鑹插甫涓嬫爣锛宍Color0/1/2`
    /// 锛?/1/2 锛?鐧?缁?榛戯級銆?    ///
    /// 涓轰粈涔堣杩欎箞涓€涓叆鍙ｏ細**鑷涓嶈鍐欐娈典笅鏍?*銆傛搴忔槸浼氳閲嶆帓鐨勨€斺€?026-09-27 杩欎竴杞?    /// 灏辨妸 `[涓婁竴灞廬[鐧絔[缁縘[榛慮[涓嬩竴灞廬` 閲嶆帓鎴愪簡 `[鈥筣 绗?N 灞?[鈥篯 [鐧絔[缁縘[榛慮 [搴曠汗][闂磋窛]`锛?    /// 鑰屽啓姝讳笅鏍囩殑鑷**鏀归敊浜嗘槸闈欓粯鐨?*锛氱偣鍒颁簡鍒殑娈碉紝绾㈢殑鍗存槸閭ｄ竴鏉℃柇瑷€锛堣繖鏉℃暀璁?    /// 鍦ㄦ娊灞夐偅鍑犺涓婂悆杩囦竴娆★紝瑙?`RowRectByLabelForTest` 鐨勬敞閲婏級銆?    /// </summary>
    internal int BoardSegIndexOfForTest(string name)
    {
        if (_bandCell != 2) return -1;
        var segs = BoardBand;
        for (int i = 0; i < segs.Length; i++)
            if (BoardSegName(segs[i]) == name) return i;
        return -1;
    }

    /// <summary>鑷鐢細鐧芥澘鑹插甫杩欎竴鍒昏蛋鐨勬槸**绱у噾甯冨眬**锛堟瀬绠€妗ｉ偅绉嶏紝娌℃湁缈婚〉鍜屾粦鏉★級銆?/summary>
    internal bool BoardCompactBandForTest => BoardCompactBand;

    private static string BoardSegName((BoardSegKind Kind, int Idx) s) =>
        s.Kind == BoardSegKind.Color ? $"Color{s.Idx}" : s.Kind.ToString();

    /// <summary>
    /// 鑷鐢細鏌愪竴鏍肩幇鍦ㄧ敾鐨勬槸鍝釜鍥炬爣鍚嶃€?    /// 鍥惧舰閭ｄ竴鏍硷紙8锛夌殑鍥炬爣**璺熺潃褰撳墠绉嶇被鍙?*锛屾墍浠ュ畠寰楅棶涓€娆＄姸鎬侊紱
    /// 鍏朵綑鐨勬牸瀛愬浘鏍囨槸鍐欐鍦?Cells 琛ㄩ噷鐨勶紝鐩存帴缁欍€?    /// 鈿?鍥惧舰杩欎竴鏍肩殑鍒ゆ嵁鍜岀粯鍒跺繀椤?*鍚屼竴鏉?*锛?浜潃鎵嶇敾褰撳墠绉嶇被锛屾病浜敾閫氱敤鍥炬爣"锛夛細
    /// 鑷瑕佹槸鏃犳潯浠堕棶 ShapeIcon锛屽氨浼氬嚭鐜?鑷璇寸敾鐨勬槸涓夎褰€佸睆骞曚笂鍏跺疄鏄€氱敤鍥炬爣"
    /// 鈥斺€旇繖姝ｆ槸杩欐潯鑷瑕佺洴鐨勪笢瑗匡紙瑙?ShapeBandTest 鐨?D 娈碉級銆?    /// </summary>
    internal string CellIconForTest(int cell)
    {
        var st = _host.State;
        // 鐧芥澘閭ｄ竴鏍硷細鏉垮紑鐫€鏃剁敾鐨勬槸**鑷粯鐨勬澘 锛?杩欏潡鏉跨殑棰滆壊**锛屽悕瀛楀啓鎴?"board:鐧芥澘" 杩欐牱
        // 鈥斺€旂洏閲屾病鏈夎繖涓悕瀛楃殑鍥炬爣锛屽畠鍙槸缁欒嚜妫€涓€涓?鐜板湪鐢荤殑鏄摢涓€鍧楁澘"鐨勫垽鎹?        //锛堢湡鐢绘硶鏄?IconAtlas.DrawBoard锛岃 DrawCell锛夈€?        if (cell == 2)
            return st.Board ? "board:" + BoardColorName(st.BoardColor) : "board";
        // 婵€鍏夌瑪閭ｄ竴鏍硷細**鍒ゆ嵁鐩存帴闂?IconAtlas**锛坄LaserIconNameForTest`锛夆€斺€旀崲涓€鏀浘鏍囨椂
        // 鍙鏀归偅杈逛竴澶勶紝杩欓噷璺熺潃鍙橈紝涓嶄細鍑虹幇"鑷璇?Material銆佸睆骞曚笂鍏跺疄鏄嚜缁樼殑閭ｆ敮"銆?        if (cell == 5)
            return IconAtlas.LaserIconNameForTest(IsActive(5, st));
        // 鍏朵綑鏍煎瓙锛堝惈姗＄毊 / 閫夋嫨閭ｄ袱鏍肩殑"閫変腑鏄剧ず閫変腑浠€涔?锛夛細**鍜岀粯鍒堕棶鍚屼竴涓嚱鏁?*
        //锛坄CellIconName`锛夈€備袱澶勫悇鍐欎竴閬嶇殑璇濓紝鑷灏辨垚浜?娴嬪彟涓€涓笢瑗?锛岃涓婇潰閭ｆ潯 鈿犮€?        return CellIconName(cell, st);
    }

    /// <summary>鑷鐢細鐜板湪杩欏潡鏉挎槸鐧界殑 / 缁跨殑 / 榛戠殑锛堟寜鏉胯壊鍜屼笁涓璁炬瘮锛岃 InkPalette.BoardPresets锛夈€?/summary>
    private static string BoardColorName(in Color4 c)
    {
        foreach (var (name, preset) in InkPalette.BoardPresets)
            if (MathF.Abs(preset.R - c.R) < 0.02f && MathF.Abs(preset.G - c.G) < 0.02f
                && MathF.Abs(preset.B - c.B) < 0.02f) return name;
        return "鑷畾涔夎壊";
    }

    /// <summary>
    /// 鑷鐢細鍥惧舰閭ｄ竴鏍?*绗?i 娈?*鐨勫浘鏍囧悕銆?    /// 鐢ㄦ潵楠?鎶涚墿绾块偅涓€娈电殑鍥炬爣璺熺潃褰撳墠寮€鍙ｆ柟鍚戣浆"锛堣 <see cref="ParabolaIconName"/>锛夆€斺€?    /// 鍥炬爣涓嶈浆鐨勮瘽锛岃€佸笀鐪嬩笉鍑?鍐嶇偣涓€娆?鍒板簳鏈夋病鏈夌敓鏁堛€?    /// </summary>
    internal string ShapeIconNameForTest(int i) => ShapeIcon(i);

    /// <summary>
    /// 鑷鐢細鍥惧舰閭ｄ竴鏍?*绗?i 娈?*鍙宠竟鏈夊嚑涓。浣嶇偣銆佺鍑犱釜鏄綋鍓嶆。锛堣 <see cref="PipsOf"/>锛夈€?    /// 鎷垮畠楠?鍝嚑鏍艰鏈夋。浣嶇偣銆佸悇鏈夊嚑妗?鈥斺€旂敤鎴?2026-09-20 闂?鎶涚墿绾块偅鏍兼€庝箞娌℃湁灏忓渾鐐?
    /// 灏辨槸杩欑被婕忎簡鎵嶅彂鐜扮殑锛屾墍浠ヨ繖鏉¤鏈夋柇瑷€鍗′綇銆?    /// </summary>
    internal (int Count, int Current) ShapePipsForTest(int i)
        => PipsOf(ShapeToolAt(i), _host.State);

    /// <summary>鑷鐢細杩欎釜宸ュ叿鍦ㄥ浘褰㈤潰鏉块噷鏈夋病鏈夊叆鍙ｏ紙瑙?<see cref="HasShapeEntry"/>锛夈€?/summary>
    internal static bool HasShapeEntryForTest(Tool t) => HasShapeEntry(t);

    /// <summary>鑷鐢細鍥惧舰閭ｄ竴鏍兼帓浜?*鍑犺**锛堣嚜妫€瑕侀潬瀹冩妸鍚勬鍒嗗洖琛岄噷锛屽幓楠?琛屼笉閲嶅彔"锛夈€?/summary>
    internal static int ShapeRowCountForTest => ShapeBandRows;

    /// <summary>鑷鐢細绗?`row` 琛?*鍑犳**锛堣嚜妫€鎸?琛?鍒嗙粍鏃惰鐭ラ亾姣忚鐨勬鏁帮級銆?/summary>
    internal static int ShapeRowLengthForTest(int row) => ShapeRowCount(row);

    /// <summary>
    /// 鑷鐢細杩欎釜宸ュ叿鍦ㄥ浘褰㈤偅涓€鏍奸噷鏄?*绗嚑娈?*锛堜袱琛屾媺骞崇紪鍙凤紱娌″叆鍙ｅ氨 鈭?锛夈€?    ///
    /// **涓轰粈涔堣鏈夊畠**锛氳嚜妫€閲屽啓姝绘鍙蜂細闅?鍔犱竴绉嶅浘褰?/ 鎾や竴绉嶅浘褰?**闈欓粯澶辨晥**
    /// 鈥斺€旇繖娆″姞妫遍敟 / 妫卞彴鏃剁浜岃寰€涓嬫尓浜嗕袱鏍硷紝鍐欐鐨?16 灏变笉澹颁笉鍝嶇偣浜嗗埆鐨勬銆?    /// 鎸夊伐鍏峰悕闂竴鍙ュ氨璺熺潃琛ㄨ蛋锛屾尓澶氬皯娆￠兘涓嶆€曪紙浠撳簱鏁欒閲岄偅涓€鏉★級銆?    /// </summary>
    internal static int ShapeSegmentIndexForTest(Tool t)
    {
        int seen = 0;
        foreach (var row in ShapeRows)
        {
            int j = Array.IndexOf(row, t);
            if (j >= 0) return seen + j;
            seen += row.Length;
        }
        return -1;
    }

    /// <summary>鑷鐢細婊戞潯鐨勭煩褰€?/summary>
    internal RectF SliderRectForTest => SliderRect();

    /// <summary>
    /// 鑷鐢細婊戞潯杞ㄩ亾鐨勫乏鍙崇锛堥€昏緫鍧愭爣锛夈€傝嚜妫€瑕佹嫋鍒?*涓ょ**鍘婚獙
    /// "杩欎釜宸ュ叿鐨勭矖缁嗙湡鐨勮蛋鍒颁簡鑼冨洿鐨勭鐐?锛屾墍浠ュ繀椤绘嬁鍒板拰缁樺埗鍚屼竴浠界殑涓や釜绔偣銆?    /// </summary>
    internal (float Left, float Right) SliderTrackRangeForTest => SliderTrackRange();

    /// <summary>鑷鐢細鏌愬伐鍏风殑绮楃粏妗ｄ綅琛紙鍜屽紩鎿庨偅寮犺〃閫愰」姣斿锛岃 --paneltest 鈶?1锛夈€?/summary>
    internal float[] WidthGradesForTest(Tool tool) => WidthGrades(tool);

    /// <summary>鑷鐢細鏌愪釜妗ｄ綅鍊煎湪婊戞潯涓婄殑 x锛堥€昏緫鍧愭爣锛夆€斺€旂収鐫€瀹冪偣 = 鐐逛腑閭ｄ釜妗ｄ綅鐐广€?/summary>
    internal float WidthGradeXForTest(float grade)
    {
        var (min, max) = WidthRange(_host.State.Tool);
        var (left, right) = SliderTrackRange();
        return left + (right - left) * ((grade - min) / (max - min));
    }

    /// <summary>鑷鐢細銆屾洿澶氥€嶉潰鏉?/ 鍚姩鍣ㄦ牸瀛?/ 搴曟爮 / 璁剧疆琛?/ 妗ｄ綅 / 閽変綇瀹牸鐨勭煩褰€?/summary>
    internal bool MoreOpenForTest => _moreOpen;
    internal RectF MoreRectForTest => MoreRect();
    internal int MorePageForTest => (int)_morePage;
    internal RectF MoreCloseRectForTest => MoreCloseRect();
    internal RectF MoreBackRectForTest => MoreBackRect();
    internal RectF MoreRowRectForTest(int i) => MoreRowRect(i);
    internal RectF MoreProfileRectForTest(int i) => MoreProfileRect(i);
    internal RectF MoreChipRectForTest(int cell) => MoreChipRect(cell);
    /// <summary>鑷鐢細鍚姩鍣ㄦ牸瀛愮殑鐭╁舰锛堟寜**鍏ㄥ眬缂栧彿**锛屽悇鍒嗙粍鑷姩灞曞紑锛涜秺鐣岃繑鍥炵┖锛夈€?/summary>
    internal RectF HubTileRectForTest(int code)
    {
        int seen = 0;
        for (int s = 0; s < HubCounts.Length; s++)
        {
            if (code < seen + HubCounts[s]) return HubTileRect(s, code - seen);
            seen += HubCounts[s];
        }
        return RectF.Empty;
    }
    internal RectF HubBottomRectForTest(int i) => HubBottomRect(i);
    /// <summary>鑷鐢細鏌愪釜鍧愭爣杩欎竴鍒荤殑鍛戒腑鐮侊紙闈㈡澘閲?鐐瑰緱涓悧"鐨勬帰閽堬級銆?/summary>
    internal int MoreHitForTest(float x, float y) => MoreHitAt(x, y);

    /// <summary>鑷鐢細琛岃〃閲屾湁鍑犺锛堜笉鍐欐鏁板瓧鈥斺€旀彃涓€琛?鍒犱竴琛岃嚜妫€瑕佽嚜宸辫窡涓婏級銆?/summary>
    internal int MoreRowCountForTest => Rows.Length;

    /// <summary>鑷/鍑哄浘鐢細鎵撳紑銆屾洿澶氥€嶉潰鏉匡紙浜у搧閲屽彧鑳界偣涓绘潯銆屸€︺€嶉偅涓€鏍硷級銆?/summary>
    internal void OpenMoreForTest() { OpenMore(); _more.Jump(1f); _moreH.Jump(MoreTargetH()); }

    /// <summary>鑷鐢細鍏虫帀銆屾洿澶氥€嶉潰鏉匡紙浜у搧閲屾槸鐐归潰鏉垮 / 鐐?鉁曪級銆?/summary>
    internal void CloseMoreForTest() { CloseMore(); _more.Jump(0f); }

    /// <summary>鑷/鍑哄浘鐢細鍒囧埌鍚姩鍣紙0锛? 璁剧疆瀛愰〉锛?锛夈€?/summary>
    internal void SetMorePageForTest(int i)
    {
        _morePage = (MorePage)Math.Clamp(i, 0, 1);
        _moreHover = MoreHitNone;
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>鑷/鍑哄浘鐢細鐩存帴鍒囨繁鑹蹭富棰橈紙**涓嶈惤鐩?*锛屽彧鏀硅繖涓€鍒荤殑鏄剧ず锛夈€?/summary>
    internal void SetDarkForTest(bool on)
    {
        _dark = on;
        PushFloatingTheme();     // 娴眰锛堟搷浣滄潯/灏忛潰鏉匡級涔熷緱璺熺潃鎹⑩€斺€斿拰鐐归偅琛屽紑鍏冲悓涓€鏉¤矾
        Invalidate();
    }

    /// <summary>
    /// 鑷鐢細**鎸夎鏍囩**鎵鹃偅涓€琛岀殑鐭╁舰锛堟壘涓嶅埌杩斿洖绌虹煩褰級銆?    ///
    /// 琛岀幇鍦ㄩ暱鍦ㄣ€屾洿澶氥€嶉潰鏉块噷锛?026-10-01 璧凤級锛涜繖涓挬瀛愯窡鐫€璧帮紝鏍囩琛ㄧ殑"鍞竴涓€浠?涓嶅彉銆?    ///
    /// 涓轰粈涔堜笉璁╄嚜妫€鍐欒涓嬫爣锛氳鏄細琚彃鏉ユ彃鍘荤殑鈥斺€?026-09-17 鎻掍簡搴曠汗涓よ銆?    /// 2026-09-19 鎶婄伆鐫€鐨勩€屽绉戝伐鍏枫€嶆崲鎴愪簡鍧愭爣绯?/ 鏁拌酱 / 鍧愭爣绯荤綉鏍间笁琛岋紝
    /// 2026-10-01 鍙堟暣涓惉杩涗簡涓ぎ闈㈡澘銆傛瘡鎻掍竴娆★紝鍐欐涓嬫爣鐨勮嚜妫€灏辫鍘绘敼涓€澶勫紩鐢紝
    /// 鑰屼笖**鏀归敊浜嗘槸闈欓粯鐨?*锛氱偣鍒颁簡鍒殑琛岋紝绾㈣壊鐨勫嵈鏄偅涓€鏉℃柇瑷€锛?鐐归噸鍚病鍙嶅簲"锛夈€?    /// 鎸夋爣绛炬壘锛屼互鍚庢彃琛屽氨涓嶄細鍐嶇鍒拌嚜妫€銆?    /// </summary>
    internal RectF RowRectByLabelForTest(string labelPart)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label.Contains(labelPart, StringComparison.Ordinal)) return MoreRowRect(i);
        return RectF.Empty;
    }

    /// <summary>鑷鐢細绗?i 琛岀殑鐭╁舰锛堢幇鍦ㄦ寚銆屾洿澶氥€嶉潰鏉块噷鐨勮鈥斺€斾骇鍝侀噷琛屽彧鍦ㄨ繖鍎匡級銆?/summary>
    internal RectF RowRectForTest(int i) => MoreRowRect(i);

    /// <summary>鑷鐢細娣辫壊涓婚涓庤创杈归殣钘忕殑寮€鍏崇姸鎬併€?/summary>
    internal bool DarkForTest => _dark;
    internal bool HideEnabledForTest => _hideEnabled;

    /// <summary>鑷/鍑哄浘鐢細鐩存帴鍒囧埌鏌愪竴妗ｏ紙浜у搧閲屽湪銆屾洿澶?鈫?璁剧疆 鈫?宸ュ叿鏉°€嶉噷鐐癸級銆?/summary>
    internal void SetProfileForTest(int i) => SetProfile((Profile)i);

    /// <summary>鑷/鍑哄浘鐢細鎶婅壊绾垮紶寮€鎴愯缃潯锛堜骇鍝侀噷鏄紶鏍囩鍒板畠锛夈€?/summary>
    internal void OpenRailForTest() { _railHover = true; _rail.Jump(1f); }

    /// <summary>鍑哄浘鐢細鎶婁笂甯︽幇鍒版煇涓€鏍硷紙绛変环浜庣偣瀹冧竴涓嬶紝浣嗕笉鎵ц閭ｄ竴鏍肩殑鍔ㄤ綔锛夈€?/summary>
    internal void SelectBandCellForTest(int cell)
    {
        _bandCell = cell;
        _railHover = true;      // 鍑哄浘鏃跺亣瑁?鐒︾偣灏卞湪闈㈡澘涓?
        _rail.Jump(1f);
    }

    /// <summary>鑷鐢細涓婂甫杩欎竴鍒诲仠鍦ㄥ摢涓€鏍硷紙"鐜板湪鏄皝鐨勮缃潯"锛夈€?/summary>
    internal int BandCellForTest => _bandCell;

    /// <summary>鍑哄浘鐢細鎶?绮楃粏棰勮"鎽嗗嚭鏉ワ紙浜у搧閲屾槸鎷栨粦鏉°€佹垨鎸囬拡鍋滃湪婊戞潯涓婃椂鍑虹幇锛夈€?/summary>
    internal void ShowSizePreviewForTest()
    {
        _hover = 300;
        _sliderDragging = true;
        _railHover = true;
        _rail.Jump(1f);
    }

    /// <summary>鑷鐢細涓婂甫鍙崇閭ｄ釜鍔ㄤ綔鎸夐挳锛堟竻绌?鍏ㄩ€夛級鐨勭煩褰?+ 鎸変綇鐘舵€併€?/summary>
    internal RectF ActionRectForTest => CurAction == BandAction.None ? RectF.Empty : ActionRect();
    internal bool ActionHoldingForTest => ActionHolding;
    internal float HoldProgressForTest => HoldProgress();

    /// <summary>鑷鐢細杩欎竴鏍肩幇鍦ㄥ帇鏆楁病鏈夛紙鎾ら攢/閲嶅仛鏍堢┖锛夈€?/summary>
    internal bool CellUnavailableForTest(int cell) => CellUnavailable(cell, _host.State);

    /// <summary>鑷鐢細杩欎竴鍒昏壊鐗囨湁鍑犱釜锛堟瀬绠€妗ｅ簲璇ユ槸 4锛夈€?/summary>
    internal int SwatchCountForTest => SwatchCount;

    /// <summary>鑷鐢細"鍏佽鑷姩鏀惰捣"鐨勫紑鍏筹紙鍚姩鏃跺簲璇ユ槸 false锛夈€?/summary>
    internal bool PeekArmedForTest => _peekArmed;

    // ---- 鑷鐢細鎮仠鎻愮ず ----------------------------------------------------

    /// <summary>鎻愮ず寮€鍏筹紙銆屾洿澶?鈫?璁剧疆 鈫?澶栬銆嶉偅涓€琛岋紱榛樿寮€锛夈€?/summary>
    internal bool TipEnabledForTest => _tipEnabled;
    /// <summary>鎻愮ず杩欎竴鍒荤湡鐨勭敾鍑烘潵浜嗘病鏈夛紙杩囦簡 500ms 寤惰繜 + 娣″叆涓湁鍊硷級銆?/summary>
    internal bool TipVisibleForTest => _tipShown && _tipFade.Value > 0.01f;
    /// <summary>浠绘剰鐩爣鐨勬彁绀烘枃妗堬紙鍚嶇О / 閿綅 / 璇存槑锛汿itle 涓?null = 涓嶅嚭鎻愮ず锛夈€?/summary>
    internal (string Title, string Key, string Note) TipContentForTest(int id) => TipContent(id);
    /// <summary>鑷鐢細瑙︽懜/绗旈暱鎸夊凡缁忓脊杩囨彁绀烘病鏈夛紙寮硅繃 = 杩欎竴娆℃澗鎵嬩笉鎵ц锛夈€?/summary>
    internal bool TipHoldFiredForTest => _tipHoldFired;
    /// <summary>鑷鐢細瑙︽懜/绗旈暱鎸夋鍦ㄨ鏃讹紙杩樻病鍒扮偣锛夈€?/summary>
    internal bool TipHoldRunningForTest => _tipHoldStart > double.NegativeInfinity && !_tipHoldFired;
    /// <summary>鎻愮ず鍗＄殑鐭╁舰锛堝睆骞曞潗鏍囷紱娌℃樉绀哄氨鏄┖鐭╁舰锛夈€?/summary>
    internal RectF TipRectForTest
    {
        get
        {
            if (!TipVisibleForTest) return RectF.Empty;
            var r = TipBox();
            if (r.IsEmpty) return RectF.Empty;
            var s = Shift();
            return new RectF
            {
                MinX = r.MinX + s.X, MinY = r.MinY + s.Y,
                MaxX = r.MaxX + s.X, MaxY = r.MaxY + s.Y,
            };
        }
    }
    /// <summary>鑷鐢細鐩存帴寮€鍏虫彁绀猴紙涓嶈惤鐩橈紱鐢熶骇璺緞鏄偣璁剧疆閲岄偅涓€琛岋級銆?/summary>
    internal void SetTipEnabledForTest(bool on)
    {
        _tipEnabled = on;
        if (!on) HideTip();
    }
    /// <summary>鑷/鍑哄浘鐢細鐩存帴鎶婃煇涓洰鏍囩殑鎻愮ず鎽嗗嚭鏉ワ紙浜у搧閲岄潬鐪熸偓鍋滅瓑 500ms锛屽嚭鍥剧瓑涓嶈捣锛夈€?/summary>
    internal void ShowTipForTest(int id)
    {
        SetTipTarget(id);
        _tipShown = true;
        _tipFade.Jump(1f);
        Invalidate();
    }

    /// <summary>鑷鐢細绮楃粏棰勮杩欎竴鍒荤殑鐭╁舰锛堝睆骞曞潗鏍囷紱娌℃樉绀哄氨鏄┖鐭╁舰锛夈€?/summary>
    internal RectF SizePreviewRectForTest
    {
        get
        {
            if (!SizePreviewVisible || _host == null) return RectF.Empty;
            var r = SizePreviewRect(_host.State);
            var s = Shift();
            return new RectF
            {
                MinX = r.MinX + s.X, MinY = r.MinY + s.Y,
                MaxX = r.MaxX + s.X, MaxY = r.MaxY + s.Y,
            };
        }
    }

    /// <summary>鑷鐢細杩欎竴妗ｆ樉绀哄嚑鏍?/ 鐜板湪鏄鍑犳。 / 鏌愪竴鏍奸拤鐫€娌℃湁銆?/summary>
    internal int VisibleCountForTest => VisibleCells().Length;
    internal int ProfileForTest => (int)_profile;
    internal bool PinnedForTest(int cell) => _pinned[cell];

    /// <summary>鑷鐢細妗ｄ綅鏉＄ i 娈点€侀拤浣忔爮绗?cell 鏍肩殑鐭╁舰锛堜笌浜у搧鍚屼竴澶勶細鏇村闈㈡澘閲岋級銆?/summary>
    internal RectF ProfileRectForTest(int i) => MoreProfileRect(i);
    internal RectF ChipRectForTest(int cell) => MoreChipRect(cell);

    /// <summary>鑷鐢細鐜板湪绠?灞曞紑"鍚椼€?/summary>
    internal bool ExpandedForTest => Expanded;

    /// <summary>鑷鐢細鐣岄潰鐪嬪埌鐨勫睆骞曪紙鏍稿瀹冨拰 IUiHost.Screen 鏄笉鏄悓涓€涓級銆?/summary>
    internal RectF ScreenForTest => _screen;

    /// <summary>鑷鐢細鐣岄潰鐪嬪埌鐨?*宸ヤ綔鍖?*锛堥粯璁や綅缃寜瀹冪畻锛岃 <see cref="RawAnchor"/>锛夈€?/summary>
    internal RectF WorkAreaForTest => _work;

    /// <summary>
    /// 鑷鐢細鐩存帴鎸囧畾"鎷栬繃鐨勪綅缃?锛坄null` = 鍥為粯璁や綅缃級锛屼笉缁忚繃鐪熼紶鏍囨嫋鍔ㄣ€?    /// 鐢ㄦ潵楠?杩涙斁鏄犳椂褰掍綅"鈥斺€旈偅鏉¤鐪嬬殑鏄?*褰掍綅鍓?*宸茬粡鎷栧埌鍒杩欎釜鍓嶆彁锛?    /// 鑰屼笉鏄嫋鍔ㄦ墜鍔挎湰韬紙鎵嬪娍鍙︽湁涓撴祴锛夈€?    /// </summary>
    internal void SetAnchorForTest(Vector2? anchor)
    {
        _anchor = anchor;
        Invalidate();
    }

    /// <summary>鑷鐢細璐磋竟闅愯棌鐨勮繘搴︼紙1 = 瀹屽叏鏄剧ず锛? = 鍙墿闇插ご锛夈€?/summary>
    internal float PeekForTest => _peek.Value;

    /// <summary>
    /// 鑷/鍑哄浘鐢細鎶?鐞?鈫?甯﹀瓙"鐨勫睍寮€杩涘害涓€鎶婃寜鍒版煇涓€硷紙0 = 鐞冿紝1 = 瀹屽叏灞曞紑锛夈€?    /// 涓棿鎬佸钩鏃跺彧鐪嬪緱鍒?200 姣锛屾兂鏍稿杩欎竴娈甸暱浠€涔堟牱灏卞彧鑳借繖涔堥拤浣忓畠銆?    /// </summary>
    internal void SetExpandForTest(float v) => _expand.Jump(v);

    /// <summary>
    /// 鑷/鍑哄浘鐢細鎶婅创杈归殣钘忎竴鎶婃寜鍒版煇涓繘搴︼紙椤哄甫鍏佽"绂诲紑灏辨敹"锛夈€?    /// 骞虫椂杩欐潯璺"鎸囬拡绂诲紑杩?700 姣"锛屽嚭鍥炬椂绛変笉璧枫€?    /// </summary>
    internal void ForcePeekForTest(float v)
    {
        _peekArmed = true;
        _peek.Jump(v);
    }

    /// <summary>鑷鐢細鑹茬嚎寮犲紑娌℃湁锛坒alse = 骞虫椂閭ｆ潯 6 鍍忕礌鐨勭嚎锛夈€?/summary>
    internal bool RailOpenForTest => RailOpen;

    /// <summary>
    /// 鑷鐢細鐒︾偣杩樺湪涓嶅湪闈㈡澘涓婏紙璁剧疆鏉¤涓嶈寮€鐫€鐪嬪畠锛夈€?    /// 鏃х殑 `RailPinnedForTest` 闅?鐐逛竴娆￠拤浣?涓€璧峰垹鎺変簡鈥斺€旈偅濂楃姸鎬佸凡缁忎笉瀛樺湪銆?    /// </summary>
    internal bool RailHoverForTest => _railHover;

    /// <summary>鑷鐢細鏌愪竴鏍艰繖涓€鍒荤畻涓嶇畻"閫変腑鐨?锛堢┛閫忎笌宸ュ叿浜掓枼閭ｆ潯闈犲畠鐪嬶級銆?/summary>
    internal bool CellActiveForTest(int cell) => IsActive(cell, _host.State);

    /// <summary>
    /// 鑷鐢細**杩欎竴鍒绘。浣嶄笅銆佸畬鍏ㄥ睍寮€鍚庣殑甯﹀瓙瀹?*銆?    /// 榛樿浣嶇疆锛?灞曞紑鍚庨偅鏉″甫瀛愬眳涓?锛夋寜瀹冪畻锛岃嚜妫€瑕佹寜鍚屼竴涓暟鍙嶆帹鐞冭鍦ㄥ摢鍎裤€?    /// </summary>
    internal float ExpandedWidthForTest => ExpandedWidth();

    /// <summary>鑷鐢細涓婂甫杩欎竴鍒诲楂橈紙6 = 鑹茬嚎锛?4 = 瀹屾暣璁剧疆鏉★級銆?/summary>
    internal float BandHeightForTest => BandRect().MaxY - BandRect().MinY;
}
