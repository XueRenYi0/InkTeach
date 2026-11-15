using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;

public partial class InkEngine
{
    // ==================================================================
    //  图库（"我的图形"）：把选中的对象存起来，之后从图形面板最后那一段取回来
    //
    //  口径与取舍见 计划-图形工具.md §41 和 ShapeLibrary 的注释；这里只写引擎这一侧：
    //    · 保存：选中 → 一个文件（磁盘那点事全在 ShapeLibrary 里）；
    //    · 面板开合 + 重读目录；
    //    · 落笔插入：点一张 → 待插入 → 画布上按下就落在那儿。**和"粘贴对象"同一条路**
    //      （平移对齐落点、进文档一步撤销、插入后自动选中）——不另造一套。
    // ==================================================================

    /// <summary>收藏动作的即时反馈（图标换星/对勾；2026-10-25 第一批）。</summary>
    internal enum LibraryFeedbackKind
    {
        None = 0,
        Success,
        Duplicate,
        Error,
    }

    /// <summary>图库面板开着吗（引擎侧浮动面板，和颜色 / 层级面板同一套画法）。</summary>
    internal bool LibraryPanelOpen;
    /// <summary>统一面板当前页：图形 / 我的收藏。默认收藏，图形入口会切过来。</summary>
    internal LibraryTab LibraryTab = LibraryTab.Favorites;
    /// <summary>收藏动作这一刻的反馈（成功 / 重复 / 失败）；<see cref="LibraryFeedbackUntilMs"/> 到点自动回普通态。</summary>
    internal LibraryFeedbackKind LibraryFeedback;
    internal double LibraryFeedbackUntilMs;
    internal string LibraryFeedbackCategory = "";
    internal bool LibraryFeedbackActive => NowMs < LibraryFeedbackUntilMs;
    /// <summary>上一次收藏/重复命中时选中的那批对象；用来让"已收藏"的对勾**留在条上**。</summary>
    private Stroke[] _librarySavedSelection;

    /// <summary>后台算完、等主循环回 UI 线程执行的小任务（图片美化等）。</summary>
    private readonly ConcurrentQueue<Action> _uiTasks = new();
    private bool _beautifyBusy;
    internal bool BeautifyBusy => _beautifyBusy;

    /// <summary>选中的是不是全是已经美化过的图片（操作条上那颗星光点亮用）。</summary>
    internal bool SelectionAllImagesBeautified
    {
        get
        {
            if (Doc.Selected.Count == 0) return false;
            foreach (var s in Doc.Selected)
                if (!s.IsImage || s.Image == null || !s.Image.Beautified) return false;
            return true;
        }
    }

    /// <summary>当前选中的还是不是"上一次收进去的那批"（只比引用，不读盘）。</summary>
    internal bool SelectionInLibrary
    {
        get
        {
            if (_librarySavedSelection == null || _librarySavedSelection.Length != Doc.Selected.Count)
                return false;
            for (int i = 0; i < _librarySavedSelection.Length; i++)
                if (!ReferenceEquals(_librarySavedSelection[i], Doc.Selected[i])) return false;
            return true;
        }
    }
    /// <summary>图库面板里的条目。打开面板时读一次，存 / 删之后重读——不做增量更新（条目很少）。</summary>
    internal List<ShapeLibrary.Entry> LibraryEntries = new();
    /// <summary>未筛选的全部条目（分类计数、切分类都从这里来）。</summary>
    internal List<ShapeLibrary.Entry> LibraryAllEntries = new();
    /// <summary>当前收藏筛选：`"all"` = 全部；其他是分类 id。</summary>
    internal string LibraryCategoryId = "all";
    /// <summary>图形页当前分类：all / common / curve / solid（与收藏分类互不影响）。</summary>
    internal string ShapeCategoryId = "all";
    /// <summary>分类偏好是否已经从盘里恢复过（只恢复一次，不能在每次 ReloadLibrary 时覆盖用户当前选择）。</summary>
    private bool _libraryCategoryRestored;
    /// <summary>图形卡悬停（-1 = 没在卡上）；只在图形页用。</summary>
    internal int ShapeHoverIndex = -1;
    /// <summary>触摸点卡后短暂显示名称（没有 hover 的替代）。</summary>
    internal int ShapeNameIndex = -1;
    internal double ShapeNameUntilMs;
    /// <summary>下一次收藏动作落到哪个分类（「全部」筛选时不改它）。</summary>
    internal string LibrarySaveCategoryId = ShapeLibrary.Uncategorized;
    /// <summary>分类表缓存（打开面板/建删分类时刷新）。</summary>
    internal List<ShapeLibrary.Category> LibraryCategories = new();
    /// <summary>给界面的分类快照（缓存；只有分类/计数/筛选变化时才重建）。</summary>
    private LibraryCategoryInfo[] _libraryCategoryInfos = Array.Empty<LibraryCategoryInfo>();
    /// <summary>指针悬停在哪个格子上（-1 = 没在格子上）。</summary>
    internal int LibraryHover = -1;
    /// <summary>内容滚动位置（物理像素，0 = 顶部）。</summary>
    internal float LibraryScrollY;
    /// <summary>分类页签的横向滚动位置（物理像素）。</summary>
    internal float LibraryTabScroll;
    /// <summary>当前第几页（旧分页字段；新界面用滚动，保留只为兼容旧自检）。</summary>
    internal int LibraryPage;
    /// <summary>收藏弹窗的形状：横条 / 竖条（内部布局跟着换）。</summary>
    internal LibraryPanelMode LibraryMode = LibraryPanelMode.Horizontal;
    /// <summary>弹窗位置/大小（**逻辑屏幕坐标**；实际绘制乘 UiUnit）。空 = 还没摆过。</summary>
    private RectF _libraryPanelLogical;
    private bool _libraryPanelPrefsLoaded;
    /// <summary>2026-11-02：位置/宽度/横竖共用一份，高度按页签各记各的（逻辑像素）。</summary>
    private float _libraryHeightShapes;
    private float _libraryHeightFavorites;
    /// <summary>0 = 没拖；1 = 拖标题栏移动；2 = 拖右下角缩放。</summary>
    private int _libraryDrag;
    private Vector2 _libraryDragStart;
    private RectF _libraryDragStartRect;
    /// <summary>缩放拖动中的**连续**外框（逻辑坐标，未整格吸附）；松手才落盘。</summary>
    private RectF _libraryResizePreviewLogical;
    /// <summary>缩放拖动中冻结的列/行：外框跟手，卡片不跟着每像素重排。</summary>
    private int _libraryResizeCols;
    private int _libraryResizeRows;
    /// <summary>分类抽屉的分类滚动起点（分类多时用）。</summary>
    internal int LibraryCategoryScroll;
    /// <summary>分类抽屉是否展开（管理分类 / 2 列快速切换时用）。</summary>
    internal bool LibraryCategoriesOpen;
    /// <summary>窄面板的二级分类下拉（收藏/图形共用；和宽面板的页签是同一个数据源）。</summary>
    internal bool LibraryCategoryMenuOpen;
    internal int LibraryCategoryMenuHover = -1;
    /// <summary>抽屉是不是「管理分类」（显示铅笔/垃圾桶/新建）；false = 2 列的快速切换。</summary>
    internal bool LibraryDrawerManage;
    /// <summary>界面自己的全屏层（更多 / 说明）开着；收藏弹窗先藏，关掉自动回来。</summary>
    internal bool UiOverlayOpen;
    /// <summary>
    /// 这一刻收藏弹窗该不该显示 / 收输入。
    /// 全屏层、截图、**穿透**时暂时藏；退出后自动回来，状态不销毁。
    /// **PPT 放映时不藏**（2026-11-12 用户反馈：放映时也要能取图形/收藏；
    /// 画布本来就能画，没有理由只把这两个面板锁住）。
    /// </summary>
    internal bool LibraryPanelVisibleNow
        => LibraryPanelOpen && !PassThrough && !UiOverlayOpen && !CaptureActive;
    /// <summary>正在改名的分类 id（空 = 没在改名）。</summary>
    internal string LibraryRenamingId;
    /// <summary>改名/日志/其他小输入共用的字体缓存（原生 EDIT 用）。</summary>
    private IntPtr _libraryRenameFont;
    private float _libraryRenameFontPx;
    private long _libraryDrawerLastClickMs;
    private string _libraryDrawerLastClickId;

