namespace InkUi;

/// <summary>命中目标的类别。只用于“点到了什么”，不参与绘制。</summary>
public enum HitKind
{
    None = 0,
    Inside,
    PageBack,
    PageClose,
    SettingsProfile,
    SettingsChip,
    SettingsNav,
    SettingsRow,
    SettingsScroll,
    HelpTab,
    HubTile,
    BottomTile,
    LogButton,
    ToolCell,
    BandSegment,
    PopupItem,
    Unknown,
}

/// <summary>
/// 稳定命中目标：用 ActionId 表达“点到了哪条动作”，不再用 `60 + 下标`。
/// 旧整数命中码由 <c>MoreHitAt</c> 兼容层映射，双跑通过后再删。
/// </summary>
public readonly record struct HitTarget(HitKind Kind, string ActionId, int Index = -1)
{
    public static readonly HitTarget None = new(HitKind.None, "", -1);

    public bool IsNone => Kind == HitKind.None;
}
