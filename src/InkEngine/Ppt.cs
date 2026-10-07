using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

// =====================================================================================
//  PPT 模式：引擎侧的状态机与存取
//
//  参考出处（用户 2026-09-26 拍板"完全隔离，参考 InkClass 原味"）：
//    · **InkClass**（GPLv3，本机 `画布测试\Ink-Canvas-Dev\Ink Canvas\MW_PPT.cs`）：
//      进放映装载 / 翻页保存上一页再装下一页 / 退出放映逐页写盘 + 记 Position 的
//      生命周期，全部照它的语义；"清空只清当前页"也来自它（见 Model 的页槽注释）。
//    · **Ink Canvas Ultra**（GPLv3，`MW_PPT.cs`）：页的键用 **SlideID**（插入/删除页
//      后批注不串）、退出放映回桌面批注。
//    · **ICC 社区版**（GPLv3，`PPTInkManager.cs`）：每页一个文件、
//      "退出写盘、进入读回、Position 记上次播到第几页"。
//
//  我们的适配（两处，都是用户点的头）：
//    ① **页槽代替"清空 + 重放"**：切页 O(1)（见 Model.cs 的 PageSlot）；
//       桌面批注天然留在 0 号页，**不需要** Ultra 那套 `_desktopStrokesBackup` 备份/还原；
//    ② **每页记住自己滚到哪**（相机偏移），而且保留"页内自由滚动"（用户：
//       "它们不能上下滚动，我这个依然支持向下滚动"）——相机只挪视野，不改任何坐标。
//
//  许可提醒：上面三份都是 GPLv3。本文件是**按它们的逻辑改写**（不是逐字拷贝），
//  真要闭源分发时应由法务过一遍；出处与许可见本节。
// =====================================================================================