    /// <summary>使用态选中的那一张（拿起 / 跟着指针）；空 = 没选。整理态不用它。</summary>
    internal ShapeLibrary.Entry LibraryUseSelected;
    /// <summary>
    /// 面板常驻图钉（两个页签各记各的，跨重启保留）。
    /// 图形页默认不常驻（选完落笔就让开），收藏页默认常驻（放完继续放）。
    /// </summary>
    internal bool LibraryPinnedShapes;
    internal bool LibraryPinnedFavorites = true;
    internal bool LibraryPanelPinned
        => LibraryTab == LibraryTab.Shapes ? LibraryPinnedShapes : LibraryPinnedFavorites;
    /// <summary>
    /// 图形页「单次 / ∞ 连续」。只对**从面板选的图形**生效，主条图形不受影响。
    /// 默认单次；只记本次运行，重启回单次（避免上节课的连续状态带到下节课）。
    /// </summary>
    internal bool ShapeRepeatContinuous;
    /// <summary>拿着一张卡时：面板收成状态条，工具栏也让位，整个屏幕都能放。</summary>
    internal bool LibraryPlacementMode => LibraryUseSelected != null && !LibrarySelectMode;
    /// <summary>放下后临时选择的工具，用来“点空白回到原来的工具”。</summary>
    private Tool _toolBeforeLibraryInsert = Tool.Pen;
    private bool _restoreToolAfterLibrarySelection;
    /// <summary>图形面板选卡 = 一次性工具：画完一个图形就回到上一次的非图形工具。</summary>
    private Tool _shapeOneShotReturn = Tool.Pen;
    private bool _shapeOneShot;
    /// <summary>当前图形工具是不是从面板卡片选的（Esc 退出连续画时用来回原工具）。</summary>
    private bool _shapeFromPanel;
    /// <summary>最近一次非图形工具（笔/荧光笔/橡皮…），一次性图形画完回到它。</summary>
    private Tool _lastNonShapeTool = Tool.Pen;
    /// <summary>0 = 没按卡；1 = 待判；2 = 从卡片拖到画布。</summary>
    private int _libraryCardPress;
    private ShapeLibrary.Entry _libraryCardPressEntry;
    private Vector2 _libraryCardPressStart;
    private uint _libraryCardPressId;
    private bool _libraryCardPressWasSelected;
    /// <summary>整理模式选中的条目（勾选框）。</summary>
    internal readonly HashSet<ShapeLibrary.Entry> LibrarySelected = new();
    internal int LibrarySelectionCount => LibrarySelected.Count;
    internal bool IsLibrarySelected(ShapeLibrary.Entry e) => e != null && LibrarySelected.Contains(e);

    /// <summary>**整理模式**：从 ☰ 进入，点卡=勾选，拖把手=排序，空白拖=框选。退出清选择。</summary>
    internal bool LibrarySelectMode;

