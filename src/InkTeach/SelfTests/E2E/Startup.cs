namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// `--startupcheck`：走一遍真实的后台待命装配，再模拟第二个实例叫醒。
    /// 判据看两件事：待命时必须是穿透 + 小球收起；叫醒后必须退出穿透 + 展开。
    /// </summary>
    private void StartupCheck()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-38} {detail}");
        }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(120);
        var ui = CurrentUi as InkUi.FullUi;

        Check("待命态：进程标记为开机自启", StartupMode, $"StartupMode={StartupMode}");
        Check("待命态：穿透开着", PassThrough, $"PassThrough={PassThrough}");
        Check("待命态：界面是小球（未展开）",
              ui != null && !ui.ExpandedForTest,
              ui == null ? "界面不是 FullUi" : $"Expanded={ui.ExpandedForTest}");

        ActivateFromExternal();
        SettleFrames(350);

        Check("叫醒后：退出穿透", !PassThrough, $"PassThrough={PassThrough}");
        Check("叫醒后：工具条展开",
              ui != null && ui.ExpandedForTest,
              ui == null ? "界面不是 FullUi" : $"Expanded={ui.ExpandedForTest}");

        if (fail == 0) Console.WriteLine($"PASS: startupcheck {pass}/{pass + fail}");
        Console.WriteLine($"startupcheck: {pass} 通过 / {fail} 失败");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
