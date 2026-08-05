# MC整合包工具

这是一个MC整合包工具，目前有 **“整合包版本迁移”** 与 **“整合包服务端打包”** 两个模块。当前版本`v1.0.0-beta.1`仅完成了迁移模块和基础设置功能。

## 迁移模块介绍

迁移模块可读取 CurseForge `.zip` 或 Modrinth `.mrpack`整合包内容，
将整合包**转换为用户指定的 Minecraft版本**，**保留与原包一致的模组、材质包、光影包列表**，并按源格式输出迁移到指定版本后的整合包。

[版本迁移器使用流程&说明](告示和教程/process&instructions.md#%E7%89%88%E6%9C%AC%E8%BF%81%E7%A7%BB%E5%99%A8) 

设置页支持简体中文、繁体中文（香港）和 English，并可切换浅色/深色主题、自定义主题色和界面字体。服务端整合包打包模块将在后续版本中完善。

> 当前版本为公开预发布测试版 `v1.0.0-beta.1`。请在原包、实例和世界存档均有备份的环境中测试；**兼容性报告不能替代 Minecraft 实际启动验证。**

[项目主页](https://github.com/FengchenWD/mc-modpack-tool) || [Windows 版本下载](https://github.com/FengchenWD/mc-modpack-tool/releases)

## 运行相关

- `MC-Modpack-Tool.exe` 是单文件 Windows 版本，可直接运行，不需要另行安装 Python 或 pip 库。

## 用户协议与许可

首次运行时，软件会显示完整的[《用户协议与使用须知》](告示和教程/USER_AGREEMENT.md)。只有点击“我已阅读并同意”后才能进入主页；
同意状态以版本号记录在当前用户本机的 `.mc_pack_migrator_config.json` 中，协议发生实质更新时可以要求重新确认。主页右下角的“用户协议”按钮可随时重新查看正文。

## 项目仓库目录结构

- 根目录保留主程序、README、正式许可文本、许可范围说明、用户协议及安全说明。
- `程序模块`：运行所需的兼容性分析模块。
- `资源`：软件使用的 PNG 与 ICO Logo。
- `依赖`：运行与发布依赖、可移植 PyInstaller 配置及构建脚本。
- `THIRD_PARTY_LICENSES`：发布环境中 Python、Tcl/Tk、运行依赖和构建组件的完整许可与通知文本。
