using InkEngine;

namespace InkTeach;

/// <summary>
/// PPT 联动的纯逻辑：原生 CLSID 识别、IDispatch 参数倒序、按页文件的"有没有"判断。
/// 不碰 COM、不开放映——真机那条路由 `--pptprobe` / 用户实测确认。
/// </summary>
internal static class UnitTestsPpt
{
    /// <summary>
    /// 微软 PowerPoint 原生 Application CLSID 必须**不看注册表**就认出来：
    /// WPS 劫持 `PowerPoint.Application` 后，按 ProgID 反查会得到 WPS 的 CLSID，
    /// 已开着的微软 PowerPoint 就认不出（症状：先开 WPS 再关、再开 PowerPoint 无翻页条）。
    /// </summary>
    [UnitTest]
    internal static void PptComSource_NativePowerPointMoniker_IsRecognized()
    {
        Check.True(PptComSource.IsAppMoniker(
            "!{91493441-5A91-11CF-8700-00AA0060263B}", out string progId),
            "原生 PowerPoint CLSID 没被认成 Application");
        Check.Equal("PowerPoint.Application", progId);

        // 大小写不敏感（moniker 字符串大小写由系统给，不可依赖）
        Check.True(PptComSource.IsAppMoniker(
            "!{91493441-5a91-11cf-8700-00aa0060263b}", out _),
            "原生 CLSID 小写形态没认出来");

        Check.True(!PptComSource.IsAppMoniker(
            "!{00000000-0000-0000-0000-000000000000}", out _),
            "无关 CLSID 被误认成 PPT Application");
    }

    /// <summary>
    /// IDispatch 的 `rgvarg` 是**倒序**：`GotoSlide(Index, ResetSlide)` 排进去时
    /// 第 0 个必须是 ResetSlide。排反了动画重置参数就丢（或不生效）。
    /// </summary>
    [UnitTest]
    internal static void ComLate_TwoArgOrder_IsReversed()
    {
        Check.Equal((7, 3), ComLate.ReverseTwoArgs(3, 7));
        Check.Equal((ComLate.MsoTrue, 5), ComLate.ReverseTwoArgs(5, ComLate.MsoTrue));
        Check.Equal(-1, ComLate.MsoTrue);
    }

    /// <summary>
    /// 空页翻页"零磁盘 IO"的前提：`HasPageFile` 必须准确回答"盘上有没有这一页"。
    /// 没有 → 翻页时跳过删除（不去碰慢盘 / 杀软扫描）。
    /// </summary>
    [UnitTest]
    internal static void PptStore_HasPageFile_TracksDisk()
    {
        string key = "ut-ppt-" + Guid.NewGuid().ToString("N");
        try
        {
            Check.True(!PptStore.HasPageFile(key, 256), "还没写过就说有文件");
            PptStore.SavePage(key, 256, new byte[] { 1, 2, 3 });
            Check.True(PptStore.HasPageFile(key, 256), "写完了还说没有文件");
            Check.True(!PptStore.HasPageFile(key, 257), "没写的页被误报有文件");
            PptStore.SavePage(key, 256, Array.Empty<byte>());   // 空 = 删除
            Check.True(!PptStore.HasPageFile(key, 256), "空页删除后文件还在");
        }
        finally
        {
            PptStore.DeleteAll(key);
        }
    }
}