/// <summary>
/// PPT 批注的落盘：**一份演示文稿一个目录、一页一个文件**。
///
/// 目录：`%LOCALAPPDATA%\InkTeach\Ppt\{名字_路径哈希\}\{键}.inkb` ＋ `Position`。
/// 为什么不放"我的文档"（InkClass 的做法）：那是给老师看的目录，而这份数据是
/// "下次进放映自动读回来"的内部缓存；用户要"一键保存到本地"时再另开显式的导出。
/// 目录名带**路径哈希**（参考 ICC 的 `{Name}_{Count}_{Hash}`，但不带页数——
/// 页数是会变的，InkClass 的目录名带页数，插一页批注就找不到）。
/// </summary>
internal static class PptStore
{
    /// <summary>自检用：把根目录指到临时目录，**别动用户真实的 PPT 批注**（同 Recovery 的套路）。</summary>
    public static string RootOverride;

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "Ppt");

    private static string DirOf(string key) => Path.Combine(Root, SafeName(key));

    /// <summary>把演示文稿标识洗成一个合法目录名（路径里那些 <c>\/:*?"&lt;&gt;|</c> 全换掉）。</summary>
    private static string SafeName(string key)
    {
        if (string.IsNullOrEmpty(key)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(key.Length);
        foreach (char c in key) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private static string FileOf(string key, int pageKey)
        => Path.Combine(DirOf(key), pageKey.ToString("D8", CultureInfo.InvariantCulture) + ".inkb");

    /// <summary>写一页。**先写 .tmp 再换名**——写一半断电不能留下一个坏文件（同 Recovery）。</summary>
    public static void SavePage(string key, int pageKey, byte[] blob)
    {
        var path = FileOf(key, pageKey);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>读一页。读不出来返回 null（当作"这一页还没有批注"）。</summary>
    public static byte[] LoadPage(string key, int pageKey)
    {
        try
        {
            var path = FileOf(key, pageKey);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    /// <summary>这份演示文稿已经存了哪几页（文件名就是页键）。</summary>
    public static List<int> ListPageKeys(string key)
    {
        var list = new List<int>();
        try
        {
            var dir = DirOf(key);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.inkb"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id != 0)
                    list.Add(id);
            }
        }
        catch { }
        return list;
    }

    /// <summary>记"上次播到第几页"（下次进放映时要用；第一批先只记不跳，见 Ppt.cs 的说明）。</summary>
    public static void SavePosition(string key, int slide)
    {
        try
        {
            var dir = DirOf(key);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Position"),
                slide.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }

    public static int LoadPosition(string key)
    {
        try
        {
            string path = Path.Combine(DirOf(key), "Position");
            if (!File.Exists(path)) return 0;
            return int.TryParse(File.ReadAllText(path), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int v) ? v : 0;
        }
        catch { return 0; }
    }

    /// <summary>删掉某一页的文件（那一页被清空了；没有就算）。</summary>
    public static void DeletePage(string key, int pageKey)
    {
        try
        {
            var path = FileOf(key, pageKey);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// 把这份演示文稿的批注**从盘上删掉**（"墨迹清空"用）。
    ///
    /// 产品里**只有这一条删盘的路**：清空必须真的把文件删了，不能指望"退出时写一份空内容"
    /// 那条间接路——那条要依赖"自动保存开着"，关着的时候老师会发现"我清了啊，怎么还有"。
    /// </summary>
    public static void DeleteAll(string key)
    {
        try
        {
            var dir = DirOf(key);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { Console.WriteLine("删 PPT 批注失败：" + ex.Message); }
    }
}

/// <summary>引擎的 PPT 部分（见文件头：参考出处与我们的两处适配）。</summary>
public partial class InkEngine
{
    private IPptSource _pptSource;
    private PptWatcher _pptWatcher;
    private PptSnapshot _pptSeen;
    private string _pptKey = "";

    /// <summary>每页记住"滚到哪"（相机偏移）。零成本——相机本来就是我们自己的一个数，
    /// 不像 InkClass 那样要"按页存偏移 + 改坐标"（那正是它注释里记着的坑）。</summary>
    private readonly Dictionary<int, float> _pageScroll = new();

    /// <summary>正在 PPT 模式（页隔离生效、界面显示页码）。</summary>
    internal bool PptMode { get; private set; }
    /// <summary>放映到第几页（1 起；0 = 没在放映）。</summary>
    internal int PptSlide { get; private set; }
    internal int PptTotal { get; private set; }

    /// <summary>自检/探针用：轮询线程真的跑了几次（证明"设了不是忘了"）。</summary>
    internal int PptPollCount => _pptWatcher?.PollCount ?? 0;

    /// <summary>当前页的键（0 = 桌面页）——自检直接读它断言"翻页真的换了页"。</summary>
    internal int CurrentPageKey => Doc.PageKey;

    /// <summary>
    /// 产品启动时接上 PPT 联动（**自检 / 基准模式不接**：那要碰 COM，
    /// 判据会变得不确定，而且自检机上多半没装 Office）。
    /// 没装 Office、没开 PPT 时它什么都不做（见 PptComSource 的静默约定）。
    /// </summary>
    internal void StartPptLink()
    {
        if (SelfCheckMode) return;
        AttachPptSource(new PptComSource(), watch: true);
    }

    /// <summary>
    /// 接上页码源。产品：接 COM 源 + 开轮询线程；自检：接假源、**不开线程**
    /// （`StepPpt` 直接向源要一次状态）。两条路走的是同一份状态机。
    /// </summary>
    internal void AttachPptSource(IPptSource src, bool watch)
    {
        _pptSource = src;
        if (!watch || SelfCheckMode) return;
        _pptWatcher = new PptWatcher(src, Native.GetCurrentThreadId());
        _pptWatcher.Start();
    }

    internal void StopPptWatch() => _pptWatcher?.Stop();

    /// <summary>
    /// 状态机走一步。主循环每帧调一次（没变化时只读一个 bool，**不碰 COM**）。
    /// 自检也调它——"测量用的循环和真正的循环必须同源"（这个项目栽过）。
    /// </summary>
    internal void StepPpt()
    {
        if (_pptSource == null) return;
        if (_pptWatcher != null && !_pptWatcher.TakeDirty()) return;

        var s = _pptWatcher != null ? _pptWatcher.Snapshot() : _pptSource.Poll();
        if (s.SameAs(_pptSeen)) return;
        _pptSeen = s;
        ApplyPptState(s);
    }

    /// <summary>
    /// 页的键：优先 **SlideID**（插页/删页不串，Ultra 的做法）；读不到（0）时用
    /// **负页码**兜底——参考 Ultra 的 `GetStrokeCacheKey`，负号是为了不和真实
    /// SlideID（正整数）撞车。
    /// </summary>
    private static int PageKeyOf(in PptSnapshot s) => s.SlideId > 0 ? s.SlideId : -Math.Max(1, s.Slide);

    private void ApplyPptState(in PptSnapshot s)
    {
        if (s.Showing && s.Total > 0)
        {
            if (!PptMode)
            {
                EnterPptMode(s);
                return;
            }

            int want = PageKeyOf(s);
            if (want == Doc.PageKey)
            {
                // 同一页，只是页码数字变了（理论上不该有，兜一下）：只更新界面数字。
                if (PptSlide != s.Slide || PptTotal != s.Total)
                {
                    PptSlide = s.Slide;
                    PptTotal = s.Total;
                    NotifyUiStateChanged();
                }
                return;
            }

            // 翻页：**离开的页先落盘**（参考 ICC "退出时统一存"，这里多一道保险：
            // 一节课中途崩了，前面的页不会丢）。
            SaveCurrentPptPage();
            PptSlide = s.Slide;
            PptTotal = s.Total;
            GotoPage(want);
            NotifyUiStateChanged();
            Console.WriteLine($"[PPT] 第 {s.Slide}/{s.Total} 页（键 {want}）");
        }
        else if (PptMode)
        {
            ExitPptMode();
        }
    }

    /// <summary>
    /// 进放映：读回这份演示文稿的各页 → 切到当前页。
    /// 老师进放映前的桌面批注**留在 0 号页里**（页槽天然就是备份，见文件头 ①）。
    /// </summary>
    private void EnterPptMode(in PptSnapshot s)
    {
        ExitReplayForEdit("进放映");
        PptMode = true;
        SyncPptHotkeys();              // 放映临时全局键：挂上（穿透开着则不挂，见 Engine.PptHotkeys）
        _pptKey = s.Key ?? "";
        PptSlide = s.Slide;
        PptTotal = s.Total;

        // 开关：每次进放映读一次（默认开）——"关"时下面的读盘 / 写盘全跳过。
        PptAutoSaveOn = GetUiPref(PptAutoSavePref) != "0";
        PptClearConfirm = false;
        LoadPptPages();
        // 位置：进放映时读一次（老师上次拖到哪儿，这次还在那儿——用户 2026-09-26 要的）。
        LoadPptBarPos();
        // 引导：**每次进放映都提示一遍**（约 1.5 秒）——用户 2026-09-27 定、
        // 2026-10-02 第五轮改成"点页码：页码跳转菜单"。
        //
        // 保留"每次都提示"的理由：菜单虽然改成了点一下就能看见，但老师未必会去点
        // 那个数字；一行 1.5 秒的小字最省事，也不会挡着讲课（用户要的是"一两秒钟"）。
        _pptHintUntilMs = NowMs + PptHintMs;
        GotoPage(PageKeyOf(s));
        NotifyUiStateChanged();
        Console.WriteLine($"[PPT] 进入放映：{_pptKey}，第 {s.Slide}/{s.Total} 页");
    }

    /// <summary>退放映：逐页写盘（含 Position）→ 切回桌面页（批注原样回来）。</summary>
    private void ExitPptMode()
    {
        PptMode = false;
        SyncPptHotkeys();              // 放映临时全局键：注销（平时一个键都不多占）
        // **把键盘/前台要回来**：放映时前台是 WPS，退出后如果不管，我们的窗口还是非前台，
        // 应用内快捷键（Ctrl+P 等）就一直是死的（用户 2026-09-30 实测："退出放映后 Ctrl+P
        // 也没用了"）。`SetKeyboardMode` 里那句 SetForegroundWindow 正是干这个的，
        // 顺手把窗口样式也重新落一遍（两种状态都是幂等的）。
        if (_windows.Count > 0) SetKeyboardMode(KeyboardMode);

        // 条的状态跟着收场：菜单/页号面板还开着、或者正被拿在手里的话，退出放映后
        // 它们就成了"看不见却还在吃输入"的孤儿（和界面"收起不清临时状态"是同一个坑）。
        PptMenuOpen = false;
        PptPagePanelOpen = false;
        PptClearConfirm = false;
        _pptHintUntilMs = 0;
        PptBarHover = -1;
        CancelPptPress();

        SaveAllPptPages();
        PptSlide = 0;
        PptTotal = 0;
        GotoPage(0);
        NotifyUiStateChanged();
        Console.WriteLine("[PPT] 退出放映，已回到桌面批注");
    }

    /// <summary>
    /// 切到某一页：**只动相机与脏区**。
    /// 页的内容由 `Doc` 的页槽管（切页 O(1)）；相机是"每页一个数"，
    /// 翻回某页时老师还停在原来滚到的位置（参考 ICC/InkClass"记住每页滚动位置"的语义）。
    /// </summary>
    private bool GotoPage(int key)
    {
        // 任何换页（翻页键 / 点箭头 / 页号面板 / 进退出放映）都先让回放退场：
        // 回放是"当前这一页的只读重演"，页一换它就失去意义（用户 2026-10-01 拍板）。
        ExitReplayForEdit("翻页");
        int from = Doc.PageKey;
        if (!Doc.SwitchPage(key)) return false;

        _pageScroll[from] = ViewOffsetY;
        ViewOffsetY = _pageScroll.TryGetValue(key, out var v) ? v : 0f;
        ClampViewOffset();
        _camAnimating = false;

        Doc.InvalidateAll();       // 内容全换：分块缓存必须整层作废（否则旧页像素会留在新页上）
        _dirty = true;
        return true;
    }

    // ---- 存取（参考 ICC：进入读回、退出写盘；我们多一条"翻页就先存上一页"）--------

    private void LoadPptPages()
    {
        if (_pptKey.Length == 0) return;
        if (!PptAutoSaveOn)
        {
            Console.WriteLine("[PPT] 自动保存关着：这次不读盘上的墨迹（盘上原样留着）");
            return;
        }
        int loaded = 0;
        try
        {
            foreach (int key in PptStore.ListPageKeys(_pptKey))
            {
                var blob = PptStore.LoadPage(_pptKey, key);
                if (blob == null) continue;
                var strokes = InkSerializer.LoadStrokes(blob, out int maxId);
                Doc.LoadPageContent(key, strokes, maxId);
                loaded++;
            }
        }
        catch (Exception ex)
        {
            // 读坏了只提示、照常进放映（批注读不回来比"功能不可用"轻得多）。
            Console.WriteLine("读 PPT 批注失败（当作没有）：" + ex.Message);
        }
        if (loaded > 0) Console.WriteLine($"[PPT] 读回 {loaded} 页批注");
    }

    /// <summary>把当前这一页写盘（键 0 是桌面页，不归它管）。</summary>
    private void SaveCurrentPptPage() => SavePageToDisk(Doc.PageKey);

    private void SavePageToDisk(int key)
    {
        if (key == 0 || _pptKey.Length == 0) return;
        if (!PptAutoSaveOn) return;              // 关着就一个字都不写（"不碰盘"的语义在这里兑现）
        var strokes = Doc.StrokesOf(key);
        if (strokes == null) return;
        try
        {
            // 空页**不写空文件**：把已有文件删掉就完了（同 InkClass 的 `length > 8` 那个判断）。
            // 不这么写的话，"清空"之后再退出放映会在盘上留一串 0 字节文件——下次读回来还是空的，
            // 但目录里一堆空文件很难看，也让"盘上还剩几页"这个诊断失去意义。
            if (strokes.Count > 0) PptStore.SavePage(_pptKey, key, InkSerializer.SaveStrokes(strokes));
            else PptStore.DeletePage(_pptKey, key);
        }
        catch (Exception ex) { Console.WriteLine("PPT 批注写盘失败：" + ex.Message); }
    }

    private void SaveAllPptPages()
    {
        if (_pptKey.Length == 0) return;
        int saved = 0;
        foreach (int key in Doc.PageKeys())
        {
            if (key == 0) continue;
            SavePageToDisk(key);
            saved++;
        }
        try { PptStore.SavePosition(_pptKey, PptSlide); } catch { }
        if (saved > 0) Console.WriteLine($"[PPT] 已保存 {saved} 页批注");
    }

    /// <summary>
    /// 丢掉这份演示文稿已存的所有页（自检/探针用；产品里没有"删除"这条路——
    /// 清空一页会把它存成空文件）。
    /// </summary>
    internal void ClearPptStoreForTest()
    {
        try
        {
            var dir = Path.Combine(PptStore.Root, _pptKey);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    // ---- 翻页命令（给底部那两条 + 长按菜单用）----------------------------------
    //
    // **命令走 PPT、不走我们**（参考 InkClass/ICC）：老师用遥控器翻页和点我们画的那两条
    // 是同一条路——页永远跟着 PPT 的放映状态走，不会出现"两套页号"打架。
    //
    // ⚠ 它们**不在 `IEngineCommands` 里**（界面契约）：底部那两条由引擎自己画自己命中
    // （见 PptBar.cs），所以界面不需要这三个入口。曾经给界面的白板格用过一版，
    // 用户 2026-09-26 更正"白板区不需要 ppt 翻页"之后就没有界面消费者了。

    internal void PptNextFromUi() => PostPptCommand(0);
    internal void PptPrevFromUi() => PostPptCommand(1);
    internal void PptExitFromUi() => PostPptCommand(2);

    private void PostPptCommand(byte cmd, int arg = 0)
    {
        if (_pptSource == null) return;
        if (_pptWatcher != null)
        {
            _pptWatcher.Post(cmd, arg);      // 产品：交给轮询线程执行（COM 只在那一线程上碰）
            return;
        }
        // 自检（假源）：直接执行 + 立刻走一步状态机，不等轮询。
        if (cmd == 0) _pptSource.Next();
        else if (cmd == 1) _pptSource.Prev();
        else if (cmd == 2) _pptSource.ExitShow();
        else if (cmd == 3) _pptSource.GotoSlide(arg);
        StepPpt();
    }

    // =================================================================================
    //  底部那一条（PptBar）：翻页 / 页码 / 菜单
    //
    //  用户 2026-09-26 拍板："模仿 Inkeys 显示在外部"（贴在 PPT 页面上、不跟工具条走），
    //  位置 = 默认屏幕左下角、可拖、位置落盘。
    //
    //  手势分工（三条互不抢，判据就是 PptBar.ZoneAt 一人一半的那几块）：
    //    · 箭头：**点一下 = 翻一页**；
    //    · 页码格：**点一下 = 菜单**（2026-10-02 第五轮；第一项「指定页码跳转」
    //      打开页号面板，点哪页跳哪页）、**按下后移动 = 拖条**；没有长按。
    //  几何在 PptBar.cs，绘制在 Overlay.DrawPptBar（浮层，和选中操作条同一套）。
    // =================================================================================

    // ---- 位置（可拖动 + 持久化）--------------------------------------------
    //
    // 用户 2026-09-26 定的两条：
    //   · 初始默认位置 = **屏幕左下角**（"因为 PPT 本来的工具就在这里"）；
    //   · **重启以后还在这个位置**（记住，但不碰 PPT 的任何设置，只记我们自己的）。

    /// <summary>条的左上角（**物理像素**）。NaN = 还没定过 → 用默认位置。</summary>
    private float _pptBarX = float.NaN, _pptBarY = float.NaN;

    /// <summary>位置存盘的键。存的是"相对屏幕左上角的偏移"（换分辨率不会跑到屏幕外），
    /// 放在界面偏好那本仓库里（引擎只存不解释，见 <see cref="GetUiPref"/>）。</summary>
    private const string PptBarXPref = "pptBarX";
    private const string PptBarYPref = "pptBarY";

    /// <summary>自检用：把位置清回"未定"，下一次问位置时重新用默认值（模拟刚开软件）。</summary>
    internal void ResetPptBarPosForTest() { _pptBarX = float.NaN; _pptBarY = float.NaN; }

    // ---- 交互状态 ------------------------------------------------------------

    /// <summary>菜单开着的（点菜单外 = 先收起、这一下照常往下走）。</summary>
    internal bool PptMenuOpen { get; private set; }

    /// <summary>页号面板开着的（从菜单第一项「指定页码跳转」进来，见 PptBar.PanelRect）。</summary>
    internal bool PptPagePanelOpen { get; private set; }

    /// <summary>悬停的块：100＋i = 菜单第 i 项，200＋i = 面板第 i 格，否则 = <see cref="PptBarZone"/> 的值；
    /// -1 = 没悬停。绘制拿它画高亮（和 `SelBarHover` 同一个套路）。</summary>
    internal int PptBarHover = -1;

    /// <summary>
    /// 正在拖这条（**按下之后移动够了就算**，不用先长按——用户 2026-09-26 定的
    /// "点中页码那一块直接拖动就能走"）。绘制据此把整条画成"拿在手里"的样子。
    /// </summary>
    internal bool PptBarDragging { get; private set; }

    private float _pptPressX, _pptPressY;       // 按下时的指针位置（判拖动阈值用）
    /// <summary>页码格被按下、还没决定是"单击"还是"拖动"。
    /// 2026-10-02 第五轮起**没有长按**了：没动过 = 单击（开菜单）、移动超阈值 = 拖条。</summary>
    private bool _pptPressed;
    private float _pptGrabDX, _pptGrabDY;       // 拿起那一刻指针相对条左上角的偏移（拖动手感）

    /// <summary>这一次按下被条吃掉了（引擎在 <c>OnPointerUp</c> 里据此把收尾交回来）。
    /// 少了它会漏掉 ReleaseCapture——那会让**整台机器的鼠标都挂在我们窗口上**。</summary>
    private bool _pptCapturing;

    // ---- 穿透模式下的"接输入小窗"（条那一块）-------------------------------
    //
    // 为什么需要它（用户 2026-09-27 报的："我在穿透模式下，PPT 翻页就不管用了"）：
    //   穿透是靠给覆盖层加 **WS_EX_TRANSPARENT** 实现的（见 Engine.ApplyPassThroughStyle），
    //   而这个样式让系统**跳过命中测试**——Engine.WndProc 的 WM_NCHITTEST 里那句
    //   "PPT 条也算我的地盘"根本执行不到，于是穿透下整条是死的（连悬停高亮都没有）。
    //   界面面板（方案 B）早就用"独立的 1/255 透明小窗 + HTCLIENT"解决过同一个问题，
    //   这里**参考它**，一块一块地补：条、以及长在条上方的菜单 / 页号面板。
    //
    // 为什么是三块合一：菜单和页号面板都长在条的正上方、且和条同宽，
    // 三块合起来正好是一个矩形（谁没开就退回谁那部分），所以**一个小窗够**。
    //
    // 为什么只在"放映 + 穿透"时显示：不穿透时覆盖层本来就能收消息（那条路已经验过），
    // 多一个小窗只会多一处"谁吃这一下"的歧义；不显示时必须**立刻收掉**，
    // 不然它会一直盖在屏幕上吃点击。

    private IntPtr _pptInputHwnd;
    private RectF _pptInputRect;
    private bool _pptInputShown;

    /// <summary>自检用：穿透那块接输入小窗这一刻铺着吗（用量它的矩形代替"点一下试试"）。</summary>
    internal bool PptInputWindowShown => _pptInputShown;

    /// <summary>自检用：接输入小窗这一刻的矩形（物理屏幕坐标）。没铺出来时返回空矩形。</summary>
    internal void PptInputWindowRect(out RectF rect) => rect = _pptInputRect;

    /// <summary>自检 / 出图用：把引导按下去（它每次进放映都出现，会和菜单压在同一处）。</summary>
    internal void PptHintSuppressForTest() => _pptHintUntilMs = 0;

    // ---- 菜单里的开关与确认 --------------------------------------------------

    /// <summary>
    /// 自动保存墨迹（**默认开**）。语义只有一条：
    /// **关 = 这次放映完全不碰盘上的东西**（不读也不写，盘上原样留着）。
    ///
    /// 为什么不做成"加载/保存"两个开关：那个组合里有一个**会丢东西**的档——
    /// "加载关 + 保存开"会把这节课的墨迹**按页覆盖**掉上节课的（键是 SlideID，同页就覆盖同页），
    /// 老师上节课的板书就没了。想永久清掉用「墨迹清空」，两件事各管各的。
    /// </summary>
    internal bool PptAutoSaveOn { get; private set; } = true;
    private const string PptAutoSavePref = "pptAutoSave";

    /// <summary>"墨迹清空"点过一次、正等着确认（3 秒内再点才真清）。</summary>
    internal bool PptClearConfirm { get; private set; }
    private double _pptClearConfirmAt;
    private const double PptConfirmMs = 3000;

    /// <summary>引导显示到什么时候（`NowMs` 基准，0 = 不显示）。
    /// 用户 2026-09-27 定："每次播放都要提示一次，大概一两秒钟吧"——
    /// **每次进放映**都显示，时长见 <see cref="PptHintMs"/>（不再记偏好，见 EnterPptMode）。</summary>
    private double _pptHintUntilMs;

    /// <summary>引导停留时长（毫秒）。1.5 秒：够看清"点页码：页码跳转菜单"这一行，
    /// 又不至于挡着讲课（用户要的是"一两秒钟"）。</summary>
    private const double PptHintMs = 1500;

    /// <summary>自检用 / 绘制用：这一刻该不该显示引导。</summary>
    internal bool PptHintVisible => PptMode && _pptHintUntilMs > 0 && NowMs < _pptHintUntilMs;

    /// <summary>
    /// 菜单的项。**只有这一张表**：绘制、命中、执行都读它——
    /// "同一个名单写在多处必漏一处"的教训见 架构-分层与规则.md 五-7。
    ///
    /// **一律六个字**。沿革：先是四项各不同（会左右参差），2026-09-26 用户先要"四字对齐"，
    /// 又把三条重新起名成"自动保存墨迹 / 清空所有墨迹 / 结束本次放映"——正好都是六字，
    /// 对齐照样成立，而多出来的那两个字把没说清的地方补齐了：
    /// **所有**（清的是这一份 PPT 的全部，不是当前页）、**自动**（不用手动存）、
    /// **本次**（结束的是这一场放映，不是改 PPT 本身）。
    /// 2026-10-01 用户提议加上"回放本页墨迹"。**2026-10-02 第五轮**：取消长按入口，
    /// 改成"点页码 = 菜单"，并把原来的页号面板降为第一项"**指定页码跳转**"——
    /// 这样"跳页"和"退出"一个点击就都看得见，不再依赖隐藏手势。
    /// 分组 = [跳转] | [存/放] | [清空] | [结束]。
    /// </summary>
    private static readonly string[] PptMenuItems =
        { "指定页码跳转", "自动保存墨迹", "回放本页墨迹", "清空所有墨迹", "结束本次放映" };

    internal static int PptMenuItemCount => PptMenuItems.Length;

    /// <summary>菜单项之间画分隔线的位置（在第 i 项**之前**画）。[跳转] | [存/放] | [清空] | [结束]。</summary>
    internal static bool PptMenuDividerBefore(int i) => i is 1 or 3 or 4;

    /// <summary>这一项是危险动作吗（"墨迹清空"）——绘制据此上强调色（等确认时也用它）。</summary>
    internal static bool PptMenuItemDanger(int i) => i == 3;

    /// <summary>第 i 项这一刻显示的文字（"清空"等确认时变成"再点确认"）。文档模式另有文案。</summary>
    internal string PptMenuItemText(int i)
    {
        if (!PptMode && DocView.IsOpen)
        {
            return i switch
            {
                0 => "指定页码跳转",
                1 => "自动保存墨迹",
                2 => "回放这一屏墨迹",
                3 => PptClearConfirm ? "再点确认" : "清空文档墨迹",
                4 => "关闭文档",
                _ => "",
            };
        }
        return i == 3 && PptClearConfirm ? "再点确认" : PptMenuItems[i];
    }

    /// <summary>第 i 项右边的状态文字（开关 = 开/关；回放 = N 笔）。其余返回 null。</summary>
    internal string PptMenuItemStatus(int i)
        => i == 1 ? (PptAutoSaveOn ? "开" : "关")
         : i == 2 ? $"{Doc.Strokes.Count} 笔"
         : null;

    /// <summary>这一项这一刻能不能点。"回放本页墨迹"没笔迹时**置灰**——
    /// 点是老师最自然的动作，"点了没反应"最伤人（右邻的"0 笔"顺便把原因说清）。</summary>
    internal bool PptMenuItemEnabled(int i) => i != 2 || Doc.Strokes.Count > 0;

    /// <summary>虚拟桌面矩形（物理像素）。</summary>
    private RectF ScreenRectPhysical() => new()
    {
        MinX = _virtualX, MinY = _virtualY,
        MaxX = _virtualX + _virtualW, MaxY = _virtualY + _virtualH,
    };

    /// <summary>两个矩形相交吗（避让界面用）。</summary>
    private static bool RectsOverlap(in RectF a, in RectF b)
        => a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;

    /// <summary>
    /// 界面矩形是不是"**全屏模态**"（盖住屏幕 80% 宽和 80% 高）。
    ///
    /// 为什么要有它：`PptBarRect` 第一次定位时会避让工具条（界面和条重叠就把条往上挪），
    /// 而「更多」面板打开时界面占的是**整块屏幕**——照常避让就会把条顶到屏幕顶部。
    ///
    /// 为什么用"矩形大小"而不是问"面板开没开"：引擎不认识界面的内部状态，它只看到
    /// `QueryBounds`（分层纪律，见 架构-分层与规则.md）。全屏模态在矩形上就是这个特征，
    /// 从矩形判最解耦。阈值 80% 很宽松：工具条再宽也只是贴底的一条（高度远小于 80%），
    /// 正常界面不会被误判。
    /// </summary>
    internal static bool UiLooksFullscreen(in RectF ui, in RectF screen)
    {
        float sw = screen.MaxX - screen.MinX, sh = screen.MaxY - screen.MinY;
        if (sw <= 0f || sh <= 0f) return false;
        return (ui.MaxX - ui.MinX) >= sw * 0.8f && (ui.MaxY - ui.MinY) >= sh * 0.8f;
    }

    /// <summary>界面的矩形（物理像素）。界面没挂 / 不可见 / 报错时是空矩形。</summary>
    private RectF UiRectPhysical()
    {
        if (!UiVisibleNow) return RectF.Empty;
        RectF r = RectF.Empty;
        if (!UiGuard("QueryBounds", () => { r = Ui.QueryBounds(); return true; }, false))
            return RectF.Empty;
        if (r.IsEmpty) return RectF.Empty;
        return new RectF
        {
            MinX = r.MinX * DpiScale, MinY = r.MinY * DpiScale,
            MaxX = r.MaxX * DpiScale, MaxY = r.MaxY * DpiScale,
        };
    }

    /// <summary>
    /// 条的矩形（物理像素）。位置：**记住的**优先（拖动后存进偏好），
    /// 没有就取默认（**屏幕左下角**，用户 2026-09-26 定的"因为 PPT 本来的工具就在这里"），
    /// 最后一律**夹进屏幕**——拖出去找不回来是这个功能最坏的失败模式。
    ///
    /// 避让界面（工具条）：**只在"还没被用户拖过"时**做一次。用户亲手放的位置是他的决定，
    /// 我们不再擅自挪（那会变成"我放好了它自己跑"）。
    /// </summary>
    internal RectF PptBarRect()
    {
        var screen = ScreenRectPhysical();
        float dpi = DpiScale;

        bool first = float.IsNaN(_pptBarX);
        if (first)
        {
            var d = PptBar.DefaultTopLeft(screen, dpi);
            _pptBarX = d.X; _pptBarY = d.Y;
        }
        var p = PptBar.ClampTopLeft(_pptBarX, _pptBarY, screen, dpi);

        if (first)
        {
            var ui = UiRectPhysical();
            // **全屏模态面板**（「更多」打开时占用 = 整块屏幕）不算"工具条"，不参与避让。
            // 不判这一条的下场（2026-10-01 `--ppttest` 当场抓到）：面板开着时条第一次定位
            // 落在左上角 (32,32)——`above = ui.MinY - 8 - 条高` 直接算到屏幕顶上去了，
            // 而默认位置是左下角。用户实测"先放映、再开面板"看不到这条，因为条在放映
            // 第一帧就定位完了；真正的危险顺序是"面板先开着、PptMode 才打开"。
            if (!ui.IsEmpty && !UiLooksFullscreen(ui, screen) && RectsOverlap(PptBar.RectAt(p.X, p.Y, dpi), ui))
            {
                // 界面（工具条）也在底部那一带：上移到它上面，留 8 不重叠。
                float above = ui.MinY - 8f * dpi - PptBar.BarH * dpi;
                p = PptBar.ClampTopLeft(p.X, above, screen, dpi);
            }
        }

        _pptBarX = p.X; _pptBarY = p.Y;
        return PptBar.RectAt(p.X, p.Y, dpi);
    }

    /// <summary>菜单矩形。</summary>
    internal void PptMenuRect(out RectF rect)
        => rect = PptBar.MenuRect(PptBarRect(), PptMenuItemCount, DpiScale, ScreenRectPhysical());

    /// <summary>菜单里第 <paramref name="index"/> 行的矩形。**绘制、命中、悬停都读它**
    /// （三处各算一遍的话迟早差几个像素——"看得见却点不中"就是这么来的）。</summary>
    internal void PptMenuItemRectAt(int index, out RectF rect)
    {
        PptMenuRect(out var menu);
        rect = PptBar.MenuItemRect(menu, index, DpiScale);
    }

    /// <summary>页号面板矩形（点页码弹出来的那张）。</summary>
    internal void PptPanelRect(out RectF rect)
        => rect = PptBar.PanelRect(PptBarRect(), Math.Max(1, BarTotal), DpiScale, ScreenRectPhysical());

    /// <summary>面板里第 <paramref name="index"/> 个格子的矩形（index 从 0 起 = 第 1 页）。</summary>
    internal void PptPanelCellRectAt(int index, out RectF rect)
    {
        PptPanelRect(out var panel);
        rect = PptBar.CellRect(panel, index, DpiScale);
    }

    /// <summary>这一块归不归底部条（**穿透豁免**与命中都要用同一份判据——
    /// "看得见的一块"和"点得到的一块"必须是同一个）。PPT 放映和文档模式共用这一条。</summary>
    internal bool PptBarContains(float x, float y)
    {
        if (!PageBarActive) return false;
        if (PptBarRect().Contains(x, y)) return true;
        if (PptMenuOpen) { PptMenuRect(out var menu); if (menu.Contains(x, y)) return true; }
        if (PptPagePanelOpen) { PptPanelRect(out var panel); if (panel.Contains(x, y)) return true; }
        return false;
    }

    /// <summary>
    /// 按下：**返回 true = 这一下归它**（引擎不再当笔画处理）。
    ///
    /// 判定顺序（三个手势的分工，写死在这里）：
    ///   ① 菜单 → 面板（先问最上面那两个浮层）；
    ///   ② 箭头：**按下即翻页**（最高频操作，不给它延迟）；
    ///   ③ 页码格：**先只记录，不下结论**——没动过 = 单击（开菜单）、
    ///      移动超阈值 = 拖条（见 <see cref="PptBarPointerMove"/> 与
    ///      <see cref="PptBarPointerUp"/>）。**长按自 2026-10-02 第五轮起不是入口了**。
    /// </summary>
    internal bool PptBarPointerDown(float x, float y)
    {
        if (!PageBarActive) return false;

        // 菜单开着：命中就执行并吃掉；没命中就收起来，**这一下照常往下走**
        //（和颜色/层级面板"点外面先收起来再照常"是同一条口径）。
        if (PptMenuOpen)
        {
            for (int i = 0; i < PptMenuItemCount; i++)
            {
                PptMenuItemRectAt(i, out var item);
                if (item.Contains(x, y))
                {
                    // 置灰的项：**这一下吃掉，但不执行**（菜单留着，右侧"0 笔"就是原因）
                    if (PptMenuItemEnabled(i)) RunPptMenuItem(i);
                    _dirty = true;
                    return true;
                }
            }
            PptMenuOpen = false;
            PptClearConfirm = false;               // 菜单关了，"等确认"也一起放掉
            _dirty = true;
            // 点在页码格上 = "再点一次收起"：吃掉它，别再打开（不收就成了点不掉的鬼打墙）
            if (PptBar.ZoneAt(PptBarRect(), x, y, DpiScale) == PptBarZone.Page) return true;
        }

        // 页号面板开着
        if (PptPagePanelOpen)
        {
            PptPanelRect(out var panel);
            if (panel.Contains(x, y))
            {
                int total = Math.Max(1, BarTotal);
                for (int i = 0; i < total; i++)
                {
                    PptPanelCellRectAt(i, out var cell);
                    if (!cell.Contains(x, y)) continue;
                    int page = i + 1;
                    if (page != BarPageNow) BarGoto(page);   // 文档：跳页顶；PPT：命令它跳页
                    PptPagePanelOpen = false;
                    _dirty = true;
                    return true;
                }
                return true;                       // 面板里的空白：吃掉，别穿透到画布
            }

            // 点在面板外：先收起来
            PptPagePanelOpen = false;
            _dirty = true;
            // 而如果这一下正落在页码格上，那就是"再点一次收起"——**吃掉它，别再弹**
            //（不收的话就成了"点一下收起、松手又打开"的鬼打墙）。
            if (PptBar.ZoneAt(PptBarRect(), x, y, DpiScale) == PptBarZone.Page) return true;
        }

        var bar = PptBarRect();
        var zone = PptBar.ZoneAt(bar, x, y, DpiScale);
        if (zone == PptBarZone.None) return false;

        if (zone == PptBarZone.LeftArrow) { BarPrev(); return true; }
        if (zone == PptBarZone.RightArrow) { BarNext(); return true; }

        // 页码格：**先只记录，松手再决定**——没动过 = 单击（开菜单）；
        // 移动超阈值 = 拖条（见 PptBarPointerMove / PptBarPointerUp）。
        // 长按（600ms）自 2026-10-02 第五轮起**不再是入口**：点一下就把菜单摊开，
        // "长按"整段空出来留给"功能提示"的触摸长按。
        _pptPressX = x; _pptPressY = y;
        _pptPressed = true;
        _dirty = true;
        return true;
    }

    /// <summary>移动：返回 true = 这一下归它（拖动中 / 悬停在条上）。</summary>
    internal bool PptBarPointerMove(float x, float y)
    {
        if (!PageBarActive) return false;

        // ---- 按下之后移动够了 = **直接拖动**（不用先长按——用户 2026-09-26 定的
        // "点中页码那一块直接拖动就能走"）。这正是"点击 vs 拖动"的
        // 通用语言（Excel 调列宽、网页拖滑块都这么分）：拖动**不用等那 600ms**，
        // 而单击回归本职——松手弹菜单（2026-10-02 第五轮起，见 PptBarPointerUp）。
        float moved = Math.Abs(x - _pptPressX) + Math.Abs(y - _pptPressY);
        if (_pptPressed && !PptBarDragging && moved > PptBar.DragSlop * DpiScale)
        {
            // 拖起来了就不再是"单击"：把按下标记清掉（松手时不会再弹菜单）。
            _pptPressed = false;
            PptBarDragging = true;
            // 抓起那一刻记下"指针相对条左上角的偏移"：拖动时条才抓在指针原来按的那个点上
            var b0 = PptBarRect();
            _pptGrabDX = _pptPressX - b0.MinX;
            _pptGrabDY = _pptPressY - b0.MinY;
        }

        // ---- 拖动中：条跟着指针走 ----
        if (PptBarDragging)
        {
            var sp = PptBar.ClampTopLeft(x - _pptGrabDX, y - _pptGrabDY, ScreenRectPhysical(), DpiScale);
            _pptBarX = sp.X; _pptBarY = sp.Y;
            _dirty = true;
            return true;                           // 拖动中吃掉所有移动，别去落墨
        }

        // ---- 悬停高亮（给绘制用）----
        // **顺序与按下分派一致：菜单 → 面板 → 条**（见 PptBarPointerDown 的判定顺序）。
        // 页数多、面板被夹到屏幕顶时，面板底部会压到条上——那时"看得见的格子"必须
        // 比条优先，否则就是"格子悬停不亮、按下去却真跳页"。
        //（2026-09-27 自检复现过：400 页时第 171 格 hover 被条抢成 1，期望 370。）
        int hover = -1;
        if (PptMenuOpen)
        {
            for (int i = 0; i < PptMenuItemCount; i++)
            {
                PptMenuItemRectAt(i, out var item);
                // 置灰的项不给悬停高亮（高亮 = "能点"，点了没反应就更奇怪）
                if (item.Contains(x, y)) { hover = PptMenuItemEnabled(i) ? 100 + i : -1; break; }
            }
        }
        if (hover < 0 && PptPagePanelOpen)
        {
            int total = Math.Max(1, BarTotal);
            for (int i = 0; i < total; i++)
            {
                PptPanelCellRectAt(i, out var cell);
                if (cell.Contains(x, y)) { hover = 200 + i; break; }
            }
        }
        if (hover < 0)
        {
            var zone = PptBar.ZoneAt(PptBarRect(), x, y, DpiScale);
            if (zone != PptBarZone.None) hover = (int)zone;
        }
        if (hover != PptBarHover) { PptBarHover = hover; _dirty = true; }

        return PptBarHover >= 0;
    }

    /// <summary>
    /// 抬起。松手这一刻才决定"这一次按下"到底是什么：
    ///   · 拖过 → **放下**（存位置，重启还在）；
    ///   · 没动过 + 在页码格上 → **弹菜单**（2026-10-02 第五轮；菜单第一项 = 指定页码跳转）。
    /// </summary>
    internal void PptBarPointerUp(float x, float y)
    {
        if (!PageBarActive) return;

        if (PptBarDragging)
        {
            var p = PptBar.ClampTopLeft(_pptBarX, _pptBarY, ScreenRectPhysical(), DpiScale);
            _pptBarX = p.X; _pptBarY = p.Y;
            SavePptBarPos();                       // 记住（下次打开还在）
            PptBarDragging = false;
            _dirty = true;
            Console.WriteLine($"[PPT] 条已挪到 ({p.X / DpiScale:F0}, {p.Y / DpiScale:F0}) 逻辑像素");
            return;
        }

        // 单击（没拖动过）：只有"在页码格上松手"才算数——**页码格单击 = 开菜单**
        //（2026-10-02 第五轮；箭头在按下那一刻就翻过页了，走不到这里）。
        // 页号面板从此是菜单第一项「指定页码跳转」，不再直接挂在单击上。
        if (_pptPressed)
        {
            _pptPressed = false;
            if (PptBar.ZoneAt(PptBarRect(), x, y, DpiScale) == PptBarZone.Page)
            {
                PptMenuOpen = true;
                PptPagePanelOpen = false;
                PptClearConfirm = false;
                _dirty = true;
                Console.WriteLine($"[{(PptMode ? "PPT" : "文档")}] 页码菜单打开（共 {BarTotal} 页）");
            }
        }
    }

    /// <summary>取消这一次"按下"（指针丢了 / 退出放映）：按下标记和拖动状态一起清掉。</summary>
    private void CancelPptPress()
    {
        if (!_pptPressed && !PptBarDragging) return;
        _pptPressed = false;
        PptBarDragging = false;
        _dirty = true;
    }

    /// <summary>
    /// 每帧推进（2026-10-02 第五轮起**没有长按**了）：只做两件到点的事——
    /// 引导卡过期、清空"再点确认"过期。接输入小窗的同步也在这里。
    /// </summary>
    internal void StepPptBar()
    {
        // 穿透那块"接输入小窗"每帧对一次（该显示时铺上、不该显示时收掉）。
        // 放在 `!PageBarActive` 早退**之前**：退出放映也要能看到"该收了"。
        SyncPptInputWindow();

        if (!PageBarActive) return;

        // 引导到点就擦掉（它只显示那么一两秒）
        if (_pptHintUntilMs > 0 && NowMs >= _pptHintUntilMs) { _pptHintUntilMs = 0; _dirty = true; }

        // "再点确认"超时就复原（不然菜单会一直挂着一个"确认中"的状态）
        if (PptClearConfirm && NowMs - _pptClearConfirmAt > PptConfirmMs)
        {
            PptClearConfirm = false;
            _dirty = true;
        }
    }

    // =====================================================================
    //  穿透模式下"PPT 条那一块"的接输入小窗
    //
    //  背景见 `_pptInputHwnd` 那一段注释：穿透给覆盖层加了 WS_EX_TRANSPARENT，
    //  系统**跳过命中测试**，于是条在穿透下彻底是死的。这里参考界面面板的"方案 B"
    //  （独立的 1/255 透明小窗）：它在命中测试里是实实在在的一块，消息直接进
    //  `PptInputWndProc`，喂给 PptBar 那三个手势。
    // =====================================================================

    /// <summary>
    /// 同步小窗：需要时铺在"条 ∪ 菜单 ∪ 页号面板"上，不需要时收掉。
    /// 每帧调一次（见 <see cref="StepPptBar"/>），有变化才真的动窗口。
    /// </summary>
    private void SyncPptInputWindow()
    {
        if (!PptMode || !PassThrough) { HidePptInputWindow(); return; }

        EnsurePptInputWindow();
        if (_pptInputHwnd == IntPtr.Zero) return;

        // 三块并集（都是**物理屏幕坐标**）：菜单/页号面板长在条的上方、和条同宽，
        // 合起来仍是一个矩形。没开的那两块自然不参与。
        var box = RectF.Empty;
        box.Add(PptBarRect());
        if (PptMenuOpen) { PptMenuRect(out var menu); box.Add(menu); }
        if (PptPagePanelOpen) { PptPanelRect(out var panel); box.Add(panel); }

        if (_pptInputShown && box.Equals(_pptInputRect)) return;   // 位置没变：不动窗口

        Native.SetWindowPos(_pptInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(box.MinX), (int)MathF.Floor(box.MinY),
            Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX)),
            Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY)),
            Native.SWP_NOACTIVATE | (_pptInputShown ? 0 : Native.SWP_SHOWWINDOW));
        _pptInputRect = box;
        _pptInputShown = true;
    }

    private void HidePptInputWindow()
    {
        if (_pptInputHwnd != IntPtr.Zero && _pptInputShown)
        {
            Native.ShowWindow(_pptInputHwnd, Native.SW_HIDE);
            // 收掉窗口时系统会自己收走捕获，这里把状态对上，免得下次按下被当成"接着上次拖"
            _pptCapturing = false;
            PptBarDragging = false;
        }
        _pptInputShown = false;
    }

    private void EnsurePptInputWindow()
    {
        if (_pptInputHwnd != IntPtr.Zero) return;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _pptInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachPptInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_pptInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("PPT 条接输入小窗创建失败: " + Marshal.GetLastWin32Error()
                              + "（穿透模式下 PPT 条会点不动）");
            return;
        }

        // 1/255 的不透明度：肉眼看不见，但对命中测试来说它**实实在在地在这**（同界面那块）。
        Native.SetLayeredWindowAttributes(_pptInputHwnd, 0, 1, Native.LWA_ALPHA);
        Native.DisableSystemPressAndHold(_pptInputHwnd);
    }

    /// <summary>
    /// 小窗的消息。**只喂 PptBar 那三个手势**（按下 / 移动 / 抬起），不喂界面
    /// ——界面有它自己那块小窗（见 Engine.UiInputWndProc）。
    ///
    /// 坐标口径和覆盖层那条路**逐句对齐**（容易错，所以照抄 Engine.OnPointerDown/Move/Up
    /// 里 PPT 那一段）：
    ///   · `PptBar*` 收的是**物理屏幕坐标**（条的位置按屏幕算的）；
    ///   · `PointerX/PointerY` 要的是**画布坐标**（光标 / 落点反馈读它）。
    /// 两条路各写一遍最容易出现"穿透下点得中、但光标和落点偏一块"。
    /// </summary>
    private IntPtr PptInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 指针在屏幕上的位置 → 画布坐标（和 OnPointerMove 同一句）
        void SetPointerCanvas(float sx, float sy)
        {
            float cx = sx, cy = sy;
            ScreenToCanvas(ref cx, ref cy);
            PointerX = cx; PointerY = cy; PointerInside = true;
        }

        switch (msg)
        {
            // 这块就是条本身，不需要再判断"落在条里的哪个位置"（同界面那块小窗）。
            case Native.WM_NCHITTEST:
                _cntNcHitTest++; _cntNcHitClient++;
                return new IntPtr(Native.HTCLIENT);

            // 点条不许把下层程序的焦点抢走（老师点一下翻页，PPT 还得是前台）。
            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            // 光标：和覆盖层那条路**问同一份判据**（指针在条上 = 箭头）。
            // 不答这一条的话系统会拿**类光标**兜底，而类光标是 NULL——
            // 表现就是"指针进了条里却顶着上一步的光标（笔 / 圆圈）"，
            // 或者干脆一个转圈（Engine.WndProc 的 WM_SETCURSOR 那条注释记着这个坑）。
            case Native.WM_SETCURSOR:
                ApplyCursor(force: true);
                return new IntPtr(1);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntDown++;
                LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                if (PptBarPointerDown(sx, sy))
                {
                    _drawing = false;
                    _pptCapturing = true;        // 这一次归它：松手时由它收尾
                    Native.SetCapture(hWnd);     // 拖动要持续收到消息（会拖到小窗之外）
                }
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntMove++;
                LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                // 悬停高亮 / 拖动跟手都在这一个函数里（它自己按 `_pptPressAtMs` 分辨）
                PptBarPointerMove(sx, sy);
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out _, out _, out _))
                {
                    StampInput(); _cntUp++;
                    SetPointerCanvas(sx, sy);
                    if (_pptCapturing)
                    {
                        _pptCapturing = false;
                        PptBarPointerUp(sx, sy);
                    }
                }
                // **必须无条件放开捕获**（按下那一刻 SetCapture 过），而且要在
                // `if (ReadPointer)` **之外**：ReadPointer 偶尔会失败（老指针 / 合成事件），
                // 放在里面的话那一次就漏掉释放——后果不是"按钮卡住"，而是**整台机器的
                // 鼠标事件都还挂在我们这个 1/255 透明的小窗上**（同 Engine.OnPointerUp 的注释）。
                Native.ReleaseCapture();
                _drawing = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            // 捕获被别人抢走（窗口被藏、被顶掉）：状态跟着放掉，别留下"一直按着"。
            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                _pptCapturing = false;
                _dirty = true;
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;    // 它不画任何东西，别的消息一概不问
        }
    }

    /// <summary>
    /// 执行菜单第 <paramref name="index"/> 项。
    /// 两处**故意不关菜单**：
    ///   · 开关项（墨迹保存）——老师点一下要**看见状态变了**，菜单关了就等于没反馈；
    ///   · "墨迹清空"的第一次点——那一下只是"进入等确认"，菜单得留着他点第二次。
    /// 其余（回放 / 结束）都是**先收菜单再执行**。
    /// </summary>
    private void RunPptMenuItem(int index)
    {
        // 文档模式：同一套菜单壳，五项按文档的语义执行（见 RunDocBarMenuItem）。
        if (!PptMode && DocView.IsOpen) { RunDocBarMenuItem(index); return; }

        switch (index)
        {
            case 0:                                  // 指定页码跳转（2026-10-02 第五轮）
                PptMenuOpen = false;
                PptClearConfirm = false;             // 菜单关了，"等确认"一起放掉
                PptPagePanelOpen = true;             // 原来的"点页码直接出面板"降到这里
                _dirty = true;
                Console.WriteLine($"[PPT] 页号面板打开（共 {PptTotal} 页）");
                return;
            case 1:                                  // 墨迹保存（开关）
                TogglePptAutoSave();
                return;                              // 菜单留着，让老师看见"开 → 关"
            case 2:                                  // 回放本页墨迹（收起菜单，起当前页回放）
                PptMenuOpen = false;
                PptClearConfirm = false;             // 菜单关了，"等确认"一起放掉
                _dirty = true;
                StartReplay();                       // 与中央面板「墨迹回放」同一个入口
                return;
            case 3:                                  // 墨迹清空（两段确认）
                if (!PptClearConfirm)
                {
                    PptClearConfirm = true;
                    _pptClearConfirmAt = NowMs;
                    _dirty = true;
                    Console.WriteLine("[PPT] 再点一次「再点确认」才真的清空");
                    return;                              // 菜单留着
                }
                PptClearConfirm = false;
                ClearAllPptMarks();
                PptMenuOpen = false;
                _dirty = true;
                return;
            case 4:                                  // 结束放映
                PptMenuOpen = false;
                PptExitFromUi();
                return;
        }
    }

    /// <summary>「墨迹保存」开关：翻了就存偏好（**开是默认值，所以"开"就不记**——配置里只留和默认不一样的）。
    /// PPT 和文档**共用一个开关**：老师心里"自动保存"就是一件事。</summary>
    private void TogglePptAutoSave()
    {
        PptAutoSaveOn = !PptAutoSaveOn;
        SetUiPref(PptAutoSavePref, PptAutoSaveOn ? null : "0");
        _dirty = true;
        Console.WriteLine($"[{(PptMode ? "PPT" : "文档")}] 自动保存墨迹 → {(PptAutoSaveOn ? "开" : "关")}");
    }

    /// <summary>
    /// 文档模式的菜单五项（*同一套壳、换名换动作*，2026-10-07 用户定；见 计划-文档模式-状态模型.md §2）：
    ///   0 指定页码跳转 / 1 自动保存墨迹 / 2 回放这一屏墨迹 / 3 清空文档墨迹（两段确认）/ 4 关闭文档。
    /// 两处**故意不关菜单**的理由同 PPT 版（开关要看状态变化；清空要等第二次点）。
    /// </summary>
    private void RunDocBarMenuItem(int index)
    {
        switch (index)
        {
            case 0:
                PptMenuOpen = false;
                PptClearConfirm = false;
                PptPagePanelOpen = true;
                _dirty = true;
                Console.WriteLine($"[文档] 页号面板打开（共 {DocView.Count} 页）");
                return;
            case 1:
                TogglePptAutoSave();
                return;                              // 菜单留着，让老师看见"开 → 关"
            case 2:
                PptMenuOpen = false;
                PptClearConfirm = false;
                _dirty = true;
                StartReplay();                       // 与中央面板「墨迹回放」同一个入口
                return;
            case 3:
                if (!PptClearConfirm)
                {
                    PptClearConfirm = true;
                    _pptClearConfirmAt = NowMs;
                    _dirty = true;
                    Console.WriteLine("[文档] 再点一次「再点确认」才真的清空");
                    return;                          // 菜单留着
                }
                PptClearConfirm = false;
                ClearDocMarks();
                PptMenuOpen = false;
                _dirty = true;
                return;
            case 4:
                PptMenuOpen = false;
                CloseDocumentFromUi();
                return;
        }
    }

    /// <summary>
    /// 「墨迹清空」：把**这份演示文稿的批注**全部清掉（内存 ＋ 盘）。
    ///
    /// 为什么必须**连盘一起删**：墨迹是进放映时从盘读进内存的，只清内存的话——
    /// 自动保存开着时会被"写空内容"顺带删掉（碰巧对），**关着时盘上原样留着、下次又冒出来**
    ///（老师："我明明清了啊？"）。所以走显式删盘，不依赖保存那条路。
    ///
    /// **不动桌面批注**（0 号页）：那是另一回事，清课件的批注不能顺手抹掉老师的桌面板书。
    /// 也**不动 `Position`**（"上次播到第几页"不是墨迹，下次接着讲还有用）。
    /// </summary>
    private void ClearAllPptMarks()
    {
        if (_pptKey.Length == 0) return;
        int pages = 0;
        foreach (int key in Doc.PageKeys())
        {
            if (key == 0) continue;
            // 走 Doc 的正式入口：它会 Release 掉旧的几何缓存、清空间索引、并把当前页标脏
            //（"把字典一丢了事"会漏掉这些，表现是"清了、但屏幕上还留着一屏墨"）。
            Doc.LoadPageContent(key, new List<Stroke>(), 0);
            pages++;
        }
        PptStore.DeleteAll(_pptKey);
        Doc.InvalidateAll();
        _dirty = true;
        Console.WriteLine($"[PPT] 已清空这份演示文稿的批注（{pages} 页，含盘上的文件）");
    }

    // ---- 位置的持久化 ---------------------------------------------------------
    //
    // 存的是**相对屏幕左上角的偏移**（逻辑像素）：老师换分辨率、换显示器之后，
    // 位置还在相对差不多的地儿，而且读回来一律夹进屏幕（见 PptBar.ClampTopLeft），
    // 不会出现"换了小屏、条跑到屏幕外找不回来"。

    private void SavePptBarPos()
    {
        if (float.IsNaN(_pptBarX)) return;
        var screen = ScreenRectPhysical();
        SetUiPref(PptBarXPref, ((_pptBarX - screen.MinX) / DpiScale)
            .ToString("F1", CultureInfo.InvariantCulture));
        SetUiPref(PptBarYPref, ((_pptBarY - screen.MinY) / DpiScale)
            .ToString("F1", CultureInfo.InvariantCulture));
    }

    /// <summary>读位置。没有 / 读坏了都退回默认（**坏配置不许让程序起不来**，
    /// 所以这里只静默忽略，不抛）。</summary>
    private void LoadPptBarPos()
    {
        var sx = GetUiPref(PptBarXPref);
        var sy = GetUiPref(PptBarYPref);
        if (sx == null || sy == null) return;
        if (!float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return;
        if (!float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return;
        var screen = ScreenRectPhysical();
        _pptBarX = screen.MinX + x * DpiScale;
        _pptBarY = screen.MinY + y * DpiScale;
    }
}