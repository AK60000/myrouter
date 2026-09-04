using System;
using System.Windows.Forms;
using myrouter.Forms;
using myrouter.Services;

namespace myrouter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppPaths.MigrateLegacy();   // 旧版散落配置文件迁入 .myrouter/（须在任何数据类构造前）
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"未处理的异常:\n{e.Exception}", "myrouter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}
