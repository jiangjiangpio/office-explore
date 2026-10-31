# OfficeTouchBar

Windows 桌面版 PowerPoint 触控翻页侧栏工具。

当前源码版本：0.3.18。本仓库保存重构前的代码快照，仍有已反馈但尚未完成修复或实机验收的问题。

## 当前实现

- PowerPoint 2019/2021/2024 兼容目标，优先 Office 2021。
- WPF 半透明可拖动侧栏。
- 鼠标、触控和主动笔按钮点击。
- PowerPoint 前台检测、放映窗口跟随、页码显示。
- 放映中的上一步/下一步每次直接通过 `SendInput` 模拟键盘上/下键，编辑模式沿用 Office 命令。
- 在设置窗口切换 Office 原生笔与内置笔；内置笔支持普通笔、荧光笔、整笔橡皮擦、笔大小滑块和逐页保存恢复。鼠标橡皮擦悬停可见，触控/主动笔接触时显示指示器；按路径段网格筛选并合并命中删除，空闲保存采用独立快照后台写入。
- 内置模式 `EXIT` 先确认墨迹保留/放弃，再退出放映；Office 模式沿用原生退出流程。
- 设置的工具页可独立控制内置书写时鼠标范围限制及全屏触屏边缘手势保护；切回鼠标或其他软件释放。Windows 10/11、触控设备及各 Office 版本仍需实机验收。
- 系统托盘、开机自启动、JSON 设置文件。
- Inno Setup 安装脚本位于 `Setup/OfficeTouchBar.iss`。

## 构建

本项目需要安装 .NET 10 SDK 和 Windows Desktop 支持：

```powershell
dotnet build .\OfficeTouchBar.csproj -c Release
```

安装 Inno Setup 6，并将其安装目录加入 PATH（使 `iscc` 命令可用）后，使用构建脚本生成干净的发布目录、文件清单和安装包：

```powershell
.\build.ps1
```

## Office 连接说明

程序通过 Windows COM Running Object Table 连接已经打开的 PowerPoint，不启动新的 PowerPoint 实例，也不注入 PowerPoint 进程。若 PowerPoint 未打开，程序仅保留在系统托盘中。

安装包使用 self-contained 发布方式，最终用户无需另外安装 .NET 运行时。

Setup 可覆盖升级同一应用，清理旧清单内已经淘汰的程序文件，保留用户设置与墨迹。安装过程只关闭本应用；实际覆盖安装须由用户在目标环境验证。

PowerPoint 对原生放映笔宽没有稳定一致的公开 COM 接口，因此 Office 原生笔模式不提供笔宽控件；内置笔模式提供笔大小滑块。

Office 笔的墨迹保存仍由 PowerPoint 原生放映流程管理，程序不增加第二套退出提示。内置笔使用独立透明输入层，按演示文稿和页码保存到用户配置目录，不修改 PPT 文件。

## 仓库范围

仓库只包含应用源码、项目和包源配置、构建脚本及安装器脚本。`Setup/LegacyPayloadFiles.txt` 是覆盖升级时识别旧程序文件所必需的清单。

测试、协作与问题记录、本地配置、日志、录屏、安装包、构建缓存及 Inno Setup 工具本体保留在本地，不纳入版本控制。`.gitignore` 使用允许清单；新增源码文件或目录时须同步检查规则。
