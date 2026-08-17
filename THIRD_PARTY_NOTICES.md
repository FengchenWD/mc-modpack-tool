# 第三方组件许可与通知

本文件对应当前 C# 单文件发布。作者自研材料的分域许可不会替代或修改下列第三方组件的许可。

## 自包含运行时

| 组件 | 发布版本 | 许可 |
| --- | --- | --- |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.10 | MIT |
| Microsoft.WindowsDesktop.App.Runtime.win-x64（WPF / Windows Forms） | 10.0.10 | MIT |

两个运行时包各自附带的 MIT 原文分别保存在 [`THIRD_PARTY_LICENSES/DotNet-10.0.10-LICENSE.txt`](THIRD_PARTY_LICENSES/DotNet-10.0.10-LICENSE.txt) 与 [`THIRD_PARTY_LICENSES/DotNet-WindowsDesktop-10.0.10-LICENSE.txt`](THIRD_PARTY_LICENSES/DotNet-WindowsDesktop-10.0.10-LICENSE.txt)。.NET Runtime 随包提供的完整上游第三方通知保存在 [`THIRD_PARTY_LICENSES/DotNet-Runtime-10.0.10-THIRD-PARTY-NOTICES.txt`](THIRD_PARTY_LICENSES/DotNet-Runtime-10.0.10-THIRD-PARTY-NOTICES.txt)。

上述正式文本来自构建时实际使用的 NuGet 运行时包，未作改写，并作为嵌入资源包含在 `MC整合包工具.exe` 中。用户可以在软件的用户协议窗口选择“查看第三方许可”阅读。

`Microsoft.NET.ILLink.Tasks 10.0.10` 由 .NET SDK 在发布阶段自动使用，仅属于构建期工具，不进入最终运行时。

## 旧版 Python 构建

旧版 Python 构建及其依赖仍按各自随附许可处理，完整索引和许可文本保存在 `Python(旧版构建)/THIRD_PARTY_NOTICES.md` 与 `Python(旧版构建)/THIRD_PARTY_LICENSES/`。这些历史文件保持原样。

## 许可边界

.NET、WPF、Windows Forms 及其上游第三方组件归各自权利人所有。将它们与本工具一同编译、嵌入或分发，不会使其改为 PolyForm Noncommercial License 1.0.0 或 CC BY-NC-SA 4.0，也不会使作者自研代码改为第三方组件的许可。
