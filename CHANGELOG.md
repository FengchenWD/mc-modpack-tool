# 变更记录

本项目采用独立的 GitHub Release 标签记录公开版本。软件窗口标题不附加版本后缀。

## 未发布

- 服务端打包模块将在后续版本中完善。

## v1.0.0-beta.1 - 预发布测试版

- 产品更名为“MC整合包工具”，新增主页和常驻侧边栏，可在主页、版本迁移、服务端打包和设置之间切换。
- 原版本迁移功能迁入独立模块页面，并在切换页面、语言或主题时保留当前迁移状态。
- 设置页支持简体中文、繁体中文（香港）和 English，以及浅色/深色主题、预设或自定义主题色和系统字体切换。
- 主页新增 Bilibili 作者主页、GitHub 项目入口、用户协议入口和“作者：风尘WD”署名。
- 更新工作台 Logo，并同步生成 PNG 与多尺寸 ICO。
- 支持读取 CurseForge `.zip` 和 Modrinth `.mrpack` 整合包。
- 按目标 Minecraft 与加载器环境重新匹配模组、资源包和光影包。
- 优先使用平台项目 ID、ForgeCDN file ID 和文件哈希确认项目身份。
- 增加保守的文本搜索评分，避免把本体模组替换成兼容附属、分支或 Fork 项目。
- 检查可识别的直接依赖、明确冲突、重复项目和输出路径冲突。
- 保持 `overrides` 内容原样打包，不查询、不迁移、不改写。
- 增加可切换语言的首次启动用户协议和自动生成目标包名。
- 提供单文件 Windows EXE，不要求最终用户安装 Python。
- 统一应用版本、网络 User-Agent、GitHub 标签和 Windows EXE 版本资源。
- 为联网下载增加 `Content-Length` 预检、预期大小限制和 2 GiB 流式硬上限。
- 增加可复现发布依赖和完整第三方许可说明。
- 移除公开源码中的内置 CurseForge API Key，源码运行改为读取 `CURSEFORGE_API_KEY` 环境变量。
- 增加手动预发布构建方案：从受保护的 GitHub Environment Secret `CURSEFORGE_API_KEY` 临时生成 `build_secrets.py`，构建后删除且不写入 Git 历史。

### 已知限制

- 兼容性分析是静态检查，不会启动 Minecraft 或执行模组代码。
- 无法穷尽仅在运行时出现的 Mixin、注册表、版本范围及模组内部冲突。
- CurseForge、Modrinth 和加载器服务的网络、限流或接口变化可能导致查询失败。
- GitHub Secret 不能保护已经写入客户端 EXE 的 CurseForge Key；发布 Key 仍可能被提取，必须独立、可撤销并定期监控和轮换。
- 这是测试版，导出的整合包仍应在备份实例中实际启动验证。