    // ---- 整理模式的框选 / 排序 -------------------------------------------
    private int _libraryManagePress;          // 0 没按；1 待判；2 框选
    private ShapeLibrary.Entry _libraryManageDown;
    private Vector2 _libraryManageStart;
    internal bool LibraryMarqueeActive;
    internal RectF LibraryMarqueeRect;
    internal bool LibraryManageSorting;
    internal ShapeLibrary.Entry LibraryManageSortEntry;
    internal ShapeLibrary.Entry LibraryManageDropBefore;
    /// <summary>滚动条拖动。</summary>
    private bool _libraryScrollDrag;
    private float _libraryScrollDragStartY;
    private float _libraryScrollDragStart;
    // ---- 触摸上下滚动（单指拖卡片区；点一下仍是选中） -------------------
    private bool _libraryTouchScrollArmed;
    private bool _libraryTouchScrolling;
    private uint _libraryTouchScrollId;
    private Vector2 _libraryTouchScrollStart;
    private float _libraryTouchScrollStartY;
    private ShapeLibrary.Entry _libraryTouchTapEntry;
    /// <summary>图形页触摸滚动后抬手要选的那张卡（和收藏的 TapEntry 同构）。</summary>
    private ShapePreset _libraryTouchTapShape;
    // ---- 页签拖动 / 滚动 ------------------------------------------------
    private int _libraryTabPress;             // 0 没按；1 待判；2 拖动
    private int _libraryTabDragIndex = -1;
    private string _libraryTabDragId;
    private float _libraryTabDragStartX;
    private float _libraryTabDragStartScroll;
    internal int LibraryTabDropIndex = -1;
    /// <summary>拖动落点（整理排序时画插入线用）：1 = 分类行；2 = 卡片插入线；3 = 画布。</summary>
    internal int LibraryDropTarget;
    internal string LibraryDropCategory;
    internal ShapeLibrary.Entry LibraryDropBefore;
    /// <summary>0 = 没菜单；3 = 移动到… 的二级分类；4 = ☰ 主菜单。</summary>
    internal int LibraryMenu;
    internal int LibraryMenuPage;
    internal string LibraryConfirm = "";
    /// <summary>
    /// 上一帧指针在不在"自绘界面块"（图库面板）上。只用来在**进出那一刻标脏**：
    /// 从画布带进来的落点环要擦掉（见 <see cref="PointerOnDrawnChrome"/> 的注释）。
    /// </summary>
    private bool _onDrawnChrome;

    /// <summary>把一张卡设成"选中 / 不选中"（框选 / 点选共用）。</summary>
    private void ApplyLibrarySelection(ShapeLibrary.Entry entry, bool on)
    {
        if (entry == null) return;
        bool changed = on ? LibrarySelected.Add(entry) : LibrarySelected.Remove(entry);
        if (!changed) return;
        NotifyUiStateChanged();
        _dirty = true;
    }

    /// <summary>整组替换勾选（框选一帧用；不做逐条 toggle）。</summary>
    private void SetLibrarySelection(IEnumerable<ShapeLibrary.Entry> entries)
    {
        var next = new HashSet<ShapeLibrary.Entry>();
        foreach (var e in entries) if (e != null) next.Add(e);
        if (next.SetEquals(LibrarySelected)) return;
        LibrarySelected.Clear();
        foreach (var e in next) LibrarySelected.Add(e);
        NotifyUiStateChanged();
        _dirty = true;
    }
    /// <summary>待插入的内容（非空 = 已经点了格子，下一次在画布上按下就落在那里）。</summary>
    private List<Stroke> _libraryPending;
    private string _libraryPendingTitle;

    /// <summary>是不是"等着落笔插入"（自检 / 光标要看它）。</summary>
    internal bool LibraryInsertArmed => _libraryPending != null;

    /// <summary>把选中的对象存成一个收藏条目。返回存了几个（0 = 没存成或重复）。</summary>
    internal int SaveSelectionToLibrary()
    {
        if (Doc.Selected.Count == 0) return 0;
        var items = new List<Stroke>(Doc.Selected);
        string category = (LibraryCategoryId == "all" || LibraryCategoryId == ShapeLibrary.TrashCategory)
            ? LibrarySaveCategoryId
            : LibraryCategoryId;
        var result = ShapeLibrary.SaveEx(items, category);
        if (!result.Success)
        {
            SetLibraryFeedback(LibraryFeedbackKind.Error, "");
            return 0;
        }
        if (result.Duplicate)
        {
            SetLibraryFeedback(LibraryFeedbackKind.Duplicate,
                                result.Entry?.CategoryName ?? ShapeLibrary.UncategorizedName);
            Console.WriteLine("收藏：这张已经在「"
                              + (result.Entry?.CategoryName ?? ShapeLibrary.UncategorizedName)
                              + "」里了，没有重复存");
            return 0;
        }

        LibrarySaveCategoryId = result.Entry?.CategoryId ?? category;
        SetLibraryFeedback(LibraryFeedbackKind.Success,
                            result.Entry?.CategoryName ?? ShapeLibrary.UncategorizedName);
        if (LibraryPanelOpen)
        {
            ReloadLibrary();
            NotifyUiStateChanged();
        }
        // 反馈除了图标换星，选中框也闪一下（和全选同一个 `SelFlashUntilMs`）。
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return items.Count;
    }

    /// <summary>
    /// ☰ → 导入图片…：选 PNG / JPG 等文件，**原样存进收藏**，透明 PNG 保留透明底。
    /// 不走截图（截图只会抓到屏幕像素，没有 alpha），也不走文档页的白底合成。
    /// </summary>
    private void ImportLibraryImages()
    {
        // 自检 / 自动测试里绝不弹系统对话框（模态调用会把消息泵卡住）。
        if (!ExportDialogEnabled)
        {
            SetInkStatus("自检模式不弹对话框");
            return;
        }

        string dir = GetUiPref("imageDir");
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        string[] files;
        try
        {
            files = ExportFileDialog.AskForOpenDocuments(
                OwnerHwnd(), dir, ImageFile.OpenFilter,
                "导入图片 / 贴纸（透明 PNG 会保留透明底，可多选）");
        }
        catch (Exception ex)
        {
            SetInkStatus("弹图片对话框失败：" + ex.Message);
            return;
        }
        finally
        {
            ExportDialogOpen = false;
            ReturnFocusAfterDialog();
        }

        if (files == null || files.Length == 0)
        {
            SetInkStatus("已取消");
            return;
        }
        SetUiPref("imageDir", Path.GetDirectoryName(files[0]) ?? "");
        ImportLibraryImagesFrom(files);
    }

