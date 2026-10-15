using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>Process memory sampling, used to attribute the footprint to each
/// stage of start-up rather than quoting a single number.</summary>
internal static class Mem
{
    public static readonly List<string> Log = new();

    public static double Ws()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1048576.0;
    }

    public static double Priv()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 / 1048576.0;
    }

    public static void Stage(string label)
        => Log.Add($"{label,-38} 宸ヤ綔闆?{Ws(),7:F1} MB   绉佹湁 {Priv(),7:F1} MB");

    /// <summary>Asks Windows to trim this process's working set. Freeing memory
    /// does not by itself shrink the working set - resident pages are only
    /// dropped under pressure - so this separates "still held" from
    /// "free but still resident".</summary>
    public static void TrimWorkingSet()
    {
        try { Native.EmptyWorkingSet(Native.GetCurrentProcess()); } catch { }
    }
}

/// <summary>Device-level objects shared by every overlay window.</summary>
internal static class Gfx
{
    public static ID3D11Device Device;
    public static IDXGIFactory2 Factory;
    public static ID2D1Factory1 D2DFactory;
    public static ID2D1Device D2DDevice;
    public static IDWriteFactory WriteFactory;
    public static IDXGIAdapter3 Adapter3;
    public static string AdapterInfo = "unknown";

    /// <summary>
    /// 鍦嗗ご鍦嗚鐨勬弿杈规牱寮忋€?*娓叉煋鍜屽懡涓祴璇曞繀椤诲叡鐢ㄥ悓涓€涓璞?*鈥斺€?    /// 鍛戒腑娴嬭瘯瑕佸垽鏂?杩欎釜鐐圭畻涓嶇畻钀藉湪杩欐潯绾夸笂"锛岀敤鐨勬牱寮忓繀椤诲拰鐢诲嚭鏉ョ殑
    /// 涓€妯′竴鏍凤紝鍚﹀垯鍒ゅ畾缁撴灉鍜岃倝鐪肩湅鍒扮殑浼氬涓嶄笂锛堢澶淬€佺粏闀垮浘褰㈠挨鍏舵槑鏄撅級銆?    /// </summary>
    public static ID2D1StrokeStyle1 RoundStroke;

    public static ID2D1StrokeStyle1 Round => RoundStroke ??= D2DFactory.CreateStrokeStyle(
        new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 10f,
        });

    /// <summary>
    /// 铏氱嚎 / 鐐圭嚎鐨勬弿杈规牱寮忥紙2026-09-19 鍔狅紝瑙?<see cref="StrokeDash"/>锛夈€?    ///
    /// **鑺傞暱鑺傝窛鐢?鑷畾涔夊浘妗?锛屽崟浣嶆槸绗斿**锛圖irect2D 鐨勭害瀹氾細dashes 閲岀殑鏁板瓧涔樼瑪瀹斤級鈥斺€?    /// 鎵€浠ョ矖绗旂殑铏氱嚎鑷姩鍙橀暱銆佺粏绗旇嚜鍔ㄥ彉瀵嗭紝鍜岃€佸笀"绮楃瑪閰嶉暱铏氱嚎"鐨勭洿瑙変竴鑷达紱
    /// 鍐欐鍍忕礌鍊肩殑璇濓紝瀹界瑪涓婁細瀵嗗緱鍍忛娇閿€?    ///
    /// 绔附锛?    ///   路 铏氱嚎鐢?**骞冲ご**鈥斺€斿渾澶翠細璁╂瘡涓€鑺備袱澶撮紦鎴愬崐鍦嗭紝鑺傛尐寰楄繎鏃剁硦鎴愪竴涓茬彔瀛愶紱
    ///   路 鐐圭嚎**蹇呴』**鐢?**鍦嗗ご**锛岃€屼笖鑺傞暱鍙栦竴涓帴杩?0 鐨勫€硷細鍦嗗ご浼氬湪杩欎竴鑺備袱澶?    ///     鍚勯紦鍑哄崐涓瑪瀹斤紝鍚堣捣鏉ユ濂芥槸**涓€涓洿寰?= 绗斿鐨勫渾鐐?*銆傚钩澶?+ 0 闀垮害鐨勮妭
    ///     浼?*浠€涔堥兘鐢讳笉鍑烘潵**锛堣繖鏄?Direct2D 涓婁竴涓緢瀹规槗韪╃┖鐨勫湴鏂癸級銆?    /// 鎷愯涓よ竟閮界敤鍦嗚锛堝拰瀹炵嚎涓€鑷达紝绮楁姌绾夸笂涓嶄細鏈夊皷瑙掞級銆?    /// </summary>
    public static ID2D1StrokeStyle1 DashedStroke;
    public static ID2D1StrokeStyle1 DottedStroke;

    public static ID2D1StrokeStyle1 Dashed => DashedStroke ??= D2DFactory.CreateStrokeStyle(
        new StrokeStyleProperties1
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Flat,
            LineJoin = LineJoin.Round,
            MiterLimit = 10f,
            DashStyle = DashStyle.Custom,
        },
        new[] { 3f, 2f });                    // 鑺?3銆佺紳 2锛埫?绗斿锛夆啋 鍑哄ⅷ绾﹀叚鎴?
    public static ID2D1StrokeStyle1 Dotted => DottedStroke ??= D2DFactory.CreateStrokeStyle(
        new StrokeStyleProperties1
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Round,         // 瑙佷笂闈細鐐圭嚎闈犲渾澶存妸"闆堕暱搴︾殑鑺?榧撴垚涓€涓渾鐐?            LineJoin = LineJoin.Round,
            MiterLimit = 10f,
            DashStyle = DashStyle.Custom,
        },
        new[] { 0.01f, 2f });                 // 鑺傗増0銆佺紳 2锛埫?绗斿锛夆啋 鍑哄ⅷ绾︿笁鎴?
    /// <summary>
    /// 杩欎釜绾垮瀷璇ョ敤鍝竴鏉℃弿杈规牱寮忋€?*娓叉煋鍙湁杩欎竴涓垎娲剧偣**
    /// 锛堣 <c>DrawStrokeCore</c>锛夆€斺€旂嚎鍨嬭鍔犳。浣嶅氨鏀硅繖閲岋紝鍒湪鍚勫鑷繁鍒ゆ柇銆?    /// </summary>
    public static ID2D1StrokeStyle1 StyleFor(StrokeDash dash) => dash switch
    {
        StrokeDash.Dashed => Dashed,
        StrokeDash.Dotted => Dotted,
        _ => Round,
    };

    public static void Init()
    {
        Mem.Stage("0. 杩涚▼鍚姩锛堣繍琛屾椂 + 绋嬪簭闆嗭級");

        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
            Vortice.Direct3D.FeatureLevel.Level_10_0,
        };
        var hr = D3D11.D3D11CreateDevice((IDXGIAdapter)null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, levels, out Device);
        hr.CheckError();
        Mem.Stage("1. 鍒涘缓 Direct3D 11 璁惧涔嬪悗");

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        Factory = adapter.GetParent<IDXGIFactory2>();
        try { Adapter3 = adapter.QueryInterface<IDXGIAdapter3>(); } catch { Adapter3 = null; }

        try
        {
            var d = adapter.Description;
            ulong dedicated = (ulong)d.DedicatedVideoMemory;
            ulong shared = (ulong)d.SharedSystemMemory;
            AdapterInfo = $"{d.Description}  涓撶敤鏄惧瓨 {dedicated / 1048576.0:F0} MB, 鍏变韩鍐呭瓨 {shared / 1048576.0:F0} MB"
                        + (dedicated < 512UL * 1048576 ? "  鈫?鏍告樉锛堟樉瀛樻潵鑷郴缁熷唴瀛橈級" : "  鈫?鐙樉");
        }
        catch { }

        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(
            Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        D2DDevice = D2DFactory.CreateDevice(dxgiDevice);
        Mem.Stage("2. 鍒涘缓 Direct2D 璁惧涔嬪悗");

        WriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        Mem.Stage("3. 鍒涘缓 DirectWrite锛堟枃瀛楋級涔嬪悗");
    }

    public static void Shutdown()
    {
        RoundStroke?.Dispose();
        RoundStroke = null;
        DashedStroke?.Dispose();          // 绾垮瀷鐨勪袱鏉★紙瑙?StyleFor锛?        DashedStroke = null;
        DottedStroke?.Dispose();
        DottedStroke = null;
        WriteFactory?.Dispose();
        D2DDevice?.Dispose();
        D2DFactory?.Dispose();
        Factory?.Dispose();
        Device?.Dispose();
    }

    /// <summary>
    /// 鎶?D3D/DXGI **鍐呴儴**鐨勭紦瀛樼紦鍐茶繕缁欑郴缁燂紙<c>IDXGIDevice3::Trim</c>锛夈€?    ///
    /// 涓轰粈涔堥渶瑕佸畠锛氭樉鍗￠┍鍔ㄤ細鎶婁竴鎵瑰唴閮ㄧ紦鍐茬暀鐫€澶嶇敤锛堟彁閫熷悗缁粯鍒讹級锛?    /// 杩欎簺缂撳啿绠楀湪鎴戜滑鐨勫崰鐢ㄩ噷銆傚疄娴嬩竴涓囩瑪涔嬪悗鏄惧瓨 62 鈫?202 MB锛?    /// 鎶婄瑪鐢诲叏鍒犳帀銆佸嚑浣曞叏閮ㄩ噴鏀句箣鍚?*浠嶇劧鍋滃湪 201 MB**鈥斺€斿氨鏄繖浜涘唴閮ㄧ紦鍐层€?    /// Trim 璁╄繍琛屾椂鍜岄┍鍔ㄦ妸杩欎簺涓㈡帀锛涙枃妗ｆ槑纭瀹?涓嶆敼鍙樻覆鏌撶姸鎬併€?    /// 涓嶅奖鍝嶇粯鍒剁粨鏋?锛屼唬浠锋槸涔嬪悗绗竴甯ц閲嶆柊鍒嗛厤锛堟湁涓€娆℃€ц兘鍥炶惤锛夛紝
    /// 鎵€浠?*鍙湪绌洪棽鏃惰皟**锛堝ぇ鍒犻櫎涔嬪悗銆佹垨鑰呭仠鎵嬩竴浼氬効锛夈€?    /// </summary>
    public static bool TrimVideoMemory()
    {
        try
        {
            using var dxgiDevice = Device.QueryInterface<IDXGIDevice3>();
            dxgiDevice.Trim();
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// A borderless, click-through-capable, non-activating overlay window that owns
/// one DirectComposition swap chain. This is the core of the whole product: the
/// annotations you see are drawn directly by the GPU into a transparent layer
/// above the desktop.
/// </summary>
internal sealed partial class OverlayWindow : IDisposable
{
    public static long RebuildCount;

    private ID2D1DeviceContext1 _ctx1;

    /// <summary>甯у彿锛堣法绐楀彛鍏变韩锛夛細缁欑瑪鐢荤洊"鏈€杩戣鐢昏繃"鐨勬椂闂存埑锛岀敤鏉ユ窐姹板喎鎺夌殑缁嗗垎缂撳瓨銆?/summary>
    private static long s_frameNo;

    /// <summary>
    /// 鐢诲竷鍧愭爣 鈫?绐楀彛鍧愭爣銆?*鐩告満鍦ㄨ繖閲岀敓鏁?*锛氭覆鏌撶殑姣忎竴澶勫彉鎹㈤兘瑕佺敤瀹冿紝
    /// 婕忔帀浠讳綍涓€澶勶紝閭ｄ竴澶勭殑涓滆タ灏变笉浼氳窡鐫€婊氬姩锛堝吀鍨嬬棁鐘讹細绗旇抗婊氫簡銆?    /// 鎬ц兘闈㈡澘娌℃粴锛屾垨鑰呭弽杩囨潵锛夈€?    /// </summary>
    private Matrix3x2 CanvasToWindow =>
        Matrix3x2.CreateTranslation(-OriginX + ViewOffsetX, -OriginY + ViewOffsetY);

    /// <summary>
    /// 褰撳墠**鐪嬪緱瑙佺殑閭ｅ潡鐢诲竷**锛堢敾甯冨潗鏍囷級銆?    ///
    /// 鍑℃槸"鎷跨獥鍙ｇ煩褰㈠幓绛涚瑪鐢?鐨勫湴鏂归兘蹇呴』鐢ㄥ畠锛岃€屼笉鏄８鐢?OriginX/OriginY鈥斺€?    /// 鐩告満鍋忕Щ涓?0 鏃朵袱鑰呮伆濂界浉绛夛紝涓€婊氬姩灏变笉绛夈€?    ///
    /// **杩欎釜 bug 瀹炴祴韪╄繃**锛氭粴鍒?-1800 涔嬪悗鍐欑殑閭ｄ竴绗旓紝鐢诲竷鍧愭爣鏄?y=2500锛?    /// 鑰岀瓫閫夌敤鐨勬槸 [0,1800]锛屼簬鏄畠琚綋鎴?涓嶅湪瑙嗛噹閲?璺宠繃浜嗭紝灞忓箷涓婁竴涓儚绱?    /// 閮芥病鏈夆€斺€旇〃鐜板氨鏄?婊氫笅鍘诲啓鐨勫瓧鐪嬩笉瑙侊紝婊氬洖椤堕儴鍙堜竴鍒囨甯?銆?    /// </summary>
    private RectF VisibleCanvasRect => new()
    {
        MinX = OriginX - ViewOffsetX,
        MinY = OriginY - ViewOffsetY,
        MaxX = OriginX - ViewOffsetX + Width,
        MaxY = OriginY - ViewOffsetY + Height,
    };

    /// <summary>鐩告満鍋忕Щ锛堢敱寮曟搸姣忓抚鍐欒繘鏉ワ紱瑙?InkEngine.ViewOffsetY锛夈€?/summary>
    internal float ViewOffsetX, ViewOffsetY;
    public IntPtr Hwnd;
    public int OriginX, OriginY, Width, Height;
    public uint Dpi = 96;

    /// <summary>
    /// 婊氬姩鏉＄殑鍑犱綍锛?*灞忓箷鍧愭爣**锛屽拰 OriginX/Width 鍚屼竴濂楋級銆?    /// 鐢诲拰鍛戒腑鍒ゅ畾鍏辩敤涓€浠斤紝閬垮厤"瀵逛簡缁樺埗銆侀敊浜嗘嫋鍔?杩欑涓€鍗婂涓€鍗婇敊鐨勭姸鎬併€?    /// </summary>
    internal struct ScrollBarLayout
    {
        /// <summary>婊戝潡涓績绾匡紙閭ｆ潯缁嗙嚎鐨勪綅缃級銆?/summary>
        public float AxisX;
        /// <summary>杞ㄩ亾涓婁笅绔€?/summary>
        public float Top, Bottom;
        /// <summary>婊戝潡椤惰竟銆侀暱搴︼紝浠ュ強鍙Щ鍔ㄧ殑璺濈銆?/summary>
        public float ThumbTop, ThumbLen, MaxTravel;
        /// <summary>鐢诲竷鑼冨洿锛堟瘮渚嬫崲绠楄鐢級銆?/summary>
        public float ExtentMinY, ExtentH;

        /// <summary>
        /// 鎸囬拡鏄惁钀藉湪"鍙姄"鐨勮寖鍥撮噷銆傚搴︽寜 grabLogical 鏀惧锛堟粦鍧楀彧鏈?4 鍍忕礌瀹斤紝
        /// 鎸?4 鍍忕礌鍒ゅ畾绛変簬鐐逛笉涓級锛屼笂涓嬪悇鐣欎竴鐐逛綑閲忥紝鏂逛究涓€娆℃姄浣忋€?        /// </summary>
        public bool HitTest(float screenX, float screenY, float grabLogical, float dpi)
            => MathF.Abs(screenX - AxisX) <= grabLogical * dpi * 0.5f
            && screenY >= Top - 6f * dpi && screenY <= Bottom + 6f * dpi;
    }

    /// <summary>When true the window reports HTTRANSPARENT so mouse/pen input
    /// falls through to whatever is underneath.</summary>
    public bool PassThrough;

    private IDXGISwapChain1 _swapChain;
    private IDXGISwapChain2 _swapChain2;
    private IDXGISwapChain3 _swapChain3;      // 璇婃柇锛氳"褰撳墠鍚庣紦鍐插彿"锛堣 Trace锛?    private IntPtr _latencyWait = IntPtr.Zero;

    /// <summary>
    /// 鐢?DXGI 鐨?甯у欢杩熺瓑寰呭璞?鏉ラ厤閫燂紝鑰屼笉鏄潬 Present(1) 闃诲鍦ㄥ瀭鐩村悓姝ヤ笂銆?    ///
    /// **瀹炴祴缁撹锛氬鍚堟垚浜ゆ崲閾句笉閫傜敤锛岄粯璁ゅ叧闂€?* 寮€鍚悗甯х巼浠?60 鍐插埌 1726锛?    /// 缁樺埗 CPU 浠?16.5% 娑ㄥ埌 85.7%锛堝崟鏍革級銆丟PU 浠?2.9% 娑ㄥ埌 62.9%銆?    /// 鍘熷洜鏄悎鎴愪氦鎹㈤摼鐨勫抚浼氳 DWM 绔嬪埢鍙栬蛋锛岀瓑寰呭璞″嚑涔庝笉浼氶樆濉烇紝
    /// 澶卞幓浜嗗瀭鐩村悓姝ヨ繖灞傞厤閫熴€傚杩欑鍦烘櫙 Present(1) 鎵嶆槸姝ｇ‘鐨勮妭娴佹墜娈点€?    /// 淇濈暀杩欐浠ｇ爜锛堥粯璁ゅ叧闂€佸凡娌℃湁鍛戒护琛屽紑鍏筹級锛屼緵灏嗘潵鎹㈣澶?鎹㈤┍鍔ㄦ椂澶嶆祴銆?    /// 2026-09-14锛歚--latencywait` / `--vblankpace` / `--framestats` / `--bufcount`
    /// 杩欏嚑涓?*鍙敤浜庡鐓у疄楠?*鐨勫紑鍏冲凡鍒犻櫎锛岀粨璁虹暀鍦?寤舵椂-瀹炴祴涓庝紭鍖?md銆?    /// </summary>
    public static bool LatencyWaitEnabled = false;
    private IDCompositionDevice _dcomp;
    private IDCompositionDevice2 _dcomp2;
    private IDCompositionTarget _target;
    /// <summary>鏍硅瑙夛細绾鍣紙浜ゆ崲閾炬寕鍦ㄥ瓙瑙嗚閲岋紝澧ㄨ抗鎵嶆湁鍦版柟鍙犱笂鍘伙級銆?/summary>
    private IDCompositionVisual _visual;
    /// <summary>浜ゆ崲閾惧唴瀹硅瑙夛紙姣忓抚鐢荤殑鍐呭閮藉湪杩欙級銆?026-10-10 璇曢獙C 浠?鏍硅瑙夌洿鎺ヨ鍐呭"鏀规垚瀛愯瑙夈€?/summary>
    private IDCompositionVisual _swapVisual;
    /// <summary>濮旀墭澧ㄨ抗杞ㄨ抗瑙嗚锛堝唴瀹癸紳_inkTrail锛屽彔鍦ㄤ氦鎹㈤摼涔嬩笂锛夈€?026-10-10 璇曢獙C 鏂板銆?/summary>
    private IDCompositionVisual _inkVisual;
    private IDCompositionVisual _fastWetVisual;
    private IDCompositionSurface _fastWetSurface;
    private bool _fastWetReady;
    private int _fastWetLastPointCount;
    private float _fastWetLastCamX = float.NaN;
    private float _fastWetLastCamY = float.NaN;
    private RectF _fastWetLastBounds = RectF.Empty;
    private Color4 _fastWetColor;
    private bool _fastWetDrawing;
    public bool FastWetReady => _fastWetReady;
    private IDCompositionInkTrailDevice _inkTrailDevice;
    private IDCompositionDelegatedInkTrail _inkTrail;
    private bool _inkTrailRuntimeFailed;
    private ID2D1DeviceContext _ctx;
    /// <summary>
    /// D2D 1.3 鐨勮澶囦笂涓嬫枃 2锛?*鍘熺敓澧ㄨ抗**锛圛D2D1Ink锛夊敮涓€鑳藉垱寤虹殑鍦版柟
    /// 锛?c>CreateInk</c> / <c>DrawInk</c> 閮芥寕鍦ㄨ繖涓帴鍙ｄ笂锛夈€傛嬁涓嶅埌灏辨槸 null銆?    /// </summary>
    private ID2D1DeviceContext2 _ctx2;
    /// <summary>澧ㄨ抗绗斿皷鏍峰紡锛堝渾澶达級鈥斺€斿拰"绗旓紳鍦嗗ご"杩欐潯鍙ｅ緞涓€鑷淬€?/summary>
    private ID2D1InkStyle _inkStyle;
    /// <summary>姣忔鐢讳竴鏉″帇鎰熺瑪杩规渶澶氶摵澶氬皯娈碉紙**鍙湪鏇茬嚎鍖栧叧鐫€鏃?*鐢熸晥锛岃 DrawPressureInk锛夈€?/summary>
    private const int InkMaxSegments = 120;
    /// <summary>`--inkmodel` 鏃跺帇鎰熷ⅷ杩圭殑娈垫暟涓婇檺锛氬缓妯¤緭鍑?鈮?80Hz銆佺偣寰堝瘑锛屾寜 120 鎶戒細鎶婂集鎷夌洿銆?/summary>
    private const int InkMaxSegmentsModeled = 4096;
    /// <summary>
    /// 娈电紦鍐诧細**棰勫垎閰嶃€佸鐢?*锛屼笉鍦ㄦ瘡甯х粯鍒堕噷 new銆?    /// 鏇茬嚎鍖栨墦寮€鏃朵細鎸夌偣鏁板闀匡紙`--smooth`锛夛細鎶樼嚎娈靛皯鏄湁鎰忔娊绋€鐨勶紝鏇茬嚎娈典笉鑳芥娊鈥斺€?    /// 涓€鎶藉氨鎶婂垰绠楀嚭鏉ョ殑寮張鎷夌洿浜嗐€?    /// </summary>
    private InkBezierSegment[] _inkSegs = new InkBezierSegment[InkMaxSegments];
    /// <summary>`--mean2fit` 鐢細鎷熷悎杈撳叆鐨勫鐢ㄧ偣/鍘嬪姏缂撳啿锛堥潤鎬佹暟缁勶紝涓嶆瘡甯у垎閰嶏級銆?/summary>
    // [鍒犻櫎 2026-10-05] `_fitPts/_fitP`锛圵PF 鎷熷悎杈撳叆缂撳啿锛夛細闅忔嫙鍚堟。娓呯悊绉婚櫎銆?    /// <summary>鍘嬪姏鐨勬寚鏁板钩婊戠郴鏁帮紙0..1锛岃秺灏忚秺绋筹級銆傝 DrawPressureInk銆?/summary>
    private const float InkPressureEma = 0.35f;      // 涓婂崌锛堝姞鍘嬶級鏂瑰悜鐨勫钩婊?    /// <summary>涓嬮檷锛堝噺鍘嬶級鏂瑰悜鐨勫钩婊戔€斺€?*姣斾笂鍗囨參**锛?026-10-08锛夛細
    /// 鐪熷疄绗?鎸変綇鐨勭偣"鍘嬪姏鐖埌 ~0.3銆佷竴寮€鍐欏帇鍔涙帀鍥?0.03锛岀瓑閫熷钩婊戜細璁╁搴︾灛闂村鎴愬彂涓濈嚎
    /// 锛?鍦嗘秷澶卞彉灏栫瑪閿?锛夈€傞檷鎱竴鐐癸紝璁╁搴︽湁涓紦鍐诧紝涓嶈窡鐫€鍘嬪姏璺炽€?/summary>
    private const float InkPressureEmaDown = 0.14f;
    /// <summary>鏈€灏忓ⅷ杩瑰崐寰勶紙鐢诲竷鍍忕礌锛夛細杞诲帇鏃朵篃涓嶈嚦浜庣粏鍒扮敾涓嶅嚭鏉ャ€?/summary>
    private const float InkMinRadius = 0.4f;
    private ID2D1Bitmap1 _backBuffer;

    // ---- 鍐呭灞傦紙鐢诲竷绌洪棿鍒嗗潡缂撳瓨锛?--------------------------------------
    // 鍐呭灞?*涓嶅啀鏄竴寮犵粦鍦ㄥ睆骞曚笂鐨勪綅鍥?*锛岃€屾槸鐢诲竷绌洪棿閲岀殑涓€鏍间竴鏍煎皬浣嶅浘銆?    // 杩欐槸"婊氬姩涓嶉噸鐢?鍜?鍧愭爣涓嶄細蹇樻崲绠?鐨勬牴銆傝 CanvasTiles.cs 椤堕儴鐨勮鏄庛€?    private CanvasTileCache _tiles;
    /// <summary>涓婁竴娆℃妸鏂囨。鑴忓尯鍚屾杩涘垎鍧楃紦瀛樻椂鐨勬枃妗ｇ増鏈彿銆?/summary>
    private int _tilesVersion = -1;
    /// <summary>涓婁竴甯х殑鐩告満鍋忕Щ銆傚彉浜?鈫?鏁村睆鍚堟垚浣嶇疆閮藉彉浜嗭紝鍚庣紦鍐叉暣鍧椾綔搴熴€?/summary>
    private float _lastCamY = float.NaN;
    private bool _lastBoardOn;
    private Color4 _lastBoardColor = new(0f, 0f, 0f, -1f);
    /// <summary>涓婁竴甯?鏂囨。鍨櫧"鍦ㄤ笉鍦紙杩?鍑轰細鏁村眰閲嶉摵锛屽拰鏉胯壊鍚屼竴濂楋級銆?/summary>
    private bool _lastPaper;
    /// <summary>褰撳墠甯х殑寮曟搸寮曠敤锛氬厜鏍呭寲鍒嗗潡鏃惰鐢ㄦ枃妗ｅ拰搴曡壊銆?/summary>
    private InkEngine _app;

    // ---- 鐣岄潰缂撳瓨 --------------------------------------------------------
    // 鐣岄潰鐢诲埌鑷繁鐨勪綅鍥句笂锛屽彧鍦ㄥ畠澹版槑"鍙樹簡"鐨勬椂鍊欓噸鐢讳竴娆°€傝繖鏄紩鎿庝晶鐨?    // 纭€ц鐭╋細璋冭瘯闈㈡澘瀹炴祴姣忓抚 2.7 ms锛岃€岀瑪杩规湰韬彧瑕?0.7 ms鈥斺€旂晫闈竴鏃?    // 杩涙瘡甯ц矾寰勶紝灏辨瘮绗旇抗鏈韩杩樿吹銆?    private RectF _uiBounds;          // 鐗╃悊鍍忕礌锛屽睆骞曞潗鏍?    private RectF _uiLogicalBounds;   // 閫昏緫鍍忕礌锛屽睆骞曞潗鏍?
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private ID2D1SolidColorBrush _scratch;
    private ID2D1SolidColorBrush _transparentBrush;
    /// <summary>鍐呭灞?鎿﹂櫎"鐢ㄧ殑搴曡壊鐢诲埛锛氶€忔槑鎵规敞鏃舵槸鍏ㄩ€忔槑锛岀櫧鏉挎椂鏄笉閫忔槑搴曘€?/summary>
    private ID2D1SolidColorBrush _boardBrush;
    private Color4 _boardBrushColor = new(-1f, -1f, -1f, -1f);
    private readonly List<Stroke> _strokeScratch = new();

    // ---- 鑴忓尯涓婂睆鐘舵€?----------------------------------------------------
    // 姣忓抚鍙妸鐪熸鍙樺寲鐨勭煩褰氦缁?Present1锛孌WM 灏辫兘璺宠繃娌″彉鐨勫尯鍩熴€?    // 娉ㄦ剰鍙岀紦鍐诧細褰撳墠鍚庣紦鍐查噷瑁呯殑鏄?*涓ゅ抚鍓?*鐨勭敾闈紝鎵€浠ヨ剰鍖哄繀椤昏鐩?    // 鏈€杩戜袱甯х殑涓存椂鍥惧厓鑼冨洿锛屽惁鍒欏揩閫熺Щ鍔ㄧ殑绗旂敾/婵€鍏変細鐣欎笅娈嬪奖銆?    private readonly List<RectF> _frameDirty = new();
    private readonly List<Vortice.RawRect> _presentRects = new();
    private readonly RectF[] _transientHistory = new RectF[2];
    private RectF _transientNow = RectF.Empty;
    /// <summary>涓婁竴甯х晫闈㈠崰鐨勭煩褰€傜晫闈㈡秷澶辨椂寰楅潬瀹冩妸鏃х敾闈㈡摝鎺夈€?/summary>
    private RectF _uiBoundsPrev = RectF.Empty;
    /// <summary>涓婁竴甯у浘搴撻潰鏉垮崰鐨勭煩褰紙绐楀彛鍧愭爣锛夈€傞潰鏉垮叧鎺夋椂瑕侀潬瀹冩妸閭ｅ紶鍗＄墖鎿︽帀銆?/summary>
    private RectF _libraryRectPrev = RectF.Empty;

    /// <summary>涓婁竴甯?鑷粯钀界偣鍙嶉"锛堟鐨 / 鍦嗙幆锛夊崰鐨勭煩褰紙绐楀彛鍧愭爣锛夈€?    /// **蹇呴』骞惰繘鑴忓尯**锛氭寚閽堜竴甯ч噷鍙兘绉诲姩瓒呰繃鍗婂緞锛堝揩鎿︺€侀潰绉摝鐨勬杩樹細闅忛€熷害鍙樺ぇ鍙樺皬锛夛紝
    /// 鍙畻褰撳墠浣嶇疆鐨勮瘽锛屾棫妗嗙暀鍦ㄥ師鍦扮殑閭ｄ竴鏉″氨鎴愪簡"鎿︿笉鎺夌殑绔栫嚎 / 鍙樻殫鐨勫甫瀛?鈥斺€?    /// 鐢ㄦ埛 2026-09-30 鎶ョ殑"姗＄毊宸︿晶 / 宸︿笂渚ч潬杩戝ⅷ杩规椂鍑虹幇缁嗗瘑绔栫嚎銆侀鑹插彉娣?灏辨槸瀹冦€?/summary>
    private RectF _cursorRectPrev = RectF.Empty;
    /// <summary>涓婁竴甯у鎸囨摝浼氳瘽鐨勮惤鐐瑰弽棣堝寘鍥寸洅锛氱粨鏉熼偅涓€甯у啀鎿︿竴娆℃棫浣嶇疆锛?涓嶆秷澶?灏卞嚭鍦ㄨ繖锛夈€?/summary>
    private RectF _touchEraseRectPrev = RectF.Empty;

    /// <summary>
    /// 鍙崇紭婊氬姩鏉￠偅涓€鏉°€?*鍗曠嫭鎴愪竴鍧楄剰鍖?*锛屼笉骞惰繘闈欐鍥惧厓鐨勮仈鍚堝寘鍥寸洅
    /// 锛堢悊鐢辫 <c>ComputeTransientBounds</c> 鏈熬锛氬苟杩涘幓浼氭妸"绗旇抗宸﹁竟"鎺掑埌鑴忓尯澶栵級銆?    /// </summary>
    private RectF _scrollbarRect = RectF.Empty;

    /// <summary>涓婁竴甯х殑婊氬姩鏉＄煩褰細娣″嚭缁撴潫閭ｄ竴甯ц繕瑕佹妸瀹冩摝鎺夛紙鍚?_cursorRectPrev 鐨勫仛娉曪級銆?/summary>
    private RectF _scrollbarRectPrev = RectF.Empty;

    /// <summary>涓婁竴甯?鎴浘鍙栨櫙"寮€鐫€鍚椻€斺€旀敹鍦洪偅涓€甯ц鏁寸獥閲嶇敾锛屾妸閬僵鎿﹀共鍑€锛?.3.0锛夈€?/summary>
    private bool _captureWasActive;

    /// <summary>PPT 鎺т欢鏉′笂涓€甯х殑鐭╁舰锛堥€€鍑烘斁鏄?/ 鎷栧姩閭ｄ竴甯ц闈犲畠鎶婃棫浣嶇疆鎿﹀共鍑€锛?    /// 鍚?`_libraryRectPrev` 鐨勫仛娉曪級銆?/summary>
    private RectF _pptBarRectPrev = RectF.Empty;
    /// <summary>鍥炴斁鎺у埗鏉′笂涓€甯х殑鐭╁舰锛堥€€鍑哄洖鏀鹃偅涓€甯ц鎶婃棫浣嶇疆鎿﹀共鍑€锛屽悓 PPT 鏉★級銆?/summary>
    private RectF _replayBarRectPrev = RectF.Empty;
    /// <summary>璁℃椂鍗＄墖涓婁竴甯х殑鐭╁舰锛堝仠涓?/ 鏀惰捣 / 鎷栧姩閭ｄ竴甯ц鎶婃棫浣嶇疆鎿﹀共鍑€锛夈€?/summary>
    private RectF _timerRectPrev = RectF.Empty;
    /// <summary>鐐瑰悕鍗＄墖涓婁竴甯х殑鐭╁舰銆?/summary>
    private RectF _rollRectPrev = RectF.Empty;
    /// <summary>寮曟搸渚ф偓鍋滄彁绀轰笂涓€甯х殑鐭╁舰锛堟崲鍧?/ 闅愯棌閭ｄ竴甯ц鎶婃棫鍗℃摝骞插噣锛屽悓 PPT 鏉★級銆?/summary>
    private RectF _tipRectPrev = RectF.Empty;

    /// <summary>椤电爜閭ｄ竴娈垫枃瀛楃殑鏍煎紡锛?3 閫昏緫鍍忕礌锛岃 `PptPageFormat`锛夈€?/summary>
    private IDWriteTextFormat _pptPageFmt;
    private float _pptPageFmtPx;
    /// <summary>鎮仠鎻愮ず鐨勪袱琛屾牸寮忥細绗竴琛?12.5銆佺浜岃 11锛堥兘宸﹀榻愶紝閿綅璺熷悕绉板悓涓€琛岋級銆?/summary>
    private IDWriteTextFormat _tipTitleFmt, _tipNoteFmt;
    private float _tipTitleFmtPx, _tipNoteFmtPx;

    // 鍐呭灞傜殑鏀瑰姩涔熻璁颁袱甯э細鍚庣紦鍐查噷韬虹潃鐨勬槸涓ゅ抚鍓嶇殑鐢婚潰銆?    private readonly List<RectF> _contentDirtyNow = new();
    private readonly List<RectF> _contentDirtyPrev = new();

    // 鏂囨。椤靛眰锛堝浘鐗?PDF锛夌殑鑴忓尯鍚岀悊锛氶〉鐢熸垚/鍥炴敹鏃㈣鏍囧潡鑴忥紝涔熻杩?涓婂睆鑴忓尯"锛?    // 鑰屼笖鍚屾牱瑕佽涓ゅ抚锛堜袱涓悗缂撳啿閮借瑕嗙洊鍒帮級銆?*绐楀彛鍧愭爣**銆?    private readonly List<RectF> _pageDirtyNow = new();
    private readonly List<RectF> _pageDirtyPrev = new();

    /// <summary>鏁村潡鍚庣紦鍐插唴瀹规棤鏁堬紙棣栧抚銆侀噸寤恒€佸昂瀵稿彉鍖栵級鏃跺繀椤诲叏灞忛噸缁樹竴娆°€?/summary>
    /// <summary>
    /// "鎺ヤ笅鏉ヨ繕瑕佹暣灞忛噸鐢诲嚑甯?銆?    ///
    /// **涓轰粈涔堟槸 2 鑰屼笉鏄?1**锛?026-10-04 淇?婊氬畬杞竴鎸夊氨闂?閿欎綅銆佸彧鍦ㄥ乏渚?锛夛細
    /// 鐢婚潰鍦ㄤ袱涓悗缂撳啿涔嬮棿浜ゆ浛锛屼竴娆℃暣灞忛噸鐢诲彧鏇存柊**鍏朵腑涓€涓?*銆傚鏋滃彧寮哄埗涓€甯э紝
    /// 鍙︿竴涓紦鍐蹭細鍋滃湪鍙樺寲鍓嶏紙鏃х浉鏈轰綅缃?/ 鏃у簳鑹?/ 鏃ф枃妗ｏ級鐨勭敾闈€傚钩鏃惰繖涓棫鐢婚潰
    /// 闈?鍙樺寲鐨勫儚绱犺鍦ㄩ殢鍚庝袱甯ч兘閲嶇敾"鐨勮剰鍖哄巻鍙插厹浣忥紝浣?*鐩告満鍙樺寲娌℃湁杩欎唤涓ゅ抚鍘嗗彶**锛?    /// 浜庢槸婊氬姩鍋滀笅鍚庣涓€甯у鏋滃彧閲嶇敾灞€閮紙绗旇抗 + 婊氬姩鏉″苟鎴愮殑鍖呭洿鐩掞級锛屾棫缂撳啿閲?    /// 娌¤瑕嗙洊鐨勯偅涓€鍧楀氨浼氶湶鍑烘潵鈥斺€斿氨鏄敤鎴风湅鍒扮殑"浠庢煇鏉＄嚎闅斿紑銆佷竴渚ч敊浣?闂?銆?    /// 寮哄埗**杩炵画涓ゅ抚**鏁村睆锛屼繚璇佷袱涓紦鍐查兘鏇存柊鍒版柊鐘舵€併€?    /// </summary>
    private int _fullFramesLeft = 2;

    /// <summary>
    /// 鏃嬭浆搴︽暟鏍囩鐢ㄧ殑鏂囧瓧鏍煎紡銆?*鎸?DPI 鐢熸垚**锛氱粯鍒舵椂鐨勫彉鎹㈠彧鏈夊钩绉伙紝
    /// 瀛楀彿鍐欐 15 灏辩瓑浜?15 鐗╃悊鍍忕礌"锛?00% 缂╂斁涓嬩細灏忎竴鍗娿€?    /// </summary>
    private IDWriteTextFormat _readoutFormat;
    private float _readoutFormatPx;

    /// <summary>
    /// **瑙掓爣**锛堜笁瑙掑舰鐨勪笁涓唴瑙?/ 骞宠鍥涜竟褰㈢殑涓や釜澶硅锛夌敤鐨勫皬鍙锋枃瀛楁牸寮忥細
    /// 姣斾富璇绘暟灏忎竴鍙凤紙11 閫昏緫鍍忕礌锛夈€?    ///
    /// 涓轰粈涔堝彟瀛樹竴浠姐€佷笉鎶婁富璇绘暟璋冨皬锛氳鏍囨槸璐村湪椤剁偣鏃佽竟鐨勶紝澶ぇ灏变細鍘嬩綇閭ｆ潯杈广€?    /// 锛堜互鍓嶄笁瑙掑舰涓棿杩樺嵃涓€琛?鍐呰鍜?锛岄偅涓€琛屾墠鐢ㄤ富璇绘暟閭ｄ竴鍙峰瓧锛涚敤鎴?2026-09-19
    /// 鎶婇偅涓€琛屽幓鎺変簡锛屼簬鏄鏍囨槸杩欎竴缁勯噷鍞竴鐨勫瓧鍙枫€傦級
    /// </summary>
    private IDWriteTextFormat _readoutFormatSmall;
    private float _readoutFormatSmallPx;

    /// <summary>璁℃椂鍗＄墖涓婄殑鏁板瓧锛?9 閫昏緫鍍忕礌锛変笌鏀惧ぇ鎬佺殑澶у瓧锛?2 閫昏緫鍍忕礌锛夈€?/summary>
    private IDWriteTextFormat _timerValueFormat, _timerBigFormat;
    private float _timerValueFormatPx, _timerBigFormatPx;
    /// <summary>鐐瑰悕缁撴灉鐨勫瓧鍙锋牸寮忥紙鎸夌粨鏋滈暱搴﹁嚜閫傚簲锛岀幇寤虹幇缂撳瓨锛夈€?/summary>
    private IDWriteTextFormat _rollResultFormat;
    private float _rollResultFormatPx;
    /// <summary>鍒扮偣闂儊閭ｄ竴鎷嶇殑棰滆壊锛堝拰涓婚鏃犲叧锛氬氨鏄?鎻愰啋绾?锛夈€?/summary>
    private static readonly Color4 TimerFlashCol = new(0.91f, 0.26f, 0.21f, 1f);

    /// <summary>
    /// 鎷栫鐐规椂鐨?涓存椂鍑犱綍"澶嶇敤鐨勯偅鏉?scratch 绗旂敾锛堣 <see cref="DrawVertexPreview"/>锛夈€?    ///
    /// 涓轰粈涔堝鐢ㄥ畠銆佽€屼笉鏄湪杩欓噷鍙﹀啓涓€濂?鐢讳竴鏉＄嚎 / 涓€涓澶?锛氬浘褰㈢殑鐢绘硶
    /// 锛堝嚑浣曘€佹弿杈广€佺澶村ご閮ㄦ瘮渚嬶級宸茬粡鍦?Stroke 閲屼簡锛屽彟鍐欎竴浠借繜鏃╀細涓嶄竴鏍枫€?    /// 瀹冨拰"姝ｅ湪涔﹀啓鐨勯偅涓€绗?鏄悓涓€绉嶄笢瑗库€斺€旀瘡甯ч噸寤轰竴娆″嚑浣曪紝浠ｄ环涓€鏍枫€?    /// </summary>
    private Stroke _vertexGhost;

    // 鎬ц兘闈㈡澘锛氫竴甯т竴寮犵紦瀛樹綅鍥撅紝鏂囧瓧鍙樹簡鎵嶉噸鐢汇€?    private ID2D1Bitmap1 _hudTarget, _hudSource;
    private ID3D11Texture2D _hudBmpTex;
    private IDWriteTextFormat _hudFormat;
    private float _hudFormatPx;
    private int _hudBmpW, _hudBmpH;
    private string _hudCacheText;

    /// <summary>鑴忓尯瑕佸洖婧嚑甯х殑涓存椂鍥惧厓銆傚弻缂撳啿涓嬪繀椤?鈮?锛屽惁鍒欎細鍑烘畫褰憋紱
    /// 浠呯敤浜庡鐓у疄楠岋紝姝ｅ父杩愯涓嶈鏀广€?/summary>
    public static int TransientHistoryFrames = 2;
    public int LastPresentRectCount;
    public double LastPresentAreaPercent;
    /// <summary>
    /// 鎬ц兘闈㈡澘鐨勫昂瀵革紙**閫昏緫**鍍忕礌锛屾寜 DPI 鏀惧ぇ鎴愮墿鐞嗗儚绱狅級銆?    ///
    /// 鑰佺増鏈槸鍐欐鐨?1000脳262 鐗╃悊鍍忕礌 + 15 鍙峰瓧锛氬湪 200% 缂╂斁鐨勫睆涓婏紝
    /// 15 鐗╃悊鍍忕礌鍙湁 7.5 閫昏緫鍍忕礌楂樷€斺€旀姇褰变笂鏍规湰鐪嬩笉娓咃紙鐢ㄦ埛鍘熻瘽锛氱湅涓嶆竻锛夈€?    /// 瀛楀彿蹇呴』璺熺潃 DPI 璧帮紝杩欐墠鏄?鐪嬪緱娓?鐨勬牴鏈師鍥狅紝涓嶆槸鎶婃鏀惧ぇ灏辫銆?    /// </summary>
    public const float HudWidthLogical = 700f;
    public const float HudHeightLogical = 258f;
    public const float HudFontLogical = 17f;
    public const float HudMarginLogical = 12f;
    public const float HudPadLogical = 10f;

    /// <summary>鏄惁鎴愬姛鎷垮埌寰蒋鐨勫鎵樺ⅷ杩硅建杩规帴鍙ｏ紙杩涚▼绾ф帰娴嬬粨鏋滐級銆?/summary>
    public static bool InkTrailAvailable;
    public static string InkTrailNote = "鏈皾璇?;
    public static string InkTrailDebug = "";

    /// <summary>
    /// 杩欏彴鏈哄櫒涓婃湁娌℃湁 **D2D 鍘熺敓澧ㄨ抗**锛堝帇鎰熺瑪杩圭殑鍙樺閫氶亾锛夈€傝 <see cref="InkNote"/>銆?    ///
    /// 瀹冨喅瀹?鏈夊帇鎰熺殑绗旇抗"鎬庝箞鐢伙細鍙敤 鈫?`ID2D1Ink`锛堟瘡鐐逛竴涓崐寰勶紝D2D 鑷繁绠楄疆寤擄級锛?    /// 涓嶅彲鐢?鈫?閫€鍥炵瓑瀹芥弿杈癸紙涔熷氨鏄?2026-09-14 浠ユ潵閭ｆ潯璺紝瑙傛劅涓嶅彉銆佸彧鏄笉鍚冨帇鎰燂級銆?    /// </summary>
    public static bool InkAvailable;
    /// <summary>鍚姩鏃ュ織閲岄偅涓€琛岋紙鍙笉鍙敤銆佷负浠€涔堬級銆?/summary>
    public static string InkNote = "鏈皾璇?;
    /// <summary>
    /// 璧拌繃**鍙樺澧ㄨ抗**閭ｆ潯璺殑娆℃暟锛堣嚜妫€鐢級銆?    ///
    /// 涓轰粈涔堣杩欎釜璁℃暟锛氳繖鏉¤矾"璋冪敤鎴愬姛浣嗗睆骞曚笂浠€涔堥兘娌℃湁"鏄竴绉嶅彲鑳界殑澶辫触褰㈡€?    /// 锛堜笉鍍忔姏寮傚父閭ｆ牱浼氳嚜宸卞枈鍑烘潵锛夛紝鑰岃嚜妫€鐨勬暟鍍忕礌鍒ゆ嵁鍙湅灞忓箷鈥斺€?    /// 鏈夎繖涓鏁版墠鑳藉尯鍒?娌¤蛋杩欐潯璺?鍜?璧颁簡浣嗘病鐢诲嚭鏉?銆?    /// </summary>
    public static int PressureInkDraws;

    /// <summary>
    /// 鏄惁鍚敤濮旀墭澧ㄨ抗杞ㄨ抗锛堝彧瀵?`PT_PEN` 鍠傜偣锛夈€傚紑鍏崇瓥鐣ュ湪 `Engine`锛?    /// 鏈夌瑪鏁板瓧鍖栧櫒鏃堕粯璁ゅ紑锛宍--inktrail` / `--noinktrail` 鍙己鍒躲€?    ///
    /// 鈿?2026-10-05 鐪熺瑪瀹炴祴瀹氭锛?*鏈満鐨勭郴缁熶笉娓叉煋杩欐潯杞ㄨ抗**鈥斺€擟OM 璋冪敤鍏ㄩ儴鎴愬姛銆?    /// 鐐逛篃鍠備簡杩涘幓锛?522/1522锛夛紝灞忓箷涓婃病鏈変换浣曟柊澧炲ⅷ锛堝垽鎹紳鎸変笅鏈熼棿鍍忕礌澧為噺锛?    /// 鐢ㄦ埛鐩纭"鍐欑殑鏃跺€欑湅涓嶈澧ㄨ抗锛屾姮绗旀墠浼氬嚭鐜?锛夈€傛墍浠ュ畠鍦ㄦ湰鏈?*绌鸿浆浣嗕笉鑷村**
    /// 锛堟箍澧ㄥ疄鍐碉紳鑷粯锛屽悎鎴愯竟鐣岄厤閫?1.0 甯т笂灞忥級锛屼繚鐣欎綔鎹㈡満鍣?鎹㈠钩鏉跨殑楠岃瘉閫氶亾锛?    /// `--syswet`锛堝彧璁╃郴缁熺敾锛夋湰鏈哄嬁鐢ㄣ€傝 `寤舵椂-瀹炴祴涓庝紭鍖?md` 搂鍏€?    /// </summary>
    public static bool InkTrailEnabled;

    /// <summary>
    /// `--syswet`锛氱湡绗斿啓瀹炵嚎绗旀椂锛?*婀垮ⅷ鍙绯荤粺杞ㄨ抗鐢?*锛堟垜浠笉鍐嶈嚜缁樺綋鍓嶇瑪鐢伙級锛?    /// 鎶瑪鍚庣敱骞插ⅷ鎺ョ銆傞粯璁ゅ叧 = 淇濇寔"涓ゆ潯閮界敾"鐨勮€佽涓猴紝鏂逛究 A/B 涓庡洖閫€銆?    /// 鈿?2026-10-05 鏈満鐪熺瑪瀹炴祴锛氱郴缁熻建杩逛笉娓叉煋锛圕OM 鎴愬姛浣嗘棤鍙澧級锛?    /// 鎵€浠ユ湰鏈?*鍕垮惎鐢?*锛涗繚鐣欏畠鐢ㄤ簬鎹㈡満鍣?鎹㈠钩鏉跨殑楠岃瘉銆傝 `寤舵椂-瀹炴祴涓庝紭鍖?md` 搂鍏€?    /// </summary>
    public static bool SystemWetPreferred;

    /// <summary>
    /// 绯荤粺濮旀墭杞ㄨ抗涓嶅彲鐢ㄦ椂锛屾槸鍚﹀惎鐢ㄥ簲鐢ㄤ晶 DirectComposition 婀垮ⅷ灞傘€?    /// 杩欐槸鈥滃鎵樿建杩规浛韬€濓紝涓嶆槸棰勬祴锛氬彧鐢诲凡缁忔敹鍒扮殑鐪熷疄鐐癸紝涓嶈繘鏂囨。銆佷笉鏀硅緭鍏ユā鍨嬨€?    /// </summary>
    public static bool FastWetFallbackEnabled = true;

    /// <summary>
    /// "鎸変綇涓嶅姩 = 鍙抽敭"杩欐潯**绯荤粺鎵嬪娍**鍦ㄦ湰绐楀彛鍐呭叧鎺変簡鍚楋紙瑙?    /// <see cref="Native.DisableSystemPressAndHold"/>锛夈€?    ///
    /// 鍋氭垚闈欐€佸瓧娈垫槸涓轰簡**鍗拌繘鍚姩妯箙**锛氬畠鐢熸晥娌℃湁鐩存帴鍐冲畾"鍋滈】鎴愬瀷"璇曞緱鍑烘潵璇曚笉鍑烘潵锛?    /// 鑰屽け鏁堟椂鐨勭幇璞★紙绗斿皷鍋滀綇寮瑰彸閿幆锛夌湅璧锋潵鍍?璇嗗埆涓嶅噯"锛屽緢瀹规槗鏌ラ敊鏂瑰悜銆?    /// </summary>
    public static bool PressAndHoldDisabled;

    public double LastRebuildMs;
    /// <summary>涓婁竴甯ф€ц兘闈㈡澘鑷繁鐨勪唬浠凤紙閲嶆帓 + 璐村浘锛夈€傞潰鏉跨殑浠ｄ环涔熻鑳借璐ㄧ枒銆?/summary>
    public double LastHudMs;
    /// <summary>
    /// 闈㈡澘**閲嶆帓涓€娆?*鐨勮€楁椂锛堝彧鏈夋枃瀛楀彉浜嗘墠閲嶆帓锛屽叾浣欏抚鏄?0锛夈€?    /// 鍒嗗紑鎶ョ殑鍘熷洜锛氭憡鍒版瘡甯х殑鍧囧€间細鎶?4 Hz 閲嶆帓涓€娆?杩欎欢浜嬭棌璧锋潵锛?    /// 鑰?闈㈡澘姣旂瑪杩硅繕璐?杩欑鍗拌薄蹇呴』鑳借瑙ｉ噴娓呮銆?    /// </summary>
    public double LastHudRedrawMs;
    /// <summary>绱閲嶆帓娆℃暟锛堣皟鐢ㄦ柟鐢ㄥ畠绠?姣忕閲嶆帓鍑犳"锛夈€?/summary>
    public long HudRedraws;
    /// <summary>涓婁竴甯ц蛋"鍙ˉ鐢?璺緞鐨勫潡鏁帮紙璇婃柇锛夈€?/summary>
    public int LastAppendedTiles;
    /// <summary>绱"琛ョ敾"鍧楁暟锛堣瘖鏂級銆?/summary>
    public long TotalAppendedTiles;
    /// <summary>涓婁竴甯ф湁澶氬皯鏉℃柊绗旂敾娌¤兘璧拌ˉ鐢昏矾寰勶紙璇婃柇锛夈€?/summary>
    public int LastAppendMissed;
    public double LastAppendMs;
    public double LastPatchMs;
    public int LastPatchCount;
    public double LastRecordMs;
    public double LastPresentMs;

    // ---- 寤舵椂鎺㈤拡 --------------------------------------------------------
    // Present 鍓嶅悗鐨?QPC 鏃舵爣锛屼互鍙?DXGI 鎶ュ憡鐨勪笂灞忔椂鍒汇€傝 Latency.cs 鐨勮鏄庯細
    // 鍓嶄袱涓槸绮剧‘鍊硷紝"涓婂睆鏃跺埢"甯?卤1 甯т笉纭畾搴︼紙绐楀彛鍖栧悎鎴愪氦鎹㈤摼涓婏紝
    // GetFrameStatistics 缁欑殑鏄?鏈€杩戜竴娆＄湡姝ｄ笂灞忕殑甯?锛屼笉涓€瀹氭濂芥槸杩欎竴甯э級銆?    public ulong LastPresentStartQpc;
    public ulong LastPresentEndQpc;
    public ulong LastDisplayQpc;
    public bool LastFrameStatsOk;
    public ulong LastPresentCount;
    /// <summary>鍒锋柊鍛ㄦ湡锛堟绉掞級銆傛潵鑷?GetFrameStatistics 鐨勫埛鏂拌鏁板樊锛屾嬁涓嶅埌鏃舵寜 60Hz 浼般€?/summary>
    public double RefreshPeriodMs = 1000.0 / 60.0;

    /// <summary>浜ゆ崲閾剧殑鍚庣紦鍐叉暟閲忥細2 鏄渶浣庡欢鏃剁殑甯歌閫夋嫨銆?/summary>
    public static int BufferCount = 2;

    /// <summary>
    /// 璇婃柇寮€鍏筹紙`--fullpresent`锛?026-10-04 鍔狅級锛?*姣忔閮界敤鏁村睆 Present锛?    /// 涓嶈蛋 `Present1` 鐨勮剰鐭╁舰**銆?    ///
    /// 鐢ㄦ潵鍒嗚鲸鐢ㄦ埛鎶ョ殑"灞忓箷涓婂浐瀹氫竴鏉℃í绾裤€佹粴鍔?涔﹀啓鏃朵粠閭ｉ噷闂?锛氶偅涓€绫绘洿鍍忔槸
    /// 鑴忕煩褰㈤儴鍒嗕笂灞忓拰 DWM/MPO锛堢‖浠跺钩闈級鍚堟垚鐨勭浉浜掍綔鐢紝鑰屼笉鏄垜浠ⅷ杩瑰眰鐨?    /// 鑴忓尯婕忕敾锛坄--prevflash` 宸叉帓闄ゅ悗鑰咃級銆傚叧鎺夎剰鐭╁舰涓婂睆濡傛灉闂氨娌′簡锛?    /// 灏辫鏄庤鍦?閮ㄥ垎涓婂睆"杩欐潯璺笂鍋氫骇鍝佺骇鍏滃簳銆?    /// </summary>
    public static bool FullPresent;

    /// <summary>
    /// 姣忓抚鍦ㄦ覆鏌撲箣鍓嶅厛 DwmFlush锛岀瓑鍒板悎鎴愯竟鐣屽啀鎶借緭鍏ャ€佹彁浜ゃ€?    /// 瑙?Native.DwmFlush 鐨勮鏄庯紝浠ュ強 README 閲屽欢鏃堕偅涓€鑺傘€?    /// </summary>
    public static bool VBlankPaced;

    /// <summary>
    /// 璇婃柇寮€鍏筹紙`--scrollflash` 绛夋帰閽堢敤锛夛細鎶婃瘡涓€甯х殑鑴忓尯/涓婂睆/鍚庣紦鍐插彿鎵撳埌鎺у埗鍙般€?    /// 鐢ㄦ潵杩?婊氬畬鎸変笅鏃跺乏鍒楅敊浣?杩欑被鍙彂鐢熷湪鐗瑰畾甯х殑闂锛?026-10-04锛夈€?    /// </summary>
    public static bool Trace;

    public int LastDrawnStrokes;
    /// <summary>涓婁竴鎷嶉鍙栦簡鍑犲潡 / 鑰楁椂 / 棰勭畻鍙ｅ緞锛堣瘖鏂笌 HUD锛夈€?/summary>
    public int LastPrefetch;
    public double LastPrefetchMs;
    public bool LastPrefetchPending;
    /// <summary>甯搁┗鍒嗗潡鏁?/ 杩欎竴甯у彲瑙佸潡鏁?/ 鍒嗗潡棰勭畻锛堣瘖鏂敤锛夈€?/summary>
    public int LastTileCount, LastTileVisible, LastTileBudget;
    public string LastError;

    public ID2D1DeviceContext Context => _ctx;

    // ---- 濮旀墭澧ㄨ抗杞ㄨ抗锛堝井杞殑浣庡欢杩熸箍澧ㄩ€氶亾锛?----------------------------
    private bool _trailActive;
    private uint _trailGeneration;
    private float _trailRadius = 2f;
    /// <summary>杩欎竴鏉¤建杩规垚鍔熷杺杩涘幓鐨勭偣鏁帮紙`--syswet` 鎶戝埗鍒ゆ嵁 + `--wetinktest` 鍒ゆ嵁閮借鐢級銆?/summary>
    private int _trailPointsFed;
    /// <summary>鏀剁瑪鍚?绛夎繖涓€甯?Present 瀹屽啀鎾よ建杩?鐨勬寕璧锋爣璁帮紙闃?杞ㄨ抗鍏堟病銆佸共澧ㄦ湭涓婂睆"闂竴甯э級銆?/summary>
    private bool _trailEndPending;
    /// <summary>
    /// 鏈抚**鎴戜滑鑷粯**鎶?姝ｅ湪鍐欑殑杩欎竴绗?鐢讳笂浜嗗睆锛圥resent 鍚庢嵁姝ゆ妸杞ㄨ抗閲屽悓鏍疯繖娈靛垹鎺夛紝
    /// 瑙?<see cref="TrimInkTrailAsDrawn"/>锛夈€俙--syswet` / 鑷鎶戝埗鑷粯鏃朵负 false鈥斺€?    /// 閭ｆ椂婀垮ⅷ鍙湁杞ㄨ抗杩欎竴浠斤紝鍒犱簡灏辨病浜嗐€?    /// </summary>
    private bool _drewWetStrokeThisFrame;
    /// <summary>杞ㄨ抗鐐瑰凡缁?缁撹处"鍒板摢涓€浠ｏ紙姣忚捣涓€绗斿綊闆讹級銆?/summary>
    private uint _trailTrimmedGeneration;
    /// <summary>杩欎竴鏉¤建杩瑰凡缁?缁撹处"鍒犳帀鐨勭偣鏁帮紙璇婃柇鐢級銆?/summary>
    private int _trailPointsRemoved;
    /// <summary>杩欎釜绐楀彛鎺㈣繃杞ㄨ抗鎺ュ彛娌℃湁銆?*鎸夌獥鍙?*鈥斺€斿灞忔椂姣忎釜绐楀彛鍚勮涓€鏉¤建杩癸紙2026-10-11 淇級銆?/summary>
    private bool _trailProbed;
    /// <summary>杩涚▼绾х疮璁″杺鐐癸紙`--wetinktest` 鍒?绯荤粺杞ㄨ抗鍒板簳鍠傛病鍠傝繘鍘?锛夈€?/summary>
    public static long InkTrailPointsFed;
    /// <summary>杩涚▼绾х疮璁?缁撹处"鍒犳帀鐨勭偣鏁帮紙璇婃柇鐢細榛樿妯″紡涓嬪簲绾︾瓑浜庡杺鐐规暟鍑忓幓鍦ㄥ満鐐规暟锛夈€?/summary>
    public static long InkTrailPointsRemoved;
    /// <summary>
    /// 璇曢獙D锛?026-10-11锛夛細**姣忓抚 DComp 鎻愪氦涓€娆?*銆侰hromium 姣忓抚閮?Commit锛?    /// 鎴戜滑鍙湪寤虹獥鍙ｆ椂鎻愪氦杩囬偅涓€娆♀€斺€?闂竴涓嬪氨娌?鐨勫珜鐤戜箣涓€灏辨槸瀹冦€?    /// 锛坄--trailcommit` 寮€锛汚/B 鐢ㄣ€傦級
    /// </summary>
    public static bool TrailCommitEveryFrame;

    /// <summary>
    /// 寮€濮嬩竴鏉″鎵樺ⅷ杩硅建杩广€傛鍚庣郴缁熷悎鎴愬櫒浼氳嚜宸辨妸绗斿皷鍚庨潰鐨勮繖涓€灏忔
    /// 鐢诲嚭鏉ワ紝鑺傚璺熺潃鏄剧ず鍣ㄥ埛鏂拌蛋锛屼笉鍙楁垜浠繖涓€甯ф湁娌℃湁鐢诲畬褰卞搷銆?    /// 榛樿**鎴戜滑鐓ф棫鐢昏嚜宸辩殑绗旂敾**锛堜袱鏉￠兘鍦紝鍙栧苟闆嗭級锛沗--syswet` 鏃惰嚜缁樻箍澧?    /// 璁╀綅缁欑郴缁熻建杩癸紙瑙?<see cref="SystemWetPreferred"/>锛夈€?    /// 杩斿洖鍊?= 杩欐潯杞ㄨ抗鏄惁鐪熺殑璧锋潵浜嗭紙`--syswet` 闈犲畠鍐冲畾鏁笉鏁笉鐢伙級銆?    /// </summary>
    public bool BeginInkTrail(Color4 color, float radius)
    {
        _inkTrailRuntimeFailed = false;
        if (_inkTrail == null || !InkTrailEnabled) return false;
        // 涓婁竴鏉¤建杩硅繕鎸傝捣娌℃挙锛堟敹绗斿悗绛?Present锛夆啋 鍏堟挙锛屽埆鍙犱笂鏂拌建杩广€?        if (_trailEndPending) EndInkTrailNow();
        try
        {
            _trailRadius = MathF.Max(0.5f, radius);
            _inkTrail.StartNewTrail(color);
            _trailActive = true;
            _trailGeneration = 0;
            _trailPointsFed = 0;
            _trailPointsRemoved = 0;
            _trailTrimmedGeneration = 0;
            InkTrailDebug = $"StartNewTrail ok, color=({color.R:F2},{color.G:F2},{color.B:F2},{color.A:F2})";
        }
        catch (Exception ex)
        {
            _trailActive = false;
            _inkTrail = null;
            _inkTrailRuntimeFailed = true;
            InkTrailDebug = "StartNewTrail 寮傚父: " + ex.Message;
        }
        return _trailActive;
    }

    /// <summary>濮旀墭杞ㄨ抗鏈瑪杩愯鏃跺け璐ワ紱Engine 鎹鍒囨崲搴旂敤渚ф箍澧ㄣ€?/summary>
    public bool InkTrailRuntimeFailed => _inkTrailRuntimeFailed;

    /// <summary>
    /// 鍚姩涓€鏉″簲鐢ㄤ晶婀垮ⅷ銆傚畠浣跨敤鐙珛 DirectComposition surface锛岄伩寮€瀹屾暣鍐呭浜ゆ崲閾剧殑
    /// 鍒嗗潡/鐣岄潰閲嶇粯锛涜緭鍏ョ偣浠嶇敱 Engine 鐨勭幇鏈?WM_POINTER 绠＄嚎鎻愪緵銆?    /// </summary>
    public bool BeginFastWet(Color4 color, float radius)
    {
        if (!_fastWetReady) TryCreateFastWetResources(force: true);
        if (!FastWetFallbackEnabled || _fastWetSurface == null || !_fastWetReady) return false;
        try
        {
            if (_fastWetEndPending) EndFastWetNow();
            if (!_fastWetLastBounds.IsEmpty) ClearFastWetBounds(_fastWetLastBounds);
            _fastWetColor = color;
            _fastWetLastPointCount = 0;
            _fastWetLastCamX = ViewOffsetX;
            _fastWetLastCamY = ViewOffsetY;
            _fastWetLastBounds = RectF.Empty;
            _fastWetDrawing = true;
            _fastWetEndPending = false;
            return true;
        }
        catch (Exception ex)
        {
            InkTrailDebug = "FastWet 鍚姩澶辫触: " + ex.Message;
            return false;
        }
    }

    /// <summary>娓呯┖搴旂敤渚ф箍澧ㄨ瑙夛紝骞舵彁浜や竴娆?DComp銆?/summary>
    public void EndFastWet()
    {
        EndFastWetNow();
    }

    private void EndFastWetNow()
    {
        if (!_fastWetReady)
        {
            _fastWetDrawing = false;
            _fastWetEndPending = false;
            return;
        }
        try
        {
            if (!_fastWetLastBounds.IsEmpty) ClearFastWetBounds(_fastWetLastBounds);
            _dcomp2.Commit().CheckError();
        }
        catch { _fastWetReady = false; _fastWetDrawing = false; _fastWetEndPending = false; }
        _fastWetLastPointCount = 0;
        _fastWetLastCamX = float.NaN;
        _fastWetLastCamY = float.NaN;
        _fastWetLastBounds = RectF.Empty;
        _fastWetDrawing = false;
        _fastWetEndPending = false;
    }

    private bool _fastWetEndPending;

    public bool FastWetEndPending => _fastWetEndPending;

    public void EndFastWetDeferred()
    {
        if (_fastWetDrawing) _fastWetEndPending = true;
    }

    /// <summary>
    /// 鎶婃椿鍔ㄧ瑪鐨?*鐪熷疄宸叉敹鍒扮偣**鐢诲埌鐙珛婀垮ⅷ surface銆傚彧閲嶇敾鏈瑪鍖呭洿鐩掞紝
    /// 涓嶈Е纰颁富浜ゆ崲閾撅紱鍥犳 Win10 娌℃湁绯荤粺杞ㄨ抗鏃朵篃鏈変竴鏉′綆寮€閿€鐨勨€滃鎵樺紡鈥濇箍澧ㄨ矾寰勩€?    ///
    /// 鏇存柊鐭╁舰璇箟锛?026-10-13 淇紝鐪熸満鈥滃瘑闆嗘柇澧ㄢ€濈殑鏍瑰洜锛夛細`BeginDraw` 鎷垮埌鐨?    /// 涓存椂琛ㄩ潰**涓嶅惈鏃у唴瀹?*锛圡SDN锛氬簲鐢ㄥ繀椤绘妸鏇存柊鐭╁舰鍐呮瘡涓€涓儚绱犻兘閲嶇敾锛夛紝
    /// 鎵€浠ヤ笉鑳解€滄摝鎺夋暣鍧椼€佸彧鐢绘柊澧炵殑閭ｄ竴娈碘€濃€斺€斿啓寰楁參/瀛楀瘑鏃讹紝鐭╁舰閲屾洿鏃╃殑閭ｆ埅
    /// 婀垮ⅷ浼氳鎿︽帀鍙堟病閲嶇敾锛屽睆骞曚笂灏卞湪绗斿皷鍚庨潰鍑虹幇楂橀鏂彛銆?    /// 鐜板湪姣忔鏇存柊閮藉湪鐭╁舰鍐呮寜鈥?*鏁寸瑪 鈭?鐭╁舰**鈥濋噸鐢伙細鍝竴娈典几杩涚煩褰㈠氨鐢诲摢涓€娈点€?    /// </summary>
    public bool UpdateFastWet(Stroke stroke)
    {
        if (!_fastWetReady || stroke == null || stroke.Points.Count < 1) return false;
        bool cameraChanged = _fastWetLastPointCount > 0
                          && (ViewOffsetX != _fastWetLastCamX || ViewOffsetY != _fastWetLastCamY);
        if (cameraChanged && !_fastWetLastBounds.IsEmpty)
        {
            // A camera move invalidates the previous screen-space image. Clear it
            // before drawing the stroke at the new transform.
            ClearFastWetBounds(_fastWetLastBounds);
            _fastWetLastBounds = RectF.Empty;
        }

        // 鏈瑕侀噸鐢荤殑鐢诲竷鐭╁舰锛氱浉鏈哄彉浜?绗竴甯?鈫?鏁存潯锛涘惁鍒?鈫?鏂扮偣鍔犱笂涓€涓棫鐐?        //锛堟柊澧ㄥ彧鍙兘鍑虹幇鍦ㄨ繖閲岋級銆傜煩褰?*浠ュ**鐨勮〃闈㈠唴瀹规槸淇濈暀鐨勶紝涓嶅幓纰般€?        int start = cameraChanged ? 0 : Math.Max(0, _fastWetLastPointCount - 1);
        var segmentBounds = RectF.Empty;
        for (int i = start; i < stroke.Points.Count; i++)
            segmentBounds.Add(stroke.Points[i].X, stroke.Points[i].Y);
        if (segmentBounds.IsEmpty) return false;
        // 鐣欑櫧鎸?*鏈€绮楀**绠楋紙鍘嬪姏婊″帇鏃跺崐瀹?= 鏍囩О瀹斤級锛氭棭鍏堟寜 0.75脳 鏍囩О鐣欑櫧锛?        // 閲嶅帇鍐欑殑绮楄竟浼氳鏇存柊鐭╁舰鐨勮竟鐣岃鎺夆€斺€旈偅鏄彟涓€绉嶁€滄柇澧ㄢ€濄€?        float pad = stroke.MaxHalfWidth + 2f;
        var clip = CanvasRectToWindow(segmentBounds.Inflate(pad));
        // CanvasRectToWindow 杩斿洖鐨勬槸鏈獥鍙ｅ眬閮ㄥ潗鏍囷紱FastWet surface 涔熸槸灞€閮ㄥ潗鏍囥€?        clip.MinX = MathF.Max(0, clip.MinX);
        clip.MinY = MathF.Max(0, clip.MinY);
        clip.MaxX = MathF.Min(Width, clip.MaxX);
        clip.MaxY = MathF.Min(Height, clip.MaxY);
        if (clip.MaxX <= clip.MinX || clip.MaxY <= clip.MinY) return false;

        try
        {
            var rect = new Vortice.RawRect(
                Math.Max(0, (int)MathF.Floor(clip.MinX)),
                Math.Max(0, (int)MathF.Floor(clip.MinY)),
                Math.Min(Width, (int)MathF.Ceiling(clip.MaxX)),
                Math.Min(Height, (int)MathF.Ceiling(clip.MaxY)));
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

            var ctx = _fastWetSurface.BeginDraw<ID2D1DeviceContext>(rect, out var offset);
            using var brush = ctx.CreateSolidColorBrush(_fastWetColor, null);
            using var clear = ctx.CreateSolidColorBrush(Transparent, null);
            try
            {
                // BeginDraw exposes a temporary target for the update rectangle. Map
                // surface-space coordinates into that target: returned offset minus
                // requested rectangle origin (the offset is not the rectangle origin).
                ctx.Transform = Matrix3x2.CreateTranslation(
                    -OriginX + ViewOffsetX + offset.X - rect.Left,
                    -OriginY + ViewOffsetY + offset.Y - rect.Top);

                // 鏇存柊鐭╁舰鍦?*鐢诲竷鍧愭爣**閲岀殑閭ｄ竴鍧楋紙瑁佸壀浼氶殢涓婇潰鐨勫彉鎹㈡槧灏勫洖鐩爣鐭╁舰锛夈€?                var clipCanvas = new RectF
                {
                    MinX = rect.Left + OriginX - ViewOffsetX,
                    MinY = rect.Top + OriginY - ViewOffsetY,
                    MaxX = rect.Right + OriginX - ViewOffsetX,
                    MaxY = rect.Bottom + OriginY - ViewOffsetY,
                };
                var clipRaw = new Vortice.RawRectF(clipCanvas.MinX, clipCanvas.MinY,
                                                   clipCanvas.MaxX, clipCanvas.MaxY);
                ctx.PushAxisAlignedClip(clipRaw, AntialiasMode.Aliased);
                // `Clear()` 涓嶅彈瑁佸壀褰卞搷锛堜細鎶婃暣涓洰鏍囨竻鎺夛級锛屾墍浠ョ敤 Copy 娣峰悎鐨?                // 濉厖鏉モ€滄摝鈥濃€斺€斿拰涓绘覆鏌撻偅鏉¤矾鍚屼竴涓墜娉曪紙瑙?RasterizeTile 鐨勬敞閲婏級銆?                ctx.PrimitiveBlend = PrimitiveBlend.Copy;
                ctx.FillRectangle(clipRaw, clear);
                ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
                DrawFastWetStroke(ctx, brush, stroke, clipCanvas);
                ctx.PopAxisAlignedClip();
                ctx.Transform = Matrix3x2.Identity;
            }
            finally
            {
                _fastWetSurface.EndDraw().CheckError();
                ctx.Dispose();
            }
            _dcomp2.Commit().CheckError();
            _fastWetLastPointCount = stroke.Points.Count;
            _fastWetLastCamX = ViewOffsetX;
            _fastWetLastCamY = ViewOffsetY;
            if (_fastWetLastBounds.IsEmpty) _fastWetLastBounds = clip;
            else _fastWetLastBounds.Add(clip);
            return true;
        }
        catch (Exception ex)
        {
            InkTrailDebug = "FastWet 鏇存柊澶辫触: " + ex.Message;
            _fastWetReady = false;
            _fastWetDrawing = false;
            _fastWetEndPending = false;
            return false;
        }
    }

    /// <summary>
    /// 鍦?*鏈鏇存柊鐭╁舰鍐?*閲嶇敾婀垮ⅷ锛氭妸鏁存潯绗旇抗閲?*浼歌繘鐭╁舰**鐨勬鍜屽渾澶寸鐐瑰叏閮ㄧ敾涓€閬嶃€?    ///
    /// 涓轰粈涔堜笉鏄€滃彧鐢绘柊澧炵殑娈碘€濓紙2026-10-13 韪╄繃锛夛細DComp 鐨勬洿鏂扮煩褰笉淇濈暀鏃у唴瀹癸紝
    /// 鐭╁舰閲屼换浣曟病琚噸鐢荤殑鏃уⅷ閮戒細琚摝鎺夈€傚彧鏈夆€滅煩褰?鈫?瀹屾暣鍐呭鈥濊繖涓€鏉＄函鍑芥暟锛?    /// 鎵嶈兘淇濊瘉鐭╁舰杈圭晫涓や晶鎺ュ緱涓婏紙瀵嗛泦涔﹀啓/鎶樿繑鏃剁殑鏂ⅷ灏辨槸浠庨偅閲屾潵鐨勶級銆?    /// 瓒婄晫閮ㄥ垎鐢辫皟鐢ㄦ柟鎺ㄧ殑瑁佸壀鐭╁舰瑁佹帀锛涚矖绛涚暀鐧界敤鏁寸瑪鏈€绮楃殑鍗婂銆?    /// </summary>
    private void DrawFastWetStroke(ID2D1DeviceContext ctx, ID2D1SolidColorBrush brush,
                                   Stroke stroke, in RectF clipCanvas)
    {
        var pts = stroke.Points;
        if (pts.Count == 0) return;

        float WidthAt(float p)
            => stroke.HasPressure && PressureWidth.Enabled
                ? MathF.Max(1f, PressureWidth.HalfWidth(stroke.Width, p) * 2f)
                : MathF.Max(1f, stroke.Width);

        if (pts.Count == 1)
        {
            var p = new Vector2(pts[0].X, pts[0].Y);
            float r = WidthAt(pts[0].P) * 0.5f;
            if (CircleHits(p, r, clipCanvas)) ctx.FillEllipse(new Ellipse(p, r, r), brush);
            return;
        }

        float hitPad = stroke.MaxHalfWidth + 1f;   // 浠讳綍涓€娈电殑澧ㄩ兘涓嶄細浼稿緱姣斿畠鏇磋繙
        for (int i = 1; i < pts.Count; i++)
        {
            float ax = pts[i - 1].X, ay = pts[i - 1].Y;
            float bx = pts[i].X, by = pts[i].Y;
            var seg = new RectF
            {
                MinX = MathF.Min(ax, bx) - hitPad,
                MinY = MathF.Min(ay, by) - hitPad,
                MaxX = MathF.Max(ax, bx) + hitPad,
                MaxY = MathF.Max(ay, by) + hitPad,
            };
            if (!seg.Intersects(clipCanvas)) continue;
            float width = WidthAt((pts[i - 1].P + pts[i].P) * 0.5f);
            ctx.DrawLine(new Vector2(ax, ay), new Vector2(bx, by), brush, width, Gfx.Round);
        }

        // 涓ょ鐨勫渾澶存寜**绔偣鍘嬪姏**琛ワ紙娈垫弿杈圭敤鐨勬槸涓偣鍘嬪姏鐨勫搴︼紝绔ご鍗婂緞浼氬樊涓€鐐癸級锛?        // 鍙湁钀藉湪鐭╁舰閲岀殑閭ｄ釜绔偣鎵嶈琛モ€斺€斿彟涓€涓湪鐭╁舰澶栵紝涔嬪墠宸茬粡鐢诲浜嗐€?        var p0 = new Vector2(pts[0].X, pts[0].Y);
        float r0 = WidthAt(pts[0].P) * 0.5f;
        if (CircleHits(p0, r0, clipCanvas)) ctx.FillEllipse(new Ellipse(p0, r0, r0), brush);
        var pn = new Vector2(pts[^1].X, pts[^1].Y);
        float rn = WidthAt(pts[^1].P) * 0.5f;
        if (CircleHits(pn, rn, clipCanvas)) ctx.FillEllipse(new Ellipse(pn, rn, rn), brush);
    }

    /// <summary>鍦嗭紙鍦嗗績 + 鍗婂緞锛夊拰鐭╁舰鏈夋病鏈変氦鍙犫€斺€旀箍澧ㄧ矖绛涚敤銆?/summary>
    private static bool CircleHits(Vector2 c, float r, in RectF box)
        => c.X + r >= box.MinX && c.X - r <= box.MaxX
        && c.Y + r >= box.MinY && c.Y - r <= box.MaxY;

    private void ClearFastWetBounds(RectF bounds)
    {
        if (_fastWetSurface == null) return;
        int l = Math.Max(0, (int)MathF.Floor(bounds.MinX));
        int t = Math.Max(0, (int)MathF.Floor(bounds.MinY));
        int r = Math.Min(Width, (int)MathF.Ceiling(bounds.MaxX));
        int b = Math.Min(Height, (int)MathF.Ceiling(bounds.MaxY));
        if (r <= l || b <= t) return;
        var rect = new Vortice.RawRect(l, t, r, b);
        var ctx = _fastWetSurface.BeginDraw<ID2D1DeviceContext>(rect, out _);
        try { ctx.Clear(Transparent); }
        finally
        {
            _fastWetSurface.EndDraw().CheckError();
            ctx.Dispose();
        }
    }

    /// <summary>绯荤粺濮旀墭杞ㄨ抗涓嶅彲鐢ㄦ椂鐨勫簲鐢ㄤ晶婀垮ⅷ鏄惁宸叉湁鍙鐪熷疄鐐广€?/summary>
    public bool FastWetDrawing => _fastWetReady && _fastWetDrawing && _fastWetLastPointCount > 0;

    /// <summary>鎶婁竴涓噰鏍风偣浜ょ粰绯荤粺鍚堟垚鍣紙灞忓箷鍧愭爣锛屽唴閮ㄦ崲绠楀埌浜ゆ崲閾惧潗鏍囷級銆?/summary>
    public void AddInkTrailPoint(float screenX, float screenY, float radius)
    {
        if (!_trailActive || _inkTrail == null) return;
        try
        {
            var p = new DCompositionInkTrailPoint
            {
                X = screenX - OriginX,
                Y = screenY - OriginY,
                Radius = MathF.Max(0.5f, radius),
            };
            _trailGeneration = _inkTrail.AddTrailPoints(new[] { p }, 1);
            _trailPointsFed += 1;
            InkTrailPointsFed += 1;
            InkTrailDebug = $"AddTrailPoints ok gen={_trailGeneration} at({p.X:F0},{p.Y:F0}) r={p.Radius:F1}";
        }
        catch (Exception ex)
        {
            _trailActive = false;
            _inkTrail = null;
            _inkTrailRuntimeFailed = true;
            InkTrailNote = "杩愯鏃跺崟鐐瑰け璐?鈫?浣跨敤搴旂敤渚ф箍澧ㄦ浛韬?;
            InkTrailDebug = "AddTrailPoints 寮傚父: " + ex.Message;
        }
    }

    /// <summary>
    /// 鎶婁竴鎵圭湡瀹炵偣 + 涓€鎵?*棰勬祴鐐?*浜ょ粰绯荤粺鍚堟垚鍣紙閮界敤灞忓箷鍧愭爣锛夈€?    ///
    /// 棰勬祴鐐规槸"杩欎竴甯т箣鍚庣瑪灏栧ぇ姒傚湪鍝?锛堣 Prediction/InkPredictorLinear.cs锛夈€傜郴缁熶細鎶婅建杩圭敾鍒?    /// 棰勬祴鐐逛笂锛涗笅涓€鏉℃秷鎭潵浜嗐€佺湡瀹炵偣琛ヤ笂锛岄娴嬫鑷劧琚鐩栤€斺€旀墍浠ヨ繖鏉￠€氶亾**鐚滈敊浜嗕篃鍙槸
    /// 灞忓箷涓婄煭鏆傜殑涓€灏忔埅**锛屼笉浼氳繘鏂囨。銆傚井杞嚜宸辩瑪杩?璺熸墜"鐨勫叧閿氨鍦ㄨ繖涓甫棰勬祴鐨勯噸杞戒笂锛?    /// 鑰岀粦瀹氭垜浠湰鏉ュ氨鏈夛紙Vortice.DirectComposition 3.8.3锛夈€?    /// </summary>
    public void AddInkTrailPoints(Vector2[] real, int realCount, Vector2[] predicted, int predictedCount,
                                  float radius, float[] radii = null)
    {
        if (!_trailActive || _inkTrail == null || real == null || realCount <= 0) return;
        try
        {
            float r = MathF.Max(0.5f, radius);
            float R(int i) => radii != null && i >= 0 && i < radii.Length
                ? MathF.Max(0.5f, radii[i]) : r;
            var realPts = new DCompositionInkTrailPoint[realCount];
            for (int i = 0; i < realCount; i++)
                realPts[i] = new DCompositionInkTrailPoint
                {
                    X = real[i].X - OriginX,
                    Y = real[i].Y - OriginY,
                    // **閫愮偣鍗婂緞**锛堟湁鍘嬫劅鏃讹級锛氭箍澧ㄧ殑绮楃粏蹇呴』鍜屽共澧ㄥ悓涓€涓槧灏勶紝
                    // 涓嶇劧"姝ｅ湪鍐欑殑杩欎竴绗?鍜屾姮鎵嬩箣鍚庨偅涓€鏉＄矖缁嗕笉涓€鏍凤紙鏈€鎵庣溂鐨勪竴绉嶄笉涓€鑷达級銆?                    Radius = R(i),
                };

            var predPts = Array.Empty<DCompositionInkTrailPoint>();
            if (predicted != null && predictedCount > 0)
            {
                float pr = R(realCount - 1);         // 棰勬祴娈垫部鐢?*鏈€鍚庝竴鐐?*鐨勭矖缁?                predPts = new DCompositionInkTrailPoint[predictedCount];
                for (int i = 0; i < predictedCount; i++)
                    predPts[i] = new DCompositionInkTrailPoint
                    {
                        X = predicted[i].X - OriginX,
                        Y = predicted[i].Y - OriginY,
                        Radius = pr,
                    };
            }

            _trailGeneration = _inkTrail.AddTrailPointsWithPrediction(
                realPts, (uint)realCount, predPts, (uint)predPts.Length);
            _trailPointsFed += realCount;
            InkTrailPointsFed += realCount;
            InkTrailDebug = $"AddTrailPointsWithPrediction 鐪熷疄 {realCount} + 棰勬祴 {predPts.Length}锛実en={_trailGeneration}";
        }
        catch (Exception ex)
        {
            // 甯﹂娴嬬殑閲嶈浇涓囦竴涓嶈鏀寔锛岄€€鍥炰笉甯﹂娴嬬殑鑰佽矾鈥斺€斿畞鍙皯涓€鐐硅窡鎵嬶紝涔熶笉鑳戒涪婀垮ⅷ銆?            InkTrailDebug = "AddTrailPointsWithPrediction 寮傚父: " + ex.Message;
            try
            {
                var p = new DCompositionInkTrailPoint
                {
                    X = real[realCount - 1].X - OriginX,
                    Y = real[realCount - 1].Y - OriginY,
                    Radius = MathF.Max(0.5f, radius),
                };
                _trailGeneration = _inkTrail.AddTrailPoints(new[] { p }, 1);
                _trailPointsFed += 1;
                InkTrailPointsFed += 1;
            }
            catch
            {
                _trailActive = false;
                _inkTrail = null;
                _inkTrailRuntimeFailed = true;
                InkTrailNote = "杩愯鏃跺杺鐐瑰け璐?鈫?浣跨敤搴旂敤渚ф箍澧ㄦ浛韬?;
            }
        }
    }

    /// <summary>杩欐潯杞ㄨ抗鐜板湪鐪熺殑鍦ㄧ敾锛氭椿璺冧笖鑷冲皯鎴愬姛鍠傝繘鍘昏繃涓€涓偣銆?/summary>
    public bool TrailDrawing => _trailActive && _trailPointsFed > 0;

    /// <summary>鏀剁瑪鐨勮建杩规挙闄よ繕鎸傜潃锛堢瓑涓嬩竴甯?Present 鍚庢墽琛岋級鈥斺€斾富寰幆鐢ㄥ畠鍏滃簳瑕佷竴甯с€?/summary>
    public bool TrailEndPending => _trailEndPending;

    /// <summary>鏀剁瑪锛氱珛鍗虫挙杞ㄨ抗锛堣嚜妫€/娓呯悊璺緞鐢紱姝ｅ父鏀剁瑪璧?<see cref="EndInkTrailDeferred"/>锛夈€?/summary>
    public void EndInkTrail() => EndInkTrailNow();

    /// <summary>
    /// 鏀剁瑪锛?*寤跺悗鍒拌繖涓€甯?Present 涔嬪悗**鍐嶆挙锛夛細`--syswet` 涓嬫箍澧ㄥ彧鏈夌郴缁熻建杩硅繖涓€浠斤紝
    /// 蹇呴』鍏堣"骞插ⅷ閭ｄ竴甯?鎻愪氦鍑哄幓鍐嶆挙锛屽惁鍒欎細鍑虹幇"鏁寸瑪娑堝け涓€甯у啀鍑虹幇"鐨勯棯銆?    /// </summary>
    public void EndInkTrailDeferred()
    {
        if (!_trailActive) return;
        _trailEndPending = true;
    }

    private void EndInkTrailNow()
    {
        _trailEndPending = false;
        if (!_trailActive || _inkTrail == null) { _trailActive = false; return; }
        _trailActive = false;
        try { if (_trailGeneration != 0) _inkTrail.RemoveTrailPoints(_trailGeneration); }
        catch { }
        // 鏀剁瑪鎾よ建杩瑰悓鏍风畻"缁撹处"锛堣瘖鏂彛寰勶細鍠?= 缁撹处 + 鍦ㄥ満锛夈€?        int outstanding = _trailPointsFed - _trailPointsRemoved;
        if (outstanding > 0) { InkTrailPointsRemoved += outstanding; _trailPointsRemoved = _trailPointsFed; }
    }

    /// <summary>
    /// 杞ㄨ抗"缁撹处"锛氭垜浠?*鑷繁宸茬粡鎶婅繖涓€绗旂敾涓婁簡灞?*锛屽氨鎶婅建杩归噷鍚屾牱杩欎竴娈靛垹鎺?    /// 锛圕hromium 鐨勬瘡甯у崗璁細鐢?RemoveTrailPoints 鎶?宸蹭笂灞忕殑鐐?娓呭嚭杞ㄨ抗锛夈€?    ///
    /// 涓轰粈涔堝繀椤诲垹锛?026-10-11 鐪熺瑪瀹炴祴锛夛細绯荤粺鐨勫鎵樿建杩?*鍗曟潯涓婇檺 128 涓偣**
    /// 锛圕hromium `kMaximumNumberOfDelegatedInkPoints = 128`锛屾敞閲婂啓鏄?鍜?OS 鍚堟垚鍣?    /// 鍐呴儴琛屼负涓€鑷?锛夈€傚彧鍠備笉鍒?鈫?鍐欓暱绗旂敾瓒呰繃 128 鐐瑰悗鏃х偣琚郴缁熸摝鎺夛紝鐥囩姸灏辨槸
    /// "鍏堝嚭澧ㄣ€佷腑閫斿紑濮嬪彉閫忔槑銆佹姮绗旀墠琛ュ叏"銆傚垹鍒?鏈抚鍠傚埌鐨勬渶鍚庝竴浠?涔嬪悗锛?    /// 杞ㄨ抗閲屾案杩滃彧鍓?*杩樻病涓婂睆鐨勯偅涓€灏忔埅**锛堣繖姝ｆ槸杩欐潯閫氶亾鐨勮璁＄敤閫旓級銆?    /// 鈿?鍙湪鑷粯杩囨椂璋冪敤锛沗--syswet` / 鎶戝埗鑷粯鏃朵笉鑳藉垹锛堥偅鏃舵箍澧ㄥ彧鏈夎建杩硅繖涓€浠斤級銆?    /// </summary>
    private void TrimInkTrailAsDrawn()
    {
        if (!_trailActive || _inkTrail == null) return;
        if (_trailGeneration == 0 || _trailGeneration == _trailTrimmedGeneration) return;
        try
        {
            _inkTrail.RemoveTrailPoints(_trailGeneration);
            _trailTrimmedGeneration = _trailGeneration;
            int outstanding = _trailPointsFed - _trailPointsRemoved;
            if (outstanding > 0)
            {
                InkTrailPointsRemoved += outstanding;
                _trailPointsRemoved = _trailPointsFed;
            }
        }
        catch { _trailActive = false; }   // 鍒犵偣澶辫触鎸?杩欐潯杞ㄨ抗搴熶簡"澶勭悊锛堝拰鍠傜偣澶辫触涓€鑷达級
    }

    private static readonly Color4 Transparent = new(0f, 0f, 0f, 0f);

    public string Create(IntPtr hInstance, string className, int x, int y, int w, int h)
    {
        OriginX = x; OriginY = y; Width = w; Height = h;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP;

        Hwnd = Native.CreateWindowEx(exStyle, className, "InkTeach",
            0x80000000L /*WS_POPUP*/, x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (Hwnd == IntPtr.Zero)
            return $"CreateWindowEx failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";

        // **鍦ㄦ湰绐楀彛鍐呭叧鎺?鎸変綇涓嶅姩 = 鍙抽敭"杩欐潯绯荤粺鎵嬪娍**锛堢悊鐢辫 Native 閲岄偅涓€娈垫敞閲婏級锛?        // 鎴戜滑瑕佺敤"绗旀寜浣忎笉鍔?褰撳仠椤挎垚鍨嬬殑淇″彿锛屽拰绯荤粺閭ｆ潯鍚屾鎵嬪娍鐩存帴鎾炶溅銆?        // 姣忎釜瑕嗙洊灞傜獥鍙ｉ兘瑕佸叧涓€娆♀€斺€旀墜鍔挎槸鎸夌獥鍙ｇ畻鐨勶紝鍙叧涓诲睆閭ｄ竴涓紝
        // 鍓睆涓婂啓瀛楃収鏍峰脊鍙抽敭鐜€?        PressAndHoldDisabled = Native.DisableSystemPressAndHold(Hwnd);

        Dpi = Native.GetDpiForWindow(Hwnd);
        if (Dpi == 0) Dpi = 96;
        // 鐪熷疄鍒锋柊鐜囷細120Hz 灞忎笂寤舵椂鎶ュ憡瑕佹姌鍚堢湡瀹炲懆鏈燂紙鎷夸笉鍒伴€€鍥?60Hz锛夈€?        // 绐楀彛鍒涘缓鏃惰涓€娆★紱鏀逛簡鏄剧ず妯″紡閲嶅惎杞欢鍗冲彲锛堣繍琛屼腑鏀圭殑鏋佸皯锛夈€?        int refreshHz = Native.GetCurrentRefreshHz();
        RefreshPeriodMs = refreshHz >= 30 ? 1000.0 / refreshHz : 1000.0 / 60.0;

        try
        {
            CreateDeviceResources();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        return null;
    }

    /// <summary>
    /// 鎸夐渶娉ㄥ唽 / 娉ㄩ攢**鍘熷杈撳叆**锛坄--rawinput` / `--rawprobe`锛夈€?    ///
    /// 鈿?**鍙兘鍦?姝ｅ湪鍐欎竴绗?鏈熼棿娉ㄥ唽锛屾姮绗斿繀椤绘敞閿€**鈥斺€旇繖鏉℃槸韪╁嚭鏉ョ殑锛?    /// `RIDEV_INPUTSINK` 浼氳绐楀彛鍦?*绌洪棽鏃朵篃鎸佺画鏀跺埌榧犳爣鐨勮疆璇㈢┖鎶?*
    /// 锛堥紶鏍囦笉鍔ㄦ椂纭欢鐓ф牱鎸夎疆璇㈢巼鍙?闆朵綅绉?鐨勬姤锛屾瘡绉掍笂鐧炬潯锛夈€?    /// 娑堟伅涓€澶氾紝涓诲惊鐜氨琚《鎴?姣忓抚閮芥覆鏌?锛氬疄娴?*绌洪棽 46 fps銆佸崟鏍?26%**
    /// 锛堢敤 `--norawinput` 瀵圭収鏄?0 fps / 0.4%锛夈€傛暀瀹ゆ満/绗旇鏈笂杩欏氨鏄鎵囩媯杞€?    /// 鎵€浠ユ寜绗旀敞鍐屻€佹姮绗旀敞閿€锛岀┖闂叉椂涓€鏉￠兘涓嶆敹銆?    /// </summary>
    public bool SetRawInput(bool on)
    {
        if (Hwnd == IntPtr.Zero) return false;
        var dev = new Native.RAWINPUTDEVICE
        {
            usUsagePage = 0x01,        // Generic Desktop
            usUsage = 0x02,            // Mouse锛堟墜鍐欐澘鍦ㄩ紶鏍囨ā寮忎笅涔熸姤杩欎釜锛?            dwFlags = on ? Native.RIDEV_INPUTSINK : Native.RIDEV_REMOVE,
            hwndTarget = on ? Hwnd : IntPtr.Zero,
        };
        return Native.RegisterRawInputDevices(new[] { dev }, 1,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.RAWINPUTDEVICE>());
    }

    private void CreateDeviceResources()
    {
        var desc = new SwapChainDescription1(
            (uint)Width, (uint)Height,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            (uint)Math.Max(2, BufferCount),
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            Vortice.DXGI.AlphaMode.Premultiplied,
            LatencyWaitEnabled ? SwapChainFlags.FrameLatencyWaitableObject : SwapChainFlags.None)
        {
            SampleDescription = new SampleDescription(1, 0),
        };

        _swapChain = Gfx.Factory.CreateSwapChainForComposition(Gfx.Device, desc, null);
        try { _swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>(); } catch { _swapChain3 = null; }

        if (LatencyWaitEnabled)
        {
            _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (_swapChain2 != null)
            {
                try
                {
                    _swapChain2.MaximumFrameLatency = 1;
                    _latencyWait = _swapChain2.FrameLatencyWaitableObject;
                }
                catch { _latencyWait = IntPtr.Zero; }
            }
            Console.WriteLine($"甯у欢杩熺瓑寰呭璞? {(_latencyWait != IntPtr.Zero ? "宸插惎鐢? : "涓嶅彲鐢紝閫€鍥炲瀭鐩村悓姝ラ樆濉?)}");
        }
        Mem.Stage($"4. 鍒涘缓浜ゆ崲閾句箣鍚庯紙{Width}x{Height}锛?);

        // Create one IDCompositionDevice2 from the D2D device, then obtain the
        // v1 face from the same COM object. Surface content and the existing
        // visual tree must belong to one composition device.
        try
        {
            _dcomp = DComp.DCompositionCreateDevice2<IDCompositionDevice>(Gfx.D2DDevice);
            _dcomp2 = _dcomp.QueryInterfaceOrNull<IDCompositionDevice2>();
        }
        catch
        {
            _dcomp2 = null;
            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(
                Gfx.Device.QueryInterface<IDXGIDevice>());
        }
        _dcomp.CreateTargetForHwnd(Hwnd, true, out _target).CheckError();
        // 鏍硅瑙?*涓嶈鍐呭**锛屼氦鎹㈤摼鏀惧湪瀛愯瑙夐噷 鈥斺€?杩欐牱婀垮ⅷ鎵嶆湁鍦版柟鍙犲湪瀹冧笂闈€?        // 2026-10-10 璇曢獙C 鎸?Chromium 鐨勭粨鏋勬敼锛?026-10-13 淇灞傚簭锛堣涓嬩竴鏉℃敞閲婏級銆?        //
        // 鈿?`AddVisual` 鐨?insertAbove 璇箟**鍜岀洿瑙夌浉鍙?*锛圡SDN 鍘熸枃锛歳eferenceVisual 涓?        //   NULL 鏃讹紝"If insertAbove is TRUE, the new child visual is above no sibling,
        //   therefore it is rendered below all of its siblings"锛夛細
        //   路 insertAbove:false 锛?鍙傜収 null 鈫?鍔犲埌**鎵€鏈夊厔寮熶箣涓?*锛?        //   路 insertAbove:true  锛?鍙傜収 null 鈫?鍔犲埌**鎵€鏈夊厔寮熶箣涓?*銆?        //   婀垮ⅷ瑙嗚锛堢郴缁熻建杩?/ FastWet 鏇胯韩锛夊繀椤诲彔鍦ㄤ氦鎹㈤摼**涓婇潰**鈥斺€斾互鍓嶈繖閲岀敤浜?        //   true锛?鍘嬪湪鏈€涓嬮潰锛夛紝寮€鐫€涓嶉€忔槑鐧芥澘鏃舵寜浣忎功鍐欏睆骞曚笂涓€涓箍澧ㄥ儚绱犻兘娌℃湁
        //   锛?026-10-13 瀹炴祴锛氱櫧鏉?+ 绯荤粺杞ㄨ抗 0 鍍忕礌銆佺櫧鏉?+ FastWet 0 鍍忕礌锛屾闈㈤兘姝ｅ父锛夛紝
        //   鍥犱负鐧芥澘鐨勫簳鑹叉妸涓嬮潰鐨勪竴鍒囬兘鐩栦綇浜嗭紱鏉炬墜骞插ⅷ杩涙枃妗ｅ悗鎵嶉噸鏂板彲瑙併€?        _visual = _dcomp.CreateVisual();
        _swapVisual = _dcomp.CreateVisual();
        _swapVisual.SetContent(_swapChain).CheckError();
        _visual.AddVisual(_swapVisual, false, null).CheckError();
        _target.SetRoot(_visual).CheckError();
        _dcomp.Commit().CheckError();

        // 寰蒋鐨?濮旀墭澧ㄨ抗杞ㄨ抗"锛氭湰鎰忔槸鎶婃鍦ㄥ啓鐨勯偅涓€绗斾氦缁欑郴缁熷悎鎴愬櫒鍘荤敾锛?        // 搴旂敤鑷繁鐨勫欢杩熷氨涓嶅啀褰卞搷绗斿皷璺熸墜绋嬪害銆傝繖涓€椤硅鍗曠嫭鎺㈡祴锛?        // 鍥犱负瀹冩槸杈冩柊鐨勬帴鍙ｏ紝鑰佺郴缁熶笂娌℃湁銆?        // 鈿?2026-10-05 鐪熺瑪瀹炴祴锛氭湰鏈虹郴缁熶笉娓叉煋瀹冿紙璋冪敤鍏ㄦ垚鍔熴€佸睆骞曟棤澧級鈥斺€?        // 淇濈暀鎺㈡祴涓庡杺鐐逛綔鎹㈡満鍣ㄩ獙璇侊紱瑙?InkTrailEnabled 鐨勬敞閲娿€?        // 2026-10-10 璇曢獙C锛氱収 Chromium 鐨勬帴娉曡ˉ涓?鎸傝瑙夋爲"杩欎竴姝?鈫?**绯荤粺鐪熺殑鐢讳簡**锛坵etinktest PASS锛夈€?        // 2026-10-11锛氭敼"鎸夌獥鍙ｆ帰娴?鈥斺€斾互鍓嶇敤闈欐€?`InkTrailNote == "鏈皾璇?` 褰撻棬妲涳紝
        // 澶氬睆鏃剁浜屼釜绐楀彛鏁翠釜璺宠繃 鈫?鍓睆姘歌繙娌℃湁杞ㄨ抗銆?        if (!_trailProbed)
        {
            _trailProbed = true;
            try
            {
                _inkTrailDevice = _dcomp.QueryInterfaceOrNull<IDCompositionInkTrailDevice>();
                if (_inkTrailDevice != null)
                {
                    _inkTrail = _inkTrailDevice.CreateDelegatedInkTrailForSwapChain(_swapChain);
                    InkTrailAvailable = _inkTrail != null;
                    InkTrailNote = InkTrailAvailable ? "鍙敤" : "鎺ュ彛鍙敤浣嗗垱寤鸿建杩瑰け璐?;
                    if (InkTrailAvailable)
                    {
                        // 鈽?鍏抽敭涓€姝ワ紙Chromium 鐨?InkTrail 灏辨槸杩欎箞鎸傜殑锛屾垜浠互鍓嶆紡浜嗭級锛?                        //   杞ㄨ抗瀵硅薄蹇呴』**浣滀负瑙嗚鍐呭鏀捐繘瑙嗚鏍?*銆佸彔鍦ㄤ氦鎹㈤摼涓婇潰锛岀郴缁熸墠浼氱敾銆?                        //   鍙垱寤轰笉鎸傛爲 鈫?璋冪敤鍏ㄦ垚鍔熴€乬en 閫掑锛屼絾灞忓箷涓婃案杩滄病鏈夊ⅷ銆?                        try
                        {
                            _inkVisual = _dcomp.CreateVisual();
                            _inkVisual.SetContent(_inkTrail).CheckError();
                            // insertAbove:false + 鍙傜収 null = 鍔犲埌鎵€鏈夊厔寮?*涔嬩笂**
                            //锛堣涔夎鏄庤 CreateDeviceResources 閲?AddVisual 閭ｄ竴娈碉級銆?                            _visual.AddVisual(_inkVisual, false, null).CheckError();
                            _dcomp.Commit().CheckError();
                            InkTrailNote += "锛堝凡鎸傝瑙夋爲锛?;
                        }
                        catch (Exception layEx)
                        {
                            InkTrailAvailable = false;
                            InkTrailNote += "锛堟寕瑙嗚鏍戝け璐? " + layEx.Message + "锛?;
                        }
                    }
                }
                else
                {
                    InkTrailAvailable = false;
                    InkTrailNote = "鍚堟垚璁惧涓嶆敮鎸?IDCompositionInkTrailDevice";
                }
            }
            catch (Exception ex)
            {
                InkTrailAvailable = false;
                InkTrailNote = "鎺㈡祴寮傚父: " + ex.Message;
            }
        }
        Mem.Stage("5. 鎺ュ叆 DirectComposition 涔嬪悗");

        // 澶氱嚎绋嬪厜鏍咃紙瀹樻柟鏂囨。锛氭妸璺緞鍑犱綍鐨勬覆鏌撴憡鍒板涓€昏緫鏍镐笂锛夈€?        // 褰撳勾鍋氳繃 A/B锛?-mtraster锛夛紝缁撹鏄敹鐩婅惤鍦ㄥ櫔澹伴噷锛屼簬鏄垹鎺夊紑鍏炽€佷繚鎸侀粯璁ゅ叧銆?        _ctx = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx1 = _ctx.QueryInterfaceOrNull<ID2D1DeviceContext1>();
        // **鍘熺敓澧ㄨ抗**锛堝帇鎰?鈫?绮楃粏锛夛細`CreateInk` / `DrawInk` 鍦?`ID2D1DeviceContext2` 涓?        // 锛圖2D 1.3锛學in10+锛夈€傛嬁涓嶅埌灏卞綋浣滆繖鍙版満鍣ㄤ笉鏀寔锛屽帇鎰熺瑪杩归€€鍥炵瓑瀹芥弿杈?        // 锛堝惎鍔ㄦ棩蹇楅噷浼氭墦鍗颁竴琛岋紝鍜?濮旀墭澧ㄨ抗杞ㄨ抗: 鍙敤/涓嶅彲鐢?涓€涓牱瀛愶級銆?        try
        {
            _ctx2 = _ctx.QueryInterfaceOrNull<ID2D1DeviceContext2>();
            if (_ctx2 != null)
            {
                _inkStyle = _ctx2.CreateInkStyle(new InkStyleProperties
                {
                    NibShape = InkNibShape.Round,
                    // **蹇呴』鏄惧紡缁欏崟浣嶉樀**锛氱粨鏋勪綋鐨勯粯璁ゅ€奸噷 `NibTransform` 鏄?*鍏ㄩ浂鐭╅樀**
                    // 锛堜笉鏄崟浣嶉樀锛夛紝鑰岄浂鐭╅樀 = 绗斿皷琚帇鎴愰浂灏哄 鈫?                    // 琛ㄧ幇鏄?CreateInk / AddSegments / DrawInk 鍏ㄩ儴鎴愬姛锛屽睆骞曚笂涓€涓儚绱犻兘娌℃湁"銆?                    // 杩欎釜鍧?2026-09-20 瀹炴祴韪╄繃涓€娆★紙`--pressuretest` 鏁板嚭鍏ㄥ睆 0 鍍忕礌锛夈€?                    NibTransform = Matrix3x2.Identity,
                });
                InkAvailable = true;
                InkNote = "鍙敤锛圖2D 鍘熺敓澧ㄨ抗 ID2D1Ink锛氬帇鎰熸寜閫愮偣鍗婂緞鏀圭矖缁嗭級";
            }
            else
            {
                InkAvailable = false;
                InkNote = "涓嶅彲鐢紙鎷夸笉鍒?ID2D1DeviceContext2锛夆啋 鍘嬫劅绗旇抗閫€鍥炵瓑瀹芥弿杈?;
            }
        }
        catch (Exception ex)
        {
            InkAvailable = false;
            InkNote = "涓嶅彲鐢紙" + ex.Message + "锛夆啋 鍘嬫劅绗旇抗閫€鍥炵瓑瀹芥弿杈?;
        }
        _ctx.SetDpi(96f, 96f);
        _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        _ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
        using (var surface = backBuffer.QueryInterface<IDXGISurface>())
        {
            _backBuffer = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
        }

        // 鍐呭灞傦細鐢诲竷绌洪棿鐨勫垎鍧楀厜鏍呯紦瀛樸€?*涓嶅湪杩欓噷鍒嗛厤浣嶅浘**鈥斺€斿潡鎸夐渶鍒涘缓锛?        // 鎬婚噺鐢?CanvasTileCache.BudgetTiles 灏侀《锛堥粯璁?48MB锛夈€?        _tiles = new CanvasTileCache();
        _tiles.Attach(_ctx);

        _scratch = _ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f), null);
        _transparentBrush = _ctx.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0f), null);
        _boardBrush = _ctx.CreateSolidColorBrush(Transparent, null);
        // Do not allocate a full-screen fallback surface on a machine where the
        // native delegated trail is already available. It is created lazily only
        // when the native route is absent or fails.
        if (_inkTrail == null || !InkTrailEnabled) TryCreateFastWetResources();
        Mem.Stage("6. 鍒涘缓绂诲睆鍐呭灞備箣鍚?);
    }

    private void TryCreateFastWetResources(bool force = false)
    {
        _fastWetReady = false;
        if (!FastWetFallbackEnabled || _dcomp2 == null || _ctx == null) return;
        if (!force && _inkTrail != null && InkTrailEnabled) return;
        try
        {
            // VirtualSurface allocates tiles only where ink is actually written; a
            // full-screen 4K surface would otherwise reserve another large buffer per monitor.
            _fastWetSurface = _dcomp2.CreateVirtualSurface((uint)Width, (uint)Height,
                Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied);
            _fastWetVisual = _dcomp.CreateVisual();
            _fastWetVisual.SetContent(_fastWetSurface).CheckError();
            // insertAbove:false + 鍙傜収 null = 鍔犲埌鎵€鏈夊厔寮?*涔嬩笂**锛堟箍澧ㄥ繀椤绘诞鍦?            // 浜ゆ崲閾句笂闈紱璇箟璇存槑瑙?CreateDeviceResources 閲?AddVisual 閭ｄ竴娈碉級銆?            _visual.AddVisual(_fastWetVisual, false, null).CheckError();
            _dcomp2.Commit().CheckError();
            _fastWetReady = true;
            InkTrailNote += "锛涘簲鐢ㄤ晶婀垮ⅷ灞傚彲鐢?;
        }
        catch (Exception ex)
        {
            _fastWetReady = false;
        _fastWetSurface?.Dispose(); _fastWetSurface = null;
        _fastWetVisual?.Dispose(); _fastWetVisual = null;
            InkTrailNote += "锛涘簲鐢ㄤ晶婀垮ⅷ灞備笉鍙敤: " + ex.Message;
        }
    }

    /// <summary>
    /// 鐧芥澘妯″紡涓嬬敾"椤电晫绾?锛氫竴灞忎竴椤碉紝鐣岀嚎钀藉湪鐢诲竷鍧愭爣鐨勬暣鏁板睆浣嶇疆銆?    ///
    /// 涓轰粈涔堟妸瀹冪敾鎴?*鐢诲竷鍐呭**鑰屼笉鏄瘡甯у彔涓€灞傦細鐣岀嚎鍦ㄥ浘涓婄殑浣嶇疆鏄浐瀹氱殑
    /// 锛堜笉闅忕浉鏈哄姩锛夛紝鎵€浠ヨ兘璺熺櫧鏉垮簳鑹蹭竴璧风儤杩涘垎鍧楃紦瀛?鈥斺€?婊氬姩鍜岀炕椤甸兘涓嶉澶栬姳閽便€?    /// 棰滆壊鎸夋澘鑹叉槑鏆楁寫锛氭祬鏉跨敾娣￠粦绾裤€佹繁鏉跨敾娣＄櫧绾匡紝鍙眰"鐪嬪緱鍑烘湁涓垎鐣?锛屼笉鎶㈡澘涔︺€?    /// </summary>
    /// <summary>
    /// **鐧芥澘搴曠汗锛堟柟鏍?/ 妯嚎锛?*銆傚弬鑰?InkClass 鐨?`MW_WhiteboardPattern.cs`锛?    /// 涓夋。锛堟棤/鏂规牸/妯嚎锛夈€佺嚎鏋佺粏銆佺嚎鑹查殢鏉块潰鏄庢殫鑷€傚簲銆?*涓嶆槸绗旇抗**
    /// 锛堟鐨摝涓嶆帀銆佹挙閿€涓嶆秹鍙娿€侀€夋嫨閫変笉涓級銆?    ///
    /// 鎴戜滑鐨勫疄鐜版瘮瀹冪渷涓€妗ｏ細瀹冩槸缁?`Border` 鎸備竴涓钩閾虹敾鍒凤紙鍥哄畾灞忓箷涓娿€佷笉闅忔粴鍔ㄥ姩锛夛紝
    /// 鎴戜滑鏄?*鐢昏繘鍒嗗潡缂撳瓨**鈥斺€斿簳绾瑰湪**鐢诲竷鍧愭爣**閲岋紝鎵€浠ワ細
    ///   路 婊氬姩鏃跺簳绾硅窡鐫€鏉夸功涓€璧疯蛋锛堝啓鍦ㄦí绾夸笂鐨勫瓧姘歌繙鍦ㄩ偅鏉＄嚎涓婏紝杩欐墠鏄?绾?鐨勮涔夛級锛?    ///   路 婊氬姩/缈婚〉/鍐欏瓧閮戒笉閲嶇敾搴曠汗锛堝潡閲屽凡缁忕儤濂戒簡锛夛紱
    ///   路 鎹㈠簳绾规垨鎹㈡澘鑹叉墠鏁村眰閲嶉摵涓€娆★紙鍜屾崲鏉胯壊鏈潵灏辨槸鍚屼竴浠朵簨锛夈€?    ///
    /// 绾垮 1 鐢诲竷鍍忕礌 = 200% 灞忎笂鐨?0.5 閫昏緫鍍忕礌锛屽拰 InkClass 鐨?0.5px 鏄悓涓€涓鎰熴€?    /// </summary>
    private void DrawBoardPattern(InkEngine app, RectF canvas)
    {
        int kind = app.BoardPattern;
        if (kind == 0) return;
        float step = app.BoardPatternStepPx;
        if (step < 4f) return;

        var c = app.BoardColor;
        float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        // 娴呮澘锛氱伆 60%锛圛nkClass 鐨?0x99,0x99,0x99,0x99锛夛紱娣辨澘锛氱櫧 20%
        var line = lum > 0.5f
            ? new Color4(0.60f, 0.60f, 0.60f, 0.60f)
            : new Color4(1f, 1f, 1f, 0.20f);
        var brush = Brush(line);

        // 瀵归綈鍒?绗竴椤电殑宸︿笂瑙?锛? 铏氭嫙妗岄潰宸︿笂瑙掞級锛氬簳绾逛笌椤电晫绾垮叡鐢ㄥ悓涓€涓敋鐐癸紝
        // 缈婚〉涔嬪悗妯嚎杩樺湪鍘熸潵鐨勯珮搴︿笂锛屼笉浼氳窡椤佃竟鐣岄敊寮€銆?        float left = app.PageLeftCanvas, top = app.PageTopCanvas;

        float firstY = MathF.Floor((canvas.MinY - top) / step) * step + top;
        for (float y = firstY; y <= canvas.MaxY + 0.5f; y += step)
        {
            if (y < canvas.MinY - 0.5f) continue;
            _ctx.DrawLine(new System.Numerics.Vector2(canvas.MinX, y),
                          new System.Numerics.Vector2(canvas.MaxX, y), brush, 1f);
        }

        if (kind != 1) return;                       // 妯嚎鍙敾妯殑锛屾柟鏍艰繕瑕佺敾绔栫殑
        float firstX = MathF.Floor((canvas.MinX - left) / step) * step + left;
        for (float x = firstX; x <= canvas.MaxX + 0.5f; x += step)
        {
            if (x < canvas.MinX - 0.5f) continue;
            _ctx.DrawLine(new System.Numerics.Vector2(x, canvas.MinY),
                          new System.Numerics.Vector2(x, canvas.MaxY), brush, 1f);
        }
    }

    private void DrawPageLines(InkEngine app, RectF canvas)
    {
        float h = app.PageHeightCanvas;
        if (h < 50f) return;
        float top = app.PageTopCanvas;

        float first = MathF.Floor((canvas.MinY - top) / h) * h + top;
        var c = app.BoardColor;
        float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        var line = lum > 0.5f
            ? new Color4(0f, 0f, 0f, 0.10f)
            : new Color4(1f, 1f, 1f, 0.14f);

        for (float y = first; y <= canvas.MaxY + 0.5f; y += h)
        {
            if (y < canvas.MinY - 0.5f) continue;
            _ctx.DrawLine(new System.Numerics.Vector2(canvas.MinX, y),
                          new System.Numerics.Vector2(canvas.MaxX, y), Brush(line), 1f);
        }
    }

    /// <summary>
    /// 鍙栧綋鍓嶈鐢ㄧ殑搴曡壊鐢诲埛銆?*涓夊眰**锛?026-10-09"涓ゅ紶绾?妯″瀷锛夛細
    ///   路 鐧芥澘寮€鐫€ 鈫?鏉胯壊锛堝彲璋冧笉閫忔槑搴︼級鈥斺€旂櫧鏉跨敾鍦ㄦ枃妗?*涓婇潰**锛堥〉鍥句細璁╀綅锛岃 SyncDocPages锛夛紱
    ///   路 鐧芥澘鍏崇潃銆佹枃妗ｅ紑鐫€銆佷笉鍦ㄧ┛閫?鈫?**鏂囨。鐨?绾?**锛堢櫧搴曪細椤电紳/椤佃竟涓嶉湶妗岄潰锛夛紱
    ///   路 鍏朵綑锛堥€忔槑鎵规敞锛夆啋 鍏ㄩ€忔槑銆?    /// 搴曡壊鍙樹簡鎵嶉噸寤虹敾鍒凤紝姝ｅ父姣忓抚涓嶅垎閰嶃€?    /// </summary>
    private ID2D1SolidColorBrush BoardBrush(InkEngine app)
    {
        // 鐧芥澘鍙互鏁翠綋璋?涓嶉€忔槑搴?锛堢敤鎴?2026-09-17锛夛細搴曡壊杩欎竴灞傚崐閫忔槑锛?        // 涓嬮潰鐨勯鐩殣绾﹂€忓嚭鏉ワ紱**绗旇抗涓嶅彈褰卞搷**锛堝畠鐢诲湪搴曡壊涓婇潰銆佹湰韬笉閫忔槑锛夈€?        var want = app.BoardOn
            ? new Color4(app.BoardColor.R, app.BoardColor.G, app.BoardColor.B,
                         app.BoardColor.A * app.BoardOpacity)
            : (app.DocView.IsOpen && !app.PassThrough ? app.DocPaperColor : Transparent);
        if (want.R != _boardBrushColor.R || want.G != _boardBrushColor.G
            || want.B != _boardBrushColor.B || want.A != _boardBrushColor.A)
        {
            _boardBrush.Color = want;
            _boardBrushColor = want;
        }
        return _boardBrush;
    }

    private ID2D1SolidColorBrush Brush(Color4 c)
    {
        uint key = ((uint)(Math.Clamp(c.R, 0, 1) * 255) << 24)
                 | ((uint)(Math.Clamp(c.G, 0, 1) * 255) << 16)
                 | ((uint)(Math.Clamp(c.B, 0, 1) * 255) << 8)
                 | (uint)(Math.Clamp(c.A, 0, 1) * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = _ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    // ------------------------------------------------------------------
    //  Content layer
    // ------------------------------------------------------------------

    /// <summary>
    /// 鏂囨。椤靛眰锛氫笌杩欏潡鐢诲竷鐩镐氦鐨勯偅浜涢〉銆?    ///   路 宸茬敓鎴?鈫?鐢讳綅鍥撅紙椤哄甫鎶?CPU 閭ｅ崐浠芥斁鎺夛細椤靛浘涓嶅瓨妗ｃ€佷笉瀵煎嚭锛岄噸鐢熸垚灏辨槸浜嗭級锛?    ///   路 娌＄敓鎴?鈫?娴呯伆鍗犱綅锛涘け璐?鈫?娴呯孩鍗犱綅锛?    ///   路 涓€寰嬫弿涓€鏉″緢娣＄殑杈癸紙椤电紳鐨勮瑷€锛屽拰鐧芥澘椤电晫绾垮悓涓€鏃忥級銆?    /// 椤靛浘**涓嶅弬涓庣瑪杩圭殑浠讳綍涓滆タ**锛堥€変腑 / 姗＄毊 / 瀛樻。 / 瀵煎嚭 / 鍥炴斁锛夛紝杩欓噷鍙槸"鐢诲嚭鏉?锛?    /// 绗旇抗鐢诲湪瀹冧笂闈紙<see cref="RasterizeTile"/> 閲岃繖涓€鍙ユ帓鍦?QueryGrid 涔嬪墠锛夈€?    /// </summary>
    private void DrawDocPages(InkEngine app, in RectF canvas)
    {
        var pages = app.DocView;
        if (!pages.IsOpen) return;
        if (app.PassThrough) return;      // 绌块€?= 鍏ㄨ寮€锛圫2锛夛細椤典笉鐢伙紙浣嶅浘涔熷湪 SyncDocPages 閲屾斁浜嗭級
        // 鐧芥澘"涓嶉€忔槑"鐩栦笂鏉ワ紙"鍙︿竴寮犵焊"锛夛細椤佃浣嶃€佹澘鑹茬洊婊°€?        // 鈿?鏉?*鍗婇€忔槑**鏃朵笉钘忛〉鈥斺€旇"閫忚繃涓婇潰閭ｅ紶绾哥湅鍒?PDF"锛?026-10-09 鐢ㄦ埛闂紱瑙?RasterizeTile锛夈€?        if (app.BoardOn && app.BoardOpacity >= 0.999f) return;

        ID2D1SolidColorBrush placeholder = null, failed = null, border = null;
        int i = pages.FirstAtOrAfter(canvas.MinY);
        for (; i < pages.Count; i++)
        {
            var p = pages.At(i);
            if (p.Rect.MinY > canvas.MaxY) break;
            if (!p.Rect.Intersects(canvas)) continue;

            if (p.Image != null)
            {
                var bmp = p.Image.GetBitmap(_ctx);
                if (bmp != null)
                {
                    _ctx.DrawBitmap(bmp,
                        new Vortice.RawRectF(p.Rect.MinX, p.Rect.MinY, p.Rect.MaxX, p.Rect.MaxY),
                        1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                    p.Image.DropCpuBytes();
                }
                else
                {
                    placeholder ??= Brush(new Color4(0.94f, 0.94f, 0.94f, 1f));
                    _ctx.FillRectangle(new Vortice.RawRectF(p.Rect.MinX, p.Rect.MinY, p.Rect.MaxX, p.Rect.MaxY),
                                       placeholder);
                }
            }
            else if (p.Failed)
            {
                failed ??= Brush(new Color4(0.99f, 0.90f, 0.90f, 1f));
                _ctx.FillRectangle(new Vortice.RawRectF(p.Rect.MinX, p.Rect.MinY, p.Rect.MaxX, p.Rect.MaxY), failed);
            }
            else
            {
                placeholder ??= Brush(new Color4(0.94f, 0.94f, 0.94f, 1f));
                _ctx.FillRectangle(new Vortice.RawRectF(p.Rect.MinX, p.Rect.MinY, p.Rect.MaxX, p.Rect.MaxY),
                                   placeholder);
            }

            border ??= Brush(new Color4(0f, 0f, 0f, 0.22f));
            _ctx.DrawRectangle(new Vortice.RawRectF(p.Rect.MinX, p.Rect.MinY, p.Rect.MaxX, p.Rect.MaxY),
                               border, 1f);
        }
    }

    /// <summary>
    /// 鏂囨。椤靛眰鍚屾锛堟瘡甯ф覆鏌撳墠璋冧竴娆★級锛?    ///   鈶?鎸夎鍙ｇ獥鍙ｇ敓鎴?鍥炴敹椤典綅鍥撅紙鐢熸垚棰勭畻 = **涓€甯т竴椤?*鈥斺€旂瑪浼樺厛锛?0-03 鏂囨。 7.5锛夛紱
    ///   鈶?椤靛眰鎶ョ殑鑴忓尯 鈫?鍒嗗潡鑴忥紙涓嬫鍏夋爡锛? 涓婂睆鑴忥紙Present1 鐨勭煩褰級銆?    /// "鏁村眰鍙樹簡"锛堟墦寮€/鍏抽棴鏂囨。锛夎蛋 MarkAllDirty + 涓ゅ抚鏁村睆鈥斺€斿拰鎹㈠簳鑹插悓涓€濂椼€?    ///
    /// **杩欎竴姝ヤ笌绗旇抗瀹屽叏鏃犲叧**锛氶〉浣嶅浘鐢熸垚涓嶇 `Doc`锛宍Doc` 鐨勫彉鍖栦篃涓嶇杩欓噷锛?    /// 涓よ竟鍚勮蛋鍚勭殑鐗堟湰鍙凤紝闈?鑴忓尯"鍦ㄥ垎鍧楃紦瀛橀噷浼氬悎銆?    /// </summary>
    private void SyncDocPages(InkEngine app)
    {
        _pageDirtyNow.Clear();

        var pages = app.DocView;

        // 绌块€?= **鍏ㄨ寮€**锛圫2锛岀敤鎴?2026-10-07 瀹氾級锛氶〉涓嶇敾銆佷笉鐢熸垚銆佷綅鍥惧叏鏀撅紙鐪佸唴瀛橈級銆?        // **鐧芥澘涓斾笉閫忔槑**涔熻蛋鍚屼竴鏉￠摼锛?涓ゅ紶绾?涓婇潰閭ｅ紶鐩栦綇锛氶〉璁╀綅銆佷綅鍥惧叏鏀撅級銆?        // 鈿?鏉?*鍗婇€忔槑**鏃朵笉钘忥細瑕侀€忚繃瀹冪湅 PDF鈥斺€旇繖鏃堕〉浣嶅浘淇濇寔椹荤暀
        //    锛堝唴瀛樺彧鍦?鏂囨。寮€鐫€锛嬫澘鍗婇€忔槑"鐨勭粍鍚堜笅澶氳姳锛涗笉閫忔槑/鏃犳澘鏃剁収鏃х渷锛夈€?        // 鐘舵€佺炕杞偅涓€甯ц"鏁村眰浣滃簾"鈥斺€斿垎鍧楅噷鐑樼潃椤靛儚绱狅紝涓嶉噸閾虹殑璇濈┛閫忎簡灞忓箷杩樼暀鐫€鍗峰瓙銆?        bool hidden = app.PassThrough || (app.BoardOn && app.BoardOpacity >= 0.999f);
        if (hidden != _docHidden)
        {
            _docHidden = hidden;
            _tiles.MarkAllDirty();
            _fullFramesLeft = 2;
        }

        if (pages.IsOpen)
        {
            if (hidden) { pages.ReleaseAll(); pages.StopWanting(); }
            else pages.SyncWindow(VisibleCanvasRect.MinY, VisibleCanvasRect.MaxY, 1, 1f);
        }

        if (pages.FullDirty)
        {
            pages.FullDirty = false;
            _tiles.MarkAllDirty();
            _fullFramesLeft = 2;
        }
        if (pages.DirtyRects.Count > 0)
        {
            foreach (var r in pages.DirtyRects)
            {
                _tiles.MarkDirty(r);
                _pageDirtyNow.Add(CanvasRectToWindow(r));
            }
            pages.DirtyRects.Clear();
        }
    }

    /// <summary>绌块€忔槸鍚︽鍦ㄦ妸鏂囨。椤靛眰钘忕潃锛堜笂涓€甯х殑鍊硷紱鐘舵€佺炕杞鏁村眰閲嶉摵锛夈€?/summary>
    private bool _docHidden;

    /// <summary>
    /// 鎶婃枃妗ｇ殑鏈€鏂扮姸鎬佸悓姝ヨ繘鍒嗗潡缂撳瓨锛屽苟鎶?杩欎竴甯ц鐪嬬殑鏂板潡"鍏夋爡鍖栧嚭鏉ャ€?    ///
    /// 涓夋锛岄『搴忓浐瀹氾細
    ///   鈶?鏂囨。鍙樹簡 鈫?鎶婅剰鍖虹鍒扮殑鍧楁爣鑴忥紙鍏ㄦ槸**鐢诲竷鍧愭爣**锛屼笌鐩告満鏃犲叧锛?    ///   鈶?鐩告満/搴曡壊鍙樹簡 鈫?鍧楀唴瀹逛竴涓儚绱犻兘涓嶇敤閲嶇敾锛屽彧鏄悎鎴愪綅缃垨搴曞彉浜?    ///   鈶?鍚屾鍙鍧楋細鏂伴湶鍑烘潵鐨勶紙鎴栬剰鐨勶級灏卞湴鍏夋爡鍖栵紝鐒跺悗鎸夐绠楁窐姹版棫鍧?    ///
    /// **婊氬姩鍙蛋绗?鈶?姝ュ拰绗?鈶?姝ラ噷鐨?娌℃湁鏂板潡"鍒嗘敮**锛屾墍浠ユ粴涓€鏍肩殑浠ｄ环
    /// 涓庢枃妗ｆ€婚噺鏃犲叧鈥斺€旇繖鏄垎鍧楃浉瀵硅€佸仛娉曠殑鏍规湰宸埆銆?    /// </summary>
    private void SyncTiles(InkEngine app)
    {
        // 鍥炴斁鏈熼棿鍐呭灞傛覆鏌撶殑鏄?*褰卞瓙鏂囨。**锛堝凡鍑哄畬鐨勭瑪鐢伙紱瑙?InkEngine.RenderDoc锛夆€斺€?        // 杩欐牱鍒嗗潡缂撳瓨銆佸绐楀彛銆丏PI銆佹澘鑹插叏閮ㄧ櫧鎷匡紝鍙湁"姝ｅ湪闀跨殑閭ｄ竴鏉?璧版诞鍔ㄥ眰銆?        var doc = app.RenderDoc;

        // 鏂囨。椤靛眰锛堝浘鐗?/ PDF锛夊厛鍚屾锛氭寜瑙嗗彛绐楀彛鐢熸垚/鍥炴敹椤典綅鍥撅紝
        // 骞舵妸瀹冩姤鐨勮剰鍖鸿浆鎴?鍒嗗潡鑴?+ 涓婂睆鑴?銆傞〉浣嶅浘鍏堝氨浣嶏紝杩欎竴甯х殑鍏夋爡鍖栨墠鐢诲緱鍑烘潵銆?        SyncDocPages(app);

        if (_tilesVersion != doc.Version)
        {
            if (doc.Dirty.Full)
            {
                _tiles.MarkAllDirty();
                _fullFramesLeft = 2;
            }
            else if (doc.StructureChangedSinceRender)
            {
                // 缁撴瀯鍙樹簡锛堝垹 / 绉?/ 鎾ら攢 / 娓呯┖锛夛細鍧楅噷"鍙樊鍑犳潯鏂扮瑪鐢?鐨勫墠鎻愭病浜嗭紝
                // 鎸傜潃鐨勮ˉ鐢绘竻鍗曚竴寰嬩綔搴燂紝鏀规垚鏁村潡閲嶇敾銆?                _tiles.FlushAppendsAsDirty();
                foreach (var r in doc.Dirty.Rects) _tiles.MarkDirty(r);
            }
            else
            {
                // 鍙涓嶅噺锛氫氦缁?琛ョ敾"璺緞鈥斺€旀妸鏂扮瑪鐢昏鍒板畠瑕嗙洊鐨勫潡涓婏紝
                // 涓嶆竻绌烘暣鍧椼€傝繖鏄?鍚屼竴椤靛ⅷ杩瑰緢澶氳繕鍦ㄥ啓"涓嶅崱鐨勫叧閿€?                // 娉ㄦ剰锛氳繖鏃剁殑鑴忓尯鍏ㄩ儴鏉ヨ嚜鏂扮瑪鐢绘湰韬紝鎵€浠ヤ笉蹇呭啀 MarkDirty銆?                int missed = 0;
                foreach (var s in doc.AppendedSinceRender)
                    if (!_tiles.MarkAppend(s)) missed++;
                LastAppendMissed = missed;
            }
            _tilesVersion = doc.Version;
        }

        // 鐩告満鍙樹簡锛?*鍒嗗潡涓€寰嬩笉鍔?*锛屽彧鏄创鐨勪綅缃彉浜嗐€傚悗缂撳啿閲岃汉鐫€鐨勬槸
        // 婊氬姩鍓嶇殑鐢婚潰锛屾暣鍧楅兘涓嶅彲淇★紝鎵€浠ヨ繖涓€甯ф暣灞忛噸鍚堟垚銆?        if (ViewOffsetY != _lastCamY)
        {
            _lastCamY = ViewOffsetY;
            _fullFramesLeft = 2;      // 鐩告満鍙樹簡锛氫袱涓紦鍐查兘瑕侀噸鐢诲埌鏂颁綅缃紙瑙佸瓧娈佃鏄庯級
        }

        // 搴曡壊鏄?*鐢昏繘鍒嗗潡閲?*鐨勶紙閫忔槑鎵规敞 = 鎿︽垚鍏ㄩ€忔槑锛涚櫧鏉?= 鏉胯壊锛涙枃妗ｅ紑鐫€ = 鏂囨。鍨櫧锛夛紝
        // 鎵€浠ユ崲搴曡壊锛堝惈"鏂囨。寮€/鍏?"绌块€?瀵艰嚧鐨勫灚鐧借繘鍑猴級绛変簬鎵€鏈夊潡閮借繃鏈熴€?        bool paperNow = !app.BoardOn && app.DocView.IsOpen && !app.PassThrough;
        if (app.BoardOn != _lastBoardOn || !app.BoardColor.Equals(_lastBoardColor)
            || paperNow != _lastPaper)
        {
            _lastBoardOn = app.BoardOn;
            _lastBoardColor = app.BoardColor;
            _lastPaper = paperNow;
            _tiles.MarkAllDirty();
            _fullFramesLeft = 2;
        }

        _tiles.Sync(VisibleCanvasRect, RasterizeTile);

        if (Trace && (_tiles.RasterizedLastFrame > 0 || _tiles.AppendedLastFrame > 0))
            Console.WriteLine($"    [tiles] raster={_tiles.RasterizedLastFrame} append={_tiles.AppendedLastFrame} "
                              + $"camY={ViewOffsetY:F0}");

        RebuildCount += _tiles.RasterizedLastFrame;
        LastRebuildMs = _tiles.RasterMsLastFrame;
        LastAppendedTiles = _tiles.AppendedLastFrame;
        TotalAppendedTiles += _tiles.AppendedLastFrame;
        LastPatchMs = _tiles.RasterMsLastFrame;
        LastPatchCount = _tiles.RasterizedLastFrame;
        LastDrawnStrokes = _tiles.StrokesLastFrame;
        LastTileCount = _tiles.Count;
        LastTileVisible = _tiles.VisibleCount;
        LastTileBudget = _tiles.BudgetNow;      // 鍙 + 鍥炴粴浣欓噺 + 棰勫彇鐜紙瑙?CanvasTileCache.BudgetNow锛?    }

    /// <summary>瑙嗗彛澶栦竴鍦堣繕鏈夋病鏈夋瑺鐫€鐨勫潡锛堝紩鎿庢嵁姝ゆ帓绌洪棽棰勫彇鎷嶏級銆?/summary>
    public bool PrefetchNeeded() => _tiles.PrefetchNeeded(VisibleCanvasRect);

    /// <summary>
    /// 绌洪棽棰勫彇涓€鎷嶏細鍙儤"瑙嗗彛澶栦竴鍦?閲屾瑺鐫€鐨勫潡锛?*涓嶅悎鎴愩€佷笉涓婂睆**锛堜笂娓?Xournal++/Rnote 鐨?    /// 棰勬覆鏌撴€濊矾锛夈€傝繑鍥?true = 鐜噷杩樻湁娲伙紝寮曟搸鍐嶆帓涓嬩竴鎷嶃€?    /// </summary>
    public bool PrefetchStep(InkEngine app)
    {
        _app = app;
        bool more = _tiles.Prefetch(VisibleCanvasRect, RasterizeTile);
        LastPrefetch = _tiles.LastPrefetch;
        LastPrefetchMs = _tiles.PrefetchMsLastFrame;
        LastPrefetchPending = more;
        if (Trace && _tiles.LastPrefetch > 0)
            Console.WriteLine($"    [prefetch] {_tiles.LastPrefetch} 鍧?{_tiles.PrefetchMsLastFrame:F2}ms "
                              + $"pending={more} camY={ViewOffsetY:F0}");
        return more;
    }

    /// <summary>
    /// 鍏夋爡鍖栦竴鍧楋細鎶婁笌杩欏潡鐢诲竷鐭╁舰鐩镐氦鐨勭瑪鐢荤敾杩涘畠鐨勭汗鐞嗐€?    ///
    /// **杩欎竴灞傚敮涓€鐨勫潗鏍囨崲绠楀氨鏄?鍑忓潡鍘熺偣"**锛岀浉鏈轰笉鍙備笌鈥斺€旂浉鏈哄彧鍑虹幇鍦?    /// <see cref="CompositeTiles"/> 閭ｄ竴姝ャ€傝€佸仛娉曢噷"鑴忓尯銆佽鍓€佸揩璺緞"
    /// 鍚勮璁板緱鎹㈢畻涓€娆★紝婕忎竴澶勫氨鍑烘畫褰憋紱鐜板湪鎯虫紡涔熸病鍦版柟婕忋€?    ///
    /// 鍧楃汗鐞嗘濂芥槸鍧楃殑澶у皬锛屾墍浠ョ敾鍑虹晫鐨勯儴鍒嗕細琚覆鏌撶洰鏍囪嚜宸辫鎺夛紝
    /// 涓嶉渶瑕侀澶栫殑瑁佸壀鐭╁舰锛堣繖涔熼『甯﹂伩鍏嶄簡鎶楅敮榻挎帴缂濓級銆?    /// </summary>
    private int RasterizeTile(ID2D1Bitmap1 target, RectF canvas, List<Stroke> onlyThese)
    {
        var app = _app;
        var doc = app.RenderDoc;      // 鍥炴斁鏈熼棿鏄奖瀛愭枃妗ｏ紙瑙?SyncTiles 鐨勮鏄庯級

        _ctx.Target = target;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.CreateTranslation(-canvas.MinX, -canvas.MinY);

        // 鍙ˉ鐢绘柊澧炵殑绗旂敾锛氫笉娓呯┖銆佷笉閬嶅巻鍧楀唴鍘熸湁鐨勭瑪鐢汇€?        // 鍐呭灞?鍙涓嶅噺"锛堟鍦ㄥ啓瀛楋級鏃舵墠璧拌繖鏉¤矾锛岃 SyncTiles 閲岀殑鍒ゆ柇銆?        if (onlyThese != null)
        {
            foreach (var s in onlyThese)
            {
                // 琚憳鍑哄幓鐨勯偅涓€鎵癸紙鎷栧姩棰勮锛変笉鐢昏繘鍐呭灞傦細瀹冧滑姝ゅ埢鐢辨诞鍔ㄥ眰鐢伙紝
                // 浣嶇疆鏄瘡甯ч兘鍦ㄥ彉鐨勫疄鏃跺彉鎹€傝 InkEngine.DetachForDrag銆?                if (app.IsContentDetached(s)) continue;
                DrawStroke(s);
            }
            _ctx.Transform = Matrix3x2.Identity;
            var hrAppend = _ctx.EndDraw();
            _ctx.Target = null;
            if (hrAppend.Failure) LastError = "tile append EndDraw: " + hrAppend.Description;
            return onlyThese.Count;
        }

        // Clear() 鏃犺瑁佸壀锛岀敤 Copy 娣峰悎鐨勫～鍏呮潵"鎿?杩欎竴鍧楋紙鑰佽鐭╋級銆?        //
        // **鐭╁舰瑕佸啓鐢诲竷鍧愭爣锛屼笉鑳藉啓 (0,0,TileSize,TileSize)**锛氬綋鍓嶇殑鍙樻崲鏄?        // "鐢诲竷 鈫?鍧楀唴"锛岀洿鎺ョ粰鍧楀唴鍧愭爣浼氳鍐嶅噺涓€娆″潡鍘熺偣锛屾暣鍧楁摝鍒扮敾闈㈠鍘伙紝
        // 缁撴灉灏辨槸"鍐呭纭疄閲嶇敾浜嗭紝浣嗘棫澧ㄦ病琚摝鎺?鈥斺€旀摝闄ゅ悗灞忓箷涓婄暀鐫€楝煎奖銆?        // 锛堝疄娴嬭俯杩囷細姗＄毊鎿︽帀浜嗘暟鎹紝灞忓箷涓婁笁鏉＄嚎杩樺湪銆傦級
        //
        // "涓ゅ紶绾?鐨勫簳锛?026-10-09锛夛細**鍗婇€忔槑鐧芥澘鐩栨枃妗?*鏃讹紝搴曡鍏堥摵"鏂囨。鐨勭焊"銆?        // 椤靛浘鍐嶇敾銆佹渶鍚庢墠鎶婃澘鑹诧紙甯﹂€忔槑搴︼級鐩栦笂鍘烩€斺€旈€忓嚭鏉ョ殑鏄?**PDF**锛屼笉鏄闈€?        bool boardOverDocTranslucent =
            app.BoardOn && app.DocView.IsOpen && app.BoardOpacity < 0.999f;

        var clearRect = new Vortice.RawRectF(
            canvas.MinX, canvas.MinY, canvas.MaxX, canvas.MaxY);
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(clearRect,
            boardOverDocTranslucent ? Brush(app.DocPaperColor) : BoardBrush(app));
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

        // 鐧芥澘妯″紡锛氬厛鐢?*搴曠汗**锛堟柟鏍?妯嚎锛夛紝鍐嶇敾"椤电晫绾?锛堜竴灞忎竴椤碉級銆?        // 涓や釜閮芥槸**鐢诲竷鍐呭**鈥斺€斿浐瀹氬湪鍥句笂鐨勪綅缃€佷笉闅忕浉鏈哄姩锛?        // 鎵€浠ヤ竴璧风儤杩涘垎鍧楃紦瀛橈細婊氬姩銆佺炕椤点€佸啓瀛楅兘涓嶉澶栬姳閽便€?        // 锛堝崐閫忔槑鐩栨枃妗ｆ椂锛屽畠浠睘浜?涓婇潰閭ｅ紶绾?锛屾尓鍒版澘鑹茬洊瀹屼箣鍚庡啀鐢汇€傦級
        if (app.BoardOn && !boardOverDocTranslucent)
        {
            DrawBoardPattern(app, canvas);
            DrawPageLines(app, canvas);
        }

        // 鏂囨。椤靛眰锛?*鍦ㄥ簳绾逛箣涓娿€佺瑪杩逛箣涓?*銆傞〉鍥炬槸鍐呭灞傜殑涓€閮ㄥ垎锛?        // 鎵€浠ュ畠璺熺潃鍒嗗潡缂撳瓨璧扳€斺€旀粴鍔ㄣ€佸啓瀛楅兘涓嶉噸鐢诲畠銆?        DrawDocPages(app, canvas);

        // 鍗婇€忔槑鐩栨枃妗ｏ細**鏉胯壊锛堝甫閫忔槑搴︼級鐩栧湪椤靛浘涓?*鈥斺€?閫忚繃涓婇潰閭ｅ紶绾哥湅鍒颁笅闈㈤偅寮?銆?        if (boardOverDocTranslucent)
        {
            _ctx.FillRectangle(clearRect, BoardBrush(app));
            DrawBoardPattern(app, canvas);
            DrawPageLines(app, canvas);
        }

        // 绌洪棿绱㈠紩鎸?*甯︾瑪瀹藉鎵?*鐨勬杩斿洖鍊欓€夛紝鎵€浠ヨ法鍦ㄥ潡杈圭晫涓婄殑绮楃瑪鐢?        // 涓よ竟閮戒細琚敾鍒帮紝涓嶄細鍑虹幇"璐磋竟琚墛鎺変竴鍗?鐨勭己鍙ｃ€?        doc.QueryGrid(canvas, _strokeScratch);
        int drawn = 0;
        foreach (var s in _strokeScratch)
        {
            if (!s.PaddedBounds.Intersects(canvas)) continue;
            if (app.IsContentDetached(s)) continue;      // 鍚屼笂锛氭嫋鍔ㄩ瑙堥偅涓€鎵逛笉杩涘唴瀹瑰眰
            DrawStroke(s);
            drawn++;
        }

        _ctx.Transform = Matrix3x2.Identity;
        var hr = _ctx.EndDraw();
        _ctx.Target = null;

        if (hr.Failure) LastError = "tile EndDraw: " + hr.Description;
        return drawn;
    }

    /// <summary>
    /// 鎶婂彲瑙佸垎鍧楄创鍒板悗缂撳啿涓婅繖涓€鍧楀尯鍩熼噷銆?    ///
    /// **鐩告満鍦ㄦ暣涓唴瀹瑰眰閲屽彧鍑虹幇鍦ㄨ繖閲?*锛氱獥鍙ｇ煩褰?鈫?鐢诲竷鐭╁舰锛屽彇姣忓潡鐩镐氦鐨?    /// 瀛愮煩褰紝璐村洖绐楀彛涓婂搴旂殑浣嶇疆銆傚潡鍦ㄧ敾甯冪┖闂撮噷寰呯潃涓嶅姩锛屾墍浠?婊氬姩"鍦?    /// 杩欓噷鏄竴鍫?*瀛愮煩褰㈡嫹璐?*鈥斺€斿拰 Win32 鐨?ScrollWindowEx銆佹祻瑙堝櫒鍚堟垚鍣?    /// 婊氬姩鍥惧眰鏄悓涓€涓墜娉曪細鎼殑鏄凡缁忕敾濂界殑鍍忕礌锛屼笉鏄噸鏂扮敾銆?    ///
    /// 蹇呴』鏄瓙鐭╁舰鑰屼笉鏄暣鍧楋細鍐欏瓧鏃惰剰鍖哄彧鏈変竴灏忓潡锛屾暣鍧楄创灏辩瓑浜庢瘡甯у鎼?    /// 鍑犲崄涓囧儚绱犮€?    /// </summary>
    private void CompositeTiles(in RectF windowRect)
    {
        // 绐楀彛 鈫?鐢诲竷锛圕anvasToWindow 鐨勯€嗭級锛歝 = w + Origin - ViewOffset
        var canvasRect = new RectF
        {
            MinX = windowRect.MinX + OriginX - ViewOffsetX,
            MinY = windowRect.MinY + OriginY - ViewOffsetY,
            MaxX = windowRect.MaxX + OriginX - ViewOffsetX,
            MaxY = windowRect.MaxY + OriginY - ViewOffsetY,
        };

        foreach (var tile in _tiles.Visible)
        {
            var tr = CanvasTileCache.RectOf(tile.Tx, tile.Ty);

            float l = MathF.Max(tr.MinX, canvasRect.MinX);
            float t = MathF.Max(tr.MinY, canvasRect.MinY);
            float r = MathF.Min(tr.MaxX, canvasRect.MaxX);
            float b = MathF.Min(tr.MaxY, canvasRect.MaxY);
            if (r <= l || b <= t) continue;

            var src = new Vortice.RawRectF(l - tr.MinX, t - tr.MinY, r - tr.MinX, b - tr.MinY);
            var dst = new Vortice.RawRectF(
                l - canvasRect.MinX + windowRect.MinX, t - canvasRect.MinY + windowRect.MinY,
                r - canvasRect.MinX + windowRect.MinX, b - canvasRect.MinY + windowRect.MinY);

            // NearestNeighbor锛氱敾甯冨拰绐楀彛鏄?1:1锛屼笉闇€瑕佹彃鍊硷紱鐢ㄧ嚎鎬ц繃婊ゅ弽鑰?            // 浼氬湪鍧楄竟鐣屼笂鎶婇偦灞呯殑鍍忕礌娣疯繘鏉ワ紙鍗婇€忔槑绗旇抗浼氱硦鍑烘贰杈癸級銆?            _ctx.DrawBitmap(tile.Source, dst, 1f,
                Vortice.Direct2D1.InterpolationMode.NearestNeighbor, src, null);
        }
    }

    /// <summary>
    /// 鐢诲竷鐭╁舰 鈫?绐楀彛鐭╁舰銆?    ///
    /// **鍑℃槸鏉ヨ嚜鏂囨。鐨勭煩褰紙鑴忓尯銆佺瑪鐢诲寘鍥寸洅锛夊湪閫佽繘 ClipToWindow /
    /// PushAxisAlignedClip 涔嬪墠閮藉繀椤昏繃杩欎竴姝?*鈥斺€旈偅浜涘嚱鏁板悆鐨勬槸绐楀彛鍧愭爣銆?    /// 鐩告満涓?0 鏃朵袱鑰呮伆濂界浉绛夛紝涓€婊氬姩灏卞叏閿欙紝鐥囩姸鏄?婊氬畬鍐欎笉浜嗗瓧"
    /// 鎴栬€?鍐欎簡鐪嬩笉瑙?銆?    ///
    /// 杩欎釜鍧戣俯杩囦袱娆★細绗竴娆′慨鐨勬槸"鏁村眰閲嶅缓"閭ｆ潯璺紙RebuildAll锛夛紝
    /// 浣嗙湡姝ｅ啓瀛楄蛋鐨勬槸"杩藉姞涓€绗?鐨勫揩璺緞锛圖rawOnlyPatch锛夆€斺€旀病淇埌銆?    /// </summary>
    private RectF CanvasRectToWindow(in RectF r) => new()
    {
        MinX = r.MinX + ViewOffsetX - OriginX, MinY = r.MinY + ViewOffsetY - OriginY,
        MaxX = r.MaxX + ViewOffsetX - OriginX, MaxY = r.MaxY + ViewOffsetY - OriginY,
    };
    private RectF ClipToWindow(RectF r)
    {
        float l = MathF.Max(r.MinX, OriginX);
        float t = MathF.Max(r.MinY, OriginY);
        float rr = MathF.Min(r.MaxX, OriginX + Width);
        float b = MathF.Min(r.MaxY, OriginY + Height);
        if (rr < l || b < t) return RectF.Empty;
        return new RectF { MinX = l, MinY = t, MaxX = rr, MaxY = b };
    }

    /// <summary>
    /// 鏃嬭浆搴︽暟鏍囩鐨勭煩褰紙**鐢诲竷鍧愭爣**锛夈€傛娊鎴愮嫭绔嬪嚱鏁帮紝鏄洜涓烘湁涓ゅ蹇呴』
    /// 鐢?*鍚屼竴濂楀嚑浣?*锛氱敾瀹冿紝浠ュ強鎶婂畠绠楄繘姣忓抚鑴忓尯銆傚皯绠椾竴澶勶紝灞忓箷涓婂氨浼?    /// 鐣欎竴鍧楁摝涓嶆帀鐨勬畫褰憋紙閫変腑妗嗛偅涓€濂?UI 宸茬粡韪╄繃杩欎釜鍧戯級銆?    ///
    /// 浣嶇疆璐村湪鏃嬭浆鎵嬫焺澶栦晶锛堣窡鐫€鎵嬫焺杞紝鍜?Figma 涓€鏍凤級锛屽苟涓斿す鍦ㄥ綋鍓嶅彲瑙?    /// 鐢诲竷鑼冨洿鍐呪€斺€旈€夊尯璐村埌灞忓箷杈逛笂鐨勬椂鍊欙紝鏍囩涓嶄細璺戝埌灞忓箷澶栭潰鍘汇€?    /// </summary>
    /// <summary>
    /// 搴︽暟鏍囩鐨勭洅瀛?*瀹藉害璺熺潃鏂囨璧?*锛氳搴︿笉璁句笂闄愪箣鍚庝細鍑虹幇 "-1234掳" 杩欑闀挎暟瀛楋紝
    /// 鍐欐 64 閫昏緫鍍忕礌浼氭妸瀛楄鎺夛紙D2D 鐢诲湪鍥哄畾鐭╁舰閲岋紝瓒呭嚭閮ㄥ垎鐩存帴涓嶈锛夈€?    /// 鐢ㄥ拰缁樺埗鍚屼竴涓枃瀛楁牸寮忛噺涓€閬嶅搴︹€斺€?*閲忓畬瑕佺暀鐫€閭ｇ偣浣欓噺**锛?    /// 鎵€浠ュ搴?= 鏂囧瓧瀹?+ 涓や晶鍐呰竟璺濄€?    /// </summary>
    private RectF RotationReadoutRect(in SelectionFrame frame, float dpi, string text)
    {
        const float minWidthLogical = 64f, heightLogical = 30f;
        float widthLogical = minWidthLogical;
        if (!string.IsNullOrEmpty(text))
        {
            // 閲忎竴娆″氨澶熶簡锛氭爣绛惧彧鍦ㄦ嫋鍔ㄤ腑鍑虹幇锛屾瘡甯т竴娆℃祴閲忓甯х巼娌℃湁褰卞搷
            // 锛堟瘮"鎸夊瓧鏁颁及瀹?鍙潬鈥斺€斾腑鑻辨枃銆佹璐熷彿銆佸害鏁扮鍙峰搴﹂兘涓嶄竴鏍凤級銆?            float textW = MeasureTextWidth(text, ReadoutFormat(dpi));
            widthLogical = MathF.Max(minWidthLogical, textW / dpi + 24f);
        }
        var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
        float w = widthLogical * dpi, h = heightLogical * dpi;
        float gap = (SelectionHandles.RotateGripLogical * 0.5f + 9f) * dpi;

        float cx = rot.X, cy = rot.Y - gap - h * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };

        var vis = VisibleCanvasRect;
        float pad = 4f * dpi;
        if (r.MinX < vis.MinX + pad) { r.MaxX += vis.MinX + pad - r.MinX; r.MinX = vis.MinX + pad; }
        if (r.MaxX > vis.MaxX - pad) { r.MinX -= r.MaxX - (vis.MaxX - pad); r.MaxX = vis.MaxX - pad; }
        if (r.MinY < vis.MinY + pad) { r.MaxY += vis.MinY + pad - r.MinY; r.MinY = vis.MinY + pad; }
        if (r.MaxY > vis.MaxY - pad) { r.MinY -= r.MaxY - (vis.MaxY - pad); r.MaxY = vis.MaxY - pad; }
        return r;
    }

    /// <summary>
    /// 鍊炬枩瑙掓爣绛剧殑鐩掑瓙锛氳创鍦?*姝ｅ湪鎷栫殑閭ｄ釜绔偣**澶栦晶锛堟寕鍦ㄥ畠涓婃柟锛夛紝鎸夋枃妗堥噺瀹斤紝
    /// 鏈€鍚庡す杩涘綋鍓嶅彲瑙佺敾甯冦€?    ///
    /// 鍜屾棆杞爣绛惧悓涓€濂楀仛娉曪紙杩?鎸夋枃妗堥噺瀹?鍜屽す鍙栭兘鐓ф妱锛夛紝鍙湁閿氱偣涓嶅悓锛?    /// 鏃嬭浆鏍囩鎸傛棆杞焺锛岃繖涓寕鍦?*鎸囬拡姝ゅ埢鎵€鍦ㄧ殑閭ｄ釜绔偣**涓娾€斺€旀嫋鍒板摢鍎胯窡鍒板摢鍎匡紝
    /// 鐪肩潧涓嶇敤鍦ㄤ袱涓湴鏂规潵鍥炴壘銆?    /// </summary>
    private RectF InclinationReadoutRect(Vector2 anchor, float dpi, string text)
        => ReadoutPillRect(anchor, dpi, text, 30f, 84f, ReadoutFormat(dpi), PillPlace.Above);

    /// <summary>璇绘暟鑳跺泭璐村湪閿氱偣鐨勫摢涓€渚с€?/summary>
    private enum PillPlace
    {
        /// <summary>閿氱偣姝ｄ笂鏂癸紙鍊炬枩瑙?/ 椤剁偣璇绘暟閭ｄ竴濂楋級銆?/summary>
        Above,
        /// <summary>閿氱偣宸﹁竟銆佺珫鐩存柟鍚戝榻愶紙瑙掓爣锛氳创鍦ㄩ偅涓鐨勫渚э級銆?/summary>
        LeftOf,
        /// <summary>閿氱偣鍙宠竟銆佺珫鐩存柟鍚戝榻愶紙瑙掓爣锛夈€?/summary>
        RightOf,
    }

    /// <summary>
    /// 涓€棰楄鏁拌兌鍥婄殑鐩掑瓙锛?*鎸夋枃妗堥噺瀹?*銆佽创鍒伴敋鐐圭殑涓€渚с€佸啀澶硅繘褰撳墠鍙鐢诲竷銆?    ///
    /// 鍏ㄥ伐绋嬪彧鏈夎繖涓€浠?閲忓 + 澶瑰彇"鐨勫疄鐜帮紙鍊炬枩瑙?伪銆佸渾鐨?r/d銆佹き鍦?a/b銆佹棆杞?螖锛?    /// 浠ュ強绗憿杞殑閭ｄ簺瑙掓爣閮借蛋瀹冿級锛氶偅杈瑰綋骞村氨鏄?閲忎竴娆°€佺敾鍙︿竴浠?鍑虹殑娈嬪奖锛?    /// 鍒嗗紑鍐欒繜鏃╀細鍦ㄧ洅瀛愮殑瀹界獎涓婂啀鐘竴娆°€?    /// </summary>
    private RectF ReadoutPillRect(Vector2 anchor, float dpi, string text,
                                  float heightLogical, float minWidthLogical,
                                  IDWriteTextFormat fmt, PillPlace place)
    {
        float widthLogical = minWidthLogical;
        if (!string.IsNullOrEmpty(text))
        {
            float textW = MeasureTextWidth(text, fmt);
            widthLogical = MathF.Max(minWidthLogical, textW / dpi + 24f);
        }

        float w = widthLogical * dpi, h = heightLogical * dpi;
        float gap = (SelectionHandles.VisualSizeLogical * 0.5f + 9f) * dpi;
        float cx, cy;
        switch (place)
        {
            case PillPlace.LeftOf: cx = anchor.X - gap - w * 0.5f; cy = anchor.Y; break;
            case PillPlace.RightOf: cx = anchor.X + gap + w * 0.5f; cy = anchor.Y; break;
            default: cx = anchor.X; cy = anchor.Y - gap - h * 0.5f; break;    // Above
        }
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };

        var vis = VisibleCanvasRect;
        float pad = 4f * dpi;
        if (r.MinX < vis.MinX + pad) { r.MaxX += vis.MinX + pad - r.MinX; r.MinX = vis.MinX + pad; }
        if (r.MaxX > vis.MaxX - pad) { r.MinX -= r.MaxX - (vis.MaxX - pad); r.MaxX = vis.MaxX - pad; }
        if (r.MinY < vis.MinY + pad) { r.MaxY += vis.MinY + pad - r.MinY; r.MinY = vis.MinY + pad; }
        if (r.MaxY > vis.MaxY - pad) { r.MinY -= r.MaxY - (vis.MaxY - pad); r.MaxY = vis.MaxY - pad; }
        return r;
    }

    // ------------------------------------------------------------------
    //  鐣岄潰灞?    // ------------------------------------------------------------------

    /// <summary>
    /// 鐣岄潰鐭╁舰鏄惁鐩栦綇杩欎釜**閫昏緫**灞忓箷鍧愭爣锛堢敤浜庤緭鍏ョ殑绗竴娆″懡涓祴璇曪級銆?    /// 浼犺繘鏉ョ殑鍧愭爣宸茬敱寮曟搸闄や互 DPI 缂╂斁锛屽拰鐣岄潰鑷繁鐨勫潗鏍囩郴涓€鑷淬€?    /// </summary>
    public bool UiBoundsLogicalContains(float screenX, float screenY)
    {
        if (_uiLogicalBounds.IsEmpty) return false;
        return screenX >= _uiLogicalBounds.MinX && screenX < _uiLogicalBounds.MaxX
            && screenY >= _uiLogicalBounds.MinY && screenY < _uiLogicalBounds.MaxY;
    }

    /// <summary>璇婃柇鐢細鐣岄潰杩欎竴甯у崰鐨勫睆骞曠煩褰€?/summary>
    public string UiDebug => _uiBounds.IsEmpty
        ? "鏃?
        : $"({_uiBounds.MinX:F0},{_uiBounds.MinY:F0})-({_uiBounds.MaxX:F0},{_uiBounds.MaxY:F0})";

    /// <summary>
    /// 甯冨眬鐣岄潰銆傝繑鍥炵晫闈㈣繖涓€甯ф槸鍚﹀彲瑙併€?    ///
    /// 鍧愭爣绾﹀畾锛堣繖鏄紩鎿庡拰鐣岄潰涔嬮棿鏈€瀹规槗鍑洪敊鐨勪竴鐐癸紝鍐欐鍦ㄨ繖閲岋級锛?    /// 鐣岄潰鎷垮埌鐨勬槸"鑷繁閭ｅ潡鐢诲竷"鐨勫潗鏍囷紝姘歌繙浠?(0,0) 寮€濮嬶紝鍗曚綅鏄€昏緫鍍忕礌锛?    /// 瀹冩妸鎮诞鏉℃憜鍦ㄨ嚜宸辩敾甯冮噷鐨勫摢涓綅缃紝杩斿洖鍝釜鐭╁舰銆傚紩鎿庤礋璐ｆ妸閫昏緫鐭╁舰
    /// 涔樹互 dpiScale 鍙樻垚灞忓箷涓婄殑鐗╃悊鐭╁舰鈥斺€旂晫闈笉闇€瑕佺煡閬撶墿鐞嗗儚绱犲拰 DPI銆?    /// </summary>
    private bool PrepareUi(InkEngine app)
    {
        // 鎴浘鍙栨櫙/璋冩暣鏈熼棿锛氱晫闈㈡暣鍧楁敹璧锋潵锛?.3.0锛夈€?        //
        // 閬僵鏄?妯℃€?鐨勶細宸ュ叿鏉＄暀鍦ㄤ笂闈細璺熼伄缃╂墦鏋讹紙瀹冨湪閬僵涔嬩笂鐢汇€佷細浜潃锛夛紝
        // 鑰屼笖"鐩存帴鎴彇"浼氭妸瀹冩媿杩涘浘閲屻€傛敹璧疯繖涓€鍧楃敱寮曟搸鍋氾紙涓嶇鐣岄潰鑷繁鎬庝箞鎯筹級锛?        // 鍙槸杩欎竴甯т笉鐢?涓嶆帴杈撳叆鈥斺€?鐣岄潰宕╀簡鍐嶉€犱竴涓?閭ｄ笁绾ч樁姊鐨勬槸 Ui 瀵硅薄锛屼笉鍙楀奖鍝嶃€?        if (app.CaptureActive)
        {
            _uiLogicalBounds = RectF.Empty;
            _uiBounds = RectF.Empty;
            return false;
        }

        if (!app.UiVisibleNow)
        {
            _uiLogicalBounds = RectF.Empty;
            _uiBounds = RectF.Empty;
            return false;
        }

        float k = Dpi / 96f * app.UiScale;   // 鐗╃悊 = 鐣岄潰閫昏緫 脳 k锛圖PI 脳 鐣岄潰缂╂斁锛?
        // 鐣岄潰鐪嬪埌鐨?灞忓箷"鏄?*鏁村潡铏氭嫙妗岄潰**锛屽拰 `IUiHost.Screen` 鏄悓涓€涓笢瑗裤€?        //
        // 浠ュ墠杩欓噷缁欑殑鏄?*鏈獥鍙ｉ偅涓€鍧楁樉绀哄櫒**鐨勭煩褰紙OriginX/Width 鏄鐩栫獥鍙ｈ嚜宸辩殑锛夛細
        // 鍚屼竴涓蹇典袱涓瓟妗堬紝鐣岄潰鎸夊摢涓畻閮戒細鏈変竴涓槸閿欑殑鈥斺€斿壇灞忎笂"璐村彸涓嬭"
        // 绔嬪埢鍋忎竴鍧椼€傚鏄剧ず鍣ㄤ笅姣忎釜瑕嗙洊绐楀彛閮戒細鐢ㄨ繖涓€艰皟涓€娆?Layout锛?        // 鐜板湪瀹冧滑浼犵殑鏄悓涓€涓煩褰紝鐣岄潰閭ｈ竟灏辨槸骞傜瓑鐨勩€?        //
        // 娉細杩欓噷鐨勯€昏緫灏哄浠嶇敤鍏ㄥ眬 DpiScale锛堢涓€鍧楀睆锛夈€傚壇灞?DPI 涓嶅悓鐨勫畬鏁存敮鎸?        // 鏄簩鏈燂紙闅剧偣 4锛夛紝灞婃椂鏀规垚鎸夌獥鍙ｅ彇銆?        var uiScreen = app.UiLogicalScreen;   // 宸查櫎浠?UiScale锛堢晫闈㈡斁澶у悗锛屽畠鑳芥憜鐨勫湴鏂瑰彉灏忥級

        // 鐣岄潰鍙湪甯冨眬浼氬彉鐨勬椂鍊欒皟锛堝昂瀵?DPI 鍙樺寲锛夈€傛甯告瘡甯ч兘璧扮紦瀛樸€?        if (_uiLayoutBounds.IsEmpty || _uiLayoutDpi != k
            || !_uiLayoutScreen.Equals(uiScreen))
        {
            _uiLayoutBounds = app.UiLayoutNow(uiScreen, k);
        _uiLayoutScreen = uiScreen;
        _uiLayoutDpi = k;
        }

        // 鍗犵敤鐭╁舰姣忓抚閮介棶涓€娆★細鎮诞鏉¤鎷栧姩銆佸睍寮€璋冭壊鏉裤€佹姌鍙犳敹璧枫€佹殏鏃舵秷澶憋紝
        // 閮介潬杩欎釜鏂规硶鍛婅瘔寮曟搸锛涘惁鍒欏懡涓祴璇曞拰鑴忓尯浼氫竴鐩村仠鍦ㄦ棫浣嶇疆銆?        var live = app.UiQueryBoundsNow();
        _uiLogicalBounds = live;
        // 鐗╃悊鍍忕礌鐨勫崰鐢ㄧ煩褰€?*杩?鐢诲埌澶栭潰閭ｄ竴鍦?涓€璧风畻**锛堟姇褰憋紝瑙?IOverlayUi.PaintMargin锛夛細
        // 涓嶇畻杩涘幓鐨勮瘽锛岄潰鏉夸竴绉诲姩锛堟嫋鍔?灞曞紑/鏀惰捣锛夛紝鏃ф姇褰卞氨鎿︿笉鎺夆€斺€旇剰鍖洪噷娌℃湁瀹冿紝
        // 灞忓箷涓婁細鐣欎笅涓€鏉¤秺鏉ヨ秺鑴忕殑鍗板瓙銆傚懡涓祴璇曠敤鐨勬槸涓婇潰閭ｄ唤**娌℃斁澶х殑**閫昏緫鐭╁舰銆?        float paintPad = app.UiPaintMarginNow * k;
        _uiBounds = live.IsEmpty ? RectF.Empty : new RectF
        {
            MinX = live.MinX * k - paintPad,
            MinY = live.MinY * k - paintPad,
            MaxX = live.MaxX * k + paintPad,
            MaxY = live.MaxY * k + paintPad,
        };

        return !live.IsEmpty;
    }

    private RectF _uiLayoutScreen = RectF.Empty;
    private float _uiLayoutDpi = -1f;
    /// <summary>Layout 涓婁竴娆¤繑鍥炵殑閫昏緫鐭╁舰锛岀敤鏉ュ垽鏂槸鍚﹂渶瑕侀噸鏂板竷灞€銆?/summary>
    private RectF _uiLayoutBounds = RectF.Empty;

    /// <summary>
    /// 鎹㈢晫闈㈡椂蹇呴』鎶婂畠娓呮帀锛氫笉鐒舵柊鐣岄潰**姘歌繙涓嶄細琚皟 Layout**锛堝睆骞曞拰 DPI 閮芥病鍙橈紝
    /// 缂撳瓨鐪嬭捣鏉ヨ繕鏈夋晥锛夛紝瀹冪殑 QueryBounds 浼氫竴鐩磋繑鍥炵┖鈥斺€?    /// 琛ㄧ幇灏辨槸"鎹笂鍘荤殑鐣岄潰鐪嬩笉瑙併€佷篃鐐逛笉鍒?銆?    /// </summary>
    internal void InvalidateUiLayout()
    {
        _uiLayoutScreen = RectF.Empty;
        _uiLayoutDpi = -1f;
        _uiLayoutBounds = RectF.Empty;
    }

    /// <summary>
    /// 鎶婂唴瀹瑰眰**鏁村眰浣滃簾**锛堝洖鏀惧垏鎹?褰卞瓙鏂囨。 / 鐪熸枃妗?銆佸線鍥炴嫋杩涘害鏃剁敤锛夈€?    ///
    /// 涓轰粈涔堜笉鑳藉彧闈?`doc.Version`锛氫袱浠芥枃妗ｇ殑鐗堟湰鍙峰彲鑳芥挒涓婏紝SyncTiles 閲?    /// `_tilesVersion != doc.Version` 涓€姣斿氨"鐪嬭捣鏉ユ病鍙?锛屽睆骞曚笂鏄笂涓€浠芥枃妗ｇ殑鍍忕礌銆?    /// </summary>
    internal void ForceContentRebuild()
    {
        _tilesVersion = -1;
        _fullFramesLeft = 2;
        _tiles.MarkAllDirty();
    }

    /// <summary>
    /// 鎶婄晫闈㈢敾鍒板悗缂撳啿涓娿€?    ///
    /// 姣忓抚鍙噸鐢荤晫闈㈣嚜宸遍偅涓€灏忓潡鐭╁舰锛堣鍓?+ 鎸夊叾閫昏緫鍧愭爣缁樺埗锛夛紝鏁村睆鐨勫叾瀹?    /// 閮ㄥ垎涓€寰嬩笉鍔ㄣ€傚疄娴嬭繖鍧楃殑浠ｄ环鍦?1 姣浠ュ唴锛屽拰涓€涓剰鍖鸿ˉ涓佸悓閲忕骇銆?    ///
    /// 涓ょ"鏇寸渷"鐨勫仛娉曢兘璇曡繃锛岄兘涓嶆垚绔嬶紝鍒蛋鍥炲ご璺細
    ///  1) 鎶婄晫闈㈢敾杩涗竴寮犱綅鍥惧啀 DrawBitmap 璐翠笂鍘?鈥斺€?Direct2D 鐨勪綅鍥句竴鏃︿綔涓?    ///     娓叉煋鐩爣锛屽氨涓嶈兘鍐嶅綋缁樺埗婧愶紝璐翠笂鍘绘案杩滄槸涓€寮犵┖鍥撅紙鍐呭灞傚洜姝ゆ墠闇€瑕?    ///     _contentTarget / _contentSource 涓や釜瑙嗗浘锛夈€?    ///  2) 鎶婄晫闈㈢殑缁樺埗褰曟垚 ID2D1CommandList 鍐嶉噸鏀?鈥斺€?褰曚笅鏉ョ殑鍛戒护閲嶆斁鏃?    ///     鍦?2 鍊嶅睆涓婁細琚斁澶у洓娆℃垨琚帹鍒扮敾甯冨锛屾湰鏈烘湭鑳芥嬁鍒扮ǔ瀹氱粨鏋溿€?    /// 绛夌晫闈㈢湡鐨勫彉寰楀緢澶嶆潅锛堝ぇ閲忔ā绯?闃村奖锛夊啀鍥炴潵瑙ｅ喅锛屽眾鏃惰杩?4K 涓€璧烽噺銆?    /// </summary>
    private void DrawUi(InkEngine app)
    {
        float k = Dpi / 96f * app.UiScale;
        // 鐣岄潰鐢诲湪鑷繁鐨勭粷瀵归€昏緫鍧愭爣閲岋紙鍜屽畠 Layout 鎷垮埌鐨勯€昏緫灞忓箷鍚屼竴濂楋級锛?        // 寮曟搸璐熻矗鎹㈢畻鎴愮墿鐞嗗儚绱狅細鍏堜箻 k锛圖PI 脳 鐣岄潰缂╂斁锛夛紝鍐嶅噺鍘荤獥鍙ｅ師鐐广€?        //
        // **杩欓噷缁濆涓嶈兘甯︾浉鏈?*锛氱晫闈㈡槸璐村湪灞忓箷涓婄殑宸ュ叿鏉★紝鐢诲竷婊氬畠涓嶅姩銆?        // 浠ュ墠杩欓噷鐢ㄧ殑鏄?CanvasToWindow锛堝惈 ViewOffsetY锛夛紝鐩告満涓?0 鏃剁湅涓嶅嚭
        // 闂锛屼竴婊氬姩鏁翠釜鐣岄潰灏变細璺熺潃鍐呭寰€涓婅窇鈥斺€旀鏄?鍧愭爣鎹㈢畻婕忎竴澶?
        // 閭ｄ竴绫?bug 鐨勫張涓€渚嬨€?        _ctx.SetDpi(96f, 96f);
        _ctx.Transform = Matrix3x2.CreateScale(k)
                       * Matrix3x2.CreateTranslation(-OriginX, -OriginY);
        // 瑁佸壀鐭╁舰鍚屾牱鐢ㄩ€昏緫鍧愭爣锛堜細琚笂闈㈢殑鍙樻崲涓€璧蜂綔鐢級銆?        // **寰€澶栨斁涓€鍦?*锛氱晫闈細鐢诲埌鍗犵敤鐭╁舰澶栭潰锛堟姇褰便€佹诞鍑虹殑棰勮锛岃 IOverlayUi.PaintMargin锛夈€?        // 鍗犵敤鐭╁舰鏈韩**涓嶆斁**鈥斺€斿畠鍚屾椂鏄懡涓祴璇曚笌杈撳叆灏忕獥鐨勭煩褰€?        float pad = app.UiPaintMarginNow;
        var clip = new Vortice.RawRectF(_uiLogicalBounds.MinX - pad, _uiLogicalBounds.MinY - pad,
                                        _uiLogicalBounds.MaxX + pad, _uiLogicalBounds.MaxY + pad);
        _ctx.PushAxisAlignedClip(clip, AntialiasMode.Aliased);
        // 闃插脊鍏ュ彛鍦ㄥ紩鎿庨偅杈癸紙`UiRenderNow`锛夛細鐣岄潰杩炴姏涓夋灏辨暣浣撳仠鐢紝绗旇抗鐓у父銆?        // 涓婚鍙傛暟缁欑殑鏄?褰撳墠娴眰涓婚"锛堢晫闈㈡帹涓婃潵鐨勯偅濂楋級鈥斺€旀垜浠殑鐣岄潰鑷繁甯︿护鐗岋紝
        // 涓嶈杩欎釜鍙傛暟锛涗絾涓囦竴灏嗘潵鏈変釜鐣岄潰鎯崇敤寮曟搸缁欑殑閰嶈壊锛屾嬁鍒扮殑搴旇鏄悓涓€濂椼€?        app.UiRenderNow(_ctx, app.FloatingTheme);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.PopAxisAlignedClip();
    }

    /// <summary>渚涘垎杈ㄧ巼瀹炴祴澶嶇敤鍚屼竴濂楃粯鍒惰矾寰勩€?/summary>
    public void DrawStrokeForTest(Stroke s) => DrawStroke(s);

    /// <summary>
    /// 鎶婁竴鎵瑰璞＄敾鍒?*绂诲睆浣嶅浘**涓婂苟璇诲洖 BGRA锛堝鍒跺埌鍓创鏉挎椂缁欏閮ㄧ▼搴忛偅寮犲浘鐢級銆?    ///
    /// 鑳屾櫙鏄?*鍏ㄩ€忔槑**鈥斺€旂矘鍒板埆澶勬椂涓嶈甯︿笂鎴戜滑鐨勭櫧搴曟垨妗岄潰銆?    /// <paramref name="region"/> 鏄繖鎵瑰璞″湪鐢诲竷鍧愭爣閲岀殑鑼冨洿锛堣皟鐢ㄦ柟绠楀ソ锛屽惈涓€鐐圭暀鐧斤級锛?    /// 浣嶅浘灏哄 = 瀹冪殑鍍忕礌灏哄锛堢敾甯冨潗鏍囧氨鏄墿鐞嗗儚绱狅紝1:1锛夈€?    /// 杩斿洖 null = 寤轰綅鍥?缁樺埗澶辫触锛堣皟鐢ㄦ柟灏卞綋"杩欐娌″浘"锛屽璞℃牸寮忕収鏀撅級銆?    /// </summary>
    public byte[] RenderStrokesToBgra(IReadOnlyList<Stroke> strokes, RectF region, int w, int h)
    {
        if (_ctx == null || strokes == null || strokes.Count == 0 || w <= 0 || h <= 0) return null;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            _ctx.Target = target;
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            _ctx.Transform = Matrix3x2.CreateTranslation(-region.MinX, -region.MinY);
            foreach (var s in strokes) DrawStroke(s);
            _ctx.Transform = Matrix3x2.Identity;
            var hr = _ctx.EndDraw();
            _ctx.Target = null;
            if (hr.Failure) { LastError = "澶嶅埗鍒板壀璐存澘鐨勭灞忕粯鍒跺け璐? " + hr.Description; return null; }

            // D2D 浣嶅浘 鈫?CPU 鍙浣嶅浘 鈫?鎷疯繘鍐呭瓨
            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var m = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        catch (Exception ex)
        {
            LastError = "澶嶅埗鍒板壀璐存澘澶辫触: " + ex.Message;
            return null;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }

    /// <summary>
    /// 鎶?*鐣岄潰**绂诲睆鐢昏繘涓€寮犱綅鍥惧苟璇诲洖 BGRA锛堝紑鍙戞湡鍑哄浘鐢級銆?    ///
    /// 涓轰粈涔堝崟寮€杩欐潯璺細骞虫椂鐨勫嚭鍥炬槸"鎴睆"锛岄偅瑕佹眰灞忓箷浜潃銆佷細璇濇病閿侊紝
    /// 鍥鹃噷杩樹細娣疯繘妗岄潰涓婄殑涓滆タ銆傝繖鏉¤矾鎶婄晫闈㈢敾鍒拌嚜宸辩殑浣嶅浘涓娾€斺€?    /// **閿佸睆 / 杩滅▼ / 杩欏彴鏈哄櫒涓婃病浜虹湅鐫€**鐨勬椂鍊欑収鏍疯兘鍑哄浘锛屼互鍚庝篃渚夸簬鎺?CI銆?    ///
    /// 鑳屾櫙鐢ㄤ竴灞傛祬鐏帮紙涓嶆槸閫忔槑锛夛細鍗婇€忔槑鐧介潰鏉垮帇鍦ㄩ€忔槑搴曚笂鐪嬩笉娓呰竟鐣屻€?    /// </summary>
    public byte[] RenderUiToBgra(InkEngine app, int padPx, out int w, out int h)
    {
        if (app.Ui == null) { w = h = 0; return null; }
        return RenderUiToBgra(app, padPx, app.UiQueryBoundsNow(), out w, out h);
    }

    /// <summary>
    /// 鍚屼笂锛屼絾**鐢诲摢涓€鍧楄嚜宸辨寚瀹?*锛堥€昏緫鍍忕礌锛夈€?    ///
    /// 鐣岄潰鑷繁鐨勫崰鐢ㄥ尯鍙鎷嶉偅棰楃悆鍜岄偅鏉″甫瀛愶紱鍍?閫変腑妗?+ 鎿嶄綔鏉?+ 瀵煎嚭鏍煎紡闈㈡澘"
    /// 杩欑被鐢诲湪娴姩灞備笂鐨勪笢瑗垮帇鏍逛笉鍦ㄩ噷闈紝寰楁妸鑼冨洿涓€璧风粰杩涙潵銆?    /// </summary>
    public byte[] RenderUiToBgra(InkEngine app, int padPx, in RectF bounds, out int w, out int h)
        => RenderToBgra(app, padPx, bounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: false, out w, out h);

    /// <summary>
    /// 鍚屼笂锛屼絾搴曡壊**閫忔槑**锛堢粰"鐢ㄧ晫闈㈣嚜宸辩殑娓叉煋鐢熸垚绋嬪簭鍥炬爣"鐢紝瑙?`--makeicon`锛夈€?    ///
    /// 涓轰粈涔堝崟寮€涓€鏉¤€屼笉鏄粰涓婇潰閭ｆ潯鍔犲弬鏁帮細閭ｆ潯鐨勭伆搴曟槸缁?鍑哄浘缁欎汉鐪?鐢ㄧ殑
    /// 锛堝浘閲屽緱鏈夊簳鑹叉墠鐪嬪緱娓呯櫧鍗＄墖锛夛紝鑰屽浘鏍囪鐨勬槸"鍙湁閭ｄ釜鍦?銆?    /// </summary>
    public byte[] RenderUiToBgraTransparent(InkEngine app, int padPx, in RectF bounds,
                                            out int w, out int h)
        => RenderToBgra(app, padPx, bounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: false, out w, out h,
                        clear: new Color4(0f, 0f, 0f, 0f));

    /// <summary>
    /// **娴姩灞?*绂诲睆鍑哄浘锛氶€変腑妗嗐€佸叓涓墜鏌勩€佹搷浣滄潯銆佹寕鍦ㄦ潯涓嬮潰鐨勫皬闈㈡澘锛堥鑹?灞傜骇/瀵煎嚭鏍煎紡锛夈€?    ///
    /// 鍜屼笂闈㈤偅鏉＄殑鍖哄埆鍙湪**鍧愭爣绯?*锛氱晫闈㈢敾鍦ㄨ嚜宸辩殑閫昏緫灞忓箷鍧愭爣閲岋紙涔?DPI 灏卞畬浜嬶級锛?    /// 娴姩灞傜敾鐨勬槸**鐢诲竷鍧愭爣**锛堢浉鏈鸿繕娌″噺锛夈€傛墍浠ヨ繖閲屼笉涔樼缉鏀撅紝鐩存帴鎶?    /// "鐢诲竷鍧愭爣 鈫?浣嶅浘"鍐欏嚭鏉ワ紝`bounds` 涔熻缁欑敾甯冨潗鏍囥€?    ///
    /// 鍗曞紑杩欐潯鐨勭悊鐢卞拰鐣岄潰閭ｆ潯涓€鏍凤細杩欎簺瀹跺叿鍏ㄥ湪娴姩灞備笂锛屾闈㈡埅鍥炬埅涓嶅埌骞插噣鐨勪竴鐗堬紝
    /// 鑰屽畠浠殑鎺掔増锛堝瓧鏈夋病鏈夊眳涓€佷袱鏉＄嚎绂诲杩滐級**鍙兘闈犲浘鐪?*锛屽嚑浣曡嚜妫€鐪嬩笉鍑烘潵銆?    /// </summary>
    public byte[] RenderFloatingToBgra(InkEngine app, int padPx, in RectF canvasBounds, out int w, out int h)
        => RenderToBgra(app, padPx, canvasBounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: true, out w, out h);

    private byte[] RenderToBgra(InkEngine app, int padPx, in RectF bounds, float dpiScale,
                                bool floatingCanvasSpace, out int w, out int h,
                                Color4? clear = null)
    {
        w = h = 0;
        if (_ctx == null) return null;
        // 娴姩灞傝繖鏉¤矾涓嶇敾鐣岄潰锛屾墍浠ヤ笉瑕佹眰鐣岄潰鎸傜潃锛涚晫闈㈤偅鏉″繀椤绘寕鐫€鎵嶆湁涓滆タ鍙敾銆?        if (!floatingCanvasSpace && (app.Ui == null || !app.UiVisibleNow)) return null;
        if (bounds.IsEmpty) return null;
        _app = app;                 // 鐢荤殑杩囩▼涓湁鍑犲瑕佽"褰撳墠绐楀彛"鐨勭姸鎬?
        float dpi = dpiScale;
        // 鐣岄潰鐨勭灞忓嚭鍥惧繀椤诲拰 DrawUi 鐢ㄥ悓涓€涓箻鏁帮紙DPI 脳 鐣岄潰缂╂斁锛夛紱
        // 娴姩灞傞偅鏉′笉涔橈紙鐢诲竷鍧愭爣鏈潵灏辨槸鐗╃悊鍍忕礌锛岃涓嬮潰 unit 涓€娈碉級銆?        float uiK = dpi * app.UiScale;
        // **涓ゆ潯璺殑鍗曚綅涓嶄竴鏍?*锛堣繖涓€鏉¤俯杩囷級锛氱晫闈㈢敾鍦ㄨ嚜宸辩殑**閫昏緫**鍧愭爣閲岋紝瑕佷箻 DPI 鎹㈡垚
        // 鐗╃悊鍍忕礌锛涚敾甯冨潗鏍?*鏈韩灏辨槸鐗╃悊鍍忕礌**锛堜笂涓嬫枃 DPI 鍥哄畾 96锛宍CanvasToWindow`
        // 涔熶笉甯︾缉鏀撅級锛屾墍浠?1 涓崟浣嶅氨鏄?1 涓綅鍥惧儚绱犮€傜涓€鐗堢粰娴姩灞備篃涔樹簡 DPI锛?        // 浣嶅浘寮€鎴愪袱鍊嶅ぇ銆佸唴瀹瑰嵈鎸?1:1 鐢伙紝鏁翠釜鐢婚潰琚帹鍒板乏杈瑰幓浜嗐€?        float unit = floatingCanvasSpace ? 1f : uiK;
        w = (int)MathF.Ceiling((bounds.MaxX - bounds.MinX) * unit) + padPx * 2;
        h = (int)MathF.Ceiling((bounds.MaxY - bounds.MinY) * unit) + padPx * 2;
        if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return null;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            _ctx.Target = target;
            _ctx.BeginDraw();
            // 搴曡壊锛?*娌℃寚瀹?*鎵嶉摵娴呯伆锛堝嚭鍥剧粰浜虹湅鏃惰鏈夊簳锛夛紱鎸囧畾浜嗗氨鐓ф寚瀹氱殑鏉モ€斺€?            // 灏ゅ叾鏄?鎸囧畾鎴愬叏閫忔槑"锛堝浘鏍囬偅鏉¤矾锛夛紝浠ュ墠鐢?`clear.A > 0` 鍒わ紝
            // 鎶?娌℃寚瀹?鍜?瑕侀€忔槑"娣锋垚浜嗕竴浠朵簨锛岀粨鏋滃浘鏍囬噷姘歌繙鍨潃涓€鍧楁祬鐏版柟鍧?            //锛?026-09-29 鐢ㄦ埛锛?鍥炬爣铏界劧鏄渾鐨勶紝浣嗗畠瀹為檯涓婃槸涓€涓柟褰?锛夈€?            _ctx.Clear(clear ?? new Color4(0.93f, 0.94f, 0.96f, 1f));
            _ctx.SetDpi(96f, 96f);

            if (floatingCanvasSpace)
            {
                // 鐢诲竷鍧愭爣灏辨槸鐗╃悊鍍忕礌锛氬彧鎸紝涓嶅钩绉荤缉鏀撅紙瑙佷笂闈?unit 閭ｄ竴娈碉級銆?                _ctx.Transform = Matrix3x2.CreateTranslation(padPx - bounds.MinX,
                                                             padPx - bounds.MinY);
                // 鎷嶈繖涓€鍧楅噷鐨勫ⅷ锛堝唴瀹瑰眰锛夛紝鍚﹀垯鍙栨櫙妗嗐€侀€変腑妗嗛兘娴湪绌虹櫧涓婏紝
                // 鐪嬩笉鍑?妗嗘湁娌℃湁鍦堜綇涓滆タ"銆傚彧鐢诲拰杩欎竴鍧楃浉浜ょ殑閭ｅ嚑鏉°€?                foreach (var s in app.Doc.Strokes)
                    if (s.PaddedBounds.Intersects(bounds) && !app.IsContentDetached(s)) DrawStroke(s);
                // 鎷栫鐐规墜鍔夸腑锛氭ā鍨嬭繕鏄棫鍑犱綍锛屼复鏃跺嚑浣曞彧娲诲湪娴姩灞備笂鈥斺€?                // 鍑哄浘杩欐潯璺笉琛ョ敾瀹冿紝鎷嶅嚭鏉ョ殑灏辨槸"鎵嬫焺涓嶈浜嗐€佺嚎涔熸病鍔?鐨勭┖闀滃ご銆?                // 锛堣鎽樺嚭鍘荤殑閭ｄ竴鎵逛笂闈㈠凡缁忚烦杩囷紝鎵€浠ヤ笉浼氬嚭鐜?鏃х嚎 + 鏂扮嚎"鍙屽奖銆傦級
                DrawVertexPreview(app);
                // **鎷栧姩棰勮**锛堢Щ鍔?鏃嬭浆鎵嬪娍涓鎽樺嚭鍐呭灞傜殑閭ｄ竴鎵癸級鍚岀悊锛?                // 涓嶈ˉ鐢荤殑璇濓紝"鏃嬭浆涓?閭ｅ紶鍥句細鍙墿涓€涓銆佺嚎涓嶈浜嗭紙2026-09-18 鍑哄浘鏍稿鏃惰俯鍒帮級銆?                DrawDragPreview(app);
                // **姝ｅ湪涔﹀啓鐨勯偅涓€绗?*锛堢敾绾夸腑鐨勫疄鏃跺嚑浣曪級涔熶笉鍦?Doc 閲岋紝寰楀崟鐙ˉ鈥斺€?                // 涓嶇劧"鐢荤嚎杩囩▼涓殑 伪 璇绘暟"杩欏紶鍥炬媿鍑烘潵鍙湁涓€棰楁爣绛俱€佹病鏈夌嚎銆?                if (app.ActiveStroke != null && !app.SuppressActiveStroke
                    && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
                    DrawStroke(app.ActiveStroke);
                if (app.PredictTailActive && !app.ActiveStroke.HiddenForPairing
                    && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
                    DrawPredictTail(app.ActiveStroke.Color, app.PredictTailWidth);
                DrawSelection(app);
                // **鍥惧簱闈㈡澘**鍚岀悊锛?026-09-22 鍔狅級锛氬畠鏄诞鍔ㄥ眰涓婄殑涓滆タ锛屼笉琛ョ敾鐨勮瘽
                // "闈㈡澘闀夸粈涔堟牱"杩欏紶鍥炬案杩滄媿涓嶅埌锛堣繖鏉¤矾鏄粰鑷/鍑哄浘鐢ㄧ殑锛?                // 灏戠敾涓€鏍峰氨鏄?鐪嬩簡涓€寮犳病鏈夐潰鏉跨殑鍥捐繕浠ヤ负闈㈡澘娌″紑"锛夈€?                DrawLibraryPanel(app);
                DrawShapeInclination(app);
                DrawCaptureOverlay(app, _ctx.Transform);   // 鎴浘鍙栨櫙锛堝惈閬僵/鍐荤粨甯э級鈥斺€斾笉鍦ㄦ埅鍥炬€佸氨鐩存帴杩斿洖
            }
            else
            {
                // 鍜?DrawUi 鍚屼竴濂楀潗鏍囷細鍏堜箻 DPI脳鐣岄潰缂╂斁锛屽啀鎶婄晫闈㈠乏涓婅鎸埌鐣欑櫧澶?                _ctx.Transform = Matrix3x2.CreateScale(uiK)
                               * Matrix3x2.CreateTranslation(-bounds.MinX * uiK + padPx,
                                                             -bounds.MinY * uiK + padPx);
                app.UiRenderNow(_ctx, app.FloatingTheme);
            }
            _ctx.Transform = Matrix3x2.Identity;
            var hr = _ctx.EndDraw();
            _ctx.Target = null;
            if (hr.Failure) { LastError = "鐣岄潰绂诲睆缁樺埗澶辫触: " + hr.Description; return null; }

            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var m = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        catch (Exception ex)
        {
            LastError = "鐣岄潰绂诲睆缁樺埗澶辫触: " + ex.Message;
            return null;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }

    /// <summary>渚涘垎杈ㄧ巼瀹炴祴澶嶇敤鍚屼竴濂?鎿︽帀涓€鍧?閫昏緫銆?/summary>
    public void ClearRectForTest(RectF r)
    {
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), _transparentBrush);
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    /// <summary>
    /// 杩欎竴甯?**鍙敾杈呭姪鍑犱綍銆佷笉鐢讳富浣?*"鐨勯偅涓€涓璞★細
    /// 鍙屾洸绾夸笁姝ュ紡鐨勭涓€姝ワ紙姝ｅ湪鏅冩笎杩戠嚎锛夆€斺€斿睆骞曚笂鍏堝彧鍑洪偅涓ゆ潯铏氱嚎锛?    /// 鏇茬嚎瑕佺瓑绗?2 涓嬬偣鍑讳箣鍚庢墠鍑虹幇锛堣 <see cref="InkEngine.HyperAsymptotePreviewOnly"/>锛夈€?    ///
    /// 鐢?<see cref="RenderFrame"/> 姣忓抚寮€澶磋涓€娆★紝**涓嶈繘鏁版嵁瀵硅薄銆佷笉杩涘瓨妗ｃ€佷篃涓嶅奖鍝嶅鍑?*
    /// 锛堝鍑鸿蛋鐨勬槸鍙︿竴鏉℃覆鏌撹矾寰勶紝鏍规湰涓嶈瀹冿級銆?    /// </summary>
    private Stroke _auxOnlyStroke;

    /// <summary>
    /// 鐢讳竴涓璞°€?*璋冪敤鏂硅礋璐ｆ妸 ctx 鍙樻崲璁炬垚"鐢诲竷鍧愭爣 鈫?绐楀彛鍧愭爣"**锛?    /// 杩欓噷鍙湪瀵硅薄鑷繁甯﹀彉鎹㈡椂鍐嶅乏涔樹竴涓嬨€?    ///
    /// 鍗曚綅鍙樻崲锛堢粷澶у鏁板璞★級璧扮殑鏄師璺紝涓€娆″浣欑殑鍙?璁惧彉鎹㈤兘涓嶅仛鈥斺€?    /// 杩欐潯璺緞姣忓抚瑕佺粰涓婁竾涓璞¤窇锛屼笉鑳戒负浜?浠ュ悗鍙兘鐢ㄥ埌"鍏堜粯鎴愭湰銆?    ///
    /// <paramref name="extra"/> 鏄?棰濆鍐嶅彔涓€灞傚疄鏃剁煩闃?鐨勫満鍚堬紙鎷栧姩棰勮锛夛紝
    /// 浼犱簡灏变笉鍘诲姩 ctx 鍙樻崲锛堣涓嬮潰鐨勫垎宀旓級銆?    /// </summary>
    private void DrawStroke(Stroke s, Matrix3x2? extra = null)
    {
        // **閰嶅鏈熺殑閭ｄ竴绗斿厛鍒敾**锛堢敤鎴?2026-09-25 瀹氾細"璇嗗埆鐨勯偅涓€鍒伙紝绗竴绗斿氨璇ユ秷澶?锛夈€?        //
        // 鈿?鏀惧湪**閫愮瑪缁樺埗**杩欎竴灞傘€佷笉鏄斁鍦ㄤ富寰幆閲岋細杩欐牱**鎵€鏈夎皟鐢ㄦ柟**閮戒細璺宠繃瀹?        //   锛堜富娓叉煋 / 瀵煎嚭 / 鑷 / 鍓创鏉匡級锛屼竴澶勫氨澶燂紝涓嶄細鍑虹幇"鏌愭潯璺繕浜嗚繃婊ゃ€?        //   瀵煎嚭鐨勫浘閲屽鍑轰竴鏉″ⅷ"閭ｇ涓嶄竴鑷淬€?        //   杩欎竴浣嶅彧绠?*鐢?*锛氭枃妗ｄ笉鍔ㄣ€佹挙閿€涓嶅姩锛堣 `Stroke.HiddenForPairing`锛夈€?        if (s.HiddenForPairing) return;

        // **"绾垮涓嶅彉"鐨勫璞?*锛堝浘褰紝瑙?Stroke.KeepsWidth锛夛細鍙樻崲鎶樿繘鍑犱綍閲?        //锛圔uildCanvasGeometry锛夛紝鎻忚竟鍙戠敓鍦?*鐢诲竷绌洪棿** 鈥斺€?瀹藉害灏辨槸 Width锛?        // 涓嶄細琚缉鏀俱€傚ソ澶勫緢鐩存帴锛氭í鐫€鎷変竴涓煩褰紝鍥涙潯杈硅繕鏄師鏉ラ偅涔堢矖銆?        if (s.KeepsWidth && (!s.Transform.IsIdentity || extra.HasValue))
        {
            DrawStrokeCore(s, extra);
            return;
        }

        if (s.Transform.IsIdentity && extra == null)
        {
            DrawStrokeCore(s);
            return;
        }

        // 灞€閮?鈫?鐢诲竷锛坰.Transform 脳 extra锛夛紝鍐?鐢诲竷 鈫?绐楀彛锛堣皟鐢ㄦ柟璁剧殑锛夈€?        // 涔樻硶椤哄簭鎸?System.Numerics 鐨勭害瀹氾細鍏堜綔鐢ㄥ乏杈圭殑銆?        var canvasToWindow = _ctx.Transform;
        _ctx.Transform = s.Transform * (extra ?? Matrix3x2.Identity) * canvasToWindow;
        DrawStrokeCore(s);
        _ctx.Transform = canvasToWindow;
    }

    /// <summary>
    /// B4 棰勬祴灏撅紙`--predict2`锛夛細**鍙敾鏄剧ず鐨?褰卞瓙"**鈥斺€旂嫭绔嬬殑涓€灏忔鍑犱綍銆佹瘡甯ч噸寤猴紝
    /// 涓嶈繘鏂囨。/瀛樻。/鍛戒腑/鎾ら攢锛堝畠杩?Stroke 閮戒笉鏄級銆傝捣鐐归敋鍦?姝ｅ湪鏄剧ず鐨勫钩婊戣緭鍑烘湯鐐?涓?    /// 锛堣 Engine.UpdatePredictTail锛夛紝鎺ュご澶勮繛缁紱闀垮害鐢卞紩鎿庝晶闄愰€熷钩婊戔€斺€?    /// 鏃х郴缁?绐佺獊璺?鐨勬韬紙鐢╁嚭鍘?缂╁洖鏉ワ級灏辨槸琚繖涓檺閫熷拰"鏂瑪鍚庣紦鎱㈠洖鏀?娌荤殑銆?    /// </summary>
    private void DrawPredictTail(Color4 color, float width)
    {
        var eng = _app;
        if (eng == null) return;
        var pts = eng.PredictTailPoints;
        if (pts.Count < 2) return;
        using var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Hollow);
            for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();               // 鈿?蹇呴』鏄惧紡 Close锛欴ispose 涓嶄繚璇佹妸鍑犱綍灏佸彛锛堜笉灏?= 绌哄嚑浣曪級
        }
        _ctx.DrawGeometry(geo, Brush(color), MathF.Max(1f, width), Gfx.StyleFor(StrokeDash.Solid));
    }

    private void DrawStrokeCore(Stroke s, Matrix3x2? extra = null)
    {
        // 鍥惧儚瀵硅薄锛氱敾鐨勬槸浣嶅浘锛屼笉鏄嚑浣曘€?*蹇呴』鏀惧湪鏈€鍓嶉潰**鈥斺€斿畠鍜屽浘褰竴鏍?        // 灞炰簬"闈炶嚜鐢辩瑪杩?锛岃蛋鍒颁笅闈㈤偅鏉?DrawGeometry 鍒嗘敮灏变細琚弿涓€涓煩褰㈣竟妗嗐€?        if (s.IsImage)
        {
            var bmp = s.Image?.GetBitmap(_ctx);
            if (bmp == null) return;
            var a = new Vector2(s.Points[0].X, s.Points[0].Y);
            var b = s.Points.Count > 1
                ? new Vector2(s.Points[^1].X, s.Points[^1].Y)
                : a;
            var dst = new Vortice.RawRectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                                           MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
            // 缂╂斁鏃剁敤绾挎€ф彃鍊硷細鏈€杩戦偦鍦ㄦ姇褰变笂浼氳鏂囧瓧杈圭紭鍏ㄦ槸閿娇锛堟斁澶х湅鏇存槑鏄撅級銆?            _ctx.DrawBitmap(bmp, dst, 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            return;
        }

        // **鍙敾杈呭姪鍑犱綍銆佸厛涓嶇敾涓讳綋**鐨勯偅涓垽鎹紙瑙?_auxOnlyStroke 閭ｆ璇存槑锛夈€?        bool auxOnly = ReferenceEquals(s, _auxOnlyStroke);
        // 鍑犱綍璧?BuildCanvasGeometry锛氬浘褰㈢殑鍙樻崲**鎶樿繘鍑犱綍閲?*锛堢嚎瀹戒笉璺熺潃缂╂斁锛?        // 瑙?Stroke.KeepsWidth锛夛紝绗旇抗鎸夎€佹牱瀛愯繑鍥炲眬閮ㄥ嚑浣曪紙ctx 涓婂甫鐫€瀹冪殑鍙樻崲锛夈€?        var geo = auxOnly ? null : s.BuildCanvasGeometry(Gfx.D2DFactory, aux: false, extra);
        if (geo == null && !auxOnly) return;
        // **鍙敾杈呭姪鍑犱綍銆佷笉鐢讳富浣?*锛氬弻鏇茬嚎涓夋寮忕殑绗竴姝ワ紙姝ｅ湪鏅冩笎杩戠嚎锛夆€斺€?        // 灞忓箷涓婂厛鍙嚭閭ｄ袱鏉¤櫄绾挎笎杩戠嚎锛屾洸绾胯绛夌 2 涓嬬偣鍑讳箣鍚庢墠璺熺潃鎸囬拡鍑虹幇
        //锛堢収 InkClass 鐨勭涓€绗旓紝瑙?Engine.HyperAsymptotePreviewOnly锛夈€?        // 涓讳綋涓嶇敾鏃?`geo` 鏄?null锛屼笅闈㈤偅涓よ鎻忚竟鑷劧璺宠繃锛岃緟鍔╁嚑浣曠収鐢汇€?        if (geo != null)
        {
            // 涓ょ鐢绘硶锛?            //   路 鍗曠偣绗旇抗 鈫?鍑犱綍鏈韩灏辨槸涓€涓渾锛屽～鍏呭畠锛堥浂闀垮害鐨勭嚎鎻忚竟浠€涔堥兘鐢讳笉鍑烘潵锛夛紱
            //   路 鍏朵綑锛堢瑪杩圭殑涓績绾裤€佺洿绾?鐭╁舰/妞渾/绠ご锛夆啋 缁熶竴浜ょ粰 D2D 鎻忚竟锛?            //     瀹藉害銆佺甯姐€佹嫄瑙掑叏鐢卞畠绠楋紙2026-09-14 璧凤紝鎴戜滑鑷繁鐨勮疆寤撲唬鐮佸凡鍒犻櫎锛夛紱
            //     **绾垮瀷锛堝疄绾?铏氱嚎/鐐圭嚎锛夊氨鍦ㄨ繖涓€姝ユ寜 <see cref="StrokeDash"/> 閫夋弿杈规牱寮?* 鈥斺€?            //     鍏ㄥ紩鎿庡彧姝や竴澶勶紝浠讳綍"鑳界敾鍑哄浘褰㈢殑璺緞"锛堝唴瀹瑰眰銆佹诞鍔ㄩ瑙堛€佸鍑恒€佽嚜妫€鍑哄浘锛?            //     閮界粡杩?DrawStroke锛屾墍浠ョ嚎鍨嬭嚜鍔ㄩ兘鐢熸晥锛屼笉鐢ㄩ€愬琛ャ€?            if (s.RendersAsDot) _ctx.FillGeometry(geo, Brush(s.Color));
            else if (!DrawPressureInk(s))
                _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.StyleFor(s.Dash));
        }

        // **杈呭姪鍑犱綍**锛堝弻鏇茬嚎鐨勪袱鏉¤櫄绾挎笎杩戠嚎銆佺珛浣撳浘褰㈣鎸＄殑鍗婂湀銆?*鍧愭爣绯荤殑缃戞牸**锛夛細
        // 鍚屼竴鏀瑪鐨勫ⅷ鑹诧紝浣?*椋庢牸鐢辫韩浠藉畾**鈥斺€?        //   路 杈呭姪绾匡紙娓愯繎绾?/ 琚尅鐨勬１锛夛細鎭掑畾缁嗚櫄绾匡紝绮楃粏 0.6 鍊嶏紙杈呭姪绾跨殑鏈垎鏄笉鎶富绾匡級锛?        //   路 **鍧愭爣绯荤綉鏍?*锛堢敤鎴?2026-09-24锛?鍙樼粏鍙樻贰"锛夛細**缁嗕竴鍗娿€佹贰涓€鍗婄殑瀹炵嚎**鈥斺€?        //     鏁村睆鏍煎瓙閾哄紑鏃讹紝"娣?姣?缁?鏇村喅瀹氬畠鎶笉鎶㈢溂銆?        // 鏀惧湪涓诲嚑浣曚箣鍚庣敾锛氫袱鑰呴噸鍙犵殑鍦版柟锛堥《鐐归檮杩?/ 鍘熺偣锛変互涓荤嚎涓哄噯銆?        var aux = s.BuildCanvasGeometry(Gfx.D2DFactory, aux: true, extra);
        if (aux != null)
        {
            bool grid = s.Kind == StrokeKind.Coordinate && s.Grid;
            _ctx.DrawGeometry(aux,
                              Brush(grid ? Stroke.Fade(s.Color, Stroke.AxisGridAlpha) : s.Color),
                              MathF.Max(1f, s.Width * (grid ? Stroke.AxisGridWidthFactor : 0.6f)),
                              Gfx.StyleFor(grid ? StrokeDash.Solid : StrokeDash.Dashed));
        }
    }

    /// <summary>
    /// **鏈夊帇鎰熺殑鑷敱绗旇抗**璧?D2D 鐨勫師鐢熷ⅷ杩癸紙`ID2D1Ink`锛氭瘡涓偣鑷甫鍗婂緞 鈫?鍙樺锛夈€?    ///
    /// 涓轰粈涔堜笉鑷繁鎷煎彉瀹借疆寤擄細瀹樻柟瀵硅繖濂楀浘鍏冨啓寰楀緢鐩寸櫧鈥斺€?姣旇繃鍘诲簲鐢ㄨ嚜宸辩敤涓€涓叉き鍦嗗拰
    /// 鍥涜竟褰㈠幓绠″ⅷ杩?*鏇村揩涔熸洿婕備寒**"锛岃€屾垜浠?2026-09-14 鍒犳帀鐨勯偅涓€灞傛鏄偅绉嶈嚜鎷艰疆寤?    /// 锛堟姌瑙掕鍓娿€佽嚜浜ゆ寲娲炪€佽捣绗旀瘺杈归兘鏄畠甯︽潵鐨勶級銆傜敤瀹冪瓑浜庢妸"鍙樺"杩欎欢浜嬩氦鍥炵粰 D2D銆?    ///
    /// **鍥涗釜鍓嶆彁锛岀己涓€涓氨杩斿洖 false 璧板洖绛夊鎻忚竟**锛堣€侀偅鏉¤矾涓€涓瓧娌℃敼锛夛細
    ///   路 杩欏彴鏈哄櫒鎷垮緱鍒?`ID2D1DeviceContext2`锛堣 Gfx.InkAvailable锛夛紱
    ///   路 杩欎竴绗?*鐪熺殑鏈夊帇鎰?*锛堣澶囨姤鐨勶紝涓嶆槸鎴戜滑鐚滅殑锛岃 Stroke.HasPressure锛夛紱
    ///   路 **瀹炵嚎**鈥斺€斿厜鏍呭寲澧ㄨ抗涓嶅悆 dash 鍥炬锛岃櫄绾?鐐圭嚎绗旇抗蹇呴』璧拌€佽矾锛?    ///   路 **娌¤鍍忕礌姗＄毊鎿︽柇**锛堟摝闄ゅ尯闂存槸"鎸夋涓嶇敾"锛岃€?ink 鏄暣鏉′竴娆℃€ч摵鍑烘潵鐨勶級銆?    ///
    /// 涓や釜瀹炵幇缁嗚妭锛?    ///   路 **娈垫暟灏侀《 120**锛氬帇鍔涙槸鎱㈠彉閲忥紝闀跨瑪鎸夋闀挎娊绋€鍗冲彲锛堜綅缃簿搴︾敱 D2D 鍦ㄦ鍐呮彃鍊艰ˉ锛夈€?    ///     鎶界█涔熻"姣忔潯绗旀瘡娆￠噸鐢?鐨勪唬浠峰皝浣忊€斺€旀垜浠笉缂撳瓨 ink 瀵硅薄锛堝畠鍙兘浠庤澶囦笂涓嬫枃寤猴紝
    ///     缂撳瓨灏卞緱鎸夌獥鍙ｅ垎寮€绠＄敓鍛藉懆鏈燂紝涓嶅垝绠楋級锛?    ///   路 **鍘嬪姏鍏堝仛涓€娆℃寚鏁板钩婊?*锛堝彧鍦ㄨ繖涓€灞傚仛锛屽瓨妗ｉ噷鐣欑殑鏄師濮嬪€硷級锛?    ///     10 bit 鐨勫師濮嬪帇鍔涘湪杞诲帇娈垫姈鍔ㄦ槑鏄撅紝鐩存帴鍠傜粰瀹藉害浼?绗斿ⅷ鍙戞瘺"銆?    /// </summary>
    private bool DrawPressureInk(Stroke s)
    {
        if (_ctx2 == null || _inkStyle == null) return false;
        if (!InkAvailable || !PressureWidth.Enabled) return false;
        if (!s.HasPressure || s.Dash != StrokeDash.Solid) return false;
        if (s.Erased.Count > 0 || s.Kind != StrokeKind.Freehand) return false;

        // 娈电敱涓ゆ潯璺敓鎴愶紙鏃х殑"鎸夋闀挎娊绋€" / `--smooth` 鐨?鏇茬嚎鍖?锛夛紝
        // 鐢熸垚瀹岀殑 `_inkSegs` 锛?`nSeg` 鍦ㄨ繖閲屼氦缁欏悓涓€鏉?D2D 澧ㄨ抗閫氶亾鐢诲嚭鍘汇€?        if (!BuildPressureSegments(s, out float startRadius, out int nSeg)) return false;

        try
        {
            var ink = _ctx2.CreateInk(new Vortice.Direct2D1.InkPoint
            {
                X = s.Points[0].X, Y = s.Points[0].Y, Radius = startRadius,
            });
            try
            {
                ink.AddSegments(_inkSegs, (uint)nSeg);
                PressureInkDraws++;
                _ctx2.DrawInk(ink, Brush(s.Color), _inkStyle);
            }
            finally { ink.Dispose(); }
            return true;
        }
        catch (Exception ex)
        {
            // **鍑洪棶棰樺氨鏁翠綋閫€鍥炵瓑瀹芥弿杈癸紝缁濅笉闈欓粯鐢绘垚绌虹櫧**鈥斺€旇€屼笖鍙姤涓€娆°€?            // 涔嬪悗涓嶅啀璧拌繖鏉¤矾锛堝惁鍒欐瘡涓€甯ч兘瑕佹姏涓€娆★紝鏃ュ織浼氳鍒峰睆锛夈€?            InkAvailable = false;
            InkNote = "杩愯涓け璐ワ紙" + ex.Message + "锛夆啋 鍘嬫劅绗旇抗閫€鍥炵瓑瀹芥弿杈?;
            Console.WriteLine("澧ㄨ抗閫氶亾锛圛D2D1Ink锛夊け璐ワ細" + ex.Message + " 鈫?鍘嬫劅绗旇抗閫€鍥炵瓑瀹芥弿杈?);
            return false;
        }
    }

    /// <summary>
    /// 鐢熸垚鍘嬫劅澧ㄨ抗鐨勬锛堝惈娓叉煋灏撅級锛屽啓杩涘鐢ㄧ殑 <c>_inkSegs</c>銆備袱鏉¤矾锛?    ///   路 **鏇茬嚎鍖栧紑鐫€**锛坄--smooth`锛夆啋 <see cref="StrokeSmoothing"/> 鐨勮繃鐐硅礉濉炲皵锛?    ///     娈垫暟鎸夌偣鏁拌蛋锛?*涓嶅啀鎸?120 鎶界█**锛堟娊绋€浼氭妸鍒氱畻鍑烘潵鐨勫集鍙堟媺鐩达級锛?    ///   路 **鍏崇潃**锛堥粯璁わ級鈫?鍘熸潵鐨?鎸夋闀挎娊绋€鍒?鈮?20 娈?锛?鎺у埗鐐硅惤鍦ㄧ洿绾夸笂"锛岃涓轰竴瀛椾笉鏀广€?    ///
    /// <paramref name="maxParam"/> 鏄?*鍥炴斁鍓嶇紑**鐢ㄧ殑鐐瑰簭鍙蜂笂闄愶紙鍙甫灏忔暟锛涙暣绗斾紶 MaxValue锛夛細
    /// 鍙杺鍒伴偅涓€澶勶紝蹇呰鏃跺湪鏈熬琛ヤ竴涓彃鍊肩偣鈥斺€旇繖鏍峰洖鏀鹃噷"姝ｅ湪闀?鐨勫帇鎰熺瑪杩?    /// 浠嶇劧璧?D2D 鍘熺敓鍙樺锛岃€屼笉鏄厛绛夊銆佸啓瀹岀獊鐒跺彉绮椼€傚墠缂€**涓嶆帴娓叉煋灏?*銆?    ///
    /// 杩斿洖 false = 娌℃湁娈碉紝璋冪敤鏂圭洿鎺ヤ笉鐢汇€?    /// </summary>
    private bool BuildPressureSegments(Stroke s, out float startRadius, out int count,
                                       float maxParam = float.MaxValue)
    {
        var pts = s.Points;
        int n = pts.Count;
        count = 0;
        startRadius = 0f;
        if (n < 2) return false;

        bool clipped = maxParam < n - 1 - 1e-4f;
        // 杩愬姩妯″瀷锛堝疄楠岋紝`--motion`锛夛細鏁存潯鐢ㄩ€変腑鐨勬ā鍨嬭緭鍑?+ 鍘嬪姏锛圡2 宸叉寜鏃堕棿鍔犳潈鎻掑€煎帇鍔涳級銆?        // 鍙"鏁寸瑪銆佹病琚鐨摝杩?鐢熸晥锛涘洖鏀惧墠缂€/鎿﹂櫎杩囩殑涓€寰嬬収鏃ц蛋鍘熷鐐广€?        bool useModel = !clipped && s.Erased.Count == 0 && StrokeMotion.Build(s);
        if (useModel) n = StrokeMotion.Count;
        int lastIdx = clipped ? Math.Clamp((int)MathF.Floor(maxParam), 0, n - 1) : n - 1;
        float frac = clipped ? maxParam - lastIdx : 0f;
        bool tailPoint = frac > 1e-4f;
        if (clipped && lastIdx < 1 && !tailPoint) return false;   // 杩樻病闀垮埌绗簩涓偣

        // 婧愮偣璁块棶鍣細鍘熷閲囨牱鐐?/ 寤烘ā杈撳嚭锛坸, y, 鍘嬪姏锛変簩閫変竴銆?        float Px(int i) => useModel ? StrokeMotion.At(i).X : pts[i].X;
        float Py(int i) => useModel ? StrokeMotion.At(i).Y : pts[i].Y;
        float Pp(int i) => useModel ? StrokeMotion.At(i).Z : pts[i].P;

        startRadius = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, Pp(0)));
        float lastX = Px(0), lastY = Py(0), lastR = startRadius;

        // `--nosmooth`锛圫trokeSmoothing.Enabled=false锛夋椂鏁翠綋閫€鍥炴姌绾匡細
        // 妯″瀷閲忓嚭鏉ョ殑鐐瑰啀瀵嗭紝涔熶笉涓婃洸绾裤€傝繖鏍?寮€/鍏?鎵嶆槸涓€涓湁鏁堝鐓с€?        bool curveBranch = StrokeSmoothing.Enabled && (useModel || (!useModel && !s.RawWhileLive));
        Stroke.GeomTrace(s, "B", useModel ? "妯″瀷" : "鍘熷", curveBranch,
                         $"clipped={clipped} erased={s.Erased.Count} model={useModel} live={s.RawWhileLive} enabled={StrokeSmoothing.Enabled}");
        if (curveBranch)
        {
            // 鎶婃簮鐐癸紙鍘熷閲囨牱鐐?/ 寤烘ā杈撳嚭锛夊杺杩涜繃鐐规洸绾匡細寤烘ā杈撳嚭鏈潵宸茬粡鍘昏繃鎶栵紝
            // 鍐嶈繃涓€娆℃洸绾垮彧鏄负浜嗘秷鎺?杈撳嚭鐐逛箣闂寸殑鎶樼嚎"锛坢ean2 鐨勫揩鍐欐姌绾挎劅锛夈€?            StrokeSmoothing.Begin();
            for (int i = 0; i <= lastIdx; i++) StrokeSmoothing.Add(Px(i), Py(i), Pp(i));
            if (tailPoint)
            {
                int j = lastIdx + 1;
                StrokeSmoothing.Add(Px(lastIdx) + (Px(j) - Px(lastIdx)) * frac,
                                    Py(lastIdx) + (Py(j) - Py(lastIdx)) * frac,
                                    Pp(lastIdx) + (Pp(j) - Pp(lastIdx)) * frac);
            }
            int segs = StrokeSmoothing.Finish();
            if (segs <= 0) return false;
            EnsureInkSegs(segs);

            var cs = StrokeSmoothing.Out;
            float ema = StrokeSmoothing.PressureAt(0);
            for (int k = 0; k < segs; k++)
            {
                float pk2 = StrokeSmoothing.PressureAt(k + 1);
                ema += (pk2 - ema) * (pk2 > ema ? InkPressureEma : InkPressureEmaDown);
                float r1 = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, ema));
                _inkSegs[count++] = new InkBezierSegment
                {
                    Point1 = new Vortice.Direct2D1.InkPoint
                    {
                        X = cs[k].C1.X, Y = cs[k].C1.Y, Radius = lastR + (r1 - lastR) / 3f,
                    },
                    Point2 = new Vortice.Direct2D1.InkPoint
                    {
                        X = cs[k].C2.X, Y = cs[k].C2.Y, Radius = lastR + (r1 - lastR) * 2f / 3f,
                    },
                    Point3 = new Vortice.Direct2D1.InkPoint { X = cs[k].P1.X, Y = cs[k].P1.Y, Radius = r1 },
                };
                lastR = r1;
                lastX = cs[k].P1.X;
                lastY = cs[k].P1.Y;
            }
        }
        else
        {
            // 閲囨牱姝ラ暱锛氫繚璇佹鏁?鈮?涓婇檺锛屼笖**鏈€鍚庝竴鐐逛竴瀹氱敾鍒?*銆?            // 寤烘ā杈撳嚭鐨勭偣宸茬粡寰堝瘑锛堚墺180Hz锛夛紝涓婇檺缁欏ぇ寰楀鈥斺€旀寜 120 鎶戒細鎶婂垰骞虫粦鍑烘潵鐨勫集鎷夌洿銆?            int maxSegs = useModel ? InkMaxSegmentsModeled : InkMaxSegments;
            int stride = Math.Max(1, (int)MathF.Ceiling((n - 1) / (float)maxSegs));
            if (useModel) EnsureInkSegs(Math.Min(n, maxSegs));
            int cap = Math.Min(_inkSegs.Length, maxSegs);
            float sm = Pp(0);
            float sx = Px(0), sy = Py(0), sr = startRadius;
            for (int i = 1; i <= lastIdx && count < cap; i++)
            {
                float pi2 = Pp(i);
                sm += (pi2 - sm) * (pi2 > sm ? InkPressureEma : InkPressureEmaDown);   // 骞虫粦鍙綔鐢ㄤ簬鍘嬪姏锛屼笉鍔ㄤ綅缃?                if (i % stride != 0 && i != lastIdx) continue; // 涓棿鐨勬寜姝ラ暱鎶界█锛堟湯鐐瑰繀鐣欙級

                float ex = Px(i), ey = Py(i);
                float er = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, sm));
                // 鐩寸嚎娈靛啓鎴愪笁娆¤礉濉炲皵锛氭帶鍒剁偣钀藉湪涓ょ涔嬮棿 鈫?浣嶇疆鏄洿绾匡紝鍗婂緞娌块€旂嚎鎬ф彃鍊笺€?                //
                // 娉ㄦ剰**蹇呴』鍏ㄩ檺瀹?* `Vortice.Direct2D1.InkPoint`锛氭垜浠嚜宸变篃鏈変竴涓?                // `InkEngine.InkPoint`锛堢瑪杩圭殑閲囨牱鐐?X/Y/P/T锛夛紝鍐欒８鍚嶄細琚畠閬綇锛?                // 鎶ラ敊鏄?鏈寘鍚?Radius 鐨勫畾涔?鈥斺€斾竴涓笉瀹规槗涓€鐪肩湅鎳傜殑缂栬瘧閿欒銆?                _inkSegs[count++] = new InkBezierSegment
                {
                    Point1 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) / 3f, Y = sy + (ey - sy) / 3f,
                        Radius = sr + (er - sr) / 3f,
                    },
                    Point2 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) * 2f / 3f, Y = sy + (ey - sy) * 2f / 3f,
                        Radius = sr + (er - sr) * 2f / 3f,
                    },
                    Point3 = new Vortice.Direct2D1.InkPoint { X = ex, Y = ey, Radius = er },
                };
                sx = ex; sy = ey; sr = er;
                lastX = sx; lastY = sy; lastR = sr;
            }
            // 鍓嶇紑钀藉湪涓や釜閲囨牱鐐逛箣闂达細琛ヤ竴涓彃鍊肩粓鐐癸紙浣嶇疆/鍘嬪姏閮界嚎鎬ф彃锛夛紝
            // 涓嶇劧鍥炴斁鐨勭瑪灏栦細"涓€鏍间竴鏍艰烦"銆?            if (tailPoint && count < cap)
            {
                int j = lastIdx + 1;
                float ex = Px(lastIdx) + (Px(j) - Px(lastIdx)) * frac;
                float ey = Py(lastIdx) + (Py(j) - Py(lastIdx)) * frac;
                float piTail = Pp(lastIdx) + (Pp(j) - Pp(lastIdx)) * frac;
                sm += (piTail - sm) * (piTail > sm ? InkPressureEma : InkPressureEmaDown);
                float er = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, sm));
                _inkSegs[count++] = new InkBezierSegment
                {
                    Point1 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) / 3f, Y = sy + (ey - sy) / 3f,
                        Radius = sr + (er - sr) / 3f,
                    },
                    Point2 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) * 2f / 3f, Y = sy + (ey - sy) * 2f / 3f,
                        Radius = sr + (er - sr) * 2f / 3f,
                    },
                    Point3 = new Vortice.Direct2D1.InkPoint { X = ex, Y = ey, Radius = er },
                };
                lastX = ex; lastY = ey; lastR = er;
            }
        }

        if (count == 0) return false;

        // [鍒犻櫎 2026-10-05] 娓叉煋灏撅紙棰勬祴娈碉級鎷兼帴锛氶殢鑰侀娴嬬郴缁熺Щ闄ゃ€?        return true;
    }

    /// <summary>娈电紦鍐叉寜闇€澧為暱锛堟洸绾垮寲鏃朵竴鏉＄瑪鐨勬鏁板彲鑳借繙瓒?120锛夈€?/summary>
    private void EnsureInkSegs(int need)
    {
        if (_inkSegs.Length >= need) return;
        Array.Resize(ref _inkSegs, Math.Max(need, _inkSegs.Length * 2));
    }

    // ------------------------------------------------------------------
    //  Frame
    // ------------------------------------------------------------------

    public void RenderFrame(InkEngine app)
    {
        _app = app;
        s_frameNo++;

        // 鍥炴斁锛氭帹杩?鐜板湪婕斿埌鍝?蹇呴』**鎺掑湪 SyncTiles 涔嬪墠**銆?        //
        // 涓轰粈涔堬紙鐢ㄦ埛 2026-10-01 鎶?姣忓啓瀹屼竴绗旇繕鏄細闂竴涓?鐨勬牴鍥狅級锛氫竴鏉＄瑪杩?        // 鍑哄畬閭ｄ竴甯ц鍋氫袱浠朵簨鈥斺€旀妸鍓嶇紑鎾ゆ帀銆佹妸杩欐潯澧ㄨˉ杩涘唴瀹瑰眰鍒嗗潡銆備袱浠朵簨蹇呴』
        // **鍚屼竴甯?*瀹屾垚锛歍ickReplay 鎺掑湪 SyncTiles 鍚庨潰鏃讹紝杩欎竴甯у垎鍧楅噷杩樻病鏈夎繖鏉″ⅷ锛?        // 鑰屽墠缂€宸茬粡涓嶅啀鐢烩€斺€斿睆骞曚笂鏁存暣缂轰竴甯э紙灏辨槸閭ｄ釜"闂竴涓?锛夈€?        app.TickReplay();

        // 鍙屾洸绾夸笁姝ュ紡鐨勭涓€姝ワ細**鍙敾娓愯繎绾裤€佸厛涓嶇敾鏇茬嚎**锛堣 _auxOnlyStroke 閭ｆ璇存槑锛夈€?        // 姣忓抚鍦ㄨ繖閲岃涓€娆♀€斺€?*鍙湁鐪熸満娓叉煋杩欐潯璺細璁?*锛屽鍑?鍑哄浘閭ｆ潯璺笉璁撅紝
        // 鍏嶅緱鍗婃垚鍝佸湪瀵煎嚭鏃惰璇綋鎴?璇ラ殣钘忎富浣?鐨勫璞°€?        _auxOnlyStroke = app.HyperAsymptotePreviewOnly ? app.ActiveStroke : null;

        // 鐣岄潰锛氬厛甯冨眬銆佽閲嶇敾灏遍噸鐢讳竴娆★紝鎷垮埌杩欎竴甯х殑鐭╁舰銆?        bool uiVisible = PrepareUi(app);

        // The content layer must be rebuilt *before* the swap-chain target is
        // bound: switching targets in the middle of BeginDraw/EndDraw puts
        // Direct2D into an error state.
        if (!app.NoContentCache)
            SyncTiles(app);
        else
            SyncDocPages(app);      // 璋冭瘯璺緞涓嶇鍒嗗潡锛屼絾鏂囨。椤靛眰鐓ф牱瑕佺敓鎴?鍥炴敹/鎶ヨ剰

        // 鎬ц兘闈㈡澘鍏堢敾杩涜嚜宸辩殑缂撳瓨浣嶅浘锛堝繀椤诲湪缁戝悗缂撳啿銆丅eginDraw 涔嬪墠鍋氾級銆?        var swHud = Stopwatch.StartNew();
        PrepareHud(app);
        LastHudMs = swHud.Elapsed.TotalMilliseconds;
        LastHudRedrawMs = _hudRedrewThisFrame ? LastHudMs : 0;

        // 鍔ㄦ€佹鐨細鎸囬拡鍋滀綇涔嬪悗鎶婂昂瀵?*缂撻噴**鍥炲幓锛堟瘡甯ф帹涓€娆★紱缁抚鐢卞紩鎿庨偅杈圭殑 `_dirty` 淇濊瘉锛夈€?        app.TickEraserIdleDecay();

        _transientNow = ComputeTransientBounds(app);
        UpdateFrameDirty(app, uiVisible);

        var sw = Stopwatch.StartNew();
        _ctx.Target = _backBuffer;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;

        // 閫愬潡閲嶇粯銆備互鍓嶆槸"鍏ㄥ睆娓呭睆 + 鍏ㄥ睆璐村浘 + 鍏ㄥ睆涓婂睆"锛屼竴甯ц鍔?5.2M 鍍忕礌锛?        // 瀹炴祴杩欐墠鏄湪鏍告樉涓婂崰 8~9% GPU 鐨勭湡姝ｅ師鍥狅紙涓嶆槸涓婂睆锛孭resent1 涔嬪悗
        // 鑴忓尯鍙湁 0.1~0.8%锛孏PU 鍗翠竴鐐规病闄嶏級銆傜幇鍦ㄦ暣鏉＄绾块兘鍙姩鑴忓尯銆?        foreach (var raw in _frameDirty)
        {
            var c = ClipToWindow(raw);
            if (c.IsEmpty) continue;

            var box = new Vortice.RawRectF(c.MinX, c.MinY, c.MaxX, c.MaxY);
            _ctx.PushAxisAlignedClip(box, AntialiasMode.Aliased);

            // Clear() 涓嶅彈瑁佸壀褰卞搷锛屾墍浠ョ敤 Copy 娣峰悎鐨勫～鍏呮潵"鎿?杩欎竴鍧椼€?            // 锛堝崐閫忔槑鐧芥澘鐩栨枃妗ｆ椂鍨?鏂囨。鐨勭焊"锛岄〉鍥鹃殢鍚庣敾銆佹澘鑹插啀鐩栤€斺€旇 RasterizeTile 鐨勫悓娆炬敞閲娿€傦級
            bool boardOverDocTranslucentDbg =
                app.BoardOn && app.DocView.IsOpen && app.BoardOpacity < 0.999f;
            _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
            _ctx.FillRectangle(box, boardOverDocTranslucentDbg
                ? Brush(app.DocPaperColor) : BoardBrush(app));
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

            if (app.NoContentCache)
            {
                // Naive path: re-rasterise every committed stroke, every frame.
                _ctx.Transform = CanvasToWindow;
                // 娉ㄦ剰锛氳繖閲岃姣旂殑鏄?*鐢诲竷鐭╁舰**銆備互鍓嶇洿鎺ユ嬁绐楀彛鐭╁舰鍘绘瘮锛?                // 鐩告満涓?0 鏃剁湅涓嶅嚭闂锛屼竴婊氬姩灏卞叏閿欙紙杩欎篃鏄鏀惰繘"绫诲瀷鍖?                // 鍧愭爣"鐨勯偅涓€绫诲潙锛夈€?                var cv = new RectF
                {
                    MinX = c.MinX + OriginX - ViewOffsetX,
                    MinY = c.MinY + OriginY - ViewOffsetY,
                    MaxX = c.MaxX + OriginX - ViewOffsetX,
                    MaxY = c.MaxY + OriginY - ViewOffsetY,
                };
                DrawDocPages(app, cv);     // 璋冭瘯璺緞锛氭枃妗ｉ〉灞備篃瑕佺敾锛堝惁鍒欑櫧灞忕湅涓嶅埌鏂囨。锛?                if (boardOverDocTranslucentDbg)
                    _ctx.FillRectangle(box, BoardBrush(app));   // 鍗婇€忔槑鏉胯壊鐩栧湪椤典笂
                foreach (var s in app.Doc.Strokes)
                {
                    if (!s.PaddedBounds.Intersects(cv)) continue;
                    if (app.IsContentDetached(s)) continue;   // 鎷栧姩棰勮閭ｄ竴鎵圭敱娴姩灞傜敾
                    DrawStroke(s);
                }
            }
            else
            {
                // 鍐呭灞傦細鎶婂彲瑙佸垎鍧楁寜鐩告満璐村湪绐楀彛涓婏紙瀛愮煩褰㈡嫹璐濓紝涓嶉噸鐢荤瑪杩癸級
                CompositeTiles(c);
            }

            _ctx.Transform = CanvasToWindow;

            // 璋冭瘯鐢細--trailonly 鏃朵笉鐢昏嚜宸遍偅涓€绗旓紝鐢ㄦ潵楠岃瘉濮旀墭澧ㄨ抗杞ㄨ抗
            // 鏄笉鏄湡鐨勭敱绯荤粺鍚堟垚鍣ㄧ敾鍑烘潵浜嗐€?            // `--syswet`锛氳繖涓€绗旂殑婀垮ⅷ鐢辩郴缁熻建杩圭敾锛堜笖瀹冪湡鐨勫湪鐢伙級鈫?鎴戜滑涓嶈嚜缁橈紝
            // 鍚﹀垯涓ゆ潯鍙犲湪涓€璧凤紝绯荤粺杞ㄨ抗鐨勪綆寤惰繜浼樺娍鏍规湰鐪嬩笉瑙併€?            if (app.ActiveStroke != null && !app.SuppressActiveStroke
                && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
            {
                // 姝ｅ湪鍐欑殑閭ｄ竴绗斿嚑浣曟瘡甯ч兘鍦ㄥ彉锛岀敤瀹炵幇缂撳瓨鍙細涓嶅仠閲嶅缓锛屽弽鑰屾洿鎱?                DrawStroke(app.ActiveStroke);
                // 杩欎竴绗?*鎴戜滑鑷繁鐢讳笂灞忎簡** 鈫?Present 涔嬪悗鎶婅建杩归噷鍚屾牱杩欎竴娈靛垹鎺?                //锛堜笉鍒犵殑鍚庢灉瑙?TrimInkTrailAsDrawn 鐨勬敞閲婏紱2026-10-11 鐪熺瑪瀹炴祴鏁欒锛夈€?                _drewWetStrokeThisFrame = true;
            }
            if (app.PredictTailActive && !app.ActiveStroke.HiddenForPairing
                && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
                DrawPredictTail(app.ActiveStroke.Color, app.PredictTailWidth);

            // 鍥炴斁锛氭鍦?闀?鐨勯偅涓€鏉★紙鍓嶇紑鍑犱綍锛涘凡缁忓嚭瀹岀殑閮藉湪鍐呭灞傞噷浜嗭級
            if (app.ReplayActive) DrawReplayCurrent(app);

            DrawDragPreview(app);
            DrawVertexPreview(app);
            DrawSelection(app);
            // 鍥惧簱闈㈡澘锛氬畠浠庡伐鍏锋潯涓婃部寰€涓婇暱锛屽帇鍦ㄥ唴瀹逛箣涓婏紙涓嶅帇鐣岄潰鈥斺€旂晫闈㈡瘮瀹冩洿闈犱笅锛?            DrawLibraryPanel(app);
            // 鐢荤嚎涓殑 伪 璇绘暟鐢诲湪娴姩灞傛渶涓婇潰锛堝畠璐寸潃姝ｅ湪鎷栫殑閭ｄ竴绔紝鍘嬩綇浠€涔堥兘涓嶇浜嬶級銆?            DrawShapeInclination(app);
            DrawLaser(app);
            DrawRadialPalette(app);   // 鍛煎嚭鐩樺帇鐫€澧ㄥ拰婵€鍏夛紱钀界偣鍙嶉锛堜笅闈㈤偅涓€鍙ワ級鍦ㄥ畠涓婇潰
            DrawTouchEraseFeedback(app);   // 澶氭寚鎿︿細璇濈殑钀界偣锛堝崟鎸囨鐨蛋 DrawToolCursor 閭ｆ潯锛?            DrawToolCursor(app);
            DrawMarquee(app);

            _ctx.Transform = Matrix3x2.Identity;

        if (app.ShowHud)
        {
            var swBlit = Stopwatch.StartNew();
            DrawHud();
        DrawTouchHud(app);       // 瑙︾偣璇婃柇锛?.3.3锛岄粯璁ゅ叧锛?            LastHudMs += swBlit.Elapsed.TotalMilliseconds;
        }

        if (app.EraserHud) DrawEraserHud(app);   // 姗＄毊璇绘暟锛?.3.7锛岄粯璁ゅ叧锛涜皟鍙傜敤锛?
        // 婊氬姩鏉★紙鏍峰紡 B锛氫竴鏍圭粏绾匡級銆傜敾鍦ㄦ诞鍔ㄥ眰锛屼笉杩涘唴瀹瑰眰銆?        DrawScrollBar(app);

        // 搴曢儴閭ｄ袱鏉?PPT 鎺т欢锛堟斁鏄犳椂鎵嶇敾锛夈€?*蹇呴』鐢诲湪 `Matrix3x2.Identity` 涔嬩笅**锛?        // 瀹冧滑鐨勭煩褰㈡槸**灞忓箷鍧愭爣**锛堣创灞忓箷搴曡竟銆佸彧璺熷睆骞曡蛋锛夛紝鐢诲湪涓婇潰鐨勭敾甯冨彉鎹㈤噷
        // 浼氳鐩告満鏁翠綋骞崇Щ鈥斺€旂浉鏈轰竴婊氾紝鏉″氨璺熺潃璺戝亸锛堝拰 HUD銆佹粴鍔ㄦ潯鍚屼竴鎵癸紝鍚屼竴鐞嗙敱锛夈€?        DrawPptBar(app);

        // 鍥炴斁鎺у埗鏉★細鍜?PPT 鏉″悓涓€灞傦紙灞忓箷鍧愭爣銆乣Identity` 鍙樻崲涔嬩笅锛夛紝璐村睆骞曞簳杈瑰眳涓€?        DrawReplayBar(app);

        // 璇惧爞璁℃椂鍗＄墖锛氬悓涓€灞傦紙灞忓箷鍧愭爣锛夛紝榛樿椤堕儴灞呬腑锛涘弻鍑绘斁澶у悗鏄睆骞曚腑澶殑澶у瓧銆?        DrawTimerCard(app);

        // 璇惧爞鐐瑰悕鍗＄墖锛氬悓涓€灞傦紝榛樿灞忓箷涓ぎ鍋忎笂銆?        DrawRollCard(app);

        // 寮曟搸渚ф偓鍋滄彁绀猴紙閫変腑鎿嶄綔鏉?/ PPT 鏉′笌闀挎寜鑿滃崟锛夛細鐢诲湪鎵€鏈夋诞灞備箣涓娿€?        // 鎴浘鏁村眰涔嬩笅鈥斺€旀埅鍥炬椂 TooltipShown 鏈潵灏辨槸鍋囩殑锛堣繘鍏ユ埅鍥句細娓呮帀锛夈€?        DrawEngineTooltip(app);

        // 鎴浘鏁村眰锛?.3.0锛夛細閬僵/鍐荤粨甯?鍙栨櫙妗?鍑嗙嚎/璇绘暟/璋冩暣鎵嬫焺銆?        // 鏀惧湪鏈€鍚?= 鐩栦綇涓婇潰鎵€鏈変笢瑗匡紙婊氬姩鏉°€丳PT 鏉°€丠UD锛夛紱鐣岄潰閭ｄ竴鍧楀湪鎴浘鏈熼棿
        // 鐢卞紩鎿庢敹璧凤紙瑙?PrepareUi锛夛紝鎵€浠ヨ繖灏辨槸灞忓箷涓婄湅鍒扮殑鍏ㄩ儴銆?        // 鎴浘缁撴潫閭ｄ竴甯ч『鎵嬫妸鍐荤粨甯т綅鍥炬斁鎺夛紙瀹冧綅鍥鹃噷瀛樼潃鏁村睆鍍忕礌锛屼笉鐣欑潃鍗犳樉瀛?鍐呭瓨锛夈€?        if (app.CaptureActive) DrawCaptureOverlay(app, CanvasToWindow);
        else ReleaseFrozenBitmap();

        _ctx.PopAxisAlignedClip();
        }

        // 鐣岄潰鐢诲湪鎵€鏈夌瑪杩逛箣涓娿€傚畠姣忓抚鍙创涓€寮犵紦瀛樹綅鍥撅紝涓嶇敤閲嶇敾鍐呭銆?        if (uiVisible)
            DrawUi(app);

        var hr = _ctx.EndDraw();
        _ctx.Target = null;
        sw.Stop();

        LastRecordMs = sw.Elapsed.TotalMilliseconds;
        if (hr.Failure) LastError = "frame EndDraw: " + hr.Description;

        // 璁颁綇杩欎竴甯х晫闈㈢殑浣嶇疆锛屼笅涓€甯ч潬瀹冩妸"鍒氬垰娑堝け"鐨勭晫闈㈡摝骞插噣銆?        _uiBoundsPrev = uiVisible ? _uiBounds : RectF.Empty;
        // 鍥惧簱闈㈡澘鍚岀悊锛氫笅涓€甯х敤瀹冩妸鍒氬垰鍏虫帀鐨勯偅寮犲崱鐗囦粠灞忓箷涓婃摝鎺夈€?        _libraryRectPrev = app.LibraryPanelOpen
            ? CanvasRectToWindow(app.LibraryPanelRectNow()).Inflate(4f * Dpi / 96f)
            : RectF.Empty;
    }

    /// <summary>
    /// 姣忓抚閮戒細閲嶇粯鐨勫厓绱犵殑鍖呭洿鐩掞細姝ｅ湪涔﹀啓鐨勯偅涓€绗斻€佹縺鍏夎建杩广€佹鐨厜鏍囥€?    /// 妗嗛€夌煩褰€佹€ц兘闈㈡澘銆傚畠浠笉杩涘唴瀹瑰眰锛屾墍浠ユ瘡甯ч兘瑕侀噸鏂扮敾锛?    /// 涔熷氨蹇呴』姣忓抚閮界畻杩涜剰鍖恒€?    /// </summary>
    private RectF ComputeTransientBounds(InkEngine app)
    {
        var r = RectF.Empty;

        // 鑷粯鐨勮惤鐐瑰弽棣堬紙姗＄毊鏂规 / 鍦嗙幆 / 鍦嗙洏锛夛細姣忓抚閮介噸鐢伙紝鑰屼笖**涓€甯у彲鑳界Щ鍔ㄨ秴杩囧崐寰?*
        //锛堝揩鎿︼紱闈㈢Н鎿︾殑妗嗚繕浼氶殢閫熷害鍙樺ぇ鍙樺皬锛夈€傛墍浠ュ畠蹇呴』璧拌繖鏉?鏈€杩戜笁甯?鐨勯泦鍚堬紝
        // 涓嶈兘鍙畻褰撳抚鎴?褰撳抚锛嬩笂涓€甯?鈥斺€斿弻缂撳啿閲岃汉鐫€鐨勬槸**涓ゅ抚鍓?*鐨勭敾闈紙瑙佷笅闈?        // `_transientHistory` 閭ｆ潯娉ㄩ噴锛夈€傜敤鎴?2026-09-30 鎶ョ殑"闈㈢Н姗＄毊宸︿晶鍑虹幇涓€鏉℃潯
        // 缁嗗瘑鐨勭珫绾裤€侀鑹插儚鍙樻繁浜?灏辨槸杩欎釜锛氭棫妗嗗彧鏈変竴甯х殑瑕嗙洊锛岀涓夊抚閭ｄ竴鏉℃摝涓嶆帀銆?        if (app.DrawnCursor != InkEngine.ToolCursorShape.None)
        {
            float rad = app.DrawnCursorRadius;
            var c = RectF.Empty;
            c.Add(app.PointerX - rad, app.PointerY - rad);
            c.Add(app.PointerX + rad, app.PointerY + rad);
            r.Add(CanvasRectToWindow(c));
        }

        if (app.ActiveStroke != null)
        {
            r.Add(CanvasRectToWindow(app.ActiveStroke.PaddedBounds));
            // B4锛?026-10-08锛夛細棰勬祴灏惧張鍥炴潵浜嗏€斺€斿畠**浼稿湪绗斿皷鍓嶉潰**锛屽繀椤荤畻杩涜剰鍖猴紝
            // 鍚﹀垯琚鍓紙閭ｆ鏄?灏惧反鍦ㄧ敾銆佸睆骞曚笂鐪嬩笉瑙?鐨勬牴鍥狅級銆?            if (app.PredictTailActive)
            {
                var tb = RectF.Empty;
                foreach (var p in app.PredictTailPoints) tb.Add(p.X, p.Y);
                if (!tb.IsEmpty)
                    r.Add(CanvasRectToWindow(tb.Inflate(MathF.Max(2f, app.PredictTailWidth))));
            }
        }

        // 鍛煎嚭鐩橈紙Ctrl+Q锛夛細鍥哄畾鐢诲湪鐩樺績锛屼絾杞ㄨ抗绾胯窡鐫€鎸囬拡銆佸唴瀹归殢鎵囧尯鍙樷€斺€?        // 鑴忓尯鎸?鐩?锛?鎶曞奖 锛?鐩樹笅閭ｈ瀛?鈭?褰撳墠鎸囬拡"缁欙紙鐩樹竴杞€佺嚎涓€鍔紝鏃у儚绱犳墠鎿﹀緱鎺夛級銆?        if (app.RadialPaletteActive)
        {
            float rad = (96f + app.FloatingTheme.ShadowReachLogical + 4f) * app.DpiScale;
            var box = RectF.Empty;
            box.Add(app.RadialCenterX - rad, app.RadialCenterY - rad);
            box.Add(app.RadialCenterX + rad, app.RadialCenterY + rad);
            box.Add(app.RadialCenterX, app.RadialCenterY + rad + 56f * app.DpiScale);  // 鎮仠瀛楁潯
            box.Add(app.PointerX, app.PointerY);
            r.Add(CanvasRectToWindow(box));
        }

        var laser = app.Laser;
        if (laser.Visible)
        {
            // 杞ㄨ抗鏈夊搴﹀拰鍙戝厜锛屽線澶栫暀涓€鐐广€?            // 鈿?绯绘暟 1.7 鏄窡鐫€ `DrawLaser` 閭ｄ簲灞傛煍鍏夋潵鐨勶細鏈€澶栭偅灞傚崐寰?= 1.55 脳 涓诲甫鐩村緞
            //   = 1.55 脳 绮楃粏 脳 DPI锛岀暀 1.7 鍊嶆墠鏈夊瘜浣欙紙浠ュ墠鍙戝厜鍙埌 0.95 鍊嶏紝鎵€浠ラ偅鏃?            //   鍐欑殑鏄?1.0 鍊嶏紱鍙戝厜涓€鍔犲锛岃繖閲屼笉璺熺潃鍔犲灏变細**鐣欎笅娌¤閲嶇敾鐨勭孩杈?*锛夈€?            float pad = 32f + laser.MaxWidthLogical * app.DpiScale * 1.7f;
            var b = RectF.Empty;
            foreach (var s in laser.Strokes)
                foreach (var p in s.Points) b.Add(p.X, p.Y);
            if (!b.IsEmpty) r.Add(CanvasRectToWindow(b.Inflate(pad)));
        }

        // 鑷粯鐨勮惤鐐瑰弽棣堬紙姗＄毊鍦嗙幆 / 绗斿皷鐜?/ 鑽у厜绗斿渾鐩橈級銆傚畠姣斿浘褰㈡湰韬ぇ涓€鍦?        // 锛堟弿杈?+ 鍒诲害锛夛紝鑴忓尯瑕佽窡鐫€鏀惧ぇ锛屽惁鍒欏揩閫熷垝杩囦細鐣欎笅娈嬪奖銆?        //
        // 鐢诲竷鍧愭爣 鈫?绐楀彛鍧愭爣蹇呴』杩?CanvasRectToWindow锛氬渾鐜窡鐫€绗旇抗璧帮紝
        // 婊氬姩涔嬪悗涓よ€呮墠浼氫竴鑷达紙婕忎簡杩欎竴姝ワ紝婊氫笅鍘讳箣鍚庡渾鐜氨鎿︿笉骞插噣锛夈€?        if (app.DrawnCursor != InkEngine.ToolCursorShape.None)
        {
            float rad = app.DrawnCursorRadius;
            var c = RectF.Empty;
            c.Add(app.PointerX - rad, app.PointerY - rad);
            c.Add(app.PointerX + rad, app.PointerY + rad);
            var cur = CanvasRectToWindow(c);
            r.Add(cur);
            // **涓婁竴甯ч偅涓€浠戒篃瑕佸苟杩涙潵**锛堝悓鍥惧簱闈㈡澘 / PPT 鏉＄殑鍋氭硶锛夛細鎸囬拡涓€甯ч噷鍙兘绉诲姩
            // 瓒呰繃鍗婂緞锛堝揩鎿︼紱闈㈢Н鎿︾殑妗嗚繕浼氶殢閫熷害鍙樺ぇ鍙樺皬锛夛紝鍙畻褰撳墠浣嶇疆鐨勮瘽锛屾棫妗嗙暀鍦?            // 鍘熷湴鐨勯偅涓€鏉″氨鎴愪簡"鎿︿笉鎺夌殑绔栫嚎 / 鍙樻殫鐨勫甫瀛?鈥斺€旀鏄敤鎴锋姤鐨勯偅涓幇璞°€?            if (!_cursorRectPrev.IsEmpty) r.Add(_cursorRectPrev);
            _cursorRectPrev = cur;
        }
        else if (!_cursorRectPrev.IsEmpty)
        {
            // 钀界偣鍙嶉娑堝け浜嗭紙鎹㈠伐鍏?/ 鎸囬拡鍑虹晫锛夛細鏈€鍚庡啀鎿︿竴娆″畠鍘熸潵鐨勪綅缃€?            r.Add(_cursorRectPrev);
            _cursorRectPrev = RectF.Empty;
        }

        // 澶氭寚鎿︿細璇濓紙涓夋寚 / 鎵嬫帉锛夌殑钀界偣鍙嶉锛氳惤鐐?= 鎵€鏈夎Е鐐圭殑涓績锛屼竴涓?榧犳爣鍚屾"钀界偣锛?        // 姣忓抚閲嶇敾銆佷竴甯ч噷鍙兘绉诲姩寰堣繙鈥斺€旇剰鍖虹収涓婇潰 DrawnCursor 鍚屼竴濂楄处锛氬綋甯?鈭?涓婁竴甯э紱
        // 缁撴潫閭ｄ竴甯ф妸鏈€鍚庣殑浣嶇疆鍐嶆摝涓€娆★紙涓嶇劧鏃х殑鍦嗙幆/鏂规灏?鐣欏湪灞忓箷涓?锛夈€?        if (app.TouchEraseSessionActive && app.TouchEraseCenter(out var ec))
        {
            float pad = app.TouchEraseSessionPixel
                ? MathF.Max(app.PixelEraserCursorHalfWidthPx, app.PixelEraserCursorHalfHeightPx) + 8f
                : app.EraserRadius * 1.35f + 10f;
            var box = RectF.Empty;
            box.Add(ec.X - pad, ec.Y - pad);
            box.Add(ec.X + pad, ec.Y + pad);
            var cur = CanvasRectToWindow(box);
            r.Add(cur);
            if (!_touchEraseRectPrev.IsEmpty) r.Add(_touchEraseRectPrev);
            _touchEraseRectPrev = cur;
        }
        else if (!_touchEraseRectPrev.IsEmpty)
        {
            r.Add(_touchEraseRectPrev);
            _touchEraseRectPrev = RectF.Empty;
        }

        if (app.MarqueeActive)
        {
            var m = RectF.Empty;
            m.Add(app.MqMinX, app.MqMinY);
            m.Add(app.MqMaxX, app.MqMaxY);
            m = CanvasRectToWindow(m);       // 妗嗛€夌煩褰㈡槸鐢诲竷鍧愭爣锛岃剰鍖鸿绐楀彛鍧愭爣
            r.Add(m.Inflate(3f));
        }

        // 鎴浘锛?.3.0锛夛細鏁村睆閬僵 + 鍐荤粨甯ф寲娲?+ 鍑嗙嚎 + 鍙栨櫙妗嗭紝**姣忓抚閮藉湪鍔?*锛?        // 鎵€浠ュ彇鏅湡闂村共鑴嗘妸鏁翠釜绐楀彛杩涜剰鍖猴紙杩欐鏃堕棿鏈潵灏辨瘡甯у叏灞忛噸鐢伙紝涓€涓ょ鐨勪簨锛夛紱
        // **鏀跺満閭ｄ竴甯?*鍚屾牱瑕佹暣绐椻€斺€旀妸閬僵鎿﹀共鍑€銆?        if (app.CaptureActive || _captureWasActive)
            r.Add(new RectF { MinX = OriginX, MinY = OriginY, MaxX = OriginX + Width, MaxY = OriginY + Height });
        _captureWasActive = app.CaptureActive;

        // 鍥惧簱闈㈡澘锛氬紑鍚堛€佹偓鍋滈珮浜€佹暣鐞嗘ā寮忓垹鏍煎瓙閮戒細鍙橈紝鎵€浠?*姣忓抚鎸夊綋鍓嶇煩褰㈢畻杩涜剰鍖?*锛?        // 鍏虫帀涔嬪悗鏃т綅缃篃瑕佹摝骞插噣鈥斺€旀墍浠ヤ笂涓€甯ч偅浠界煩褰㈠悓鏍峰苟杩涙潵
        //锛堝拰鐣岄潰 `_uiBoundsPrev` 涓€妯′竴鏍风殑鍋氭硶锛氬彧绠楀綋鍓嶏紝灞忓箷浼氱暀涓€寮犳摝涓嶆帀鐨勫崱鐗囧嵃瀛愶級銆?        if (app.LibraryPanelOpen)
            r.Add(CanvasRectToWindow(app.LibraryPanelRectNow()).Inflate(4f * Dpi / 96f));
        if (!_libraryRectPrev.IsEmpty) r.Add(_libraryRectPrev);

        // 搴曢儴椤电爜鏉★紙鍚畠涓婇潰鐨勯〉鍙烽潰鏉?/ 鑿滃崟锛夛細鎮仠銆佹嫋鍔ㄦ瘡甯ч兘鍦ㄥ彉锛?        // 蹇呴』鎸夊綋鍓嶇煩褰㈢畻杩涜剰鍖猴紱**閫€鍑烘斁鏄?鍏虫枃妗ｉ偅涓€甯?*鏃т綅缃篃瑕佹摝鎺夛紝鎵€浠ヤ笂涓€甯?        // 閭ｄ唤鐭╁舰鍚屾牱骞惰繘鏉ワ紙鍚屽浘搴撻潰鏉裤€佺晫闈?`_uiBoundsPrev` 鐨勫仛娉曪級銆?        {
            var cur = RectF.Empty;
            if (app.PageBarVisible)
            {
                cur = app.PptBarRect();
                if (app.PptPagePanelOpen) { app.PptPanelRect(out var pp); cur.Add(pp.MinX, pp.MinY); cur.Add(pp.MaxX, pp.MaxY); }
                // 鑿滃崟鍜岄娆″紩瀵奸兘闀垮湪鏉＄殑涓婃柟锛堝紩瀵艰繕姣旇彍鍗曠獎涓€鐐癸紝鐢ㄥ悓涓€涓煩褰㈠厹浣忓氨澶燂級
                if ((app.PptMode && app.PptHintVisible) || app.PptMenuOpen)
                { app.PptMenuRect(out var pm); cur.Add(pm.MinX, pm.MinY); cur.Add(pm.MaxX, pm.MaxY); }
                float barPad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(barPad));
            }
            if (!_pptBarRectPrev.IsEmpty) r.Add(_pptBarRectPrev);
            _pptBarRectPrev = cur;
        }

        // 鍥炴斁锛氭帶鍒舵潯锛堟偓鍋?鎷栧姩/鏁板€奸兘鍦ㄥ彉锛夛紜 姝ｅ湪闀跨殑閭ｄ竴鏉＄殑鍓嶇紑鈥斺€旈兘姣忓抚鍦ㄥ彉锛?        // 蹇呴』鎸夊綋鍓嶇煩褰㈢畻杩涜剰鍖猴紱閫€鍑哄洖鏀鹃偅涓€甯ф棫浣嶇疆涔熻鎿︼紙鍚?PPT 鏉°€佸浘搴撻潰鏉跨殑濂楄矾锛夈€?        {
            var cur = RectF.Empty;
            if (app.ReplayActive)
            {
                cur = app.ReplayBarRect();
                float pad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(pad));
                int idx = app.ReplayCurrentIndexNow;
                if (idx >= 0 && idx < app.ReplayStrokesNow.Count)
                {
                    var b = app.ReplayStrokesNow[idx].PaddedBounds.Inflate(4f * Dpi / 96f);
                    r.Add(CanvasRectToWindow(b));
                }
            }
            if (!_replayBarRectPrev.IsEmpty) r.Add(_replayBarRectPrev);
            _replayBarRectPrev = cur;
        }

        // 璇惧爞璁℃椂鍗＄墖锛氳窇绉?/ 鍒扮偣闂儊 / 鎷栧姩閮藉湪鍙橈紝鎸夊綋鍓嶇煩褰㈢畻杩涜剰鍖猴紱
        // 鍋滀笅 / 鏀惰捣 / 鏀惧ぇ杩樺師閭ｄ竴甯ф棫浣嶇疆涔熻鎿︼紙鍚?PPT 鏉°€佸洖鏀炬潯鐨勫璺級銆?        {
            var cur = RectF.Empty;
            if (app.TimerCardOpen)
            {
                cur = app.TimerCardRect();
                float pad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(pad));
            }
            if (!_timerRectPrev.IsEmpty) r.Add(_timerRectPrev);
            _timerRectPrev = cur;
        }

        // 璇惧爞鐐瑰悕鍗＄墖锛氭粴鍔?鎷栧姩/寮€鍚堥兘鍦ㄥ彉锛涘叧鎺夐偅涓€甯ф棫浣嶇疆涔熻鎿︼紙鍚岃鏃跺崱锛夈€?        {
            var cur = RectF.Empty;
            if (app.RollCardOpen)
            {
                cur = app.RollCardRect();
                float pad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(pad));
            }
            if (!_rollRectPrev.IsEmpty) r.Add(_rollRectPrev);
            _rollRectPrev = cur;
        }

        // 寮曟搸渚ф偓鍋滄彁绀猴紙閫変腑鎿嶄綔鏉?/ PPT 鏉′笌闀挎寜鑿滃崟锛夛細鏄剧ず銆佹崲鍧椼€佹敹璧烽兘鍦ㄥ彉锛?        // 鎸夊綋鍓嶇煩褰㈢畻杩涜剰鍖猴紱**鏀惰捣閭ｄ竴甯?*鏃т綅缃篃瑕佹摝锛堝悓鍥惧簱 / PPT 鏉＄殑鍋氭硶锛夈€?        {
            var cur = RectF.Empty;
            if (app.TooltipShown)
            {
                cur = TooltipCardRect(app);
                float tipPad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                if (!cur.IsEmpty) r.Add(cur.Inflate(tipPad));
            }
            if (!_tipRectPrev.IsEmpty) r.Add(_tipRectPrev);
            _tipRectPrev = cur;
        }

        // 閫変腑楂樹寒鐢诲湪娴姩灞備笂銆佷笉杩涘唴瀹瑰眰锛屾墍浠ュ畠鐨勫尯鍩熷繀椤绘瘡甯х畻杩涜剰鍖恒€?        //
        // 娉ㄦ剰**涓嶈兘鍙畻瀵硅薄鑷繁鐨勫寘鍥寸洅**锛氶€変腑妗嗚繖涓€濂?UI 姣斿璞″ぇ鈥斺€?        // 澶栦晶鐨勫厜鏅曘€佽法鍦ㄨ竟绾夸笂鐨勬墜鏌勩€佷互鍙婁几鍒颁笂杈瑰渚х殑鏃嬭浆鎵嬫焺銆?        // 婕忔帀鍝竴鍧楋紝鍝竴鍧楀氨浼氬湪灞忓箷涓婄暀涓嬫摝涓嶆帀鐨勬畫褰憋紙瀹炴祴韪╄繃锛?        // 鏃嬭浆鎵嬫焺鏃佽竟鐣欎簡涓€灏忓潡绾㈣壊鐨勫墠甯ф畫鐣欙級銆?        if (app.Doc.Selected.Count > 0)
        {
            // 鎷栧姩棰勮鏈熼棿妗嗘槸"瀹炴椂"鐨勶紙妯″瀷杩樻病鍔紝瑙?LiveSelectionFrame锛夛細
            // 鑴忓尯蹇呴』璺熺潃瀹冭蛋锛屽惁鍒欐鍜屾墜鏌勪細鍦ㄦ柊浣嶇疆涓婄暀涓嬫摝涓嶆帀鐨勬畫褰便€?            var frame = app.LiveSelectionFrame;
            var sb = frame.CanvasAabb;
            float dpi = app.DpiScale;
            float margin = SelectionHandles.VisualSizeLogical * 0.5f * dpi + 6f;
            // 鎵嬫焺鐢诲湪**鏈€灏忔搷浣滄**涓婏紙灏忓璞℃椂姣旂湡瀹炴澶т竴鍦堬級锛岃剰鍖轰篃鎸夊畠绠椼€?            var ui = SelectionHandles.UiBox(sb, dpi).Inflate(margin);

            var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
            float grip = SelectionHandles.RotateGripLogical * 0.5f * dpi + 3f;
            ui.Add(rot.X - grip, rot.Y - grip);
            ui.Add(rot.X + grip, rot.Y + grip);
            r.Add(CanvasRectToWindow(ui));   // 閫変腑妗嗕篃鏄敾甯冨潗鏍囷紙瀹冭窡鐫€澧ㄨ抗璧帮級

            // 鎿嶄綔鏉″湪閫変腑妗嗕笅鏂癸紝涔熷繀椤荤畻杩涙潵锛屽惁鍒欏畠鑷繁浼氱暀涓嬫畫褰便€?            // **浣欓噺 = 4 锛?鎶曞奖鏈€杩滀几鍑哄灏?*锛氭搷浣滄潯鐜板湪甯︽姇褰憋紙涓婚缁欑殑锛夛紝
            // 鍙斁 4 鍍忕礌鐨勮瘽鎶曞奖浼氳鑴忓尯鍒囨帀銆佸湪灞忓箷涓婄暀涓€鏉″嵃瀛愩€?            float cardPad = 4f + app.FloatingTheme.ShadowReachLogical * dpi;
            r.Add(CanvasRectToWindow(SelectionHandles.BarRect(sb, dpi, app.ViewportCanvas).Inflate(cardPad)));

            // 鏀惰捣鎬佺殑鍦嗛挳 / 鎵撳紑鐨勯潰鏉?/ 鎻愮ず鏉★細閮芥槸"姣忓抚閮藉湪鍙?鐨勬诞鍔ㄥ眰锛?            // 婕忎竴鍧楀氨鍦ㄥ睆骞曚笂鐣欎竴鍧楁摝涓嶆帀鐨勬畫褰憋紙杩欐潯韪╄繃濂藉嚑娆′簡锛夈€?            r.Add(CanvasRectToWindow(SelectionHandles.BarCollapsedRect(sb, dpi, app.ViewportCanvas).Inflate(cardPad)));
            if (app.SelPanelOpen == SelPanel.Ink)
            {
                r.Add(CanvasRectToWindow(SelectionHandles
                    .PanelRect(sb, dpi, app.ViewportCanvas, SelectionHandles.SwatchCount)
                    .Inflate(cardPad + 2f)));
                if (app.CustomColorOpen)
                    r.Add(CanvasRectToWindow(SelectionHandles
                        .CustomPanelRect(sb, dpi, app.ViewportCanvas, SelectionHandles.SwatchCount)
                        .Inflate(cardPad + 2f)));
            }
            else if (app.SelPanelOpen == SelPanel.Layer)
                r.Add(CanvasRectToWindow(SelectionHandles.LayerPanelRect(sb, dpi, app.ViewportCanvas)
                    .Inflate(cardPad + 2f)));

            // 鏃嬭浆璇绘暟鏍囩璐村湪鏃嬭浆鎵嬫焺澶栦晶锛屾瘮閫変腑妗嗘湰韬繕楂樺嚭鍘讳竴鎴紝
            // 鍚屾牱蹇呴』杩涜剰鍖猴紱鎷栧姩涓畠姣忓抚閮藉湪鍔紝闈?_transientHistory 鍥炴函涓ゅ抚銆?            // **鏂囨瑕佸拰缁樺埗鐢ㄥ悓涓€涓嚱鏁?*锛氬崟閫夌洿绾挎椂瀹冩槸 `伪 = xx.x掳`銆佸埆鐨勫璞℃槸 `螖`锛?            // 涓よ€呭搴︿笉鍚屸€斺€旇剰鍖烘寜鍝釜鏂囨绠楋紝灏卞緱鐢诲摢涓枃妗堛€?            if (app.SelRotating)
                r.Add(CanvasRectToWindow(RotationReadoutRect(frame, dpi, CurrentRotationReadout(app)).Inflate(3f)));

            // 鎷栧畾涔夊厓绱?/ 鎷栧洓瑙掓椂鐨勮鏁拌兌鍥婏細鎸傚湪"姝ｅ湪鎷栫殑閭ｄ釜鍏冪礌"涓婃柟锛屾瘡甯ч兘璺熺潃鎸囬拡璧帮紝
            // 鑰屼笖瀹冨湪鍏冪礌澶栦晶銆佹牴鏈笉鍦ㄩ€変腑妗嗛噷鈥斺€旀紡浜嗚繖涓€鍧楀氨浼氬湪灞忓箷涓婄暀涓€琛屾畫褰便€?            // **鏂囨涓庣粯鍒跺悓涓€涓嚱鏁?*锛堝渾鐨?`r = / d =`銆佺洿绾跨殑 `伪 = `銆?            // 鍚镐綇鏃剁殑"绛夎竟/姝ｆ柟褰?瀹藉害閮戒笉涓€鏍凤級銆?            if (app.VertexDragging || app.ShapeSnapKind != ShapeSnapKind.None)
            {
                var pill = VertexPillRect(app);
                if (!pill.IsEmpty) r.Add(CanvasRectToWindow(pill.Inflate(3f)));
            }

            // 澶氳竟褰㈣鏍囷紙瑙勬牸 9.7锛夛細**鎷栭《鐐规椂**鎵嶆湁锛堜笁瑙掑舰 / 骞宠鍥涜竟褰㈤兘鏄級锛岃€屼笖姣忎竴棰?            // 閮芥寕鍦ㄩ《鐐瑰渚э紙妯潃璁╁埌杈圭殑澶栭潰锛夈€佹牴鏈笉鍦ㄩ€変腑妗嗛噷鈥斺€斾笉鍗曠嫭鍔犺繘鏉ュ氨浼氳鑴忓尯瑁佹帀锛?            // 灞忓箷涓婄暀涓嬪嚑琛屾摝涓嶆帀鐨勬暟瀛椼€?*鐩掑瓙鍜岀粯鍒惰蛋鍚屼竴涓嚱鏁?*锛團illAnglePills锛夛紝
            // 鎵€浠ュ畠璇磋繖涓€甯ф湁鍑犻锛岃繖閲屽氨鍔犲嚑鍧椼€?            for (int i = 0, np = FillAnglePills(app, _anglePills); i < np; i++)
                r.Add(CanvasRectToWindow(_anglePills[i].Rect.Inflate(3f)));
        }

        // 鐢荤嚎杩囩▼涓殑 伪 璇绘暟锛堢敤鎴?2026-09-18 鍥涙潯涔嬩竴锛夈€?        // **蹇呴』鍦?鏈夐€変腑瀵硅薄"閭ｅ潡涔嬪**锛氱敾绾跨殑鏃跺€欐病鏈夐€変腑瀵硅薄锛堟病鍋氱敾瀹岃嚜鍔ㄩ€変腑锛夛紝
        // 鎸傚湪閲岄潰鐨勮瘽杩欏潡鑴忓尯姘歌繙涓嶄細琚畻杩涙潵锛屾爣绛惧氨浼氬湪灞忓箷涓婄暀涓€琛屾畫褰便€?        if (app.ShapeInclinationActive)
            r.Add(CanvasRectToWindow(InclinationReadoutRect(
                app.ShapeInclinationAnchor, app.DpiScale,
                ShapeLineReadout(app)).Inflate(3f)));

        // 澶氱瑪鍥惧舰"杩欎竴绗斿惛鍒颁簡浠€涔?閭ｉ鑳跺泭锛堢幇鍦ㄥ彧鏈夋１鏌辩殑銆岀洿妫辨煴銆嶏級銆?        // **鍚屾牱蹇呴』鍦?鏈夐€変腑瀵硅薄"閭ｅ潡涔嬪**锛氱敾妫辨煴鐨勬椂鍊欎竴涓璞￠兘娌￠€変腑銆?        if (app.StepSnap != ShapeSnapKind.None)
        {
            var stepPill = StepSnapPillRect(app);
            if (!stepPill.IsEmpty) r.Add(CanvasRectToWindow(stepPill.Inflate(3f)));
        }

        if (app.ShowHud)
        {
            // 闈㈡澘鍖哄煙姣忓抚閮借杩涜剰鍖猴細鍚庣紦鍐查噷韬虹潃鐨勬槸涓ゅ抚鍓嶇殑鐢婚潰锛屼笉閲嶈创灏变細闂€?            float m = HudMarginLogical * HudScale;
            var h = RectF.Empty;
            h.Add(OriginX + m - 2, OriginY + m - 2);
            h.Add(OriginX + m + HudWidthPx + 2, OriginY + m + HudHeightPx + 2);
            r.Add(h);
        }

        if (app.TouchHud)
        {
            // 瑙︾偣璇婃柇锛?.3.3锛夛細宸︿笅瑙掔殑灏忔诞灞傦紝鍚屾€ц兘闈㈡澘鐨勯亾鐞嗏€斺€旀瘡甯ч兘瑕佽繘鑴忓尯銆?            var h = TouchHudRect(app);
            if (!h.IsEmpty) r.Add(h);
        }

        if (app.EraserHud)
        {
            // 姗＄毊璇绘暟锛?.3.7锛夛細鍚屼竴瑙掕惤锛屾瘡甯ч兘瑕佽繘鑴忓尯锛堟暟瀛椾竴鐩村湪鍙橈級銆?            var h = EraserHudRect(app);
            if (!h.IsEmpty) r.Add(h);
        }

        // 婊氬姩鏉＄敾鍦ㄥ彸杈圭紭锛岃€屼笖瑕佹贰鍑猴紝鎵€浠ョ敾鐨勯偅浜涘抚蹇呴』绠楄繘鑴忓尯锛?        // 鍚﹀垯瀹冩秷澶变箣鍚庝細鍦ㄥ睆骞曚笂鐣欎竴鏉℃摝涓嶆帀鐨勭嚎銆?        //
        // 鈿?**涓ゆ潯绾緥**锛?        //   鈶?瀹冩槸**鍗曠嫭涓€鍧?*锛屼笉鑳藉苟杩?`r`锛堥潤姝㈠浘鍏冪殑鑱斿悎鍖呭洿鐩掞級鈥斺€斿畠鏁村睆楂橈紝
        //      鍙褰撳墠鏈変竴绗斿湪灞忓箷宸﹀崐杈癸紝鑱斿悎鍖呭洿鐩掑氨浼氬彉鎴?浠庣瑪杩瑰埌灞忓箷鍙崇紭"锛?        //      鎶?绗旇抗宸﹁竟"鏁村潡鎺掗櫎鍦ㄨ剰鍖哄銆?026-10-04 鐢ㄦ埛鎶ョ殑"婊氬畬杞竴鎸夊氨闂?閿欎綅銆?        //      鍙湪宸︿晶"灏辨槸瀹冨厹鍑烘潵鐨勶紙閰嶅悎鐩告満鍙樺寲鍙噸鐢讳竴甯э級銆傚垎寮€涔嬪悗浜掍笉鎷栫疮銆?        //   鈶?**鍙湪瀹冪湡鐨勪細鐢诲嚭鏉ョ殑鏃跺€欐墠鍗犺剰鍖?*锛堟偓鍋?鎷栧姩/娣″嚭绐楀彛鍐咃級銆備笉鐢诲氨涓嶅崰锛?        //      杩欐牱"杩欎竴甯т粈涔堥兘瑕佷笉閲嶇敾"鏃?`_frameDirty` 鎵嶆槸鐪熺殑绌恒€佹墠鑳借蛋鏁村睆鍏滃簳
        //      锛堝榻?Windows Terminal锛氭病鏈夊け鏁堝氨涓?BeginPaint锛夈€?        //      娣″嚭缁撴潫閭ｄ竴甯ф棫浣嶇疆杩樿澶氭摝涓€娆★紝鎵€浠?UpdateFrameDirty 閲屼細杩炰笂涓€甯х殑鐭╁舰涓€璧峰姞銆?        bool sbShown = TryScrollBar(app, out _)
                       && (app.ScrollBarHover || app.ScrollBarDragging
                           || (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0 <= 3.5);
        _scrollbarRect = sbShown
            ? new RectF
              {
                  MinX = OriginX + Width - 30f * app.DpiScale,
                  MinY = OriginY,
                  MaxX = OriginX + Width,
                  MaxY = OriginY + Height,
              }
            : RectF.Empty;

        return r;
    }

    /// <summary>姹囨€昏繖涓€甯ц涓婂睆鐨勭煩褰氦缁?Present1銆?/summary>
    private void UpdateFrameDirty(InkEngine app, bool uiVisible)
    {
        _frameDirty.Clear();

        var full = new RectF
        {
            MinX = OriginX, MinY = OriginY,
            MaxX = OriginX + Width, MaxY = OriginY + Height,
        };

        // 鑴忓尯涔熺湅 **RenderDoc**锛堝洖鏀句腑鏄奖瀛愭枃妗ｂ€斺€斿畠鎵嶆槸鍐呭灞傝繖涓€甯х敾鐨勪笢瑗匡級銆?        // 鍙湅鐪熸枃妗ｇ殑璇濓紝鍥炴斁閲?鍒氬嚭瀹岀殑閭ｄ竴鏉?涓嶄細杩涗笂灞忚剰鍖猴紝璐村嚭鏉ョ殑鏄笂涓€甯с€?        var doc = app.RenderDoc;
        _contentDirtyNow.Clear();
        if (doc.Dirty.Full)
            _contentDirtyNow.Add(full);
        else
            foreach (var raw in doc.Dirty.Rects) _contentDirtyNow.Add(CanvasRectToWindow(raw));

        if (_fullFramesLeft > 0)
        {
            // 棣栧抚 / 鐩告満鍙樹簡 / 鏁村眰閲嶅缓 / 鎹㈠簳鑹诧細**杩炵画涓ゅ抚**鏁村睆閲嶇敾鈥斺€斾袱涓悗缂撳啿
            // 閮借鏇存柊鍒版柊鐘舵€侊紙瑙?_fullFramesLeft 鐨勮鏄庯紱鍙粰涓€甯у氨鏄?婊氬畬鎸変笅闂?鐨勬牴鍥狅級銆?            _frameDirty.Add(full);
            _contentDirtyPrev.Clear();
            _fullFramesLeft--;
        }
        else
        {
            // 杩欎竴甯у拰涓婁竴甯х殑鍐呭鏀瑰姩閮借閲嶇敾锛堝悗缂撳啿閲屾槸涓ゅ抚鍓嶇殑鐢婚潰锛?            AddClipped(_frameDirty, _contentDirtyPrev);
            AddClipped(_frameDirty, _contentDirtyNow);
            // 鏂囨。椤靛眰鍚岀悊锛氶〉鐢熸垚/鍥炴敹瑕佽鐩栬繖涓€甯у拰涓婁竴甯т袱澶?            AddClipped(_frameDirty, _pageDirtyPrev);
            AddClipped(_frameDirty, _pageDirtyNow);
        }

        // 銆?.4.3銆?.4.1 閭ｈ"闈㈢Н姗＄毊妗嗗彲瑙佹椂鏁寸獥閲嶇敾"宸茬粡**鎾ゆ帀**锛?        // 瀹冩槸鎺掓煡鏈熼棿鐨勪繚闄?瀵圭収瀹為獙锛岀湡姝ｇ殑鏍瑰洜鏄笅闈?`AddClipped` 閲岀殑**鍍忕礌瀵归綈**
        // 锛堝皬鏁拌竟鐣屼細璁╂瘡涓剰鍖虹煩褰㈢殑宸﹁竟缂樻紡涓€鍒楀儚绱狅級銆傜敤鎴?8.4.2 瀹炴祴纭骞插噣涔嬪悗锛?        // 灏辨病鏈夊繀瑕佷负瀹冧粯"姣忓抚鏁寸獥閲嶇敾"鐨勯挶浜嗭紙寮辨満涓婇偅鏄?20~40ms/甯х殑椤挎劅锛夈€?        // 涓囦竴浠ュ悗鍐嶇湅鍒伴偅绉?璺熺潃妗嗚蛋鐨勭珫绾?锛屽厛鍥炴潵鐪?`AddClipped`锛氶偅閲屾槸鍞竴涓€澶?        // 鍐冲畾"鍝簺鍍忕礌浼氳鎿﹀埌"鐨勫湴鏂广€?
        // 涓存椂鍥惧厓锛氳繖涓€甯?+ 鍓嶄袱甯э紙鍙岀紦鍐查噷韬虹潃鐨勬槸涓ゅ抚鍓嶇殑鐢婚潰锛?        var t = _transientNow;
        for (int i = 0; i < Math.Clamp(TransientHistoryFrames, 0, 2); i++)
            t.Add(_transientHistory[i]);
        if (!t.IsEmpty) AddClipped(_frameDirty, t);

        // 鍙崇紭婊氬姩鏉￠偅涓€鏉★細**鍗曠嫭鍔?*锛屼笉鍙備笌涓婇潰閭ｄ釜鑱斿悎鍖呭洿鐩掞紙鐞嗙敱瑙?ComputeTransientBounds锛夈€?        // 涓婁竴甯ч偅涓€浠戒篃瑕佸姞锛氬畠鍙兘鏄?娣″嚭缁撴潫銆佽繖涓€甯т笉鍐嶇敾"鐨勭涓€甯э紝灏戝姞涓€甯у氨鐣欎笅涓€鏉℃畫褰便€?        if (!_scrollbarRect.IsEmpty) AddClipped(_frameDirty, _scrollbarRect);
        if (!_scrollbarRectPrev.IsEmpty) AddClipped(_frameDirty, _scrollbarRectPrev);
        _scrollbarRectPrev = _scrollbarRect;

        // 鐣岄潰姣忓抚閮戒細閲嶆柊璐村埌鍚庣紦鍐蹭笂锛屾墍浠ュ畠閭ｅ潡鍖哄煙姣忓抚閮藉緱绠楄繘涓婂睆鐨勮剰鍖猴紝
        // 鍚﹀垯鍙岀紦鍐蹭竴浜ゆ崲锛岀晫闈㈠氨浼氶棯涓€涓嬫垨骞茶剢涓嶈浜嗐€?        // 鐣岄潰杩欎竴甯у拰涓婁竴甯у崰鐨勫湴鏂归兘瑕佺畻杩涙潵锛氱晫闈㈠彲鑳芥槸鍒氬垰娑堝け鐨?        // 锛堟瘮濡傜┛閫忔彁绀烘贰鍑恒€佽皟鑹叉澘鏀惰捣锛夛紝鍙岀紦鍐查噷杩樼暀鐫€瀹冪殑鏃х敾闈€?        if (uiVisible) AddClipped(_frameDirty, _uiBounds);
        if (!_uiBoundsPrev.IsEmpty) AddClipped(_frameDirty, _uiBoundsPrev);
        if (!_uiBoundsPrev.IsEmpty) AddClipped(_frameDirty, _uiBoundsPrev);

        // 鍏滃簳锛氱湡瑕佹槸涓€涓煩褰㈤兘娌℃湁锛屽氨鏁村睆鏉ヤ竴娆★紝閬垮厤鍑虹幇娌℃摝骞插噣鐨勭敾闈?        if (_frameDirty.Count == 0) _frameDirty.Add(full);

        if (Trace)
        {
            string R(RectF r) => r.IsEmpty ? "-" : $"({r.MinX:F0},{r.MinY:F0})..({r.MaxX:F0},{r.MaxY:F0})";
            string RL(List<RectF> l) => l.Count == 0 ? "-" : string.Join(",", l.ConvertAll(x => R(x)));
            Console.WriteLine($"    [dirtysrc] now={RL(_contentDirtyNow)} prev={RL(_contentDirtyPrev)} "
                              + $"trans={R(_transientNow)} h0={R(_transientHistory[0])} h1={R(_transientHistory[1])} "
                              + $"ui={R(_uiBounds)} uiprev={R(_uiBoundsPrev)} cursor={R(_cursorRectPrev)} uiVis={uiVisible}");
        }

        _transientHistory[1] = _transientHistory[0];
        _transientHistory[0] = _transientNow;

        _contentDirtyPrev.Clear();
        _contentDirtyPrev.AddRange(_contentDirtyNow);

        // 鏂囨。椤靛眰鍚屼竴濂楋細杩欎竴甯х殑鐣欑粰涓嬩竴甯у綋"涓婁竴甯?
        _pageDirtyPrev.Clear();
        _pageDirtyPrev.AddRange(_pageDirtyNow);
    }

    private void AddClipped(List<RectF> list, RectF r)
    {
        // **鍍忕礌瀵归綈**锛?.4.2 淇級锛氳剰鍖烘槸浠庣敾甯?閫昏緫鍧愭爣鎹㈢畻杩囨潵鐨勶紝杈圭晫甯稿甫灏忔暟
        // 锛圖PI 2.0銆佺浉鏈轰簹鍍忕礌鍋忕Щ銆佸鏁板昂瀵革級銆傚皬鏁拌竟鐣屼氦缁?`PushAxisAlignedClip` + `Copy`
        // 濉厖鏃讹紝**鏈€宸︼紙鏈€涓婏級閭ｄ竴鍒楀儚绱犲彲鑳藉彧琚杩涘幓涓€鍗娾€斺€旀摝涓嶆帀**锛屽睆骞曚笂灏辩暀涓嬩竴鏉?        // 绔栫嚎锛堟棫鍐呭锛夈€傜敤鎴?2026-09-30 鎶ョ殑涓ゆ潯姝ｆ槸瀹冿細
        //   路 "闈㈢Н姗＄毊宸︿晶濮嬬粓璺熺潃涓€鏍归粦绾?鈥斺€旀瘡涓剰鍖虹煩褰㈠悇婕忎竴鏉″乏杈圭紭锛?        //     鏁寸獥閲嶇敾鍚庡彧鍓╀竴涓煩褰紝鎵€浠?澶ф畫褰卞甫娌′簡銆佸彧鍓╀竴鏍?锛?        //   路 "鎮诞鏍忔敹璧峰睍寮€浼氭畫鐣欓粦绔栫嚎"鈥斺€斿悓涓€浠朵簨锛堢晫闈㈤偅鍧楄剰鍖猴級銆?        // 寰€澶栧彇鏁达紙floor 宸?涓娿€乧eil 鍙?涓嬶級涔嬪悗锛岃鍓鍜屼笂灞忕煩褰㈤兘钀藉湪鏁村儚绱犱笂銆?        var c = ClipToWindow(new RectF
        {
            MinX = MathF.Floor(r.MinX), MinY = MathF.Floor(r.MinY),
            MaxX = MathF.Ceiling(r.MaxX), MaxY = MathF.Ceiling(r.MaxY),
        });
        if (!c.IsEmpty) list.Add(c);
    }

    private void AddClipped(List<RectF> list, List<RectF> rects)
    {
        foreach (var r in rects) AddClipped(list, r);
    }

    /// <summary>
    /// 鎷栧姩棰勮锛氭墜鍔夸腑鎶婅"鎽樺嚭鍐呭灞?鐨勯偅涓€鎵规寜**瀹炴椂鍙樻崲**鐢诲湪鏈€涓婂眰銆?    ///
    /// 涓轰粈涔堣鏈夎繖涓€灞傦細鎷栧姩鏈熼棿妯″瀷涓€涓瓧閮戒笉鏀癸紙鎵€浠ュ唴瀹瑰眰閭ｄ簺鍧楀叏閮ㄦ湁鏁堛€?    /// 涓€甯ч兘涓嶇敤閲嶇敾锛屾渶鍧忓抚涓嶅啀闅?璧拌繃鐨勯潰绉?娑級锛屽疄鏃朵綅绉诲彧娲诲湪
    /// <see cref="InkEngine.DragPreviewMatrix"/> 閲岋紱鏃т綅缃垯鏄粠鍐呭灞傝创鍥炴潵鐨勶紝
    /// 鍍忕礌绾ц繕鍘熴€佷笉浼氶棯銆?    ///
    /// 寰楀埌鐨勪袱涓壇浣滅敤锛岄兘鏄兂瑕佺殑锛?    ///   路 琚嫋鐨勯偅鍧?*鑷姩娴捣鏉?*锛堝畠鐢诲湪鏁村眰鍐呭涔嬩笂锛夆€斺€斾笉鐢ㄥ幓鍘嬫殫鍛ㄥ洿锛?    ///   路 鎷栧姩鏈熼棿 z 搴忔殏鏃跺湪鏈€涓婂眰锛堟澗鎵嬪洖鍒版纭眰娆★紝杩欐槸"鎷栧姩棰勮"鐨勫父瑙勫彇鑸嶏級銆?    ///
    /// 鐢绘硶蹇呴』涓庡唴瀹瑰眰**鍍忕礌涓€鑷?*锛屽惁鍒欐澗鎵嬮偅涓€涓嬩細"璺?锛氳繖閲屽乏涔樼殑鐭╅樀姝ｆ槸
    /// 鍐呭灞備細鎶婂璞″彉鎹㈡垚鐨勯偅涓紙瀵硅薄鑷繁鐨勫彉鎹?脳 瀹炴椂鐭╅樀锛夛紝
    /// 鑷閲屾湁涓€鏉?鏉炬墜鍓嶅悗鍚屼竴娈靛ⅷ鐨勫ⅷ閲忎竴鑷?鐩潃瀹冦€?    /// </summary>
    private void DrawDragPreview(InkEngine app)
    {
        // **鍏堥棶"鎷栧姩棰勮鍒板簳娲荤潃娌℃湁"**锛圖ragPreviewActive锛夛紝涓嶈兘鍙湅"鎽樺嚭鍘讳簡鍑犳潯"锛?        // 鎷栫鐐圭殑鎵嬪娍澶嶇敤浜嗗悓涓€濂?鎽樺嚭鍘?鐨勬満鍒讹紙DetachForDrag锛夛紝浣嗛偅鏃剁敾鍦ㄦ诞鍔ㄥ眰涓婄殑
        // 鏄?*涓存椂鍑犱綍**锛圖rawVertexPreview锛夛紝涓嶆槸"杩欐壒瀵硅薄鎸夊疄鏃剁煩闃典綅绉?銆?        // 灏戜簡杩欎竴鍙ワ紝鎷栫鐐规椂浼氱敤**鍗曚綅鐭╅樀**鎶婇偅鏉℃棫绾垮啀鐢讳竴閬嶁€斺€斿睆骞曚笂灏辨槸"鏃х嚎涓嶅姩 +
        // 鏂扮嚎璺熺潃鎸囬拡"涓ゆ潯绾匡紝鑰屽唴瀹瑰眰鏄庢槑宸茬粡璺宠繃瀹冧簡锛?026-09-18 鍑哄浘鏍稿鏃舵姄鍒帮級銆?        if (!app.DragPreviewActive) return;

        var strokes = app.DragPreviewStrokes;
        if (strokes.Count == 0) return;

        // 瀹炴椂鐭╅樀**褰撴垚 extra 浼犱笅鍘?*锛岃€屼笉鏄杩?ctx锛歚KeepsWidth` 鐨勫浘褰?        //锛堢嚎瀹戒笉鍙橈紝瑙?Stroke.KeepsWidth锛夎鎶婅繖灞傜煩闃垫姌杩涘嚑浣曢噷锛涘杩?ctx 鐨勮瘽锛?        // 鎷栧姩杩囩▼涓嚎瀹戒細琚竴璧锋媺绮椼€佹澗鎵嬪張寮瑰洖鍘伙紙鐪嬬潃鍍忔姈涓€涓嬶級銆?        foreach (var s in strokes) DrawStroke(s, app.DragPreviewMatrix);
    }

    /// <summary>
    /// 鎷栫鐐规椂鐨?*涓存椂鍑犱綍**锛氭墜鍔挎湡妯″瀷涓€涓瓧涓嶆敼锛屽彧鍦ㄦ诞鍔ㄥ眰鎶?    /// "鏀硅繃绔偣鐨勯偅涓€鏉″浘褰?鎸?*瀹炴椂绔偣**鐢诲嚭鏉ワ紙瑙?璁″垝-鍥惧舰宸ュ叿.md 8.1鈶狅級銆?    ///
    /// 鍜屾嫋鍔ㄩ瑙堟槸鍚屼竴濂楄矾瀛愶紙鐢诲湪娴姩灞傘€佹澗鎵嬪洖褰掑唴瀹瑰眰锛夛紝鍖哄埆鍙湁涓€涓細
    /// 閭ｈ竟鏀圭殑鏄?*鍙樻崲鐭╅樀**锛岃繖閲屾敼鐨勬槸**鍑犱綍**鈥斺€旀墍浠ョ敾鐨勬槸鍚屼竴鏉″璞°€佹崲涓€缁勭偣锛?    /// 鍙樻崲浠嶇敤瀹冭嚜宸遍偅涓紙灞€閮ㄥ潗鏍?鈫?鐢诲竷锛夈€?    /// </summary>
    private void DrawVertexPreview(InkEngine app)
    {
        var src = app.VertexPreviewStroke;
        if (src == null) return;
        var pts = app.VertexPreviewPoints;
        if (pts == null || pts.Count == 0) return;

        _vertexGhost ??= new Stroke();
        var g = _vertexGhost;
        g.Tool = src.Tool;
        g.Kind = src.Kind;
        g.Color = src.Color;
        g.Width = src.Width;
        g.Dash = src.Dash;          // 绾垮瀷涔熻璺熺潃锛屼笉鐒舵嫋涓€鏉¤櫄绾挎椂棰勮浼氱獊鐒跺彉瀹炵嚎
        // **鏇茬嚎鏈濆悜涔熷繀椤昏窡鐫€**锛氭姏鐗╃嚎寮€浜嗗摢涓彛 / 鍙屾洸绾垮摢鏉℃槸瀹炶酱锛?        // 鏄繖涓璞″嚑浣曠殑涓€閮ㄥ垎銆傛紡浜嗗畠锛屾嫋鍔ㄩ瑙堜細鎸?榛樿閭ｄ竴妗?鐢伙紝
        // 灞忓箷涓婂氨鏄?涓€涓湞涓婄殑鎶涚墿绾块瑙堛€佹澗鎵嬪彉鎴愭湞鍙崇殑"銆?        g.CurveAxis = src.CurveAxis;
        // **娓愯繎绾垮紑鍏?/ 鐒︾偣涓夎褰?*涔熻璺熺潃锛?026-09-22锛夛細瀹冧滑鍚屾牱"鏄繖涓璞″嚑浣曠殑涓€閮ㄥ垎"
        // 鈥斺€旀紡浜嗙殑璇濓紝涓€鏉?鏃犳笎杩戠嚎"鐨勫弻鏇茬嚎鍦ㄦ嫋鍔ㄩ瑙堥噷浼氱獊鐒堕暱鍑轰袱鏉¤櫄绾匡紝鏉炬墜鍙堟病浜嗐€?        g.ShowAsymptotes = src.ShowAsymptotes;
        g.FocusTriangle = src.FocusTriangle;
        // P 鐨勪綅缃細鎷栧姩涓敤鐨勬槸**棰勮閭ｄ釜瑙?*锛堣 Engine.VertexPreviewFocusU锛夛紝
        // 娌″湪鎷?P 鏃讹紙NaN锛夊氨鐓фā鍨嬮噷閭ｄ釜锛堝彲鑳芥槸 NaN = 鑷姩鎽嗭紝浜ょ粰妯″瀷鑷繁鍒わ級銆?        g.FocusPointU = float.IsNaN(app.VertexPreviewFocusU) ? src.FocusPointU : app.VertexPreviewFocusU;
        // **缃戞牸**锛?026-09-24锛夛細寮€鍏宠窡鐫€锛屾牸璺濅篃瑕佽窡鐫€鈥斺€旀嫋閭ｉ鏍肩偣鎵嬫焺鏀圭殑姝ｆ槸鏍艰窛锛?        // 鍙湪鏉炬墜鍚庢墠鍙樼殑璇濓紝鎷栧姩鍏ㄧ▼灞忓箷涓婄殑鏍煎瓙绾逛笣涓嶅姩锛屾墜鎰熷氨鏄?杩欎竴鎷栦粈涔堜篃娌″彂鐢?銆?        g.Grid = src.Grid;
        g.AxisGridStep = app.VertexPreviewGridStep > 0f ? app.VertexPreviewGridStep : src.AxisGridStep;
        g.Transform = src.Transform;
        g.SetPoints(pts);           // 灞€閮ㄥ潗鏍囷細鍙樻崲閭ｄ竴灞備粛鐢?g.Transform 璐熻矗
        DrawStroke(g);
    }

    /// <summary>
    /// 鐢婚€変腑妗嗗拰鎵嬫焺銆傝鏍艰 design/閫変腑涓庢搷浣滄潯-璁捐绋?png锛堟柟妗?B锛夈€?    ///
    /// 涓夋潯璁捐绾︽潫锛岄兘璺熸暀瀹ゅ満鏅湁鍏筹細
    ///   路 **鐢诲湪娴姩灞備笂锛屼笉杩涘唴瀹瑰眰**銆傚惁鍒欐瘡鏀逛竴娆￠€夊尯灏辫閲嶇敾鏁村潡鍐呭灞傦紝
    ///     鑰岄€変腑鏄珮棰戞搷浣滐紙姣忔嫋涓€涓嬫閫夐兘瑕佸彉锛夈€?    ///   路 **鍏夋檿鏄繀椤荤殑锛屼笉鏄楗?*銆備竴鐩忔姇褰辨墦涓婂幓锛屾繁鑹?PPT 涓婁竴鏉＄函钃濈嚎
    ///     浼氱硦鎺夛紱鎻忚竟澶栦晶閭ｅ湀鍗婇€忔槑钃濊瀹冨湪娴呰壊鍜屾繁鑹茶儗鏅笂閮界珛寰椾綇銆?    ///   路 鎵嬫焺鐨?*浣嶇疆鍜屽懡涓垽瀹氶兘鍦?SelectionHandles 閲岀畻**锛堥偅涓€灞傚彧鏈夋暟瀛︼級銆?    ///     杩欓噷鍙礋璐ｇ敾鈥斺€旀崲瑙傛劅鏀硅繖閲岋紝鏀逛氦浜掕鍒欐敼閭ｈ竟锛屼簰涓嶇壍杩炪€?    ///
    /// 鎿嶄綔鏉★紙澶嶅埗/鍒犻櫎/缈昏浆/鏃嬭浆閭ｆ帓鎸夐挳锛夎繕娌＄敾锛岀瓑鍥炬爣璁捐瀹氱銆?    /// </summary>
    private void DrawSelection(InkEngine app)
    {
        var sel = app.Doc.Selected;
        if (sel.Count == 0) return;

        // 閫夊尯鍧愭爣绯伙細**涓€寰嬭酱瀵归綈**鈥斺€斾笉绠￠€変簡涓€鏉¤繕鏄鏉★紝妗嗛兘鏄睆骞曚笂閭ｄ釜姝ｇ煩褰?        // 锛堣 SelectionFrame 鐨勬敞閲婏級銆傛嫋鍔ㄩ瑙堟湡闂村彇"瀹炴椂妗?锛氭ā鍨嬭繕娌″姩锛?        // 浣嗘蹇呴』璺熺潃鍐呭璧帮紝鑰屼笖鏄瘡甯ч噸鏂拌创鍚堝綋鍓嶅唴瀹癸紙瑙?LiveSelectionFrame锛夈€?        var frame = app.LiveSelectionFrame;
        if (frame.IsEmpty) return;
        var b = frame.CanvasAabb;      // 鎿嶄綔鏉″拰鑴忓尯鐢ㄥ畠鐨勮酱瀵归綈鑼冨洿

        float dpi = app.DpiScale;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4

        // 鍗曢€変竴涓?鏈夊畾涔夊厓绱犵殑鍥惧舰"锛堢洿绾?/ 绠ご / 鍦?/ 妞渾锛夋椂锛屾墜鏌勬寜瀹氫箟鍏冪礌缁?        // 锛堣涓嬮潰绗?3銆? 姝ワ級锛氫袱涓垨浜斾釜锛岃€屼笉鏄€氱敤 8 涓€?        bool shapeLike = SelectionHandles.ShapeEditable(sel, out var shapeStroke);

        // **杩欎竴甯ц鐢诲嚑涓?瀹氫箟鍏冪礌鎵嬫焺"**锛? = 涓€涓兘娌℃湁锛夈€?        //
        // 鍒ゆ嵁鏄?**杩欎釜瀵硅薄鏈夋病鏈夌壒娈婃墜鏌?*"锛屼笉鏄?瀹冩槸涓嶆槸鍥惧舰" 鈥斺€?鍚庤€咃紙`shapeLike`锛?        // 瀵?*鍥涚鏇茬嚎**锛堟姏鐗╃嚎 / 鍙屾洸绾?/ 姝ｅ鸡 / 浣欏鸡锛夊拰**鍥涗釜绔嬩綋鍥惧舰**
        //锛堝渾鏌?/ 鍦嗛敟 / 闀挎柟浣?/ 鍥涢潰浣擄級涔熸垚绔嬶紝鑰屽畠浠竴涓壒娈婃墜鏌勯兘娌℃湁锛?        // 浜庢槸涓ゅご绌猴細鏃㈢敾涓嶅嚭鐗规畩鎵嬫焺锛屽張鎷夸笉鍒颁笅闈?else 閲岀殑**閫氱敤鍏墜鏌?*
        //锛堢缉鏀?/ 瀵硅鎷変几锛夈€傜敤鎴?2026-09-20 闂殑灏辨槸杩欎欢浜嬶細
        // "娌℃湁鐗规畩鐐圭殑鍥惧舰閫変腑浠ュ悗閭ｅ叓涓媺浼稿瑙掓媺浼告棆杞繖浜涙槸涓嶆槸搴旇閮界粰锛?鈥斺€?*璇ョ粰**銆?        Span<ShapeHandle> shapeHandles = stackalloc ShapeHandle[5];
        int nh = shapeLike ? SelectionHandles.ShapeHandlesOf(shapeStroke, shapeHandles) : 0;

        // 鎷栧姩 / 鏃嬭浆鏈熼棿鐨?*瑁呴グ鏀舵暃**锛堟柟妗?A锛夛細鎵嬫焺涓庢搷浣滄潯姝ゅ埢鐐逛笉涓?        // 锛堟寚閽堝凡琚嫋鎷芥帴绠★級锛岀暀鐫€灏辨槸"鐪嬪緱瑙併€佺偣涓嶅埌"锛岃繕璺熺潃鍐呭涓€璧锋檭銆?        //
        // 娉ㄦ剰涓変欢浜嬶細
        //   路 缂╂斁鎵嬪娍涓嶆敹锛堟嫋鎵嬫焺鏃跺埆鐨勬焺鏄弬鐓э紝瑙?SelChromeCollapsed锛夛紱
        //   路 鏃嬭浆鏃?*鐣?*鏃嬭浆鏌勪笌搴︽暟鏍囩锛氭焺鏍囩潃"鎴戞姄鐨勬槸杩欎釜"锛屽害鏁版槸璇绘暟锛?        //   路 涓嶇敾涓嶇瓑浜庝笉绠楋細杩欎簺鐭╁舰鐨勮剰鍖虹収鏃х敱 ComputeTransientBounds 缁欏嚭锛?        //     闈犱袱甯у洖婧妸鍒氭秷澶辩殑閭ｄ竴鍧楁摝骞插噣锛堣繖閲岃俯杩囧潙锛岃閭ｆ娉ㄩ噴锛夈€?        bool collapsed = app.SelChromeCollapsed;

        // 妗嗙殑鍥涗釜瑙掋€?*鎸夊洓涓杩炴垚鍥涜竟褰㈢敾**锛岃€屼笉鏄?DrawRectangle锛?        // 鐜板湪妗嗕竴寰嬭酱瀵归綈锛圱oCanvas 鏄崟浣嶉樀锛夛紝涓ょ鐢绘硶缁撴灉涓€鏍凤紝
        // 浣嗚繖鏍峰皢鏉ョ湡瑕佸姞"鏁寸粍鐨勬湞鍚?鏃讹紝杩欓噷涓€琛岄兘涓嶇敤鏀广€?        var c0 = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frame, dpi);
        var c1 = SelectionHandles.CanvasPosition(SelHandle.TopRight, frame, dpi);
        var c2 = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, dpi);
        var c3 = SelectionHandles.CanvasPosition(SelHandle.BottomLeft, frame, dpi);

        // **鏃嬭浆涓笉鐢昏繖涓**锛堢敤鎴?2026-09-25 璇曡繃 ClassIn 涔嬪悗瀹氾級锛欳lassIn 杞殑鏃跺€欐病鏈夌煩褰㈡銆?        // 涓や釜鐞嗙敱锛?        //   路 妗嗘槸**杞村榻?*鐨勶紝涓€杞氨姣忓抚閲嶆柊璐村悎鍐呭锛岃竟瑙掕窡鐫€鍐呭涓€璧疯烦 鈥斺€?鐪嬬潃鍍?妗嗗湪鎶?
        //     锛堢敤鎴凤細"鎴戜滑鐨勫鎺ョ煩褰㈡鍦ㄨ浆鐨勬椂鍊欐€绘湁鐐瑰皬 bug"锛夛紱鑰岃浆鐨勫綋涓嬫病浜虹湅妗嗭紝
        //     閮藉湪鐪嬪唴瀹硅浆鍒板摢涓搴︿簡锛堥偅涓搴︽湁涓撻棬鐨勫害鏁版爣绛撅紝瑙佺 6 姝ワ級銆?        //   路 灏戠敾涓€灞傦紝灏卞皯涓€澶?妗嗗拰鍐呭涓嶅悓姝?鐨勬満浼氥€?        // 鏉炬墜锛坄SelRotating` 钀藉洖 false锛変箣鍚庢鑷繁灏卞洖鏉ヤ簡 鈥斺€?浠€涔堥兘涓嶇敤琛ャ€?        //
        // 鍙敹"妗?杩欎袱姝ワ紙鍏夋檿 锛?鎻忚竟锛夛細鎵嬫焺閭ｄ竴鍦堟湰鏉ュ氨闅?`collapsed` 鏀惰捣鏉ヤ簡锛堣涓婇潰锛夛紝
        // 鏃嬭浆鏌勫拰搴︽暟鏍囩鍦ㄧ 3 / 6 姝ラ噷鍗曠嫭鍒ゆ嵁锛?*鐓ф棫鐣欑潃**锛堟鍒诲畠灏辨槸"鎴戞姄鐫€鐨勯偅涓笢瑗?锛夈€?        if (!app.SelRotating)
        {
            // 1) 鍏夋檿
            _scratch.Color = new Color4(accent.R, accent.G, accent.B, 0.16f);
            DrawQuad(c0, c1, c2, c3, 6.5f);

            // 2) 鎻忚竟銆備袱绉嶇壒娈婄姸鎬佸湪杩欓噷浣撶幇锛?            //    路 **澶嶅埗鎷栨嫿妯″紡**锛氭鐢绘垚铏氱嚎锛?鎸夌潃鎷?= 鎷栧嚭鍓湰"鐨勯€氱敤璇█锛夛紱
            //    路 **澶嶅埗鎴愬姛閭?0.25 绉?*锛氭鍔犵矖鍙樹寒锛岄棯涓€涓嬶紙鐢ㄦ埛鍙嶉"澶嶅埗浠ュ悗鐪嬩笉鍑烘潵"锛夈€?            if (app.SelFlashing)
            {
                _scratch.Color = new Color4(accent.R, accent.G, accent.B, 1f);
                DrawQuad(c0, c1, c2, c3, 5.5f);
            }
            else
            {
                _scratch.Color = accent;
                if (app.CopyDragArmed) DrawQuadDashed(c0, c1, c2, c3, 2.5f);
                else DrawQuad(c0, c1, c2, c3, 2.5f);
            }
        }

        // 3) 鏃嬭浆鎵嬫焺锛堝湪鍥惧舰鐨勪笂鏂瑰渚э紝鍏堢敾杩炵嚎鍐嶇敾鍦嗭級
        //    鎷栧姩涓敹璧锋潵锛屼絾**鏃嬭浆涓鐣?*锛堟鍒诲畠灏辨槸"姝ｅ湪鎶撶殑閭ｄ釜涓滆タ"锛夈€?        //    **鍦嗕笉缁欐棆杞焺**锛堢敤鎴峰畾锛?鍦嗚浆浜嗙湅涓嶅嚭鏉?锛夆€斺€旂敾閮戒笉鐢伙紝鍛戒腑涔熶竴鏍枫€?        var white = Brush(new Color4(1f, 1f, 1f, 1f));
        if (!collapsed || app.SelRotating)
        {
            if (!shapeLike || SelectionHandles.RotateHandleVisible(shapeStroke))
            {
                float rotR = SelectionHandles.RotateGripLogical * 0.5f * dpi;
                // 鏃嬭浆鏌勭殑浣嶇疆锛氱洿绾?绠ご/妞渾涓夌"瀹氫箟鍏冪礌鍥惧舰"涔熺敤**閫氱敤妗?*鐨勭畻娉?                // 锛堟涓婅竟涓偣澶栧啀鎶竴娈碉級鈥斺€斿畠鍙槸涓姄鎵嬶紝浣嶇疆璺熺潃妗嗚蛋鏈€绋筹紝涓嶇敤鍙︾畻涓€濂椼€?                var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
                // 杩炵嚎鐨勯偅涓€澶达紝鍜?缁欎笉缁欑壒娈婃墜鏌?**鍚屼竴鏉″垽鎹?*锛?                //   路 **鏈?*瀹氫箟鍏冪礌鎵嬫焺鐨勶紙鐩寸嚎 / 鍦?/ 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰⑩€︼級
                //     鈫?杩炲埌**瀹冭嚜宸辩殑涓績**锛堜竴鏉℃枩绾跨殑鍖呭洿鐩掍笂杈逛腑鐐规牴鏈笉鍦ㄧ嚎涓婏紝
                //       杩炵嚎鐪嬬潃鍍忚繛鍒板埆鐨勪笢瑗夸笂鍘讳簡锛夛紱
                //   路 **娌℃湁**鐨勶紙鐭╁舰 / 澧ㄨ抗 / 鍥惧儚 / 鍥涚鏇茬嚎 / 鍥涗釜绔嬩綋鍥惧舰锛?                //     鈫?鍜岄€氱敤妗嗕竴鏍凤紝杩炲埌**妗嗕笂杈逛腑鐐?*銆?                //     鍥涚鏇茬嚎鍜岀珛浣撳浘褰㈠師鏉ヨ惤杩涚殑鏄?鍥惧舰"閭ｄ竴鏀紝浜庢槸閭ｆ牴绾胯繛鍒颁簡
                //     `Points[0]` 涓?`Points[1]` 鐨勪腑鐐光€斺€斿鎶涚墿绾垮氨鏄?椤剁偣鍜岀粡杩囩偣鐨勪腑鐐?锛?                //     涓€涓窡鍥惧舰鐪嬭捣鏉ユ鏃犲叧绯荤殑鐐癸紙鐢ㄦ埛 2026-09-20锛?鎶涚墿绾跨殑鏃嬭浆鎵嬫焺绾?                //     杩炵殑鍦版柟寰堝鎬?锛夈€傚垽鎹崲杩囨潵涔嬪悗瀹冧滑鍜岀煩褰竴涓牱瀛愩€?                var topCenter = nh > 0
                    ? RotationGripAnchor(shapeStroke)
                    : SelectionHandles.CanvasPosition(SelHandle.Top, frame, dpi);
                _ctx.DrawLine(topCenter, rot, _scratch, 1.4f);
                _ctx.FillEllipse(new Ellipse(rot, rotR, rotR), white);
                _ctx.DrawEllipse(new Ellipse(rot, rotR, rotR), _scratch, 1.6f);
                // 鍦嗛噷鏀?*瀹樻柟鍥炬爣**锛屼笉鏄墜鐢讳竴娈靛姬銆?                // 鎵嬬敾閭ｇ増鍦ㄦ姇褰变笂鐪嬪儚涓?漏"鈥斺€旀棆杞浘鏍囩殑璇嗗埆鐗瑰緛灏辨槸閭ｄ釜绠ご锛?                // 灏戜竴绗斿氨涓嶆垚褰€傝繖鏄?鍥炬爣鍒嚜宸辩敾"鐨勫張涓€涓疄渚嬨€?                float glyph = SelectionHandles.RotateGlyphLogical * dpi;
                DrawIcon(IconPaths.rotate, rot.X - glyph * 0.5f, rot.Y - glyph * 0.5f, glyph, _scratch);
            }
        }

        // 4) 鎵嬫焺銆?*鍒ゆ嵁鍙湁涓€鏉★細杩欎釜瀵硅薄鏈夋病鏈?瀹氫箟鍏冪礌鎵嬫焺"**锛坄nh`锛岃涓婇潰閭ｆ锛夛細
        //    路 **鏈?*锛堝崟閫夌洿绾?/ 绠ご / 鍦?/ 妞渾 / 涓夎褰?/ 骞宠鍥涜竟褰?/ 鍧愭爣绯?/ 鏁拌酱锛夛細
        //      鎸?*瀹氫箟鍏冪礌**缁欌€斺€旂洿绾?绠ご/鍦嗕袱涓€佹き鍦嗕袱涓紙鍙崇鐐圭 a銆佷笂绔偣绠?b锛夈€?        //      涓夎褰?骞宠鍥涜竟褰笁涓《鐐广€佸潗鏍囩郴涓変釜銆? 涓缉鏀炬焺瀵瑰畠浠槸澶氫綑鐢氳嚦鏄敊鐨?        //      锛堝乏鍙虫媺浼镐竴鏉＄嚎浼氶『鎵嬫敼鎺夊€炬枩瑙掞紱鎷変竴涓渾浼氭妸瀹冩媺鎴愭き鍦嗭級銆?        //    路 **娌℃湁**锛堢煩褰?/ 鍥惧儚 / 鑷敱绗旇抗 / 澶氶€?/ **鍥涚鏇茬嚎** / **鍥涗釜绔嬩綋鍥惧舰**锛夛細
        //      **閫氱敤 8 鎵嬫焺**锛堝洓瑙?锛?鍥涜竟涓偣锛夈€傜煩褰繚鐣欏畠浠槸鍥犱负瀹冩槸鍞竴
        //      "鎷変簡杩樻槸鐭╁舰"鐨勫浘褰紱鏇茬嚎鍜岀珛浣撳浘褰㈡槸 2026-09-20 琛ヤ笂鐨?        //      鈥斺€斿湪閭ｄ箣鍓嶅畠浠袱澶寸┖锛堣涓婇潰 `nh` 閭ｆ锛夈€?        //    鐧藉簳 + 钃濊竟锛氭繁鑹茶儗鏅笂鏄櫧鏂瑰潡鏄剧溂锛屾祬鑹茶儗鏅笂闈犺摑杈圭珛浣忥紝涓€濂楃敾娉曚袱杈归兘鎴愮珛銆?        //    鎷栧姩 / 鏃嬭浆 / 鎷栧厓绱犱腑鏀惰捣鏉ワ紙姝ゅ埢鐐逛笉涓紝鑰屼笖鏄渶"鏅冪溂"鐨勪竴鍦堝鍏凤級銆?        if (!collapsed)
        {
            // 瑙嗚鏌?*璺熺潃瀵硅薄澶у皬缂?*锛堝皬瀵硅薄鍒鎵嬫焺鐩栦綇锛夛紝鍛戒腑鍗婂緞涓嶇缉鈥斺€?            // 鍒ゆ嵁涓庣悊鐢遍兘鍦?SelectionHandles.VisualHandleSize銆?            float hs = SelectionHandles.VisualHandleSize(frame.CanvasAabb, dpi);
            float radius = hs * 0.28f;
            if (nh > 0)
            {
                for (int i = 0; i < nh; i++)
                    DrawHandleSquare(SelectionHandles.ShapeHandleCanvasPosition(shapeStroke, shapeHandles[i]),
                                     hs, radius, white);
            }
            else
            {
                Span<SelHandle> all = stackalloc SelHandle[]
                {
                    SelHandle.TopLeft, SelHandle.Top, SelHandle.TopRight, SelHandle.Right,
                    SelHandle.BottomRight, SelHandle.Bottom, SelHandle.BottomLeft, SelHandle.Left,
                };
                // **鍜屽懡涓垽瀹氬悓涓€鎶婂昂瀛?*锛圫electionHandles.ThinEdges锛屽垽鎹彧鏈夐偅涓€浠斤級锛?                // 瀵硅薄鍦ㄦ煇鏂瑰悜姣旀墜鏌勫懡涓洿寰勮繕绐勬椂锛岄偅涓柟鍚戠殑杈逛腑鐐规焺**鐢讳篃涓嶇敾**銆?                // 鍛戒腑鏃㈢劧宸茬粡涓嶆敹瀹冿紝鐢诲嚭鏉ュ氨鍙槸"鐪嬪緱瑙佺偣涓嶅埌"锛涜€屼笖灏忓璞′笂鍏釜鏂瑰潡
                // 浼氭尋鎴愪竴鍥紙2026-10-05 鐢ㄦ埛锛?澧ㄨ抗鐗瑰埆灏忕殑鏃跺€欒繖涓浼氭尋鍦ㄤ竴璧?锛夈€?                var (thinV, thinH) = SelectionHandles.ThinEdges(frame.CanvasAabb, dpi);
                foreach (var h in all)
                {
                    if (thinV && h is SelHandle.Top or SelHandle.Bottom) continue;
                    if (thinH && h is SelHandle.Left or SelHandle.Right) continue;
                    DrawHandleSquare(SelectionHandles.CanvasPosition(h, frame, dpi), hs, radius, white);
                }
            }
        }

        // 5) 鎿嶄綔鏉★紙涔濇牸锛? 鏀惰捣鎬佺殑鍦嗛挳銆傛斁鍦ㄤ笅鏂癸紝鐞嗙敱瑙?DrawSelectionBar 鐨勬敞閲娿€?        //    鎷栧姩 / 鏃嬭浆涓敹璧锋潵锛氭鍒荤偣涓嶄腑锛涜€屼笖璐寸潃灞忓箷涓嬭竟鏃跺畠浼?*鍋滃湪鍘熷湴**锛?        //    鍐呭缁х画璧般€佹潯涓嶈窡鈥斺€旈偅鏄渶鍍忓崱姝荤殑涓€骞曪紙瑙?SelectionHandles.BarRect锛夈€?        //
        // 鈿?閭ｄ竴鍧楃敾涓嶇敾鐢?`SelectionBarShown` 瀹氾紱**鐢绘暣鏉¤繕鏄敾閭ｉ鍦嗛挳**鐢?        // `BarDrawnCollapsed` 瀹氾紙涓ょ鏀惰捣锛氱敤鎴疯嚜宸辨敹鐨勩€佺敾瀹岃嚜鍔ㄩ€変腑閭ｄ釜妗嗏€斺€旇閭ｉ噷鐨勬敞閲婏級銆?        if (!collapsed && app.SelectionBarShown)
        {
            if (app.BarDrawnCollapsed) DrawCollapsedDot(app, b);
            else DrawSelectionBar(app, b);
        }

        // 5.5) 娴姩闈㈡澘锛堥鑹?绮楃粏銆佸眰绾э級銆傜敾鍦ㄦ潯涔嬪悗锛岀洊鍦ㄥ唴瀹逛箣涓娿€?        //     鍜屾潯鍚屼竴鏉″垽鎹細鏁存潯涓嶅湪鐨勬椂鍊欙紝瀹冧笅闈㈡寕鐨勯潰鏉垮綋鐒朵篃涓嶅湪銆?        if (!collapsed && app.SelectionBarShown && !app.BarDrawnCollapsed)
        {
            if (app.SelPanelOpen == SelPanel.Ink) DrawInkPanel(app, b);
            else if (app.SelPanelOpen == SelPanel.Layer) DrawLayerPanel(app, b);
            // 鑷畾涔夊彇鑹叉澘鎸傚湪澧ㄨ抗闈㈡澘鏃佽竟锛堢敾鍦ㄩ潰鏉夸箣鍚?= 鍘嬪湪瀹冧笂闈級銆?            // 瀹冨彧鍦ㄥⅷ杩归潰鏉垮紑鐫€鏃舵墠鏈夋剰涔夛紙闈㈡澘涓€鍏筹紝瀛愰潰鏉跨敱寮曟搸涓€璧锋竻鎺夛級銆?            if (app.CustomColorOpen && app.SelPanelOpen == SelPanel.Ink) DrawPickPanel(app, b);
        }

        // 6) 娴嚭鐨勮鏁版爣绛俱€?*涓や釜鏁板垎寮€鏄剧ず**锛堣 璋冪爺-鍥惧舰宸ュ叿.md 2.3锛夛細
        //    路 螖 = 鎴戣繖涓€娆?*杞簡澶氬皯**锛堥€嗘椂閽堜负姝ｃ€佷笉璁句笂闄愶級鈫?鎷栨棆杞墜鏌勬椂鍑虹幇锛?        //      **鍗曢€夌洿绾?绠ご鏃朵緥澶?*锛氶偅鏃惰鐨勬槸 `伪鈧€ + 螖`锛堜粠杩欐潯绾垮師鏈夌殑鍊炬枩瑙掓帴鐫€杞€?        //      鍚屾牱涓嶈涓婇檺锛岀敤鎴?2026-09-18 婢勬竻锛夛紝鍐欐垚绾暟瀛楋紙瑙?CurrentRotationReadout锛夛紱
        //    路 伪 = 杩欐潯绾?*鏈韩**鐨勫€炬枩瑙掞紙[0掳,180掳)锛屾案杩滈潪璐燂級鈫?鐢荤嚎涓€佹嫋绔偣鏃跺嚭鐜般€?        //    涓や欢浜嬮兘蹇呴』璇存竻妤?杩欎釜瑙掑害鏄笉鏄惛鍑烘潵鐨?锛氶潬棰滆壊鈥斺€斿惛浣忔椂鏁村潡鍙樺己璋冭壊
        //    锛團igma / Office 涔熸槸杩欎釜璇█锛夈€傛病鏈夎繖灞傛彁绀猴紝鐢ㄦ埛鍒嗕笉娓?鎴戣嚜宸辨嫋鍒扮殑"
        //    鍜?瀹冩浛鎴戝惛涓婄殑"銆?        if (app.SelRotating)
        {
            // 鍚屼竴涓枃妗堬細鐩掑瓙鎸夊畠閲忓锛屽瓧涔熺敾瀹冦€?*鏀逛竴澶勫氨寰楁敼涓ゅ**鐨勫湴鏂规敹鎴愪竴涓嚱鏁?            // 锛圕urrentRotationReadout 涔熻 ComputeTransientBounds 鐢ㄦ潵绠楄剰鍖猴級銆?            string readout = CurrentRotationReadout(app);
            DrawReadoutPill(RotationReadoutRect(frame, dpi, readout), readout, app.SelRotationSnapped);
        }
        else if (app.VertexDragging || app.ShapeSnapKind != ShapeSnapKind.None)
        {
            // 鎷栫殑鏄?*瀹氫箟鍏冪礌 / 鍥涜**锛氱洿绾?绠ご璇?伪銆佸渾璇?r+d銆佹き鍦嗚 a 鎴?b锛?            // 鍚镐綇鐗规畩褰㈢姸鏃舵敼璇?鍚稿埌浜嗕粈涔?锛堣 VertexReadoutLabel锛夈€?            // 涓夎褰?/ 骞宠鍥涜竟褰㈡病鍚镐綇鏃?*浠€涔堥兘涓嶇敾**锛堝畠浠殑璇绘暟鏄笅闈㈢殑瑙掓爣閭ｄ竴缁勶級銆?            var pill = VertexPillRect(app);
            if (!pill.IsEmpty)
                DrawReadoutPill(pill, VertexReadoutLabel(app), VertexPillSnapped(app));
        }
        else if (app.StepSnap != ShapeSnapKind.None)
        {
            // **鐢诲绗斿浘褰㈡椂鍚镐綇鐨?*锛堢幇鍦ㄥ彧鏈夋１鏌辩 2 绗斿惛鍒般€岀洿妫辨煴銆嶏級銆?            // 鍜屾嫋椤剁偣鍚镐綇"绛夎竟 / 姝ｆ柟褰?鏄?*鍚屼竴棰楄兌鍥娿€佸悓涓€濂楄瑷€**锛?            // 鍚镐綇浜嗗氨鏁村潡鐢ㄥ己璋冭壊锛坄snapped: true`锛夆€斺€?杩欎釜缁撴灉鏄畠鏇挎垜鍚镐笂鐨?銆?            var pill = StepSnapPillRect(app);
            if (!pill.IsEmpty)
                DrawReadoutPill(pill, SelectionHandles.ShapeSnapLabel(app.StepSnap), true);
        }

        // 7) 澶氳竟褰㈣鏁帮紙瑙勬牸 9.7锛夛細涓夎褰㈢殑**涓変釜鍐呰**銆佸钩琛屽洓杈瑰舰鐨?*涓や釜澶硅**銆?        //    瀹冧笉鍦ㄤ笂闈㈤偅涓や釜鍒嗘敮閲岋紝鍥犱负杩欎竴缁勬槸**濂藉嚑棰?*瑙掓爣锛堟渶澶氫笁棰楋級锛屾湁涓€濂楄嚜宸辩殑
        //    "杩欎竴甯ф湁鍝簺"锛堣 FillAnglePills锛夈€?*涓や釜鍥惧舰閮藉彧鍦ㄦ嫋椤剁偣鏃跺嚭鐜?*
        //    锛堢敤鎴?2026-09-20 瀹氾細"涓夎褰㈠簲璇ュ湪鎷栧姩鐨勬椂鍊欏啀鏄剧ず瑙掑害锛岃涓嶇劧鐪嬭捣鏉ヤ篃涔?锛夈€?        DrawAnglePills(app);
    }

    /// <summary>
    /// 鏃嬭浆涓鏄剧ず鍝釜鏁帮細
    ///   路 **鍗曢€夌洿绾?绠ご** 鈫?`鎸変笅鏃剁殑鍊炬枩瑙?伪鈧€ + 杞繃鐨勮 螖`锛?*绾暟瀛椼€佷竴浣嶅皬鏁般€?    ///     涓嶆姌瑙?*锛坄405.0掳` / `-135.0掳` / `45.0掳`锛夈€傚啓鎴愮函鏁板瓧鑰屼笉鏄?`伪 = 鈥 鏄湁鎰忕殑锛?    ///     405掳 宸茬粡瓒呭嚭鍊炬枩瑙掔殑瀹氫箟鍩燂紙[0掳,180掳)锛夛紝鏍囨垚 伪 鍙嶈€岄敊锛涜€屼笖鐢ㄦ埛璇?    ///     "鍏朵粬鍜屽師鏉ョ殑閫昏緫涓€鏍?锛屽師鏉ラ偅濂楄鏁板氨鏄函鏁板瓧銆?    ///   路 **鍗曢€変竴涓浘褰?*锛堢煩褰?妞渾/涓夎褰?骞宠鍥涜竟褰級鈫?`濮挎€?= 0.0掳`锛氳繖涓浘褰?    ///     **鐩稿姘村钩鐨勫Э鎬佽**锛堟姌鍦?[0掳,180掳)锛?掳 = 姝ｇ殑銆?0掳 = 绔栫殑锛夈€傜敤鎴疯鐨勭敤閫旀槸
    ///     "姝殑妞渾/鐭╁舰锛岀湅鐫€璇绘暟鎷栧埌 0掳 灏辫浆姝ｄ簡"鈥斺€旀墍浠ュ畠鍜?伪 鍙槸鍚嶅瓧涓嶅悓銆侀暱鐩镐竴鏍枫€?    ///   路 **鍏跺畠鎯呭喌** 鈫?螖锛坄FormatDegrees`锛屾暣搴︼級锛氫竴琛岄兘娌℃敼銆?    /// **缁樺埗涓庤剰鍖洪兘璧板畠**锛屽厤寰椾袱杈规枃妗堜笉涓€鑷?    /// 锛堟枃妗堜笉涓€鑷?鈫?鐩掑瓙瀹藉害涓嶄竴鑷?鈫?鑴忓尯鐩栦笉浣忔爣绛?鈫?灞忓箷涓婄暀娈嬪奖锛夈€?    /// </summary>
    private static string CurrentRotationReadout(InkEngine app)
        => app.SelRotationReadsPose
            ? SelectionHandles.FormatPose(app.SelRotationPose)
            : app.SelRotationReadsInclination
                ? SelectionHandles.FormatSignedDegrees(app.SelRotationInclination)
                : RotationLabel(app);

    /// <summary>
    /// **鐢荤嚎杩囩▼涓殑 伪 璇绘暟**锛氱敤鎴峰湪鎷夎繖鏉＄嚎鐨勬椂鍊欏氨瑕佺湅鍒板畠鐜板湪鏄灏戝害
    /// 锛?026-09-18 鍥涙潯涔嬩竴锛夈€傝创鍦ㄨ鎷栧姩鐨勯偅涓€绔渚э紝鍜屾嫋绔偣鏃舵槸鍚屼竴棰楄兌鍥娿€?    ///
    /// 涓轰粈涔堝崟鐙竴涓柟娉曘€佷笉鎸傚湪 DrawSelection 閲岋細鐢荤嚎鐨勬椂鍊?*娌℃湁閫変腑瀵硅薄**
    /// 锛堢敾瀹岃嚜鍔ㄩ€変腑锛堣 Engine.EndStroke锛夊彂鐢熷湪**鏉炬墜涔嬪悗**锛屾媺绾跨殑杩欎竴璺笂閫夊尯杩樻槸绌虹殑锛夛紝
    /// DrawSelection 绗竴琛屽氨杩斿洖浜嗐€?    /// </summary>
    private void DrawShapeInclination(InkEngine app)
    {
        if (!app.ShapeInclinationActive) return;
        float dpi = app.DpiScale;
        string readout = ShapeLineReadout(app);
        DrawReadoutPill(InclinationReadoutRect(app.ShapeInclinationAnchor, dpi, readout),
                        readout, app.ShapeInclinationSnapped);
    }

    /// <summary>鐢荤嚎涓殑璇绘暟鏂囨锛?*伪 锛?闀垮害**锛堝悓涓€棰楄兌鍥婏紝鍜?伪 鍚屼竴涓?杈圭敾杈圭湅"閫昏緫锛?    /// 闀垮害鏄?2026-10-05 鐢ㄦ埛瑕佸姞鐨勶級銆傜粯鍒朵笌鑴忓尯蹇呴』鍏辩敤杩欎竴浠解€斺€斿垎寮€鍐欏氨浼氱暀涓嬫摝涓嶆帀鐨勮竟銆?/summary>
    private static string ShapeLineReadout(InkEngine app)
        => $"{InclinationLabel(app.ShapeInclinationDegrees)}  闀?{app.ShapeLength:F0}";

    /// <summary>
    /// 鏃嬭浆鏌勯偅鏍硅繛绾垮湪**鍥惧舰杩欎竴澶?*鎸傚摢鍎匡細
    ///   路 鐩寸嚎 / 绠ご锛氫袱涓鐐圭殑涓偣锛堟枩绾跨殑鍖呭洿鐩掍笂杈逛腑鐐规牴鏈笉鍦ㄧ嚎涓婏級锛?    ///   路 妞渾锛氬畠鐨?*涓績**锛堥偅鏄畠鑷繁鐨?涓績"锛屾鐨勪腑蹇冨湪鏃嬭浆鍚庝笉鏄畠锛夛紱
    ///   路 涓夎褰?/ 骞宠鍥涜竟褰細涓変釜锛堝洓涓級椤剁偣鐨?*褰㈠績**鈥斺€?澶氳竟褰㈣嚜宸辩殑涓績"灏辨槸瀹冿紝
    ///     鎷挎煇涓€涓《鐐瑰綋鎸傜偣浼氳閭ｆ牴绾跨湅鐫€鍍忔寕姝簡銆?    /// 鍦嗕笉缁欐棆杞焺锛岃蛋涓嶅埌杩欓噷銆?    /// </summary>
    private static Vector2 RotationGripAnchor(Stroke s)
    {
        if (s.Kind == StrokeKind.Ellipse || Stroke.IsSemiAxisEllipse(s.Kind)) return s.ShapeCenterLocal is var c
            ? Vector2.Transform(c, s.Transform) : Vector2.Zero;
        if (s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && s.Points.Count >= 3)
        {
            var sum = new Vector2(s.Points[0].X, s.Points[0].Y)
                    + new Vector2(s.Points[1].X, s.Points[1].Y)
                    + new Vector2(s.Points[2].X, s.Points[2].Y);
            if (s.Kind == StrokeKind.Parallelogram) sum += s.ParallelogramFourthLocal();
            float n = s.Kind == StrokeKind.Parallelogram ? 4f : 3f;
            return Vector2.Transform(sum / n, s.Transform);
        }
        var a = SelectionHandles.ShapeHandleCanvasPosition(s, ShapeHandle.Anchor);
        var r = SelectionHandles.ShapeHandleCanvasPosition(s, ShapeHandle.Rim);
        return (a + r) * 0.5f;
    }

    /// <summary>
    /// 鎷栧畾涔夊厓绱?/ 鎷栧洓瑙掓椂閭ｉ鑳跺泭鐨?*鏂囨**銆備簲绉嶉噺锛埼?/ r+d / a / b / 鍚稿埌浜嗕粈涔堬級
    /// 閮藉湪杩欓噷鎴愬瀷锛岃€屼笖**缁樺埗涓庤剰鍖洪兘璋冨畠**鈥斺€旀枃妗堜竴鍙樺绐勫氨鍙橈紝
    /// 鑴忓尯鎸夊摢涓枃妗堢畻灏卞緱鐢诲摢涓枃妗堛€?*娌℃湁鍙樉绀虹殑杩斿洖 null**锛堣繖涓€甯т笉鐢昏兌鍥婏級銆?    ///
    /// 浼樺厛绾э細**鐗规畩褰㈢姸鍚搁檮**锛堣鏍?9.6锛? 鑰佽鏁般€傜悊鐢辨槸"鍚稿埌浜嗕粈涔?鏄?*杩欎竴鍒绘渶鏈夌敤鐨?    /// 娑堟伅**鈥斺€斿畠鍥炵瓟"鎴戣繖涓€涓嬫嫋鍑轰簡涓粈涔堝舰鐘?锛岃€?伪 / a / b 鍙槸褰撲笅杩欎釜鏁般€?    /// </summary>
    private static string VertexReadoutLabel(InkEngine app)
    {
        if (app.ShapeSnapKind != ShapeSnapKind.None)
            return SelectionHandles.ShapeSnapLabel(app.ShapeSnapKind);
        return app.VertexReadout switch
        {
            // 鍦嗭細涓や釜鏁颁竴璧风粰锛堝崐寰勬槸"鎷夊澶?锛岀洿寰勬槸鑰佸笀鍢撮噷甯歌鐨勯偅涓暟锛?            InkEngine.VertexReadoutKind.Radius =>
                $"r = {app.VertexReadoutValue:F1}  d = {app.VertexReadoutSecondary:F1}",
            InkEngine.VertexReadoutKind.AxisA => $"a = {app.VertexReadoutValue:F1}",
            InkEngine.VertexReadoutKind.AxisB => $"b = {app.VertexReadoutValue:F1}",
            // 鎶涚墿绾匡細鎶ヨ鏈噷閭ｄ釜 p锛堢劍鐐广€佸噯绾块兘浠庡畠鏉ワ紝瑙?VertexReadoutKind 鐨勬敞閲婏級銆?            InkEngine.VertexReadoutKind.ParabolaP => $"p = {app.VertexReadoutValue:F1}",
            // 鍙屾洸绾匡細**璇存竻鏄疄鍗婅酱杩樻槸铏氬崐杞?*鈥斺€旀柟鍚戜竴鎹紝杩欎袱涓悕瀛椾細浜掓崲锛?            // 鍏夊啓涓?a / b 鑰佸笀浼氬湪鍙︿竴涓湞鍚戜笅鐪嬮敊锛堣繖鏄湰鏃忓敮涓€涓€澶?鍚嶅瓧浼氭紓"鐨勫湴鏂癸級銆?            InkEngine.VertexReadoutKind.HyperbolaReal => $"a = {app.VertexReadoutValue:F1}锛堝疄鍗婅酱锛?,
            InkEngine.VertexReadoutKind.HyperbolaImag => $"b = {app.VertexReadoutValue:F1}锛堣櫄鍗婅酱锛?,
            // 姝ｅ鸡 / 浣欏鸡锛氬嘲鐐?/ 璋风偣閭ｄ袱涓墜鏌勬槸**涓や釜閲忎竴璧峰姩**鐨?            //锛堢旱鍚?= 鎸箙銆佹í鍚?= 鍛ㄦ湡锛夛紝鎵€浠ヤ袱涓暟閮芥姤鍑烘潵銆?            InkEngine.VertexReadoutKind.WavePeriod =>
                $"T = {app.VertexReadoutValue:F1}  A = {app.VertexReadoutSecondary:F1}",
            // 鐩寸嚎/绠ご锛氬€炬枩瑙?伪锛圼0掳,180掳)锛夛紝鍜屾棆杞鏁?螖 鏄袱涓暟锛?            // 闀垮害鏄?2026-10-05 鍔犵殑绗簩涓暟锛堝悓 伪 涓€棰楄兌鍥婏級銆?            InkEngine.VertexReadoutKind.Inclination =>
                app.VertexReadoutSecondary > 0.01f
                    ? $"{InclinationLabel(app.VertexReadoutValue)}  闀?{app.VertexReadoutSecondary:F0}"
                    : InclinationLabel(app.VertexReadoutValue),
            // 涓夎褰?/ 骞宠鍥涜竟褰㈡病鍚镐綇锛氳繖涓€棰楄兌鍥婃病鏈夊彲鏄剧ず鐨勯噺鈥斺€斿畠浠鏄剧ず鐨勬槸
            // 鍐呰 / 澶硅锛岄偅鏄?*鍙︿竴缁?*瑙掓爣锛堣 DrawAnglePills锛岀鈶㈣疆锛夈€?            _ => null,
        };
    }

    /// <summary>
    /// 鎷栧畾涔夊厓绱?/ 鎷栧洓瑙掓椂閭ｉ鑳跺泭**鍗犵殑閭ｅ潡鐭╁舰**锛堢┖ = 杩欎竴甯у氨娌℃湁鑳跺泭锛夈€?    ///
    /// 缁樺埗涓庤剰鍖洪兘璧板畠锛氭枃妗堛€侀敋鐐广€佸搴︿笁澶?*蹇呴』鍚屾簮**鈥斺€斿垎寮€绠楃殑璇濓紝
    /// 鑴忓尯鎸変竴涓搴︾畻銆佺敾鎸夊彟涓€涓敾锛屽睆骞曚笂灏变細鍓╀笅涓€鏉℃摝涓嶆帀鐨勮竟銆?    /// 閿氱偣涔熷湪杩欓噷瀹氾細鍚镐綇鏃惰创"鍚镐綇鐨勯偅涓偣"锛屽惁鍒欒创琚嫋鐨勫厓绱犮€?    /// </summary>
    private RectF VertexPillRect(InkEngine app)
    {
        string text = VertexReadoutLabel(app);
        if (string.IsNullOrEmpty(text)) return RectF.Empty;
        var at = app.ShapeSnapKind != ShapeSnapKind.None
            ? app.ShapeSnapAnchor
            : app.VertexPreviewCanvasPoint;
        return InclinationReadoutRect(at, app.DpiScale, text);
    }

    /// <summary>杩欓鑳跺泭鏄笉鏄?鍚稿嚭鏉ョ殑"锛堝惛浣忔椂鏁村潡鍙樺己璋冭壊锛夈€?/summary>
    private static bool VertexPillSnapped(InkEngine app)
        => app.ShapeSnapKind != ShapeSnapKind.None || app.VertexInclinationSnapped;

    /// <summary>
    /// **澶氱瑪鍥惧舰"杩欎竴绗斿惛鍒颁簡浠€涔?**閭ｉ鑳跺泭鍗犵殑閭ｅ潡鐭╁舰锛堢┖ = 杩欎竴甯ф病鏈夎兌鍥婏級銆?    ///
    /// 鐜板湪鍙湁妫辨煴绗?2 绗斿惛鍒般€岀洿妫辨煴銆嶈繖涓€绉嶏紙瑙?`Engine.StepSnap`锛夈€?    /// 鍜?<see cref="VertexPillRect"/> 鍚屼竴濂楄鐭╋細**缁樺埗涓庤剰鍖洪兘璧板畠**锛?    /// 閿氱偣鍙?鍚稿畬涔嬪悗閭ｄ釜椤堕潰涓績"锛坄Engine.StepSnapAnchor`锛夆€斺€斾笉鑳借繖閲屽彟绠椾竴浠斤紝
    /// 涓よ竟宸竴甯х殑璇濆睆骞曚笂灏变細鐣欎竴鏉℃摝涓嶆帀鐨勮竟銆?    /// </summary>
    private RectF StepSnapPillRect(InkEngine app)
    {
        if (app.StepSnap == ShapeSnapKind.None) return RectF.Empty;
        string text = SelectionHandles.ShapeSnapLabel(app.StepSnap);
        if (string.IsNullOrEmpty(text)) return RectF.Empty;
        return InclinationReadoutRect(app.StepSnapAnchor, app.DpiScale, text);
    }

    // =====================================================================
    //  澶氳竟褰㈣鏁帮紙瑙勬牸 9.7锛夛細涓夎褰㈢殑涓変釜鍐呰 / 骞宠鍥涜竟褰㈢殑涓や釜澶硅
    //
    //  杩欎竴缁勪笉鏄?涓€棰楄兌鍥?锛屾槸**濂藉嚑棰?*锛堟渶澶氫笁棰楋級锛屾墍浠ュ彟鏈変竴濂?杩欎竴甯ф湁鍝簺鑳跺泭"
    //  鐨勬敹闆嗗嚱鏁帮細FillAnglePills銆?*鐢讳笌鑴忓尯閮藉彧璋冨畠涓€娆?*鈥斺€旂洅瀛愮殑浣嶇疆銆佸ぇ灏忋€佹枃妗?    //  鍏ㄥ湪閭ｄ竴浠介噷瀹氭锛屼袱杈瑰悇绠椾竴浠界殑璇濓紝鑴忓尯浼氭寜涓€涓ぇ灏忔摝銆佹寜鍙︿竴涓ぇ灏忕敾锛?    //  鎷栦箙浜嗗睆骞曚笂灏辩暀涓€鏉¤竟锛堥€変腑妗嗛偅涓€濂?UI 韪╄繃杩欎釜鍧戯級銆?    //
    //  **涓嶅啀鏈?鍐呰鍜?閭ｄ竴琛?*锛堢敤鎴?2026-09-19 瀹氾細鏄剧ず鍜屽お涔憋級銆備负瀹冩湇鍔＄殑
    //  0.1掳 閰嶅钩涔熶竴璧锋挙浜嗭紙瑙?SelectionHandles.PolygonAngles锛夈€?    // =====================================================================

    /// <summary>涓€甯ф渶澶氬嚑棰楄鏍囷細涓夎褰笁涓 / 骞宠鍥涜竟褰袱涓銆?/summary>
    private const int MaxAnglePills = 3;

    /// <summary>涓€棰楄鏍囷細鐩掑瓙 + 鏂囨 + 鏄笉鏄皬鍙凤紙鐢讳笌鑴忓尯鍏辩敤鍚屼竴涓疄渚嬶級銆?/summary>
    private readonly struct AnglePill
    {
        public readonly RectF Rect;
        public readonly string Text;
        public readonly bool Small;
        public AnglePill(in RectF rect, string text, bool small)
        {
            Rect = rect; Text = text; Small = small;
        }
    }

    /// <summary>瑙掓爣涓庤搴︾殑澶嶇敤缂撳啿锛堟瘡甯ч噸濉紝涓?new锛岀悊鐢卞悓"鎵嬫焺鍛戒腑姣忓抚閮借窇"閭ｆ潯锛夈€?/summary>
    private readonly Vector2[] _angleVerts = new Vector2[4];
    private readonly float[] _angleDegs = new float[3];
    private readonly AnglePill[] _anglePills = new AnglePill[MaxAnglePills];

    /// <summary>
    /// 杩欎竴甯ц鐢诲摢浜涜鏍囷紙瑙勬牸 9.7锛夛紝杩斿洖鍑犻銆?*缁樺埗涓庤剰鍖哄敮涓€鐨勬潵婧愩€?*
    ///
    /// 鎽嗕綅瑙勫垯锛氭瘡涓鏍囪创鍦ㄥ畠閭ｄ釜椤剁偣鐨?*澶栦晶**锛岃€屼笖**鍙線宸﹀彸鏀?*锛堜笉寰€涓婁笅鏀撅級銆?    /// 涓や釜鐞嗙敱锛岄兘鏄鐜板満閭ｄ袱鏍峰鍏烽€煎嚭鏉ョ殑锛?    ///   路 姝ｄ笂鏂归偅涓€鍧楁槸**鏃嬭浆鏌?*鐨勶紙妗嗕笂杈逛腑鐐瑰線涓?30 閫昏緫鍍忕礌锛屾焺 + 杩炵嚎閮藉湪閭ｅ効锛夛紱
    ///   路 姝ｄ笅鏂归偅涓€鍧楁槸**鎿嶄綔鏉?*鐨勶紙妗嗕笅杈瑰線涓?14 閫昏緫鍍忕礌锛岃€屼笖妯法鏁翠釜閫夊尯瀹藉害锛夈€?    /// 宸﹀彸鏀捐繕椤哄甫淇濊瘉浜?涓嶉伄浣忚璁茬殑閭ｆ潯杈?閭ｄ釜椤剁偣"锛氶《鐐瑰氨鏄浘褰㈠湪閭ｄ竴渚х殑鏈€澶栫偣锛?    /// 璁╁埌瀹冨闈㈠幓锛屼袱鏉¤竟鍜岄《鐐归兘鍦ㄨ閲庨噷銆?    ///
    /// 澶у皬锛氫竴寰嬪皬鍙峰瓧锛堣创鍦ㄩ《鐐规梺杈癸紝澶ぇ浜嗕細鍘嬩綇閭ｆ潯杈癸級銆?    /// </summary>
    private int FillAnglePills(InkEngine app, Span<AnglePill> dst)
    {
        int n = app.FillAngleReadout(_angleVerts, _angleDegs);
        if (n <= 0 || dst.Length < n) return 0;

        float dpi = app.DpiScale;
        var small = ReadoutFormatSmall(dpi);

        // 褰㈠績锛氬垽"杩欎釜椤剁偣鍦ㄥ浘褰㈢殑鍝竴渚?锛屽喅瀹氳鏍囧線宸﹁繕鏄線鍙虫斁銆?        // 锛堜笁瑙掑舰鐢ㄤ笁涓《鐐癸紱骞宠鍥涜竟褰㈠彧鎶ヤ袱瑙掋€佸氨鎸夐偅涓や釜椤剁偣鐨勪腑鐐瑰垎宸﹀彸锛屽鐢ㄣ€傦級
        var centroid = Vector2.Zero;
        for (int i = 0; i < n; i++) centroid += _angleVerts[i];
        centroid /= n;

        for (int i = 0; i < n; i++)
        {
            string text = SelectionHandles.FormatAngleDegrees(_angleDegs[i]);
            var place = _angleVerts[i].X >= centroid.X ? PillPlace.RightOf : PillPlace.LeftOf;
            dst[i] = new AnglePill(
                ReadoutPillRect(_angleVerts[i], dpi, text, 22f, 44f, small, place), text, true);
        }
        return n;
    }

    /// <summary>鐢婚偅涓€缁勮鏍囷紙鐩掑瓙涓庢枃妗堟潵鑷?<see cref="FillAnglePills"/>锛屽拰鑴忓尯鍚屼竴浠斤級銆?/summary>
    private void DrawAnglePills(InkEngine app)
    {
        int n = FillAnglePills(app, _anglePills);
        for (int i = 0; i < n; i++)
            DrawReadoutPill(_anglePills[i].Rect, _anglePills[i].Text, false, _anglePills[i].Small);
    }

    /// <summary>
    /// 鍊炬枩瑙掓爣绛剧殑鏂囨锛堢敾瀹冦€佹寜瀹冮噺鐩掑瓙瀹藉害銆佽繘鑴忓尯锛岄兘璧拌繖涓€涓嚱鏁帮級銆?    ///
    /// 鍙傛暟鏄?*閭ｄ釜瑕佹樉绀虹殑 伪**锛堢敾绾夸腑 / 鎷栫洿绾跨鐐?/ 鍗曢€夌洿绾挎嫋鏃嬭浆鏌勪笁涓潵婧愶級鈥斺€?    /// 涓嶅湪杩欓噷璇?app 鐨勭姸鎬侊紝鏄洜涓?璇ユ樉绀哄摢涓潵婧愮殑 伪"鐢辫皟鐢ㄧ偣鏈€娓呮銆?    /// </summary>
    private static string InclinationLabel(float degrees)
        => SelectionHandles.FormatInclination(degrees);

    /// <summary>
    /// 涓€涓柟褰㈡墜鏌勶紙鐧藉簳 + 钃濊竟锛夈€傚叓涓€氱敤鎵嬫焺鍜屼袱涓鐐规墜鏌?*鍏辩敤杩欎竴浠界敾娉?*锛?    /// 绔偣鎵嬫焺鍙槸浣嶇疆涓嶅悓锛岄暱寰椾笉涓€鏍蜂細璁╃敤鎴蜂互涓烘槸涓ょ涓滆タ銆?    /// </summary>
    private void DrawHandleSquare(Vector2 p, float size, float radius, ID2D1SolidColorBrush white)
    {
        var box = new Vortice.RawRectF(p.X - size * 0.5f, p.Y - size * 0.5f,
                                       p.X + size * 0.5f, p.Y + size * 0.5f);
        var rr = new RoundedRectangle(box, radius, radius);
        _ctx.FillRoundedRectangle(rr, white);
        _ctx.DrawRoundedRectangle(rr, _scratch, 1.8f);
    }

    /// <summary>
    /// 鐢讳竴棰?璇绘暟鑳跺泭"锛堟棆杞?螖 涓庡€炬枩瑙?伪 鍏辩敤杩欎竴浠界敾娉曪級銆?    ///
    /// 鎶藉嚭鏉ユ槸鍥犱负涓ゅ蹇呴』**闀垮緱涓€妯′竴鏍?*锛氬畠浠細鍦ㄥ悓涓€鏉＄洿绾夸笂鍏堝悗鍑虹幇
    /// 锛堝厛杞竴涓嬨€佸啀鎷栫鐐癸級锛屾牱寮忓樊涓€鐐圭敤鎴峰氨浼氫互涓烘槸涓ょ涓滆タ銆?    /// <paramref name="snapped"/> 涓虹湡鏃舵暣鍧楃敤寮鸿皟鑹测€斺€?杩欎釜鏁版槸鍚稿嚭鏉ョ殑"銆?    /// <paramref name="small"/> 涓虹湡鏃剁敤灏忎竴鍙峰瓧锛堜笁瑙掑舰鐨勪笁涓?*瑙掓爣**锛?    /// 瀹冧滑璐村湪椤剁偣鏃佽竟锛屽お澶т簡浼氬帇浣忛偅鏉¤竟锛夈€?    /// </summary>
    private void DrawReadoutPill(in RectF label, string text, bool snapped, bool small = false)
    {
        // **杩欓噷瑕佺殑鏄?缂╂斁鍊嶆暟"锛?6 DPI 鍩哄噯锛夛紝涓嶆槸 Dpi 閭ｄ釜鍘熷鍊?*锛?        // Dpi 鏄獥鍙ｇ殑鐗╃悊 DPI锛?00% 灞忎笂灏辨槸 192锛夛紝鎷垮畠鍘?CreateTextFormat
        // 浼氶€犲嚭涓€涓?2880 鍍忕礌楂樼殑瀛楀彿锛屽瓧鐩存帴绯婃弧鏁翠釜鐢婚潰锛堣俯杩囷級銆?        float dpi = _app.DpiScale;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4
        var box = new Vortice.RawRectF(label.MinX, label.MinY, label.MaxX, label.MaxY);
        float pill = (label.MaxY - label.MinY) * 0.5f;
        var shape = new RoundedRectangle(box, pill, pill);

        _ctx.FillRoundedRectangle(shape, Brush(snapped
            ? new Color4(accent.R, accent.G, accent.B, 0.96f)
            : _app.FloatingTheme.Panel));
        _scratch.Color = snapped
            ? new Color4(1f, 1f, 1f, 0.85f)
            : new Color4(accent.R, accent.G, accent.B, 0.85f);
        _ctx.DrawRoundedRectangle(shape, _scratch, 1.5f);

        _scratch.Color = snapped
            ? new Color4(1f, 1f, 1f, 1f)
            : _app.FloatingTheme.Text;
        _ctx.DrawText(text, small ? ReadoutFormatSmall(dpi) : ReadoutFormat(dpi),
                      // 娉ㄦ剰锛歏ortice 鐨?Rect(x, y, width, height) 鏄?浣嶇疆 + 灏哄"锛?                      // 涓嶆槸 (left, top, right, bottom)銆傚啓閿欑殑璇濇枃瀛椾細琚帓鍒板緢杩滅殑
                      // 鍦版柟鍘伙紙灞呬腑鎺掔増鏃剁洿鎺ヨ窇鍒板睆骞曞锛夛紝鐪嬭捣鏉ュ氨鍍?瀛楁病鐢诲嚭鏉?銆?                      new Rect(label.MinX, label.MinY,
                               label.MaxX - label.MinX, label.MaxY - label.MinY),
                      _scratch);
    }

    /// <summary>
    /// 搴︽暟鏍囩鐨勬枃瀛楁牸寮忥紙鎸?DPI 鐢熸垚骞剁紦瀛橈級銆?    ///
    /// 缁樺埗鏃剁殑鍙樻崲鍙湁骞崇Щ銆佹病鏈夌缉鏀撅紝鎵€浠ュ瓧鍙峰啓姝诲氨绛変簬"鐗╃悊鍍忕礌"鈥斺€?    /// 15 鐗╃悊鍍忕礌鍦?200% 缂╂斁涓嬪彧鏈?7.5 閫昏緫鍍忕礌锛屾姇褰变笂鏍规湰鐪嬩笉娓呫€?    /// </summary>
    /// <summary>
    /// 鐢?DirectWrite 閲忎竴娈垫枃瀛楀湪缁欏畾鏍煎紡涓嬬殑瀹藉害銆傜粰"鐩掑瓙瑕佽窡鐫€鏂囨鍙橀暱"鐨勫湴鏂圭敤
    /// 锛堝害鏁版爣绛俱€侀潰鏉垮簳鑹诧級銆傜敤鍚屼竴涓牸寮忛噺锛岄噺鍑烘潵鐨勬墠鍜岀敾鍑烘潵鐨勪竴鑷淬€?    /// </summary>
    /// <summary>搴︽暟鏍囩鐨勬枃妗堬紙鐢诲畠銆佹寜瀹冮噺鐩掑瓙瀹藉害锛岄兘璧拌繖涓€涓嚱鏁帮級銆?/summary>
    private static string RotationLabel(InkEngine app) => SelectionHandles.FormatDegrees(app.SelRotationDegrees);

    private static float MeasureTextWidth(string text, IDWriteTextFormat format)
    {
        if (string.IsNullOrEmpty(text) || format == null) return 0f;
        try
        {
            using var layout = Gfx.WriteFactory.CreateTextLayout(text, format, 4096f, 1024f);
            return layout.Metrics.Width;
        }
        catch { return 0f; }        // 閲忎笉鍑烘潵灏遍€€鍥炴渶灏忓搴︼紝缁濅笉鑳藉洜涓洪噺涓搴︽妸娓叉煋鎼炴寕
    }

    private IDWriteTextFormat ReadoutFormat(float dpi)
    {
        float px = MathF.Max(11f, MathF.Round(15f * dpi));
        if (_readoutFormat == null || _readoutFormatPx != px)
        {
            _readoutFormat?.Dispose();
            _readoutFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _readoutFormat.TextAlignment = TextAlignment.Center;
            _readoutFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutFormatPx = px;
        }
        return _readoutFormat;
    }

    /// <summary>
    /// **瑙掓爣**鐢ㄧ殑灏忓彿鏂囧瓧鏍煎紡锛?1 閫昏緫鍍忕礌锛屾瘮涓昏鏁板皬涓€鍙凤級銆?    ///
    /// 鍜?<see cref="ReadoutFormat"/> 鍚屼竴濂楁寜 DPI 鐢熸垚 + 缂撳瓨鐨勫啓娉曪紙鐞嗙敱瑙侀偅閲岋級锛?    /// 閲忕洅瀛愬拰鐢诲瓧蹇呴』鐢?*鍚屼竴涓牸寮?*锛岄噺鍑烘潵鐨勫搴︽墠鍜岀敾鍑烘潵鐨勪竴鑷淬€?    /// </summary>
    private IDWriteTextFormat ReadoutFormatSmall(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(11f * dpi));
        if (_readoutFormatSmall == null || _readoutFormatSmallPx != px)
        {
            _readoutFormatSmall?.Dispose();
            _readoutFormatSmall = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _readoutFormatSmall.TextAlignment = TextAlignment.Center;
            _readoutFormatSmall.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutFormatSmallPx = px;
        }
        return _readoutFormatSmall;
    }

    /// <summary>
    /// **鍥惧簱闈㈡澘**锛?鎴戠殑鍥惧舰"锛夛細浠庡伐鍏锋潯涓婃部寰€涓婇暱鐨勪竴寮犲崱鐗囷紝閲岄潰鏄敹钘忕殑缂╃暐鍥惧銆?    ///
    /// 涓轰粈涔堣繖涓潰鏉垮綊寮曟搸鐢伙紙鑰屼笉鏄晫闈級锛氭牸瀛愰噷鐨勫唴瀹规槸**鐢荤瑪杩?*鈥斺€旀妸鏉＄洰鐨勫寘鍥寸洅
    /// 绛夋瘮缂╄繘鏍煎瓙銆乣DrawStroke(s, fit)` 鐩存帴鐢汇€傝繖鏉¤矾鐜版垚銆侀浂鏂版満鍒讹紝鑰屼笖**涓嶈惤缂╃暐鍥句綅鍥?*锛?    /// 瀛樼殑鏄璞★紝鐢荤殑鏃跺€欏氨姘歌繙鍜屽璞′竴鑷达紙鍙傝€冨疄鐜颁篃鏄?鍔犺浇鏃剁幇娓叉煋"锛屽彧鏄畠閭ｈ竟寰?    /// 閫犱竴寮?RenderTargetBitmap锛屾垜浠繛浣嶅浘閮戒笉鐢ㄥ缓锛夈€?    /// 浠ｄ环锛氭瘡甯ч噸鐢?N 涓牸瀛愰噷鐨勭瑪鐢烩€斺€旀牸瀛愭渶澶?16 涓€佹瘡涓潯鐩嚑涓璞★紝蹇界暐涓嶈銆?    ///
    /// 鐢绘硶涓庢搷浣滄潯/鍏跺畠闈㈡澘鍚屼竴濂楋細`DrawPanelCard` 鍗＄墖 锛?涓婚鑹诧紝鍦嗚鍙栫晫闈㈡帹涓婃潵鐨?    /// `CornerRadius`锛堜笁鍧椾笢瑗垮繀椤绘槸涓€瀹讹級銆?    /// </summary>
    private void DrawLibraryPanel(InkEngine app)
    {
        if (!app.LibraryPanelOpen) return;
        float dpi = Dpi / 96f * app.UiScale;   // 鍥惧簱闈㈡澘璺熺潃鐣岄潰缂╂斁锛堟寕鍦ㄥ伐鍏锋潯涓婏紝涓€瀹跺瓙锛?        var theme = app.FloatingTheme;
        var panel = app.LibraryPanelRectNow();
        int n = app.LibraryEntries.Count;

        float radius = MathF.Min(theme.CornerRadius * dpi, 12f * dpi);
        DrawPanelCard(app, panel, radius);

        // ---- 鏍囬琛岋細銆屽浘搴?路 N 涓€嶏紜 鍙宠竟銆屾暣鐞嗐€嶃€屸湑銆?----
        var head = LibraryLayout.HeaderRect(panel, dpi);
        _scratch.Color = theme.Text;
        _ctx.DrawText(n > 0 ? $"鍥惧簱 路 {n} 涓? : "鍥惧簱",
                      ReadoutFormat(dpi),
                      new Rect(head.MinX, head.MinY, head.MaxX - head.MinX, head.MaxY - head.MinY),
                      _scratch);

        var edit = LibraryLayout.EditRect(panel, dpi);
        if (app.LibraryEditMode)
        {
            // 婵€娲绘€佺敤涓婚鐨勫疄蹇冭摑锛堝拰鎿嶄綔鏉′笂"澶嶅埗妯″紡寮€鐫€"鍚屼竴绉嶈〃杈撅級
            _scratch.Color = theme.ActiveBg;
            float br = (edit.MaxY - edit.MinY) * 0.32f;
            _ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(edit.MinX, edit.MinY, edit.MaxX, edit.MaxY),
                                     br, br), _scratch);
        }
        _scratch.Color = app.LibraryEditMode ? theme.ActiveText : theme.TextMuted;
        _ctx.DrawText(app.LibraryEditMode ? "瀹屾垚" : "鏁寸悊", ReadoutFormatSmall(dpi),
                      new Rect(edit.MinX, edit.MinY, edit.MaxX - edit.MinX, edit.MaxY - edit.MinY),
                      _scratch);

        var close = LibraryLayout.CloseRect(panel, dpi);
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText("鉁?, ReadoutFormatSmall(dpi),
                      new Rect(close.MinX, close.MinY, close.MaxX - close.MinX, close.MaxY - close.MinY),
                      _scratch);

        // ---- 绌哄簱锛氱粰涓€鍙?鎬庝箞瀛?锛岃€屼笉鏄┖鐧戒竴鐗囷紙鍙傝€冨疄鐜扮殑绌烘€佹枃妗堝悓娆撅級----
        if (n == 0)
        {
            _scratch.Color = theme.TextMuted;
            float gap = LibraryLayout.CellGapLogical * dpi;
            float y = panel.MinY + LibraryLayout.PadLogical * dpi + LibraryLayout.HeaderHLogical * dpi + gap;
            _ctx.DrawText("鍥惧簱杩樻槸绌虹殑", ReadoutFormat(dpi),
                          new Rect(panel.MinX, y, panel.MaxX - panel.MinX, 24f * dpi), _scratch);
            _ctx.DrawText("閫変腑瀵硅薄 鈫?鎿嶄綔鏉￠偅棰椼€屽浘搴撱€嶅浘鏍囧瓨杩涙潵", ReadoutFormatSmall(dpi),
                          new Rect(panel.MinX, y + 26f * dpi, panel.MaxX - panel.MinX, 20f * dpi), _scratch);
            return;
        }

        // ---- 缂╃暐鍥惧 ----
        for (int i = 0; i < n; i++)
        {
            var cell = LibraryLayout.CellRect(panel, dpi, i);
            if (cell.MinY > panel.MaxY) break;        // 瓒呭嚭闈㈡澘鐨勮涓嶇敾锛堝垪鏁?脳 琛屾暟宸茬敱甯冨眬澶逛綇锛?
            if (i == app.LibraryHover)
            {
                _scratch.Color = theme.Hover;
                float inset = 2f * dpi;
                float hr = radius * 0.6f;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(cell.MinX + inset, cell.MinY + inset,
                                         cell.MaxX - inset, cell.MaxY - inset), hr, hr), _scratch);
            }

            // 缂╃暐鍥撅細鏉＄洰鍖呭洿鐩掔瓑姣旂缉杩涙牸瀛愶紙鍐呯缉 4 閫昏緫鍍忕礌锛夛紝鏁存牸瑁佹帀婧㈠嚭鐨勯儴鍒嗐€?            var entry = app.LibraryEntries[i];
            var box = EditRegion.Of(entry.Strokes);
            float boxW = box.MaxX - box.MinX, boxH = box.MaxY - box.MinY;
            if (boxW > 0.1f && boxH > 0.1f)
            {
                float inner = 4f * dpi;
                float availW = MathF.Max(1f, (cell.MaxX - cell.MinX) - inner * 2f);
                float availH = MathF.Max(1f, (cell.MaxY - cell.MinY) - inner * 2f);
                float k = MathF.Min(availW / boxW, availH / boxH);
                var fit = Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(
                    (cell.MinX + cell.MaxX) * 0.5f - k * (box.MinX + box.MaxX) * 0.5f,
                    (cell.MinY + cell.MaxY) * 0.5f - k * (box.MinY + box.MaxY) * 0.5f);

                // 瑁佸壀蹇呴』鐢?*绐楀彛鍧愭爣**锛圥ushAxisAlignedClip 鍚冪殑鏄獥鍙ｅ潗鏍囷紝瑙?                // CanvasRectToWindow 涓婇潰閭ｆ潯娉ㄩ噴锛夆€斺€斿杺鐢诲竷鍧愭爣鍦ㄧ浉鏈烘粴杩囦箣鍚庝細瑁佹銆?                var clipWin = CanvasRectToWindow(cell);
                _ctx.PushAxisAlignedClip(
                    new Vortice.RawRectF(clipWin.MinX, clipWin.MinY, clipWin.MaxX, clipWin.MaxY),
                    AntialiasMode.Aliased);
                foreach (var s in entry.Strokes) DrawStroke(s, fit);
                _ctx.PopAxisAlignedClip();
            }

            // 鏁寸悊妯″紡锛氬彸涓婅涓€棰楃孩 鉁曪紙瑙︽懜灞忕殑鍒犻櫎鍏ュ彛鈥斺€旈偅鍎挎病鏈夊彸閿級
            if (app.LibraryEditMode)
            {
                var badge = LibraryLayout.BadgeRect(cell, dpi);
                float br = (badge.MaxY - badge.MinY) * 0.5f;
                _scratch.Color = new Color4(0.90f, 0.28f, 0.30f, 1f);
                _ctx.FillEllipse(new Ellipse(new System.Numerics.Vector2(
                    (badge.MinX + badge.MaxX) * 0.5f, (badge.MinY + badge.MaxY) * 0.5f), br, br), _scratch);
                _scratch.Color = new Color4(1f, 1f, 1f, 1f);
                _ctx.DrawText("鉁?, ReadoutFormatSmall(dpi),
                              new Rect(badge.MinX, badge.MinY, badge.MaxX - badge.MinX, badge.MaxY - badge.MinY),
                              _scratch);
            }
        }
    }

    /// <summary>
    /// 鎿嶄綔鏉★紙鍗佹牸锛夛細鏀惰捣 / 棰滆壊 / 閿佸畾 / 灞傜骇 / 瀵煎嚭 / **鍥惧簱** / 澶嶅埗 / 宸﹀彸缈昏浆 / 涓婁笅缈昏浆 / 鍒犻櫎銆?    /// 椤哄簭鐨勮涔夎 <see cref="SelBarButton"/>锛涜繖涓€灞傚彧璐熻矗鐢汇€?    ///
    /// **涓轰粈涔堝湪涓嬫柟鑰屼笉鏄笂鏂?*锛氶€変腑鍐呭闈犲睆骞曚笂杈规椂锛屼笂鏂圭殑鎿嶄綔鏉¤涔堣椤跺嚭
    /// 灞忓箷銆佽涔堢洊浣忔鍦ㄨ鐨勫唴瀹广€備笅鏂规案杩滄湁浣嶇疆锛屼釜瀛愮煯鐨勮€佸笀涔熷寰楃潃銆?    ///
    /// 鍥炬爣涓嶆槸鑷繁鐢荤殑锛氳矾寰勬暟鎹潵鑷井杞?Fluent UI System Icons锛圡IT锛夛紝
    /// 鐢?tools/gen-icons.ps1 鎶撳彇鐢熸垚銆傛墜鐢荤殑鍦嗚鍜屽厜瀛︽瘮渚嬫€绘槸宸竴鍙ｆ皵銆?    ///
    /// 涓夌鐘舵€侀兘瑕佺敾鍑烘潵锛?026-09-16 琛ョ殑锛岃捣鍥犳槸鐢ㄦ埛璇?澶嶅埗浠ュ悗鐪嬩笉鍑烘潵"锛夛細
    ///   路 **鎮仠**锛氭祬鐏板渾瑙掑簳锛坄SelBarHover`锛屼互鍓嶈繖涓瓧娈垫槸涓瀛楁锛屾病浜鸿祴鍊间篃娌′汉鐢伙級锛?    ///   路 **婵€娲?*锛氭祬钃濆簳 + 鍥炬爣鍔犳繁锛堥鑹?灞傜骇闈㈡澘寮€鐫€銆佸鍒舵ā寮忓紑鐫€锛夛紱
    ///   路 **绂佺敤**锛氬浘鏍囧噺鍒?35%锛堝鍑鸿繕娌℃帴銆侀攣瀹氬鍥惧儚鏃犳剰涔夆€︹€︼級銆?    /// </summary>
    private void DrawSelectionBar(InkEngine app, in RectF sel)
    {
        var visible = app.ViewportCanvas;
        float dpi = Dpi / 96f;
        // 甯冨眬浠?SelectionHandles 鍙栵紝涓庡懡涓垽瀹氬悓婧愶細鍒嗗紑鍐欒繜鏃╁樊鍑犱釜鍍忕礌锛?        // 琛ㄧ幇灏辨槸"鐪嬪緱瑙佹寜閽嵈鐐逛笉涓?銆?        var rect = SelectionHandles.BarRect(sel, dpi, visible);
        // 鍦嗚**鐢ㄧ晫闈㈤偅濂椾护鐗?*锛圲iTheme.CornerRadius = 10锛夛紝涓嶈嚜宸卞彂鏄庝竴涓兌鍥婏細
        // 宸ュ叿鏉°€佹搷浣滄潯銆侀潰鏉夸笁鏍蜂笢瑗跨殑鍦嗚蹇呴』鏄悓涓€涓暟锛岀湅鐫€鎵嶆槸涓€瀹剁殑銆?        float radius = MathF.Min(app.FloatingTheme.CornerRadius * dpi, (rect.MaxY - rect.MinY) * 0.5f);

        // 閰嶈壊涓庢姇褰?*鍏ㄥ彇鐣岄潰鎺ㄤ笂鏉ョ殑涓婚**锛圲iTheme.Default 鍙槸娌＄晫闈㈡椂鐨勫厹搴曪級锛?        // 鎿嶄綔鏉″拰宸ュ叿鏉″繀椤绘槸鍚屼竴濂楅鑹诧紝鍚勫啓涓€浠借繜鏃╀細鑺?        // 锛堢敤鎴?2026-09-18 瀹炴祴锛氭祬鑹蹭富棰樻敼瀹屼箣鍚庯紝宸ュ叿鏉℃湁鎶曞奖銆佹搷浣滄潯娌℃湁锛屼竴鐪煎氨鐪嬪緱鍑猴級銆?        DrawPanelCard(app, rect, radius);
        var theme = app.FloatingTheme;

        const int n = SelectionHandles.BarButtonCount;
        float glyphBox = SelectionHandles.BarIconBoxLogical * dpi;   // 鍥炬爣妗嗘瘮瀛楀舰澶т竴鐐?
        for (int i = 0; i < n; i++)
        {
            var btn = SelectionHandles.BarButtonRect(i, sel, dpi, visible);
            var kind = (SelBarButton)i;
            bool hot = app.SelBarHover == i;
            bool active = IsBarButtonActive(app, kind);
            // 绂佺敤锛堝浘鏍囨敼鐢?theme.TextMuted锛夛細涔濇牸閮芥槸閫氱敤鎿嶄綔锛堟敹璧?棰滆壊/閿佸畾/灞傜骇/
            // 瀵煎嚭/澶嶅埗/缈昏浆脳2/鍒犻櫎锛夛紝瀵逛换浣曢€変腑瀵硅薄閮芥垚绔嬶紝鎵€浠?*褰撳墠娌℃湁涓€鏍间細鐏?*銆?            // 杩欎竴鏍肩暀鐫€鏄粰"浠ュ悗鐪熼渶瑕佺鐢?鐨勪綅缃€斺€?026-09-20 鍔犺繃鍙堟挙鎺夌殑
            // 銆屽紑鍙ｆ柟鍚戙€嶅氨鏄潬瀹冪伆鐨勶紙鐜板湪閭ｆ牸鏁翠釜娌′簡锛屾湞鍚戞敼鍒板浘褰㈤潰鏉块噷瀹氾紝
            // 瑙?Engine.CycleParabolaAxis锛夈€?            bool disabled = false;

            // 鎮仠 / 婵€娲荤殑搴曪細**姝ｆ柟褰?*锛岀敾涓庤嚜妫€鍚屾簮锛堣 SelectionHandles.BarHoverChip锛夈€?            // 8.2.1 涔嬪墠杩欓噷鏄?鍥涘懆鍚勭缉 3"锛?8脳30 鐨勬牸瀛愰噷鐢诲嚭鏉ユ槸 **32脳24 鐨勯暱鏂瑰舰**锛?            // 鐢ㄦ埛涓€鐪肩湅鍑?鎮仠鏄暱鏂瑰舰涓嶆槸姝ｆ柟褰紝鎵€浠ュ垎鏁?鈥斺€旂幇鍦?30脳30 鐨勬牸瀛愰噷鏄?26脳26銆?            if (hot || active)
            {
                var chip = SelectionHandles.BarHoverChip(i, sel, dpi, visible);
                var bg = new Vortice.RawRectF(chip.MinX, chip.MinY, chip.MaxX, chip.MaxY);
                float br = MathF.Min(bg.Bottom - bg.Top, bg.Right - bg.Left) * 0.32f;
                _scratch.Color = active
                    ? theme.ActiveBg                            // 婵€娲伙細涓婚鐨勫疄蹇冭摑
                    : theme.Hover;                              // 鎮仠锛氫富棰樼殑娴呰摑鐏?                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            var iconBrush = Brush(disabled ? theme.TextMuted
                                     : active ? theme.ActiveText
                                              : theme.Text);
            float cx = (btn.MinX + btn.MaxX) * 0.5f, cy = (btn.MinY + btn.MaxY) * 0.5f;

            switch (kind)
            {
                case SelBarButton.Color:
                    // 鍦嗙幆鐨?*鍗婂緞**鎸夊浘鏍囨鐨勪竴鍗婂乏鍙崇粰锛堜互鍓嶇粰鎴?0.62 鍊嶏紝鐢诲嚭鏉ユ瘮
                    // 鍒殑鍥炬爣杩樺ぇ涓€鍦堚€斺€旂敤鎴凤細"閭ｄ釜棰滆壊鍦堢殑姣斾緥涔熷お澶т簡"锛夈€?                    DrawColorRing(app, cx, cy, glyphBox * 0.44f, disabled);
                    break;
                case SelBarButton.Lock:
                    DrawIcon(IsSelectionLocked(app) ? IconPaths.lockClosed : IconPaths.unlock,
                             cx - glyphBox * 0.5f, cy - glyphBox * 0.5f, glyphBox, iconBrush);
                    break;
                default:
                    DrawIcon(IconForBar(kind), cx - glyphBox * 0.5f, cy - glyphBox * 0.5f,
                             glyphBox, iconBrush);
                    break;
            }
        }
    }

    /// <summary>鎿嶄綔鏉″崄鏍煎悇鑷殑鍥炬爣锛堥鑹查偅鏍兼槸鑷粯鐨?褰撳墠鑹茬幆"锛屼笉鍦ㄨ繖閲岋級銆?/summary>
    private static string IconForBar(SelBarButton b) => b switch
    {
        SelBarButton.Collapse => IconPaths.collapse,
        SelBarButton.Layer => IconPaths.layer,
        SelBarButton.Export => IconPaths.export,
        SelBarButton.Library => IconPaths.library,
        SelBarButton.Copy => IconPaths.copy,
        SelBarButton.FlipH => IconPaths.flipH,
        SelBarButton.FlipV => IconPaths.flipV,
        SelBarButton.Delete => IconPaths.delete,
        _ => IconPaths.layer,
    };


    /// <summary>杩欎竴鏍兼槸"婵€娲?鐘舵€佸悧锛堥潰鏉垮紑鐫€ / 妯″紡寮€鐫€锛夈€?/summary>
    private static bool IsBarButtonActive(InkEngine app, SelBarButton b) => b switch
    {
        SelBarButton.Color => app.SelPanelOpen == SelPanel.Ink,
        SelBarButton.Layer => app.SelPanelOpen == SelPanel.Layer,
        SelBarButton.Copy => app.CopyDragArmed,
        _ => false,
    };

    /// <summary>閫変腑鐨勫璞℃槸涓嶆槸**鍏ㄩ兘**閿佺潃锛堝喅瀹氶攣鍥炬爣鐢诲紑閿佽繕鏄棴閿侊級銆?/summary>
    private static bool IsSelectionLocked(InkEngine app)
    {
        if (app.Doc.Selected.Count == 0) return false;
        foreach (var s in app.Doc.Selected) if (!s.Locked) return false;
        return true;
    }

    /// <summary>
    /// "棰滆壊"閭ｄ竴鏍肩殑鍥炬爣锛?*褰撳墠鑹茬殑鍦嗙幆**锛堝弬鑰冨疄鐜扮殑鐢绘硶锛夈€?    ///
    /// 涓€鐪艰兘鐪嬪嚭"杩欎竴鎵瑰ⅷ鐜板湪鏄粈涔堥鑹?锛涢€変腑鐨勯鑹蹭笉涓€鑷存椂锛堟贩鍚堥€夊尯锛?    /// 鐢绘垚**鍙岃壊鐜?*鈥斺€斿乏鍗婅竟绗竴绉嶈壊銆佸彸鍗婅竟绗簩绉嶈壊锛屼笉鍋囪鏄函鑹层€?    /// 閫変腑鐨勫叏鏄浘鍍忓璞℃椂鐢绘垚绂佺敤鎬侊紙鍥惧儚娌℃湁"棰滆壊"杩欎釜姒傚康锛夈€?    /// </summary>
    private void DrawColorRing(InkEngine app, float cx, float cy, float r, bool disabled)
    {
        Color4 a = default; int distinct = 0; Color4 b2 = default;
        bool any = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            if (!any) { a = s.Color; any = true; continue; }
            if (distinct == 0 && !SameRgb(a, s.Color)) { b2 = s.Color; distinct = 1; }
            else if (distinct == 1 && !SameRgb(a, s.Color) && !SameRgb(b2, s.Color)) distinct = 2;
        }
        if (!any)                                   // 鍏ㄦ槸鍥惧儚瀵硅薄锛氱敾涓伆鐜紙绂佺敤鎬侊級
        {
            _scratch.Color = new Color4(0.5f, 0.52f, 0.56f, 0.35f);
            _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), _scratch, 2.6f * Dpi / 96f);
            return;
        }

        float w = 2.2f * Dpi / 96f;
        var half = new Ellipse(new Vector2(cx, cy), r, r);
        _scratch.Color = disabled ? new Color4(a.R, a.G, a.B, 0.35f) : a;
        // 鍏堟暣鍦堢敾搴曡壊锛屽啀鎶?鍙︿竴绉嶉鑹?閭ｄ竴娈电洊涓婂幓锛堢敤涓€灏忔鍦嗗姬褰撶ず鎰忥紝涓嶅仛绮惧瘑鍒嗚壊锛?        // 杩欐潯鍙渶瑕佽浜虹湅鍑烘潵"涓嶆涓€绉嶉鑹?锛屼笉闇€瑕佸噯纭瘮渚嬶級銆?        _ctx.DrawEllipse(half, _scratch, w);
        if (distinct >= 1)
        {
            _scratch.Color = disabled ? new Color4(b2.R, b2.G, b2.B, 0.35f) : b2;
            float a0 = -MathF.PI / 2f;
            // 鐢ㄥ洓鏉＄煭寮х嚎鎷煎嚭"鍙﹀鍗婂湀"锛圖2D 鐨?DrawArc 鍦ㄥ悇鐗堟湰绛惧悕涓嶄竴鑷达紝涓嶅€煎緱涓哄畠鍐掗櫓锛?            for (int k = 0; k < 12; k++)
            {
                float t0 = a0 + k / 24f * MathF.PI * 2f;
                float t1 = t0 + MathF.PI * 2f / 24f;
                _ctx.DrawLine(new Vector2(cx + MathF.Cos(t0) * r, cy + MathF.Sin(t0) * r),
                              new Vector2(cx + MathF.Cos(t1) * r, cy + MathF.Sin(t1) * r), _scratch, w);
            }
        }
    }

    private static bool SameRgb(in Color4 a, in Color4 b)
        => MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;

    /// <summary>
    /// 鏀惰捣鎬佺殑鍦嗛挳锛堝弬鑰冨疄鐜伴噷鏉￠閭ｄ釜 鉁?鐐逛笅鍘讳箣鍚庣殑鏍峰瓙锛夈€?    /// 鐢讳竴涓渾 + 涓棿鐨?锛?锛氫袱娈电嚎鑷繁鐢烩€斺€斿畠涓嶆槸鍥炬爣锛屾槸涓函鍑犱綍绗﹀彿銆?    /// </summary>
    private void DrawCollapsedDot(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        var r = SelectionHandles.BarCollapsedRect(sel, dpi, app.ViewportCanvas);
        float d = r.MaxX - r.MinX;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);

        bool hot = app.SelBarHover == (int)SelBarButton.Collapse;
        var th = app.FloatingTheme;
        _scratch.Color = hot ? th.Hover : th.Panel;
        _ctx.FillEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch, 1f * dpi);

        _scratch.Color = th.Text;
        float arm = d * 0.19f, w = 1.8f * dpi;
        _ctx.DrawLine(new Vector2(c.X - arm, c.Y), new Vector2(c.X + arm, c.Y), _scratch, w);
        _ctx.DrawLine(new Vector2(c.X, c.Y - arm), new Vector2(c.X, c.Y + arm), _scratch, w);
    }

    /// <summary>
    /// 娴姩闈㈡澘 / 鎿嶄綔鏉＄殑鍗＄墖搴曪細**涓婚缁欑殑鎶曞奖 锛?搴?锛?鎻忚竟 锛嬶紙娴呰壊鐨勶級搴曟部鍐呴槾褰?*銆?    ///
    /// 鏁板瓧鍏ㄩ儴鏉ヨ嚜 `app.FloatingTheme`锛岃€岄偅涓富棰樻槸**鐣岄潰鎺ㄤ笂鏉ョ殑**锛坄InkUi.Tokens`锛夆€斺€?    /// 鎵€浠ユ诞灞傚拰宸ュ叿鏉℃案杩滄槸涓€濂楋紝涓嶄細鍐嶅嚭鐜?宸ュ叿鏉℃崲浜嗘姇褰便€佹搷浣滄潯杩樻槸鑰佹牱瀛?銆?    /// 娌℃湁鐣岄潰鎸備笂鏉ユ椂鐢?`UiTheme.Default` 鍏滃簳锛堣嚜妫€瀹夸富锛夈€?    /// </summary>
    private void DrawPanelCard(InkEngine app, in RectF r, float radius)
    {
        float dpi = Dpi / 96f;
        var th = app.FloatingTheme;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);

        // **鎶曞奖鎸夊舰鐘跺ぇ灏忕缉涓€缂?*锛氳繖濂楄儉骞咃紙鏈€杩?16 閫昏緫鍍忕礌锛夋槸鎸?48 楂樼殑闈㈡澘瀹氱殑锛?        // 鐩存帴濂楀湪涓€涓?28 鍍忕礌鐨勫皬鍦嗭紙鏀惰捣鍚庨偅棰楃悆锛変笂浼氬彉鎴愪竴澶у洟鍏夋檿銆?        // 瑙勫垯锛氭瘮 48 鐭殑鎸夋瘮渚嬬缉锛屼笉瓒呰繃 1銆?        float shadowK = MathF.Min(1f, (r.MaxY - r.MinY) / 48f);
        for (int i = th.Shadow.Length - 1; i >= 0; i--)
        {
            var layer = th.Shadow[i];
            float inf = layer.Inflate * dpi * shadowK;
            float dy = layer.Dy * dpi * shadowK;
            _scratch.Color = layer.Color;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(r.MinX - inf, r.MinY - inf + dy, r.MaxX + inf, r.MaxY + inf + dy),
                radius + inf, radius + inf), _scratch);
        }

        _scratch.Color = th.Panel;
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch, 1f * dpi);

        // 搴曟部閭ｉ亾"鍗疯竟"锛堝拰宸ュ叿鏉″悓涓€涓仛娉曪細瑁佷竴鏉℃í甯︺€佸湪甯﹀瓙閲屾弿涓€閬嶅渾瑙掔煩褰紝
        // 杩欐牱涓ょ椤虹潃鍦嗚鏀惰繘鍘伙紝涓嶄細鎴冲嚭杞粨澶栵級銆傛繁鑹蹭富棰樿繖涓槸鍏ㄩ€忔槑鐨勶紝绛変簬涓嶇敾銆?        if (th.EdgeBottom.A > 0.001f)
        {
            float y = r.MaxY - 1f * dpi;
            _ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, y - 0.75f * dpi, r.MaxX, y + 0.75f * dpi),
                                     AntialiasMode.Aliased);
            var edge = new Vortice.RawRectF(r.MinX + 0.5f * dpi, r.MinY + 0.5f * dpi,
                                            r.MaxX - 0.5f * dpi, r.MaxY - 0.5f * dpi);
            _scratch.Color = th.EdgeBottom;
            _ctx.DrawRoundedRectangle(new RoundedRectangle(edge, radius - 0.5f * dpi, radius - 0.5f * dpi),
                                      _scratch, 1f * dpi);
            _ctx.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// 棰滆壊 / 绮楃粏 / 绾垮瀷闈㈡澘锛堢収鍙傝€冨疄鐜帮級锛氫竴鏉＄矖缁嗘粦鏉°€佷竴琛岀嚎鍨嬨€佷竴鍧?4 鍒楄壊鏉裤€?    ///
    /// 鑹叉澘鐨?褰撳墠鑹?鐢?*鐜?*鏍囧嚭鏉ワ紙鍙傝€冨疄鐜扮殑鍋氭硶锛夛細姣旀暣鍧楁崲鑹茬渷鍦版柟锛?    /// 鑰屼笖娣峰悎閫夊尯锛堝嚑绉嶉鑹叉贩鐫€閫夛級涔熻兘琛ㄨ揪鈥斺€旈偅鏃剁幆鐢绘垚鍙岃壊锛堣 DrawColorRing锛夈€?    ///
    /// 8.2.0 涓ゅ鍙樺寲锛?    ///   路 婊戞潯浠?鐐规。浣?鏀规垚**杩炵画鎷栧姩**锛氭嫋鍔ㄤ腑鍦ㄦ粦閽笂鏂规樉绀哄疄鏃舵暟鍊硷紙閫昏緫鍍忕礌锛?    ///     鍜?HUD 鐨勩€岀矖缁嗐€嶅悓涓€鍙ｅ緞锛夈€佸彸绔竴棰楄窡鐫€鍙樼矖鐨勭瑪灏栭瑙堬紝瑙嗚鍜岃壊鏉块偅涓€鎺?    ///     锛堝簳杞?宸查€夋/鐧介挳 + 寮鸿皟鑹叉弿杈癸級鏄悓涓€濂楄瑷€锛?    ///   路 鑹叉澘鏈牸浠?铏氱嚎鐏扮幆鍗犱綅"鏀规垚**鑹茬浉鐜?*锛氱偣瀹冨脊鍑哄唴缃?HSV 灏忚壊鏉?    ///     锛堣 <see cref="DrawPickPanel"/>锛夈€?    /// </summary>
    private void DrawInkPanel(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        int sc = SelectionHandles.SwatchCount;
        var th = app.FloatingTheme;
        var p = SelectionHandles.PanelRect(sel, dpi, app.ViewportCanvas, sc);
        // 鍦嗚鐢ㄧ晫闈㈡帹涓婃潵鐨勪富棰樹护鐗岋紙= InkUi.Tokens.FloatingCorner 10锛夛紝
        // 涓夊娴眰鍚屼竴涓暟鈥斺€斾互鍓嶈繖涓ゅ紶闈㈡澘鍚勫啓浜嗕竴涓?10f 瀛楅潰閲忋€?        DrawPanelCard(app, p, th.CornerRadius * dpi);

        // ---- 鈶?绮楃粏婊戞潯锛堣繛缁紱鎷栧姩瀹炴椂鏁板€硷紱鍙崇绗斿皷棰勮锛?---
        var slider = SelectionHandles.SliderRect(sel, dpi, app.ViewportCanvas, sc);
        var (tx0, tx1) = SelectionHandles.SliderTrackRange(sel, dpi, app.ViewportCanvas, sc);
        int steps = app.SliderStepCount();
        float cy = (slider.MinY + slider.MaxY) * 0.5f;

        // 褰撳墠鍊硷細鎷栧姩涓敤鎵嬮噷鐨勯偅涓紙瀹炴椂锛夛紝骞虫椂鍙栭€夊尯绗竴鏉″ⅷ鐨勫搴︺€?        var (minW, maxW) = app.WidthRangeForSelection();
        bool dragging = app.WidthSliderDragging;
        float value = dragging ? app.WidthSliderDragValue : app.SliderValueOfSelection();
        float t = maxW > minW ? Math.Clamp((value - minW) / (maxW - minW), 0f, 1f) : 0f;
        float kx = tx0 + (tx1 - tx0) * t;

        // 宸查€夋鐢?*褰撳墠澧ㄨ壊**锛堝拰涓绘潯閭ｆ潯婊戞潯涓€涓瑷€锛氳繖涓€娈垫槸"杩欎竴鎵瑰ⅷ鐨勭矖缁?锛夈€?        Color4 selColor = th.ActiveBg;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            selColor = new Color4(s.Color.R, s.Color.G, s.Color.B, 1f);
            break;
        }

        // 搴曡建锛堝嚬妲斤級锛?宸查€夋銆傛嫋鍔ㄤ腑杞ㄩ亾鍔犵矖 1 鍍忕礌 = "鎵嬮噷鎻＄潃瀹?鐨勫弽棣堛€?        float hTrack = (dragging ? 6f : 5f) * dpi;
        _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.30f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(tx0, cy - hTrack * 0.5f, tx1, cy + hTrack * 0.5f),
            hTrack * 0.5f, hTrack * 0.5f), _scratch);
        if (kx - tx0 > 0.5f * dpi)
        {
            _scratch.Color = new Color4(selColor.R, selColor.G, selColor.B, dragging ? 0.85f : 0.55f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(tx0, cy - hTrack * 0.5f, kx, cy + hTrack * 0.5f),
                hTrack * 0.5f, hTrack * 0.5f), _scratch);
        }

        // 鏃ф。浣嶇殑浣嶇疆鐣欎竴鎺?*鍙傝€冨埢搴?*锛堟粦鏉¤繛缁簡锛岀偣杩樺湪鈥斺€斿嚟鑲岃倝璁板繂鎵惧緱鍒板湴鏂癸級銆?        for (int i = 0; i < steps; i++)
        {
            float x = SelectionHandles.SliderStepX(i, steps, sel, dpi, app.ViewportCanvas, sc);
            _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.55f);
            _ctx.FillEllipse(new Ellipse(new Vector2(x, cy), 1.4f * dpi, 1.4f * dpi), _scratch);
        }

        // 婊戦挳锛氱櫧搴?+ 寮鸿皟鑹叉弿杈癸紙鍜岃壊鏉垮綋鍓嶈壊鐨勭幆銆佺嚎鍨嬪綋鍓嶆。鐨勬弿杈瑰悓涓€濂楋級銆?        _ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 7f * dpi, 7f * dpi),
                         Brush(new Color4(1f, 1f, 1f, 1f)));
        _scratch.Color = th.ActiveBg;
        _ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 7f * dpi, 7f * dpi), _scratch, 1.8f * dpi);

        // 鍙崇"绗斿皷棰勮"锛氫竴棰楄窡鐫€鍊煎彉澶х殑鐐癸紙鍜屼富鏉￠偅鏉℃粦鏉″悓涓€涓叕寮忥級銆?        {
            float pr = Math.Clamp(2f + t * 6.5f, 2f, 8.5f) * dpi;
            float px = slider.MaxX - SelectionHandles.SliderTailLogical * 0.5f * dpi;
            _scratch.Color = selColor;
            _ctx.FillEllipse(new Ellipse(new Vector2(px, cy), pr, pr), _scratch);
        }

        // 鎷栧姩涓細鏁板€兼樉绀哄湪婊戦挳涓婃柟锛?鎷栧姩鐨勬椂鍊欐樉绀哄€?鈥斺€旂敤鎴?2026-09-30锛夈€?        // 琛岄珮 30锛?.2.1 鏀剁煯杩囷級锛氬瓧鍙?11 + 鐩掗珮 16锛屾濂藉崱鍦ㄥ崱鐗囦笂娌垮拰杞ㄩ亾涔嬮棿銆?        if (dragging)
        {
            string text = value.ToString("0.0");
            float tx = Math.Clamp(kx, p.MinX + 18f * dpi, slider.MaxX - 18f * dpi);
            float tw = 44f * dpi;
            _scratch.Color = th.Text;
            _ctx.DrawText(text, ReadoutFormatSmall(dpi),
                          new Rect(tx - tw * 0.5f, cy - 19f * dpi, tw, 16f * dpi), _scratch);
        }

        // ---- 鈶♀憿 绾垮瀷锛氬疄绾?/ 铏氱嚎 / 鐐圭嚎 ----
        // 褰撳墠鏄摢涓€妗?*鐢?SelectionHandles 缁熶竴绠?*锛堣 DashOfSelection 鐨勬敞閲婏細
        // 鐢诲拰鑷蹇呴』璇诲悓涓€浠斤紝涓嶇劧"闈㈡澘璁や笉璁ゅ緱褰撳墠妗?杩欎欢浜嬭嚜宸遍獙涓嶄簡鑷繁锛夈€?        var curDash = SelectionHandles.DashOfSelection(app.Doc.Selected);
        for (int i = 0; i < SelectionHandles.StyleCellCount; i++)
        {
            var cell = SelectionHandles.StyleCellRect(i, sel, dpi, app.ViewportCanvas, sc);
            var dash = (StrokeDash)i;             // 鏍煎簭 = 绾垮瀷鍙栧€硷紝瑙?StyleCellRect 鐨勬敞閲?            bool active = dash == curDash;

            // 褰撳墠妗ｏ細娴呭簳 + **寮鸿皟鑹叉弿杈?*銆傚厜闈犳祬搴曚笉澶熸樉鐪尖€斺€旂敤鎴?2026-09-19 鎶ョ殑
            // "瀹炵嚎鍜岃櫄绾垮ソ鍍忎笉鑳介€変腑"锛屾渶鍙兘鐪嬪埌鐨勫氨鏄?杩欎竴琛屾病鏈夊摢涓€鏍煎儚琚€変腑鐨勬牱瀛?銆?            // 鎻忚竟杩欎竴鎷涘拰婊戞潯閭ｄ釜褰撳墠妗ｇ殑鍦嗛挳銆佽壊鏉块偅涓綋鍓嶈壊鐨勭幆鏄悓涓€濂楄瑷€銆?            // 涓嶅啀鐢?th.Hover锛氶偅鏄?*鎮仠**鑹诧紝鎷垮畠琛ㄧず"閫変腑"鏈韩灏变覆浜嗐€?            _scratch.Color = th.Panel;
            float cr = 7f * dpi;
            var cellRect = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
            _ctx.FillRoundedRectangle(new RoundedRectangle(cellRect, cr, cr), _scratch);
            if (active)
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawRoundedRectangle(new RoundedRectangle(cellRect, cr, cr), _scratch, 1.6f * dpi);
            }

            // 鏍煎瓙閲岀敾**杩欎竴妗ｇ湡瀹炵殑鏍峰瓙**锛堜竴鏉℃暣绾?/ 涓€娈典竴娈?/ 涓€灏忕偣涓€灏忕偣锛夛細
            // 鑰佸笀鐪嬬殑鏄?鍝牸鏄櫄绾?锛岀敾涓枃瀛楁爣绛惧弽鑰岃澶氳涓€鐪笺€?            float lx0 = cell.MinX + 9f * dpi, lx1 = cell.MaxX - 9f * dpi;
            float ly = (cell.MinY + cell.MaxY) * 0.5f;
            _scratch.Color = active ? th.Text : th.TextMuted;
            var a = new Vector2(lx0, ly);
            var b = new Vector2(lx1, ly);
            float lw = 2.6f * dpi;
            switch (dash)
            {
                case StrokeDash.Dashed: DrawDashed(a, b, lw); break;
                case StrokeDash.Dotted: DrawDotted(a, b, lw); break;
                default: _ctx.DrawLine(a, b, _scratch, lw); break;
            }
        }

        // ---- 鈶?鑹叉澘 ----
        var swatches = InkPalette.SelectionSwatches;
        Color4 curColor = default; bool anyColor = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            curColor = s.Color; anyColor = true; break;
        }
        for (int i = 0; i < sc; i++)
        {
            var cell = SelectionHandles.SwatchRect(i, sel, dpi, app.ViewportCanvas, sc);
            var c = new Vector2((cell.MinX + cell.MaxX) * 0.5f, (cell.MinY + cell.MaxY) * 0.5f);
            float rad = (cell.MaxX - cell.MinX) * 0.5f;
            bool custom = i == swatches.Length - 1;      // 鏈牸 = 鑷畾涔夊彇鑹诧紙8.2.0锛氳壊鐩哥幆锛?
            if (custom)
            {
                // **鑹茬浉鐜?*锛?4 娈点€佹瘡娈典竴涓壊鐩革紙寰€涓よ竟鍚勫鐢诲崐娈碉紝閬垮厤娈甸棿闇茬紳锛夈€?                // 瀹冩槸"鑷畾涔夐鑹?鐨勯€氱敤璇█锛涘唴渚у啀鐐逛竴棰楀綋鍓嶈壊锛岃鏄?鐜板湪杩欎釜鑹蹭篃鏄嚜瀹氫箟鏉ョ殑"銆?                const int segs = 24;
                float rw = 2.8f * dpi;
                for (int k = 0; k < segs; k++)
                {
                    float a0 = k / (float)segs * MathF.PI * 2f;
                    float a1 = (k + 1.5f) / segs * MathF.PI * 2f;       // +鍗婃閲嶅彔
                    _scratch.Color = Hsv.ToRgb(k / (float)segs, 0.85f, 0.95f);
                    _ctx.DrawLine(new Vector2(c.X + MathF.Cos(a0) * rad, c.Y + MathF.Sin(a0) * rad),
                                  new Vector2(c.X + MathF.Cos(a1) * rad, c.Y + MathF.Sin(a1) * rad),
                                  _scratch, rw);
                }
                if (anyColor)
                {
                    _scratch.Color = curColor;
                    _ctx.FillEllipse(new Ellipse(c, rad * 0.42f, rad * 0.42f), _scratch);
                }
                // 婵€娲伙細鑹叉澘寮€鐫€锛屾垨鑰呭綋鍓嶈壊**涓嶅湪**杩欏紶鑹叉澘閲岋紙閭ｆ鍙栬壊灏辨槸浠庤繖鍎挎潵鐨勶級銆?                bool customActive = app.CustomColorOpen
                    || (anyColor && !PaletteContains(swatches, curColor));
                if (customActive)
                {
                    _scratch.Color = th.ActiveBg;
                    _ctx.DrawEllipse(new Ellipse(c, rad + 3.5f * dpi, rad + 3.5f * dpi),
                                     _scratch, 2.2f * dpi);
                }
                continue;
            }

            var col = swatches[i].Color;
            _scratch.Color = col;
            _ctx.FillEllipse(new Ellipse(c, rad, rad), _scratch);
            _scratch.Color = new Color4(0f, 0f, 0f, 0.18f);        // 娴呰壊鐗囧帇鍦ㄦ祬搴曚笂瑕佽兘鐪嬭杈?            _ctx.DrawEllipse(new Ellipse(c, rad, rad), _scratch, 1f * dpi);

            // 褰撳墠鑹诧細澶栭潰濂椾竴鍦堬紙鍙傝€冨疄鐜扮殑鍋氭硶锛?            if (anyColor && SameRgb(col, curColor))
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawEllipse(new Ellipse(c, rad + 3.5f * dpi, rad + 3.5f * dpi), _scratch, 2.2f * dpi);
            }
        }
    }

    /// <summary>杩欏紶鑹叉澘閲屾湁娌℃湁杩欎釜棰滆壊锛堥槇鍊煎悓 <see cref="SameRgb"/>锛夈€?/summary>
    private static bool PaletteContains((string Name, Color4 Color)[] swatches, in Color4 c)
    {
        for (int i = 0; i < swatches.Length - 1; i++)          // 鏈牸鏄嚜瀹氫箟锛屼笉鍙備笌
            if (SameRgb(swatches[i].Color, c)) return true;
        return false;
    }

    /// <summary>
    /// **鑷畾涔夊彇鑹叉澘**锛?.2.0锛岃壊鏉挎湯鏍肩偣寮€锛夛細鑹茬浉鏉?+ 楗卞拰搴?鏄庡害鏂瑰潡 + 褰撳墠鑹查瑙堛€?    ///
    /// 鐢ㄦ埛褰撴椂閫夌殑鏄?A 鏂规锛?杞婚噺 HSV 鑹叉澘"锛屼笉寮圭郴缁熷璇濇锛夛細
    /// 鐐规湯鏍?鈫?寮瑰嚭杩欏紶灏忓崱鐗?鈫?閫夊畬**鐩存帴搴旂敤鍒伴€変腑澧ㄨ抗**锛堣崸鍏夌瑪鑷姩淇濆崐閫忔槑锛?    /// 璧?<see cref="InkPalette.ForStroke"/>锛夛紱鐐瑰崱鐗囧闈?= 鍙栨秷锛堜繚鎸佸師鑹诧級銆?    ///
    /// 灏哄鍜屽渾瑙掕窡澧ㄨ抗/灞傜骇闈㈡澘鍏辩敤涓€濂?token锛坄FloatPadLogical` / `FloatGapLogical` /
    /// `theme.CornerRadius`锛夛紝鎸傚湪澧ㄨ抗闈㈡澘鏃佽竟銆?*搴曡竟瀵归綈**鈥斺€旂"鑷畾涔?閭ｄ竴鏍兼渶杩戙€?    ///
    /// 涓ゅ潡鍙栬壊闈㈢敤**缂撳瓨浣嶅浘**鐢伙紙楗卞拰搴?鏄庡害鏂瑰潡锛氳壊鐩稿彉浜嗘墠閲嶇敾涓€寮?104虏 鐨勫皬鍥撅紱
    /// 鑹茬浉鏉★細灏哄鍙樹簡鎵嶉噸鐢伙級銆備笉鐢?D2D 娓愬彉绗斿埛锛氶偅鏄繖濂椾唬鐮侀噷浠庢病鍑虹幇杩囩殑
    /// API 闈紝鑰?鎵嬬畻涓€寮犲皬鍥?鏄繖鏉℃覆鏌撶绾块噷鐜版垚鐨勫仛娉曪紙HUD 缂撳瓨灏辨槸杩欎箞骞茬殑锛夈€?    /// </summary>
    private void DrawPickPanel(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        int sc = SelectionHandles.SwatchCount;
        var th = app.FloatingTheme;
        var p = SelectionHandles.CustomPanelRect(sel, dpi, app.ViewportCanvas, sc);
        DrawPanelCard(app, p, th.CornerRadius * dpi);

        var sv = SelectionHandles.PickSvRect(sel, dpi, app.ViewportCanvas, sc);
        var hue = SelectionHandles.PickHueRect(sel, dpi, app.ViewportCanvas, sc);
        var prev = SelectionHandles.PickPreviewRect(sel, dpi, app.ViewportCanvas, sc);
        var (ph, ps, pv) = app.PickHsv;
        var picked = Hsv.ToRgb(ph, ps, pv);

        EnsurePickBitmaps(dpi, ph);

        // ---- 鈶?楗卞拰搴︼紙妯級锛忔槑搴︼紙绾碉級鏂瑰潡 ----
        if (_pickSvBmp != null)
            _ctx.DrawBitmap(_pickSvBmp, new Vortice.RawRectF(sv.MinX, sv.MinY, sv.MaxX, sv.MaxY),
                            1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.22f);
        _ctx.DrawRectangle(new Vortice.RawRectF(sv.MinX, sv.MinY, sv.MaxX, sv.MaxY), _scratch, 1f * dpi);

        // 閫変腑鐨勯偅涓€鐐癸細鐧藉湀 + 涓€鍦堟殫杈癸紙鍘嬪湪浜壊涓娿€佹殫鑹蹭笂閮界湅寰楄锛?        float sx = sv.MinX + (sv.MaxX - sv.MinX) * ps;
        float sy = sv.MinY + (sv.MaxY - sv.MinY) * pv;
        _ctx.DrawEllipse(new Ellipse(new Vector2(sx, sy), 5.5f * dpi, 5.5f * dpi),
                         Brush(new Color4(1f, 1f, 1f, 1f)), 2.2f * dpi);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.55f);
        _ctx.DrawEllipse(new Ellipse(new Vector2(sx, sy), 6.8f * dpi, 6.8f * dpi), _scratch, 1f * dpi);

        // ---- 鈶?绔栫洿鑹茬浉鏉?----
        if (_pickHueBmp != null)
            _ctx.DrawBitmap(_pickHueBmp, new Vortice.RawRectF(hue.MinX, hue.MinY, hue.MaxX, hue.MaxY),
                            1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.22f);
        _ctx.DrawRectangle(new Vortice.RawRectF(hue.MinX, hue.MinY, hue.MaxX, hue.MaxY), _scratch, 1f * dpi);

        // 褰撳墠鑹茬浉閭ｆ潯鎸囩ず绾匡紙鐧界嚎 + 鏆楄竟锛屽拰鏂瑰潡閭ｉ鐐瑰悓涓€濂楋級
        float hy = hue.MinY + (hue.MaxY - hue.MinY) * ph;
        _ctx.DrawLine(new Vector2(hue.MinX + 1f * dpi, hy), new Vector2(hue.MaxX - 1f * dpi, hy),
                      Brush(new Color4(1f, 1f, 1f, 1f)), 2.2f * dpi);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.45f);
        _ctx.DrawLine(new Vector2(hue.MinX + 1f * dpi, hy - 1.9f * dpi),
                      new Vector2(hue.MaxX - 1f * dpi, hy - 1.9f * dpi), _scratch, 0.9f * dpi);
        _ctx.DrawLine(new Vector2(hue.MinX + 1f * dpi, hy + 1.9f * dpi),
                      new Vector2(hue.MaxX - 1f * dpi, hy + 1.9f * dpi), _scratch, 0.9f * dpi);

        // ---- 鈶?褰撳墠鑹查瑙?----
        // 棰勮鐢荤殑鏄?*鍩鸿壊**锛堣崸鍏夌瑪閫夊嚭鏉ョ殑涔熸槸杩欎釜鑹诧紱瀹為檯鎻忎笂鍘讳細鑷姩淇濆崐閫忔槑锛夈€?        float side = MathF.Min(prev.MaxY - prev.MinY, 30f * dpi);
        var sw = new RoundedRectangle(
            new Vortice.RawRectF(prev.MinX, prev.MinY, prev.MinX + side, prev.MinY + side), 7f * dpi, 7f * dpi);
        _scratch.Color = picked;
        _ctx.FillRoundedRectangle(sw, _scratch);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.22f);
        _ctx.DrawRoundedRectangle(sw, _scratch, 1f * dpi);
        _scratch.Color = th.ActiveBg;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(prev.MinX - 2f * dpi, prev.MinY - 2f * dpi,
                                 prev.MinX + side + 2f * dpi, prev.MinY + side + 2f * dpi),
            8f * dpi, 8f * dpi), _scratch, 1.6f * dpi);
    }

    // ---- 鍙栬壊鏉夸袱寮犵紦瀛樹綅鍥撅紙鑹茬浉/灏哄鍙樹簡鎵嶉噸鐢伙級----------------------------

    private ID2D1Bitmap _pickSvBmp, _pickHueBmp;
    private float _pickSvHue = -1f;
    private int _pickSvPix = -1, _pickHueWPix = -1, _pickHueHPix = -1;

    private void EnsurePickBitmaps(float dpi, float hue)
    {
        int svPix = Math.Max(2, (int)MathF.Round(SelectionHandles.PickSvLogical * dpi));
        int hueW = Math.Max(1, (int)MathF.Round(SelectionHandles.PickHueLogical * dpi));
        int hueH = svPix;

        if (_pickHueBmp == null || _pickHueWPix != hueW || _pickHueHPix != hueH)
        {
            _pickHueBmp?.Dispose();
            _pickHueBmp = CreatePickBitmap(hueW, hueH, (x, y) => Hsv.ToRgb(y / (hueH - 1f), 1f, 1f));
            _pickHueWPix = hueW; _pickHueHPix = hueH;
        }
        if (_pickSvBmp == null || _pickSvPix != svPix || MathF.Abs(_pickSvHue - hue) > 0.004f)
        {
            _pickSvBmp?.Dispose();
            _pickSvBmp = CreatePickBitmap(svPix, svPix,
                (x, y) => Hsv.ToRgb(hue, x / (svPix - 1f), 1f - y / (svPix - 1f)));
            _pickSvPix = svPix; _pickSvHue = hue;
        }
    }

    /// <summary>鎸夐€愬儚绱犲嚱鏁板缓涓€寮?CPU 鏁版嵁鐨勪竴娆℃€?D2D 浣嶅浘锛圔GRA銆佷笉閫忔槑锛夈€?/summary>
    private ID2D1Bitmap CreatePickBitmap(int w, int h, Func<int, int, Color4> colorAt)
    {
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = colorAt(x, y);
                int i = (y * w + x) * 4;
                buf[i + 0] = (byte)Math.Clamp((int)(c.B * 255f + 0.5f), 0, 255);
                buf[i + 1] = (byte)Math.Clamp((int)(c.G * 255f + 0.5f), 0, 255);
                buf[i + 2] = (byte)Math.Clamp((int)(c.R * 255f + 0.5f), 0, 255);
                buf[i + 3] = 255;
            }
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None);
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            return _ctx.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(), (uint)(w * 4), props);
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>灞傜骇灏忛潰鏉匡細缃《 / 缃簳涓ゆ牸锛堝浘鏍囩敤 Fluent 鐨?涓?涓嬬澶?+ 搴曟墭"锛夈€?/summary>
    private void DrawLayerPanel(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        var p = SelectionHandles.LayerPanelRect(sel, dpi, app.ViewportCanvas);
        // 鍦嗚鍚屾牱鍙栫晫闈㈡帹涓婃潵鐨勪富棰樹护鐗岋紙涓夊娴眰涓€涓暟锛?.2.0 鏀跺彛锛夈€?        DrawPanelCard(app, p, app.FloatingTheme.CornerRadius * dpi);

        string[] icons = { IconPaths.toFront, IconPaths.toBack };
        var th = app.FloatingTheme;
        for (int i = 0; i < 2; i++)
        {
            var cell = SelectionHandles.LayerCellRect(i, sel, dpi, app.ViewportCanvas);
            _scratch.Color = th.Hover;
            float cr = 8f * dpi;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY), cr, cr), _scratch);
            float icon = SelectionHandles.BarIconBoxLogical * dpi;
            DrawIcon(icons[i], (cell.MinX + cell.MaxX) * 0.5f - icon * 0.5f,
                     (cell.MinY + cell.MaxY) * 0.5f - icon * 0.5f, icon,
                     Brush(th.Text));
        }
    }


    /// <summary>
    /// 婊氬姩鏉★紙鏍峰紡 B锛歐in11 / Figma 閭ｆ牴缁嗙嚎锛夈€?    ///
    /// 榛樿闅愯棌锛屾粴鍔ㄦ椂娴幇锛?*闈欐 3 绉掓贰鍑?*锛圛nkClass 瀹炴祴 1.5 绉掑お蹇紝
    /// 鐢ㄦ埛浼氭壘涓嶅埌瀹冿級銆傜敾鍦ㄦ诞鍔ㄥ眰涓娾€斺€斿畠姣忓抚閮藉湪鍙橈紙娣″嚭锛夛紝杩涘唴瀹瑰眰灏辩瓑浜?    /// 姣忓抚閲嶇敾鍐呭銆?    ///
    /// 姣斾緥婊戝潡闈?鐢诲竷鑼冨洿"锛堝唴瀹硅竟鐣?鈭?涓€灞忥紝瑙?InkDocument.Extent锛夛細
    /// 娌℃湁鎬婚珮搴﹀氨娌℃湁姣斾緥鍙畻銆傜敾甯冨彧鏈変竴灞忔椂骞茶剢涓嶇敾銆?    ///
    /// 鐢?<c>_scratch</c> 鑰屼笉鏄?Brush(棰滆壊)锛氭贰鍑烘椂棰滆壊姣忓抚閮藉湪鍙橈紝
    /// Brush() 鏄寜棰滆壊缂撳瓨鐨勶紝浼氫竴甯у線缂撳瓨閲屽涓€涓敾鍒枫€?    /// </summary>
    private void DrawScrollBar(InkEngine app)
    {
        if (!app.ScrollBarVisible) return;               // 2026-10-09 璧峰仠鐢紙瑙?Engine.ScrollBarEnabled锛?        if (!TryScrollBar(app, out var sb)) return;      // 鐢诲竷鍙湁涓€灞忥細娌℃湁婊氬姩鏉?
        // 鎮仠/鎷栧姩涓細涓嶆贰鍑猴紝鑰屼笖鍔犵矖鈥斺€?杩欐牴绾胯兘鎷?闈犲畠鑷繁璇达紝
        // 涓嶉潬鎹㈠厜鏍囷紙鎵嬪瀷鎸夎鑼冨彧鑳借〃绀洪摼鎺ワ紝鍥涘悜绠ご浼氳璇绘垚缂╂斁锛夈€?        bool hot = app.ScrollBarHover || app.ScrollBarDragging;
        float dpi = Dpi / 96f;
        double idle = (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0;
        float alpha = hot ? 1f
                    : idle <= 3.0 ? 1f
                    : MathF.Max(0f, 1f - (float)((idle - 3.0) / 0.4));
        if (alpha <= 0.01f) return;

        float w = (hot ? 9f : 4f) * dpi;
        float x0 = sb.AxisX - w * 0.5f;
        float r = w * 0.5f;

        // 杞ㄩ亾锛氭瀬娣★紝鍙敤鏉?鐭ラ亾杩欏効鏈変笢瑗?锛屼笉鎶㈣瑙?        _scratch.Color = new Color4(0.50f, 0.52f, 0.55f, (hot ? 0.22f : 0.16f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.Top, x0 + w, sb.Bottom), r, r), _scratch);

        // 婊戝潡锛氫腑鐏板甫閫忔槑搴︼紝娴呰壊娣辫壊鑳屾櫙涓婇兘绔嬪緱浣?        _scratch.Color = new Color4(0.47f, 0.49f, 0.53f, (hot ? 0.86f : 0.75f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.ThumbTop, x0 + w, sb.ThumbTop + sb.ThumbLen), r, r),
            _scratch);
    }

    /// <summary>
    /// 婊氬姩鏉＄殑鍑犱綍锛?*灞忓箷鍧愭爣**锛屽拰 OriginX/Width 鍚屼竴濂楋級銆?    /// 鐢诲拰鎷栧姩鍏辩敤瀹冣€斺€斾袱澶勫悇绠椾竴浠斤紝鏀逛竴澶勫繕涓€澶勬槸杩欑被浠ｇ爜鐨勭粡鍏哥炕杞︾偣銆?    /// 杩斿洖 false = 鐜板湪涓嶈鏈夋粴鍔ㄦ潯锛堢敾甯冨彧鏈変竴灞忥級銆?    /// </summary>
    internal bool TryScrollBar(InkEngine app, out ScrollBarLayout l)
    {
        l = default;
        float dpi = Dpi / 96f;
        var extent = app.CanvasExtent;
        float extentH = extent.MaxY - extent.MinY;
        if (extentH <= Height + 1f) return false;

        float top = OriginY + 40f * dpi;
        float bottom = OriginY + Height - 40f * dpi;
        float trackLen = bottom - top;
        if (trackLen < 40f * dpi) return false;

        // 婊戝潡闀垮害 = 涓€灞?/ 鐢诲竷鎬婚珮锛涗綅缃?= 瑙嗗彛椤跺湪鐢诲竷閲岀殑姣斾緥
        float thumbLen = MathF.Max(40f * dpi, trackLen * (Height / extentH));
        float maxTravel = trackLen - thumbLen;
        float viewTop = OriginY - app.ViewOffsetY;       // 瑙嗗彛椤堕儴瀵瑰簲鐨勭敾甯?Y
        float frac = extentH - Height > 1f
            ? (viewTop - extent.MinY) / (extentH - Height) : 0f;
        frac = Math.Clamp(frac, 0f, 1f);

        l = new ScrollBarLayout
        {
            AxisX = OriginX + Width - 8f * dpi,
            Top = top,
            Bottom = bottom,
            ThumbTop = top + maxTravel * frac,
            ThumbLen = thumbLen,
            MaxTravel = maxTravel,
            ExtentMinY = extent.MinY,
            ExtentH = extentH,
        };
        return true;
    }

    /// <summary>
    /// 鐢讳竴涓洓杈瑰舰銆傞€変腑妗嗙幇鍦ㄦ槸杞村榻愮殑锛屼絾杈规浠嶇劧鐢ㄥ洓鏉＄嚎鎷硷細
    /// 杩欐潯璺緞鍚屾椂瑕佽兘鐢?妗嗘湁鑷繁鐨勬湞鍚?鐨勬儏褰紙瑙?SelectionFrame 鐨勬敞閲婏級锛?    /// 鎹㈡垚 DrawRectangle 灏辩瓑浜庢妸閭ｆ潯璺牭姝讳簡銆?    /// </summary>
    private void DrawQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float width)
    {
        _ctx.DrawLine(a, b, _scratch, width);
        _ctx.DrawLine(b, c, _scratch, width);
        _ctx.DrawLine(c, d, _scratch, width);
        _ctx.DrawLine(d, a, _scratch, width);
    }

    /// <summary>
    /// 鐢讳竴涓?*铏氱嚎**鍥涜竟褰紙澶嶅埗鎷栨嫿妯″紡鐨勬鐢級銆?    ///
    /// 涓轰粈涔堜笉鐢?D2D 鐨?dash style锛氶偅瑕佺粰姣忕绾垮鍚勫缓涓€涓?`ID2D1StrokeStyle`
    /// 骞剁鐢熷懡鍛ㄦ湡锛岃€岀幇鍦ㄥ彧鏈変袱澶勮铏氱嚎锛堣繖涓 + 闈㈡澘閲岀殑"铏氱嚎"绀烘剰锛夈€傛墜绠楀垎娈?    /// 鍗佹潵琛屻€佺粨鏋滃畬鍏ㄥ彲鎺э紝涔熶笉鐢ㄦ媴蹇冨悇鐗堟湰 Vortice 鐨?API 宸紓銆?    /// </summary>
    private void DrawQuadDashed(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float width)
    {
        DrawDashed(a, b, width);
        DrawDashed(b, c, width);
        DrawDashed(c, d, width);
        DrawDashed(d, a, width);
    }

    private void DrawDashed(Vector2 a, Vector2 b, float width) => DrawDashes(a, b, width, 7f, 5f);

    /// <summary>
    /// 鐐圭嚎锛堢嚎鍨嬮偅涓€琛岀殑绗?3 鏍硷級锛氳妭**鐭?*銆佺紳姣旇妭闀匡紝鎵嶇湅寰楀嚭鏄?涓€鐐逛竴鐐?
    /// 鑰屼笉鏄?寰堢煭鐨勪竴娈垫"銆?    /// </summary>
    private void DrawDotted(Vector2 a, Vector2 b, float width) => DrawDashes(a, b, width, 2f, 4.5f);

    /// <summary>
    /// 鎵嬪伐鐢昏櫄绾?/ 鐐圭嚎锛?paramref name="dash"/> 涓?<paramref name="gap"/> 鏄?*閫昏緫**鍍忕礌銆?    ///
    /// 涓轰粈涔堣繖閲屾墜宸ョ敾銆佷笉鐢?<see cref="Dashed"/> 閭ｆ潯 D2D 鎻忚竟鏍峰紡锛?    /// 杩欎釜鍑芥暟鐢荤殑鏄?*鐣岄潰涓婄殑灏忛瑙?*锛堥€変腑妗嗗洓瑙掋€佺嚎鍨嬫牸瀛愰噷鐨勯偅涓€娈碉級锛?    /// 鐢ㄧ殑鏄?涓€鏉＄洿绾挎"鑰屼笉鏄浘褰㈠嚑浣曪紝棰勮瑕佺殑鏄?涓€鐪肩湅鍑鸿櫄瀹?锛?    /// 鍥哄畾鑺傞暱姣?鎸夌瑪瀹芥垚姣斾緥"鏇村彲鎺с€?    /// **鐢诲竷涓婄殑鍥惧舰璧?D2D 鍘熺敓鏍峰紡**锛堣 <see cref="StyleFor"/>锛夛紝涓ゆ潯璺悇鑷渶鍚堥€傘€?    /// </summary>
    private void DrawDashes(Vector2 a, Vector2 b, float width, float dash, float gap)
    {
        float len = Vector2.Distance(a, b);
        if (len < 0.5f) return;
        var dir = (b - a) / len;
        float k = Dpi / 96f;
        dash *= k; gap *= k;
        for (float t = 0; t < len; t += dash + gap)
        {
            float t1 = MathF.Min(t + dash, len);
            _ctx.DrawLine(a + dir * t, a + dir * t1, _scratch, width);
        }
    }

    /// <summary>
    /// 鐢讳竴涓浘鏍囥€傚嚑浣曟寜 24脳24 鐨勫崟浣嶅潗鏍囩紦瀛橈紝杩欓噷鐢ㄥ彉鎹㈢缉鏀惧埌鐩爣灏哄鈥斺€?    /// 鍚屼竴浠藉嚑浣曡兘鍦ㄤ笉鍚屽昂瀵稿拰 DPI 涓嬪鐢紝涓嶇敤閲嶅缓銆?    /// </summary>
    private void DrawIcon(string d, float x, float y, float size, ID2D1SolidColorBrush brush)
    {
        var geo = SvgPath.Get(d);
        if (geo == null) return;

        var saved = _ctx.Transform;
        _ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                       * Matrix3x2.CreateTranslation(x, y) * saved;
        _ctx.FillGeometry(geo, brush);
        _ctx.Transform = saved;
    }

    /// <summary>
    /// PPT 鏀炬槧鏃惰创鍦ㄥ睆骞曚笂鐨?*閭ｄ竴鏉?*鎺т欢锛坄[鈼€] [椤电爜] [鈻禲`锛夛紝澶栧姞瀹冧笂闈㈢殑涓や釜娴眰
    /// 锛堢偣椤电爜寮圭殑**椤靛彿闈㈡澘**銆侀暱鎸夊脊鐨勮彍鍗曪級銆傚嚑浣曞湪 `PptBar`銆佷氦浜掔姸鎬佸湪 `InkEngine`锛圥pt.cs锛夈€?    ///
    /// 瀹冩槸**娴眰**锛堝拰閫変腑鎿嶄綔鏉″悓涓€灞傘€佸悓涓€濂椾护鐗岋級鈥斺€斾綅缃彧璺熷睆骞曡蛋锛堝彲鎷栥€佸彲璁帮級锛?    /// 涓嶈窡宸ュ叿鏉°€佷笉鍙楄创杈归殣钘忓奖鍝嶏紙鐢ㄦ埛 2026-09-26锛?澶栭儴灏辨槸 ppt 椤甸潰涓婏紝
    /// 涓嶈窡鐫€鎵规敞杞欢璧?锛夈€?    /// </summary>
    private void DrawPptBar(InkEngine app)
    {
        if (!app.PageBarVisible) return;     // PPT 鏀炬槧 / 鏂囨。鍏辩敤锛涙枃妗?绌块€忔椂鏀惰蛋锛圫2 鍏ㄨ寮€锛?        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        var bar = app.PptBarRect();
        float radius = MathF.Min(theme.CornerRadius * dpi, (bar.MaxY - bar.MinY) * 0.5f);

        DrawPanelCard(app, bar, radius);

        // **鎷栧姩涓?*锛氭暣鏉℃弿杈瑰彉寮鸿皟鑹诧紙"瀹冪幇鍦ㄦ嬁鍦ㄤ綘鎵嬮噷"锛夈€傛嫋璧锋潵鏈韩鐪嬪緱瑙侊紝
        // 杩欎釜鎻忚竟鏄粰"鎶撹捣鏉ラ偅涓€鍒?涓€涓槑纭洖搴斻€?        if (app.PptBarDragging)
        {
            _scratch.Color = theme.ActiveBg;
            _ctx.DrawRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(bar.MinX, bar.MinY, bar.MaxX, bar.MaxY),
                                     radius, radius), _scratch, 2f * dpi);
        }

        float aw = PptBar.ArrowW * dpi;
        var la = new RectF { MinX = bar.MinX, MinY = bar.MinY, MaxX = bar.MinX + aw, MaxY = bar.MaxY };
        var ra = new RectF { MinX = bar.MaxX - aw, MinY = bar.MinY, MaxX = bar.MaxX, MaxY = bar.MaxY };
        DrawPptArrow(app, la, left: true, hot: app.PptBarHover == (int)PptBarZone.LeftArrow);
        DrawPptArrow(app, ra, left: false, hot: app.PptBarHover == (int)PptBarZone.RightArrow);

        // 椤电爜鏍肩殑**鎮仠楂樹寒**锛堢敤鎴?2026-09-26 鎸囧嚭锛?椤电爜閭ｉ噷娌℃湁鎮仠鎸囩ず"锛夈€?        // 绠ご鏃╁氨鏈夈€佸氨瀹冩病鏈夆€斺€旇€屽畠鎭版伆鏄?鎸変綇涓嶅姩寮硅彍鍗?/ 鐐逛竴涓嬪脊椤靛彿闈㈡澘"鐨勯偅鍧楋細
        // 娌℃湁楂樹寒锛屾暣鏉′笂鏈€璇ョ偣鐨勫湴鏂圭湅鐫€鍙嶈€屾槸"姝荤殑"銆?        // 銆屸嫯銆嶅垹鎺変箣鍚庯紙2026-09-27锛夎繖涓€鍧楀氨鏄?*鏁翠釜涓牸**锛屾暟瀛椾篃鍦ㄥ畠鐨勬涓棿銆?        if (app.PptBarHover == (int)PptBarZone.Page)
        {
            var pc = PptBar.MidCell(bar, dpi);
            float cIn = 4f * dpi;
            var cBg = new Vortice.RawRectF(pc.MinX + cIn, pc.MinY + cIn, pc.MaxX - cIn, pc.MaxY - cIn);
            float cBr = MathF.Min(cBg.Right - cBg.Left, cBg.Bottom - cBg.Top) * 0.28f;
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(cBg, cBr, cBr), _scratch);
        }

        // 椤电爜锛?*鏉′笂鐨勪富瑙?*锛?3 閫昏緫鍍忕礌锛屾瘮瑙掓爣澶т竴妗ｏ級锛氭暟瀛楃敤姝ｆ枃鑹层€?/ 鎬绘暟"鐢ㄦ瑕佽壊銆?        // 鍒嗕袱娈电敾灏辨槸"灞傜骇"鈥斺€斾竴涓瓧鍙蜂竴鍙ｈ壊鐨勮瘽锛?3" 鍜?"/ 12" 涓€鏍烽噸锛岃涓嶅嚭"鎴戝湪绗嚑椤?銆?        // 鈿?`ReadoutFormat*` 閭ｄ竴鏃忕殑鍙傛暟鏄?*缂╂斁姣?*锛坄px = 13 * dpi`锛夛紝涓嶆槸鐗╃悊 DPI鈥斺€?        // 浼?`Dpi`锛?00% 灞忎笂鏄?192锛変細鎶婂瓧鍙锋斁澶?192 鍊嶃€佸瓧琚帓鍒扮煩褰㈠锛岀湅璧锋潵灏辨槸"瀛楁病鐢诲嚭鏉?銆?        var mid = PptBar.MidCell(bar, dpi);
        var pfmt = PptPageFormat(dpi);
        string pCur = app.BarPageNow.ToString();
        string pRest = $" / {app.BarTotal}";
        float pWCur = MeasureTextWidth(pCur, pfmt);
        float pWRest = MeasureTextWidth(pRest, pfmt);
        float pX0 = (mid.MinX + mid.MaxX) * 0.5f - (pWCur + pWRest) * 0.5f;
        _scratch.Color = theme.Text;
        _ctx.DrawText(pCur, pfmt, new Rect(pX0, mid.MinY, pWCur, mid.MaxY - mid.MinY), _scratch);
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText(pRest, pfmt, new Rect(pX0 + pWCur, mid.MinY, pWRest, mid.MaxY - mid.MinY), _scratch);

        if (app.PptMode && app.PptHintVisible) DrawPptHint(app);
        if (app.PptPagePanelOpen) DrawPptPagePanel(app);
        if (app.PptMenuOpen) DrawPptMenu(app);
    }

    /// <summary>
    /// **椤靛彿闈㈡澘**锛氱偣椤电爜寮瑰嚭鏉ョ殑閭ｅ紶鏍煎瓙澧欙紙鐐瑰摢椤佃烦鍝〉锛夈€傜敤鎴?2026-09-26 閫夌殑鏂规 B
    /// 锛?鎴戜滑涓嬮潰閭ｄ釜杩涘害鏉″彲浠ヤ笉鐢紝鎴戜滑鍙互鐐瑰嚮椤电爜杩欓噷瀹炵幇蹇€熻烦椤?锛夈€?    ///
    /// 涓轰粈涔堝綊寮曟搸鐢伙紙鑰屼笉鏄晫闈級锛氬畠闀垮湪鏉＄殑姝ｄ笂鏂广€佽璺熺潃鏉＄殑浣嶇疆璧帮紝
    /// 鑰屾潯鏈韩灏辨槸寮曟搸鐢荤殑锛堣 PptBar锛夛紱浜ょ粰鐣岄潰浼氬涓€鏉?浣嶇疆鍚屾"鐨勭紳銆?    /// 鐢绘硶鐓ф妱鐜版垚鐨勫浘搴撻潰鏉匡紙鍚屼竴濂?DrawPanelCard + 鎮仠楂樹寒锛夈€?    /// </summary>
    // ---- 鍥炴斁锛堝ⅷ杩?C锛?------------------------------------------------------

    /// <summary>
    /// 鍥炴斁涓?姝ｅ湪闀?鐨勯偅涓€鏉°€?*宸茬粡鍑哄畬鐨勯兘鍦ㄥ唴瀹瑰眰锛堝奖瀛愭枃妗ｏ級閲?*锛?    /// 杩欓噷鍙敾鍓嶇紑锛氳嚜鐢辩瑪杩?鈫?涓績绾垮墠缂€锛堝帇鎰熶粛璧?D2D 鍙樺锛夛紱鍥惧舰/鍥剧墖 鈫?鍒扮偣鏁存潯鍑虹幇銆?    /// </summary>
    private void DrawReplayCurrent(InkEngine app)
    {
        int idx = app.ReplayCurrentIndexNow;
        if (idx < 0 || idx >= app.ReplayStrokesNow.Count) return;
        var s = app.ReplayStrokesNow[idx];
        float param = app.ReplayCurrentParamNow;

        if (s.Kind != StrokeKind.Freehand || s.IsImage)
        {
            DrawStroke(s);                      // 鍥惧舰 / 鍥剧墖锛氭暣鏉″嚭鐜帮紙纭媶鎴愰€愮偣鍙嶈€屾€級
            return;
        }
        if (s.Points.Count == 0) return;
        if (s.RendersAsDot)
        {
            float rad = s.DotRadius();
            _ctx.FillEllipse(new Ellipse(new Vector2(s.Points[0].X, s.Points[0].Y),
                                         rad, rad),
                             Brush(s.Color));
            return;
        }

        float pointParam = param * (s.Points.Count - 1);
        if (pointParam <= 0.02f) return;        // 鍒氳捣姝ワ細杩樻病鏈夊彲鐢荤殑娈?        if (s.HasPressure && s.Dash == StrokeDash.Solid && s.Erased.Count == 0
            && DrawReplayPressurePrefix(s, pointParam))
            return;

        var geo = s.BuildCenterlinePrefix(Gfx.D2DFactory, pointParam);
        if (geo == null) return;
        try
        {
            _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.StyleFor(s.Dash));
        }
        finally { geo.Dispose(); }
    }

    /// <summary>鍘嬫劅鍓嶇紑锛氬悓 <see cref="DrawPressureInk"/> 閭ｆ潯璺紝鍙槸鍙杺鍒板墠缂€銆?/summary>
    private bool DrawReplayPressurePrefix(Stroke s, float pointParam)
    {
        if (_ctx2 == null || _inkStyle == null || !InkAvailable || !PressureWidth.Enabled) return false;
        if (!BuildPressureSegments(s, out float startRadius, out int nSeg, pointParam)) return false;
        try
        {
            var ink = _ctx2.CreateInk(new Vortice.Direct2D1.InkPoint
            {
                X = s.Points[0].X, Y = s.Points[0].Y, Radius = startRadius,
            });
            try
            {
                ink.AddSegments(_inkSegs, (uint)nSeg);
                _ctx2.DrawInk(ink, Brush(s.Color), _inkStyle);
            }
            finally { ink.Dispose(); }
            return true;
        }
        catch { InkAvailable = false; return false; }
    }

    /// <summary>
    /// 鍥炴斁鎺у埗鏉°€傚拰 PPT 鏉″悓涓€灞傘€佸悓涓€濂楃敾娉曪紙`DrawPanelCard` 锛?涓婚鑹诧級锛?    /// 鍐呭鏄挱鏀惧櫒閭ｄ竴濂楋細鎾斁/鏆傚仠銆佸洓妗ｅ€嶉€熴€佽繘搴︺€佽鏁般€佸叧闂€?    /// </summary>
    private void DrawReplayBar(InkEngine app)
    {
        if (!app.ReplayActive) return;
        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        var bar = app.ReplayBarRect();
        float radius = MathF.Min(theme.CornerRadius * dpi, (bar.MaxY - bar.MinY) * 0.5f);
        DrawPanelCard(app, bar, radius);

        bool Hot(ReplayBarZone z) => app.ReplayHoverZone == z;
        void FillZone(in RectF r, Color4 color)
        {
            _scratch.Color = color;
            float rad = 6f * dpi;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(r.MinX, r.MinY + 3f * dpi, r.MaxX, r.MaxY - 3f * dpi), rad, rad), _scratch);
        }

        // 鎾斁 / 鏆傚仠
        var pr = ReplayBar.ZoneRect(bar, ReplayBarZone.PlayPause, dpi);
        if (Hot(ReplayBarZone.PlayPause)) FillZone(pr, theme.Hover);
        _scratch.Color = theme.Text;
        var pc = new Vector2((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
        if (app.ReplayPlaying)
        {
            float w = 4f * dpi, h = 15f * dpi, gap = 5f * dpi;
            _ctx.FillRectangle(new Vortice.RawRectF(pc.X - gap * 0.5f - w, pc.Y - h * 0.5f,
                                                    pc.X - gap * 0.5f, pc.Y + h * 0.5f), _scratch);
            _ctx.FillRectangle(new Vortice.RawRectF(pc.X + gap * 0.5f, pc.Y - h * 0.5f,
                                                    pc.X + gap * 0.5f + w, pc.Y + h * 0.5f), _scratch);
        }
        else
        {
            float h = 15f * dpi, w = 13f * dpi;
            using var path = Gfx.D2DFactory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(new Vector2(pc.X - w * 0.45f, pc.Y - h * 0.5f), FigureBegin.Filled);
                sink.AddLine(new Vector2(pc.X - w * 0.45f, pc.Y + h * 0.5f));
                sink.AddLine(new Vector2(pc.X + w * 0.55f, pc.Y));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _ctx.FillGeometry(path, _scratch);
        }

        // 鍥涙。鍊嶉€?        for (int i = 0; i < 4; i++)
        {
            var zone = (ReplayBarZone)(ReplayBarZone.SpeedHalf + i);
            var zr = ReplayBar.ZoneRect(bar, zone, dpi);
            float speed = ReplayBar.SpeedOfZone(zone);
            bool on = MathF.Abs(speed - app.ReplaySpeed) < 0.01f;
            var rr = new RoundedRectangle(new Vortice.RawRectF(zr.MinX, zr.MinY + 5f * dpi,
                                                              zr.MaxX, zr.MaxY - 5f * dpi), 6f * dpi, 6f * dpi);
            if (on) { _scratch.Color = theme.ActiveBg; _ctx.FillRoundedRectangle(rr, _scratch); }
            else if (Hot(zone)) { _scratch.Color = theme.Hover; _ctx.FillRoundedRectangle(rr, _scratch); }
            _scratch.Color = on ? theme.ActiveText : theme.TextMuted;
            _ctx.DrawText(ReplayBar.SpeedName(speed), ReadoutFormatSmall(dpi),
                          new Rect(zr.MinX, zr.MinY, zr.MaxX - zr.MinX, zr.MaxY - zr.MinY), _scratch);
        }

        // 杩涘害锛氳建閬?锛?宸叉挱 锛?婊戦挳
        var track = ReplayBar.ProgressTrack(bar, dpi);
        float totalMs = MathF.Max(1f, app.ReplayTotalMsNow);
        float frac = Math.Clamp(app.ReplayPosMsNow / totalMs, 0f, 1f);
        float cy = (track.MinY + track.MaxY) * 0.5f;
        float knobR = 5f * dpi;
        float x0 = track.MinX + knobR, x1 = track.MaxX - knobR;
        float kx = x0 + (x1 - x0) * frac;
        if (Hot(ReplayBarZone.Progress))
        {
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(track.MinX, cy - 6f * dpi, track.MaxX, cy + 6f * dpi),
                6f * dpi, 6f * dpi), _scratch);
        }
        _scratch.Color = theme.TextMuted;
        _ctx.DrawLine(new Vector2(x0, cy), new Vector2(x1, cy), _scratch, 3f * dpi);
        _scratch.Color = theme.ActiveBg;
        _ctx.DrawLine(new Vector2(x0, cy), new Vector2(kx, cy), _scratch, 3f * dpi);
        _scratch.Color = theme.Text;
        _ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), knobR, knobR), _scratch);

        // 璇绘暟锛堣繘搴﹀尯鍙充晶銆佸叧闂敭宸︿晶锛?        float timeL = track.MaxX + 6f * dpi;
        float timeR = bar.MaxX - (ReplayBar.Pad + ReplayBar.CloseW + ReplayBar.Gap) * dpi;
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText($"{ReplayBar.TimeText(app.ReplayPosMsNow)} / {ReplayBar.TimeText(totalMs)}",
                      ReadoutFormatSmall(dpi),
                      new Rect(timeL, bar.MinY, MathF.Max(1f, timeR - timeL), bar.MaxY - bar.MinY), _scratch);

        // 鍏抽棴
        var cr = ReplayBar.ZoneRect(bar, ReplayBarZone.Close, dpi);
        if (Hot(ReplayBarZone.Close)) FillZone(cr, theme.Hover);
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText("鉁?, ReadoutFormatSmall(dpi),
                      new Rect(cr.MinX, cr.MinY, cr.MaxX - cr.MinX, cr.MaxY - cr.MinY), _scratch);
    }

    private ID2D1SolidColorBrush SetBrush(Color4 c)
    {
        _scratch.Color = c;
        return _scratch;
    }

    private IDWriteTextFormat RollResultFormat(float dpi, float size)
    {
        float px = MathF.Max(12f, MathF.Round(size * dpi));
        if (_rollResultFormat == null || _rollResultFormatPx != px)
        {
            _rollResultFormat?.Dispose();
            _rollResultFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _rollResultFormat.TextAlignment = TextAlignment.Center;
            _rollResultFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _rollResultFormatPx = px;
        }
        return _rollResultFormat;
    }


    private IDWriteTextFormat TimerValueFormat(float dpi)
    {
        float px = MathF.Max(12f, MathF.Round(19f * dpi));
        if (_timerValueFormat == null || _timerValueFormatPx != px)
        {
            _timerValueFormat?.Dispose();
            _timerValueFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _timerValueFormat.TextAlignment = TextAlignment.Center;
            _timerValueFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _timerValueFormatPx = px;
        }
        return _timerValueFormat;
    }

    private IDWriteTextFormat TimerBigFormat(float dpi)
    {
        float px = MathF.Max(24f, MathF.Round(92f * dpi));
        if (_timerBigFormat == null || _timerBigFormatPx != px)
        {
            _timerBigFormat?.Dispose();
            _timerBigFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _timerBigFormat.TextAlignment = TextAlignment.Center;
            _timerBigFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _timerBigFormatPx = px;
        }
        return _timerBigFormat;
    }

    private void DrawPptPagePanel(InkEngine app)
    {
        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        app.PptPanelRect(out var panel);
        DrawPanelCard(app, panel, theme.CornerRadius * dpi);

        int total = Math.Max(1, app.BarTotal);
        var fmt = ReadoutFormatSmall(dpi);
        for (int i = 0; i < total; i++)
        {
            app.PptPanelCellRectAt(i, out var cell);
            int page = i + 1;
            bool cur = page == app.BarPageNow;           // 褰撳墠椤碉細瀹炲績寮鸿皟搴?            bool hot = app.PptBarHover == 200 + i;

            if (cur || hot)
            {
                float inset = 2f * dpi;
                var bg = new Vortice.RawRectF(cell.MinX + inset, cell.MinY + inset,
                                              cell.MaxX - inset, cell.MaxY - inset);
                float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.25f;
                _scratch.Color = cur ? theme.ActiveBg : theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            _scratch.Color = cur ? theme.ActiveText : theme.Text;
            _ctx.DrawText(page.ToString(), fmt,
                          new Rect(cell.MinX, cell.MinY, cell.MaxX - cell.MinX, cell.MaxY - cell.MinY),
                          _scratch);
        }
    }

    /// <summary>椤电爜鐨勬枃瀛楁牸寮忥紙**13 閫昏緫鍍忕礌**锛氭潯涓婄殑涓昏锛屾瘮瑙掓爣 11 澶т竴妗ｏ級銆?/summary>
    private IDWriteTextFormat PptPageFormat(float dpi)
    {
        float px = MathF.Max(11f, MathF.Round(13f * dpi));
        if (_pptPageFmt == null || _pptPageFmtPx != px)
        {
            _pptPageFmt?.Dispose();
            _pptPageFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _pptPageFmt.TextAlignment = TextAlignment.Center;
            _pptPageFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _pptPageFmtPx = px;
        }
        return _pptPageFmt;
    }

    /// <summary>
    /// 缈婚〉绠ご锛?*鑷粯**锛堜袱鏉＄嚎缁勬垚鐨?chevron锛夈€?    ///
    /// 涓轰粈涔堜笉鐢?`IconPaths`锛氶偅寮犺〃鏄?`tools/gen-icons.ps1` 浠庡畼鏂瑰浘鏍囧簱鐢熸垚鐨?    /// 锛堟枃浠跺ご鍐欑潃"璇峰嬁鎵嬫敼"锛夛紝鑰屼笖閲岄潰娌℃湁 chevron锛坄chevronRight` 鍦?*鐣岄潰**閭ｆ湰
    /// `InkUi/Icons.g.cs` 閲岋紝寮曟搸鐪嬩笉鍒帮級鈥斺€斾负涓や釜绠ご鏀圭敓鎴愭祦绋嬩笉鍒掔畻锛?    /// 涓ゆ潯绾挎洿绠€鍗曪紝涔熶笉浼氶殢鍥炬爣搴撶増鏈紓绉汇€?    ///
    /// **瑙嗚閲嶉噺**锛堢敤鎴?2026-09-26锛?鎴戞劅瑙夋槸杩欎釜缈婚〉绠ご澶ぇ浜?锛夛細
    ///   路 **灏哄鍩烘湰涓嶇缉**锛氭垜浠珮 15 閫昏緫鍍忕礌 / 鍗＄墖 56 = 27%锛岃€?Inkeys 鐨勭澶村浘鏍囨
    ///     鏄?40 / 缁勪欢 60 = **67%**锛堟暟鎹 璋冪爺-PPT妯″紡-鍙傝€僆nkClass.md锛夆€斺€?    ///     鎴戜滑鏈潵灏辨瘮浜哄灏忎竴鍗婂锛屽啀缂╄Е鎽稿睆涓婂氨鐐逛笉涓簡锛岃繖閲屽彧寰敹 7%锛?    ///   路 **鐪熸閲嶇殑鏄瑪鐢?*锛? 鍍忕礌瀹界殑 chevron 閰?2 閫昏緫鍍忕礌绾垮锛堢墿鐞?4px锛夛紝
    ///     鐭瑪鐢讳笂鏄惧緱鍙堢矖鍙堥粦 鈫?绾垮闄嶅埌 1.5锛堝榻愰」鐩浘鏍囪鑼?1.4~1.5 鎻忚竟"锛?    ///     瑙?璋冪爺-鐣岄潰-鍋囬潰鏉夸笌鍥炬爣.md锛夛紱
    ///   路 甯告€佺敤**娆¤鑹?*銆佹偓鍋滄墠杞繁鈥斺€旈〉鐮佹墠鏄富瑙掞紝绠ご鏄厤瑙掋€?    /// </summary>
    private void DrawPptArrow(InkEngine app, in RectF box, bool left, bool hot)
    {
        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        if (hot)
        {
            float inset = 4f * dpi;
            var bg = new Vortice.RawRectF(box.MinX + inset, box.MinY + inset,
                                          box.MaxX - inset, box.MaxY - inset);
            float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.3f;
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
        }

        float cx = (box.MinX + box.MaxX) * 0.5f, cy = (box.MinY + box.MaxY) * 0.5f;
        float h = 7f * dpi;        // 鍗婇珮锛堝師 7.5锛屽井鏀讹級
        float w = 4.2f * dpi;      // 涓ょ考鐩稿涓績鐨勫亸绉伙紙鍘?4.5锛?        float wing = left ? w : -w;         // 宸︾澶达細涓ょ考鍦ㄥ彸杈广€佸皷绔湪宸?        float tipX = cx - wing;
        _scratch.Color = hot ? theme.Text : theme.TextMuted;
        float lw = 1.5f * dpi;
        _ctx.DrawLine(new Vector2(cx + wing, cy - h), new Vector2(tipX, cy), _scratch, lw);
        _ctx.DrawLine(new Vector2(tipX, cy), new Vector2(cx + wing, cy + h), _scratch, lw);
    }

    /// <summary>
    /// 鑿滃崟锛?*闀挎寜椤电爜鏍?*寮€锛夈€備笁椤?*涓€寰嬪洓瀛?*锛堢敤鎴?2026-09-26锛?鑿滃崟鍙互鍥涘瓧瀵归綈鍚?锛夛紝
    /// 鎵€浠ユ枃瀛楀ぉ鐒跺榻愩€佷笉鐢ㄩ€愰」閲忓锛涘垎闅旂嚎鏄?涓€椤逛竴缁?銆?    ///
    /// 涓や釜涓存椂鏍峰瓙锛?    ///   路 銆屽ⅷ杩逛繚瀛樸€嶅彸杈硅窡涓€涓?寮€ / 鍏?锛堝紑鍏抽」锛夛紱
    ///   路 銆屽ⅷ杩规竻绌恒€嶇偣杩囦竴娆′箣鍚庯紝鏁磋鏂囧瓧鍙樻垚銆屽啀鐐圭‘璁ゃ€嶅苟涓婂己璋冨簳鈥斺€?    ///     杩欐槸**涓嶅脊绐楃殑纭**锛堟垜浠殑瑙勭煩鏄?灞忓箷涓婁竴涓瓧閮戒笉鐣?锛岃 README 鐨勬彁绀烘潯閭ｆ锛夈€?    /// </summary>
    private void DrawPptMenu(InkEngine app)
    {
        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        app.PptMenuRect(out var menu);
        DrawPanelCard(app, menu, theme.CornerRadius * dpi);

        var fmt = ReadoutFormatSmall(dpi);
        float padX = 16f * dpi;

        for (int i = 0; i < InkEngine.PptMenuItemCount; i++)
        {
            app.PptMenuItemRectAt(i, out var item);

            // 鍒嗛殧绾跨敾鍦?杩欎竴椤瑰拰涓婁竴椤逛箣闂?锛堜竴鐪肩湅鍑哄垎缁勶級
            if (InkEngine.PptMenuDividerBefore(i) && i > 0)
            {
                app.PptMenuItemRectAt(i - 1, out var prev);
                float y = (prev.MaxY + item.MinY) * 0.5f;
                _scratch.Color = new Color4(theme.PanelBorder.R, theme.PanelBorder.G,
                                            theme.PanelBorder.B, 0.6f);
                _ctx.DrawLine(new Vector2(item.MinX + 10f * dpi, y),
                              new Vector2(item.MaxX - 10f * dpi, y), _scratch, 1f * dpi);
            }

            bool danger = InkEngine.PptMenuItemDanger(i);
            bool waiting = danger && app.PptClearConfirm;       // 姝ｅ湪绛夌‘璁わ細鏁磋涓婂己璋冨簳
            bool enabled = app.PptMenuItemEnabled(i);           // 缃伆椤癸細涓嶄寒銆佷笉楂樹寒銆佺偣浜嗕笉鍔?            if (enabled && (app.PptBarHover == 100 + i || waiting))
            {
                float inset = 3f * dpi;
                var bg = new Vortice.RawRectF(item.MinX + inset, item.MinY + inset,
                                              item.MaxX - inset, item.MaxY - inset);
                float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.25f;
                _scratch.Color = waiting ? theme.ActiveBg : theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            // 鏂囧瓧锛氭瀹?*鎸夋枃瀛楄嚜宸遍噺**锛堣€屼笉鏄暣琛岋級鈥斺€旇繖鏍峰洓瀛椾粠宸﹁捣涓€鏍风殑浣嶇疆寮€濮嬶紝
            // 灏辨槸"鍥涘瓧瀵归綈"锛涚敤鏁磋灞呬腑鎺掔殑璇濓紝瀛楁暟涓€鍙橈紙"鍐嶇偣纭"锛夊氨浼氬乏鍙虫檭銆?            string label = app.PptMenuItemText(i);
            float lw = MeasureTextWidth(label, fmt);
            _scratch.Color = waiting ? theme.ActiveText : enabled ? theme.Text : theme.TextMuted;
            _ctx.DrawText(label, fmt,
                          new Rect(item.MinX + padX, item.MinY, lw, item.MaxY - item.MinY),
                          _scratch);

            // 鍙宠竟鐨勭姸鎬侊紙寮€鍏?= 寮€/鍏筹紱鍥炴斁 = N 绗旓級
            string st = app.PptMenuItemStatus(i);
            if (st != null)
            {
                float sw = MeasureTextWidth(st, fmt);
                _scratch.Color = i == 0 && app.PptAutoSaveOn ? theme.ActiveBg : theme.TextMuted;
                _ctx.DrawText(st, fmt,
                              new Rect(item.MaxX - padX - sw, item.MinY, sw, item.MaxY - item.MinY),
                              _scratch);
            }
        }
    }

    /// <summary>
    /// **寮曞**锛氳繘鏀炬槧鐨勫ご 1.5 绉掞紝鍦ㄦ潯鐨勪笂鏂规诞涓€琛屽皬瀛楋紙"闀挎寜椤电爜鍙懠鍑鸿彍鍗?锛夈€?    ///
    /// 浣嶇疆**鍜岃彍鍗曞悓涓€澶?*鈥斺€斿畠娣″嚭涔嬪悗鑿滃崟姝ｅソ鍦ㄩ偅鍎垮嚭鐜帮紝鑰佸笀浼氳嚜鐒跺湴鎶婁袱浠朵簨杩炶捣鏉ャ€?    /// 鐢熷懡鍛ㄦ湡瑙?<see cref="InkEngine.PptHintVisible"/>锛?*姣忔杩涙斁鏄犻兘鎻愮ず涓€閬嶃€佸彧鍋?1.5 绉?*
    /// 锛堢敤鎴?2026-09-27 瀹氱殑锛夆€斺€斿洜涓恒€屸嫯銆嶅垹鎺変箣鍚庤彍鍗曞彧鍓╅暱鎸夎繖涓€鏉¤矾锛?    /// "鍙彁绀轰竴娆?浼氳鑰佸笀绗簩娆′笂璇惧氨鎯充笉璧锋潵瀹冧簡銆?    /// </summary>
    private void DrawPptHint(InkEngine app)
    {
        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        app.PptMenuRect(out var anchor);            // 鍊熻彍鍗曠殑浣嶇疆锛堝氨鏄潯鐨勪笂鏂癸級
        var hint = new RectF
        {
            MinX = anchor.MinX, MinY = anchor.MinY,
            MaxX = anchor.MinX + 172f * dpi, MaxY = anchor.MinY + 32f * dpi,
        };
        DrawPanelCard(app, hint, 8f * dpi);
        _scratch.Color = theme.Text;
        // 鏂囨锛?026-10-02 绗簲杞級锛氬叆鍙ｄ粠"闀挎寜"鏀规垚"鐐逛竴涓?涔嬪悗锛岃繖鍙ヨ瘽鍙
        // "鐐瑰畠鍑烘潵鐨勬槸椤电爜璺宠浆鑿滃崟"鈥斺€旂敤鎴锋媿鏉跨殑鐭彞锛屾寚鐐规€ц川闈?鐐归〉鐮侊細"涓変釜瀛椼€?        _ctx.DrawText("鐐归〉鐮侊細椤电爜璺宠浆鑿滃崟", ReadoutFormatSmall(dpi),
                      new Rect(hint.MinX, hint.MinY, hint.MaxX - hint.MinX, hint.MaxY - hint.MinY),
                      _scratch);
    }

    // ---- 寮曟搸渚ф偓鍋滄彁绀猴紙Tooltip锛?026-10-02锛?-------------------------------
    //
    // 鐘舵€佸湪 `InkEngine.Tooltip.cs`锛堣皝鎮仠銆?00ms 寤惰繜銆佸紑鍏炽€佹枃妗堣〃锛夛紱杩欓噷鍙礋璐?    // "閲忔枃瀛椼€佹憜浣嶇疆銆佺敾鍗＄墖銆佹姤鑴忓尯"銆傝鐭╁拰鐣岄潰灞傞偅涓€濂椾竴鑷达紝瑙勬牸瑙?    // 銆婅皟鐮?鎮仠鎻愮ず-Tooltip.md銆嬧€斺€斾袱濂楀悇鑷疄鐜帮紙涓や釜宸ョ▼鐢诲湪涓嶅悓琛ㄩ潰涓婏級锛?    // 浣嗗欢杩熴€佸唴瀹规牸寮忋€佸瑙傝瑷€蹇呴』涓€鑷淬€?
    private IDWriteTextFormat TipTitleFormat(float dpi)
    {
        float px = MathF.Max(10f, MathF.Round(12.5f * dpi));
        if (_tipTitleFmt == null || _tipTitleFmtPx != px)
        {
            _tipTitleFmt?.Dispose();
            _tipTitleFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _tipTitleFmt.TextAlignment = TextAlignment.Leading;
            _tipTitleFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _tipTitleFmtPx = px;
        }
        return _tipTitleFmt;
    }

    private IDWriteTextFormat TipNoteFormat(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(11f * dpi));
        if (_tipNoteFmt == null || _tipNoteFmtPx != px)
        {
            _tipNoteFmt?.Dispose();
            _tipNoteFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _tipNoteFmt.TextAlignment = TextAlignment.Leading;
            _tipNoteFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _tipNoteFmtPx = px;
        }
        return _tipNoteFmt;
    }

    /// <summary>
    /// 鎻愮ず鍗＄殑鐭╁舰锛堢墿鐞嗗儚绱狅紝鍜屾诞灞傚悓涓€濂楀睆骞曞潗鏍囷級锛氬搴︽寜鏂囧瓧閲忋€佹í鍚戝す杩涘睆骞曪紝
    /// 绾靛悜涓?*浼樺厛閿氱偣涓婃柟 8 鍍忕礌**锛岄《鍒板睆骞曚笂娌垮氨缈诲埌涓嬫柟銆?    /// 閲忓銆佹憜浣嶃€佺粯鍒躲€佽剰鍖?*閮借杩欎竴浠?*鈥斺€斿悇绠椾竴閬嶈繜鏃╁樊鍑犱釜鍍忕礌锛堟诞灞傜殑鑰佹暀璁級銆?    /// </summary>
    private RectF TooltipCardRect(InkEngine app)
    {
        if (!app.TooltipShown || string.IsNullOrEmpty(app.TooltipTitle)) return RectF.Empty;
        float dpi = Dpi / 96f;
        var titleFmt = TipTitleFormat(dpi);
        var noteFmt = TipNoteFormat(dpi);

        float titleW = MeasureTextWidth(app.TooltipTitle, titleFmt);
        float keyW = string.IsNullOrEmpty(app.TooltipKey)
            ? 0f : 12f * dpi + MeasureTextWidth(app.TooltipKey, titleFmt);
        float noteW = string.IsNullOrEmpty(app.TooltipNote)
            ? 0f : MeasureTextWidth(app.TooltipNote, noteFmt);
        float padX = 12f * dpi, padY = 8f * dpi;
        float w = MathF.Max(titleW + keyW, noteW) + padX * 2f;
        float h = (string.IsNullOrEmpty(app.TooltipNote) ? 20f : 36f) * dpi + padY * 2f;

        var a = app.TooltipAnchorNow;
        var scr = app.TipScreenNow;
        float cx = (a.MinX + a.MaxX) * 0.5f;
        float x0 = cx - w * 0.5f, x1 = cx + w * 0.5f;
        float lo = scr.MinX + 4f * dpi, hi = scr.MaxX - 4f * dpi;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }

        float gap = 8f * dpi;
        float top = a.MinY - gap - h;
        if (top < scr.MinY + 4f * dpi) top = a.MaxY + gap;
        return new RectF { MinX = x0, MinY = top, MaxX = x1, MaxY = top + h };
    }

    private void DrawEngineTooltip(InkEngine app)
    {
        if (!app.TooltipShown) return;
        var box = TooltipCardRect(app);
        if (box.IsEmpty) return;

        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        float radius = MathF.Min(8f * dpi, (box.MaxY - box.MinY) * 0.5f);
        DrawPanelCard(app, box, radius);

        var titleFmt = TipTitleFormat(dpi);
        var noteFmt = TipNoteFormat(dpi);
        float padX = 12f * dpi, padY = 8f * dpi, lineH = 20f * dpi;
        bool hasNote = !string.IsNullOrEmpty(app.TooltipNote);

        float titleW = MeasureTextWidth(app.TooltipTitle, titleFmt);
        float titleY = hasNote ? box.MinY + padY : (box.MinY + box.MaxY) * 0.5f - lineH * 0.5f;
        _scratch.Color = theme.Text;
        _ctx.DrawText(app.TooltipTitle, titleFmt,
                      new Rect(box.MinX + padX, titleY, MathF.Max(1f, titleW + 4f), lineH), _scratch);
        if (!string.IsNullOrEmpty(app.TooltipKey))
        {
            _scratch.Color = theme.TextMuted;
            _ctx.DrawText(app.TooltipKey, titleFmt,
                          new Rect(box.MinX + padX + titleW + 12f * dpi, titleY, Width + 4096f, lineH),
                          _scratch);
        }
        if (hasNote)
        {
            _scratch.Color = theme.TextMuted;
            _ctx.DrawText(app.TooltipNote, noteFmt,
                          new Rect(box.MinX + padX, box.MinY + padY + lineH,
                                   box.MaxX - box.MinX - padX * 2f, lineH),
                          _scratch);
        }
    }

    /// <summary>
    /// 鐢绘縺鍏夌瑪鐨勮建杩广€傜敤鎴?2026-09-27 瑕佺殑鏄?ClassIn 閭ｄ釜銆?*鎷栨嫿婵€鍏夌瑪**銆嶇殑鏍峰瓙锛?    /// **绛夊鐨勫彂鍏夊甫**锛堜笉鏄棫鐗堥偅鏉′粠灏惧埌澶存笎缁嗙殑褰楁槦灏撅級锛屾澗鎵嬪悗鍋?2 绉掑啀鏁存潯娣″嚭銆?    ///
    /// **姣斾緥鐓?ClassIn 閲忓嚭鏉ョ殑鏁版嵁瀹?*锛堢敤鎴凤細"鎴戜滑杩欎釜鍙戝厜濂藉儚鍜?ClassIn 鐨勪笉澶т竴鏍凤紝
    /// 鎴戞劅瑙?ClassIn 鐨勯偅绉嶄技涔庡ソ鐪嬩竴鐐?锛夈€?    ///
    /// 閲忔硶锛歚tmp/measure-laser.ps1` 鍦ㄥ弬鑰冨浘涓婄珫鐫€鍒囦竴鍒€锛屼粠杈圭紭鍒颁腑蹇冩墦鍗?RGB
    ///锛堥偅鏉＄瑪鐢荤暐鏂滐紝绔栧垏姣旂湡妯埅闈㈠绾?1.13 鍊嶏紝姣斾緥涓嶅彈褰卞搷锛夈€備竴鍒€涓嬪幓闀胯繖鏍?    ///锛坮 锛?绂昏建杩逛腑蹇冪殑鍍忕礌锛宎 锛?"閭ｄ竴鐐圭殑绾㈡湁澶氬疄"锛屾寜鐧藉簳鍙嶇畻锛夛細
    ///
    ///   | r锛堝儚绱狅級 | 棰滆壊           | a    | 鏄粈涔?            |
    ///   |-----------|----------------|------|--------------------|
    ///   | 0 锝?6    | 255,255,255    | 鈥?   | **绾櫧鑺?*锛堢害 12 鍍忕礌瀹斤級 |
    ///   | 6 锝?10   | 215,2,21       | 鈮?   | **楗卞拰绾㈣竟**锛堜袱渚у悇绾?4 鍍忕礌锛?|
    ///   | 10 锝?12  | 244,94,106     | 0.64 | 鏌斿厜璧风偣           |
    ///   | 12 锝?16  | 246,137..203   | 0.47 鈫?0.21 | 鏌斿厜涓    |
    ///   | 16 锝?26  | 249,217..235   | 0.15 鈫?0.08 | 鏌斿厜鍚庢    |
    ///   | 26 锝?33  | 244..247       | 0.05 鈫?0.03 | 鏌斿厜灏惧反锛堝揩璐村埌绾哥櫧浜嗭級 |
    ///
    /// 涓夋潯缁撹锛堟垜浠涓€鐗堝叏鍙嶇潃鍋氱殑锛屾墍浠ュ嚭鍥句竴鐪嬫槸"涓ゆ潯缁嗙孩绾垮す涓€鏉＄櫧缂?鈥斺€?    /// 鐧借姱鍜岀孩鐜お绐勩€?*鍏夋檿鍑犱箮娌＄敾**锛屾暣鏉¤璧锋潵鏄釜绌哄績绠★級锛?    ///   鈶?鐧借姱**寰堝**锛堝崐寰?鈮?0.30 脳 绾㈠甫鐩村緞锛屽嵆鏁存潯鐧借姱鍗犵孩甯︾殑鍏垚锛夛紱
    ///   鈶?绾㈣竟**寰堝疄**锛?15,2,21锛屽嚑涔庝笉閫忔槑锛夛紝涓嶆槸娣＄孩锛?    ///   鈶?**鍏夋檿鎷栧緱寰堣繙**锛氫竴鐩存嫋鍒扮害 **1.55 脳 绾㈠甫鐩村緞**锛堟槸绾㈠甫鍗婂緞鐨?3 鍊嶏級锛?    ///      鑰屼笖鏄竴鏉′粠 0.64 骞虫粦鎺夊埌 0.03 鐨勬笎鍙樷€斺€?*杩欐墠鏄?鍙戝厜"鐨勬湰浣?*銆?    ///
    /// 鍥犱负 鈶?鏄笎鍙橈紝涓€鏍圭瓑瀹藉甫瀛愮敾涓嶅嚭鏉ワ紝杩欓噷鐢?*浜斿眰鍚屽績甯?*鍒嗘杩戜技
    ///锛圖irect2D 鐨勫緞鍚戞笎鍙樼瑪鍒锋槸缁曚竴涓渾蹇冪殑锛屽涓嶅埌浠绘剰褰㈢姸鐨勮建杩逛笂锛夈€?    /// 姣忓眰鐨勫崐寰?涓嶉€忔槑搴﹁ `DrawLaser` 閲岄偅浜旇 `Glow(...)`锛屾暟瀛楀搴旂殑灏辨槸涓婅〃銆?    ///
    /// 浜庢槸**绮楃粏婊戞潯缁堜簬鏄湡绠＄敤鐨?*锛堜互鍓嶉偅涓粦鏉″彧绠¤惤鐐归偅涓渾鐐癸紝杞ㄨ抗瀹藉害鏄啓姝荤殑 13 / 6锛夈€?    ///
    /// 鈿?绮楃粏**鎸夋瘡鏉¤嚜宸辫鐨勯偅浠?*璇伙紙`Stroke.WidthLogical`锛夛紝涓嶆槸璇?褰撳墠鍊?锛?    ///   璇诲綋鍓嶅€肩殑璇濓紝鐢诲畬涓€鏉°€佹姮鎵嬭皟绮椼€佸啀鐢讳竴鏉★紝**鍓嶄竴鏉′細璺熺潃涓€璧峰彉鑳?*銆?    /// 鈿?鍔犲浜嗗彂鍏夊氨瑕?*鍚屾鍔犲鑴忓尯**锛坄Overlay` 閲岄偅鏉?`pad`锛岃閭ｉ噷鐨勬敞閲婏級锛?    ///   鍙戝厜鐢诲埌 1.3 鍊嶇洿寰勶紝鑴忓尯杩樻寜 1.0 鍊嶇畻鐨勮瘽锛屾姮鎵?娣″嚭鏃朵細鐣欎笅娌¤閲嶇敾鐨勭孩杈广€?    /// 鈿?**鎹㈡垚榛樿鐨?SourceOver 娣峰悎**锛堝師鏉ユ槸 `PrimitiveBlend.Add`锛夛細
    ///   鍔犳硶鍙姞浜紝"绾?+ 鐧?= 鐧?锛屽湪**鐧芥澘 / 娴呰壊 PPT** 涓婃暣鏉¤建杩逛細鐩存帴鐪嬩笉瑙?    ///   锛堣嚜妫€閲?鐧芥澘涓婄湅寰楄"閭ｆ潯灏辨槸閲忚繖涓細鍔犳硶娣峰悎浼氶噺鍑?0 涓儚绱狅級銆?    ///   娉ㄦ剰鐧借姱鍦ㄧ櫧鏉夸笂鏈潵灏?鐪嬩笉瑙?锛岀湡姝ｈ瀹冨湪鐧藉簳涓婄珛浣忕殑鏄?*閭ｅ湀绾㈣竟鍜屾煍鍏?*鈥斺€?    ///   鎵€浠ョ孩杈圭粰鍒?0.95銆佹煍鍏夋渶閲岄偅涓€灞傜粰鍒?0.29锛屼笉璁稿啀璋冩贰銆?    ///
    /// 涓ょ琛ヤ竴涓渾甯斤紙鍗婂緞 = 鍚勮嚜鐨勫崐瀹斤級锛氬甫瀛愭槸"涓や晶鍚勫亸鍗婁釜瀹藉害"鎻忓嚭鏉ョ殑澶氳竟褰紝
    /// 绔彛鏄钩澶达紱琛ヤ笂鍦嗗附鎵嶅儚**鍐欏嚭鏉ョ殑涓€绗?*锛岃€屼笉鏄鍒囨柇鐨勪竴鏉″甫瀛愩€?    /// </summary>
    private void DrawLaser(InkEngine app)
    {
        var strokes = app.Laser.Strokes;
        if (strokes.Count == 0) return;

        var glow = new Color4(0.98f, 0.06f, 0.10f, 1f);    // 鏌斿厜锛氫寒绾紙閲忓埌鐨?R 涓€鐩粹増248锛岀豢钃濆線涓嬫帀锛?        var edge = new Color4(0.85f, 0.01f, 0.08f, 1f);    // 绾㈣竟锛氶ケ鍜岀孩锛堥噺鍒扮殑灏辨槸 215,2,21锛?        var core = new Color4(1f, 1f, 1f, 1f);             // 鐧借姱锛氶噺鍒扮殑鏄?*绾櫧** 255,255,255

        foreach (var s in strokes)
        {
            int n = s.Points.Count;
            if (n < 2) continue;
            float a = 1f - s.Fade;              // 鍋滅暀鏈?= 1锛堜竴鐐规病娣★級锛涙贰鍑烘椂涓€璺埌 0
            if (a <= 0.001f) continue;

            float d = MathF.Max(6f, s.WidthLogical * app.DpiScale);   // 绾㈣壊涓诲甫鐨勭洿寰?
            // ---- 澶栭潰閭ｅ湀鏌斿厜锛?*鍏眰**鍚屽績銆佽秺澶栬秺娣?----
            // 灞傛暟鍜屼笉閫忔槑搴︿笉鏄媿鑴戣瀹氱殑锛氶噺鍑烘潵閭ｆ潯"绾⑩啋鐧?鏄竴鏉′粠 0.64 骞虫粦鎺夊埌 0.03銆?            // 涓€鐩存嫋鍒扮害 1.55 鍊嶄富甯︾洿寰勭殑娓愬彉锛堣涓婇潰琛ㄦ牸锛夛紝鍚屽績甯︽槸鎶婂畠**鍒嗘杩戜技**
            //锛圖irect2D 鐨勫緞鍚戞笎鍙樼瑪鍒锋槸"缁曚竴涓渾蹇?鐨勶紝濂椾笉鍒颁换鎰忓舰鐘剁殑杞ㄨ抗涓婏級銆?            // 姣忓眰鐨?alpha 鏄?*鍙嶆帹**鐨勶細瑕佽鍙犲畬涔嬪悗鐨勭疮璁′笉閫忔槑搴﹀垰濂界瓑浜庨噺鍒扮殑閭ｄ釜鍊?            //锛?.60 / 0.32 / 0.20 / 0.11 / 0.07 / 0.03锛夛紝鎵€浠ヨ秺閲屽眰鐨?鍗曞眰"鍊艰秺澶с€?            // 鏈€鍚庝竴灞?0.41 鐪嬬潃寰堝ぇ锛屽叾瀹炲畠鍙湶鍦?绾㈣竟澶栨部鍒?0.58 鍊?杩欎竴鍦堜笂鈥斺€?            // 鍙傝€冨浘鍦ㄩ偅閲岀殑钀藉礀鏈€闄★紙0.64 鈫?0.47锛夛紝灏卞緱闈犺繖涓€灞傞《涓婂幓銆?            // 鍙嶆帹瀹屽啀鍥炲ご閲忔垜浠嚜宸辩殑鍑哄浘锛坄tmp/measure-own*.txt`锛夛紝閫愮偣瀵逛笂浜嗗弬鑰冨浘銆?            Glow(1.60f, 0.03f, a);
            Glow(1.25f, 0.04f, a);
            Glow(0.98f, 0.043f, a);
            Glow(0.80f, 0.10f, a);
            Glow(0.68f, 0.15f, a);
            Glow(0.58f, 0.41f, a);

            // ---- 绾㈣竟 ----锛堢櫧鑺殢鍚庣洊鎺変腑闂达紝鍙湪涓や晶鍚勭暀 0.20d 鐨勭孩锛?            Layer(s.Points, d * 0.50f, new Color4(edge.R, edge.G, edge.B, 0.95f * a));

            // ---- 鐧借姱 ----锛堝崰澶уご锛?.60d 瀹界殑鐧姐€傞噺鍒扮殑鐧借姱/绾㈠甫 鈮?6/10銆?            //   **涓嶅垎绮楃粏閮界敾**鈥斺€旀柊姣斾緥閲岀櫧鑺槸涓讳綋锛岀粏绗旀椂瀹冧袱杈硅嚜鐒跺彧鍓╀竴鏉＄粏绾㈢嚎锛?            //   姝ｆ槸 ClassIn 閭ｄ釜鏍峰瓙锛涙棫鐗堥偅绉?澶粏灏变笉鐢荤櫧鑺?鐨勯棬妲涙槸缁?            //   "鐧借姱鍙崰 0.32d"鐨勬棫姣斾緥璁剧殑锛岀幇鍦ㄤ笉闇€瑕佷簡銆傦級
            Layer(s.Points, MathF.Max(0.6f, d * 0.30f), new Color4(core.R, core.G, core.B, 0.98f * a));

            // 鏌斿厜閭ｄ竴灞傦紙鍗婂緞鎸変富甯︾洿寰勭殑鍊嶆暟缁欙紝瑙佷笂闈㈤偅涓叉暟瀛楋級
            void Glow(float k, float alpha, float fade)
                => Layer(s.Points, d * k, new Color4(glow.R, glow.G, glow.B, alpha * fade));
        }

        // 涓€灏忓眰锛氱瓑瀹界殑甯﹀瓙 锛?涓ょ鐨勫渾甯?        void Layer(IReadOnlyList<InkPoint> pts, float half, in Color4 col)
        {
            using (var geo = BuildTaperedRibbon(pts, 0, pts.Count - 1, half, half))
            {
                if (geo == null) return;
                _scratch.Color = col;
                _ctx.FillGeometry(geo, _scratch);
            }
            _scratch.Color = col;
            var head = pts[0]; var tail = pts[pts.Count - 1];
            _ctx.FillEllipse(new Ellipse(new Vector2(head.X, head.Y), half, half), _scratch);
            _ctx.FillEllipse(new Ellipse(new Vector2(tail.X, tail.Y), half, half), _scratch);
        }
    }

    private static ID2D1PathGeometry BuildPolyline(IReadOnlyList<InkPoint> pts, int start, int end)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(new Vector2(pts[start].X, pts[start].Y), FigureBegin.Hollow);
        for (int i = start + 1; i <= end; i++)
            sink.AddLine(new Vector2(pts[i].X, pts[i].Y));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// Filled ribbon along a slice of the trail, tapering from headHalf at the
    /// newest point down to tailHalf at the oldest. Returns null when the slice
    /// is too short to form a polygon.
    /// </summary>
    private static ID2D1PathGeometry BuildTaperedRibbon(
        IReadOnlyList<InkPoint> pts, int start, int end, float headHalf, float tailHalf)
    {
        start = Math.Max(0, start);
        end = Math.Min(pts.Count - 1, end);
        int n = end - start + 1;
        if (n < 2) return null;

        var outline = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            int idx = start + i;
            int a = idx > start ? idx - 1 : idx;
            int b = idx < end ? idx + 1 : idx;
            float dx = pts[b].X - pts[a].X;
            float dy = pts[b].Y - pts[a].Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float nx = -dy, ny = dx;

            float t = i / (float)(n - 1);          // 0 = tail, 1 = head
            float hw = tailHalf + (headHalf - tailHalf) * t;

            outline[i] = new Vector2(pts[idx].X + nx * hw, pts[idx].Y + ny * hw);
            outline[n * 2 - 1 - i] = new Vector2(pts[idx].X - nx * hw, pts[idx].Y - ny * hw);
        }

        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.SetFillMode(Vortice.Direct2D1.FillMode.Winding);
            sink.BeginFigure(outline[0], FigureBegin.Filled);
            for (int i = 1; i < outline.Length; i++) sink.AddLine(outline[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    // =====================================================================
    //  鍛煎嚭鐩橈紙Ctrl+Q锛夛細鎸変綇 鈫?鍒掑悜鎵囧尯 鈫?鏉炬墜
    // =====================================================================
    //
    // 鍜岃惤鐐瑰弽棣堜竴鏍风敾鍦?*娴姩灞?*銆佸潗鏍囧拰鎸囬拡鍚屾簮锛堢敾甯冨潗鏍?+ CanvasToWindow锛夛紝
    // 鎵€浠ョ浉鏈轰竴婊氬畠涓嶄細婕傘€傞厤鑹?*鐢ㄧ晫闈㈡帹涓婃潵鐨?FloatingTheme**锛氭祬鑹?娣辫壊涓婚
    // 鍚勪竴濂楋紝鍜岄潰鏉?鎿嶄綔鏉″悓涓€涓鎰燂紙"鐣岄潰閫昏緫璺熶富绋嬪簭璧?閭ｆ潯瑕佹眰鐨勮惤鐐癸級銆?    //
    // 瑙嗚瑙勬牸锛?026-09-30 v4 瀹氱锛氭柟妗?S銆屽叏鎵囬潰銆? 涓绘潯鍚屾鍥炬爣 + 鎺掑簭 V-a锛?    // 瑙併€婅皟鐮?鍛煎嚭鐩?鎵囬潰涓庢瘮渚?md銆嬩笌 design/鍛煎嚭鐩?璁捐绋縱4-瀹氱鏂瑰悜.html锛夛細
    //   路 鐩樺崐寰?96锛堢洿寰?192锛夈€佺幆甯?鍐?40 鈫?澶?96銆佺紳 2掳銆佹姇褰辩収鏃у眰鍙犺儉娉曪紱
    //   路 鍥炬爣 24 灞呬腑鍦?R68锛堝唴澶栧悇鐣?16锛夆€斺€旇緭鍏ュ垽鎹竴涓病鍔紙姝诲尯 24 / 閿佸畾 36锛夛紱
    //   路 宸ュ叿鏍硷細甯告€?regular銆侀€変腑 filled 鐧藉浘鏍囷紙璺緞 = 涓绘潯鍚屾锛岃 IconPaths锛夛紱
    //   路 棰滆壊鏍硷細`penFilled` 涓婅壊锛堥粦/绾?钃濓級鈥斺€?杩欐敮棰滆壊鐨勭瑪"锛涢€変腑淇濇寔鍘熻壊锛?    //   路 **娌℃湁甯告樉鏍囩**锛氬悕瀛楀彧鍑虹幇鍦ㄤ腑澶鏁帮紱鎮仠鏃剁洏涓?娴竴琛屽瓧"銆?    internal const float RadialPlateRadiusLogical = 96f;
    internal const float RadialInnerRadiusLogical = 40f;
    internal const float RadialIconRingLogical = 68f;
    internal const float RadialCenterLogical = 34f;
    internal const float RadialSeamDeg = 2f;          // 鎵囨牸涔嬮棿鐨勭紳锛堟瘡杈?1掳锛?
    /// <summary>绗?i 涓墖鍖烘槸涓嶆槸"棰滆壊"锛堥粦/绾?钃?= 1/2/3锛夆€斺€旂敾娉?閫変腑鎬佷笉鍚屻€?/summary>
    private static readonly bool[] RadialSectorIsColor =
        { false, true, true, true, false, false, false, false };

    // "0 鍙锋墖鏍?鐨勫嚑浣曠紦瀛橈紙浠ョ洏蹇冧负鍘熺偣銆佸寳鏈濅笂锛夛紱dpi 鍙樹簡鎵嶉噸寤恒€?    private ID2D1PathGeometry _radialWedgeGeo;
    private float _radialWedgeDpi = -1f;

    private void DrawRadialPalette(InkEngine app)
    {
        if (!app.RadialPaletteActive || !app.RadialPaletteVisible) return;

        float dpi = Dpi / 96f;
        float R = RadialPlateRadiusLogical * dpi;
        float r0 = RadialInnerRadiusLogical * dpi;
        float ring = RadialIconRingLogical * dpi;
        float rc = RadialCenterLogical * dpi;
        var c = new Vector2(app.RadialCenterX, app.RadialCenterY);
        var theme = app.FloatingTheme;
        int sel = app.RadialPaletteSector;

        // 鎶曞奖锛氱収闈㈡澘閭ｅ"灞傚彔鑳€娉?锛堢敱澶у埌灏忓彔銆佽创鐫€褰㈢姸鏈€鏆楋級
        DrawDiscShadow(app, c, R);

        // 鎸囬拡 鈫?鐩樺績锛氱偣绾匡紙璁?鎴戝湪鍒?杩欎欢浜嬫湁涓洜鏋滐級
        var ptr = new Vector2(app.PointerX, app.PointerY);
        if (Vector2.Distance(c, ptr) > 8f * dpi)
        {
            _scratch.Color = new Color4(theme.TextMuted.R, theme.TextMuted.G,
                                        theme.TextMuted.B, 0.55f);
            _ctx.DrawLine(c, ptr, _scratch, 2.4f * dpi, Gfx.Dotted);
        }

        // 搴曠洏 鈫?鍏墖鏍?鈫?鐩樿竟锛堢洏杈规渶鍚庣敾锛屽帇鍦ㄦ墖鏍煎娌夸笂锛岀嚎鎵嶅埄钀斤級
        _ctx.FillEllipse(new Ellipse(c, R, R), Brush(theme.Panel));
        DrawRadialWedges(c, dpi, r0, R, theme, sel);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, R, R), _scratch, 1f * dpi);

        // 鍏牸鍥炬爣锛堜富鏉″悓娆?Fluent 璺緞锛涢鑹叉牸 = penFilled 涓婅壊锛?        float sz = 24f * dpi;
        for (int i = 0; i < 8; i++)
        {
            var bp = OnCircle(c, ring, -90f + 45f * i);
            bool on = i == sel;
            if (RadialSectorIsColor[i])
            {
                // **棰滆壊鏍间笉杞櫧**锛氶€変腑涔熻鐪嬪緱瑙?杩欐槸绾?钃?锛堢孩钃濆帇瀹炲績钃濆簳浼氬彂闂凤級
                _scratch.Color = InkPalette.PenBand[i - 1].Color;
                DrawIcon(IconPaths.penFilled, bp.X - sz * 0.5f, bp.Y - sz * 0.5f, sz, _scratch);
            }
            else
            {
                _scratch.Color = on ? theme.ActiveText : theme.Text;
                DrawIcon(RadialIconFor(i, on), bp.X - sz * 0.5f, bp.Y - sz * 0.5f, sz, _scratch);
            }
        }

        // 涓ぎ璇绘暟锛堣繖鏄敮涓€甯告樉鐨勬枃瀛楋級
        _ctx.FillEllipse(new Ellipse(c, rc, rc), Brush(theme.Panel));
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, rc, rc), _scratch, 1f * dpi);
        string centerText = sel >= 0 ? InkEngine.RadialSectorNames[sel]
                                     : (app.RadialMovedForDraw ? "鍙栨秷" : "宸ュ叿");
        _scratch.Color = sel >= 0 ? theme.Text : theme.TextMuted;
        _ctx.DrawText(centerText, RadialCenterFormat(dpi),
                      new Rect(c.X - rc, c.Y - rc * 0.8f, rc * 2f, rc * 1.6f), _scratch);

        // 鎮仠鏃?娴竴琛屽瓧"锛氱洏澶栦笅鏂圭殑灏忕櫧鏉★紝缁欎竴鍙ヤ汉璇?        if (sel >= 0)
        {
            string hint = RadialHintFor(app, sel);
            var fmt = RadialHintFormat(dpi);
            float tw = MeasureTextWidth(hint, fmt) + 30f * dpi;
            float hh = 26f * dpi;
            float top = c.Y + R + 12f * dpi;
            DrawPanelCard(app, new RectF
            {
                MinX = c.X - tw * 0.5f, MinY = top,
                MaxX = c.X + tw * 0.5f, MaxY = top + hh,
            }, hh * 0.5f);
            _scratch.Color = theme.Text;
            _ctx.DrawText(hint, fmt, new Rect(c.X - tw * 0.5f, top, tw, hh), _scratch);
        }
    }

    /// <summary>鍦嗙洏鎶曞奖锛氱収闈㈡澘鐨?灞傚彔鑳€娉?锛堜富棰樻帹涓婃潵鍑犲眰灏辩敾鍑犲眰锛岀敱澶у埌灏忥級銆?/summary>
    private void DrawDiscShadow(InkEngine app, Vector2 c, float R)
    {
        var th = app.FloatingTheme;
        float dpi = Dpi / 96f;
        for (int i = th.Shadow.Length - 1; i >= 0; i--)
        {
            var layer = th.Shadow[i];
            _scratch.Color = layer.Color;
            float r2 = R + layer.Inflate * dpi;
            _ctx.FillEllipse(new Ellipse(new Vector2(c.X, c.Y + layer.Dy * dpi), r2, r2), _scratch);
        }
    }

    /// <summary>鎮仠閭ｄ竴琛屽瓧锛氫竴鍙ヤ汉璇濓紙"宸茬粡鏄瑪锛氭崲涓嬩竴鑹?杩欑锛夈€?/summary>
    private static string RadialHintFor(InkEngine app, int i)
    {
        bool already = i switch
        {
            0 => app.Tool == Tool.Pen,
            4 => app.Tool == Tool.Eraser || app.Tool == Tool.PixelEraser,
            5 => app.Tool == Tool.Marquee,
            6 => app.Tool == Tool.Highlighter,
            7 => app.Tool == Tool.Laser,
            _ => false,
        };
        return i switch
        {
            0 => already ? "宸茬粡鏄瑪锛氭崲涓嬩竴鑹? : "鍒囧埌绗?,
            1 => "鐢ㄩ粦绗?,
            2 => "鐢ㄧ孩绗?,
            3 => "鐢ㄨ摑绗?,
            4 => already ? "鏁寸瑪鎿?鈬?闈㈢Н鎿? : "鍒囧埌姗＄毊",
            5 => already ? "鐭╁舰 鈬?濂楃储" : "鍒囧埌妗嗛€?,
            6 => already ? "宸茬粡鏄崸鍏夌瑪锛氭崲涓嬩竴鑹? : "鍒囧埌鑽у厜绗?,
            _ => already ? "宸茬粡鏄縺鍏夌瑪" : "鍒囧埌婵€鍏夌瑪",
        };
    }

    /// <summary>
    /// 鍏釜鎵囨牸锛? 鍙峰湪鍖楋紙-90掳锛夛紝涓€鏍?45掳锛屾牸涓庢牸涔嬮棿鐣?<see cref="RadialSeamDeg"/> 鐨勭紳銆?    /// 鍏堥摵鏈€変腑鐨勬牸銆佸啀鎻忔牸缂濄€佹渶鍚庢妸閫変腑鏍肩洊涓婂幓銆?    /// </summary>
    private void DrawRadialWedges(Vector2 c, float dpi, float r0, float r1, UiTheme theme, int sel)
    {
        EnsureRadialWedgeGeo(dpi, r0, r1);

        // 鏈€変腑锛氭祬搴曪紙娣辫壊涓婚閲屽畠灏辨槸"姣旂洏搴曚寒涓€妗?鐨勬牸锛?        for (int i = 0; i < 8; i++)
        {
            if (i == sel) continue;
            FillRotatedWedge(c, i, Brush(theme.Hover));
        }

        // 鏍肩紳锛? 鏉¤竟鐣岀嚎锛堢粏銆佹贰鈥斺€旀妸"涓€鏍间竴鏍?璇存竻妤氾紝鍒敾鎴愬垏铔嬬硶锛?        _scratch.Color = new Color4(theme.PanelBorder.R, theme.PanelBorder.G,
                                    theme.PanelBorder.B, 0.7f);
        for (int i = 0; i < 8; i++)
        {
            float a = -90f + 45f * i - 22.5f;
            _ctx.DrawLine(OnCircle(c, r0, a), OnCircle(c, r1, a), _scratch, 1f * dpi);
        }

        if (sel < 0) return;

        // 閫変腑鏍硷細宸ュ叿 = 瀹炲績寮鸿皟鑹诧紱棰滆壊 = 娣″己璋冭壊锛堝僵绗斾繚鎸佸師鑹诧紝绾㈣摑涓嶅帇钃濆簳锛?        var fill = RadialSectorIsColor[sel]
            ? new Color4(theme.ActiveBg.R, theme.ActiveBg.G, theme.ActiveBg.B, 0.14f)
            : theme.ActiveBg;
        FillRotatedWedge(c, sel, Brush(fill));
        if (RadialSectorIsColor[sel]) StrokeSelectedWedge(c, sel, dpi, r0, r1, theme.ActiveBg);
    }

    /// <summary>
    /// 缂撳瓨"0 鍙锋墖鏍?鐨勫嚑浣曪紙浠ョ洏蹇冧负鍘熺偣銆佸寳鏈濅笂锛夛細寮х敤鎶樼嚎閫艰繎
    /// 锛堟瘡 45掳 鍒?12 娈碉紝R96 涓嬫渶澶х煝楂?鈮?.1px锛岃倝鐪肩湅涓嶅嚭鏄姌绾匡級銆?    /// 缁樺埗鏃舵寜鏍煎彿鏃嬭浆 / 骞崇Щ锛堣 <see cref="FillRotatedWedge"/>锛夛紝涓嶅繀姣忓抚閲嶅缓銆?    /// </summary>
    private void EnsureRadialWedgeGeo(float dpi, float r0, float r1)
    {
        if (_radialWedgeGeo != null && Math.Abs(_radialWedgeDpi - dpi) < 0.001f) return;
        _radialWedgeGeo?.Dispose();
        _radialWedgeDpi = dpi;

        float half = 22.5f - RadialSeamDeg * 0.5f;
        const int seg = 12;
        var pts = new List<Vector2>(seg * 2 + 2);
        for (int t = 0; t <= seg; t++) pts.Add(WedgePt(r0, -half + 2f * half * t / seg));
        for (int t = seg; t >= 0; t--) pts.Add(WedgePt(r1, -half + 2f * half * t / seg));

        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int k = 1; k < pts.Count; k++) sink.AddLine(pts[k]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        _radialWedgeGeo = geo;
    }

    /// <summary>鎵囨牸鍧愭爣锛氳搴︿粠鍖楄捣椤烘椂閽堬紙鍜屽紩鎿庣殑鎵囧尯瀹氫箟鍚屼竴濂楋級銆?/summary>
    private static Vector2 WedgePt(float r, float deg)
    {
        float a = (deg - 90f) * MathF.PI / 180f;
        return new Vector2(r * MathF.Cos(a), r * MathF.Sin(a));
    }

    /// <summary>鎶婄紦瀛樼殑閭ｄ竴鏍艰浆鍒扮 i 涓綅缃啀濉紙姝ｆ暟瑙掑害 = 灞忓箷涓婇『鏃堕拡锛夈€?/summary>
    private void FillRotatedWedge(Vector2 c, int i, ID2D1SolidColorBrush brush)
    {
        var saved = _ctx.Transform;
        _ctx.Transform = Matrix3x2.CreateRotation(i * MathF.PI / 4f)
                       * Matrix3x2.CreateTranslation(c) * saved;
        _ctx.FillGeometry(_radialWedgeGeo, brush);
        _ctx.Transform = saved;
    }

    /// <summary>棰滆壊鏍奸€変腑鐨勫己璋冩弿杈癸細澶栧姬 + 涓ゆ潯鍗婂緞锛堝唴寮т笉鎻忥紝涓ぎ璇绘暟閭ｈ竟骞插噣锛夈€?/summary>
    private void StrokeSelectedWedge(Vector2 c, int i, float dpi, float r0, float r1, Color4 color)
    {
        float half = 22.5f - RadialSeamDeg * 0.5f;
        float mid = -90f + 45f * i;
        _scratch.Color = color;
        float w = 1.5f * dpi;
        for (int t = 0; t < 12; t++)
        {
            float a0 = mid - half + 2f * half * t / 12f;
            float a1 = mid - half + 2f * half * (t + 1) / 12f;
            _ctx.DrawLine(OnCircle(c, r1 - 1f * dpi, a0), OnCircle(c, r1 - 1f * dpi, a1), _scratch, w);
        }
        _ctx.DrawLine(OnCircle(c, r0, mid - half), OnCircle(c, r1, mid - half), _scratch, w);
        _ctx.DrawLine(OnCircle(c, r0, mid + half), OnCircle(c, r1, mid + half), _scratch, w);
    }

    /// <summary>
    /// 鍛煎嚭鐩樼 i 鏍肩殑鍥炬爣锛堜富鏉″悓娆?Fluent 璺緞锛夛細甯告€?regular銆侀€変腑 filled銆?    /// 棰滆壊鏍间笉璧拌繖閲屸€斺€斿畠浠槸 `penFilled` 涓婅壊鐨?褰╃瑪"锛岃 <see cref="DrawRadialPalette"/>銆?    /// </summary>
    private static string RadialIconFor(int i, bool selected) => i switch
    {
        0 => selected ? IconPaths.penFilled : IconPaths.pen,
        4 => selected ? IconPaths.eraserFilled : IconPaths.eraser,
        5 => selected ? IconPaths.selectFilled : IconPaths.select,
        6 => selected ? IconPaths.highlighterFilled : IconPaths.highlighter,
        _ => selected ? IconPaths.laserFilled : IconPaths.laser,   // 7 婵€鍏?    };

    private static Vector2 OnCircle(Vector2 c, float r, float deg)
    {
        float a = deg * MathF.PI / 180f;
        return new Vector2(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a));
    }

    private IDWriteTextFormat _radialHintFmt;
    private float _radialHintPx;
    private IDWriteTextFormat _radialCenterFmt;
    private float _radialCenterPx;

    /// <summary>鎮仠閭ｄ竴琛屽瓧鐨勬牸寮忥紙12px锛屽鍦ㄧ洏涓嬬殑灏忕櫧鏉￠噷锛夈€?/summary>
    private IDWriteTextFormat RadialHintFormat(float dpi)
    {
        float px = MathF.Max(10f, MathF.Round(12f * dpi));
        if (_radialHintFmt == null || _radialHintPx != px)
        {
            _radialHintFmt?.Dispose();
            _radialHintFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _radialHintFmt.TextAlignment = TextAlignment.Center;
            _radialHintFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _radialHintPx = px;
        }
        return _radialHintFmt;
    }

    private IDWriteTextFormat RadialCenterFormat(float dpi)
    {
        float px = MathF.Max(12f, MathF.Round(17f * dpi));
        if (_radialCenterFmt == null || _radialCenterPx != px)
        {
            _radialCenterFmt?.Dispose();
            _radialCenterFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _radialCenterFmt.TextAlignment = TextAlignment.Center;
            _radialCenterFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _radialCenterPx = px;
        }
        return _radialCenterFmt;
    }

    /// <summary>
    /// 钀界偣鍙嶉锛氳嚜宸辩敾鍦ㄦ诞鍔ㄥ眰涓婄殑"鎸囬拡"銆?    ///
    /// 涓轰粈涔堜笉鐩存帴鐢ㄧ郴缁熷厜鏍囷細鍦嗙幆/鍦嗙洏瑕佽窡鐫€**绗斿**鍙橈紝杩樿鍦ㄦ繁鑹?PPT 鍜?    /// 鐧借壊鐧芥澘涓婇兘鐪嬪緱瑙佲€斺€旂郴缁熷厜鏍囧仛涓嶅埌杩欎袱鐐广€備唬浠锋槸鐪熷厜鏍囧繀椤诲悓鏃惰棌璧锋潵锛?    /// 閭ｄ唤閫昏緫鍦ㄥ紩鎿庨噷锛圛nkEngine.DrawnCursor / CursorKind.Hidden锛夈€?    ///
    /// 鐢诲湪娴姩灞傝繕鏈変釜濂藉锛氬畠鐨勫潗鏍囧拰绗旇抗鍚屾簮锛屼笉瀛樺湪"鍏夋爣鐑偣鍋忎簡涓€涓や釜
    /// 鍍忕礌"鐨勯棶棰樷€斺€斿啓瀛楁椂閭ｄ竴涓や釜鍍忕礌鏄兘鎰熻鍑烘潵鐨勩€?    /// </summary>
    private void DrawToolCursor(InkEngine app)
    {
        var shape = app.DrawnCursor;
        if (shape == InkEngine.ToolCursorShape.None) return;

        var c = new Vector2(app.PointerX, app.PointerY);
        if (shape == InkEngine.ToolCursorShape.Disc)
        {
            DrawHighlighterDisc(app, c);
            return;
        }
        if (shape == InkEngine.ToolCursorShape.Dot)
        {
            DrawLaserDot(app, c);
            return;
        }
        if (shape == InkEngine.ToolCursorShape.Rect)
        {
            DrawEraserRectCursor(app, c);
            return;
        }

        // 闂?EffectiveTool锛堜笉鏄?Tool锛夛細**绗斿€掕繃鏉ユ嬁锛堢瑪灏炬鐨級鏃讹紝杩欎竴鍦堝氨鏄鐨殑鍦?*
        // 鈥斺€斿崐寰勬寜姗＄毊銆侀鑹叉寜姗＄毊銆傚紩鎿庨偅杈癸紙DrawnCursor / CursorOuterRadius锛変篃鏄棶瀹冿紝
        // 涓ゅ鍚屼竴涓垽鎹紝涓嶇劧浼氬嚭鐜?鍗婂緞鏄鐨殑銆侀鑹叉槸绗旂殑"杩欑鍗婂悐瀛愩€?        bool erasing = app.EffectiveTool == Tool.Eraser;

        float outer = app.CursorOuterRadius;
        float truth = erasing
            ? app.EraserRadius
            : app.CursorRingTrueRadius;
        var fill = erasing
            ? new Color4(0.35f, 0.55f, 0.95f, 0.10f)      // 姗＄毊锛氭贰钃濓紝"瑕佹摝鎺夎繖涓€鍧?
            : new Color4(0.35f, 0.55f, 0.95f, 0.06f);     // 绗斿皷锛氭洿娣★紝涓嶆尅瑙嗙嚎
        DrawRingCursor(c, truth, outer, fill);
    }

    /// <summary>
    /// 鍦嗙幆锛氭鐨殑"瑕佹摝鎺夊澶т竴鍧?銆佺瑪鐨?杩欎竴绗斿绮?銆?    ///
    /// 鍙岃壊鎻忚竟鍜屽洓鍚戝埢搴﹂兘鏄鍦烘櫙閫煎嚭鏉ョ殑锛氬崟鑹插渾鍦ㄧ櫧鏉匡紙娴咃級鍜屾繁鑹?PPT锛堟繁锛?    /// 涓婃€绘湁涓€绉嶇湅涓嶆竻锛涘埢搴︽爣鍑哄渾蹇冿紝鎶曞奖涓婂啓瀛楁墜鏄姈鐨勶紝寰楃煡閬撹惤鐐瑰湪鍝€?    ///
    /// **涓や釜鍦?*锛氬唴鍦堟槸**鐪熷疄绗斿**锛?鎴戝啓鍑烘潵灏辫繖涔堢矖"锛夛紝澶栧湀鏄浐瀹氬昂瀵哥殑
    /// 鏈€灏忓彲瑙佽寖鍥淬€傚彧鏈変竴涓湀鏃舵棤瑙ｏ細鎸夌湡瀹炲搴︾敾锛岀粏绗旂湅涓嶈锛涙寜涓嬮檺鐢伙紝
    /// 缁嗙瑪鐨勫湀姣旂瑪杩圭矖涓€鍊嶏紝鍙嶈€岃鍒ゃ€侾hotoshop 鐨勭瑪鍒峰厜鏍囧氨鏄繖涓仛娉曘€?    /// </summary>
    /// <summary>
    /// 鍍忕礌姗＄毊鐨勮惤鐐癸細**绔栫潃鐨勯粍閲戞瘮渚嬬煩褰?*锛堥珮 : 瀹?= 1.618锛夈€?    ///
    /// 涓轰粈涔堟槸鐭╁舰鑰屼笉鏄渾鐜細瀹冨氨鏄?涓€鍧楁鐨?銆備粠涓婂線涓嬫姽涓€鍒楁澘涔︽椂锛岀敤鎴疯鐭ラ亾
    /// 杩欎竴鎶规湁澶氬銆佸楂橈紱鍦嗙幆琛ㄨ揪涓嶄簡杩欎釜銆?    ///
    /// 涓夊眰锛堝拰鍦嗙幆鍚屼竴濂楄壊褰╅€昏緫锛夛細
    ///   路 **鍗婇€忔槑濉厖**鈥斺€旂湅寰楄"杩欎竴鍧椾細琚摝鎺?锛屽悓鏃跺簳涓嬬殑瀛楄繕鐪嬪緱瑙侊紙鐢ㄦ埛瑕佺殑
    ///     "鏈夌偣閫忔槑搴?锛夈€傚お瀹炰細鎸′綇瑕佹摝鐨勪笢瑗匡紝澶贰鍙堢湅涓嶅嚭杈圭晫锛?    ///   路 鐧借壊澶栧湀 + 娣辫壊鍐呭湀鐨勫弻鑹叉弿杈癸細娣辫壊 PPT 鍜岀櫧鑹茬櫧鏉夸笂閮藉緱鐪嬪緱瑙侊紱
    ///   路 涓績涓€涓皬鍗佸瓧锛氭姇褰变笂鍐欏瓧鎵嬩細鎶栵紝寰楃煡閬撶簿纭惤鐐瑰湪鍝€?    /// </summary>
    private void DrawEraserRectCursor(InkEngine app, Vector2 c)
    {
        // 妗?= **鐪熸浼氳鎿︽帀鐨勯偅涓€鍧?*锛氬拰 EraseRectAlongPath 璇诲悓涓€浠藉昂瀵搞€?        // 鎷栧姩涓窡鐫€绉诲姩閫熷害鍙樺ぇ锛?.3.4 鍔ㄦ€佹鐨級锛屾偓鍋滄椂鏄熀鍑嗭紙= 鎸変笅鍘荤涓€涓嬬殑澶у皬锛夈€?        DrawEraserBoxAt(c, MathF.Max(1f, app.PixelEraserCursorHalfWidthPx),
                           MathF.Max(1f, app.PixelEraserCursorHalfHeightPx));
    }

    /// <summary>鐢讳竴鍧?闈㈢Н姗＄毊"鐨勮惤鐐规锛堥紶鏍?绗旂殑鍏夋爣涓庤Е鎽稿弽棣堝叡鐢ㄥ悓涓€浠界敾娉曪級銆?/summary>
    private void DrawEraserBoxAt(Vector2 c, float hw, float hh)
    {
        _ctx.FillRectangle(
            new Vortice.RawRectF(c.X - hw, c.Y - hh, c.X + hw, c.Y + hh),
            // 濉厖鑹诧細**0.12 鈫?0.22**锛堢敤鎴?2026-09-17锛?闈㈢Н姗＄毊鎿﹀お閫忔槑浜?锛夈€?            // 鍙傝€冪偣锛氱瑪灏栭偅涓渾鐜～鍏呮槸 0.06锛屾暣绗旀鐨殑鍦嗘槸 0.10鈥斺€旈潰绉鐨槸
            // 鍞竴"瑕佺湅娓呰竟鐣岀殑涓€鍧楅潰"锛屽畠鏈€瀹炴墠瀵癸紱0.22 涔嬪悗搴曚笅鐨勫瓧浠嶇劧閫忓緱鍑烘潵銆?            Brush(new Color4(0.35f, 0.55f, 0.95f, 0.22f)));

        // 涓ゅ眰鎻忚竟閮芥瘮鍘熸潵鐣ュ疄涓€妗ｏ紝涓旂嚎瀹?1.5 鈫?1.8锛氶潰绉鐨殑杈圭晫鏄?浼氫笉浼氭摝鎺?
        // 鐨勫垽鎹紝鎶曞奖涓婅涓€鐪肩湅娓呫€?        _scratch.Color = new Color4(1f, 1f, 1f, 0.85f);
        _ctx.DrawRectangle(new Vortice.RawRectF(
            c.X - hw - 0.9f, c.Y - hh - 0.9f, c.X + hw + 0.9f, c.Y + hh + 0.9f), _scratch, 1.8f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.9f);
        _ctx.DrawRectangle(new Vortice.RawRectF(
            c.X - hw + 0.9f, c.Y - hh + 0.9f, c.X + hw - 0.9f, c.Y + hh - 0.9f), _scratch, 1.8f);

        float tick = MathF.Max(5f, MathF.Min(hw, hh) * 0.25f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.85f);
        _ctx.DrawLine(new Vector2(c.X - tick, c.Y), new Vector2(c.X + tick, c.Y), _scratch, 1.8f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - tick), new Vector2(c.X, c.Y + tick), _scratch, 1.8f);
    }

    /// <summary>
    /// **澶氭寚鎿︿細璇濈殑钀界偣鍙嶉**锛堜笁鎸?/ 鎵嬫帉锛?026-10-05 瀹炴祴锛?鑳芥摝锛屼絾鐪嬩笉瑙佹鐨?锛夛細
    /// 钀界偣 = **鎵€鏈夎Е鐐圭殑涓績**锛堜竴鍙墜 = 涓€涓鐨紝鍜岄紶鏍囧悓婧愶紱涓嶅啀姣忎釜瑙︾偣鐢讳竴涓紝
    /// 鐪熸満鍙嶉"涓変釜鎸囧ご鍑烘潵涓変釜姗＄毊鎿?锛夛紱褰㈢姸銆佸昂瀵搞€侀鑹插叏璧伴紶鏍囬偅浠?    /// 锛堟暣绗旀摝 = 鍦嗙幆銆侀潰绉摝 = 鐭╁舰锛夛紝鐪熸鎿︾殑灏哄鍜屽畠鍚屼竴浠斤紝鎵€浠?鐪嬭鐨?= 鎿︽帀鐨?銆?    /// 鍗曟寚鎸夌潃姗＄毊宸ュ叿璧?`DrawToolCursor`锛堝紩鎿庢妸 DrawnCursor 鎵撳紑浜嗭級锛屼笉鍦ㄨ繖涓€鏉￠噷銆?    /// 鏁版嵁鐜板彇鐜扮敾銆佹ā寮忎竴钀藉氨涓嶇敾鈥斺€旀病鏈夌紦瀛橈紝涓嶄細"鍑虹幇涓嶆秷澶?銆?    /// </summary>
    private void DrawTouchEraseFeedback(InkEngine app)
    {
        if (!app.TouchEraseSessionActive || !app.TouchEraseCenter(out var c)) return;
        if (app.TouchEraseSessionPixel)
            DrawEraserBoxAt(c, MathF.Max(1f, app.PixelEraserCursorHalfWidthPx),
                               MathF.Max(1f, app.PixelEraserCursorHalfHeightPx));
        else
        {
            float r = MathF.Max(1f, app.EraserRadius);
            DrawRingCursor(c, r, r, new Color4(0.35f, 0.55f, 0.95f, 0.10f));
        }
    }

    private void DrawRingCursor(Vector2 c, float truthR, float outerR, Color4 fill)
    {
        float r = MathF.Max(outerR, 1f);
        bool doubleRing = r - truthR > 1.5f;         // 涓や釜鍦堝樊寰楀お杩戝氨鍙敾澶栧湀

        // 涓棿涓€灞傚緢娣＄殑濉厖锛岃"鑼冨洿"涓€鐪煎彲瑙併€?        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(fill));

        // 鍐呭湀锛氱湡瀹炵瑪瀹姐€傚畠鎵嶆槸"杩欎竴绗旀湁澶氱矖"鐨勭瓟妗堛€?        if (doubleRing)
        {
            _ctx.FillEllipse(new Ellipse(c, truthR, truthR),
                             Brush(new Color4(0.35f, 0.55f, 0.95f, 0.16f)));
            _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.55f);
            _ctx.DrawEllipse(new Ellipse(c, truthR, truthR), _scratch, 1f);
        }

        // 鍙岃壊鎻忚竟锛氬鍦堟祬銆佸唴鍦堟繁銆傚崟鑹插渾鍦ㄦ繁鑹叉闈㈠拰鐧借壊鐧芥澘涓婃€绘湁涓€绉嶇湅涓嶆竻锛?        // 鍙屽眰鎻忚竟涓ょ鑳屾櫙涓婇兘鎴愮珛銆?        _ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f),
            Brush(new Color4(1f, 1f, 1f, 0.75f)), 1.5f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.85f);
        _ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f), _scratch, 1.5f);

        // 鍥涗釜鏂瑰悜鐨勫皬鍒诲害鏍囧嚭鍦嗗績锛岃惤鐐规洿鍑嗐€?        float tick = MathF.Max(4f, r * 0.22f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.7f);
        _ctx.DrawLine(new Vector2(c.X - r - tick, c.Y), new Vector2(c.X - r + tick * 0.2f, c.Y), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X + r - tick * 0.2f, c.Y), new Vector2(c.X + r + tick, c.Y), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - r - tick), new Vector2(c.X, c.Y - r + tick * 0.2f), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X, c.Y + r - tick * 0.2f), new Vector2(c.X, c.Y + r + tick), _scratch, 1.5f);
    }

    /// <summary>
    /// 婵€鍏夌瑪鐨勮惤鐐癸細涓€涓疄蹇冨皬鍦嗙偣 + 鐧借壊澶栧湀銆?    ///
    /// 涓轰粈涔堜笉鏄渾鐜細婵€鍏夋槸"鎴戣鐨勬槸杩欓噷"锛屼笉闇€瑕佽〃杈捐寖鍥达紙瀹冭繛澧ㄩ兘涓嶇暀锛夈€?    /// 鐧藉湀鏄负浜嗗湪娣辫壊鑳屾櫙涓婁篃鑳界湅瑙侊紝骞朵笖鍜屾縺鍏夎建杩圭殑绾㈡槸鍚屼竴涓壊绯汇€?    /// </summary>
    private void DrawLaserDot(InkEngine app, Vector2 c)
    {
        float r = MathF.Max(app.CursorDotRadius, 2f);
        _ctx.FillEllipse(new Ellipse(c, r + 1f, r + 1f), Brush(new Color4(1f, 1f, 1f, 0.85f)));
        // 鍜屾縺鍏夎建杩瑰悓鑹诧紙DrawLaser 閲岀敤鐨勫氨鏄繖缁勭孩锛?        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(1f, 0.16f, 0.16f, 0.95f)));
    }

    /// <summary>
    /// 鑽у厜绗旂殑钀界偣锛?*涓€涓洿寰?= 绗斿鐨勫疄蹇冨渾鐩?*銆?    ///
    /// 涓轰粈涔堟槸鍦嗭紝鑰屼笉鏄棫鐗堥偅鏍规í鏉★紙鑳跺泭锛夛細鑽у厜绗旂敾鍑烘潵鐨勫ⅷ杩瑰氨鏄?涓績绾?    /// 涓や晶鍚勫崐涓瑪瀹?+ 涓ょ鍦嗗附"锛岃惤绗斿鏈韩鏄竴涓洿寰?= 绗斿鐨勫渾锛涘崟鍑讳竴涓?    /// 鐣欎笅鐨勪篃姝ｆ槸杩欎釜鍦嗐€傛í鏉″彧鍦ㄦ按骞虫柟鍚戝彉瀹斤紝绔栫潃鐢荤殑閭ｄ竴鍒诲拰瀹為檯澧ㄨ抗
    /// 瀵逛笉涓婏紝鎷栧姩鏃剁湅涓婂幓鍍忔嫋鐫€涓€鏍瑰昂瀛愩€?    ///
    /// 濉厖鐢ㄨ崸鍏夌瑪褰撳墠鐨勯鑹诧紝钀界瑪鍓嶅氨鑳界湅鍒颁細鐢诲嚭浠€涔堣壊锛涙弿杈圭敤鍜岀瑪/姗＄毊
    /// 鍦嗙幆鍚屼竴濂楃殑鍙岃壊锛堟祬搴曘€佹繁搴曚笂閮界湅寰楄锛夈€?    /// </summary>
    private void DrawHighlighterDisc(InkEngine app, Vector2 c)
    {
        // 鍗婂緞灏辨槸鍗婁釜绗斿鈥斺€斿拰鐪熸钀藉埌灞忓箷涓婄殑澧ㄨ抗鍚屼竴涓昂瀵革紝涓嶆斁澶т篃涓嶇缉灏忋€?        float r = MathF.Max(app.HighlighterWidthLogical * app.DpiScale * 0.5f, 2f);
        var hl = app.HighlighterCurrent;

        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(hl.R, hl.G, hl.B, 0.22f)));

        _scratch.Color = new Color4(1f, 1f, 1f, 0.75f);
        _ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f), _scratch, 1.5f);

        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.55f);
        _ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f), _scratch, 1.5f);
    }

    private void DrawMarquee(InkEngine app)
    {
        if (!app.MarqueeActive) return;

        // 濂楃储锛氱敾**鑷敱鏇茬嚎**锛屼笉鏄煩褰€傛墜鍔挎槸浠€涔堟牱锛屽睆骞曚笂灏卞緱鏄粈涔堟牱鈥斺€?        // 鐢ㄦ埛瑕佽兘鐪嬭鑷繁"鍦?鍒板摢鍎夸簡锛屽惁鍒?80% 杩欐潯鍒ゆ嵁瀹屽叏涓嶅彲棰勬湡銆?        // 鏀跺彛閭ｆ潯杈圭敾娣′竴鐐癸細瀹冩彁绀?鏉炬墜浼氳嚜鍔ㄩ棴鍚?锛屼絾杩樹笉鏄幇鍦ㄧ殑杈圭晫銆?        //
        // 绾垮ご蹇呴』鐢诲埌**鎸囬拡姝ゅ埢鐨勪綅缃?*锛圠assoLive锛夛紝涓嶈兘鍙敾鎶界█杩囩殑璺緞鐐癸細
        // 璺緞鐐逛箣闂撮殧鐫€ 3 閫昏緫鍍忕礌锛屽彧鐢昏矾寰勭殑璇濈嚎澶翠細鎱㈠崐鎷嶏紙200% 缂╂斁 = 6 涓墿鐞嗗儚绱狅紝
        // 鐪嬪緱鍑烘潵锛夈€傛娊绋€鏄负浜嗗垽鎹究瀹滐紝涓嶆槸涓轰簡灏戠敾杩欎竴鎴€?        if (app.SelMode == SelectMode.Lasso && app.LassoPath.Count >= 1
            && (app.LassoPath.Count >= 2
                || Vector2.Distance(app.LassoPath[0], app.LassoLive) > 0.5f))
        {
            _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
            using (var geo = BuildFreePolyline(app.LassoPath, app.LassoLive))
                _ctx.DrawGeometry(geo, _scratch, 1.6f, Gfx.Round);

            _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.30f);
            var p0 = app.LassoPath[0];
            _ctx.DrawLine(p0, app.LassoLive, _scratch, 1.2f);
            return;
        }

        var r = new Vortice.RawRectF(app.MqMinX, app.MqMinY, app.MqMaxX, app.MqMaxY);
        _ctx.FillRectangle(r, Brush(new Color4(0.25f, 0.6f, 1f, 0.12f)));
        _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
        _ctx.DrawRectangle(r, _scratch, 1.2f);
    }

    /// <summary>
    /// 鎶婁竴涓茬敾甯冨潗鏍囩殑鐐硅繛鎴愪竴鏉?*寮€鏀炬姌绾?*锛堝绱㈤瑙堢敤锛夈€?    ///
    /// 涓轰粈涔堜笉澶嶇敤鎵嬪啓绗旇抗閭ｅ锛氶偅鏄?涓績绾?+ 鍘熺敓鎻忚竟"鐨勫ⅷ锛岃鎸夌瑪瀹姐€侀鑹层€?    /// 鍒嗗潡缂撳瓨璧帮紱濂楃储鍙槸涓€鏍逛复鏃剁殑寮曞绾匡紝姣忓抚鐜板缓鐜版墧灏卞锛堝嚑鍗佷釜椤剁偣锛夈€?    /// </summary>
    private static ID2D1PathGeometry BuildFreePolyline(IReadOnlyList<Vector2> pts, Vector2 tail)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(pts[0], FigureBegin.Hollow);
        for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
        if (Vector2.Distance(tail, pts[pts.Count - 1]) > 0.5f) sink.AddLine(tail);
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    // =====================================================================
    //  鎴浘妯″紡锛?.3.0锛夛細閬僵/鎸栨礊锛堟礊閲屾槸鍐荤粨甯э級/鍙栨櫙妗?鍑嗙嚎/璇绘暟/璋冩暣鎵嬫焺
    //
    //  鍏ㄩ儴鐢诲湪**鐢诲竷鍧愭爣**閲岋紙浼犺繘鏉ョ殑 canvasToTarget 鍐冲畾钀藉埌鍝効锛夛細
    //    路 瀹炴満娓叉煋锛歝anvasToTarget = CanvasToWindow锛堢獥鍙?灞忓箷锛夛紱
    //    路 绂诲睆鍑哄浘锛?-captureshow锛夛細canvasToTarget = 鍑哄浘閭ｆ潯璺殑骞崇Щ鐭╅樀銆?    //  鍚屼竴浠界敾娉曚袱鏉¤矾閮藉锛堝嚭鍥鹃偅鏉¤矾浠ュ墠鍙兘鍑轰竴鍦堟锛岀幇鍦ㄩ伄缃╀篃鑳芥媿鍑烘潵锛夈€?    // =====================================================================

    private void DrawCaptureOverlay(InkEngine app, Matrix3x2 canvasToTarget)
    {
        // 鎶撳睆閭ｄ竴鐬笉鐢绘垜浠嚜宸辩殑浠讳綍涓滆タ锛堝喕缁撳抚灏辨姄鍦ㄨ繖涓€鐬紝瑙?Engine.CaptureFrameHidden锛?        if (!app.CaptureActive || app.CaptureFrameHidden) return;

        float s = Dpi / 96f;
        var amber = new Color4(1f, 0.68f, 0.10f, 1f);
        var white = new Color4(1f, 1f, 1f, 0.92f);
        var saved = _ctx.Transform;
        _ctx.Transform = canvasToTarget;

        var fr = new RectF
        {
            MinX = MathF.Min(app.CapMinX, app.CapMaxX), MinY = MathF.Min(app.CapMinY, app.CapMaxY),
            MaxX = MathF.Max(app.CapMinX, app.CapMaxX), MaxY = MathF.Max(app.CapMinY, app.CapMaxY),
        };
        var screen = app.ViewportCanvas;      // 鍙鐢诲竷 = 灞忓箷
        bool hasHole = fr.MaxX - fr.MinX >= 2f && fr.MaxY - fr.MinY >= 2f;
        string modeName = app.CaptureHideInk ? "闅愯棌绐楀彛鎴浘" : "鎴浘";

        // ---- 鈸?搴曪細**鍐荤粨甯ч摵婊℃暣灞?*锛?.3.1锛夈€?        //   鍙栨櫙鏈熼棿鏁村潡灞忓箷閮界敤"杩涘叆閭ｄ竴鍒绘姄鐨勯偅涓€寮?褰撳簳锛?        //     路 闅愯棌绐楀彛鎴浘锛氶偅寮犳槸**钘忚捣鎴戜滑鏁翠釜瑕嗙洊灞備箣鍚?*鎶撶殑 鈫?灞忓箷涓婄湡鐨勪竴涓佺偣
        //       鎴戜滑鐨勪笢瑗块兘娌℃湁锛?闅愯棌绐楀彛鎴浘"杩欎釜鍚嶅瓧鐪嬪埌鐨勬牱瀛愶級锛?        //     路 鎴浘锛氶偅寮犲甫鐫€鏉夸功锛堟垜浠殑鍙栨櫙灞備粛鐒朵笉杩涘幓锛夈€?        //   閬僵鍘嬪湪瀹冧笂闈⑩€斺€?妗嗗鍘嬫殫銆佹閲屽師鏍?鏉ヨ嚜鍚屼竴寮犲浘锛屼笉浼氬嚭鐜?        //   "妗嗗瀹炴椂銆佹閲屽喕缁?鐨勯敊浣嶆劅锛?.3.0 鐨勯棶棰橈級銆?        //   娌℃湁鍐荤粨甯ф椂锛堝嚭鍥?/ 鍏滃簳锛夊氨鏄笅闈㈢殑瀹炴椂鍐呭銆?---
        if (app.CaptureFrozen && app.CaptureFrozenBgra != null)
        {
            var bmp = EnsureFrozenBitmap(app);
            if (bmp != null)
                _ctx.DrawBitmap(bmp,
                    new Vortice.RawRectF(app.CaptureFrozenX, app.CaptureFrozenY - app.ViewOffsetY,
                                         app.CaptureFrozenX + app.CaptureFrozenW,
                                         app.CaptureFrozenY - app.ViewOffsetY + app.CaptureFrozenH),
                    1f, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
        }

        // ---- 鈶?閬僵锛?2% 榛戦摵婊℃暣涓睆骞曪紝妗嗛噷鎸栨礊锛圗venOdd锛?---
        _scratch.Color = new Color4(0f, 0f, 0f, 0.42f);
        if (!hasHole)
        {
            _ctx.FillRectangle(new Vortice.RawRectF(screen.MinX, screen.MinY, screen.MaxX, screen.MaxY), _scratch);
        }
        else
        {
            using var geo = Gfx.D2DFactory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                sink.SetFillMode(Vortice.Direct2D1.FillMode.Alternate);
                sink.BeginFigure(new Vector2(screen.MinX, screen.MinY), FigureBegin.Filled);
                sink.AddLine(new Vector2(screen.MaxX, screen.MinY));
                sink.AddLine(new Vector2(screen.MaxX, screen.MaxY));
                sink.AddLine(new Vector2(screen.MinX, screen.MaxY));
                sink.EndFigure(FigureEnd.Closed);
                sink.BeginFigure(new Vector2(fr.MinX, fr.MinY), FigureBegin.Filled);
                sink.AddLine(new Vector2(fr.MaxX, fr.MinY));
                sink.AddLine(new Vector2(fr.MaxX, fr.MaxY));
                sink.AddLine(new Vector2(fr.MinX, fr.MaxY));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _ctx.FillGeometry(geo, _scratch);
        }

        // ---- 鈶?鍏ㄥ睆鍑嗙嚎锛?.3.1锛?*涓嶅啀鑷粯灏忓崄瀛?*鈥斺€旂郴缁熷崄瀛楀湪澶栭潰璺熺潃锛?        //      杩欎袱鏉＄嚎鍙洖绛?鎴戠幇鍦ㄥ榻愬埌鍝竴琛?鍝竴鍒?锛夈€傚緟鏈?鎷栨/璋冩暣鍏ㄧ▼閮界敾銆?---
        {
            float gx = app.PointerX, gy = app.PointerY;
            _scratch.Color = new Color4(1f, 1f, 1f, 0.14f);
            _ctx.DrawLine(new Vector2(gx, screen.MinY), new Vector2(gx, screen.MaxY), _scratch, 3f * s);
            _ctx.DrawLine(new Vector2(screen.MinX, gy), new Vector2(screen.MaxX, gy), _scratch, 3f * s);
            _scratch.Color = new Color4(amber.R, amber.G, amber.B, 0.55f);
            _ctx.DrawLine(new Vector2(gx, screen.MinY), new Vector2(gx, screen.MaxY), _scratch, 1f * s);
            _ctx.DrawLine(new Vector2(screen.MinX, gy), new Vector2(screen.MaxX, gy), _scratch, 1f * s);
        }

        // 閫氱敤鑳跺泭鎸夐挳锛堣皟鏁撮樁娈甸偅涓ら + 鍙充笂瑙掔殑銆屸湑 鍙栨秷銆嶅叡鐢ㄤ竴濂楃敾娉曪級
        void PillButton(in RectF rc, string label, bool primary)
        {
            var r3 = new Vortice.RawRectF(rc.MinX, rc.MinY, rc.MaxX, rc.MaxY);
            _scratch.Color = primary ? amber : new Color4(0.16f, 0.17f, 0.20f, 0.86f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(r3, 8f * s, 8f * s), _scratch);
            _ctx.DrawRoundedRectangle(new RoundedRectangle(r3, 8f * s, 8f * s), Brush(white), 1.2f * s);
            _ctx.DrawText(label, CaptureInfoFormat(),
                          new Rect(r3.Left, r3.Top, r3.Right - r3.Left, r3.Bottom - r3.Top),
                          Brush(primary ? new Color4(0.12f, 0.12f, 0.14f, 1f)
                                        : new Color4(1f, 1f, 1f, 1f)));
        }

        // ---- 鈶?鍙栨櫙妗嗭細鐧藉灚 + 鐞ョ弨绾?+ 鍥涜灏忔柟鍧楋紙鏈夋鎵嶇敾锛?---
        if (hasHole)
        {
            var box = new Vortice.RawRectF(fr.MinX, fr.MinY, fr.MaxX, fr.MaxY);
            _ctx.DrawRectangle(box, Brush(white), 3.4f * s);
            _scratch.Color = amber;
            _ctx.DrawRectangle(box, _scratch, 1.8f * s);
            void CornerSq(float x, float y)
            {
                float q = 5f * s;
                var r2 = new Vortice.RawRectF(x - q * 0.5f, y - q * 0.5f, x + q * 0.5f, y + q * 0.5f);
                _ctx.FillRectangle(r2, Brush(white));
                _scratch.Color = amber;
                _ctx.DrawRectangle(r2, _scratch, 1.2f * s);
            }
            CornerSq(fr.MinX, fr.MinY); CornerSq(fr.MaxX, fr.MinY);
            CornerSq(fr.MaxX, fr.MaxY); CornerSq(fr.MinX, fr.MaxY);

            // ---- 鈶?灏哄璇绘暟锛氭鍙充笅瑙掑渚э紙璐磋竟缈诲埌涓婃柟/澶硅繘灞忓箷锛夛紱
            //      璋冩暣闃舵鐢诲湪妗嗗唴鍙充笅瑙掆€斺€旀涓嬮潰璁╃粰銆屸湏 鉁曘€嶄袱棰楁寜閽簡銆?---
            float rw = fr.MaxX - fr.MinX, rh = fr.MaxY - fr.MinY;
            if (rw >= 24f * s && rh >= 16f * s)
            {
                string text = $"{rw / s:F0} 脳 {rh / s:F0}";
                float boxW = 104f * s, boxH = 26f * s, gap = 6f * s;
                float bx, by;
                if (app.CaptureAdjusting)
                {
                    bx = fr.MaxX - boxW - 8f * s;
                    by = fr.MaxY - boxH - 8f * s;
                }
                else
                {
                    bx = Math.Clamp(fr.MaxX - boxW, screen.MinX + 4f * s, screen.MaxX - boxW - 4f * s);
                    by = fr.MaxY + gap;
                    if (by + boxH > screen.MaxY - 4f * s) by = fr.MinY - gap - boxH;
                    by = Math.Clamp(by, screen.MinY + 4f * s, screen.MaxY - boxH - 4f * s);
                }
                var pill = new Vortice.RawRectF(bx, by, bx + boxW, by + boxH);
                _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.82f);
                _ctx.FillRoundedRectangle(new RoundedRectangle(pill, boxH * 0.5f, boxH * 0.5f), _scratch);
                _ctx.DrawText(text, CaptureInfoFormat(),
                              new Rect(pill.Left, pill.Top, pill.Right - pill.Left, pill.Bottom - pill.Top),
                              Brush(new Color4(1f, 1f, 1f, 1f)));
            }
        }

        // ---- 鈶?椤堕儴鎻愮ず锛堟ā寮忓悕 + 鎬庝箞鎿嶄綔锛?---
        {
            string hint = app.CaptureAdjusting
                ? $"{modeName} 路 鎷栧姩杈硅璋冩暣 路 Enter / 鍙屽嚮 = 瀹屾垚 路 Esc / 鍙抽敭 鍙栨秷"
                : $"{modeName} 路 鎷栧姩妗嗛€夋埅鍥惧尯鍩?路 Ctrl+S 鎹㈡ā寮?路 Esc / 鍙抽敭 鍙栨秷";
            float cx = (screen.MinX + screen.MaxX) * 0.5f;
            var pill = new Vortice.RawRectF(cx - 280f * s, screen.MinY + 40f * s,
                                            cx + 280f * s, screen.MinY + 74f * s);
            _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.72f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(pill, 17f * s, 17f * s), _scratch);
            _ctx.DrawText(hint, CaptureInfoFormat(),
                          new Rect(pill.Left, pill.Top, pill.Right - pill.Left, pill.Bottom - pill.Top),
                          Brush(new Color4(1f, 1f, 1f, 0.95f)));
        }

        // ---- 鈶?璋冩暣闃舵锛? 涓墜鏌?+ 銆屸湏 瀹屾垚 / 鉁?鍙栨秷銆嶏紱寰呮満/鎷栨涓細鍙充笂瑙掋€屸湑 鍙栨秷銆?---
        if (app.CaptureAdjusting)
        {
            app.CaptureHandles(out var tl, out var t, out var tr, out var r,
                               out var br, out var b, out var bl, out var l);
            void Handle(Vector2 p)
            {
                float q = 3.5f * s;
                var r2 = new Vortice.RawRectF(p.X - q, p.Y - q, p.X + q, p.Y + q);
                _ctx.FillRectangle(r2, Brush(white));
                _scratch.Color = amber;
                _ctx.DrawRectangle(r2, _scratch, 1.2f * s);
            }
            Handle(tl); Handle(t); Handle(tr); Handle(r);
            Handle(br); Handle(b); Handle(bl); Handle(l);

            app.CaptureButtons(out var okBtn, out var cancelBtn);
            PillButton(okBtn, "鉁?瀹屾垚", primary: true);
            PillButton(cancelBtn, "鉁?鍙栨秷", primary: false);
        }
        else
        {
            // 鍙栨櫙鏈熼棿宸ュ叿鏉℃槸鏀惰捣鐨勶紙瑙?Overlay.PrepareUi锛夛細瑙︽懜灞忔病鏈?Esc / 鍙抽敭锛?            // 寰楁湁涓€棰楃湅寰楄鐨勫嚭鍙ｃ€傛斁鍦ㄥ彸涓婅锛屼笉鍜岄《閮ㄧ殑鎻愮ず鎵撴灦銆?            PillButton(app.CaptureCancelRect(), "鉁?鍙栨秷", primary: false);
        }

        _ctx.Transform = saved;
    }

    // ---- 鍐荤粨甯т綅鍥剧紦瀛橈紙姣忎釜绐楀彛涓€浠斤紝鎸?buffer 寮曠敤鍒?鎹㈡病鎹?锛?---

    private ID2D1Bitmap _frozenBmp;
    private byte[] _frozenSrc;
    private int _frozenW, _frozenH;

    /// <summary>鏀炬帀鍐荤粨甯т綅鍥撅紙鎴浘鏀跺満鍚庤皟鐢級銆?*杩炵潃閭ｄ唤鍍忕礌鏁扮粍涓€璧锋澗寮€**鈥斺€?    /// 鍙竻浣嶅浘涓嶆竻寮曠敤鐨勮瘽锛岄偅寮?20MB 鐨勬暣灞忓儚绱犱細琚繖鍧楃紦瀛樹竴鐩存嫿鐫€涓嶆斁銆?/summary>
    private void ReleaseFrozenBitmap()
    {
        if (_frozenBmp == null && _frozenSrc == null) return;
        _frozenBmp?.Dispose();
        _frozenBmp = null; _frozenSrc = null; _frozenW = _frozenH = 0;
    }

    private ID2D1Bitmap EnsureFrozenBitmap(InkEngine app)
    {
        if (app.CaptureFrozenBgra == null) return null;
        if (_frozenBmp != null && ReferenceEquals(_frozenSrc, app.CaptureFrozenBgra)
            && _frozenW == app.CaptureFrozenW && _frozenH == app.CaptureFrozenH)
            return _frozenBmp;
        _frozenBmp?.Dispose(); _frozenBmp = null; _frozenSrc = null;
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f, BitmapOptions.None);
        var handle = GCHandle.Alloc(app.CaptureFrozenBgra, GCHandleType.Pinned);
        try
        {
            _frozenBmp = _ctx.CreateBitmap(new SizeI(app.CaptureFrozenW, app.CaptureFrozenH),
                                           handle.AddrOfPinnedObject(), (uint)(app.CaptureFrozenW * 4), props);
        }
        catch { _frozenBmp = null; }
        finally { handle.Free(); }
        if (_frozenBmp != null)
        { _frozenSrc = app.CaptureFrozenBgra; _frozenW = app.CaptureFrozenW; _frozenH = app.CaptureFrozenH; }
        return _frozenBmp;
    }

    /// <summary>鍙栨櫙妗嗘梺杈圭殑璇绘暟/鎻愮ず/鎸夐挳鐨勬枃瀛楁牸寮忥紙14 閫昏緫鍍忕礌銆佸眳涓€佹寜 DPI 鐢熸垚涓€娆★級銆?/summary>
    private IDWriteTextFormat _capInfoFmt;
    private IDWriteTextFormat CaptureInfoFormat()
    {
        if (_capInfoFmt != null) return _capInfoFmt;
        _capInfoFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 14f * (Dpi / 96f), "zh-CN");
        _capInfoFmt.TextAlignment = TextAlignment.Center;
        _capInfoFmt.ParagraphAlignment = ParagraphAlignment.Center;
        return _capInfoFmt;
    }

    private float HudScale => Dpi / 96f;
    public float HudWidthPx => HudWidthLogical * HudScale;
    public float HudHeightPx => HudHeightLogical * HudScale;

    /// <summary>
    /// 鎬ц兘闈㈡澘鐨勬枃瀛楁牸寮忥紝**鎸?DPI 鐢熸垚骞剁紦瀛?*銆?    ///
    /// 杩欐槸"鐪嬩笉娓?鐨勭湡姝ｅ師鍥狅細娓叉煋鏃剁殑鍙樻崲鍙湁骞崇Щ銆佹病鏈夌缉鏀撅紝瀛楀彿鍐欐 15
    /// 灏辩瓑浜?15 鐗╃悊鍍忕礌鈥斺€?00% 缂╂斁鐨勫睆涓婂彧鏈?7.5 閫昏緫鍍忕礌楂橈紝鎶曞奖涓婄硦鎴愪竴鍥€?    /// </summary>
    private IDWriteTextFormat HudFormat()
    {
        float px = MathF.Max(11f, MathF.Round(HudFontLogical * HudScale));
        if (_hudFormat == null || _hudFormatPx != px)
        {
            _hudFormat?.Dispose();
            _hudFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _hudFormatPx = px;
        }
        return _hudFormat;
    }

    /// <summary>
    /// 闈㈡澘杩涚紦瀛樹綅鍥俱€?*蹇呴』鍦ㄥ抚鐨?BeginDraw 涔嬪墠璋冪敤**鈥斺€擠irect2D 涓嶅厑璁稿湪
    /// BeginDraw/EndDraw 涓棿鎹㈡覆鏌撶洰鏍囷紙鎹簡灏辨妸璁惧鎼炶繘閿欒鐘舵€侊紝杩欐潯韪╄繃锛夈€?    ///
    /// 涓轰粈涔堣缂撳瓨锛氳€佸疄鐜版槸姣忓抚鐩存帴鎺?7 琛屾枃瀛楋紝瀹炴祴闈㈡澘鑷繁鍚冩帀 4.3 ms/甯э紝
    /// 姣斿畠鏄剧ず鐨勬墍鏈変笢瑗垮姞璧锋潵杩樿吹锛堢瑪杩规墠 2 ms锛夈€傛枃瀛楁槸 4 Hz 鎵嶅彉鐨勶紝
    /// 姣忓抚閲嶆帓绾睘娴垂銆傜幇鍦ㄥ彧鏈夋枃瀛楀彉浜嗘墠閲嶆帓涓€娆★紝骞虫椂鍙槸涓€娆¤创鍥俱€?    /// </summary>
    private void PrepareHud(InkEngine app)
    {
        if (!app.ShowHud) { _hudCacheText = null; return; }

        float scale = HudScale;
        int w = (int)MathF.Ceiling(HudWidthPx), h = (int)MathF.Ceiling(HudHeightPx);
        EnsureHudBitmap(w, h);

        string text = app.HudText ?? "";
        _hudRedrewThisFrame = false;
        if (_hudCacheText == text) return;

        _ctx.Target = _hudTarget;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;
        var box = new Vortice.RawRectF(0, 0, w, h);
        _ctx.FillRectangle(box, Brush(new Color4(0.05f, 0.06f, 0.09f, 0.86f)));
        _scratch.Color = new Color4(0.35f, 0.8f, 1f, 0.9f);
        _ctx.DrawRectangle(new Vortice.RawRectF(0.5f, 0.5f, w - 0.5f, h - 0.5f), _scratch, 1.5f);
        _scratch.Color = new Color4(0.94f, 0.97f, 1f, 1f);
        // 娉ㄦ剰 Rect 鏄?(x, y, width, height)锛屼笉鏄?(left, top, right, bottom)銆?        float pad = HudPadLogical * scale;
        _ctx.DrawText(text, HudFormat(), new Rect(pad, pad * 0.8f, w - pad * 2f, h - pad * 1.6f), _scratch);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.Target = null;
        var hr = _ctx.EndDraw();
        if (hr.Failure) LastError = "hud EndDraw: " + hr.Description;
        _hudCacheText = text;
        _hudRedrewThisFrame = true;
        HudRedraws++;
    }

    private bool _hudRedrewThisFrame;

    private void EnsureHudBitmap(int w, int h)
    {
        if (_hudTarget != null && _hudBmpW == w && _hudBmpH == h) return;
        _hudSource?.Dispose(); _hudTarget?.Dispose(); _hudBmpTex?.Dispose();
        _hudSource = null; _hudTarget = null; _hudBmpTex = null;

        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, w), Height = (uint)Math.Max(1, h),
            MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _hudBmpTex = Gfx.Device.CreateTexture2D(desc);
        var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                                                 Vortice.DCommon.AlphaMode.Premultiplied);
        using (var surface = _hudBmpTex.QueryInterface<IDXGISurface>())
        {
            // 涓€寮犵汗鐞嗕袱涓鍥撅細褰撴覆鏌撶洰鏍囩殑閭ｅ紶涓嶈兘鍚屾椂褰撶粯鍒舵簮锛圖irect2D 鐨勮瀹氾級銆?            _hudTarget = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            _hudSource = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.None));
        }
        _hudBmpW = w; _hudBmpH = h;
        _hudCacheText = null;      // 灏哄鍙樹簡蹇呴』閲嶆帓
    }

    /// <summary>鎶婄紦瀛樺ソ鐨勯潰鏉胯创鍒板悗缂撳啿涓婏紙甯у唴璋冪敤锛屼唬浠峰氨鏄竴娆′綅鍥炬嫹璐濓級銆?/summary>
    private void DrawHud()
    {
        if (_hudSource == null) return;
        float m = HudMarginLogical * HudScale;
        var dst = new Vortice.RawRectF(m, m, m + _hudBmpW, m + _hudBmpH);
        _ctx.DrawBitmap(_hudSource, dst, 1f,
                        Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
    }

    // ---- 瑙︾偣璇婃柇娴眰锛?.3.3锛岄粯璁ゅ叧锛?-----------------------------------

    private const float TouchHudWidthLogical = 470f;
    private const float TouchHudHeightLogical = 86f;

    /// <summary>瑙︾偣璇婃柇娴眰鐨?*灞忓箷鍧愭爣**鐭╁舰锛堣剰鍖虹敤锛涚粯鍒堕偅浠芥槸瀹冪殑绐楀彛灞€閮ㄧ増锛夈€?/summary>
    private RectF TouchHudRect(InkEngine app)
    {
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = TouchHudWidthLogical * s, h = TouchHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return RectF.Empty;
        return new RectF
        {
            MinX = OriginX + m, MinY = OriginY + Height - m - h,
            MaxX = OriginX + m + w, MaxY = OriginY + Height - m,
        };
    }

    private IDWriteTextFormat _touchHudFmt;
    private IDWriteTextFormat TouchHudFormat()
    {
        if (_touchHudFmt != null) return _touchHudFmt;
        _touchHudFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13.5f * (Dpi / 96f), "zh-CN");
        _touchHudFmt.TextAlignment = TextAlignment.Leading;
        _touchHudFmt.ParagraphAlignment = ParagraphAlignment.Near;
        _touchHudFmt.WordWrapping = WordWrapping.NoWrap;
        return _touchHudFmt;
    }

    private void DrawTouchHud(InkEngine app)
    {
        if (!app.TouchHud) return;
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = TouchHudWidthLogical * s, h = TouchHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return;

        // 鐢诲湪**绐楀彛灞€閮?*鍧愭爣锛圤rigin 閭ｄ竴浠藉彧鐢ㄥ湪鑴忓尯涓婏紝鍜屾€ц兘闈㈡澘鍚屼竴涓璺級銆?        var box = new Vortice.RawRectF(m, Height - m - h, m + w, Height - m);
        _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.80f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s), _scratch);
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s),
                                  Brush(new Color4(1f, 1f, 1f, 0.25f)), 1f * s);
        _ctx.DrawText(app.TouchHudText ?? "", TouchHudFormat(),
                      new Rect(box.Left + 10f * s, box.Top + 6f * s,
                               box.Right - box.Left - 20f * s, box.Bottom - box.Top - 12f * s),
                      Brush(new Color4(1f, 1f, 1f, 0.95f)));
    }

    // ---- 姗＄毊璇绘暟娴眰锛?.3.7锛岄粯璁ゅ叧锛涜皟鍙傜敤锛?-------------------------------

    private const float EraserHudWidthLogical = 760f;
    private const float EraserHudHeightLogical = 62f;

    /// <summary>姗＄毊璇绘暟娴眰鐨?*灞忓箷鍧愭爣**鐭╁舰锛堣剰鍖虹敤锛夈€傛斁鍦ㄥ乏涓嬭锛堣Е鐐规诞灞傚紑鐫€灏辫鍒板畠涓婇潰锛夈€?/summary>
    private RectF EraserHudRect(InkEngine app)
    {
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = EraserHudWidthLogical * s, h = EraserHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return RectF.Empty;
        float bottom = Height - m - (app.TouchHud ? (TouchHudHeightLogical + 8f) * s : 0f);
        return new RectF
        {
            MinX = OriginX + m, MinY = OriginY + bottom - h,
            MaxX = OriginX + m + w, MaxY = OriginY + bottom,
        };
    }

    private IDWriteTextFormat _eraserHudFmt;
    private IDWriteTextFormat EraserHudFormat()
    {
        if (_eraserHudFmt != null) return _eraserHudFmt;
        _eraserHudFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13.5f * (Dpi / 96f), "zh-CN");
        _eraserHudFmt.TextAlignment = TextAlignment.Leading;
        _eraserHudFmt.ParagraphAlignment = ParagraphAlignment.Near;
        _eraserHudFmt.WordWrapping = WordWrapping.NoWrap;
        return _eraserHudFmt;
    }

    private void DrawEraserHud(InkEngine app)
    {
        if (!app.EraserHud) return;
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = EraserHudWidthLogical * s, h = EraserHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return;

        float bottom = Height - m - (app.TouchHud ? (TouchHudHeightLogical + 8f) * s : 0f);
        var box = new Vortice.RawRectF(m, bottom - h, m + w, bottom);
        _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.80f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s), _scratch);
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s),
                                  Brush(new Color4(1f, 1f, 1f, 0.25f)), 1f * s);
        _ctx.DrawText(app.EraserHudText ?? "", EraserHudFormat(),
                      new Rect(box.Left + 10f * s, box.Top + 6f * s,
                               box.Right - box.Left - 20f * s, box.Bottom - box.Top - 12f * s),
                      Brush(new Color4(1f, 1f, 1f, 0.95f)));
    }

    public void Present()
    {
        _presentRects.Clear();
        double area = 0;
        foreach (var r in _frameDirty)
        {
            int l = (int)MathF.Floor(MathF.Max(0, r.MinX - OriginX));
            int t = (int)MathF.Floor(MathF.Max(0, r.MinY - OriginY));
            int rr = (int)MathF.Ceiling(MathF.Min(Width, r.MaxX - OriginX));
            int b = (int)MathF.Ceiling(MathF.Min(Height, r.MaxY - OriginY));
            if (rr <= l || b <= t) continue;
            _presentRects.Add(new Vortice.RawRect(l, t, rr, b));
            area += (long)(rr - l) * (b - t);
        }

        LastPresentRectCount = _presentRects.Count;
        LastPresentAreaPercent = Width > 0 && Height > 0
            ? area / (Width * (double)Height) * 100.0
            : 100.0;

        if (Trace)
        {
            int buf = -1;
            try { if (_swapChain3 != null) buf = (int)_swapChain3.CurrentBackBufferIndex; } catch { }
            var b = RectF.Empty;
            foreach (var r in _frameDirty) { b.Add(r.MinX, r.MinY); b.Add(r.MaxX, r.MaxY); }
            int l0 = int.MaxValue, t0 = int.MaxValue, r0 = int.MinValue, bo0 = int.MinValue;
            foreach (var r in _presentRects)
            {
                if (r.Left < l0) l0 = r.Left;
                if (r.Top < t0) t0 = r.Top;
                if (r.Right > r0) r0 = r.Right;
                if (r.Bottom > bo0) bo0 = r.Bottom;
            }
            Console.WriteLine($"    [present] rects={_presentRects.Count} area={LastPresentAreaPercent:F0}% "
                              + (l0 <= r0 ? $"rectsBox=({l0},{t0})..({r0},{bo0}) " : "")
                              + $"dirtyBox=({b.MinX:F0},{b.MinY:F0})..({b.MaxX:F0},{b.MaxY:F0}) "
                              + $"camY={ViewOffsetY:F0} backBuf={buf}");
        }

        var sw = Stopwatch.StartNew();
        SharpGen.Runtime.Result hr;

        // 璇曢獙D锛?026-10-11锛孉/B锛夛細姣忓抚 DComp 鎻愪氦锛圕hromium 鐨勫仛娉曪紱鎴戜滑浠ュ墠鍙彁浜よ繃涓€娆★級銆?        if (TrailCommitEveryFrame && _inkTrail != null)
        {
            try { _dcomp.Commit(); } catch { }
        }

        // 鐢ㄧ瓑寰呭璞″厛"鎺掗槦"锛氬畠鍙戜俊鍙锋椂鎵嶆槸鎻愪氦涓嬩竴甯х殑姝ｇ‘鏃舵満銆?        // 杩欐牱 Present 鐢?syncInterval=0 绔嬪嵆杩斿洖锛屼笉蹇呰椹卞姩鍦ㄥ瀭鐩村悓姝ラ噷蹇欑瓑銆?        bool useWaitable = _latencyWait != IntPtr.Zero;
        if (useWaitable)
            Native.WaitForSingleObjectEx(_latencyWait, 100, true);

        LastPresentStartQpc = Qpc.Now;

        // 鑴忓尯澶皯鎴栧お澶ч兘涓嶅垝绠楋細澶ぇ涓嶅鐩存帴鏁村睆涓婂睆锛屽お灏戣鏄庤繖涓€甯?        // 娌′粈涔堝彉鍖栵紙渚嬪鍙瓑鍨傜洿鍚屾锛夈€?-fullpresent = 姘歌繙鏁村睆涓婂睆锛堣瘖鏂級銆?        if (FullPresent
            || _presentRects.Count == 0 || _presentRects.Count > 16 || LastPresentAreaPercent > 80.0)
        {
            hr = _swapChain.Present(useWaitable ? 0u : 1u, PresentFlags.None);
            LastPresentAreaPercent = 100.0;
            LastPresentRectCount = 0;
        }
        else
        {
            hr = _swapChain.Present1(useWaitable ? 0u : 1u, PresentFlags.None,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_presentRects), null, null);
        }

        sw.Stop();
        LastPresentEndQpc = Qpc.Now;
        LastPresentMs = sw.Elapsed.TotalMilliseconds;
        // `--syswet` 鏀剁瑪鎸傝捣鐨勮建杩规挙闄わ細骞插ⅷ杩欎竴甯у凡缁忔彁浜わ紝鐜板湪鎾や笉浼氶湶鍑虹┖妗ｃ€?        if (_trailEndPending) EndInkTrailNow();
        // 搴旂敤渚ф箍澧ㄦ浛韬篃蹇呴』绛変富浜ゆ崲閾炬妸骞插ⅷ鎻愪氦鍚庡啀娓呯┖锛岄伩鍏嶆敹绗旀椂鍑虹幇绌虹櫧甯с€?        if (!hr.Failure && _fastWetEndPending) EndFastWetNow();
        // 杞ㄨ抗"缁撹处"锛?026-10-11锛夛細杩欎竴绗旀垜浠嚜宸卞凡缁忕敾涓婂睆浜嗭紝鎶婅建杩归噷鍚屾牱杩欎竴娈靛垹鎺夈€?        // 鈿?鍙垹"鑷粯杩?鐨勬儏鍐碉紱`--syswet` / 鎶戝埗鑷粯鏃朵笉鍒狅紙閭ｆ椂婀垮ⅷ鍙湁杞ㄨ抗杩欎竴浠斤級銆?        if (!hr.Failure && _drewWetStrokeThisFrame) TrimInkTrailAsDrawn();
        _drewWetStrokeThisFrame = false;
        if (hr.Failure)
        {
            LastError = "Present: " + hr.Description;
            // 澶辫触 = 杩欎竴甯ф病缈讳笂鍘伙紝鍙岀紦鍐?鍙︿竴鍧楁槸涓婁竴甯?鐨勫墠鎻愬彲鑳戒笉鍐嶆垚绔嬨€?            // 涓嬩竴甯у己鍒惰繛缁袱甯ф暣灞忥紝鎶婄姸鎬侀噸鏂板榻?            //锛堝榻?Windows Terminal 鐨勮鐭╋細澶辨晥琚秷璐瑰悗鎻愪氦澶辫触 鈫?涓嬩竴甯у叏閲忛噸鏉ワ級銆?            _fullFramesLeft = 2;
        }

    }


    public void Dispose()
    {
        _brushes.Clear();
        _scratch?.Dispose();
        _readoutFormat?.Dispose();
        _readoutFormatSmall?.Dispose();
        _hudFormat?.Dispose();
        _hudSource?.Dispose();
        _hudTarget?.Dispose();
        _hudBmpTex?.Dispose();
        _pickSvBmp?.Dispose();           // 鑷畾涔夊彇鑹叉澘鐨勭紦瀛樹綅鍥撅紙8.2.0锛?        _pickHueBmp?.Dispose();
        _frozenBmp?.Dispose();           // 鎴浘鐨勫喕缁撳抚浣嶅浘锛?.3.0锛?        _tiles?.Dispose();
        _backBuffer?.Dispose();
        _inkStyle?.Dispose();          // 澧ㄨ抗绗斿皷鏍峰紡锛堝帇鎰熷彉瀹介偅鏉¤矾锛?        _ctx?.Dispose();
        _inkVisual?.Dispose();
        _fastWetSurface?.Dispose();
        _fastWetVisual?.Dispose();
        _swapVisual?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _dcomp2?.Dispose();
        _swapChain?.Dispose();
        _swapChain2?.Dispose();
        if (_latencyWait != IntPtr.Zero) { Native.CloseHandle(_latencyWait); _latencyWait = IntPtr.Zero; }
        if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }
}
