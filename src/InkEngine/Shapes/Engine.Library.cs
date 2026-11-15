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

    /// <summary>收藏弹窗菜单里的一行（绘制与命中共用）。</summary>
    internal readonly record struct LibraryMenuItem(string Key, string Label, bool Danger, bool Enabled);

    private const int LibraryMenuPerPage = 8;

    internal List<LibraryMenuItem> LibraryMenuItems()
    {
        var rows = new List<LibraryMenuItem>(8);
        if (LibraryMenu == 3)
        {
            rows.Add(new LibraryMenuItem("back", Loc.Tr("‹ 返回"), false, true));
            var cats = _libraryCategoryInfos;
            int pages = Math.Max(1, (cats.Length + LibraryMenuPerPage - 1) / LibraryMenuPerPage);
            int page = Math.Clamp(LibraryMenuPage, 0, pages - 1);
            int start = page * LibraryMenuPerPage;
            int end = Math.Min(cats.Length, start + LibraryMenuPerPage);
            for (int i = start; i < end; i++)
            {
                if (cats[i].Id == LibraryCategoryIds.All || cats[i].Id == LibraryCategoryIds.Trash) continue;
                rows.Add(new LibraryMenuItem("move:" + cats[i].Id, Loc.Tr(cats[i].Name), false, true));
            }
            if (pages > 1)
                rows.Add(new LibraryMenuItem("nextmove", Loc.T("下一页 {0}/{1} ›", page + 1, pages), false, true));
            return rows;
        }

        // ☰ 主菜单：只放“调整 / 补充内容”，使用态不出现删除/改名。
        // “导入图片”是**内容导入**（透明贴纸），不是收藏备份的导入/导出。
        rows.Add(new LibraryMenuItem("importimg", Loc.Tr("导入图片 / 贴纸…"), false, true));
        rows.Add(new LibraryMenuItem("manage", Loc.Tr("整理收藏…"), false, true));
        rows.Add(new LibraryMenuItem("categories", Loc.Tr("管理分类…"), false, true));
        bool trashView = LibraryCategoryId == ShapeLibrary.TrashCategory;
        int trashN = 0;
        foreach (var e in LibraryAllEntries) if (e.InTrash) trashN++;
        if (trashView)
        {
            rows.Add(new LibraryMenuItem("backcat", Loc.Tr("‹ 返回收藏"), false, true));
            rows.Add(new LibraryMenuItem("emptytrash",
                LibraryConfirm == "emptytrash" ? Loc.Tr("再点一次：清空回收站") : Loc.T("清空回收站 · {0} 个", trashN),
                true, trashN > 0));
        }
        else
        {
            rows.Add(new LibraryMenuItem("trash",
                trashN > 0 ? Loc.T("回收站 · {0} 个", trashN) : Loc.Tr("回收站（空）"), false, true));
        }
        // 方向和位置分开：拖动只挪位置，要换横/竖排走这里，不靠“拖到哪儿”。
        rows.Add(new LibraryMenuItem("orientation",
            LibraryMode == LibraryPanelMode.Horizontal ? Loc.Tr("切换为竖排") : Loc.Tr("切换为横排"),
            false, true));
        rows.Add(new LibraryMenuItem("reset", Loc.Tr("重置位置与大小"), false, true));
        return rows;
    }

    internal RectF LibraryMenuRect()
    {
        var m = LibraryPanelInfoNow();
        var screen = UiLogicalScreen;
        float dpi = UiUnit;
        var items = LibraryMenuItems();
        float w = 220f * dpi;
        float h = (8f + Math.Max(1, items.Count) * 30f) * dpi;
        var anchor = m.Menu;                 // 画布坐标
        var uiBar = UiBarBoundsNow();
        // 屏幕边界先换成画布坐标，再和 anchor 比；不然滚动白板后菜单会跑。
        float screenMinX = screen.MinX * dpi;
        float screenMaxX = screen.MaxX * dpi;
        float screenMinY = screen.MinY * dpi - ViewOffsetY;
        float screenMaxY = screen.MaxY * dpi - ViewOffsetY;
        float bottom = uiBar.IsEmpty ? screenMaxY - 8f * dpi
                                     : uiBar.MinY * dpi - ViewOffsetY - 8f * dpi;
        float x = Math.Clamp(anchor.MinX, screenMinX + 8f * dpi,
                             MathF.Max(screenMinX + 8f * dpi, screenMaxX - 8f * dpi - w));
        float y = anchor.MaxY + 6f * dpi;
        if (y + h > bottom) y = anchor.MinY - h - 6f * dpi;
        y = Math.Clamp(y, screenMinY + 8f * dpi,
                       MathF.Max(screenMinY + 8f * dpi, bottom - h));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    internal RectF LibraryMenuRowRect(in RectF menu, int i)
        => new()
        {
            MinX = menu.MinX + 4f * UiUnit, MinY = menu.MinY + 4f * UiUnit + i * 30f * UiUnit,
            MaxX = menu.MaxX - 4f * UiUnit, MaxY = menu.MinY + 4f * UiUnit + (i + 1) * 30f * UiUnit,
        };

    private void CloseLibraryMenu()
    {
        LibraryMenu = 0;
        LibraryMenuPage = 0;
        LibraryConfirm = "";
        _dirty = true;
    }

    /// <summary>自检用：关掉收藏弹窗的菜单。</summary>
    internal void CloseLibraryMenuForTest() => CloseLibraryMenu();

    /// <summary>自检用：画完图形后点空白是否已经挂上“回到上一个工具”。</summary>
    internal bool RestoreToolAfterShapeSelectionForTest => _restoreToolAfterLibrarySelection;
    /// <summary>自检用：模拟点空白，兑现“回到上一个工具”。</summary>
    internal void ClickBlankAfterShapeForTest() => ExitLibraryPlacementSelection();

    private void RunLibraryMenu(string key)
    {
        if (key.StartsWith("move:", StringComparison.Ordinal))
        {
            string id = key.Substring(5);
            CloseLibraryMenu();
            MoveLibraryEntry(id);
            return;
        }
        if (key == "nextmove")
        {
            LibraryMenuPage++;
            _dirty = true;
            return;
        }
        switch (key)
        {
            case "importimg":
                CloseLibraryMenu();
                ImportLibraryImages();
                break;
            case "manage":
                CloseLibraryMenu();
                LibrarySelectMode = true;
                LibraryUseSelected = null;
                _libraryPending = null;
                _libraryPendingTitle = null;
                LibrarySelected.Clear();
                LibraryMarqueeActive = false;
                LibraryConfirm = "";
                NotifyUiStateChanged();
                _dirty = true;
                break;
            case "categories":
                CloseLibraryMenu();
                LibraryCategoriesOpen = true;
                LibraryCategoryMenuOpen = false;
                LibraryCategoryMenuHover = -1;
                LibraryDrawerManage = true;
                EnsureLibraryCategoryVisible();
                _dirty = true;
                break;
            case "trash":
                CloseLibraryMenu();
                SetLibraryCategory(LibraryCategoryIds.Trash);
                break;
            case "backcat":
                CloseLibraryMenu();
                SetLibraryCategory(LibraryCategoryIds.All);
                break;
            case "orientation":
                CloseLibraryMenu();
                ToggleLibraryOrientation();
                break;
            case "reset":
                CloseLibraryMenu();
                ResetLibraryPlacement();
                break;
            case "back":
                CloseLibraryMenu();
                break;
            case "emptytrash":
                if (LibraryConfirm != key) { LibraryConfirm = key; _dirty = true; break; }
                CloseLibraryMenu();
                EmptyLibraryTrash();
                break;
        }
    }

    private void ApplyLibraryFilter()
    {
        if (LibraryCategoryId == "all")
        {
            LibraryEntries = new List<ShapeLibrary.Entry>();
            foreach (var e in LibraryAllEntries) if (!e.InTrash) LibraryEntries.Add(e);
        }
        else if (LibraryCategoryId == ShapeLibrary.TrashCategory)
        {
            LibraryEntries = new List<ShapeLibrary.Entry>();
            foreach (var e in LibraryAllEntries) if (e.InTrash) LibraryEntries.Add(e);
        }
        else
        {
            LibraryEntries = new List<ShapeLibrary.Entry>();
            foreach (var e in LibraryAllEntries)
                if (!e.InTrash && e.CategoryId == LibraryCategoryId) LibraryEntries.Add(e);
        }
        LibraryHover = -1;
        LibrarySelected.Clear();
        LibraryUseSelected = null;
    }

    /// <summary>当前页签的滚动项数（图形页是过滤后的卡片数）。</summary>
    private int LibraryScrollCountNow()
        => LibraryTab == LibraryTab.Shapes ? ShapeCardsNow().Length : LibraryEntries.Count;

    /// <summary>滚轮 / 滚动条滚内容（0 = 顶部）。</summary>
    internal void LibraryScrollBy(float dy)
    {
        var m = LibraryPanelInfoNow();
        float max = LibraryLayout.MaxScroll(m, LibraryScrollCountNow());
        float next = Math.Clamp(LibraryScrollY + dy, 0f, max);
        if (MathF.Abs(next - LibraryScrollY) < 0.01f) return;
        LibraryScrollY = next;
        LibraryHover = -1;
        _dirty = true;
    }

    internal void ClampLibraryScroll()
    {
        var m = LibraryPanelInfoNow();
        LibraryScrollY = Math.Clamp(LibraryScrollY, 0f,
            LibraryLayout.MaxScroll(m, LibraryScrollCountNow()));
    }

    /// <summary>让当前分类的页签始终露在可视区里；分类多时页签条横向滚。</summary>
    internal void EnsureLibraryTabVisible()
    {
        var m = LibraryPanelInfoNow();
        if (!m.Tabbed) { LibraryTabScroll = 0; return; }
        var cats = LibraryTabsNow();
        if (cats.Length == 0) return;
        var widths = new float[cats.Length];
        for (int i = 0; i < cats.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
        string curId = LibraryTab == LibraryTab.Shapes ? ShapeCategoryId : LibraryCategoryId;
        int idx = Array.FindIndex(cats, c => c.Id == curId);
        if (idx < 0) { LibraryTabScroll = 0; return; }
        float gap = LibraryLayout.TabGapLogical * UiUnit;
        float left = 0;
        for (int i = 0; i < idx; i++) left += widths[i] + gap;
        float right = left + widths[idx];
        float view = m.Tabs.MaxX - m.Tabs.MinX;
        float max = MathF.Max(0f, LibraryLayout.TabsWidth(widths, UiUnit) - view);
        if (left < LibraryTabScroll) LibraryTabScroll = left;
        else if (right > LibraryTabScroll + view) LibraryTabScroll = right - view;
        LibraryTabScroll = Math.Clamp(LibraryTabScroll, 0f, max);
    }

    /// <summary>页签用到的分类：全部 + 未分类 + 用户分类；回收站不进页签（在 ☰ 里）。</summary>
    internal LibraryCategoryInfo[] LibraryTabCategories()
    {
        var list = new List<LibraryCategoryInfo>(_libraryCategoryInfos.Length);
        foreach (var c in _libraryCategoryInfos)
            if (c.Id != ShapeLibrary.TrashCategory) list.Add(c);
        return list.ToArray();
    }

    /// <summary>当前页签真正显示的页签表：图形页是 全部/常用/曲线/立体，收藏页是分类。</summary>
    internal (string Id, string Name)[] LibraryTabsNow()
    {
        if (LibraryTab == LibraryTab.Shapes)
        {
            var a = new (string Id, string Name)[ShapeCatalog.Categories.Length];
            for (int i = 0; i < a.Length; i++)
                a[i] = (ShapeCatalog.Categories[i].Id, ShapeCatalog.Categories[i].Name);
            return a;
        }
        var cats = LibraryTabCategories();
        var b = new (string Id, string Name)[cats.Length];
        for (int i = 0; i < cats.Length; i++) b[i] = (cats[i].Id, cats[i].Name);
        return b;
    }

    /// <summary>图形页当前分类的卡片（已按分类过滤）。</summary>
    internal ShapePreset[] ShapeCardsNow() => ShapeCatalog.ForCategory(ShapeCategoryId);

    internal void SetShapeCategory(string id)
    {
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        if (ShapeCategoryId == id) return;
        ShapeCategoryId = id;
        SetUiPref("shapeCat", id);
        ShapeHoverIndex = -1;
        ShapeNameIndex = -1;
        LibraryScrollY = 0;
        EnsureLibraryTabVisible();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>图形卡是否就是当前选中的那一张：工具相同 + 档位相同。</summary>
    internal bool ShapePresetActive(ShapePreset p)
    {
        if (Tool != p.Tool) return false;
        return p.Tool switch
        {
            // 直线三张卡各代表一档线型：只有当前 LineDash 对得上时才高亮。
            Tool.Line => LineDash == (StrokeDash)Math.Clamp(p.Variant, 0, 2),
            Tool.Parabola => p.Variant == 0 ? ParabolaAxis == CurveAxis.OpenUp : ParabolaAxis != CurveAxis.OpenUp,
            Tool.Hyperbola => HyperbolaAsymptotes == (p.Variant == 0),
            Tool.ConicEllipse => EllipseFocusTriangle == (p.Variant == 0),
            Tool.Coordinate => CoordGridDefault == (p.Variant == 0),
            Tool.Prism or Tool.Pyramid or Tool.Frustum => SidesFor(p.Tool) == p.Variant,
            _ => true,
        };
    }

    /// <summary>点面板里的图形卡：换工具 + 直接设到这张卡对应的档位。</summary>
    internal void SetShapePresetFromUi(ShapePreset p)
    {
        // 从面板点卡进来时记住“上一次的非图形工具”：
        // 单次 = 画完一个就回去；连续 = 保持图形工具，Esc 再回去。
        var back = _shapeOneShot ? _shapeOneShotReturn : _lastNonShapeTool;
        SetToolFromUi(p.Tool);
        _shapeOneShotReturn = back;
        _shapeFromPanel = true;
        _shapeOneShot = !ShapeRepeatContinuous;
        switch (p.Tool)
        {
            // 直线卡自己带线型（实线/虚线/点线各一张，2026-11-12 用户定）：
            // 点哪张就用哪档，**不受工具栏上次切到哪档影响**；同时同步 LineDash，
            // 工具栏那颗直线图标跟着变，画到画布上也是同一档。
            case Tool.Line:
                LineDash = (StrokeDash)Math.Clamp(p.Variant, 0, 2);
                SetUiPref("lineDash", ((int)LineDash).ToString());
                break;
            case Tool.Parabola:
                ParabolaAxis = p.Variant == 0 ? CurveAxis.OpenUp : CurveAxis.OpenRight;
                break;
            case Tool.Hyperbola:
                HyperbolaAsymptotes = p.Variant == 0;
                break;
            case Tool.ConicEllipse:
                EllipseFocusTriangle = p.Variant == 0;
                break;
            case Tool.Coordinate:
                CoordGridDefault = p.Variant == 0;
                SetUiPref("coordGrid", CoordGridDefault ? "1" : null);
                break;
            case Tool.Prism:
                _sidesPrism = p.Variant;
                break;
            case Tool.Pyramid:
                _sidesPyramid = p.Variant;
                break;
            case Tool.Frustum:
                _sidesFrustum = p.Variant;
                break;
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>竖排左列：保证当前分类在可视范围内（分类多时跟着滚动）。</summary>
    private void EnsureLibraryCategoryVisible()
    {
        var m = LibraryPanelInfoNow();
        if (_libraryCategoryInfos.Length == 0) return;
        int cap = Math.Max(1, LibraryLayout.CategoryDrawerCapacity(m) - 1);
        int idx = Array.FindIndex(_libraryCategoryInfos, c => c.Id == LibraryCategoryId);
        if (idx < 0) return;
        // 当前分类尽量放在第二行：上面留一行、下面也露一行，拖到相邻分类不用先滚。
        int maxScroll = Math.Max(0, _libraryCategoryInfos.Length - cap);
        LibraryCategoryScroll = Math.Clamp(idx - (cap > 1 ? 1 : 0), 0, maxScroll);
    }

    private void RefreshLibraryCategoryInfos()
    {
        var list = new List<LibraryCategoryInfo>(LibraryCategories.Count + 2);
        int activeCount = 0, trashCount = 0;
        foreach (var e in LibraryAllEntries)
        {
            if (e.InTrash) trashCount++; else activeCount++;
        }
        list.Add(new LibraryCategoryInfo { Id = "all", Name = "全部", Count = activeCount });
        foreach (var c in LibraryCategories)
        {
            int n = 0;
            foreach (var e in LibraryAllEntries)
                if (!e.InTrash && e.CategoryId == c.Id) n++;
            list.Add(new LibraryCategoryInfo
            {
                Id = c.Id, Name = c.Name, Count = n, Pinned = c.Pinned
            });
        }
        // 回收站是虚拟分类：不写进 categories.txt，但界面要能选到它。
        list.Add(new LibraryCategoryInfo
        {
            Id = ShapeLibrary.TrashCategory, Name = ShapeLibrary.TrashName, Count = trashCount
        });
        _libraryCategoryInfos = list.ToArray();
    }

    /// <summary>
    /// 把待插入的图库内容落在 (x, y)：**包围盒左上角对齐落点**、原始大小、进文档**一步撤销**、
    /// 插入后**自动选中**——和"粘贴对象"完全同一条路（用户要的就是"点一下就有份能拖的"）。
    /// </summary>
    internal bool TryInsertLibraryAt(float x, float y)
    {
        if (_libraryPending == null) return false;
        var src = _libraryPending;
        string title = _libraryPendingTitle;
        _libraryPending = null;
        _libraryPendingTitle = null;

        var objs = new List<Stroke>(src.Count);
        foreach (var s in src) objs.Add(s.Clone());
        foreach (var s in objs) s.Id = 0;          // 身份重新发（同剪贴板那条教训：撞号会指错对象）

        // 包围盒中心对齐指针，并整体夹进可见区；虚影用同一个矩阵。
        var move = LibraryPlacementMatrix(objs, x, y);
        foreach (var s in objs) s.Transform = s.Transform * move;

        Doc.AddStrokes(objs);
        Doc.SelectOnly(objs);
        // 插入后要能马上拖 / 缩放，所以切到选择工具；否则当前若是画笔，
        // 选中框亮着但一拖就变成写字，老师会觉得“选中了却操作不了”。
        // 记住插入前的工具：点空白取消选中时回到它（荧光笔/橡皮也照原样回）。
        if (Tool != Tool.Marquee || !_restoreToolAfterLibrarySelection)
            _toolBeforeLibraryInsert = Tool;
        // 和粘贴图片同一条口径（见 PasteFromClipboard）。
        SwitchTool(Tool.Marquee);
        _restoreToolAfterLibrarySelection = true;
        // **收起来那一态**：和"刚画完一个图形"一致（见 `_autoSelCollapsed`）——
        // 插进来只是先给个轻的框，要整条操作条点一下圆钮。
        _autoSelCollapsed = true;
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"从图库插入 {objs.Count} 个对象");
        SetInkStatus(string.IsNullOrWhiteSpace(title)
            ? $"已插入收藏（{objs.Count} 个对象）"
            : $"已插入收藏「{title}」（{objs.Count} 个对象）");
        return true;
    }

    /// <summary>屏幕矩形 → 画布矩形。面板位置按**屏幕**记住，绘制/命中按画布。</summary>
    private RectF LibraryScreenToCanvas(in RectF r) => new()
    {
        MinX = r.MinX, MinY = r.MinY - ViewOffsetY,
        MaxX = r.MaxX, MaxY = r.MaxY - ViewOffsetY,
    };

    /// <summary>图库面板这一刻的矩形（**画布坐标**）。绘制、命中、脏区都问它。</summary>
    internal RectF LibraryPanelRectNow()
    {
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;

        EnsureLibraryPanelPrefs();
        if (_libraryDrag == 2 && !_libraryResizePreviewLogical.IsEmpty)
        {
            // 缩放拖动：外框跟手连续走，不做整格 Snap；卡片布局由 Info 冻结列/行。
            var preview = new RectF
            {
                MinX = _libraryResizePreviewLogical.MinX * dpi,
                MinY = _libraryResizePreviewLogical.MinY * dpi,
                MaxX = _libraryResizePreviewLogical.MaxX * dpi,
                MaxY = _libraryResizePreviewLogical.MaxY * dpi,
            };
            return LibraryScreenToCanvas(
                ClampLibraryPanel(preview, screen, uiTop, dpi, LibraryMode));
        }
        if (_libraryPanelLogical.IsEmpty || _libraryPanelLogical.MaxX <= _libraryPanelLogical.MinX
            || _libraryPanelLogical.MaxY <= _libraryPanelLogical.MinY)
        {
            var def = DefaultLibraryRect(screen, uiTop, dpi, LibraryMode);
            _libraryPanelLogical = new RectF { MinX = def.MinX / dpi, MinY = def.MinY / dpi,
                                               MaxX = def.MaxX / dpi, MaxY = def.MaxY / dpi };
        }

        var desired = new RectF
        {
            MinX = _libraryPanelLogical.MinX * dpi, MinY = _libraryPanelLogical.MinY * dpi,
            MaxX = _libraryPanelLogical.MaxX * dpi, MaxY = _libraryPanelLogical.MaxY * dpi,
        };
        var panel = ClampLibraryPanel(desired, screen, uiTop, dpi, LibraryMode);
        // 宽度和位置两页共用，统一按收藏卡的 84 吸附；图形卡更小，同一个宽度自然就是 9 列。
        panel = LibraryLayout.Snap(panel, dpi, LibraryMode, LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        panel = ClampLibraryPanel(panel, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF { MinX = panel.MinX / dpi, MinY = panel.MinY / dpi,
                                           MaxX = panel.MaxX / dpi, MaxY = panel.MaxY / dpi };
        // 拿起卡片后连小状态条也不画：整块屏幕交给虚影/落点。
        // 但完整矩形已经存进 _libraryPanelLogical，放下/取消后原样恢复。
        if (LibraryPlacementMode) return RectF.Empty;
        return LibraryScreenToCanvas(panel);
    }

    internal LibraryLayout.Info LibraryPanelInfoNow()
    {
        bool resizing = _libraryDrag == 2 && !_libraryResizePreviewLogical.IsEmpty;
        var m = LibraryLayout.Measure(LibraryPanelRectNow(), UiUnit, LibraryMode,
            resizing ? _libraryResizeCols : 0,
            resizing ? _libraryResizeRows : 0,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellWLogical : LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical,
            showRepeat: LibraryTab == LibraryTab.Shapes);
        if (!m.Tabbed) return m;

        // 页签档：把 CurrentCategory 指到当前页签（抽屉/管理分类定位用）。
        var tabs = LibraryTabsNow();
        string curId = LibraryTab == LibraryTab.Shapes ? ShapeCategoryId : LibraryCategoryId;
        int idx = Array.FindIndex(tabs, t => t.Id == curId);
        if (idx < 0) return m;
        var widths = new float[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(tabs[i].Name) * UiUnit;
        m = m with { CurrentCategory = LibraryLayout.TabRect(m, widths, idx, UiUnit, LibraryTabScroll) };
        return m;
    }

    internal string LibrarySelectedCategoryId
        => LibraryTab == LibraryTab.Shapes ? ShapeCategoryId : LibraryCategoryId;

    /// <summary>窄面板二级分类下拉：收藏和图形共用；数据只认 LibraryTabsNow。</summary>
    internal RectF LibraryCategoryMenuRect()
    {
        var m = LibraryPanelInfoNow();
        var cats = LibraryTabsNow();
        float dpi = UiUnit;
        int n = Math.Max(1, cats.Length);
        float itemH = 32f * dpi;
        float w = MathF.Max(m.CurrentCategory.MaxX - m.CurrentCategory.MinX, 176f * dpi);
        float h = n * itemH + 8f * dpi;
        float x = m.CurrentCategory.MinX;
        float y = m.CurrentCategory.MaxY + 4f * dpi;
        var panel = LibraryPanelRectNow();
        if (!panel.IsEmpty)
        {
            if (y + h > panel.MaxY - 8f * dpi)
                y = m.CurrentCategory.MinY - h - 4f * dpi;
            y = Math.Clamp(y, panel.MinY + 8f * dpi,
                           MathF.Max(panel.MinY + 8f * dpi, panel.MaxY - 8f * dpi - h));
            x = Math.Clamp(x, panel.MinX + 8f * dpi,
                           MathF.Max(panel.MinX + 8f * dpi, panel.MaxX - 8f * dpi - w));
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    internal RectF LibraryCategoryMenuItemRect(int i)
    {
        var m = LibraryCategoryMenuRect();
        float dpi = UiUnit, itemH = 32f * dpi;
        return new RectF
        {
            MinX = m.MinX + 6f * dpi, MinY = m.MinY + 4f * dpi + i * itemH,
            MaxX = m.MaxX - 6f * dpi, MaxY = m.MinY + 4f * dpi + (i + 1) * itemH,
        };
    }

    internal void SetLibraryCategoryById(string id)
    {
        if (LibraryTab == LibraryTab.Shapes) SetShapeCategory(id);
        else SetLibraryCategory(id);
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        _dirty = true;
    }

    /// <summary>整理操作条：全选 / 移动 / 删除 / 完成。</summary>
    internal (RectF All, RectF Move, RectF Delete, RectF Done) LibraryManageRects()
        => LibraryLayout.ManageRects(LibraryPanelInfoNow());

    /// <summary>旧接口：选中操作条（兼容旧自检；返回 更新/移动/删除/取消）。</summary>
    internal (RectF Update, RectF Move, RectF Delete, RectF Cancel) LibrarySelectionRects()
    {
        var (all, move, del, done) = LibraryManageRects();
        return (all, move, del, done);
    }

    /// <summary>
    /// 「用画布上当前选中的对象更新这条收藏」能不能点。
    /// 条件（沿用 2026-10-25 的规矩）：**只选中一张收藏** ＋ **画布上有选中**。
    /// 页签档把它从 ⋯ 菜单里搬到操作条上（菜单没了），所以需要这个显式的可用性判断。
    /// </summary>
    internal bool CanUpdateFromCanvas => LibrarySelected.Count == 1 && Doc.Selected.Count > 0;

    /// <summary>分类抽屉这一刻的矩形（覆盖在卡片上）。</summary>
    internal RectF LibraryDrawerRectNow()
    {
        var m = LibraryPanelInfoNow();
        int cap = LibraryLayout.CategoryDrawerCapacity(m) - (LibraryDrawerManage ? 1 : 0);
        int shown = Math.Min(Math.Max(1, cap), Math.Max(1, _libraryCategoryInfos.Length));
        var r = LibraryLayout.CategoryDrawerRect(m, shown + (LibraryDrawerManage ? 1 : 0));
        // 抽屉可能比窄面板宽：别伸出屏幕。
        float screenMaxX = UiLogicalScreen.MaxX * UiUnit - 8f * UiUnit;
        if (r.MaxX > screenMaxX)
        {
            float dx = r.MaxX - screenMaxX;
            r = new RectF { MinX = r.MinX - dx, MinY = r.MinY, MaxX = r.MaxX - dx, MaxY = r.MaxY };
        }
        return r;
    }

    private int LibraryDrawerVisibleCount()
    {
        var m = LibraryPanelInfoNow();
        int cap = Math.Max(1, LibraryLayout.CategoryDrawerCapacity(m) - (LibraryDrawerManage ? 1 : 0));
        return Math.Min(cap, Math.Max(1, _libraryCategoryInfos.Length));
    }

    /// <summary>抽屉行的命中：返回分类 id；`"__new"` = 新建；null = 没命中。</summary>
    private string LibraryDrawerCategoryAt(float x, float y)
    {
        var m = LibraryPanelInfoNow();
        var drawer = LibraryDrawerRectNow();
        int shown = LibraryDrawerVisibleCount();
        int first = Math.Clamp(LibraryCategoryScroll, 0, Math.Max(0, _libraryCategoryInfos.Length - shown));
        for (int row = 0; row < shown; row++)
            if (LibraryLayout.CategoryDrawerRowRect(drawer, m.Dpi, row).Contains(x, y))
                return _libraryCategoryInfos[first + row].Id;
        if (LibraryDrawerManage
            && LibraryLayout.CategoryDrawerRowRect(drawer, m.Dpi, shown).Contains(x, y))
            return "__new";
        return null;
    }

    /// <summary>绘制用：分类快照（含「全部」和回收站）。</summary>
    internal LibraryCategoryInfo[] LibraryCategoryListForPaint() => _libraryCategoryInfos;

    /// <summary>☰ → 重置位置与大小：回到默认横排、贴工具条上沿居中。</summary>
    internal void ResetLibraryPlacement()
    {
        CancelLibraryPanelDrag();
        CancelLibraryTouchScroll();
        LibraryMode = LibraryPanelMode.Horizontal;
        _libraryPanelLogical = default;
        _libraryPanelPrefsLoaded = true;
        LibraryScrollY = 0;
        LibraryTabScroll = 0;
        LibraryCategoriesOpen = false;
        LibraryDrawerManage = false;
        LibraryMenu = 0;
        LibraryConfirm = "";
        SaveLibraryPanelPrefs();
        SetInkStatus("收藏弹窗已恢复默认位置");
        _dirty = true;
    }

    /// <summary>自检用：把弹窗位置/大小/方向恢复成默认，避免上一条用例带偏。</summary>
    /// <summary>自检：把面板设成指定逻辑宽度（用于验证 compact 分类下拉）。</summary>
    internal void SetLibraryPanelWidthForTest(float logicalWidth)
    {
        var p = LibraryPanelRectNow();
        float dpi = UiUnit;
        _libraryPanelLogical = new RectF
        {
            MinX = p.MinX / dpi, MinY = p.MinY / dpi,
            MaxX = p.MinX / dpi + logicalWidth, MaxY = p.MaxY / dpi,
        };
        _libraryPanelPrefsLoaded = true;
        _dirty = true;
    }

    internal void ResetLibraryPanelForTest()
    {
        CancelLibraryPanelDrag();
        CancelLibraryTouchScroll();
        _libraryPanelLogical = default;
        LibraryMode = LibraryPanelMode.Horizontal;
        _libraryHeightShapes = 0f;
        _libraryHeightFavorites = 0f;
        _libraryPanelPrefsLoaded = true;
        LibraryPinnedShapes = false;
        LibraryPinnedFavorites = true;
        ShapeRepeatContinuous = false;
        ShapeCategoryId = "all";
        LibraryCategoryId = "all";
        _libraryCategoryRestored = true;     // 自检要确定性：别让上一轮存下的分类把默认带偏
        LibraryPage = 0;
        LibraryScrollY = 0;
        LibraryTabScroll = 0;
        LibraryCategoryScroll = 0;
        LibraryMenu = 0;
        LibraryConfirm = "";
        LibrarySelectMode = false;
        LibraryUseSelected = null;
        LibrarySelected.Clear();
        LibraryCategoriesOpen = false;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        LibraryDrawerManage = false;
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        SaveLibraryPanelPrefs();
        _dirty = true;
    }

    /// <summary>
    /// 自检用：**只清内存**里的面板位置 / 每页高度，下一次打开会从盘里重新读
    ///（模拟"关掉软件再开"）。和 <see cref="ResetLibraryPanelForTest"/> 不同，它不写盘。
    /// </summary>
    internal void ReloadLibraryPanelPlacementForTest()
    {
        CancelLibraryPanelDrag();
        CancelLibraryTouchScroll();
        _libraryPanelLogical = default;
        _libraryHeightShapes = 0f;
        _libraryHeightFavorites = 0f;
        _libraryPanelPrefsLoaded = false;
        LibraryPanelOpen = false;
        ShapeCategoryId = "all";
        LibraryCategoryId = "all";
        _libraryCategoryRestored = false;
        _dirty = true;
    }

    private RectF DefaultLibraryRect(in RectF screen, float uiTop, float dpi, LibraryPanelMode mode)
    {
        // 2026-10-26：卡片 72×80 → 84×92 之后重算的默认档。
        // 2026-11-02：顶上加一级页签（30 + 4），默认高度各 +34。
        // 横排 752 = 24 + 8×84 + 7×8；竖排 384 = 24 + 4×84 + 3×8。
        //
        // 2026-11-03：切页签改成**顶边固定**之后，默认高度取两页里更高的那一档
        //（横排 = 图形 5 行 532；竖排 = 收藏 5 行 582）。这样第一次打开任何一页，
        // 顶边都在同一个位置，切页签时菜单栏不会上下跳；矮的那页下方自然留空。
        float hLog = MathF.Max(DefaultTabHeightLogical(LibraryTab.Shapes),
                               DefaultTabHeightLogical(LibraryTab.Favorites));
        float w = (mode == LibraryPanelMode.Horizontal ? 752f : 384f) * dpi;
        float h = hLog * dpi;
        var desired = new RectF
        {
            MinX = (screen.MinX + screen.MaxX) * 0.5f * dpi - w * 0.5f,
            MinY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi - h,
            MaxX = 0, MaxY = 0,
        };
        desired.MaxX = desired.MinX + w;
        desired.MaxY = desired.MinY + h;
        var snapped = LibraryLayout.Snap(desired, dpi, mode,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellWLogical : LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        return ClampLibraryPanel(snapped, screen, uiTop, dpi, mode);
    }

    private RectF ClampLibraryPanel(in RectF rect, in RectF screen, float uiTop, float dpi, LibraryPanelMode mode)
    {
        float minX = screen.MinX * dpi + 8f * dpi;
        float minY = screen.MinY * dpi + 8f * dpi;
        float maxX = screen.MaxX * dpi - 8f * dpi;
        float maxY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi;
        float w = MathF.Min(rect.MaxX - rect.MinX, MathF.Max(72f * dpi, maxX - minX));
        float h = MathF.Min(rect.MaxY - rect.MinY, MathF.Max(72f * dpi, maxY - minY));
        float x = Math.Clamp(rect.MinX, minX, MathF.Max(minX, maxX - w));
        float y = Math.Clamp(rect.MinY, minY, MathF.Max(minY, maxY - h));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    private void EnsureLibraryPanelPrefs()
    {
        if (_libraryPanelPrefsLoaded) return;
        _libraryPanelPrefsLoaded = true;
        LibraryMode = GetUiPref("libMode") == "v" ? LibraryPanelMode.Vertical : LibraryPanelMode.Horizontal;
        // 图钉：图形页默认不常驻，收藏页默认常驻；两个页签各记各的。
        LibraryPinnedShapes = GetUiPref("libPinShapes") == "1";
        LibraryPinnedFavorites = GetUiPref("libPinFav") != "0";
        if (float.TryParse(GetUiPref("libHShapes"), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out float hs) && hs > 60f)
            _libraryHeightShapes = hs;
        if (float.TryParse(GetUiPref("libHFav"), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out float hf) && hf > 60f)
            _libraryHeightFavorites = hf;
        string raw = GetUiPref("libRect");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return;
        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w)
            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float h)
            && w > 60f && h > 60f)
        {
            // 2026-11-02：面板顶上加了一级页签。老 libRect 没这 34，补一次，
            // 否则老用户打开收藏会只剩 1 行卡片。
            if (GetUiPref("libVer") != "2")
            {
                h += LibraryLayout.TopTabHLogical + LibraryLayout.TopTabGapLogical;
                SetUiPref("libVer", "2");
            }
            _libraryPanelLogical = new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
            // 老配置没有按页签的高度：把这份高度先记给收藏页（启动默认页），
            // 不丢用户以前手动拖过的高度。
            if (_libraryHeightFavorites <= 1f) _libraryHeightFavorites = h;
        }
    }

    private void SaveLibraryPanelPrefs()
    {
        SaveCurrentTabHeight();
        SetUiPref("shapeCat", ShapeCategoryId);
        if (LibraryCategoryId != ShapeLibrary.TrashCategory) SetUiPref("libCat", LibraryCategoryId);
        SetUiPref("libMode", LibraryMode == LibraryPanelMode.Vertical ? "v" : "h");
        SetUiPref("libPinShapes", LibraryPinnedShapes ? "1" : null);
        SetUiPref("libPinFav", LibraryPinnedFavorites ? null : "0");
        SetUiPref("libVer", "2");
        SetUiPref("libHShapes", _libraryHeightShapes > 1f
            ? _libraryHeightShapes.ToString("F1", CultureInfo.InvariantCulture) : null);
        SetUiPref("libHFav", _libraryHeightFavorites > 1f
            ? _libraryHeightFavorites.ToString("F1", CultureInfo.InvariantCulture) : null);
        if (_libraryPanelLogical.IsEmpty) { SetUiPref("libRect", null); return; }
        var r = _libraryPanelLogical;
        SetUiPref("libRect", string.Join(",",
            r.MinX.ToString("F1", CultureInfo.InvariantCulture),
            r.MinY.ToString("F1", CultureInfo.InvariantCulture),
            (r.MaxX - r.MinX).ToString("F1", CultureInfo.InvariantCulture),
            (r.MaxY - r.MinY).ToString("F1", CultureInfo.InvariantCulture)));
    }

    private void StartLibraryDrag(int kind, float x, float y)
    {
        ShapeHoverIndex = -1;
        if (kind == 2)
        {
            // 外框随便变，卡片列/行先冻在这一刻；松手才重排。
            var now = LibraryPanelInfoNow();
            _libraryResizeCols = now.Cols;
            _libraryResizeRows = now.Rows;
            // preview / _libraryPanelLogical 都按**屏幕**逻辑坐标存；now.Panel 是画布。
            _libraryResizePreviewLogical = new RectF
            {
                MinX = now.Panel.MinX / UiUnit,
                MinY = (now.Panel.MinY + ViewOffsetY) / UiUnit,
                MaxX = now.Panel.MaxX / UiUnit,
                MaxY = (now.Panel.MaxY + ViewOffsetY) / UiUnit,
            };
        }
        _libraryDrag = kind;
        _libraryDragStart = new Vector2(x, y + ViewOffsetY);
        _libraryDragStartRect = _libraryPanelLogical;
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
    }

    internal bool LibraryPanelDragMove(float x, float y)
    {
        if (_libraryDrag == 0) return false;
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;
        // 指针是画布坐标，面板位置是屏幕坐标；先把 y 加回相机偏移。
        float sx = x;
        float sy = y + ViewOffsetY;
        float dx = (sx - _libraryDragStart.X) / dpi;
        float dy = (sy - _libraryDragStart.Y) / dpi;
        var r = _libraryDragStartRect;
        if (_libraryDrag == 1)
        {
            r = new RectF { MinX = r.MinX + dx, MinY = r.MinY + dy,
                            MaxX = r.MaxX + dx, MaxY = r.MaxY + dy };
        }
        else
        {
            // 拖动中允许连续像素，只要不塌到 1×2 以下；松手才吸附整格。
            float w = MathF.Max(LibraryLayout.MinWidthLogical, (r.MaxX - r.MinX) + dx);
            float h = MathF.Max(LibraryLayout.MinHeightLogical, (r.MaxY - r.MinY) + dy);
            r = new RectF { MinX = r.MinX, MinY = r.MinY, MaxX = r.MinX + w, MaxY = r.MinY + h };
        }
        var phys = new RectF { MinX = r.MinX * dpi, MinY = r.MinY * dpi,
                               MaxX = r.MaxX * dpi, MaxY = r.MaxY * dpi };
        phys = ClampLibraryPanel(phys, screen, uiTop, dpi, LibraryMode);
        if (_libraryDrag == 2)
        {
            _libraryResizePreviewLogical = new RectF
            {
                MinX = phys.MinX / dpi, MinY = phys.MinY / dpi,
                MaxX = phys.MaxX / dpi, MaxY = phys.MaxY / dpi,
            };
        }
        else
        {
            _libraryPanelLogical = new RectF { MinX = phys.MinX / dpi, MinY = phys.MinY / dpi,
                                               MaxX = phys.MaxX / dpi, MaxY = phys.MaxY / dpi };
        }
        _dirty = true;
        return true;
    }

    internal void LibraryPanelDragEnd()
    {
        if (_libraryDrag == 0) return;
        int kind = _libraryDrag;
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;

        // 缩放：拖动中一直是连续外框，松手才吸到**最近**整格。
        // 吸附只改宽高，左上角不动；不在这里再按屏幕边“贴一下”，免得松手整块跳。
        if (kind == 2 && !_libraryResizePreviewLogical.IsEmpty)
        {
            _libraryDrag = 0;
            var raw = new RectF
            {
                MinX = _libraryResizePreviewLogical.MinX * dpi,
                MinY = _libraryResizePreviewLogical.MinY * dpi,
                MaxX = _libraryResizePreviewLogical.MaxX * dpi,
                MaxY = _libraryResizePreviewLogical.MaxY * dpi,
            };
            var settled = LibraryLayout.SnapNearest(raw, dpi, LibraryMode, LibraryLayout.CellWLogical,
                LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
            settled = ClampLibraryPanel(settled, screen, uiTop, dpi, LibraryMode);
            _libraryPanelLogical = new RectF
            {
                MinX = settled.MinX / dpi, MinY = settled.MinY / dpi,
                MaxX = settled.MaxX / dpi, MaxY = settled.MaxY / dpi,
            };
            _libraryResizePreviewLogical = default;
            _libraryResizeCols = 0;
            _libraryResizeRows = 0;
            ClampLibraryScroll();
            EnsureLibraryTabVisible();
            SaveLibraryPanelPrefs();
            _dirty = true;
            return;
        }

        _libraryDrag = 0;
        var p = new RectF { MinX = _libraryPanelLogical.MinX * dpi, MinY = _libraryPanelLogical.MinY * dpi,
                            MaxX = _libraryPanelLogical.MaxX * dpi, MaxY = _libraryPanelLogical.MaxY * dpi };
        bool nearLeft = p.MinX <= screen.MinX * dpi + 40f * dpi;
        bool nearRight = p.MaxX >= screen.MaxX * dpi - 40f * dpi;
        bool nearBottom = p.MaxY >= uiTop * dpi - 40f * dpi;
        bool nearTop = p.MinY <= screen.MinY * dpi + 40f * dpi;
        // **只移动，不改形状**：以前拖到左/右就自动变竖排、拖到上/下就自动变横排，
        // 老师只是想把弹窗挪个位置，卡片却突然全部重排。方向和大小只由右侧
        // 缩放角 + ☰ 里的“切换横排/竖排”决定；这里最多贴边，不改 LibraryMode。
        var desired = new RectF { MinX = p.MinX, MinY = p.MinY, MaxX = p.MaxX, MaxY = p.MaxY };
        var snapped = LibraryLayout.Snap(desired, dpi, LibraryMode, LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        if (nearLeft)
            snapped = new RectF { MinX = screen.MinX * dpi + 8f * dpi, MinY = snapped.MinY,
                                  MaxX = screen.MinX * dpi + 8f * dpi + (snapped.MaxX - snapped.MinX),
                                  MaxY = snapped.MaxY };
        else if (nearRight)
        {
            float nw = snapped.MaxX - snapped.MinX;
            snapped = new RectF { MinX = screen.MaxX * dpi - 8f * dpi - nw, MinY = snapped.MinY,
                                  MaxX = screen.MaxX * dpi - 8f * dpi, MaxY = snapped.MaxY };
        }
        if (nearBottom)
            snapped = new RectF { MinX = snapped.MinX, MinY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi - (snapped.MaxY - snapped.MinY),
                                  MaxX = snapped.MaxX, MaxY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi };
        else if (nearTop)
            snapped = new RectF { MinX = snapped.MinX, MinY = screen.MinY * dpi + 8f * dpi,
                                  MaxX = snapped.MaxX, MaxY = screen.MinY * dpi + 8f * dpi + (snapped.MaxY - snapped.MinY) };
        snapped = ClampLibraryPanel(snapped, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF { MinX = snapped.MinX / dpi, MinY = snapped.MinY / dpi,
                                           MaxX = snapped.MaxX / dpi, MaxY = snapped.MaxY / dpi };
        var lm = LibraryPanelInfoNow();
        ClampLibraryScroll();
        EnsureLibraryTabVisible();
        SaveLibraryPanelPrefs();
        _dirty = true;
    }

    internal void ToggleLibraryOrientation()
    {
        float dpi = UiUnit;
        var p = LibraryPanelRectNow();
        float cx = (p.MinX + p.MaxX) * 0.5f;
        float cy = (p.MinY + p.MaxY) * 0.5f + ViewOffsetY;   // 屏幕坐标
        LibraryMode = LibraryMode == LibraryPanelMode.Horizontal
            ? LibraryPanelMode.Vertical : LibraryPanelMode.Horizontal;
        // 换横竖后，旧的“按页签高度”已经不适用；两页都回到新方向的默认高度。
        _libraryHeightShapes = 0f;
        _libraryHeightFavorites = 0f;
        float w = (LibraryMode == LibraryPanelMode.Horizontal ? 752f : 384f) * dpi;
        float h = DefaultTabHeightLogical(LibraryTab) * dpi;
        var desired = new RectF { MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
                                  MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f };
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;
        var snapped = LibraryLayout.Snap(desired, dpi, LibraryMode, LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        snapped = ClampLibraryPanel(snapped, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF { MinX = snapped.MinX / dpi, MinY = snapped.MinY / dpi,
                                           MaxX = snapped.MaxX / dpi, MaxY = snapped.MaxY / dpi };
        var lm = LibraryPanelInfoNow();
        ClampLibraryScroll();
        EnsureLibraryTabVisible();
        SaveLibraryPanelPrefs();
        _dirty = true;
    }

    /// <summary>点选卡片（触摸滚动里"没滚 = 点了一下"走这里，抬手才选）。</summary>
    private void SelectLibraryCardForTap(ShapeLibrary.Entry entry)
    {
        LibraryUseSelected = entry;
        LibrarySelected.Clear();
        _libraryPending = null;
        _libraryPendingTitle = null;
        LibraryConfirm = "";
        LibraryCategoriesOpen = false;
        LibraryDrawerManage = false;
        LibraryMenu = 0;
        if (entry == null)
        {
            LibraryHover = -1;
        }
        else
        {
            SetInkStatus(Loc.T("已选中「{0}」：点画布落下，Esc 返回", entry.Title));
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>再点同一张卡 = 放下；点别的卡 = 换一张。</summary>
    private void ToggleLibraryCardForTap(ShapeLibrary.Entry entry)
    {
        if (entry != null && ReferenceEquals(LibraryUseSelected, entry))
        {
            LibraryUseSelected = null;
            _libraryPending = null;
            _libraryPendingTitle = null;
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }
        SelectLibraryCardForTap(entry);
    }

    /// <summary>取消面板移动/缩放拖动（失焦 / 关面板 / 进穿透）。</summary>
    private void CancelLibraryPanelDrag()
    {
        bool had = _libraryDrag != 0 || !_libraryResizePreviewLogical.IsEmpty
                || _libraryCardPress != 0;
        _libraryDrag = 0;
        _libraryResizePreviewLogical = default;
        _libraryResizeCols = 0;
        _libraryResizeRows = 0;
        _libraryCardPress = 0;
        _libraryCardPressEntry = null;
        _libraryCardPressWasSelected = false;
        if (had)
        {
            Native.ReleaseCapture();
            _dirty = true;
        }
    }

    /// <summary>画布上的这个落点能不能放下收藏（面板、工具栏、PPT 条等都不算画布）。</summary>
    private bool LibraryCanvasPointAllowed(float x, float y)
    {
        if (!LibraryPanelVisibleNow) return false;
        if (LibraryLayout.Contains(LibraryPanelRectNow(), x, y)) return false;
        float sy = y + ViewOffsetY;
        if (InUiPopup(x, sy) || UiContains(x, sy) || PptBarContains(x, sy)
            || TimerCardContains(x, sy) || RollCardContains(x, sy)) return false;
        var vp = ViewportCanvas;
        return x >= vp.MinX && x <= vp.MaxX && y >= vp.MinY && y <= vp.MaxY;
    }

    /// <summary>
    /// 收藏对象放到画布上时的矩阵：**包围盒中心对齐指针**，并整体夹进可见区
    /// （包括工具栏上方）。虚影和最终落点都用这一份，保证"看到哪就落在哪"。
    /// </summary>
    internal Matrix3x2 LibraryPlacementMatrix(IReadOnlyList<Stroke> strokes, float x, float y)
    {
        var box = EditRegion.Of(strokes);
        if (box.IsEmpty) return Matrix3x2.CreateTranslation(x, y);

        float halfW = (box.MaxX - box.MinX) * 0.5f;
        float halfH = (box.MaxY - box.MinY) * 0.5f;
        float margin = 8f * UiUnit;
        var vp = ViewportCanvas;
        var ui = UiBarBoundsNow();
        // 拿起卡片时工具栏暂时让位，所以下边界直接到屏幕底；
        // 普通放置才夹在工具条上方，避免落进看不见的面板底下。
        float bottom = LibraryPlacementMode ? vp.MaxY : vp.MaxY - margin;
        // UiBarBounds 是**逻辑**坐标，这里要和 ViewportCanvas 的物理坐标对齐；
        // 少乘一个 UiUnit 会把落点夹到屏幕上半（虚影和放置都只能到上半屏）。
        if (!LibraryPlacementMode && !ui.IsEmpty)
            bottom = MathF.Min(bottom, ui.MinY * UiUnit - ViewOffsetY - margin);

        float minCx = vp.MinX + margin + halfW;
        float maxCx = vp.MaxX - margin - halfW;
        float minCy = vp.MinY + margin + halfH;
        float maxCy = bottom - halfH;
        float cx = minCx > maxCx ? (vp.MinX + vp.MaxX) * 0.5f : Math.Clamp(x, minCx, maxCx);
        float cy = minCy > maxCy ? (vp.MinY + bottom) * 0.5f : Math.Clamp(y, minCy, maxCy);
        return Matrix3x2.CreateTranslation(cx - (box.MinX + box.MaxX) * 0.5f,
                                           cy - (box.MinY + box.MaxY) * 0.5f);
    }

    /// <summary>虚影只在"指针真的在画布上"时显示；移到面板/界面/别的窗口就藏起来。</summary>
    internal bool LibraryGhostVisibleNow
    {
        get
        {
            if (!LibraryPanelVisibleNow || LibraryUseSelected == null || LibrarySelectMode
                || !PointerInside) return false;
            if (LibraryLayout.Contains(LibraryPanelRectNow(), PointerX, PointerY)) return false;
            if (LibraryMenu != 0 && LibraryLayout.Contains(LibraryMenuRect(), PointerX, PointerY))
                return false;
            float sy = PointerY + ViewOffsetY;
            if (InUiPopup(PointerX, sy) || UiContains(PointerX, sy)
                || PptBarContains(PointerX, sy) || TimerCardContains(PointerX, sy)
                || RollCardContains(PointerX, sy)) return false;
            var vp = ViewportCanvas;
            return PointerX >= vp.MinX && PointerX <= vp.MaxX
                && PointerY >= vp.MinY && PointerY <= vp.MaxY;
        }
    }

    /// <summary>
    /// 放下后对象是临时选中的；点空白 / Esc 时取消选中，并回到插入前的工具。
    /// 截屏、激光这种瞬态工具回退到画笔，其余按老师原来的工具恢复。
    /// </summary>
    private void ExitLibraryPlacementSelection()
    {
        if (!_restoreToolAfterLibrarySelection) return;
        var back = _toolBeforeLibraryInsert;
        _restoreToolAfterLibrarySelection = false;
        ClearSelectionForNewContext();
        if (back == Tool.Capture || back == Tool.Laser) back = Tool.Pen;
        SwitchTool(back);
        NotifyUiStateChanged();
        _dirty = true;
    }

    /// <summary>取消触摸滚动（失焦 / 关面板 / 进穿透都要走，别让状态粘住）。</summary>
    private void CancelLibraryTouchScroll()
    {
        bool had = _libraryTouchScrollArmed || _libraryTouchScrolling;
        _libraryTouchScrollArmed = false;
        _libraryTouchScrolling = false;
        _libraryTouchScrollId = 0;
        _libraryTouchTapEntry = null;
        _libraryTouchTapShape = null;
        if (had) Native.ReleaseCapture();
        _dirty = true;
    }

    internal bool LibraryPointerDown(float x, float y, uint pointerType, uint pointerId)
    {
        if (InUiPopup(x, y)) return false;
        float dpi = UiUnit;
        var m = LibraryPanelInfoNow();
        var panel = m.Panel;

        // 一级页签行最右：📌 常驻 / 单次-∞连续。先于菜单和卡片处理。
        // 图钉本体 24×24 偏小，命中区外扩 2px，触摸时更好点。
        if (m.Pin.Inflate(2f * dpi).Contains(x, y))
        {
            if (LibraryMenu != 0) CloseLibraryMenu();
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            ToggleLibraryPinFromUi();
            return true;
        }
        if (LibraryTab == LibraryTab.Shapes && !m.Repeat.IsEmpty && m.Repeat.Contains(x, y))
        {
            if (LibraryMenu != 0) CloseLibraryMenu();
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            bool continuous = m.Tabbed ? m.RepeatContinuous.Contains(x, y)
                                        : !ShapeRepeatContinuous;
            SetShapeRepeatContinuousFromUi(continuous);
            return true;
        }

        if (LibraryCategoryMenuOpen && m.Tabbed)
        {
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
        }
        // 窄面板二级分类下拉：收藏/图形共用；菜单项优先于下面的卡片/页签。
        if (LibraryCategoryMenuOpen)
        {
            var cats = LibraryTabsNow();
            for (int i = 0; i < cats.Length; i++)
                if (LibraryCategoryMenuItemRect(i).Contains(x, y))
                {
                    SetLibraryCategoryById(cats[i].Id);
                    return true;
                }
            if (LibraryCategoryMenuRect().Contains(x, y)) return true;
            bool onPill = m.CurrentCategory.Contains(x, y);
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
            if (onPill) return true;   // 再点 pill = 只收起，不要立刻又打开
        }

        // 改名中：只有「✓ / ✕」留给下面的抽屉分支，其余任何点击都先取消。
        if (!string.IsNullOrEmpty(LibraryRenamingId))
        {
            var renRow = LibraryDrawerRowRectForId(LibraryRenamingId);
            bool keep = LibraryCategoriesOpen && !renRow.IsEmpty
                        && (LibraryLayout.DrawerSaveRect(renRow, dpi).Contains(x, y)
                            || LibraryLayout.DrawerCancelRect(renRow, dpi).Contains(x, y));
            if (!keep) EndLibraryCategoryRename(false);
        }

        if (LibraryMenu != 0)
        {
            var menu = LibraryMenuRect();
            var items = LibraryMenuItems();
            if (LibraryLayout.Contains(menu, x, y))
            {
                for (int i = 0; i < items.Count; i++)
                    if (LibraryLayout.Contains(LibraryMenuRowRect(menu, i), x, y))
                    {
                        if (items[i].Enabled) RunLibraryMenu(items[i].Key);
                        return true;
                    }
                return true;
            }
            CloseLibraryMenu();
            return LibraryLayout.Contains(panel, x, y);
        }

        // 分类抽屉：管理分类 / 2 列的快速切换。
        if (LibraryCategoriesOpen)
        {
            var drawer = LibraryDrawerRectNow();
            if (LibraryLayout.Contains(drawer, x, y))
            {
                if (!string.IsNullOrEmpty(LibraryRenamingId))
                {
                    var renRow = LibraryDrawerRowRectForId(LibraryRenamingId);
                    if (!renRow.IsEmpty && LibraryLayout.DrawerSaveRect(renRow, dpi).Contains(x, y))
                    { EndLibraryCategoryRename(true); return true; }
                    if (!renRow.IsEmpty && LibraryLayout.DrawerCancelRect(renRow, dpi).Contains(x, y))
                    { EndLibraryCategoryRename(false); return true; }
                    EndLibraryCategoryRename(false);
                }

                string id = LibraryDrawerCategoryAt(x, y);
                if (id == "__new")
                {
                    LibraryConfirm = "";
                    CreateLibraryCategory();
                    BeginLibraryCategoryRename(LibraryCategoryId);
                    return true;
                }
                if (!string.IsNullOrEmpty(id))
                {
                    var row = LibraryDrawerRowRectForId(id);
                    bool userCat = LibraryLayout.DrawerRowIsUserCategory(id);
                    if (LibraryDrawerManage && userCat
                        && LibraryLayout.DrawerStarRect(row, dpi).Contains(x, y))
                    {
                        bool pinned = false;
                        foreach (var c in _libraryCategoryInfos)
                            if (c.Id == id) { pinned = c.Pinned; break; }
                        ShapeLibrary.SetCategoryPinned(id, !pinned);
                        LibraryCategories = ShapeLibrary.ListCategories();
                        RefreshLibraryCategoryInfos();
                        EnsureLibraryCategoryVisible();
                        EnsureLibraryTabVisible();
                        SetInkStatus(!pinned ? "已标记为常用分类" : "已取消常用标记");
                        _dirty = true;
                        return true;
                    }
                    if (LibraryDrawerManage && userCat
                        && LibraryLayout.DrawerPencilRect(row, dpi).Contains(x, y))
                    { BeginLibraryCategoryRename(id); return true; }
                    if (LibraryDrawerManage && userCat
                        && LibraryLayout.DrawerTrashRect(row, dpi).Contains(x, y))
                    {
                        if (LibraryConfirm == "delcat:" + id)
                        {
                            DeleteLibraryCategory(id);
                        }
                        else
                        {
                            int count = 0;
                            foreach (var c in _libraryCategoryInfos)
                                if (c.Id == id) { count = c.Count; break; }
                            LibraryConfirm = "delcat:" + id;
                            SetInkStatus(Loc.T("再点一次删除分类；{0} 个内容会移到「未分类」", count));
                            _dirty = true;
                        }
                        return true;
                    }
                    LibraryConfirm = "";
                    LibraryCategoriesOpen = false;
                    LibraryDrawerManage = false;
                    SetLibraryCategory(id);
                    return true;
                }
                return true;
            }
            LibraryCategoriesOpen = false;
            LibraryDrawerManage = false;
            LibraryConfirm = "";
            _dirty = true;
        }

        if (!LibraryLayout.Contains(panel, x, y)) return false;

        if (LibraryLayout.ResizeRect(panel, dpi).Contains(x, y))
        {
            StartLibraryDrag(2, x, y);
            return true;
        }

        // 一级页签：图形 / 我的收藏。只换内容，不动位置/大小。
        if (m.ShapesTab.Contains(x, y)) { SetLibraryTab(LibraryTab.Shapes); return true; }
        if (m.FavoritesTab.Contains(x, y)) { SetLibraryTab(LibraryTab.Favorites); return true; }

        // 图形页：分类页签 + 37 张档位卡；点卡换工具并直接设档，面板常驻。
        if (LibraryTab == LibraryTab.Shapes)
        {
            if (m.Close.Contains(x, y)) { CloseLibraryPanel(); return true; }
            var shapeCats = ShapeCatalog.Categories;
            var shapeCards = ShapeCardsNow();
            if (LibraryLayout.MaxScroll(m, shapeCards.Length) > 0.5f)
            {
                var track = LibraryLayout.ScrollTrack(m);
                if (x >= track.MinX - 6f * dpi && x <= track.MaxX + 6f * dpi
                    && y >= track.MinY && y <= track.MaxY)
                {
                    _libraryScrollDrag = true;
                    _libraryScrollDragStartY = y;
                    _libraryScrollDragStart = LibraryScrollY;
                    if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
                    LibraryScrollTo(y);
                    return true;
                }
            }
            if (m.Tabbed && m.Tabs.Contains(x, y))
            {
                var widths = new float[shapeCats.Length];
                for (int i = 0; i < shapeCats.Length; i++)
                    widths[i] = LibraryLayout.TabWidthLogical(shapeCats[i].Name) * dpi;
                int ti = LibraryLayout.TabAt(m, widths, x, y, dpi, LibraryTabScroll);
                if (ti >= 0 && ti < shapeCats.Length) SetShapeCategory(shapeCats[ti].Id);
                else StartLibraryDrag(1, x, y);   // 页签右边的空白也能拖面板
                return true;
            }
            if (!m.Tabbed && m.CurrentCategory.Contains(x, y))
            {
                // 窄面板放不下四个页签：打开同一个分类下拉（不再"点一下跳下一个"）。
                LibraryCategoryMenuOpen = !LibraryCategoryMenuOpen;
                LibraryCategoryMenuHover = -1;
                _dirty = true;
                return true;
            }
            if (m.Grid.Contains(x, y))
            {
                var cards = shapeCards;
                int idx = LibraryLayout.CellAt(m, cards.Length, x, y, LibraryScrollY);
                // 触摸：可滚动时先当“可能滚动”，点一下（没超阈值）才选卡。
                if (pointerType == Native.PT_TOUCH
                    && LibraryLayout.MaxScroll(m, cards.Length) > 0.5f)
                {
                    _libraryTouchScrollArmed = true;
                    _libraryTouchScrolling = false;
                    _libraryTouchScrollId = pointerId;
                    _libraryTouchScrollStart = new Vector2(x, y);
                    _libraryTouchScrollStartY = LibraryScrollY;
                    _libraryTouchTapEntry = null;
                    _libraryTouchTapShape = idx >= 0 && idx < cards.Length ? cards[idx] : null;
                    if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
                    return true;
                }
                if (idx >= 0 && idx < cards.Length)
                {
                    ShapeNameIndex = idx;
                    ShapeNameUntilMs = NowMs + 1500;
                    SetShapePresetFromUi(cards[idx]);
                }
                else StartLibraryDrag(1, x, y);   // 卡片之间的缝 / 最后一行空白 = 拖面板
                return true;
            }
            StartLibraryDrag(1, x, y);
            return true;
        }

        // 右侧很细的滚动条（内容超过一屏才有）。
        if (LibraryLayout.MaxScroll(m, LibraryEntries.Count) > 0.5f)
        {
            var track = LibraryLayout.ScrollTrack(m);
            if (x >= track.MinX - 6f * dpi && x <= track.MaxX + 6f * dpi
                && y >= track.MinY && y <= track.MaxY)
            {
                _libraryScrollDrag = true;
                _libraryScrollDragStartY = y;
                _libraryScrollDragStart = LibraryScrollY;
                if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
                LibraryScrollTo(y);
                return true;
            }
        }

        // ================= 整理模式 =================
        if (LibrarySelectMode)
        {
            var (all, move, del, done) = LibraryLayout.ManageRects(m);
            if (done.Contains(x, y)) { ExitLibraryManage(); return true; }
            if (all.Contains(x, y)) { ToggleSelectAllLibrary(); return true; }
            if (move.Contains(x, y))
            {
                if (LibrarySelected.Count > 0)
                {
                    LibraryMenu = 3;
                    LibraryMenuPage = 0;
                    LibraryConfirm = "";
                    _dirty = true;
                }
                return true;
            }
            if (del.Contains(x, y)) { DeleteLibraryManage(); return true; }
            if (m.Header.Contains(x, y)) return true;

            int idx = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);
            if (idx >= 0 && idx < LibraryEntries.Count)
            {
                var cell = LibraryLayout.CellRect(m, idx, LibraryScrollY);
                var entry = LibraryEntries[idx];
                if (LibraryLayout.GripRect(cell, dpi).Contains(x, y))
                {
                    BeginLibraryManageSort(entry, x, y);
                    return true;
                }
                BeginLibraryManagePress(entry, x, y);
                return true;
            }
            BeginLibraryManagePress(null, x, y);
            return true;
        }

        // ================= 回收站（使用态） =================
        if (LibraryCategoryId == ShapeLibrary.TrashCategory)
        {
            if (m.Menu.Contains(x, y))
            {
                LibraryMenu = 4;
                LibraryMenuPage = 0;
                LibraryConfirm = "";
                _dirty = true;
                return true;
            }
            if (m.Close.Contains(x, y)) { CloseLibraryPanel(); return true; }
            if (m.Header.Contains(x, y)) { SetLibraryCategory(LibraryCategoryIds.All); return true; }
            int idx = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);
            if (idx >= 0 && idx < LibraryEntries.Count)
            {
                string t = LibraryEntries[idx].Title;
                ShapeLibrary.Restore(LibraryEntries[idx]);
                ReloadLibrary();
                SetInkStatus(Loc.T("已恢复「{0}」", t));
                NotifyUiStateChanged();
                return true;
            }
            StartLibraryDrag(1, x, y);
            return true;
        }

        // 顶栏：☰ / 页签（或当前分类）/ ✕
        if (m.Menu.Contains(x, y))
        {
            LibraryMenu = 4;
            LibraryMenuPage = 0;
            LibraryConfirm = "";
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
            return true;
        }
        if (m.Close.Contains(x, y)) { CloseLibraryPanel(); return true; }

        if (m.Tabbed)
        {
            var cats = LibraryTabCategories();
            var widths = new float[cats.Length];
            for (int i = 0; i < cats.Length; i++)
                widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * dpi;
            int ti = LibraryLayout.TabAt(m, widths, x, y, dpi, LibraryTabScroll);
            if (ti >= 0 && ti < cats.Length)
            {
                BeginLibraryTabPress(ti, cats[ti].Id, x, y);
                return true;
            }
        }
        else if (m.CurrentCategory.Contains(x, y))
        {
            // 收藏页窄面板：和图形页同一条下拉，不再弹分类抽屉（抽屉只做管理）。
            LibraryCategoryMenuOpen = !LibraryCategoryMenuOpen;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
            return true;
        }

        int hit = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);

        // 触摸：单指在卡片区上下拖 = 滚动，点一下（移动没过阈值）= 选中。
        // 鼠标 / 笔保持原来的立即点选；整理模式下单指仍归框选/排序，不抢。
        if (pointerType == Native.PT_TOUCH && !LibrarySelectMode
            && LibraryLayout.Contains(m.Grid, x, y)
            && LibraryLayout.MaxScroll(m, LibraryEntries.Count) > 0.5f)
        {
            _libraryTouchScrollArmed = true;
            _libraryTouchScrolling = false;
            _libraryTouchScrollId = pointerId;
            _libraryTouchScrollStart = new Vector2(x, y);
            _libraryTouchScrollStartY = LibraryScrollY;
            _libraryTouchTapEntry = (hit >= 0 && hit < LibraryEntries.Count)
                ? LibraryEntries[hit] : null;
            if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
            return true;
        }

        if (hit >= 0 && hit < LibraryEntries.Count)
        {
            var entry = LibraryEntries[hit];
            // 触摸没有 hover：直接点选/再点收起；鼠标/笔要等抬手或拖出阈值，
            // 才能分清“点一下”和“按住拖到画布”。
            if (pointerType == Native.PT_TOUCH)
            {
                ToggleLibraryCardForTap(entry);
                return true;
            }

            _libraryCardPress = 1;
            _libraryCardPressEntry = entry;
            _libraryCardPressStart = new Vector2(x, y);
            _libraryCardPressId = pointerId;
            _libraryCardPressWasSelected = ReferenceEquals(LibraryUseSelected, entry);
            if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
            return true;
        }

        LibraryUseSelected = null;
        _libraryPending = null;
        _libraryPendingTitle = null;
        StartLibraryDrag(1, x, y);
        return true;
    }

    // ================= 指针移动 / 抬起（整理、页签、滚动条） =================

    /// <summary>面板内的专用拖动状态；返回 true = 这一串移动归它。</summary>
    internal bool LibraryPanelPointerMove(float x, float y, uint pointerId)
    {
        if (LibraryCategoryMenuOpen)
        {
            int hov = -1;
            var cats = LibraryTabsNow();
            for (int i = 0; i < cats.Length; i++)
                if (LibraryCategoryMenuItemRect(i).Contains(x, y)) { hov = i; break; }
            if (hov != LibraryCategoryMenuHover)
            {
                LibraryCategoryMenuHover = hov;
                _dirty = true;
            }
            if (LibraryCategoryMenuRect().Contains(x, y)) return true;
        }

        // 从卡片按住往外拖：过阈值才算拖，松手在画布上放置。
        if (_libraryCardPress != 0)
        {
            if (pointerId != _libraryCardPressId) return true;
            if (_libraryCardPress == 1
                && Vector2.Distance(new Vector2(x, y), _libraryCardPressStart) > 6f * UiUnit)
            {
                _libraryCardPress = 2;
                if (_libraryCardPressEntry != null)
                    SelectLibraryCardForTap(_libraryCardPressEntry);
            }
            if (_libraryCardPress == 2)
            {
                ClearEngineTooltip();
                _dirty = true;       // 虚影位置靠 PointerX/Y，重画一帧
            }
            return true;
        }

        // 触摸滚动：第一根手指负责滚动，其它手指吞掉，别落成笔画/第二次点选。
        if (_libraryTouchScrollArmed || _libraryTouchScrolling)
        {
            if (pointerId != _libraryTouchScrollId) return true;
            float dx = x - _libraryTouchScrollStart.X;
            float dy = y - _libraryTouchScrollStart.Y;
            float threshold = 8f * UiUnit;
            if (!_libraryTouchScrolling)
            {
                if (MathF.Abs(dy) >= threshold && MathF.Abs(dy) > MathF.Abs(dx) * 1.1f)
                {
                    _libraryTouchScrolling = true;
                    _libraryTouchTapEntry = null;   // 已经是在滚动，抬手不能当成点选
                    _libraryTouchTapShape = null;
                }
                else if (MathF.Abs(dx) >= threshold)
                {
                    // 横向滑：这次不算点选，也不让面板跟着跑；继续等纵向手势。
                    _libraryTouchTapEntry = null;
                    _libraryTouchTapShape = null;
                }
            }
            if (_libraryTouchScrolling)
            {
                var m = LibraryPanelInfoNow();
                float max = LibraryLayout.MaxScroll(m, LibraryScrollCountNow());
                LibraryScrollY = Math.Clamp(_libraryTouchScrollStartY - dy, 0f, max);
                LibraryHover = -1;
                ClearEngineTooltip();
                _dirty = true;
            }
            return true;
        }

        if (_libraryScrollDrag) { LibraryScrollTo(y); return true; }
        if (_libraryTabPress != 0) { LibraryTabDragMove(x, y); return true; }
        if (LibraryManageSorting) { LibraryManageSortMove(x, y); return true; }
        if (_libraryManagePress != 0) { LibraryManagePressMove(x, y); return true; }

        if (LibraryTab == LibraryTab.Shapes && LastPointerType != Native.PT_TOUCH)
        {
            var m = LibraryPanelInfoNow();
            var cards = ShapeCardsNow();
            int hover = m.Grid.Contains(x, y)
                ? LibraryLayout.CellAt(m, cards.Length, x, y, LibraryScrollY) : -1;
            if (hover != ShapeHoverIndex)
            {
                ShapeHoverIndex = hover;
                _dirty = true;
            }
        }
        return false;
    }

    internal bool LibraryPanelPointerUp(float x, float y, uint pointerId)
    {
        if (_libraryCardPress != 0)
        {
            if (pointerId != _libraryCardPressId) return true;
            bool dragged = _libraryCardPress == 2;
            bool wasSelected = _libraryCardPressWasSelected;
            var entry = _libraryCardPressEntry;
            _libraryCardPress = 0;
            _libraryCardPressEntry = null;
            _libraryCardPressWasSelected = false;
            Native.ReleaseCapture();
            if (dragged && entry != null && LibraryCanvasPointAllowed(x, y))
            {
                LibraryUseSelected = null;
                InsertLibraryGroupAt(new[] { entry }, x, y);
            }
            else if (!dragged)
            {
                // 没拖 = 点了一下：再点同一张收起，点别的换一张。
                if (wasSelected) ToggleLibraryCardForTap(entry);
                else SelectLibraryCardForTap(entry);
            }
            _dirty = true;
            return true;
        }

        if (_libraryTouchScrollArmed || _libraryTouchScrolling)
        {
            if (pointerId != _libraryTouchScrollId) return true;
            bool scrolled = _libraryTouchScrolling;
            var tap = _libraryTouchTapEntry;
            var tapShape = _libraryTouchTapShape;
            _libraryTouchScrollArmed = false;
            _libraryTouchScrolling = false;
            _libraryTouchScrollId = 0;
            _libraryTouchTapEntry = null;
            _libraryTouchTapShape = null;
            Native.ReleaseCapture();
            if (!scrolled)
            {
                if (tapShape != null) SetShapePresetFromUi(tapShape);
                else ToggleLibraryCardForTap(tap);
            }
            _dirty = true;
            return true;
        }
        if (_libraryScrollDrag)
        {
            _libraryScrollDrag = false;
            Native.ReleaseCapture();
            return true;
        }
        if (_libraryTabPress != 0)
        {
            LibraryTabDragEnd(x, y);
            return true;
        }
        if (LibraryManageSorting)
        {
            LibraryManageSortEnd();
            return true;
        }
        if (_libraryManagePress != 0)
        {
            LibraryManagePressEnd(x, y);
            return true;
        }
        return false;
    }

    private void LibraryScrollTo(float y)
    {
        var m = LibraryPanelInfoNow();
        int count = LibraryScrollCountNow();
        float max = LibraryLayout.MaxScroll(m, count);
        var track = LibraryLayout.ScrollTrack(m);
        var thumb = LibraryLayout.ScrollThumb(m, count, LibraryScrollY);
        if (max <= 0.5f || track.IsEmpty) return;
        float thumbH = thumb.IsEmpty ? 22f * UiUnit : thumb.MaxY - thumb.MinY;
        float span = MathF.Max(1f, (track.MaxY - track.MinY) - thumbH);
        float t = Math.Clamp((y - track.MinY - thumbH * 0.5f) / span, 0f, 1f);
        LibraryScrollY = t * max;
        LibraryHover = -1;
        _dirty = true;
    }

    // ---- 页签拖动 / 点击 ----

    private void BeginLibraryTabPress(int index, string id, float x, float y)
    {
        _libraryTabPress = 1;
        _libraryTabDragIndex = index;
        _libraryTabDragId = id;
        _libraryTabDragStartX = x;
        _libraryTabDragStartScroll = LibraryTabScroll;
        LibraryTabDropIndex = -1;
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
    }

    private void LibraryTabDragMove(float x, float y)
    {
        if (_libraryTabPress == 0) return;
        if (_libraryTabPress == 1)
        {
            if (MathF.Abs(x - _libraryTabDragStartX) < 4f * UiUnit) return;
            if (!LibraryLayout.DrawerRowIsUserCategory(_libraryTabDragId))
                return;   // 「全部 / 未分类」固定，不参与排序
            _libraryTabPress = 2;
        }
        var m = LibraryPanelInfoNow();
        var cats = LibraryTabCategories();
        var widths = new float[cats.Length];
        for (int i = 0; i < cats.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
        int drop = cats.Length;
        for (int i = 0; i < cats.Length; i++)
        {
            if (!LibraryLayout.DrawerRowIsUserCategory(cats[i].Id)) continue;
            var tr = LibraryLayout.TabRect(m, widths, i, UiUnit, LibraryTabScroll);
            if (x < (tr.MinX + tr.MaxX) * 0.5f) { drop = i; break; }
        }
        LibraryTabDropIndex = drop;
        _dirty = true;
    }

    private void LibraryTabDragEnd(float x, float y)
    {
        int state = _libraryTabPress;
        string id = _libraryTabDragId;
        int drop = LibraryTabDropIndex;
        _libraryTabPress = 0;
        LibraryTabDropIndex = -1;
        _libraryTabDragIndex = -1;
        _libraryTabDragId = null;
        Native.ReleaseCapture();
        if (state == 2 && !string.IsNullOrEmpty(id) && LibraryLayout.DrawerRowIsUserCategory(id))
        {
            var cats = LibraryTabCategories();
            string before = drop >= 0 && drop < cats.Length ? cats[drop].Id : null;
            if (before == id) before = null;
            if (ShapeLibrary.ReorderCategory(id, before))
            {
                ReloadLibrary();
                SetLibraryCategory(id);
                SetInkStatus("分类顺序已更新");
            }
        }
        else if (!string.IsNullOrEmpty(id))
        {
            SetLibraryCategory(id);
        }
        _dirty = true;
    }

    // ---- 整理模式：点击 / 框选 ----

    private void BeginLibraryManagePress(ShapeLibrary.Entry entry, float x, float y)
    {
        _libraryManagePress = 1;
        _libraryManageDown = entry;
        _libraryManageStart = new Vector2(x, y);
        LibraryMarqueeActive = false;
        _libraryPending = null;
        _libraryPendingTitle = null;
        LibraryUseSelected = null;
        LibraryConfirm = "";
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
    }

    private void LibraryManagePressMove(float x, float y)
    {
        if (_libraryManagePress == 0) return;
        if (_libraryManagePress == 1)
        {
            if (Vector2.Distance(new Vector2(x, y), _libraryManageStart) < 4f * UiUnit) return;
            _libraryManagePress = 2;
            LibraryMarqueeActive = true;
        }
        var m = LibraryPanelInfoNow();
        LibraryAutoScrollForDrag(m, y);
        var rect = new RectF
        {
            MinX = MathF.Min(_libraryManageStart.X, x),
            MinY = MathF.Min(_libraryManageStart.Y, y),
            MaxX = MathF.Max(_libraryManageStart.X, x),
            MaxY = MathF.Max(_libraryManageStart.Y, y),
        };
        LibraryMarqueeRect = rect;
        var picked = new List<ShapeLibrary.Entry>();
        for (int i = 0; i < LibraryEntries.Count; i++)
        {
            var cell = LibraryLayout.CellRect(m, i, LibraryScrollY);
            if (!cell.Intersects(m.Grid)) continue;
            if (cell.Intersects(rect)) picked.Add(LibraryEntries[i]);
        }
        SetLibrarySelection(picked);
        _dirty = true;
    }

    private void LibraryManagePressEnd(float x, float y)
    {
        int state = _libraryManagePress;
        var down = _libraryManageDown;
        _libraryManagePress = 0;
        _libraryManageDown = null;
        LibraryMarqueeActive = false;
        Native.ReleaseCapture();
        if (state == 1)
        {
            if (down != null) ApplyLibrarySelection(down, !IsLibrarySelected(down));
            else SetLibrarySelection(Array.Empty<ShapeLibrary.Entry>());
        }
        _dirty = true;
    }

    // ---- 整理模式：拖把手排序 ----

    private void BeginLibraryManageSort(ShapeLibrary.Entry entry, float x, float y)
    {
        LibraryManageSorting = true;
        LibraryManageSortEntry = entry;
        LibraryManageDropBefore = null;
        LibraryManageDropIndex = -1;
        LibrarySelected.Clear();
        LibrarySelected.Add(entry);
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
        NotifyUiStateChanged();
        _dirty = true;
    }

    private void LibraryManageSortMove(float x, float y)
    {
        if (!LibraryManageSorting || LibraryManageSortEntry == null) return;
        var m = LibraryPanelInfoNow();
        LibraryAutoScrollForDrag(m, y);
        int moving = LibraryEntries.FindIndex(e => e.Path == LibraryManageSortEntry.Path);
        int idx = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);
        int drop;
        if (idx < 0)
        {
            drop = LibraryEntries.Count;
        }
        else
        {
            var cell = LibraryLayout.CellRect(m, idx, LibraryScrollY);
            bool after = x > (cell.MinX + cell.MaxX) * 0.5f;
            drop = idx + (after ? 1 : 0);
        }
        // 落回自己身上 = 不做任何事。
        if (drop == moving || drop == moving + 1) LibraryManageDropIndex = -1;
        else LibraryManageDropIndex = drop;
        LibraryManageDropBefore = drop < LibraryEntries.Count ? LibraryEntries[drop] : null;
        _dirty = true;
    }

    private void LibraryManageSortEnd()
    {
        var entry = LibraryManageSortEntry;
        int drop = LibraryManageDropIndex;
        LibraryManageSorting = false;
        LibraryManageSortEntry = null;
        LibraryManageDropIndex = -1;
        LibraryManageDropBefore = null;
        Native.ReleaseCapture();
        if (entry != null && drop >= 0)
        {
            var before = drop < LibraryEntries.Count ? LibraryEntries[drop] : null;
            if (before != null && before.Path == entry.Path) before = null;
            if (ShapeLibrary.ReorderBefore(new[] { entry }, before))
            {
                ReloadLibrary();
                SetInkStatus("顺序已更新");
            }
        }
        _dirty = true;
    }

    private void LibraryAutoScrollForDrag(in LibraryLayout.Info m, float y)
    {
        float margin = 24f * UiUnit;
        if (y < m.Grid.MinY + margin) LibraryScrollBy(-12f * UiUnit);
        else if (y > m.Grid.MaxY - margin) LibraryScrollBy(12f * UiUnit);
    }

    private void ToggleSelectAllLibrary()
    {
        if (LibraryEntries.Count == 0) return;
        if (LibrarySelected.Count == LibraryEntries.Count)
            LibrarySelected.Clear();
        else
            foreach (var e in LibraryEntries) LibrarySelected.Add(e);
        NotifyUiStateChanged();
        _dirty = true;
    }

    private void DeleteLibraryManage()
    {
        if (LibrarySelected.Count == 0) return;
        if (LibraryCategoryId == ShapeLibrary.TrashCategory
            && LibraryConfirm != "deleteselected")
        {
            LibraryConfirm = "deleteselected";
            SetInkStatus(Loc.T("再点一次：永久删除选中的 {0} 项", LibrarySelected.Count));
            _dirty = true;
            return;
        }
        DeleteLibrarySelection();
        LibraryConfirm = "";
    }

    private void ExitLibraryManage()
    {
        LibrarySelectMode = false;
        LibrarySelected.Clear();
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        LibraryManageSortEntry = null;
        LibraryConfirm = "";
        NotifyUiStateChanged();
        _dirty = true;
    }

    internal int LibraryDrawerVisibleCountForPaint => LibraryDrawerVisibleCount();
    internal bool LibraryRenameEditingForTest
        => TextInput?.IsEditing == true
           && string.Equals(TextInput.EditingId, "library.rename", StringComparison.Ordinal);
    internal void SetLibraryRenameTextForTest(string text) => _textInput?.SetTextForTest(text);
    internal void EndLibraryCategoryRenameForTest(bool save) => EndLibraryCategoryRename(save);
    internal bool LibraryManageSortingForPaint => LibraryManageSorting;
    internal int LibraryManageDropIndex;
    internal bool LibraryManageModeForTest => LibrarySelectMode;
    internal ShapeLibrary.Entry LibraryUseSelectedForTest => LibraryUseSelected;

    private void InsertLibraryGroupAt(IReadOnlyList<ShapeLibrary.Entry> entries, float x, float y)
    {
        if (entries == null || entries.Count == 0) return;
        var strokes = new List<Stroke>();
        foreach (var e in entries)
            if (e.Strokes != null) strokes.AddRange(e.Strokes);
        if (strokes.Count == 0) return;
        _libraryPending = strokes;
        _libraryPendingTitle = entries.Count == 1 ? entries[0].Title : $"{entries.Count} 项";
        TryInsertLibraryAt(x, y);
        SetInkStatus(entries.Count == 1
            ? $"已插入「{entries[0].Title}」"
            : $"已插入 {entries.Count} 项");
        // 图钉灭 = 放完这一张就让开（面板状态仍然保留，再点收藏格可回来）。
        if (!LibraryPanelPinned) CloseLibraryPanel();
        _dirty = true;
    }

    /// <summary>
    /// 改**配对期隐藏**这一位，并且**顺手让它对应的内容块失效**。
    ///
    /// ## 为什么必须有这个函数的"顺手失效"（这是用户 2026-09-25 报的 bug）
    ///
    /// 症状：画两支的时候，**停顿那一刻第一笔没消失，等抬手才消失**。
    ///
    /// 根因是**只改了字段、没让渲染缓存失效**（正是本仓库记过的一条老教训）：
    /// 内容层不是每帧重画、而是**分块位图缓存**（`CanvasTileCache`），第一笔这一刻
    /// **早就烘进块里**了；而块只在 `Doc.Version` 变化时才重光栅化（`Overlay.SyncTiles`）。
    /// 于是只置字段的话：`Overlay.DrawStroke` 确实会跳过它，但**没有任何一帧重画那一块**，
    /// 屏幕上原样留着旧像素 —— 一直留到抬手提交（那一步 `RemoveStroke` 抬了版本号，
    /// 块被重画），**看起来就是"松手以后才消失"**。
    ///
    /// 所以"藏"和"让那一块重画"必须**同时**发生 —— 这就是这个函数存在的全部理由，
    /// 两个调用点（藏 / 放回来）都要走它，**不要直接给字段赋值**。
    /// </summary>
    private void SetPairHidden(Stroke s, bool on)
    {
        if (s == null || s.HiddenForPairing == on) return;
        s.HiddenForPairing = on;
        // 模型本身一个字没改（这一笔还在文档原处、撤销栈照样靠它），
        // 只是告诉渲染层"这块要重来"（见 Document.InvalidateContent 的注释）。
        Doc.InvalidateContent(s.PaddedBounds);
    }

    /// <summary>
    /// **两笔配对**：这一笔和"上一笔"是不是同一个双曲线的两支（用户 2026-09-25 定的约定）。
    ///
    /// &gt; "**画两支就是双曲线，画一支就是抛物线**。不管它实际上是抛物线还是双曲线，
    /// &gt;  我们只要按照这个来区分。"
    ///
    /// 为什么这一刀比"判形状"干净：形状判断是**连续、有噪声**的（夹角、残差都栽在这儿 ——
    /// 真实手抖下双曲线只剩 25~40%），而"**画了几笔**"是**离散、零噪声**的：老师画两支的时候，
    /// **他自己知道**在画双曲线。所以族的选择**不再交给拟合**，拟合只负责"造一个像的"。
    ///
    /// 认出来 → 返回那个双曲线，并把"上一笔"钉在 <see cref="_twoBranchPrev"/> 上
    /// （抬手提交时**一起收走**，见 `EndStroke`）；认不出 → `IsNothing`，
    /// **这一笔照原来的单笔路走**（绝不抢活）。
    ///
    /// 挑"上一笔"的四条，都很保守 —— 因为**误配的代价是"把两笔变成一个"**：
    ///   ① **文档里最后一笔**（不往前找更早的，那会开始猜）；
    ///   ② 在 **20 秒**窗口内（见 <see cref="TwoBranchWindowMs"/>）；
    ///   ③ 是**手绘的墨**（`Freehand`）—— 已经成型的对象不参与；
    ///   ④ 两边点数都够，最后交给 `TryTwoBranchHyperbola` 判**中心对称**。
    /// </summary>
    private ShapeGuess TryTwoBranchPair(Stroke ink, Vector2[] pts)
    {
        if (Doc.Strokes.Count == 0) return ShapeGuess.None("没有上一笔");
        if (NowMs - _lastInkEndMs > TwoBranchWindowMs) return ShapeGuess.None("上一笔太久");
        var prev = Doc.Strokes[^1];

        // **诊断日志**（用户 2026-09-25 上手"一个也画不出来"）：
        // 一行说清"上一笔什么样、这一笔什么样、卡在哪一道闸"。
        // 真机排查全靠它 —— 否则只能靠猜，而这条路已经猜错好几轮了。
        // 限流：最多每 500ms 一行（这个方法在停顿期间每 40ms 走一次）。
        bool log = NowMs - _pairLogMs > 500;
        if (log) _pairLogMs = NowMs;

        ShapeGuess g;
        // **"第一支"用哪一串点**：正常就是那一笔手绘的墨；
        // 但要是它**刚被停顿成型过**（收笔前手停了一下），对象里只剩 2~3 个定义点，
        // 这里就用**当初那一笔原迹**（引用比较确认它还是文档里最后一笔，见字段注释）。
        // 拿不到就置 null（= 这一笔不能当"第一支"用）。
        var prevInk = prev;
        if (prev.Kind != StrokeKind.Freehand)
            prevInk = prev == _lastDwellShape && _lastDwellInk != null ? _lastDwellInk : null;

        if (prevInk == null)
        {
            // ⚠ 这一条**很容易中**：第一支要是也停顿过（那 400ms 的静止），
            //   它就**已经变成一个对象**了，而对象的 `Points` 只剩定义点、
            //   **拿不回原来那些采样点**，于是配不成对。日志里会打出 `上一笔 Parabola`。
            //   （2026-09-26 起：**刚停顿成型的那一笔**能从 `_lastDwellInk` 拿回原迹，
            //    所以只有"更早的、别处来的对象"才会走到这里。）
            g = ShapeGuess.None("上一笔不是手绘的墨（它已经被成型过了）");
        }
        else if (prevInk.Points.Count < 8 || pts.Length < 8)
        {
            g = ShapeGuess.None("有一笔太短");
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
                // ★ **幽灵期先把第一笔藏起来**（用户 2026-09-25 定：
                //   "识别的那一刻，第一笔就该消失"）—— 原来要等抬手提交才消失。
                //   ⚠ 只是**不画**（`Overlay.DrawStroke` 会跳过），文档一个字没动 ——
                //     撤销栈要靠它还在原处。
                SetPairHidden(prev, true);
            }
        }

        if (log)
            Console.WriteLine($"[两笔配对] {(g.IsNothing ? "否" : "**是双曲线**")}：{g.Rule}"
                              + $"（上一笔 {prev.Kind}·{prevInk?.Points.Count ?? 0} 点"
                              + $"{(prevInk != null && prevInk != prev ? "（用它当初的原迹）" : "")}，"
                              + $"这一笔 {pts.Length} 点）");
        return g;
    }

    private void EndStroke()
    {
        InputBlackBox.End(NowMs);   // 黑匣子：收笔（含"每笔收到即落盘"）
        _lastStrokeEndMs = NowMs;   // 板书历史："手停了没有"的判据（见 MaybeHistorySnapshot）
        // 触摸手势的兜底中断（8.4.0）：正常收笔时 `_touchMode` 已经是 None（触摸那条自己收过），
        // 只有"丢捕获 / 意外路径"会带着没结束的模式走到这里——把开了头的（擦除批次、选中拖动）收干净。
        TouchAbort();

        // 激光笔抬手：这条**开始计时**（停留 2 秒后再整体淡出，见 `LaserTrail.HoldMs`）。
        // 放在最前面：下面那几条分支（截屏 / 图形 / 多笔）都和激光笔无关，不必等它们；
        // 而且**每一条收笔路径都要走到**（正常抬手、丢捕获都走 `EndStroke`）——
        // 漏一次的话那条轨迹就永远是"还在写"，既不淡也不会消失。
        Laser.Release(NowMs);

        if (ScrollBarDragging) EndScrollBarDrag();
        // `--syswet`：湿墨只有系统轨迹这一份，撤除要等干墨那一帧 Present 之后
        //（否则"轨迹先没了、干墨还没上屏"会闪一帧）；其余路径照旧立即撤。
        bool sysWetEnding = _systemWetStroke != null;
        foreach (var w in _windows)
            if (sysWetEnding) w.EndInkTrailDeferred(); else w.EndInkTrail();
        _systemWetStroke = null;
        // **录墨迹**（用户 2026-09-26 提"我手画多少条双曲线给你，你按这些来定制判据"）：
        // 把这一笔的**原始采样点**追加到录制文件。放在最前面 —— 此时 `ActiveStroke`
        // 要么还是原始墨、要么是停顿成型换掉的那个对象（所以真正要用的点见 `_recordPts`）。
        FlushInkRecord();
        // **抬手就关掉停顿那颗定时器**：它只在"有笔在写"的时候有意义（见 StartDwellTimer）。
        StopDwellTimer();
        // [删除 2026-10-05] 收笔时清"预测尾"：随老预测系统一起移除（渲染尾已不存在）。
        Doc.EndErase();
        // 面积擦抬手：框回基准尺寸（悬停时显示的 = 下次按下去第一下擦掉的那一块）。
        PixelEraseDragging = false;
        if (CaptureActive)
        {
            // 起框那一次松手 → 进调整阶段；调整里拖手柄那几次松手 → 只收手。
            if (!CaptureAdjusting) EndCapture();
            else EndCaptureAdjustDrag();
            _capturePressHandled = false;
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 确认/取消那一下抬手：截图已经收场，别再落到"框选"那条路上
        //（不然 Tool=Marquee 会让它顺手把刚落下的图取消选中）。
        if (_capturePressHandled)
        {
            _capturePressHandled = false;
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 记下"**这一笔到此结束**"的时刻 —— 两笔配对（画两支 = 双曲线）要用它算时间窗口
        // （见 `TwoBranchWindowMs`，现在是 20 秒）。
        // ⚠ 放在这里（**截屏那条分支之后**）：`CaptureActive` 那条路也走 `EndStroke`，
        //   截一次屏不该把两笔的窗口冲掉。
        _lastInkEndMs = NowMs;

        if (ActiveStroke != null)
        {
            // **多笔图形**：松手 = **这一笔完成**（照 InkClass：推进就发生在 MouseUp，
            // 见 `MW_ShapeDrawing.cs:1814-1822` 的 `drawMultiStepShapeCurrentStep`）。
            // 推进一步之后再看"还有没有下一笔"。
            //
            // 注意这里**不以"拖了多远"为准**：多笔图形的每一笔定的是**一个几何量**
            //（渐近线框 / 曲线过哪），"在目标位置点一下"和"拖过去"一样有效 ——
            // 所以不拿 ShapeDragLongEnough 去卡它（那是给一笔成形的图形防误点用的）。
            if (_stepPlan != null) _stepIndex++;

            // 松手就把"**这一笔吸到了什么**"收回去（「直棱柱」那颗胶囊只在拖动中出现，
            // 和拖顶点吸附那边同一个口径：松手就没有了）。不清的话它会一直挂在屏幕上。
            _stepSnap = ShapeSnapKind.None;

            // 还有下一笔 → 这一下松手**不算完**：半成品停在 ActiveStroke 里
            //（**不进文档、也不写撤销记录**），等最后一笔松手再统一提交。
            // 这样在用户眼里这几笔是连着的，中途不会先冒出一条"半成品"曲线、再把它改掉，
            // 撤销也不会多出好几条记录（InkClass 同样把两笔塞进同一个提交里，`:1846-1850`）。
            bool stepPending = _stepPlan != null && _stepIndex < _stepPlan.Apply.Length;

            // 图形：拖动太短 = 误点，**不提交**。什么都不留（连撤销记录都不留）——
            // 一个退化的图形（零长度直线、零面积矩形）在画面上看不见，却能被点中、
            // 能被框选，是最难解释的一类杂物。
            //
            // **多笔图形**没有"拖多长"可依（见上），所以改看**定义元素齐没齐**：
            // 双曲线少一个"曲线经过的点"就等于什么都没定下来（`MinCurvePoints` 说它要三个点），
            // 这种半成品不提交。尺寸下限在各自的算式里已经卡死
            //（`SetHyperbolaFromAsymptote`），退化不成"看不见却占着一条对象"。
            bool commit = !stepPending
                       && ActiveStroke.Points.Count > 0
                       && (_stepPlan != null
                           ? ActiveStroke.Points.Count >= Stroke.MinCurvePoints(ActiveStroke.Kind)
                           // 三种来源分三档：
                           //   · 自由笔迹：不卡长度（它本来就是一笔一画）；
                           //   · **停顿变出来的图形**：也不卡长度 —— 识别器已经验收过它
                           //    （总长 ≥ 40 逻辑像素、每条边都贴得住墨，见 ShapeRecognize）；
                           //     而**这条门槛量的是图形、不是墨**（"定义元素首末两点够不够远"），
                           //     对"定义元素天生就短"的图形会误杀，一误杀就是**图形和手绘原迹
                           //     一起被丢掉**（用户 2026-09-24 报的"图形整个会消失掉"就是这一类）。
                           //     ⚠ 今天这一档**够不到**：识别器的"总长 ≥ 40 逻辑像素"把定义元素的
                           //     尺度顶在了这条门槛之上（圆：r ≥ 6.4 逻辑像素 > 4）。留着它是防
                           //     "以后加一个定义元素很小的图形"（比如一个"点"）——那时它会立刻用上，
                           //     而它挡的是**用户的墨凭空消失**，不是省一行代码的事；
                           //   · 图形工具：照旧要"拖够长"（防误点、防画出退化的零面积图形
                           //     ——那种东西看不见却点得中，是最难解释的一类杂物）。
                           : (!IsShapeTool(ActiveStroke.Tool) && _dwellInk == null)
                             || _dwellInk != null
                             || ShapeDragLongEnough(ActiveStroke));

            // **取消选中那一击不留墨**（用户 2026-09-23 定）。
            //
            // 起因：停顿变出来的图形是自动选中的，而"点一下别处"的意图是收起那个框。
            // 那一击原本会落**一个实心圆点**（`Stroke.IsSinglePoint`），于是"取消选中"
            // 这个纯粹的动作会在屏幕上留下墨——**每次取消都脏一块**，板书时特别烦。
            // 判据是"整段位移 ≤ DwellTapSlopLogical"，所以真画一笔绝不会被吃掉。
            //
            // ⚠ **停顿成型那一笔不在这条里**（`_dwellInk == null` 才算）：它既然被识别器
            //   认出来了，就绝不可能是"点一下"（识别器要总长 ≥ 40 逻辑像素）；而这里量的是
            //   **图形**的首末两点 —— 圆的定义元素是（圆心，圆周点），首末两点距离就是**半径**，
            //   半径小于点选容差（8 逻辑像素）的圆会正好落进来，整笔（图形 ＋ 手绘原迹）
            //   被当成"点一下"抹掉。而这条**够得着**：圆的识别门槛是 2πr ≥ 40 → r ≥ 6.4 逻辑
            //   像素，所以 6.4~8 这一段的圆真会被误杀（`--dwelltest` H4 用半径 7.5 逻辑像素的圆
            //   钉住；把那半句删掉它当场红："整笔没了"）。
            if (commit && _dwellInk == null && _dismissTapArmed && !IsShapeTool(ActiveStroke.Tool)
                && _stepPlan == null && DwellTapLeaveNoInk(ActiveStroke))
            {
                commit = false;
                Console.WriteLine("[停顿成型] 这一下只是取消选中，不留墨");
            }

            // 这一次提交的是不是一个**刚成型的图形**（要不要自动选中它，见下面"画完自动选中"）
            Stroke committedShape = null;
            if (commit)
            {
                // **双曲线的渐近线按档收口**（用户 2026-09-22："化的时候是都有渐近线，
                // 但是最终显示没有"）：画的过程中一直是"有"（那是向导，见 BeginShapeAt），
                // 交到文档里的这一刻才按面板那一档决定 —— 于是"无渐近线"那一档
                // 画的时候照样看得到那两条虚线，画完就只剩曲线。
                // ⚠ 必须走 `SetShowAsymptotes`（它会抬 Revision）——直接给字段赋值的话，
                // 屏幕上那份"带渐近线"的辅助几何缓存在那儿不重建，画完照样看得见那两条虚线
                //（用户 2026-09-22 报的就是这个："我选择的不带渐近线的，但是画完以后还有渐近线"）。
                if (ActiveStroke.Kind == StrokeKind.Hyperbola)
                    ActiveStroke.SetShowAsymptotes(HyperbolaAsymptotes);
                if (_dwellInk != null)
                {
                    // 停顿成型：这一笔不再是"正在写"（曲线化恢复生效）。即使定型成的是
                    // 自由笔迹（`_dwellInk` 那条路），也不该再带着"活笔"的标志。
                    ActiveStroke.RawWhileLive = false;
                    // **停顿成型**：走"替换型"提交——图形进文档，手绘原迹跟着撤销栈走，
                    // 于是按一次 Ctrl+Z 回到**自己画的那一笔**（见 DwellShapeAction）。
                    //
                    // ★ **两笔成型**（`_twoBranchPrev != null`，用户 2026-09-25 定的
                    //   "**画两支就是双曲线**"）：这时**上一笔也要收走** ——
                    //   `RemoveStroke` 是"效果先应用"，记账全由下面那条动作负责
                    //   （它带两笔原迹，所以 **一步 Ctrl+Z 回到两笔手绘**）。
                    if (_twoBranchPrev != null)
                    {
                        var prev = _twoBranchPrev;
                        int prevIdx = _twoBranchIndex;
                        _twoBranchPrev = null;
                        _twoBranchIndex = -1;
                        // ⚠ **必须先把隐藏位清掉**：不清的话 Ctrl+Z 把这一笔放回来时
                        //   它还是隐身的 —— 屏幕上"撤销之后什么都没回来"，而且再也变不回来 ✗
                        //   （走 `SetPairHidden` 而不是直接赋值：它会把"那一笔原来待的块"
                        //     一起标脏，见那个函数的注释）
                        SetPairHidden(prev, false);
                        Doc.RemoveStroke(prev);
                        Doc.AddDwellShape(ActiveStroke, _dwellInk, prev, prevIdx);
                    }
                    else
                    {
                        Doc.AddDwellShape(ActiveStroke, _dwellInk);
                        // **把"这一支的原迹"留一个引用**（见 `_lastDwellShape` 那段注释）：
                        // 画双曲线的第一支时手要是停了一下，它就先变成了一个**抛物线对象**，
                        // 而对象里拿不回采样点 → 两笔配对就断了。只认**抛物线**这一种：
                        // 那是"画一支双曲线被误判成抛物线"的真实情形；别的种类（圆 / 椭圆…）
                        // 不在这里放行 —— 它们的形状和"一支双曲线"差太远，误配的代价更大。
                        if (ActiveStroke.Kind == StrokeKind.Parabola)
                        {
                            _lastDwellShape = ActiveStroke;
                            _lastDwellInk = _dwellInk;
                        }
                    }
                    _dwellCommitted = true;
                    // **直线抬手后不选中**（用户 2026-09-23 定）：直线是"顺手一划"，画完要立刻接着
                    // 写下一笔——弹出一个选中框 + 操作条反而挡路；按住期间已经能调（转向/伸缩），
                    // 抬手之后想再调就再点它一下（和 GoodNotes"松手后点一下才选中"同一个手感）。
                    // ⚠ 这一条例外**只在这条路上**：图形工具画的直线照旧自动选中（那是既有设计）。
                    if (ActiveStroke.Kind != StrokeKind.Line) committedShape = ActiveStroke;
                    Console.WriteLine($"[停顿成型] 定型 {ActiveStroke.Kind}"
                                      + (committedShape != null ? "（已选中；Ctrl+Z 可回手绘）" : "（未选中；Ctrl+Z 可回手绘）"));
                }
                else
                {
                    // 落笔：从这一刻起曲线化生效（几何缓存键里带着这个标志，会自己重画）
                    ActiveStroke.RawWhileLive = false;
                    Doc.AddStroke(ActiveStroke);
                    // 真提交进文档了才算"成型"——下面那一步要拿它做自动选中。
                    if (IsShapeTool(ActiveStroke.Tool)) committedShape = ActiveStroke;
                }
                // **先结账再打印**：这一段（含 AddStroke 的快照/缓存收拾）才是分配最集中的地方，
                // 晚一步收仪表，印出来的就是上一笔的数字。
                EndStrokeMeasure();
                // 这一笔的**历时 + 真实采样率**（2026-10-07 加，10-07 晚修正口径）：
                // "收到 12 个点"本身说明不了任何事——12 个点摊在 0.1 秒上是 120Hz（正常），
                // 摊在 0.8 秒上就只有 15Hz（异常）。排查"点是不是被稀疏了"必须先有这个分母。
                //
                // ⚠ **口径必须是"采样点/秒"，不是"消息/秒"**：开 Windows Ink 时系统会把
                //   2~3 个硬件采样合并进一条消息（真机实测 12 条消息 → 31 个采样点），
                //   拿消息数当频率会**把笔的采样率报低一半以上**（第一版就是这么写的）。
                //   同时把合并比打出来——它本身就说明"这一路到底有没有合并可恢复"。
                // 普通手写用第一个点的采样时间；形状识别 / 图形工具的点 T 是 0，
                // 退回按下时刻，否则会印出“历时 28 分钟”这种假数（2026-10-19 学校日志）。
                double pointT = ActiveStroke.Points.Count > 0 ? ActiveStroke.Points[0].T : 0;
                double strokeStart = pointT > 0 ? pointT : _measureStartMs;
                double strokeMs = Math.Max(0, NowMs - strokeStart);
                double sampleHz = strokeMs > 1 ? LastCoalescedSamples * 1000.0 / strokeMs : 0;
                double msgHz = strokeMs > 1 ? LastCoalescedMessages * 1000.0 / strokeMs : 0;
                string merge = LastCoalescedMessages > 0
                    ? (LastCoalescedMessages == LastCoalescedSamples ? "1:1（系统没合并）"
                       : $"{(double)LastCoalescedSamples / LastCoalescedMessages:F1}:1（合并历史已恢复）")
                    : "无";
                // **这一笔真正写进点里的压力区间**（2026-10-07 加）。
                //
                // 为什么必须有它：那天我连着犯两次同类的错 ——
                //   ① 兜底值取 0.5 → 开头鼓粗点
                //   ② raw 补点写成 0 → 开 ink 时整条线全是细口子（用户："笔记很脏"）
                // 两次都**只有一个数字能看出来**：点的压力最小值/最大值。
                // 光看"采到的压力"那一栏是不够的 —— **采到了**和**用上了**是两件事，
                // 中间任何一段出错它都看不见。
                float ptMinP = 0f, ptMaxP = 0f;
                int ptSteps = 0;                       // 压力"跳变"次数（相邻点压力不同的次数）
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
                    $"路径={DescribeInputPath(_activePointerType)}"
                    + $"　采集到 {ActiveStroke.Points.Count} 个点"
                    + $"，历时 {strokeMs:F0} ms → 采样 {sampleHz:F0} Hz（消息 {msgHz:F0} Hz）"
                    + $"，收到 按下{_cntDown} 移动{_cntMove} 抬起{_cntUp} 丢失捕获{_cntCaptureLost}"
                    + $"，设备={PointerTypeName(_activePointerType)}"
                    + $"，压感={(ActiveStrokeHasPressure ? "有" : "无")}"
                    // 真正落到点里的压力区间。**有压感但 min 是 0** 就说明有路把点写成了 0
                    // （2026-10-07 那个"笔记很脏"的 bug 正是这样：raw 补的点全被写成 0）。
                    + (ActiveStroke.Points.Count > 0
                        ? $"，点压力 {ptMinP:F2}~{ptMaxP:F2}（跳变 {ptSteps}/{ActiveStroke.Points.Count} 点）"
                          + (ActiveStrokeHasPressure && ptMinP <= 0.001f ? " **有0！疑似有路把它写成0**" : "")
                        : "")
                    + $"，合并 {merge}（{LastCoalescedMessages} 条消息 → {LastCoalescedSamples} 点）"
                    // 委托轨迹（2026-10-11）：**喂进去 ≠ 留在系统里**——默认模式下我们自己
                    // 画到哪就把轨迹结账到哪，所以"结账"应当≈"喂"；只喂不结账（结账远小于喂）
                    // 就是"长笔画中途变透明"的那种病。`--trailonly` 下我们自己不画、也就不结账
                    //（那正是"湿墨只剩系统轨迹"的诊断模式，结账 0 是预期）。
                    + (OverlayWindow.InkTrailEnabled
                        ? $"，[轨迹 喂{OverlayWindow.InkTrailPointsFed - _trailFedSeen}"
                          + $" 结账{OverlayWindow.InkTrailPointsRemoved - _trailRemovedSeen}]"
                        : "")
                    + (RawProbeEnabled
                        // 时长≈0 的空笔（退出收尾结算）不报频率：`条数 / 1ms` 会印出
                        // "14000 Hz" 这种假数（2026-10-08 用户两份数据结尾都有）。
                        ? $"，[原始输入 {_rawReportsThisStroke} 条 → " + (strokeMs > 1
                            ? $"{_rawReportsThisStroke * 1000.0 / strokeMs:F0} Hz]"
                            : "—（时长≈0）]")
                        : "")
                    + (RawInputCapture
                        ? $"，[raw 补点 +{_rawPointsAdded}]"
                          + (RawProbeEnabled
                             ? $"[挡：无笔{_rawSkipNoStroke} API{_rawSkipApi} 类型{_rawSkipType} 绝对{_rawSkipAbs} 零移{_rawSkipZero}"
                               + $" 非手写{_rawSkipKind} 门{_rawSkipGate} 无锚{_rawSkipAnchor} 无中间点{_rawSkipNoInter} 上限{_rawSkipCap} 太近{_rawSkipNear}]"
                             : "")
                        : "")
                    // [删除 2026-10-05] 预测器/预测尾/喂 DWM 段数的日志：随预测系统一起移除。
                    // 分配与 GC：低配机排查"偶发卡顿"的**唯一依据**。
                    // 第 2 代那一位出现在书写期间，就说明这一笔画到一半被全堆回收打断过。
                    + $"，分配 {StrokeAllocBytes / 1024.0:F1} KB（整笔会话累计，非驻留）/GC {StrokeGc0}/{StrokeGc1}/{StrokeGc2}";
                Console.WriteLine("[笔画] " + _lastStrokeReport);
                // `--strokefile`：把这一行**追加到文件**。
                // 为什么要有它：这一行的数字（尤其"压力 a~b/上限"）是调手感参数的**唯一依据**，
                // 而截图会折行、会被笔迹挡住、会转述错——**今天已经因此栽了三次**。
                // 默认关（不写文件、零开销），要诊断时才开。
                if (_strokeFilePath.Length > 0)
                {
                    try
                    {
                        System.IO.File.AppendAllText(_strokeFilePath,
                            $"[{DateTime.Now:HH:mm:ss}] {_lastStrokeReport}{Environment.NewLine}",
                            new System.Text.UTF8Encoding(true));
                    }
                    catch (Exception ex) { Console.WriteLine($"  ⚠ 写笔画日志失败：{ex.Message}"); }
                }
                if (StrokeGc2 > 0)
                    Console.WriteLine($"  ⚠ 这一笔期间发生了 {StrokeGc2} 次第 2 代 GC"
                                      + "（低配机上这就是一次可见的卡顿，值得查是哪里在分配）");
            }
            else if (!stepPending && IsShapeTool(ActiveStroke.Tool) && _stepPlan == null)
            {
                Console.WriteLine($"[图形] 拖动太短（< {ShapeMinDragLogical:F0} 逻辑像素），没提交");
            }
            // 多笔式没走完：**连 ActiveStroke 都不清**——它还要接着给下一笔当预览用；
            // 表和"画到第几笔"也跟着留在原地（下一笔按下时要用）。
            if (!stepPending)
            {
                ActiveStroke = null;
                _stepPlan = null;
                _stepIndex = 0;
            }

            // **画完自动选中**（用户 2026-09-22 定）：图形一成型就把它的选中框亮出来，
            // 老师可以立刻拖它、拖顶点改形状——不用先去点"框选"再回头点它一下。
            // （截图和粘贴早就是"落下就选中"，这条把图形也拉齐了。）
            //
            // **工具不换**（这一点和截图 / 粘贴不同，它们换成了框选，因为"接着画下一个"
            // 对它们不存在）：手里还是原来那个图形工具，所以"这一下算动它还是算接着画"
            // 的分流落在 `OnPointerDown`（`AutoSelectionPress`）。
            //
            // 而且刚画完是**收起来**的形态（一颗圆钮），点一下它才展开成常规那一套
            // ——理由见 `_autoSelCollapsed`（用户上手之后定的：一整条摊在图形下面又重又挡）。
            // **面板选的一次性图形：画完一个就回到上一次的非图形工具**（和收藏插入后
            // 回到原工具同一套直觉）。直接在主条选的图形工具不在此列，保持原来“接着画”。
            bool oneShotShape = _shapeOneShot && committedShape != null;
            if (oneShotShape)
            {
                _shapeOneShot = false;
                var back = _shapeOneShotReturn;
                if (back == Tool.Capture || back == Tool.Laser) back = Tool.Pen;
                // 画完先选中；工具切到框选，点空白时沿用收藏插入那一套“回到上一个工具”。
                SwitchTool(Tool.Marquee);
                Doc.SelectOnly(new[] { committedShape });
                _autoSelCollapsed = false;
                _toolBeforeLibraryInsert = back;
                _restoreToolAfterLibrarySelection = true;
                _dirty = true;
                NotifyUiStateChanged();
            }
            else if (committedShape != null)
            {
                Doc.SelectOnly(new[] { committedShape });
                _autoSelCollapsed = true;
                _dirty = true;
            }
            // **停顿变出来的那个框，工具还是笔** —— 所以还要记一笔"这一次选中
            // 是停顿给的"，否则那个框会"画得出来、点不着"（见 `_dwellSelected`）。
            // 直线上这一位是 false（它抬手不选中，见上面）：没有选中框，也就没有"点不着"的问题。
            _dwellSelected = committedShape != null && _dwellCommitted;
        }
        // 停顿成型这一笔的账在这里结清：状态机复位、原迹引用放手
        //（原迹已经交给撤销栈了，见 DwellShapeAction —— 这里放手不会丢东西）。
        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        // 拖动**手柄**（改几何）的收尾：**和当前工具无关**。
        //
        // 挂在下面"选择工具"那条分支里是不够的：手柄拖动属于**选中**那一套，
        // 而松手时手上的工具可能已经不是选择工具了（拖到一半按热键换了工具，
        // 见 SwitchTool——它只作废多笔图形的半成品，不动手柄拖动）。
        // 那种情况下这一拖会**永远不提交**：屏幕上拖得好好的，松手一看 a 一点没变
        //（自检当场抓出来过）。
        //
        // ⚠ 判据是 `SelDragging` 而**不是**"是不是框选工具"：画完自动选中那个框
        //（见上面 `AutoSelectionPress` 那段）是在**图形工具**下起手拖的，
        // 按工具判的话这一拖同样永远不提交。
        // 文本框的双击编辑**等松手再兑现**：按下去先交给选择手势，
        // 拖过就是移动，没拖才是双击。必须在 EndSelDrag 之前读 `_selDragMoved`，
        // 它在收尾里会被清掉。
        bool pendingTextEdit = _pendingTextEdit && SelDragging
                            && !_selDragMoved && _dragHandle == SelHandle.None;
        bool wasSelDragging = SelDragging;
        if (wasSelDragging) EndSelDrag();
        if (wasSelDragging)
        {
            if (pendingTextEdit) EnterPendingTextBoxEdit();
            else _pendingTextEdit = false;
        }
        else if (_sliderDragging) EndWidthSliderDrag();        // 粗细滑条：松手 = 一步撤销
        else if (_colorDragging) EndColorPickerDrag(PointerX, PointerY);   // 取色板：板内应用 / 板外取消
        else if (Tool == Tool.Marquee || MarqueeActive) ApplyMarquee();    // MarqueeActive：触摸长按那条路（工具可能还是笔）
        if (!wasSelDragging) _pendingTextEdit = false;
        EndStrokeMeasure();      // 兜底：没收过的分支（取消、切换工具等）也把总账结掉
        _drawing = false;
        _dirty = true;
        _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
        // 手测台：一次拖拽收尾。**必须在 EndErase 之后**——撤销栈深度要算上刚提交的那一步。
        EraserTelemetry?.EndDrag(Doc, Doc.UndoDepth, NowMs);
    }

    private static string PointerTypeName(uint t) => t switch
    {
        Native.PT_MOUSE => "鼠标",
        Native.PT_TOUCH => "触摸",
        Native.PT_PEN => "笔",
        _ => "未知",
    };

}
