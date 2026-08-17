# MC整合包工具

这是一个MC整合包工具，目前有 **“整合包版本迁移”** 、 **“整合包服务端一键打包”** 与 **客户端整合包打包** 三个模块。当前版本`v1.0.0-beta.4`完成了迁移模块、客户端打包、服务端一键打包和基础设置功能。

> 本软件在设计、代码起草、检查、调试及文字整理过程中**使用了 AI 工具辅助**，**并非**全部由作者逐行人工编写。
> 工具判断可能产生遗漏或错误，请结合兼容性报告、游戏日志及实际启动结果独立判断。

<img width="1280" height="800" alt="image" src="https://github.com/user-attachments/assets/bdbb89cd-71cf-4b12-a12e-5a65908cb8f8" />

## 迁移模块介绍

迁移模块可读取 CurseForge `.zip` 或 Modrinth `.mrpack`整合包内容，
将整合包**转换为用户指定的 Minecraft版本**，**保留与原包一致的模组、材质包、光影包列表**，并按源格式输出迁移到指定版本后的整合包。

[版本迁移器使用流程&说明](告示和教程/process&instructions.md#版本迁移器) 

<img width="1280" height="800" alt="image" src="https://github.com/user-attachments/assets/2abc2aa9-6345-4d49-88a8-4cbc0491ab3c" />


## 服务端一键打包模块介绍

服务端一键打包模块可读取本地 Minecraft 游戏目录，或标准 CurseForge `.zip`、Modrinth `.mrpack` 整合包，自动识别 **Minecraft 版本、模组加载器、加载器版本、模组、配置文件和存档**。

工具会筛选适用于服务端的模组、匹配当前版本可用的服务器核心和 Java 环境，并生成包含启动脚本、服务端核心、所选模组、配置及存档的**完整服务器 ZIP**。

[服务端一键打包使用流程&说明](告示和教程/process&instructions.md#服务端一键打包)

<img width="1280" height="836" alt="image" src="https://github.com/user-attachments/assets/9521b081-4eeb-4147-ac09-9da42465035e" />

## 客户端整合包打包模块介绍

客户端整合包打包模块可读取本地 Minecraft 游戏目录或版本隔离实例，识别其中的**模组、材质包、光影包、地图、配置及其他客户端数据**，并由用户选择需要导出的内容。

工具会通过文件精确哈希匹配 CurseForge 或 Modrinth 平台项目；匹配成功的文件写入平台下载清单，未识别文件则按原相对路径内嵌，最终生成 CurseForge `.zip`、Modrinth `.mrpack`，或同时生成两种格式。

[客户端整合包打包使用流程&说明](告示和教程/process&instructions.md#版本迁移器)

<img width="1280" height="836" alt="image" src="https://github.com/user-attachments/assets/29ceb844-8edd-4734-97f4-f9a19b856738" />

## 其他页面

设置页支持**简体中文、繁体中文（香港）和 English**，并可切换浅色/深色主题、自定义主题色和界面字体，以及预留的软件更新按钮入口。

<img width="1280" height="836" alt="image" src="https://github.com/user-attachments/assets/21560dae-ff77-4445-9af2-c8af2ca4c51a" />

## 浅色模式预览

<img width="1280" height="836" alt="image" src="https://github.com/user-attachments/assets/ce8eaae9-0738-4c5d-bc34-dfdf79b4b584" />

> 当前版本为公开预发布测试版 `v1.0.0-beta.4`。
> 请在原包、实例和世界存档均有备份的环境中测试；**兼容性报告不能替代 Minecraft 实际启动验证。**

[项目主页](https://github.com/FengchenWD/mc-modpack-tool) || [Windows 版本下载](https://github.com/FengchenWD/mc-modpack-tool/releases)

## 运行相关

- `MC-Modpack-Tool.exe` 是单文件 Windows 版本，可直接运行，**不需要另行安装额外资源**。

## 用户协议与许可

首次运行时，软件会显示完整的[《用户协议与使用须知》](告示和教程/USER_AGREEMENT.md)。只有点击“我已阅读并同意”后才能进入主页；

同意状态以版本号记录在当前用户本机的 `.mc_pack_migrator_config.json` 中，协议发生实质更新时可以要求重新确认。主页右下角的“用户协议”按钮可随时重新查看正文。

本项目按材料类型分域授权，不提供可任选其一的双重许可：

- 作者拥有并有权授权的自研代码、XAML、项目与构建文件、测试及其编译形式，适用 [PolyForm Noncommercial License 1.0.0](LICENSE)；
- 作者拥有并有权授权的自研美术、Logo、图像、文档和文字资源，适用 [CC BY-NC-SA 4.0](LICENSE-ASSETS.md)；
- 第三方组件和内容继续适用各自许可，详见 [第三方组件许可与通知](THIRD_PARTY_NOTICES.md)。

完整的材料范围、非双重许可说明和历史版本边界见 [NOTICE.md](NOTICE.md)。PolyForm Noncommercial 是非商业源码许可，并非 OSI 认可的开源许可证。
