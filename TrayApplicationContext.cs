using System.Drawing;
using System.Reflection;
using Microsoft.Win32;

namespace Linkout;

/// <summary>
/// 托盘应用上下文：
/// - 管理 NotifyIcon 与右键菜单；
/// - 持有剪贴板监听窗口对象；
/// - 负责程序的统一退出流程。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string AutoRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoRunValueName = "Linkout";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _enableMenuItem;
    private readonly ClipboardListenerWindow _clipboardListenerWindow;
    private readonly Icon _trayIcon;
    private readonly bool _shouldDisposeTrayIcon;
    private bool _isEnabled = true;

    public TrayApplicationContext()
    {
        _clipboardListenerWindow = new ClipboardListenerWindow
        {
            IsEnabled = true
        };

        _enableMenuItem = new ToolStripMenuItem("启用净化")
        {
            CheckOnClick = true,
            Checked = true
        };
        _enableMenuItem.Click += (_, _) =>
        {
            _isEnabled = _enableMenuItem.Checked;
            _clipboardListenerWindow.IsEnabled = _isEnabled;
        };

        var exitMenuItem = new ToolStripMenuItem("退出程序");
        exitMenuItem.Click += (_, _) => ExitThread();

        var autoStartMenuItem = new ToolStripMenuItem("开机自启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        autoStartMenuItem.Click += (_, _) =>
        {
            SetAutoStart(autoStartMenuItem.Checked);
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_enableMenuItem);
        contextMenu.Items.Add(autoStartMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitMenuItem);

        (_trayIcon, _shouldDisposeTrayIcon) = LoadTrayIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Linkout",
            Visible = true,
            ContextMenuStrip = contextMenu
        };
    }

    /// <summary>
    /// 线程退出时释放托盘图标与监听句柄，避免残留图标。
    /// </summary>
    protected override void ExitThreadCore()
    {
        _clipboardListenerWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_shouldDisposeTrayIcon)
        {
            _trayIcon.Dispose();
        }
        base.ExitThreadCore();
    }

    /// <summary>
    /// 加载托盘图标：
    /// 1. 优先从嵌入资源 Linkout.app.ico 读取；
    /// 2. 资源不存在或加载失败时回退到系统默认图标。
    /// </summary>
    private static (Icon Icon, bool ShouldDispose) LoadTrayIcon()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Linkout.app.ico");
            if (stream is not null)
            {
                return (new Icon(stream), true);
            }
        }
        catch
        {
            // 读取或解析嵌入图标失败时回退默认图标，避免程序崩溃。
        }

        return (SystemIcons.Application, false);
    }

    /// <summary>
    /// 检查当前用户注册表中是否已配置开机自启动。
    /// </summary>
    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoRunKeyPath, writable: false);
            var value = key?.GetValue(AutoRunValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            // 读取注册表失败时按“未启用”处理，避免影响主流程。
            return false;
        }
    }

    /// <summary>
    /// 设置开机自启动：
    /// - enabled=true 时写入 Run 项；
    /// - enabled=false 时删除 Run 项。
    /// </summary>
    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoRunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(AutoRunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                // 写入当前进程可执行文件路径（兼容单文件发布），外层加引号以兼容路径中包含空格的情况。
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath))
                {
                    return;
                }

                key.SetValue(AutoRunValueName, $"\"{processPath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AutoRunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 访问权限不足或其他注册表异常时静默处理，避免程序异常退出。
        }
    }
}