    /// <summary>
    /// 把一组图片文件导入当前收藏分类（对话框和自检共用）。返回成功导入的条数。
    /// 每条图片各存一张卡片；同一张图已经存在时按重复跳过，不写第二份。
    /// </summary>
    internal int ImportLibraryImagesFrom(IReadOnlyList<string> files)
    {
        if (files == null || files.Count == 0) return 0;

        // 和“从画布收藏”同一条分类口径：在「全部 / 回收站」里操作时，
        // 落到上一次选的分类（没有就是未分类）。
        string targetId = (LibraryCategoryId == "all" || LibraryCategoryId == ShapeLibrary.TrashCategory)
            ? LibrarySaveCategoryId
            : LibraryCategoryId;
        if (!LibraryCategories.Exists(c => c.Id == targetId))
            targetId = ShapeLibrary.Uncategorized;

        string targetName = ShapeLibrary.UncategorizedName;
        foreach (var c in LibraryCategories)
            if (c.Id == targetId) { targetName = c.Name; break; }

        int ok = 0, duplicate = 0, failed = 0;
        string firstError = null;

        // 大图最多摆到视口的一半左右；小图标按“屏幕像素 1:1”走，不被放大。
        float dpi = MathF.Max(0.1f, DpiScale);
        float natural = 1f / dpi;
        var vp = ViewportCanvas;
        float maxW = MathF.Max(64f, (vp.MaxX - vp.MinX) * 0.55f);
        float maxH = MathF.Max(64f, (vp.MaxY - vp.MinY) * 0.55f);

        foreach (string path in files)
        {
            var loaded = ImageFile.Load(path);
            if (!loaded.Success)
            {
                failed++;
                firstError ??= loaded.Error;
                Console.WriteLine($"[收藏] 导入图片失败：{path}：{loaded.Error}");
                continue;
            }

            // ImageFile 已经按预乘 alpha 输出，不能再走 Adopt（会二次预乘）。
            var img = ImageData.AdoptPremultiplied(loaded.Width, loaded.Height, loaded.Bgra);
            if (img == null)
            {
                failed++;
                firstError ??= "图片像素无法识别";
                continue;
            }

            float scale = MathF.Min(natural,
                MathF.Min(maxW / MathF.Max(1, img.Width), maxH / MathF.Max(1, img.Height)));
            if (!(scale > 0.0001f)) scale = natural;

            var stroke = new Stroke
            {
                Tool = Tool.Capture,
                Kind = StrokeKind.Image,
                Color = new Color4(1f, 1f, 1f, 1f),
                Width = 0f,
                Image = img,
            };
            stroke.SetPoints(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(img.Width, img.Height),
            });
            stroke.Transform = Matrix3x2.CreateScale(scale);

            string title = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(title)) title = null;
            else if (title.Length > 40) title = title[..40];

