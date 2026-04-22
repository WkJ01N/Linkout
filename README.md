# Linkout

## README.md

# Linkout

Linkout 是一款专为 Windows 平台设计的轻量级剪贴板链接净化工具。它能够自动监测剪贴板中的文本，识别并剔除各大视频、社交及电商平台的追踪参数（Tracking Parameters），还原纯净的分享链接。

## 核心功能

- **自动化监听**：基于 Windows 消息循环（WM_CLIPBOARDUPDATE）实现，仅在剪贴板内容变化时触发，无轮询，零 CPU 占用。
- **智能净化**：精准识别 Bilibili、小红书、抖音、淘宝等平台的分享链接，剔除 spm、vd_source、si 等数十种常见追踪参数。
- **短链还原**：支持 b23.tv、v.douyin.com、m.tb.cn 等短链接的异步解析，自动获取并净化重定向后的真实长链。
- **行内替换**：在清理链接的同时，完整保留用户复制文本中的原始文字描述、空格及排版。
- **格式保护**：智能识别富文本（Html/Rtf）、文件或图片复制行为，在检测到复杂格式时自动避让，确保不会破坏 Word/Excel 及文件的正常剪切操作。
- **高性能架构**：采用 .NET 10 现代语法，利用 ValueTask 减少内存分配，并使用 Source Generator 生成的高效正则引擎。
- **系统集成**：支持开机自启动设置，采用系统托盘常驻运行，不干扰日常操作。

## 技术栈

- **框架**：.NET 10.0 (Windows Forms)
- **语言**：C# 13
- **底层**：Win32 API (P/Invoke)

## 编译与运行

### 环境要求
- Windows 10/11
- .NET 10 Desktop Runtime

### 编译步骤
1. 克隆仓库
```bash
git clone https://github.com/WkJ01N/Linkout.git
```
3. 使用 .NET CLI 发布单文件（依赖系统环境版）：
```bash
dotnet publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true
```

## 使用说明
1. 运行 `Linkout.exe`，程序将自动进入右下角系统托盘。
2. 在托盘图标上右键，可开启或关闭“启用净化”及“开机自启动”。
3. 复制任何带有长尾巴参数的链接，Linkout 会在瞬间将其净化，直接在记事本或其他地方粘贴即可看到效果。
