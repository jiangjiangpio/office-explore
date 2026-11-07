# OfficeTouchBar

Windows 桌面版 PowerPoint 触控翻页侧栏工具。

当前源码版本：0.3.20。此版本修复临时 COM 读取失败引起的工具重置、漏掉抬起事件后的输入状态滞留，以及窗口置顶状态反复变化。Windows 触控设备和 PowerPoint 联动仍需实机验收。

## 构建

本项目需要安装 .NET 10 SDK 和 Windows Desktop 支持：

```powershell
dotnet build .\OfficeTouchBar.csproj -c Release
```

安装 Inno Setup 6 或更新版本，并将其安装目录加入 PATH（使 `iscc` 命令可用）后，使用构建脚本生成干净的发布目录、文件清单和安装包：

```powershell
.\build.ps1
```

状态逻辑回归检查可在 .NET 10 支持的平台执行：

```sh
dotnet run --project Tests/RegressionChecks.csproj -c Release
```

检查使用平台替身覆盖 COM 读取失败、窗口排序和触点状态；不替代 WPF、真实触控驱动和 PowerPoint 的端到端测试。详见 `Tests/README.md`。

## Office 连接说明

程序通过 Windows COM Running Object Table 连接已经打开的 PowerPoint，不启动新的 PowerPoint 实例，也不注入 PowerPoint 进程。若 PowerPoint 未打开，程序仅保留在系统托盘中。

安装包使用 self-contained 发布方式，最终用户无需另外安装 .NET 运行时。

Setup 可覆盖升级同一应用，清理旧清单内已经淘汰的程序文件，保留用户设置与墨迹。安装过程只关闭本应用；实际覆盖安装须由用户在目标环境验证。

PowerPoint 对原生放映笔宽没有稳定一致的公开 COM 接口，因此 Office 原生笔模式不提供笔宽控件；内置笔模式提供笔大小滑块。

Office 笔的墨迹保存仍由 PowerPoint 原生放映流程管理，程序不增加第二套退出提示。内置笔使用独立透明输入层，按演示文稿和页码保存到用户配置目录，不修改 PPT 文件。
