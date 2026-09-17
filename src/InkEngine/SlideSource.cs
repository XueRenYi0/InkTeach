namespace InkEngine;

/// <summary>
/// "幻灯片现在放到第几页"这一刻的样子。**这是接 PPT 唯一的输入**——
/// 页的所有者始终是我们（对象自己带归属），这里只是"现在该显示哪一页"。
/// 设计依据：调研-对接PPT.md 第三节。
/// </summary>
internal readonly struct SlideState
{
    /// <summary>正在放映。</summary>
    public bool Showing { get; init; }

    /// <summary>第几页（**1 起**，放映顺序）。</summary>
    public int Position { get; init; }

    /// <summary>共几页。</summary>
    public int Count { get; init; }

    /// <summary>
    /// 这一页的**身份**（PowerPoint 的 `Slide.SlideID`；0 = 拿不到）。
    /// 用它而不是页号：在 PPT 里调换页序，SlideID 不变、页号会变——
    /// 用页号当身份，批注就会跟着错位（InkClass 的坑，见 调研-对接PPT.md 第二节）。
    /// </summary>
    public long SlideId { get; init; }

    /// <summary>这一叠演示文稿的身份（全路径；拿不到就给个兜底的名字＋页数）。</summary>
    public string DeckKey { get; init; }

    public bool Equals(in SlideState o)
        => Showing == o.Showing && Position == o.Position && Count == o.Count
        && SlideId == o.SlideId && DeckKey == o.DeckKey;
}

/// <summary>
/// **"现在是第几页"的来源**。存在的意义：把 PowerPoint / WPS 隔在**一个实现**后面，
/// 于是：
///   · 整套页逻辑（归属、切页、清空本页、跨页撤销、落盘）**不依赖 Office** 就能自检
///     （用 <see cref="FakeSlideSource"/>）；
///   · 将来接 WPS / 别的课件软件 = 再写一个实现，上层一行不改。
/// </summary>
internal interface ISlideSource
{
    /// <summary>拿这一刻的状态。返回 false = 现在没有在放映（或拿不到）。**不许抛异常**。</summary>
    bool TryGetState(out SlideState state);

    /// <summary>从我们的面板翻页。返回 false = 这一次没翻成（到头了 / 拿不到）。</summary>
    bool Next();
    bool Previous();

    /// <summary>借它自己的"幻灯片导航"来跳页（可选，照 InkClass 的思路）。</summary>
    bool ShowNativeNavigator();

    /// <summary>给人看的名字（日志里用）。</summary>
    string Name { get; }
}

/// <summary>
/// **自检用的假幻灯片源**：内存里一个整数，能"翻页"。
///
/// 为什么值得单写一个：真 PowerPoint 只在装了 Office 的机器上才有，而我们的自检
/// 要在任何机器（以后的 CI）上跑。假实现让"页变了 → 屏幕上的墨跟着换"这条链
/// 完全可测，真 COM 那一层缩成"能不能读到这三个数"。
/// </summary>
internal sealed class FakeSlideSource : ISlideSource
{
    public string Name => "假放映（自检用）";

    public bool Showing = true;
    public int Position = 1;
    public int Count = 3;
    public string Deck = @"C:\自检\演示文稿.pptx";

    /// <summary>每一页的身份：假实现里就用一个大偏移 + 页号（**故意和页号不同**，
    /// 这样"按身份归属"这件事在自检里真的被验到，而不是恰好和页号相等）。</summary>
    public long SlideIdOf(int position) => 900000 + position;

    public bool TryGetState(out SlideState state)
    {
        state = new SlideState
        {
            Showing = Showing,
            Position = Position,
            Count = Count,
            SlideId = Showing ? SlideIdOf(Position) : 0,
            DeckKey = Showing ? Deck : null,
        };
        return Showing;
    }

    public bool Next()
    {
        if (!Showing || Position >= Count) return false;
        Position++;
        return true;
    }

    public bool Previous()
    {
        if (!Showing || Position <= 1) return false;
        Position--;
        return true;
    }

    public bool ShowNativeNavigator() => false;
}
