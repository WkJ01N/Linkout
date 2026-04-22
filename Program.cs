namespace Linkout;

static class Program
{
    /// <summary>
    /// 程序主入口：
    /// 1. 不显示主窗体；
    /// 2. 启动后直接进入系统托盘；
    /// 3. 通过消息循环持续接收剪贴板变更通知。
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }    
}