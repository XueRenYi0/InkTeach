using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>妗嗛€夊伐鍏蜂笅鎷栫┖鐧藉鐨勯€夋嫨鏂瑰紡锛坄Ctrl+Alt+9` 鍒囷級銆傝 InkEngine.SelMode銆?/summary>
/// <summary>
/// 妗嗛€夊伐鍏蜂笅鐨勪袱绉嶉€夋嫨鏂瑰紡銆?b>public</b>锛氬畠鏄晫闈㈠绾︾殑涓€閮ㄥ垎
/// 锛堢晫闈㈣鑳?鍒囨垚濂楃储"锛岃 <see cref="IEngineCommands.SetSelectMode"/>锛夈€?/// </summary>
public enum SelectMode
{
    /// <summary>鐭╁舰妗嗛€夛細纰板埌澧ㄥ氨绠楅€変腑锛圤neNote 鐨勮涔夛級銆?/summary>
    Rect = 0,
    /// <summary>鑷敱濂楃储锛氫唬琛ㄧ偣 80% 钀藉湪鍦堥噷鎵嶇畻閫変腑锛圵PF 鐨勮涔夛級銆?/summary>
    Lasso = 1,
}

internal enum PassThroughMode
{
    /// <summary>WM_NCHITTEST -> HTTRANSPARENT only (safe, but the documented
    /// hand-off only reaches windows owned by the same thread).</summary>
    HitTest = 0,

    /// <summary>Adds WS_EX_TRANSPARENT to the window style on top of HitTest.</summary>
    ExTransparent = 1,

    /// <summary>Adds WS_EX_LAYERED | WS_EX_TRANSPARENT (the classic click-through
    /// recipe). Kept as a probe because it may disturb DirectComposition.</summary>
    LayeredTransparent = 2,
}

/// <summary>
/// 寮曟搸鏈綋銆傞儴鍒嗗疄鐜版寜鑱岃矗鎷嗗湪鍒殑鏂囦欢閲岋紙`Ppt.cs` = PPT 鏀炬槧鑱斿姩锛夈€?/// </summary>
public partial class InkEngine
{
    // ---- document / interaction state ------------------------------------
    internal readonly InkDocument Doc = new();
    internal readonly LaserTrail Laser = new();

    /// <summary>
    /// 鏂囨。椤靛簳灞傦紙鍥剧墖 / PDF 瀵煎叆鍚?涓€鍙犻〉"鐨勯偅涓€灞傦級銆?*涓嶆槸绗旇抗**锛?    /// 涓嶅弬涓庡瓨妗ｃ€佸鍑恒€佸洖鏀俱€佹挙閿€锛堣 <see cref="DocPages"/> 鐨勮鏄庯級銆?    /// 娌℃墦寮€鏂囨。鏃跺畠鏄┖鐨勨€斺€斾笉璋冪敤 = 闆跺紑閿€銆?    /// </summary>
    internal readonly DocPages DocView = new();

    /// <summary>涓诲惊鐜嚎绋?id锛堝悗鍙扮嚎绋嬪彨閱掔敤锛?see cref="Loop"/> 绗竴鍙ュ～锛夈€?/summary>
    private volatile uint _mainThreadId;

    /// <summary>鍚庡彴娓插畬涓€椤电殑"鍙啋"鏍囪锛堟覆鏌撶嚎绋嬪啓銆佷富寰幆璇伙級銆?/summary>
    private volatile bool _docPageReady;

    /// <summary>
    /// 鍚庡彴绾跨▼锛堟枃妗ｉ〉娓叉煋锛夋覆瀹屼竴椤?鈫?缃爣璁?+ 涓€鏉＄嚎绋嬫秷鎭紝鎶婁富寰幆浠?`WaitMessage()`
    /// 閲岃涪閱掋€?*蹇呴』鍙啋**锛氱┖闂叉椂寮曟搸涓嶇粰甯э紝涓嶅彨灏辨槸"椤垫覆濂戒簡銆佸睆骞曚笂杩樼┖鐫€"銆?    /// 锛堝拰 PPT 杞绾跨▼鍞ら啋涓诲惊鐜悓涓€濂楀仛娉曪紝瑙?PptWatcher銆傦級
    /// </summary>
    private void WakeForDocPage()
    {
        _docPageReady = true;
        try
        {
            if (_mainThreadId != 0)
                Native.PostThreadMessage(_mainThreadId, PptWatcher.WakeMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>涓诲惊鐜瘡鎷嶏細鍚庡彴鏉ョ殑椤靛埌浜嗗氨缃剰锛屼笅涓€鎷嶈蛋娓叉煋锛堝湪 SyncDocPages 閲岄噰绾筹級銆?/summary>
    private void StepDocPageReady()
    {
        if (!_docPageReady) return;
        _docPageReady = false;
        _dirty = true;
    }

    /// <summary>
    /// 鐣岄潰鑷繁鐨勫亸濂斤紙娣辫壊涓婚銆佽创杈归殣钘忋€佹。浣嶃€侀拤浣忥級銆傚紩鎿?*鍙瓨涓嶈В閲?*锛?    /// 瀹冧笉鐭ラ亾"鏋佺畝妗?鏄粈涔堬紝鐣岄潰璇村瓨浠€涔堝氨瀛樹粈涔堛€傝惤鐩樺湪 settings.json 鐨?`ui` 娈点€?    /// </summary>
    internal readonly Dictionary<string, string> UiPrefs = new();
    private bool _uiPrefsDirty;

    /// <summary>鑷/鍩哄噯妯″紡锛?*涓嶈涔熶笉鍐?*鐢ㄦ埛閰嶇疆锛堝垽鎹纭畾锛屾洿涓嶈兘鏀圭敤鎴风殑璁剧疆锛夈€?/summary>
    /// <summary>
    /// 杩欎釜鍙傛暟**鏄紑鍏炽€佷笉鏄嚜妫€ mode** 鍚楋紵鏄殑璇濅笉璇ユ妸绋嬪簭甯﹁繘鑷妯″紡銆?    ///
    /// 鍙垪"鐢ㄦ埛鍙兘浼氬崟鐙暡銆佽€屼笖纭疄涓嶆槸 mode"鐨勯偅浜涖€?    /// 鐪熸暡閿?mode 鍚嶏紙姣斿 `--inputtset`锛変粛鎸夎€佽鐭╄繘鑷妯″紡 鈥斺€?閭ｆ鏄嚜妫€妯″紡瀛樺湪鐨勬剰涔夈€?    /// </summary>
    private static bool IsSwitchNotMode(string a) => a switch
    {
        "--norawinput" or "--wintab" or "--nowintab" or "--nopressure" or "--rawprobe" or "--notrend"
            or "--himetric" or "--nohimetric" or "--notouch" or "--syswet" or "--ownwet" or "--strokefile"
            or "--inktrail" or "--noinktrail" or "--trailonly" or "--trailcommit"
            or "--hud" or "--nohud" or "--fullpresent"
            or "--recink" or "--recinkp" or "--reclive"
            or "--doc"
            or "--nosmooth" or "--printersafe"
            or "--mean2gauss" or "--mean2guniform" or "--mean2gsigma" or "--mean2gfast"
            or "--mean2glo" or "--mean2ghi" or "--mean2gtip"
            or "--predict2" or "--nopredict2" or "--nofastwet" => true,
        _ => false,
    };

    internal bool SelfCheckMode;

    /// <summary>鍚姩鏃舵嬁鍒扮殑閭ｄ釜鍙傛暟锛坄args[0]`锛夈€傚彧缁?鑷妯″紡"閭ｈ璇婃柇鐢ㄣ€?/summary>
    private string _modeName = "";

    /// <summary>閭ｈ璇婃柇鍙墦涓€娆°€?/summary>
    private bool _selfCheckLogged;

    // ---- 鑷姩瀛樻。锛堝穿婧冩仮澶嶏級------------------------------------------------
    //
    // 浜у搧閲?*娌℃湁"淇濆瓨"杩欎釜鍔ㄤ綔**锛屾墍浠ユ寜"鎸佷箙鐢诲竷"鏉ュ仛锛氭瘡 15 绉掞紙鏉夸功鍙樹簡鎵嶅啓锛?    // 瀛樹竴浠藉埌 %LOCALAPPDATA%锛屼笅娆℃墦寮€鑷姩鎺ヤ笂銆?    // 涓轰粈涔堝€煎緱锛氫竴鑺傝鐨勬澘涔︿涪浜嗘槸鏈€鍧忕殑澶辫触妯″紡锛涘悓绫诲紑婧愯蒋浠讹紙Xournal++锛?    // 姝ｅ洜涓?宕╂簝灏变涪"琚敤鎴锋彁浜嗕弗閲嶆暟鎹涪澶辩殑 issue銆?
    /// <summary>鑷姩瀛樻。鐨勯棿闅旓紙姣锛夈€傛澘涔︽病鍙樺氨涓嶅啓銆?/summary>
    private double _autoSaveEveryMs = 15000;
    private double _nextAutoSaveAtMs;
    private long _autoSavedVersion = -1;

    /// <summary>鑷鐢細杩欎竴鍦鸿窇涓嬫潵鑷姩瀛樻。鍐欎簡澶氬皯娆★紙楠岃瘉"娌″彉灏变笉鍐?锛夈€?/summary>
    internal int AutoSaveCount { get; private set; }

    /// <summary>鑷鐢細鎶婅嚜鍔ㄥ瓨妗ｉ棿闅旇皟鐭紙涓嶇劧寰楃瓑 15 绉掓墠楠屽緱鍒拌妭娴侊級銆?/summary>
    internal void SetAutoSaveIntervalForTest(double ms)
    {
        _autoSaveEveryMs = ms;
        _nextAutoSaveAtMs = 0;
    }

    /// <summary>鑷鐢細璧颁竴閬?鍚姩鏃舵帴涓婁笂娆℃澘涔?杩欐潯鐪熻矾寰勶紙鍚潖鏂囦欢瀹归敊锛夈€?/summary>
    internal void RestoreAutoSaveForTest() => RestoreAutoSaveIfAny();

    /// <summary>
    /// **鍚姩鏃惰涓嶈鎺ヤ笂涓婃鐨勬澘涔?*锛坄ui` 娈甸噷鐨?`restoreInk`锛夈€?*榛樿鍏?*銆?    ///
    /// 鐢ㄦ埛 2026-09-17锛?閫€鍑轰互鍚庡啀鎵撳紑涓嶇敤鎭㈠澧ㄨ抗鍚р€︹€﹀悗鏈熷彲浠ヨ缃紝浣嗘槸鎴戣寰?    /// 榛樿涓嶆仮澶嶅ⅷ杩广€? 鐞嗙敱鍐欏湪 <see cref="RestoreAutoSaveIfAny"/> 閲岋細鏁欏鏈哄櫒鏄?    /// 鍏敤鐨勶紝涓€寮€鏈哄氨閾烘弧涓婁竴鑺傝鐨勬澘涔︿笉鍚堢悊銆?    ///
    /// 瀹冨悓鏃剁**鍐?*锛氫笉璇荤殑涓滆タ涓嶅繀鍐欙紙瑙?<see cref="MaybeAutoSave"/>锛夈€?    /// </summary>
    internal bool RestoreInkOnStartup => GetUiPref("restoreInk") == "1";

    /// <summary>鑷鐢細绔嬪埢鎸夊綋鍓嶆枃妗ｅ啓涓€娆¤嚜鍔ㄥ瓨妗ｏ紙涓嶇湅闂撮殧锛夈€?/summary>
    internal void AutoSaveNow()
    {
        try { Recovery.SaveAuto(InkSerializer.Save(Doc)); }
        catch (Exception ex) { Console.WriteLine("鑷姩瀛樻。澶辫触锛? + ex.Message); }
        _autoSavedVersion = Doc.Version;
        AutoSaveCount++;
    }

    // ---- 鏉夸功鍘嗗彶锛? 鍙烽〉鐨勫揩鐓э紱瑙?BoardHistory锛?026-10-14 鍔狅級----------------
    //
    // 鍜?鑷姩瀛樻。"鐨勫垎宸ワ細鑷姩瀛樻。鍙湁**涓€浠?*銆佽€屼笖**鍙?鑷姩鎭㈠涓婃鏉夸功"鍋忓ソ绠¤緰**
    //锛堥粯璁ゅ叧鐫€ = 杩炲啓閮戒笉鍐欙級锛涙澘涔﹀巻鍙?*涓嶇湅閭ｄ釜鍋忓ソ**鈥斺€斿畠鏄?鑰佸笀鍥炲ご鑳芥壘鍥炶嚜宸卞啓杩囩殑涓滆タ"锛?    // 鐣欏瓨鏄暀瀛樸€佹樉绀烘槸鏄剧ず锛堥粯璁ら兘涓嶆樉绀猴級銆備笂闄愪笌杞浆鍦?BoardHistory 閲屻€?    /// <summary>鏈€灏忛棿闅旓細涓ゆ蹇収鑷冲皯闅旇繖涔堜箙锛堝仠椤块棬妲涗箣澶栫殑鍏滃簳锛岄槻"鍐欎袱绗斿仠涓€涓?棰戠箒鍐欑洏锛夈€?/summary>
    private const double HistoryMinGapMs = 10000;
    /// <summary>鎶瑪鍚庤嚦灏戦潤姝㈣繖涔堜箙鎵嶅厑璁稿啓蹇収锛堝啓瀛楁湡闂翠竴娆￠兘涓嶇鐩橈級銆?/summary>
    private const double HistoryIdleMs = 1500;

    private double _nextHistoryAtMs = HistoryMinGapMs;
    private long _historyFingerprint = -1;
    private bool _historyLogged;
    /// <summary>鏈€鍚庝竴娆℃姮绗旂殑鏃跺埢锛?鎵嬪仠浜嗘病鏈?鐨勫垽鎹級銆?/summary>
    private double _lastStrokeEndMs = double.NegativeInfinity;

    /// <summary>鑷鐢細绔嬪埢鍐欎竴浠芥澘涔﹀巻鍙诧紙涓嶇湅闂撮殧涓庢寚绾癸紱鍚屾钀界洏鈥斺€旀祴璇曡绔嬪埢鏁版枃浠讹級銆?/summary>
    internal bool SnapshotBoardHistoryForTest() => WriteBoardHistory(Doc.StrokesOf(0), sync: true);

    /// <summary>
    /// 姣忓抚杩囦竴瓒燂細**鎵嬪仠涓嬫潵涔嬪悗**锛堟姮绗?鈮?.5 绉掋€佷笖璺濅笂娆?鈮?0 绉掞級銆佽€屼笖鏉夸功鐢诲竷鐪熺殑鍙樹簡
    /// 锛堟寜鎸囩汗鍒わ級鎵嶅啓涓€浠姐€傛寕鍦?RenderAll 閲岋紙鍜岃嚜鍔ㄥ瓨妗ｅ悓涓€澶勶級锛岃嚜妫€閭ｆ潯璺篃鐓ф牱楠屽緱鍒般€?    ///
    /// 鈿?**涓轰粈涔堜笉"姣?15 绉掓棤鏉′欢鍐?**锛堢敤鎴?2026-10-14 鎻愮殑鍗￠】椋庨櫓锛屽疄娴嬪彛寰勶級锛?    /// 涓€浠藉揩鐓?鈮?0.1~3 MB锛堜笉鍚浘鐗囷級锛屽簭鍒楀寲 + 钀界洏鍦?UI 绾跨▼涓婄害 5~20ms鈥斺€?    /// 姝ｅソ鍚冩帀涓€甯э紙16ms 棰勭畻锛夈€傝惤鍦ㄥ啓瀛椾腑闂村氨鏄竴娆″彲瑙佺殑鍗￠】銆?    /// 鎵€浠ワ細**鍐欏瓧鏈熼棿涓€娆￠兘涓嶅啓**锛涘仠鎵嬭璇濈殑閭ｄ竴涓嬫墠鍐欙紙閭ｆ椂鎺変竴甯ф病浜虹湅寰楀嚭鏉ワ級銆?    /// 宕╂簝鍓?涓€鐩存病鍋滆繃鎵?鐨勯偅涓€灏忔闈犻€€鍑?閲嶅惎鏃堕偅涓€浠藉厹搴曘€?    /// </summary>
    private void MaybeHistorySnapshot()
    {
        if (NowMs < _nextHistoryAtMs) return;
        if (_drawing || ActiveStroke != null) return;
        if (NowMs - _lastStrokeEndMs < HistoryIdleMs) return;
        _nextHistoryAtMs = NowMs + HistoryMinGapMs;
        var board = Doc.StrokesOf(0);
        long fp = BoardFingerprint(board);
        if (fp == _historyFingerprint) return;
        if (WriteBoardHistory(board, sync: false)) _historyFingerprint = fp;
    }

    /// <summary>
    /// 鏉夸笂鍐呭鐨勬寚绾癸紙鏉℃暟 + 鎬荤偣鏁帮級銆傚彧鐢ㄦ潵鍒?鍊间笉鍊煎緱鍐嶅啓涓€浠?锛屼笉杩芥眰绮剧‘鈥斺€?    /// 澧炲垹涓€绗斻€佹摝鎺変竴娈甸兘浼氬彉锛涚湡鍒ら敊浜嗘渶澶氬鍐欎竴娆★紙瑕嗙洊鍐欙紝浠ｄ环鏈夌晫锛夈€?    /// </summary>
    private static long BoardFingerprint(List<Stroke> board)
    {
        if (board == null || board.Count == 0) return 0;
        long fp = board.Count;
        foreach (var s in board) fp = fp * 131 + s.Points.Count;
        return fp;
    }

    /// <summary>
    /// 鎶?0 鍙烽〉锛堟闈㈡澘涔︼級鍐欐垚涓€浠藉巻鍙插揩鐓с€?    /// **鍥剧墖涓€寰嬩笉瑕?*锛氭埅鍥鹃偅绉嶅璞℃暣鏉¤烦杩囷紙鐢ㄦ埛 2026-10-14 瀹氾級銆?    /// 绌烘澘涔︿笖杩欎竴杞繕娌″啓杩?鈫?涓嶇暀绌烘枃浠讹紙鍏嶅緱寮€涓€涓嬭蒋浠跺氨澶氫竴浠界┖鐨勶級銆?    /// `sync`锛?026-10-15锛夛細鍛ㄦ湡蹇収璧?*鍚庡彴鍐?*锛坒alse锛夛紱閫€鍑哄墠閭ｄ唤蹇呴』钀藉畬锛坱rue锛夈€?    /// </summary>
    private bool WriteBoardHistory(List<Stroke> board, bool sync)
    {
        var list = new List<Stroke>();
        if (board != null)
            foreach (var s in board)
                if (!s.IsImage) list.Add(s);

        if (list.Count == 0 && !BoardHistory.HasSession) return false;
        try
        {
            var blob = InkSerializer.SaveStrokes(list);
            string path = sync ? BoardHistory.Save(blob) : BoardHistory.SaveDeferred(blob);
            if (path == null) return false;
            if (!_historyLogged && list.Count > 0)
            {
                _historyLogged = true;
                Console.WriteLine($"[鏉夸功鍘嗗彶] 寮€濮嬪湪鍚庡彴鐣欏揩鐓э紙{list.Count} 涓璞★紱"
                                  + "銆屾洿澶?鈫?澧ㄨ抗 鈫?鍘嗗彶鏉夸功銆嶅彲鎵惧洖锛?);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("鏉夸功鍘嗗彶澶辫触锛? + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 鍒颁簡闂撮殧銆佽€屼笖鏉夸功鐪熺殑鍙樹簡锛屽氨鍐欎竴娆°€?    /// 鍒ゆ嵁鐢ㄦ枃妗ｇ増鏈彿锛坄Doc.Version`锛夆€斺€斿畠鍦ㄦ瘡娆″鍒犳敼鏃堕兘浼氬姞涓€锛?    /// 姣?姣?15 绉掓棤鏉′欢鍐欎竴閬?鐪佸緱澶氾紙涓€涓囩瑪鐨勫叏閲忓簭鍒楀寲涓嶆槸鐧界粰鐨勶級銆?    ///
    /// **鍋忓ソ鍏崇潃灏变竴娆￠兘涓嶅啓**锛氶粯璁ゆ。涓嶈閭ｄ釜鏂囦欢锛堣 <see cref="RestoreAutoSaveIfAny"/>锛夛紝
    /// 鍐欎竴涓病浜鸿鐨勬枃浠跺彧鏄櫧鐧藉簭鍒楀寲 + 鍦ㄧ敤鎴风殑鐩樹笂鐣欎竴浠芥澘涔︺€?    /// </summary>
    private void MaybeAutoSave()
    {
        if (!RestoreInkOnStartup) return;
        if (NowMs < _nextAutoSaveAtMs) return;

        // 2026-10-15锛堜綆閰嶆満"涔﹀啓鍋跺皵杩熺紦"锛夛細**鍙寫涔﹀啓闂撮殭鍐?*鈥斺€斿埌鐐规椂鎵嬭繕鍦ㄥ啓銆?        // 鎴栧垰鍋滅瑪锛?1.5s锛夊氨鍏堜笉鍐欙紙涓嶉噸缃棿闅旓紝涓嬩竴甯х户缁垽锛屾墜涓€鍋滃氨浼氳ˉ涓婏級銆?        // 鍦ㄥ啓鐨勮繃绋嬩腑搴忓垪鍖栨暣浠芥澘涔?= 涓€娆″彲鎰熺煡鐨勯】鎸紱杩欐槸"闅忔澘涔﹀ぇ灏忕嚎鎬у彉闀?鐨?        // 灏戞暟鍑犲涓荤嚎绋嬪伐浣滀箣涓€銆傚簭鍒楀寲浠嶈纰?Doc锛堝彧鑳戒富绾跨▼锛夛紝**鍐欑洏璧板悗鍙颁笂**
        //锛堣 AutoSaveDeferred / BackgroundSaver锛夈€?        if (_drawing || ActiveStroke != null) return;
        if (NowMs - _lastStrokeEndMs < HistoryIdleMs) return;

        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;
        if (Doc.Version == _autoSavedVersion) return;

        // PPT 妯″紡锛?*鏀规垚鍐欏綋鍓嶈繖涓€椤靛埌 PPT 鑷繁鐨勭洰褰?*锛屼笉鍐欒嚜鍔ㄥ瓨妗ｃ€?        // 涓轰粈涔堬細鑷姩瀛樻。鏄?妗岄潰鐨勯偅涓€浠芥澘涔?锛屼笅娆″惎鍔ㄤ細琚綋鎴愭闈㈡壒娉ㄦ帴鍥炴潵鈥斺€?        // 鎶婃煇涓€椤?PPT 鐨勫唴瀹瑰啓杩涘幓灏变覆浜嗭紙閭ｆ槸鍙︿竴鏉¤矾鐨勬暟鎹級銆?        // PPT 椤垫湰鏉ュ氨鏈夎嚜宸辩殑钀界洏閫氶亾锛堢炕椤垫椂銆侀€€鍑烘椂鍚勪竴娆★級锛岃繖閲岃ˉ涓?鏀炬槧涓€?
        // 閭ｄ竴娈碉紙鑰佸笀鍦ㄤ竴椤典笂鍐欏緢涔呫€佽繕娌＄炕椤靛氨宕╀簡锛屽唴瀹逛笉鑷充簬涓級銆?        // 2026-10-15锛氳繖鏉′篃鏀规垚**鍚庡彴鍐?*锛堝悓涓婏紝鍙寫涔﹀啓闂撮殭锛夈€?        if (PptMode)
        {
            SaveCurrentPptPage(deferred: true);
            _autoSavedVersion = Doc.Version;
            AutoSaveCount++;
            return;
        }

        AutoSaveDeferred();
    }

    /// <summary>鍛ㄦ湡鑷姩瀛樻。锛?026-10-15 鍔狅級锛氬簭鍒楀寲鍦ㄤ富绾跨▼锛堢 Doc锛夛紝**鍐欑洏璧板悗鍙颁笂**銆?    /// 閫€鍑?/ 閲嶅惎閭ｄ袱澶勪粛璧板悓姝ョ殑 <see cref="AutoSaveNow"/>锛堝繀椤昏惤瀹屾墠绠楁暟锛夈€?/summary>
    private void AutoSaveDeferred()
    {
        try { Recovery.SaveAutoDeferred(InkSerializer.Save(Doc)); }
        catch (Exception ex) { Console.WriteLine("鑷姩瀛樻。澶辫触锛? + ex.Message); }
        _autoSavedVersion = Doc.Version;
        AutoSaveCount++;
    }

    // ---- 鍘嗗彶娓呯悊锛堝ⅷ杩?B锛夛細鍚姩鍚?*棣栦釜绌洪棽甯?*鍋氫竴娆?----------------------
    //
    // 涓轰粈涔堜笉鍦ㄥ惎鍔ㄩ偅涓€鍒诲仛锛氳€佸笀鎸変笅鍥炬爣鍒扮獥鍙ｅ嚭鐜颁箣闂寸殑鏃堕棿瑕佸敖閲忕煭锛?    // 鎵垜浠嚜宸辩殑涓や釜灏忕洰褰曡櫧鐒舵槸姣绾э紝涔熶笉鍊煎緱鍗犲湪"寮€鏈?閭ｄ竴涓嬨€?    // 涓轰粈涔堟寕鍦?WM_TIMER锛?50ms 閭ｅ彛閽燂級涓婏細瀹冩湰鏉ュ氨鍦ㄦ淮绛旓紝鍊熶竴娆″氨澶熶簡銆?    private bool _historySwept = true;          // 鑷/鏃犵獥鍙ｆā寮忛粯璁?宸叉壂杩? = 姘歌繙涓嶆壂
    private double _historySweepAtMs = double.MaxValue;

    /// <summary>鑷鐢細鐩存帴鎸夌粰瀹氫繚鐣欐湡鎵竴娆★紙0 = 姘镐箙锛屼竴浠介兘涓嶅垹锛夈€?/summary>
    internal (int Files, int Dirs) SweepHistoryForTest(int days)
        => days <= 0 ? (0, 0) : InkHistory.Sweep(days);

    private void MaybeSweepHistory()
    {
        if (_historySwept || NowMs < _historySweepAtMs) return;
        _historySwept = true;
        int days = InkHistory.RetentionDays(GetUiPref("historyDays"));
        if (days <= 0) return;                  // 姘镐箙锛氫竴浠介兘涓嶅垹
        var (files, dirs) = InkHistory.Sweep(days);
        if (files > 0 || dirs > 0)
            Console.WriteLine($"[澧ㄨ抗] 鍘嗗彶娓呯悊锛堜繚鐣?{days} 澶╋級锛氬垹浜?{files} 涓枃浠躲€亄dirs} 涓┖鐩綍");
    }

    internal Stroke ActiveStroke;

    /// <summary>
    /// `--syswet`锛氳繖涓€绗旂殑婀垮ⅷ鐢辩郴缁熻建杩硅礋璐ｏ紙娓叉煋鏃朵笉鍐嶈嚜缁樺綋鍓嶇瑪鐢伙級銆?    /// 鐢?*寮曠敤鐩哥瓑**璁ょ瑪鐢伙紝鏀剁瑪/鎹㈢瑪閮戒笉浼氫覆銆?    /// </summary>
    private Stroke _systemWetStroke;
    /// <summary>搴旂敤渚?DComp 婀垮ⅷ鏇胯韩褰撳墠璐熻矗鏄剧ず鐨勭瑪鐢汇€?/summary>
    private Stroke _fastWetStroke;

    /// <summary>
    /// 搴旂敤渚ф箍澧ㄥ眰"杩欎竴甯ц繕娌℃彁浜?鐨勬寕璧锋爣璁帮紙2026-10-14 鍔狅級銆?    ///
    /// 涓轰粈涔堟敼鎴愭寕璧峰紡锛氬師鏉?*姣忔敹鍒颁竴鏉¤緭鍏ユ秷鎭?*灏卞仛涓€鏁磋疆 GPU 寰€杩?    /// 锛圔eginDraw 鈫?鎿?鈫?鐢?鈫?EndDraw 鈫?Commit锛夈€傜湡绗斾竴鏉℃秷鎭父甯︿竴涓插悎骞剁偣锛?    /// 鍐欏緱瓒婂揩杩欑寰€杩旇秺澶氾紝**鎶婃暣涓秷鎭惊鐜兘鎷栨參**鈥斺€旂敤鎴锋墜鏈?120Hz 閫愬抚褰曞儚瀹炴祴锛?    /// "搴旂敤婀垮ⅷ灞?閭ｄ竴妗ｅ弽鑰屾渶涓嶈窡鎵嬶紙绂荤瑪鏈€杩滐級銆傜幇鍦ㄦ寕璧枫€佺敱娓叉煋寰幆鍦?    /// **姣忓抚鐢讳富甯т箣鍓?*鎻愪氦涓€娆★紙瑙?RenderAll 閲岄偅涓€娈碉級锛氭垚鏈檷鍒?1/N锛?    /// 鑰屼笖浠嶇劧姣旀湰甯?Present 鏃╂彁浜も€斺€斿畠鍘熸潵鐨勯偅鐐逛紭鍔夸繚浣忋€?    /// </summary>
    private bool _fastWetPending;
    /// <summary>鎸傝捣鏃剁敤鐨勬渶鍚庡睆骞曚綅缃紙鍐冲畾鏇存柊鍝竴涓獥鍙ｇ殑婀垮ⅷ灞傦級銆?/summary>
    private float _fastWetScreenX, _fastWetScreenY;

    /// <summary>Overlay 娓叉煋鍒ゆ嵁锛氳繖涓€绗旀槸涓嶆槸"鐢辩郴缁熻建杩圭敾婀垮ⅷ"銆?/summary>
    internal bool IsSystemWetStroke(Stroke s) => s != null && ReferenceEquals(s, _systemWetStroke);
    internal bool IsFastWetStroke(Stroke s) => s != null && ReferenceEquals(s, _fastWetStroke);
    internal Tool Tool = Tool.Pen;
    /// <summary>Tool sizes are authored in logical pixels and scaled by the
    /// monitor DPI at use. Without this everything looks half-size on a 150%
    /// display, which is exactly how a 18px eraser turns into an unusable dot.</summary>
    internal float DpiScale = 1f;
    /// <summary>鐣岄潰缂╂斁妗ｏ紙瑙?<see cref="UiScalePresets"/>锛? = 鏍囧噯锛夈€傜墿鐞?= 閫昏緫 脳 DpiScale 脳 UiScale銆?/summary>
    internal float UiScale = 1f;
    /// <summary>鐣岄潰锛氫竴涓€昏緫鍗曚綅 = 澶氬皯鐗╃悊鍍忕礌銆?*鐣岄潰鐩稿叧鐨勬崲绠椾竴寰嬬敤瀹?*锛屽埆鍐嶇敤瑁?DpiScale銆?/summary>
    internal float UiUnit => DpiScale * UiScale;

    internal float EraserRadiusLogical = 22f;
    /// <summary>
    /// 鏁寸瑪姗＄毊鐨勮惤鐐瑰崐寰勶紙閫昏緫鍍忕礌锛夌殑妗ｄ綅銆?*浠ュ墠瀹冩牴鏈病鏈夋。浣?*鈥斺€?    /// 鐣岄潰涓婄粰姗＄毊鐢讳簡绮楃粏婊戞潯锛屽彲寮曟搸閲?`SetWidthFromUi` 鎶婂畠褰掑埌"鍏跺畠"閭ｄ竴鏀紝
    /// 缁撴灉鏀圭殑鏄?*绗斿**锛氳€佸笀鎷栨鐨殑婊戞潯锛岀瑪杩圭矖缁嗘倓鎮勫彉浜嗐€佹鐨竴鐐规病鍙樸€?    /// 鐜板湪鍥涚宸ュ叿鍚勮鍚勭殑銆?*2026-10-09 鏅氱敤鎴峰畾锛氬浐瀹氫簲妗ｃ€佹粦鍔ㄥ惛闄勫湪妗ｄ笂**
    /// 锛堜笉鍍忕瑪閭ｆ牱閾哄紑寰堝妗ｂ€斺€旀鐨氨 5 涓ぇ灏忥紝鎷栧埌鍝兘钀藉湪鏈€杩戠殑妗ｏ級銆?    /// </summary>
    internal static readonly float[] EraserRadiusPresets = { 12f, 17f, 22f, 28f, 34f };
    internal int EraserRadiusIndex = 2;
    /// <summary>涓ょ姗＄毊鍚勮嚜鐨勫彲璋冭寖鍥达紙閫昏緫鍍忕礌锛夈€傜晫闈㈡粦鏉＄殑鑼冨洿瑕佸拰杩欓噷涓€鑷淬€?/summary>
    internal const float EraserRadiusMin = 8f, EraserRadiusMax = 48f;
    internal const float PixelEraserMinWidth = 30f, PixelEraserMaxWidth = 160f;
    internal float PenWidthLogical = 3f;
    internal float HighlighterWidthLogical = 18f;
    /// <summary>
    /// 婵€鍏夌瑪鐨勭矖缁嗐€備互鍓嶆縺鍏夊€熺殑鏄?*绗斿**锛坄Engine.cs` 閲岄偅鏉?    /// `tool == Highlighter ? Highlighter : Pen`锛夛紝浜庢槸"鍒囩矖缁?瀵规縺鍏夋病鎰忎箟銆?    /// 钀界偣鐨勭偣澶у皬涔熸病娉曡窡绗旇抗瀵逛笂銆傜幇鍦ㄥ洓绉嶅伐鍏峰悇璁板悇鐨勩€?    ///
    /// 鈿?榛樿妗?2026-09-27 浠?4 鏀圭矖鍒?8锛堢敤鎴凤細"鍙互鎶婇粯璁ゆ。鏀圭矖涓€鐐?锛夈€?    /// 涓轰粈涔堝綋鏃舵樉寰楃粏锛氶偅鏉¤建杩规槸"**缁嗙櫧鑺?锛?涓€灞傛贰绾㈡檿**"锛?    /// 鐧借姱鍙崰 0.32 涓洿寰?鈫?4 鐨勭洿寰勪笅鐧借姱鎵?1.3 鍍忕礌锛岃倝鐪煎熀鏈彧鍓╀竴鐐圭孩闆俱€?    /// 鐜板湪鍙戝厜姣斾緥鏀规垚 ClassIn 閭ｇ锛?*瀹界櫧鑺?锛?缁嗙孩杈?锛?鏌斿厜**锛岃 `Overlay.DrawLaser`锛夛紝
    /// 鍚屼竴妗ｅ湪灞忓箷涓婁篃鏇?瀹?銆?    /// </summary>
    internal float LaserWidthLogical = 8f;
    /// <summary>
    /// **绗旂殑绾垮瀷**锛堢敤鎴?2026-09-19 绗?2 浠讹細绗旂殑鑹插甫鏉′笂瑕佽兘鍒囪櫄瀹炵嚎锛夈€?    ///
    /// 鍜?<see cref="PenWidthLogical"/> 鍚屼竴涓湴浣嶏細**杩欐敮绗旇嚜宸辩殑璁剧疆**銆?    /// 鍙綔鐢ㄤ簬鏂扮敾鍑烘潵鐨?*鑷敱绗旇抗**锛岃€屼笖鏄?鍙湁绗旂敤"鈥斺€?    /// 鑽у厜绗斾笌婵€鍏夌瑪鐨勪袱鏉″崐閫忔槑/鍙戝厜杞ㄨ抗鐢绘垚铏氱嚎娌℃湁鎰忎箟锛屽浘褰篃涓嶅悆瀹?    /// 锛堝浘褰㈢殑绾垮瀷鍘嗘潵鏄?閫変腑涔嬪悗鍦ㄦ搷浣滄潯闈㈡澘閲屾敼"锛岃 <see cref="SetSelectionDash"/>锛夈€?    ///
    /// 鐢荤殑鏃跺€欏彇褰撴椂鐨勫€煎啓杩涘璞★紙瑙?<see cref="Stroke.Dash"/>锛夛紝涔嬪悗鍚勫瓨鍚勭殑锛?    /// 鏀硅繖涓紑鍏?*涓嶄細**鍔ㄥ凡缁忓啓鍦ㄦ澘涓婄殑涓滆タ鈥斺€斿拰鍧愭爣绯荤綉鏍奸偅鏉′竴涓彛寰勩€?    /// </summary>
    internal StrokeDash PenDash = StrokeDash.Solid;
    internal float EraserRadius => EraserRadiusLogical * DpiScale;
    private float _lastEraseX, _lastEraseY;

    // ---- 鍔ㄦ€佹鐨紙8.3.4 璧凤紱8.3.6 鎹㈠舰鐘讹紱8.3.7 瀹氱獥鍙ｄ笌闂ㄦ锛?-------------
    //
    // 闈㈢Н鎿︾殑灏哄璺熺潃**绉诲姩閫熷害**璧帮細**闈欐/鎱?= 浣犺皟鐨勯偅涓熀鍑嗭紙涓嶇缉锛?*銆佸揩 = 鏈€澶?2.5 鍊嶃€?    // 褰㈢姸鐓ч殧澹?Inkeys銆岀瑪閫熸鐨€嶅畾锛堜粬浠偅鏉℃湰鏉ュ氨鏄粰瑙﹀睆璁惧鐨勶級锛?    //   浠栦滑锛歚speed鈮?0 鈫?max(25, speed脳2.33+13.33)`銆乣speed>20 鈫?min(200, 3.0脳speed)`
    //         鈫?鏈?*涓嬮檺 25px**锛堟參鍒板簳涓嶅啀灏忥級銆佹湁**涓婇檺 200px**銆佷腑闂翠竴娈电嚎鎬ф枩鍧★紱
    //   鎴戜滑锛歚factor = clamp(1.0 + (v 鈭?0.80) 脳 1.0, 1.0, 2.5)`锛坴 鍗曚綅鐗╃悊鍍忕礌/姣锛?    //         鈫?0.80 浠ヤ笅鎭?**1.0锛堝熀鍑嗭紝涓€鐐逛笉缂╋級**銆佷互涓婄嚎鎬ф定銆?.3 鍒伴《銆?    //         **8.3.8**锛氫笅闄愪粠 0.7 鎻愬埌 1.0锛堢敤鎴凤細"鐐瑰嚮涓€涓嬩笉鍔ㄧ殑鏃跺€欙紝姗＄毊浼氱缉灏忓悧锛熸垜璋冪殑
    //         鍒濆鍊煎簲璇ュ氨鏄粯璁ゅぇ灏忥紝鐐瑰嚮浠ュ悗涓嶇缉灏?锛夆€斺€旀寜浣忎笉鍔ㄦ椂鎵嬫寚/榧犳爣鐨?*寰皬鎶栧姩**
    //         涔熶細琚畻鎴?閫熷害"锛屼簬鏄參鎱㈡粦鍒?0.7 鍊嶃€傜幇鍦?*闈欐/鎱㈤€熷氨鏄熀鍑?*锛屼笉缂┿€?    //
    // 8.3.4 鐨勪袱涓瘺鐥咃紙鐢ㄦ埛鎶?鎱㈤€熶笅蹇藉ぇ蹇藉皬"锛?.3.6 閮芥不浜嗭細
    //   鈶?閫熷害鍘熸潵鐢?杩欎竴娆℃秷鎭殑 dist 梅 dt"鈥斺€旀參閫熸椂涓€娆″彧璧颁竴涓ゅ儚绱犮€侀櫎浠ュ緢灏忕殑 dt锛?    //      浼板嚭鏉ョ殑閫熷害澶╃劧鎶?鈫?鐜板湪**绐楀彛绱**锛堟敀澶?6 鍍忕礌鎴?30ms 鎵嶇畻涓€娆★級锛?    //   鈶?骞虫粦鍘熸潵 伪=0.35锛堚増25ms 鏃堕棿甯告暟锛屽お鐏碉級鈫?鐜板湪鎸?*鏃堕棿甯告暟**骞虫粦锛堟定 120ms /
    //      鏀?450ms + 鏈€灏戜竴姝?0.01锛夆€斺€旇繖灏辨槸 Inkeys 閭ｄ釜"姣忔鍙蛋宸窛鐨?1/50銆佽嚦灏?0.1px"
    //      鐨勫悓婧愬仛娉曪紙浠栦滑鎸夋秷鎭暟绠椼€佹垜浠寜鏃堕棿绠楋紝甯х巼鏃犲叧锛夈€?    //   鍙﹀姞**鍥炲樊**锛?.8 娑?/ 0.6 鍥烇級锛氶€熷害鍦ㄩ棬妲涢檮杩戞檭鏃跺昂瀵镐笉浼氭潵鍥炲垏銆?    //
    // **8.3.7锛堢敤鎴锋姤"甯歌閫熷害涓嬭繕鏄拷澶у拷灏?锛?*锛氶棶棰樹笉鍦ㄦ洸绾跨殑鏁帮紝鑰屾槸锛?    //   鈶?绐楀彛鎸?*鍍忕礌**鏀掞紙"鏀掑 6px"锛夆€斺€斿父瑙勯€熷害涓嬪嚑姣灏辨敀婊★紝绛変簬娌℃湁绐楀彛锛?    //   鈶?闂ㄦ 0.35 **姣斿父瑙勬摝瀛楅€熷害杩樹綆**鈥斺€旂瓑浜?涓€鎿﹀氨鍙樺昂瀵?銆?    //   鐜板湪锛氱獥鍙?*鎸夋椂闂存敀婊?100ms**銆侀棬妲涙彁鍒?**0.80**锛堝父瑙勯€熷害钀藉湪姝诲尯閲岋紝灏哄绾逛笣涓嶅姩锛夈€?    //   骞虫粦鍐嶆斁鎱紙娑?220 / 鏀?450锛夛紝骞跺姞 `--eraserhud` 璇绘暟鈥斺€旈棬妲涙寜鐪熸満璇绘暟瀹氾紝涓嶉潬鐚溿€?    //
    // 鈿?**鍙綔鐢ㄤ簬闈㈢Н鎿?*銆?鏁寸瑪鎿?鐨?澶у皬"鏄?*鍛戒腑鍗婂緞**锛堢鍒板摢鏉″垹鍝潯锛夛紝
    //   璁╁崐寰勯殢閫熷害鍙?= "鐐瑰埌鍝潯鍏ㄧ湅鎵嬮€?锛屼笉鍙鏈燂紝鎵€浠ユ暣绗旀摝鎭掑畾銆?    // 鉁?**涓嶅垎璁惧**锛氱瑪 / 榧犳爣 / 鎵嬫寚璧扮殑鏄悓涓€娈垫摝闄や唬鐮侊紝鎵€浠ユ墜鎸囧湪瑙︽懜灞忎笂鎶归潰绉摝鐓ф牱鏈夈€?    // 鍚庨棬锛歚--eraserfixed` 鍏虫帀鍔ㄦ€侊紙涓嶈繘鐣岄潰锛夈€?    internal bool DynamicEraser = true;

    /// <summary>鏈€鎱?闈欐鏃剁殑绯绘暟涓嬮檺 = **1.0锛堝氨鏄熀鍑嗭級**锛氫綘璋冪殑閭ｄ釜澶у皬灏辨槸榛樿澶у皬锛?    /// 鎸変綇涓嶅姩銆佹參鎱㈡姽閮戒笉缂╋紙8.3.8 浠?0.7 鎻愪笂鏉ワ紱Inkeys 閭ｈ竟鏄釜 25px 鍦版澘锛屼絾鎴戜滑鐨?    /// 鍩哄噯鏄敤鎴疯嚜宸辫皟鐨勬粦鏉★紝"闈欐 = 浣犺皟鐨勫€?鎵嶇鍚堢洿瑙夛級銆?/summary>
    internal const float EraserFactorMin = 1.0f;
    /// <summary>鏈€蹇椂鐨勭郴鏁颁笂闄愶紙鍩哄噯鐨?2.5 鍊嶏紱Inkeys 鐨勫搴旂墿鏄?200px 灏侀《锛夈€?/summary>
    internal const float EraserFactorMax = 2.5f;
    /// <summary>寮€濮嬫定鐨勯棬妲涳紙鐗╃悊鍍忕礌/姣锛夆€斺€?*蹇呴』楂樹簬"甯歌鎶?鐨勯€熷害**锛?.3.7 瀹炴祴鍙ｅ緞锛?    /// 甯歌鎿﹀瓧 鈮?.4~0.7銆佸埢鎰忓揩鎵?鈮?.5锛夈€傞棬妲涗互涓嬫亽涓嬮檺 = "甯歌閫熷害姘歌繙鍚屼竴灏哄"銆?/summary>
    internal const float EraserSpeedKnee = 0.8f;
    /// <summary>鍥炲樊涓嬫部锛氭帀鍒拌繖浠ヤ笅鎵嶅洖涓嬮檺锛?.6~0.8 涔嬮棿淇濇寔锛岄槻闂ㄦ闄勮繎鏉ュ洖鍒囷級銆?/summary>
    internal const float EraserSpeedBack = 0.6f;
    /// <summary>鏂滃潯鏂滅巼锛歚factor = 涓嬮檺 + (v 鈭?闂ㄦ) 脳 鏂滅巼`锛?.8 鈫?2.3 px/ms 涔嬮棿娑ㄥ埌椤讹級銆?/summary>
    internal const float EraserSpeedSlope = 1.0f;
    /// <summary>鍒伴《鐨勯€熷害锛坧x/ms锛夛細`闂ㄦ + (涓婇檺 鈭?涓嬮檺) 梅 鏂滅巼`銆傜粰璇绘暟/鏂囨。鐢ㄣ€?/summary>
    internal static float EraserSpeedTop => EraserSpeedKnee + (EraserFactorMax - EraserFactorMin) / EraserSpeedSlope;
    /// <summary>骞虫粦鏃堕棿甯告暟锛堟绉掞級锛氭定寰楁瘮鏀跺緱蹇竴鐐癸紙璺熸墜锛夛紝浣嗛兘杩滄參浜庢墜鎶栫殑棰戠巼銆?/summary>
    internal const float EraserGrowTauMs = 220f, EraserShrinkTauMs = 450f;
    /// <summary>姣忔鏈€灏戞尓鍔ㄧ殑绯绘暟锛圛nkeys 閭?0.1px 淇濆簳姝ラ暱鐨勫悓婧愬仛娉曪紝淇濊瘉鏀跺緱鍥炴潵锛夈€?/summary>
    private const float EraserMinStep = 0.01f;
    /// <summary>
    /// 閫熷害閫氶亾锛堟妱 MyPaint 鐨?**Fine/Gross Speed**锛歠ine 璺熸墜銆乬ross "changes very slowly"锛夈€?    /// 鐩爣閫熷害鍙栦袱鑰呯殑**杈冨ぇ鍊?*锛氬揩鎵椂 fine 绔嬪埢璧蜂綔鐢紱鎱笅鏉ユ椂 gross 杩樺仠鍦ㄦ棫閫熷害涓婏紝
    /// **灏哄涓嶄細涓€鎱㈠氨濉?*鈥斺€旇繖灏辨槸"鍋滀綇鍐嶈交鍔ㄤ竴涓嬬獊鐒跺彉灏?鐨勪竴鍗婅В娉曪紙鍙︿竴鍗婃槸涓嬮潰鐨勭紦閲婏級銆?    /// 姣忎釜閫氶亾"鏀掑鏃堕棿鎴栬窛绂诲氨缁撶畻涓€娆?銆?    /// </summary>
    internal const float EraserFineWindowMs = 40f, EraserFineWindowPx = 12f;
    internal const float EraserGrossWindowMs = 350f, EraserGrossWindowPx = 120f;
    /// <summary>鎸囬拡鍋滀綇涔嬪悗鍏?*淇濇寔**澶氫箙鎵嶅紑濮嬬紦閲婏紙鍏嶅緱鎵埌涓€鍗婂仠涓€涓嬨€佸昂瀵稿氨缂╋級銆?/summary>
    internal const float EraserIdleHoldMs = 150f;
    /// <summary>"姝ｅ湪鍑忛€?鏃剁殑淇濇寔鏃堕棿锛堟洿鐭?鈫?鎻愬墠寮€濮嬫敹锛夈€傚垽鎹?= `fine &lt; gross 脳 0.7`锛?    /// 涓や釜婊ゆ尝閲忕殑宸氨鏄?*鍔犻€熷害鐨勭鍙?*锛屽櫔澹拌鍘嬩簡涓ら亶鈥斺€旂敤鎴锋兂瑕?鍔犻€熷害"鐨勭ǔ瀹氫唬鐞嗐€?/summary>
    internal const float EraserDecelHoldMs = 60f;

    private float _eraseSpeedEma;        // 褰撳墠鐢ㄧ殑閫熷害 = max(fine, gross)锛堢墿鐞嗗儚绱?姣锛?    private double _eraseLastMs;         // 涓婁竴娆￠€熷害閲囨牱鏃跺埢
    private float _eraseFine, _eraseGross;                       // 涓ゆ潯閫熷害閫氶亾锛坧x/ms锛?    private float _eraseFineDist, _eraseFineMs;                  // fine 绐楀彛绱锛?0ms / 12px锛?    private float _eraseGrossDist, _eraseGrossMs;                // gross 绐楀彛绱锛?50ms / 120px锛?    private float _eraseDynFactor = 1f;  // 褰撳墠灏哄绯绘暟锛堣惤绗?= 1锛?    private float _eraseTarget = 1f;     // 骞虫粦鐨勭洰鏍囷紙鍥炲樊鐘舵€佷篃瀛樿繖鍎匡級
    private double _eraseHoldBaseMs;     // 涓婁竴娆?*绉诲姩**鐨勬椂鍒伙紙缂撻噴鐨勪繚鎸佹湡浠庤繖閲岀畻锛?    private bool _eraseDecaying;         // 宸茬粡杩涘叆"鍋滀綇缂撻噴"闃舵锛堟瘡甯ф帹锛涗竴鍔ㄥ氨閫€鍑猴級
    private float _eraseTestIdle;        // 鑷鐢細绱鐨勭┖闂叉椂闂?
    /// <summary>姗＄毊璇婃柇娴眰锛坄--eraserhud`锛夛細瀹炴椂鏄剧ず 閫熷害 / 鐩爣 / 褰撳墠绯绘暟 / 灏哄銆?    /// 瀹冩槸**璋冨弬宸ュ叿**锛氱湡鏈轰笂鎿﹀嚑涓嬶紝璇诲嚭"甯歌閫熷害鏄灏?px/ms"锛岄棬妲涘氨鎸夐偅涓暟瀹氥€?/summary>
    internal bool EraserHud;
    internal string EraserHudText = "";
    private double _eraserHudNextMs;

    /// <summary>
    /// 闈㈢Н鎿?*姝ｅ湪鎷?*鍚椼€傛鍙湪鎷栧姩涓窡鐫€閫熷害鍙橈紱**鎮仠鏃舵樉绀哄熀鍑嗘**鈥斺€斿洜涓鸿惤绗旂涓€涓?    /// 鐢ㄧ殑灏辨槸鍩哄噯锛堥€熷害杩樻病鏈夛級锛屾墍浠?鎮仠鐪嬭鐨?= 鎸変笅鍘荤涓€涓嬫摝鎺夌殑"锛屾墍瑙佸嵆鎵€寰椼€?    /// </summary>
    internal bool PixelEraseDragging;

    /// <summary>
    /// 钀界偣妗?= **鐪熸浼氳鎿︽帀鐨勯偅涓€鍧?*锛堢墿鐞嗗儚绱犲崐瀹?鍗婇珮锛夈€傛摝闄わ紙`EraseRectAlongPath`锛?    /// 鍜?Overlay 鐢荤殑閭ｄ釜妗嗚鐨勬槸**鍚屼竴浠?*鈥斺€斾笉鐒跺氨浼氬嚭鐜?鐪嬭鐨勬"鍜?鎿︽帀鐨勮寖鍥?瀵逛笉涓娿€?    /// 鎷栧姩涓?= 鍩哄噯 脳 閫熷害绯绘暟锛涙病鍦ㄦ嫋 = 鍩哄噯銆?    /// </summary>
    internal float PixelEraserCursorHalfWidthPx
        => PixelEraserHalfWidthPx * (PixelEraseDragging ? _eraseDynFactor : 1f);
    internal float PixelEraserCursorHalfHeightPx
        => PixelEraserHalfHeightPx * (PixelEraseDragging ? _eraseDynFactor : 1f);

    /// <summary>
    /// 鍍忕礌姗＄毊鐨勮惤鐐瑰昂瀵革紙閫昏緫鍍忕礌锛夛細**绔栫潃鐨勯粍閲戞瘮渚嬬煩褰?*锛岄珮 : 瀹?= 1.618銆?    ///
    /// 榛樿瀹?93 脳 楂?150锛氱敤鎴峰疄娴嬬殑鐢ㄦ硶鏄?浠庝笂寰€涓嬫姽涓€娈?锛堜竴鍒楁澘涔︺€佷竴涓珫鎺掔殑瀛楋級锛?    /// 涓嶆槸妯壂涓€琛岋紝鎵€浠ョ珫杈规瘮妯竟闀裤€侰trl+Alt+6 鍦ㄨ繖閲岀殑鍑犳。涔嬮棿鍒囷紙妗ｄ綅鍚屾牱瀹堥粍閲戞瘮锛夈€?    /// </summary>
    internal const float GoldenRatio = 1.618f;
    internal float PixelEraserWidthLogical = 93f;
    /// <summary>闈㈢Н姗＄毊浜旀。锛?026-10-09 鏅氱敤鎴峰畾锛氬浐瀹?5 妗ｃ€佹粦鍔ㄥ惛闄勶紱
    /// 灏辨槸涔嬪墠寰呭姙閲岄偅缁?46/70/93/122/150锛夈€傞粯璁?93锛堜笅鏍?2锛夈€?/summary>
    internal static readonly float[] PixelEraserWidthPresets = { 46f, 70f, 93f, 122f, 150f };
    internal int PixelEraserWidthIndex = 2;
    internal float PixelEraserHeightLogical => PixelEraserWidthLogical * GoldenRatio;
    internal float PixelEraserHalfWidthPx => PixelEraserWidthLogical * 0.5f * DpiScale;
    internal float PixelEraserHalfHeightPx => PixelEraserHeightLogical * 0.5f * DpiScale;

    /// <summary>鎴浘宸ュ叿鐨勬嫋鍔ㄧ煩褰紙鐢诲竷鍧愭爣锛夈€?/summary>
    internal bool CaptureActive;
    internal float CapMinX, CapMinY, CapMaxX, CapMaxY;

    /// <summary>
    /// **鏉惧紑涔嬪悗銆佺‘璁や箣鍓?*鐨?璋冩暣"闃舵锛?.3.0 鍔犵殑锛夈€?    ///
    /// 浠ュ墠鏉炬墜 = 绔嬪埢鎶撳睆钀藉浘锛屾姝簡鍙兘鍒犳帀閲嶆埅銆傜幇鍦ㄦ澗鎵嬪厛鐣欐锛?    /// 鍙互鎷栬竟/鎷栬/鎷栨暣浣擄紝`Enter` 鎴栫偣銆屸湏 瀹屾垚銆嶆墠钀藉浘锛宍Esc`/鍙抽敭/鐐规澶栧彇娑?    ///锛堝井淇?/ QQ / Snipaste 閮芥槸杩欎釜鎵嬫劅锛夈€?    /// </summary>
    internal bool CaptureAdjusting;

    /// <summary>
    /// **鍐荤粨甯?*锛?.3.0锛夛細鎸変笅鎴浘鐨勯偅涓€鐬棿锛屽厛鎶?*鏁村潡铏氭嫙妗岄潰**鎶撲笅鏉ュ瓨鐫€锛?    /// 鏉炬墜/纭鏃跺彧鏄?*瑁佸壀**瀹冦€?    ///
    /// 涓轰粈涔堣繖涔堟敼锛堢敤鎴?2026-09-30 閫夌殑鏂规锛夛細鏃ф祦绋嬫槸鏉炬墜鏃惰棌绐楀彛锛?0ms锛夊啀鎶撳睆锛?    /// 鑰佸笀鑳界湅鍒?鎵规敞鏁村眰闂竴涓?锛涜€屼笖鏉炬墜灏卞畾灞€銆佹病娉曡皟鏁淬€傚喕缁撲箣鍚庯細
    ///   路 鎶撳彇鍙彂鐢熷湪**鎸変笅閭ｄ竴鍒?*涓€娆★紙`闅愯棌鐣岄潰` 妯″紡鐨勯偅 60ms 闂湪鍒氭寜涓嬫椂锛屼箣鍚庡叏绋嬩笉闂級锛?    ///   路 鏉炬墜鍙鍓唴瀛橈紝灞忓箷涓嶅姩 鈫?璋冩暣闃舵鍙互闅忎究鎷栵紱
    ///   路 娲為噷鐢荤殑鏄?*鍐荤粨甯ф湰韬?*锛堟墍瑙佸嵆鎵€寰楋細鎷嶅埌鐨勫氨鏄湅鍒扮殑锛夈€?    /// 浠ｄ环锛氭媿鍒扮殑鐢婚潰 = 鎸変笅閭ｄ竴鍒伙紙PPT 鍔ㄧ敾浼氬仠鍦ㄦ寜涓嬫椂锛夛紱鍐呭瓨鐬€?~20MB锛堜竴鍧楁暣灞忥級銆?    /// </summary>
    internal byte[] CaptureFrozenBgra;          // 鐗╃悊鍍忕礌銆丅GRA銆佽嚜涓婅€屼笅銆佹暣鍧楄櫄鎷熸闈?    internal int CaptureFrozenX, CaptureFrozenY, CaptureFrozenW, CaptureFrozenH;
    internal bool CaptureFrozen;                // 鍐荤粨甯у湪鎵嬩笂鍚楋紙鎶撳け璐?鈫?false锛岄€€鍥炴澗鎵嬫椂鎶擄級

    internal enum CaptureDrag { None, Move, L, R, T, B, TL, TR, BL, BR }
    private CaptureDrag _capDrag = CaptureDrag.None;
    private float _capDragX, _capDragY;         // 鎸変笅鏃剁殑鎸囬拡锛堢敾甯冿級
    private float _capD0MinX, _capD0MinY, _capD0MaxX, _capD0MaxY;   // 鎸変笅鏃剁殑妗?    /// <summary>璋冩暣闃舵閭ｄ竴娆℃寜涓嬪凡缁忚鎴浘鍚冩帀锛堟姮鎵嬫椂鍒啀璧版閫夐偅濂楋級銆?/summary>
    private bool _capturePressHandled;

    /// <summary>
    /// 杩涙埅鍥炬ā寮?*涔嬪墠**鐢ㄧ殑宸ュ叿锛?.3.1锛夛細Esc / 鍙抽敭 /銆屸湑銆嶅彇娑堟椂鍥炲埌瀹冿紝
    /// 鑰屼笉鏄妸鑰佸笀鐣欏湪"鎴浘宸ュ叿涓婄┖绛?鐨勭姸鎬侀噷銆傚湪 `SwitchTool(鈫扖apture)` 鏃惰涓€娆°€?    /// </summary>
    private Tool _toolBeforeCapture = Tool.Pen;

    /// <summary>璋冩暣闃舵"鍙屽嚮妗嗗唴 = 瀹屾垚"鐨勫垽鎹紙寰俊鐨勬墜鎰燂級锛氫笂娆℃寜涓嬬殑鏃跺埢/浣嶇疆銆?/summary>
    private double _capLastClickMs = double.NegativeInfinity;
    private float _capLastClickX, _capLastClickY;

    /// <summary>妗嗙殑鏈€灏忚竟闀匡紙閫昏緫鍍忕礌锛夛細姣旇繖杩樺皬褰撴垚璇偣/鍙栨秷銆?/summary>
    internal const float CaptureMinSizeLogical = 10f;

    /// <summary>璋冩暣闃舵鐨?8 涓墜鏌勫懡涓崐寰勶紙閫昏緫鍍忕礌锛夈€?/summary>
    internal const float CaptureHandleHitLogical = 14f;

    /// <summary>銆屸湏 瀹屾垚 / 鉁?鍙栨秷銆嶄袱棰楁寜閽殑灏哄涓庨棿璺濓紙閫昏緫鍍忕礌锛夈€?/summary>
    internal const float CaptureBtnWLogical = 86f, CaptureBtnHLogical = 30f, CaptureBtnGapLogical = 8f;

    /// <summary>
    /// 鎴浘妯″紡锛堢敤鎴?2026-09-17 瑕佺殑"瀵规帴"锛屽弬鑰?InkClass 鐨勪袱椤硅彍鍗曪級锛?    ///   路 **true = 闅愯棌鐣岄潰鎴彇**锛堥粯璁わ紝涔熸槸鍘熸潵鐨勮涓猴級锛氭姄涔嬪墠鎶婃暣涓鐩栧眰钘忚捣鏉ワ紝
    ///     鎷嶅埌鐨勫彧鏈変笅灞傚唴瀹光€斺€?*鎵规敞銆佺櫧鏉裤€侀潰鏉夸竴璧疯棌**锛屼竴姒備笉鍏ラ暅銆?    ///     锛堢敤鎴?2026-09-17 鏇存杩囧彨娉曪細鍘熸潵鍐?闅愯棌鎵规敞鎴彇"锛?    ///      浣嗗畠鍏跺疄杩炵櫧鏉垮拰闈㈡澘涓€璧疯棌锛屾墍浠ョ晫闈笂鎶婅繖妗ｅ彨**"闅愯棌鐣岄潰"**銆傦級
    ///   路 **false = 鐩存帴鎴彇**锛?*杩炴澘涔︿竴璧锋媿**锛堣€佸笀鎯虫妸"PPT + 鎴戝啓鐨勬壒娉?涓€璧风粰鍒汉锛?    ///     鎴栬€呮妸鑷繁鍐欑殑瑙ｉ杩囩▼鍋氭垚涓€寮犲浘锛夈€?    ///
    /// 鐩存帴鎴彇鏃?*鍙栨櫙妗嗘湰韬?*杩樻槸瑕佽棌鎺夛紙涓嶇劧閭ｄ釜鐞ョ弨鑹叉浼氭媿杩涘浘閲岋級鈥斺€?    /// 瑙?<see cref="CaptureFrameHidden"/>銆?    /// </summary>
    internal bool CaptureHideInk = true;

    /// <summary>
    /// 杩欎竴甯т笉鐢?鎴戜滑鑷繁鐩栧湪鐢婚潰涓婄殑涓滆タ"鈥斺€斿彇鏅**鍜岃惤鐐瑰厜鏍?*锛堝彧鍦?鐩存帴鎴彇"
    /// 鎶撳睆鐨勯偅涓€鐬负鐪燂級銆傚厜鏍囦篃寰楄棌锛氬畠鏄惀鐝€鑹茬殑锛岀暀鐫€灏变細琚媿杩涘浘閲屻€?    /// 8.3.0 璧疯繛**閬僵/鍑嗙嚎**涓€璧蜂笉鐢伙紙鍐荤粨甯у氨鎶撳湪杩欎竴鐬紝涓€涓佺偣鎴戜滑鑷繁鐨勪笢瑗块兘涓嶈兘杩涘浘锛夈€?    /// </summary>
    internal bool CaptureFrameHidden;

    // ---- 鐧芥澘搴曠汗锛堟柟鏍?/ 妯嚎锛?-------------------------------------------
    //
    // 鐢ㄦ埛 2026-09-17 瑕佺殑锛?缁欑櫧鏉垮鍔犵綉鏍煎拰妯嚎鍔熻兘锛屽彲浠ュ弬[鑰僝inkclass 鐨勫疄鐜?銆?    // InkClass 閭ｈ竟锛坄MW_WhiteboardPattern.cs`锛夌殑鍋氭硶涓庡彇鑸嶏細
    //   路 涓夋。锛?*鏃?/ 鏂规牸 / 妯嚎**锛?    //   路 **涓嶆槸绗旇抗**鈥斺€?姗＄毊鎿︿笉鎺夈€佹挙閿€涓嶆秹鍙娿€侀€夋嫨閫変笉涓€佹崲椤靛叡鐢?锛?    //   路 绾跨粏 0.5 閫昏緫鍍忕礌锛堣緟鍔╁弬鑰冪嚎锛岀氦缁嗕笉鎶㈣瑙夛級锛涚嚎鑹查殢鏉块潰鏄庢殫鑷€傚簲
    //     锛堟祬鏉匡細鐏?60%锛涙繁鏉匡細鐧?20%锛夛紱
    //   路 闂磋窛鍙皟锛堝畠缁欑殑鏄?16锝?40 鐨勬粦鍧楋級銆?    //
    // 鎴戜滑杩欓噷鏇磋繘涓€姝ワ細搴曠汗鍜屾澘鑹蹭竴璧?*鐢昏繘鍒嗗潡缂撳瓨**锛堣 Overlay.DrawBoardPattern锛夛紝
    // 鎵€浠ユ粴鍔ㄣ€佺炕椤点€佸啓瀛楅兘涓嶈姳棰濆鐨勯挶锛涗唬浠峰彧鏈?鎹㈠簳绾?鎹㈡澘鑹叉椂鏁村眰閲嶉摵涓€娆?锛?    // 鍜屾崲鏉胯壊鏈潵灏辨槸鍚屼竴浠朵簨銆?
    /// <summary>鐧芥澘搴曠汗锛? = 鏃狅紝1 = 鏂规牸锛? = 妯嚎銆?/summary>
    internal int BoardPattern;

    /// <summary>
    /// **鐧芥澘鐨勪笉閫忔槑搴?*锛堢敤鎴?2026-09-17锛?澧炲姞涓€涓€忔槑搴︾殑鎷栧姩鍔熻兘锛岃繖鏍峰彲浠ユ壒娉ㄧ殑鏃跺€?    /// 闅愮害鐪嬭涓嬮潰鐨勯鐩紝浣嗘槸涔﹀啓鏈夊共鍑€"锛夈€?    ///
    /// 1 = 瀹炲績鏉块潰锛堢幇鍦ㄨ繖鏍凤級锛涜皟灏?鈫?鏉块潰鍗婇€忔槑锛屼笅闈㈢殑 PPT / 棰樼洰闅愮害閫忓嚭鏉ャ€?    /// **鍏抽敭鏄?涔﹀啓骞插噣"**锛氬崐閫忔槑鍙綔鐢ㄤ簬**搴曡壊**鈥斺€斿簳鑹叉槸鐢昏繘鍒嗗潡缂撳瓨鐨勭涓€灞傦紝
    /// 绗旇抗鐢诲湪瀹冧笂闈€佹湰韬笉閫忔槑锛屾墍浠ュ瓧鐓ф牱鏄疄鐨勶紙鑷閲屼笓闂ㄩ獙杩欎竴鏉★級銆?    /// </summary>
    internal float BoardOpacity = 1f;
    /// <summary>鍙皟鑼冨洿锛?.35 鍐嶄綆灏辩湅涓嶆竻鑷繁鍐欑殑瀛椾簡锛?.0 = 瀹炲績銆?/summary>
    internal const float BoardOpacityMin = 0.35f, BoardOpacityMax = 1f;

    internal void SetBoardOpacityFromUi(float v)
    {
        v = Math.Clamp(v, BoardOpacityMin, BoardOpacityMax);
        if (MathF.Abs(BoardOpacity - v) < 0.005f) return;
        BoardOpacity = v;
        // 鍜屾崲鏉胯壊 / 鎹㈠簳绾逛竴鏍凤細搴曡壊鏄敾杩涘垎鍧楃紦瀛樼殑锛屾墍浠ユ墍鏈夊潡閮借繃鏈熶簡
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }
    /// <summary>搴曠汗闂磋窛锛堥€昏緫鍍忕礌锛夈€侷nkClass 鐨勮寖鍥存槸 16锝?40锛屾垜浠寜閫昏緫鍍忕礌瀛樸€?/summary>
    internal float BoardPatternStepLogical = 40f;

    /// <summary>搴曠汗闂磋窛锛堢敾甯冨儚绱?= 鐗╃悊鍍忕礌锛夈€?/summary>
    internal float BoardPatternStepPx => Math.Clamp(BoardPatternStepLogical, 8f, 240f) * DpiScale;

    internal void SetBoardPatternFromUi(int pattern, float stepLogical)
    {
        pattern = Math.Clamp(pattern, 0, 2);
        stepLogical = Math.Clamp(stepLogical, 8f, 240f);
        if (BoardPattern == pattern && MathF.Abs(BoardPatternStepLogical - stepLogical) < 0.01f) return;
        BoardPattern = pattern;
        BoardPatternStepLogical = stepLogical;
        // 鍜屾崲鏉胯壊涓€鏍凤細搴曠汗鏄?*鐢昏繘鍒嗗潡缂撳瓨**鐨勶紝鎵€浠ユ墍鏈夊潡閮借繃鏈熶簡
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetCaptureHideInkFromUi(bool hideInk)
    {
        if (CaptureHideInk == hideInk) return;
        CaptureHideInk = hideInk;
        NotifyUiStateChanged();
    }

    /// <summary>寮曟搸鍏ュ彛锛氳繘鍏ユ埅鍥惧彇鏅紙闈㈡澘涓婄偣妯″紡娈佃蛋瀹冿紝瑙?IEngineCommands.EnterCapture锛夈€?/summary>
    internal void BeginCaptureModeFromUi()
    {
        ExitReplayForEdit("鎴浘");
        BeginCaptureMode();
    }

    /// <summary>Pen width presets, in logical pixels. Cycled with Ctrl+Alt+W
    /// until there is a proper on-screen control for it.
    /// 鏈€缁嗘。 2026-09-27 浠?1.5 闄嶅埌 **1**锛堢敤鎴凤細"鐢荤瑪鐨勬渶灏忕瑪瀹借鎴?1 鍙互鍚楋紵"鈥斺€?    /// 1.5 鍐欑粏瀛椼€佺敾鍧愭爣杞村埢搴︽椂杩樻槸鍋忕矖锛夈€?    /// **2026-10-09 鏅氶棿鍐嶆敼**锛堢敤鎴凤細"鎶?1 鍙峰埌 10 鍙疯繖涓寖鍥存嬁鍑烘潵锛岄棿璺濆鎴愪袱鍊?    /// 锛堟瘮濡?1 鍙峰拰 2 鍙蜂腑闂村啀鍔犱竴妗ｏ級锛涜秴杩?20 鍙蜂互鍚庝繚鎸?20 鎴?40 鍗冲彲"锛夛細
    /// **1/1.5/2/2.5/3/3.5/4/5/6/7/8/9/10/20/40**锛?5 妗ｏ級鈥斺€?    /// 1~10 娈垫彃婊′腑鐐癸紙甯哥敤鐨勫湴鏂规洿缁嗭級锛?0 浠ヤ笂鍙暀 20 / 40 涓や釜绮楁。銆?    /// 榛樿妗ｄ粛鏄?3锛堟暟鍊?3锛岀幇涓嬫爣 4锛夈€傛粦鏉℃嫋鍔?*鍚搁檮鍒版渶杩戞。**锛堜笉鍐嶆湁涓棿鍊笺€佷篃涓嶇敾鐐癸級銆?    /// 鐣岄潰閭ｄ唤瀵瑰簲琛ㄥ湪 `FullUi.PenWidthGrades`锛岄潬 --paneltest 閫愰」姣斿鍗′綇銆?/summary>
    internal static readonly float[] WidthPresets = { 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 20f, 40f };
    /// <summary>涓婁竴浠ｆ。浣嶈〃锛?026-10-09 鐧藉ぉ锛? 妗ｏ級锛氬彧涓?*杩佺Щ鑰侀厤缃?*鑰岀暀銆?    /// 鑰侀厤缃?`w2.pen` 瀛樼殑鏄偅寮犺〃鐨?绗嚑妗?鈥斺€旂洿鎺ユ寜鏂拌〃璇讳細閿欎綅锛?    /// 鍏堣繃杩欏紶琛ㄥ彇鍥炴暟鍊笺€佸啀鍚搁檮杩涙柊琛紙16 / 24 涓嶅湪鏂拌〃锛屽惛闄勫埌鏈€杩戠殑 20锛夈€?/summary>
    internal static readonly float[] Legacy9WidthPresets = { 1f, 2f, 3f, 4f, 6f, 8f, 10f, 16f, 24f };
    /// <summary>鏇磋€佺殑涓€浠ｏ紙2026-09-27 ~ 2026-10-09 鐧藉ぉ鍓嶏紝6 妗ｏ級锛氳縼绉?`w.pen` 鐢ㄣ€?    /// 鑰侀厤缃?`w.pen` 瀛樼殑鏄?绗嚑妗?锛岀洿鎺ユ寜鏂拌〃璇讳細鎶?6 璇绘垚 2鈥斺€斿厛杩囪繖寮犺〃鍙栧洖鏁板€硷紝
    /// 鍐嶅惛闄勫洖鏂拌〃锛堣€佽〃姣忎釜鍊奸兘鍦ㄦ柊琛ㄩ噷锛屽惛闄勬棤鎹燂級銆?/summary>
    internal static readonly float[] LegacyWidthPresets = { 1f, 3f, 6f, 10f, 16f, 24f };
    internal int WidthPresetIndex = 4;            // = 3f锛堥粯璁ょ矖缁嗭級

    /// <summary>
    /// 姣忕宸ュ叿鍚勮嚜鐨勭矖缁嗘。浣嶃€?*绗斿拰鑽у厜绗旂殑妗ｄ綅涓嶆槸涓€鍥炰簨**锛氳崸鍏夌瑪鏄?娑備竴澶ф潯"锛?    /// 1 鍍忕礌杩欑妗ｄ綅瀵瑰畠娌℃剰涔夛紱婵€鍏夋洿灏忋€傚叡鐢ㄤ竴寮犺〃鐨勭粨鏋滃氨鏄?    /// "閫変簡鑽у厜绗旀寜 Ctrl+Alt+6 娌″弽搴?锛堝畠鏀圭殑鏄瑪瀹斤級鈥斺€斿疄娴嬪氨鏄繖涓?bug銆?    /// </summary>
    /// <summary>鑽у厜绗旀。浣嶃€?026-10-09 鏅氬悓绗斾竴璧?闂磋窛瀹芥垚涓ゅ€?锛?/18/32 鈫?**8/13/18/25/32**锛? 妗ｏ級銆?    /// 榛樿妗ｄ粛鏄?18锛堢幇涓嬫爣 2锛夈€?/summary>
    internal static readonly float[] HighlighterWidthPresets = { 8f, 13f, 18f, 25f, 32f };
    /// <summary>
    /// 婵€鍏夌殑妗ｄ綅銆傛部闈╋細4/8/14锛堝垵鐗堬級鈫?8/14/22锛?026-09-27 鐧藉ぉ"榛樿妗ｇ矖涓€鐐?锛?    /// 鈫?4/8/14/22锛?026-09-27 鏅氾級鈫?**4/6/8/11/14/18/22**锛?026-10-09 鏅氬悓绗斾竴璧?    /// "闂磋窛瀹芥垚涓ゅ€?锛夈€?*榛樿妗ｄ粛鏄?8**锛堢幇涓嬫爣 2锛夈€?    /// </summary>
    internal static readonly float[] LaserWidthPresets = { 4f, 6f, 8f, 11f, 14f, 18f, 22f };
    internal int HighlighterWidthIndex = 2;
    internal int LaserWidthIndex = 2;

    /// <summary>褰撳墠宸ュ叿鐨勭矖缁嗭紙閫昏緫鍍忕礌锛夈€傝惤鐐瑰弽棣堛€佺晫闈㈢姸鎬侀兘璇诲畠銆?/summary>
    internal float CurrentToolWidthLogical => Tool switch
    {
        Tool.Highlighter => HighlighterWidthLogical,
        Tool.Laser => LaserWidthLogical,
        Tool.PixelEraser => PixelEraserWidthLogical,
        Tool.Eraser => EraserRadiusLogical,
        _ => PenWidthLogical,
    };

    internal bool PointerInside;
    internal float PointerX, PointerY;
    /// <summary>
    /// 鏈€鍚庝竴娆℃寚閽堟秷鎭潵鑷粈涔堣澶囷紙榧犳爣 / 绗?/ 瑙︽懜锛夈€?*鎮仠鐨勭Щ鍔ㄤ篃瑕佹洿鏂?*鈥斺€?    /// 鎸囬拡褰㈢姸鐩存帴鍙栧喅浜庡畠锛岃 <see cref="ComputeCursorKind"/>銆?    /// </summary>
    internal uint LastPointerType = Native.PT_MOUSE;

    /// <summary>
    /// 褰撳墠杩欐敮绗旂殑**绗斿皷鏄笉鏄氨鍦ㄥ睆骞曚笂**锛堣Е鎽稿睆鑷甫鐨勭瑪 = true锛涘鎺ユ墜鍐欐澘 = false锛夈€?    ///
    /// 涓轰粈涔堣鍒嗚繖涓€鍒€锛?026-09-27 鐢ㄦ埛鎶ョ殑 bug锛夛細"鎵嬪啓绗旇惤绗斿悗涓嶇敾钀界偣銆佹妸绯荤粺鍏夋爣涔?    /// 钘忚捣鏉?杩欐潯瑙勫垯鐨勪緷鎹槸"**绗斿皷鏈韩灏辨槸钀界偣**"銆傚彲鎵嬪啓鏉跨殑绗斿皷鍦?*鏉垮瓙涓?*銆?    /// 鏍规湰涓嶅湪灞忓箷閲屸€斺€斿啓瀛楁椂灞忓箷涓婁粈涔堥兘娌℃湁锛岀敤鎴风殑鍘熻瘽鏄?鎰熻涓嶅お娴佺晠銆佹湁鐐瑰鎬?銆?    /// 鎵€浠ヨ繖鏉¤鍒?*鍙瑙︽懜灞忚嚜甯︾殑绗旀垚绔?*锛涢紶鏍囥€佹墜鍐欐澘锛堜互鍙婅涓嶅嚭璁惧鐨勭瑪锛?    /// 涓€寰嬬粰"鏂滅瑪"鍏夋爣锛岃瀹冧竴璺窡鐫€钀界偣璧般€?    ///
    /// 璁惧鍙ユ焺 鈫?缁撹鍙煡涓€娆★紙鍚屼竴鏀瑪涓嶄細涓€浼氬効鍦ㄥ睆涓婁竴浼氬効鍦ㄦ澘瀛愪笂锛夛紝缂撳瓨浣忥紱
    /// 鏌ヤ笉鍒帮紙鑰侀┍鍔?/ 鍚堟垚璁惧锛夋寜 false 璧?= "缁欏厜鏍?鈥斺€?*瀹佸彲澶氱粰锛屼笉鑳芥病鏈?*銆?    /// </summary>
    internal bool PenDeviceOnScreen;

    /// <summary>璁惧鍙ユ焺 鈫?"绗斿皷鍦ㄥ睆骞曚笂鍚?鐨勭紦瀛橈紙瑙?<see cref="PenDeviceOnScreen"/>锛夈€?/summary>
    private readonly Dictionary<IntPtr, bool> _penOnScreenCache = new();

    /// <summary>鏌ヤ竴娆?杩欎釜璁惧鍙ユ焺鐨勭瑪灏栧湪涓嶅湪灞忓箷涓?锛岀粨鏋滆繘缂撳瓨銆?/summary>
    private bool QueryPenOnScreen(IntPtr sourceDevice)
    {
        if (sourceDevice == IntPtr.Zero) return false;
        if (_penOnScreenCache.TryGetValue(sourceDevice, out bool known)) return known;
        bool onScreen = false;
        if (Native.GetPointerDevice(sourceDevice, out var info))
            onScreen = info.pointerDeviceType == Native.POINTER_DEVICE_TYPE_INTEGRATED_PEN;
        _penOnScreenCache[sourceDevice] = onScreen;
        return onScreen;
    }

    /// <summary>
    /// 绗斿伐鍏锋鍒?*璇ヤ笉璇ョ粰鏂滅瑪鍏夋爣**锛堣€屼笉鏄棌璧锋潵浜ょ粰鑷粯钀界偣锛夈€?    /// 瑙勫垯锛氬彧鏈?绗斿皷灏卞湪灞忓箷涓?鐨勯偅鏀瑪鎵嶈棌锛堢瑪灏栧嵆钀界偣锛夛紱榧犳爣銆佹墜鍐欐澘銆佽涓嶅嚭鐨勭瑪
    /// 閮界粰鏂滅瑪锛堢郴缁?IDC_PEN锛夈€傝Е鎽歌疆涓嶅埌瀹冿紙<see cref="ComputeCursorKind"/> 寮€澶村氨杩斿洖浜嗭級銆?    /// </summary>
    internal bool PenShowsCursor
        => LastPointerType != Native.PT_PEN || !PenDeviceOnScreen;

    /// <summary>
    /// 鏈€鍚庝竴鏉℃寚閽堟秷鎭噷**绗旀槸涓嶆槸鍊掕繃鏉ユ嬁鐨?*锛堢瑪灏炬鐨紝PEN_FLAG_INVERTED / ERASER锛夈€?    ///
    /// 涓轰粈涔堣鐣欒繖涓姸鎬侊紙鐢ㄦ埛 2026-09-27 鎶ョ殑锛夛細鍊掓寔鐨勭瑪**鐢荤殑鏃跺€?*鏃╁氨鎸夋鐨蛋浜?    /// 锛坄OnPointerDown` / `OnPointerMove` 閲岄偅鍙?`inverted ? Tool.Eraser : Tool`锛夛紝
    /// 鍞嫭**鍏夋爣娌¤窡鐫€鍙?*鈥斺€旂瑪灏炬偓鍋滃湪鐢诲竷涓婃椂锛屽睆骞曚笂杩樻槸鏂滅瑪 / 绗斿皷鐜紝
    /// 鑰佸笀鐪嬩笉鍑?杩欎竴澶存槸姗＄毊"锛屼篃鐪嬩笉鍑?浼氭摝鎺夊澶т竴鍧?銆?    ///
    /// 鐢熷懡鍛ㄦ湡璺熺潃 <see cref="LastPointerType"/> 璧帮細璇绘寚閽堟秷鎭椂涓€璧锋洿鏂帮紙瑙?    /// <see cref="ReadPointer"/>锛夛紱榧犳爣 / 瑙︽懜姘歌繙璇讳笉鍒板€掓寔锛堝畠浠病鏈夌瑪灏撅級锛?    /// 浜庢槸姣忔潯娑堟伅閮戒細鎶婂畠澶嶄綅鎴?false銆?    /// </summary>
    internal bool LastPointerInverted;

    /// <summary>
    /// 姝ゅ埢**瀹為檯鍦ㄧ敤鐨勫伐鍏?*锛氱瑪鍊掕繃鏉ユ嬁鏃跺氨鏄鐨紝鍏朵綑鎯呭喌灏辨槸 <see cref="Tool"/>銆?    ///
    /// 鍏夋爣杩欎竴鏃忥紙<see cref="ToolCursorKind"/> / <see cref="DrawnCursor"/> /
    /// <see cref="CursorOuterRadius"/>锛夊繀椤婚棶瀹冭€屼笉鏄洿鎺ラ棶 Tool鈥斺€?*鍒ゆ嵁鍙啓涓€澶?*锛?    /// 涓嶇劧"鍊掓寔"杩欎釜鍒ゆ柇灏变細鏁ｅ埌濂藉嚑澶勶紙鏈」鐩负杩欑鏁ｈ惤鏍借繃鍥涙锛?    /// 瑙?鏋舵瀯-鍒嗗眰涓庤鍒?md 浜?7锛夈€?    /// </summary>
    internal Tool EffectiveTool => LastPointerInverted ? Tool.Eraser : Tool;

    /// <summary>
    /// 宸︿笂瑙掗偅涓粦鑹叉€ц兘闈㈡澘锛團PS/寤舵椂锛夈€?    ///
    /// **榛樿鍏?*锛堢敤鎴?2026-09-30锛氭櫘閫氱敤鎴蜂笉璇ョ湅鍒板畠锛屽彇娑堥粯璁ゅ紑锛夈€?    /// 瑕佺湅鏁板瓧鏃剁敤鍛戒护琛?`--hud` 寮€锛堣嚜妫€/鍩哄噯閭ｆ壒妯″紡鍙︽湁涓€濂楄鍒欙細
    /// 瀹冧滑榛樿涔熶笉寮€锛岄櫎闈炴樉寮?`--hud`鈥斺€旈潰鏉夸細鐩栦綇灞忓箷涓婄殑澧紝鏁板ⅷ鐨勭敤渚嬩細鍋囧け璐ワ級銆?    /// 蹇嵎閿凡缁忔病鏈変簡锛堝師 Ctrl+I 宸插垹锛夈€?    /// </summary>
    internal bool ShowHud = false;

    /// <summary>
    /// 姗＄毊鐨勫疄娴嬮噰闆嗭紙"鎵嬫祴鍙? --eraserlab锛夈€?*骞虫椂鏄?null**锛屾墍鏈変笂鎶ョ偣閮芥槸 `?.`锛?    /// 姝ｅ父浣跨敤涓€鍒嗛挶寮€閿€閮戒笉澶氳姳銆傝 <see cref="EraserTelemetry"/>銆?    /// </summary>
    internal EraserTelemetry EraserTelemetry;

    /// <summary>
    /// **澶嶅埗鎷栨嫿妯″紡**锛氱偣浜嗘搷浣滄潯鐨勫鍒舵寜閽箣鍚庤繘鍏ワ紝姝ゆ椂鎸変綇閫変腑鍐呭鎷栧姩 = **鎷栧嚭涓€浠藉壇鏈?*
    /// 锛堝師浠朵笉鍔級锛屽彲浠ヨ繛鐫€鎷栧浠斤紱鍐嶇偣鎸夐挳 / 鐐圭┖鐧?/ 鎹㈠伐鍏烽€€鍑恒€?    ///
    /// 璇箟鎶?InkClass锛堥偅杈瑰凡缁忚俯杩囦竴杞潙锛夛細鐐瑰浘鏍?*鍙繘妯″紡**锛屼笉鏄?鐐逛竴涓嬪師鍦板厠闅嗕竴浠?
    /// 鈥斺€斿悗鑰呭壇鏈浐瀹氬亸绉?24px銆佽惤鐐逛笉鍙帶锛屽凡缁忓簾寮冦€?    /// </summary>
    internal bool CopyDragArmed;

    /// <summary>澶嶅埗鎷栨嫿杩欎竴娆℃嫋鍔ㄩ噷"鎻掑叆鍓湰"閭ｆ潯寰呮彁浜ょ殑鍔ㄤ綔锛堟澗鎵嬫椂鍜屼綅绉诲悎鎴愪竴姝ワ級銆?/summary>
    private AddStrokesAction _pendingClone;

    /// <summary>
    /// 姗＄毊鐨勮惤鐐瑰弽棣堜箣澶栵紝**鍚屾椂鏄剧ず绯荤粺绠ご**锛堥粯璁よ棌璧锋潵锛屽彧鐢绘垜浠嚜宸辩殑鍦?鏂瑰潡锛夈€?    ///
    /// 鐣欒繖涓紑鍏虫槸涓轰簡鍋?A/B锛氳嚜缁樼殑鏂瑰潡鍏夋爣鏈川鏄?鎴戜滑鐢荤殑涓€甯?锛屽ぉ鐢熸瘮绯荤粺鍏夋爣鏅?    /// 涓€涓埛鏂板懆鏈燂紙16.7 ms锛夛紱绯荤粺绠ご鐢诲湪纭欢鍏夋爣骞抽潰涓婏紝鍑犱箮娌℃湁寤惰繜銆傜敤鎴疯
    /// "涓嶈窡鎵?鏃讹紝鎶婁袱涓兘鏄剧ず鍑烘潵鈥斺€旂澶磋窡鎵嬨€佹柟鍧楀舰鐘跺噯锛屽摢绉嶆洿鑸掓湇鐢辩敤鎴疯浜嗙畻銆?    /// </summary>
    internal bool EraserKeepsSystemCursor;
    internal string HudText = "";

    /// <summary>
    /// **瑙︾偣璇婃柇**锛?.3.3锛夛細缁欏鏍″ぇ灞忛噺"鍚屾椂鎶ュ嚑涓Е鐐?/ 鎶ヤ笉鎶ユ帴瑙﹂潰绉?鐨勫皬娴眰銆?    ///
    /// 涓轰粈涔堣瀹冿細鎵嬪娍鏂规锛堣 璋冪爺-瑙︽懜鎵嬪娍-瀛︽牎澶у睆.md锛夎兘涓嶈兘钀藉湴锛屽叏鐪嬭繖鍧楀睆
    /// 鈶犱竴娆¤兘涓嶈兘鎶?鈮? 涓Е鐐广€佲憽鎺ヨЕ闈㈢Н鎶ヤ笉鎶モ€斺€旇€岃繖涓や欢浜?*鍙湁鐪熸満鎵嶈兘閲?*
    ///锛堟棤瑙︽懜灞忕殑鏈哄櫒涓婂悎鎴愭敞鍏ヤ笉鍙潬锛氶殧澹?InkClass 瀹炴祴 0 鍛戒腑锛屾枃妗ｅ啓鏄?涓嶈鎵胯鏈満娉ㄥ叆"锛夈€?    /// 榛樿鍏炽€乣--touchhud` 寮€銆侀檺棰戝埛鏂帮紙鐓т粬浠殑纭害鏉?鈮?00ms锛夈€佷笉钀界洏銆?    /// </summary>
    internal bool TouchHud;
    internal string TouchHudText = "";
    internal int TouchHudMax;                    // 瑙佽繃鐨勬渶澶у悓鏃惰Е鐐规暟
    internal bool TouchHudSawArea;               // 瑙佽繃闈為浂鎺ヨЕ闈㈢Н鍚?    internal int TouchHudNow => _touchDiagIds.Count;
    private readonly HashSet<uint> _touchDiagIds = new();
    private float _touchDiagMaxW, _touchDiagMaxH;
    private double _touchDiagNextMs;
    private string _touchDiagLast = "鈥?;
    internal bool MarqueeActive;

    /// <summary>
    /// 妗嗛€夊伐鍏蜂笅鎷栫┖鐧藉鏃剁殑**閫夋嫨鏂瑰紡**锛氱煩褰㈡ 鈫?鑷敱濂楃储锛坄Ctrl+Alt+9` 鍒囷級銆?    ///
    /// 涓轰粈涔堜笉缁欑晫闈㈡寜閽細宸ュ叿鏉′笂宸茬粡鎸や簡鍏宸ュ叿锛岃€?閫夋嫨鏂瑰紡"鏄釜**浣庨璁剧疆**
    /// 锛堜竴鑺傝鍙兘涓€娆￠兘涓嶅垏锛夈€備竴涓敭 + 闈㈡澘涓婁竴琛屽瓧锛堣 BuildHudText锛夎冻澶燂紝
    /// 杩樼渷鎺変竴娆?鍥炬爣鍒板簳鐢诲摢涓?鐨勫缇庝簤璁恒€?*涓嶈惤鐩?*锛堝拰宸ュ叿灏哄涓€鏍锋椿鍦ㄥ唴瀛橀噷锛夈€?    ///
    /// 涓ょ鎵嬪娍鐨勫垽鎹?*鏁呮剰涓嶄竴鏍?*锛堣 InkDocument.ApplyMarquee / ApplyLasso锛夛細
    /// 鎷栫煩褰紳"鎴戞浣忚繖涓€鐗?锛堢鍒板氨绠楋級锛岀敾涓€鍦堬紳"鎴戞妸杩欎竴鏉″湀璧锋潵浜?锛?0% 鍦ㄥ唴锛夈€?    /// </summary>
    internal SelectMode SelMode = SelectMode.Rect;

    /// <summary>濂楃储鎷栧嚭鏉ョ殑璺緞锛?*鐢诲竷鍧愭爣**锛岄棿璺?鈮?<see cref="LassoStepLogical"/>锛夈€?/summary>
    internal readonly List<Vector2> LassoPath = new();

    /// <summary>
    /// 鎸囬拡**姝ゅ埢**鐨勪綅缃紙鐢诲竷鍧愭爣锛夈€傚绱㈢殑璺緞鏄娊绋€杩囩殑锛堥棿璺?鈮? 閫昏緫鍍忕礌锛夛紝
    /// 鍙敾璺緞鐨勮瘽绾垮ご浼氭瘮鎸囬拡鎱㈠崐鎷嶏紙200% 缂╂斁涓嬪樊 6 涓墿鐞嗗儚绱狅紝鐪嬪緱鍑烘潵锛夈€?    /// 鎵€浠ラ瑙堢敾"璺緞 + 杩欎竴涓偣"锛屾墜鎰熸墠鏄嚎澶村挰鐫€鎸囬拡璧般€?    /// </summary>
    internal Vector2 LassoLive;

    /// <summary>濂楃储璺緞鐨勯噰鏍烽棿璺濓紙閫昏緫鍍忕礌锛夛細澶瘑娌＄敤锛岃繕浼氭妸鐐规暟鎾戝埌鍑犵櫨銆?/summary>
    internal const float LassoStepLogical = 3f;

    /// <summary>
    /// 妗嗛€夌煩褰?/ 鎴浘鍙栨櫙妗嗙殑**閿氱偣**锛堟寜涓嬮偅涓€鐐癸紝鐢诲竷鍧愭爣锛夈€?    ///
    /// 鐭╁舰蹇呴』鎸?閿氱偣 鈫?褰撳墠鐐?鏉ョ畻锛?*涓嶈兘**鍦ㄦ瘡娆＄Щ鍔ㄦ椂绱Н min/max锛?    /// 绱Н鐨勮瘽鎸囬拡寰€鍥炶蛋妗嗕笉浼氳窡鐫€缂╋紝灞忓箷涓婄湅鍒扮殑鏄?鎵繃鐨勬渶澶ц寖鍥?锛?    /// 鎵嬫劅灏辨槸"涓嶈窡鎵?锛堢敤鎴?2026-09-15 瀹炴祴鍙嶉锛?浜哄鏄€変腑璧峰鐐逛互鍚庯紝
    /// 榧犳爣涓嶇鎬庝箞鍙橈紝瀹冮兘鏄竴涓彲鍙樼殑鐭╁舰锛屼絾鏄垜浠繖涓技涔庝笉鏄?锛夈€?    /// </summary>
    private float _mqAnchorX, _mqAnchorY;

    /// <summary>鎴浘鍙栨櫙妗嗙殑閿氱偣锛堝悓涓婏紝涓ゅ鐢ㄧ殑鏄悓涓€濂楃畻娉曪級銆?/summary>
    private float _capAnchorX, _capAnchorY;

    /// <summary>鎸変笅閭ｄ竴鍒荤殑淇グ閿紙濂楃储鐨?Shift 鍔犻€?/ Alt 鍑忛€夛級銆?/summary>
    private bool _lassoAdditive, _lassoSubtractive;

    // ---- 閫夋嫨鎵嬪娍锛堟閫夊伐鍏?= 閫夋嫨宸ュ叿锛?--------------------------------
    // 鍥涚鎯呭舰鍦ㄨ繖閲屽垎娴侊細鐐规搷浣滄潯鎸夐挳 / 鎷栨墜鏌?/ 鍦ㄩ€変腑鑼冨洿閲屾暣浣撴嫋 / 绌虹櫧澶勯噸鏂版閫夈€?    internal bool SelDragging;
    internal int SelBarHover = -1;          // 鎮仠鐨勬寜閽紝-1 = 鏃狅紙缁欐覆鏌撶敤锛?
    /// <summary>
    /// 鎿嶄綔鏉?*鏀惰捣**浜嗗悧锛堢敤鎴?2026-09-16 鏇存锛氭潯棣栭偅涓?鉁?涓嶆槸"鍙栨秷閫夋嫨"锛屾槸"鏀惰捣"锛夈€?    /// 鏀惰捣鍚庢暣鏉′笉鐢伙紝鍙湪妗嗕笅鏂圭暀涓€涓皬鍦嗛挳锛岀偣瀹冨睍寮€鈥斺€旀姇褰辨椂涓嶆尅鏉夸功銆?    /// 娉ㄦ剰鏀惰捣鐨勬槸**鏉?*锛屾涓庢墜鏌勭収鏃э紙鍙傝€冨疄鐜颁篃鏄繖鏍凤級銆?    /// </summary>
    internal bool SelBarCollapsed;

    /// <summary>
    /// **鐢诲畬鑷姩閫変腑閭ｄ釜妗?*鐜板湪鎶婃搷浣滄潯"鏀惰捣鏉?鐢伙紙鍙寕涓€棰楀渾閽?锛?妗?锛?鎵嬫焺 锛?鏃嬭浆鏌勶級銆?    ///
    /// 鐢ㄦ埛 2026-09-22 涓婃墜涔嬪悗瀹氱殑鍙ｅ緞锛屽師璇濅袱鍙ワ細
    /// "鍒氶€変腑浠ュ悗锛屼娇鐢ㄦ敹璧锋潵鐨勯偅涓舰鎬侊紝鐒跺悗鎿嶄綔浜嗚繖涓姸鎬佹爮浠ュ悗锛屽氨鎸夌収甯歌閫昏緫璧板惂"銆?    /// "**鎴戠幇鍦ㄦ槸鍙鏀剁缉锛屽叾浠栫殑閮戒笉鍙?*"銆?    ///
    /// 鎵€浠ヨ鍒嗘竻杩欎釜鏍囧織**绠′粈涔堛€佷笉绠′粈涔?*锛?    ///   路 **绠?*锛氭搷浣滄潯閭ｄ竴鍧楃敾鎴?涓€棰楀渾閽?杩樻槸"涓€鏁存潯涔濇牸"锛堣 <see cref="BarDrawnCollapsed"/>锛夈€?    ///     鍒氱敾瀹屾憡寮€涓€鏁存潯锛岃鎰熷お閲嶃€侀偅寮犲崱鐗囧張姝ｅソ鍘嬪湪鍥惧舰涓嬮潰锛涚偣涓€涓嬪渾閽氨鎽婂紑銆?    ///   路 **涓嶇**锛氭嫋鍔?/ 鎷夋墜鏌?/ 鏃嬭浆 / 鐐规潯 鈥斺€?杩欎簺**鍜屾閫夊伐鍏蜂笅涓€涓牱**
    ///    锛坄AutoSelectionZoneAt` 鍘嬫牴涓嶈杩欎釜鏍囧織锛涚涓€鐗堣浜嗭紝缁撴灉灏辨槸鐢ㄦ埛鎶ョ殑
    ///     "涓嶈兘鎷栧姩浣嶇疆锛屽彧鑳芥媺浼哥缉鏀?锛夈€?    ///
    /// 瀹冨拰鐢ㄦ埛鑷繁閭ｄ釜"鏀惰捣"鍋忓ソ锛?see cref="SelBarCollapsed"/>锛夋槸**涓よ矾**锛?    /// 鐐瑰紑鍦嗛挳鏃朵袱涓竴璧锋竻锛堜笉鐒朵細"鐐逛簡鍦嗛挳杩樻槸鍦嗛挳"锛夛紱杩欎釜鏍囧織鏈韩涓嶅啓鍥炲亸濂姐€?    /// </summary>
    private bool _autoSelCollapsed;

    /// <summary>鐢诲畬鑷姩閫変腑閭ｄ釜妗嗙幇鍦ㄦ槸涓嶆槸"鏀惰捣鏉?鐨勫舰鎬侊紙瑙?<see cref="_autoSelCollapsed"/>锛夈€?/summary>
    internal bool AutoSelCollapsed => _autoSelCollapsed;

    // =====================================================================
    //  鍋滈】鎴愬瀷锛堟墜鍐欎竴绗斿仠涓€涓?鈫?鍙樻垚鍥惧舰锛夈€傝鏍间笌瀹炴祴鏁版嵁瑙?璁″垝-鍥惧舰宸ュ叿.md 搂鍥涘崄浜屻€?    // =====================================================================

    /// <summary>寮€鍏筹紙**榛樿寮€**锛岀敤鎴?2026-09-23 瀹氱殑锛?鍥犱负鏄仠椤垮彉锛屾墍浠ラ粯璁ゅ紑"锛夈€?    /// 鍋忓ソ鍙啓"鍏宠繃鐨?閭ｄ竴浠斤紙鐓?`InkSettings` 鐨勮鐭╋細鍜岄粯璁や竴鏍峰氨涓嶅啓锛夈€?/summary>
    internal bool DwellShapeEnabled = true;

    /// <summary>鍋滈】鐘舵€佹満鏈綋锛?鍋滀簡澶氫箙"閭ｄ欢浜嬪叏鍦ㄥ畠閲岄潰锛岃 <see cref="DwellAssist"/>锛夈€?/summary>
    private readonly DwellAssist _dwell = new();

    /// <summary>armed 鏃惰鎹笅鏉ョ殑**鎵嬬粯鍘熻抗**锛堟挙閿€瑕佹妸瀹冩斁鍥炴枃妗ｏ紝瑙?`DwellShapeAction`锛夈€?/summary>
    private Stroke _dwellInk;

    /// <summary>杩欎竴绗旀彁浜ょ殑鏄笉鏄?鍋滈】鍙樺嚭鏉ョ殑鍥惧舰"锛堝喅瀹氾細鑷姩閫変腑 锛?鎾ら攢璧版浛鎹級銆?/summary>
    private bool _dwellCommitted;

    /// <summary>杞瀹氭椂鍣ㄥ紑鐫€娌℃湁锛堝彧鍦ㄧ瑪鐢昏繘琛屼腑寮€锛岃 <see cref="DwellAssist.TickMs"/>锛夈€?/summary>
    private bool _dwellTimerOn;

    /// <summary>瀹氭椂鍣?id锛? 鏄?缃《"閭ｉ锛堝埆浜虹殑锛夛紝2 鏄仠椤挎垚鍨嬭繖棰椼€?/summary>
    private const int DwellTimerId = 2;

    /// <summary>
    /// **杩欎竴娆＄殑鑷姩閫変腑鏄?鍋滈】鍙樺嚭鏉ョ殑"**锛堣€屼笉鏄浘褰㈠伐鍏风敾瀹岄€夌殑锛夈€?    ///
    /// 涓轰粈涔堣鍗曟湁涓€涓爣蹇楋細鍥惧舰宸ュ叿涓嬮偅涓嚜鍔ㄩ€変腑鐨勬锛屾槸鎸?褰撳墠宸ュ叿鏄笉鏄浘褰㈠伐鍏?
    /// 鏉ユ斁琛岀殑锛堣 `SelectionBarShown` / `SelectionInteractiveAt`锛夈€傝€屽仠椤挎垚鍨?*宸ュ叿杩樻槸绗?*
    /// 锛堢敤鎴锋墜閲岄偅鏀瑪娌″彉锛夛紝鎵€浠ュ彧鎸夊伐鍏峰垽鐨勮瘽锛岄偅涓浼?鐢诲緱鍑烘潵銆佺偣涓嶇潃"銆?    ///
    /// 瀹冨彧鍦ㄨ繖涓€涓潵婧愪笅涓虹湡锛氭閫?/ 鍥惧舰宸ュ叿鐢诲畬閮戒笉浼氱疆瀹冿紝**鎵€浠ョ瑪涓嬮潰涓嶄細鍑┖澶氬嚭浜や簰**銆?    /// 娓呭畠鐨勫湴鏂逛篃鍙湁涓€澶勶紙`ClearSelectionForNewContext`锛夛紝鍜?`_autoSelCollapsed` 鍚屼竴澶勩€?    /// </summary>
    private bool _dwellSelected;

    /// <summary>鍒氭妸"鍋滈】鍙樺嚭鏉ョ殑閫変腑妗?鏀惰捣鏉ョ殑閭ｄ竴涓嬧€斺€?*濡傛灉鍙槸鐐逛竴涓嬶紝涓嶈鐣欏ⅷ**
    ///锛堢敤鎴?2026-09-23 瀹氱殑锛?鐐瑰埆澶勫彇娑堥€変腑锛岃€屼笖涔熶笉浼氱暀澧ㄨ抗"锛夈€?    /// 瑙?<see cref="DwellTapLeaveNoInk"/>銆?/summary>
    private bool _dismissTapArmed;

    /// <summary>"鍙栨秷閫変腑閭ｄ竴鍑?鐨勫宸紙閫昏緫鍍忕礌锛夛細鏁存浣嶇Щ涓嶈秴杩囧畠灏辩畻"鐐逛竴涓嬶紝涓嶆槸鐢?銆?    /// 鍙?8锛氶紶鏍囩偣涓€涓嬮€氬父鍙 1~2 px锛屾墜鍐欑瑪鏇村皬锛涚湡鐢讳竴绗斾笉浼氬彧鏈?8 px銆?/summary>
    internal const float DwellTapSlopLogical = 8f;

    // ---- armed锛堝菇鐏垫湡锛?鎸変綇鎷栧姩"鐨勪袱浠朵簨锛?*鍙粰鐩寸嚎鐢?*锛岃 ArmedStrokeMove锛?-------

    /// <summary>
    /// 鎴愬瀷閭ｄ竴鍒荤殑**绗斿皷浣嶇疆**锛堢洿绾跨殑"璧锋姝诲尯"鍩哄噯锛夈€?    /// 鐢ㄩ€旇 <see cref="DwellDragSlopLogical"/>锛氱瑪灏栬繕鍦ㄨ繖鍎块檮杩戞椂锛岀嚎涓€涓儚绱犻兘涓嶆敼銆?    /// </summary>
    private Vector2 _dwellArmAnchor;

    /// <summary>鐩寸嚎锛?*閽変綇鐨勯偅涓€澶?*锛堟垚鍨嬫椂瀹氫笅鏉ワ紝鎷栧姩鍏ㄧ▼涓嶅彉锛夈€?    /// 涓轰粈涔堜笉鑳界洿鎺ユ嬁 `Points[0]`锛氳瘑鍒櫒缁欑殑绔偣鏄?鎷熷悎鏂瑰悜鐨勬姇褰辨瀬鍊?锛?    /// 鍝竴澶磋惤鍒?`Points[0]` 鏄笉瀹氱殑锛堣 <see cref="ShapeRecognize.TryLine"/>锛夈€?/summary>
    private Vector2 _dwellLinePin;

    /// <summary>**涓婁竴绗?鏀剁瑪"鐨勬椂鍒?*锛堝紩鎿庢椂閽燂紝姣锛夈€備袱绗旈厤瀵硅鐢ㄥ畠绠楁椂闂寸獥鍙?鈥斺€?    /// 鐢ㄦ埛 2026-09-25 瀹?5 绉掞紝2026-09-26 **鏀惧紑鍒?20 绉?*锛堣 <see cref="TwoBranchWindowMs"/>锛夈€?    /// 鍐欏湪 `EndStroke` 閲岋紙鎴睆閭ｆ潯鍒嗘敮涔嬪悗锛夛細鎴竴娆″睆涓嶈鎶婁袱绗旂殑绐楀彛鍐叉帀銆?/summary>
    private double _lastInkEndMs = double.MinValue;

    /// <summary>**涓ょ瑪鎴愬瀷**鏃?绗竴绗?閭ｄ釜瀵硅薄锛堢敤鎴?2026-09-25锛?鐢讳袱鏀氨鏄弻鏇茬嚎"锛夈€?    /// 闈?null = 鎶墜鎻愪氦鏃惰鎶?*涓婁竴绗斾篃鏀惰蛋**锛堣 `EndStroke` 閲岄偅娈碉級銆?    /// 璁ゅ嚭鏉ョ殑閭ｄ竴鍒婚拤浣忥紱`_dwellInk` 娓呭摢鍎垮畠灏辨竻鍝効锛堝彇娑堛€佸浣嶉兘瑕佽窡锛夈€?/summary>
    private Stroke _twoBranchPrev;
    private int _twoBranchIndex = -1;

    /// <summary>閰嶅璇婃柇鏃ュ織鐨?*闄愭祦**鏃跺埢锛坄TryTwoBranchPair` 姣?40ms 璧颁竴娆★紝涓嶉檺娴佷細鍒峰睆锛夈€?/summary>
    private double _pairLogMs = double.MinValue;

    /// <summary>
    /// **鏈€鍚庝竴娆?鍋滈】鎴愬瀷"鍑烘潵鐨勫璞?*锛屼互鍙?*瀹冨綋鍒濋偅涓€绗斿師杩?*锛堢敤鎴?2026-09-26 鎶?    /// "鐢诲嚑娆℃墠鎴愪竴娆?涔嬪悗鍔犵殑锛夈€?    ///
    /// 涓轰粈涔堥渶瑕侊細鐢诲弻鏇茬嚎鐨?*绗竴鏀?*鏃讹紝濡傛灉鏀剁瑪鍓嶆墜鍋滀簡涓€涓嬶紙400ms锛夛紝杩欎竴鏀氨**鍏堣
    /// 鍋滈】鏃舵垚浜嗕竴涓姏鐗╃嚎瀵硅薄** 鈥斺€?鑰屽璞＄殑 `Points` 鍙墿 2~3 涓畾涔夌偣锛?*鎷夸笉鍥炲師鏉?    /// 閭ｄ簺閲囨牱鐐?*锛屼簬鏄袱绗旈厤瀵?*姘歌繙閰嶄笉涓?*锛岀敤鎴风湅鍒扮殑灏辨槸"杩欐涓嶈銆佸啀鐢讳竴娆¤涓嶅畾琛?
    /// 锛堣涓嶈鍋滈偅涓€涓嬫槸闅忔満鐨勶級銆傚璞℃湰韬凡缁忓湪鏂囨。閲屼簡锛屾挙閿€鏍堜篃璁扮潃瀹冪殑鍘熻抗 鈥斺€?    /// 杩欓噷鍙槸**鍦ㄥ紩鎿庨噷鐣欎竴涓紩鐢?*锛岄厤瀵规椂鎷垮畠褰?绗竴鏀?鐢ㄣ€?    ///
    /// 鈿?鍙湪**寮曟搸**閲岀暀锛屼笉杩涙ā鍨嬨€佷笉杩涘瓨妗ｏ細瀹冩槸涓函绮圭殑杩愯鏈熶究鍒┿€?    /// 鈿?鐢ㄥ畠涔嬪墠蹇呴』纭 `prev == _lastDwellShape`锛堝紩鐢ㄦ瘮杈冿級鈥斺€?鐢ㄦ埛涓棿鐢讳簡鍒殑銆?    ///   鎴栨挙閿€鎺変簡锛宍Doc.Strokes[^1]` 灏变笉鍐嶆槸瀹冧簡锛岃嚜鍔ㄥけ鏁堛€?    /// </summary>
    private Stroke _lastDwellShape;
    private Stroke _lastDwellInk;

    /// <summary>
    /// **璇嗗埆璇婃柇鏃ュ織寮€鍏?*锛?026-09-26 鍔狅紝缁?鐪熸満涓婁负浠€涔堟病璁ゅ嚭鏉?鐣欑殑鍙ｅ瓙锛夈€?    ///
    /// 鐢辨潵锛氱敤鎴锋姤"妞渾鍜岀煩褰袱涓兘**甯稿父浠€涔堥兘涓嶈**"锛岃€?*鐪熸満涓?娌¤鍑烘潵"涓€鐐圭棔杩归兘娌℃湁**
    /// 锛堣涓嶅嚭 = 闈欓粯浠€涔堥兘涓嶅仛锛岃 搂42锛夆€斺€?鍙兘闈犲悎鎴愯鏂欏幓鐚滐紝鐚滀簡涓よ疆閮芥病澶嶇幇銆?    /// 寮€浜嗗畠锛屽仠椤垮畾鍨嬮偅涓€鍒讳細寰€鎺у埗鍙版墦涓€琛岋細璁ゆ垚浜嗕粈涔堬紙甯?`Rule`锛夈€佹垨鑰?*涓轰粈涔堟病璁?*
    /// 锛坄Rule` 閲屾瘡涓€閬撻椄閮藉甫鐫€鏁板瓧锛屾瘮濡?鐭╁舰妗嗚创涓嶄綇锛堟渶杩?18 > 瀹瑰樊 16锛?锛夈€?    ///
    /// 鐢ㄦ硶锛歚InkTeach.exe --reclog`锛堢粓绔噷璺戯級锛屾垨鐜鍙橀噺 `INKTEACH_RECLOG=1`
    /// 锛堝悗鑰呭鎵€鏈夎嚜妫€妯″紡涔熺敓鏁堬紝`--dwelltest` 灏辫兘鎶婃棩蹇楁墦鍑烘潵锛夈€?    /// 鈿?榛樿涓?false锛氭甯稿紑绐楄窇**涓嶅埛灞?*銆?    /// </summary>
    internal static bool InkLogEnabled;

    /// <summary>
    /// **澧ㄨ抗褰曞埗**锛堢敤鎴?2026-09-26 鎻愶細"**鎴戞墜鐢诲灏戞潯鍙屾洸绾跨粰浣狅紝浣犳牴鎹繖浜涘弻鏇茬嚎鏉ュ畾鍒?    /// 涓€涓柟妗?*"锛夈€傞潪 null 鏃讹紝姣忎竴绗旀敹绗旈兘鎶?*鍘熷閲囨牱鐐?*杩藉姞鍒拌繖涓枃浠堕噷銆?    ///
    /// 鐢ㄦ硶锛歚InkTeach.exe --recink reports/鎴戠殑鍙屾洸绾?txt`锛岀劧鍚庣収甯哥敾銆?    /// **璇嗗埆澶辫触鐨勯偅鍑犵瑪鎵嶆槸鏈€鍊奸挶鐨勬牱鏈?*锛屾墍浠ョ湅鍒版病鍙樺嚭鏉ヤ笉鐢ㄧ锛岀収鐢诲畬鎶墜灏辫銆?    ///
    /// 鈿?璁扮殑鏄?*璇嗗埆鍣ㄧ湅鍒扮殑閭ｄ覆鐐?*锛坄TickDwellShape` 閲岄偅浠?`pts`锛屽潗鏍囧彛寰勫畬鍏ㄤ竴鑷达級鈥斺€?    ///   涓嶆槸鎴愬瀷鍚庣殑瀵硅薄锛氬璞￠噷鍙墿涓や笁涓畾涔夌偣锛?*鍘熷鐨勯噰鏍风偣鎷夸笉鍥炴潵浜?*銆?    /// 鈿?涓€绗旈噷**娌″仠椤?*锛堟病瑙﹀彂璇嗗埆锛夋椂锛岀敤鏀剁瑪閭ｄ竴鍒荤殑 `ActiveStroke` 鍏滃簳锛堢偣鏁板鎵嶇畻锛夈€?    /// </summary>
    internal static string InkRecordPath;

    /// <summary>`--recinkp`锛氬綍澧ㄨ抗鏃?*澶氬綍鍘嬪姏鍜屾潵婧?*锛堣 `FlushInkRecord` 閭ｆ娉ㄩ噴锛夈€?    /// 涓嶅姞杩欎釜寮€鍏虫椂锛屽綍鍒舵牸寮忎笌浠ュ墠閫愬瓧涓€鑷达紙`--inkfile` 閭ｄ釜璇诲彇鍣ㄤ笉鍙楀奖鍝嶏級銆?/summary>
    internal static bool InkRecordWithPressure;

    /// <summary>杩欎竴绗旈噷鍝簺鐐规槸 **raw 琛ョ偣**鍔犵殑锛堜笅鏍囷級銆傚彧褰曟潵婧愭椂鎵嶈銆?/summary>
    private readonly System.Collections.Generic.List<int> _rawAddedIdx = new();

    /// <summary>鏈€杩戜竴娆?Wintab 璇诲埌鐨勫钩鏉垮潗鏍囦笌鍘熷鍘嬪姏锛坄--recinkp` 褰曡繘鏂囦欢锛?*涓嶅弬涓庣粯鍒?*锛夈€?    /// 鐢ㄦ潵浜嬪悗绠?骞虫澘 鈫?灞忓箷"鐨勬槧灏?鈥斺€?瑕佸仛"鏁寸瑪璧?Wintab"灏变笉鑳界寽杩欎釜鍏崇郴銆?/summary>
    internal int WtLastTabletX, WtLastTabletY, WtLastRawPressure;

    // ===================== `--clean N`锛氬叆绗斿墠鐨勫噣鍖?=====================
    //
    // **涓轰粈涔堣鏈夊畠锛?026-10-07锛岀敤鎴峰綍浜嗙湡鏈虹瑪杩逛箣鍚庯級**锛?    // 鎶婄敤鎴风湡瀹炲啓鐨?22 绗斿綍涓嬫潵閲忥紝鍙戠幇涓や欢浜嬶細
    //   鈶?**浣嶇疆鏈夋瘺鍒?*锛氭姈鍔?RMS 3.7px銆佸嘲鍊煎亸绂?31.7px銆佹柟鍚戝弽杞?12~23 娆?绗?    //      鈥斺€?鑰岀偣璺濆彧鏈?4~10px锛屾墍浠ユ槸"姣?4 涓偣灏辨潵鍥炴嫄涓€涓?鐨勯偅绉嶇粏姣涘埡銆?    //      app 鍘熸潵鐨勮窛绂荤獥锛?2px 鈮?3 涓偣锛夊瀹?*涓嶅**锛? 鐐瑰钩鍧囧彧鎶?卤2px 鍘嬪埌 卤1.2px銆?    //   鈶?**鍘嬪姏鏄彴闃?*锛?8 涓偣閲屽帇鍔涘彧鍙?8 娆?鈫?瀹藉害涓€璺充竴璺炽€?    //
    // 鍔犲ぇ `--mean2win` 涔熻兘鍘嬫瘺鍒猴紝浣嗛偅鏄?*璺濈绐?*锛氱獥鍙ｈ秺澶ф粸鍚庤秺澶э紙鈮堢獥涓€鍗婏級锛?    // 鎹㈡潵鐨勮窡鎵嬪彉宸緢鏄庢樉銆傝€岃繖涓ょ姣涚梾閮芥槸"鐭▼"鐨?鈥斺€?鐢?*鍥哄畾鐐规暟鐨勬粦鍔ㄥ钩鍧?*
    // 鏇村垝绠楋細鍚屾牱鍘嬪櫔澹帮紝婊炲悗鍙湁 N/2 涓?*鐐?*锛堚増 鍗佸嚑姣锛夈€?    //
    // 榛樿 **0 = 瀹屽叏涓嶅姩**锛堜竴涓瓧鑺備笉鏀癸級銆傜瓑鐢ㄦ埛璇曞嚭鍚堥€傜殑妗ｅ啀鑰冭檻璁鹃粯璁ゃ€?
    /// <summary>`--wintab`锛氬惎鐢ㄥ苟**寮哄埗**锛堜笉璁?鑷姩閫€"锛夈€?/summary>
    internal bool WintabForced;

    /// <summary>Wintab 鐨勭敤鎴峰亸濂介敭锛?*娌℃湁鐣岄潰**锛屽彧鐢卞懡浠よ鍐欙級銆?/summary>
    private const string WintabPrefKey = "wintab";

    /// <summary>鍛戒护琛岃姹傜殑鍋忓ソ鍊硷紙"1"/"0"/绌?娌¤姹傦級銆?*鎺ㄨ繜鍒?Loop() 鎵嶅啓鐩?*锛?    /// 鐞嗙敱瑙佷笂闈㈤偅娈垫敞閲婏紙鏋勯€犲嚱鏁伴噷 SelfCheckMode 杩樹笉鍙俊锛夈€?/summary>
    private string _pendingWintabPref = "";

    /// <summary>`--wintab` / `--nowintab` 浼氭妸瀹冭鍒拌缃噷 鈥斺€?鐢ㄦ埛鏁蹭竴娆″氨澶燂紝涓嶇敤姣忔甯﹀紑鍏炽€?/summary>
    private void WriteWintabPref(string v)
    {
        try
        {
            SetUiPref(WintabPrefKey, v);
            // **绔嬪埢钀界洏**锛堜笉鍙槸鏍囪剰锛夛細杩欐槸鐢ㄦ埛鏄庣‘琛ㄨ揪鐨勬剰鎰匡紝
            // 涓囦竴绋嬪簭涔嬪悗宕╀簡/琚己鏉€涔熶笉璇ヤ涪 鈥斺€?鑰屼笖"璁颁綇浜?杩欎欢浜嬪繀椤诲綋鍦哄彲楠岃瘉銆?            FlushSettings();
            Console.WriteLine($"  [wintab] 宸茶浣忥細{(v == "1" ? "浠ュ悗榛樿鍚敤" : "浠ュ悗榛樿鍏抽棴")}"
                              + $"锛堣缃噷鐨?{WintabPrefKey}锛屾病鏈夌晫闈級");
        }
        catch (Exception ex) { Console.WriteLine($"  [wintab] 璁板亸濂藉け璐ワ紙涓嶅奖鍝嶄娇鐢級锛歿ex.Message}"); }
    }

    // ---- 婀垮ⅷ妗ｄ綅锛?026-10-13 鏂板锛氱敤鎴疯姹?妗ｄ綅寮忓垏鎹?锛屽甫璧颁笂鏈轰綋楠岋級----------------
    //
    // 妗ｄ綅鏄?*鍦ㄥ師鏉ョ殑璺緞涔嬩笂鍙犲姞**锛屼笉鏄浛鎹細
    //   0 = 鍘熻矾寰勶細涓嶅杺绯荤粺杞ㄨ抗銆佷笉寤哄簲鐢ㄦ箍澧ㄥ眰鈥斺€旀箍澧ㄥ彧鏈変富浜ゆ崲閾捐嚜缁橈紙**榛樿**锛?    //       淇濊瘉"鍏虫。 = 鍜屼互鍓嶉€愬瓧涓€鏍?锛夛紱
    //   1 = + 绯荤粺濮旀墭杞ㄨ抗锛圛DCompositionDelegatedInkTrail锛氬彧琛?杩樻病涓婂睆鐨勯偅涓€灏忔埅"锛夛紱
    //   2 = + 搴旂敤婀垮ⅷ灞傦紙FastWet锛氬簲鐢ㄤ晶鐙珛 DComp 铏氳〃鐢绘暣绗旓紝鑷粯鐓ф棫鍏滃簳锛夛紱
    //   3 = 1 + 2锛堣建杩逛紭鍏堬紝鍚姩澶辫触/杩愯鏃跺け璐ョ敱 FastWet 鎺ョ锛夈€?    // 涓変釜妗ｄ綅閲?*涓讳氦鎹㈤摼鑷粯閮界収鏃?*锛堟。浣嶅彧鍔犱笉鍑忥級锛屾墍浠ヤ换浣曚竴妗ｉ兘涓嶄細姣斿師璺緞鏇村樊銆?    //
    // 鍒囨崲鏂瑰紡锛歚--wetmode N`锛堣浣忓埌璁剧疆閲岋級鎴栬繍琛屾椂 **Ctrl+Alt+Shift+W** 寰幆銆?    internal static int WetMode;

    /// <summary>婀垮ⅷ妗ｄ綅鐨勭敤鎴峰亸濂介敭锛堝拰 wintab 涓€鏍凤細鍛戒护琛?鐑敭鍐欙紝娌℃湁鐣岄潰锛夈€?/summary>
    private const string WetModePrefKey = "wetmode";

    /// <summary>鍛戒护琛岃姹傜殑妗ｄ綅锛堟帹杩熷埌 Loop() 鎵嶅啓鐩橈紝鐞嗙敱鍚?wintab锛夈€?/summary>
    private string _pendingWetModePref = "";

    internal static string WetModeName(int m) => m switch
    {
        1 => "绯荤粺濮旀墭杞ㄨ抗",
        2 => "搴旂敤婀垮ⅷ灞傦紙FastWet锛?,
        3 => "杞ㄨ抗 + 鍏滃簳",
        _ => "鍘熻矾寰勶紙涓嶅姞婀垮ⅷ灞傦級",
    };

    /// <summary>鎶婂綋鍓嶆。浣嶈惤鍒颁袱涓紑鍏充笂锛屽苟鎶?琚叧鎺夐偅鏉￠€氶亾"鐨勫湪鍦烘箍澧ㄦ敹鎺夈€?/summary>
    private void ApplyWetMode()
    {
        bool trail = WetMode is 1 or 3;
        bool layer = WetMode is 2 or 3;
        OverlayWindow.InkTrailEnabled = trail;
        OverlayWindow.FastWetFallbackEnabled = layer;
        foreach (var w in _windows)
        {
            if (!trail) w.EndInkTrail();
            if (!layer) w.EndFastWet();
        }
        _dirty = true;
    }

    /// <summary>鐑敭锛氬垏涓嬩竴妗ｏ紙0鈫?鈫?鈫?鈫?锛夈€?/summary>
    internal void CycleWetMode() => SetWetMode(WetMode == 3 ? 0 : WetMode + 1, "Ctrl+Alt+Shift+W");

    /// <summary>鐣岄潰鐐广€岃缃?鈫?澧ㄨ抗 鈫?婀垮ⅷ妗ｄ綅銆嶏細鐩存帴瀹氬埌鏌愪竴妗ｏ紙0..3锛夈€?/summary>
    internal void SetWetModeFromUi(int mode) => SetWetMode(mode, "璁剧疆 鈫?澧ㄨ抗 鈫?婀垮ⅷ妗ｄ綅");

    /// <summary>
    /// 瀹氭。锛?*寮曟搸鏄潈濞?*锛夛細绔嬪埢鐢熸晥銆佺珛鍒昏惤鐩樸€佹墦涓€琛岃鏄庛€?    /// 涓や釜鍏ュ彛閮借蛋杩欓噷锛氬叏灞€鐑敭 <see cref="CycleWetMode"/> 鍜岃缃〉閭ｄ竴琛?    /// 锛坄IEngineCommands.SetWetMode`锛夆€斺€?鍚屼竴浠朵簨鍙啓涓€浠?锛岀晫闈笉鑷繁璁版。銆?    /// </summary>
    private void SetWetMode(int mode, string from)
    {
        WetMode = Math.Clamp(mode, 0, 3);
        ApplyWetMode();
        if (!SelfCheckMode)
        {
            try
            {
                SetUiPref(WetModePrefKey, WetMode.ToString());
                FlushSettings();     // 绔嬪埢钀界洏锛氱敤鎴锋槸"鎸変簡涓€涓?锛屼笉鑳戒涪
            }
            catch (Exception ex) { Console.WriteLine($"  [婀垮ⅷ] 璁版。浣嶅け璐ワ紙鏈浠嶇敓鏁堬級锛歿ex.Message}"); }
        }
        Console.WriteLine($"[婀垮ⅷ] 妗ｄ綅 {WetMode}锛歿WetModeName(WetMode)}锛坽from}锛涗富浜ゆ崲閾捐嚜缁樺缁堢収鏃у厹搴曪級");
        _dirty = true;
    }

    /// <summary>`--clean N`锛氬叆绗斿墠瀵逛綅缃笌鍘嬪姏鍋?N 鐐规粦鍔ㄥ钩鍧囷紙0/1 = 鍏筹級銆?/summary>
    internal static int CleanWindow;

    /// <summary>杩炵画澶氬皯绗旓紙鍏?ink 閭ｇ闈炵瑪绗旓級娌′粠 Wintab 鎷垮埌鍘嬪姏銆?/summary>
    private int _wtDryStrokes;

    /// <summary>
    /// **鑷姩閫€**锛?026-10-07 鐢ㄦ埛鎻愬嚭"瑕佷笉瑕侀粯璁ゅ叧鎺?涔嬪悗鍔犵殑淇濋櫓锛夈€?    ///
    /// 鎯虫竻妤氱殑浜嬶細鎴戜滑**鍙粠 Wintab 鍊?鍘嬪姏"杩欎竴鏍?*锛屽潗鏍囨案杩滆蛋鍘熸潵鐨勮矾 鈥斺€?    /// 鎵€浠ユ渶鍧忔儏鍐靛彧鏄?鍘嬪姏涓嶅"锛岃€屽帇鍔涜繕鏈夊尯闂存柇瑷€鍏滅潃銆傜湡姝ｇ殑闂鏄?    /// **鍦ㄥ埆浜虹殑鏈哄櫒涓?*锛堟暀瀹ら偅鍙帮級鎴戜滑鐪嬩笉瑙侊細
    ///   路 椹卞姩鏄埆瀹剁殑 / 鏄潖鐨?鈫?鎷夸笉鍒板帇鍔?    ///   路 鏌愪簺鏉垮瓙锛圓valonia 鐨?PR 閲岀偣鍚?Huion锛?*寮€浜?Wintab 浼氭妸 WM_POINTER 椤舵帀**
    /// 鎵€浠ヤ笌鍏?榛樿鍏?锛堥偅灏辫繛浣犻兘浜彈涓嶅埌锛夛紝涓嶅**榛樿寮€锛嬭嚜宸遍€€**锛?    /// 杩炵潃鍑犵瑪涓€绗斿帇鍔涢兘鎷夸笉鍒?鈫?璇存槑杩欏彴鏈哄櫒涓婂畠娌＄敤 鈫?鍏虫帀锛屾湰娆′細璇濅笉鍐嶅紑銆?    /// 鎯宠瀹冪暀鐫€鐢?`--wintab` 寮哄埗銆?    /// </summary>
    private void WintabSelfCheckAfterStroke(uint ptype)
    {
        if (!_wintab.IsOpen || WintabForced) return;
        if (ptype == Native.PT_PEN) return;          // 寮€ ink 鐨勭瑪鏈潵灏变笉鐢ㄥ畠锛屼笉绠楁暟
        if (WtPressurePoints > 0) { _wtDryStrokes = 0; return; }
        if (++_wtDryStrokes < 3) return;
        _wintab.Close();
        WintabEnabled = false;
        Console.WriteLine("  [wintab] 杩炵画 3 绗斾竴绗斿帇鍔涢兘娌℃嬁鍒?鈫?鏈浼氳瘽涓嶅啀鍚敤"
                          + "锛堣繖鍙版満鍣ㄤ笂瀹冩病鐢紱鎯冲己鍒剁暀鐫€鐢?`--wintab`锛?);
    }


    private readonly System.Collections.Generic.List<(float X, float Y, float P)> _cleanBuf = new();

    /// <summary>鎶婅繖涓€鎵圭殑鍘嬪姏鏍锋湰鎽婂埌鐐逛笂鐢ㄧ殑鏆傚瓨锛堝厤鍒嗛厤锛夈€?/summary>
    private readonly float[] _sampleScratch = new float[64];

    /// <summary>
    /// 鍔犱竴涓偣锛屽繀瑕佹椂鍏堝噣鍖栥€?    /// 鈿?婊炲悗 = 绐楀彛鍐呯殑鐐规暟锛堟渶澶?N 涓偣锛夈€侼 鍒紑澶ぇ锛氬啓鍒?5 灏辨湁鐐?绗斿皷鎷栫潃涓€鎴?浜嗐€?    /// </summary>
    private void AddPointCleaned(float x, float y, float p, double t)
    {
        if (ActiveStroke == null) return;
        InputBlackBox.Sample(NowMs, x, y, p);   // 榛戝專瀛愶細璁板綍鍠傝繘棰勬祴鍣ㄧ殑鍘熷鐐癸紙鍥炴斁闈犲畠 1:1 澶嶇幇锛?        // 鍠傞娴嬪櫒銆?*缁熶竴鐢ㄥ鐞嗘椂鍒?NowMs**鈥斺€斿悓涓€绗旈噷娣风敤"纭欢鏃舵爣 / 娑堟伅鏃舵爣"
        // 锛堢瑪娑堟伅 vs raw 琛ョ偣鐨勬潵婧愪笉鍚岋級浼氭妸閫熷害绠椾贡锛寁0 鍏堢敤涓€鎶婂崟璋冩椂閽熶繚绋炽€?        // 2026-10-15 鏀跺彛锛氬彧鍓╂敼杩涚増 v6锛圔4/Google/DL 宸查€€褰癸紝瑙?`璋冪爺/宸插仠鐢?棰勬祴绠楁硶鏀跺彛.md`锛夈€?        if (PredictTailEnabled) _predictor.Add(x, y, NowMs);
        if (CleanWindow <= 1) { ActiveStroke.AddPoint(x, y, p, t); return; }

        _cleanBuf.Add((x, y, p));
        if (_cleanBuf.Count > CleanWindow) _cleanBuf.RemoveAt(0);
        float ax = 0f, ay = 0f, ap = 0f;
        for (int i = 0; i < _cleanBuf.Count; i++)
        { ax += _cleanBuf[i].X; ay += _cleanBuf[i].Y; ap += _cleanBuf[i].P; }
        int n = _cleanBuf.Count;
        ActiveStroke.AddPoint(ax / n, ay / n, ap / n, t);
    }

    // ---- 鑷粯棰勬祴灏撅紙2026-10-15 鎺ュ叆锛氱嚎鎬ч娴嬶紝Chromium 鍚屾棌锛?-----------------
    // 鏃ц处锛?涔辫烦"鐨勬韬紝鍐欏湪 App.TailJumpTest 鐨勬敞閲婇噷锛夛細榧犳爣/鍏煎妯″紡杈撳叆鏄獊鍙戠殑鈥斺€?    // 鏉ヤ竴闃?鈫?灏惧反鐢╁嚭鍘伙紱闂撮殧涓€瓒?40ms 鈫?閫熷害娓呴浂 鈫?灏惧反鏁翠釜娑堝け銆備慨娉?= 闀垮害闄愰€熷钩婊戙€?    // 绾緥锛氬彧鐢绘樉绀猴紙鐙珛鍑犱綍锛屼笉杩涙枃妗?瀛樻。/鍛戒腑锛夈€佹瘡甯ч噸寤恒€佹參鍐?鍙嶅悜/鏂瑪鐢?    // InkPredictorLinear 鍐呭缓鎶婂叧锛涘紑鍏筹細璁剧疆銆屽ⅷ杩归娴嬨€嶈 / `--nopredict2`锛?*榛樿寮€**锛夈€?    // 2026-10-15 鐢ㄦ埛鎷嶆澘锛?*鍙栨秷姣璋冭妭**锛堛€岄娴嬫椂闀裤€嶈涓?`--predlatency` 鎾ら櫎锛夈€?    // **鏇挎崲 v6**锛堢敤鎴峰湪 Win10 鏈哄櫒瀹炴祴 v6 鐢╁熬鏄庢樉锛夆€斺€斿彧鍓╄繖涓嚎鎬ч娴?+ 涓€涓紑鍏炽€?    // v6 澶囦唤 `.revert/2026-10-15-棰勬祴绾挎€ф帴鍏?`銆佽鏄?`璋冪爺/宸插仠鐢?棰勬祴绠楁硶鏀跺彛.md`銆?    // 瀵圭収涓?60Hz 缁撹瑙?`璋冪爺/璋冪爺-绾挎€ч娴?Chromium.md`銆?    internal static bool PredictTailEnabled;

    // 鐗堟湰鍙锋樉绀鸿 Update.cs 鏈熬鐨?`ProductInfo`锛堢晫闈㈠伐绋嬬敤锛涘畠鎷夸笉鍒板唴閮ㄧ殑 UpdateFeed锛夈€?    // 鍞竴棰勬祴鍣紙鍦板钩绾垮浐瀹?21.3ms = 瀹炴祴绔埌绔欢杩燂紱娌℃湁鏃堕暱妗ｏ級銆?    private readonly InkPredictorLinear _predictor = new();
    // 棰勬祴鐐规殏瀛橈紙澶嶇敤锛?*涓嶆槸姣忓抚 new**鈥斺€攙6 涓€娆℃渶澶氱粰 鈮? 涓偣锛岀紦鍐茬粰瓒?64锛夈€?    private readonly PredictedPoint[] _predBuf = new PredictedPoint[64];
    // 濮旀墭澧ㄨ抗娲昏穬鏃剁殑棰勬祴缂╂斁锛?026-10-12 濮旀墭鎺ュ叆鍚庯級锛氭箍澧ㄥ凡鎶?澧ㄨ窡绗?鍋氭帀锛?    // 棰勬祴搴旈€€鍦衡€斺€旈粯璁ょ缉鍒?0.35锛堢暀涓€涓濆鍐叉敹绗斾氦鎺ワ級锛沗--wetpred 0` 鍏ㄥ叧 / `1` 鍥炴棫琛屼负銆?    private bool _trailActiveThisStroke;
    private float _wetPredScale = 0.35f;
    private float _tailLen;                               // 鏄剧ず灏鹃暱锛堢敾甯冨儚绱狅紝闄愰€熷钩婊戯級
    private float _tailWidthState;                        // 灏惧骞虫粦鐘舵€侊紙0=鏈畾锛?    private readonly List<Vector2> _tailOffsets = new();  // 棰勬祴鍋忕Щ锛堢浉瀵瑰師濮嬫湯鐐癸紝鐢诲竷鍧愭爣锛?    private readonly List<Vector2> _tailCanvas = new();   // 渚?Overlay锛氶敋鐐?+ 棰勬祴鐐癸紙鐢诲竷鍧愭爣锛?    private float _tailWidth = 3f;

    /// <summary>Overlay 姣忓抚璇伙細棰勬祴灏剧幇鍦ㄨ涓嶈鐢伙紙鐐规暟 鈮? 鎵嶇敾锛夈€?/summary>
    internal bool PredictTailActive =>
        PredictTailEnabled && ActiveStroke != null && _tailCanvas.Count >= 2;
    internal IReadOnlyList<Vector2> PredictTailPoints => _tailCanvas;
    internal float PredictTailWidth => _tailWidth;
    /// <summary>璇婃柇锛坄--tailjumptest` / 榛戝專瀛愬洖鏀剧敤锛夛細鍠傜偣鏁?/ 灏剧偣鏁?/ 褰撳墠灏鹃暱銆?/summary>
    internal string PredictTailDebug =>
        $"feed={_predictor.Count} canvas={_tailCanvas.Count} len={_tailLen:F1}";

    private void ResetPredictTail()
    {
        _predictor.Reset();
        _trailActiveThisStroke = false;
        _tailLen = 0f;
        _tailWidthState = 0f;
        _tailOffsets.Clear();
        _tailCanvas.Clear();
    }

    /// <summary>
    /// 姣忓抚涓€娆★紙娓叉煋鍓嶏級锛氱畻鍑鸿繖涓€甯ц鐢诲闀跨殑棰勬祴灏俱€?    /// **闀垮害闄愰€熷湪"鍍忕礌"涓婂仛**锛堟不"璺宠穬鐨勭瑪澶?锛夛細閫熷害浼拌鐨勫抚闂村皷宄帮紙鎵归噺鎶曢€掋€?    /// 鍘嬪姏鍙樺寲锛変笉鍐嶇洿鎺ヤ箻杩涘亸绉烩€斺€斿熬宸撮暱搴︽寜 浼稿睍 ~2.2px/甯с€佸洖鏀?~1.2px/甯?    /// 锛?0Hz 涓?鈮?00/110 px/s锛夐€艰繎鐩爣锛屾柟鍚戠収鏃с€傜湅寰楄鐨勬晥鏋滄槸"澧ㄩ『婊戝湴璺熶笂鍘?鏀跺洖鏉?銆?    /// 锛坕nternal锛氳嚜妫€鐨?`SettleFrames` 甯ч┍鍔ㄥ櫒涔熻璋冨畠鈥斺€斿畠涓嶈蛋涓诲惊鐜€傦級
    /// </summary>
    internal void UpdatePredictTail()
    {
        _tailCanvas.Clear();
        if (!PredictTailEnabled || ActiveStroke == null || ActiveStroke.Points.Count < 2)
        {
            _tailLen = 0f;
            _tailOffsets.Clear();
            return;
        }

        float frameMs = _windows.Count > 0 && _windows[0].RefreshPeriodMs > 1.0
            ? (float)_windows[0].RefreshPeriodMs
            : 1000f / 90f;

        var pred = _predBuf;                      // 澶嶇敤缂撳啿
        int n = _predictor.Predict(pred);         // 鍞竴棰勬祴鍣紙2026-10-15 璧凤細绾挎€э級
        if (n > 0)
        {
            var raw = ActiveStroke.Points[^1];
            _tailOffsets.Clear();
            for (int i = 0; i < n; i++)
                _tailOffsets.Add(new Vector2(pred[i].X - raw.X, pred[i].Y - raw.Y));
            // 2026-10-12锛氭湰绗旀湁绯荤粺濮旀墭杞ㄨ抗锛堟箍澧ㄥ湪鐢伙級鈫?棰勬祴閫€鍦猴紙脳_wetPredScale锛夈€?            if (_trailActiveThisStroke && _wetPredScale < 0.999f)
                for (int i = 0; i < _tailOffsets.Count; i++)
                    _tailOffsets[i] = _tailOffsets[i] * _wetPredScale;
        }

        // ---- 闀垮害闄愰€燂紙鍦?鍍忕礌"涓婂仛锛屾不"璺宠穬鐨勭瑪澶?锛?---
        // 閫熺巼鎸?*鏃堕棿**瀹氫箟锛坧x/ms锛夛細60Hz 瀛︽牎灞忎笌 90Hz 鏈満瑙傛劅涓嶆紓绉伙紙甯ф暟涓嶅悓銆佹椂闂寸浉鍚岋級銆?        // 涓夋鍒嗗紑锛氭甯镐几鍑哄钩婊?/ 姝ｅ父鍥炵缉鐭?/ **棰勬祴澶辨晥鏃跺揩鏀?*鈥斺€斿悗鑰呮不
        // "鎬ヨ浆澶勬棫鏂瑰悜鐨勫熬鐣欏湪灞忓箷涓婃參鎱㈢缉"锛堢敤鎴?2026-10-08 瀛︽牎鏈猴細"鍐欏揩鏃舵€ヨ浆鍋跺皵璺充竴涓?锛夈€?        float tipLenRaw = 0f;
        if (n > 0 && _tailOffsets.Count > 0)
        {
            var o = _tailOffsets[^1];
            tipLenRaw = MathF.Sqrt(o.X * o.X + o.Y * o.Y);
        }
        // 2026-10-12锛?*DL 妗ｇ殑閫熷害鑷€傚簲闄愰€?*鈥斺€斿揩鍐欐椂鏀舵斁閮借鏇村揩銆?        // 绂荤嚎瀵圭収锛坃ml-predict 鎬昏〃 搂鍏級锛氬浐瀹氶檺閫熷湪蹇洿绾夸笂鍚冩帀 ~17px 婊炲悗锛?3鈫?0锛夛紝
        // 鑷€傚簲鏀跺洖 ~80%锛?0鈫?6.6锛変笖涔辩敥浠嶈窡寰椾綇銆侭4 妗ｄ繚鎸佸師甯告暟锛堝畠褰撴椂鏄寜鍘熷
        // 绐佸彂杈撳叆璋冪殑锛屼笉鍔級銆?        float extRate = 0.20f, retRate = 0.11f, fastRate = 0.72f;
        float rate = n <= 0
            ? fastRate * frameMs                                       // 澶辨晥锛氬揩鏀讹紙鈮?px/甯90Hz锛?            : (tipLenRaw > _tailLen ? extRate : retRate) * frameMs;    // 姝ｅ父锛氫几 2.2 / 缂?1.2锛園90Hz锛?        _tailLen += Math.Clamp(tipLenRaw - _tailLen, -rate, rate);
        if (_tailLen < 0.4f) _tailLen = 0f;
        if (_tailLen <= 0f || _tailOffsets.Count == 0) return;

        // 閿氱偣浼樺厛鐢?姝ｅ湪鏄剧ず鐨勫钩婊戣緭鍑烘湯鐐?锛堟帴澶村杩炵画锛屼笉浼氬拰绗旇韩閿欏紑锛夛紱
        // 寤烘ā涓嶆垚鍔燂紙catmull/raw锛夋椂閫€鍥炲師濮嬫湯鐐广€?        Vector2 anchor;
        float anchorP = -1f;
        if (StrokeMotion.Build(ActiveStroke) && StrokeMotion.Count > 0)
        {
            var m = StrokeMotion.At(StrokeMotion.Count - 1);
            anchor = new Vector2(m.X, m.Y);
            anchorP = m.Z;
        }
        else
        {
            var r = ActiveStroke.Points[^1];
            anchor = new Vector2(r.X, r.Y);
        }

        // 灏惧锛?*涓庣瑪韬湯鐐瑰悓涓€鏉ユ簮**鈥斺€斿帇鎰熷彇"寤烘ā杈撳嚭鏈偣"鐨勫帇鍔涳紝鑰屼笉鏄師濮嬫湯鐐?        //锛堝師濮嬪帇鍔涘彲鑳芥槸璧风瑪閭ｄ竴涓嬬殑灏栧嘲锛屼細璁╁熬宸存瘮绗旇韩鑳栧嚭涓€鍦堬紝鐪嬭捣鏉ュ氨鏄?绗斿ご鍐掍釜鍦?锛?        // 2026-10-08 鐢ㄦ埛鎶ョ殑"璧风瑪鍏堝嚭涓渾"灏辨槸瀹冿紜鐭熬鐨勮鎰燂級銆傚搴﹀啀鍋氫竴闃跺钩婊戦槻闂€?        float pForWidth = anchorP >= 0f ? anchorP : ActiveStroke.Points[^1].P;
        float wTarget = ActiveStroke.HasPressure
            ? MathF.Max(1f, PressureWidth.HalfWidth(ActiveStroke.Width, pForWidth) * 2f)
            : MathF.Max(1f, ActiveStroke.Width);
        _tailWidthState = _tailWidthState <= 0f ? wTarget : _tailWidthState + (wTarget - _tailWidthState) * 0.3f;
        _tailWidth = _tailWidthState;

        // 澶煭鐨勫熬涓嶇敾锛氬渾绔附涓?鍙堢煭鍙堢矖"鐨勪竴鎴湅璧锋潵灏辨槸涓渾鐐?鍦嗗ご锛堝悓涓€鏉″弽棣堬級銆?        if (_tailLen < 2f) return;

        var tipOff = _tailOffsets[^1];
        float tipOffLen = MathF.Max(1e-3f, MathF.Sqrt(tipOff.X * tipOff.X + tipOff.Y * tipOff.Y));
        float scale = _tailLen / tipOffLen;
        _tailCanvas.Add(anchor);
        for (int i = 0; i < _tailOffsets.Count; i++)
            _tailCanvas.Add(anchor + _tailOffsets[i] * scale);
    }

    /// <summary>璁颁笅"褰撴椂鐨?Wintab 璇绘暟"锛屼緵 `--recinkp` 褰曡繘鏂囦欢锛堜笉鍙備笌缁樺埗锛夈€?/summary>
    private void NoteWintabSample()
    {
        WtLastTabletX = _wintab.LatestX;
        WtLastTabletY = _wintab.LatestY;
        WtLastRawPressure = _wintab.RawPressure;
    }

    /// <summary>褰曞埗缂撳啿锛?*璇嗗埆鍣ㄨ繖涓€绗旂湅鍒扮殑閭ｄ覆鐐?*锛坄TickDwellShape` 姣忔鏀掍竴浠斤級銆?    /// 鍙瓨寮曠敤銆佷笉澶嶅埗 鈥斺€?娌″紑褰曞埗鏃朵竴涓瓧鑺傞兘涓嶈姳銆?/summary>
    private Vector2[] _recordPts;

    /// <summary>杩欎竴绗旇鎴愪簡浠€涔?/ 涓轰粈涔堟病璁わ紙鍐欒繘褰曞埗鏂囦欢鐨勬澶达紝鏂逛究鎸?澶辫触鐨勬牱鏈?褰掔被锛夈€?/summary>
    private string _recordGuess = "";

    /// <summary>褰曞埗鏂囦欢閲岀殑绗斿簭鍙凤紙浠?1 寮€濮嬶級銆?/summary>
    private int _recordSeq;

    /// <summary>**涓ょ瑪閰嶅鐨勬椂闂寸獥鍙?*锛堟绉掞級銆傚師鏉ユ槸鐢ㄦ埛 2026-09-25 瀹氱殑 **5 绉?*锛?    /// **2026-09-26 鏀惧鍒?20 绉?*锛堢敤鎴锋姤"鐢诲嚑娆℃墠鎴愪竴娆?涔嬪悗鏍稿鐨勶級銆?    ///
    /// 鈿?5 绉掍负浠€涔堜笉澶燂細杩欎釜绐楀彛閲忕殑鏄?**涓婁竴绗旀敹绗旈偅涓€鍒?*"鍒?**杩欎竴绗斿仠椤?*"涔嬮棿鐨勬椂闂达紝
    /// 涔熷氨鏄?**鐢荤浜屾敮鑺变簡澶氫箙 锛?鍋滈】**"鈥斺€?涓€绗旂敾寰楁參涓€鐐癸紙鎯充竴涓嬨€佹瘮涓€涓嬩袱鏀涓嶅绉帮級
    /// 灏辫秴浜嗐€傚疄娴嬭繖瀵硅鏂欓噷鐨勬椂闂村垎甯冩病鏈夋剰涔夛紙鑷鏄帹鏃堕挓鐨勶級锛屼絾浠?鐢讳竴鏀鍑犵"
    /// 杩欎釜閲忕骇鐪嬶紝5 绉?*蹇呯劧**缁忓父涓嶅銆?    ///
    /// 鈿?鏀惧**娌℃湁寮曞叆鏂扮殑璇厤椋庨櫓**锛氭寫"涓婁竴绗?鐨勭涓€鏉″氨鏄?**鏂囨。閲屾渶鍚庝竴绗?*"锛?    /// 鎵€浠ヤ腑闂村彧瑕佺敾浜嗗埆鐨勶紝`prev` 灏卞凡缁忔崲浜轰簡锛涜繖涓獥鍙ｅ彧鏄?鍒窡寰堜箙浠ュ墠閭ｄ竴绗旂‖鍑?
    /// 鐨勪竴閬撳厹搴曘€傜湡瑕佽閰嶏紝鎸′綇瀹冪殑鏄偅鏉?*涓績瀵圭О**鏍￠獙锛堣嚜妫€閲?涓ょ瑪涓嶇浉骞?    /// 锛堝悓涓€杈圭敾涓ら亶锛夆啋 涓嶈鍙樻垚鍙屾洸绾?鐩潃瀹冿級銆?/summary>
    internal const double TwoBranchWindowMs = 20000;

    /// <summary>鍏跺畠鍥惧舰锛?*璇嗗埆閭ｄ竴鍒荤殑瀹氫箟鍏冪礌鐐?*锛堝菇鐏垫敼澶у皬鐨勫熀鍑嗭紝瑙?ArmedStrokeMove锛夈€?    /// 瀛?鍩哄噯"鑰屼笉鏄?绱浣嶇Щ"锛氫竴寰嬩粠鍩哄噯绠?*缁濆鍊?* 鈥斺€?绱浼氳窡鐫€浜氬儚绱犳姈鍔ㄦ紓锛?    /// 鐩寸嚎閭ｈ竟鐨勮捣姝ユ鍖猴紙<see cref="DwellDragSlopLogical"/>锛夊氨鏄负浜嗚繖浠朵簨銆?    /// 鐩寸嚎涓嶅悆杩欎竴浣嶏紙瀹冩嫋鍙︿竴澶存敼褰㈢姸锛夈€?/summary>
    private Vector2[] _dwellGhostOrigin;

    /// <summary>骞界伒鏈?*涓嶅姩鐨勯偅涓畾涔夊厓绱?*鐨勪笅鏍囷紙閿氱偣锛夈€傚垽鎹?= **绂荤瑪灏栨渶杩?*鐨勯偅涓€涓?    /// 鈥斺€?鍜岀洿绾跨殑 <see cref="_dwellLinePin"/> 鏄?*鍚屼竴鏉″垽鎹?*锛堢敤鎴?2026-09-25 瀹氾細
    /// "鍦嗗舰鍦嗗績涓嶅姩銆佺煩褰㈡湁涓€涓《鐐逛笉鍔?锛涗汉鏀剁瑪鐨勪綅缃氨鍦?缁堢偣"閭ｄ竴甯︼紝鎵€浠ヨ繙绔墠鏄捣鐐癸級銆?    /// 鎴愬瀷鏃舵寫瀹氥€佹嫋鍔ㄥ叏绋嬩笉鍙樸€傗垝1 = 杩欎竴绗斾笉鍚冨菇鐏垫敼澶у皬銆?/summary>
    private int _dwellGhostAnchorIdx = -1;

    /// <summary>骞界伒鏈?*璺熺潃绗斿皷璧扮殑閭ｄ釜瀹氫箟鍏冪礌**鐨勪笅鏍囥€傚垽鎹?= **绂荤瑪灏栨渶杩?*鐨勯偅涓€涓?    /// 锛堝氨鏄?鐢ㄦ埛鎷栧嚭鏉ョ殑閭ｄ釜缁堢偣"锛夈€傚拰閿氱偣涓€璧峰湪鎴愬瀷鏃舵寫瀹氥€?/summary>
    private int _dwellGhostNearIdx = -1;

    /// <summary>
    /// armed 涔嬪悗"鎸変綇鎷栧姩"鐨?*璧锋姝诲尯**锛堥€昏緫鍍忕礌锛夛細绗斿皷鍦ㄨ繖涓寖鍥村唴鍔紝绾夸竴涓儚绱犻兘涓嶆敼銆?    ///
    /// 涓轰粈涔堥潪鏈変笉鍙紙鍜?`DwellAssist.DeadZoneLogical` 鍚屼竴涓亾鐞嗭紝浣嗗悗鏋滄洿鍑讹級锛?    /// 绗斿皷"闈欐"鎸夊湪灞忓箷涓婃椂锛岄┍鍔ㄤ粛鍦ㄤ笂鎶ヤ簹鍍忕礌鎶栧姩銆傛病鏈夋鍖虹殑璇濓紝閭ｆ鎶栧姩浼氳褰撴垚
    /// "鐢ㄦ埛鍦ㄦ嫋"鈥斺€?*鐩寸嚎鏈€鎯?*锛氱瑪灏栧氨鍋滃湪"璺熺潃绗斿皷璧扮殑閭ｄ竴澶?锛屾姈 1 涓敾甯冨崟浣嶅氨澶?    /// 鎶婄嚎鍘嬫垚闆堕暱搴︼紙`SnapEndPoint` 瀵归浂闀垮害鍘熸牱杩斿洖锛夆啋 **鏁存潯绾垮彧鍓╀竴涓偣**
    /// 锛堢敤鎴?2026-09-24 鎶ョ殑"绔栫潃鐢荤殑鐩寸嚎鍙樻垚寰堢煭鐨勪竴涓í鐩寸嚎"锛沗--dwelltest` H6 閽変綇锛?    /// 淇箣鍓嶉偅涓€妗ｆ紓绉?299.51 鐢诲竷鍗曚綅锛屾暣鏉?300 鐨勭嚎鍙墿涓€涓偣锛夈€?    /// </summary>
    internal const float DwellDragSlopLogical = 5f;

    /// <summary>
    /// 鎿嶄綔鏉￠偅涓€鍧楃幇鍦ㄧ敾鎴?*鏀惰捣鏉ラ偅涓€棰楀渾閽?*鍚楋紙鑰屼笉鏄竴鏁存潯涔濇牸锛夈€?    /// 涓ょ鏉ユ簮锛氱敤鎴疯嚜宸辨敹鐨勶紙<see cref="SelBarCollapsed"/>锛夈€?*鐢诲畬鑷姩閫変腑閭ｄ釜妗?*
    /// 锛堣 <see cref="_autoSelCollapsed"/>锛夈€?    ///
    /// 鈿?缁樺埗 / 鍛戒腑 / 鍏夋爣**閮藉繀椤婚棶杩欎竴鏉?*锛氬彧鍒?`SelBarCollapsed` 鐨勮瘽锛?    /// 鑷姩閫変腑閭ｄ釜妗嗕細"鐢荤殑鏄渾閽€佺偣鐨勬槸涓€鏁存潯"锛屼袱杈瑰涓嶄笂
    ///锛?鍚屼竴涓悕鍗曞啓鍦ㄥ澶勫繀婕忎竴澶?锛岃 鏋舵瀯-鍒嗗眰涓庤鍒?md 浜?7锛夈€?    /// </summary>
    internal bool BarDrawnCollapsed => SelBarCollapsed || _autoSelCollapsed;

    /// <summary>褰撳墠寮€鐫€鐨勬诞鍔ㄩ潰鏉匡紙鍚屼竴鏃跺埢鍙紑涓€涓級銆傝 <see cref="SelPanel"/>銆?/summary>
    internal SelPanel SelPanelOpen = SelPanel.None;

    /// <summary>
    /// **鑷畾涔夊彇鑹叉澘寮€鐫€鍚?*锛?.2.0 鍔狅紝`SelectionSwatches` 鏈€鍚庝竴鏍肩偣寮€鐨勯偅涓皬鑹叉澘锛夈€?    ///
    /// 瀹冩槸澧ㄨ抗闈㈡澘鐨?*瀛愰潰鏉?*锛氶潰鏉垮叧 / 鎹㈤潰鏉?/ 鏀舵潯 / 閫夊尯娌′簡锛屽畠閮借窡鐫€鍏?    ///锛堣 CloseCustomColor锛夛紝缁熶竴鏀跺湪涓€澶勶紝鍏嶅緱鍑虹幇"闈㈡澘鍏充簡鑹叉澘杩樻寕鍦ㄥ睆骞曚笂"銆?    /// </summary>
    internal bool CustomColorOpen;

    /// <summary>
    /// 姝ｅ湪鎷栭鑹查潰鏉块噷鐨勭矖缁嗘粦鏉°€?    ///
    /// 8.2.0 璧峰畠鏄?*杩炵画婊戞潯**锛堜笉鍐嶆槸"鐐逛竴涓嬪氨鍚稿埌妗ｄ綅"锛夛細鎷栧姩鏈熼棿鍔ㄤ綔鐩存帴浣滅敤鍦?    /// 閫変腑瀵硅薄涓婏紙`_widthDragAction` 鍙嶅 Redo锛屽睆骞曞疄鏃跺彉锛夛紝鏉炬墜鎵嶆寕涓婃挙閿€鏍?    /// 鈥斺€斻€屼竴娆℃嫋鎷?= 涓€姝ユ挙閿€銆嶃€?    /// </summary>
    private bool _sliderDragging;

    /// <summary>绮楃粏婊戞潯杩欎竴娆℃嫋鎷藉搴旂殑鍔ㄤ綔锛堟嫋鍔ㄤ腑鍙嶅鏀圭洰鏍囧€硷紱鏉炬墜鎻愪氦鎴栦涪寮冿級銆?/summary>
    private SetStrokePropAction _widthDragAction;
    /// <summary>鎸変笅閭ｄ竴鍒绘粦鏉＄殑鍊硷紙鏉炬墜瑕佷笉瑕佹彁浜ょ殑鍒ゆ嵁涔嬩竴锛夈€?/summary>
    private float _widthDragStartValue;
    /// <summary>鎷栨嫿涓粦鏉＄殑鍊硷紙閫昏緫鍍忕礌锛涚敾鏁板€煎拰鍒?娌″彉灏变笉閲嶇敾"閮界敤瀹冿級銆?/summary>
    private float _widthDragValue;

    /// <summary>鎷栧姩涓殑绮楃粏鍊硷紙閫昏緫鍍忕礌锛夆€斺€旂粯鍒堕偅琛屾暟鍊笺€佽嚜妫€璇诲€奸兘璧板畠銆?/summary>
    internal float WidthSliderDragValue => _widthDragValue;
    /// <summary>榧犳爣姝ｆ寜鍦ㄧ矖缁嗘粦鏉′笂鍚楋紙缁樺埗"鎷栧姩涓樉绀烘暟鍊?閭ｆ潯鍒ゆ嵁锛夈€?/summary>
    internal bool WidthSliderDragging => _sliderDragging;
    /// <summary>鍙栬壊鏉块噷褰撳墠閫夌殑 HSV锛?..1锛夆€斺€旂粯鍒跺拰鑷閮借瀹冦€?/summary>
    internal (float H, float S, float V) PickHsv => (_pickH, _pickS, _pickV);

    /// <summary>绮楃粏婊戞潯杩欎竴娆℃嫋鎷界殑鐩爣锛堟寜涓嬮偅涓€鍒荤殑闈炲浘鍍忛€変腑瀵硅薄锛夈€?/summary>
    private readonly List<Stroke> _propDragTargets = new();

    /// <summary>
    /// **鑷畾涔夊彇鑹叉澘**锛?.2.0锛夌殑涓変欢鐘舵€侊細
    ///   路 `_pickH/_pickS/_pickV` = 鑹叉澘閲屽綋鍓嶉€夌殑 HSV锛?..1锛夛紱
    ///   路 `_colorDragging` / `_colorDragOnHue` = 姝ｅ湪鎷栬壊鐩告潯杩樻槸楗卞拰搴?鏄庡害鏂瑰潡锛?    ///   路 `_colorDragAction` = 杩欎竴娆℃嫋鎷界殑灞炴€у姩浣滐紙鍚屽悕涓€濂楋細鏉惧紑鍦ㄨ壊鏉块噷 = 搴旂敤锛?    ///     鏉惧紑鍦ㄥ闈?= 鎾ら攢骞朵涪寮?鈥斺€?瑙?<see cref="EndColorPickerDrag"/>锛夈€?    /// </summary>
    private float _pickH, _pickS = 1f, _pickV = 1f;
    private bool _colorDragging;
    private bool _colorDragOnHue;
    private SetStrokePropAction _colorDragAction;

    /// <summary>澶嶅埗鎴愬姛涔嬪悗閫夊尯"闂竴涓?鐨勫埌鏈熸椂鍒伙紙0.25 绉掞紝瑙?Overlay.DrawSelection锛夈€?/summary>
    internal double SelFlashUntilMs;

    /// <summary>澶嶅埗鎴愬姛鏃堕€夊尯闂竴涓嬬殑鏃堕暱锛堟绉掞級銆?/summary>
    public const double SelFlashMs = 250;
    /// <summary>
    /// 鎵规敞閿洏妯″紡銆傚紑 = 閿洏褰掓壒娉ㄥ眰锛堢紪杈戝揩鎹烽敭鐢熸晥锛夛紱
    /// 鍏?= 瑕嗙洊灞傛案涓嶆姠鐒︾偣锛岄敭鐩樿繕缁欎笅灞傜▼搴忋€?    ///
    /// 杩欐槸涓?*鏄庣‘鐨勫彇鑸?*锛氬紑鐫€鐨勬椂鍊欐斁鏄犱腑鐨?PPT 鏀朵笉鍒伴敭鐩橈紝鍥犱负瑕嗙洊灞?    /// 鎷跨潃鍓嶅彴鐒︾偣銆傜敤鎴锋媿鏉?鍏堜笉鑰冭檻 PPT锛屽厛鎶婂簳灞傚仛濂?锛屾墍浠ラ粯璁ゅ紑锛?    /// 鐢?Ctrl+Alt+K 鍒囨崲銆?    /// </summary>
    internal bool KeyboardMode = true;

    /// <summary>
    /// 鐩告満锛氱敾甯冨潗鏍?鈫?灞忓箷鍧愭爣鐨勭旱鍚戝亸绉汇€?*婊氬姩鍙敼杩欎竴涓暟**锛?    /// 涓嶅姩浠讳綍瀵硅薄鏁版嵁鈥斺€旇繖鏄粴鍔ㄨ兘鍋氬埌 O(1) 鐨勫墠鎻愩€?    ///
    /// 寰€涓嬫粴 = 鍐呭涓婄Щ = 杩欎釜鍊煎彉璐熴€傚彧鍋氱旱鍚戯細鏉夸功鏄旱鍚戠疮绉殑锛?    /// 妯悜娌℃湁鐢ㄦ涔嬪湴锛岃繕鐪佹帀鍙屾寚妯壂涓?鐢绘í绾?鐨勬墜鍔垮啿绐併€?    /// </summary>
    internal float ViewOffsetY;

    /// <summary>
    /// 鏈€鍚庝竴娆?婊氬姩鏉¤闇查潰"鐨勬椂鍒伙紙婊氬姩鎴栨偓鍋滐級銆傛粴鍔ㄦ潯鎸夎繖涓椂闂存贰鍑衡€斺€?    /// 闈欐 3 绉掑悗娑堝け锛圛nkClass 瀹炴祴 1.5 绉掑お蹇紝鐢ㄦ埛浼氭壘涓嶅埌瀹冿級銆?    /// </summary>
    internal double ScrollBarActiveAtMs = double.MinValue;

    /// <summary>鎸囬拡姝ｅ仠鍦ㄦ粴鍔ㄦ潯涓婏細婊戝潡鍔犵矖銆佷笉娣″嚭銆傛嫋鍔ㄤ腑涔熺畻锛堟粦鍧椾笉鑳藉崐璺贰鎺夛級銆?/summary>
    internal bool ScrollBarHover;
    internal bool ScrollBarDragging;

    /// <summary>
    /// 婊氬姩鏉℃€诲紑鍏炽€?*2026-10-09 鐢ㄦ埛鎷嶆澘锛氬仠鐢ㄣ€?*
    /// 鐞嗙敱锛堢敤鎴峰綊绾筹級锛歅DF / PPT / 妗岄潰鎵规敞閮界敤涓嶅埌瀹冿紱鐧芥澘鏈潵灏辩湅寰楀埌"绗嚑灞?锛?    /// 鍙崇紭 16px 鎶撳彇甯︿細鍚冩帀"鍐欏埌杈圭紭"鐨勮捣绗旓紙绗斾篃涓€鏍凤級锛涜Е鎽告嫋瀹冭繕鏈夎繃
    /// "骞界伒鎵嬫寚"鍗℃鍚庣画杈撳叆鐨?bug锛堣 OnPointerUp 閲岀殑闃插尽娉ㄩ噴锛夈€?    /// 浠ｇ爜 / 鍑犱綍 / 鑷鍏ㄩ儴淇濈暀锛屽彧鏄笉鐢汇€佷笉鎺ョ锛?    /// `--scrollshow` 瀵圭収妯″紡浼氫复鏃舵墦寮€瀹冿紙瑙?App.ScrollShowcase锛夈€?    /// </summary>
    internal static bool ScrollBarEnabled = false;

    /// <summary>Overlay锛堝埆澶勭被鍨嬶級缁樺埗鐢細闈欐€佸瓧娈靛湪閭ｉ噷鍚嶅瓧涓嶅ソ鍐欙紝缁欏畠涓€涓疄渚嬭娉曘€?/summary>
    internal bool ScrollBarVisible => ScrollBarEnabled;
    /// <summary>鎸変笅鏃舵寚閽堢浉瀵规粦鍧楅《杈圭殑鍋忕Щ锛屾嫋鍔ㄤ腑淇濇寔瀹冧笉鍙橈紝婊戝潡鎵嶄笉"璺?銆?/summary>
    private float _scrollGrabDy;
    private OverlayWindow _scrollWindow;

    /// <summary>灞忓箷鍧愭爣 鈫?鐢诲竷鍧愭爣锛堢浉鏈猴級銆傝緭鍏ヨ繘鏉ョ涓€浠朵簨灏辨槸杩囪繖涓€?/summary>
    internal void ScreenToCanvas(ref float x, ref float y) => y -= ViewOffsetY;

    /// <summary>褰撳墠瑙嗗彛鍦?*鐢诲竷鍧愭爣**閲岀殑鑼冨洿锛堢浉鏈轰箣鍚庯級銆?/summary>
    internal RectF ViewportCanvas => new()
    {
        MinX = _virtualX,
        MinY = _virtualY - ViewOffsetY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY - ViewOffsetY + _virtualH,
    };

    /// <summary>
    /// 鐢诲竷鑼冨洿 = 鍐呭杈圭晫 鈭?褰撳墠瑙嗗彛 鈭?**瑙嗗彛涓嬫柟涓€灞忕┖鐧?*銆?    ///
    /// 鏈€鍚庨偅涓€灞忔槸蹇呴』鐨勶細鍙彇"鍐呭 鈭?瑙嗗彛"鐨勮瘽锛?*绌烘枃妗ｇ殑鐢诲竷鎭板ソ涓€灞?*锛?    /// 涓嬭竟鐣屽氨鏄?0锛屽線涓嬫粴绔嬪埢琚す鍥炲幓鈥斺€旇€屾粴涓嶅姩灏卞啓涓嶅埌涓嬮潰鍘伙紝鍐欎笉鍒颁笅闈?    /// 鍐呭灏变笉闀匡紝鍐呭涓嶉暱鑼冨洿灏变笉闀库€︹€?*姝婚攣**銆?    /// 锛堝疄娴嬭俯杩囷細鐢ㄦ埛涓€鎵撳紑杞欢锛屾粴杞畬鍏ㄦ病鍙嶅簲锛屽氨鏄繖涓€傦級
    ///
    /// 鐣欎竴灞忕┖鐧界瓑浜?姘歌繙鏈変竴寮犵櫧绾稿湪涓嬮潰"锛屽儚璁颁簨鏈竴鏍枫€?    /// </summary>
    internal RectF CanvasExtent
    {
        get
        {
            var vp = ViewportCanvas;
            var r = Doc.Extent(vp);
            // 鏂囨。椤靛眰涔熻绠楄繘鐢诲竷鑼冨洿锛屽惁鍒欑浉鏈哄埌鏈€鍚庝竴椤典箣鍓嶅氨琚す浣忥紙婊氫笉涓嬪幓锛夈€?            if (DocView.IsOpen) r.Add(DocView.Extent());
            r.Add(new RectF
            {
                MinX = vp.MinX, MinY = vp.MaxY,
                MaxX = vp.MaxX, MaxY = vp.MaxY + _virtualH,
            });
            return r;
        }
    }
    // 杩欎袱涓粰鑷鐪嬶紙--cursortest 瑕佹瀯閫?鎷栨嫿涓?鐨勭姸鎬侊級銆?    internal SelHandle _dragHandle = SelHandle.None;
    internal bool _dragIsMove;
    /// <summary>
    /// 杩欎竴娆℃嫋鍔ㄧ殑"鐐归€夎捣鎵嬬瑪杩?锛堟寜涓嬫椂鍛戒腑鐨勯偅涓€鏉★級銆傛澗鎵嬫椂鑻?*涓€鐐规病绉诲姩**锛?    /// 灏辨妸澶氶€夋敹绐勬垚鍙€夊畠鈥斺€擯PT/Figma 鐨勮涓恒€傝 EndSelDrag銆?    /// </summary>
    internal Stroke _dragHitStroke;

    /// <summary>
    /// 鐐归€夌殑鍛戒腑瀹瑰樊锛堥€昏緫鍍忕礌锛夈€傜粏绗斿彧鏈?1.5 閫昏緫鍍忕礌瀹斤紝涓ユ牸鎸夊ⅷ鍒ょ瓑浜庣偣涓嶄腑锛?    /// 4 鍍忕礌鍜屾搷浣滄潯鎵嬫焺鐨勫宸悓涓€閲忕骇銆傝 <see cref="InkDocument.HitObjectAt"/>銆?    /// </summary>
    internal const float ClickToleranceLogical = 4f;
    private SelectionFrame _dragFrame;

    /// <summary>
    /// 杩欎竴娆℃墜鍔?*鐪熺殑鍔ㄨ繃**鍚楋紙鎸囬拡绂绘寜涓嬮偅涓€鐐硅秴杩囦簡鐐瑰嚮瀹瑰樊锛夈€?    ///
    /// 鍙敤鏉ュ喅瀹?瑁呴グ瑕佷笉瑕佹敹璧锋潵"锛堣 <see cref="SelChromeCollapsed"/>锛夛細
    /// 鍗曞嚮涓€涓嬩笉璇ヨ鎵嬫焺鍜屾搷浣滄潯闂竴涓嬨€?    /// </summary>
    private bool _selDragMoved;

    /// <summary>
    /// 鎷栧姩棰勮锛堣 <see cref="DragPreviewActive"/>锛夐噷**浠庡唴瀹瑰眰鎽樺嚭鍘?*鐨勯偅涓€鎵癸紝
    /// 鍒ゅ畾鐢紙鍐呭灞傞噸鐢讳竴鍧楁椂 O(1) 闂?杩欐潯瑕佷笉瑕佽烦杩?锛夈€傝 <see cref="IsContentDetached"/>銆?    /// </summary>
    private readonly HashSet<Stroke> _detachSet = new();

    /// <summary>
    /// 鍚屼竴鎵癸紝鎸?*鏂囨。椤哄簭**鎺掍竴閬嶃€傜敾鐨勬椂鍊欑敤瀹冣€斺€?    /// 涓€涓泦鍚堢殑閬嶅巻椤哄簭涓嶆槸 z 搴忥紝澶氶€夐噷鑽у厜绗斿帇鐫€瀛楅偅绉嶆儏鍐靛氨浼氱炕鏉ョ炕鍘汇€?    /// </summary>
    private readonly List<Stroke> _detachOrdered = new();

    /// <summary>
    /// 涓婁竴甯ф嫋鍔ㄩ瑙堝崰鐨勭敾甯冭寖鍥淬€?    ///
    /// 鑴忓尯瑕佸啓鎴?涓婁竴甯?鈭?杩欎竴甯?锛氬悗缂撳啿閲岃汉鐫€鐨勬槸涓ゅ抚鍓嶇殑鐢婚潰锛岃€岃繖涓ゅ潡骞惰捣鏉?    /// 姝ｅソ瑕嗙洊涓ゅ抚涔嬮棿鐨勫樊闆嗭紙杩欏拰浠ュ墠"鏃т綅缃?鈭?鏂颁綅缃?鏄悓涓€濂楄处锛岃
    /// <c>Overlay.UpdateFrameDirty</c> 鐨勬敞閲婏級銆?    /// </summary>
    private RectF _dragPrevBounds;

    // =====================================================================
    //  鍥惧舰瀹氫箟鍏冪礌缂栬緫锛堢洿绾?/ 绠ご / 鍦?/ 妞渾锛夛細鍜?鎷栧璞?鍚屼竴鏉″揩璺?    //
    //  鎵嬪娍鏈?*妯″瀷涓€涓瓧涓嶆敼**锛屽彧鍦ㄦ诞鍔ㄥ眰鎸?涓存椂鍑犱綍锛堢绫?+ 鏀瑰姩鍚庣殑瀹氫箟鍏冪礌锛?鐢伙紝
    //  鏉炬墜鎵嶆彁浜や竴姝ユ敼鍑犱綍鐨勫姩浣滐紙瑙?璁″垝-鍥惧舰宸ュ叿.md 8.1鈶狅級銆?    //  姣忓抚 SetPoint + 鏍囪剰閭ｆ潯璺細鎶婂彈褰卞搷鐨勫潡鏁村潡閲嶅厜鏍呭寲鈥斺€斿疄娴?19ms/甯с€?    // =====================================================================

    /// <summary>姝ｅ湪鎷栧畾涔夊厓绱犳敼鍑犱綍锛堣鏁版爣绛鹃潬瀹冨喅瀹氭樉涓嶆樉绀猴級銆?/summary>
    internal bool VertexDragging => _vertexDragging;

    /// <summary>棰勮瑕佺敾鐨勯偅涓€鏉★紙妯″瀷姝ゅ埢娌″姩锛屽畠杩樻槸鏃у嚑浣曪級銆?/summary>
    internal Stroke VertexPreviewStroke => _vertexDragging ? _vertexTarget : null;

    /// <summary>棰勮鐢ㄧ殑**灞€閮ㄥ潗鏍?*鎺у埗鐐癸紙鎶婃嫋鍔ㄤ腑閭ｄ釜瀹氫箟鍏冪礌鎹㈡帀涔嬪悗鐨勪竴缁勭偣锛夈€?/summary>
    internal IReadOnlyList<Vector2> VertexPreviewPoints => _vertexPreviewLocal;

    /// <summary>
    /// 鎷?*鐒︾偣涓夎褰㈢殑椤剁偣 P** 鏃讹紝杩欎竴甯х畻鍑烘潵鐨勫弬鏁拌锛沗NaN` = 杩欎竴鎷栦笉鏄嫋 P銆?    ///
    /// 涓轰粈涔堣鍗曠嫭璧颁竴涓瓧娈点€佷笉濉炶繘 <see cref="VertexPreviewPoints"/>锛?    /// **P 鏍规湰涓嶆槸鎺у埗鐐?*鈥斺€斿畠鏄?鍙傛暟瑙?绠楀嚭鏉ョ殑锛堣 <see cref="Stroke.ConicEllipsePointLocal"/>锛夛紝
    /// 濉炶繘鐐硅〃灏辩瓑浜庢壙璁?P 鏄 3 涓畾涔夊厓绱?锛岄偅绱ф / 瀛樻。 / 鍙樻崲閮藉緱涓哄畠澶氬啓涓€濂楄鐭┿€?    /// 鎵€浠ラ瑙堝彧澶氬甫**杩欎竴涓暟**锛屾诞鍔ㄥ眰鎷垮畠鐢婚瑙堬紙瑙?Overlay 鐨?DrawVertexPreview锛夈€?    /// </summary>
    internal float VertexPreviewFocusU => _vertexPreviewFocusU;
    private float _vertexPreviewFocusU = float.NaN;

    /// <summary>
    /// 鎷?*鍧愭爣绯荤殑鏍艰窛鎵嬫焺**锛堢鍥涢锛夋椂锛岃繖涓€甯х畻鍑烘潵鐨勬牸璺濓紱`0` = 杩欎竴鎷栦笉鏄嫋瀹冦€?    ///
    /// 鍜?`FocusPointU` 鍚屼竴涓亾鐞嗭細**鏍艰窛涓嶆槸鎺у埗鐐?*锛堝潗鏍囩郴姘歌繙鍙湁涓変釜鐐癸紝
    /// 瑙?<see cref="StrokeKind.Coordinate"/>锛夛紝鎵€浠ラ瑙堝彧澶氬甫杩欎竴涓暟锛?    /// 娴姩灞傛嬁瀹冪敾棰勮锛堣 Overlay 鐨?DrawVertexPreview 閲岄偅涓?`g.AxisGridStep`锛夈€?    /// 鐢ㄦ墜鍔垮紑濮嬫椂閭ｄ釜 `0` 褰?娌″湪鎷?鐨勮鍙凤紝鏄洜涓烘牸璺濇湰韬?*涓嶈鏄?0**
    ///锛? = 鑷姩锛岃 <see cref="Stroke.AxisGridStep"/> 鐨勬敞閲婏級銆?    /// </summary>
    internal float VertexPreviewGridStep => _vertexPreviewGridStep;
    private float _vertexPreviewGridStep;

    /// <summary>鎷栧姩涓殑閭ｄ釜鍏冪礌鍦?*鐢诲竷鍧愭爣**閲岀殑浣嶇疆锛堣鏁版爣绛捐创鍦ㄥ畠澶栦晶锛夈€?/summary>
    internal Vector2 VertexPreviewCanvasPoint => _vertexPreviewCanvas;

    /// <summary>璇绘暟璇ユ樉绀哄摢涓€绉嶉噺锛堢洿绾挎槸 伪銆佸渾鏄?r/d銆佹き鍦嗘槸 a 鎴?b锛夈€?/summary>
    internal VertexReadoutKind VertexReadout => _vertexReadout;

    /// <summary>璇绘暟鐨勪富鏁板€硷紙伪 / r / a / b锛夛紱鍗曚綅鐢?<see cref="VertexReadout"/> 鍐冲畾銆?/summary>
    internal float VertexReadoutValue => _vertexReadoutValue;

    /// <summary>璇绘暟鐨勬瑕佸€硷紙鍙湁鍦嗙敤锛氱洿寰?d = 2r锛夛紱鍏跺畠鎯呭喌鏄?0銆?/summary>
    internal float VertexReadoutSecondary => _vertexReadoutSecondary;

    /// <summary>璇绘暟鏄笉鏄?鍚?鍑烘潵鐨勶紙杞惛闄?/ Shift 缃戞牸锛夆€斺€旀爣绛炬寜瀹冨彉鑹层€?/summary>
    internal bool VertexInclinationSnapped => _vertexSnapped;

    /// <summary>
    /// 鎷栭《鐐?/ 杞寸鐐?/ 鍥涜鏃?*鍚稿埌浜嗕粈涔?*锛堣鏍?9.6锛夈€俙None` = 杩欎竴甯ф病鍚镐綇
    /// 锛堜篃鍙兘杩欎竴鎷栨牴鏈笉閫傜敤鍚搁檮鈥斺€旀暣浣撶Щ鍔ㄣ€佹棆杞氨姘歌繙鏄?`None`锛夈€?    /// 鑳跺泭涓婄殑瀛楃敱瀹冨喅瀹氾紙<see cref="SelectionHandles.ShapeSnapLabel"/>锛夈€?    /// </summary>
    internal ShapeSnapKind ShapeSnapKind => _shapeSnap;

    /// <summary>鍚镐綇鏃堕偅棰楄兌鍥婅创鍝効锛堢敾甯冨潗鏍囷級锛氭嫋鍏冪礌 = 琚嫋鐨勯偅涓厓绱犮€佹嫋鍥涜 = 鎸囬拡銆?/summary>
    internal Vector2 ShapeSnapAnchor => _shapeSnapAnchor;

    private bool _vertexDragging;

    /// <summary>
    /// **澶氱瑪鍥惧舰**宸茬粡鎷栧畬鍑犵瑪锛? = 杩樻病寮€濮嬶級銆傜敾娉曚笌"涓€鍏辨湁鍑犵瑪"閮藉湪
    /// <see cref="PlanOf"/> 閭ｅ紶琛ㄩ噷锛堢洰鍓嶅彧鏈夊弻鏇茬嚎锛?*涓ょ瑪**锛夈€?    ///
    /// 瑙勫垯鏄収 **InkClass** 鐨勶紙鐢ㄦ埛 2026-09-20 瀹氾細"鎸夌収浠栫殑杩欎釜瑙勫垯澶嶅埢"锛夆€斺€?    /// 瀹冪殑澶氱瑪鍥惧舰灏辨槸"**涓€绗?= 鎸変笅-鎷?鏉炬墜**"锛屾澗鎵嬫帹杩涗笅涓€绗?    ///锛坄MW_ShapeDrawing.cs` 鐨?`drawMultiStepShapeCurrentStep` 锛?`inkCanvas_MouseUp`锛夛細
    ///
    ///   路 **鍙屾洸绾夸袱绗?*锛坄drawingShapeMode = 24/25`锛宍MW_ShapeDrawing.cs:1051-1152`锛夛細
    ///     鈶?**浠庝腑蹇冩嫋鍑烘笎杩戠嚎**锛堟嫋鍒板摢銆乣A / B` 灏辨槸澶氬皯锛夆啋 鏉炬墜**閿佷綇**锛屾鍚庝笉鍐嶅彉锛?    ///     鈶?**鍐嶆嫋涓€涓?*锛氭嫋鍒板摢銆?*鏇茬嚎灏辩粡杩囧摢**锛堜腑蹇冩部鐢ㄧ 1 绗旈偅涓紝
    ///        InkClass 鐨?`NeedUpdateIniP()` 鍦ㄧ浜岀瑪鍒绘剰**涓嶉噸璁?`iniP`**锛宍:1971-1977`锛夈€?    ///
    /// 涓€斿嚑绗旀澗鎵?*閮戒笉绠楀畬**锛氬崐鎴愬搧鐣欏湪 `ActiveStroke`锛?*涓嶈繘鏂囨。銆佷笉鍐欐挙閿€璁板綍**锛夛紝
    /// 鏈€鍚庨偅涓€绗旀澗鎵嬫墠缁熶竴鎻愪氦 鈥斺€?鎵€浠ヤ袱绗斿悎璧锋潵**鍙湁涓€鏉℃挙閿€璁板綍**锛圛nkClass 涔熸槸
    /// 杩欎釜鍙ｅ緞锛歚:1846-1850` 鎶婃笎杩戠嚎绗旂敾鍜屾洸绾跨瑪鐢诲杩涘悓涓€涓彁浜ら噷锛夈€?    /// </summary>
    private int _stepIndex;

    /// <summary>
    /// 姝ｅ湪鐢荤殑杩欎釜鍗婃垚鍝佺敤鐨勬槸鍝紶琛紙null = 涓嶆槸澶氱瑪鍥惧舰锛夈€傚畠鍜?<see cref="_stepIndex"/>
    /// 涓€璧峰洖绛?**杩樻湁娌℃湁涓嬩竴绗?*"锛屾墍浠ユ崲宸ュ叿 / 璁よ緭鏃朵袱浠堕兘瑕佹竻锛堣 <see cref="SwitchTool"/>锛夈€?    /// </summary>
    private StepPlan _stepPlan;

    /// <summary>
    /// 澶氭鍥惧舰鐨?*绗竴涓偣**锛堝弻鏇茬嚎鐨勪腑蹇?/ 鎶涚墿绾跨殑椤剁偣锛岀敾甯冨潗鏍囷級锛氬悗闈㈠嚑姝ラ兘鍥寸潃瀹冭浆銆?    /// </summary>
    private Vector2 _stepOrigin;
    private Stroke _vertexTarget;
    /// <summary>琚嫋鐨勯偅涓?*瀹氫箟鍏冪礌**锛堝渾鏄渾蹇?鍦嗗懆鐐广€佹き鍦嗘槸涓績/鍥涗釜杞寸鐐广€佸杈瑰舰鏄《鐐癸級銆?/summary>
    private ShapeHandle _vertexHandle;
    private Vector2[] _vertexPreviewLocal;
    private Vector2 _vertexPreviewCanvas;
    private VertexReadoutKind _vertexReadout;
    private float _vertexReadoutValue;
    private float _vertexReadoutSecondary;
    private bool _vertexSnapped;
    /// <summary>杩欎竴甯х殑"鐗规畩褰㈢姸鍚搁檮"缁撴灉锛堣 <see cref="ShapeSnapKind"/>锛夈€?/summary>
    private ShapeSnapKind _shapeSnap;
    private Vector2 _shapeSnapAnchor;
    /// <summary>涓婁竴甯?琚嫋鐨勯偅涓厓绱?闄勮繎鐨勮剰鐭╁舰锛堣繖涓€甯ц杩炲畠涓€璧锋爣锛夈€?/summary>
    private RectF _vertexPrevBounds;

    /// <summary>
    /// 鎷栧畾涔夊厓绱犳椂鐨勬渶灏忓昂瀵革紙**閫昏緫鍍忕礌**锛夛細鍦嗙殑鍗婂緞銆佹き鍦嗙殑鍗婅酱閮戒笉璁稿皬浜庡畠銆?    ///
    /// 鐞嗙敱鍜?鐢诲浘鏃舵嫋鍔ㄨ窛绂荤煭浜?4 閫昏緫鍍忕礌灏变笉鎻愪氦"鏄悓涓€涓細閫€鍖栨垚绾挎 / 鐐圭殑鍥惧舰
    /// 鍦ㄥ睆骞曚笂鐪嬩笉瑙侊紝鍗磋繕鍗犵潃涓€鏉″璞°€佽繕鑳借妗嗛€夊埌鈥斺€旀槸鏈€闅捐В閲婄殑涓€绫绘潅鐗┿€?    /// 鐢ㄦ埛瀵规き鍦嗙殑瑕佹眰鍘熻瘽鏄?a銆乥 鍚勮鏈夋渶灏忓€硷紙涓嶈閫€鍖栨垚绾挎锛?銆?    /// </summary>
    internal const float ShapeMinAxisLogical = 4f;

    /// <summary>鎷栧畾涔夊厓绱犳椂锛岄偅涓鏁?*鏄粈涔堥噺**锛堝喅瀹氳兌鍥婁笂鍐?伪 / r / a / b锛夈€?/summary>
    internal enum VertexReadoutKind
    {
        None = 0,
        /// <summary>鐩寸嚎鐨勫€炬枩瑙?伪锛圼0掳,180掳)锛夈€?/summary>
        Inclination,
        /// <summary>鍦嗙殑鍗婂緞 r锛堣兌鍥婁笂椤哄甫鍐欑洿寰?d = 2r锛夈€?/summary>
        Radius,
        /// <summary>妞渾鐨勬í鍗婅酱 a銆?/summary>
        AxisA,
        /// <summary>妞渾鐨勭旱鍗婅酱 b銆?/summary>
        AxisB,
        /// <summary>
        /// 鎶涚墿绾匡細璇炬湰閲岄偅涓?**p**锛坄y虏 = 2px` / `x虏 = 2py` 閲岀殑 p锛夈€?        ///
        /// 涓轰粈涔堟姤 p 鑰屼笉鏄?鍗婂澶氬皯鍍忕礌"锛氱劍鐐?`(p/2, 0)`銆佸噯绾?`x = 鈭抪/2` 鍏ㄤ粠瀹冩潵锛?        /// 鑰屼笖**鍥涚寮€鍙ｅ悓涓€涓紡瀛?*锛坧 = 鍗婅法虏 梅 (2 脳 娣卞害)锛夆€斺€斾笉闇€瑕佹寜鏂瑰悜鍒嗘枃妗堛€?        /// </summary>
        ParabolaP,
        /// <summary>鍙屾洸绾匡細**瀹炲崐杞?*锛堟洸绾胯嚜宸辩殑閭ｄ竴鍗婏紝瀹炶酱娌?y 鏃舵姤鐨勬槸绾靛悜閭ｄ釜锛夈€?/summary>
        HyperbolaReal,
        /// <summary>鍙屾洸绾匡細**铏氬崐杞?*锛堟洸绾胯嚜宸辩殑鍙︿竴鍗婏紱**涓嶆槸**娓愯繎绾挎鐨?A / B鈥斺€旈偅涓や釜鍙敾铏氱嚎锛夈€?/summary>
        HyperbolaImag,
        /// <summary>姝ｅ鸡 / 浣欏鸡锛?*鍛ㄦ湡 T**锛堜竴涓懆鏈熸湁澶氬锛泂econdary 鏄尟骞?A锛夈€?/summary>
        WavePeriod,
    }

    /// <summary>姝ｅ湪鎷栨棆杞墜鏌勨€斺€斿害鏁版爣绛鹃潬瀹冨喅瀹氭樉涓嶆樉绀恒€?/summary>
    internal bool SelRotating;
    /// <summary>
    /// 褰撳墠杩欎竴鎷栦竴鍏辫浆浜嗗灏戝害锛?*閫嗘椂閽堜负姝ｃ€侀『鏃堕拡涓鸿礋**锛岀浉瀵规寜涓嬮偅涓€鍒伙級銆?    ///
    /// **涓嶈涓婇檺**锛堢敤鎴?2026-09-15 瀹氾細"璐村悎鎴戜滑楂樹腑鏁板"锛夛細杞袱鍦堝氨鏄?720掳锛?    /// 鍊掔潃杞氨鏄礋鏁帮紝瓒呰繃 卤180掳 涔?*涓嶇粫鍥?*鈥斺€旀墍浠ュ畠涓嶆槸"璧风偣鍒板綋鍓嶇偣鐨勫す瑙?锛?    /// 鑰屾槸姣忓抚涓€灏忔绱姞鍑烘潵鐨勶紙瑙?<see cref="_rotAccumDeg"/>锛夈€?    /// </summary>
    internal float SelRotationDegrees;
    /// <summary>杩欎釜瑙掑害鏄?鍚?鍑烘潵鐨勶紙90掳 鎴?Shift 15掳 缃戞牸锛夛紝鏍囩瑕佸彉鑹叉彁绀恒€?/summary>
    internal bool SelRotationSnapped;

    /// <summary>
    /// **鍗曢€変竴鏉＄洿绾?/ 绠ご**鎷栨棆杞焺鏃剁殑璇绘暟锛歚鎸変笅鏃剁殑鍊炬枩瑙?伪鈧€ + 杩欎竴鎷栫疮绉浆杩囩殑瑙?螖`銆?    ///
    /// **鏃犱笂闄愩€佷篃涓嶆姌瑙?*锛堢敤鎴?2026-09-18 婢勬竻锛?鈥︹€﹂€嗘椂閽堟槸涓烘锛岄『鏃堕拡涓鸿礋锛屽畠鍙互鏃犻檺
    /// 杞笅鍘伙紝涔熷氨璇村彲浠ヨ浆鍒?360銆?20銆?000 澶氬害锛屾垨鑰呰璐熺殑 2000 澶氬害锛岃繖閮芥槸鍙互鐨勩€?    /// 涔熷氨璇村畠鏄櫎浜嗗垵濮嬭搴︽槸瑕佽皟涓€涓嬩互澶栵紝鍏朵粬鍜屽師鏉ョ殑鏈€寮€濮嬬殑閫昏緫鏄竴鏍风殑銆?锛夈€?    /// 涓€鏉?伪鈧€ = 45掳 鐨勭嚎锛氶€嗘椂閽堣浆涓€鍦?= `405掳`銆佸啀杞?= `765掳`锛涢『鏃堕拡 400掳 = `鈭?55掳`銆?    /// 鎵€浠ュ畠**涓嶆槸**鍊炬枩瑙掞紙鍊炬枩瑙掑彧鍦?[0掳,180掳)锛夛紝鏍囩涓婁篃灏卞啓鎴愮函鏁板瓧
    /// 锛堣 SelectionHandles.FormatSignedDegrees锛夈€?    ///
    /// 鍙湪"鍗曢€夌洿绾?绠ご"杩欎竴鏉″垎鏀噷鏈夋剰涔夛紝鐢?<see cref="SelRotationReadsInclination"/> 鍐冲畾
    /// 鏍囩璇昏皝锛涘叾瀹冩儏鍐碉紙澶氶€?/ 鍥惧儚 / 鑷敱绗旇抗 / 鍦嗭級鐓ф棫璇?螖锛?    /// 鍗曢€変竴涓浘褰紙鐭╁舰 / 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰級璇荤殑鏄Э鎬佽锛堣 <see cref="SelRotationPose"/>锛夈€?    /// </summary>
    internal float SelRotationInclination;
    /// <summary>杩欎竴娆℃棆杞殑璇绘暟鏄笉鏄?伪鈧€ + 螖锛堢湡 = 鍗曢€夌洿绾?绠ご锛涘亣 = 鐓ф棫璇?螖锛夈€?/summary>
    internal bool SelRotationReadsInclination;

    /// <summary>
    /// **鍗曢€変竴涓浘褰?*锛堢煩褰?/ 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰級鎷栨棆杞焺鏃剁殑璇绘暟锛氳繖涓浘褰?    /// **鐩稿姘村钩鐨勫Э鎬佽**锛堝害锛屾姌鍦?[0掳,180掳)锛?掳 = 姝ｇ殑銆?0掳 = 绔栫殑锛夈€?    ///
    /// 鍜岀洿绾跨殑閭ｄ釜璇绘暟鏈変袱澶?*鏁呮剰涓嶄竴鏍?*锛堣鏍?9.7锛夛細
    ///   路 **鎶樺湪 [0掳,180掳)**锛氬Э鎬佽鍙槸"杩欎釜鍥惧舰鏈濆摢杈硅汉鐫€"锛屾病鏈夊湀鏁板彲瑷€
    ///     锛堢洿绾块偅涓槸"浠?伪鈧€ 鎺ョ潃杞簡澶氬皯"锛岀敤鎴?2026-09-18 鏄庣‘瑕佷笉璁句笂闄愶紝涓よ€呯敤閫斾笉鍚岋級锛?    ///   路 瀹?*姣忓抚浠庢渶缁堢煩闃甸噷瑙ｅ嚭鏉?*锛堣 UpdateSelDrag 鏈熬锛夛紝涓嶆槸"鎸変笅鏃剁殑瑙?+ 绱Н瑙?
    ///     涓€璺疮鍔犲嚭鏉ョ殑鈥斺€斾簬鏄?鐭╅樀涓庢爣绛炬槸鍚屼竴涓"鏄?*鏋勯€犲嚭鏉ョ殑**鎬ц川锛氶暅鍍?/ 涓婁笅缈昏浆
    ///     杩囩殑鍥惧舰锛堣鍒楀紡涓鸿礋锛屾湰鍦拌浆 +1掳 鍦ㄥ睆骞曚笂鏄?-1掳锛変篃涓嶄細瀵逛笉涓娿€?    ///     绱Н瑙掔収鏍峰湪绠″惛闄勶細鍚镐綇浜嗗氨鎶婂樊棰濆姞鍥炲幓锛堝拰鐩寸嚎閭ｄ竴鏀槸鍚屼竴濂楄处锛夈€?    /// </summary>
    internal float SelRotationPose;
    /// <summary>杩欎竴娆℃棆杞殑璇绘暟鏄笉鏄Э鎬佽锛堢湡 = 鍗曢€変竴涓浘褰紱瑙?<see cref="SelectionHandles.PoseEditable"/>锛夈€?/summary>
    internal bool SelRotationReadsPose;

    /// <summary>杩欐鏃嬭浆鏄笉鏄?鍗曢€夌洿绾?绠ご"閭ｄ竴妗ｏ紙鎸変笅閭ｄ竴鍒诲垽瀹氫竴娆★紝鎵嬪娍閲屼笉鍐嶅彉锛夈€?/summary>
    private bool _rotLineLike;
    /// <summary>杩欐鏃嬭浆鏄笉鏄?鍗曢€変竴涓浘褰?閭ｄ竴妗ｏ紙璇诲Э鎬佽锛涘悓鏍锋寜涓嬫椂鍒ゅ畾涓€娆★級銆?/summary>
    private bool _rotPoseLike;
    /// <summary>琚浆鐨勫浘褰㈡槸涓嶆槸**闀滃儚杩?*鐨勶紙琛屽垪寮忎负璐燂級锛氫慨姝ｈ鍙嶇潃鍔犲洖绱Н瑙掞紙瑙?UpdateSelDrag锛夈€?/summary>
    private bool _rotMirrored;
    /// <summary>
    /// 鎸変笅閭ｄ竴鍒昏繖涓?*鍥惧舰**鐨勫Э鎬佽锛堟姌鍦?[0掳,180掳)锛夆€斺€斿畠瀵瑰簲鐩寸嚎閭ｄ竴鏀殑 伪鈧€锛?    /// 鍚搁檮瑕佸湪"瀹?+ 绱Н瑙?杩欎釜**杩炵画**鐨勮涓婂仛锛堟姌杩囩殑鍊肩洿鎺ュ惛浼氭妸涓夎褰㈢炕 180掳锛岃
    /// SelectionHandles.SnapExpandedDegrees 鐨勬敞閲婏級銆?    /// </summary>
    private float _rotStartPose;
    /// <summary>
    /// 鎸変笅閭ｄ竴鍒昏繖鏉＄嚎鐨勫€炬枩瑙?伪鈧€锛堟姌鍦?[0掳,180)锛夆€斺€旀棆杞鏁板氨鏄粠瀹冨紑濮?鎺ョ潃鍙?鐨勩€?    /// 鍒濆瑙掔敤鎶樿繃鐨勫€兼槸鏈夋剰鐨勶紙鐢ㄦ埛锛?鍒濆瑙掑害鏄璋冧竴涓?锛夛細涓€鏉＄嚎娌℃湁鏂瑰悜锛?    /// 鎸変笅鏃惰鍒?45掳 鑰屼笉鏄?405掳锛屼箣鍚庢墠鎸夌敤鎴疯浆鐨勬柟鍚戠疮鍔犮€?    /// </summary>
    private float _rotStartInclination;

    /// <summary>
    /// 鏈鏃嬭浆鐨?*绱Н瑙?*锛堝害锛岄€嗘椂閽堜负姝ｏ級銆傛瘡甯х敤
    /// <see cref="SelectionHandles.RotationStepDegrees"/> 鍙栦竴灏忔鍔犱笂鍘汇€?    ///
    /// 涓轰粈涔堜笉鐩存帴閲?璧风偣 鈫?褰撳墠鐐?锛氶偅涓噺澶╃敓鍙湁 卤180掳 鐨勫垎杈ㄧ巼锛?    /// 浠庤捣鐐圭洿鎺ョ敥鍒?200掳 鐨勪綅缃紝閲忓嚭鏉ユ槸 -160掳锛堟渶鐭矾寰勶級锛?杞簡涓€鍦?
    /// 杩欎欢浜嬪氨涓簡銆備竴甯т竴姝ュ湴绱Н鎵嶈寰椾綇杞簡鍑犲湀銆?    /// </summary>
    private float _rotAccumDeg;
    /// <summary>涓婁竴甯ф寚閽堝湪**妗嗗潗鏍?*閲岀殑浣嶇疆锛堢畻杩欎竴甯ц浆浜嗗灏戠敤锛夈€?/summary>
    private Vector2 _rotPrevPoint;

    private Vector2 _dragStartPoint;
    private Matrix3x2[] _dragStartXform;
    private Stroke[] _dragTargets;
    private Matrix3x2 _selDragMatrix = Matrix3x2.Identity;
    /// <summary>璋冭瘯寮€鍏筹細涓嶇敾鑷繁閭ｄ竴绗旓紝鍙暀绯荤粺鍚堟垚鍣ㄧ敾鍑烘潵鐨勫鎵樿建杩广€?/summary>
    internal bool SuppressActiveStroke;
    internal float MqMinX, MqMinY, MqMaxX, MqMaxY;
    internal bool PassThrough;
    /// <summary>
    /// 杩涚┛閫忓墠鐨勭櫧鏉跨姸鎬佲€斺€?绌块€忓紑鍏?閫€鍑烘椂鎸夊畠鎭㈠锛堣 <see cref="SetPassThrough"/> 鐨?    /// `restoreBoard`锛涚敤鎴?2026-09-30 鎷嶆澘锛?鐧芥澘寮€杩樻槸寮€銆佸叧杩樻槸鍏?锛夈€?    /// 鎹㈠伐鍏烽€€鍑虹┛閫?*涓?*鎭㈠锛堥偅鏄?鎴戝氨瑕佸啓"锛岃 SwitchTool 涓?SetPassThrough 鐨勬敞閲婏級銆?    /// </summary>
    private bool _boardBeforePassThrough;
    /// <summary>
    /// Windows only honours click-through for a *layered* window, so the
    /// default has to include WS_EX_LAYERED. The other modes are kept as
    /// controls for the automated pass-through test.
    /// </summary>
    internal PassThroughMode PassMode = PassThroughMode.LayeredTransparent;
    internal bool NoContentCache;
    internal double NowMs;

    internal static readonly Color4 PenColor = InkPalette.PenDefault;
    internal static readonly Color4 HighlighterColor = InkPalette.HighlighterDefault;

    // 涓嬮潰杩欎簺鏍囨垚 internal 鑰屼笉鏄?private锛屾槸缁欏紑鍙戞湡娴嬭瘯瀹夸富鐪嬬殑锛?    // InkTeach 閫氳繃 InternalsVisibleTo 缁ф壙寮曟搸銆佺洿鎺ヨ鍐呴儴鐘舵€佹潵鍋氳嚜鍔ㄥ寲
    // 娴嬮噺銆備骇鍝佺晫闈㈣蛋 IOverlayUi锛屼笉纰拌繖浜涖€?    internal readonly List<OverlayWindow> _windows = new();
    internal IntPtr _hInstance;
    internal string _className;
    private Native.WndProcDelegate _wndProc;
    private static readonly Dictionary<IntPtr, OverlayWindow> s_map = new();
    internal bool _quit;
    internal bool _dirty = true;
    private bool _animating;
    internal bool _drawing;
    private uint _activePointer;
    private uint _activePointerType;

    // ---- 鍘嬫劅閲囬泦涓庣瑪杩归娴嬶紙瀹炵幇瑙?Input/PenInput.cs銆丳rediction/InkPredictorLinear.cs锛?---
    //  杩欎袱浠朵簨鍏辩敤鍚屼竴鏉℃椂闂磋酱锛氶噰鏍风偣鑷甫纭欢鏃舵爣锛屽帇鎰熶繚鐪熼潬瀹冿紝澶栨帹棰勬祴涔熼潬瀹冦€?    private readonly PenSampleBuffer _pen = new();
    /// <summary>
    /// 闈炵瑪鎸囬拡锛堥紶鏍?/ 瑙︽懜锛夌殑鍚堝苟鐐广€傛墜鍐欐澘**娌″紑 Windows Ink** 鏃惰澶囨槸浠?    /// `PT_MOUSE` 涓婃姤鐨勶紝瑙︽懜灞忔槸 `PT_TOUCH`鈥斺€旇繖涓ゆ潯璺互鍓嶄竴鏉℃秷鎭彧鍙栦竴涓偣锛?    /// 蹇啓鏃惰建杩硅闈欓粯鎶界█锛堣 Input/PointerInput.cs锛夈€?    /// </summary>
    private readonly PointerSampleBuffer _ptr = new();
    private readonly Vector2[] _trailReal = new Vector2[PenSampleBuffer.MaxSamples];
    /// <summary>婀垮ⅷ**閫愮偣鍗婂緞**锛堝拰 _trailReal 涓€涓€瀵瑰簲锛夛細鏈夊帇鎰熸椂婀垮ⅷ涔熷緱鏈夌矖鏈夌粏锛?    /// 鍚﹀垯鎶墜閭ｄ竴涓嬬矖缁嗕細璺筹紙瑙?<see cref="TrailRadius"/>锛夈€?/summary>
    private readonly float[] _trailRadii = new float[PenSampleBuffer.MaxSamples];

    // [璇曢獙E锛?026-10-11 璇曡繃銆佸凡鎾 寰€绯荤粺杞ㄨ抗鐨?predicted 鏁扮粍鍠?鍐嶈蛋涓€姝?鐨勯娴嬬偣
    //锛坄--trailpredict`锛夛細榧犳爣/瑙︽懜蹇€熶功鍐欐椂**鐢╁熬鏄庢樉**锛堢敤鎴风湡鏈烘媿鏉匡級锛屾暣鏉℃挙鎺夈€?    // 缁撹锛氳繖鏉￠€氶亾鐨勯娴嬬偣涓嶈兘鐢ㄦ潵椤堕噰鏍烽棿闅欌€斺€斿睆骞曚笂鐨?灏惧反"涓€鐢╂瘮閲囨牱鏃т竴鐐规洿闅剧湅銆?
    // [鍒犻櫎 2026-10-05] 鑰侀娴嬬郴缁熸暣鏉￠摼锛坄_predictor`/`_predBuf`/`_trailPred`/`_tailScratch`銆?    // `PredictEnabled`/`_predictArg`/`PredictPrefKey`銆佹覆鏌撳熬涓庨娴嬪熬缁熻绛夛級锛氱敤鎴峰喅瀹氫笉鎺ラ娴嬨€?    // [2026-10-15 鏀跺彛] 棰勬祴涓夋。锛堢粡鍏?B4 / Google / DL锛夐€€褰广€佷笉杩涚紪璇戯紱鍞竴瀹炵幇 = v6
    // 锛堝浠?`.revert/2026-10-15-棰勬祴鏀跺彛/`锛岃鏄?`璋冪爺/宸插仠鐢?棰勬祴绠楁硶鏀跺彛.md`锛夈€?
    // ---- 涔﹀啓鏈熼棿鐨勫垎閰?/ GC 浠〃锛堜綆閰嶆満鎺掓煡鐢級--------------------------
    //
    // 浣庨厤鏈轰笂"鍗?鐨勭涓€鏉ユ簮涓嶆槸骞冲潎甯ф椂闂达紝鑰屾槸**鍋跺彂闀垮抚**锛屽叾涓渶鍑剁殑涓€绉嶅氨鏄?    // GC 鐨勫墠鍙扮 2 浠ｅ洖鏀讹紙瑕佹壂鏁翠釜鍫嗐€佽繕鍙兘鍘嬬缉鍐呭瓨锛屼竴娆″嚑鍗佹绉掞級銆?    // 鎵€浠ヨ繖涓や欢浜嬪繀椤昏兘鎵撳嵃鍑烘潵锛屽惁鍒欎紭鍖栧彧鑳介潬鐚滐細
    //   路 鍐欎竴绗斿埌搴曞垎閰嶄簡澶氬皯瀛楄妭锛堝垎閰嶈秺澶氾紝瓒婂鏄撹Е鍙戝洖鏀讹級锛?    //   路 杩欎竴绗旀湡闂寸湡鐨勫彂鐢熶簡鍑犳 GC銆佹槸鍝竴浠ｏ紙**绗?2 浠ｅ嚭鐜板湪涔﹀啓鏈熼棿 = 鍗遍櫓淇″彿**锛夈€?    /// <summary>杩欎竴绗旀湡闂存湰绾跨▼鐨勬墭绠″垎閰嶅瓧鑺傛暟銆?/summary>
    internal long StrokeAllocBytes;
    /// <summary>杩欎竴绗旀湡闂村畬鎴愮殑 GC 娆℃暟锛堢 0 / 1 / 2 浠ｏ級銆?/summary>
    internal int StrokeGc0, StrokeGc1, StrokeGc2;
    /// <summary>绱缁熻锛坄--penlive` 姹囨€婚偅涓€琛岃鐢級銆?/summary>
    internal int StrokesMeasured, StrokesWithGc2;
    internal long AllocBytesMax;
    internal double AllocKbSum;

    long _mAlloc0;
    int _mGc0, _mGc1, _mGc2;
    bool _measureDone;

    /// <summary>璧蜂华琛細璁颁笅杩欎竴鍒荤殑鍒嗛厤璁℃暟鍜屼笁浠?GC 娆℃暟銆?/summary>
    private void BeginStrokeMeasure()
    {
        // GetAllocatedBytesForCurrentThread 鍙槸璇讳竴涓鏁板櫒锛屽緢渚垮疁锛屼笉浼氳嚜宸辫Е鍙戝洖鏀躲€?        _mAlloc0 = GC.GetAllocatedBytesForCurrentThread();
        _mGc0 = GC.CollectionCount(0);
        _mGc1 = GC.CollectionCount(1);
        _mGc2 = GC.CollectionCount(2);
        _measureDone = false;
    }

    /// <summary>鏀朵华琛細绠楀樊棰濓紝绱繘鎬昏处銆傞噸澶嶈皟鐢ㄥ彧绠楃涓€娆★紙鏀剁瑪鏈夊嚑鏉″垎鏀級銆?/summary>
    private void EndStrokeMeasure()
    {
        if (_measureDone) return;
        _measureDone = true;
        StrokeAllocBytes = GC.GetAllocatedBytesForCurrentThread() - _mAlloc0;
        StrokeGc0 = GC.CollectionCount(0) - _mGc0;
        StrokeGc1 = GC.CollectionCount(1) - _mGc1;
        StrokeGc2 = GC.CollectionCount(2) - _mGc2;

        StrokesMeasured++;
        AllocKbSum += StrokeAllocBytes / 1024.0;
        if (StrokeAllocBytes > AllocBytesMax) AllocBytesMax = StrokeAllocBytes;
        if (StrokeGc2 > 0) StrokesWithGc2++;
    }

    /// <summary>鏈瑪鏈夋病鏈夊帇鎰燂紙璁惧绾у垽鏂紝涓嶆槸鐪嬫暟鍊硷級銆?/summary>
    internal bool ActiveStrokeHasPressure;
    /// <summary>涓婁竴绗旂殑鍚堝苟鐜?棰勬祴缁熻锛堣瘖鏂笌鑷鐢級銆?/summary>
    internal int LastCoalescedSamples, LastCoalescedMessages;
    /// <summary>绱缁熻锛?-penlive 缁撴潫鏃舵眹鎬荤敤锛夛細鐪熺瑪鐨勫帇鎰?鍊捐/鍚堝苟鎯呭喌銆?/summary>
    internal int PenTotalPoints, PenPressurePoints, PenMessages, PenSamples;
    internal bool PenSawPressureMask, PenSawTiltMask, PenSawRotationMask;
    /// <summary>绱缁熻锛氶潪绗旀寚閽堬紙榧犳爣 / 瑙︽懜锛夎鍒板灏戞秷鎭€佸灏戝悎骞堕噰鏍风偣銆?/summary>
    internal int PtrTotalPoints, PtrMessages, PtrSamples, PtrCoalescedExtra;
    // [鍒犻櫎 2026-10-05] 棰勬祴鍓嶅甫閲?/ 鍠?DWM 娈垫暟缁熻锛堣€侀娴嬬郴缁燂級銆?    internal int _cntDown, _cntMove, _cntUp, _cntCaptureLost;
    internal string _lastStrokeReport;

    /// <summary>`--strokefile` 鐨勮惤鐩樿矾寰勶紙**绌轰覆 = 涓嶅啓**锛涜繖椤圭洰娌″紑 nullable锛屼笉鐢?`?`锛夈€?/summary>
    private string _strokeFilePath = "";
    private long _hotkeysRegistered;

    /// <summary>
    /// 閿綅琛細鍔ㄤ綔 鈫?閿?+ 浣滅敤鍩熴€?*鍙厤缃€佸彲鏌ュ啿绐併€佸彲钀界洏**锛堣 KeyBindings.cs锛夈€?    /// 鍚姩鏃跺厛璇荤敤鎴烽厤缃殑瑕嗙洊椤癸紝閫€鍑烘椂鎶婃敼鍔ㄥ啓鍥炲幓銆?    /// </summary>
    internal KeyMap Keys = KeyMap.Default();

    /// <summary>鏌愪釜鍔ㄤ綔褰撳墠鐨勯敭浣嶆枃鏈紙鐣岄潰鎮仠鎻愮ず鐢紱閿綅琛ㄧ殑鍞竴璧锋簮瑙?KeyBindings.cs锛夈€?/summary>
    internal string KeyTextFor(KeyAction action) => Keys.KeyText(action);

    internal int _virtualX, _virtualY, _virtualW, _virtualH;

    internal double _autoExitAt = double.MaxValue;

    // ---- perf accounting --------------------------------------------------
    internal readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    internal double _fps;
    private int _frames;
    private double _fpsWindowStart;
    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private double _cpuPercent;
    internal double _peakWorkingSetMb;

    // ---- 鐣岄潰鎺ュ叆鍙?-------------------------------------------------------
    // 寮曟搸鍙 IOverlayUi銆傜晫闈㈤€氳繃 UiHost 鏀瑰紩鎿庣姸鎬侊紝姘歌繙涓嶇洿鎺ョ鏂囨。銆?    internal UiHost Host;
    internal IOverlayUi Ui;
    internal bool UiInvalidatePending;
    internal Color4 CurrentColor = InkPalette.PenDefault;
    internal Color4 HighlighterCurrent = InkPalette.HighlighterDefault;
    internal bool UiCapturing;

    /// <summary>鑷鐢細绯荤粺闂簡鎴戜滑鍑犳鍛戒腑娴嬭瘯銆佸叾涓嚑娆″洖绛?杩欏潡鏄垜鐨?銆?/summary>
    internal int _cntNcHitTest, _cntNcHitClient;

    // ==== 闈㈡澘鐨?鎺ヨ緭鍏ュ皬绐?锛堟柟妗?B锛岃 璁″垝-搴曞眰瀵规帴鐣岄潰.md 4.1锛?================
    //
    // 涓轰粈涔堥渶瑕佸畠锛氱┛閫忓繀椤婚潬 WS_EX_TRANSPARENT锛堣法杩涚▼鎵嶇偣寰楀埌涓嬪眰锛夛紝
    // 鑰岄偅涓牱寮忚绐楀彛**鍦ㄧ郴缁熼偅涓€灞傚氨琚帓闄ゅ湪杈撳叆涔嬪**鈥斺€斿疄娴?WM_NCHITTEST
    // 涓€娆￠兘涓嶄細琚皟鐢ㄣ€傛墍浠?闈㈡澘鍙偣"鍜?鐐瑰嚮绌块€?鍦ㄥ悓涓€涓獥鍙ｉ噷鏄簰鏂ョ殑銆?    //
    // 瑙ｆ硶锛氱敾闈㈢収鏃х敾鍦ㄨ鐩栧眰涓婏紙涓嶆柊澧炴覆鏌撹矾寰勩€佷笉鎶㈠眰搴忥級锛屽彟澶栧紑涓€鍧?    // **鍙敹杈撳叆銆佷笉鐢讳笢瑗?*鐨勫皬绐楋紝姝ｅソ鐩栦綇闈㈡澘鐭╁舰銆傜郴缁熸寜绐楀彛鍒嗗彂杈撳叆锛?    // 浜庢槸"绌块€?鍜?闈㈡澘鍙偣"澶╃劧鍚屾椂鎴愮珛銆?    private IntPtr _uiInputHwnd;
    private bool _uiInputShown;
    private RectF _uiInputRect;                 // 鐗╃悊鍍忕礌

    /// <summary>
    /// 鎸囬拡杩欎竴鍒绘槸涓嶆槸鍋滃湪鐣岄潰鑷繁閭ｄ竴鍧椾笂銆?    /// 涓や釜鍦版柟瑕佺敤锛氭偓鍋滄椂鍏夋爣瑕佺粰绠ご锛堜笉鏄瑪灏?姗＄毊鍦堬級锛涚┛閫忔ā寮忎笅
    /// WM_SETCURSOR 瑕佸尯鍒?闈㈡澘涔嬪璁╀笅灞傚喅瀹?涓?闈㈡澘涔嬩笂鎴戜滑鑷繁缁欑澶?銆?    /// 锛坕nternal 鏄负浜嗚嚜妫€鑳芥瀯閫?浠庣敾甯冪洿鍏?/ 浠庡伐鍏锋潯杩涘叆"涓ょ鏉ヨ矾锛岃 --cursortest銆傦級
    /// </summary>
    internal bool _uiHover;

    /// <summary>
    /// 鐧芥澘妯″紡锛氱粰鏁村潡鐢诲竷閾轰竴灞備笉閫忔槑鐨勫簳鑹诧紝閬綇妗岄潰鍜屽埆鐨勭▼搴忋€?    /// 鍏虫帀灏辨槸鍘熸湰鐨?閫忔槑鎵规敞"鈥斺€旂洿鎺ュ啓鍦ㄥ埆浜虹殑 PPT銆佺綉椤典笂闈€?    /// </summary>
    internal bool BoardOn;
    /// <summary>
    /// **鏂囨。鐨?绾?**锛?026-10-09 鐢ㄦ埛瀹氱殑"涓ゅ紶绾?妯″瀷锛夛細鏂囨。寮€鐫€鏃惰嚜鍔ㄩ摵鐨勪竴灞傜櫧搴?    /// 锛堥〉缂?椤佃竟涓嶉湶妗岄潰锛夈€傚畠**鐙珛浜庣櫧鏉?*鈥斺€旂櫧鏉挎槸"鍐嶆潵涓€寮犵焊"锛岀洊鍦ㄦ枃妗ｄ笂闈紱
    /// 涓ゅ紶绾哥殑寮€鍏充簰涓嶅奖鍝嶏紙瑙?Overlay.BoardBrush / SyncDocPages锛夈€?    /// </summary>
    internal Color4 DocPaperColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal Color4 BoardColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal double _lastRebuildMs;
    internal double _lastRecordMs;
    internal double _lastPresentMs;
    // 鍒嗗潡缂撳瓨鐨勭姸鎬侊紙闈㈡澘涓庤嚜妫€璇绘暟锛?    internal int _tilesUsed, _tilesVisible, _tilesBudget, _tilesRasterized;
    /// <summary>鏈€杩戜竴鎷嶉鍙栦簡鍑犲潡锛圚UD 鐢紱瑙佷富寰幆鐨勭┖闂查鍙栵級銆?/summary>
    internal int _prefetchLast;
    private double _inputToPresentMs;
    private double _lastInputMs = -1;

    // ---- 寤舵椂鎺㈤拡锛堣 Latency.cs锛?--------------------------------------
    /// <summary>鎵撳紑鍚庢瘡鐢诲畬涓€甯у氨璁颁竴鏉″欢鏃舵牱鏈€傚彧鏈夋祴閲忔ā寮忎細鎵撳紑銆?/summary>
    internal bool LatencyRecording;
    internal readonly LatencyRecorder Latency = new();
    /// <summary>鏈€杩戜竴鏉℃寚閽堟秷鎭嚜甯︾殑纭欢鏃舵爣锛圦PC 璁℃暟锛屽彲鑳芥槸 0 = 涓嶅彲淇★級銆?/summary>
    private ulong _lastInputPerfQpc;
    /// <summary>鏈€杩戜竴鏉℃寚閽堟秷鎭鎴戜滑鎷垮埌鐨勬椂鍒伙紙QPC锛夈€?/summary>
    private ulong _lastInputMsgQpc;
    /// <summary>宸茬粡璁拌繃鏍风殑閭ｆ潯娑堟伅锛岄伩鍏嶅悓涓€甯ч噸澶嶈銆?/summary>
    private ulong _lastSampledMsgQpc;
    /// <summary>涓婁竴鏉℃寚閽堟秷鎭埌杈剧殑鏃跺埢锛岀敤鏉ラ噺瀹為檯杈撳叆閲囨牱闂撮殧銆?/summary>
    private ulong _prevInputMsgQpc;
    /// <summary>杩欎竴甯ф敹杩涗簡鍑犱釜鎸囬拡閲囨牱鐐广€?/summary>
    private int _framePoints;
    /// <summary>绱娓叉煋甯ф暟銆傛祴閲忔ā寮忕敤瀹冪畻鐪熷疄甯х巼锛堟牱鏈潯鏁?鈮?甯ф暟锛夈€?/summary>
    internal long FrameCounter;
    internal double _nextLogAt;
    private double _accRecord, _accPresent, _accHud, _accStats;
    private int _accFrames;
    private double _nextStatsAt;
    private double _lastStatsMs;
    /// <summary>涓婁竴甯х敾鎬ц兘闈㈡澘鑺变簡澶氫箙銆傞潰鏉胯嚜宸辩殑浠ｄ环涔熻鐪嬪緱瑙侊紝鍚﹀垯瀹冧細鎺╃洊闂銆?/summary>
    private double _lastHudMs;
    private double _hudAvgMs;        // 闈㈡澘浠ｄ环鐨勭獥鍙ｅ潎鍊硷紙鍗曞抚浼氳"閲嶆帓閭ｄ竴甯?甯﹀亸锛?    private double _accPanel;
    private double _hudRedrawMs, _hudRedrawSum, _hudRedrawPerSec;
    private double _hudRateWall;
    private long _hudRedrawCount, _hudRedrawTotal, _hudRedrawAtWall;
    private double _workingSetMb;
    private double _privateMb;
    private double _privateWsMb;      // 涓撶敤宸ヤ綔闆?= 浠诲姟绠＄悊鍣?鍐呭瓨"鍒?    private double _sharedCommitMb;   // 鍏变韩鎻愪氦锛堝伐浣滈泦閲屾湁澶氬皯鏄埆鐨勮繘绋嬩篃鏈夌殑锛?    private double _gpuMb;
    private double _lastTopmostMs;

    /// <summary>Everything that talks to the OS for reporting: process times,
    /// working set, CPU share. Runs a few times a second, never per frame.</summary>
    private void UpdateStats()
    {
        var sw = Stopwatch.StartNew();

        double wall = _clock.Elapsed.TotalMilliseconds;
        // 涓€娆¤皟鐢ㄦ嬁榻愬唴瀛樹笁涓彛寰勶紱CPU 鐢?GetProcessTimes銆?        // 浠ュ墠璧?Process.GetCurrentProcess()+Refresh()锛岄偅鏉¤矾瑕佸嚑鐧惧井绉掑埌鍑犳绉?        // 锛堢粺璁￠偅涓€鏍?3~4 ms 鐨勫ぇ澶村氨鏄畠锛夆€斺€旈潰鏉胯嚜宸辩殑寮€閿€涓嶈璺熻瑙傛祴鐨勪笢瑗夸竴涓噺绾с€?        var c = new Native.PROCESS_MEMORY_COUNTERS_EX2();
        c.cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>();
        bool memOk = Native.GetProcessMemoryInfo(Native.GetCurrentProcess(), ref c, c.cb);
        if (memOk)
        {
            _workingSetMb = c.WorkingSetSize.ToUInt64() / 1048576.0;
            _privateMb = c.PrivateUsage.ToUInt64() / 1048576.0;
            double pws = c.PrivateWorkingSetSize.ToUInt64() / 1048576.0;
            // 鑰佺郴缁熶笂 EX2 鐨勫悗涓や釜瀛楁鏄?0锛氶偅鏃堕€€鍥?宸ヤ綔闆?锛屼笉缂栦竴涓亣鏁板嚭鏉ャ€?            _privateWsMb = pws > 0 ? pws : _workingSetMb;
            _sharedCommitMb = c.SharedCommitUsage / 1048576.0;
        }

        if (Native.GetProcessTimes(Native.GetCurrentProcess(), out _, out _, out long kernel, out long user))
        {
            var cpu = TimeSpan.FromTicks(kernel + user);
            double cpuDelta = (cpu - _lastCpu).TotalMilliseconds;
            double wallDelta = wall - _lastCpuWall;
            if (wallDelta > 250)
            {
                _cpuPercent = cpuDelta / wallDelta / Environment.ProcessorCount * 100.0;
                _lastCpu = cpu;
                _lastCpuWall = wall;
            }
        }

        _gpuMb = GpuUsedMb();
        if (_workingSetMb > _peakWorkingSetMb) _peakWorkingSetMb = _workingSetMb;
        sw.Stop();
        _lastStatsMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Stamped whenever a pointer message is handled, so the loop can
    /// report how long input took to reach the screen.</summary>
    private void StampInput() => _lastInputMs = _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// 鎶?杩欎竴甯ц鐩栫殑閭ｆ潯鏈€鏂拌緭鍏?璁版垚涓€鏉″欢鏃舵牱鏈€傝皟鐢ㄧ偣鍦ㄦ瘡涓獥鍙ｉ兘
    /// Present 涔嬪悗鈥斺€斿彧鏈夊埌閭ｆ椂鍊欐墠鐭ラ亾杩欎竴甯у疄闄呬粈涔堟椂鍊欒浜ゅ嚭鍘汇€?    ///
    /// 鍙**鏂?*杈撳叆锛氬悓涓€甯ч噷鏀跺埌澶氭潯鎸囬拡娑堟伅鏃朵互鏈€鍚庝竴鏉′负鍑嗭紙瀹冩墠鏄?    /// 灞忓箷涓婇偅涓€绗旂殑绗斿皷浣嶇疆锛夛紝宸茬粡璁拌繃鐨勫氨涓嶅啀璁般€?    /// </summary>
    private void RecordLatencySample()
    {
        if (_windows.Count == 0) return;
        if (_lastInputMsgQpc == 0 || _lastInputMsgQpc == _lastSampledMsgQpc) return;
        var w0 = _windows[0];
        if (w0.LastPresentEndQpc < _lastInputMsgQpc) return;   // 杩欎竴甯ц繕娌℃妸杈撳叆浜ゅ嚭鍘?
        _lastSampledMsgQpc = _lastInputMsgQpc;

        // 璁℃椂璧风偣浼樺厛鐢?*閲囨牱鐐硅嚜宸辩殑鏃舵爣**锛岃€屼笉鏄?鎴戜滑璇诲埌瀹冪殑鏃跺埢"銆?        //
        // 鍘熷洜鏄秷鎭細琚郴缁熷悎骞讹細娉ㄥ叆 140 Hz 鐨勮緭鍏ワ紝搴旂敤瀹為檯鍙敹鍒扮害 60 鏉?        // WM_POINTERUPDATE锛堝叾浣欒鍚堝苟杩?historyCount锛夈€傜敤"璇诲埌鏃跺埢"褰撹捣鐐癸紝
        // 浼氭妸"绯荤粺浠€涔堟椂鍊欒偗鎶婃秷鎭粰鎴戜滑"绠楄繘搴旂敤寤舵椂閲岋紝閲忓嚭鏉ヤ竴鍗婄殑甯?        // 鏄?1.5 ms銆佸彟涓€鍗婃槸 16 ms鈥斺€旈偅鏄姇閫掕妭濂忥紝涓嶆槸娓叉煋鎱€?        // 鐢ㄩ噰鏍风偣鏃舵爣褰撹捣鐐癸紝鎵嶆槸"绗斿皷鍔?鈫?杩欎竴甯т氦鍑哄幓"鐨勫共鍑€鍙ｅ緞銆?        bool perfOk = HardwareStampUsable(_lastInputPerfQpc, _lastInputMsgQpc);
        ulong origin = perfOk ? _lastInputPerfQpc : _lastInputMsgQpc;

        var s = new LatencySample
        {
            InputToMsgMs = perfOk ? Qpc.Ms(_lastInputPerfQpc, _lastInputMsgQpc) : double.NaN,
            MsgToPresentMs = Qpc.Ms(origin, w0.LastPresentStartQpc),
            PresentBlockMs = Qpc.Ms(w0.LastPresentStartQpc, w0.LastPresentEndQpc),
            PointsInFrame = _framePoints,
            PresentCount = w0.LastPresentCount,
        };
        // 鍚堣浠?閲囨牱鐐逛骇鐢?绠楄捣锛堟嬁涓嶅埌閲囨牱鐐规椂鏍囧氨閫€鍥?璇诲埌娑堟伅"閭ｄ竴鍒伙級銆?        s.TotalMs = s.MsgToPresentMs + s.PresentBlockMs;

        // 涓婂睆鏃跺埢锛氱獥鍙ｅ寲鍚堟垚浜ゆ崲閾句笂锛孏etFrameStatistics 缁欑殑鏄?鏈€杩戜竴娆＄湡姝?        // 涓婂睆鐨勫抚"銆傚畠鍙兘姣旀垜浠繖涓€甯ф棭锛堣礋鍊硷級鈥斺€旈偅绉嶆儏鍐典笉绠楋紝鏍囪涓轰笉鍙緱銆?        if (w0.LastFrameStatsOk && w0.LastDisplayQpc > w0.LastPresentEndQpc)
        {
            s.PresentToDisplayMs = Qpc.Ms(w0.LastPresentEndQpc, w0.LastDisplayQpc);
            s.HasDisplay = s.PresentToDisplayMs < w0.RefreshPeriodMs * 4;
        }

        Latency.Add(s);
        _framePoints = 0;
    }

    /// <summary>娴嬮噺妯″紡鍦ㄦ瘡涓満鏅紑濮嬪墠璋冪敤锛氭妸鎺㈤拡鐨勬父鏍囨竻骞插噣锛岄伩鍏嶄笂涓€鍦烘櫙鐨?    /// 娈嬬暀鏃舵爣鎶婄涓€鏉℃牱鏈畻鎴?绛変簡涓夌櫨姣"銆?/summary>
    internal void ResetLatencyProbe()
    {
        _lastInputMsgQpc = 0;
        _lastSampledMsgQpc = 0;
        _prevInputMsgQpc = 0;
        _framePoints = 0;
    }

    /// <summary>
    /// 鍒ゆ柇 POINTER_INFO.PerformanceCount 鑳戒笉鑳藉綋 QPC 鐢ㄣ€?    ///
    /// 鐪熷疄鎵嬪啓绗斾笂瀹冩槸纭欢鐨?QPC 鏃舵爣锛屽€煎緱閲囦俊锛涗絾**鍚堟垚杈撳叆锛圫endInput /
    /// 娉ㄥ叆锛変笂瀹冨彲鑳芥槸 0 鎴栧埆鐨勮鏁板櫒**锛岀洿鎺ュ噺浼氬緱鍒拌崚璋殑鏁般€傛墍浠ュ厛鍋?    /// 涓ゆ潯妫€鏌ワ細闈為浂銆佷笖涓嶆櫄浜庢垜浠敹鍒板畠鐨勬椂鍒汇€佷笖宸€煎湪 2 绉掍互鍐呫€?    /// 涓嶉€氳繃灏卞啓 NaN锛屾姤鍛婇噷閭ｄ竴鍒椾細鏄?涓嶅彲寰?锛屼笉浼氭嬁鍋囨暟褰撶粨璁恒€?    /// </summary>
    private static bool HardwareStampUsable(ulong stamp, ulong msgQpc)
        => stamp != 0 && stamp <= msgQpc && (msgQpc - stamp) * Qpc.TicksToMs < 2000.0;

    internal string LastCaptureReport = "";

    // =====================================================================
    //  Startup
    // =====================================================================

    internal int Run(string[] args)
    {
        // 璁剧疆鎺у埗鍙拌緭鍑虹紪鐮併€?        // 銆愪负浠€涔堝繀椤诲寘 try銆戯細鍙戝竷鐗堟槸 GUI 瀛愮郴缁燂紙鏃犳帶鍒跺彴锛夛紝鍙屽嚮鍚姩鏃惰繘绋嬫墜閲?        // 鏍规湰娌℃湁鎺у埗鍙板彞鏌勶紝杩欎釜 setter 浼氳蛋 SetConsoleOutputEncoding 鎶?        // IOException鈥滃彞鏌勬棤鏁堚€濓紝鎶婃暣涓惎鍔ㄦ祦绋嬫墦鏂紙韪╄繃锛氬弻鍑诲彂甯冪増鍚姩澶辫触锛夈€?        // 鏈夋帶鍒跺彴锛堝懡浠よ/甯﹀弬鏁拌嚜妫€锛夋椂鎵嶇湡姝ｇ敓鏁堬紝娌℃帶鍒跺彴灏辫烦杩団€斺€?        // 鍙嶆娌℃帶鍒跺彴鏃舵墍鏈?Console.WriteLine 鏈潵涔熸槸绌烘搷浣滐紝涓嶅奖鍝嶄换浣曞姛鑳姐€?        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Native.EnableMouseInPointer(true);

        Gfx.Init();
        NowMs = _clock.Elapsed.TotalMilliseconds;

        _hInstance = Native.GetModuleHandle(null);
        _className = "InkTeachOverlay_" + Guid.NewGuid().ToString("N");
        if (!RegisterWindowClass())
            return 2;

        string mode = args.Length > 0 ? args[0] : "";
        // 鈿?**鍛戒护琛屽紑鍏充笉鑳藉啓鍦ㄧ涓€涓綅缃?*杩欎釜鍧戯紙2026-10-07 淇級锛?        // 涓婇潰杩欒浼氳 `InkTeach.exe --nowintab` 鎶?*寮€鍏?*褰撴垚"鑷 mode"锛?        // 浜庢槸 `SelfCheckMode = true` 鈥斺€?鎹复鏃惰缃矾寰勩€佸叧鎺夊鍑哄璇濇鈥?        // 鐢ㄦ埛鎯冲鐓т竴涓嬫墜鎰燂紝缁撴灉绋嬪簭杩涗簡鍗婁釜鑷鐘舵€侊紝**鑰屼笖浠栫湅涓嶅嚭鏉?*銆?        // 杩欓噷鎶?涓嶆槸 mode 鐨勫紑鍏?鎺掗櫎鎺夛細鍗曠嫭鏁蹭竴涓紑鍏虫椂锛岃蛋鐨勮繕鏄甯告ā寮忋€?        // 锛堝垽鎹槸姝ｅ悜鐨?宸茬煡寮€鍏?娓呭崟 鈥斺€?鍙垪**涓嶆槸 mode 鐨勯偅浜?*锛?        //   鐪熸暡閿欎簡 mode 鍚嶇殑琛屼负涓嶅彉锛屼粛鏃ц繘鑷妯″紡銆傦級
        if (IsSwitchNotMode(mode)) mode = "";
        SelfCheckMode = mode.Length > 0;
        _modeName = mode;
        // 杩欎竴琛?*涓嶅湪杩欓噷鎵?* 鈥斺€?瑙佷笅闈?update 閲岄偅鍙ユ敞閲娿€?        // 锛堣繖閲屾墦鐨勮瘽锛宍--clean 3` 杩欑"寮€鍏冲啓鍦ㄧ涓€涓綅缃?浼氳璇姤鎴愯嚜妫€妯″紡锛?        //   鑰岀晫闈㈤偅涓€灞傝绛夊埌 `RunModeDispatch` 鎵嶇煡閬撳畠鍒板簳绠椾笉绠?mode銆?        //   2026-10-07 杩欎釜璇姤璁╂垜鐧芥煡浜嗗ソ鍑犺疆锛屾墍浠ユ尓鍒板垽瀹氫箣鍚庛€傦級
        // 浜у搧妯″紡锛氬惎鍔?1.2 绉掑悗锛堢獥鍙ｅ凡缁忛湶闈級鍋氫竴娆″巻鍙叉竻鐞嗭紱鑷涓€寰嬩笉鎵€?        if (mode.Length == 0)
        {
            _historySwept = false;
            _historySweepAtMs = NowMs + 1200;
        }

        // 鐢ㄦ埛鍋忓ソ锛堟繁鑹蹭富棰?璐磋竟闅愯棌/妗ｄ綅/閽変綇锛屼互鍙?鍚姩瑕佷笉瑕佹帴涓婁笂娆＄殑鏉夸功"锛夈€?        //
        // **蹇呴』璇诲湪鎭㈠鏉夸功涔嬪墠**锛氭帴涓嶆帴涓婃鐨勬澘涔︾敱 `ui.restoreInk` 鍐冲畾
        // 锛堥粯璁や笉鎺ワ紝瑙?RestoreAutoSaveIfAny锛夈€?        // 鑷/鍩哄噯妯″紡**涓嶈**鈥斺€斿垽鎹纭畾锛岃€屼笖涓嶈鎷跨敤鎴风殑璁剧疆鍘昏窇鑷銆?        if (InkSettings.PathOverride == null && args.Contains("--nosettings"))
            InkSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                              "inkteach-nosettings.json");
        if (!SelfCheckMode)
            foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
                Console.WriteLine("settings: " + w);

        // 鐐瑰悕鍚嶅崟锛坄%APPDATA%\InkTeach\Names.txt`锛夛細浜у搧妯″紡鍚姩璇讳竴娆★紱
        // 鑷涓嶈鐢ㄦ埛鐨勫悕鍗曪紙鍒ゆ嵁瑕佺‘瀹氾級锛岃鐢ㄥ氨涓存椂璁?NamesPathOverride + ReloadNames銆?        if (mode.Length == 0) LoadClassroomPrefs();

        // 涓婃鍥犱负鐣岄潰鍑洪棶棰橀噸鍚繃锛熸妸鏉夸功璇诲洖鏉ワ紙璇昏蛋灏卞垹锛屽彧鎭㈠涓€娆★級銆?        // 鑷/鍩哄噯妯″紡涓嶆幒鍜岋細閭ｄ簺妯″紡涓嶈琚?涓婃鐣欎笅鐨勬澘涔?褰卞搷鍒ゆ嵁銆?        if (mode.Length == 0) RestoreSessionIfAny();

        // 涓婃鐨勮嚜鍔ㄥ瓨妗ｏ紙宕╂簝/姝ｅ父閫€鍑洪兘鐣欙級锛?*榛樿涓嶆帴**锛岃 RestoreAutoSaveIfAny銆?        if (mode.Length == 0) RestoreAutoSaveIfAny();

        // 瀹夸富鑷繁鐨勫惎鍔ㄥ垎鏀紙寮€鍙戞湡鐨勭偣鍑荤洰鏍囥€佹埅鍥惧伐鍏风瓑锛夈€傝繑鍥?true
        // 琛ㄧず杩欐潯鍛戒护琛屽凡缁忕敱瀹夸富澶勭悊瀹岋紝寮曟搸涓嶅啀寰€涓嬭蛋銆備骇鍝佺晫闈笉闇€瑕佽鍐欍€?        if (PrepareHostStartup(mode, args, out int hostExit))
            return hostExit;

        // PPT 鏀炬槧鑱斿姩锛?*鍙湪浜у搧妯″紡涓嬬湡鐨勬帴**锛堣嚜妫€/鍩哄噯妯″紡涓嶆帴鈥斺€旈偅瑕佺 COM锛?        // 鍒ゆ嵁浼氬彉寰椾笉纭畾锛涜€屼笖鎺㈤拡 `--pptprobe` 鑷繁 new 涓€涓簮鍘婚棶锛?        // 涓嶈蛋杩欐潯璺級銆傛病瑁?Office / 娌″紑 PPT 鏃跺畠浠€涔堥兘涓嶅仛銆?        StartPptLink();

        // --hud锛氭樉寮忔墦寮€璋冭瘯鎬ц兘闈㈡澘锛堥粯璁ゅ叧锛屼氦浜掗噷宸叉病鏈夊紑瀹冪殑蹇嵎閿級銆?        // 娴?鍐欎竴绗旂殑鍐呭瓨/寤舵椂"灏遍潬瀹冿細闈㈡澘涓婂苟鍒?鎻愪氦 / 宸ヤ綔闆?/ 鏄惧瓨 / 绗旂敾鏁般€?        // 鏀炬垚**鏄惧紡寮€鍏?*鑰屼笉鏄敼榛樿锛氭櫘閫氱敤鎴蜂笉璇ョ湅鍒拌繖涓粦妗嗐€?        if (args.Contains("--hud")) ShowHud = true;
        // --nohud锛氬叧鎺夎皟璇曟€ц兘闈㈡澘銆傚畠鏄粰寮€鍙戠湅鐨勶紝姣忓抚瑕佽姳绾?1.9 ms
        // 锛堟枃瀛楁帓鐗?+ 杩涚▼璁℃暟锛夛紝娴嬪簳灞傛€ц兘鏃跺繀椤绘帓闄ゆ帀锛屽惁鍒欓噺鍒扮殑鏄?        // 娴嬮噺宸ュ叿鏈韩鑰屼笉鏄覆鏌撳紩鎿庛€?*涓ゆ潯鍚屾椂缁欐椂浠?--nohud 涓哄噯銆?*
        if (args.Contains("--nohud")) ShowHud = false;
        // --fullpresent锛氭瘡娆℃暣灞忎笂灞忥紙涓嶈蛋 Present1 鑴忕煩褰級銆傝瘖鏂?鍥哄畾妯嚎闂?鐢細
        // 濡傛灉鍔犱笂瀹冨氨涓嶉棯浜嗭紝璇存槑闂鍦?閮ㄥ垎涓婂睆 + DWM 鍚堟垚"杩欐潯璺笂锛堣 Overlay 鐨勮鏄庯級銆?        if (args.Contains("--fullpresent")) OverlayWindow.FullPresent = true;
        // --noprefetch锛氬叧鎺?瑙嗗彛澶栦竴鍦?鐨勭┖闂查鍙栵紝鍥炲埌绾悓姝ュ厜鏍咃紙瀵圭収鐢級銆?        if (args.Contains("--noprefetch")) CanvasTileCache.PrefetchEnabled = false;
        // --traceframes锛氭妸姣忓抚鐨勮剰鍖?涓婂睆/鍒嗗潡/棰勫彇鏃ュ織鎵撳埌鎺у埗鍙帮紙OverlayWindow.Trace锛夈€?        if (args.Contains("--traceframes")) OverlayWindow.Trace = true;
        // 瑙︾偣璇婃柇锛?.3.3锛夛細`--touchhud` 鐩存帴寮€鐫€鍚姩銆?        if (args.Contains("--touchhud")) TouchHud = true;
        // 鍔ㄦ€佹鐨殑鍚庨棬锛?.3.4锛夛細鍏虫帀"閫熷害鈫掑昂瀵?锛屾摝闄ゅ昂瀵告亽瀹氾紙涓嶈繘鐣岄潰锛夈€?        if (args.Contains("--eraserfixed")) DynamicEraser = false;
        // 姗＄毊璇婃柇璇绘暟锛?.3.7锛岃皟鍙傜敤锛夛細宸︿笅瑙掓诞灞傚疄鏃舵樉绀?閫熷害 / 鐩爣 / 褰撳墠绯绘暟 / 灏哄銆?        if (args.Contains("--eraserhud")) { EraserHud = true; RebuildEraserHud(); }

        // 瀵圭収瀹為獙鐢細--nohist 鍏虫帀鑴忓尯鐨勫甯у洖婧紝搴斿綋绔嬪埢鍑虹幇娈嬪奖锛?        // 鐢ㄦ潵璇佹槑娈嬪奖娴嬭瘯鏈韩鏄湁鏁堢殑锛堣€屼笉鏄案杩滈€氳繃锛夈€?        if (args.Contains("--nohist")) OverlayWindow.TransientHistoryFrames = 0;
        // ---- 濮旀墭澧ㄨ抗杞ㄨ抗锛堢瑪 / 榧犳爣 / 瑙︽懜閮藉杺锛?------------------------------
        //
        // 2026-10-11 璧?*榛樿鎬绘槸寮€**锛堥櫎闈?`--noinktrail`锛夛細鏈満瀹冨凡缁忕湡鐨勫湪鐢讳簡
        //锛堜互鍓嶄笉鐢荤殑鏍瑰洜鏈変袱澶勶細杞ㄨ抗瀵硅薄娌℃寕杩涜瑙夋爲銆佹瘡甯ф病鎶?宸蹭笂灞忕殑鐐?缁撹处鍒犳帀锛?        //  瑙?Overlay.cs 鐨?TrimInkTrailAsDrawn / 璇曢獙C锛夈€?        // 浠ュ墠鐨勯棬妲涙槸"绯荤粺閲屾湁绗旇澶囨墠寮€"鈥斺€旂幇鍦ㄩ紶鏍?瑙︽懜涔熷杺鐐癸紝閭ｄ釜闂ㄦ娌℃剰涔変簡
        //锛堟墜鍐欐澘鍏?ink 鏃跺氨鏄?PT_MOUSE锛涜Е鎽稿睆鍙湁 PT_TOUCH锛夈€?        // 瀹炴祴锛?026-10-11锛岄€愬抚閲囨牱锛夛細榧犳爣**涔熺敾**锛堟嫋鍔ㄤ腑宄板€?163 鍍忕礌鐨?灏惧反"锛夛紱
        // 鍏堝墠"0 鍍忕礌"鏄噰鏍峰お绋€婕忔帀浜嗗畠锛堝熬宸村彧瀛樻椿鍒版垜浠笅涓€娆′笂灞忥級銆?        // 绗?榧犳爣/瑙︽懜閮藉杺锛堟瘡娑堟伅鍑犳 COM 璋冪敤锛屼唬浠峰彲蹇界暐锛夈€?        // ---- 婀垮ⅷ妗ｄ綅锛?..3锛?--------------------------------------------------
        // 椤哄簭锛?*鍏堣璁剧疆閲岃鐫€鐨勬。**锛堟病鏈?= 0 鍘熻矾寰勶級锛屽啀鐪?`--wetmode N`
        //锛堣鐩?+ 璁颁綇锛夛紝鏈€鍚庤鑰佺殑寮€鍙?璇婃柇寮€鍏宠鐩栵紙`--inktrail` / `--noinktrail`
        // 鐨勫垽鎹€愬瓧涓嶅彉锛岃嚜妫€涓庡鐓у叏鐓ф棫锛夈€?        WetMode = int.TryParse(GetUiPref(WetModePrefKey), out int wmPref) ? Math.Clamp(wmPref, 0, 3) : 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--wetmode" && int.TryParse(args[i + 1], out int wmArg))
            {
                WetMode = Math.Clamp(wmArg, 0, 3);
                // 鈿?杩欓噷**涓嶈兘**鐢?`SelfCheckMode` 鎶婂叧锛氳繖涓€姝ヨ窇鍦?RunModeDispatch 涔嬪墠锛?                // 閭ｄ釜鏍囧織杩樹笉鍙俊锛堝拰 wintab 鍋忓ソ鍚屼竴涓潙锛岃涓嬮潰 _pendingWintabPref 鐨勬敞閲婏級銆?                // 鍐欎笉鍐欑洏浜ょ粰 Loop() 寮€澶撮偅涓€娈靛垽锛堥偅鏃舵爣蹇楀凡缁忓畾涓嬫潵浜嗭級銆?                _pendingWetModePref = WetMode.ToString();
            }
        ApplyWetMode();   // 姝ゆ椂绐楀彛杩樻病寤猴紙_windows 涓虹┖锛夛紝杩欎竴涓嬪彧钀戒袱涓潤鎬佸紑鍏炽€?        if (args.Contains("--inktrail")) OverlayWindow.InkTrailEnabled = true;
        if (args.Contains("--noinktrail")) OverlayWindow.InkTrailEnabled = false;

        // `--trailonly`锛堟墜鍔ㄨ瘖鏂紝2026-10-11锛夛細**鎴戜滑鑷繁涓嶇敾"姝ｅ湪鍐欑殑杩欎竴绗?**鈥斺€?        // 浜庢槸鎸変綇鏈熼棿灞忓箷涓婂嚭鐜扮殑浠讳綍澧ㄥ氨鍙兘鏄郴缁熻建杩圭敾鐨勩€傜瑪/榧犳爣/瑙︽懜閮借兘鐢紝
        // 鐢ㄦ潵鎵嬪姩鍥炵瓟"绯荤粺鍒板簳缁欏摢绉嶈緭鍏ョ敾杩欐潯杞ㄨ抗"锛堥厤鍚?`[绗旂敾]` 琛岀殑 `[杞ㄨ抗 鍠侼 缁撹处M]`锛夈€?        if (args.Contains("--trailonly")) SuppressActiveStroke = true;
        // `--trailcommit`锛圓/B锛?026-10-11 璇曢獙D锛夛細姣忓抚鎻愪氦涓€娆?DComp锛圕hromium 鐨勫仛娉曪級銆?        if (args.Contains("--trailcommit")) OverlayWindow.TrailCommitEveryFrame = true;

        // `--syswet`锛氱湡绗?+ 瀹炵嚎绗旀椂锛?*婀垮ⅷ鍙绯荤粺杞ㄨ抗鐢?*锛堟垜浠笉鍐嶈嚜缁樺綋鍓嶇瑪鐢伙級銆?        // 鈿?2026-10-05 鐪熺瑪瀹炴祴锛堝垽鎹慨濂藉悗锛夛細**鏈満鐨勫鎵樿建杩逛笉娓叉煋**鈥斺€擟OM 璋冪敤鍏ㄩ儴
        //    鎴愬姛銆佺偣涔熷杺杩涘幓浜嗭紝浣嗗啓瀛楁湡闂村睆骞曚笂娌℃湁浠讳綍鏂板澧紙鐢ㄦ埛浜茬溂纭锛?        //    "鍐欑殑鏃跺€欑湅涓嶈澧ㄨ抗锛屾姮绗旀墠浼氬嚭鐜?锛夈€傛墍浠?`--syswet` 鍦ㄦ湰鏈哄彧浼氳婀垮ⅷ娑堝け锛?        //    **榛樿鍏炽€佸嬁鍚敤**锛涗繚鐣欏畠鐢ㄤ簬鎹㈡満鍣?鎹㈠钩鏉跨殑楠岃瘉銆傝 `寤舵椂-瀹炴祴涓庝紭鍖?md` 搂鍏€?        // `--ownwet` 寮哄埗鍥炶€佽矾锛圓/B 鐢級銆?        OverlayWindow.SystemWetPreferred = args.Contains("--syswet");
        if (args.Contains("--ownwet")) OverlayWindow.SystemWetPreferred = false;
        // 鍙礋璐?鍏?锛堟。浣嶅凡缁忓湪涓婇潰钀借繃锛涜繖閲屽啀鍐?`!Contains` 浼氭妸妗ｄ綅 0/1 鐨勫簲鐢ㄦ箍澧ㄥ眰鎵撳紑锛夈€?        if (args.Contains("--nofastwet")) OverlayWindow.FastWetFallbackEnabled = false;

        // PPT 杩炴帴璇婃柇锛?-pptdebug锛夛細鎶?ROT 鎵弿姣忎竴姝ユ墦鍑烘潵锛堟帓鏌ョ敤锛屽钩鏃跺叧锛夈€?        PptComSource.Debug = args.Contains("--pptdebug");
        ComLate.Debug = PptComSource.Debug;

        // ---- 绗旇抗棰勬祴锛氫笁妗ｆ敹鍙ｏ紙2026-10-15锛?---------------------------------------
        // [鍒犻櫎 2026-10-05] `--predict/--predictms/--predictlead`锛堣€侀娴嬬郴缁燂級涓?        // `--predicttip`锛堥娴嬬偣骞跺叆 mean2锛夌殑鍏ュ彛銆佸杺鐐逛笌娓叉煋灏炬帴绾垮凡鍏ㄩ儴绉婚櫎銆?        // [2026-10-15] 缁忓吀 B4 / Google / DL锛坄--predict3`锛夐€€褰广€佷笉杩涚紪璇戯紱鍞竴瀹炵幇 = v6銆?        // 澶囦唤 `.revert/2026-10-15-棰勬祴鏀跺彛/`锛岃鏄?`璋冪爺/宸插仠鐢?棰勬祴绠楁硶鏀跺彛.md`銆?
        // ---- 涔﹀啓鏈熼棿鐨?GC 浣庡欢杩熸。 -------------------------------------------
        //
        // 榛樿**寮€**锛氫功鍐欐湡闂寸敤 SustainedLowLatency 鎶戝埗"鍓嶅彴绗?2 浠ｅ洖鏀?
        //锛堥偅鏄渶閲嶇殑涓€绉嶅洖鏀讹紝涓€娆″嚑鍗佹绉掞紝钀藉湪涔﹀啓涓棿灏辨槸"绗旂獊鐒跺崱涓€涓?锛夈€?        // 璇﹁ GcLatency.cs 鐨勮鏄庝笌涓や釜鍧戙€?        //
        // `--nogclatency` 鍏虫帀瀹冿紝涓撻棬鐢ㄦ潵鍋?寮€ / 涓嶅紑"鐨勫鐓ф祴閲忊€斺€?        // 浣庨厤鏈轰笂鍒板簳鏄笉鏄畠瀵瑰崱椤挎湁鐢紝寰楅潬杩欎釜寮€鍏抽噺锛屼笉鑳藉嚟鎰熻銆?        GcLatency.Enabled = !args.Contains("--nogclatency");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--gchold" && double.TryParse(args[i + 1], out double gh))
                GcLatency.HoldMs = Math.Clamp(gh, 0, 60000);

        // ---- 鍘嬫劅 鈫?绮楃粏 ------------------------------------------------------
        //
        // 榛樿**寮€**锛氳澶囨姤鍘嬪姏灏辨寜鍘嬪姏鏀圭矖缁嗏€斺€斿拰绯荤粺澧ㄨ抗锛圱ablet PC / OneNote 閭ｄ竴妗ｏ級
        // 鍚屼竴涓彛寰勶細鏈€灏忓帇鍔?50%銆佹渶澶?150%锛堣 PressureWidth锛夈€?        //
        // 涓や釜寮€鍏抽兘鏄?*缁欑湡鏈鸿皟鎵嬫劅鐢ㄧ殑**锛屼笉鏄粰鐢ㄦ埛骞虫椂鎸夌殑锛?        //   路 `--nopressure` 鍏虫帀锛岀敤鏉ュ仛"鏈?鏃?瀵圭収锛堝樊寮傝褰撳満鐪嬪緱鍑烘潵鎵嶇畻鏁帮級锛?        //   路 `--pressrange min,max[,gamma]` 鐜拌皟鍔ㄦ€佽寖鍥翠笌鏇茬嚎锛屼笉鐢ㄩ噸缂栥€?        _noPressureArg = args.Contains("--nopressure");
        PressureWidth.Enabled = !_noPressureArg;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--pressrange") continue;
            var parts = args[i + 1].Split(',');
            if (parts.Length >= 2
                && float.TryParse(parts[0], out float pmin)
                && float.TryParse(parts[1], out float pmax)
                && pmax > pmin)
            {
                PressureWidth.Min = Math.Clamp(pmin, 0.05f, 2f);
                PressureWidth.Max = Math.Clamp(pmax, 0.10f, 3f);
                if (parts.Length >= 3 && float.TryParse(parts[2], out float pg) && pg > 0.05f)
                    PressureWidth.Gamma = Math.Clamp(pg, 0.1f, 4f);
            }
        }

        // ---- 鐢ㄦ埛寮€鍏筹細鍘嬫劅绮楃粏锛?026-10-01锛屻€屾洿澶?鈫?璁剧疆 鈫?涔﹀啓銆嶏級----------
        //
        // 榛樿**寮€**锛涘亸濂藉彧鍐?鍏宠繃"鐨勯偅涓€浠斤紙`ui.pressure = "0"`锛夈€?        // **鍛戒护琛屼紭鍏?*锛歚--nopressure` 鏄粰"鏈?鏃?瀵圭収瀹為獙鐢ㄧ殑锛屽畠瀛樺湪鏃朵笉鍚亸濂解€斺€?        // 鍚﹀垯鑷/瀹為獙鏈哄櫒涓婅鍒扮殑鐢ㄦ埛鍋忓ソ浼氭妸瀵圭収鏉′欢鎮勬倓鏀规帀銆?        // 鑷妯″紡涓?`UiPrefs` 鏍规湰娌′粠鐩樹笂璇伙紙瑙佷笂闈?LoadUiPrefs 閭ｄ竴娈碉級锛?        // 鎵€浠ヨ繖閲屾嬁鍒扮殑姘歌繙鏄┖ 鈫?Enabled 灏辨槸鍛戒护琛?榛樿鍊硷紝鍒ゆ嵁绋冲畾銆?        if (!_noPressureArg && GetUiPref(PressurePrefKey) == "0")
            PressureWidth.Enabled = false;

        // ---- 绮剧粏绗旇抗锛?026-10-07 鍔犲紑鍏筹紱**2026-10-09 鏅氱敤鎴峰畾锛氬紑鍏虫挙鎺夈€佸父寮€**锛?-----
        //
        // 瀹冪鐨勬槸銆?*鍘熷杈撳叆琛ョ偣**銆嶏細绯荤粺浼氭妸鏉ヤ笉鍙婃姇閫掔殑绉诲姩鍚堝苟鎴愪竴鏉℃秷鎭紝
        // 鍙彇鏈€鏂伴偅涓€涓瓑浜庢妸閲囨牱鐜囩爫鍗婏紙鐪熸満瀹炴祴锛氭寚閽堟秷鎭?61Hz锛岃€岃澶囧疄闄呮姤浜?190Hz锛夈€?        // 琛ョ偣灏辨槸鎸夊師濮嬫姤鐜囨妸涓棿鐐规崬鍥炴潵銆傜敤鎴风湡鏈虹敤浜嗕竴鏁村ぉ锛?姣旇緝绋冲畾浜?鈥斺€?        // 涓嶅啀缁欑敤鎴峰叧鐨勫紑鍏筹紙璁剧疆椤甸偅涓€琛屽凡鎾わ級銆傚彧鍓╁紑鍙戝鐓э細`--norawinput` 浠嶅彲鍏炽€?        if (_noRawInputArg)
            RawInputCapture = false;

        // ---- Wintab锛?026-10-07 鐢ㄦ埛鎷嶆澘锛?*榛樿鍏?*锛?-----------------------
        // 鈿?**鍙湪杩欎竴澶勫喅瀹?*銆備箣鍓嶆垜鍦ㄤ袱涓湴鏂瑰悇鍐欎簡涓€鍗婏紙杩欓噷璇诲亸濂姐€佷笅闈㈠弬鏁板潡閲?        // 鍙堟棤鏉′欢 `WintabEnabled = false`锛夛紝鍚庡啓鐨勬妸鍏堝啓鐨?*鏁翠釜鐩栨帀** 鈥斺€?        // "璁颁綇"閭ｆ潯閾惧瓙褰撳満澶辨晥銆?*鍚屼竴浠朵簨鍐欏湪涓ゅ = 涓€瀹氬嚭閿?*锛屼粖澶╁凡缁忔牻杩囦竴娆′簡銆?        //
        // 浼樺厛绾э細鍛戒护琛岋紙`--wintab` / `--nowintab`锛岄『鎵嬫妸鍋忓ソ鍐欎笅鏉ワ級锛?鍋忓ソ 锛?榛樿锛堝叧锛夈€?        // **娌℃湁鐣岄潰** 鈥斺€?瀹冩槸鍐呴儴鏈哄埗锛屼笉鏄粰鐢ㄦ埛璋冪殑鍋忓ソ銆?        {
            bool wantOn = GetUiPref(WintabPrefKey) == "1";
            if (args.Contains("--nowintab")) { wantOn = false; _pendingWintabPref = "0"; }
            else if (args.Contains("--wintab")) { wantOn = true; _pendingWintabPref = "1"; }
            WintabEnabled = wantOn;
            WintabForced = wantOn;
            // 鈿?**鍋忓ソ涓嶈兘鍦ㄨ繖閲屽啓鐩?*锛氭瀯閫犲嚱鏁拌窇鍦?`RunModeDispatch` 涔嬪墠锛?            // 閭ｆ椂 `SelfCheckMode` 杩樻槸"瑙侀潪绌哄弬鏁板氨鐪?鐨勮€佸垽鎹紙`--wintab` 娌＄櫥璁拌繃锛?            // 浼氳褰撴垚 mode锛夆啋 `FlushSettings()` 涓€鐪嬫槸鑷妯″紡灏?*鐩存帴杩斿洖锛屼粈涔堥兘涓嶅啓**銆?            // 鎵€浠ュ彧璁颁笅"寰呭啓"锛岀瓑 `Loop()` 閲屽垽瀹氬畬浜嗗啀鍐欙紙鍜岄偅琛岃瘖鏂悓涓€涓亾鐞嗭級銆?        }

        // ---- 妯℃嫙鍘嬪姏涓庣瑪閿嬶細**宸插叏閮ㄥ仠鐢?*锛?026-10-05锛屼唬鐮佷繚鐣欙級----------------
        // [鍋滅敤] `--simpressure/--simpressdepth`锛圶ournal++ 閫熷害鍘嬪姏锛夈€?        // `--pfpressure/--pfthinning/--pfstreamline`锛坧erfect-freehand 閫熷害鍘嬪姏锛夈€?        // `--simtaper`锛堝浐瀹氫袱绔敟锛夈€乣--flicktip`锛堟湯灏剧敥閫熸敹灏栵紝鍏ュ彛宸插垹闄わ級銆?        // 鎭㈠鏂规硶瑙?`宸插仠鐢?娓叉煋瀹為獙.md`锛涘仠鐢ㄥ墠瀹屾暣婧愮爜鍦?`.revert/2026-10-05-娓叉煋鍑忔硶/`銆?        PressureSim.Enabled = false;
        PressureSim.UsePf = false;
        // 锛堣繖閲屽師鏉ヨ繕鏈夊悇寮€鍏崇殑瑙ｆ瀽涓?flicktip 閫熷害闂ㄦ帶鍧楋紱鍋滅敤涓庡垹闄ょ殑鍘熸枃瑙佺櫥璁版枃妗ｃ€傦級
        PressureSim.BumpVersion();

        // ---- 涓績绾挎洸绾垮寲锛堣繃鐐?Catmull-Rom 锛?瑙掔偣淇濇姢锛?-----------------------
        //
        // **榛樿寮€**锛?026-09-28 鐢ㄦ埛鎷嶆澘锛?榛樿寮€涔熸病鍏崇郴"锛夛細鎶?閫愮偣鐩寸嚎娈?鎹㈡垚
        // "杩囨瘡涓€涓噰鏍风偣鐨勪笁娆¤礉濉炲皵"锛屾€ヨ浆澶勭敱瑙掔偣淇濇姢鍒囨柇鍒囩嚎銆?        // **涓嶆敼瀛樻。閲岀殑鐐广€佷笉鏀瑰舰鐘惰涔?*鈥斺€旀洸绾夸弗鏍艰繃鐐癸紝鐩磋/椤跨瑪/灏栬鍘熸牱淇濈暀锛?        // 姝ｅ湪鍐欑殑閭ｄ竴绗旇蛋鎶樼嚎锛坄Stroke.RawWhileLive`锛夛紝钀界瑪閭ｄ竴鍒绘墠鎹㈡垚鏇茬嚎銆?        //
        //   --nosmooth          閫€鍥炴姌绾匡紙鍋?寮€ / 鍏?瀵圭収鐢級
        //   --smoothcorner N    瑙掔偣闃堝€硷紙搴︼級銆傞粯璁?35锛氳浆寰楁瘮瀹冩€ュ氨淇濈暀灏栬銆?        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--smoothcorner" && float.TryParse(args[i + 1], out float sc))
            {
                StrokeSmoothing.CornerAngleDeg = Math.Clamp(sc, 5f, 90f);
                StrokeSmoothing.BumpVersion();
            }
            // [鍋滅敤 2026-10-05] `--smoothmacropx`锛堝畯瑙傝鐐圭獥锛夛細褰撳ぉ瀹炴祴鍙?鑴?锛屽凡鍥為€€榛樿 0銆?            // 鎭㈠瑙?`宸插仠鐢?娓叉煋瀹為獙.md`銆?            // else if (args[i] == "--smoothmacropx" && float.TryParse(args[i + 1], out float mp) && mp >= 0)
            // {
            //     StrokeSmoothing.CornerMacroPx = Math.Clamp(mp, 0f, 200f);
            //     StrokeSmoothing.BumpVersion();
            // }
        }

        // ---- 绗旇抗杩愬姩妯″瀷锛?026-10-03 瀵圭収鍙?鈫?2026-10-05 鏀舵暃锛?-----------------
        // 淇濈暀涓や釜妯″紡锛?*mean2锛堥粯璁わ紝M6锛?* 涓?**catmull锛圡1 鑰佽矾寰勶紝瀵圭収/鍏滃簳锛?*銆?        // [鍋滅敤 2026-10-05] raw / sliding / spring / oneeuro / mean / gauss锛?        // 鍙?`--nosmooth`銆乣--inkmodel`銆乣--inkm*`銆乣--slidewin`銆乣--motionwin` 绛夊弬鏁帮細
        // 浠ｇ爜淇濈暀锛圫trokeMotion 鍐呭搴斿垎鏀湭鍔級锛屾仮澶嶈 `宸插仠鐢?娓叉煋瀹為獙.md`銆?        // 浠嶇劧**鍙仛娓叉煋鏈熷姞宸?*锛氬瓨妗ｉ噷鐨勭偣銆佸懡涓€佹挙閿€銆佹鐨竴姒備笉鍔ㄣ€?        {
            var motionMode = StrokeMotionMode.Mean2;   // 2026-10-04 榛樿妗ｏ紙鐢ㄦ埛瀹氱锛?            // **杩囩偣鏇茬嚎鍖栨€诲紑鍏?*锛坄--smooth` / `--nosmooth`锛夛細2026-10-06 鎺ュ洖鏉ョ殑銆?            // 瀹冧箣鍓嶈娉ㄩ噴鎺夈€佽€?`StrokeMotion.SetMode` 鍙堟妸瀹冩寜妯″紡寮哄埗鍏虫帀锛?            // 缁撴灉鏄?鏂囨。璇撮粯璁ゅ紑銆乣--nosmooth` 鏄鐓у紑鍏?锛屽疄闄?*姘歌繙鍏崇潃**锛?            // `--nosmooth` 娌℃湁浠讳綍鏁堟灉锛宍--smoothshow` 鍑虹殑 off/on 涓ゅ紶鍥鹃€愬儚绱犵浉鍚?            // 锛堝疄娴嬶細涓嶅悓鐐规暟 = 0锛夈€傜幇鍦ㄥ畠鍙瀹冭绠＄殑鈥斺€旀洸绾垮寲寮€涓嶅紑銆?            if (args.Contains("--nosmooth")) StrokeSmoothing.SetEnabled(false);
            if (args.Contains("--smooth")) StrokeSmoothing.SetEnabled(true);
            // 鑷粯棰勬祴灏撅紙2026-10-08 鐢ㄦ埛 90Hz 鐪熸満楠屾敹 鈫?2026-10-09 鏀归粯璁ゅ叧 鈫?            // **2026-10-14 鍙戝竷娴嬭瘯鐗堣捣鍙堟敼榛樿寮€**锛夈€?026-10-15锛氭帴鍏ョ嚎鎬ч娴?            //锛圕hromium 鍚屾棌锛夈€乿6 閫€褰癸紙瑙?`璋冪爺/宸插仠鐢?棰勬祴绠楁硶鏀跺彛.md`锛夈€?            // 璁剧疆椤点€屽ⅷ杩归娴嬨€嶅紑鍏筹紝鍋忓ソ钀界洏 `ui.predict2`锛堥粯璁ゅ紑锛屽彧鍐?鍏宠繃"鐨勯偅涓€浠斤級锛?            // `--nopredict2` 鍏炽€乣--predict2` 寮哄紑锛屼袱鑰呴兘缁欏鐓у疄楠岀敤銆佷紭鍏堜簬鍋忓ソ銆?            // **鏃堕暱涓嶅彲璋?*锛堢敤鎴?2026-10-15 瀹氾級锛氬湴骞崇嚎鍥哄畾 21.3ms锛岃 InkPredictorLinear銆?            _noPredictArg = args.Contains("--nopredict2");
            _forcePredictArg = args.Contains("--predict2");
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--wetpred" && float.TryParse(args[i + 1], out float wp))
                    _wetPredScale = Math.Clamp(wp, 0f, 1f);
            ApplyPredictTailSetting();
            // [鍋滅敤] if (args.Contains("--inkmodel")) motionMode = StrokeMotionMode.Spring;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--motion") continue;
                motionMode = args[i + 1].ToLowerInvariant() switch
                {
                    // [鍋滅敤] "raw" or "m0" => StrokeMotionMode.Raw,
                    "catmull" or "m1" => StrokeMotionMode.Catmull,
                    // [鍋滅敤] "sliding" or "m2" => StrokeMotionMode.Sliding,
                    // [鍋滅敤] "spring" or "m3" => StrokeMotionMode.Spring,
                    // [鍋滅敤] "oneeuro" or "m4" => StrokeMotionMode.OneEuro,
                    // [鍋滅敤] "mean" or "m5" => StrokeMotionMode.Mean,
                    "mean2" or "m6" => StrokeMotionMode.Mean2,
                    // [鍋滅敤] "gauss" or "m7" => StrokeMotionMode.Gauss,
                    _ => motionMode,
                };
            }
            // InkModel.ApplyParamsFromArgs(args);      // M3 鍙傛暟锛?-inkm*锛塠鍋滅敤]
            StrokeMotion.ApplyParamsFromArgs(args);  // mean2 鍙傛暟锛?-mean2win 绛夛級
            // 绐楀彛鍒ゆ嵁鐨勮噦闀夸笂闄愶細`--smoothcornerpx N`锛? = 鍏虫姢鏍忥紝鐢ㄤ簬鍋?淇墠/淇悗"瀵圭収锛?            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--smoothcornerpx" && float.TryParse(args[i + 1], out float scp))
                {
                    StrokeSmoothing.CornerWindowMaxPx = Math.Clamp(scp, 0f, 400f);
                    StrokeSmoothing.BumpVersion();
                }
            }
            // 鍑犱綍璺緞璇婃柇锛坄--geomtrace`锛夛細姣忎竴绗旀姤涓€娆¤蛋浜嗗摢鏉″矓璺?            if (args.Contains("--geomtrace")) GeomTraceOn = true;
            // 杞杩炵画鎬ч槇鍊硷紙`--smoothcornerdeg N`锛? = 鍏筹紝鍋?淇墠/淇悗"瀵圭収锛?            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--smoothcornerdeg" && float.TryParse(args[i + 1], out float scd))
                {
                    StrokeSmoothing.CornerSmoothDeg = Math.Clamp(scd, 0f, 90f);
                    StrokeSmoothing.BumpVersion();
                }
            }
            if (args.Contains("--cornerdump")) StrokeSmoothing.DumpTurns = true;
            if (args.Contains("--notrend")) StrokeSmoothing.CornerUseTrend = false;
            // 鍘熷杈撳叆榛樿**寮€**锛?026-10-07 鐢ㄦ埛瀹氾細"寮€涓嶅紑 ink 瑕佹湁涓€鏍风殑鎵嬪啓浣撻獙"锛夈€?            // `--norawinput` 鍏虫帀鍋氬鐓э紱`--rawprobe` 鍗曠嫭鎵撳紑"鍙暟鏉℃暟"鐨勮瘖鏂€?            _noRawInputArg = args.Contains("--norawinput");
            RawInputCapture = !_noRawInputArg;
            RawProbeEnabled = RawInputCapture || args.Contains("--rawprobe");
            // Wintab 榛樿**寮€**锛?026-10-07锛夛細鍏?ink 鏃剁瑪琚綋榧犳爣鎶ャ€侀偅鏉¤矾涓婃病鏈夊帇鍔涳紝
            // Wintab 鏄┍鍔ㄨ嚜宸辩殑閫氶亾锛岃兘琛ュ洖鍘嬪姏锛堝疄娴?0~16383锛屾瘮 Windows Ink 鐨?1024 缁?16 鍊嶏級銆?            // `--nowintab` 鍏虫帀鍋氬鐓с€?*鍙湪鏈夊巶鍟嗛┍鍔ㄧ殑鏉垮瓙涓婃湁鏁?*锛屾病鏈夊氨鑷姩涓嶅惎鐢ㄣ€?            // Wintab **榛樿鍏?*锛?026-10-07 鐢ㄦ埛瀹氱殑锛夈€?            //
            // 娌块潻锛氬厛榛樿寮€锛堟兂璁?鍏?ink 涔熸湁鍘嬫劅"锛夛紝鍚庢潵鐢ㄦ埛瀹炴祴"鍏?ink 蹇€熺敾鍦嗘椂寰堣剰"
            // 鈥斺€?Wintab 鍙€熷帇鍔涖€佷笉鍊熷潗鏍囷紝鎵€浠ュ舰鐘舵病闂锛?*鑴忓湪"瀹藉害涓€璺充竴璺?**銆?            // 鏉冭　涔嬪悗鐢ㄦ埛鎷嶆澘锛?*榛樿鍏筹紝涓嶇暀鐣岄潰锛屼互鍚庢湁闇€瑕佸啀寮€**銆?            // 杩欑鍚堣繖涓」鐩竴璐殑瑙勭煩锛?*淇濆畧浼樺厛**銆?            //
            // 瑕佸紑鐨勮瘽锛歚--wintab`锛?*浼氳浣?*锛岃涓嬮潰 WriteUiPref 鈥斺€?涓嶇敤姣忔鏁诧級銆?            // 鈿?Wintab 鐨勫紑鍏?*鍦ㄤ笂闈㈤偅涓€澶勭粺涓€鍐冲畾**锛堝埆鍦ㄨ繖閲屽啀鍐欎竴閬?鈥斺€?            // 涔嬪墠灏辨槸涓ゅ鍚勫啓涓€鍗婏紝"璁颁綇"閭ｆ潯閾惧瓙褰撳満琚洊鎺夛級銆?            // `--clean N`锛氬叆绗斿墠瀵逛綅缃笌鍘嬪姏鍋?N 鐐规粦鍔ㄥ钩鍧囷紙0 = 鍏抽棴锛岄粯璁わ級銆?            // 瑙?AddPointCleaned 閭ｆ娉ㄩ噴锛氭不"缁嗘瘺鍒?鍜?鍘嬪姏鍙伴樁"锛屾粸鍚庢瘮鍔犲ぇ璺濈绐楀皬寰楀銆?            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--clean" && int.TryParse(args[i + 1], out int cw))
                    CleanWindow = Math.Clamp(cw, 0, 15);
            // `--strokefile`锛氭妸姣忎竴绗旂殑 [绗旂敾] 鎶ュ憡**杩藉姞鍒版枃浠?*锛堥粯璁ゅ叧锛岄浂寮€閿€锛夈€?            // 缁?璋冩墜鎰?鐢細閭ｄ竴琛岄噷鐨?鍘嬪姏 a~b/涓婇檺"鏄敮涓€渚濇嵁锛?            // 鑰屾埅鍥?杞堪閮戒細涓俊鎭紙浠婂ぉ鍥犳鏍借繃涓夋锛夈€?            // 涓嶇粰璺緞灏辩敤宸ヤ綔鐩綍涓嬬殑 `绗旂敾鏃ュ織.txt`銆?            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--strokefile") continue;
                string p = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "";
                _strokeFilePath = p.Length > 0 ? p : System.IO.Path.Combine(
                    Environment.CurrentDirectory, "绗旂敾鏃ュ織.txt");
                Console.WriteLine($"  绗旂敾鏃ュ織 鈫?{_strokeFilePath}");
            }
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--rawmindist" && float.TryParse(args[i + 1], out float rmd))
                    RawMinDistPx = Math.Clamp(rmd, 0f, 50f);
                if (args[i] == "--rawmax" && int.TryParse(args[i + 1], out int rmx))
                    RawMaxPerStroke = Math.Clamp(rmx, 0, 100000);
            }
            StrokeMotion.SetMode(motionMode);
            InkModel.SetEnabled(false);              // M3 寮圭哀 [鍋滅敤 2026-10-05]

            string modeDesc = StrokeMotion.Mode switch
            {
                StrokeMotionMode.Catmull => $"catmull锛圡1锛氳繃鐐规洸绾匡紜瑙掔偣淇濇姢锛岃鐐归槇鍊?{StrokeSmoothing.CornerAngleDeg}掳锛涙椿绗旇蛋鎶樼嚎锛?,
                _ => $"mean2锛圡6锛氳窛绂荤獥 {StrokeMotion.Mean2WindowPx:F0}px 锛?杩囩偣鏇茬嚎 锛?鏀剁瑪杩借刀"
                     + (StrokeMotion.Mean2Gauss
                         ? $" 锛?楂樻柉鏉兿?{StrokeMotion.Mean2SigmaSlow:F1}鈫抺StrokeMotion.Mean2SigmaFast:F1}px"
                           + (StrokeMotion.Mean2TipBlendMax > 0f ? $" 锛?娣风瑪灏杮StrokeMotion.Mean2TipBlendMax:F2}" : "")
                         : "")
                     + "锛?,
            };
            Console.WriteLine($"绗旇抗杩愬姩妯″瀷: {modeDesc}");
        }

        // ---- D1锛氫簹鍍忕礌杈撳叆锛堥粯璁ゅ紑锛沗--nohimetric` 閫€鍥?D0 瀵圭収锛?-------------
        // 2026-10-08锛氱湡鏈洪獙鏀讹紙鐢ㄦ埛鎵嬪啓鏉?himetric 鏄犲皠 682/851 鐐广€? 閫€鍥烇級鈫?榛樿寮€銆?        // 鎷夸笉鍒?himetric 鐨勮澶囷紙澶氭暟榧犳爣/瑙︽懜锛夐€愮偣鑷姩閫€鍥炴暣鏁板儚绱狅紝琛屼负涓?D0 涓€鑷淬€?        InputPrecision.Reset();
        InputPrecision.UseHimetric = !args.Contains("--nohimetric");
        Console.WriteLine(InputPrecision.UseHimetric
            ? "杈撳叆绮惧害: himetric 浜氬儚绱狅紙榛樿锛涢€愮偣鎷夸笉鍒版椂鑷姩閫€鍥炴暣鏁板儚绱狅紱--nohimetric 瀵圭収锛?
            : "杈撳叆绮惧害: 鏁存暟鍍忕礌锛圖0锛?-nohimetric 瀵圭収妗ｏ級");

        // ---- 鍛堢幇鑺傚 ---------------------------------------------------------
        // 榛樿鏀规垚"绛夊埌鍚堟垚杈圭晫鍐嶆娊杈撳叆銆佺珛鍒?Present(0)"銆傚疄娴嬭繖涓€椤规妸
        // "Present 杩斿洖 鈫?鍍忕礌浜?浠?3.5 涓埛鏂板懆鏈熷帇鍒?1 涓紙瑙?        // reports/寤舵椂-瀹炴祴涓庝紭鍖?md锛夈€?-nopace 閫€鍥炶€佺殑 Present(1) 闃诲琛屼负銆?        OverlayWindow.VBlankPaced = !args.Contains("--nopace");
        if (OverlayWindow.VBlankPaced) OverlayWindow.LatencyWaitEnabled = true;

        _virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        // 涓诲睆灏哄锛歚--rawinput` 閲屾崲绠?缁濆鍘熷鎶?鐨勫綊涓€鍖栧熀鍑嗭紙0..65535 鈫?涓诲睆鍍忕礌锛夈€?        _primaryW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        _primaryH = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        if (_primaryW <= 0) _primaryW = _virtualW;
        if (_primaryH <= 0) _primaryH = _virtualH;

        Console.WriteLine($"virtual desktop: {_virtualW}x{_virtualH} at ({_virtualX},{_virtualY})"
                          + $"锛涗富灞?{_primaryW}x{_primaryH}");

        if (!CreateOverlays())
            return 3;

        // 閿綅锛氬厛璇荤敤鎴烽厤缃殑瑕嗙洊椤癸紝鍐嶆敞鍐屽叏灞€鐑敭銆傝鍧忎簡涓嶉樆濉炲惎鍔ㄢ€斺€?        // 鐢ㄩ粯璁ら敭浣嶈窇璧锋潵锛屼絾鎶婇棶棰橀€愭潯鎵撳嚭鏉ワ紙闈欓粯鍥為€€鏈€鍧戜汉锛夈€?        foreach (var w in InkSettings.Load(Keys))
            Console.WriteLine("settings: " + w);

        // 鑷姩鏇存柊鐨勬潵婧愶細settings.json 瑕嗙洊榛樿鍊硷紙榛樿鏄┖鐨?= 涓嶆鏌ワ級銆?        // 鏀捐繖閲岃锛屾槸涓轰簡"鐢ㄦ埛鏀归厤缃枃浠朵笉鐢ㄩ噸鏂扮紪璇?銆?        UpdateFeed.Url = InkSettings.LoadUpdateUrl() ?? "";

        // **鏈夋洿鏂版簮 鈫?鍒濆鐘舵€佸氨鏄?妫€鏌ユ洿鏂?**锛堣€屼笉鏄?鏈厤缃簮"锛夈€?        // `UpdateText` 鐨勯粯璁ゅ€兼槸"鏈厤缃洿鏂版簮"锛岄偅鏄粰"鐪熺殑涓€涓簮閮芥病鏈?鍑嗗鐨勶紱
        // 浠ュ墠涓嶇鏈夋病鏈夋簮锛屼竴鎵撳紑灏辨樉绀?妫€鏌ユ洿鏂帮紙鏈厤缃簮锛?锛岀湅鐫€鍍忓潖浜?        // 锛堢敤鎴?2026-09-29 鍙嶉锛夈€傜晫闈㈤偅杈?`UpdateStage.Idle` 鏄剧ず鐨勫氨鏄?妫€鏌ユ洿鏂?銆?        if (UpdateFeed.HasAnySource)
        {
            UpdateState = UpdateStage.Idle;
            UpdateText = "";
        }

        // **涓婃鐢ㄧ殑棰滆壊**锛氭瘡涓伐鍏峰悇璁颁竴涓紙瀛樼殑鏄壊甯﹀簭鍙凤紝瑙?SetColorFromUi锛夈€?        // 璇讳笉鍒?/ 瀵逛笉涓婂氨鐢ㄩ粯璁よ壊锛堢瑪=绾€佽崸鍏?榛勶級鈥斺€旈偅鏄?InkPalette 閲岀殑榛樿銆?        if (int.TryParse(GetUiPref("penColor"), out int penIdx)
            && penIdx >= 0 && penIdx < InkPalette.PenBand.Length)
        {
            CurrentColor = InkPalette.PenBand[penIdx].Color;
            _penColorIdx = penIdx;                     // 杩炴寜鏃朵粠杩欓噷寰€涓嬭蛋
        }
        if (int.TryParse(GetUiPref("hlColor"), out int hlIdx)
            && hlIdx >= 0 && hlIdx < InkPalette.HighlighterBand.Length)
        {
            HighlighterCurrent = InkPalette.ToHighlighter(InkPalette.HighlighterBand[hlIdx].Color);
            _hlColorIdx = hlIdx;
        }
        // **绮楃粏妗?*锛堟瘡涓伐鍏峰垎寮€璁帮紝鐢ㄦ埛 2026-09-30 瀹氾級锛氬瓨鐨勬槸"绗嚑妗?銆?        // **2026-10-09 鏅氭。琛ㄥ叏鎹唬**锛堟瘡寮犺〃閮藉湪甯哥敤娈靛姞瀵嗕竴鍊嶏級锛氳€佷笅鏍囨寜鏂拌〃璇讳細閿欎綅锛?        // 鎵€浠ラ敭鍚嶅崌绾т竴浠ｏ紙绗?`w3.pen`銆佸叾浣?`w2.*`锛夈€傝涓嶅埌灏辨寜"鑰佷笅鏍?鈫?鑰佽〃鍙栧€?鈫?        // **鍚搁檮**杩涙柊琛?杩佺Щ锛堣€佸€奸兘鍦ㄦ柊琛ㄩ檮杩戯紝鏃犳崯鎴栧彧鎸崐鏍硷級銆?        if (int.TryParse(GetUiPref("w3.pen"), out int wPen3) && wPen3 >= 0 && wPen3 < WidthPresets.Length)
        { WidthPresetIndex = wPen3; PenWidthLogical = WidthPresets[wPen3]; }
        else if (MigrateWidthPref("w2.pen", Legacy9WidthPresets, WidthPresets, ref WidthPresetIndex, ref PenWidthLogical)) { }
        else if (MigrateWidthPref("w.pen", LegacyWidthPresets, WidthPresets, ref WidthPresetIndex, ref PenWidthLogical)) { }
        if (int.TryParse(GetUiPref("w2.hl"), out int wHl2) && wHl2 >= 0 && wHl2 < HighlighterWidthPresets.Length)
        { HighlighterWidthIndex = wHl2; HighlighterWidthLogical = HighlighterWidthPresets[wHl2]; }
        else if (MigrateWidthPref("w.hl", new[] { 8f, 18f, 32f }, HighlighterWidthPresets, ref HighlighterWidthIndex, ref HighlighterWidthLogical)) { }
        if (int.TryParse(GetUiPref("w2.laser"), out int wLaser2) && wLaser2 >= 0 && wLaser2 < LaserWidthPresets.Length)
        { LaserWidthIndex = wLaser2; LaserWidthLogical = LaserWidthPresets[wLaser2]; }
        else if (MigrateWidthPref("w.laser", new[] { 4f, 8f, 14f, 22f }, LaserWidthPresets, ref LaserWidthIndex, ref LaserWidthLogical)) { }
        if (int.TryParse(GetUiPref("w2.pixel"), out int wPixel2) && wPixel2 >= 0 && wPixel2 < PixelEraserWidthPresets.Length)
        { PixelEraserWidthIndex = wPixel2; PixelEraserWidthLogical = PixelEraserWidthPresets[wPixel2]; }
        else if (MigrateWidthPref("w.pixel", new[] { 46f, 93f, 150f }, PixelEraserWidthPresets, ref PixelEraserWidthIndex, ref PixelEraserWidthLogical)) { }
        // 涓婃鐢ㄧ殑姗＄毊褰㈡€侊紙鏁寸瑪鎿?/ 闈㈢Н鎿︼級
        if (GetUiPref("eraserKind") == "pixel") _eraserKind = Tool.PixelEraser;
        // 涓婃鐢ㄧ殑绾垮瀷锛堝疄绾?/ 铏氱嚎 / 鐐圭嚎锛?        if (int.TryParse(GetUiPref("lineDash"), out int dash) && dash >= 0 && dash <= 2)
            LineDash = (StrokeDash)dash;
        // **绮楃粏鐨?鏁板€?鐗?*锛氭粦鏉℃嫋鍔ㄧ幇鍦?*鍚搁檮鍦ㄦ。涓?*锛屾墍浠ヨ繖浠芥暟鍊兼甯稿氨绛変簬鏌愭。锛?        // 鏃╂湡鐗堟湰瀛樿繃浠绘剰鍊硷紙濡?2.37锛夆€斺€旇鍥炴潵鏃跺惛闄勫埌鏈€杩戠殑妗ｏ紝鐣岄潰涓嶄細鏄剧ず涓€涓?涓嶅瓨鍦?鐨勬。銆?        if (float.TryParse(GetUiPref("wv.pen"), out float vPen))
        { PenWidthLogical = Nearest(WidthPresets, vPen); WidthPresetIndex = NearestIndex(WidthPresets, PenWidthLogical); }
        if (float.TryParse(GetUiPref("wv.hl"), out float vHl))
        { HighlighterWidthLogical = Nearest(HighlighterWidthPresets, vHl); HighlighterWidthIndex = NearestIndex(HighlighterWidthPresets, HighlighterWidthLogical); }
        if (float.TryParse(GetUiPref("wv.laser"), out float vLaser))
        { LaserWidthLogical = Nearest(LaserWidthPresets, vLaser); LaserWidthIndex = NearestIndex(LaserWidthPresets, LaserWidthLogical); }
        if (float.TryParse(GetUiPref("wv.pixel"), out float vPixel))
        { PixelEraserWidthLogical = Nearest(PixelEraserWidthPresets, vPixel); PixelEraserWidthIndex = NearestIndex(PixelEraserWidthPresets, PixelEraserWidthLogical); }
        // **鐣岄潰缂╂斁**锛?026-10-10锛屾。琛ㄨ UiScalePresets锛夛細閰嶇疆閲屽瓨鏁板€硷紙"1.3"锛夛紝璇诲洖鍏堝惛闄勩€?        // `--uiscale` 鍙鐩?*鏈杩愯**銆佷笉钀界洏鈥斺€旇嚜妫€鍑哄浘涓庢崲鏈哄鐓х敤銆?        if (float.TryParse(GetUiPref("uiscale"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float usPref))
            UiScale = UiScalePresets.Snap(usPref);
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--uiscale" && float.TryParse(args[i + 1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float usArg))
                UiScale = UiScalePresets.Snap(usArg);
        // 涓婃鐢ㄧ殑閫夋嫨鏂瑰紡锛堢煩褰?/ 濂楃储锛?        if (GetUiPref("selMode") == "lasso") SelMode = SelectMode.Lasso;
        // 瑙︽懜鎵嬪娍鐨勬棆閽紙8.4.0锛夛細鍙屾寚鎬婚椄 / 鎵嬫帉鎿?/ 涓夋寚鎿?/ 闀挎寜閫夋嫨 / 涓ゆ寚鐐归€?/ 鍗曟寚婕父銆?        LoadTouchPrefs();
        // --notouch锛氳Е鎽告墜鍔挎€诲紑鍏?*涓存椂鍏虫帀**锛堜繚闄╀笣 / A-B 瀵圭収锛涗笉鍐欏亸濂斤級銆?        // 浼樺厛浜庤缃細鍛戒护琛岀粰鐨勮繖涓€浠界洊杩?"touch.gestures"銆?        if (args.Contains("--notouch")) _touch.Enabled = false;

        // **涓婁竴娆℃槸鑷姩鏇存柊瑁呬笂鏉ョ殑鍚?*锛氭崲澹宠剼鏈細鍦ㄦ洿鏂扮洰褰曢噷鐣欎竴涓?done.txt銆?        // 鐪嬪埌瀹?= 鏈鍚姩灏辨槸"鏇存柊瀹岀殑绗竴娆″惎鍔?锛屽湪鐣岄潰涓婃槑璇翠竴鍙?        // 锛堛€屾洿澶氥€嶆娊灞夐偅涓€琛屼細鏄剧ず"宸叉洿鏂板埌 x.y.z"锛夛紝鐒跺悗鎶婃爣璁板垹鎺夆€斺€斿彧璇翠竴娆°€?        // 涓轰粈涔堣杩欎竴姝ワ細鎹㈠３閲嶅惎涔嬪悗鐣岄潰鍘熸湰浠€涔堟彁绀洪兘娌℃湁锛岃€佸笀鏍规湰涓嶇煡閬撴垚娌℃垚銆?        try
        {
            string done = Path.Combine(UpdateFeed.UpdateDirFor(UpdateFeed.CurrentVersion), "done.txt");
            if (File.Exists(done))
            {
                File.Delete(done);
                UpdateState = UpdateStage.UpToDate;
                UpdateText = $"宸叉洿鏂板埌 {UpdateFeed.CurrentVersion}";
                Console.WriteLine($"鑷姩鏇存柊锛氭湰娆″惎鍔ㄦ槸鎹㈠３鏇存柊涓婃潵鐨勶紙{UpdateFeed.CurrentVersion}锛?);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("鑷姩鏇存柊锛氭鏌?done.txt 澶辫触锛? + ex.Message);
        }

        RegisterHotkeys();

        // 鑷/鍩哄噯妯″紡涓嬮潰鏉块粯璁ゅ叧鎺夛紝闄ら潪鏄惧紡 --hud銆?        //
        // 涓轰粈涔堬細闈㈡澘鏄釜澶ч粦妗嗭紝鐢诲湪灞忓箷宸︿笂瑙?*鍘嬪湪涓€鍒囦箣涓?*銆傝€屽緢澶氳嚜妫€鐨?        // 鍒ゆ嵁鎭版伆鏄?鏁板睆骞曚笂鐨勫ⅷ"锛?-coordtest / --inputtest / --erasertest /
        // --ghosttest / --tiletest / --appendtest / --passtest / --rotatetest鈥︼級锛?        // 闈㈡澘寰€涓婁竴鐩栵紝閭ｄ簺鐢ㄤ緥浼?*鍋囧け璐?*鈥斺€旈潰鏉胯秺澶х洊寰楄秺澶氾紝杩欐鎶婇潰鏉?        // 鏀惧ぇ鍒?700脳258 閫昏緫鍍忕礌灏卞綋鍦鸿俯鍒颁簡銆備氦浜掓ā寮忎粛鐒堕粯璁ゅ紑鐫€銆?        if (mode.Length > 0 && !args.Contains("--hud")) ShowHud = false;

        int dispatch = RunModeDispatch(mode, args);
        if (dispatch >= 0)
            return dispatch;

        // 璧板埌杩欓噷璇存槑鏄?*浜や簰妯″紡**锛氭妸閿綅琛ㄦ墦鍑烘潵锛堣繛鍚屾敞鍐屽け璐ョ殑鏉＄洰锛夈€?        // 娴嬭瘯妯″紡涓嶆墦鍗扳€斺€斾笁鍗佸琛屼細鎶婃瘡鏉¤嚜妫€鐨勬棩蹇楁饭鎺夈€?        // 浠ュ墠杩欎釜鏂规硶**鏍规湰娌′汉璋冪敤**锛屾墍浠ラ敭浣嶄竴鐩村彧娲诲湪 README 閲屻€?        PrintUsageHelp();

        Loop();
        Shutdown();
        return ExitCode;
    }

    /// <summary>
    /// 鍚姩闃舵銆佺獥鍙ｅ垱寤轰箣鍓嶇殑瀹夸富鍒嗘敮銆傚紩鎿庢湰韬病鏈夎繖鏍风殑妯″紡锛?    /// 寮€鍙戞湡鐨勬埅鍥?鐐瑰嚮鐩爣宸ュ叿鎸傚湪瀹夸富锛圛nkTeach锛夐噷銆?    /// </summary>
    protected virtual bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    /// <summary>
    /// 璇诲洖"鍥犱负鐣岄潰鍑洪棶棰樿€岄噸鍚?涔嬪墠鏆傚瓨鐨勬澘涔︺€?*璇昏蛋灏卞垹**锛堝彧鎭㈠涓€娆★級锛?    /// 澶棫鐨勭敱 <see cref="Recovery.TakeSession"/> 鑷繁涓㈡帀銆?    /// </summary>
    private void RestoreSessionIfAny()
    {
        var blob = Recovery.TakeSession();
        if (blob == null) return;

        try
        {
            InkSerializer.LoadInto(Doc, blob);
            Doc.InvalidateAll();
            Console.WriteLine($"宸叉仮澶嶄笂娆＄晫闈㈠嚭闂鏃舵殏瀛樼殑鏉夸功锛歿Doc.Strokes.Count} 绗?);
        }
        catch (Exception ex)
        {
            Console.WriteLine("浼氳瘽鎭㈠澶辫触锛堥偅浠芥殏瀛樺凡涓㈠純锛夛細" + ex.Message);
        }
    }

    /// <summary>
    /// 鍚姩鏃舵帴涓婁笂娆＄殑鏉夸功锛堣嚜鍔ㄥ瓨妗ｏ級銆?    /// **璇昏蛋涓嶅垹**锛氬畠灏辨槸杩欎唤鏉夸功鏈韩锛屼笅娆℃墦寮€杩樿鎺ョ潃鐢ㄣ€?    /// 璇诲潖浜嗗彧鎻愮ず銆佷粠绌虹櫧寮€濮嬧€斺€旇嚜鍔ㄥ瓨妗ｄ笉璇ユ垚涓?璧蜂笉鏉?鐨勭悊鐢便€?    /// </summary>
    private void RestoreAutoSaveIfAny()
    {
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;

        // **榛樿涓嶆帴涓婁竴娆＄殑鏉夸功**锛堢敤鎴?2026-09-17锛?閫€鍑轰互鍚庡啀鎵撳紑涓嶇敤鎭㈠澧ㄨ抗鍚р€︹€?        // 鎴戣寰楅粯璁や笉鎭㈠澧ㄨ抗"锛夈€?        //
        // 涓轰粈涔堣繖涓粯璁ゆ槸瀵圭殑锛氭暀瀹ょ殑鏈哄櫒鏄?*鍏敤**鐨勶紝涓婁竴鑺傝锛堢敋鑷充笂涓€涓彮锛夌殑鏉夸功
        // 涓€寮€鏈哄張閾烘弧鏁翠釜灞忓箷锛岃€佸笀绗竴浠朵簨灏卞緱鍏堟竻绌猴紱鑰屼笖"鎵撳紑灏辨湁鍒汉鐨勪笢瑗?
        // 鏈韩灏变笉鍚堥€傘€傝涓庤涔嬮棿瑕佺殑鏄竴鍧楀共鍑€鐨勭櫧鏉裤€?        //
        // 娉ㄦ剰鍜屽浘鐪佷簨鐨勪复鏃舵殏瀛橈紙<see cref="RestoreSessionIfAny"/>锛夊垎寮€锛氶偅鏉″彧鍦?        // **杞欢鑷繁涓诲姩閲嶅惎**锛堢晫闈笂鐐?閲嶅惎"銆佺晫闈㈠嚭闂鑷姩閲嶅缓锛夋椂鎵嶈蛋锛?        // 閭ｆ槸"閲嶅惎涓嶈涓笢瑗?锛屽拰"涓嬫鎵撳紑瑕佷笉瑕佹帴涓?鏄袱浠朵簨锛屼粛鐒朵繚鐣欍€?        //
        // 寮€鍏虫斁鍦ㄧ晫闈㈠亸濂介噷锛坄ui.restoreInk = "1"`锛夛紝浠ュ悗鍦?鏇村"鎶藉眽閲岀粰涓€琛屽氨鑳芥敼锛?        // 鐜板湪娌℃湁杩欎竴椤?= 涓嶆仮澶嶃€?        if (!RestoreInkOnStartup)
        {
            Console.WriteLine("鍚姩锛氫笉鎺ヤ笂娆＄殑鏉夸功锛堥粯璁わ紱瑕佹帴涓婂氨鍦ㄨ缃噷鎵撳紑 restoreInk锛?);
            return;
        }

        var blob = Recovery.LoadAuto();
        if (blob == null) return;

        try
        {
            InkSerializer.LoadInto(Doc, blob);
            Doc.InvalidateAll();
            _autoSavedVersion = Doc.Version;
            Console.WriteLine($"宸叉帴涓婁笂娆＄殑鏉夸功锛歿Doc.Strokes.Count} 绗?);
        }
        catch (Exception ex)
        {
            Console.WriteLine("鑷姩瀛樻。璇讳笉鍑烘潵锛堝凡蹇界暐锛屼粠绌虹櫧寮€濮嬶級锛? + ex.Message);
        }
    }

    /// <summary>
    /// 绐楀彛寤哄ソ銆佸揩鎹烽敭娉ㄥ唽瀹屼箣鍚庣殑鍒嗘敮銆傝繑鍥?-1 琛ㄧず"娌℃湁鐗规畩妯″紡锛?    /// 姝ｅ父璺戞秷鎭惊鐜?锛涜繑鍥?&gt;= 0 琛ㄧず鐩存帴浠ヨ鍊奸€€鍑恒€?    /// 浜у搧鐣岄潰鍙細鎷垮埌 -1锛涙祴璇曟ā寮忕敱 InkTeach 瑕嗗啓銆?    /// </summary>
    protected virtual int RunModeDispatch(string mode, string[] args) => -1;

    /// <summary>
    /// `--geomtrace`锛?026-10-06 鎺掓煡"蹇€熺敾鍦嗗彉鎶樼嚎"鏃跺姞锛夈€?    ///
    /// 鎵撳紑鍚庯紝姣忎竴绗旂殑鍑犱綍**璧板摢鏉¤矾**浼氭墦涓€琛岋細閫氶亾锛圓 绛夊鎻忚竟 / B 鍘嬫劅澧ㄨ抗锛?
    /// 鏁版嵁婧愶紙妯″瀷杈撳嚭 / 鍘熷閲囨牱鐐癸級+ 鏇茬嚎杩樻槸鎶樼嚎锛屽鍔犵偣鏁般€佸钩鍧囩偣璺濄€佸帇鎰熸湁鏃犮€?    /// 娲荤瑪鏍囧織銆佹洸绾垮紑鍏炽€傛帓鏌?鍒板簳鍝潯宀旇矾娌℃帴涓婃洸绾?闈犺浠ｇ爜鐚滀笉澶熲€斺€?    /// 杩欎釜寮€鍏宠绋嬪簭鑷繁鎶ワ紙鍓嶄袱杞氨鏄潬璇讳唬鐮佺寽锛屼袱娆￠兘鐚滈敊浜嗭級銆?    /// </summary>
    internal static bool GeomTraceOn;

    /// <summary>
    /// `--rawprobe`锛?026-10-07锛夛細娉ㄥ唽鍘熷杈撳叆銆佹暟"璁惧鍒板簳鎶ヤ簡澶氬皯鏉?銆?    ///
    /// 鐢ㄦ埛鐨勭枒闂槸"鎵嬪啓鏉挎爣绉?300Hz锛屼负浠€涔堝叧鎺?ink 鍙湁 65Hz"銆?    /// 纭欢涓婃姤鐜?鈮?鑳藉埌搴旂敤鎵嬮噷鐨勬潯鏁扳€斺€斾腑闂撮殧鐫€ Windows 鐨勮緭鍏ョ绾?    /// 锛堥紶鏍囨秷鎭細琚悎骞讹級銆?*Raw Input锛圵M_INPUT锛夋槸缁曡繃鍚堝苟鐨勫畼鏂归€氶亾**锛?    /// 瀹冪粰鍑虹殑鏁版墠鍥炵瓟寰椾簡"鏄垜浠敹寰楀皯锛岃繕鏄郴缁熸湰鏉ュ氨娌＄粰"銆?    ///
    /// 鍙暟鏁帮紝涓嶅弬涓庤惤绗旓紙浜у搧璺緞涓€涓瓧涓嶆敼锛夈€?    /// </summary>
    internal static bool RawProbeEnabled;

    /// <summary>鍘熷杈撳叆娉ㄥ唽鏄惁鎴愬姛锛堣瘖鏂級銆?/summary>
    internal static bool RawProbeOk;

    /// <summary>鏈瑪鏈熼棿鏀跺埌鐨勫師濮嬭緭鍏ユ潯鏁般€?/summary>
    private int _rawReportsThisStroke;

    /// <summary>
    /// `--rawinput`锛氭妸鍘熷杈撳叆锛圧aw Input锛?*琛ュ厖**鎴愯繖涓€绗旂殑閲囨牱鐐广€?    ///
    /// 涓轰粈涔堥渶瑕侊紙鐢ㄦ埛 2026-10-07 鐪熸満瀹炴祴锛夛細
    ///   鍏虫帀 Windows Ink 鏃舵墜鍐欐澘璧伴紶鏍囬€氶亾锛學indows 鐨勯紶鏍囨秷鎭绾?*浼氬悎骞?*锛?    ///   鑰屼笖鍚堝苟鎺夌殑鐐瑰湪 `GetPointerInfoHistory` 閲?*鎷夸笉鍥炴潵**锛堝疄娴?1:1锛夈€?    ///   鍚屼竴鍙版満鍣ㄥ悓涓€绗旓細鎸囬拡娑堟伅鍙粰 60~79 Hz锛岃€?*鍘熷杈撳叆鏈?154~194 Hz** 鈥斺€?    ///   涔熷氨鏄鍏垚鐨勮緭鍏ヨ鐧芥墧浜嗐€?    ///
    /// 璁捐锛?*鍙涓嶆敼**锛屼骇鍝佽矾寰勪竴涓瓧涓嶅姩锛夛細
    ///   路 鎸囬拡娑堟伅浠嶆槸**鍞竴鐨勪富璺緞**锛氳矾鐢?/ 鍛戒腑 / 绌块€?/ 澶氱獥鍙ｅ叏褰掑畠锛?    ///   路 鍘熷杈撳叆鍙湪**姝ｅ湪鍐欎竴绗?*鏃讹紝寰€杩欎竴绗斾笂**杩藉姞閲囨牱鐐?*锛?    ///   路 榧犳爣鍘熷鎶ユ槸**鐩稿浣嶇Щ**锛屾墍浠ョ敤"**鎸囬拡娑堟伅褰撶粷瀵归敋鐐?+ raw 鎶ョ疮鍔犲～缂?*"锛?    ///     姣忎釜鎸囬拡娑堟伅鎶婇敋鐐硅鎴愬畠鐨勭粷瀵逛綅缃€佺疮璁℃竻闆?鈫?**璇樊琚す鍦ㄤ竴鏉℃秷鎭箣鍐咃紝涓嶆紓**锛?    ///   路 闆朵綅绉荤殑鎶ヨ涓㈡帀锛堝師濮嬭緭鍏ュ湪涓嶅姩鏃朵篃鎸夎疆璇㈢巼鍙戠┖鎶ワ級銆?    /// </summary>
    internal static bool RawInputCapture;

    /// <summary>
    /// raw 琛ョ偣鐨?*鏈€灏忚窛绂?*锛堢敾甯冨儚绱狅級銆備綆浜庡畠鐨勬姤鐩存帴涓㈡帀銆?    ///
    /// 涓轰粈涔堝繀椤绘湁锛氶紶鏍囩殑鍘熷鎶ュ彲浠ュ埌 1000Hz锛堟父鎴忛紶鏍囷級锛屽揩閫熺Щ鍔ㄦ椂鐩搁偦鎶ュ彧宸笉鍒?    /// 1 鍍忕礌鈥斺€?*寰€绗旇抗閲屽闆堕暱娈?*銆傝€屽嚑浣曢偅杈规槑纭杩囬浂闀挎浼氬鍑轰竴涓€€鍖栨
    /// 锛堣 `BuildCenterlineCore` 閲?鍐嶈ˉ涓€娆″氨缁欏嚑浣曞鍑轰竴涓浂闀挎"閭ｆ娉ㄩ噴锛夈€?    /// 3px 杩欎釜鏁拌繙缁嗕簬瑙傛劅鎵€闇€锛?2px 绗斿涓嬶紝鐐硅窛 4px 鏃跺鸡楂樿宸彧鍓?0.04px锛夛紝
    /// 鍚屾椂鎶?姣忕鏈€澶氬澶氬皯涓偣"闄愬埗鍦?閫熷害/3 浠ュ唴銆?    /// </summary>
    internal static float RawMinDistPx = 3f;

    /// <summary>raw 琛ョ偣鐨?*姣忕瑪涓婇檺**锛堢浜岄亾淇濋櫓锛岄槻鐥呮€佽澶囷級銆?/summary>
    internal static int RawMaxPerStroke = 3000;

    /// <summary>`--rawinput` 鏈熼棿浠庡師濮嬭緭鍏ヨˉ杩涙潵鐨勭偣鏁帮紙璇婃柇锛夈€?/summary>
    private int _rawPointsAdded;

    // ---- 鍘熷杈撳叆"琚摢閬撻棬鎸′綇鐨?鍒嗛」璁℃暟锛堣瘖鏂紝2026-10-07 鍔狅級------------
    // 璧峰洜锛氱敤鎴风湡鏈哄疄娴?`[鍘熷杈撳叆 42 鏉?鈫?160 Hz]` 浣?`[raw 琛ョ偣 +0]` 鈥斺€?    // 鎶撳緱鍒板嵈涓€鏉℃病琛ャ€傚厜鐪嬫€绘暟瀹氫綅涓嶄簡鏄摢閬撻棬鎸＄殑锛屾墍浠ユ瘡涓?return 鍓嶈涓€绗斻€?    private int _rawSkipNoStroke, _rawSkipApi, _rawSkipType, _rawSkipAbs;
    private int _rawSkipZero, _rawSkipKind, _rawSkipGate, _rawSkipAnchor;
    private int _rawSkipCap, _rawSkipNear, _rawSkipNoInter;

    /// <summary>涓诲睆鍍忕礌灏哄锛堢粷瀵瑰師濮嬫姤鐨勫綊涓€鍖栧熀鍑嗭級銆?/summary>
    private float _primaryW = 1920f, _primaryH = 1080f;

    /// <summary>`--wintabprobe`锛氭敹鍒扮殑 Wintab 鍖呮潯鏁帮紙鍙暟娑堟伅锛屼笉瑙ｆ瀽鍖呬綋锛夈€?/summary>
    internal int WintabPackets;
    /// <summary>`--wintabprobe`锛氭敹鍒扮殑 WT_PROXIMITY 鏉℃暟锛?绗斿湪鏉垮瓙涓?鐨勯€氱煡锛夈€?/summary>
    internal int WtProximity;
    /// <summary>`--wintabprobe`锛氭寜娑堟伅绫诲瀷鍒嗛」璁℃暟锛堝熀鍧€ + 鍋忕Щ锛夛紝鐢ㄦ潵鍒嗗紑
    /// **WT_PACKET锛堢湡鏁版嵁锛?* 鍜?**WT_CTXUPDATE锛堥┍鍔ㄦ瘡绉掍竴娆＄殑鐘舵€侀€氱煡锛?*鈥斺€?    /// 鍙暟鎬绘暟浼氭妸鍚庤€呭綋鎴愬寘锛岃鍑烘潵鐨?鍖呯巼"鏄亣鐨勩€?/summary>
    internal readonly int[] WtByType = new int[64];
    private int _wtMsgBase = -1;

    /// <summary>Wintab 娑堟伅鍩哄潃 + 鍋忕Щ 鈫?鍚嶅瓧銆?/summary>
    private static string WtMsgName(int off) => off switch
    {
        0 => "WT_PACKET(鐪熸暟鎹?",
        1 => "WT_CTXOPEN",
        2 => "WT_CTXCLOSE",
        3 => "WT_CTXUPDATE(鐘舵€侀€氱煡)",
        4 => "WT_CTXOVERLAP",
        5 => "WT_PROXIMITY",
        6 => "WT_INFOCHANGE",
        _ => "?",
    };

    /// <summary>鎶?lcPktData 鐨勪綅缈昏瘧鎴愪汉璇濓紙鎺㈤拡杈撳嚭鐢級銆?/summary>
    private static string DecodePktData(int v)
    {
        var s = new System.Text.StringBuilder("鍚細");
        void Add(int bit, string name) { if ((v & bit) != 0) s.Append(name).Append(' '); }
        Add(Native.PK_X, "X");
        Add(Native.PK_Y, "Y");
        Add(Native.PK_Z, "Z");
        Add(Native.PK_NORMAL_PRESSURE, "姝ｅ帇鍔?);
        Add(Native.PK_TANGENT_PRESSURE, "鍒囧悜鍘嬪姏");
        Add(Native.PK_ORIENTATION, "鍊炬枩");
        Add(Native.PK_ROTATION, "鏃嬭浆");
        Add(Native.PK_BUTTONS, "鎸夐敭");
        Add(Native.PK_CURSOR, "绗旂被鍨?);
        Add(Native.PK_TIME, "鏃堕棿鎴?);
        Add(Native.PK_STATUS, "鐘舵€?);
        Add(Native.PK_CONTEXT, "涓婁笅鏂?);
        Add(Native.PK_SERIAL_NUMBER, "搴忓垪鍙?);
        return s.ToString();
    }

    /// <summary>
    /// `--wintabprobe`锛氭帰 Wintab 杩欐潯璺€氫笉閫氥€?    ///
    /// 鍙洖绛斾笁涓棶棰橈紝鍏ㄦ槸"鑳戒笉鑳界敤"銆佷笉鏄?濂戒笉濂界敤"锛?    ///   鈶?`wintab32.dll` 鍦ㄤ笉鍦ㄣ€佸鍑哄嚱鏁版壘涓嶆壘寰楀埌锛?    ///   鈶?`WTInfo` 鎶ヤ粈涔堬紙鎺ュ彛鐗堟湰銆佽澶囨暟锛夛紱
    ///   鈶?**`WTOpen` 鑳戒笉鑳芥墦寮€涓婁笅鏂?* 鈥斺€?鎵撲笉寮€鍚庨潰涓€鍒囧厤璋堬紱
    ///   鈶?鎵撳紑涔嬪悗**鐪熺殑鏀跺緱鍒板寘鍚?*锛堟暟 Wintab 娑堟伅鑼冨洿鍐呯殑娑堟伅鏉℃暟锛夈€?    ///
    /// 鍙銆佷笉鏀逛骇鍝佽涓猴紱澶辫触灏辫鏄庤繖鍙版満鍣?杩欏潡鏉垮瓙娌℃湁杩欐潯璺€?    /// </summary>
    internal int WintabProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== Wintab 鎺㈤拡锛堝彧璇伙紝涓嶆敼浠讳綍浜у搧琛屼负锛?==");

        IntPtr hmod = Native.LoadLibrary("wintab32.dll");
        if (hmod == IntPtr.Zero)
        {
            Console.WriteLine("  wintab32.dll          : 杞藉叆澶辫触 鈫?杩欏彴鏈哄櫒娌℃湁杩欐潯璺?);
            Console.WriteLine($"                          锛坋rr={Marshal.GetLastWin32Error()}锛?);
            return 1;
        }
        Console.WriteLine("  wintab32.dll          : 杞藉叆鎴愬姛");

        IntPtr pInfo = Native.GetProcAddress(hmod, "WTInfoA");
        IntPtr pOpen = Native.GetProcAddress(hmod, "WTOpenA");
        IntPtr pClose = Native.GetProcAddress(hmod, "WTClose");
        Console.WriteLine($"  WTInfoA / WTOpenA     : {(pInfo != IntPtr.Zero ? "鎵惧埌" : "缂?)} / {(pOpen != IntPtr.Zero ? "鎵惧埌" : "缂?)}");
        if (pInfo == IntPtr.Zero || pOpen == IntPtr.Zero) return 1;

        // 鈶?鎺ュ彛鐗堟湰锛圵TInfo(WTI_INTERFACE, IFC_VERSION, &ver)锛岃繑鍥?16 浣嶏級
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(buf, i, 0);
            uint got = Native.WTInfo(Native.WTI_INTERFACE, Native.IFC_VERSION, buf);
            ushort ver = (ushort)Marshal.ReadInt16(buf);
            Console.WriteLine($"  鎺ュ彛鐗堟湰              : 0x{ver:X4}锛圵TInfo 杩斿洖 {got} 瀛楄妭锛?
                              + (ver == 0 ? "  鈫?椹卞姩鍙兘娌″湪鍝嶅簲" : ""));

            for (int i = 0; i < 1024; i++) Marshal.WriteByte(buf, i, 0);
            Native.WTInfo(Native.WTI_INTERFACE, Native.IFC_NDEVICES, buf);
            uint devs = (uint)Marshal.ReadInt32(buf);
            Console.WriteLine($"  璁惧鏁?               : {devs}");

            // 鈶?鍙栭粯璁や笂涓嬫枃锛岀湅瀹冪殑 lcMsgBase锛堝寘娑堟伅浠庡摢鍎垮紑濮嬶級
            for (int i = 0; i < 1024; i++) Marshal.WriteByte(buf, i, 0);
            uint ctxBytes = Native.WTInfo(Native.WTI_DEFCONTEXT, 0, buf);
            int msgBase = Marshal.ReadInt32(buf, Native.LC_MSGBASE_OFFSET);
            int opts = Marshal.ReadInt32(buf, Native.LC_OPTIONS_OFFSET);
            int pktData = Marshal.ReadInt32(buf, Native.LC_PKTDATA_OFFSET);
            int pktRate = Marshal.ReadInt32(buf, Native.LC_PKTRATE_OFFSET);
            Console.WriteLine($"  榛樿涓婁笅鏂?           : WTInfo 杩斿洖 {ctxBytes} 瀛楄妭锛宭cMsgBase=0x{msgBase:X4}锛宭cPktRate={pktRate}");
            string msgFlag = (opts & Native.CXO_MESSAGES) != 0
                ? "鏈?CXO_MESSAGES"
                : "**娌℃湁 CXO_MESSAGES**锛堜笉璁惧畠椹卞姩涓嶆姇鍖咃級";
            string sysFlag = (opts & Native.CXO_SYSTEM) != 0 ? " +CXO_SYSTEM" : "";
            string penFlag = (opts & Native.CXO_PEN) != 0 ? " +CXO_PEN" : "";
            Console.WriteLine($"    榛樿 lcOptions       : 0x{opts:X4}  {msgFlag}{sysFlag}{penFlag}");
            Console.WriteLine($"    榛樿 lcPktData       : 0x{pktData:X4}  " + DecodePktData(pktData));
            if (msgBase <= 0) msgBase = Native.WT_DEFBASE;

            // **琛ラ綈 Wintab 鐨勬爣鍑嗗垵濮嬪寲**锛堢涓€鐗堟紡浜嗚繖姝?鈫?涓婁笅鏂囧紑寰楄捣鏉ヤ絾鏀朵笉鍒板寘锛夛細
            //   lcOptions |= CXO_MESSAGES          鈫?璁╅┍鍔ㄦ姇閫?WT_PACKET
            //   lcPktData |= X/Y/鍘嬪姏/鎸夐敭/鈥?       鈫?鍖呴噷甯︽垜浠渶瑕佺殑瀛楁
            //   lcMoveMask/lcBtnDnMask/lcBtnUpMask = lcPktData  鈫?绉诲姩鍜屾寜閿兘鍙?            int want = Native.PK_X | Native.PK_Y | Native.PK_NORMAL_PRESSURE
                     | Native.PK_BUTTONS | Native.PK_CURSOR | Native.PK_TIME | Native.PK_STATUS;
            Marshal.WriteInt32(buf, Native.LC_OPTIONS_OFFSET, opts | (int)Native.CXO_MESSAGES);
            Marshal.WriteInt32(buf, Native.LC_PKTDATA_OFFSET, pktData | want);
            Marshal.WriteInt32(buf, Native.LC_MOVEMASK_OFFSET, pktData | want);
            Marshal.WriteInt32(buf, Native.LC_BTNDNMASK_OFFSET, pktData | want);
            Marshal.WriteInt32(buf, Native.LC_BTNUPMASK_OFFSET, pktData | want);
            Console.WriteLine($"    閰嶇疆鍚?lcOptions     : 0x{(opts | (int)Native.CXO_MESSAGES):X4}锛堝凡琛?CXO_MESSAGES锛?);
            Console.WriteLine($"    閰嶇疆鍚?lcPktData     : 0x{(pktData | want):X4}  {DecodePktData(pktData | want)}");

            // 鈶?WTOpen锛氱湡姝ｇ殑闂ㄦ
            IntPtr hwnd = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
            IntPtr ctx = Native.WTOpen(hwnd, buf, true);
            if (ctx == IntPtr.Zero)
            {
                Console.WriteLine($"  WTOpen                : **澶辫触**锛坋rr={Marshal.GetLastWin32Error()}锛?
                                  + " 鈫?杩欐潯璺杩欏彴鏈哄櫒涓嶅瓨鍦?);
                return 1;
            }
            Console.WriteLine($"  WTOpen                : **鎴愬姛**锛坈tx=0x{ctx.ToInt64():X}锛?);

            // 鈶?鏁板寘锛歐intab 鐨勫寘娑堟伅钀藉湪 [lcMsgBase, lcMsgBase+64)锛屽彧鏁版潯鏁般€佷笉瑙ｆ瀽
            //    杩?*鍒嗗埆鏁?* WT_PACKET(鍩哄潃+0) 鍜?WT_PROXIMITY(鍩哄潃+5)锛?            //      路 鍙湁 PROXIMITY 娌℃湁 PACKET 鈫?椹卞姩鐭ラ亾绗斿湪闄勮繎浣嗘病缁欐暟鎹?            //      路 涓や釜閮芥湁 鈫?鐪熼€氫簡
            _wtMsgBase = msgBase;
            WintabPackets = 0;
            WtProximity = 0;
            Array.Clear(WtByType);
            Console.WriteLine();
            Console.WriteLine("  ---- 璁惧灞炴€э紙WTInfo 鏋氫妇锛?*涓嶉棶甯冨眬銆佺洿鎺ラ棶椹卞姩**锛?---");
            // 绱㈠紩鍚箟鎴戜笉鐚滐紙浠婂ぉ鐚滃竷灞€鏍戒簡涓夋锛夛細**鎶?1..32 鍏ㄦ灇涓惧嚭鏉ョ湅鍊?*锛?            // 闈?鍊肩殑閲忕骇"璁ゅ嚭鍝釜鏄?X/Y锛堝ぇ鑼冨洿锛夈€佸摢涓槸姝ｅ帇鍔涳紙0..1023 鎴?0..8191锛夈€?            for (uint idx = 1; idx <= 32; idx++)
            {
                for (int i = 0; i < 64; i++) Marshal.WriteByte(buf, i, 0);
                uint gotAxis = Native.WTInfo(Native.WTI_DEVICES, idx, buf);
                if (gotAxis == 0) continue;
                var ax = Marshal.PtrToStructure<Native.AXIS>(buf);
                if (ax.axMax == 0 && ax.axMin == 0 && ax.axUnits == 0) continue;
                // 绱㈠紩鍚?*鎸?wintab.h 鐨勬爣鍑嗛『搴忔帹娴?*锛?2=X 13=Y 14=Z 15=姝ｅ帇鍔?16=鍒囧悜鍘嬪姏 17=鍊炬枩锛夛紝
                // 骞剁敤瀹炴祴鍊间簰鐩稿嵃璇侊細12/13 鏄?50800/31750锛堝吀鍨嬪箙闈級銆?=400锛堟鏄?lcPktRate锛夈€?                // 6=0x15F7锛堟鏄粯璁?lcPktData锛夆€斺€旇繖浜?鎰忓瀵逛笂"璇存槑绱㈠紩娌¤閿欍€?                // **浣?15 鍜?16 閮芥槸 0..16383锛屽厜鐪嬮噺绋嬪垎涓嶅嚭璋佹槸鍘嬪姏**锛堝€炬枩涔熷彲鑳芥槸杩欎釜閲忕▼锛夛紝
                // 鎵€浠ヤ笅闈㈡爣鐨勬槸"鎺ㄦ祴"锛岀湡姝ｇ殑鍒ゆ嵁鏄?*瀹炲帇涓嬭鍖?*锛堣"鍖呬綋瀹炴祴"锛夈€?                string guess = idx switch
                {
                    12 => "  鈫?鎺ㄦ祴 X 杞村箙闈?,
                    13 => "  鈫?鎺ㄦ祴 Y 杞村箙闈?,
                    14 => "  鈫?鎺ㄦ祴 Z 鎮仠楂樺害锛?~1024 寰堝悎鐞嗭級",
                    15 => "  鈫?鎺ㄦ祴 **姝ｅ帇鍔?*锛堝緟瀹炲帇纭锛?,
                    16 => "  鈫?鎺ㄦ祴 鍒囧悜鍘嬪姏锛堥噺绋嬩笌 15 鐩稿悓锛?,
                    17 => "  鈫?鎺ㄦ祴 鍊炬枩鏂逛綅瑙掞紙0.1掳 涓哄崟浣嶏級",
                    _ => "  鈫?闈炶酱灞炴€э紙鎸?AXIS 璇讳細寰楀埌 min=鍊?max=0 杩欑鏍峰瓙锛屼笉鐢ㄧ锛?,
                };
                Console.WriteLine($"    [{idx,2}] min={ax.axMin,12} max={ax.axMax,12} units={ax.axUnits,-6} res={ax.axResolution}{guess}");
            }

            Console.WriteLine();
            Console.WriteLine("  鈻?璇风敤鎵嬪啓绗斿湪鏉垮瓙涓?*鏉ュ洖鍒?12 绉?*锛堟偓鍋滃嵆鍙紝涓嶅繀鍘嬬瑪灏栵級鈥?);
            Console.WriteLine("     鈿?涓€瀹氳鐪熺殑鍔ㄧ瑪鈥斺€斾笉鍔ㄧ殑璇濋┍鍔ㄤ笉浼氬彂 WT_PACKET锛屾祴涓嶅嚭缁撴灉銆?);

            // 鎸?**wintab.h 鐨勫浐瀹氬瓧娈甸『搴?* 绱姞鍋忕Щ鍜岄暱搴︺€?            //
            // 鈿?**鎵€鏈夎 lcPktData 閫変腑鐨勫瓧娈甸兘瑕佽鍏ラ暱搴?*锛屽寘鎷垜浠笉璇荤殑閭ｄ簺
            //锛圤RIENTATION / ROTATION / CHANGED / TANGENT_PRESSURE锛夆€斺€?            // 绗竴鐗堝彧绱姞浜?瑕佽鐨勫瓧娈?锛岀畻鍑?36 瀛楄妭锛岃€岀湡瀹炲寘鏄?**48 瀛楄妭**
            //锛坙cPktData=0x15F7 閲屽惈 ORIENTATION锛?2 瀛楄妭锛夆啋 璺ㄥ寘璇诲彇鍏ㄩ儴閿欎綅 鈫?            // X/Y/鍘嬪姏璇诲嚭 **6973 涓?* 杩欑楝兼暟銆?*杩欐槸浠婂ぉ绗笁娆℃牻鍦?鑷繁绠楀竷灞€"涓?*
            //锛堝墠涓ゆ锛歀OGCONTEXT 瀹炴祴 172 鑰屼笉鏄?160锛涜繖娆?36 鑰屼笉鏄?48锛夈€?            // 鎵€浠ヤ笅闈㈤櫎浜嗙畻瀵癸紝杩樺姞浜嗕竴閬?*鍚堢悊鎬ф柇瑷€**锛堣杈撳嚭閲岀殑"鏄惁鍙俊"锛夈€?            int xOff = -1, yOff = -1, pOff = -1, pktBytes = 0;
            {
                int o = 0, tmp;
                void Field(int bit, int size, bool want, out int off)
                {
                    off = -1;
                    if ((pktData & bit) == 0) return;
                    if (want) off = o;
                    o += size;
                }
                Field(Native.PK_CONTEXT, 4, false, out tmp);
                Field(Native.PK_STATUS, 4, false, out tmp);
                Field(Native.PK_TIME, 4, false, out tmp);
                Field(Native.PK_CHANGED, 4, false, out tmp);
                Field(Native.PK_SERIAL_NUMBER, 4, false, out tmp);
                Field(Native.PK_CURSOR, 4, false, out tmp);
                Field(Native.PK_BUTTONS, 4, false, out tmp);
                Field(Native.PK_X, 4, true, out xOff);
                Field(Native.PK_Y, 4, true, out yOff);
                Field(Native.PK_Z, 4, false, out tmp);
                Field(Native.PK_NORMAL_PRESSURE, 4, true, out pOff);
                Field(Native.PK_TANGENT_PRESSURE, 4, false, out tmp);
                Field(Native.PK_ORIENTATION, 12, false, out tmp);   // 鈫?婕忎簡瀹冨氨鏄偅涓?bug
                Field(Native.PK_ROTATION, 12, false, out tmp);
                pktBytes = o;
            }
            Console.WriteLine($"  鍖呬綋甯冨眬锛堟寜瑙勮寖绠楋級  : 姣忓寘 {pktBytes} 瀛楄妭锛沊@{xOff} Y@{yOff} 鍘嬪姏@{pOff}"
                              + "   鈫?**瀹炴祴璇佹槑杩欎釜涓嶅锛岃涓?*");

            // ===================== 瀹炴祴淇 =====================
            // 涓婁竴杞湡鏈?71 涓寘锛堟偓鍋滐級鐨?*姣忎釜 4 瀛楄妭鍋忕Щ**缁熻锛屾妸鐪熷疄甯冨眬閽夋浜嗭細
            //
            //   鍋忕Щ  瀹炴祴琛屼负                        缁撹
            //    0    鎭?1000锛?HCTX 0x3E8锛?         涓婁笅鏂囧彞鏌?鉁?            //    4    鎭?0                            鐘舵€?            //    8    鎭?0                            鈫?**椹卞姩鍦ㄨ繖閲屽鍗犱簡涓€涓瓧**
            //   12    71408328 鈫?71408703 閫掑        鏃堕棿鎴?ms)
            //   16    0 鈫?70锛屾瘡鍖?+1                鍖呭簭鍙?鉁擄紙71 涓寘姝ｅソ 0..70锛?            //   20    鎭?1                           绗旂被鍨嬶紙1=绗旓級
            //   24    鎭?0                           鎸夐敭锛堟病鎸夛級
            //   28    504 鈫?1084锛屽彉 66 娆?          X
            //   32    432 鈫? 877锛屽彉 68 娆?          Y
            //   36    鎭?0锛?*鎮仠鏃舵湰鏉ュ氨璇ユ槸 0**锛? **姝ｅ帇鍔?* 鈫?灏辨槸瀹?            //   40    869 鈫?1400锛屽彉 35 娆?          鍊炬枩锛氭柟浣嶈
            //   44    417 鈫? 529锛屽彉 35 娆?          鍊炬枩锛氶珮浣庤
            //
            // 鍒ゆ嵁寰堝共鍑€锛?*鎮仠鏃跺帇鍔涘繀椤绘槸 0**锛屽叏琛ㄩ噷鍙湁鍋忕Щ 36 鍜?48 鏄?鎭?0"锛?            // 鑰?36 绱ц窡 32(Y) 涔嬪悗銆佹濂藉湪瑙勮寖閲?normalPressure 璇ュ湪鐨勪綅缃€?            // 锛堝亸绉?40/44 鍦ㄦ偓鍋滄椂鏄?869~1400 鑰屼笉鏄?0锛屾墍浠?*涓嶆槸**鍘嬪姏銆傦級
            //
            // 鏍瑰洜锛氶┍鍔ㄥ湪 offset 8 澶氭彃浜嗕竴涓瓧锛堣鑼冮噷閭ｉ噷骞舵病鏈夊凡閫変腑鐨勫瓧娈碉級锛?            // 浜庢槸浠庡畠寰€鍚?*鎵€鏈夊瓧娈垫暣浣撴尓浜?4 瀛楄妭** 鈥斺€?杩欏氨鏄垜鎸夎鑼冪畻鐨勫亸绉?            // 锛圶@24 Y@28 鍘嬪姏@32锛夊叏閮ㄩ敊浣嶃€佹妸 Y 褰撴垚鍘嬪姏鎶ュ嚭鏉ョ殑鍘熷洜銆?            // 鏁欒鍗囩骇鐗堬細**杩?鎸夎鑼冪畻"閮借鐢ㄥ疄娴嬮拤涓€閬嶏紱瑙勮寖鈮犺繖涓┍鍔ㄧ殑瀹炵幇銆?*
            xOff = 28; yOff = 32; pOff = 36;
            Console.WriteLine($"  鍖呬綋甯冨眬锛?*瀹炴祴淇**锛? X@{xOff} Y@{yOff} 鍘嬪姏@{pOff}"
                              + "   鈫?椹卞姩鍦?offset 8 澶氬崰涓€涓瓧锛屾暣浣撴尓 4 瀛楄妭");
            // ====================================================

            IntPtr pktBuf = Marshal.AllocHGlobal(64 * Math.Max(64, pktBytes));
            int pkMinX = int.MaxValue, pkMaxX = int.MinValue;
            int pkMinY = int.MaxValue, pkMaxY = int.MinValue;
            int pkMinP = int.MaxValue, pkMaxP = int.MinValue;
            long pkWithPressure = 0, pkTotal = 0;

            // ---- 涓嶉潬 OCR銆佷笉闈犵寽锛岃鍑哄瓧娈电殑涓ょ鍔炴硶 ----
            //
            // 鈶?**鎸?4 瀛楄妭鍋忕Щ缁熻 min/max/鍙樺寲娆℃暟**銆?            //    姣忎釜瀛楁鏈夎嚜宸辩殑"鎬ф牸"锛氫笂涓嬫枃鍙ユ焺涓€鐩翠笉鍙橈紱鍧愭爣鏄ぇ骞呴潰銆?            //    闅忎綅缃彉锛涘帇鍔涙槸 0~16383銆侀殢鐢ㄥ姏鍙橈紱鏃堕棿鎴充竴鐩存定銆?            //    鐪嬪嚭杩欏紶琛ㄥ氨鑳借鍑哄竷灞€锛?*涓嶇敤鏁板崄鍏繘鍒?*銆?            //    锛堜粖澶╁湪"鏁板崄鍏繘鍒?涓婂凡缁忔牻杩囷細鎴浘浼氫覆琛屻€丱CR 鏁颁笉鍑嗐€傦級
            const int WORDS = 16;                       // 64 瀛楄妭 梅 4
            var wMin = new int[WORDS];
            var wMax = new int[WORDS];
            var wChg = new int[WORDS];                  // 鍊煎彉鍖栬繃澶氬皯娆?            var wPrev = new int[WORDS];
            var wSeen = new bool[WORDS];
            for (int w = 0; w < WORDS; w++) { wMin[w] = int.MaxValue; wMax[w] = int.MinValue; }

            // 鈶?**鍘熷瀛楄妭鐩存帴鍐欐枃浠?*锛堟娊鍓?32 涓寘锛屾瘡鍖?64 瀛楄妭锛夈€?            //    鍐欐枃浠惰€屼笉鏄彧鎵撳睆骞曪細灞忓箷浼氳鎴柇銆佽浆杩颁細鍑洪敊锛?            //    鏂囦欢鎴戣兘鐩存帴璇汇€?*瑕佽瘉鎹紝涓嶈杞堪銆?*
            var rawPackets = new System.Collections.Generic.List<byte[]>();
            try
            {
                for (int sec = 0; sec < 12; sec++)
                {
                    Thread.Sleep(1000);
                    DrainMessages();
                    // 姣忕鎶婃敀涓嬬殑鍖呭叏鍙栧嚭鏉ワ紝缁熻 X/Y/鍘嬪姏鐨勮寖鍥?                    //
                    // 鈿?**涓€娆″彧鍙?1 涓寘**锛坈MaxPackets=1锛夛細杩欐牱姣忎釜鍖呴兘鍐欒繘 buf 鐨?0 鍋忕Щ锛?                    // **璺ㄥ寘閿欎綅涓嶅彲鑳藉彂鐢?*銆備笂涓€鐗堟寜"鑷畻鐨勫寘闀?48"涓€娆″彇 64 涓€?                    // 鎸?48 姝ラ暱璺斥€斺€斿鏋滅湡瀹炲寘闀夸笉鏄?48锛屼粠绗?2 涓寘璧峰氨鍏ㄩ敊浣嶏紝
                    // 缁撴灉 X/Y/鍘嬪姏閮借鍑?7000 涓囩骇锛堢 1 涓寘鍏跺疄鏄鐨勶級銆?                    // 鏁欒锛?*涓嶈鍐嶈嚜宸辩畻鍖呴暱**锛涜兘涓€娆′竴涓氨鍒壒閲忋€?                    int nPk;
                    while ((nPk = Native.WTPacketsGet(ctx, 1, pktBuf)) > 0)
                    {
                        pkTotal++;
                        if (xOff >= 0) { int v = Marshal.ReadInt32(pktBuf, xOff); if (v < pkMinX) pkMinX = v; if (v > pkMaxX) pkMaxX = v; }
                        if (yOff >= 0) { int v = Marshal.ReadInt32(pktBuf, yOff); if (v < pkMinY) pkMinY = v; if (v > pkMaxY) pkMaxY = v; }
                        if (pOff >= 0) { int v = Marshal.ReadInt32(pktBuf, pOff); if (v < pkMinP) pkMinP = v; if (v > pkMaxP) pkMaxP = v; if (v > 0) pkWithPressure++; }
                        for (int w = 0; w < WORDS; w++)
                        {
                            int v = Marshal.ReadInt32(pktBuf, w * 4);
                            if (v < wMin[w]) wMin[w] = v;
                            if (v > wMax[w]) wMax[w] = v;
                            if (!wSeen[w]) { wSeen[w] = true; wPrev[w] = v; }
                            else if (v != wPrev[w]) { wChg[w]++; wPrev[w] = v; }
                        }
                        if (rawPackets.Count < 32)
                        {
                            var b = new byte[64];
                            Marshal.Copy(pktBuf, b, 0, 64);
                            rawPackets.Add(b);
                        }
                        if (pkTotal > 200000) break;          // 闃茶窇椋?                    }
                    Console.Write($"\r    绗?{sec + 1,2}/12 绉掞紝宸插彇 {pkTotal} 鍖咃紝鍘嬪姏 {pkMinP}~{pkMaxP}   ");
                }
            }
            finally { Marshal.FreeHGlobal(pktBuf); }
            Console.WriteLine();
            int pk = WtByType[0];
            string pktVerdict = pk > 0
                ? $" 鈫?杩欐潯璺€氫簡锛?2 绉掔害 {pk / 12.0:F0} Hz锛?
                : " 鈫?鏀朵笉鍒扮湡鍖咃紙椹卞姩娌＄粰鏁版嵁 / 绗斾笉鍦ㄨ寖鍥村唴 / 娌＄湡鐨勫姩绗旓級";
            Console.WriteLine($"  WT_PACKET锛堢湡鍖咃級     : **{pk} 鏉?*{pktVerdict}");
            Console.WriteLine("  鎸夌被鍨嬪垎椤癸細");
            for (int off = 0; off <= 8; off++)
                if (WtByType[off] > 0)
                    Console.WriteLine($"    {WtMsgName(off),-24}: {WtByType[off]}");
            Console.WriteLine("  ---- 鍖呬綋瀹炴祴锛堝叧閿暟瀛楋級----");
            Console.WriteLine($"    鍙栧埌鍖呮暟            : {pkTotal}锛堝叾涓帇鍔?0 鐨?{pkWithPressure} 鏉★級");
            if (pkTotal == 0)
            {
                Console.WriteLine("    **娌℃湁鏁版嵁**        : 涓€鏉″寘閮芥病鍙栧埌锛屼笅闈㈢殑鑼冨洿鏃犳剰涔?
                                  + "锛堥┍鍔ㄦ病鍙?/ 绗斾笉鍦ㄨ寖鍥?/ 娌＄湡鐨勫姩绗旓級");
            }
            else
            {
                Console.WriteLine($"    X 鑼冨洿              : {pkMinX} ~ {pkMaxX}");
                Console.WriteLine($"    Y 鑼冨洿              : {pkMinY} ~ {pkMaxY}");
                Console.WriteLine($"    **鍘嬪姏鑼冨洿**        : {pkMinP} ~ {pkMaxP}"
                                  + (pkMaxP > 1024 ? "  鈫?**瓒呰繃 1024锛佹瘮 Windows Ink 閭ｆ潯璺洿缁?*" : ""));
                // **鍚堢悊鎬ф柇瑷€**锛氭暟浣嶆澘鍧愭爣鍜屽帇鍔涗笉鍙兘鍒板崈涓囩骇銆?                // 瓒呬簡灏辨槸鍋忕Щ/闀垮害绠楅敊锛?*杩欎簺鏁颁竴涓兘涓嶈兘淇?*鈥斺€?                // 浠婂ぉ宸茬粡鍦ㄨ繖鏍借繃涓€娆★紙婕忕畻 ORIENTATION 鈫?鍖呴暱绠楁垚 36 鑰屼笉鏄?48 鈫?璺ㄥ寘閿欎綅 鈫?璇诲嚭 6973 涓囷級銆?                bool sane = pkMaxX < 10_000_000 && pkMaxY < 10_000_000 && pkMaxP < 10_000_000
                            && pkMinX >= 0 && pkMinY >= 0 && pkMinP >= 0;
                Console.WriteLine($"    杩欎簺鏁板彲淇″悧        : {(sane ? "**鏄?*锛堝湪鍚堢悊閲忕骇鍐咃級" : "**鍚?鈥斺€?鍋忕Щ/闀垮害绠楅敊浜嗭紝鍒俊**")}");
                if (!sane)
                    Console.WriteLine("       锛堝潗鏍?鍘嬪姏涓嶅彲鑳藉埌鍗冧竾绾э紱璇存槑鍖呬綋甯冨眬绠楅敊锛岃鏁板叏閮ㄤ綔搴燂級");
            }
            // ---- 鈶?鎸?4 瀛楄妭鍋忕Щ鐨勭粺璁¤〃锛?*璁ゅ嚭甯冨眬闈犺繖寮犺〃锛屼笉闈犳暟鍗佸叚杩涘埗** ----
            if (pkTotal > 0)
            {
                Console.WriteLine("  ---- 鎸?4 瀛楄妭鍋忕Щ缁熻锛堣鍑哄瓧娈甸潬杩欏紶琛級----");
                Console.WriteLine("    鍋忕Щ   鏈€灏?       鏈€澶?     鍙樺寲娆℃暟   鍍忎粈涔?);
                for (int w = 0; w < WORDS; w++)
                {
                    if (!wSeen[w]) continue;
                    int lo = wMin[w], hi = wMax[w], chg = wChg[w];
                    string look;
                    if (chg == 0) look = "甯告暟锛堜笂涓嬫枃/鐘舵€佷箣绫伙級";
                    else if (lo >= 0 && hi <= 65535 && chg > 10) look = "**鍙樺寲閲忓ぇ銆佽寖鍥撮€備腑**";
                    else if (hi > 65535) look = "鏃堕棿鎴虫垨澶у箙闈㈠潗鏍?;
                    else look = "";
                    Console.WriteLine($"    {w * 4,4}  {lo,10}  {hi,10}  {chg,8}   {look}");
                }
                Console.WriteLine("     鍒よ锛氫笂涓嬫枃鍙ユ焺涓€鐩翠笉鍙橈紱鍧愭爣闈㈠ぇ銆侀殢浣嶇疆鍙橈紱"
                                  + "**鍘嬪姏鑼冨洿 0~16383銆侀殢鐢ㄥ姏鍙?*锛涙椂闂存埑涓€鐩存定銆?);
            }

            // ---- 鈶?鍘熷瀛楄妭鍐欐枃浠讹紙涓嶇湅灞忓箷銆佷笉鐢ㄦ埅鍥俱€佷笉浼氳浆杩板嚭閿欙級----
            string rawPath = System.IO.Path.Combine(
                Environment.CurrentDirectory, "wintab-鍘熷鏁版嵁.txt");
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# Wintab 鍘熷鍖咃紙鍓?{rawPackets.Count} 涓紝姣忓寘 64 瀛楄妭锛?);
                sb.AppendLine($"# 涓婁笅鏂?lcPktData = 0x{pktData:X4}锛坽DecodePktData(pktData)}锛?);
                sb.AppendLine($"# 涓€鍏卞彇鍒?{pkTotal} 涓寘锛沊 鑼冨洿 {pkMinX}~{pkMaxX}锛孻 鑼冨洿 {pkMinY}~{pkMaxY}锛屽帇鍔涜寖鍥?{pkMinP}~{pkMaxP}");
                sb.AppendLine();
                sb.AppendLine("# ---- 鎸?4 瀛楄妭鍋忕Щ缁熻锛堣鍑哄瓧娈甸潬杩欏紶琛級----");
                sb.AppendLine("#   鍋忕Щ        鏈€灏?         鏈€澶?   鍙樺寲娆℃暟");
                for (int w = 0; w < WORDS; w++)
                {
                    if (!wSeen[w]) continue;
                    sb.AppendLine($"#   {w * 4,4}  {wMin[w],11}  {wMax[w],11}  {wChg[w],8}");
                }
                sb.AppendLine();
                sb.AppendLine("# 鍒よ锛氫笂涓嬫枃鍙ユ焺涓€鐩翠笉鍙橈紱鍧愭爣闈㈠ぇ銆侀殢浣嶇疆鍙橈紱鍘嬪姏 0~16383銆侀殢鐢ㄥ姏鍙橈紱鏃堕棿鎴充竴鐩存定銆?);
                sb.AppendLine();
                for (int i = 0; i < rawPackets.Count; i++)
                {
                    sb.AppendLine($"鍖?{i + 1}:");
                    for (int r = 0; r < 64; r += 16)      // 姣忚 16 瀛楄妭 = 4 涓?4 瀛楄妭瀛?                    {
                        var line = new System.Text.StringBuilder();
                        line.Append($"  +{r,2}  ");
                        for (int c = 0; c < 16; c += 4)
                        {
                            for (int k = 0; k < 4; k++)
                                line.Append(rawPackets[i][r + c + k].ToString("X2")).Append(' ');
                            // 椤轰究鎶婂皬绔暣鏁颁篃瑙ｅ嚭鏉ワ紝鐪佸緱浠ュ悗杩樿蹇冪畻
                            int v = rawPackets[i][r + c]
                                  | (rawPackets[i][r + c + 1] << 8)
                                  | (rawPackets[i][r + c + 2] << 16)
                                  | (rawPackets[i][r + c + 3] << 24);
                            line.Append($"={v,11} | ");
                        }
                        sb.AppendLine(line.ToString().TrimEnd(' ', '|', ' '));
                    }
                    sb.AppendLine();
                }
                System.IO.File.WriteAllText(rawPath, sb.ToString(), new System.Text.UTF8Encoding(true));
                Console.WriteLine($"  鍘熷鏁版嵁宸插啓鏂囦欢      : {rawPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  鈿?鍐欏師濮嬫暟鎹枃浠跺け璐? : {ex.Message}");
            }

            Native.WTClose(ctx);
            Console.WriteLine("  WTClose               : 宸插叧闂?);
            _wtMsgBase = -1;
            return WintabPackets > 0 ? 0 : 1;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>涓婁竴鏉＄粷瀵瑰師濮嬫姤鐨勫師濮嬪€硷紙鍙栧樊鍊肩敤锛岃 HandleRawInput锛夈€?/summary>
    private int _rawAbsX, _rawAbsY;
    private bool _rawAbsValid;

    // ---- "鍒板簳鏈夋病鏈変腑闂寸偣鍙崬"鐨勮嚜閫傚簲鍒ゆ嵁锛?026-10-07 鍔狅級------------------
    // 涓轰粈涔堥渶瑕侊細**1:1 鐨勮澶囦笂鍘熷鎶ュ拰鎸囬拡娑堟伅鏄悓涓€鎵逛綅缃?*锛堟湰鏈哄悎鎴愯緭鍏ュ疄娴?    // 61 鎶?/ 61 娑堟伅锛夛紝琛ヨ繘鍘荤殑鍏ㄦ槸**閲嶅鐐?*锛堥浂闀挎锛夆€斺€旂函鍣０锛屼笉鏄俊鎭€?    // 鍙湁鍍忕敤鎴锋墜鍐欐澘閭ｆ牱"鎸囬拡娑堟伅琚郴缁熷悎骞?锛堝疄娴?13 鏉℃秷鎭?/ 42 鏉″師濮嬫姤 = 2.3:1锛?    // 鎵嶆湁涓棿鐐瑰彲鎹炪€?    // 鎵€浠ワ細**鍙湁褰撳師濮嬫姤鏉℃暟鏄庢樉澶氫簬鎸囬拡娑堟伅鏉℃暟鏃舵墠琛ョ偣**銆?    // 濂藉鏄?1:1 鐨勮澶囦笂杩欎釜鍔熻兘绛変簬涓嶅瓨鍦紙闆堕闄╋級锛屾湁鍚堝苟鐨勮澶囦笂鑷姩鐢熸晥銆?    private int _rawSinceLastPtr;      // 鑷笂涓€鏉℃寚閽堟秷鎭互鏉ユ敹鍒扮殑鍘熷鎶ユ暟
    private int _rawTotalSincePtr;     // 绱鐨勫師濮嬫姤鏁?    private int _ptrMsgsThisStroke;    // 绱鐨勬寚閽堟秷鎭暟
    private bool _rawHasIntermediates; // 鍒ゅ畾锛氭湁涓棿鐐瑰彲鎹?
    /// <summary>鏈€杩戜竴娆℃寚閽堟秷鎭粰鐨?*缁濆灞忓箷鍧愭爣**锛坮aw 濉紳鐨勯敋鐐癸級銆?/summary>
    private float _rawAnchorX, _rawAnchorY;
    /// <summary>鑷敋鐐逛互鏉?raw 鎶ョ疮璁＄殑浣嶇Щ銆?/summary>
    private float _rawAccumX, _rawAccumY;
    /// <summary>閿氱偣鏄惁鏈夋晥锛堟病閿氱偣灏变笉鏁㈢敤 raw 濉紳锛夈€?/summary>
    private bool _rawAnchorValid;

    /// <summary>
    /// 鏀跺熬鏃剁粰杩涚▼鐨勯€€鍑虹爜銆傞粯璁?0锛涜嚜妫€鍙戠幇鏈?FAIL 鏃剁疆 1锛?    /// 杩欐牱鑴氭湰/CI 涓嶇敤鍘昏В鏋愭帶鍒跺彴鏂囧瓧涔熻兘鍒ょ孩缁匡紙鍜?tools/ui-mock 鐨勮嚜妫€涓€涓鐭╋級銆?    /// 浜у搧璺緞姘歌繙鏄?0銆?    /// </summary>
    protected int ExitCode;

    private bool RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
            // 鍏滃簳鍏夋爣銆?*杩欎竴鍙ユ槸"姘歌繙杞湀"鐨勮В鑽?*锛歨Cursor 鐣欑┖锛圢ULL锛夋椂
            // 绯荤粺浼氱粰涓€涓粯璁ょ殑绛夊緟绫诲厜鏍囷紝鐢ㄦ埛鐪嬪埌鐨勫氨鏄竴鐩磋浆鍦堛€?            // 鍏蜂綋褰㈢姸鐢?WM_SETCURSOR 鎸夌姸鎬佽鐩栵紙瑙?ApplyCursor锛夈€?            hCursor = Cursors.System(Native.IDC_ARROW),
        };
        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private bool CreateOverlays()
    {
        bool ok = true;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var w = new OverlayWindow();
            var err = w.Create(_hInstance, _className, r.Left, r.Top, r.Width, r.Height);
            if (err != null)
            {
                Console.WriteLine($"overlay create failed on monitor {hMon}: {err}");
                ok = false;
                return false;
            }
            s_map[w.Hwnd] = w;
            _windows.Add(w);
            Console.WriteLine($"overlay on monitor {hMon}: {r.Width}x{r.Height} at ({r.Left},{r.Top}) dpi={w.Dpi}");
            Console.WriteLine($"婀垮ⅷ妗ｄ綅: {WetMode} {WetModeName(WetMode)}"
                              + "锛堣缃噷璁扮潃锛汣trl+Alt+Shift+W 寰幆鍒囨崲锛?-wetmode 0..3 鎸囧畾锛?);
            Console.WriteLine($"濮旀墭澧ㄨ抗杞ㄨ抗(InkTrail): {(OverlayWindow.InkTrailEnabled ? "寮€" : "鍏?)}"
                              + $"锛堟帴鍙OverlayWindow.InkTrailNote}锛?);
            Console.WriteLine($"搴旂敤渚ф箍澧ㄦ浛韬?FastWet): {(w.FastWetReady ? "鍙敤锛堢郴缁熻建杩瑰け鏁堟椂鑷姩鎺ョ锛? : "涓嶅彲鐢紙鍥為€€涓讳氦鎹㈤摼鑷粯锛?)}");
            Console.WriteLine($"婀垮ⅷ鍛堢幇: {(OverlayWindow.SystemWetPreferred ? "绯荤粺杞ㄨ抗鐙崰锛?-syswet锛? : "鑷繁鐢伙紙绯荤粺杞ㄨ抗鐓у杺锛?)}");
            // 璋冨弬鏃?鎴戝埌搴曡皟涓婁簡娌℃湁"蹇呴』涓€鐪肩湅寰楄锛氳繖閲屽嵃鐨勬槸**鐢熸晥鍊?*锛屼笉鏄?鍙敤/涓嶅彲鐢?銆?            // 锛?026-09-22 鐢ㄦ埛纰板埌鐨勪袱涓潙锛?-predictms 100 琚潤榛樺す鍒?15锛?-noinktrail 鐢熸晥浜嗘病鏈?            //   鍙兘闈犵寽銆傝繖涓や欢浜嬮兘涓嶈闈犵寽銆傦級
            // 2026-10-15锛氬彧鍓╃嚎鎬ч娴嬶紙Chromium 鍚屾棌锛夆€斺€旂畻娉曡涓嶅啀闇€瑕?妗?鐨勫彛寰勶紱
            // 鍦板钩绾垮浐瀹?21.3ms锛堟绉掍笉鍙皟锛夈€?            Console.WriteLine(PredictTailEnabled
                ? $"绗旇抗棰勬祴: 寮€锛堣嚜缁樺熬锛涚嚎鎬ч娴嬶紙Chromium 鍚屾棌锛夛紱鍦板钩绾垮浐瀹?{_predictor.HorizonMs:F1} ms锛?
                  + "璁剧疆銆屽ⅷ杩归娴嬨€嶅彲鍏?/ --nopredict2锛?
                : "绗旇抗棰勬祴: 鍏筹紙璁剧疆 鈫?涔﹀啓 鈫?澧ㄨ抗棰勬祴锛?-predict2 鍙啀寮€锛?);
            // 涔﹀啓鏈熼棿鐨?GC 浣庡欢杩熸。锛氫綆閰嶄笂"鍋跺彂鍗′竴涓?鐨勭涓€瀚岀枒灏辨槸瀹冩病鐢熸晥銆?            // 杩欓噷鍗扮殑鏄?*璇诲洖鏉ョ殑瀹為檯鐘舵€?*锛堣 GcLatency.Describe锛夛紝涓嶆槸"鎴戜滑鎯宠瀹冨紑"銆?            Console.WriteLine($"涔﹀啓鏈熼棿 GC 浣庡欢杩熸。: {GcLatency.Describe()}"
                              + $"锛圫ustainedLowLatency锛屾渶鍚庝竴绗斿悗 {GcLatency.HoldMs / 1000:F0} 绉掗€€鍥烇級");

            // 绯荤粺绗旇缃細**"闀挎寜褰撳彸閿?鏈€瀹规槗鍦ㄦ澘涔︽椂娣讳贡**鈥斺€旂瑪灏栧仠浣忎笉鍔ㄤ細琚垽鎴愰暱鎸夛紝
            // 鑰屾澘涔︽椂鍋滈】鏄父鎬併€傝繖閲屽彧**濡傚疄鎵撳嵃璇诲埌鐨勬暟鍊?*锛屼笉瑙ｉ噴鍝釜鍙栧€间唬琛ㄥ紑杩樻槸鍏?            //锛堥偅濂楀彇鍊兼垜浠病鏈夊彲闈犲嚭澶勶紝涓嶈兘鐚滐紱鏈」鐩殑瑙勭煩鏄笉璁告妸鐚滅殑鍐欐垚缁撹锛夈€?            // 鎯虫敼灏卞幓锛氳缃?鈫?钃濈墮鍜岃澶?鈫?绗斿拰 Windows Ink 鈫?鍏朵粬绗旇缃€?            {
                const string penKey = @"Software\Microsoft\Wisp\Pen\SysEventParameters";
                bool hasMode = Native.ReadDword(penKey, "HoldMode", out uint hm, out _);
                bool hasWait = Native.ReadDword(penKey, "WaitTime", out uint wt, out _);
                bool hasHold = Native.ReadDword(penKey, "HoldTime", out uint ht, out _);
                if (hasMode || hasWait || hasHold)
                {
                    var parts = new List<string>();
                    if (hasMode) parts.Add($"HoldMode={hm}");
                    if (hasWait) parts.Add($"闀挎寜鍒ゅ畾绛夊緟 {wt} ms");
                    if (hasHold) parts.Add($"闀挎寜淇濇寔 {ht} ms");
                    Console.WriteLine("绯荤粺绗旇缃? " + string.Join("銆?, parts)
                                      + "锛堣繖涓€浠芥槸**绯荤粺鍏ㄥ眬**鐨勮缃紝鍙垪鍑烘潵鐪嬫竻妤氣€斺€?
                                      + "鎴戜滑鑷繁鐨勭獥鍙ｅ凡缁忓崟鐙叧鎺変簡杩欐潯鎵嬪娍锛岃涓嬩竴琛岋級");
                }
                else
                {
                    Console.WriteLine(@"绯荤粺绗旇缃? 璇讳笉鍒?HKCU\Software\Microsoft\Wisp\Pen\SysEventParameters"
                                      + "锛堣繖鍙版満鍣ㄦ病鏈夎繖濂楃瑪璁剧疆锛涚湡绗旇澶囧彲鑳界敱椹卞姩鑷繁绠★級");
                }
            }
            // **杩欐潯鏄?鏃犺绯荤粺鎬庝箞璁撅紝鎴戜滑杩欒竟閮戒笉璁ゅ畠"鐨勯獙鏀跺彛**锛堢敤鎴?2026-09-23 瑕佺殑锛夛細
            // 涓夋潯 Window 绾у叧娉曡 Native.DisableSystemPressAndHold銆備负浠€涔堝繀椤诲嵃鍑烘潵锛?            // 瀹冨け鏁堟椂鐨勭幇璞℃槸"绗斿皷鍋滀綇寮瑰彸閿幆"锛岀湅璧锋潵鍍?鍋滈】鎴愬瀷娌¤瘑鍒嚭鏉?锛?            // 鎺掓煡鏂瑰悜浼氫粠涓€寮€濮嬪氨閿欍€?            Console.WriteLine("闀挎寜=鍙抽敭鎵嬪娍: " + (OverlayWindow.PressAndHoldDisabled
                ? "宸插湪鏈獥鍙ｅ唴鍏虫帀锛堢郴缁熼噷寮€鐫€涔熶笉褰卞搷鎴戜滑锛?
                : "鈿?娌″叧鎺夛紙鐪熺瑪涓婄瑪灏栧仠浣忓彲鑳藉脊鍙抽敭鐜紝浼氬拰鍋滈】鎴愬瀷鎶級"));
            Console.WriteLine($"鍘嬫劅鈫掔矖缁? {(PressureWidth.Enabled
                ? $"寮€锛坽PressureWidth.Min:F2}~{PressureWidth.Max:F2} 鍊嶏紝鏇茬嚎 gamma {PressureWidth.Gamma:F2}锛?
                : "鍏筹紙--nopressure锛?)}锛涘彉瀹介€氶亾: {OverlayWindow.InkNote}");
            // [鍋滅敤 2026-10-05] 妯℃嫙鍘嬪姏 / 绗旈攱 / 鏀跺皷锛氬叏閮ㄥ仠鐢紙浠ｇ爜涓庡浠借 宸插仠鐢?娓叉煋瀹為獙.md锛夈€?            Console.WriteLine("鏃犲帇鎰熺瑪杩瑰寮? 宸插仠鐢紙妯℃嫙鍘嬪姏 / 绗旈攱 / 鏀跺皷锛?);
            // **杈撳叆璺緞鎬昏**锛?026-10-07 鐢ㄦ埛瑕佹眰锛夛細涓€娆¤娓呰繖鍙版満鍣ㄤ笂浼氳蛋鍝嚑鏉¤矾銆?            // 姣忔潯绗旂殑 `[绗旂敾]` 琛屼篃浼氱敤澶х櫧璇濇姤"杩欎竴绗斿疄闄呰蛋浜嗗摢鏉?锛堣 DescribeInputPath锛夈€?            Console.WriteLine("杈撳叆璺緞: 寮€ ink 鈫?Windows Ink 绗旓紙鍘嬪姏 1024 绾э級"
                              + "锛涘叧 ink 鈫?"
                              + (WintabEnabled ? "Wintab 缁欏帇鍔涳紙16383 绾э級锛?raw 琛ョ偣"
                                               : "鍙湁 raw 琛ョ偣锛圵intab 榛樿鍏筹紱鎯冲紑鐢?--wintab锛屼細璁颁綇锛?)
                              + "锛沇intab: "
                              + (WintabEnabled
                                  ? "寰呭紑锛堜负涓嶅奖鍝嶅紑 ink锛屾敼鎴愮涓€绗斻€屽叧 ink銆嶆椂鎵嶅紑锛?
                                  : "鏈惎鐢?));
            return true;
        }, IntPtr.Zero);

        if (_windows.Count == 0)
        {
            Console.WriteLine("no monitors found");
            return false;
        }

        // Refresh the HUD ~4x a second so the numbers move even when idle.
        //
        // 椤轰究姣忛殧 250 ms 閲嶆柊"缃《"涓€娆★紙WM_TIMER 閲岃皟 ReassertTopmost锛夈€?        // 杩欎釜蹇呴』澶熷揩锛?*鍒殑缃《绐楀彛浼氭妸鎴戜滑鐩栦綇**鈥斺€斿疄娴嬭緭鍏ユ硶鐘舵€佹潯
        // 锛坵etype 鐨?StatusBarWnd锛夈€佹闈㈠皬缁勪欢锛圧ocketView锛夐兘鏄?TOPMOST锛?        // 璋佹渶鍚庤皟 SetWindowPos 璋佸湪涓婇潰銆傝鐩栦綇鐨勫悗鏋滀笉鍙槸"鐪嬩笉瑙?锛?        // 鍚堟垚/鐪熷疄鐐瑰嚮閮戒細钀藉埌閭ｄ釜绐楀彛涓婏紝鎵规敞涓€涓瓧閮界敾涓嶅嚭鏉ャ€?        Native.SetTimer(_windows[0].Hwnd, (IntPtr)1, 250, IntPtr.Zero);
        DpiScale = _windows[0].Dpi / 96f;
        PressureSim.DpiScale = DpiScale;    // 妯℃嫙鍘嬪姏鎸?72dpi 鍙ｅ緞鎹㈢畻璺濈锛堣 PressureSim锛?        PressureSim.BumpVersion();

        Host?.UpdateScreen(UiLogicalScreen);
        Host?.UpdateWorkArea(UiLogicalWorkArea);
        Console.WriteLine($"DPI 缂╂斁 {DpiScale:F2}锛堥€昏緫 {_windows[0].Width / DpiScale:F0}x{_windows[0].Height / DpiScale:F0}锛夛紝"
                          + $"鐣岄潰缂╂斁 {UiScale:F2}锛坽UiScalePresets.Name(UiScale)}锛?);

        // 鎶婇敭鐩樻ā寮忚惤鍒扮獥鍙ｆ牱寮忎笂銆傚瓧娈甸粯璁ゆ槸寮€鐨勶紝浣嗘牱寮忚绛夌獥鍙ｅ缓濂芥墠鑳芥敼鈥斺€?        // 涓嶈皟杩欎竴鍙ワ紝榛樿鍊煎拰瀹為檯鏍峰紡灏卞涓嶄笂锛堝緱鎸変竴娆?Ctrl+Alt+K 鎵嶇敓鏁堬級銆?        SetKeyboardMode(KeyboardMode);
        return ok;
    }

    private void RegisterHotkeys()
    {
        // 鍏ㄥ眬鐑敭**涓嶅啀鏄竴涓插啓姝荤殑 (id, key)**锛氬姩浣滀笌閿綅鐢?Keys 杩欏紶琛ㄥ喅瀹氾紝
        // id 鍙槸"娉ㄥ唽椤哄簭"锛孒andleHotkey 鎷垮畠鍙嶆煡鍔ㄤ綔銆傝繖鏍烽敭浣嶅彲閰嶇疆銆佸彲鏌ュ啿绐侊紝
        // 鑰屽紩鎿庨噷涓嶈璇?Ctrl+Alt+P"杩欑涓滆タ銆?        IntPtr h = _windows[0].Hwnd;
        _hotkeyActions.Clear();
        Keys.GlobalFailures.Clear();

        foreach (var b in Keys.For(KeyScope.Global))
        {
            int id = _hotkeyActions.Count + 1;
            uint mod = Native.MOD_NOREPEAT;
            if ((b.Chord.Modifiers & KeyChord.ModCtrl) != 0) mod |= Native.MOD_CONTROL;
            if ((b.Chord.Modifiers & KeyChord.ModAlt) != 0) mod |= Native.MOD_ALT;
            if ((b.Chord.Modifiers & KeyChord.ModShift) != 0) mod |= Native.MOD_SHIFT;

            _hotkeyActions.Add(b.Action);
            if (!Native.RegisterHotKey(h, id, mod, b.Chord.Vk))
            {
                // **娉ㄥ唽澶辫触瑕佺暀鐥?*锛氭暀瀹ゆ満涓婂井淇?/ QQ / 杈撳叆娉?/ 鏁欏杞欢閮戒細鎶㈤敭
                // 锛堝疄娴?Ctrl+Alt+W 灏辫寰俊鎴浘鍗犱簡锛夈€備互鍓嶈繖閲屽彧鎵撲竴琛屾帶鍒跺彴锛?                // 鐢ㄦ埛鏍规湰涓嶇煡閬擄紝鍙寰?杩欎釜閿病鍙嶅簲"銆?                int err = Marshal.GetLastWin32Error();
                string msg = $"{b.Chord}锛坽KeyMap.Describe(b.Action)}锛夋敞鍐屽け璐ワ紝閿欒 {err}"
                           + (err == 1409 ? "锛氬凡琚埆鐨勭▼搴忓崰鐢? : "");
                Keys.GlobalFailures.Add(msg);
                Console.WriteLine("hotkey " + msg);
            }
            else _hotkeysRegistered++;
        }
        Console.WriteLine($"registered {_hotkeysRegistered}/{Keys.Bindings.Count(b => b.Scope == KeyScope.Global)} global hotkeys");
    }

    // ---- 鏀炬槧鏈熼棿鐨?涓存椂鍏ㄥ眬鐑敭"锛?026-09-30 鐢ㄦ埛瀹氭柟妗堚憼锛?----------------
    //
    // 鑳屾櫙锛氭斁鏄犳椂**鍓嶅彴鏄?WPS/PPT**锛屾垜浠殑瑕嗙洊灞傛敹涓嶅埌閿洏锛堥敭鐩樻寜"鐒︾偣"鎶曢€掞紱
    // 榧犳爣鎵嶆槸鎸?浣嶇疆"锛屾墍浠ユ粴杞?鐐瑰嚮鍦ㄦ斁鏄犱腑鐓ф牱鏈夋晥锛夈€傝€屾垜浠殑宸ュ叿閿槸"搴旂敤鍐?    // 蹇嵎閿?锛岄潬鐒︾偣 鈫?鏀炬槧鏃跺叏澶辨晥 鉁椼€?    //
    // 鍔炴硶锛?*涓€杩涙斁鏄犳壒娉ㄦā寮忥紝灏辨妸杩欏嚑涓敭涓存椂娉ㄥ唽鎴愬叏灞€鐑敭**鈥斺€旂郴缁熺洿鎺ユ姇缁欐垜浠€?    // 涓嶇湅鐒︾偣锛涜€屼笖閿鎴戜滑鍚炴帀銆?*涓嶄細浼犵粰 WPS**锛屾墍浠ヤ笉浼氭湁"鎴戜滑鍒囦簡宸ュ叿銆丳PT 鍙堢炕
    // 涓€椤?鐨勫弻鍙戝壇浣滅敤锛堥殧澹?InkClass 閭ｇ"鎵撴灦"灏辨槸瀹冪敤鍏ㄥ眬閽╁瓙浣嗕笉鍚為敭閫犳垚鐨勶級銆?    // 閫€鍑烘斁鏄犵珛鍒绘敞閿€锛氬钩鏃朵竴涓敭閮戒笉澶氬崰銆?    //
    // **渚嬪锛氱┛閫忓紑鐫€鏃舵暣浣撹璺?*锛堢敤鎴?2026-09-30 瀹氾細"绌块€忔ā寮忎笅锛孭PT 鐨勯敭璧蜂綔鐢ㄣ€?    // 鎴戜滑鐨勯敭涓嶈捣浣滅敤"锛夆€斺€旂┛閫?= "杩欎竴娈甸敭鐩樺綊涓嬪眰绋嬪簭"锛屾墍浠ヨ繖鍑犱釜閿复鏃舵敞閿€锛?    // PPT/WPS 鑷繁鐨?Ctrl+P/E/L/Z銆佲啇鈫?绛夋仮澶嶅彲鐢紱閫€鍑虹┛閫忔椂鑻ヨ繕鍦ㄦ斁鏄狅紝绔嬪埢鏀跺洖
    // 锛堢粺涓€璧?SyncPptHotkeys锛岃 SetPassThrough锛夈€?    private const int PptHotkeyBase = 81;          // 涓€灏忔涓撶敤 id锛堝父瑙勭儹閿槸 1..N锛屽埆鎾烇級
    private static readonly (uint Mod, uint Vk, KeyAction Act)[] PptHotkeys =
    {
        (Native.MOD_CONTROL, 0x50 /*P*/, KeyAction.ToolPen),
        (Native.MOD_CONTROL, 0x49 /*I*/, KeyAction.ToolHighlighter),
        (Native.MOD_CONTROL, 0x4C /*L*/, KeyAction.ToolLaser),
        (Native.MOD_CONTROL, 0x45 /*E*/, KeyAction.ToolEraser),
        (Native.MOD_CONTROL, 0x5A /*Z*/, KeyAction.Undo),
        // 鈿?2026-10-04锛氬懠鍑虹洏**涓嶅啀杩涜繖寮犺〃**鈥斺€斿畠宸插崌涓哄父椹诲叏灞€閿?`Ctrl+Alt+Shift+Q`
        // 锛堣 KeyBindings.Default锛夛紝鏀炬槧鏃跺墠鍙版槸 PPT/WPS 涔熺収鏍风敓鏁堬紱涓存椂琛ㄩ噷鍐嶆寕涓€閬?        // 灏辩瓑浜?鍚屼竴涓姩浣滀袱鎶婂叏灞€閿?锛屾鏄繖娆℃敼閿娑堟帀鐨勪笢瑗裤€?        // 鏀炬槧鏃舵柟鍚戦敭涔熷綊鎴戜滑锛?*鏈夐€変腑 鈫?寰皟锛涙病閫変腑 鈫?鈫愨啋 浠?WPS 缈婚〉**
        // 锛堥敭鐩樺湪鎴戜滑鎵嬮噷锛屼笉鎷︾殑璇?WPS 鏀朵笉鍒?鈫愨啋锛屼粈涔堥兘涓嶅彂鐢熲€斺€旂敤鎴?2026-09-30 瀹炴祴锛夈€?        (0u, 0x25 /*鈫?/, KeyAction.PptPrev),
        (0u, 0x27 /*鈫?/, KeyAction.PptNext),
        (0u, 0x26 /*鈫?/, KeyAction.PanUp),
        (0u, 0x28 /*鈫?/, KeyAction.PanDown),
    };
    private bool _pptHotkeysOn;

    /// <summary>鐜板湪璇ヤ笉璇ユ寕鏀炬槧涓存椂鍏ㄥ眬閿細鏀炬槧涓?**涓斾笉绌块€?*銆?    /// 绌块€?= "杩欎竴娈甸敭鐩樺綊涓嬪眰绋嬪簭"锛堢敤鎴?2026-09-30 瀹氾細"绌块€忔ā寮忎笅锛孭PT 鐨勯敭璧蜂綔鐢ㄣ€?    /// 鎴戜滑鐨勯敭涓嶈捣浣滅敤"锛夛紝鎵€浠ョ┛閫忔湡闂磋璺紝閫€鍑虹┛閫忕珛鍒绘敹鍥炪€?/summary>
    private bool PptHotkeysWanted => PptMode && !PassThrough;

    /// <summary>鎸夊綋鍓嶇姸鎬佹寕/鎽樻斁鏄犱复鏃跺叏灞€閿€傝繘/閫€鏀炬槧锛圥pt.cs锛変笌寮€/鍏崇┛閫?    /// 锛圫etPassThrough锛夐兘璋冨畠锛沗RegisterPptHotkeys` 骞傜瓑锛岄噸澶嶈皟涓嶅仛浜嬨€?/summary>
    private void SyncPptHotkeys() => RegisterPptHotkeys(PptHotkeysWanted);

    /// <summary>鎸?鎽橀偅 8 涓斁鏄犱复鏃跺叏灞€閿紙鐪熸纰扮郴缁熺殑閭ｄ竴灞傦級銆?/summary>
    private void RegisterPptHotkeys(bool on)
    {
        if (_pptHotkeysOn == on || _windows.Count == 0) return;
        IntPtr h = _windows[0].Hwnd;
        for (int i = 0; i < PptHotkeys.Length; i++)
        {
            int id = PptHotkeyBase + i;
            var (mod, vk, _) = PptHotkeys[i];
            if (on)
            {
                if (!Native.RegisterHotKey(h, id, mod | Native.MOD_NOREPEAT, vk))
                    Console.WriteLine($"hotkey 鏀炬槧涓存椂閿?#{i} 娉ㄥ唽澶辫触锛岄敊璇?{Marshal.GetLastWin32Error()}");
            }
            else Native.UnregisterHotKey(h, id);
        }
        _pptHotkeysOn = on;
        Console.WriteLine(on ? "鏀炬槧鎵规敞妯″紡锛氬伐鍏烽敭锛圕trl+P/I/L/E/Z锛変笌鏂瑰悜閿凡涓存椂鍗囩骇涓哄叏灞€鐑敭"
                            : "鏀炬槧涓存椂鍏ㄥ眬鐑敭宸叉敞閿€锛堥€€鍑烘斁鏄犳垨寮€鐫€绌块€忥級");
    }

    /// <summary>鑷鐢細鏀炬槧涓存椂鍏ㄥ眬閿幇鍦ㄦ寕鐫€娌℃湁锛堢┛閫忔湡闂翠細璁╃粰涓嬪眰锛夈€?/summary>
    internal bool PptHotkeysOnForTest => _pptHotkeysOn;

    /// <summary>娉ㄥ唽椤哄簭 鈫?鍔ㄤ綔銆傛寜杩欎釜椤哄簭 RegisterHotKey锛學M_HOTKEY 鐨?id 灏辨槸瀹冦€?/summary>
    private readonly List<KeyAction> _hotkeyActions = new();

    internal KeyAction ActionForHotkeyId(int id)
        => id >= 1 && id <= _hotkeyActions.Count ? _hotkeyActions[id - 1]
         : id >= PptHotkeyBase && id < PptHotkeyBase + PptHotkeys.Length ? PptHotkeys[id - PptHotkeyBase].Act
         : KeyAction.None;

    /// <summary>鑷鐢細杩欐壒鍏ㄥ眬鐑敭瀹為檯娉ㄥ唽鎴愬姛浜嗗嚑涓€?/summary>
    internal long HotkeysRegistered => _hotkeysRegistered;

    // =====================================================================
    //  Message loop
    // =====================================================================

    internal void Loop()
    {
        // 涓诲惊鐜嚎绋?id锛氬悗鍙扮嚎绋嬶紙鏂囨。椤垫覆鏌?/ PPT 杞锛夌敤瀹?PostThreadMessage 鍙啋鎴戜滑銆?        _mainThreadId = Native.GetCurrentThreadId();

        // **鑷妯″紡閭ｈ璇婃柇**锛?026-10-07锛夛細鎵撳湪杩欓噷锛岃€屼笉鏄瀯閫犲嚱鏁伴噷銆?        //
        // 鏋勯€犲嚱鏁拌窇鍦?`RunModeDispatch` **涔嬪墠**锛岄偅鏃惰繕涓嶇煡閬?`args[0]` 鍒板簳绠椾笉绠?mode 鈥斺€?        // 鎻愬墠鎵撲細鎶?`--clean 3` 杩欑"寮€鍏冲啓鍦ㄧ涓€涓綅缃?璇姤鎴愯嚜妫€妯″紡锛堟垜浠婂ぉ灏辫鑷繁鐨?        // 鏃ュ織楠楄繃涓€杞級銆傝€?`Loop()` 鏄?`RunModeDispatch` 鍒ゅ畬涔嬪悗鎵€鏈夋ā寮忓繀缁忕殑绗竴绔欙細
        // 鍏滃簳鍒嗘敮宸茬粡鎶婅繖涓爣璁版竻骞插噣浜嗭紝鎵€浠ヨ繖閲屾墦鍑烘潵鐨勪竴瀹氭槸瀵圭殑銆?        //
        // `[mode]` 鏄?*缁欒剼鏈鐨?ASCII 鏍囪**锛氫腑鏂囬偅涓查噸瀹氬悜鍒版枃浠舵椂浼氳缂栫爜鎼炰贡锛?        // 闈犲瓧褰㈠垽"鏄笉鏄嚜妫€妯″紡"鍒や笉鍑嗐€?        if (SelfCheckMode && !_selfCheckLogged)
        {
            _selfCheckLogged = true;
            Console.WriteLine($"  [mode] 鑷锛歿_modeName}锛堣缃?瀛樻。/PPT 缂撳瓨閮借蛋涓存椂璺緞锛?);
        }
        // 鍛戒护琛岃姹傜殑 Wintab 鍋忓ソ锛?*鍒拌繖閲屾墠鍐欑洏**锛氭鍒?`RunModeDispatch` 宸茬粡鍒ゅ畬
        // 锛堝厹搴曞垎鏀細鎶婂紑鍏崇殑 SelfCheckMode 娓呮帀锛夛紝鎵€浠ュ紑鍏虫暡涓€娆℃槸鐪熺殑璁板緱浣忋€?        if (_pendingWintabPref.Length > 0 && !SelfCheckMode)
        {
            WriteWintabPref(_pendingWintabPref);
            _pendingWintabPref = "";
        }
        // 鍛戒护琛岃姹傜殑婀垮ⅷ妗ｄ綅锛屽悓鏍?*鍒拌繖閲屾墠鍐欑洏**锛堢悊鐢卞悓 wintab锛夈€?        if (_pendingWetModePref.Length > 0 && !SelfCheckMode)
        {
            try
            {
                SetUiPref(WetModePrefKey, _pendingWetModePref);
                FlushSettings();
                Console.WriteLine($"  [婀垮ⅷ] 宸茶浣忔。浣?{_pendingWetModePref}锛歿WetModeName(WetMode)}"
                                  + "锛堜笅娆″惎鍔ㄧ収杩欎釜妗ｈ窇锛汣trl+Alt+Shift+W 鍙殢鏃跺垏锛?);
            }
            catch (Exception ex) { Console.WriteLine($"  [婀垮ⅷ] 璁版。浣嶅け璐ワ紙涓嶅奖鍝嶄娇鐢級锛歿ex.Message}"); }
            _pendingWetModePref = "";
        }

        while (!_quit)
        {
            DrainMessages();
            PumpUpdate();                 // 鑷姩鏇存柊锛氭妸鍚庡彴缁撴灉鎼繃鏉ワ紝璇ユ崲澹冲氨鎹㈠３
            if (_quit) break;

            // 鍘熷杈撳叆鐨?*娉ㄩ攢鍏滃簳**锛氬彧瑕佹病鍦ㄥ啓涓€绗斿氨娉ㄩ攢鎺夈€?            // 鏀惧湪涓诲惊鐜噷鑰屼笉鏄€愪釜鎶瑪鍒嗘敮涓娾€斺€旀姮绗旇矾寰勬湁鍗佸嚑鏉★紙姝ｅ父鎶墜銆佷涪鎹曡幏銆?            // 鎴浘銆佸浘褰㈠伐鍏枫€佸绗斿紡鈥︼級锛屾紡涓€鏉″氨浼氶€€鍥?绌洪棽涔熸敹榧犳爣绌烘姤"锛?            // 琛ㄧ幇灏辨槸绌洪棽 46fps 绌鸿浆銆佸崟鏍?26%锛堝疄娴嬶級銆?            if (_rawRegistered && ActiveStroke == null) SetRawCapture(false);

            // 鈿?Wintab **涓嶅湪杩欓噷鍏?*锛?026-10-07 鐪熸満鎶撳埌鐨?bug锛夛細
            // 鍘熸潵杩欓噷鍐欎簡 `if (_wintab.IsOpen && ActiveStroke == null) _wintab.Close();`锛?            // 閰嶄笂 EnsureWintab 閲岀殑 `_wintabTried` 涓€鎵撹繃灏变笉鍐嶆墦寮€ 鈫?**鍙湁绗竴绗旀湁鍘嬪姏**锛?            // 鍚庨潰姣忎竴绗旈兘鏄?涓婁笅鏂囧凡鍏炽€佸張涓嶉噸寮€"銆傜敤鎴风湡鏈烘棩蹇楃殑 `鍘嬫劅=鏃燻 灏辨槸瀹冦€?            // 鐜板湪鏀规垚**寮€涓€娆″氨鐣欑潃**锛氬畠鐢ㄧ殑鏄疆璇紙娌℃湁鍖呮秷鎭級锛岀┖闂叉椂鎴戜滑涓€娆￠兘涓嶅幓鍙栵紝
            // 鎵€浠ョ暀鐫€涓嶄骇鐢熷敜閱掞紱璧风瑪鏃跺厛 Flush 鍊掓帀绉帇锛屼篃涓嶄細鍚冨埌杩囨湡鏍锋湰銆?
            NowMs = _clock.Elapsed.TotalMilliseconds;
            PumpKeyGestures();            // 宸ュ叿閿殑鎵嬪娍锛氶暱鎸夊垽瀹?+ 杩炴寜鎹㈣壊鐨勫欢杩熺粨绠?            PumpRadialPalette();          // 鍛煎嚭鐩橈細鍑虹洏寤惰繜 / 鏉炬墜杞 / 瓒呮椂
            StepMicroAdjust();            // 杩炵画寰皟浼氳瘽锛氬仠鎵?400ms 灏?灏佸彛"锛堜笅涓€娆″彟璧蜂竴鏉℃挙閿€锛?
            if (NowMs >= _autoExitAt) break;

            // 璺戞弧涓€鍒嗛挓杩樻病鍑轰簨 鈫?鎶?鍥犱负鐣岄潰鍑洪棶棰樿€岄噸鍚?鐨勮鏁版竻鎺夈€?            // 涓嶈繖涔堝仛鐨勮瘽锛屼笂鍗堜袱娆°€佷笅鍗堜袱娆★紝绗洓娆″氨浼氳帿鍚嶅叾濡欎笉璁搁噸鍚€?            if (!_restartCountCleared && NowMs > 60_000)
            {
                Recovery.ClearRestartCount();
                _restartCountCleared = true;
            }

            Laser.Prune(NowMs);
            StepCameraAnim();                 // 缈婚〉鍔ㄧ敾锛?67ms锛?            StepPpt();                        // PPT 鏀炬槧鑱斿姩锛堟病鍙樺寲鏃跺彧璇讳竴涓?bool锛屼笉纰?COM锛?            StepDocPageReady();               // 鏂囨。椤靛悗鍙版覆鏌撳畬涓€椤碉細缃剰锛堣繖涓€鎷嶄細閲囩撼瀹冿級
            StepDocAutoSave();                // 鏂囨。鎵规敞鐨勮妭娴佷繚瀛橈紙鑷姩淇濆瓨寮€鐫€鎵嶅啓锛?            StepPptBar();                     // 搴曢儴閭ｆ潯锛氬紩瀵艰繃鏈?/ "鍐嶇偣纭"杩囨湡锛堟病鏈夐暱鎸変簡锛?            StepEngineTooltip();              // 寮曟搸渚ф偓鍋滄彁绀虹殑 500ms 寤惰繜锛堝埌鐐圭偣浜級
            StepTimerCard();                  // 璇惧爞璁℃椂鍗＄墖锛氭帹杩涚鏁?/ 鍒扮偣 / 鍚屾鎺ヨ緭鍏ュ皬绐?            StepRollCard();                   // 璇惧爞鐐瑰悕鍗＄墖锛氭粴鍔ㄦ帹杩?/ 鍚屾鎺ヨ緭鍏ュ皬绐?            if (NeedsFrame())
            {
                // VBlankPaced锛氬厛绛夊埌鍚堟垚杈圭晫锛?*鍐嶆娊涓€娆℃秷鎭?*锛岀劧鍚庣敾銆佹彁浜ゃ€?                //
                // 椤哄簭鏄湁璁茬┒鐨勩€傜瓑鍒拌竟鐣屼箣鍚庢娊锛屾妸"绛夎竟鐣屾湡闂村埌杈剧殑杈撳叆"
                // 涔熺畻杩涜繖涓€甯э紝绗斿皷灏辨槸鏈€鏂扮殑锛涘弽杩囨潵锛堝厛鎶姐€佸啀绛変竴涓懆鏈燂級
                // 绛変簬璁╂渶鏂扮殑閭ｄ竴绗斿湪闃熷垪閲屽韬轰竴涓埛鏂板懆鏈熴€?                if (OverlayWindow.VBlankPaced)
                {
                    Native.DwmFlush();
                    DrainMessages();
                    _dirty = true;
                }
                UpdatePredictTail();   // B4锛氶娴嬪熬锛堟瘡甯ч噸寤猴紱榛樿寮€锛?026-10-14 璧凤級锛沗--nopredict2` 鍙叧锛?                // 娓叉煋鏈熼棿鐣岄潰鍙兘鍙堟彁鍑?鎴戣繕瑕佷竴甯?锛堝湪 Render 閲岃皟 InvalidateUi锛夈€?                // 鐢ㄥ簭鍙疯鍑烘潵锛屽埆璁╄繖涓€鍙?_dirty = false 鎶婂畠鎶规帀鈥斺€?                // 鎶规帀鐨勮〃鐜板氨鏄?鍔ㄧ敾鎴栦竴娆℃€у瑙傚彉鍖栧崱鍦ㄧ涓€甯?銆?                long seqBefore = _uiInvalidateSeq;
                long docVerBefore = Doc.Version;
                RenderAll();
                // 娓叉煋鏈熼棿**鏀逛簡鏂囨。**锛?鎸変綇娓呯┖"灏辨槸鍦ㄧ晫闈㈢殑 Render 閲屽鏃堕棿鐨勶級
                // 涔熻鍐嶈涓€甯э細杩欎竴甯ц创鍑哄幓鐨勬槸鏀逛箣鍓嶇殑鍍忕礌銆?                // 灏戜簡鍚庨潰杩欏崐鍙ワ紝娓呯┖涔嬪悗灞忓箷涓婇偅灞傚ⅷ浼氫竴鐩寸暀鐫€鈥斺€旇 RenderAll 閲岀殑娉ㄩ噴銆?                _dirty = _uiInvalidateSeq != seqBefore || Doc.Version != docVerBefore;

                // 绌洪棽棰勫彇锛氭甯稿抚璧板畬锛屽鏋?娌″湪鍐欍€佹病鍦ㄥ彇鏅?涓旇鍙ｅ涓€鍦堣繕鏈夋瑺鐫€鐨勫潡锛?                // 鎺掑嚑鎷嶅彧鐑樺潡銆佷笉鍚堟垚涓嶄笂灞忕殑绌洪棽鎷嶏紙涓婃父 Xournal++/Rnote 鐨勯娓叉煋鎬濊矾锛夈€?                if (!_quit && !CaptureActive && ActiveStroke == null && AnyPrefetchNeeded())
                    _prefetchStepsLeft = Math.Max(_prefetchStepsLeft, 64);
            }
            else if (_prefetchStepsLeft > 0)
            {
                // 绌洪棽棰勫彇涓€鎷嶏細鍙儤"瑙嗗彛澶栦竴鍦?閲屾瑺鐫€鐨勫潡锛屼笉鍚堟垚銆佷笉涓婂睆銆?                // 涓€鎷嶄箣鍚庣瓑鏈€澶?4ms锛堟湁杈撳叆绔嬪埢閱掞級锛氭棦涓嶆墦婊＄┖闂?CPU锛屼篃涓嶆嫋杈撳叆銆?                _prefetchStepsLeft--;
                bool more = false;
                foreach (var w in _windows) more |= w.PrefetchStep(this);
                if (!more) _prefetchStepsLeft = 0;
                Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 4, Native.QS_ALLINPUT, 0);
            }
            else
            {
                Native.WaitMessage();
            }
        }
    }

    /// <summary>绌洪棽棰勫彇鐨勫墿浣欐媿鏁帮紙瑙佷富寰幆锛涙瘡鎷?<see cref="CanvasTileCache.PrefetchPerFrame"/> 鍧楋級銆?/summary>
    private int _prefetchStepsLeft;

    /// <summary>瑙嗗彛澶栦竴鍦堣繕鏈夋病鏈夋瑺鐫€鐨勫潡锛堜换涓€绐楀彛锛夈€?/summary>
    private bool AnyPrefetchNeeded()
    {
        if (!CanvasTileCache.PrefetchEnabled) return false;
        foreach (var w in _windows)
            if (w.PrefetchNeeded()) return true;
        return false;
    }

    /// <summary>鎶婃秷鎭槦鍒楅噷鐜版湁鐨勬秷鎭叏閮ㄥ鐞嗘帀锛屼笉闃诲銆傛祴璇曟ā寮忓鐢ㄥ悓涓€浠斤紝
    /// 鍏嶅緱"娴嬮噺鐢ㄧ殑寰幆"鍜?鐪熸鐨勫惊鐜?鍥犱负椤哄簭涓嶅悓鑰岄噺鍑轰袱涓粨璁恒€?/summary>
    internal void DrainMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
            // PPT 杞绾跨▼鐨勫敜閱掓秷鎭細**涓嶆淳鍙?*锛堝畠娌℃湁瀵瑰簲鐨勭獥鍙ｏ級鈥斺€?            // 涓诲惊鐜揣鎺ョ潃鐨?StepPpt() 浼氭妸鏂板揩鐓у鐞嗘帀銆?            if (msg.message == PptWatcher.WakeMessage) continue;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    internal void RenderAll()
    {
        FrameCounter++;
        double frameStart = NowMs;
        double wall0 = _clock.Elapsed.TotalMilliseconds;

        // 鑷姩瀛樻。锛?*鏀惧湪杩欓噷**锛堟瘡甯ч兘杩囷級鑰屼笉鏄富寰幆閲屸€斺€斾富寰幆閭ｆ潯璺?        // 鍙湪"鐪熸湁甯?鏃舵墠璧板埌锛岃€岃嚜妫€鏄敤"鎶芥秷鎭紜娓叉煋"椹卞姩鐨勶紝鎸傚湪涓诲惊鐜噷
        // 鑷灏辨案杩滈獙涓嶅埌瀹冿紙绗竴鐗堝氨鏄繖涔堟紡鐨勶細鏀逛簡鏉夸功涔熶笉鍐欙級銆?        // 鑷妯″紡璧颁复鏃惰矾寰勶紙Recovery.AutoSavePathOverride锛夛紝涓嶄細纰扮敤鎴风殑鏉夸功銆?        MaybeAutoSave();
        // 鏉夸功鍘嗗彶锛? 鍙烽〉蹇収锛夛細鍙湪**鎵嬪仠涓嬫潵**涔嬪悗鍐欙紙瑙?MaybeHistorySnapshot 鐨勬敞閲婏級銆?        MaybeHistorySnapshot();

        // PPT 鐘舵€佹満銆?*涓轰粈涔堜袱澶勯兘璋?*锛堣繖閲?+ 涓诲惊鐜?Loop 閲岋級锛氫富寰幆閭ｆ潯璺?        // 鍙湪"鐪熸湁甯?鏃舵墠璧板埌锛岃€岃嚜妫€鏄敤"鎶芥秷鎭紜娓叉煋"椹卞姩鐨勶紙鍚?MaybeAutoSave
        // 鐨勭悊鐢憋級锛涘弽杩囨潵锛岀┖闂叉棤甯ф椂涔熻鑳藉搷搴旂炕椤碉紝鎵€浠?Loop 閲岄偅涓€鍙ヤ笉鑳界渷銆?        // 涓よ竟閮芥槸骞傜瓑鐨勶紙TakeDirty 鍙栬蛋灏辨竻銆丼ameAs 鎸￠噸澶嶏級銆?        StepPpt();
        StepPptBar();     // 搴曢儴閭ｆ潯锛氬紩瀵艰繃鏈?/ "鍐嶇偣纭"杩囨湡锛堢悊鐢卞悓涓婏紝鑷閭ｆ潯璺篃璧板畠锛?        StepTimerCard();  // 璇惧爞璁℃椂鍗＄墖鍚岀悊锛氳嚜妫€鐢?鎶芥秷鎭紜娓叉煋"椹卞姩锛屼笉璧颁富寰幆
        StepRollCard();   // 璇惧爞鐐瑰悕鍗＄墖鍚岀悊
        PumpRadialPalette();   // 鍛煎嚭鐩樺悓鐞嗭細鑷鐢?鎶芥秷鎭紜娓叉煋"椹卞姩锛屼笉璧颁富寰幆

        // 涔﹀啓鏈熼棿鐨?GC 浣庡欢杩熸。锛氳秴鏃堕€€鍥炪€傛斁鍦ㄨ繖閲?*鍜岃嚜鍔ㄥ瓨妗ｅ悓涓€涓悊鐢?*鈥斺€?        // 鎸備富寰幆閲岀殑璇濓紝鑷閭ｆ潯璺案杩滈獙涓嶅埌"瓒呮椂鑳介€€鍥?锛堣 GcLatency.cs锛夈€?        // `_drawing` 浼犺繘鍘伙細鎵嬪娍杩樺湪杩涜灏变竴鐩翠繚鎸侊紝鍒湪涓€绗旂殑涓€旈€€鍑烘潵銆?        GcLatency.Tick(_drawing);

        // The HUD text and the process counters cost an order of magnitude more
        // than drawing the ink does, so they refresh a few times a second rather
        // than every frame. Measured before this change: 2.7 ms + 3.5 ms per
        // frame, against 0.7 ms for the actual rendering.
        var swHud = Stopwatch.StartNew();
        bool statsRan = false;
        if (wall0 >= _nextStatsAt)
        {
            // 闈㈡澘鏄剧ず鏃舵墠闇€瑕?4 Hz 鍒锋柊锛涘彧鎵撴棩蹇楃殑璇?1 Hz 灏卞銆?            // 杩涚▼璁℃暟涓€娆¤ ~3 ms锛屾病蹇呰姣忕璺?4 娆°€?            _nextStatsAt = wall0 + (ShowHud ? 250 : 1000);
            UpdateStats();
            // 鍙湪鐪熺殑瑕佹樉绀洪潰鏉挎椂鎵嶆嫾瀛楃涓层€侭uildHudText 浼氬幓鏌ヨ繘绋嬪唴瀛橈紝
            // 閭ｆ槸鏁存潯璺緞閲屾渶璐电殑涓€姝ワ紱闈㈡澘鍏虫帀杩樺湪璺戝氨绾睘娴垂鈥斺€?            // 瀹炴祴瀹冭绌洪棽 CPU 浠?~0.3% 娑ㄥ埌 2~3.6%锛堝崟鏍革級銆?            if (ShowHud) HudText = BuildHudText();
            statsRan = true;
        }
        swHud.Stop();

        // 鐩告満鍐欑粰鍚勮鐩栫獥鍙ｏ細娓叉煋鐨勬瘡涓€澶勫彉鎹㈤兘鐢ㄥ畠锛堣 OverlayWindow.CanvasToWindow锛夈€?        //
        // 鈿?娓叉煋鐢ㄧ殑鍋忕Щ**鍙栨暣鍒版暣鍍忕礌**锛圗xcalidraw 鐨?`snapScrollToDevicePixels` 鍚屾锛夛細
        // 鍒嗗潡璐村浘璧?NearestNeighbor锛岀湡瀹炲皬鏁板亸绉讳細琚┍鍔ㄥ惛闄勶紝鑰屾椿绗斿拰鍛戒腑娴嬭瘯璧板叏绮惧害鈥斺€?        // 鍦?婊氬埌搴曡 ClampOffset 澶瑰嚭灏忔暟"鎴?缈婚〉鍔ㄧ敾鐨勪腑闂村抚"涓婏紝涓よ€呮渶澶氬樊鍗婁釜鍍忕礌
        // 锛堣〃鐜版槸钀界瑪鎻愪氦鐬棿澧ㄥ彲鑳借交杞昏烦涓€涓嬨€佹垨缂撳瓨鍐呭杞诲井鑴夊姩锛夈€?        // 鍙栨暣鍙綔鐢ㄤ簬娓叉煋灞傦細寮曟搸鑷繁鐨?`ViewOffsetY` 淇濇寔鍏ㄧ簿搴︼紙杈撳叆鏄犲皠/澶圭揣/鍙鍖鸿绠楋級锛?        // 宸€?<1px锛屽懡涓拰瑙傛劅閮藉寰椾笂銆?        float renderCamY = MathF.Round(ViewOffsetY);
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = renderCamY; }

        // 搴旂敤渚ф箍澧ㄥ眰锛團astWet锛夛細**姣忓抚鏈€澶氭彁浜や竴娆?*锛屾妸杩欎竴甯ф敹鍒扮殑鐐逛竴娆＄敾瀹屻€?        // 浣嶇疆鏈変袱澶勮绌讹細
        //   路 鏀惧湪鐩告満鍐欒繘绐楀彛**涔嬪悗**鈥斺€斿畠鐨勫彉鎹㈢敤 ViewOffset锛屽繀椤诲拰涓诲抚鍚屼竴浠斤紱
        //   路 鏀惧湪鐢讳富甯?*涔嬪墠**鈥斺€斿厛浜庢湰甯?Present 鎻愪氦锛屼繚浣?姣斾富甯ф棭"鐨勯偅鐐逛紭鍔匡紝
        //     鍚屾椂鍏嶆帀"姣忔潯杈撳叆娑堟伅涓€杞?BeginDraw/EndDraw/Commit"鐨勫紑閿€
        //     锛?026-10-14 鐢ㄦ埛鎵嬫満 120Hz 瀹炴祴锛氶偅绉嶅啓娉曞弽鑰屾渶涓嶈窡鎵嬶級銆?        if (_fastWetPending)
        {
            _fastWetPending = false;
            var fw = WindowAt(_fastWetScreenX, _fastWetScreenY);
            if (fw != null && IsFastWetStroke(ActiveStroke))
                fw.UpdateFastWet(ActiveStroke);
        }

        // 闈㈡澘鐨勬帴杈撳叆灏忕獥璺熺潃鐣岄潰杩欎竴鍒诲崰鐨勫湴鏂硅蛋锛堟柟妗?B锛夈€?        // 鏀惧湪娓叉煋涔嬪墠锛氳繖涓€甯х晫闈㈢敾鍦ㄥ摢锛岃緭鍏ュ氨璇ユ敹鍦ㄥ摢锛屼袱浠朵簨鍚屾簮銆?        UpdateUiInputWindow();

        // 璁颁笅娓叉煋鍓嶇殑鏂囨。鐗堟湰锛?*娓叉煋鏈熼棿鐣岄潰鍙兘鏀规枃妗?*锛?鎸変綇娓呯┖"灏辨槸鍦ㄧ晫闈㈢殑
        // Render 鍥炶皟閲屽鏃堕棿鐨勨€斺€旂晫闈㈡病鏈夊埆鐨?姣忓抚鍥炶皟"鍙敤锛夈€傝涓嬮潰鐨勫垽鏂€?        //
        // 鈿?鐩殑鏄?**RenderDoc**锛堝钩鏃?鐪熸枃妗ｏ紱鍥炴斁涓?褰卞瓙鏂囨。锛夛細鑴忓尯/澧為噺娓呭崟鏄?        // 鍐呭灞傜収鐫€娓叉煋鐨勯偅涓€浠界暀涓嬬殑銆備互鍓嶈繖閲屽彧娓呯湡鏂囨。锛岀粨鏋滃洖鏀鹃噷褰卞瓙鏂囨。鐨?        // `Dirty.Full` 姘歌繙娓呬笉鎺夆€斺€?*姣忔潯绗旇抗鍐欏畬閮借Е鍙戜竴娆″叏灞忔暣灞傞噸閾?*锛堢湅鐫€闂竴涓嬨€?        // 鐧借姳涓€澶х瑪閲嶇粯锛夈€?026-10-01 鐢ㄦ埛鎶?鍐欏畬涓€涓瓧浼氶棯涓€涓?灏辨槸杩欎釜銆?        var renderDoc = RenderDoc;
        long docVerBeforeRender = renderDoc.Version;

        foreach (var w in _windows)
            w.RenderFrame(this);
        foreach (var w in _windows)
            w.Present();

        if (LatencyRecording) RecordLatencySample();

        // 鐣岄潰鑷繁瑕佹眰鐨勯噸鐢伙紙InvalidateUi锛夊凡缁忓湪杩欎竴甯ц创瀹岋紝鍙互娓呮帀浜嗐€?        UiInvalidatePending = false;

        // Every window has now applied this round of changes, so the stale
        // regions can be dropped. Doing it here (rather than inside a window)
        // is what keeps multi-monitor setups correct.
        //
        // **浣嗘覆鏌撴湡闂存敼浜嗘枃妗ｅ氨涓嶈兘娓?*锛氳繖涓€甯ц创鍑哄幓鐨勬槸鏀逛箣鍓嶇殑鍍忕礌锛?        // 鑴忓尯寰楃暀缁欎笅涓€甯с€傝繖閲岃俯杩囦竴涓湡 bug锛堢敤鎴锋姤鐨?姗＄毊娓呯┖娌℃湁鐢ㄣ€?        // 杩樻妸澧ㄨ抗鍗′綇銆佽繛甯歌姗＄毊閮芥摝涓嶆帀"锛夛細娓呯┖鏄湪鐣岄潰鐨?Render 閲岃Е鍙戠殑锛?        // 寮曟搸娓叉煋瀹屾棤鏉′欢 Reset()锛屼簬鏄?*鏂囨。宸茬粡绌轰簡銆佸睆骞曚笂閭ｅ眰澧ㄨ繕鐣欑潃**鈥斺€?        // 鐪嬬潃鍍忔竻绌哄け鏁堬紝鑰屾鐨篃鎿︿笉鎺夛紙鏂囨。閲屽凡缁忔病鏈変笢瑗垮彲鎿︿簡锛夈€?        if (renderDoc.Version == docVerBeforeRender)
        {
            renderDoc.Dirty.Reset();
            renderDoc.AppendedSinceRender.Clear();
            renderDoc.StructureChangedSinceRender = false;
        }

        var w0 = _windows[0];
        _lastRebuildMs = w0.LastRebuildMs;
        _lastRecordMs = w0.LastRecordMs;
        _lastPresentMs = w0.LastPresentMs;
        _tilesUsed = w0.LastTileCount;
        _tilesVisible = w0.LastTileVisible;
        _tilesBudget = w0.LastTileBudget;
        _tilesRasterized = w0.LastPatchCount;
        _prefetchLast = w0.LastPrefetch;

        // How long the newest input took to reach the screen. This is our own
        // contribution; the display pipeline adds up to one more scan-out.
        // 鍙湪"杩欎竴甯х‘瀹炲鐞嗕簡鏂拌緭鍏?鏃堕噰鏍凤紝鑰屼笖閲囧畬灏变綔搴熴€?        //
        // 杩欓噷鍘熸潵鏈変釜 bug锛歘lastInputMs 鐢ㄥ畬涓嶆竻闆讹紝浜庢槸绌洪棽甯э紙婵€鍏夊姩鐢汇€?        // 鎴栧彧鏄瓑鍨傜洿鍚屾鐨勯偅涓€甯э級浼氭妸鍚屼竴鏉℃棫杈撳叆鍐嶇畻涓€閬嶏紝鑰屼笖瓒婄畻瓒婂ぇ锛?        // 鎶?EMA 涓€璺線涓婂甫銆傞潰鏉夸笂"杈撳叆鍒颁笂灞?閭ｄ釜鏁板洜姝ゅ亸澶т笖涓嶆敹鏁涖€?        if (_lastInputMs > 0)
        {
            double sample = _clock.Elapsed.TotalMilliseconds - _lastInputMs;
            if (sample >= 0 && sample < 200)
                _inputToPresentMs = _inputToPresentMs <= 0
                    ? sample
                    : _inputToPresentMs * 0.85 + sample * 0.15;
            _lastInputMs = -1;
        }

        double wall = _clock.Elapsed.TotalMilliseconds;
        _lastFrameMs = wall - frameStart;
        // 鎵嬫祴鍙帮細鎶婅繖涓€甯х殑"閲嶇敾 + 鏁村抚"鑰楁椂鍠傜粰閲囬泦鍣紙瀹冨彧鍦ㄦ嫋鎷戒腑绱锛夈€?        if (EraserTelemetry != null)
        {
            EraserTelemetry.Frame(_windows.Count > 0 ? _windows[0].LastPatchMs : 0,
                                  _lastFrameMs, _inputToPresentMs);
            EraserTelemetry.Beat(NowMs, _fps, Doc);
        }

        _frames++;
        if (wall - _fpsWindowStart >= 500)
        {
            _fps = _frames * 1000.0 / (wall - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = wall;
        }

        _accRecord += _lastRecordMs;
        _accPresent += _lastPresentMs;
        // 闈㈡澘鑷繁鐨勪唬浠蜂粠绐楀彛閭ｈ竟鍙栵紙閲嶆帓 + 璐村浘锛夛紝涓嶆槸杩欓噷鐨?缁熻"鑰楁椂鈥斺€?        // 娣峰湪涓€璧蜂細璁?闈㈡澘寰堣吹"杩欎欢浜嬭缁熻浠ｇ爜鐩栦綇銆?        _lastHudMs = _windows.Count > 0 ? _windows[0].LastHudMs : 0;
        _accHud += swHud.Elapsed.TotalMilliseconds;
        _accPanel += _lastHudMs;
        if (_windows.Count > 0)
        {
            double redraw = _windows[0].LastHudRedrawMs;
            if (redraw > 0) { _hudRedrawSum += redraw; _hudRedrawCount++; }
            _hudRedrawTotal = _windows[0].HudRedraws;
        }
        if (statsRan) _accStats += _lastStatsMs;
        _accFrames++;

        if (NowMs >= _nextLogAt)
        {
            _nextLogAt = NowMs + 1000;
            int n = Math.Max(1, _accFrames);
            Console.WriteLine(
                $"[{NowMs / 1000,6:F1}s] fps={_fps,5:F1}"
                + $" 璁板綍={_accRecord / n,5:F2} 涓婂睆={_accPresent / n,5:F2}"
                  + $" 闈㈡澘={_hudRedrawMs,4:F2}ms脳{_hudRedrawPerSec:F0}/s 缁熻={_lastStatsMs,5:F2}"
                  + $" 鍚堣={(_accRecord + _accPresent + _accHud + _accStats) / n,6:F2}ms"
                  + $" 缃《={_lastTopmostMs,5:F2}ms"
                  + $" 鑴忓尯={_windows[0].LastPresentRectCount}鍧?{_windows[0].LastPresentAreaPercent,5:F1}%"
                  + $" ws={_workingSetMb,6:F1}MB");
            _accRecord = _accPresent = _accHud = _accStats = 0;
            _hudAvgMs = _accPanel / n;
            _accPanel = 0;
            double secs = Math.Max(0.001, (NowMs - _hudRateWall) / 1000.0);
            _hudRedrawPerSec = (_hudRedrawTotal - _hudRedrawAtWall) / secs;
            _hudRedrawAtWall = _hudRedrawTotal;
            _hudRateWall = NowMs;
            _hudRedrawMs = _hudRedrawCount > 0 ? _hudRedrawSum / _hudRedrawCount : 0;
            _hudRedrawSum = 0; _hudRedrawCount = 0;
            _accFrames = 0;
        }
    }

    /// <summary>
    /// 鏄惧瓨宸茬敤閲忥紙MB锛夈€?    ///
    /// 涓轰粈涔堣鏄剧ず瀹冿細**鏍告樉鐨勬樉瀛樻槸浠庣郴缁熷唴瀛橀噷鍒嗙殑**锛岃€屽畠**涓嶈鍏ヨ繘绋嬪伐浣滈泦**銆?    /// 浠诲姟绠＄悊鍣ㄩ偅涓€鍒楀湪鏍告樉鏈哄櫒涓婁細鎶?GPU 鍏变韩鏄惧瓨绠楄繘鍘伙紝浜庢槸鍑虹幇
    /// "浠诲姟绠＄悊鍣?1000MB銆侀潰鏉垮彧鏈?100MB"鈥斺€斾笉鏄潰鏉夸笉鍑嗭紝鏄彧鎶ヤ簡涓€涓彛寰勶紝
    /// 鎭板ソ婕忔帀浜嗘渶澶х殑涓€鍧椼€?    /// </summary>
    private static double GpuUsedMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return 0;
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return info.CurrentUsage / 1048576.0;
        }
        catch { return 0; }
    }
    /// <summary>瑙︾偣璇婃柇锛氭寚閽堜簨浠堕『鎵嬫洿鏂帮紙鍙湁寮€鍏虫墦寮€鏃舵墠璧帮紝骞虫椂闆跺紑閿€锛夈€?/summary>
    private void TouchDiagFeed(uint id, uint ptype, bool down)
    {
        if (!TouchHud) return;
        if (ptype == Native.PT_TOUCH)
        {
            if (down)
            {
                _touchDiagIds.Add(id);
                if (_touchDiagIds.Count > TouchHudMax) TouchHudMax = _touchDiagIds.Count;
            }
            var (w, h) = ReadTouchSizePx(id);
            if (w > 0f || h > 0f)
            {
                TouchHudSawArea = true;
                if (w > _touchDiagMaxW) _touchDiagMaxW = w;
                if (h > _touchDiagMaxH) _touchDiagMaxH = h;
            }
        }
        _touchDiagLast = ptype switch
        {
            Native.PT_TOUCH => "瑙︽懜",
            Native.PT_PEN => "绗?,
            Native.PT_MOUSE => "榧犳爣",
            _ => _touchDiagLast,
        };
        // 闄愰閲嶅缓锛堣Е鐐规暟閲忓彉鍖栨椂绔嬪埢閲嶅缓锛涢潰绉偅绉嶈繛缁彉鍖栨寜 200ms 鏀跺彛锛?        if (down || NowMs >= _touchDiagNextMs)
        {
            _touchDiagNextMs = NowMs + 200;
            RebuildTouchHud();
            _dirty = true;
        }
    }

    private void RebuildTouchHud()
    {
        string area = TouchHudSawArea
            ? $"鎺ヨЕ闈㈢Н锛氳杩囨渶澶?{_touchDiagMaxW:F0} 脳 {_touchDiagMaxH:F0} 鐗╃悊鍍忕礌"
            : "鎺ヨЕ闈㈢Н锛氭病瑙佽繃闈為浂鍊?鈫?杩欏潡灞忎笉涓婃姤闈㈢Н锛堟墜鎺屾摝涓嶅彲鐢紝涓夋寚鎿︾収甯革級";
        TouchHudText = $"鏈€杩戣緭鍏ワ細{_touchDiagLast}銆€褰撳墠 {_touchDiagIds.Count} 鎸囥€€鏈€澶?{TouchHudMax} 鎸嘰n"
                     + area + "\n"
                     + "璇曪細1 / 2 / 3 鏍规墜鎸囧悇鎸変竴涓嬶紝鍐嶇敤鎵嬫帉鍘嬩竴涓?;
    }

    /// <summary>璇讳竴涓Е鎽歌Е鐐圭殑鎺ヨЕ灏哄锛堢墿鐞嗗儚绱狅級銆傜敤鏄惧紡缂撳啿鍖猴紙瑙?Native 閭ｈ竟鐨勮鏄庯級銆?/summary>
    private static (float w, float h) ReadTouchSizePx(uint id)
    {
        IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(160);
        try
        {
            if (!Native.GetPointerTouchInfo(id, buf)) return (0f, 0f);
            var ti = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINTER_TOUCH_INFO>(buf);
            // 鈿?璁惧涓嶄笂鎶ラ潰绉椂锛宺cContact 瑙勮寖榛樿鏄?浠ユ寚閽堜负涓績鐨?0脳0"鈥斺€?            // 鎵€浠ュ厛鐪?touchMask 璇存病璇村畠鏈夋晥锛?026-10-05 淇細姝ゅ墠缁撴瀯浣撴紡浜?            // touchFlags/touchMask锛屾暣浣撳亸绉?8 瀛楄妭锛岃鍒扮殑"闈㈢Н"鍏跺疄鏄潗鏍囷級銆?            if ((ti.touchMask & Native.TOUCH_MASK_CONTACTAREA) == 0) return (0f, 0f);
            int w = ti.rcContact.Width, h = ti.rcContact.Height;
            if (w <= 0 && h <= 0) { w = ti.rcContactRaw.Width; h = ti.rcContactRaw.Height; }
            return (w, h);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
    }

    private string BuildHudText()
    {
        // 闈㈡澘鐨勬帓鐗堝師鍒欙細**涓€琛屼竴绫讳簨**锛屾暟瀛楀榻愶紝鍗曚綅缁熶竴銆?        // 鍐呭瓨閭ｄ竴琛屾槸閲嶇偣锛氫笁涓彛寰勫苟鍒楋紝骞朵笖鎶?浠诲姟绠＄悊鍣ㄩ噷鍙摢涓€鍒?鍐欏嚭鏉?        // 鈥斺€旂敤鎴锋嬁瀹冭窡浠诲姟绠＄悊鍣ㄥ鐓ф椂鏈€闇€瑕佺殑灏辨槸杩欏彞璇濄€?        return
            $"甯х巼 {_fps,6:F1} fps锛堢┖闂蹭笉閲嶇粯锛?  甯ц€楁椂 {_lastFrameMs,5:F2} ms   杈撳叆鈫掍笂灞?{_inputToPresentMs,5:F1} ms\n" +
            $"缁樺埗 {_lastRecordMs,5:F2} ms      涓婂睆 {_lastPresentMs,5:F2} ms      鍒嗗潡鍏夋爡 {_lastRebuildMs,5:F2} ms\n" +
            $"闈㈡澘 {_hudRedrawMs,4:F2} ms/娆?脳{_hudRedrawPerSec,3:F0}/绉掞紙鎽婂埌姣忓抚 {_hudAvgMs,4:F2} ms锛?  缁熻 {_lastStatsMs,5:F2} ms\n" +
            $"鍐呭瓨 {_privateWsMb,6:F1} MB锛堜笓鐢ㄥ伐浣滈泦 = 浠诲姟绠＄悊鍣ㄧ殑鈥滃唴瀛樷€濓級\n" +
            $"宸叉彁浜?{_privateMb,6:F1} MB锛?鈥滄彁浜ゅぇ灏忊€濓級   宸ヤ綔闆?{_workingSetMb,6:F1} MB锛堝惈鍏变韩锛塡n" +
            $"鍏变韩鎻愪氦 {_sharedCommitMb,6:F1} MB   鏄惧瓨 {_gpuMb,6:F1} MB   CPU {_cpuPercent,4:F1} %\n" +
            $"绗旂敾 {Doc.Strokes.Count}      鐐规暟 {Doc.TotalPoints}\n" +
            $"閫変腑 {Doc.Selected.Count}      宸ュ叿 {ToolName(Tool)}{SelectModeTag()}{(PassThrough ? "锛堢┛閫忎腑锛? : "")}      绮楃粏 {CurrentToolWidthLogical,4:F1}      鎾ら攢鏍?{Doc.UndoDepth}\n" +
            $"鍒嗗潡 {_tilesUsed}/{_tilesBudget}锛堝彲瑙?{_tilesVisible}锛屾湰甯у厜鏍?{_tilesRasterized}锛岄鍙?{_prefetchLast}锛?     缃戞牸 {Doc.GridCells}\n" +
            $"Ctrl+Alt锛?绗?2鑽у厜 3婵€鍏?4姗＄毊 7鍍忕礌姗＄毊 5妗嗛€?6绮楃粏 Z鎾ら攢 C娓呴櫎鍏ㄩ儴\n" +
            (EraserTelemetry != null
                ? $"姗＄毊鎵嬫祴鍙帮細璁板綍涓?路 宸茶 {EraserTelemetry.DragCount} 鏉℃嫋鎷斤紙閫€鍑烘椂鍐欐眹鎬伙級\n"
                : "") +
            $"婀垮ⅷ妗ｄ綅 {WetMode} {WetModeName(WetMode)}锛圕trl+Alt+Shift+W 鍒囦笅涓€妗ｏ級\n" +
            $"鍏跺畠 Ctrl+Alt锛欼闈㈡澘 P绌块€?K閿洏 Y绌块€忔柟寮?X閫€鍑?;
    }

    /// <summary>闈㈡澘涓婅窡鐫€"宸ュ叿"鏄剧ず鐨勫綋鍓嶉€夋嫨鏂瑰紡锛堝彧鏈夋閫夊伐鍏风敤寰椾笂锛夈€?/summary>
    private string SelectModeTag()
        => Tool == Tool.Marquee ? (SelMode == SelectMode.Lasso ? "路濂楃储" : "路鐭╁舰") : "";

    /// <summary>宸ュ叿鍚嶏紙閬ユ祴 / HUD / 鏃ュ織鐢級銆傚悕瀛楄〃鍦ㄥ叕寮€鐨?<see cref="ToolNames"/>鈥斺€?    /// 鐣岄潰灞傜殑鎮仠鎻愮ず涔熻鍚屼竴浠斤紙2026-10-02锛夛紝閬垮厤涓ゅ鍚勫啓涓€浠姐€?/summary>
    private static string ToolName(Tool t) => ToolNames.Of(t);

    // =====================================================================
    //  Window procedure
    // =====================================================================

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // `--wintabprobe`锛歐intab 鐨勫寘娑堟伅钀藉湪 [lcMsgBase, lcMsgBase+64) 杩欎竴娈点€?        // **鍙暟鏉℃暟銆佷笉瑙ｆ瀽鍖呬綋**鈥斺€旀帰閽堝彧闇€瑕佸洖绛?椹卞姩鍒板簳鍙戜笉鍙戝寘"銆?        // 椤哄甫鎶?WT_PROXIMITY锛堝熀鍧€+5锛夊崟鐙暟涓€涓嬶細"绗斿湪闄勮繎"鍜?鐪熸湁鏁版嵁"鏄袱浠朵簨銆?        if (_wtMsgBase > 0 && msg >= (uint)_wtMsgBase && msg < (uint)_wtMsgBase + 64)
        {
            int off = (int)(msg - (uint)_wtMsgBase);
            WtByType[off]++;
            WintabPackets++;
            if (off == 5) WtProximity++;
            return IntPtr.Zero;
        }

        // **绯荤粺鏉ラ棶"瑕佷笉瑕侀偅鏉￠暱鎸夋墜鍔?**锛氫竴寰嬪洖"涓嶈"銆?        //
        // 杩欐槸鍏虫帀"鎸変綇涓嶅姩 = 鍙抽敭"鐨?*涓昏矾**锛堝彟涓ゆ潯鍦?Native.DisableSystemPressAndHold 閲岋級锛?        // 瀹樻柟鏂囨。瀵硅繖涓€浣嶇殑鍘熻瘽灏辨槸 "disables press and hold (right-click) gesture"锛?        // 涔熷氨鏄畠鍚屾椂骞叉帀鎵嬪娍鍜屽彸閿秷鎭€傚洖鍦ㄨ繖閲岃€屼笉鏄煇涓獥鍙ｅ垎鏀噷锛?        // 鏄洜涓虹郴缁熷彲鑳芥嬁**浠绘剰涓€涓?*鎴戜滑鐨勭獥鍙ｆ潵闂紙瑕嗙洊灞傘€佹帴杈撳叆灏忕獥閮界畻锛夈€?        //
        // 椤哄甫涓€鏉″畼鏂硅鏄庯細鍏虫帀涔嬪悗宸﹂敭娑堟伅**涓嶅啀闇€瑕佺瓑閭ｆ"鍖哄垎闀挎寜涓庡崟鍑?鐨勫欢杩?*锛?        // 钀界瑪鏇磋窡鎵嬨€?        if (msg == Native.WM_TABLET_QUERYSYSTEMGESTURESTATUS)
            return new IntPtr(Native.TABLET_DISABLE_PRESSANDHOLD);

        // 闈㈡澘鐨?鎺ヨ緭鍏ュ皬绐?锛堟柟妗?B锛夛細瀹冨彧鏀惰緭鍏ワ紝浠€涔堥兘涓嶇敾銆?        if (_uiInputHwnd != IntPtr.Zero && hWnd == _uiInputHwnd)
            return UiInputWndProc(hWnd, msg, wParam, lParam);

        // PPT 鏉￠偅涓€鍧楃殑"鎺ヨ緭鍏ュ皬绐?锛堝悓涓€濂楁柟妗堬紝鍙湪鏀炬槧 + 绌块€忔椂瀛樺湪锛夈€?        // 涓轰粈涔堟潯闇€瑕佸畠銆佽€岃鐩栧眰閭ｆ潯 NCHITTEST 璞佸厤涓嶅鐢紝瑙?Ppt.cs 閲?        // `_pptInputHwnd` 閭ｄ竴娈垫敞閲婏紙涓€鍙ヨ瘽锛氱┛閫忕敤鐨?WS_EX_TRANSPARENT 璁?        // 绯荤粺璺宠繃鍛戒腑娴嬭瘯锛岃眮鍏嶆牴鏈墽琛屼笉鍒帮級銆?        if (_pptInputHwnd != IntPtr.Zero && hWnd == _pptInputHwnd)
            return PptInputWndProc(hWnd, msg, wParam, lParam);

        // 璇惧爞璁℃椂鍗＄墖鐨?鎺ヨ緭鍏ュ皬绐?锛堝悓涓€濂楁柟妗堬紱鍗＄墖鍜?PPT 鏉′笉鐩搁偦锛屾墍浠ュ崟寮€涓€鍧楋級銆?        if (_timerInputHwnd != IntPtr.Zero && hWnd == _timerInputHwnd)
            return TimerInputWndProc(hWnd, msg, wParam, lParam);

        // 鐐瑰悕鍗＄墖鐨?鎺ヨ緭鍏ュ皬绐?锛堝悓涓婏級銆?        if (_rollInputHwnd != IntPtr.Zero && hWnd == _rollInputHwnd)
            return RollInputWndProc(hWnd, msg, wParam, lParam);

        // 瀹夸富鑷繁鐨勭獥鍙ｏ紙寮€鍙戞湡鐨勭偣鍑荤洰鏍囷級鍏堝鐞嗐€備骇鍝佺晫闈笉浼氱敤鍒拌繖涓€灞傘€?        if (HandleHostWindowMessage(hWnd, msg, wParam, lParam, out var hostResult))
            return hostResult;

        // 婊氳疆锛氭粴鍔ㄧ敾甯冿紙鍙敼鐩告満鍋忕Щ锛屼笉鍔ㄥ璞℃暟鎹級銆傝 HandleWheel銆?        if (msg == 0x020A /*WM_MOUSEWHEEL*/) return HandleWheel(wParam);
        // 鎵规敞閿洏妯″紡涓嬬殑鎸夐敭銆傚彧鏈夎繖涓ā寮忔敹寰楀埌鈥斺€旇 SetKeyboardMode銆?        // 宸ュ叿閿紙Ctrl+P/I/L/E/M锛夎蛋鎵嬪娍鐘舵€佹満锛岄渶瑕?鏉鹃敭"鍜?鏄笉鏄嚜鍔ㄩ噸澶?涓や欢浜嬶細
        // 鑷姩閲嶅 = lParam bit30锛堣 ToolKeyDown 鐨勮鏄庯級銆?        if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
        {
            // 鎴浘娴佺▼閲岀殑鎸夐敭锛?.3.1锛夛細**鍙栨櫙鏈熼棿鏄ā鎬佺殑**鈥斺€?            //   路 Esc = 鍙栨秷锛堜粈涔堥兘涓嶈惤锛屽洖鍒拌繘鏉ヤ箣鍓嶇殑宸ュ叿锛夛紱
            //   路 Enter = 瀹屾垚锛堣皟鏁撮樁娈碉級锛涙柟鍚戦敭 = 寰皟妗嗭紙璋冩暣闃舵锛孲hift = 鏀瑰ぇ灏忥級锛?            //   路 Ctrl+S = 鎹㈠彟涓€绉嶆埅娉曪紙閲嶆柊鍐荤粨涓€娆★級锛?            //   路 鍏跺畠閿竴寰嬪悶鎺夛紙鍙栨櫙涓寜 Ctrl+P 鎹㈠伐鍏蜂細鎶婄姸鎬佹悈涔憋級銆?            if (CaptureActive)
            {
                int vk = wParam.ToInt32();
                if (vk == 0x1B /*VK_ESCAPE*/) { CancelCapture(); return IntPtr.Zero; }
                if (CaptureAdjusting && (vk == 0x0D || vk == 0x0A) /*VK_RETURN*/)
                { ConfirmCapture(); return IntPtr.Zero; }
                if (CaptureAdjusting && (vk is 0x25 or 0x26 or 0x27 or 0x28 /*鏂瑰悜閿?/))
                { CaptureNudge(vk); return IntPtr.Zero; }
                if (vk == 0x53 /*S*/ && (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0)
                { ToggleCaptureMode(); return IntPtr.Zero; }
                return IntPtr.Zero;
            }
            // 鍙偣浜嗐€屾埅灞忋€嶆牸銆佽繕娌¤繘鍙栨櫙锛欵sc = 鎶婂伐鍏疯繕缁欒繘鏉ヤ箣鍓嶉偅涓紙鍜屽彇鏅噷鍚屼竴涓彛寰勶級
            if (Tool == Tool.Capture && wParam.ToInt32() == 0x1B)
            {
                var back = _toolBeforeCapture;
                if (back == Tool.Capture) back = Tool.Pen;
                SwitchTool(back);
                _dirty = true;
                ApplyCursor();
                NotifyUiStateChanged();
                return IntPtr.Zero;
            }
            bool repeat = (lParam.ToInt64() & 0x40000000) != 0;
            if (HandleKeyDown(wParam, repeat)) return IntPtr.Zero;
        }
        if ((msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) && CaptureActive)
            return IntPtr.Zero;                     // 鍙栨櫙鏈熼棿杩炲悓鎶墜涓€璧峰悶锛堥敭浣嶈〃鍒畫鐣欏崐濂楃姸鎬侊級
        if ((msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) && HandleKeyUp(wParam))
            return IntPtr.Zero;

        switch (msg)
        {
            case Native.WM_NCHITTEST:
                // 杩欎袱涓鏁板彧缁欒嚜妫€鐢細瀹冨洖绛?绌块€忔椂绯荤粺鍒板簳杩橀棶涓嶉棶鎴?锛?                // 杩欐槸鍒ゅ畾"鑳戒笉鑳藉彧闈犲懡涓祴璇曞仛鍖哄煙绌块€?鐨勫敮涓€纭瘉鎹€?                _cntNcHitTest++;
                if (!PassThrough)
                {
                    _cntNcHitClient++;
                    return new IntPtr(Native.HTCLIENT);
                }
                // 绌块€忔ā寮忎笅锛?*鐣岄潰鑷繁閭ｄ竴鍧椾粛鐒跺綊鐣岄潰**銆?                //
                // 浠ュ墠杩欓噷涓€寰嬪洖 HTTRANSPARENT锛屼簬鏄?寮€鐫€绌块€忚繕鎯崇敤宸ュ叿鏉?杩欎釜
                // 鏈€甯歌鐨勭粍鍚堢洿鎺ュけ鏁堬細鍛戒腑娴嬭瘯鍦ㄧ郴缁熼偅涓€灞傚氨鎶婃垜浠帓闄や簡锛?                // WM_POINTERDOWN 鏍规湰杞笉鍒板紩鎿庯紝鎮诞鐞冨彉鎴愪簡涓€涓敾鍑烘潵鐨勮楗般€?                HitTestPoint(lParam, out float hitX, out float hitY);
                // PPT 鏉★紙鏀炬槧鏃跺簳閮ㄩ偅涓ゆ潯锛変篃绠?鎴戠殑鍦扮洏"锛?*寮€鐫€绌块€忔椂鑰佸笀鐓ф牱寰楄兘
                // 鐐圭炕椤?/ 鎷栬繘搴︽潯**鈥斺€斾笉鐒堕偅涓€涓嬩細钀藉埌涓嬪眰 PPT 涓婏紝琚畠褰撴垚缈婚〉鐐瑰嚮銆?                bool mine = UiContains(hitX, hitY) || PptBarContains(hitX, hitY)
                         || ReplayBarContains(hitX, hitY)
                         || TimerCardContains(hitX, hitY) || RollCardContains(hitX, hitY);
                if (mine) _cntNcHitClient++;
                return new IntPtr(mine ? Native.HTCLIENT : Native.HTTRANSPARENT);

            // 鎸囬拡褰㈢姸銆?*蹇呴』鏄惧紡鍥炵瓟**锛氫笉澶勭悊鏃剁郴缁熶細鎷跨獥鍙ｇ被鐨勫厜鏍囧厹搴曪紝
            // 鑰岀被鍏夋爣涓?NULL 鐨勮〃鐜板氨鏄?姘歌繙杞湀"锛堣繖娆′慨鐨勫氨鏄畠锛夈€?            //
            // 鍙湪"绉诲姩涓旇緭鍏ユ湭琚崟鑾?鏃舵墠浼氭敹鍒拌繖鏉℃秷鎭€斺€旀墍浠ユ嫋鎷戒腑鐨勫厜鏍?            // 寰楀湪鎸変笅閭ｄ竴鍒昏嚜宸辫锛堣 OnPointerDown锛夛紝涓嶈兘绛夎繖閲屻€?            case Native.WM_SETCURSOR:
                // 闈炲鎴峰尯锛堣竟妗嗐€佺缉鏀捐锛夌敱绯荤粺鎸夎嚜宸辩殑瑙勭煩鏉ャ€?                if ((lParam.ToInt64() & 0xFFFF) != Native.HTCLIENT) break;
                // 绌块€忥細闈㈡澘涔嬪璁╀笅灞傜獥鍙ｅ喅瀹氾紱**闈㈡澘鑷繁閭ｄ竴鍧楄缁欑澶?*
                // 锛堝惁鍒?宸ュ叿鏉′笂椤剁潃涓€涓瑪灏栧湀"鎴栬€呬竴涓浆鍦堝厜鏍囷級銆?                if (PassThrough && !_uiHover) break;
                ApplyCursor(force: true);
                return new IntPtr(1);                   // TRUE = 宸插鐞嗭紝鍒啀鍏滃簳

            case Native.WM_POINTERENTER:
                PointerInside = true;
                TrackPointerLeave(hWnd);
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                // 鎸囬拡绂诲紑绐楀彛锛氳惤鐐瑰弽棣堬紙姗＄毊鍦嗙幆銆佺瑪灏栫幆锛夊繀椤昏窡鐫€娑堝け锛?                // 鍚﹀垯鎵嬩竴绉诲紑锛屽睆骞曚笂灏辩暀涓嬩竴涓湀銆?                PointerInside = false;
                ClearEngineTooltip();          // 鎮仠鎻愮ず鍚岀悊锛氫汉璧颁簡锛屾彁绀轰笉鑳界暀鍦ㄥ睆骞曚笂
                _dirty = true;
                ApplyCursor();
                // 娉ㄦ剰锛?*杩欓噷涓嶈兘鍙?Ui.PointerLeave()**銆傝鐩栧眰鐨?绂诲紑"鍦ㄩ潰鏉挎帴绠¤緭鍏ユ椂
                // 鎭版伆鏄?鎸囬拡杩涗簡闈㈡澘"鐨勯偅涓€鍒伙紙浜轰粠鐢诲竷绉诲埌宸ュ叿鏉′笂锛夛紝
                // 鐣岄潰鏀跺埌鐨勪細鏄弽鐨勩€傜晫闈㈤偅鏉＄寮€鐢辨帴杈撳叆灏忕獥鍙戯紙瑙?UiInputWndProc锛夈€?                return IntPtr.Zero;

            case Native.WM_MOUSELEAVE:
                PointerInside = false;
                ClearEngineTooltip();
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
                OnPointerDown(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUPDATE:
                OnPointerMove(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUP:
                OnPointerUp(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_INPUT:                     // `--rawprobe` 鍙暟鏁帮紱`--rawinput` 椤哄甫琛ラ噰鏍风偣
                HandleRawInput(lParam);
                return IntPtr.Zero;
            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                EndStroke();
                return IntPtr.Zero;

            // 鍙抽敭锛氭埅鍥炬祦绋嬮噷 = 鍙栨秷锛堝拰 Esc 涓€鏉″彛寰勶紝8.3.0锛夈€?            // 榧犳爣鍜岀瑪鐨勬《鐘堕敭閮戒細鍚堟垚 WM_RBUTTONDOWN锛屾墍浠ヨ繖涓€鏉′袱绉嶈澶囬兘瑕嗙洊銆?            case Native.WM_RBUTTONDOWN:
                if (CaptureActive) { CancelCapture(); return IntPtr.Zero; }
                break;

            case Native.WM_HOTKEY:
                HandleHotkey(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                // **鍋滈】鎴愬瀷閭ｉ 40ms 鐨勫畾鏃跺櫒**锛坕d = 2锛夊彧闂竴浠朵簨锛?绗斿仠浜嗗涔?銆?                // 椤烘墜鍋氱殑缃《閭ｄ竴濂楋紙id = 1 閭ｉ 250ms 鐨勶級鍦ㄨ繖閲?*涓嶉噸澶嶅仛**鈥斺€?                // 涓嶅垎寮€鐨勮瘽瀹冧細璺熺潃 40ms 鐨勮妭濂忚窇锛屼竴绉掑嚟绌哄 25 娆＄疆椤躲€?                if (wParam.ToInt32() == DwellTimerId)
                {
                    TickDwellShape();
                    return IntPtr.Zero;
                }
                // 鍙湁瑕佹樉绀烘€ц兘闈㈡澘鏃舵墠闇€瑕佸懆鏈熸€ч噸缁橈紱闈㈡澘鍏虫帀杩樻瘡绉掗噸鐢?4 娆?                // 绾睘鐧界儳鐢点€?                if (ShowHud) _dirty = true;
                UpdateScrollBarHoverFromCursor();
                var swTop = Stopwatch.StartNew();
                ReassertTopmost();
                swTop.Stop();
                _lastTopmostMs = swTop.Elapsed.TotalMilliseconds;
                MaybeSweepHistory();      // 鍚姩鍚庨涓┖闂插抚鍋氫竴娆″巻鍙叉竻鐞嗭紙澧ㄨ抗 B锛?                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _quit = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 璁╁涓诲鐞嗗睘浜庡畠鑷繁鍒涘缓鐨勭獥鍙ｇ殑娑堟伅銆傝繑鍥?true 琛ㄧず娑堟伅宸茶娑堣垂銆?    /// </summary>
    protected virtual bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private void ReassertTopmost()
    {
        // **寮圭潃绯荤粺瀵硅瘽妗嗙殑鏃跺€欎竴寰嬩笉鎶?*锛堢敤鎴?2026-09-17 鎶ョ殑"鐐瑰紑涓€涓嬪氨鏀跺洖鍘汇€?        // 閫変笉鍒?jpg"锛?026-09-17 鎺㈤拡瀹氫綅锛夈€?        //
        // 杩欎竴鎶槸姣忕涓€娆＄殑瀹氭椂鍣ㄥ共鐨勶紙WM_TIMER锛夛紝骞虫椂鏄繀瑕佺殑锛氬叏灞忚鐩栧眰琚埆鐨?        // 绋嬪簭鎶㈠埌鍚庨潰鍘诲氨"鐪嬩笉瑙佷篃鐢讳笉涓?銆備絾**寮圭潃"鍙﹀瓨涓?鐨勬椂鍊欏畠鏄湁瀹崇殑**锛?        // 瑕嗙洊灞傝閲嶆柊鎶埌缃《锛屽氨鍘昏窡瀵硅瘽妗嗘姠閭ｄ竴灞傦紝瀵硅瘽妗嗚嚜宸辩殑**涓嬫媺鍒楄〃**
        // 锛堟櫘閫氬脊绐楋紝涓嶅湪缃《灞傦級闅忓嵆琚洊浣?/ 琚叧鎺夆€斺€旀帰閽堣繛鐫€鎷嶄笁寮犵湅寰楀緢娓呮锛?        // 鐐逛笅鎷?150 姣鏃跺垪琛ㄥソ濂藉湴寮€鐫€锛圥NG / JPEG 涓ゆ潯锛夛紝900 姣鏃跺凡缁忔病浜嗐€?        // 閭?900 姣姝ｅソ鍘嬬潃涓€娆″畾鏃跺櫒銆?        //
        // 瀵硅瘽妗嗘湡闂寸敱 ExportFileDialog 鑷繁淇濊瘉"瀹冨湪鏈€涓婇潰銆佽€屼笖涓€鐩寸疆椤?
        // 锛堣 CenterAndBringUp锛夛紝鍏虫帀涔嬪悗 ReturnFocusAfterDialog 鍐嶆妸瑕嗙洊灞傛嬀鍥炴潵銆?        if (ExportDialogOpen) return;

        // **璁╄矾涓€鎷?*锛氬墠鍙版槸鍒殑"缃《灞?绐楀彛锛堝井淇℃埅鍥俱€乄in+Shift+S 鐨勬埅鍥炬潯绛夛級鏃朵笉鎶€斺€?        // 姣忕涓€娆＄殑鎶妇浼氭妸鎴戜滑閲嶆柊鍘嬪埌瀹冧笂闈紝鎴浘妗嗗湪灞忓箷涓婂氨姘歌繙鐪嬩笉瑙?        // 锛堢敤鎴?2026-10-03 鎶?绯荤粺鎴浘琚壒娉ㄥ眰鐩栦綇"锛夈€傜瓑鍓嶅彴鍥炲埌鎴戜滑鎴栨櫘閫氱獥鍙ｏ紝
        // 涓嬩竴鎷嶅畾鏃跺櫒鑷劧鎶洖鏉ャ€?        if (ShouldYieldTopmost()) return;

        foreach (var w in _windows)
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // 鎺ヨ緭鍏ュ皬绐?*蹇呴』鎺掑湪鏈€鍚庢姮**锛氫袱鍧楅兘鏄疆椤剁獥鍙ｏ紝璋佹渶鍚?SetWindowPos
        // 璋佸湪涓婇潰锛堣 璁″垝-搴曞眰瀵规帴鐣岄潰.md 4.1 鐨?灞傚簭"绾緥锛夈€傚彧鍦ㄨ繖涓€澶勬姮锛?        // 鍒涓嶈鍐嶆姮瀹冿紝鍚﹀垯浼氬嚭鐜?闈㈡澘鍋跺皵琚嚜宸辩殑绗旇抗灞傜洊浣?銆?        if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 鍓嶅彴鏄笉鏄?鍒殑缃《灞傜獥鍙?鈥斺€旀槸灏辫璺竴鎷嶏紙鎴浘宸ュ叿/缃《鎾斁鍣ㄧ瓑閮芥槸杩欎竴灞傦級銆?    /// 鏅€氱獥鍙ｄ笉鐢ㄨ锛氭垜浠姮涓婂幓鏈潵灏变笉鎸″畠銆?    /// </summary>
    private bool ShouldYieldTopmost()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        long ex = Native.GetWindowLongPtr(fg, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOPMOST) == 0) return false;
        return !IsOwnWindow(fg);
    }

    /// <summary>杩欎釜绐楀彛鏄笉鏄垜浠嚜宸卞垱寤虹殑锛堣鐩栧眰 / 鍚勬帴杈撳叆灏忕獥锛夈€?/summary>
    private bool IsOwnWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        foreach (var w in _windows) if (w.Hwnd == h) return true;
        return h == _uiInputHwnd || h == _pptInputHwnd
            || h == _timerInputHwnd || h == _rollInputHwnd;
    }

    /// <summary>
    /// 杩欎竴鍒昏涓嶈鍑轰竴甯э細鑴忎簡锛屾垨鑰呮湁涓滆タ杩樺湪鍔ㄣ€?    ///
    /// 涓変釜"鍦ㄥ姩"鐨勬潵婧愶紝鍚勬湁鍚勭殑鐞嗙敱锛?    ///   路 婵€鍏夎建杩癸細瀹?*鏉炬墜鍚庡仠鐣?2 绉掑啀鏁翠綋娣″嚭**锛堣 `LaserTrail`锛夛紝涓嶉┍鍔ㄧ殑璇?    ///     鍋滅暀缁撴潫閭ｄ竴鍒绘病浜哄幓鎺ㄨ繘娣″嚭锛岄偅閬撳厜鑺掍細涓€鐩存寕鍦ㄥ睆骞曚笂锛?    ///   路 姝ｅ湪涔﹀啓锛氱瑪灏栬繖鏉＄嚎姣忓抚閮藉湪鍙橈紱
    ///   路 **鐣岄潰鑷繁澹版槑鐨勫姩鐢?*锛?see cref="IOverlayUi.IsAnimating"/>锛夛細
    ///     灞曞紑/鏀惰捣銆佹偓鍋滃睍寮€銆佽创杈瑰惛闄勫叏闈犲畠锛岀己浜嗗氨鏄?鍔ㄧ敾鍋滃湪绗竴甯?銆?    ///
    /// 鑷鐩存帴璋冨畠鏉ユ暟甯р€斺€旇繖鏍?寮曟搸缁欎笉缁欏抚"鐢ㄧ殑鏄?*鍚屼竴涓垽鎹?*锛?    /// 涓嶄細鍑虹幇"娴嬬殑鏄竴濂椼€佽窇鐨勬槸鍙︿竴濂?銆?    /// </summary>
    internal bool NeedsFrame()
    {
        _animating = Laser.Visible || _drawing || SelFlashing
                   || UiIsAnimatingNow || _camAnimating
                   || _replayPlaying            // 鍥炴斁鎾潃锛氭寔缁嚭甯э紙鏆傚仠鍗冲仠锛?                   || TimerWantsFrame            // 璁℃椂鍣ㄨ窇鐫€/鍒扮偣闂儊锛堝€掕鏃?1Hz銆佺琛ㄨ繛缁級
                   || RollWantsFrame             // 鐐瑰悕婊氬姩锛?0ms 涓€璺筹紝瀹氭牸鍗冲仠锛?                   || RadialPaletteActive        // 鍛煎嚭鐩樺紑鐫€瑕佽繛缁嚭甯э紙鍑虹洏寤惰繜 + 鏉炬墜杞锛?                   || TooltipPending;            // 寮曟搸渚ф偓鍋滄彁绀鸿繕鍦ㄧ瓑 500ms锛堝埌鐐硅鏈変汉鐐逛寒瀹冿級
        return _dirty || _animating
               || _windows.Exists(w => w.TrailEndPending)     // --syswet锛氭敹绗旀挙杞ㄨ抗蹇呴』鏈変竴甯у厹搴?               || _windows.Exists(w => w.FastWetEndPending);  // 搴旂敤渚ф箍澧ㄦ敹绗旂殑鎾ら櫎鍚屾牱瑕佷竴甯у厹搴?    }

    // =====================================================================
    //  Pointer input
    // =====================================================================

    private void OnPointerDown(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntDown++;
        ClearEngineTooltip();            // 鎸変笅 = 鏂板姩浣滃紑濮嬶紝鎮仠鎻愮ず鍏堟敹
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        TouchDiagFeed(id, ptype, down: true);

        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 鐩告満锛氬睆骞?鈫?鐢诲竷

        // 鍛煎嚭鐩樺紑鐫€鏃剁殑钀界偣锛?*鐐瑰湪鐩橀潰閲?= 鐐归€夋墖鍖哄苟纭**锛堣Е灞?涓ゆ寚杞荤偣鍛煎嚭"闈犲畠锛?        // 榧犳爣/绗斿悓鏍峰彲鐢級锛涚偣鍦ㄧ洏澶?= 鍙栨秷锛岃繖涓€涓?*缁х画璧板師璺?*锛堣鍐欏瓧鍐欏瓧锛夈€?        // 锛堟棫琛屼负鏄?浠讳綍钀界瑪涓€寰嬪彇娑?鈥斺€旂幇鍦ㄥ鍑?鐩樹笂鐐归€?杩欎竴鏉★紱璁捐绋块檮褰?C 鐨?        //   "钀界瑪琛ㄧず鎴戣鍐欏瓧浜?浠嶇劧鎴愮珛锛氱偣鐩樺杩樻槸鍙栨秷銆傦級
        if (RadialPaletteActive && RadialPaletteTryPointerDown(x, y)) return;

        // 鍥惧簱闈㈡澘鎺掑湪鐣岄潰涔嬪墠锛氬畠浠庡伐鍏锋潯涓婃部**寰€涓婇暱**锛屼袱鍧楁湰鏉ヤ笉閲嶅彔锛?        // 浣嗛『搴忓啓娓呮鈥斺€旈潰鏉挎槸鑷繁鐨勬诞灞傦紝鍏堥棶瀹冦€?        // 鎸夊湪闈㈡澘澶?= 鍏堟妸瀹冩敹璧锋潵锛岀劧鍚庤繖涓€涓?*鐓у父寰€涓嬮潰璧?*锛堝悓棰滆壊/灞傜骇闈㈡澘鐨勫彛寰勶細
        // 闈㈡澘澶栫殑绗竴涓嬫棦鏀堕潰鏉裤€佷篃涓嶈€借鐢伙級銆俙LibraryPointerDown` 鍐呴儴杩樹細绠?        // "闈㈡澘閲岀殑绌虹櫧"锛堝悆鎺夛紝涓嶇┛閫忓埌鐢诲竷锛夈€?        if (LibraryPanelOpen)
        {
            if (LibraryPointerDown(x, y)) { ApplyCursor(); return; }
            CloseLibraryPanel();
        }

        // 鍥炴斁锛堝ⅷ杩?C锛変紭鍏堜簬鐣岄潰涓庣敾甯冿紝涓夌钀界偣锛?        //   路 鎺у埗鏉′笂 鈫?褰掑畠锛堟挱鏀?鏆傚仠銆佸€嶉€熴€佹嫋杩涘害銆佸叧闂級锛?        //   路 鐣岄潰涓?鈫?**鍏堥€€鍑哄洖鏀?*锛岃繖涓€涓嬬収甯哥粰鐣岄潰锛堟崲宸ュ叿/寮€闈㈡澘 = 鑰佸笀瑕佹帴绠′簡锛夛紱
        //   路 鐢诲竷涓?鈫?**鏆傚仠/缁х画锛屼笉钀藉ⅷ**锛堣璇炬椂鏈€椤烘墜鐨勫姩浣滐紝鐢ㄦ埛宸叉媿鏉匡級銆?        if (_replay != null)
        {
            if (ReplayBarContains(screenX, screenY))
            {
                if (ReplayPointerDown(screenX, screenY))
                {
                    _drawing = false;
                    Native.SetCapture(hWnd);
                    _replayCapturing = true;
                    _dirty = true;
                    ApplyCursor();
                    return;
                }
                return;
            }
            // 鐣岄潰鍜?PPT 鏉°€佽鏃跺崱鐗囬兘绠?瑕佹帴绠?锛氬厛閫€鍑哄洖鏀撅紝杩欎竴涓嬬収甯稿線涓嬭蛋
            //锛堢偣 PPT 鏉?= 缈婚〉/闀挎寜鑿滃崟/璺抽〉锛涚偣鐣岄潰 = 鎹㈠伐鍏?寮€闈㈡澘锛涚偣鍗＄墖 = 鏆傚仠/鍋滐級銆?            if (UiContains(screenX, screenY) || PptBarContains(screenX, screenY)
                || TimerCardContains(screenX, screenY) || RollCardContains(screenX, screenY))
            {
                StopReplay("鐐圭晫闈㈡垨 PPT 鏉?);
                // 涓?return锛氳繖涓€涓嬬収甯哥粰涓嬮潰鐨勭晫闈?/ PPT 鏉″鐞?            }
            else
            {
                ReplayTogglePause();
                _dirty = true;
                return;
            }
        }

        // 椤靛彿闈㈡澘锛圥PT / 鏂囨。锛夊紑鐫€鏃讹細鐐?鍒殑鍦版柟"灏辨敹璧锋潵鈥斺€?        // 2026-10-09 鐢ㄦ埛瀹氾細"璺冲畬涓嶆秷澶便€佸彲浠ユ帴鐫€鐐癸紱鐐瑰叾浠栦綅缃墠娑堝け"銆?        // 闈㈡澘/鏉¤嚜宸辩殑鍛戒腑涓嶅湪杩欓噷澶勭悊锛堥偅鏄?PptBarPointerDown 鐨勬椿锛氭牸瀛愰噷璺抽〉銆?        // 閲岄潰绌虹櫧鍚冩帀銆佹潯涓婂啀鐐归〉鐮佹牸 = 鏀惰捣锛夈€傝繖閲屽彧鍏?杩?PptBar 閮芥病璧板埌"鐨勮惤鐐光€斺€?        // 鏈€鍏稿瀷鐨勬槸**鐐圭晫闈紙宸ュ叿鏉★級**锛氬畠鍦?PptBar 涔嬪墠灏辨妸鎸変笅鍚冩帀浜嗐€?        if (PptPagePanelOpen && !PptBarContains(screenX, screenY))
        {
            PptPagePanelOpen = false;
            _dirty = true;
        }

        // 鐣岄潰浼樺厛锛氱偣鍦ㄦ偓娴潯涓婂氨鏄搷浣滅晫闈紝涓嶆槸鐢讳竴绗斻€?        //
        // 涓や欢瀹规槗韪╃殑浜嬶細
        //  1) 杩欎竴娈靛繀椤?*鎺掑湪绌块€忓垽鏂箣鍓?*鈥斺€斾笉鐒跺紑鐫€绌块€忔椂鐣岄潰姘歌繙鏀朵笉鍒版寜涓嬶紱
        //  2) 浼犵粰鐣岄潰鐨勬槸**閫昏緫灞忓箷鍧愭爣**锛屼笉鏄敾甯冨潗鏍囥€傜晫闈㈢殑甯冨眬涓庣粯鍒堕兘鍦?        //     灞忓箷鍧愭爣閲岋紝鍠傚畠鐢诲竷鍧愭爣鐨勮瘽鐩告満涓€婊氾紝鍛戒腑灏辨暣浣撳亸鎺変竴鍧椼€?        //
        // 娉細闈㈡澘鍦ㄥ疄闄呬骇鍝侀噷鐢?鎺ヨ緭鍏ュ皬绐?锛堟柟妗?B锛夋帴绠★紝璧扮殑涓嶆槸杩欐潯璺紱
        // 杩欓噷鐣欑潃鏄?*鍏滃簳**鈥斺€斾竾涓€閭ｅ潡灏忕獥娌″缓璧锋潵锛岃嚦灏戦潪绌块€忔ā寮忎笅杩樿兘鐢ㄣ€?        if (UiPointerDown(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted,
                          ptype == Native.PT_TOUCH, id))
        {
            _drawing = false;
            // 鐣岄潰涔熻鎹曡幏鎸囬拡锛氭嫋鍑烘偓娴潯銆佸湪鎸夐挳涓婃粦寮€閮介渶瑕佺户缁敹鍒版秷鎭€?            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 钀藉湪鐣岄潰鐢荤殑閭ｄ竴鍧楅噷銆佷絾鐣岄潰娌″悆杩欎竴涓嬶細杩欎竴涓嬪氨绠椾簡銆?        // 鈶?涓嶈惤澧ㄢ€斺€旈潰鏉垮簳涓嬬殑澧ㄧ湅涓嶈锛岄潰鏉夸竴鎸張鍐掑嚭鏉ワ紝灞炰簬"鐪嬩笉瑙佸嵈琚涓嬫潵"锛?        // 鈶?涓嶉€忕粰涓嬪眰鈥斺€旂晫闈㈠０鏄庡崰鐢ㄧ殑鍖哄煙锛圦ueryBounds锛夊氨鏄畠鐨勫湴鐩橈紝
        //    瑕佹斁琛屽緱璁╃晫闈㈣嚜宸卞埆鎶婇偅涓€鍧楃畻杩涘幓銆?        if (UiContains(screenX, screenY))
        {
            _uiHover = true;
            _drawing = false;
            _dirty = true;
            return;
        }

        // 璇惧爞璁℃椂鍗＄墖锛堝紩鎿庝晶娴眰锛夛細**鎺掑湪鐣岄潰涔嬪悗銆丳PT 鏉′箣鍓嶃€佺┛閫忎箣鍓?*鈥斺€?        // 鍜?PPT 鏉″悓涓€鏉¤鐭╋細鐪嬪緱瑙佺殑閭ｄ竴鍧楀氨鏄偣寰楀埌鐨勶紙绌块€?鏀炬槧涓嬩篃瑕佽兘鏆傚仠/鍋滐級銆?        if (TimerCardPointerDown(screenX, screenY))
        {
            _drawing = false;
            _timerCapturing = true;         // 杩欎竴娆″綊瀹冿細鏉炬墜鏃剁敱瀹冩敹灏撅紙瑙?OnPointerUp锛?            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 璇惧爞鐐瑰悕鍗＄墖锛堝紩鎿庝晶娴眰锛夛細鍜岃鏃跺崱鍚屼竴妲戒綅瑙勫垯銆?        if (RollCardPointerDown(screenX, screenY))
        {
            _drawing = false;
            _rollCapturing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 搴曢儴閭ｄ袱鏉?PPT 鎺т欢锛堟斁鏄犳椂鎵嶅湪锛夛細**鎺掑湪鐣岄潰涔嬪悗銆佺┛閫忎箣鍓?*銆?        //   路 鐣岄潰鍦ㄥ畠涓婇潰锛堣 RenderFrame 鐨勭粯鍒堕『搴忥級锛屾墍浠ョ晫闈㈠厛闂紱
        //   路 蹇呴』鍦ㄧ┛閫忎箣鍓嶁€斺€斿紑鐫€绌块€忔椂鑰佸笀鐓ф牱寰楄兘鐐圭炕椤?/ 鎷栬繘搴︽潯
        //     锛圢CHITTEST 閲岀粰瀹冨紑浜嗗尯鍩熻眮鍏嶏紝瑙侀偅閲岀殑娉ㄩ噴锛夈€?        if (PptBarPointerDown(screenX, screenY))
        {
            _drawing = false;
            _pptCapturing = true;           // 杩欎竴娆″綊瀹冿細鏉炬墜鏃剁敱瀹冩敹灏撅紙瑙?OnPointerUp锛?            Native.SetCapture(hWnd);        // 闀挎寜瑕佹寔缁敹鍒扮Щ鍔紙鍒?鎷胯捣鍚庢湁娌℃湁鎷栬蛋"锛?            _dirty = true;
            ApplyCursor();
            return;
        }

        // 鐣岄潰涔嬪鐨勭┛閫忥細浜や笅灞傜獥鍙ｏ紝鎴戜滑涓嶆敹杩欎竴涓嬨€?        if (PassThrough) return;

        // 鍥惧簱閲岀偣浜嗕竴寮犱箣鍚庯紙`_libraryPending` 闈炵┖锛夛細**杩欎竴娆℃寜涓嬪氨鏄?鎶婂畠鏀句笅鏉?**
        // 鈥斺€斿拰"绮樿创瀵硅薄"鍚屼竴鏉¤矾锛堜竴姝ユ挙閿€銆佹斁瀹岃嚜鍔ㄩ€変腑锛夈€?        // 浣嶇疆锛氭帓鍦ㄧ晫闈箣鍚庯紙鐐瑰湪宸ュ叿鏉′笂涓嶈璺戝埌鐢诲竷涓婃彃涓滆タ锛夈€佽惤绗斾箣鍓?        //锛堝畠涓嶆槸"鐢讳竴绗?锛屼笉鑳借繘 _drawing锛夈€?        if (LibraryInsertArmed)
        {
            TryInsertLibraryAt(x, y);
            ApplyCursor();
            return;
        }

        // 瑙︽懜鎵嬪娍灞傦紙8.4.0锛夛細**瑙︽懜鐨勬寜涓嬪繀椤诲厛鍠傝Е鐐硅〃锛屽啀鍋?鍚屼竴鏃跺埢鍙窡涓€鏉℃寚閽?鐨勫畧鍗?*銆?        //
        // 鈿?娆″簭灏辨槸杩欓噷鏈€瑕佸懡锛?026-10-05 瀹炴祴鏍瑰洜锛夛細绗竴鏍规墜鎸囪惤涓嬬殑鐬棿 `_drawing`
        // 灏辨垚 true 浜嗭紝瀹堝崼鏀惧湪鍓嶉潰鐨勮瘽**绗簩鏍规墜鎸囨案杩滆繘涓嶄簡瑙︾偣琛?*鈥斺€?        // 鍚堟垚娉ㄥ叆鎺㈤拡褰撴椂璇诲嚭鏉ユ槸"涓ゆ寚鎸変笅鍚庯細瑙︾偣 = 1銆佹ā寮?= Write"锛屽弻鎸囨墜鍔挎案杩滆捣涓嶆潵銆?        // 瑙︾偣琛ㄥ彧璐熻矗"璁颁笅杩欎竴鍒绘湁鍑犳牴鎵嬫寚"锛涙姠涓嶆姠绗旂敱 TouchVerdict 鍥炵瓟锛?        //   路 Gesture2 / Erase / Roam 鈫?鍚冩帀锛屽苟鎺ョ activePointer锛堟垚涓烘墜鍔夸富浜猴級锛?        //   路 Ignore锛堝啓瀛椾腑閫旇弓鍒扮殑鎵嬫寚锛夆啋 鍚冩帀锛屼絾**涓嶈鎶?* activePointer
        //     锛堟姠浜嗙涓€鏍规墜鎸囩殑绉诲姩灏变細琚?`id != _activePointer` 鎸℃帀銆佹鍦ㄥ啓鐨勫瓧褰撳満鏂嚎锛夛紱
        //   路 Write 鈫?涓嶆秷璐癸紝缁х画璧颁笅闈㈠師鏉ョ殑鍐欏瓧瀹堝崼涓庤捣绗斻€?        //
        // 鍙湁"褰撳墠鍦ㄥ啓鐨勪篃鏄Е鎽?锛堟垨娌℃湁鎸囬拡鍦ㄥ啓锛夋椂鎵嶅杺锛氱瑪 / 榧犳爣鍦ㄥ啓鐨勬椂鍊欙紝
        // 瑙︽懜鐓ф棫璧板畧鍗拷鐣ワ紝涓嶆悈灞€銆佷篃涓嶅湪琛ㄩ噷鐣欐畫鐣欍€?        if (ptype == Native.PT_TOUCH && (!_drawing || _activePointerType == Native.PT_TOUCH)
            && TouchDownDispatch(hWnd, id, x, y, ReadTouchSize(id), out bool touchSteal))
        {
            if (touchSteal)
            {
                _activePointer = id;
                _activePointerType = ptype;
                PointerX = x; PointerY = y; PointerInside = true;
            }
            return;
        }

        // **鍚屼竴鏃跺埢鍙窡涓€鏉℃寚閽?*鈥斺€旇繖鏄?OnPointerMove/OnPointerUp 閲岄偅鍙?        // `id != _activePointer` 鐨勫彟涓€鍗娿€傚凡缁忔湁鎸囬拡鍦ㄦ墜锛堟鍦ㄥ啓銆佹鍦ㄦ嫋婊氬姩鏉★級鏃讹紝
        // 鍚庢潵鐨勬寜涓嬬洿鎺ュ拷鐣ャ€?        //
        // 杩欐潯瀹堝崼浠ュ墠娌℃湁锛岃€?*鍙湁瑙︽懜韪╁緱鍒?*锛氶紶鏍囧拰绗斾竴娆″彧鏈変竴涓寚閽堬紝
        // 瑙︽懜灞忎笂鍐欏瓧鏃惰弓鍒扮殑绗簩鏍规墜鎸囷紙鎴栬€呰惤灞忕殑鎺屾牴锛変細璁╀笅闈?`_activePointer = id`
        // 鎶婃寚閽堟姠璧般€乣ActiveStroke` 琚崲鎴愭柊鐨勪竴绗斺€斺€旇€屾鍦ㄥ啓鐨勯偅涓€绗?*杩樻病杩涙枃妗?*锛?        // 浜庢槸"鍐欎竴鍗婄殑瀛楀嚟绌烘秷澶?銆傚畠椤哄甫灏辨槸鏈€鍩虹鐨勬帉蹇冩姂鍒讹細鎺屾牴涓嶅啀鎶㈢瑪銆?        if (_drawing) return;

        _activePointer = id;
        _activePointerType = ptype;
        PointerX = x; PointerY = y; PointerInside = true;

        // 婊氬姩鏉℃涔嬶紙**2026-10-09 璧峰仠鐢?*锛岃 ScrollBarEnabled锛夛細瀹冨彧鍗犲彸杈圭紭涓€鏉＄獎甯︼紝
        // "鎷栨粦鍧?姣?鍦ㄩ偅鍎跨敾涓€绗?鏇寸壒娈娾€斺€斾唬浠锋槸鍙崇紭 16px 浼氬悆鎺?鍐欏埌杈圭紭"鐨勮捣绗斻€?        if (ScrollBarEnabled && TryBeginScrollBarDrag(screenX, screenY))
        {
            _drawing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ptype == Native.PT_TOUCH)
        {
            _touchMode = TouchMode.Write;      // 鍗曟寚鍐欏瓧锛氶暱鎸夌敱蹇冭烦鍒わ紙瑙?TouchTick锛?            StartDwellTimer();
        }

        _drawing = true;
        Native.SetCapture(hWnd);
        // 涔﹀啓浼氳瘽寮€濮嬶細杩?GC 浣庡欢杩熸。锛堣 GcLatency.cs锛夛紝骞惰捣鍒嗛厤/GC 浠〃銆?        // 涓€绗旀帴涓€绗斿啓鐨勬椂鍊欙紝浣庡欢杩熸。浼氫竴鐩村緟鐫€锛涙渶鍚庝竴绗旂粨鏉熸弧鍑犵鎵嶉€€鍥炪€?        GcLatency.Enter();
        BeginStrokeMeasure();
        // 鎷栨嫿涓郴缁熶笉鍐嶅彂 WM_SETCURSOR锛堣緭鍏ュ凡琚崟鑾凤級锛屽厜鏍囧繀椤诲湪鎸変笅杩欎竴鍒诲畾涓嬫潵銆?        ApplyCursor();

        // 鎴浘锛?.3.1锛夛細寰呮満 / 鎷栨 / 璋冩暣涓夌鐘舵€佸厛閮借繃杩欓噷銆?        // 杩斿洖 true = 杩欎竴涓嬭鎴浘鍚冩帀锛沠alse = 鐓у父璧板伐鍏峰垎娴侊紙涔熷氨鏄?浠庤繖閲岃捣妗?锛夈€?        if (CaptureActive && CapturePointerDown(x, y))
        {
            _capturePressHandled = true;
            return;
        }

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                EraserTelemetry?.BeginDrag(Tool.Eraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseAt(x, y, EraserRadius);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.PixelEraser:
                _lastEraseX = x; _lastEraseY = y;
                ResetDynamicEraser();
                PixelEraseDragging = true;
                Doc.BeginEraseRect();
                EraserTelemetry?.BeginDrag(Tool.PixelEraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseRectAt(x, y, PixelEraserCursorHalfWidthPx, PixelEraserCursorHalfHeightPx);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.Capture:
                // 鍜屾閫夊悓涓€涓墜鍔裤€?*鍜屾閫変竴鏍峰瓨鐢诲竷鍧愭爣**锛堟覆鏌撻偅涓€灞傚氨
                // 涓嶅繀涓哄畠鍗曠嫭鎹㈢畻涓€娆★級锛屾姄灞忔椂鍐嶆崲绠楀洖灞忓箷鍧愭爣
                // 锛坰creen = canvas + 鐩告満鍋忕Щ锛岃 EndCapture锛夈€?                BeginCaptureAt(x, y);
                break;

            case Tool.Marquee:
                // 鍏堥棶閫夋嫨鎵嬪娍锛堟寜閽?/ 鎵嬫焺 / 鏁翠綋鎷栧姩锛夛紱閮芥病鎺ユ墠璧锋柊鐨勬閫夈€?                // 淇グ閿湪杩欎竴鍒昏涓€娆″氨浼犺繘鍘伙紙鑰屼笉鏄湪鍒ゅ畾鍑芥暟閲岃閿洏锛夆€斺€?                // 杩欐牱"Shift 鍔犻€?/ Alt 鍑忛€?杩欐潯鍒嗘敮鑳借鑷鐩存帴椹卞姩銆?                if (!TryBeginSelectionGesture(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0))
                {
                    BeginMarqueeAt(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0);
                }
                break;

            case Tool.Laser:
                // **鏂拌捣涓€鏉?*锛堟姮鎵嬮偅鍑犳潯杩樺湪娣″嚭锛屽師鏍风暀鐫€鈥斺€旂収 ClassIn锛氬彲浠ュ悓鏃舵湁濂藉嚑鏉★級銆?                // 绮楃粏鍦ㄨ繖閲岃杩涜繖涓€鏉★紙姣忔潯鑷繁璁帮紝瑙?`LaserTrail.Stroke.WidthLogical`锛夈€?                Laser.Begin(x, y, NowMs, LaserWidthLogical);
                break;

            // 鍥惧舰宸ュ叿锛?*鍚屼竴涓墜鍔?*"鎸変笅璁拌捣鐐?鈫?鎷栧姩鏀圭粓鐐?鈫?鏉炬墜鎻愪氦"
            // 锛堟帴涓€涓瓑浜庢帴涓冧釜锛屽樊鍒彧鍦?Kind锛岃 璁″垝-鍥惧舰宸ュ叿.md 8.2 / 9.1锛夈€?            //
            // 鍒ゆ嵁璧?<see cref="IsShapeTool"/>锛?*涓嶅啀鍦ㄨ繖閲岄€愪釜鍒?case**锛?            // 浠ュ墠杩欓噷鏄竴涓?`case Tool.Line: 鈥锛屽拰 IsShapeTool 鏄?*鍚屼竴浠朵簨鐨勪袱浠藉悕鍗?*銆?            // 2026-09-19 鍔犲潗鏍囩郴 / 鏁拌酱鏃惰繖閲屽氨婕忔敼浜嗭紝鐥囩姸鏄?宸ュ叿鍒囪繃鍘讳簡銆?            // 鐢诲嚭鏉ョ殑杩樻槸涓€鍧ㄨ嚜鐢辩瑪杩?鈥斺€擿--axistest` 绗竴娆¤窇灏辨妸瀹冩姄鍑烘潵浜嗐€?            // 鍚堟垚涓€浠戒箣鍚庯紝浠ュ悗鍐嶅姞鍥惧舰绉嶇被涓嶄細鍐嶆紡绗簩涓湴鏂广€?            default:
                if (IsShapeTool(tool))
                {
                    // **鐢诲畬鑷姩閫変腑閭ｄ釜妗?*锛堣 EndStroke锛夛細鎸変笅鍏堥棶"杩欎竴涓嬫槸涓嶆槸鍦ㄥ姩瀹?銆?                    //   路 鍦ㄥ畠韬笂锛堝渾閽?/ 鎿嶄綔鏉?/ 鎵嬫焺 / 鏃嬭浆鏌?/ **妗嗗唴浠绘剰涓€鐐?*锛?                    //     鈫?浜ょ粰閫夋嫨鎵嬪娍閭ｄ竴濂楋紙鎷栧姩銆佹媺鎵嬫焺銆佺偣鏉★紝鍜屾閫夊伐鍏蜂笅**瀹屽叏涓€鏍?*锛夛紱
                    //   路 鍦?*妗嗗** 鈫?鏀惰捣杩欎釜妗嗭紝杩欎竴绗旂収甯哥敾銆?                    // 鐢ㄦ埛 2026-09-22 瀹氱殑鍙ｅ緞锛?鐐瑰嚮浜嗗叾浠栧湴鏂癸紝杩欎釜閫変腑妗嗗氨鍙栨秷"锛?                    // 鑰?鍙鏀剁缉锛屽叾浠栫殑閮戒笉鍙?鈥斺€旀墍浠ユ閲岀収鏍疯兘鎷栵紙绗竴鐗堟妸妗嗛噷鍒ゆ垚
                    // "鎺ョ潃鐢讳竴绗?锛岀敤鎴蜂笂鎵嬪氨鏄?涓嶈兘鎷栧姩浣嶇疆锛屽彧鑳芥媺浼哥缉鏀?锛夈€?                    // 鍒ゆ嵁鍙湁 `AutoSelectionZoneAt` 杩欎竴澶勶紙鍏夋爣閭ｈ竟闂?`SelectionInteractiveAt`锛夈€?                    if (Doc.Selected.Count > 0)
                    {
                        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
                        bool alt = (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
                        if (AutoSelectionPress(x, y, shift, alt)) { _dirty = true; return; }
                        ClearSelectionForNewContext();
                    }

                    // **澶氱瑪鍥惧舰**锛堢洰鍓嶅彧鏈夊弻鏇茬嚎锛氫袱绗旓紝瑙佽〃 StepPlan锛夈€傝鍒欑収 InkClass锛?                    // **涓€绗?= 鎸変笅-鎷?鏉炬墜**锛屾澗鎵嬫帹杩涗笅涓€绗旓紙瑙?_stepIndex 閭ｆ娉ㄩ噴锛夈€?                    //   鈶?绗?1 绗旀寜涓?鈫?璧峰崐鎴愬搧锛坄_stepPlan` / `_stepIndex` 涔熷湪閭ｉ噷钀斤級锛?                    //   鈶?鍚庨潰鍑犵瑪鎸変笅 鈫?**浠€涔堥兘涓嶆柊寤?*锛屽彧鎺ョ潃鏀归偅涓崐鎴愬搧
                    //     锛堜腑蹇冩部鐢ㄧ 1 绗旈偅涓紝InkClass 涔熸槸杩欎箞骞茬殑锛夈€?                    if (_stepPlan != null && _stepIndex > 0)
                    {
                        ApplyStepGeometry(x, y);
                        break;
                    }
                    var plan = PlanOf(tool);
                    if (plan != null)
                    {
                        BeginStepShape(tool, plan, x, y);
                        ApplyStepGeometry(x, y);      // 鎸変笅涔熺畻涓€娆★紙瑙侀偅涓嚱鏁扮殑娉ㄩ噴锛?                        break;
                    }
                    BeginShapeAt(tool, x, y);
                    break;
                }

                // **绗斾笅闈㈤偅涓湅寰楄鐨勯€変腑妗?*锛堝仠椤挎垚鍨?/ 瑙︽懜闀挎寜閫夊嚭鏉ョ殑锛夛細杩欎竴涓嬪悓鏍疯
                // 鍏堥棶"鏄笉鏄湪鍔ㄥ畠"銆傜敤鎴?2026-09-23 瀹氱殑鍙ｅ緞鍜屽浘褰㈠伐鍏蜂笅**瀹屽叏涓€鏍?*锛?                // 鐐规閲?鈫?鎷栧姩 / 鎷夋墜鏌勶紱鐐规澶?鈫?鏀惰捣杩欎釜妗嗐€?*杩欎竴绗旂収甯哥敾**
                //锛堝彧鏄?鍙笉杩囩偣浜嗕竴涓?鐨勮瘽涓嶇暀澧級銆?                //
                // 2026-10-05 鐪熸満鍙嶉锛?闀挎寜閫変腑浠ュ悗涓嶈兘绉诲姩鎿嶄綔"鈥斺€斿垽鎹粠鍙 `_dwellSelected`
                // 鎵╁埌**涔熻 `_touchSelected`**锛氳Е鎽搁暱鎸夐€夊嚭鏉ョ殑妗嗭紝鍦ㄧ瑪涓嬮潰鍚屾牱鏄?                // "鎸変綇妗嗛噷鎷?= 绉诲姩銆佹寜妗嗗 = 鏀惰捣骞剁収甯稿啓"锛堝拰妗嗛€?/ 鍥惧舰宸ュ叿涓嬪悓涓€濂楄涓猴級銆?                if (TryDwellSelectionPress(x, y)) { _dirty = true; return; }

                BeginFreehandStrokeAt(id, ptype, x, y, screenX, screenY, pressure);
                break;
        }
        _dirty = true;
    }

    /// <summary>
    /// 绗斾笅闈㈤偅涓€涓嬶細**濡傛灉閭ｄ釜"鐪嬪緱瑙佺殑閫変腑妗?杩樺湪锛屽厛闂畠鏄笉鏄湪鍔ㄥ畠**
    ///锛堣繑鍥?true = 杩欎竴涓嬭閫夋嫨鎵嬪娍鍚冩帀浜嗭紝璋冪敤鏂圭洿鎺ユ敹宸ワ級銆?    ///
    /// 鍒ゆ嵁璁や袱绉嶆锛歚_dwellSelected`锛堝仠椤挎垚鍨嬶級鍜?`_touchSelected`锛堣Е鎽搁暱鎸夐€変腑锛夈€?    /// 2026-10-05 鐪熸満鍙嶉锛?闀挎寜閫変腑浠ュ悗涓嶈兘绉诲姩鎿嶄綔"鈥斺€旇Е鎽搁€夊嚭鏉ョ殑妗嗕互鍓嶅湪绗斾笅闈?    /// 涓嶆嫤锛屾寜涓婂幓灏卞彉鎴愭帴鐫€鍐欎竴绗旓紱鐜板湪鍜屾閫?/ 鍥惧舰宸ュ叿涓嬪悓涓€鏉¤涓恒€?    ///
    /// 鈿?**鍙湁杩欎竴澶勫疄鐜?*锛歚OnPointerDown` 鍜?`--dwelltest` 閮介棶瀹冦€?    /// 鍒ゆ嵁鍐欎袱浠界殑璇濓紝鑷楠岀殑鍏跺疄鏄?鑷鑷繁閭ｄ竴浠?锛岀瓑浜庢病楠?    ///锛?鍚屼竴涓悕鍗曞啓鍦ㄥ澶勫繀婕忎竴澶?锛岃 鏋舵瀯-鍒嗗眰涓庤鍒?md 浜?7锛夈€?    /// </summary>
    private bool TryDwellSelectionPress(float x, float y)
    {
        if ((!_dwellSelected && !_touchSelected) || Doc.Selected.Count == 0) return false;
        bool shiftKey = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
        bool altKey = (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
        if (AutoSelectionPress(x, y, shiftKey, altKey)) return true;
        // 妗嗗锛氭敹璧疯繖涓锛岃繖涓€绗旂収甯哥敾鈥斺€斿彧鏄?濡傛灉杩欎竴涓嬪彧鏄釜鐐?锛屽ⅷ涓嶇暀
        //锛堣 EndStroke 閲岄偅鏉?`_dismissTapArmed`锛夈€?        ClearSelectionForNewContext();
        _dismissTapArmed = true;
        return false;
    }

    /// <summary>
    /// 璧蜂竴绗旇嚜鐢辩瑪杩癸紙绗?/ 鑽у厜绗?/ 婵€鍏夌瑪閮借蛋杩欓噷锛夈€?    ///
    /// 浠?`OnPointerDown` 閲?*鎶藉嚭鏉?*鐨勭悊鐢辨湁涓ゆ潯锛?    ///   路 鍋滈】鎴愬瀷鐨勮嚜妫€瑕佽兘浠?鎸変笅"杩欎竴姝?*鐪熻窇涓€閬?*锛坄--dwelltest`锛夛紝
    ///     鑰屼笉鏄湪娴嬭瘯閲屽彟閫犱竴鏉＄瑪杩瑰璞♀€斺€旈偅鏍锋祴鐨勫氨涓嶆槸杩欐潯璺簡
    ///     锛?鎴戦€犱簡涓璞°€佸瓧娈垫槸鎴戣嚜宸卞～鐨?涓嶇畻娴嬶紝瑙?project_memory 鐨勬暀璁級锛?    ///   路 璧风瑪鐨勫嚑浠朵簨锛堢嚎鍨?/ 鍚堟垚鍣ㄨ建杩?/ 棰勬祴鍣?/ 鍋滈】璺熻釜锛夋湰鏉ュ氨璇ュ湪涓€澶勫啓瀹屻€?    /// </summary>
    private void BeginFreehandStrokeAt(uint id, uint ptype, float x, float y,
                                       float screenX, float screenY, float pressure)
    {
        var tool = Tool;
        float trailW = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                 : tool == Tool.Laser ? LaserWidthLogical
                                                 : PenWidthLogical) * DpiScale;
        // 杩欎竴绗旂殑绾垮瀷锛?*鍙湁绗斿悆閭ｄ釜寮€鍏?*锛堣 PenDash锛夛紝鍒殑宸ュ叿涓€寰嬪疄绾裤€?        var dash = tool == Tool.Pen ? PenDash : StrokeDash.Solid;
        // **铏氱嚎绗旇抗涓嶈捣濮旀墭澧ㄨ抗**锛氶偅鏉¤建杩圭敱绯荤粺鍚堟垚鍣ㄧ敾锛岀敾涓嶅嚭鎴戜滑鐨勭嚎鍨?        // 锛堝畠鍙細鐢讳竴鏉″疄绾匡級锛屼竴绗斿啓瀹屽氨浼?瀹炵嚎绐佺劧鍙樿櫄绾?闂竴涓嬨€?        // 閫€鍥炶惤鑷繁鐢诲弽鑰屾槸瀵圭殑鈥斺€旇嚜宸辩敾鐨勬箍澧ㄦ湰鏉ュ氨鏄櫄绾匡紝鍓嶅悗涓€鑷淬€?        //
        // 2026-10-11锛?*涓嶅啀鍙鐪熺瑪寮€**鈥斺€旂瑪/榧犳爣/瑙︽懜閮藉杺銆傛潵鐢憋細鐪熺瑪閭ｆ潯閫氶亾璋冮€氬悗
        // 锛堟寕瑙嗚鏍?+ 缁撹处锛夛紝鐢ㄦ埛瀹炴祴"榧犳爣妯″紡锛堟墜鍐欐澘鍏?ink锛変笉璺熸墜"鈥斺€斿洜涓洪紶鏍囪矾寰?        // 涓€涓偣閮芥病鍠傝繃銆傞€愬抚閲囨牱**瀹炴祴**锛氶紶鏍?*涔熺敾**锛堟嫋鍔ㄤ腑閲忓埌杩?163 鍍忕礌鐨勫熬宸达級銆?        // 鈿?杩欐潯閫氶亾鐢荤殑鏄?杩樻病涓婂睆鐨勯偅涓€灏忔"鈥斺€旀垜浠嚜宸辩殑甯т竴鎻愪氦灏辨妸瀹冩秷鍖栨帀锛?        //   鎵€浠ヨ倝鐪肩湅甯告槸"闂竴涓?锛坄--trailonly` 涓嬪挨鍏舵槑鏄撅級锛岄偅鏄粨鏋勬€ц〃鐜帮紝涓嶆槸 bug銆?        var wetWin = WindowAt(screenX, screenY);
        bool trailStarted = false;
        if (dash == StrokeDash.Solid)
            trailStarted = wetWin?.BeginInkTrail(
                tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor, trailW * 0.5f) ?? false;
        _pen.BeginStroke();   // 缂哄帇鍥炲～鐨勫熀鍑嗗彧娲诲湪"涓€绗?涔嬪唴锛堣 PenSampleBuffer.BeginStroke锛?        ResetPredictTail();   // B4锛氭柊鐨勪竴绗旓紝棰勬祴鍣ㄤ粠闆跺紑濮?        _trailActiveThisStroke = trailStarted;   // 濮旀墭澧ㄨ抗锛氭湰绗旀湁绯荤粺婀垮ⅷ 鈫?棰勬祴閫€鍦好梥cale
        InputBlackBox.Begin(NowMs, x, y, pressure, ptype);   // 榛戝專瀛愶細璧风瑪锛堝洖鏀剧敤锛?        ActiveStroke = new Stroke
        {
            Tool = tool,
            Color = tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
            Width = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                              : tool == Tool.Laser ? LaserWidthLogical
                                              : PenWidthLogical) * DpiScale,
            Dash = dash,
            // **姝ｅ湪鍐欑殑杩欎竴绗旂敾鎶樼嚎**锛堟洸绾垮彧鐢ㄥ湪钀界瑪涔嬪悗锛夛細鏇茬嚎鐨勬渶鍚庝竴娈垫瘡鏉ヤ竴涓?            // 鏂扮偣灏辫鍥炲ご閲嶇畻锛岀瑪灏栧悗闈㈤偅鍑犲崄鍍忕礌浼氫竴鐩村井寰姩 鈥斺€?鐢ㄦ埛 2026-09-28
            // 瀹炴祴鐨勫師璇濇槸"涓婇潰浼氬嚭娈嬪奖涓€鐩村湪閭ｉ棯"銆傝 Stroke.RawWhileLive銆?            RawWhileLive = true,
        };
        // Win11 鐨勭郴缁熻建杩瑰彲鐢ㄦ椂淇濇寔鍘熷崗璁紱绯荤粺杞ㄨ抗涓嶅彲鐢ㄦ椂鍚敤鍚屼竴绐楀彛鍐呯殑
        // 鐙珛 DComp 婀垮ⅷ灞傘€傚畠鍙帴绠℃樉绀猴紝涓嶆帴绠¤緭鍏ワ紝涔熶笉鏀瑰彉鏂囨。鎻愪氦銆?        bool fastWetStarted = false;
        // 鍙粰鐪熸鐨勭瑪鍚敤鏇胯韩锛涙縺鍏夌瑪鏈夎嚜宸辩殑 LaserTrail锛屼笉鑳芥贩杩?FastWet銆?        if (!trailStarted && tool == Tool.Pen && dash == StrokeDash.Solid)
        {
            fastWetStarted = wetWin?.BeginFastWet(
                tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
                trailW * 0.5f) ?? false;
        }
        // `--syswet`锛氱湡绗?+ 瀹炵嚎绗旓紝涓旇建杩圭湡鐨勮捣鏉ヤ簡 鈫?婀垮ⅷ浜ょ粰绯荤粺锛堟垜浠笉鍐嶈嚜缁橈級銆?        // 鑽у厜绗?婵€鍏夌瑪涓嶅湪姝ゅ垪鈥斺€旂郴缁熻建杩圭敾涓嶅嚭瀹冧滑鐨勮鎰燂紱`--ownwet` 鍙己鍒跺洖鑰佽矾銆?        // 鈿?浠嶇劧**鍙粰鐪熺瑪**锛氶紶鏍?瑙︽懜鐨勮建杩圭郴缁熻涓嶈杩樻病缁撹锛屼笉鑳借"鎴戜滑涓嶈嚜缁?鍘昏祵銆?        _systemWetStroke = (trailStarted && OverlayWindow.SystemWetPreferred && tool == Tool.Pen
                            && ptype == Native.PT_PEN)
            ? ActiveStroke : null;
        _fastWetStroke = fastWetStarted ? ActiveStroke : null;
        // 璧风瑪锛氳惤绗旇繖鏉℃秷鎭噷鍙兘宸茬粡鍚堝苟浜嗗嚑涓噰鏍风偣锛屼竴璧锋敹杩涙潵锛堜互鍓嶅彧鍙栨渶鏂伴偅涓€涓級銆?        ActiveStrokeHasPressure = false;
        _wtStrokePressure = -1f;     // Wintab 鐨勭己鍘嬪洖濉熀鍑?*姣忕瑪褰掗浂**锛堟柊涓€绗斾笉缁ф壙涓婁竴绗旂殑鍔涘害锛?        WtSeenPressure = false;
        // 鈿?涓変釜璁℃暟**蹇呴』姣忕瑪褰掗浂**锛?026-10-07 鐪熸満鎶撳埌鐨勭浜屼釜 bug锛夛細
        // 鍘熸潵瀹冧滑涓€鐩寸疮鍔狅紝浜庢槸鏃ュ織閲屼細鍑虹幇"鍘嬫劅=鏃?浣?琛ュ帇89鐐?杩欑**鑷浉鐭涚浘**鐨勮鈥斺€?        // 閭ｄ釜 89 鏄墠鍑犵瑪鏀掍笅鏉ョ殑锛屽鎴戜竴寮€濮嬩互涓哄帇鍔涜ˉ涓婁簡銆佸叾瀹炴槸鍚庨潰姣忎竴绗旈兘娌″紑涓婁笅鏂囥€?        // **鏃ュ織鏁板瓧蹇呴』鏄?杩欎竴绗?鐨?*锛屼笉鐒跺畠灏变笉鏄瘉鎹€傚樊鍊煎熀绾夸篃涓€璧峰綊闆躲€?        WtPolledPackets = WtPressurePoints = WtBadPackets = 0;
        WtRawPMin = int.MaxValue; WtRawPMax = int.MinValue;
        _wtPendingPoints = 0;
        _cleanBuf.Clear();
        _wintabPacketsSeen = _wintab.PacketsRead;
        _wintabBadSeen = _wintab.BadPackets;
        // 濮旀墭杞ㄨ抗锛氳繖涓€绗旂殑鍠傜偣/缁撹处宸€煎熀绾匡紙`[绗旂敾]` 閲屾姤"杩欎竴绗?鐨勬暟锛夈€?        _trailFedSeen = OverlayWindow.InkTrailPointsFed;
        _trailRemovedSeen = OverlayWindow.InkTrailPointsRemoved;
        LastCoalescedSamples = LastCoalescedMessages = 0;
        _rawReportsThisStroke = 0;      // `--rawprobe`锛氬師濮嬭緭鍏ヨ鏁版瘡绗斿綊闆?        _rawPointsAdded = 0;            // `--rawinput`锛氳ˉ杩涙潵鐨勭偣鏁版瘡绗斿綊闆?        _rawSkipNoStroke = _rawSkipApi = _rawSkipType = _rawSkipAbs = 0;
        _rawSkipZero = _rawSkipKind = _rawSkipGate = _rawSkipAnchor = 0;
        _rawSkipCap = _rawSkipNear = _rawSkipNoInter = 0;
        _rawAbsValid = false;           // 缁濆鎶ョ殑"涓婁竴鏉?姣忕瑪浣滃簾锛堝樊鍊间笉鑳借法绗旂疮鍔狅級
        _rawSinceLastPtr = _rawTotalSincePtr = _ptrMsgsThisStroke = 0;
        _rawHasIntermediates = false;   // 鍒ゆ嵁瑕佹敀鍑犳潯鎵嶆湁缁撹
        _rawAnchorValid = false;        // 閿氱偣鐢辫繖涓€绗旂殑绗竴鏉℃寚閽堟秷鎭缓绔?        SetRawCapture(true);            // 鍘熷杈撳叆锛?*鍙湪杩欎竴绗旀湡闂存敹**锛堢┖闂插繀椤诲叧锛?        // 鈿?Wintab **涓嶅湪杩欓噷寮€**锛?026-10-07 鐢ㄦ埛鎻愰棶鍚庢敼鐨勶級锛?        //
        // 鍘熸潵鍦ㄨ捣绗旀椂鏃犳潯浠?`EnsureWintab()`锛岃櫧鐒惰濂戒簡"鍙湪闈炵瑪璺緞涓婄敤"锛?        // 浣?*涓婁笅鏂囧凡缁忓紑鐫€** 鈥斺€?涓囦竴椹卞姩鍥犱负"鏈?Wintab 瀹㈡埛绔?鑰屾敼鍙樼瑪鐨勪笂鎶ユ柟寮?        // 锛圵intab 鏄崟涓€鎻愪緵鑰咃紝鍘嗗彶涓婂氨鏈?鍝釜 API 鎷垮埌鏁版嵁"鐨勫彇鑸嶏級锛?        // 閭ｅ氨杩炵疮浜?*寮€ ink** 閭ｆ潯璺?鈥斺€?鑰岄偅鏉¤矾鏈璺?Wintab 涓€鐐瑰叧绯婚兘娌℃湁銆?        //
        // 鐜板湪鏀规垚**鐪熸闇€瑕佹椂鎵嶅紑**锛堣 AppendStrokeSamples 鐨勯潪绗斿垎鏀級锛?        // 寮€ ink 鐨勭瑪锛圥T_PEN锛?*涓€杈堝瓙閮戒笉浼氭墦寮€ Wintab 涓婁笅鏂?*锛?        // 杩欐潯闅旂灏辨垚浜嗙粨鏋勪笂鐨勪簨瀹烇紝鑰屼笉鏄?鎴戝杩囦竴閬?銆?        AppendStrokeSamples(id, ptype, x, y, screenX, screenY, pressure);
        // 鍗婂緞**閫愮偣绠?*锛堣 TrailRadius锛夛細鏈夊帇鎰熺殑绗旓紝婀垮ⅷ鐨勭矖缁嗗繀椤诲拰骞插ⅷ涓€鑷淬€?        FeedInkTrail(ptype, TrailRadius(), screenX, screenY);
        // 濮旀墭杞ㄨ抗鍙兘鍦ㄥ垱寤烘垚鍔熷悗銆佺涓€娆＄湡姝ｅ杺鐐规椂鎵嶅け璐ャ€備笉鑳借杩欎竴绗斾粠姝?        // 娌℃湁婀垮ⅷ锛氬悓涓€绗旂珛鍗冲垏鍒板簲鐢ㄤ晶 DComp 婀垮ⅷ灞傦紝骞舵妸宸茬粡鏀跺埌鐨勭偣涓€娆¤ˉ鐢汇€?        if (!fastWetStarted && trailStarted && wetWin != null && wetWin.InkTrailRuntimeFailed)
        {
            _systemWetStroke = null;
            fastWetStarted = wetWin.BeginFastWet(
                tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
                trailW * 0.5f);
            _fastWetStroke = fastWetStarted ? ActiveStroke : null;
        }
        if (fastWetStarted)
        {
            // 璧风瑪杩欎竴涓?*绔嬪埢**鐢讳竴娆★紙璁╃涓€鐐瑰氨鍙锛涙瘡绗斿彧鏈夎繖涓€娆″悓姝ユ彁浜わ級锛?            // 涔嬪悗閮借蛋"姣忓抚鏈€澶氫竴娆?鐨勬寕璧疯矾绾匡紙瑙?_fastWetPending 鐨勬敞閲婏級銆?            wetWin?.UpdateFastWet(ActiveStroke);
            _fastWetPending = false;
        }

        // **鍋滈】鎴愬瀷璺熺潃杩欎竴绗斿紑濮嬭鏃?*锛堣 璁″垝-鍥惧舰宸ュ叿.md 搂鍥涘崄浜岋級銆?        // 婵€鍏夌瑪涓嶅弬涓庯細瀹冨彧鏄?鎸囦竴涓?锛屾湰鏉ュ氨涓嶇暀澧紝鎶婂畠鍙樺嚭涓€涓浘褰㈡潵娌℃湁鎰忎箟銆?        _dwellInk = null;
        _dwellCommitted = false;
        // 褰曞ⅷ杩癸紙`--recink`锛夛細杩欎竴绗斾粠闆跺紑濮嬫敀锛堣 `FlushInkRecord`锛夈€?        _recordPts = null;
        _recordGuess = "";
        // 涓婁竴绗旇鏄繕鎸傜潃锛?*閰嶅娌¤蛋鍒版彁浜?*灏卞浣嶄簡锛屾瘮濡備腑閫旀崲浜嗗伐鍏凤級锛?        // **蹇呴』鎶婂畠鏀惧洖鍙** 鈥斺€?鍚﹀垯閭ｄ竴绗斾細**姘歌繙闅愯韩** 鉁楋紙瑙?`Stroke.HiddenForPairing`锛夈€?        // 鈿?璧?`SetPairHidden`锛氬畠鍚屾椂鎶婇偅涓€绗斿崰鐨勫潡鏍囪剰锛屽惁鍒?鏀惧洖鏉ヤ簡銆佸睆骞曚笂杩樻槸涓嶆樉绀?
        //   锛堥厤瀵规湡閭ｅ嚑甯у凡缁忔妸鍧楅噸鐢绘垚"娌℃湁瀹?鐨勭増鏈簡锛夈€?        SetPairHidden(_twoBranchPrev, false);
        _twoBranchPrev = null;
        _twoBranchIndex = -1;

        if (DwellShapeEnabled && tool != Tool.Laser)
        {
            _dwell.Begin(NowMs, new Vector2(x, y), DwellAssist.DeadZoneLogical * DpiScale);
            StartDwellTimer();
        }
        else _dwell.Reset();
    }

    /// <summary>
    /// 杩欎竴甯х殑鎸囬拡绉诲姩锛堣嚜鐢辩瑪杩归偅涓€鏀級銆備粠 `OnPointerMove` 鎶藉嚭鏉ワ紝
    /// 鐞嗙敱鍜?<see cref="BeginFreehandStrokeAt"/> 鍚屼竴涓細鍋滈】鎴愬瀷鐨勮嚜妫€瑕佽兘**鐪熻窇**杩欎竴鏉°€?    /// </summary>
    private void ExtendFreehandStroke(uint id, uint ptype, float x, float y,
                                      float screenX, float screenY, float pressure)
    {
        // **鍋滈】鎴愬瀷**锛歛rmed 涔嬪悗杩欎竴绗斾笉鍐嶅爢閲囨牱鐐癸紙瀹冨凡缁忓彉鎴愬菇鐏靛浘褰簡锛夛紝
        // 鎸囬拡绉诲姩涓€寰嬩氦缁?`ArmedStrokeMove`锛?*鍙湁鐩寸嚎**浼氬姩锛堟嫋鍙︿竴澶磋浆鍚?/ 浼哥缉锛夛紝
        // 鍏跺畠鍥惧舰**鍙瑙堛€佷粈涔堥兘涓嶆敼**锛堟姮鎵嬫墠瀹氬瀷 + 鑷姩閫変腑锛岃閭ｄ釜鍑芥暟锛夈€?        // 鈿?杩欓噷**娌℃湁"鍙栨秷"**锛氭棭鍏堥偅鏉?闈炵洿绾挎嫋璧板氨鍙栨秷銆佽繖涓€甯х殑鐐圭収甯歌繘绗旇抗"鏄?        //    鎴戜滑鑷繁瀹氱殑锛屾棭鍘绘帀浜嗐€?        bool dwellConsumed = _dwell.State == DwellState.Armed
                          && ArmedStrokeMove(x, y, screenX, screenY);
        if (dwellConsumed) return;

        // 鎸囬拡鎶ヤ粈涔堝潗鏍囧氨瀛樹粈涔堝潗鏍囷細涓嶅仛骞虫粦銆佷笉鍋氭娊绋€銆?        // 浣?*涓€鏉℃秷鎭噷鐨勭偣瑕佸叏閮ㄦ敹涓?*鈥斺€旂郴缁熶細鎶婃潵涓嶅強鎶曢€掔殑绉诲姩鍚堝苟
        // 锛堣嚜宸卞疄娴嬶細娉ㄥ叆 140 Hz锛屽簲鐢ㄥ彧鏀跺埌绾?60 鏉℃秷鎭紝鍏朵綑鍦?history 閲岋級锛?        // 鍙彇鏈€鏂伴偅涓€涓瓑浜庢妸绗旂殑閲囨牱鐜囩爫鍗婏紙瑙?Input/PenInput.cs锛夈€?        AppendStrokeSamples(id, ptype, x, y, screenX, screenY, pressure);
        FeedInkTrail(ptype, TrailRadius(), screenX, screenY);
        var wetWin = WindowAt(screenX, screenY);
        if (wetWin != null && wetWin.InkTrailRuntimeFailed && !IsFastWetStroke(ActiveStroke))
        {
            // Native delegated trail can fail after a successful start. Switch the
            // same active stroke to the application surface without changing input
            // ownership or document semantics.
            _systemWetStroke = null;
            if (wetWin.BeginFastWet(ActiveStroke.Color, TrailRadius()))
                _fastWetStroke = ActiveStroke;
        }
        if (IsFastWetStroke(ActiveStroke))
        {
            // 鈿?涓嶅湪杩欓噷鍋?GPU 宸ヤ綔锛堣 _fastWetPending 鐨勬敞閲婏級锛氬彧鎸傝捣锛?            // 鐪熸鎻愪氦鐢辫繖涓€甯х敾涓诲抚涔嬪墠閭ｄ竴娆″畬鎴愨€斺€旀瘡甯ф渶澶氫竴杞€?            _fastWetPending = true;
            _fastWetScreenX = screenX;
            _fastWetScreenY = screenY;
        }
        // 鍋滈】璺熻釜锛?*姝诲尯鍐呯殑鎶栧姩涓嶇畻"鍔ㄨ繃"**锛堣 DwellAssist.DeadZoneLogical锛夆€斺€?        // 灏戜簡瀹冿紝绗斿皷闈欐鏃堕偅鐐逛簹鍍忕礌鎶栧姩浼氭妸璁℃椂涓€鐩村埛鏂帮紝
        // "鍋?600ms"姘歌繙鏀掍笉婊★紝鏁存潯鍔熻兘鐪嬬潃鍍忔病鐢熸晥銆?        _dwell.Sample(NowMs, new Vector2(x, y));
    }

    // ---------------------------------------------------------------------
    //  鍋滈】鎴愬瀷锛氳鏃?/ 瑙﹀彂 / 鎸変綇璋冩暣 / 瀹氬瀷锛堣鏍艰 璁″垝-鍥惧舰宸ュ叿.md 搂鍥涘崄浜岋級
    // ---------------------------------------------------------------------

    /// <summary>
    /// 寮€閭ｉ 40ms 鐨勮疆璇㈠畾鏃跺櫒锛?*鍙湪绗旂敾杩涜涓紑**锛夈€?    ///
    /// 涓轰粈涔堥潪瑕佹湁瀹冿細**绗斾笉鍔ㄥ氨娌℃湁 `WM_POINTERUPDATE`**锛岃€屼富寰幆绌洪棽鏃堕樆濉炲湪
    /// `WaitMessage()`锛堣 `Loop`锛夆€斺€斾笉涓诲姩閱掕繃鏉ワ紝灏辨案杩滈棶涓嶅嚭"鍋滀簡澶氫箙"銆?    ///
    /// 涓轰粈涔堝崟鐙竴棰楄€屼笉鏄惌鐜版垚閭ｉ 250ms 鐨勶細250ms 鐨勭矑搴︿笅涓€绗?600ms 鐨勫仠椤?
    /// 浼氬湪 500~750ms 涔嬮棿闅忔満瑙﹀彂锛屾墜鎰熸槸椋樼殑锛?0ms 鍙閱?25 娆?绉掞紝鑰屼笖**鍙湪鍐欏瓧鏈熼棿**銆?    /// </summary>
    private void StartDwellTimer()
    {
        if (_dwellTimerOn || _windows.Count == 0) return;
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)DwellTimerId, DwellAssist.TickMs, IntPtr.Zero);
        _dwellTimerOn = true;
    }

    private void StopDwellTimer()
    {
        if (!_dwellTimerOn || _windows.Count == 0) return;
        Native.KillTimer(_windows[0].Hwnd, (IntPtr)DwellTimerId);
        _dwellTimerOn = false;
    }

    /// <summary>
    /// **杞閭ｄ竴鍒?*锛氱瑪鍋滃浜嗗氨璇嗗埆涓€娆°€傝繖鏄暣鏉¤矾涓婂敮涓€鐨?鏃堕棿鍒ゆ嵁"鍏ュ彛
    ///锛坄Engine.Loop` 閲?`NowMs` 宸茬粡鍒锋柊杩囦簡锛岃閭ｉ噷鐨勯『搴忥級銆?    /// </summary>
    private void TickDwellShape()
    {
        // 瑙︽懜鎵嬪娍鐨勯暱鎸夛紙8.4.0锛夛細**鍜屽仠椤挎垚鍨嬪叡鐢ㄨ繖涓€娆″績璺?*銆?        // 涓よ€呴潬鍒ゆ嵁鍒嗗紑锛氬仠椤挎垚鍨嬭"宸茬粡鐢诲嚭涓滆タ"锛堢偣鏁?鈮? + 璇嗗埆鍣ㄧ殑鏈€鐭暱搴︼級锛?        // 闀挎寜瑕?浠庡ご鍒板熬娌＄敾鍑哄幓"锛堣矾寰?鈮? 閫昏緫鍍忕礌锛屽拰"鐐逛竴涓嬩笉鐣欏ⅷ"鍚屼竴涓暟锛夈€?        TouchTick();

        if (_dwell.State != DwellState.Tracking) return;
        if (!_dwell.StillEnough(NowMs)) return;

        var ink = ActiveStroke;
        if (ink == null || ink.Points.Count < 3) return;

        var pts = new Vector2[ink.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(ink.Points[i].X, ink.Points[i].Y);
        // **褰曞ⅷ杩?*锛坄--recink`锛夛細璇嗗埆鍣ㄧ湅鍒扮殑灏辨槸杩欎竴涓茬偣锛岀洿鎺ョ暀涓紩鐢ㄧ粰瀹冨瓨 鉁?        if (InkRecordPath != null) _recordPts = pts;
        // 鏈€鐭暱搴﹂偅鏉￠棬妲?*鍦ㄨ瘑鍒櫒閲?*锛堣 ShapeRecognize.Recognize 鐨?scale 鍙傛暟锛夆€斺€?        // 杩欓噷涓嶅啀鑷繁涔樹竴閬?DpiScale锛氬悓涓€涓暟鍐欏湪涓ゅ锛岃繜鏃╀細婕傦紙鐢ㄦ埛 2026-09-23 鐨勬暀璁竻鍗曢噷
        // 绗竴鏉″氨鏄繖涓級銆?        // 鈽?**涓ょ瑪 鈫?鍙屾洸绾?*锛堢敤鎴?2026-09-25 瀹氱殑绾﹀畾锛?**鐢讳袱鏀氨鏄弻鏇茬嚎锛岀敾涓€鏀氨鏄姏鐗╃嚎**"锛夈€?        //
        // 鈿犫殸 **蹇呴』鎺掑湪"璁や笉鍑哄浘褰㈠氨杩斿洖"閭ｉ亾闂搞€愪箣鍓嶃€?* 鈥斺€?杩欐槸鑷鎶撳嚭鏉ョ殑鑷村懡閿欎綅锛?        //   鍗曠嫭涓€鏀弻鏇茬嚎**鏈潵灏变笉璇ヨ璁ゆ垚鍏鍥惧舰閲岀殑浠讳綍涓€绉?*锛堟柊绾﹀畾锛氬崟绗斾笉鍑哄弻鏇茬嚎锛?        //   鑰屽畠涔熶笉鏄姏鐗╃嚎锛夆啋 `Recognize` 杩斿洖 `IsNothing`锛涜鏄妸閰嶅鎸″湪瀹冨悗闈紝
        //   **杩欐潯璺案杩滆蛋涓嶅埌**锛堝疄娴嬶細璇枡瀹屽叏瀵圭О銆佷竴涓兘涓嶅嚭锛屾棩蹇楅噷杩炰竴琛岄厤瀵硅褰曢兘娌℃湁锛夈€?        var pair = TryTwoBranchPair(ink, pts);
        if (!pair.IsNothing)
        {
            if (InkLogEnabled) Console.WriteLine($"[鎴愬瀷] 涓ょ瑪 鈫?鍙屾洸绾匡細{pair.Rule}");
            if (InkRecordPath != null) _recordGuess = "涓ょ瑪 鈫?鍙屾洸绾匡細" + pair.Rule;
            ArmDwellShape(ink, pair);
            return;
        }

        var guess = ShapeRecognize.Recognize(pts, DpiScale);
        if (guess.IsNothing)
        {
            // **`--reclog` 璇婃柇**锛堢敤鎴?2026-09-26 鎶?妞渾鍜岀煩褰袱涓兘甯稿父浠€涔堥兘涓嶈"閭ｄ竴鏉★級锛?            // 鐪熸満涓?娌¤鍑烘潵"鍘熸潵**涓€鐐圭棔杩归兘娌℃湁**锛屽彧鑳介潬鍚堟垚璇枡鍘荤寽銆?            // 杩欎竴琛屾妸"涓轰粈涔堟病璁?鐩存帴鎵撳嚭鏉ワ紙`Rule` 閲屾瘡涓€閬撻椄閮藉甫鐫€鏁板瓧锛夈€?            if (InkLogEnabled)
                Console.WriteLine($"[鎴愬瀷] 娌¤鍑烘潵锛歿guess.Rule}锛坽pts.Length} 鐐癸級");
            if (InkRecordPath != null) _recordGuess = "娌¤鍑烘潵锛? + guess.Rule;
            // 璁や笉鍑哄浘褰?鈫?**浠€涔堥兘涓嶅仛锛屽ⅷ杩瑰師鏍风暀鐫€**锛堢敤鎴?2026-09-25 瀹氾級銆?            //
            // 杩欓噷鍘熷厛鍏滃簳"淇濆舰骞虫粦"锛堟妸杩欎竴绗旀崲鎴愯宸甫鍐呯殑鍏夋粦鏇茬嚎锛屄?3 绗竴姝ワ級锛?            // 2026-09-25 鐢ㄦ埛涓婃墜涔嬪悗**鍙栨秷**浜?鈥斺€?鍘熻瘽锛?鎶?淇濆舰骞虫粦'杩欎釜鍔熻兘鍙栨秷鎺夊惂锛?            // 鍥犱负瀹冨奖鍝嶆垜鐢绘姏鐗╃嚎"銆?            // 鍘熷洜寰堝疄鍦細骞虫粦**鍙湪"璁や笉鍑哄浘褰?鏃剁敓鏁?*锛岃€?*鐢绘姏鐗╃嚎锛堝挨鍏跺彧鐢诲崐鏀殑锛?            // 缁忓父姝ｅソ钀藉湪杩欎竴妗?* 鈥斺€?浜庢槸鑰佸笀鐢荤殑閭ｆ潯鏇茬嚎琚倓鎮勬崲鎴愬彟涓€鏉★紝
            // 鐪嬬潃鍍?瀹冭嚜宸卞彉浜嗗舰"銆傝€岃繖鎭版伆杩濆弽 搂42 閭ｆ潯鑰佽鐭┿€?            //
            // 鎹㈡垚"浠€涔堥兘涓嶅仛"涔嬪悗锛?*璁や笉鍑?= 鑹€уけ璐?*锛堝ⅷ杩樻槸浠栬嚜宸遍偅涓€绗旓級锛?            // 瑙勭煩鍥炲埌"**瀹佸彲涓嶅彉锛屼篃涓嶈兘鍙樺嚭涓€涓涓嶄笂鐨?*"銆?            return;
        }

        if (InkLogEnabled) Console.WriteLine($"[鎴愬瀷] 璁ゅ嚭浜?{guess.Kind}锛歿guess.Rule}");
        if (InkRecordPath != null) _recordGuess = $"璁ゅ嚭浜?{guess.Kind}锛? + guess.Rule;
        ArmDwellShape(ink, guess);
    }

    /// <summary>
    /// **鎶婅繖涓€绗斿啓杩涘綍鍒舵枃浠?*锛堣 `InkRecordPath`锛夆€斺€擿EndStroke` 涓€杩涙潵灏辫皟銆?    ///
    /// 鏍煎紡锛堝ソ璇汇€佷篃濂藉啓鑴氭湰瑙ｆ瀽锛夛細
    /// <code>
    /// --- stroke 3  t=812345  guess=娌¤鍑烘潵锛氫袱绗斾笉鍍忓悓涓€涓弻鏇茬嚎鐨勪袱鏀紙鈥︼級
    /// 301.25,400.50
    /// 305.10,402.75
    /// 鈥?    /// </code>
    /// 娈靛ご涓€琛岀粰鍑?绗嚑绗?/ 浠€涔堟椂鍊?/ 褰撴椂璁ゆ垚浜嗕粈涔堬紙鎴栦负浠€涔堟病璁わ級"锛?    /// 鍚庨潰涓€琛屼竴涓師濮嬮噰鏍风偣銆?*杩炵画涓ょ瑪灏辨槸涓€鏉″弻鏇茬嚎**锛堟垜鎸夎繖涓潵閰嶅缁熻锛夈€?    ///
    /// 鈿?鍐欎笉杩涘幓锛堣矾寰勪笉瀵?/ 鏂囦欢琚崰锛?*缁濅笉鑳藉奖鍝嶇敾鍥?* 鈥斺€?鏁存鍖呭湪 try 閲岋紝
    ///   澶辫触浜嗗彧寰€鎺у埗鍙拌涓€澹般€?    /// </summary>
    private void FlushInkRecord()
    {
        if (InkRecordPath == null) return;

        // `--recinkp`锛氶櫎浜嗗潗鏍囷紝**鍐嶅綍鍘嬪姏鍜?杩欎釜鐐规槸浠庡摢鏉¤矾鏉ョ殑"**銆?        //
        // 涓轰粈涔堥渶瑕侊紙2026-10-07锛夛細鐢ㄦ埛鎶?绗旇寰堣剰"锛岃€屾棩蹇楅噷鐨勬暟瀛楀叏閮藉寰椾笂 鈥斺€?        // 鍏夌湅鏁板瓧宸茬粡鏌ヤ笉涓嬪幓浜嗭紝蹇呴』鐪嬪埌**鐪熷疄鐨勭偣**锛?        // 鍒板簳鏄帇鍔涘啓閿欎簡銆佽繕鏄ˉ鐐规妸浣嶇疆琛ユ浜嗐€佽繕鏄埆鐨勩€?        // 鏉ユ簮閭ｄ竴鍒楁槸鍏抽敭锛歚1` = raw 琛ョ偣锛宍0` = 鎸囬拡娑堟伅锛屼竴鐪煎氨鑳藉垎杈ㄣ€?        if (InkRecordWithPressure && ActiveStroke != null && ActiveStroke.Points.Count >= 2)
        {
            try
            {
                using var wp = new StreamWriter(InkRecordPath, append: true);
                _recordSeq++;
                wp.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  hp={(ActiveStroke.HasPressure ? 1 : 0)}"
                             + $"  pts={ActiveStroke.Points.Count}");
                var rawSet = new System.Collections.Generic.HashSet<int>(_rawAddedIdx);
                for (int i = 0; i < ActiveStroke.Points.Count; i++)
                {
                    var q = ActiveStroke.Points[i];
                    // 鏈€鍚庝笁鍒楁槸**褰撴椂鐨?Wintab 璇绘暟**锛堝钩鏉垮潗鏍?+ 鍘嬪姏锛夈€?                    // 瀹冧滑涓嶅弬涓庣粯鍒讹紝鍙敤鏉ヤ簨鍚庣畻鍑?骞虫澘鍧愭爣 鈫?灞忓箷鍧愭爣"鐨勬槧灏?鈥斺€?                    // 瑕佸仛"鏁寸瑪閮借蛋 Wintab"灏卞繀椤绘湁瀹冿紝鑰屼笖**涓嶈兘鐚?*銆?                    wp.WriteLine($"{q.X:F2},{q.Y:F2},{q.P:F4},{q.T:F1},{(rawSet.Contains(i) ? 1 : 0)}"
                                 + $",{WtLastTabletX},{WtLastTabletY},{WtLastRawPressure}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[褰曞ⅷ杩筣 鍐欏け璐ワ細{ex.Message}"); }
            return;
        }

        // 浼樺厛鐢?璇嗗埆鍣ㄧ湅鍒扮殑閭ｄ覆鐐?锛涜繖涓€绗旀病鍋滈】锛堟病瑙﹀彂璇嗗埆锛夊氨鐢ㄦ敹绗旀椂鐨勬墜缁樼偣鍏滃簳銆?        // 鈿?鍏滃簳瑕?*鐐规暟澶?*鎵嶇畻锛氬仠椤挎垚鍨嬪悗 `ActiveStroke` 宸茬粡琚崲鎴愬浘褰㈠璞★紙鍙墿涓や笁涓?        //   瀹氫箟鐐癸級锛岄偅绉嶄笉鑳藉綋澧ㄨ抗褰曡繘鍘伙紙鍚﹀垯鎴戜細鎷夸竴鍫?涓ゆ牴鐐?褰撴牱鏈?鉁楋級銆?        var pts = _recordPts;
        if ((pts == null || pts.Length < 8) && ActiveStroke != null && ActiveStroke.Points.Count >= 8)
        {
            pts = new Vector2[ActiveStroke.Points.Count];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Vector2(ActiveStroke.Points[i].X, ActiveStroke.Points[i].Y);
        }
        try
        {
            using var w = new StreamWriter(InkRecordPath, append: true);
            _recordSeq++;
            if (pts == null || pts.Length < 8)
                w.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  "
                            + $"guess=锛堣繖涓€绗旀病褰曞埌鍘熷鐐癸細娌″仠椤裤€佹垨鐐规暟涓嶅锛?);
            else
            {
                w.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  "
                            + $"guess={(_recordGuess.Length == 0 ? "锛堟病鍒拌瘑鍒偅涓€姝ワ級" : _recordGuess)}");
                foreach (var p in pts) w.WriteLine($"{p.X:F2},{p.Y:F2}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[褰曞ⅷ杩筣 鍐欎笉杩涘幓锛坽InkRecordPath}锛夛細{ex.Message}");
        }
        _recordPts = null;
        _recordGuess = "";
    }

    /// <summary>
    /// **璁ゅ嚭鍥惧舰浜?*锛氭妸姝ｅ湪鍐欑殑杩欎竴绗旀崲鎴愬菇鐏甸瑙堬紙鐢ㄦ埛杩樻寜鐫€绗旓級銆?    ///
    /// 鍋氭硶灏辨槸**鎹㈡帀 `ActiveStroke` 鎸囧悜鐨勫璞?*锛堟崲鎴愪竴涓櫘閫氬浘褰㈠璞★級锛?    ///   路 娓叉煋涓嶇敤鍐欎竴琛屾柊浠ｇ爜鈥斺€?姝ｅ湪涔﹀啓鐨勯偅涓€绗?鏈潵灏辨槸姣忓抚閲嶇敾鐨勶紙瑙?`DrawStroke`锛夛紱
    ///   路 鎶墜鏃?`EndStroke` 鎻愪氦鐨勫氨鏄繖涓璞★紝**鍥惧舰浠庢潵娌¤繘杩囨枃妗?*锛?    ///     鎵€浠?涓€姝ユ挙閿€鍥炴墜缁?闈犵殑鏄?<see cref="DwellShapeAction"/> 鎶婂師杩瑰甫鍦ㄨ褰曢噷銆?    /// </summary>
    private void ArmDwellShape(Stroke ink, in ShapeGuess guess)
    {
        // B4.1锛?026-10-08 鐪熸満锛夛細**鎹㈢瑪蹇呴』澶嶄綅棰勬祴灏?*鈥斺€斾笅闈細鎶?ActiveStroke 浠?        // 鎵嬬粯绗旇抗鎹㈡垚鍥惧舰锛屼絾棰勬祴鍣ㄨ繕鐣欑潃鏃х瑪杩圭殑鏈偣/閫熷害锛涗笉澶嶄綅鐨勮瘽锛?        // 涓嬩竴甯?`UpdatePredictTail` 浼氭妸"棰勬祴鐐?鈭?鍥惧舰鏈偣"褰撳熬宸村亸绉伙紝浠庡浘褰㈡湯绔?        // 鐢诲嚭涓€鏉℃寚鍚戞棫鍘熺偣鐨勯暱灏撅紙鐢ㄦ埛鐪熸満锛?鎷栫鐐逛細椋樺嚭鍙﹀涓€鏉＄嚎娈碉紝鎱㈡參闀垮洖
        // 鍙樼洿绾挎椂鐨勫師濮嬩綅缃偣銆佹澗鎵嬫秷澶?锛夈€傚師鐞嗕笌娓呭崟瑙?璋冪爺-棰勬祴灏?鍘熺悊涓庢敞鎰忕偣.md 搂浜屻€?        ResetPredictTail();
        var shape = DwellAssist.BuildShapeStroke(guess, ink);

        // 鍋滄墜 = 杩欎竴绗斾笉鍐嶉暱浜嗭細鎶?姝ｅ湪鍐?鐨勪笁鏉￠€氶亾鍏ㄦ敹鎺夈€?        // 涓嶆敹鐨勮瘽灞忓箷涓婁細鍚屾椂鐣欑潃涓€鏉℃墜缁樼殑姝嚎鍜屼竴鏉¤鏁村浘褰紙InkClass 閭ｈ竟
        // 涓烘瑕佸垏 `EditingMode`锛岃繕鐣欎笅杩?涓ゆ潯绾?"棰勮娈嬬暀"涓€涓插潙锛涙垜浠繖杈?        // 閲囨牱鍜屼笂灞忛兘鍦ㄥ紩鎿庢墜閲岋紝鎵€浠ヨ鏀剁殑鍙湁杩欎笁澶勶級銆?        foreach (var w in _windows)
        {
            w.EndInkTrail();
            w.EndFastWet();
        }
        _systemWetStroke = null;
        _fastWetStroke = null;
        _fastWetPending = false;      // 杩欎竴绗斾笉鍐嶉暱锛氭寕璧风殑鏇存柊浣滃簾锛堣 _fastWetPending 娉ㄩ噴锛?
        var anchor = _dwell.Anchor;                       // 绗斿仠浣忕殑浣嶇疆
        double stillMs = _dwell.StillMs(NowMs);           // 澶嶄綅涔嬪墠鍏堥噺

        if (guess.Kind == StrokeKind.Line)
        {
            // 鈹€鈹€ **鐩寸嚎锛氬敮涓€鐨勪緥澶栵紝缁х画褰?骞界伒"鎸変綇**锛堢敤鎴?2026-09-24 瀹氾級鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
            // 鐩寸嚎鐨勫彟涓€澶存鏄敾瀹屾渶甯歌璋冪殑锛堣浆鎴愭按骞?/ 绔栫洿锛夛紝鎸変綇灏辫兘鎷栤€斺€旀瘮"鍏堟澗鎵嬨€?            // 鍐嶇偣瀹冦€佸啀鎷栨墜鏌?灏戜袱姝ャ€傛姮鎵嬫墠瀹氬瀷锛岃€屼笖**涓嶉€変腑**锛堥『鎵嬩竴鍒掞紝鎺ョ潃鍐欙級銆?            // 瑕佽涓や欢浜嬶細
            //   路 璧锋姝诲尯鍩哄噯锛氱瑪灏栬繕鍦ㄨ繖鍎块檮杩?鈫?绾夸竴涓儚绱犻兘涓嶆敼锛堣 DwellDragSlopLogical锛夛紱
            //   路 閽変綇鐨勯偅涓€澶达細**绂荤瑪灏栬繙鐨勯偅涓€澶?*銆備笉鑳芥嬁 `Points[0]`锛氳瘑鍒櫒缁欑殑绔偣鏄?            //     "鎷熷悎鏂瑰悜鐨勬姇褰辨瀬鍊?锛屽摢涓€澶磋惤鍦ㄩ偅閲?*涓嶅畾**锛岄拤閿欐椂鎶栦竴涓嬪氨鎶婄嚎鍘嬫垚闆堕暱搴?            //     锛堢敤鎴?2026-09-24 鎶ョ殑"绔栫嚎鍙樻垚寰堢煭鐨勫皬妯嚎"锛夈€?            _dwellInk = ink;
            ActiveStroke = shape;
            _dwellArmAnchor = anchor;
            _dwellGhostOrigin = null;   // 鐩寸嚎璧?鎷栧彟涓€澶存敼褰㈢姸"閭ｆ潯璺紝涓嶅悆骞界伒鏀瑰ぇ灏忕殑鍩哄噯
            _dwellGhostAnchorIdx = -1;
            _dwellGhostNearIdx = -1;
            if (guess.Def.Length >= 2)
                _dwellLinePin = Vector2.Distance(guess.Def[0], anchor) >= Vector2.Distance(guess.Def[1], anchor)
                              ? guess.Def[0] : guess.Def[1];
            // 璇绘暟鍦?*鎴愬瀷杩欎竴鍒诲氨浣?*锛堢敤鎴?2026-10-05 鎶ョ殑涓や釜 bug锛?            //   鈶?绗竴娆＄敤鏃惰兌鍥婇鍦ㄥ睆骞曞乏涓婅鈥斺€擿_shapeAnchor` 杩樻槸榛樿鐨?(0,0)锛?            //   鈶?涔嬪悗姣忔閮藉厛鍋滃湪涓婁竴鏉＄嚎涓娾€斺€旈偅鏄笂涓€鏉＄暀涓嬬殑閿氱偣锛岀瑪涓€鍔ㄦ墠璺冲洖鏉ャ€?            // 閿氱偣鎸傚湪**绂荤瑪灏栬繎鐨勯偅涓€澶?*锛堟帴涓嬫潵浼氳窡鐫€绗斿皷璧扮殑閭ｄ竴绔級锛?            // 鍜屾嫋鍔ㄤ腑鐨勭畻娉曞悓涓€鏉″垽鎹紙`_dwellLinePin` 鍙栫殑灏辨槸杩滅殑閭ｄ竴澶达級銆?            if (ActiveStroke.Points.Count >= 2)
            {
                var q0 = new Vector2(ActiveStroke.Points[0].X, ActiveStroke.Points[0].Y);
                var q1 = new Vector2(ActiveStroke.Points[^1].X, ActiveStroke.Points[^1].Y);
                _shapeInclination = SelectionHandles.InclinationDegrees(q0, q1);
                _shapeLength = Vector2.Distance(q0, q1);
                _shapeInclinationSnapped = false;
                _shapeAnchor = Vector2.Distance(q0, anchor) <= Vector2.Distance(q1, anchor) ? q0 : q1;
            }
            _dwell.Fire();
            _dirty = true;
            Console.WriteLine($"[鍋滈】鎴愬瀷] 鍋?{stillMs:F0}ms 鈫?{guess.Kind}锛坽guess.Rule}锛?
                              + "锛涙寜浣忚繕鑳芥嫋鍙︿竴澶磋浆鍚戯紝鏉炬墜瀹氬瀷锛?*涓嶉€変腑**锛屾帴鐫€鍐欙級");
            return;
        }

        // 鈹€鈹€ **鍏跺畠鍥惧舰锛氫篃鏄菇鐏?鈥斺€?鎸変綇鎷栧埌浣嶏紝鏉炬墜鎵嶅畾鍨?锛?閫変腑** 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // 鐢ㄦ埛 2026-09-25 璇曡繃 ClassIn 涔嬪悗鏀圭殑锛堜笂涓€鐗?2026-09-24 鏄?璇嗗埆鍒板氨瀹氬瀷骞堕€変腑"锛夛細
        // ClassIn 鐨勬墜鎰熸槸"鍥惧舰鍙樺嚭鏉ヤ互鍚庡厛鍒澗鎵嬶紝鐩存帴鎶婂畠鎷栧埌浣嶏紝鏉炬墜鎵嶉€変腑"鈥斺€?        // 姣?鏉炬墜 鈫?鍐嶇偣瀹?鈫?鍐嶆嫋"灏戜袱姝ワ紝鑰岀敾瀹屽氨鎯虫尓浣嶇疆杩欎欢浜嬪湪鏉夸功閲岄潪甯稿父瑙併€?        //
        // 鈿?鍜?2026-09-24 鐮嶆帀鐨勯偅濂楀菇鐏?*涓嶆槸涓€鍥炰簨**锛氱爫鎺夌殑鏄?鎸変綇鏀瑰ぇ灏?/ 杞"锛?        //   杩欓噷**鍙钩绉?*锛堣 ArmedStrokeMove锛夆€斺€斿畾涔夊厓绱犱箣闂寸殑鐩稿鍏崇郴涓€涓兘涓嶅姩锛?        //   鑰岄偅鎵?鍥惧舰娑堝け / 鍙樺舰"鐨勭梾鍥犲叏鍑哄湪鏀瑰嚑浣曚笂銆?        //   鏈哄埗涔熷畬鍏ㄥ鐢ㄧ洿绾块偅鏉★細鍥惧舰**浠庢潵娌¤繘杩囨枃妗?*锛屽畠灏辨槸 `ActiveStroke`锛?        //   鎶墜鏃?`EndStroke` 鎻愪氦锛坄_dwellInk != null` 閭ｆ潯璺級锛屽苟鍥犱负"涓嶆槸鐩寸嚎"
        //   璧?`committedShape` 鈫?鑷姩閫変腑銆?        _dwellInk = ink;
        ActiveStroke = shape;
        _dwellArmAnchor = anchor;
        var org = new Vector2[shape.Points.Count];
        for (int i = 0; i < org.Length; i++)
            org[i] = new Vector2(shape.Points[i].X, shape.Points[i].Y);
        _dwellGhostOrigin = org;

        // **閿氱偣 / 璺熺潃绗斿皷璧扮殑閭ｄ竴鐐?*锛?*鎴愬瀷鏃舵寫瀹氥€佹嫋鍔ㄥ叏绋嬩笉鍙?*銆?        //   路 閿氱偣 = 绂荤瑪灏?*鏈€杩?*鐨勯偅涓畾涔夊厓绱?鈥斺€?鍜岀洿绾跨殑 `_dwellLinePin` **鍚屼竴鏉″垽鎹?*
        //     锛堜汉鏀剁瑪灏卞仠鍦?缁堢偣"閭ｄ竴甯︼紝鎵€浠ヨ繙绔墠鏄?璧风偣"锛夛紱
        //   路 璺熺潃璧扮殑 = 绂荤瑪灏?*鏈€杩?*鐨勯偅涓€涓紙= 鐢ㄦ埛鎷栧嚭鏉ョ殑閭ｄ釜缁堢偣锛夈€?        // 鈿?涓や釜涓嬫爣蹇呴』鍦ㄨ繖閲屽畾姝伙細鎷栧姩涓啀绠楃殑璇濓紝鎸囬拡涓€绉诲姩瀹冧滑灏卞彲鑳戒簰鎹?        //   鈫?鍥惧舰浼?缈绘潵缈诲幓"锛堝悓涓€涓梾鏍硅 搂42.6.2 绗笁鏉★細鐩寸嚎褰撳垵鎷?`Points[0]` 褰撴敮鐐癸級銆?        //
        // 鈽?**鍙屾洸绾匡細閿氱偣閽夋"涓績"**锛堢敤鎴?2026-09-25 瀹氾級锛?        //     "瀹冨湪骞界伒妯″紡涓嬶紝搴旇鏄?*鎸夌収涓績涓洪敋鐐?*杩涜鏀惧ぇ缂╁皬銆?
        //   閫氱敤瑙勫垯锛堢绗斿皷鏈€杩滅殑閭ｄ竴涓級鍦ㄨ繖閲屼細鎸戦敊 鈥斺€?鍙屾洸绾挎湁**涓変釜**瀹氫箟鍏冪礌
        //   锛堜腑蹇?/ 娓愯繎绾胯鐐?/ 缁忚繃鐐癸級锛岀瑪灏栧仠鍦ㄦ敮绾夸笂鏃?绂荤瑪灏栨渶杩滅殑"寰堝彲鑳芥槸
        //   **娓愯繎绾胯鐐?*锛岄偅鏍风缉鏀惧氨缁曢偅涓鐐硅浆锛屽浘褰細鏁翠釜璺戝亸 鉁?        //   鎵€浠ヤ腑蹇冧竴寰嬩笉鍔紝**璺熺潃绗斿皷璧扮殑閭ｄ竴涓湪"鍏朵綑涓ょ偣"閲屾寫**锛堢绗斿皷鏈€杩戠殑锛夈€?        if (guess.Kind == StrokeKind.Hyperbola && org.Length >= 3)
        {
            _dwellGhostAnchorIdx = 0;                       // 涓績 = `Points[0]`锛屼竴寰嬩笉鍔?            _dwellGhostNearIdx = 1;
            float nD = float.MaxValue;
            for (int i = 1; i < org.Length; i++)
            {
                float dd = Vector2.Distance(org[i], anchor);
                if (dd < nD) { nD = dd; _dwellGhostNearIdx = i; }
            }
        }
        else
        {
            _dwellGhostAnchorIdx = 0;
            _dwellGhostNearIdx = 1;
            float farD = -1f, nearD = float.MaxValue;
            for (int i = 0; i < org.Length; i++)
            {
                float dd = Vector2.Distance(org[i], anchor);
                if (dd > farD) { farD = dd; _dwellGhostAnchorIdx = i; }
                if (dd < nearD) { nearD = dd; _dwellGhostNearIdx = i; }
            }
        }
        if (org.Length < 2 || _dwellGhostAnchorIdx == _dwellGhostNearIdx)
        {
            // 閫€鍖栵紙灏戜簬涓や釜鐐?/ 涓ょ偣閲嶅悎锛夆啋 涓嶅悆骞界伒鏀瑰ぇ灏忥紝鍙瑙堬紙鍜?2026-09-24 閭ｇ増涓€鏍凤級
            _dwellGhostAnchorIdx = -1;
            _dwellGhostNearIdx = -1;
        }

        _dwell.Fire();
        _dirty = true;
        Console.WriteLine($"[鍋滈】鎴愬瀷] 鍋?{stillMs:F0}ms 鈫?{guess.Kind}锛坽guess.Rule}锛?
                          + "锛涙寜浣忚兘鎷栧埌浣嶏紝鏉炬墜瀹氬瀷锛?*骞堕€変腑**锛汣trl+Z 鍙洖鎵嬬粯锛?);
    }

    /// <summary>
    /// armed 涔嬪悗鐨勬寚閽堢Щ鍔ㄣ€傝繑鍥?true = 杩欎竴涓嬭"骞界伒棰勮"鍚冩帀浜嗭紙涓嶅啀寰€绗旇抗閲屽爢鐐癸級銆?    ///
    /// **涓ょ鍥惧舰閮藉悆绉诲姩**锛堢敤鎴?2026-09-25 璇曡繃 ClassIn 涔嬪悗瀹氾級锛屽樊鍒彧鍦?鎷栫殑鏄粈涔?锛?    ///   路 **鐩寸嚎**锛?*绂荤瑪灏栬繙鐨勯偅涓€澶撮拤浣?*銆佹嫋鍑哄幓灏辨槸**杞悜 / 浼哥缉**锛堢収 ClassIn / InkClass 鐨?    ///     `LineAssistMove`锛夛紝鍚搁檮**鍜岀敾鐩寸嚎鍚屼竴濂?*锛堢壒娈婅杞惛闄?卤1掳銆丼hift 15掳 纭綉鏍笺€?    ///     Alt 鑷敱锛?026-10-05 缁熶竴鈥斺€斿師鏉ュ彧鍚?0/90銆佸宸?4掳锛夆€斺€?鐢诲潗鏍囪酱灏遍潬杩欎竴涓嬨€傜暀鐫€瀹冿紝鏄洜涓?    ///     鐩寸嚎鏄?椤烘墜涓€鍒?锛屽畠鐨勫彟涓€澶存鏄敾瀹屾渶甯歌璋冪殑锛堣浆鎴愭按骞?/ 绔栫洿锛夛紝
    ///     鑰屽湪閫変腑鎬侀噷璋冭澶氫袱姝ワ紙鍏堢偣瀹冦€佸啀鎷栨墜鏌勶級銆?    ///   路 **鍏跺畠鍥惧舰**锛?*鏀瑰ぇ灏?*锛堢敤鎴?2026-09-25 涓婃墜 ClassIn 涔嬪悗閫愭潯瀹氱殑锛夆€斺€?    ///     **閿氱偣涓嶅姩锛屾嫋鍑哄幓灏辨槸鏀瑰彟涓€涓瑙?/ 鍗婂緞**锛?    ///     鍦嗗舰"鍦嗗績涓嶅姩銆佹敼鍙樺渾鐨勫ぇ灏?锛涚煩褰?鏈変竴涓《鐐逛笉鍔紝鐩稿綋浜庢嫋鍙﹀涓€涓瑙掔嚎"锛?    ///     鑰屼笖"**鏃嬭浆鍔熻兘濂藉儚涓嶈兘杞簡锛屽簲璇ュ氨鏄嫋鍔ㄦ敼澶у皬**"锛堝菇鐏垫湡涓嶅仛鏃嬭浆锛夈€?    ///     閫氱敤瑙勫垯 = **绂荤瑪灏栨渶杩滅殑閭ｄ釜瀹氫箟鍏冪礌閽変綇銆佺绗斿皷鏈€杩戠殑閭ｄ釜璺熺瑪灏栬蛋**锛?    ///     鍏朵綑瀹氫箟鍏冪礌鎸?*閫愬垎閲忔瘮渚?*璺熺潃璧帮紙瑙佷笅闈㈠疄鐜帮級銆?    ///     鈿?鍜?2026-09-24 鐮嶆帀鐨勯偅濂?*涓嶆槸**鍚屼竴鎵逛唬鐮侊細閭ｄ笁鏉?鍥惧舰娑堝け / 鍙樺舰"鐨勭梾鍥?    ///     鏌ユ竻浜?*閮戒笉鏄菇鐏垫€佺殑閿?*锛堟槸 `EndStroke` 涓ら亾闂ㄦ閲忛敊浜嗗璞°€佷互鍙婄洿绾块敋鐐?    ///     鎷夸簡 `Points[0]` 鈥斺€?涓夋潯鐜板湪閮藉凡淇紝瑙?搂42.6.2锛夛紱鑰屽悓鑺傚疄娴嬬殑"鎵€鏈夊浘褰㈤兘鍦?    ///     璺熺潃鎵嬫姈鏀瑰ぇ灏?鍒欑敱**璧锋姝诲尯**鎸＄潃锛堜笅闈㈢涓€娈碉級銆?    ///     鎶墜 鈫?瀹氬瀷 + 鑷姩閫変腑锛堣 `EndStroke`锛夈€?    /// </summary>
    private bool ArmedStrokeMove(float x, float y, float screenX, float screenY)
    {
        var s = ActiveStroke;
        if (s == null) return false;

        var p = new Vector2(x, y);
        // **璧锋姝诲尯**锛堜袱鏉¤矾鍏辩敤锛夛細绗斿皷杩樺湪鎴愬瀷閭ｄ竴鍒荤殑浣嶇疆闄勮繎锛堝彧鏈夐┍鍔ㄤ笂鎶ョ殑浜氬儚绱犳姈鍔級
        // 鈫?涓€涓儚绱犻兘涓嶆敼銆傛病鏈夊畠锛?鎸変綇涓嶅姩"鍏跺疄涓€鐩村湪鏀癸細鐩寸嚎閭ｈ竟绗斿皷姝ｅソ鍋滃湪鏀偣
        // 閭ｄ竴澶存椂浼氭妸绾垮帇鎴愰浂闀垮害锛堢敤鎴?2026-09-24 鎶ョ殑"绔栫嚎鍙樻垚寰堢煭鐨勫皬妯嚎"锛?        // `--dwelltest` H6 瀹炴祴婕傜Щ 299.51锛夛紱鍥惧舰杩欒竟浼氱湅鍒板浘褰?鑷繁鎶栫潃鏀瑰ぇ灏?銆?        // 鈿?**杩欎竴鏉℃槸骞界伒鏀瑰ぇ灏忕殑鍓嶆彁**锛堜笉鏄彲閫変紭鍖栵級锛毬?2.6.2 瀹炴祴杩?鎵€鏈夊浘褰㈠綋鏃堕兘鍦?        //   璺熺潃鎵嬫姈鏀瑰ぇ灏忥紙1.0~3.4 鐢诲竷鍗曚綅锛?锛屾病鏈夋鍖猴紝閲嶆柊鍔犲洖鏉ュ氨鏄妸閭ｄ釜姣涚梾涓€璧峰姞鍥炴潵銆?        if (Vector2.Distance(p, _dwellArmAnchor) <= DwellDragSlopLogical * DpiScale)
            return true;      // 杩欎竴涓嬬収鏍疯"骞界伒棰勮"鍚冩帀锛屽彧鏄笉鏀?
        // 鈹€鈹€ 鐩寸嚎锛氭嫋鍙︿竴澶?鈫?杞悜 / 浼哥缉锛堟敼褰㈢姸锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        if (s.Kind == StrokeKind.Line)
        {
            if (s.Points.Count < 2) return true;
            // **鍜?鐢荤洿绾?鍚屼竴濂楀惛闄?*锛堢敤鎴?2026-10-05锛?鍋滈】鍙樼洿绾块偅涓惛闄勫お澶э紝
            // 鍜岀敾鐩寸嚎缁熶竴涓€涓?锛夛細杞惛闄勫埌鐗规畩瑙?卤1掳锛孲hift = 15掳 纭綉鏍笺€丄lt = 鑷敱銆?            // 鍘熸潵璧扮殑鏄瘑鍒櫒閭ｅ"鍙惛 0/90銆佸宸?4掳"锛坄SnapToAxis`锛夆€斺€斾袱鏉¤矾鍚勪竴濂椼€?            // 瀹瑰樊宸?4 鍊嶏紱闀跨嚎涓婁竴鍋忓氨鏄嚑鍗佸儚绱犮€傜幇鍦?*鍙湁 `SnapEndPoint` 杩欎竴澶勬潈濞佸疄鐜?*銆?            bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
            var a = _dwellLinePin;
            var b = SelectionHandles.SnapEndPoint(a, p, shift, alt, out bool snapped);
            s.SetPoints(new[] { a, b });
            _shapeInclination = SelectionHandles.InclinationDegrees(a, b);
            // 闀垮害璇绘暟鍜?伪 鍚屾簮鍚屽抚銆傗殸 杩欎竴浣嶅師鏉ユ病鍐欙細骞界伒鏈熸嫋闀挎嫋鐭紝鏍囩閲岀殑"闀?
            // 涓€鐩村仠鍦?0/鏃у€硷紙鐢ㄦ埛 2026-10-05锛?鎷夐暱鍙樼煭锛岄偅涓暱搴︿篃娌″彉"锛夈€?            _shapeLength = Vector2.Distance(a, b);
            _shapeInclinationSnapped = snapped;
            _shapeAnchor = b;
            return true;
        }

        // 鈹€鈹€ 鍏跺畠鍥惧舰锛氭敼澶у皬锛堥敋鐐逛笉鍔紝鎷栧嚭鍘?= 鏀瑰彟涓€涓瑙?/ 鍗婂緞锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // 鐢ㄦ埛 2026-09-25 涓婃墜 ClassIn 閫愭潯瀹氱殑瑙勫垯锛?        //   (a) 鍦嗗舰锛?*鍦嗗績涓嶅姩**锛屾敼鍙樺渾鐨勫ぇ灏忥紱
        //   (b) 鐭╁舰锛?*鏈変竴涓《鐐逛笉鍔?*锛屾嫋鍔ㄧ浉褰撲簬鎷栧彟澶栦竴涓瑙掔嚎锛?        //   (c) **鏃嬭浆鍔熻兘涓嶈兘杞?*锛屽氨鏄嫋鍔ㄦ敼澶у皬銆?        // 涓嬮潰杩欎竴灏忔鍚屾椂瑕嗙洊 (a)(b)(c)锛岃€屼笖**涓€琛屽嚑浣曢兘涓嶉噸鍐?*锛堢収 42.9 绗?3 鏉★級锛?        //   鈶?閿氱偣锛堢绗斿皷鏈€杩滈偅涓畾涔夊厓绱狅級**涓€涓儚绱犻兘涓嶅姩** 鈫?(a)(b) 鐨?涓嶅姩"閭ｅ崐鍙ワ紱
        //   鈶?"璺熺潃璧扮殑閭ｄ釜"锛堢绗斿皷鏈€杩戠殑瀹氫箟鍏冪礌锛?*鐩存帴璁炬垚鎸囬拡** 鈫?瀵瑰渾灏辨槸
        //      "鍦嗗懆鐐规惉鍒版寚閽堜笂"锛屽崐寰勮嚜鐒?= |鎸囬拡 鈭?鍦嗗績|锛屽渾蹇冩病鍔?鈥斺€?姝ｅソ鏄?(a)锛?        //      瀵圭煩褰㈠氨鏄?鍙︿竴涓瑙?= 鎸囬拡"锛岃捣濮嬭娌″姩 鈥斺€?姝ｅソ鏄?(b)锛?        //   鈶?鍏朵綑瀹氫箟鍏冪礌锛堜笁瑙掑舰 / 骞宠鍥涜竟褰?/ 鍧愭爣绯婚偅绉嶄笁涓偣浠ヤ笂鐨勶級鎸?*閫愬垎閲忔瘮渚?*
        //      璺熺潃璧帮紝姣斾緥浠?閿氱偣 鈫?璺熺潃璧扮殑閭ｇ偣"杩欎竴娈电畻锛?        //   鈶?鍏ㄧ▼鍙尓鐐广€?*涓嶅仛濮挎€佹棆杞?* 鈫?(c)銆?        // 鈿?鈶?鐨勯櫎娉曡闃查浂锛氭煇鍒嗛噺鍦ㄥ熀鍑嗛噷灏辨槸 0锛堟瘮濡傚渾鐨勫渾鍛ㄧ偣姝ｅソ鍦ㄦ鍙虫柟锛夋椂锛?        //   閭ｄ釜鍒嗛噺鍙?1锛堜笉鍙橈級銆傗憽瀵?璺熺潃璧扮殑閭ｄ釜"鏄?*鐩存帴璧嬪€?*锛屼笉缁忚繃闄ゆ硶锛屾墍浠?        //   鍦嗘案杩滀笉浼氬洜涓鸿繖涓€€鍖栥€?        if (_dwellGhostOrigin == null || _dwellGhostOrigin.Length < 2
            || _dwellGhostAnchorIdx < 0 || _dwellGhostNearIdx < 0) return true;

        int n = _dwellGhostOrigin.Length;
        var moved = new Vector2[n];
        for (int i = 0; i < n; i++) moved[i] = _dwellGhostOrigin[i];

        var A = _dwellGhostOrigin[_dwellGhostAnchorIdx];
        moved[_dwellGhostAnchorIdx] = A;          // 鈶?閿氱偣涓嶅姩锛堝啓涓€閬嶏紝鎰忔€濆啓鍦ㄤ唬鐮侀噷锛?        moved[_dwellGhostNearIdx] = p;            // 鈶?璺熺潃绗斿皷璧扮殑閭ｄ竴鐐?
        var e0 = _dwellGhostOrigin[_dwellGhostNearIdx] - A;
        var e1 = p - A;
        float sx = MathF.Abs(e0.X) > 1e-3f ? e1.X / e0.X : 1f;
        float sy = MathF.Abs(e0.Y) > 1e-3f ? e1.Y / e0.Y : 1f;
        for (int i = 0; i < n; i++)               // 鈶?鍏朵綑鐐规寜姣斾緥
        {
            if (i == _dwellGhostAnchorIdx || i == _dwellGhostNearIdx) continue;
            var rel = _dwellGhostOrigin[i] - A;
            moved[i] = A + new Vector2(rel.X * sx, rel.Y * sy);
        }
        s.SetPoints(moved);
        return true;
    }

    /// <summary>
    /// **鍙栨秷閫変腑閭ｄ竴鍑讳笉鐣欏ⅷ**锛堢敤鎴?2026-09-23 瀹氾級锛氳繖涓€绗斿鏋滄槸"鐐逛竴涓?
    /// 锛?*绗斿皷璧拌繃鐨勬€昏矾绋?* 鈮?<see cref="DwellTapSlopLogical"/> 閫昏緫鍍忕礌锛夛紝
    /// 灏变笉鎻愪氦銆佷笉杩涙挙閿€鏍堛€?    ///
    /// 涓轰粈涔堝崟鐙畾杩欐潯锛氱偣涓€涓嬬┖鐧藉師鏈細钀?*涓€涓偣**锛坄Stroke.IsSinglePoint` 娓叉煋鎴愪竴涓疄蹇冨渾鐐癸級锛?    /// 鑰?鐐逛竴涓嬪埆澶?鐨勬剰鍥炬槸"鏀惰捣閭ｄ釜閫変腑妗?锛岀暀涓€涓ⅷ鐐圭瓑浜?*姣忔鍙栨秷閫変腑閮借剰涓€鍧楀睆骞?*
    /// 锛圛nkClass 涔熸槸杩欎箞澶勭悊鐨勶紝瑙佸畠鐨?`TryDiscardDismissTapStroke`锛夈€?    ///
    /// 鈿?**鍒ゆ嵁涓轰粈涔堜笉鏄?棣栨湯涓ょ偣鐨勪綅绉?**锛堜袱涓潵婧愰兘閭ｄ箞鍐欙紝杩欓噷閮戒笉鑳藉弬鑰冿級锛?    ///   路 鍙傝€冨疄鐜?Ink Canvas 鐢ㄧ殑鏄?**鎶捣鐐?鈭?鎸変笅鐐?* 鈮?6px"
    ///     锛坄鐢诲竷娴嬭瘯/Ink-Canvas-Dev/Ink Canvas/MW_PopupLayers.cs:230` 鐨?    ///      `DismissTapMaxMovePx`锛涘畠鍙湪"鎸変笅鐪熺殑鏀惰捣浜嗗彲瑙侀潰鏉?鏃舵墠绔嬭繖涓爣璁帮紝
    ///      鎵€浠ヨ俯鍒扮殑鏈轰細灏戯級锛?    ///   路 鎴戜滑鏃╁厛閲忕殑鏄?*绗旇抗棣栨湯涓ょ偣**銆?    ///   涓よ€呭"鐐逛竴涓?閮藉锛屼絾瀵?**涓€绗旂敾鎴愪釜闂悎鍦?*"锛堝渾 / 鍥涜竟褰?/ 浜旇竟褰⑩€︹€﹂鏈袱鐐?    ///   澶╃敓閲嶅悎锛屼綅绉?鈮?0锛変細鎶?*鏁寸瑪**鍒ゆ垚"鐐逛竴涓? 鈫?鐢ㄦ埛鐨勫ⅷ鍑┖娑堝け銆?    ///   杩欎笉鏄帹婕旓細`--dwelltest` 鐨?L 鎵帬褰撳満鎶撳埌 6 渚嬶紙"浜旇竟褰紙璁や笉鍑猴級"涓夋。灏哄 脳
    ///   涓ゆ。涔卞姩 脳 甯﹂€変腑锛屽叏閮ㄦ秷澶憋級銆?    ///   鎹㈡垚**璺▼**灏卞ぉ鐒跺垎寰楀紑锛氱偣鍦ㄥ師鍦?鈮?0锛屼换浣曠湡鐢荤殑涓€绗旈兘鏄嚑鍗佷笂鐧惧儚绱犮€?    /// </summary>
    private bool DwellTapLeaveNoInk(Stroke s)
    {
        if (s.Points.Count < 2) return true;
        var pts = new Vector2[s.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return ShapeRecognize.PathLength(pts) <= DwellTapSlopLogical * DpiScale;
    }

    // ---- 鑷鍏ュ彛锛坄--dwelltest` 鐢紱鍥涙潯閮芥槸"钖勫皝瑁?锛屼笉鍚换浣曢€昏緫锛?-------
    //
    // 涓轰粈涔堝仛鎴愬洓涓杽澹宠€屼笉鏄竴涓?澶фā鎷熷嚱鏁?锛氳嚜妫€瑕佽兘鍦?*涓棿浠讳綍涓€姝?*鏂█
    //锛堟瘮濡?鍋?559ms 鏃?*杩樻病**鍙?锛夛紝濉炶繘涓€涓嚱鏁伴噷灏卞彧鑳藉湪鏈€鍚庣湅涓€鐪肩粨鏋溿€?    // 鏃堕挓鏄嚜妫€**鐩存帴鎺?`NowMs`** 鐨勨€斺€斾笉璁?sleep锛?00ms 涓€娆°€佸崄鍑犱釜鐢ㄤ緥锛?    // 鑷浼氫粠"涓€鐪肩湅瀹?鍙樻垚"绛夊崄绉?锛岃€屼笖鐪熸満涓婃椂搴忎笉鍙鐜帮級銆?
    /// <summary>鑷鐢細鎸変竴涓嬶紙**杩?绗斾笅闈㈤偅涓€変腑妗?鐨勫垎娴佷竴璧疯蛋**锛屽垽鎹氨鏄?    /// <see cref="TryDwellSelectionPress"/> 閭ｄ竴浠斤紝鍜岀湡鎸変笅鍚屾簮锛夈€?/summary>
    internal void DwellBeginForTest(float x, float y)
    {
        if (TryDwellSelectionPress(x, y)) return;      // 琚€夋嫨鎵嬪娍鍚冩帀锛氳繖涓€绗斾笉璇ヨ捣
        BeginFreehandStrokeAt(0, Native.PT_MOUSE, x, y, x, y, 0.5f);
    }

    /// <summary>鑷鐢細鍠備竴涓噰鏍风偣锛堣蛋鐪熷叆鍙?<see cref="ExtendFreehandStroke"/>锛夈€?/summary>
    internal void DwellMoveForTest(float x, float y)
    {
        // 鍜岀湡鍏ュ彛 `OnPointerMove` 涓€鏍凤細**鎵嬮噷娌℃湁娲荤瑪鐢诲氨娌℃湁"绉诲姩"鍙█**
        // 锛堥潪鐩寸嚎鏄?璇嗗埆鍒板氨瀹氬瀷"锛岄偅涓€鍒昏繖涓€绗斿凡缁忕粨鏉熴€乣ActiveStroke` 宸叉斁鎵嬶紱
        //  鐪熸満鐨勭Щ鍔ㄨ惤鍦?`OnPointerMove` 鐨?`if (ActiveStroke != null)` 澶栭潰锛屼粈涔堥兘涓嶅仛锛夈€?        // 杩欓噷灏戝啓杩欎竴鍙ョ殑璇濓紝鑷鍠傜偣浼氱洿鎺ユ崊杩?`ExtendFreehandStroke` 閲岀殑绌哄紩鐢ㄣ€?        if (ActiveStroke == null) return;
        ExtendFreehandStroke(0, Native.PT_MOUSE, x, y, x, y, 0.5f);
    }

    /// <summary>鑷鐢細鍋囪 40ms 瀹氭椂鍣ㄥ搷浜嗕竴娆°€?/summary>
    internal void DwellTickForTest() => TickDwellShape();

    /// <summary>鑷鐢細鎶墜锛堣蛋鐪熷叆鍙?<see cref="EndStroke"/>锛夈€?/summary>
    internal void DwellEndForTest() => EndStroke();

    // ---- 榛戝專瀛愶紙`--reclive` / `--replayinput`锛夌敤鐨勮杽閽╁瓙 -------------------------
    // 鍥炴斁鐨勫師鍒欏拰 DwellProbe 涓€鏍凤細**璧扮湡鍏ュ彛銆佹帹鏃堕挓**鈥斺€旇捣绗?閲囨牱/鏀剁瑪閮借繘鐪熷嚱鏁帮紝
    // 杩欐牱鍑€鍖栥€佸缓妯°€侀娴嬪櫒鍏ㄩ儴鎸夊綋鏃剁殑鍚屼竴濂椾唬鐮佸鐜般€?
    /// <summary>榛戝專瀛愬洖鏀撅細璧风瑪锛堝惈 ptype / 鍘嬪姏锛夈€?/summary>
    internal void ReplayBeginForTest(float x, float y, float p, uint ptype)
        => BeginFreehandStrokeAt(0, ptype, x, y, x, y, p);

    /// <summary>榛戝專瀛愬洖鏀撅細鍠備竴涓噰鏍风偣锛堝拰 `AddPointCleaned` 鍚屽叆鍙ｃ€佸悓鏃堕挓鍙ｅ緞[NowMs]锛夈€?/summary>
    internal void ReplaySampleForTest(float x, float y, float p) => AddPointCleaned(x, y, p, NowMs);

    /// <summary>榛戝專瀛愬洖鏀撅細鏀剁瑪銆?/summary>
    internal void ReplayEndForTest() => EndStroke();

    /// <summary>棰勬祴鍣ㄨ瘖鏂紙榛戝專瀛愬洖鏀炬姤鍛婄敤锛夆€斺€?026-10-15锛氱嚎鎬ч娴嬫病鏈?闂?杩欏绂绘暎璁℃暟锛?    /// 鍥炴斁鎶ュ憡閲岀殑闂ㄨЕ鍙戝垪闅忎箣鎾ら櫎銆?/summary>

    /// <summary>鑷鐢細鐘舵€佹満鍦ㄥ摢涓€妗ｃ€?/summary>
    internal DwellState DwellStateForTest => _dwell.State;

    /// <summary>鑷鐢細杩欎竴娆¤嚜鍔ㄩ€変腑鏄笉鏄?鍋滈】鍙樺嚭鏉ョ殑"銆?/summary>
    internal bool DwellSelectedForTest => _dwellSelected;

    /// <summary>鑷鐢細寮€鍏筹紙榛樿寮€鐨勯偅涓級銆傜敤瀹岃杩樺師銆?/summary>
    internal void SetDwellEnabledForTest(bool on) => DwellShapeEnabled = on;

    private void OnPointerMove(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntMove++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        TouchDiagFeed(id, ptype, down: false);
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 鐩告満锛氬睆骞?鈫?鐢诲竷锛屼笅娓稿叏鎸夌敾甯冨潗鏍囪蛋
        PointerX = x; PointerY = y; PointerInside = true;

        // 鍥炴斁锛氭帶鍒舵潯鎮仠 / 鎷栬繘搴︼紙灞忓箷鍧愭爣锛屽拰鏉¤嚜宸辩殑鍧愭爣绯讳竴鑷达級銆?        // 鎷栬繘搴︽椂涓嶅啀寰€涓嬭蛋鈥斺€旇繖涓€涓茬Щ鍔ㄥ綊鎺у埗鏉°€?        if (_replay != null)
        {
            ReplayPointerMove(screenX, screenY);
            if (_replayScrubbing) return;
        }

        // 鍥惧簱闈㈡澘鐨勬偓鍋滐紙鍜屾寜涓嬪悓涓€鏉″彛寰勶細闈㈡澘鏄渶涓婇潰閭ｄ竴灞傦級銆?        // 鏍煎瓙浜竴涓嬫槸"杩欎竴鏍肩偣寰椾腑"鐨勫弽棣堬紱鏁寸悊妯″紡涓嬪厜鏍囧仠鍦ㄧ孩 鉁?涓婁篃鏄悓涓€濂椼€?        if (LibraryPanelOpen && !_drawing)
        {
            float dpi = UiUnit;   // 鍥惧簱闈㈡澘璺熺潃鐣岄潰缂╂斁璧帮紙瀹冩寕鍦ㄥ伐鍏锋潯涓婏紝涓€瀹跺瓙锛?            var panel = LibraryPanelRectNow();
            bool inside = LibraryLayout.Contains(panel, x, y);
            int hov = inside ? LibraryLayout.CellAt(panel, dpi, LibraryEntries.Count, x, y) : -1;
            if (hov != LibraryHover)
            {
                LibraryHover = hov;
                _dirty = true;      // 鎮仠楂樹寒鍙樹簡鎵嶉噸鐢伙紝涓嶆槸姣忔绉诲姩閮介噸鐢?            }
            // 杩?/ 鍑洪潰鏉垮悇鏍囦竴娆¤剰锛氫粠鐢诲竷甯﹁繘鏉ョ殑钀界偣鐜紙绗?/ 姗＄毊鐨勫湀锛夎鎿︽帀銆?            // 鍏夐潬鑴忓尯鐨?鍓嶄袱甯т复鏃跺浘鍏?涓嶅鈥斺€旀渶鍚庝竴姝ョЩ鍔ㄨ嫢娌″埆鐨勫師鍥犳爣鑴忥紝
            // 灏变笉浼氭湁娓叉煋甯у幓鎿﹂偅涓€鍦堬紙灞忓箷涓婁細鐣欎竴涓渾鐜嵃瀛愶級銆?            if (inside != _onDrawnChrome) { _onDrawnChrome = inside; _dirty = true; }
            if (inside) { ApplyCursor(); return; }     // 闈㈡澘閲岋細鍚冩帀锛屽埆璁╁簳涓嬬殑鍐呭璺熺潃鍔?        }

        // 鐣岄潰鎹曡幏浜嗘寚閽堬紙渚嬪鎸変笅鎸夐挳鍚庢粦鍑哄幓锛夛紝娑堟伅鍏ㄥ綊鐣岄潰銆?        if (UiCapturing)
        {
            ClearEngineTooltip();
            UiPointerMove(screenX, screenY, pressure, false, inverted, id);   // 閫昏緫灞忓箷鍧愭爣
            _dirty = true;
            return;
        }

        // 鐣岄潰浼樺厛锛氭偓鍋滀篃瑕佽浆鍙戙€?        //
        // 鎮仠楂樹寒銆?闈犺繎鎵嶉暱澶?銆?鎮仠 120 ms 鎵嶆诞鍑?鍏ㄩ潬杩欐潯銆備互鍓嶅彧鏈?        // **鎹曡幏鍒嗘敮**浼氳浆鍙戯紝鑰?榧犳爣鍋滃湪鎸夐挳涓?鎭版伆鏄病鎹曡幏鐨勭姸鎬佲€斺€?        // 浜庢槸鐣岄潰鐨勬偓鍋滄案杩滀笉浼氫寒锛岃€屼笖涓嶆姤閿欙紝鍙槸"鎰熻涓嶈窡鎵?銆?        // 涔﹀啓涓笉杞彂锛氶偅涓€绗斿凡缁忓綊鐢诲竷浜嗭紝鐣岄潰杩欐椂鍊欎笉璇ュ啀鍔ㄣ€?        if (!_drawing && UiPointerMove(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted, id))
        {
            ClearEngineTooltip();                 // 鎸囬拡鍦ㄧ晫闈㈤偅鏉′笂锛氬紩鎿庝晶鎻愮ず璁╀綅
            if (!_uiHover) { _uiHover = true; ApplyCursor(); }
            _dirty = true;
            return;
        }
        if (_uiHover) { _uiHover = false; ApplyCursor(); }

        // 璇惧爞璁℃椂鍗＄墖锛氭嫋鍔ㄨ窡鎵?/ 鎮仠锛堟帓鍦?PPT 鏉′箣鍓嶃€佺┛閫忎箣鍓嶁€斺€斿悓鎸変笅椤哄簭锛夈€?        if (!_drawing && TimerCardPointerMove(screenX, screenY))
        {
            ClearEngineTooltip();
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 璇惧爞鐐瑰悕鍗＄墖锛氬悓涓娿€?        if (!_drawing && RollCardPointerMove(screenX, screenY))
        {
            ClearEngineTooltip();
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 搴曢儴閭ｄ袱鏉?PPT 鎺т欢锛堟斁鏄犳椂鎵嶅湪锛夛細鎮仠楂樹寒 / 杩涘害鏉℃嫋鍔ㄣ€?        // 鎺掑湪绌块€忎箣鍓嶁€斺€旂┛閫忔椂瀹冪殑鎮仠涓庢嫋鍔ㄧ収鏍疯璺熸墜銆?        if (!_drawing && PptBarPointerMove(screenX, screenY))
        {
            UpdateEngineTooltip();                // 鏉′笂鐨勬偓鍋滄彁绀猴紙椤电爜/绠ご/鑿滃崟锛?            ApplyCursor();
            _dirty = true;
            return;
        }

        // 绌块€忔ā寮忥細鎴戜滑涓嶆敹杈撳叆锛屼篃涓嶈鍔ㄥ厜鏍囷紙閭ｆ槸涓嬪眰绐楀彛鐨勪簨锛夈€?        if (PassThrough) { ClearEngineTooltip(); _dirty = true; return; }

        // 鎷栨粴鍔ㄦ潯锛氬拰"鐢讳竴绗?浜掓枼銆?        if (ScrollBarDragging)
        {
            ClearEngineTooltip();
            UpdateScrollBarDrag(screenY);
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 瑙︽懜鎵嬪娍鐨勭Щ鍔紙8.4.0锛夛細**鎺掑湪"鍚屼竴鎸囬拡"閭ｉ亾鍒ゅ畾涔嬪墠**鈥斺€?        // 鍙屾寚鎵嬪娍閲岀浜屾牴鎵嬫寚鐨勭Щ鍔ㄦ湰鏉ヤ細琚畠鎸℃帀锛坕d != _activePointer锛夈€?        if (_touchMode != TouchMode.None && TouchMoveDispatch(id, x, y))
        {
            ApplyCursor();
            return;
        }

        if (!_drawing || id != _activePointer)
        {
            // 鎮仠璺緞銆備互鍓嶈繖閲岀洿鎺?return锛?娌¤惤绗?鏃跺紩鎿庡畬鍏ㄤ笉鐭ラ亾鎸囬拡鍦ㄥ摢鍎匡紝
            // 浜庢槸鎮仠鍏夋爣銆佹粴鍔ㄦ潯鎮仠銆佽惤鐐归瑙堥兘鏃犱粠璋堣捣銆?            UpdateScrollBarHover(screenX, screenY);
            UpdateBarHover(x, y);                  // 鎿嶄綔鏉′節鏍肩殑 hover 鎬侊紙鐢讳笌鍛戒腑鍚屾簮锛?            UpdateEngineTooltip();                 // 寮曟搸渚ф偓鍋滄彁绀猴紙鎿嶄綔鏉?/ PPT 鏉★級
            ApplyCursor();
            if (DrawnCursor != ToolCursorShape.None) _dirty = true;
            return;
        }

        // 鎴浘鐨?鏉惧紑鍚庤皟鏁?锛?.3.0锛夛細鎷栨墜鏌?鎷栨暣浣撱€?*鍜屽綋鍓嶅伐鍏锋棤鍏?*銆?        if (CaptureAdjusting) { CaptureAdjustMove(x, y); return; }

        // 娴眰闈㈡澘閲岀殑鎷栧姩锛堢矖缁嗘粦鏉?/ 鑷畾涔夊彇鑹叉澘锛夛細**鍜屽綋鍓嶅伐鍏锋棤鍏?*銆?        // 闈㈡澘鍦ㄤ换浣曞伐鍏蜂笅閮藉彲鑳藉紑鐫€锛堝浘褰㈠伐鍏烽偅涓嚜鍔ㄩ€変腑鐨勬涔熻兘鎽婂紑锛夛紝
        // 鏃у疄鐜板彧鍦?Marquee 鍒嗘敮閲屽鐞嗘粦鏉♀€斺€斿浘褰㈠伐鍏烽噷灏辨嫋涓嶅姩銆?        if (_sliderDragging) { DragWidthSliderTo(x); return; }
        if (_colorDragging) { DragColorPickerTo(x, y); return; }

        // 閫夋嫨鎷栧姩 / 妗嗛€夛細**鎸夌姸鎬佽矾鐢憋紝涓嶄緷璧?`_touchMode`**锛?026-09-30 淇級銆?        // 瑙︽懜闀挎寜鎴愮珛涔嬪悗锛屾ā寮忓瓧娈靛彲鑳借寮傚父璺緞锛堜涪鎹曡幏绛夛級娓呮帀鈥斺€旈偅鏃舵嫋鍔ㄤ細鎺夎繘
        // 宸ュ叿鍒嗘敮銆佽褰撴垚"鎺ョ潃鍐欎竴绗?锛堝疄娴嬬棁鐘讹細闀挎寜閫変腑浜嗗璞★紝浣嗘嫋涓嶅姩锛夈€?        // 杩欎袱鏉″榧犳爣閭ｆ潯璺槸**鍚屼竴涓皟鐢?*锛堝師鏉ュ湪 Marquee 鍒嗘敮閲岋級锛屾墍浠ラ噸澶嶆棤瀹炽€?        if (SelDragging) { UpdateSelDrag(x, y); return; }
        if (MarqueeActive) { ExtendMarqueeTo(x, y); _dirty = true; return; }

        var tool = inverted ? Tool.Eraser : Tool;
        // 鎵嬫祴鍙帮細鏁?鎸囬拡娑堟伅"鑰屼笉鏄?鎿﹂櫎姝?鈥斺€旀秷鎭箣闂寸殑闂撮殧鎵嶆槸璺熶笉璺熸墜銆?        if (tool == Tool.Eraser || tool == Tool.PixelEraser) EraserTelemetry?.Move(NowMs);
        switch (tool)
        {
            case Tool.Eraser:
                EraseAlongPath(x, y);
                break;

            case Tool.PixelEraser:
                EraseRectAlongPath(x, y);
                break;

            case Tool.Capture:
                if (CaptureActive) ExtendCaptureTo(x, y);
                break;

            case Tool.Marquee:
                if (SelDragging) UpdateSelDrag(x, y);
                else ExtendMarqueeTo(x, y);
                break;

            case Tool.Laser:
                Laser.Add(x, y, NowMs);
                break;

            // 鍥惧舰锛氬彧鏇存柊**鎺у埗鐐?*锛堝浘褰㈢敱瀹冪殑瀹氫箟鍏冪礌鍐冲畾锛夛紝涓嶅線閲屽爢閲囨牱鐐广€?            // 棰勮灏辫蛋"姝ｅ湪涔﹀啓鐨勯偅涓€绗?閭ｆ潯璺紙瀹冩湰鏉ュ氨姣忓抚閲嶇敾锛夛紝鎵€浠ュ疄鏃躲€?            //
            // 鍒ゆ嵁鍚屾牱璧?<see cref="IsShapeTool"/>锛?*涓嶅湪杩欓噷閫愪釜鍒?case**鈥斺€?            // 鍜?OnPointerDown 閭ｆ潯鏄悓涓€涓悊鐢憋紙閭ｈ竟婕忔敼杩囦竴娆★紝涓や釜鍦版柟涓€璧蜂慨鎺夛級銆?            default:
                // 鎷?*鎵嬫焺**锛堟敼鍑犱綍锛? 鎷栨暣鏉★紝鎺掑湪鍥惧舰鍓嶉潰锛氳繖涓€绫绘嫋鍔ㄦ槸鍦?*鎸変笅閭ｄ竴鍒?*
                // 琚?OnPointerDown 鎺ヤ笅鐨勶紝鑰岄偅鏃剁殑宸ュ叿鍙兘杩樻槸鍥惧舰宸ュ叿
                //锛堢敾瀹岃嚜鍔ㄩ€変腑閭ｄ釜妗嗭紝瑙?`AutoSelectionPress` 閭ｄ竴娈碉級锛屼篃鍙兘鎷栧埌涓€鍗?                // 琚儹閿崲浜嗗伐鍏凤紙瑙?SwitchTool锛夈€備袱绉嶆儏鍐?`ActiveStroke` 閮芥槸 null 鈥斺€?                // 钀藉埌涓嬮潰 UpdateShapePreview 涓婂氨浠€涔堥兘涓嶅彂鐢燂紙榧犳爣鎷栧緱鍔ㄣ€佹ā鍨嬩竴鍔ㄤ笉鍔級銆?                if (SelDragging)
                {
                    UpdateSelDrag(x, y);
                    break;
                }
                if (ActiveStroke != null && IsShapeTool(tool))
                {
                    UpdateShapePreview(x, y);
                    break;
                }
                if (ActiveStroke != null)
                {
                    ExtendFreehandStroke(id, ptype, x, y, screenX, screenY, pressure);
                }
                break;
        }
        // 鎷栨嫿涓紙宸叉崟鑾凤級锛氱郴缁熶笉浼氬啀鍙?WM_SETCURSOR锛屾瘡甯ц嚜宸辩淮鎶ゃ€?        ApplyCursor();
        _dirty = true;
    }

    private void OnPointerUp(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntUp++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (TouchHud && _touchDiagIds.Remove(id))
        {
            RebuildTouchHud();
            _dirty = true;
        }

        // 鐣岄潰浼樺厛鏀跺熬锛屽惁鍒欎細鐣欎笅"鎸夐挳涓€鐩存寜鐫€"鐨勭姸鎬併€?        if (UiCapturing && ReadPointer(id, out float ux, out float uy, out float upressure,
                                       out bool uinverted, out _))
        {
            UiPointerUp(ux, uy, upressure, false, uinverted, id);
            // **蹇呴』鏄惧紡鏀惧紑鎹曡幏**锛氭寜閽笂涔熻蛋 SetCapture锛堟嫋鍑烘寜閽€佸湪鎸夐挳涓婃粦寮€
            // 閮借缁х画鏀跺埌娑堟伅锛夛紝鑰岀郴缁?*涓嶄細**鍦ㄦ寜閿姮璧锋椂鏇挎垜浠斁寮€銆?            // 蹇樹簡杩欎竴鍙ョ殑鍚庢灉涓嶆槸"鎸夐挳鍗′綇"锛岃€屾槸**鏁村彴鏈哄櫒鐨勯紶鏍囦簨浠堕兘杩樻寕鍦?            // 鎴戜滑绐楀彛涓?*锛氱┛閫忋€佺偣涓嬪眰绋嬪簭銆佹崲鍒板埆鐨勮蒋浠跺叏涔憋紝鑰屼笖鐜拌薄绂诲師鍥犲緢杩溿€?            Native.ReleaseCapture();
            UiCapturing = false;
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ScrollBarDragging)
        {
            // 闃插尽锛?026-10-09锛夛細杩欐潯璺緞鍦?瑙︽懜鎷栨粴鍔ㄦ潯"鏃?*蹇呴』鍏堟竻瑙︽懜瑙︾偣琛?*鈥斺€?            // 鐪熸満 bug 鐨勬牴鍥犲氨鏄畠锛氭嫋瀹屾姮鎵嬫紡娓?鈫?琛ㄩ噷鐣欎笅"骞界伒鎵嬫寚" 鈫?涔嬪悗鍐欏瓧銆?            // 鍐嶆嫋婊氬姩鏉″叏琚偅寮犺〃鍚冩帀锛堢洿鍒伴噸鍚級銆傛粴鍔ㄦ潯鍚屾棩宸插仠鐢紝杩欏彞鐣欑潃闃插娲汇€?            if (_activePointerType == Native.PT_TOUCH) _touch.Up(id);
            EndScrollBarDrag();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 璇惧爞璁℃椂鍗＄墖锛?*鍙鎸変笅褰掕繃瀹冿紝灏辨棤鏉′欢鏀惧紑鎹曡幏**锛堝摢鎬曞崱鐗囧凡缁忚 鉁?鍏虫帀銆?        // 鎴栬€?ReadPointer 杩欎竴娆″け璐ワ級鈥斺€旀紡鎺夎繖涓€鍙ュ氨鏄?鏁存満榧犳爣鎸傚湪鎴戜滑绐楀彛涓?銆?        if (_timerCapturing)
        {
            if (ReadPointer(id, out float tx, out float ty, out _, out _, out _))
                TimerCardPointerUp(tx, ty);
            _timerCapturing = false;
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 璇惧爞鐐瑰悕鍗＄墖锛氬悓涓娿€?        if (_rollCapturing)
        {
            if (ReadPointer(id, out float rx2, out float ry2, out _, out _, out _))
                RollCardPointerUp(rx2, ry2);
            _rollCapturing = false;
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // PPT 鎺т欢鏉★紙鏀炬槧鏃舵墠鍦級锛?*杩欎竴娆℃寜涓嬫槸瀹冨悆鎺夌殑灏辩敱瀹冩敹灏?*鈥斺€?        // 闀挎寜锛堟嬁璧?/ 鎷栬蛋锛夊拰鐭寜锛堝脊椤靛彿闈㈡澘锛夐兘鍦ㄨ繖閲岀粨鏉熴€?        // 蹇呴』鏄惧紡 ReleaseCapture锛堟寜涓嬮偅涓€鍒?SetCapture 杩囷級锛氬繕浜嗚繖涓€鍙ワ紝
        // **鏁村彴鏈哄櫒鐨勯紶鏍囬兘杩樻寕鍦ㄦ垜浠獥鍙ｄ笂**锛堣涓婇潰閭ｅ彞娉ㄩ噴锛夈€?        if (_pptCapturing && ReadPointer(id, out float bx, out float by, out _, out _, out _))
        {
            _pptCapturing = false;
            PptBarPointerUp(bx, by);
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 鍥炴斁鎺у埗鏉★細杩欎竴涓嬫寜涓嬪綊瀹冨氨鐢卞畠鏀跺熬锛堟嫋杩涘害鏉＄殑鏉炬墜涔熷湪杩欓噷锛夈€?        if (_replayCapturing && ReadPointer(id, out float rx, out float ry, out _, out _, out _))
        {
            _replayCapturing = false;
            ReplayPointerUp(rx, ry);
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 瑙︽懜鎵嬪娍鏀跺熬锛?.4.0锛夛細鍜岀晫闈?/ 婊氬姩鏉￠偅涓ゅ潡鍚屼竴涓舰鐘垛€斺€旇嚜宸辨敹灏俱€佽嚜宸辨斁鎹曡幏銆?        // **涓嶈兘绛?`id != _activePointer` 閭ｉ亾闂?*锛氬弻鎸囨墜鍔块噷绗簩鏍规墜鎸囩殑鎶墜鏈潵浼氳瀹冩尅鎺夈€?        if (TouchUpDispatch(id))
        {
            if (!_touch.Any)                    // 鍏ㄩ儴鎶捣锛氳繖涓€杞墜鍔挎敹鍦?            {
                Native.ReleaseCapture();
                _drawing = false;
                ApplyCursor();
            }
            _dirty = true;
            return;
        }

        if (id != _activePointer) return;
        Native.ReleaseCapture();
        _drawing = false;
        EndStroke();
    }

    /// <summary>鍔ㄦ€佹鐨細钀界瑪閭ｄ竴鍒绘妸閫熷害涓庣郴鏁板綊鍒板熀鍑嗭紙绗竴涓嬪氨鏄粦鏉￠偅涓ぇ灏忥紝绉诲姩涓墠娓愬叆锛夈€?/summary>
    private void ResetDynamicEraser()
    {
        double now = NowMs;
        _eraseSpeedEma = 0f;
        _eraseFine = _eraseGross = 0f;
        _eraseFineDist = _eraseFineMs = 0f;
        _eraseGrossDist = _eraseGrossMs = 0f;
        _eraseDynFactor = 1f;
        _eraseTarget = 1f;
        _eraseHoldBaseMs = now;
        _eraseDecaying = false;
        _eraseTestIdle = 0f;
        _eraseLastMs = now;
        RebuildEraserHud();
    }

    /// <summary>
    /// 缁欏畾閫熷害鐨?*绋虫€?*鐩爣绯绘暟锛堣嚜妫€鍜屾枃妗ｅ叡鐢紱鍥炲樊甯﹂噷鍙?浠庢參寰€涓婅蛋"閭ｄ竴鏀級銆?    /// 鏇茬嚎锛歚0.8 浠ヤ笅 鈫?1.0`锛堝熀鍑嗭紝涓嶇缉锛夛紱浠ヤ笂 `1.0 + (v 鈭?0.8) 脳 1.0`锛屽す鍒?1.0~2.5锛?.3 鍒伴《锛夈€?    /// </summary>
    internal static float EraserTargetFactorForSpeed(float speedPxPerMs)
    {
        if (speedPxPerMs <= EraserSpeedBack) return EraserFactorMin;
        return Math.Clamp(EraserFactorMin + (speedPxPerMs - EraserSpeedKnee) * EraserSpeedSlope,
                          EraserFactorMin, EraserFactorMax);
    }

    /// <summary>
    /// 骞虫粦涓€姝ワ細鏈濈洰鏍囨尓锛堟定/鏀舵椂闂村父鏁颁笉鍚?+ 鏈€灏戞闀匡級锛屽甫**鍥炲樊**鈥斺€?    /// 閫熷害钀藉湪 [0.6, 0.8] 涔嬮棿鏃剁洰鏍囦繚鎸佷笉鍙橈紝閫熷害鍦ㄩ棬妲涢檮杩戞姈涔熶笉浼氭潵鍥炲垏銆?    ///
    /// <paramref name="allowGrow"/> = false 鏃?*鍙鏀躲€佷笉璁告定**锛堢┖闂茬紦閲婄敤锛夛細
    /// 鍋滀綇涓嶅姩鐨勬椂鍊?gross 閫氶亾杩樻病婕忕┖銆佺洰鏍囨殏鏃惰繕鍦ㄩ珮澶勶紝涓嶅厑璁稿畠鎶婂昂瀵?*瓒婂仠瓒婂ぇ**銆?    /// </summary>
    private void ApplyEraserTarget(double nowMs, bool allowGrow = true)
    {
        float target;
        if (_eraseSpeedEma < EraserSpeedBack) target = EraserFactorMin;
        else if (_eraseSpeedEma > EraserSpeedKnee) target = EraserTargetFactorForSpeed(_eraseSpeedEma);
        else target = _eraseTarget;                          // 鍥炲樊甯︼細淇濇寔

        if (!allowGrow && target > _eraseDynFactor) target = _eraseDynFactor;   // 绌洪棽锛氬彧璁告敹
        _eraseTarget = target;
        float gap = target - _eraseDynFactor;
        if (MathF.Abs(gap) < 0.001f) { _eraseDynFactor = target; return; }

        float dt = (float)Math.Max(0.5, nowMs - _eraseLastMs);
        float tau = target > _eraseDynFactor ? EraserGrowTauMs : EraserShrinkTauMs;
        float step = gap * (1f - MathF.Exp(-dt / tau));
        if (MathF.Abs(step) < EraserMinStep)
            step = MathF.Sign(gap) * MathF.Min(EraserMinStep, MathF.Abs(gap));
        _eraseDynFactor += step;
    }

    /// <summary>
    /// 閫熷害浼拌锛氫袱鏉￠€氶亾鍚勮嚜"鎸夋椂闂?璺濈鏀掓弧涓€涓獥鍙?鍐嶇粨绠楋紝鍙?*杈冨ぇ鍊?*褰撶洰鏍囬€熷害銆?    /// 路 fine锛?0ms / 12px锛夛細璺熸墜锛屽揩鎵珛鍒诲弽鏄狅紱
    /// 路 gross锛?50ms / 120px锛夛細鎱㈤€氶亾鈥斺€旀參涓嬫潵鏃跺畠杩樺仠鍦ㄦ棫閫熷害涓婏紝灏哄涓嶄細涓€鎱㈠氨濉屻€?    /// 锛堟妱 MyPaint 鐨?Fine/Gross Speed锛歠ine 璺熸墜銆乬ross "changes very slowly"銆傦級
    /// </summary>
    private void FeedEraserSpeed(float dist, float dtMs)
    {
        float dt = MathF.Max(0.5f, dtMs);

        _eraseFineDist += dist; _eraseFineMs += dt;
        if (_eraseFineMs >= EraserFineWindowMs || _eraseFineDist >= EraserFineWindowPx)
        {
            _eraseFine = _eraseFineDist / MathF.Max(1f, _eraseFineMs);
            _eraseFineDist = 0f; _eraseFineMs = 0f;
        }

        _eraseGrossDist += dist; _eraseGrossMs += dt;
        if (_eraseGrossMs >= EraserGrossWindowMs || _eraseGrossDist >= EraserGrossWindowPx)
        {
            _eraseGross = _eraseGrossDist / MathF.Max(1f, _eraseGrossMs);
            _eraseGrossDist = 0f; _eraseGrossMs = 0f;
        }

        _eraseSpeedEma = MathF.Max(_eraseFine, _eraseGross);
    }

    /// <summary>
    /// 鍔ㄦ€佹鐨細鍠備竴涓噰鏍风偣锛堢敾甯冨潗鏍囷級鏇存柊"閫熷害 鈫?灏哄绯绘暟"銆?    /// 閫熷害璧?`FeedEraserSpeed`锛堟寜鏃堕棿绐楀彛锛夛紱鐩爣涓庡钩婊戣 `ApplyEraserTarget`銆?    /// </summary>
    private void UpdateDynamicEraser(float x, float y)
    {
        if (!DynamicEraser) { _eraseDynFactor = 1f; return; }
        if (EraserFactorOverrideForTest > 0f) { _eraseDynFactor = EraserFactorOverrideForTest; return; }
        double now = NowMs;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dt = (float)Math.Max(0.5, now - _eraseLastMs);
        FeedEraserSpeed(MathF.Sqrt(dx * dx + dy * dy), dt);
        ApplyEraserTarget(now);
        _eraseLastMs = now;
        _eraseHoldBaseMs = now;          // 鏈夋柊绉诲姩锛氱紦閲婄殑淇濇寔鏈熼噸鏂板紑濮?        _eraseDecaying = false;
        _eraseTestIdle = 0f;

        // 璇婃柇娴眰锛氭嫋鍔ㄤ腑闄愰鍒锋柊锛?0ms锛夛紝璁╀汉鑳界湅娓?鎴戣繖涓€涓嬫槸浠€涔堥€熷害"銆?        if (EraserHud && now >= _eraserHudNextMs)
        {
            _eraserHudNextMs = now + 60;
            RebuildEraserHud();
            _dirty = true;
        }
    }

    /// <summary>
    /// 姣忓抚鎺ㄤ竴娆★細鎸囬拡**鍋滀綇**涔嬪悗鎶婂昂瀵搁『鐫€缂撻噴鍥炲幓锛堜笉鍐嶇瓑涓嬩竴娆＄Щ鍔ㄦ潵瑙﹀彂锛夈€?    ///
    /// 涓轰粈涔堣杩欎竴鏉★細灏哄鍙湪**鏀跺埌鎸囬拡娑堟伅**鏃舵墠鏇存柊鈥斺€旈紶鏍囧仠浣?= 娌℃湁娑堟伅 = 灏哄**鍐诲湪鏈€澶?*锛?    /// 鍐嶈交杞讳竴鍔紝閫熷害绐楀彛绔嬪埢绠楀嚭寰堝皬鐨勯€熷害 鈫?鐩爣鍥炲埌涓嬮檺 鈫?鐪嬭捣鏉?绐佺劧鍙樺皬"锛堢敤鎴锋姤鐨勶級銆?    /// 鐜板湪鍋滀綇瓒呰繃 HoldMs 灏辨瘡甯у杺涓€涓?闆惰窛绂?鏍锋湰锛氫袱鏉￠€氶亾闅忔椂闂磋嚜宸辨紡绌猴紝鐩爣闅忎箣鍥炶惤鍒颁笅闄愶紝
    /// 灏哄鎸?鏀?鐨勬椂闂村父鏁伴『鐫€缂╁洖鍘烩€斺€?*涓嶉渶瑕佸彟鍐欎竴濂楄“鍑忛€昏緫**锛堝拰 MyPaint 鐨?杈撳叆婊ゆ尝"鍚屾簮锛?    /// 浠栦滑鐨勯€熷害杈撳叆鍦ㄥ仠姝㈡椂鑷劧褰掗浂锛屾垜浠彧鏄妸"褰掗浂"鍦ㄧ┖闂叉椂涔熷杺杩涘幓锛夈€?    ///
    /// "姝ｅ湪鍑忛€?锛坒ine 鏄庢樉灏忎簬 gross锛? 蹇壂鍒氬仠涓?鈫?鐢ㄦ洿鐭殑淇濇寔鏃堕棿**鎻愬墠**寮€濮嬫敹锛?    /// 杩欏氨鏄?鍔犻€熷害"浠ョǔ瀹氬舰寮忓叆鍦虹殑浣嶇疆锛堜袱涓护娉㈤噺鐨勫樊 = 鍔犻€熷害鐨勭鍙凤紝鍣０琚帇浜嗕袱閬嶏級銆?    /// </summary>
    internal void TickEraserIdleDecay()
    {
        if (!DynamicEraser || !PixelEraseDragging) return;
        if (TickEraserIdleDecayCore(NowMs)) _dirty = true;   // 杩樺湪缂撻噴 鈫?淇濊瘉杩樻湁涓嬩竴甯?    }

    /// <summary>缂撻噴涓€姝ャ€傝繑鍥?true = 杩欎竴姝ュ姩杩囷紙璋冪敤鏂规嵁姝や繚鎸佸埛鏂帮級銆?/summary>
    private bool TickEraserIdleDecayCore(double nowMs)
    {
        if (!_eraseDecaying)
        {
            // 淇濇寔鏈燂細浠?涓婁竴娆＄Щ鍔?寮€濮嬬畻锛屽 HoldMs 鎵嶈繘鍏ョ紦閲婏紙鏈熼棿灏哄**鏁呮剰鍐讳綇**锛夈€?            float idle = (float)(nowMs - _eraseHoldBaseMs);
            if (idle < EraserIdleHoldMs) return false;
            _eraseDecaying = true;
            _eraseLastMs = nowMs - 1.0;                 // 缂撻噴鐨勭涓€姝ユ寜 1ms 鎺ㄨ繘
        }
        float step = (float)Math.Max(1.0, nowMs - _eraseLastMs);
        FeedEraserSpeed(0f, step);                      // 闆惰窛绂绘牱鏈?鈫?閫氶亾闅忔椂闂存紡绌?        ApplyEraserTarget(nowMs, allowGrow: false);     // 绌洪棽锛氬彧璁告敹銆佷笉璁告定
        _eraseLastMs = nowMs;
        return true;
    }

    /// <summary>鑷鐢細妯℃嫙"绌洪棽 dtMs"锛堜繚鎸佹湡绱銆佷箣鍚庢瘡姝ョ紦閲婏級锛岃繑鍥炲綋鍓嶇郴鏁般€?/summary>
    internal float DynamicEraserIdleForTest(float dtMs)
    {
        if (!_eraseDecaying)
        {
            _eraseTestIdle += dtMs;
            if (_eraseTestIdle < EraserIdleHoldMs) return _eraseDynFactor;   // 淇濇寔鏈熷唴涓嶅姩
            _eraseDecaying = true;
        }
        FeedEraserSpeed(0f, dtMs);
        ApplyEraserTarget(_eraseLastMs + dtMs, allowGrow: false);
        _eraseLastMs += dtMs;
        return _eraseDynFactor;
    }

    /// <summary>鍒锋柊姗＄毊璇婃柇娴眰鐨勬枃瀛楋紙閫熷害 / 鐩爣 / 褰撳墠绯绘暟 / 灏哄 + 鏇茬嚎鍙傛暟锛夈€?/summary>
    private void RebuildEraserHud()
    {
        if (!EraserHud) return;
        float f = PixelEraseDragging ? _eraseDynFactor : 1f;
        EraserHudText = $"閫熷害 {_eraseSpeedEma:F2} px/ms锛坒ine {_eraseFine:F2} / gross {_eraseGross:F2}锛夈€€"
                      + $"鐩爣 脳{_eraseTarget:F2}銆€褰撳墠 脳{f:F2}銆€"
                      + $"灏哄 {PixelEraserWidthLogical * f:F0}脳{PixelEraserHeightLogical * f:F0} 閫昏緫鍍忕礌"
                      + $"锛堝熀鍑?{PixelEraserWidthLogical:F0}脳{PixelEraserHeightLogical:F0}锛塡n"
                      + $"鏇茬嚎锛氣墹{EraserSpeedKnee:F2} 鎭?脳{EraserFactorMin:F1}锛堟鍖猴級鈫?鈮EraserSpeedTop:F1} 灏侀《 脳{EraserFactorMax:F1}锛?
                      + $"鍥炲樊 {EraserSpeedBack:F2}/{EraserSpeedKnee:F2}锛?
                      + $"閫氶亾 fine {EraserFineWindowMs:F0}ms / gross {EraserGrossWindowMs:F0}ms锛?
                      + $"鍋?{EraserIdleHoldMs:F0}ms 鍚庣紦閲婏紙鍑忛€?{EraserDecelHoldMs:F0}ms锛夛紱"
                      + $"骞虫粦 娑?{EraserGrowTauMs:F0} / 鏀?{EraserShrinkTauMs:F0} ms";
    }

    /// <summary>鑷鐢細鎶婂昂瀵哥郴鏁伴拤姝诲湪涓€涓€间笂锛堥獙"妗嗗拰鎿︿弗涓濆悎缂?鏃朵笉鍙楁墜閫熷奖鍝嶏級銆? = 涓嶈鐩栥€?/summary>
    internal float EraserFactorOverrideForTest;

    /// <summary>鑷鐢細鎶婁竴涓€熷害锛堢墿鐞嗗儚绱?姣锛夊杺杩涘幓锛岀湅绠楀嚭浠€涔?*绋虫€?*绯绘暟锛堝悗闂ㄥ叧鎺夋椂鎭?1锛夈€?/summary>
    internal float DynamicEraserFactorForTest(float speedPxPerMs)
    {
        if (!DynamicEraser) return 1f;
        _eraseSpeedEma = speedPxPerMs;
        _eraseTarget = EraserTargetFactorForSpeed(speedPxPerMs);
        _eraseDynFactor = _eraseTarget;
        return _eraseDynFactor;
    }

    /// <summary>鑷鐢細鍠備竴姝?宸茬煡閫熷害"锛堣蛋鐪熷疄鐨勭洰鏍?+ 骞虫粦閭ｄ竴娈碉級锛岃繑鍥炲綋鍓嶇郴鏁扳€斺€旀參閫熸姈涓嶆姈闈犲畠銆?/summary>
    internal float DynamicEraserAdvanceForTest(float speedPxPerMs, double dtMs)
    {
        _eraseSpeedEma = speedPxPerMs;
        ApplyEraserTarget(_eraseLastMs + dtMs);
        return _eraseDynFactor;
    }

    /// <summary>鑷鐢細鐩存帴鍠?璺濈 + 鏃堕棿"缁欓€熷害绐楀彛锛岃繑鍥炵獥鍙ｉ€熷害锛坧x/ms锛夆€斺€旈獙绐楀彛鎶椾笉鎶楁姈銆?/summary>
    internal float DynamicEraserFeedForTest(float dist, double dtMs)
    {
        FeedEraserSpeed(dist, (float)dtMs);
        return _eraseSpeedEma;
    }

    /// <summary>鑷鐢細鎶婂姩鎬佹鐨殑鐘舵€佸綊闆讹紙绛変环浜?鍒氳惤绗?锛夈€?/summary>
    internal void ResetDynamicEraserForTest() => ResetDynamicEraser();

    /// <summary>鑷鐢細鐩存帴寮€/鍏冲姩鎬佹鐨紙浜у搧閲岃蛋 `--eraserfixed`锛夈€?/summary>
    internal bool DynamicEraserForTest { get => DynamicEraser; set => DynamicEraser = value; }

    /// <summary>
    /// Walks the eraser along the segment the pointer just travelled instead of
    /// only testing the newest position. Without this, a quick flick leaves gaps
    /// between samples and only some of the crossed strokes disappear - which is
    /// what "the eraser feels unresponsive" actually looks like.
    /// </summary>
    private void EraseAlongPath(float x, float y)
    {
        float r = EraserRadius;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, r * 0.5f)), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseAt(ex, ey, r);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    /// <summary>
    /// 鍍忕礌姗＄毊娌挎寚閽堣蛋杩囩殑璺緞鎵竴閬嶃€傜悊鐢卞拰鏁寸瑪姗＄毊涓€鏍凤細鍙祴鏈€鏂伴偅涓€鐐癸紝蹇垝灏变細鍦?    /// 涓ゆ閲囨牱涔嬮棿婕忔帀涓€鏁存銆傛闀垮彇**鐭竟鐨勪竴鍗?*鈥斺€旂浉閭讳袱涓綅缃嚦灏戦噸鍙犱竴鍗婏紝
    /// 涓棿涓嶄細鏈夌紳锛涗笂闄?64 姝ワ紝鍏嶅緱涓€娆″ぇ璺ㄥ害鎷栧姩鎶婃瘡甯х殑浠ｄ环鎷夌垎銆?    /// </summary>
    private void EraseRectAlongPath(float x, float y)
    {
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        // 鍔ㄦ€佹鐨細杩欎竴娈电殑"閫熷害"绠楀嚭鐨勫昂瀵?= 妗嗙敾鐨勯偅涓昂瀵革紙鍚屼竴浠斤紝鐪嬭鐨勫氨鏄摝鐨勶級銆?        // 钀界瑪閭ｄ竴涓嬭繕娌￠€熷害 鈫?绯绘暟 1 鈫?鍜屾偓鍋滄椂鐪嬪埌鐨勬涓€鏍峰ぇ銆?        UpdateDynamicEraser(x, y);
        float hw = PixelEraserCursorHalfWidthPx, hh = PixelEraserCursorHalfHeightPx;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, MathF.Min(hw, hh))), 1, 64);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseRectAt(ex, ey, hw, hh);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    // =====================================================================
    //  鎴浘
    //
    //  娴佺▼锛堟瘡涓€姝ラ兘鏈夊畠闈炶繖涔堝仛涓嶅彲鐨勭悊鐢憋級锛?    //
    //   1. 鎷栦竴涓锛堝拰妗嗛€夊悓涓€涓墜鍔匡紝鐢ㄦ埛涓嶇敤瀛︽柊鍔ㄤ綔锛夛紱
    //   2. **鎶婅嚜宸辩殑瑕嗙洊灞傝棌璧锋潵**鍐嶆姄灞忊€斺€斾笉鐒舵姄鍒扮殑灏辨槸"鎵规敞鐩栧湪 PPT 涓?
    //      閭ｄ竴鍧紝绛変簬鎶婅€佸笀鍐欑殑瀛楀張璐村洖鍘讳竴寮狅紱
    //   3. 鎶撳埌鐨勬槸涓€姣斾竴鐨?*鐗╃悊鍍忕礌**锛屾寜 DPI 鎶樻垚鐢诲竷灏哄锛?    //   4. 鏀惧埌**瑙嗗彛宸︿笂瑙?*骞剁珛鍒婚€変腑鈥斺€旂敤鎴风湅寰楄銆佹嫋寰楀姩銆佽兘鎺ョ潃澶嶅埗/缈昏浆锛?    //   5. 鍚屾椂鍐欒繘鍓创鏉匡細鑰佸笀瑕佹妸瀹冭创鍒板埆鐨勫湴鏂癸紙PPT銆佸井淇°€乄ord锛夋槸甯歌鍔ㄤ綔銆?    //  =====================================================================

    /// <summary>
    /// 瑕嗙洊灞傝棌璧锋潵鏃剁敤鐨勫彞鏌勫垪琛ㄣ€?*澶氬睆涔熻涓€璧疯棌**锛屽惁鍒欏壇灞忎笂鎶撳埌鐨勮繕鏄?    /// 鎴戜滑鑷繁鐢荤殑涓滆タ锛堝灞忔暀瀹ゅ緢甯歌锛夈€?    /// </summary>
    private IntPtr[] OverlayHandles()
    {
        var list = new List<IntPtr>(_windows.Count);
        foreach (var w in _windows) list.Add(w.Hwnd);
        // 鎶撳睆鏃惰杩?鎺ヨ緭鍏ュ皬绐?涓€璧疯棌锛氬畠鏄?1/255 鐨勪竴灞傜伆锛岃倝鐪肩湅涓嶈锛?        // 浣嗘媿杩涘浘閲屽氨鏄竴灞傝剰锛堟埅鍥剧晫闈㈡案杩滀笉璇ュ嚭鐜板湪鎴浘閲岋級銆?        // PPT 鏉￠偅鍧楀皬绐楀悓鐞嗭紙瀹冧篃鏄垜浠殑绐楀彛锛夈€?        if (_uiInputHwnd != IntPtr.Zero) list.Add(_uiInputHwnd);
        if (_pptInputHwnd != IntPtr.Zero) list.Add(_pptInputHwnd);
        if (_timerInputHwnd != IntPtr.Zero) list.Add(_timerInputHwnd);
        if (_rollInputHwnd != IntPtr.Zero) list.Add(_rollInputHwnd);
        return list.ToArray();
    }

    /// <summary>鎴浘钀藉湴鐨勪綅缃細**瑙嗗彛宸︿笂瑙?*寰€閲岀缉涓€鐐癸紙瑙?CaptureMargin 鐨勬敞閲婏級銆?/summary>
    internal const float CaptureMarginLogical = 24f;

    /// <summary>
    /// 鏉炬墜锛?*鍏堜笉瀹氬眬**锛?.3.0锛夆€斺€旀澶皬**涓嶉€€鍑哄彇鏅?*锛?.3.1锛氬崟鍑讳竴涓嬪彧褰?杩樻病鎯冲ソ"锛?    /// 鍥炲埌绛夋嫋妗嗙殑鐘舵€侊級锛屽惁鍒欒繘鍏?璋冩暣"闃舵绛夌‘璁ゃ€?    /// 鎶撳睆宸茬粡鍦ㄨ繘鍏ュ彇鏅椂鍋氬畬浜嗭紙鍐荤粨甯э級锛岃繖閲屽彧鍒ゅ昂瀵搞€佸垏鐘舵€併€?    /// </summary>
    private void EndCapture()
    {
        _dirty = true;

        var s = CaptureFrameScreen();
        int w = (int)MathF.Round(s.MaxX - s.MinX);
        int h = (int)MathF.Round(s.MaxY - s.MinY);
        float minPx = CaptureMinSizeLogical * DpiScale;
        if (w < minPx || h < minPx)
        {
            // 鍗曞嚮锛堟病鎷栧嚭妗嗭級锛?*缁х画鍙栨櫙**锛屼笉瑕佹暣涓€€鎺夆€斺€?            // 鑰佸笀鐨勫姩浣滃父甯告槸"鐐逛竴涓嬬湅鐪嬨€佸啀鎷?锛屼竴涓嬬偣鎺夋暣涓ā寮忓お鍑躲€?            CapMinX = CapMaxX = PointerX;
            CapMinY = CapMaxY = PointerY;
            _capDrag = CaptureDrag.None;
            Console.WriteLine("鎴浘锛氭病鎷栧嚭妗嗭紝缁х画鍙栨櫙");
            return;
        }

        CaptureAdjusting = true;
        _capDrag = CaptureDrag.None;
        Console.WriteLine($"鎴浘锛氭 {w}脳{h} 鐗╃悊鍍忕礌锛堟澗寮€鍚庡彲璋冩暣锛氭嫋杈?鎷栬/鎷栨暣浣擄紝"
                          + "Enter/鍙屽嚮 鎴栫偣銆屸湏銆嶅畬鎴愶紝Esc 鍙栨秷锛?);
    }

    /// <summary>
    /// **纭鎴浘**锛氳鍐荤粨甯?鈫?鍓创鏉?鈫?**鍘熶綅钀藉浘** 鈫?閫変腑銆佸垏鍥炴閫夈€?    /// 杩斿洖鏄惁鐪熺殑钀戒簡鍥俱€俙Enter` / 鐐广€屸湏 瀹屾垚銆嶉兘璧板畠銆?    /// </summary>
    internal bool ConfirmCapture()
    {
        if (!CaptureActive) return false;
        var s = CaptureFrameScreen();
        int x = (int)MathF.Round(s.MinX), y = (int)MathF.Round(s.MinY);
        int w = (int)MathF.Round(s.MaxX - s.MinX), h = (int)MathF.Round(s.MaxY - s.MinY);
        if (w <= 0 || h <= 0) { CancelCapture(); return false; }

        byte[] pixels = CaptureFrozen
            ? CropCaptureFrozen(x, y, w, h)
            : GrabCaptureNow(x, y, w, h);          // 鍏滃簳锛氬喕缁撴病鎶撳埌 鈫?鑰佽矾寰勭幇鎶?
        // 鐘舵€佸厛鏀跺共鍑€锛氶伄缃?妗?鍐荤粨甯х珛鍒绘秷澶憋紙灞忓箷涓?鍟?鍦板洖鍒板父鎬侊級
        CaptureActive = false;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        ReleaseCaptureFrozen();
        _dirty = true;

        if (pixels == null)
        {
            Console.WriteLine("鎴浘澶辫触锛氭姄灞忚繑鍥炵┖");
            return false;
        }

        ClipboardImage.SetImage(pixels, w, h);

        var img = ImageData.Adopt(w, h, pixels);
        if (img == null) return false;

        // **鍘熶綅钀?*锛?.3.0锛岀敤鎴峰畾鐨勶級锛氱敾甯冨潗鏍?= 灞忓箷鍧愭爣 鈭?鐩告満绾靛悜鍋忕Щ锛坸 涓嶆粴锛夈€?        // 澶硅繘瑙嗗彛銆佺暀涓€鍦堣竟璺濃€斺€?*杈硅窛鏄繀椤荤殑**锛氳创鐫€ (0,0) 鐨勮瘽閫変腑妗嗙殑鏃嬭浆鎵嬫焺浼氳窇鍒板睆骞曞銆?        //
        // 鈿?`scale = 1`锛?*1 鐗╃悊鍍忕礌 = 1 鐢诲竷鍗曚綅**锛堢敾甯冨潗鏍囧氨鏄墿鐞嗗儚绱狅紝瑙?ViewportCanvas
        //   涓?`--imagetest` 鐨勪笂灞忔帰閽堬級銆傛棫鐗堝啓鐨勬槸 `1f / DpiScale`锛岄偅鏄妸鍥?*缂╁皬 DpiScale 鍊?*鈥斺€?        //   200% 缂╂斁鐨勫睆涓婅创鍑烘潵鍙湁涓€鍗婂ぇ锛堢敤鎴?2026-09-30锛?鎴浘鎰熻涔熶笉瀵?锛岃繖鏉℃槸纭激锛夈€?        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        float px = Math.Clamp(x, vp.MinX + m, MathF.Max(vp.MinX + m, vp.MaxX - m - w));
        float py = Math.Clamp(y - ViewOffsetY, vp.MinY + m, MathF.Max(vp.MinY + m, vp.MaxY - m - h));
        var placed = Doc.AddImage(img, px, py, 1f);
        if (placed != null)
        {
            Doc.SelectOnly(new[] { placed });
            Tool = Tool.Marquee;             // 鍒氭埅瀹屽浘锛屾渶鍙兘鐨勪笅涓€浠朵簨鏄憜鏀惧畠
            NotifyUiStateChanged();
        }
        Console.WriteLine($"鎴浘 {w}脳{h} 鐗╃悊鍍忕礌 鈫?鍘熶綅钀藉湪鐢诲竷 ({px:F0},{py:F0})锛屽凡閫変腑");
        RegainKeyboardFocus();          // 鍥炲埌鎵规敞鎬侊細鍓嶅彴/閿洏瑕佸洖鏉ワ紙瑙侀偅娈垫敞閲婏級
        return placed != null;
    }

    /// <summary>
    /// 鍏滃簳锛氬喕缁撳抚涓嶅湪锛堟寜涓嬫椂鎶撳け璐ワ級鏃舵寜鑰佽矾寰?*鐜版姄**鈥斺€?    /// 闅愯棌鐣岄潰妯″紡钘忔暣涓鐩栧眰锛?0ms 鐨勯棯灏遍棯鍦ㄨ繖閲岋級锛岀洿鎺ユ埅鍙栧彧钘忔垜浠殑妗?鍏夋爣銆?    /// </summary>
    private byte[] GrabCaptureNow(int x, int y, int w, int h)
    {
        if (CaptureHideInk)
        {
            using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                return ScreenCapture.Grab(x, y, w, h);
        }
        CaptureFrameHidden = true;
        _dirty = true;
        RenderAll();
        Native.DwmFlush();
        var p = ScreenCapture.Grab(x, y, w, h);
        CaptureFrameHidden = false;
        _dirty = true;
        return p;
    }

    /// <summary>
    /// 鍙栨秷鎴浘锛圗sc / 鍙抽敭 / 銆屸湑 鍙栨秷銆? 鐐规澶栵級锛氫粈涔堥兘涓嶈惤锛屾妸鍐荤粨甯у拰鐘舵€佹敹骞插噣锛?    /// 骞朵笖**鍥炲埌杩涙埅鍥句箣鍓嶇敤鐨勫伐鍏?*锛?.3.1锛涗笉鍐嶆槸"鐣欏湪鎴浘宸ュ叿涓婄┖绛?锛夈€?    /// </summary>
    internal void CancelCapture()
    {
        if (!CaptureActive) return;
        CaptureActive = false;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capturePressHandled = false;
        ReleaseCaptureFrozen();
        var back = _toolBeforeCapture;
        if (back == Tool.Capture) back = Tool.Pen;      // 鍏滃簳锛屾甯歌蛋涓嶅埌
        SwitchTool(back);
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
        RegainKeyboardFocus();          // 鍥炲埌鎵规敞鎬侊細鍓嶅彴/閿洏瑕佸洖鏉ワ紙瑙侀偅娈垫敞閲婏級
        Console.WriteLine("鎴浘鍙栨秷");
    }

    /// <summary>銆屸湑 鍙栨秷銆嶉偅棰楁寜閽紙**鐢诲竷鍧愭爣**锛夛細寰呮満/鎷栨涓竴鐩存寕鍦ㄥ彸涓婅銆?/summary>
    internal RectF CaptureCancelRect()
    {
        float s = DpiScale;
        float w = CaptureBtnWLogical * s, h = CaptureBtnHLogical * s;
        var vp = ViewportCanvas;
        float pad = 16f * s;
        return new RectF
        {
            MinX = vp.MaxX - pad - w, MinY = vp.MinY + pad,
            MaxX = vp.MaxX - pad, MaxY = vp.MinY + pad + h,
        };
    }

    internal bool CaptureCancelHit(float x, float y) => CaptureCancelRect().Contains(x, y);

    /// <summary>璋冩暣闃舵鐨勪袱棰楁寜閽細銆屸湏 瀹屾垚銆嶃€屸湑 鍙栨秷銆嶃€傛寕鍦ㄦ鐨勫彸涓嬫柟锛岃创杈硅嚜鍔ㄧ炕闈€?/summary>
    internal void CaptureButtons(out RectF ok, out RectF cancel)
    {
        float w = CaptureBtnWLogical * DpiScale, h = CaptureBtnHLogical * DpiScale;
        float gap = CaptureBtnGapLogical * DpiScale;
        var vp = ViewportCanvas;
        float x0 = CapMaxX - (w * 2 + gap);
        x0 = Math.Clamp(x0, vp.MinX + 4f, MathF.Max(vp.MinX + 4f, vp.MaxX - (w * 2 + gap) - 4f));
        float y0 = CapMaxY + gap;
        if (y0 + h > vp.MaxY) y0 = CapMinY - gap - h;              // 涓嬫柟鏀句笉涓?鈫?鏀炬涓婃柟
        y0 = Math.Clamp(y0, vp.MinY + 4f, MathF.Max(vp.MinY + 4f, vp.MaxY - h - 4f));
        cancel = new RectF { MinX = x0, MinY = y0, MaxX = x0 + w, MaxY = y0 + h };
        ok = new RectF { MinX = x0 + w + gap, MinY = y0, MaxX = x0 + w + gap + w, MaxY = y0 + h };
    }

    /// <summary>鎸囬拡鍘嬪湪鍝鎸夐挳涓婏紙鉁?/ 鉁曪級鈥斺€斿厜鏍囧舰鐘惰鐢ㄣ€?/summary>
    internal bool CaptureButtonHit(float x, float y)
    {
        CaptureButtons(out var ok, out var cancel);
        return ok.Contains(x, y) || cancel.Contains(x, y);
    }

    /// <summary>璋冩暣闃舵 8 涓墜鏌勯噷锛屾寚閽堝帇鍦ㄥ摢涓笂锛堟病鏈?= None锛夈€傛嫋鍔ㄤ笌鍏夋爣鍏辩敤鍚屼竴濂楀垽鎹€?/summary>
    internal CaptureDrag CaptureHandleAt(float x, float y)
    {
        CaptureHandles(out var tl, out var t, out var tr, out var r,
                       out var br, out var b, out var bl, out var l);
        float hit = CaptureHandleHitLogical * DpiScale;
        if (Near(x, y, tl, hit)) return CaptureDrag.TL;
        if (Near(x, y, tr, hit)) return CaptureDrag.TR;
        if (Near(x, y, bl, hit)) return CaptureDrag.BL;
        if (Near(x, y, br, hit)) return CaptureDrag.BR;
        if (Near(x, y, l, hit)) return CaptureDrag.L;
        if (Near(x, y, r, hit)) return CaptureDrag.R;
        if (Near(x, y, t, hit)) return CaptureDrag.T;
        if (Near(x, y, b, hit)) return CaptureDrag.B;
        return CaptureDrag.None;
    }

    /// <summary>
    /// 鎴浘妯″紡涓嬬殑鎸変笅锛?.3.1锛夛細寰呮満 / 鎷栨 / 璋冩暣涓夌鐘舵€佸厛閮借繃杩欓噷銆?    /// 杩斿洖 true = 杩欎竴涓嬭鎴浘鍚冩帀锛涜繑鍥?false = 鐓у父璧板伐鍏峰垎娴侊紙涔熷氨鏄?浠庤繖閲岃捣妗?锛夈€?    /// </summary>
    private bool CapturePointerDown(float x, float y)
    {
        if (!CaptureAdjusting)
        {
            // 寰呮満/鎷栨涓細鍙充笂瑙掗偅棰椼€屸湑 鍙栨秷銆?            if (CaptureCancelHit(x, y)) { CancelCapture(); return true; }
            return false;                            // 鍏朵綑浜ょ粰 `case Tool.Capture` 璧锋
        }

        // ---- 璋冩暣闃舵 ----
        CaptureButtons(out var ok, out var cancel);
        if (ok.Contains(x, y)) { ConfirmCapture(); return true; }
        if (cancel.Contains(x, y)) { CancelCapture(); return true; }

        _capDragX = x; _capDragY = y;
        _capD0MinX = CapMinX; _capD0MinY = CapMinY; _capD0MaxX = CapMaxX; _capD0MaxY = CapMaxY;
        _capDrag = CaptureHandleAt(x, y);
        if (_capDrag == CaptureDrag.None)
        {
            bool inside = x >= CapMinX && x <= CapMaxX && y >= CapMinY && y <= CapMaxY;
            if (!inside) { CancelCapture(); return true; }   // 鐐规澶?= 鍙栨秷锛堝井淇?QQ 鐨勫彛寰勶級
            // 鍙屽嚮妗嗗唴 = 瀹屾垚锛堝井淇＄殑鎵嬫劅锛夛細400ms銆? 閫昏緫鍍忕礌浠ュ唴绠楀悓涓€澶?            double now = NowMs;
            bool dbl = now - _capLastClickMs < 400
                       && MathF.Abs(x - _capLastClickX) < 6f * DpiScale
                       && MathF.Abs(y - _capLastClickY) < 6f * DpiScale;
            _capLastClickMs = now; _capLastClickX = x; _capLastClickY = y;
            if (dbl) { ConfirmCapture(); return true; }
            _capDrag = CaptureDrag.Move;
        }
        _dirty = true;
        return true;
    }

    /// <summary>璋冩暣闃舵鐨?8 涓墜鏌勪腑蹇冿紙宸︿笂/涓婁腑/鍙充笂/鍙充腑/鍙充笅/涓嬩腑/宸︿笅/宸︿腑锛夈€?/summary>
    internal void CaptureHandles(out Vector2 tl, out Vector2 t, out Vector2 tr, out Vector2 r,
                                out Vector2 br, out Vector2 b, out Vector2 bl, out Vector2 l)
    {
        float cx = (CapMinX + CapMaxX) * 0.5f, cy = (CapMinY + CapMaxY) * 0.5f;
        tl = new Vector2(CapMinX, CapMinY); t = new Vector2(cx, CapMinY);
        tr = new Vector2(CapMaxX, CapMinY); r = new Vector2(CapMaxX, cy);
        br = new Vector2(CapMaxX, CapMaxY); b = new Vector2(cx, CapMaxY);
        bl = new Vector2(CapMinX, CapMaxY); l = new Vector2(CapMinX, cy);
    }

    private static bool Near(float x, float y, Vector2 p, float r)
        => MathF.Abs(x - p.X) <= r && MathF.Abs(y - p.Y) <= r;

    /// <summary>
    /// 璋冩暣闃舵鐨?*鏂瑰悜閿井璋?*锛?.3.1锛夛細鏂瑰悜閿?= 妗嗘暣浣撴尓 1 閫昏緫鍍忕礌锛堟姇褰变笂榧犳爣寰堥毦寰皟锛夛紝
    /// `Shift` + 鏂瑰悜閿?= 鏀瑰ぇ灏忥紙鍙充笅閭ｄ袱鏉¤竟锛夈€?    /// </summary>
    private void CaptureNudge(int vk)
    {
        if (!CaptureAdjusting) return;
        float s = 1f * DpiScale;
        float dx = vk == 0x25 ? -s : vk == 0x27 ? s : 0f;
        float dy = vk == 0x26 ? -s : vk == 0x28 ? s : 0f;
        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
        var vs = VirtualScreen;
        float minYc = vs.MinY - ViewOffsetY, maxYc = vs.MaxY - ViewOffsetY;
        float minSz = CaptureMinSizeLogical * DpiScale;
        if (shift)
        {
            CapMaxX = Math.Clamp(CapMaxX + dx, CapMinX + minSz, vs.MaxX);
            CapMaxY = Math.Clamp(CapMaxY + dy, CapMinY + minSz, maxYc);
        }
        else
        {
            float w = CapMaxX - CapMinX, h = CapMaxY - CapMinY;
            float l = Math.Clamp(CapMinX + dx, vs.MinX, vs.MaxX - w);
            float t = Math.Clamp(CapMinY + dy, minYc, maxYc - h);
            CapMinX = l; CapMaxX = l + w;
            CapMinY = t; CapMaxY = t + h;
        }
        _dirty = true;
    }

    /// <summary>璋冩暣闃舵鎷栧姩锛氭暣浣撶Щ / 鎷栨煇鏉¤竟 / 鎷栨煇涓锛屽す鍦ㄥ睆骞曢噷銆佷笉灏忎簬鏈€灏忓昂瀵搞€?/summary>
    private void CaptureAdjustMove(float x, float y)
    {
        if (_capDrag == CaptureDrag.None) return;
        float dx = x - _capDragX, dy = y - _capDragY;
        float minSz = CaptureMinSizeLogical * DpiScale;
        var vs = VirtualScreen;
        float minYc = vs.MinY - ViewOffsetY, maxYc = vs.MaxY - ViewOffsetY;

        float l = _capD0MinX, r = _capD0MaxX, t = _capD0MinY, b = _capD0MaxY;
        switch (_capDrag)
        {
            case CaptureDrag.Move: l += dx; r += dx; t += dy; b += dy; break;
            case CaptureDrag.L: l = MathF.Min(l + dx, r - minSz); break;
            case CaptureDrag.R: r = MathF.Max(r + dx, l + minSz); break;
            case CaptureDrag.T: t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.B: b = MathF.Max(b + dy, t + minSz); break;
            case CaptureDrag.TL:
                l = MathF.Min(l + dx, r - minSz); t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.TR:
                r = MathF.Max(r + dx, l + minSz); t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.BL:
                l = MathF.Min(l + dx, r - minSz); b = MathF.Max(b + dy, t + minSz); break;
            case CaptureDrag.BR:
                r = MathF.Max(r + dx, l + minSz); b = MathF.Max(b + dy, t + minSz); break;
        }
        // 澶瑰湪灞忓箷閲岋紱澶瑰畬灏哄鍙兘琚帇灏?鈫?鎸?鎷栫殑鏄摢鏉¤竟"鎶婂杈硅鍥炲幓锛屽畧浣忔渶灏忓昂瀵?        l = Math.Clamp(l, vs.MinX, vs.MaxX); r = Math.Clamp(r, vs.MinX, vs.MaxX);
        t = Math.Clamp(t, minYc, maxYc);   b = Math.Clamp(b, minYc, maxYc);
        if (r - l < minSz)
        {
            if (_capDrag is CaptureDrag.L or CaptureDrag.TL or CaptureDrag.BL) l = r - minSz;
            else r = l + minSz;
        }
        if (b - t < minSz)
        {
            if (_capDrag is CaptureDrag.T or CaptureDrag.TL or CaptureDrag.TR) t = b - minSz;
            else b = t + minSz;
        }
        CapMinX = l; CapMaxX = r; CapMinY = t; CapMaxY = b;
        _dirty = true;
    }

    /// <summary>璋冩暣鎷栧姩鏀跺熬锛堟姮鎵嬶級銆?/summary>
    private void EndCaptureAdjustDrag()
    {
        _capDrag = CaptureDrag.None;
        _dirty = true;
    }

    // =====================================================================
    //  瑙︽懜鎵嬪娍锛?.4.0锛夆€斺€旇鏍间笌璋冪爺瑙?璋冪爺-瑙︽懜鎵嬪娍-瀛︽牎澶у睆.md
    //
    //  鍒嗗伐锛歍ouch.cs 鍙洖绛?杩欎竴涓嬫槸浠€涔?锛堣Е鐐硅〃 + 骞插噣寮€濮嬬殑瑙掕壊鍒ゅ畾 + 闀挎寜璁℃椂锛夛紱
    //  杩欓噷鎸?_touchMode 璺敱鍔ㄤ綔锛氬啓瀛?/ 鎿?/ 婕父 / 缈婚〉 / 妗嗛€壜风偣閫?/ 閫変腑鍙樻崲銆?    // =====================================================================

    private readonly TouchGestures _touch = new();
    private TouchMode _touchMode = TouchMode.None;

    /// <summary>瑙︽懜閫夊嚭鏉ョ殑閫変腑锛?*涓存椂**鐨勶紙妗嗗拰鎿嶄綔鏉¤鏄剧ず锛屼絾宸ュ叿涓嶆崲锛夆€斺€斿拰 `_dwellSelected` 鍚屼竴璺€?/summary>
    private bool _touchSelected;

    private Vector2 _g2StartMid, _g2LastMid;   // 鍙屾寚锛氳捣鐐逛腑鐐?/ 涓婁竴甯т腑鐐?    private int _g2Axis;              // 0 鏈畾 / 1 妯紙缈婚〉锛? 2 绾碉紙婕父锛?    private uint _g2IdA, _g2IdB;      // 閽変綇鐨勪袱鏍规墜鎸囷紙2026-10-09锛氭墜鎺?鏉傝Е鐐逛笉鍙備笌锛? = 鏈拤锛?    private float _g2ScrollSm;        // 鍙屾寚婕父鐨勬粴鍔ㄩ噺骞虫粦锛堝帇"涓ゆ寚浜嬩欢浜ら敊"鐨勫崐鏍奸敮榻匡級
    private bool _g2Turned;           // 杩欎竴娆℃墜鍔垮凡缁忕炕杩囬〉锛堜竴娆℃墜鍔垮彧缈讳竴椤碉級
    private bool _g2Transform;        // 鏈夐€変腑锛氳繖涓€娆″弻鎸囨槸鍦ㄥ彉鎹㈠璞?    private bool _g2Tap;              // 涓ゆ寚鐐规寜鍊欓€夛紙鏉炬墜鏃剁粨绠楋級
    private float _g2Dist0;           // 鍙樻崲鐢細璧峰涓ゆ寚璺濈锛堝弻鎸囧彧鍋氱缉鏀撅紝缁曢€夊尯涓績锛?
    /// <summary>涓€鏍规墜鎸囨寜涓嬮偅涓€鍒荤殑閫夊尯蹇収銆傝繖涓€涓嬪彲鑳藉湪鍥惧舰宸ュ叿涓嬭"鐐规澶?= 鏀惰捣閫夊尯"
    /// 娓呮帀锛堣 <see cref="ClearSelectionForNewContext"/>锛夛紱浣嗗畠鑻ョ揣鎺ョ潃鍗囩骇鎴愬弻鎸囨墜鍔匡紝
    /// 閭ｄ釜"鐐规澶?鍏跺疄娌″彂鐢熲€斺€旀墜鍔胯捣鎵嬫椂鎶婇€夊尯鏁戝洖鏉?    ///锛?026-10-05 鐪熸満鍙嶉锛?閫変腑浠ュ悗锛屽弻鎸囨斁鍦ㄥ埆澶勮浆鍦堟€庝箞娌″弽搴?锛夈€?/summary>
    private readonly List<Stroke> _touchSelSnapshot = new();

    /// <summary>鍙屾寚涓婁笅婊戠殑婊氬姩绯绘暟锛氭崲绠楁垚閫昏緫鍍忕礌鍚庤蛋**鍜岄紶鏍囨粴杞?涓婁笅閿悓涓€涓?*
    /// `ScrollCanvasBy`銆?026-10-05 鐪熸満鍙嶉"1:1 婊戝緱涓嶅" 鈫?鏆傚畾 2 鍊嶏紱
    /// 瑙夊緱蹇簡/杩樻參锛屽彧璋冭繖涓€涓暟銆?/summary>
    private const float TouchScrollGain = 2f;
    private Vector2 _roamLast;        // 鍗曟寚婕父锛氫笂涓€甯т綅缃?    private uint _roamId;             // 鍗曟寚婕父锛氳鍝竴鏍规墜鎸囷紙鍏跺畠瑙︾偣鐨勭Щ鍔ㄤ笉璁稿紑鑸癸級
    /// <summary>闀挎寜璧峰ご鐨勯偅鏍规墜鎸囷紙妗嗛€?/ 鎷栧姩閫変腑锛夛細**鍙湁瀹?*鑳界敾妗?/ 鎷栦笢瑗裤€?    /// 鍒殑瑙︾偣锛堟帉鏍广€佸菇鐏佃Е鐐广€佺浜屾牴鎵嬫寚锛夌殑绉诲姩涓€寰嬩笉璁稿姭鎸佲€斺€旂湡鏈轰笂"鎷栫潃鎷栫潃
    /// 涓滆タ绐佺劧椋炲埌鎺屾牴搴曚笅"灏辨槸杩欓噷婕忓垽锛?026-10-05锛屽拰 `_roamId` 鍚屼竴鏉＄邯寰嬶級銆?/summary>
    private uint _selTouchId;
    private bool _radialTouchMode;    // 杞洏杩欐鏄Е灞忓懠鍑虹殑锛堟澗鎵嬫敼鎴愯Е鐐归┍鍔紱娌″垝鍔?鐣欏湪鐩樹笂锛?    private bool _radialSticky;       // 瑙﹀睆杞洏锛氭病鍒掑姩鏉炬墜鍚庣暀鍦ㄧ洏涓婄瓑鐐归€夛紙5s 瓒呮椂锛?
    /// <summary>璇昏Е鎽告墜鍔跨殑鏃嬮挳锛堝惎鍔ㄦ椂涓€娆★級銆?*鎬诲紑鍏?2026-10-09 鏅氬凡鎾?*
    /// 锛堢敤鎴风湡鏈鸿瘯绋筹細"寮€鍏虫嬁鎺夛紝榛樿寮€"锛夛紱鍙墿 `--notouch` 杩欎釜寮€鍙戝鐓э紙瑙佸弬鏁板潡锛夛紝
    /// 杩欓噷鎭掑紑銆?/summary>
    internal void LoadTouchPrefs()
    {
        _touch.Enabled = true;
        _touch.PalmErase = GetUiPref("touch.palm") != "0";
        _touch.ThreeFingerErase = GetUiPref("touch.three") != "0";
        _touch.LongPressSelect = GetUiPref("touch.longpress") != "0";
        _touch.TwoFingerTapSelect = GetUiPref("touch.2tap") != "0";
        _touch.SingleFingerRoam = GetUiPref("touch.roam") == "1";
        _touch.PalmFactor = GetUiPref("touch.palmLevel") switch
        {
            "0" => 4f,      // 淇濆畧
            "2" => 2f,      // 鐏垫晱
            _ => 3f,        // 鏍囧噯
        };
    }

    /// <summary>杩欏潡灞?鎶ヤ笉鎶ュ涓Е鐐?/ 鎶ヤ笉鎶ラ潰绉?鈥斺€斿彧缁欒缃〉鍜屾棩蹇楃敤锛屼笉褰卞搷鍒ゅ畾銆?/summary>
    internal (int maxTouches, bool sawArea) TouchCapability => (_touch.MaxSeen, _touch.SawArea);

    /// <summary>
    /// 瑙︽懜瑙︾偣钀戒笅銆傝繑鍥?true = 杩欎竴涓?*宸茬粡琚Е鎽稿眰鎺ユ帀**锛堣皟鐢ㄦ柟鐩存帴 return锛夛紱
    /// 杩斿洖 false = 鎸夋櫘閫氬啓瀛楅偅鏉¤矾璧帮紙鍗曟寚灏忛潰绉級銆?    /// </summary>
    private bool TouchDownDispatch(IntPtr hWnd, uint id, float x, float y, float sizePx,
                                   out bool stealPointer)
    {
        stealPointer = false;
        var v = _touch.Down(id, x, y, sizePx, NowMs, DpiScale);
        if (v == TouchVerdict.Write)
        {
            // 鍗曟寚杩欎竴涓嬩細璧?鏅€氬啓瀛楅偅鏉¤矾"锛堝彲鑳借瀹冩竻鎺夐€夊尯锛夆€斺€斿厛璁板揩鐓э紝
            // 鎵嬪娍鍗囩骇鏃惰嫢鍙戠幇閫夊尯娌′簡灏遍潬瀹冩晳鍥炴潵锛堣 BeginTouchTwoFinger锛夈€?            _touchSelSnapshot.Clear();
            if (Doc.Selected.Count > 0) _touchSelSnapshot.AddRange(Doc.Selected);
        }
        else if (v != TouchVerdict.Ignore)
        {
            // 鎵嬪娍瑕佹帴绠★細绗竴鏍规墜鎸囧彲鑳藉凡缁忚捣浜嗗埆鐨勫ご锛堟閫夊伐鍏蜂笅閭ｄ釜杩樻病鎷栫殑妗嗭紝
            // 浠ュ強鍥惧舰宸ュ叿涓?鎸夋澶栭『鎵嬫敹璧烽€夊尯"锛夆€斺€斿彧鑳芥挙锛屼笉鑳?apply銆?            CancelTouchPressMarquee();
        }
        switch (v)
        {
            case TouchVerdict.Ignore:
                return true;                       // 鍚冩帀锛氫笉鎶㈡鍦ㄥ啓鐨勯偅涓€绗斻€佷篃涓嶈惤澧?
            case TouchVerdict.Erase:
                stealPointer = true;
                // 鍙兘鏄?涓ゆ寚鍙樻崲杩涜涓紝绗笁鏍规墜鎸囪惤涓嬫潵鍙樻垚鎿?锛氬厛鎶婂彉鎹㈡敹璐?                //锛堜竴鐐规病鍔ㄥ氨涓嶈繘鎾ら攢鏍堬級锛屼笉鐒?SelDragging 浼氫竴鐩存寕鐫€锛堟澗鎵嬪悗鍏ㄥ彉鎴愭嫋鍔級銆?                if (SelDragging) EndSelDrag();
                _g2Transform = false;
                CancelTouchStroke();
                _touchMode = TouchMode.Erase;
                _drawing = true;
                Native.SetCapture(hWnd);
                GcLatency.Enter();
                BeginStrokeMeasure();
                // **璺熼紶鏍囧悓涓€鏉¤矾**锛?026-10-05 鐢ㄦ埛鍙ｅ緞锛夛細鎵规鎸夊綋鍓嶉偅鎶婃鐨紑
                //锛堟暣绗旀摝 = BeginErase銆侀潰绉摝 = BeginEraseRect锛夛紝涓嬮潰鐨?TouchEraseSample 鍚屾簮銆?                // 鍏?EndErase 鍏滃簳锛氬鏋滅涓€鏍规墜鎸囨槸**姗＄毊宸ュ叿**鎸変笅鏉ョ殑锛堥偅涓€璺嚜宸卞紑浜嗘壒娆★級锛?                // 杩欓噷鍏堟妸瀹冩敹璐︼紝鍏嶅緱鎵规琚《鎺夛紙娌℃湁鎵瑰湪寮€鏃?EndErase 鏄┖鎿嶄綔锛夈€?                Doc.EndErase();
                if (_eraserKind == Tool.PixelEraser)
                {
                    Doc.BeginEraseRect();
                    // **鍔ㄦ€佹鐨?*锛氬拰榧犳爣鎸変笅鍚屼竴濂楋紙Reset 鈫?绉诲姩涓斁澶э級銆?                    // 鐪熸満鍙嶉"涓夋寚姗＄毊娌℃湁鍔ㄦ€佸ぇ灏?灏辨槸杩欓噷娌℃帴涓婏紙PixelEraseDragging 涓€鐩存槸 false锛夈€?                    ResetDynamicEraser();
                    PixelEraseDragging = true;
                    if (TouchEraseCenter(out var c0)) { _lastEraseX = c0.X; _lastEraseY = c0.Y; }
                }
                else Doc.BeginErase();
                // **鎵嬫帉鎸変笅鍏堜笉鎿?*锛堢Щ鍔ㄦ墠鎿︼級锛氭墜鎺岃惤鍦ㄥ睆涓婁笉鍔ㄦ槸"鎵嬫墭鐫€"锛屼笉璇ョ洿鎺ユ妸涓嬮潰
                // 鐨勬澘涔︽摝鎺夛紱涓夋寚鎿︾収鏃ф寜涓嬪嵆鎿︼紙閭ｆ槸涓诲姩鍔ㄤ綔锛夈€傝 TouchGestures 鐨勬敞閲娿€?                if (!_touch.LastDownWasPalm) TouchEraseSample();
                Console.WriteLine(_touch.LastDownWasPalm ? "瑙︽懜锛氭墜鎺?鈫?鎿﹂櫎锛堢Щ鍔ㄦ墠鎿︼級" : "瑙︽懜锛氫笁鎸?鈫?鎿﹂櫎");
                return true;

            case TouchVerdict.Gesture2:
                stealPointer = true;
                // 浠?鎿?鍒囧埌鎵嬪娍锛氬厛鎶婅繖涓€杞摝闄ゆ敹璐︼紙涓嶇劧 BeginEraseRect 涓€鐩存寕鐫€锛夈€?                if (_touchMode == TouchMode.Erase) { Doc.EndErase(); EndStrokeMeasure(); PixelEraseDragging = false; }
                CancelTouchStroke();
                _touchMode = TouchMode.Gesture2;
                _drawing = true;
                Native.SetCapture(hWnd);
                // **涓ゆ寚鎵嬪娍涔熻璧?40ms 蹇冭烦**锛歚TouchGestures.Tick` 閲岀殑"涓ゆ寚闀挎寜=鍛煎嚭鐩?
                // 瑕侀潬瀹冿紙鍐欏瓧閭ｉ瀹氭椂鍣ㄥ彧鍦ㄥ崟鎸囧啓瀛楃殑璺笂鍚姩锛涙紡浜嗚繖閲?闀挎寜姘歌繙涓嶅垽锛夈€?                StartDwellTimer();
                BeginTouchTwoFinger();
                return true;

            case TouchVerdict.Roam:
                stealPointer = true;
                CancelTouchStroke();
                _touchMode = TouchMode.Roam;
                _drawing = true;
                Native.SetCapture(hWnd);
                _roamId = id;
                _roamLast = new Vector2(x, y);
                Console.WriteLine("瑙︽懜锛氬崟鎸囨极娓?);
                return true;

            default:
                return false;                      // Write锛氳蛋鍘熸潵鐨勫啓瀛楅偅鏉¤矾
        }
    }

    /// <summary>鎾ゆ帀绗竴鏍规墜鎸囧湪妗嗛€夊伐鍏蜂笅璧风殑閭ｄ竴涓?杩樻病鎷栫殑妗?锛堟墜鍔挎帴绠℃椂鐢紱涓嶈兘 apply锛夈€?    /// 涓嶆挙鐨勮瘽锛氭棆杞?鎿﹂櫎鏈熼棿妗嗚繕鐢诲湪灞忓箷涓婏紝鎵嬪娍缁撴潫鍚庡畠涔熶笉璧帮紙"鏉炬墜浜嗘杩樺湪"锛夈€?/summary>
    private void CancelTouchPressMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>瑙︽懜瑙︾偣绉诲姩锛堟寜褰撳墠妯″紡璺敱锛夈€傝繑鍥?true = 宸茬粡澶勭悊瀹岋紙璋冪敤鏂圭洿鎺?return锛夈€?/summary>
    private bool TouchMoveDispatch(uint id, float x, float y)
    {
        if (_touchMode == TouchMode.None) return false;
        _touch.Move(id, x, y, DpiScale);

        switch (_touchMode)
        {
            case TouchMode.Write:
                return false;                      // 鍐欏瓧锛氳蛋鍘熸潵鐨勮矾锛堥暱鎸夌敱瀹氭椂鍣ㄥ垽锛?
            case TouchMode.Erase:
                TouchEraseSample();
                return true;

            case TouchMode.Gesture2:
                TouchGestureMove();
                return true;

            case TouchMode.Marquee:
                if (id != _selTouchId) return true;   // 鍙湁"闀挎寜璧峰ご閭ｆ牴鎵嬫寚"鑳界敾妗嗭紙鎺屾牴/鍒殑瑙︾偣涓嶇畻锛?                ExtendMarqueeTo(x, y);
                _dirty = true;
                return true;

            case TouchMode.SelDrag:
                if (id != _selTouchId) return true;   // 鍚岀悊锛氬埆鐨勮Е鐐逛笉璁告妸閫変腑鐨勪笢瑗挎嫿璧?                UpdateSelDrag(x, y);
                return true;

            case TouchMode.Radial:
            {
                // 杞洏寮€鐫€锛氭柟鍚?= **褰撳墠瑙︾偣鐨勫钩鍧囦綅**锛堜竴鏍规墜鎸囧厛鎶篃涓嶆柇锛涚‘璁ゅ湪鏉炬墜閭ｄ竴鍒伙級銆?                var views = _touch.Views;
                if (views.Count == 0) return true;
                float mx = 0, my = 0;
                foreach (var v in views) { mx += v.Pos.X; my += v.Pos.Y; }
                PointerX = mx / views.Count;
                PointerY = my / views.Count;
                UpdateRadialSelection();
                _dirty = true;
                return true;
            }

            case TouchMode.Roam:
            {
                // 鍙**寮€濮嬫极娓哥殑閭ｆ牴鎵嬫寚**锛氬睆骞曚笂鎶ユ潵鐨勫叾瀹冭Е鐐癸紙娈嬬偣 / 鎺屾牴 / 璇Е锛?                // 鐨勭Щ鍔ㄤ笉璁稿紑鑸光€斺€斿悎鎴愭敞鍏ラ噷瀹炴祴鍒?娈嬬暀瑙︾偣鍦?(1440,1200) 鍙嶅鍙戠Щ鍔紝
                // 鎶婄浉鏈烘嫿寰椾贡璺?锛涚湡鏈轰笂鍚岀悊锛堝鎶ョ殑瑙︾偣涓嶈褰卞搷婕父锛夈€?                if (id != _roamId) return true;
                var p = new Vector2(x, y);
                float dy = p.Y - _roamLast.Y;
                _roamLast = p;
                ScrollCanvasBy(dy / DpiScale);      // 鍜屾粴杞?涓婁笅閿悓涓€涓嚱鏁帮紙1:1 閫昏緫鍍忕礌锛?                return true;
            }
        }
        return false;
    }

    /// <summary>瑙︽懜瑙︾偣鎶捣锛堟寜褰撳墠妯″紡鏀跺熬锛夈€傝繑鍥?true = 璋冪敤鏂圭洿鎺?return锛堝啓瀛楅偅鏉￠櫎澶栵級銆?/summary>
    private bool TouchUpDispatch(uint id)
    {
        // 琛ㄩ噷鐨勮Е鐐?*姘歌繙瑕佹竻**锛堝摢鎬曡繖涓€鍒绘病鏈夋墜鍔垮湪璺戯級锛氫笂闈?鍏堝杺琛?閭ｆ潯璺?        // 鍙兘鎶婁竴鏍规墜鎸囩暀鍦ㄨ〃閲岋紝婕忔竻浼氳涓嬩竴杞殑"骞插噣寮€濮?璁℃暟閿欎贡銆?        if (_touchMode == TouchMode.None) { _touch.Up(id); return false; }
        bool normalStroke = _touchMode == TouchMode.Write;

        // 涓ゆ寚鐐规寜瑕佸湪"鍑忔帀杩欎竴鏍逛箣鍓?鍒わ紙鍒ゆ嵁瑕佹眰涓や釜瑙︾偣閮借繕鍦級
        if (_touchMode == TouchMode.Gesture2 && _touch.TwoFingerTap(NowMs, DpiScale, out _))
            _g2Tap = true;

        switch (_touchMode)
        {
            case TouchMode.Erase:
                // **鏈€鍚庝竴鏍?鐪熷湪琛ㄩ噷"鐨勬墜鎸囨姮璧锋墠鏀惰处**锛氫笁鎸囨摝涓€旀澗 1~2 鏍规墜鎸囷紝浼氳瘽瑕佺户缁摝
                //锛?026-10-05 鐪熸満鍙嶉锛?涓夋寚浼氬彉澶э紝浣嗗墿涓€鏍规墜鎸囨帴鐫€鎿︽椂灏变笉鍙樹簡"鈥斺€?                //  浠ュ墠姣忔姮涓€鏍瑰氨 EndErase + 鍏冲姩鎬侊細灏哄鍐诲湪鍩哄噯銆佹壒娆′篃纰庢垚涓€姝ヤ竴娆℃挙閿€锛夈€?                // `Contains` 鐢ㄦ潵鎸?骞界伒 up"锛堝悎鎴愭敞鍏ヤ細琛ュ彂宸叉姮璧疯Е鐐圭殑 up锛涚湡鏈轰涪 up 鍚庡悓鐞嗭級锛?                // 鍙湁褰撳墠杩樺湪琛ㄩ噷鐨勯偅娆℃姮璧锋墠绠楁暟锛屾敹璐﹀洜姝?*鎭板ソ涓€娆?*銆?                if (_touch.Contains(id) && _touch.Count <= 1)
                {
                    Doc.EndErase();             // 涓€娆℃摝闄?= 涓€姝ユ挙閿€
                    EndStrokeMeasure();
                    PixelEraseDragging = false; // 闈㈢Н鎿﹀洖鍩哄噯锛堝姩鎬佸ぇ灏忓埆娈嬬暀锛?                }
                break;

            case TouchMode.Gesture2:
                TouchGestureReleased();
                break;

            case TouchMode.Radial:
                // 鏈€鍚庝竴鏍规墜鎸囨姮璧?= 纭锛涙病鍒掑姩灏辨澗鎵?鈫?"鐣欏湪鐩樹笂绛夌偣閫?锛堣 CommitRadialPalette锛夈€?                if (_touch.Count <= 1) CommitRadialPalette();
                break;

            case TouchMode.Marquee:
                // 鍙湁"璧峰ご閭ｆ牴鎵嬫寚"鎶墜鎵嶇粨绠楋紱鍒殑瑙︾偣鎶墜涓嶇粨绠楋紙妗嗚繕鍦ㄧ敾锛?                if (id == _selTouchId)
                {
                    ApplyMarquee();             // 鏉炬墜鍑洪€夊尯
                    _touchSelected = Doc.Selected.Count > 0;
                }
                break;

            case TouchMode.SelDrag:
                if (id == _selTouchId)          // 鍚屼笂锛氬埆鐨勮Е鐐规姮鎵嬩笉璁告妸鎷栧姩鎻愬墠鎻愪氦鎺?                {
                    EndSelDrag();               // 鎻愪氦锛堜竴姝ユ挙閿€锛涗竴鐐规病鍔ㄥ氨涓嶈繘鎾ら攢鏍堬級
                    _touchSelected = Doc.Selected.Count > 0;
                }
                break;
        }

        _touch.Up(id);

        if (_touch.Any) return !normalStroke;   // 杩樻湁鎵嬫寚鎸夌潃锛氭墜鍔跨户缁?
        _touchMode = TouchMode.None;
        _touch.Reset();
        _touchSelSnapshot.Clear();
        _g2Transform = false;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2IdA = _g2IdB = 0;               // 鎵嬪娍鏀跺満锛氶拤瀛愭斁鎺夛紙涓嬩竴杞噸鏂伴拤锛?        _g2ScrollSm = 0f;
        StopDwellTimer();
        _dirty = true;
        return !normalStroke;                   // 鍐欏瓧閭ｆ潯锛氭竻瀹岃Е鐐瑰悗鐓у父璧?EndStroke
    }

    /// <summary>涓柇瑙︽懜鎵嬪娍锛堜涪鎹曡幏 / 鎹㈣澶?/ 鎰忓璺緞锛夛細鎶婂紑浜嗗ご鐨勯兘鏀跺共鍑€銆?/summary>
    private void TouchAbort()
    {
        if (_touchMode == TouchMode.None && !_touch.Any) return;
        if (_touchMode == TouchMode.Erase) { Doc.EndErase(); EndStrokeMeasure(); PixelEraseDragging = false; }
        // 瑙﹀睆鍛煎嚭鐨勮疆鐩橈細鎵嬪娍涓柇 = 鐩樹篃鏀舵帀锛堝埆鐣欎竴涓病浜虹鐨勭洏鍦ㄥ睆骞曚笂锛夈€?        if (RadialPaletteActive && _radialTouchMode) CancelRadialPalette("鎵嬪娍涓柇");
        // 鈿?**閫夋嫨鎷栧姩 / 妗嗛€変笉鍦ㄨ繖閲岀粨鏉?*锛?026-09-30 瀹炴祴锛夛細瑙︽懜闀挎寜涔嬪悗锛?        // 绯荤粺鍋跺皵浼氬湪"鎸変綇涓嶅姩"鐨勬煇涓€鍒诲彂涓€娆′涪鎹曡幏锛屾妸鎷栧姩鎻愬墠 `EndSelDrag()` 鎺夆€斺€?        // 鐥囩姸灏辨槸"闀挎寜閫変腑浜嗗璞★紝浣嗘嫋涓嶅姩"锛堢瑪鐢昏鏁板嵈娑ㄤ簡锛氶偅涓€鎷栬褰撴垚鎺ョ潃鍐欏瓧锛夈€?        // 鐣欑粰鏉炬墜閭ｆ潯璺紙`EndStroke` 閲岀殑 `if (SelDragging) EndSelDrag()`锛夊幓鏀讹細
        // 瀹冩墠鏄湡鐨勬姮鎵嬫椂鍒伙紝鑰屼笖瀵归紶鏍囬偅鏉¤矾鏈潵灏辨槸鍚屼竴鏉°€?        _touch.Reset();
        _touchSelSnapshot.Clear();
        _touchMode = TouchMode.None;
        _g2Transform = false;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2IdA = _g2IdB = 0;               // 鎵嬪娍涓柇锛氶拤瀛愪竴骞舵斁鎺?        _g2ScrollSm = 0f;
        Console.WriteLine("瑙︽懜鎵嬪娍涓柇锛堜涪鎹曡幏/鎹㈣澶囷級");
        _dirty = true;
    }

    /// <summary>闀挎寜鎴愮珛锛?0ms 蹇冭烦椹卞姩锛夛細鎾ゆ帀閭ｄ竴灏忕瑪锛岃繘"鐐归€?/ 妗嗛€?銆?/summary>
    private void TouchLongPressFire()
    {
        if (_touchMode != TouchMode.Write) return;
        var c = _touch.Views.Count > 0 ? _touch.Views[0] : default;
        float x = c.Pos.X, y = c.Pos.Y;
        _selTouchId = c.Id;              // 杩欎竴鏍规槸"妗嗛€?/ 鎷栧姩"鐨勮捣澶存墜鎸囷紙瑙?_selTouchId锛?
        CancelTouchStroke();

        // 鍏堥棶"鎵嬫寚涓嬮潰鏈夋病鏈変笢瑗?锛堢偣閫夛細閫変腑骞剁洿鎺ヨ繘鍏ユ嫋鍔級锛?        // 娌′笢瑗?鈫?妗嗛€夛紙鏉炬墜鍑洪€夊尯锛夈€?        if (TryBeginSelectionGesture(x, y, shift: false, alt: false))
        {
            _touchSelected = Doc.Selected.Count > 0;
            _touchMode = TouchMode.SelDrag;
        }
        else
        {
            BeginMarqueeAt(x, y, shift: false, alt: false);
            _touchMode = TouchMode.Marquee;
        }
        _touch.ClearLongPress();
        _dirty = true;
    }

    /// <summary>鎾ゆ帀"鍒氳捣澶寸殑閭ｄ竴灏忕瑪"锛堜笉杩涙枃妗ｃ€佷笉鐣欐挙閿€锛夈€?/summary>
    private void CancelTouchStroke()
    {
        if (ActiveStroke != null)
        {
            Doc.Dirty.Add(ActiveStroke.PaddedBounds);
            ActiveStroke = null;
        }
        // [闅忚€侀娴嬬郴缁熷垹闄?2026-10-05] 杩欓噷鍘熸潵鏄?`ClearRenderTail(); ActiveStrokeOnTrail = false;`
        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        StopDwellTimer();
        _dirty = true;
    }

    /// <summary>涓夋寚 / 鎵嬫帉鎿︿細璇濇鍦ㄨ窇锛圤verlay 鐨勮惤鐐瑰弽棣堜笌鑴忓尯閮借瀹冿級銆?/summary>
    internal bool TouchEraseSessionActive => _touchMode == TouchMode.Erase;

    /// <summary>杩欐鎿﹂櫎浼氳瘽鐢ㄧ殑鏄笉鏄潰绉摝锛堣窡闅忕敤鎴峰綋鍓嶉偅鎶婃鐨紝鍜岄紶鏍囧悓婧愶級銆?/summary>
    internal bool TouchEraseSessionPixel => _eraserKind == Tool.PixelEraser;

    /// <summary>鎿﹂櫎浼氳瘽閲岀殑娲昏Е鐐癸紙鐢昏惤鐐瑰弽棣堢敤锛涙瘡娆＄幇鍙栵紝涓嶇紦瀛樷€斺€旈伩鍏?鍑虹幇涓嶆秷澶?锛夈€?/summary>
    internal IReadOnlyList<TouchGestures.ContactView> TouchViewsLive => _touch.Views;

    /// <summary>鎿﹂櫎鎵嬪娍鐨勮惤鐐癸細**鎵€鏈夎Е鐐圭殑涓績**锛堟病鏈夎Е鐐硅繑鍥?false锛夈€傛摝闄ゃ€佽惤鐐瑰弽棣堛€?    /// 鑴忓尯涓夊鍏辩敤杩欎竴涓偣锛屼繚璇?鐪嬭鐨?= 鎿︽帀鐨?姘歌繙鏄悓涓€涓綅缃€?/summary>
    internal bool TouchEraseCenter(out Vector2 c)
    {
        var list = _touch.Views;
        if (list.Count == 0) { c = default; return false; }
        float sx = 0f, sy = 0f;
        for (int i = 0; i < list.Count; i++) { sx += list[i].Pos.X; sy += list[i].Pos.Y; }
        c = new Vector2(sx / list.Count, sy / list.Count);
        return true;
    }

    /// <summary>鎿︿竴涓嬶細**鏁翠釜鎵嬪娍鍙敤涓€涓?榧犳爣姗＄毊"**锛岃惤鐐?= 鎵€鏈夎Е鐐圭殑涓績
    ///锛?026-10-05 鐪熸満鍙嶉锛?涓変釜鎸囧ご鍑烘潵涓変釜姗＄毊鎿?鈥斺€斾竴鍙墜搴旇鏄竴涓鐨紝鍜岄紶鏍囧悓婧愶級銆?    /// 鏁寸瑪鎿?= `EraseAt(涓績, EraserRadius)`锛堢鍒板氨鏁存潯鍒狅級锛?    /// 闈㈢Н鎿?= `EraseRectAt(涓績, 榧犳爣钀界偣妗嗗崐瀹?鍗婇珮)`鈥斺€斿昂瀵稿拰榧犳爣鎸変笅閭ｄ竴浠藉畬鍏ㄤ竴鑷达紝
    /// 鑰屼笖**鍔ㄦ€佹鐨収鏃?*锛氬杺閫熷害 鈫?蹇壂鍙樺ぇ锛堝拰榧犳爣 `EraseRectAlongPath` 鍚屼竴濂楋級銆?/summary>
    private void TouchEraseSample()
    {
        if (!TouchEraseCenter(out var c)) return;
        if (_eraserKind == Tool.PixelEraser)
        {
            UpdateDynamicEraser(c.X, c.Y);       // 鍔ㄦ€佹鐨細寮€鍏冲叧鎺夋椂瀹冭嚜宸辨妸绯绘暟褰?1
            _lastEraseX = c.X; _lastEraseY = c.Y;
            Doc.EraseRectAt(c.X, c.Y, PixelEraserCursorHalfWidthPx, PixelEraserCursorHalfHeightPx);
        }
        else
        {
            _lastEraseX = c.X; _lastEraseY = c.Y;
            Doc.EraseAt(c.X, c.Y, EraserRadius);
        }
        _dirty = true;
    }

    /// <summary>鍙屾寚寮€濮嬶細鏈夐€変腑 鈫?鍊?鏁翠綋鎷栧姩"閭ｅ锛坉etach + 棰勮鐭╅樀 + 鏉炬墜涓€姝ユ挙閿€锛夛紱娌￠€変腑 鈫?绛夋柟鍚戙€?/summary>
    private void BeginTouchTwoFinger()
    {
        if (!_touch.TryPair(out var p)) return;

        // 鈿?2026-10-09锛堢湡鏈?鍙屾寚涓婁笅婊戝槦鍢熸姈"鐨勬牴淇級锛?*鎶婅繖涓ゆ牴鎵嬫寚鐨?id 閽変綇**锛?        // 杩欎竴杞墜鍔垮叏绋嬪彧璁ゅ畠浠紙TouchGestureMove 鐢?id 鐗?TryPair锛夈€?        // 涔嬪墠鐨勮娉?琛ㄩ噷鍓嶄袱涓?浼氳涓ょ被浜嬫悈涔扁€斺€斺憼 鎺屾牴/鏉傝Е鐐规贩杩涜〃锛?        // 鈶?鎵嬫寚鎶?钀戒竴涓嬭閲嶆姤銆佽拷鍔犲埌琛ㄥ熬鈥斺€?鍓嶄袱涓?鎹汉锛屼腑鐐规潵鍥炶烦銆?        // 閲嶅叆淇濇姢锛氶拤瀛愯繕娲荤潃灏变笉閲嶇疆锛堟墜鎸?blip 閲嶈惤浼氬啀璧板埌杩欓噷锛屼笉鑳芥妸
        // _g2StartMid / 杞撮攣 閲嶆柊娲椾竴閬嶏級銆?        if (_g2IdA != 0 && _touch.Contains(_g2IdA) && _touch.Contains(_g2IdB)) return;
        _g2IdA = p.IdA;
        _g2IdB = p.IdB;
        _g2ScrollSm = 0f;

        _g2StartMid = _g2LastMid = (p.A + p.B) * 0.5f;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2Transform = false;

        // 绗竴鏍规墜鎸囨寜涓嬫椂鍙兘鎶婇€夊尯娓呮帀浜嗭紙鍥惧舰宸ュ叿"鐐规澶?= 鏀惰捣"锛岃 ClearSelectionForNewContext锛?        // 鈥斺€旇繖涓€涓嬬揣鎺ョ潃鍗囩骇鎴愬弻鎸囨墜鍔匡紝璇存槑閭ｄ釜"鐐规澶?鏍规湰娌″彂鐢熴€傛妸閫夊尯鏁戝洖鏉ワ紝
        // 鎵嬪娍鐓у父鍙樻崲锛?026-10-05 鐪熸満鍙嶉锛?閫変腑浠ュ悗鍙屾寚鏀惧埆澶勮浆鍦堟€庝箞娌″弽搴?锛夈€?        if (Doc.Selected.Count == 0 && _touchSelSnapshot.Count > 0)
        {
            foreach (var st in _touchSelSnapshot)
                if (Doc.Strokes.Contains(st)) Doc.Selected.Add(st);
            if (Doc.Selected.Count > 0)
            {
                _touchSelected = true;
                _dirty = true;
            }
        }
        _touchSelSnapshot.Clear();

        if (Doc.Selected.Count == 0) return;

        // 鏈夐€変腑锛氬湪**閫夊尯涓績**璧蜂竴娆?鏁翠綋鎷栧姩"銆傚€熺幇鎴愰偅濂楃殑濂藉锛?        // 棰勮璧?_selDragMatrix銆佹澗鎵?EndSelDrag 鎻愪氦锛堜竴姝ユ挙閿€锛夈€侀攣瀹氬璞¤嚜鍔ㄦ憳鎺夈€?        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        if (TryBeginSelectionGesture((aabb.MinX + aabb.MaxX) * 0.5f,
                                     (aabb.MinY + aabb.MaxY) * 0.5f, shift: false, alt: false))
        {
            _g2Transform = true;
            _g2Dist0 = MathF.Max(1f, Vector2.Distance(p.A, p.B));
            // 鏃嬭浆璇绘暟褰掍綅锛氭棆杞凡缁忓彧璧?鎷栨棆杞墜鏌?閭ｆ潯璺紝鍙屾寚涓嶅啀浜х敓搴︽暟銆?            SelRotationSnapped = false;
            SelRotationReadsPose = false;
            SelRotationReadsInclination = false;
            _touchSelected = true;
            Console.WriteLine("瑙︽懜锛氬弻鎸?鈫?缂╂斁閫変腑瀵硅薄锛堟棆杞敤鏃嬭浆鎵嬫焺銆佺Щ鍔ㄧ敤鍗曟寚鎷栵級");
        }
    }

    /// <summary>鍙屾寚绉诲姩锛氭湁閫変腑 鈫?鍙樻崲瀵硅薄锛涙病閫変腑 鈫?绾垫粦婕父 / 妯粦缈婚〉锛堟柟鍚戦攣 + 涓€娆′竴椤碉級銆?/summary>
    private void TouchGestureMove()
    {
        // **鍙閽変綇鐨勯偅涓ゆ牴**锛?026-10-09锛夛細鎵嬫帉銆佹潅瑙︾偣鍦ㄨ〃閲屼篃褰撴病鐪嬭銆?        if (!_touch.TryPair(_g2IdA, _g2IdB, out var p)) return;
        var mid = (p.A + p.B) * 0.5f;

        if (_g2Transform)
        {
            // 缁?*閫夊尯涓績**缂╂斁锛堜笉甯﹀钩绉汇€佷笉甯︽棆杞級銆?            // 2026-10-05 鐢ㄦ埛鍙ｅ緞锛堢浜岃疆瀹氱锛夛細鍙屾寚**鍙缂╂斁**锛涚Щ鍔ㄨ蛋鍗曟寚鎷栥€?            // 鏃嬭浆璧?*鎷栨棆杞墜鏌?*鈥斺€斾笁鏉¤矾鍚勭涓€浠朵簨锛屼簰鐩镐笉鎵撴灦锛堝拰榧犳爣閭ｈ竟涓€涓€瀵瑰簲锛夈€?            var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var center = new Vector2((aabb.MinX + aabb.MaxX) * 0.5f, (aabb.MinY + aabb.MaxY) * 0.5f);
            float d1 = Vector2.Distance(p.A, p.B);
            float scale = Math.Clamp(d1 / _g2Dist0, 0.1f, 10f);

            _selDragMatrix = Matrix3x2.CreateTranslation(-center.X, -center.Y)
                           * Matrix3x2.CreateScale(scale)
                           * Matrix3x2.CreateTranslation(center);
            _selDragMoved = true;
            _dirty = true;
            return;
        }

        float dx = mid.X - _g2LastMid.X, dy = mid.Y - _g2LastMid.Y;
        _g2LastMid = mid;

        if (_g2Axis == 0)
        {
            var total = mid - _g2StartMid;
            float lockPx = TouchGestures.DirLockLogical * DpiScale;
            if (total.Length() < lockPx) return;
            if (MathF.Abs(total.X) >= MathF.Abs(total.Y) * TouchGestures.DirRatio) _g2Axis = 1;
            else if (MathF.Abs(total.Y) >= MathF.Abs(total.X) * TouchGestures.DirRatio) _g2Axis = 2;
            else return;                       // 鏂滅潃锛氬厛涓嶅姩锛岀瓑涓绘柟鍚戞槑纭?        }

        if (_g2Axis == 2)
        {
            // 涓婁笅 = 婕父锛氳蛋**榧犳爣婊氳疆/涓婁笅閿悓涓€涓?* ScrollCanvasBy锛堜笉鍐嶈嚜宸卞啓鐩告満鏁板锛夈€?            // 绯绘暟瑙?TouchScrollGain锛堢湡鏈哄弽棣?1:1 涓嶅锛屾殏瀹?2 鍊嶏級銆?            // **2026-10-09 浜岃疆鍙屼繚闄?*锛堢敤鎴峰娴嬩粛鎶?鎶栧緱鍘夊"锛夛細
            //   鈶?涓ゆ寚鐨勪簨浠朵氦閿欏埌杈炬椂锛屼腑鐐逛細鍛?鍗婃牸閿娇"鈥斺€旇交 EMA 鎶瑰钩锛?            //   鈶?鍗曚簨浠跺紓甯稿ぇ璺筹紙涓㈠抚閲嶆斁 / 鎺岀紭韫叆锛夐檺骞咃紝鍒鐢婚潰鐚涜烦銆?            _g2ScrollSm += (dy - _g2ScrollSm) * 0.55f;
            float lim = 90f * DpiScale;
            float use = Math.Clamp(_g2ScrollSm, -lim, lim);
            ScrollCanvasBy(use * TouchScrollGain / DpiScale);
            return;
        }

        if (_g2Turned || dx == 0f) return;
        float totalX = mid.X - _g2StartMid.X;
        if (MathF.Abs(totalX) < TouchGestures.PageTurnLogical * DpiScale) return;
        _g2Turned = true;                      // 涓€娆℃墜鍔垮彧缈讳竴椤碉紙鎶墜鍐嶆粦鎵嶆槸涓嬩竴椤碉級
        bool next = totalX < 0;                // 寰€宸︽粦 = 涓嬩竴椤?        if (PptMode)
        {
            if (next) PptNextFromUi(); else PptPrevFromUi();
        }
        else FlipPage(next);
        Console.WriteLine($"瑙︽懜锛氬弻鎸囨í婊?鈫?{(next ? "涓嬩竴椤? : "涓婁竴椤?)}");
    }

    /// <summary>鍙屾寚鎵嬪娍鏀跺熬锛堟渶鍚庝竴鏍规寚澶存姮璧锋椂锛夈€?/summary>
    private void TouchGestureReleased()
    {
        // **涓ゆ寚杞荤偣浼樺厛浜?鍙樻崲鎻愪氦"**锛氱偣鎸夋病鏈変綅绉伙紝涓嶈璧板彉鎹㈤偅鏉¤矾銆?        // 锛?026-10-05 瀹炴祴锛氱敾甯冧笂鏈夐€変腑瀵硅薄鏃讹紝杞荤偣浼氳 `_g2Transform` 鍒嗘敮鍚冩帀鈥斺€?        //   鑰侀『搴忔妸 transform 鏀惧湪鍓嶉潰锛岃交鐐规案杩滆疆涓嶅埌銆傦級
        if (_g2Tap && !_g2Turned && _g2Axis == 0 && _touch.Views.Count >= 2)
        {
            if (SelDragging) EndSelDrag();     // 涓囦竴 transform 閭ｆ潯宸茬粡璧蜂簡鎷栧姩锛堥浂浣嶇Щ=涓嶈繘鎾ら攢鏍堬級
            _g2Transform = false;
            var mid = (_touch.Views[0].Pos + _touch.Views[1].Pos) * 0.5f;
            OpenRadialPaletteFromTouch(mid.X, mid.Y, sticky: true);
            _g2Tap = false;
            return;
        }
        if (_g2Transform)
        {
            if (SelDragging) EndSelDrag();     // 鎻愪氦鍙樻崲锛堜竴姝ユ挙閿€锛涙病鍔ㄥ氨涓嶈繘鎾ら攢鏍堬級
            _g2Transform = false;
            return;
        }
        _g2Tap = false;
    }

    /// <summary>
    /// 40ms 蹇冭烦锛堢瑪鐢昏繘琛屼腑閭ｉ瀹氭椂鍣ㄦ瘡娆￠兘璋冿級锛氶暱鎸夋垚绔嬪氨鎶婅繖涓€涓嬪崌绾ф垚"閫夋嫨"銆?    /// 鍒ゆ嵁鍏ㄥ湪 <see cref="TouchGestures.Tick"/>锛堣矾寰?鈮? 閫昏緫鍍忕礌 = 鍜?鐐逛竴涓嬩笉鐣欏ⅷ"鍚屼竴涓暟锛夈€?    /// </summary>
    private void TouchTick()
    {
        _touch.Tick(NowMs, DpiScale);
        if (_touch.LongPressFired && _touchMode == TouchMode.Write) TouchLongPressFire();

        // 涓ゆ寚闀挎寜鎴愮珛 鈫?鍛煎嚭鐩橈紙瑙﹀睆鍏ュ彛锛夈€傝繘 Radial 妯″紡鍚庯細鏂瑰悜鐢辫Е鐐瑰钩鍧囦綅鍠傦紙TouchMoveDispatch锛夛紝
        // 鏉炬墜鍦?TouchUpDispatch 閲岀‘璁わ紱娌″垝鍔ㄥ氨鏉炬墜 = 鐣欏湪鐩樹笂绛夌偣閫夛紙瑙?CommitRadialPalette锛夈€?        if (_touch.TwoFingerHoldFired && _touchMode == TouchMode.Gesture2)
        {
            _touch.ClearLongPress();
            // 鍛煎嚭鐩樼殑浣嶇疆鐢?*閽変綇鐨勯偅涓ゆ牴**锛?026-10-09锛夛紱閽夊瓙涓嶅湪鏃堕€€鍥?琛ㄩ噷鍓嶄袱涓?銆?            TouchGestures.PairView pair = default;
            bool pairOk = _g2IdA != 0 ? _touch.TryPair(_g2IdA, _g2IdB, out pair)
                                      : _touch.TryPair(out pair);
            if (pairOk)
            {
                // 鏈夐€変腑鏃惰繖涓ゆ寚鍙兘宸茬粡鎶婂彉鎹㈣捣澶翠簡锛圫elDragging锛夆€斺€斿懠鍑虹洏涔嬪墠鍏堟敹璐?                //锛堜竴鐐规病鍔ㄥ氨涓嶈繘鎾ら攢鏍堬級锛屼笉鐒剁洏涓€寮€杩欎釜鎷栧姩浼氫竴鐩存寕鐫€銆?                if (SelDragging) EndSelDrag();
                _g2Transform = false;
                _touchMode = TouchMode.Radial;
                OpenRadialPaletteFromTouch((pair.A.X + pair.B.X) * 0.5f,
                                           (pair.A.Y + pair.B.Y) * 0.5f, sticky: false);
            }
        }
    }

    /// <summary>鑷鐢細瑙︽懜灞傝繖涓€鍒荤殑妯″紡 / 瑙︾偣琛ㄣ€?/summary>
    internal TouchMode TouchModeForTest => _touchMode;
    internal int TouchCountForTest => _touch.Count;
    internal bool TouchSelectedForTest => _touchSelected;
    internal int _touchDebugAxis => _g2Axis;
    /// <summary>鑷鐢細鐩存帴鍠備竴涓Е鐐瑰彇鍒ゅ畾锛堝悎鎴愯Е鎽哥殑 `rcContact` 绯荤粺涓嶈锛岄潰绉偅鏉″彧鑳借繖涔堟祴锛夈€?/summary>
    internal TouchVerdict TouchClassifyForTest(uint id, float x, float y, float sizePx)
        => _touch.Down(id, x, y, sizePx, NowMs, DpiScale);
    internal void TouchResetForTest() { _touch.Reset(); _touchMode = TouchMode.None; _touchSelected = false; }

    /// <summary>鎶婄浉鏈烘粴鍒?杩欎釜瀵硅薄鐪嬪緱瑙?銆傚彧鍋氱旱鍚戔€斺€旀í鍚戞病鏈夋粴鍔ㄨ繖鍥炰簨銆?/summary>
    private void EnsureVisible(Stroke s)
    {
        if (s == null) return;
        var vp = ViewportCanvas;
        var b = s.PaddedBounds;
        if (b.MaxY > vp.MaxY) ViewOffsetY += b.MaxY - vp.MaxY;
        else if (b.MinY < vp.MinY) ViewOffsetY += b.MinY - vp.MinY;
        ClampViewOffset();
    }

    /// <summary>
    /// 浠庡壀璐存澘绮樹竴寮犲浘锛圕trl+V锛夈€備綅缃鍒欏拰鎴浘涓€鑷达細**瑙嗗彛宸︿笂瑙?*锛?    /// 绮樺畬绔嬪埢閫変腑銆?    /// </summary>
    /// <summary>
    /// 澶嶅埗閫変腑瀵硅薄鍒板壀璐存澘锛?*瀵硅薄瀛楄妭 + 涓€寮犲浘**銆?    ///
    /// 瀵硅薄瀛楄妭璁?绮樺洖鏉ヤ粛鏄彲缂栬緫瀵硅薄"锛堣法椤点€佽法绐楀彛銆佽法绋嬪簭瀹炰緥閮借锛夛紱
    /// 鍥炬槸缁欏閮ㄧ▼搴忕殑鍏滃簳锛圵ord / PPT / 寰俊绮樺埌鐨勬槸鍥撅紝鑷冲皯鑳界敤锛夈€?    /// 鍙斁瀵硅薄涓嶆斁鍥句篃琛岋紝浣嗛偅鏍?澶嶅埗涓€娈垫澘涔﹁创鍒板井淇?灏辨病鍙嶅簲鈥斺€斾袱澶撮兘瑕侀【銆?    /// </summary>
    internal bool CopySelectionToClipboard()
    {
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("澶嶅埗锛氭病鏈夐€変腑浠讳綍涓滆タ锛堝厛鐢ㄦ閫夋垨鐐归€夐€変腑锛?);
            return false;
        }

        byte[] objects = ClipboardInk.Serialize(sel);

        // 鍚屾椂娓叉煋涓€寮犲浘锛氳寖鍥?= 杩欐壒瀵硅薄鐨勭敾甯冭寖鍥?+ 4 閫昏緫鍍忕礌鐣欑櫧
        byte[] dib = null; int w = 0, h = 0;
        var box = EditRegion.Of(sel);
        if (!box.IsEmpty && _windows.Count > 0)
        {
            box = box.Inflate(4f * DpiScale);
            w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            // 闃插憜锛氬浘澶ぇ灏变笉鏀句簡锛堝璞℃牸寮忕収鏀撅級
            if ((long)w * h <= 32_000_000) dib = _windows[0].RenderStrokesToBgra(sel, box, w, h);
            else { w = h = 0; }
        }

        bool ok = ClipboardInk.Set(objects, dib, w, h);
        Console.WriteLine(ok
            ? $"澶嶅埗 {sel.Count} 涓璞★紙鍚屾椂鏀句簡涓€寮?{w}脳{h} 鐨勫浘锛氬閮ㄧ▼搴忎篃绮樺緱涓婏級"
            : "澶嶅埗澶辫触锛堝壀璐存澘姝ｈ鍒殑绋嬪簭鍗犵潃锛燂級");

        // 灞忓箷涓婄殑鍙嶉鍙湁**閫夊尯闂竴涓?*锛堢敤鎴峰弽棣堢殑"澶嶅埗浠ュ悗鐪嬩笉鍑烘潵"锛夈€?        // 鎻愮ず鏉?2026-09-16 鎸夌敤鎴疯姹傚幓鎺変簡锛氬睆骞曚笂涓€涓瓧閮戒笉鐣欍€?        if (ok) SelFlashUntilMs = NowMs + SelFlashMs;
        return ok;
    }

    // ---- 瀵煎嚭锛堥€変腑 鈫?閫忔槑搴?PNG锛?-----------------------------------------
    //
    // 鐢ㄦ埛 2026-09-17 瀹氾細**鍙閫変腑鐨?*銆?*閫忔槑搴?PNG**銆?*寮圭郴缁?鍙﹀瓨涓?**锛?    // 鑰屼笖**鍚屾椂鏀捐繘鍓创鏉?*锛堜袱鏍烽兘瑕侊級銆傝惤鐐逛笌鍛藉悕鎸?    // [璋冪爺-閫変腑妗?鍙嶉-瀵煎嚭-灞傜骇-灞炴€?md] 绗簩鑺傘€?    //
    // 灞忓箷涓?*涓嶇暀瀛?*锛堢敤鎴?2026-09-16 鏄庣‘瑕佹眰鎶婃彁绀烘潯鍘绘帀锛夛紝鍙閫夊尯闂竴涓嬧€斺€?    // 鏂囦欢瀵硅瘽妗嗘湰韬氨鏄渶娓呮鐨勫弽棣堛€?
    /// <summary>
    /// 鎶婇€変腑鐨勫唴瀹圭灞忔覆鏌撴垚涓€寮?*閫忔槑搴?* BGRA锛堝拰"澶嶅埗鍒板壀璐存澘"閭ｆ潯璺悓涓€涓覆鏌撳櫒锛夈€?    /// 杩斿洖 null = 涓嶈兘瀵硷紙娌￠€変腑 / 澶ぇ / 娓叉煋澶辫触锛夛紝姣忕鎯呭喌閮芥湁鏃ュ織銆?    /// </summary>
    private byte[] RenderSelectionForExport(out int w, out int h)
    {
        w = h = 0;
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("瀵煎嚭锛氭病鏈夐€変腑浠讳綍涓滆タ锛堝厛鐢ㄦ閫夋垨鐐归€夐€変腑锛?);
            return null;
        }
        var box = EditRegion.Of(sel);
        if (box.IsEmpty) return null;
        box = box.Inflate(4f * DpiScale);                     // 鍜?澶嶅埗"鍚屼竴涓暀鐧?        w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 32_000_000)
        {
            Console.WriteLine($"瀵煎嚭锛氶€変腑鐨勫唴瀹瑰お澶э紙{w}脳{h}锛?);
            w = h = 0;
            return null;
        }
        if (_windows.Count == 0) { w = h = 0; return null; }

        var bgra = _windows[0].RenderStrokesToBgra(sel, box, w, h);
        if (bgra == null) { Console.WriteLine("瀵煎嚭锛氱灞忔覆鏌撳け璐?); w = h = 0; }
        return bgra;
    }

    /// <summary>
    /// 鍐?PNG 锛?**鍚屾椂鏀捐繘鍓创鏉?*锛堝璞?锛?杩欏紶鍥撅紝鍜?Ctrl+C 鍚屼竴涓壀璐存澘鍚堝悓锛夛紝
    /// 鍐嶈閫夊尯闂竴涓嬨€?    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path)
        => WriteExport(bgra, w, h, path, 1);

    /// <summary>
    /// 鍐欑洏銆?paramref name="filterIndex"/> = 鑰佸笀鍦ㄧ被鍨嬫爮閫夌殑閭ｄ竴鏉★紙鍐冲畾"閫忔槑搴?/ 鐧藉簳"锛夛紝
    /// 缂栫爜鍣ㄦ寜鎵╁睍鍚嶅喅瀹氣€斺€斾袱鑰呯殑鍒嗗伐瑙?`ExportFormats.Encode` 鐨勬敞閲娿€?    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path, int filterIndex)
    {
        LastExportPath = null;
        LastExportFilterIndex = 0;
        try
        {
            var bytes = ExportFormats.Encode(path, bgra, w, h, filterIndex, out string tag);
            if (bytes == null) { Console.WriteLine($"瀵煎嚭锛歿tag} 缂栫爜澶辫触"); return false; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"瀵煎嚭鎴愬姛锛歿path}锛坽w}脳{h}锛寋tag}锛寋bytes.Length / 1024.0:F0} KB锛?);
            LastExportPath = Path.GetFullPath(path);
            LastExportFilterIndex = filterIndex;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"瀵煎嚭澶辫触锛歿ex.Message}");
            return false;
        }

        // 鐢ㄦ埛 2026-09-17 鏇存锛?*瀵煎嚭涓嶅啀椤烘墜鏀捐繘鍓创鏉?*鈥斺€?澶嶅埗"閭ｆ潯璺凡缁忔湁浜?        // 锛堥€変腑 鈫?Ctrl+C / 鎿嶄綔鏉′笂鐨勫鍒讹級锛屽鍑哄氨鍙钀界洏锛岃亴璐ｅ崟涓€銆佷篃灏戜竴娆?        // 鍓创鏉垮啓鍏ワ紙鍓创鏉挎槸鍏ㄥ眬璧勬簮锛岃兘灏戠灏卞皯纰帮級銆?        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **瀵煎嚭鎸夐挳鍋氱殑浜?*锛氱灞忔覆鏌?鈫?寮?鍙﹀瓨涓? 鈫?鍐欑洏銆傚彇娑?= 浠€涔堥兘涓嶅仛銆?    ///
    /// 鏍煎紡**灏卞湪閭ｄ釜瀵硅瘽妗嗛噷閫?*锛堣 <see cref="ExportFormats"/>锛氶€忔槑搴?/ 鐧藉簳 /
    /// 鏂囦欢澶у皬 / 鑰佽蒋浠跺吋瀹瑰洓绉嶏紝姣忕鎶婁紭鍔垮啓鍦ㄧ被鍨嬪悕閲岋級銆傛垜浠繖杈?*涓嶅啀鍏堝脊涓€灞?    /// 鑷繁鐨勬牸寮忛潰鏉?*鈥斺€旂敤鎴?2026-09-17锛?閭ｄ笅闈㈣繖涓や釜鍥炬爣灏辨病鏈夌敤浜嗗惂锛岀敤鎴烽兘鍙互
    /// 鑷繁淇濆瓨鍥剧墖浜?锛氳寰楀锛屼袱澶勯€夋牸寮忓弽鑰岀粫锛岀偣瀵煎嚭鐩存帴鍑哄璇濇杩樺皯涓€姝ャ€?    /// 锛堣Е鎽稿睆涓婇偅涓笅鎷夌幇鍦ㄤ篃鐐瑰緱寮€浜嗭細鐪熷洜鏄鐩栧眰姣忕鎶㈠眰锛岃 ReassertTopmost銆傦級
    /// </summary>
    /// <param name="defaultFilterIndex">
    /// 榛樿閫変腑绗嚑鏉★紙1 璧凤級銆傜晫闈㈡妸"涓婃鐢ㄧ殑閭ｆ潯"璁板湪鍋忓ソ閲屼紶杩涙潵锛?    /// 鎵€浠ュ父鐢?JPG 鐨勮€佸笀涓嬫涓嶇敤鍐嶉€夈€?    /// </param>
    /// <returns>鐢ㄦ埛鏈€缁堥€夌殑閭ｄ竴鏉★紙1 璧凤級锛涘彇娑堟垨娌″鎴愯繑鍥?0銆?/returns>
    internal int ExportSelection(int defaultFilterIndex = 1)
    {
        ExportAttempts++;
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return 0;

        // 鑷妯″紡**缁濅笉寮瑰璇濇**锛氱郴缁熷璇濇浼氶樆濉炲湪閭ｉ噷绛夋秷鎭紝
        // 鑰岃嚜妫€鏄潬"鎶芥秷鎭?+ 娓叉煋"鑷繁鎺ㄨ繘鐨勨€斺€斾竴寮瑰氨鎶婃暣鏉¤嚜妫€鍗℃鍒拌秴鏃躲€?        // 锛堣嚜妫€瑕侀獙鐨勬槸"娓叉煋 鈫?缂栫爜 鈫?鍐欑洏 鈫?鍓创鏉?杩欐潯閾撅紝閭ｆ潯鐢?--iotest 璧?        //  ExportSelectionToPathForTest锛屼笉缁忚繃杩欓噷銆傦級
        if (!ExportDialogEnabled)
        {
            Console.WriteLine("瀵煎嚭锛氳嚜妫€妯″紡涓嶅脊鍙﹀瓨涓哄璇濇锛堣 --iotest锛?);
            return 0;
        }

        // 榛樿鎵╁睍鍚嶈窡鐫€閫夌殑閭ｄ竴鏉¤蛋锛堢 1 鏉?PNG 閫忔槑搴曟槸鑰佸笀鏈€甯歌鐨勶細
        // "璐村埌鍒涓嶅甫璧扮櫧搴?锛夈€?        int want = Math.Clamp(defaultFilterIndex, 1, ExportFormats.Count);
        string suggested = $"閫変腑-{DateTime.Now:yyyyMMdd-HHmm}" + ExportFormats.ExtensionFor(want);
        string path = null;
        int chosen = want;
        BorrowFocusForDialog();                 // 瑕嗙洊灞傚钩鏃朵笉鎶㈢劍鐐癸紝寮规鍓嶄复鏃舵斁寮€
        ExportDialogOpen = true;                // 寮规鏈熼棿涓嶈鍐嶆姮瑕嗙洊灞傦紝瑙?ReassertTopmost
        try { path = ExportFileDialog.AskForImage(OwnerHwnd(), suggested, want, "瀵煎嚭閫変腑鐨勫唴瀹?, out chosen); }
        catch (Exception ex)
        {
            // **寮规杩欎竴姝ュ嚭閿欑粷涓嶅厑璁告墦姝昏蒋浠?*銆傜湡韪╄繃锛?NET 7 璧风粨鏋勪綋瀛楁涓嶈兘鐢?            // StringBuilder锛岄偅涓€鐗堜竴鐐?瀵煎嚭"鏁翠釜杩涚▼灏辨病浜嗭紙寮傚父浠?P/Invoke 鍐掑埌 Main锛夈€?            // 鐜板湪鏈€鍧忎篃鍙槸"杩欐瀵间笉鍑哄幓"锛屽苟涓旀妸鍘熷洜璇存竻妤氥€?            Console.WriteLine($"瀵煎嚭锛氬脊鍙﹀瓨涓哄け璐ワ紙{ex.GetType().Name}: {ex.Message}锛?);
            return 0;
        }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }

        if (path == null) { Console.WriteLine("瀵煎嚭锛氬彇娑?); return 0; }
        return WriteExport(bgra, w, h, path, chosen) ? chosen : 0;
    }

    /// <summary>
    /// 瀵煎嚭鎸夐挳鐪熸璋冪殑閭ｄ竴灞傦細**璁颁綇鑰佸笀涓婃閫夌殑閭ｄ竴鏉℃牸寮?*锛坄ui.exportFormat`锛夛紝
    /// 涓嬫鐩存帴榛樿瀹冣€斺€斿父鐢?JPG 鐨勮€佸笀涓嶇敤姣忔閮藉幓涓嬫媺閲屾壘銆?    ///
    /// 杩欎竴鏉″亸濂界敱**寮曟搸鑷繁瑙ｉ噴**锛堝埆鐨?`ui.*` 閮芥槸鐣岄潰璇翠簡绠楋級銆傜悊鐢憋細鎿嶄綔鏉?    /// 鏄紩鎿庨偅杈圭敾鍜屽懡涓祴璇曠殑锛坄Selection.cs`锛夛紝瀵煎嚭鎸夐挳鍘嬫牴涓嶇粡杩?`InkUi`锛?    /// 鎵€浠?璁颁綇涓婃鐨勯€夋嫨"鍙兘钀藉湪杩欓噷銆傞敭鍚嶇収鏃ф斁鍦?`ui` 娈碉紝涓嶅彟寮€涓€鑺傘€?    ///
    /// 娉ㄦ剰锛氳嚜妫€妯″紡涓嬭繖鏉″亸濂?*璇讳笉鍒颁篃鍐欎笉杩?*锛堣嚜妫€涓嶇鐢ㄦ埛閰嶇疆锛夛紝
    /// 鎵€浠ヨ嚜妫€閲岃蛋鐨勬槸榛樿绗?1 鏉★紝鍒ゆ嵁鏄‘瀹氱殑銆?    /// </summary>
    private void ExportSelectionPref()
    {
        int last = ExportDefaultFilterIndex();
        int chosen = ExportSelection(last);
        if (chosen >= 1) SetUiPref("exportFormat", chosen.ToString());
    }

    /// <summary>
    /// 榛樿閫変腑绗嚑鏉℃牸寮忥細璇诲亸濂?`ui.exportFormat`锛岃涓嶅嚭鏉?瓒婄晫灏卞洖鍒扮 1 鏉★紙PNG 閫忔槑搴曪級銆?    /// 鍗曠嫭鎶藉嚭鏉ユ槸涓轰簡鑳借嚜妫€锛?璁颁綇涓婃鐨勯€夋嫨"杩欐潯閫昏緫涓嶄緷璧栧璇濇锛夈€?    /// </summary>
    internal int ExportDefaultFilterIndex()
        => int.TryParse(GetUiPref("exportFormat"), out int v)
           && v >= 1 && v <= ExportFormats.Count ? v : 1;

    /// <summary>鑷鐢細涓嶅脊瀵硅瘽妗嗭紝鐩存帴鍐欏埌鎸囧畾璺緞锛堝叾浣欐祦绋嬩竴妯′竴鏍凤級銆?/summary>
    internal bool ExportSelectionToPathForTest(string path, int filterIndex = 1)
    {
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return false;
        return WriteExport(bgra, w, h, path, filterIndex);
    }

    private IntPtr OwnerHwnd() => _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

    /// <summary>
    /// 瀵煎嚭鏃惰涓嶈寮圭郴缁熺殑"鍙﹀瓨涓?銆?*鑷妯″紡蹇呴』鍏虫帀**锛堣 <see cref="ExportSelection"/>锛夛細
    /// 绯荤粺瀵硅瘽妗嗕細闃诲绛夋秷鎭紝鑰岃嚜妫€鏄嚜宸辨娊娑堟伅鎺ㄨ繘鐨勶紝涓€寮瑰氨鍗″埌瓒呮椂銆?    /// 浜у搧閲屼繚鎸?true銆?    /// </summary>
    internal bool ExportDialogEnabled = true;

    /// <summary>
    /// "绯荤粺瀵硅瘽妗嗘寮€鐫€"銆?*鍙敤鏉ユ寜浣?<see cref="ReassertTopmost"/>**锛?    /// 閭ｄ竴涓嬫瘡绉掍竴娆＄殑缃《閲嶆姮浼氬幓璺熷璇濇鎶㈠眰锛岄『鎵嬫妸瀹冪殑涓嬫媺鍒楄〃鍏虫帀
    /// 锛堢敤鎴锋姤鐨?閫変笉鍒?jpg"灏辨槸瀹冿級銆傝 <see cref="ReassertTopmost"/> 鐨勬敞閲娿€?    /// </summary>
    internal bool ExportDialogOpen;

    /// <summary>
    /// 涓婁竴娆?*鐪熺殑鍐欏嚭鍘?*鐨勯偅涓枃浠讹紙鍏ㄨ矾寰勶紱娌℃垚鍔熻繃灏辨槸 null锛夛紝浠ュ強瀹冩槸涓嶆槸 JPEG銆?    ///
    /// `--dialogprobe --save` 闈犲畠纭"瀵硅瘽妗?鈫?璺緞 鈫?缂栫爜 鈫?钀界洏"鏁存潯閾剧湡鐨勯€氫簡锛?    /// 鑰屼笉鏄彧鐪嬪璇濇寮规病寮瑰嚭鏉ャ€?    /// </summary>
    internal string LastExportPath;
    /// <summary>涓婁竴娆℃垚鍔熷鍑虹敤鐨勯偅涓€鏉℃牸寮忥紙1 璧凤紝瑙?<see cref="ExportFormats"/>锛夈€?/summary>
    internal int LastExportFilterIndex;

    /// <summary>
    /// 璇曡繃鍑犳瀵煎嚭锛?*鍖呮嫭娌℃湁閫変腑銆佽鑷鎸′笅銆佺敤鎴峰彇娑?*锛夈€?    ///
    /// 鑷瑕佺殑"鐐逛竴涓?鍜?鐪熺殑寮规"鍒嗗紑锛歚--edittest` 鍦ㄨ嚜妫€妯″紡涓嬬偣瀵煎嚭锛?    /// 瀵硅瘽妗嗘槸鍏崇潃鐨勶紝鍙兘闈犺繖涓鏁板櫒璇佹槑閭ｄ竴涓嬬湡鐨勮蛋鍒颁簡瀵煎嚭杩欐潯璺?    /// 锛堣€屼笉鏄?浠€涔堥兘娌″彂鐢?锛夈€?    /// </summary>
    internal int ExportAttempts;

    // 寮圭郴缁熷璇濇鏈熼棿鍊熶竴涓嬬劍鐐癸細鎶?WS_EX_NOACTIVATE 鎽樻帀銆佸脊瀹岃鍥炲幓銆?    // 鍜?鎵规敞閿洏妯″紡"鍚屼竴涓墜娉曪紙閭ｈ竟鏄暱鏈熸憳鎺夛紝杩欓噷鏄复鏃剁殑锛夈€?    private bool _focusBorrowed;

    private void BorrowFocusForDialog()
    {
        if (_focusBorrowed) return;
        _focusBorrowed = true;
        // **鍚屾椂鎶?缃《"鎽樻帀**锛氭垜浠殑瑕嗙洊灞傛槸 WS_EX_TOPMOST + 閾烘弧鏁村睆锛?        // 鑰岀郴缁熷璇濇鑷繁鐨勫脊灞傦紙**鏂囦欢绫诲瀷閭ｄ釜涓嬫媺鍒楄〃**锛夋槸鏅€氬脊绐椻€斺€?        // 瀹冧細琚疆椤剁殑瑕嗙洊灞傜洊浣忥紝鐢ㄦ埛鐪嬪埌鐨勫氨鏄?鐐瑰紑涓€涓嬪氨鏀跺洖鍘汇€侀€変笉鍒?JPG"
        // 锛堢敤鎴?2026-09-17 鎶ョ殑锛夈€傚脊妗嗘湡闂存垜浠笉闇€瑕佹诞鍦ㄦ渶涓婇潰锛屾憳鎺夊氨濂姐€?        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
            // 鈿?**涓嶈 SWP_FRAMECHANGED**锛氳鐩栧眰鏄?DComp 鍚堟垚鐨勫叏灞忓眰锛屽己鍒堕噸绠楁鏋?            // 浼氬湪鎹㈡牱寮忛偅涓€甯ч棯涓€涓嬬櫧锛?026-10-05 鐢ㄦ埛鎶?淇濆瓨鍥剧墖鍏堥棯鐧藉睆"鐨勬潵婧愪箣涓€锛夈€?            // WS_EX_NOACTIVATE 灞炴縺娲荤被鏍峰紡锛屽幓鎺夊畠涓嶉渶瑕?FRAMECHANGED銆?            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            Native.SetWindowPos(w.Hwnd, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        if (_windows.Count > 0) Native.SetForegroundWindow(_windows[0].Hwnd);
    }

    private void ReturnFocusAfterDialog()
    {
        if (!_focusBorrowed) return;
        _focusBorrowed = false;
        if (KeyboardMode) return;               // 閿洏妯″紡鏈潵灏辫姹傝兘婵€娲伙紝鍒妸瀹冪殑鏍峰紡鏀瑰洖鍘?        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            // 鍚?BorrowFocusForDialog锛氳繖閲屼篃涓嶅甫 SWP_FRAMECHANGED锛堥棯鐧芥潵婧愶級銆?            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            // 鎶?缃《"瑁呭洖鍘伙紙瑕嗙洊灞傚钩鏃跺繀椤绘诞鍦ㄦ墍鏈夌▼搴忎笂闈級
            Native.SetWindowPos(w.Hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 绮樿创锛?*浼樺厛绮樺洖鍙紪杈戝璞?*鈥斺€斿壀璐存澘閲屾湁鎴戜滑鐨勫璞℃牸寮忓氨杩樺師鎴愬璞★紝
    /// 娌℃湁灏遍€€鍥?褰撳浘鐗囩矘璐?锛堝師鏈夎涓猴級銆傝惤鍦ㄨ鍙ｅ乏涓婅骞惰嚜鍔ㄩ€変腑锛岀矘瀹屽氨鑳芥嫋璧般€?    /// </summary>
    internal bool PasteFromClipboard()
    {
        ExitReplayForEdit("绮樿创");
        if (ClipboardInk.TryGetObjects(out var objs) && objs.Count > 0)
        {
            var vp = ViewportCanvas;
            float m = CaptureMarginLogical * DpiScale;
            var box = EditRegion.Of(objs);
            var move = Matrix3x2.CreateTranslation(vp.MinX + m - box.MinX, vp.MinY + m - box.MinY);
            foreach (var s in objs) s.Transform = s.Transform * move;   // 鎻掑叆鍓嶅钩绉伙紙鐢诲竷鍧愭爣锛?
            Doc.AddStrokes(objs);          // 涓€姝ユ挙閿€
            Doc.SelectOnly(objs);
            Tool = Tool.Marquee;
            NotifyUiStateChanged();
            _dirty = true;
            Console.WriteLine($"绮樿创 {objs.Count} 涓璞★紙浠嶆槸鍙紪杈戝璞★級");
            return true;
        }
        return PasteImageFromClipboard();
    }

    internal bool PasteImageFromClipboard()
    {
        if (!ClipboardImage.TryGetImage(out var pixels, out int w, out int h, out bool hasAlpha))
        {
            Console.WriteLine("绮樿创锛氬壀璐存澘閲屾病鏈夊彲鐢ㄧ殑鍥惧儚锛堝彧璁?CF_DIB / CF_DIBV5锛?);
            return false;
        }
        var img = ImageData.Adopt(w, h, pixels, hasAlpha);
        if (img == null) return false;

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);
        EnsureVisible(placed);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"绮樿创鍥剧墖 {w}脳{h}");
        return true;
    }

    // ==================================================================
    //  鍥惧簱锛?鎴戠殑鍥惧舰"锛夛細鎶婇€変腑鐨勫璞″瓨璧锋潵锛屼箣鍚庝粠鍥惧舰闈㈡澘鏈€鍚庨偅涓€娈靛彇鍥炴潵
    //
    //  鍙ｅ緞涓庡彇鑸嶈 璁″垝-鍥惧舰宸ュ叿.md 搂41 鍜?ShapeLibrary 鐨勬敞閲婏紱杩欓噷鍙啓寮曟搸杩欎竴渚э細
    //    路 淇濆瓨锛氶€変腑 鈫?涓€涓枃浠讹紙纾佺洏閭ｇ偣浜嬪叏鍦?ShapeLibrary 閲岋級锛?    //    路 闈㈡澘寮€鍚?+ 閲嶈鐩綍锛?    //    路 钀界瑪鎻掑叆锛氱偣涓€寮?鈫?寰呮彃鍏?鈫?鐢诲竷涓婃寜涓嬪氨钀藉湪閭ｅ効銆?*鍜?绮樿创瀵硅薄"鍚屼竴鏉¤矾**
    //      锛堝钩绉诲榻愯惤鐐广€佽繘鏂囨。涓€姝ユ挙閿€銆佹彃鍏ュ悗鑷姩閫変腑锛夆€斺€斾笉鍙﹂€犱竴濂椼€?    // ==================================================================

    /// <summary>鍥惧簱闈㈡澘寮€鐫€鍚楋紙寮曟搸渚ф诞鍔ㄩ潰鏉匡紝鍜岄鑹?/ 灞傜骇闈㈡澘鍚屼竴濂楃敾娉曪級銆?/summary>
    internal bool LibraryPanelOpen;
    /// <summary>闈㈡澘閲岀殑鏉＄洰銆傛墦寮€闈㈡澘鏃惰涓€娆★紝瀛?/ 鍒犱箣鍚庨噸璇烩€斺€斾笉鍋氬閲忔洿鏂帮紙鏉＄洰寰堝皯锛夈€?/summary>
    internal List<ShapeLibrary.Entry> LibraryEntries = new();
    /// <summary>鎸囬拡鎮仠鍦ㄥ摢涓牸瀛愪笂锛?1 = 娌″湪鏍煎瓙涓婏級銆?/summary>
    internal int LibraryHover = -1;
    /// <summary>
    /// 涓婁竴甯ф寚閽堝湪涓嶅湪"鑷粯鐣岄潰鍧?锛堝浘搴撻潰鏉匡級涓娿€傚彧鐢ㄦ潵鍦?*杩涘嚭閭ｄ竴鍒绘爣鑴?*锛?    /// 浠庣敾甯冨甫杩涙潵鐨勮惤鐐圭幆瑕佹摝鎺夛紙瑙?<see cref="PointerOnDrawnChrome"/> 鐨勬敞閲婏級銆?    /// </summary>
    private bool _onDrawnChrome;
    /// <summary>
    /// 銆屾暣鐞嗐€嶆ā寮忥細姣忎釜鏍煎瓙涓婂彔涓€棰楃孩 鉁曪紝鐐瑰畠灏辨槸鍒犮€?    /// 涓轰粈涔堣鏈夎繖涓ā寮忥紙鍙傝€冨疄鐜颁篃鏈変竴妯′竴鏍风殑涓€涓級锛?*瑙︽懜灞忔病鏈夊彸閿?*锛?    /// 鑰?鐐规牸瀛?鏈韩宸茬粡鏄?鎻掑叆"锛屾墍浠ュ垹闄ゅ繀椤绘崲涓€绉嶆墜鍔裤€?    /// </summary>
    internal bool LibraryEditMode;
    /// <summary>寰呮彃鍏ョ殑鍐呭锛堥潪绌?= 宸茬粡鐐逛簡鏍煎瓙锛屼笅涓€娆″湪鐢诲竷涓婃寜涓嬪氨钀藉湪閭ｉ噷锛夈€?/summary>
    private List<Stroke> _libraryPending;

    /// <summary>鏄笉鏄?绛夌潃钀界瑪鎻掑叆"锛堣嚜妫€ / 鍏夋爣瑕佺湅瀹冿級銆?/summary>
    internal bool LibraryInsertArmed => _libraryPending != null;

    /// <summary>鎶婇€変腑鐨勫璞″瓨鎴愪竴涓浘搴撴潯鐩€傝繑鍥炲瓨浜嗗嚑涓紙0 = 娌″瓨鎴愶級銆?/summary>
    internal int SaveSelectionToLibrary()
    {
        if (Doc.Selected.Count == 0) return 0;
        var items = new List<Stroke>(Doc.Selected);
        if (ShapeLibrary.Save(items) == null) return 0;
        // 鍙嶉鍙湁"闂竴涓?锛堝拰鍏ㄩ€夊悓涓€涓?`SelFlashUntilMs`锛夛細杩欎竴鐗堟病鏈?toast锛?        // 鑰屽瓨杩涘浘搴撹繖浠朵簨**蹇呴』鏈変釜鍔ㄩ潤**鈥斺€斾笉鐒惰€佸笀涓嶇煡閬撶偣涓簡娌℃湁銆?        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return items.Count;
    }

    internal void OpenLibraryPanel()
    {
        ReloadLibrary();
        LibraryPanelOpen = true;
        LibraryEditMode = false;
        _dirty = true;
    }

    internal void CloseLibraryPanel()
    {
        if (!LibraryPanelOpen && !LibraryEditMode) return;
        LibraryPanelOpen = false;
        LibraryEditMode = false;
        LibraryHover = -1;
        _onDrawnChrome = false;   // 闈㈡澘娌′簡锛?鍦ㄧ晫闈笂"杩欐潯鐘舵€佽窡鐫€娓呴浂锛堜笅娆℃墦寮€閲嶆柊瑙﹀彂鏍囪剰锛?        _dirty = true;
    }

    internal void ToggleLibraryPanel()
    {
        if (LibraryPanelOpen) CloseLibraryPanel();
        else OpenLibraryPanel();
    }

    /// <summary>閲嶈鐩綍锛堟墦寮€闈㈡澘 / 瀛樺畬 / 鍒犲畬閮借蛋杩欓噷锛夈€?/summary>
    internal void ReloadLibrary()
    {
        LibraryEntries = ShapeLibrary.List();
        LibraryHover = -1;
        _dirty = true;
    }

    /// <summary>鐐逛簡绗?i 涓牸瀛?= 杩?钀界瑪鎻掑叆"鎬侊紙闈㈡澘鑷繁鏀惰捣鏉ワ紝鍒尅鐫€瑕佽惤鐨勫湴鏂癸級銆?/summary>
    internal bool ArmLibraryInsert(int index)
    {
        if (index < 0 || index >= LibraryEntries.Count) return false;
        _libraryPending = LibraryEntries[index].Strokes;
        CloseLibraryPanel();
        // 鎵嬪ご姝ｅ湪鐢荤殑涓滆タ浣滃簾锛堝拰绮樿创鍓嶄竴鏍凤細鍏嶅緱杩欎竴娆℃寜涓嬪張琚綋鎴愭帴鐫€鐢伙級
        ActiveStroke = null;
        _stepPlan = null;
        _stepIndex = 0;
        Console.WriteLine($"鍥惧簱锛氬凡閫変腑绗?{index + 1} 涓潯鐩紙{_libraryPending.Count} 涓璞★級锛屽埌鐢诲竷涓婃寜涓嬪氨钀藉湪閭ｉ噷");
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 鎶婂緟鎻掑叆鐨勫浘搴撳唴瀹硅惤鍦?(x, y)锛?*鍖呭洿鐩掑乏涓婅瀵归綈钀界偣**銆佸師濮嬪ぇ灏忋€佽繘鏂囨。**涓€姝ユ挙閿€**銆?    /// 鎻掑叆鍚?*鑷姩閫変腑**鈥斺€斿拰"绮樿创瀵硅薄"瀹屽叏鍚屼竴鏉¤矾锛堢敤鎴疯鐨勫氨鏄?鐐逛竴涓嬪氨鏈変唤鑳芥嫋鐨?锛夈€?    /// </summary>
    internal bool TryInsertLibraryAt(float x, float y)
    {
        if (_libraryPending == null) return false;
        var src = _libraryPending;
        _libraryPending = null;

        var objs = new List<Stroke>(src.Count);
        foreach (var s in src) objs.Add(s.Clone());
        foreach (var s in objs) s.Id = 0;          // 韬唤閲嶆柊鍙戯紙鍚屽壀璐存澘閭ｆ潯鏁欒锛氭挒鍙蜂細鎸囬敊瀵硅薄锛?
        var box = EditRegion.Of(objs);
        var move = Matrix3x2.CreateTranslation(x - box.MinX, y - box.MinY);
        foreach (var s in objs) s.Transform = s.Transform * move;

        Doc.AddStrokes(objs);
        Doc.SelectOnly(objs);
        // **鏀惰捣鏉ラ偅涓€鎬?*锛氬拰"鍒氱敾瀹屼竴涓浘褰?涓€鑷达紙瑙?`_autoSelCollapsed`锛夆€斺€?        // 鎻掕繘鏉ュ彧鏄厛缁欎釜杞荤殑妗嗭紝瑕佹暣鏉℃搷浣滄潯鐐逛竴涓嬪渾閽€?        _autoSelCollapsed = true;
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        Console.WriteLine($"浠庡浘搴撴彃鍏?{objs.Count} 涓璞?);
        return true;
    }

    /// <summary>鍥惧簱闈㈡澘杩欎竴鍒荤殑鐭╁舰锛?*鐢诲竷鍧愭爣**锛夈€傜粯鍒躲€佸懡涓€佽剰鍖洪兘闂畠銆?/summary>
    internal RectF LibraryPanelRectNow()
    {
        float dpi = UiUnit;
        var ui = UiQueryBoundsNow();                 // 鐣岄潰閭ｅ鏄?*閫昏緫**鍍忕礌锛岃涔樺洖鐗╃悊锛圖PI 脳 鐣岄潰缂╂斁锛?        var screen = LogicalVirtualScreen;           // 閫昏緫铏氭嫙妗岄潰
        float uiTop = ui.IsEmpty
            ? screen.MinY + 80f
            : ui.MinY * dpi;                         // 鐣岄潰鍧楃殑涓婃部锛堝伐鍏锋潯鍦ㄦ渶涓嬶紝涓婂甫鍦ㄥ畠涓婇潰锛?        return LibraryLayout.PanelRect(screen.MinX * dpi, screen.MaxX * dpi, uiTop, dpi,
                                       LibraryEntries.Count);
    }

    /// <summary>鍥惧簱闈㈡澘涓婄殑鎸変笅銆傝繑鍥?true = 杩欎竴涓嬪綊闈㈡澘锛堜笉鍐嶅線涓嬭蛋鍒扮敾甯冿級銆?/summary>
    private bool LibraryPointerDown(float x, float y)
    {
        float dpi = UiUnit;
        var panel = LibraryPanelRectNow();
        if (!LibraryLayout.Contains(panel, x, y)) return false;    // 闈㈡澘澶栵細浜ょ粰璋冪敤鏂规敹闈㈡澘

        if (LibraryLayout.CloseRect(panel, dpi).Contains(x, y)) { CloseLibraryPanel(); return true; }

        if (LibraryLayout.EditRect(panel, dpi).Contains(x, y))
        {
            LibraryEditMode = !LibraryEditMode;
            LibraryHover = -1;
            _dirty = true;
            return true;
        }

        int idx = LibraryLayout.CellAt(panel, dpi, LibraryEntries.Count, x, y);
        if (idx < 0) return true;                 // 闈㈡澘閲岀殑绌虹櫧锛氬悆鎺夛紝鍒┛閫忓埌鐢诲竷涓婂幓

        if (LibraryEditMode)
        {
            var cell = LibraryLayout.CellRect(panel, dpi, idx);
            if (LibraryLayout.BadgeRect(cell, dpi).Contains(x, y))
            {
                ShapeLibrary.Delete(LibraryEntries[idx].Path);
                ReloadLibrary();
            }
            // 鏁寸悊妯″紡涓嬬偣鏍煎瓙鏈綋**涓嶆彃鍏?*锛堝弬鑰冨疄鐜板悓娆撅細闃叉鏁寸悊鏃惰鎻掍竴鍫嗭級
            return true;
        }

        ArmLibraryInsert(idx);
        return true;
    }

    /// <summary>
    /// 鏀?*閰嶅鏈熼殣钘?*杩欎竴浣嶏紝骞朵笖**椤烘墜璁╁畠瀵瑰簲鐨勫唴瀹瑰潡澶辨晥**銆?    ///
    /// ## 涓轰粈涔堝繀椤绘湁杩欎釜鍑芥暟鐨?椤烘墜澶辨晥"锛堣繖鏄敤鎴?2026-09-25 鎶ョ殑 bug锛?    ///
    /// 鐥囩姸锛氱敾涓ゆ敮鐨勬椂鍊欙紝**鍋滈】閭ｄ竴鍒荤涓€绗旀病娑堝け锛岀瓑鎶墜鎵嶆秷澶?*銆?    ///
    /// 鏍瑰洜鏄?*鍙敼浜嗗瓧娈点€佹病璁╂覆鏌撶紦瀛樺け鏁?*锛堟鏄湰浠撳簱璁拌繃鐨勪竴鏉¤€佹暀璁級锛?    /// 鍐呭灞備笉鏄瘡甯ч噸鐢汇€佽€屾槸**鍒嗗潡浣嶅浘缂撳瓨**锛坄CanvasTileCache`锛夛紝绗竴绗旇繖涓€鍒?    /// **鏃╁氨鐑樿繘鍧楅噷**浜嗭紱鑰屽潡鍙湪 `Doc.Version` 鍙樺寲鏃舵墠閲嶅厜鏍呭寲锛坄Overlay.SyncTiles`锛夈€?    /// 浜庢槸鍙疆瀛楁鐨勮瘽锛歚Overlay.DrawStroke` 纭疄浼氳烦杩囧畠锛屼絾**娌℃湁浠讳綍涓€甯ч噸鐢婚偅涓€鍧?*锛?    /// 灞忓箷涓婂師鏍风暀鐫€鏃у儚绱?鈥斺€?涓€鐩寸暀鍒版姮鎵嬫彁浜わ紙閭ｄ竴姝?`RemoveStroke` 鎶簡鐗堟湰鍙凤紝
    /// 鍧楄閲嶇敾锛夛紝**鐪嬭捣鏉ュ氨鏄?鏉炬墜浠ュ悗鎵嶆秷澶?**銆?    ///
    /// 鎵€浠?钘?鍜?璁╅偅涓€鍧楅噸鐢?蹇呴』**鍚屾椂**鍙戠敓 鈥斺€?杩欏氨鏄繖涓嚱鏁板瓨鍦ㄧ殑鍏ㄩ儴鐞嗙敱锛?    /// 涓や釜璋冪敤鐐癸紙钘?/ 鏀惧洖鏉ワ級閮借璧板畠锛?*涓嶈鐩存帴缁欏瓧娈佃祴鍊?*銆?    /// </summary>
    private void SetPairHidden(Stroke s, bool on)
    {
        if (s == null || s.HiddenForPairing == on) return;
        s.HiddenForPairing = on;
        // 妯″瀷鏈韩涓€涓瓧娌℃敼锛堣繖涓€绗旇繕鍦ㄦ枃妗ｅ師澶勩€佹挙閿€鏍堢収鏍烽潬瀹冿級锛?        // 鍙槸鍛婅瘔娓叉煋灞?杩欏潡瑕侀噸鏉?锛堣 Document.InvalidateContent 鐨勬敞閲婏級銆?        Doc.InvalidateContent(s.PaddedBounds);
    }

    /// <summary>
    /// **涓ょ瑪閰嶅**锛氳繖涓€绗斿拰"涓婁竴绗?鏄笉鏄悓涓€涓弻鏇茬嚎鐨勪袱鏀紙鐢ㄦ埛 2026-09-25 瀹氱殑绾﹀畾锛夈€?    ///
    /// &gt; "**鐢讳袱鏀氨鏄弻鏇茬嚎锛岀敾涓€鏀氨鏄姏鐗╃嚎**銆備笉绠″畠瀹為檯涓婃槸鎶涚墿绾胯繕鏄弻鏇茬嚎锛?    /// &gt;  鎴戜滑鍙鎸夌収杩欎釜鏉ュ尯鍒嗐€?
    ///
    /// 涓轰粈涔堣繖涓€鍒€姣?鍒ゅ舰鐘?骞插噣锛氬舰鐘跺垽鏂槸**杩炵画銆佹湁鍣０**鐨勶紙澶硅銆佹畫宸兘鏍藉湪杩欏効 鈥斺€?    /// 鐪熷疄鎵嬫姈涓嬪弻鏇茬嚎鍙墿 25~40%锛夛紝鑰?**鐢讳簡鍑犵瑪**"鏄?*绂绘暎銆侀浂鍣０**鐨勶細鑰佸笀鐢讳袱鏀殑鏃跺€欙紝
    /// **浠栬嚜宸辩煡閬?*鍦ㄧ敾鍙屾洸绾裤€傛墍浠ユ棌鐨勯€夋嫨**涓嶅啀浜ょ粰鎷熷悎**锛屾嫙鍚堝彧璐熻矗"閫犱竴涓儚鐨?銆?    ///
    /// 璁ゅ嚭鏉?鈫?杩斿洖閭ｄ釜鍙屾洸绾匡紝骞舵妸"涓婁竴绗?閽夊湪 <see cref="_twoBranchPrev"/> 涓?    /// 锛堟姮鎵嬫彁浜ゆ椂**涓€璧锋敹璧?*锛岃 `EndStroke`锛夛紱璁や笉鍑?鈫?`IsNothing`锛?    /// **杩欎竴绗旂収鍘熸潵鐨勫崟绗旇矾璧?*锛堢粷涓嶆姠娲伙級銆?    ///
    /// 鎸?涓婁竴绗?鐨勫洓鏉★紝閮藉緢淇濆畧 鈥斺€?鍥犱负**璇厤鐨勪唬浠锋槸"鎶婁袱绗斿彉鎴愪竴涓?**锛?    ///   鈶?**鏂囨。閲屾渶鍚庝竴绗?*锛堜笉寰€鍓嶆壘鏇存棭鐨勶紝閭ｄ細寮€濮嬬寽锛夛紱
    ///   鈶?鍦?**20 绉?*绐楀彛鍐咃紙瑙?<see cref="TwoBranchWindowMs"/>锛夛紱
    ///   鈶?鏄?*鎵嬬粯鐨勫ⅷ**锛坄Freehand`锛夆€斺€?宸茬粡鎴愬瀷鐨勫璞′笉鍙備笌锛?    ///   鈶?涓よ竟鐐规暟閮藉锛屾渶鍚庝氦缁?`TryTwoBranchHyperbola` 鍒?*涓績瀵圭О**銆?    /// </summary>
    private ShapeGuess TryTwoBranchPair(Stroke ink, Vector2[] pts)
    {
        if (Doc.Strokes.Count == 0) return ShapeGuess.None("娌℃湁涓婁竴绗?);
        if (NowMs - _lastInkEndMs > TwoBranchWindowMs) return ShapeGuess.None("涓婁竴绗斿お涔?);
        var prev = Doc.Strokes[^1];

        // **璇婃柇鏃ュ織**锛堢敤鎴?2026-09-25 涓婃墜"涓€涓篃鐢讳笉鍑烘潵"锛夛細
        // 涓€琛岃娓?涓婁竴绗斾粈涔堟牱銆佽繖涓€绗斾粈涔堟牱銆佸崱鍦ㄥ摢涓€閬撻椄"銆?        // 鐪熸満鎺掓煡鍏ㄩ潬瀹?鈥斺€?鍚﹀垯鍙兘闈犵寽锛岃€岃繖鏉¤矾宸茬粡鐚滈敊濂藉嚑杞簡銆?        // 闄愭祦锛氭渶澶氭瘡 500ms 涓€琛岋紙杩欎釜鏂规硶鍦ㄥ仠椤挎湡闂存瘡 40ms 璧颁竴娆★級銆?        bool log = NowMs - _pairLogMs > 500;
        if (log) _pairLogMs = NowMs;

        ShapeGuess g;
        // **"绗竴鏀?鐢ㄥ摢涓€涓茬偣**锛氭甯稿氨鏄偅涓€绗旀墜缁樼殑澧紱
        // 浣嗚鏄畠**鍒氳鍋滈】鎴愬瀷杩?*锛堟敹绗斿墠鎵嬪仠浜嗕竴涓嬶級锛屽璞￠噷鍙墿 2~3 涓畾涔夌偣锛?        // 杩欓噷灏辩敤**褰撳垵閭ｄ竴绗斿師杩?*锛堝紩鐢ㄦ瘮杈冪‘璁ゅ畠杩樻槸鏂囨。閲屾渶鍚庝竴绗旓紝瑙佸瓧娈垫敞閲婏級銆?        // 鎷夸笉鍒板氨缃?null锛? 杩欎竴绗斾笉鑳藉綋"绗竴鏀?鐢級銆?        var prevInk = prev;
        if (prev.Kind != StrokeKind.Freehand)
            prevInk = prev == _lastDwellShape && _lastDwellInk != null ? _lastDwellInk : null;

        if (prevInk == null)
        {
            // 鈿?杩欎竴鏉?*寰堝鏄撲腑**锛氱涓€鏀鏄篃鍋滈】杩囷紙閭?400ms 鐨勯潤姝級锛?            //   瀹冨氨**宸茬粡鍙樻垚涓€涓璞?*浜嗭紝鑰屽璞＄殑 `Points` 鍙墿瀹氫箟鐐广€?            //   **鎷夸笉鍥炲師鏉ラ偅浜涢噰鏍风偣**锛屼簬鏄厤涓嶆垚瀵广€傛棩蹇楅噷浼氭墦鍑?`涓婁竴绗?Parabola`銆?            //   锛?026-09-26 璧凤細**鍒氬仠椤挎垚鍨嬬殑閭ｄ竴绗?*鑳戒粠 `_lastDwellInk` 鎷垮洖鍘熻抗锛?            //    鎵€浠ュ彧鏈?鏇存棭鐨勩€佸埆澶勬潵鐨勫璞?鎵嶄細璧板埌杩欓噷銆傦級
            g = ShapeGuess.None("涓婁竴绗斾笉鏄墜缁樼殑澧紙瀹冨凡缁忚鎴愬瀷杩囦簡锛?);
        }
        else if (prevInk.Points.Count < 8 || pts.Length < 8)
        {
            g = ShapeGuess.None("鏈変竴绗斿お鐭?);
        }
        else
        {
            var prevPts = new Vector2[prevInk.Points.Count];
            for (int i = 0; i < prevPts.Length; i++)
                prevPts[i] = new Vector2(prevInk.Points[i].X, prevInk.Points[i].Y);
            g = ShapeRecognize.TryTwoBranchHyperbola(prevPts, pts, DpiScale);
            if (!g.IsNothing)
            {
                _twoBranchPrev = prev;
                _twoBranchIndex = Doc.Strokes.Count - 1;
                // 鈽?**骞界伒鏈熷厛鎶婄涓€绗旇棌璧锋潵**锛堢敤鎴?2026-09-25 瀹氾細
                //   "璇嗗埆鐨勯偅涓€鍒伙紝绗竴绗斿氨璇ユ秷澶?锛夆€斺€?鍘熸潵瑕佺瓑鎶墜鎻愪氦鎵嶆秷澶便€?                //   鈿?鍙槸**涓嶇敾**锛坄Overlay.DrawStroke` 浼氳烦杩囷級锛屾枃妗ｄ竴涓瓧娌″姩 鈥斺€?                //     鎾ら攢鏍堣闈犲畠杩樺湪鍘熷銆?                SetPairHidden(prev, true);
            }
        }

        if (log)
            Console.WriteLine($"[涓ょ瑪閰嶅] {(g.IsNothing ? "鍚? : "**鏄弻鏇茬嚎**")}锛歿g.Rule}"
                              + $"锛堜笂涓€绗?{prev.Kind}路{prevInk?.Points.Count ?? 0} 鐐?
                              + $"{(prevInk != null && prevInk != prev ? "锛堢敤瀹冨綋鍒濈殑鍘熻抗锛? : "")}锛?
                              + $"杩欎竴绗?{pts.Length} 鐐癸級");
        return g;
    }

    private void EndStroke()
    {
        InputBlackBox.End(NowMs);   // 榛戝專瀛愶細鏀剁瑪锛堝惈"姣忕瑪鏀跺埌鍗宠惤鐩?锛?        _lastStrokeEndMs = NowMs;   // 鏉夸功鍘嗗彶锛?鎵嬪仠浜嗘病鏈?鐨勫垽鎹紙瑙?MaybeHistorySnapshot锛?        // 瑙︽懜鎵嬪娍鐨勫厹搴曚腑鏂紙8.4.0锛夛細姝ｅ父鏀剁瑪鏃?`_touchMode` 宸茬粡鏄?None锛堣Е鎽搁偅鏉¤嚜宸辨敹杩囷級锛?        // 鍙湁"涓㈡崟鑾?/ 鎰忓璺緞"浼氬甫鐫€娌＄粨鏉熺殑妯″紡璧板埌杩欓噷鈥斺€旀妸寮€浜嗗ご鐨勶紙鎿﹂櫎鎵规銆侀€変腑鎷栧姩锛夋敹骞插噣銆?        TouchAbort();

        // 婵€鍏夌瑪鎶墜锛氳繖鏉?*寮€濮嬭鏃?*锛堝仠鐣?2 绉掑悗鍐嶆暣浣撴贰鍑猴紝瑙?`LaserTrail.HoldMs`锛夈€?        // 鏀惧湪鏈€鍓嶉潰锛氫笅闈㈤偅鍑犳潯鍒嗘敮锛堟埅灞?/ 鍥惧舰 / 澶氱瑪锛夐兘鍜屾縺鍏夌瑪鏃犲叧锛屼笉蹇呯瓑瀹冧滑锛?        // 鑰屼笖**姣忎竴鏉℃敹绗旇矾寰勯兘瑕佽蛋鍒?*锛堟甯告姮鎵嬨€佷涪鎹曡幏閮借蛋 `EndStroke`锛夆€斺€?        // 婕忎竴娆＄殑璇濋偅鏉¤建杩瑰氨姘歌繙鏄?杩樺湪鍐?锛屾棦涓嶆贰涔熶笉浼氭秷澶便€?        Laser.Release(NowMs);

        if (ScrollBarDragging) EndScrollBarDrag();
        // `--syswet`锛氭箍澧ㄥ彧鏈夌郴缁熻建杩硅繖涓€浠斤紝鎾ら櫎瑕佺瓑骞插ⅷ閭ｄ竴甯?Present 涔嬪悗
        //锛堝惁鍒?杞ㄨ抗鍏堟病浜嗐€佸共澧ㄨ繕娌′笂灞?浼氶棯涓€甯э級锛涘叾浣欒矾寰勭収鏃х珛鍗虫挙銆?        bool sysWetEnding = _systemWetStroke != null;
        bool fastWetEnding = _fastWetStroke != null;
        foreach (var w in _windows)
        {
            if (sysWetEnding) w.EndInkTrailDeferred(); else w.EndInkTrail();
            if (fastWetEnding) w.EndFastWetDeferred();
        }
        _systemWetStroke = null;
        _fastWetStroke = null;
        _fastWetPending = false;     // 鏀剁瑪锛氭寕璧风殑婀垮ⅷ鏇存柊浣滃簾锛堝厤寰椾笅涓€甯ф嬁鏃т綅缃壘绐楀彛锛?        // **褰曞ⅷ杩?*锛堢敤鎴?2026-09-26 鎻?鎴戞墜鐢诲灏戞潯鍙屾洸绾跨粰浣狅紝浣犳寜杩欎簺鏉ュ畾鍒跺垽鎹?锛夛細
        // 鎶婅繖涓€绗旂殑**鍘熷閲囨牱鐐?*杩藉姞鍒板綍鍒舵枃浠躲€傛斁鍦ㄦ渶鍓嶉潰 鈥斺€?姝ゆ椂 `ActiveStroke`
        // 瑕佷箞杩樻槸鍘熷澧ㄣ€佽涔堟槸鍋滈】鎴愬瀷鎹㈡帀鐨勯偅涓璞★紙鎵€浠ョ湡姝ｈ鐢ㄧ殑鐐硅 `_recordPts`锛夈€?        FlushInkRecord();
        // **鎶墜灏卞叧鎺夊仠椤块偅棰楀畾鏃跺櫒**锛氬畠鍙湪"鏈夌瑪鍦ㄥ啓"鐨勬椂鍊欐湁鎰忎箟锛堣 StartDwellTimer锛夈€?        StopDwellTimer();
        // [鍒犻櫎 2026-10-05] 鏀剁瑪鏃舵竻"棰勬祴灏?锛氶殢鑰侀娴嬬郴缁熶竴璧风Щ闄わ紙娓叉煋灏惧凡涓嶅瓨鍦級銆?        Doc.EndErase();
        // 闈㈢Н鎿︽姮鎵嬶細妗嗗洖鍩哄噯灏哄锛堟偓鍋滄椂鏄剧ず鐨?= 涓嬫鎸変笅鍘荤涓€涓嬫摝鎺夌殑閭ｄ竴鍧楋級銆?        PixelEraseDragging = false;
        if (CaptureActive)
        {
            // 璧锋閭ｄ竴娆℃澗鎵?鈫?杩涜皟鏁撮樁娈碉紱璋冩暣閲屾嫋鎵嬫焺閭ｅ嚑娆℃澗鎵?鈫?鍙敹鎵嬨€?            if (!CaptureAdjusting) EndCapture();
            else EndCaptureAdjustDrag();
            _capturePressHandled = false;
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 纭/鍙栨秷閭ｄ竴涓嬫姮鎵嬶細鎴浘宸茬粡鏀跺満锛屽埆鍐嶈惤鍒?妗嗛€?閭ｆ潯璺笂
        //锛堜笉鐒?Tool=Marquee 浼氳瀹冮『鎵嬫妸鍒氳惤涓嬬殑鍥惧彇娑堥€変腑锛夈€?        if (_capturePressHandled)
        {
            _capturePressHandled = false;
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 璁颁笅"**杩欎竴绗斿埌姝ょ粨鏉?*"鐨勬椂鍒?鈥斺€?涓ょ瑪閰嶅锛堢敾涓ゆ敮 = 鍙屾洸绾匡級瑕佺敤瀹冪畻鏃堕棿绐楀彛
        // 锛堣 `TwoBranchWindowMs`锛岀幇鍦ㄦ槸 20 绉掞級銆?        // 鈿?鏀惧湪杩欓噷锛?*鎴睆閭ｆ潯鍒嗘敮涔嬪悗**锛夛細`CaptureActive` 閭ｆ潯璺篃璧?`EndStroke`锛?        //   鎴竴娆″睆涓嶈鎶婁袱绗旂殑绐楀彛鍐叉帀銆?        _lastInkEndMs = NowMs;

        if (ActiveStroke != null)
        {
            // **澶氱瑪鍥惧舰**锛氭澗鎵?= **杩欎竴绗斿畬鎴?*锛堢収 InkClass锛氭帹杩涘氨鍙戠敓鍦?MouseUp锛?            // 瑙?`MW_ShapeDrawing.cs:1814-1822` 鐨?`drawMultiStepShapeCurrentStep`锛夈€?            // 鎺ㄨ繘涓€姝ヤ箣鍚庡啀鐪?杩樻湁娌℃湁涓嬩竴绗?銆?            //
            // 娉ㄦ剰杩欓噷**涓嶄互"鎷栦簡澶氳繙"涓哄噯**锛氬绗斿浘褰㈢殑姣忎竴绗斿畾鐨勬槸**涓€涓嚑浣曢噺**
            //锛堟笎杩戠嚎妗?/ 鏇茬嚎杩囧摢锛夛紝"鍦ㄧ洰鏍囦綅缃偣涓€涓?鍜?鎷栬繃鍘?涓€鏍锋湁鏁?鈥斺€?            // 鎵€浠ヤ笉鎷?ShapeDragLongEnough 鍘诲崱瀹冿紙閭ｆ槸缁欎竴绗旀垚褰㈢殑鍥惧舰闃茶鐐圭敤鐨勶級銆?            if (_stepPlan != null) _stepIndex++;

            // 鏉炬墜灏辨妸"**杩欎竴绗斿惛鍒颁簡浠€涔?*"鏀跺洖鍘伙紙銆岀洿妫辨煴銆嶉偅棰楄兌鍥婂彧鍦ㄦ嫋鍔ㄤ腑鍑虹幇锛?            // 鍜屾嫋椤剁偣鍚搁檮閭ｈ竟鍚屼竴涓彛寰勶細鏉炬墜灏辨病鏈変簡锛夈€備笉娓呯殑璇濆畠浼氫竴鐩存寕鍦ㄥ睆骞曚笂銆?            _stepSnap = ShapeSnapKind.None;

            // 杩樻湁涓嬩竴绗?鈫?杩欎竴涓嬫澗鎵?*涓嶇畻瀹?*锛氬崐鎴愬搧鍋滃湪 ActiveStroke 閲?            //锛?*涓嶈繘鏂囨。銆佷篃涓嶅啓鎾ら攢璁板綍**锛夛紝绛夋渶鍚庝竴绗旀澗鎵嬪啀缁熶竴鎻愪氦銆?            // 杩欐牱鍦ㄧ敤鎴风溂閲岃繖鍑犵瑪鏄繛鐫€鐨勶紝涓€斾笉浼氬厛鍐掑嚭涓€鏉?鍗婃垚鍝?鏇茬嚎銆佸啀鎶婂畠鏀规帀锛?            // 鎾ら攢涔熶笉浼氬鍑哄ソ鍑犳潯璁板綍锛圛nkClass 鍚屾牱鎶婁袱绗斿杩涘悓涓€涓彁浜ら噷锛宍:1846-1850`锛夈€?            bool stepPending = _stepPlan != null && _stepIndex < _stepPlan.Apply.Length;

            // 鍥惧舰锛氭嫋鍔ㄥお鐭?= 璇偣锛?*涓嶆彁浜?*銆備粈涔堥兘涓嶇暀锛堣繛鎾ら攢璁板綍閮戒笉鐣欙級鈥斺€?            // 涓€涓€€鍖栫殑鍥惧舰锛堥浂闀垮害鐩寸嚎銆侀浂闈㈢Н鐭╁舰锛夊湪鐢婚潰涓婄湅涓嶈锛屽嵈鑳借鐐逛腑銆?            // 鑳借妗嗛€夛紝鏄渶闅捐В閲婄殑涓€绫绘潅鐗┿€?            //
            // **澶氱瑪鍥惧舰**娌℃湁"鎷栧闀?鍙緷锛堣涓婏級锛屾墍浠ユ敼鐪?*瀹氫箟鍏冪礌榻愭病榻?*锛?            // 鍙屾洸绾垮皯涓€涓?鏇茬嚎缁忚繃鐨勭偣"灏辩瓑浜庝粈涔堥兘娌″畾涓嬫潵锛坄MinCurvePoints` 璇村畠瑕佷笁涓偣锛夛紝
            // 杩欑鍗婃垚鍝佷笉鎻愪氦銆傚昂瀵镐笅闄愬湪鍚勮嚜鐨勭畻寮忛噷宸茬粡鍗℃
            //锛坄SetHyperbolaFromAsymptote`锛夛紝閫€鍖栦笉鎴?鐪嬩笉瑙佸嵈鍗犵潃涓€鏉″璞?銆?            bool commit = !stepPending
                       && ActiveStroke.Points.Count > 0
                       && (_stepPlan != null
                           ? ActiveStroke.Points.Count >= Stroke.MinCurvePoints(ActiveStroke.Kind)
                           // 涓夌鏉ユ簮鍒嗕笁妗ｏ細
                           //   路 鑷敱绗旇抗锛氫笉鍗￠暱搴︼紙瀹冩湰鏉ュ氨鏄竴绗斾竴鐢伙級锛?                           //   路 **鍋滈】鍙樺嚭鏉ョ殑鍥惧舰**锛氫篃涓嶅崱闀垮害 鈥斺€?璇嗗埆鍣ㄥ凡缁忛獙鏀惰繃瀹?                           //    锛堟€婚暱 鈮?40 閫昏緫鍍忕礌銆佹瘡鏉¤竟閮借创寰椾綇澧紝瑙?ShapeRecognize锛夛紱
                           //     鑰?*杩欐潯闂ㄦ閲忕殑鏄浘褰€佷笉鏄ⅷ**锛?瀹氫箟鍏冪礌棣栨湯涓ょ偣澶熶笉澶熻繙"锛夛紝
                           //     瀵?瀹氫箟鍏冪礌澶╃敓灏辩煭"鐨勫浘褰細璇潃锛屼竴璇潃灏辨槸**鍥惧舰鍜屾墜缁樺師杩?                           //     涓€璧疯涓㈡帀**锛堢敤鎴?2026-09-24 鎶ョ殑"鍥惧舰鏁翠釜浼氭秷澶辨帀"灏辨槸杩欎竴绫伙級銆?                           //     鈿?浠婂ぉ杩欎竴妗?*澶熶笉鍒?*锛氳瘑鍒櫒鐨?鎬婚暱 鈮?40 閫昏緫鍍忕礌"鎶婂畾涔夊厓绱犵殑
                           //     灏哄害椤跺湪浜嗚繖鏉￠棬妲涗箣涓婏紙鍦嗭細r 鈮?6.4 閫昏緫鍍忕礌 > 4锛夈€傜暀鐫€瀹冩槸闃?                           //     "浠ュ悗鍔犱竴涓畾涔夊厓绱犲緢灏忕殑鍥惧舰"锛堟瘮濡備竴涓?鐐?锛夆€斺€旈偅鏃跺畠浼氱珛鍒荤敤涓婏紝
                           //     鑰屽畠鎸＄殑鏄?*鐢ㄦ埛鐨勫ⅷ鍑┖娑堝け**锛屼笉鏄渷涓€琛屼唬鐮佺殑浜嬶紱
                           //   路 鍥惧舰宸ュ叿锛氱収鏃ц"鎷栧闀?锛堥槻璇偣銆侀槻鐢诲嚭閫€鍖栫殑闆堕潰绉浘褰?                           //     鈥斺€旈偅绉嶄笢瑗跨湅涓嶈鍗寸偣寰椾腑锛屾槸鏈€闅捐В閲婄殑涓€绫绘潅鐗╋級銆?                           : (!IsShapeTool(ActiveStroke.Tool) && _dwellInk == null)
                             || _dwellInk != null
                             || ShapeDragLongEnough(ActiveStroke));

            // **鍙栨秷閫変腑閭ｄ竴鍑讳笉鐣欏ⅷ**锛堢敤鎴?2026-09-23 瀹氾級銆?            //
            // 璧峰洜锛氬仠椤垮彉鍑烘潵鐨勫浘褰㈡槸鑷姩閫変腑鐨勶紝鑰?鐐逛竴涓嬪埆澶?鐨勬剰鍥炬槸鏀惰捣閭ｄ釜妗嗐€?            // 閭ｄ竴鍑诲師鏈細钀?*涓€涓疄蹇冨渾鐐?*锛坄Stroke.IsSinglePoint`锛夛紝浜庢槸"鍙栨秷閫変腑"
            // 杩欎釜绾补鐨勫姩浣滀細鍦ㄥ睆骞曚笂鐣欎笅澧ㄢ€斺€?*姣忔鍙栨秷閮借剰涓€鍧?*锛屾澘涔︽椂鐗瑰埆鐑︺€?            // 鍒ゆ嵁鏄?鏁存浣嶇Щ 鈮?DwellTapSlopLogical"锛屾墍浠ョ湡鐢讳竴绗旂粷涓嶄細琚悆鎺夈€?            //
            // 鈿?**鍋滈】鎴愬瀷閭ｄ竴绗斾笉鍦ㄨ繖鏉￠噷**锛坄_dwellInk == null` 鎵嶇畻锛夛細瀹冩棦鐒惰璇嗗埆鍣?            //   璁ゅ嚭鏉ヤ簡锛屽氨缁濅笉鍙兘鏄?鐐逛竴涓?锛堣瘑鍒櫒瑕佹€婚暱 鈮?40 閫昏緫鍍忕礌锛夛紱鑰岃繖閲岄噺鐨勬槸
            //   **鍥惧舰**鐨勯鏈袱鐐?鈥斺€?鍦嗙殑瀹氫箟鍏冪礌鏄紙鍦嗗績锛屽渾鍛ㄧ偣锛夛紝棣栨湯涓ょ偣璺濈灏辨槸**鍗婂緞**锛?            //   鍗婂緞灏忎簬鐐归€夊宸紙8 閫昏緫鍍忕礌锛夌殑鍦嗕細姝ｅソ钀借繘鏉ワ紝鏁寸瑪锛堝浘褰?锛?鎵嬬粯鍘熻抗锛?            //   琚綋鎴?鐐逛竴涓?鎶规帀銆傝€岃繖鏉?*澶熷緱鐫€**锛氬渾鐨勮瘑鍒棬妲涙槸 2蟺r 鈮?40 鈫?r 鈮?6.4 閫昏緫
            //   鍍忕礌锛屾墍浠?6.4~8 杩欎竴娈电殑鍦嗙湡浼氳璇潃锛坄--dwelltest` H4 鐢ㄥ崐寰?7.5 閫昏緫鍍忕礌鐨勫渾
            //   閽変綇锛涙妸閭ｅ崐鍙ュ垹鎺夊畠褰撳満绾細"鏁寸瑪娌′簡"锛夈€?            if (commit && _dwellInk == null && _dismissTapArmed && !IsShapeTool(ActiveStroke.Tool)
                && _stepPlan == null && DwellTapLeaveNoInk(ActiveStroke))
            {
                commit = false;
                Console.WriteLine("[鍋滈】鎴愬瀷] 杩欎竴涓嬪彧鏄彇娑堥€変腑锛屼笉鐣欏ⅷ");
            }

            // 杩欎竴娆℃彁浜ょ殑鏄笉鏄竴涓?*鍒氭垚鍨嬬殑鍥惧舰**锛堣涓嶈鑷姩閫変腑瀹冿紝瑙佷笅闈?鐢诲畬鑷姩閫変腑"锛?            Stroke committedShape = null;
            if (commit)
            {
                // **鍙屾洸绾跨殑娓愯繎绾挎寜妗ｆ敹鍙?*锛堢敤鎴?2026-09-22锛?鍖栫殑鏃跺€欐槸閮芥湁娓愯繎绾匡紝
                // 浣嗘槸鏈€缁堟樉绀烘病鏈?锛夛細鐢荤殑杩囩▼涓竴鐩存槸"鏈?锛堥偅鏄悜瀵硷紝瑙?BeginShapeAt锛夛紝
                // 浜ゅ埌鏂囨。閲岀殑杩欎竴鍒绘墠鎸夐潰鏉块偅涓€妗ｅ喅瀹?鈥斺€?浜庢槸"鏃犳笎杩戠嚎"閭ｄ竴妗?                // 鐢荤殑鏃跺€欑収鏍风湅寰楀埌閭ｄ袱鏉¤櫄绾匡紝鐢诲畬灏卞彧鍓╂洸绾裤€?                // 鈿?蹇呴』璧?`SetShowAsymptotes`锛堝畠浼氭姮 Revision锛夆€斺€旂洿鎺ョ粰瀛楁璧嬪€肩殑璇濓紝
                // 灞忓箷涓婇偅浠?甯︽笎杩戠嚎"鐨勮緟鍔╁嚑浣曠紦瀛樺湪閭ｅ効涓嶉噸寤猴紝鐢诲畬鐓ф牱鐪嬪緱瑙侀偅涓ゆ潯铏氱嚎
                //锛堢敤鎴?2026-09-22 鎶ョ殑灏辨槸杩欎釜锛?鎴戦€夋嫨鐨勪笉甯︽笎杩戠嚎鐨勶紝浣嗘槸鐢诲畬浠ュ悗杩樻湁娓愯繎绾?锛夈€?                if (ActiveStroke.Kind == StrokeKind.Hyperbola)
                    ActiveStroke.SetShowAsymptotes(HyperbolaAsymptotes);
                if (_dwellInk != null)
                {
                    // 鍋滈】鎴愬瀷锛氳繖涓€绗斾笉鍐嶆槸"姝ｅ湪鍐?锛堟洸绾垮寲鎭㈠鐢熸晥锛夈€傚嵆浣垮畾鍨嬫垚鐨勬槸
                    // 鑷敱绗旇抗锛坄_dwellInk` 閭ｆ潯璺級锛屼篃涓嶈鍐嶅甫鐫€"娲荤瑪"鐨勬爣蹇椼€?                    ActiveStroke.RawWhileLive = false;
                    // **鍋滈】鎴愬瀷**锛氳蛋"鏇挎崲鍨?鎻愪氦鈥斺€斿浘褰㈣繘鏂囨。锛屾墜缁樺師杩硅窡鐫€鎾ら攢鏍堣蛋锛?                    // 浜庢槸鎸変竴娆?Ctrl+Z 鍥炲埌**鑷繁鐢荤殑閭ｄ竴绗?*锛堣 DwellShapeAction锛夈€?                    //
                    // 鈽?**涓ょ瑪鎴愬瀷**锛坄_twoBranchPrev != null`锛岀敤鎴?2026-09-25 瀹氱殑
                    //   "**鐢讳袱鏀氨鏄弻鏇茬嚎**"锛夛細杩欐椂**涓婁竴绗斾篃瑕佹敹璧?* 鈥斺€?                    //   `RemoveStroke` 鏄?鏁堟灉鍏堝簲鐢?锛岃璐﹀叏鐢变笅闈㈤偅鏉″姩浣滆礋璐?                    //   锛堝畠甯︿袱绗斿師杩癸紝鎵€浠?**涓€姝?Ctrl+Z 鍥炲埌涓ょ瑪鎵嬬粯**锛夈€?                    if (_twoBranchPrev != null)
                    {
                        var prev = _twoBranchPrev;
                        int prevIdx = _twoBranchIndex;
                        _twoBranchPrev = null;
                        _twoBranchIndex = -1;
                        // 鈿?**蹇呴』鍏堟妸闅愯棌浣嶆竻鎺?*锛氫笉娓呯殑璇?Ctrl+Z 鎶婅繖涓€绗旀斁鍥炴潵鏃?                        //   瀹冭繕鏄殣韬殑 鈥斺€?灞忓箷涓?鎾ら攢涔嬪悗浠€涔堥兘娌″洖鏉?锛岃€屼笖鍐嶄篃鍙樹笉鍥炴潵 鉁?                        //   锛堣蛋 `SetPairHidden` 鑰屼笉鏄洿鎺ヨ祴鍊硷細瀹冧細鎶?閭ｄ竴绗斿師鏉ュ緟鐨勫潡"
                        //     涓€璧锋爣鑴忥紝瑙侀偅涓嚱鏁扮殑娉ㄩ噴锛?                        SetPairHidden(prev, false);
                        Doc.RemoveStroke(prev);
                        Doc.AddDwellShape(ActiveStroke, _dwellInk, prev, prevIdx);
                    }
                    else
                    {
                        Doc.AddDwellShape(ActiveStroke, _dwellInk);
                        // **鎶?杩欎竴鏀殑鍘熻抗"鐣欎竴涓紩鐢?*锛堣 `_lastDwellShape` 閭ｆ娉ㄩ噴锛夛細
                        // 鐢诲弻鏇茬嚎鐨勭涓€鏀椂鎵嬭鏄仠浜嗕竴涓嬶紝瀹冨氨鍏堝彉鎴愪簡涓€涓?*鎶涚墿绾垮璞?*锛?                        // 鑰屽璞￠噷鎷夸笉鍥為噰鏍风偣 鈫?涓ょ瑪閰嶅灏辨柇浜嗐€傚彧璁?*鎶涚墿绾?*杩欎竴绉嶏細
                        // 閭ｆ槸"鐢讳竴鏀弻鏇茬嚎琚鍒ゆ垚鎶涚墿绾?鐨勭湡瀹炴儏褰紱鍒殑绉嶇被锛堝渾 / 妞渾鈥︼級
                        // 涓嶅湪杩欓噷鏀捐 鈥斺€?瀹冧滑鐨勫舰鐘跺拰"涓€鏀弻鏇茬嚎"宸お杩滐紝璇厤鐨勪唬浠锋洿澶с€?                        if (ActiveStroke.Kind == StrokeKind.Parabola)
                        {
                            _lastDwellShape = ActiveStroke;
                            _lastDwellInk = _dwellInk;
                        }
                    }
                    _dwellCommitted = true;
                    // **鐩寸嚎鎶墜鍚庝笉閫変腑**锛堢敤鎴?2026-09-23 瀹氾級锛氱洿绾挎槸"椤烘墜涓€鍒?锛岀敾瀹岃绔嬪埢鎺ョ潃
                    // 鍐欎笅涓€绗斺€斺€斿脊鍑轰竴涓€変腑妗?+ 鎿嶄綔鏉″弽鑰屾尅璺紱鎸変綇鏈熼棿宸茬粡鑳借皟锛堣浆鍚?浼哥缉锛夛紝
                    // 鎶墜涔嬪悗鎯冲啀璋冨氨鍐嶇偣瀹冧竴涓嬶紙鍜?GoodNotes"鏉炬墜鍚庣偣涓€涓嬫墠閫変腑"鍚屼竴涓墜鎰燂級銆?                    // 鈿?杩欎竴鏉′緥澶?*鍙湪杩欐潯璺笂**锛氬浘褰㈠伐鍏风敾鐨勭洿绾跨収鏃ц嚜鍔ㄩ€変腑锛堥偅鏄棦鏈夎璁★級銆?                    if (ActiveStroke.Kind != StrokeKind.Line) committedShape = ActiveStroke;
                    Console.WriteLine($"[鍋滈】鎴愬瀷] 瀹氬瀷 {ActiveStroke.Kind}"
                                      + (committedShape != null ? "锛堝凡閫変腑锛汣trl+Z 鍙洖鎵嬬粯锛? : "锛堟湭閫変腑锛汣trl+Z 鍙洖鎵嬬粯锛?));
                }
                else
                {
                    // 钀界瑪锛氫粠杩欎竴鍒昏捣鏇茬嚎鍖栫敓鏁堬紙鍑犱綍缂撳瓨閿噷甯︾潃杩欎釜鏍囧織锛屼細鑷繁閲嶇敾锛?                    ActiveStroke.RawWhileLive = false;
                    Doc.AddStroke(ActiveStroke);
                    // 鐪熸彁浜よ繘鏂囨。浜嗘墠绠?鎴愬瀷"鈥斺€斾笅闈㈤偅涓€姝ヨ鎷垮畠鍋氳嚜鍔ㄩ€変腑銆?                    if (IsShapeTool(ActiveStroke.Tool)) committedShape = ActiveStroke;
                }
                // **鍏堢粨璐﹀啀鎵撳嵃**锛氳繖涓€娈碉紙鍚?AddStroke 鐨勫揩鐓?缂撳瓨鏀舵嬀锛夋墠鏄垎閰嶆渶闆嗕腑鐨勫湴鏂癸紝
                // 鏅氫竴姝ユ敹浠〃锛屽嵃鍑烘潵鐨勫氨鏄笂涓€绗旂殑鏁板瓧銆?                EndStrokeMeasure();
                // 杩欎竴绗旂殑**鍘嗘椂 + 鐪熷疄閲囨牱鐜?*锛?026-10-07 鍔狅紝10-07 鏅氫慨姝ｅ彛寰勶級锛?                // "鏀跺埌 12 涓偣"鏈韩璇存槑涓嶄簡浠讳綍浜嬧€斺€?2 涓偣鎽婂湪 0.1 绉掍笂鏄?120Hz锛堟甯革級锛?                // 鎽婂湪 0.8 绉掍笂灏卞彧鏈?15Hz锛堝紓甯革級銆傛帓鏌?鐐规槸涓嶆槸琚█鐤忎簡"蹇呴』鍏堟湁杩欎釜鍒嗘瘝銆?                //
                // 鈿?**鍙ｅ緞蹇呴』鏄?閲囨牱鐐?绉?锛屼笉鏄?娑堟伅/绉?**锛氬紑 Windows Ink 鏃剁郴缁熶細鎶?                //   2~3 涓‖浠堕噰鏍峰悎骞惰繘涓€鏉℃秷鎭紙鐪熸満瀹炴祴 12 鏉℃秷鎭?鈫?31 涓噰鏍风偣锛夛紝
                //   鎷挎秷鎭暟褰撻鐜囦細**鎶婄瑪鐨勯噰鏍风巼鎶ヤ綆涓€鍗婁互涓?*锛堢涓€鐗堝氨鏄繖涔堝啓鐨勶級銆?                //   鍚屾椂鎶婂悎骞舵瘮鎵撳嚭鏉モ€斺€斿畠鏈韩灏辫鏄?杩欎竴璺埌搴曟湁娌℃湁鍚堝苟鍙仮澶?銆?                double strokeMs = NowMs - (ActiveStroke.Points.Count > 0 ? ActiveStroke.Points[0].T : NowMs);
                double sampleHz = strokeMs > 1 ? LastCoalescedSamples * 1000.0 / strokeMs : 0;
                double msgHz = strokeMs > 1 ? LastCoalescedMessages * 1000.0 / strokeMs : 0;
                string merge = LastCoalescedMessages > 0
                    ? (LastCoalescedMessages == LastCoalescedSamples ? "1:1锛堢郴缁熸病鍚堝苟锛?
                       : $"{(double)LastCoalescedSamples / LastCoalescedMessages:F1}:1锛堝悎骞跺巻鍙插凡鎭㈠锛?)
                    : "鏃?;
                // **杩欎竴绗旂湡姝ｅ啓杩涚偣閲岀殑鍘嬪姏鍖洪棿**锛?026-10-07 鍔狅級銆?                //
                // 涓轰粈涔堝繀椤绘湁瀹冿細閭ｅぉ鎴戣繛鐫€鐘袱娆″悓绫荤殑閿?鈥斺€?                //   鈶?鍏滃簳鍊煎彇 0.5 鈫?寮€澶撮紦绮楃偣
                //   鈶?raw 琛ョ偣鍐欐垚 0 鈫?寮€ ink 鏃舵暣鏉＄嚎鍏ㄦ槸缁嗗彛瀛愶紙鐢ㄦ埛锛?绗旇寰堣剰"锛?                // 涓ゆ閮?*鍙湁涓€涓暟瀛楄兘鐪嬪嚭鏉?*锛氱偣鐨勫帇鍔涙渶灏忓€?鏈€澶у€笺€?                // 鍏夌湅 `[wintab 鈥` 閭ｆ爮鏄笉澶熺殑 鈥斺€?閭ｆ槸"閲囧埌鐨勫帇鍔?锛屼笉鏄?鍐欒繘鐐归噷鐨?銆?                // **閲囧埌浜?*鍜?*鐢ㄤ笂浜?*鏄袱浠朵簨锛屼腑闂翠换浣曚竴娈靛嚭閿欏畠閮界湅涓嶈銆?                float ptMinP = 0f, ptMaxP = 0f;
                int ptSteps = 0;                       // 鍘嬪姏"璺冲彉"娆℃暟锛堢浉閭荤偣鍘嬪姏涓嶅悓鐨勬鏁帮級
                if (ActiveStroke.Points.Count > 0)
                {
                    ptMinP = float.MaxValue; ptMaxP = float.MinValue;
                    foreach (var q in ActiveStroke.Points)
                    {
                        if (q.P < ptMinP) ptMinP = q.P;
                        if (q.P > ptMaxP) ptMaxP = q.P;
                    }
                    for (int i = 1; i < ActiveStroke.Points.Count; i++)
                        if (MathF.Abs(ActiveStroke.Points[i].P - ActiveStroke.Points[i - 1].P) > 0.0005f) ptSteps++;
                }
                _lastStrokeReport =
                    $"璺緞={DescribeInputPath(_activePointerType)}"
                    + $"銆€閲囬泦鍒?{ActiveStroke.Points.Count} 涓偣"
                    + $"锛屽巻鏃?{strokeMs:F0} ms 鈫?閲囨牱 {sampleHz:F0} Hz锛堟秷鎭?{msgHz:F0} Hz锛?
                    + $"锛屾敹鍒?鎸変笅{_cntDown} 绉诲姩{_cntMove} 鎶捣{_cntUp} 涓㈠け鎹曡幏{_cntCaptureLost}"
                    + $"锛岃澶?{PointerTypeName(_activePointerType)}"
                    + $"锛屽帇鎰?{(ActiveStrokeHasPressure ? "鏈? : "鏃?)}"
                    // 鐪熸钀藉埌鐐归噷鐨勫帇鍔涘尯闂淬€?*鏈夊帇鎰熶絾 min 鏄?0** 灏辫鏄庢湁璺妸鐐瑰啓鎴愪簡 0
                    // 锛?026-10-07 閭ｄ釜"绗旇寰堣剰"鐨?bug 姝ｆ槸杩欐牱锛歳aw 琛ョ殑鐐瑰叏琚啓鎴?0锛夈€?                    + (ActiveStroke.Points.Count > 0
                        ? $"锛岀偣鍘嬪姏 {ptMinP:F2}~{ptMaxP:F2}锛堣烦鍙?{ptSteps}/{ActiveStroke.Points.Count} 鐐癸級"
                          + (ActiveStrokeHasPressure && ptMinP <= 0.001f ? " **鏈?锛佺枒浼兼湁璺妸瀹冨啓鎴?**" : "")
                        : "")
                    // 鍙湪**鐪熺殑寮€鐫€**鏃舵墠鎶ヨ繖涓€鏍忋€?                    // 娌¤鍘傚晢椹卞姩锛堟病鏈?Wintab锛夌殑鏈哄櫒涓婏紝鍚姩鏃跺凡缁忔墦杩囦竴琛?                    // 銆孾wintab] 鏈惎鐢細鈥︺€嶏紝鍐嶆瘡涓€绗旈兘鎶?鏈紑"灏辨槸鍣煶浜嗐€?                    + (_wintab.IsOpen
                        ? $"锛孾wintab 鍖厈WtPolledPackets} 琛ュ帇{WtPressurePoints}/{ActiveStroke.Points.Count}鐐?
                          + (WtRawPMax >= 0 ? $" 鍘嬪姏{WtRawPMin}~{WtRawPMax}/{_wintab.MaxPressure}" : " **涓€绗旈兘娌℃嬁鍒板帇鍔?*")
                          + $" {_wintab.LayoutText}"
                          + (WtBadPackets > 0 ? $" **瓒婄晫{WtBadPackets}**" : "")
                          + "]"
                        : "")
                    + $"锛屽悎骞?{merge}锛坽LastCoalescedMessages} 鏉℃秷鎭?鈫?{LastCoalescedSamples} 鐐癸級"
                    // 濮旀墭杞ㄨ抗锛?026-10-11锛夛細**鍠傝繘鍘?鈮?鐣欏湪绯荤粺閲?*鈥斺€旈粯璁ゆā寮忎笅鎴戜滑鑷繁
                    // 鐢诲埌鍝氨鎶婅建杩圭粨璐﹀埌鍝紝鎵€浠?缁撹处"搴斿綋鈮?鍠?锛涘彧鍠備笉缁撹处锛堢粨璐﹁繙灏忎簬鍠傦級
                    // 灏辨槸"闀跨瑪鐢讳腑閫斿彉閫忔槑"鐨勯偅绉嶇梾銆俙--trailonly` 涓嬫垜浠嚜宸变笉鐢汇€佷篃灏变笉缁撹处
                    //锛堥偅姝ｆ槸"婀垮ⅷ鍙墿绯荤粺杞ㄨ抗"鐨勮瘖鏂ā寮忥紝缁撹处 0 鏄鏈燂級銆?                    + (OverlayWindow.InkTrailEnabled
                        ? $"锛孾杞ㄨ抗 鍠倇OverlayWindow.InkTrailPointsFed - _trailFedSeen}"
                          + $" 缁撹处{OverlayWindow.InkTrailPointsRemoved - _trailRemovedSeen}]"
                        : "")
                    + (RawProbeEnabled
                        // 鏃堕暱鈮? 鐨勭┖绗旓紙閫€鍑烘敹灏剧粨绠楋級涓嶆姤棰戠巼锛歚鏉℃暟 / 1ms` 浼氬嵃鍑?                        // "14000 Hz" 杩欑鍋囨暟锛?026-10-08 鐢ㄦ埛涓や唤鏁版嵁缁撳熬閮芥湁锛夈€?                        ? $"锛孾鍘熷杈撳叆 {_rawReportsThisStroke} 鏉?鈫?" + (strokeMs > 1
                            ? $"{_rawReportsThisStroke * 1000.0 / strokeMs:F0} Hz]"
                            : "鈥旓紙鏃堕暱鈮?锛塢")
                        : "")
                    + (RawInputCapture
                        ? $"锛孾raw 琛ョ偣 +{_rawPointsAdded}]"
                          + (RawProbeEnabled
                             ? $"[鎸★細鏃犵瑪{_rawSkipNoStroke} API{_rawSkipApi} 绫诲瀷{_rawSkipType} 缁濆{_rawSkipAbs} 闆剁Щ{_rawSkipZero}"
                               + $" 闈炴墜鍐檣_rawSkipKind} 闂▄_rawSkipGate} 鏃犻敋{_rawSkipAnchor} 鏃犱腑闂寸偣{_rawSkipNoInter} 涓婇檺{_rawSkipCap} 澶繎{_rawSkipNear}]"
                             : "")
                        : "")
                    // [鍒犻櫎 2026-10-05] 棰勬祴鍣?棰勬祴灏?鍠?DWM 娈垫暟鐨勬棩蹇楋細闅忛娴嬬郴缁熶竴璧风Щ闄ゃ€?                    // 鍒嗛厤涓?GC锛氫綆閰嶆満鎺掓煡"鍋跺彂鍗￠】"鐨?*鍞竴渚濇嵁**銆?                    // 绗?2 浠ｉ偅涓€浣嶅嚭鐜板湪涔﹀啓鏈熼棿锛屽氨璇存槑杩欎竴绗旂敾鍒颁竴鍗婅鍏ㄥ爢鍥炴敹鎵撴柇杩囥€?                    + $"锛屽垎閰?{StrokeAllocBytes / 1024.0:F1} KB/GC {StrokeGc0}/{StrokeGc1}/{StrokeGc2}";
                Console.WriteLine("[绗旂敾] " + _lastStrokeReport);
                WintabSelfCheckAfterStroke(_activePointerType);
                // `--strokefile`锛氭妸杩欎竴琛?*杩藉姞鍒版枃浠?*銆?                // 涓轰粈涔堣鏈夊畠锛氳繖涓€琛岀殑鏁板瓧锛堝挨鍏?鍘嬪姏 a~b/涓婇檺"锛夋槸璋冩墜鎰熷弬鏁扮殑**鍞竴渚濇嵁**锛?                // 鑰屾埅鍥句細鎶樿銆佷細琚瑪杩规尅浣忋€佷細杞堪閿欌€斺€?*浠婂ぉ宸茬粡鍥犳鏍戒簡涓夋**銆?                // 榛樿鍏筹紙涓嶅啓鏂囦欢銆侀浂寮€閿€锛夛紝瑕佽瘖鏂椂鎵嶅紑銆?                if (_strokeFilePath.Length > 0)
                {
                    try
                    {
                        System.IO.File.AppendAllText(_strokeFilePath,
                            $"[{DateTime.Now:HH:mm:ss}] {_lastStrokeReport}{Environment.NewLine}",
                            new System.Text.UTF8Encoding(true));
                    }
                    catch (Exception ex) { Console.WriteLine($"  鈿?鍐欑瑪鐢绘棩蹇楀け璐ワ細{ex.Message}"); }
                }
                if (StrokeGc2 > 0)
                    Console.WriteLine($"  鈿?杩欎竴绗旀湡闂村彂鐢熶簡 {StrokeGc2} 娆＄ 2 浠?GC"
                                      + "锛堜綆閰嶆満涓婅繖灏辨槸涓€娆″彲瑙佺殑鍗￠】锛屽€煎緱鏌ユ槸鍝噷鍦ㄥ垎閰嶏級");
            }
            else if (!stepPending && IsShapeTool(ActiveStroke.Tool) && _stepPlan == null)
            {
                Console.WriteLine($"[鍥惧舰] 鎷栧姩澶煭锛? {ShapeMinDragLogical:F0} 閫昏緫鍍忕礌锛夛紝娌℃彁浜?);
            }
            // 澶氱瑪寮忔病璧板畬锛?*杩?ActiveStroke 閮戒笉娓?*鈥斺€斿畠杩樿鎺ョ潃缁欎笅涓€绗斿綋棰勮鐢紱
            // 琛ㄥ拰"鐢诲埌绗嚑绗?涔熻窡鐫€鐣欏湪鍘熷湴锛堜笅涓€绗旀寜涓嬫椂瑕佺敤锛夈€?            if (!stepPending)
            {
                ActiveStroke = null;
                _stepPlan = null;
                _stepIndex = 0;
            }

            // **鐢诲畬鑷姩閫変腑**锛堢敤鎴?2026-09-22 瀹氾級锛氬浘褰竴鎴愬瀷灏辨妸瀹冪殑閫変腑妗嗕寒鍑烘潵锛?            // 鑰佸笀鍙互绔嬪埢鎷栧畠銆佹嫋椤剁偣鏀瑰舰鐘垛€斺€斾笉鐢ㄥ厛鍘荤偣"妗嗛€?鍐嶅洖澶寸偣瀹冧竴涓嬨€?            // 锛堟埅鍥惧拰绮樿创鏃╁氨鏄?钀戒笅灏遍€変腑"锛岃繖鏉℃妸鍥惧舰涔熸媺榻愪簡銆傦級
            //
            // **宸ュ叿涓嶆崲**锛堣繖涓€鐐瑰拰鎴浘 / 绮樿创涓嶅悓锛屽畠浠崲鎴愪簡妗嗛€夛紝鍥犱负"鎺ョ潃鐢讳笅涓€涓?
            // 瀵瑰畠浠笉瀛樺湪锛夛細鎵嬮噷杩樻槸鍘熸潵閭ｄ釜鍥惧舰宸ュ叿锛屾墍浠?杩欎竴涓嬬畻鍔ㄥ畠杩樻槸绠楁帴鐫€鐢?
            // 鐨勫垎娴佽惤鍦?`OnPointerDown`锛坄AutoSelectionPress`锛夈€?            //
            // 鑰屼笖鍒氱敾瀹屾槸**鏀惰捣鏉?*鐨勫舰鎬侊紙涓€棰楀渾閽級锛岀偣涓€涓嬪畠鎵嶅睍寮€鎴愬父瑙勯偅涓€濂?            // 鈥斺€旂悊鐢辫 `_autoSelCollapsed`锛堢敤鎴蜂笂鎵嬩箣鍚庡畾鐨勶細涓€鏁存潯鎽婂湪鍥惧舰涓嬮潰鍙堥噸鍙堟尅锛夈€?            if (committedShape != null)
            {
                Doc.SelectOnly(new[] { committedShape });
                _autoSelCollapsed = true;
                _dirty = true;
            }
            // **鍋滈】鍙樺嚭鏉ョ殑閭ｄ釜妗嗭紝宸ュ叿杩樻槸绗?* 鈥斺€?鎵€浠ヨ繕瑕佽涓€绗?杩欎竴娆￠€変腑
            // 鏄仠椤跨粰鐨?锛屽惁鍒欓偅涓浼?鐢诲緱鍑烘潵銆佺偣涓嶇潃"锛堣 `_dwellSelected`锛夈€?            // 鐩寸嚎涓婅繖涓€浣嶆槸 false锛堝畠鎶墜涓嶉€変腑锛岃涓婇潰锛夛細娌℃湁閫変腑妗嗭紝涔熷氨娌℃湁"鐐逛笉鐫€"鐨勯棶棰樸€?            _dwellSelected = committedShape != null && _dwellCommitted;
        }
        // 鍋滈】鎴愬瀷杩欎竴绗旂殑璐﹀湪杩欓噷缁撴竻锛氱姸鎬佹満澶嶄綅銆佸師杩瑰紩鐢ㄦ斁鎵?        //锛堝師杩瑰凡缁忎氦缁欐挙閿€鏍堜簡锛岃 DwellShapeAction 鈥斺€?杩欓噷鏀炬墜涓嶄細涓笢瑗匡級銆?        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        // 鎷栧姩**鎵嬫焺**锛堟敼鍑犱綍锛夌殑鏀跺熬锛?*鍜屽綋鍓嶅伐鍏锋棤鍏?*銆?        //
        // 鎸傚湪涓嬮潰"閫夋嫨宸ュ叿"閭ｆ潯鍒嗘敮閲屾槸涓嶅鐨勶細鎵嬫焺鎷栧姩灞炰簬**閫変腑**閭ｄ竴濂楋紝
        // 鑰屾澗鎵嬫椂鎵嬩笂鐨勫伐鍏峰彲鑳藉凡缁忎笉鏄€夋嫨宸ュ叿浜嗭紙鎷栧埌涓€鍗婃寜鐑敭鎹簡宸ュ叿锛?        // 瑙?SwitchTool鈥斺€斿畠鍙綔搴熷绗斿浘褰㈢殑鍗婃垚鍝侊紝涓嶅姩鎵嬫焺鎷栧姩锛夈€?        // 閭ｇ鎯呭喌涓嬭繖涓€鎷栦細**姘歌繙涓嶆彁浜?*锛氬睆骞曚笂鎷栧緱濂藉ソ鐨勶紝鏉炬墜涓€鐪?a 涓€鐐规病鍙?        //锛堣嚜妫€褰撳満鎶撳嚭鏉ヨ繃锛夈€?        //
        // 鈿?鍒ゆ嵁鏄?`SelDragging` 鑰?*涓嶆槸**"鏄笉鏄閫夊伐鍏?锛氱敾瀹岃嚜鍔ㄩ€変腑閭ｄ釜妗?        //锛堣涓婇潰 `AutoSelectionPress` 閭ｆ锛夋槸鍦?*鍥惧舰宸ュ叿**涓嬭捣鎵嬫嫋鐨勶紝
        // 鎸夊伐鍏峰垽鐨勮瘽杩欎竴鎷栧悓鏍锋案杩滀笉鎻愪氦銆?        if (SelDragging) EndSelDrag();
        else if (_sliderDragging) EndWidthSliderDrag();        // 绮楃粏婊戞潯锛氭澗鎵?= 涓€姝ユ挙閿€
        else if (_colorDragging) EndColorPickerDrag(PointerX, PointerY);   // 鍙栬壊鏉匡細鏉垮唴搴旂敤 / 鏉垮鍙栨秷
        else if (Tool == Tool.Marquee || MarqueeActive) ApplyMarquee();    // MarqueeActive锛氳Е鎽搁暱鎸夐偅鏉¤矾锛堝伐鍏峰彲鑳借繕鏄瑪锛?        EndStrokeMeasure();      // 鍏滃簳锛氭病鏀惰繃鐨勫垎鏀紙鍙栨秷銆佸垏鎹㈠伐鍏风瓑锛変篃鎶婃€昏处缁撴帀
        _drawing = false;
        _dirty = true;
        _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
        // 鎵嬫祴鍙帮細涓€娆℃嫋鎷芥敹灏俱€?*蹇呴』鍦?EndErase 涔嬪悗**鈥斺€旀挙閿€鏍堟繁搴﹁绠椾笂鍒氭彁浜ょ殑閭ｄ竴姝ャ€?        EraserTelemetry?.EndDrag(Doc, Doc.UndoDepth, NowMs);
    }

    private static string PointerTypeName(uint t) => t switch
    {
        Native.PT_MOUSE => "榧犳爣",
        Native.PT_TOUCH => "瑙︽懜",
        Native.PT_PEN => "绗?,
        _ => "鏈煡",
    };

    // =====================================================================
    //  鎸囬拡褰㈢姸锛堝厜鏍囷級
    //
    //  涓€鏉¤鍒欙細**姣忔鎸囬拡绉诲姩閮借鑳藉洖绛?杩欎釜浣嶇疆銆佽繖涓伐鍏枫€佽繖涓澶囷紝
    //  鐢ㄦ埛鎺ヤ笅鏉ヨ兘鍋氫粈涔?**銆傚洖绛斾笉浜嗗氨閫€鍒扮澶达紝缁濅笉鐣欑┖鈥斺€旂暀绌哄氨鏄?    //  绯荤粺鍏滃簳锛屼篃灏辨槸杩欐淇殑"姘歌繙杞湀"銆?    //
    //  浼樺厛绾э紙涓婇潰鐨勫帇涓嬮潰鐨勶級锛?    //    1. 绌块€忔ā寮?  鈫?涓嶆彃鎵嬶紝浜ょ粰涓嬪眰绐楀彛
    //    2. 瑙︽懜       鈫?涓嶆樉绀烘寚閽?    //    3. 鎷栨嫿涓?    鈫?鐢ㄦ寜涓嬮偅涓€鍒荤殑璇箟锛堟鏃剁郴缁熶笉鍐嶅彂 WM_SETCURSOR锛?    //    4. 婊氬姩鏉?/ 閫変腑妗嗘墜鏌?/ 鎿嶄綔鏉?    //    5. 宸ュ叿       鈫?鐢诲竷涓婄殑褰㈢姸
    // =====================================================================

    internal enum ToolCursorShape
    {
        None,
        /// <summary>鍦嗙幆锛氭鐨€佹墜鍐欑瑪鎮仠銆傜洿鎺ヨ〃杈?钀界偣鑼冨洿鍐呬細鎬庢牱"銆?/summary>
        Ring,
        /// <summary>
        /// 鐭╁舰锛氬儚绱犳鐨€傚畠鐨勮惤鐐?*灏辨槸涓€涓煩褰?*锛堢珫鐫€鐨勯粍閲戞瘮渚嬶級锛?        /// 鎵€浠ュ厜鏍囦笉璇ョ敾鎴愬渾鈥斺€旂敾鎴愬渾绛変簬鍛婅瘔鐢ㄦ埛涓€涓敊鐨勫舰鐘躲€?        /// </summary>
        Rect,
        /// <summary>
        /// 瀹炲績鍦嗙洏锛氳崸鍏夌瑪銆傝惤鐐瑰灏辨槸涓€涓洿寰?= 绗斿鐨勫渾鈥斺€斿崟鍑讳竴涓嬬暀涓嬬殑
        /// 澧ㄧ偣涔熸槸瀹冿紝鎵€浠?鍏夋爣"鍜?鐐逛笅鍘讳細寰楀埌浠€涔?鏄悓涓€涓舰鐘躲€?        /// </summary>
        Disc,
        /// <summary>瀹炲績鐐癸細婵€鍏夌瑪銆傛縺鍏夎〃杈剧殑鏄?鎴戣鐨勬槸杩欓噷"锛屼笉鏄?澶氬"銆?/summary>
        Dot,
    }

    private IntPtr _cursorApplied;
    private int _cursorSizePx, _cursorSizeDpi;

    /// <summary>
    /// 绯荤粺鎸囬拡鐨勭墿鐞嗗儚绱犲昂瀵革紙璺熼殢 DPI 涓庣敤鎴风殑"鎸囬拡澶у皬"杈呭姪鍔熻兘璁剧疆锛夈€?    /// 鏈満 200% 缂╂斁涓嬫槸 64銆傝嚜缁樺厜鏍囨寜瀹冪敓鎴愶紝鎵嶄笉浼氬湪楂樺垎灞忎笂绯婃帀銆?    /// </summary>
    internal int CursorSizePx
    {
        get
        {
            int dpi = Math.Max(96, (int)MathF.Round(96f * DpiScale));
            if (_cursorSizePx == 0 || dpi != _cursorSizeDpi)
            {
                int v = Native.GetSystemMetricsForDpi(Native.SM_CXCURSOR, (uint)Math.Min(dpi, 768));
                _cursorSizePx = v > 0 ? v : (int)(32 * DpiScale);
                _cursorSizeDpi = dpi;
            }
            return _cursorSizePx;
        }
    }

    /// <summary>褰撳墠璇ユ樉绀轰粈涔堝厜鏍囥€傜函鍑芥暟锛堜笉纰扮郴缁燂級锛屾墍浠ヨ兘鎷挎潵鍋氳嚜妫€銆?/summary>
    internal CursorKind ComputeCursorKind()
    {
        // 鎸囬拡鍋滃湪**鐣岄潰鍧?*涓婏紙鎺ヨ緭鍏ュ皬绐?/ 鍥惧簱闈㈡澘杩欑被鑷粯鐣岄潰锛夛細缁欑澶淬€?        // 鐣岄潰涓婄殑鎸夐挳涓嶈椤剁潃涓€涓瑪灏栧湀/姗＄毊鍦堚€斺€旈偅涓€鍦堟槸"钀界偣鍙嶉"锛?        // 鍙鐢诲竷鏈夋剰涔夈€?        if (!_drawing && (_uiHover || PointerOnDrawnChrome())) return CursorKind.Default;

        // 鍥炴斁涓竴寰嬬澶达細鐢诲竷閭ｄ竴涓嬪彧鏄?鏆傚仠/缁х画"锛屼笉璇ラ《鐫€绗斿皷鍦?姗＄毊鍦堛€?        if (_replay != null) return CursorKind.Default;

        if (PassThrough) return CursorKind.Leave;                 // 璋佹潵鎺ョ鐢辩郴缁熷喅瀹?        if (LastPointerType == Native.PT_TOUCH) return CursorKind.Hidden;

        if (_drawing)
        {
            // 鎷栨嫿涓笉閲嶆柊鍋氬懡涓祴璇曪細鎸囬拡鏃╁氨绂诲紑鎵嬫焺浜嗭紝閲嶆祴浼氳鍏夋爣鍦ㄥ崐璺彉鍥炲幓銆?            // **涓嶅啀闂?鏄笉鏄閫夊伐鍏?**锛氬浘褰㈠伐鍏蜂笅涔熷彲鑳芥鍦ㄦ嫋閭ｄ釜鐢诲畬鑷姩鍑虹幇鐨勬
            //锛堣 `OnPointerDown` 閲?`AutoSelectionPress` 閭ｄ竴娈碉級銆?            if (SelDragging)
                return _dragIsMove ? CursorKind.Move : HandleCursor(_dragHandle);

            // **鎸夊湪鎿嶄綔鏉?/ 闈㈡澘 / 鍦嗛挳涓婄殑閭ｄ竴涓嬶細淇濇寔绠ご**锛堢敤鎴?2026-09-27 鎶ョ殑
            // "鎮诞杩囧幓鏄紶鏍囷紝鐐逛竴涓嬪畠鍙堝彉鎴愬崄瀛椾簡"锛夈€?            //
            // 鏍瑰洜锛歚OnPointerDown` 鍦?杩欎竴涓嬪綊璋?鍒嗘祦**涔嬪墠**灏辫浜?`_drawing = true`
            // 骞惰皟浜嗕竴娆?ApplyCursor锛堣閭ｉ噷鐨勬敞閲婏級锛屼簬鏄寜涓嬬殑閭ｄ竴甯у厜鏍囪惤鍒?            // ToolCursorKind鈥斺€旀閫夊伐鍏蜂笅灏辨槸鍗佸瓧锛涚瓑鍒嗘祦璧板畬锛堢偣涓寜閽級宸茬粡鏅氫簡锛?            // 鍏夋爣閭ｄ竴涓嬬殑璺冲彉鐢ㄦ埛鐪嬪緱瑙併€?            //
            // 鍒ゆ嵁**澶嶇敤 SelectionCursor**锛?涓€鍧楁槸鐣岄潰灏辨槸鐣岄潰"鐨勫敮涓€鍒ゆ嵁锛岃瀹冪殑娉ㄩ噴锛夛細
            // 瀹冪粰 Default 鐨勫湴鏂癸紙鏉?/ 闈㈡澘 / 鍦嗛挳锛夊氨鏄晫闈㈡搷浣滐紝鍏夋爣鍒姩鈥斺€斾笉鍙﹀垪鍚嶅崟銆?            // 鏀捐繃涓ょ鎯呭舰锛氭閫夋嫋鍔ㄤ腑锛圡arqueeActive锛氭缁忚繃鏉＄殑涓婃柟鏃朵篃璇ヤ繚鎸佸崄瀛楋級
            // 鍜屾墜鏌?/ 妗嗗唴鎷栧姩锛堥偅绉嶆寜涓嬪綋甯у氨杩涗簡 SelDragging锛岃蛋涓婇潰閭ｆ潯锛夈€?            if (!MarqueeActive && SelectionBarShown
                && SelectionCursor(PointerX, PointerY) == CursorKind.Default)
                return CursorKind.Default;

            return ToolCursorKind;
        }

        // 婊氬姩鏉★細涓嶆崲鍏夋爣銆傛墜鍨嬫寜瑙勮寖鍙兘琛ㄧず閾炬帴锛涘洓鍚戠澶村張浼氳璇绘垚"绉诲姩/鎷変几"銆?        // "鑳芥嫋"杩欎欢浜嬩氦缁欐粦鍧楄嚜宸辩殑鎮仠鍙嶉锛堝彉绮椼€佸彉娣憋級鍘昏銆?        if (ScrollBarHover) return CursorKind.Default;

        // 閫変腑妗嗛偅涓€濂楋細妗嗛€夊伐鍏蜂笅鏁翠釜妗嗛兘绠楋紱**鍥惧舰宸ュ叿涓嬪彧鏈夋鐨勫鍏?/ 鍥惧舰鑷繁閭ｆ潯澧ㄧ畻**
        //锛堝埆澶勪竴鎸夋槸鎺ョ潃鐢讳竴绗旓紝鍏夋爣灏辫鏄敾鐨勫崄瀛椻€斺€斿垽鎹湪 SelectionInteractiveAt锛夈€?        if (SelectionInteractiveAt(PointerX, PointerY))
        {
            var k = SelectionCursor(PointerX, PointerY);
            if (k.HasValue) return k.Value;      // null = 杩欎竴甯︽病鏈夌壒娈婅涔夛紝浜ょ粰宸ュ叿
        }

        return ToolCursorKind;
    }

    /// <summary>
    /// 鎸囬拡姝ｈ惤鍦?*寮曟搸鑷粯鐨勭晫闈㈠潡**锛堝浘搴撻潰鏉?/ PPT 鏉￠偅涓€鏃忥級涓婂悧銆?    ///
    /// 涓轰粈涔堝崟鍒楁垚涓€鏉″垽鎹紙2026-09-27 淇級锛氳繖浜涙诞灞傞兘涓嶅湪"鎺ヨ緭鍏ュ皬绐?閲岋紝瀹冧滑鑷繁鐨?    /// 鎮仠鍒嗘敮鍙皟 ApplyCursor銆?*娌℃湁浠讳綍鍦版柟鏇存柊"鎸囬拡鍦ㄧ晫闈笂"杩欎釜鐘舵€?*锛屼簬鏄悓涓€鍧?    /// 闈㈡澘涓婄殑鍏夋爣鍙栧喅浜庢潵璺紙浠庡伐鍏锋潯杩囨潵鏄澶淬€佷粠鐢诲竷鐩村叆鏄伐鍏峰厜鏍囷級銆?    /// 淇硶涓嶆槸"椤烘墜鎶?_uiHover 涔熺疆涓?锛堥偅鏄帴杈撳叆灏忕獥鐨勭姸鎬侊紝娣风潃鐢ㄦ棭鏅氬啀鍑洪敊锛夛紝
    /// 鑰屾槸鎶婅繖涓嚑浣曞垽鎹?*鏀舵垚涓€鏉?*锛氬厜鏍囷紙<see cref="ComputeCursorKind"/>锛夊拰
    /// 钀界偣鍙嶉锛?see cref="DrawnCursor"/>锛夐兘闂畠鈥斺€?涓€鍧楁槸鐣岄潰灏辨槸鐣岄潰"锛屽悕鍗曞彧鍐欎竴澶?    /// 锛堟暀璁 鏋舵瀯-鍒嗗眰涓庤鍒?md 浜?7锛夈€?    ///
    /// 鍚嶅崟锛氬浘搴撻潰鏉匡紙鐢诲竷鍧愭爣锛? PPT 鏉?闀挎寜鑿滃崟/椤靛彿闈㈡澘锛?*鐗╃悊灞忓箷鍧愭爣**锛?    /// 鍊?`PptBarContains` 閭ｄ唤"绌块€忚眮鍏嶄笌鍛戒腑"鐨勭幇鎴愬垽鎹€斺€斿畠浠湰鏉ュ氨鏄?涓€鍧?锛夈€?    /// </summary>
    private bool PointerOnDrawnChrome()
    {
        if (LibraryPanelOpen && LibraryLayout.Contains(LibraryPanelRectNow(), PointerX, PointerY))
            return true;
        // 鐢诲竷鍧愭爣 鈫?灞忓箷鍧愭爣鍙樊涓€涓瀭鐩存粴鍔ㄩ噺锛圫creenToCanvas 灏辨槸 `y -= ViewOffsetY`锛夛紝
        // x 娌℃湁婊氬姩銆佺洿鎺ョ敤銆?        return PptBarContains(PointerX, PointerY + ViewOffsetY)
            || TimerCardContains(PointerX, PointerY + ViewOffsetY)
            || RollCardContains(PointerX, PointerY + ViewOffsetY);
    }

    /// <summary>
    /// 鐢诲竷涓婄殑宸ュ叿鍏夋爣锛堜笉鍚粴鍔ㄦ潯銆佹搷浣滄潯銆佹墜鏌勶級銆?    ///
    /// 闂殑鏄?<see cref="EffectiveTool"/>锛?*绗斿€掕繃鏉ユ嬁锛堢瑪灏炬鐨級鏃讹紝鍏夋爣瑕佹槸姗＄毊鐨?*
    /// 锛堢敤鎴?2026-09-27 鎶ョ殑"绗斿熬鎮仠娌℃湁鎸囩ず"锛夈€傚€掓寔鏃朵笂闈㈤偅琛?Tool.Pen 鐨勫垎鏀牴鏈蛋涓嶅埌锛?    /// 鐩存帴钀藉埌 Tool.Eraser 鈥斺€?绯荤粺鍏夋爣钘忚捣鏉ャ€佽惤鐐圭敱鑷粯鍦嗙幆琛ㄨ揪锛堝崐寰勮 CursorOuterRadius锛夈€?    /// </summary>
    private CursorKind ToolCursorKind => EffectiveTool switch
    {
        // 绗旓細**鑳界粰鏂滅瑪灏辩粰鏂滅瑪**锛堥紶鏍?/ 鎵嬪啓鏉?/ 璁や笉鍑鸿澶囩殑绗旓級鈥斺€?026-09-27 鍔犵殑锛?        // 鐢ㄦ埛鐐瑰悕瑕?InkClass 閭ｇ鏂滅瑪锛氱儹鐐瑰湪绗斿皷銆佷笉鎸¤绾匡紝涓斾竴璺窡鐫€钀界偣璧?        // 锛堟墜鍐欐澘鍐欏瓧鏃跺挨鍏堕渶瑕侊紝瑙?PenDeviceOnScreen 鐨勬敞閲婏級銆?        // 鐢ㄧ殑鏄?*绯荤粺閭ｆ敮 IDC_PEN**锛堣嚜缁樺僵绗旂増璇曡繃銆佽鐢ㄦ埛鍚︽帀锛岃 CursorKind.Pen 鐨勬敞閲婏級銆?        // 鍙湁"绗斿皷灏卞帇鍦ㄥ睆骞曚笂"鐨勮Е灞忕瑪鎵嶈棌璧锋潵锛堢瑪灏栧嵆钀界偣锛岀幆鐢辫嚜缁樿〃杈撅級銆?        Tool.Pen => PenShowsCursor ? CursorKind.Pen : CursorKind.Hidden,
        Tool.Highlighter => CursorKind.Hidden,      // 钀界偣鐢辫嚜缁樼殑瀹藉害鍦嗙洏琛ㄨ揪
        Tool.Laser => CursorKind.Hidden,            // 钀界偣鐢辫嚜缁樼殑瀹炲績鐐硅〃杈?        // 钀界偣鐢辫嚜缁樺渾鐜?/ 鐭╁舰琛ㄨ揪锛涘紑浜?EraserKeepsSystemCursor 灏变袱涓兘鏄剧ず锛圓/B 鐢級銆?        Tool.Eraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        Tool.PixelEraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        // 鎴浘锛?.3.1锛岀収寰俊锛夛細**绯荤粺鍗佸瓧鍏ㄧ▼璺熺潃**锛屼笉鍐嶈嚜缁樺彇鏅瑙掓嫭鍙凤紱
        // 鍙栨櫙鏈熼棿鍙︾敾涓ゆ潯鍏ㄥ睆鍑嗙嚎锛堝湪 Overlay 閲岋紝瑙?DrawCaptureOverlay锛夈€?        // 璋冩暣闃舵鍘嬪湪鎵嬫焺涓婃崲鎴愮郴缁熺殑缂╂斁绠ご鈥斺€旇繖鏄?Windows 鎴浘宸ュ叿鐨勫仛娉曪紝璁よ捣鏉ユ渶蹇€?        Tool.Capture => CaptureCursorKind(),
        // 妗嗛€?/ 鍥惧舰浠嶇劧鐢ㄥ崄瀛楀噯鏄燂細"浠庤繖鍎挎嫋鍒伴偅鍎?鐨勯€氱敤璇█銆?        Tool.Marquee or Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow
            => CursorKind.Cross,
        // 妞渾锛堝甫鐒︾偣锛夛細鍜屾き鍦嗕竴鏍锋槸"浠庤繖鍎挎嫋鍒伴偅鍎?鐨勪竴鎷栵紝缁欏崄瀛楀噯鏄熴€?        Tool.ConicEllipse => CursorKind.Cross,
        _ => CursorKind.Default,
    };

    /// <summary>
    /// 鎴浘妯″紡涓嬬殑绯荤粺鍏夋爣锛?.3.1锛夛細
    ///   路 寰呮満 / 鎷栨 = **鍗佸瓧**锛堝井淇″悓娆撅紱"浠庡摢鍎垮紑濮?姘歌繙鐪嬪緱瑙侊級锛?    ///   路 璋冩暣闃舵鍘嬪湪鎵嬫焺涓?= 瀵瑰簲鐨?*缂╂斁绠ご**锛堝拰閫変腑妗嗘墜鏌勫悓涓€濂楄瑷€锛夛紱
    ///   路 鍘嬪湪銆屸湏 瀹屾垚 / 鉁?鍙栨秷 / 鍙充笂瑙?鉁曘€嶄笂 = 鏅€氱澶达紱
    ///   路 鎶撳睆閭ｄ竴鐬紙`CaptureFrameHidden`锛夌敱 `ApplyCursor` 閭ｈ竟鐓ф棫钘忋€?    /// </summary>
    private CursorKind CaptureCursorKind()
    {
        if (!CaptureActive) return CursorKind.Cross;          // 鍙偣浜嗘牸瀛愶細鍏堢粰鍗佸瓧
        if (CaptureAdjusting)
        {
            switch (CaptureHandleAt(PointerX, PointerY))
            {
                case CaptureDrag.L or CaptureDrag.R: return CursorKind.ResizeWE;
                case CaptureDrag.T or CaptureDrag.B: return CursorKind.ResizeNS;
                case CaptureDrag.TL or CaptureDrag.BR: return CursorKind.ResizeNWSE;
                case CaptureDrag.TR or CaptureDrag.BL: return CursorKind.ResizeNESW;
            }
            if (CaptureButtonHit(PointerX, PointerY)) return CursorKind.Default;
        }
        else if (CaptureCancelHit(PointerX, PointerY)) return CursorKind.Default;
        return CursorKind.Cross;
    }

    /// <summary>
    /// 閫変腑妗嗕笂鐨勫厜鏍囷紙鍚搷浣滄潯銆佸叓涓墜鏌勩€佹棆杞墜鏌勩€佹鍐呮嫋鍔級銆?    /// 杩斿洖 null = 鎸囬拡涓嶅湪閫変腑妗嗚繖涓€甯︼紝璋冪敤鏂圭户缁線涓嬮棶宸ュ叿銆?    ///
    /// 鐢ㄥ彲绌哄€艰€屼笉鏄?杩斿洖 Default 琛ㄧず娌℃湁"锛欴efault锛堢澶达級鏈韩涔熸槸涓€涓?    /// **鏄庣‘绛旀**锛堟瘮濡傛搷浣滄潯鎸夐挳涓婂氨璇ユ槸绠ご锛夛紝涓よ€呮贩鍦ㄤ竴璧峰啓锛?    /// 灏变細鍑虹幇"鎸夐挳涓婂弽鑰岄湶鍑烘墜鏌勫厜鏍?杩欑閿欍€?    /// </summary>
    private CursorKind? SelectionCursor(float canvasX, float canvasY)
    {
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var aabb = frame.CanvasAabb;

        // **鎿嶄綔鏉℃槸"涓€鍧?锛屼笉鍙槸鍑犱釜鎸夐挳**锛堢敤鎴?2026-09-17 鎶ョ殑锛?鎿嶄綔瀵瑰簲鍥炬爣鐨勬椂鍊?        // 涓€浼氭槸鍗佸瓧鍏夋爣锛屼竴浼氭槸绠ご鍥炬爣"锛夈€?        //
        // 鍘熸潵鍙鎸夐挳鏈綋锛坄BarButtonAt`锛夛紝鎸夐挳涔嬮棿鐨勫垎闅旂嚎銆佷袱澶寸殑鐣欑櫧閮戒笉绠楋紝
        // 浜庢槸閭ｄ簺浣嶇疆涓婂氨閫€鍥炲伐鍏峰厜鏍団€斺€旀閫夊伐鍏锋槸鍗佸瓧锛岄紶鏍囧湪鏉′笂妯潃婊戣繃鍘诲氨鏄?        // 绠ご锛忓崄瀛楋紡绠ご锛忓崄瀛椼€傛潯鍦ㄨ瑙変笂鏄竴鏁村潡鐧借壊鑳跺泭锛屾寚閽堣惤鍦ㄥ畠涓婇潰灏辫鏄澶淬€?        // 鏀惰捣鎬侀偅棰楀渾閽€佷互鍙婃寕鍦ㄦ潯涓嬮潰鐨勫皬闈㈡澘锛堥鑹?绮楃粏銆佸眰绾с€佸鍑猴級鍚岀悊銆?        //
        // 鈿?杩欎竴鍧楃敱 `SelectionBarShown` 鎶婂畧锛堝浘褰㈠伐鍏蜂笅鍙湁閭ｄ釜鑷姩閫変腑鐨勬鎸傜潃涓€棰楀渾閽紝
        // 瑙侀偅閲岀殑娉ㄩ噴锛夛紱鐢绘暣鏉¤繕鏄敾鍦嗛挳鐢?`BarDrawnCollapsed` 瀹氥€?        if (SelectionBarShown)
        {
            if (BarDrawnCollapsed)
            {
                if (SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas).Contains(canvasX, canvasY))
                    return CursorKind.Default;
            }
            else
            {
                if (CustomColorOpen
                    && SelectionHandles.PickContains(canvasX, canvasY, aabb, dpi, ViewportCanvas,
                                                     SelectionHandles.SwatchCount))
                    return CursorKind.Default;
                if (SelectionHandles.BarRect(aabb, dpi, ViewportCanvas).Contains(canvasX, canvasY))
                    return CursorKind.Default;
                if (SelPanelOpen != SelPanel.None
                    && SelectionHandles.PanelContains(canvasX, canvasY, aabb, dpi, ViewportCanvas,
                                                      SelPanelOpen, SelectionHandles.SwatchCount))
                    return CursorKind.Default;
            }
        }

        var h = SelectionHandles.HitTest(canvasX, canvasY, Doc.Selected, frame, dpi);
        if (h != SelHandle.None) return HandleCursor(h);

        return SelectionHandles.InsideUiFrame(frame, new Vector2(canvasX, canvasY), dpi)
            ? CursorKind.Move : null;
    }

    private static CursorKind HandleCursor(SelHandle h) => h switch
    {
        SelHandle.TopLeft or SelHandle.BottomRight => CursorKind.ResizeNWSE,
        SelHandle.TopRight or SelHandle.BottomLeft => CursorKind.ResizeNESW,
        SelHandle.Left or SelHandle.Right => CursorKind.ResizeWE,
        SelHandle.Top or SelHandle.Bottom => CursorKind.ResizeNS,
        SelHandle.Rotate => CursorKind.Rotate,
        // 绔偣鎵嬫焺缁欏崄瀛楀噯鏄燂細瀹冩槸"绮剧‘鍙栦竴涓偣"锛屽拰鎷変几锛堝洓鍚戠澶达級鏄袱绉嶆剰鎬濄€?        SelHandle.EndpointA or SelHandle.EndpointB => CursorKind.Cross,
        // 鐒︾偣涓夎褰㈢殑椤剁偣 P 鍚岀悊锛氬畠涔熸槸"绮剧‘鍙栦竴涓偣"锛堝悓 EndpointA 閭ｆ潯鐞嗙敱锛夈€?        SelHandle.FocusPoint => CursorKind.Cross,
        _ => CursorKind.Default,
    };

    /// <summary>
    /// 鎶婂綋鍓嶈鏈夌殑鍏夋爣璁句笂銆?    ///
    /// 鈿?**"鍚屼竴涓彞鏌勪笉閲嶅璁?鍙槸涓€鍙ヤ紭鍖栵紝涓嶈兘褰撲簨瀹炵敤**锛?026-09-27 淇級锛?    /// 鏈夊ソ鍑犲鎴戜滑**鎶婂厜鏍囦氦鍑哄幓浜?*鎴?*璁╁埆浜烘敼鎺変簡瀹?*鈥斺€?    ///   路 绌块€忔ā寮忥紙`CursorKind.Leave`锛夛細鎸囬拡褰掍笅灞傜獥鍙ｏ紝瀹冭嚜宸变細鎹㈡垚绠ご锛?    ///   路 鏀圭獥鍙ｆ墿灞曟牱寮忥紙`ApplyPassThroughStyle` 鐨?SWP_FRAMECHANGED锛夛細
    ///     绯荤粺浼氶『鎵嬫妸鍏夋爣鎭㈠鎴愮被鍏夋爣锛堢澶达級銆?    /// 杩欎簺鏃跺埢涔嬪悗锛宍_cursorApplied` 璁扮殑杩樻槸"鎴戜滑璁捐繃鐨勯偅涓€涓?锛岃€屽睆骞曚笂鏄澶粹€斺€?    /// 浜庢槸鍚庨潰鎵€鏈?璁炬垚鍚屼竴涓?鐨勮姹傚叏琚繖鍙ヤ紭鍖栬烦杩囷紝**鍏夋爣灏卞崱鍦ㄧ澶翠笂涓嶅姩浜?*銆?    /// 鐢ㄦ埛 2026-09-27 鎶ョ殑姝ｆ槸杩欎釜锛?鍒囧埌绌块€忓啀鍒囧洖鐢荤瑪锛岃繕鏄笁瑙掑舰锛?    /// 鍒囪崸鍏夌瑪鏇存槑鏄锯€斺€旂澶村拰鑷粯鍦嗙洏涓€璧峰嚭鐜帮紱澶氬垏鍑犳鏈夋椂鍊欏張鑳藉垏鍥炴潵"銆?    /// 淇硶锛氬嚒鏄?鏀炬墜"鎴?鍒汉鍙兘鏀硅繃"鐨勫湴鏂癸紝閮芥妸 `_cursorApplied` 浣滃簾銆?    /// </summary>
    internal void ApplyCursor(bool force = false)
    {
        var kind = ComputeCursorKind();
        if (kind == CursorKind.Leave)
        {
            // 涓嶆彃鎵嬶細鎸囬拡褰掍笅灞傜獥鍙ｃ€?*椤烘墜鎶婄紦瀛樹綔搴?*鈥斺€斾粠鐜板湪璧峰睆骞曚笂鏄粈涔堝厜鏍?            // 宸茬粡涓嶇敱鎴戜滑鍐冲畾锛屼笅涓€娆¤璁剧殑鏃跺€欏繀椤荤湡璁撅紙瑙佷笂闈㈤偅娈碉級銆?            _cursorApplied = IntPtr.Zero;
            return;
        }
        var h = Cursors.HandleFor(kind, CursorSizePx);
        if (h == IntPtr.Zero) return;
        if (!force && h == _cursorApplied) return;
        Native.SetCursor(h);
        _cursorApplied = h;
    }

    /// <summary>鑷鐢細鎴戜滑鏈€鍚庝竴娆＄湡鐨勮涓嬪幓鐨勫厜鏍囷紙IntPtr.Zero = 宸蹭綔搴?/ 杩樻病璁捐繃锛夈€?/summary>
    internal IntPtr CursorAppliedForTest => _cursorApplied;

    /// <summary>
    /// 鑷繁瑕佺敾钀界偣鍙嶉锛堟鐨渾鐜€佺瑪灏栫幆銆佽崸鍏夌瑪鍦嗙洏锛夋椂锛屽繀椤绘妸绯荤粺鍏夋爣钘忚捣鏉ワ紝
    /// 鍚﹀垯灏辨槸"绠ご + 鍦嗙幆"鍙犲湪涓€璧枫€傝Е鎽镐笉鐢伙細鎵嬫寚娌℃湁鎮仠锛岀敾浜嗕細鐣欏湪灞忓箷涓娿€?    /// </summary>
    internal ToolCursorShape DrawnCursor
    {
        get
        {
            // CaptureFrameHidden = "姝ｅ湪鎶撳睆鐨勯偅涓€鐬?锛氬彇鏅鍜?*钀界偣鍏夋爣**閮戒笉鐢汇€?            // 鍏夋爣鏄惀鐝€鑹茬殑锛岀暀鐫€瀹冨氨浼氳"鐩存帴鎴彇"鎷嶈繘鍥鹃噷锛堣嚜妫€鎶撳埌杩囷細鍥剧墖鏈€澶栦竴鍦?            // 澶氬嚭 15 涓惀鐝€鍍忕礌鈥斺€旀嫋妗嗘濂芥敹鍦ㄨ涓婏紝鍏夋爣灏卞帇鍦ㄩ偅涓€瑙掞級銆?            //
            // 瑙︽懜榛樿涓嶇敾锛堟墜鎸囨病鏈夋偓鍋滐紝鐢讳簡浼氱暀鍦ㄥ睆骞曚笂锛夆€斺€?*浣嗘鐨緥澶?*锛?            // 鎵嬫寚鎸夌潃鎿︾殑鏃跺€欐病鏈夎惤鐐瑰弽棣堝氨鏄?鐪嬩笉瑙佹鐨摝"锛?026-10-05 鐢ㄦ埛瀹炴祴锛夛紝
            // 鎵€浠ユ摝闄よ繘琛屼腑鎶婇紶鏍囬偅濂楄惤鐐瑰師鏍锋惉杩囨潵锛堝舰鐘?灏哄/棰滆壊璧板悓涓€浠戒唬鐮侊級锛?            // 鏉炬墜锛坃drawing 钀斤級绔嬪埢娑堝け銆傚鎸囨摝浼氳瘽锛圱ouchMode.Erase锛夎蛋浼氳瘽涓撶敤鍙嶉锛?            // 涓嶅湪杩欎竴鏉￠噷锛堝畠鎸夋瘡涓Е鐐圭敾锛岃剰鍖虹敱 ComputeTransientBounds 绠★級銆?            bool touchEraserCursor = LastPointerType == Native.PT_TOUCH
                                     && _drawing && _touchMode == TouchMode.Write
                                     && (EffectiveTool == Tool.Eraser || EffectiveTool == Tool.PixelEraser);
            if (PassThrough || !PointerInside || CaptureFrameHidden
                || (LastPointerType == Native.PT_TOUCH && !touchEraserCursor))
                return ToolCursorShape.None;

            // 鎸囬拡鍋滃湪鐣岄潰鍧椾笂锛堟帴杈撳叆灏忕獥 / 鍥惧簱闈㈡澘锛夛細杩欎竴鍦堣惤鐐瑰弽棣堣娑堝け銆?            // 涓嶅垽杩欎竴鏉＄殑璇濓紝鎸囬拡绉诲埌闈㈡澘涓婁箣鍚庯紝瑕嗙洊灞傛敹涓嶅埌浠讳綍鎸囬拡娑堟伅锛?            // 涓婁竴甯х殑鍦嗙幆浼?*鐣欏湪灞忓箷涓婁笉鍔?*鈥斺€旂湅璧锋潵灏卞儚鍗′綇浜嗐€?            if (_uiHover || PointerOnDrawnChrome()) return ToolCursorShape.None;

            // 瑙勫垯涓€鍙ヨ瘽锛?*钀界偣绂诲睆骞曡繙鐨勶紙榧犳爣 / 鎵嬪啓鏉匡級涓€璺敾**锛?            // **绗斿皷灏卞帇鍦ㄥ睆骞曚笂鐨?*锛堣Е鎽稿睆鑷甫绗旓級锛屼竴钀界瑪灏变笉璇ュ啀璺熶竴涓湀
            // 锛堜細鎶婃墜鍐欑殑浣嶇疆鎸′綇锛岃€屼笖绗斿皷鍜屽湀鐨勪腑蹇冨樊涓€涓ゅ儚绱犳椂鐪嬬潃鍍忛敊浣嶏級銆?            //
            // 鈿?2026-09-27 淇繃涓€娆★紙鐢ㄦ埛鎶?鎵嬪啓鏉垮啓瀛楁椂鍏夋爣娑堝け銆佷笉娴佺晠"锛夛細
            // 浠ュ墠杩欏彞鏄?鍙 PT_PEN 灏辩畻绗斿皷鍦ㄥ睆骞曚笂"鈥斺€?*鎵嬪啓鏉跨殑绗斿皷鍦ㄦ澘瀛愪笂銆?            // 鏍规湰涓嶅湪灞忓箷閲?*锛屼簬鏄啓瀛楀叏绋嬪睆骞曚笂浠€涔堥兘娌℃湁銆傜幇鍦ㄦ寜璁惧鍑鸿韩鍒嗭細
            // 鍙湁瑙︽懜灞忚嚜甯︾殑绗旓紙PenDeviceOnScreen锛夋墠浜彈"钀界瑪涓嶇敾"锛?            // 鎵嬪啓鏉夸笌榧犳爣涓€鏍蜂竴璺敾锛堝尯鍒彧鏄墜鍐欐澘缁欑郴缁熸枩绗斻€佷笉鐢ㄨ嚜缁橈紝瑙佸伐鍏峰垎鏀級銆?            bool penTip = LastPointerType == Native.PT_PEN && PenDeviceOnScreen;

            // 闂?EffectiveTool锛?*绗斿€掕繃鏉ユ嬁灏辨槸姗＄毊鐨勮惤鐐瑰弽棣?*锛堝崐寰?= 姗＄毊鍗婂緞锛?            // 瑙?CursorOuterRadius锛夆€斺€斾笉鐒剁瑪灏炬偓鍋滄椂鐢荤殑鏄?绗斿皷鏈夊绮?鐨勭幆锛?            // 涓庡畠鐪熷疄浼氭摝鎺夌殑閭ｄ竴鍧楀涓嶄笂锛堢敤鎴?2026-09-27 鎶ョ殑锛夈€?            switch (EffectiveTool)
            {
            case Tool.Eraser:
                // 姗＄毊鍙嶈繃鏉ワ細瀹冭〃杈剧殑鏄?杩欎竴鍧椾細琚摝鎺?锛屾摝闄や腑鏇磋鐪嬪緱鍒般€?                return ToolCursorShape.Ring;
            case Tool.PixelEraser:
                // 鍚岀悊锛氱煩褰㈣涓€鐩寸湅寰楄锛屾摝闄や腑鏇磋璇存竻"杩欎竴鍧楁鍦ㄨ鎿?銆?                return ToolCursorShape.Rect;
            case Tool.Capture:
                // 鎴浘锛?.3.1锛夛細**涓嶇敾鑷粯钀界偣**鈥斺€旂郴缁熷崄瀛楀叏绋嬭窡鐫€锛堣 CaptureCursorKind锛夛紝
                // 灞忓箷涓婂彟鐢讳袱鏉″叏灞忓噯绾匡紙鍦?Overlay.DrawCaptureOverlay 閲岋級銆?                // 8.3.0 閭ｄ釜 24脳24 瑙掓嫭鍙疯鐢ㄦ埛鐐瑰悕"姣旇緝闅剧敤"锛屾暣鎵归€€鍦恒€?                return ToolCursorShape.None;
                case Tool.Pen:
                    // 宸茬粡鍦ㄦ樉绀烘枩绗旓紙榧犳爣 / 鎵嬪啓鏉匡紝鑷粯褰╃瑪锛夛細钀界偣鐢卞畠涓€璺〃杈撅紝
                    // **涓嶅啀鍙犺嚜缁樼幆**鈥斺€?绗斾笂鍐嶅涓€涓湀"灏辨槸杩欐寮€澶磋鐨勯偅绉嶅彔褰便€?                    if (PenShowsCursor) return ToolCursorShape.None;
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Ring;
                case Tool.Highlighter:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Disc;
                case Tool.Laser:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Dot;
                default:
                    return ToolCursorShape.None;
            }
        }
    }

    /// <summary>鑷粯钀界偣鍙嶉鐨勫崐寰勶紙鍍忕礌锛夛細鑴忓尯鎸夊畠鎵╋紝涓嶇劧蹇€熺Щ鍔ㄤ細鐣欐畫褰便€?/summary>
    internal float DrawnCursorRadius
    {
        get
        {
            switch (DrawnCursor)
            {
                case ToolCursorShape.Ring:
                    return CursorOuterRadius * 1.35f + 10f;
                case ToolCursorShape.Disc:
                    // 鍦嗙洏锛氳剰鍖烘寜鍗婂緞绠楋紝鍐嶇暀鍑烘弿杈瑰拰鎶楅敮榻跨殑杈广€?                    return MathF.Max(HighlighterWidthLogical * DpiScale * 0.5f, 2f) + 12f;
                case ToolCursorShape.Rect:
                    // 鐭╁舰锛氭寜**鍗婂瑙掔嚎**鎵╋紝鍥涗釜瑙掓墠涓嶄細鍦ㄥ揩閫熺Щ鍔ㄦ椂鐣欐畫褰便€?                    // 鐢ㄨ惤鐐规锛堟嫋鍔ㄤ腑浼氶殢閫熷害鍙樺ぇ锛夆€斺€旀寜鍩哄噯绠楃殑璇濓紝鏀惧ぇ鐨勯偅涓€鍦堜細鐣欐畫褰便€?                    return MathF.Sqrt(PixelEraserCursorHalfWidthPx * PixelEraserCursorHalfWidthPx
                                    + PixelEraserCursorHalfHeightPx * PixelEraserCursorHalfHeightPx) + 12f;
                case ToolCursorShape.Dot:
                    return CursorDotRadius * 1.5f + 10f;
                default:
                    return 0f;
            }
        }
    }

    /// <summary>
    /// 绗?婵€鍏夎惤鐐瑰弽棣堢殑**鐪熷疄**鍗婂緞锛堝儚绱狅級锛氬氨鏄?杩欎竴绗旀湁澶氱矖"鐨勪竴鍗娿€?    /// 缁嗙瑪锛?.5 閫昏緫鍍忕礌锛変笉鑳借"鐪嬪緱瑙佺殑涓嬮檺"鎾戞帀鈥斺€旈偅浼氳浜鸿鍒ょ瑪瀹斤紝
    /// 鎵€浠ヤ笅闄愮敱澶栧眰閭ｄ釜鍥哄畾灏哄鐨勫湀璐熻矗锛堣 Overlay.DrawRingCursor锛夈€?    /// </summary>
    internal float CursorRingTrueRadius => CurrentToolWidthLogical * 0.5f * DpiScale;

    /// <summary>
    /// 婵€鍏夎建杩圭殑绮楃粏锛堝儚绱狅級銆?*榛樿鍊煎拰浠ュ墠涓€妯′竴鏍?*锛氫互鍓嶈建杩瑰啓姝?    /// "鑺?5 鍍忕礌 + 澶栧彂鍏?26 鍍忕礌"锛屽拰浠讳綍瀹藉害璁剧疆閮芥棤鍏筹紱鐜板湪鎸夋縺鍏夎嚜宸辩殑
    /// 绮楃粏缂╂斁锛?.625 杩欎釜绯绘暟灏辨槸璁╅粯璁?4 閫昏緫鍍忕礌 脳 2 鍊嶅睆 = 5 鍍忕礌锛?    /// 澶栬涓嶅彉锛夛紝杩欐牱鍒囩矖缁嗗婵€鍏夋墠鏄湁鎰忎箟鐨勩€?    /// </summary>
    internal float LaserCorePx => MathF.Max(2f, LaserWidthLogical * DpiScale * 0.625f);
    internal float LaserGlowPx => LaserCorePx * 5.2f;

    /// <summary>
    /// 婵€鍏夎惤鐐圭殑鍗婂緞锛堝儚绱狅級銆傚彇"杞ㄨ抗澶撮儴鐨勭矖缁?锛堢粏婵€鍏夋湁涓彲瑙佷笅闄愶級锛?    /// 杩欐牱鐐瑰拰瀹冩嫋鍑烘潵鐨勫熬宸存槸鍚屼竴涓昂瀵革紝鐪嬭捣鏉ユ槸涓€鏀瑪鑰屼笉鏄?鐐逛笂鍔犳潯绾?銆?    /// </summary>
    internal float CursorDotRadius
        => MathF.Max(LaserCorePx, Cursors.DotMinRadiusLogical * DpiScale);

    /// <summary>
    /// 钀界偣鍙嶉**澶栧湀**鐨勫崐寰勶紙鍍忕礌锛夈€傜粏绗旂殑鐪熷疄鍗婂緞鍙湁 1.5 鍍忕礌锛?    /// 鍗曠敾閭ｄ釜鍦堢瓑浜庢病鐢伙紝鎵€浠ュ闈㈠啀濂椾竴涓浐瀹氬昂瀵哥殑鍦堬細
    ///   鍐呭湀 = 鐪熷疄绗斿锛?:1锛?鎴戝啓鍑烘潵灏辫繖涔堢矖"锛?    ///   澶栧湀 = 鏈€灏忓彲瑙佸昂瀵革紙"钀界偣鍦ㄨ繖鍎?锛?    /// 姗＄毊鏈潵灏卞彧琛ㄨ揪鑼冨洿锛屽彧鏈変竴涓湀銆?    /// </summary>
    internal float CursorOuterRadius => EffectiveTool == Tool.Eraser
        ? EraserRadius
        : MathF.Max(CursorRingTrueRadius, Cursors.RingMinRadiusLogical * DpiScale);

    /// <summary>
    /// 璁竴娆?鎸囬拡绂诲紑绐楀彛"鐨勯€氱煡銆傝鐩栧眰浠ュ墠瀹屽叏娌¤杩囷紝鎵€浠?PointerInside
    /// 姘歌繙鍋滃湪 true鈥斺€旀墜绉诲紑涔嬪悗钀界偣鐜細鐣欏湪灞忓箷涓娿€?    /// </summary>
    private void TrackPointerLeave(IntPtr hWnd)
    {
        var tme = new Native.TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
            dwFlags = Native.TME_LEAVE,
            hwndTrack = hWnd,
        };
        Native.TrackMouseEvent(ref tme);
    }

    // =====================================================================
    //  婊氬姩鏉★紙鎷栧姩 + 鎮仠锛?    //
    //  浠ュ墠鍙湁婊氳疆鑳芥粴锛氭粴鍔ㄦ潯鏄?*鐢诲嚭鏉ョ殑銆佹嫋涓嶅姩鐨勪笢瑗?*銆傚厜鏍囪繖涓€棰樼殑
    //  鍓嶆彁鏄?婊戝潡鐪熺殑鑳芥嫋"锛屾墍浠ュ湪杩欓噷琛ヤ笂銆傚舰鐘朵粛鐒舵槸绠ご锛堢悊鐢辫璋冪爺鏂囨。
    //  5.3锛氭墜鍨嬪彧鑳借〃绀洪摼鎺ワ紝鍥涘悜绠ご浼氳璇绘垚缂╂斁/绉诲姩锛夈€?    // =====================================================================

    /// <summary>鍛戒腑瀹藉害锛堥€昏緫鍍忕礌锛夈€傛粦鍧楀彧鏈?4 鍍忕礌瀹斤紝鎸?4 鍍忕礌鍒ゅ畾绛変簬鐐逛笉涓€?/summary>
    private const float ScrollBarGrabLogical = 16f;

    /// <summary>涓婁笅澶逛綇鐩告満鍋忕Щ锛堟粴杞拰鎷栨粴鍔ㄦ潯鍏辩敤锛夈€?/summary>
    internal void ClampViewOffset()
    {
        ViewOffsetY = ClampOffset(ViewOffsetY);
    }

    /// <summary>鎶婁竴涓浉鏈哄亸绉诲す杩涘悎娉曡寖鍥达紙缈婚〉瑕?鍏堢畻鐩爣鍐嶅喅瀹氬姩涓嶅姩"锛屾墍浠ユ娊鍑烘潵锛夈€?/summary>
    private float ClampOffset(float y)
    {
        var extent = CanvasExtent;
        float lowest = _virtualH - extent.MaxY;
        if (y > 0f) y = 0f;
        if (y < lowest) y = lowest;
        return y;
    }

    // ---- 鏁村睆缈婚〉锛?涓€灞?= 涓€椤?锛?----------------------------------------
    //
    // 鐢ㄦ埛 2026-09-16 閫夌殑鏂规锛堣 璋冪爺-鐧芥澘缈婚〉.md锛夛細**杩樻槸鍚屼竴寮犺繛缁敾甯?*锛?    // 鍙槸鐩告満**鎸夋暣灞忚烦**鈥斺€斾笂涓€灞?涓嬩竴灞忓悇璧颁竴涓鍙ｉ珮锛岀炕瀹屽睆骞曚笂涓嶇暀鍗婅瀛椼€?    //
    // 涓轰粈涔堜笉鍋?姣忛〉涓€濂楃瑪杩?锛圛nkClass 閭ｇ锛夛細瀹冪殑"椤?涓昏鏄负浜嗚窡 PPT 瀵归綈锛?    // 鎴戜滑鏆傛椂涓嶆帴 PPT锛涜€屼笖瀹冧负姝や粯鍑轰簡"鍧愭爣鐗╁寲 + 鎸夐〉璁板亸绉?鐨勪唬浠凤紙瀹冪殑娉ㄩ噴鍐欑潃
    // 鍒囧洖鍘熼〉浼?浣嶇疆瀵逛笉涓娿€佹粴涓婂幓鐨勫唴瀹瑰啀涔熸粴涓嶅洖鏉?锛夈€傛垜浠彧鍔ㄧ浉鏈猴紝娌℃湁杩欎簺闂銆?
    private double _camFrom, _camTo, _camStartMs;
    private bool _camAnimating;
    private const double CamAnimMs = 167;      // 鍜岀晫闈㈠悓涓€濂楁椂闀匡紙167ms 鏄?Windows 鐨?Direct Entrance锛?
    /// <summary>
    /// 缈讳竴灞忋€?paramref name="down"/> = 寰€涓嬬炕锛堝唴瀹逛笂绉汇€佸亸绉诲彉璐燂紝鍜屾粴杞悓涓€濂楃鍙凤級銆?    /// 杩斿洖鏄惁鐪熺殑缈讳簡锛堝凡缁忓湪椤?搴曞氨涓嶅姩锛夈€?    /// </summary>
    internal bool FlipPage(bool down)
    {
        float want;
        if (DocView.IsOpen)
        {
            // 鏂囨。妯″紡锛氱炕椤?= **璺冲埌涓?涓嬩竴椤电殑椤甸《**锛?涓€椤典竴缈?锛?0-03 鏂囨。瀹氱殑浜や簰锛夈€?            // 鐩告満鍙湁涓€涓暟锛氭妸鐩爣椤甸《鎽嗗埌瑙嗗彛椤?鈫?ViewOffsetY = _virtualY - 椤甸《銆?            var vp = ViewportCanvas;
            int cur = DocView.CurrentIndex(vp.MinY, vp.MaxY);
            if (cur < 0) cur = DocView.IndexAt(vp.MinY + 1f);      // 姝ｅソ鍋滃湪椤电紳閲岋細鎸夐《杈圭畻
            int target;
            if (cur < 0)
            {
                target = 0;
            }
            else if (down)
            {
                target = cur + 1;                                   // 寰€涓嬶細鐩存帴涓嬩竴椤甸〉椤?            }
            else
            {
                // 寰€涓婏細鍏堝洖鏈〉椤甸《锛涘凡缁忓湪椤甸《锛堝樊涓嶅埌 1/10 灞忥級鎵嶅幓涓婁竴椤?                float intoPage = vp.MinY - DocView.TopOf(cur);
                target = intoPage > _virtualH * 0.12f ? cur : cur - 1;
            }
            target = Math.Clamp(target, 0, DocView.Count - 1);
            want = ClampOffset(_virtualY - DocView.TopOf(target));
        }
        else
        {
            want = ClampOffset(down ? ViewOffsetY - _virtualH : ViewOffsetY + _virtualH);
        }
        if (Math.Abs(want - ViewOffsetY) < 1f) return false;

        if (!ClientAreaAnimationOn)
        {
            ViewOffsetY = want;               // 绯荤粺鍏充簡鍔ㄧ敾灏辩洿鎺ヨ烦缁堟€侊紙鑰佹満鍣ㄤ笂鏄父浜嬶級
            _dirty = true;
            return true;
        }
        _camFrom = ViewOffsetY; _camTo = want; _camStartMs = NowMs; _camAnimating = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 缈婚〉鍔ㄧ敾锛?67ms銆佸揩鍑虹紦鍋滐紙鍜岄潰鏉跨殑灞曞紑鍚屼竴濂楁洸绾匡級銆?    ///
    /// **姣忓抚鎺ㄨ繘涓€娆?*锛岀敱涓诲惊鐜皟銆傝嚜妫€鐨?`SettleFrames` 涔熻皟鍚屼竴浠解€斺€?    /// 鑷鏄墜鍔ㄦ娊甯с€佷笉璧颁富寰幆锛屽皯杩欎竴鍙ュ氨浼氶噺鍑?缈婚〉杩斿洖 true 浣嗙浉鏈?    /// 涓€鐩村仠鍦ㄨ捣鐐?锛坄--pagetest` 绗竴鐗堝氨鏄繖涔堣嚜宸辨妸鑷繁楠椾簡涓€娆★級銆?    /// </summary>
    internal void StepCameraAnim()
    {
        if (!_camAnimating) return;
        double t = (NowMs - _camStartMs) / CamAnimMs;
        if (t >= 1.0) { ViewOffsetY = (float)_camTo; _camAnimating = false; }
        else
        {
            float k = 1f - MathF.Pow(1f - (float)t, 3f);
            ViewOffsetY = (float)(_camFrom + (_camTo - _camFrom) * k);
        }
        _dirty = true;
    }

    /// <summary>褰撳墠鍦ㄧ鍑犲睆锛? 璧凤級銆傜浉鏈哄亸绉婚櫎浠ヨ鍙ｉ珮 + 1銆?/summary>
    internal int ScreenIndex => (int)Math.Round(-ViewOffsetY / Math.Max(1f, _virtualH)) + 1;

    /// <summary>杩樿兘涓嶈兘寰€涓婄炕锛堝埌椤朵簡灏变笉琛岋紱寰€涓嬫案杩滃彲浠モ€斺€旂敾甯冧笅闈㈡案杩滃涓€灞忥級銆?/summary>
    internal bool CanFlipPageUp => ViewOffsetY < -1f;

    /// <summary>
    /// "涓€椤?鍦?*鐢诲竷鍧愭爣**閲岀殑楂?= 涓€涓鍙ｉ珮锛涚涓€椤电殑椤?= 铏氭嫙妗岄潰椤躲€?    /// 椤电晫绾匡紙鐧芥澘妯″紡涓嬬敾鐨勯偅鏉℃贰绾匡級鎸夎繖涓や釜鍊肩畻锛屾墍浠ュ畠**鍥哄畾鍦ㄧ敾甯冧笂**銆?    /// 涓嶉殢鐩告満鍔ㄢ€斺€旇兘琚儤杩涘垎鍧楃紦瀛橈紝骞虫椂闆舵垚鏈€?    /// </summary>
    internal float PageHeightCanvas => _virtualH;
    internal float PageTopCanvas => _virtualY;
    /// <summary>搴曠汗鐨勬í鍚戦敋鐐癸紙绗竴椤电殑宸﹁竟鐣岋級鈥斺€斿拰椤电晫绾垮叡鐢ㄥ悓涓€涓師鐐广€?/summary>
    internal float PageLeftCanvas => _virtualX;

    /// <summary>
    /// 绯荤粺鐨?鍦?Windows 涓樉绀哄姩鐢?寮€鍏筹紙`SPI_GETCLIENTAREAANIMATION`锛夈€?    /// 鍏虫帀鏃舵墍鏈夊姩鏁堢洿鎺ヨ烦缁堟€佲€斺€旀暀瀹よ€佹満鍣ㄤ笂甯稿叧锛岃璁℃枃绋块噷涔熸妸杩欐潯鍒椾负纭鑼冦€?    /// 鍙栦笉鍒板氨褰撲綔"寮€鐫€"锛堣窡绯荤粺榛樿涓€鑷达級銆?    /// </summary>
    private bool? _animOn;
    internal bool ClientAreaAnimationOn
    {
        get
        {
            if (_animOn == null)
            {
                int v = 1;
                try
                {
                    if (!Native.SystemParametersInfo(Native.SPI_GETCLIENTAREAANIMATION, 0, ref v, 0)) v = 1;
                }
                catch { v = 1; }
                _animOn = v != 0;
            }
            return _animOn.Value;
        }
    }

    private bool TryBeginScrollBarDrag(float screenX, float screenY)
    {
        var w = WindowAt(screenX, screenY);
        if (w == null || !w.TryScrollBar(this, out var sb)) return false;
        if (!sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f)) return false;

        // 鐐瑰湪杞ㄩ亾涓婏紙婊戝潡浠ュ锛夛細鍏堟妸婊戝潡涓績鎸埌鎸囬拡澶勶紝鍐嶅綋鎴?婊戝潡鍐呮嫋鍔?銆?        // macOS / Figma 閮芥槸杩欎釜鎵嬫劅锛氱偣鍝効婊氬埌鍝効锛屾瘮"缈讳竴椤?鏇寸鍚堢洿瑙夈€?        if (screenY < sb.ThumbTop || screenY > sb.ThumbTop + sb.ThumbLen)
        {
            float want = Math.Clamp(screenY - sb.ThumbLen * 0.5f, sb.Top, sb.Top + sb.MaxTravel);
            ApplyScrollBarThumbTop(w, sb, want);
            w.TryScrollBar(this, out sb);
        }

        ScrollBarDragging = true;
        ScrollBarHover = true;
        ScrollBarActiveAtMs = NowMs;
        _scrollWindow = w;
        _scrollGrabDy = screenY - sb.ThumbTop;
        return true;
    }

    private void UpdateScrollBarDrag(float screenY)
    {
        if (_scrollWindow == null) return;
        if (!_scrollWindow.TryScrollBar(this, out var sb)) return;
        float want = Math.Clamp(screenY - _scrollGrabDy, sb.Top, sb.Top + sb.MaxTravel);
        ApplyScrollBarThumbTop(_scrollWindow, sb, want);
    }

    private void EndScrollBarDrag()
    {
        ScrollBarDragging = false;
        _scrollWindow = null;
    }

    /// <summary>婊戝潡椤惰竟 鈫?鐩告満鍋忕Щ銆傜粯鍒舵椂閭ｆ鎹㈢畻鐨勯€嗚繍绠楋紙瑙?Overlay.DrawScrollBar锛夈€?/summary>
    private void ApplyScrollBarThumbTop(OverlayWindow w, in OverlayWindow.ScrollBarLayout sb, float thumbTop)
    {
        if (sb.MaxTravel <= 0.001f) return;
        float frac = Math.Clamp((thumbTop - sb.Top) / sb.MaxTravel, 0f, 1f);
        float viewTop = sb.ExtentMinY + frac * (sb.ExtentH - w.Height);
        ViewOffsetY = w.OriginY - viewTop;
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    /// <summary>鎸囬拡鏄惁鍋滃湪婊氬姩鏉′笂銆傚仠鍦ㄤ笂闈㈠氨涓嶈娣″嚭锛屾墍浠ユ瘡娆＄Щ鍔ㄥ拰姣忔蹇冭烦閮借闂€?/summary>
    internal void UpdateScrollBarHover(float screenX, float screenY)
    {
        if (!ScrollBarEnabled)
        {
            // 鍋滅敤鍚庢妸娈嬬暀鐨勬偓鍋滅姸鎬佹竻骞插噣锛堝厜鏍?/ 缁樺埗閮借瀹冿級銆?            if (ScrollBarHover) { ScrollBarHover = false; _dirty = true; }
            return;
        }
        bool hover = false;
        if (!PassThrough && !ScrollBarDragging)
        {
            var w = WindowAt(screenX, screenY);
            if (w != null && w.TryScrollBar(this, out var sb))
                hover = sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f);
        }

        if (hover != ScrollBarHover)
        {
            ScrollBarHover = hover;
            _dirty = true;                      // 婊戝潡瑕佷粠 4px 鍙樺埌 9px
        }
        if (hover || ScrollBarDragging) ScrollBarActiveAtMs = NowMs;
    }

    /// <summary>
    /// 蹇冭烦锛?50ms 涓€娆★級閲屾寜榧犳爣浣嶇疆鍒锋柊婊氬姩鏉℃偓鍋溿€傛病鏈夎繖涓€姝ワ紝鎸囬拡鍋滃湪婊戝潡涓?    /// 涓嶅姩鏃舵病浜洪€氱煡锛屾粦鍧椾細鑷繁娣″嚭鈥斺€旂敤鎴蜂細瑙夊緱"杩欑帺鎰忓効鐐逛笉涓?銆?    /// </summary>
    private void UpdateScrollBarHoverFromCursor()
    {
        if (PassThrough || ScrollBarDragging) return;
        if (!Native.GetCursorPos(out var p)) return;
        UpdateScrollBarHover(p.X, p.Y);
    }

    /// <summary>鎵惧嚭鍖呭惈鏌愪釜灞忓箷鍧愭爣鐨勮鐩栫獥鍙ｏ紙澶氬睆鏃剁敤锛夈€?/summary>
    private OverlayWindow WindowAt(float screenX, float screenY)
    {
        foreach (var w in _windows)
        {
            if (screenX >= w.OriginX && screenX < w.OriginX + w.Width &&
                screenY >= w.OriginY && screenY < w.OriginY + w.Height)
                return w;
        }
        return _windows.Count > 0 ? _windows[0] : null;
    }

    // =====================================================================
    //  鍥惧舰宸ュ叿锛氱洿绾?/ 鐭╁舰 / 妞渾 / 绠ご
    //
    //  鍥涚閮芥槸"鎷?A鈫払"锛氭寜涓嬭璧风偣銆佹嫋鍔ㄦ椂**鍙敼缁堢偣**銆佹澗鎵嬫彁浜ゆ垚涓€涓?    //  Kind 姝ｇ‘鐨勫浘褰紙瑙?璁″垝-鍥惧舰宸ュ叿.md 8.2锛夈€傚嚑浣曠敱涓や釜绔偣瀹氫箟锛?    //  鎵€浠ヨ繍琛屾椂鍙湁涓や釜鐐光€斺€斾笉鏄?鐢诲畬鍐嶆嫙鍚?锛屼篃涓嶆槸鍏堝瓨涓€涓查噰鏍风偣銆?    // =====================================================================

    /// <summary>
    /// 鎷栧姩璺濈鐭簬杩欎箞澶?*閫昏緫鍍忕礌**灏变笉鎻愪氦锛堥伩鍏嶈鐐归€犲嚭涓€涓€€鍖栫殑鍥惧舰锛夈€?    ///
    /// 4 杩欎釜鏁板拰鐐归€夊宸?<see cref="ClickToleranceLogical"/> 鍚屼竴涓噺绾э細
    /// 鎶曞奖涓婃墜鎶栦釜涓€涓ゅ儚绱犲緢姝ｅ父锛?鐐逛竴涓?涓嶈鐣欎笅浠讳綍涓滆タ銆?    /// </summary>
    internal const float ShapeMinDragLogical = 4f;

    /// <summary>杩欏嚑绉嶅伐鍏疯蛋"鎷栧嚭鏉ヤ竴涓浘褰?閭ｆ潯璺紙鍏朵綑宸ュ叿鐓ф棫鍐欒嚜鐢辩瑪杩癸級銆?    ///
    /// 鈿?**鍚嶅崟閲岀暀鐫€闀挎柟浣?/ 鍥涢潰浣?*锛?026-09-20 绗崄浜屾壒鎾ょ殑鏄?*闈㈡澘鍏ュ彛**锛?    /// 涓嶆槸鐢绘硶锛夛細瀹冧滑鐢诲嚭鏉ヤ粛鐒舵槸鍥惧舰锛屾棫鏉夸功閲岀殑閭ｄ簺瑕佽兘閫変腑銆佽兘绉诲姩銆佽兘鍒犮€?    /// 涔熷氨鏄?**鑳界敾**"姣?**鏈夊叆鍙?*"瀹戒竴灞傗€斺€旀暟杞存槸杩欎釜閬撶悊鐨勭涓€渚?    /// 锛堣 璁″垝-鍥惧舰宸ュ叿.md 11.2 涓?FullUi.HasShapeEntry锛夈€?/summary>
    internal static bool IsShapeTool(Tool t)
        => t is Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Circle
             or Tool.Triangle or Tool.Parallelogram or Tool.Arrow
             or Tool.Coordinate or Tool.NumberLine
             or Tool.Parabola or Tool.Hyperbola or Tool.Sine or Tool.Cosine
             or Tool.Wave or Tool.Tangent
             // 妞渾锛堝甫鐒︾偣锛夛紙2026-09-22锛夛細鍜岀浜岃閭ｄ簺鏇茬嚎鍚屼竴鏃忊€斺€?             // 瀹冧篃鏄?涓€鎸変竴鎷栧嚭涓€涓弬鏁板寲瀵硅薄"锛屼笉鏄嚜鐢辩瑪杩广€?             or Tool.ConicEllipse
             or Tool.Cylinder or Tool.Cone or Tool.Cuboid or Tool.Tetrahedron
             or Tool.Prism or Tool.Pyramid or Tool.Frustum
             or Tool.ConeFrustum or Tool.Sphere;

    /// <summary>
    /// **涓€绗?*鍋氫粈涔堬細鎶?杩欎竴绗旀嫋鍒扮殑浣嶇疆"鍐欒繘鍗婃垚鍝佺殑鍑犱綍銆?    ///
    /// 鍙傛暟鍚箟锛歚s` = 鍗婃垚鍝佸璞°€乣origin` = **绗?1 绗旀寜涓嬬殑閭ｄ釜鐐?*锛堝弻鏇茬嚎鐨勪腑蹇冿紱
    /// 鍚庨潰鍑犵瑪娌跨敤锛屼笉鍦ㄨ嚜宸遍偅涓€绗旈噸璁撅級銆乣p` = 杩欎竴绗旀寚閽堟墍鍦ㄧ殑浣嶇疆銆?    /// `minSize` = 鍑犱綍涓嬮檺锛堣 <see cref="ShapeMinAxisLogical"/>锛岄槻"闆跺昂瀵稿浘褰?锛夈€?    /// </summary>
    private delegate void StepApply(Stroke s, Vector2 origin, Vector2 p, float minSize);

    /// <summary>
    /// 涓€绉?*澶氱瑪鍥惧舰**鐨勮〃锛?*涓€绗斾竴琛?*锛坄Apply` 鐨勯暱搴?= 杩欑鍥惧舰瑕佹嫋鍑犵瑪锛夈€?    ///
    /// 涓轰粈涔堣鏀舵垚涓€寮犺〃锛堢敤鎴?2026-09-20 瀹氾細"鐓т粬鐨勮鍒欏鍒烩€︹€︾敤鐘舵€佹満"锛夛細
    /// InkClass 閲?鐜板湪鐢诲埌绗嚑绗?杩欎欢浜?*鎶勪簡涓夐亶**鈥斺€斿弻鏇茬嚎鐢ㄨ嚜宸辩殑
    /// `drawMultiStepShapeCurrentStep`銆侀暱鏂逛綋鐢ㄨ嚜宸辩殑 `CuboidStrokeCollection`銆?    /// 鍥涢潰浣撶敤鑷繁鐨?`isFirstTouchTetrahedron`锛屼笁浠藉悇鍐欏悇鐨勩€佸悇鍦?MouseUp 閲屽悇鍒や竴娆°€?    /// 鎴戜滑鏀舵垚**涓€琛岃〃**锛氫互鍚庡姞涓€绉嶅绗斿浘褰?= **琛ㄩ噷鍔犱竴琛?锛?鍐欏畠閭ｅ嚑绗旂殑绠楀紡**锛?    /// 鎺ㄨ繘 / 鎻愪氦 / 浣滃簾 / 瑙︽懜閭ｅ閫昏緫涓€涓瓧閮戒笉鐢ㄥ姩銆?    ///
    /// 琛ㄩ噷**鏆傛椂涓嶅啓鎻愮ず鏂囨**锛氭彁绀烘槸 搂18 閭ｄ欢浜嬶紙鐢ㄦ埛瀹?鏈€鍚庡仛"锛夛紝
    /// 鍒版椂鍊欏湪杩欎竴琛屾梺杈瑰姞涓€鍒楀氨澶熶簡銆?    /// </summary>
    private sealed class StepPlan
    {
        public StepApply[] Apply = Array.Empty<StepApply>();
    }

    /// <summary>
    /// **鍙屾洸绾匡細涓ょ瑪**锛堢収 InkClass 鐨?`drawingShapeMode = 24/25`锛宍MW_ShapeDrawing.cs:1051-1152`锛夛細
    ///
    ///   绗?1 绗旓細**浠庝腑蹇冩嫋鍑烘笎杩戠嚎**锛坄A = |dx|銆丅 = |dy|`锛?鎷栧埌鍝氨鏄摢"锛夛紱
    ///   绗?2 绗旓細**鎷栧埌鍝€佹洸绾垮氨缁忚繃鍝?*锛堜腑蹇冩部鐢ㄧ 1 绗旈偅涓級銆?    ///
    /// 绠楀紡鍚勫彧鏈変竴浠姐€侀兘鍦ㄦā鍨嬪眰锛?see cref="Stroke.SetHyperbolaFromAsymptote"/> /
    /// <see cref="Stroke.SetHyperbolaThroughPoint"/>锛夛紝杩欏紶琛ㄥ彧璐熻矗"绗嚑绗旇皟鍝竴涓?銆?    /// </summary>
    private static readonly StepPlan HyperbolaPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, min) => s.SetHyperbolaFromAsymptote(o.X, o.Y, p.X, p.Y, min),
            static (s, o, p, _) => s.SetHyperbolaThroughPoint(p.X, p.Y),
        },
    };

    /// <summary>
    /// **闀挎柟浣擄細涓ょ瑪**锛堢収 InkClass 鐨?`case 9`锛夆€斺€旂 1 绗旀闈㈢煩褰€佺 2 绗旀繁搴︺€?    /// </summary>
    private static readonly StepPlan CuboidPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetCuboidFront(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetCuboidDepth(p.X, p.Y),
        },
    };

    /// <summary>**鍥涢潰浣擄細涓ょ瑪**锛堢収浠栫殑 `case 26`锛夆€斺€旂 1 绗斿簳闈笁瑙掑舰銆佺 2 绗旈《鐐广€?/summary>
    private static readonly StepPlan TetrahedronPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetTetraBase(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetTetraApex(p.X, p.Y),
        },
    };

    /// <summary>
    /// **妫辨煴 / 妫遍敟 / 妫卞彴锛氫袱绗?*锛?026-09-20 鐢ㄦ埛鎻愮殑锛岃 璁″垝-鍥惧舰宸ュ叿.md 搂32 / 搂34锛夆€斺€?    /// 绗?1 绗旀嫋鍑?*搴曢潰澶栨帴妗?*锛堝唴鎺ユ n 杈瑰舰锛夈€佺 2 绗旀嫋鍒?*椤朵笂閭ｄ釜涓績**
    ///锛堟１鏌?= 椤堕潰涓績銆佹１閿?= 椤剁偣銆佹１鍙?= 涓婂簳涓績锛夈€?    ///
    /// 鈿?**涓夊厔寮熷叡鐢ㄨ繖涓€寮犺〃銆佷竴涓瓧閮戒笉鐢ㄦ敼**鈥斺€斿洜涓哄畠浠殑鎺у埗鐐?*瀹屽叏涓€鏍?*
    ///锛堝簳闈㈠鎺ユ涓よ 锛?椤朵笂閭ｄ釜涓績锛夛紝宸埆鍙湪妯″瀷閲?椤朵笂閭ｄ釜涓績绠楀嚭鏉ョ殑闈?    /// 闀夸粈涔堟牱"锛坄Stroke.PrismTopLocal`锛夈€傝繖灏辨槸"涓€涓姩浣滃畾涓夌绔嬩綋"鐨勭敱鏉ャ€?    ///
    /// 鍜屽洓闈綋**鍚屼竴濂楀彛寰?*锛堢 2 绗?鎷栧埌鍝氨鏄摢"锛夛紝鎵€浠?寰€涓婃嫋 = 鐩存１鏌?/ 鐩存１閿?/
    /// 鐩存１鍙般€佹嫋姝?= 鏂滅殑"鏄悓涓€涓姩浣滅殑鑷劧缁撴灉锛?鐩?閭ｄ竴妗ｇ殑**杞诲井鍚搁檮**鍦?    /// `ApplyStepGeometry` 閲岃ˉ锛堥偅涓€姝ヤ笉鍦ㄨ〃閲岋紝鍥犱负瀹冭璇?鎹㈡。浣嶄箣澶栫殑鐘舵€侊級銆?    /// </summary>
    private static readonly StepPlan PrismPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetPrismBase(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetPrismApex(p.X, p.Y),
        },
    };

    /// <summary>
    /// 杩欑宸ュ叿鏄笉鏄?*澶氱瑪**鐨勶紱鏄殑璇濆畠鐨勮〃鍦ㄥ摢锛堣 <see cref="StepPlan"/>锛夈€?    /// **鍒ゆ嵁鍙湁杩欎竴澶?*锛氭寜涓嬶紙璧峰崐鎴愬搧 / 鎺ョ潃鏀癸級涓庢澗鎵嬶紙鎺ㄨ繘杩樻槸鎻愪氦锛夐兘闂畠锛?    /// 鍚勫啓涓€浠藉悕鍗曞氨鏄?鍔犱竴绉嶅浘褰㈠繀婕忎竴澶?鐨勮€佹瘺鐥呫€?    /// </summary>
    private static StepPlan PlanOf(Tool t) => t switch
    {
        Tool.Hyperbola => HyperbolaPlan,
        Tool.Cuboid => CuboidPlan,
        Tool.Tetrahedron => TetrahedronPlan,
        // 妫辨煴 / 妫遍敟 / 妫卞彴**鍏辩敤涓€寮犺〃**锛堟帶鍒剁偣瀹屽叏涓€鏍凤紝瑙?PrismPlan 鐨勬敞閲婏級鈥斺€?        // 鎵€浠?鍔犱竴绉嶇珛浣?杩欎欢浜嬪湪杩欓噷鏄?*闆舵敼鍔?*銆?        Tool.Prism or Tool.Pyramid or Tool.Frustum => PrismPlan,
        _ => null,
    };

    /// <summary>
    /// 杩欑鍥惧舰**瑕佹嫋鍑犵瑪**锛坄PlanOf` 閭ｅ紶琛ㄧ殑琛屾暟锛涗竴绗旂敾瀹岀殑缁?1锛夈€?    ///
    /// **涓轰粈涔堣浠庤〃閲岀畻銆佽€屼笉鏄鑷鑷繁鍒椾竴浠?鍝簺鏄绗?**锛?    /// 鑷閲岄偅浠藉悕鍗曚竴鏃﹁窡涓嶄笂锛堣繖涓€杞姞妫遍敟 / 妫卞彴鏃跺氨婕忎簡锛夛紝缁撴灉涓嶆槸"绾?锛?    /// 鑰屾槸**鑷鎸変竴绗斿幓鎷栥€佸绗斿浘褰㈡牴鏈敾涓嶅嚭鏉?*鈥斺€旂劧鍚庢柇瑷€閲岄偅鍙?    /// `s != null && s.Kind == d.kind` 鎵嶇孩锛屾姤鍑烘潵鐨勫嵈鏄?娌＄敾鍑烘潵"锛?    /// 鐪嬬潃鍍忓姛鑳藉潖浜嗐€佸叾瀹炴槸鑷鍐欐紡浜嗐€備粠琛ㄩ噷绠楀氨姘歌繙鍚屾锛?    /// **鍔犱竴绉嶅绗斿浘褰?= 琛ㄩ噷鍔犱竴琛岋紝鑷鑷姩璺熺潃鎷栫瑪銆?*
    /// </summary>
    internal static int ShapeStepCount(Tool t)
    {
        var plan = PlanOf(t);
        return plan == null || plan.Apply.Length < 1 ? 1 : plan.Apply.Length;
    }

    /// <summary>
    /// **澶氱瑪鍥惧舰鐨勭 1 绗?*鎸変笅锛氳捣涓€鏉?*杩樻病杩涙枃妗?*鐨勫崐鎴愬搧锛屽苟璁颁笅"绗竴涓偣"
    ///锛堝弻鏇茬嚎鐨?*涓績**锛夆€斺€?鍚庨潰鍑犵瑪閮藉洿鐫€瀹冪畻銆侷nkClass 涔熸槸杩欎釜鍙ｅ緞锛?    /// 绗簩绗斿埢鎰?*涓嶉噸璁?* `iniP`锛坄NeedUpdateIniP()`锛宍:1971-1977`锛夈€?    /// </summary>
    private void BeginStepShape(Tool tool, StepPlan plan, float x, float y)
    {
        BeginShapeAt(tool, x, y);
        _stepPlan = plan;
        _stepIndex = 0;
        _stepOrigin = new Vector2(x, y);
    }

    /// <summary>
    /// 鎶?**褰撳墠杩欎竴绗?*銆佹寚閽堝湪 `(x, y)`"鍐欒繘鍗婃垚鍝佺殑鍑犱綍锛堟煡琛?鈫?璋冩ā鍨嬮偅涓€浠界畻寮忥級銆?    ///
    /// **鎸変笅鍜屾嫋鍔ㄩ兘璋冨畠**锛氭寜涓嬩篃璋冿紝鏄负浜?鍦ㄧ洰鏍囦綅缃偣涓€涓嬨€佹病鎬庝箞鎷?涔熺畻鏁?    ///锛堣€佸笀寰堝彲鑳藉氨鐩存帴鐐瑰湪瑕佺粡杩囩殑鍦版柟锛夈€?    /// </summary>
    private bool ApplyStepGeometry(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || _stepPlan == null) return false;
        if (_stepIndex < 0 || _stepIndex >= _stepPlan.Apply.Length) return false;
        _stepPlan.Apply[_stepIndex](s, _stepOrigin, new Vector2(x, y),
                                    ShapeMinAxisLogical * DpiScale);

        // **妫辨煴绗?2 绗旂殑"鐩存１鏌?杞诲井鍚搁檮**锛堢敤鎴?2026-09-20锛?鐩存１鏌辨湁涓€涓交寰惛闄?锛夆€斺€?        // 鍐欏湪**杩欎竴姝ヤ箣鍚?*锛氬厛鎸夋寚閽堣€佽€佸疄瀹炶惤涓€娆★紝鍐嶇湅瑕佷笉瑕佹妸瀹冩壋鐩淬€?        //
        // 涓轰粈涔堟斁鍦ㄨ繖閲岃€屼笉鏄斁杩?`PrismPlan` 閭ｅ紶琛細琛ㄩ噷鐨勬瘡涓€琛屾槸**绾畻寮?*
        //锛坄(s,o,p,min) => 鈥锛屼笉璇诲紩鎿庣姸鎬併€佷笉鍑虹姸鎬侊級锛岃€屽惛闄勮**鎶婄粨鏋滄姤缁欑晫闈?*
        //锛堝嚭銆岀洿妫辨煴銆嶉偅棰楄兌鍥婏紝瑙?`_stepSnap`锛夛紝杩樿鏈変竴浠藉宸父閲忋€傜暀鍦ㄨ繖閲屼袱杈归兘骞插噣銆?        //
        // 鍒ゆ嵁鐢?*闀垮害**锛?椤跺績鍜屽簳蹇冪殑妯悜宸?灏忎簬瀹瑰樊锛夛紝鍜?姝ｅ渾"閭ｆ潯鍚搁檮鍚屼竴涓彛寰勶細
        // "杞诲井鍚搁檮"灏辫鏄?鎵嬫姈涓€鐐圭偣涔熺畻鐩?锛岀敤瑙掑害鍦ㄦ煴瀛愬緢楂樻椂浼氬彉寰楀緢闅惧惛浣忋€?        //
        // **妫辨煴 / 妫遍敟 / 妫卞彴鍏辩敤杩欎竴鏉?*锛堥棶 `Stroke.IsPrismFamily`锛屼笉鍚勫啓涓€浠藉悕鍗曪級锛?        // 涓夊厔寮熺殑"鐩?鏄悓涓€浠朵簨鈥斺€?*椤朵笂閭ｄ釜涓績鍦ㄥ簳蹇冩涓婃柟**銆?        // 鎶ョ粰鐣岄潰鐨勮兌鍥婂悕瀛楁寜绉嶇被鍒嗭紙鐩存１鏌?/ 鐩存１閿?/ 鐩存１鍙帮紝瑙?UprightSnapOf锛夈€?        _stepSnap = ShapeSnapKind.None;
        if (_stepIndex == 1 && Stroke.IsPrismFamily(s.Kind))
        {
            float dx = MathF.Abs(s.PrismApexLocal().X - s.PrismBaseCenterLocal().X);
            if (dx <= PrismUprightToleranceLogical * DpiScale)
            {
                s.SnapPrismApexVertical();
                _stepSnap = UprightSnapOf(s.Kind);
            }
        }
        // 鑳跺泭鎸傚湪"**鍚稿畬涔嬪悗**閭ｄ釜椤堕潰涓績"涓婏紙鐢诲竷鍧愭爣锛夆€斺€旂粯鍒朵笌鑴忓尯閮借杩欎竴涓暟
        //锛堢悊鐢卞悓 VertexPreviewCanvasPoint锛氫袱杈瑰悇绠椾竴浠藉氨浼氬樊涓€甯с€佺暀娈嬪奖锛夈€?        StepSnapAnchor = Vector2.Transform(s.PrismApexLocal(), s.Transform);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// "鐩存１鏌?鍚搁檮鐨?*妯悜瀹瑰樊**锛堥€昏緫鍍忕礌锛夛細椤跺績鍜屽簳蹇冪殑妯潗鏍囧樊鍦ㄨ繖涓寖鍥村唴灏辨壋鐩淬€?    /// 12 鏄収"鎵嬫姈涓€涓ゅ儚绱?+ 鎶曞奖涓婄殑涓€鐐瑰亸"瀹氱殑锛涙瘮鐐归€夊宸紙4锛夊锛?    /// 鍥犱负杩欐槸涓?杞诲井鍚搁檮"锛屼笉鏄?绮剧‘鍒ゅ畾"銆?    /// </summary>
    internal const float PrismUprightToleranceLogical = 12f;

    /// <summary>
    /// 鍚稿埌绔栫洿鏃惰鎶ュ摢涓€棰楄兌鍥婏細**鍚嶅瓧鎸夌绫诲垎**锛堢洿妫辨煴 / 鐩存１閿?/ 鐩存１鍙帮級锛?    /// 鍥犱负鑰佸笀璇村嚭鍙ｇ殑鏄?杩欐槸涓洿妫遍敟"锛屼笉鏄缁熺殑"鍚镐綇浜?銆?    ///
    /// 鍚搁檮鐨?*鍒ゆ嵁**涓夊厔寮熷叡鐢紙閮藉湪 <see cref="ApplyStepGeometry"/> 閲岄偅涓€澶勶級锛?    /// 杩欓噷鍙槸鎶?鍚稿埌浜嗕粈涔?缈绘垚浜鸿瘽锛堟爣绛炬湰浣撳湪 `SelectionHandles.ShapeSnapLabel`锛夈€?    /// </summary>
    private static ShapeSnapKind UprightSnapOf(StrokeKind k) => k switch
    {
        StrokeKind.Pyramid => ShapeSnapKind.RightPyramid,
        StrokeKind.Frustum => ShapeSnapKind.RightFrustum,
        _ => ShapeSnapKind.RightPrism,
    };

    /// <summary>
    /// **澶氱瑪鍥惧舰褰撳墠閭ｄ竴绗斿惛鍒颁簡浠€涔?*锛堢幇鍦ㄥ彧鏈夋１鏌辩殑銆岀洿妫辨煴銆嶄竴妗ｏ級銆?    /// 鍜?<see cref="ShapeSnapKind"/> 鍚屼竴涓灇涓俱€佸悓涓€棰楄兌鍥娾€斺€旂敤鎴风湅鍒扮殑璇█瑕佷竴鑷淬€?    /// 鏉炬墜 / 鏀剁瑪鏃跺繀椤绘竻鎺夛紙瑙?`EndStroke`锛夛紝鍚﹀垯閭ｉ鑳跺泭浼氱暀鍦ㄥ睆骞曚笂銆?    /// </summary>
    internal ShapeSnapKind StepSnap => _stepSnap;
    private ShapeSnapKind _stepSnap = ShapeSnapKind.None;

    /// <summary>銆岀洿妫辨煴銆嶉偅棰楄兌鍥婃寕鍦ㄥ摢锛?*鐢诲竷鍧愭爣** = 鍚稿畬涔嬪悗鐨勯《闈腑蹇冿級銆?    /// 缁樺埗涓庤剰鍖洪兘璇诲畠锛屼笉璁稿悇绠椾竴浠斤紙鍚?<see cref="VertexPreviewCanvasPoint"/>锛夈€?/summary>
    internal Vector2 StepSnapAnchor;


    /// <summary>
    /// **澶氱瑪鍥惧舰鐨勭 1 绗?*閲?灞忓箷涓婂厛鍙嚭鐜颁竴鍗?鐨勯偅涓€涓細鍙屾洸绾胯繕娌″畾"鏇茬嚎缁忚繃鐨勭偣"鏃讹紝
    /// **鍙敾娓愯繎绾裤€佸厛涓嶇敾鏇茬嚎**鈥斺€旂収 InkClass锛堢涓€绗斿彧鐢讳袱鏉¤櫄绾挎笎杩戠嚎锛?    /// `MW_ShapeDrawing.cs:1059-1068`锛屾洸绾挎槸绗簩绗旀墠鍑虹幇鐨勶級銆?    ///
    /// 鍒ゆ嵁鐩存帴鐪?*妯″瀷鏈夋病鏈夌涓変釜鐐?*锛屼笉鍙﹁鐘舵€佷綅锛氭病鏈夐偅涓偣锛屾洸绾挎湰鏉ュ氨娌″畾涔?    ///锛坄HyperbolaCurveALocal` 鍙兘缁欎竴涓厹搴曞ぇ灏忥級锛岀敾鍑烘潵鏄亣鐨勩€?    /// 娓叉煋閭ｄ竴澶村湪 Overlay 閲岃瀹冿紙瑙?`DrawStrokeCore` 寮€澶寸殑璇存槑锛夈€?    /// </summary>
    internal bool HyperAsymptotePreviewOnly
        => ActiveStroke?.Kind == StrokeKind.Hyperbola && ActiveStroke.Points.Count < 3;

    /// <summary>鍥惧舰宸ュ叿 鈫?瀹冪敾鍑烘潵鐨勭绫汇€?*鍜?<see cref="IsShapeTool"/> 鍚屼竴浠藉悕鍗?*锛?    /// 鍔犲浘褰㈡椂涓ゅ閮借鍔紙鑷 `--shapebandtest` 鐨?鍚嶅崟涓€鑷?閭ｆ潯浼氬崱浣忥級銆?/summary>
    internal static StrokeKind KindOfShapeTool(Tool t) => t switch
    {
        Tool.Line => StrokeKind.Line,
        Tool.Rectangle => StrokeKind.Rectangle,
        Tool.Ellipse => StrokeKind.Ellipse,
        Tool.ConicEllipse => StrokeKind.ConicEllipse,
        Tool.Circle => StrokeKind.Circle,
        Tool.Triangle => StrokeKind.Triangle,
        Tool.Parallelogram => StrokeKind.Parallelogram,
        Tool.Coordinate => StrokeKind.Coordinate,
        Tool.NumberLine => StrokeKind.NumberLine,
        Tool.Parabola => StrokeKind.Parabola,
        Tool.Hyperbola => StrokeKind.Hyperbola,
        Tool.Sine => StrokeKind.Sine,
        Tool.Cosine => StrokeKind.Cosine,
        Tool.Wave => StrokeKind.Wave,
        Tool.Tangent => StrokeKind.Tangent,
        Tool.Cylinder => StrokeKind.Cylinder,
        Tool.Cone => StrokeKind.Cone,
        Tool.Cuboid => StrokeKind.Cuboid,
        Tool.Tetrahedron => StrokeKind.Tetrahedron,
        Tool.Prism => StrokeKind.Prism,
        Tool.Pyramid => StrokeKind.Pyramid,
        Tool.Frustum => StrokeKind.Frustum,
        Tool.ConeFrustum => StrokeKind.ConeFrustum,
        Tool.Sphere => StrokeKind.Sphere,
        _ => StrokeKind.Arrow,
    };

    /// <summary>
    /// 鎶涚墿绾?*涓€绗?*锛堢収 InkClass 鐨?`case 20/21`锛氶《鐐?鈫?鏈鐐癸紝涓€娆℃嫋瀹岋級锛?    ///
    ///   路 椤剁偣 = **鎸変笅閭ｄ竴鍒?*閭ｄ釜鐐癸紙瑙?<see cref="BeginShapeAt"/>锛夛紱
    ///   路 **寮€鍙ｆ湞鍝竟**鐢辫繖涓€鎷栫殑绗﹀彿瀹氾紙瑙?<see cref="Stroke.ParabolaAxisOfDrag"/>锛?    ///     鈥斺€?闈㈡澘閭ｄ竴鏍煎彧鍥炵瓟"**涓婁笅杩樻槸宸﹀彸**"杩欎欢鎺ㄤ笉鍑烘潵鐨勪簨锛坄ParabolaAxis`锛夛紱
    ///   路 `p`锛堝紶鍙ｏ級涓?**鐢诲埌鍝?*"閮界敱杩欎竴鎷栧畾锛氭洸绾?*姝ｅソ鍋滃湪浣犳嫋鍒扮殑閭ｄ釜鐐?*涓?    ///     锛坄p = t虏/(2s)` 鍙嶈В锛涚敾鍑鸿寖鍥磋 `Stroke.ParabolaSpanOf`锛夈€?    ///
    /// 鐢ㄦ埛 2026-09-20 鐨勫彛寰勶紙鍘熻瘽锛夛細"鐜板湪杩欎釜鍙屾洸绾垮拰鎶涚墿绾跨殑鎰熻涓嶅锛岃繕鏄弬鑰冧粬鐨勯€昏緫鍚?
    /// 鈥斺€?涓婁竴鐗?鏂瑰悜鐢遍潰鏉块€夋"鐨勯棶棰樻槸锛氶潰鏉块€夌潃鍚戜笂銆佹墜鍗村線涓嬫嫋鏃跺弽瑙ｅ嚭璐熸暟锛?    /// 鏇茬嚎浼氬綋鍦虹缉鎴愪竴鏉＄粏閽堬紙`p` 鎺夊埌涓嬮檺锛夈€傛柟鍚戣窡鐫€鎷栧姩璧板氨鍐嶄篃涓嶄細鍑鸿繖绉嶄簨銆?    /// </summary>
    private bool UpdateParabolaPreview(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || s.Kind != StrokeKind.Parabola) return false;
        var v = new Vector2(s.Points[0].X, s.Points[0].Y);
        var q = new Vector2(x, y);
        // **鏂瑰悜鍏堝畾**锛歚SetParabolaVertex` 瑕佺敤瀹冩潵閾洪偅涓崰浣嶇偣锛堣妯″瀷閲岄偅娈垫敞閲婏級銆?        s.CurveAxis = Stroke.ParabolaAxisOfDrag(v, q, ParabolaAxis);
        s.SetParabolaVertex(v.X, v.Y);
        s.SetParabolaThroughPoint(q.X, q.Y);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **鎶涚墿绾垮伐鍏峰綋鍓嶇殑"鍝竴瀵?**锛堢敤鎴?2026-09-20 瀹氾細鐢?*涔嬪墠**瀹氬ソ锛屼笉鍦ㄩ€変腑妗嗛噷鏀光€斺€?    /// 浠栧師璇濇槸"閫変腑妗嗙殑鎶涚墿绾挎寜閽姛鑳藉彇娑堬紝鎴戜笉鎵撶畻浠庤繖涓浆鎶涚墿绾垮紑鍙?锛夈€?    ///
    /// 鍙袱妗ｃ€佷篃姝ｆ槸 InkClass 鐨勯偅涓や釜鎸夐挳锛?    ///   路 `OpenUp` = **涓婁笅鎶涚墿**锛堜粬鐨?`y = ax虏`锛宍case 20`锛夛紱
    ///   路 `OpenRight` = **宸﹀彸鎶涚墿**锛堜粬鐨?`y虏 = ax`锛宍case 21`锛夈€?    ///
    /// **鍏蜂綋鏈濆摢杈逛笉鍦ㄨ繖閲?*鈥斺€旂敱鐢荤殑鏃跺€欓偅涓€鎷栫殑绗﹀彿瀹氾紙瑙?`Stroke.ParabolaAxisOfDrag`锛夈€?    /// 2026-09-20 鏅氱敤鎴风湅杩囦箣鍚庡畾锛?鎰熻涓嶅锛岃繕鏄弬鑰冧粬鐨勯€昏緫"锛?    /// 鏂瑰悜鐢遍潰鏉块€夋鏃讹紝"閫夌潃鍚戜笂銆佹墜鍗村線涓嬫嫋"浼氳鏇茬嚎缂╂垚涓€鏉＄粏閽堛€?    /// 闈㈡澘鍙洖绛旀帹涓嶅嚭鏉ョ殑閭ｄ欢浜嬶紙涓婁笅杩樻槸宸﹀彸锛夛紝杩炵画閲忥紙鏈濆摢杈广€佸澶с€佸闀匡級鍏ㄤ氦缁欐墜銆?    ///
    /// 瀹冨彧鍦ㄤ袱澶勫嚭鐜帮細鍥惧舰闈㈡澘閲?*鍐嶇偣涓€娆￠偅涓€鏍?*鎹竴妗ｏ紙瑙?FullUi.ActivateSegment 涓?    /// <see cref="CycleParabolaAxis"/>锛夛紝浠ュ強鐢荤殑鏃跺€欏啓杩涘璞★紙瑙?<see cref="BeginShapeAt"/>锛夈€?    ///
    /// 瀛樻垚**寮曟搸瀛楁**銆佷笉瀛樺亸濂芥枃浠讹細涓€娆¤閲岃繛鐢诲嚑鏉″悓鍚戠殑鎶涚墿绾挎槸甯告€侊紝
    /// 鐣欑潃涓婁竴娆￠偅涓柟鍚戞瘮姣忔鍥?涓婁笅"椤烘墜锛涗絾涔熺姱涓嶄笂璺ㄨ繘绋嬭鐫€锛屾墍浠ヤ笉杩涘亸濂姐€?    /// </summary>
    public CurveAxis ParabolaAxis { get; private set; } = CurveAxis.OpenUp;

    /// <summary>
    /// 鎹笅涓€妗?鍝竴瀵?锛?*涓婁笅 鈫?宸﹀彸 鈫?涓婁笅**锛堣 <see cref="ParabolaAxis"/>锛夈€?    /// 鍙奖鍝?*涓嬩竴绗?*鐢诲嚭鏉ョ殑鎶涚墿绾匡紝涓嶇宸茬粡鐢诲ソ鐨勫璞°€?    /// </summary>
    public void CycleParabolaAxis()
    {
        ParabolaAxis = ParabolaAxis == CurveAxis.OpenUp ? CurveAxis.OpenRight : CurveAxis.OpenUp;
        // 闈㈡澘涓婇偅涓€鏍肩殑鍥炬爣瑕佽窡鐫€鎹紝鎵€浠ユ帹涓€娆＄姸鎬侊紙鍚屾椂鏍囪剰锛孒UD 涔嬬被涔熻瀹冿級銆?        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **鐩寸嚎閭ｄ竴鏍煎綋鍓嶇殑绾垮瀷**锛堝疄绾?/ 铏氱嚎 / 鐐圭嚎锛夈€傜敤鎴?2026-09-20 瀹氾細
    /// "鐐瑰嚮鐩寸嚎鐨勫浘鏍囷紝瀹冧細鍙樻垚铏氱嚎锛屽啀鐐瑰嚮鍙樻垚鐐硅櫄绾匡紝鍐嶇偣鍑诲張鍙樻垚鐩寸嚎鈥︹€?    /// **杩欐牱灏辩渷浜嗗ソ鍑犱釜绌洪棿鏍?*"銆?    ///
    /// 涓轰粈涔堝彧缁欑洿绾裤€佷笉缁欏叏閮ㄥ浘褰細閭ｄ竴鏍肩偣绗簩涓?*瑕佹湁鏂板惈涔?*鎵嶆暍杩欎箞鐢紝
    /// 鑰屽浘褰㈤潰鏉块噷鐜板湪鍙湁"鎶涚墿绾挎崲寮€鍙ｆ柟鍚?鍗犵潃杩欎釜鍔ㄤ綔锛堣 `FullUi.ActivateSegment`锛夈€?    /// 鐩寸嚎鏄敾寰楁渶澶氱殑涓€绉嶏紝铏氬疄绾垮張鏄€佸笀鏈€甯哥敤鐨勪袱绉嶏紙杈呭姪绾裤€佸欢闀跨嚎锛夛紝
    /// 鎵€浠ュ厛缁欏畠锛涘埆鐨勫浘褰互鍚庣湡闇€瑕侊紝鐓ц繖涓ā瀛愬姞鍗冲彲銆?    ///
    /// **瀹冩槸"涓嬩竴绗旂敤鍝"锛屼笉鏄?鏉夸笂閭ｄ簺鐩寸嚎鐜板湪鏄粈涔?**鈥斺€斿凡缁忕敾濂界殑鍚勫瓨鍚勭殑
    /// 锛堣 <see cref="Stroke.Dash"/>锛夛紝瑕佹敼瀹冧滑璧伴€変腑鍚庣殑鎿嶄綔鏉￠潰鏉匡紙`SetSelectionDash`锛夈€?    /// 鍜?`PenDash` 閭ｆ潯鍙ｅ緞涓€鏍凤紝鍖哄埆鍙槸"鍝敮宸ュ叿鍚冨畠"銆?    ///
    /// 瀛樻垚**寮曟搸瀛楁**銆佷笉瀛樺亸濂芥枃浠讹紙鍜?<see cref="ParabolaAxis"/> 鍚屼竴鏉＄悊鐢憋級锛?    /// 涓€娆¤閲岃繛鐢诲嚑鏉¤櫄绾挎槸甯告€侊紝鐣欑潃涓婁竴妗ｆ瘮姣忔鍥炲疄绾块『鎵嬨€?    /// </summary>
    public StrokeDash LineDash { get; private set; } = StrokeDash.Solid;

    /// <summary>
    /// 鎹笅涓€妗ｇ洿绾跨嚎鍨嬶細**瀹炵嚎 鈫?铏氱嚎 鈫?鐐圭嚎 鈫?瀹炵嚎**锛堣 <see cref="LineDash"/>锛夈€?    /// 鍙奖鍝?*涓嬩竴绗?*鐢诲嚭鏉ョ殑鐩寸嚎锛屼笉纰板凡缁忕敾濂界殑瀵硅薄銆?    /// </summary>
    public void CycleLineDash()
    {
        // 涓夋。涓€杞細Solid(0) 鈫?Dashed(1) 鈫?Dotted(2) 鈫?Solid銆?        // 鐢ㄥ彇妯¤€屼笉鏄垪涓撅紝鏄负浜嗕互鍚庤鍔犵鍥涙。锛堟瘮濡傜偣鍒掔嚎锛夋椂鍙敼杩欓噷涓€鍙ヨ瘽銆?        LineDash = (StrokeDash)(((int)LineDash + 1) % 3);
        // **璁颁綇绾垮瀷**锛堝疄绾?/ 铏氱嚎 / 鐐圭嚎锛岀敤鎴?2026-09-30 瀹氾細閲嶅惎鍥炴潵杩樻槸瀹冿級
        SetUiPref("lineDash", ((int)LineDash).ToString());
        // 闈㈡澘涓婇偅涓€鏍肩殑鍥炬爣瑕佽窡鐫€鎹紝鎵€浠ユ帹涓€娆＄姸鎬侊紙鍜屾姏鐗╃嚎鎹㈡湞鍚戝悓涓€濂楋級銆?        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **绔嬩綋閭ｄ竴鏍煎綋鍓嶇殑妗?*锛氬簳闈㈠嚑杈瑰舰锛?~6锛夈€傜敤鎴?2026-09-20 瀹氾細
    /// "鎴戞兂鎯宠兘涓嶈兘鍋氭垚鍍忕洿绾垮垏鎹㈤偅鏍峰垏鎹笁鍥涗簲鍏?鈥斺€斾簬鏄畠鍜?<see cref="LineDash"/>
    /// **瀹屽叏鍚屾瀯**锛氶潰鏉夸笂閭ｄ竴鏍?*鍐嶇偣涓€娆℃崲涓€妗?*锛堥厤 4 涓。浣嶇偣锛夛紝
    /// 鐢荤殑閭ｄ竴鍒诲啓杩涘璞★紙瑙?`BeginShapeAt`锛夈€?    ///
    /// 鍜?<see cref="LineDash"/> 涓€鏍峰彧绠?**涓嬩竴绗?*"锛氬凡缁忕敾濂界殑妫辨煴鍚勫瓨鍚勭殑
    ///锛堣 <see cref="Stroke.PrismSides"/>锛夛紝闈㈡澘鎹㈡。涓嶄細鍥炲ご鏀瑰畠浠€?    /// 瀛樻垚寮曟搸瀛楁銆佷笉瀛樺亸濂芥枃浠讹紙鍜屾姏鐗╃嚎鏈濆悜銆佺洿绾跨嚎鍨嬪悓涓€鏉＄悊鐢憋級锛?    /// 涓€娆¤閲岃繛鐢诲嚑涓悓涓€绉嶆１鏌辨槸甯告€侊紝鐣欑潃涓婁竴妗ｆ瘮姣忔鍥炲埌榛樿椤烘墜銆?    ///
    /// 鈿?**妫辨煴 / 妫遍敟 / 妫卞彴鍚勮鍚勭殑妗ｏ紝涓嶆槸涓€涓叡浜殑妗?*鈥斺€旂敤鎴?2026-09-20 涓婃墜灏卞彂鐜?    /// "鍒囦竴涓彟澶栦袱涓篃鍔?锛岄偅鏄?bug锛氫竴涓汉瀹屽叏鍙兘"鍥涙１鏌遍厤涓夋１閿?锛?    /// 涓夋牸鍏辩敤涓€涓暟鍙嶈€屾病娉曡〃杈俱€傛墍浠ヨ繖閲屾槸**涓変釜瀛楁**锛岄棶璋佽闂?    /// <see cref="SidesFor"/>锛?*鍞竴鍒ゆ嵁**锛屽埆鍦ㄥ闈㈠悇鍐欎竴浠?switch锛夈€?    /// </summary>
    private int _sidesPrism = Stroke.DefaultPrismSides;
    private int _sidesPyramid = Stroke.DefaultPrismSides;
    private int _sidesFrustum = Stroke.DefaultPrismSides;

    /// <summary>
    /// **鏌愪釜绔嬩綋宸ュ叿褰撳墠閭ｄ竴妗?*锛堝簳闈㈠嚑杈瑰舰锛?~6锛夈€備笉鏄繖涓€鏃忕殑宸ュ叿缁欓粯璁ゆ。
    ///锛堣皟鐢ㄦ柟涔熷氨涓嶄細鎷垮畠鍘荤敾浠€涔堬級銆?    /// </summary>
    public int SidesFor(Tool tool) => tool switch
    {
        Tool.Pyramid => _sidesPyramid,
        Tool.Frustum => _sidesFrustum,
        Tool.Prism => _sidesPrism,
        _ => Stroke.DefaultPrismSides,
    };

    /// <summary>
    /// 鎹?*褰撳墠宸ュ叿**閭ｄ竴妗ｏ細**3 鈫?4 鈫?5 鈫?6 鈫?3**锛堣 <see cref="SidesFor"/>锛夈€?    /// 鍙奖鍝?*涓嬩竴绗?*鐢诲嚭鏉ョ殑閭ｄ竴涓紝鏃笉纰板凡缁忕敾濂界殑瀵硅薄锛屼篃**涓嶅姩鍙﹀涓ゆ牸**
    ///锛?妫辨煴鎹㈡。 鈫?妫遍敟鐨勬。璺熺潃璧?鏄敤鎴?2026-09-20 鎶ョ殑 bug锛夈€?    /// </summary>
    public void CycleSolidSides()
    {
        var t = this.Tool;
        if (!ShapeSpec.HasSideCount(t)) return;          // 涓嶆槸閭ｄ竴鏃忓氨浠€涔堜篃涓嶅仛
        // 鍥涙。涓€杞紝**鎸夎竟鏁板崌搴?*锛堢敤鎴峰畾锛氫笁/鍥?浜?鍏級鈥斺€斿崌搴忔瘮"鎸夊父鐢ㄥ害"鏇村ソ璁帮紝
        // 鑰屼笖妗ｄ綅鐐逛粠涓婂線涓嬭鍑烘潵灏辨槸 3/4/5/6锛屼笉鐢ㄩ澶栬椤哄簭銆?        int cur = SidesFor(t);
        int next = cur >= Stroke.MaxPrismSides ? Stroke.MinPrismSides : cur + 1;
        switch (t)
        {
            case Tool.Pyramid: _sidesPyramid = next; break;
            case Tool.Frustum: _sidesFrustum = next; break;
            default: _sidesPrism = next; break;
        }
        // 闈㈡澘涓婇偅涓€鏍肩殑鍥炬爣瑕佽窡鐫€鎹紝鎵€浠ユ帹涓€娆＄姸鎬侊紙鍜岀洿绾挎崲绾垮瀷鍚屼竴濂楋級銆?        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **鍙屾洸绾块偅涓€鏍煎綋鍓嶇殑妗?*锛氱敾涓嶇敾閭ｄ袱鏉¤櫄绾挎笎杩戠嚎锛堢敤鎴?2026-09-22 鎻愮殑
    /// "澧炲姞涓ゆ尅锛屾湁娓愯繎绾垮拰鏃犳笎杩戠嚎"锛夈€?    ///
    /// 鍜?<see cref="LineDash"/> / <see cref="CycleSolidSides"/> **瀹屽叏鍚屾瀯**锛?    /// 闈㈡澘涓婇偅涓€鏍?*鍐嶇偣涓€娆℃崲涓€妗?*锛堥厤 2 涓。浣嶇偣銆佸浘鏍囪窡鐫€鎹級锛?    /// 鐢荤殑閭ｄ竴鍒诲啓杩涘璞★紙瑙?<see cref="BeginShapeAt"/> 涓?<see cref="EndStroke"/>锛夈€?    ///
    /// 鈿?**鐢荤殑鏃跺€欎竴寰嬬敾娓愯繎绾?*锛堢敤鎴?2026-09-22 鐨勫師璇濓細"鍖栫殑鏃跺€欐槸閮芥湁娓愯繎绾匡紝
    /// 浣嗘槸鏈€缁堟樉绀烘病鏈?锛夆€斺€旈偅涓ゆ潯铏氱嚎鏄?*鐢绘硶鐨勫悜瀵?*锛堢涓€姝ュ氨鏄?浠庝腑蹇冩嫋鍑烘笎杩戠嚎妗?锛?    /// 瑙?<see cref="HyperbolaPlan"/>锛夛紝杈圭敾杈圭湅鏄繀椤荤殑锛涘畠鎸夋。**鏀跺彛鍦ㄦ澗鎵嬮偅涓€鍒?*
    ///锛堣 <see cref="EndStroke"/> 閲岀殑鎻愪氦閭ｄ竴娈碉級銆?    ///
    /// 瀛樻垚寮曟搸瀛楁銆佷笉瀛樺亸濂芥枃浠讹紙鍜屾姏鐗╃嚎鏈濆悜銆佺洿绾跨嚎鍨嬨€佺珛浣撹竟鏁板悓涓€鏉＄悊鐢憋級锛?    /// 涓€鑺傝閲岃繛鐢诲嚑鏉″悓涓€绉嶅弻鏇茬嚎鏄父鎬侊紝鐣欑潃涓婁竴妗ｆ瘮姣忔鍥為粯璁ら『鎵嬨€?    /// </summary>
    public bool HyperbolaAsymptotes { get; private set; } = true;

    /// <summary>鎹笅涓€妗?鍙屾洸绾跨敾涓嶇敾娓愯繎绾?锛?*鏈?鈫?鏃?鈫?鏈?*锛堣 <see cref="HyperbolaAsymptotes"/>锛夈€?/summary>
    public void CycleHyperbolaAsymptotes()
    {
        HyperbolaAsymptotes = !HyperbolaAsymptotes;
        // 闈㈡澘涓婇偅涓€鏍肩殑鍥炬爣瑕佽窡鐫€鎹紝鎵€浠ユ帹涓€娆＄姸鎬侊紙鍜屾崲妫辨煴杈规暟鍚屼竴濂楋級銆?        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **妞渾锛堝甫鐒︾偣锛夐偅涓€鏍煎綋鍓嶇殑妗?*锛氱敾涓嶇敾鐒︾偣涓夎褰紙鐢ㄦ埛 2026-09-22 鎻愮殑
    /// "涓ゆ。锛氭湁鐒︾偣涓夎褰㈠拰娌℃湁鐒︾偣涓夎褰?锛?*涓や釜鐒︾偣涓ゆ。閮界敾**锛岃
    /// <see cref="Stroke.FocusTriangle"/>锛夈€?    ///
    /// 鍜?<see cref="HyperbolaAsymptotes"/> 鍚屼竴濂楋細闈㈡澘閭ｄ竴鏍煎啀鐐逛竴娆℃崲妗ｃ€?    /// 鐢荤殑閭ｄ竴鍒诲啓杩涘璞°€佸彧绠?涓嬩竴绗?锛堝凡缁忕敾濂界殑鍚勫瓨鍚勭殑锛夈€?    /// 榛樿**鏈?*锛氳繖涓€鏍肩殑瀛樺湪鎰忎箟灏辨槸璁茬劍鐐逛笁瑙掑舰锛堝彧瑕佷竴涓き鍦嗙殑璇濈敤绗竴琛岄偅涓き鍦嗭級銆?    /// </summary>
    public bool EllipseFocusTriangle { get; private set; } = true;

    /// <summary>鎹笅涓€妗?妞渾鐢讳笉鐢荤劍鐐逛笁瑙掑舰"锛?*鏈?鈫?鏃?鈫?鏈?*锛堣 <see cref="EllipseFocusTriangle"/>锛夈€?/summary>
    public void CycleEllipseFocusTriangle()
    {
        EllipseFocusTriangle = !EllipseFocusTriangle;
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 鍥惧舰宸ュ叿鐨勮捣鎵嬶細閫犱竴鏉?*鍙湁璧风偣**鐨勫浘褰紝鎷栧姩鏈熺敱
    /// <see cref="UpdateShapePreview"/> 鏀规帶鍒剁偣锛屾澗鎵嬬敱 <see cref="EndStroke"/> 鎻愪氦銆?    ///
    /// 鍒绘剰**涓嶅杺棰勬祴鍣ㄣ€佷篃涓嶈捣濮旀墭澧ㄨ抗**锛氶偅鏄?绗斿皷璺熸墜"鐢ㄧ殑锛?    /// 鑰屽浘褰㈣窡鐫€鎸囬拡璧扮殑鏄?*鍚搁檮鍚庣殑鎺у埗鐐?*鈥斺€斾袱濂楃敾鍦ㄥ睆骞曚笂浼氬彉鎴愪袱鏉′笉涓€鏍风殑绾裤€?    /// </summary>
    private void BeginShapeAt(Tool tool, float x, float y)
    {
        var kind = KindOfShapeTool(tool);
        ResetPredictTail();   // B4锛氬浘褰㈠伐鍏蜂笉棰勬祴锛屼絾鎶婁笂涓€绗旂殑灏惧反娓呮帀
        ActiveStroke = new Stroke
        {
            Tool = tool,
            Kind = kind,
            Color = CurrentColor,
            Width = PenWidthLogical * DpiScale,
            // 鍧愭爣绯昏涓嶈缃戞牸 = **鐢荤殑閭ｄ竴鍒婚偅涓紑鍏崇殑鐘舵€?*锛堣 CoordGridDefault锛夈€?            // 瀛樺埌瀵硅薄鑷繁韬笂锛屼箣鍚庡崟鐙敼瀹冧笉褰卞搷鍒殑鍧愭爣绯伙紙瑙?Stroke.Grid 鐨勬敞閲婏級銆?            Grid = kind == StrokeKind.Coordinate && CoordGridDefault,
            // 鎶涚墿绾跨殑**寮€鍙ｆ柟鍚戞槸鐢讳箣鍓嶉€夊ソ鐨?*锛堣 ParabolaAxis锛夛細鐢荤殑閭ｄ竴鍒诲啓杩涘璞★紝
            // 涔嬪悗瀹冨氨鏄繖鏉℃洸绾胯嚜宸辩殑灞炴€э紝鍜屽伐鍏峰綋鍓嶉偅妗ｅ啀鏃犲叧绯汇€?            CurveAxis = kind == StrokeKind.Parabola ? ParabolaAxis : CurveAxis.OpenUp,
            // 鐩寸嚎鐨?*绾垮瀷涔熸槸鐢讳箣鍓嶉€夊ソ鐨?*锛堣 LineDash锛夛細鍚屾牱鍦ㄧ敾鐨勯偅涓€鍒诲啓杩涘璞°€?            // 鍒殑鍥惧舰涓€寰嬪疄绾库€斺€斿畠浠殑绾垮瀷鍘嗘潵鏄?閫変腑涔嬪悗鍦ㄦ搷浣滄潯闈㈡澘閲屾敼"銆?            Dash = kind == StrokeKind.Line ? LineDash : StrokeDash.Solid,
            // 绔嬩綋閭ｄ竴鏃忕殑**搴曢潰鍑犺竟褰?*鍚屾牱鏄敾涔嬪墠閫夊ソ鐨勶紙瑙?SidesFor锛夛細
            // 鐢荤殑閭ｄ竴鍒诲啓杩涘璞★紝涔嬪悗闈㈡澘鍐嶆崲妗ｄ篃涓嶅洖澶存敼瀹冦€?            // 鈿?涓夊厔寮熼兘瑕佽蛋杩欎竴鍙モ€斺€?026-09-20 绗竴鐗堝啓鎴?`kind == StrokeKind.Prism`锛?            // 缁撴灉**妫遍敟 / 妫卞彴姘歌繙鐢绘垚鍥涙１**锛堢敤鎴蜂笂鎵嬩竴鍙ュ氨鎶撳嚭鏉ヤ簡锛夈€?            PrismSides = Stroke.IsPrismFamily(kind) ? SidesFor(tool) : Stroke.DefaultPrismSides,
            // 妞渾锛堝甫鐒︾偣锛夌殑**鐒︾偣涓夎褰㈠紑鍏?*鍚屾牱鏄敾涔嬪墠閫夊ソ鐨勶紙瑙?EllipseFocusTriangle锛夛細
            // 鐢荤殑閭ｄ竴鍒诲啓杩涘璞★紝涔嬪悗闈㈡澘鍐嶆崲妗ｄ篃涓嶅洖澶存敼瀹冦€?            FocusTriangle = kind != StrokeKind.ConicEllipse || EllipseFocusTriangle,
            // 鍙屾洸绾跨殑娓愯繎绾?*鐢荤殑鏃跺€欎竴寰嬬敾**锛堥偅鏄敾娉曠殑鍚戝锛岃 HyperbolaAsymptotes锛?            // 鈥斺€旀寜妗ｆ敹鍙ｅ湪鏉炬墜閭ｄ竴鍒伙紙瑙?EndStroke 鐨勬彁浜ら偅涓€娈碉級銆?            ShowAsymptotes = true,
        };
        ActiveStroke.AddPoint(x, y, 1f, NowMs);
        // 鎶涚墿绾跨殑**椤剁偣 = 鎸変笅閭ｄ釜鐐?*锛氬畠鐜板湪鏄?*涓€绗旂敾瀹?*鐨勶紙鐓?InkClass 鐨?`case 20/21`锛?        // 椤剁偣 鈫?鏈鐐癸紝涓€娆℃嫋瀹岋級锛屾墍浠ラ《鐐瑰湪杩欓噷灏辫惤瀹氾紝鎷栧姩鍙礋璐?鏇茬嚎杩囧摢銆佸紑澶氬ぇ"
        //锛堣 UpdateParabolaPreview锛夈€傚绗斿浘褰㈢殑绗竴涓偣涓嶅湪杩欓噷瀹氾紙瀹冧滑鍚勬湁鍚勭殑绠楀紡锛夈€?        if (kind == StrokeKind.Parabola) ActiveStroke.SetParabolaVertex(x, y);
        // 璇绘暟鐘舵€佷粠杩欎竴鍒婚噸鏂板紑濮嬶細涓嶆竻鐨勮瘽锛屼笂涓€娆＄敾绾垮惛浣忕殑閭ｄ釜寮鸿皟鑹蹭細婕忓埌
        // 杩欎竴娆＄殑绗竴甯э紙杩樻病鏀跺埌绉诲姩娑堟伅锛屛?涔熻繕娌＄畻锛夈€?        _shapeInclination = 0f;
        _shapeLength = 0f;
        _shapeInclinationSnapped = false;
        _shapeAnchor = new Vector2(x, y);
        // 涓夎褰?/ 骞宠鍥涜竟褰㈡槸"澶栨 鈫?涓変釜椤剁偣"锛屾嫋鍔ㄦ湡姣忎竴甯ч兘瑕佹嬁**鎸変笅閭ｄ竴鍒?*鐨?        // 閭ｄ釜瑙掑幓绠楀妗嗏€斺€斿畠涓嶅湪鎺у埗鐐硅〃閲岋紙鎺у埗鐐瑰凡缁忚鎺ㄦ垚涓変釜椤剁偣浜嗭級銆?        _shapeBoxOrigin = new Vector2(x, y);
        ActiveStrokeHasPressure = false;
        LastCoalescedSamples = LastCoalescedMessages = 0;
    }

    /// <summary>
    /// 鎷栧姩涓洿鏂板浘褰㈢殑鎺у埗鐐广€?    ///
    /// 鍚搁檮鍙**鏈夋柟鍚戠殑涓や釜**锛堢洿绾?/ 绠ご锛夊仛锛氱煩褰㈠拰妞渾鏄?涓や釜瀵硅鐐?/ 涓績+澶栬鐐?
    /// 瀹氫箟鐨勶紝娌℃湁"鍊炬枩瑙?杩欏洖浜嬶紝缁曡捣鐐硅浆鍙細鎶婄敤鎴锋媺鍑烘潵鐨勬鏀瑰皬锛堣 璁″垝-鍥惧舰宸ュ叿.md 8.2
    /// 閭ｄ竴琛屽彧鍐欎簡"鐢荤嚎鍚搁檮"锛夈€係hift = 15掳 纭綉鏍笺€丄lt = 瀹屽叏鑷敱锛屽拰鏃嬭浆鍚屼竴濂楄涔夈€?    /// 涓夎褰?/ 骞宠鍥涜竟褰㈡病鏈?鍊炬枩瑙?鍙惛锛屽畠浠殑"鐗规畩褰㈢姸鍚搁檮"鍙戠敓鍦?*鎷栭《鐐?*鏃?    /// 锛堣 璁″垝-鍥惧舰宸ュ叿.md 9.6锛氱敾鐨勬椂鍊欎笉鍚革紝鏀圭殑鏃跺€欐墠鍚革級銆?    /// </summary>
    private void UpdateShapePreview(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || s.Points.Count == 0) return;

        var start = new Vector2(s.Points[0].X, s.Points[0].Y);
        var end = new Vector2(x, y);

        // 涓夎褰?/ 骞宠鍥涜竟褰細澶栨锛堟寜涓嬬偣 锛?鎸囬拡锛夆啋 **涓変釜鎺у埗鐐?*涓€娆＄畻鍑烘潵銆?        if (s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram)
        {
            s.SetShapeBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 鍧愭爣绯?/ 鏁拌酱锛氬悓鏍锋槸"澶栨 鈫?涓€娆＄畻鍑哄叏閮ㄦ帶鍒剁偣"锛屽彧鏄帶鍒剁偣鏄洓涓€?        // 鎷栧姩鏈熷氨蹇呴』鍐欐垚**鏈€缁堥偅涓€浠藉畾涔?*锛屽惁鍒欓瑙堝拰鏉炬墜鐨勭粨鏋滀細宸竴涓?        // 锛堝拰涓夎褰㈤偅鏉″悓涓€涓悊鐢憋細涓ゅ绠楁硶 = 鏉炬墜灏辫烦锛夈€?        // 鏁拌酱鍦ㄨ繖閲岄『鎵嬫妸 y 閽夊湪鎸変笅鐐归偅涓€琛屼笂锛堝線鏂滀笂鏂规嫋涔熻繕鏄按骞崇嚎锛夈€?        if (s.Kind is StrokeKind.Coordinate or StrokeKind.NumberLine)
        {
            s.SetAxisBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // **澶氱瑪鍥惧舰**锛氳繖涓€绗旂殑鍑犱綍浜ょ粰琛ㄩ噷閭ｄ竴琛岋紙瑙佽〃 StepPlan锛夈€?        // **鍙湁鎸変綇鎷栧姩鎵嶆洿鏂?*鈥斺€擨nkClass 灏辨槸杩欐牱锛堝姝ュ浘褰㈢殑鍑犱綍鍙湪 `MouseTouchMove` 閲屾洿鏂帮紝
        // 鎸囬拡涓嶆寜閿椂鍗婃垚鍝佷竴鍔ㄤ笉鍔級銆傝繖涔熸槸"瑙︽懜灞忎篃鑳界敤"鐨勬牴瀛愶細鎵嬫寚娌℃湁鎮仠锛?        // 鑰屾瘡涓€姝ユ湰鏉ュ氨鏄?鎸変綇鎷?鏉炬墜"锛岀瑪 / 榧犳爣 / 鎵嬫寚璧扮殑鏄?*鍚屼竴鏉¤矾**銆?        if (_stepPlan != null)
        {
            ApplyStepGeometry(x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 鎶涚墿绾胯蛋**涓€绗?*锛堢収 InkClass 鐨?`case 20/21`锛夛細椤剁偣鍦ㄦ寜涓嬮偅涓€鍒诲氨瀹氫笅浜?        //锛堣 BeginShapeAt锛夛紝杩欎竴鎷栧彧瀹?鏇茬嚎缁忚繃鍝釜鐐广€佸紑澶氬ぇ"銆?        if (s.Kind == StrokeKind.Parabola)
        {
            UpdateParabolaPreview(x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 绔嬩綋鍥惧舰锛?*鏃嬭浆浣撻偅涓€鏃?*锛氬渾鏌?/ 鍦嗛敟 / 鍦嗗彴 / 鐞冿級锛?*澶栨帴鐭╁舰 鈫?涓€娆＄畻鍑哄叏閮ㄥ嚑浣?*
        //锛堝拰鍧愭爣绯婚偅鏉″悓涓€涓璺細鎷栧姩鏈熷氨鍐欐垚鏈€缁堥偅涓€浠藉畾涔夛紝鍚﹀垯棰勮鍜屾澗鎵嬬殑缁撴灉浼氬樊涓€涓嬶級銆?        //
        // 鈿?鍒ゆ嵁璧?`Stroke.IsRevolutionSolid`锛?*涓嶅啀鍐?鍦嗘煴 or 鍦嗛敟"**锛?        // 2026-09-20 鍔犲渾鍙版椂灏辨紡浜嗚繖涓€澶勶紙褰撴椂闈?娌¤蛋鍒拌繖鍎夸篃鑳界敾瀵?钂欒繃鍘讳簡鈥斺€?        // 鍏滃簳閭ｆ潯 `SetEnd` 鍐欒繘鍘荤殑瑙掔偣铏界劧娌″綊涓€锛屼絾 `SolidRectLocal` 鑷繁浼?min/max锛?        // 缁撴灉涓€鏍凤級銆傚姞鐞冩椂鎶婂畠涓€骞舵敹鎴愬垽鎹嚱鏁帮細**鍔犱竴绉嶆棆杞綋涓嶇敤鍐嶆潵琛ヨ繖涓€琛?*銆?        if (Stroke.IsRevolutionSolid(s.Kind))
        {
            s.SetSolidBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 姝ｅ鸡 / 浣欏鸡 / 娉㈡氮绾匡細鎸変笅 = **璧风偣**锛?浠?y 杞村紑濮嬬敾"锛夛紝鎷栧嚭鍘?= **缁堢偣**銆?        // 姝ｅ鸡 / 浣欏鸡锛氳繖涓€鎷栧畾涓?*涓€涓懆鏈?*鍜?*鎸箙**锛堟瀹藉氨鏄竴涓懆鏈燂級锛?        // 娉㈡氮绾匡細杩欎竴鎷栧畾涓?*鐢诲闀?*鍜?*鎸箙**锛堝懆鏈熺敱鎸箙瀹氾紝瑙?Stroke.WavePeriodLocal锛夈€?        // 涓夎€呯殑绠楀紡鍚屼竴浠斤紝宸埆鍙湪 `WavePeriodLocal` 閭ｄ竴琛屻€?        if (s.Kind is StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave)
        {
            s.SetWaveBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, ShapeMinAxisLogical * DpiScale);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 姝ｅ垏锛氭寜涓?= **鍘熺偣**锛堣繖涓€鏀殑涓績锛夛紝鎷栧嚭鍘?= **浠ュ畠涓轰腑蹇冪殑妗?*鈥斺€?        // 妯悜鏄崐鏀暱锛堟笎杩戠嚎姝ｅソ钀芥杈癸級銆佺旱鍚戞槸鍙鍗婇珮锛堣秴鍑哄氨鎴柇锛夈€?        // 瀹冨拰姝ｅ鸡/浣欏鸡**涓嶄竴鏍?*锛氭寮︽槸鎶?璧风偣"鎷栧埌缁堢偣锛堣捣鎵嬬偣鐣欏湪鍘熷湴锛夛紝
        // 姝ｅ垏鏄?*涓績涓嶅姩銆佸洓鍛ㄤ竴璧烽暱**銆傛墍浠ヨ皟鐢ㄧ殑鏄?`SetTangentBox` 涓嶆槸 `SetWaveBox`銆?        if (s.Kind == StrokeKind.Tangent)
        {
            s.SetTangentBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, ShapeMinAxisLogical * DpiScale);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
        {
            bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
            end = SelectionHandles.SnapEndPoint(start, end, shift, alt, out bool snapped);
            // 鐢荤嚎杩囩▼涓篃鎶?伪 鎶ョ粰娴姩灞傦紙鐢ㄦ埛 2026-09-18 瑕佺殑锛氳竟鐢昏竟鐪嬭繖鏉＄嚎鏄灏戝害锛夈€?            // 伪 鐢?*鏉冨▉鍑芥暟**绠楋紝杩欓噷鍙槸鎶婂畠鍜?鍚告病鍚镐綇"涓€璧峰瓨涓嬫潵锛屼緵娓叉煋涓庤剰鍖虹敤銆?            _shapeInclination = SelectionHandles.InclinationDegrees(start, end);
            _shapeLength = Vector2.Distance(start, end);
            _shapeInclinationSnapped = snapped;
        }
        s.SetEnd(end.X, end.Y);
        _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
    }

    /// <summary>
    /// 姝ｅ湪鐢讳竴鏉?*鏈夊€炬枩瑙?*鐨勫浘褰紙鐩寸嚎 / 绠ご锛夈€?    ///
    /// 鐭╁舰鍜屾き鍦嗘槸涓や釜瀵硅鐐瑰畾涔夌殑锛屾病鏈?鍊炬枩瑙?杩欏洖浜嬶紝鎵€浠ヤ笉缁欒鏁?    /// 锛堢敾绾胯鏁板彧瀵圭洿绾?绠ご锛岃 璁″垝-鍥惧舰宸ュ叿.md 8.2锛夈€?    /// </summary>
    internal bool ShapeInclinationActive
        => ActiveStroke != null && ActiveStroke.Kind is StrokeKind.Line or StrokeKind.Arrow;

    /// <summary>鐢荤嚎涓偅鏉＄嚎**褰撳墠缁撴灉**鐨?伪锛圼0掳,180掳)锛夛紱涓嶅湪鐢荤嚎鏃舵槸 0銆?/summary>
    internal float ShapeInclinationDegrees => _shapeInclination;

    /// <summary>鐢荤嚎涓偅鏉＄嚎**褰撳墠缁撴灉**鐨勯暱搴︼紙鐢诲竷鍍忕礌锛夈€傚拰 伪 鍚屾簮銆佸悓涓€鏃跺埢鏇存柊锛?    /// 鏍囩鎶婁袱涓暟涓€璧锋姤锛?026-10-05 鐢ㄦ埛锛?鍐嶇粰鐩寸嚎澧炲姞涓€涓暱搴︼紝鍜屽害鏁版槸鍚屼竴涓€昏緫"锛夈€?/summary>
    internal float ShapeLength => _shapeLength;

    /// <summary>鐢荤嚎涓殑 伪 鏄?鍚?鍑烘潵鐨勫悧锛堢壒娈婅 / Shift 缃戞牸锛夆€斺€旀爣绛炬寜瀹冨彉鑹层€?/summary>
    internal bool ShapeInclinationSnapped => _shapeInclinationSnapped;

    /// <summary>鐢荤嚎涓偅涓細琚嫋鍔ㄧ殑绔偣锛堢敾甯冨潗鏍囷級锛氳鏁版爣绛捐创瀹冨渚с€?/summary>
    internal Vector2 ShapeInclinationAnchor => _shapeAnchor;

    private float _shapeInclination;
    private float _shapeLength;
    private bool _shapeInclinationSnapped;
    private Vector2 _shapeAnchor;
    /// <summary>
    /// 鍥惧舰璧锋墜鏃舵寜涓嬬殑閭ｄ釜鐐癸紙鐢诲竷鍧愭爣锛夈€傚彧鏈変笁瑙掑舰 / 骞宠鍥涜竟褰㈢敤寰楀埌锛?    /// 瀹冧滑鏄?澶栨 鈫?涓変釜椤剁偣"锛岃€屽妗嗙殑涓や釜瑙掗噷鏈変竴涓紙鎸変笅鐨勯偅涓級**涓嶅湪鎺у埗鐐硅〃閲?*锛?    /// 蹇呴』鍦ㄨ捣鎵嬮偅涓€鍒昏涓嬫潵锛屽惁鍒欐嫋鍔ㄤ腑鍋氫笉鍑?澶栨"杩欎釜涓棿閲忋€?    /// </summary>
    private Vector2 _shapeBoxOrigin;

    /// <summary>
    /// 杩欐鎷栨嫿澶熶笉澶熼暱锛堜笉澶熷氨褰撴病鐢伙紝浠€涔堜篃涓嶇暀锛夈€?    /// 鍒ゆ嵁鐢?*鐢诲竷鍧愭爣涓嬬殑瀹為檯璺濈**锛岃€屼笉鏄?鏀舵病鏀跺埌杩囩Щ鍔ㄦ秷鎭?鈥斺€?    /// 鎵嬫姈涓€涓嬩篃浼氭敹鍒扮Щ鍔ㄦ秷鎭€?    /// </summary>
    private bool ShapeDragLongEnough(Stroke s)
    {
        if (s.Points.Count < 2) return false;
        // 鍧愭爣绯昏鍗曠嫭绠楋細瀹冪殑**鍘熺偣鏄寜涓嬬偣**锛堣 Stroke.SetAxisBox锛夛紝
        // 鑰?鎷栧嚭鍘荤殑閭ｄ竴涓? = 鍘熺偣 鈫?澶栨瑙掔殑璺濈锛堝妗嗚 = 鍘熺偣 卤 鎷栧姩閲忥紝鎵€浠ユ濂界瓑浜庢嫋鍔ㄩ噺锛夈€?        // 鎷垮妗嗗瑙掑幓鍒ょ殑璇濓紙= 鈭? 脳 鎷栧姩閲忥級浼氭妸"鍙嫋浜?3 鍍忕礌"涔熷綋鎴愬闀裤€?        if (s.Kind == StrokeKind.Coordinate && s.Points.Count >= 3)
        {
            var o = new Vector2(s.Points[2].X, s.Points[2].Y);
            var c = new Vector2(s.Points[0].X, s.Points[0].Y);
            return Vector2.Distance(o, c) >= ShapeMinDragLogical * DpiScale;
        }
        var a = new Vector2(s.Points[0].X, s.Points[0].Y);
        var b = new Vector2(s.Points[^1].X, s.Points[^1].Y);
        return Vector2.Distance(a, b) >= ShapeMinDragLogical * DpiScale;
    }

    /// <summary>
    /// 鎶婅繖鏉℃寚閽堟秷鎭噷鐨?*鍏ㄩ儴**閲囨牱鐐硅拷鍔犺繘褰撳墠绗旂敾銆?    ///
    /// 鐪熺瑪璧板悎骞剁偣锛坄GetPointerPenInfoHistory`锛岃 <c>Input/PenInput.cs</c>锛夛細
    /// 绯荤粺浼氭妸鏉ヤ笉鍙婃姇閫掔殑绉诲姩鍚堝苟杩涗竴鏉℃秷鎭紝鍙彇鏈€鏂伴偅涓€涓瓑浜庢妸閲囨牱鐜囩爫鍗娿€?    /// 闈炵瑪璁惧銆佹垨璇讳笉鍒板悎骞剁偣鏃堕€€鍥?涓€涓秷鎭竴涓偣"鐨勮€佽矾锛堣涓轰笌浠ュ墠瀹屽叏涓€鑷达級銆?    ///
    /// 鍧愭爣锛氱瑪鐢诲瓨**鐢诲竷**鍧愭爣锛涢娴嬪櫒鍠?*灞忓箷**鍧愭爣锛堟箍澧ㄨ建杩逛篃鏄睆骞曠┖闂达級銆?    /// </summary>
    /// <summary>
    /// **杩欎竴绗旇蛋鐨勬槸鍝潯璺?* 鈥斺€?鐢ㄥぇ鐧借瘽鍐欏嚭鏉ャ€?    ///
    /// 涓轰粈涔堣鏈夊畠锛?026-10-07 鐢ㄦ埛鐨勫師璇濓級锛?**鎴戞€庝箞鐭ラ亾浠€涔堟椂鍊欐槸璧颁簡 ink锛熶粈涔堟椂鍊欒蛋浜?    /// 閭ｄ釜 wintab锛熶粈涔堟椂鍊欏張鏄紶鏍囨ā寮忚ˉ鐐癸紵**"
    ///
    /// 浠ュ墠杩欎簺淇℃伅鏁ｅ湪涓夊锛坄璁惧=榧犳爣` / `[wintab 鈥` / `[raw 琛ョ偣 +N]`锛夛紝
    /// 寰楄嚜宸卞璇绘墠鐭ラ亾 鈥斺€?**閭ｅ氨绛変簬娌″憡璇夌敤鎴?*銆傝繖閲屽悎鎴愪竴鍙ヤ汉璇濓紝涓€鐪肩湅鏄庣櫧銆?    /// </summary>
    private string DescribeInputPath(uint ptype)
    {
        if (ptype == Native.PT_PEN)
            return "寮€ ink锛圵indows Ink 绗旓級鈫?鍘嬪姏 1024 绾?;
        if (ptype == Native.PT_TOUCH)
            return "瑙︽懜";
        bool wt = WtPressurePoints > 0;
        bool raw = _rawPointsAdded > 0;
        if (wt && raw) return "鍏?ink 鈫?Wintab 缁欏帇鍔?锛?raw 琛ョ偣";
        if (wt) return "鍏?ink 鈫?Wintab 缁欏帇鍔涳紙杩欒疆琛ョ偣娌″嚭鍔涳級";
        if (raw) return "鍏?ink 鈫?鍙湁 raw 琛ョ偣锛?*娌℃湁 Wintab**锛屾墍浠ユ病鏈夊帇鍔涳級";
        return _wintab.IsOpen
            ? "鍏?ink 鈫?榧犳爣锛圵intab 寮€鐫€锛屼絾杩欎竴绗旀病鎷垮埌鍘嬪姏锛?
            : "榧犳爣锛圵intab 鏈惎鐢級";
    }

    private void AppendStrokeSamples(uint id, uint ptype, float curCanvasX, float curCanvasY,
                                     float screenX, float screenY, float curPressure)
    {
        if (ActiveStroke == null) return;

        // `--rawinput`锛氳繖涓€鏉℃寚閽堟秷鎭粰鍑虹殑鏄?*缁濆浣嶇疆** 鈫?鎶婂畠璁炬垚閿氱偣銆佺疮璁℃竻闆躲€?        // 杩欐槸"璇樊涓嶆紓"鐨勫叧閿細raw 鎶ョ殑浣嶇Щ鍙湪涓ゆ潯鎸囬拡娑堟伅涔嬮棿绱姞锛?        // 姣忔潵涓€鏉℃寚閽堟秷鎭氨閲嶆柊瀵瑰噯涓€娆＄湡鍊笺€?        ResetRawAnchor(screenX, screenY);

        // 椤轰究缁撶畻"鏈夋病鏈変腑闂寸偣鍙崬"锛氭妸杩欎竴娈电殑鍘熷鎶ユ暟骞惰繘绱锛屽啀鐪嬫瘮鍊笺€?        // 闃堝€?1.3 鏄暀浜嗕綑閲忕殑鈥斺€?:1 璁惧鐨勫疄娴嬫瘮鍊煎氨鏄?1.0锛屾湁鍚堝苟鐨勫疄娴?2.3~4銆?        _ptrMsgsThisStroke++;
        _rawTotalSincePtr += _rawSinceLastPtr;
        _rawSinceLastPtr = 0;
        if (_ptrMsgsThisStroke >= 3)
            _rawHasIntermediates = _rawTotalSincePtr >= _ptrMsgsThisStroke * 1.3;

        if (ptype == Native.PT_PEN && _pen.Read(id, NowMs, _lastInputMsgQpc) > 0)
        {
            LastCoalescedMessages++;
            LastCoalescedSamples += _pen.Count;
            PenMessages++;
            PenSamples += _pen.Count;
            PenSawPressureMask |= _pen.AnyPressure;
            PenSawTiltMask |= _pen.HasTilt;
            PenSawRotationMask |= _pen.Last.Rotation != 0;
            for (int i = 0; i < _pen.Count; i++)
            {
                var s = _pen[i];
                float cx = s.X, cy = s.Y;
                ScreenToCanvas(ref cx, ref cy);
                // 鈿?**鐪熺瑪杩欐潯涔熻鍑€鍖?*锛?026-10-07锛夛細鐢ㄦ埛鎶ョ殑鏄?寮€涓嶅紑 ink 閮借剰"锛?                // 鑰屽紑 ink 璧扮殑灏辨槸杩欎竴鏉°€傛澘瀛愭姤鐨勪綅缃瘺鍒烘槸**鍚屼竴涓潵婧?*锛?                // 涓嶄細鍥犱负璧颁簡 Windows Ink 灏辫嚜宸卞彉骞插噣銆?                AddPointCleaned(cx, cy, s.Pressure, s.TimeMs);
                PenTotalPoints++;
                if (s.HasPressure) PenPressurePoints++;
            }
            ActiveStrokeHasPressure |= _pen.AnyPressure;
            // 鍘嬫劅鏄?*鏁寸瑪鐨勫睘鎬?*锛堣 Stroke.HasPressure锛夛細杩欎竴绗斿彧瑕佹湁涓€鏉℃秷鎭姤杩囨湁鏁堝帇鍔涳紝
            // 瀹冨氨浠庢鍒昏捣鎸夊帇鎰熸覆鏌撱€?*褰撳満鍐欒繘瀵硅薄**锛堣€屼笉鏄覆鏌撴椂鍐嶉棶涓€娆★級锛?            // 鍥犱负婀垮ⅷ銆佸共澧ㄣ€佺揣妗嗐€佸瓨妗ｈ鐨勯兘鏄繖涓€涓爣蹇椼€?            //
            // **鍘嬫劅鍙綔鐢ㄤ簬銆岀瑪銆嶈繖涓€鏀?*锛?026-09-20 瀵归綈 WPF 鏃跺畾鐨勶紝涔熸槸瀹樻柟绀轰緥鐨勫仛娉曪細
            // 鑽у厜绗旂殑 `DrawingAttributes.IgnorePressure = true`锛夛細
            //   路 鑽у厜绗旀槸涓€鏀?骞冲ご椹厠绗?锛岀矖缁嗛殢鍘嬪姏鍙樹細璁╁垝鍑烘潵鐨勫甫瀛愬拷瀹藉拷绐勶紙婊″帇杩樻槸 2 鍊嶅锛夛紱
            //   路 婵€鍏夌瑪鍙槸鎸囦竴涓嬶紝娌℃湁"绗旇抗绮楃粏"杩欏洖浜嬨€?            if (ActiveStrokeHasPressure && ActiveStroke.Tool == Tool.Pen) ActiveStroke.HasPressure = true;
            return;
        }

        // ---- 榧犳爣 / 瑙︽懜锛氬悓涓€涓亾鐞嗭紝璇?POINTER_INFO 鐨勫悎骞剁偣 --------------------
        //
        // 杩欐潯浠ュ墠涓嶅瓨鍦紝浠ｄ环鍦ㄤ袱绫昏澶囦笂鍚屾椂浣撶幇锛氭墜鍐欐澘**娌″紑 Windows Ink** 鏃?        // 浠?PT_MOUSE 涓婃姤銆佽Е鎽稿睆鏄?PT_TOUCH锛屽畠浠竴鏉℃秷鎭彧鍙栨渶鏂伴偅涓€涓偣锛?        // 蹇啓鏃惰建杩硅闈欓粯鎶界█锛岃€屼笖棰勬祴鍣ㄦ嬁涓嶅埌瓒冲瀵嗙殑閫熷害浼拌銆?        if (ptype != Native.PT_PEN && _ptr.Read(id, NowMs, _lastInputMsgQpc) > 0)
        {
            LastCoalescedMessages++;
            LastCoalescedSamples += _ptr.Count;
            PtrMessages++;
            PtrSamples += _ptr.Count;
            // HistoryCount 鏄?绯荤粺鏈潵璇存湁鍑犳潯娑堟伅"锛屽噺鎺夋垜浠湡鐨勬敹涓嬬殑閭ｅ嚑鏉★紝
            // 灏辨槸琚悎骞舵帀鐨勪腑闂寸偣锛堝悎骞剁巼灏辨槸瀹冮櫎浠?HistoryCount锛夈€?            PtrCoalescedExtra += Math.Max(0, _ptr.HistoryCount - 1);

            // ---- Wintab锛氱粰杩欐潯"琚綋鎴愰紶鏍囩殑绗?琛ヤ笂鐪熷疄鍘嬪姏 ----------------------
            //
            // 涓轰粈涔堝彧鍦ㄨ繖鏉¤矾涓婅ˉ锛氱湡绗旓紙PT_PEN锛屽嵆寮€鐫€ ink锛夋湰鏉ュ氨鏈夊帇鍔涳紝
            // 鑰屼笖閭ｆ潯璺洿缁嗙殑鍙湁 Windows Ink 鐨?1024 绾р€斺€斾笉鐢ㄦ崲銆?*缂哄帇鍔涚殑鎭版伆鏄繖涓€鏉?*銆?            //
            // 鈿?**涓や釜"涓嶈兘琛?鐨勬儏鍐?*锛堥兘鏄兂娓呮鎵嶅啓鐨勶紝涓嶆槸璇曞嚭鏉ョ殑锛夛細
            //   鈶?**鐪熼紶鏍?*锛氫笉浜х敓 Wintab 鍖?鈫?鍙栦笉鍒板€?鈫?鑰佽涓洪€愬瓧涓嶅彉锛堥紶鏍囩敾鐨勪粛鏃х瓑瀹斤級銆?            //   鈶?**绗斿彧鏄偓鍋滃湪鏉夸笂**锛氭偓鍋滃帇鍔?*鏈潵灏辫鏄?0**锛圵acom 瀹樻柟鍙ｅ緞锛?            //      "鍖洪棿鏈€灏忓€艰〃绀洪潤姝?鏈寜涓?锛夈€傝鏄妸 0 褰撴湁鏁堝帇鍔涜ˉ杩涘幓锛?            //      鐢ㄦ埛"绗旀悂鍦ㄦ澘涓娿€佹墜涓婄敤榧犳爣鐢?鏃讹紝榧犳爣绾挎潯浼氳帿鍚嶅叾濡欏彉缁嗐€?            //      鈫?鎵€浠?*鍙 > 0**锛屽苟涓旀部鐢ㄧ幇鎴愮殑銆岀己鍘嬪洖濉€嶅彛寰?            //      锛堣 `Input/PenInput.cs` 鐨?`_lastValidPressure`锛夛細
            //      杩欎竴绗斿彧瑕佽杩囦竴娆＄湡瀹炲帇鍔涳紝鍚庨潰鍋跺皵缂轰竴涓嬪氨娌跨敤涓婁竴涓€硷紝鑰屼笉鏄帀鍥炰腑鐐广€?            // 缁欒繖涓€鎵圭偣鐨勫帇鍔涳細
            //   WtSeenPressure  鈫?宸茬粡鎷垮埌鐪熷疄鍘嬪姏锛岀敤瀹?            //   Wintab 寮€鐫€浣嗙涓€鍖呰繕娌″埌 鈫?**鐢?0 鍏滃簳锛屼笉鑳界敤 0.5**
            //   Wintab 娌″紑 鈫?-1锛堣蛋鑰佽涓?`s.Pressure`锛岄€愬瓧涓嶅彉锛?            //
            // 鈿?**涓轰粈涔堢涓€鍖呮病鍒拌鐢?0 鑰屼笉鏄?0.5**锛?026-10-07 鐢ㄦ埛鐪熸満鍙嶉"绗竴涓偣鑰佷細鏈夌偣绮?锛夛細
            //   0.5 鏄犲皠鍑烘潵鏄?*涓瓑**绮楃粏锛堚増1.05脳 鍚嶄箟瀹斤級锛岃€岃捣绗斿帇鍔?*鐗╃悊涓婂氨鏄帴杩?0**
            //   锛堢敤鎴风湡鏈哄疄娴嬫渶灏忓€?457/16383 鈮?3% 鈫?鈮?.15脳 瀹斤級銆?            //   浜庢槸姣忎竴绗斿紑澶撮兘浼氶紦鍑轰竴涓矖鐐广€佸悗闈㈡墠闅忓帇鎰熷彉缁嗐€?            //   **鍏滃簳鍊艰璐村悎鐗╃悊浜嬪疄**锛屼笉鏄彇涓?涓棿鍊?鏈€淇濋櫓 鈥斺€?涓棿鍊煎湪杩欓噷鎭版伆鏄敊鐨勩€?            float wp = -1f;
            bool wpIsReal = false;
            float wpPrev = 0f;      // 鏈壒鐨?*鎻掑€艰捣鐐?*锛堜笂涓€鎵圭敤鐨勯偅涓帇鍔涘€硷級
            // Wintab **鍙湪鐪熺殑璧板埌杩欐潯闈炵瑪璺緞鏃?*鎵嶅紑锛堣 BeginStroke 閲岄偅娈垫敞閲婏級锛?            // 寮€ ink 鐨勭瑪鍏ㄧ▼涓嶄細纰板埌瀹冿紝闅旂鏄粨鏋勬€х殑銆?            if (WintabEnabled && !_wintab.IsOpen && !_wintabTried)
            {
                EnsureWintab();
                if (_wintab.IsOpen) _wintab.Flush();   // 鍊掓帀绌洪棽鏈熼棿鎺掗槦鐨勮繃鏈熷寘
            }
            if (_wintab.IsOpen && PressureWidth.Enabled)
            {
                float before = _wtStrokePressure;     // 涓婁竴鎵圭敤鐨勫帇鍔?                bool wasReal = WtSeenPressure;

                bool gotWt = _wintab.Poll();
                WtPolledPackets += _wintab.PacketsRead - _wintabPacketsSeen;
                _wintabPacketsSeen = _wintab.PacketsRead;
                WtBadPackets += _wintab.BadPackets - _wintabBadSeen; _wintabBadSeen = _wintab.BadPackets;
                if (gotWt && _wintab.Pressure01 > 0f)
                    AcceptWintabPressure(_wintab.RawPressure, _wintab.Pressure01);
                if (gotWt) NoteWintabSample();

                // 娉ㄦ剰锛氳繖閲岀殑 0 鍙槸"缁欑偣鐢ㄧ殑鍊?锛?*涓嶆敼鍙?HasPressure** 鈥斺€?                // 鐪熼紶鏍囷紙涓嶄骇鐢?Wintab 鍖咃級浠嶇劧涓嶄細琚綋鎴愭湁鍘嬫劅锛岃€佽涓轰笉鍙楀奖鍝嶃€?                wp = WtSeenPressure ? _wtStrokePressure : 0f;
                wpIsReal = WtSeenPressure;
                // 鎻掑€艰捣鐐?= 鏈壒**涔嬪墠**閭ｄ釜鍘嬪姏鍊笺€?                // 绗竴鎵圭湡瀹炲帇鍔?*涓嶆彃鍊?*锛堣捣鐐瑰氨鐢ㄦ湰鎵圭殑鍊硷級鈥斺€?鍜?鍥炲～"淇濇寔涓€鑷达紝
                // 鍚﹀垯浼氭妸鍒氬洖濉ソ鐨勫紑澶村張鎷夊洖鍏滃簳鐨?0锛岀櫧濉€?                wpPrev = wasReal ? before : wp;
            }
            if (wpIsReal)
            {
                WtPressurePoints += _ptr.Count;      // **鏁扮偣锛屼笉鏄暟璋冪敤**
                ActiveStrokeHasPressure = true;
                // 鍜岀湡绗旈偅鏉¤矾鍚屼竴鏉¤鐭╋紙瑙佷笂闈?PT_PEN 鍒嗘敮锛夛細**鍘嬫劅鍙綔鐢ㄤ簬銆岀瑪銆嶈繖涓€鏀?*銆?                if (ActiveStrokeHasPressure && ActiveStroke.Tool == Tool.Pen) ActiveStroke.HasPressure = true;
            }

            // 鈿?**鍘嬪姏瑕佹寜"鐐瑰湪杩欐壒閲岀殑浣嶇疆"鎽婂紑**锛?026-10-07锛岀敤鎴蜂竴鍙ヨ瘽閽夋浜嗙梾鍥狅級锛?            // "鍏充簡 wintab 寮€ ink 灏辨甯搞€佸紑浜?wintab 鍏?ink 灏卞緢鑴?
            //  鈫?浣嶇疆涓ゆ潯璺槸**鍚屼竴浠?*锛堟垜浠彧鍊熷帇鍔涳級锛屽樊鍒?*鍙湪鍘嬪姏鍒嗚鲸鐜?*锛?            //     寮€ ink 閭ｆ潯璺槸**閫愮偣鑷甫鍘嬪姏**锛堥┍鍔ㄧ粰鐨勫悎骞剁偣閲屾瘡鐐归兘鏈夛級锛?            //     鎴戣繖鏉″師鏉ュ彧鍙?杩欎竴鎵规渶鍚庝竴涓寘"鐨勫帇鍔涖€佸缁欒繖鎵规墍鏈夌偣 鈫?**瀹藉害鏄彴闃?*銆?            // 鑰岃澶囩殑鍖呯巼锛?92Hz锛夋瘮娑堟伅鐜囷紙60~80Hz锛夐珮寰楀 鈥斺€?**涓€鎵圭偣鏈潵瀵瑰簲鐫€濂藉嚑涓寘**锛?            // 閭ｄ簺鍖呯殑鍘嬪姏浠ュ墠琚垜鎵斾簡銆傜幇鍦?`Samples` 鎶婂畠浠兘甯﹀嚭鏉ワ紝鎸変綅缃憡鍒扮偣涓娿€?            int nSample = _wintab.IsOpen ? _wintab.Samples.Count : 0;
            if (nSample > 0)
            {
                // 鍓嶅悜濉厖锛氭牱鏈噷鐨?0锛堟偓鍋?鏈寜涓嬶級娌跨敤涓婁竴涓湁鏁堝€?                // 鈥斺€?鍚?`Input/PenInput.cs` 鐨勩€岀己鍘嬪洖濉€嶅彛寰勩€?                float carry = _wtStrokePressure > 0f ? _wtStrokePressure : 0f;
                for (int j = 0; j < nSample && j < _sampleScratch.Length; j++)
                {
                    float v = _wintab.Samples[j];
                    if (v > 0f) carry = v;
                    _sampleScratch[j] = carry;
                }
            }
            if (!wpIsReal) _wtPendingPoints += _ptr.Count;   // 杩欎簺鐐圭瓑鐪熷疄鍘嬪姏鏉ヤ簡瑕佸洖濉?            int nPtr = _ptr.Count;
            for (int i = 0; i < nPtr; i++)
            {
                var s = _ptr[i];
                float cx = s.X, cy = s.Y;
                ScreenToCanvas(ref cx, ref cy);
                float pp;
                if (wp < 0f) pp = s.Pressure;                     // Wintab 娌″紑 鈫?鑰佽涓洪€愬瓧涓嶅彉
                else if (nSample <= 0) pp = wp;                   // 杩欐娌″彇鍒板寘 鈫?娌跨敤涓婁竴涓?                else pp = _sampleScratch[nPtr <= 1 ? nSample - 1
                                                   : (int)MathF.Round(i * (nSample - 1) / (float)(nPtr - 1))];
                AddPointCleaned(cx, cy, pp, s.TimeMs);
                PtrTotalPoints++;
            }
            // 闈炵瑪璁惧娌℃湁 penMask锛屼篃灏辨案杩滀笉浼氱粰杩欎竴绗旀墦涓?HasPressure鈥斺€?            // 杩欐鏄?WPF / 寰蒋鐧芥澘閲?榧犳爣鐢荤殑閭ｆ潯绾挎槸绛夊"鐨勬潵婧愩€?            return;
        }

        // ---- 璇讳笉鍒板悎骞剁偣锛氶€€鍥?涓€涓秷鎭竴涓偣"鐨勮€佽矾锛堣涓轰笌浠ュ墠瀹屽叏涓€鑷达級------
        // 浣?*鍚屾牱琛?Wintab 鍘嬪姏**鈥斺€斾笉鐒惰繖涓€绗斾細"鍓嶅崐娈垫湁鍘嬪姏銆佸悗鍗婃绐佺劧鎺夊洖 0.5"锛?        // 鍦ㄧ瑪鐢讳腑闂寸暀涓嬩竴涓矖缁嗚烦鍙樸€傚彧鍦ㄩ潪绗旓紙鍏?ink锛夐偅鏉¤矾涓婅ˉ锛屽紑 ink 鐨勮矾涓€涓瓧涓嶅姩銆?        float fbWp = -1f;
        if (ptype != Native.PT_PEN && _wintab.IsOpen && PressureWidth.Enabled)
        {
            if (_wintab.Poll())
            {
                NoteWintabSample();
                if (_wintab.Pressure01 > 0f) AcceptWintabPressure(_wintab.RawPressure, _wintab.Pressure01);
            }

            WtPolledPackets += _wintab.PacketsRead - _wintabPacketsSeen;
            _wintabPacketsSeen = _wintab.PacketsRead;
            WtBadPackets += _wintab.BadPackets - _wintabBadSeen; _wintabBadSeen = _wintab.BadPackets;
            // 鍚屼笂锛氳繕娌℃嬁鍒扮涓€鍖呮椂鐢?**0** 鍏滃簳锛堣捣绗斿帇鍔涚墿鐞嗕笂鎺ヨ繎 0锛夛紝
            // 涓嶈兘鐢?0.5 鈥斺€?閭ｄ細璁╂瘡涓€绗斿紑澶撮紦鍑轰竴涓腑绮楃殑鐐广€?            fbWp = WtSeenPressure ? _wtStrokePressure : 0f;
            if (WtSeenPressure)
            {
                WtPressurePoints += 1;
                ActiveStrokeHasPressure = true;
                if (ActiveStroke.Tool == Tool.Pen) ActiveStroke.HasPressure = true;
            }
        }
        if (fbWp >= 0f && !WtSeenPressure) _wtPendingPoints++;
        AddPointCleaned(curCanvasX, curCanvasY, fbWp >= 0f ? fbWp : curPressure, NowMs);
    }

    /// <summary>
    /// 澶勭悊涓€鏉″師濮嬭緭鍏ワ紙`--rawprobe` / `--rawinput`锛夈€?    ///
    /// `--rawprobe`锛氬彧鏁版潯鏁帮紙鐢ㄦ潵閲?璁惧鍒板簳鎶ヤ簡澶氬皯"锛岃 `[绗旂敾]` 閭ｄ竴鏍忥級銆?    /// `--rawinput`锛氬湪"姝ｅ湪鍐欎竴绗?鏃讹紝鎶?raw 鐨勭浉瀵逛綅绉绘崲绠楁垚缁濆浣嶇疆**琛ユ垚閲囨牱鐐?*銆?    ///
    /// 闂ㄦ鍗″緱寰堜繚瀹堚€斺€斿彧瑕佹湁涓€鏉′笉婊¤冻灏?*鍙鏁般€佷笉鍔犵偣**锛岀粷涓嶅奖鍝嶆棦鏈夎涓猴細
    ///   路 蹇呴』鏄紶鏍囩被鍨嬬殑鎶ワ紙绗旂殑鍘熷鎶ユ槸 digitizer 绫汇€佸巶鍟嗙鏈夋牸寮忥紝涓嶈蛋杩欓噷锛夛紱
    ///   路 蹇呴』姝ｅ湪鍐欎竴绗旓紝涓旇繖涓€绗旀槸**鑷敱绗旇抗**锛堝浘褰?姗＄毊/妗嗛€?鎴浘/婵€鍏夐兘涓嶇锛夛紱
    ///   路 蹇呴』鏈?*鏈夋晥鐨勭粷瀵归敋鐐?*锛堟病閿氱偣灏辨病娉曟妸鐩稿浣嶇Щ鍙樼粷瀵光€斺€斿畞鍙笉鍔犱篃涓嶇寽锛夈€?    /// </summary>
    private void HandleRawInput(IntPtr lParam)
    {
        _rawReportsThisStroke++;
        _rawSinceLastPtr++;
        if (!RawInputCapture || ActiveStroke == null) { _rawSkipNoStroke++; return; }

        uint size = (uint)Marshal.SizeOf<Native.RAWINPUT>();
        uint header = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, out var ri, ref size, header) == uint.MaxValue)
        { _rawSkipApi++; return; }
        if (ri.header.dwType != Native.RIM_TYPEMOUSE) { _rawSkipType++; return; }

        int dx = ri.mouse.lLastX, dy = ri.mouse.lLastY;

        // 鍙湁"鑷敱绗旇抗 + 鎵嬪啓宸ュ叿"鎵嶈ˉ鐐癸細鍜?OnPointerMove 閭ｆ潯鍒嗘祦淇濇寔鍚屼竴寮犲彛寰?        if (ActiveStroke.Kind != StrokeKind.Freehand) { _rawSkipKind++; return; }
        var tool = ActiveStroke.Tool;
        if (tool != Tool.Pen && tool != Tool.Highlighter) { _rawSkipKind++; return; }
        if (SelDragging || _dwell.State == DwellState.Armed) { _rawSkipGate++; return; }
        // **鍙湁"纭疄鏈変腑闂寸偣鍙崬"鎵嶈ˉ**锛堣 _rawHasIntermediates 閭ｆ娉ㄩ噴锛夛細
        // 1:1 鐨勮澶囦笂琛ヨ繘鍘荤殑鍏ㄦ槸閲嶅鐐癸紙闆堕暱娈碉級锛岀函鍣０銆?        if (!_rawHasIntermediates) { _rawSkipNoInter++; return; }
        // 涓ょ鎶ラ兘**蹇呴』鍏堟湁缁濆閿氱偣**锛堟潵鑷寚閽堟秷鎭級鎵嶆暍鐢ㄢ€斺€旀病閿氱偣灏辨病娉曞垽鏂?        // "杩欎竴鎶ユ槸鐪熺殑鍦ㄩ檮杩戯紝杩樻槸鎹㈢畻閿欎簡"锛屽畞鍙笉鍔犱篃涓嶇寽銆?        if (!_rawAnchorValid) { _rawSkipAnchor++; return; }

        float sx, sy;
        bool isAbs = (ri.mouse.usFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0;
        if (isAbs)
        {
            // **缁濆鎶?*锛?026-10-07 琛ワ紝瀹炴祴鎵嬪啓鏉垮湪榧犳爣妯″紡涓婃姤鐨勫氨鏄繖绉嶏紝
            // 杩?SendInput 娉ㄥ叆鐨勪篃鏄€斺€斿師鏉ラ偅鍙?閬囧埌缁濆灏辫烦杩?鎶?*姣忎竴鏉?*閮借烦鎺変簡锛夈€?            //
            // 鈿?**缁濆鎶ヤ篃瑕佸彇"宸€?鍐嶇疮鍔狅紝涓嶈兘鎷跨粷瀵瑰€煎綋浣嶇疆鐩存帴鐢?*鈥斺€旂涓€鐗堝氨鏄洿鎺?            // 鐢紝缁撴灉琚祴鍑烘潵锛氱粷瀵瑰€肩殑褰掍竴鍖栨槧灏勬湁寰皬鍋忓樊 鈫?琛ヨ繘鍘荤殑鐐规湁鍋忕Щ 鈫?            // **缁嗙嚎鐨勬姉閿娇琚憡寮€銆佽鐩栫巼鎺夊埌闃堝€间互涓?*锛坄--widthtest` 鏈€缁嗛偅妗?            // 浠庡悎鏍兼帀鍒?0.69锛夈€傝€屼笖鏈満鍚堟垚杈撳叆鏄?1:1锛堝師濮嬫姤鍜屾寚閽堟秷鎭槸鍚屼竴鎵逛綅缃級锛?            // 鐩存帴褰撲綅缃敤绛変簬**寰€绗旇抗閲屽鍣０**銆?            //
            // 鍙栧樊鍊煎氨瀵逛簡锛?            //   路 1:1 鏃跺樊鍊?= 鎸囬拡娑堟伅閭ｆ潯浣嶇Щ 鈫?绱姞鍑烘潵姝ｅソ鏄寚閽堢偣 鈫?琚渶灏忚窛绂昏繃婊ゆ帀 鈫?闆跺櫔澹帮紱
            //   路 鏈夊悎骞舵椂锛堢湡鏈?2.3:1锛夊樊鍊?= 娌胯矾寰勭殑涓棿浣嶇Щ 鈫?绱姞鍑烘潵鏄湡姝ｇ殑鎻掑€肩偣銆?            // 鑰屼笖瀹?*姣忔鎸囬拡娑堟伅閮借閲嶆柊瀵瑰噯**锛堥敋鐐癸級锛屾槧灏勫亸涓€鐐逛篃涓嶆紓銆?            if (!_rawAbsValid) { _rawAbsX = dx; _rawAbsY = dy; _rawAbsValid = true; _rawSkipAbs++; return; }
            float pw = _primaryW / 65535f, ph = _primaryH / 65535f;
            float mx = (dx - _rawAbsX) * pw;
            float my = (dy - _rawAbsY) * ph;
            _rawAbsX = dx; _rawAbsY = dy;
            // 澶氬睆/铏氭嫙妗岄潰鏃跺綊涓€鍖栧熀鍑嗕笉鏄富灞忥紝宸€间細琚斁澶ф垨缂╁皬鈥斺€旂敤涓€涓鏉句絾鏈夋晥鐨?            // 涓婇檺鎸℃帀鐥呮€佸€硷紙姝ｅ父鐨勬姤鍗曟浣嶇Щ涓嶄細瓒呰繃鍗婁釜灞忥級銆?            if (MathF.Abs(mx) > _primaryW * 0.5f || MathF.Abs(my) > _primaryH * 0.5f) { _rawSkipAbs++; return; }
            _rawAccumX += mx;
            _rawAccumY += my;
            sx = _rawAnchorX + _rawAccumX;
            sy = _rawAnchorY + _rawAccumY;
        }
        else
        {
            if (dx == 0 && dy == 0) { _rawSkipZero++; return; }   // 涓嶅姩鏃朵篃浼氬彂绌烘姤锛屼涪鎺?            _rawAccumX += dx;
            _rawAccumY += dy;
            sx = _rawAnchorX + _rawAccumX;
            sy = _rawAnchorY + _rawAccumY;
        }

        float cx = sx, cy = sy;
        ScreenToCanvas(ref cx, ref cy);

        // 鈶?姣忕瑪涓婇檺锛堢浜岄亾淇濋櫓锛?        if (_rawPointsAdded >= RawMaxPerStroke) { _rawSkipCap++; return; }

        // 鈶?鏈€灏忚窛绂伙細鎶婇浂闀挎鎸″湪闂ㄥ銆傞紶鏍?raw 鍙互鍒?1000Hz锛屼笉绉诲紑鐨勮瘽
        //    鐩搁偦涓ょ偣宸笉鍒?1 鍍忕礌鈥斺€斿嚑浣曢偅杈规槑纭杩囬浂闀挎浼氬鍑洪€€鍖栨銆?        if (ActiveStroke.Points.Count > 0)
        {
            var last = ActiveStroke.Points[^1];
            float ddx = cx - last.X, ddy = cy - last.Y;
            if (ddx * ddx + ddy * ddy < RawMinDistPx * RawMinDistPx) { _rawSkipNear++; return; }
        }

        // 鍘嬪姏娌跨敤"杩欎竴绗斿埌鐩墠涓烘鐨勫€?锛堝厜鏍?*鍘熸湰灏辨病鏈夊帇鍔?*鏃舵墠鐢?0.5 杩欎釜涓偣锛夈€?        //
        // 鈿?**2026-10-07 淇?*锛氳繖閲屽師鏉ュ啓姝?`0.5f`锛屾敞閲婂嵈璇?娌跨敤鍒扮洰鍓嶄负姝㈢殑鍊?鈥斺€旀敞閲婃槸瀵圭殑锛?        // 浠ｇ爜鏄敊鐨勩€備互鍓嶆棤鎵€璋擄紙榧犳爣閭ｆ潯璺湰鏉ュ叏绗旈兘鏄?0.5锛屾€庝箞浼犻兘涓€鏍凤級锛?        // 浣嗘帴浜?Wintab 涔嬪悗鍚庢灉寰堟槑鏄撅細`[raw 琛ョ偣 +214]` 杩欎簺鐐瑰叏琚媺鍒?0.5锛?        // 鑰屾寚閽堟秷鎭潵鐨勭偣鎷跨殑鏄湡瀹炲帇鍔?鈫?**鍚屼竴绗旈噷涓€鍗婄湡瀹炪€佷竴鍗婁腑鐐癸紝绮楃粏涓€璺充竴璺?*銆?        //
        // **鍙湪 Wintab 鐪熺殑鍦ㄨˉ鍘嬫椂鎵嶆敼**锛坄WtSeenPressure`锛夛細娌″紑 Wintab銆?        // 鎴栬€呭紑 ink 璧扮湡绗旈偅鏉¤矾鏃讹紝杩欓噷渚濇棫鏄?0.5锛岃€佽涓洪€愬瓧涓嶅彉銆?        // 鍘嬪姏娌跨敤"杩欎竴绗斿埌鐩墠涓烘鐨勫€?鈥斺€?*杩欏氨鏄敞閲婁竴鐩磋鐨勪簨**锛?        // 浠ｇ爜鍘熸潵鍐欐 0.5銆佹垜涓婁竴鐗堝張鍐欐 0锛屼袱娆￠兘鏄?鎷夸竴涓父鏁板幓浠ｈ〃涓€浠舵湁鐘舵€佺殑浜?銆?        //
        // 鈿?**2026-10-07 淇紙鐢ㄦ埛鎶?寮€涓嶅紑 ink 绗旇閮藉緢鑴?锛?*锛?        // 涓婁竴鐗堝啓鎴?`_wintab.IsOpen ? 0f : 0.5f`锛屽悗鏋滄槸鈥斺€?        // **寮€ ink 鏃惰蛋鐪熺瑪閭ｆ潯璺紝`WtSeenPressure` 姘歌繙鏄?false锛堝彧鍦ㄩ潪绗旇矾寰勪笂缃綅锛夛紝
        // 鑰?`_wintab.IsOpen` 鏄?true 鈫?姣忎竴涓?raw 琛ヨ繘鏉ョ殑鐐瑰帇鍔涢兘琚啓鎴?0**
        // 鈫?鏈夊帇鎰熺殑绗旂敾閲?0 鏄犲皠鎴?**0.10 鍊嶅** 鈫?鏁存潯绾夸笂鍏ㄦ槸缁嗗彛瀛?= 鐪嬭捣鏉?鑴?銆?        // 鑰屼笖瀹冭窡"鍘嬪姏浠庡摢鍎挎潵"鏃犲叧锛屾墍浠?*寮€ ink / 鍏?ink 閮借剰** 鈥斺€?姝ｆ槸鐢ㄦ埛鐪嬪埌鐨勩€?        //
        // 姝ｇ‘鍋氭硶锛?*娌跨敤杩欎竴绗旀渶鍚庝竴涓偣鐨勫帇鍔?*銆備笁绉嶆儏鍐靛悓鏃舵垚绔嬶細
        //   路 寮€ ink锛氭渶鍚庝竴涓偣鐨勫帇鍔涘氨鏄湡绗旂殑鐪熷疄鍘嬪姏 鈫?raw 鐐规棤缂濇帴涓?鉁?        //   路 鍏?ink锛歐intab 鍘嬪姏鍒颁簡灏辩敤 Wintab锛屾病鍒板氨璺熺潃閭诲眳锛堣捣绗旈偅鍑犱釜鐐规槸 0锛屽洖澶磋鍥炲～锛夆湏
        //   路 鐪熼紶鏍囷細`HasPressure` 濮嬬粓涓哄亣锛岃繖涓€兼牴鏈笉鍙備笌娓叉煋 鈫?鑰佽涓轰笉鍙楀奖鍝?鉁?        float rawP;
        if (WtSeenPressure) rawP = _wtStrokePressure;
        else if (ActiveStroke.Points.Count > 0) rawP = ActiveStroke.Points[^1].P;
        else rawP = 0.5f;
        // 璧风瑪閭ｅ嚑涓紙杩樻病鎷垮埌鐪熷疄鍘嬪姏鏃剁殑锛夌偣璁颁笅鏉ワ紝绛夌涓€鍖呭帇鍔涘埌浜嗗洖濉€?        if (!WtSeenPressure && _wintab.IsOpen) _wtPendingPoints++;
        AddPointCleaned(cx, cy, rawP, NowMs);
        _rawPointsAdded++;
        // **琛ュ帇鐨勮处瑕佹妸 raw 琛ョ殑鐐逛篃绠楄繘鏉?*锛氫笉鐒?`琛ュ帇N鐐筦 鍙暟鎸囬拡娑堟伅閭ｆ潯璺紝
        // 鑰?raw 琛ョ殑鐐瑰父甯稿崰涓€鍗婁互涓婏紙鐢ㄦ埛鐪熸満瀹炴祴 139 鐐归噷 raw 琛ヤ簡 88 涓級鈥斺€?        // 閭ｈ鏃ュ織灏变細浣庢姤涓€鍗婏紝**涔熷氨璇佹槑涓嶄簡淇鍒板簳鐢熸晥娌℃湁**銆?        if (WtSeenPressure) WtPressurePoints++;
    }

    private void ResetRawAnchor(float screenX, float screenY)
    {
        _rawAnchorX = screenX; _rawAnchorY = screenY;
        _rawAccumX = _rawAccumY = 0;
        _rawAnchorValid = true;
    }

    /// <summary>
    /// 鎸夌瑪寮€鍏冲師濮嬭緭鍏ワ紙**绌洪棽鏃跺繀椤诲叧**锛岃 <see cref="OverlayWindow.SetRawInput"/> 閭ｆ娉ㄩ噴锛?    /// 涓嶅叧鐨勮瘽绌洪棽浼氭寔缁敹鍒伴紶鏍囪疆璇㈢┖鎶ワ紝涓诲惊鐜椤舵垚 46fps 绌鸿浆銆佸崟鏍?26%锛夈€?    /// </summary>
    private void SetRawCapture(bool on)
    {
        if (!RawProbeEnabled && !RawInputCapture) return;
        if (on == _rawRegistered) return;
        if (_windows.Count == 0) return;
        _rawRegistered = on && _windows[0].SetRawInput(true);
        if (!on) _windows[0].SetRawInput(false);
    }

    private bool _rawRegistered;

    // ===================== Wintab锛坄--nowintab` 鍏虫帀锛?=====================
    //
    // 绗竴闃舵锛?*鍙€?鍘嬪姏"锛屼笉鍊熷潗鏍?*銆傚潗鏍囦粛鏃ц蛋鏃㈡湁鐨勬寚閽?榧犳爣璺紙鍚?Raw Input 琛ョ偣锛夛紝
    // 鎵€浠ュ嚑浣曚竴涓瓧閮芥病鍔?鈥斺€?涓囦竴 Wintab 鏈夐棶棰橈紝鐥囩姸鍙細鏄?鍘嬪姏涓嶅"锛屼笉浼氭妸绗旂敾鐢绘銆?    // 璇﹁ `Input/WintabInput.cs`锛堥偅閲屼篃璁扮潃涓変釜浠庡悓琛岃祫鏂欓噷鏌ユ潵鐨勫潙锛夈€?
    /// <summary>Wintab 鏄惁鍚敤锛堥粯璁ゅ紑锛宍--nowintab` 鍏筹級銆?/summary>
    /// <summary>Wintab 鏄惁鍚敤銆?*瀛楁榛樿 false** 鈥斺€?涓?榛樿鍏?淇濇寔涓€鑷达細
    /// 涓囦竴涓婇潰閭ｆ鍐冲畾娌¤窇鍒帮紝瀹佸彲涓嶅紑锛堜繚瀹堜紭鍏堬級锛屼篃涓嶈鎮勬倓寮€鐫€銆?/summary>
    internal bool WintabEnabled;

    private readonly WintabInput _wintab = new();

    /// <summary>Wintab 绱鍙栧埌鐨勫寘鏁?/ 瓒婄晫鍖呮暟 / 鐪熺殑琛ヤ笂鍘嬪姏鐨勭偣鏁帮紙`[绗旂敾]` 閲屾姤鍑烘潵锛夈€?    /// 娉ㄦ剰**鍜屾帰閽堥偅涓?<see cref="WintabPackets"/> 涓嶆槸涓€鍥炰簨**锛氶偅涓暟鐨勬槸"鍖呮秷鎭?鏉℃暟
    /// 锛坄--wintabprobe` 鐢級锛岃繖閲屾暟鐨勬槸杞鍙栧洖鏉ョ殑鍖咃紙浜у搧璺緞鐢級銆?/summary>
    internal int WtPolledPackets, WtBadPackets, WtPressurePoints;
    private int _wintabPacketsSeen, _wintabBadSeen;

    /// <summary>涓婁竴绗旂粨鏉熸椂杩涚▼绾ц建杩瑰杺鐐?缁撹处鏁帮紙`[绗旂敾]` 閲屾姤"杩欎竴绗?鐨勫樊鍊硷級銆?/summary>
    private long _trailFedSeen, _trailRemovedSeen;

    /// <summary>杩欎竴绗旂殑"缂哄帇鍥炲～"鍩哄噯锛堝悓 `Input/PenInput.cs` 鐨勫彛寰勶級锛?    /// 瑙佽繃涓€娆＄湡瀹炲帇鍔涗箣鍚庯紝鍋跺皵缂轰竴涓嬪氨娌跨敤涓婁竴涓€硷紝鑰屼笉鏄帀鍥炰腑鐐硅绗旂敾涓棿鍑逛竴涓嬨€?/summary>
    private float _wtStrokePressure = -1f;

    /// <summary>杩欎竴绗旀湁娌℃湁瑙佽繃**鐪熷疄**鍘嬪姏锛? 0锛涙偓鍋滈偅涓?0 涓嶇畻锛夈€?/summary>
    internal bool WtSeenPressure;

    /// <summary>寮€澶磋繕鏈夊嚑涓偣鎷跨殑鏄?鍏滃簳鍊?锛堢瓑绗竴鍖呯湡瀹炲帇鍔涘埌浜嗚鎶婂畠浠敼鍥炴潵锛夈€?    /// 瑙?<see cref="AcceptWintabPressure"/> 閭ｆ娉ㄩ噴銆?/summary>
    private int _wtPendingPoints;

    /// <summary>
    /// 鎺ュ彈涓€涓?*鐪熷疄**鍘嬪姏鍊笺€?*绗竴娆℃帴鍙楁椂鍥炲～寮€澶撮偅鍑犱釜鐐广€?*
    ///
    /// 涓轰粈涔堝繀椤诲洖濉紙2026-10-07 鐢ㄦ埛鐪熸満涓よ疆鍙嶉锛夛細
    /// 璧风瑪閭?1~3 涓偣鏄湪 Wintab 绗竴鍖呭埌杈?*涔嬪墠**鍔犺繘绗旂敾鐨勶紝鍙兘鎷垮厹搴曞€硷細
    ///   路 鍏滃簳 0.5 鈫?寮€澶撮紦涓€涓?*绮楃偣**锛堢涓€鐗堬紝"绗竴涓偣鑰佷細鏈夌偣绮?锛?    ///   路 鍏滃簳 0   鈫?寮€澶翠竴娈?*缁嗙嚎**锛堢浜岀増锛?鏈夌殑澧ㄨ抗寮€澶寸粏缁嗙殑"锛?    /// **涓ょ閮芥槸鐚滅殑锛岄兘涓嶅 鈥斺€?鍥犱负閭ｄ竴鍒绘垜浠牴鏈繕涓嶇煡閬撳帇鍔涙槸澶氬皯銆?*
    /// 姝ｇ‘鍋氭硶锛氱瓑绗竴鍖呯湡瀹炲帇鍔涘埌浜嗭紝**鎶婇偅鍑犱釜鐐圭殑鍘嬪姏鏀规垚鐪熷疄鍊?* 鈥斺€?涓嶅啀鐚溿€?    /// 锛堜唬浠锋槸 O(鍑犱釜鐐? 鐨勮祴鍊硷紝鍙互蹇界暐銆傦級
    /// </summary>
    private void AcceptWintabPressure(int raw, float p01)
    {
        _wtStrokePressure = p01;
        WtSeenPressure = true;
        if (raw < WtRawPMin) WtRawPMin = raw;
        if (raw > WtRawPMax) WtRawPMax = raw;

        var s = ActiveStroke;
        if (_wtPendingPoints > 0 && s != null)
        {
            int cnt = Math.Min(_wtPendingPoints, s.Points.Count);
            for (int i = 0; i < cnt; i++)
            {
                var q = s.Points[i];
                q.P = p01;                 // X/Y 涓嶅姩锛?*鍙敼鍘嬪姏**
                s.Points[i] = q;
            }
            s.MarkPressureEdited();        // 鍛婅瘔娓叉煋"杩欎竴绗斿彉浜?锛堝彧鏀瑰帇鍔涳紝涓嶇鍖呭洿鐩掞級
        }
        _wtPendingPoints = 0;
        _cleanBuf.Clear();
    }

    /// <summary>杩欎竴绗斿帇鍔?*鍘熷鍊?*鐨勫尯闂达紙0..椹卞姩涓婇檺锛夈€?*鍙姤 0..1 鐪嬩笉鍑哄姏搴︾敤鍒颁簡閲忕▼鐨勫摢涓€娈?*鈥斺€?    /// 鐢ㄦ埛"杞荤灏辨弧瀹?鎴?鍘嬪埌搴曚篃涓嶅绮?杩欑被鎵嬫劅闂锛屽叏闈犺繖涓や釜鏁板畾浣嶃€?/summary>
    internal int WtRawPMin = int.MaxValue, WtRawPMax = int.MinValue;

    /// <summary>鎵撹繃涓€娆″氨璁颁綇"杩欏彴鏈哄櫒娌℃湁 Wintab"锛屼笉鍐嶅弽澶嶈瘯锛堝厤寰楁瘡绗旈兘鐧借窇涓€娆★級銆?/summary>
    private bool _wintabTried;

    /// <summary>璧风瑪鏃跺紑 Wintab锛?*鍙紑涓€娆?*锛涙墦涓嶅紑灏卞綋杩欏彴鏈哄櫒娌℃湁锛屽畨闈欓€€鍥烇級銆?/summary>
    private void EnsureWintab()
    {
        // 鍘嬫劅鍏崇潃锛堢晫闈€岃缃?鈫?涔﹀啓 鈫?鍘嬫劅绮楃粏銆嶉偅涓紑鍏筹級灏变笉蹇呭紑 鈥斺€?        // 鍘嬪姏鍊煎弽姝ｆ病浜虹敤銆?*"涓€閿叧鎺夊帇鎰?瑕佹妸杩欎竴璺殑宸ヤ綔涔熷仠鎺?*锛?        // 涓嶇劧浣庨厤鏈轰笂绛変簬鐧藉共銆傚叧鐨勬椂鍊?`_wintabTried` 淇濇寔 false锛?        // 鐢ㄦ埛鍐嶆墦寮€鍘嬫劅鏃朵笅涓€绗斿氨浼氳嚜鍔ㄥ紑璧锋潵銆?        if (!WintabEnabled || !PressureWidth.Enabled || _wintabTried || _windows.Count == 0) return;
        _wintabTried = true;
        try
        {
            if (_wintab.Open(_windows[0].Hwnd))
                Console.WriteLine($"  [wintab] {_wintab.Note}");
            else
                Console.WriteLine($"  [wintab] 鏈惎鐢細{_wintab.Note}锛堝叧 ink 鏃跺氨娌℃湁鍘嬪姏锛岃涓轰笌浠ュ墠涓€鑷达級");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [wintab] 寮傚父锛屽凡蹇界暐锛歿ex.Message}");
            _wintab.Close();
        }
    }

    // 闅忚€侀娴嬬郴缁熸暣鏉￠摼绉婚櫎銆傜畻娉曚笌鎺ョ嚎鍘熸枃瑙?`.revert/2026-10-05-娓叉煋鍑忔硶/`銆?
    /// <summary>
    /// 婀垮ⅷ璇ョ敤澶氱矖鐨勫崐寰勶細**鍜屽共澧ㄥ悓涓€涓槧灏?*锛堣 <see cref="PressureWidth"/>锛夈€?    ///
    /// 涓嶅仛杩欎竴姝ョ殑鍚庢灉寰堟墡鐪硷細鎶墜閭ｄ竴鐬棿锛屾箍澧紙鍥哄畾鍗婂緞锛変細璺虫垚骞插ⅷ锛堟湁绮楁湁缁嗭級鈥斺€?    /// 姝ｅ湪鍐欑殑"涓€"鍜岃惤绗斿悗鐨?涓€"绮楃粏涓嶆槸涓€鏉＄嚎銆?    /// </summary>
    private float TrailRadius()
    {
        var s = ActiveStroke;
        if (s == null) return 0f;
        if (!s.HasPressure || !PressureWidth.Enabled || s.Points.Count == 0) return s.Width * 0.5f;
        return PressureWidth.HalfWidth(s.Width, s.Points[^1].P);
    }

    /// <summary>
    /// 婀垮ⅷ锛氭妸杩欎竴鏉℃秷鎭噷鐨勭湡瀹炵偣锛堝睆骞曞潗鏍囷級杩炲悓**棰勬祴鐐?*涓€璧蜂氦缁欑郴缁熷悎鎴愬櫒銆?    ///
    /// 鈿?**瀹炴祴锛?026-09-29锛夛細DWM 涓嶆妸鎴戜滑鍠傜殑棰勬祴鐐圭敾鍑烘潵銆?*
    /// 鍒ゆ嵁鐢ㄦ渶澶ф。 `--predictms 200 --predictlead 400` 鍋氭帰閽堬細鍚屼竴鏃跺埢榧犳爣閭ｆ潯
    /// 锛堥娴嬪熬鐢辨垜浠嚜宸辩敾锛変細绐滃嚭鍘讳竴澶ф埅锛?*鎵嬪啓鏉块偅鏉℃鏃犲彉鍖?*銆?    /// 涔熷氨鏄鐪熺瑪鐨?璺熸墜"瀹屽叏鏉ヨ嚜濮旀墭杞ㄨ抗鏈韩锛岄娴嬭繖鍑犳鐩墠鏄?*鐧藉杺**銆?    /// 鍏堢暀鐫€锛堝嚑娆?COM 璋冪敤锛屼唬浠峰彲浠ュ拷鐣ワ紱涓囦竴浠ュ悗绯荤粺鐗堟湰寮€濮嬭浜嗗氨鐩存帴鐢熸晥锛夛紝
    /// 浣?*鍒妸"绗旂殑棰勬祴"绠楄繘鏁堟灉璐﹂噷**鈥斺€旂瑪閭ｆ潯璺殑鏁堟灉鍏ㄥ湪寮х嚎鍜屽搴︿笂銆?    /// 鎶ュ憡閲?`鍠侱WM N 娈碉紙骞冲潎 X / 鏈€澶?Y px锛塦 杩欎竴椤瑰彧鍦ㄦ帓鏌?鍒板簳鍠傛病鍠?鏃剁湅銆?    /// </summary>
    /// 棰勬祴鍙綔鐢ㄤ簬婀垮ⅷ鈥斺€斿畠鐢荤殑鏄?姝ｅ湪鍐欑殑杩欎竴绗?鐨勬渶鍚庝竴灏忔锛岀湡瀹炵偣涓€鍒板氨琚鐩栵紝
    /// 涓嶈繘瀛樻。銆佷篃涓嶄細鍙樻垚涓€鏉＄湡鐨勭瑪鐢汇€傝繖鏄?涓嶇敥澧?鐨勭涓€閬撲繚闄┿€?    /// </summary>
    private void FeedInkTrail(uint ptype, float radius, float screenX, float screenY)
    {
        if (!OverlayWindow.InkTrailEnabled) return;
        var win = WindowAt(screenX, screenY);
        if (win == null) return;

        // 閲囨牱缂撳啿**鎸夎緭鍏ョ被鍨嬮€?*锛氱瑪鍦?`_pen`銆侀紶鏍?瑙︽懜鍦?`_ptr`锛堜袱杈归兘鏄睆骞曞儚绱狅級銆?        // 鈿?浠ュ墠杩欓噷鍐欐璇?`_pen`鈥斺€旀斁寮€榧犳爣/瑙︽懜涔嬪悗锛岄偅鏍蜂細鎶?*涓婁竴鏉＄瑪娑堟伅鐨勬棫鐐?*
        // 鍐嶅杺涓€閬嶏紝杞ㄨ抗浼氱敾鍦ㄩ敊璇殑浣嶇疆/鏃堕棿娈典笂銆?        int realCount = 0;
        if (ptype == Native.PT_PEN)
        {
            for (int i = 0; i < _pen.Count && realCount < _trailReal.Length; i++)
            {
                _trailReal[realCount] = new Vector2(_pen[i].X, _pen[i].Y);
                // 閫愮偣鍗婂緞 = 杩欎竴鐐圭殑鍘嬪姏璧?*鍜屽共澧ㄥ悓涓€涓槧灏?*锛堣 PressureWidth锛夈€?                _trailRadii[realCount] = PressureRadiusOf(_pen[i].Pressure);
                realCount++;
            }
        }
        else
        {
            for (int i = 0; i < _ptr.Count && realCount < _trailReal.Length; i++)
            {
                _trailReal[realCount] = new Vector2(_ptr[i].X, _ptr[i].Y);
                // 闈炵瑪娌℃湁鐪熷疄鍘嬪姏锛坄PointerSample` 鎭?0.5 鍗犱綅锛夆啋 鍗婂緞灏辨槸褰撳墠绗斿鐨勪竴鍗婏紝
                // 鍜屽共澧ㄤ竴鑷达紙`radius` 鐢辫皟鐢ㄦ柟缁欙紝瑙?TrailRadius锛夈€?                _trailRadii[realCount] = radius;
                realCount++;
            }
        }
        if (realCount == 0)
        {
            _trailReal[realCount] = new Vector2(screenX, screenY);
            _trailRadii[realCount] = radius;
            realCount++;
        }

        // [璇曢獙E锛?026-10-11 璇曡繃銆佸凡鎾 鏇惧湪杩欓噷鎶?鍐嶈蛋涓€姝?鐨勯娴嬬偣鍠傝繘 predicted 鏁扮粍
        //锛坄--trailpredict`锛夛細榧犳爣/瑙︽懜蹇€熶功鍐欐椂鐢╁熬鏄庢樉锛堢敤鎴风湡鏈烘媿鏉匡級锛屾暣鏉℃挙鎺夈€?        win.AddInkTrailPoints(_trailReal, realCount, null, 0, radius, _trailRadii);
    }

    /// <summary>涓€涓帇鍔涘€?鈫?婀垮ⅷ鍗婂緞锛堝拰骞插ⅷ鍚屼竴鏄犲皠銆佸悓涓€鍗曚綅锛夈€?/summary>
    private float PressureRadiusOf(float p)
    {
        var s = ActiveStroke;
        if (s == null) return 0f;
        if (!s.HasPressure || !PressureWidth.Enabled) return s.Width * 0.5f;
        return PressureWidth.HalfWidth(s.Width, p);
    }

    private void ApplyMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;

        // 閫夋嫨鏂瑰紡鏄绱㈡椂璧板彟涓€鏉″垽鎹紙80% 鍦ㄥ唴 + 璐磋竟鏃犻檺寤朵几锛夛紝瑙?ApplyLassoSelection銆?        if (SelMode == SelectMode.Lasso) { ApplyLassoSelection(); return; }

        float l = MqMinX, t = MqMinY, r = MqMaxX, b = MqMaxY;
        if (r - l < 4 || b - t < 4)
        {
            // 杩欐槸涓€娆?*鐐瑰嚮**锛屼笉鏄嫋妗嗐€傚崟鍑荤┖鐧藉灏辫鍙栨秷閫変腑銆?            // 涔嬪墠杩欓噷鐩存帴 return銆佷粈涔堥兘涓嶅仛锛岀敤鎴峰緱鐐逛袱涓嬫墠鍙栨秷锛屽緢鍒壄銆?            Doc.Selected.Clear();
            _dirty = true;
            return;
        }

        Doc.ApplyMarquee(new RectF { MinX = l, MinY = t, MaxX = r, MaxY = b });
        // **涓嶅湪杩欓噷鑷姩鎷嗗紑**銆傚師鏉ヨ繖閲岃皟浜?SplitErasedSelection()锛岃鍚︽帀浜嗭細
        // 鎷嗘垚涓や釜瀵硅薄浼氳鍗婇€忔槑鑽у厜绗斿湪"涓ゆ埅浜掔浉绌胯繃"鐨勫湴鏂?*娣峰悎涓ゆ銆侀鑹插彉娣?*鈥斺€?        // 閭ｇ瓑浜?鎴戜粈涔堥兘娌℃摝锛屽彧鏄閫変簡涓€涓嬶紝澧ㄥ嵈鍙樹簡"銆傚師鍒欐槸**鎿﹂櫎鍙槸鍍忕礌娌′簡**锛?        // 闄や簡琚摝鎺夌殑閭ｄ竴鍧楋紝鍏朵粬鍍忕礌鍦ㄤ换浣曟搷浣滃墠鍚庨兘璇ヤ竴妯′竴鏍枫€?        // 鎯冲崟鐙憜寮勬煇涓€鎴椂鐢ㄦ樉寮忓姩浣滐紙Ctrl+Alt+8 鎷嗗紑鎿︽柇鐨勭瑪杩癸級锛岃 RunAction銆?        Console.WriteLine($"marquee selected {Doc.Selected.Count} strokes");
        _dirty = true;
    }

    /// <summary>
    /// 鍒囨崲妗嗛€夊伐鍏蜂笅"鎷栫┖鐧藉"鐨勬柟寮忥細鐭╁舰妗?鈫?鑷敱濂楃储銆?    ///
    /// **鍙垏鏂瑰紡锛屼笉鎹㈠伐鍏?*鈥斺€旂敤鎴锋寜杩欎釜閿殑鏃跺€欐墜涓婂彲鑳借繕鎷跨潃绗旓紙姝ｅ湪鍐欙級锛?    /// 鎹㈠伐鍏蜂細椤烘墜鎶婇€変腑娓呮帀锛堣 SwitchTool锛夛紝閭ｆ槸浠栨病瑕佺殑鍓綔鐢ㄣ€?    /// 鏂瑰紡涓嶈惤鐩橈細鍜屽伐鍏峰昂瀵镐竴鏍锋椿鍦ㄥ唴瀛橀噷锛岄噸寮€灏辨槸榛樿鐨勭煩褰€?    /// </summary>
    private void ToggleSelectMode()
    {
        SetSelectMode(SelMode == SelectMode.Lasso ? SelectMode.Rect : SelectMode.Lasso);
    }

    /// <summary>
    /// 鍒囧埌鎸囧畾鐨勯€夋嫨鏂瑰紡銆傞敭鐩橀偅涓€璺紙Ctrl+Alt+9锛夊拰鐣岄潰閭ｄ竴璺?    /// 锛?see cref="SetSelectModeFromUi"/>锛?*閮借蛋杩欓噷**锛屽厤寰椾袱鏉¤矾鍚勫啓涓€濂楄鍒欍€?    /// </summary>
    private void SetSelectMode(SelectMode mode)
    {
        SelMode = mode;
        // **璁颁綇閫夋嫨鏂瑰紡**锛堢煩褰?/ 濂楃储锛岀敤鎴?2026-09-30 瀹氾級锛氶敭鐩樺拰鐣岄潰涓ゆ潯璺兘璧拌繖閲岋紝
        // 鎵€浠ュ彧鍦ㄨ繖涓€澶勫啓灏卞浜嗭紱鍚姩鏃惰鍥炴潵锛堣 Run 閲岄偅鍙?selMode锛夈€?        SetUiPref("selMode", mode == SelectMode.Lasso ? "lasso" : "rect");
        // 鍗婅矾鍒囧氨鎶婃病鐢诲畬鐨勫湀涓㈡帀锛屽厤寰椾笅涓€娆℃寜涓嬫帴鍦ㄦ棫璺緞鍚庨潰銆?        LassoPath.Clear();
        MarqueeActive = false;
        Console.WriteLine(mode == SelectMode.Lasso
            ? "閫夋嫨鏂瑰紡锛氳嚜鐢卞绱紙鍦堜綇 80% 灏辩畻閫変腑锛涘湀鍒板睆骞曡竟锛濆綋浣滄棤闄愬欢浼革級"
            : "閫夋嫨鏂瑰紡锛氱煩褰㈡锛堢鍒板ⅷ灏辩畻閫変腑锛?);
        if (Tool != Tool.Marquee)
            Console.WriteLine("  锛堢幇鍦ㄤ笉鏄閫夊伐鍏凤細鎸?Ctrl+Alt+5 鎹㈠埌妗嗛€夋墠鐢ㄥ緱涓婏級");
    }

    /// <summary>鑷鐢細鍒囦竴娆￠€夋嫨鏂瑰紡锛堢瓑鍚屼簬鎸変竴涓?Ctrl+Alt+9锛夈€?/summary>
    internal void ToggleSelectModeForTest() => ToggleSelectMode();

    /// <summary>
    /// 妗嗛€夊伐鍏锋寜涓嬫椂璧蜂竴涓柊鎵嬪娍銆?*鎶藉嚭鏉ユ槸涓轰簡鑷鑳借蛋鍒板悓涓€娈典唬鐮?*
    /// 锛堣 <see cref="MarqueeDragForTest"/>锛夛細鍚堟垚榧犳爣鍦ㄩ攣灞?/ 琚埆鐨勭▼搴忓崰鐫€鎹曡幏鏃?    /// 杩涗笉鏉ワ紝閭ｅ嚑鏉¤嚜妫€鍙兘璁拌烦杩団€斺€旇€岃繖娈垫帴绾挎伆鎭版槸鏈€瀹规槗鍐欓敊鐨勫湴鏂广€?    /// 淇グ閿敱璋冪敤鏂硅閿洏鍚庝紶杩涙潵锛屽垽瀹氬嚱鏁颁繚鎸佺函閫昏緫銆?    /// </summary>
    private void BeginMarqueeAt(float x, float y, bool shift, bool alt)
    {
        MarqueeActive = true;
        _mqAnchorX = x; _mqAnchorY = y;
        MqMinX = MqMaxX = x; MqMinY = MqMaxY = y;
        // 濂楃储锛氳矾寰勪粠鎸変笅杩欎竴鐐瑰紑濮嬶紱淇グ閿篃鍦ㄨ繖涓€鍒昏涓嬫潵锛堟澗鎵嬫椂鐢ㄥ畠鍐冲畾鍔犻€?鍑忛€夛級銆?        LassoPath.Clear();
        LassoLive = new Vector2(x, y);
        if (SelMode == SelectMode.Lasso) LassoPath.Add(LassoLive);
        _lassoAdditive = shift;
        _lassoSubtractive = alt;
    }

    /// <summary>
    /// 鎷栨涓竴璺Щ鍔ㄣ€?    ///
    /// 鐭╁舰锛?*閿氱偣 鈫?褰撳墠鐐?*锛堝洓涓柟鍚戦兘鑳芥媺锛屽線鍥炶蛋妗嗚窡鐫€缂╁洖鍘伙級銆?    /// 濂楃储锛氭娊绋€鍚庢敹杩涜矾寰勶紝鍚屾椂璁颁笅鎸囬拡褰撳墠浣嶇疆缁欓瑙堢敤锛堣 <see cref="LassoLive"/>锛夈€?    /// </summary>
    private void ExtendMarqueeTo(float x, float y)
    {
        if (SelMode != SelectMode.Lasso)
        {
            // 鐭╁舰妗?= 浠庨敋鐐规媺鍒板綋鍓嶇偣鐨勯偅涓€涓煩褰€?*涓嶆槸**"鎵繃鐨勬渶澶ц寖鍥?銆?            MqMinX = MathF.Min(_mqAnchorX, x); MqMaxX = MathF.Max(_mqAnchorX, x);
            MqMinY = MathF.Min(_mqAnchorY, y); MqMaxY = MathF.Max(_mqAnchorY, y);
            return;
        }

        // 濂楃储锛氶棿璺?< 3 閫昏緫鍍忕礌鐨勭偣瀵瑰垽鎹病鏈夎础鐚紝鍙細鎶婂杈瑰舰浠庡嚑鍗佷釜椤剁偣鎾戝埌鍑犲崈涓紝
        // 鑰屾瘡鏉＄瑪杩圭殑"鐐瑰湪涓嶅湪鍦堥噷"閮借涔樿繖涓《鐐规暟銆?        LassoLive = new Vector2(x, y);
        if (LassoPath.Count == 0) { LassoPath.Add(LassoLive); }
        else
        {
            var last = LassoPath[LassoPath.Count - 1];
            if (Vector2.Distance(last, LassoLive) >= LassoStepLogical * DpiScale)
                LassoPath.Add(LassoLive);
        }

        // 鑴忓尯**鍙涓嶅噺**锛堝拰鐭╁舰閭ｆ潯璺埢鎰忎笉鍚岋級锛氬凡缁忕敾鍒板睆骞曚笂鐨勯偅涓€娈电嚎锛?        // 涓嶈兘鍥犱负鎸囬拡寰€鍥炵Щ灏变笉閲嶇敾鈥斺€斾笉閲嶇敾灏辨摝涓嶆帀锛屽睆骞曚笂浼氱暀涓嬩竴鎴棫绾裤€?        MqMinX = MathF.Min(MqMinX, x); MqMaxX = MathF.Max(MqMaxX, x);
        MqMinY = MathF.Min(MqMinY, y); MqMaxY = MathF.Max(MqMaxY, y);
    }

    /// <summary>
    /// **杩涘叆鎴浘鍙栨櫙**锛?.3.1锛岀収寰俊鐨勮妭濂忥級锛氫竴杩涙潵灏?    ///   路 鎶?鍐荤粨甯?锛堟暣灞忥紱闅愯棌绐楀彛鎴浘閭ｅ紶鏄?*钘忚捣鎴戜滑鏁翠釜瑕嗙洊灞?*鎶撶殑锛夛紱
    ///   路 鏁村睆閾洪伄缃╋紙鍏ㄧ伆锛岃繕娌℃湁妗嗭級+ 鍗佸瓧鍏夋爣 + 鍏ㄥ睆鍑嗙嚎 + 椤堕儴鎻愮ず + 鍙充笂銆屸湑 鍙栨秷銆嶏紱
    ///   路 宸ュ叿鍒囧埌鎴浘锛堥潰鏉块偅涓€鏍间寒鐫€锛涚晫闈㈠湪鍙栨櫙鏈熼棿鐢卞紩鎿庢敹璧凤級銆?    ///
    /// 鍏ュ彛涓夊锛氱偣妯″紡娈碉紙`EnterCapture`锛夈€侀敭鐩?`Ctrl+S`銆佷互鍙婂湪"鎴浘宸ュ叿宸查€変腑"鏃?    /// 鐩存帴鍦ㄧ敾甯冧笂鎸変笅锛?see cref="BeginCaptureAt"/> 浼氬厹搴曡皟瀹冿級銆?    /// `freeze=false` 鍙粰鑷鐢紙涓嶇湡鐨勬姄灞忥級銆?    /// </summary>
    internal void BeginCaptureMode(bool freeze = true)
    {
        if (CaptureActive) return;
        if (Tool != Tool.Capture) SwitchTool(Tool.Capture);   // 璁板綍 _toolBeforeCapture 鍦ㄩ噷闈?        CaptureActive = true;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capturePressHandled = false;
        _capLastClickMs = double.NegativeInfinity;

        // 鎸囬拡鍙?*绯荤粺鍏夋爣褰撳墠浣嶇疆**锛氫粠闈㈡澘涓婄偣杩涙潵鏃讹紝瑕嗙洊灞傛敹涓嶅埌閭ｅ嚑鏉＄Щ鍔ㄦ秷鎭紝
        // PointerX/Y 杩樺仠鍦ㄦ棫浣嶇疆鈥斺€斿噯绾垮拰"浠庡摢鍎胯捣妗?灏变細鏅氫竴鎷嶆墠璺熶笂銆?        if (Native.GetCursorPos(out var p))
        {
            PointerX = p.X;
            PointerY = p.Y - ViewOffsetY;      // 灞忓箷 鈫?鐢诲竷锛堢浉鏈哄彧鏈夌旱鍚戝亸绉伙級
        }
        // 杩樻病鏈夋锛氬洓涓偣閮芥斁鍦ㄦ寚閽堜笂锛?妗嗗お灏?鐨勫垽鎹嚜鐒舵垚绔?鈫?閬僵鏁村潡鐏般€佷笉鎸栨礊锛?        CapMinX = CapMaxX = PointerX;
        CapMinY = CapMaxY = PointerY;

        if (freeze) BeginCaptureFreeze();
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 鎴浘鍙栨櫙妗嗘寜涓嬶紙鍜屾閫夊悓涓€濂楅敋鐐圭畻娉曪級銆?    ///
    /// 8.3.1 璧凤細濡傛灉杩樻病杩涘彇鏅紙鍙偣浜嗐€屾埅灞忋€嶆牸銆佽繕娌＄偣妯″紡娈碉級锛?*鎸変笅鐨勮繖涓€涓嬪氨鏄叆鍙?*鈥斺€?    /// 鍏堣皟 <see cref="BeginCaptureMode"/>锛堟姄鍐荤粨甯с€侀摵閬僵锛夛紝鍐嶈惤閿氱偣銆?    /// </summary>
    private void BeginCaptureAt(float x, float y, bool freeze = true)
    {
        if (!CaptureActive) BeginCaptureMode(freeze);
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capAnchorX = x; _capAnchorY = y;
        CapMinX = CapMaxX = x; CapMinY = CapMaxY = y;
    }

    /// <summary>
    /// 鎹㈠彟涓€绉嶆埅娉曪紙鍙栨櫙涓寜 `Ctrl+S`锛夛細閲嶆柊鍐荤粨涓€娆°€?    /// 銆屾埅鍥俱€嶁啇鈫掋€岄殣钘忕獥鍙ｆ埅鍥俱€嶄袱绉嶆ā寮忕殑**鍙宸埆**鍏ㄥ湪杩欎竴鎶撻噷锛堣棌涓嶈棌鎴戜滑锛夈€?    /// </summary>
    private void ToggleCaptureMode()
    {
        CaptureHideInk = !CaptureHideInk;
        if (CaptureActive)
        {
            ReleaseCaptureFrozen();
            BeginCaptureFreeze();
            CaptureAdjusting = false;
            _capDrag = CaptureDrag.None;
            CapMinX = CapMaxX = PointerX;
            CapMinY = CapMaxY = PointerY;
            _dirty = true;
        }
        NotifyUiStateChanged();
        Console.WriteLine($"鎴浘妯″紡锛歿(CaptureHideInk ? "闅愯棌绐楀彛鎴浘锛堝彧鎷嶄笅灞傦級" : "鎴浘锛堣繛鎵规敞涓€璧凤級")}");
    }

    /// <summary>
    /// 鎸変笅鏃舵姄"鍐荤粨甯?锛?*鏁村潡铏氭嫙妗岄潰**锛屾姄鍒板唴瀛橀噷銆?    ///
    /// 涓ょ妯″紡鎶撶殑涓滆タ涓嶄竴鏍凤紙鍜屾澗鎵嬫椂閭ｄ唤鍙ｅ緞瀹屽叏涓€鑷达級锛?    ///   路 **闅愯棌鐣岄潰**锛氭妸鏁翠釜瑕嗙洊灞傝棌璧锋潵鍐嶆姄锛堟媿鍒扮殑鍙湁涓嬪眰鍐呭锛夆€斺€?    ///     閭?~60ms 鐨?闂?浠?鏉炬墜鏃?鎸埌浜?鍒氭寜涓嬫椂"锛堣€佸笀鍒氬彂璧锋搷浣滐紝棰勬湡灏辨槸鍒囨ā寮忥級锛?    ///   路 **鐩存帴鎴彇**锛氬彧钘忔垜浠嚜宸辩殑**閬僵/鍙栨櫙妗?鍏夋爣**锛屾澘涔︿竴璧锋媿杩涘幓銆?    ///     鐢?`CaptureFrameHidden` 閲嶇敾涓€甯у氨璧帮紙涓嶇敤钘忕獥鍙ｃ€佷笉鐢ㄧ瓑鍚堟垚鍣ㄦ挙绐楀彛锛夈€?    ///
    /// 鎶撳け璐ワ紙杩斿洖 null锛夊氨淇濇寔 `CaptureFrozen = false`锛屾澗鎵嬫椂閫€鍥炶€佽矾寰勭幇鎶撯€斺€?    /// 鎴浘涓嶈兘鍥犱负"鍐荤粨娌℃姄鍒?鏁翠釜澶辨晥銆?    /// </summary>
    private void BeginCaptureFreeze()
    {
        ReleaseCaptureFrozen();
        var vs = VirtualScreen;
        int x = (int)MathF.Round(vs.MinX), y = (int)MathF.Round(vs.MinY);
        int w = (int)MathF.Round(vs.MaxX - vs.MinX), h = (int)MathF.Round(vs.MaxY - vs.MinY);
        if (w <= 0 || h <= 0) return;

        byte[] px;
        if (CaptureHideInk)
        {
            // 钘忔暣涓鐩栧眰锛堝惈鎺ヨ緭鍏ュ皬绐楋級鈫?鎶?鈫?鎭㈠銆傛仮澶嶄箣鍚庢妸鍐呭灞傛暣灞備綔搴燂細
            // 绐楀彛钘忚繃鍙堟樉绀猴紝鍚庣紦鍐查噷韬虹殑鏄棫鐢婚潰锛堣繖鏉¤€佹敞閲婇噷璁拌繃锛夈€?            using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                px = ScreenCapture.Grab(x, y, w, h);
            Doc.InvalidateAll();
        }
        else
        {
            // 鍙棌鎴戜滑鑷繁鐨勪笢瑗匡細閲嶇敾涓€甯?鈫?绛夊悎鎴愬櫒璐翠笂鍘?鈫?鎶?鈫?鎭㈠銆?            CaptureFrameHidden = true;
            _dirty = true;
            RenderAll();
            Native.DwmFlush();
            px = ScreenCapture.Grab(x, y, w, h);
            CaptureFrameHidden = false;
            _dirty = true;
        }

        // **鎶婂墠鍙?閿洏瑕佸洖鏉?*锛堢敤鎴?2026-09-30 鎶ョ殑"鎴畬鍥剧瑪鐨勫揩鎹烽敭娌℃湁鐢ㄤ簡"锛夛細
        // 涓婇潰閭ｄ竴涓嬭棌绐椾細鎶婂墠鍙拌缁欏埆浜猴紝鎭㈠鏄剧ず鐢ㄧ殑鏄?SW_SHOWNOACTIVATE銆佷笉浼氳嚜宸卞洖鏉ャ€?        // 涓嶈ˉ杩欎竴鍙ワ紝涓嶅彧"鎴畬鍥?鐨勫揩鎹烽敭鏄鐨勨€斺€?*鍙栨櫙涓殑 Esc/Enter 涔熸敹涓嶅埌**
        //锛堝畠浠悓鏍烽潬鎴戜滑鐨勭獥鍙ｆ敹閿級銆傝瑙?RegainKeyboardFocus 閭ｆ銆?        RegainKeyboardFocus();

        if (px == null)
        {
            Console.WriteLine("鎴浘锛氬喕缁撳抚娌℃姄鍒帮紝閫€鍥炴澗鎵嬫椂鎶撳睆");
            return;
        }
        CaptureFrozenBgra = px;
        CaptureFrozenX = x; CaptureFrozenY = y;
        CaptureFrozenW = w; CaptureFrozenH = h;
        CaptureFrozen = true;
    }

    /// <summary>鏀炬帀鍐荤粨甯х殑鍐呭瓨锛堢‘璁?鍙栨秷/閲嶆柊鎴浘鏃堕兘璧板畠锛夈€?/summary>
    private void ReleaseCaptureFrozen()
    {
        CaptureFrozenBgra = null;
        CaptureFrozen = false;
        CaptureFrozenW = CaptureFrozenH = 0;
    }

    /// <summary>褰撳墠鍙栨櫙妗嗗湪**灞忓箷**锛堢墿鐞嗗儚绱狅級閲岀殑鐭╁舰銆?/summary>
    internal RectF CaptureFrameScreen()
    {
        float x0 = MathF.Min(CapMinX, CapMaxX), y0 = MathF.Min(CapMinY, CapMaxY);
        float x1 = MathF.Max(CapMinX, CapMaxX), y1 = MathF.Max(CapMinY, CapMaxY);
        return new RectF
        {
            MinX = x0, MinY = y0 + ViewOffsetY,          // 鐩告満鍙湁绾靛悜鍋忕Щ
            MaxX = x1, MaxY = y1 + ViewOffsetY,
        };
    }

    /// <summary>浠庡喕缁撳抚閲岃鍑烘寚瀹氬睆骞曠煩褰紙鐗╃悊鍍忕礌锛夈€傝秺鐣岃嚜鍔ㄥす鍙栵紙妗嗘案杩滃湪灞忓唴锛屽厹搴曡€屽凡锛夈€?/summary>
    private byte[] CropCaptureFrozen(int x, int y, int w, int h)
    {
        if (!CaptureFrozen || CaptureFrozenBgra == null || w <= 0 || h <= 0) return null;
        var dst = new byte[(long)w * h * 4 <= int.MaxValue ? w * h * 4 : 0];
        if (dst.Length == 0) return null;
        for (int row = 0; row < h; row++)
        {
            int sy = y + row - CaptureFrozenY;
            if (sy < 0 || sy >= CaptureFrozenH) continue;
            int sx = x - CaptureFrozenX;
            int n = w, so = 0;
            if (sx < 0) { n += sx; so = -sx; sx = 0; }
            if (sx + n > CaptureFrozenW) n = CaptureFrozenW - sx;
            if (n <= 0) continue;
            int soB = so * 4 + row * w * 4;
            int siB = (sy * CaptureFrozenW + sx) * 4;
            Array.Copy(CaptureFrozenBgra, siB, dst, soB, n * 4);
        }
        return dst;
    }

    /// <summary>
    /// 鎴浘鍙栨櫙妗嗘嫋鍔細閿氱偣 鈫?褰撳墠鐐癸紙寰€鍥炴嫋瑕佽窡鐫€缂╋紝鍚﹀垯灏辨槸"涓嶈窡鎵?锛夈€?    /// 鎷栧嚭灞忓箷涔熸病鏈夋剰涔夛紝澶瑰湪灞忓箷閲岋紙鍜屾閫夐偅绉?璐磋竟=鏃犻檺寤朵几"涓嶅悓锛氭埅鍥惧氨鏄鐪嬭鐨勯偅涓€鍧楋級銆?    /// </summary>
    private void ExtendCaptureTo(float x, float y)
    {
        var vs = VirtualScreen;
        float sx = Math.Clamp(x, vs.MinX, vs.MaxX);
        float sy = Math.Clamp(y, vs.MinY - ViewOffsetY, vs.MaxY - ViewOffsetY);
        CapMinX = MathF.Min(_capAnchorX, sx); CapMaxX = MathF.Max(_capAnchorX, sx);
        CapMinY = MathF.Min(_capAnchorY, sy); CapMaxY = MathF.Max(_capAnchorY, sy);
    }

    /// <summary>
    /// 鑷鐢細鍙┍鍔ㄦ埅鍥惧彇鏅鐨?鎸変笅 鈫?鎷?锛?*涓嶇湡鐨勬姄灞?*锛堟姄灞忚钘忕獥鍙ｏ紝
    /// 杩樹細鎶婅繖涓€甯х殑娴嬭瘯鐜寮勪贡锛夛紝涔?*涓嶆崲宸ュ叿**锛堟崲宸ュ叿浼氭薄鏌撳悗闈㈢殑鐢ㄤ緥锛夈€?    /// 鍙栨櫙妗嗗拰妗嗛€夋鐢ㄧ殑鏄悓涓€濂楅敋鐐圭畻娉曘€?    /// </summary>
    internal void CaptureFrameDragForTest(float ax, float ay, float x1, float y1, float x2 = float.NaN, float y2 = float.NaN)
    {
        CaptureActive = true;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capAnchorX = ax; _capAnchorY = ay;
        CapMinX = CapMaxX = ax; CapMinY = CapMaxY = ay;
        ExtendCaptureTo(x1, y1);
        if (!float.IsNaN(x2)) ExtendCaptureTo(x2, y2);
    }

    /// <summary>
    /// 鑷鐢細鎶婁竴鏉¤矾寰勬寜"鎸変笅 鈫?涓€璺Щ鍔?鈫?鏉炬墜"**鐪熷疄璺戜竴閬?*鈥斺€旇蛋鐨勬槸鍜岄紶鏍?    /// 瀹屽叏鐩稿悓鐨勪笁涓叆鍙ｏ紙<see cref="BeginMarqueeAt"/> / <see cref="ExtendMarqueeTo"/> /
    /// <see cref="ApplyMarquee"/>锛夛紝鍙槸涓嶇粡杩囨搷浣滅郴缁熺殑娑堟伅闃熷垪銆?    /// </summary>
    internal void MarqueeDragForTest(IReadOnlyList<Vector2> path,
                                     bool shift = false, bool alt = false)
    {
        if (path == null || path.Count == 0) return;
        BeginMarqueeAt(path[0].X, path[0].Y, shift, alt);
        for (int i = 1; i < path.Count; i++) ExtendMarqueeTo(path[i].X, path[i].Y);
        ApplyMarquee();               // 鏉炬墜锛氭寜 SelMode 璧扮煩褰㈡垨濂楃储
    }

    /// <summary>
    /// 濂楃储鏉炬墜銆傚垽鎹湪 <see cref="InkDocument.ApplyLasso"/>锛?0% 鍦ㄥ唴 + 璐磋竟鏃犻檺寤朵几锛夛紝
    /// 杩欓噷鍙涓変欢浜嬶細璺緞澶煭灏卞綋**鍗曞嚮绌虹櫧**锛堝彇娑堥€変腑锛屽拰妗嗛€変竴鑷达級銆?    /// 淇グ閿姞鍑忛€夈€佹竻鎺夎矾寰勩€?    /// </summary>
    private void ApplyLassoSelection()
    {
        if (LassoPath.Count < 3 || (MqMaxX - MqMinX) < 4f || (MqMaxY - MqMinY) < 4f)
        {
            // 鐐逛竴涓嬬┖鐧藉氨鎯?浠€涔堥兘鍒€?鈥斺€斿拰妗嗛€夐偅鏉″悓鏍风殑澶勭悊锛堣 ApplyMarquee锛夈€?            Doc.Selected.Clear();
            CopyDragArmed = false;
            LassoPath.Clear();
            _dirty = true;
            return;
        }

        int n = Doc.ApplyLasso(LassoPath, ViewportCanvas, LassoEdgeSnapLogical * DpiScale,
                               additive: _lassoAdditive, subtractive: _lassoSubtractive);
        Console.WriteLine(_lassoSubtractive
            ? $"lasso removed {n} strokes锛堝墿 {Doc.Selected.Count} 鏉★級"
            : $"lasso selected {n} strokes锛堝湀 {LassoPath.Count} 涓偣"
              + (_lassoAdditive ? $"锛孲hift 鍔犻€夛紝鍏?{Doc.Selected.Count} 鏉★級" : "锛?));
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>
    /// "鍦堝埌灞忓箷杈?鐨勫垽瀹氬宸紙閫昏緫鍍忕礌锛夛細鎸囬拡绂诲彲瑙佸尯鍩熻竟绾胯繖涔堣繎灏卞綋浣滆创杈癸紝
    /// 閭ｄ竴娈垫寜鏃犻檺寤朵几澶勭悊锛堣 <see cref="InkDocument.ApplyLasso"/>锛夈€?    ///
    /// 鍙?4锛氭寚閽堣兘琚嫋鍒扮獥鍙ｅ锛堟湁鎹曡幏锛夛紝鎵€浠ヨ创杈瑰熀鏈兘鑳芥弧瓒筹紱鍙栧お澶у張浼氳
    /// "绂昏竟杩樻湁涓€鐐圭偣"鐨勫湀涔熷伔鍋峰欢浼稿嚭鍘伙紝鎶婂睆骞曞鐨勫ⅷ涓€璧峰湀杩涙潵銆?    /// </summary>
    internal const float LassoEdgeSnapLogical = 4f;

    /// <summary>
    /// 璇讳竴鏉℃寚閽堟秷鎭紝椤轰究鎶婁袱涓椂鏍囪涓嬫潵锛堝欢鏃舵帰閽堢敤锛夛細
    ///   pi.PerformanceCount 鈥斺€?绯荤粺缁欑殑纭欢/椹卞姩鏃舵爣锛圦PC 璁℃暟锛?    ///   鐜板湪                鈥斺€?鎴戜滑鐪熸鎷垮埌瀹冪殑鏃跺埢
    /// 涓よ€呬箣宸?= 杈撳叆鏍?+ 鎴戜滑鑷繁鐨勬秷鎭槦鍒椼€?*杩欎竴娈甸噷鍙湁闃熷垪鏄垜浠殑璐ｄ换**锛?    /// 鑰岄槦鍒楁嫋寤剁殑鐩存帴鍘熷洜灏辨槸 Present(1) 鎶婄嚎绋嬪崱鍦ㄥ瀭鐩村悓姝ラ噷銆?    /// </summary>
    /// <summary>
    /// 瑙︽懜瑙︾偣鐨?*鎺ヨЕ闈㈢Н**锛堢煩褰㈤暱杈癸紝鐗╃悊鍍忕礌锛夈€傛嬁涓嶅埌 / 杩欏潡灞忎笉鎶?鈫?0銆?    /// 鎵嬫帉鎿︾殑鍒嗙骇闈犲畠锛堣 <see cref="TouchGestures"/> 鐨勮嚜閫傚簲鍩虹嚎锛夈€?    /// </summary>
    private static float ReadTouchSize(uint id)
    {
        // 鐢?*鏄惧紡缂撳啿鍖?*锛?60 瀛楄妭锛夎€屼笉鏄?`out POINTER_TOUCH_INFO`锛氬悗鑰呰蛋鐨勬槸
        // 杩愯鏃舵寜鎵樼缁撴瀯浣撳ぇ灏忓垎閰嶇殑鏍堟Ы锛屽悎鎴愭寚閽堥偅鏉¤矾涓婂疄娴嬩細 AV锛?        // 鏄惧紡缁欒冻缂撳啿鍖?+ `PtrToStructure` 鏈€绋筹紙杩欓噷鏄函璇伙紝娌℃湁鍐欏洖锛夈€?        IntPtr buf = Marshal.AllocHGlobal(160);
        try
        {
            if (!Native.GetPointerTouchInfo(id, buf)) return 0f;
            var ti = Marshal.PtrToStructure<Native.POINTER_TOUCH_INFO>(buf);
            int w = ti.rcContact.Width, h = ti.rcContact.Height;
            if (w <= 0 && h <= 0) { w = ti.rcContactRaw.Width; h = ti.rcContactRaw.Height; }
            return MathF.Max(w, h);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool ReadPointer(uint id, out float x, out float y, out float pressure,
                             out bool inverted, out uint pointerType)
    {        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        if (!Native.GetPointerInfo(id, out var pi)) return false;
        _lastInputPerfQpc = pi.PerformanceCount;
        _lastInputMsgQpc = Qpc.Now;
        if (LatencyRecording && _prevInputMsgQpc != 0)
        {
            double gap = Qpc.Ms(_prevInputMsgQpc, _lastInputMsgQpc);
            if (gap > 0 && gap < 500) Latency.AddInputGap(gap);
        }
        _prevInputMsgQpc = _lastInputMsgQpc;
        _framePoints++;
        x = pi.ptPixelLocationX;
        y = pi.ptPixelLocationY;
        pointerType = pi.pointerType;
        // 绗旂殑"鍑鸿韩"锛堢瑪灏栧湪涓嶅湪灞忓箷涓婏級锛?*鎵嬪啓鏉垮拰瑙﹀睆绗旂殑钀界偣瑙勫垯涓嶅悓**锛岃繖鏉″繀椤?        // 姣忔潯绗旀秷鎭兘纭涓€閬嶏紙鎹㈣澶?鎹㈢瑪浼氬彉锛夛紝缁撴灉鎸夎澶囧彞鏌勭紦瀛樸€佸彧鏌ヤ竴娆°€?        // 榧犳爣/瑙︽懜涓嶆煡锛堝畠浠殑璺笉闇€瑕佽繖涓俊鎭級銆?        if (pi.pointerType == Native.PT_PEN)
            PenDeviceOnScreen = QueryPenOnScreen(pi.sourceDevice);
        if (pi.pointerType == Native.PT_PEN && Native.GetPointerPenInfo(id, out var pen))
        {
            pressure = pen.pressure / 1024f;
            if (pressure <= 0.01f) pressure = 0.5f;
            inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;
        }
        // 鐣欑粰鍏夋爣閭ｄ竴鏃忕敤锛圗ffectiveTool锛夛細**姣忔潯娑堟伅閮藉啓**锛岄紶鏍?/ 瑙︽懜鑷劧钀藉洖 false銆?        // 鍏夋爣鐨勫舰鐘跺繀椤诲湪"鎮仠"锛堣繕娌¤惤绗旓級鏃跺氨瀵癸紝鎵€浠ヤ笉鑳界瓑 OnPointerDown 閲岄偅涓眬閮ㄥ彉閲忋€?        LastPointerInverted = inverted;
        return true;
    }

    // =====================================================================
    //  Hotkeys
    // =====================================================================

    private void HandleHotkey(int id)
    {
        // id 鏄?娉ㄥ唽椤哄簭"锛屽姩浣滅敱閿綅琛ㄥ喅瀹氾紙瑙?RegisterHotkeys锛夈€?        var action = ActionForHotkeyId(id);
        if (action == KeyAction.None) return;
        RunAction(action);
        // 鎹㈠伐鍏?鎹㈡ā寮忎箣鍚庢寚閽堝舰鐘剁珛鍒昏璺熺潃鍙樸€?        ApplyCursor();
        _dirty = true;
    }

    /// <summary>
    /// 鎵ц涓€涓姩浣溿€?*鍏ㄥ眬鐑敭鍜屾壒娉ㄥ唴蹇嵎閿蛋鍚屼竴涓叆鍙?*鈥斺€斿惁鍒欏悓涓€涓姩浣?    /// 浼氭湁涓や唤瀹炵幇锛屾棭鏅氫細涓嶄竴鑷达紙"鎸?Ctrl+Alt+1 鎹㈢瑪鎹㈠緱濂藉ソ鐨勶紝鎸?Ctrl+1 鍗村皯娓呬簡婵€鍏?锛夈€?    /// </summary>
    private void RunAction(KeyAction action)
    {
        // 鎵嬫祴鍙帮細浜嬩欢娴佹按銆傛挙閿€灏ゅ叾閲嶈鈥斺€?鎿﹀畬椹笂鎾ら攢"灏辨槸"杩欎竴鎿︿笉鏄垜鎯宠鐨?銆?        if (EraserTelemetry != null && action != KeyAction.None)
            EraserTelemetry.Note(KeyMap.Describe(action), NowMs);

        // 鍛煎嚭鐩樺紑鐫€鏃讹紝鍒殑鍔ㄤ綔鍏堟妸瀹冩敹鎺夛紙瀹冭嚜宸辩殑"鍐嶆寜涓€娆?涓嶇畻锛汦sc 鍦?HandleKeyDown 閲屽崟鐙鐞嗭級銆?        if (RadialPaletteActive && action != KeyAction.RadialPalette)
            CancelRadialPalette("鍏跺畠鍔ㄤ綔");

        // 杩炵画寰皟浼氳瘽锛氶櫎浜嗗井璋?缂╂斁鏈韩锛屽叾瀹冧换浣曞姩浣滈兘琛ㄧず"杩欐鎵嬪仠浜?鈥斺€斿厛灏佸彛锛?        // 鍏嶅緱涓嬩竴娆″井璋冩妸澧為噺鍙犲埌涓€鏉″凡缁忕炕绡囩殑鍔ㄤ綔涓婏紙鍋滄墜瓒呮椂鐢?StepMicroAdjust 鏀讹級銆?        if (_microAdjust != null && !IsMicroAdjustAction(action)) _microAdjust = null;

        switch (action)
        {
            // 绌块€忓紑鍏筹紙鍏ㄥ眬 Ctrl+Alt+Shift+T 杩欐潯鍚屼竴鏉¤矾锛夛細閫€鍑烘椂鎭㈠杩涚┛閫忓墠鐨勬澘鎬侊紝
            // 瑙?SetPassThrough 鐨?restoreBoard銆?            case KeyAction.TogglePassThrough: SetPassThrough(!PassThrough, restoreBoard: true); break;
            // 鍛煎嚭鐩橈細鎸変綇鎵嶅嚭鏉ョ殑"鏍囪抗鑿滃崟"锛堟澗鎵嬬‘璁わ紝瑙?OpenRadialPalette锛夈€?            case KeyAction.RadialPalette: OpenRadialPalette(); break;
            // 宸ュ叿閿粺涓€璧?ToolKeyPress锛?*涓嶇鏄簲鐢ㄥ唴閿繕鏄?鏀炬槧鏃剁殑涓存椂鍏ㄥ眬鐑敭"**锛?            // 閮借鏈?宸茬粡鏄畠 鈫?鎹㈣壊/鎹㈡。"杩欐潯閫昏緫锛堢敤鎴?2026-09-30 瀹炴祴锛氭斁鏄犻噷 Ctrl+P
            // 鑳藉垏鍒扮瑪浜嗭紝浣嗗凡缁忔槸绗旀椂鍐嶆寜涓嶆崲鑹测€斺€斿氨鏄洜涓鸿繖鏉＄儹閿矾寰勬紡浜?ToolKeyPress锛夈€?            case KeyAction.ToolPen: ToolKeyPress(KeyAction.ToolPen); break;
            case KeyAction.ToolHighlighter: ToolKeyPress(KeyAction.ToolHighlighter); break;
            case KeyAction.ToolLaser: ToolKeyPress(KeyAction.ToolLaser); break;
            case KeyAction.ToolEraser: ToolKeyPress(KeyAction.ToolEraser); break;
            case KeyAction.ToolPixelEraser: ToolKeyPress(KeyAction.ToolPixelEraser); break;
            case KeyAction.SplitErased:
            {
                int n = Doc.SplitErasedSelection();
                Console.WriteLine(n > 0
                    ? $"鎷嗗紑鎿︽柇鐨勭瑪杩癸細{n} 鏉?鈫?鍚勬鎴愪负鐙珛瀵硅薄锛堝彲鍗曠嫭鎼繍/鍒犻櫎锛?
                    : "鎷嗗紑鎿︽柇鐨勭瑪杩癸細閫変腑鐨勯噷闈㈡病鏈夎鎿︽柇鐨勶紙鍏堢敤妗嗛€夐€変腑瀹冿級");
                break;
            }
            case KeyAction.ToolCapture: SwitchTool(Tool.Capture); BeginCaptureMode(); break;
            case KeyAction.ToolMarquee: SwitchTool(Tool.Marquee); break;
            // 鍥惧舰**娌℃湁閿綅鍔ㄤ綔**锛堢敤鎴?2026-09-19 瀹氾細鍥惧舰閫氶€氫笉瑕佸揩鎹烽敭锛夛紝
            // 鎹㈢绫诲彧鏈夐潰鏉夸笂甯﹂偅涓€鏉¤矾锛坄FullUi.ActivateSegment` 鈫?`SwitchTool`锛夈€?            case KeyAction.SelectShape: ToggleSelectMode(); break;
            case KeyAction.Undo: Doc.Undo(); Laser.Clear(); break;
            case KeyAction.Redo: Doc.Redo(); break;
            case KeyAction.Copy: CopySelectionToClipboard(); break;
            // 閿洏杩欎竴鏉″拰姗＄毊甯﹂偅涓寜閽槸**鍚屼竴涓姩浣?*锛堟竻闄ゅ叏閮ㄥⅷ杩癸紱2026-10-14 缁熶竴锛夈€?            case KeyAction.Clear: ClearAllInkFromUi(); break;
            case KeyAction.ToggleBoard: SetBoardFromUi(!BoardOn); break;
            case KeyAction.ToggleHud: ShowHud = !ShowHud; break;
            case KeyAction.CycleWetMode: CycleWetMode(); break;
            case KeyAction.CycleWidth: CycleWidth(); break;
            case KeyAction.ToggleKeyboardMode: SetKeyboardMode(!KeyboardMode); break;
            case KeyAction.Quit: _quit = true; break;

            case KeyAction.SelectAll: SelectAll(); break;
            case KeyAction.Duplicate: Doc.DuplicateSelected(); break;
            case KeyAction.DeleteSelected: Doc.DeleteSelected(); break;
            case KeyAction.CancelSelection: Doc.Selected.Clear(); break;
            case KeyAction.Paste: PasteFromClipboard(); break;
            // 鏈夐€変腑 = 宸?鍙崇Щ 1锛?*娌￠€変腑锛氣啇鈫?涔熸槸缈婚〉**锛?026-10-05 鐢ㄦ埛瀹氾細
            // 鍥涗釜鏂瑰悜閿殑璇硶缁熶竴鈥斺€旀湁閫変腑灏卞井璋冿紝娌￠€変腑灏?鈫愨啋 缈婚〉銆佲啈鈫?婊氱敾甯冿級銆?            // 鏀炬槧閲屾病閫変腑鏃跺悓鏍疯蛋 鈫愨啋锛堜唬 PPT 缈婚〉锛夛紝鍜屾斁鏄犱复鏃跺叏灞€閿偅鏉¤矾涓€鑷淬€?            case KeyAction.NudgeLeft:
                if (Doc.Selected.Count > 0) Nudge(-1f, 0f);
                else if (PptMode) PptPrevFromUi();
                else FlipPageFromUi(false);
                break;
            // 鈫戔啌锛?*鏈夐€変腑 鈫?寰皟锛涙病閫変腑 鈫?涓婁笅婊氱敾甯?*锛堢敤鎴?2026-09-30 瀹氾細"浠讳綍鎯呭喌涓?            // 閮借兘涓婁笅婊戝姩鐢诲竷"锛屼竴鏍?= 灞忓箷楂樼殑 1/10锛岀収 ClassIn 鐨勬墜鎰燂級銆?            case KeyAction.NudgeUp:
                if (Doc.Selected.Count > 0) Nudge(0f, -1f); else ScrollCanvasBy(+ScrollStepLogical);
                break;
            case KeyAction.NudgeRight:
                if (Doc.Selected.Count > 0) Nudge(1f, 0f);
                else if (PptMode) PptNextFromUi();
                else FlipPageFromUi(true);
                break;
            case KeyAction.NudgeDown:
                if (Doc.Selected.Count > 0) Nudge(0f, 1f); else ScrollCanvasBy(-ScrollStepLogical);
                break;
            case KeyAction.NudgeLeftFar: Nudge(-10f, 0f); break;
            case KeyAction.NudgeUpFar: Nudge(0f, -10f); break;
            case KeyAction.NudgeRightFar: Nudge(10f, 0f); break;
            case KeyAction.NudgeDownFar: Nudge(0f, 10f); break;
            case KeyAction.ScaleUp: ScaleSelection(1.1f); break;
            case KeyAction.ScaleDown: ScaleSelection(1f / 1.1f); break;
            case KeyAction.FlipPageUp: FlipPageFromUi(false); break;
            case KeyAction.FlipPageDown: FlipPageFromUi(true); break;
            // 鏀炬槧鏃?鈫愨啋锛堜复鏃跺叏灞€鐑敭閫佽繘鏉ョ殑锛夛細**鏈夐€変腑 鈫?寰皟锛涙病閫変腑 鈫?浠?WPS/PPT 缈婚〉**銆?            // 閿洏鍦ㄦ垜浠墜閲岋紝涓嶆嫤鐨勮瘽 WPS 鏀朵笉鍒?鈫愨啋锛岃〃鐜板氨鏄?鎸変簡娌″弽搴?锛堢敤鎴峰疄娴嬶級銆?            case KeyAction.PptPrev:
                if (Doc.Selected.Count > 0) Nudge(-1f, 0f); else PptPrevFromUi();
                break;
            case KeyAction.PptNext:
                if (Doc.Selected.Count > 0) Nudge(1f, 0f); else PptNextFromUi();
                break;
            // 鏀炬槧鏃?鈫戔啌锛堜复鏃跺叏灞€鐑敭閫佽繘鏉ョ殑锛夛細鍜屾櫘閫氭ā寮忓悓涓€鏉￠€昏緫鈥斺€斾竴鏍?= 灞忓箷楂樼殑 1/10銆?            case KeyAction.PanUp:
                if (Doc.Selected.Count > 0) Nudge(0f, -1f); else ScrollCanvasBy(+ScrollStepLogical);
                break;
            case KeyAction.PanDown:
                if (Doc.Selected.Count > 0) Nudge(0f, 1f); else ScrollCanvasBy(-ScrollStepLogical);
                break;
        }
    }

    // ---- 杩炵画寰皟浼氳瘽锛?026-10-05锛?----------------------------------------
    //
    // 闂锛氭柟鍚戦敭鎸変綇浼氳嚜鍔ㄩ噸澶嶏紙Windows 杩炲彂 KEYDOWN锛夛紝鏃у疄鐜版瘡涓€涓嬮兘鏄竴鏉?    // `ApplyTransform` 鈫?鎾ら攢鏍堥噷鐬棿鍗佸嚑鏉★紝"绉绘浜嗘兂鎾ら攢瑕佹寜鍗佸嚑娆?Ctrl+Z"銆?    // 淇硶锛氬井璋?/ 缂╂斁**鍏辩敤涓€鏉″姩浣?*锛屽悗缁閲忓彔涓婂幓锛涙澗寮€鎴栧仠 400ms 灏?灏佸彛"锛?    // 涓嬩竴娆″井璋冨彟寮€涓€鏉°€備腑閫旇鍒殑鍔ㄤ綔鏀硅繃锛堥紶鏍囨嫋鍔ㄣ€佹挙閿€銆佹竻绌衡€︹€︼級涔熻寰楀嚭鏉モ€斺€?    // `TryAppendDelta` 鎷挎枃妗ｇ増鏈彿褰撻椄锛堣 Model.TransformObjectsAction锛夈€?    private TransformObjectsAction _microAdjust;
    private double _microAdjustLastMs = double.NegativeInfinity;
    private const double MicroAdjustMergeMs = 400;

    private static bool IsMicroAdjustAction(KeyAction a) => a is
        KeyAction.NudgeLeft or KeyAction.NudgeUp or KeyAction.NudgeRight or KeyAction.NudgeDown or
        KeyAction.NudgeLeftFar or KeyAction.NudgeUpFar or KeyAction.NudgeRightFar or KeyAction.NudgeDownFar or
        KeyAction.ScaleUp or KeyAction.ScaleDown;

    /// <summary>寰皟鎴栫缉鏀惧簲鐢ㄤ竴姝ワ紱鍜屼笂涓€姝ヨ繕鑳芥帴涓婏紙鍚屼竴鎵瑰璞°€佷腑闂存病鍒殑鏀瑰姩锛夊氨鍚堝苟銆?/summary>
    private void MicroAdjust(in Matrix3x2 delta)
    {
        if (Doc.Selected.Count == 0) return;
        if (_microAdjust != null && _microAdjust.SameTargets(Doc.Selected)
            && _microAdjust.TryAppendDelta(Doc, delta))
        {
            _microAdjustLastMs = NowMs;
            _dirty = true;
            return;
        }
        var act = new TransformObjectsAction(Doc.Selected, delta);
        act.Redo(Doc);
        Doc.CommitInteractive(act);
        _microAdjust = act;
        _microAdjustLastMs = NowMs;
        _dirty = true;
    }

    /// <summary>涓诲惊鐜瘡甯у彨锛氬仠鎵嬭秴杩?400ms 鈫?浼氳瘽灏佸彛锛堜笅涓€娆″井璋冨彟璧蜂竴鏉℃挙閿€璁板綍锛夈€?/summary>
    private void StepMicroAdjust()
    {
        if (_microAdjust != null && NowMs - _microAdjustLastMs > MicroAdjustMergeMs)
            _microAdjust = null;
    }

    /// <summary>
    /// 閫変腑瀵硅薄鐨勯敭鐩樼缉鏀撅紙`Ctrl+=` / `Ctrl+-`锛夛細浠?*閫夊尯妗嗕腑蹇?*涓洪敋锛堝拰鍙屾寚鎹忓悎鍚屼竴涓?    /// 鍙栫偣锛岃 TouchGestureMoved 閭ｄ竴娈碉級锛屼竴姝?10%銆傝蛋 <see cref="MicroAdjust"/>锛?    /// 鎵€浠ヨ繛鎸変簲涓?= 涓€鏉℃挙閿€銆?    /// </summary>
    private void ScaleSelection(float factor)
    {
        if (Doc.Selected.Count == 0)
        {
            Console.WriteLine("缂╂斁閫変腑锛氱幇鍦ㄦ病鏈夐€変腑瀵硅薄锛堝厛妗嗛€夛級");
            return;
        }
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        float cx = (aabb.MinX + aabb.MaxX) * 0.5f;
        float cy = (aabb.MinY + aabb.MaxY) * 0.5f;
        // 闃查€€鍖栵細缂╁埌鍙墿鍑犲儚绱犲氨鍒啀缂╀簡锛堝€掍笉鏄煩闃典細鍧忥紝鏄睆骞曚笂绛変簬娑堝け浜嗭級銆?        float maxSide = MathF.Max(aabb.MaxX - aabb.MinX, aabb.MaxY - aabb.MinY);
        if (factor < 1f && maxSide * factor < 4f)
        {
            Console.WriteLine("缂╂斁閫変腑锛氬凡缁忕缉鍒版渶灏忥紙鍐嶇缉灏辨病鏈変簡锛?);
            return;
        }
        MicroAdjust(Matrix3x2.CreateTranslation(-cx, -cy)
                  * Matrix3x2.CreateScale(factor)
                  * Matrix3x2.CreateTranslation(cx, cy));
    }

    /// <summary>
    /// 鏂瑰悜閿井璋冦€?*杩炵画鎸夛紙鍚寜浣忚嚜鍔ㄩ噸澶嶏級鍚堝苟鎴愪竴姝ユ挙閿€**锛?026-10-05锛夛細
    /// 姣忎竴姝ラ兘璧?<see cref="MicroAdjust"/>锛屽悓涓€娆?鎵嬩笉鍋?鍙暀涓€鏉℃挙閿€璁板綍锛?    /// 鍋滄墜 400ms 鎴栦腑閫斿仛浜嗗埆鐨勪簨鎵嶅皝鍙ｃ€?    /// </summary>
    private void Nudge(float dx, float dy) => MicroAdjust(Matrix3x2.CreateTranslation(dx, dy));

    /// <summary>
    /// 鍒囨崲**褰撳墠宸ュ叿**鐨勭矖缁嗐€備互鍓嶈繖閲屽啓姝绘敼绗斿锛屼簬鏄?閫変簡鑽у厜绗旀寜鍒囩矖缁嗘病鍙嶅簲"
    /// 鈥斺€旀敼鐨勪笉鏄畠銆傚洓绉嶅伐鍏峰悇鏈変竴寮犳。浣嶈〃锛堣 WidthPresets 鐨勬敞閲婏級銆?    /// </summary>
    /// <summary>
    /// 鎹㈠伐鍏枫€?*鐢ㄦ埛瑙勫垯锛?026-09-15 瀹氾紝涔熸槸 InkClass / PPT / Figma 鐨勬儻渚嬶級**锛?    /// "閫変腑鏄复鏃朵笂涓嬫枃"鈥斺€旈櫎浜嗘閫夊伐鍏疯嚜宸憋紝鎹㈠埌浠讳綍鍒殑宸ュ叿閮?*鏀惰捣閫夊尯**銆?    ///
    /// 涓嶈繖涔堝仛浼氬嚭涓や釜闂锛氣憼 閫夊尯閬僵浼氭妸鎺ヤ笅鏉ョ殑绗竴绗斿悆鎺夛紙鑰佸笀鐐瑰畬宸ュ叿鐢讳笉鍑烘潵锛?    /// 寰楀厛鐐逛竴涓嬬┖鐧斤級锛涒憽 鐢ㄦ埛鐪嬬潃"鎴戝凡缁忔崲宸ュ叿浜嗭紝鎬庝箞杩橀€夌潃"銆?    /// 婊氬姩銆佹縺鍏夈€侀敭鐩樼紪杈戦敭**涓嶇畻**鎹笂涓嬫枃锛堣 璁″垝-閫変腑宸ュ叿.md 鐨勯偅寮犺〃锛夈€?    /// </summary>
    private void SwitchTool(Tool t)
    {
        // 浠讳綍鎹㈠伐鍏凤紙闈㈡澘鏍?/ 宸ュ叿閿?/ 鑷锛夐兘瑕佸厛鎶婂懠鍑虹洏鏀舵帀鈥斺€旂洏杩樺湪銆?        // 宸ュ叿宸茬粡鎹㈣蛋锛屾槸鏈€瀹规槗鐪嬪嚭鏉ョ殑鐘舵€佹墦鏋躲€?        if (RadialPaletteActive) CancelRadialPalette("鎹㈠伐鍏?);
        // 鎹㈠埌鎴浘宸ュ叿 = 璁颁笅"杩涙埅鍥句箣鍓嶇敤鐨勫伐鍏?锛?.3.1锛欵sc 鍙栨秷鏃惰鍥炲埌瀹冿級銆?        // 鍙绗竴娆★紙杩涙潵涔嬪悗 Tool 宸茬粡鏄?Capture锛屼笉浼氬啀瑕嗙洊锛夈€?        if (t == Tool.Capture && Tool != Tool.Capture) _toolBeforeCapture = Tool;
        if (Tool != t && t != Tool.Marquee) ClearSelectionForNewContext();
        // **鎹㈠埌妗嗛€夊伐鍏?= 閭ｄ釜"鍒氱敾瀹?鐨勬浠庢鎸夊父瑙勯偅涓€濂楄蛋**锛堟暣鏉℃搷浣滄潯銆佹閲屼换鎰忎竴鐐?        // 閮借兘鎷栵級銆傜悊鐢憋細`SwitchTool` 瀵?鎹㈠埌妗嗛€?鏈潵灏?*涓嶆竻閫夊尯**锛堣涓婁竴鍙ワ級锛?        // 鑰?閫夋嫨"杩欎釜宸ュ叿鐨勫悕瀛楁湰韬氨鏄湪璇?鎴戣鏁寸悊瀹?鈥斺€旈偅鏃惰繕缁欎竴棰楀渾閽€?        // 瑕佽€佸笀鍐嶇偣涓€涓嬫墠鎽婂紑锛屽弽鑰岀粫锛堣繖涓€璺殑鍙ｅ緞瑙?`_autoSelCollapsed`锛夈€?        if (t == Tool.Marquee) _autoSelCollapsed = false;
        // **鎹㈠伐鍏?= 澶氱瑪鍥惧舰浣滃簾**锛堣琛?StepPlan锛岀洰鍓嶅彧鏈夊弻鏇茬嚎涓ょ瑪锛夈€?        //
        // 涓や欢浜嬩笉鑳藉皯锛氣憼 鐘舵€佸綊闆垛€斺€斾笉娓呯殑璇濓紝鍒囪蛋鍐嶅垏鍥炴潵鎸夌涓€绗旓紝浼氭帴鐫€涓婁竴杞幓鏀?        // 閭ｆ潯鐢讳簡涓€鍗婄殑鏇茬嚎锛岃€屼笉鏄敾鏂扮殑锛涒憽 **鎶婇偅鏉″崐鎴愬搧涓㈡帀**鈥斺€斿畠涓€鐩存寕鍦?        // ActiveStroke 涓娿€?*涓嶅湪鏂囨。閲?*锛岃€?ActiveStroke 鏄棤鏉′欢鍙備笌娓叉煋鐨?        //锛堣 Overlay 鐨?DrawStroke锛夛紝涓嶄涪灏变細鍙樻垚"灞忓箷涓婄暀鐫€涓€鏉℃€庝箞涔熸摝涓嶆帀鐨勬洸绾?
        //锛堣嚜妫€閲屾湁涓€鏉′笓闂ㄦ暟灞忓箷涓婇偅涓€鍧楃殑澧級銆?        if (_stepPlan != null)
        {
            ActiveStroke = null;
            _dirty = true;
        }
        _stepPlan = null;
        _stepIndex = 0;
        _stepSnap = ShapeSnapKind.None;      // 銆岀洿妫辨煴銆嶉偅棰楄兌鍥婅窡鐫€鍗婃垚鍝佷竴璧蜂綔搴?        Tool = t;
        // **璁颁綇"姗＄毊鐢ㄧ殑鏄摢涓€绉嶅舰鎬?**锛堟暣绗旀摝 / 闈㈢Н鎿︼級锛氭寜 Ctrl+E 鍥炴潵鏃跺垏鍥炲畠锛?        // 骞朵笖涓嬫鍚姩涔熻繕鍦紙瑙?ToolKeyPress 涓?Run 閲岀殑璇诲彇锛夈€?        if (t == Tool.Eraser || t == Tool.PixelEraser)
        {
            _eraserKind = t;
            SetUiPref("eraserKind", t == Tool.PixelEraser ? "pixel" : "whole");
        }

        // **绌块€忓拰宸ュ叿鏄簰鏂ョ殑**锛堢敤鎴?2026-09-17 瀹氾級銆?        //
        // 绌块€忓紑鐫€鐨勬椂鍊欑偣鍑昏惤鍒颁笅灞傜▼搴忎笂锛岀敾甯冩牴鏈敹涓嶅埌绗斺€斺€旇繖鏃?閫変腑浜嗙瑪"鏄釜鍋囩姸鎬侊細
        // 鎸夐挳浜潃銆佸啓涓嶅嚭瀛椼€傛墍浠ユ崲宸ュ叿锛堢偣闈㈡澘涔熷ソ銆佹寜鐑敭涔熷ソ锛夌瓑浜庝竴鍙?鎴戣寮€濮嬬敤浜?锛?        // 椤烘墜鎶婄┛閫忓叧鎺夈€傚弽杩囨潵锛岀偣闈㈡澘涓婇偅涓?榧犳爣"鏍兼槸鏄庤瑕佺┛閫忥紝瀹冨崟鐙紑銆?        // 杩欐潯閫€鍑?*涓嶆仮澶嶇櫧鏉?*锛坮estoreBoard: false锛夛細鎹㈠伐鍏风殑鎰忔€濇槸"鎴戠幇鍦ㄥ氨瑕佸啓"锛?        // 鐢诲竷瑕佷繚鎸佺溂鍓嶆墍瑙佲€斺€旂獊鐒剁洊鍥炵櫧鏉垮弽鑰屾槸鎯婂悡锛堝拰"绌块€忓紑鍏?閫€鍑哄尯鍒嗭紝瑙?SetPassThrough锛夈€?        if (PassThrough) SetPassThrough(false);

        // 鍗婅矾鎹㈠伐鍏凤細閭ｆ潯杩樺湪鍐欑殑婵€鍏夎建杩?*褰撲綔鎶墜鏀跺熬**锛堟暣鎵瑰紑濮?2 绉掕鏃讹級銆?        // 涓嶈繖涔堝仛鐨勮瘽杩欐壒杞ㄨ抗姘歌繙鏄?杩樺湪鍐?锛堣鏃跺櫒鍋滃湪 -inf锛夛紝浜庢槸鏃笉娣″嚭銆?        // 鍙堣 `Laser.Visible` 涓€鐩翠负鐪?鈫?**姣忎竴甯ч兘鍑轰竴甯?*锛堢櫧鐑?CPU锛夈€?        // 鈿?`Release` 鑷繁浼氬垽"鏈夋病鏈夋鍦ㄥ啓鐨?锛氭病鏈夊氨鐩存帴杩斿洖锛屾墍浠ヨ繖閲屽彲浠ユ棤鑴戣皟锛?        //   涓嶄細鎶婁笂涓€鎵硅繕鍦ㄦ贰鐨勬縺鍏夌粰"缁懡"锛堣 `LaserTrail.Release`锛夈€?        Laser.Release(NowMs);
    }

    /// <summary>鏀惰捣閫夊尯锛堟崲宸ュ叿銆佷互鍙?涓庨€変腑鏃犲叧鐨勬柊鎿嶄綔"璧拌繖閲岋級銆?/summary>
    private void ClearSelectionForNewContext()
    {
        CopyDragArmed = false;
        // 閫夊尯娌′簡锛岄潰鏉垮拰瀹冧笅闈㈡寕鐨勮嚜瀹氫箟鍙栬壊鏉夸篃涓€璧锋敹鎺夆€斺€?        // 涓嶆敹鐨勮瘽"闈㈡澘鍏充簡鑹叉澘杩樻寕鍦ㄥ睆骞曚笂"锛堣 CloseCustomColor 閭ｆ娉ㄩ噴锛夈€?        CloseCustomColor();
        SelPanelOpen = SelPanel.None;
        // 閫夊尯娌′簡锛?鐢诲畬鑷姩閫変腑閭ｄ釜妗嗚繕鏀舵病鏀剁潃"涔熷氨娌℃湁鎰忎箟浜嗏€斺€斿綊闆讹紝
        // 鍏嶅緱涓嬩竴涓嚜鍔ㄩ€変腑鐨勬鍒氬紑濮嬪氨甯︾潃涓婁竴杞殑灞曞紑鐘舵€侊紙瑙?`_autoSelCollapsed`锛夈€?        _autoSelCollapsed = false;
        // **鍋滈】鎴愬瀷閭ｄ竴璺殑"閫変腑"涔熶竴璧峰綊闆?*锛堣 `_dwellSelected`锛夛細瀹冩瘮 `_autoSelCollapsed`
        // 澶氭媴涓€浠朵簨鈥斺€旀斁琛?绗斾笅闈㈤偅涓鑳戒笉鑳界偣"锛屾墍浠ユ洿涓嶈兘璺熺潃涓婁竴杞暀鍒颁笅涓€杞€?        _dwellSelected = false;
        // 瑙︽懜鎵嬪娍閫夊嚭鏉ョ殑閭ｄ竴璺悓鐞嗭紙瑙?`_touchSelected`锛夈€?        _touchSelected = false;
        if (Doc.Selected.Count == 0) return;
        Doc.Selected.Clear();
        _dirty = true;
    }

    internal void CycleWidth()
    {
        switch (Tool)
        {
            case Tool.Highlighter:
                HighlighterWidthIndex = (HighlighterWidthIndex + 1) % HighlighterWidthPresets.Length;
                HighlighterWidthLogical = HighlighterWidthPresets[HighlighterWidthIndex];
                break;
            case Tool.Laser:
                LaserWidthIndex = (LaserWidthIndex + 1) % LaserWidthPresets.Length;
                LaserWidthLogical = LaserWidthPresets[LaserWidthIndex];
                break;
            case Tool.PixelEraser:
                PixelEraserWidthIndex = (PixelEraserWidthIndex + 1) % PixelEraserWidthPresets.Length;
                PixelEraserWidthLogical = PixelEraserWidthPresets[PixelEraserWidthIndex];
                break;
            case Tool.Eraser:
                // 鏁寸瑪姗＄毊涔熸湁鑷繁鐨勬。浣嶃€備互鍓嶅畠钀借繘 default锛屾寜 Ctrl+Alt+6 鏀圭殑鏄?*绗斿**
                // 鈥斺€?鎷跨潃姗＄毊璋冪矖缁嗭紝绗旇抗鍙樼矖浜?灏辨槸杩欎箞鏉ョ殑銆?                EraserRadiusIndex = (EraserRadiusIndex + 1) % EraserRadiusPresets.Length;
                EraserRadiusLogical = EraserRadiusPresets[EraserRadiusIndex];
                break;
            default:
                WidthPresetIndex = (WidthPresetIndex + 1) % WidthPresets.Length;
                PenWidthLogical = WidthPresets[WidthPresetIndex];
                break;
        }
        Console.WriteLine($"{Tool} 绮楃粏 -> {CurrentToolWidthLogical} 閫昏緫鍍忕礌"
                        + $"锛堟湰鏈哄疄闄?{CurrentToolWidthLogical * DpiScale:F0} 鐗╃悊鍍忕礌锛?);
        EraserTelemetry?.Note($"{ToolName(Tool)}绮楃粏 鈫?{CurrentToolWidthLogical:F0} 閫昏緫鍍忕礌", NowMs);
        // **璁颁綇绮楃粏妗?*锛堢敤鎴?2026-09-30 瀹氾細绗?/ 鑽у厜绗?/ 婵€鍏夌瑪 / 闈㈢Н鎿?鍒嗗紑璁帮級銆?        // 2026-10-09 鏅氶敭鍚嶅啀鍗囦竴浠ｏ紙`w3.pen` / `w2.*`锛夆€斺€旀柊琛ㄧ殑"绗嚑妗?涓庤€侀敭鐨勮涔変笉鍚岋紝
        // 娣疯浼氶敊浣嶏紱鑰侀敭鍙綋杩佺Щ鏉ユ簮銆傜瑪鍚屾椂鍐欎竴浠?鏁板€?锛坄wv.pen`锛夈€?        SetUiPref("w3.pen", WidthPresetIndex.ToString());
        SetUiPref("wv.pen", PenWidthLogical.ToString("0.##"));
        SetUiPref("w2.hl", HighlighterWidthIndex.ToString());
        SetUiPref("w2.laser", LaserWidthIndex.ToString());
        SetUiPref("w2.pixel", PixelEraserWidthIndex.ToString());
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 寮€鍏崇┛閫忋€?    ///
    /// `restoreBoard`锛氶€€鍑虹┛閫忔椂锛岃涓嶈鎶?*杩涚┛閫忔椂琚『鎵嬪叧鎺夌殑鐧芥澘**鎭㈠銆?    ///   路 **绌块€忓紑鍏?*閫€鍑猴紙闈㈡澘閭ｄ竴鏍?/ 鍏ㄥ眬 `Ctrl+Alt+Shift+T`锛変紶 true鈥斺€?    ///     鑰佸笀鎸夊畠鐨勬剰鎬濇槸"鍥炲埌鍒氭墠"锛屾墍浠?鏉垮紑杩樻槸寮€銆佸叧杩樻槸鍏?锛?    ///   路 **鎹㈠伐鍏?*閫€鍑猴紙`SwitchTool` 閲岀殑鑷姩鍏崇┛閫忥級浼?false鈥斺€旈偅鏉¤矾鐨勬剰鎬濇槸
    ///     "鎴戠幇鍦ㄥ氨瑕佸啓"锛岀敾甯冭淇濇寔鐪煎墠鎵€瑙侊紙闇插嚭鏉ョ殑涓嬪眰搴旂敤锛夛紝绐佺劧鐩栧洖鐧芥澘鍙嶈€屾槸鎯婂悡锛?    ///     鎯冲洖鏉匡紝鍐嶇偣涓€涓嬬櫧鏉挎牸灏辫锛堢敤鎴?2026-09-30 鎷嶆澘锛岃銆婅皟鐮?蹇嵎閿?鐒︾偣涓庣┛閫忋€?.3锛夈€?    /// </summary>
    private void SetPassThrough(bool on, bool restoreBoard = false)
    {
        // 杩涚┛閫忓厛璁颁笅鏉挎€侊紙瑕佽"寮€鍏抽€€鍑?鐢ㄦ潵鎭㈠锛夈€?        // 鍙湪"鐪熺殑浠庡叧鍒板紑"杩欎竴涓嬭鈥斺€旈噸澶嶈皟 SetPassThrough(true) 鏃?BoardOn 宸茬粡琚叧鎺変簡锛?        // 鍐嶈涓€娆″氨浼氭妸澶栭潰鐨勫揩鐓ц鐩栨垚 false锛岄€€鍑烘椂鍙嶈€屼笉鎭㈠銆?        if (on && !PassThrough) _boardBeforePassThrough = BoardOn;

        // 杩涚┛閫?= "閿洏/鎸囬拡閮界粰涓嬪眰"锛氬紑鐫€鐨勫懠鍑虹洏鏀舵帀锛堝拰 8.5 宸ュ叿閿笉鍝嶅簲鍚屼竴鏉¤涔夛級銆?        if (on && RadialPaletteActive) CancelRadialPalette("杩涚┛閫?);

        PassThrough = on;
        // 绌块€?= "閿洏璁╃粰涓嬪眰"锛氭斁鏄犱复鏃跺叏灞€閿窡鐫€鎸?鎽橈紙鐢ㄦ埛 2026-09-30 瀹氾紝
        // 瑙?PptHotkeys 閭ｆ娉ㄩ噴锛夈€備笉鍦ㄦ斁鏄犳椂杩欎竴鍙ユ槸绌烘搷浣溿€?        SyncPptHotkeys();
        // 绌块€忔墦寮€/鍏虫帀鏃舵妸婵€鍏夎建杩规竻鎺夛紙鍘熸潵鏄妸 `Visible` 缃亣锛岀瓑浠蜂簬"绔嬪埢鍏ㄦ病"锛夈€?        // 鈿?鍒彧闅愯棌涓嶆竻锛氳建杩逛細涓€鐩寸暀鍦ㄩ泦鍚堥噷锛宍Laser.Visible` 浠嶄负鐪?鈫?        //    姣忎竴甯ч兘鍑轰竴甯э紙鐧界儳 CPU锛夛紝鑰屼笖涓嬫涓€杩涙潵瀹冧滑鍙堝啋鍑烘潵銆?        if (!on) Laser.Clear();

        // **绌块€忓拰鐧芥澘浜掓枼**锛堢敤鎴?2026-09-17 闂殑閭ｆ潯锛夈€備袱涓柟鍚戦兘瑕佹尅锛?        //   路 寮€鐧芥澘 鈫?鍏崇┛閫忥細鐧芥澘鏄笉閫忔槑鐨勪竴灞傦紝绌块€忔槸"鐐瑰嚮钀藉埌涓嬪眰绋嬪簭"锛?        //     涓や釜涓€璧峰紑鐫€锛岃€佸笀鐪嬪埌鐨勬槸鐧芥澘銆佺偣鍒扮殑鍗存槸鐧芥澘涓嬮潰閭ｄ釜鐪嬩笉瑙佺殑绐楀彛銆?        //   路 寮€绌块€?鈫?鍏崇櫧鏉匡細鍚屼笂锛屽弽杩囨潵涔熶竴鏍疯涓嶉€氥€?        // 琚叧鎺夌殑鐧芥澘**寮€鍏抽€€鍑烘椂鎭㈠**锛坮estoreBoard锛岀敤鎴?2026-09-30 鎷嶆澘锛夛紱
        // **鎹㈠伐鍏烽€€鍑轰笉鎭㈠**鈥斺€旂悊鐢辫鏂规硶澶撮偅涓よ銆?        if (on && BoardOn)
        {
            BoardOn = false;
            Doc.InvalidateAll();
            NotifyUiStateChanged();
        }
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        // 绌块€忔椂鎶婃寚閽堜氦杩樼粰涓嬪眰绐楀彛锛圓pplyCursor 浼氬湪绌块€忔ā寮忎笅鑷姩鏀炬墜锛屽苟浣滃簾缂撳瓨锛夛紱
        // 閫€鍑虹┛閫忚绔嬪埢鎶婂睘浜庢垜浠殑鍏夋爣璁惧洖鏉ワ紝涓嶅繀绛変笅涓€娆￠紶鏍囩Щ鍔?        //锛堣繖涓€鍙ユ槸 `force`锛氭敼鏍峰紡鍒氭妸鍏夋爣鎭㈠鎴愮澶达紝鑰岀紦瀛橀噷鐨勫€煎凡缁忎笉鎴愮珛浜嗭級銆?        ApplyCursor(force: true);

        if (!on)
        {
            // **鎭㈠鏉挎€?*锛堝彧鎸?寮€鍏抽€€鍑?锛夛細杩涚┛閫忓墠鏉挎槸寮€鐨勩€佺幇鍦ㄨ鎴戜滑涓€璧峰叧鐫€ 鈫?寮€鍥炴潵銆?            // 鎭㈠瑕佽窡 SetBoardFromUi 涓€鏍锋暣灞備綔搴燂紙搴曡壊鏄儤杩涘垎鍧楃紦瀛樼殑锛夈€?            if (restoreBoard && _boardBeforePassThrough && !BoardOn)
            {
                BoardOn = true;
                Doc.InvalidateAll();
                NotifyUiStateChanged();
                _dirty = true;
                Console.WriteLine("閫€鍑虹┛閫忥細鐧芥澘鎭㈠鍒拌繘绌块€忎箣鍓嶏紙寮€鐫€锛?);
            }
            _boardBeforePassThrough = false;   // 蹇収鍙湇鍔＄浉閭昏繖鍑犳绌块€忥紝鐢ㄥ畬鍗虫竻

            // **绌块€忓叧鎺?= 鍥炲埌"鑳芥壒娉?鐨勭姸鎬?鈫?蹇呴』鎶婇敭鐩?鍓嶅彴瑕佸洖鏉?*銆?            // 鐢ㄦ埛 2026-09-30 澶嶇幇鐨勭湡 bug锛氱┛閫忓紑寮€鍏冲叧鍑犳涔嬪悗锛孋trl+P 杩欎簺搴旂敤鍐呭揩鎹烽敭
            // 灏卞交搴曟浜嗏€斺€斿洜涓虹┛閫忔湡闂存垜浠殑绐楀彛涓嶆槸鍓嶅彴锛堟牱寮忛噷涔熶笉璁╁畠琚縺娲伙級锛?            // 鍏虫帀绌块€忔椂鍙仮澶嶄簡鏍峰紡銆?*娌′汉鎶婂墠鍙拌繕缁欐垜浠?*锛屼簬鏄寜閿叏琚埆鐨勭獥鍙ｆ敹璧般€?            // `SetKeyboardMode` 閲岄偅鍙?SetForegroundWindow 姝ｆ槸骞茶繖涓殑锛堝箓绛夛紝閲嶅璋冩病鍓綔鐢級銆?            // 鈿?鍜?閫€鍑烘斁鏄犺鎶婂墠鍙拌鍥炴潵"鏄悓涓€绫昏ˉ涓侊紝瑙?Ppt.ExitPptMode鈥斺€斾互鍚庡嚒鏄?            //   "浠庡埆鐨勭姸鎬佸垏鍥炴壒娉ㄦ€?鐨勫湴鏂归兘瑕佸仛杩欎竴姝ャ€?            if (_windows.Count > 0) SetKeyboardMode(KeyboardMode);
        }

        Console.WriteLine($"pass-through = {on} (mode {PassMode})");
    }


    internal void ApplyPassThroughStyle(OverlayWindow w)
    {
        long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
        const long clear = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        ex &= ~clear;

        if (PassThrough)
        {
            if (PassMode == PassThroughMode.ExTransparent)
                ex |= Native.WS_EX_TRANSPARENT;
            else if (PassMode == PassThroughMode.LayeredTransparent)
                ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        }

        Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);

        // 鏀规墿灞曟牱寮忎細璁╃郴缁?*椤烘墜鎶婂厜鏍囨仮澶嶆垚绫诲厜鏍囷紙绠ご锛?*鈥斺€旀垜浠偅涓?        // "宸茶杩囧氨涓嶉噸璁?鐨勭紦瀛樿繖鏃跺氨鎴愪簡鍋囦簨瀹烇紙瑙?ApplyCursor 鐨勬敞閲婏級銆?        // 浣滃簾瀹冿紝涓嬩竴娆?ApplyCursor 鎵嶄細鎶婂睘浜庢垜浠殑鍏夋爣鐪熻鍥炲幓銆?        _cursorApplied = IntPtr.Zero;
    }

    /// <summary>
    /// 鍏崇獥銆佹敞閿€蹇嵎閿€侀噴鏀捐澶囥€傚瓙绫昏嫢瑕佽鍐欙紝鍔″繀鍏堣皟鐢?base銆?    /// </summary>
    protected virtual void Shutdown()
    {
        foreach (var w in _windows)
        {
            Native.KillTimer(w.Hwnd, (IntPtr)1);
            for (int i = 1; i <= _hotkeyActions.Count + 2; i++) Native.UnregisterHotKey(w.Hwnd, i);
            w.Dispose();
        }
        // 鍙啓"鏀硅繃鐨?閿綅鍜?鐣岄潰鏀硅繃鐨?鍋忓ソ锛涢兘娌″姩杩囧氨涓嶇鐢ㄦ埛鐨勯厤缃枃浠躲€?        FlushSettings();

        // 閫€鍑哄墠琛ヤ竴娆¤嚜鍔ㄥ瓨妗ｏ紙涓嬩竴娆℃墦寮€鎺ヤ笂锛夈€傝嚜妫€妯″紡涓嶅姩鐢ㄦ埛鐨勫瓨妗ｃ€?        if (Doc.Version != _autoSavedVersion) AutoSaveNow();

        // 鍦ㄩ€旂殑鍛ㄦ湡鍐欑洏锛堟澘涔﹀巻鍙?/ 鑷姩瀛樻。 / PPT 鎵规敞锛夊厛鍏ㄩ儴绛夎惤瀹屸€斺€?*椤哄簭涓嶈兘鍙?*锛?        // 鍙嶄簡鐨勮瘽锛?鍚庡埌鐨勬棫鍐呭"浼氭妸涓嬮潰杩欎唤閫€鍑哄揩鐓х洊鎺夈€?        BackgroundSaver.Flush(5000);

        // 鏉夸功鍘嗗彶锛氶€€鍑哄墠鍐嶈ˉ涓€浠斤紙**涓嶅彈"鑷姩鎭㈠"鍋忓ソ褰卞搷**锛夈€傝繖涓€浠芥槸"鍏宠蒋浠堕偅涓€鍒?
        // 鐨勬澘涔︾敾甯冣€斺€斾笂闈㈤偅鏉?鍋滈】鎵嶅啓"鍙繚璇侀樁娈垫€х暀瀛橈紝鏈€鍚庤繖涓€娈甸潬瀹冨厹搴曘€?        // 閫€鍑鸿繖浠借蛋**鍚屾**锛堣惤瀹屾墠绠楁暟锛夈€?        WriteBoardHistory(Doc.StrokesOf(0), sync: true);
        BoardHistory.CloseSession();      // 涓嬩竴娆¤繍琛屽彟璧蜂竴浠藉揩鐓?
        if (_uiInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_uiInputHwnd);
            _uiInputHwnd = IntPtr.Zero;
            _uiInputShown = false;
        }
        if (_pptInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_pptInputHwnd);
            _pptInputHwnd = IntPtr.Zero;
            _pptInputShown = false;
        }
        if (_timerInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_timerInputHwnd);
            _timerInputHwnd = IntPtr.Zero;
            _timerInputShown = false;
        }
        if (_rollInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_rollInputHwnd);
            _rollInputHwnd = IntPtr.Zero;
            _rollInputShown = false;
        }

        _windows.Clear();
        s_map.Clear();
        Cursors.DisposeAll();
        Gfx.Shutdown();
        // 鎵嬫祴鍙帮細閫€鍑烘椂鎶婃眹鎬诲啓鍑烘潵锛堥€愭潯鏁版嵁鍦ㄦ瘡鏉℃嫋鎷界粨鏉熸椂灏卞凡缁忚惤鐩樹簡锛夈€?        EraserTelemetry?.Close(Doc, NowMs);
        // D1 浜氬儚绱狅細閫€鍑烘椂缁欎竴琛岀粺璁★紝纭 `--himetric` 鍒板簳鏈夋病鏈夌湡鐨勬槧灏勪笂
        // 锛堣澶囦笉鎶?himetric / 鎷夸笉鍒拌澶囩煩褰㈡椂浼氶€愮偣閫€鍥炴暣鏁板儚绱狅紝涓嶈兘鍙湅妯箙锛夈€?        if (InputPrecision.UseHimetric)
            Console.WriteLine($"杈撳叆绮惧害缁熻: himetric 鏄犲皠 {InputPrecision.MappedPoints} 鐐?/ "
                              + $"閫€鍥炴暣鏁板儚绱?{InputPrecision.FallbackPoints} 鐐?);
        Console.WriteLine("shutdown complete");
    }

    // =====================================================================
    //  鐣岄潰鎺ュ叆锛堝紩鎿庤竟鐣岋級
    //
    //  寮曟搸璐熻矗锛氱瑪鐢?鏂囨。/杈撳叆/娓叉煋/宸ュ叿鐘舵€併€?    //  鐣岄潰璐熻矗锛氱敾鑷繁銆佽В閲婄偣鍑汇€?    //  涓よ竟鍞竴鐨勯€氶亾灏辨槸 IOverlayUi / IUiHost锛屽紩鎿庨噷娌℃湁浠讳綍鍏蜂綋鐣岄潰浠ｇ爜銆?    // =====================================================================

    /// <summary>
    /// 鎹㈢晫闈€備紶 null 鎴?<see cref="NullUi"/> 灏辨槸鏃犵晫闈紙绾瑪杩癸級銆?    /// 鐣岄潰鍙湪瑕嗙洊灞傚缓濂戒箣鍚庢帴鍏ワ紱杩愯涓崲鐣岄潰涔熸槸瀹夊叏鐨勩€?    /// </summary>
    /// <param name="keepScreen">
    /// 浼?true 琛ㄧず娌跨敤褰撳墠杩欎釜鐣岄潰宸茬粡绠楀ソ鐨勫睆骞曞昂瀵搞€傝嚜妫€閲?涓存椂鎹㈡帀鍐嶆崲鍥炴潵"
    /// 浼氱敤鍒板锛岄伩鍏嶇晫闈㈡寜寮曟搸鍐呴儴鐨勭獥鍙ｅ昂瀵搁噸鏂板竷灞€锛堥偅鎶婄晫闈㈡憜鍒板埆澶勫幓浜嗭級銆?    /// 姝ｅ紡浜у搧浠ｇ爜鐢ㄩ粯璁ゅ€煎嵆鍙€?    /// </param>
    public void SetUi(IOverlayUi ui, bool keepScreen = false)
    {
        Ui = ui ?? new NullUi();
        // 鎹簡鐣岄潰灏卞緱璁╁悇绐楀彛閲嶆柊甯冨眬锛氳鐩栫獥鍙ｇ紦瀛樼潃"涓婃缁?Layout 鐨勫睆骞?锛?        // 涓嶆竻鐨勮瘽鏂扮晫闈㈡案杩滅瓑涓嶅埌 Layout锛孮ueryBounds 浼氫竴鐩磋繑鍥炵┖锛堢湅涓嶈涔熺偣涓嶅埌锛夈€?        foreach (var w in _windows) w.InvalidateUiLayout();
        _uiFaults = 0;
        _uiHover = false;
        // 鐣岄潰鐪嬪埌鐨勫睆骞曞潗鏍囨槸閫昏緫鍍忕礌锛氬畠鎸夎嚜宸辩殑閫昏緫灏哄甯冨眬锛屽紩鎿庤礋璐ｆ寜 DPI脳鐣岄潰缂╂斁 鏀惧ぇ銆?        Host = new UiHost(this, UiLogicalScreen, DpiScale, keepScreen);
        Ui.Attach(Host);
        InvalidateUi();
        NotifyUiStateChanged();
    }

    /// <summary>褰撳墠鎺ュ叆鐨勭晫闈紙璇婃柇鐢級銆?/summary>
    public IOverlayUi CurrentUi => Ui;

    /// <summary>铏氭嫙妗岄潰鑼冨洿锛岀晫闈㈡嵁姝ゅ喅瀹氭偓娴潯鏀惧湪鍝€?/summary>
    public RectF VirtualScreen => new()
    {
        MinX = _virtualX,
        MinY = _virtualY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY + _virtualH,
    };

    /// <summary>铏氭嫙妗岄潰鐨?*閫昏緫**鑼冨洿锛堥櫎浠?DPI 缂╂斁锛夛紝鐣岄潰甯冨眬鐢ㄨ繖涓€?/summary>
    public RectF LogicalVirtualScreen => new()
    {
        MinX = _virtualX / DpiScale,
        MinY = _virtualY / DpiScale,
        MaxX = (_virtualX + _virtualW) / DpiScale,
        MaxY = (_virtualY + _virtualH) / DpiScale,
    };

    /// <summary>
    /// 鐣岄潰鑷繁鐨?*閫昏緫灞忓箷**锛氶€昏緫妗岄潰鍐嶉櫎浠?UiScale銆?    /// 鐣岄潰鏀惧ぇ鍚?1 涓?鐣岄潰閫昏緫鍗曚綅"鍗犳洿澶氱墿鐞嗗儚绱狅紝瀹冭兘鎽嗙殑鍦版柟鑷劧鍙樺皬鈥斺€?    /// 杩欐槸"鍙箻涓€涓?UiScale"涔嬪蹇呴』鍚屾椂鍋氱殑涓€浠朵簨锛屼笉鐒?1.3 鍊嶄笅宸ュ叿鏉′細琚帹鍒板睆骞曞銆?    /// </summary>
    public RectF UiLogicalScreen => new()
    {
        MinX = LogicalVirtualScreen.MinX / UiScale,
        MinY = LogicalVirtualScreen.MinY / UiScale,
        MaxX = LogicalVirtualScreen.MaxX / UiScale,
        MaxY = LogicalVirtualScreen.MaxY / UiScale,
    };

    /// <summary>涓诲睆宸ヤ綔鍖虹殑"鐣岄潰閫昏緫"鐗堬紙鍙ｅ緞鍚?<see cref="UiLogicalScreen"/>锛夈€?/summary>
    internal RectF UiLogicalWorkArea
    {
        get
        {
            var w = LogicalPrimaryWorkArea;
            return new RectF
            {
                MinX = w.MinX / UiScale, MinY = w.MinY / UiScale,
                MaxX = w.MaxX / UiScale, MaxY = w.MaxY / UiScale,
            };
        }
    }

    /// <summary>
    /// 璁剧疆椤点€岀晫闈㈠ぇ灏忋€嶆崲妗ｏ紙鍚搁檮鍚庣珛鍒荤敓鏁堬紱钀界洏鐢辩晫闈㈣礋璐ｏ紝瑙?FullUi.ActivateRow锛夈€?    /// 鐣岄潰灏哄浣撶郴涓€鍙橈細鍚勭獥鍙ｇ殑甯冨眬缂撳瓨浣滃簾銆佸涓绘嬁鏂扮殑"閫昏緫灞忓箷"閲嶇畻銆佹暣甯ч噸鐢汇€?    /// </summary>
    internal void SetUiScaleFromUi(float scale)
    {
        scale = UiScalePresets.Snap(scale);
        if (MathF.Abs(scale - UiScale) < 0.001f) return;
        UiScale = scale;
        foreach (var w in _windows) w.InvalidateUiLayout();
        Host?.UpdateScreen(UiLogicalScreen);
        Host?.UpdateWorkArea(UiLogicalWorkArea);
        InvalidateUi();
        _dirty = true;
    }

    /// <summary>
    /// **涓诲睆鐨勫伐浣滃尯**锛堢墿鐞嗗儚绱狅級锛氬睆骞曞噺鎺変换鍔℃爮涔嬪悗鍓╀笅鐨勯偅鍧楃煩褰€?    ///
    /// 涓轰粈涔堢晫闈㈤渶瑕佸畠锛堢敤鎴?2026-09-27锛夛細鎮诞鏉＄殑**榛樿浣嶇疆**瑕?绱ц创浠诲姟鏍忎笂鏂广€?    /// 涓よ€呬笉閲嶅彔"銆傛寜鏁翠釜灞忓箷绠楀仛涓嶅埌鈥斺€旇鐩栧眰鏄疆椤剁殑锛屼换鍔℃爮鎸′笉浣忓畠锛屼簬鏄创搴曢偅涓€鏉?    /// 浼?*鍘嬪湪浠诲姟鏍忎笂**锛堣€岄潰鏉跨煩褰㈡槸"鎴戜滑鐨勫湴鐩?锛岄偅涓€鍧楃殑鐐瑰嚮涔熶竴骞惰鍚冩帀锛夈€?    /// 宸ヤ綔鍖鸿繖涓暟澶╃劧鎶婁换鍔℃爮鎵ｆ帀浜嗭紝浠诲姟鏍忓湪搴?宸?涓?鍙抽兘鎴愮珛銆?    ///
    /// 璇讳笉鍒帮紙鑰佺郴缁?/ 鍚堟垚鐜锛夊氨閫€鍥炶櫄鎷熸闈⑩€斺€?*瀹佸彲鎸?娌℃湁浠诲姟鏍?绠楋紝涔熶笉鑳界粰绌虹煩褰?*锛?    /// 缁欎簡绌虹煩褰紝鐣岄潰浼氭妸瀹冨綋"灞忓箷鏄?0脳0"浠庤€屾妸闈㈡澘澶瑰埌宸︿笂瑙掋€?    ///
    /// 鈿?澶氬睆鏃剁粰鐨勬槸**涓诲睆**锛堜笉鏄?闈㈡澘褰撳墠鎵€鍦ㄩ偅鍧楀睆"锛夛細鐢ㄦ埛 2026-09-27 閫夌殑銆?    /// 浣嶇疆鏈潵灏变笉璁扮洏锛屾墍浠?姣忔閮藉洖涓诲睆"鏄彲棰勬祴鐨勯偅涓€妗ｃ€?    /// </summary>
    public RectF PrimaryWorkArea
    {
        get
        {
            var r = default(Native.RECT);
            if (Native.SystemParametersInfoRect(Native.SPI_GETWORKAREA, 0, ref r, 0)
                && r.Width > 0 && r.Height > 0)
                return new RectF { MinX = r.Left, MinY = r.Top, MaxX = r.Right, MaxY = r.Bottom };
            return VirtualScreen;
        }
    }

    /// <summary>涓诲睆宸ヤ綔鍖虹殑**閫昏緫**鑼冨洿锛堥櫎浠?DPI锛夛紝鐣岄潰绠?榛樿浣嶇疆"鐢ㄥ畠銆?/summary>
    public RectF LogicalPrimaryWorkArea
    {
        get
        {
            var w = PrimaryWorkArea;
            return new RectF
            {
                MinX = w.MinX / DpiScale, MinY = w.MinY / DpiScale,
                MaxX = w.MaxX / DpiScale, MaxY = w.MaxY / DpiScale,
            };
        }
    }

    /// <summary>
    /// 鐣岄潰澹版槑"澶栬鍙樹簡锛岃閲嶇敾鎴戠殑缂撳瓨"銆傚彲浠ョ敱鐣岄潰鐨勪换鎰忕嚎绋嬭皟鐢紝
    /// 涓荤嚎绋嬪湪涓嬩竴甯ф秷璐广€?*涓嶈姣忓抚璋?*锛岄偅绛変簬姣忓抚閲嶇敾鏁翠釜鐣岄潰銆?    /// </summary>
    public void InvalidateUi()
    {
        UiInvalidatePending = true;
        _uiInvalidateSeq++;
        _dirty = true;
    }

    /// <summary>
    /// 鐣岄潰鐨勯噸鐢昏姹傛祦姘村彿銆傛覆鏌撳惊鐜嬁瀹冨垽"娓叉煋鏈熼棿鐣岄潰鏄笉鏄張鎻愪簡璇锋眰"锛?    /// 浠庤€?*涓嶆姽鎺?*閭ｄ竴甯ц姹傦紙瑙?Loop 閲岄偅娈电殑娉ㄩ噴锛夈€?    /// </summary>
    private long _uiInvalidateSeq;

    internal UiState SnapshotState() => new()
    {
        Tool = Tool,
        // 鏀炬槧涓細鐣岄潰鐨?杩涙斁鏄犲氨鎶婇潰鏉垮睍寮€ + 褰掍綅"闈犲畠锛堝彧鍦ㄨ竟娌跨敤涓€娆★紝瑙?UiState.PptMode锛夈€?        PptMode = PptMode,
        ParabolaAxis = ParabolaAxis,      // 鐣岄潰鎷垮畠鎶婂浘褰㈤潰鏉块偅涓€鏍肩殑鍥炬爣杞垚褰撳墠鏈濆悜
        LineDash = LineDash,              // 鐣岄潰鎷垮畠鎶娿€岀洿绾裤€嶉偅涓€鏍肩殑鍥炬爣鎹㈡垚褰撳墠绾垮瀷
        PrismSides = _sidesPrism,         // 鐣岄潰鎷垮畠鎶娿€屾１鏌便€嶉偅涓€鏍肩殑鍥炬爣鎹㈡垚褰撳墠妗ｏ紙锛嬫。浣嶇偣锛?        PyramidSides = _sidesPyramid,     // 妫遍敟 / 妫卞彴鍚勮鍚勭殑妗ｏ紙涓夋牸鐨勬。浣嶇偣浜掍笉褰卞搷锛?        FrustumSides = _sidesFrustum,
        SolidMinSides = Stroke.MinPrismSides,   // 妗ｄ綅鑼冨洿涔熸帹涓婂幓锛堢晫闈㈡墠鐭ラ亾鐐瑰嚑涓偣锛?        SolidMaxSides = Stroke.MaxPrismSides,
        // 鍥惧舰闈㈡澘閲岄偅涓ゆ牸"鍐嶇偣涓€娆℃崲涓€妗?鐨勫綋鍓嶆。锛堢晫闈㈡嬁瀹冪敾鍥炬爣 + 妗ｄ綅鐐癸級锛?        // 鍙屾洸绾跨殑娓愯繎绾匡紙鏈?/ 鏃狅級銆佹き鍦嗭紙甯︾劍鐐癸級鐨勭劍鐐逛笁瑙掑舰锛堟湁 / 鏃狅級銆?        HyperbolaAsymptotes = HyperbolaAsymptotes,
        EllipseFocusTriangle = EllipseFocusTriangle,
        Color = Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
        PaletteBase = Tool == Tool.Highlighter
            ? new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)
            : CurrentColor,
        Width = CurrentToolWidthLogical,
        // 鎸夊伐鍏峰垎寮€缁欙細鍙粰涓€涓?Width 鐨勮瘽锛屾粦鍧楀湪鍒囧伐鍏锋椂浼氳烦锛堜笁涓伐鍏峰悇璁板悇鐨勫搴︼級銆?        PenWidth = PenWidthLogical,
        HighlighterWidth = HighlighterWidthLogical,
        HighlighterColor = HighlighterCurrent,
        LaserWidth = LaserWidthLogical,
        Dash = PenDash,
        PassThrough = PassThrough,
        Board = BoardOn,
        BoardColor = BoardColor,
        BoardPattern = BoardPattern,
        BoardPatternStep = BoardPatternStepLogical,
        BoardOpacity = BoardOpacity,
        CaptureHideInk = CaptureHideInk,
        SelectMode = SelMode,
        CoordGridDefault = CoordGridDefault,
        DwellShapeOn = DwellShapeEnabled,
        PressureOn = PressureWidth.Enabled,      // 鐣岄潰鎷垮畠鏄剧ず銆岃缃?鈫?涔﹀啓 鈫?鍘嬫劅绮楃粏銆嶉偅涓紑鍏?        FineStrokeOn = RawInputCapture,          // 銆岃缃?鈫?涔﹀啓 鈫?绮剧粏绗旇抗銆嶏紙鍘熷杈撳叆琛ョ偣锛夋€诲紑鍏?        PredictTailOn = PredictTailEnabled,      // 銆岃缃?鈫?涔﹀啓 鈫?澧ㄨ抗棰勬祴銆嶏紙鑷粯棰勬祴灏撅級鎬诲紑鍏筹紙榛樿寮€锛?026-10-14 娴嬭瘯鐗堣捣锛?        WetMode = WetMode,                       // 銆岃缃?鈫?澧ㄨ抗 鈫?婀垮ⅷ妗ｄ綅銆嶏紙0..3锛涚晫闈㈡樉绀哄綋鍓嶆。锛?        HistoryCount = BoardHistory.Count,       // 銆屾洿澶?鈫?澧ㄨ抗 鈫?鍘嗗彶鏉夸功銆嶉偅涓€鏍肩殑浜?鐏?        TouchGesturesOn = _touch.Enabled,        // 銆岃缃?鈫?涔﹀啓 鈫?瑙︽懜鎵嬪娍銆嶆€诲紑鍏筹紙榛樿寮€锛?        // [鍒犻櫎 2026-10-05] PredictOn锛堝ⅷ杩归娴嬶級锛氶殢鑰侀娴嬬郴缁熺Щ闄ゃ€?        ScreenIndex = ScreenIndex,
        CanFlipPageUp = CanFlipPageUp,
        IsDrawing = _drawing,
        UndoDepth = Doc.UndoDepth,
        RedoDepth = Doc.RedoDepth,
        StrokeCount = Doc.Strokes.Count,
        UpdateStage = UpdateState,
        UpdateText = UpdateText,
        InkStatus = InkStatus,                  // 鐣岄潰銆屽ⅷ杩广€嶉〉鐨勭姸鎬佽
        DocOpen = DocView.IsOpen,
        DocInfo = DocView.IsOpen ? $"{DocView.Title} 路 {DocView.Count} 椤? : "",
        ReplayActive = ReplayActive,
        ReplayPlaying = ReplayPlaying,
        ReplaySpeed = ReplaySpeed,
        // 銆岃鍫傘€嶉〉锛氳鏃跺櫒鐘舵€?+ 鐐瑰悕鍚嶅崟锛堢偣鍚嶅叏鍦ㄧ晫闈㈠眰鍋氾紝寮曟搸鍙鐩?鎺ㄧ姸鎬侊級
        TimerActive = TimerActive,
        TimerPaused = TimerPaused,
        TimerFinished = TimerFinished,
        TimerMode = TimerKind,
        TimerValueMs = TimerValueMs,
        TimerCardOpen = TimerCardOpen,
        TimerSettingsOpen = TimerSettingsOpen,
        TimerExpanded = TimerExpanded,
        RollCardOpen = RollCardOpen,
        RollSettingsOpen = RollSettingsOpen,
        Names = Names,
    };

    private void NotifyUiStateChanged()
    {
        if (Ui == null) return;
        var snapshot = SnapshotState();
        UiGuard("OnStateChanged", () => Ui.OnStateChanged(snapshot));
    }

    internal void SetToolFromUi(Tool tool)
    {
        ExitReplayForEdit("鎹㈠伐鍏?);
        SwitchTool(tool);
        EraserTelemetry?.Note($"鐣岄潰鎹㈠伐鍏?鈫?{ToolName(tool)}", NowMs);
        ApplyCursor();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>闈㈡澘鐐规鐨牸锛氬垏鍥?*涓婃鐢ㄧ殑閭ｄ竴绉嶆鐨舰鎬?*锛堟暣绗?闈㈢Н锛夛紝椤烘墜鍏崇┛閫忋€?    /// [2026-10-05 鐢ㄦ埛瀹歖 姗＄毊瀛愮被鍨嬩笉鍐嶇敱"鍐嶇偣涓€娆?鍒囨崲锛岄潰鏉胯繖涓€鏍煎浐瀹氳繘姗＄毊銆佺被鍨嬬湅涓婂甫銆?/summary>
    internal void SetEraserPreferredFromUi()
    {
        ExitReplayForEdit("鎹㈠伐鍏?);
        SwitchTool(_eraserKind);
        ApplyCursor();
        _dirty = true;
        NotifyUiStateChanged();
    }

    // ---- 鑷閽╁瓙锛堟祴璇曡椹卞姩"鎹㈠伐鍏?鎿嶄綔鏉℃寜閽?鎷栧姩涓Щ鍔?杩欎簺绉佹湁璺緞锛?---

    /// <summary>鑷鐢細鎵ц涓€涓敭浣嶅姩浣滐紙鎹㈠伐鍏枫€佹挙閿€鈥︹€﹂兘浠庡悓涓€涓叆鍙ｈ繘锛夈€?/summary>
    internal void RunActionForTest(KeyAction a) => RunAction(a);

    /// <summary>鑷鐢細鐐规搷浣滄潯绗?index 涓寜閽€?/summary>
    internal void RunBarActionForTest(int index)
        => RunBarAction(index, SelectionHandles.FrameOf(Doc.Selected),
                        SelectionHandles.FrameOf(Doc.Selected).CanvasAabb);

    /// <summary>鑷鐢細鎷栧姩涓Щ鍔ㄦ寚閽堬紙閫夋嫨鎵嬪娍锛夈€?/summary>
    internal void UpdateSelectionGestureForTest(float x, float y) => UpdateSelDrag(x, y);

    internal void SetColorFromUi(Color4 color)
    {
        // 鑽у厜绗旂敤鍚屼竴缁勫熀鑹诧紝鍙槸闄嶄綆涓嶉€忔槑搴︹€斺€旇€佸笀鐪嬪埌鐨勮壊鐩告槸涓€鑷寸殑銆?        if (Tool == Tool.Highlighter)
            HighlighterCurrent = InkPalette.ToHighlighter(color);
        else
            CurrentColor = color;
        // **璁颁綇杩欎釜棰滆壊**锛堢敤鎴?2026-09-30 瀹氾細姣忎釜宸ュ叿鍚勮涓€涓壊锛岄噸鍚洖鏉ヨ繕鏄畠锛夈€?        // 瀛樼殑鏄?*鑹插甫閲岀殑搴忓彿**鑰屼笉鏄?RGB锛氳壊甯︿互鍚庡井璋冮鑹插€硷紝瀛樻。涔熶笉鐢ㄨ窡鐫€杩併€?        var band = Tool == Tool.Highlighter ? InkPalette.HighlighterBand : InkPalette.PenBand;
        for (int i = 0; i < band.Length; i++)
            if (CloseColor(band[i].Color, color))
            {
                if (Tool == Tool.Highlighter) _hlColorIdx = i; else _penColorIdx = i;
                SetUiPref(Tool == Tool.Highlighter ? "hlColor" : "penColor", i.ToString());
                break;
            }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **鏂扮敾鐨勫潗鏍囩郴瑕佷笉瑕佺綉鏍?*锛堢敤鎴峰湪銆屾洿澶氥€嶆娊灞夐噷寮€鍏筹紝瑙?FullUi 鐨?鍧愭爣绯荤綉鏍?閭ｄ竴琛岋級銆?    ///
    /// 瀹冨彧鏄?鐢荤殑鏃跺€欏彇鍝釜鍊?锛氭瘡涓潗鏍囩郴鑷繁瀛樹簡涓€浠斤紙瑙?<see cref="Stroke.Grid"/>锛夛紝
    /// 鎵€浠ユ敼杩欎釜寮€鍏?*涓嶄細**鍔ㄥ凡缁忕敾鍦ㄦ澘涓婄殑鍧愭爣绯烩€斺€旈偅浜涜閫変腑涔嬪悗鍗曠嫭鏀?    /// 锛?see cref="ToggleSelectionGrid"/>锛夈€傜悊鐢辫 Stroke.Grid 鐨勬敞閲婏細
    /// 瀵硅薄瑕佽嚜鍖呭惈锛屼笉鑳借瀹冮暱澶т互鍚庤繕鍙椾竴涓叏灞€寮€鍏虫憜甯冦€?    ///
    /// 榛樿**鍏?*锛氶粦鏉夸笂鐨勫潗鏍囩郴鏈潵灏辨槸鍏夌绉冧竴涓崄瀛楋紝鏍煎瓙鏄鐨勬椂鍊欐墠瑕併€?    /// </summary>
    internal bool CoordGridDefault;

    /// <summary>
    /// 銆屾洿澶氥€嶆娊灞夐噷閭ｄ竴琛?鍧愭爣绯荤綉鏍?琚偣浜嗕竴涓嬨€?    ///
    /// **閫変腑浜嗗潗鏍囩郴灏辨敼瀹冧滑锛屾病閫変腑灏辨敼"浠ュ悗鏂扮敾鐨勯粯璁ゅ€?**锛?    /// 鑰佸笀鐨勯鏈熸槸"鎴戠偣杩欎竴涓嬫槸鍐茬潃鐪煎墠杩欎釜涓滆タ鏉ョ殑"锛岃€屾娊灞夐噷娌℃湁"鎿嶄綔璋?鐨勬蹇碉紝
    /// 浜庢槸鐢?褰撳墠鏈夋病鏈夐€変腑鍧愭爣绯?鏉ュ垎娲锯€斺€斾竴娆＄偣鍑诲彧骞蹭竴浠朵簨銆?    /// 杩斿洖鏀逛簡鍑犱釜瀵硅薄锛? = 鏀圭殑鏄柊鐢荤殑榛樿鍊硷級銆?    /// </summary>
    internal int ToggleSelectionGrid()
    {
        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected)
            if (s.Kind == StrokeKind.Coordinate) targets.Add(s);   // 鏁拌酱娌℃湁缃戞牸

        if (targets.Count == 0)
        {
            CoordGridDefault = !CoordGridDefault;
            Console.WriteLine($"鍧愭爣绯荤綉鏍硷紙鏂扮敾鐨勯粯璁ゅ€硷級锛歿(CoordGridDefault ? "寮€" : "鍏?)}");
            _dirty = true;
            NotifyUiStateChanged();
            return 0;
        }

        // 涓€鎵归噷"鏈夋牸 / 娌℃牸"娣风潃鏃讹紝鎸?鍙杩樻湁娌℃牸鐨勶紝灏卞叏缁欏紑涓?鏉ュ畾
        // 鈥斺€斿拰閿佸畾閭ｄ竴鏉″悓涓€涓彛寰勶紙鏈夋病閿佺殑灏卞叏閿佷笂锛夈€?        bool anyOff = false;
        foreach (var s in targets) if (!s.Grid) { anyOff = true; break; }
        Doc.ApplyGrid(targets, anyOff);
        Console.WriteLine($"鍧愭爣绯荤綉鏍硷細{targets.Count} 涓璞?鈫?{(anyOff ? "寮€" : "鍏?)}");
        _dirty = true;
        return targets.Count;
    }

    /// <summary>鐣岄潰鍚姩鏃舵妸"鍧愭爣绯荤綉鏍?鐨勫亸濂芥帹杩囨潵锛堣 FullUi.LoadPrefs锛夈€?/summary>
    internal void SetCoordGridDefaultFromUi(bool on)
    {
        if (CoordGridDefault == on) return;
        CoordGridDefault = on;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 銆屾洿澶氥€嶆娊灞夐噷閭ｄ竴琛?鍋滈】鎴愬瀷"琚偣浜嗕竴涓?/ 鐣岄潰鍚姩鏃舵妸鍋忓ソ鎺ㄨ繃鏉?    ///锛堣 FullUi.LoadPrefs锛夈€傝鏍艰 璁″垝-鍥惧舰宸ュ叿.md 搂鍥涘崄浜屻€?    ///
    /// 瀹冨彧绠?浠ュ悗鐢荤殑閭ｄ簺鍙備笉鍙備笌"锛?*涓嶅姩宸茬粡鐢诲湪鏉夸笂鐨勪换浣曚笢瑗?*鈥斺€?    /// 鍙樺嚭鏉ョ殑鍥惧舰鏄櫘閫氬璞★紝寮€鍏冲叧鎺変笉浼氭妸瀹冧滑鍙樺洖鎵嬬粯锛堟挙閿€鎵嶆槸閭ｆ潯璺級銆?    /// </summary>
    internal void SetDwellShapeFromUi(bool on)
    {
        if (DwellShapeEnabled == on) return;
        DwellShapeEnabled = on;
        Console.WriteLine($"鍋滈】鎴愬瀷锛歿(on ? "寮€锛堝仠涓€涓嬪氨鎶婃墜缁樺彉鍥惧舰锛? : "鍏?)}");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 銆屾洿澶?鈫?璁剧疆 鈫?涔﹀啓 鈫?鍘嬫劅绮楃粏銆嶈鐐逛簡涓€涓嬶紙2026-10-01锛夈€?    /// 璇箟瑙?<see cref="IEngineCommands.SetPressure"/>锛?*娓叉煋鏈?*寮€鍏筹紝鏂囨。涓€涓瓧鑺備笉鍔ㄣ€?    ///
    /// 鈿?蹇呴』 `Doc.InvalidateAll()`锛氱瑪杩规槸鎸夊潡**鐑樿繘鍐呭灞傜紦瀛?*鐨勶紝鍙爣涓€涓剰鍖虹殑璇?    /// 灞忓箷涓婅繕鏄棫绮楃粏锛?寮€鍏崇偣浜嗘病鍙嶅簲"鏈€鍏稿瀷鐨勪竴绉嶏級銆傛暣灞備綔搴熸渶璐典篃灏遍噸閾轰竴灞忋€?    /// </summary>
    internal void SetPressureFromUi(bool on)
    {
        if (PressureWidth.Enabled == on) return;
        PressureWidth.Enabled = on;
        Doc.InvalidateAll();
        _dirty = true;
        Console.WriteLine($"鍘嬫劅绮楃粏锛歿(on ? "寮€锛堟寜鍘嬪姏鏀圭矖缁嗭級" : "鍏筹紙鎵€鏈夌瑪杩圭瓑瀹斤紝鎵嬪啓鏉跨収鏍锋祦鐣咃級")}");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 瑙︽懜鎵嬪娍**鎬诲紑鍏?*锛堛€屾洿澶?鈫?璁剧疆 鈫?涔﹀啓 鈫?瑙︽懜鎵嬪娍銆嶏紝2026-10-05 鍔狅級銆?    /// 鍏虫帀 = 鍙墿鍗曟寚涔﹀啓锛氬弻鎸囨墜鍔?/ 鈮? 鎸囨摝 / 闀挎寜閫夋嫨 / 涓ゆ寚鐐归€?/ 鍗曟寚婕父鍏ㄩ儴鍋滅敤
    /// 锛堥椄闂ㄥ湪 `TouchGestures.Enabled` 鐨勬瘡鏉″垽瀹氶噷锛夈€傛鍦ㄨ窇鐨勬墜鍔垮氨鍦颁腑鏂€?    /// </summary>
    internal void SetTouchGesturesFromUi(bool on)
    {
        if (_touch.Enabled == on) return;
        _touch.Enabled = on;
        if (!on) TouchAbort();
        Console.WriteLine($"瑙︽懜鎵嬪娍锛歿(on ? "寮€锛堝崟鎸囧啓 / 鍙屾寚鎵嬪娍 / 涓夋寚鎿?/ 闀挎寜閫夋嫨锛? : "鍏筹紙鍙墿鍗曟寚涔﹀啓锛?)}");
        NotifyUiStateChanged();
    }

    /// <summary>鍛戒护琛屼笂鏈夋病鏈?`--nopressure`锛堢粰瀵圭収瀹為獙鐢紝瀹冧紭鍏堜簬鐢ㄦ埛鍋忓ソ锛夈€?/summary>
    private bool _noPressureArg;

    /// <summary>鍘嬫劅绮楃粏鐨勫亸濂介敭锛堝彧鍐?鍏宠繃"鐨勯偅涓€浠斤級銆?/summary>
    private const string PressurePrefKey = "pressure";

    /// <summary>绮剧畝绗旇抗锛堝師濮嬭緭鍏ヨˉ鐐癸級鐨勫亸濂介敭锛堝悓鏍峰彧鍐?鍏宠繃"鐨勯偅涓€浠斤級銆?/summary>
    private const string FineStrokePrefKey = "finestroke";

    /// <summary>銆屽ⅷ杩归娴嬨€嶏紙鑷粯棰勬祴灏撅級鐨勫亸濂介敭锛?*榛樿寮€**锛屽彧鍐?鍏宠繃"鐨勯偅涓€浠斤級銆?/summary>
    private const string PredictPrefKey = "predict2";

    /// <summary>鍛戒护琛屼笂鏈夋病鏈?`--nopredict2` / `--predict2`锛堝鐓у疄楠岀敤锛屼紭鍏堜簬鐢ㄦ埛鍋忓ソ锛夈€?/summary>
    private bool _noPredictArg, _forcePredictArg;

    /// <summary>鍛戒护琛屼笂鏈夋病鏈?`--norawinput`锛堢粰瀵圭収瀹為獙鐢紝瀹冧紭鍏堜簬鐢ㄦ埛鍋忓ソ锛夈€?/summary>
    private bool _noRawInputArg;

    /// <summary>
    /// 銆屾洿澶?鈫?璁剧疆 鈫?涔﹀啓 鈫?绮剧粏绗旇抗銆嶈鐐逛簡涓€涓嬶紙2026-10-07锛夈€?    ///
    /// 鍏虫帀 = 涓嶅啀鎸夊師濮嬫姤鐜囪ˉ涓棿鐐癸細**鐪佷竴鐐规€ц兘**锛堜綆閰嶆満鍙叧锛夛紝
    /// 浠ｄ环鏄啓蹇椂绾挎潯鐨勭粏鑺傚皯涓€浜涖€?*鍙奖鍝嶄互鍚庡啓鐨?*锛屽凡缁忕敾濂界殑涓€涓瓧鑺備笉鍔ㄣ€?    /// 姝ｅ湪鍐欑殑閭ｄ竴绗斾細**绔嬪埢娉ㄩ攢**鍘熷杈撳叆锛堣 <see cref="SetRawCapture"/>锛?    /// 绌洪棽鏃跺繀椤绘敞閿€锛屽惁鍒欎細閫€鍥?绌洪棽涔熸敹榧犳爣绌烘姤"閭ｄ釜 46fps 鐨勮€侀棶棰橈級銆?    /// </summary>
    internal void SetFineStrokeFromUi(bool on)
    {
        if (RawInputCapture == on) return;
        RawInputCapture = on;
        if (!on) SetRawCapture(false);
        Console.WriteLine($"绮剧粏绗旇抗锛歿(on ? "寮€锛堟寜璁惧鐨勫師濮嬫姤鐜囪ˉ涓棿鐐癸級" : "鍏筹紙涓嶅啀琛ョ偣锛岀渷涓€鐐规€ц兘锛?)}");
        NotifyUiStateChanged();
    }

    /// <summary>鍚姩鏃跺簲鐢?绮剧粏绗旇抗"锛堣嚜妫€瑕佸崟鐙皟涓€娆★紝鐞嗙敱鍚屽帇鎰燂級銆?    /// 寮€鍏冲凡鎾わ紙甯稿紑锛夆€斺€旇繖閲屽彧鏄噸鏂版柇瑷€"甯稿紑"锛屼粛鍙湁 `--norawinput` 鑳藉叧銆?/summary>
    internal void ApplyFineStrokePrefForTest()
        => RawInputCapture = !_noRawInputArg;

    /// <summary>
    /// 銆屾洿澶?鈫?璁剧疆 鈫?涔﹀啓 鈫?澧ㄨ抗棰勬祴銆嶈鐐逛簡涓€涓嬶紙2026-10-09锛夆€斺€?*B4 鑷粯棰勬祴灏剧殑鎬诲紑鍏?*銆?    ///
    /// 鍏虫帀 = 涓嶅啀鐢婚娴嬪熬锛堢瑪杩规湯绔洿鑰佸疄锛夛紱寮€/鍏抽兘椤烘墜澶嶄綅棰勬祴鍣紙鍏斥啋绔嬪埢鎾ゅ熬锛?    /// 寮€鈫掍粠骞插噣鐘舵€佽捣姝ワ紝绛変笅涓€绗旈噸鏂板杺鐐癸級銆傚彧褰卞搷浠ュ悗鐢荤殑锛屽凡钀界瑪鐨勫ⅷ涓€涓瓧鑺備笉鍔ㄣ€?    /// </summary>
    internal void SetPredictFromUi(bool on)
    {
        if (PredictTailEnabled == on) return;
        PredictTailEnabled = on;
        ResetPredictTail();
        Console.WriteLine($"澧ㄨ抗棰勬祴锛歿(on ? "寮€锛堣嚜缁橀娴嬪熬锛? : "鍏筹紙涓嶇敾棰勬祴灏撅級")}");
        NotifyUiStateChanged();
    }

    /// <summary>鍚姩鏃跺簲鐢?澧ㄨ抗棰勬祴"鍋忓ソ锛堣嚜妫€瑕佸崟鐙皟涓€娆★紝鐞嗙敱鍚屽帇鎰?/ 绮剧粏绗旇抗锛夈€?/summary>
    internal void ApplyPredictPrefForTest() => ApplyPredictTailSetting();

    /// <summary>**榛樿寮€**锛?026-10-14 鍙戝竷娴嬭瘯鐗堣捣锛?026-10-09 鏇惧畾榛樿鍏筹級锛涘懡浠よ涓哄鐓у疄楠屼紭鍏?    /// 锛坄--nopredict2` 鍏?/ `--predict2` 寮€锛夛紝鍚﹀垯鍚亸濂斤紙鍋忓ソ鍐欒繃 "0" 鎵嶅叧锛夈€?/summary>
    private void ApplyPredictTailSetting()
        => PredictTailEnabled = !_noPredictArg
           && (_forcePredictArg || GetUiPref(PredictPrefKey) != "0");

    /// <summary>
    /// 鑷鐢細鎶?鍘嬫劅绮楃粏"鐨勫亸濂?*閲嶆柊搴旂敤涓€娆?*鈥斺€旀ā鎷?閲嶅紑杞欢"閲岃鍋忓ソ閭ｄ竴姝ャ€?    /// 鑷妯″紡鍚姩鏃舵牴鏈笉璇荤洏锛堣 LoadUiPrefs 閭ｆ锛夛紝鎵€浠ュ亸濂藉線杩斿繀椤婚潬杩欎竴鏉¤ˉ涓娿€?    /// </summary>
    internal void ApplyPressurePrefForTest()
        => PressureWidth.Enabled = !_noPressureArg && GetUiPref(PressurePrefKey) != "0";



    /// <summary>
    /// 婊戞潯/鐐规。浼犺繘鏉ョ殑鐩爣绮楃粏锛堥€昏緫鍍忕礌锛夈€?*2026-10-09 鏅氳捣锛氫竴寰嬪惛闄勫埌"鏈€杩戠殑涓€妗?**鈥斺€?    /// 婊戞潯鎷栧姩涓嶅啀浜х敓涓棿鍊硷紙1 鍙峰拰 2 鍙蜂箣闂存病鏈?1.3 杩欑锛夛紝鎷垮埌鐨勫氨鏄。琛ㄩ噷鐨勬暟銆?    /// 妗ｈ〃瑙?WidthPresets / HighlighterWidthPresets / LaserWidthPresets /
    /// EraserRadiusPresets / PixelEraserWidthPresets銆?    /// </summary>
    internal void SetWidthFromUi(float logicalPx)
    {
        // 澶栧眰鐨?0.5锝?4 鍙槸"鍒妸鏄庢樉绂昏氨鐨勫€兼斁杩涙潵"鐨勫厹搴曪紱**鐪熸鐨勮寖鍥存寜宸ュ叿绠?*銆?        // 杩欎釜 64 鏇剧粡鎶婇潰绉鐨崱浣忚繃锛氬畠鐨勬í杈硅鑳藉埌 160锛堜竴鍧楀ぇ姗＄毊锛夛紝
        // 琚灞傚す鍦?64 涔嬪悗锛?鎷栧埌鏈€鍙?鍙兘鍒?64锛?-paneltest 褰撳満鎶撳埌锛夈€?        float v = Math.Clamp(logicalPx, 0.5f, PixelEraserMaxWidth);

        // **鎸夊綋鍓嶅伐鍏疯矾鐢?*銆備互鍓嶅彧鏈?鑽у厜绗?/ 婵€鍏?/ 鍏跺畠"涓夋敮锛?        // 涓ょ姗＄毊鍏ㄨ惤杩?鍏跺畠"鈫?鏀圭殑鏄瑪瀹斤紙鐣岄潰涓婄殑姗＄毊婊戞潯鏄釜鎽嗚锛夈€?        switch (Tool)
        {
            case Tool.Highlighter:
            {
                int i = NearestIndex(HighlighterWidthPresets, v);
                HighlighterWidthIndex = i;
                HighlighterWidthLogical = HighlighterWidthPresets[i];
                SetUiPref("w2.hl", i.ToString());
                SetUiPref("wv.hl", HighlighterWidthLogical.ToString("0.##"));
                break;
            }

            case Tool.Laser:
            {
                int i = NearestIndex(LaserWidthPresets, v);
                LaserWidthIndex = i;
                LaserWidthLogical = LaserWidthPresets[i];
                SetUiPref("w2.laser", i.ToString());
                SetUiPref("wv.laser", LaserWidthLogical.ToString("0.##"));
                break;
            }

            case Tool.PixelEraser:
            {
                // 鍍忕礌姗＄毊鏀圭殑鏄?*閭ｄ竴鍧楁鐨殑妯竟**锛堥珮 = 妯竟 脳 榛勯噾姣旓級銆?                float cv = Math.Clamp(v, PixelEraserMinWidth, PixelEraserMaxWidth);
                int i = NearestIndex(PixelEraserWidthPresets, cv);
                PixelEraserWidthIndex = i;
                PixelEraserWidthLogical = PixelEraserWidthPresets[i];
                SetUiPref("w2.pixel", i.ToString());
                SetUiPref("wv.pixel", PixelEraserWidthLogical.ToString("0.##"));
                break;
            }

            case Tool.Eraser:
            {
                // 鏁寸瑪姗＄毊鏀圭殑鏄?*钀界偣鍗婂緞**锛堢鍒板摢鍎垮氨鍒犲摢涓€鏉★級锛涘浐瀹?5 妗ｃ€佸惛闄勩€?                float cv = Math.Clamp(v, EraserRadiusMin, EraserRadiusMax);
                int i = NearestIndex(EraserRadiusPresets, cv);
                EraserRadiusIndex = i;
                EraserRadiusLogical = EraserRadiusPresets[i];
                break;
            }

            default:
            {
                int i = NearestIndex(WidthPresets, v);
                WidthPresetIndex = i;
                PenWidthLogical = WidthPresets[i];
                SetUiPref("w3.pen", i.ToString());
                SetUiPref("wv.pen", PenWidthLogical.ToString("0.##"));
                break;
            }
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>绂?v 鏈€杩戠殑閭ｄ釜妗ｅ€硷紙妗ｈ〃閮介潪绌猴級銆?/summary>
    private static float Nearest(float[] table, float v)
    {
        float best = table[0], bestD = float.MaxValue;
        foreach (var x in table)
        {
            float d = MathF.Abs(x - v);
            if (d < bestD) { bestD = d; best = x; }
        }
        return best;
    }

    private static int NearestIndex(float[] table, float v) => Array.IndexOf(table, Nearest(table, v));

    /// <summary>杩佺Щ鑰侀厤缃殑涓€涓。浣嶄笅鏍囷細鑰佽〃鍙栧€?鈫?鍚搁檮杩涙柊琛紙瑙?LoadUiPrefs 鐨勮皟鐢ㄥ锛夈€?/summary>
    private bool MigrateWidthPref(string key, float[] oldTable, float[] newTable, ref int index, ref float value)
    {
        if (!int.TryParse(GetUiPref(key), out int oldIdx) || oldIdx < 0 || oldIdx >= oldTable.Length) return false;
        value = Nearest(newTable, oldTable[oldIdx]);
        index = NearestIndex(newTable, value);
        return true;
    }

    internal void UndoFromUi()
    {
        ExitReplayForEdit("鎾ら攢");
        Doc.Undo();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void RedoFromUi()
    {
        ExitReplayForEdit("閲嶅仛");
        Doc.Redo();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **銆屾竻闄ゅ叏閮ㄥⅷ杩广€?*锛?026-10-14 鐢ㄦ埛瀹氾細涓€涓姩浣溿€佷笁涓満鏅€佷竴涓悕瀛楋級锛?    ///   路 **PPT**锛氳繖浠芥紨绀烘枃绋?*鎵€鏈夐〉**鐨勬壒娉紙鍚洏涓婄紦瀛橈紱涓嶅姩妗岄潰鏉夸功銆佷笉鍔?鎾埌绗嚑椤?锛夛紱
    ///   路 **鏂囨。锛圥DF/鍥剧墖锛?*锛氳繖浠芥枃妗?*鎵€鏈夐〉**鐨勬壒娉紙鍚洏涓婄紦瀛橈紱涓嶅姩妗岄潰鏉夸功锛夛紱
    ///   路 **鐧芥澘 / 妗岄潰鎵规敞**锛氭暣寮犵敾甯冿紙鍚炕杩囩殑灞忥級銆?    /// 涓変釜鍦烘櫙閮?*鍙挙閿€**锛堜竴姝ュ洖鍒版竻绌哄墠锛夛紱涓や釜鍏ュ彛璋冨畠锛?    /// 姗＄毊甯﹂偅涓寜浣?0.8 绉掔殑鎸夐挳锛坄IEngineCommands.Clear`锛夊拰缈婚〉鏉¤彍鍗曢偅涓€椤广€?    /// </summary>
    internal void ClearAllInkFromUi()
    {
        ExitReplayForEdit("娓呴櫎鍏ㄩ儴澧ㄨ抗");
        if (PptMode)
        {
            ClearAllPptMarks();          // 鎵€鏈夐〉 + 杩炵洏鍒狅紱鍙挙閿€锛堣 ClearSlidesAction锛?        }
        else if (DocView.IsOpen)
        {
            ClearDocMarks();             // 鏁翠唤鏂囨。 + 杩炵洏鍒狅紱Doc.Clear() 鏈潵灏卞彲鎾ら攢
        }
        else
        {
            int n = Doc.Strokes.Count;
            Doc.Clear();
            Console.WriteLine(n > 0
                ? $"[鏉夸功] 宸叉竻闄ゅ叏閮ㄥⅷ杩癸紙{n} 涓璞★紱鍙挙閿€锛?
                : "[鏉夸功] 娓呴櫎鍏ㄩ儴澧ㄨ抗锛氭湰鏉ュ氨娌℃湁澧ㄨ抗");
        }
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void QuitFromUi() => _quit = true;

    // ---- 娴眰涓婚锛堥€変腑鎿嶄綔鏉?/ 灏忛潰鏉跨敤瀹冪敾锛?----------------------------

    /// <summary>
    /// 娴眰涓婚銆傜晫闈?`Attach` 鏃舵帹涓婃潵锛?see cref="IUiHost.SetFloatingTheme"/>锛夛紝
    /// 娌＄晫闈㈡寕涓婃潵鏃剁敤 <see cref="UiTheme.Default"/> 鍏滃簳銆?    ///
    /// 瀹冨拰鐣岄潰鑷繁閭ｉ宸ュ叿鏉″叡鐢ㄤ竴濂楁暟瀛楋紙閮藉嚭鑷?`InkUi.Tokens`锛夛紝
    /// 鎵€浠?鎿嶄綔鏉″拰宸ュ叿鏉℃槸涓€瀹?杩欏彞璇濆湪浠ｇ爜涓婁篃鏈変簡鐫€钀斤紝鑰屼笉鏄潬涓ゅ鍚勫啓涓€浠姐€?    /// </summary>
    internal UiTheme FloatingTheme = UiTheme.Default;

    /// <summary>鐣岄潰鎶婃诞灞備富棰樻帹涓婃潵浜嗐€備富棰樻崲浜嗚绔嬪埢閲嶇敾锛堟诞灞傚湪杩欎竴甯у氨鍙樻牱锛夈€?/summary>
    internal void SetFloatingThemeFromUi(UiTheme theme)
    {
        FloatingTheme = theme;
        _dirty = true;
    }

    // ---- 鐣岄潰鍋忓ソ鐨勫瓨鍙栵紙寮曟搸鍙綋浠撳簱锛屼笉瑙ｉ噴鍐呭锛?---------------------

    /// <summary>鐣岄潰鏉ラ棶涓€鏉″亸濂姐€傛病鏈夊氨杩斿洖 null锛堢晫闈㈣嚜宸辩煡閬撻粯璁ゅ€硷級銆?/summary>
    internal string GetUiPref(string key)
        => key != null && UiPrefs.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// 鐣岄潰璁颁竴鏉″亸濂姐€備紶 null 琛ㄧず"鍥炲埌榛樿"锛岄偅灏辨妸杩欎竴椤?*鍒犳帀**鈥斺€?    /// 閰嶇疆鏂囦欢閲屽彧鐣欏拰榛樿涓嶄竴鏍风殑椤癸紝浠ュ悗榛樿鍊兼敼浜嗭紝鑰侀厤缃笉浼氭妸鏂伴粯璁ら《鎺夈€?    /// </summary>
    internal void SetUiPref(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (value == null)
        {
            if (UiPrefs.Remove(key)) _uiPrefsDirty = true;
            return;
        }
        if (UiPrefs.TryGetValue(key, out var old) && old == value) return;
        UiPrefs[key] = value;
        _uiPrefsDirty = true;
    }

    /// <summary>鎶婇敭浣嶄笌鐣岄潰鍋忓ソ鍐欑洏锛堝彧鍦ㄧ湡鐨勬敼杩囨椂鎵嶅啓锛夈€傝嚜妫€妯″紡涓€寰嬩笉鍐欍€?/summary>
    internal void FlushSettings()
    {
        if (SelfCheckMode) return;
        if (!Keys.Dirty && !_uiPrefsDirty) return;
        InkSettings.Save(Keys, UiPrefs, UpdateFeed.Url);
        _uiPrefsDirty = false;
    }

    /// <summary>鑷鐢細**鏃犺鑷妯″紡鐨勭浠?*锛岀珛鍒绘妸璁剧疆鍐欑洏锛堢敤鏉ラ獙璇?璁板緱浣?杩欐潯閾惧瓙锛夈€?/summary>
    internal void SaveSettingsForTest() => InkSettings.Save(Keys, UiPrefs, UpdateFeed.Url);

    /// <summary>
    /// 鑷鐢細鎶婂唴瀛橀噷鐨勭晫闈㈠亸濂芥竻绌恒€?*浠庢枃浠堕噸璇?*銆?    /// 涓嶈繖涔堝仛鐨勮瘽锛?閲嶅紑鐣岄潰璇诲洖浜嗗亸濂?鍏跺疄璇荤殑鏄唴瀛橀噷閭ｄ唤锛岀瓑浜庢病楠屽埌钀界洏銆?    /// </summary>
    internal void ReloadUiPrefsForTest()
    {
        UiPrefs.Clear();
        foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
            Console.WriteLine("settings: " + w);
    }

    /// <summary>
    /// 鐣岄潰涓婇偅涓?閲嶅惎"锛氱粰鑰佸笀涓€涓?鎰熻涓嶅灏遍噸寮€涓€娆?鐨勫嚭鍙?    /// 锛堟暀瀹ゅぇ灞?+ 鎵嬪啓鏉跨殑鏈哄櫒涓婂彲鑳芥病鏈夐敭鐩橈紝鐣岄潰鏄敮涓€鍏ュ彛锛夈€?    ///
    /// **璇箟 = 鍍忕數鑴戦噸鍚細鏉夸功涓嶆帴鍥炴潵**锛堢敤鎴?2026-09-22 瀹氾級銆傝蛋鐨勬槸鐪熸媺鏂拌繘绋?+ 鑷繁閫€鍑猴紝
    /// 鎵€浠ュ唴瀛橈紙鍚┍鍔ㄥ唴閮ㄧ紦鍐查偅绾?100 MB锛夊叏閮ㄨ繕缁欑郴缁燂紱鏂拌繘绋嬭捣鏉ユ槸涓€鍧?*骞插噣鐧芥澘**銆?    ///
    /// 鍜?鐣岄潰宕╀簡鑷繁閲嶅缓"閭ｆ潯璺紙<see cref="RestartNow"/>锛?*鍒绘剰涓嶅悓**锛屽埆鎶婁袱澶勪竴璧锋敼锛?    ///   路 杩欓噷锛氳€佸笀**涓诲姩**鐐圭殑锛屼粬瑕佺殑灏辨槸"閲嶆潵涓€娆?锛?    ///   路 閭ｉ噷锛氳€佸笀娌¤姹傘€丄pp 鑷繁鍐冲畾閲嶅惎锛?*涓簡鏉夸功鏄簨鏁?*鈥斺€旀墍浠ラ偅杈圭収鏃?    ///     鍐欎細璇濇殏瀛樸€佸惎鍔ㄦ椂璇诲洖鏉ャ€?    ///
    /// 涓や釜鏂囦欢鐨勫垎宸ュ埆鎼炴贩锛堣 Recovery锛夛細
    ///   路 浼氳瘽鏆傚瓨锛圱EMP锛岃璧板氨鍒狅級="閲嶅惎涓嶄涪涓滆タ"锛岃繖閲?*鏄庣‘涓嶅啓**锛岃繕瑕佹妸娈嬬暀鍒犳帀锛?    ///   路 鑷姩瀛樻。锛圠OCALAPPDATA锛岃璧颁笉鍒狅級="鐩樹笂鐣欎竴浠?锛岃繖閲岀収鍐欌€斺€?    ///     灞忓箷涓婃槸骞插噣鐧芥澘锛屼絾閭ｄ唤鏉夸功杩樺湪鐩樹笂锛岄渶瑕佹椂鑳芥壘鍥炴潵锛堜綋鎰熷儚鐢佃剳閲嶅惎锛?    ///     妗岄潰鏄共鍑€鐨勶紝纭洏涓婄殑鏂囦欢杩樺湪锛夈€?    /// </summary>
    // =====================================================================
    //  鑷姩鏇存柊锛?026-09-29锛?    // =====================================================================
    //
    // 鍒嗗伐锛?*缃戠粶鍏ㄥ湪鍚庡彴绾跨▼**锛屼富绾跨▼鍙仛涓や欢浜嬧€斺€旀妸缁撴灉鍙樻垚鐘舵€佹枃瀛椼€?    // 浠ュ強锛堜笅瀹屼箣鍚庯級鎷夎捣鎹㈠３鑴氭湰鐒跺悗閫€鍑鸿嚜宸便€傜悊鐢憋細涓诲惊鐜繕鎵涚潃娓叉煋鍜岃緭鍏ワ紝
    // 璁╁畠鍦ㄥ崄鍑犵鐨勪笅杞介噷鍗′綇鏄笉鑳芥帴鍙楃殑銆?    //
    // 鐘舵€佸瓧娈电敱**涓荤嚎绋?*鍐欙紙鐣岄潰鍦ㄨ锛夛紝鍚庡彴绾跨▼鍙線 `_updResult` / `_updZipPath`
    // 閲屾斁缁撴灉銆佸啀鎶?`_updPost` / `_updApplyPosted` 绔嬭捣鏉ャ€備富寰幆姣忓抚 `PumpUpdate()` 鏀朵竴娆°€?
    /// <summary>鑷姩鏇存柊鐨勭姸鎬侊紙鐣岄潰閭ｄ竴琛屾樉绀轰粈涔堛€佺偣浜嗗仛浠€涔堬紝閮界湅瀹冿級銆?/summary>
    internal UpdateStage UpdateState = UpdateStage.NotConfigured;
    /// <summary>鑷姩鏇存柊鐨勪竴琛岀姸鎬佹枃瀛椼€?/summary>
    internal string UpdateText = "鏈厤缃洿鏂版簮";

    private float UpdateProgress;                       // 0..1锛堜笅杞戒腑锛屽彧缁欑晫闈㈢湅锛?    private string _updVersion = "", _updNotes = "", _updSha = "";
    private volatile bool _updPost;                     // 鍚庡彴锛氭鏌ョ粨鏋滄斁濂戒簡
    private volatile bool _updApplyPosted;              // 鍚庡彴锛氫笅杞界粨鏉熶簡
    private volatile bool _updBusy;                     // 鏈夊悗鍙颁换鍔″湪璺戯紙鍒彔鍔狅級
    /// <summary>
    /// 鑷/楠屾敹鐢細鏌ュ埌鏂扮増鏈氨**鑷姩缁х画涓嬭浇瀹夎**锛堜笉鐢ㄧ瓑浜哄啀鐐逛竴涓嬶級銆?    /// 浜у搧閲屾案杩滄槸 false锛堢敤鎴风偣涓や笅锛氫竴涓嬫煡銆佷竴涓嬭锛夈€?    /// </summary>
    internal bool AutoApplyUpdate;
    /// <summary>
    /// 鑷/楠屾敹鐢細`--updatecheck` 鍙煡涓嶈鏃讹紝**鏌ュ畬灏辫嚜宸遍€€鍑?*
    /// 锛堜骇鍝侀噷娌℃湁杩欎釜寮€鍏斥€斺€旂敤鎴风偣涓€涓嬫煡銆佸啀鐐逛竴涓嬭锛岀晫闈㈠綋鐒惰鐣欑潃锛夈€?    /// </summary>
    internal bool AutoCheckOnly;
    private readonly object _updLock = new();
    private (UpdateFeed.Manifest m, string err, List<(string Url, bool UseProxy)> dl) _updResult;
    private string _updUsedUrl = "";
    private string _updZipPath = "", _updError = "";
    private List<(string Url, bool UseProxy)> _updDownloads = new();   // 涓嬭浇鍊欓€夛紙蹇殑鍦ㄥ墠锛屽け璐ヨ嚜鍔ㄦ崲涓嬩竴鏉★級
    private long _updGot, _updTotal;                    // 涓嬭浇杩涘害锛堝悗鍙板啓銆佷富绾跨▼璇伙級

    /// <summary>銆屾鏌ユ洿鏂般€嶈鐐逛簡涓€涓嬶紙瑙?<see cref="IEngineCommands.CheckUpdate"/>锛夈€?/summary>
    internal void CheckUpdateFromUi()
    {
        if (!UpdateFeed.HasAnySource)
        {
            // **娌￠厤鏇存柊婧愭槸姝ｅ父鐘舵€侊紝涓嶆槸閿欒**锛堟暀瀹ゆ満鍣ㄥ彲浠ユ寚鍒板眬鍩熺綉鍏变韩锛?            // 涓€鏉￠兘娌℃湁鏃剁偣銆屾鏌ユ洿鏂般€嶅彧鏀逛竴琛岀姸鎬佹枃瀛楋級
            UpdateState = UpdateStage.NotConfigured;
            UpdateText = "鏈厤缃洿鏂版簮";
            NotifyUiStateChanged();
            Console.WriteLine("鑷姩鏇存柊锛氭病鏈夊彲鐢ㄧ殑鏇存柊婧愶紙settings.json 鐨?update.url锛屾垨 UpdateFeed.Sources锛?);
            return;
        }
        if (_updBusy) return;                           // 宸茬粡鍦ㄦ煡 / 鍦ㄤ笅锛屽埆鍙犲姞

        UpdateState = UpdateStage.Checking;
        UpdateText = "妫€鏌ヤ腑鈥?;
        NotifyUiStateChanged();

        Console.WriteLine($"鑷姩鏇存柊锛氭鏌ワ紙褰撳墠 {UpdateFeed.CurrentVersion}锛?
                          + (UpdateFeed.Url.Length > 0 ? "鐢ㄦ埛閰嶇疆婧? : $"鍊欓€?{UpdateFeed.Sources.Length} 鏉★紝骞惰璇?)
                          + (UpdateFeed.Url.Length > 0 ? "" : "锛涘姞閫熺珯鐩磋繛銆丟itHub 閭ｆ潯璧扮郴缁熶唬鐞?) + "锛?);
        _updBusy = true;
        var th = new System.Threading.Thread(() =>
        {
            var m = UpdateFeed.FetchBest(UpdateFeed.CurrentVersion, out string used, out string err, out var dl);
            lock (_updLock)
            {
                _updResult = (m, err, dl);
                _updUsedUrl = used;
            }
            _updPost = true;
        })
        { IsBackground = true, Name = "InkTeach-Update-Check" };
        th.Start();
    }

    /// <summary>
    /// 宸茬粡鏌ュ埌鏂扮増鏈簡锛屽啀鐐逛竴涓嬶細**涓嬭浇 鈫?鏍￠獙 鈫?鎹㈠３閲嶅惎**銆?    /// 涓嬭浇鎸夊€欓€夊垪琛ㄩ€愭潯璇曪紙鍚岀増鏈殑鍥藉唴鍔犻€熺珯鍦ㄥ墠锛夛細澶辫触鎴栧崱姝诲氨鎹笅涓€鏉★紝
    /// 鍏ㄩ儴璇曞畬杩樹笉琛屾墠鎶ュけ璐ワ紙2026-10-02锛涗互鍓嶅彧璇曚竴鏉★紝澶辫触瑕佺敤鎴烽噸鐐逛竴娆★級銆?    /// </summary>
    internal void ApplyUpdateFromUi()
    {
        if (_updBusy || UpdateState != UpdateStage.Available || _updDownloads.Count == 0) return;

        UpdateState = UpdateStage.Downloading;
        UpdateProgress = 0f;
        UpdateText = "涓嬭浇涓?0%";
        NotifyUiStateChanged();

        string sha = _updSha, ver = _updVersion;
        var urls = _updDownloads;
        Console.WriteLine($"鑷姩鏇存柊锛氬紑濮嬩笅杞?{ver}锛坽urls.Count} 鏉″€欓€夋簮锛岄€愭潯璇曪級");
        _updBusy = true;
        var th = new System.Threading.Thread(() =>
        {
            string dir = UpdateFeed.UpdateDirFor(ver);
            string zip = Path.Combine(dir, $"InkTeach-{UpdateFeed.SafeVer(ver)}-win-x64.zip");
            bool ok = false;
            string err = "娌℃湁鍙敤鐨勪笅杞藉湴鍧€";
            foreach (var (url, useProxy) in urls)
            {
                System.Threading.Interlocked.Exchange(ref _updGot, 0);
                System.Threading.Interlocked.Exchange(ref _updTotal, 0);
                Console.WriteLine($"鑷姩鏇存柊锛氳瘯 {UpdateFeed.HostOf(url)}");
                ok = UpdateFeed.Download(url, zip, sha,
                    (got, total) =>
                    {
                        System.Threading.Interlocked.Exchange(ref _updGot, got);
                        System.Threading.Interlocked.Exchange(ref _updTotal, total);
                    }, out err, useProxy);
                if (ok) break;
                Console.WriteLine("鑷姩鏇存柊锛氳繖鏉℃簮娌℃垚锛? + err + "锛夛紝鎹笅涓€鏉?);
            }
            lock (_updLock)
            {
                _updZipPath = ok ? zip : "";
                _updError = err;
            }
            _updApplyPosted = true;
        })
        { IsBackground = true, Name = "InkTeach-Update-Download" };
        th.Start();
    }

    /// <summary>涓诲惊鐜瘡甯у彨涓€娆★細鎶婂悗鍙扮粨鏋滄惉鎴愮姸鎬侊紱涓嬪畬浜嗗氨鎷夋崲澹宠剼鏈苟閫€鍑鸿嚜宸便€?*鍙湪涓荤嚎绋嬭窇銆?*</summary>
    private void PumpUpdate()
    {
        if (UpdateState == UpdateStage.Downloading)
        {
            long got = System.Threading.Interlocked.Read(ref _updGot);
            long total = System.Threading.Interlocked.Read(ref _updTotal);
            float p = total > 0 ? Math.Clamp(got / (float)total, 0f, 1f) : 0f;
            if (p - UpdateProgress > 0.01f)
            {
                UpdateProgress = p;
                UpdateText = $"涓嬭浇涓?{p * 100:F0}%";
                NotifyUiStateChanged();
            }
        }

        if (_updPost)
        {
            _updPost = false;
            _updBusy = false;
            UpdateFeed.Manifest m;
            string err;
            List<(string Url, bool UseProxy)> dl;
            lock (_updLock) (m, err, dl) = _updResult;

            bool needApply = false;
            if (m == null)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "妫€鏌ュけ璐ワ紝鍐嶇偣閲嶈瘯";
                Console.WriteLine("鑷姩鏇存柊锛氭鏌ュけ璐ワ細" + err);
            }
            else if (UpdateFeed.CompareVersions(m.Version, UpdateFeed.CurrentVersion) <= 0)
            {
                UpdateState = UpdateStage.UpToDate;
                UpdateText = "宸叉槸鏈€鏂?;
                Console.WriteLine($"鑷姩鏇存柊锛氬凡鏄渶鏂帮紙{UpdateFeed.CurrentVersion}锛涙簮 {UpdateFeed.HostOf(_updUsedUrl)}锛?);
            }
            else if (m.Url.Length == 0 || m.Sha256.Length == 0)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "娓呭崟涓嶅畬鏁?;
                Console.WriteLine($"鑷姩鏇存柊锛氭竻鍗曢噷 {m.Version} 缂?url 鎴?sha256锛屾嫆缁?);
            }
            else
            {
                _updVersion = m.Version;
                _updNotes = m.Notes;
                _updDownloads = dl ?? new List<(string Url, bool UseProxy)>();
                _updSha = m.Sha256;
                UpdateState = UpdateStage.Available;
                UpdateText = $"鏈夋柊鐗堟湰 {m.Version}";
                Console.WriteLine($"鑷姩鏇存柊锛氬彂鐜?{m.Version}锛堝綋鍓?{UpdateFeed.CurrentVersion}锛?
                                  + $"婧?{UpdateFeed.HostOf(_updUsedUrl)}锛?
                                  + (m.Notes.Length > 0 ? "锛? + Shorten(m.Notes) : ""));
                needApply = AutoApplyUpdate;
            }
            NotifyUiStateChanged();

            // `--updatecheck`锛堥獙鏀剁敤锛夛細**鏌ュ畬灏遍€€**锛屽埆鎶婄晫闈㈡寕鍦ㄥ睆骞曚笂銆?            // 浜у搧閲屾病鏈夎繖涓紑鍏筹紙鐢ㄦ埛鐐逛竴涓嬫煡銆佸啀鐐逛竴涓嬭锛岀晫闈㈠綋鐒惰鐣欑潃锛夈€?            if (AutoCheckOnly) { _quit = true; return; }
            if (needApply) ApplyUpdateFromUi();
        }

        if (_updApplyPosted)
        {
            _updApplyPosted = false;
            _updBusy = false;
            string zip, err;
            lock (_updLock) { zip = _updZipPath; err = _updError; }

            if (zip.Length == 0)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "涓嬭浇澶辫触锛屽啀鐐归噸璇?;
                Console.WriteLine("鑷姩鏇存柊锛氫笅杞藉け璐ワ細" + err + "锛堣蒋浠朵繚鎸佸師鏍凤級");
                NotifyUiStateChanged();
                return;
            }

            // 鎹㈠３锛氳剼鏈?*绛夋垜浠€€鍑轰箣鍚?*鎵嶅姩鏂囦欢锛堣 UpdateFeed.LaunchSwap锛夈€?            // 鎷変笉璧锋潵灏辩暀鍦ㄥ師鍦扳€斺€?鏇存柊娌¤鎴愩€佽蒋浠朵篃娌′簡"鏄渶鍧忕殑缁撴灉銆?            string exe = Environment.ProcessPath;
            string appDir = Path.GetDirectoryName(exe);
            try
            {
                string script = UpdateFeed.WriteSwapScript(Path.GetDirectoryName(zip));
                if (UpdateFeed.LaunchSwap(script, appDir, zip, exe, out string lerr))
                {
                    UpdateState = UpdateStage.Ready;
                    UpdateText = "姝ｅ湪閲嶅惎鈥?;
                    Console.WriteLine($"鑷姩鏇存柊锛歿_updVersion} 宸蹭笅杞藉苟鏍￠獙锛屾崲澹宠剼鏈凡鎷夎捣锛屾湰杩涚▼閫€鍑?);
                    NotifyUiStateChanged();
                    _quit = true;                       // 閫€鍑?鈫?鑴氭湰鎺ユ墜锛氭敼鍚嶆棫鐩綍銆佽В鍘嬨€侀噸鍚?                }
                else
                {
                    UpdateState = UpdateStage.Failed;
                    UpdateText = "鎹㈠３澶辫触锛堜繚鎸佸師鏍凤級";
                    Console.WriteLine("鑷姩鏇存柊锛氭崲澹冲惎鍔ㄥけ璐ワ細" + lerr);
                    NotifyUiStateChanged();
                }
            }
            catch (Exception ex)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "鎹㈠３澶辫触锛堜繚鎸佸師鏍凤級";
                Console.WriteLine("鑷姩鏇存柊锛氭崲澹冲け璐ワ細" + ex.Message);
                NotifyUiStateChanged();
            }
        }
    }

    /// <summary>鐘舵€佹枃瀛楄鑳藉杩涢潰鏉块偅涓€琛岋紝闀跨殑鎴帀锛堟崲琛屼細璁╄楂樹贡鎺夛級銆?/summary>
    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 60 ? s : s[..60] + "鈥?;
    }

    internal void RestartFromUi()
    {
        Recovery.DiscardSession();   // 淇濊瘉鏂拌繘绋嬫槸绌虹櫧锛氫笉鍐欙紝鑰屼笖鍒犳帀涓婃鐨勬畫鐣?        AutoSaveNow();               // 鐩樹笂鐣欎竴浠斤紙涓嶅彈"涓嬫鎵撳紑瑕佷笉瑕佹帴涓?閭ｄ釜鍋忓ソ褰卞搷锛?
        if (!RestartSelf("鐣岄潰涓婄殑閲嶅惎"))
        {
            Console.WriteLine("閲嶅惎娌℃垚鍔燂紝缁х画鐢紙鏉夸功杩樺湪锛?);
            return;
        }
        RestartRequested = true;
        _quit = true;
    }

    internal void SetPassThroughFromUi(bool on)
    {
        // 寮€绌块€?= 閿洏/鐐瑰嚮褰掍笅灞傦細鍥炴斁涓紑瀹冨氨鐐逛笉鍒版帶鍒舵潯浜嗭紝鍏堟敹鎺夊洖鏀俱€?        if (on) ExitReplayForEdit("寮€绌块€?);
        // 鐣岄潰閭ｆ牸鍙彂"鍒囨崲"锛坄!st.PassThrough`锛夛紝鎵€浠ヨ繖灏辨槸"绌块€忓紑鍏?杩欐潯璺細
        // 閫€鍑烘椂鎭㈠杩涚┛閫忓墠鐨勬澘鎬侊紙鐢ㄦ埛 2026-09-30 鎷嶆澘锛夈€?        SetPassThrough(on, restoreBoard: true);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 寮€鍏崇櫧鏉裤€傚簳鑹蹭竴鍙橈紝鏁翠釜鍐呭灞傞兘瑕侀噸鐢烩€斺€旂紦瀛橀噷閭ｅ紶鍥炬槸鎸夋棫搴曡壊鐢荤殑銆?    /// </summary>
    internal void SetBoardFromUi(bool on)
    {
        if (BoardOn == on) return;
        BoardOn = on;
        // 寮€鐧芥澘灏遍『鎵嬪叧鎺夌┛閫忥紙鍙︿竴杈瑰湪 SetPassThrough 閲岋紝涓や釜鏂瑰悜閮芥尅锛岀悊鐢辫閭ｅ効锛?        if (on) SetPassThrough(false);
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 瑁呯晫闈紝**骞剁暀涓?鍐嶉€犱竴涓?鐨勫姙娉?*銆備骇鍝佷唬鐮佺敤杩欎竴鏉★紝涓嶈鐢?<see cref="SetUi"/>锛?    /// 鐣岄潰鍑洪棶棰樻椂寮曟搸瑕佽兘鑷繁鎶婄晫闈㈤噸寤哄洖鏉ワ紙鏁欏澶у睆寰堝彲鑳芥病鏈夐敭鐩橈紝
    /// 鐣岄潰鏄敮涓€鐨勫嚭鍙ｏ紝瑙?<see cref="Recovery"/> 鐨勬敞閲婏級銆?    ///
    /// 浼犺繘鏉ョ殑宸ュ巶蹇呴』姣忔閮借兘閫犱竴涓?*鍏ㄦ柊鐨?*鐣岄潰锛堜笉瑕佸鐢ㄥ悓涓€涓疄渚嬧€斺€?    /// 閭ｄ釜瀹炰緥姝ｆ槸鍒氳鍒ゅ畾"鐘舵€佸潖浜?鐨勯偅涓€涓級銆?    /// </summary>
    public void SetUiFactory(Func<IOverlayUi> factory)
    {
        _uiFactory = factory;
        SetUi(factory != null ? factory() : null);
    }

    /// <summary>鐣岄潰鍒囬€夋嫨鏂瑰紡锛堢煩褰㈡ / 鑷敱濂楃储锛夈€?/summary>
    internal void SetSelectModeFromUi(SelectMode mode)
    {
        if (SelMode == mode) return;
        SetSelectMode(mode);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 鐣岄潰鎹㈡澘鑹层€傚簳鑹蹭竴鍙樻暣涓唴瀹瑰眰閮借閲嶇敾鈥斺€旂紦瀛橀噷閭ｅ紶鍥炬槸鎸夋棫搴曡壊鐢荤殑
    /// 锛堝拰 <see cref="SetBoardFromUi"/> 鍚岀悊锛夈€?    /// </summary>
    internal void SetBoardColorFromUi(Color4 color)
    {
        if (BoardColor.Equals(color)) return;
        BoardColor = color;
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>鐣岄潰涓婄殑"鍏ㄩ€?銆?see cref="SelectAll"/> 鑷繁浼氭妸宸ュ叿鍒囨垚妗嗛€夛紝鍏嶅緱鐢ㄦ埛浠ヤ负娌＄敓鏁堛€?/summary>
    internal void SelectAllFromUi()
    {
        ExitReplayForEdit("鍏ㄩ€?);
        SelectAll();
        _dirty = true;
    }

    /// <summary>鐣岄潰涓婄殑"涓婁竴灞?/ 涓嬩竴灞?锛堟暣灞忕炕椤碉級銆?/summary>
    internal void FlipPageFromUi(bool down)
    {
        ExitReplayForEdit("缈婚〉");
        if (!FlipPage(down)) return;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 鎸囬拡鎸変笅鐨勭涓€绔欙細鍏堥棶鐣岄潰銆傝繑鍥?true 琛ㄧず杩欐杈撳叆褰掔晫闈紙姣斿鎸夊埌浜?    /// 鎮诞鏉′笂鐨勬寜閽級锛屽紩鎿庝笉鍐嶆妸瀹冨彉鎴愮瑪鐢汇€?    /// </summary>
    private bool UiPointerDown(float x, float y, float pressure, bool fromPen, bool eraserTip,
                               bool fromTouch = false, uint pointerId = 0)
    {
        if (!UiVisibleNow) return false;
        var e = new UiPointerEvent(x / UiUnit, y / UiUnit, pressure, fromPen, eraserTip,
                                   fromTouch, pointerId);
        if (!UiGuard("PointerDown", () => Ui.PointerDown(e), false)) return false;
        UiCapturing = true;
        return true;
    }

    private bool UiPointerMove(float x, float y, float pressure, bool fromPen, bool eraserTip,
                               uint pointerId = 0)
    {
        if (!UiVisibleNow) return false;

        // 鍙湁鍦ㄧ晫闈㈠凡缁忔崟鑾疯緭鍏ユ垨鎸囬拡钀藉湪鐣岄潰鐭╁舰鍐呮椂鎵嶈浆鍙戯紝閬垮厤娌″繀瑕佺殑璋冪敤銆?        if (!UiCapturing && !UiContains(x, y)) return false;
        var e = new UiPointerEvent(x / UiUnit, y / UiUnit, pressure, fromPen, eraserTip,
                                   false, pointerId);
        return UiGuard("PointerMove", () => Ui.PointerMove(e), false);
    }

    private bool UiPointerUp(float x, float y, float pressure, bool fromPen, bool eraserTip,
                             uint pointerId = 0)
    {
        if (!UiVisibleNow || !UiCapturing) return false;
        var e = new UiPointerEvent(x / UiUnit, y / UiUnit, pressure, fromPen, eraserTip,
                                   false, pointerId);
        bool consumed = UiGuard("PointerUp", () => Ui.PointerUp(e), false);
        UiCapturing = false;
        return consumed;
    }

    private bool UiContains(float x, float y)
    {
        x /= UiUnit; y /= UiUnit;   // 鐗╃悊 鈫?鐣岄潰閫昏緫锛圖PI 脳 鐣岄潰缂╂斁锛?        foreach (var w in _windows)
            if (w.UiBoundsLogicalContains(x, y)) return true;
        return false;
    }

    /// <summary>
    /// 鍙?WM_NCHITTEST 鐨?lParam 閲岀殑灞忓箷鍧愭爣銆?    ///
    /// 瀹冩槸涓や釜**鏈夌鍙?* 16 浣嶆嫾璧锋潵鐨?*鐗╃悊**鍍忕礌鍧愭爣銆傚繀椤绘寜鏈夌鍙峰彇锛?    /// 鍓睆鍦ㄤ富灞忓乏杈?涓婅竟鏃跺潗鏍囨槸璐熺殑锛屾寜鏃犵鍙峰彇浼氬緱鍒?6 涓囧锛?    /// 璐磋竟銆佸懡涓祴璇曞叏閿欌€斺€旇€屽崟灞?+ 姝ｅ潗鏍囩殑鏈哄櫒涓婂畬鍏ㄧ湅涓嶅嚭鏉ャ€?    /// </summary>
    private static void HitTestPoint(IntPtr lParam, out float x, out float y)
    {
        long v = lParam.ToInt64();
        x = (short)(v & 0xFFFF);
        y = (short)((v >> 16) & 0xFFFF);
    }

    // ---- 闈㈡澘鐨?鎺ヨ緭鍏ュ皬绐?锛堟柟妗?B锛?-------------------------------------

    // ---- 鐣岄潰鍥炶皟鐨勭粺涓€鍏ュ彛锛?*閮芥槸闃插脊鐨?* ------------------------------
    //
    // 涓轰粈涔堝€煎緱鍖呰繖涓€灞傦細鐣岄潰閲岀殑涓€涓?bug 涓嶈璁?鏉夸功"杩欎釜涓诲姛鑳借窡鐫€涓嶅彲鐢ㄣ€?    // 浠ュ墠鐨勪繚鎶ゅ彧鏈夌粯鍒堕偅涓€娈碉紙`Overlay.DrawUi` 閲岀殑 try/catch 鎵撳嵃锛夛紝
    // 鑰?`PointerDown/Move/Up`銆乣OnStateChanged`銆乣QueryBounds`銆乣IsAnimating`
    // 鍏ㄦ槸瑁歌皟鈥斺€旂晫闈竴鎶涳紝寮傚父灏卞啋鍒版秷鎭惊鐜噷锛岃€佸笀褰撳満浠€涔堥兘鐢讳笉鍑烘潵銆?    //
    // 瑙勭煩锛?*3 绉掑唴 3 娆?*寮傚父灏辨妸鐣岄潰鍋滅敤锛堟崲鎴?NullUi锛屽苟钘忔帀鎺ヨ緭鍏ュ皬绐楋級锛?    // 绗旇抗鐓у父銆?    //
    // 涓轰粈涔堟槸"涓€涓椂闂寸獥鍐?3 娆?鑰屼笉鏄?杩炵画 3 娆¤皟鐢?锛氱晫闈㈡瘡甯ч兘浼氳璋冨ソ鍑犳
    // 锛圴isible / QueryBounds / Render / PointerMove鈥︼級锛屽彧瑕佸埆澶勭殑璋冪敤鎴愬姛灏辨妸
    // 璁℃暟娓呴浂鐨勮瘽锛屼竴鏉″彧鍦?PointerDown 閲岃俯鐨?bug 姘歌繙鏀掍笉鍒?3 娆°€?    // 鍙嶈繃鏉?涓€娆″氨鍋滅敤"鍙堝お鐙犫€斺€斿伓鍙戜竴娆′笉璇ヨ宸ュ叿鏉℃秷澶变竴鏁磋妭璇俱€?    private int _uiFaults;
    private double _uiLastFaultMs = double.NegativeInfinity;

    /// <summary>鐣岄潰宸ュ巶锛氭湁瀹冨紩鎿庢墠鑳?鑷繁鎶婄晫闈㈤噸寤轰竴涓洖鏉?銆?/summary>
    private Func<IOverlayUi> _uiFactory;

    /// <summary>鏈€杩戜竴鍒嗛挓閲岄噸寤鸿繃鍑犳鐣岄潰銆傞噸寤轰篃澶辫触灏卞緱寰€涓婅蛋涓€绾э紙閲嶅惎杞欢锛夈€?/summary>
    private int _uiRebuilds;
    private double _uiRebuildWindowStartMs = double.NegativeInfinity;

    /// <summary>鑷鐢細杩欎竴娆?閲嶅惎杞欢"鏈夋病鏈夌湡鐨勮鍙戣捣銆?/summary>
    internal bool RestartRequested { get; private set; }

    /// <summary>閲嶅惎璁℃暟娓呰繃涓€娆″氨澶熶簡锛堣窇婊′竴鍒嗛挓绠?绋充綇浜?锛夈€?/summary>
    private bool _restartCountCleared;

    /// <summary>
    /// 鐣岄潰鐨勫紓甯告鏁颁笌澶勭疆銆?*涓夌骇闃舵**锛屼竴绾ф瘮涓€绾ч噸锛屼絾閮戒笉鐣欐灞€锛?    ///
    ///   鈶?閲嶅缓鐣岄潰锛堝紩鎿庤嚜宸?new 涓€涓洖鏉ワ級鈥斺€?鏉夸功涓嶅姩锛屽伐鍏锋潯閲嶆柊鎸備竴閬嶏紱
    ///   鈶?閲嶅惎杞欢 鈥斺€?閲嶅惎鍓嶆妸鏉夸功鏆傚瓨涓嬫潵锛岄噸鍚悗璇诲洖鏉ワ紝**浠€涔堥兘涓嶄涪**锛?    ///   鈶?閮戒笉琛屽氨閫€鍥炴棤鐣岄潰 鈥斺€?鑷冲皯杩樿兘鍐欙紝鑰屼笖鏄繀鐜?bug 鏃剁殑鍞竴鍑鸿矾
    ///      锛堝惁鍒欏氨鏄棤闄愰噸鍚紝灞忓箷涓€鐩村湪闂紝姣斿仠鐢ㄦ洿绯燂級銆?    ///
    /// 涓轰粈涔堝繀椤诲線"閲嶅惎"璧帮細鏁欏鐨勫ぇ灞?+ 鎵嬪啓鏉挎満鍣ㄤ笂寰堝彲鑳芥病鏈夐敭鐩橈紝
    /// 鐣岄潰灏辨槸鍞竴鐨勫嚭鍙ｏ紱鐣岄潰娌′簡鍙堟病娉曢噸鍚紝灏辨槸姝诲眬锛堢敤鎴?2026-09-16 鎻愮殑锛夈€?    /// </summary>
    private void NoteUiFault(string what, Exception ex)
    {
        if (NowMs - _uiLastFaultMs > 3000) _uiFaults = 0;   // 闅斾箙浜嗗綋鏂扮殑涓€杞?        _uiLastFaultMs = NowMs;
        _uiFaults++;
        if (_uiFaults < 3)
        {
            Console.WriteLine($"鐣岄潰寮傚父锛坽what}锛岀 {_uiFaults} 娆★級锛歿ex.Message}");
            return;
        }

        Console.WriteLine($"鐣岄潰 3 绉掑唴鎶涗簡 {_uiFaults} 娆″紓甯革紙鏈€鍚庝竴娆″湪 {what}锛夛細{ex.Message}");

        // 鈶?杩樿兘閲嶅缓灏卞厛閲嶅缓锛氫笉涓㈡澘涔︼紝閫氬父涔熷鐢紙鐣岄潰澶氬崐鏄姸鎬佽鎼炲潖浜嗭級銆?        if (_uiFactory != null && UiRebuildAllowed())
        {
            RebuildUi(what);
            return;
        }

        // 鈶?閲嶅缓涔熸晳涓嶅洖鏉?鈫?閲嶅惎杞欢锛堝厛鎶婃澘涔﹀瓨涓嬫潵锛夈€?        if (TryRestartForUiProblem(what)) return;

        // 鈶?鏈€鍚庣殑鍏滃簳锛氶€€鍥炴棤鐣岄潰銆傚繀鐜扮殑 bug 璧板埌杩欓噷锛岃嚦灏戣繕鑳藉啓銆?        DisableUi(what);
    }

    private void RebuildUi(string what)
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) { _uiRebuilds = 0; _uiRebuildWindowStartMs = NowMs; }
        _uiRebuilds++;
        _uiFaults = 0;

        Console.WriteLine($"  鈫?閲嶅缓鐣岄潰锛堣繖涓晫闈㈡槸绗?{_uiRebuilds} 娆￠噸寤猴級鈥斺€?鏉夸功涓嶅彈褰卞搷");
        try
        {
            SetUi(_uiFactory());
        }
        catch (Exception ex)
        {
            Console.WriteLine("鐣岄潰閲嶅缓澶辫触锛? + ex.Message);
            SetUi(null);
        }
    }

    /// <summary>涓€鍒嗛挓鍐呮渶澶氶噸寤轰袱娆★紱鍐嶅璇存槑閲嶅缓涓嶇鐢紝璇ュ線涓婅蛋涓€绾с€?/summary>
    private bool UiRebuildAllowed()
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) return true;
        return _uiRebuilds < 2;
    }

    /// <summary>
    /// 鍥犱负鐣岄潰鍑洪棶棰樿€岄噸鍚蒋浠躲€傝繑鍥?true = 宸茬粡寮€濮嬮噸鍚紙璋冪敤鏂瑰埆鍐嶅線涓嬭蛋锛夈€?    /// **鍏堝瓨鏉夸功鍐嶉噸鍚?*锛氶噸鍚殑鍓嶆彁鏄笉涓笢瑗裤€?    /// </summary>
    private bool TryRestartForUiProblem(string what)
    {
        int n = Recovery.NoteRestart();
        if (n > Recovery.MaxRestartsInWindow)
        {
            Console.WriteLine($"  鈫?杩欎釜绐楀彛鍐呭凡缁忛噸鍚繃 {n - 1} 娆★紝涓嶅啀閲嶅惎锛堝鍗婃槸蹇呯幇鐨勯棶棰橈級");
            return false;
        }

        try { Recovery.SaveSession(InkSerializer.Save(Doc)); } catch (Exception ex) { Console.WriteLine("鏉夸功鏆傚瓨澶辫触锛? + ex.Message); }
        Console.WriteLine($"  鈫?閲嶅惎杞欢锛堢 {n} 娆★級锛屾澘涔﹀凡鏆傚瓨锛岄噸鍚悗鑷姩璇诲洖鏉?);

        if (!RestartSelf(what)) return false;
        RestartRequested = true;
        _quit = true;           // 涓诲惊鐜€€鍑?鈫?Shutdown 鈫?杩涚▼缁撴潫锛屾柊杩涚▼鎺ユ墜
        return true;
    }

    /// <summary>
    /// 鐪熺殑鎶婅蒋浠堕噸鏂版媺璧锋潵銆?*瀹夸富鍙互瑕嗗啓**锛堣嚜妫€閲屽彧璁颁竴绗旓紝涓嶇湡鎷夎繘绋嬶級銆?    /// 杩斿洖 false = 娌℃媺璧锋潵锛岄偅灏卞埆閫€鍑衡€斺€旈€€鍥炴棤鐣岄潰缁х画璺戞€绘瘮"閫€鍑哄悗浠€涔堥兘娌℃湁"寮恒€?    /// </summary>
    protected virtual bool RestartSelf(string why)
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("鎷夎捣鏂拌繘绋嬪け璐ワ細" + ex.Message);
            return false;
        }
    }

    /// <summary>涓夌骇閮借蛋涓嶉€氭椂鐨勫厹搴曪細鍋滅敤鐣岄潰锛岀瑪杩圭収甯搞€?/summary>
    private void DisableUi(string what)
    {
        Console.WriteLine($"  鈫?鐣岄潰宸插仠鐢紙{what}锛夈€傜瑪杩逛笉鍙楀奖鍝嶏紱淇ソ涔嬪悗閲嶅紑杞欢灏卞洖鏉ヤ簡銆?);
        Ui = new NullUi();
        UiCapturing = false;
        Native.ReleaseCapture();
        _uiHover = false;
        if (_uiInputHwnd != IntPtr.Zero && _uiInputShown)
        {
            Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
            _uiInputShown = false;
        }
        _dirty = true;
    }

    private T UiGuard<T>(string what, Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception ex) { NoteUiFault(what, ex); return fallback; }
    }

    private void UiGuard(string what, Action call)
    {
        try { call(); }
        catch (Exception ex) { NoteUiFault(what, ex); }
    }

    internal bool UiVisibleNow => Ui != null && UiGuard("Visible", () => Ui.Visible, false);
    internal RectF UiQueryBoundsNow() =>
        Ui == null ? RectF.Empty : UiGuard("QueryBounds", () => Ui.QueryBounds(), RectF.Empty);
    /// <summary>鐣岄潰澹版槑"鎴戜細鐢诲埌鍗犵敤鐭╁舰澶栭潰涓€鍦?锛堟姇褰?娴嚭棰勮锛夛紝瑙?IOverlayUi.PaintMargin銆?/summary>
    internal float UiPaintMarginNow =>
        Ui == null ? 0f : UiGuard("PaintMargin", () => Ui.PaintMargin, 0f);
    internal bool UiIsAnimatingNow => Ui != null && UiGuard("IsAnimating", () => Ui.IsAnimating, false);
    internal RectF UiLayoutNow(RectF screen, float dpiScale) =>
        Ui == null ? RectF.Empty : UiGuard("Layout", () => Ui.Layout(screen, dpiScale), RectF.Empty);
    internal void UiRenderNow(ID2D1DeviceContext ctx, UiTheme theme) =>
        UiGuard("Render", () => Ui.Render(ctx, theme));

    /// <summary>
    /// 姣忔娓叉煋鍓嶈皟鐢ㄤ竴娆★細璁╂帴杈撳叆灏忕獥璺熺潃鐣岄潰杩欎竴鍒诲崰鐨勫湴鏂硅蛋銆?    /// 鐣岄潰闅愯棌鎴栨病鍗犲湴鏂规椂鎶婂畠钘忚捣鏉モ€斺€旇棌璧锋潵灏辩瓑浜?涓嶅瓨鍦ㄤ簬杈撳叆閲?銆?    /// </summary>
    private void UpdateUiInputWindow()
    {
        // 鎴浘鏈熼棿鐣岄潰鏄敹璧风殑锛堣 PrepareUi 寮€澶撮偅娈碉級锛氭帴杈撳叆灏忕獥涔熶竴璧锋敹鈥斺€?        // 涓嶇劧"鐪嬩笉瑙佺殑宸ュ叿鏉?閭ｄ竴鏉¤繕浼氭妸鐐瑰嚮鍚冩帀锛堟埅鍥炬嫋妗嗘濂借鐢ㄩ偅涓€鐗囷級銆?        RectF logical = (UiVisibleNow && !CaptureActive) ? UiQueryBoundsNow() : RectF.Empty;

        if (logical.IsEmpty)
        {
            if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            {
                Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
                _uiInputShown = false;
                _uiHover = false;
            }
            return;
        }

        EnsureUiInputWindow();
        if (_uiInputHwnd == IntPtr.Zero) return;

        // 閫昏緫 鈫?鐗╃悊鍙湪杩欓噷鍋氫竴娆★紙鐣岄潰鐨勫潗鏍囩郴姘歌繙鍙湁閫昏緫閭ｄ竴濂楋級銆?        var phys = new RectF
        {
            MinX = logical.MinX * UiUnit, MinY = logical.MinY * UiUnit,
            MaxX = logical.MaxX * UiUnit, MaxY = logical.MaxY * UiUnit,
        };

        if (_uiInputShown && phys.Equals(_uiInputRect)) return;

        Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(phys.MinX), (int)MathF.Floor(phys.MinY),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxX - phys.MinX)),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxY - phys.MinY)),
            Native.SWP_NOACTIVATE | (_uiInputShown ? 0 : Native.SWP_SHOWWINDOW));

        _uiInputRect = phys;
        _uiInputShown = true;
    }

    private void EnsureUiInputWindow()
    {
        if (_uiInputHwnd != IntPtr.Zero) return;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _uiInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachUiInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_uiInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("鐣岄潰鎺ヨ緭鍏ュ皬绐楀垱寤哄け璐? " + Marshal.GetLastWin32Error()
                              + "锛堥潰鏉垮湪绌块€忔ā寮忎笅浼氱偣涓嶅姩锛?);
            return;
        }

        // 1/255 鐨勪笉閫忔槑搴︼細鑲夌溂鐪嬩笉瑙侊紝浣嗗鍛戒腑娴嬭瘯鏉ヨ瀹?*瀹炲疄鍦ㄥ湪鍦板湪杩?*銆?        Native.SetLayeredWindowAttributes(_uiInputHwnd, 0, 1, Native.LWA_ALPHA);

        // 杩欎釜灏忕獥涔熺畻"鎴戜滑鐨勭獥鍙?锛氱郴缁熷彲鑳芥嬁瀹冩潵闂暱鎸夋墜鍔匡紙娑堟伅閭ｆ潯瑙?WndProc锛夛紝
        // 绐楀彛灞炴€х殑閭ｄ袱鏉¤鍦ㄨ繖閲岃ˉ涓€娆★紙鎵嬪娍鎸夌獥鍙ｇ畻锛屾紡涓€涓獥鍙ｅ氨婕忎竴鍧楀湴鏂癸級銆?        Native.DisableSystemPressAndHold(_uiInputHwnd);
    }

    /// <summary>
    /// 鎺ヨ緭鍏ュ皬绐楃殑娑堟伅銆傚畠涓嶇敾浠讳綍涓滆タ锛屽彧璐熻矗鎶婃寚閽堜簨浠剁炕鎴愮晫闈簨浠躲€?    /// 鍧愭爣涓€寰嬫槸**鐗╃悊灞忓箷鍧愭爣**鈥斺€?c>UiPointer*</c> 閭ｄ笁涓寘瑁呰礋璐ｉ櫎浠?DPI锛?    /// 璺熻鐩栧眰閭ｆ潯璺畬鍏ㄥ悓涓€濂楀彛寰勩€?    /// </summary>
    private IntPtr UiInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // 杩欏潡灏辨槸闈㈡澘鏈韩锛屼笉闇€瑕佸啀鍒ゆ柇"钀藉湪闈㈡澘閲岀殑鍝釜浣嶇疆"銆?            case Native.WM_NCHITTEST:
                _cntNcHitTest++; _cntNcHitClient++;
                return new IntPtr(Native.HTCLIENT);

            // 鐐归潰鏉夸笉璁告妸涓嬪眰绋嬪簭鐨勭劍鐐规姠璧帮紙鑰佸笀鐐逛竴涓嬫寜閽紝PPT 杩樻槸鍓嶅彴锛夈€?            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            // 鍏夋爣锛氬拰瑕嗙洊灞傞偅鏉¤矾**闂悓涓€浠藉垽鎹?*锛堟寚閽堝湪闈㈡澘涓?= 绠ご锛夈€?            // 涓嶇瓟杩欎竴鏉＄殑璇濈郴缁熶細鎷?*绫诲厜鏍?*鍏滃簳锛圢ULL 鈫?榛樿绠ご锛夛細闈㈡澘涓婄湅鐫€娌￠棶棰橈紝
            // 浣?闈㈡澘 鈫?鐢诲竷"鏉ュ洖璧版椂锛屾垜浠偅涓?宸茶杩囧氨涓嶉噸璁?鐨勭紦瀛樹細琚繖涓€娆?            // 鍏滃簳鎮勬倓浣滃簾锛堣 ApplyCursor 鐨勬敞閲婏級銆?            case Native.WM_SETCURSOR:
                ApplyCursor(force: true);
                return new IntPtr(1);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERENTER:
                _uiHover = true; _dirty = true; ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                _uiHover = false; _dirty = true; ApplyCursor();
                // 鐣岄潰閭ｆ潯"浜鸿蛋浜?鐢辫繖閲屽彂锛氭柟妗?B 涓嬶紝鎸囬拡绂诲紑闈㈡澘锛濈寮€杩欏潡鎺ヨ緭鍏ュ皬绐椼€?                UiGuard("PointerLeave", () => Ui.PointerLeave());
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntDown++;
                LastPointerType = ptype;
                _uiHover = true;

                if (UiPointerDown(sx, sy, pressure, ptype == Native.PT_PEN, inverted,
                                  ptype == Native.PT_TOUCH, id))
                {
                    // 鐣岄潰涔熻鎹曡幏锛氬湪鎸夐挳涓婃粦寮€銆佹嫋鍑洪潰鏉匡紝閮借缁х画鏀跺埌娑堟伅銆?                    Native.SetCapture(hWnd);
                }
                // 鐣岄潰娌″悆杩欎竴鍙ｄ篃**涓嶈惤澧ㄣ€佷笉閫忕粰涓嬪眰**锛歈ueryBounds 澹版槑鐨勫氨鏄?                // 鐣岄潰鐨勫湴鐩橈紙闈㈡澘搴曚笅鐨勫ⅷ鐪嬩笉瑙侊紝灏嗘潵闈㈡澘涓€鎸張鍐掑嚭鏉ワ級銆?                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntMove++;
                LastPointerType = ptype;
                _uiHover = true;
                UiPointerMove(sx, sy, pressure, ptype == Native.PT_PEN, inverted, id);
                _dirty = true;
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out float pressure,
                                out bool inverted, out _))
                {
                    StampInput(); _cntUp++;
                    UiPointerUp(sx, sy, pressure, false, inverted, id);
                }
                Native.ReleaseCapture();
                UiCapturing = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                UiCapturing = false;
                _uiHover = false;
                _dirty = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>鎺у埗鍙扮敤娉曡鏄庛€備骇鍝佺晫闈笉浼氳蛋杩欓噷銆?/summary>
    protected virtual void PrintUsageHelp()
    {
        Console.WriteLine();
        Console.WriteLine("InkTeach 鎵规敞鍘熷瀷宸插惎鍔ㄣ€傞敭浣嶏紙鍙厤缃紝瑙?" + InkSettings.FilePath + "锛夛細");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
        {
            Console.WriteLine("  锛佷笅闈㈣繖浜涢敭娌℃敞鍐屼笂锛堣鍒殑绋嬪簭鍗犱簡锛熸崲涓€涓敭锛夛細");
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    " + f);
        }
        Console.WriteLine();
    }

    // =====================================================================
    //  閫夋嫨鎵嬪娍锛堟閫夊伐鍏?= 閫夋嫨宸ュ叿锛?    // =====================================================================

    // ---- 鎷栧姩鏈熸敹瑁呴グ锛堟柟妗?A锛変笌鎷栧姩棰勮锛堟柟妗?B锛?-----------------------

    /// <summary>
    /// 鎷栧姩 / 鏃嬭浆鎵嬪娍閲?瑁呴グ璇ユ敹璧锋潵"鍚楋紙鏂规 A锛夈€?    ///
    /// 涓夋潯鍒ゆ嵁锛?    ///   路 姝ｅ湪鎷栵紙<see cref="SelDragging"/>锛夛紱
    ///   路 **鐪熺殑鍔ㄨ繃**锛堣秴杩?<see cref="ClickToleranceLogical"/>锛夆€斺€斿崟鍑讳竴涓嬩笉璇ラ棯锛?    ///   路 鏄?*绉诲姩鎴栨棆杞?* 鈥斺€?缂╂斁鎵嬪娍涓嶆敹锛氭嫋鏌愪釜鎵嬫焺鏃跺彟澶栧嚑涓墜鏌勬槸鏈夌敤鐨勫弬鐓э紝
    ///     鑰屽厜鏍囦篃宸茬粡璇存槑鎶撶殑鏄摢涓€涓€?    ///
    /// 涓轰粈涔堝彲浠ユ敹锛氭寜涓嬮偅涓€鍒绘寚閽堝氨琚?SetCapture 鎺ョ浜嗭紝鎵嬫焺涓庢搷浣滄潯
    /// **姝ゅ埢鏍规湰鐐逛笉涓?*锛岀敾鐫€灏辨槸"鐪嬪緱瑙併€佺偣涓嶅埌"銆?    /// 娉ㄦ剰锛?*鑴忓尯鐓ф棫绠?*锛堣 Overlay.ComputeTransientBounds锛夛紝闈犱袱甯у洖婧妸
    /// 娑堝け鐨勯偅涓€鍧楁摝骞插噣銆?    ///
    /// 2026-09-18 琛ワ細**鎷栫鐐逛篃鏀?*銆傝繖鏃跺€欐墜鏌勫仠鍦ㄦā鍨嬮噷锛堟棫浣嶇疆锛夛紝
    /// 鑰屼复鏃跺嚑浣曞凡缁忚窡鐫€鎸囬拡璧颁簡鈥斺€旂暀鐫€閭ｄ釜鏌勪細鎸囩潃涓€鏉＄嚎涓婃牴鏈病鏈夌殑鐐广€?    /// 鏀惰捣鏉ヤ箣鍚庣敾闈㈤噷鍙墿"涓存椂鍑犱綍 + 鍊炬枩瑙掕鏁?锛屾伆濂芥槸姝ゅ埢鍞竴鏈夌敤鐨勪袱浠朵笢瑗裤€?    /// </summary>
    internal bool SelChromeCollapsed
        => SelDragging && _selDragMoved
        && (_dragIsMove || _dragHandle == SelHandle.Rotate || _vertexDragging);

    /// <summary>鎷栧姩棰勮鍦ㄤ笉鍦細鎵嬪娍杩涜涓紝鑰屼笖鐪熺殑鏈変竴鎵瑰璞¤鎽樺嚭鏉ヤ簡銆?/summary>
    internal bool DragPreviewActive => SelDragging && !_vertexDragging && _detachOrdered.Count > 0;

    /// <summary>鎷栧姩棰勮瑕佺敾鐨勯偅涓€鎵癸紙鎸夋枃妗ｉ『搴忥紝鐢?<see cref="DragPreviewMatrix"/> 鍙樻崲锛夈€?/summary>
    internal IReadOnlyList<Stroke> DragPreviewStrokes => _detachOrdered;

    /// <summary>
    /// 鎷栧姩棰勮鐨勫疄鏃跺彉鎹紙鐢诲竷鍧愭爣锛夛紝涔樺湪瀵硅薄**鎸変笅閭ｄ竴鍒?*鐨勫彉鎹箣涓娿€?    ///
    /// 鍐呭灞傞偅鏉¤€佽矾浼氭妸瀵硅薄鍙樻崲鎴?鎸変笅鏃剁殑鍙樻崲 脳 杩欎釜鐭╅樀"锛岃繖閲屽乏涔樼殑姝ｆ槸瀹冿紝
    /// 鎵€浠ヤ袱绉嶇敾娉曞簲褰撳儚绱犱竴鑷达紙鑷閲屾湁涓€鏉′笓闂ㄧ洴杩欎欢浜嬶級銆?    /// </summary>
    internal Matrix3x2 DragPreviewMatrix => _selDragMatrix;

    /// <summary>杩欎竴鏉＄幇鍦ㄨ鎽樺嚭鍐呭灞備簡鍚楋紙鍐呭灞傞噸鐢讳竴鍧楁椂瑕佽烦杩囧畠锛夈€?/summary>
    internal bool IsContentDetached(Stroke s) => _detachSet.Count > 0 && _detachSet.Contains(s);

    // =====================================================================
    //  鎿嶄綔鏉★紙涔濇牸锛? 娴姩闈㈡澘 + 鎻愮ず鏉★細鐘舵€佷笌鍔ㄤ綔
    // =====================================================================

    /// <summary>閫夊尯姝ｅ湪"闂竴涓?鍚楋紙澶嶅埗鎴愬姛鐨勫弽棣堬級銆?/summary>
    internal bool SelFlashing => NowMs < SelFlashUntilMs;

    /// <summary>
    /// 鏀归€変腑瀵硅薄鐨勯鑹层€?*鑽у厜绗斾繚鐣欏崐閫忔槑**锛坄InkPalette.ToHighlighter`锛夛紝
    /// **鍥惧儚瀵硅薄璺宠繃**锛堝畠娌℃湁"棰滆壊"杩欎釜姒傚康锛夛紝鍏朵綑鎸夊悓涓€鑹插€肩粺鏀广€?    /// 杩斿洖鐪熸鏀规帀浜嗗嚑鏉★紙0 = 涓€鏉￠兘娌℃敼锛岃皟鐢ㄦ柟鎹缁欐彁绀猴級銆?    /// </summary>
    internal int SetSelectionColor(Color4 baseColor)
    {
        var targets = new List<Stroke>();
        var colors = new List<Color4>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            // 鑽у厜绗斾笌鏅€氬ⅷ杩瑰叡鐢ㄥ悓涓€涓?鍩鸿壊"锛屼絾鑽у厜绗旇杞垚鍗婇€忔槑鈥斺€?            // 鐩存帴鎸夊熀鑹插埛浼氭妸鑽у厜绗斿埛鎴愬疄蹇冿紙閭ｇ"瓒婃敼瓒婄碂"鐨勬晥鏋滐級銆?            // 瑙勫垯鍙湁涓€浠斤細`InkPalette.ForStroke`锛堣嚜瀹氫箟鍙栬壊鏉夸篃璧板畠锛夈€?            var c = InkPalette.ForStroke(baseColor, s);
            if (s.Color.Equals(c)) continue;
            targets.Add(s);
            colors.Add(c);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyColors(targets, colors.ToArray());   // 涓€鏉″姩浣?= 涓€姝ユ挙閿€
        // 灞忓箷鍙嶉鎸夌敤鎴疯姹?*鍙暀"閫変腑妗嗕笂鐪嬪緱瑙佺殑鍙樺寲"**锛堥鑹茬幆銆佹鐨勭矖缁嗭級锛?        // 涓嶅啀寮规彁绀烘潯锛涢渶瑕佺暀鐥曠殑鍐欐帶鍒跺彴锛堣繖涓€鐗堜竴鐩村姝わ級銆?        Console.WriteLine($"鏀归鑹诧細{changed} 涓璞?);
        return changed;
    }

    /// <summary>
    /// 鏀归€変腑瀵硅薄鐨勭矖缁嗭紙**閫昏緫**鍍忕礌妗ｄ綅锛屽唴閮ㄤ箻 DPI 鎹㈡垚鐗╃悊瀹藉害锛夈€?    /// 鍥惧儚瀵硅薄璺宠繃銆傝繑鍥炴敼浜嗗嚑鏉°€?    /// </summary>
    internal int SetSelectionWidth(float logicalWidth)
    {
        float w = logicalWidth * DpiScale;
        var targets = new List<Stroke>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (MathF.Abs(s.Width - w) < 0.01f) continue;
            targets.Add(s);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyWidths(targets, w);
        Console.WriteLine($"鏀圭矖缁嗭細{changed} 涓璞?鈫?{logicalWidth:F1} 閫昏緫鍍忕礌");
        return changed;
    }

    /// <summary>
    /// 鏀归€変腑瀵硅薄鐨勭嚎鍨嬶紙瑙?<see cref="StrokeDash"/>锛夈€傝繑鍥炴敼浜嗗嚑鏉°€?    ///
    /// **鍙烦杩囧浘鍍?*鈥斺€斿畠鏍规湰娌℃湁"鎻忚竟"杩欏洖浜嬨€?    ///
    /// 鑷敱绗旇抗**涔熺粰鏀?*锛堢敤鎴?2026-09-19 鏀圭殑鍙ｅ緞锛夛細鍘熸潵杩欓噷璺宠繃 Freehand锛?    /// 渚濇嵁鏄?鎵嬪啓铏氱嚎娌℃湁鎰忎箟"锛屼簬鏄€佸笀閫変腑涓€鏉＄瑪杩瑰啀鐐硅櫄绾?*涓€鐐瑰弽搴旈兘娌℃湁**
    /// 锛堥潰鏉块偅涓€琛岃繕涓嶉珮浜紝鍥犱负楂樹寒璇荤殑鏄?绗竴鏉￠潪鍥惧儚瀵硅薄鐨勭嚎鍨?锛夈€?    /// 鐢ㄦ埛鎶ョ殑"閫変腑浠ュ悗铏氱嚎闈㈡澘杩樻病鏈夊疄鐜?灏辨槸瀹冦€?    /// 娓叉煋閭ｈ竟**鏈潵灏辨敮鎸?*锛坄Gfx.StyleFor(s.Dash)` 鍦?DrawStrokeCore 涓€澶勭粺涓€鐢熸晥锛夛紝
    /// 鎵€浠ヨ繖鏉￠檺鍒朵竴鎾わ紝"铏氱嚎绗旇抗"浠庣敾鍒板瓨鍒板鍑哄叏閮介€氥€?    /// </summary>
    internal int SetSelectionDash(StrokeDash dash)
    {
        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (s.Dash == dash) continue;          // 宸茬粡鏄繖涓€妗ｏ紝涓嶇敤璁颁竴姝ョ┖鎾ら攢
            targets.Add(s);
        }
        if (targets.Count == 0) return 0;
        Doc.ApplyDash(targets, dash);              // 涓€鏉″姩浣?= 涓€姝ユ挙閿€
        Console.WriteLine($"鏀圭嚎鍨嬶細{targets.Count} 涓璞?鈫?{DashName(dash)}");
        return targets.Count;
    }

    /// <summary>
    /// 绾垮瀷鐨勪腑鏂囧悕锛堟帶鍒跺彴鐥曡抗鐢級銆?*鍙涓€浠?*鈥斺€旀暎鍦ㄥ悇澶勫張鏄竴浠?鍚屼竴浠朵簨鐨勪袱浠藉悕鍗?銆?    /// </summary>
    internal static string DashName(StrokeDash d) => d switch
    {
        StrokeDash.Dashed => "铏氱嚎",
        StrokeDash.Dotted => "鐐圭嚎",
        _ => "瀹炵嚎",
    };

    /// <summary>
    /// 鐣岄潰鑹插甫鏉′笂閭ｄ釜**铏氬疄绾垮垏鎹?*琚偣浜嗕竴涓嬶紙涓夋。杞祦锛岃 FullUi 鐨?DrawDashToggle锛夈€?    ///
    /// 瀹冩敼鐨勬槸**浠ュ悗鏂扮敾鐨勭瑪杩?*鐢ㄥ摢绉嶇嚎鍨嬶紝涓嶇宸茬粡鐢诲ソ鐨勶紙閭ｄ簺瑕侀€変腑涔嬪悗鍐嶆敼锛?    /// 璧?<see cref="SetSelectionDash"/>锛夆€斺€斿拰棰滆壊/绮楃粏鐨?鐢讳箣鍓嶉€夈€佺敾涔嬪悗鏀?涓ゆ潯璺竴鏍枫€?    /// </summary>
    internal void SetDashFromUi(StrokeDash dash)
    {
        if (PenDash == dash) return;
        PenDash = dash;
        Console.WriteLine($"绗旂殑绾垮瀷锛歿DashName(dash)}锛堜笅涓€绗斿紑濮嬬敓鏁堬級");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 閿佸畾 / 瑙ｉ攣閫変腑瀵硅薄锛堢敤鎴?2026-09-16 瀹氱殑璇箟锛?*閿佸畾鍚庤兘閫変腑銆佷絾鎷栦笉鍔?*锛夈€?    /// 鍏ㄩ儴鏈攣 鈫?鍏ㄩ攣锛涘叏閮ㄥ凡閿?鈫?鍏ㄨВ锛涙贩鍚?鈫?鍏ㄩ攣銆?    /// </summary>
    internal int ToggleSelectionLock()
    {
        if (Doc.Selected.Count == 0) return 0;
        bool anyUnlocked = false;
        foreach (var s in Doc.Selected) if (!s.Locked) { anyUnlocked = true; break; }
        bool lockTo = anyUnlocked;                 // 鏈夋病閿佺殑灏卞叏閿佷笂锛屽惁鍒欏叏瑙?
        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected) if (s.Locked != lockTo) targets.Add(s);
        int n = targets.Count;
        if (n > 0) Doc.ApplyLock(targets, lockTo);
        if (n > 0)
            Console.WriteLine(lockTo ? $"宸查攣瀹?{n} 涓璞? : $"宸茶В閿?{n} 涓璞?);
        return n;
    }

    /// <summary>灞傜骇锛氭妸閫変腑鐨勬暣浣撶疆椤?/ 缃簳锛堢敤鎴?2026-09-16 瀹氾細鍙仛杩欎袱涓級銆?/summary>
    internal bool ReorderSelection(bool toFront)
    {
        if (Doc.Selected.Count == 0) return false;
        if (Doc.Selected.Count >= Doc.Strokes.Count)        // 鍏ㄩ€夋椂缃《/缃簳娌℃湁鎰忎箟
        {
            Console.WriteLine("灞傜骇锛氭暣椤甸兘閫変腑浜嗭紝娌℃湁灞傜骇鍙皟");
            return false;
        }
        Doc.ReorderSelected(toFront);
        Console.WriteLine(toFront ? "灞傜骇锛氬凡缃《" : "灞傜骇锛氬凡缃簳");
        return true;
    }

    /// <summary>
    /// 鐐逛簡娴姩闈㈡澘涓婄殑涓滆タ銆傝繑鍥?true = 杩欎竴娆℃寜涓嬭闈㈡澘娑堣垂鎺変簡銆?    /// 闈㈡澘鐨勫嚑浣曞叏閮ㄦ潵鑷?<see cref="SelectionHandles"/>锛堟覆鏌撲笌鍛戒腑鍚屾簮锛夈€?    /// </summary>
    private bool HandlePanelClick(float x, float y)
    {
        var aabb = LiveSelectionFrame.CanvasAabb;
        float dpi = DpiScale;
        int sc = SelectionHandles.SwatchCount;
        var part = SelectionHandles.PanelPartAt(x, y, aabb, dpi, ViewportCanvas, SelPanelOpen, sc);

        switch (part)
        {
            case SelectionHandles.PanelPart.Slider:
                BeginWidthSliderDrag(x, aabb);
                return true;

            // 绾垮瀷涓夋牸锛堝疄绾?/ 铏氱嚎 / 鐐圭嚎锛夈€?*鏍煎簭灏辨槸 StrokeDash 鐨勫彇鍊?*锛?            // 鎵€浠?绗?i 鏍?鑳界洿鎺ヨ浆鎴愮嚎鍨嬶紝涓嶇敤鍐嶇淮鎶や竴寮犲鐓ц〃锛堣 StyleCellRect锛夈€?            case SelectionHandles.PanelPart.StyleSolid:
                SetSelectionDash(StrokeDash.Solid);
                return true;

            case SelectionHandles.PanelPart.StyleDashed:
                SetSelectionDash(StrokeDash.Dashed);
                return true;

            case SelectionHandles.PanelPart.StyleDotted:
                SetSelectionDash(StrokeDash.Dotted);
                return true;

            case SelectionHandles.PanelPart.LayerFront:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: true);
                return true;

            case SelectionHandles.PanelPart.LayerBack:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: false);
                return true;

            // 瀵煎嚭鏍煎紡锛? = PNG锛堥€忔槑搴曪級銆? = JPG锛堢櫧搴曪級銆?        }

        if (part >= SelectionHandles.PanelPart.SwatchBase)
        {
            int i = part - SelectionHandles.PanelPart.SwatchBase;
            var swatches = InkPalette.SelectionSwatches;
            if (i < 0 || i >= swatches.Length) return true;
            if (i == swatches.Length - 1)      // 鏈牸 = 鑷畾涔夊彇鑹诧紙8.2.0锛氬唴缃?HSV 灏忚壊鏉匡級
            {
                if (CustomColorOpen) CloseCustomColor();       // 鍐嶇偣涓€涓?= 鏀惰捣
                else OpenCustomColor();
                return true;
            }
            CloseCustomColor();                // 鐐逛簡甯歌鑹茬墖 = 涓嶇帺鑷畾涔変簡锛堝惁鍒欒壊鏉夸細鎸傚湪閭ｅ効锛?            SetSelectionColor(swatches[i].Color);
            return true;
        }
        return false;                          // 闈㈡澘鐨勭┖鐧藉锛氫粈涔堥兘涓嶅仛
    }

    /// <summary>
    /// 鐐逛簡**鑷畾涔夊彇鑹叉澘**涓婄殑涓滆タ锛堝畠鎸傚湪澧ㄨ抗闈㈡澘鏃佽竟锛屼紭鍏堢骇姣旈潰鏉挎湰韬珮锛夈€?    /// 杩斿洖 true = 杩欎竴娆℃寜涓嬭鑹叉澘娑堣垂鎺変簡锛堝寘鎷崱鐗囬噷娌℃帶浠剁殑绌虹櫧澶勨€斺€?    /// 閭ｄ笉绠?鐐瑰湪澶栭潰"锛屼笉瑙﹀彂鍙栨秷锛夈€?    /// </summary>
    private bool HandlePickClick(float x, float y)
    {
        var aabb = LiveSelectionFrame.CanvasAabb;
        var part = SelectionHandles.PickPartAt(x, y, aabb, DpiScale, ViewportCanvas,
                                               SelectionHandles.SwatchCount);
        switch (part)
        {
            case SelectionHandles.PickPart.Hue:
                BeginColorPickerDrag(x, y, aabb, onHue: true);
                return true;
            case SelectionHandles.PickPart.Sv:
                BeginColorPickerDrag(x, y, aabb, onHue: false);
                return true;
            case SelectionHandles.PickPart.Inside:
                return true;                   // 鍗＄墖绌虹櫧锛氬悆鎺夎繖涓€涓嬶紝浣嗕粈涔堥兘涓嶆敼
        }
        return false;
    }

    /// <summary>棰滆壊闈㈡澘閲?绮楃粏婊戞潯"褰撳墠鐨勬。浣嶈〃锛氬叏鑽у厜绗斿氨鐢ㄨ崸鍏夌瑪閭ｄ笁妗ｃ€?/summary>
    private float[] WidthStepsForSelection()
    {
        bool anyPen = false, anyHighlighter = false;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (s.Tool == Tool.Highlighter) anyHighlighter = true; else anyPen = true;
        }
        return (anyHighlighter && !anyPen) ? HighlighterWidthPresets : WidthPresets;
    }

    /// <summary>
    /// 棰滆壊闈㈡澘閲岄偅鏉＄矖缁嗘粦鏉＄殑**鍙栧€艰寖鍥?*锛堣繛缁粦鏉＄殑涓ょ锛岄€昏緫鍍忕礌锛夈€?    /// = 璇ユ。浣嶈〃鐨勯灏撅細鍏ㄨ崸鍏夌瑪 8..32銆佸叾浣?1..24銆?*婊戞潯澶瑰湪瀹冮噷闈?*鈥斺€?    /// 鑽у厜绗斾笉璁歌鎷栨垚瀹炲績缁嗙嚎锛堢敤鎴?2026-09-27 瀹氱殑"鐙珛涓€寮犱寒鑹茶〃"閭ｅ锛夈€?    /// </summary>
    internal (float Min, float Max) WidthRangeForSelection()
    {
        var steps = WidthStepsForSelection();
        return (steps[0], steps[^1]);
    }

    /// <summary>閫夊尯鍦ㄦ粦鏉′笂鐨勪綅缃紙0..1锛涘彇绗竴鏉￠潪鍥惧儚瀵硅薄锛涚┖閫夊尯/鑼冨洿閫€鍖?= 0锛夈€?/summary>
    internal float SliderTofSelection()
    {
        var (min, max) = WidthRangeForSelection();
        if (max <= min) return 0f;
        return Math.Clamp((SliderValueOfSelection() - min) / (max - min), 0f, 1f);
    }

    /// <summary>閫夊尯绗竴鏉￠潪鍥惧儚瀵硅薄鐨勫搴︼紙閫昏緫鍍忕礌锛涙病鏈?= 0锛夈€?/summary>
    internal float SliderValueOfSelection()
    {
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            return s.Width / DpiScale;
        }
        return 0f;
    }

    /// <summary>閫夊尯閲岀殑妗ｄ綅琛ㄩ暱搴︼紙婊戞潯閭ｆ帓鍙傝€冨埢搴︽湁鍑犱釜鐐癸級銆?/summary>
    internal int SliderStepCount() => WidthStepsForSelection().Length;

    /// <summary>
    /// 鎸変笅绮楃粏婊戞潯锛?*杩炵画鎷栧姩**寮€濮嬶紙8.2.0锛屼笉鍐嶆槸"鐐逛竴涓嬪惛鍒版。浣?锛夈€?    /// 鎸変笅杩欎竴涓嬪氨绔嬪埢鐢熸晥锛堝拰鏃ф墜鎰熶竴鑷达細鐐瑰摢鍎垮氨鏄摢鍎匡級锛屾嫋鍔ㄤ腑鍙嶅鏀广€?    /// 鏉炬墜鎵嶈繘鎾ら攢鏍堬紙<see cref="EndWidthSliderDrag"/>锛夈€?    /// </summary>
    private void BeginWidthSliderDrag(float x, in RectF aabb)
    {
        _propDragTargets.Clear();
        foreach (var s in Doc.Selected) if (!s.IsImage) _propDragTargets.Add(s);
        if (_propDragTargets.Count == 0) return;

        _sliderDragging = true;
        _widthDragValue = SliderValueAtX(x, aabb);

        // 鑽у厜绗旈€夊尯鐨勬粦鏉″彧缁?8..32锛堣 WidthRangeForSelection锛夆€斺€?        // 浠庡闈紙姣斿鏇寸矖鐨勬棫鏁版嵁锛夋嫋杩涙潵涔熶笉浼氳秺鐣屻€?        _widthDragStartValue = SliderValueOfSelection();
        _widthDragAction = new SetStrokePropAction(_propDragTargets, _widthDragValue * DpiScale);
        _widthDragAction.Redo(Doc);

        if (MathF.Abs(_widthDragValue - _widthDragStartValue) > 0.01f)
            Console.WriteLine($"鎷栫矖缁嗭細{_widthDragStartValue:F1} 鈫?{_widthDragValue:F1} 閫昏緫鍍忕礌");
        _dirty = true;
    }

    /// <summary>鎷栧姩涓細鎶婃粦鏉℃嫋鍒?x 澶勶紙杩炵画鍊硷紝澶瑰湪鑼冨洿鐨勭鐐逛笂锛夈€?/summary>
    private void DragWidthSliderTo(float x)
    {
        if (_widthDragAction == null) return;
        float v = SliderValueAtX(x, LiveSelectionFrame.CanvasAabb);
        if (MathF.Abs(v - _widthDragValue) < 0.05f) return;   // 娌″彉涓嶉噸鐢伙紙姣忓抚鍑犲崄娆?Redo 鏄櫧缁欙級
        _widthDragValue = v;
        _widthDragAction.RetargetWidth(v * DpiScale);
        _widthDragAction.Redo(Doc);
        _dirty = true;
    }

    /// <summary>鏉炬墜锛氫竴娆℃嫋鎷?= 涓€姝ユ挙閿€锛堝€肩湡鐨勫彉浜嗘墠璁帮級銆?/summary>
    private void EndWidthSliderDrag()
    {
        _sliderDragging = false;
        var act = _widthDragAction;
        _widthDragAction = null;
        _propDragTargets.Clear();
        if (act != null && act.HasChange)
        {
            Doc.CommitInteractive(act);
            Console.WriteLine($"鏀圭矖缁嗭細{act.TargetCount} 涓璞?鈫?{_widthDragValue:F1} 閫昏緫鍍忕礌");
        }
        _dirty = true;
    }

    /// <summary>x 鈫?婊戞潯涓婄殑杩炵画鍊硷紙閫昏緫鍍忕礌锛夛紝澶瑰湪 <see cref="WidthRangeForSelection"/> 閲屻€?/summary>
    private float SliderValueAtX(float x, in RectF aabb)
    {
        var (min, max) = WidthRangeForSelection();
        float t = SelectionHandles.SliderTAt(x, aabb, DpiScale, ViewportCanvas, SelectionHandles.SwatchCount);
        return min + (max - min) * t;
    }

    // ---- 鑷畾涔夊彇鑹叉澘锛?.2.0锛氳壊鐩告潯 + 楗卞拰搴?鏄庡害鏂瑰潡 + 褰撳墠鑹查瑙堬級--------

    /// <summary>
    /// 鐐瑰紑鑷畾涔夊彇鑹叉澘锛堣壊鏉挎渶鍚庝竴鏍硷級锛?*璧风偣 = 褰撳墠閫変腑澧ㄨ抗鐨勯鑹?*锛堟崲绠楁垚 HSV锛夛紝
    /// 杩欐牱鎵撳紑鏃堕瑙堣壊鍜屽ⅷ杩逛竴鑷达紝涓嶄細"涓€寮€灏辫烦鑹?銆傚叏閫夊浘鍍忔椂閫€鍥炴墜閲岄偅鏀瑪鐨勮壊銆?    /// </summary>
    private void OpenCustomColor()
    {
        CustomColorOpen = true;
        Color4 c = Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            c = s.Color;
            break;
        }
        (_pickH, _pickS, _pickV) = Hsv.FromRgb(c);
        _dirty = true;
    }

    /// <summary>
    /// 鍏虫帀鑷畾涔夊彇鑹叉澘銆?*鎷栧姩涓鎵撴柇鐨勮瘽锛屾妸杩樻病鎻愪氦鐨勯偅娆℃敼鍔ㄦ挙鍥炴潵**
    ///锛堢敤鎴峰畾鐨?鐐归潰鏉垮 = 鍙栨秷锛屼繚鎸佸師鑹?锛夈€?    /// </summary>
    private void CloseCustomColor()
    {
        if (!CustomColorOpen) return;
        CustomColorOpen = false;
        _colorDragging = false;
        if (_colorDragAction != null)
        {
            _colorDragAction.Undo(Doc);
            _colorDragAction = null;
        }
        _dirty = true;
    }

    /// <summary>鎸変笅鍙栬壊鏉匡細寮€濮嬩竴娆?瀹炴椂棰勮"鐨勬嫋鎷斤紙鏉炬墜鍦ㄦ澘鍐?= 搴旂敤锛屾澘澶?= 鍙栨秷锛夈€?/summary>
    private void BeginColorPickerDrag(float x, float y, in RectF aabb, bool onHue)
    {
        UpdatePickAtPoint(x, y, aabb, onHue);
        _colorDragging = true;
        _colorDragOnHue = onHue;

        _propDragTargets.Clear();
        foreach (var s in Doc.Selected) if (!s.IsImage) _propDragTargets.Add(s);
        if (_propDragTargets.Count == 0) { _colorDragAction = null; return; }

        var baseColor = Hsv.ToRgb(_pickH, _pickS, _pickV);
        var colors = new Color4[_propDragTargets.Count];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = InkPalette.ForStroke(baseColor, _propDragTargets[i]);
        _colorDragAction = new SetStrokePropAction(_propDragTargets, colors);
        _colorDragAction.Redo(Doc);
        _dirty = true;
    }

    /// <summary>鎸囬拡浣嶇疆 鈫?鍙栬壊鏉块噷鐨?HSV锛堟嫋鍝潡鏇存柊鍝潡锛夈€?/summary>
    private void UpdatePickAtPoint(float x, float y, in RectF aabb, bool hue)
    {
        int sc = SelectionHandles.SwatchCount;
        if (hue)
        {
            var r = SelectionHandles.PickHueRect(aabb, DpiScale, ViewportCanvas, sc);
            if (r.MaxY > r.MinY) _pickH = Math.Clamp((y - r.MinY) / (r.MaxY - r.MinY), 0f, 1f);
        }
        else
        {
            var r = SelectionHandles.PickSvRect(aabb, DpiScale, ViewportCanvas, sc);
            if (r.MaxX > r.MinX) _pickS = Math.Clamp((x - r.MinX) / (r.MaxX - r.MinX), 0f, 1f);
            if (r.MaxY > r.MinY) _pickV = Math.Clamp((y - r.MinY) / (r.MaxY - r.MinY), 0f, 1f);
        }
    }

    /// <summary>鎷栧姩鍙栬壊鏉匡細瀹炴椂鎶婃柊棰滆壊浣滅敤鍒伴€変腑瀵硅薄涓婏紙杩樻病杩涙挙閿€鏍堬級銆?/summary>
    private void DragColorPickerTo(float x, float y)
    {
        if (!_colorDragging) return;
        UpdatePickAtPoint(x, y, LiveSelectionFrame.CanvasAabb, _colorDragOnHue);
        if (_colorDragAction != null)
        {
            _colorDragAction.RetargetColor(Hsv.ToRgb(_pickH, _pickS, _pickV));
            _colorDragAction.Redo(Doc);
        }
        _dirty = true;
    }

    /// <summary>
    /// 鏉炬墜锛氭寚閽?*鍦ㄨ壊鏉块噷** = 杩欎竴娆￠€夊彇绠楁暟锛堜竴姝ユ挙閿€锛夛紱**鍦ㄥ闈?* = 鍙栨秷
    ///锛堟妸棰勮鎾ゅ洖鍘伙紝淇濇寔鍘熻壊锛夈€傜敤鎴?2026-09-30 瀹氱殑鍙ｅ緞锛?鐐归潰鏉垮 = 鍙栨秷锛堜繚鎸佸師鑹诧級"銆?    /// </summary>
    private void EndColorPickerDrag(float x, float y)
    {
        if (!_colorDragging) return;
        _colorDragging = false;
        var act = _colorDragAction;
        _colorDragAction = null;
        if (act == null) { _dirty = true; return; }

        bool inside = SelectionHandles.PickContains(x, y, LiveSelectionFrame.CanvasAabb, DpiScale,
                                                    ViewportCanvas, SelectionHandles.SwatchCount);
        if (inside && act.HasChange)
        {
            Doc.CommitInteractive(act);
            Console.WriteLine($"鑷畾涔夊彇鑹诧細{act.TargetCount} 涓璞?鈫?"
                              + $"RGB({_pickH:F2}, {_pickS:F2}, {_pickV:F2})");
        }
        else
        {
            act.Undo(Doc);                     // 鍙栨秷 / 娌″彉鍖栵細棰勮鎾ゅ洖鍘伙紝涓嶈鎾ら攢
        }
        _propDragTargets.Clear();
        _dirty = true;
    }


    /// <summary>
    /// 鎿嶄綔鏉′笂榧犳爣鎮仠鐨勬槸鍝竴鏍硷紙-1 = 娌″湪鏉′笂锛夈€?*鍙湪娌℃寜浣忔椂绠?*锛?    /// 鎷栧姩涓寚閽堟棭灏辩寮€鎸夐挳浜嗭紝閲嶇畻鍙細璁╅珮浜贡璺炽€?    ///
    /// 鍒ゆ嵁璧?<see cref="SelectionBarShown"/>锛堝拰缁樺埗銆佸厜鏍囧悓涓€鏉★級锛氶偅涓€鍧椾笉鍦ㄧ殑鏃跺€?    /// 鎮仠楂樹寒涔熶笉璇ヤ寒銆傛敹璧锋€侊紙鐢ㄦ埛鑷繁鏀剁殑 / 鐢诲畬鑷姩閫変腑閭ｄ釜妗嗭級鍙珮浜偅棰楀渾閽€?    /// </summary>
    private void UpdateBarHover(float x, float y)
    {
        int hover = -1;
        if (!_drawing && SelectionBarShown)
        {
            var aabb = LiveSelectionFrame.CanvasAabb;
            if (BarDrawnCollapsed)
            {
                var dot = SelectionHandles.BarCollapsedRect(aabb, DpiScale, ViewportCanvas);
                hover = dot.Contains(x, y) ? (int)SelBarButton.Collapse : -1;
            }
            else hover = SelectionHandles.BarButtonAt(x, y, aabb, DpiScale, ViewportCanvas);
        }
        if (hover == SelBarHover) return;
        SelBarHover = hover;
        _dirty = true;
    }

    /// <summary>
    /// 鎵嬪娍寮€濮嬶細鎶婅繖涓€鎵瑰璞′粠**鍐呭灞?*閲屾憳鍑哄幓锛堟柟妗?B锛夈€?    ///
    /// 涓夋锛屼唬浠?O(鏂囨。鏉℃暟)锛岃€屼笖涓€涓墜鍔垮彧鍋氫竴娆★細
    ///   鈶?鏀舵垚闆嗗悎锛堝唴瀹瑰眰閲嶇敾鏃跺垽瀹?O(1)锛夛紱
    ///   鈶?鎸夋枃妗ｉ『搴忔帓涓€浠斤紙鐢荤殑鏃跺€欒 z 搴忥紝闆嗗悎鐨勯亶鍘嗛『搴忎笉鏄?z 搴忥級锛?    ///   鈶?鎶婂畠浠?*鎸変笅閭ｄ竴鍒?*鍘嬭繃鐨勫潡鏍囪剰涓€娆?鈥斺€?閲嶇敾閭ｄ簺鍧楁椂璺宠繃杩欎竴鎵癸紝
    ///      浜庢槸"瀹冧滑鍘熸潵寰呯殑鍦版柟"鍦ㄥ唴瀹瑰眰閲屽綋鍦哄氨骞插噣浜嗭紱涔嬪悗鐨勬暣涓墜鍔挎湡闂?    ///      鍐呭灞備竴甯ч兘涓嶇敤鍐嶅姩锛堢浉鏈烘粴鍔ㄤ篃鐓ф棫鎴愮珛锛屽洜涓哄潡娲诲湪鐢诲竷绌洪棿锛夈€?    /// </summary>
    private void DetachForDrag()
    {
        _detachSet.Clear();
        _detachOrdered.Clear();
        foreach (var s in _dragTargets) _detachSet.Add(s);
        if (_detachSet.Count > 0)
        {
            foreach (var s in Doc.Strokes)
                if (_detachSet.Contains(s)) _detachOrdered.Add(s);
            foreach (var s in _dragTargets) Doc.InvalidateContent(s.PaddedBounds);
        }
        _dragPrevBounds = PreviewBounds(Matrix3x2.Identity);
    }

    /// <summary>鎵嬪娍缁撴潫锛氳繖涓€鎵瑰洖鍒板唴瀹瑰眰锛堜笅涓€娆￠噸鐢诲氨甯︿笂瀹冧滑浜嗭級銆?/summary>
    private void ReattachAfterDrag()
    {
        _detachSet.Clear();
        _detachOrdered.Clear();
        _dragPrevBounds = RectF.Empty;
        _selDragMoved = false;
    }

    // ---------------------------------------------------------------------
    //  鍥惧舰绔偣缂栬緫锛氳捣鎵?/ 姣忓抚 / 鎻愪氦
    // ---------------------------------------------------------------------

    /// <summary>
    /// 瀹氫箟鍏冪礌鎵嬫焺鎸変笅锛氳捣涓€娆?鏀瑰嚑浣?鐨勬墜鍔裤€?    ///
    /// 澶嶇敤 <see cref="DetachForDrag"/> 閭ｅ锛?*鍙湪杩欎竴鍒?*鎶婅繖鏉″浘褰㈠帇杩囩殑鍧楁爣鑴忎竴娆°€?    /// 骞朵笖璁╁唴瀹瑰眰鍦ㄩ噸鐢绘椂璺宠繃瀹冣€斺€斾簬鏄畠鍘熸潵寰呯殑鍦版柟褰撳満灏卞共鍑€浜嗭紝
    /// 涔嬪悗鏁翠釜鎵嬪娍鏈熷唴瀹瑰眰涓€甯ч兘涓嶇敤鍐嶅姩锛堣嚜妫€閲?鍐呭灞備竴甯т笉閲嶇敾"鐩殑灏辨槸杩欐潯锛夈€?    /// </summary>
    private void BeginVertexDrag(Stroke s, ShapeHandle h, float x, float y)
    {
        _vertexDragging = true;
        _vertexTarget = s;
        _vertexHandle = h;
        // 鏍兼暟**涓嶈兘鍙湅"鐜板湪鏈夊嚑涓偣"**锛氬弻鏇茬嚎鐨勭涓変釜鐐癸紙"鏇茬嚎缁忚繃鐨勯偅涓偣"锛夋槸**鍙己鐨?*
        // 锛堝垰璧锋墜銆佹垨杩佺Щ鍓嶇殑鑰佸璞″氨鍙湁涓や釜锛夛紝鑰?鎷栨洸绾夸笂鐨勯偅涓偣"杩欎釜鎵嬫焺鐓ф牱瑕佽兘鎸変笅鈥斺€?        // 鎸変笅灏卞緱寰€绗笁鏍煎啓锛屾牸鏁颁笉澶熷氨鏄?*鏁扮粍瓒婄晫**锛堣嚜妫€閲屽綋鍦哄穿杩囦竴娆★級銆?        // 鎻愪氦鏃朵細鎶婅繖绗笁鏍煎啓杩涙ā鍨嬶紙ApplyGeometry锛夛紝瀵硅薄涔熷氨浠庝袱涓偣琛ユ垚涓変釜銆?        _vertexPreviewLocal = new Vector2[Math.Max(s.Points.Count, Stroke.MinCurvePoints(s.Kind))];
        _vertexPrevBounds = RectF.Empty;
        _vertexPreviewCanvas = SelectionHandles.ShapeHandleCanvasPosition(s, h);
        // "杩欎竴鎷栦笉鏄嫋鏍艰窛"锛? = 娌″湪鎷栵紱鏍艰窛鑷繁涓嶈鏄?0锛岃閭ｄ釜瀛楁鐨勬敞閲婏級銆?        _vertexPreviewGridStep = 0f;
        // 棰勮鍒濆灏辨槸鍘熸牱锛堣繖涓椂鍊欒繕娌″姩锛岀敾闈笉璇ユ湁浠讳綍鍙樺寲锛夈€?        WriteVertexLocalPoints(_vertexPreviewCanvas);
        UpdateVertexReadout();
        _vertexSnapped = false;
        _shapeSnap = ShapeSnapKind.None;
        _shapeSnapAnchor = _vertexPreviewCanvas;

        _dragHandle = SelectionHandles.SelHandleOf(h);
        _dragIsMove = false;
        _dragStartPoint = new Vector2(x, y);
        _dragTargets = new[] { s };
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        DetachForDrag();

        SelDragging = true;
        _dirty = true;
    }

    /// <summary>
    /// 鎷栧姩涓細**妯″瀷涓€涓瓧涓嶆敼**鈥斺€斿彧鍦?    /// <see cref="_vertexPreviewLocal"/> / <see cref="_vertexPreviewCanvas"/> 閲屾敀鍑?    /// "涓存椂鍑犱綍"锛岀敱娴姩灞傛瘡甯х敾鍑烘潵锛?see cref="OverlayWindow"/> 鐨?DrawVertexPreview锛夈€?    ///
    /// 鍧愭爣绯绘湁涓ゅ眰锛岄敊涓€灞傚厓绱犲氨浼氶锛氭寚閽堝拰鎵嬫焺鍦?*鐢诲竷鍧愭爣**锛?    /// 鑰屽畾涔夊厓绱犲瓨鍦ㄥ璞＄殑**灞€閮ㄥ潗鏍?*閲岋紝涓棿闅旂潃 `Transform`锛堟棆杞繃鐨勫浘褰㈠繀椤昏繃
    /// `Transform鈦宦筦 鎵嶈兘鏀圭偣锛夈€?    ///
    /// 鍚搁檮鏈?*涓ゅ**锛屽悇绠″悇鐨勶細
    ///   路 鐩寸嚎 / 绠ご 鈫?**鏂瑰悜**鍚搁檮锛堢粫鍙︿竴涓鐐硅浆銆佷繚鎸侀暱搴︼級锛?    ///   路 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰?鈫?**鐗规畩褰㈢姸**鍚搁檮锛堟鍦?/ 绛夎叞 / 绛夎竟 / 鐩磋 /
    ///     鑿卞舰 / 鐭╁舰锛夛紝鍦?<see cref="WriteVertexLocalPoints"/> 閲屽仛锛?    ///     璇箟鏄?鎶婅鎷栫殑閭ｄ釜鐐逛慨姝ｅ埌鎭板ソ婊¤冻绾︽潫鐨勪綅缃?锛堣鏍?9.6锛夈€?    /// 涓ゅ閮藉彲鑳藉悓鏃?鍚镐綇"锛屾墍浠ヨ兌鍥婁笂鐨勫瓧鐢?<see cref="ShapeSnapKind"/> 涓?    /// 伪 璇绘暟鍒嗗埆璐熻矗锛屼簰涓嶈鐩栥€?    /// </summary>
    private void UpdateVertexDrag(float x, float y)
    {
        var s = _vertexTarget;
        if (s == null) return;

        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        var canvasPoint = new Vector2(x, y);
        bool snapped = false;
        if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
        {
            // "瀵归潰閭ｄ釜绔偣"锛氭嫋鍝竴澶村氨缁曞彟涓€澶磋浆锛屾墠绗﹀悎"鎴戝垰鎷栫殑杩欏ご鍔ㄣ€侀偅澶翠笉鍔?銆?            var fixedHandle = _vertexHandle == ShapeHandle.Anchor ? ShapeHandle.Rim : ShapeHandle.Anchor;
            var fixedPoint = SelectionHandles.ShapeHandleCanvasPosition(s, fixedHandle);
            canvasPoint = SelectionHandles.SnapEndPoint(fixedPoint, canvasPoint, shift, alt, out snapped);
        }

        // 鑴忓尯鍙爣"琚嫋鐨勯偅涓厓绱?*璧拌繃鐨勯偅涓€灏忔**"锛氭棫銆佹柊涓や釜灏忕煩褰㈠垎寮€鍔?        // 锛堜笂涓€甯?鈭?杩欎竴甯э紝鍜屾嫋鍔ㄩ瑙堝悓涓€濂楄处锛夈€傛暣鏉＄嚎鐨勫寘鍥寸洅妯法灞忓箷鏃跺嚑涔庢槸鏁村睆锛?        // 鑰岃繖閲屽姩鐨勫叾瀹炲彧鏈変竴涓厓绱犫€斺€旀寜鏁存潯绠楃瓑浜庢瘡甯х櫧閲嶇敾涓€澶х墖銆?        if (!_vertexPrevBounds.IsEmpty) Doc.Dirty.Add(_vertexPrevBounds);
        var prevCanvas = _vertexPreviewCanvas;
        _vertexPreviewCanvas = canvasPoint;
        _vertexSnapped = snapped;
        _shapeSnapAnchor = canvasPoint;         // 鍚镐綇鏃惰兌鍥婅创鍦ㄨ鎷栫殑閭ｄ釜鍏冪礌涓?        WriteVertexLocalPoints(canvasPoint);    // 閲岄潰椤烘墜鍒?鐗规畩褰㈢姸鍚搁檮"锛堣 9.6锛?        _vertexPrevBounds = EndpointDirtyRect(prevCanvas, canvasPoint, s.Width * 0.5f + 2f);
        if (!_vertexPrevBounds.IsEmpty) Doc.Dirty.Add(_vertexPrevBounds);

        UpdateVertexReadout();

        if (!_selDragMoved
            && Vector2.Distance(new Vector2(x, y), _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;
        _dirty = true;
    }

    /// <summary>
    /// 鏉炬墜锛氭妸鏀掍簡涓€璺殑涓存椂鍑犱綍**鎻愪氦鎴愪竴姝ユ挙閿€**锛堟敼鐨勬槸鍑犱綍锛屼笉鏄彉鎹級銆?    ///
    /// 涓€鐐规病绉诲姩灏变笉鎻愪氦鈥斺€旀寜涓€涓嬬鐐规墜鏌勬湰鏉ヤ細澶氬嚭涓€鏉?鍘熸牱"鐨勬挙閿€璁板綍銆?    /// </summary>
    private void CommitVertexDrag()
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        bool moved = _selDragMoved;
        // 杩欎竴鎷栨槸涓嶆槸"鎷栫劍鐐逛笁瑙掑舰鐨勯《鐐?P"锛堣 _vertexPreviewFocusU锛夆€斺€?        // 鍙栧€艰鍦?*娓呭瓧娈典箣鍓?*锛岃€屼笖鍙湁鐪熺殑鍔ㄨ繃鎵嶇畻鏁般€?        float focusU = _vertexPreviewFocusU;
        bool focusDrag = moved && s != null && !float.IsNaN(focusU);
        // 鍚屼竴涓亾鐞嗭細杩欎竴鎷栨槸涓嶆槸"鎷栧潗鏍囩郴鐨勬牸璺濇墜鏌?锛堣 _vertexPreviewGridStep锛夈€?        float gridStep = _vertexPreviewGridStep;
        bool gridDrag = moved && s != null && gridStep > 0f;

        _vertexDragging = false;
        _vertexTarget = null;
        _vertexPreviewLocal = null;
        _vertexPreviewFocusU = float.NaN;       // 鎵嬪娍缁撴潫锛氶瑙堝瓧娈靛綊浣?        _vertexPreviewGridStep = 0f;
        _vertexPrevBounds = RectF.Empty;
        _dragTargets = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _selDragMoved = false;
        _shapeSnap = ShapeSnapKind.None;        // 鎵嬪娍缁撴潫锛氳兌鍥婅窡鐫€娑堝け

        // 鎷?P锛?*鐐硅〃涓€涓瓧娌″彉**锛圥 涓嶆槸鎺у埗鐐癸級锛屽彧鏈夐偅涓弬鏁拌瑕佸啓
        //锛堟挙閿€瑕佷笉瑕佽窡鐫€鍥烇紝瑙?SetStrokeGeometryAction 鐨?newFocusU锛夈€?        // 鈿?浼犵殑鏄?`float?`锛氬彧鏈?鐪熺殑鍦ㄦ嫋 P"鎵嶅甫鍊硷紝鍒殑鏀瑰嚑浣曞姩浣滀紶 null锛堜竴涓瓧閮戒笉鍔?P锛夈€?        if (focusDrag) Doc.ApplyGeometry(s, pts, focusU);
        // 鎷栨牸璺濓細鍚屼竴涓璺€斺€旂偣琛ㄤ竴涓瓧娌″彉锛堟牸璺濅笉鏄帶鍒剁偣锛夛紝鍙湁閭ｄ竴涓暟瑕佸啓銆?        else if (gridDrag) Doc.ApplyGeometry(s, pts, null, gridStep);
        else if (moved && s != null && pts != null) Doc.ApplyGeometry(s, pts);
        // 涓€鐐规病鍔紙鍙槸鐐逛簡涓€涓嬬鐐规墜鏌勬澗鎵嬶級锛?*涔熷繀椤绘妸瀹冨帇杩囩殑閭ｅ潡閲嶇敾涓€娆?*鈥斺€?        // 璧锋墜閭ｄ竴涓嬪畠宸茬粡浠庡唴瀹瑰眰鎽樺嚭鍘讳簡锛屼笉閲嶇敾鐨勮瘽杩欎竴鍧楀氨涓€鐩存槸"娌℃湁杩欐潯绾?锛?        // 灞忓箷涓婄殑绾夸細褰撳満娑堝け锛堟澗鎵嬪悗妯″瀷鍏跺疄浠€涔堥兘娌″彉锛夈€?        // 璧?InvalidateContent 鑰屼笉鏄?Dirty.Add锛氬唴瀹瑰眰鍙湪**鐗堟湰鍙峰彉浜?*鏃舵墠娑堣垂鑴忓尯
        // 锛堣閭ｄ釜鍑芥暟鐨勬敞閲婏級锛屽皯鎶竴娆＄増鏈彿灏辩瓑浜庣櫧鏍囥€?        else if (s != null) Doc.InvalidateContent(s.PaddedBounds);

        // 鍥炲埌鍐呭灞傦細鎻愪氦閭ｄ竴姝ュ凡缁忔妸鏃т綅涓庢柊浣嶉兘鏍囪剰浜嗭紝鎵€浠ヨ繖涓€甯х殑閲嶇敾涓€瀹氫細
        // 甯︿笂瀹冿紙鑰屼笉鏄户缁敾棰勮锛夈€?        ReattachAfterDrag();
        _dirty = true;
    }

    /// <summary>
    /// 鎶?琚嫋鐨勫畾涔夊厓绱犺惤鍦ㄧ敾甯冨潗鏍?<paramref name="canvasPoint"/>"鍐欒繘棰勮鐨勫眬閮ㄧ偣銆?    ///
    /// 杩欓噷鏄?*鍏ㄩ儴鎷栧姩璇箟**鎵€鍦細鍚屼竴涓嚱鏁拌浼哄€欏叚绉嶅璞?    /// 锛堢洿绾?绠ご鏀圭鐐广€佸渾鏀瑰崐寰勩€佸渾蹇冨钩绉汇€佹き鍦嗘敼涓€鏉″崐杞淬€佷笁瑙掑舰/骞宠鍥涜竟褰㈡敼椤剁偣锛夛紝
    /// 鎵€浠ユ寜 `(Kind, 鎵嬫焺)` 鍒嗘淳锛岃€屼笉鏄?鏇挎崲绗?N 涓偣"銆?    ///
    /// 涓夋潯纭鐭╋細
    ///   路 鎸囬拡鍦?*鐢诲竷鍧愭爣**銆佺偣瀛樺湪**灞€閮ㄥ潗鏍?*锛屽繀椤昏繃 `Transform鈦宦筦锛堟棆杞繃鐨勫璞″挨鍏讹級锛?    ///   路 鎷栧渾蹇?涓績鏃?*涓や釜鐐逛竴璧峰钩绉?*鈥斺€斿崐寰?/ 鍗婅酱鍥犳閫愪綅涓嶅彉
    ///     锛堣繖涔熸槸"鎷栧渾蹇?= 骞崇Щ"鐨勫垽鎹級锛?    ///   路 **鐗规畩褰㈢姸鍚搁檮**锛堣鏍?9.6锛夊彧鍦ㄨ繖鏉¤矾涓婂仛锛氬畠鏀圭殑鏄?琚嫋鐨勯偅涓偣"锛?    ///     鑰屼笉鏄舰鐘剁殑绉嶇被銆俙Alt` 浼犺繘鍘诲氨鍙樻垚瀹屽叏鑷敱銆?    /// </summary>
    private void WriteVertexLocalPoints(Vector2 canvasPoint)
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        // 鎷风殑鏄?妯″瀷閲?*鐜板湪鏈夌殑**鐐?锛岃€?`pts` 鍙兘**姣斿畠闀?*锛堝弻鏇茬嚎閭ｄ釜鍙己鐨勭涓変釜鐐癸紝
        // 瑙?BeginVertexDrag锛夆€斺€旈暱鍑烘潵鐨勯偅鍑犳牸鍏堢暀闆跺悜閲忥紝鐢变笅闈㈢殑鍒嗘敮鎸夐渶濉€?        for (int i = 0; i < pts.Length; i++)
            pts[i] = i < s.Points.Count ? new Vector2(s.Points[i].X, s.Points[i].Y) : Vector2.Zero;

        var local = SelectionHandles.ToLocalPoint(canvasPoint, s.Transform);
        var c = s.ShapeCenterLocal;
        float minAxis = ShapeMinAxisLogical * DpiScale;
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        float lenTol = SelectionHandles.ShapeSnapLengthToleranceLogical * DpiScale;
        _shapeSnap = ShapeSnapKind.None;        // 姣忓抚閲嶅垽锛氫笉鍚哥殑甯у繀椤诲洖鍒?娌℃湁"

        switch (s.Kind)
        {
            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
            {
                // 椤剁偣锛?*鎷栧摢涓彧鍔ㄥ摢涓?*锛岀鍥涗釜瑙掞紙骞宠鍥涜竟褰級姘歌繙鏄畻鍑烘潵鐨勩€?                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= pts.Length) break;
                pts[idx] = SelectionHandles.SnapPolygonVertex(
                    s.Kind, idx, pts[0], pts[1], pts[2], local, lenTol, alt, out _shapeSnap);
                break;
            }

            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
            {
                // 瀹氫箟鍏冪礌**鍚勬嫋鍚勭殑**鈥斺€旇繖姝ｆ槸"瀹冧滑閮芥槸鐪熺殑瀹氫箟鍏冪礌"閭ｄ欢浜嬬殑鍏戠幇銆?                // 杩欓噷娌℃湁"鐗规畩褰㈢姸鍚搁檮"锛氬潗鏍囩郴 / 鏁拌酱娌℃湁绛夎叞銆佺洿瑙掗偅涓€绫荤殑绾︽潫锛?                // `_shapeSnap` 灏变竴鐩存槸 None锛堜笂闈㈠凡缁忛噸缃繃锛夈€?                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0) break;

                // **绗?4 棰?= 鏍艰窛鎵嬫焺**锛堢敤鎴?2026-09-24锛?鎷栧姩閭ｄ釜鐐规潵鏀瑰彉杩欎釜鏂规牸鐨勫ぇ灏?锛夛細
                // 瀹?*涓嶆槸鎺у埗鐐?*锛屾墍浠ヨ繖涓€鏀鍒ゅ湪"鐐规暟"閭ｉ亾闂?*涔嬪墠**锛堝潗鏍囩郴鍙湁涓変釜鐐癸級锛?                // 鑰屼笖**涓€涓偣閮戒笉鍔?*鈥斺€斿妗嗐€佸師鐐归兘淇濇寔鍘熸牱锛屾敼鐨勫彧鏈夋牸璺濄€?                if (idx == 3 && s.Kind == StrokeKind.Coordinate)
                {
                    // 鎵嬫焺钀藉湪"鍘熺偣 + (鏍艰窛, 鈭掓牸璺?"涓婏紝鎵€浠ヤ袱涓柟鍚戠殑鍋忕Щ閲忛兘绛変簬鏍艰窛锛?                    // 鍙栧钩鍧囷紙= 鎵撳埌閭ｆ潯瀵硅绾夸笂鐨勬姇褰憋級鑰屼笉鏄崟鐪嬫煇涓€杞达細鎷栧姩鏂瑰悜鍋忎竴鐐逛篃涓嶄細璺炽€?                    var d = local - pts[2];
                    float want = (MathF.Abs(d.X) + MathF.Abs(d.Y)) * 0.5f;
                    _vertexPreviewGridStep = Math.Clamp(want,
                        Stroke.AxisMinGridStepLocal,
                        Stroke.AxisMaxGridStepLocal(pts[0], pts[1]));
                    break;
                }

                if (idx >= pts.Length) break;
                float minLen = ShapeMinAxisLogical * DpiScale;

                if (s.Kind == StrokeKind.NumberLine)
                {
                    // **鏁拌酱鐨勪袱涓鐐?*锛氬彧鏀?x锛寉 涓€寰嬩繚鎸?鈥斺€?寰€鏂滀笂鏂规嫋涔熻繕鏄按骞崇嚎
                    // 锛堟暟杞存浜嗗氨涓嶆槸鏁拌酱浜嗭級銆傚悓鏃朵笉璁镐簰鐩歌秺杩囷細瓒婅繃鍘?宸︾/鍙崇"
                    // 杩欎袱涓悕瀛楀氨鎾掕皫浜嗭紝鎵嬫焺涔熶細浜掓崲浣嶇疆銆?                    float x = local.X;
                    if (idx == 0) x = MathF.Min(x, pts[1].X - minLen);
                    else x = MathF.Max(x, pts[0].X + minLen);
                    pts[idx] = new Vector2(x, pts[0].Y);
                    break;
                }

                if (idx == 2)
                {
                    // **鍘熺偣**锛氬湪澶栨閲岄殢渚挎尓锛堣€佸笀鏈€甯哥敤鐨勫姩浣滃氨鏄?妗嗙敾瀹屼簡锛?                    // 鎶婂師鐐规嫋鍒板乏涓嬭锛屽彧鐣欑涓€璞￠檺"锛夈€?                    // 澶瑰湪澶栨鍐呮槸蹇呴』鐨勶細璺戝埌妗嗗锛屼袱鏉¤酱灏遍兘涓嶅湪妗嗛噷浜ゅ弶浜嗭紝
                    // 鐢诲嚭鏉ユ槸涓涓嶆竻鐨勪笢瑗匡紙鑰岃创鍦ㄦ瑙掍笂姝ｆ槸鎯宠鐨勯偅绉嶇敤娉曪級銆?                    float fx0 = MathF.Min(pts[0].X, pts[1].X), fx1 = MathF.Max(pts[0].X, pts[1].X);
                    float fy0 = MathF.Min(pts[0].Y, pts[1].Y), fy1 = MathF.Max(pts[0].Y, pts[1].Y);
                    pts[2] = new Vector2(Math.Clamp(local.X, fx0, fx1), Math.Clamp(local.Y, fy0, fy1));
                }
                else
                {
                    pts[idx] = local;                 // 澶栨浠绘剰涓€瑙掗兘鑳芥嫋锛堟敼鑼冨洿锛?                }
                break;
            }

            case StrokeKind.Circle when _vertexHandle == ShapeHandle.Rim:
            {
                // 鍦嗙殑鍦嗗懆鐐癸細**鍙敼鍗婂緞锛屽渾蹇冮拤浣?*銆傚崐寰勫湪灞€閮ㄥ潗鏍囬噷閲忥紙涓ょ偣璺濈锛夛紝
                // 鏂瑰悜鐓ф寚閽堣蛋锛涘お灏忓氨澶瑰埌鏈€灏忓€硷紙涓嶇劧鍦嗛€€鍖栨垚涓€鐐癸紝鐪嬩笉瑙佽繕鐐逛笉涓級銆?                var dir = local - c;
                float r = dir.Length();
                if (r < minAxis)
                    local = c + (r > 1e-3f ? dir / r : new Vector2(1f, 0f)) * minAxis;
                pts[1] = local;
                break;
            }

            case StrokeKind.Circle when _vertexHandle == ShapeHandle.Anchor:
            {
                // 鍦嗗績锛?*鏁存潯骞崇Щ**锛堝崐寰勯€愪綅涓嶅彉锛夈€?                // 姝ｅ父璺緞涓婃嫋瀹冭蛋鐨勬槸"鏁翠綋鎷栧姩"锛堟敼鍙樻崲鐭╅樀锛岃 TryBeginSelectionGesture锛夛紝
                // 杩欓噷鍙槸鍏滃簳鈥斺€斾竾涓€鍝ぉ鐪熶粠杩欐潯璺繘鏉ワ紝璇箟涔熷繀椤绘槸"骞崇Щ"鑰屼笉鏄?鏀逛竴涓偣"銆?                //
                // `when` 蹇呴』鍐欏湪杩欓噷锛歚case A: case B when 鏉′欢:` 鐨勬潯浠?*鍙 B**锛?                // A 浼氭棤鏉′欢鍛戒腑锛堣繖涓€鏉″湪涓嬮潰 sine/cosine 閭ｆ涔熶笓闂ㄦ彁閱掕繃锛夈€?                var d = local - c;
                pts[0] = c + d;
                pts[1] = s.RimLocalPoint() + d;
                break;
            }

            case StrokeKind.Ellipse when _vertexHandle == ShapeHandle.AxisRight:
            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.AxisRight:
            {
                // 鍙崇鐐癸細**鍙敼 a**锛宐 淇濇寔锛堣繖灏辨槸"姣忔鍙姩閭ｄ竴鏉?锛夈€?                // 寰€宸︽嫋杩囦腑蹇冧篃鐓ф牱缂╋紙璐熸暟琚笅闈㈢殑 minAxis 鍗′綇锛夛紝
                // 鎵€浠?鎯崇缉宸﹁竟"涓嶉渶瑕佸彟涓€涓墜鏌勨€斺€旀姄鍙崇鐐逛竴璺線宸︽嫋灏辫銆?                // 鈿?涓や釜 case 閮藉甫 `when`锛歚case A: case B when 鏉′欢:` 鐨勬潯浠?*鍙 B**锛堣€佽处锛夈€?                float a = MathF.Max(minAxis, local.X - c.X);
                // 姝ｅ渾鍚搁檮锛堣鏍?9.6锛夛細|a 鈭?b| 鍦ㄥ宸唴灏卞彇鎴?b 鈥斺€?浜庢槸 a銆乥 閫愪綅鐩哥瓑銆?                a = SelectionHandles.SnapEllipseAxis(a, s.SemiAxisBLocal, lenTol, alt, out bool snapA);
                if (snapA) _shapeSnap = ShapeSnapKind.Circle;
                pts[1] = new Vector2(c.X + a, c.Y + s.SemiAxisBLocal);
                break;
            }

            case StrokeKind.Ellipse when _vertexHandle == ShapeHandle.AxisTop:
            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.AxisTop:
            {
                // 涓婄鐐癸細**鍙敼 b**锛宎 淇濇寔锛堝悓鐞嗭紝寰€涓嬫嫋杩囦腑蹇冧篃鑳界缉锛夈€?                float b = MathF.Max(minAxis, c.Y - local.Y);
                // 姝ｅ渾鍚搁檮锛堣鏍?9.6锛夛細|b 鈭?a| 鍦ㄥ宸唴灏卞彇鎴?a銆?                b = SelectionHandles.SnapEllipseAxis(b, s.SemiAxisALocal, lenTol, alt, out bool snapB);
                if (snapB) _shapeSnap = ShapeSnapKind.Circle;
                pts[1] = new Vector2(c.X + s.SemiAxisALocal, c.Y + b);
                break;
            }

            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.FocusPoint:
            {
                // **鐒︾偣涓夎褰㈢殑椤剁偣 P**锛氬畠**涓嶅湪 pts 閲?*锛圥 鏄弬鏁拌绠楀嚭鏉ョ殑锛?                // 涓嶆槸鎺у埗鐐癸紝瑙?ShapeHandle.FocusPoint锛夛紝鎵€浠ヨ繖閲屼竴涓偣閮戒笉鍐欙紝
                // 鍙妸"鎷栧埌鍝効"鎶樻垚鍙傛暟瑙掕鍒伴瑙堝瓧娈甸噷鈥斺€旀诞鍔ㄥ眰鎷垮畠鐢婚瑙堬紝
                // 鏉炬墜鐢?CommitVertexDrag 鍐欒繘瀵硅薄锛堥偅涓€姝ユ墠杩涙挙閿€鏍堬級銆?                _vertexPreviewFocusU = s.ConicEllipseAngleOf(local);
                break;
            }

            // 鍥涚鏇茬嚎锛堟姏鐗╃嚎 / 鍙屾洸绾?/ 姝ｅ鸡 / 浣欏鸡锛夌殑鐗规畩鐐规墜鏌?2026-09-20 鍏ㄧ爫浜?            //锛堢敤鎴凤細"閫氶€氭寜甯歌鎿嶄綔锛岀粰鎿嶄綔鏌勫拰鏃嬭浆"锛夛紝鎵€浠ヨ繖閲屼篃娌℃湁瀹冧滑鐨勫啓鍥炲垎鏀細
            // 閭ｄ簺"鎷栬繖涓偣 鈫?鍙敼 p / 鍙敼 a / 鍙敼鍛ㄦ湡锛嬫尟骞?鐨勭畻寮忎竴璧峰垹鎺変簡銆?            // 鏇茬嚎鐜板湪璧?*閫氱敤妗?*锛堢函鍙樻崲锛氱缉鏀?/ 鏃嬭浆 / 骞崇Щ锛夛紝妯″瀷灞備竴涓瓧閮戒笉鐢ㄦ敼銆?            default:
                // 鐩寸嚎 / 绠ご锛氭敼鍝竴澶村氨鏄摢涓€澶达紙棣栫偣鎴栨湯鐐癸級銆?                pts[_vertexHandle == ShapeHandle.Anchor ? 0 : pts.Length - 1] = local;
                break;
        }
    }

    /// <summary>
    /// 鍒锋柊"鎷栧姩涓殑璇绘暟"锛氱洿绾挎槸鍊炬枩瑙?伪銆佸渾鏄?`r`锛堥檮甯︾洿寰?d = 2r锛夈€佹き鍦嗘槸 `a` 鎴?`b`銆?    ///
    /// 閲忕殑閮芥槸**鐢ㄦ埛鐪嬪埌鐨勯偅涓噺**锛?    ///   路 鐩寸嚎鐨?伪 鎸?*鐢诲竷鍧愭爣**閲忥紙鏃嬭浆杩囩殑绾匡紝灞€閮ㄨ涓嶇瓑浜庡睆骞曡锛夛紱
    ///   路 鍦嗙殑 r 鎸?*鐢诲竷鍧愭爣**閲忥紙灞忓箷涓婅繖涓渾澶氬ぇ锛夛紱
    ///   路 妞渾鐨?a / b 鎸?*灞€閮ㄥ潗鏍?*閲?鈥斺€?閭ｆ槸妞渾鑷繁鐨勫崐杞?    ///     锛堝畠杞埌鍝釜鏂瑰悜锛岄暱杞撮兘鏄偅涔堥暱锛夈€?    /// </summary>
    private void UpdateVertexReadout()
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        if (s == null || pts == null || pts.Length < 2)
        {
            _vertexReadout = VertexReadoutKind.None;
            return;
        }

        var c = Vector2.Transform(pts[0], s.Transform);
        var e = Vector2.Transform(pts[^1], s.Transform);
        switch (s.Kind)
        {
            case StrokeKind.Circle:
            {
                float r = Vector2.Distance(c, e);
                _vertexReadout = VertexReadoutKind.Radius;
                _vertexReadoutValue = r;
                _vertexReadoutSecondary = r * 2f;
                break;
            }

            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.FocusPoint:
            {
                // 鎷?*鐒︾偣涓夎褰㈢殑椤剁偣 P**锛氳繖涓€鎷栨病鏈夎鏁般€?                // 鐞嗙敱鍚屼笁瑙掑舰椤剁偣閭ｆ潯锛歅 鏈韩涓嶆槸鑰佸笀瑕佺湅鐨勯偅涓暟锛?                // 鑰?|PF鈧亅 + |PF鈧倈 = 2a"鏄?*鍙︿竴涓?*璇绘暟锛堜笉鍦ㄨ繖涓€鎵归噷锛屽埆鎶?伪 鎶ュ嚭鏉ュ厖鏁帮級銆?                _vertexReadout = VertexReadoutKind.None;
                break;
            }

            case StrokeKind.Ellipse:
            case StrokeKind.ConicEllipse:
            {
                bool vertical = _vertexHandle == ShapeHandle.AxisTop;
                var d = pts[^1] - pts[0];
                _vertexReadout = vertical ? VertexReadoutKind.AxisB : VertexReadoutKind.AxisA;
                _vertexReadoutValue = vertical ? MathF.Abs(d.Y) : MathF.Abs(d.X);
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
                // 涓夎褰?/ 骞宠鍥涜竟褰?*杩欎竴杞病鏈夎鏁?*锛氬畠浠鏄剧ず鐨勬槸鍐呰 / 澶硅锛?                // 閭ｆ槸绗憿杞紙9.7锛夌殑浜嬶紱鐜板湪鏀归《鐐规椂鍙湁"鍚稿埌浜嗕粈涔?閭ｄ竴棰楄兌鍥?                // 锛堣 ShapeSnapKind锛夈€?*涓嶈**钀藉埌 default 鍘绘姤涓€涓?伪锛?                // 涓変釜椤剁偣涔嬮棿鏍规湰娌℃湁"鍊炬枩瑙?杩欎釜閲忋€?                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            // 鍥涚鏇茬嚎鐨勮鏁帮紙p / 瀹炲崐杞?/ 铏氬崐杞?/ 鍛ㄦ湡锛嬫尟骞咃級2026-09-20 闅忔墜鏌勪竴璧峰垹浜嗭細
            // 璇绘暟鎸傚湪鎵嬫焺涓婏紝鎵嬫焺娌′簡锛岄偅涓噺灏辨病浜烘嫋寰楀姩瀹冧滑浜嗭紙瑙?ShapeHandlesOf锛夈€?            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
                // 鍧愭爣绯?/ 鏁拌酱鍚屾牱**娌℃湁瑙掑害璇绘暟**锛氬洓涓畾涔夊厓绱犱箣闂存病鏈?鍊炬枩瑙?鍙█
                // 锛堣酱姘歌繙鏄按骞崇殑 / 绔栫洿鐨勶紝鍒诲害闂磋窛涔熷彧鏄釜闀垮害锛夈€?                // 钀藉埌 default 鍘绘姤 伪 鐨勮瘽锛屾嫋妗嗚浼氱粰鍑轰竴涓鏃犳剰涔夌殑搴︽暟銆?                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            default:
                _vertexReadout = VertexReadoutKind.Inclination;
                _vertexReadoutValue = SelectionHandles.InclinationDegrees(c, e);
                // 鐩寸嚎 / 绠ご鍐嶆姤涓€涓?*闀垮害**锛?026-10-05 鐢ㄦ埛锛氬拰 伪 鍚屼竴涓€昏緫锛夈€?                _vertexReadoutSecondary = Vector2.Distance(c, e);
                break;
        }
    }

    /// <summary>
    /// 澶氳竟褰㈣鏁帮紙瑙勬牸 9.7锛夛細涓夎褰?= **涓変釜鍐呰**锛屽钩琛屽洓杈瑰舰 = **瀹冭嚜宸遍偅涓や釜澶硅**銆?    /// 涓や釜閮?*鍙湪鎷栭《鐐规椂鏄剧ず**銆?    ///
    /// **2026-09-20 鐢ㄦ埛瀹?*锛?涓夎褰㈠簲璇ュ湪鎷栧姩鐨勬椂鍊欏啀鏄剧ず瑙掑害锛岃涓嶇劧鐪嬭捣鏉ヤ篃涔?鈥斺€?    /// 鍦ㄩ偅涔嬪墠涓夎褰㈡槸"涓€閫変腑灏辨樉绀轰笁涓"锛岄潤姝㈡憜鐫€涔熶竴鐩存寕鐫€涓夐瑙掓爣銆?    /// 鐜板湪瀹冨拰骞宠鍥涜竟褰?*璧板悓涓€鏉″垽鎹?*锛堝钩琛屽洓杈瑰舰浠?2026-09-19 璧峰氨鏄繖鏍凤級銆?    /// 椤哄甫涔熸病浜?鏁翠綋绉诲姩 / 鏃嬭浆涓夎褰㈡椂鏄剧ず瑙掑害"锛氳浆涓€涓笁瑙掑舰骞朵笉浼氭敼鍙樺畠鐨勫唴瑙掞紝
    /// 閭ｅ嚑涓暟鍦ㄩ偅涓や欢浜嬮噷閮芥槸鍣煶銆?    ///
    /// 涓や釜杈撳嚭锛?paramref name="vertices"/> 鏄偅浜涜鐨勯《鐐癸紙**鐢诲竷鍧愭爣**锛屾爣绛捐创鍦ㄥ畠澶栦晶锛夛紝
    /// <paramref name="degrees"/> 鏄搴︼紙搴︼級銆傝繑鍥炶鏄剧ず鍑犱釜瑙掞紙0 = 杩欎竴甯ф病鏈夎繖濂楄鏁帮級銆?    ///
    /// 椤剁偣鍙?*杩欎竴甯у睆骞曚笂閭ｄ釜鍑犱綍**锛氭嫋椤剁偣涓ā鍨嬩竴涓瓧娌℃敼锛屾墍浠ヨ蛋涓存椂鍑犱綍
    /// 锛坄_vertexPreviewLocal`锛変箻涓婂畠鑷繁鐨?`Transform`銆?    /// **缁樺埗涓庤剰鍖洪兘鍙蛋杩欎竴涓嚱鏁?*锛氫袱杈瑰悇绠椾竴浠界殑璇濓紝鏍囩浼氭寜涓€涓綅缃摝銆佹寜鍙︿竴涓綅缃敾锛?    /// 鎷栧姩涔呬簡灞忓箷涓婂氨鐣欎竴鏉℃摝涓嶆帀鐨勮竟锛堥€変腑妗嗛偅涓€濂楄俯杩囪繖涓潙锛夈€?    /// </summary>
    internal int FillAngleReadout(Span<Vector2> vertices, Span<float> degrees)
    {
        // **鍙湁"鎷栭《鐐逛腑"鎵嶅嚭璇绘暟**锛堣涓婇潰閭ｆ锛夛細涓嶆槸鎷栭《鐐硅繖涓€浠朵簨 鈫?杩欎竴甯ф病鏈夎鏁般€?        if (!_vertexDragging || _vertexTarget == null
            || _vertexTarget.Kind is not (StrokeKind.Triangle or StrokeKind.Parallelogram)
            || _vertexPreviewLocal == null)
            return 0;

        var s = _vertexTarget;
        int n = s.Kind == StrokeKind.Triangle ? 3 : 4;
        if (vertices.Length < n || degrees.Length < 3) return 0;

        var m = s.Transform;
        for (int i = 0; i < 3; i++) vertices[i] = Vector2.Transform(_vertexPreviewLocal[i], m);
        // 绗洓涓《鐐?*涓嶅湪涓存椂鐐硅〃閲?*锛堝钩琛屽洓杈瑰舰瀛樹笁涓偣锛夛紝鐜版帹涓€涓€斺€?        // 灏戜簡瀹冿紝鎶ュ嚭鏉ョ殑閭ｄ釜瑙掑氨灏戜簡涓€鏉¤竟锛岃搴︿細绠楅敊銆?        if (n == 4)
            vertices[3] = Vector2.Transform(Stroke.ParallelogramFourth(
                _vertexPreviewLocal[0], _vertexPreviewLocal[1], _vertexPreviewLocal[2]), m);
        return SelectionHandles.PolygonAngles(s.Kind, vertices[..n], degrees);
    }

    /// <summary>琚嫋绔偣"璧拌繃鐨勪竴灏忔"鐨勮剰鐭╁舰锛堜袱涓鐐逛綅缃彇骞堕泦锛屽啀鎸夊崐绗斿澶栨墿锛夈€?/summary>
    private static RectF EndpointDirtyRect(Vector2 a, Vector2 b, float inflate)
    {
        var r = RectF.Empty;
        r.Add(a.X, a.Y);
        r.Add(b.X, b.Y);
        return r.Inflate(inflate);
    }

    /// <summary>
    /// 杩欎竴鎵瑰璞″湪鐭╅樀 <paramref name="m"/> 涓嬪崰鐨勭敾甯冭寖鍥达紙杞村榻愶級銆?    ///
    /// 鑴忓尯蹇呴』鏄鐭╁舰锛岃€屽璞″拰閫夊尯妗嗛兘鍙兘鏄枩鐨勶紝鎵€浠ュ彇鍥涗釜瑙掑彉鎹㈠悗鐨勫寘鍥寸洅銆?    /// 寰€澶栧鐣?2 閫昏緫鍍忕礌锛氭姉閿娇鐨勮竟缂樹細璺戝嚭绮剧‘鍖呭洿鐩掍竴鐐圭偣锛屾紡浜嗗氨浼氬湪灞忓箷涓?    /// 鐣欎竴鏉″彂涓濅竴鏍风殑娈嬪奖銆?    /// </summary>
    private RectF PreviewBounds(in Matrix3x2 m)
    {
        var r = RectF.Empty;
        foreach (var s in _dragTargets)
        {
            var b = s.PaddedBounds;
            if (b.IsEmpty) continue;
            if (m.IsIdentity) { r.Add(b); continue; }
            r.Add(TransformBounds(b, m));
        }
        return r.IsEmpty ? r : r.Inflate(2f * DpiScale);
    }

    /// <summary>鎶婁竴涓酱瀵归綈鐭╁舰杩囦竴閬嶇煩闃碉紝鍙栧洓涓鍙樻崲鍚庣殑鏂板寘鍥寸洅銆?/summary>
    private static RectF TransformBounds(in RectF b, in Matrix3x2 m)
    {
        if (b.IsEmpty) return RectF.Empty;
        if (m.IsIdentity) return b;
        var p0 = Vector2.Transform(new Vector2(b.MinX, b.MinY), m);
        var p1 = Vector2.Transform(new Vector2(b.MaxX, b.MinY), m);
        var p2 = Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m);
        var p3 = Vector2.Transform(new Vector2(b.MinX, b.MaxY), m);
        var r = RectF.Empty;
        r.Add(p0.X, p0.Y); r.Add(p1.X, p1.Y);
        r.Add(p2.X, p2.Y); r.Add(p3.X, p3.Y);
        return r;
    }

    /// <summary>
    /// **杩欎竴甯ц鐢荤殑閭ｄ釜閫変腑妗?*锛氭嫋鍔ㄩ瑙堟湡闂磋鐢ㄥ疄鏃跺彉鎹紝涓嶈兘鍐嶇敤妯″瀷閲岀殑銆?    ///
    /// 涓轰粈涔堝繀椤诲垎寮€锛氭柟妗?B 閲屾ā鍨嬪埌鏉炬墜鎵嶅姩锛堥偅鏍峰唴瀹瑰眰鎵嶈兘涓€甯ч兘涓嶉噸鐢伙級锛?    /// 浜庢槸 <see cref="SelectionHandles.FrameOf"/> 鎷垮埌鐨勬槸**鎸変笅閭ｄ竴鍒?*鐨勬 鈥斺€?    /// 鐩存帴鎷垮畠鐢伙紝鎷栧姩涓鍜屾墜鏌勪細鐣欏湪鍘熷湴涓嶅姩銆佸唴瀹瑰拰妗嗗垎瀹讹紙鏉炬墜鎵?鍟?鍦拌烦鍥炴潵锛夈€?    ///
    /// 绠楁硶鍜?<see cref="SelectionHandles.FrameOf"/> 鏄?*鍚屼竴鏉?*锛氭瘡涓璞＄殑**涓栫晫**
    /// 鍖呭洿鐩掑厛杩囦竴閬嶅疄鏃剁煩闃碉紝鍐嶅苟鎴愯酱瀵归綈鐨勬銆傛墍浠ヤ竴鏉″拰澶氭潯娌℃湁鍒嗘敮鈥斺€?    /// 妗嗘案杩滄鐫€锛屾棆杞腑姣忓抚閲嶆柊璐村悎褰撳墠鍐呭锛堜細"鍛煎惛"锛岃繖鏄?妗嗘案杩滄鐫€"鐨勪唬浠凤紝
    /// 瑙?SelectionFrame 鐨勬敞閲婏級銆?    /// 涓嶅湪鎷栧姩涓氨鍘熸牱杩斿洖锛堢粷澶у鏁板抚璧拌繖鏉★紝闆跺紑閿€锛夈€?    /// </summary>
    internal SelectionFrame LiveSelectionFrame
    {
        get
        {
            // 鎷栫鐐?/ 鎷栭《鐐癸細妯″瀷鍒版澗鎵嬫墠鍔紝鎵€浠ユ蹇呴』鎸?*涓存椂鍑犱綍**绠椻€斺€?            // 鐓фā鍨嬬畻鐨勮瘽妗嗕細鐣欏湪鏃т綅缃笂锛屽拰灞忓箷涓婇偅鏉＄嚎褰撳満鍒嗗銆?            // 锛堝拰涓嬮潰"鎷栧姩棰勮鐢ㄥ疄鏃剁煩闃?鏄悓涓€浠朵簨鐨勪袱绉嶅舰鎬侊細涓€涓敼鍑犱綍銆佷竴涓敼鍙樻崲銆傦級
            //
            // 鍙ｅ緞蹇呴』鍜岄潤姝㈡€?*涓€妯′竴鏍?*锛?026-09-19锛夛細璧?PreviewInkBounds
            // 锛堢鐐瑰鎺?/ 鍦嗙殑鍙傛暟鍖栧鎺?/ 椤剁偣澶栨帴 + 鍗婄瑪瀹斤級锛屽畠鍜?WorldInkBounds 涓€寮犺〃銆?            // 浠ュ墠杩欓噷鍐欐浜?LineLikeInkBounds 鈥斺€?閭ｆ槸**鐩寸嚎涓撶敤**鐨勪竴鏉″紡瀛愶紝
            // 鍦?妞渾/涓夎褰㈡嫋鍏冪礌鏃舵浼氶€€鍖栨垚"涓や釜鎺у埗鐐圭殑澶栨帴"锛堝渾鐨勭揣妗嗕竴搴︾畻鎴?256脳16锛夈€?            // 鍙︼細杩欏効浠ュ墠鐢ㄧ殑鏄?PaddedBoundsOf 鈥斺€?閭ｆ槸**鑴忓尯**鐨勫彛寰勶紙澧ㄨ抗 + 2 鍍忕礌锛夛紝
            // 浜庢槸鎷栧姩涓瘡杈规瘮瀵硅薄澶?2 鍍忕礌锛屾澗鎵嬮偅涓€鐬浼氱缉涓€涓嬨€?            if (_vertexDragging && _vertexTarget != null && _vertexPreviewLocal != null)
            {
                var pv = _vertexPreviewLocal;
                var rv = _vertexTarget.PreviewInkBounds(pv, Matrix3x2.Identity);
                return new SelectionFrame { Local = rv, ToCanvas = Matrix3x2.Identity };
            }

            var f = SelectionHandles.FrameOf(Doc.Selected);
            if (!DragPreviewActive || f.IsEmpty) return f;
            var r = RectF.Empty;
            foreach (var s in Doc.Selected)
            {
                // 閿佸畾鐨勫璞?*涓嶈窡鐫€鍔?*锛堢敤鎴?2026-09-16 瀹氱殑 B 璇箟锛夛紝
                // 鎵€浠ュ畠浠偅鍧楁寜"褰撳墠浣嶇疆"绠椻€斺€斾笉鐒舵浼氳窡鐫€瀹冧滑涓€璧锋紓銆?                if (s.Locked) { r.Add(s.WorldInkBounds); continue; }
                // 鐩寸嚎 / 绠ご瑕佹寜**绔偣**杩囷紙瀵硅薄鍙樻崲 脳 瀹炴椂鐭╅樀锛夌畻锛屼笉鑳芥嬁"褰撳墠澧ㄨ抗妗?鍐嶄箻鐭╅樀锛?                // 鍚庤€呯瓑浜?鎶婁竴涓煩褰㈡暣浣撹浆杩囧幓鍐嶅彇澶栨帴"锛屼竴鏉¤浆 30掳 鐨勭洿绾挎浼氭拺鍒版帴杩戜袱鍊嶅
                // 锛?026-09-18 瀹炴祴锛?62 vs 绾胯嚜宸?490锛夈€?                r.Add(s.IsLineLike
                    ? s.LineLikeInkBounds(_selDragMatrix)
                    : TransformBounds(s.WorldInkBounds, _selDragMatrix));
            }
            return new SelectionFrame { Local = r, ToCanvas = Matrix3x2.Identity };
        }
    }

    /// <summary>
    /// **鐢诲畬鑷姩閫変腑閭ｄ釜妗?*锛堣 <see cref="EndStroke"/>锛夛細鎸囬拡钀藉湪鍝竴妗ｃ€?    ///
    /// **鍜屾閫夊伐鍏蜂笅閭ｄ竴濂楀畬鍏ㄤ竴鏍?*锛屽彧鏈?鎸夊埌妗嗗"杩欎竴绉嶆儏鍐典笉鍚岋細
    ///   路 <see cref="AutoSelZone.Furniture"/> 锛?鎿嶄綔鏉￠偅涓€鍧楋紙鍦嗛挳鎴栨暣鏉★級/ 闈㈡澘 /
    ///     鎵嬫焺 / 鏃嬭浆鏌勨€斺€斾氦缁?<see cref="TryBeginSelectionGesture"/> 閭ｄ竴濂楀垎娴侊紱
    ///   路 <see cref="AutoSelZone.Grab"/> 锛?**妗嗗唴浠绘剰涓€鐐?*锛堝惈鍥惧舰鑷繁閭ｆ潯澧級
    ///     鈫?鏁翠綋鎷栧姩锛屽拰妗嗛€夊伐鍏蜂笅鍚屼竴鏉¤矾锛堟寜浣忔閲屽摢鍎块兘鑳芥嫋锛夛紱
    ///   路 <see cref="AutoSelZone.None"/> 锛?妗?*澶?*锛氭敹璧疯繖涓锛?*杩欎竴绗旂収甯哥敾**
    ///     鈥斺€旂敤鎴?2026-09-22 鐨勫彛寰勶細"鐐瑰嚮浜嗗叾浠栧湴鏂癸紝杩欎釜閫変腑妗嗗氨鍙栨秷"銆?    ///
    /// 鐢ㄦ埛涓婃墜涔嬪悗鐨勫師璇濇槸"**鎴戠幇鍦ㄦ槸鍙鏀剁缉锛屽叾浠栫殑閮戒笉鍙?*"鈥斺€旀墍浠ヨ繖涓€妗ｅ垽鎹?    /// **涓嶈璇?`_autoSelCollapsed`**锛氭敹璧?/ 鎽婂紑鍙奖鍝?鏉?鐢绘垚鍦嗛挳杩樻槸涓€鏁存潯
    ///锛堣 <see cref="BarDrawnCollapsed"/>锛夛紝鎷栧姩銆佹媺鎵嬫焺銆佺偣鏉¤繖浜涜涓轰竴涓瓧閮戒笉鍙樸€?    /// 鈿?绗竴鐗堟妸"妗嗛噷"鍒ゆ垚"鎺ョ潃鐢讳竴绗?锛岀粨鏋滃氨鏄敤鎴锋姤鐨勯偅鍙?    /// "瀹冨ソ鍍忎笉鑳芥嫋鍔ㄤ綅缃紝鍙兘鎷変几缂╂斁"鈥斺€?*妗嗛噷閭ｅ潡鍦版柟蹇呴』鑳芥嫋**銆?    ///
    /// 鈿?**鍙湁杩欎竴浠藉垽鎹?*锛氭寜涓嬫椂寰€鍝潯璺蛋锛坄OnPointerDown` 鈫?<see cref="AutoSelectionPress"/>锛?    /// 鍜屽厜鏍囧舰鐘讹紙<see cref="ComputeCursorKind"/>锛夐兘闂畠锛屼袱澶勫悇鍐欎竴閬嶇殑璇濆氨浼氬嚭鐜?    /// "鍏夋爣鐪嬬潃鑳芥嫋銆佹寜涓嬪幓鍗村湪鐢诲浘"杩欑涓€鍗婂涓€鍗婇敊鐨勭姸鎬?    ///锛?鍚屼竴涓悕鍗曞啓鍦ㄥ澶勫繀婕忎竴澶?鐨勬暀璁 鏋舵瀯-鍒嗗眰涓庤鍒?md 浜?7锛夈€?    /// </summary>
    private AutoSelZone AutoSelectionZoneAt(float x, float y)
    {
        float dpi = DpiScale;
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var aabb = frame.CanvasAabb;

        // 鈶?鎿嶄綔鏉￠偅涓€鍧椻€斺€?*鏀惰捣鏉ュ氨鏄偅涓€棰楀渾閽紝鎽婂紑浜嗗氨鏄竴鏁存潯 锛?闈㈡澘**銆?        //    鑼冨洿鍜?SelectionCursor 閲岄偅鍑犲潡**鍚屼竴涓?*锛?鐪嬪緱瑙佺殑涓€鍧?鍜?鐐瑰緱鍒扮殑涓€鍧?
        //    蹇呴』鏄悓涓€涓級銆?        if (BarDrawnCollapsed)
        {
            if (SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas).Contains(x, y))
                return AutoSelZone.Furniture;
        }
        else
        {
            if (SelectionHandles.BarRect(aabb, dpi, ViewportCanvas).Contains(x, y))
                return AutoSelZone.Furniture;
            if (CustomColorOpen
                && SelectionHandles.PickContains(x, y, aabb, dpi, ViewportCanvas,
                                                 SelectionHandles.SwatchCount))
                return AutoSelZone.Furniture;
            if (SelPanelOpen != SelPanel.None
                && SelectionHandles.PanelContains(x, y, aabb, dpi, ViewportCanvas,
                                                  SelPanelOpen, SelectionHandles.SwatchCount))
                return AutoSelZone.Furniture;
        }

        // 鈶?鎵嬫焺锛堝叓涓缉鏀炬焺 / 鏃嬭浆鏌?/ 瀹氫箟鍏冪礌鎵嬫焺锛夈€?*蹇呴』鎺掑湪"鏁翠綋鎷栧姩"閭ｆ潯鍓嶉潰**锛?        //    鐩寸嚎鐨勪袱涓鐐规墜鏌勬濂藉帇鍦ㄦ杈逛笂锛屽厛鍒ゆ鍐呯殑璇濋偅涓€鎸変細琚綋鎴?鎷栨暣鏉?銆?        if (SelectionHandles.HitTest(x, y, Doc.Selected, frame, dpi) != SelHandle.None)
            return AutoSelZone.Furniture;

        // 鈶?**妗嗗唴** 鈫?鏁翠綋鎷栧姩锛坄TryBeginSelectionGesture` 閲岀 3 / 4 姝ラ偅涓€濂楋細
        //    妗嗛噷绌虹櫧澶勬槸"鎷栧姩"銆佹寜鍦ㄦ煇鏉″ⅷ涓婃槸"鏀剁獎鎴愬彧閫夊畠"锛夈€?        //    鍒ゆ嵁灏辨槸妗嗛€夊伐鍏蜂笅閭ｄ竴鏉★紙`frame.ToLocalPoint` 钀藉湪 `frame.Local` 閲岋級锛?        //    涓嶅彟鍐欎竴浠?绂昏疆寤撳杩滅畻鎸変笂浜?銆?        if (SelectionHandles.InsideUiFrame(frame, new Vector2(x, y), dpi))
            return AutoSelZone.Grab;

        // 鈶?妗?*澶?*锛氫笉绠楀姩瀹冣€斺€旀敹璧疯繖涓锛岃繖涓€绗旂収甯哥敾銆?        return AutoSelZone.None;
    }

    /// <summary>鍥惧舰宸ュ叿涓嬨€侀偅涓嚜鍔ㄥ嚭鐜扮殑妗嗭細杩欎竴娆℃寜涓嬬畻涓嶇畻"鍦ㄥ姩瀹?銆?/summary>
    private bool AutoSelectionPress(float x, float y, bool shift, bool alt)
        => AutoSelectionZoneAt(x, y) != AutoSelZone.None
           && TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>
    /// 姝ゅ埢鎸囬拡鎵€鍦ㄨ繖涓€甯︼紝"閫変腑妗嗛偅涓€濂?锛堟墜鏌?/ 妗嗗唴鎷栧姩锛夋槸涓嶆槸鍙敤銆?    ///
    /// 妗嗛€夊伐鍏蜂笅**鏁翠釜妗?*閮藉彲鐢紱鍥惧舰宸ュ叿涓嬫寜 <see cref="AutoSelectionZoneAt"/> 閭ｄ袱妗ｈ蛋銆?    /// 杩欎竴鏉′篃**鍙湁杩欎竴浠?*鈥斺€斿厜鏍囬兘闂畠銆?    /// </summary>
    private bool SelectionInteractiveAt(float canvasX, float canvasY)
        => Doc.Selected.Count > 0
           && (Tool == Tool.Marquee
               || ((IsShapeTool(Tool) || _dwellSelected || _touchSelected)
                   && AutoSelectionZoneAt(canvasX, canvasY) != AutoSelZone.None));

    /// <summary>
    /// **鎿嶄綔鏉￠偅涓€鍧楋紙鏁存潯 / 鍦嗛挳 / 瀹冧笅闈㈡寕鐨勯潰鏉匡級鐢讳笉鐢汇€佺偣涓嶇偣**銆?    ///
    /// 妗嗛€夊伐鍏蜂笅褰撶劧鏈夛紱**鍥惧舰宸ュ叿涓嬩篃鏈?*鈥斺€旂敾瀹岃嚜鍔ㄩ€変腑閭ｄ釜妗嗕細鎸備竴**棰楀渾閽?*
    /// 锛堣 <see cref="_autoSelCollapsed"/>锛夛細涓嶇敾鐨勮瘽鑰佸笀涓嶇煡閬撹鍘诲摢鍎挎妸瀹冩憡寮€锛?    /// 鑰岄偅鍙槸涓€棰?26 閫昏緫鍍忕礌鐨勫渾閽紝涓嶅儚鏁存潯涔濇牸閭ｆ牱浼氭妸"鎺ョ潃鐢?鐨勫湴鏂瑰崰鎺夈€?    /// 鎽婂紑涔嬪悗琛屼负**涓€涓瓧閮戒笉鍙?*锛堝彧鏄?鏉?闀垮緱瀹屾暣浜嗭級銆?    ///
    /// 鈿?杩欐槸**鍞竴**鍒ゆ嵁锛氱粯鍒讹紙Overlay锛? 鎸変笅鍒嗘祦锛圱ryBeginSelectionGesture锛? 鎮仠楂樹寒
    /// 锛圲pdateBarHover锛? 鍏夋爣锛圫electionCursor锛夊洓澶勯兘闂畠锛屼换涓€澶勬紡浜嗛兘浼氬彉鎴?    /// "鐢讳簡浣嗙偣涓嶅埌"鎴栬€呭弽杩囨潵"鐐瑰緱鍒颁絾鐪嬩笉瑙?銆傚叿浣撶敾鏁存潯杩樻槸鐢诲渾閽紝闂?`BarDrawnCollapsed`銆?    /// </summary>
    internal bool SelectionBarShown
        => Doc.Selected.Count > 0 && (Tool == Tool.Marquee || IsShapeTool(Tool) || _dwellSelected || _touchSelected);

    /// <summary>鍥惧舰宸ュ叿涓嬮偅涓嚜鍔ㄩ€変腑鐨勬锛氭寚閽堣惤鍦?瀹跺叿 / 绠楀姩瀹?/ 閮戒笉鏄?鍝竴妗ｃ€?/summary>
    private enum AutoSelZone
    {
        /// <summary>閮戒笉绠椻€斺€旀寜涓嬪幓鏄帴鐫€鐢讳竴绗斻€?/summary>
        None,
        /// <summary>妗嗙殑瀹跺叿锛氬渾閽?/ 鎿嶄綔鏉?/ 闈㈡澘 / 鎵嬫焺 / 鏃嬭浆鏌勶紙浜ょ粰閫夋嫨鎵嬪娍閭ｄ竴濂楀垎娴侊級銆?/summary>
        Furniture,
        /// <summary>妗嗗唴浠绘剰涓€鐐癸紙鍚浘褰㈣嚜宸遍偅鏉″ⅷ锛夛細鎷栧畠璧般€?/summary>
        Grab,
    }

    /// <summary>
    /// 妗嗛€夊伐鍏锋寜涓嬫椂鐨勫垎娴併€傝繑鍥?true 琛ㄧず杩欐鎸変笅宸茬粡琚€夋嫨鎵嬪娍鎺ユ帀銆?    ///
    /// 椤哄簭鏈夎绌讹紙浠?1 鍒?5锛?*涓嶈兘鎹?*锛夛細
    ///   1. 鎿嶄綔鏉℃寜閽€斺€斿畠鍜屾墜鏌勩€佹閮界揣鎸ㄧ潃锛屾斁鍚庨潰灏辩偣涓嶄腑锛?    ///   2. 鎵嬫焺锛堢缉鏀?/ 鏃嬭浆锛夛紱
    ///   3. 鍦?*褰撳墠閫夊尯妗嗗唴** 鈫?鏁翠綋鎷栧姩锛?    ///   4. **鐐归€?*锛氭寜鍦ㄤ竴鏉″ⅷ杩逛笂灏遍€変腑瀹冿紙娌℃湁閫変腑鏃朵篃璧拌繖鏉★級锛?    ///   5. 閮芥病鏈?鈫?杩斿洖 false锛屼氦缁欏闈㈢殑妗嗛€夈€?    ///
    /// 绗?3 姝ュ繀椤诲湪绗?4 姝ヤ箣鍓嶏細鍚﹀垯"鎷栧姩宸查€変腑鐨勪竴鍧?浼氳鍒ゆ垚"鐐归€夊叾涓竴鏉?锛?    /// 澶氶€夊氨鍐嶄篃鎼笉鍔ㄤ簡銆傦紙2026-09-15 瀹炵幇鐐归€夋椂瀹氱殑椤哄簭銆傦級
    /// </summary>
    private bool TryBeginSelectionGesture(float x, float y, bool shift, bool alt)
    {
        _dragHitStroke = null;
        _pendingClone = null;
        float dpi = DpiScale;

        bool move = false;
        var h = SelHandle.None;
        var frame = SelectionHandles.FrameOf(Doc.Selected);

        // 鈥斺€?鈸?鏀惰捣鎬佸渾閽?/ 娴姩闈㈡澘 / 鎿嶄綔鏉★細浼樺厛绾ф渶楂橈紙瀹冧滑鐩栧湪鍒殑涓滆タ涓婇潰锛夆€斺€?        //
        // 椤哄簭锛?*闈㈡澘鍐呴儴 鈫?鎿嶄綔鏉?鈫?闈㈡澘澶栭儴**銆備负浠€涔堟妸"鏉?鏀惧湪"闈㈡澘澶栭儴"涔嬪墠锛?        // 闈㈡澘寮€鐫€鏃跺幓鐐?灞傜骇"閭ｄ竴鏍硷紝搴旇鐩存帴鎹㈡垚灞傜骇闈㈡澘锛岃€屼笉鏄?鍏堝叧鎺夊啀琚繖涓€鏍?        // 鍙堟墦寮€"銆傝€岀偣闈㈡澘鍜屾潯浠ュ鐨勪换浣曞湴鏂癸紝鎵嶆妸闈㈡澘鏀惰捣鏉ワ紙骞朵笖杩欎竴娆＄偣鍑?*缁х画**
        // 寰€涓嬭蛋锛屼簬鏄?鐐圭┖鐧藉彇娑堥€変腑"鐨勪範鎯笉浼氬洜涓洪潰鏉垮紑鐫€灏卞け鐏碉級銆?        //
        // 鈿?鏁存鐢?`SelectionBarShown` 鎶婂畧锛氬彧鏈?妗嗛€夊伐鍏?/ 鍥惧舰宸ュ叿涓嬮偅涓嚜鍔ㄩ€変腑鐨勬"
        // 鎵嶆湁鐐瑰緱鍒扮殑閭ｄ竴鍧楋紙瑙侀偅閲岀殑娉ㄩ噴锛夆€斺€斾笉鎶婂畧鐨勮瘽锛屾搷浣滄潯浼氶殧绌哄悆鎺?        // "鎺ョ潃鐢讳笅涓€涓?鐨勯偅涓€涓嬨€?        if (SelectionBarShown)
        {
            var aabb0 = LiveSelectionFrame.CanvasAabb;

            if (BarDrawnCollapsed)
            {
                // 鏀惰捣鎬侊細鍙湁涓€涓渾閽紝鐐瑰畠灞曞紑锛堟鍜屾墜鏌勭収鏃э紝鏀惰捣鐨勫彧鏄?鏉?锛夈€?                var dot = SelectionHandles.BarCollapsedRect(aabb0, dpi, ViewportCanvas);
                if (dot.Contains(x, y))
                {
                    SelBarCollapsed = false;
                    // **鐢诲畬鑷姩閫変腑閭ｄ釜妗?*锛氱偣寮€杩欓鍦嗛挳涔嬪悗灏辨垚"甯歌閭ｄ竴濂?浜?                    //锛堟暣鏉℃搷浣滄潯鍑烘潵銆佹鍐呬换鎰忎竴鐐归兘鑳芥嫋锛夆€斺€旂敤鎴?2026-09-22 瀹氱殑灏辨槸杩欎竴姝ャ€?                    _autoSelCollapsed = false;
                    _dirty = true;
                    return true;
                }
            }
            // **鑷畾涔夊彇鑹叉澘浼樺厛**锛氬畠鎸傚湪澧ㄨ抗闈㈡澘鏃佽竟锛屽彲鑳藉帇鐫€鍒殑鎺т欢锛岃€屼笖
            // "鐐瑰畠鑷繁 = 涓嶇畻鐐瑰湪澶栭潰"锛堢偣绌虹櫧涓嶅彇娑堬紝鍙湁鐐瑰崱鐗囦互澶栨墠鍙栨秷锛夈€?            else if (CustomColorOpen
                     && SelectionHandles.PickContains(x, y, aabb0, dpi, ViewportCanvas,
                                                      SelectionHandles.SwatchCount))
            {
                HandlePickClick(x, y);
                return true;                        // 鑹叉澘涓婄殑鐐瑰嚮涓€寰嬪悆鎺夛紙鍖呮嫭绌虹櫧澶勶級
            }
            else if (SelPanelOpen != SelPanel.None
                     && SelectionHandles.PanelContains(x, y, aabb0, dpi, ViewportCanvas,
                                                       SelPanelOpen, SelectionHandles.SwatchCount))
            {
                HandlePanelClick(x, y);
                return true;                        // 闈㈡澘涓婄殑鐐瑰嚮涓€寰嬪悆鎺夛紙鍖呮嫭绌虹櫧澶勶級
            }
            else
            {
                int btn0 = SelectionHandles.BarButtonAt(x, y, aabb0, dpi, ViewportCanvas);
                if (btn0 >= 0) { RunBarAction(btn0, frame, aabb0); return true; }
                if (SelPanelOpen != SelPanel.None || CustomColorOpen)
                {
                    CloseCustomColor();
                    SelPanelOpen = SelPanel.None;
                    _dirty = true;
                }
            }
        }

        // 鈥斺€?1~3锛氭湁閫変腑鏃舵墠璋堝緱涓婏紙娌￠€変腑灏辩洿鎺ヨ烦鍒扮 4 姝ョ殑鐐归€夛級鈥斺€?        if (Doc.Selected.Count > 0)
        {
            // 鎿嶄綔鏉″畾浣嶇敤妗嗙殑**鐢诲竷杞村榻愯寖鍥?*锛氭鏈韩鍙兘鏄枩鐨勶紝浣?瀹冨崰浜嗗睆骞曚笂
            // 鍝竴鍧?姘歌繙鏄釜姝ｇ煩褰紝鎿嶄綔鏉¤创鍦ㄩ偅涓煩褰㈢殑涓嬮潰鎵嶅銆?            var aabb = frame.CanvasAabb;

            // 锛堟搷浣滄潯涓庨潰鏉垮凡缁忓湪 鈸?閲屽鐞嗚繃浜嗭紝杩欓噷鍙墿鎵嬫焺 / 妗嗗唴鎷栧姩 / 鐐归€夛級
            h = SelectionHandles.HitTest(x, y, Doc.Selected, frame, dpi);

            // 瀹氫箟鍏冪礌鎵嬫焺锛堢洿绾?/ 绠ご / 鍦?/ 妞渾锛夛細**涓ょ璇箟鍒嗘淳**
            //   路 **鍦嗗績 / 涓績**锛圓nchor锛夛細鎷栧畠 = 鏁翠綋骞崇Щ锛堟敼鍙樻崲鐭╅樀銆佸嚑浣曚竴涓偣涓嶅姩锛?            //     鍗婂緞 / 鍗婅酱鍥犳閫愪綅涓嶅彉锛夈€?            //   路 **绔偣 / 鍦嗗懆鐐?/ 杞寸鐐?*锛氳蛋"鏀瑰嚑浣?閭ｆ潯蹇矾锛堟墜鍔挎湡涓嶇妯″瀷銆佹澗鎵嬩竴姝ユ挙閿€锛夈€?            // 閿佸畾璇箟鐓ф棫锛氶攣瀹氱殑鎷栦笉鍔紝钀藉洖"妗嗗唴鎷栧姩"璁╀笅闈㈤偅娈甸攣瀹氬垎鏀妸瀹冨悆鎺夈€?            bool anchorMove = false;
            // "鎸変笅鐨勬槸涓嶆槸瀹氫箟鍏冪礌鎵嬫焺"鈥斺€旈《鐐归偅鍑犳牸**璧?IsVertexHandle 涓€鏉″垽鎹?*锛?            // 涓嶅湪杩欓噷鍐嶅垪涓€閬嶅悕瀛楋紙鍒楀悕瀛楃殑鍐欐硶 2026-09-19 婕忚繃涓€娆?VertexD锛?            // 鐥囩姸鏄?鎷栧崟浣嶉暱搴︾偣浠€涔堥兘娌″彂鐢?锛岃 SelectionHandles.IsVertexHandle锛夈€?            bool handleLike = h is SelHandle.EndpointA or SelHandle.EndpointB
                                 or SelHandle.Left or SelHandle.Right or SelHandle.Top or SelHandle.Bottom
                              || SelectionHandles.IsVertexHandle(h)
                              // 鐒︾偣涓夎褰㈢殑椤剁偣 P锛?026-09-22锛夛細**瀹冧篃鏄竴鏍煎畾涔夊厓绱犳墜鏌?*
                              // 鈥斺€斿皯鍐欒繖涓€鏍硷紝灞忓箷涓婁細鐢诲嚭涓€涓?鐪嬪緱瑙併€佹寜涓嶅姩"鐨勫渾鐐广€?                              || h == SelHandle.FocusPoint;
            if (handleLike && SelectionHandles.ShapeEditable(Doc.Selected, out var shape))
            {
                var sh = SelectionHandles.HandleOf(shape, h);
                if (SelectionHandles.IsAnchorMove(shape, sh))
                {
                    anchorMove = true;
                    _dragHitStroke = shape;    // 鏉炬墜鑻ユ病鍔?鈫?鏀剁獎鎴?鍙€変腑瀹?锛堝拰鐐归€夊悓涓€鏉¤矾锛?                    h = SelHandle.None;
                }
                else if (sh != ShapeHandle.None && !shape.Locked)
                {
                    BeginVertexDrag(shape, sh, x, y);
                    return true;
                }
                // **鍒€ョ潃鎶?h 鎶规帀**锛歚Top / Bottom / Left / Right` 杩欏洓涓悕瀛楁槸**涓ょ敤**鐨勨€斺€?                //   路 瀵规き鍦?/ 鍧愭爣绯?/ 鏁拌酱锛氬畠浠槸"瀹氫箟鍏冪礌"锛堣蛋涓婇潰閭ｆ潯鏀瑰嚑浣曠殑璺級锛?                //   路 瀵?*娌℃湁瀹氫箟鍏冪礌鎵嬫焺**鐨勫璞★紙鐭╁舰 / 澧ㄨ抗 / 鍥惧儚 / 鍥涚鏇茬嚎 / 鍥涗釜绔嬩綋鍥惧舰锛夛細
                //     瀹冧滑灏辨槸**閫氱敤妗嗙殑鍥涜竟涓偣缂╂斁鏌?*銆?                // 鍘熸潵杩欓噷鏃犳潯浠?`h = SelHandle.None`锛屼簬鏄偅绫诲璞?鍥涜鑳芥媺銆佸洓杈逛腑鐐瑰彧鑳芥暣浣撴嫋"
                //锛堢敤鎴?2026-09-20锛?閭ｅ叓涓偣鍑虹幇浜嗭紝浣嗘槸鎴戠湅鐫€鍙湁瀵硅绾胯兘鎷栧姩鏀剧缉锛?                //  鎴戣寰楀乏鍙虫媺浼镐篃鍙互缁?锛夈€?                // 鍒ゆ嵁杩樻槸閭ｄ竴鏉★細**瀹冨埌搴曟湁娌℃湁瀹氫箟鍏冪礌鎵嬫焺**鈥斺€旀湁锛屾墠璇存槑杩欎釜 h 鐪熺殑"娌℃湁"锛?                // 璇ユ姽鎺夛紙"鐢婚兘涓嶇敾灏卞埆鐐瑰緱鍒?锛岃 HitTest 閭ｆ锛夛紱娌℃湁锛屽氨鐣欑粰涓嬮潰鐨勯€氱敤妗嗐€?                else if (SelectionHandles.HasShapeHandles(shape))
                    h = SelHandle.None;
            }

            if (h == SelHandle.None)
            {
                if (anchorMove)
                {
                    // 鍦嗗績 / 涓績锛?*鐩存帴绠椾綔鏁翠綋鎷栧姩**锛屼笉璧颁笅闈?鎸囬拡搴曚笅鏈夋病鏈変笢瑗?閭ｄ竴鍏炽€?                    // 鐞嗙敱鏄嚑浣曚簨瀹烇細鍦嗗績绂绘弿杈逛竴鏁翠釜鍗婂緞閭ｄ箞杩滐紝鑰岀偣閫夊彧璁ゆ弿杈?                    // 锛堣 HitObjectAt锛夆€斺€旈潬閭ｄ竴娈电殑璇?鎷栧渾蹇?鏍规湰鎷栦笉鍔紙2026-09-19 鑷鎶撳埌锛夈€?                    move = true;
                }
                else
                {
                    // 娌＄偣鍦ㄦ墜鏌勪笂锛氱湅鎸囬拡鏄笉鏄惤鍦?*鎿嶄綔妗?*閲岋紙鏁翠綋鎷栧姩锛夈€?                    // 灏忓璞￠偅涓鎾戝埌鏈€灏忓昂瀵革紙瑙?SelectionHandles.UiBox锛夛紝
                    // "鐪嬪緱瑙佺殑閭ｄ竴鍦?閲岄潰閮借兘鎷栤€斺€斿垽鎹拰鍏夋爣銆佽嚜鍔ㄩ€変腑妗嗗悓涓€浠姐€?                    move = SelectionHandles.InsideUiFrame(frame, new Vector2(x, y), dpi);

                    // 椤烘墜璁颁笅"鎸囬拡搴曚笅鏄摢涓€鏉?锛氭澗鎵嬫椂鑻ヤ竴鐐规病绉诲姩锛屽氨鎶婂閫?*鏀剁獎鎴愬彧閫夊畠**
                    // 锛圥PT/Figma 鐨勮涓猴級銆傝惤鍦ㄦ鍐呯┖鐧藉 鈫?璁颁笉鍒颁笢瑗?鈫?鏉炬墜涓嶆敼閫夋嫨銆?                    if (move) _dragHitStroke = Doc.HitObjectAt(x, y, ClickToleranceLogical * dpi);
                }
            }
        }

        // 鈥斺€?4锛氱偣閫夛紙2026-09-15 鏂板锛夆€斺€?        if (h == SelHandle.None && !move)
        {
            var hit = Doc.SelectAt(x, y, ClickToleranceLogical * dpi,
                                   additive: shift, subtractive: alt);
            if (hit == null) return false;                // 5锛氱┖鐧?鈫?浜ょ粰妗嗛€?
            _dragHitStroke = hit;
            _dirty = true;
            // 鍔犻€?鍑忛€夋椂**涓嶈繘鎷栧姩**锛氭柟渚胯繛鐫€鐐瑰嚑鏉℃敀鍑轰竴涓€夋嫨銆?            if (shift || alt) return true;

            // 鏃犱慨楗伴敭锛氶€変腑瀹冨苟**鐩存帴杩涘叆鏁翠綋鎷栧姩**锛堢偣浣忓氨鑳芥嫋锛屽拰 PPT 涓€鏍凤級銆?            frame = SelectionHandles.FrameOf(Doc.Selected);
            move = true;
        }

        // 鈥斺€?4.5锛氬鍒舵嫋鎷芥ā寮忥細鎸変綇閫変腑鍐呭鎷栧姩 = **鎷栧嚭涓€浠藉壇鏈?*锛堝師浠朵笉鍔級鈥斺€?        // 鍏嬮殕鏀惧湪杩欓噷锛堟寜涓嬮偅涓€鍒伙級锛屽壇鏈笌鍘熶欢瀹屽叏閲嶅悎锛岄殢鍚庤繖涓€娆℃嫋鍔ㄧЩ鍔ㄧ殑灏辨槸鍓湰锛?        // 鏉炬墜鏃舵妸"鎻掑叆鍓湰 + 绉诲姩鍓湰"鍚堟垚涓€姝ユ挙閿€锛堣 EndSelDrag锛夈€?        if (move && CopyDragArmed)
        {
            _pendingClone = Doc.CloneSelectedInPlace();
            if (_pendingClone.Strokes.Count > 0)
            {
                frame = SelectionHandles.FrameOf(Doc.Selected);   // 鍓湰涓庡師浠堕噸鍚堬紝妗嗕笉鍙?                _dirty = true;
            }
            else _pendingClone = null;
        }

        _dragHandle = h;
        _dragIsMove = move;
        _dragFrame = frame;
        _dragStartPoint = new Vector2(x, y);
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        _dragTargets = Doc.Selected.ToArray();

        // 閿佸畾锛堢敤鎴?2026-09-16 瀹氱殑 B 璇箟锛夛細**鑳介€変腑锛屼絾鎷栦笉鍔?*銆?        // 鍋氭硶鏄妸閿佸畾鐨勪粠"杩欎竴娆℃墜鍔胯鍔ㄧ殑閭ｆ壒"閲屾憳鎺夆€斺€旈€夊尯妗嗙収鏃х敾锛堟绠楃殑鏄?        // Doc.Selected锛夛紝鍙槸瀹冧滑涓嶈窡鐫€鍔ㄣ€傚叏閿佹椂鎻愮ず涓€鍙ャ€佸苟涓斾粈涔堥兘涓嶅姩銆?        {
            int lockedCount = 0;
            foreach (var s in _dragTargets) if (s.Locked) lockedCount++;
            if (lockedCount > 0)
            {
                if (lockedCount == _dragTargets.Length)
                {
                    // 閿佸畾鐨勫璞℃嫋涓嶅姩鈥斺€旇繖涓€鍙ュ彧鍦ㄦ帶鍒跺彴鐣欑棔锛堝睆骞曞弽棣堟寜鐢ㄦ埛瑕佹眰鍘绘帀浜嗘彁绀烘潯锛?                    // 閿佸浘鏍囨湰韬氨鏄姸鎬佹寚绀猴級銆?                    Console.WriteLine($"鎷栧姩锛氳繖 {lockedCount} 涓璞″凡閿佸畾锛屾嫋涓嶅姩");
                    SelDragging = true;                 // 鍚冩帀杩欐鎸変笅锛氬埆閫€鍖栨垚閲嶆柊妗嗛€?                    _dragTargets = Array.Empty<Stroke>();
                    _dragStartXform = Array.Empty<Matrix3x2>();
                    _dragHandle = SelHandle.None;
                    _dragIsMove = false;
                    _dirty = true;
                    return true;
                }
                var movable = new List<Stroke>(_dragTargets.Length - lockedCount);
                foreach (var s in _dragTargets) if (!s.Locked) movable.Add(s);
                _dragTargets = movable.ToArray();
                Console.WriteLine($"鎷栧姩锛歿lockedCount} 涓璞″凡閿佸畾锛屼笉璺熺潃鍔?);
            }
        }

        _dragStartXform = new Matrix3x2[_dragTargets.Length];
        for (int i = 0; i < _dragTargets.Length; i++)
            _dragStartXform[i] = _dragTargets[i].Transform;

        // 鏂规 B锛氭妸杩欎竴鎵逛粠**鍐呭灞?*鎽樺嚭鍘伙紝鏀圭敱娴姩灞傜敾瀹炴椂棰勮銆?        // 妯″瀷鍦ㄦ暣涓墜鍔块噷涓€涓瓧閮戒笉鏀癸紝鎵€浠ュ唴瀹瑰眰涓€甯ч兘涓嶇敤閲嶇敾銆?        DetachForDrag();

        // 鏃嬭浆锛氱疮绉浠?0 璧凤紝鎸囬拡鐨?涓婁竴甯т綅缃?灏辨槸鎸変笅杩欎竴鐐广€?        // 蹇呴』鍦ㄨ繖閲屽綊闆垛€斺€斾笂涓€娆℃嫋鎷芥敀涓嬫潵鐨勮搴︾粷涓嶈兘甯﹁繘杩欎竴娆°€?        _rotAccumDeg = 0f;
        _rotPrevPoint = frame.ToLocalPoint(_dragStartPoint);

        // 杩欎竴娆℃棆杞 伪 杩樻槸 螖锛?*鎸変笅杩欎竴鍒诲垽瀹氫竴娆?*锛堢敤鎴?2026-09-18 鐨勫洓鏉′箣涓€锛夈€?        // 鍒ゅ畾澶嶇敤"鍗曢€変竴鏉＄洿绾?绠ご"閭ｄ釜鐜版垚鍒ゆ嵁锛堢粓鐐规墜鏌勭敤鐨勬槸鍚屼竴涓級锛?        // 浜庢槸"浣曟椂缁欑鐐规墜鏌勩€佷綍鏃剁粰 伪 璇绘暟"姘歌繙鏄悓涓€涓瓟妗堚€斺€斾笉浼氫袱澶勮蛋宀斻€?        _rotLineLike = SelectionHandles.EndpointEditable(Doc.Selected, out var rotLine);
        _rotStartInclination = _rotLineLike
            ? SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(rotLine, 0),
                                                  SelectionHandles.EndpointCanvasPosition(rotLine, 1))
            : 0f;
        SelRotationInclination = _rotStartInclination;
        SelRotationReadsInclination = false;   // 鏉炬墜鍓嶆爣绛句笉鏄剧ず锛岃繖涓爣蹇楀彧鍦ㄦ嫋鍔ㄤ腑涓虹湡

        // 鍗曢€変竴涓浘褰紙鐭╁舰 / 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰級鈫?璇绘暟璧?*濮挎€佽**锛堣鏍?9.7锛夈€?        // 鍦嗕笉鍦ㄥ唴锛堣浆浜嗙湅涓嶅嚭鏉ワ級锛屽浘鍍?/ 绗旇抗鏀逛笉杩涙潵锛圥oseEditable 鍙閭ｅ洓绉嶏級銆?        // 闀滃儚鐘舵€佷篃鍦ㄨ繖閲岃涓€娆★細鎵嬪娍閲屽畠涓嶄細鍙橈紙涔樹笂鍘荤殑鏄函鏃嬭浆锛岃鍒楀紡鎭掓锛夈€?        _rotPoseLike = SelectionHandles.PoseEditable(Doc.Selected, out var rotPose);
        _rotMirrored = _rotPoseLike && SelectionHandles.IsMirrored(rotPose.Transform);
        _rotStartPose = _rotPoseLike ? SelectionHandles.PoseAngleDegrees(rotPose.Transform) : 0f;
        SelRotationPose = _rotStartPose;
        SelRotationReadsPose = false;          // 鍚屼笂锛氬彧鍦ㄦ嫋鍔ㄤ腑涓虹湡

        SelDragging = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 鑷鐢細鐩存帴璧颁竴娆?閫夋嫨鎵嬪娍鍒嗘祦"锛堟搷浣滄潯 / 鎵嬫焺 / 妗嗗唴鎷栧姩 / 鐐归€夐兘浠庤繖鍎胯繘锛夈€?    /// 杩斿洖 false = 杩欐鎸変笅娌¤閫夋嫨鎵嬪娍鎺ユ帀锛堣皟鐢ㄦ柟浼氳捣妗嗛€夛級銆?    ///
    /// 涓轰粈涔堟妸淇グ閿綋鍙傛暟锛氱偣閫夐噷鐨?Shift/Alt 鍒嗘敮瑕佺湅鐪熼敭鐩橈紝鑷娌℃硶鎸夌潃 Shift 璺戯紝
    /// 浜庢槸"璇婚敭鐩?杩欎竴姝ョ暀鍦ㄨ皟鐢ㄦ柟锛堥紶鏍囨寜涓嬮偅澶勶級锛屽垽瀹氬嚱鏁颁繚鎸佺函閫昏緫銆?    /// </summary>
    internal bool SelectionGestureForTest(float x, float y, bool shift = false, bool alt = false)
        => TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>鑷鐢細缁撴潫涓€娆￠€夋嫨鎵嬪娍锛堟澗鎵嬶級銆?/summary>
    internal void EndSelectionGestureForTest() => EndSelDrag();

    /// <summary>
    /// 鑷鐢細璧颁竴閬?鎸囬拡绉诲姩"閲?*娴眰鎷栧姩**閭ｄ竴娈碉紙绮楃粏婊戞潯 / 鑷畾涔夊彇鑹叉澘锛夈€?    /// 鐪熸満涓婅繖娈靛湪 `OnPointerMove` 閲屾帓鍦ㄥ伐鍏?switch 涔嬪墠锛堝拰宸ュ叿鏃犲叧锛夛紱
    /// 鑷娌℃硶鍚堟垚绯荤粺鎸囬拡娑堟伅锛屾墍浠ユ妸鍚屼竴灏忔閫昏緫鏆撮湶鍑烘潵锛堝拰涓婇潰閭ｆ潯鍚屼竴涓璺級銆?    /// </summary>
    internal void PanelDragMoveForTest(float x, float y)
    {
        if (_sliderDragging) { DragWidthSliderTo(x); return; }
        if (_colorDragging) DragColorPickerTo(x, y);
    }

    /// <summary>鑷鐢細缁撴潫娴眰鎷栧姩锛堟澗鎵嬶級鈥斺€旀粦鏉℃彁浜ゃ€佸彇鑹叉澘鎸夋寚閽堝湪涓嶅湪鏉垮唴鍐冲畾搴旂敤/鍙栨秷銆?/summary>
    internal void EndPanelDragForTest(float x, float y)
    {
        if (_sliderDragging) EndWidthSliderDrag();
        else if (_colorDragging) EndColorPickerDrag(x, y);
    }

    /// <summary>鑷鐢細鑷畾涔夊彇鑹叉澘寮€鐫€鍚椼€?/summary>
    internal bool CustomColorOpenForTest => CustomColorOpen;

    /// <summary>鑷/鍑哄浘鐢細鎸夌湡瀹炲叆鍙ｆ墦寮€鑷畾涔夊彇鑹叉澘锛堣捣鐐硅壊 = 閫変腑澧ㄨ抗/鎵嬮噷閭ｆ敮绗旓級銆?/summary>
    internal void OpenCustomColorForTest() => OpenCustomColor();

    /// <summary>
    /// 鎷栧姩涓細姣忓抚閮戒粠**鎸変笅閭ｄ竴鍒荤殑鍙樻崲**閲嶇畻锛岃€屼笉鏄湪涓婁竴甯х粨鏋滀笂缁х画涔樸€?    /// 鍚庤€呬細绱Н娴偣璇樊锛屾嫋寰楄秺涔呭亸寰楄秺澶氾紝鎾ら攢涔熷洖涓嶅埌鍘熸牱銆?    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        // 鎷栫鐐癸細鍙︿竴鏉¤矾锛堟敼鍑犱綍锛屾ā鍨嬩笉鍔級锛屽拰"鎷栧璞?鍒嗗紑璧般€?        if (_vertexDragging) { UpdateVertexDrag(x, y); return; }

        var cur = new Vector2(x, y);
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        // Alt = 涓存椂鍏虫帀鍚搁檮銆係hift 鏄?纭綉鏍?15掳"锛孉lt 鏄?瀹屽叏鑷敱"锛屼袱涓慨楗伴敭
        // 鍚勭涓€澶达紝涓棿閭ｆ。锛堥粯璁ょ殑 90掳 杞惛闄勶級涓嶇敤鎸夐敭銆?        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;

        Matrix3x2 m, localM;
        // 鐗规畩褰㈢姸鍚搁檮锛堣鏍?9.6锛?*鍙湪鎷栧洓瑙掓椂鍙兘鍑虹幇**锛氭暣浣撶Щ鍔ㄣ€佹棆杞竴寰嬩笉鍚?        // 锛堥偅涓や欢浜嬫敼鐨勬槸浣嶇疆鍜屽Э鎬侊紝鏀逛笉浜?杩欐槸涓嶆槸涓鏂瑰舰"锛夈€傛瘡甯у厛娓呯┖锛?        // 涓嬮潰閭ｆ潯鍒嗘敮鍚镐綇浜嗗啀濉€?        _shapeSnap = ShapeSnapKind.None;
        if (_dragIsMove)
        {
            // 鏁翠綋绉诲姩锛氭寚閽堝湪鐢诲竷涓婅蛋澶氬皯锛屽璞″氨璧板灏戙€?*涓嶈兘**鍦ㄦ鍧愭爣閲岀畻
            // 鍐嶅叡杞洖鏉モ€斺€旀鏄枩鐨勬椂鍊欓偅鏍蜂細璧板亸鏂瑰悜銆?            m = Matrix3x2.CreateTranslation(cur - _dragStartPoint);
        }
        else if (_dragHandle == SelHandle.Rotate)
        {
            // 鈥斺€?鏃嬭浆锛?*涓€甯т竴姝ュ湴绱Н**锛堣搴︿笉璁句笂闄愶紝瑙?SelRotationDegrees锛夆€斺€?            //
            // 瑙掑害鍦?*妗嗗潗鏍?*閲岄噺锛堝拰鐪熸鏂藉姞鐨勬棆杞煩闃靛悓涓€濂楄緭鍏ワ級銆?            // 閫変腑妗嗙幇鍦ㄤ竴寰嬭酱瀵归綈锛圱oCanvas 鏄崟浣嶉樀锛夛紝鎵€浠ユ鍧愭爣 == 鐢诲竷鍧愭爣锛?            // 閲忓嚭鏉ョ殑瑙掑害灏辨槸灞忓箷涓婄湅鍒扮殑瑙掑害鈥斺€旇繖涔熸鏄?璇绘暟鎸夌溂鐫涚湅鐨勬柟鍚?閭ｆ潯瑙勫垯銆?            var c = new Vector2((_dragFrame.Local.MinX + _dragFrame.Local.MaxX) * 0.5f,
                                (_dragFrame.Local.MinY + _dragFrame.Local.MaxY) * 0.5f);
            var p = _dragFrame.ToLocalPoint(cur);
            _rotAccumDeg += SelectionHandles.RotationStepDegrees(c, _rotPrevPoint, p);
            _rotPrevPoint = p;

            float localDeg;
            bool snapped;
            if (_rotLineLike)
            {
                // 鈥斺€?鍗曢€夌洿绾?/ 绠ご锛氳鏁颁笌鍚搁檮閮借蛋 **伪鈧€ + 螖**锛堢敤鎴?2026-09-18 婢勬竻锛夆€斺€?                //
                // 鐢ㄦ埛鍘熻瘽锛?鎸夌収楂樹腑鏁板杩欎釜鐩寸嚎鐨勫€炬枩瑙掞紝鐜板湪鏄?45 搴︼紝閭ｄ箞鎴戦偅涓棆杞墜鏌?                // 鍒氬紑濮嬭浆鐨勬椂鍊欏氨鏄?45 搴︺€傞偅涔堥€嗘椂閽堟槸涓烘锛岄『鏃堕拡涓鸿礋锛屽畠鍙互鏃犻檺杞笅鍘伙紝
                // 涔熷氨璇村彲浠ヨ浆鍒?360銆?20銆?000 澶氬害锛屾垨鑰呰璐熺殑 2000 澶氬害鈥︹€﹂櫎浜嗗垵濮嬭搴?                // 鏄璋冧竴涓嬩互澶栵紝鍏朵粬鍜屽師鏉ョ殑鏈€寮€濮嬬殑閫昏緫鏄竴鏍风殑銆?
                //
                // 浜庢槸杩欓噷**涓嶆姌瑙掋€佷笉鍙栨ā**锛氳鏁板氨鏄?`鎸変笅鏃剁殑 伪鈧€ + 杩欎竴鎷栫疮绉浆杩囩殑瑙?螖`
                // 锛埼?鏈潵灏辨棤涓婇檺锛岃 _rotAccumDeg锛夈€?5掳 鐨勭嚎閫嗘椂閽堣浆涓€鍦?= 405掳銆?                // 鍐嶈浆 = 765掳锛涢『鏃堕拡 400掳 = 鈭?55掳銆?                // 鍚搁檮鍦?*灞曞紑鍊?*涓婂仛锛岀壒娈婅鎸?180掳 鍛ㄦ湡锛?05.5掳 鍚稿埌 405掳锛夛紝
                // 鎵€浠?瑕佷笉瑕佸惛"鍜屾姌鍥為偅涓€鐗堝畬鍏ㄤ竴鏍凤紝鍙槸鍚稿畬鐨勬暟浠嶆槸灞曞紑鐨勩€?                float expanded = _rotStartInclination + _rotAccumDeg;
                float snappedDeg = SelectionHandles.SnapExpandedInclinationDegrees(expanded, shift, alt,
                                                                                  out snapped);
                if (snapped)
                {
                    // "瑕佽惤鍒伴偅涓杩樺緱澶氳浆澶氬皯搴?鈥斺€旀妸瀹冨姞杩涚疮绉锛?                    // 浜庢槸**鐭╅樀鐢ㄧ殑鏄惛杩囦箣鍚庣殑瑙?*锛屾爣绛句笂鐨勬暟鍜屽睆骞曚笂鐪嬪埌鐨勪竴鑷淬€?                    _rotAccumDeg += snappedDeg - expanded;
                }
                localDeg = _rotAccumDeg;
                SelRotationInclination = _rotStartInclination + localDeg;   // 灞曞紑鍊硷紝鏁呮剰涓嶆姌
                SelRotationReadsInclination = true;
            }
            else if (_rotPoseLike && _dragTargets.Length == 1)
            {
                // 鈥斺€?鍗曢€変竴涓浘褰細璇绘暟涓庡惛闄勯兘璧?*濮挎€佽**锛堣鏍?9.7锛夆€斺€?                //
                // 鍜岀洿绾块偅涓€鏀槸鍚屼竴濂楄处锛屽彧鏄洰鏍囪涓嶅悓锛堥偅杈规槸鍏釜鐗规畩瑙掞紝杩欒竟鏄?0/90锛夛細
                //   "鎸変笅鏃剁殑濮挎€佽 锛?绱Н瑙? 鈫?鍦ㄨ繖涓?*杩炵画**鐨勮涓婂惛 鈫?鎶婃渶灏忎慨姝?                //   鍔犲洖绱Н瑙掋€傚繀椤诲湪杩炵画瑙掍笂鍚革細鎶樿繃鐨?[0,180) 閲?"179.6掳" 绂?0掳 鍙湁 0.4掳锛?                //   浣嗗畠浠樊鏁存暣 180掳鈥斺€旂洿鎺ュ惛浼氭妸鍙亸 0.4掳 鐨勪笁瑙掑舰**缈昏繃鏉?*銆?                //
                // 闀滃儚杩囩殑鍥惧舰"鏈湴杞?+1掳"鍦ㄥ睆骞曚笂鏄?-1掳锛堣鍒楀紡涓鸿礋锛夛紝鎵€浠ヤ袱涓柟鍚戦兘瑕佷箻
                // _rotMirrored 鐨勭鍙凤紱鍚屼竴涓墜鍔块噷闀滃儚鐘舵€佷笉浼氬彉锛堜箻涓婂幓鐨勬槸绾棆杞級銆?                float sgn = _rotMirrored ? -1f : 1f;
                float expanded = _rotStartPose + sgn * _rotAccumDeg;
                float snappedExpanded = SelectionHandles.SnapExpandedPoseDegrees(expanded, shift, alt,
                                                                                out snapped);
                if (snapped)
                {
                    _rotAccumDeg += sgn * (snappedExpanded - expanded);
                    // 鍚镐綇鏃舵爣绛剧洿鎺ュ啓"**鍚稿埌鐨勯偅鏉＄嚎**"锛? / 90锛屾垨 Shift 缃戞牸涓婄殑瑙掞級锛?                    // 浠庣煩闃甸噷瑙ｅ嚭鏉ョ殑鍊煎湪娴偣鍣０涓嬪彲鑳藉嵃鎴?`180.0掳`锛堟暟瀛︿笂鍜?0掳 鏄悓涓€鏉＄嚎锛?                    // 浣嗙敤鎴疯鐨勫氨鏄?鎷栧埌璇绘暟 0掳 灏辫浆姝ｄ簡"锛夈€備袱鑰呭樊鍦?0.001掳 浠ュ唴锛?                    // 鎵€浠ヤ笅闈㈤偅鏉?鐭╅樀涓庢爣绛惧悓涓€涓"鐨勮嚜妫€鐓ф牱鎴愮珛銆?                    // 鍏堟寜 0.001掳 鎶逛竴閬嶆诞鐐瑰櫔澹板啀鎶橈細`-0.0000001` 鎶樿繘 [0,180) 浼氬彉鎴?179.9999999锛?                    // 鍗板嚭鏉ヨ繕鏄?"180.0掳"鈥斺€旀妸鍣０鎶规帀鎵嶆嬁寰楀埌骞插噣鐨?0.0锛堣繙缁嗕簬涓€浣嶅皬鏁扮殑鏄剧ず绮惧害锛夈€?                    SelRotationPose = SelectionHandles.FoldDegrees(MathF.Round(snappedExpanded, 3));
                }
                localDeg = _rotAccumDeg;
                SelRotationReadsPose = true;    // 娌″惛浣忔椂鏁板€煎湪涓嬮潰 m 绠楀嚭鏉ヤ箣鍚庡啀濉?            }
            else
            {
                // 鍏跺畠鎯呭喌锛堝閫?/ 鍥惧儚 / 鑷敱绗旇抗 / 鍦嗭級锛氱収鏃ц 螖銆佺収鏃у惛 90掳銆?                // 鍚搁檮浣滅敤鍦?*绱Н瑙?*涓婏細90掳 / 15掳 鐨勬暣鏁板€嶅湪璐熻搴︺€佽秴杩囦竴鍦堢殑瑙掑害涓婄収鏍峰寰椾笂銆?                localDeg = SelectionHandles.SnapRotationDegrees(_rotAccumDeg, shift, alt, out snapped);
                SelRotationReadsInclination = false;
            }

            // 杩欎竴娈垫槸"妗嗗潗鏍囧拰灞忓箷鍙嶆墜鏃讹紝鎶婅鏁扮炕鍥炵溂鐫涚湅鍒扮殑鏂瑰悜"鐨勯€氱敤淇濋櫓锛?            // 闀滃儚杩囩殑瀵硅薄 + 鏂滄鐨勭粍鍚堜笅锛屾湰鍦拌搴︾殑姝ｈ礋鍜屽睆骞曟槸鍙嶇殑銆?            // 閫変腑妗嗘敼鎴?*涓€寰嬭酱瀵归綈**涔嬪悗 ToCanvas 鎭掍负鍗曚綅闃碉紝杩欓噷鎭掍笉瑙﹀彂锛?            // 鐣欑潃鏄洜涓哄叕寮忔湰鏉ュ氨瑕佽鐩?妗嗘湁鑷繁鐨勬湞鍚?閭ｄ竴澶╋紙瑙?SelectionFrame 鐨勬敞閲婏級銆?            // 娉細瀹冨彧绠?螖 閭ｄ釜璇绘暟锛浳?鏄粠**鐢诲竷鍧愭爣**閲忓嚭鏉ョ殑锛屽ぉ鐢熷氨鏄溂鐫涚湅鍒扮殑鏂瑰悜銆?            SelRotationDegrees = SelectionHandles.IsMirrored(_dragFrame.ToCanvas) ? -localDeg : localDeg;
            SelRotationSnapped = snapped;
            SelRotating = true;

            // 鐭╅樀鐢?*鍚搁檮鍚庣殑閭ｄ釜鏁?*锛堟病闀滃儚鏃跺畠鍜屾爣绛句笂鐨勬暟涓€妯′竴鏍凤級銆?            localM = SelectionHandles.RotateMatrix(localDeg, c);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }
        else
        {
            // 鎵嬫焺鎹㈢畻鍦?*妗嗗潗鏍?*閲屽仛锛堢缉鏀剧殑閿氱偣鏄?瀵硅閭ｄ釜鎵嬫焺"锛屽彧鏈夊湪妗嗗潗鏍囬噷
            // 鎵嶆槸"娌跨潃妗嗙殑涓ゆ潯杈?锛夛紝鍐嶅叡杞洖鐢诲竷鍧愭爣锛歁 = F鈦宦?路 M_local 路 F
            //
            // 鎷?*鐭╁舰鍥涜**鏃跺厛闂竴鍙?瑕佷笉瑕佸惛鎴愭鏂瑰舰"锛堣鏍?9.6锛夈€傞『搴忔槸鍏堝惛闄勩€?            // 鍐嶅洖閫€鍒伴€氱敤鎹㈢畻锛氬惛浣忎簡灏辩敤鍚搁檮缁欑殑閭ｄ釜鐭╅樀锛屾病鍚镐綇涓€涓瓧閮戒笉鏀广€?            // 娉ㄦ剰瀹冨彧鏀?`localM`锛堟鍧愭爣閲岀殑鐭╅樀锛夛紝鍏辫江閭ｄ竴姝ョ収鏃р€斺€旇繖鏍?杞繃/闀滃儚杩囩殑
            // 妗?閭ｇ鎯呭舰锛堜粖澶╂鎭掕酱瀵归綈锛屼絾鍏紡鐣欑潃锛変笉浼氳缁曡繃銆?            if (SelectionHandles.TrySnapSquareCorner(_dragTargets, _dragHandle, _dragFrame,
                                                     cur, DpiScale, alt, out var squareM))
            {
                _shapeSnap = ShapeSnapKind.Square;
                _shapeSnapAnchor = cur;             // 鑳跺泭璐村湪鎸囬拡锛? 琚嫋鐨勯偅涓锛変笂
                localM = squareM;
            }
            else
            {
                localM = SelectionHandles.DragMatrix(_dragHandle, _dragFrame,
                                                     _dragStartPoint, cur, DpiScale, shift, shift, alt);
            }
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }

        // 濮挎€佽璇绘暟**浠庢渶缁堢煩闃甸噷瑙ｅ嚭鏉?*锛歚瀵硅薄鍙樻崲 脳 棰勮鐭╅樀` 姝ｆ槸娴姩灞傜敾閭ｄ竴甯х敤鐨勫紡瀛愶紝
        // 鎵€浠ユ爣绛句笂鐨勬暟灏辨槸灞忓箷涓婇偅涓Э鎬佲€斺€?鐭╅樀涓庢爣绛惧悓涓€涓"鏄瀯閫犲嚭鏉ョ殑锛屼笉鏄噾鍑烘潵鐨?        // 锛堢洿绾块偅涓€鏀綋骞村氨鏄湪杩欓噷瀵逛笉涓婏細鏍囩鐢ㄤ竴涓銆佺煩闃电敤鍙︿竴涓锛夈€?        // 鍚镐綇閭ｄ竴妗ｅ凡缁忓湪涓婇潰濉垚"鍚稿埌鐨勯偅涓"浜嗭紙0/90 鏇村ソ鐪嬶紝瑙侀偅閲岀殑娉ㄩ噴锛夛紝璺宠繃銆?        if (_rotPoseLike && _dragTargets.Length == 1 && !SelRotationSnapped)
            SelRotationPose = SelectionHandles.PoseAngleDegrees(_dragTargets[0].Transform * m);

        // 鏂规 B锛?*涓嶅姩妯″瀷**锛屽疄鏃朵綅绉诲彧娲诲湪 _selDragMatrix 閲岋紝鐢辨诞鍔ㄥ眰鐢婚瑙堛€?        //
        // 浠ュ墠杩欓噷鏄?姣忓抚 SetTransformLive"锛屽畠浼氭妸娓叉煋鐗堟湰鍙锋姮璧锋潵锛屼簬鏄唴瀹瑰眰
        // 姣忎竴甯ч兘瑕佹妸"璧拌繃鐨勯潰绉?閲岀殑鎵€鏈夌瑪杩归噸鐢讳竴閬嶁€斺€斿疄娴嬫暎甯冨叏灞忕殑涓€灏忔挳
        // 鏄?19ms/甯э紝鑰屽叾涓鎷栫殑鍙湁閭ｅ嚑鏉°€傜幇鍦ㄥ唴瀹瑰眰鍙湪**鎵嬪娍寮€濮?*琚爣鑴忎竴娆?        // 锛堟妸杩欎竴鎵逛粠鍧楅噷鎽樺嚭鍘婚偅涓€涓嬶級锛屼腑閫斾竴甯ч兘涓嶇銆?        //
        // 鑴忓尯 = 涓婁竴甯ч瑙堢殑浣嶇疆 鈭?杩欎竴甯ч瑙堢殑浣嶇疆锛氬悗缂撳啿閲岃汉鐫€鐨勬槸涓ゅ抚鍓嶇殑鐢婚潰锛?        // 鑰岃繖涓ゅ潡骞惰捣鏉ユ濂借鐩?涓ゅ抚涔嬮棿宸簡鍝簺鍍忕礌"锛堝拰浠ュ墠"鏃т綅 鈭?鏂颁綅"鍚屼竴濂楄处锛夈€?        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);
        _selDragMatrix = m;
        _dragPrevBounds = PreviewBounds(m);
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);

        // 鏂规 A锛氱湡鐨勫姩杩囨墠鏀惰楗帮紙鍗曞嚮涓€涓嬩笉璇ラ棯锛夈€?        if (!_selDragMoved
            && Vector2.Distance(cur, _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;

        _dirty = true;
    }

    /// <summary>
    /// 鏉炬墜锛氬厛鎶婃ā鍨嬫仮澶嶅埌鎸変笅閭ｄ竴鍒伙紝鍐嶆彁浜?*涓€鏉?*鍙樻崲鍛戒护銆?    ///
    /// 杩欐牱鎷栧姩杩囩▼涓殑瀹炴椂棰勮涓嶄骇鐢熸挙閿€璁板綍锛岃€屾挙閿€涓€姝ュ氨绮剧‘鍥炲埌鎷栧姩鍓?    /// 鈥斺€斾笉浼氬嚭鐜?鎷栫殑鏃跺€欏姩浜嗕笁鍗佹锛岃鎸変笁鍗佹鎾ら攢"閭ｇ浜嬨€?    /// </summary>
    private void EndSelDrag()
    {
        SelDragging = false;
        SelRotating = false;            // 搴︽暟鏍囩鍙湪鎷栧姩涓嚭鐜?        _rotAccumDeg = 0f;              // 涓嬩竴娆℃嫋鎷戒粠 0 寮€濮嬫暟锛堟爣绛句篃涓嶆樉绀轰簡锛?        SelRotationReadsInclination = false;   // 璇绘暟褰掍綅锛氫笅涓€娆℃寜涓嬫椂閲嶆柊鍒ゅ畾璇?伪 杩樻槸 螖
        SelRotationReadsPose = false;          // 濮挎€佽閭ｄ竴妗ｅ悓鏍峰綊浣?        _shapeSnap = ShapeSnapKind.None;       // "姝ｆ柟褰?閭ｉ鑳跺泭涔熷彧鍦ㄦ嫋鍔ㄤ腑鍑虹幇

        // 鎷栫鐐癸細鏀跺熬璧?鏀瑰嚑浣?閭ｆ潯璺紙涓€娆℃嫋鎷?= 涓€姝ユ敼鍑犱綍鐨勬挙閿€锛夈€?        if (_vertexDragging) { CommitVertexDrag(); return; }

        if (_dragTargets == null) return;

        // 涓€鎵归兘娌″緱鍔紙渚嬪閫変腑鐨勫叏閿佺潃锛夛細鍙妸鐘舵€佹敹骞插噣锛?*涓嶈**鎻愪氦绌哄姩浣?        // 锛堟彁浜や簡灏辨槸"鐐逛竴涓嬪涓€鏉＄┖鎾ら攢璁板綍"锛夈€?        if (_dragTargets.Length == 0)
        {
            _dragTargets = null;
            _dragStartXform = null;
            _dragHandle = SelHandle.None;
            _dragIsMove = false;
            _dragHitStroke = null;
            _pendingClone = null;
            _selDragMatrix = Matrix3x2.Identity;
            ReattachAfterDrag();
            _dirty = true;
            return;
        }

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            Doc.SetTransformLive(_dragTargets[i], _dragStartXform[i]);
            Doc.Dirty.Add(_dragTargets[i].PaddedBounds);
        }

        Doc.Selected.Clear();
        foreach (var t in _dragTargets) Doc.Selected.Add(t);
        // **涓€鐐规病绉诲姩灏卞埆杩涙挙閿€鏍?*锛氭寜涓€涓嬮€変腑妗嗭紙鎴栬€呯偣閫変竴鏉★級鍘熸湰浼氬鍑轰竴鏉?        // "鍘熸牱"鐨勬挙閿€璁板綍鈥斺€旂偣閫変竴澶氾紝鎾ら攢鏍堥噷鍏ㄦ槸杩欑绌鸿褰曘€?        if (_pendingClone != null)
        {
            // 鎷栧嚭鍓湰锛?*鎻掑叆鍓湰 + 绉诲姩鍓湰 = 涓€姝ユ挙閿€**锛堟嫋閿欎簡鎸変竴娆″氨鍏ㄥ洖鍘伙級銆?            if (_selDragMatrix.IsIdentity)
            {
                Doc.CommitCompound(_pendingClone);
            }
            else
            {
                var moveAct = new TransformObjectsAction(Doc.Selected, _selDragMatrix);
                moveAct.Redo(Doc);
                Doc.CommitCompound(_pendingClone, moveAct);
            }
            _pendingClone = null;
        }
        else if (!_selDragMatrix.IsIdentity)
        {
            Doc.ApplyTransform(_selDragMatrix);
        }

        // "鐐逛竴涓嬫妸澶氶€夋敹绐勬垚鍗曢€?锛氭寜涓嬫椂鍛戒腑浜嗕竴鏉°€佽€岃繖涓€娆?*涓€鐐规病绉诲姩** 鈫?鍙暀瀹?        // 锛圥PT/Figma 鐨勮涓猴級銆傜Щ鍔ㄨ繃灏辨甯告暣浣撴惉锛屼笉鏀剁獎銆?        if (_dragIsMove && _dragHitStroke != null && _selDragMatrix.IsIdentity
            && Doc.Selected.Count > 1 && Doc.Selected.Contains(_dragHitStroke))
            Doc.SelectOnly(new[] { _dragHitStroke });

        _dragTargets = null;
        _dragStartXform = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _dragHitStroke = null;
        _pendingClone = null;
        _selDragMatrix = Matrix3x2.Identity;

        // 鏂规 B锛氳繖涓€鎵瑰洖鍒板唴瀹瑰眰锛堜笅涓€娆￠噸鐢诲氨鎶婂畠浠敾杩涘潡閲岋級銆?        // 椤哄簭鏀惧湪鏈€鍚庯細涓婇潰閭ｆ潯"鎻愪氦鍙樻崲"宸茬粡鎶婃棫浣嶄笌鏂颁綅閮芥爣鑴忎簡锛?        // 鎵€浠ヨ繖涓€甯х殑閲嶇敾涓€瀹氫細甯︿笂瀹冧滑锛堣€屼笉鏄户缁敾棰勮锛夈€?        ReattachAfterDrag();

        _dirty = true;
    }

    /// <summary>
    /// 鎿嶄綔鏉℃寜閽€備笅鏍囦笌 SelectionHandles.BarButtonAt 鐨勮繑鍥炲€煎拰娓叉煋鏃剁殑
    /// 鍥炬爣鏁扮粍涓€涓€瀵瑰簲锛? 澶嶅埗 / 1 鍒犻櫎 / 2 宸﹀彸缈昏浆 / 3 涓婁笅缈昏浆 / 4 鏃嬭浆銆?    /// </summary>
    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
    {
        // 涓嬫爣 鈫?璇箟鍙湪 SelBarButton 閲屽啓涓€閬嶏紙娓叉煋銆佸懡涓€佸姩浣滀笁澶勫叡鐢ㄥ畠锛夈€?        var btn = (SelBarButton)index;
        switch (btn)
        {
            // **鏀惰捣宸ュ叿鏉?*锛堢敤鎴?2026-09-16 鏇存锛氭潯棣栭偅涓?鉁?涓嶆槸"鍙栨秷閫夋嫨"锛夈€?            // 鏀惰捣鍚庡彧鍓╀竴涓皬鍦嗛挳锛岀偣瀹冨睍寮€锛涙鍜屾墜鏌勭収鏃э紝鏀惰捣鐨勫彧鏄?鏉?銆?            case SelBarButton.Collapse:
                SelBarCollapsed = true;
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                CopyDragArmed = false;
                break;

            case SelBarButton.Color:
                if (SelPanelOpen == SelPanel.Ink)
                {
                    CloseCustomColor();                         // 鍏抽潰鏉?= 鑹叉澘涓€璧峰叧
                    SelPanelOpen = SelPanel.None;
                }
                else
                {
                    SelPanelOpen = SelPanel.Ink;
                }
                break;

            case SelBarButton.Lock:
                ToggleSelectionLock();
                break;

            case SelBarButton.Layer:
                CloseCustomColor();                             // 鎹㈤潰鏉?= 鑹叉澘璺熺潃鍏?                SelPanelOpen = SelPanelOpen == SelPanel.Layer ? SelPanel.None : SelPanel.Layer;
                break;

            case SelBarButton.Export:
                // **鐩存帴寮圭郴缁?鍙﹀瓨涓?**锛屾牸寮忓湪閭ｄ釜瀵硅瘽妗嗙殑绫诲瀷鏍忛噷閫夛紙瑙?ExportFormats锛夈€?                //
                // 鏃╁厛鍓嶈繖閲屽厛寮€涓€灞傛垜浠嚜宸辩殑涓ゆ牸闈㈡澘锛岀悊鐢辨槸"绯荤粺閭ｄ釜涓嬫媺鐐逛笉寮€"
                // 锛堢敤鎴锋姤鐨?閫変笉鍒?jpg"锛夈€傞偅涓湡鍥犲悗鏉ユ煡娓呬簡锛?*瑕嗙洊灞傛瘡绉掓姠涓€娆＄疆椤?*锛?                // 鎶婂璇濇鐨勪笅鎷夋尋鎺変簡锛堣 ReassertTopmost锛夈€傜湡鍥犱慨鎺変箣鍚庯紝
                // 鐢ㄦ埛 2026-09-17 鎻愮殑閭ｅ彞灏辨垚绔嬩簡鈥斺€?閭ｄ笅闈㈣繖涓や釜鍥炬爣灏辨病鏈夌敤浜嗗惂锛?                // 鐢ㄦ埛閮藉彲浠ヨ嚜宸变繚瀛樺浘鐗囦簡"锛氫袱澶勯€夋牸寮忓弽鑰岀粫锛屽垹鎺夎繕灏戜竴姝ャ€?                ExportSelectionPref();
                break;

            case SelBarButton.Library:
            {
                // **瀛樺叆鍥惧簱**锛氭妸閫変腑鐨勫璞″瓨鎴愪竴涓潯鐩紙纾佺洏閭ｇ偣浜嬪湪 ShapeLibrary 閲岋級銆?                // 鍙嶉鍙湁"闂竴涓?鈥斺€旇繖涓€鐗堟病鏈?toast锛涘瓨杩涘幓涔嬪悗浠庡浘褰㈤潰鏉挎渶鍚庨偅涓€娈?                // 銆屽浘搴撱€嶉噷鑳界湅瑙侊紙`--librarytest` 灏辨槸鐓ц繖鏉￠摼璺噺鐨勶級銆?                int saved = SaveSelectionToLibrary();
                Console.WriteLine(saved > 0
                    ? $"宸插瓨鍏ュ浘搴擄紙{saved} 涓璞★級"
                    : "瀛樺叆鍥惧簱澶辫触锛堥€変腑涓虹┖鎴栬€呭啓涓嶈繘鍘伙級");
                break;
            }

            // **杩涘叆/閫€鍑?澶嶅埗鎷栨嫿妯″紡"**锛屼笉鏄?鐐逛竴涓嬪師鍦板厠闅嗕竴浠?銆?            // 鎶?InkClass 鐨勭粨璁猴細鐐瑰嚮鍗冲厠闅嗛偅鐗?鍓湰鍥哄畾鍋忕Щ 24px銆佽惤鐐逛笉鍙帶"锛屽凡搴熷純锛?            // 鐜板湪鐐瑰浘鏍囧彧杩涙ā寮忥紙鍥炬爣楂樹寒 + 妗嗗彉铏氱嚎锛夛紝涔嬪悗鎸変綇閫変腑鍐呭鎷?= 鎷栧嚭鍓湰銆?            case SelBarButton.Copy:
                CopyDragArmed = !CopyDragArmed;
                break;

            // 缈昏浆缁?*妗嗚嚜宸辩殑杞?*銆傛鐜板湪涓€寰嬭酱瀵归綈锛屾墍浠ュ畠灏辨槸灞忓箷鐨勫乏鍙?涓婁笅缈烩€斺€?            // 鍜?妗嗘案杩滄鐫€"鍚屼竴鏉¤鍒欙紙瑙?SelectionFrame 鐨勬敞閲婏級銆?            case SelBarButton.FlipH:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: true)));
                break;
            case SelBarButton.FlipV:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: false)));
                break;

            case SelBarButton.Delete:
            {
                int n = Doc.Selected.Count, locked = 0;
                foreach (var s in Doc.Selected) if (s.Locked) locked++;
                Doc.DeleteSelected();
                Console.WriteLine(locked > 0
                    ? $"鍒犻櫎 {n - locked} 涓璞★紙{locked} 涓攣瀹氾紝娌″垹锛?
                    : $"鍒犻櫎 {n} 涓璞?);
                break;
            }
        }
        Laser.Clear();
        // 鍒犻櫎涔嬪悗閫夊尯鍙兘绌轰簡锛涙ā寮忚窡鐫€閫夊尯璧帮紙InkClass 鍚屾锛氶€夊尯娌′簡灏遍€€鍑猴級銆?        if (Doc.Selected.Count == 0) CopyDragArmed = false;
        _dirty = true;
    }

    /// <summary>
    /// 鎶?妗嗗潗鏍囦笅鐨勫彉鎹?缈昏瘧鎴?鐢诲竷鍧愭爣涓嬬殑鍙樻崲"锛?c>F鈦宦?路 M 路 F</c>銆?    ///
    /// 妗嗘槸鏂滅殑鏃跺€欙紝鍚屼竴鍙?娌挎鐨勬í杞存斁澶т袱鍊?鍦ㄧ敾甯冨潗鏍囬噷鏄釜鏂滅殑缂╂斁銆?    /// 鍏辫江灏辨槸鍦ㄤ袱濂楀潗鏍囦箣闂寸炕璇戣繖浠朵簨鈥斺€斾笉鐢ㄤ负"鏂滅潃鐨勬儏鍐?鍙﹀啓涓€濂楀叕寮忋€?    /// </summary>
    private static Matrix3x2 Conjugate(in Matrix3x2 frame, in Matrix3x2 localM)
    {
        if (frame.IsIdentity) return localM;
        if (!Matrix3x2.Invert(frame, out var inv)) return localM;
        return inv * localM * frame;
    }
    // =====================================================================
    //  鎵规敞閿洏妯″紡
    // =====================================================================

    /// <summary>
    /// 寮€鍏虫壒娉ㄩ敭鐩樻ā寮忋€?    ///
    /// 寮€锛氬幓鎺?WS_EX_NOACTIVATE 骞舵妸绐楀彛鎻愬埌鍓嶅彴锛岄敭鐩樺綊鎵规敞灞傦紝缂栬緫绫?    ///     蹇嵎閿紙Ctrl+Z / Ctrl+D / Delete / 鏂瑰悜閿€︼級鎵嶆湁鍦版柟钀藉湴銆?    /// 鍏筹細鍔犲洖 WS_EX_NOACTIVATE锛岃鐩栧眰鍥炲埌"姘镐笉鎶㈢劍鐐?锛岄敭鐩樿繕缁欎笅灞傜▼搴?    ///     鈥斺€旈偅鏃跺€欏彧鏈夊叏灞€鐑敭锛圕trl+Alt+Shift+鈥︼級鍙敤銆?    ///
    /// 鍙栬垗璇存竻妤氾細寮€鐫€鐨勬椂鍊欙紝鏀炬槧涓殑 PPT 鏀朵笉鍒伴敭鐩樸€?    /// </summary>
    internal void SetKeyboardMode(bool on)
    {
        KeyboardMode = on;
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (on) ex &= ~Native.WS_EX_NOACTIVATE;
            else ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            if (on)
            {
                Native.SetForegroundWindow(w.Hwnd);
                // **鍓嶅彴 + 鐒︾偣瑕佷竴璧风粰**锛?026-09-30锛夛細鍙?SetForegroundWindow 鐨勮瘽锛?                // 鍦ㄦ煇浜涜矾寰勪笅锛堜粠"琚棌杩?琚姠杩?鐨勭姸鎬佸洖鏉ワ級绐楀彛铏界劧鏄墠鍙帮紝
                // 閿洏鐒︾偣鍗磋繕鐣欏湪鍒汉閭ｅ効鈥斺€斿睆骞曚笂鐪嬬潃涓€鍒囨甯革紝鎸夐敭鍏ㄦ敹涓嶅埌銆?                Native.SetFocus(w.Hwnd);
            }
        }
        Console.WriteLine($"鎵规敞閿洏妯″紡 = {on}"
            + (on ? "锛堥敭鐩樺綊鎵规敞灞傦紱姝ゆ椂涓嬪眰绋嬪簭鏀朵笉鍒伴敭鐩橈級" : "锛堥敭鐩樿繕缁欎笅灞傜▼搴忥級"));
        _dirty = true;
    }

    /// <summary>
    /// **鎶婂墠鍙?閿洏瑕佸洖鏉?*鈥斺€?浠庡埆鐨勭姸鎬佸垏鍥炴壒娉ㄦ€?鐨勭粺涓€琛ヤ竵銆?    ///
    /// 涓轰粈涔堝繀椤绘湁杩欎竴鍙ワ細钘忕獥鍙ｏ紙`ScreenCapture.HiddenOverlay`锛? 鎽樼疆椤?/ 鎹㈢獥鍙ｆ牱寮?    /// 涔嬪悗锛屽墠鍙颁細琚郴缁熻浆缁欏埆浜猴紝鑰屾仮澶嶆樉绀虹敤鐨勬槸 `SW_SHOWNOACTIVATE`锛堜笉璁告姠鐒︾偣锛夛紝
    /// **瀹冧笉浼氳嚜宸卞洖鏉?*銆備簬鏄?`Ctrl+P` 杩欎簺搴旂敤鍐呭揩鎹烽敭鍏ㄨ鍒殑绐楀彛鏀惰蛋锛涙洿闅愯斀鐨勬槸
    /// **鎴浘鍙栨櫙涓殑 `Esc`/`Enter` 涔熸敹涓嶅埌**锛堝畠浠悓鏍烽潬鎴戜滑鐨勭獥鍙ｆ敹閿級锛?    /// 鑰屾垜浠殑绐楀彛 `WM_MOUSEACTIVATE 鈫?MA_NOACTIVATE`锛?*鐐瑰畠涔熶笉浼氭縺娲?*锛?    /// 鐢ㄦ埛鑷繁鏁戜笉鍥炴潵鈥斺€斿彧鑳介噸鍚蒋浠躲€?    ///
    /// 宸茬粡韪╄繃涓夋锛?*鍏ㄦ槸鍚屼竴涓潙**锛?    ///   鈶?绌块€忓紑寮€鍏冲叧锛堣 SetPassThrough 閭ｆ娉ㄩ噴锛夛紱
    ///   鈶?閫€鍑烘斁鏄狅紙瑙?Ppt.ExitPptMode锛夛紱
    ///   鈶?**鎴畬鍥?*锛堢敤鎴?2026-09-30 鎶ョ殑"鎴畬鍥句互鍚庣瑪鐨勫揩鎹烽敭娌℃湁鐢ㄤ簡"锛夈€?    /// 浠ュ悗鍑℃槸"浠庡埆鐨勭姸鎬佸垏鍥炴壒娉ㄦ€?鐨勫湴鏂归兘瑕佽皟瀹冦€傚箓绛夈€?    /// </summary>
    private void RegainKeyboardFocus()
    {
        if (!KeyboardMode || _windows.Count == 0) return;
        foreach (var w in _windows)
        {
            Native.SetForegroundWindow(w.Hwnd);
            Native.SetFocus(w.Hwnd);
        }
    }

    /// <summary>
    /// 鎵规敞閿洏妯″紡涓嬬殑鎸夐敭銆?*鍙湪 <see cref="KeyboardMode"/> 鎵撳紑鏃舵敹鍒般€?*
    ///
    /// 閿綅涓€寰嬬収 Windows 鐨勯€氱敤涔犳儻锛屼笉鑷繁鍙戞槑锛?    ///   Ctrl+Z 鎾ら攢 / Ctrl+Y 閲嶅仛 / Ctrl+A 鍏ㄩ€?/ Delete 鍒犻櫎 / Esc 鍙栨秷閫夋嫨
    ///   Ctrl+D 澶嶅埗涓€浠斤紙绯荤粺鍓创鏉胯繕娌℃帴锛屽厛鐢?D锛?    ///   鏂瑰悜閿Щ鍔?1 鍍忕礌锛屾寜浣?Shift 鏄?10 鍍忕礌
    ///
    /// 宸茬煡寰呮敼锛氭柟鍚戦敭鎸変綇浼氶噸澶嶈Е鍙戯紝姣忔閮芥槸涓€鏉℃挙閿€璁板綍銆傝鎺?杩炵画寰皟
    /// 鍚堝苟鎴愪竴姝?锛屽緱绛夌紪杈戝懡浠ゆ敮鎸佸悎骞讹紙鎾ら攢鏍堥噷鐩搁偦鍚岀被鍔ㄤ綔鍚堝苟锛夈€?    /// </summary>
    // ================= 宸ュ叿閿殑"鎵嬪娍"鐘舵€佹満 =================
    //
    // 鎵嬪啓鏉跨殑绗斾笂鍙湁涓や釜鎸夐挳锛屾墍浠ュ伐鍏烽敭瑕?涓€閿鐢?锛堢敤鎴?2026-09-29 瀹氾級锛?    //   路 鍗曞嚮             = 鍒囧埌瀹冿紱**宸茬粡鏄畠** 鈫?杩炴寜鎹㈣壊/鎹㈡。锛堝拰鐐归潰鏉块偅涓€鏍间竴涓鐭╋級
    //   路 蹇€熷弻鍑?鈮?50ms) = 涓诲伐鍏峰線鍚庤疆涓€鏍硷細绗?鈫?鑽у厜绗?鈫?婵€鍏夌瑪 鈫?姗＄毊 鈫?閫変腑 鈫?绗?    //   路 鎸変綇(鈮?.6s)      = 鍥炵涓€涓鑹?/ 绗竴妗?    //
    // 涓轰粈涔堣"寤惰繜 250ms 缁撶畻杩炴寜鎹㈣壊"锛氬弻鍑诲拰"鍚岄敭杩炴寜"鍙兘闈犳椂闂村垎寮€銆?    // 寤惰繜鍙惤鍦?*鏈€鍚庝竴鍑?*涓婏細杩炴寜鏃朵笅涓€鍑讳竴鍒板氨绔嬪埢缁撶畻锛屾墜鎰熸槸杩炵画鐨勩€?    private const double ToolKeyDoubleMs = 350;   // 涓ゅ嚮闂撮殧 鈮?瀹?= 鍙屽嚮
    private const double ToolKeyHoldMs = 600;     // 鎸変綇 鈮?瀹?= 闀挎寜
    // 杩炴寜鍔ㄤ綔锛堟崲鑹?鎹㈡。锛夊湪**鏉炬墜鍚?*鍐嶇瓑杩欎箞涔呮墠缁撶畻銆?    // 鈿?蹇呴』 > ToolKeyDoubleMs锛氬弻鍑绘槸"绗簩娆℃寜涓嬭惤鍦ㄤ笂娆℃澗鎵嬪悗 350ms 鍐?锛?    //   缁撶畻鑻ユ瘮瀹冩棭锛屽弻鍑诲氨浼氳"鍏堟崲浜嗚壊銆佸啀鍒囧伐鍏?锛?026-09-29 鑷繁韪╄繃锛夈€?    private const double ToolKeyRepeatMs = 400;

    private sealed class KeyGesture
    {
        public double DownAt = -1;      // 鏈鎸変笅鏃跺埢锛?1 = 娌℃寜鐫€
        public double UpAt = -1;        // 涓婃鏉惧紑鏃跺埢锛?1 = 涓嶅弬涓庡弻鍑诲垽瀹氾紙鍒氶暱鎸夊畬锛?        public bool HoldFired;          // 鏈鎸変綇宸茬粡瑙﹀彂杩囬暱鎸?        public bool WantsRepeat;        // 杩欎竴鍑昏惤鍦?宸茬粡鍦ㄨ繖涓伐鍏蜂笂" 鈫?鏉炬墜鍚庤鎹㈣壊/鎹㈡。
        public double PendingAt = -1;   // 寰呯粨绠楃殑"杩炴寜鍔ㄤ綔"鏃跺埢
        public KeyAction PendingWhat;
    }

    private readonly Dictionary<KeyAction, KeyGesture> _gestures = new();

    private KeyGesture Gest(KeyAction a)
    {
        if (!_gestures.TryGetValue(a, out var g)) _gestures[a] = g = new KeyGesture();
        return g;
    }

    /// <summary>杩欎簺閿蛋鎵嬪娍鐘舵€佹満锛堝崟鍑?鍙屽嚮/闀挎寜锛夛紝鍏朵綑閿収鏃т竴鎸変竴鍔ㄣ€?/summary>
    private static bool IsToolKey(KeyAction a) => a
        is KeyAction.ToolPen or KeyAction.ToolHighlighter or KeyAction.ToolLaser
        or KeyAction.ToolEraser or KeyAction.ToolPixelEraser or KeyAction.ToolMarquee;

    /// <summary>涓诲伐鍏风殑杞崲椤哄簭锛堝弻鍑诲線鍚庝竴鏍硷級銆?/summary>
    private static readonly Tool[] MainToolCycle =
        { Tool.Pen, Tool.Highlighter, Tool.Laser, Tool.Eraser, Tool.Marquee };

    private static Tool ToolOf(KeyAction a) => a switch
    {
        KeyAction.ToolPen => Tool.Pen,
        KeyAction.ToolHighlighter => Tool.Highlighter,
        KeyAction.ToolLaser => Tool.Laser,
        KeyAction.ToolEraser => Tool.Eraser,
        KeyAction.ToolPixelEraser => Tool.PixelEraser,
        _ => Tool.Marquee,
    };

    /// <summary>鍙屽嚮锛氫富宸ュ叿寰€鍚庤疆涓€鏍硷紙鍥惧舰/鎴浘杩欎簺涓嶅湪寰幆閲?鈫?浠庣瑪寮€濮嬶級銆?/summary>
    private void NextMainTool()
    {
        int i = Array.IndexOf(MainToolCycle, Tool);
        var next = MainToolCycle[(i + 1) % MainToolCycle.Length];
        SwitchTool(next);
        Console.WriteLine($"宸ュ叿閿弻鍑?鈫?{ToolName(next)}");
    }

    private bool ToolKeyDown(KeyAction a, double now)
    {
        var g = Gest(a);
        if (g.DownAt >= 0) return true;                            // 鑷姩閲嶅鐨勬寜涓嬶細蹇界暐锛岀瓑鏉炬墜
        bool dbl = g.UpAt >= 0 && now - g.UpAt <= ToolKeyDoubleMs;
        g.DownAt = now;
        g.HoldFired = false;
        g.WantsRepeat = false;
        if (dbl)
        {
            CancelPending(g);
            g.UpAt = -1;                                            // 鍙屽嚮鍚庝笉鍐嶈繛鐫€鍒?绗笁鍑?
            NextMainTool();
            return true;
        }
        var target = ToolOf(a);
        if (Tool != target) { SwitchTool(target); return true; }     // 鍗曞嚮锛氱珛鍗冲垏锛堣蹇級
        // 宸茬粡鍦ㄨ繖涓伐鍏蜂笂 鈫?鏉炬墜鍚?400ms 鎵ц"杩炴寜鍔ㄤ綔"锛堢瓑涓€绛夌湅鏄笉鏄弻鍑伙級
        g.WantsRepeat = true;
        return true;
    }

    private bool ToolKeyUp(KeyAction a, double now)
    {
        var g = Gest(a);
        if (g.DownAt < 0) return false;
        double held = now - g.DownAt;
        g.DownAt = -1;
        if (held >= ToolKeyHoldMs || g.HoldFired) { g.UpAt = -1; g.WantsRepeat = false; return true; }
        g.UpAt = now;                                               // 鐭寜锛氳涓嬫潵缁欏弻鍑诲垽瀹氱敤
        if (g.WantsRepeat)
        {
            g.WantsRepeat = false;
            g.PendingAt = now + ToolKeyRepeatMs;
            g.PendingWhat = a;
        }
        return true;
    }

    /// <summary>姣忓抚涓€娆★細闀挎寜鍒ゅ畾 + 杩炴寜鍔ㄤ綔鐨勫欢杩熺粨绠椼€?/summary>
    private void PumpKeyGestures()
    {
        double now = NowMs;
        foreach (var (a, g) in _gestures)
        {
            if (g.DownAt >= 0 && !g.HoldFired && now - g.DownAt >= ToolKeyHoldMs)
            {
                g.HoldFired = true;
                CancelPending(g);
                ResetToolToFirst(a);
                continue;
            }
            if (g.PendingAt >= 0 && now >= g.PendingAt)
            {
                var what = g.PendingWhat;
                CancelPending(g);
                DoToolKeyRepeat(what);
            }
        }
    }

    private static void CancelPending(KeyGesture g) => g.PendingAt = -1;

    /// <summary>杩炴寜瑕佸共鐨勪簨锛?*鍙湁绗?鑽у厜绗旀崲鑹?*銆?    /// [2026-10-05 鐢ㄦ埛瀹歖 姗＄毊"鏁寸瑪/闈㈢Н"銆佹閫?鐭╁舰/濂楃储"涓嶅啀鍚岄敭鍒囨崲鈥斺€旂埍鐢ㄥ摢绉嶅氨涓€鐩寸敤鍝锛?    /// 瀛愮被鍨嬪幓闈㈡澘鐨勪笂甯﹂噷閫夛紙"鍐嶇偣鍚屼竴鏍兼崲妗?涔熼殢涔嬪彇娑堬紝瑙?FullUi.Activate锛夈€?/summary>
    private void DoToolKeyRepeat(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.ToolPen: CycleBandColor(highlighter: false); break;
            case KeyAction.ToolHighlighter: CycleBandColor(highlighter: true); break;
            // 姗＄毊 / 妗嗛€夛細杩炴寜涓嶅仛浜嬶紙瀛愮被鍨嬪浐瀹氾紝闈㈡澘涓婂甫閲岄€夛級銆?        }
    }

    /// <summary>闀挎寜锛氬洖绗竴涓鑹诧紙鍙绗?/ 鑽у厜绗旓紱瀛愮被鍨嬩笉鍐嶇敱閿洏鏀癸級銆?/summary>
    private void ResetToolToFirst(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.ToolPen:
                SetColorFromUi(InkPalette.PenBand[0].Color);
                Console.WriteLine($"闀挎寜 鈫?绗斿洖鍒般€寋InkPalette.PenBand[0].Name}銆?);
                break;
            case KeyAction.ToolHighlighter:
                SetColorFromUi(InkPalette.HighlighterBand[0].Color);
                Console.WriteLine($"闀挎寜 鈫?鑽у厜绗斿洖鍒般€寋InkPalette.HighlighterBand[0].Name}銆?);
                break;
            // [2026-10-05 鐢ㄦ埛瀹歖 姗＄毊 / 妗嗛€夌殑瀛愮被鍨嬩笉鍐嶇敱閿洏锛堥暱鎸夛級鏀癸紱闈㈡澘涓婂甫閲岄€夈€?        }
    }

    /// <summary>
    /// 杩炴寜鎹㈣壊锛氬湪鑹插甫閲屽線鍚庤蛋涓€鏍硷紙杞湀锛夈€?    ///
    /// 鈿?**鐢ㄨ涓嬫潵鐨勫簭鍙疯蛋锛屼笉闈?鎸夐鑹插€兼壘褰撳墠浣嶇疆"**鈥斺€?026-09-30 淇殑鐪?bug锛?    /// 浠ュ墠姣忔寜涓€涓嬮兘鎷垮綋鍓嶉鑹插幓鑹插甫閲屽尮閰嶏紝瀹瑰樊 0.10 鎶娿€屾繁钃?/ 钘忛潚銆嶈鎴愬悓涓€涓紝
    /// 浜庢槸鍦?澧ㄧ豢鈫掗厭绾⑩啋钘忛潚鈫?琚綋鎴愭繁钃?鈫掑ⅷ缁?涔嬮棿鏉ュ洖韫︼紝鐢ㄦ埛瀹炴祴"鍙湁鍚庨潰涓夌鍦ㄥ垏"銆?    /// 鐜板湪搴忓彿鍙湪 SetColorFromUi锛堝埆浜烘敼浜嗚壊锛夋椂鍚屾锛屽惊鐜湰韬案杩?+1銆?    /// </summary>
    private void CycleBandColor(bool highlighter)
    {
        var band = highlighter ? InkPalette.HighlighterBand : InkPalette.PenBand;
        int idx = highlighter ? _hlColorIdx : _penColorIdx;
        int next = ((idx % band.Length) + 1) % band.Length;
        if (highlighter) _hlColorIdx = next; else _penColorIdx = next;
        SetColorFromUi(band[next].Color);      // 鑽у厜绗旂殑閫忔槑搴︾敱 SetColorFromUi 鑷繁鍔?        Console.WriteLine($"杩炴寜 鈫?{band[next].Name}");
    }

    /// <summary>褰撳墠绗旇壊 / 鑽у厜鑹插湪鑹插甫閲岀殑搴忓彿锛堥粯璁?绾?/ 鑽у厜榛勶級銆?/summary>
    private int _penColorIdx = 1;
    private int _hlColorIdx;

    private static bool CloseColor(Color4 a, Color4 b) =>
        Math.Abs(a.R - b.R) < 0.10f && Math.Abs(a.G - b.G) < 0.10f && Math.Abs(a.B - b.B) < 0.10f;

    /// <summary>涓婃鐢ㄧ殑姗＄毊褰㈡€侊紙鏁寸瑪鎿?/ 闈㈢Н鎿︼級鈥斺€旀寜 Ctrl+E 鏃跺垏鍥炲畠锛岃 SetUiPref("eraserKind")銆?/summary>
    private Tool _eraserKind = Tool.Eraser;

    /// <summary>鑷鐢細璁剧疆"璁颁綇鐨勬鐨绫?锛堜笁鎸?/ 鎵嬫帉鎿﹁窡闅忓畠锛屽拰榧犳爣鍚屾簮锛夈€?/summary>
    internal Tool EraserKindForTest { get => _eraserKind; set => _eraserKind = value; }

    /// <summary>
    /// 宸ュ叿閿殑**鍗曞嚮**閫昏緫锛?026-09-30 鏀跺彛锛氬弻鍑?闀挎寜閭ｅ鎵嬪娍鍏ㄩ儴鍙栨秷锛屽彧鐣欏崟鍑伙紱
    /// 2026-10-05 鍐嶆敹锛?*鍙湁绗?鑽у厜绗?宸茬粡鏄畠 鈫?鎹㈣壊"**锛屾鐨?妗嗛€夎繛鎸変笉鍐嶆崲瀛愮被鍨嬶級锛?    ///   路 涓嶆槸杩欎釜宸ュ叿 鈫?鍒囪繃鍘?    ///   路 宸茬粡鏄畠    鈫?绗?鑽у厜绗旀崲棰滆壊锛涙鐨?妗嗛€変粈涔堥兘涓嶅仛
    ///
    /// **绌块€忔ā寮忎笅鏁翠釜澶辨晥**锛堢敤鎴?2026-09-30 鎷嶆澘锛?寮€浜嗙┛閫忎互鍚庯紝绗斻€佹鐨繖浜涘揩鎹烽敭
    /// 搴旇灏辨病鏈夌敤浜嗭紝绛夐€€鍑虹┛閫忔墠鏈夌敤"锛夛細绌块€?= "涓嶈兘鐢?锛岃繖鏃舵崲宸ュ叿/鎹㈣壊閮芥病鏈夌潃钀斤紝
    /// 鑰屼笖"宸茬粡涓嶆槸绗斾簡銆侀鑹插嵈杩樺湪鍙?姝ｆ槸鐢ㄦ埛鎶ョ殑閭ｄ釜鎬姸鎬併€傛兂鐢荤敾鍏堥€€鍑虹┛閫?    /// 锛堝叏灞€ `Ctrl+Alt+Shift+T` / 鐐圭┛閫忔牸锛夛紝宸ュ叿閿殢鍗虫仮澶嶃€?    /// 鍙尅**閿洏**杩欎袱鏉¤矾锛堝簲鐢ㄥ唴閿?+ 鏀炬槧涓存椂鍏ㄥ眬閿紝閮芥眹鍒拌繖閲岋級锛?    /// 闈㈡澘涓婇偅涓€鏍间笉鍦ㄦ鍒椻€斺€旂偣瀹冧粛鐒?椤烘墜鍏崇┛閫?+ 鎹㈠伐鍏?锛堟病閿洏鐨勬暀瀹ら潬瀹冿級銆?    /// </summary>
    private void ToolKeyPress(KeyAction a)
    {
        if (PassThrough)
        {
            Console.WriteLine("绌块€忔ā寮忎笅锛氬伐鍏烽敭涓嶅搷搴旓紙鍏堥€€鍑虹┛閫忥級");
            return;
        }
        var target = ToolOf(a);
        // 姗＄毊锛氬洖鍒?涓婃鐢ㄧ殑閭ｄ竴绉嶅舰鎬?锛堟暣绗?闈㈢Н锛夛紝涓嶆槸姘歌繙鍥炴暣绗旀摝
        if (target == Tool.Eraser) target = _eraserKind;
        if (Tool != target) { SwitchTool(target); return; }
        DoToolKeyRepeat(a);
    }

    // =====================================================================
    //  鍛煎嚭鐩橈紙Ctrl+Alt+Shift+Q锛夛細鎸変綇 鈫?鍒掑悜鎵囧尯 鈫?鏉炬墜
    // =====================================================================
    //
    // 鏉ラ緳鍘昏剦锛氥€婅皟鐮?绗旈敭鏂规.md銆嬮檮褰?C/D锛堥敭鐩樺懠鍑虹増锛涚瑪韬敭鐗堢暀寰呯湡鏈哄疄娴嬬‖浠讹級銆?    // 琛屼负涓€鍙ヨ瘽锛?*鎸変綇鎵嶅嚭鏉ョ殑鏍囪抗鑿滃崟**鈥斺€旀寜鐫€涓嶅姩浼氱湅鍒扮洏锛?20ms 鍐呯洿鎺ュ垝璧?=
    // 鐩樹笉闂紙鐔熸墜璺級锛屾澗鎵嬬‘璁ゃ€丒sc / 钀界瑪 / 鏉惧湪涓績 = 鍙栨秷銆?    //
    // 鍜屼富绋嬪簭鍏跺畠閮ㄥ垎鐨勬帴鍙ｏ紝鍏ㄩ儴鐓х幇鏈夎涔夛細
    //   路 **绌块€忛噷鐓ф牱鑳藉嚭鐩?*锛?026-10-04 鐢ㄦ埛瀹氾級锛氬畠鏄?浠庝笅灞傛妸绗旀姠鍥炴潵"鐨勫叆鍙ｏ紝
    //     閫夋墖鍖?= 閫€鍑虹┛閫?+ 鎹㈠伐鍏?閫夎壊锛?*鍒绘剰涓嶅悓浜?*宸ュ叿閿?8.5 閭ｆ潯"绌块€忎笉鍝嶅簲"锛夛紱
    //   路 **2026-10-04 璧峰畠鏄父椹诲叏灞€閿?*锛坄Ctrl+Alt+Shift+Q`锛夛細鍓嶅彴鏄?PPT/WPS
    //     涔熺収鏍峰嚭鐩橈紝涓嶅啀闇€瑕佷复鏃跺叏灞€閿〃閭ｄ竴璺紱
    //   路 鎵囧尯閲岄€夊伐鍏?= 鍜屾寜 Ctrl+P/I/L/E/M **鍚屼竴鏉″懡浠?*锛堝惈"宸茬粡鏄畠 鈫?鎹㈣壊/鎹㈡。"锛夛紱
    //   路 棰滆壊鎵囧尯 = "缁欐垜杩欐敮棰滆壊鐨勭瑪"锛堜笉鍦ㄧ瑪涓婂氨鍒囧埌绗旓紝璧?SwitchTool锛夈€?    //
    // 鎵囧尯椤哄簭锛堜粠鍖楄捣銆侀『鏃堕拡锛?*鎺掑簭 V-a**锛氫笂涓嬪乏鍙冲洓涓浣嶇粰鍓嶅洓楂橀銆佸洓瑙掔粰娆￠锛?    // 鐢ㄦ埛 2026-09-30 瀹氾級锛氱瑪 / 榛?/ 绾?/ 钃?/ 姗＄毊 / 妗嗛€?/ 鑽у厜绗?/ 婵€鍏夈€?    // 姝ｄ綅 = 绗斅风孩路姗＄毊路鑽у厜绗旓紱鍥涜 = 榛懧疯摑路妗嗛€壜锋縺鍏夈€傞粦绾㈣摑 = `InkPalette.PenBand`
    // 鐨勫墠涓変釜锛堣壊甯︽湰鏉ュ氨鏄?甯哥敤鐨勬帓鍓嶉潰锛氶粦绾㈣摑缁库€?锛夛紝涓斾繚鎸侀『鏃堕拡鐩搁偦銆?    // 灏哄涓庤瑙夎鏍煎榻愶紙2026-09-30 v4 瀹氱锛氭柟妗?S銆屽叏鎵囬潰銆嶃€佺洿寰?192锛夛細
    // 鐩樺崐寰?96銆佹鍖?24銆侀攣瀹?36锛涚幆甯?40鈫?6銆佸浘鏍?24 灞呬腑鍦?R68锛堣 Overlay锛夈€?    // 鍒や綅涓€鐩存槸**鎸夎搴﹀垎 45掳 鎵囧尯**锛屾墖闈㈠寲鍙崲鐢绘硶锛氭鍖?閿佸畾/婊炲洖涓€涓瓧娌″姩銆?    // internal锛堜笉鏄?private锛夛細鑷瑕佹嬁瀹冨拰 Overlay 鐨勬墖闈㈠嚑浣曞琛ㄢ€斺€?    // "鍥炬爣鐜?卤 鍥炬爣鍗婂緞"蹇呴』钀藉湪锛堥攣瀹氳窛绂? 鐩樺崐寰勶級閲岋紝鏀瑰崟杈瑰繕鍙︿竴杈瑰氨浼氱孩銆?    internal const float RadialRadiusLogical = 96f;
    internal const float RadialDeadZoneLogical = 24f;
    internal const float RadialLockLogical = 36f;
    private const double RadialShowDelayMs = 120;     // 鍑虹洏寤惰繜锛堢啛鎵嬭矾锛氫笉绛夌洏鐩存帴鍒掞級
    private const double RadialTimeoutMs = 5000;      // 闃插憜锛氭寜澶箙娌℃澗鎵嬪氨鑷鍙栨秷

    internal bool RadialPaletteActive { get; private set; }
    internal bool RadialPaletteVisible { get; private set; }
    internal int RadialPaletteSector { get; private set; } = -1;
    internal float RadialCenterX, RadialCenterY;      // 鐢诲竷鍧愭爣锛堝拰 PointerX/Y 鍚屾簮锛?
    private double _radialOpenedAtMs;
    private bool _radialMoved;                        // 绂诲紑杩囬攣瀹氳窛绂伙紙涓ぎ鏂囨鐢級
    private uint _radialVk = 0x51;                    // 鍛煎嚭閿殑涓婚敭锛堟澗鎵嬭疆璇㈡寜瀹冩煡锛?
    /// <summary>鎵囧尯鍚嶏細鐢荤洏銆佹棩蹇椼€佽嚜妫€鍏辩敤涓€浠斤紙椤哄簭 = 浠庡寳椤烘椂閽堬紝V-a锛夈€?/summary>
    internal static readonly string[] RadialSectorNames =
        { "绗?, "榛?, "绾?, "钃?, "姗＄毊", "妗嗛€?, "鑽у厜绗?, "婵€鍏? };

    /// <summary>涓ぎ鏂囨瑕佺敤鐨?鍒掕繃鍙堝洖涓績"鍒ゆ嵁锛堢洏寮€鐫€鏃舵墠鏈夋剰涔夛級銆?/summary>
    internal bool RadialMovedForDraw => _radialMoved;

    /// <summary>鎵撳紑鍛煎嚭鐩橈紙鎸変綇鐨勯偅涓€鍒伙級銆傛潯浠朵笉婊¤冻灏遍潤榛樹笉鍔ㄣ€?/summary>
    /// <remarks>
    /// **绌块€忛噷涔熻兘寮€**锛?026-10-04 鐢ㄦ埛瀹氾級锛氬懠鍑虹洏鍗囦负鍏ㄥ眬閿氨鏄负浜?鍦ㄥ埆鐨勭▼搴?    /// 鍓嶉潰涔熻兘蹇€熸妸绗旇皟鍑烘潵"锛岀┛閫忓紑鐫€鏃舵寜瀹?*鐓у父鍑虹洏**鈥斺€旈€変竴涓墖鍖哄氨绛変簬
    /// "鎴戠幇鍦ㄥ氨瑕佸啓"锛岄『鎵嬮€€鍑虹┛閫忥紙鍜岀偣闈㈡澘宸ュ叿鏍煎悓涓€鏉¤矾锛岃 CommitRadialPalette锛夈€?    /// 杩?*鍒绘剰涓嶅悓浜?*宸ュ叿閿?`Ctrl+P/I/L/E/M`锛氶偅浜斾釜鍦ㄧ┛閫忎笅涓嶅搷搴旓紙2026-09-30 瀹氱殑
    /// "绌块€?= 閿洏褰掍笅灞?锛夛紝鍛煎嚭鐩樻槸"浠庝笅灞傛妸绗旀姠鍥炴潵"鐨勯偅涓€涓叆鍙ｏ紝瑙勫垯涓嶅悓銆?    ///
    /// 鈿?浠ｄ环锛氱┛閫忔椂鎴戜滑**鏀朵笉鍒伴紶鏍囩Щ鍔ㄦ秷鎭?*鈥斺€擿WS_EX_TRANSPARENT` 鎶婅鐩栧眰
    /// 浠庣郴缁熻緭鍏ラ噷鏁翠釜鎽樻帀锛堣 `_uiInputHwnd` 閭ｆ娉ㄩ噴锛夈€傛墍浠ョ┛閫忛噷鐩樺績涓庢柟鍚?    /// 閮藉緱闈犵郴缁熷厜鏍囩殑褰撳墠浣嶇疆锛坄GetCursorPos`锛夛紝涓嶈兘绛夐紶鏍囨秷鎭€?    /// </remarks>
    private void OpenRadialPalette()
    {
        if (RadialPaletteActive) return;
        if (CaptureActive) return;
        if (_drawing)
        {
            Console.WriteLine("涔﹀啓涓細鍛煎嚭鐩樹笉鍝嶅簲锛堟姮绗斿悗鍐嶆寜锛?);
            return;
        }

        // 绌块€忔椂 PointerX/Y 鍋滃湪鏃т綅缃紙鏀朵笉鍒扮Щ鍔ㄦ秷鎭級锛岀洏蹇冭鐢ㄧ郴缁熷厜鏍囩殑褰撳墠浣嶇疆銆?        SyncPointerFromCursor();

        // 璁颁笅杩欐瀹為檯缁戝畾鐨勪富閿紙閿綅鍙敼锛涙澗鎵嬭疆璇㈡寜瀹冩煡锛屽啓姝?Q 浼氬湪鏀归敭鍚庡け鐏碉級銆?        // 鈿?2026-10-04 璧峰懠鍑虹洏鏄?*鍏ㄥ眬**閿紙`Ctrl+Alt+Shift+Q`锛夛細浣滅敤鍩熷繀椤绘煡 Global锛?        // 鏌ユ壒娉ㄥ唴浼氭煡涓嶅埌锛堢敤鎴锋敼閿悗鏇存槸鐩存帴澶辩伒锛夈€?        var binding = Keys.Find(KeyScope.Global, KeyAction.RadialPalette);
        _radialVk = binding != null && binding.Chord.IsValid ? binding.Chord.Vk : 0x51;

        RadialPaletteActive = true;
        RadialPaletteVisible = false;          // 120ms 涔嬪悗锛堟垨绉诲姩涔嬪悗锛夋墠鐪熸鐢诲嚭鏉?        RadialPaletteSector = -1;
        _radialMoved = false;
        _radialTouchMode = false;              // 閿洏/榧犳爣閭ｅ锛氭澗鎵嬭疆璇㈡寜閿?        _radialSticky = false;
        _radialOpenedAtMs = NowMs;
        RadialCenterX = PointerX;              // 鐩樺績 = 鎸変笅鐨勯偅涓€鍒绘寚閽堝湪鍝?        RadialCenterY = PointerY;
        _dirty = true;
        Console.WriteLine(PassThrough
            ? "鍛煎嚭鐩橈紙绌块€忎腑锛夛細鎸変綇鍒掑悜鎵囧尯锛屾澗鎵?= 閫€鍑虹┛閫?+ 鍒囧埌瀹冿紙鏉惧湪涓績/鍒掑洖涓績/钀界瑪 = 鍙栨秷锛?
            : "鍛煎嚭鐩橈細鎸変綇鍒掑悜鎵囧尯锛屾澗鎵嬬‘璁わ紙鏉惧湪涓績/鍒掑洖涓績/钀界瑪 = 鍙栨秷锛?);
    }

    /// <summary>
    /// **瑙﹀睆鐗堝懠鍑虹洏**锛?026-10-05锛夛細涓ゆ寚闀挎寜锛堝垝鍔ㄦ澗鎵嬬‘璁わ級鎴栦袱鎸囪交鐐癸紙鐣欏湪鐩樹笂鐐归€夛級銆?    ///
    /// 涓庨敭鐩樼増鐨勫樊鍒氨涓ゆ潯锛?    ///   鈶?鏉炬墜涓嶆槸"杞鎸夐敭"锛岃€屾槸**鐢辫Е鐐归┍鍔?*鈥斺€斾袱鏍规墜鎸囩殑骞冲潎浣嶅氨鏄柟鍚戯紙TouchMoveDispatch
    ///      鐨?`TouchMode.Radial` 鍒嗘敮鍦ㄥ杺锛夛紝鏈€鍚庝竴鏍规姮璧锋椂纭锛圱ouchUpDispatch锛夛紱
    ///   鈶?**娌″垝鍔ㄥ氨鏉炬墜 = 鐣欏湪鐩樹笂绛夌偣閫?*锛坄_radialSticky`锛?s 瓒呮椂锛涚偣鎵囧尯/鐐圭洏澶栬
    ///      <see cref="RadialPaletteTryPointerDown"/>锛夆€斺€旈敭鐩橀偅濂楃殑"鏉惧湪涓績=鍙栨秷"涓嶅彉銆?    /// </summary>
    private void OpenRadialPaletteFromTouch(float x, float y, bool sticky)
    {
        if (RadialPaletteActive) return;
        if (CaptureActive) return;

        RadialPaletteActive = true;
        RadialPaletteVisible = false;
        RadialPaletteSector = -1;
        _radialMoved = false;
        _radialTouchMode = true;
        _radialSticky = sticky;
        _radialOpenedAtMs = NowMs;
        RadialCenterX = x;
        RadialCenterY = y;
        PointerX = x;
        PointerY = y;
        _dirty = true;
        Console.WriteLine(sticky
            ? "鍛煎嚭鐩橈紙瑙﹀睆路杞荤偣锛夛細鐐规墖鍖虹‘璁わ紝鐐圭洏澶?涓績鍙栨秷锛?s 瓒呮椂锛?
            : "鍛煎嚭鐩橈紙瑙﹀睆路闀挎寜锛夛細鍒掑悜鎵囧尯鏉炬墜纭锛涙病鍒掑姩=鐣欏湪鐩樹笂绛夌偣閫?);
    }

    /// <summary>
    /// 鍛煎嚭鐩樺紑鐫€鏃剁殑涓€娆¤惤鐐癸細**鍛戒腑鐩橀潰 = 鐐归€夋墖鍖哄苟纭**锛堣Е灞?杞荤偣鍛煎嚭"闈犲畠锛?    /// 榧犳爣/绗斿悓鏍峰彲鐢級锛涚偣鍦ㄧ洏澶?= 鍙栨秷骞惰繑鍥?false锛堣繖涓€涓嬬户缁蛋鍘熻矾锛岃鍐欏瓧鍐欏瓧锛夈€?    /// </summary>
    private bool RadialPaletteTryPointerDown(float x, float y)
    {
        if (!RadialPaletteActive) return false;
        if (!RadialPaletteVisible) { CancelRadialPalette("鏈嚭鐩樺氨钀界瑪"); return false; }
        float dx = x - RadialCenterX, dy = y - RadialCenterY;
        float r = RadialRadiusLogical * DpiScale;
        if (dx * dx + dy * dy > r * r) { CancelRadialPalette("鐐圭洏澶?); return false; }

        // 鐐归€夋槸涓€娆?鏈夋剰鐨勬柟鍚戣緭鍏?锛氭妸 `_radialMoved` 缃笂鈥斺€旂偣涓績鎸変笅鍘?= 鍙栨秷
        //锛堣€屼笉鏄 Commit 閲岀殑"娌″垝鍔ㄥ氨鐣欑洏"鍒嗘敮褰撴垚鍙堜竴娆￠潤缃級銆?        _radialMoved = true;
        PointerX = x;
        PointerY = y;
        UpdateRadialSelection();
        CommitRadialPalette();
        return true;
    }

    /// <summary>
    /// 绌块€忔椂鎶婃寚閽堜綅缃悓姝ユ垚**绯荤粺鍏夋爣鐨勫綋鍓嶄綅缃?*銆?    ///
    /// 涓轰粈涔堝繀椤绘湁瀹冿細绌块€忕粰瑕嗙洊灞傚姞浜?`WS_EX_TRANSPARENT`锛岀郴缁熼偅涓€灞傚氨瀹屽叏涓?    /// 缁欏畠鎶曢€掗紶鏍囨秷鎭紙杩?WM_NCHITTEST 閮戒笉闂級锛宍PointerX/Y` 浼氫竴鐩村仠鍦ㄦ渶鍚?    /// 涓€娆℃甯告ā寮忎笅鐨勪綅缃€傚懠鍑虹洏闈?鐩樺績 鈫?鎸囬拡鐨勪綅绉?閫夋墖鍖猴紝浣嶇疆鏄棫鐨勫氨绛変簬
    /// 鐩樺績涔辫烦銆佹柟鍚戝け鐏点€俙GetCursorPos` 涓庣劍鐐规棤鍏筹紝绌块€忔湡闂寸収甯哥粰鐪熶綅缃?    ///锛堝拰 `BeginCaptureMode` 閲岄偅鍙?`GetCursorPos` 鍚屼竴涓悊鐢变笌鍐欐硶锛夈€?    /// </summary>
    private void SyncPointerFromCursor()
    {
        if (!PassThrough) return;             // 姝ｅ父妯″紡鏈夌湡榧犳爣娑堟伅锛屽埆鍘绘姠
        if (!Native.GetCursorPos(out var p)) return;
        PointerX = p.X;
        PointerY = p.Y - ViewOffsetY;         // 灞忓箷 鈫?鐢诲竷锛堝拰 BeginCaptureMode 鍚屼竴鍙ワ級
        PointerInside = true;
    }

    /// <summary>鏉炬墜 = 纭銆傛病浣嶇Щ/姝诲尯 = 鍙栨秷锛堣Е灞忔病鍒掑姩鍒欑暀鐩樼瓑鐐归€夛級锛涙湁鎵囧尯灏辨墽琛岄偅鏉″懡浠ゃ€?/summary>
    private void CommitRadialPalette()
    {
        if (!RadialPaletteActive) return;
        UpdateRadialSelection();               // 浠ユ澗鎵嬭繖涓€鍒荤殑鎸囬拡涓哄噯锛堝揩鍒掍笉涓級
        int sec = RadialPaletteSector;

        // 瑙﹀睆鐗堬細**娌″垝鍔ㄥ氨鏉炬墜 = 鐣欏湪鐩樹笂绛夌偣閫?*锛圔lender marking menu 鐨勯偅鍗婂锛?        // 5s 瓒呮椂锛涚偣鎵囧尯/鐐逛腑蹇冦€佺偣鐩樺閮借蛋 RadialPaletteTryPointerDown锛夈€?        if (sec < 0 && _radialTouchMode && !_radialMoved)
        {
            _radialSticky = true;
            Console.WriteLine("鍛煎嚭鐩橈細娌″垝鍔?鈫?鐣欏湪鐩樹笂绛夌偣閫夛紙5s锛?);
            return;
        }

        RadialPaletteActive = false;
        RadialPaletteVisible = false;
        RadialPaletteSector = -1;
        _radialTouchMode = false;
        _radialSticky = false;
        _dirty = true;

        if (sec < 0)
        {
            Console.WriteLine("鍛煎嚭鐩?鈫?鍙栨秷");
        }
        else
        {
            // **绌块€忎腑閫夋墖鍖?= "鎴戠幇鍦ㄥ氨瑕佸啓"锛氶『鎵嬮€€鍑虹┛閫?*锛?026-10-04 鐢ㄦ埛瀹氾級銆?            // 鍜岀偣闈㈡澘宸ュ叿鏍煎悓涓€鏉¤矾锛坄SwitchTool` 閲岄偅鍙?`if (PassThrough) SetPassThrough(false)`锛夈€?            // 鈿?椤哄簭涓嶈兘鍙嶏細宸ュ叿閿湪绌块€忎笅"涓嶅搷搴?锛?026-09-30 瀹氱殑 8.5 閭ｆ潯锛夛紝
            // 涓嶅厛閫€绌块€忕殑璇?`ToolKeyPress` 浼氱洿鎺?return锛屾寜涓嬪幓**鎮勬棤澹版伅**鈥斺€?            // 姝ｆ槸鐢ㄦ埛鎶ョ殑閭ｄ釜鐜拌薄銆傝繖閲屽厛閫€锛堟澘鎬佷笉鎭㈠锛氭崲浜嗗伐鍏风殑鎰忔€濇槸"鎴戣鍦?            // 鐪煎墠杩欑墖涓滆タ涓婂啓"锛屽拰 SwitchTool 鐨勯€夋嫨涓€鑷达紝瑙?SetPassThrough 鐨勮鏄庯級銆?            if (PassThrough) SetPassThrough(false, restoreBoard: false);

            Console.WriteLine($"鍛煎嚭鐩?鈫?{RadialSectorNames[sec]}");
            switch (sec)
            {
                case 0: ToolKeyPress(KeyAction.ToolPen); break;
                case 1: PickPenColorFromPalette(0); break;
                case 2: PickPenColorFromPalette(1); break;
                case 3: PickPenColorFromPalette(2); break;
                // V-a锛氬崡=姗＄毊銆佽タ鍗?妗嗛€夈€佽タ=鑽у厜绗斻€佽タ鍖?婵€鍏夛紙2026-09-30 瀹氾級
                case 4: ToolKeyPress(KeyAction.ToolEraser); break;
                case 5: ToolKeyPress(KeyAction.ToolMarquee); break;
                case 6: ToolKeyPress(KeyAction.ToolHighlighter); break;
                case 7: ToolKeyPress(KeyAction.ToolLaser); break;
            }
        }
        ApplyCursor();
        _dirty = true;
    }

    /// <summary>棰滆壊鎵囧尯 = "缁欐垜杩欐敮棰滆壊鐨勭瑪"锛堜笉鍦ㄧ瑪涓婂氨鍒囧埌绗旓紱绌块€忎簰鏂ョ瓑鐓у父鎬侊級銆?/summary>
    private void PickPenColorFromPalette(int bandIndex)
    {
        bandIndex = Math.Clamp(bandIndex, 0, InkPalette.PenBand.Length - 1);
        if (Tool != Tool.Pen) SwitchTool(Tool.Pen);
        SetColorFromUi(InkPalette.PenBand[bandIndex].Color);
    }

    private void CancelRadialPalette(string why)
    {
        if (!RadialPaletteActive) return;
        RadialPaletteActive = false;
        RadialPaletteVisible = false;
        RadialPaletteSector = -1;
        _radialTouchMode = false;
        _radialSticky = false;
        _dirty = true;
        Console.WriteLine($"鍛煎嚭鐩?鈫?鍙栨秷锛坽why}锛?);
    }

    /// <summary>
    /// 姣忓抚涓€娆★細鍑虹洏寤惰繜銆佹柟鍚戦噸绠椼€佹澗鎵嬭疆璇€佽秴鏃躲€?    ///
    /// **涓轰粈涔堣鏈夎疆璇?*锛氬叏灞€鐑敭锛堝父椹荤殑 `Ctrl+Alt+Shift+Q` 鍜屾斁鏄犱复鏃堕偅鎵癸級鍙粰
    /// WM_HOTKEY锛堟寜涓嬶級锛屾病鏈夋澗鎵嬫秷鎭紱`GetAsyncKeyState` 鐪嬬殑鏄墿鐞嗛敭鐘舵€侊紝
    /// 60fps 涓嬭宸?鈮?6ms銆?    /// </summary>
    private void PumpRadialPalette()
    {
        if (!RadialPaletteActive) return;

        // 绌块€忛噷娌℃湁榧犳爣娑堟伅锛屾墖鍖烘柟鍚戝彧鑳芥瘡甯ч棶绯荤粺瑕佷竴娆★紙瑙?SyncPointerFromCursor锛夈€?        SyncPointerFromCursor();

        if (RadialTestHold)
        {
            // 鑷/鎽嗘牱锛氫笉杞銆佷笉瓒呮椂锛屽彧鎶婄洏鎸夊嚭鏉ワ紙鍑哄浘涓庣姸鎬佹柇瑷€鐢級銆?            if (!RadialPaletteVisible) { RadialPaletteVisible = true; _dirty = true; }
            UpdateRadialSelection();
            return;
        }

        if (!RadialPaletteVisible && NowMs - _radialOpenedAtMs >= RadialShowDelayMs)
        {
            RadialPaletteVisible = true;
            _dirty = true;
        }

        UpdateRadialSelection();

        // 瑙﹀睆鐗堬紙闀挎寜鍛煎嚭锛夛細鏉炬墜涓嶆槸鎸夐敭锛岃€屾槸瑙︾偣锛圱ouchUpDispatch 閲屾渶鍚庝竴鏍规姮璧锋椂纭锛夈€?        if (!_radialTouchMode && (Native.GetAsyncKeyState((int)_radialVk) & 0x8000) == 0)
        {
            CommitRadialPalette();
            return;
        }
        if (NowMs - _radialOpenedAtMs > RadialTimeoutMs)
            CancelRadialPalette("鎸夊お涔?);
    }

    /// <summary>鎸夊綋鍓嶆寚閽堜綅缃噸绠楁墖鍖猴紙姝诲尯 / 閿佸畾璺濈 / 璺ㄦ墖鍖烘粸鍥為兘鍦ㄨ繖閲岋級銆?/summary>
    private void UpdateRadialSelection()
    {
        float dpi = DpiScale;
        float dx = PointerX - RadialCenterX, dy = PointerY - RadialCenterY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float dead = RadialDeadZoneLogical * dpi;
        float lockR = RadialLockLogical * dpi;

        int sec;
        if (dist < dead)
        {
            sec = -1;                                   // 姝诲尯锛氭澗鎵?= 鍙栨秷
        }
        else
        {
            float deg = MathF.Atan2(dy, dx) * (180f / MathF.PI);
            if (RadialPaletteSector < 0 && dist < lockR)
            {
                sec = -1;                               // 杩樹笉澶熻繙锛氬厛鍒攣鏂瑰悜
            }
            else if (RadialPaletteSector >= 0)
            {
                // 婊炲洖锛氬凡缁忛€変腑涓€涓墖鍖烘椂锛屽嚭鐣?9掳 浠ュ唴浠嶇畻瀹冿紙杈圭晫鎶栧姩涓嶈烦鎵囧尯锛夈€?                float delta = Normalize180(deg - (-90f + 45f * RadialPaletteSector));
                sec = MathF.Abs(delta) <= 22.5f + 9f ? RadialPaletteSector : SectorIndexFromDeg(deg);
            }
            else sec = SectorIndexFromDeg(deg);
        }

        if (dist >= lockR) _radialMoved = true;
        if (sec != RadialPaletteSector)
        {
            RadialPaletteSector = sec;
            _dirty = true;
        }
    }

    /// <summary>鎸囬拡瑙掑害 鈫?鎵囧尯鍙凤紙0 = 鍖楋紝椤烘椂閽堬級銆傚寳鍦ㄤ笂锛?90掳 璧枫€佹瘡 45掳 涓€涓€?/summary>
    internal static int SectorIndexFromDeg(float deg)
    {
        int i = (int)MathF.Round(deg / 45f);
        return ((i + 2) % 8 + 8) % 8;
    }

    private static float Normalize180(float deg)
    {
        while (deg <= -180f) deg += 360f;
        while (deg > 180f) deg -= 360f;
        return deg;
    }

    // ---- 鍛煎嚭鐩樿嚜妫€閽╁瓙锛?-radialtest / --radialshow锛?---

    /// <summary>鑷/鎽嗘牱锛氳烦杩囨澗鎵嬭疆璇笌瓒呮椂锛堢姸鎬佹柇瑷€涓?瀹氭牸鍑哄浘"鐢級銆?/summary>
    internal bool RadialTestHold;

    internal void RadialOpenForTest(float canvasX, float canvasY)
    {
        PointerX = canvasX; PointerY = canvasY;
        OpenRadialPalette();
    }

    internal void RadialMoveForTest(float canvasX, float canvasY)
    {
        PointerX = canvasX; PointerY = canvasY;
        UpdateRadialSelection();
    }

    internal void RadialPumpForTest() => PumpRadialPalette();
    internal void RadialCommitForTest() => CommitRadialPalette();
    internal void RadialCancelForTest(string why) => CancelRadialPalette(why);

    /// <summary>鑷锛氭斁鏄犱复鏃跺叏灞€閿〃閲屾湁娌℃湁鏌愪釜鍔ㄤ綔锛堜互鍙婂畠鐨勯敭锛夈€?/summary>
    internal static (uint Mod, uint Vk)? PptHotkeyEntryForTest(KeyAction a)
    {
        foreach (var (mod, vk, act) in PptHotkeys)
            if (act == a) return (mod, vk);
        return null;
    }

    private bool HandleKeyDown(IntPtr wParam, bool isRepeat)
    {
        // 閿綅琛ㄩ┍鍔細鎸?褰撳墠淇グ閿姸鎬?+ 涓婚敭"鎷兼垚涓€涓拰寮︼紝鍘绘壒娉ㄥ唴浣滅敤鍩熼噷鏌ャ€?        // 鏌ヤ笉鍒板氨**涓嶅悶杩欎釜閿?*锛堣繑鍥?false锛夛紝浜ょ粰绯荤粺/涓嬪眰绋嬪簭鈥斺€斿悶鎺夋墍鏈夋寜閿?        // 浼氳鎵规敞閿洏妯″紡涓嬭繛杈撳叆娉曢兘鐢ㄤ笉浜嗐€?        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;

        var chord = new KeyChord(mods, (uint)wParam.ToInt32());

        // 鍛煎嚭鐩樺紑鐫€鏃讹紝Esc = 鍙栨秷銆?        // 鈿?杩欐潯鍙湪"淇グ閿凡缁忓厛鏉惧紑"涔嬪悗鎵嶇湡鑳芥敹鍒扳€斺€?*Ctrl+Esc 鏄郴缁熶繚鐣欑殑
        //    "鎵撳紑寮€濮嬭彍鍗?**锛圓lt+Esc 杩樹細鍒囩獥鍙ｏ級锛學indows 涓嶄細鎶婂畠閫佽繘绐楀彛
        //    锛堢湡鏈鸿嚜妫€閲岄噺鍒拌繃锛氭寜浣?Ctrl+Q 鏃舵寜 Esc锛屾垜浠竴鏉℃秷鎭兘鏀朵笉鍒帮紱鐜板湪
        //    鍛煎嚭閿浜?Alt/Shift锛屼竴鏍疯鍏ㄩ儴鏉惧紑涔嬪悗 Esc 鎵嶉€佸緱鍒帮級銆?        //    鎵€浠ユ彁浜ゅ彧璁?Q 鏉炬墜锛堣 HandleKeyUp锛夛紝鍏堟澗 Ctrl/Alt/Shift 鐩樿繕鐣欑潃锛?        //    杩欐椂 Esc 鎵嶆湁鏁堬紱涓诲彇娑堣矾寰勬槸"鏉惧湪姝诲尯 / 鍒掑洖涓績 / 钀界瑪"銆?        if (RadialPaletteActive && wParam.ToInt32() == 0x1B /*VK_ESCAPE*/)
        {
            CancelRadialPalette("Esc");
            _dirty = true;
            return true;
        }

        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null) return false;

        // 鍛煎嚭鐩樺拰宸ュ叿閿竴鏍?鍙湅绗竴娆℃寜涓?锛堟寜浣忎笉鏀剧殑鑷姩閲嶅涓嶅啀閲嶅紑锛夈€?        if (hit.Action == KeyAction.RadialPalette)
        {
            if (!isRepeat) RunAction(hit.Action);
            _dirty = true;
            return true;
        }

        if (IsToolKey(hit.Action))
        {
            // **鑷姩閲嶅鐨勬寜涓嬪悶鎺?*锛氭寜浣忎笉鏀炬椂 Windows 浼氳繛鍙?KEYDOWN锛岃€?宸茬粡鏄畠 鈫?            // 鎹㈣壊"杩欐潯浼氳杩炲彂甯︾潃涓€璺媯杞紙鎵€浠ュ伐鍏烽敭鍙湅绗竴娆℃寜涓嬶紝鏉炬墜鎵嶇畻涓€娆★級銆?            if (!isRepeat) ToolKeyPress(hit.Action);
            _dirty = true;
            return true;
        }
        RunAction(hit.Action);
        _dirty = true;
        return true;
    }

    /// <summary>鏉鹃敭锛氬彧鏈嶅姟宸ュ叿閿殑鎵嬪娍锛堥暱鎸?鍙屽嚮鍒ゅ畾锛夛紝鍏朵綑閿笉鐪嬫澗閿€?/summary>
    private bool HandleKeyUp(IntPtr wParam)
    {
        // 鍛煎嚭鐩橈細**Q 鏉炬墜 = 纭锛涘彧璁や富閿紝涓嶈淇グ閿?*銆?        // 涓轰粈涔堬細鈶?鐢ㄦ埛缁忓父鍏堟澗 Ctrl/Alt/Shift 鍐嶆澗 Q锛屾寜鍜屽鸡鏌ヤ細婕忥紱
        // 鈶?鍏堟澗淇グ閿箣鍚庣洏杩樼暀鐫€锛岃繖鏃舵寜 Esc 鎵嶆槸"鐪熻兘閫佸埌鎴戜滑鎵嬮噷"鐨勫彇娑?        //   锛圕trl+Esc 琚郴缁熺殑寮€濮嬭彍鍗曞崰浜嗭紝瑙?HandleKeyDown 閭ｆ锛夈€?        if (RadialPaletteActive && wParam.ToInt32() == _radialVk)
        {
            CommitRadialPalette();
            _dirty = true;
            return true;
        }

        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;
        var chord = new KeyChord(mods, (uint)wParam.ToInt32());
        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null || !IsToolKey(hit.Action)) return false;
        ToolKeyUp(hit.Action, NowMs);
        return true;
    }

    /// <summary>
    /// 鍏ㄩ€夈€傞『渚垮垏鍒伴€夋嫨宸ュ叿鈥斺€斾笉鍒囩殑璇濇墜鏌勫拰鎿嶄綔鏉′笉浼氬嚭鐜帮紝
    /// 鐢ㄦ埛浼氫互涓?鍏ㄩ€夋病鐢熸晥"銆?    /// </summary>
    internal void SelectAll()
    {
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 婊氳疆婊氬姩鐢诲竷銆?*鍙敼鐩告満鍋忕Щ ViewOffsetY锛屼竴涓璞＄殑鏁版嵁閮戒笉鍔?*鈥斺€?    /// 杩欐槸婊氬姩鑳藉仛鍒?O(1) 鐨勫墠鎻愩€傜粷涓嶈兘鍍?InkClass 閭ｆ牱鎶婂钩绉荤儤鐒欒繘鐐瑰潗鏍?    /// 锛堥偅鏄?O(瀵硅薄鏁?/娆★紝涓€涓囩瑪婊氫竴鏍艰閲嶅啓鍗佷竾涓偣锛夈€?    ///
    /// 鍙仛绾靛悜銆傚線涓嬫粴 = 鍐呭涓婄Щ = 鍋忕Щ鍙樿礋銆?    ///
    /// 浠ｄ环锛?*杩欓噷涓€涓儚绱犻兘涓嶉噸鐢?*銆傚唴瀹瑰眰鏄敾甯冪┖闂寸殑鍒嗗潡缂撳瓨
    /// 锛圕anvasTiles.cs锛夛紝婊氬姩鍙槸鎹釜浣嶇疆鎶婂凡缁忕敾濂界殑鍧楄创涓婂幓锛涘彧鏈夊湪
    /// 鏂伴湶鍑?*娌＄敾杩?*鐨勫潡鏃舵墠浼氬厜鏍呭寲閭ｄ竴灏忓潡銆傛墍浠ユ粴涓€鏍肩殑浠ｄ环涓庢枃妗?    /// 閲屾湁澶氬皯绗旀棤鍏斥€斺€斾粠"鏁村眰閲嶇敾"锛堜竴涓囩瑪 82~154ms锛屾粴涓€鏍煎崱涓€涓嬶級
    /// 鍙樻垚"璐村浘 + 鍋跺皵琛ヤ竴涓ゅ潡"銆傝繖涔熸鏄?Win32 ScrollWindowEx 鍜屾祻瑙堝櫒
    /// 鍚堟垚鍣ㄦ粴鍔ㄥ浘灞傜敤鐨勬墜娉曘€?    /// </summary>
    /// <summary>
    /// 涓€"鏍?鐨勬粦鍔ㄨ窛绂?= **灞忓箷楂樺害鐨?1/10**锛堢敤鎴?2026-09-30 瀹氾紝鐓?ClassIn 鐨勬墜鎰燂級銆?    /// 涓婁笅鏂瑰悜閿€佷互鍙婃斁鏄犳湡鐨勫悓鍚嶉敭閮界敤瀹冦€?    /// </summary>
    private float ScrollStepLogical => _windows.Count > 0 ? _windows[0].Height / DpiScale / 10f : 60f;

    /// <summary>
    /// 鈫戔啌锛堜换浣曟ā寮忥級涓婁笅鎸閲庘€斺€斿拰婊氳疆**鍚屼竴鏉?鐩告満鍋忕Щ"**锛堝彧鍔ㄨ閲庯紝涓嶅姩澧ㄨ抗鏁版嵁锛夈€?    /// 绗﹀彿鐓ф粴杞偅鏉★細寰€"涓?鐪?= <see cref="ViewOffsetY"/> 鍙樺皬锛屾墍浠?<paramref name="logicalDelta"/>
    /// 涓烘 = 鐪嬩笂闈㈢殑鍐呭銆?    /// </summary>
    private void ScrollCanvasBy(float logicalDelta)
    {
        if (_windows.Count == 0) return;
        ViewOffsetY += logicalDelta * DpiScale;
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    internal IntPtr HandleWheel(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        // 寰€涓嬫粴鏃?delta = -120锛岃鍏堝彉璐燂紙鍐呭涓婄Щ锛夆€斺€旀墍浠ヨ繖閲屾槸 +=銆?        // 鍐欐垚 -= 浼氭妸绗﹀彿缈昏繃鏉ワ細婊氳疆寰€涓?= 瑙嗗浘寰€涓婏紝姝ｅソ鎶垫帀锛?        // 鍐嶈"涓嶈婊氳繃椤堕儴"澶瑰洖 0锛岃〃鐜板氨鏄?*瀹屽叏婊氫笉鍔?*銆?        ViewOffsetY += delta / 120f * 72f * DpiScale;   // 涓€鏍?= 72 閫昏緫鍍忕礌
        // 涓婁笅閮藉す浣忕敾甯冭寖鍥达紙鍐呭杈圭晫 鈭?涓€灞忥級銆?        // 涓嶅す鐨勮瘽浼氭粴杩涙棤灏界殑绌虹櫧锛岃€屼笖姣斾緥婊氬姩鏉℃嬁涓嶅埌鏈夋剰涔夌殑鑼冨洿銆?        // 涓嬭竟鐣屾槸"瑙嗗彛搴曡竟璐翠綇鍐呭搴曡竟"鈥斺€旀帴鐫€鍐欙紝鍐呭闀垮嚭鍘伙紝鑼冨洿鑷繁闀垮嚭鏉ワ紝
        // 鎵€浠ヤ笉浼氭妸浜哄崱鍦ㄥ簳杈广€?        ClampViewOffset();

        ScrollBarActiveAtMs = NowMs;                     // 婊氬姩鏃惰婊氬姩鏉￠湶闈?        // 娉ㄦ剰锛?*涓嶈皟 Doc.InvalidateAll()**銆傞偅浼氭妸鏁翠釜鏂囨。鏍囪剰銆佽鎵€鏈夊垎鍧?        // 閲嶇敾鈥斺€旀鏄繖娆¤鎷旀帀鐨勭梾鏍广€傚垎鍧楃紦瀛樿嚜宸变細鍙戠幇鐩告満鍙樹簡锛屽彧鍋氶噸鍚堟垚銆?        _dirty = true;
        return IntPtr.Zero;
    }}