            var save = ShapeLibrary.SaveEx(new[] { stroke }, targetId, title);
            // 存完这一张就把它从“存活图像”账上放掉；收藏卡片之后从磁盘重新读。
            // 不释放的话每导入一张，诊断里的 LiveImages / LiveBytes 就永久多一张。
            img.Release();
            if (!save.Success)
            {
                failed++;
                firstError ??= "写入收藏失败";
            }
            else if (save.Duplicate) duplicate++;
            else ok++;
        }

        if (ok > 0)
        {
            // 在回收站里导入时切回「全部」，否则新卡片会被回收站过滤掉、看不见。
            if (LibraryCategoryId == ShapeLibrary.TrashCategory)
            {
                LibraryCategoryId = "all";
                LibrarySelected.Clear();
                LibrarySelectMode = false;
                LibraryUseSelected = null;
            }
            ReloadLibrary();
            LibraryScrollY = 0;                 // 新条目排最前，滚回顶部让老师马上看到
            ClampLibraryScroll();
            NotifyUiStateChanged();
            _dirty = true;
        }

        string extra = "";
        if (duplicate > 0) extra += Loc.T("，{0} 张已在收藏里", duplicate);
        if (failed > 0) extra += Loc.T("，{0} 张失败", failed);
        if (ok > 0)
            SetInkStatus(Loc.T("已导入 {0} 张到「{1}」{2}；选中卡片 → 放置",
                               ok, Loc.Tr(targetName), extra));
        else if (duplicate > 0 || failed > 0)
        {
            string msg = duplicate > 0 ? Loc.T("这 {0} 张已经在收藏里了", duplicate)
                                       : Loc.Tr("没有导入图片");
            if (failed > 0)
                msg += string.IsNullOrEmpty(firstError) ? Loc.T("；{0} 张失败", failed)
                                                        : Loc.T("；{0} 张失败：{1}", failed, firstError);
            SetInkStatus(msg);
        }
        else
        {
            SetInkStatus("没有导入图片");
        }
        return ok;
    }

    private void SetLibraryFeedback(LibraryFeedbackKind kind, string category)
    {
        LibraryFeedback = kind;
        LibraryFeedbackCategory = category ?? "";
        LibraryFeedbackUntilMs = NowMs + 1400;
        if (kind == LibraryFeedbackKind.Success || kind == LibraryFeedbackKind.Duplicate)
            _librarySavedSelection = Doc.Selected.ToArray();
        _dirty = true;
    }

    /// <summary>界面全屏层（更多 / 说明）开合：只翻一个标志，弹窗内容不销毁。</summary>
    internal void SetUiOverlayFromUi(bool open)
    {
        if (UiOverlayOpen == open) return;
        UiOverlayOpen = open;
        if (open)
        {
            HideLibraryRenameWindow();
            LibraryHover = -1;
            LibraryDropTarget = 0;
            LibraryDropCategory = null;
            LibraryDropBefore = null;
        }
        _dirty = true;
    }

    internal void OpenLibraryPanel() => OpenLibraryPanel(LibraryTab.Favorites);

    internal void OpenLibraryPanel(LibraryTab tab)
    {
        // 穿透下收藏弹窗没有独立输入窗，会被藏起来；用户明确点“收藏/图形”= 现在要用它，
        // 先退出穿透（和 SwitchTool 选工具同一条语义）。
        if (PassThrough) SetPassThrough(false);
        ReloadLibrary();
        if (LibraryPanelOpen) SaveCurrentTabHeight();
        LibraryTab = tab;
        LibraryPanelOpen = true;
        ShapeHoverIndex = -1;
        ShapeNameIndex = -1;
        LibraryMenu = 0;
        LibraryMenuPage = 0;
        LibraryConfirm = "";
        LibraryCategoryScroll = 0;
        LibraryCategoriesOpen = false;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        LibraryDrawerManage = false;
        LibrarySelectMode = false;
        LibraryUseSelected = null;
        LibraryScrollY = 0;
        LibraryTabScroll = 0;
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        HideLibraryRenameWindow();
        EnsureLibraryCategoryVisible();
        EnsureLibraryTabVisible();
        ApplyTabHeight(tab);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>切一级页签：只换内容，不清面板位置/大小/横竖。</summary>
    internal void SetLibraryTab(LibraryTab tab)
    {
        if (!LibraryPanelOpen) { OpenLibraryPanel(tab); return; }
        if (LibraryTab == tab) return;
        SaveCurrentTabHeight();
        LibraryTab = tab;
        LibraryMenu = 0;
        LibraryMenuPage = 0;
        LibraryConfirm = "";
        LibraryCategoriesOpen = false;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        LibraryDrawerManage = false;
        LibrarySelectMode = false;
        LibraryUseSelected = null;
        LibraryScrollY = 0;
        LibraryTabScroll = 0;
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        ShapeHoverIndex = -1;
        ShapeNameIndex = -1;
        HideLibraryRenameWindow();
        ApplyTabHeight(tab);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>图形入口：已开在图形页就关；开在收藏页就切过来。</summary>
    internal void ToggleShapesPanel()
    {
        if (PassThrough)
        {
            SetPassThrough(false);
            if (!LibraryPanelOpen) OpenLibraryPanel(LibraryTab.Shapes);
            else SetLibraryTab(LibraryTab.Shapes);
            return;
        }
        if (LibraryPanelOpen && LibraryTab == LibraryTab.Shapes) { CloseLibraryPanel(); return; }
        if (LibraryPanelOpen) { SetLibraryTab(LibraryTab.Shapes); return; }
        OpenLibraryPanel(LibraryTab.Shapes);
    }

    /// <summary>
    /// 面板常驻图钉：亮 = 用画布时面板不收起；灭 = 开始画/放时让开。
    /// 两个页签各记各的，立即落盘。
    /// </summary>
    internal void ToggleLibraryPinFromUi()
    {
        EnsureLibraryPanelPrefs();
        if (LibraryTab == LibraryTab.Shapes) LibraryPinnedShapes = !LibraryPinnedShapes;
        else LibraryPinnedFavorites = !LibraryPinnedFavorites;
        SetUiPref("libPinShapes", LibraryPinnedShapes ? "1" : null);
        SetUiPref("libPinFav", LibraryPinnedFavorites ? null : "0");
        SetInkStatus(LibraryPanelPinned
            ? "面板常驻：开（画的时候不收起）"
            : "面板常驻：关（开始画时让开）");
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 图形页「单次 / ∞ 连续」。连续只对**从面板选的图形**生效；
    /// 主条图形本来就是连续，不受这里影响。默认单次，只记本次运行。
    /// </summary>
    internal void SetShapeRepeatContinuousFromUi(bool continuous)
    {
        if (ShapeRepeatContinuous == continuous) return;
        ShapeRepeatContinuous = continuous;
        // 当前手里正好是从面板选的图形：立即改这一笔之后的去留，不用重选卡片。
        if (_shapeFromPanel && IsShapeTool(Tool))
            _shapeOneShot = !continuous;
        SetInkStatus(continuous
            ? "图形：连续画（画完继续，Esc 结束）"
            : "图形：单次（画一个回到原工具）");
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>把当前页签的高度记到它自己那一份里（位置/宽度/横竖不记）。</summary>
    private void SaveCurrentTabHeight()
    {
        if (_libraryPanelLogical.IsEmpty) return;
        float h = _libraryPanelLogical.MaxY - _libraryPanelLogical.MinY;
        if (h <= 1f) return;
        if (LibraryTab == LibraryTab.Shapes) _libraryHeightShapes = h;
        else _libraryHeightFavorites = h;
    }

    /// <summary>某页签在“还没手动拖过”时的默认高度（逻辑像素）。</summary>
    private float DefaultTabHeightLogical(LibraryTab tab)
    {
        if (LibraryMode == LibraryPanelMode.Horizontal)
            return tab == LibraryTab.Shapes
                ? LibraryLayout.HeightForRows(5, 1f, LibraryLayout.ShapeCellHLogical)
                : 286f;
        // 竖排：图形页默认露 5 行（拖过之后按用户那一档记，允许更矮并滚动），收藏默认 5 行。
        return tab == LibraryTab.Shapes
            ? LibraryLayout.HeightForRows(5, 1f, LibraryLayout.ShapeCellHLogical)
            : 582f;
    }

    /// <summary>
    /// 切页签时只换高度：位置、宽度、横竖都不动，**顶边（MinY）固定**。
    /// 两个页签的高度各记各的，也不再把图形页硬卡在 5 行——图形页同样允许
    /// 拖矮后用滚动看，和收藏页一个口径（2026-11-03 用户报"大小没记忆"）。
    /// 顶部对齐是用户 2026-11-03 明确要的："收藏和图形应该高对齐，不应该对齐底部"。
    /// </summary>
    private void ApplyTabHeight(LibraryTab tab)
    {
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;

        // 先把矩形准备好（首次打开从偏好 / 默认值初始化）。
        // 这里**不走 `LibraryPanelRectNow()`**：那一步会按"新页签的卡片高度"重新
        // Snap 一遍，底边会跟着卡片高度抖 10 像素；切页签只该换高度，底边必须钉住。
        EnsureLibraryPanelPrefs();
        if (_libraryPanelLogical.IsEmpty)
        {
            var def = DefaultLibraryRect(screen, uiTop, dpi, LibraryMode);
            _libraryPanelLogical = new RectF
            {
                MinX = def.MinX / dpi, MinY = def.MinY / dpi,
                MaxX = def.MaxX / dpi, MaxY = def.MaxY / dpi,
            };
        }

        float hLog = tab == LibraryTab.Shapes ? _libraryHeightShapes : _libraryHeightFavorites;
        if (hLog <= 1f) hLog = DefaultTabHeightLogical(tab);

        var r = _libraryPanelLogical;
        float want = hLog * dpi;
        // **顶边（MinY）固定，只往下/往上换高度**（2026-11-03 用户：
        // "收藏和图形应该高对齐，不应该对齐底部"）。切到矮的页签时下面留空；
        // 切到高的页签如果会压到工具条，就把整块往上提刚好放下（提不动才缩高）。
        float top = r.MinY * dpi;
        float maxBottom = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi;
        float minTop = screen.MinY * dpi + 8f * dpi;
        if (top + want > maxBottom)
            top = MathF.Max(minTop, maxBottom - want);
        var desired = new RectF
        {
            MinX = r.MinX * dpi, MaxX = r.MaxX * dpi,
            MinY = top, MaxY = top + want,
        };
        desired = ClampLibraryPanel(desired, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF
        {
            MinX = desired.MinX / dpi, MinY = desired.MinY / dpi,
            MaxX = desired.MaxX / dpi, MaxY = desired.MaxY / dpi,
        };
    }

    /// <summary>
    /// 图钉灭时，第一次真正用画布（落笔/点空白）就把面板让开。
    /// 选卡后先不关：老师还有机会改「单次 / 连续」，一开始画它才收起。
    /// </summary>
    private void AutoCloseLibraryOnCanvasUse()
    {
        if (!LibraryPanelVisibleNow || LibraryPanelPinned) return;
        CloseLibraryPanel();
    }

    /// <summary>自检用：走一次“开始用画布”的收起判据（不真的画一笔）。</summary>
    internal void LibraryCanvasUseForTest() => AutoCloseLibraryOnCanvasUse();

    internal void CloseLibraryPanel()
    {
        if (!LibraryPanelOpen) return;
        SaveCurrentTabHeight();
        SaveLibraryPanelPrefs();
        LibraryPanelOpen = false;
        ShapeHoverIndex = -1;
        ShapeNameIndex = -1;
        LibrarySelected.Clear();
        LibrarySelectMode = false;
        LibraryUseSelected = null;
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        LibraryManageSortEntry = null;
        CancelLibraryTouchScroll();
        _libraryScrollDrag = false;
        _libraryTabPress = 0;
        _libraryTabDragIndex = -1;
        _libraryTabDragId = null;
        _libraryPending = null;
        _libraryPendingTitle = null;
        LibraryMenu = 0;
        LibraryMenuPage = 0;
        LibraryConfirm = "";
        LibraryCategoriesOpen = false;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        LibraryDrawerManage = false;
        HideLibraryRenameWindow();
        CancelLibraryPanelDrag();
        LibraryHover = -1;
        _onDrawnChrome = false;   // 面板没了，"在界面上"这条状态跟着清零（下次打开重新触发标脏）
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 进穿透时把收藏弹窗藏起来：**只藏，不关**——分类、位置、大小、勾选都留着，
    /// 退出穿透自动回来。正在进行的拖动 / 框选 / 菜单 / 改名要收掉，
    /// 否则回来时会留下半截状态（或者一个飘在外面的原生 EDIT）。
    /// </summary>
    private void HideLibraryForPassThrough()
    {
        if (!LibraryPanelOpen) return;

        CancelLibraryTouchScroll();

        // 没在改名就别碰焦点：进去穿透是要把焦点交给下层程序的。
        if (!string.IsNullOrEmpty(LibraryRenamingId)) HideLibraryRenameWindow();

        bool hadCapture = _libraryDrag != 0 || _libraryScrollDrag
                       || _libraryManagePress != 0 || LibraryManageSorting
                       || _libraryTabPress != 0;
        CancelLibraryPanelDrag();
        _libraryScrollDrag = false;
        _libraryManagePress = 0;
        _libraryManageDown = null;
        LibraryMarqueeActive = false;
        LibraryMarqueeRect = default;
        LibraryManageSorting = false;
        LibraryManageSortEntry = null;
        LibraryManageDropBefore = null;
        LibraryManageDropIndex = -1;
        _libraryTabPress = 0;
        _libraryTabDragIndex = -1;
        _libraryTabDragId = null;
        LibraryTabDropIndex = -1;
        LibraryDropTarget = 0;
        LibraryDropCategory = null;
        LibraryDropBefore = null;
        LibraryHover = -1;
        ClearEngineTooltip();
        LibraryMenu = 0;
        LibraryMenuPage = 0;
        LibraryConfirm = "";
        LibraryCategoriesOpen = false;
        LibraryDrawerManage = false;
        _libraryDrawerLastClickMs = 0;
        _libraryDrawerLastClickId = null;
        // 已经按了“放置”、还没落笔的：穿透期间画布不归我们，先取消，免得
        // 退出穿透后老师只是随手点一下画布却突然插进一张图。
        _libraryPending = null;
        _libraryPendingTitle = null;
        _onDrawnChrome = false;
        if (hadCapture) Native.ReleaseCapture();
        _dirty = true;
    }

    internal void ToggleLibraryPanel()
    {
        // 穿透下弹窗只是“藏着”，不是“关着”。这一下先把穿透退掉，
        // 把原来那个弹窗带回来；不要当成“关闭”把它真的关掉。
        if (PassThrough)
        {
            SetPassThrough(false);
            if (!LibraryPanelOpen) OpenLibraryPanel(LibraryTab.Favorites);
            else SetLibraryTab(LibraryTab.Favorites);
            return;
        }
        if (LibraryPanelOpen && LibraryTab == LibraryTab.Favorites) { CloseLibraryPanel(); return; }
        if (LibraryPanelOpen) { SetLibraryTab(LibraryTab.Favorites); return; }
        OpenLibraryPanel(LibraryTab.Favorites);
    }

    /// <summary>
    /// 分类偏好**只恢复一次**（2026-11-03 用户："比如用户使用的是立体这一类，
    /// 那么重启以后还是这个"）。`ReloadLibrary` 每次存 / 删 / 打开都会跑，
    /// 不能每跑一次就拿盘里的值覆盖用户当前的选择，所以用一次性闸门。
    /// 收藏分类是动态 id（用户建的分类可能已被删），恢复时要按当前分类表校验；
    /// 回收站是临时视图，不恢复（重启回到「全部」，免得一打开就是回收站）。
    /// </summary>
    private void RestoreLibraryCategoriesOnce()
    {
        if (_libraryCategoryRestored) return;
        _libraryCategoryRestored = true;

        string sc = GetUiPref("shapeCat");
        if (!string.IsNullOrEmpty(sc)
            && Array.Exists(ShapeCatalog.Categories, c => c.Id == sc))
            ShapeCategoryId = sc;

        string lc = GetUiPref("libCat");
        if (!string.IsNullOrEmpty(lc) && lc != ShapeLibrary.TrashCategory
            && (lc == "all" || LibraryCategories.Exists(c => c.Id == lc)))
            LibraryCategoryId = lc;
    }

    /// <summary>重读目录（打开面板 / 存完 / 删完都走这里），按当前分类重新筛。</summary>
    internal void ReloadLibrary()
    {
        LibraryAllEntries = ShapeLibrary.List(includeTrash: true);
        LibraryCategories = ShapeLibrary.ListCategories();
        RestoreLibraryCategoriesOnce();
        if (LibraryCategoryId != "all"
            && LibraryCategoryId != ShapeLibrary.TrashCategory
            && !LibraryCategories.Exists(c => c.Id == LibraryCategoryId))
            LibraryCategoryId = "all";
        if (LibrarySaveCategoryId != ShapeLibrary.Uncategorized
            && !LibraryCategories.Exists(c => c.Id == LibrarySaveCategoryId))
            LibrarySaveCategoryId = ShapeLibrary.Uncategorized;
        ApplyLibraryFilter();
        RefreshLibraryCategoryInfos();
        ClampLibraryScroll();
        EnsureLibraryCategoryVisible();
        EnsureLibraryTabVisible();
        _dirty = true;
    }

    /// <summary>收藏面板：切分类（`"all"` = 全部）。只筛，不搬。</summary>
    internal void SetLibraryCategory(string id)
    {
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        if (id != "all" && id != ShapeLibrary.TrashCategory
            && !LibraryCategories.Exists(c => c.Id == id)) return;
        LibraryCategoryId = id;
        if (id != "all" && id != ShapeLibrary.TrashCategory) LibrarySaveCategoryId = id;
        if (id != ShapeLibrary.TrashCategory) SetUiPref("libCat", id);
        // 切分类 = 换了一批卡，原来的勾选 / 选中 / 滚动都没意义了。
        LibrarySelected.Clear();
        LibrarySelectMode = false;
        LibraryUseSelected = null;
        _libraryPending = null;
        _libraryPendingTitle = null;
        LibraryConfirm = "";
        LibraryScrollY = 0;
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        ApplyLibraryFilter();
        RefreshLibraryCategoryInfos();
        EnsureLibraryCategoryVisible();
        EnsureLibraryTabVisible();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void CreateLibraryCategory()
    {
        var cat = ShapeLibrary.CreateCategory(null);
        LibraryCategories = ShapeLibrary.ListCategories();
        LibraryCategoryId = cat.Id;
        LibrarySaveCategoryId = cat.Id;
        SetUiPref("libCat", cat.Id);
        ApplyLibraryFilter();
        RefreshLibraryCategoryInfos();
        LibraryScrollY = 0;
        EnsureLibraryCategoryVisible();
        EnsureLibraryTabVisible();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void DeleteLibraryCategory(string id)
    {
        if (string.IsNullOrEmpty(id) || id == ShapeLibrary.Uncategorized) return;
        ShapeLibrary.DeleteCategory(id);       // 条目全部移到「未分类」，绝不删内容
        if (LibraryCategoryId == id) LibraryCategoryId = ShapeLibrary.Uncategorized;
        if (LibrarySaveCategoryId == id) LibrarySaveCategoryId = ShapeLibrary.Uncategorized;
        if (LibraryCategoryId != ShapeLibrary.TrashCategory) SetUiPref("libCat", LibraryCategoryId);
        LibraryConfirm = "";
        LibraryRenamingId = null;
        LibraryCategoriesOpen = true;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        LibraryDrawerManage = true;
        ReloadLibrary();
        SetInkStatus("分类已删除；里面的内容都在「未分类」");
        NotifyUiStateChanged();
    }

    /// <summary>抽屉行几何（按 id 找，已经算上滚动）。</summary>
    private RectF LibraryDrawerRowRectForId(string id)
    {
        var m = LibraryPanelInfoNow();
        var drawer = LibraryDrawerRectNow();
        int shown = LibraryDrawerVisibleCount();
        int first = Math.Clamp(LibraryCategoryScroll, 0, Math.Max(0, _libraryCategoryInfos.Length - shown));
        for (int row = 0; row < shown; row++)
            if (_libraryCategoryInfos[first + row].Id == id)
                return LibraryLayout.CategoryDrawerRowRect(drawer, m.Dpi, row);
        return default;
    }

    /// <summary>开始改分类名：一个统一文字宿主盖在那一行上，中文/输入法都由系统处理。</summary>
    internal void BeginLibraryCategoryRename(string id)
    {
        if (!LibraryLayout.DrawerRowIsUserCategory(id)) return;
        LibraryRenamingId = id;
        LibraryConfirm = "";
        LibraryMenu = 0;
        LibraryCategoriesOpen = true;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        var row = LibraryDrawerRowRectForId(id);
        if (row.IsEmpty) { LibraryRenamingId = null; return; }

        var edit = LibraryLayout.DrawerEditRect(row, UiUnit);
        string current = ShapeLibrary.CategoryNameOf(id) ?? "";
        var logical = new RectF
        {
            MinX = edit.MinX / UiUnit,
            MinY = (edit.MinY + ViewOffsetY) / UiUnit,
            MaxX = edit.MaxX / UiUnit,
            MaxY = (edit.MaxY + ViewOffsetY) / UiUnit,
        };

        TextInput.Begin(new TextInputRequest
        {
            Id = "library.rename",
            Rect = logical,
            InitialText = current,
            Mode = TextInputMode.SingleLine,
            MaxLength = 60,
            Visual = new TextInputVisual
            {
                Background = Opaque(FloatingTheme.Panel),
                Text = FloatingTheme.Text,
                FontSize = 11f,
                Padding = 4f,
            },
            OnCommit = text => FinishLibraryCategoryRename(id, text),
            OnCancel = () => FinishLibraryCategoryRename(id, null),
        });
        if (TextInput?.IsEditing != true) LibraryRenamingId = null;
        _dirty = true;
    }

    private void FinishLibraryCategoryRename(string id, string text)
    {
        if (string.IsNullOrEmpty(id)) return;
        LibraryRenamingId = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            string before = ShapeLibrary.CategoryNameOf(id);
            if (ShapeLibrary.RenameCategory(id, text))
            {
                string after = ShapeLibrary.CategoryNameOf(id);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                    SetInkStatus(Loc.T("分类已改名：{0}", after));
            }
        }
        LibraryCategories = ShapeLibrary.ListCategories();
        string nowName = ShapeLibrary.CategoryNameOf(id);
        foreach (var e in LibraryAllEntries)
            if (e.CategoryId == id) e.CategoryName = nowName;
        RefreshLibraryCategoryInfos();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>结束改名：`✓`/Enter 保存，`✕`/Esc/点别处取消。</summary>
    internal void EndLibraryCategoryRename(bool save)
    {
        if (string.IsNullOrEmpty(LibraryRenamingId)) return;
        string id = LibraryRenamingId;
        if (TextInput?.IsEditing == true)
        {
            if (save) TextInput.Commit();
            else TextInput.Cancel();
            return;
        }
        FinishLibraryCategoryRename(id, null);
    }

    private void HideLibraryRenameWindow()
    {
        if (TextInput?.IsEditing == true) TextInput.Cancel();
        LibraryRenamingId = null;
    }

    private IntPtr EnsureLibraryRenameFont(float dpi)
    {
        float px = MathF.Round(11f * dpi);
        if (_libraryRenameFont != IntPtr.Zero && MathF.Abs(_libraryRenameFontPx - px) < 0.5f)
            return _libraryRenameFont;
        if (_libraryRenameFont != IntPtr.Zero)
        {
            Native.DeleteObject(_libraryRenameFont);
            _libraryRenameFont = IntPtr.Zero;
        }
        _libraryRenameFont = Native.CreateFontW(
            -(int)px, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        _libraryRenameFontPx = px;
        return _libraryRenameFont != IntPtr.Zero
            ? _libraryRenameFont
            : Native.GetStockObject(Native.DEFAULT_GUI_FONT);
    }

    /// <summary>清空回收站（永久删除，二次确认由界面负责）。</summary>
    internal int EmptyLibraryTrash()
    {
        int n = ShapeLibrary.EmptyTrash();
        if (n > 0) SetInkStatus(Loc.T("已清空回收站（{0} 项）", n));
        if (LibraryCategoryId == ShapeLibrary.TrashCategory)
        {
            LibrarySelected.Clear();
            LibraryConfirm = "";
            ReloadLibrary();
            NotifyUiStateChanged();
        }
        _dirty = true;
        return n;
    }

    /// <summary>把选中的条目移到某个分类（从回收站移出 = 恢复并落到那个分类）。</summary>
    internal void MoveLibraryEntry(string categoryId)
    {
        if (LibrarySelected.Count == 0) return;
        var moving = new List<ShapeLibrary.Entry>();
        foreach (var e in LibraryEntries) if (LibrarySelected.Contains(e)) moving.Add(e);
        if (moving.Count == 0) return;
        foreach (var e in moving) ShapeLibrary.Move(e, categoryId);
        ShapeLibrary.ReorderToTop(moving);
        LibrarySelected.Clear();
        ReloadLibrary();
        SetInkStatus(Loc.T("已移动到「{0}」（{1} 项，排最前）", Loc.Tr(ShapeLibrary.CategoryNameOf(categoryId)), moving.Count));
        NotifyUiStateChanged();
    }

    /// <summary>勾选后删除：收藏里 = 进回收站；回收站里 = 永久删。</summary>
    internal void DeleteLibrarySelection()
    {
        if (LibrarySelected.Count == 0) return;
        bool permanent = LibraryCategoryId == ShapeLibrary.TrashCategory;
        int n = LibrarySelected.Count;
        foreach (var e in new List<ShapeLibrary.Entry>(LibrarySelected))
        {
            if (permanent) ShapeLibrary.Delete(e.Path);
            else ShapeLibrary.Trash(e);
        }
        SetInkStatus(permanent ? Loc.T("已永久删除 {0} 项", n) : Loc.T("已移入回收站（{0} 项）", n));
        LibrarySelected.Clear();
        LibraryConfirm = "";
        ReloadLibrary();
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 用**画布上当前选中的对象**覆盖收藏面板里选中的那一条（画布内改完 → 回写收藏）。
    /// 只允许一次一条、且不是回收站里的；内容撞另一条时拒绝。
    /// </summary>
    internal void UpdateLibraryEntryFromSelection()
    {
        if (LibrarySelected.Count != 1 || Doc.Selected.Count == 0) return;
        ShapeLibrary.Entry entry = null;
        foreach (var e in LibrarySelected) { entry = e; break; }
        var r = ShapeLibrary.Replace(entry, Doc.Selected);
        if (r.Success)
        {
            SetInkStatus(Loc.T("已更新收藏「{0}」（{1} 个对象）", r.Entry?.Title, r.Entry?.Objects));
        }
        else if (r.Duplicate)
        {
            SetInkStatus(Loc.T("改完的内容和「{0}」里已有的收藏重复，没有更新", Loc.Tr(r.DuplicateEntry?.CategoryName)));
        }
        else if (r.Unchanged)
        {
            SetInkStatus(Loc.Tr("内容和这条收藏一样，没有变化"));
        }
        else
        {
            SetInkStatus(Loc.T("更新收藏失败：{0}", r.Error ?? "未知原因"));
        }
        if (r.Success)
        {
            ReloadLibrary();
            NotifyUiStateChanged();
        }
        _dirty = true;
    }

}
