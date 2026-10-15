using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// 寮€鍙戞湡瀹夸富锛氭寔鏈夊紩鎿庯紝澶栧姞涓€鏁村鑷姩鍖栨祴璇?鍩哄噯宸ュ叿銆?/// 缁ф壙鍙槸涓轰簡璁╄繖浜涘伐鍏风洿鎺ヨ寮曟搸鍐呴儴鐘舵€侊紙浜у搧浠ｇ爜璇风敤缁勫悎锛歯ew InkEngine()锛夈€?/// </summary>
internal sealed partial class App : InkEngine.InkEngine
{
    private IntPtr _clickTargetHwnd;
    private string _clickLogFile;
    private bool _panelShowMore;
    private bool _panelShowMini;
    private bool _panelShowBand;
    private bool _selfCheckMode;

    /// <summary>
    /// `--doc <鏂囦欢>`锛氬惎鍔ㄥ悗鎵撳紑杩欎唤鏂囨。锛堝浘鐗?/ PDF锛夈€俷ull = 娌＄粰銆?    /// 瑙ｆ瀽鍦?<see cref="PrepareHostStartup"/>锛涚湡姝ｇ殑"鎵撳紑"鍦ㄧ獥鍙ｅ缓濂戒箣鍚庣殑姝ｅ父鍚姩鍒嗘敮閲屽仛
    /// 锛圫4/S6 鎺ョ嚎鈥斺€旂幇鍦ㄥ厛鍙浣忓苟鏍￠獙锛夈€?    /// </summary>
    private string _startupDocPath;

    /// <summary>瀵嗛泦妯″紡锛氭墍鏈夌瑪鐢诲啓鍦ㄤ竴灏忓潡鍖哄煙閲岋紙閲?鍚屼竴椤靛緢澶氬ⅷ杩?锛夈€?/summary>
    private bool DenseWrite;

    // =====================================================================
    //  Host hooks: everything here is a development tool, not engine code.
    //  寮曟搸鐨?Run() 涓嶅啀璁よ瘑娴嬭瘯妯″紡锛涙ā寮忓垎鍙戝叏閮ㄨ惤鍦ㄨ繖涓€灞傘€?    // =====================================================================

    /// <summary>
    /// 寤虹獥鍙ｄ箣鍓嶅氨瑕佸鐞嗘帀鐨勪袱浠朵簨锛氭埅鍥惧伐鍏枫€佷綔涓?涓嬪眰绐楀彛"鐨勭偣鍑荤洰鏍囥€?    /// 浜岃€呴兘涓嶉渶瑕佽鐩栧眰锛屾墍浠ュ繀椤诲湪 CreateOverlays 涔嬪墠鎷︿笅鏉ャ€?    /// </summary>
    protected override bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;

        // 鍙傛暟瀵圭収鐢細--tilesize 256|512 鐩存帴鎹㈠唴瀹瑰眰鐨勫垎鍧楄竟闀匡紙瑙?CanvasTiles.cs锛夈€?        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--tilesize" && int.TryParse(args[i + 1], out var ts) && ts >= 64)
                CanvasTileCache.TileSize = ts;

        // **璇嗗埆璇婃柇鏃ュ織**锛堣 `Engine.InkLogEnabled`锛夛細`--reclog` 鎴栫幆澧冨彉閲?        // `INKTEACH_RECLOG=1`銆傛斁鍦?`PrepareHostStartup` 閲岋紝鎵€鏈夋ā寮忥紙鍚悇绉嶈嚜妫€锛夐兘鍚冨緱鍒般€?        if (args.Contains("--reclog") || Environment.GetEnvironmentVariable("INKTEACH_RECLOG") == "1")
            InkLogEnabled = true;

        // **褰曞ⅷ杩?*锛坄--recink <鏂囦欢>`锛夛細瑙?`InkRecordPath`銆傜敤鎴?2026-09-26 鎻愮殑鍋氭硶 鈥斺€?        // "鎴戞墜鐢诲灏戞潯鍙屾洸绾跨粰浣狅紝浣犳牴鎹繖浜涙潵瀹氬埗鍒ゆ嵁"锛?*鐪熸墜鐢荤殑鏍锋湰**姣斾换浣曞悎鎴愯鏂欓兘鍊奸挶銆?        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--recink")
            {
                InkRecordPath = System.IO.Path.GetFullPath(args[i + 1]);
                Console.WriteLine($"[褰曞ⅷ杩筣 姣忎竴绗旈兘浼氳拷鍔犲啓杩涳細{InkRecordPath}");
                if (args.Contains("--recinkp"))
                {
                    InkRecordWithPressure = true;
                    Console.WriteLine("           锛?-recinkp锛氬褰?鍘嬪姏/鏉ユ簮/褰撴椂骞虫澘鍧愭爣锛?);
                }
            }

        // **杈撳叆榛戝專瀛?*锛坄--reclive <鏂囦欢>`锛夛細閫愭潯璁板綍鍠傝繘寮曟搸鐨勮緭鍏ワ紙璧风瑪/閲囨牱/鏀剁瑪 + 寮曟搸鏃舵爣锛夈€?        // 閰嶅悎 `--replayinput` 绂荤嚎 1:1 澶嶇幇鈥斺€?鍋跺皵璺充竴涓?杩欑被璇翠笉娓呯殑闂闈犲畠鐣欒瘉鎹€?        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--reclive")
            {
                var bb = System.IO.Path.GetFullPath(args[i + 1]);
                // 鈿?琛ㄥご鍦?*鍙傛暟瑙ｆ瀽鍓?*鐢熸垚锛屽埆璇诲紩鎿庣殑闈欐€佸€硷紙閭ｆ椂杩樻槸榛樿锛夆€斺€旀寜 args 鑷繁绠楋細
                bool pred = !args.Contains("--nopredict2");
                InputBlackBox.Open(bb,
                    $"version {UpdateFeed.CurrentVersion} pred {(pred ? 1 : 0)}");
                Console.WriteLine($"[榛戝專瀛怾 杈撳叆鏃ュ織锛歿bb}");
            }

        // **鎵撳紑鏂囨。**锛坄--doc <鏂囦欢>`锛夛細鍥剧墖 / PDF銆傚疄娴嬩笌寮€鍙戞湡鐩磋揪鐢ㄣ€?        // 鍙槸"璁颁綇璺緞"锛氱湡姝ｆ墦寮€鍦ㄧ獥鍙ｅ缓濂戒箣鍚庯紙鏂囨。妯″紡瑕佹湁瑕嗙洊灞傛墠鑳界敾椤碉級銆?        // 鏂囦欢涓嶅瓨鍦?鈫?鍙彁绀恒€佸綋娌＄粰锛堢粷涓嶅洜姝ゅ奖鍝嶅惎鍔ㄢ€斺€旇鐭╀笁锛氬け璐ュ綋娌℃湁锛夈€?        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--doc")
            {
                var p = System.IO.Path.GetFullPath(args[i + 1]);
                if (System.IO.File.Exists(p))
                {
                    _startupDocPath = p;
                    Console.WriteLine($"[鏂囨。] 鍚姩鍚庢墦寮€锛歿p}");
                }
                else
                {
                    Console.WriteLine($"[鏂囨。] 鎵句笉鍒版枃浠讹紝宸插拷鐣ワ細{p}");
                }
            }

        // A separate process that just sits there waiting to be clicked. The
        // pass-through test uses it as the window *underneath* the overlay, so
        // the test observes real cross-process mouse routing.
        if (mode == "--clicktarget")
        {
            RunClickTarget(args.Length > 1 ? args[1] : "clicktarget.txt");
            return true;
        }

        // Capture-only mode: grab the desktop without creating any overlay.
        if (mode == "--screenshot")
        {
            string path = args.Length > 1 ? args[1] : "screen.bmp";
            int sx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int sy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

            // --screenshot <path> <x> <y> <w> <h>锛氬彧鎴竴灏忓潡锛?:1 鍍忕礌锛夛紝
            // 鐢ㄦ潵绮剧‘瀹氫綅鍒殑绋嬪簭鐣岄潰涓婄殑鎸夐挳浣嶇疆銆?            if (args.Length >= 6 &&
                int.TryParse(args[2], out int rx) && int.TryParse(args[3], out int ry) &&
                int.TryParse(args[4], out int rw) && int.TryParse(args[5], out int rh))
            {
                sx = rx; sy = ry; sw = rw; sh = rh;
            }

            bool okShot = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
            Console.WriteLine(okShot ? $"宸蹭繚瀛?{path} ({sw}x{sh})" : "鎴浘澶辫触");
            exitCode = okShot ? 0 : 1;
            return true;
        }

        return false;
    }

    /// <summary>娴嬭瘯妯″紡鍒嗗彂銆傝繑鍥?-1 = 姝ｅ父璺戞秷鎭惊鐜€?/summary>
    protected override int RunModeDispatch(string mode, string[] args)
    {
        _selfCheckMode = mode.Length > 0;
        // 鑷涓€寰嬬敤**涓存椂閰嶇疆鏂囦欢**锛氬垽鎹纭畾锛屾洿涓嶈兘鎶婄敤鎴风湡姝ｇ殑璁剧疆鏀规帀銆?        if (_selfCheckMode && InkSettings.PathOverride == null)
            InkSettings.PathOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck.json");
        // 鑷姩瀛樻。鍚岀悊锛氳嚜妫€**缁濅笉鑳界鐢ㄦ埛鐪熸鐨勬澘涔?*
        if (_selfCheckMode && Recovery.AutoSavePathOverride == null)
            Recovery.AutoSavePathOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-autosave.ink");
        // PPT 鎵规敞缂撳瓨鍚岀悊锛氬亣婧愮敤鐨勯敭鏄?鍋囨紨绀烘枃绋?锛屼箣鍓嶆病闅旂鏃跺巻娆¤嚜妫€浼氭妸瀹?        // **姘镐箙鍐欒繘鐢ㄦ埛鐪熺洰褰?*锛堣繕浼氳法娆＄疮绉€佹妸璁℃暟绫绘柇瑷€鎾戝潖鈥斺€?026-10-01 鎶撳埌锛夈€?        // 鑷姣忔鍚姩鍏堟竻绌鸿繖浠戒复鏃剁紦瀛橈紱--ppttest 浼氬湪杩愯鏃跺啀鎸囧悜鑷繁鐨勭洰褰曘€?        if (_selfCheckMode && PptStore.RootOverride == null)
        {
            PptStore.RootOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-ppt");
            try { if (Directory.Exists(PptStore.RootOverride)) Directory.Delete(PptStore.RootOverride, true); } catch { }
        }
        // 鏂囨。鎵规敞缂撳瓨鍚岀悊锛氳嚜妫€姣忔鍚姩鍏堟竻绌鸿繖浠戒复鏃剁紦瀛橈紙鍜?PptStore 涓€鏍凤紝鍒鐢ㄦ埛鐪熺洰褰曪級
        if (_selfCheckMode && DocStore.RootOverride == null)
        {
            DocStore.RootOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-doc");
            try { if (Directory.Exists(DocStore.RootOverride)) Directory.Delete(DocStore.RootOverride, true); } catch { }
        }
        // 鏉夸功鍘嗗彶锛? 鍙烽〉蹇収锛夊悓鐞嗭細鑷缁濅笉鍐欑敤鎴风殑鍘嗗彶鐩綍銆?        if (_selfCheckMode && BoardHistory.DirOverride == null)
        {
            BoardHistory.DirOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-history");
            try { if (Directory.Exists(BoardHistory.DirOverride)) Directory.Delete(BoardHistory.DirOverride, true); } catch { }
            BoardHistory.CloseSession();
        }
        // 鑷閲?*涓嶅脊"鍙﹀瓨涓?瀵硅瘽妗?*锛氬畠浼氶樆濉炵瓑娑堟伅锛岃€岃嚜妫€鏄嚜宸辨娊娑堟伅鎺ㄨ繘鐨勶紝
        // 涓€寮瑰氨鍗″埌瓒呮椂锛坄--selftest` 浼氶€愪釜鐐规搷浣滄潯涓婄殑鎸夐挳锛岀偣鍒?瀵煎嚭"灏变腑鎷涳級銆?        // 瀵煎嚭閭ｆ潯閾剧敱 `--iotest` 璧?涓嶅脊妗嗐€佺洿鎺ュ啓鎸囧畾璺緞"楠岋紝瑙?ExportSelectionToPathForTest銆?        if (_selfCheckMode) ExportDialogEnabled = false;

        // 浜や簰妯″紡鎸?*浜у搧鐣岄潰**锛涜嚜妫€/鍩哄噯妯″紡鎸?浠€涔堥兘涓嶇敾"鐨勭┖瀹夸富
        // 锛堣嚜妫€瑕佹暟灞忓箷涓婄殑澧紝涓€鍧楅潰鏉跨洊涓婂幓浼氭妸鍒ゆ嵁鎼炶剰鈥斺€旇繖鏉¤俯杩囷級銆?        //
        // 鐢?SetUiFactory 鑰屼笉鏄?SetUi锛氱晫闈竾涓€宕╀簡锛屽紩鎿庤鑳借嚜宸卞啀閫犱竴涓洖鏉ワ紝
        // 鍐嶉€犱笉鍥炴潵鎵嶈疆鍒伴噸鍚紙瑙?璁″垝-搴曞眰瀵规帴鐣岄潰.md 4.5 鐨勪笁绾ч樁姊級銆?        if (mode.Length == 0) SetUiFactory(() => new InkUi.FullUi());
        else SetUi(new HeadlessUi());

        // --board锛氭妸鐧芥澘鎵撳紑锛?*涓嶉€忔槑**搴曡壊锛夈€?        //
        // 鑷/鎽嗘牱妯″紡涓嬮潰鏉夸竴鐩栥€佹闈竴闇诧紝鎴嚭鏉ョ殑鍥鹃噷鍏ㄦ槸鍒汉鐨勭獥鍙ｏ紝
        // 鐪嬪浘鏃跺弽鑰岃璐瑰姴鎶婃垜浠殑澧ㄤ粠鑳屾櫙閲屾寫鍑烘潵銆傚紑浜嗙櫧鏉夸箣鍚庤鐩栧眰鏄?        // 涓嶉€忔槑鐨勨€斺€旀埅灞忓緱鍒扮殑灏卞彧鏈夋垜浠嚜宸辩殑鐧芥澘 + 绗旇抗 + 钀界偣锛?        // 妗岄潰涓婄殑涓滆タ涓€姒傝繘涓嶆潵锛堢敤鎴锋彁鐨?鎴浘鍙埅杩欎釜杞欢"灏辨槸杩欎釜寮€鍏筹級銆?        // 娉ㄦ剰锛氫骇鍝侀噷鐨?鎴浘"锛圕trl+Alt+S锛?*涓嶈兘**杩欎箞鍋氾紝瀹冭鐨勬鏄笅灞傚唴瀹广€?        if (args.Contains("--board"))
        {
            BoardOn = true;
            Console.WriteLine("鐧芥澘: 宸叉墦寮€锛堟埅鍥惧彧鏈夋垜浠嚜宸辩殑鍐呭锛屼笉鍚闈級");
        }

        if (mode == "--selftest")
        {
            double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 12;
            // 鍩哄噯鐨勪袱涓紑鍏筹紙2026-10-07锛夛細`--benchpressure` 璁╃瑪鐢诲甫鍘嬫劅锛?            // `--benchpts N` 鎸囧畾姣忕瑪鐐规暟鈥斺€旇€佸熀鍑嗘瘡绗斿彧 19.5 鐐癸紝姣旂湡瀹炲啓娉曡交杩?9 鍊嶏紝
            // 涓嶉棶杩欎袱涓棶棰橈紝"涓€涓囩瑪鍚冨灏?绛斾笉鍑嗐€?            _benchPressure = args.Contains("--benchpressure");
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--benchpts" && int.TryParse(args[i + 1], out int bp))
                    _benchPts = Math.Clamp(bp, 2, 4000);
                if (args[i] == "--benchspread" && int.TryParse(args[i + 1], out int bs))
                    _benchSpread = Math.Clamp(bs, 1, 60);
            }
            _autoExitAt = NowMs + seconds * 1000;
            _nextLogAt = NowMs;
            SelfTest(seconds);
        }
        else if (mode == "--report")
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            Report(args.Length > 1 ? args[1] : "reports/inkprobe-report.txt");
        }
        else if (mode == "--memory")
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            MemoryReport(args.Length > 1 ? args[1] : "reports/inkprobe-memory.txt");
        }
        else if (mode == "--inputtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InputPathTest();
        }
        else if (mode == "--passtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PassThroughTest();
        }
        else if (mode == "--uitest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            UiInputTest();
        }
        else if (mode == "--paneltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelTest();
        }
        else if (mode == "--radialtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RadialTest();
        }
        else if (mode == "--radialshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RadialShow(args.Length > 1 ? args[1] : "reports/radial-palette.bmp");
        }
        else if (mode == "--shapebandtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeBandTest();
        }
        else if (mode == "--inktest")
        {
            // **绾嚑浣曪紝涓嶅缓绐楀彛銆佷笉纰扮晫闈?*锛氳瘑鍒櫒鏄绾挎參璺緞锛?            // 瀹冭楠岀殑鏄?鍒ゆ嵁瀵逛笉瀵?锛屼笉璇ユ贩杩涚晫闈?杈撳叆閭ｅ銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = RecoProbe.Run();
            _quit = true;
        }
        else if (mode == "--inkfile")
        {
            // **璇荤敤鎴锋墜鐢荤殑鏍锋湰**锛坄--recink` 褰曞嚭鏉ョ殑閭ｄ唤锛夛細瑙?`RecoProbe.RunInkFile`銆?            // 绾绾匡細涓嶅缓绐楀彛銆佷笉纰扮晫闈€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = RecoProbe.RunInkFile(args.Length > 1 ? args[1] : "reports/鎴戠殑鍙屾洸绾?txt");
            _quit = true;
        }
        else if (mode == "--dwelltest")
        {
            // 鎵嬪娍閭ｄ竴濂?*蹇呴』鐪熻窇**锛堣捣绗?/ 閲囨牱 / 杞 / 鎶墜閮借蛋寮曟搸閲岀湡鍦ㄧ敤鐨勫嚱鏁帮級锛?            // 鎵€浠ュ畠鍜?`--inktest` 涓嶅悓锛氳繖鏉¤鍦ㄦ椿鐫€鐨勫紩鎿庨噷璺戙€傛椂閽熸槸鎺ㄧ殑锛屼笉 sleep銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = DwellProbe.Run(this);
            _quit = true;
        }
        else if (mode == "--librarytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 绗簩涓弬鏁扮粰浜嗚矾寰勫氨椤烘墜鍑轰竴寮犻潰鏉跨殑鍥撅紙鍜?--shapetoolshow 涓€涓敤娉曪級
            LibraryTest(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--dashtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DashTest();
        }
        else if (mode == "--axistest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AxisTest();
        }
        else if (mode == "--curvetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CurveTest();
        }
        else if (mode == "--prismtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrismTest();
        }
        else if (mode == "--prismshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrismShowcase(args.Length > 1 ? args[1] : "reports/妫辨煴.bmp");
        }
        else if (mode == "--axisshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AxisShowcase(args.Length > 1 ? args[1] : "reports/鍧愭爣绯讳笌鏁拌酱.bmp");
        }
        else if (mode == "--curveshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CurveShowcase(args.Length > 1 ? args[1] : "reports/鍥涚鏇茬嚎.bmp");
        }
        else if (mode == "--conicshow")
        {
            // 2026-09-22锛氬弻鏇茬嚎涓ゆ。锛堟湁 / 鏃犳笎杩戠嚎锛夛紜 妞渾涓ゆ。锛堟湁 / 鏃犵劍鐐逛笁瑙掑舰锛?            // 鎽嗘垚涓€寮犲洓鏍肩殑鍥锯€斺€旇繖涓€鎵硅鐪嬬殑姝ｆ槸"涓ゆ。涔嬮棿鐨勫樊鍒?銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ConicShowcase(args.Length > 1 ? args[1] : "reports/鍦嗛敟鏇茬嚎-涓ゆ。.bmp");
        }
        else if (mode == "--panelshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 鍙€夌殑涓や釜鍙傛暟锛堝嚭"鏀逛箣鍓?/ 鏀逛箣鍚?鐨勫鐓у浘涓撶敤锛屼骇鍝侀噷閮戒笉浼氫紶锛夛細
            //   绗?2 涓?锛?鎻忚竟鍨嬪搴撳浘鏍囧己鍒剁嚎瀹斤紙涓嶄紶灏辨寜 IconAtlas.StrokedIcons 閭ｄ竴浠斤級锛?            //   绗?3 涓?锛?婵€鍏夌瑪閭ｄ竴鏍煎己鍒舵垚鍝釜妗ｅ彿锛堜笉浼犲氨鐢ㄤ骇鍝侀偅鏀級銆?            if (args.Length > 2 && float.TryParse(args[2], out var forcedStroke))
                InkUi.IconAtlas.DevForceStroke = forcedStroke;
            if (args.Length > 3 && int.TryParse(args[3], out var laserVariant))
                InkUi.IconAtlas.DevLaserVariant = laserVariant;
            PanelShow(args.Length > 1 ? args[1] : "reports/panel-绗竴鐗?png");
        }
        else if (mode == "--demogif")
        {
            // 婕旂ず杩炴媿锛堝啓瀛?鈫?鍋滈】鍙樺浘褰?鈫?閫変腑鎷栧姩 鈫?鎴浘鍙栨櫙锛夛細甯у瓨鎴?<鐩綍>\NNNN.bmp锛?            // 涔嬪悗鐢?tools\make-demo-gif.ps1 鎷兼垚 README 澶撮儴閭ｅ紶 GIF銆傚彧鍦ㄥ紑鍙戞満涓婅窇銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DemoGif(args.Length > 1 ? args[1] : "tmp/demo");
        }
        else if (mode == "--makeicon")
        {
            // 鐢熸垚绋嬪簭鍥炬爣锛堢敤鐣岄潰鑷繁鐨勬覆鏌撶敾 <see cref="AppIconUi"/> 閭ｅ紶锛氱櫧鐮栵紜澶х瑪锛夛紝瑙?MakeIcon銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            MakeIcon(args.Length > 1 ? args[1] : "assets/InkTeach.ico");
        }
        else if (mode == "--pageshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageShow(args.Length > 1 ? args[1] : "reports/page-lines.bmp");
        }
        else if (mode == "--iconshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            IconShow(args.Length > 1 ? args[1] : "reports/laser-icons.bmp");
        }
        else if (mode == "--shapeiconshow")
        {
            // 鍥惧舰闈㈡澘閭ｄ簺鑷粯鍥炬爣鐨?*瀵圭収琛?*锛堝惈姣忎竴妗ｇ殑鍙樹綋锛夛紝瑙?ShapeIconSheet銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeIconShow(args.Length > 1 ? args[1] : "reports/shape-icons.bmp");
        }
        else if (mode == "--captureicons")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureIconShow(args.Length > 1 ? args[1] : "reports/capture-icons.bmp");
        }
        else if (mode == "--toolicons")
        {
            // 2026-09-26锛氱櫧鏉?/ 婵€鍏夌瑪鐨勫浘鏍囧€欓€夛紙鐢ㄦ埛锛?鐧芥澘锛氭劅瑙変笉鍍?銆?            // "婵€鍏夌瑪锛氭垜鍠滄閭ｇ'绗斿皠鍑轰竴閬撳厜'鐨勫舰鎬?锛夛紝瑙?ToolIconSheet銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ToolIconShow(args.Length > 1 ? args[1] : "reports/tool-icons.bmp");
        }
        // 2026-09-27锛氫笂娓搁偅涓€鎵?甯︾瑪鐨?鍥炬爣锛堢瑪 / 閾呯瑪 / 鑽у厜绗?/ 涔︽硶绗?/ 鐢荤瑪鈥︼級锛?        // 绗?2 涓弬鏁扮粰浜嗕笢瑗垮氨鍦ㄦ瘡鏀瑪涓婂姞涓€鍓?涓夐亾鏉?锛堬紳褰撴縺鍏夌瑪鐢ㄧ殑鏍峰瓙锛夈€?        // 鏁版嵁鏉ヨ嚜 PenIcons锛坱mp/gen-pen-icons.ps1 鎷夌殑锛夛紝**浜у搧鍥炬爣琛ㄦ病鍔?*銆?        else if (mode == "--penshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUi(new PenSheet());
            BoardOn = true;                      // 鐧芥澘鎵撳簳锛氬浘閲屾病鏈夋闈笂鐨勬潅涓滆タ
            SettleFrames(400);
            string path = args.Length > 1 ? args[1] : "reports/pen-candidates.png";
            if (!OffscreenShot(path)) Console.WriteLine("鍑哄浘澶辫触");
            _quit = true;
        }
        else if (mode == "--recoverytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RecoveryTest();
        }
        else if (mode == "--panelperf")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelPerf();
        }
        else if (mode == "--erasertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraserTest();
        }
        else if (mode == "--pixelerasetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PixelEraserTest();
        }
        else if (mode == "--dynerasertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DynEraserTest();
        }
        else if (mode == "--pixeleraseshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PixelEraserShowcase();
        }
        else if (mode == "--eraseselfcross")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseSelfCrossProbe();
        }
        else if (mode == "--eraserlab")
        {
            // **姗＄毊鎵嬫祴鍙?*锛氳繖鏄粰浜虹敤鐨勪氦浜掍細璇濓紝涓嶆槸鑷鈥斺€旈摵濂芥牱渚嬨€佹墦寮€閬ユ祴锛?            // 鐒跺悗鎶婃帶鍒舵潈浜ゅ洖寮曟搸鐨勬甯告秷鎭惊鐜紙return -1 灏辨槸"娌℃湁娴嬭瘯妯″紡"锛夈€?            EraserLab(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null,
                      args.Contains("--syscursor"));
            return -1;
        }
        else if (mode == "--eraseimage")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseImageProbe();
        }
        else if (mode == "--imageerase")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageEraseProbe();
        }
        else if (mode == "--clipboardtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ClipboardTest();
        }
        else if (mode == "--lassotest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LassoTest();
        }
        else if (mode == "--cursortest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CursorTest();
        }
        else if (mode == "--tiletest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TileTest();
        }
        else if (mode == "--prefetchtest")
        {
            // 鍒嗗潡绌洪棽棰勫彇鑷锛氭粴涓ゆ牸 鈫?棰勫彇鎶婅鍙ｅ涓€鍦堢儤濂?鈫?鍐嶆粴涓€鏍间笉閲嶇敾锛?            // 鑷甫"鍏抽鍙栧氨閲嶇敾"鐨勫鐓э紙鑷瘉鏈夋晥锛夈€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrefetchTest();
        }
        else if (mode == "--widthtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            WidthTest();
        }
        else if (mode == "--ghosttest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 鍔?"scroll" 鍐嶈窇涓€閬嶏細鐩告満涓嶄负 0 鏃讹紝鍏夋爣鐨勮剰鍖烘崲绠楀彧瑕佹紡浜?            // 鐩告満鍋忕Щ灏变細"鎿︿笉骞插噣"锛岃€岄偅绉嶆畫褰卞湪娌℃粴鍔ㄧ殑鐢婚潰涓婃牴鏈湅涓嶅嚭鏉ャ€?            GhostTest(args.Contains("scroll"));
        }
        else if (mode == "--trailtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InkTrailTest(args.Contains("--touch"));   // `--trailtest --touch` = 鍚堟垚瑙︽懜瀵圭収
        }
        else if (mode == "--fastwettest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 搴旂敤渚ф箍澧紙FastWet锛夊睆骞曡嚜妫€锛氬叧鎺夌郴缁熻建杩癸紝鏌?鎱㈠啓鏂彛"鍜?鐧芥澘闅愯韩"銆?            // `--fastwettest --board` = 涓嶉€忔槑鐧藉簳涓嬬殑閭ｆ潯锛?026-10-13 鐨勫洖褰掑氨鍑哄湪杩欙級銆?            FastWetTest();
        }
        else if (mode == "--historytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 鏉夸功鍘嗗彶鑷锛氬揩鐓у彧鍚瑪杩癸紙鎴浘涓嶈繘锛? 鎬婚噺杞浆 / 浠庡巻鍙叉壘鍥?/ 娓呴櫎鍙挙閿€銆?            BoardHistoryTest();
        }
        else if (mode == "--lagtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 婀垮ⅷ寤舵椂瀵圭収锛氫富缁?/ FastWet / 绯荤粺杞ㄨ抗 / 浜у搧缁勫悎鍥涙。锛岄噺澧ㄥ皷钀藉悗涓庤烦鍙樺搷搴斻€?            WetLagTest();
        }
        else if (mode == "--camlag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 鎵嬫満 120Hz 褰曞儚瀵圭収锛氬悓涓€濂楀寑閫熸嫋鍔ㄨ窇鍚勬箍澧ㄦ。锛屽睆涓婃湁妗ｄ綅妯潯 + 50px 鍒诲害灏猴紝
            // 鎷嶄笅鏉ラ噺"绯荤粺鍏夋爣灏?鈫?澧ㄥ皷"鐨勮窛绂伙紙梅 閫熷害 = 姣锛夈€?            string which = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "all";
            int camSpeed = 1200, camSweeps = 4;
            double camLive = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--speed" && i + 1 < args.Length && int.TryParse(args[i + 1], out var vSpeed))
                    camSpeed = Math.Clamp(vSpeed, 100, 6000);
                if (args[i] == "--sweeps" && i + 1 < args.Length && int.TryParse(args[i + 1], out var vSweeps))
                    camSweeps = Math.Clamp(vSweeps, 1, 20);
                if (args[i] == "--live" && i + 1 < args.Length && double.TryParse(args[i + 1], out var vLive))
                    camLive = Math.Clamp(vLive, 3, 600);
            }
            CamLagTest(which, camSpeed, camSweeps, camLive);
        }
        else if (mode == "--longrun")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = args.Length > 1 && double.TryParse(args[1], out var s2) ? s2 : 180;
            LongRunTest(secs);
        }
        else if (mode == "--restest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s4) ? s4 : 5000;
            ResolutionTest(n);
        }
        else if (mode == "--cornertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CornerTest();
        }
        else if (mode == "--shapetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeTest();
        }
        else if (mode == "--shapetooltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeToolTest();
        }
        else if (mode == "--shapetoolshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // `--circle / --ellipse / --triangle / --parallelogram / --rectangle / --parabola /
            //  --cuboid` 閫夊浘褰紙涓嶇粰灏辨槸鐩寸嚎锛夛紱`--snap` = 閭ｄ竴寮犺鎷?鍚搁檮鐢熸晥涓?鐨勫抚锛?            // `--angles` = 鍐呰 / 澶硅閭ｄ竴缁勮鏁帮紱`--pose` = 鎷栨棆杞焺鏃剁殑**濮挎€佽**璇绘暟銆?            // 鍚庝袱涓紙鎶涚墿绾?/ 闀挎柟浣擄級鏄?2026-09-20 鍔犵殑锛氬畠浠唬琛?**娌℃湁鐗规畩鎵嬫焺**"鐨勯偅涓€绫伙紝
            // 瑕佹媿鐨勬槸"閫氱敤鍏墜鏌?锛?鏃嬭浆鏌勯偅鏍圭嚎鎸傚摢鍎?锛堢敤鎴凤細"鎶涚墿绾跨殑鏃嬭浆鎵嬫焺绾胯繛鐨勫湴鏂瑰緢濂囨€?锛夈€?            string kind = args.Contains("--circle") ? "circle"
                        : args.Contains("--ellipse") ? "ellipse"
                        : args.Contains("--triangle") ? "triangle"
                        : args.Contains("--parallelogram") ? "parallelogram"
                        : args.Contains("--parabola") ? "parabola"
                        : args.Contains("--cuboid") ? "cuboid"
                        : args.Contains("--rectangle") ? "rectangle" : "line";
            ShapeToolShowcase(args.Length > 1 ? args[1] : "reports/shape-line-handles.bmp",
                              kind, args.Contains("--drag"), args.Contains("--draw"),
                              args.Contains("--rotate"), args.Contains("--rotated"),
                              args.Contains("--snap"), args.Contains("--angles"),
                              args.Contains("--pose"), args.Contains("--autosel"));
        }
        else if (mode == "--captest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CapTest();
        }
        else if (mode == "--selfcross")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelfCrossTest();
        }
        else if (mode == "--holetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HoleTest();
        }
        else if (mode == "--imagetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageTest();
        }
        else if (mode == "--capturetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureTest();
        }
        else if (mode == "--hotkeytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HotkeyTest();
        }
        else if (mode == "--touchtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TouchTest();
        }
        else if (mode == "--savetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SaveTest();
        }
        else if (mode == "--transformtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TransformTest();
        }
        else if (mode == "--handletest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HandleTest();
        }
        else if (mode == "--rotatetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RotateTest();
        }
        else if (mode == "--keytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            KeyTest();
        }
        else if (mode == "--wintabprobe")
        {
            // 鎺?Wintab锛堟暟浣嶆澘椹卞姩鐨勫師鐢?API锛夎繖鏉¤矾閫氫笉閫氥€?            // 鍙銆佷笉鏀逛骇鍝佽涓猴紱瑙?Engine.WintabProbe 鐨勮鏄庛€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = WintabProbe();
            _quit = true;
        }
        else if (mode == "--rotateshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            float deg = args.Length > 1 && float.TryParse(args[1], out var d) ? d : 90f;
            RotateShowcase(deg);
        }
        else if (mode == "--cursorshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string which = args.Length > 1 ? args[1].ToLowerInvariant() : "pen";
            float width = args.Length > 2 && float.TryParse(args[2], out var w) ? w : 0f;
            showcasePenDevice = args.Contains("pen");
            CursorShowcase(which, width);
        }
        else if (mode == "--selshowcase")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelShowcase(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--captureshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureShow(args.Length > 1 ? args[1] : "reports/capture-frame.png");
        }
        else if (mode == "--dialogprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DialogProbe(args.Length > 1 ? args[1] : "tmp/dlg");
        }
        else if (mode == "--edittest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EditTest();
        }
        else if (mode == "--seldrag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelDragShowcase();
        }
        else if (mode == "--seltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelTest();
        }
        else if (mode == "--selbench")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelBenchTest();
        }
        else if (mode == "--duptest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 14;
            DupTest(n);
        }
        else if (mode == "--writetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double mins = args.Length > 1 && double.TryParse(args[1], out var wm) ? wm : 20;
            double sps = args.Length > 2 && double.TryParse(args[2], out var ws) ? ws : 6;
            DenseWrite = args.Contains("dense");
            WriteTest(mins, sps);
        }
        else if (mode == "--memlife")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var ml) ? ml : 10000;
            MemLifeTest(n);
        }
        else if (mode == "--appendtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AppendPathTest();
        }
        else if (mode == "--latbench")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LatencyBench(args.Length > 1 ? args[1] : "reports/latency.csv");
        }
        else if (mode == "--penlive")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = args.Length > 1 && double.TryParse(args[1], out var pl) ? pl : 20;
            LatencyLive(secs);
        }
        else if (mode == "--cameratest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CameraTest();
        }
        else if (mode == "--gclatencytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            GcLatencyTest();
        }
        else if (mode == "--touchguardtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TouchGuardTest();
        }
        // [鍋滅敤 2026-10-05] 棰勬祴灏?棰勬祴绠楁硶鑷锛堣€侀娴嬬郴缁熷仠鐢紝瑙?宸插仠鐢?娓叉煋瀹為獙.md锛?        // else if (mode == "--predicttailtest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     PredictTailTest();
        // }
        // else if (mode == "--predicttest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     PredictorTest();
        // }
        else if (mode == "--smoothtest")
        {
            // **绾畻娉曪紝涓嶅缓绐楀彛**锛堝拰 `--inktest` 鍚屼竴涓彛寰勶級锛氭洸绾垮櫒鏄绾垮嚑浣曪紝
            // 瀹冭楠岀殑鏄?鐩磋鍙樻病鍙樺舰 / 鍦嗘湁娌℃湁鏇村渾"锛屼笉璇ユ贩杩涚晫闈?杈撳叆閭ｄ竴濂椼€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = SmoothProbe.Run();
            _quit = true;
        }
        // [鍋滅敤 2026-10-05] M3 寮圭哀涓撻」鑷锛堟ā寮忓凡鍋滅敤锛岃 宸插仠鐢?娓叉煋瀹為獙.md锛?        // else if (mode == "--inkmodeltest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     ExitCode = InkModelProbe.Run();
        //     _quit = true;
        // }
        else if (mode == "--motiontest")
        {
            // 瀵圭収鍙版€昏嚜妫€锛歁0/M1/M2/M3/M4/M5 鍚屼竴鎵硅鏂欏嚭琛ㄣ€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = MotionProbe.Run();
            _quit = true;
        }
        else if (mode == "--pdmetrics")
        {
            // 澶ф暟鎹绾跨浜岀幆锛氱湡瀹炶鏂欙紙SCUT 涓枃 / UCI锛夌绾胯瘎娴嬮娴嬪櫒鈥斺€?            // 渚涚粰鐜?/ 鍚冨埌鐜?/ 瑙掑害璇樊 / 瓒呭墠锛堣 PdMetricsProbe 鐨勫彛寰勬敞閲婏級銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = PdMetricsProbe.Run(args);
            _quit = true;
        }
        else if (mode == "--replayinput")
        {
            // 榛戝專瀛愬洖鏀撅細鎶?`--reclive` 璁板綍鐨勭湡瀹炶緭鍏?1:1 鍠傚洖寮曟搸锛岀畻鍑洪娴嬪熬鐨勬寚鏍囥€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = InputReplayProbe.Run(this, args);
            _quit = true;
        }
        else if (mode == "--smoothshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SmoothShowcase(args.Length > 1 ? args[1] : "reports/鏇茬嚎鍖?瀵圭収.bmp");
        }
        else if (mode == "--smoothflashtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SmoothFlashTest(!args.Contains("--off"), args.Contains("--fast"));
        }
        else if (mode == "--prevflash")
        {
            // 璇婃柇锛?*"鍐欎笅涓€绗旀椂锛屼笂涓€绗旈棯涓嶉棯"**锛堢敤鎴?2026-10-04 鎶ワ級銆?            // --pen = 璧板悎鎴愮瑪锛圥T_PEN + 鍘嬫劅锛岃鐩?ID2D1Ink 閭ｆ潯鐪熷疄娓叉煋璺級锛?            // --left = 鐢ㄦ埛鐨勫鐜板満鏅細妯嚎搴曠汗鐧芥澘 + 灞忓箷宸︿晶绔栧啓銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrevFlashTest(args.Contains("--pen"), args.Contains("--left"),
                          args.Contains("--ui"), args.Contains("--fast"));
        }
        else if (mode == "--scrollflash")
        {
            // 璇婃柇锛?*"婊氳疆婊氬姩涔嬪悗涓€鎸夐紶鏍囧氨闂?閿欎綅"**锛堢敤鎴?2026-10-04 鎶ワ紝
            // 鍏抽敭绾跨储锛氭粴鍔ㄤ箣鍚庛€佹寜涓嬫墠闂紝鏉炬墜灏变笉闂紱浠ュ墠绔栧啓鏃朵篃閬囧埌杩囷級銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollFlashTest();
        }
        else if (mode == "--wetdrytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 20;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--live" && double.TryParse(args[i + 1], out var s)) secs = s;
            WetDryTest(args.Contains("--live"), secs);
        }
        else if (mode == "--updatetest")
        {
            // **绾绾?*锛堣В鏋?/ 鐗堟湰姣旇緝 / sha256 / 鏈湴鍙栨竻鍗?/ 鎹㈠３鑴氭湰璇硶锛夛紝涓嶇缃戠粶
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = UpdateProbe.Run();
            _quit = true;
        }
        else if (mode == "--updatecheck")
        {
            // **楠屾敹鐢紙浼氱湡鐨勬崲澹筹紒锛?*锛歚--updatecheck [娓呭崟鍦板潃] [--apply]`
            //   涓嶇粰鍦板潃 = 鐢ㄩ厤缃噷鐨勬洿鏂版簮锛沗--apply` = 鏌ュ埌鏂扮増鏈氨鑷姩涓嬭浇瀹夎銆?            // 瀹冩槸"鍦ㄧ湡鏈轰笂楠屼竴閬嶈嚜鍔ㄦ洿鏂?鐨勫叆鍙ｏ紙鏁欏閲屼篃鑳界敤杩欐潯鍛戒护楠岋級銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string u = (args.Length > 1 && !args[1].StartsWith("--")) ? args[1] : null;
            if (u != null) UpdateFeed.Url = u;
            AutoApplyUpdate = args.Contains("--apply");
            AutoCheckOnly = !AutoApplyUpdate;      // 鍙煡涓嶈 鈫?鏌ュ畬鑷繁閫€锛堝埆鎶婄晫闈㈡寕鍦ㄥ睆骞曚笂锛?            Console.WriteLine($"=== 鑷姩鏇存柊楠屾敹锛氭簮={(UpdateFeed.Url.Length > 0 ? UpdateFeed.Url : "锛堟湭閰嶇疆锛?)}"
                              + $"锛宎pply={AutoApplyUpdate} ===");
            CheckUpdateFromUi();
        }
        else if (mode == "--tailjumptest")
        {
            // [閲嶅惎 2026-10-08] B4 棰勬祴灏?绐佺獊璺?妫€娴嬶紙鑰佺郴缁?2026-10-05 鍋滅敤锛涜 宸插仠鐢?娓叉煋瀹為獙.md锛?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TailJumpTest(args.Contains("--noisy"), args.Contains("--off"));
        }
        else if (mode == "--predlinetest")
        {
            // 绾挎€ч娴嬬函绠楁硶鑷锛?026-10-15 鍔狅級锛氫笉闇€瑕佺湡绗?灞忓箷锛堣 SelfTests/PredictLinear.cs锛夈€?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PredictLinearTest();
        }
        else if (mode == "--tapdotprobe")
        {
            // [2026-10-08] "璧风瑪鍐掑渾"澶嶇幇鍣紙鍚堟垚绗旓級锛氳 TapDotProbe銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TapDotProbe();
        }
        else if (mode == "--pressurediag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureDiagTest();
        }
        else if (mode == "--pressuretest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureTest();
        }
        else if (mode == "--inkfiletest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InkFileTest();
        }
        else if (mode == "--replaytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ReplayTest();
        }
        else if (mode == "--timertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TimerTest();
        }
        else if (mode == "--rolltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RollTest();
        }
        else if (mode == "--replayshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ReplayShow(args.Length > 1 ? args[1] : "reports/replay-bar.bmp");
        }
        else if (mode == "--ballprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BallProbe(args.Length > 1 ? args[1] : "reports/ball");
        }
        // [鍋滅敤 2026-10-05] 鐪熷疄绗旇抗鏁版嵁棰勬祴璇勬祴锛堣€侀娴嬬郴缁熷仠鐢紝瑙?宸插仠鐢?娓叉煋瀹為獙.md锛?        // else if (mode == "--predictdata")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     PredictEval.Run(args.Length > 1 ? args[1] : "tmp/datasets");
        //     _quit = true;
        // }
        else if (mode == "--wetinktest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 15;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--live" && double.TryParse(args[i + 1], out var s)) secs = s;
            if (args.Contains("--live")) WetInkLiveTest(secs);
            else WetInkTest(args.Contains("--activate"));
        }
        else if (mode == "--scrollwrite")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollWriteTest();
        }
        else if (mode == "--scrollshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollShowcase();
        }
        else if (mode == "--wheeltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            WheelTest();
        }
        else if (mode == "--ppttest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PptTest();
        }
        else if (mode == "--pptshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PptShow(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--pptprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 12;
            if (args.Length > 1 && double.TryParse(args[1], out var ps)) secs = ps;
            PptProbe(secs);
        }
        else if (mode == "--pagetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageTest();
        }
        else if (mode == "--doctest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DocumentTest();
        }
        else if (mode == "--pdfprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int pn = args.Length > 2 && int.TryParse(args[2], out var ptmp) ? ptmp : 3;
            PdfProbe(args.Length > 1 ? args[1] : null, pn);
        }
        else if (mode == "--docstates")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DocStatesProbe(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--iotest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 缁欒矾寰勬椂**淇濈暀鏂囦欢**锛堜汉宸ユ牳瀵?/ 鎷垮埆鐨勮В鐮佸櫒楠屽畠锛?            IoTest(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--patterntest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PatternTest();
        }
        else if (mode == "--lasertest")
        {
            // 婵€鍏夌瑪鑷锛?026-09-27锛夛細鎷栧熬瀵垮懡锛堝啓杩囨暣鏉′笉缂┿€佹澗鎵嬪仠 2 绉掑啀娣★級銆?            // 澶氭潯骞跺瓨銆佺矖缁嗙湡鐨勭鐢ㄣ€佷笉杩涙枃妗ｃ€傞『甯﹀嚭涓€寮犲浘锛堟憜鏍疯鐪嬶紝鑷鍒や笉浜嗗ソ涓嶅ソ鐪嬶級銆?            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LaserTest();
        }
        else if (mode == "--coordtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 12;
            CoordTest(n);
        }
        else
        {
            // 娌℃湁娴嬭瘯妯″紡锛氫氦鍥炲紩鎿庤窇姝ｅ父娑堟伅寰幆銆?            //
            // 鈿?**浣嗚鎶?鑷閭ｅ闅旂"鎾ゆ帀**锛?026-10-07 淇級銆?            // 涓婇潰涓€杩涙潵灏?`_selfCheckMode = mode.Length > 0`锛屼簬鏄?*浠讳綍涓€涓潪绌虹殑绗竴鍙傛暟**
            // 锛堝寘鎷?`--nowintab` / `--clean 3` / `--recink 鈥 杩欎簺**寮€鍏?*锛?            // 閮戒細杩?鍗婁釜鑷鐘舵€?锛氭崲涓存椂璁剧疆銆佹崲瀛樻。璺緞銆佸叧鎺夊鍑哄璇濇 鈥斺€?            // **鑰岀敤鎴峰畬鍏ㄧ湅涓嶅嚭鏉?*锛堜粬鍙細瑙夊緱"鎬庝箞鍜屽钩鏃朵笉涓€鏍?锛夈€?            //
            // 鍒ゆ嵁鏀规垚缁撴瀯鎬х殑锛?*鍙湁褰撴煇涓?mode 鍒嗘敮鐪熺殑璁ら浜嗗畠锛屾墠绠楄嚜妫€妯″紡**銆?            // 璧板埌杩欎釜鍏滃簳鍒嗘敮 = 娌′汉璁ら = 褰撴垚姝ｅ父鍚姩銆?            // 锛堜互鍓嶉潬涓€寮?杩欎簺寮€鍏充笉鏄?mode"鐨勬竻鍗曟潵鎸?鈥斺€?閭ｇ娓呭崟涓€瀹氫細婕忥紝浠婂ぉ婕忎簡涓夋銆傦級
            _selfCheckMode = false;
            SelfCheckMode = false;                 // 寮曟搸閭ｄ竴浠戒篃鎾ゆ帀锛屽厤寰楁棩蹇楅噷璋庢姤"鑷"
            InkSettings.PathOverride = null;
            Recovery.AutoSavePathOverride = null;
            PptStore.RootOverride = null;
            DocStore.RootOverride = null;
            ExportDialogEnabled = true;

            // `--doc <鏂囦欢>`锛氬埌杩欏効绐楀彛/瑙嗗彛閮藉氨缁簡锛岀湡姝ｆ墦寮€瀹?            // 锛堝け璐ュ彧鎻愮ず鈥斺€旇鐭╀笁锛氬け璐ュ綋娌℃湁锛屼笉褰卞搷鍚姩锛?            if (_startupDocPath != null)
            {
                var err = OpenDocuments(new[] { _startupDocPath });
                if (err != null) Console.WriteLine($"[鏂囨。] 鎵撳紑澶辫触锛歿err}");
            }
            return -1;
        }

        Loop();
        Shutdown();
        return 0;
    }

    /// <summary>閫€鍑哄墠鎶婂伐鍏锋潯鐨勯厤缃惤鐩橈紝淇濊瘉鑰佸笀鎷栧姩鐨勪綅缃笉浼氫涪銆?/summary>
    protected override void Shutdown()
    {
        base.Shutdown();
    }

    /// <summary>鐐瑰嚮鐩爣绐楀彛鍙叧蹇?鏈夋病鏈夎鐐瑰埌"銆?/summary>
    /// <summary>
    /// 鑷閲?*涓嶇湡鐨勬媺鏂拌繘绋?*锛堜細寮€鍑轰竴鍫嗚鐩栫獥鍙ｏ紝鎶婃満鍣ㄥ崰婊★級锛?    /// 鍙涓€绗斿苟杩斿洖"鎴愬姛"鈥斺€旇繖鏍?閲嶅缓鏁戜笉鍥炴潵灏遍噸鍚?閭ｆ潯璺兘琚畬鏁磋蛋鍒般€?    /// 浜у搧閲岃蛋鍩虹被瀹炵幇锛堢湡鎷夎繘绋嬶級銆?    /// </summary>
    protected override bool RestartSelf(string why)
    {
        // **鍙湁鑷妯″紡鎵嶆嫤**銆備互鍓嶈繖閲屾棤鏉′欢杩斿洖 true锛屼簬鏄骇鍝侀噷鐨?閲嶅惎"涔熶笉閲嶅惎浜?        // 锛堢敤鎴风偣浜嗕竴涓嬶紝鐣岄潰杩樺湪锛屼粈涔堥兘娌″彂鐢燂級鈥斺€旇嚜妫€鐨勬浛韬妸浜у搧璺緞涓€璧锋崲鎺変簡銆?        if (!_selfCheckMode) return base.RestartSelf(why);
        Console.WriteLine($"[鑷] 涓嶇湡鐨勯噸鍚紙{why}锛夛紱浜у搧閲岃繖涓€姝ヤ細閲嶆柊鎷夎捣鑷繁");
        return true;
    }

    protected override bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_clickTargetHwnd == IntPtr.Zero || hWnd != _clickTargetHwnd)
            return false;

        if (msg == Native.WM_LBUTTONDOWN)
        {
            try { File.AppendAllText(_clickLogFile, "clicked\n"); } catch { }
            return true;
        }
        if (msg == Native.WM_CLOSE) { _quit = true; return true; }

        // 鍏跺畠娑堟伅浜ゅ洖榛樿澶勭悊锛屼笉鑳藉湪杩欏効鍚炴帀锛屽惁鍒欑獥鍙ｇ敾涓嶅嚭鏉ャ€?        result = Native.DefWindowProc(hWnd, msg, wParam, lParam);
        return true;
    }

    protected override void PrintUsageHelp()
    {
        base.PrintUsageHelp();
        Console.WriteLine("寮€鍙戞湡娴嬭瘯妯″紡锛堜笉灞炰簬寮曟搸锛夛細");
        Console.WriteLine("  --selftest [绉抅     鑷姩鐢诲嚑绗斿苟妫€鏌ユ覆鏌?);
        Console.WriteLine("  --report [鏂囦欢]     杈撳嚭瀹屾暣鎬ц兘鎶ュ憡");
        Console.WriteLine("  --memory [鏂囦欢]     杈撳嚭鍐呭瓨褰掑洜鎶ュ憡");
        Console.WriteLine("  --inputtest         鎸囬拡杈撳叆璺緞鑷");
        Console.WriteLine("  --passtest          绌块€忕湡鏈烘祴璇曪紙璺ㄨ繘绋嬬偣鍑伙級");
        Console.WriteLine("  --uitest            鐣岄潰杈撳叆閫氳矾鑷锛堝悎鎴愮偣鍑伙紝鐪嬭皝鏀跺埌锛?);
        Console.WriteLine("  --paneltest         浜у搧鐣岄潰鑷锛堢悆 鈫?鎸夐挳甯﹁繖鏉℃渶灏忛棴鐜級");
        Console.WriteLine("  --radialtest        鍛煎嚭鐩樿嚜妫€锛圕trl+Alt+Shift+Q锛氬紑 / 鍒?/ 鏉?/ 鍙栨秷 / 绌块€?/ 鏉鹃敭杞锛?);
        Console.WriteLine("  --radialshow [鍥綸   鍛煎嚭鐩樻憜鏍凤細瀹氭牸鍦ㄥ睆骞曚腑澶嚭鍥撅紙榛樿 reports/radial-palette.bmp锛?);
        Console.WriteLine("  --shapeiconshow [璺緞] 鍑哄浘锛氬浘褰㈤潰鏉垮浘鏍囩殑瀵圭収琛紙鍚瘡涓€妗ｇ殑鍙樹綋锛?);
        Console.WriteLine("  --toolicons [璺緞]  鍑哄浘锛氱櫧鏉?/ 婵€鍏夌瑪鐨勫浘鏍囧€欓€夛紙鏈€変腑 / 閫変腑 / 鏀惧ぇ涓夋牸锛?);
        Console.WriteLine("  --penshow [璺緞] [bars]  鍑哄浘锛氫笂娓搁偅鎵广€屽甫绗旂殑銆嶅浘鏍囷紱甯︾ 2 涓弬鏁板氨姣忔牸鍔犱竴鍓€屼笁閬撴潬銆?);
        Console.WriteLine("  --shapebandtest     鍥惧舰閭ｆ牸鐨勭晫闈㈠叆鍙ｈ嚜妫€锛堜竷娈?/ 涓変釜鏂扮儹閿?/ 涓绘潯鍥炬爣璺熺潃鍙橈級");
        Console.WriteLine("  --inktest           **澧ㄨ抗璇嗗埆鐨勫崟鐙祴璇?*锛堟棩甯告敼璇嗗埆鍙窇杩欎竴鏉★細鍑嗙‘鐜?/ 璇姤 / 闅句緥 / 鍐欏洖锛?);
        Console.WriteLine("  --dwelltest         鍋滈】鎴愬瀷鐨勬墜鍔胯嚜妫€锛堢湡鍏ュ彛 + 鎺ㄦ椂閽燂細瑙﹀彂 / 瀹氬瀷 / 鎾ら攢鍥炴墜缁?/ 涓嶇暀澧級");
        Console.WriteLine("  --dashtest          绾垮瀷鑷锛堝疄绾?铏氱嚎/鐐圭嚎涓婂睆澧ㄩ噺銆侀潰鏉块偅涓€琛屻€佸瓨妗ｅ線杩旓級");
        Console.WriteLine("  --axistest          鍧愭爣绯?鏁拌酱鑷锛堢敾娉?/ 鍥涗釜鎵嬫焺 / 缃戞牸 / 涓婂睆 / 瀛樻。寰€杩旓級");
        Console.WriteLine("  --axisshow [璺緞]   鍑哄浘锛氬潗鏍囩郴锛堝甫缃戞牸/涓嶅甫锛? 鏁拌酱 + 涓€鏉¤櫄绾?);
        Console.WriteLine("  --curvetest         鍥涚鏇茬嚎鑷锛堢敾娉?/ 绱ф / 鎵嬫焺绮剧畝 / 鏈濆悜 / 瀛樻。寰€杩旓級");
        Console.WriteLine("  --curveshow [璺緞]  鍑哄浘锛氭姏鐗╃嚎鍥涚寮€鍙?+ 鍙屾洸绾夸袱涓柟鍚?+ 姝ｅ鸡/浣欏鸡鍚勪竴鍛ㄦ湡");
        Console.WriteLine("  --conicshow [璺緞]  鍑哄浘锛氬弻鏇茬嚎涓ゆ。锛堟湁/鏃犳笎杩戠嚎锛? 妞渾涓ゆ。锛堟湁/鏃犵劍鐐逛笁瑙掑舰锛?);
        Console.WriteLine("                      锛堢涓夎椤哄甫鍑?*鏃嬭浆浣撻偅涓€鏃?*锛氬渾鏌?/ 鍦嗛敟 / 鍦嗗彴 / 鐞冿級");
        Console.WriteLine("  --pagetest          鏁村睆缈婚〉鑷锛堜竴灞?= 涓€椤碉細椤甸珮 = 瑙嗗彛楂樸€佸彧鍔ㄧ浉鏈恒€佸埌椤跺氨鍋滐級");
        Console.WriteLine("  --ppttest           PPT 妯″紡鑷锛堝亣椤电爜婧愶細闅旂 / 椤靛唴婊氬姩 / 娓呴櫎鍏ㄩ儴澧ㄨ抗=鎵€鏈夐〉涓斿彲鎾ら攢 / 閫€鍑哄啓鐩?/ 鍐嶈繘璇诲洖锛?);
        Console.WriteLine("  --pptprobe [绉抅     PPT 杩炴帴鎺㈤拡锛堢湡鏈鸿窇锛氭墦寮€ PPT 鎸?F5锛岀湅杩炰笉杩炲緱涓婏級");
        Console.WriteLine("                     鍔?--pptcmd 浼氬疄闄呮寜 Next/Prev/GotoSlide 楠岃瘉鍛戒护锛?-pptdebug 鎵?ROT 缁嗚妭");
        Console.WriteLine("  --iotest [璺緞]     瀵煎嚭鑷锛堥€変腑 鈫?PNG 閫忔槑搴?/ JPEG 鐧藉簳锛涚粰璺緞灏变繚鐣欐枃浠讹級");
        Console.WriteLine("  --patterntest       鐧芥澘搴曠汗鑷锛堟柟鏍?妯嚎/闂磋窛 + 鏁板睆骞曚笂鐨勭嚎 + 閲嶉摵浠ｄ环锛?);
        Console.WriteLine("  --lasertest         婵€鍏夌瑪鑷锛堟嫋灏句竴鐩村啓涓嶇缉 / 鏉炬墜鍋?2 绉掑啀鏁翠綋娣″嚭 / 澶氭潯骞跺瓨 /");
        Console.WriteLine("                      绮楃粏鐪熺殑绠＄敤 / 涓嶆槸绗旇抗锛夛紱椤哄甫鍑哄浘 reports/laser-trail.png");
        Console.WriteLine("  --pageshow <鍥?     鏁村睆缈婚〉鎽嗘牱锛堢浉鏈哄仠鍦ㄤ袱灞忎箣闂?/ 姝ｅソ瀵归綈锛屽悇鍑轰竴寮狅級");
        Console.WriteLine("  --panelshow <鍥? [--band] [--mini] [--more [--page N]] [--cell N] [--shape 鍚嶅瓧] [--zoom N]   鐣岄潰鍑哄浘锛堢灞忥紱--more --page 0=鍚姩鍣?1=璁剧疆锛?);
        Console.WriteLine("  --demogif <鐩綍>    婕旂ず杩炴媿锛堝啓瀛椻啋鍋滈】鍙樺浘褰⑩啋閫変腑鎷栧姩鈫掓埅鍥惧彇鏅紝瀛?NNNN.bmp 甯э級");
        Console.WriteLine("  --makeicon <鍥?ico>      鐢ㄧ晫闈㈣嚜宸辩殑娓叉煋鐢熸垚绋嬪簭鍥炬爣锛堢嚎鏉＄瑪锛嬬櫧鐮栵紜甯︾瑪閿嬬殑绾㈢瑪杩癸級");
        Console.WriteLine("  --captureshow <鍥?  鎴浘鍙栨櫙妗?+ 灏哄璇绘暟鍑哄浘锛堢灞忥級");
        Console.WriteLine("  --dialogprobe <鍓嶇紑> [--save]  瀵煎嚭瀵硅瘽妗嗘帰閽堬紙鐪熷脊妗?+ 鐐瑰畠鐨勪笅鎷?+ 杩炴媿涓夊紶锛?);
        Console.WriteLine("                     --save 杩炪€屼繚瀛樸€嶄竴璧风偣锛岄獙鍒拌惤鐩樹负姝級");
        Console.WriteLine("  --erasertest        姗＄毊鎿︽纭€?);
        Console.WriteLine("  --pixelerasetest    鍍忕礌姗＄毊姝ｇ‘鎬э紙鍒囨垚涓ゆ / 妗嗛噷鏃犲ⅷ / 涓€姝ユ挙閿€锛?);
        Console.WriteLine("  --dynerasertest     鍔ㄦ€佹鐨紙鏇茬嚎锛氭鍖哄埌 0.8 / 鎱?鍩哄噯1.0涓嶇缉 / 蹇皝椤?2.5锛涚獥鍙ｆ姉鎶栵紱鎱㈡壂 vs 蹇壂锛涙璺熼€熷害锛涗弗涓濆悎缂濓級");
        Console.WriteLine("  --eraserhud         姗＄毊璇绘暟娴眰锛堝乏涓嬭瀹炴椂 閫熷害/鐩爣/褰撳墠绯绘暟/灏哄锛涜皟闂ㄦ鐢紝涓嶈繘鐣岄潰锛?);
        Console.WriteLine("  --pixeleraseshow    鍍忕礌姗＄毊鎽嗘牱锛堟摝涔嬪墠/涔嬪悗鍚勫瓨涓€寮犲浘锛岃嚜宸辨姄灞忥級");
        Console.WriteLine("  --eraserlab [鍓嶇紑]  姗＄毊鎵嬫祴鍙帮細閾烘牱渚?+ 璁板綍姣忔潯鎷栨嫿锛岀粰浜虹敤榧犳爣娴嬶紙涓嶈嚜鍔ㄩ€€鍑猴級");
        Console.WriteLine("  --imagetest         鍥惧儚瀵硅薄锛堜笂灞?/ 澶嶅埗缈昏浆 / 瀛樻。 / 鍓创鏉匡級");
        Console.WriteLine("  --clipboardtest     鍓创鏉垮璞￠€氶亾锛堝鍒?鈫?绮樺洖鏉ヤ粛鏄璞★紱浼氳鐩栫郴缁熷壀璐存澘锛?);
        Console.WriteLine("  --lassotest         濂楃储閫夋嫨锛?0% 鍒ゆ嵁 / 璐磋竟鏃犻檺寤朵几 / 鍔犻€夊噺閫夛級");
        Console.WriteLine("  --capturetest       鎴浘锛堟嫋妗?鈫?宸︿笂瑙?鈫?鍓创鏉匡紝涓斾笉鎷嶈繘鑷繁鐨勬壒娉級");
        Console.WriteLine("  --cursorshow <绗攟鑽у厜绗攟婵€鍏夌瑪|姗＄毊|鍍忕礌姗＄毊> [瀹絔  钀界偣鎽嗘牱");
        Console.WriteLine("  --widthtest         绗旇抗绮楃粏/鍘嬪姏");
        Console.WriteLine("  --ghosttest         娈嬪奖妫€娴?);
        Console.WriteLine("  --tiletest          鍒嗗潡缂撳瓨鑷锛堣竟鐣屾棤缂?/ 鍥炵▼澶嶇敤 / 鍐呭瓨涓婄晫锛?);
        Console.WriteLine("  --prefetchtest      鍒嗗潡绌洪棽棰勫彇鑷锛堥鍙栧懡涓垯鍐嶆粴涓€鏍间笉閲嶇敾锛涘惈鍏抽鍙栧鐓э級");
        Console.WriteLine("  --noprefetch        鍏虫帀鍒嗗潡绌洪棽棰勫彇锛堝鐓э紱榛樿寮€锛?);
        Console.WriteLine("  --trailtest [--touch]  濮旀墭澧ㄨ抗杞ㄨ抗瀵圭収锛堥粯璁ゅ悎鎴愰紶鏍囷紱--touch = 鍚堟垚瑙︽懜锛?);
        Console.WriteLine("  --uiscale <0.8|0.9|1|1.15|1.3>  鐣岄潰缂╂斁妗ｏ紙宸ュ叿甯?鑹插甫/鎶藉眽/璁剧疆椤碉紱鍙鐩栨湰娆¤繍琛岋紝涓嶈惤鐩橈級");
        Console.WriteLine("  --smoothtest        涓績绾挎洸绾垮寲鑷锛堣繃鐐?Catmull-Rom锛氱洿瑙掍笉鍙樺舰 / 鍦嗗姬鏇村渾婊?/ 褰㈢姸涓嶈窇锛?);
        Console.WriteLine("  --smoothshow [鍥綸   鍑哄浘锛氭洸绾垮寲寮€/鍏冲鐓э紙鍚屼竴缁勬牱鏈悇瀛樹竴寮?-off / -on锛?2 浣?BMP锛?);
        Console.WriteLine("  --smoothflashtest [--off]  鈥滅敾鐨勬椂鍊欓棯涓嶉棯鈥濅笓椤规娴嬶紙鍚堟垚榧犳爣鐢昏繃鍘伙紝鐪嬪凡缁忕敾杩囩殑澧ㄨ繕鍔ㄤ笉鍔級");
        Console.WriteLine("  --prevflash [--pen] [--left]  鈥滃啓涓嬩竴绗旀椂锛屼笂涓€绗旈棯涓嶉棯鈥濅笓椤规娴嬶紙鍚堟垚榧犳爣/鍚堟垚绗旓紱--left=宸︿晶绔栧啓+妯嚎搴曠汗锛?);
        Console.WriteLine("  --motion <鍚嶅瓧>     catmull / mean2锛?*榛樿 mean2**=璺濈绐楋紜杩囩偣鏇茬嚎锛嬫敹绗旇拷璧讹級");
        Console.WriteLine("  --motiontest        杩愬姩妯″瀷鑷锛坆aseline / catmull / mean2 鍚屾壒璇枡鍑鸿〃锛?);
        Console.WriteLine("  --pdmetrics <璇枡> [--dt 15] [--limit N] [--sweep]  棰勬祴鍣ㄧ绾胯瘎娴嬶紙SCUT/UCI锛氫緵缁?鍚冨埌/瑙掑害璇樊/瓒呭墠锛?);
        Console.WriteLine("  --reclive <鏂囦欢>     杈撳叆榛戝專瀛愶細璁板綍鍠傝繘寮曟搸鐨勮緭鍏ワ紙閰?--replayinput 绂荤嚎澶嶇幇锛?);
        Console.WriteLine("  --replayinput <鏂囦欢> [--frame 10]  榛戝專瀛愬洖鏀撅細渚涚粰鐜?甯ч棿澧ㄥ皷浣嶇Щ/鏂瑰悜缈昏浆锛堣瘖鏂級");
        Console.WriteLine("  --himetric / --nohimetric  浜氬儚绱犺緭鍏ワ紙榛樿寮€锛?-nohimetric 閫€鍥?D0 瀵圭収锛?);
        Console.WriteLine("  妯″瀷璋冨弬锛?-mean2win 鐢诲竷鍍忕礌 / --smoothcorner N 瑙掔偣闃堝€?);
        Console.WriteLine("  棰勬祴灏撅細--nopredict2 鍏筹紙榛樿寮€锛涘敮涓€绠楁硶 = 绾挎€ч娴嬶紙Chromium 鍚屾棌锛夛紝--predict2 鍙啀寮€锛?);
        Console.WriteLine("  [宸插仠鐢╙ 棰勬祴銆佹嫙鍚?--mean2fit)銆佹ā鎷熷帇鍔?--simpressure/--pfpressure)銆佺瑪閿?);
        Console.WriteLine("           (--simtaper/--flicktip)銆佸鐓фā寮?raw/sliding/spring/oneeuro/mean/gauss)绛夛細");
        Console.WriteLine("           瑙?宸插仠鐢?娓叉煋瀹為獙.md锛堜唬鐮佷繚鐣欙級");
        Console.WriteLine("  --wetdrytest [--live 20] 婀垮ⅷ/骞插ⅷ浜ゆ帴娴嬮噺锛?*瑕佺湡绗?*锛氬湪涓棿閭ｆ潯娴呯伆绾块棿鐢讳竴绗旓級");
        Console.WriteLine("  --updatetest        鑷姩鏇存柊鑷锛堢绾匡細瑙ｆ瀽 / 鐗堟湰姣旇緝 / sha256 / 涓嬭浇鍊欓€?/ 鎹㈠３鑴氭湰娌欑鐪熻窇锛?);
        Console.WriteLine("  --updatecheck [娓呭崟鍦板潃] [--apply]  鑷姩鏇存柊楠屾敹锛?*浼氱湡鐨勬崲澹?*锛?-apply = 鏌ュ埌灏辫锛?);
        Console.WriteLine("  --touchguardtest    瑙︽懜鑷锛堝悎鎴愯Е鎽革細PT_TOUCH 閫氳矾 + 绗簩鏍规墜鎸囦笉璁告姠绗旓級");
        Console.WriteLine("  --touchtest         瑙︽懜鎵嬪娍鑷锛堝崟鎸囧啓 / 鍙屾寚婕父缈婚〉 / 涓夋寚鎿?/ 闀挎寜閫?/ 閫変腑鍙樻崲 / 鎬诲紑鍏筹級");
        Console.WriteLine("  --notouch           瑙︽懜鎵嬪娍鎬诲紑鍏?*涓存椂鍏虫帀**锛堜繚闄╀笣 / A-B 瀵圭収锛涗笉鍐欏亸濂斤級");
        Console.WriteLine("  --touchhud          瑙︾偣璇婃柇娴眰锛堣Е鐐规暟 / 鎺ヨЕ闈㈢Н / 鏈€杩戠殑杈撳叆璁惧锛?);
        Console.WriteLine("  --gclatencytest     涔﹀啓鏈熼棿 GC 浣庡欢杩熸。鑷锛堢湡杩涘緱鍘?/ 鏃犵 2 浠ｅ洖鏀?/ 瓒呮椂閫€寰楀洖锛?
                          + "锛?-nogclatency 瀵圭収銆?-gchold N 璋冧繚鎸佹绉?);
        Console.WriteLine("  --pressurediag      鍘嬫劅閲囬泦璇婃柇锛堝悎鎴愮瑪娉ㄥ叆锛氬悎骞剁偣銆佸帇鎰熸湁鏁堜綅銆佸帇鍔涘垎甯冿級");
        Console.WriteLine("  --pressuretest      鍘嬫劅鑷锛堟槧灏勫嚱鏁?/ 涓婂睆绮楃粏闅忓帇鍔涘彉 / 鏃犲帇鎰熶笌铏氱嚎鍥為€€ / 寮€鍏冲線杩?/ 瀛樻。寰€杩旓級");
        Console.WriteLine("  --inkfiletest       澧ㄨ抗鏂囦欢鑷锛堜繚瀛?鎵撳紑寰€杩?/ 鎵撳紑鍓嶅浠戒笌杞浆 / 鍧忔枃浠朵笉鍔ㄦ枃妗?/ 鏀炬槧涓疆鐏帮級");
        Console.WriteLine("  --historytest       鏉夸功鍘嗗彶鑷锛堝揩鐓у彧鍚瑪杩广€佷笉鍚埅鍥?/ 鎬婚噺杞浆 / 浠庡巻鍙叉壘鍥?/ 娓呴櫎鍙挙閿€锛?);
        Console.WriteLine("  --replaytest        澧ㄨ抗鍥炴斁鑷锛堟椂闂磋酱 / 鍓嶇紑 / 鏆傚仠 / 鍊嶉€?/ 鍙 / 鎺у埗鏉?/ 缈婚〉閫€鍑猴級");
        Console.WriteLine("  --timertest         璇惧爞璁℃椂鍣ㄨ嚜妫€锛堝姛鑳藉崱锛氳缃€佲啍杩愯鎬?/ 鈿?/ 卤1鍒?/ 鍒扮偣瓒呮椂 / 鎻愮ず闊?/ 绌块€忓皬绐楋級");
        Console.WriteLine("  --rolltest          璇惧爞鐐瑰悕鑷锛堝姛鑳藉崱锛氳缃啍缁撴灉 / 婊氬姩瀹氭牸 / 鍘婚噸姹犲瓙 / 鍚嶅崟 / 绌块€忓皬绐楋級");
        Console.WriteLine("  --replayshow <鍥?   澧ㄨ抗鍥炴斁鎽嗘牱锛堥摵鍑犵瑪 鈫?鎾埌涓€鍗?鈫?鎴帶鍒舵潯閭ｄ竴鍧楋級");
        Console.WriteLine("  --ballprobe [鍓嶇紑]  鏀惰捣鐞冭创杈硅瘖鏂紙宸?鍙?鍥涜 脳 鏄剧ず/闅愯棌锛岄€愪釜鍑哄浘锛?);
        Console.WriteLine("  --fastwettest       搴旂敤渚ф箍澧紙FastWet锛夎嚜妫€锛氬叧鎺夌郴缁熻建杩癸紝鏌ユ參鍐欐柇鍙ｏ紱");
        Console.WriteLine("                      `--fastwettest --board` 鍐嶆煡涓嶉€忔槑鐧芥澘涓嬫箍澧ㄥ彲涓嶅彲瑙?);
        Console.WriteLine("  --lagtest           婀垮ⅷ寤舵椂瀵圭収锛堝悎鎴愰紶鏍囷細涓荤粯 / FastWet / 绯荤粺杞ㄨ抗 / 浜у搧缁勫悎锛?);
        Console.WriteLine("                      閲廫"澧ㄥ皷钀藉悗鎸囬拡\"澶氬皯 px 涓庤烦鍙樺搷搴?ms锛涜建杩规。杩樿兘鐪嬪嚭瀹冨埌搴曠敾娌＄敾锛?);
        Console.WriteLine("  --camlag [all|1..4] [--speed N] [--sweeps N] [--live N]   鎵嬫満 120Hz 褰曞儚瀵圭収锛?);
        Console.WriteLine("                      鍚屼竴濂楀寑閫熸嫋鍔ㄩ€愭。璺戯紙1 涓荤粯 2 FastWet 3 绯荤粺杞ㄨ抗 4 浜у搧缁勫悎锛夛紝");
        Console.WriteLine("                      灞忎笂鏈夋。浣嶆í鏉?+ 姣?50 鐗╃悊鍍忕礌鐨勫埢搴﹀昂锛涙媿涓嬫潵閲廫"鍏夋爣灏栤啍澧ㄥ皷\"");
        Console.WriteLine("                      鐨勮窛绂?梅 閫熷害 = 姣銆?-live N = 鎹㈡垚浣犺嚜宸辩敤绗斿啓 N 绉掋€?);
        Console.WriteLine("  --wetinktest        婀垮ⅷ杞ㄨ抗瀹炴祴锛堝彧璁╃郴缁熺敾锛屾暟涓婂睆鍍忕礌锛氳繖鏉￠€氶亾鍒板簳鐢讳笉鐢伙級");
        Console.WriteLine("  --wetmode <0..3>    婀垮ⅷ妗ｄ綅锛堣浣忓埌璁剧疆锛涜繍琛屾椂 Ctrl+Alt+Shift+W 寰幆鍒囨崲锛夛細");
        Console.WriteLine("                      0=鍘熻矾寰勶紙榛樿锛氫笉鍠傝建杩广€佷笉寤烘箍澧ㄥ眰锛?=+绯荤粺濮旀墭杞ㄨ抗");
        Console.WriteLine("                      2=+搴旂敤婀垮ⅷ灞傦紙FastWet锛?=鍏ㄥ紑锛堣建杩逛紭鍏堛€丗astWet 鍏滃簳锛?);
        Console.WriteLine("                      涔熷彲浠ョ偣锛氥€屾洿澶?鈫?璁剧疆 鈫?澧ㄨ抗 鈫?婀垮ⅷ妗ｄ綅銆嶏紙鐐逛竴涓嬫崲涓€妗ｏ級");
        Console.WriteLine("  --syswet            鐪熺瑪 + 瀹炵嚎绗旓細婀垮ⅷ鍙绯荤粺杞ㄨ抗鐢伙紙璇婃柇锛涒殸 鍗曟潯杞ㄨ抗绯荤粺涓婇檺 128 鐐癸紝闀跨瑪鐢讳細鍥炴粴锛?);
        Console.WriteLine("  --trailonly         鑷繁閭ｄ竴绗斾笉鐢伙紙婀垮ⅷ鍙墿绯荤粺杞ㄨ抗锛夆€斺€旀墜鍔ㄩ獙璇佺郴缁熷埌搴曠粰鍝杈撳叆鐢?);
        // [鍋滅敤] Console.WriteLine("  --predictdata [璺緞] 鐪熷疄绗旇抗鏁版嵁涓婄殑棰勬祴璇勬祴锛圲CI Character Trajectories锛?);
        Console.WriteLine("  --latbench <csv>    寤舵椂瀹炴祴锛堝垎鍦烘櫙 + 鍒嗕綅鏁?+ 绋冲畾鎬э級");
        Console.WriteLine("  --penlive [绉抅      鐪熺瑪寤舵椂瀹炴祴锛堟寕涓婃墜鍐欑瑪鍐欎竴浼氬効锛屽嚭鎶ュ憡锛?);
        Console.WriteLine("  --longrun [绉抅      闀挎椂杩愯鍐呭瓨/CPU");
        Console.WriteLine("  --restest [n]       鍒嗚鲸鐜囧鐓э紙绂诲睆灞傦級");
        Console.WriteLine("  --cornertest        鎶樿濉厖鑷锛?5/90/135掳 浼氫笉浼氭帀鑹诧級");
        Console.WriteLine("  --captest           绗旇抗涓ょ鑷锛堝崟鍑伙紳鍦嗙偣锛屽绗旓紳鍦嗗ご鏀跺熬锛?);
        Console.WriteLine("  --seltest           妗嗛€夎嚜妫€锛堟鍒扮殑灏辫閫変腑锛?);
        Console.WriteLine("  --selfcross         鑷氦鍙犺壊鑷锛堜竴绗旇嚜宸辩┛杩囪嚜宸憋紝棰滆壊涓嶈兘鍙樻繁锛?);
        Console.WriteLine("  --board             鎵撳紑鐧芥澘锛堜笉閫忔槑搴曡壊锛夛細鑷鎴浘閲屽彧鏈夋垜浠嚜宸辩殑鍐呭");
        Console.WriteLine("  --rotatetest        鏃嬭浆搴︽暟璇绘暟涓庡惛闄勶紙鍚湡鏈烘嫋鍔?+ 涓婂睆鏍稿锛?);
        Console.WriteLine("  --screenshot <璺緞> [x y w h]  鎴睆宸ュ叿");
        Console.WriteLine("  --clicktarget <鏃ュ織>           绌块€忔祴璇曠敤鐨勪笅灞傜獥鍙?);
        Console.WriteLine();
    }

    /// <summary>
    /// 瀵规瘮瀹為獙锛氬悓涓€绗旂敾锛屽紑/鍏?Direct2D 鐨勬姉閿娇鍚勭敾涓€娆°€?    /// 鐢ㄦ潵璇存槑"鍏虫帀鎴戜滑鐨勪紭鍖栦箣鍚庯紝鐪嬬潃浠嶇劧骞虫粦"杩欓儴鍒嗘槸绯荤粺缁欑殑銆?    /// </summary>
    private void AntialiasProbe(bool enable)
    {
        var w = _windows[0];
        var ctx = w.Context;
        var before = ctx.AntialiasMode;
        ctx.AntialiasMode = enable ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased;

        // 涓€鏉?45掳 鏂滅嚎锛氭姉閿娇鍦ㄦ枩杈逛笂鏈€鏄庢樉銆?        Doc.Clear();
        Doc.InvalidateAll();
        var s = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
            Width = 8f * DpiScale,
        };
        float x = VirtualScreen.MinX + 500, y = VirtualScreen.MinY + 400;
        for (int i = 0; i <= 200; i++) s.AddPoint(x + i * 3f, y + i * 3f, 0.5f, i);

        Doc.AddStroke(s);
        SettleFrames(500);

        // 鏁?鐏?鍍忕礌锛氶粦鏄瑪鐢诲唴閮紝鐧芥槸鑳屾櫙锛屼粙浜庝袱鑰呬箣闂寸殑鏄竟缂樼殑鎶楅敮榻胯繃娓°€?        int gray = ScreenProbe.CountNear((int)x - 20, (int)y - 20,
                                         700, 700, 128, 128, 128, 110);
        int black = ScreenProbe.CountNear((int)x - 20, (int)y - 20,
                                          700, 700, 0, 0, 0, 40);
        Console.WriteLine($"  鎶楅敮榻?{(enable ? "寮€" : "鍏?)}锛氳竟缂樿繃娓″儚绱?{gray}锛屽疄蹇冨儚绱?{black}");

        ctx.AntialiasMode = before;
    }
    private void ScrollBench()
    {
        Console.WriteLine();
        Console.WriteLine("=== 婊氬姩浠ｄ环锛堢湡瀹炴粴杞矾寰勶紝涓€鏍?= 144 鐗╃悊鍍忕礌锛?==");
        Console.WriteLine("  鍐呭閾哄紑 8 灞忥紙鈮堜竴鑺傛澘涔︾殑閲忕骇锛夛紝浠庨《閮ㄥ線涓嬫粴 12 鏍?鈮?涓€灞?);
        Console.WriteLine("    鏂囨。绗旀暟 | 姣忔牸骞冲潎 | 鏈€鎱竴鏍?| 涓€鏍煎厜鏍?| 鏈€鎱竴鏍煎厜鏍?| 鏂板厜鏍呭潡 | 鍒ゆ柇");
        Console.WriteLine("  -----------|----------|----------|----------|--------------|----------|------");

        // 鍜?WheelTest 鐢ㄥ悓涓€濂楁瀯閫犳柟寮忥細delta 鏀惧湪 wParam 鐨勯珮 16 浣嶃€?        static IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        const int steps = 12;

        foreach (int n in new[] { 1000, 5000, 10000 })
        {
            Doc.Clear();
            Doc.ClearHistory();
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
            GenerateStrokesSpread(n, 8);
            SettleFrames(600);                       // 鍏堟妸棣栧睆鐢诲嚭鏉?
            double total = 0, worst = 0, raster = 0, worstRaster = 0;
            int newTiles = 0;
            for (int k = 0; k < steps; k++)
            {
                HandleWheel(Wheel(-120));            // 寰€涓嬫粴涓€鏍?                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();                         // 鍙覆鏌撹繖涓€鏍?
                var w0 = _windows[0];
                total += w0.LastRecordMs;
                raster += w0.LastRebuildMs;
                if (w0.LastRecordMs > worst) worst = w0.LastRecordMs;
                if (w0.LastRebuildMs > worstRaster) worstRaster = w0.LastRebuildMs;
                newTiles += w0.LastPatchCount;
            }

            string note = worst < 16.7 ? "涓嶆帀甯? : worst < 33 ? "鍋舵帀涓€甯? : "浼氬崱";
            Console.WriteLine($"  {n,9} | {total / steps,6:F2} ms | {worst,6:F2} ms | {raster / steps,6:F2} ms | {worstRaster,10:F2} ms | {newTiles,8} | {note}");
        }

        Console.WriteLine("  娉細鑰佸仛娉曪紙鏁村眰閲嶇敾锛夌殑瀵圭収鍊硷細涓€灞忛噺绾?20ms锛?000 绗?41ms锛?0000 绗?82ms銆?);
        Console.WriteLine("      鐜板湪婊氫竴鏍肩殑浠ｄ环鍙拰銆屾柊闇插嚭鏉ョ殑閭ｄ竴灏忔潯銆嶆湁鍏筹紝涓庢枃妗ｆ€婚噺鏃犲叧銆?);
    }

    /// <summary>
    /// 鎶婃粴鍔ㄦ潯鎽嗗嚭鏉ョ粰浜虹湅锛氬啓婊′笁灞忓唴瀹癸紝婊氬埌涓棿锛岃婊氬姩鏉″鍦?鍒氭粴鍔ㄨ繃"鐨?    /// 闇查潰鐘舵€侊紝鐒跺悗鎸傜潃涓嶅姩鐢卞閮ㄦ埅鍥俱€?    /// </summary>
    private void ScrollShowcase()
    {
        // 婊氬姩鏉″湪 2026-10-09 宸插仠鐢紙瑙?Engine.ScrollBarEnabled锛夛紱杩欎釜瀵圭収妯″紡
        // 涓存椂鎶婂畠鎵撳紑鈥斺€斿嚭鍥剧湅鐨勬槸"褰撳勾闀夸粈涔堟牱"锛屼笉鏄骇鍝佽涓恒€?        ScrollBarEnabled = true;
        Console.WriteLine("锛堟敞锛氭粴鍔ㄦ潯宸插湪浜у搧閲屽仠鐢紱--scrollshow 涓哄鐓у嚭鍥句复鏃舵墦寮€锛?);
        Doc.Clear();
        Doc.ClearHistory();

        // 鍐欐弧涓夊睆锛氭瘡灞忓嚑琛岋紝璺ㄥ害瓒呰繃涓€灞忥紝婊氬姩鏉℃墠鏈夋瘮渚嬪彲绠?        for (int screen = 0; screen < 3; screen++)
        {
            for (int row = 0; row < 6; row++)
            {
                float y = _virtualY + screen * _virtualH + 250 + row * 240;
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f,
                };
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f;
                    s.AddPoint(_virtualX + 300 + t * 1200, y + MathF.Sin(t * 9f) * 30f, 0.5f, i * 8);
                }
                Doc.AddStroke(s);
            }
        }

        // 婊氬埌涓棿锛屽苟璁╂粴鍔ㄦ潯澶勪簬"鍒氭粴鍔ㄨ繃"鐨勭姸鎬侊紙鐪熷疄婊氬姩浼氳嚜宸辫杩欎釜鏃跺埢锛?        ViewOffsetY = -_virtualH * 1.0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
        ScrollBarActiveAtMs = NowMs;              // 鐩稿綋浜庡垰鍒氭粴杩?        Doc.InvalidateAll();
        SettleFrames(700);
        Console.WriteLine("婊氬姩鏉″凡鎽嗗ソ锛堝垰婊氬姩杩囩殑鐘舵€侊級锛岀瓑澶栭儴鎴浘");
    }

    private void PptShow(string path)
    {
        if (path == null)
        {
            Console.WriteLine("  鐢ㄦ硶锛?-pptshow <鍥?bmp> [--panel] [--menu] [--ink] [--wide] [--pass]");
            _quit = true;
            return;
        }

        // 棰滆壊涓庢姇褰辨槸鐣岄潰鎺ㄧ粰寮曟搸鐨勶紙瑙?IUiHost.SetFloatingTheme锛夆€斺€?        // 涓嶆寕鐣岄潰鎷垮埌鐨勬槸 UiTheme.Default 閭ｅ鍏滃簳锛屽嚭鐨勫浘灏变笉鏄骇鍝侀噷鐨勬牱瀛愩€?        SetUiFactory(() => new InkUi.FullUi());

        var fake = new PptFakeSource { Showing = true, Slide = 3, SlideId = 258, Total = 12, Key = "鎽嗘牱" };
        AttachPptSource(fake, watch: false);
        var argv = Environment.GetCommandLineArgs();

        ResetPptBarPosForTest();            // 浠庨粯璁や綅缃紙宸︿笅瑙掞級璧凤紝鍑虹殑鍥炬墠鏄?绗竴鐪?鐨勬牱瀛?        StepPpt();
        SettleFrames(150);
        // 寮曞閭ｄ竴琛屽瓧**姣忔杩涙斁鏄犻兘浼氬嚭鐜?*锛?026-09-27 璧凤級锛岃€屽畠鍜岃彍鍗曢暱鍦ㄥ悓涓€澶勩€?        // 浼氫簰鐩稿帇鐫€鐪嬩笉娓呪€斺€旀墍浠ラ櫎浜嗕笓闂ㄥ嚭瀹冪殑 `--hint`锛屽叾浣欏嚭鍥句竴寰嬫妸瀹冩寜涓嬪幓銆?        // 鈿?蹇呴』鍘嬪湪 `StepPpt()` **涔嬪悗**锛氳繘鏀炬槧閭ｄ竴鍒诲畠鎵嶈鐐硅捣鏉ワ紝鍘嬫棭浜嗘病鐢ㄣ€?        if (!argv.Contains("--hint")) PptHintSuppressForTest();

        // `--ink`锛氱粰褰撳墠椤甸摵涓夌瑪锛堜竴闀夸竴鐭竴鐐癸級鈥斺€斻€屽洖鏀炬湰椤靛ⅷ杩广€嶉偅涓€椤?        // 浜笉浜€佸彸渚?N 绗?瀵逛笉瀵癸紝鍑哄浘鏃朵竴鐪煎氨鑳界湅鍒般€?        if (argv.Contains("--ink"))
        {
            var s1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s1.AddPoint(300f + 320f * k / 4f, 300f + 40f * k, 0.3f + 0.1f * k, k * 200f);
            Doc.AddStroke(s1);
            var s2 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.13f, 0.45f, 0.90f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s2.AddPoint(320f + 280f * k / 4f, 560f - 30f * k, 0.4f + 0.1f * k, 1200f + k * 180f);
            Doc.AddStroke(s2);
            var dot = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.15f, 0.65f, 0.35f, 1f), Width = 8f,
            };
            dot.AddPoint(780f, 420f, 0.8f, 2200f);
            Doc.AddStroke(dot);
            SettleFrames(120);
        }

        // `--pass`锛氳瘖鏂敤鈥斺€旀墦寮€绌块€忥紝璁?鎺ヨ緭鍏ュ皬绐?閾轰笂锛堜唬鐮佹敞閲婅瀹冩槸 1/255 鐨勭伆銆?        // 鑲夌溂鐪嬩笉瑙侊紱杩欎釜寮€鍏冲氨鏄嬁鏉ユ牳瀵?鍒板簳鐪嬩笉鐪嬪緱瑙?鐨勶級銆?        if (argv.Contains("--pass"))
        {
            SetPassThroughFromUi(true);
            SettleFrames(150);
        }

        var bar = PptBarRect();
        float midX = (bar.MinX + bar.MaxX) * 0.5f;
        float midY = (bar.MinY + bar.MaxY) * 0.5f;

        if (argv.Contains("--menu"))
        {
            PptBarPointerDown(midX, midY);      // 2026-10-02 绗簲杞細鐐归〉鐮?= 鑿滃崟
            PptBarPointerUp(midX, midY);
        }
        else if (argv.Contains("--panel"))
        {
            PptBarPointerDown(midX, midY);      // 鐐归〉鐮?鈫?鑿滃崟
            PptBarPointerUp(midX, midY);
            PptMenuItemRectAt(0, out var miJump);
            PptBarPointerDown((miJump.MinX + miJump.MaxX) * 0.5f,   // 鑿滃崟绗竴椤?鈫?椤靛彿闈㈡澘
                              (miJump.MinY + miJump.MaxY) * 0.5f);
        }
        else
        {
            // 鎮仠鍦?*椤电爜鏍?*涓婏細瀹冩墠鏄潯涓婄殑涓昏锛堢偣瀹冨嚭鑿滃崟锛夛紝椤轰究鎶婃偓鍋滈珮浜嚭杩涘浘閲?            // 鈥斺€旂敤鎴?2026-09-26 鎸囧嚭杩?椤电爜閭ｉ噷娌℃湁鎮仠鎸囩ず"锛岃繖鏉″浘灏辨槸缁欓偅浠朵簨鐪嬬殑銆?            PptBarPointerMove(midX, midY);
        }
        SettleFrames(200);

        // 鎴浘鑼冨洿 = 杩欎竴鍒昏鎷嶇殑涓滆タ锛堟潯 鈭?闈㈡澘 鈭?鑿滃崟 鈭?棣栨寮曞锛?        var box = PptBarRect();
        if (PptPagePanelOpen) { PptPanelRect(out var p); box.Add(p.MinX, p.MinY); box.Add(p.MaxX, p.MaxY); }
        if (PptMenuOpen || PptHintVisible) { PptMenuRect(out var m); box.Add(m.MinX, m.MinY); box.Add(m.MaxX, m.MaxY); }
        float pad = argv.Contains("--wide") ? 160f : 24f;   // --wide锛氬嚭鍥惧甫涓婁竴鍦堝懆鍥达紝鏌?鏉″闈㈡湁浠€涔?
        int sx = (int)Math.Max(_virtualX, box.MinX - pad);
        int sy = (int)Math.Max(_virtualY, box.MinY - pad);
        int sw = (int)Math.Min(_virtualX + _virtualW - sx, box.MaxX - box.MinX + pad * 2);
        int sh = (int)Math.Min(_virtualY + _virtualH - sy, box.MaxY - box.MinY + pad * 2);
        bool ok = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
        Console.WriteLine(ok ? $"  宸蹭繚瀛?{path}锛坽sw}脳{sh}锛? : "  鎴睆澶辫触");
        _quit = true;
    }

    /// <summary>
    /// `--pptprobe [绉抅`锛?*鐪熸満杩炴帴鎺㈤拡**锛堢敤鎴峰湪鑷繁鐨勬満鍣ㄤ笂璺戜竴娆″氨鐭ラ亾閫氫笉閫氾級銆?    ///
    /// 涓轰粈涔堝彧鑳借繖鏍烽獙锛氭垜杩欒竟娌℃湁 PowerPoint / WPS 鐜锛屼骇鍝侀偅鏉?COM 杩炴帴
    /// 娌℃硶鍦ㄨ嚜妫€閲岃窇锛堣嚜妫€涔熶笉璇ュ幓纰板畠鈥斺€擿StartPptLink` 鍦ㄨ嚜妫€妯″紡涓嬬洿鎺ヨ繑鍥烇級銆?    /// 杩欐潯鍛戒护姣忕闂竴娆?鐜板湪杩炰笂璋佷簡銆佸湪涓嶅湪鏀炬槧銆佺鍑犻〉"锛屾妸绛旀鎵撳嚭鏉ャ€?    ///
    /// 鐢ㄦ硶锛堣€佸笀鐨勬満鍣ㄤ笂锛夛細
    ///   鈶?璺?`InkTeach.exe --pptprobe 20`锛?    ///   鈶?瓒佽繖 20 绉掓墦寮€涓€涓?PPT锛屾寜 F5 鏀炬槧銆佺炕涓ら〉锛圵PS 婕旂ず涔熶竴鏍疯瘯涓€娆★級锛?    ///   鈶?鐪嬭緭鍑猴細鏀炬槧涓簲褰撲粠 False 鍙?True銆侀〉鐮佽窡鐫€缈婚〉鍙樸€丼lideID 鏄釜澶ф暣鏁般€?    /// 涓夋潯閮藉 = 杩炴帴灞傚湪杩欏彴鏈哄櫒涓婃病闂锛圵PS 涔熺収杩欐潯璧帮紝瀹冨氨鏄粰鍥戒骇鍔炲叕濂椾欢閾虹殑璺級銆?    /// </summary>
    private void PptProbe(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== PPT 杩炴帴鎺㈤拡 ===");
        Console.WriteLine($"  鎺ヤ笅鏉?{seconds:F0} 绉掗噷锛岃鍘绘墦寮€涓€涓?PPT 骞舵寜 F5 鏀炬槧銆佺炕涓ら〉锛?);
        Console.WriteLine("  WPS 婕旂ず涔熶竴鏍疯瘯涓€娆★紙杩欐潯鎺㈤拡灏辨槸鐢ㄦ潵鍒嗘竻鍝鐨?Office 鑳借繛涓婄殑锛夈€?);
        Console.WriteLine();

        // `--pptcmd`锛氳繛鎺ヤ笂涔嬪悗鐪熺殑鎸変竴涓?Next / Prev / GotoSlide锛岀湅椤电爜璺熶笉璺熺潃鍔?        //锛堣繖鏉¤矾寰勮蛋鐨勬槸 IDispatch::Invoke锛屽拰鍙灞炴€т笉鏄悓涓€鏉¤皟鐢級銆?        bool cmdTest = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--pptcmd");
        bool cmdDone = !cmdTest;

        var src = new PptComSource();
        int connectedSamples = 0, showingSamples = 0;
        double t0 = NowMs;
        string lastLine = "";
        while (NowMs - t0 < seconds * 1000)
        {
            var s = src.Poll();
            if (s.Connected) connectedSamples++;
            if (s.Showing) showingSamples++;
            string line = $"  杩炰笂={s.Connected}  鏀炬槧涓?{s.Showing}  绗?{s.Slide}/{s.Total} 椤? "
                        + $"SlideID={s.SlideId}  鏍囪瘑={s.Key}";
            if (line != lastLine) { Console.WriteLine(line); lastLine = line; }

            if (!cmdDone && s.Showing && NowMs - t0 > 1500)
            {
                // 鍛戒护鍚?PowerPoint 浼氬繖涓€涓嬶紝灞炴€у彲鑳界煭鏆傝澶辫触鈥斺€旇疆璇㈤噸璇曞埌鎭㈠涓烘銆?                PptSnapshot WaitShow(int ms)
                {
                    var end = NowMs + ms; var last = default(PptSnapshot);
                    while (NowMs < end) { last = src.Poll(); if (last.Showing) return last; SettleFrames(100); }
                    return last;
                }
                PptSnapshot WaitSlide(int want, int ms)
                {
                    var end = NowMs + ms; var last = default(PptSnapshot);
                    while (NowMs < end) { last = src.Poll(); if (last.Showing && last.Slide == want) return last; SettleFrames(100); }
                    return last;
                }

                int before = s.Slide;

                // 鈶?GotoSlide锛氬姩鐢婚〉涔熶笉褰卞搷锛岀珛鍒昏兘楠屻€?                src.GotoSlide(before + 1);
                var sG1 = WaitSlide(before + 1, 2500);
                src.GotoSlide(before);
                var sG0 = WaitSlide(before, 2500);
                bool okGoto = sG1.Showing && sG1.Slide == before + 1 && sG0.Showing && sG0.Slide == before;

                // 鈶?Next锛?*鍔ㄧ敾椤典笂瑕佹寜濂藉嚑涓嬫墠缈婚〉**锛堟瘡涓€涓嬫槸涓€涓姩鐢绘锛夛紝
                //    寰幆鍒伴〉鐮佸彉鍖栦负姝紝涓嶆槸"鎸変竴涓嬪氨蹇呴』缈?銆?                int nextPresses = 0;
                var sNext = sG0;
                while (nextPresses < 6 && (!sNext.Showing || sNext.Slide <= before))
                { src.Next(); nextPresses++; sNext = WaitShow(1200); }
                bool okNext = sNext.Showing && sNext.Slide > before;

                // 鈶?Prev锛氬悓鏍峰惊鐜洖鍒板師椤碉紱鏈€鍚?Goto 瀵归綈锛岄槻鍋滃湪鍔ㄧ敾涓棿銆?                int prevPresses = 0;
                var sPrev = sNext;
                while (prevPresses < 8 && (!sPrev.Showing || sPrev.Slide > before))
                { src.Prev(); prevPresses++; sPrev = WaitShow(1200); }
                src.GotoSlide(before); WaitShow(1500);
                bool okPrev = sPrev.Showing && sPrev.Slide <= before;

                Console.WriteLine($"  鍛戒护鑷: Goto {before}->{before + 1}->{before} {(okGoto ? "PASS" : "FAIL")}; "
                                + $"Next x{nextPresses}->{sNext.Slide} {(okNext ? "PASS" : "FAIL")}; "
                                + $"Prev x{prevPresses}->{sPrev.Slide} {(okPrev ? "PASS" : "FAIL")}");
                cmdDone = true;
            }

            SettleFrames(200);
        }

        Console.WriteLine();
        if (cmdTest && !cmdDone)
            Console.WriteLine("  鍛戒护鑷锛氭病璺戯紙鎺㈤拡鏈熼棿娌¤鍒版斁鏄犵姸鎬侊級");
        Console.WriteLine(showingSamples > 0
            ? $"  PASS锛氳鍒拌繃鏀炬槧鐘舵€侊紙{showingSamples} 娆￠噰鏍峰湪鏀炬槧涓級鈥斺€旇繛鎺ュ眰鍦ㄨ繖鍙版満鍣ㄤ笂鍙敤"
            : connectedSamples > 0
                ? $"  鍗婇€氾細杩炰笂浜嗘紨绀烘枃绋匡紙{connectedSamples} 娆￠噰鏍凤級锛屼絾娌¤鍒版斁鏄犵姸鎬佲€斺€旇纭鎸変簡 F5 鍏ㄥ睆鏀炬槧"
                : "  涓嶉€氾細涓€娆￠兘娌¤繛涓婏紙娌¤妗岄潰 Office / WPS銆佹病鏈夋墦寮€鐨勬紨绀烘枃绋匡紝鎴?COM 琚畨鍏ㄨ蒋浠舵尅浣忥級");
        _quit = true;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    /// <summary>
    /// 鏄惧瓨宸茬敤閲忥紙MB锛夈€?    ///
    /// 娉ㄦ剰锛?*鍒幓瑙ｆ瀽 GpuMb() 杩斿洖鐨勫瓧绗︿覆**鈥斺€旈偅鏄?154/7396 MB"杩欑缁欎汉鐪嬬殑
    /// 鏍煎紡锛孴ryParse 浼氬け璐ュ苟闈欓粯杩斿洖 0锛堢涓€鐗堝氨鏄繖涔堥敊鐨勶紝鏁村垪鏄剧ず 0锛夈€?    /// 瑕佹暟灏辩洿鎺ラ棶鍚屼竴涓?API 瑕佹暟銆?    ///
    /// 涓轰粈涔堣繖椤归噸瑕侊細鏍告樉鐨勬樉瀛?*鏄粠绯荤粺鍐呭瓨閲屽垎鐨?*锛屽湪 4GB 鏁欏鏈轰笂瀹冨拰
    /// 杩涚▼鍐呭瓨鎶㈢殑鏄悓涓€鍧椼€?-memory 鎶ュ憡閲屼竴涓囩瑪鏃舵樉瀛?154MB锛屾瘮鐐规暟鎹?    /// 锛?.9MB锛夊ぇ涓や釜鏁伴噺绾р€斺€旂湡姝ｇ殑澶уご寰堝彲鑳藉湪杩欓噷銆?    /// </summary>
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
    private void SelDragShowcase()
    {
        SelShowcase();                       // 鍏堟憜濂戒袱鏉＄瑪杩瑰拰閫変腑妗?
        var sel = Doc.Selected.ToArray();
        var sb = EditRegion.Of(sel);
        var c = new Vector2((sb.MinX + sb.MaxX) * 0.5f, (sb.MinY + sb.MaxY) * 0.5f);
        var m = Matrix3x2.CreateScale(1.25f, 1.25f, c) * Matrix3x2.CreateTranslation(120f, -60f);

        // 瀹屽叏鐓?UpdateSelDrag 鐨勫仛娉曪細鏍囪剰鏃т綅缃?鈫?瀹炴椂鏀瑰彉鎹?鈫?鏍囪剰鏂颁綅缃?        foreach (var s in sel)
        {
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.SetTransformLive(s, s.Transform * m);
            Doc.Dirty.Add(s.PaddedBounds);
        }

        SettleFrames(700);
        Console.WriteLine("宸茬敤瀹炴椂鍙樻崲鎸蛋锛岀瓑澶栭儴鎴浘锛堣繖涓ā寮忎笉浼氳嚜宸遍€€鍑猴級");
    }

    /// <summary>
    /// **鎴浘鍙栨櫙妗?*鐨勭灞忓嚭鍥撅細鎽嗗嚑绗旇儗鏅ⅷ 鈫?鎶婂彇鏅鎽嗗湪涓棿 鈫?鎷嶄笅鏉ャ€?    ///
    /// 鐪嬬殑鏄袱浠惰嚜妫€璇讳笉鍑烘潵鐨勪簨锛?*妗嗗拰瑙掓爣鐨勭矖缁嗗湪 2 鍊嶅睆涓婇『涓嶉『鐪?*锛?    /// 浠ュ強鎷栧姩涓偅涓?*灏哄璇绘暟鑳跺泭**璺熸鐨勮窛绂汇€佸瓧鐨勫ぇ灏忓悎涓嶅悎閫傘€?    /// </summary>
    /// <summary>
    /// **瀵煎嚭瀵硅瘽妗嗘帰閽?*锛氱湡鐨勫脊涓€娆＄郴缁?鍙﹀瓨涓?锛屽悓鏃剁敱涓€涓悗鍙扮嚎绋嬪幓鐐瑰畠鐨?    /// "淇濆瓨绫诲瀷"涓嬫媺锛屽苟鎶婃瘡涓€姝ユ埅涓嬫潵銆?    ///
    /// 涓轰粈涔堣涓撻棬鍋氬畠锛氳繖閲屽嚭杩囦袱娆￠棶棰橈紙"閫変笉鍒?jpg"銆?瀵硅瘽妗嗚鑷繁鐨勪笅鎷夌洊浣?锛夛紝
    /// 鑰?*杩欎簺闂鍙兘闈犵湡鏈?+ 鐪嬪浘鍒ゆ柇**鈥斺€旂郴缁熷璇濇鏄彟涓€涓獥鍙ｇ被锛?    /// 鎴戜滑鐨勮嚜妫€锛堝悎鎴愯緭鍏?+ 鏁板儚绱狅級纰颁笉鍒板畠鐨勫唴閮ㄦ帶浠躲€傛帰閽堟妸"鎵剧獥鍙?鈫?    /// 鐐逛笅鎷?鈫?鎴浘 鈫?鍏虫帀"杩欏鍔ㄤ綔鍥哄畾涓嬫潵锛屾敼涓€娆″氨鑳藉啀楠屼竴娆°€?    ///
    /// 鐢ㄦ硶锛歚--dialogprobe [鍓嶇紑]`锛屼骇鍑?`<鍓嶇紑>-1-鎵撳紑鏃?bmp`銆乣-2-鐐逛笅鎷夊悗.bmp`銆?    /// </summary>
    private void DialogProbe(string prefix)
    {
        BoardOn = true;
        Tool = Tool.Marquee;
        Doc.Clear();
        Doc.ClearHistory();
        // 鍑犳潯妯嚎閾哄湪灞忓箷涓棿锛氬璇濇浼氬帇鍦ㄤ笂闈紝姝ｅソ鐪?妗嗗拰澧ㄨ皝鍦ㄤ笂闈?
        for (int i = 0; i < 5; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.85f, 0.15f, 0.15f, 1f), Width = 8f * DpiScale,
            };
            float y = _virtualY + _virtualH * 0.35f + i * 90f * DpiScale;
            s.AddPoint(_virtualX + _virtualW * 0.15f, y, 1f, i * 4);
            s.AddPoint(_virtualX + _virtualW * 0.85f, y, 1f, i * 4 + 2);
            Doc.AddStroke(s);
        }
        Doc.SelectOnly(Doc.Strokes.ToArray());
        SettleFrames(700);

        var t = new Thread(() => DialogProbeThread(prefix)) { IsBackground = true };
        t.Start();

        ExportDialogEnabled = true;
        ExportSelection(1);                 // 闃诲鍦ㄧ郴缁熷璇濇閲岋紝鍚庡彴绾跨▼鍦ㄦ搷浣滃畠
        Console.WriteLine("dialogprobe锛氬璇濇宸插叧闂紙璧扮殑鏄彇娑堥偅鏉¤矾锛?);

        // 璧颁簡 `--save` 鐨勮瘽锛岃繖閲岄獙"鐪熺殑鍐欏嚭鏉ヤ簡"锛堝啓瀹屽氨鍒狅紝涓嶇暀鍨冨溇锛?        if (DialogProbeCheckExport && LastExportPath != null)
        {
            var fi = new FileInfo(LastExportPath);
            Console.WriteLine(fi.Exists && fi.Length > 0
                ? $"dialogprobe锛?*鏂囦欢鐪熺殑鍐欏嚭鏉ヤ簡** {LastExportPath}锛坽fi.Length} 瀛楄妭锛?
                  + $"绫诲瀷绗?{LastExportFilterIndex} 鏉★級鈥斺€斿璇濇閭ｆ潯閾炬槸閫氱殑"
                : $"dialogprobe锛?*娌″啓鍑烘潵** {LastExportPath}");
            try { fi.Delete(); } catch { }
            Console.WriteLine("dialogprobe锛氭祴璇曟枃浠跺凡鍒犳帀");
            DialogProbeCheckExport = false;
        }
        _quit = true;
    }

    private static bool DialogProbeCheckExport;

    private static IntPtr ProbeFindDialog()
    {
        IntPtr found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        var sb = new System.Text.StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != me) return true;
            Native.GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != "#32770") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string ProbeClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        Native.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private void DialogProbeThread(string prefix)
    {
        IntPtr dlg = IntPtr.Zero;
        for (int i = 0; i < 120 && dlg == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            dlg = ProbeFindDialog();
        }
        if (dlg == IntPtr.Zero) { Console.WriteLine("dialogprobe锛? 绉掑唴娌＄瓑鍒板璇濇"); return; }

        Native.GetWindowRect(dlg, out var r);
        Console.WriteLine($"dialogprobe锛氬璇濇 {dlg} 鍦?({r.Left},{r.Top})锛寋r.Width}脳{r.Height}");

        // 绛?1.2 绉掞細璁╃湅闂ㄧ嚎绋嬫妸瀹冮《鍒版渶鍓嶃€佷篃璁╁璇濇鎶婅嚜宸辩殑瀛愭帶浠跺缓瀹?        // 锛堢涓€娆℃帰閽堝湪"鍒氭壘鍒?灏辨煡瀛愮獥鍙ｏ紝涓€涓?ComboBox 閮芥病鏌ュ埌锛夈€?        Thread.Sleep(1200);
        Native.GetWindowRect(dlg, out r);
        ShotRegion(prefix + "-0-鍏ㄥ睆.bmp", 0, 0, 2880, 1800);
        ShotRegion(prefix + "-1-鎵撳紑鏃?bmp", r.Left - 30, r.Top - 30, r.Width + 60, r.Height + 60);

        // 鎴戜滑瑕嗙洊灞傝繖浼氬効鍦ㄥ璇濇涓婇潰杩樻槸涓嬮潰锛熺湅涓や釜鐐逛笂鐨勬渶涓婂眰绐楀彛鏄皝銆?        int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
        IntPtr atMid = Native.WindowFromPoint(new Native.POINT { X = midX, Y = midY });
        Console.WriteLine($"dialogprobe锛氬璇濇姝ｄ腑閭ｄ竴鐐规渶涓婂眰 = {atMid}锛坽ProbeClassName(atMid)}锛?
                        + $"锛涙垜浠殑瑕嗙洊灞?= {_windows[0].Hwnd}锛坽ProbeClassName(_windows[0].Hwnd)}锛?);

        // 瀛愭帶浠舵竻鍗曪細鐪嬪璇濇閲屽埌搴曟湁浠€涔堬紙绗竴娆℃病鎵惧埌 ComboBox锛?        int n = 0;
        Native.EnumChildWindows(dlg, (h, _) =>
        {
            if (n++ < 40)
            {
                Native.GetWindowRect(h, out var cr0);
                Console.WriteLine($"    瀛愮獥鍙?{ProbeClassName(h),-20} ({cr0.Left},{cr0.Top})-({cr0.Right},{cr0.Bottom})");
            }
            return true;
        }, IntPtr.Zero);

        // "淇濆瓨绫诲瀷"閭ｄ釜涓嬫媺锛氬畠鍜?鏂囦欢鍚?鏄悓涓€绫绘帶浠讹紙閮芥槸 ComboBox锛夛紝
        // **"淇濆瓨绫诲瀷"鏄笅闈㈤偅涓?*锛堟枃浠跺悕鍦ㄤ笂锛夈€傜涓€鐗堝彇浜嗙涓€涓?鈫?鐐瑰埌浜嗘枃浠跺悕妗嗐€?        // 鎵€浠ヨ繖閲屽彇**浣嶇疆鏈€闈犱笅鐨勯偅涓€涓?*銆?        IntPtr combo = IntPtr.Zero;
        string comboClass = "";
        int bestTop = int.MinValue;
        Native.EnumChildWindows(dlg, (h, _) =>
        {
            string cls = ProbeClassName(h);
            if (cls != "ComboBox" && cls != "ComboBoxEx32") return true;
            Native.GetWindowRect(h, out var cr0);
            if (cr0.Top <= bestTop) return true;
            bestTop = cr0.Top; combo = h; comboClass = cls;
            return true;
        }, IntPtr.Zero);

        if (combo == IntPtr.Zero)
        {
            Console.WriteLine("dialogprobe锛氭病鎵惧埌鏂囦欢绫诲瀷涓嬫媺锛堝瓙绐楀彛閲屾病鏈?ComboBox锛?);
        }
        else
        {
            Native.GetWindowRect(combo, out var cr);
            // 瀛愮獥鍙ｇ殑鐭╁舰**澶у鏁版椂鍊欐槸灞忓箷鍧愭爣**锛堝璇濇绉诲埌 (779,330) 涔嬪悗锛?            // 瀹冧篃璺熺潃鎶?(1042,1088)锛夈€備絾瀵硅瘽妗嗚繕鍦?(0,0) 鏃朵袱绉嶈娉曢暱寰椾竴妯′竴鏍凤紝
            // 鍒嗕笉娓呪€斺€旀墍浠ヨ繖閲屾寜"鍦ㄤ笉鍦ㄥ璇濇鐭╁舰閲?鍒や竴涓嬶紝涓嶅湪灏辫ˉ涓婂璇濇鍘熺偣銆?            int ox = 0, oy = 0;
            bool insideDlg = cr.Left >= r.Left && cr.Top >= r.Top
                          && cr.Right <= r.Right && cr.Bottom <= r.Bottom;
            if (!insideDlg) { ox = r.Left; oy = r.Top; }
            int cx = ox + cr.Right - 14, cy = oy + (cr.Top + cr.Bottom) / 2;
            Console.WriteLine($"dialogprobe锛氫笅鎷?{comboClass} 璇诲埌 ({cr.Left},{cr.Top})-({cr.Right},{cr.Bottom})锛?
                            + $"瀵硅瘽妗?({r.Left},{r.Top})-({r.Right},{r.Bottom})锛?
                            + $"{(insideDlg ? "鎸夊睆骞曞潗鏍? : "鎸夌浉瀵瑰潗鏍囷紜鍘熺偣")} 鈫?鐐?({cx},{cy})");

            // **鍏堢湅涓€鐪硷細杩欎釜鐐逛笂鏄皝鐨勭獥鍙?*锛堟垜浠鐩栧眰鏄疆椤剁殑锛屽緢鍙兘鎶㈣蛋杩欎竴涓嬶級
            IntPtr over = Native.WindowFromPoint(new Native.POINT { X = cx, Y = cy });
            Console.WriteLine($"dialogprobe锛氶偅涓€鐐逛笂鏈€涓婂眰鐨勭獥鍙?= {over}锛坽ProbeClassName(over)}锛?
                            + $"锛屾垜浠殑瑕嗙洊灞?= {_windows[0].Hwnd}");

            SendMouse(cx, cy, 0);
            Thread.Sleep(60);
            SendMouse(cx, cy, Native.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(50);
            SendMouse(cx, cy, Native.MOUSEEVENTF_LEFTUP);
            // **杩炵潃鎷嶄笁寮?*锛氬垽"鐐瑰紑灏辨敹鍥炲幓"杩欑姣涚梾锛屽緱鐪嬪畠鏄病寮€銆佽繕鏄紑浜嗕竴涓嬪張琚叧鎺夈€?            int sx = ox + cr.Left - 30, sy = oy + cr.Bottom - 40;
            int sw = Math.Max(cr.Width + 60, 400), sh = 460;
            Thread.Sleep(150);
            ShotRegion(prefix + "-2a-鐐瑰悗150ms.bmp", sx, sy, sw, sh);
            Thread.Sleep(250);
            ShotRegion(prefix + "-2b-鐐瑰悗400ms.bmp", sx, sy, sw, sh);
            Thread.Sleep(500);
            ShotRegion(prefix + "-2c-鐐瑰悗900ms.bmp", sx, sy, sw, sh);

            // 涓嬫媺閲?*绗簩椤瑰氨鏄?JPEG**锛圥NG 鍦ㄤ笂銆丣PEG 鍦ㄤ笅锛屾瘡椤圭害 30 鐗╃悊鍍忕礌锛夈€?            // 鐐瑰畠涓€涓嬶紝鐪嬫枃浠跺悕鍚庣紑浼氫笉浼氳窡鐫€鍙樻垚 .jpg鈥斺€旇繖灏辨槸鐢ㄦ埛瑕佺殑閭ｆ潯璺€?            int jx = ox + cr.Left + 200, jy = oy + cr.Bottom + 45;
            SendMouse(jx, jy, 0);
            Thread.Sleep(60);
            SendMouse(jx, jy, Native.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(50);
            SendMouse(jx, jy, Native.MOUSEEVENTF_LEFTUP);
            Thread.Sleep(500);
            ShotRegion(prefix + "-3-閫変簡JPG.bmp", sx, sy, sw, sh);

            // `--save`锛氫竴璺蛋鍒板簳鈥斺€旀妸鏂囦欢鍚嶆敼鎴愪复鏃惰矾寰勶紝鐐?淇濆瓨"锛?            // 鐪嬫垜浠殑浠ｇ爜鏄笉鏄湡鐨勬妸鍥惧啓鍑烘潵浜嗭紙鍐欏畬鐢变富绾跨▼楠岃瘉骞跺垹鎺夛級銆?            if (Environment.GetCommandLineArgs().Contains("--save"))
            {
                DialogProbeCheckExport = true;
                IntPtr saveBtn = IntPtr.Zero;
                int leftMost = int.MaxValue;
                Native.EnumChildWindows(dlg, (h, _) =>
                {
                    string cls = ProbeClassName(h);
                    Native.GetWindowRect(h, out var hr);
                    if (cls == "Button" && hr.Top > r.Top + r.Height * 3 / 4 && hr.Left < leftMost)
                    {
                        leftMost = hr.Left;                                 // 涓嬪崐閮ㄥ垎鏈€宸﹁竟閭ｄ釜 = 淇濆瓨
                        saveBtn = h;
                    }
                    return true;
                }, IntPtr.Zero);

                if (saveBtn != IntPtr.Zero)
                {
                    Native.GetWindowRect(saveBtn, out var br);
                    int bx = (br.Left + br.Right) / 2, by = (br.Top + br.Bottom) / 2;
                    Console.WriteLine($"dialogprobe锛氱偣銆屼繚瀛樸€?{bx},{by})");
                    SendMouse(bx, by, 0);
                    Thread.Sleep(60);
                    SendMouse(bx, by, Native.MOUSEEVENTF_LEFTDOWN);
                    Thread.Sleep(50);
                    SendMouse(bx, by, Native.MOUSEEVENTF_LEFTUP);
                    return;                       // 璁╁畠鑷繁鍘讳繚瀛樸€佸叧妗嗭紝鍒啀琛?WM_CLOSE
                }
            }
        }

        Thread.Sleep(300);
        Native.PostMessage(dlg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ShotRegion(string path, int x, int y, int w, int h)
    {
        bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
        Console.WriteLine(ok ? $"  鍑哄浘 {System.IO.Path.GetFullPath(path)}锛圕WD = {Environment.CurrentDirectory}锛?
                             : $"  鍑哄浘澶辫触 {path}");
    }

    private void CaptureShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        BoardOn = true;                     // 鐧藉簳锛屼笉鐒舵鍘嬪湪妗岄潰涓婄湅涓嶆竻
        Doc.Clear();
        Doc.ClearHistory();

        float cx = VirtualScreen.MinX + 700, top = VirtualScreen.MinY + 300;
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 5f * DpiScale,
        };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            s.AddPoint(cx - 300 + t * 620, top + 120 + MathF.Sin(t * 6f) * 60f, 0.5f, i * 8);
        }
        Doc.AddStroke(s);

        // 鍙栨櫙鍖猴細**鏁呮剰鍙栦竴鍧椾笉鏄暣鏁扮殑灏哄**锛?20脳300 閫昏緫鍍忕礌锛夛紝璇绘暟鎵嶆湁鐪嬪ご
        CaptureActive = true;
        CapMinX = cx - 260; CapMinY = top;
        CapMaxX = cx + 260; CapMaxY = top + 300 * DpiScale;
        // `--adjust`锛氬嚭"鏉惧紑鍚庤皟鏁?閭ｄ竴鐗堬紙8.3.0锛? 涓墜鏌?+ 鉁?鉁?涓ら鎸夐挳锛?        CaptureAdjusting = Environment.GetCommandLineArgs().Contains("--adjust");
        // `--ready`锛氬嚭"鍒氳繘灞?閭ｄ竴鐗堬紙8.3.1锛氭暣灞忕伆 + 椤堕儴鎻愮ず + 鍙充笂瑙掋€屸湑 鍙栨秷銆嶏紝杩樻病鏈夋锛?        bool ready = Environment.GetCommandLineArgs().Contains("--ready");
        if (ready) CapMinX = CapMaxX = CapMinY = CapMaxY = cx;

        // 鍑哄浘鑼冨洿 = 妗?+ 鍥涘懆涓€鍦堬紙閬僵/鍑嗙嚎/璇绘暟/鎸夐挳閮藉彲鑳芥湁锛夛細鏁村潡瑙嗗彛澶ぇ锛?        // 缁?妗?+ 120 閫昏緫鍍忕礌"灏卞锛堢湅鍥剧湅鐨勬槸閭ｅ嚑涓帶浠剁殑鎺掔増锛夈€?        var r = new RectF { MinX = CapMinX, MinY = CapMinY, MaxX = CapMaxX, MaxY = CapMaxY };
        // 鈿?`--ready` 閭ｄ袱浠朵笢瑗匡紙椤堕儴鎻愮ず銆佸彸涓婅銆屸湑銆嶏級閮借创鍦?*灞忓箷涓婃部**锛?        //   "鐐?+ 120"鏍规湰鎷嶄笉鍒帮紙2026-10-03 淇細浠ュ墠鍙嚭涓€鍧?240脳240 鐨勭伆锛夈€?        //   鏀规垚**灞忓箷涓婃部涓€鏉￠€氭爮**鈥斺€斾袱浠堕兘鍦ㄩ噷闈€?        if (ready)
            r = new RectF
            {
                MinX = VirtualScreen.MinX, MinY = VirtualScreen.MinY,
                MaxX = VirtualScreen.MaxX, MaxY = VirtualScreen.MinY + 360f * DpiScale,
            };
        if (!OffscreenFloatingShot(path, ready ? r : r.Inflate(120f * DpiScale))) Console.WriteLine("鍑哄浘澶辫触");
        CaptureActive = false;
        CaptureAdjusting = false;
        _quit = true;
    }

    /// <summary>
    /// `--demogif &lt;鐩綍&gt;`锛氬綍涓€娈?*婕旂ず杩炴媿**锛堝啓瀛?鈫?鍋滈】鍙樺浘褰?鈫?閫変腑鎷栧姩 鈫?鎴浘鍙栨櫙锛夈€?    ///
    /// 涓轰粈涔堣鏈夊畠锛歊EADME 澶撮儴閭ｅ紶鍔ㄥ浘娌℃硶"绂诲睆鎽嗘媿"鈥斺€斿畠瑕佺殑姝ｆ槸**鐪熷疄浜や簰杩囩▼**
    /// 锛堝悎鎴愰紶鏍囪蛋鐪熷疄杈撳叆閫氳矾銆佸仠椤垮彉褰㈣蛋鐪熷疄瀹氭椂鍣ㄣ€佹埅鍥惧彇鏅蛋鐪熷喕缁擄級銆傛墍浠ヨ繖閲?    /// 鍜岃嚜妫€鍚屼竴濂楄矾锛氱櫧鏉挎墦搴曪紙涓嶉€忔槑锛岀敾闈㈤噷娌℃湁妗岄潰鏉傜墿锛? 鍚堟垚杈撳叆 + 绾?110ms 鎴竴甯с€?    /// 甯у瓨鎴?`&lt;鐩綍&gt;\0000.bmp鈥锛屼箣鍚庣敤 `tools\make-demo-gif.ps1` 鎷兼垚 GIF銆?    /// 鍙湪寮€鍙戞満涓婅窇锛屼笉杩涗骇鍝併€佷篃涓嶈繘榛樿鑷濂椾欢銆?    /// </summary>
    private void DemoGif(string dir)
    {
        SetUiFactory(() => new InkUi.FullUi());
        Tool = Tool.Pen;
        BoardOn = true;
        Host.Commands.SetBoard(true);              // 寮曟搸渚х湡寮€鏉匡細鎴睆閲屾槸骞插噣鐧藉簳
        SettleFrames(700);

        Directory.CreateDirectory(dir);
        int frame = 0;
        // 鎴睆鑼冨洿锛堢墿鐞嗗儚绱狅級锛氬姩浣滃尯 + 搴曢儴宸ュ叿甯︼紙1600脳1420锛夈€?        int fx = _virtualX + 850, fy = _virtualY + 380, fw = 1600, fh = 1420;
        void Snap() => ScreenProbe.SaveBmp(
            Path.Combine(dir, frame++.ToString("0000") + ".bmp"), fx, fy, fw, fh);

        // 涓€杈规寜 `t鈭圼0,1]` 鎺ㄨ繘鍔ㄤ綔锛屼竴杈圭害 120ms 鎴竴甯э紱缁撴潫鍐嶈ˉ涓€寮犮€?        void Animate(int ms, Action<float> at)
        {
            var sw = Stopwatch.StartNew();
            // 鈿?鍒啓 `long.MinValue`锛歚el - last` 浼氭孩鍑烘垚璐熸暟锛屾潯浠舵案杩滀笉鎴愮珛
            //   锛堢涓€鐗堝氨鏄繖涔堝彧鎷嶄簡 10 寮犫€斺€旀瘡涓樁娈电粨鏉熶竴寮狅紝涓棿涓€寮犳病鏈夛級銆?            long last = -10000;
            for (;;)
            {
                long el = sw.ElapsedMilliseconds;
                at?.Invoke(ms <= 0 ? 1f : Math.Min(1f, el / (float)ms));
                PumpMessages();
                StepCameraAnim();
                RenderAll();
                if (el - last >= 120) { Snap(); last = el; }
                if (el >= ms) break;
                Thread.Sleep(10);
            }
            Snap();
        }

        // 鈹€鈹€ 鈶?鐢ㄧ瑪鐢讳竴涓?鎵嬬敾鍦?锛堝甫鎵嬫姈锛?.0 绉掕蛋瀹岋級鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        float cx = _virtualX + 1560, cy = _virtualY + 900, r = 250;
        const int N = 64;
        var pts = new Vector2[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float a = -MathF.PI / 2f + i / (float)N * MathF.PI * 2f;
            float rr = r * (1f + 0.045f * MathF.Sin(i * 1.9f));   // 鎵嬫姈锛氬埆鏄畬缇庡渾
            pts[i] = new Vector2(cx + rr * MathF.Cos(a), cy + rr * MathF.Sin(a));
        }
        SendMouse((int)pts[0].X, (int)pts[0].Y, 0);
        SettleFrames(150); Snap();
        SendMouse((int)pts[0].X, (int)pts[0].Y, Native.MOUSEEVENTF_LEFTDOWN);
        Animate(2800, t => SendMouse((int)pts[Math.Min(N, (int)(t * N))].X,
                                     (int)pts[Math.Min(N, (int)(t * N))].Y, 0));

        // 鈹€鈹€ 鈶?鍋滀綇涓嶅姩锛?00ms 鍚?鍋滈】鍙樺浘褰?锛堝渾锛夛紜 鏉炬墜鑷姩閫変腑 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        Animate(950, _ => SendMouse((int)pts[N].X, (int)pts[N].Y, 0));
        SendMouse((int)pts[N].X, (int)pts[N].Y, Native.MOUSEEVENTF_LEFTUP);
        Animate(900, _ => { });                    // 瀹氬瀷 + 鑷姩閫変腑锛堟敹璧锋垚涓€棰楀渾閽級

        // 鐐逛竴涓嬮偅棰楀渾閽細鎶婃搷浣滄潯鎽婂紑锛堝浘閲屾墠鏈夊畬鏁寸殑鍗佹牸锛夈€?        {
            var dot = SelectionHandles.BarCollapsedRect(
                SelectionHandles.FrameOf(Doc.Selected).CanvasAabb, DpiScale, ViewportCanvas);
            float dx = (dot.MinX + dot.MaxX) * 0.5f, dy = (dot.MinY + dot.MaxY) * 0.5f;
            SendMouse((int)dx, (int)dy, 0);
            SettleFrames(90); Snap();
            SendMouse((int)dx, (int)dy, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(90);
            SendMouse((int)dx, (int)dy, Native.MOUSEEVENTF_LEFTUP);
            Animate(650, _ => { });                // 鎽婂紑 + 鐪嬩竴鐪?        }

        // 鈹€鈹€ 鈶?鎸変綇妗嗗唴鎷栧姩锛堝湪妗嗛噷鎸変笅 = 鎷栨暣浣擄紝涓€姝ユ挙閿€閭ｇ锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(140); Snap();
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        Animate(1100, t => SendMouse((int)(cx + t * 330f), (int)(cy + t * 180f), 0));
        SendMouse((int)(cx + 330f), (int)(cy + 180f), Native.MOUSEEVENTF_LEFTUP);
        Animate(700, _ => { });

        // 鈹€鈹€ 鈶?鎴浘鍙栨櫙锛氭暣灞忓帇鏆?鈫?鎷栧嚭鍙栨櫙妗?鈫?杩涜皟鏁存€侊紙8 鎵嬫焺 + 鉁?鉁曪級鈹€鈹€鈹€鈹€鈹€
        CaptureHideInk = false;                    // 杩炴壒娉ㄤ竴璧峰喕锛氬喕鍑烘潵鐨勫簳灏辨槸鎴戜滑鐨勬澘涔?        BeginCaptureMode();
        SettleFrames(220); Snap();
        CapMinX = CapMaxX = cx - 240; CapMinY = CapMaxY = cy - 140;
        Animate(1000, t =>
        {
            CapMaxX = cx - 240 + t * 720f;
            CapMaxY = cy - 140 + t * 430f;
        });
        CaptureAdjusting = true;
        Animate(1500, _ => { });

        Console.WriteLine($"[婕旂ず杩炴媿] {frame} 甯?鈫?{dir}");
        ExitCode = 0;
        _quit = true;
    }
    private void SelShowcase(string path = null)
    {
        BoardOn = true;                 // 鐧藉簳锛屼笉鐒舵墜鏌勫帇鍦ㄦ闈笂鐪嬩笉娓?        Doc.Clear();
        Doc.ClearHistory();

        // **蹇呴』鎸備笂鐣岄潰**锛氭搷浣滄潯/灏忛潰鏉跨殑棰滆壊涓庢姇褰辨槸鐣岄潰鎺ㄧ粰寮曟搸鐨?        // 锛堣 IUiHost.SetFloatingTheme锛夆€斺€斾笉鎸傜晫闈㈡嬁鍒扮殑鏄?UiTheme.Default 閭ｅ鍏滃簳锛?        // 鍑虹殑鍥惧氨涓嶆槸浜у搧閲岀湅鍒扮殑鏍峰瓙銆?        // 娉ㄦ剰椤哄簭锛?*鍋忓ソ瑕佸厛璁?*锛孲etUiFactory 鏄珛鍒绘寕杞界殑锛堟寕杞芥椂灏变細璇诲亸濂斤級銆?        // --dark锛氭繁鑹查偅妗ｄ篃鍑轰竴寮狅紙娴眰鐨勬繁鑹叉槸杩欎竴杞柊鍔犵殑锛?        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");
        SetUiFactory(() => new InkUi.FullUi());

        float cx = VirtualScreen.MinX + 720;
        float top = VirtualScreen.MinY + 300;

        // 涓€鏉¤嚜鐢辩瑪杩?        var free = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f,
        };
        for (int i = 0; i <= 80; i++)
        {
            float t = i / 80f;
            free.AddPoint(cx - 320 + t * 640, top + 80 + MathF.Sin(t * 7f) * 70f, 0.5f, i * 8);
        }
        Doc.AddStroke(free);

        // 涓€涓甫鏃嬭浆 + 闈炵瓑姣旂缉鏀剧殑鐭╁舰锛氱敤鏉ユ牳瀵?鍙樻崲鍙備笌閫変腑妗?杩欎欢浜?        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 6f,
        };
        rect.AddPoint(cx - 260, top + 350, 1f, 0);
        rect.AddPoint(cx + 260, top + 520, 1f, 0);
        var center = new Vector2(cx, top + 435);
        rect.Transform = Matrix3x2.CreateRotation(0.14f, center)
                      * Matrix3x2.CreateScale(1.3f, 0.85f, center);
        Doc.AddStroke(rect);

        // 涓€鏉?*寰堝**鐨勮崸鍏夌瑪锛氱敤鏉ョ湅閫変腑妗嗘湁娌℃湁鎶婂ⅷ鍦堜綇銆?        // 妗嗘寜涓績绾跨畻鐨勮瘽锛岃繖鏉?64 鍍忕礌瀹界殑甯﹀瓙涓婁細鍘嬬潃鍥涙潯杈光€斺€斾竴鐪煎氨鑳界湅鍑烘潵銆?        var wide = new Stroke
        {
            Tool = Tool.Highlighter, Kind = StrokeKind.Freehand,
            Color = HighlighterCurrent, Width = 32f * DpiScale,
        };
        wide.AddPoint(cx - 420, top + 690, 0.5f, 0);
        wide.AddPoint(cx + 420, top + 690, 0.5f, 1);
        Doc.AddStroke(wide);

        // 2026-09-30锛?.2.0锛夛細鍑哄浘涔熸妸**闈㈡澘**甯︿笂鈥斺€?        //   --ink    寮€澧ㄨ抗闈㈡澘锛堥鑹?绮楃粏/绾垮瀷锛屽惈鏂扮殑杩炵画婊戞潯锛?        //   --layer  寮€灞傜骇闈㈡澘
        //   --custom 鍐嶅彔涓婅嚜瀹氫箟鍙栬壊鏉匡紙鑹茬浉鏉?+ 楗卞拰搴?鏄庡害鏂瑰潡锛?        // 鎺掔増濂戒笉濂界湅鍙兘闈犲浘鐪嬶紝鎵€浠ヨ繖浜?涓€寮犲浘閲岃鏈夐潰鏉?鐨勫鐓у繀椤绘湁鍑哄彛銆?        //
        // 鈿?宸ュ叿蹇呴』鏄?*妗嗛€?*锛氭搷浣滄潯/闈㈡澘鐢?`SelectionBarShown` 鎶婂畧
        //锛堝彧鏈夋閫?/ 鍥惧舰宸ュ叿鎵嶆湁鐐瑰緱鍒扮殑閭ｄ竴鍧楋級銆傚繕浜嗚繖涓€鍙ユ椂鍑哄浘閲屽彧鏈夋鍜屾墜鏌勶紝
        // 鐪嬪浘鐨勮繕浠ヤ负"闈㈡澘娌″紑"鈥斺€斿嚭鍥捐繖鏉¤矾绗竴鐗堝氨韪╀簡杩欎釜銆?        var cmdArgs = Environment.GetCommandLineArgs();
        Tool = Tool.Marquee;
        SelBarCollapsed = false;
        bool wantInk = cmdArgs.Contains("--ink"), wantLayer = cmdArgs.Contains("--layer");
        bool wantCustom = cmdArgs.Contains("--custom");
        SelPanelOpen = (wantInk || wantCustom) ? SelPanel.Ink : wantLayer ? SelPanel.Layer : SelPanel.None;

        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Doc.InvalidateAll();
        // 鑹叉澘鏀惧湪**閫変腑鎽嗗ソ涔嬪悗**寮€锛氳捣鐐硅壊鍙栫殑鏄?閫変腑澧ㄨ抗"锛屽厛寮€灏遍€€鎴愭墜閲岄偅鏀瑪鐨勯粯璁よ壊
        // 锛堢涓€鐗堝嚭鍥惧氨韪╀簡杩欎釜锛氶€変腑閭ｆ潯榛戝ⅷ锛岃壊鏉垮嵈鍋滃湪绾㈣壊涓婏級銆?        if (wantCustom) OpenCustomColorForTest();

        // `--dragmid`锛氭妸绮楃粏婊戞潯**鎸夊湪涓棿涓嶆澗鎵?*鈥斺€旂粰"鎷栧姩涓樉绀烘暟鍊?鍑哄鐓у浘鐢ㄣ€?        // 鐪熸満涓婂畠闈犳寚閽堟秷鎭┍鍔紝鍑哄浘杩欐潯璺病鏈夋寚閽堬紝鍙兘鍊熻嚜妫€閭ｄ袱涓挬瀛愭妸瀹冩憜鍑烘潵銆?        if (cmdArgs.Contains("--dragmid"))
        {
            var sl = SelectionHandles.SliderRect(SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                  DpiScale, ViewportCanvas, SelectionHandles.SwatchCount);
            float mx = SelectionHandles.SliderXOfT(0.62f, SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                   DpiScale, ViewportCanvas, SelectionHandles.SwatchCount);
            SelectionGestureForTest(mx, (sl.MinY + sl.MaxY) * 0.5f);
            PanelDragMoveForTest(mx + 2f, (sl.MinY + sl.MaxY) * 0.5f);
        }
        SettleFrames(800);

        // 閲忓昂锛氭妸涓夊娴眰鐨勫疄闄呭昂瀵?闂磋窛鎵撳嚭鏉ワ紙涓嶅嚟鎰熻璋冿紝鍓嶅悗涓ょ増閮借窇鍚屼竴鏉★級銆?        PrintOverlayMetrics();

        if (path == null)
        {
            Console.WriteLine("閫変腑妗嗗凡鎽嗗ソ锛岀瓑澶栭儴鎴浘锛堣繖涓ā寮忎笉浼氳嚜宸遍€€鍑猴級");
            return;
        }

        // 鍑哄浘锛堢灞忥紝閿佸睆/杩滅▼涔熻兘鍑猴級锛氳寖鍥?= 閫変腑妗?鈭?鎿嶄綔鏉?鈭紙寮€鐫€鐨勶級闈㈡澘锛屽啀鐣欎竴鍦堢粰鎶曞奖銆?        // 鎿嶄綔鏉℃寕鍦ㄦ涓嬫柟锛圫electionHandles.BarRect 绠楃殑灏辨槸閭ｄ釜浣嶇疆锛夛紝
        // 鎵€浠ユ妸瀹冧竴璧峰苟杩涙潵锛屽惁鍒欏嚭鍥句細鎶婃搷浣滄潯鍒囨帀涓€鍗娿€?        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        var bar = SelectionHandles.BarRect(aabb, DpiScale, ViewportCanvas);
        var region = new RectF
        {
            MinX = MathF.Min(aabb.MinX, bar.MinX), MinY = MathF.Min(aabb.MinY, bar.MinY),
            MaxX = MathF.Max(aabb.MaxX, bar.MaxX), MaxY = MathF.Max(aabb.MaxY, bar.MaxY),
        };
        if (SelPanelOpen == SelPanel.Ink)
        {
            region.Add(SelectionHandles
                .PanelRect(aabb, DpiScale, ViewportCanvas, SelectionHandles.SwatchCount));
            if (CustomColorOpen)
                region.Add(SelectionHandles.CustomPanelRect(aabb, DpiScale, ViewportCanvas,
                                                           SelectionHandles.SwatchCount));
        }
        else if (SelPanelOpen == SelPanel.Layer)
        {
            region.Add(SelectionHandles.LayerPanelRect(aabb, DpiScale, ViewportCanvas));
        }
        if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("鍑哄浘澶辫触");
        _quit = true;
    }

    /// <summary>
    /// **娴眰閲忓昂**锛?.2.0锛夛細鎶婁笁澶勬诞灞傦紙鎿嶄綔鏉?/ 澧ㄨ抗闈㈡澘 / 灞傜骇闈㈡澘锛夌殑瀹為檯灏哄鍜?    /// 鍐呰竟璺?/ 鏍奸棿璺?/ 鍒嗙粍缂濇寜**鍑犱綍璇诲嚭鏉?*鎵撴垚涓€寮犺〃銆?    ///
    /// 涓轰粈涔堣鏈夊畠锛堢敤鎴?2026-09-30锛?鍏堢敤 --uitest 鎶婁笁澶勬诞灞傜殑瀹為檯灏哄/闂磋窛閲忓嚭鏉ワ紝
    /// 涓嶅嚟鎰熻璋?锛夛細鏀剁揣瑕佹敹鍦ㄥ摢鍎裤€佹敹浜嗗灏戯紝寰楀厛鏈夊墠鍚庝袱鐗堢殑鍚屼竴鎶婂昂瀛愩€?    /// 瀹冭蛋鐨勬槸**缁樺埗/鍛戒腑鍚屼竴浠藉嚑浣曞嚱鏁?*锛屾墍浠ラ噺鍑烘潵鐨勫氨鏄睆骞曚笂鐪熸鐢诲嚭鏉ョ殑閭ｄ釜鏁帮紝
    /// 涓嶆槸鍙︽妱涓€浠?璁捐绋挎暟瀛?銆?    ///
    /// 鍗曚綅涓€寰嬮€昏緫鍍忕礌锛坉pi 浼?1銆乿isible 浼犵┖ = 涓嶅す鍙栵紝閲忕殑鏄函鍏紡锛夈€?    /// </summary>
    private void PrintOverlayMetrics()
    {
        const float dpi = 1f;
        var sel = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        var none = RectF.Empty;
        int sc = SelectionHandles.SwatchCount;

        var bar = SelectionHandles.BarRect(sel, dpi, none);
        var b0 = SelectionHandles.BarButtonRect(0, sel, dpi, none);
        var b1 = SelectionHandles.BarButtonRect(1, sel, dpi, none);
        float barPad = b0.MinX - bar.MinX;
        float barGap = b1.MinX - b0.MaxX;
        float barH = bar.MaxY - bar.MinY;
        float barRadius = MathF.Min(FloatingTheme.CornerRadius, barH * 0.5f);

        var p = SelectionHandles.PanelRect(sel, dpi, none, sc);
        var s0 = SelectionHandles.SwatchRect(0, sel, dpi, none, sc);
        var s1 = SelectionHandles.SwatchRect(1, sel, dpi, none, sc);
        var sRow = SelectionHandles.SwatchRect(SelectionHandles.SwatchColumns, sel, dpi, none, sc);
        var c0 = SelectionHandles.StyleCellRect(0, sel, dpi, none, sc);
        var c1 = SelectionHandles.StyleCellRect(1, sel, dpi, none, sc);

        var lp = SelectionHandles.LayerPanelRect(sel, dpi, none);
        var l0 = SelectionHandles.LayerCellRect(0, sel, dpi, none);
        var l1 = SelectionHandles.LayerCellRect(1, sel, dpi, none);

        Console.WriteLine();
        Console.WriteLine("=== 娴眰閲忓昂锛堥€昏緫鍍忕礌锛沝pi=1銆佷笉澶瑰彇锛?==");
        Console.WriteLine($"  鎿嶄綔鏉?    {bar.MaxX - bar.MinX:F0} 脳 {barH:F0}   涓ょ鍐呰竟璺?{barPad:F1}   "
                          + $"鎸夐挳 {b0.MaxX - b0.MinX:F0}锛堟柟鏍煎瓙锛夋牸缂?{barGap:F1}  "
                          + $"鎮仠搴?{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}脳"
                          + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}  "
                          + $"鍦嗚 {barRadius:F0}");
        Console.WriteLine($"  澧ㄨ抗闈㈡澘   {p.MaxX - p.MinX:F0} 脳 {p.MaxY - p.MinY:F0}   鍐呰竟璺?{s0.MinX - p.MinX:F1}   "
                          + $"鑹茬墖 {s0.MaxX - s0.MinX:F0} 缂?{s1.MinX - s0.MaxX:F1} 琛岀紳 {sRow.MinY - s0.MaxY:F1}");
        Console.WriteLine($"            绾垮瀷鏍?{c0.MaxX - c0.MinX:F1}脳{c0.MaxY - c0.MinY:F0}銆佹牸缂?{c1.MinX - c0.MaxX:F1}锛?
                          + $"婊戞潯琛?{SelectionHandles.SliderRowLogical:F0} / 绾垮瀷琛?{SelectionHandles.StyleRowLogical:F0}锛?
                          + $"闈㈡澘绂绘潯 {SelectionHandles.PanelGapLogical:F0}");
        Console.WriteLine($"  灞傜骇闈㈡澘   {lp.MaxX - lp.MinX:F0} 脳 {lp.MaxY - lp.MinY:F0}   鍐呰竟璺?{l0.MinX - lp.MinX:F1}   "
                          + $"鏍?{l0.MaxX - l0.MinX:F0} 缂?{l1.MinX - l0.MaxX:F1}");
        var pk = SelectionHandles.CustomPanelRect(sel, dpi, none, sc);
        var psv = SelectionHandles.PickSvRect(sel, dpi, none, sc);
        var phue = SelectionHandles.PickHueRect(sel, dpi, none, sc);
        Console.WriteLine($"  鍙栬壊鏉?    {pk.MaxX - pk.MinX:F0} 脳 {pk.MaxY - pk.MinY:F0}   鍐呰竟璺?{psv.MinX - pk.MinX:F1}   "
                          + $"SV {psv.MaxX - psv.MinX:F0} 鑹茬浉鏉?{phue.MaxX - phue.MinX:F0}  鏍肩紳 {phue.MinX - psv.MaxX:F1}");
        Console.WriteLine();
    }

    /// <summary>
    /// **娴眰灏哄 token 鑷**锛?.2.0 楠屾敹鈶狅級锛氫笁澶勬诞灞傦紙鎿嶄綔鏉?/ 澧ㄨ抗闈㈡澘 / 灞傜骇闈㈡澘 / 鍙栬壊鏉匡級
    /// 鐨勫唴杈硅窛銆佹牸闂磋窛銆佹牸涓庣紳蹇呴』**浠庡悓涓€濂?token 璇诲嚭鏉?*銆?    ///
    /// 鍒ゆ嵁鏄?鍑犱綍璇诲嚭鏉ョ殑鏁?== token 绠楀嚭鏉ョ殑鏁?锛堝宸?0.01锛夆€斺€斿摢澶╂湁浜虹粫杩?token
    /// 椤烘墜鍐欎竴涓?10f锛岃繖閲屽綋鍦哄彉绾€俙--selftest` 鍜?`--uitest` 鍏辩敤鍚屼竴浠斤紙璁″垝閲屽啓鐨勬槸
    /// 涓ゆ潯鑷锛屼袱杈硅窇鍒扮殑灏辨槸鍚屼竴涓畠锛夈€?    ///
    /// <paramref name="check"/> 鏄皟鐢ㄦ柟鑷繁鐨勭孩缁垮洖璋冿紙涓よ竟鐨勮緭鍑烘牸寮忎笉涓€鏍凤級銆?    /// </summary>
    private void CheckFloatOverlayTokens(Action<string, bool, string> check)
    {
        float dpi = DpiScale;
        const float tol = 0.01f;
        var sel = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        int sc = SelectionHandles.SwatchCount;

        var bar = SelectionHandles.BarRect(sel, dpi, RectF.Empty);
        var btn0 = SelectionHandles.BarButtonRect(0, sel, dpi, RectF.Empty);
        var btn1 = SelectionHandles.BarButtonRect(1, sel, dpi, RectF.Empty);
        float barPad = (btn0.MinX - bar.MinX) / dpi;
        float barGap = (btn1.MinX - btn0.MaxX) / dpi;
        float barW = (bar.MaxX - bar.MinX) / dpi;
        float barWTok = SelectionHandles.FloatPadLogical * 2
                      + SelectionHandles.BarButtonCount * SelectionHandles.BarButtonWidthLogical
                      + (SelectionHandles.BarButtonCount - 1) * SelectionHandles.BarGapLogical;
        bool barOk = Near(barPad, SelectionHandles.FloatPadLogical, tol)
                  && Near(barGap, SelectionHandles.BarGapLogical, tol)
                  && Near(barW, barWTok, tol)
                  && Near((bar.MaxY - bar.MinY) / dpi, SelectionHandles.BarHeightLogical, tol);

        var p = SelectionHandles.PanelRect(sel, dpi, RectF.Empty, sc);
        var s0 = SelectionHandles.SwatchRect(0, sel, dpi, RectF.Empty, sc);
        var s1 = SelectionHandles.SwatchRect(1, sel, dpi, RectF.Empty, sc);
        var sRow = SelectionHandles.SwatchRect(SelectionHandles.SwatchColumns, sel, dpi, RectF.Empty, sc);
        var c0 = SelectionHandles.StyleCellRect(0, sel, dpi, RectF.Empty, sc);
        var c1 = SelectionHandles.StyleCellRect(1, sel, dpi, RectF.Empty, sc);
        int rows = (sc + SelectionHandles.SwatchColumns - 1) / SelectionHandles.SwatchColumns;
        float panelWTok = SelectionHandles.FloatPadLogical * 2
                        + SelectionHandles.SwatchColumns * SelectionHandles.SwatchSizeLogical
                        + (SelectionHandles.SwatchColumns - 1) * SelectionHandles.FloatGapLogical;
        float panelHTok = SelectionHandles.FloatPadLogical * 2
                        + SelectionHandles.SliderRowLogical + SelectionHandles.StyleRowLogical
                        + rows * SelectionHandles.SwatchSizeLogical + (rows - 1) * SelectionHandles.FloatGapLogical;
        bool panelOk = Near((s0.MinX - p.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                    && Near((s1.MinX - s0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((sRow.MinY - s0.MaxY) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((s0.MaxX - s0.MinX) / dpi, SelectionHandles.SwatchSizeLogical, tol)
                    && Near((c1.MinX - c0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((c0.MaxY - c0.MinY) / dpi, SelectionHandles.StyleRowLogical, tol)
                    && Near((p.MaxX - p.MinX) / dpi, panelWTok, tol)
                    && Near((p.MaxY - p.MinY) / dpi, panelHTok, tol);

        // 8.2.1锛氭偓鍋?/ 婵€娲荤殑搴曞繀椤绘槸**姝ｆ柟褰?*锛堢敤鎴风偣鍚嶇殑閭ｆ潯锛?鎮仠鏄暱鏂瑰舰涓嶆槸姝ｆ柟褰紝
        // 鎵€浠ュ垎鏁?锛夆€斺€旈€愭牸鏍稿"瀹?= 楂?= 鏍煎 鈭?2脳inset"锛屽苟妫€鏌ュ畠灞呬腑銆?        bool chipOk = true; string chipNote = "";
        for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
        {
            var chip = SelectionHandles.BarHoverChip(i, sel, dpi, RectF.Empty);
            var btnI = SelectionHandles.BarButtonRect(i, sel, dpi, RectF.Empty);
            float cw = (chip.MaxX - chip.MinX) / dpi, chh = (chip.MaxY - chip.MinY) / dpi;
            float want = SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2;
            if (MathF.Abs(cw - chh) > tol || MathF.Abs(cw - want) > tol
                || MathF.Abs((chip.MinX + chip.MaxX) * 0.5f - (btnI.MinX + btnI.MaxX) * 0.5f) > tol * dpi)
            {
                chipOk = false;
                chipNote = $"绗?{i} 鏍煎簳 {cw:F1}脳{chh:F1}锛堣 {want:F0}脳{want:F0} 涓斿眳涓級";
                break;
            }
        }

        var lp = SelectionHandles.LayerPanelRect(sel, dpi, RectF.Empty);
        var l0 = SelectionHandles.LayerCellRect(0, sel, dpi, RectF.Empty);
        var l1 = SelectionHandles.LayerCellRect(1, sel, dpi, RectF.Empty);
        bool layerOk = Near((l0.MinX - lp.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                    && Near((l1.MinX - l0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((l0.MaxX - l0.MinX) / dpi, SelectionHandles.LayerCellLogical, tol);

        var pk = SelectionHandles.CustomPanelRect(sel, dpi, RectF.Empty, sc);
        var psv = SelectionHandles.PickSvRect(sel, dpi, RectF.Empty, sc);
        var phue = SelectionHandles.PickHueRect(sel, dpi, RectF.Empty, sc);
        bool pickOk = Near((psv.MinX - pk.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                   && Near((phue.MinX - psv.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                   && Near((psv.MaxX - psv.MinX) / dpi, SelectionHandles.PickSvLogical, tol)
                   && Near((phue.MaxX - phue.MinX) / dpi, SelectionHandles.PickHueLogical, tol);

        // 鍦嗚涔熸槸 token锛氫笁澶勬诞灞傞兘璇荤晫闈㈡帹涓婃潵鐨勪富棰橈紙= InkUi.Tokens.FloatingCorner锛夈€?        bool cornerOk = Near(FloatingTheme.CornerRadius, InkUi.Tokens.FloatingCorner, tol);

        check("娴眰灏哄锛氬唴杈硅窛/鏍奸棿璺?鏍煎昂瀵?鍦嗚閮藉湪 token 涓婏紙鎿嶄綔鏉★級", barOk,
              $"鍐呰竟璺?{barPad:F1}銆佹牸缂?{barGap:F1}銆佹暣鏉?{barW:F0}脳{(bar.MaxY - bar.MinY) / dpi:F0}");
        check("娴眰灏哄锛氭搷浣滄潯鎮仠/婵€娲诲簳鏄鏂瑰舰锛?.2.1锛岀敤鎴风偣鍚嶏級", chipOk,
              chipOk
                  ? $"{SelectionHandles.BarButtonCount} 鏍奸€愭牸鏍稿锛?
                    + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}脳"
                    + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}銆佸眳涓?
                  : chipNote);
        check("娴眰灏哄锛氬ⅷ杩归潰鏉垮湪 token 涓婏紙鍚缂濅笌鏁村崱瀹介珮锛?, panelOk,
              $"鍐呰竟璺?{(s0.MinX - p.MinX) / dpi:F1}銆佽壊鐗囩紳 {(s1.MinX - s0.MaxX) / dpi:F1}銆?
              + $"琛岀紳 {(sRow.MinY - s0.MaxY) / dpi:F1}銆佸崱鐗?{(p.MaxX - p.MinX) / dpi:F0}脳{(p.MaxY - p.MinY) / dpi:F0}");
        check("娴眰灏哄锛氬眰绾ч潰鏉垮湪 token 涓?, layerOk,
              $"鍐呰竟璺?{(l0.MinX - lp.MinX) / dpi:F1}銆佹牸缂?{(l1.MinX - l0.MaxX) / dpi:F1}");
        check("娴眰灏哄锛氳嚜瀹氫箟鍙栬壊鏉垮湪 token 涓?, pickOk,
              $"鍐呰竟璺?{(psv.MinX - pk.MinX) / dpi:F1}銆丼V {(psv.MaxX - psv.MinX) / dpi:F0}銆?
              + $"鑹茬浉鏉?{(phue.MaxX - phue.MinX) / dpi:F0}");
        check("娴眰鍦嗚锛氳蛋鐣岄潰涓婚浠ょ墝锛堜笁澶勪竴涓暟锛?, cornerOk,
              $"涓婚 {FloatingTheme.CornerRadius:F0} vs InkUi.Tokens.FloatingCorner {InkUi.Tokens.FloatingCorner:F0}");
    }

    private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
    private (bool ok, string detail) CheckHotkeyDoc(KeyMap map)
    {
        const string docPath = "蹇嵎閿€昏〃.md";
        if (!File.Exists(docPath))
            return (true, $"{docPath} 涓嶅湪褰撳墠鐩綍锛岃繖涓€椤硅烦杩?);

        var lines = File.ReadAllText(docPath).Split('\n');
        var missing = new List<string>();
        foreach (var b in map.Bindings)
        {
            string chord = b.Chord.ToString();
            string name = KeyMap.Describe(b.Action);
            bool found = lines.Any(l => l.Contains(chord) && l.Contains(name));
            if (!found) missing.Add($"{b.Scope} {chord} {name}");
        }

        return (missing.Count == 0,
                missing.Count == 0
                    ? $"鍏?{map.Bindings.Count} 鏉★紝鍏ㄩ儴鏌ュ緱鍒?
                    : "缂猴細" + string.Join("锛?, missing.Take(6))
                      + (missing.Count > 6 ? $" 绛?{missing.Count} 鏉? : ""));
    }

    /// <summary>
    /// 钀界偣鍙嶉鎽嗘牱锛氭妸鎸囬拡鍋滃湪灞忓箷涓ぎ銆佹寜鎸囧畾宸ュ叿鎶婅惤鐐瑰弽棣堟寕鍑烘潵锛岀瓑澶栭儴鎴浘銆?    /// 鐩殑鍜?--rotateshow 涓€鏍封€斺€斿厜鏍囪繖绫讳笢瑗垮彧鏈夌湅鍦ㄥ浘涓婃墠绠楅獙杩囥€?    /// </summary>
    private void CursorShowcase(string tool, float widthLogical)
    {
        Doc.Clear();
        Doc.ClearHistory();
        Tool = tool switch
        {
            "highlighter" or "hl" => Tool.Highlighter,
            "laser" => Tool.Laser,
            "eraser" => Tool.Eraser,
            "pixeleraser" or "blockeraser" => Tool.PixelEraser,
            "capture" or "screenshot" => Tool.Capture,      // 鎴浘锛氱湅鑷粯鐨勫彇鏅钀界偣
            _ => Tool.Pen,
        };
        if (widthLogical > 0f)
        {
            if (Tool == Tool.Highlighter) HighlighterWidthLogical = widthLogical;
            else if (Tool == Tool.Laser) LaserWidthLogical = widthLogical;
            else if (Tool == Tool.Eraser) EraserRadiusLogical = widthLogical;
            else if (Tool == Tool.PixelEraser) PixelEraserWidthLogical = widthLogical;
            else PenWidthLogical = widthLogical;
        }

        PassThrough = false;
        PointerInside = true;
        _drawing = false;
        LastPointerType = showcasePenDevice ? Native.PT_PEN : Native.PT_MOUSE;
        PointerX = _virtualX + _virtualW * 0.5f;
        PointerY = _virtualY + _virtualH * 0.5f;
        ShowHud = false;
        _dirty = true;
        SettleFrames(600);
        string size = Tool == Tool.PixelEraser
            ? $"鐭╁舰 {PixelEraserWidthLogical:F0}脳{PixelEraserHeightLogical:F0} 閫昏緫鍍忕礌"
              + $"锛堥珮:瀹?= {GoldenRatio:F3}锛?
            : $"瀹?{CurrentToolWidthLogical} 閫昏緫鍍忕礌锛堝鍦?{CursorOuterRadius:F0}px锛?;
        Console.WriteLine($"钀界偣鍙嶉宸叉憜濂斤細{Tool} {size}"
                        + $"锛坽DrawnCursor}锛岃剰鍖?{DrawnCursorRadius:F0}px锛?);
        Console.WriteLine("cursor showcase ready");
    }

    private bool showcasePenDevice = false;

    /// <summary>
    /// 鍍忕礌姗＄毊鎽嗘牱锛?-pixeleraseshow锛夛細鐢诲嚑鏉?鏉夸功"锛岀敤绔栫潃鐨勯粍閲戞瘮鐭╁舰鎶逛竴涓嬶紝
    /// **鑷繁鎶撳睆**瀛樹袱寮犲浘鈥斺€旀摝涔嬪墠锛堝甫钀界偣锛夊拰鎿︿箣鍚庯紙钀界偣鎸紑锛夈€?    ///
    /// 涓轰粈涔堜笉闈犲閮ㄦ埅鍥惧伐鍏凤細鑷宸茬粡鑳芥姄灞忎簡锛圫creenProbe锛夛紝鎽嗘牱鑷繁瀛樺浘灏戜竴姝ヤ汉宸ャ€?    /// 钀界偣鍦ㄧ浜屽紶閲屽繀椤绘尓寮€鈥斺€旂煩褰㈡鍘嬪湪鍒囧彛涓婏紝灏辩湅涓嶅嚭"涓€绗旇鍒囨垚浜嗕袱娈?銆?    /// </summary>
    private void PixelEraserShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float dpi = DpiScale;

        // 涓夋潯妯ⅷ锛堢瑪杩癸紝浼氳鍒囨垚涓ゆ锛夈€佷竴鏉¤崸鍏夌瑪锛堝悓鏍蜂細琚垏锛夈€?        // 涓€涓煩褰紙鍥惧舰锛岃疆寤撹纰板埌鏃舵暣鏉″垹锛夈€備笁绉嶅璞′竴灞忕湅鍏ㄣ€?        // 澧ㄦ按鐨勪綅缃钀藉湪姗＄毊鐨勭珫杈硅寖鍥达紙卤鍗婇珮锛夐噷鈥斺€旇惤鍦ㄥ闈㈠氨鐪嬩笉鍑?鎿︽帀浜嗕竴鏁存潯"銆?        foreach (float dy in new[] { -120f, -60f, 0f })
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0.10f, 0.11f, 0.13f, 1f), Width = 8f * dpi,
            };
            for (int i = 0; i <= 120; i++)
                s.AddPoint(cx - 520 + i * 9f, cy + dy + MathF.Sin(i * 0.32f) * 6f, 0.9f, i);
            Doc.AddStroke(s);
        }
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Color = new Color4(1f, 0.84f, 0.16f, 1f), Width = 26f * dpi,
        };
        for (int i = 0; i <= 60; i++) hl.AddPoint(cx - 440 + i * 15f, cy + 110, 1f, i);
        Doc.AddStroke(hl);

        var box = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 5f * dpi,
        };
        box.AddPoint(cx + 40, cy - 250, 1f, 0);       // 宸﹁竟鍘嬪湪姗＄毊閲岋紙姗＄毊鍗婂 = 93 鐗╃悊鍍忕礌锛?        box.AddPoint(cx + 280, cy + 250, 1f, 0);
        Doc.AddStroke(box);

        // **澶氭鍥惧舰**锛堥暱鏂逛綋 12 鏉℃１銆佸渾鏌?5 绗旓級鈥斺€?026-09-20 淇殑閭ｄ袱绫伙細
        // 鎿︿竴鍒€涔嬪悗澶栧舰閫愮瑪涓嶅彉锛氳鎸＄殑妫?/ 琚尅鐨勫崐鍦堢啍瀹?*杩樻槸缁嗚櫄绾?*锛?        // 娈典笌娈典箣闂翠篃**涓嶄細杩炲嚭鍋囩嚎**銆傞兘鎽嗗湪姗＄毊鐨勭珫鐩村甫閲岋紝濂界湅鍑?纭疄鍒囦簡涓€鍒€"銆?        var cub = new Stroke
        {
            Tool = Tool.Cuboid, Kind = StrokeKind.Cuboid,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * dpi,
        };
        cub.SetCuboidFront(cx - 40, cy + 40, cx + 180, cy + 240);
        cub.SetCuboidDepth(cx + 20, cy - 20);         // 娣卞害 = |40 鈭?(鈭?0)| = 60
        Doc.AddStroke(cub);

        var cyl = new Stroke
        {
            Tool = Tool.Cylinder, Kind = StrokeKind.Cylinder,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * dpi,
        };
        cyl.SetSolidBox(cx - 320, cy - 300, cx - 80, cy + 60);   // 鍙宠竟閭ｆ潯姣嶇嚎鍘嬪湪姗＄毊閲?        Doc.AddStroke(cyl);

        Tool = Tool.PixelEraser;
        PassThrough = false;
        ShowHud = false;
        PointerInside = true;
        LastPointerType = Native.PT_MOUSE;
        PointerX = cx; PointerY = cy;
        Doc.InvalidateAll();
        SettleFrames(600);

        int w = (int)(PixelEraserWidthLogical * dpi * 4.6f);
        int h = (int)(PixelEraserHeightLogical * dpi * 3.6f);
        int x0 = (int)cx - w / 2, y0 = (int)cy - h / 2;

        string shot1 = Path.Combine("reports", "鍍忕礌姗＄毊-1-钀界偣涓庢摝涔嬪墠.bmp");
        ScreenProbe.SaveBmp(shot1, x0, y0, w, h);
        Console.WriteLine($"宸插瓨 {Path.GetFullPath(shot1)}");
        Console.WriteLine($"  钀界偣锛歿(int)PixelEraserWidthLogical}脳{(int)PixelEraserHeightLogical} 閫昏緫鍍忕礌"
                        + $"锛坽PixelEraserWidthLogical * dpi:F0}脳{PixelEraserHeightLogical * dpi:F0} 鐗╃悊鍍忕礌锛?);

        int before = Doc.Strokes.Count;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
        Doc.EndErase();

        // 钀界偣鎸紑鍐嶆媿锛氱煩褰㈡鍘嬪湪鍒囧彛涓婂氨鐪嬩笉鍑哄垏鎴愪簡浠€涔堟牱銆?        PointerX = _virtualX + 120; PointerY = _virtualY + _virtualH - 120;
        SettleFrames(600);

        string shot2 = Path.Combine("reports", "鍍忕礌姗＄毊-2-鎿︿箣鍚?bmp");
        ScreenProbe.SaveBmp(shot2, x0, y0, w, h);
        Console.WriteLine($"宸插瓨 {Path.GetFullPath(shot2)}");
        Console.WriteLine($"  鎿︿箣鍓?{before} 鏉?鈫?鎿︿箣鍚?{Doc.Strokes.Count} 鏉?
                        + "锛堜笁鏉℃í澧?+ 鑽у厜绗斿悇鍒囨垚涓ゆ埅锛涚煩褰㈢啍鎴愮瑪杩瑰悗鍐嶅垏锛?
                        + "闀挎柟浣撱€佸渾鏌辩啍鎴?*涓€鏉℃１涓€娈?*鈥斺€旇櫄绾挎１ / 铏氱嚎鍗婂湀杩樻槸铏氱嚎锛?
                        + "娈典笌娈典箣闂存病鏈夊亣绾匡級");
        for (int i = 0; i < Doc.Strokes.Count; i++)
        {
            var s = Doc.Strokes[i];
            Console.WriteLine($"    #{i} {s.Kind,-9} 鐐?{s.Points.Count,4} 鎿﹂櫎鍖洪棿 {s.Erased.Count} 娈?
                            + $" 鍓╀綑 {s.RemainingRuns().Count} 娈?
                            + $"  x {s.Bounds.MinX:F0}..{s.Bounds.MaxX:F0}"
                            + $"  y {s.Bounds.MinY:F0}..{s.Bounds.MaxY:F0}");
        }
        Console.WriteLine("pixel eraser showcase done");
        _quit = true;
    }

    /// <summary>
    /// 鏃嬭浆鏍囩鎽嗘牱锛氶€変腑涓€涓璞°€佹妸妗嗚浆鍒版寚瀹氳搴︺€佹妸搴︽暟鏍囩鎸傚嚭鏉ヤ笉鍔紝
    /// 鐢卞閮ㄦ埅鍥句汉宸ユ牳瀵癸紙浣嶇疆銆佸彲璇绘€с€佸惛闄勫彉鑹诧級銆傝繖涓ā寮忎笉浼氳嚜宸遍€€鍑恒€?    /// </summary>
    private void RotateShowcase(float deg)
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.55f;
        var stroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = PenColor, Width = 8f * DpiScale,
        };
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.PI * 2f;
            stroke.AddPoint(sx + MathF.Cos(a) * 170f * DpiScale,
                            sy + MathF.Sin(a) * 120f * DpiScale, 0.9f, i * 8);
        }
        Doc.AddStroke(stroke);
        Doc.Selected.Clear();
        Doc.Selected.Add(stroke);
        Tool = Tool.Marquee;

        // 鐪熺殑杞繃鍘伙細妗嗗拰鏍囩閮借窡鐫€杞紝鎴浘鎵嶆槸"姝ｅ湪杞?鐨勬牱瀛?        var m = Matrix3x2.CreateRotation(deg * MathF.PI / 180f, new Vector2(sx, sy));
        Doc.SetTransformLive(stroke, m);
        Doc.SetTransformLive(stroke, m);
        _dirty = true;
        SettleFrames(300);

        SelRotating = true;
        SelRotationDegrees = deg;
        SelRotationSnapped = Math.Abs(deg % 90f) < 0.5f;
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"鏃嬭浆鏍囩宸叉憜濂斤細{SelectionHandles.FormatDegrees(deg)}"
                        + (SelRotationSnapped ? "锛堝惛闄勬€侊級" : "锛堣嚜鐢辨€侊級"));
        Console.WriteLine("rotate showcase ready");
    }
    private void ShapeToolShowcase(string path, string kind, bool drag, bool drawing,
                                   bool rotate, bool rotated, bool snap,
                                   bool angleRead, bool poseRead, bool autoSel)
    {
        BoardOn = true;                       // 鐧藉簳锛氭墜鏌勫帇鍦ㄦ闈笂鐪嬩笉娓?        SetUiFactory(() => new InkUi.FullUi());   // 娴眰閰嶈壊/鎶曞奖鐢辩晫闈㈡帹涓婃潵
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        // ---- `--autosel`锛?*鐢诲畬鑷姩閫変腑閭ｄ竴甯?*锛?026-09-22锛?---
        //
        // 鐪熸満鎷栦竴涓煩褰€佹澗鎵嬶紝鎷?鍒氱敾瀹?鐨勬牱瀛愩€傝鐪嬬殑鏄袱浠朵簨锛?        //   鈶?妗嗗湪锛堟 锛?鍏釜鎵嬫焺 锛?鏃嬭浆鏌勶級锛?        //   鈶?**涓嬮潰娌℃湁鎿嶄綔鏉?*鈥斺€斿浘褰㈠伐鍏蜂笅閭ｄ竴鏉′笉鐢伙紙瑙?Engine.SelectionBarShown锛夛紝
        //      鍥犱负瀹冩濂芥寕鍦ㄥ浘褰㈡涓嬫柟锛屼細鍚冩帀"鎺ョ潃鐢讳笅涓€涓?鐨勯偅涓€涓嬨€?        // 鍑哄浘鑼冨洿鍥犳鍒绘剰**寰€涓嬪鐣欎竴娈?*锛氫笉寰€涓嬬暀鐨勮瘽锛?鏈夋病鏈夋潯"杩欎欢浜嬬湅涓嶅嚭鏉ャ€?        if (autoSel)
        {
            SetToolFromUi(Tool.Rectangle);
            var aFrom = new Vector2(cx - 260f, cy - 170f);
            var aTo = new Vector2(cx + 260f, cy + 170f);
            SendMouse((int)aFrom.X, (int)aFrom.Y, 0);                            SettleFrames(60);
            SendMouse((int)aFrom.X, (int)aFrom.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(aFrom.X + (aTo.X - aFrom.X) * i / 4f),
                          (int)(aFrom.Y + (aTo.Y - aFrom.Y) * i / 4f), 0);
                SettleFrames(40);
            }
            SendMouse((int)aTo.X, (int)aTo.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(400);                       // 鏉炬墜 鈫?鑷姩閫変腑锛岀瓑瀹冪敾瀹?
            Console.WriteLine($"鐢诲畬鑷姩閫変腑锛氬璞?{Doc.Strokes.Count} 涓紝"
                            + $"閫変腑 {Doc.Selected.Count} 涓紝宸ュ叿杩樻槸 {Host.State.Tool}锛?
                            + $"鎿嶄綔鏉＄敾涓嶇敾 = {SelectionBarShown}");

            var sel = SelectionHandles.FrameOf(Doc.Selected);
            var shot = sel.CanvasAabb;
            // 鏃嬭浆鏌勫湪妗嗕笂鏂瑰渚э紝鍗曠嫭骞惰繘鏉ワ紙瀹冧笉鍦?aabb 閲岋級銆?            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, sel, DpiScale);
            shot.Add(grip.X - 40f, grip.Y - 40f);
            shot.Add(grip.X + 40f, grip.Y + 40f);
            // **寰€涓嬪鐣?120 閫昏緫鍍忕礌**锛氶偅姝ｆ槸"鎿嶄綔鏉℃湰璇ュ嚭鐜扮殑鍦版柟"
            //锛坄SelectionHandles.BarOffsetLogical` 璧风畻锛夛紝鐣欏嚭鏉ユ墠鐪嬪緱鍑哄畠涓嶅湪銆?            shot.Add(shot.MinX, shot.MaxY + 120f * DpiScale);
            if (!OffscreenFloatingShot(path, shot.Inflate(30f))) Console.WriteLine("鍑哄浘澶辫触");
            _quit = true;
            return;
        }

        // ---- 绗憿杞紙璇绘暟锛夐偅鍑犲紶 ----
        // `--pose`锛氭嫋鏃嬭浆鏌?鈫?濮挎€佽璇绘暟锛堢煩褰㈠仠鍦ㄥ惛浣忕殑 **0掳**銆佹き鍦嗗仠鍦?**90掳**锛夈€?        if (poseRead)
        {
            bool isRect = kind != "ellipse";
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 璧锋墜閮芥槸"姝殑"锛氱煩褰?8掳锛堝樊涓€鐐圭偣灏辨锛夈€佹き鍦?52掳锛堝緢鏄庢樉鏄鐨勶級銆?            float startDeg = isRect ? 8f : 52f;
            var center = new Vector2(cx, cy);
            if (isRect)
            {
                sh.Tool = Tool.Rectangle; sh.Kind = StrokeKind.Rectangle;
                sh.AddPoint(center.X - 320f, center.Y - 200f, 1f, 0);
                sh.AddPoint(center.X + 320f, center.Y + 200f, 1f, 0);
            }
            else
            {
                sh.Tool = Tool.Ellipse; sh.Kind = StrokeKind.Ellipse;
                sh.AddPoint(center.X, center.Y, 1f, 0);              // 涓績
                sh.AddPoint(center.X + 330f, center.Y + 150f, 1f, 0); // 澶栬鐐癸紙a=330, b=150锛?            }
            sh.Transform = SelectionHandles.RotateMatrix(startDeg, center);
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var f0 = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f0.CanvasAabb.MinX + f0.CanvasAabb.MaxX) * 0.5f,
                                 (f0.CanvasAabb.MinY + f0.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f0, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a0 = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);
            // 瑕佽浆鍒板摢鍎匡細鐭╁舰椤烘椂閽?8.4掳锛? 鈫?-0.4锛屄?掳 鍐?鈫?鍚稿埌 0锛夛紱
            //          妞渾閫嗘椂閽?38.4掳锛?2 鈫?90.4锛屄?掳 鍐?鈫?鍚稿埌 90锛夈€?            float turn = isRect ? -8.4f : 38.4f;

            SendMouse((int)grip.X, (int)grip.Y, 0);                          SettleFrames(60);
            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            var last = grip;
            for (int i = 1; i <= 4; i++)
            {
                // 灞忓箷鍧愭爣 y 鏈濅笅锛氭柟鍚戣**鍑忓皬**鎵嶆槸瑙嗚涓婄殑閫嗘椂閽堬紝鎵€浠?rad = a0 鈭?杞繃鐨勮銆?                float rad = a0 - turn * i / 4f * MathF.PI / 180f;
                last = new Vector2(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SendMouse((int)last.X, (int)last.Y, 0);
                SettleFrames(50);
            }
            SettleFrames(220);

            var live = LiveSelectionFrame;
            var gripNow = SelectionHandles.CanvasPosition(SelHandle.Rotate, live, DpiScale);
            var region = live.CanvasAabb;
            region.Add(gripNow.X - 70f * DpiScale, gripNow.Y - 80f * DpiScale);
            region.Add(gripNow.X + 70f * DpiScale, gripNow.Y + 20f * DpiScale);
            Console.WriteLine($"濮挎€佽閭ｄ竴甯э紙{(isRect ? "鐭╁舰" : "妞渾")}锛夛細璇绘暟 {SelRotationPose:F1}掳"
                            + $"锛堝惛浣?{SelRotationSnapped}锛夛紝璧锋墜 {startDeg:F0}掳锛屾嫋浜?{turn:F1}掳"
                            + $"锛屾爣绛炬枃妗堛€寋SelectionHandles.FormatPose(SelRotationPose)}銆?);
            if (!OffscreenFloatingShot(path, region.Inflate(40f))) Console.WriteLine("鍑哄浘澶辫触");
            SendMouse((int)last.X, (int)last.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            _quit = true;
            return;
        }

        // `--angles`锛氫笁瑙掑舰鍜屽钩琛屽洓杈瑰舰**閮借鎷栫潃涓€涓《鐐逛笉鏉炬墜**鎵嶅嚭瑙掑害
        //锛堢敤鎴?2026-09-20 瀹氾細涓夎褰篃鏀规垚"鎷栧姩鏃舵墠鏄剧ず"锛屽拰骞宠鍥涜竟褰竴鏉″垽鎹級銆?        if (angleRead)
        {
            bool para = kind == "parallelogram";
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            if (para)
            {
                sh.Tool = Tool.Parallelogram; sh.Kind = StrokeKind.Parallelogram;
                sh.AddPoint(cx - 300f, cy + 170f, 1f, 0);      // 搴曞乏
                sh.AddPoint(cx + 300f, cy + 170f, 1f, 0);      // 搴曞彸
                sh.AddPoint(cx - 110f, cy - 170f, 1f, 0);      // 椤跺乏
            }
            else
            {
                // 鍒绘剰**涓嶇瓑杈?*锛氫笁涓涓変釜涓嶅悓鐨勬暟锛屽嚭鍥炬椂涓€鐪艰兘鐪嬪嚭"姣忎釜瑙掑悇鑷嫭绔?
                sh.Tool = Tool.Triangle; sh.Kind = StrokeKind.Triangle;
                sh.AddPoint(cx - 70f, cy - 250f, 1f, 0);       // 涓婇《鐐?                sh.AddPoint(cx - 320f, cy + 170f, 1f, 0);      // 涓嬪乏
                sh.AddPoint(cx + 320f, cy + 170f, 1f, 0);      // 涓嬪彸
            }
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            // 鎷栦竴涓《鐐广€?*鍋滀綇涓嶆澗鎵?*锛氫笁瑙掑舰鍜屽钩琛屽洓杈瑰舰**閮藉彧鍦ㄦ嫋椤剁偣鏃?*鎵嶅嚭瑙掑害
            // 锛堢敤鎴?2026-09-20 瀹氾細"涓夎褰㈠簲璇ュ湪鎷栧姩鐨勬椂鍊欏啀鏄剧ず瑙掑害锛岃涓嶇劧鐪嬭捣鏉ヤ篃涔?锛夛紝
            // 鎵€浠ユ媿杩欎竴寮犲繀椤绘寜浣忎笉鏀锯€斺€旀澗鎵嬫媿鍑烘潵鏄┖鐨勩€?            // 涓夎褰㈡嫋**涓婇《鐐?*锛圴ertex0锛夈€佸钩琛屽洓杈瑰舰鎷?*搴曞彸椤剁偣**锛圴ertex1锛夈€?            var dragHandle = para ? ShapeHandle.Vertex1 : ShapeHandle.Vertex0;
            var dragBy = para ? new Vector2(70f, 40f) : new Vector2(90f, 70f);
            var v = SelectionHandles.ShapeHandleCanvasPosition(sh, dragHandle);
            var toP = new Vector2(v.X + dragBy.X, v.Y + dragBy.Y);
            SendMouse((int)v.X, (int)v.Y, 0);                            SettleFrames(60);
            SendMouse((int)v.X, (int)v.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(v.X + (toP.X - v.X) * i / 4f),
                          (int)(v.Y + (toP.Y - v.Y) * i / 4f), 0);
                SettleFrames(50);
            }
            SettleFrames(220);

            Span<Vector2> vs = stackalloc Vector2[4];
            Span<float> ds = stackalloc float[3];
            int n = FillAngleReadout(vs, ds);
            // **娌℃湁"鍐呰鍜?閭ｄ竴琛?*锛堢敤鎴?2026-09-19 瀹氾級锛屾墍浠ュ彧鎶ラ偅鍑犱釜瑙掕嚜宸便€?            Console.WriteLine(para
                ? $"澶硅閭ｄ竴甯э紙骞宠鍥涜竟褰㈡嫋椤剁偣涓級锛歿n} 涓 = "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[0])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[1])}锛岄《鐐?{vs[0]} / {vs[1]}"
                : $"鍐呰閭ｄ竴甯э紙涓夎褰㈡嫋椤剁偣涓級锛歿n} 涓 = "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[0])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[1])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[2])}");
            var region = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            region = region.Inflate(220f * DpiScale);            // 瑙掓爣鎸傚湪椤剁偣澶栦晶锛屾瑕佹斁瀹?            if (!OffscreenFloatingShot(path, region)) Console.WriteLine("鍑哄浘澶辫触");
            SendMouse((int)toP.X, (int)toP.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            _quit = true;
            return;
        }

        // ---- 鍦?/ 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰?/ 鎶涚墿绾?/ 闀挎柟浣擄細闈欐閫変腑鎬?----
        //锛堝悗涓ょ鏄?**娌℃湁鐗规畩鎵嬫焺**"鐨勯偅涓€绫伙細鎷嶇殑鏄€氱敤鍏墜鏌?锛?鏃嬭浆鏌勯偅鏍硅繛绾匡級
        if (kind != "line")
        {
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            switch (kind)
            {
                case "circle":
                    sh.Tool = Tool.Circle; sh.Kind = StrokeKind.Circle;
                    sh.AddPoint(cx - 40f, cy - 40f, 1f, 0);        // 鍦嗗績
                    sh.AddPoint(cx + 200f, cy - 40f, 1f, 0);       // 鍦嗗懆鐐癸紙鍗婂緞 240锛?                    break;
                case "ellipse":
                    sh.Tool = Tool.Ellipse; sh.Kind = StrokeKind.Ellipse;
                    sh.AddPoint(cx - 40f, cy, 1f, 0);              // 涓績
                    sh.AddPoint(cx + 260f, cy + 180f, 1f, 0);      // 澶栬鐐癸紙a=300, b=180锛?                    break;
                case "triangle":
                    sh.Tool = Tool.Triangle; sh.Kind = StrokeKind.Triangle;
                    sh.AddPoint(cx, cy - 220f, 1f, 0);             // 涓婁腑
                    sh.AddPoint(cx - 260f, cy + 160f, 1f, 0);      // 涓嬪乏
                    sh.AddPoint(cx + 260f, cy + 160f, 1f, 0);      // 涓嬪彸
                    break;
                case "parabola":
                    // 鎶涚墿绾匡細椤剁偣鍦ㄥ乏涓嬨€佺粡杩囩偣鍦ㄥ彸涓?鈫?寮€鍙ｅ悜涓婏紙鍥涗釜鏈濆悜閲屾渶濂借鐨勯偅涓級銆?                    // 瀹冩槸"娌℃湁鐗规畩鎵嬫焺"閭ｄ竴绫荤殑浠ｈ〃锛氶€変腑鍚庤鐪嬪埌**閫氱敤鍏墜鏌?*锛?                    // 鏃嬭浆鏌勯偅鏍圭嚎璇ヨ繛鍒?*妗嗕笂杈逛腑鐐?*锛堜笉鏄《鐐逛笌缁忚繃鐐圭殑涓偣锛夈€?                    sh.Tool = Tool.Parabola; sh.Kind = StrokeKind.Parabola;
                    sh.CurveAxis = CurveAxis.OpenUp;
                    sh.AddPoint(cx - 240f, cy + 150f, 1f, 0);      // 椤剁偣锛堟寜涓嬮偅涓偣锛?                    sh.SetParabolaVertex(cx - 240f, cy + 150f);
                    sh.SetParabolaThroughPoint(cx + 200f, cy - 170f);
                    break;
                case "cuboid":
                    // 闀挎柟浣擄細姝ｉ潰鐭╁舰 锛?娣卞害 150锛堢収鐢绘硶锛氭繁搴﹀線鍙充笂閫€锛夈€?                    sh.Tool = Tool.Cuboid; sh.Kind = StrokeKind.Cuboid;
                    sh.AddPoint(cx - 260f, cy - 40f, 1f, 0);
                    sh.SetCuboidFront(cx - 260f, cy - 40f, cx + 180f, cy + 200f);
                    sh.SetCuboidDepth(cy - 40f, cy - 40f + 150f);  // 娣?= |姝ｉ潰涓婅竟 鈭?杩欎釜 y| = 150
                    break;
                default: // parallelogram
                    sh.Tool = Tool.Parallelogram; sh.Kind = StrokeKind.Parallelogram;
                    sh.AddPoint(cx - 250f, cy + 160f, 1f, 0);      // 搴曞乏
                    sh.AddPoint(cx + 250f, cy + 160f, 1f, 0);      // 搴曞彸
                    sh.AddPoint(cx - 125f, cy - 160f, 1f, 0);      // 椤跺乏
                    break;
            }
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            // 鍚搁檮閭ｄ竴甯э細鐪熸嫋涓€娆￠《鐐癸紝**鍋滀綇涓嶆澗鎵?*鍐嶆媿锛堟澗鎵嬪氨娌℃湁鑳跺泭浜嗭級銆?            // 涓夎褰㈡憜鍒?绛夎竟"鐨勫宸噷锛堥珮 = 鈭?/2 脳 搴曡竟闀?520 鈮?450.3锛夈€?            if (snap && kind == "triangle")
            {
                var apex = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.Vertex0);
                var apexTo = new Vector2(cx + 1f, cy + 160f - 260f * MathF.Sqrt(3f));
                SendMouse((int)apex.X, (int)apex.Y, 0);                          SettleFrames(60);
                SendMouse((int)apex.X, (int)apex.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
                for (int i = 1; i <= 4; i++)
                {
                    SendMouse((int)(apex.X + (apexTo.X - apex.X) * i / 4f),
                              (int)(apex.Y + (apexTo.Y - apex.Y) * i / 4f), 0);
                    SettleFrames(40);
                }
                SettleFrames(200);
                Console.WriteLine($"鍚搁檮甯э細鍚稿埌={ShapeSnapKind}"
                                + $"锛堛€寋SelectionHandles.ShapeSnapLabel(ShapeSnapKind)}銆嶏級锛?
                                + $"琚嫋鐨勯《鐐?{apex} 鈫?{apexTo}");
            }

            var sFrame = SelectionHandles.FrameOf(Doc.Selected);
            var sAabb = sFrame.CanvasAabb;
            var sBar = SelectionHandles.BarRect(sAabb, DpiScale, ViewportCanvas);
            var region = sAabb;
            region.Add(sBar);
            if (SelectionHandles.RotateHandleVisible(sh))       // 鍦嗘病鏈夋棆杞焺锛屽埆鐧界畻涓€鍧?            {
                var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, sFrame, DpiScale);
                region.Add(grip.X - 40f * DpiScale, grip.Y - 40f * DpiScale);
                region.Add(grip.X + 40f * DpiScale, grip.Y + 40f * DpiScale);
            }
            // 鎷栧厓绱犳椂鐨勮兌鍥婃寕鍦ㄥ厓绱犲渚э細`--snap` 閭ｅ紶蹇呴』鎶婂畠涓€璧峰湀杩涙潵
            if (snap && VertexDragging)
            {
                region.Add(VertexPreviewCanvasPoint.X - 90f * DpiScale,
                           VertexPreviewCanvasPoint.Y - 60f * DpiScale);
                region.Add(VertexPreviewCanvasPoint.X + 90f * DpiScale,
                           VertexPreviewCanvasPoint.Y + 10f * DpiScale);
            }
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("鍑哄浘澶辫触");
            if (snap && VertexDragging)
            {
                SendMouse((int)ShapeSnapAnchor.X, (int)ShapeSnapAnchor.Y, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(150);
            }
            _quit = true;
            return;
        }

        // ---- 涓€鏉?宸茬粡杞繃 60掳"鐨勭洿绾裤€侀潤姝㈤€変腑锛堢揣妗嗘敼鍙ｅ緞鐨勭浜屼釜鐜板満锛?---
        if (rotated)
        {
            var rl = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 鍜屽彟澶栧嚑寮?*鍚屼竴鏉＄嚎**锛埼扁個 鈮?27.65掳锛夛紝缁曞畠鑷繁鐨勪腑鐐硅瑙夐€嗘椂閽堣浆 32.35掳
            // 鈫?杞畬 伪 鈮?60掳锛屾鏄?--rotate 鏉炬墜涔嬪悗閭ｄ釜鐘舵€併€?            rl.AddPoint(cx - 420f, cy + 220f, 1f, 0);
            rl.AddPoint(cx + 420f, cy - 220f, 1f, 0);
            rl.Transform = SelectionHandles.RotateMatrix(32.35f, new Vector2(cx, cy));
            Doc.AddStroke(rl);
            Doc.SelectOnly(new[] { rl });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var fr = SelectionHandles.FrameOf(Doc.Selected);
            var aabbR = fr.CanvasAabb;
            var barR = SelectionHandles.BarRect(aabbR, DpiScale, ViewportCanvas);
            var gripR = SelectionHandles.CanvasPosition(SelHandle.Rotate, fr, DpiScale);
            var regionR = aabbR;
            regionR.Add(barR);
            regionR.Add(gripR.X - 40f * DpiScale, gripR.Y - 40f * DpiScale);
            regionR.Add(gripR.X + 40f * DpiScale, gripR.Y + 40f * DpiScale);
            Console.WriteLine($"杞繃 60掳 鐨勭嚎銆侀潤姝㈤€変腑锛氭 {aabbR.MaxX - aabbR.MinX:F0}脳{aabbR.MaxY - aabbR.MinY:F0}"
                            + $"锛岀嚎鑷繁鐨勫ⅷ杩?{rl.WorldInkBounds.MaxX - rl.WorldInkBounds.MinX:F0}"
                            + $"脳{rl.WorldInkBounds.MaxY - rl.WorldInkBounds.MinY:F0}"
                            + $"锛孭addedBounds {rl.PaddedBounds.MaxX - rl.PaddedBounds.MinX:F0}"
                            + $"脳{rl.PaddedBounds.MaxY - rl.PaddedBounds.MinY:F0}");
            if (!OffscreenFloatingShot(path, regionR.Inflate(30f))) Console.WriteLine("鍑哄浘澶辫触");
            _quit = true;
            return;
        }

        // ---- 鏃嬭浆鎷栧姩涓細鐪熸寜鐪熻浆锛堢敤鎴疯鐪嬬殑灏辨槸杩欎竴甯э級----
        if (rotate)
        {
            var spinLine = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 鍜?闈欐閫変腑"閭ｅ紶**鍚屼竴鏉＄嚎**锛埼?鈮?27.65掳锛夛紝涓夊紶鍥炬墠濂藉姣?            spinLine.AddPoint(cx - 420f, cy + 220f, 1f, 0);
            spinLine.AddPoint(cx + 420f, cy - 220f, 1f, 0);
            Doc.AddStroke(spinLine);
            Doc.SelectOnly(new[] { spinLine });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var f0 = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f0.CanvasAabb.MinX + f0.CanvasAabb.MaxX) * 0.5f,
                                 (f0.CanvasAabb.MinY + f0.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f0, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a0 = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);

            SendMouse((int)grip.X, (int)grip.Y, 0);                          SettleFrames(60);
            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            // 閫嗘椂閽?31.5掳锛氳繖鏉＄嚎 伪鈧€ 鈮?27.65掳 鈫?59.15掳锛埪?掳 鍐咃級鈫?鍚稿埌 60掳锛岃鏁板ソ鐪嬩篃鍚镐綇浜?            const float Turn = 31.5f;
            var last = grip;
            for (int i = 1; i <= 4; i++)
            {
                float rad = a0 - Turn * i / 4f * MathF.PI / 180f;
                last = new Vector2(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SendMouse((int)last.X, (int)last.Y, 0);
                SettleFrames(40);
            }
            SettleFrames(200);

            // 鑼冨洿锛?*瀹炴椂妗?*锛堟棆杞腑妗嗘寜棰勮鍑犱綍绠楋紝瑙?LiveSelectionFrame锛夆埅 鏃嬭浆鏌?鈭?璇绘暟鏍囩
            var live = LiveSelectionFrame;
            var aabb = live.CanvasAabb;
            var gripNow = SelectionHandles.CanvasPosition(SelHandle.Rotate, live, DpiScale);
            var region = aabb;
            region.Add(gripNow.X - 60f * DpiScale, gripNow.Y - 60f * DpiScale);
            region.Add(gripNow.X + 60f * DpiScale, gripNow.Y + 60f * DpiScale);
            region.Add(gripNow.X - 90f * DpiScale, gripNow.Y - 90f * DpiScale);
            region.Add(gripNow.X + 90f * DpiScale, gripNow.Y);
            // 椤哄甫閲忎竴涓?绾?*鐪熷疄**鍗犲澶?锛堟妸涓や釜绔偣杩囧疄鏃剁煩闃靛啀澶栨墿鍗婄瑪瀹斤級鈥斺€?            // 鐢ㄦ潵鍜屼笂闈㈤偅涓姣旓細鐩哥瓑灏辨槸璐村悎锛屾槑鏄惧皬灏辨槸妗嗚櫄鑳栵紙鍙墦鍗帮紝涓嶆敼琛屼负锛夈€?            var liveM = spinLine.Transform * DragPreviewMatrix;
            var t0 = Vector2.Transform(new Vector2(spinLine.Points[0].X, spinLine.Points[0].Y), liveM);
            var t1 = Vector2.Transform(new Vector2(spinLine.Points[^1].X, spinLine.Points[^1].Y), liveM);
            var tight = RectF.Empty;
            tight.Add(t0.X, t0.Y);
            tight.Add(t1.X, t1.Y);
            tight = tight.Inflate(spinLine.Width * 0.5f + 2f);
            Console.WriteLine($"鏃嬭浆涓細璇绘暟 {SelRotationInclination:F1}掳锛堝惛浣?{SelRotationSnapped}锛夛紝"
                            + $"瀹炴椂妗?{aabb.MaxX - aabb.MinX:F0}脳{aabb.MaxY - aabb.MinY:F0}"
                            + $" ({(int)aabb.MinX},{(int)aabb.MinY})-({(int)aabb.MaxX},{(int)aabb.MaxY})锛?
                            + $"绾胯嚜宸辩湡瀹炲崰 {tight.MaxX - tight.MinX:F0}脳{tight.MaxY - tight.MinY:F0}"
                            + $" ({(int)tight.MinX},{(int)tight.MinY})-({(int)tight.MaxX},{(int)tight.MaxY})"
                            + $"锛屾ā鍨嬮噷锛堟病鍔級鐨勫ⅷ杩硅寖鍥?"
                            + $"{(int)spinLine.WorldInkBounds.MinX},{(int)spinLine.WorldInkBounds.MinY}"
                            + $"-{(int)spinLine.WorldInkBounds.MaxX},{(int)spinLine.WorldInkBounds.MaxY}");
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("鍑哄浘澶辫触");

            SendMouse((int)last.X, (int)last.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);
            // 鏉炬墜鍚庢浼氶噸鏂版寜**妯″瀷**绠楋紙FrameOf锛夆€斺€斿拰鎷栧姩涓偅涓?鏃ф杞繃鍘荤殑澶栨帴鐭╁舰"
            // 宸灏戯紝杩欓噷缁欏嚭鏁板瓧锛堝彧鎵撳嵃锛屼笉鏀硅涓猴級銆?            var afterAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            Console.WriteLine($"鏃嬭浆鏉炬墜鍚庯細妗?{afterAabb.MaxX - afterAabb.MinX:F0}脳{afterAabb.MaxY - afterAabb.MinY:F0}"
                            + $"锛堟嫋鍔ㄤ腑鏄?{aabb.MaxX - aabb.MinX:F0}脳{aabb.MaxY - aabb.MinY:F0}锛夛紝"
                            + $"绾跨殑瀹炴椂濮挎€?"
                            + $"{SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(spinLine, 0), SelectionHandles.EndpointCanvasPosition(spinLine, 1)):F1}掳");
            _quit = true;
            return;
        }

        // ---- 姝ｅ湪鐢讳竴鏉＄洿绾匡細鎸変綇涓嶆斁锛屾嫋鍒?*鍘熷 45.5掳**锛埪?掳 瀹瑰樊鍐?鈫?鍚稿埌 45掳锛?---
        if (drawing)
        {
            SetToolFromUi(Tool.Line);
            var from = new Vector2(cx - 380f, cy + 200f);
            float rad = -45.5f * MathF.PI / 180f;
            var tipTarget = new Vector2(from.X + 620f * MathF.Cos(rad), from.Y + 620f * MathF.Sin(rad));

            SendMouse((int)from.X, (int)from.Y, 0);                            SettleFrames(60);
            SendMouse((int)from.X, (int)from.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(from.X + (tipTarget.X - from.X) * i / 4f),
                          (int)(from.Y + (tipTarget.Y - from.Y) * i / 4f), 0);
                SettleFrames(40);
            }
            SettleFrames(200);

            var tip = ShapeInclinationAnchor;
            // 璧锋墜蹇呴』鏄?RectF.Empty锛堥粯璁ゆ瀯閫犵殑 RectF 鏄?(0,0)-(0,0)锛屼細鎶婄敾闈㈠師鐐圭畻杩涙潵锛?            var drawBox = RectF.Empty;
            drawBox.Add(from.X, from.Y);
            drawBox.Add(tip.X, tip.Y);
            drawBox = drawBox.Inflate(30f * DpiScale);
            // 璇绘暟鏍囩鎸傚湪"姝ｅ湪鎷栫殑閭ｄ竴绔?涓婃柟锛岄珮搴?30 閫昏緫 + 涓€娈甸棿璺濓細涓€璧峰湀杩涚敾闈?            drawBox.Add(tip.X - 80f * DpiScale, tip.Y - 60f * DpiScale);
            drawBox.Add(tip.X + 80f * DpiScale, tip.Y);
            Console.WriteLine($"鐢荤嚎涓細伪 = {ShapeInclinationDegrees:F1}掳锛堝惛浣?{ShapeInclinationSnapped}锛?
                            + $"锛岃捣鐐?{from}锛岀瑪灏?{tip}"
                            + $"锛屽嚭鍥捐寖鍥?({drawBox.MinX:F0},{drawBox.MinY:F0})-({drawBox.MaxX:F0},{drawBox.MaxY:F0})");
            if (!OffscreenFloatingShot(path, drawBox)) Console.WriteLine("鍑哄浘澶辫触");

            SendMouse((int)tipTarget.X, (int)tipTarget.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(120);
            _quit = true;
            return;
        }

        var line = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
        };
        line.AddPoint(cx - 420f, cy + 220f, 1f, 0);
        line.AddPoint(cx + 420f, cy - 220f, 1f, 0);
        Doc.AddStroke(line);
        Doc.SelectOnly(new[] { line });
        Tool = Tool.Marquee;
        Doc.InvalidateAll();
        SettleFrames(700);

        if (!drag)
        {
            // 鑼冨洿 = 閫変腑妗?鈭?鏃嬭浆鏌?鈭?鎿嶄綔鏉★紙鎿嶄綔鏉′篃鐢?DrawSelection 鐢伙紝
            // 涓嶅苟杩涙潵浼氳瑁佹帀涓€鍗婏級锛屽啀鐣欎竴鍦堢粰鎶曞奖銆?            var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var bar = SelectionHandles.BarRect(aabb, DpiScale, ViewportCanvas);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate,
                                                       SelectionHandles.FrameOf(Doc.Selected), DpiScale);
            var region = aabb;
            region.Add(bar);
            region.Add(grip.X - 40f, grip.Y - 40f);
            region.Add(grip.X + 40f, grip.Y + 40f);
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("鍑哄浘澶辫触");
            _quit = true;
            return;
        }

        // 鐪熸嫋涓€娆＄鐐癸細鍘熷鏂瑰悜 30.5掳锛埪?掳 瀹瑰樊鍐咃級鈫?璇绘暟璇ユ樉绀?伪 = 30.0掳銆?        // 鐢ㄧ湡杈撳叆锛堣€屼笉鏄洿鎺ユ憜鍐呴儴鐘舵€侊級锛屽嚭鐨勫浘鎵嶆槸浜у搧閲岄偅涓€甯с€?        var eFixed = SelectionHandles.EndpointCanvasPosition(line, 1);
        var eDrag = SelectionHandles.EndpointCanvasPosition(line, 0);
        float theta = 149.5f * MathF.PI / 180f;
        var to = new Vector2(eFixed.X + 700f * MathF.Cos(theta), eFixed.Y + 700f * MathF.Sin(theta));

        SendMouse((int)eDrag.X, (int)eDrag.Y, 0);                          SettleFrames(60);
        SendMouse((int)eDrag.X, (int)eDrag.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
        for (int i = 1; i <= 4; i++)
        {
            SendMouse((int)(eDrag.X + (to.X - eDrag.X) * i / 4f),
                      (int)(eDrag.Y + (to.Y - eDrag.Y) * i / 4f), 0);
            SettleFrames(40);
        }
        SettleFrames(200);

        var preview = VertexPreviewCanvasPoint;
        // 璧锋墜蹇呴』鏄?RectF.Empty锛氶粯璁ゆ瀯閫犵殑 RectF 鏄?(0,0)-(0,0)锛?        // 鐩存帴 Add 杩涘幓浼氭妸鐢婚潰鍘熺偣涔熺畻杩涜寖鍥达紙鍑哄浘涓€鍗婃槸绌虹櫧灏辨槸瀹冿級銆?        var box = RectF.Empty;
        box.Add(eFixed.X, eFixed.Y);
        box.Add(preview.X, preview.Y);
        box = box.Inflate(40f * DpiScale);
        // 璇绘暟鏍囩鎸傚湪琚嫋绔偣涓婃柟锛岄珮搴?30 閫昏緫 + 涓€娈甸棿璺濓細鎶婂畠涓€璧峰湀杩涚敾闈?        box.Add(preview.X - 70f * DpiScale, preview.Y - 60f * DpiScale);
        box.Add(preview.X + 70f * DpiScale, preview.Y);
        Console.WriteLine($"鎷栫鐐逛腑锛毼?= {VertexReadoutValue:F1}掳锛堝惛浣?{VertexInclinationSnapped}锛夛紝"
                        + $"鍥哄畾绔?{eFixed}锛屾嫋鍔ㄧ {preview}"
                        + $"锛堟ā鍨嬮噷杩欐潯绾胯繕娌″姩锛氬ⅷ杩硅寖鍥?{(int)line.WorldInkBounds.MinX},{(int)line.WorldInkBounds.MinY}"
                        + $"-{(int)line.WorldInkBounds.MaxX},{(int)line.WorldInkBounds.MaxY}锛夛紝"
                        + $"鍑哄浘鑼冨洿 ({box.MinX:F0},{box.MinY:F0})-({box.MaxX:F0},{box.MaxY:F0})");
        if (!OffscreenFloatingShot(path, box)) Console.WriteLine("鍑哄浘澶辫触");

        SendMouse((int)to.X, (int)to.Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(120);
        _quit = true;
    }

    /// <summary>
    /// 鎶樿濉厖鑷锛?*鎷愯澶勪笉鑳芥帀鑹?*锛堝啓"L""V""7""鈭?杩欑被鎶樼瑪鏃讹紝濉厖鍦?    /// 鑷氦澶勮鎸栫┖鐨勯偅涓€?bug锛夈€?    ///
    /// 鍒ゆ嵁鍦?2026-09-14 閲嶅啓杩囷紝鍥犱负**鏃у垽鎹嚜宸辨湁闂**锛氬畠鎷?鎷愯绐楀彛鐨勫ⅷ閲?
    /// 姣?鐩存绐楀彛鐨勫ⅷ閲?锛岃姹傚墠鑰?鈮?鍚庤€呯殑 95%銆傚彲鏄嫄瑙掔獥鍙ｉ噷鏈潵灏辨湁 3/4 鐨?    /// 鍦版柟娌″ⅷ锛堜袱涓噦鍒嗗埆浠庣獥鍙ｇ殑宸﹁竟鍜屼笅杈瑰嚭鍘伙級锛屾墍浠ヨ繖鏉¤嚜妫€浠庡啓涓嬫潵閭ｅぉ璧?    /// 灏辨槸绾㈢殑鈥斺€斾竴鏉℃案杩滅孩鐨勮嚜妫€绛変簬娌℃湁鑷锛屽畠鎶婄湡姝ｇ殑淇″彿娣规帀浜嗐€?    ///
    /// 鐜板湪鐨勫垽鎹洿鎺ュ鐫€**瑕侀槻鐨?bug**锛氭部绗旇抗涓績绾垮彇涓€涓查噰鏍风偣锛堝惈鎷愯鏈韩锛夛紝
    /// 姣忎釜鐐瑰湪濉厖閲岄兘蹇呴』鏈夊ⅷ锛團illContainsPoint锛夈€傛嫄瑙掕鎸栫┖ 鈫?鎷愯閭ｄ釜鐐?    /// 灏辨槸绌虹殑 鈫?鎶ョ孩銆傚彟鍔犱竴鏉?涓婂睆鍏滃簳"锛堢櫧鏉?+ 鎷愯绐楀彛鏁伴粦鍍忕礌锛夛紝
    /// 鍥犱负"鍑犱綍瀵逛絾娌＄敾鍒板睆骞曚笂"涔熸槸涓€绉嶅彲鑳姐€?    /// </summary>
    /// <summary>
    /// 鑷氦鍙犺壊鑷锛?*涓€绗旇嚜宸辩┛杩囪嚜宸辩殑鍦版柟锛岄鑹蹭笉鑳藉彉娣?*銆?    ///
    /// 涓轰粈涔堝崟鐙嫀鍑烘潵楠岋細鎴戜滑鐢荤殑鏄?涓€鏉￠棴鍚堣疆寤?+ 涓€娆″～鍏?锛岀悊璁轰笂鑷氦澶勫彧浼氳
    /// Winding 璁℃暟鍙樻垚 2锛屼粛鐒跺彧鍚堟垚涓€娆★紱浣嗕腑闂存湁涓€閬撳伐搴忓彲鑳界牬鍧忚繖涓墠鎻愨€斺€?    /// **鍑犱綍缁嗗垎缂撳瓨**锛坄CreateFilledGeometryRealization`锛夊鏋滄寜涓夎褰㈤€愪釜鍚堟垚锛?    /// 閲嶅彔鍖哄氨浼氳鍚堟垚涓ゆ銆傜敤鎴锋姤鐨?鐢昏鏃跺唴閮ㄥ彉娣?姝ｆ槸杩欎釜褰㈢姸锛屾墍浠ヨ涓撻棬鐩€?    ///
    /// 鍒ゆ嵁锛氬湪鍚屼竴绗旂殑"鑷氦閲嶅彔鍖?鍜?鏅€氬尯"鍚勫彇涓€涓儚绱狅紝棰滆壊蹇呴』鐩稿悓
    /// 锛堝崐閫忔槑鑽у厜绗旀渶鏁忔劅鈥斺€斿彔涓ゆ涓€鐪煎氨鑳界湅鍑烘潵锛夈€?    /// </summary>
    // ------------------------------------------------------------------
    //  绂诲睆娓叉煋灏忓伐鍏?    //
    //  閲?棰滆壊浼氫笉浼氬彉娣?杩欑被浜嬩笉鑳介潬鎶撳睆锛氭姄灞忓湪閿佸睆 / 杩滅▼浼氳瘽 / 琚埆鐨勭獥鍙ｇ洊浣忔椂
    //  鎷嶅埌鐨勬槸妗岄潰澹佺焊锛屼袱澶勯兘鏄绾?鈫?"宸?0" 鐪嬬潃鍍忛€氳繃锛屽叾瀹炰粈涔堥兘娌￠獙
    //  锛?026-09-15 灏辫楠楄繃涓€娆★級銆傜灞忔覆鏌撲粈涔堟椂鍊欒窇閮戒竴鏍枫€?    // ------------------------------------------------------------------

    /// <summary>寤轰竴瀵圭灞忎綅鍥撅細涓€涓綋娓叉煋鐩爣锛屼竴涓?CPU 鍙锛堣鍥炲儚绱犵敤锛夈€?/summary>
    private (ID2D1Bitmap1 target, ID2D1Bitmap1 cpu) MakeOffscreen(int w, int h)
    {
        var ctx = _windows[0].Context;
        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var target = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        var cpu = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        return (target, cpu);
    }

    /// <summary>鎶婃枃妗ｉ噷鐨勭瑪鐢荤敾杩涚灞忎綅鍥撅紙鐢诲竷鍧愭爣 鈫?浣嶅浘鍧愭爣锛屾寜 -origin 骞崇Щ锛夈€?/summary>
    private void RenderDocToBitmap(ID2D1Bitmap1 target, float originX, float originY)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0.98f, 0.98f, 0.98f, 1f));      // 褰撲綔鐧芥澘搴曡壊
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-originX, -originY);
        foreach (var s in Doc.Strokes) _windows[0].DrawStrokeForTest(s);
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  绂诲睆 EndDraw 澶辫触: " + hr.Description);
        ctx.Target = null;
    }

    /// <summary>鎶婄灞忎綅鍥捐鍥炲唴瀛橈紙BGRA锛夈€?/summary>
    private static byte[] ReadPixels(ID2D1Bitmap1 target, ID2D1Bitmap1 cpu, int w, int h)
    {
        cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
        var m = cpu.Map(MapOptions.Read);
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
        cpu.Unmap();
        return buf;
    }

    private byte[] RenderAndRead(ID2D1Bitmap1 target, ID2D1Bitmap1 cpu, int w, int h,
                                 float originX, float originY)
    {
        RenderDocToBitmap(target, originX, originY);
        return ReadPixels(target, cpu, w, h);
    }

    /// <summary>鍙栦竴灏忓潡鐨勫钩鍧囪壊锛堝弬鏁版槸 BGRA 缂撳啿銆佽瀹藉儚绱犳暟銆佸乏涓婅銆佽竟闀匡級銆?/summary>
    private static (int r, int g, int b) AvgPatch(byte[] buf, int width, int px, int py, int size)
    {
        // 鎺㈤拡钀藉湪缂撳啿澶栭潰鏃?*澶瑰洖鏉?*锛岃€屼笉鏄姏寮傚父锛氳繖鏄噺棰滆壊鐨勫伐鍏凤紝
        // 閲忔浜嗗簲璇ユ姤涓€涓彲鐤戠殑鏁板瓧璁╀汉鍘荤湅锛屼笉璇ユ妸鏁翠釜鑷杩涚▼鎵撴柇銆?        int height = buf.Length / (width * 4);
        px = Math.Clamp(px, 0, Math.Max(0, width - size));
        py = Math.Clamp(py, 0, Math.Max(0, height - size));
        long r = 0, g = 0, b = 0; int n = 0;
        for (int y = py; y < py + size; y++)
            for (int x = px; x < px + size; x++)
            {
                int i = y * width * 4 + x * 4;
                b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; n++;
            }
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>涓や釜棰滆壊鐨勫樊锛堜笁閫氶亾缁濆鍊间箣鍜岋級銆?/summary>
    private static int ColorDiff((int r, int g, int b) a, (int r, int g, int b) b)
        => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);

    /// <summary>
    /// 閫犱竴鏉?鑷繁绌胯繃鑷繁"鐨勮崸鍏夌瑪绗旇抗锛坄--selfcross`銆乣--eraseselfcross`銆佸儚绱犳鐨嚜妫€
    /// 鍏辩敤鍚屼竴鏉★級銆?    ///
    /// **鐪熸鐨勮嚜浜ょ偣**锛氫袱娈典腑蹇冪嚎鑱旂珛瑙ｅ嚭鏉ユ槸鐩稿 (161.7, 20.9)锛屼笉鏄?(200, 0)鈥斺€?    /// 鍚庤€呯浜ょ偣 40 鍍忕礌锛岄偅鍎垮彧鏈変竴鑲″ⅷ锛屾嬁瀹冨仛鍒ゆ嵁绛変簬浠€涔堥兘娌￠獙锛堣€佺敤渚嬪氨鏄繖涔堥敊鐨勶級銆?    /// </summary>
    private static Stroke MakeSelfCrossingHighlight(float cx, float cy, float width)
    {
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = InkPalette.HighlighterDefault, Width = width,
        };
        var raw = new List<Vector2>
        {
            new(cx, cy), new(cx + 120, cy), new(cx + 240, cy + 60),
            new(cx + 240, cy + 220), new(cx + 60, cy + 260),
            new(cx - 20, cy + 120), new(cx + 200, cy),          // 鈫?绌胯繃绗竴娈?            new(cx + 330, cy - 150),
        };
        for (int i = 0; i + 1 < raw.Count; i++)
            for (int k = 0; k < 20; k++)
            {
                float t = k / 20f;
                s.AddPoint(raw[i].X + (raw[i + 1].X - raw[i].X) * t,
                           raw[i].Y + (raw[i + 1].Y - raw[i].Y) * t, 0.5f, i * 20 + k);
            }
        s.AddPoint(raw[^1].X, raw[^1].Y, 0.5f, 999);
        return s;
    }

    /// <summary>
    /// 姗＄毊鎵嬫祴鍙帮紙--eraserlab [鏃ュ織鍓嶇紑]锛夈€?    ///
    /// **杩欐槸缁欎汉鐢ㄧ殑锛屼笉鏄嚜妫€**锛氶摵涓€灞?鍙摝鐨勪笢瑗?锛屾墦寮€姗＄毊閬ユ祴锛岀劧鍚庢妸鎺у埗鏉冧氦鍥?    /// 寮曟搸鐨勬甯告秷鎭惊鐜紝鐢ㄦ埛鎷块紶鏍囪嚜宸辨摝銆傛瘡鏉℃嫋鎷界粨鏉熺珛鍒昏惤涓€琛?CSV锛岄€€鍑烘椂鍐欐眹鎬汇€?    ///
    /// 涓轰粈涔堣鏈夊畠锛氭墜鎰熴€佷範鎯€佽鎿﹁繖浜涗簨鍙湁鐪熶汉鐢ㄧ湡鎵嬫墠鐭ラ亾銆傝嚜妫€鑳借瘉鏄?鍒囧緱瀵广€?    /// 鎾ら攢涓€姝ャ€佷笂灞忓儚绱犲"锛岃瘉鏄庝笉浜?杩欎釜灏哄椤烘墜""杩欎竴鎿︽垜鍏跺疄涓嶆兂鎿?銆?    ///
    /// 鏍蜂緥閲?*鏁呮剰鏀句簡涓夌被瀵硅薄**鈥斺€旂瑪杩癸紙浼氳鍒囧紑锛夈€佽崸鍏夌瑪锛堣嚜浜わ紝鐪嬮鑹蹭細涓嶄細鍙樻繁锛夈€?    /// 鍥惧舰锛堢鍒拌疆寤撴暣鏉″垹锛夛紝涓€灞忓氨鑳芥妸涓夌琛屼负閮借瘯鍒般€?    /// </summary>
    /// <summary>
    /// 瀹為獙锛?-eraseimage锛夛細**鐢婚潰閲屾湁涓€寮犲ぇ鍥炬椂锛屾摝闄や竴姝ヨ澶氫箙**銆?    ///
    /// 鏌ョ殑鏄敤鎴锋姤鐨?鎿﹀浘鍍忎笉娴佺晠"銆傚垎鍧楃紦瀛樼殑閲嶇敾鏄?鏁村潡娓呮帀鍐嶇敾涓€閬嶅潡鍐呯殑鎵€鏈夊璞?锛?    /// 鑰屽浘鍍忓璞＄敾鐨勬槸**涓€寮犱綅鍥?*鈥斺€斿潡閲屾湁鍥撅紝杩欎竴鍧楁瘡娆￠噸鐢婚兘瑕佹妸鍥鹃噸鏂拌创涓€閬嶃€?    /// 鎵€浠ュ浘瓒婂ぇ銆佹鐨法杩囩殑鍧楄秺澶氾紝涓€姝ユ摝闄ゅ氨瓒婅吹銆傝繖閲屽仛 A/B锛氬悓涓€涓叉摝闄ゅ姩浣滐紝
    /// 涓€娆″湪"鏈夊浘 + 鍥句笂鏈夊ⅷ"鐨勭増闈笂璺戯紝涓€娆″湪"娌℃湁鍥?鐨勭増闈笂璺戙€?    /// </summary>
    /// <summary>
    /// 鎺㈤拡锛?-imageerase锛夛細**涓ょ姗＄毊纰板埌鍥惧儚瀵硅薄鏃跺垎鍒細鎬庢牱**銆?    ///
    /// 鐢ㄦ埛鎶?鎿﹀浘鍍忎笉娴佺晠"锛屽厛鎶?鍒板簳浼氬彂鐢熶粈涔?閽夋锛?    ///   路 鍍忕礌姗＄毊锛圕trl+Alt+7锛夛細鎸夎璁?*涓嶇**鍥惧儚锛堜唬鐮侀噷 `if (s.IsImage) continue`锛夛紱
    ///   路 鏁寸瑪姗＄毊锛圕trl+Alt+4锛夛細璧?`IsShape` 鍒嗘敮 鈫?`HitTestExact` 鈫?鍥惧儚**鏁存潯鍒犳帀**銆?    /// 杩欎袱涓笉涓€鏍凤紝鏄巻鍙查仐鐣欙紙鍥惧儚鏄悗鏉ュ姞鐨勶紝鏁寸瑪姗＄毊閭ｆ潯鍒嗘敮娌¤窡鐫€鏀癸級銆?    /// </summary>
    private void ImageEraseProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 鎺㈤拡锛氫袱绉嶆鐨鍒板浘鍍忓璞℃椂浼氭€庢牱 ===");

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        Doc.Clear();
        Doc.ClearHistory();
        var img = Doc.AddImage(MakeTestImage(400, 300), cx - 200, cy - 150, 1f);
        Console.WriteLine($"  鍏堟斁涓€寮?400脳300 鐨勫浘锛堝璞℃暟 {Doc.Strokes.Count}锛夛紝"
                        + "姗＄毊钀界偣鎵撳湪鍥惧儚姝ｄ腑");

        int hit = Doc.EraseRectAt(cx, cy, 40f, 40f);          // 鍍忕礌姗＄毊鐨勮皟鐢?        Console.WriteLine($"  鍍忕礌姗＄毊鎿︿竴涓嬶細鍙楀奖鍝?{hit} 鏉★紝鍥捐繕鍦ㄥ悧 "
                        + $"{(Doc.Strokes.Contains(img) ? "鍦? : "**娌′簡**")}"
                        + $"锛堝璞℃暟 {Doc.Strokes.Count}锛?);

        int removed = Doc.EraseAt(cx, cy, 40f);               // 鏁寸瑪姗＄毊鐨勮皟鐢?        Console.WriteLine($"  鏁寸瑪姗＄毊鎿︿竴涓嬶細鍒犳帀 {removed} 鏉★紝鍥捐繕鍦ㄥ悧 "
                        + $"{(Doc.Strokes.Contains(img) ? "鍦? : "**娌′簡**")}"
                        + $"锛堝璞℃暟 {Doc.Strokes.Count}锛?);

        Console.WriteLine("image-erase probe done");
        _quit = true;
    }

    private void EraseImageProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 瀹為獙锛氱敾闈㈤噷鏈夊ぇ鍥炬椂锛屾摝闄や竴姝ョ殑浠ｄ环 ===");

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        const int Steps = 25;

        double Run(bool withImage, out double patchMax, out int tiles, out int drawn, out double selfMs)
        {
            Doc.Clear();
            Doc.ClearHistory();
            // 鎸?*鎴浘**鐨勫疄闄呭昂瀵告潵锛氫竴寮犲叏灞忔埅鍥惧氨鏄櫄鎷熸闈㈤偅涔堝ぇ锛?880脳1800 = 20MB锛夈€?            int iw = (int)_virtualW, ih = (int)_virtualH;
            if (withImage) Doc.AddImage(MakeTestImage(iw, ih), _virtualX, _virtualY, 1f);

            // 鍥句笂鐢诲嚑琛岀瑪杩癸紙鑰佸笀鍦ㄥ浘/PPT 涓婂湀鐢伙級锛屾鐨氨鎿﹁繖浜?            var rnd = new Random(5);
            for (int k = 0; k < 6; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0.9f, 0.15f, 0.15f, 1f), Width = 8f * DpiScale };
                float y = cy - 400 + k * 130;
                for (int i = 0; i <= 60; i++)
                {
                    float t = i / 60f;
                    s.AddPoint(cx - 700 + t * 1400, y + MathF.Sin(t * 7f + k) * 12f, 0.9f, i);
                }
                Doc.AddStroke(s);
            }
            Doc.InvalidateAll();
            SettleFrames(500);

            double sum = 0, mx = 0, self = 0;
            int tl = 0, dr = 0;
            float bx = cx - 600, by = cy - 400;
            for (int i = 0; i < Steps; i++)
            {
                bx += 48; by += 32;
                var sw = Stopwatch.StartNew();
                Doc.EraseRectAt(bx, by, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                sw.Stop();
                self += sw.Elapsed.TotalMilliseconds;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                sum += _windows[0].LastPatchMs;
                if (_windows[0].LastPatchMs > mx) mx = _windows[0].LastPatchMs;
                tl += _windows[0].LastPatchCount;
                dr += _windows[0].LastDrawnStrokes;
            }
            patchMax = mx; tiles = tl; drawn = dr; selfMs = self;
            return sum / Steps;
        }

        double noImg = Run(false, out var mxA, out var tlA, out var drA, out var selfA);
        double withImg = Run(true, out var mxB, out var tlB, out var drB, out var selfB);

        Console.WriteLine($"  娌℃湁鍥撅細閲嶇敾 {noImg,6:F2} ms/姝ワ紙宄板€?{mxA,5:F2}锛?
                        + $"銆€{TL(tlA)} 鍧?姝ャ€€{DR(drA)} 绗?姝ャ€€鎿﹂櫎鏈韩 {selfA / Steps,5:F2} ms/姝?);
        Console.WriteLine($"  鏈? 鍥撅細閲嶇敾 {withImg,6:F2} ms/姝ワ紙宄板€?{mxB,5:F2}锛?
                        + $"銆€{TL(tlB)} 鍧?姝ャ€€{DR(drB)} 绗?姝ャ€€鎿﹂櫎鏈韩 {selfB / Steps,5:F2} ms/姝?);
        Console.WriteLine($"  缁撹锛氬甫鍥炬椂涓€姝ユ參 {withImg - noImg:F2} ms锛堥噸鐢婚儴鍒嗭級");
        string TL(int t) => (t / (double)Steps).ToString("F1");
        string DR(int d) => (d / (double)Steps).ToString("F0");
        Console.WriteLine("erase-image probe done");
        _quit = true;
    }

    private void EraserLab(string prefix, bool keepSystemCursor = false)
    {
        string stamp = DateTime.Now.ToString("MMdd-HHmmss");
        string root = string.IsNullOrWhiteSpace(prefix) ? $"reports/姗＄毊-鎵嬫祴-{stamp}" : prefix;

        Doc.Clear();
        Doc.ClearHistory();

        float L = DpiScale;                       // 閫昏緫 鈫?鐗╃悊
        float X(float logical) => _virtualX + logical * L;
        float Y(float logical) => _virtualY + logical * L;
        var ink = new Color4(0.08f, 0.09f, 0.12f, 1f);

        // 鈶?涓よ"鏉夸功"锛氭瘡琛屼笁鏉￠暱绗旇抗锛堟ā鎷熶竴琛岃繛鐫€鍐欎笅鏉ワ級
        for (int row = 0; row < 2; row++)
        {
            float yy = Y(320f + row * 80f);
            for (int seg = 0; seg < 3; seg++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = 5f * L };
                float x0 = X(90f + seg * 420f), x1 = X(90f + seg * 420f + 395f);
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f;
                    s.AddPoint(x0 + (x1 - x0) * t,
                               yy + MathF.Sin(t * 9f + seg * 2f) * 7f * L, 0.9f, i);
                }
                Doc.AddStroke(s);
            }
        }

        // 鈶?涓変釜"瀛?锛氫笁妯竴绔栤€斺€?鏀逛竴涓瓧瑕侀噸鍐欎竴鏁磋"閭ｇ鍦烘櫙锛堟暣绗旀摝浼氳繛甯︽暣琛岋級
        for (int ch = 0; ch < 3; ch++)
        {
            float x0 = X(140f + ch * 220f), y0 = Y(470f);
            for (int k = 0; k < 3; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = 6f * L };
                s.AddPoint(x0, y0 + k * 26f * L, 1f, 0);
                s.AddPoint(x0 + 120f * L, y0 + k * 26f * L, 1f, 1);
                Doc.AddStroke(s);
            }
            var v = new Stroke { Tool = Tool.Pen, Color = ink, Width = 6f * L };
            v.AddPoint(x0 + 60f * L, y0 - 12f * L, 1f, 0);
            v.AddPoint(x0 + 60f * L, y0 + 64f * L, 1f, 1);
            Doc.AddStroke(v);
        }

        // 鈶?涓€鏉¤嚜浜ょ殑鑽у厜绗旓細鎿︿竴鍒€锛岀湅浜ゅ彔澶勪細涓嶄細鍙樻繁锛堣繖姝ｆ槸杩欑増鏀瑰姩鐨勬牳蹇冩敹鐩婏級
        Doc.AddStroke(MakeSelfCrossingHighlight(X(880f), Y(360f), 26f * L));

        // 鈶?鍥惧舰锛氱鍒拌疆寤撴暣鏉″垹锛堟鐨粠姝ｄ腑闂村垝杩囦笉璇ュ垹锛?        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.85f, 0.2f, 0.2f, 1f), Width = 5f * L,
        };
        rect.AddPoint(X(790f), Y(520f), 1f, 0);
        rect.AddPoint(X(1090f), Y(620f), 1f, 1);
        Doc.AddStroke(rect);

        var arrow = new Stroke
        {
            Tool = Tool.Arrow, Kind = StrokeKind.Arrow,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * L,
        };
        arrow.AddPoint(X(1160f), Y(600f), 1f, 0);
        arrow.AddPoint(X(1360f), Y(530f), 1f, 1);
        Doc.AddStroke(arrow);

        // 鈶?涓€鍧?鎴浘鍖?锛氭ā鎷熻€佸笀鎴簡 PPT/鍥撅紝鐒跺悗鍦ㄥ浘涓婂湀鐢汇€?        //    鐢ㄥ畠璇曚袱浠朵簨鈥斺€?*鍥句笂鐨勫ⅷ鑳芥甯告摝鎺?*锛岃€?*鍥炬湰韬笉璇ヨ姗＄毊鍚冩帀**
        //    锛堝浘鍍忓璞℃寜璁捐涓嶅弬涓庢摝闄わ紝瑕佸垹瀹冪敤妗嗛€?+ Delete锛夈€?        Doc.AddImage(MakeTestImage(640, 220), X(90f), Y(660f), DpiScale);
        for (int k = 0; k < 3; k++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0.9f, 0.15f, 0.15f, 1f), Width = 4f * L };
            float y0 = Y(690f + k * 60f);
            for (int i = 0; i <= 30; i++)
            {
                float t = i / 30f;
                s.AddPoint(X(120f + t * 560f), y0 + MathF.Sin(t * 8f + k) * 6f * L, 0.9f, i);
            }
            Doc.AddStroke(s);
        }

        // 榛樿灏辩珯鍦ㄥ儚绱犳鐨笂鈥斺€旇娴嬬殑灏辨槸瀹冦€傛暣绗旀鐨?Ctrl+Alt+4锛屼竴閿崲鍥炴潵銆?        Tool = Tool.PixelEraser;
        ShowHud = true;                 // 闈㈡澘涓婃湁涓€琛?璁板綍涓?N 鏉?锛岀敤鎴风湅寰楄纭疄鍦ㄨ
        PassThrough = false;
        BoardOn = false;                // 閫忔槑鎵规敞锛氳兘鐪嬭搴曚笅鐨勪笢瑗匡紝鍜屼笂璇句竴鏍?        Doc.InvalidateAll();
        _dirty = true;

        EraserTelemetry = new EraserTelemetry(root + ".csv", root + ".txt", NowMs);
        if (keepSystemCursor)
        {
            EraserKeepsSystemCursor = true;
            Console.WriteLine("  宸叉墦寮€瀵圭収锛氱郴缁熺澶翠篃浼氭樉绀猴紙鍜岃嚜缁樻柟鍧楀姣旇窡鎵嬬▼搴︼級");
        }

        Console.WriteLine();
        Console.WriteLine($"=== 姗＄毊鎵嬫祴鍙?===  鏃ュ織锛歿Path.GetFullPath(root)}.csv / .txt");
        Console.WriteLine("  宸查摵鏍蜂緥锛氫袱琛屾澘涔︺€佷笁涓瓧銆佷竴鏉¤嚜浜よ崸鍏夌瑪銆佷竴涓煩褰竴涓澶?);
        Console.WriteLine("  宸︿笅瑙掕繕鏈変竴鍧椼€屾埅鍥惧尯銆嶏細鍥句笂鏈変笁鏉℃壒娉紝璇曡瘯鍥句笂鐨勫ⅷ鑳戒笉鑳芥甯告摝銆佸浘鏈韩浼氫笉浼氳鍚冩帀");
        Console.WriteLine("  褰撳墠宸ュ叿锛氬儚绱犳鐨?Ctrl+Alt+7锛堟暣绗旀鐨?4銆佺瑪 1銆佹挙閿€ Z銆?
                        + "鎹㈡鐨ぇ灏?6銆侀€€鍑?X锛?);
        SettleFrames(300);
    }

    /// <summary>
    /// 瀹為獙 / 鍥炲綊锛?-eraseselfcross锛夛細**鍒囦竴鍒€涔嬪悗锛岃崸鍏夌瑪鑷氦澶勪細涓嶄細鍙樻繁**銆?    ///
    /// 2026-09-15锛氬儚绱犳鐨敼鎴?鍦ㄤ竴鏉＄瑪杩逛笂璁版摝闄ゅ尯闂?涔嬪悗锛岃繖涓疄楠屼粠"鎶ユ暟瀛?鍙樻垚
    /// 浜?*鍥炲綊鍒ゆ嵁**锛堝樊蹇呴』鏄?0锛夆€斺€斿畠姝ｆ槸閭ｄ竴鐗堟敼鍔ㄥ瓨鍦ㄧ殑鐞嗙敱銆傚悓涓€浠朵簨鍦?    /// `--pixelerasetest` 閲屼篃浼氶獙涓€娆°€?    ///
    /// 涓轰粈涔堝€煎緱鍗曠嫭閲忥細`--selfcross` 瀹堢潃"涓€绗旇嚜宸辩┛杩囪嚜宸辨椂棰滆壊涓嶈兘鍙樻繁"杩欐潯涓嶅彉閲忥紝
    /// 鑰屽畠鏄?*涓€娆?DrawGeometry** 鐨勬€ц川鈥斺€擠2D 鎶婁竴鏉℃弿杈瑰綋鎴愪竴涓暣浣撳～鍏呬竴娆°€?    /// 鍍忕礌姗＄毊鎶婁竴鏉＄瑪杩?*鎷嗘垚涓や釜瀵硅薄**涔嬪悗灏卞彉鎴愪袱娆″～鍏咃紝涓ゆ澧ㄥ湪绌洪棿涓婇噸鍙犵殑鍦版柟
    /// 浼氬悇鐢讳竴娆★細鍗婇€忔槑鐨勮崸鍏夌瑪鍙犱袱娆★紝浜ゅ彔澶勫氨娣变簡銆?    ///
    /// 杩欎笉鏄?鐪嬬潃宸笉澶?鐨勪簨锛岃兘琚儚绱犺瘉浼€傛墍浠ヨ繖閲屽湪**绂讳氦鐐瑰緢杩滅殑鍦版柟**鍒囦竴鍒€锛?    /// 璁╀簰鐩哥┛杩囩殑閭ｄ袱鑲″ⅷ钀藉湪涓嶅悓鐨勭鐗囬噷锛屽啀閲忎氦鐐硅壊鍜屾櫘閫氬鑹茬殑宸€?    /// 鍙姤鏁板瓧銆佷笉鍒ょ孩缁匡紙瀹冩槸鍦ㄩ獙璇佷竴涓璁″彇鑸嶏紝涓嶆槸鍥炲綊鐢ㄤ緥锛夈€?    /// </summary>
    private void EraseSelfCrossProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 瀹為獙锛氬垏娈典箣鍚庤嚜浜ゅ浼氫笉浼氬彉娣憋紙鍗婇€忔槑鑽у厜绗旓級===");

        Doc.Clear();
        Doc.ClearHistory();

        const int W = 800, H = 800;
        var (target, cpu) = MakeOffscreen(W, H);
        byte[] Render() => RenderAndRead(target, cpu, W, H, _virtualX, _virtualY);

        float cx = _virtualX + 500f, cy = _virtualY + 500f;
        Doc.AddStroke(MakeSelfCrossingHighlight(cx, cy, 40f));
        (int r, int g, int b) Avg(byte[] buf, int px, int py, int size)
            => AvgPatch(buf, W, px, py, size);
        // **鐪熸鐨勮嚜浜ょ偣鍦ㄥ摢鍎?*锛氭妸涓ゆ涓績绾胯В鍑烘潵鎵嶇煡閬撱€?        //   A: P1(120,0) 鈫?P2(240,60)锛堢涓€娈甸偅鏉℃í绾跨殑鏂滃熬宸达級
        //   B: P5(-20,120) 鈫?P6(200,0)锛堢粫鍥炴潵閭ｄ竴鑲★級
        //   A(u) = (120+120u, 60u)銆丅(t) = (-20+220t, 120-120t)
        //   60u = 120-120t 鈫?u = 2-2t锛涗唬杩?x锛?20+220t = 360-240t 鈫?t = 0.826, u = 0.348
        //   鈫?浜ょ偣 鈮?(161.7, 20.9)
        //
        // 椤哄甫涓€鎻愶細鍘熸潵閭ｆ潯 --selfcross 鐢ㄤ緥鎺㈢殑鏄浉瀵?(200, 0)锛岀杩欓噷 40 鍍忕礌鈥斺€?        // 閭ｅ効鍏跺疄鍙湁涓€鑲″ⅷ锛屼袱杈归鑹插綋鐒朵竴鏍凤紝閭ｆ潯鍒ゆ嵁鏄?*绌虹殑**锛堣鏂囨。锛夈€?        int crossX = (int)(cx + 161.7f - _virtualX) - 8;
        int crossY = (int)(cy + 20.9f - _virtualY) - 8;
        int plainX = (int)(cx + 60f - _virtualX) - 8;
        int plainY = (int)(cy - _virtualY) - 8;

        var buf0 = Render();
        var cross0 = Avg(buf0, crossX, crossY, 17);
        var plain0 = Avg(buf0, plainX, plainY, 17);
        int Diff((int r, int g, int b) a, (int r, int g, int b) b) => ColorDiff(a, b);
        int diff0 = Diff(cross0, plain0);
        Console.WriteLine($"  涓€鍒€娌″垏锛坽Doc.Strokes.Count} 涓璞★級锛氫氦鍙犲 ({cross0.r},{cross0.g},{cross0.b})"
                        + $" vs 鏅€氬 ({plain0.r},{plain0.g},{plain0.b})  宸?{diff0}");

        // 鍦ㄥ彸渚ч偅鏉＄珫杈逛笂鍒囦竴鍒€锛堢浜ょ偣 240 鍍忕礌浠ヤ笂锛夛細
        // 鎿﹂櫎鍖洪棿璁板湪**鍚屼竴鏉＄瑪杩?*涓婏紝鎵€浠ヤ簰鐩哥┛杩囩殑閭ｄ袱鑲″ⅷ杩樺湪鍚屼竴娆?DrawGeometry 閲屻€?        int before = Doc.Strokes.Count;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx + 240, cy + 140, 20f, 30f);
        Doc.EndErase();

        var buf1 = Render();
        var cross1 = Avg(buf1, crossX, crossY, 17);
        var plain1 = Avg(buf1, plainX, plainY, 17);
        int diff1 = Diff(cross1, plain1);
        Console.WriteLine($"  鍒囦竴鍒€鍚庯紙{Doc.Strokes.Count} 涓璞★級锛氫氦鍙犲 ({cross1.r},{cross1.g},{cross1.b})"
                        + $" vs 鏅€氬 ({plain1.r},{plain1.g},{plain1.b})  宸?{diff1}");
        Console.WriteLine($"  瀵硅薄鏁?{before} 鈫?{Doc.Strokes.Count}"
                        + $"锛涗氦鍙犲姣斿垏涔嬪墠娣变簡 {diff1 - diff0}锛? = 娌″彉娣憋級");

        target.Dispose();
        cpu.Dispose();
        Console.WriteLine("erase-selfcross probe done");
        _quit = true;
    }
    private bool _benchPressure;

    /// <summary>`--benchpts N`锛氬熀鍑嗛噷姣忕瑪鍑犱釜鐐癸紙0 = 鑰佽涓猴細闅忔満 8~31锛夈€?    ///
    /// 涓轰粈涔堣鏈夊畠锛氳€佺殑"涓€涓囩瑪"姣忕瑪骞冲潎 19.5 涓偣锛?    /// 鑰?*鐢ㄦ埛鐪熸満鍐欏瓧鏄?139~211 涓偣**锛坮aw 琛ョ偣涔嬪悗锛夆€斺€?鑰佸熀鍑嗘瘮鐪熷疄鐢ㄦ硶杞讳簡杩?9 鍊嶃€?    /// 瑕佺瓟"涓€涓囩瑪鍒板簳鍚冨灏?锛屽緱鎸夌湡瀹炵偣瀵嗗害閲忋€?/summary>
    private int _benchPts;

    /// <summary>`--benchspread N`锛氫竴涓囩瑪**鍒嗘暎鍒?N 灞?*锛? = 鑰佽涓猴細鍏ㄦ尋鍦ㄤ竴灞忥級銆?    /// 鐪熷疄鏉夸功鏄竴鑺傝寰€涓嬪啓鍗佸嚑灞忥紝姣忓睆鍙湁閭ｄ箞澶氬瓧锛涘叏鎸や竴灞忔槸鏈€鏋佺鐨勫舰鐘讹紝
    /// 閲忓嚭鏉ョ殑閲嶉摵浠ｄ环浼氬ぇ寰楀锛屾嬁瀹冨綋"涓€鑺傝鐨勬秷鑰?浼氬悡浜恒€?/summary>
    private int _benchSpread;

    private void Benchmark(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== benchmark: {strokeCount} strokes"
                          + (_benchSpread > 0 ? $" spread over {_benchSpread} screens" : " on one screen")
                          + (_benchPressure ? " (with pressure)" : "")
                          + (_benchPts > 0 ? $" ({_benchPts} pts/stroke)" : "") + " ===");

        var gen = Stopwatch.StartNew();
        // 榛樿閭ｄ竴灞忔槸**鏈€鏋佺鐨勫舰鐘?*锛堜竴涓囩瑪鍏ㄦ尋鍦ㄤ竴灞忥級锛涚湡瀹炴澘涔︽槸涓€鑺傝鍒嗘暎鍗佸嚑灞忋€?        // `--benchspread N` 璧板垎鏁ｇ増锛岄噺鐨勬墠鏄?浣庨厤鏈轰笂涓€鑺傝"閭ｄ釜褰㈢姸銆?        if (_benchSpread > 0) GenerateStrokesSpread(strokeCount, _benchSpread);
        else GenerateStrokes(strokeCount);
        gen.Stop();

        var buildSw = Stopwatch.StartNew();
        foreach (var s in Doc.Strokes) s.BuildGeometry(Gfx.D2DFactory);
        buildSw.Stop();

        // Force a full content rebuild so the next frames re-rasterise everything.
        Doc.InvalidateAll();
        _dirty = true;

        RenderAll();
        var w0 = _windows[0];
        double rebuildMs = w0.LastRebuildMs;

        Console.WriteLine($"  generate      : {gen.Elapsed.TotalMilliseconds,8:F1} ms");
        Console.WriteLine($"  build geometry: {buildSw.Elapsed.TotalMilliseconds,8:F1} ms  ({buildSw.Elapsed.TotalMilliseconds / strokeCount:F3} ms/stroke)");
        Console.WriteLine($"  full rebuild  : {rebuildMs,8:F1} ms  (rasterise {strokeCount} strokes onto the content layer)");
        Console.WriteLine($"  points total  : {Doc.TotalPoints}");
        Console.WriteLine($"  working set   : {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1} MB");
        Console.WriteLine();
    }

    // =====================================================================
    //  Measurement report
    // =====================================================================

    private void MemoryReport(string outFile)
    {
        var lines = new List<string>();
        void P(string s = "")
        {
            Console.WriteLine(s);
            lines.Add(s);
        }

        // Working set alone is misleading: Windows only drops resident pages
        // under pressure, so a process can look huge while holding very little.
        // Commit charge (private bytes) is the honest number to quote.
        void Settle()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Mem.TrimWorkingSet();
        }

        P("===== InkTeach 鍐呭瓨褰掑洜 =====");
        P($"鏄惧崱        : {Gfx.AdapterInfo}");
        P($"灞忓箷        : {_virtualW}x{_virtualH}锛寋_windows.Count} 涓鐩栫獥鍙?);
        P($"瀵圭収        : 涓€涓粈涔堥兘涓嶅仛鐨?.NET 鎺у埗鍙扮▼搴?= 23.3 MB 宸ヤ綔闆?/ 6.9 MB 绉佹湁");
        P("璇存槑        : 銆屽伐浣滈泦銆嶏紙浠诲姟绠＄悊鍣ㄩ噷閭ｄ竴鍒楋級浼氳绯荤粺闅忔椂鍥炴敹锛屼笉鍙嶆槧鐪熷疄鍗犵敤锛?);
        P("              涓嬮潰缁熶竴鐢ㄣ€屾彁浜ゅぇ灏?/ 绉佹湁瀛楄妭銆嶏紝鍙﹀闄勪笂鏄惧瓨鍗犵敤銆?);
        P();

        P("銆愬垎闃舵銆戝惎鍔ㄨ繃绋嬩腑鍐呭瓨鐨勫闀?);
        foreach (var l in Mem.Log) P("  " + l);
        P();

        for (int i = 0; i < 40; i++) RenderAll();
        Settle();
        P($"銆愮┖闂茬ǔ鎬併€戞彁浜ゅぇ灏?{Mem.Priv():F1} MB锛堢湡瀹炲崰鐢級锛屽伐浣滈泦 {Mem.Ws():F1} MB");
        P("             锛?0 甯т箣鍚庡己鍒跺洖鏀讹紝杩欐墠鏄骇鍝佹棩甯告寕鐫€鏃剁殑鍗犵敤锛?);
        P();

        P("銆愭寜绗旂敾鏁般€戝唴瀛樺闀?);
        P("    绗旂敾鏁?| 鎻愪氦澶у皬 | 鐩稿绌洪棽 | 鏄惧瓨 (宸茬敤/棰勭畻) | 鍦烘櫙");
        P("  ---------|----------|----------|------------------|----------------");
        double basePriv = Mem.Priv();
        foreach (int count in new[] { 0, 20, 100, 500, 2000, 10000 })
        {
            GenerateStrokes(count);
            Doc.Version++;
            RenderAll();
            Settle();
            double priv = Mem.Priv();
            string note = count switch
            {
                0 => "绌洪棽",
                <= 100 => "璁蹭竴椤?PPT",
                <= 500 => "涓€鑺傚畬鏁磋绋?,
                <= 2000 => "閲嶅害浣跨敤",
                _ => "鍘嬪姏娴嬭瘯",
            };
            string delta = count == 0 ? "    鈥?   " : $"+{priv - basePriv,5:F1} MB";
            P($"  {count,8} | {priv,6:F1} MB | {delta} | {GpuMb(),16} | {note}");
        }
        P();

        // Where exactly does the 10k-stroke memory go?
        P("銆愪竴涓囩瑪鐨勫唴瀛樺幓鍚戙€戦€愭鎷嗗紑锛堢湅鎻愪氦澶у皬锛?);
        Doc.Clear();
        Settle();
        RenderAll();
        Settle();
        P($"  娓呯┖銆佸洖鏀躲€佸苟璁╃郴缁熼噴鏀惧悗 : 鎻愪氦 {Mem.Priv(),6:F1} MB | 鏄惧瓨 {GpuMb()}");
        GenerateStrokes(10_000);
        Settle();
        P($"  鍙敓鎴愮瑪鐢绘暟鎹€佽繕娌＄敾     : 鎻愪氦 {Mem.Priv(),6:F1} MB | 鏄惧瓨 {GpuMb()}  锛坽Doc.TotalPoints} 涓偣锛?);
        Doc.Version++;
        RenderAll();
        P($"  绗竴娆℃暣灞傜粯鍒朵箣鍚?        : 鎻愪氦 {Mem.Priv(),6:F1} MB | 鏄惧瓨 {GpuMb()}");
        Doc.Clear();
        Settle();
        P($"  鍐嶆妸绗旂敾鍏ㄩ儴娓呯┖骞跺洖鏀跺悗   : 鎻愪氦 {Mem.Priv(),6:F1} MB | 鏄惧瓨 {GpuMb()}");
        P($"  宸查噴鏀剧殑鍑犱綍瀵硅薄绱       : {Stroke.ReleasedGeometries}");
        P();

        // Cost of one screen-sized offscreen layer.
        {
            Settle();
            double before = Mem.Priv();
            var pf = new Vortice.DCommon.PixelFormat(
                Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            var ctx = _windows[0].Context;
            var bmp = ctx.CreateBitmap(new SizeI(_virtualW, _virtualH), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            ctx.Target = bmp;
            ctx.BeginDraw();
            ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            ctx.EndDraw();
            ctx.Target = null;
            double after = Mem.Priv();
            bmp.Dispose();
            P($"銆愬浘灞傛垚鏈€戜竴寮犱笌鏈睆鍚屽昂瀵哥殑绂诲睆鍥惧眰 = +{after - before:F1} MB锛堟彁浜ゅぇ灏忥級"
              + $"锛堢悊璁哄€?{_virtualW * _virtualH * 4 / 1048576.0:F1} MB锛?K 姣忓眰 32 MB锛?);
            P();
        }

        P("銆愮粨璁恒€?);
        P("  - 绌洪棽鍩虹嚎鎵嶆槸浜у搧鏃ュ父鍗犵敤锛涗竴涓囩瑪閭ｆ 200 MB 鏄帇鍔涙祴璇曠殑鏋佺鍊笺€?);
        P("  - 绌洪棽鍩虹嚎鐨勬瀯鎴愶細.NET 杩愯鏃?+ 绋嬪簭闆嗙害 31 MB锛孌irect3D/Direct2D/鏂囧瓧绾?42 MB锛?);
        P("    灞忓箷灏哄鐨勫悎鎴愮紦鍐插尯绾?26 MB銆?);
        P("  - 绗旂敾鏁伴噺甯︽潵鐨勫闀夸富瑕佸湪 GPU 渚э紝鑰屼笖娓呯┖涔嬪悗骞朵笉浼氳繕鍥炴搷浣滅郴缁?);
        P("    锛圖irect2D 鍐呴儴鐣欑潃澶嶇敤锛夛紝鎵€浠ュ嘲鍊兼槸銆屾杞紡銆嶄繚鎸佺殑銆?);
        P("  - 杩欐槸 Direct2D 鐨勮涓猴紝涓嶆槸 .NET 鐨勮涓猴紱鎹㈡垚 C++ 涔熶竴鏍枫€?);

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(outFile, lines, System.Text.Encoding.UTF8);
            Console.WriteLine();
            Console.WriteLine("鎶ュ憡宸插啓鍏?" + Path.GetFullPath(outFile));
        }
        catch (Exception ex) { Console.WriteLine("鍐欐姤鍛婂け璐? " + ex.Message); }
    }

    private static double WorkingSetMb()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1048576.0;
    }

    private static double PrivateMb()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 / 1048576.0;
    }

    /// <summary>Dedicated GPU memory currently held by this process's device.</summary>
    private static string GpuMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return "n/a";
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return $"{info.CurrentUsage / 1048576.0:F0}/{info.Budget / 1048576.0:F0} MB";
        }
        catch { return "n/a"; }
    }

    private (double rec, double present) MeasureFrames(int n)
    {
        double rec = 0, pres = 0;
        for (int i = 0; i < n; i++)
        {
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            rec += _windows[0].LastRecordMs;
            pres += _windows[0].LastPresentMs;
        }
        return (rec / n, pres / n);
    }

    /// <summary>
    /// Draws a bright magenta probe stroke, waits for the compositor, then looks
    /// for those pixels in a screen grab. This is the only way to tell whether
    /// what the GPU produced actually made it onto the display.
    /// </summary>
    private bool VerifyRendering(out int hits, out int fullScreenHits, out string diag)
    {
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        var probe = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 16f };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            probe.AddPoint(cx - 380 + t * 760, cy + MathF.Sin(t * 6.28f) * 120, 0.8f, NowMs);
        }

        Doc.Clear();
        Doc.AddStroke(probe);
        Doc.InvalidateAll();

        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 600) { PumpMessages(); RenderAll(); }

        bool ok = ScreenProbe.TryFindMagenta((int)(cx - 400), (int)(cy - 160), 800, 320, out hits);
        fullScreenHits = ok ? hits : ScreenProbe.CountMagenta(_virtualX, _virtualY, _virtualW, _virtualH);
        diag = $"绗旂敾 {Doc.Strokes.Count}锛屽疄缁?{_windows[0].LastDrawnStrokes}锛?
             + $"閲嶅缓 {_windows[0].LastRebuildMs:F1} ms锛岄敊璇?{(string.IsNullOrEmpty(_windows[0].LastError) ? "鏃? : _windows[0].LastError)}";
        return ok;
    }

    private void Report(string outFile)
    {
        var lines = new List<string>();
        void P(string s = "")
        {
            Console.WriteLine(s);
            lines.Add(s);
        }

        P("===== InkTeach 瀹炴祴鏁版嵁 =====");
        P($"鏃ユ湡          : {DateTime.Now:yyyy-MM-dd HH:mm}");
        P($"鏄剧ず鍣?       : {_virtualW}x{_virtualH} 铏氭嫙妗岄潰锛寋_windows.Count} 涓鐩栫獥鍙?);
        P($"鐩戣鍣?DPI    : {_windows[0].Dpi}");
        P($"杩愯鏃?       : .NET {Environment.Version}");
        P($"Windows       : {Environment.OSVersion.Version}");
        P();

        for (int i = 0; i < 8; i++)
        {
            RenderAll();
            if (i < 3)
                P($"  [warmup frame {i}] render error: {(string.IsNullOrEmpty(_windows[0].LastError) ? "none" : _windows[0].LastError)}");
        }

        P($"銆愬熀绾裤€? 绗旂敾銆佺┖闂?          : 宸ヤ綔闆?{WorkingSetMb(),6:F1} MB   绉佹湁 {PrivateMb(),6:F1} MB   鏄惧瓨 {GpuMb()}");
        P($"銆愭覆鏌撻獙璇併€戝惎鍔ㄥ悗             : {(VerifyRendering(out var h0, out _, out var d0) ? "姝ｅ父" : "涓㈠け")} ({h0} 鍍忕礌)  {d0}");
        P();

        // --- 4K layer memory probe -------------------------------------
        {
            double before = WorkingSetMb();
            var probes = new List<ID2D1Bitmap1>();
            var pf = new Vortice.DCommon.PixelFormat(
                Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            var ctx = _windows[0].Context;
            for (int i = 0; i < 2; i++)
            {
                var bmp = ctx.CreateBitmap(new SizeI(3840, 2160), IntPtr.Zero, 0,
                    new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
                // Force the allocation: Direct2D commits memory lazily on first use.
                ctx.Target = bmp;
                ctx.BeginDraw();
                ctx.Clear(new Color4(0f, 0f, 0f, 0f));
                ctx.EndDraw();
                ctx.Target = null;
                probes.Add(bmp);
            }
            double after = WorkingSetMb();
            P($"銆愬唴瀛樸€戦澶?2 寮?3840x2160 鍥惧眰: 宸ヤ綔闆?+{after - before:F1} MB锛屾樉瀛?{GpuMb()}");
            P("        锛堜袱寮犲浘灞傜悊璁哄€?2 x 3840 x 2160 x 4B = 66 MB銆傛敞鎰?GPU 鍚堟垚鐨勫浘灞傞€氬父");
            P("          涓嶈鍏ヨ繘绋嬪伐浣滈泦锛岃鐪嬫樉瀛?涓撶敤 GPU 鍐呭瓨锛屼换鍔＄鐞嗗櫒閲屼篃瑕佺湅杩欎竴鍒椼€傦級");
            foreach (var b in probes) b.Dispose();
            P();
        }

        // --- per-stroke-count measurements -----------------------------
        P("銆愭€ц兘銆戞寜绗旂敾鏁伴噺瀵规瘮锛堣褰?CPU 鎻愪氦缁樺埗鍛戒护锛屼笂灞?浜ゅ埌鏄剧ず鍣級");
        P();
        P("  绗旂敾鏁?| 棣栧抚鍏ㄩ噺閲嶅缓 | 缂撳瓨灞傛瘡甯ц褰?| 涓嶇紦瀛樻瘡甯ц褰?| 涓婂睆 | 宸ヤ綔闆?);
        P("  -------|--------------|----------------|----------------|------|--------");

        foreach (int count in new[] { 0, 1_000, 10_000 })
        {
            GenerateStrokes(count);
            NoContentCache = false;

            Doc.InvalidateAll();
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            double rebuild = _windows[0].LastRebuildMs;

            var cached = MeasureFrames(count >= 10_000 ? 30 : 20);

            NoContentCache = true;
            var uncached = MeasureFrames(count >= 10_000 ? 8 : 20);
            NoContentCache = false;

            // Keep the cached layer in sync again for the next round.
            Doc.Version++;

            P($"  {count,7} | {rebuild,10:F1} ms | {cached.rec,12:F2} ms | {uncached.rec,12:F2} ms | {cached.present,4:F2} ms | {WorkingSetMb(),5:F1} MB / 鏄惧瓨 {GpuMb()}");
        }
        P();

        bool okAfterTable = VerifyRendering(out var h1, out _, out var d1);
        P($"銆愭覆鏌撻獙璇併€戞€ц兘娴嬭瘯涔嬪悗       : {(okAfterTable ? "姝ｅ父" : "涓㈠け")} ({h1} 鍍忕礌)  {d1}");
        P();

        // --- eraser: correctness, then cost ----------------------------
        EraseCheck(out bool eraseOk, out string eraseDetail);
        P($"銆愭鐨摝銆戞摝闄ゆ槸鍚︽纭?         : {(eraseOk ? "姝ｇ‘" : "寮傚父")}  {eraseDetail}");
        P();

        // --- interactive path: cost of committing one new stroke ---------
        {
            GenerateStrokes(10_000);
            Doc.Version++;
            RenderAll();

            double sum = 0;
            const int n = 25;
            var rnd = new Random(7);
            for (int i = 0; i < n; i++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = PenColor, Width = 4f };
                float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
                float y = _virtualY + (float)rnd.NextDouble() * _virtualH;
                for (int j = 0; j < 12; j++)
                {
                    x += 9; y += 7;
                    s.AddPoint(x, y, 0.7f, NowMs);
                }
                Doc.AppendStroke(s);
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                sum += _windows[0].LastPatchMs;
            }
            P($"銆愪氦浜掕矾寰勩€戝湪 1 涓囩瑪鐢荤殑鐢婚潰涓婏紝缁撴潫涓€绗旂殑澧為噺鏇存柊: {sum / n:F2} ms/绗?
              + $"锛堝鐓э細鏁村眰閲嶇敾 {_windows[0].LastRebuildMs:F0} ms 閲忕骇锛?);

            // Eraser drag: each step removes the strokes under the cursor and
            // repaints only that little box.
            double eraseSum = 0;
            int eraseSteps = 0, eraseRemoved = 0;
            var rnd2 = new Random(11);
            float ex = _virtualX + (float)rnd2.NextDouble() * _virtualW;
            float ey = _virtualY + (float)rnd2.NextDouble() * _virtualH;
            double eraseRebuildRef = _windows[0].LastRebuildMs;
            for (int i = 0; i < n; i++)
            {
                ex += 14; ey += 9;
                eraseRemoved += Doc.EraseAt(ex, ey, 30f);
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                eraseSum += _windows[0].LastPatchMs;
                eraseSteps++;
            }
            P($"銆愭鐨摝銆戝湪 1 涓囩瑪鐢荤殑鐢婚潰涓婏紝鎿﹂櫎鎷栧姩涓€姝? {eraseSum / eraseSteps:F2} ms/姝?
              + $"锛堝叡鎿︽帀 {eraseRemoved} 绗旓紱瀵圭収鏁村眰閲嶇敾 {eraseRebuildRef:F0} ms锛?);

            // 鍍忕礌姗＄毊鐨勪竴姝ワ細鍛戒腑鍒ゅ畾 + 鍒囨 + 閲嶇敾閭ｄ竴灏忓潡銆傚畠姣旀暣绗旀鐨鍋?            // 涓€浠朵簨锛堝垏娈典笌鏂板缓纰庣墖锛夛紝鎵€浠ュ繀椤诲崟鐙噺鈥斺€斾笉鑳芥嬁鏁寸瑪姗＄毊鐨勬暟瀛楅《鏇裤€?            double cutSum = 0;
            int cutSteps = 0, cutStrokes = 0;
            double cutSelfMs = 0;
            int cutTiles = 0, cutDrawn = 0;
            var rnd3 = new Random(13);
            float bx = _virtualX + (float)rnd3.NextDouble() * _virtualW;
            float by = _virtualY + (float)rnd3.NextDouble() * _virtualH;
            int objBefore = Doc.Strokes.Count;
            // 娉ㄦ剰锛氳繖閲岃蛋鐨勬槸**涓€娆″畬鏁存嫋鎷?*鐨?API锛圔egin/End锛夛紝鍜岀敤鎴风湡鎿︿竴绗斾竴妯′竴鏍枫€?            // 鎷栨嫿涓瘡姝ュ彧鏀瑰尯闂达紙渚垮疁锛夛紝鏉炬墜鏃朵竴娆℃€ц惤鎴愮嫭绔嬪璞♀€斺€旈偅涓€绗斿崟鐙鏃躲€?            Doc.BeginEraseRect();
            for (int i = 0; i < n; i++)
            {
                bx += 14; by += 9;
                var swCut = Stopwatch.StartNew();
                cutStrokes += Doc.EraseRectAt(bx, by, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                swCut.Stop();
                cutSelfMs += swCut.Elapsed.TotalMilliseconds;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                cutSum += _windows[0].LastPatchMs;
                cutTiles += _windows[0].LastPatchCount;
                cutDrawn += _windows[0].LastDrawnStrokes;
                cutSteps++;
            }
            var swSettle = Stopwatch.StartNew();
            Doc.EndErase();
            swSettle.Stop();
            double settleMs = swSettle.Elapsed.TotalMilliseconds;
            P($"銆愬儚绱犳鐨€戝湪 1 涓囩瑪鐢荤殑鐢婚潰涓婏紝鎿﹂櫎鎷栧姩涓€姝? {cutSum / cutSteps:F2} ms/姝?
              + $"锛堝叾涓摝闄ゆ湰韬?{cutSelfMs / cutSteps:F2} ms + 閲嶇敾 {cutSum / cutSteps - cutSelfMs / cutSteps:F2} ms锛?
              + $"鍏卞垏鍒?{cutStrokes} 绗斻€佹瘡姝ラ噸鐢?{cutTiles / (double)cutSteps:F1} 鍧楋紱"
              + $"瀵硅薄鏁?{objBefore} 鈫?{Doc.Strokes.Count}锛堝尯闂磋〃閭ｄ竴鐗堜笉娑紝鎷嗗璞￠偅鐗堜細娑ㄥ埌 {objBefore + cutStrokes}锛夛紱"
              + $"姣忔閲嶇敾绗旀暟 {cutDrawn / (double)cutSteps:F0}锛?
              + $"鏉炬墜缁撹处锛堝尯闂?鈫?鐙珛瀵硅薄锛墈settleMs:F1} ms锛屼竴娆℃€э紱"
              + $"鏁寸瑪姗＄毊鍚屼綅缃?{eraseSum / eraseSteps:F2} ms/姝ワ級");
            // 涓婇潰杩欏嚑涓暟锛堟摝闄ゆ湰韬?/ 閲嶇敾 / 姣忔鍑犲潡 / 姣忔鍑犵瑪锛夋槸**鍒嗗緱寮€**鐨勶細
            // 鎹竴鐗堝疄鐜版椂灏辩湅寰楀嚭鎱㈠湪鍝竴鍗娾€斺€?026-09-15 姣?鎷嗗璞?鍜?鍖洪棿琛?涓ょ増锛?            // 鎿﹂櫎鏈韩 5.9鈫?.9ms锛堝揩涓€鍊嶏級銆侀噸鐢?2.5鈫?.8ms锛堟瘡姝ヨ鐢荤殑绗旀暟 764鈫?86锛?            // 澶氬嚭鏉ョ殑鏄?琚摝杩囦絾杩樺湪鏂囨。閲?鐨勬暣鏉＄瑪杩癸級銆?
            // Undo of a single stroke costs the same as an erase patch.
            double undoSum = 0;
            int undoCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!Doc.Undo()) break;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                undoSum += _windows[0].LastPatchMs;
                undoCount++;
            }
            P($"銆愭挙閿€銆戝湪 1 涓囩瑪鐢荤殑鐢婚潰涓婏紝鎾ら攢涓€姝? {(undoCount > 0 ? undoSum / undoCount : 0):F2} ms/姝?
              + $"锛坽undoCount} 姝ワ級");
            P();

            // Where does an erase step actually spend its time? Split the hit
            // test from the repaint, and A/B the spatial index against a linear
            // scan over every stroke. Runs after the undo measurement because
            // it rebuilds the document and clears the undo history.
            {
                GenerateStrokes(10_000);
                Doc.InvalidateAll();
                RenderAll();

                var list = new List<Stroke>();
                var rndQ = new Random(99);
                var probes = new RectF[500];
                for (int i = 0; i < probes.Length; i++)
                {
                    float px = _virtualX + (float)rndQ.NextDouble() * _virtualW;
                    float py = _virtualY + (float)rndQ.NextDouble() * _virtualH;
                    probes[i] = new RectF { MinX = px - 45, MinY = py - 45, MaxX = px + 45, MaxY = py + 45 };
                }

                var swq = Stopwatch.StartNew();
                int found = 0;
                foreach (var r in probes) found += Doc.QueryGrid(r, list);
                swq.Stop();

                var swl = Stopwatch.StartNew();
                int found2 = 0;
                foreach (var r in probes)
                    foreach (var s in Doc.Strokes)
                        if (s.Bounds.Intersects(r)) found2++;
                swl.Stop();

                P($"銆愬懡涓祴璇曘€?00 娆℃煡璇細绌洪棿绱㈠紩 {swq.Elapsed.TotalMilliseconds:F1} ms"
                  + $"锛屽叏琛ㄦ壂鎻?{swl.Elapsed.TotalMilliseconds:F1} ms"
                  + $"锛堟彁閫?{swl.Elapsed.TotalMilliseconds / Math.Max(0.01, swq.Elapsed.TotalMilliseconds):F0} 鍊嶏紝鍛戒腑 {found2} 鏉★級");
                P();
            }
        }

        // --- laser cost on top of 10k cached strokes --------------------
        GenerateStrokes(10_000);
        Doc.Version++;
        RenderAll();
        var idle10k = MeasureFrames(20);

        double MeasureLaser(out int frames)
        {
            Laser.Clear();
            Laser.Begin(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.5f,
                        _clock.Elapsed.TotalMilliseconds, LaserWidthLogical);
            double t0 = _clock.Elapsed.TotalMilliseconds;
            double total = 0;
            frames = 0;
            while (_clock.Elapsed.TotalMilliseconds - t0 < 2000)
            {
                double t = _clock.Elapsed.TotalMilliseconds;
                float px = _virtualX + _virtualW * 0.5f + (float)(Math.Sin(t / 300.0) * 400);
                float py = _virtualY + _virtualH * 0.5f + (float)(Math.Cos(t / 220.0) * 200);
                Laser.Add(px, py, t);
                NowMs = t;
                Laser.Prune(NowMs);
                RenderAll();
                total += _windows[0].LastRecordMs;
                frames++;
            }
            Laser.Clear();
            return frames > 0 ? total / frames : 0;
        }

        double laserRibbon = MeasureLaser(out int framesRibbon);
        P($"銆愭縺鍏夌瑪銆? 涓囩瑪鐢讳箣涓婂彔鍔犳縺鍏夎建杩癸紙姣忓抚璁板綍鑰楁椂锛?);
        P($"    鐢绘縺鍏?                : {laserRibbon:F2} ms   锛坽framesRibbon} 甯э級");
        P($"    涓嶅紑婵€鍏夛紙鍚屼竴鐢婚潰锛?  : {idle10k.rec:F2} ms");
        bool okAfterLaser = VerifyRendering(out var h2, out _, out var d2);
        P($"銆愭覆鏌撻獙璇併€戞縺鍏夌瑪涔嬪悗         : {(okAfterLaser ? "姝ｅ父" : "涓㈠け")} ({h2} 鍍忕礌)  {d2}");
        P();

        // --- pass-through rendering check ------------------------------
        P("銆愮┛閫忋€戜笁绉嶅疄鐜版柟寮忎笅锛岃鐩栧眰鏄惁浠嶇劧姝ｅ父鏄剧ず锛?);
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        P($"  {"锛堝鐓э級涓嶇┛閫?,-20} : 鍛戒腑娴嬭瘯 {ProbeHitTest()}");
        foreach (PassThroughMode m in new[] { PassThroughMode.HitTest, PassThroughMode.ExTransparent, PassThroughMode.LayeredTransparent })
        {
            PassThrough = true;
            PassMode = m;
            foreach (var w in _windows) ApplyPassThroughStyle(w);

            bool visible = VerifyRendering(out int hits, out int allHits, out string diag);
            string extra = "";
            if (!visible) extra = $"  鍏ㄥ睆鍙︽壘鍒?{allHits} 涓悓鑹插儚绱?;
            P($"  {m,-20} : {(visible ? "娓叉煋姝ｅ父" : "娓叉煋涓㈠け")}  ({hits} 鍍忕礌){extra}");
            P($"      璇婃柇: {diag}");

            P($"      鍛戒腑娴嬭瘯: {ProbeHitTest()}");

            PassThrough = false;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
        }
        P();
        P("璇存槑锛氥€屾覆鏌撴甯搞€嶅彧琛ㄧず瑕嗙洊灞傛湰韬病琚牬鍧忋€傞紶鏍囩偣鍑绘槸鍚︾湡鐨勮惤鍒颁笅灞傜▼搴忥紝闇€瑕佸湪鐪熸満涓婃墜鍔ㄧ‘璁ゃ€?);

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(outFile, lines, System.Text.Encoding.UTF8);
            Console.WriteLine();
            Console.WriteLine("鎶ュ憡宸插啓鍏?" + Path.GetFullPath(outFile));
        }
        catch (Exception ex)
        {
            Console.WriteLine("鍐欐姤鍛婂け璐? " + ex.Message);
        }
    }

    private void MemoryProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== memory probe: cost of one full-screen content layer ===");
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        double before = proc.WorkingSet64 / 1048576.0;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var probes = new List<ID2D1Bitmap1>();
        var ctx = _windows[0].Context;
        int w = 3840, h = 2160;
        for (int i = 0; i < 3; i++)
        {
            probes.Add(ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw)));
        }
        proc.Refresh();
        double after = proc.WorkingSet64 / 1048576.0;
        Console.WriteLine($"  {w}x{h} x3 layers: working set {before:F1} MB -> {after:F1} MB  (delta {after - before:F1} MB)");
        foreach (var b in probes) b.Dispose();
        Console.WriteLine();
    }

    private void PumpMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Renders for a while so DirectComposition has definitely put the
    /// latest frame on screen before we grab pixels.</summary>
    private void SettleFrames(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            PumpMessages();
            StepCameraAnim();          // 涓诲惊鐜瘡甯у仛鐨勯偅涓€浠朵簨锛岃嚜妫€涔熷緱鍋氾紙鍚﹀垯鐩告満姘歌繙鍋滃湪璧风偣锛?            UpdatePredictTail();       // B4锛氶娴嬪熬鐨勬瘡甯ф洿鏂帮紙涓诲惊鐜噷鎸備簡鍚屼竴鍙ワ紱鑷涓嶈蛋涓诲惊鐜級
            RenderAll();
        }
    }

    // ------------------------------------------------------------------
    //  Synthetic input test
    // ------------------------------------------------------------------

    private static Native.INPUT MouseInput(int x, int y, uint extraFlags, int vx, int vy, int vw, int vh)
    {
        var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
        inp.mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        inp.mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        inp.mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE
                       | Native.MOUSEEVENTF_VIRTUALDESK | extraFlags;
        return inp;
    }

    private void SendMouse(int x, int y, uint extraFlags)
    {
        var inp = new[] { MouseInput(x, y, extraFlags, _virtualX, _virtualY, _virtualW, _virtualH) };
        Native.SendInput(1, inp, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT_KBD KeyInput(ushort vk, bool up)
    {
        var inp = new Native.INPUT_KBD { type = Native.INPUT_KEYBOARD };
        inp.ki.wVk = vk;
        inp.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0u;
        return inp;
    }

    /// <summary>
    /// 鍚堟垚閿洏锛氭妸涓€涓茶櫄鎷熼敭**鎸夐『搴忔寜涓嬨€佸啀鍊掑簭鎶捣**锛圕trl+Alt+O 杩欑缁勫悎蹇呴』杩欐牱鍙戯紝
    /// 鍙寜涓婚敭鏄Е鍙戜笉浜嗗叏灞€鐑敭鐨勶級銆?    ///
    /// 涓轰粈涔堣瀹冿紙鑰屼笉鏄儚鎵规敞鍐呮寜閿偅鏍风洿鎺?`PostMessage`锛夛細鍏ㄥ眬鐑敭鏄?*绯荤粺**鍦?    /// 閿洏杈撳叆娴佷笂鍒ゅ畾鐨勶紝`RegisterHotKey` 鏀跺埌鐨勬槸鐪熷疄鐨勬寜閿紱瑕侀獙"鎸変笅鍘荤湡鐨勫垏浜嗗伐鍏?锛?    /// 灏卞緱璧扮湡瀹炵殑閿洏閫氳矾銆傛壒娉ㄥ唴閭ｄ簺閿笉鐢ㄥ畠鏄洜涓哄畠浠敱鎴戜滑鑷繁鐨勭獥鍙ｆ敹锛?    /// 鐩存帴鎶曢€掔獥鍙ｆ秷鎭氨鑳藉埌銆?    /// </summary>
    private uint SendKeyChord(params ushort[] vks)
    {
        var seq = new List<Native.INPUT_KBD>();
        foreach (var vk in vks) seq.Add(KeyInput(vk, false));
        for (int i = vks.Length - 1; i >= 0; i--) seq.Add(KeyInput(vks[i], true));
        // 杩斿洖鍊硷紳**鐪熺殑濉炶繘绯荤粺杈撳叆娴佺殑浜嬩欢涓暟**銆傝嚜妫€瑕佹妸瀹冩墦鍑烘潵锛?        // 瀹冩瘮"宸ュ叿娌″垏"澶氫竴灞備俊鎭€斺€斿垎寰楁竻"鎸夐敭娌¤繘绯荤粺"鍜?杩涗簡浣嗙儹閿病鍝嶅簲"銆?        return Native.SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<Native.INPUT_KBD>());
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;        // Alt
    private const ushort VK_SHIFT = 0x10;

    /// <summary>鎸変竴娆?Ctrl+Alt+&lt;涓婚敭&gt;锛堢幇鍦ㄥ彧缁?閫€褰瑰浘褰㈤敭涓嶅搷搴?閭ｆ潯娴嬭瘯鐢紱
    /// 2026-10-04 璧峰叏灞€鐑敭閮芥槸 `Ctrl+Alt+Shift+鈥 鐨勫舰鐘讹級銆傝繑鍥炲杩涘幓鐨勪簨浠舵暟銆?/summary>
    private uint SendCtrlAlt(ushort vk) => SendKeyChord(VK_CONTROL, VK_MENU, vk);

    /// <summary>
    /// Drives a real drag through SendInput so the whole input path is exercised
    /// end to end - pointer messages, stroke accumulation, redraw, and finally
    /// pixels on screen. Synthetic input is consumed by this overlay, which is
    /// topmost, so nothing underneath is clicked.
    /// </summary>
    // ------------------------------------------------------------------
    //  Click target (second process) + real pass-through test
    // ------------------------------------------------------------------

    /// <summary>Runs a plain window in its own process that records every time
    /// it is clicked. Used as the window underneath the overlay.</summary>
    private void RunClickTarget(string logFile)
    {
        _clickLogFile = logFile;
        _clickTargetHwnd = Native.CreateWindowEx(
            0, _className, "InkTeach 鐐瑰嚮鐩爣",
            0x00CF0000L /*WS_OVERLAPPEDWINDOW*/, 120, 120, 420, 320,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_clickTargetHwnd == IntPtr.Zero)
        {
            File.WriteAllText(logFile, "error\n");
            return;
        }
        Native.ShowWindow(_clickTargetHwnd, 5 /*SW_SHOW*/);
        File.WriteAllText(logFile, "ready\n");

        while (!_quit)
        {
            if (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            else break;
        }
    }

    private static int CountClicks(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            var text = ReadTextShared(file);
            int n = 0, i = 0;
            while ((i = text.IndexOf("clicked", i, StringComparison.Ordinal)) >= 0) { n++; i += 7; }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 璇讳竴涓?鍙︿竴涓繘绋嬫鍦ㄥ啓"鐨勫皬鏂囦欢锛?*甯﹂噸璇?*銆?    ///
    /// 涓轰粈涔堥渶瑕侊細鐐瑰嚮鐩爣杩涚▼姣忛殧涓€浼氬効灏卞線杩欎釜鏂囦欢閲岃拷鍔犱竴琛岋紝鐖惰繘绋嬪悓鏃跺湪璇伙紱
    /// 鎾炰笂鍏变韩鍐茬獊鏃?`File.ReadAllText` 鐩存帴鎶?IOException锛屾暣鏉＄敤渚嬩細浠?寮傚父"鏀跺満
    /// 鑰屼笉鏄垽鎹け璐モ€斺€斿浠堕噷灏辨姤鎴?FATAL锛岀湅璧锋潵鍍忓姛鑳藉潖浜嗐€?    /// 杩欑被"娴嬭瘯鑷繁鐨勬椂搴忛棶棰?蹇呴』鍜岃娴嬬殑涓滆タ鍒嗗紑锛屼笉鐒朵細娴垂寰堝鏃堕棿鍘绘煡涓€涓笉瀛樺湪鐨?bug銆?    /// </summary>
    private static string ReadTextShared(string path, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (Exception) { return ""; }   // 鍒扮偣浜嗚繕璇讳笉鍒板氨褰撶┖鐨勶紝鍒妸鐢ㄤ緥鎵撴垚寮傚父
        }
    }
    private void CaptureIconShow(string path) => SheetShow(new CaptureIconSheet(), path);

    /// <summary>鍑哄浘锛?*婵€鍏夌瑪鍥炬爣鍊欓€?*锛堝紑鍙戞湡姣斿浘鐢紝瑙?<see cref="LaserIconSheet"/>锛夈€?/summary>
    private void IconShow(string path) => SheetShow(new LaserIconSheet(), path);

    /// <summary>鍑哄浘锛氬浘褰㈤潰鏉块偅浜涜嚜缁樺浘鏍囩殑瀵圭収琛紙瑙?<see cref="ShapeIconSheet"/>锛夈€?/summary>
    private void ShapeIconShow(string path) => SheetShow(new ShapeIconSheet(), path);

    /// <summary>鍑哄浘锛?*鐧芥澘 / 婵€鍏夌瑪鐨勫浘鏍囧€欓€?*锛?026-09-26锛岃 <see cref="ToolIconSheet"/>锛夈€?/summary>
    private void ToolIconShow(string path) => SheetShow(new ToolIconSheet(), path);

    /// <summary>
    /// **鎶婁竴寮?鍥炬爣瀵圭収琛?鐨勫浘鎴笅鏉?*鈥斺€擿--iconshow` / `--captureicons` / `--shapeiconshow`
    /// 涓夊鍏辩敤杩欎竴浠斤紙鍘熸潵鍙湁鍓嶉潰涓や釜銆佸悇鍐欎簡涓€閬嶏紝鍔犵涓変釜鏃舵敹杩囨潵鐨勶級銆?    /// </summary>
    private void SheetShow(IOverlayUi sheet, string path)
    {
        SetUi(sheet);
        BoardOn = true;                      // 鐧芥澘鎵撳簳锛氬浘閲屾病鏈夋闈笂鐨勬潅涓滆タ
        SettleFrames(400);

        // 浼樺厛璧?*绂诲睆鍑哄浘**锛氶攣灞?/ 杩滅▼ / 娌℃樉绀哄櫒鏃剁収鏍峰嚭寰楁潵锛屽浘閲屼篃涓嶄細娣疯繘妗岄潰銆?        if (OffscreenShot(path)) return;

        var b = CurrentUi.QueryBounds();
        int x = (int)MathF.Floor(b.MinX * DpiScale) - 20;
        int y = (int)MathF.Floor(b.MinY * DpiScale) - 20;
        int w = (int)MathF.Ceiling((b.MaxX - b.MinX) * DpiScale) + 40;
        int h = (int)MathF.Ceiling((b.MaxY - b.MinY) * DpiScale) + 40;
        bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
        Console.WriteLine(ok ? $"宸插嚭鍥?{path}" : "鍑哄浘澶辫触");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }

    /// <summary>
    /// 鐪熷疄闈㈡澘鐨勬瘡甯т唬浠凤細**鍚屼竴浠藉唴瀹?*锛屾寕闈㈡澘涓庝笉鎸傞潰鏉垮悇娓叉煋涓€閬嶏紝姣旀椂闂淬€?    ///
    /// 涓轰粈涔堣鏈夎繖涓細璁捐闃舵閭ｅ彞"闈㈡澘姣忓抚 0.5锝?.5 姣"鏄?*浼?*鐨?    /// 锛堟嬁 160脳60 鐨勫皬鐣岄潰鎸夐潰绉鎺ㄧ殑锛夛紝鑰岄潰鏉跨幇鍦ㄦ湁 636脳82銆?    /// 杩樺湪鍐欏瓧杩欐潯鏈€鏁忔劅鐨勮矾寰勪笂銆傛暀瀹ゆ満鍣ㄦ瘮寮€鍙戞満鎱紝浼扮殑鏁颁笉鑳藉綋缁撹銆?    ///
    /// 閲忎笁妗ｏ細涓嶆寕鐣岄潰锛堝熀鍑嗭級锛忔敹璧锋垚鐞冿紡灞曞紑鎴愬甫瀛愶紙鍚缃潯锛屾渶璐电殑涓€妗ｏ級銆?    /// 姣忔。閮藉己鍒?`_dirty = true` 鍐嶆覆鏌擄紝閬垮厤"绌洪棽涓嶅嚭甯?鎶婁唬浠烽噺鎴?0銆?    /// </summary>
    private void PanelPerf()
    {
        Console.WriteLine();
        Console.WriteLine("=== 鐪熷疄闈㈡澘鐨勬瘡甯т唬浠凤紙鍚屼竴浠藉唴瀹癸紝鎸?vs 涓嶆寕锛?==");

        // 涓€灞忔澘涔︼細200 鏉″悇 40 涓偣
        Doc.Clear();
        Doc.ClearHistory();
        var rnd = new Random(7);
        for (int s = 0; s < 200; s++)
        {
            var st = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0.1f, 0.1f, 0.1f, 1f),
                Width = 6f * DpiScale,
            };
            float x0 = _virtualX + rnd.Next(100, Math.Max(200, _virtualW - 400));
            float y0 = _virtualY + rnd.Next(100, Math.Max(200, _virtualH - 300));
            for (int k = 0; k < 40; k++) st.AddPoint(x0 + k * 6, y0 + MathF.Sin(k * 0.3f) * 20, 1f, k);
            Doc.AddStroke(st);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        double Measure(int frames)
        {
            // **璺?3 杞彇鏈€灏忓€?*锛氳繖鍙版満鍣ㄤ笂鍚屾椂璺戠潃杩滅▼鎺у埗鍜屽嚑涓悆 GPU 鐨勫鎴风锛?            // 骞冲潎鍊艰"鍒汉鎶㈣蛋鐨勯偅鍑犳绉?鎶珮寰楃璋憋紙鍚屼竴妗ｅ疄娴嬪湪 1.1锝?.0 涔嬮棿璺筹級銆?            // 鏈€灏忓€?= 鏈€"骞插噣"鐨勯偅涓€杞紝鐢ㄥ畠姣旀墠姣斿緱鍑烘垜浠嚜宸变唬鐮佺殑宸埆銆?            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                SettleFrames(150);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++) { _dirty = true; RenderAll(); }
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / frames);
            }
            return best;
        }

        SetUi(new HeadlessUi());
        SettleFrames(200);
        double none = Measure(200);

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        double ball = Measure(200);                       // 鏀惰捣鎬侊細涓€涓悆

        if (CurrentUi is InkUi.FullUi ui)
        {
            ui.SetExpandForTest(1f);                     // 灞曞紑锛堟樉寮忚烦 1锛涘埆鐢ㄤ細"缈婚潰"鐨勬棫 SnapForTest锛?            ui.OpenRailForTest();                         // 鑹插甫涔熷紶寮€锛堟渶璐电殑涓€妗ｏ級
        }
        SettleFrames(300);
        double full = Measure(200);

        Console.WriteLine($"  涓嶆寕鐣岄潰            {none,6:F2} ms/甯?);
        Console.WriteLine($"  鏀惰捣鎴愪竴涓悆        {ball,6:F2} ms/甯?  锛堟瘮涓嶆寕澶?{ball - none,5:F2}锛?);
        Console.WriteLine($"  灞曞紑锛嬭壊甯﹀紶寮€      {full,6:F2} ms/甯?  锛堟瘮涓嶆寕澶?{full - none,5:F2}锛?);

        // ---- 褰掑洜锛氭妸鍑犲潡鍒嗗埆鍏虫帀鍐嶉噺锛岀湅閽辫姳鍦ㄥ摢 ----
        Console.WriteLine();
        Console.WriteLine("  褰掑洜锛堜粠銆屽睍寮€锛嬭壊甯︺€嶈繖涓€妗ｉ噷鐪佷簡澶氬皯锛夛細");
        void Attrib(string name, Action set)
        {
            set();
            SettleFrames(200);
            double t = Measure(200);
            Console.WriteLine($"    {name,-22} {t,6:F2} ms/甯?  锛堢渷 {full - t,5:F2}锛?);
        }
        Attrib("涓嶇畻闃村奖", () => InkUi.FullUi.PerfSkipShadow = true);
        InkUi.FullUi.PerfSkipShadow = false;
        Attrib("涓嶇畻鍑规Ы涓庨《鍏?, () => InkUi.FullUi.PerfSkipChrome = true);
        InkUi.FullUi.PerfSkipChrome = false;
        Attrib("涓嶇畻鑹茬墖缁嗚妭", () => InkUi.FullUi.PerfSkipSwatchDetail = true);
        InkUi.FullUi.PerfSkipSwatchDetail = false;
        Attrib("涓嶇畻鍥炬爣", () => InkUi.FullUi.PerfSkipIcons = true);
        InkUi.FullUi.PerfSkipIcons = false;

        bool ok = (full - none) <= 1.5;                   // 璁″垝閲屽畾鐨勭孩绾匡細鈮?.5ms/甯?        Console.WriteLine(ok
            ? "  PASS: 瀹屾暣灞曞紑鎬佺殑棰濆浠ｄ环鍦?1.5 姣浠ュ唴"
            : $"  FAIL: 瀹屾暣灞曞紑鎬佹瘮涓嶆寕鐣岄潰璐典簡 {full - none:F2} 姣锛堢孩绾?1.5锛?);
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }
    private bool OffscreenShot(string path)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderUiToBgra(this, 24, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>鍚屼笂锛屼絾**鐢诲摢涓€鍧楄嚜宸辨寚瀹?*锛堥€昏緫鍍忕礌锛夆€斺€旇 `RenderUiToBgra` 鐨勯噸杞姐€?/summary>
    private bool OffscreenShot(string path, in RectF bounds)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderUiToBgra(this, 24, bounds, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>鍚屼笂锛屼絾鐢荤殑鏄?*娴姩灞?*锛堥€変腑妗?/ 鎿嶄綔鏉?/ 灏忛潰鏉匡級鈥斺€斿潗鏍囩粰鐢诲竷鍧愭爣銆?/summary>
    private bool OffscreenFloatingShot(string path, in RectF canvasBounds)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderFloatingToBgra(this, 24, canvasBounds, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>
    /// `--makeicon &lt;out.ico&gt;`锛?*鐢ㄧ晫闈㈣嚜宸辩殑娓叉煋**鐢熸垚绋嬪簭鍥炬爣銆?    ///
    /// 2026-10-01 绗簲杞畾锛氬浘鏍?= 璁捐绋?v5 鐨?**B 妗ｃ€孎luent 绗?锛?甯︾瑪閿嬬殑绾㈢瑪杩广€?*
    /// 锛堢敤鎴凤細"浣犵瑪閿嬪姬绾胯繖涓€鐗堝仛寰楁尯濂界殑锛屾垜鎯充娇鐢ㄨ繖涓?锛涚瀛愯
    /// design/鍥炬爣-璁捐绋縱5-绾挎潯鍨嬬殑绗?png 涓?v5b-绗旇抗鍥涢€?png锛岀敾鍦?<see cref="AppIconUi"/> 閲岋級銆?    /// 涓婁竴鐗堟槸"瀹炲績鐨勫ぇ绗?路 鐧界爾"锛屽啀涓婁竴鐗堟槸"鏀惰捣鎬侀偅棰楃悆"锛岄兘鐣欏湪 git 鍘嗗彶閲岋紱杩欐潯鍛戒护鐨勬満鍒舵病鍙橈細
    /// 鎶婄獥鍙?DPI **涓存椂鏀惧ぇ**鍐嶇灞忓嚭鍥?鈥斺€?鐣岄潰鏄寜"閫昏緫鍧愭爣 脳 DPI/96"鐢荤殑锛?    /// 鎵€浠ユ斁澶?DPI 绛変簬**鎸夌煝閲忛噸鐢讳竴寮犲ぇ鐨?*锛岃€屼笉鏄妸 96 鐨勫皬鍥炬媺澶э紙鎷夊ぇ浼氱硦锛夈€?    /// 鍐嶆妸閭ｄ竴寮犳寜鍚勬。灏哄缂╀笅鍘汇€佽鎴愪竴涓灏哄 .ico銆?    /// </summary>
    private void MakeIcon(string path)
    {
        SetUi(new AppIconUi());
        SettleFrames(120);
        if (CurrentUi is not AppIconUi ui || _windows.Count == 0)
        {
            Console.WriteLine("  鍑哄浘鏍囧け璐ワ細鍥炬爣鐣岄潰娌℃寕涓?/ 娌℃湁绐楀彛");
            _quit = true;
            return;
        }

        var box = ui.QueryBounds();
        if (box.IsEmpty) { Console.WriteLine("  鍑哄浘鏍囧け璐ワ細鍗犵敤鐭╁舰鏄┖鐨?); _quit = true; return; }

        // 鐩爣杈归暱 256锛氬浘鏍囩晫闈㈡槸 64 閫昏緫鍍忕礌鐨勬柟妗嗭紝DPI 鎶埌 384 姝ｅソ鍑?256脳256銆?        var win = _windows[0];
        uint want = (uint)Math.Clamp(MathF.Round(96f * 256f / (box.MaxX - box.MinX)), 96, 96 * 12);
        uint dpi0 = win.Dpi;
        win.Dpi = want;
        byte[] px = win.RenderUiToBgraTransparent(this, 0, box, out int bw, out int bh);
        win.Dpi = dpi0;
        if (px == null || bw <= 0)
        {
            Console.WriteLine("  鍑哄浘鏍囧け璐ワ細" + (win.LastError ?? "娌＄敾鍑烘潵"));
            _quit = true;
            return;
        }
        Console.WriteLine($"  娓叉煋灏哄 {bw}脳{bh}锛堜负瀹冩妸绐楀彛 DPI 涓存椂鎶埌 {want}锛?);

        // **涓嶅仛鍦嗗舰钂欑増**锛氬舰鐘跺氨鏄櫧鐮栬嚜宸辩殑鍦嗚锛岀爾澶栨湰鏉ュ氨鏄€忔槑
        // 锛堟棫鐗堥偅棰楃悆鎵嶉渶瑕佹妸鏂瑰舰鏌斿厜娓呮垚鍦嗭紱杩欐潯娉ㄩ噴鐣欑潃锛屽厤寰椾笅娆℃敼鍥炲幓鏃跺繕浜嗭級銆?        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            WriteMultiSizeIco(path, px, bw, bh, sizes);
            // 椤哄甫鍑轰竴寮?PNG 棰勮锛氱瑪瀵逛笉瀵广€佺孩涓嶇孩銆佺爾鐨勫渾瑙掗『涓嶉『锛?*鍙兘鐪嬪浘**銆?            string png = Path.ChangeExtension(path, ".png");
            using (var b = BgraToBitmap(px, bw, bh)) b.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  宸插啓鍑?{path}锛坽sizes.Length} 妗ｏ細{string.Join('/', sizes)}锛?);
            Console.WriteLine($"  棰勮鍥?{png}锛堜汉鐪兼牳瀵圭敤锛?);
        }
        catch (Exception ex) { Console.WriteLine("  鍑哄浘鏍囧け璐ワ細" + ex.Message); }
        _quit = true;
    }

    /// <summary>
    /// 閲忓嚭"鐞?鐨勫崐寰勶紙鍍忕礌锛夛細娌夸腑蹇冭/鍒楁壘 伪鈮?6 鐨勬渶杩滅偣锛屽彇涓よ€呯殑杈冨皬鍊笺€?    /// 涓轰粈涔堣閲忚€屼笉鏄畻锛氱湡瀹炲崐寰勭敱鐣岄潰娓叉煋锛堝惈 DPI 缂╂斁涓庢姇褰憋級鍐冲畾锛岀‖绠楀鏄撳樊鍑犲儚绱犮€?    /// 鎶曞奖鐨?伪 寰堝皬锛堚墹30 涓婁笅锛夛紝杩涗笉浜?96 杩欓亾闂紝鎵€浠ラ噺鍒扮殑鏄悆鏈韩銆?    ///
    /// 鈿?2026-10-01 璧?*娌℃湁璋冪敤**锛堝浘鏍囨崲鎴愪簡鏂硅鐨勩€岀櫧鐮栵紜澶х瑪銆嶏紝涓嶅啀闇€瑕佸渾褰㈣挋鐗堬級銆?    ///   鐣欑潃鏄粰"浠ュ悗鐪熻鍋氬渾褰㈠浘鏍?鐨勯偅涓€鍒伙細杩炲悓涓嬮潰閭ｄ釜 <see cref="MaskIconToCircle"/>锛?    ///   涓ゆ潯閮藉湪"鐞?鐨勭増鏈笂瀹炴祴杩囷紙韪╄繃鐨勫潙鍐欏湪娉ㄩ噴閲岋紝姣斾唬鐮佸€奸挶锛夈€?    /// </summary>
    private static int IconRadiusPx(byte[] bgra, int w, int h)
    {
        int cx = w / 2, cy = h / 2, rx = 0, ry = 0;
        for (int x = 0; x < w; x++) if (bgra[(cy * w + x) * 4 + 3] >= 96) rx = Math.Max(rx, Math.Abs(x - cx));
        for (int y = 0; y < h; y++) if (bgra[(y * w + cx) * 4 + 3] >= 96) ry = Math.Max(ry, Math.Abs(y - cy));
        return Math.Max(1, Math.Min(rx, ry) - 1);      // 鍐嶆敹 1px锛氭妸鎶楅敮榻块偅鍦堜篃鏀惰繘鍘?    }

    /// <summary>
    /// 鍦嗗舰 alpha 钂欑増锛氬渾澶栧叏閫忔槑锛岃竟缂?1px 绾挎€ц繃娓★紙鎶楅敮榻匡級銆?    /// 鈿?鏁版嵁鏄?*棰勪箻** BGRA锛屽洓涓垎閲忚涓€璧蜂箻鈥斺€斿彧鎶?伪 娓呴浂浼氱暀涓嬮粦杈癸紙棰勪箻鐨勭粡鍏稿潙锛夈€?    /// </summary>
    private static void MaskIconToCircle(byte[] bgra, int w, int h, int radius)
    {
        float cx = w / 2f, cy = h / 2f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float k = Math.Clamp(radius - d + 0.5f, 0f, 1f);      // 鍦嗗唴 1銆佸渾澶?0銆佽竟缂樿繃娓?                if (k >= 1f) continue;
                int o = (y * w + x) * 4;
                bgra[o]     = (byte)(bgra[o]     * k);
                bgra[o + 1] = (byte)(bgra[o + 1] * k);
                bgra[o + 2] = (byte)(bgra[o + 2] * k);
                bgra[o + 3] = (byte)(bgra[o + 3] * k);
            }
        }
    }

    /// <summary>BGRA锛圖2D 鐨?*棰勪箻**鏍煎紡锛夆啋 GDI+ 浣嶅浘銆傚儚绱犺鏄嚜涓婅€屼笅鐨勩€?/summary>
    private static System.Drawing.Bitmap BgraToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                             System.Drawing.Imaging.ImageLockMode.WriteOnly,
                             System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(bgra, y * w * 4, row, 0, w * 4);
                Marshal.Copy(row, 0, IntPtr.Add(d.Scan0, y * d.Stride), w * 4);
            }
        }
        finally { bmp.UnlockBits(d); }
        return bmp;
    }

    /// <summary>
    /// 鍐欎竴涓?*澶氬昂瀵?ICO**锛堟瘡妗ｄ竴寮?PNG鈥斺€擵ista 浠ュ悗绯荤粺灏辫杩欑锛夈€?    ///
    /// 鏍煎紡寰堝皬锛屾墜鍐欐瘮寮曠涓夋柟搴撶渷浜嬶細6 瀛楄妭鐩綍澶?锛?姣忔。 16 瀛楄妭鐩綍椤?锛?鍚勬。 PNG 鏁版嵁銆?    /// 娉ㄦ剰鐩綍椤归噷瀹介珮鍚勫彧鍗?*涓€涓瓧鑺?*锛?56 瑕佸啓鎴?0锛堣繖鏄?ICO 鏍煎紡鐨勭害瀹氾紝涓嶆槸绗旇锛夈€?    /// </summary>
    private static void WriteMultiSizeIco(string path, byte[] bgra, int bw, int bh, int[] sizes)
    {
        using var src = BgraToBitmap(bgra, bw, bh);
        var blobs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var one = new System.Drawing.Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(one))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, s, s));
            }
            using var ms = new MemoryStream();
            one.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            blobs.Add(ms.ToArray());
        }

        using var fs = File.Create(path);
        using var wr = new BinaryWriter(fs);
        wr.Write((ushort)0); wr.Write((ushort)1); wr.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte wh = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            wr.Write(wh); wr.Write(wh);
            wr.Write((byte)0);            // 璋冭壊鏉块鑹叉暟锛堢湡褰╀笉鐢級
            wr.Write((byte)0);            // 淇濈暀
            wr.Write((ushort)1);          // 骞抽潰鏁?            wr.Write((ushort)32);         // 浣嶆繁
            wr.Write(blobs[i].Length);
            wr.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) wr.Write(b);
    }

    private bool SaveBgraAsBmp(string path, byte[] bytes, int w, int h)
    {
        if (bytes == null)
        {
            Console.WriteLine("绂诲睆鍑哄浘澶辫触锛? + (_windows[0].LastError ?? "娌℃湁鍗犵敤鐭╁舰"));
            return false;
        }

        // BMP锛氭枃浠跺ご 14 锛?淇℃伅澶?40锛屽儚绱犳寜 BGRA銆佽浠庝笅寰€涓婏紙琛屽 4 瀛楄妭瀵归綈锛夈€?        int stride = w * 4;
        int size = 54 + stride * h;
        var outBytes = new byte[size];
        void W16(int at, int v) { outBytes[at] = (byte)v; outBytes[at + 1] = (byte)(v >> 8); }
        void W32(int at, int v)
        {
            outBytes[at] = (byte)v; outBytes[at + 1] = (byte)(v >> 8);
            outBytes[at + 2] = (byte)(v >> 16); outBytes[at + 3] = (byte)(v >> 24);
        }
        outBytes[0] = (byte)'B'; outBytes[1] = (byte)'M';
        W32(2, size); W32(10, 54); W32(14, 40);
        W32(18, w); W32(22, h);
        W16(26, 1); W16(28, 32);
        W32(34, stride * h);
        for (int y = 0; y < h; y++)
            Array.Copy(bytes, (h - 1 - y) * w * 4, outBytes, 54 + y * stride, w * 4);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllBytes(path, outBytes);
            Console.WriteLine($"宸茬灞忓嚭鍥?{path}锛坽w}脳{h}锛?);
            ExitCode = 0;
            _quit = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("鍐欏嚭鍥惧け璐ワ細" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// **椤电晫绾挎憜鏍?*锛氱櫧鏉挎墦搴曘€侀摵涓夊睆鏉夸功锛屽嚭涓ゅ紶鍥锯€斺€?    ///   路 `<path>`锛氱浉鏈哄仠鍦ㄤ袱灞忎箣闂达紙椤电晫绾胯惤鍦ㄥ睆骞曚腑闂达紝鏈€濂界湅缁嗚妭锛夛紱
    ///   路 `<path>-瀵归綈.bmp`锛氱浉鏈烘濂藉仠鍦ㄧ 2 灞忥紙椤电晫绾垮帇鍦ㄥ睆骞曚笂涓嬫部锛夈€?    /// 涓轰粈涔堣涓ゅ紶锛氫竴鐪肩湅鍑?涓€灞忎竴椤?鐨勫垎鐣屽埌搴曢『涓嶉『鐪笺€佷細涓嶄細琚綋鎴愮敾闈笂鐨勮剰鐐广€?    /// </summary>
    private void PageShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        BoardOn = true;
        BoardColor = InkPalette.BoardPresets[0].Color;
        // --pattern N锛氬嚭鍥炬椂甯︿笂鐧芥澘搴曠汗锛? 鏃?/ 1 鏂规牸 / 2 妯嚎锛?-step 缁欓棿璺濓級
        {
            var cli0 = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(cli0, "--pattern");
            int si = Array.IndexOf(cli0, "--step");
            if (pi >= 0 && pi + 1 < cli0.Length && int.TryParse(cli0[pi + 1], out int pat))
            {
                float step = si >= 0 && si + 1 < cli0.Length && float.TryParse(cli0[si + 1], out float sv)
                             ? sv : 40f;
                SetBoardPatternFromUi(pat, step);
            }
        }

        for (int screen = 0; screen < 3; screen++)
            for (int row = 0; row < 4; row++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.10f, 0.11f, 0.14f, 1f), Width = 5f * DpiScale,
                };
                float y = _virtualY + screen * _virtualH + 240f + row * 380f;
                for (int i = 0; i <= 36; i++)
                    s.AddPoint(_virtualX + 420f + i * 52f, y + MathF.Sin(i * 0.4f) * 26f, 1f, i * 8);
                Doc.AddStroke(s);
            }

        // 绗?2 灞忓彸杈规爣涓€鍙?杩欓噷鏄 2 灞?锛岀湅鍥炬椂濂藉鍙?        var tag = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.13f, 0.36f, 0.24f, 1f), Width = 9f * DpiScale,
        };
        float ty = _virtualY + _virtualH + 150f;
        for (int i = 0; i <= 60; i++) tag.AddPoint(_virtualX + 2200f + i * 4f, ty + (i % 7) * 6f, 1f, i * 8);
        Doc.AddStroke(tag);

        void Shot(float offset, string outPath)
        {
            ViewOffsetY = offset;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = offset; }
            Doc.InvalidateAll();
            SettleFrames(600);
            bool ok = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);
            Console.WriteLine(ok ? $"宸插嚭鍥?{outPath}" : $"鍑哄浘澶辫触 {outPath}");
        }

        Shot(-_virtualH * 0.55f, path);                       // 鐣岀嚎钀藉湪灞忓箷涓棿
        string aligned = System.IO.Path.ChangeExtension(path, null) + "-瀵归綈.bmp";
        Shot(-_virtualH * 1.0f, aligned);                     // 姝ｅソ绗?2 灞?
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        _quit = true;
    }

    private void PanelShow(string path)
    {
        _panelShowMore = Environment.GetCommandLineArgs().Contains("--more");
        _panelShowMini = Environment.GetCommandLineArgs().Contains("--mini");
        _panelShowBand = Environment.GetCommandLineArgs().Contains("--band");
        SetUiFactory(() => new InkUi.FullUi());
        Tool = Tool.Pen;
        BoardOn = true;                      // 鐧芥澘鎵撳簳锛氭埅鍑烘潵鐨勫浘閲屾病鏈夋闈笂鐨勬潅涓滆タ
        // --pixel锛氭妸宸ュ叿鍒囨垚闈㈢Н姗＄毊鍐嶅嚭鍥撅紙鐪?姗＄毊鎸夐挳鐨勫浘鏍囪窡鐫€鎹?閭ｄ竴鐗堬級
        if (Environment.GetCommandLineArgs().Contains("--pixel")) Tool = Tool.PixelEraser;
        if (Environment.GetCommandLineArgs().Contains("--eraser")) Tool = Tool.Eraser;
        if (Environment.GetCommandLineArgs().Contains("--laser")) Tool = Tool.Laser;
        if (Environment.GetCommandLineArgs().Contains("--hl")) Tool = Tool.Highlighter;
        // --select锛氭妸宸ュ叿鍒囨垚閫夋嫨锛堢湅"閫夋嫨閭ｄ竴鏍肩殑鍥炬爣"锛夛紱閰?--lasso 灏辨槸濂楃储閭ｄ竴妗?        //锛?026-09-26 鍔狅細閫夋嫨閭ｆ牸鐜板湪"閫変腑浠€涔堟樉绀轰粈涔?锛屼袱绉嶆閫夊悇涓€涓浘鏍囷級銆?        if (Environment.GetCommandLineArgs().Contains("--select")) Tool = Tool.Marquee;
        // --board-green / --board-black锛氭崲鏉胯壊鍐嶅嚭鍥撅紙鐪?鐧芥澘閭ｆ牸濉殑鏄摢鍧楁澘鐨勯鑹?锛?        {
            var argvB = Environment.GetCommandLineArgs();
            if (argvB.Contains("--board-green")) Host.Commands.SetBoardColor(InkPalette.BoardPresets[1].Color);
            if (argvB.Contains("--board-black")) Host.Commands.SetBoardColor(InkPalette.BoardPresets[2].Color);
        }
        // --pattern N / --step N锛氭妸鐧芥澘搴曠汗鎽嗘垚鏌愪竴妗ｅ啀鍑哄浘锛堢湅鑹插甫涓婇偅涓ゆ牸鏂囧瓧鍜屾。浣嶇偣锛夈€?        // 鍜?`--cell` / `--zoom` / `--dashline` 鍚屼竴绫伙細浜у搧閲屾病鏈夎繖涓叆鍙ｏ紝鍙湇鍔?鍥句笂鐪嬬粏鑺?銆?        {
            var argvP = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(argvP, "--pattern");
            if (pi >= 0 && pi + 1 < argvP.Length && int.TryParse(argvP[pi + 1], out int patArg))
            {
                int si = Array.IndexOf(argvP, "--step");
                float stepArg = si >= 0 && si + 1 < argvP.Length
                                && float.TryParse(argvP[si + 1], out float sv) ? sv : 40f;
                Host.Commands.SetBoardPattern(patArg, stepArg);
            }
        }
        // --board-on锛氭妸鐧芥澘鐪熺殑鎵撳紑鍐嶅嚭鍥俱€?*鍙奖鍝嶅浘**锛歚BoardOn = true` 閭ｅ彞璁剧殑鏄?App
        // 鑷繁鐨勫瓧娈碉紝寮曟搸閭ｈ竟鏄叧鐫€鐨勨€斺€旇€?鐧芥澘鑹插甫鏈€鍙崇閭ｄ釜 鉁?鍦ㄦ澘鍏崇潃鏃舵槸鍘嬫殫鐨?        //锛堣 FullUi.DrawBandAction锛夛紝鎵€浠ヨ鐪嬪畠姝ｅ父鐨勬牱瀛愬繀椤荤湡寮€涓€涓嬫澘銆?        if (Environment.GetCommandLineArgs().Contains("--board-on")) Host.Commands.SetBoard(true);
        // --shape <鍚嶅瓧>锛氭妸宸ュ叿鍒囨垚鏌愪竴绉嶅浘褰㈠啀鍑哄浘锛堢湅"涓绘潯閭ｄ竴鏍肩殑鍥炬爣璺熺潃绉嶇被鍙?锛夈€?        // 鍚嶅瓧鐢ㄥ懡浠よ閲岄偅濂楀皬鍐欏啓娉曪紝鍜?--shapetoolshow 鐨?--circle/--triangle 涓€鑷淬€?        {
            var argv = Environment.GetCommandLineArgs();
            int si = Array.IndexOf(argv, "--shape");
            if (si >= 0 && si + 1 < argv.Length)
            {
                Tool = argv[si + 1].ToLowerInvariant() switch
                {
                    "line" => Tool.Line,
                    "rect" or "rectangle" => Tool.Rectangle,
                    "ellipse" => Tool.Ellipse,
                    "circle" => Tool.Circle,
                    "triangle" => Tool.Triangle,
                    "parallelogram" => Tool.Parallelogram,
                    "arrow" => Tool.Arrow,
                    // 2026-09-20锛氬洓绉嶆洸绾匡紙鍑哄浘鐪?鍥惧舰鏍间袱琛?+ 鏂板浘鏍?鏃惰鐢級
                    "parabola" => Tool.Parabola,
                    "hyperbola" => Tool.Hyperbola,
                    "sine" => Tool.Sine,
                    "cosine" => Tool.Cosine,
                    "wave" => Tool.Wave,
                    "tangent" => Tool.Tangent,
                    "cylinder" => Tool.Cylinder,
                    "cone" => Tool.Cone,
                    "conefrustum" => Tool.ConeFrustum,
                    "sphere" => Tool.Sphere,
                    // 绔嬩綋閭ｄ竴鏃忥紙鍑哄浘鐪?鍥惧舰鏍肩浜岃 + 妗ｄ綅鐐?鏃惰鐢級锛氫笁涓殑鍚嶅瓧灏辨槸
                    // 瀹冧滑鍚勮嚜鐨勫伐鍏峰悕灏忓啓锛堟１鏌?/ 妫遍敟 / 妫卞彴锛夈€?                    "prism" => Tool.Prism,
                    "pyramid" => Tool.Pyramid,
                    "frustum" => Tool.Frustum,
                    _ => Tool.Line,
                };
            }
        }
        // 涓婇潰閭ｅ嚑琛屾敼鐨勬槸 **App 鑷繁鐨?Tool 瀛楁**锛岀晫闈笉浼氬洜姝ら噸绠?褰撳墠鏄摢涓€鏍?
        // 锛坄Tool` 鏄釜瑁稿瓧娈碉紝娌℃湁閫氱煡锛夈€傝€屽嚭鍥捐鐪嬬殑鎭版伆鏄?鍥惧舰閭ｄ竴鏍肩殑涓よ"锛?        // 鎵€浠ヨ繖閲?*璧颁竴娆＄湡鐨勬崲宸ュ叿**锛堝紩鎿?鈫?閫氱煡鐣岄潰 鈫?涓婂甫璺熺潃鍒囧埌鍥惧舰鏍硷級銆?        Host.Commands.SetTool(Tool);
        // 濂楃储閭ｄ竴妗ｈ鍗曠嫭璁撅紙瀹冨拰"閫夌殑鏄摢涓伐鍏?鏄袱鍥炰簨锛岃 SetSelectMode锛?        if (Environment.GetCommandLineArgs().Contains("--lasso"))
            Host.Commands.SetSelectMode(SelectMode.Lasso);
        SettleFrames(400);

        // --hide锛氳创杈归殣钘?*瑕佸厛鍐欒繘鍋忓ソ鍐嶆寕鐣岄潰**锛堢晫闈㈡槸鍦?Attach 閲岃鍋忓ソ鐨勶級锛?        // 鎸傚畬鍐嶆妸闇插ご涓€鎶婃寜鍒板簳銆傞厤鍚?--ball 鍑?鏀惰捣鎬侀湶澶?锛屼笉閰嶅氨鏄?灞曞紑鎬侀湶澶?銆?        bool wantHide = Environment.GetCommandLineArgs().Contains("--hide");
        if (wantHide)
        {
            SetUiPref("hide", "1");
            SetUiFactory(() => new InkUi.FullUi());
        }

        if (CurrentUi is InkUi.FullUi ui)
        {
            // --ball锛?*鍑烘敹璧锋€侊紙閭ｄ釜鐞冿級**鈥斺€斿惎鍔ㄩ粯璁ゅ氨鏄睍寮€鎬侊紝鎵€浠ヨ鏄惧紡璺?0锛?            // 涓嶄紶灏变繚鎸佸睍寮€锛堟敹璧?璐磋竟杩欎竴绫绘瘺鐥呭彧鍦ㄧ悆涓婄湅寰楄锛屾墍浠ヨ鑳藉崟鎷嶅畠锛夈€?            // 鈿?2026-10-03 淇細鍘熸潵鍐欑殑鏄?`if (!wantBall) ui.SnapForTest()`锛岃€岄偅涓€鍒荤殑
            //   `SnapForTest` 鏄?*璺冲埌鍙︿竴绔?*鐨勫紑鍏炽€佷笉鏄?灞曞紑"鈥斺€斿惎鍔ㄦ椂鏈潵灏卞睍寮€锛?            //   閭ｄ竴璺冲弽鑰屾妸榛樿鎬佹敹鎴愪簡鐞冿細**涓嶅甫 --ball 鍑虹悆銆佸甫 --ball 鍑哄睍寮€**锛?            //   鍜屾敞閲婃濂界浉鍙嶏紙鍑?README 涓诲浘鏃跺疄娴嬪埌锛夈€傜幇鍦ㄦ樉寮忚烦 0 / 璺?1銆?            bool wantBall = Environment.GetCommandLineArgs().Contains("--ball");
            ui.SetExpandForTest(wantBall ? 0f : 1f);
            if (wantHide) ui.ForcePeekForTest(0f);
            // --expand <0..1>锛氭妸"鐞?鈫?甯﹀瓙"鐨勫睍寮€杩涘害閽夊湪涓棿鏌愪竴甯э紙鏍稿鍔ㄧ敾鐢級
            {
                var argv = Environment.GetCommandLineArgs();
                int ei = Array.IndexOf(argv, "--expand");
                if (ei >= 0 && ei + 1 < argv.Length && float.TryParse(argv[ei + 1], out float ex))
                    ui.SetExpandForTest(ex);
            }
            if (_panelShowMini) ui.SetProfileForTest(0);     // --mini锛氭瀬绠€妗ｏ紙鐭兌鍥婏級
            if (_panelShowBand) ui.OpenRailForTest();        // --band锛氭妸鑹茬嚎寮犲紑鎴愯缃潯
            // --cell N锛氭妸涓婂甫鎺板埌绗?N 鏍煎啀鍑哄浘锛圢=2 灏辨槸鐧芥澘閭ｆ潯
            // `[涓婁竴灞廬 [鐧絔[缁縘[榛慮 [涓嬩竴灞廬` 锛?"绗?N 灞?锛?            var cli = Environment.GetCommandLineArgs();
            int ci = Array.IndexOf(cli, "--cell");
            if (ci >= 0 && ci + 1 < cli.Length && int.TryParse(cli[ci + 1], out int cellArg))
                ui.SelectBandCellForTest(cellArg);
            // --width N锛氬厛鎶婄矖缁嗚皟鎴?N 鍐嶅嚭鍥撅紙鐪?鐪熷疄澶у皬棰勮"鐢級
            int wi = Array.IndexOf(cli, "--width");
            if (wi >= 0 && wi + 1 < cli.Length && float.TryParse(cli[wi + 1], out float widthArg))
            {
                Host.Commands.SetWidth(widthArg);
                SettleFrames(120);
            }
            if (cli.Contains("--preview")) ui.ShowSizePreviewForTest();
            // --dashline N锛氭妸銆岀洿绾裤€嶉偅涓€鏍肩殑妗ｄ綅寰€鍚庡垏 N 涓嬶紙鐪?鍥炬爣璺熺潃绾垮瀷鎹?閭ｄ笁妗ｏ細
            // N=0 瀹炵嚎銆? 铏氱嚎銆? 鐐圭嚎锛涢厤 `--shape line` 鐢級銆備骇鍝侀噷娌℃湁杩欎釜鍏ュ彛锛?            // 瀹冨彧鏈嶅姟"鍥句笂鐪嬬粏鑺?锛堝拰 --zoom / --cell 鍚屼竴绫伙級銆?            {
                int dli = Array.IndexOf(cli, "--dashline");
                if (dli >= 0 && dli + 1 < cli.Length && int.TryParse(cli[dli + 1], out int dashArg))
                {
                    for (int k = 0; k < dashArg; k++) Host.Commands.CycleLineDash();
                    SettleFrames(120);
                }
            }
            // --paraaxis N锛氭妸銆屾姏鐗╃嚎銆嶉偅涓€鏍肩殑妗ｄ綅寰€鍚庡垏 N 涓嬶紙N=1 灏辨槸"宸﹀彸鎶涚墿"閭ｄ竴妗ｏ級銆?            // 鍜?--dashline 鍚屼竴绫伙紝鍙湇鍔?鍥句笂鐪嬬粏鑺?銆?            {
                int pai = Array.IndexOf(cli, "--paraaxis");
                if (pai >= 0 && pai + 1 < cli.Length && int.TryParse(cli[pai + 1], out int paArg))
                {
                    for (int k = 0; k < paArg; k++) Host.Commands.CycleParabolaAxis();
                    SettleFrames(120);
                }
            }
            // --dark锛氬嚭鍥惧墠鐩存帴鍒囨繁鑹蹭富棰橈紙**涓嶈惤鐩?*锛涘拰鐐广€屾繁鑹蹭富棰樸€嶉偅涓€琛屽悓涓€鏉¤矾锛?            if (Environment.GetCommandLineArgs().Contains("--dark")) ui.SetDarkForTest(true);
            // --more [--page N]锛氬嚭**涓ぎ銆屾洿澶氥€嶉潰鏉?*锛? 璇惧爞 / 1 澧ㄨ抗 / 2 璁剧疆锛夈€?            // 闈㈡澘鎵撳紑鏃?QueryBounds 鏄暣鍧楀睆骞曪紝鎴浘鑼冨洿鍦ㄤ笅闈㈠崟鐙锛堣 shot 閭ｄ竴娈碉級銆?            if (_panelShowMore)
            {
                ui.OpenMoreForTest();
                var argvMore = Environment.GetCommandLineArgs();
                int pi2 = Array.IndexOf(argvMore, "--page");
                if (pi2 >= 0 && pi2 + 1 < argvMore.Length && int.TryParse(argvMore[pi2 + 1], out int pageArg))
                    ui.SetMorePageForTest(pageArg);
            }
            SettleFrames(500);

            // --timerwin [--run|--edit|--min|--full] / --rollwin [--n 3] [--names]锛?            // 璇惧爞绐楃敾鍦?*寮曟搸娴眰**閲岋紝绂诲睆閭ｆ潯璺紙鍙敾鐣岄潰灞傦級鎷嶄笉鍒板畠锛?            // 鎵€浠ョ瓑瀹冪湡鐢诲嚭鏉ワ紝鐩存帴鎴睆骞曚笂鐨勭獥鍙ｇ煩褰紙1:1锛夈€?            if (Environment.GetCommandLineArgs().Contains("--timerwin")
                || Environment.GetCommandLineArgs().Contains("--rollwin"))
            {
                var argvW = Environment.GetCommandLineArgs();
                if (argvW.Contains("--timerwin"))
                {
                    OpenTimerCardFromUi();
                    SettleFrames(200);
                    var tw = TimerCardRect();
                    var cin = TimerWin.CardRect(tw, DpiScale, false);
                    float lu = TimerWin.Layout(cin, DpiScale);
                    void ClickAxis(in RectF r)
                    {
                        TimerPointerDownForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                        TimerPointerUpForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--stopwatch")) { StartTimerFromUi(TimerMode.Stopwatch, 0f); SettleFrames(900); }
                    else if (argvW.Contains("--run")) StartTimerFromUi(TimerMode.Countdown, 300f);
                    int tbi = Array.IndexOf(argvW, "--tab");
                    if (tbi >= 0 && tbi + 1 < argvW.Length && int.TryParse(argvW[tbi + 1], out int tabArg))
                    {
                        var tcin = TimerWin.CardRect(TimerCardRect(), DpiScale, false);
                        float tlu = TimerWin.Layout(tcin, DpiScale);
                        var tr = TimerWin.TabRect(tcin, tlu, Math.Clamp(tabArg, 0, 2));
                        TimerPointerDownForTest((tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f);
                        TimerPointerUpForTest((tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--full")) ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Fullscreen));
                    else if (argvW.Contains("--edit")) ClickAxis(TimerWin.ValueRect(cin, lu));
                    else if (argvW.Contains("--min")) ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Minimize));
                }
                if (argvW.Contains("--rollwin"))
                {
                    if (argvW.Contains("--names"))
                    {
                        string tmpNames = Path.Combine(Path.GetTempPath(), "inkteach-shot-names.txt");
                        File.WriteAllLines(tmpNames, new[] { "寮犱紵", "鏉庡", "鐜嬪己", "鍒樻磱", "闄堟櫒" });
                        InkEngine.InkEngine.NamesPathOverride = tmpNames;
                        ReloadNamesFromUi();
                    }
                    OpenRollCardFromUi();
                    int ri = Array.IndexOf(argvW, "--n");
                    if (ri >= 0 && ri + 1 < argvW.Length && int.TryParse(argvW[ri + 1], out int nArg) && nArg > 1)
                    {
                        var rw = RollCardRect();
                        float ru = RollUnit();
                        var pr = RollWin.PlusRect(rw, ru);
                        for (int k = 1; k < Math.Min(nArg, 60); k++)
                        {
                            RollPointerDownForTest((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
                            RollPointerUpForTest((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
                        }
                    }
                    StartRollFromUi();
                    double rt0 = NowMs;
                    while (RollingNow && NowMs - rt0 < 3000) { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
                }
                SettleFrames(400);
                // --board [--boardblack]锛氬嚭鍥惧墠閾轰竴灞傜櫧鏉匡紝璁╄儗鏅槸骞插噣鐨勩€?                // 璇惧爞绐楁槸**鎴睆**鍑烘潵鐨勶紙绂诲睆閭ｆ潯璺彧鐢荤晫闈㈠眰锛屾媿涓嶅埌瀹冿級锛屼笉鍔犺繖灞傜殑璇?                // 鍥鹃噷浼氭贩鐫€妗岄潰鍥炬爣鍜屽綋鏃剁殑绐楀彛鈥斺€旀嬁鍘诲綋瀹ｄ紶鍥惧緢闅剧湅銆?                if (argvW.Contains("--board"))
                {
                    Host.Commands.SetBoard(true);
                    if (argvW.Contains("--boardblack"))
                        Host.Commands.SetBoardColor(new Color4(0.13f, 0.15f, 0.17f, 1f));
                    SettleFrames(300);
                }
                if (argvW.Contains("--keepboard")) SetPassThroughFromUi(false);
                SettleFrames(300);
                var wr = argvW.Contains("--timerwin") ? TimerCardRect() : RollCardRect();
                bool okW = ScreenProbe.SaveBmp(path,
                    (int)MathF.Floor(wr.MinX), (int)MathF.Floor(wr.MinY),
                    Math.Max(1, (int)MathF.Ceiling(wr.MaxX - wr.MinX)),
                    Math.Max(1, (int)MathF.Ceiling(wr.MaxY - wr.MinY)));
                Console.WriteLine(okW
                    ? $"宸插嚭鍥?{path}锛堟埅灞?{wr.MaxX - wr.MinX:F0}脳{wr.MaxY - wr.MinY:F0}锛?
                    : "鍑哄浘澶辫触");
                ExitCode = okW ? 0 : 1;
                _quit = true;
                return;
            }

            // 浼樺厛绂诲睆鍑哄浘锛堥攣灞?/ 杩滅▼涔熻兘鍑猴紝鍥鹃噷涓嶆贩妗岄潰锛夈€?            // **鑼冨洿瑕佽繛"鐢诲埌澶栭潰閭ｄ竴鍦?涓€璧风粰**锛堟姇褰憋紝瑙?IOverlayUi.PaintMargin锛夛細
            // 杩欎竴鏉＄殑杈逛笂鐣欑櫧鍙湁 24 **鐗╃悊**鍍忕礌锛?00% 缂╂斁涓嬪氨鏄?12 閫昏緫鍍忕礌锛?            // 鑰屾姇褰辨渶杩滆儉鍑哄幓 16+8 閫昏緫鍍忕礌鈥斺€斾笉缁欏氨浼氳鍒囨帀涓€鍗婏紝鍥剧湅鐫€鍍?娌℃湁鎶曞奖"銆?            var b = ui.QueryBounds();
            // --more锛氶潰鏉挎墦寮€鏃跺崰鐢?= 鏁村潡灞忓箷锛岀収瀹冨嚭鍥句細寰楀埌涓€鏁村睆锛?0MB 绾э級銆?            // 璁捐楠屾敹瑕佺殑鏄偅鍧楀崱鐗?锛?鍛ㄥ洿涓€鍦堥伄缃╋紝鎵€浠ヨ鍒?闈㈡澘 锛?64"銆?            if (_panelShowMore)
            {
                var mr = ui.MoreRectForTest;
                b = new RectF
                {
                    MinX = mr.MinX - 64f, MinY = mr.MinY - 64f,
                    MaxX = mr.MaxX + 64f, MaxY = mr.MaxY + 64f,
                };
            }
            var shot = new RectF
            {
                MinX = b.MinX - ui.PaintMargin, MinY = b.MinY - ui.PaintMargin,
                MaxX = b.MaxX + ui.PaintMargin, MaxY = b.MaxY + ui.PaintMargin,
            };
            // --zoom N锛氬彧鍑?*绗?N 鏍肩殑杩戞櫙**锛?026-09-19 鍔狅紝鐪?鍥惧舰閭ｄ竴鏍肩殑鍥炬爣"鐢級銆?            // 瑁佸壀妗嗗氨鏄偅涓€鏍肩殑鐭╁舰鍐嶆澗 8 閫昏緫鍍忕礌鈥斺€斿缁欎竴鍦堟槸涓轰簡鍒妸鎸夐挳鐨勫渾瑙?            // 鍜岄€変腑搴曡壊鍒囨帀锛堝垏鎺変箣鍚庡浘涓婄湅鐫€鍍?鍥炬爣缂轰簡涓€鍧?锛夈€備骇鍝侀噷娌℃湁杩欎釜鍏ュ彛锛?            // 瀹冨彧鏈嶅姟"鍥句笂鐪嬬粏鑺?銆?            {
                int zi = Array.IndexOf(cli, "--zoom");
                if (zi >= 0 && zi + 1 < cli.Length && int.TryParse(cli[zi + 1], out int zoomCell))
                {
                    var cr = ui.CellRectForTest(zoomCell);
                    if (!cr.IsEmpty)
                        shot = new RectF
                        {
                            MinX = cr.MinX - 8f, MinY = cr.MinY - 8f,
                            MaxX = cr.MaxX + 8f, MaxY = cr.MaxY + 8f,
                        };
                }
            }
            if (OffscreenShot(path, shot)) return;
            int x = (int)MathF.Floor(b.MinX * DpiScale) - 30;
            int y = (int)MathF.Floor(b.MinY * DpiScale) - 30;
            int w = (int)MathF.Ceiling((b.MaxX - b.MinX) * DpiScale) + 60;
            int h = (int)MathF.Ceiling((b.MaxY - b.MinY) * DpiScale) + 60;
            bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
            Console.WriteLine(ok
                ? $"宸插嚭鍥?{path}锛堥潰鏉?{b.MaxX - b.MinX:F0}脳{b.MaxY - b.MinY:F0} 閫昏緫鍍忕礌锛?
                : "鍑哄浘澶辫触");
            ExitCode = ok ? 0 : 1;
        }
        _quit = true;
    }
    private void RadialShow(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 鍛煎嚭鐩樻憜鏍凤紙Ctrl+Alt+Shift+Q锛?==");

        // --dark锛氭繁鑹查偅妗ｄ篃鍑轰竴寮狅紙鍛煎嚭鐩樿窡鐫€ FloatingTheme 璧帮紝杩欓噷椤烘墜楠岃鎰燂級銆?        // 鑷妯″紡鐢ㄤ复鏃堕厤缃紙`_selfCheckMode`锛夛紝涓嶄細纰扮敤鎴风殑 settings.json銆?        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ShowHud = false;
        RadialTestHold = true;

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // 鈶?鎵撳紑锛堣繕娌″垝锛?        RadialOpenForTest(cx, cy);
        SettleFrames(400);
        bool ok1 = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // 鈶?鍒掑悜銆岀孩銆嶏紙姝ｄ笢锛?        RadialMoveForTest(cx + 120f, cy);
        SettleFrames(300);
        string path2 = System.IO.Path.ChangeExtension(path, null) + "-red.bmp";
        bool ok2 = ScreenProbe.SaveBmp(path2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        RadialCancelForTest("鎽嗘牱缁撴潫");
        RadialTestHold = false;
        Console.WriteLine(ok1 && ok2 ? $"鍑哄浘锛歿path} / {path2}" : "鎶撳睆澶辫触锛堢湅涓婁竴琛岋級");
        _quit = true;
    }
    private static byte[] MakeLegacyFile(InkDocument doc, int version, int droppedBytes)
    {
        byte[] cur = InkSerializer.Save(doc);
        byte[] old = droppedBytes > 0 ? new byte[cur.Length - droppedBytes] : cur;
        if (droppedBytes > 0) Array.Copy(cur, old, old.Length);
        // 榄旀暟锛? 瀛楄妭锛夊悗闈㈤偅涓皬绔?int32 灏辨槸鐗堟湰鍙枫€?        old[4] = (byte)version; old[5] = 0; old[6] = 0; old[7] = 0;
        return old;
    }
    private void AxisShowcase(string path)
    {
        Console.WriteLine($"=== 鍑哄浘锛歿path} ===");
        BoardOn = true;                          // 鐧藉簳锛氫笉鐒舵闈㈣儗鏅細娣疯繘鐢婚潰
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 鏉夸功鐨勮繎榛戣壊
        float x0 = _virtualX + 460f, y0 = _virtualY + 400f;
        // 澶栨锛堜袱寮犲潗鏍囩郴鍏辩敤锛夛細x0..x0+760 脳 y0..y0+620锛屾墍浠ュ崐瀹藉崐楂?= (380, 310)銆?        // **鐢绘硶鏀规垚浜?鎸変笅 = 鍘熺偣銆佹嫋鍑哄幓 = 灞曞紑"**锛堣 Stroke.SetAxisBox锛夛紝
        // 鎵€浠ヨ繖閲岀殑绗竴鍙傛暟鏄師鐐广€佺浜屽弬鏁版槸"寰€鍙充笅鎷栧杩?锛屼笉鍐嶆槸澶栨鐨勪袱涓銆?        const float hw = 380f, hh = 310f;

        // 鈶?鍧愭爣绯?路 甯︾綉鏍硷紝鍘熺偣鎽婂湪妗嗙殑宸︿笅锛堣€佸笀鏈€甯哥敤鐨?鍙暀绗竴璞￠檺"锛?        var g = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = true,
        };
        for (int i = 0; i < 3; i++) g.AddPoint(x0 + 130f, y0 + 470f, 1f, 0);
        g.SetAxisBox(x0 + 130f, y0 + 470f, x0 + 130f + hw, y0 + 470f + hh);
        Doc.AddStroke(g);

        // 鈶?鍧愭爣绯?路 涓嶅甫缃戞牸锛屽師鐐瑰湪妗嗘涓棿锛堝拰宸﹁竟瀵圭収锛?        var c = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = false,
        };
        for (int i = 0; i < 3; i++) c.AddPoint(x0 + 900f + hw, y0 + hh, 1f, 0);
        c.SetAxisBox(x0 + 900f + hw, y0 + hh, x0 + 900f + hw + hw, y0 + hh + hh);
        Doc.AddStroke(c);

        // 鈶?鏁拌酱锛氫竴鏉℃按骞崇嚎 + 鍙崇绠ご锛堟病鏈夊埢搴︼級
        var n = new Stroke
        {
            Tool = Tool.NumberLine, Kind = StrokeKind.NumberLine,
            Color = ink, Width = 4f * DpiScale,
        };
        for (int i = 0; i < 2; i++) n.AddPoint(x0, y0 + 760f, 1f, 0);
        n.SetAxisBox(x0, y0 + 760f, x0 + 1660f, y0 + 760f);
        Doc.AddStroke(n);

        // 鈶?涓€鏉¤櫄绾跨洿绾匡紙椤哄甫鎶婄嚎鍨嬩篃鐪嬭繘鍘伙細铏氱嚎鍦ㄧ矖绗斾笂鏄笉鏄儚鏍凤級
        var d = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = ink, Width = 7f * DpiScale, Dash = StrokeDash.Dashed,
        };
        d.AddPoint(x0, y0 + 960f, 1f, 0);
        d.AddPoint(x0 + 1660f, y0 + 960f, 1f, 0);
        Doc.AddStroke(d);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)(x0 - 60f), (int)(y0 - 60f), 1900, 1150);
        Console.WriteLine(ok ? $"  宸蹭繚瀛?{path}" : "  淇濆瓨澶辫触");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 鍑哄浘锛氬嚱鏁版洸绾垮悇鏉ヤ竴浠解€斺€?*鎶涚墿绾垮洓绉嶅紑鍙ｃ€佸弻鏇茬嚎涓や釜鏂瑰悜銆佹寮?/ 浣欏鸡鍚勪竴涓懆鏈熴€?    /// 娉㈡氮绾夸笁涓懆鏈熴€佹鍒囦竴鏀?*銆?    /// 鐢ㄦ硶 `--curveshow [璺緞]`锛岄粯璁?`reports/鍥涚鏇茬嚎.bmp`銆?    ///
    /// 鑷鐩殑鏄?鏁板€煎涓嶅"锛岃繖涓€寮犳槸缁?*鐪肩潧**鐪嬬殑锛堣繖鏉℃暀璁 璁″垝-鍥惧舰宸ュ叿.md 10.7锛夛細
    ///   路 鏇茬嚎鑳栦笉鑳栥€佸涓嶅椤猴紙娈垫暟鎸夊昂瀵稿畾锛屽お灏戜簡鐪嬬潃鏄姌绾匡級锛?    ///   路 鍙屾洸绾?*鎴柇鍒板摢**锛坄Stroke.HyperbolaTMaxOf`锛氬嚭妗嗗氨鍋滅瑪銆佹渶澶氱敾鍒?`2a`锛?    ///     澶煭鐪嬬潃鍍忎袱鏍圭煭绾裤€佸お闀垮氨鎶婂寘鍥寸洅鎾戝緱寰堝ぇ锛夛紱
    ///   路 鍥涚寮€鍙ｆ槸涓嶆槸鐪熺殑鍥涗釜鏂瑰悜锛堝乏鍙冲紑鍙ｆ槸"鎹㈣嚜鍙橀噺"锛屾渶瀹规槗鍐欏弽锛夛紱
    ///   路 姝ｅ鸡 / 浣欏鸡鐨?*璧风偣**瀵逛笉瀵癸紙姝ｅ鸡浠庤酱璧枫€佷綑寮︿粠宄拌捣锛夛紝鑰屼笖**閮芥槸涓€涓懆鏈?*锛?    ///   路 **娉㈡氮绾?*鏄笉鏄?寰堝涓懆鏈?锛堝拰姝ｅ鸡閭ｄ竴寮犳憜鍦ㄤ竴璧风湅锛屼竴鐪煎氨璇ュ垎寰楀紑锛夛紱
    ///   路 姝ｅ垏**涓や晶鐨勬笎杩戠嚎**鍦ㄤ笉鍦ㄣ€佹洸绾挎湁娌℃湁璐寸潃瀹冧笂鍘伙紙杩欐潯闈?`TangentMinAspect` 鍏滅潃锛?    ///     鎵€浠ヨ繖涓€寮?*鏁呮剰鍙嫋 60 楂?*锛氱敾鍑烘潵浠嶈鏄创鐫€娓愯繎绾跨殑閭ｄ竴鏀級銆?    /// 鎽嗘斁锛? 鍒?脳 3 琛岋紝绗竴琛屽洓绉嶅紑鍙ｃ€佺浜岃"鍙屾洸绾夸袱涓柟鍚?锛?姝ｅ鸡 / 浣欏鸡 / 娉㈡氮绾?/ 姝ｅ垏"銆?    /// 绗笁琛屽洓绉嶇珛浣撱€傜浜岃鎸や簡 6 涓浘褰紝鎵€浠ヤ綅缃槸鎸?涓嶇涓婇偦灞?閫愪釜鎽嗙殑锛堣鍚勬娉ㄩ噴锛夈€?    /// </summary>
    /// <summary>
    /// 鍑哄浘锛坄--smoothshow [璺緞]`锛夛細**鏇茬嚎鍖栧紑/鍏崇殑瀵圭収**锛?026-09-28锛夈€?    ///
    /// 鍚屼竴缁?鎵嬪啓鏍锋湰"鐢讳袱閬嶁€斺€斿厛鎸夌幇鍦ㄧ殑鎶樼嚎鍑?`-off`锛屽啀鎶婃洸绾垮寲鎵撳紑銆佹暣灞傞噸鐢诲嚭 `-on`锛?    ///   鈶?蹇姬锛氶噰鏍锋晠鎰忕█锛堟ā鎷?鍏虫帀 Windows Ink 鐨勭瑪 / 榧犳爣"锛夛紱
    ///   鈶?鐩磋锛氬繀椤诲師鏍蜂繚鐣欙紙鐢ㄦ埛鍞竴鐨勭‖绾︽潫锛夛紱
    ///   鈶?鎶涚墿绾匡細鎵嬬敾鏇茬嚎涓嶈琚敼褰紙涓婃鐮?淇濆舰骞虫粦"灏辨槸鍥犱负杩欎釜锛夛紱
    ///   鈶?閿娇锛氳繛缁皷瑙掞紝涓€涓兘涓嶈琚（鍦嗐€?    /// 濂戒笉濂界湅鍙兘闈犵溂鐫涳紝鎵€浠ュ崟寮€涓€鏉″嚭鍥惧懡浠わ紙鍒ゆ嵁浜ょ粰涓嬩竴杞湡浜哄姣旓級銆?    /// </summary>
    private void SmoothShowcase(string path)
    {
        Console.WriteLine($"=== 鍑哄浘锛氭洸绾垮寲瀵圭収锛坽path}锛?==");
        BoardOn = true;                          // 鐧藉簳锛氫笉鐒舵闈㈣儗鏅細娣疯繘鐢婚潰
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);
        var dotInk = new Color4(0.62f, 0.66f, 0.72f, 1f);   // 閲囨牱鐐圭殑灏忕伆鐐?        float penW = 6f * DpiScale;
        float ox = _virtualX + 120f, oy = _virtualY + 160f;

        void Add(IEnumerable<Vector2> pts, float dx, float dy, bool dots = true)
        {
            var list = new List<Vector2>();
            foreach (var p in pts) list.Add(new Vector2(p.X + dx, p.Y + dy));

            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = ink, Width = penW };
            foreach (var p in list) s.AddPoint(p.X, p.Y, 0.5f, 0);
            Doc.AddStroke(s);

            if (!dots) return;
            // 姣忎釜閲囨牱鐐规墦涓€涓皬鐏扮偣锛氭洸绾胯繃涓嶈繃鐐广€佺洿瑙掑姩娌″姩锛岀湅杩欎釜鏈€鐩磋
            foreach (var p in list)
            {
                var d = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = dotInk, Width = 2.4f * DpiScale,
                };
                d.AddPoint(p.X, p.Y, 0.5f, 0);
                Doc.AddStroke(d);
            }
        }

        // 鈶?蹇姬锛氫腑蹇?(ox+300, oy+280)銆佸崐寰?280銆佹瘡 13掳 涓€涓偣锛堚増65px 涓€娈碉紝鎶樿鑲夌溂鍙锛?        var arc = new List<Vector2>();
        for (int i = 0; i <= 14; i++)
        {
            float a = MathF.PI * i / 14f;
            arc.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(arc, ox + 300f, oy + 280f);

        // 鈶?鐩磋锛氫袱鏉¤噦鍚?6 涓偣锛堟闀?56锛?        var corner = new List<Vector2>();
        for (int i = 0; i <= 6; i++) corner.Add(new Vector2(i * 56f, 0f));
        for (int i = 1; i <= 6; i++) corner.Add(new Vector2(336f, i * 56f));
        Add(corner, ox + 760f, oy + 100f);

        // 鈶?鎶涚墿绾匡細y = x虏/500锛寈 鈭?[鈭?60, 260]锛堟墜鐢绘洸绾夸笉璁歌鏀瑰舰锛?        var parab = new List<Vector2>();
        for (int i = 0; i <= 13; i++)
        {
            float x = -260f + i * 40f;
            parab.Add(new Vector2(x, x * x / 500f));
        }
        Add(parab, ox + 1400f, oy + 120f);

        // 鈶?閿娇锛氬懆鏈?2 鐨勮繛缁皷瑙掞紙姣忎釜宄拌胺閮藉繀椤荤暀浣忥級
        var zig = new List<Vector2>();
        for (int i = 0; i <= 12; i++) zig.Add(new Vector2(i * 55f, (i % 2 == 0) ? 0f : 110f));
        Add(zig, ox + 1860f, oy + 100f);

        // 鈶?鎱㈢敾鐩磋锛?0掳 鎽婂湪 5 灏忔涓娿€?*姣忔鍙湁 20掳**锛堟瘮瑙掔偣闃堝€?35掳 灏忥級鈥斺€?        //    鍙湁 卤2 鐐圭獥鍙ｅ垽鎹厹寰椾綇锛涘眬閮ㄥ垽鎹湅鍒版瘡娈?20掳 鏄垽涓嶅嚭鏉ョ殑銆?        //
        //    鈿?2026-10-06 淇锛氬師鏉ヨ繖鏉＄敤鐨勬槸 **120px 涓€娈?*锛?90掳 鎽婂湪涓夊皬娈典笂"锛夈€?        //    閭ｄ釜鐐硅窛瀵瑰簲鐨勯€熷害鏄?130Hz 脳 120px 鈮?15600px/s锛屾槸"椋炵瑪"涓嶆槸"鎱㈢敾"锛?        //    鑰屼笖"90掳 鎽婂湪 3 涓?120px 鐐逛笂"涓?鍗婂緞 ~229px 鐨勫渾寮?鍦ㄦ暟瀛︿笂鏃犳硶鍖哄垎
        //    锛堝彧鏈?4 涓偣锛夛紝閫肩潃绐楀彛鍒ゆ嵁鍘昏浼ゅ渾寮р€斺€旀鏄?蹇€熺敾鍦嗗彉鎶樼嚎"鐨勬牴鍥犮€?        //    鏀规垚 11px 涓€娈碉紙鐪熉锋參鐢荤殑閲囨牱瀵嗗害锛変箣鍚庯紝瀹冩墠鐪熺殑鍦ㄦ祴瀹冨０绉拌娴嬬殑涓滆タ銆?        var slow = new List<Vector2>();
        {
            const float stepPx = 11f;            // 鐐硅窛锛堟參鐢伙細11px/鐐癸級
            int nPer = 6;                        // 鍏堢洿璧?6 娈?            float ang5 = 0f;
            var p = new Vector2(0f, 0f);
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            for (int i = 0; i < 5; i++)          // 杞?5 娈?脳 20掳 = 100掳
            {
                ang5 += 20f * MathF.PI / 180f;
                slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx;
            }
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            slow.Add(p);
        }
        Add(slow, ox + 760f, oy + 520f);

        // 鈶?瀵嗛噰鏍峰姬锛?*鍜岀湡绗旓紙Windows Ink 寮€锛変竴涓噺绾?*锛堟瘡 1掳 涓€涓偣 鈮?4.9px 涓€娈碉級銆?        // 瀹冨洖绛旂殑鏄?鎷挎墜鍐欐澘璇曚负浠€涔堢湅涓嶅嚭鍖哄埆"锛氱偣瀵嗗埌涓€瀹氱▼搴︼紝鎶樼嚎鍜屾洸绾垮氨鏄悓涓€鏍圭嚎
        //锛堝鸡闀?c銆佸崐寰?R 鏃朵腑鐐瑰亸宸?鈮?c虏/8R锛宑=5銆丷=280 鈫?0.011px锛屾瘮涓€涓儚绱犲皬涓や釜鏁伴噺绾э級銆?        var dense = new List<Vector2>();
        for (int i = 0; i <= 180; i++)
        {
            float a = MathF.PI * i / 180f;
            dense.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(dense, ox + 1560f, oy + 700f, dots: false);

        // 鈶?**绋€鐤忓揩寮?*锛堢敤鎴?2026-10-06 鎶ョ殑閭ｄ釜锛夛細鍗婂緞 150銆佹瘡 22.5掳 涓€涓偣
        //锛堝鸡闀?= 2脳150脳sin(11.25掳) 鈮?58.5px 涓€娈碉級銆傝繖鏄?蹇€熺敾鍦?/ 鎵嬪啓鏉垮叧鎺?        //  Windows Ink"鐨勯噰鏍峰瘑搴︺€?        //
        // 涓轰粈涔堝崟鍒楄繖涓€鏉★細绐楀彛鍒ゆ嵁閲忓埌鐨勮浆瑙?鈮?2脳(鐐硅窛/鍗婂緞)锛?        // 杩欓噷 = 2脳58.5/150 = 44.7掳 > 闃堝€?35掳 鈫?**姣忎釜鐐归兘琚垽鎴愯鐐?* 鈫?        // 鍗婂渾閫€鍖栨垚 8 杈瑰舰銆備慨鍓嶄慨鍚庝竴鐪艰兘鐪嬪嚭鏉ワ紙瀵圭収鐢?`--smoothcornerpx 0`锛夈€?        var sparse = new List<Vector2>();
        for (int i = 0; i <= 8; i++)
        {
            float a = MathF.PI * i / 8f;
            sparse.Add(new Vector2(MathF.Cos(a) * 150f, -MathF.Sin(a) * 150f));
        }
        Add(sparse, ox + 1120f, oy + 380f);

        // 鈶?**鐪熸満澶嶇幇**锛堢敤鎴?2026-10-07 鐨勫疄娴嬫暟鎹級锛氥€宨nk 鍏炽€佸揩閫熺敾鍦嗐€嶉偅涓€妗?        //锛坄鍘嬫劅=False 娲荤瑪=False 鐐?11 鍧囪窛 38.9px 鎬婚暱 389px`锛夈€?        //
        // 鍏抽敭绠楁湳锛?*鍦嗕笂姣忎釜椤剁偣鐨勮浆瑙?= 360掳/娈垫暟**锛屼笌鍗婂緞鏃犲叧銆?        //   11 鐐?鈫?10 娈?鈫?**36掳/椤剁偣**锛屽垰濂借秺杩?35掳 闃堝€硷紱
        //   ink 寮€鐨?36 鐐?鈫?35 娈?鈫?10.3掳/椤剁偣锛屽樊寰楄繙銆?        // 鎵€浠ヨ繖涓€鏉¤澶嶇幇鐨勬槸"**灞€閮ㄥ垽鎹憼鍦ㄧ█閲囨牱涓嬫妸鍏夋粦鍦嗗姬鍒ゆ垚瑙掔偣**"锛?        // 鍜岀敤渚嬧懄锛堥偅鏉℃槸绐楀彛鍒ゆ嵁鈶㈣秺鐣岋級鏄?*涓や釜涓嶅悓鐨勬瘺鐥?*銆?        var realcase = new List<Vector2>();
        {
            const int nSeg = 10;                 // 11 涓偣 = 10 娈?            float R = 389f / (2f * MathF.PI);    // 鎬婚暱 389px 鈫?鍗婂緞 鈮?61.9px
            // **蹇呴』缁曟垚鎺ヨ繎闂悎鐨勪竴鍦?*锛?1 鐐圭粫 355掳 鈫?姣忔 35.5掳锛屽垰濂借秺杩?            // 35掳 闃堝€硷紙灞€閮ㄥ垽鎹憼锛夈€傜粫寰楀皯涓€鐐癸紙姣斿鍗婂湀锛夋瘡娈靛彧鏈?17掳锛?            // 澶嶇幇涓嶅嚭鏉モ€斺€旂涓€鐗堝氨鏄繖涔堝仛鐨勶紝鍑哄浘鏄厜婊戠殑锛岀櫧娴嬩竴杞€?            for (int i = 0; i <= nSeg; i++)
            {
                float a = 355f * MathF.PI / 180f * i / nSeg - 355f * MathF.PI / 180f * 0.5f;
                realcase.Add(new Vector2(MathF.Cos(a) * R, -MathF.Sin(a) * R));
            }
        }
        Add(realcase, ox + 420f, oy + 760f);

        // 鈶?**鐪熸満澶嶇幇路妞渾**锛堢敤鎴?2026-10-07 绗簩杞暟鎹級锛氫粬鐨勫舰鐘朵笉鏄渾锛屾槸
        //    鍛ㄩ暱 515~564px 鐨?*妞渾**锛堚増200脳150锛夛紝10~14 涓偣銆佺偣璺?40~60px銆?        //
        //    涓轰粈涔堝繀椤诲崟鍒楋細妞渾涓?*杞鏄彉鍖栫殑**鈥斺€旂閮ㄦ洸鐜囧崐寰?= b虏/a 鈮?56px銆?        //    渚ч潰 = a虏/b 鈮?133px锛屾墍浠ョ閮ㄦ瘡椤剁偣杞 鈮?8掳銆佷晶闈㈠彧鏈?鈮?0掳锛?        //    涓棿杩炵画杩囨浮銆備换浣?鐩搁偦宸?寮忕殑鍒ゆ嵁濡傛灉瀹瑰樊鍙栧皬浜嗭紝绔儴鐓ф牱琚垏鎴愯
        //    鈥斺€旂涓€鐗堣繛缁€у宸?12掳 灏辨槸杩欎箞鏍界殑锛堝渾鐨勮浆瑙掑澶勭浉绛夛紝鑳借繃锛?        //    妞渾杩囦笉浜嗭級銆?        var ellipse = new List<Vector2>();
        {
            const float a = 100f, b = 75f;       // 鈮?鍛ㄩ暱 553px锛屽拰鐢ㄦ埛瀹炴祴涓€鑷?            const int n = 12;                    // 12 涓偣
            // 鎸?*寮ч暱鍧囧寑**鍙栨牱锛氱瓑寮ч暱琛岃蛋锛堟墜鐢绘椂鐐圭殑鍒嗗竷鎺ヨ繎杩欎釜锛?            // 涓嶆槸鎸夊弬鏁拌鍧囧寑鈥斺€旀寜瑙掑潎鍖€浼氳绔儴鐐瑰彉绋€锛屾祴涓嶅嚭鐪熷疄鍒嗗竷锛?            float perimeter = 0f;
            for (int k = 0; k < 720; k++)
            {
                float t0 = k * MathF.PI / 360f, t1 = (k + 1) * MathF.PI / 360f;
                float s0 = MathF.Sqrt(a * a * MathF.Sin(t0) * MathF.Sin(t0) + b * b * MathF.Cos(t0) * MathF.Cos(t0));
                float s1 = MathF.Sqrt(a * a * MathF.Sin(t1) * MathF.Sin(t1) + b * b * MathF.Cos(t1) * MathF.Cos(t1));
                perimeter += (s0 + s1) * 0.5f * (MathF.PI / 360f);
            }
            float step = perimeter / n;
            ellipse.Add(new Vector2(a, 0f));
            float theta = 0f;
            for (int i = 1; i <= n; i++)
            {
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    float d = 0.0005f;
                    acc += s * d;
                    theta += d;
                }
                float ang = theta - MathF.PI * 0.5f;   // 璁╄捣绗旇惤鍦ㄧ閮?                ellipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(ellipse, ox + 800f, oy + 760f);

        // 鈶?**鐪熸満澶嶇幇路涓嶅潎鍖€鐐硅窛**锛堢敤鎴?2026-10-07 绗笁杞暟鎹級锛?        //    浠栧疄娴嬬殑椤剁偣杞搴忓垪鏄?`51 18 17 24 30 31 37 22 41 35 27` /
        //    `37 42 35 70 27 13 23 69 60 16` 鈥斺€?**鍚屼竴涓厜婊戝舰鐘朵笂锛岃浆瑙掑湪 13掳~70掳 涔嬮棿璺?*銆?        //
        //    涓轰粈涔堬細杞 鈮?**鐐硅窛 / 灞€閮ㄦ洸鐜囧崐寰?*銆傛墜蹇€熺敾鏃剁偣璺濅笉鍧囧寑
        //    锛?0Hz 閲囨牱 + 鎵嬫姈锛夛紝浜庢槸杞涔熻窡鐫€璺炽€?*杩欎笉浠ｈ〃褰㈢姸鏈夎銆?*
        //    浠讳綍"杞 鈮?鍥哄畾缁濆鍊煎氨绠楄"鐨勫垽鎹兘浼氳瀹冮獥鍒?鈥斺€?杩欏氨鏄墠鍑犵増鍙嶅鏍界殑鍦版柟銆?        //
        //    杩欐潯鐢ㄤ緥灏辨槸鐢?妞渾 + 鍧囧寑寮ч暱鐐?+ 娌胯矾寰勬姈鍔?卤50% 鐐硅窛"澶嶇幇閭ｄ釜鍒嗗竷銆?        var jitterEllipse = new List<Vector2>();
        {
            const float a = 110f, b = 78f;
            const int n = 12;
            var rnd = new Random(20261007);
            float theta = 0f;
            jitterEllipse.Add(new Vector2(a, 0f));
            for (int i = 1; i <= n; i++)
            {
                // 鍩哄噯姝ラ暱鎸夊懆闀垮潎鍒嗭紝鍐嶄箻 0.5~1.6 鐨勬姈鍔紙澶嶇幇 60Hz 蹇€熸墜鐢荤殑鐐硅窛鍒嗗竷锛?                float baseStep = 553f / n;
                float step = (float)(baseStep * (0.5 + rnd.NextDouble() * 1.1));
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    acc += s * 0.0005f;
                    theta += 0.0005f;
                }
                float ang = theta - MathF.PI * 0.5f;
                jitterEllipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(jitterEllipse, ox + 1300f, oy + 760f);

        // 鈶?**鐐瑰瘑搴﹀鐓?*锛堢敤鎴?2026-10-07 闂?raw input 鍊间笉鍊?锛夛細
        //    鍚屼竴涓き鍦嗐€佸悓涓€鏉″姬闀胯矾寰勶紝鎸変袱绉嶇偣璺濆悇鐢讳竴鏉★細
        //      路 宸﹁竟 13 鐐?= 鍏?ink 鏃剁殑鐪熷疄鐐硅窛锛垀68Hz 脳 2900px/s 鈮?43px 涓€鐐癸級
        //      路 鍙宠竟 33 鐐?= 鎺ヤ笂 raw input 涔嬪悗鐨勭偣璺濓紙~170Hz 鈮?17px 涓€鐐癸級
        //    宸﹁竟鏄?鎴戜滑鐜板湪鑳芥嬁鍒扮殑"锛屽彸杈规槸"raw input 鑳芥嬁鍒扮殑"銆?        //    涓ゆ潯閮借繃鍚屼竴濂楁洸绾夸唬鐮侊紝宸埆**鍙湪閲囨牱瀵嗗害**鈥斺€旀墍浠ヨ繖寮犲浘鐩存帴鍥炵瓟
        //    "澶氬嚭鏉ョ殑閭?60% 杈撳叆鍒板簳涔板埌浜嗕粈涔?銆?        void DensePair(float dx, int n, bool dots)
        {
            var pts = new List<Vector2>();
            const float a2 = 120f, b2 = 88f;
            for (int i = 0; i <= n; i++)
            {
                float ang = MathF.PI * 1.9f * i / n - MathF.PI * 0.95f;
                pts.Add(new Vector2(MathF.Cos(ang) * a2, -MathF.Sin(ang) * b2));
            }
            Add(pts, dx, oy + 1180f, dots);
        }
        DensePair(ox + 260f, 13, dots: true);    // 鐜板湪锛堝叧 ink 鐨勭湡瀹炲瘑搴︼級
        DensePair(ox + 760f, 33, dots: true);    // raw input 涔嬪悗

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string off = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-off" + ext);
        string on = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-on" + ext);

        // 鎶樼嚎閭ｅ紶锛氬厛鍏虫帀寮€鍏筹紝鏁村眰閲嶇敾锛堢増鏈彿浼氶《鎺夋瘡鏉＄瑪鐨勫嚑浣曠紦瀛橈級
        bool wasEnabled = StrokeSmoothing.Enabled;
        StrokeSmoothing.SetEnabled(false);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok1 = ScreenProbe.SaveBmp(off, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);

        // 鏇茬嚎閭ｅ紶锛氭墦寮€寮€鍏筹紝鍚屼竴鎵圭瑪閲嶇敾涓€閬?        StrokeSmoothing.SetEnabled(true);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok2 = ScreenProbe.SaveBmp(on, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);
        StrokeSmoothing.SetEnabled(wasEnabled);     // 鍑哄畬鍥炬仮澶嶅師鐘舵€侊紝鍒妸鍚庨潰鐨勮矾寰勫甫鍋?
        Console.WriteLine(ok1 ? $"  鎶樼嚎锛歿off}" : "  鎶樼嚎鍥句繚瀛樺け璐?);
        Console.WriteLine(ok2 ? $"  鏇茬嚎锛歿on}" : "  鏇茬嚎鍥句繚瀛樺け璐?);
        Console.WriteLine();
        _quit = true;
    }
    private static (int count, float maxDist, float x, float y) DiffFarFromTip(
        byte[] a, byte[] b, int w, int h, int ox, int oy, float tipX, float tipY, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = MathF.Sqrt((px - tipX) * (px - tipX) + (py - tipY) * (py - tipY));
                if (dist <= minDist) continue;      // 绗斿皷闄勮繎鏈潵灏辨槸鏂板ⅷ锛屽厑璁稿彉
                count++;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy);
    }

    /// <summary>
    /// 婊氬姩姣斿锛氫袱寮犲悓灏哄鍥炬寜"鍐呭鏁翠綋涓婄Щ <paramref name="shift"/> 鍍忕礌"閫愬儚绱犲
    /// 锛坅[y] 搴旂瓑浜?b[y+shift]锛夈€傝繑鍥炰笉鍖归厤鐨勫儚绱犳暟锛堜寒搴﹀樊 &gt; 24锛夈€?    /// 鐢ㄦ潵鎵?婊氬姩鏃舵煇鏉℃í甯︽病鏇存柊"鈥斺€旈偅浼氳〃鐜版垚涓€鏁磋涓嶅尮閰嶃€?    /// </summary>
    private static int CountShiftMismatch(byte[] a, byte[] b, int w, int h, int shift)
    {
        if (shift <= 0 || shift >= h) return int.MaxValue;
        int bad = 0;
        for (int y = 0; y + shift < h; y++)
        {
            int ra = y * w * 4, rb = (y + shift) * w * 4;
            for (int x = 0; x < w; x++)
            {
                int oa = ra + x * 4, ob = rb + x * 4;
                int d = Math.Abs(a[oa] - b[ob]) + Math.Abs(a[oa + 1] - b[ob + 1]) + Math.Abs(a[oa + 2] - b[ob + 2]);
                if (d > 24) bad++;
            }
        }
        return bad;
    }
    private static int FirstLineCenter(byte[] cap, int w, int h, int x)
    {
        if (cap == null) return -1;
        int run = -1;
        for (int y = 0; y < h; y++)
        {
            int o = (y * w + x) * 4;
            bool dark = cap[o] < 225 && cap[o + 1] < 225 && cap[o + 2] < 225;
            if (dark) { if (run < 0) run = y; }
            else if (run >= 0) return (run + y - 1) / 2;
        }
        return -1;
    }

    /// <summary>涓ゅ紶鍚屽昂瀵?BGRA 鍥剧殑宸紓鍍忕礌鏁帮紙浜害宸?&gt; 24锛夈€?/summary>
    private static int DiffCount(byte[] a, byte[] b, int w, int h)
    {
        int n = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
            if (d > 24) n++;
        }
        return n;
    }

    /// <summary>
    /// 涓ゅ紶鍚屽昂瀵?BGRA 鍥剧殑宸紓锛屽彧鐪?*绂绘姌绾?<paramref name="path"/> 鐨勫墠
    /// <paramref name="pathCount"/> 涓偣 &gt; minDist 鐨勯偅浜涘儚绱?*銆?    /// 杩斿洖锛堣繖鏍风殑鍍忕礌鏁般€佸叾涓渶杩滈偅涓埌璺緞鐨勮窛绂汇€佸潗鏍囷級銆?    /// </summary>
    private static (int count, float maxDist, float x, float y,
                    int minX, int minY, int maxX, int maxY) DiffFarFromPath(
        byte[] a, byte[] b, int w, int h, int ox, int oy,
        List<Vector2> path, int pathCount, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = DistToPath(path, pathCount, px, py);
                if (dist <= minDist) continue;      // B 鑷繁鐢荤殑 / 鐩栦綇鐨勶紝鍏佽鍙?                count++;
                if (i < minX) minX = i;
                if (i > maxX) maxX = i;
                if (j < minY) minY = j;
                if (j > maxY) maxY = j;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy, minX, minY, maxX, maxY);
    }

    /// <summary>鐐瑰埌鎶樼嚎锛堝墠 count 涓偣锛夌殑鏈€鐭窛绂汇€?/summary>
    private static float DistToPath(List<Vector2> path, int count, float x, float y)
    {
        int n = Math.Min(count, path.Count);
        if (n <= 0) return float.MaxValue;
        if (n == 1) return Vector2.Distance(path[0], new Vector2(x, y));
        float best = float.MaxValue;
        for (int i = 1; i < n; i++)
        {
            float d = DistToSegment(path[i - 1].X, path[i - 1].Y, path[i].X, path[i].Y, x, y);
            if (d < best) best = d;
        }
        return best;
    }

    private static float DistToSegment(float ax, float ay, float bx, float by, float px, float py)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    private static (float[] width, float[] center) InkProfile(byte[] bgra, int w, int h)
    {
        var width = new float[w];
        var center = new float[w];
        for (int i = 0; i < w; i++)
        {
            int min = int.MaxValue, max = -1;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                if (bgra[o] + bgra[o + 1] + bgra[o + 2] < 600)
                {
                    if (j < min) min = j;
                    if (j > max) max = j;
                }
            }
            width[i] = max >= 0 ? max - min + 1 : -1f;
            center[i] = max >= 0 ? (min + max) * 0.5f : -1f;
        }
        return (width, center);
    }

    /// <summary>
    /// `--tailjumptest`锛?*棰勬祴灏?绐佺獊绐佸線澶栬烦"鐨勪笓椤规娴?*锛?026-09-28 鐢ㄦ埛瀹炴祴锛?    /// "榧犳爣妯″紡涓嬫墜鍐欐澘鍐欏瓧锛屾湯绔€讳細绐佺獊绐佸線澶栬烦锛孖nk 寮€浜嗗氨娌℃湁浜?锛夈€?    ///
    /// 鐥囩姸鐨勬満鐞嗭細榧犳爣妯″紡涓嬫病鏈夊鎵樿建杩癸紝棰勬祴灏剧敱鎴戜滑鑷繁鐢伙紱鑰岄紶鏍?鍏煎妯″紡鐨勮緭鍏?    /// 鏄?*绐佸彂**鐨勶紙鎶ュ嚑涓偣銆佸仠涓€涓嬨€佸啀鎶ュ嚑涓偣锛夈€傛潵涓€闃靛ソ鏁版嵁 鈫?鏈€熷害绠楀緱澶?鈫?    /// 灏惧反鐢╁埌鍓嶉潰锛涢棿闅斾竴瓒?`MaxGapMs`(20ms) 鈫?閫熷害娓呴浂 鈫?灏惧反娌′簡锛涘啀鏉ユ暟鎹張鐢╁嚭鏉ャ€?    ///
    /// 鍒ゆ嵁鏄?*绗斿皷鐨勯€愬抚杞ㄨ抗**锛堝搧绾㈠ⅷ鐨勬渶鍙冲垪锛夛細
    ///   路 `寰€鍓嶈烦鐨勬渶澶у箙搴锛氫竴甯ч噷绗斿皷绐佺劧鍓嶈繘澶氬皯锛?    ///   路 `寰€鍚庨€€鐨勬鏁?/ 鏈€澶у悗閫€`锛氬熬宸存秷澶辨椂绗斿皷浼?*缂╁洖鍘?*鈥斺€旇繖灏辨槸"绐佺獊"鐨勬韬€?    /// 瀵圭収缁?`--off` 搴斿綋鏄庢樉鏇寸ǔ锛堣繖灏辨槸杩欐潯娴嬭瘯鐨勮嚜璇侊級銆?    /// 娴嬭瘯**鑷繁鎽嗙姸鎬?*锛堜笉渚濊禆鍚姩榛樿鍊硷級锛氶粯璁ゆ祴"寮€棰勬祴"锛宍--off` 娴嬪鐓с€?    /// </summary>
    // [閲嶅惎 2026-10-08] B4 棰勬祴灏?绐佺獊璺?妫€娴嬶紙鑰佺郴缁?2026-10-05 鍋滅敤锛屾湰娆℃帴鍥炴柊灏撅級锛?    // 鏂板熬鐨勭邯寰嬶紙闀垮害闄愰€?+ 鏂瑪鍚庣紦鎱㈠洖鏀讹級灏辨槸瀵圭潃涓嬮潰杩欐"鏈虹悊"淇殑銆?    private void TailJumpTest(bool noisy = false, bool predictOff = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 棰勬祴灏锯€滅獊绐佽烦鈥濇娴嬶紙鍚堟垚榧犳爣锛岀獊鍙戣妭濂忥級===");
        // 娴嬭瘯鑷繁鎽嗙姸鎬侊紙涓嶄緷璧栧惎鍔ㄩ粯璁ゅ€硷級锛氶粯璁ゆ祴"寮€棰勬祴"锛宍--off` 娴嬪鐓?        bool predOn = !predictOff;
        global::InkEngine.InkEngine.PredictTailEnabled = predOn;
        Console.WriteLine($"  棰勬祴={(predOn ? "寮€" : "鍏筹紙瀵圭収锛?)}锛涜妭濂?{(noisy ? "蹇藉ぇ蹇藉皬 + 闂撮殧涓嶉綈锛堝儚鐪熼紶鏍囷級" : "鍥哄畾姝ラ暱 + 姣?4 鐐逛竴鍋?)}");

        // 鈹€鈹€ B4.2锛?026-10-08 鐪熸満鍙嶉锛夛細**鎬ヨ浆**鏃朵笉璁哥暀"鏃ф柟鍚?鐨勫熬锛?*绾紩鎿庯紝涓嶈蛋灞忓箷/鍚堟垚榧犳爣**锛?        //    鎵€浠ユ斁鍦?SkipIfNoSyntheticInput 涔嬪墠鈥斺€旈紶鏍囧繖鐨勬椂鍊欒繖椤逛篃鐓ф牱鑳借窇锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        //   鐢ㄦ埛锛堝鏍″ぇ灞?+ 鎵嬪啓鏉匡級锛?鍐欏揩鐨勬椂鍊欐€ヨ浆/鎬ユ嫄寮紝鍋跺皵浼氳烦涓€涓?銆?        //   鏈虹悊锛氭棫鏂瑰悜鐨勫熬鐣欏湪灞忓箷涓娿€佸啀鎸夋參閫熺巼缂╁洖 = 閭ｄ竴"璺?銆?        //   鐜板湪涓ら亾闃茬嚎锛氶娴嬪櫒鑷繁鐨?*鏂瑰悜鑷€傚簲寮ч暱鏀剁缉**锛?026-10-15 绾挎€ч娴嬪唴缃紱
        //   鎬ヨ浆閭ｄ竴甯х殑灏鹃暱鎸夋柟鍚戜笉绋冲害鑷姩缂╋級+ 澶辨晥蹇敹锛坣=0 鏃?0.72px/ms锛夈€?        //   蹇啓鍚戝彸 鈫?鎬ヨ浆 75掳 鈫?涓ゅ抚鍐?娌挎棫鏂瑰悜浼稿嚭"瑕?鈮?px銆?        if (predOn)
        {
            Doc.Clear();
            Doc.ClearHistory();
            float bx = 400f, by = 300f;
            DwellBeginForTest(bx, by);
            double t0 = NowMs;
            for (int i = 1; i <= 20; i++)          // 蹇啓锛?ms 涓€鐐广€佹瘡鐐?12px锛?3px/ms锛屾楂橀€燂級
            {
                NowMs = t0 + i * 4;
                DwellMoveForTest(bx + i * 12f, by);
            }
            UpdatePredictTail(); UpdatePredictTail(); UpdatePredictTail();
            bool hadTail = PredictTailActive;
            float rad = 75f * MathF.PI / 180f;     // 鎬ヨ浆 75掳锛氬悜涓?            for (int k = 1; k <= 2; k++)
            {
                NowMs = t0 + (20 + k) * 4;
                DwellMoveForTest(bx + 240f + 12f * k * MathF.Cos(rad),
                                       by + 12f * k * MathF.Sin(rad));
            }
            UpdatePredictTail(); UpdatePredictTail();
            float lastX = bx + 240f + 24f * MathF.Cos(rad);
            float lead = float.MinValue;
            foreach (var pt in PredictTailPoints) lead = MathF.Max(lead, pt.X - lastX);
            if (lead == float.MinValue) lead = 0f; // 灏炬敹骞插噣浜嗭細鏇磋皥涓嶄笂"鏃ф柟鍚戞畫鐣?
            bool turnOk = hadTail && lead <= 4f;
            Console.WriteLine($"  鎬ヨ浆 75掳锛氳浆鍓嶅熬={(hadTail ? "鏈? : "鏃?)}锛?
                              + $"杞悗娌挎棫鏂瑰悜鏈€杩滀几鍑?{lead:F1}px锛堣姹?鈮?锛?);
            Console.WriteLine(turnOk
                ? "  PASS: 鎬ヨ浆澶勬病鏈?鏃ф柟鍚?鐨勫熬娈嬬暀"
                : $"  FAIL: 鎬ヨ浆澶勬畫鐣欐棫鏂瑰悜鐨勫熬 {lead:F1}px锛堟垨杞墠灏惧氨娌￠暱鍑烘潵锛?);
            DwellEndForTest();
            Doc.Clear();
            Doc.ClearHistory();
        }

        if (SkipIfNoSyntheticInput("棰勬祴灏捐烦璺冩娴嬶紙闇€瑕佸悎鎴愰紶鏍囷級")) { _quit = true; return; }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        SetColorFromUi(new Color4(1f, 0f, 1f, 1f));      // 鍝佺孩锛氬拰鍏夋爣/鐧藉簳鍒嗗緱寮€
        Doc.InvalidateAll();
        SettleFrames(200);

        float y0 = _virtualY + _virtualH * 0.5f;
        float x0 = _virtualX + 400f;
        const int N = 60;

        // 鍏堟妸杩欎竴姝ョ殑"姝ラ暱 + 闂撮殧"瀹氫笅鏉ワ紙纭畾鎬э級锛氬拷澶у拷灏忕殑姝ラ暱鏄?*榧犳爣鍔犻€?*鐨勬牱瀛愶紝
        // 涓嶉綈鐨勯棿闅旀槸**鎶ョ偣鑺傚**鐨勬牱瀛愶紝涓よ€呬竴璧锋墠閫煎緱鍑?灏惧反涓€鍑轰竴杩?銆?        var rnd = new Random(20260928);
        var steps = new float[N];
        var gaps = new int[N];
        float total = 0f;
        for (int i = 0; i < N; i++)
        {
            steps[i] = noisy ? 2f + (float)rnd.NextDouble() * 32f : 30f;
            gaps[i] = noisy ? 6 + rnd.Next(36) : (i % 4 == 3 ? 60 : 8);
            total += steps[i];
        }

        int bandW = (int)(total + 220f), bandH = 120;
        int bandX = (int)(x0 - 60f), bandY = (int)(y0 - bandH * 0.5f);
        var buf = new byte[bandW * bandH * 4];

        SendMouse((int)x0, (int)y0, 0);
        SettleFrames(120);
        SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        var tip = new List<(double t, float x)>();
        var sentAt = new List<(double t, float x)>();   // 姣忎釜閲囨牱鏃跺埢"鏈€鍚庢姤鐨勫潗鏍?锛堢畻鍓嶆部-鎶ョ偣宸級
        int sent = 0;
        float cx = x0;
        double nextMove = NowMs;
        int tailFrames = 0;                      // 璇婃柇锛氶娴嬪熬澶勪簬婵€娲荤姸鎬佺殑甯ф暟
        while (!_quit && sent < N)
        {
            if (NowMs >= nextMove)
            {
                cx += steps[sent];
                sent++;
                SendMouse((int)cx, (int)y0, 0);
                nextMove = NowMs + gaps[sent - 1];
            }
            SettleFrames(1);                     // 涓€鎷?鈮?涓€甯э細淇濊瘉"鎶ョ偣闂撮殧"鐪熺殑钀藉湪
                                                 // MaxGapMs(40ms) 浠ュ唴鈥斺€斾笉鐒堕娴嬪櫒姣忔媿閮借
                                                 // 鏂瑪閲嶇疆锛屽熬宸存案杩滈暱涓嶅嚭鏉ワ紝娴嬭瘯浠€涔堥兘娴嬩笉鍒?            if (PredictTailActive) tailFrames++;
            if (ScreenProbe.CaptureRegionInto(buf, bandX, bandY, bandW, bandH))
            {
                int col = MaxInkColumn(buf, bandW, bandH, minRun: 5);
                tip.Add((NowMs, col >= 0 ? bandX + col : float.NaN));
                sentAt.Add((NowMs, cx));
            }
        }
        SendMouse((int)cx, (int)y0, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        float maxFwd = 0f, maxBack = 0f;
        int backCount = 0, solidBack = 0, measured = 0;
        for (int i = 1; i < tip.Count; i++)
        {
            if (float.IsNaN(tip[i].x) || float.IsNaN(tip[i - 1].x)) continue;
            float d = tip[i].x - tip[i - 1].x;
            measured++;
            if (d > maxFwd) maxFwd = d;
            if (d < 0f) { backCount++; if (-d > maxBack) maxBack = -d; }
            if (d < -3f) solidBack++;          // 鏄庣‘鍥為€€锛?3px锛堢墿鐞嗗儚绱狅級= 灏炬暣娈垫秷澶卞啀鍥炴潵鐨勯噺绾?        }
        float lastSent = cx;
        float lastTip = 0f;
        for (int i = tip.Count - 1; i >= 0; i--) if (!float.IsNaN(tip[i].x)) { lastTip = tip[i].x; break; }

        Console.WriteLine($"  閲囧埌 {tip.Count} 甯э紙鏈夋晥 {measured} 瀵癸級锛岀敾鍒?x={lastSent:F0}锛?
                          + $"鏈€鍚庣瑪灏?x={lastTip:F0}锛堣惤鍚?{lastSent - lastTip:F0}px锛?);
        Console.WriteLine($"  寰€鍓嶈烦锛氭渶澶?{maxFwd:F0}px/甯?);
        Console.WriteLine($"  寰€鍚庨€€锛歿backCount} 娆★紝鏈€澶?{maxBack:F0}px锛堝叾涓?>3px 鐨勬槑纭洖閫€ {solidBack} 娆★級  鈫?鈥滅獊绐佲€濈殑姝ｈ韩");
        // [B4 鏂板垽鎹甝 澧ㄥ墠娌?鈭?鏈€鍚庢姤鐐癸細閲?灏炬湁娌℃湁鐪熺殑浼稿嚭鍘?锛堟亽瀹氱殑涓€鎴湪宸垎閲岀湅涓嶈锛夈€?        double leadSum = 0; int leadN = 0; float leadMax = float.MinValue;
        for (int i = 0; i < tip.Count && i < sentAt.Count; i++)
        {
            if (float.IsNaN(tip[i].x)) continue;
            float lead = tip[i].x - sentAt[i].x;
            leadSum += lead; leadN++;
            if (lead > leadMax) leadMax = lead;
        }
        if (leadN > 0)
            Console.WriteLine($"  澧ㄥ墠娌库垝鎶ョ偣锛氬钩鍧?{leadSum / leadN:F1}px / 鏈€澶?{leadMax:F1}px"
                              + "锛堝紑棰勬祴搴旀槑鏄惧亸姝ｏ紱瀹冨氨鏄€樺熬鈥欏湪灞忓箷涓婄殑鏍峰瓙锛?);
        Console.WriteLine($"  棰勬祴灏撅細{tailFrames}/{tip.Count} 甯у浜庢縺娲荤姸鎬?);
        // 鍒ゆ嵁锛?026-10-15 澶嶆牳锛屽惈 v6 鍩虹嚎瀵圭収锛夛細
        //  路 榛樿鑺傚锛堝浐瀹?30px 姝ラ暱銆佹瘡 4 鐐逛竴鍋滐級锛氬洖缂╂鏁版槸"绐佺獊璺?鐨勬韬紝蹇呴』 鈮?
        //    锛坴6 涓庣嚎鎬ч娴嬪疄娴嬮兘鏄?**0**锛夛紱鍗曞抚鍓嶈烦涓婇檺 = 姝ラ暱 30 + 寮ч暱椤?6 + 4px 浣欓噺 = 40
        //    锛堝疄娴?36.0px锛屾濂介《璁捐涓婇檺锛夈€?        //  路 `--noisy` 鑺傚锛堥棿闅?6~41ms銆佹闀?2~34px 闅忔満锛夛細灏忓洖閫€锛堚墹3px锛夋槸**灏鹃暱璺熺潃
        //    閫熷害鎶栧姩璋冩暣**鐨勫浐鏈夌幇璞★紝涓嶆槸"绐佺獊"鈥斺€攙6 鍩虹嚎 8 娆°€佺嚎鎬ч娴?6 娆★紝鏈€澶ч兘鍙湁
        //    3px銆傛墍浠ヨ繖涓€妗ｅ彧鏁?**>3px 鐨勬槑纭洖閫€**锛堝熬鏁存娑堝け鍐嶅洖鏉ョ殑閲忕骇锛屽簲 鈮?锛夛紱
        //    鍗曞抚鍓嶈烦鏀惧鍒?42锛堟闀夸笂闄?34 + 寮ч暱椤?6 = 40 鐞嗚鍊硷紝瀹炴祴 39锛夈€?        bool ok = noisy
            ? (solidBack <= 2 && maxFwd <= 42f)
            : (backCount <= 2 && maxFwd <= 40f);
        Console.WriteLine(ok
            ? "  PASS: 绗斿皷杞ㄨ抗鏄ǔ鐨勶紙娌℃湁鍙嶅鍥炵缉锛?
            : $"  FAIL: 绗斿皷杞ㄨ抗涓嶇ǔ锛堝洖缂?{backCount} 娆★紙鏄庣‘ {solidBack}锛? 鍗曞抚鏈€澶?{maxFwd:F0}px锛?);
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 涓€鍧?BGRA 鍥鹃噷**鏈€鍙宠竟**閭ｄ竴鍒?澶熺矖鐨勫搧绾㈠ⅷ"锛堚墺 <paramref name="minRun"/> 涓繛缁儚绱狅級銆?    /// 瑕佹眰"澶熺矖"鏄负浜嗘妸钀界偣鍏夋爣鐜帓闄ゆ帀锛堥偅鏄粏绾匡紝绔栫潃鍒囧彧鏈変袱涓変釜鍍忕礌锛夈€?    /// </summary>
    private static int MaxInkColumn(byte[] bgra, int w, int h, int minRun)
    {
        for (int i = w - 1; i >= 0; i--)
        {
            int run = 0;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                bool magenta = bgra[o] > 200 && bgra[o + 1] < 90 && bgra[o + 2] > 200;
                if (magenta) { if (++run >= minRun) return i; }
                else run = 0;
            }
        }
        return -1;
    }

    private void CurveShowcase(string path)
    {
        Console.WriteLine($"=== 鍑哄浘锛歿path} ===");
        BoardOn = true;                          // 鐧藉簳锛氫笉鐒舵闈㈣儗鏅細娣疯繘鐢婚潰
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 鏉夸功鐨勮繎榛戣壊
        float x0 = _virtualX + 200f, y0 = _virtualY + 180f;
        const float pitch = 620f;                            // 鏍煎瓙闂磋窛锛堟í绔栦竴鏍凤紝鐪嬬潃鏄綉鏍硷級

        // 閫犱竴鏉℃洸绾匡細鍙湁 BeginShapeAt 閾虹殑閭ｄ竴涓崰浣嶇偣锛屼箣鍚庝氦缁?Set*Box
        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }
        // 姣忔牸鐨勫乏涓婅
        float CellX(int col) => x0 + col * pitch;
        float CellY(int row) => y0 + row * pitch;

        // ---- 绗竴琛岋細鎶涚墿绾跨殑鍥涚寮€鍙ｏ紙**鍏堥€夋湞鍚戯紝鍐嶇敤"椤剁偣 + 缁忚繃鐐?涓ょ偣鐢?*锛?----
        // 杩欏氨鏄敤鎴?2026-09-20 瀹氱殑鍙ｅ緞锛氭柟鍚戞槸"閫?鍑烘潵鐨勶紙CurveAxis锛夛紝澶у皬鐢?缁忚繃閭ｄ釜鐐?鍙嶈В銆?        var dirs = new[] { CurveAxis.OpenUp, CurveAxis.OpenDown, CurveAxis.OpenRight, CurveAxis.OpenLeft };
        for (int col = 0; col < dirs.Length; col++)
        {
            float cx = CellX(col), cy = CellY(0);
            var v = new Vector2(cx + 280f, cy + 280f);
            var s = Make(Tool.Parabola, StrokeKind.Parabola, v.X, v.Y);
            s.CurveAxis = dirs[col];                              // 鈶?鍏堥€夊紑鍙ｆ柟鍚?            s.SetParabolaVertex(v.X, v.Y);                        // 鈶?瀹氶《鐐?            // 鈶?瀹?鏇茬嚎缁忚繃鐨勭偣"锛氬彇 `u = 1` 澶勭殑鏇茬嚎鐐癸紙`s = p/2銆乼 = p`锛夛紝
            //    鍙嶈В鍑烘潵姝ｅソ鏄?p = 140锛堣 Stroke.ParabolaPThroughPoint锛夛紝涓€涓牸瀛愯寰椾笅銆?            var (dir, perp) = Stroke.ParabolaBasis(dirs[col]);
            var q = v + perp * 140f + dir * 70f;
            s.SetParabolaThroughPoint(q.X, q.Y);
            Doc.AddStroke(s);
        }

        // ---- 绗簩琛岋細鍙屾洸绾夸袱涓柟鍚?锛?姝ｅ鸡 / 浣欏鸡鍚勪竴涓懆鏈?----
        // 鍙屾洸绾挎寜鐪熷疄鐨?*涓ゆ**鍑哄浘锛堢涓€姝ユ嫋鍑?*娓愯繎绾挎**銆佺浜屾瀹?鏇茬嚎杩囧摢涓偣"锛夛紝
        // 杩欐牱鍥句笂杩欎袱鏉¤窡鑰佸笀鐪熺敾鍑烘潵鐨勫畬鍏ㄤ竴鑷达紙鏈濆悜涔熸槸閭ｄ竴姝ュ畾鐨勶級銆?        // 娓愯繎绾挎鏄?*鎷栧埌鍝氨鏄摢**锛歚A = |dx|銆丅 = |dy|`锛?026-09-20 鏀规垚涓嶅啀鎵撳鎶橈級銆?        var hX = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(0) + 280f, CellY(1) + 280f);
        hX.SetHyperbolaFromAsymptote(CellX(0) + 280f, CellY(1) + 280f,
                                     CellX(0) + 280f + 300f, CellY(1) + 280f + 120f, 8f);   // 鏂滅巼 0.4
        hX.SetHyperbolaThroughPoint(CellX(0) + 280f + 300f, CellY(1) + 280f + 104f);
        Doc.AddStroke(hX);          // 鐐规洿妯?鈫?鐒︾偣鍦?x 杞达紙宸﹀彸鍙屾洸绾匡級

        var hY = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(1) + 280f, CellY(1) + 280f);
        hY.SetHyperbolaFromAsymptote(CellX(1) + 280f, CellY(1) + 280f,
                                     CellX(1) + 280f + 120f, CellY(1) + 280f + 300f, 8f);   // 鏂滅巼 2.5
        hY.SetHyperbolaThroughPoint(CellX(1) + 280f + 104f, CellY(1) + 280f + 300f);
        Doc.AddStroke(hY);          // 鐐规洿绔?鈫?鐒︾偣鍦?y 杞达紙涓婁笅鍙屾洸绾匡級

        // 姝ｅ鸡锛氳捣鐐瑰湪杞翠笂锛堢涓€涓浂鐐癸級锛?*寰€涓婃嫋 = 鍏堜笂鍚庝笅**锛堝氨鏄鏈殑 y = sin x锛夈€?        // 鈿?2026-09-20 绗崄鍏壒锛?*妗嗗灏辨槸涓€涓懆鏈?*锛堢敤鎴凤細"姝ｅ鸡鍜屼綑寮︾敤涓€涓懆鏈熺殑鍥?锛夆€斺€?        // 鎵€浠ヨ繖涓€寮犳嫋鍑烘潵鐨勬槸**涓€涓懆鏈?*锛?00 瀹姐€丄 = 75锛夈€傚鍛ㄦ湡閭ｄ欢浜嬪湪涓嬩竴鏍笺€屾尝娴嚎銆嶃€?        // 鈿?杩欎竴琛岀殑**绾靛悜浣嶇疆閮藉啓鎴?`CellY(1) + 鏈鍐呭亸绉籤**锛氭牸瀛愰棿璺濇槸 620锛?        // 绗笁琛岋紙绔嬩綋锛変粠 `CellY(1) + 600` 灏卞紑濮嬩簡 鈥斺€?鍋忕Щ瓒呰繃 600 灏卞帇鍦ㄧ珛浣撲笂浜?        //锛?026-09-20 绗崄鍏壒鎶婃尝娴嚎鎽嗗埌 +760銆佸綋鍦哄帇浣忓渾鍙板拰鐞冿紝鍑哄浘鎵嶅彂鐜帮級銆?        float sinX = x0 + 1250f;
        var sin = Make(Tool.Sine, StrokeKind.Sine, sinX, CellY(1) + 300f);
        sin.SetWaveBox(sinX, CellY(1) + 300f, sinX + 300f, CellY(1) + 225f, 8f);   // dy = 鈭?5 鈫?A = 75
        Doc.AddStroke(sin);

        // 浣欏鸡锛氳捣鐐瑰湪**宄伴《**锛?*寰€涓嬫嫋 = 浠庡嘲椤跺線涓?*锛堝氨鏄鏈殑 y = cos x锛夈€?        // 鍚屾牱鏄?*涓€涓懆鏈?*锛堢旱鎷?= 宄?鈫?璋?= 2A锛屾墍浠ュ悓鏍?75 鐨勬尟骞呰鎷?150 楂橈級銆?        float cosX = x0 + 1600f;
        var cos = Make(Tool.Cosine, StrokeKind.Cosine, cosX, CellY(1) + 225f);
        cos.SetWaveBox(cosX, CellY(1) + 225f, cosX + 300f, CellY(1) + 375f, 8f);
        Doc.AddStroke(cos);

        // 娉㈡氮绾匡紙绗崄鍏壒锛夛細**澶氬懆鏈熺殑姝ｅ鸡娉?*鈥斺€旀í鍚戞嫋鐨勬槸"瑕佺敾澶氶暱"銆佸懆鏈熺敱鎸箙瀹?        //锛坄T = 1 脳 A`锛岀敤鎴峰畾鐨?鎸箙 = 涓€涓懆鏈?锛夈€傝繖閲?A = 70銆佺敾 210 瀹?鈫?**3 涓暣鍛ㄦ湡**锛?        // 姝ｆ槸鐢ㄦ埛瑕佺殑"寰堝涓懆鏈熺殑娉㈡氮绾?銆?        // 鎽嗗湪姝ｅ鸡 / 浣欏鸡涓嬮潰涓€鐐癸紙鍚屼竴琛岄噷閿欏紑锛屼笁寮犲苟鎺掔湅"涓€涓懆鏈?vs 澶氬懆鏈?鏈€鐩磋锛夈€?        float wavX = x0 + 1950f;
        var wav = Make(Tool.Wave, StrokeKind.Wave, wavX, CellY(1) + 400f);
        wav.SetWaveBox(wavX, CellY(1) + 400f, wavX + 210f, CellY(1) + 330f, 8f);
        Doc.AddStroke(wav);

        // 姝ｅ垏锛?026-09-20 绗崄浜旀壒锛夛細**涓€鏀?*锛屾寜涓?= 鍘熺偣銆佹嫋鍑轰互瀹冧负涓績鐨勬
        //锛堟í鍚?= 鍗婃敮闀裤€佺旱鍚?= 鍙鍗婇珮锛屼袱鏉℃笎杩戠嚎灏辫惤鍦ㄦ鐨勫乏鍙充袱杈癸級銆?        // 鈿?瑙嗚鍗婇珮鏈?*涓嬮檺姣斾緥 3:1**锛堣 Stroke.TangentMinAspect锛夛細杩欓噷鏁呮剰鍙嫋 60 楂橈紝
        // 鐢诲嚭鏉ヤ細琚《鍒?240 鈥斺€?鍥句笂瑕佺湅鐨勬鏄?闅忔墜涓€鎷栧氨璐寸潃娓愯繎绾?杩欎欢浜嬨€?        float tanX = x0 + 2560f, tanY = CellY(1) + 330f;
        var tan = Make(Tool.Tangent, StrokeKind.Tangent, tanX, tanY);
        tan.SetTangentBox(tanX, tanY, tanX + 80f, tanY + 60f, 8f);
        Doc.AddStroke(tan);

        // ---- 绗笁琛岋細绔嬩綋鍥惧舰锛?026-09-20 绗簲鎵癸紝鐓?InkClass 鐨?case 6/7锛?---
        // **鏃嬭浆浣撻偅涓€鏃?*锛氫竴娆℃嫋鍑?*澶栨帴鐭╁舰**灏辨垚锛堟き鍦嗙敱鐭╁舰娲剧敓锛?        // 鎵佺巼 = `Stroke.SolidEllipseRatio`锛夛紝琚尅浣忕殑閭ｅ崐鍦堟槸**缁嗚櫄绾?*锛堣緟鍔╁嚑浣曟Ы锛夈€?        // 浣嶇疆璐寸潃绗笁琛岀殑鏍肩嚎涓婃部锛氬啀寰€涓婃姮灏变細鍜岀浜岃锛堝弻鏇茬嚎 / 姝ｅ鸡锛夊彔鍦ㄤ竴璧凤紝
        // 鍐嶅線涓嬪帇鍑哄浘锛?800 楂橈級灏变細琚鎺夆€斺€斾袱澶撮兘璇曡繃锛岃繖涓綅缃垰濂姐€?        //
        // 鈿?杩欎竴琛屽師鏉ユ斁鐨勬槸**鍦嗘煴 / 鍦嗛敟 / 闀挎柟浣?/ 鍥涢潰浣?*鈥斺€斿悗涓や釜 2026-09-20 绗崄浜屾壒
        // 鎾や簡闈㈡澘鍏ュ彛锛堣 ShapeRows锛夛紝鎵€浠ヨ繖閲屾崲鎴?*鐢ㄦ埛鐜板湪鐪熻兘鎷栧嚭鏉ョ殑鍥涗釜**锛?        // 鍦嗘煴 / 鍦嗛敟 / 鍦嗗彴 / 鐞冦€傦紙闀挎柟浣?/ 鍥涢潰浣撶殑鐢绘硶娌″垹锛宍--curvetest` 閲岄偅浜?        // 鍑犱綍鏂█鐓ф棫鍦ㄧ鐫€瀹冧滑锛屾棫鏉夸功涔熺収鏍锋墦寮€銆傦級
        float sy0 = CellY(2) - 20f;
        var cyl = Make(Tool.Cylinder, StrokeKind.Cylinder, CellX(0) + 130f, sy0);
        cyl.SetSolidBox(CellX(0) + 130f, sy0, CellX(0) + 430f, sy0 + 350f);
        Doc.AddStroke(cyl);

        var cone = Make(Tool.Cone, StrokeKind.Cone, CellX(1) + 130f, sy0);
        cone.SetSolidBox(CellX(1) + 130f, sy0, CellX(1) + 430f, sy0 + 350f);
        Doc.AddStroke(cone);

        // 鍦嗗彴锛?026-09-20 绗崄涓夋壒锛夛細鍚屼竴涓鎺ョ煩褰紝涓婂簳鏄竴鍦?*灏忎竴鍦?*鐨勬き鍦嗐€?        var cfz = Make(Tool.ConeFrustum, StrokeKind.ConeFrustum, CellX(2) + 130f, sy0);
        cfz.SetSolidBox(CellX(2) + 130f, sy0, CellX(2) + 430f, sy0 + 350f);
        Doc.AddStroke(cfz);

        // 鐞冿紙绗崄鍥涙壒锛夛細鍗婂緞 = min(鍗婂, 鍗婇珮) 鐨勫唴鍒囨鍦?锛?璧ら亾妞渾锛堣繎渚у疄绾匡級銆?        // 杩欓噷**鏁呮剰鎷栦竴涓鏂?*锛?50脳350锛夆€斺€旂悆鏈潵灏辫鏄釜鍦嗐€?        var sph = Make(Tool.Sphere, StrokeKind.Sphere, CellX(3) + 130f, sy0);
        sph.SetSolidBox(CellX(3) + 130f, sy0, CellX(3) + 480f, sy0 + 350f);
        Doc.AddStroke(sph);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  宸蹭繚瀛?{path}" : "  淇濆瓨澶辫触");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 鍑哄浘锛?026-09-22锛夛細**鍙屾洸绾夸袱妗?锛?妞渾锛堝甫鐒︾偣锛変袱妗?*鎽嗘垚涓€寮犲洓鏍肩殑鍥俱€?    /// 鐢ㄦ硶 `--conicshow [璺緞]`锛岄粯璁?`reports/鍦嗛敟鏇茬嚎-涓ゆ。.bmp`銆?    ///
    /// 涓轰粈涔堝崟寮€涓€寮犮€佷笉濉炶繘 `--curveshow`锛氶偅涓€寮犵殑涓夎宸茬粡婊′簡锛堢浜岃鏈熬绂荤珛浣?    /// 鍙湁 600 鍍忕礌锛夛紝濉炶繘鍘讳細鍙犲湪涓€璧凤紙绗崄鍏壒灏辫繖涔堝嚭杩囦竴娆￠敊锛夈€傝€岃繖涓€鎵硅鐪嬬殑
    /// 姝ｆ槸"**涓ゆ。涔嬮棿鐨勫樊鍒?*"鈥斺€斿洓鏍煎苟鎺掋€佸悓涓€濂楀昂瀵革紝涓€鐪煎氨鑳藉姣旓細
    ///   涓婃帓锛氬弻鏇茬嚎**鏈?/ 鏃?*娓愯繎绾匡紱涓嬫帓锛氭き鍦?*鏈夌劍鐐逛笁瑙掑舰 / 鍙湁鐒︾偣**銆?    /// 妞渾鐨?P 鐢?*榛樿浣嶇疆**锛堟き鍦嗗乏涓婃柟閭ｄ釜鐐癸紝瑙?`Stroke.DefaultFocusPointU`锛夆€斺€?    /// 鍥句笂瑕佺湅鐨勬鏄?椤剁偣**鍦ㄦき鍦嗕笂**銆佷笁瑙掑舰鍦ㄤ笂鍗婅竟"锛屽拰闈㈡澘鍥炬爣鍚屼竴涓綅缃€?    /// </summary>
    private void ConicShowcase(string path)
    {
        Console.WriteLine($"=== 鍑哄浘锛歿path} ===");
        BoardOn = true;                          // 鐧藉簳锛氫笉鐒舵闈㈣儗鏅細娣疯繘鐢婚潰
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 鏉夸功鐨勮繎榛戣壊
        const float colPitch = 1440f, rowPitch = 720f;
        float x0 = _virtualX + 720f, y0 = _virtualY + 300f;

        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }

        // ---- 涓婃帓锛氬弻鏇茬嚎涓ゆ。锛?*鍚屼竴涓嚑浣?*锛屽彧宸偅涓ゆ潯铏氱嚎鍦ㄤ笉鍦級----
        // 鐢绘硶鎸夌湡瀹炵殑涓ゆ鏉ワ細绗竴姝ユ嫋鍑?*娓愯繎绾挎**锛?300, +120锛屾枩鐜?0.4锛夛紝
        // 绗簩姝ュ畾"鏇茬嚎缁忚繃鍝釜鐐?锛堟洿妯?鈫?鐒︾偣鍦?x 杞达級銆?        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0;
            var h = Make(Tool.Hyperbola, StrokeKind.Hyperbola, cx, cy);
            h.SetHyperbolaFromAsymptote(cx, cy, cx + 300f, cy + 120f, 8f);
            h.SetHyperbolaThroughPoint(cx + 300f, cy + 104f);
            h.ShowAsymptotes = col == 0;                 // 宸︼細鏈夋笎杩戠嚎锛涘彸锛氭棤娓愯繎绾?            Doc.AddStroke(h);
        }

        // ---- 涓嬫帓锛氭き鍦嗭紙甯︾劍鐐癸級涓ゆ。锛?*鍚屼竴涓き鍦?*锛屽彧宸偅涓ゆ潯杈瑰湪涓嶅湪锛?---
        // 涓績 锛?澶栬鐐规嫋 300 脳 160 鈫?瀹芥き鍦嗭紝鐒︾偣鍦ㄦí杞翠笂銆佺涓績 卤254銆?        // 宸︼細鏈夌劍鐐逛笁瑙掑舰锛?*P 鐢ㄩ粯璁や綅缃?*锛屼篃灏辨槸"椤剁偣鍦ㄦき鍦嗕笂"鐨勯偅涓牱瀛愶級锛?        // 鍙筹細鍙湁涓や釜鐒︾偣銆?        // 鈿?P 鐨勯粯璁よ鏄?*宸︿笂鏂?*锛?20掳锛岃 `Stroke.DefaultFocusPointU`锛夆€斺€斿浘涓婄湅鐨勫氨鏄?        // "涓夎褰㈢殑椤剁偣璐寸潃妞渾閭ｆ潯绾裤€佸湪涓婂崐杈?锛屽拰闈㈡澘鍥炬爣涓婄敾鐨勬槸鍚屼竴涓綅缃€?        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0 + rowPitch;
            var e = Make(Tool.ConicEllipse, StrokeKind.ConicEllipse, cx, cy);
            // 鈿?**涓嶈鐢?`SetShapeBox`**锛氶偅鏄?涓夎褰?/ 骞宠鍥涜竟褰?鐨勫妗嗗啓娉曪紙浼氬啓涓変釜椤剁偣锛夛紝
            // 鑰屾き鍦嗙殑瀹氫箟鏄?**涓績 锛?澶栬鐐?*"涓や釜鐐?鈥斺€?鐢ㄩ敊鐨勮瘽鐢诲嚭鏉ユ槸涓張缁嗗張楂樼殑鎬笢瑗?            //锛堢涓€鐗堝嚭鍥炬椂灏辨槸杩欎箞缈荤殑锛歛 鍜?b 鍏ㄩ敊浜嗭級銆傛纭啓娉曟槸"璧锋墜鐐瑰綋涓績銆佸啀鎷栧嚭澶栬鐐?銆?            e.SetEnd(cx + 300f, cy + 160f);
            e.FocusTriangle = col == 0;
            Doc.AddStroke(e);
        }

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  宸蹭繚瀛?{path}" : "  淇濆瓨澶辫触");
        Console.WriteLine();
        _quit = true;
    }
    private void PrismShowcase(string path)
    {
        BoardOn = true;                       // 鐧藉簳锛氳櫄绾垮帇鍦ㄦ闈笂鐪嬩笉娓?        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);

        void Put(Tool tool, StrokeKind kind, int sides, float bx, float by, float dx, float dy)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink,
                Width = 6f * DpiScale, PrismSides = sides,
            };
            s.AddPoint(bx, by, 1f, 0);
            s.SetPrismBase(bx, by, bx + 340f, by + 140f);        // 搴曢潰澶栨帴妗?340脳140锛堝帇鎵佹瘮 鈮?0.41锛?            s.SetPrismApex(bx + 170f + dx, by + 70f + dy);       // 椤朵笂閭ｄ釜涓績锛坉x/dy 鍐冲畾鐩磋繕鏄枩锛?            Doc.AddStroke(s);
        }

        // 涓夎锛氭１鏌?/ 妫遍敟 / 妫卞彴锛屾瘡琛岄兘鏄?*涓?鍥?浜?鍏?*銆侀兘鏄洿鐨勶紙椤朵笂閭ｄ釜涓績姝ｅ搴曞績涓婃柟 240锛?        var rows = new (Tool tool, StrokeKind kind)[]
        {
            (Tool.Prism, StrokeKind.Prism),
            (Tool.Pyramid, StrokeKind.Pyramid),
            (Tool.Frustum, StrokeKind.Frustum),
        };
        float[] colX = { 200f, 820f, 1440f, 2060f };
        int[] colSides = { 3, 4, 5, 6 };
        for (int r = 0; r < rows.Length; r++)
        {
            float by = _virtualY + 420f + r * 400f;
            for (int c = 0; c < colX.Length; c++)
                Put(rows[r].tool, rows[r].kind, colSides[c], _virtualX + colX[c], by, 0f, -240f);
        }

        // 鏈€鍚庝竴琛岋細**鐩?vs 鏂?*锛屼笁鏃忓悇涓€瀵癸紙鍚屼竴涓洓妫憋紝鏂滅殑閭ｄ釜椤跺績寰€鍙虫尓 200锛?        float lastY = _virtualY + 420f + rows.Length * 400f;
        var pairs = new (Tool tool, StrokeKind kind, float x, bool slant)[]
        {
            (Tool.Prism, StrokeKind.Prism, 200f, false),
            (Tool.Prism, StrokeKind.Prism, 760f, true),
            (Tool.Pyramid, StrokeKind.Pyramid, 1320f, false),
            (Tool.Pyramid, StrokeKind.Pyramid, 1880f, true),
            (Tool.Frustum, StrokeKind.Frustum, 2440f, false),
        };
        foreach (var (tool, kind, x, slant) in pairs)
            Put(tool, kind, 4, _virtualX + x, lastY,
                slant ? 200f : 0f, slant ? -200f : -240f);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok
            ? $"  宸蹭繚瀛?{path}锛堜笂涓夎锛氫笁/鍥?浜?鍏?鐨?妫辨煴 / 妫遍敟 / 妫卞彴锛岄兘鏄洿鐨勶紱鏈锛氱洿 vs 鏂滐級"
            : "  淇濆瓨澶辫触");
        _quit = true;
    }

    private void RenderInto(ID2D1Bitmap1 target, int strokeCount)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (n++ >= strokeCount) break;
            _windows[0].DrawStrokeForTest(s);
        }
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  RenderInto EndDraw 澶辫触: " + hr.Description);
        try { ctx.Flush(out _, out _); } catch { /* 瑙佹姤鍛婅鏄庯細CreateBitmap 鐩爣涓?Flush 浼氭姤鐘舵€侀敊璇紝鍥涚鍒嗚鲸鐜囦竴鑷达紝涓嶅奖鍝嶆í鍚戝姣?*/ }
        ctx.Target = null;
    }

    private void PatchInto(ID2D1Bitmap1 target, RectF r)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
            Vortice.Direct2D1.AntialiasMode.Aliased);
        _windows[0].ClearRectForTest(r);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (!s.IntersectsRect(r)) continue;
            _windows[0].DrawStrokeForTest(s);
            if (++n > 400) break;
        }
        ctx.PopAxisAlignedClip();
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr2 = ctx.EndDraw();
        if (hr2.Failure) Console.WriteLine("  PatchInto EndDraw 澶辫触: " + hr2.Description);
        try { ctx.Flush(out _, out _); } catch { }
        ctx.Target = null;
    }
    private IntPtr _syntheticPen;

    // ---- 鍚堟垚瑙︽懜锛堣嚜妫€娉ㄥ叆鐢級------------------------------------------
    //
    // 涓轰粈涔堣Е鎽搁渶瑕佸崟鐙竴浠斤細**POINTER_TYPE_INFO 鐪熷疄瀹氫箟閲屼腑闂存槸涓€涓?union锛?    // 鏈€澶ф垚鍛樻槸 POINTER_TOUCH_INFO锛?44 瀛楄妭锛?*锛岃€屾垜浠偅涓粨鏋勪綋鍙０鏄庝簡 pen 鍒嗘敮
    // 锛?20 瀛楄妭锛夊姞 24 瀛楄妭濉厖 鈫?152 瀛楄妭锛屼笌鐪熷疄姝ラ暱锛? + 4 瀵归綈 + 144锛変竴鑷达紱
    // 鑰屼笖涓や釜鍒嗘敮鐨?*寮€澶撮兘鏄悓涓€浠?POINTER_INFO**锛?    // 鎵€浠ユ寜 pen 鐨勫瓧娈靛悕濉€佹妸 type 璁炬垚 PT_TOUCH 灏辫兘閫犲嚭涓€涓Е鎽歌Е鐐广€?    private IntPtr _syntheticTouch;

    private bool EnsureSyntheticTouch()
    {
        if (_syntheticTouch != IntPtr.Zero) return true;
        // maxCount = 10锛氳Е鎽?*鍙互鍚屾椂鏈夊涓Е鐐?*鈥斺€?绗簩鏍规墜鎸囧畧鍗?銆佲墺3 鎸囨摝銆?        // 鍙屾寚鎵嬪娍鐨勮嚜妫€閮借涓€娆℃敞鍏?2~5 涓Е鐐广€?        _syntheticTouch = Native.CreateSyntheticPointerDevice(
            Native.PT_TOUCH, 10, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticTouch != IntPtr.Zero;
    }

    /// <summary>
    /// 娉ㄥ叆鍚堟垚瑙︽懜锛?*涓嶅甫闈㈢Н**锛夈€俙points` 鏄?杩欎竴鍒?*鎵€鏈?*鎸夊湪灞忎笂鐨勮Е鐐?锛?    /// 涓嬫爣灏辨槸瑙︾偣搴忓彿锛堢郴缁熸寜 pointerId 璺熻釜锛屾墍浠ユ瘡娆¤皟鐢ㄩ兘瑕佹妸杩樻寜鐫€鐨勮Е鐐逛竴璧峰甫涓婏級銆?    /// `contact=false` 琛ㄧず鍏ㄩ儴鎶捣銆?    /// </summary>
    private void SendTouches(bool contact, params (float x, float y)[] points)
    {
        var sized = new (float x, float y, float size)[points.Length];
        for (int i = 0; i < points.Length; i++) sized[i] = (points[i].x, points[i].y, 0f);
        SendTouchesSized(contact, sized);
    }

    /// <summary>
    /// 娉ㄥ叆鍚堟垚瑙︽懜锛?*鍙甫鎺ヨЕ闈㈢Н**锛夛細`size` = 鎺ヨЕ鐭╁舰杈归暱锛堢墿鐞嗗儚绱狅級锛? = 涓嶆姤闈㈢Н銆?    /// 鎵嬫帉鎿︾殑鍒ゆ嵁闈犲畠锛堣 Touch.cs 鐨勮嚜閫傚簲鍩虹嚎锛夛紝鎵€浠?鎵嬫帉"鐢ㄤ緥蹇呴』鑳介€犲嚭澶ч潰绉€?    ///
    /// 鈿?姝ラ暱 = **鎵樼缁撴瀯浣撳ぇ灏?*锛? 鍘熺敓姝ラ暱 152锛岃 Native 鐨勬敞閲婏級锛?    /// `rcContact` 绛夊亸绉?*杩愯鏃剁敤 `Marshal.OffsetOf` 绠?*鈥斺€斾笉鍐欓瓟娉曟暟瀛椼€?    /// 锛堝巻鍙插潙锛氭紡鎺?`touchFlags/touchMask` 鏃舵寜 144 鎺掋€佺浜屼釜瑙︾偣鏁翠綋閿欎綅涓㈠け銆傦級
    /// </summary>
    private unsafe void SendTouchesSized(bool contact, params (float x, float y, float size)[] points)
    {
        int stride = Marshal.SizeOf<Native.POINTER_TYPE_INFO>();
        int unionOff = (int)Marshal.OffsetOf<Native.POINTER_TYPE_INFO>(nameof(Native.POINTER_TYPE_INFO.pen));
        int maskOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.touchMask));
        int rcOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContact));
        int rcRawOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContactRaw));

        int n = Math.Max(1, points.Length);
        IntPtr buf = Marshal.AllocHGlobal(stride * n + 16);
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var one = new Native.POINTER_TYPE_INFO();
                one.type = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerType = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerId = (uint)(i + 1);
                one.pen.pointerInfo.pointerFlags =
                    Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE
                    | (contact ? Native.POINTER_FLAG_INCONTACT : 0u)
                    | (i == 0 ? Native.POINTER_FLAG_PRIMARY : 0u);
                one.pen.pointerInfo.ptPixelLocationX = (int)points[i].x;
                one.pen.pointerInfo.ptPixelLocationY = (int)points[i].y;
                one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

                IntPtr dst = buf + i * stride;
                Marshal.StructureToPtr(one, dst, false);
                if (points[i].size > 0f)
                {
                    // 鎶ラ潰绉細touchMask 缃綅 + rcContact / rcContactRaw 閮藉～鐭╁舰銆?                    float half = points[i].size * 0.5f;
                    int l = (int)(points[i].x - half), t = (int)(points[i].y - half);
                    int r = (int)(points[i].x + half), b = (int)(points[i].y + half);
                    Marshal.WriteInt32(dst + maskOff, (int)Native.TOUCH_MASK_CONTACTAREA);
                    Marshal.WriteInt32(dst + rcOff + 0, l);
                    Marshal.WriteInt32(dst + rcOff + 4, t);
                    Marshal.WriteInt32(dst + rcOff + 8, r);
                    Marshal.WriteInt32(dst + rcOff + 12, b);
                    Marshal.WriteInt32(dst + rcRawOff + 0, l);
                    Marshal.WriteInt32(dst + rcRawOff + 4, t);
                    Marshal.WriteInt32(dst + rcRawOff + 8, r);
                    Marshal.WriteInt32(dst + rcRawOff + 12, b);
                }
            }
            Native.InjectSyntheticPointerInput(_syntheticTouch, buf, (uint)points.Length);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool EnsureSyntheticPen()
    {
        if (_syntheticPen != IntPtr.Zero) return true;
        _syntheticPen = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticPen != IntPtr.Zero;
    }

    /// <summary>娉ㄥ叆涓€涓悎鎴愮瑪閲囨牱鐐癸紝璧扮殑鏄拰鐪熺瑪鍚屼竴鏉?WM_POINTER 璺緞銆?/summary>
    private unsafe void SendPenPoint(float x, float y, uint pressure, bool contact, bool first)
    {
        uint flags = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE;
        if (contact) flags |= Native.POINTER_FLAG_INCONTACT;
        if (first) flags |= Native.POINTER_FLAG_NEW | Native.POINTER_FLAG_PRIMARY | Native.POINTER_FLAG_FIRSTBUTTON;

        var one = new Native.POINTER_TYPE_INFO();
        one.type = Native.PT_PEN;
        one.pen.pointerInfo.pointerType = Native.PT_PEN;
        one.pen.pointerInfo.pointerFlags = flags;
        one.pen.pointerInfo.ptPixelLocationX = (int)x;
        one.pen.pointerInfo.ptPixelLocationY = (int)y;
        one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        one.pen.penFlags = 0;
        one.pen.penMask = Native.PEN_MASK_PRESSURE;
        one.pen.pressure = pressure;

        // 鍗曠偣锛氭寜**鎵樼缁撴瀯浣撴闀?*鎺掞紙= 鍘熺敓姝ラ暱 152锛涘崟鐐瑰叾瀹炴棤鎵€璋擄紝浣嗗埆鍐嶅啓姝绘棫鏁板瓧锛夈€?        IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.POINTER_TYPE_INFO>() + 16);
        try
        {
            Marshal.StructureToPtr(one, buf, false);
            Native.InjectSyntheticPointerInput(_syntheticPen, buf, 1);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    private void BallProbe(string prefix)
    {
        SetUiPref("hide", "1");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null) { Console.WriteLine("  鐣岄潰娌℃寕涓?); ExitCode = 1; _quit = true; return; }

        ui.SetExpandForTest(0f);            // 鏀惰捣鎬侊紙鐞冿級
        var scr = ui.ScreenForTest;
        const float gap = 2f, ball = 48f;
        var spots = new (string Name, float X, float Y)[]
        {
            ("left-mid",    scr.MinX + gap,               (scr.MinY + scr.MaxY) * 0.5f),
            ("right-mid",   scr.MaxX - gap - ball,        (scr.MinY + scr.MaxY) * 0.5f),
            ("bottom-left", scr.MinX + gap,               scr.MaxY - gap - ball),
            ("bottom-mid",  (scr.MinX + scr.MaxX) * 0.5f - ball * 0.5f, scr.MaxY - gap - ball),
            ("bottom-right",scr.MaxX - gap - ball,        scr.MaxY - gap - ball),
            ("left-low",    scr.MinX + gap,               scr.MaxY - 80f),
        };

        foreach (var (name, x, y) in spots)
        {
            foreach (int peek in new[] { 1, 0 })
            {
                ui.SetAnchorForTest(new System.Numerics.Vector2(x, y));
                ui.ForcePeekForTest(peek);
                SettleFrames(250);
                var b = ui.QueryBounds();
                var shot = new RectF
                {
                    MinX = b.MinX - 10f, MinY = b.MinY - 10f,
                    MaxX = b.MaxX + 10f, MaxY = b.MaxY + 10f,
                };
                string path = $"{prefix}-{name}-{(peek == 1 ? "show" : "hide")}.bmp";
                OffscreenShot(path, shot);
                Console.WriteLine($"  {name,-13} peek={peek} 閿?({x:F0},{y:F0}) "
                                  + $"鍗犵敤=({b.MinX:F0},{b.MinY:F0})-({b.MaxX:F0},{b.MaxY:F0}) "
                                  + $"鐞冮珮={b.MaxY - b.MinY:F0} {path}");
            }
        }
        _quit = true;
    }

    /// <summary>
    /// `--replayshow &lt;鍥?gt;`锛氬洖鏀炬帶鍒舵潯鐨勬憜鏍凤紙鎺掔増濂戒笉濂界湅鑷鍒や笉浜嗭紝鍙兘鐪嬪浘锛夈€?    /// 閾哄嚑绗?鈫?寮€濮嬪洖鏀?鈫?鎷栧埌蹇竴鍗?鈫?鎴帶鍒舵潯閭ｄ竴鍧楋紙甯︿竴鐐瑰懆鍥达級銆?    /// 鐪嬪浘鐢細鎸夐挳闂磋窛銆佸€嶉€熼珮浜€佽繘搴︽粦閽€佽鏁般€佲湑銆?    /// </summary>
    private void ReplayShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        Doc.ResetToSinglePage();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        SettleFrames(150);

        void AddInk(float x, float y, float dx, float t0, float t1)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s.AddPoint(x + dx * k / 4f, y + k * 8f, 0.3f + 0.15f * k, t0 + (t1 - t0) * k / 4f);
            Doc.AddStroke(s);
        }
        for (int i = 0; i < 6; i++) AddInk(300f + i * 40f, 260f + i * 70f, 420f, i * 700f, i * 700f + 600f);
        SettleFrames(200);

        if (!StartReplayForTest())
        {
            Console.WriteLine("  鎽嗘牱澶辫触锛氬洖鏀炬病璧锋潵锛堟病鏈夊彲瑙佺瑪杩癸紵锛?);
            ExitCode = 1; _quit = true; return;
        }
        ReplaySeekForTest(ReplayTotalForTest * 0.45f);   // 鍋滃湪"姝ｅ湪闀?閭ｅ効
        ReplaySetSpeedForTest(2f);
        SettleFrames(300);

        var bar = ReplayBarRectForTest;
        int sx, sy, sw, sh;
        if (Environment.GetCommandLineArgs().Contains("--full"))
        {
            // 鏁村睆锛氱湅"宸插嚭瀹岀殑锛堢儤杩涘唴瀹瑰眰锛夛紜 姝ｅ湪闀跨殑閭ｄ竴鏉★紙鍓嶇紑锛?瀵逛笉瀵?            sx = _virtualX; sy = _virtualY; sw = _virtualW; sh = _virtualH;
        }
        else
        {
            int pad = (int)(18 * DpiScale);
            sx = (int)bar.MinX - pad; sy = (int)bar.MinY - pad;
            sw = (int)(bar.MaxX - bar.MinX) + pad * 2;
            sh = (int)(bar.MaxY - bar.MinY) + pad * 2;
        }
        bool ok = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
        Console.WriteLine(ok ? $"  宸蹭繚瀛?{path}锛坽sw}脳{sh}锛? : "  鎴睆澶辫触");
        StopReplay("鎽嗘牱鏀跺熬");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }
    private static IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
    private int DrawAndCountHorizontal(float x, float y, float len, float width)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = width,
        };
        s.AddPoint(x, y, 1f, 0);
        s.AddPoint(x + len, y, 1f, 1);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(350);
        return ScreenProbe.CountMagenta((int)x, (int)(y - width), (int)len, (int)(width * 2f));
    }

    /// <summary>鐢讳竴鏉＄矖绔栫嚎骞舵暟瀹冪殑澧ㄥ儚绱犮€傜珫绾跨殑**涓績绾?*鐢诲湪 x 涓娿€?/summary>
    private int DrawAndCountVertical(float x, float y, float len, float width)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = width,
        };
        s.AddPoint(x, y, 1f, 0);
        s.AddPoint(x, y + len, 1f, 1);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(350);
        return ScreenProbe.CountMagenta((int)(x - width), (int)y, (int)(width * 2f), (int)len);
    }
    private bool SkipIfNoSyntheticInput(string what)
    {
        if (ProbeSyntheticInput(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.4f)) return false;
        Console.WriteLine($"  鐜锛氬悎鎴愯緭鍏ヤ笉鍙敤 鈫?SKIP: {what}璺宠繃锛堝鍗婃槸鍒殑绋嬪簭姝ｅ崰鐫€榧犳爣锛涙澗寮€鍚庨噸璺戝嵆鍙級");
        return true;
    }

    private bool ProbeSyntheticInput(float cx, float cy)
    {
        Tool = Tool.Pen;
        int before = Doc.Strokes.Count;
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);
        SendMouse((int)(cx + 60), (int)(cy + 30), 0);
        SettleFrames(80);
        SendMouse((int)(cx + 60), (int)(cy + 30), Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        // **鍏堟暟鍐嶆竻**锛氫笅闈㈤偅涓?Clear() 浼氭妸鍒氱敾鍑烘潵鐨勮繖涓€绗斾篃娓呮帀锛?        // 浠ュ墠鍦ㄨ繖閲岀洿鎺?`return Doc.Strokes.Count > before`锛屼簬鏄案杩滆繑鍥?false
        // 鈥斺€旀墍鏈夐潬瀹冩妸鍏崇殑鐢ㄤ緥锛堟畫褰便€佹鐨€佹埅鍥锯€︹€︼級鍏跺疄涓€鐩村湪闈欓粯 SKIP锛?        // 鑰屽睆骞曚笂鐨勬棩蹇楀嵈鍐欑潃"鍚堟垚杈撳叆涓嶅彲鐢?锛屾妸閿呮帹缁欎簡鐜銆?        // 鍒ゆ嵁蹇呴』鍙?Clear 涔嬪墠鐨勫€笺€?        int after = Doc.Strokes.Count;
        Doc.Clear();
        Doc.ClearHistory();
        return after > before;
    }
    private Thread StartInjector(float x0, float y0, float x1, float y1, double hz, double ms)
    {
        var th = new Thread(() =>
        {
            SendMouse((int)x0, (int)y0, 0);
            Thread.Sleep(80);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
            var sw = Stopwatch.StartNew();
            double period = 1000.0 / hz;
            int i = 1;
            while (true)
            {
                double target = i * period;
                if (target > ms) break;
                // 鐫″埌鎺ヨ繎銆佸啀鑷棆鍒扮偣锛歋leep 鐨勬姈鍔ㄦ槸 1~15ms锛岀洿鎺ョ敤鏉ラ厤閫?                // 浼氳"娉ㄥ叆鏃跺埢"鏈韩鍙樺緱姣旇娴嬮噺鐨勫欢鏃惰繕鎶栥€?                while (true)
                {
                    double remain = target - sw.Elapsed.TotalMilliseconds;
                    if (remain <= 0) break;
                    if (remain > 2) Thread.Sleep((int)(remain - 1));
                    else Thread.SpinWait(200);
                }
                float u = (float)(target / ms);
                float px = x0 + (x1 - x0) * u;
                float py = y0 + (y1 - y0) * u + MathF.Sin(u * 9f) * 70f;
                SendMouse((int)px, (int)py, 0);
                i++;
            }
            SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);
        })
        { IsBackground = true };
        th.Start();
        return th;
    }

    /// <summary>
    /// 杈规娊娑堟伅杈规覆鏌擄紝鐩村埌娉ㄥ叆绾跨▼鏀剁瑪銆?    ///
    /// **椤哄簭蹇呴』鍜屼富寰幆 Loop() 瀹屽叏涓€鑷?*锛氬厛鎶姐€佺瓑鍒板悎鎴愯竟鐣岋紙閰嶉€熸ā寮忥級銆?    /// 鍐嶆娊涓€娆°€佺劧鍚庣敾銆傜涓€鐗堟妸 DwmFlush 鏀惧湪鎶芥秷鎭箣鍚庯紝閲忓嚭鏉ョ殑
    /// "鏀跺埌 鈫?璋?Present" 鏄?16.2ms鈥斺€旈偅涓嶆槸娓叉煋鎱紝閭ｆ槸娴嬮噺寰幆鑷繁鎺掔殑闃熴€?    /// 娴嬮噺浠ｇ爜鍜岃娴嬩唬鐮佺殑鏃跺簭涓嶄竴鑷达紝閲忓嚭鏉ョ殑灏辨槸娴嬮噺鏂规硶鐨勯棶棰樸€?    /// </summary>
    private void PumpUntilDone(Thread injector)
    {
        while (injector.IsAlive)
        {
            DrainMessages();
            if (_dirty || _drawing)
            {
                if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
                RenderAll();
                _dirty = false;
            }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, Native.QS_ALLINPUT, 0);
        }
        DrainMessages();
        RenderAll();
    }

    private string PresentModeName()
    {
        if (OverlayWindow.VBlankPaced) return "鍚堟垚杈圭晫閰嶉€?+ Present(0)";
        if (OverlayWindow.LatencyWaitEnabled) return "甯у欢杩熺瓑寰呭璞?+ Present(0)锛堜笉闄愰€燂級";
        return "Present(1) 闃诲鍦ㄥ瀭鐩村悓姝?;
    }

    /// <summary>鎶婁竴涓満鏅窇瀹岋細娓呯┖閲囨牱 鈫?娉ㄥ叆 鈫?鏀舵暟鎹?鈫?鎵撳嵃 + 钀?CSV銆?/summary>
    private void RunLatencyScenario(string tag, string csvPath, string note,
                                    float x0, float y0, float x1, float y1, double hz, double ms)
    {
        Console.WriteLine();
        Console.WriteLine($"--- 鍦烘櫙 {tag}锛歿note}");
        Console.WriteLine($"    娉ㄥ叆 {hz:F0} Hz锛寋ms / 1000.0:F1} 绉掞紝璺緞 ({x0:F0},{y0:F0}) 鈫?({x1:F0},{y1:F0})");

        Latency.Scenario = tag;
        Latency.Clear();
        ResetLatencyProbe();
        LatencyRecording = true;

        long frames0 = FrameCounter;
        double wall0 = _clock.Elapsed.TotalMilliseconds;
        var th = StartInjector(x0, y0, x1, y1, hz, ms);
        PumpUntilDone(th);
        double wall = _clock.Elapsed.TotalMilliseconds - wall0;
        long frames = FrameCounter - frames0;

        LatencyRecording = false;
        Console.WriteLine($"    閲囧埌 {Latency.Count} 鏉℃牱鏈紝娓叉煋 {frames} 甯?/ {wall / 1000.0:F1} 绉?
                          + $" = {frames * 1000.0 / wall:F1} fps锛堢瑪鐢绘暟 {Doc.Strokes.Count}锛?);
        Console.Write(Latency.Report(spikeThresholdMs: 2 * (_windows.Count > 0 ? _windows[0].RefreshPeriodMs : 1000.0 / 60.0)));
        Latency.WriteCsv(csvPath, tag);
    }

    /// <summary>鎶婇〉闈㈠啓婊★紝鐢ㄦ潵閲?鍚屼竴椤靛ⅷ杩瑰緢澶氭椂"鐨勫欢鏃躲€?/summary>
    private void FillDensePage(int strokes, float cx, float cy)
    {
        Doc.Clear();
        var rnd = new Random(7);
        for (int i = 0; i < strokes; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = new Color4(0.2f, 0.3f, 0.9f, 1f),
                Width = 3f * DpiScale,
            };
            float bx = cx - 420 + (float)rnd.NextDouble() * 840;
            float by = cy - 260 + (float)rnd.NextDouble() * 520;
            for (int k = 0; k <= 14; k++)
                s.AddPoint(bx + k * 3.5f, by + MathF.Sin(k * 0.7f) * 5f, 0.6f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(700);
    }

    /// <summary>
    /// 娴?Present 杩斿洖 鈫?灞忓箷鐪熺殑鍙樿壊"杩欎竴娈碘€斺€擠WM 鍚堟垚 + 鎵弿杈撳嚭 + 闈㈡澘銆?    ///
    /// 涓轰粈涔堝繀椤荤洴鐫€灞忓箷鐪嬶細杩涚▼鍐呯殑 API 鎷夸笉鍒拌繖涓€娈电殑鍑嗙‘鏃跺埢銆傚墠闈㈠洓娈?    /// 鎴戜滑閮借兘鑷繁鎵撴椂鏍囷紝鍞嫭"鍍忕礌浠€涔堟椂鍊欎寒"鍙湁灞忓箷鑷繁鐭ラ亾銆?    ///
    /// 鍋氭硶锛氱櫧鏉垮簳鑹叉寜 8 甯т竴涓懆鏈熼粦鐧戒氦鏇匡紙4 甯х櫧銆? 甯ч粦锛夛紝鍚庡彴绾跨▼鐢?GDI
    /// 涓€鐩磋灞忓箷涓婂浐瀹氱殑涓€涓偣锛岃涓嬫瘡娆″彉鑹茬殑鏃跺埢锛涗富绾跨▼姣忓抚璁颁笅
    /// (Present 杩斿洖鏃跺埢, 杩欎竴甯х殑棰滆壊)銆傚懆鏈?8 甯?鈮?33ms锛岃繙澶т簬浠讳綍鍙兘鐨?    /// 绠＄嚎寤舵椂锛屾墍浠ユ瘡娆″彉鑹查兘鑳藉敮涓€瀵逛笂"鏄摢涓€甯у厛鍙樼殑"銆?    /// delta = 鍙樿壊鏃跺埢 鈭?閭ｄ竴甯?Present 杩斿洖鐨勬椂鍒汇€?    ///
    /// 杩欎釜灏哄瓙鑷韩鐨勫亸宸璇存竻妤氾細GDI 鎶撶殑鏄?DWM 鍚堟垚鍚庣殑妗岄潰锛岃鍙栨湰韬篃鍙兘
    /// 鎸夊瀭鐩村悓姝ュ榻愶紝鎵€浠ョ粷瀵瑰€煎彲鑳芥暣浣撳亸澶у嚑姣銆?*涓ょ鍛堢幇鏂瑰紡鐢ㄥ悓涓€鎶?    /// 灏哄瓙閲忓嚭鏉ョ殑宸€硷紙宸嚑涓埛鏂板懆鏈燂級鎵嶆槸杩欐潯鏁版嵁鐪熸鐨勪环鍊笺€?*
    /// </summary>
    private void RunPhotonProbe(string csvPath, int cycles)
    {
        Console.WriteLine();
        Console.WriteLine("--- 鍦烘櫙 photon锛歅resent 杩斿洖 鈫?灞忓箷鐪熺殑鍙樿壊锛圖WM 鍚堟垚 + 鎵弿杈撳嚭锛?);
        Console.WriteLine("    鐧芥澘鎸?8 甯ч粦鐧戒氦鏇匡紝鍙﹁捣涓€涓嚎绋嬬洴鐫€灞忓箷涓婁竴涓偣鐪嬩粈涔堟椂鍊欏彉");

        Doc.Clear();
        BoardOn = true;
        _dirty = true;
        RenderAll();
        _dirty = false;

        int probeX = _virtualX + 40, probeY = _virtualY + 40;
        var flips = new List<(ulong qpc, int color)>();
        bool stop = false;

        var th = new Thread(() =>
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            var bi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = 1, biHeight = -1, biPlanes = 1, biBitCount = 32,
                    biCompression = (int)Native.BI_RGB,
                }
            };
            IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS,
                                                 out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(memDc, dib);
            int last = -1;
            while (!stop)
            {
                Native.BitBlt(memDc, 0, 0, 1, 1, screenDc, probeX, probeY, Native.SRCCOPY);
                int c = Marshal.ReadByte(bits, 0) > 128 ? 1 : 0;   // B 閫氶亾
                if (last >= 0 && c != last) flips.Add((Qpc.Now, c));
                last = c;
                Thread.SpinWait(150);
            }
            Native.SelectObject(memDc, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        })
        { IsBackground = true };
        th.Start();
        Thread.Sleep(200);

        var frames = new List<(ulong qpc, int color)>();
        int total = cycles * 8;
        for (int i = 0; i < total; i++)
        {
            int color = (i / 4) % 2 == 0 ? 1 : 0;      // 1 = 鐧斤紝0 = 榛?            BoardColor = color == 1 ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 1f);
            _dirty = true;
            if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
            RenderAll();
            _dirty = false;
            frames.Add((_windows[0].LastPresentEndQpc, color));
        }

        stop = true;
        th.Join(2000);
        BoardOn = false;
        BoardColor = new Color4(0.99f, 0.99f, 0.98f, 1f);
        _dirty = true;
        RenderAll();
        _dirty = false;

        // 鎶婃瘡娆″彉鑹插涓?杩欎竴娈电殑澶翠竴甯?銆傚悓涓€棰滆壊鐨勫抚鏄繛缁?4 甯т竴娈碉紝
        // 灞忓箷涓婂彉鑹插搴旂殑鏄繖涓€娈电殑绗?1 甯т笂灞忋€?        var deltas = new List<double>();
        int searchFrom = 0;
        foreach (var (qpc, color) in flips)
        {
            int match = -1;
            for (int k = searchFrom; k < frames.Count; k++)
            {
                if (frames[k].color != color) continue;
                if (k > 0 && frames[k - 1].color == color) continue;   // 鍙娈甸
                if (frames[k].qpc <= qpc) { match = k; searchFrom = k + 1; }
                else break;
            }
            if (match < 0) continue;
            deltas.Add(Qpc.Ms(frames[match].qpc, qpc));
        }

        double refresh = _windows[0].RefreshPeriodMs;
        if (deltas.Count == 0)
        {
            Console.WriteLine("    娌″涓婏紙灞忓箷鎶撳彇鍙兘琚埆鐨勭獥鍙ｆ尅浣忥紝鎴栨姄鍙栨湰韬け璐ワ級");
            return;
        }
        var d = new Dist(deltas);
        Console.WriteLine($"    鎶撳埌 {deltas.Count} 娆″彉鑹诧紝鏈満浜ゆ崲閾炬姤鍛婄殑鍒锋柊鍛ㄦ湡 {refresh:F2} ms");
        Console.WriteLine($"    Present 杩斿洖 鈫?灞忓箷鍙樿壊锛歿d}");
        Console.WriteLine($"    鎶樺悎 {(d.Mean / refresh):F2} 涓埛鏂板懆鏈燂紙鍧囧€硷級銆?
                          + $"{(d.P50 / refresh):F2} 涓紙涓綅锛?);
        _photonDeltaMs = d.Mean;
        _photonDeltaFrames = d.Mean / refresh;
    }

    /// <summary>涓婃満娴嬪緱鐨勪笂灞忔锛堟绉?/ 鍒锋柊鍛ㄦ湡鏁帮級锛岀粰鎶ュ憡鎷?绔埌绔?鐢ㄣ€?/summary>
    private double _photonDeltaMs;
    private double _photonDeltaFrames;

    private void LatencyBench(string csvPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 寤舵椂瀹炴祴锛堢瑪灏栧姩 鈫?鍍忕礌浜級===");
        Console.WriteLine($"  鍛堢幇鏂瑰紡      锛歿PresentModeName()}");
        Console.WriteLine($"  浜ゆ崲閾惧悗缂撳啿  锛歿OverlayWindow.BufferCount}");
        Console.WriteLine($"  鎬ц兘闈㈡澘 HUD  锛歿(ShowHud ? "寮€" : "鍏?)}");
        Console.WriteLine($"  濮旀墭澧ㄨ抗杞ㄨ抗  锛歿OverlayWindow.InkTrailNote}"
                          + (OverlayWindow.InkTrailEnabled ? "锛屽凡鍚敤" : "锛屾湭鍚敤"));
        Console.WriteLine($"  璇存槑          锛氭敞鍏ョ殑鏄悎鎴愰紶鏍囷紝璧扮殑鏄拰鐪熺瑪鍚屼竴鏉?WM_POINTER 璺緞锛?
                          + "鈥滆緭鍏?鈫?鏀跺埌娑堟伅鈥濋偅涓€娈靛湪鍚堟垚杈撳叆涓婂彲鑳戒笉鍙緱锛堟姤鍛婇噷浼氭爣锛夈€?);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.45f;

        // 1) 绌虹櫧椤碉紝姝ｅ父涔﹀啓閫熷害銆?        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("empty-slow", csvPath, "绌虹櫧椤碉紝姝ｅ父涔﹀啓閫熷害",
            cx - 380, cy, cx + 380, cy, 140, 3000);

        // 2) 绌虹櫧椤碉紝蹇€熷垝绾裤€傜偣鐨勯棿璺濆ぇ銆佸崟浣嶆椂闂磋鐩栭潰绉ぇ锛?        //    閲忕殑鏄?鍚屾牱涓€甯ц鍏夋爡鍖栫殑澧ㄦ洿澶?鏃剁殑琛ㄧ幇銆?        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("empty-fast", csvPath, "绌虹櫧椤碉紝蹇€熷垝绾?,
            cx - 600, cy, cx + 600, cy, 200, 2500);

        // 3) 瀵嗛泦椤碉細鍚屼竴灏忓潡閲屽凡缁忔湁 3000 绗斻€?        FillDensePage(3000, cx, cy);
        RunLatencyScenario("dense", csvPath, "瀵嗛泦椤碉紙鍚屽尯鍩熷凡鏈?3000 绗旓級",
            cx - 380, cy, cx + 380, cy, 140, 3000);

        // 4) 闀胯窇锛氳繛缁啓 12 绉掞紝涓撻棬鐪?瓒婂啓瓒婃參"鍜屾姈鍔ㄥ垎甯冦€?        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("long-12s", csvPath, "杩炵画涔﹀啓 12 绉掞紙绋冲畾鎬э級",
            cx - 380, cy, cx + 380, cy, 140, 12000);

        // 5) 涓婂睆娈碉細鍞竴蹇呴』"鐪嬪睆骞?鎵嶇煡閬撶殑涓€娈点€?        RunPhotonProbe(csvPath, 20);
        if (_photonDeltaMs > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  绔埌绔及璁★紙鏈€鏂颁竴绗?鈫?鍍忕礌浜級锛?);
            Console.WriteLine($"    闃诲寮?: 16.5锛堝埌 Present 杩斿洖锛? {_photonDeltaMs:F1}锛堜笂灞忥級"
                              + $" 鈮?{16.5 + _photonDeltaMs:F0} ms");
        }

        Console.WriteLine();
        Console.WriteLine($"CSV 宸插啓鍏?{csvPath}");
        _quit = true;
    }

    /// <summary>
    /// 鐪熺瑪瀹炴祴锛氭妸鎺㈤拡鎸備笂锛岃鐢ㄦ埛鐢ㄦ墜鍐欑瑪鍦ㄥ睆骞曚笂鍐欙紝缁撴潫鍚庡嚭鎶ュ憡銆?    ///
    /// 杩欎釜妯″紡鍚屾椂鍥炵瓟涓€涓叧閿棶棰橈細**鎵嬪啓鏉垮埌搴曟槸浠?绗?鎶ヨ繘鏉ョ殑锛岃繕鏄互
    /// "榧犳爣"鎶ヨ繘鏉ョ殑**銆傚墠鑰呰兘鎷垮埌纭欢鏃舵爣鍜屽帇鎰燂紝鍚庤€呰繛"绗斿皷浠€涔堟椂鍊欏姩"
    /// 閮藉彧鑳介潬杞鐚溾€斺€斿緢澶氭墜鍐欐澘鐨?杩熸粸鎰?灏卞嚭鍦ㄨ繖閲岋紝璺熷簲鐢ㄤ唬鐮佹棤鍏炽€?    /// </summary>
    private void LatencyLive(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 鐪熺瑪寤舵椂瀹炴祴 ===");
        Console.WriteLine($"  鍛堢幇鏂瑰紡锛歿PresentModeName()}锛屼氦鎹㈤摼鍚庣紦鍐?{OverlayWindow.BufferCount}");
        Console.WriteLine($"  璇风敤鎵嬪啓绗旂敾绾匡紝鎸佺画 {seconds:F0} 绉掞紙鎴栨寜 Ctrl+Alt+Shift+X 鎻愬墠缁撴潫锛夆€︹€?);
        Console.WriteLine();

        Latency.Scenario = "鐪熺瑪鎵嬪啓";
        Latency.Clear();
        ResetLatencyProbe();
        LatencyRecording = true;

        var seen = new Dictionary<uint, int>();
        double t0 = NowMs;
        double nextTick = t0 + 2000;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            seen[LastPointerType] = seen.GetValueOrDefault(LastPointerType) + 1;
            if (_dirty || _drawing)
            {
                if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
                RenderAll();
                _dirty = false;
            }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= nextTick)
            {
                nextTick = NowMs + 2000;
                Console.WriteLine($"  [{NowMs / 1000:F0}s] 宸查噰 {Latency.Count} 甯э紝"
                                  + $"褰撳墠璁惧 {DeviceName(LastPointerType)}锛?
                                  + $"绗旂敾 {Doc.Strokes.Count}");
            }
        }

        LatencyRecording = false;
        Console.WriteLine();
        Console.WriteLine("璁惧浣跨敤缁熻锛堟瘡甯ч噰鍒扮殑鏈€鏂板瀷鍙凤級锛?
                          + string.Join("锛?, seen.OrderByDescending(kv => kv.Value)
                                                  .Select(kv => $"{DeviceName(kv.Key)} {kv.Value} 甯?)));
        Console.WriteLine("  绗?= PT_PEN 鎵嶆槸鐪熺瑪閫氶亾锛堟湁鍘嬫劅銆佹湁纭欢鏃舵爣锛夛紱"
                          + "榧犳爣 = 鎵嬪啓鏉垮伐浣滃湪鍏煎妯″紡锛屽崟杩欎竴椤瑰氨澶熼€犳垚杩熸粸鎰熴€?);

        // 鐪熺瑪鐨?鏁版嵁璐ㄩ噺"姹囨€伙細杩欐瑕佷笉瑕佺浉淇″帇鎰熴€佸悎骞剁偣鏀跺叏浜嗘病鏈夈€?        // 浠ュ強棰勬祴鍦ㄤ粈涔堟。浣嶏紙杩欎簺鍦?璋冪爺-鍘嬫劅涓庨娴?鍘熺悊.md 涓?娴嬭瘯-鍘嬫劅涓庨娴?md 閲岄兘鏈夊垽鎹級銆?        Console.WriteLine();
        Console.WriteLine("=== 鐪熺瑪鏁版嵁璐ㄩ噺姹囨€?===");
        Console.WriteLine($"  鍚堝苟鐐癸細{PenMessages} 鏉℃秷鎭?鈫?{PenSamples} 涓噰鏍风偣"
                          + (PenMessages > 0 ? $"锛堟瘡鏉?{PenSamples / (double)PenMessages:F2} 涓級" : ""));
        // 闈炵瑪鎸囬拡锛堥紶鏍?/ 瑙︽懜锛変篃鎶ヤ竴浠斤細鎵嬪啓鏉挎病寮€ Windows Ink銆佷互鍙婅Е鎽稿睆锛?        // 璧扮殑灏辨槸杩欐潯璺€斺€?鍚堝苟鐜?杩欎竴椤逛互鍓嶅彧鏈夌瑪鏈夋暟锛岀幇鍦ㄤ袱杈归兘鏈夈€?        if (PtrMessages > 0)
            Console.WriteLine($"  闈炵瑪鎸囬拡锛堥紶鏍?瑙︽懜锛夛細{PtrMessages} 鏉℃秷鎭?鈫?{PtrSamples} 涓噰鏍风偣"
                              + $"锛堟瘡鏉?{PtrSamples / (double)PtrMessages:F2} 涓級"
                              + $"锛屾敹涓?{PtrTotalPoints} 涓偣");
        Console.WriteLine($"  鍘嬫劅锛歿PenPressurePoints}/{PenTotalPoints} 涓偣甯︽湁鏁堝帇鎰燂紱"
                          + $"璁惧鎶?pressure 浣?{Yes(PenSawPressureMask)}锛?
                          + $"鍊捐浣?{Yes(PenSawTiltMask)}锛屾棆杞綅={Yes(PenSawRotationMask)}");
        // [鍒犻櫎 2026-10-05] 棰勬祴缁熻锛氶殢鑰侀娴嬬郴缁熺Щ闄ゃ€?        Console.WriteLine($"  婀垮ⅷ杞ㄨ抗锛歿(OverlayWindow.InkTrailEnabled ? "寮€" : "鍏?)}"
                          + $"锛坽OverlayWindow.InkTrailNote}锛?);

        // 涔﹀啓鏈熼棿鐨勫垎閰嶄笌 GC锛氫綆閰嶆満鎺掓煡"鍋跺彂鍗￠】"鐨勪緷鎹€?        // 姣忕瑪鍒嗛厤瓒婂ぇ瓒婂鏄撹Е鍙戝洖鏀讹紱**绗?2 浠ｅ洖鏀跺嚭鐜板湪涔﹀啓鏈熼棿 = 閭ｄ竴涓嬪氨鍗′簡鍑犲崄姣**銆?        if (StrokesMeasured > 0)
            Console.WriteLine($"  涔﹀啓鏈熼棿锛歿StrokesMeasured} 绗旓紝姣忕瑪鍒嗛厤 骞冲潎 {AllocKbSum / StrokesMeasured:F1} KB"
                              + $" / 鏈€澶?{AllocBytesMax / 1024.0:F1} KB锛?
                              + (StrokesWithGc2 == 0
                                  ? "绗?2 浠?GC 涓€娆￠兘娌″嚭鐜板湪涔﹀啓鏈熼棿锛堝ソ锛?
                                  : $"鈿?绗?2 浠?GC 鍑虹幇鍦?{StrokesWithGc2} 绗旈噷锛堜綆閰嶆満涓婂氨鏄彲瑙佸崱椤匡紝瑕佹煡锛?));
        Console.WriteLine($"  GC 浣庡欢杩熸。锛歿GcLatency.Describe()}");

        Console.WriteLine(PenTotalPoints == 0
            ? "  娉ㄦ剰锛氳繖涓€杞竴涓湡绗旈噰鏍风偣閮芥病鏈夆€斺€斿啓鐨勬椂鍊欑敤鐨勬槸榧犳爣/瑙︽懜锛屾垨鑰呯瑪宸ヤ綔鍦ㄥ吋瀹规ā寮?
            : PenSawPressureMask
                ? "  鍘嬫劅鍙敤锛氳繖鍙版満鍣?杩欐敮绗旂‘瀹炲湪鎶ュ帇鍔?
                : "  鍘嬫劅涓嶅彲鐢細璁惧娌℃姤 pressure 浣嶏紙鍏堟煡椹卞姩鐨?Windows Ink 寮€鍏筹級");

        Console.Write(Latency.Report(spikeThresholdMs: 2 * (_windows.Count > 0 ? _windows[0].RefreshPeriodMs : 1000.0 / 60.0)));
        string csv = "reports/latency-live.csv";
        Latency.WriteCsv(csv, "real-pen");
        Console.WriteLine($"CSV 宸插啓鍏?{csv}");
        _quit = true;
    }

    /// <summary>姹囨€昏閲岀敤鐨勫皬鍐欐硶銆?/summary>
    private static string Yes(bool v) => v ? "鏈? : "鏃?;

    private static string DeviceName(uint t) => t switch
    {
        Native.PT_PEN => "绗?,
        Native.PT_TOUCH => "瑙︽懜",
        Native.PT_MOUSE => "榧犳爣",
        _ => $"鏈煡({t})",
    };

    /// <summary>
    /// Draws one long stroke, checks it is on screen, erases its middle, then
    /// checks the middle went away and the ends survived. Correctness of the
    /// region patch is not something you can eyeball from inside the process.
    /// </summary>
    private void EraseCheck(out bool correct, out string detail)
    {
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // Magenta horizontal stroke, wiped at its left third.
        var target = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 16f };
        for (int i = 0; i <= 80; i++)
        {
            float t = i / 80f;
            target.AddPoint(cx - 400 + t * 800, cy, 0.9f, NowMs);
        }

        // Green vertical stroke crossing the magenta one, far from the erase
        // point. It must survive untouched *including* where it passes through
        // the rectangle that gets cleared and re-rasterised.
        float vx = cx + 200;
        var bystander = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 1f, 0f, 1f), Width = 16f };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            bystander.AddPoint(vx, cy - 300 + t * 600, 0.9f, NowMs);
        }

        Doc.Clear();
        Doc.AddStroke(target);
        Doc.AddStroke(bystander);
        Doc.InvalidateAll();
        SettleFrames(500);

        int targetBefore = ScreenProbe.CountMagenta((int)(cx - 360), (int)(cy - 30), 120, 60);
        int bystanderBefore = ScreenProbe.CountGreen((int)(vx - 30), (int)(cy - 30), 60, 60);

        int removed = Doc.EraseAt(cx - 300, cy, 30f);
        SettleFrames(500);

        int targetAfter = ScreenProbe.CountMagenta((int)(cx - 360), (int)(cy - 30), 120, 60);
        // Sample the bystander *inside* the patched band, not above it.
        int bystanderAfter = ScreenProbe.CountGreen((int)(vx - 30), (int)(cy - 30), 60, 60);

        correct = targetBefore > 200 && targetAfter < 40 && removed == 1
                  && bystanderBefore > 200 && bystanderAfter > bystanderBefore * 0.8;
        detail = $"琚摝绗旂敾 {targetBefore}鈫抺targetAfter} 鍍忕礌锛堝簲褰掗浂锛夛紝"
               + $"鍚屽尯鍩熷彟涓€绗?{bystanderBefore}鈫抺bystanderAfter} 鍍忕礌锛堝簲淇濈暀锛夛紝鍒犻櫎 {removed} 绗?;
    }

    // =====================================================================
    //  Teardown
    // =====================================================================

}

internal static class ScreenProbe
{
    /// <summary>Grabs the whole virtual desktop into a 32-bit BMP. Used for
    /// eyeballing what another app actually looks like while it is measured.</summary>
    public static bool SaveBmp(string path, int x, int y, int w, int h)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var pixels = new byte[w * h * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return WriteBmp(path, pixels, w, h);
    }

    /// <summary>鎶?BGRA 鍍忕礌鍐欐垚 32 浣?BMP 鏂囦欢銆?/summary>
    private static bool WriteBmp(string path, byte[] pixels, int w, int h)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            int imageBytes = w * h * 4;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            // BITMAPFILEHEADER
            bw.Write((ushort)0x4D42);          // 'BM'
            bw.Write(14 + 40 + imageBytes);    // 鏂囦欢澶у皬
            bw.Write(0);
            bw.Write(14 + 40);                 // 鍍忕礌鏁版嵁鍋忕Щ

            // BITMAPINFOHEADER
            bw.Write(40);
            bw.Write(w);
            bw.Write(h);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0);                       // BI_RGB
            bw.Write(imageBytes);
            bw.Write(2835);                    // 鍒嗚鲸鐜?            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);

            // BMP 鏄嚜涓嬭€屼笂瀛樺偍鐨勶紝鎶撳埌鐨勫儚绱犳槸鑷笂鑰屼笅鐨勶紝閫愯鍊掑簭鍐欍€?            for (int row = h - 1; row >= 0; row--)
                bw.Write(pixels, row * w * 4, w * 4);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int CountNear(int x, int y, int w, int h, int r, int g, int b, int tol)
    {
        var box = new Box(r, g, b, tol);
        return Capture(x, y, w, h, (bb, gg, rr) =>
            Math.Abs(rr - box.R) <= box.Tol
            && Math.Abs(gg - box.G) <= box.Tol
            && Math.Abs(bb - box.B) <= box.Tol);
    }

    private readonly struct Box
    {
        public readonly int R, G, B, Tol;
        public Box(int r, int g, int b, int tol) { R = r; G = g; B = b; Tol = tol; }
    }

    public static int CountMagenta(int x, int y, int w, int h) => Capture(x, y, w, h, IsMagenta);
    public static int CountGreen(int x, int y, int w, int h) => Capture(x, y, w, h, IsGreen);
    public static int CountRed(int x, int y, int w, int h) => Capture(x, y, w, h, IsRed);
    public static int CountCursor(int x, int y, int w, int h) => Capture(x, y, w, h, IsCursor);

    /// <summary>娣辫壊鍍忕礌鏁帮紙鎵?娣辫壊鑳跺泭"閭ｇ涓滆タ锛氭埅鍥炬椂鐨勫昂瀵歌鏁帮級銆?/summary>
    public static int CountDark(int x, int y, int w, int h) => Capture(x, y, w, h, IsDark);
    private static bool IsDark(byte b, byte g, byte r) => r < 90 && g < 90 && b < 90;

    /// <summary>
    /// 涓€鍧楀尯鍩熺殑**骞冲潎浜害**锛?..255锛孊T.601 鏉冮噸锛夈€傜粰"閬僵鍘嬫殫娌″帇鏆?杩欑被鍒ゆ嵁鐢ㄢ€斺€?    /// 鏁板儚绱狅紙Count*锛夊垎涓嶆竻"鏁村潡閮芥殫浜嗕竴鐐?鍜?灏戞暟鍍忕礌寰堟殫"銆?    /// </summary>
    public static double AvgLuma(int x, int y, int w, int h)
    {
        var buf = CaptureRegion(x, y, w, h);
        if (buf.Length < 4) return 0;
        double sum = 0; int n = 0;
        for (int i = 0; i + 3 < buf.Length; i += 4)
        { sum += 0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    // 鍒ゅ畾鐢ㄨ壊銆傚儚绱犳槸 BGRA 椤哄簭锛屾墍浠ュ弬鏁板悕鎸?(b, g, r)銆?    private static bool IsMagenta(byte b, byte g, byte r) => b > 200 && g < 90 && r > 200;
    private static bool IsGreen(byte b, byte g, byte r) => g > 180 && r < 90 && b < 90;
    private static bool IsRed(byte b, byte g, byte r) => r > 170 && g < 110 && b < 110;
    private static bool IsCursor(byte b, byte g, byte r)
        => r > 180 && g > 70 && g < 190 && b > 70 && b < 190;

    /// <summary>鎶撲竴鍧楀尯鍩熺殑鍘熷 BGRA 鍍忕礌锛岀敤浜庡墠鍚庡樊鍒嗐€?/summary>
    public static byte[] CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return Array.Empty<byte>();
        var buf = new byte[w * h * 4];
        return CaptureRegionInto(buf, x, y, w, h) ? buf : Array.Empty<byte>();
    }

    /// <summary>鎶婁竴浠?BGRA 缂撳啿鍘熸牱鍐欐垚 32 浣?BMP锛?*涓嶇粡灞忓箷**锛氭姄杩囩殑閭ｄ竴甯ц鑳界暀妗ｏ級銆?/summary>
    public static bool SaveBuffer(string path, byte[] bgra, int w, int h) => WriteBmp(path, bgra, w, h);

    /// <summary>鍚?<see cref="CaptureRegion"/>锛屼絾鎶婂儚绱犲啓杩涜皟鐢ㄦ柟缁欑殑缂撳啿銆?*閫愬抚鎶撳睆鐨勬祴閲忓繀椤荤敤瀹?*锛?    /// 姣忓抚 new 涓€涓?1400脳360脳4 鈮?2MB 鐨勬暟缁勪細鐩存帴杩?LOH锛屽疄娴嬭兘鎶?*涓€娆＄瑪鐢?*鍒峰嚭
    /// 64 娆＄ 2 浠?GC / 378 MB 鍒嗛厤鈥斺€旈偅閲忓埌鐨勬槸娴嬮噺宸ュ叿鑷繁锛屼笉鏄骇鍝併€?    /// 锛?026-09-28 鎷?`--wetdrytest` 韪╃殑锛岃鍦ㄨ繖閲屽厤寰楀啀鐘€傦級
    /// </summary>
    public static bool CaptureRegionInto(byte[] dst, int x, int y, int w, int h)
    {
        if (dst == null || w <= 0 || h <= 0 || dst.Length < w * h * 4) return false;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);
        Marshal.Copy(bits, dst, 0, w * h * 4);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return true;
    }

    /// <summary>涓ゅ紶鍚屽昂瀵告埅鍥剧殑閫愬儚绱犲樊寮傛暟锛堝蹇嶈交寰姉閿娇鎶栧姩锛夈€?/summary>
    public static int DiffCount(byte[] a, byte[] b, int tolerance = 12)
    {
        if (a == null || b == null || a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int dr = Math.Abs(a[i] - b[i]);
            int dg = Math.Abs(a[i + 1] - b[i + 1]);
            int db = Math.Abs(a[i + 2] - b[i + 2]);
            if (dr > tolerance || dg > tolerance || db > tolerance) n++;
        }
        return n;
    }

    /// <summary>Grabs a screen region with GDI and counts pixels of one colour.
    /// This is how the prototype proves that what the GPU drew really landed on
    /// the display, instead of trusting that the code "should" work.</summary>
    public static bool TryFindMagenta(int x, int y, int w, int h, out int hits)
    {
        hits = Capture(x, y, w, h, IsMagenta);
        return hits > 500;
    }

    private static int Capture(int x, int y, int w, int h, Func<byte, byte, byte, bool> match)
    {
        int hits = 0;
        if (w <= 0 || h <= 0) return 0;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return 0;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var buffer = new byte[w * h * 4];
        Marshal.Copy(bits, buffer, 0, buffer.Length);
        for (int i = 0; i < buffer.Length; i += 4)
        {
            // Buffer is BGRA order.
            if (match(buffer[i], buffer[i + 1], buffer[i + 2])) hits++;
        }

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);

        return hits;
    }
}

internal static class Program
{
    /// <summary>
    /// 鍗曞疄渚嬬敤鐨勫懡鍚嶄簰鏂ヤ綋銆?*蹇呴』鏄潤鎬佸瓧娈?*锛氬彞鏌勪竴閲婃斁锛?宸茬粡鍦ㄨ窇"杩欎釜鍒ゆ柇灏辨病浜嗐€?    /// 杩涚▼閫€鍑烘椂鐢辩郴缁熻嚜鍔ㄦ斁寮冿紙涓嶉渶瑕佹墜鍔?ReleaseMutex锛夈€?    /// </summary>
    private static System.Threading.Mutex s_single;

    [STAThread]
    private static int Main(string[] args)
    {
        // **WinExe 榛樿涓嶅甫鎺у埗鍙?*锛堝弻鍑绘椂涓嶈寮归粦妗嗭級锛屼絾鑷 / 鍑哄浘鍑犱箮鍏ㄦ槸浠庡懡浠よ璺戠殑锛?        // 閭ｄ簺 `Console.WriteLine` 涓嶈兘涓€璧蜂涪鎺夈€備袱杈瑰吋椤撅細**鏈夊弬鏁版椂**璇曠潃鎺ヤ笂鐖惰繘绋嬬殑鎺у埗鍙?        //锛堟帴涓嶄笂璇存槑鏄粠璧勬簮绠＄悊鍣ㄥ弻鍑诲嚭鏉ョ殑锛岄偅姝ｅソ鈥斺€斿畨闈欏惎鍔級銆?        if (args.Length > 0) TryAttachParentConsole();

        // **鍗曞疄渚?*锛氳繖涓▼搴忔槸涓€灞傚叏灞忕疆椤剁殑瑕嗙洊灞傘€傚紑涓や釜鐨勫悗鏋滀笉鏄?澶氫竴涓獥鍙?锛?        // 鑰屾槸锛氫袱灞傚彔鍦ㄤ竴璧凤紙鍐欏瓧钀藉埌鍝竴灞傜湅杩愭皵锛夈€佺浜屼釜瀹炰緥鐨勭儹閿敞鍐屼笉涓娿€?        // **PPT 鑱斿姩涓よ竟閮藉湪鍋?*锛堜簰鐩告姠鐫€缈婚〉銆佹姠鐫€寰€鍚屼竴涓枃浠跺啓鎵规敞锛夈€?        //
        // 鈿?鑷閲岀殑 `--clicktarget` 鏄?*鏁呮剰鍐嶈捣涓€涓繘绋?*褰?涓嬪眰绐楀彛"鐢ㄧ殑
        //   锛坄--passtest` / `--uitest` 閮介潬瀹冿級锛岄偅涓嶆槸浜у搧瀹炰緥銆佷篃涓嶈兘琚嫤鈥斺€?        //   鎷︿簡閭ｄ袱鏉＄敤渚嬪氨娌℃硶璺戜簡銆傛墍浠ュ畠鍗曠嫭鏀捐銆?        //
        // **鏁存閮芥斁杩?try 閲?*锛氬崟瀹炰緥鍒ゆ柇鏇剧粡鍐欏湪 try 澶栭潰锛屽畠涓€鏃︽姏寮傚父锛堣
        // AcquireSingleInstance 閲?abandoned 閭ｆ锛夊氨缁曡繃浜嗕笅闈㈢殑 ReportFatal锛?        // GUI 鐗堬紙娌℃湁鎺у埗鍙帮級鐩存帴闈欓粯娑堝け銆?        try
        {
            bool isHelper = Array.IndexOf(args, "--clicktarget") >= 0;
            if (!isHelper && !AcquireSingleInstance()) return 0;
            return new App().Run(args);
        }
        catch (Exception ex)
        {
            // 娌℃湁鎺у埗鍙版椂锛?FATAL: ..."杩欏彞璇濈瓑浜庢病鍐欌€斺€旇惤鍒扮洏涓?+ 寮逛竴鍙ワ紝
            // 鑷冲皯璁╃敤鎴风煡閬?瀹冨嚭閿欎簡銆佽鎯呭湪鍝?锛堜粠鍛戒护琛岃窇鏃舵帶鍒跺彴涔熺収鏃ф湁锛夈€?            Console.WriteLine("FATAL: " + ex);
            ReportFatal(ex);
            return 1;
        }
    }

    /// <summary>鎺ヤ笂鐖惰繘绋嬬殑鎺у埗鍙板苟閲嶅紑 stdout锛堣 Main 閲岄偅涓€娈碉級銆?/summary>
    private static void TryAttachParentConsole()
    {
        try
        {
            if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { /* 鎺ヤ笉涓婂氨绠椾簡锛氫笉褰卞搷鍔熻兘锛屽彧鏄湅涓嶅埌鏃ュ織 */ }
    }

    /// <summary>
    /// 鎶?鍞竴瀹炰緥"銆傚凡缁忓湪璺?鈫?鎶婇偅涓疄渚嬬殑瑕嗙洊灞傜獥鍙ｆ彁鍒板墠闈紝杩斿洖 false銆?    /// 鐢?`Local\` 鍓嶇紑锛?*姣忎釜鐧诲綍浼氳瘽涓€涓?*锛堝悓涓€鍙版満鍣ㄤ袱涓敤鎴峰悇寮€涓€涓簰涓嶆墦鎵帮級銆?    /// </summary>
    private static bool AcquireSingleInstance()
    {
        System.Threading.Mutex mutex;
        try
        {
            mutex = new System.Threading.Mutex(true, @"Local\InkTeach.SingleInstance", out bool first);
            if (first) { s_single = mutex; return true; }
        }
        catch (Exception ex)
        {
            // 浜掓枼浣撴湰韬缓涓嶅嚭鏉ワ紙鏋佸皯瑙侊級涓嶈鎶婅蒋浠舵嫤姝伙細鎸?娌℃湁鍒殑瀹炰緥"缁х画銆?            Console.WriteLine("鍗曞疄渚嬩簰鏂ヤ綋鍒涘缓澶辫触锛屾寜鈥滄病鏈夊埆鐨勫疄渚嬧€濈户缁細" + ex.Message);
            return true;
        }

        // 鎶笉鍒版椂**鍏堢瓑涓€灏忎細鍎垮啀鍒?*鈥斺€?鐣岄潰涓婄殑閲嶅惎"閭ｆ潯璺槸"鍏堣捣鏂拌繘绋嬨€?        // 鑰佽繘绋嬮殢鍚庢墠閫€"锛堣 Engine.RestartSelf锛氬畠 `_quit = true` 涔嬪悗鎵嶈蛋 Shutdown锛夈€?        // 涓嶇瓑鐨勮瘽鏂拌繘绋嬩竴涓婃潵灏辩湅瑙佷簰鏂ヤ綋杩樺湪鑰佽繘绋嬫墜閲岋紝鐩存帴閫€鍑衡€斺€?        // 琛ㄧ幇鏄?鐐逛簡閲嶅惎锛岃蒋浠舵病浜?锛岃€岃繖鏄繖绫诲崟瀹炰緥淇濇姢鏈€甯歌鐨勫潙銆?        // 2 绉掞紙姣?50ms 璇曚竴娆★級瀵?鑰佽繘绋嬫鍦ㄩ€€"瓒冲浜嗭紱鐪熺殑鏄?鐢ㄦ埛鍙堝弻鍑讳簡涓€娆?锛?        // 涔熷氨鏅?2 绉掔湅鍒伴偅鍙ユ彁绀猴紝涓嶅奖鍝嶄粈涔堛€?        for (int i = 0; i < 40; i++)
        {
            try
            {
                if (mutex.WaitOne(50)) { s_single = mutex; return true; }
            }
            catch (System.Threading.AbandonedMutexException)
            {
                // **杩欎竴鏉″氨鏄?鐐逛簡閲嶅惎涓嶅洖鏉?鐨勭湡鍑?*锛?026-09-28 淇級锛?                // 鑰佽繘绋嬮€€鍑烘椂杩樻敟鐫€杩欎釜浜掓枼浣擄紝鍐呮牳鎶婁簰鏂ヤ綋鏍囪鎴?"abandoned"锛?                // 浜庢槸杩欓噷鐨?WaitOne 涓嶆槸杩斿洖 false锛岃€屾槸**鎶?AbandonedMutexException**銆?                // 鎶涘紓甯?鈮?娌℃姠鍒帮細鎸?MSDN锛屽紓甯告姏鍑烘椂绛夊緟宸茬粡婊¤冻銆佹墍鏈夋潈宸茬粡杞繃鏉ヤ簡
                // 锛?the wait is satisfied"锛夆€斺€旀墍浠ュ綋鎴?鎶㈠埌浜?缁х画鍚姩鎵嶆槸瀵圭殑銆?                //
                // 涔嬪墠娌℃帴杩欎釜寮傚父锛氬畠涓€璺啋鍒?Main 澶栭潰锛堥偅鏃跺崟瀹炰緥鍒ゆ柇鍐欏湪 try 涔嬪锛夛紝
                // GUI 鐗堟病鏈夋帶鍒跺彴銆佷篃娌℃湁 crash 鏃ュ織锛岃〃鐜板氨鏄?鑰佺獥鍙ｅ叧浜嗐€佹柊绐楀彛鍐嶄篃涓嶅嚭鏉?銆?                s_single = mutex;
                return true;
            }
            catch (Exception ex)
            {
                // 鍏跺畠鎰忓涔熷埆鎶婅蒋浠舵嫤姝伙紙姣斿鏉冮檺/鍙ユ焺闂锛夈€?                Console.WriteLine("鍗曞疄渚嬪垽鏂嚭閿欙紝鎸夆€滄病鏈夊埆鐨勫疄渚嬧€濈户缁細" + ex.Message);
                return true;
            }
        }

        mutex.Dispose();
        Console.WriteLine("宸茬粡鏈?InkTeach 鍦ㄨ繍琛屼簡锛堝崟瀹炰緥淇濇姢锛夛細鎶婂畠鐨勭獥鍙ｆ彁鍒板墠闈紝杩欎竴涓€€鍑恒€?);
        BringExistingToFront();
        return false;
    }

    /// <summary>
    /// 鎵惧埌宸茬粡鍦ㄨ窇鐨勯偅涓疄渚嬬殑绐楀彛锛屾妸瀹冩縺娲汇€傛壘涓嶅埌灏辨彁绀轰竴鍙?    /// 锛堣鐩栧眰鏄€忔槑鐨勩€佽繕鍙兘琚创杈归殣钘忔敹鎴愬簳閮ㄤ竴鏉★紝鐢ㄦ埛澶氬崐浠ヤ负"鍙屽嚮娌″弽搴?锛夈€?    ///
    /// **璁ょ獥鍙ｉ潬"绐楀彛绫诲悕鍓嶇紑"锛屼笉闈犳爣棰?*锛氬疄娴嬩粠鍒殑杩涚▼璇昏繖鍑犱釜绐楀彛鐨勬爣棰橈紝
    /// 鎷垮埌鐨勬槸 `"I"`锛堝缓绐楀彛鏃堕偅涓悕瀛楄鎴垚浜嗕竴涓瓧绗︼紝鍘熷洜娌℃繁绌垛€斺€?    /// 閭ｅ嚑涓獥鍙ｉ兘鏄?WS_EX_TOOLWINDOW锛屼笉杩?Alt+Tab锛屾爣棰樻湰鏉ヤ篃娌′汉鐪嬶級锛?    /// 鑰?*绫诲悕璇诲嚭鏉ユ槸瀹屾暣鐨?* `InkTeachOverlay_<guid>`锛堣 OverlayWindow 娉ㄥ唽绐楀彛绫伙級銆?    /// 鍚屼竴鎵归噷杩樻湁涓や釜"鎺ヨ緭鍏ュ皬绐?锛坄Engine` 閲屽缓鐨勭晫闈㈤潰鏉?/ PPT 鏉★級锛?    /// 瀹冧滑涓嶇畻"瀹炰緥鐨勭獥鍙?锛屾墍浠ユ寜**闈㈢Н鎸戞渶澶х殑閭ｅ紶**鈥斺€旇鐩栧眰鏄暣鍧楀睆骞曢偅鍧椼€?    /// </summary>
    private static void BringExistingToFront()
    {
        IntPtr found = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(128);
            if (Native.GetClassNameW(h, sb, sb.Capacity) <= 0) return true;
            if (!sb.ToString().StartsWith("InkTeachOverlay_", StringComparison.Ordinal)) return true;

            if (!Native.GetWindowRect(h, out var r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; found = h; }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            Native.SetForegroundWindow(found);
            return;
        }
        Native.MessageBoxW(IntPtr.Zero,
            "InkTeach 宸茬粡鍦ㄨ繍琛屼簡銆俓n\n瀹冩槸涓€灞傞€忔槑鐨勮鐩栧眰锛岀湅灞忓箷搴曢儴涓棿閭ｆ潯宸ュ叿鏉°€?,
            "InkTeach", Native.MB_OK);
    }

    /// <summary>宕╂簝鏃舵妸璇︽儏鍐欏埌鐩樹笂骞跺脊涓€鍙ワ紙WinExe 鍙屽嚮鍚姩鏃舵病鏈夋帶鍒跺彴鍙湅锛夈€?/summary>
    private static void ReportFatal(Exception ex)
    {
        string path = "";
        try
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InkTeach", "last-crash.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
        }
        catch { }

        Native.MessageBoxW(IntPtr.Zero,
            "InkTeach 鍚姩澶辫触锛屽凡缁忛€€鍑恒€俓n\n璇︽儏锛? + (path.Length > 0 ? path : "锛堝啓鏃ュ織涔熷け璐ヤ簡锛?),
            "InkTeach", Native.MB_OK | Native.MB_ICONERROR);
    }
}
