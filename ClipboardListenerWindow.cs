using System.Runtime.InteropServices;

namespace Linkout;

/// <summary>
/// 基于 NativeWindow 的隐藏消息窗口。
/// 不创建可视窗体，仅用于接收系统广播消息 WM_CLIPBOARDUPDATE。
/// </summary>
internal sealed class ClipboardListenerWindow : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int DebounceDelayMs = 80;
    private const int ClipboardRetryCount = 3;
    private const int ClipboardRetryDelayMs = 20;

    // 记录上一次由本程序主动写回剪贴板的文本，用于防止读写死循环。
    private string? _lastCleanedText;
    private bool _disposed;
    private CancellationTokenSource? _debounceCts;
    private readonly LinkCleaner _cleaner = new();

    /// <summary>
    /// 净化开关，由托盘菜单控制。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public ClipboardListenerWindow()
    {
        var cp = new CreateParams
        {
            Caption = "ClipboardListenerMessageWindow"
        };
        CreateHandle(cp);

        // 注册当前窗口为系统剪贴板格式监听器。
        // 成功后，只要剪贴板变化，系统就会发送 WM_CLIPBOARDUPDATE 到本窗口。
        if (!AddClipboardFormatListener(Handle))
        {
            throw new InvalidOperationException("无法注册剪贴板监听器 AddClipboardFormatListener。");
        }
    }

    /// <summary>
    /// 接收 Windows 消息：
    /// WM_CLIPBOARDUPDATE 是系统在剪贴板变化时投递的通知。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE && IsEnabled)
        {
            _ = HandleClipboardChangedDebouncedAsync();
        }

        base.WndProc(ref m);
    }

    private async Task HandleClipboardChangedDebouncedAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            // 极短防抖：合并短时间内连续消息，提升稳定性。
            await Task.Delay(DebounceDelayMs, token);

            // 保护富文本内容：
            // 当剪贴板同时包含纯文本与 Html/Rtf 等复杂格式时，
            // 说明用户可能复制了网页/Word 等带排版内容，跳过净化避免格式丢失。
            if (ShouldSkipBecauseRichTextPresent())
            {
                return;
            }

            var originalText = await SafeGetClipboardTextAsync(token);
            if (string.IsNullOrEmpty(originalText))
            {
                return;
            }

            // 防死循环关键：如果这次读到的内容与程序刚写回的一致，说明是自触发消息，直接跳过。
            if (string.Equals(originalText, _lastCleanedText, StringComparison.Ordinal))
            {
                return;
            }

            var cleanedText = await _cleaner.CleanAsync(originalText, token);

            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                return;
            }

            if (!string.Equals(originalText, cleanedText, StringComparison.Ordinal))
            {
                // 在写回前先记录，确保下一个 WM_CLIPBOARDUPDATE 能准确识别“自写入”。
                _lastCleanedText = cleanedText;
                await SafeSetClipboardTextAsync(cleanedText, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 防抖取消属于正常流程，无需处理。
        }
        catch (ExternalException)
        {
            // 剪贴板可能被其他进程暂时占用，忽略本次，等待下一次系统消息。
        }
        catch
        {
            // 后台常驻工具避免因异常退出，这里吞掉异常保障进程稳定。
        }
    }

    /// <summary>
    /// 安全读取剪贴板文本：
    /// 最多重试 3 次，每次间隔 20ms，缓解剪贴板被其他进程短暂占用导致的 ExternalException。
    /// </summary>
    private static async Task<string?> SafeGetClipboardTextAsync(CancellationToken token)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Clipboard.ContainsText())
                {
                    return null;
                }

                return Clipboard.GetText();
            }
            catch (ExternalException) when (attempt < ClipboardRetryCount - 1)
            {
                await Task.Delay(ClipboardRetryDelayMs, token);
            }
        }

        return null;
    }

    /// <summary>
    /// 安全写入剪贴板文本：
    /// 最多重试 3 次，每次间隔 20ms，缓解剪贴板被其他进程短暂占用导致的 ExternalException。
    /// </summary>
    private static async Task<bool> SafeSetClipboardTextAsync(string text, CancellationToken token)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException) when (attempt < ClipboardRetryCount - 1)
            {
                await Task.Delay(ClipboardRetryDelayMs, token);
            }
        }

        return false;
    }

    /// <summary>
    /// 判断是否应跳过净化：
    /// - 若剪贴板包含 Text 且同时包含 Html 或 Rtf，则视为富文本复制场景；
    /// - 若包含 FileDrop 或 Bitmap，则视为文件/图片复制场景。
    /// </summary>
    private static bool ShouldSkipBecauseRichTextPresent()
    {
        var dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
        {
            return false;
        }

        var hasText = dataObject.GetDataPresent(DataFormats.Text);
        var hasHtml = dataObject.GetDataPresent(DataFormats.Html);
        var hasRtf = dataObject.GetDataPresent(DataFormats.Rtf);
        var hasFileDrop = dataObject.GetDataPresent(DataFormats.FileDrop);
        var hasBitmap = dataObject.GetDataPresent(DataFormats.Bitmap);

        if (hasFileDrop || hasBitmap)
        {
            return true;
        }

        return hasText && (hasHtml || hasRtf);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        // 取消注册剪贴板监听，防止句柄销毁后仍接收通知。
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    /// <summary>
    /// Win32 API: 将指定窗口加入剪贴板变更监听队列。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>
    /// Win32 API: 将窗口从剪贴板监听队列移除。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
