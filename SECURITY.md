# 安全说明

## 支持范围

当前仅维护最新的预发布版本。测试版不承诺长期安全更新，但确认的问题会在后续版本中评估和修复。

## 报告安全问题

仓库创建后，应在 GitHub 仓库设置中启用 Private vulnerability reporting，并通过仓库的
“Security / Report a vulnerability”私下报告以下问题：

- ZIP 路径穿越、任意文件覆盖或不安全临时文件处理；
- API Key、访问令牌、个人路径或其他敏感信息泄露；
- 下载文件哈希校验绕过；
- 可由整合包内容触发的代码执行或权限边界问题。

请不要在公开 Issue 中粘贴 API Key、访问令牌、包含个人信息的日志或未公开漏洞细节。
普通功能错误和兼容性误判可以使用公开 Issue 报告，并应先移除日志中的个人路径和敏感信息。

## CurseForge API Key

公开源码和 Git 历史不得包含 CurseForge API Key。从源码运行或进行本地构建时，只通过环境变量 `CURSEFORGE_API_KEY` 提供 Key，不要把 Key 写入源码、配置样例、Issue、日志或构建输出。

官方预发布 EXE 使用受保护的 `prerelease` GitHub Environment 及其中独立的 Secret `CURSEFORGE_API_KEY` 进行手动构建，且任务只允许从 `main` 分支运行。应为该 Environment 配置适当的部署审批。构建期间可以临时生成 `build_secrets.py` 并将 Key 写入 EXE，但临时文件必须由构建流程在成功或失败后删除，且不得进入 Git 历史或 artifact。来自 Fork、拉取请求或非发布流程的代码不应获得该 Secret。

GitHub Secret 只保护源码与构建日志中的值，不能防止客户端凭据被提取。任何写入 EXE 的 Key 都应视为可能通过反编译、内存或网络流量公开，因此必须满足以下要求：

- 使用只服务于本项目发布构建的独立 Key，不与个人工具、其他项目或服务端凭据共用；
- 确保 Key 可随时撤销，监控请求量、配额消耗、异常来源和 CurseForge 侧告警；
- 定期轮换 Key；确认泄露、异常调用或配额滥用时立即撤销并替换 Secret，随后重新构建和发布 EXE；
- 不把“加密”“混淆”、PyInstaller 打包或 GitHub Secret 视为客户端 Key 的保密边界。

发现仓库、发布文件或日志意外泄露 Key 时，请按上文方式私下报告，不要在公开 Issue 中复述该值。
