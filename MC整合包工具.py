#!/usr/bin/env python3
"""
MC整合包工具
============
维护增强版功能:
  - 精确匹配目标 Minecraft 与加载器版本，支持 CurseForge / Modrinth 回退
  - 检查模组、资源包、光影包的目标版本、依赖、明确冲突与输出冲突
  - 对输入归档、下载哈希、重复输出路径和原子写入进行安全校验
  - 主页、模块侧边栏、兼容性报告与工作台 Logo
  - CurseForge Key 支持环境变量，并可在发布构建时由临时模块注入

运行依赖: Python 3.10+、requests；分析模块位于“程序模块”，Logo 位于“资源”。
"""

import os, sys, json, shutil, zipfile, tempfile, threading, webbrowser
import traceback, re, hashlib, xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from datetime import datetime
from typing import Optional, Iterable
from dataclasses import dataclass, field
from urllib.parse import unquote, urlparse


APP_ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
MODULE_DIR = APP_ROOT / "程序模块"
if MODULE_DIR.is_dir() and str(MODULE_DIR) not in sys.path:
    sys.path.insert(0, str(MODULE_DIR))


def _abort_missing_dependency(message: str) -> None:
    try:
        import tkinter as startup_tk
        from tkinter import messagebox as startup_messagebox
        startup_root = startup_tk.Tk(); startup_root.withdraw()
        startup_messagebox.showerror("无法启动", message)
        startup_root.destroy()
    except Exception:
        print(message, file=sys.stderr)
    raise SystemExit(1)


try:
    import requests
except ImportError:
    _abort_missing_dependency(
        '缺少 requests。请先运行：python -m pip install -r "依赖\\requirements.txt"')

try:
    from compatibility_analyzer import CompatibilityIssue, analyze_compatibility
except ImportError:
    _abort_missing_dependency("缺少 程序模块\\compatibility_analyzer.py，请检查工具目录是否完整。")

try:
    import tkinter as tk
    from tkinter import ttk, filedialog, messagebox, colorchooser
    import tkinter.font as tkfont
    from tkinter.ttk import Progressbar
except ImportError:
    _abort_missing_dependency("当前 Python 未包含 Tkinter。请安装带 Tcl/Tk 组件的 Python 3.10 或更高版本。")


# ============================================================
# 常量
# ============================================================

REQUEST_TIMEOUT = (15, 30)
APP_NAME = "MC整合包工具"
APP_VERSION = "1.0.0-beta.1"
AUTHOR_CREDIT = "作者：风尘WD"
BILIBILI_URL = "https://space.bilibili.com/1003434667"
GITHUB_URL = "https://github.com/FengchenWD/mc-modpack-tool"
NAVIGATION_ITEMS = (
    ("home", "主页"),
    ("migration", "版本迁移"),
    ("server", "服务端打包"),
    ("settings", "设置"),
)
SUPPORTED_LANGUAGES = ("zh_CN", "zh_HK", "en_US")
SUPPORTED_THEMES = ("light", "dark")
DEFAULT_ACCENT_COLOR = "#167D6A"
DEFAULT_FONT_FAMILY = "Microsoft YaHei UI"
LANGUAGE_LABELS = {
    "zh_CN": "简体中文",
    "zh_HK": "繁體中文（香港）",
    "en_US": "English",
}
THEME_PRESETS = ("#167D6A", "#2563EB", "#C2416C", "#7C3AED", "#D97706")

TRANSLATIONS = {
    "zh_CN": {
        "app.name": "MC整合包工具",
        "nav.home": "主页", "nav.migration": "版本迁移",
        "nav.server": "服务端打包", "nav.settings": "设置",
        "app.subtitle": "Minecraft 整合包工作台",
        "home.migration": "整合包版本迁移", "home.server": "整合包服务端打包",
        "home.agreement": "用户协议", "home.bilibili": "Bilibili 主页",
        "home.github": "GitHub", "home.github_pending": "GitHub（即将开放）",
        "author": "作者：风尘WD",
        "placeholder.server": "服务端打包模块将在后续步骤中完善",
        "migration.title": "整合包版本迁移",
        "migration.subtitle": "CurseForge / Modrinth 目标版本匹配与兼容性检查",
        "migration.ready": "就绪", "migration.build": "生成新整合包",
        "migration.pack_file": "整合包文件", "migration.choose_file": "选择文件",
        "migration.read_pack": "读取整合包", "migration.source_overview": "源包概览",
        "migration.target": "迁移目标", "migration.loader": "加载器",
        "migration.loader_version": "加载器版本", "migration.latest_loader": "获取最新稳定版",
        "migration.output_dir": "输出目录", "migration.browse": "浏览",
        "migration.output_name": "文件名 / 包名",
        "migration.embed_downloads": "嵌入可下载文件",
        "migration.check": "检查兼容性", "migration.report": "兼容性报告",
        "migration.report_hint": "读取整合包后运行兼容性检查",
        "migration.refresh": "重新分析", "migration.files": "文件明细",
        "migration.log": "运行日志", "tree.severity": "级别",
        "tree.category": "类别", "tree.item": "项目", "tree.conclusion": "结论",
        "tree.name": "名称", "tree.source": "来源", "tree.status": "状态",
        "tree.output": "输出", "menu.exclude": "从输出排除此项",
        "menu.curseforge": "在 CurseForge 查看", "menu.modrinth": "在 Modrinth 查看",
        "settings.title": "设置", "settings.subtitle": "更改会立即生效并自动保存",
        "settings.language": "语言", "settings.language_desc": "选择软件界面的显示语言。",
        "settings.appearance": "外观", "settings.appearance_desc": "切换浅色或深色主题。",
        "settings.light": "浅色", "settings.dark": "深色",
        "settings.accent": "主题颜色", "settings.accent_desc": "选择按钮、链接和选中状态使用的强调色。",
        "settings.custom_color": "自定义颜色", "settings.reset_color": "恢复默认",
        "settings.font": "界面字体", "settings.font_desc": "从这台电脑已安装的字体中选择。",
        "settings.default_font": "系统默认", "settings.font_preview": "MC整合包工具 · Minecraft 1.21.1",
        "settings.color_dialog": "选择主题颜色", "settings.saved": "设置已保存",
        "agreement.title": "用户协议与使用须知", "agreement.version": "协议版本 {version}",
        "agreement.license": "查看 CC BY-NC-SA 4.0 原文", "agreement.accept": "我已阅读并同意",
        "agreement.decline": "不同意并退出", "common.close": "关闭",
        "common.info": "提示", "common.not_configured": "暂未配置",
        "github.pending_title": "GitHub 仓库尚未开放",
        "github.pending_message": "GitHub 入口已经准备好。创建仓库后补充链接即可启用。",
        "category.mod": "模组", "category.resourcepack": "资源包", "category.shaderpack": "光影包",
        "status.found": "已找到", "status.not_found": "未找到", "status.pending": "待检查",
        "status.preserved": "原样保留（目标环境未变化）", "status.passthrough": "原样保留",
        "status.unknown": "未知", "status.excluded": "已从输出排除",
        "action.restore": "恢复", "action.exclude": "排除",
        "compat.error": "阻断", "compat.warning": "警告", "compat.info": "信息",
        "compat.boundary": "边界", "compat.pass": "通过", "compat.overall": "整体",
        "compat.static": "静态检查", "compat.description": "说明",
        "compat.no_issues": "未发现可静态确认的阻断或警告。",
        "compat.summary": "{errors} 项阻断 · {warnings} 项警告 · {items} 个内容项目",
        "compat.scope.mod": "模组", "compat.scope.resourcepack": "资源包",
        "compat.scope.shaderpack": "光影包", "compat.scope.content": "内容",
        "compat.scope.dependency": "依赖", "compat.scope.output": "输出",
        "compat.scope.general": "通用", "compat.item.modpack": "整合包",
        "compat.issue.item_not_found": "未找到目标版本文件。",
        "compat.issue.missing_required_dependency": "可能缺少必需前置模组：{dependency}。",
        "compat.issue.explicit_incompatibility": "与已选项目 {item} 明确冲突。",
        "compat.issue.explicitly_incompatible_item": "平台元数据明确标记该项目不兼容。",
        "compat.issue.duplicate_project": "同一平台项目出现多次。",
        "compat.issue.duplicate_output_path": "多个文件将写入同一个输出路径。",
        "compat.issue.unsafe_output_path": "目标文件路径可能越出整合包目录。",
        "compat.issue.required_embedded_download_unavailable": "此 CurseForge 项目必须嵌入 Modrinth 包，但平台未提供可下载地址。",
        "compat.issue.required_embedded_scope_unsupported": "此 CurseForge 项目必须嵌入，但原 Modrinth 客户端/服务端作用域无法安全保留。",
        "compat.issue.override_output_collision": "目标文件与 overrides 中的现有文件同名；为保证原文件不变，已阻止覆盖。",
        "dialog.choose_pack": "选择整合包文件", "dialog.pack_files": "整合包",
        "dialog.all_files": "所有文件", "dialog.output_dir": "选择输出目录",
    },
    "zh_HK": {
        "app.name": "MC整合包工具",
        "nav.home": "主頁", "nav.migration": "版本遷移",
        "nav.server": "伺服器封裝", "nav.settings": "設定",
        "app.subtitle": "Minecraft 整合包工作台",
        "home.migration": "整合包版本遷移", "home.server": "整合包伺服器封裝",
        "home.agreement": "用戶協議", "home.bilibili": "Bilibili 主頁",
        "home.github": "GitHub", "home.github_pending": "GitHub（即將開放）",
        "author": "作者：风尘WD",
        "placeholder.server": "伺服器封裝模組將在後續步驟中完善",
        "migration.title": "整合包版本遷移",
        "migration.subtitle": "CurseForge / Modrinth 目標版本配對與兼容性檢查",
        "migration.ready": "就緒", "migration.build": "產生新整合包",
        "migration.pack_file": "整合包檔案", "migration.choose_file": "選擇檔案",
        "migration.read_pack": "讀取整合包", "migration.source_overview": "來源包概覽",
        "migration.target": "遷移目標", "migration.loader": "載入器",
        "migration.loader_version": "載入器版本", "migration.latest_loader": "取得最新穩定版",
        "migration.output_dir": "輸出資料夾", "migration.browse": "瀏覽",
        "migration.output_name": "檔案名稱 / 包名",
        "migration.embed_downloads": "嵌入可下載檔案",
        "migration.check": "檢查兼容性", "migration.report": "兼容性報告",
        "migration.report_hint": "讀取整合包後執行兼容性檢查",
        "migration.refresh": "重新分析", "migration.files": "檔案明細",
        "migration.log": "運行日誌", "tree.severity": "級別",
        "tree.category": "類別", "tree.item": "項目", "tree.conclusion": "結論",
        "tree.name": "名稱", "tree.source": "來源", "tree.status": "狀態",
        "tree.output": "輸出", "menu.exclude": "從輸出排除此項",
        "menu.curseforge": "在 CurseForge 查看", "menu.modrinth": "在 Modrinth 查看",
        "settings.title": "設定", "settings.subtitle": "變更會立即生效並自動儲存",
        "settings.language": "語言", "settings.language_desc": "選擇軟件介面的顯示語言。",
        "settings.appearance": "外觀", "settings.appearance_desc": "切換淺色或深色主題。",
        "settings.light": "淺色", "settings.dark": "深色",
        "settings.accent": "主題顏色", "settings.accent_desc": "選擇按鈕、連結及選取狀態使用的強調色。",
        "settings.custom_color": "自訂顏色", "settings.reset_color": "回復預設",
        "settings.font": "介面字體", "settings.font_desc": "從這部電腦已安裝的字體中選擇。",
        "settings.default_font": "系統預設", "settings.font_preview": "MC整合包工具 · Minecraft 1.21.1",
        "settings.color_dialog": "選擇主題顏色", "settings.saved": "設定已儲存",
        "agreement.title": "用戶協議與使用須知", "agreement.version": "協議版本 {version}",
        "agreement.license": "查看 CC BY-NC-SA 4.0 原文", "agreement.accept": "我已閱讀並同意",
        "agreement.decline": "不同意並退出", "common.close": "關閉",
        "common.info": "提示", "common.not_configured": "尚未設定",
        "github.pending_title": "GitHub 儲存庫尚未開放",
        "github.pending_message": "GitHub 入口已經準備好。建立儲存庫後補上連結即可啟用。",
        "category.mod": "模組", "category.resourcepack": "資源包", "category.shaderpack": "光影包",
        "status.found": "已找到", "status.not_found": "未找到", "status.pending": "待檢查",
        "status.preserved": "原樣保留（目標環境未變）", "status.passthrough": "原樣保留",
        "status.unknown": "未知", "status.excluded": "已從輸出排除",
        "action.restore": "回復", "action.exclude": "排除",
        "compat.error": "阻斷", "compat.warning": "警告", "compat.info": "資訊",
        "compat.boundary": "邊界", "compat.pass": "通過", "compat.overall": "整體",
        "compat.static": "靜態檢查", "compat.description": "說明",
        "compat.no_issues": "未發現可由靜態檢查確認的阻斷或警告。",
        "compat.summary": "{errors} 項阻斷 · {warnings} 項警告 · {items} 個內容項目",
        "compat.scope.mod": "模組", "compat.scope.resourcepack": "資源包",
        "compat.scope.shaderpack": "光影包", "compat.scope.content": "內容",
        "compat.scope.dependency": "依賴", "compat.scope.output": "輸出",
        "compat.scope.general": "通用", "compat.item.modpack": "整合包",
        "compat.issue.item_not_found": "未找到目標版本檔案。",
        "compat.issue.missing_required_dependency": "可能欠缺必要前置模組：{dependency}。",
        "compat.issue.explicit_incompatibility": "與已選項目 {item} 明確衝突。",
        "compat.issue.explicitly_incompatible_item": "平台資料明確標記此項目不兼容。",
        "compat.issue.duplicate_project": "同一平台項目出現多次。",
        "compat.issue.duplicate_output_path": "多個檔案將寫入相同輸出路徑。",
        "compat.issue.unsafe_output_path": "目標檔案路徑可能超出整合包資料夾。",
        "compat.issue.required_embedded_download_unavailable": "此 CurseForge 項目必須嵌入 Modrinth 包，但平台沒有提供可下載位址。",
        "compat.issue.required_embedded_scope_unsupported": "此 CurseForge 項目必須嵌入，但原 Modrinth 用戶端/伺服器範圍無法安全保留。",
        "compat.issue.override_output_collision": "目標檔案與 overrides 中的現有檔案同名；為確保原檔案不變，已阻止覆蓋。",
        "dialog.choose_pack": "選擇整合包檔案", "dialog.pack_files": "整合包",
        "dialog.all_files": "所有檔案", "dialog.output_dir": "選擇輸出資料夾",
    },
    "en_US": {
        "app.name": "MC Modpack Tool",
        "nav.home": "Home", "nav.migration": "Version Migration",
        "nav.server": "Server Export", "nav.settings": "Settings",
        "app.subtitle": "Minecraft Modpack Workbench",
        "home.migration": "Modpack Version Migration", "home.server": "Modpack Server Export",
        "home.agreement": "User Agreement", "home.bilibili": "Bilibili Profile",
        "home.github": "GitHub", "home.github_pending": "GitHub (Coming Soon)",
        "author": "Author: FengchenWD",
        "placeholder.server": "The server export module will be completed in a later step.",
        "migration.title": "Modpack Version Migration",
        "migration.subtitle": "CurseForge / Modrinth target matching and compatibility checks",
        "migration.ready": "Ready", "migration.build": "Build New Modpack",
        "migration.pack_file": "Modpack File", "migration.choose_file": "Choose File",
        "migration.read_pack": "Read Modpack", "migration.source_overview": "Source Overview",
        "migration.target": "Migration Target", "migration.loader": "Loader",
        "migration.loader_version": "Loader Version", "migration.latest_loader": "Get Latest Stable",
        "migration.output_dir": "Output Folder", "migration.browse": "Browse",
        "migration.output_name": "File / Pack Name",
        "migration.embed_downloads": "Embed downloadable files",
        "migration.check": "Check Compatibility", "migration.report": "Compatibility Report",
        "migration.report_hint": "Read a modpack, then run a compatibility check",
        "migration.refresh": "Analyze Again", "migration.files": "File Details",
        "migration.log": "Activity Log", "tree.severity": "Severity",
        "tree.category": "Category", "tree.item": "Item", "tree.conclusion": "Result",
        "tree.name": "Name", "tree.source": "Source", "tree.status": "Status",
        "tree.output": "Output", "menu.exclude": "Exclude from Output",
        "menu.curseforge": "View on CurseForge", "menu.modrinth": "View on Modrinth",
        "settings.title": "Settings", "settings.subtitle": "Changes apply immediately and are saved automatically",
        "settings.language": "Language", "settings.language_desc": "Choose the language used by the application interface.",
        "settings.appearance": "Appearance", "settings.appearance_desc": "Switch between the light and dark themes.",
        "settings.light": "Light", "settings.dark": "Dark",
        "settings.accent": "Accent Color", "settings.accent_desc": "Choose the accent used for buttons, links, and selections.",
        "settings.custom_color": "Custom Color", "settings.reset_color": "Reset",
        "settings.font": "Interface Font", "settings.font_desc": "Choose from fonts installed on this computer.",
        "settings.default_font": "System Default", "settings.font_preview": "MC Modpack Tool · Minecraft 1.21.1",
        "settings.color_dialog": "Choose Accent Color", "settings.saved": "Settings saved",
        "agreement.title": "User Agreement and Important Information", "agreement.version": "Agreement version {version}",
        "agreement.license": "View CC BY-NC-SA 4.0 License", "agreement.accept": "I Have Read and Agree",
        "agreement.decline": "Decline and Exit", "common.close": "Close",
        "common.info": "Information", "common.not_configured": "Not Configured",
        "github.pending_title": "GitHub Repository Not Available Yet",
        "github.pending_message": "The GitHub entry is ready. Add the repository URL after it is created to enable this button.",
        "category.mod": "Mod", "category.resourcepack": "Resource Pack", "category.shaderpack": "Shader Pack",
        "status.found": "Found", "status.not_found": "Not Found", "status.pending": "Pending",
        "status.preserved": "Preserved (target environment unchanged)", "status.passthrough": "Preserved",
        "status.unknown": "Unknown", "status.excluded": "Excluded from Output",
        "action.restore": "Restore", "action.exclude": "Exclude",
        "compat.error": "Blocking", "compat.warning": "Warning", "compat.info": "Info",
        "compat.boundary": "Limit", "compat.pass": "Pass", "compat.overall": "Overall",
        "compat.static": "Static Check", "compat.description": "Description",
        "compat.no_issues": "No statically verifiable blocking issues or warnings were found.",
        "compat.summary": "{errors} blocking · {warnings} warnings · {items} content items",
        "compat.scope.mod": "Mod", "compat.scope.resourcepack": "Resource Pack",
        "compat.scope.shaderpack": "Shader Pack", "compat.scope.content": "Content",
        "compat.scope.dependency": "Dependency", "compat.scope.output": "Output",
        "compat.scope.general": "General", "compat.item.modpack": "Modpack",
        "compat.issue.item_not_found": "No file was found for the target version.",
        "compat.issue.missing_required_dependency": "A required dependency may be missing: {dependency}.",
        "compat.issue.explicit_incompatibility": "Explicitly conflicts with selected item {item}.",
        "compat.issue.explicitly_incompatible_item": "Platform metadata explicitly marks this item as incompatible.",
        "compat.issue.duplicate_project": "The same platform project appears more than once.",
        "compat.issue.duplicate_output_path": "Multiple files will be written to the same output path.",
        "compat.issue.unsafe_output_path": "The target path may escape the modpack directory.",
        "compat.issue.required_embedded_download_unavailable": "This CurseForge item must be embedded in a Modrinth pack, but no download URL is available.",
        "compat.issue.required_embedded_scope_unsupported": "This CurseForge item must be embedded, but its original Modrinth client/server scope cannot be preserved safely.",
        "compat.issue.override_output_collision": "The target file has the same name as an existing overrides file; overwrite was blocked to preserve it.",
        "dialog.choose_pack": "Choose Modpack File", "dialog.pack_files": "Modpacks",
        "dialog.all_files": "All Files", "dialog.output_dir": "Choose Output Folder",
    },
}

TRANSLATIONS["zh_CN"].update({
    "runtime.input_changed": "输入文件已变化，请重新读取整合包",
    "runtime.target_changed": "目标设置已变化，请重新检查兼容性",
    "runtime.output_changed": "输出内容已变化，请重新检查兼容性",
    "runtime.compat_rechecking": "正在重新检查兼容性...",
    "runtime.compat_stale": "目标设置已变化，已丢弃过期检查结果",
    "runtime.reading": "正在读取整合包...", "runtime.parsing": "正在解析...",
    "runtime.compat_generating": "正在生成兼容性报告...",
    "runtime.compat_blocking": "兼容性检查完成：存在待处理问题，可点击生成继续处理",
    "runtime.compat_warning": "兼容性检查完成：有警告，但可以生成",
    "runtime.compat_ready": "兼容性检查完成：可以生成",
    "runtime.unresolved": "仍有未处理的兼容性阻断，未生成整合包",
    "runtime.stopping": "正在停止后台任务并清理临时文件...",
    "runtime.searching_targets": "正在查找目标版本文件...",
    "runtime.searching_api": "正在 API 查找中...",
    "runtime.starting_cf": "自动开始 CurseForge 搜索...",
    "runtime.searching_cf": "正在 CurseForge 搜索中...",
    "runtime.all_search_done": "全部搜索完成！",
    "runtime.building": "正在生成整合包...", "runtime.build_done": "生成完成！",
    "runtime.build_done_notes": "生成完成，有需要查看的提醒", "runtime.error": "出错",
    "runtime.read_done": "读取完成：{summary}",
    "runtime.lookup_done": "查找完成：{found}/{total} 找到，{missing} 未找到",
    "runtime.cf_done": "CF 搜索完成，仍有 {missing} 个未找到",
    "dialog.error": "错误", "dialog.compatibility": "兼容性检查",
    "dialog.read_pack_first": "请先读取整合包。",
    "dialog.target_incomplete_title": "目标设置不完整",
    "dialog.target_incomplete": "请填写目标 Minecraft、加载器类型和加载器版本。",
    "dialog.select_file_first": "请先选中一个文件。",
    "dialog.valid_pack": "请先选择一个有效的整合包文件",
    "dialog.parse_pack_first": "请先解析整合包",
    "dialog.cannot_build": "无法生成",
    "dialog.target_changed": "目标设置已变化，请重新运行兼容性检查。",
    "dialog.check_first": "请先完成当前目标的兼容性检查。",
    "dialog.output_required": "请指定输出目录和文件名",
    "dialog.input_changed": "输入文件在读取后发生了变化。\n\n请先重新读取当前选择的整合包，再执行兼容性检查和生成。",
    "dialog.same_output": "输出文件不能与原整合包使用同一路径。\n\n请修改输出文件名或输出目录，以免覆盖原包。",
    "dialog.target_empty": "目标 MC、加载器类型和加载器版本均不能为空。",
    "dialog.overwrite_title": "覆盖文件",
    "dialog.overwrite": "输出文件已存在：\n{path}\n\n确定覆盖吗？",
    "dialog.exclude_title": "从输出排除",
    "dialog.exclude": "确定从新整合包排除「{name}」吗？\n\n原始整合包不会被修改。",
    "dialog.complete": "完成",
    "build.success_notice": "整合包导出成功！可能仍存在部分模组及前置版本冲突或不符合，软件还在持续开发中，带来不便尽情谅解",
    "build.location": "导出位置：\n{path}",
    "build.missing": "以下 {count} 个文件未包含：", "build.notes": "生成提醒（{count}）：",
    "build.more_items": "... 及其他 {count} 个", "build.more_notes": "... 及其他 {count} 条",
})
TRANSLATIONS["zh_HK"].update({
    "runtime.input_changed": "輸入檔案已變更，請重新讀取整合包",
    "runtime.target_changed": "目標設定已變更，請重新檢查兼容性",
    "runtime.output_changed": "輸出內容已變更，請重新檢查兼容性",
    "runtime.compat_rechecking": "正在重新檢查兼容性...",
    "runtime.compat_stale": "目標設定已變更，已捨棄過期檢查結果",
    "runtime.reading": "正在讀取整合包...", "runtime.parsing": "正在解析...",
    "runtime.compat_generating": "正在產生兼容性報告...",
    "runtime.compat_blocking": "兼容性檢查完成：有待處理問題，可按產生繼續處理",
    "runtime.compat_warning": "兼容性檢查完成：有警告，但可以產生",
    "runtime.compat_ready": "兼容性檢查完成：可以產生",
    "runtime.unresolved": "仍有未處理的兼容性阻斷，未產生整合包",
    "runtime.stopping": "正在停止背景工作並清理暫存檔案...",
    "runtime.searching_targets": "正在尋找目標版本檔案...",
    "runtime.searching_api": "正在透過 API 尋找...",
    "runtime.starting_cf": "自動開始 CurseForge 搜尋...",
    "runtime.searching_cf": "正在 CurseForge 搜尋...",
    "runtime.all_search_done": "全部搜尋完成！",
    "runtime.building": "正在產生整合包...", "runtime.build_done": "產生完成！",
    "runtime.build_done_notes": "產生完成，有需要查看的提示", "runtime.error": "發生錯誤",
    "runtime.read_done": "讀取完成：{summary}",
    "runtime.lookup_done": "尋找完成：{found}/{total} 已找到，{missing} 未找到",
    "runtime.cf_done": "CF 搜尋完成，仍有 {missing} 個未找到",
    "dialog.error": "錯誤", "dialog.compatibility": "兼容性檢查",
    "dialog.read_pack_first": "請先讀取整合包。",
    "dialog.target_incomplete_title": "目標設定不完整",
    "dialog.target_incomplete": "請填寫目標 Minecraft、載入器類型及載入器版本。",
    "dialog.select_file_first": "請先選擇一個檔案。",
    "dialog.valid_pack": "請先選擇有效的整合包檔案",
    "dialog.parse_pack_first": "請先解析整合包",
    "dialog.cannot_build": "無法產生",
    "dialog.target_changed": "目標設定已變更，請重新執行兼容性檢查。",
    "dialog.check_first": "請先完成目前目標的兼容性檢查。",
    "dialog.output_required": "請指定輸出資料夾及檔案名稱",
    "dialog.input_changed": "輸入檔案在讀取後已變更。\n\n請先重新讀取目前選擇的整合包，再執行兼容性檢查及產生。",
    "dialog.same_output": "輸出檔案不能與原整合包使用相同路徑。\n\n請修改輸出檔案名稱或資料夾，以免覆蓋原包。",
    "dialog.target_empty": "目標 MC、載入器類型及載入器版本均不能留空。",
    "dialog.overwrite_title": "覆蓋檔案",
    "dialog.overwrite": "輸出檔案已存在：\n{path}\n\n確定要覆蓋嗎？",
    "dialog.exclude_title": "從輸出排除",
    "dialog.exclude": "確定從新整合包排除「{name}」嗎？\n\n原始整合包不會被修改。",
    "dialog.complete": "完成",
    "build.success_notice": "整合包匯出成功！部分模組及前置版本仍可能有衝突或不符合要求。軟件仍在持續開發，敬請見諒。",
    "build.location": "匯出位置：\n{path}",
    "build.missing": "以下 {count} 個檔案未包含：", "build.notes": "產生提示（{count}）：",
    "build.more_items": "... 以及其他 {count} 個", "build.more_notes": "... 以及其他 {count} 項",
})
TRANSLATIONS["en_US"].update({
    "runtime.input_changed": "The input file changed. Read the modpack again.",
    "runtime.target_changed": "Target settings changed. Check compatibility again.",
    "runtime.output_changed": "Output content changed. Check compatibility again.",
    "runtime.compat_rechecking": "Checking compatibility again...",
    "runtime.compat_stale": "Target settings changed; the stale result was discarded.",
    "runtime.reading": "Reading modpack...", "runtime.parsing": "Parsing...",
    "runtime.compat_generating": "Generating compatibility report...",
    "runtime.compat_blocking": "Compatibility check complete: resolve the blocking issues before export",
    "runtime.compat_warning": "Compatibility check complete: warnings found, export is available",
    "runtime.compat_ready": "Compatibility check complete: ready to export",
    "runtime.unresolved": "Blocking compatibility issues remain; no modpack was built",
    "runtime.stopping": "Stopping background tasks and cleaning temporary files...",
    "runtime.searching_targets": "Looking for target-version files...",
    "runtime.searching_api": "Searching platform APIs...",
    "runtime.starting_cf": "Starting the CurseForge fallback search...",
    "runtime.searching_cf": "Searching CurseForge...",
    "runtime.all_search_done": "All searches are complete.",
    "runtime.building": "Building modpack...", "runtime.build_done": "Build complete.",
    "runtime.build_done_notes": "Build complete with notices", "runtime.error": "Error",
    "runtime.read_done": "Read complete: {summary}",
    "runtime.lookup_done": "Search complete: {found}/{total} found, {missing} missing",
    "runtime.cf_done": "CurseForge search complete; {missing} items remain missing",
    "dialog.error": "Error", "dialog.compatibility": "Compatibility Check",
    "dialog.read_pack_first": "Read a modpack first.",
    "dialog.target_incomplete_title": "Incomplete Target Settings",
    "dialog.target_incomplete": "Enter a target Minecraft version, loader type, and loader version.",
    "dialog.select_file_first": "Select a file first.",
    "dialog.valid_pack": "Choose a valid modpack file first.",
    "dialog.parse_pack_first": "Parse a modpack first.",
    "dialog.cannot_build": "Cannot Build",
    "dialog.target_changed": "Target settings changed. Run the compatibility check again.",
    "dialog.check_first": "Complete the compatibility check for the current target first.",
    "dialog.output_required": "Choose an output folder and file name.",
    "dialog.input_changed": "The input file changed after it was read.\n\nRead the currently selected modpack again, then rerun compatibility checks and export.",
    "dialog.same_output": "The output cannot use the same path as the source modpack.\n\nChange the output file name or folder to avoid overwriting the source.",
    "dialog.target_empty": "Target Minecraft, loader type, and loader version are all required.",
    "dialog.overwrite_title": "Overwrite File",
    "dialog.overwrite": "The output file already exists:\n{path}\n\nOverwrite it?",
    "dialog.exclude_title": "Exclude from Output",
    "dialog.exclude": "Exclude “{name}” from the new modpack?\n\nThe source modpack will not be modified.",
    "dialog.complete": "Complete",
    "build.success_notice": "Modpack export succeeded. Some mod or dependency versions may still conflict or be unsuitable. This application remains under active development; thank you for your understanding.",
    "build.location": "Export location:\n{path}",
    "build.missing": "The following {count} files were not included:", "build.notes": "Build notices ({count}):",
    "build.more_items": "... and {count} more", "build.more_notes": "... and {count} more",
})
TRANSLATIONS["zh_CN"].update({
    "deps.title": "可能缺少前置模组",
    "deps.intro": "平台元数据显示，当前输出列表可能缺少以下必需前置模组：",
    "deps.owner": "{reference}（{platform}；由 {owners} 声明）",
    "deps.more_owners": " 等 {count} 个项目", "deps.more": "... 以及其他 {count} 个可能缺少的前置模组",
    "deps.footer": "本工具只做提示，不会自动查询、添加、恢复或启用任何模组。\n请根据整合包实际情况自行判断是否需要调整。",
    "blockers.title": "仍有阻断问题",
    "blockers.body": "仍有 {count} 项兼容性阻断未解决，因此没有生成整合包。\n\n可以再次点击“生成新整合包”重新处理，或在内容列表中手动排除相关项目。",
    "resolution.not_found_title": "未找到目标版本",
    "resolution.not_found_export": "「{name}」没有找到适用于目标 Minecraft / 加载器的版本。\n\n是否从新整合包中排除？\n\n该项目没有可导出的目标文件。选择“否”将取消本次导出，项目不会被排除或保留为旧版本。",
    "resolution.not_found_check": "「{name}」没有找到适用于目标 Minecraft / 加载器的版本。\n\n是否从新整合包中排除？\n\n选择“否”仅忽略本次提示，不会排除项目或保留旧版本；生成整合包时会再次询问。",
    "resolution.item_title": "项目存在阻断问题",
    "resolution.item_body": "「{name}」存在以下阻断问题：\n{details}\n\n是否从新整合包中排除？",
    "resolution.duplicate_title": "输出路径重复",
    "resolution.duplicate_body": "「{name}」与其他项目会写入同一路径：\n{path}\n\n是否排除此项目并保留列表中排在前面的项目？",
    "resolution.unknown_path": "未知路径",
    "detail.confirmed": "已确认", "detail.heuristic": "启发式判断", "detail.incomplete": "检查不完整",
    "detail.confidence": "可信度：{value}", "detail.code": "检查代码：{value}",
    "detail.path": "路径：{value}", "detail.evidence": "证据：{value}",
})
TRANSLATIONS["zh_HK"].update({
    "deps.title": "可能欠缺前置模組",
    "deps.intro": "平台資料顯示，目前輸出清單可能欠缺以下必要前置模組：",
    "deps.owner": "{reference}（{platform}；由 {owners} 聲明）",
    "deps.more_owners": " 等 {count} 個項目", "deps.more": "... 以及其他 {count} 個可能欠缺的前置模組",
    "deps.footer": "本工具只作提示，不會自動查詢、新增、回復或啟用任何模組。\n請按整合包實際情況自行判斷是否需要調整。",
    "blockers.title": "仍有阻斷問題",
    "blockers.body": "仍有 {count} 項兼容性阻斷未解決，因此沒有產生整合包。\n\n可再次按「產生新整合包」重新處理，或在內容清單中手動排除相關項目。",
    "resolution.not_found_title": "未找到目標版本",
    "resolution.not_found_export": "「{name}」沒有找到適用於目標 Minecraft / 載入器的版本。\n\n是否從新整合包中排除？\n\n此項目沒有可匯出的目標檔案。選擇「否」會取消本次匯出，項目不會被排除或保留為舊版本。",
    "resolution.not_found_check": "「{name}」沒有找到適用於目標 Minecraft / 載入器的版本。\n\n是否從新整合包中排除？\n\n選擇「否」只會略過本次提示，不會排除項目或保留舊版本；產生整合包時會再次詢問。",
    "resolution.item_title": "項目有阻斷問題",
    "resolution.item_body": "「{name}」有以下阻斷問題：\n{details}\n\n是否從新整合包中排除？",
    "resolution.duplicate_title": "輸出路徑重複",
    "resolution.duplicate_body": "「{name}」與其他項目會寫入相同路徑：\n{path}\n\n是否排除此項目並保留清單中排在前面的項目？",
    "resolution.unknown_path": "未知路徑",
    "detail.confirmed": "已確認", "detail.heuristic": "啟發式判斷", "detail.incomplete": "檢查不完整",
    "detail.confidence": "可信度：{value}", "detail.code": "檢查代碼：{value}",
    "detail.path": "路徑：{value}", "detail.evidence": "證據：{value}",
})
TRANSLATIONS["en_US"].update({
    "deps.title": "Potentially Missing Dependencies",
    "deps.intro": "Platform metadata indicates that these required dependencies may be missing from the output:",
    "deps.owner": "{reference} ({platform}; declared by {owners})",
    "deps.more_owners": " and {count} more items", "deps.more": "... and {count} more potentially missing dependencies",
    "deps.footer": "This tool only reports the metadata. It will not search for, add, restore, or enable any mod automatically.\nReview the modpack and decide whether changes are required.",
    "blockers.title": "Blocking Issues Remain",
    "blockers.body": "{count} blocking compatibility issues remain, so no modpack was built.\n\nClick “Build New Modpack” to review them again, or exclude the affected items in the file list.",
    "resolution.not_found_title": "No Target Version Found",
    "resolution.not_found_export": "No version of “{name}” was found for the target Minecraft version and loader.\n\nExclude it from the new modpack?\n\nThere is no target file to export. Choosing No cancels this export; the item will not be excluded or preserved at its old version.",
    "resolution.not_found_check": "No version of “{name}” was found for the target Minecraft version and loader.\n\nExclude it from the new modpack?\n\nChoosing No dismisses this prompt only. The item will not be excluded or preserved at its old version, and export will ask again.",
    "resolution.item_title": "Item Has Blocking Issues",
    "resolution.item_body": "“{name}” has the following blocking issues:\n{details}\n\nExclude it from the new modpack?",
    "resolution.duplicate_title": "Duplicate Output Path",
    "resolution.duplicate_body": "“{name}” and another item will write to the same path:\n{path}\n\nExclude this item and keep the item that appears first?",
    "resolution.unknown_path": "Unknown path",
    "detail.confirmed": "Confirmed", "detail.heuristic": "Heuristic", "detail.incomplete": "Incomplete",
    "detail.confidence": "Confidence: {value}", "detail.code": "Check code: {value}",
    "detail.path": "Path: {value}", "detail.evidence": "Evidence: {value}",
})

TRANSLATIONS["zh_CN"].update({
    "agreement.save_error_title": "无法保存同意状态",
    "agreement.save_error_message": "本次仍可继续使用，但软件无法写入当前用户配置。下次启动时需要重新确认协议。",
    "agreement.language": "协议语言",
    "settings.save_error_title": "设置未保存",
    "settings.save_error_message": "当前更改已在本次运行中生效，但无法写入用户配置。下次启动时可能恢复原设置。",
    "output.new_pack": "{target} 新整合包", "output.migrated_suffix": "（迁移）",
    "content.project": "项目 #{project_id}", "content.unknown_file": "未知文件",
    "build.item": "{name} [{category}]", "build.disabled_item": "[禁用] {name}",
    "build.reason.override_collision": "{item}（与 overrides 现有文件同名，未覆盖原文件）",
    "build.reason.env_scope": "{item}（无法保留 env 作用域）",
    "build.warning.cf_override_collision": "{name}：目标路径与 overrides 现有文件同名，已保留原文件并使用联网安装引用。",
    "build.warning.mr_override_collision": "{name}：目标路径与 overrides 现有文件同名，已保留原文件和联网安装引用。",
    "build.warning.cf_download_fallback": "{name}：下载失败，已回退为 CurseForge 联网安装引用。",
    "build.warning.cf_no_download": "{name}：平台未提供下载地址，已保留 CurseForge 联网安装引用。",
    "build.warning.mr_env_reference": "{name}：为保留 Modrinth env 作用域，已保留联网安装引用。",
    "build.warning.mr_download_fallback": "{name}：下载失败，已回退为 Modrinth 联网安装引用。",
    "build.warning.disabled_download_preserved": "[禁用] {name}：目标下载失败，已保留旧禁用版本。",
    "build.warning.disabled_no_target_preserved": "[禁用] {name}：未找到目标版本，已保留旧禁用版本。",
    "error.parse": "读取整合包失败。请检查文件是否完整、格式是否受支持，并查看运行日志。",
    "error.compatibility": "兼容性分析失败。请查看运行日志后重试。",
    "error.identity": "CurseForge 文件身份解析失败，兼容性检查已中止。请稍后重试。",
    "error.lookup": "平台版本查询已中止。请检查网络连接后重试。",
    "error.cf_fallback": "CurseForge 回退搜索已中止。请检查网络连接后重试。",
    "error.build": "生成整合包失败。请查看运行日志并确认输出位置可写。",
    "error.generic": "操作失败。请查看运行日志获取技术详情。",
    "common.unknown_platform": "未知平台", "common.unknown_item": "未知项目",
    "limitation.bytecode": "静态检查无法验证模组字节码、Mixin、注册表、数据包及仅运行时出现的冲突。",
    "limitation.direct_relations": "只检查可识别的直接必需依赖和明确冲突；不会验证可选/未知关系、递归依赖或跨平台项目身份。",
    "limitation.metadata_absent": "部分项目没有提供依赖/冲突元数据，这些项目无法完成静态确认。",
    "overview.untitled": "未命名整合包", "overview.unknown": "未知",
    "overview.body": "{name}\n\n平台格式    {format}\nMinecraft   {minecraft}\n加载器      {loader}\n内容        {content}\n原样保留    overrides 中的全部文件（不查询、不迁移、不改写）",
    "count.mods": "{count} 个模组", "count.resourcepacks": "{count} 个资源包",
    "count.shaderpacks": "{count} 个光影包", "count.disabled": "{count} 个禁用",
    "count.excluded": "{count} 个已排除", "count.files": "{count} 个文件",
    "status.warning_beta_alpha": "仅 Beta/Alpha 版",
    "status.warning_release_type": "仅 {release_type} 版",
    "status.disabled": "禁用",
    "runtime.read_ready": "已读取整合包，请确认目标设置后运行兼容性检查",
})
TRANSLATIONS["zh_HK"].update({
    "agreement.save_error_title": "無法儲存同意狀態",
    "agreement.save_error_message": "本次仍可繼續使用，但軟件無法寫入目前的用戶設定。下次啟動時需要重新確認協議。",
    "agreement.language": "協議語言",
    "settings.save_error_title": "設定未儲存",
    "settings.save_error_message": "目前變更已在本次運行中生效，但無法寫入用戶設定。下次啟動時可能回復原設定。",
    "output.new_pack": "{target} 新整合包", "output.migrated_suffix": "（已遷移）",
    "content.project": "項目 #{project_id}", "content.unknown_file": "未知檔案",
    "build.item": "{name} [{category}]", "build.disabled_item": "[已停用] {name}",
    "build.reason.override_collision": "{item}（與 overrides 現有檔案同名，未覆蓋原檔案）",
    "build.reason.env_scope": "{item}（無法保留 env 作用範圍）",
    "build.warning.cf_override_collision": "{name}：目標路徑與 overrides 現有檔案同名，已保留原檔案並使用聯網安裝引用。",
    "build.warning.mr_override_collision": "{name}：目標路徑與 overrides 現有檔案同名，已保留原檔案及聯網安裝引用。",
    "build.warning.cf_download_fallback": "{name}：下載失敗，已回復為 CurseForge 聯網安裝引用。",
    "build.warning.cf_no_download": "{name}：平台沒有提供下載位址，已保留 CurseForge 聯網安裝引用。",
    "build.warning.mr_env_reference": "{name}：為保留 Modrinth env 作用範圍，已保留聯網安裝引用。",
    "build.warning.mr_download_fallback": "{name}：下載失敗，已回復為 Modrinth 聯網安裝引用。",
    "build.warning.disabled_download_preserved": "[已停用] {name}：目標下載失敗，已保留舊停用版本。",
    "build.warning.disabled_no_target_preserved": "[已停用] {name}：未找到目標版本，已保留舊停用版本。",
    "error.parse": "讀取整合包失敗。請檢查檔案是否完整、格式是否受支援，並查看運行日誌。",
    "error.compatibility": "兼容性分析失敗。請查看運行日誌後重試。",
    "error.identity": "CurseForge 檔案身分解析失敗，兼容性檢查已中止。請稍後重試。",
    "error.lookup": "平台版本查詢已中止。請檢查網絡連線後重試。",
    "error.cf_fallback": "CurseForge 回退搜尋已中止。請檢查網絡連線後重試。",
    "error.build": "產生整合包失敗。請查看運行日誌並確認輸出位置可寫入。",
    "error.generic": "操作失敗。請查看運行日誌以取得技術詳情。",
    "common.unknown_platform": "未知平台", "common.unknown_item": "未知項目",
    "limitation.bytecode": "靜態檢查無法驗證模組位元組碼、Mixin、註冊表、資料包及只在執行時出現的衝突。",
    "limitation.direct_relations": "只檢查可識別的直接必要依賴及明確衝突；不會驗證可選／未知關係、遞迴依賴或跨平台項目身分。",
    "limitation.metadata_absent": "部分項目沒有提供依賴／衝突資料，這些項目無法完成靜態確認。",
    "overview.untitled": "未命名整合包", "overview.unknown": "未知",
    "overview.body": "{name}\n\n平台格式    {format}\nMinecraft   {minecraft}\n載入器      {loader}\n內容        {content}\n原樣保留    overrides 中的全部檔案（不查詢、不遷移、不改寫）",
    "count.mods": "{count} 個模組", "count.resourcepacks": "{count} 個資源包",
    "count.shaderpacks": "{count} 個光影包", "count.disabled": "{count} 個已停用",
    "count.excluded": "{count} 個已排除", "count.files": "{count} 個檔案",
    "status.warning_beta_alpha": "只有 Beta／Alpha 版本",
    "status.warning_release_type": "只有 {release_type} 版本",
    "status.disabled": "已停用",
    "runtime.read_ready": "已讀取整合包，請確認目標設定後執行兼容性檢查",
})
TRANSLATIONS["en_US"].update({
    "agreement.save_error_title": "Could Not Save Agreement Status",
    "agreement.save_error_message": "You may continue this session, but the application could not update your user configuration. You will need to accept the agreement again the next time it starts.",
    "agreement.language": "Agreement Language",
    "settings.save_error_title": "Settings Not Saved",
    "settings.save_error_message": "The change is active for this session, but the user configuration could not be updated. The previous setting may return the next time the application starts.",
    "output.new_pack": "{target} New Modpack", "output.migrated_suffix": " (Migrated)",
    "content.project": "Project #{project_id}", "content.unknown_file": "Unknown File",
    "build.item": "{name} [{category}]", "build.disabled_item": "[Disabled] {name}",
    "build.reason.override_collision": "{item} (an overrides file has the same name; the existing file was not overwritten)",
    "build.reason.env_scope": "{item} (the env scope could not be preserved)",
    "build.warning.cf_override_collision": "{name}: an overrides file has the same target path; the existing file and online-install reference were preserved.",
    "build.warning.mr_override_collision": "{name}: an overrides file has the same target path; the existing file and remote reference were preserved.",
    "build.warning.cf_download_fallback": "{name}: the download failed; a CurseForge online-install reference was used instead.",
    "build.warning.cf_no_download": "{name}: the platform did not provide a download URL; the CurseForge online-install reference was preserved.",
    "build.warning.mr_env_reference": "{name}: the remote Modrinth reference was preserved to keep its env scope.",
    "build.warning.mr_download_fallback": "{name}: the download failed; the Modrinth remote reference was used instead.",
    "build.warning.disabled_download_preserved": "[Disabled] {name}: the target download failed; the old disabled version was preserved.",
    "build.warning.disabled_no_target_preserved": "[Disabled] {name}: no target version was found; the old disabled version was preserved.",
    "error.parse": "The modpack could not be read. Verify that the file is complete and uses a supported format, then review the Activity Log.",
    "error.compatibility": "Compatibility analysis failed. Review the Activity Log and try again.",
    "error.identity": "CurseForge file identity resolution failed, so the compatibility check was stopped. Try again later.",
    "error.lookup": "The platform version lookup was stopped. Check the network connection and try again.",
    "error.cf_fallback": "The CurseForge fallback search was stopped. Check the network connection and try again.",
    "error.build": "The modpack could not be built. Review the Activity Log and verify that the output location is writable.",
    "error.generic": "The operation failed. Review the Activity Log for technical details.",
    "common.unknown_platform": "Unknown platform", "common.unknown_item": "Unknown item",
    "limitation.bytecode": "Static analysis cannot verify mod bytecode, Mixins, registries, data packs, or conflicts that appear only at runtime.",
    "limitation.direct_relations": "Only recognized direct required dependencies and explicit conflicts are checked; optional or unknown relations, transitive dependencies, and cross-platform project identity are not verified.",
    "limitation.metadata_absent": "Some projects did not provide dependency or conflict metadata and could not be verified completely.",
    "overview.untitled": "Untitled Modpack", "overview.unknown": "Unknown",
    "overview.body": "{name}\n\nPlatform      {format}\nMinecraft     {minecraft}\nLoader        {loader}\nContent       {content}\nPreserved     Every file in overrides (no lookup, migration, or modification)",
    "count.mods": "{count} mods", "count.resourcepacks": "{count} resource packs",
    "count.shaderpacks": "{count} shader packs", "count.disabled": "{count} disabled",
    "count.excluded": "{count} excluded", "count.files": "{count} files",
    "status.warning_beta_alpha": "Beta/Alpha release only",
    "status.warning_release_type": "{release_type} release only",
    "status.disabled": "Disabled",
    "runtime.read_ready": "Modpack loaded. Confirm the target settings, then run the compatibility check.",
})


def translate_text(language: str, key: str, **values) -> str:
    language_map = TRANSLATIONS.get(language, TRANSLATIONS["zh_CN"])
    template = language_map.get(key, TRANSLATIONS["zh_CN"].get(key, key))
    try:
        return template.format(**values)
    except (KeyError, ValueError):
        return template


def _normalize_hex_color(value: object, default: str = DEFAULT_ACCENT_COLOR) -> str:
    text = str(value or "").strip().upper()
    return text if re.fullmatch(r"#[0-9A-F]{6}", text) else default


def _mix_color(color: str, target: str, amount: float) -> str:
    color = _normalize_hex_color(color)
    target = _normalize_hex_color(target, "#000000")
    amount = max(0.0, min(1.0, float(amount)))
    source_rgb = tuple(int(color[index:index + 2], 16) for index in (1, 3, 5))
    target_rgb = tuple(int(target[index:index + 2], 16) for index in (1, 3, 5))
    mixed = tuple(round(source + (dest - source) * amount) for source, dest in zip(source_rgb, target_rgb))
    return "#" + "".join(f"{channel:02X}" for channel in mixed)


def _relative_luminance(color: str) -> float:
    normalized = _normalize_hex_color(color)
    channels = []
    for index in (1, 3, 5):
        value = int(normalized[index:index + 2], 16) / 255
        channels.append(value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4)
    red, green, blue = channels
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue


def _contrast_ratio(first: str, second: str) -> float:
    lighter, darker = sorted((_relative_luminance(first), _relative_luminance(second)), reverse=True)
    return (lighter + 0.05) / (darker + 0.05)


def _contrast_text(color: str) -> str:
    candidates = ("#10231F", "#FFFFFF")
    return max(candidates, key=lambda candidate: _contrast_ratio(candidate, color))


def _accessible_foreground(color: str, *backgrounds: str, minimum: float = 4.5) -> str:
    normalized = _normalize_hex_color(color)
    checked_backgrounds = backgrounds or ("#FFFFFF",)
    if min(_contrast_ratio(normalized, background) for background in checked_backgrounds) >= minimum:
        return normalized
    targets = ("#10231F", "#FFFFFF")
    target = max(
        targets,
        key=lambda candidate: min(_contrast_ratio(candidate, background) for background in checked_backgrounds),
    )
    for step in range(1, 21):
        candidate = _mix_color(normalized, target, step / 20)
        if min(_contrast_ratio(candidate, background) for background in checked_backgrounds) >= minimum:
            return candidate
    return target


def build_palette(theme: str, accent: str) -> dict[str, str]:
    mode = theme if theme in SUPPORTED_THEMES else "light"
    accent = _normalize_hex_color(accent)
    if mode == "dark":
        palette = {
            "app_bg": "#151B1E", "surface": "#1F282C", "surface_alt": "#263236",
            "sidebar": "#102723", "sidebar_hover": "#1D413A", "text": "#E8F0EE",
            "muted": "#9FB0AC", "border": "#3B494D", "input": "#182125",
            "heading": "#2A363A", "log_bg": "#0F1518", "log_fg": "#D7E4E0",
            "danger_bg": "#4B2529", "danger_fg": "#FFB4B8",
            "warning_bg": "#4A3A1E", "info_bg": "#203A53", "ok_bg": "#183D31",
            "disabled": "#52625F",
        }
        palette["accent_soft"] = _mix_color(accent, palette["surface"], 0.68)
        palette["accent_hover"] = _mix_color(accent, "#FFFFFF", 0.12)
        palette["sidebar_sub"] = _mix_color(accent, "#FFFFFF", 0.58)
    else:
        palette = {
            "app_bg": "#F5F7FA", "surface": "#FFFFFF", "surface_alt": "#EEF2F5",
            "sidebar": "#123B36", "sidebar_hover": "#20564F", "text": "#1F2933",
            "muted": "#667085", "border": "#D8DEE6", "input": "#FFFFFF",
            "heading": "#EEF2F5", "log_bg": "#18212B", "log_fg": "#D8E0E8",
            "danger_bg": "#FDECEC", "danger_fg": "#B42318",
            "warning_bg": "#FFF5E1", "info_bg": "#EEF4FF", "ok_bg": "#EAF7F0",
            "disabled": "#9ABCB5",
        }
        palette["accent_soft"] = _mix_color(accent, "#FFFFFF", 0.88)
        palette["accent_hover"] = _mix_color(accent, "#000000", 0.10)
        palette["sidebar_sub"] = "#A9CCC4"
    palette.update({
        "accent": accent,
        "accent_text": _contrast_text(accent),
        "accent_hover_text": _contrast_text(palette["accent_hover"]),
        "disabled_text": _contrast_text(palette["disabled"]),
        "link": _accessible_foreground(accent, palette["app_bg"], palette["surface"]),
        "sidebar_text": "#FFFFFF",
    })
    return palette

SEARCH_LIMIT = 30
CF_SEARCH_LIMIT = 30
CF_GAME_ID = 432
SEARCH_SCORE_MARGIN = 8.0
MAX_VERIFIED_SEARCH_CANDIDATES = 5
MAX_ARCHIVE_ENTRIES = 100_000
MAX_ARCHIVE_MEMBER_BYTES = 2 * 1024 * 1024 * 1024
MAX_DOWNLOAD_BYTES = MAX_ARCHIVE_MEMBER_BYTES
MAX_ARCHIVE_UNCOMPRESSED_BYTES = 8 * 1024 * 1024 * 1024
MAX_ARCHIVE_COMPRESSION_RATIO = 1_000
MIN_COMPRESSION_RATIO_CHECK_BYTES = 64 * 1024 * 1024
MAX_METADATA_BYTES = 16 * 1024 * 1024
ZIP_COPY_CHUNK_BYTES = 1024 * 1024
EXPORT_SUCCESS_NOTICE = (
    "整合包导出成功！可能仍存在部分模组及前置版本冲突或不符合，"
    "软件还在持续开发中，带来不便尽情谅解"
)
USER_AGREEMENT_VERSION = "2026-08-05-v3"
CC_BY_NC_SA_URL = "https://creativecommons.org/licenses/by-nc-sa/4.0/"
USER_AGREEMENT_TEXT = """
《MC整合包工具用户协议与使用须知》

生效日期：2026 年 8 月 5 日

在使用本软件前，请完整阅读并理解本协议。点击“我已阅读并同意”即表示你同意受本协议约束；如不同意，请退出并停止使用本软件。

一、软件与作者

1. 本软件名称为“MC整合包工具”，作者为 Bilibili UP 主“风尘WD”。
2. 本软件在设计、代码起草、检查、调试及文字整理过程中使用了 AI 工具辅助，并非全部由作者逐行人工编写。AI 辅助可能产生遗漏或错误，请结合兼容性报告、游戏日志及实际启动结果独立判断。
3. AI 工具及其服务提供方不是本软件的作者、维护者或担保方，也不对本软件的运行结果承担责任。
4. 本软件是围绕游戏《Minecraft》整合包处理而独立开发的第三方辅助工具，不包含、替代或授权《Minecraft》游戏本体，也并非 Minecraft 官方产品；本软件不由 Mojang Studios 或 Microsoft 开发、批准、认可、赞助或背书，本软件及作者与上述主体不存在隶属、代理或合作关系。
5. 就本软件当前设计、预期用途和分发方式而言，作者以遵守现行 Minecraft EULA 与 Usage Guidelines 为开发原则，不以修改、替代或未经授权分发游戏本体为目的。相关规则可能更新，应以官方现行文本为准；本条不构成对用户任何具体使用、修改或分发行为必然合规的保证。
   Minecraft EULA：https://www.minecraft.net/eula
   Minecraft Usage Guidelines：https://www.minecraft.net/usage-guidelines

二、许可协议（CC BY-NC-SA 4.0）

1. 本软件由作者依据“知识共享 署名—非商业性使用—相同方式共享 4.0 国际许可协议”（CC BY-NC-SA 4.0）免费许可和分发。
2. 在遵守许可条件的前提下，你可以复制、分享、转载本软件，也可以修改、改编并基于本软件创作。
3. 署名（BY）：分享或修改时，应以合理方式标注软件名称及作者“风尘WD”，提供本许可协议链接，保留已有版权与许可说明，并说明是否作出修改；不得暗示作者为你的版本、用途或行为背书。
4. 非商业性使用（NC）：不得将本软件或其修改版本主要用于获取商业利益或金钱报酬。商业授权需求应另行取得作者明确许可。
5. 相同方式共享（SA）：公开分发修改版本或演绎作品时，应继续采用 CC BY-NC-SA 4.0 或该许可允许的兼容许可。
6. 不得附加法律条款、数字版权管理措施或其他技术限制，阻止接收者行使本许可已经授予的权利。
7. 上述内容仅为主要条款摘要，不能替代许可协议法律文本。如摘要与正式文本不一致，以官方协议原文为准：
   https://creativecommons.org/licenses/by-nc-sa/4.0/

三、著作权与第三方权利

1. AI 辅助本身不当然改变作者对其具有独创性的人类创作、选择、编排、修改及整合部分享有的著作权和相关权利；具体权利范围以适用法律认定为准。
2. 在法律允许范围内，作者保留对软件功能说明、本协议未尽事项以及后续版本的解释和更新权。该约定不限制用户依法享有的权利，也不改变已经依据 CC BY-NC-SA 4.0 合法取得且依约行使的许可权利。
3. Minecraft、CurseForge、Modrinth、各加载器、模组、资源包、光影包、整合包内容、第三方库、商标及服务分别归其权利人所有，并适用各自的许可、用户协议与规则。本软件的许可不代表作者有权再次许可这些第三方内容。

四、使用条件与用户责任

1. 你应仅处理自己拥有或已获授权使用、迁移和分发的整合包及内容，并遵守适用法律、Minecraft EULA、平台规则和每个内容项目的许可条件。
2. 本软件不会授予你绕过下载限制、访问控制、平台规则或第三方许可的权利。因生成、上传、分享、运营或商业使用新整合包产生的合规责任由实施相关行为的用户承担。
3. 在迁移前应自行备份原整合包、配置、实例和世界存档。不得将本软件的静态兼容性报告视为模组一定可启动、存档一定安全或服务器一定稳定的保证。

五、联网、数据与本机文件

1. 为搜索项目、查询版本、获取加载器信息及按需下载文件，本软件会访问 CurseForge、Modrinth 及相关加载器或下载服务，并可能向这些服务发送项目 ID、文件哈希、文件名或搜索关键词、目标游戏版本和加载器等查询信息。
2. 部分核心功能需要联网。点击“我已阅读并同意”即表示你已知悉并同意本软件为实现上述功能发起必要的网络请求，并同意相关第三方服务依其规则处理请求所需信息；如不愿接受此类联网操作，请不要同意并停止使用本软件。
3. 网络中断、连接波动、DNS 或代理异常、防火墙或安全软件拦截、平台接口调整、授权变化、限流、维护或故障，以及地区网络可用性差异，均可能导致软件部分或全部功能暂时或持续无法使用、请求超时、查询或下载失败、结果不完整。作者不保证相关网络服务持续、及时或无错误可用；若介意此类风险，请勿使用本软件。
4. 本软件不会主动把你选择的整合包归档本体上传给作者。第三方服务仍可能按照其隐私政策和服务器日志规则处理你的网络地址、请求内容及其他必要连接信息。
5. 本软件会在本机创建配置记录和临时解压文件，并在正常退出时尝试清理临时内容。首次同意状态仅保存在本机配置中；删除该配置后，软件会再次显示本协议。

六、功能边界、免责声明与责任限制

1. 本软件仍在持续开发。平台元数据可能缺失、过时或错误；网络、API、下载权限、文件哈希、模组运行时行为及游戏版本差异均可能导致遗漏、误判、下载失败、启动崩溃、内容丢失或存档损坏。
2. 兼容性检查主要基于整合包清单和平台可用元数据，不执行 Minecraft 或模组代码，无法穷尽依赖版本范围、Mixin、注册表、数据包、配置、世界存档及仅在运行时出现的问题。
3. 在适用法律允许的最大范围内，本软件按“现状”和“可用状态”提供，不作适销性、特定用途适用性、无错误或不侵权等明示或默示保证。对于使用或无法使用本软件造成的间接损失、数据损失或业务中断，作者仅在法律规定的范围内承担责任；法律不得排除或限制的责任不受本条影响。

七、协议更新与其他

1. 作者可随软件功能、许可说明或合规要求更新本协议。协议发生实质更新时，后续版本可要求你重新阅读并同意；新协议不追溯剥夺已经依法取得的许可权利。
2. 本协议某一条款被认定无效或不可执行时，不影响其他条款的效力。
3. 点击同意仅表示你接受当前显示版本的协议。你可以选择不同意并退出软件。
""".strip()

USER_AGREEMENT_TEXTS = {
    "zh_CN": USER_AGREEMENT_TEXT,
    "zh_HK": """
《MC整合包工具用戶協議與使用須知》

生效日期：2026 年 8 月 5 日

使用本軟件前，請完整閱讀並理解本協議。按下「我已閱讀並同意」即表示你同意受本協議約束；如不同意，請退出並停止使用本軟件。

一、軟件與作者

1. 本軟件名稱為「MC整合包工具」，作者為 Bilibili UP 主「风尘WD」。
2. 本軟件在設計、程式碼起草、檢查、除錯及文字整理過程中使用了 AI 工具輔助，並非全部由作者逐行人工編寫。AI 輔助可能產生遺漏或錯誤，請結合兼容性報告、遊戲記錄及實際啟動結果獨立判斷。
3. AI 工具及其服務提供者不是本軟件的作者、維護者或擔保方，亦不對本軟件的運行結果承擔責任。
4. 本軟件是圍繞遊戲《Minecraft》整合包處理而獨立開發的第三方輔助工具，不包含、取代或授權《Minecraft》遊戲本體，亦並非 Minecraft 官方產品；本軟件並非由 Mojang Studios 或 Microsoft 開發、批准、認可、贊助或背書，本軟件及作者與上述主體不存在隸屬、代理或合作關係。
5. 就本軟件目前的設計、預期用途及發佈方式而言，作者以遵守現行 Minecraft EULA 及 Usage Guidelines 為開發原則，不以修改、取代或未經授權發佈遊戲本體為目的。相關規則可能更新，應以官方現行文本為準；本條不構成對用戶任何具體使用、修改或發佈行為必然合規的保證。
   Minecraft EULA：https://www.minecraft.net/eula
   Minecraft Usage Guidelines：https://www.minecraft.net/usage-guidelines

二、許可協議（CC BY-NC-SA 4.0）

1. 本軟件由作者依據「共享創意 姓名標示—非商業性—相同方式分享 4.0 國際許可協議」（CC BY-NC-SA 4.0）免費許可及發佈。
2. 在遵守許可條件的前提下，你可以複製、分享及轉載本軟件，也可以修改、改編並基於本軟件創作。
3. 姓名標示（BY）：分享或修改時，應以合理方式標示軟件名稱及作者帳戶名「风尘WD」，提供本許可協議連結，保留已有版權及許可說明，並說明是否作出修改；不得暗示作者為你的版本、用途或行為背書。
4. 非商業性使用（NC）：不得將本軟件或其修改版本主要用於獲取商業利益或金錢報酬。商業授權需要另行取得作者明確許可。
5. 相同方式共享（SA）：公開發佈修改版本或演繹作品時，應繼續採用 CC BY-NC-SA 4.0 或該許可允許的兼容許可。
6. 不得附加法律條款、數碼版權管理措施或其他技術限制，以阻止接收者行使本許可已授予的權利。
7. 上述內容只屬主要條款摘要，不能取代許可協議法律文本。如摘要與正式文本不一致，以官方協議原文為準：
   https://creativecommons.org/licenses/by-nc-sa/4.0/

三、版權與第三方權利

1. AI 輔助本身不當然改變作者對其具有獨創性的人類創作、選擇、編排、修改及整合部分享有的版權及相關權利；具體權利範圍以適用法律認定為準。
2. 在法律允許的範圍內，作者保留對軟件功能說明、本協議未盡事項及後續版本的解釋和更新權。此約定不限制用戶依法享有的權利，亦不改變已經依據 CC BY-NC-SA 4.0 合法取得並按約行使的許可權利。
3. Minecraft、CurseForge、Modrinth、各載入器、模組、資源包、光影包、整合包內容、第三方程式庫、商標及服務分別歸其權利人所有，並適用各自的許可、用戶協議及規則。本軟件的許可不代表作者有權再次許可這些第三方內容。

四、使用條件與用戶責任

1. 你只應處理自己擁有或已獲授權使用、遷移及發佈的整合包和內容，並遵守適用法律、Minecraft EULA、平台規則及每個內容項目的許可條件。
2. 本軟件不會授予你繞過下載限制、存取控制、平台規則或第三方許可的權利。因產生、上載、分享、營運或商業使用新整合包而產生的合規責任，由實施相關行為的用戶承擔。
3. 遷移前應自行備份原整合包、設定、實例及世界存檔。不得將本軟件的靜態兼容性報告視為模組必定可以啟動、存檔必定安全或伺服器必定穩定的保證。

五、聯網、資料與本機檔案

1. 為搜尋項目、查詢版本、取得載入器資料及按需要下載檔案，本軟件會存取 CurseForge、Modrinth 及相關載入器或下載服務，並可能向這些服務傳送項目 ID、檔案雜湊、檔案名稱或搜尋關鍵字、目標遊戲版本及載入器等查詢資料。
2. 部分核心功能需要聯網。按下「我已閱讀並同意」即表示你已知悉並同意本軟件為實現上述功能發出必要的網絡請求，並同意相關第三方服務按其規則處理請求所需資料；如不願接受此類聯網操作，請不要同意並停止使用本軟件。
3. 網絡中斷、連線波動、DNS 或代理異常、防火牆或保安軟件攔截、平台介面調整、授權變更、流量限制、維護或故障，以及地區網絡可用性差異，均可能導致軟件部分或全部功能暫時或持續無法使用、請求逾時、查詢或下載失敗、結果不完整。作者不保證相關網絡服務持續、及時或無錯誤可用；如介意此類風險，請勿使用本軟件。
4. 本軟件不會主動把你選擇的整合包封存檔本體上載給作者。第三方服務仍可能按照其私隱政策及伺服器記錄規則處理你的網絡位址、請求內容及其他必要連線資料。
5. 本軟件會在本機建立設定記錄及臨時解壓檔案，並在正常退出時嘗試清理臨時內容。首次同意狀態只儲存在本機設定中；刪除該設定後，軟件會再次顯示本協議。

六、功能邊界、免責聲明與責任限制

1. 本軟件仍在持續開發。平台資料可能缺失、過時或錯誤；網絡、API、下載權限、檔案雜湊、模組執行時行為及遊戲版本差異均可能導致遺漏、誤判、下載失敗、啟動崩潰、內容遺失或存檔損壞。
2. 兼容性檢查主要基於整合包清單及平台可用資料，不執行 Minecraft 或模組程式碼，無法窮盡依賴版本範圍、Mixin、註冊表、資料包、設定、世界存檔及只在執行時出現的問題。
3. 在適用法律允許的最大範圍內，本軟件按「現狀」及「可用狀態」提供，不就適銷性、特定用途適用性、無錯誤或不侵權作出任何明示或默示保證。對於使用或無法使用本軟件造成的間接損失、資料遺失或業務中斷，作者只在法律規定的範圍內承擔責任；法律不得排除或限制的責任不受本條影響。

七、協議更新與其他

1. 作者可因應軟件功能、許可說明或合規要求更新本協議。協議有實質更新時，後續版本可要求你重新閱讀並同意；新協議不追溯剝奪已經依法取得的許可權利。
2. 本協議任何條款被認定無效或不可執行時，不影響其他條款的效力。
3. 按下同意只表示你接受目前顯示版本的協議。你可以選擇不同意並退出軟件。
""".strip(),
    "en_US": """
MC Modpack Tool User Agreement and Important Information

Effective date: August 5, 2026

Read and understand this agreement in full before using the application. Clicking "I Have Read and Agree" means that you agree to be bound by it. If you do not agree, exit and stop using the application.

1. Application and Author

1. The application is named "MC Modpack Tool" (Chinese name: "MC整合包工具") and is authored by Bilibili creator FengchenWD (风尘WD).
2. AI tools assisted with design, code drafting, review, debugging, and writing. The application was not written entirely by the author line by line. AI assistance may introduce omissions or errors, so use the compatibility report, game logs, and actual launch results to make your own assessment.
3. The providers of the AI tools and services are not authors, maintainers, or guarantors of this application and are not responsible for its operation.
4. This is an independently developed third-party utility for processing Minecraft modpacks. It does not include, replace, or license the Minecraft game and is not an official Minecraft product. It is not developed, approved, endorsed, sponsored, or supported by Mojang Studios or Microsoft, and neither the application nor its author has an affiliation, agency, or partnership relationship with those entities.
5. The application's current design, intended use, and distribution model are developed with the current Minecraft EULA and Usage Guidelines in mind. It is not intended to modify, replace, or distribute the game itself without authorization. Those rules may change and the current official text controls. This paragraph does not guarantee that any particular use, modification, or distribution by a user will comply with those rules.
   Minecraft EULA: https://www.minecraft.net/eula
   Minecraft Usage Guidelines: https://www.minecraft.net/usage-guidelines

2. License (CC BY-NC-SA 4.0)

1. The author licenses and distributes this application free of charge under the Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International license (CC BY-NC-SA 4.0).
2. Subject to the license terms, you may copy, share, and redistribute the application, and may remix, transform, and build upon it.
3. Attribution (BY): When sharing or modifying the application, give appropriate credit to the application and its author, FengchenWD, provide a link to the license, retain existing copyright and license notices, and indicate whether changes were made. You may not imply that the author endorses your version, use, or conduct.
4. NonCommercial (NC): You may not use the application or a modified version primarily for commercial advantage or monetary compensation. Commercial licensing requires separate, express permission from the author.
5. ShareAlike (SA): If you publicly distribute a modified version or derivative work, you must use CC BY-NC-SA 4.0 or a compatible license permitted by it.
6. You may not impose additional legal terms, digital rights management, or technological restrictions that prevent recipients from exercising rights granted by the license.
7. The text above summarizes major terms and does not replace the legal code. If the summary conflicts with the official license, the official license controls:
   https://creativecommons.org/licenses/by-nc-sa/4.0/

3. Copyright and Third-Party Rights

1. AI assistance does not by itself alter copyright or related rights in the author's original human-created expression, selection, arrangement, modification, and integration. The exact scope of rights is determined under applicable law.
2. To the extent permitted by law, the author retains the right to explain and update application functionality, matters not covered by this agreement, and later releases. This provision does not restrict rights granted to users by law and does not alter license rights lawfully obtained and exercised under CC BY-NC-SA 4.0.
3. Minecraft, CurseForge, Modrinth, loaders, mods, resource packs, shader packs, modpack content, third-party libraries, trademarks, and services belong to their respective rights holders and are governed by their own licenses, agreements, and rules. This application's license does not mean that the author can relicense third-party content.

4. Conditions of Use and User Responsibility

1. Process only modpacks and content that you own or are authorized to use, migrate, and distribute. Follow applicable law, the Minecraft EULA, platform rules, and the license terms of each content item.
2. This application does not grant permission to bypass download restrictions, access controls, platform rules, or third-party licenses. The user who creates, uploads, shares, operates, or commercially uses a new modpack is responsible for compliance arising from those actions.
3. Back up the source modpack, configurations, instances, and worlds before migration. Do not treat the static compatibility report as a guarantee that mods will launch, worlds will remain safe, or servers will remain stable.

5. Network Access, Data, and Local Files

1. To search for projects, query versions, retrieve loader information, and download requested files, the application connects to CurseForge, Modrinth, and related loader or download services. Queries may include project IDs, file hashes, filenames or search terms, target game versions, and loader information.
2. Some core features require network access. Clicking "I Have Read and Agree" means that you understand and consent to the network requests required for those features and to the relevant third-party services processing the information required by each request under their own rules. If you do not accept this network activity, do not agree and stop using the application.
3. Network outages or instability, DNS or proxy issues, firewall or security software, API changes, authorization changes, rate limits, maintenance or outages, and regional differences in network availability may cause some or all features to be temporarily or persistently unavailable, requests to time out, searches or downloads to fail, or results to be incomplete. The author does not guarantee continuous, timely, or error-free availability of third-party network services. Do not use the application if you do not accept these risks.
4. As part of its normal operation, the application does not upload the selected modpack archive itself to the author. Third-party services may still process your network address, request data, and other connection information under their privacy policies and server logging practices.
5. The application creates local configuration records and temporary extracted files and attempts to clean temporary content during a normal exit. Agreement acceptance is stored only in the local user configuration. Deleting that configuration causes the agreement to be shown again.

6. Functional Limits, Disclaimer, and Limitation of Liability

1. The application remains under development. Platform metadata may be missing, outdated, or incorrect. Network conditions, APIs, download permissions, file hashes, mod runtime behavior, and game-version differences may cause omissions, incorrect conclusions, failed downloads, launch crashes, content loss, or world corruption.
2. Compatibility checks are primarily based on modpack manifests and available platform metadata. They do not execute Minecraft or mod code and cannot exhaustively identify dependency-version ranges, Mixins, registries, data packs, configurations, worlds, or issues that occur only at runtime.
3. To the fullest extent permitted by applicable law, the application is provided "as is" and "as available," without express or implied warranties of merchantability, fitness for a particular purpose, freedom from errors, or non-infringement. The author is responsible for indirect loss, data loss, or business interruption caused by use or inability to use the application only to the extent required by law. Liability that cannot lawfully be excluded or limited is unaffected.

7. Updates and Other Terms

1. The author may update this agreement as application functionality, licensing information, or compliance requirements change. A later release may require renewed acceptance after a material update. A new agreement does not retroactively remove license rights already obtained under law.
2. If any provision is found invalid or unenforceable, the remaining provisions remain effective.
3. Clicking agree means only that you accept the version currently displayed. You may decline and exit the application.
""".strip(),
}

USER_AGREEMENT_SECTION_HEADINGS = {
    "zh_CN": (
        "一、软件与作者",
        "二、许可协议（CC BY-NC-SA 4.0）",
        "三、著作权与第三方权利",
        "四、使用条件与用户责任",
        "五、联网、数据与本机文件",
        "六、功能边界、免责声明与责任限制",
        "七、协议更新与其他",
    ),
    "zh_HK": (
        "一、軟件與作者",
        "二、許可協議（CC BY-NC-SA 4.0）",
        "三、版權與第三方權利",
        "四、使用條件與用戶責任",
        "五、聯網、資料與本機檔案",
        "六、功能邊界、免責聲明與責任限制",
        "七、協議更新與其他",
    ),
    "en_US": (
        "1. Application and Author",
        "2. License (CC BY-NC-SA 4.0)",
        "3. Copyright and Third-Party Rights",
        "4. Conditions of Use and User Responsibility",
        "5. Network Access, Data, and Local Files",
        "6. Functional Limits, Disclaimer, and Limitation of Liability",
        "7. Updates and Other Terms",
    ),
}

WINDOWS_RESERVED_NAMES = {
    "con", "prn", "aux", "nul",
    *(f"com{index}" for index in range(1, 10)),
    *(f"lpt{index}" for index in range(1, 10)),
}

try:
    # This module exists only while a release EXE is being built and is gitignored.
    from build_secrets import CURSEFORGE_API_KEY as _BUILTIN_CF_API_KEY
except (ImportError, AttributeError):
    _BUILTIN_CF_API_KEY = ""


def _resolve_curseforge_api_key(builtin_key: str = _BUILTIN_CF_API_KEY) -> str:
    return os.environ.get("CURSEFORGE_API_KEY", "").strip() or str(builtin_key or "").strip()


CF_API_KEY = _resolve_curseforge_api_key()
CF_CLASS_IDS = {"mod": 6, "resourcepack": 12, "shaderpack": 6552}


def generate_output_pack_name(
    source_name: str,
    source_mc: str,
    target_mc: str,
    language: str = "zh_CN",
) -> str:
    """Generate a target-aware pack name without silently overwriting manual edits."""
    name = str(source_name or "").strip()
    source_version = str(source_mc or "").strip()
    target_version = str(target_mc or "").strip()
    if not name:
        return translate_text(language, "output.new_pack", target=target_version).strip()
    if not target_version:
        return name

    candidate = name
    replaced = False
    if source_version:
        source_pattern = re.compile(
            rf"(?<![\d.]){re.escape(source_version)}(?![\d.])"
        )
        candidate, replacements = source_pattern.subn(target_version, name, count=1)
        replaced = replacements > 0
    else:
        leading_version = re.match(
            r"^(?P<prefix>\s*)(?P<version>\d+\.\d+(?:\.\d+)?)(?=$|[\s_-])",
            name,
        )
        if leading_version:
            start, end = leading_version.span("version")
            candidate = f"{name[:start]}{target_version}{name[end:]}"
            replaced = True

    if not replaced:
        candidate = f"{target_version} {name}"
    if candidate.casefold() == name.casefold():
        return f"{name}{translate_text(language, 'output.migrated_suffix')}"
    return candidate


def paths_refer_to_same_location(first: str, second: str) -> bool:
    if not first or not second:
        return False
    try:
        if os.path.exists(first) and os.path.exists(second):
            return os.path.samefile(first, second)
    except OSError:
        pass
    return os.path.normcase(os.path.realpath(os.path.abspath(first))) == os.path.normcase(
        os.path.realpath(os.path.abspath(second))
    )


class OperationCancelled(RuntimeError):
    """Raised when the user closes the app during a background operation."""


def _check_cancelled(cancel_event: threading.Event | None) -> None:
    if cancel_event is not None and cancel_event.is_set():
        raise OperationCancelled("操作已取消")


# ============================================================
# 数据类
# ============================================================

@dataclass
class ModInfo:
    name: str = ""
    project_id: str = ""
    file_id: str = ""
    version_id: str = ""
    download_url: str = ""
    file_name: str = ""
    file_size: int = 0
    hashes: dict = field(default_factory=dict)
    old_mc_version: str = ""
    old_loader: str = ""
    category: str = "mod"
    disabled: bool = False
    excluded: bool = False
    passthrough: bool = False
    required: bool = True
    file_path: str = ""
    environment: dict = field(default_factory=dict)
    status: str = "pending"
    target_file_id: str = ""
    target_download_url: str = ""
    target_version_id: str = ""
    target_file_name: str = ""
    target_file_size: int = 0
    target_hashes: dict = field(default_factory=dict)
    note: str = ""
    source: str = ""
    cf_slug: str = ""
    mr_slug: str = ""
    target_dependencies: list[dict] = field(default_factory=list)
    dependency_metadata_available: bool = False
    original_entry: dict = field(default_factory=dict)
    identity_locked: bool = False
    preserve_original: bool = False
    original_project_id: str = ""
    original_source: str = ""


@dataclass
class ModpackInfo:
    format_type: str = ""
    mc_version: str = ""
    loader_type: str = ""
    loader_version: str = ""
    mods: list[ModInfo] = field(default_factory=list)
    overrides_dir: str = ""
    override_paths: set[str] = field(default_factory=set)
    passthrough_files: list[dict] = field(default_factory=list)
    raw_data: dict = field(default_factory=dict)


@dataclass
class BuildResult:
    missing_files: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)


# ============================================================
# 调试日志器
# ============================================================

class Logger:
    _callback = None
    @classmethod
    def set_callback(cls, cb): cls._callback = cb
    @classmethod
    def log(cls, level: str, msg: str):
        if cls._callback: cls._callback(level, msg)
        else:
            message = f"[{level.upper()}] {msg}"
            try:
                print(message)
            except UnicodeEncodeError:
                encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
                safe_message = message.encode(encoding, errors="replace").decode(encoding)
                print(safe_message)


# ============================================================
# 工具函数
# ============================================================

def generate_game_version_candidates(target_mc: str, strict: bool = False) -> list[str]:
    if strict: return [target_mc]
    parts = target_mc.split(".")
    candidates = [target_mc]
    if len(parts) >= 3:
        candidates.append(".".join(parts[:2]))
    return candidates

def extract_curseforge_hashes(file_data: dict) -> dict[str, str]:
    """Convert CurseForge's numeric hash records to hashlib names."""
    algorithm_names = {1: "sha1", 2: "md5"}
    result: dict[str, str] = {}
    for item in file_data.get("hashes", []) or []:
        name = algorithm_names.get(item.get("algo"))
        value = str(item.get("value", "")).lower()
        if name and value:
            result[name] = value
    return result

def normalize_curseforge_dependencies(file_data: dict) -> list[dict]:
    relation_types = {1: "embedded", 2: "optional", 3: "required", 4: "tool", 5: "incompatible"}
    return [
        {
            "project_id": str(item.get("modId", "")),
            "dependency_type": relation_types.get(item.get("relationType"), "unknown"),
        }
        for item in (file_data.get("dependencies", []) or [])
        if item.get("modId")
    ]

def select_primary_file(files: list[dict]) -> dict | None:
    if not files: return None
    return next((item for item in files if item.get("primary") is True), files[0])


def select_usable_primary_file(files: list[dict]) -> dict | None:
    primary = select_primary_file(files)
    if not primary:
        return None
    if not primary.get("filename") or not primary.get("url") or not primary.get("hashes"):
        return None
    return primary


def normalize_loader_name(loader: str) -> str:
    normalized = re.sub(r"[^a-z0-9]+", "", str(loader).casefold())
    return {
        "fabricloader": "fabric",
        "quiltloader": "quilt",
        "neoforged": "neoforge",
        "neo": "neoforge",
    }.get(normalized, normalized)


def parse_curseforge_file_id(urls: Iterable[str]) -> str:
    """Extract the exact CurseForge file ID encoded in trusted ForgeCDN URLs."""

    for url in urls:
        try:
            parsed = urlparse(str(url))
        except (TypeError, ValueError):
            continue
        host = (parsed.hostname or "").casefold().rstrip(".")
        if host != "forgecdn.net" and not host.endswith(".forgecdn.net"):
            continue
        match = re.search(r"(?:^|/)files/(\d+)/(\d{1,3})(?:/|$)", parsed.path, re.IGNORECASE)
        if not match:
            continue
        prefix, suffix = match.groups()
        return str(int(prefix) * 1000 + int(suffix))
    return ""


def environment_requires_scoped_handling(environment: dict | None) -> bool:
    if not environment:
        return False
    client = str(environment.get("client", "required")).casefold()
    server = str(environment.get("server", "required")).casefold()
    return client != "required" or server != "required"

def split_concatenated_words(text: str) -> str:
    if not text or ' ' in text: return text
    result = re.sub(r'([a-z])([A-Z])', r'\1 \2', text)
    result = re.sub(r'([a-zA-Z])(\d)', r'\1 \2', result)
    result = re.sub(r'(\d)([a-zA-Z])', r'\1 \2', result)
    if result != text: return result.lower()
    common_words = [
        'smooth','chunk','save','cup','board','cupboard',
        'library','quest','freeze','fix','ultimine','chain',
        'mining','map','mini','jei','roughly','enough',
        'items','just','cloth','config','api','fabric',
        'forge','mod','core','block','in','stay','true',
        'resource','pack','shader','better','ores',
        'cobblemon','compat','tensura','ftb','shine',
    ]
    common_words.sort(key=len, reverse=True)
    remaining = text.lower(); found_words = []
    while remaining:
        matched = False
        for word in common_words:
            if remaining.startswith(word):
                found_words.append(word); remaining = remaining[len(word):]; matched = True; break
        if not matched: found_words.append(remaining[0]); remaining = remaining[1:]
    result = ' '.join(found_words)
    return result if result != text.lower() else text

_SEARCH_EXTENSIONS = (".jar.disabled", ".disabled", ".jar", ".zip", ".litematic", ".mrpack")
_SEARCH_IGNORED_TOKENS = {
    "and", "the", "for", "with", "of", "in", "on", "to", "by",
    "mod", "mods", "pack", "resource", "texture", "shader", "edition", "version",
    "mc", "minecraft", "fabric", "forge", "neoforge", "quilt", "loader", "jar", "zip",
}
_SEARCH_ROLE_TOKENS = {
    "addon", "addons", "bridge", "compat", "compatibility", "extension", "integration",
    "patch", "plugin", "port", "support", "unofficial", "plus", "fork", "forked",
    "redux", "reborn", "continued", "tweak", "tweaks",
}
_SEARCH_TOKEN_ALIASES = {"lib": "library", "libs": "library"}


def _strip_search_noise(value: str) -> str:
    text = unquote(PurePosixPath(str(value).replace("\\", "/")).name).strip()
    lowered = text.casefold()
    for extension in _SEARCH_EXTENSIONS:
        if lowered.endswith(extension):
            text = text[: -len(extension)]
            break
    # Keep bracket contents: they may be a real brand (for example "[Let's Do]").
    # Chinese-only annotations naturally disappear during ASCII identity tokenization.
    text = re.sub(r"[][()【】（）《》「」]", " ", text)
    text = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", text)
    return text.strip()


def _is_version_token(token: str) -> bool:
    if token.isdigit():
        return True
    if re.fullmatch(r"(?:mc|v|r)\d+(?:[a-z]*\d*)?", token):
        return True
    return bool(re.fullmatch(r"\d+(?:alpha|beta|pre|preview|rc|snapshot|build)\d*", token))


def _identity_token_list(value: str) -> list[str]:
    cleaned = _strip_search_noise(value)
    raw_tokens = re.findall(r"[a-z0-9]+", cleaned.casefold())
    result: list[str] = []
    for token in raw_tokens:
        token = _SEARCH_TOKEN_ALIASES.get(token, token)
        if len(token) < 2 or token in _SEARCH_IGNORED_TOKENS or _is_version_token(token):
            continue
        expanded = split_concatenated_words(token) if token.isalpha() and len(token) >= 6 else token
        pieces = expanded.split()
        if len(pieces) > 1 and all(len(piece) >= 2 for piece in pieces):
            candidates = pieces
        else:
            candidates = [token]
        for candidate in candidates:
            candidate = _SEARCH_TOKEN_ALIASES.get(candidate, candidate)
            if candidate not in _SEARCH_IGNORED_TOKENS and candidate not in result:
                result.append(candidate)
    return result


def _build_orig_tokens(filename: str) -> set[str]:
    return set(_identity_token_list(filename))


def _identity_compact(value: str) -> str:
    return "".join(_identity_token_list(value))


def extract_search_queries(file_name: str) -> list[str]:
    tokens = _identity_token_list(file_name)
    if not tokens:
        return []
    candidates = [" ".join(tokens), "-".join(tokens), "".join(tokens)]
    if len(tokens) > 3:
        candidates.append(" ".join(tokens[:3]))
    if len(tokens) > 2:
        candidates.append(" ".join(tokens[:2]))
    if len(tokens) > 1 and len(tokens[0]) >= 3:
        candidates.append(tokens[0])
    result: list[str] = []
    seen: set[str] = set()
    for candidate in candidates:
        normalized = re.sub(r"\s+", " ", candidate).strip().casefold()
        if len(normalized) >= 2 and normalized not in seen:
            result.append(normalized)
            seen.add(normalized)
    return result


def generate_cf_search_queries(file_name: str) -> list[str]:
    return extract_search_queries(file_name)


@dataclass(frozen=True)
class _CandidateMatch:
    result: dict
    score: float
    exact: bool
    hits: int
    extra_count: int


def _evaluate_search_candidate(
    result: dict,
    original_filename: str,
    search_query: str,
    name_key: str,
) -> _CandidateMatch | None:
    original_tokens = set(_identity_token_list(original_filename))
    if not original_tokens:
        return None
    title = str(result.get(name_key, "") or "")
    slug = str(result.get("slug", "") or "")
    fields = [
        (value, set(_identity_token_list(value)), _identity_compact(value))
        for value in (title, slug)
        if value
    ]
    fields = [field for field in fields if field[1]]
    if not fields:
        return None

    candidate_tokens = set().union(*(field[1] for field in fields))
    added_roles = (candidate_tokens & _SEARCH_ROLE_TOKENS) - (original_tokens & _SEARCH_ROLE_TOKENS)
    if added_roles:
        Logger.log("debug", f"    拒绝附属项目 {title or slug}: 新增角色词 {sorted(added_roles)}")
        return None

    original_compact = _identity_compact(original_filename)
    query_tokens = set(_identity_token_list(search_query))

    # A title/slug that contains the complete short base name plus another product
    # word is a derived project, even when another field happens to look exact.
    for _value, field_tokens, field_compact in fields:
        extra_tokens = field_tokens - original_tokens
        if (
            original_tokens.issubset(field_tokens)
            and extra_tokens
            and field_compact != original_compact
        ):
            Logger.log(
                "debug",
                f"    拒绝本体超集 {title or slug}: 新增身份词 {sorted(extra_tokens)}",
            )
            return None

    field_matches: list[_CandidateMatch] = []
    for _value, field_tokens, field_compact in fields:
        exact = bool(original_compact) and field_compact == original_compact
        matched = original_tokens & field_tokens
        extra_tokens = field_tokens - original_tokens
        if exact:
            query_compact = _identity_compact(search_query)
            score = 135.0 + (5.0 if query_compact == field_compact else 0.0)
            field_matches.append(
                _CandidateMatch(result, score, True, len(original_tokens), 0)
            )
            continue
        if len(matched) < len(original_tokens):
            continue
        precision = len(matched) / len(field_tokens)
        if precision < 0.75:
            continue
        query_coverage = len(query_tokens & field_tokens) / max(len(query_tokens), 1)
        score = 70.0 + precision * 25.0 + query_coverage * 5.0 - len(extra_tokens) * 6.0
        field_matches.append(
            _CandidateMatch(result, score, False, len(matched), len(extra_tokens))
        )

    if not field_matches:
        best_coverage = max(len(original_tokens & field[1]) for field in fields)
        Logger.log(
            "debug",
            f"    拒绝低覆盖候选 {title or slug}: {best_coverage}/{len(original_tokens)} 核心词",
        )
        return None
    return max(
        field_matches,
        key=lambda item: (item.score, item.exact, item.hits, -item.extra_count),
    )


def _rank_search_candidates(
    results: Iterable[dict],
    original_filename: str,
    search_query: str,
    name_key: str,
) -> list[_CandidateMatch]:
    deduplicated: dict[str, _CandidateMatch] = {}
    for result in results:
        if not isinstance(result, dict):
            continue
        match = _evaluate_search_candidate(result, original_filename, search_query, name_key)
        if match is None:
            continue
        identity = str(
            result.get("project_id") or result.get("id") or result.get("slug")
            or f"{result.get(name_key, '')}|{result.get('slug', '')}"
        ).casefold()
        previous = deduplicated.get(identity)
        if previous is None or match.score > previous.score:
            deduplicated[identity] = match
    ranked = sorted(
        deduplicated.values(),
        key=lambda item: (item.score, item.exact, item.hits, -item.extra_count),
        reverse=True,
    )
    for item in ranked[:10]:
        Logger.log(
            "debug",
            f"    候选评分 {item.score:>5.1f}: {item.result.get(name_key) or item.result.get('slug', '?')}",
        )
    return ranked


def _pick_unambiguous_match(matches: list[_CandidateMatch]) -> _CandidateMatch | None:
    if not matches:
        return None
    best = matches[0]
    if len(matches) > 1:
        runner_up = matches[1]
        too_close = best.score - runner_up.score < SEARCH_SCORE_MARGIN
        if too_close and (not best.exact or runner_up.exact):
            Logger.log(
                "info",
                f"  搜索结果存在歧义（{best.score:.1f} vs {runner_up.score:.1f}），拒绝自动选择",
            )
            return None
    return best


def _pick_unambiguous_candidate(matches: list[_CandidateMatch]) -> dict | None:
    selected = _pick_unambiguous_match(matches)
    return selected.result if selected else None


def pick_best_search_result(results: list, ofn: str, sq: str) -> dict | None:
    return _pick_unambiguous_candidate(_rank_search_candidates(results, ofn, sq, "title"))


def pick_best_cf_result(results: list, ofn: str, sq: str) -> dict | None:
    return _pick_unambiguous_candidate(_rank_search_candidates(results, ofn, sq, "name"))


# ============================================================
# URL 解析
# ============================================================

def parse_download_urls(urls: list[str]) -> dict:
    result = {"project_id": "", "version_id": "", "source": ""}
    for url in urls:
        if not url: continue
        try:
            parsed = urlparse(url)
            parts = [p for p in parsed.path.split("/") if p]
            if "data" in parts and "versions" in parts:
                idx_d = parts.index("data"); idx_v = parts.index("versions")
                if idx_d + 1 < len(parts): result["project_id"] = parts[idx_d + 1]
                if idx_v + 1 < len(parts): result["version_id"] = parts[idx_v + 1]
                result["source"] = "modrinth"
                return result
        except Exception: pass
    return result


# ============================================================
# 加载器版本自动获取
# ============================================================

def _numeric_version_key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))

def _latest_numeric_version(versions: list[str]) -> str:
    stable = [v for v in versions if re.fullmatch(r"\d+(?:\.\d+)+", v or "")]
    return max(stable, key=_numeric_version_key) if stable else ""

def fetch_latest_loader_version(
    loader_type: str,
    mc_version: str,
    cancel_event: threading.Event | None = None,
) -> str:
    """
    获取指定加载器在指定MC版本下的最新稳定版本。
    失败时返回空字符串。
    """
    loader_type = loader_type.strip().lower()
    if loader_type not in {"fabric", "forge", "neoforge", "quilt"}:
        Logger.log("warn", f"不支持的加载器: {loader_type}")
        return ""
    if not re.fullmatch(r"\d+\.\d+(?:\.\d+)?", mc_version):
        Logger.log("warn", f"无效的 Minecraft 版本: {mc_version}")
        return ""

    try:
        _check_cancelled(cancel_event)
        if loader_type == "fabric":
            resp = requests.get(
                f"https://meta.fabricmc.net/v2/versions/loader/{mc_version}",
                timeout=(8, 15))
            _check_cancelled(cancel_event)
            resp.raise_for_status()
            data = resp.json()
            versions = []
            for entry in data if isinstance(data, list) else []:
                loader = entry.get("loader", entry)
                if loader.get("stable") is True and loader.get("version"):
                    versions.append(loader["version"])
            return _latest_numeric_version(versions)
        elif loader_type == "forge":
            resp = requests.get(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
                timeout=(8, 15))
            _check_cancelled(cancel_event)
            resp.raise_for_status()
            data = resp.json()
            promos = data.get("promos", {})
            for suffix in ("latest", "recommended"):
                key = f"{mc_version}-{suffix}"
                if key in promos:
                    return promos[key]
        elif loader_type == "neoforge":
            resp = requests.get(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
                timeout=(8, 15))
            _check_cancelled(cancel_event)
            resp.raise_for_status()
            root = ET.fromstring(resp.text)
            mc_parts = [int(part) for part in mc_version.split(".")]
            if mc_parts[0] != 1 or len(mc_parts) < 2:
                return ""
            if mc_version == "1.20.1":
                neo_prefix = "47.1."
            else:
                neo_prefix = f"{mc_parts[1]}.{mc_parts[2] if len(mc_parts) > 2 else 0}."
            versions = [
                node.text for node in root.iter()
                if node.tag.rsplit("}", 1)[-1] == "version" and node.text
                and re.fullmatch(re.escape(neo_prefix) + r"\d+", node.text)
            ]
            return _latest_numeric_version(versions)
        elif loader_type == "quilt":
            resp = requests.get(
                f"https://meta.quiltmc.org/v3/versions/loader/{mc_version}",
                timeout=(8, 15))
            _check_cancelled(cancel_event)
            resp.raise_for_status()
            data = resp.json()
            versions = []
            for entry in data if isinstance(data, list) else []:
                loader = entry.get("loader", entry)
                version = loader.get("version", "")
                if version:
                    versions.append(version)
            return _latest_numeric_version(versions)
    except OperationCancelled:
        raise
    except Exception as e:
        Logger.log("warn", f"获取加载器版本失败 ({loader_type}): {e}")
    return ""


# ============================================================
# API 封装
# ============================================================

class APIRequestError(RuntimeError):
    """A platform request failed and the current lookup batch should stop."""

    def __init__(self, service: str, message: str, status_code: int | None = None):
        super().__init__(message)
        self.service = service
        self.status_code = status_code


class APINotFoundError(APIRequestError):
    """The requested platform object does not exist; the service itself is healthy."""

class CurseForgeAPI:
    BASE = "https://api.curseforge.com/v1"
    def __init__(self, api_key: str = "", cancel_event: threading.Event | None = None):
        self.cancel_event = cancel_event
        self.session = requests.Session()
        self.session.headers.update({
            "Accept": "application/json",
            "User-Agent": f"MCPackMigrator/{APP_VERSION}",
        })
        self.set_api_key(api_key)
    def set_api_key(self, api_key: str):
        self.api_key = api_key.strip()
        if self.api_key: self.session.headers["x-api-key"] = self.api_key
        else: self.session.headers.pop("x-api-key", None)
    def _get(self, endpoint: str, params: dict | None = None) -> dict:
        _check_cancelled(self.cancel_event)
        if not self.api_key:
            raise Exception("未配置 CurseForge API Key。")
        url = f"{self.BASE}{endpoint}"
        try:
            resp = self.session.get(url, params=params, timeout=REQUEST_TIMEOUT)
        except requests.exceptions.Timeout as exc:
            raise APIRequestError("CurseForge", "CurseForge API 超时。") from exc
        except requests.exceptions.ConnectionError as exc:
            raise APIRequestError("CurseForge", "无法连接 CurseForge API。") from exc
        except requests.exceptions.RequestException as exc:
            raise APIRequestError("CurseForge", f"CurseForge API 请求失败：{exc}") from exc
        _check_cancelled(self.cancel_event)
        if resp.status_code == 404:
            raise APINotFoundError("CurseForge", "CurseForge 项目或文件不存在。", 404)
        if resp.status_code in (401, 403):
            raise APIRequestError("CurseForge", f"CurseForge API Key 无效或无权访问（{resp.status_code}）。", resp.status_code)
        if resp.status_code == 429:
            raise APIRequestError("CurseForge", "CurseForge API 请求过于频繁（429），请稍后重试。", 429)
        if resp.status_code != 200:
            raise APIRequestError("CurseForge", f"CurseForge API 返回 HTTP {resp.status_code}。", resp.status_code)
        try:
            data = resp.json()
        except ValueError as exc:
            raise APIRequestError("CurseForge", "CurseForge API 返回了无效 JSON。") from exc
        if not isinstance(data, dict):
            raise APIRequestError("CurseForge", "CurseForge API 返回了意外的数据格式。")
        return data
    def _post(self, endpoint: str, payload: dict) -> dict:
        _check_cancelled(self.cancel_event)
        if not self.api_key:
            raise Exception("未配置 CurseForge API Key。")
        url = f"{self.BASE}{endpoint}"
        try:
            resp = self.session.post(url, json=payload, timeout=REQUEST_TIMEOUT)
        except requests.exceptions.Timeout as exc:
            raise APIRequestError("CurseForge", "CurseForge API 超时。") from exc
        except requests.exceptions.ConnectionError as exc:
            raise APIRequestError("CurseForge", "无法连接 CurseForge API。") from exc
        except requests.exceptions.RequestException as exc:
            raise APIRequestError("CurseForge", f"CurseForge API 请求失败：{exc}") from exc
        _check_cancelled(self.cancel_event)
        if resp.status_code in (401, 403):
            raise APIRequestError("CurseForge", f"CurseForge API Key 无效或无权访问（{resp.status_code}）。", resp.status_code)
        if resp.status_code == 429:
            raise APIRequestError("CurseForge", "CurseForge API 请求过于频繁（429），请稍后重试。", 429)
        if resp.status_code != 200:
            raise APIRequestError("CurseForge", f"CurseForge API 返回 HTTP {resp.status_code}。", resp.status_code)
        try:
            data = resp.json()
        except ValueError as exc:
            raise APIRequestError("CurseForge", "CurseForge API 返回了无效 JSON。") from exc
        if not isinstance(data, dict):
            raise APIRequestError("CurseForge", "CurseForge API 返回了意外的数据格式。")
        return data
    def search_mods(self, query: str, limit: int = CF_SEARCH_LIMIT,
                    category: str = "mod") -> list[dict]:
        params = {"gameId": CF_GAME_ID, "searchFilter": query, "pageSize": limit, "index": 0}
        class_id = CF_CLASS_IDS.get(category)
        if class_id: params["classId"] = class_id
        data = self._get("/mods/search", params); results = data.get("data", [])
        Logger.log("info", f"CF 搜索 '{query}' → {len(results)} 条结果"); return results
    def get_files(self, project_id: int) -> list[dict]:
        af = []; index = 0
        while True:
            _check_cancelled(self.cancel_event)
            data = self._get(f"/mods/{project_id}/files", {"index": index, "pageSize": 50})
            files = data.get("data", []); af.extend(files)
            if len(files) < 50: break
            index += 50
        return af

    def get_mod(self, project_id: int) -> dict:
        data = self._get(f"/mods/{project_id}")
        project = data.get("data", {})
        return project if isinstance(project, dict) else {}

    def get_files_by_ids(self, file_ids: Iterable[int | str]) -> dict[int, dict]:
        normalized = sorted({int(file_id) for file_id in file_ids if str(file_id).isdigit() and int(file_id) > 0})
        result: dict[int, dict] = {}
        for offset in range(0, len(normalized), 50):
            _check_cancelled(self.cancel_event)
            batch = normalized[offset:offset + 50]
            data = self._post("/mods/files", {"fileIds": batch}).get("data", [])
            if not isinstance(data, list):
                raise APIRequestError("CurseForge", "CurseForge 批量文件接口返回了意外的数据格式。")
            for item in data:
                if isinstance(item, dict) and str(item.get("id", "")).isdigit():
                    result[int(item["id"])] = item
        return result

    def find_target_file(self, project_id: int, target_mc: str, target_loader: str,
                         strict_mc: bool = True) -> dict | None:
        Logger.log("info", f"CF 查找 projectID={project_id} → MC={target_mc} loader={target_loader} strict={strict_mc}")
        files = self.get_files(project_id)

        mc_candidates = generate_game_version_candidates(target_mc, strict=strict_mc)
        for mc_ver in mc_candidates:
            candidates = []
            for f in files:
                gv = f.get("gameVersions", [])
                if mc_ver in gv:
                    if target_loader:
                        if any(v.lower() == target_loader.lower() for v in gv):
                            candidates.append((f.get("releaseType", 1), f.get("fileDate", ""), f))
                    else: candidates.append((f.get("releaseType", 1), f.get("fileDate", ""), f))
                    continue
            if candidates:
                candidates.sort(key=lambda x: (x[0], -self._parse_date(x[1])))
                Logger.log("info", f"CF 找到(mc≈{mc_ver}): {candidates[0][2].get('displayName','?')}")
                return candidates[0][2]
        Logger.log("warn", f"CF 未找到匹配 projectID={project_id}")
        return None

    def get_download_url(self, mod_id: int, file_id: int) -> str:
        try:
            data = self._get(f"/mods/{mod_id}/files/{file_id}/download-url")
            return data.get("data", "")
        except APINotFoundError:
            return ""
        except APIRequestError as exc:
            if exc.status_code == 403:
                Logger.log("info", f"CF 项目禁止第三方下载: mod={mod_id} file={file_id}")
                return ""
            raise
    @staticmethod
    def _parse_date(date_str: str) -> int:
        try: return int(datetime.fromisoformat(date_str.replace("Z", "+00:00")).timestamp())
        except Exception: return 0
    @staticmethod
    def make_mod_url(slug: str = "", project_id: int = 0, category: str = "mod") -> str:
        cat_map = {"mod": "mc-mods", "resourcepack": "texture-packs", "shaderpack": "shaders"}
        cat_path = cat_map.get(category, "mc-mods")
        if slug: return f"https://www.curseforge.com/minecraft/{cat_path}/{slug}"
        if project_id: return f"https://www.curseforge.com/minecraft/{cat_path}/{project_id}"
        return "https://www.curseforge.com/minecraft/search"


class ModrinthAPI:
    BASE = "https://api.modrinth.com/v2"
    def __init__(self, cancel_event: threading.Event | None = None):
        self.cancel_event = cancel_event
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent": f"FengchenWD/MCPackMigrator/{APP_VERSION}",
        })
    def _get(self, endpoint: str, params: dict | None = None) -> dict | list:
        _check_cancelled(self.cancel_event)
        url = f"{self.BASE}{endpoint}"
        try:
            resp = self.session.get(url, params=params, timeout=REQUEST_TIMEOUT)
        except requests.exceptions.Timeout as exc:
            raise APIRequestError("Modrinth", "Modrinth API 超时。") from exc
        except requests.exceptions.ConnectionError as exc:
            raise APIRequestError("Modrinth", "无法连接 Modrinth API。") from exc
        except requests.exceptions.RequestException as exc:
            raise APIRequestError("Modrinth", f"Modrinth API 请求失败：{exc}") from exc
        _check_cancelled(self.cancel_event)
        if resp.status_code == 404:
            raise APINotFoundError("Modrinth", "Modrinth 项目或版本不存在。", 404)
        if resp.status_code == 429:
            raise APIRequestError("Modrinth", "Modrinth API 请求过于频繁（429），请稍后重试。", 429)
        if resp.status_code != 200:
            raise APIRequestError("Modrinth", f"Modrinth API 返回 HTTP {resp.status_code}。", resp.status_code)
        try:
            return resp.json()
        except ValueError as exc:
            raise APIRequestError("Modrinth", "Modrinth API 返回了无效 JSON。") from exc
    def get_project(self, project_id: str) -> dict: return self._get(f"/project/{project_id}")  # type: ignore
    def get_all_versions(self, project_id: str) -> list[dict]:
        data = self._get(f"/project/{project_id}/version")
        return data if isinstance(data, list) else []

    def find_target_version(self, project_id: str, target_mc: str, target_loader: str,
                            strict_mc: bool = True) -> dict | None:
        Logger.log("info", f"MR 查找 project={project_id} → MC={target_mc} loader={target_loader} strict={strict_mc}")
        av = self.get_all_versions(project_id)
        if not av: return None

        mc_candidates = generate_game_version_candidates(target_mc, strict=strict_mc)
        Logger.log("debug", f"  候选版本号: {mc_candidates} (strict={strict_mc})")
        for mc_ver in mc_candidates:
            matching = []
            for v in av:
                gv_list = v.get("game_versions", [])
                if mc_ver in gv_list: matching.append(v); continue
            if not matching: continue
            Logger.log("debug", f"  mc={mc_ver}: {len(matching)} 个版本匹配")
            if target_loader:
                wanted_loader = target_loader.lower()
                candidates = [v for v in matching if wanted_loader in {
                    str(loader).lower() for loader in v.get("loaders", [])
                }]
            else:
                candidates = matching
            if candidates: return self._pick_best(candidates)
        Logger.log("warn", f"MR 未找到（strict={strict_mc}）")
        return None

    def _pick_best(self, versions: list[dict]) -> dict | None:
        candidates = []
        for v in versions:
            vt = v.get("version_type", "release"); priority = {"release": 1, "beta": 2, "alpha": 3}.get(vt, 4)
            candidates.append((priority, v.get("date_published", ""), v))
        if not candidates: return None
        candidates.sort(key=lambda x: (x[0], -self._parse_date(x[1])))
        return candidates[0][2]
    def search_project(self, query: str, loader: str | None = None,
                       project_type: str = "mod", limit: int = SEARCH_LIMIT) -> list[dict]:
        facets: list[list[str]] = []
        if loader: facets.append([f"categories:{loader}"])
        facets.append([f"project_type:{project_type}"])
        params: dict = {"query": query, "limit": limit}
        if facets: params["facets"] = json.dumps(facets)
        data = self._get("/search", params)
        return data.get("hits", []) if isinstance(data, dict) else []
    def lookup_by_hash(self, hash_val: str, algorithm: str = "sha1") -> dict | None:
        if not hash_val: return None
        try:
            data = self._get(f"/version_file/{hash_val}", {"algorithm": algorithm})
            if isinstance(data, dict) and "project_id" in data: return data
        except APINotFoundError:
            pass
        return None
    @staticmethod
    def _parse_date(date_str: str) -> int:
        try: return int(datetime.fromisoformat(date_str.replace("Z", "+00:00")).timestamp())
        except Exception: return 0
    @staticmethod
    def make_mod_url(project_id: str = "", slug: str = "") -> str:
        if project_id: return f"https://modrinth.com/mod/{project_id}"
        if slug: return f"https://modrinth.com/mod/{slug}"
        return "https://modrinth.com/search"


# ============================================================
# zip 构建
# ============================================================

def make_zip_with_forward_slashes(
    output_path: str,
    source_dir: str,
    *,
    overwrite: bool = False,
    cancel_event: threading.Event | None = None,
) -> None:
    _check_cancelled(cancel_event)
    output = Path(output_path).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=f".{output.name}.", suffix=".tmp", dir=output.parent)
    os.close(fd)
    try:
        with zipfile.ZipFile(temp_name, 'w', zipfile.ZIP_DEFLATED) as zf:
            for root, dirs, files in os.walk(source_dir):
                _check_cancelled(cancel_event)
                for file in files:
                    _check_cancelled(cancel_event)
                    file_path = os.path.join(root, file)
                    arcname = os.path.relpath(file_path, source_dir).replace('\\', '/')
                    zip_info = zipfile.ZipInfo.from_file(file_path, arcname)
                    zip_info.compress_type = zipfile.ZIP_DEFLATED
                    with open(file_path, "rb") as source, zf.open(zip_info, "w", force_zip64=True) as target:
                        while True:
                            _check_cancelled(cancel_event)
                            chunk = source.read(ZIP_COPY_CHUNK_BYTES)
                            if not chunk:
                                break
                            target.write(chunk)
        _check_cancelled(cancel_event)
        if overwrite:
            os.replace(temp_name, output)
        else:
            try:
                os.link(temp_name, output)
            except FileExistsError as exc:
                raise FileExistsError(f"输出文件已存在: {output}") from exc
            except OSError as link_error:
                if output.exists():
                    raise FileExistsError(f"输出文件已存在: {output}") from link_error
                if os.name != "nt":
                    raise OSError(f"目标文件系统不支持安全的原子新建: {output}") from link_error
                try:
                    os.rename(temp_name, output)
                except FileExistsError as exc:
                    raise FileExistsError(f"输出文件已存在: {output}") from exc
                else:
                    temp_name = ""
            else:
                os.remove(temp_name)
    finally:
        if os.path.exists(temp_name): os.remove(temp_name)


# ============================================================
# 整合包解析器
# ============================================================

class PackParser:
    OVERRIDE_ROOT_NAMES = {"overrides"}
    UNSUPPORTED_SCOPED_OVERRIDE_NAMES = {"client-overrides", "server-overrides"}

    @staticmethod
    def _validate_archive(
        file_path: str,
        cancel_event: threading.Event | None = None,
    ) -> None:
        _check_cancelled(cancel_event)
        with zipfile.ZipFile(file_path, "r") as zf:
            entries = zf.infolist()
            if len(entries) > MAX_ARCHIVE_ENTRIES:
                raise ValueError(f"整合包条目过多（上限 {MAX_ARCHIVE_ENTRIES}）。")
            total_size = 0
            for entry in entries:
                _check_cancelled(cancel_event)
                if entry.is_dir():
                    continue
                path_parts = tuple(
                    part.casefold() for part in entry.filename.replace("\\", "/").split("/") if part)
                scoped_root = next(
                    (part for part in path_parts if part in PackParser.UNSUPPORTED_SCOPED_OVERRIDE_NAMES), None)
                if scoped_root:
                    raise ValueError(
                        f"当前版本无法在保持作用域的前提下迁移 {scoped_root}，已停止以避免静默丢失内容。")
                if entry.flag_bits & 0x1:
                    raise ValueError(f"整合包包含加密条目，无法安全读取: {entry.filename}")
                if entry.file_size > MAX_ARCHIVE_MEMBER_BYTES:
                    raise ValueError(f"整合包单个文件过大: {entry.filename}")
                total_size += entry.file_size
                if total_size > MAX_ARCHIVE_UNCOMPRESSED_BYTES:
                    raise ValueError("整合包解压后总大小超过安全上限。")
                if entry.file_size >= MIN_COMPRESSION_RATIO_CHECK_BYTES:
                    ratio = entry.file_size / max(entry.compress_size, 1)
                    if ratio > MAX_ARCHIVE_COMPRESSION_RATIO:
                        raise ValueError(f"整合包条目压缩比异常: {entry.filename}")

    @staticmethod
    def _read_json_member(zf: zipfile.ZipFile, member_name: str) -> dict:
        entry = zf.getinfo(member_name)
        if entry.file_size > MAX_METADATA_BYTES:
            raise ValueError(f"整合包元数据文件过大: {member_name}")
        with zf.open(entry) as stream:
            raw = stream.read(MAX_METADATA_BYTES + 1)
        if len(raw) > MAX_METADATA_BYTES:
            raise ValueError(f"整合包元数据文件过大: {member_name}")
        try:
            data = json.loads(raw.decode("utf-8-sig"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError(f"整合包元数据无效: {member_name}: {exc}") from exc
        if not isinstance(data, dict):
            raise ValueError(f"整合包元数据根节点必须是对象: {member_name}")
        return data

    @staticmethod
    def detect_format(
        file_path: str,
        cancel_event: threading.Event | None = None,
    ) -> str:
        _check_cancelled(cancel_event)
        with zipfile.ZipFile(file_path, "r") as zf:
            manifest = PackParser._find_in_zip(zf, "manifest.json")
            index = PackParser._find_in_zip(zf, "modrinth.index.json")
            if manifest and index:
                raise ValueError("整合包同时包含 CurseForge 与 Modrinth 元数据，格式不明确。")
            if manifest: return "curseforge"
            if index: return "modrinth"
        return "unknown"
    @staticmethod
    def parse(
        file_path: str,
        cancel_event: threading.Event | None = None,
    ) -> ModpackInfo:
        PackParser._validate_archive(file_path, cancel_event)
        fmt = PackParser.detect_format(file_path, cancel_event)
        if fmt == "curseforge":
            info = PackParser._parse_curseforge(file_path, cancel_event)
        elif fmt == "modrinth":
            info = PackParser._parse_modrinth(file_path, cancel_event)
        else:
            raise ValueError("无法识别整合包格式")
        with zipfile.ZipFile(file_path, "r") as zf:
            info.override_paths = PackParser._collect_override_paths(zf, cancel_event)
        return info
    @staticmethod
    def _find_in_zip(zf: zipfile.ZipFile, filename: str) -> str | None:
        matches = []
        for name in zf.namelist():
            normalized = name.replace("\\", "/").rstrip("/")
            if normalized.count("/") <= 1 and PurePosixPath(normalized).name == filename:
                matches.append(name)
        if len(matches) > 1:
            raise ValueError(f"整合包包含重复的 {filename}，格式不明确。")
        return matches[0] if matches else None
    @staticmethod
    def _collect_override_paths(
        zf: zipfile.ZipFile,
        cancel_event: threading.Event | None = None,
    ) -> set[str]:
        paths: set[str] = set()
        for entry in zf.infolist():
            _check_cancelled(cancel_event)
            if entry.is_dir():
                continue
            parts = tuple(part for part in entry.filename.replace("\\", "/").split("/") if part)
            override_index = next(
                (index for index, part in enumerate(parts) if part.casefold() in PackParser.OVERRIDE_ROOT_NAMES),
                None,
            )
            if override_index is None:
                continue
            relative_parts = parts[override_index + 1:]
            if relative_parts:
                paths.add(PurePosixPath(*relative_parts).as_posix())
        return paths
    @staticmethod
    def _classify_by_path(path: str) -> str:
        p = path.lower().replace("\\", "/"); parts = p.split("/")
        if len(parts) >= 2:
            top = parts[0]
            if top in ("shaderpacks", "shaderpack"): return "shaderpack"
            if top in ("resourcepacks", "resourcepack"): return "resourcepack"
            if top == "mods": return "mod"
        return "other"
    @staticmethod
    def _parse_curseforge(
        file_path: str,
        cancel_event: threading.Event | None = None,
    ) -> ModpackInfo:
        _check_cancelled(cancel_event)
        with zipfile.ZipFile(file_path, "r") as zf:
            mp = PackParser._find_in_zip(zf, "manifest.json")
            if not mp: raise ValueError("未找到 manifest.json")
            manifest = PackParser._read_json_member(zf, mp)
        info = ModpackInfo(format_type="curseforge"); info.raw_data = manifest
        mc = manifest.get("minecraft", {}); info.mc_version = mc.get("version", "")
        loaders = mc.get("modLoaders", [])
        if loaders:
            selected_loader = next((loader for loader in loaders if loader.get("primary") is True), loaders[0])
            lid = selected_loader.get("id", "")
            if "-" in lid: info.loader_type, info.loader_version = lid.split("-", 1)
            else: info.loader_type = lid; info.loader_version = ""
        for f in manifest.get("files", []):
            _check_cancelled(cancel_event)
            info.mods.append(ModInfo(
                name=f"Project #{f.get('projectID')}", project_id=str(f.get("projectID", "")),
                file_id=str(f.get("fileID", "")), old_mc_version=info.mc_version,
                old_loader=info.loader_type, category="mod", status="pending", source="curseforge",
                required=bool(f.get("required", True)), original_entry=dict(f), identity_locked=True))
        return info
    @staticmethod
    def _parse_modrinth(
        file_path: str,
        cancel_event: threading.Event | None = None,
    ) -> ModpackInfo:
        _check_cancelled(cancel_event)
        with zipfile.ZipFile(file_path, "r") as zf:
            ip = PackParser._find_in_zip(zf, "modrinth.index.json")
            if not ip: raise ValueError("未找到 modrinth.index.json")
            index = PackParser._read_json_member(zf, ip)
        info = ModpackInfo(format_type="modrinth"); info.raw_data = index
        deps = index.get("dependencies", {})
        info.mc_version = deps.get("minecraft", "")
        loader_map = {"forge": "forge", "fabric-loader": "fabric", "neoforge": "neoforge", "quilt-loader": "quilt"}
        for key, lt in loader_map.items():
            if key in deps: info.loader_type = lt; info.loader_version = deps[key]; break
        for f in index.get("files", []):
            _check_cancelled(cancel_event)
            path_in_pack = f.get("path", "")
            downloads = [str(url) for url in (f.get("downloads", []) or []) if url]
            file_name = path_in_pack.split("/")[-1] if path_in_pack else ""
            hashes = f.get("hashes", {})
            ids = parse_download_urls(downloads)
            project_id = ids["project_id"]; version_id = ids["version_id"]
            curseforge_file_id = parse_curseforge_file_id(downloads)
            category = PackParser._classify_by_path(path_in_pack)
            if category == "other":
                info.passthrough_files.append(dict(f))
                continue
            is_disabled = file_name.endswith(".jar.disabled")
            source = "modrinth" if ids["source"] == "modrinth" else "unknown"
            info.mods.append(ModInfo(
                name=file_name or "未知文件", project_id=project_id, file_id=curseforge_file_id,
                version_id=version_id, download_url=downloads[0] if downloads else "",
                file_name=file_name, file_size=f.get("fileSize", 0) or 0, hashes=hashes,
                old_mc_version=info.mc_version, old_loader=info.loader_type,
                category=category, disabled=is_disabled, status="pending", source=source,
                file_path=path_in_pack, environment=dict(f.get("env", {}) or {}),
                original_entry=dict(f), identity_locked=bool(project_id or curseforge_file_id)))
        return info

    @staticmethod
    def extract_overrides(
        file_path: str,
        temp_dir: str,
        cancel_event: threading.Event | None = None,
    ) -> str:
        PackParser._validate_archive(file_path, cancel_event)
        with zipfile.ZipFile(file_path, "r") as zf:
            members: list[tuple[zipfile.ZipInfo, tuple[str, ...], int | None]] = []
            for entry in zf.infolist():
                _check_cancelled(cancel_event)
                raw_name = entry.filename.replace("\\", "/")
                path = PurePosixPath(raw_name)
                parts = tuple(part for part in path.parts if part not in ("", "."))
                if path.is_absolute() or ".." in parts or any(":" in part for part in parts):
                    raise ValueError(f"整合包包含不安全路径: {entry.filename}")
                for part in parts:
                    normalized_part = part.casefold()
                    stem = normalized_part.rstrip(" .").split(".", 1)[0]
                    if not part or part.endswith((" ", ".")) or stem in WINDOWS_RESERVED_NAMES or "\x00" in part:
                        raise ValueError(f"整合包包含 Windows 不安全路径: {entry.filename}")
                override_index = next(
                    (index for index, part in enumerate(parts) if part.casefold() in PackParser.OVERRIDE_ROOT_NAMES),
                    None,
                )
                members.append((entry, parts, override_index))

            base = Path(temp_dir).resolve()
            seen: set[str] = set()
            extracted_total = 0
            for entry, parts, override_index in members:
                _check_cancelled(cancel_event)
                if override_index is None:
                    continue
                rel_parts = parts[override_index + 1:]
                if not rel_parts or entry.is_dir():
                    continue
                dest = (base.joinpath(*rel_parts)).resolve()
                try:
                    dest.relative_to(base)
                except ValueError:
                    raise ValueError(f"整合包路径越界: {entry.filename}")
                dest_key = os.path.normcase(str(dest)).casefold()
                if dest_key in seen:
                    raise ValueError(f"整合包包含重复路径: {entry.filename}")
                seen.add(dest_key)
                dest.parent.mkdir(parents=True, exist_ok=True)
                member_size = 0
                with zf.open(entry) as src, open(dest, "wb") as dst:
                    while True:
                        _check_cancelled(cancel_event)
                        chunk = src.read(ZIP_COPY_CHUNK_BYTES)
                        if not chunk:
                            break
                        member_size += len(chunk)
                        extracted_total += len(chunk)
                        if member_size > MAX_ARCHIVE_MEMBER_BYTES or extracted_total > MAX_ARCHIVE_UNCOMPRESSED_BYTES:
                            raise ValueError("整合包解压内容超过安全上限。")
                        dst.write(chunk)
        return temp_dir


# ============================================================
# 整合包构建器
# ============================================================

class PackBuilder:
    CATEGORY_DIRS = {"mod": "mods", "resourcepack": "resourcepacks", "shaderpack": "shaderpacks"}

    @staticmethod
    def _order_entries_like_source(source_entries: Iterable[dict], entries: list[dict]) -> list[dict]:
        remaining = list(entries)
        ordered: list[dict] = []
        for source_entry in source_entries:
            for index, entry in enumerate(remaining):
                if entry == source_entry:
                    ordered.append(remaining.pop(index))
                    break
        ordered.extend(remaining)
        return ordered

    @staticmethod
    def _validate_target(tmc: str, tlt: str, tlv: str):
        if not tmc or not tlt or not tlv:
            raise ValueError("目标 MC、加载器类型和加载器版本均不能为空。")

    @staticmethod
    def _curseforge_entry(mod: ModInfo) -> dict | None:
        if not (mod.project_id.isdigit() and mod.target_file_id.isdigit()):
            return None
        return {"projectID": int(mod.project_id), "fileID": int(mod.target_file_id), "required": mod.required}

    @staticmethod
    def _modrinth_entry(mod: ModInfo) -> dict | None:
        if not (mod.target_download_url and mod.target_file_name and mod.target_hashes):
            return None
        directory = PackBuilder.CATEGORY_DIRS.get(mod.category, "mods")
        entry = {
            "path": f"{directory}/{mod.target_file_name}",
            "downloads": [mod.target_download_url],
            "hashes": mod.target_hashes,
            "fileSize": mod.target_file_size,
        }
        if mod.environment:
            entry["env"] = dict(mod.environment)
        return entry

    @staticmethod
    def _target_dir(overrides_dir: str, category: str) -> str:
        return os.path.join(overrides_dir, PackBuilder.CATEGORY_DIRS.get(category, "mods"))

    @staticmethod
    def _content_output_path(mod: ModInfo, *, disabled: bool = False) -> str:
        file_name = mod.target_file_name or mod.file_name
        if not file_name:
            return ""
        if disabled and not file_name.casefold().endswith(".disabled"):
            file_name += ".disabled"
        directory = PackBuilder.CATEGORY_DIRS.get(mod.category, "mods")
        return f"{directory}/{file_name}".replace("\\", "/")

    @staticmethod
    def _protected_override_paths(info: ModpackInfo) -> set[str]:
        return {path.replace("\\", "/").casefold() for path in info.override_paths}

    @staticmethod
    def build_curseforge(op: str, info: ModpackInfo, tmc: str, tlt: str, tlv: str,
                         od: str, download_mods: bool = False, pack_name: str = "",
                         overwrite: bool = False,
                         cancel_event: threading.Event | None = None) -> BuildResult:
        _check_cancelled(cancel_event)
        PackBuilder._validate_target(tmc, tlt, tlv)
        manifest = {
            "minecraft": {"version": tmc, "modLoaders": [{"id": f"{tlt}-{tlv}", "primary": True}]},
            "manifestType": "minecraftModpack", "manifestVersion": 1,
            "name": pack_name or info.raw_data.get("name", "Migrated Modpack"),
            "version": info.raw_data.get("version", "1.0.0"),
            "author": info.raw_data.get("author", ""),
            "files": [dict(entry) for entry in info.passthrough_files],
            "overrides": "overrides"}
        result = BuildResult(); dmf: list[ModInfo] = []
        protected_paths = PackBuilder._protected_override_paths(info)
        tmp = tempfile.mkdtemp(prefix="mcpack_")
        try:
            odd = os.path.join(tmp, "overrides")
            if od and os.path.isdir(od):
                PackBuilder._copy_overrides(od, odd, cancel_event)
            for mod in info.mods:
                _check_cancelled(cancel_event)
                if mod.passthrough:
                    continue
                if mod.excluded:
                    continue
                if mod.preserve_original and mod.original_entry:
                    manifest["files"].append(dict(mod.original_entry))
                    continue
                if mod.disabled:
                    dmf.append(mod); continue
                if mod.status not in ("found", "warning"):
                    result.missing_files.append(f"{mod.name} [{mod.category}]"); continue
                entry = PackBuilder._curseforge_entry(mod)
                if not entry:
                    result.missing_files.append(f"{mod.name} [{mod.category}]"); continue
                embedded = False
                output_collision = PackBuilder._content_output_path(mod).casefold() in protected_paths
                if output_collision:
                    result.warnings.append(
                        f"{mod.name}：目标路径与 overrides 现有文件同名，已保留原文件并使用联网安装引用。")
                elif download_mods:
                    if mod.target_download_url:
                        embedded = PackBuilder._download_mod(
                            mod.target_download_url, PackBuilder._target_dir(odd, mod.category),
                            mod.target_file_name or mod.file_name,
                            expected_size=mod.target_file_size, expected_hashes=mod.target_hashes,
                            cancel_event=cancel_event)
                        if not embedded:
                            Logger.log("warn", f"完整包下载失败，已保留清单引用: {mod.name}")
                            result.warnings.append(f"{mod.name}：下载失败，已回退为 CurseForge 联网安装引用。")
                    else:
                        result.warnings.append(f"{mod.name}：平台未提供下载地址，已保留 CurseForge 联网安装引用。")
                if not embedded: manifest["files"].append(entry)
            PackBuilder._handle_disabled(dmf, odd, result, cancel_event)
            _check_cancelled(cancel_event)
            manifest["files"] = PackBuilder._order_entries_like_source(
                info.raw_data.get("files", []) or [], manifest["files"])
            with open(os.path.join(tmp, "manifest.json"), "w", encoding="utf-8") as f:
                json.dump(manifest, f, indent=2, ensure_ascii=False)
            make_zip_with_forward_slashes(
                op, tmp, overwrite=overwrite, cancel_event=cancel_event)
        finally: shutil.rmtree(tmp, ignore_errors=True)
        return result

    @staticmethod
    def build_modrinth(op: str, info: ModpackInfo, tmc: str, tlt: str, tlv: str,
                        od: str, download_mods: bool = False, pack_name: str = "",
                        overwrite: bool = False,
                        cancel_event: threading.Event | None = None) -> BuildResult:
        _check_cancelled(cancel_event)
        PackBuilder._validate_target(tmc, tlt, tlv)
        lkm = {"forge": "forge", "fabric": "fabric-loader", "neoforge": "neoforge", "quilt": "quilt-loader"}
        deps = {"minecraft": tmc}
        deps[lkm.get(tlt, tlt)] = tlv
        index = {
            "game": "minecraft", "formatVersion": 1,
            "versionId": info.raw_data.get("versionId", "1.0.0"),
            "name": pack_name or info.raw_data.get("name", "Migrated Modpack"),
            "summary": info.raw_data.get("summary", ""),
            "files": [dict(entry) for entry in info.passthrough_files],
            "dependencies": deps}
        result = BuildResult(); dmf: list[ModInfo] = []
        protected_paths = PackBuilder._protected_override_paths(info)
        tmp = tempfile.mkdtemp(prefix="mcpack_")
        try:
            odd = os.path.join(tmp, "overrides")
            if od and os.path.isdir(od):
                PackBuilder._copy_overrides(od, odd, cancel_event)
            for mod in info.mods:
                _check_cancelled(cancel_event)
                if mod.passthrough:
                    continue
                if mod.excluded:
                    continue
                if mod.preserve_original and mod.original_entry:
                    index["files"].append(dict(mod.original_entry))
                    continue
                if mod.disabled:
                    dmf.append(mod); continue
                if mod.status not in ("found", "warning"):
                    result.missing_files.append(f"{mod.name} [{mod.category}]"); continue
                entry = PackBuilder._modrinth_entry(mod)
                must_embed = mod.source == "curseforge"
                scoped_environment = environment_requires_scoped_handling(mod.environment)
                output_collision = PackBuilder._content_output_path(mod).casefold() in protected_paths
                if output_collision:
                    if entry and not must_embed:
                        index["files"].append(entry)
                        result.warnings.append(
                            f"{mod.name}：目标路径与 overrides 现有文件同名，已保留原文件和联网安装引用。")
                    else:
                        result.missing_files.append(
                            f"{mod.name} [{mod.category}]（与 overrides 现有文件同名，未覆盖原文件）")
                    continue
                if must_embed and scoped_environment:
                    result.missing_files.append(f"{mod.name} [{mod.category}]（无法保留 env 作用域）")
                    continue
                embedded = False
                attempted_download = (download_mods or must_embed) and not scoped_environment
                if attempted_download and mod.target_download_url:
                    embedded = PackBuilder._download_mod(
                        mod.target_download_url, PackBuilder._target_dir(odd, mod.category),
                        mod.target_file_name or mod.file_name,
                        expected_size=mod.target_file_size, expected_hashes=mod.target_hashes,
                        cancel_event=cancel_event)
                if embedded: continue
                if entry and not must_embed:
                    index["files"].append(entry)
                    if download_mods and scoped_environment:
                        result.warnings.append(f"{mod.name}：为保留 Modrinth env 作用域，已保留联网安装引用。")
                    elif download_mods:
                        Logger.log("warn", f"完整包下载失败，已保留 Modrinth 引用: {mod.name}")
                        result.warnings.append(f"{mod.name}：下载失败，已回退为 Modrinth 联网安装引用。")
                else:
                    result.missing_files.append(f"{mod.name} [{mod.category}]")
            PackBuilder._handle_disabled(dmf, odd, result, cancel_event)
            _check_cancelled(cancel_event)
            index["files"] = PackBuilder._order_entries_like_source(
                info.raw_data.get("files", []) or [], index["files"])
            with open(os.path.join(tmp, "modrinth.index.json"), "w", encoding="utf-8") as f:
                json.dump(index, f, indent=2, ensure_ascii=False)
            make_zip_with_forward_slashes(
                op, tmp, overwrite=overwrite, cancel_event=cancel_event)
        finally: shutil.rmtree(tmp, ignore_errors=True)
        return result

    @staticmethod
    def _handle_disabled(
        dmf: list[ModInfo],
        odd: str,
        result: BuildResult,
        cancel_event: threading.Event | None = None,
    ):
        for mod in dmf:
            _check_cancelled(cancel_event)
            old_path = os.path.join(PackBuilder._target_dir(odd, "mod"), mod.file_name)
            old_exists = bool(mod.file_name and os.path.isfile(old_path))
            if mod.status in ("found", "warning") and mod.target_download_url:
                ok = PackBuilder._download_mod(
                    mod.target_download_url, PackBuilder._target_dir(odd, "mod"),
                    mod.target_file_name or mod.file_name, suffix=".disabled",
                    expected_size=mod.target_file_size, expected_hashes=mod.target_hashes,
                    cancel_event=cancel_event)
                if ok and mod.file_name:
                    new_name = mod.target_file_name or mod.file_name
                    if not new_name.endswith(".disabled"): new_name += ".disabled"
                if not ok:
                    if old_exists:
                        result.warnings.append(f"[禁用] {mod.name}：目标下载失败，已保留旧禁用版本。")
                    else:
                        result.missing_files.append(f"[禁用] {mod.name}")
            elif old_exists:
                result.warnings.append(f"[禁用] {mod.name}：未找到目标版本，已保留旧禁用版本。")
            else:
                result.missing_files.append(f"[禁用] {mod.name}")

    @staticmethod
    def _copy_overrides(
        src: str,
        dest: str,
        cancel_event: threading.Event | None = None,
    ):
        _check_cancelled(cancel_event)
        # overrides are user-owned passthrough data and must never be filtered by manifest changes.
        for root, dirs, files in os.walk(src):
            _check_cancelled(cancel_event)
            for directory in list(dirs):
                if os.path.islink(os.path.join(root, directory)):
                    raise ValueError(f"overrides 包含不允许的符号链接目录: {directory}")
            rel = os.path.relpath(root, src); dd = os.path.join(dest, rel)
            os.makedirs(dd, exist_ok=True)
            for f in files:
                _check_cancelled(cancel_event)
                source_file = os.path.join(root, f)
                if os.path.islink(source_file):
                    raise ValueError(f"overrides 包含不允许的符号链接文件: {f}")
                destination_file = os.path.join(dd, f)
                with open(source_file, "rb") as source, open(destination_file, "wb") as target:
                    while True:
                        _check_cancelled(cancel_event)
                        chunk = source.read(ZIP_COPY_CHUNK_BYTES)
                        if not chunk:
                            break
                        target.write(chunk)
                shutil.copystat(source_file, destination_file)

    @staticmethod
    def _download_mod(url: str, dd: str, fn: str, suffix: str = "",
                      expected_size: int = 0, expected_hashes: dict | None = None,
                      cancel_event: threading.Event | None = None) -> bool:
        if not url: return False
        temp_path = ""; resp = None; final_path = ""

        def resolve_destination(file_name: str) -> tuple[str, str]:
            if suffix and not file_name.endswith(suffix):
                file_name += suffix
            if (not file_name or file_name in (".", "..") or Path(file_name).name != file_name
                    or "/" in file_name or "\\" in file_name or ":" in file_name):
                raise ValueError(f"不安全的下载文件名: {file_name!r}")
            os.makedirs(dd, exist_ok=True)
            return file_name, os.path.join(dd, file_name)

        try:
            _check_cancelled(cancel_event)
            if fn:
                fn, final_path = resolve_destination(fn)
                if os.path.exists(final_path):
                    Logger.log("warn", f"保留 overrides 同名文件，跳过下载: {fn}")
                    return False
            resp = requests.get(url, timeout=REQUEST_TIMEOUT, stream=True)
            _check_cancelled(cancel_event)
            resp.raise_for_status()
            expected_size = int(expected_size or 0)
            if expected_size < 0:
                raise ValueError(f"无效的预期文件大小: {expected_size}")
            if expected_size > MAX_DOWNLOAD_BYTES:
                raise ValueError(
                    f"文件超过下载安全上限（{expected_size} > {MAX_DOWNLOAD_BYTES}）"
                )
            raw_content_length = str(
                getattr(resp, "headers", {}).get("Content-Length", "")
            ).strip()
            content_length = 0
            if raw_content_length:
                try:
                    content_length = int(raw_content_length)
                except ValueError:
                    Logger.log("warn", f"忽略无效 Content-Length: {raw_content_length!r}")
                else:
                    if content_length < 0:
                        raise ValueError(f"无效 Content-Length: {content_length}")
                    if content_length > MAX_DOWNLOAD_BYTES:
                        raise ValueError(
                            f"响应超过下载安全上限（{content_length} > {MAX_DOWNLOAD_BYTES}）"
                        )
                    if expected_size and content_length > expected_size:
                        raise ValueError(
                            f"响应大小超过预期（{content_length} > {expected_size}）"
                        )
            if not fn:
                cd = resp.headers.get("Content-Disposition", "")
                if "filename=" in cd: fn = cd.split("filename=")[-1].strip('"')
                else: fn = os.path.basename(urlparse(url).path)
                fn, final_path = resolve_destination(fn)
            if os.path.exists(final_path):
                Logger.log("warn", f"保留 overrides 同名文件，跳过下载: {fn}")
                return False
            hashers = {}
            for name in (expected_hashes or {}):
                normalized = str(name).lower().replace("-", "")
                if normalized in hashlib.algorithms_available:
                    hashers[name] = hashlib.new(normalized)
            fd, temp_path = tempfile.mkstemp(prefix=".download-", suffix=".part", dir=dd)
            size = 0
            with os.fdopen(fd, "wb") as f:
                for chunk in resp.iter_content(8192):
                    _check_cancelled(cancel_event)
                    if not chunk: continue
                    next_size = size + len(chunk)
                    if next_size > MAX_DOWNLOAD_BYTES:
                        raise ValueError(
                            f"下载内容超过安全上限（{next_size} > {MAX_DOWNLOAD_BYTES}）"
                        )
                    if expected_size and next_size > expected_size:
                        raise ValueError(
                            f"下载内容超过预期大小（{next_size} > {expected_size}）"
                        )
                    f.write(chunk); size = next_size
                    for hasher in hashers.values(): hasher.update(chunk)
            if expected_size and size != int(expected_size):
                raise ValueError(f"文件大小校验失败（期望 {expected_size}，实际 {size}）")
            for name, hasher in hashers.items():
                expected = str((expected_hashes or {}).get(name, "")).lower()
                if expected and hasher.hexdigest().lower() != expected:
                    raise ValueError(f"{name} 校验失败")
            _check_cancelled(cancel_event)
            try:
                reservation = os.open(final_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o666)
            except FileExistsError:
                Logger.log("warn", f"保留 overrides 同名文件，跳过下载: {fn}")
                return False
            else:
                os.close(reservation)
            try:
                os.replace(temp_path, final_path); temp_path = ""
            except Exception:
                try:
                    os.remove(final_path)
                except OSError:
                    pass
                raise
            Logger.log("info", f"下载完成: {fn}")
            return True
        except OperationCancelled:
            raise
        except Exception as e:
            Logger.log("error", f"下载失败 {url}: {e}")
            return False
        finally:
            if temp_path and os.path.exists(temp_path): os.remove(temp_path)
            if resp is not None:
                try: resp.close()
                except Exception: pass


# ============================================================
# 主 UI 应用
# ============================================================

class App:
    CONFIG_PATH = os.environ.get(
        "MC_PACK_MIGRATOR_CONFIG_PATH",
        os.path.expanduser("~/.mc_pack_migrator_config.json"),
    )
    STATUS_PRIORITY = {
        "found": 0,
        "preserved": 0,
        "passthrough": 0,
        "warning": 1,
        "not_found": 2,
        "pending": 3,
        "excluded": 4,
    }
    CATEGORY_PRIORITY = {"mod": 0, "resourcepack": 1, "shaderpack": 2}

    def __init__(self):
        self.root = tk.Tk(); self.root.title(APP_NAME)
        self.root.geometry("1280x800"); self.root.minsize(1100, 680)
        self.input_path = tk.StringVar()
        self.target_mc = tk.StringVar(value="1.21.1")
        self.target_loader_type = tk.StringVar(value="fabric")
        self.target_loader_version = tk.StringVar(value="")
        self.output_dir = tk.StringVar()
        self.output_filename = tk.StringVar(value="")
        self.download_mods = tk.BooleanVar(value=False)
        self.ui_language = tk.StringVar(value="zh_CN")
        self.ui_theme = tk.StringVar(value="light")
        self.ui_accent_color = tk.StringVar(value=DEFAULT_ACCENT_COLOR)
        self.ui_font_family = tk.StringVar(value=DEFAULT_FONT_FAMILY)
        self.user_agreement_accepted = False
        self._last_auto_output_filename = ""
        self.parsed_input_path = ""
        self.pack_info: ModpackInfo | None = None
        self.temp_overrides_dir = ""; self.working = False; self.analysis_ready = False
        self.compatibility_report = None; self.analysis_target_snapshot = None; self.logo_image = None
        self._build_after_resolution = False
        self._build_entry_active = False
        self._build_resume_pending = False
        self._resolution_skips: set[tuple] = set()
        self._notified_dependency_warnings: set[tuple[str, str, str]] = set()
        self.closing = False; self.loader_request_id = 0
        self.cancel_event = threading.Event()
        self._worker_lock = threading.Lock(); self._active_workers = 0
        self.cf_api = CurseForgeAPI(CF_API_KEY, self.cancel_event)
        self.mr_api = ModrinthAPI(self.cancel_event)
        self.sort_column = ""; self.sort_reverse = False
        Logger.set_callback(self._on_log)
        self._load_config()
        self._font_options = self._available_font_families()
        if self.ui_font_family.get() not in self._font_options:
            self.ui_font_family.set(DEFAULT_FONT_FAMILY)
        self.palette = build_palette(self.ui_theme.get(), self.ui_accent_color.get())
        self.root.configure(background=self.palette["app_bg"])
        self._configure_style()
        if not self.user_agreement_accepted:
            self.root.withdraw()
        self._last_target_snapshot = self._target_snapshot(); self._build_ui()
        self._target_traces = [
            variable.trace_add("write", self._on_target_value_write)
            for variable in (self.target_mc, self.target_loader_type, self.target_loader_version)
        ]
        self._input_path_trace = self.input_path.trace_add("write", self._on_input_path_write)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

    def _configure_style(self):
        self.style = getattr(self, "style", ttk.Style(self.root))
        self.style.theme_use("clam")
        p = self.palette
        family = self.ui_font_family.get().strip() or DEFAULT_FONT_FAMILY
        self.style.configure(".", font=(family, 10), background=p["app_bg"], foreground=p["text"])
        self.style.configure("App.TFrame", background=p["app_bg"])
        self.style.configure("Surface.TFrame", background=p["surface"])
        self.style.configure("Sidebar.TFrame", background=p["sidebar"])
        self.style.configure("SidebarBrand.TLabel", background=p["sidebar"], foreground=p["sidebar_text"], font=(family, 14, "bold"))
        self.style.configure("SidebarSub.TLabel", background=p["sidebar"], foreground=p["sidebar_sub"], font=(family, 9))
        self.style.configure("Nav.TButton", background=p["sidebar"], foreground=p["sidebar_text"], padding=(18, 12), borderwidth=0, anchor=tk.W, font=(family, 10))
        self.style.map("Nav.TButton", background=[("active", p["sidebar_hover"])], foreground=[("active", p["sidebar_text"])])
        self.style.configure("NavActive.TButton", background=p["accent_soft"], foreground=p["text"], padding=(18, 12), borderwidth=0, anchor=tk.W, font=(family, 10, "bold"))
        self.style.map("NavActive.TButton", background=[("active", p["accent_soft"])], foreground=[("active", p["text"])])
        for style_name, size, weight in (("PageTitle.TLabel", 18, "bold"), ("HomeTitle.TLabel", 26, "bold"), ("HomeAuthor.TLabel", 10, "bold")):
            self.style.configure(style_name, background=p["app_bg"], foreground=p["text"], font=(family, size, weight))
        self.style.configure("PageSub.TLabel", background=p["app_bg"], foreground=p["muted"], font=(family, 9))
        self.style.configure("HomeModule.TButton", background=p["surface"], foreground=p["text"], padding=(28, 28), borderwidth=1, bordercolor=p["border"], relief="solid", font=(family, 14, "bold"))
        self.style.map("HomeModule.TButton", background=[("active", p["accent_soft"])], foreground=[("active", p["text"])])
        self.style.configure("HomeLink.TButton", background=p["app_bg"], foreground=p["link"], padding=(10, 6), borderwidth=0)
        self.style.map("HomeLink.TButton", background=[("active", p["accent_soft"])], foreground=[("active", p["link"]), ("disabled", p["muted"])])
        self.style.configure("Placeholder.TLabel", background=p["app_bg"], foreground=p["muted"], font=(family, 11))
        self.style.configure("Header.TFrame", background=p["sidebar"])
        self.style.configure("HeaderTitle.TLabel", background=p["sidebar"], foreground=p["sidebar_text"], font=(family, 18, "bold"))
        self.style.configure("HeaderSub.TLabel", background=p["sidebar"], foreground=p["sidebar_sub"], font=(family, 9))
        self.style.configure("Section.TLabelframe", background=p["surface"], bordercolor=p["border"], relief="solid")
        self.style.configure("Section.TLabelframe.Label", background=p["surface"], foreground=p["text"], font=(family, 10, "bold"))
        self.style.configure("Surface.TLabel", background=p["surface"], foreground=p["text"])
        self.style.configure("Muted.TLabel", background=p["surface"], foreground=p["muted"])
        self.style.configure("SettingTitle.TLabel", background=p["surface"], foreground=p["text"], font=(family, 11, "bold"))
        self.style.configure("SettingDesc.TLabel", background=p["surface"], foreground=p["muted"], font=(family, 9))
        self.style.configure("Preview.TLabel", background=p["accent_soft"], foreground=p["text"], padding=(16, 12), font=(family, 13))
        self.style.configure("Primary.TButton", background=p["accent"], foreground=p["accent_text"], padding=(14, 8), borderwidth=0)
        self.style.map(
            "Primary.TButton",
            background=[("active", p["accent_hover"]), ("disabled", p["disabled"])],
            foreground=[("active", p["accent_hover_text"]), ("disabled", p["disabled_text"])],
        )
        self.style.configure("Secondary.TButton", background=p["accent_soft"], foreground=p["text"], padding=(12, 7), borderwidth=0)
        self.style.map("Secondary.TButton", background=[("active", p["accent_soft"])])
        self.style.configure("Danger.TButton", background=p["danger_bg"], foreground=p["danger_fg"], padding=(10, 7), borderwidth=0)
        self.style.configure("TEntry", fieldbackground=p["input"], foreground=p["text"], insertcolor=p["text"], bordercolor=p["border"])
        self.style.configure("TCombobox", fieldbackground=p["input"], background=p["surface"], foreground=p["text"], arrowcolor=p["text"])
        self.style.map(
            "TCombobox",
            fieldbackground=[("readonly", p["input"])],
            background=[("readonly", p["surface_alt"]), ("active", p["accent_soft"]), ("pressed", p["accent_soft"])],
            foreground=[("readonly", p["text"]), ("active", p["text"]), ("pressed", p["text"])],
            selectbackground=[("readonly", p["input"])],
            selectforeground=[("readonly", p["text"])],
        )
        self.style.configure("TCheckbutton", background=p["surface"], foreground=p["text"])
        self.style.configure("TRadiobutton", background=p["surface"], foreground=p["text"])
        self.style.configure("Treeview", background=p["surface"], fieldbackground=p["surface"], foreground=p["text"], rowheight=29, bordercolor=p["border"])
        self.style.configure("Treeview.Heading", background=p["heading"], foreground=p["text"], font=(family, 9, "bold"), relief="flat")
        self.style.map("Treeview", background=[("selected", p["accent_soft"])], foreground=[("selected", p["text"])])
        self.style.configure("TNotebook", background=p["app_bg"], borderwidth=0)
        self.style.configure("TNotebook.Tab", padding=(16, 8), background=p["heading"], foreground=p["muted"])
        self.style.map("TNotebook.Tab", background=[("selected", p["surface"])], foreground=[("selected", p["link"])])
        self.style.configure(
            "TScrollbar",
            background=p["surface_alt"],
            troughcolor=p["app_bg"],
            bordercolor=p["border"],
            arrowcolor=p["text"],
        )
        self.style.map("TScrollbar", background=[("active", p["accent_soft"]), ("pressed", p["accent_soft"])])
        self.style.configure("Fresh.Horizontal.TProgressbar", troughcolor=p["heading"], background=p["accent"], bordercolor=p["heading"])
        for option, value in (
            ("background", p["surface"]),
            ("foreground", p["text"]),
            ("selectBackground", p["accent_soft"]),
            ("selectForeground", p["text"]),
        ):
            self.root.option_add(f"*TCombobox*Listbox.{option}", value)
        for font_name in ("TkDefaultFont", "TkTextFont", "TkMenuFont", "TkHeadingFont"):
            try:
                tkfont.nametofont(font_name).configure(family=family)
            except tk.TclError:
                pass

    def _current_language(self) -> str:
        variable = getattr(self, "ui_language", None)
        try:
            language = variable.get() if variable is not None else "zh_CN"
        except (AttributeError, tk.TclError):
            language = "zh_CN"
        return language if language in SUPPORTED_LANGUAGES else "zh_CN"

    def _t(self, key: str, **values) -> str:
        return translate_text(self._current_language(), key, **values)

    def _available_font_families(self) -> list[str]:
        try:
            all_families = sorted(
                {name.strip() for name in tkfont.families(self.root) if name.strip() and not name.startswith("@")},
                key=str.casefold,
            )
        except tk.TclError:
            all_families = []
        unsuitable_tokens = (
            "wingdings", "webdings", "symbol", "marlett", "emoji", "icon",
            "dingbat", "barcode", "holomdl2", "wide latin", "goudy stout",
            "algerian", "blackadder", "bauhaus", "bradley hand", "brush script",
            "chiller", "curlz", "edwardian", "freestyle script", "french script",
            "harlow", "harrington", "jokerman", "juice itc", "kunstler",
            "magneto", "matura", "mistral", "old english", "papyrus",
            "parchment", "playbill", "ravie", "snap itc", "viner hand",
            "vivaldi", "vladimir",
        )
        try:
            baseline = tkfont.Font(root=self.root, family=DEFAULT_FONT_FAMILY, size=10).measure(
                "MC Modpack Tool - Version Migration"
            )
        except tk.TclError:
            baseline = 0
        families = []
        for name in all_families:
            if any(token in name.casefold() for token in unsuitable_tokens):
                continue
            if baseline:
                try:
                    width = tkfont.Font(root=self.root, family=name, size=10).measure(
                        "MC Modpack Tool - Version Migration"
                    )
                except tk.TclError:
                    continue
                if width < baseline * 0.62 or width > baseline * 1.45:
                    continue
            families.append(name)
        if DEFAULT_FONT_FAMILY not in families:
            families.insert(0, DEFAULT_FONT_FAMILY)
        return families

    def _sidebar_width(self) -> int:
        family = self.ui_font_family.get().strip() or DEFAULT_FONT_FAMILY
        candidates = (
            (self._t("app.name"), 14),
            (self._t("app.subtitle"), 9),
            (self._t("nav.home"), 10),
            (self._t("nav.migration"), 10),
            (self._t("nav.server"), 10),
            (self._t("nav.settings"), 10),
        )
        try:
            required = max(
                tkfont.Font(root=self.root, family=family, size=size).measure(label)
                for label, size in candidates
            ) + 38
        except tk.TclError:
            required = 220
        return max(220, min(280, required))

    def _open_web_entry(self, url: str, pending: bool = False) -> bool:
        if not url:
            if pending:
                messagebox.showinfo(self._t("github.pending_title"), self._t("github.pending_message"))
            return False
        webbrowser.open_new_tab(url)
        return True

    def _on_language_selected(self, _event=None) -> None:
        selected_label = self.language_choice.get()
        language = next((code for code, label in LANGUAGE_LABELS.items() if label == selected_label), "zh_CN")
        self.ui_language.set(language)
        self._apply_language()
        self._save_ui_preferences()

    def _save_ui_preferences(self, parent=None) -> bool:
        if self._save_config():
            return True
        messagebox.showwarning(
            self._t("settings.save_error_title"),
            self._t("settings.save_error_message"),
            parent=parent or self.root,
        )
        return False

    def _on_theme_selected(self) -> None:
        if self.ui_theme.get() not in SUPPORTED_THEMES:
            self.ui_theme.set("light")
        self._apply_appearance()

    def _use_accent_color(self, color: str) -> None:
        self.ui_accent_color.set(_normalize_hex_color(color))
        self._apply_appearance()

    def _pick_accent_color(self) -> None:
        _rgb, color = colorchooser.askcolor(
            color=self.ui_accent_color.get(), title=self._t("settings.color_dialog"), parent=self.root
        )
        if color:
            self._use_accent_color(color)

    def _on_font_selected(self, _event=None) -> None:
        selected = self.font_choice.get().strip()
        self.ui_font_family.set(selected or DEFAULT_FONT_FAMILY)
        self._apply_appearance()

    def _reset_font(self) -> None:
        self.ui_font_family.set(DEFAULT_FONT_FAMILY)
        if hasattr(self, "font_choice"):
            self.font_choice.set(DEFAULT_FONT_FAMILY)
        self._apply_appearance()

    def _apply_appearance(self) -> None:
        self.palette = build_palette(self.ui_theme.get(), self.ui_accent_color.get())
        self.root.configure(background=self.palette["app_bg"])
        self._configure_style()
        self._apply_native_widget_theme()
        if hasattr(self, "sidebar"):
            self.sidebar.configure(width=self._sidebar_width())
        if hasattr(self, "accent_preview"):
            self.accent_preview.configure(background=self.palette["accent"])
        self._save_ui_preferences()

    def _apply_native_widget_theme(self) -> None:
        p = self.palette
        family = self.ui_font_family.get().strip() or DEFAULT_FONT_FAMILY
        for widget in (getattr(self, "info_text", None), getattr(self, "compat_detail", None)):
            if widget is not None:
                widget.configure(bg=p["surface"], fg=p["text"], insertbackground=p["text"], font=(family, 10))
        if hasattr(self, "log_text"):
            self.log_text.configure(bg=p["log_bg"], fg=p["log_fg"], insertbackground=p["log_fg"], font=("Consolas", 9))
        for widget in (getattr(self, "info_text", None), getattr(self, "log_text", None)):
            scrollbar = getattr(widget, "vbar", None)
            if scrollbar is not None:
                scrollbar.configure(
                    background=p["surface_alt"],
                    activebackground=p["accent_soft"],
                    troughcolor=p["app_bg"],
                    highlightbackground=p["border"],
                    borderwidth=0,
                )
        if hasattr(self, "tree_menu"):
            self.tree_menu.configure(bg=p["surface"], fg=p["text"], activebackground=p["accent_soft"], activeforeground=p["text"])
        if hasattr(self, "compat_tree"):
            for tag, color in (("blocking", p["danger_bg"]), ("warning", p["warning_bg"]), ("info", p["info_bg"]), ("ok", p["ok_bg"])):
                self.compat_tree.tag_configure(tag, background=color, foreground=p["text"])
        if hasattr(self, "mod_tree"):
            colors = {
                "found": p["ok_bg"], "not_found": p["danger_bg"], "warning": p["warning_bg"],
                "disabled": p["surface_alt"], "excluded": p["heading"], "pending": p["surface"],
            }
            for tag, color in colors.items():
                self.mod_tree.tag_configure(tag, background=color, foreground=p["text"])

    def _apply_language(self) -> None:
        self.root.title(self._t("app.name"))
        if hasattr(self, "sidebar"):
            self.sidebar.configure(width=self._sidebar_width())
        nav_keys = {"home": "nav.home", "migration": "nav.migration", "server": "nav.server", "settings": "nav.settings"}
        for page_id, button in getattr(self, "nav_buttons", {}).items():
            button.configure(text=self._t(nav_keys[page_id]))
        widget_keys = {
            "brand_title_label": "app.name", "home_title_label": "app.name",
            "brand_subtitle_label": "app.subtitle", "home_migration_btn": "home.migration",
            "home_server_btn": "home.server", "home_bilibili_btn": "home.bilibili",
            "home_agreement_btn": "home.agreement", "home_author_label": "author",
            "server_title_label": "home.server", "server_placeholder_label": "placeholder.server",
            "migration_title_label": "migration.title", "migration_subtitle_label": "migration.subtitle",
            "build_btn": "migration.build", "pack_file_label": "migration.pack_file",
            "browse_input_btn": "migration.choose_file", "parse_btn": "migration.read_pack",
            "loader_label": "migration.loader", "loader_version_label": "migration.loader_version",
            "fetch_loader_btn": "migration.latest_loader", "output_dir_label": "migration.output_dir",
            "browse_output_btn": "migration.browse", "output_name_label": "migration.output_name",
            "download_check": "migration.embed_downloads", "find_btn": "migration.check",
            "compat_refresh_btn": "migration.refresh", "settings_title_label": "settings.title",
            "settings_subtitle_label": "settings.subtitle", "language_title_label": "settings.language",
            "language_desc_label": "settings.language_desc", "appearance_title_label": "settings.appearance",
            "appearance_desc_label": "settings.appearance_desc", "light_radio": "settings.light",
            "dark_radio": "settings.dark", "accent_title_label": "settings.accent",
            "accent_desc_label": "settings.accent_desc", "custom_color_btn": "settings.custom_color",
            "reset_color_btn": "settings.reset_color", "font_title_label": "settings.font",
            "font_desc_label": "settings.font_desc", "font_preview_label": "settings.font_preview",
            "default_font_btn": "settings.default_font",
        }
        for attribute, key in widget_keys.items():
            widget = getattr(self, attribute, None)
            if widget is not None:
                widget.configure(text=self._t(key))
        if hasattr(self, "home_github_btn"):
            self.home_github_btn.configure(text=self._t("home.github" if GITHUB_URL else "home.github_pending"))
        if hasattr(self, "source_overview_frame"):
            self.source_overview_frame.configure(text=self._t("migration.source_overview"))
            self.target_frame.configure(text=self._t("migration.target"))
        if hasattr(self, "notebook"):
            self.notebook.tab(self.compat_tab, text=self._t("migration.report"))
            self.notebook.tab(self.files_tab, text=self._t("migration.files"))
            self.notebook.tab(self.log_tab, text=self._t("migration.log"))
            for column, key in (("severity", "tree.severity"), ("category", "tree.category"), ("item", "tree.item"), ("message", "tree.conclusion")):
                self.compat_tree.heading(column, text=self._t(key))
            for column, key in (("name", "tree.name"), ("source_col", "tree.source"), ("category", "tree.category"), ("status_text", "tree.status"), ("delete_action", "tree.output")):
                self.mod_tree.heading(column, text=self._t(key))
            self.tree_menu.entryconfigure(0, label=self._t("menu.exclude"))
            self.tree_menu.entryconfigure(2, label=self._t("menu.curseforge"))
            self.tree_menu.entryconfigure(3, label=self._t("menu.modrinth"))
        if hasattr(self, "language_choice"):
            self.language_choice.set(LANGUAGE_LABELS[self.ui_language.get()])
        if self.pack_info:
            self._update_info(); self._refresh_mod_tree()
        if self.compatibility_report is not None and self.analysis_ready:
            self._render_compatibility_report(self.compatibility_report)
        elif hasattr(self, "_compatibility_state") and hasattr(self, "compat_summary"):
            key, values, color = self._compatibility_state
            self.compat_summary.configure(text=self._t(key, **values), foreground=color)
        elif hasattr(self, "compat_summary") and not self.pack_info:
            self.compat_summary.configure(text=self._t("migration.report_hint"))
        if hasattr(self, "_status_state") and hasattr(self, "status_label"):
            key, values, color = self._status_state
            self.status_label.configure(text=self._t(key, **values), foreground=color)
        elif hasattr(self, "status_label") and not getattr(self, "working", False) and not self.pack_info:
            self.status_label.configure(text=self._t("migration.ready"))

    def _load_config(self):
        self.user_agreement_accepted = False
        try:
            if os.path.exists(self.CONFIG_PATH):
                with open(self.CONFIG_PATH, "r", encoding="utf-8") as f: cfg = json.load(f)
                if not isinstance(cfg, dict):
                    return
                self.target_mc.set(cfg.get("target_mc", "1.21.1"))
                self.target_loader_type.set(cfg.get("target_loader_type", "fabric"))
                self.target_loader_version.set(cfg.get("target_loader_version", ""))
                self.output_dir.set(cfg.get("output_dir", ""))
                preferences = cfg.get("ui_preferences", {})
                if not isinstance(preferences, dict):
                    preferences = {}
                language = preferences.get("language", "zh_CN")
                theme = preferences.get("theme", "light")
                accent = _normalize_hex_color(preferences.get("accent_color"))
                font_family = str(preferences.get("font_family", DEFAULT_FONT_FAMILY) or DEFAULT_FONT_FAMILY).strip()
                if hasattr(self, "ui_language"):
                    self.ui_language.set(language if language in SUPPORTED_LANGUAGES else "zh_CN")
                    self.ui_theme.set(theme if theme in SUPPORTED_THEMES else "light")
                    self.ui_accent_color.set(accent)
                    self.ui_font_family.set(font_family)
                self.user_agreement_accepted = (
                    cfg.get("accepted_agreement_version") == USER_AGREEMENT_VERSION
                )
        except Exception: pass
    def _save_config(self) -> bool:
        cfg = {"target_mc": self.target_mc.get(),
               "target_loader_type": self.target_loader_type.get(),
               "target_loader_version": self.target_loader_version.get(),
               "output_dir": self.output_dir.get()}
        temp_path = ""
        try:
            config_path = Path(self.CONFIG_PATH).expanduser()
            config_path.parent.mkdir(parents=True, exist_ok=True)
            existing_cfg = {}
            if config_path.exists():
                try:
                    with open(config_path, "r", encoding="utf-8") as f:
                        loaded_cfg = json.load(f)
                    if isinstance(loaded_cfg, dict):
                        existing_cfg = loaded_cfg
                except (OSError, ValueError, TypeError):
                    pass
            existing_cfg.update(cfg)
            cfg = existing_cfg
            if hasattr(self, "ui_language"):
                cfg["ui_preferences"] = {
                    "schema_version": 1,
                    "language": self.ui_language.get() if self.ui_language.get() in SUPPORTED_LANGUAGES else "zh_CN",
                    "theme": self.ui_theme.get() if self.ui_theme.get() in SUPPORTED_THEMES else "light",
                    "accent_color": _normalize_hex_color(self.ui_accent_color.get()),
                    "font_family": self.ui_font_family.get().strip() or DEFAULT_FONT_FAMILY,
                }
            if (
                getattr(self, "user_agreement_accepted", False)
                or existing_cfg.get("accepted_agreement_version") == USER_AGREEMENT_VERSION
            ):
                cfg["accepted_agreement_version"] = USER_AGREEMENT_VERSION
            temp_path = str(config_path.with_name(f".{config_path.name}.{os.getpid()}.tmp"))
            with open(temp_path, "w", encoding="utf-8") as f:
                json.dump(cfg, f, ensure_ascii=False, indent=2)
            os.replace(temp_path, config_path)
            temp_path = ""
            return True
        except Exception as e:
            Logger.log("warn", f"配置保存失败: {e}")
            return False
        finally:
            if temp_path:
                try: os.remove(temp_path)
                except OSError: pass

    def _show_user_agreement(self, require_acceptance: bool = True) -> bool:
        language = self._current_language()
        dialog = tk.Toplevel(self.root)
        dialog.title(self._t("agreement.title"))
        dialog.configure(background=self.palette["app_bg"])
        if not require_acceptance:
            dialog.transient(self.root)
        if getattr(self, "logo_image", None) is not None:
            try: dialog.iconphoto(True, self.logo_image)
            except tk.TclError: pass

        width = min(840, max(640, dialog.winfo_screenwidth() - 80))
        height = min(720, max(540, dialog.winfo_screenheight() - 80))
        x = max(0, (dialog.winfo_screenwidth() - width) // 2)
        y = max(0, (dialog.winfo_screenheight() - height) // 2)
        dialog.geometry(f"{width}x{height}+{x}+{y}")
        dialog.minsize(min(680, width), min(560, height))

        header = ttk.Frame(dialog, style="Header.TFrame", padding=(20, 15))
        header.pack(fill=tk.X)
        title_box = ttk.Frame(header, style="Header.TFrame")
        title_box.pack(side=tk.LEFT, fill=tk.X, expand=True)
        agreement_title_label = ttk.Label(
            title_box,
            text=self._t("agreement.title"),
            style="HeaderTitle.TLabel",
        )
        agreement_title_label.pack(anchor=tk.W)
        agreement_version_label = ttk.Label(
            title_box,
            text=self._t("agreement.version", version=USER_AGREEMENT_VERSION),
            style="HeaderSub.TLabel",
        )
        agreement_version_label.pack(anchor=tk.W, pady=(3, 0))
        language_box = ttk.Frame(header, style="Header.TFrame")
        language_box.pack(side=tk.RIGHT, padx=(16, 0))
        agreement_language_label = ttk.Label(
            language_box,
            text=self._t("agreement.language"),
            style="HeaderSub.TLabel",
        )
        agreement_language_label.pack(anchor=tk.W, pady=(0, 4))
        agreement_language_choice = ttk.Combobox(
            language_box,
            values=list(LANGUAGE_LABELS.values()),
            state="readonly",
            width=20,
        )
        agreement_language_choice.set(LANGUAGE_LABELS[language])
        agreement_language_choice.pack(anchor=tk.E)

        footer = ttk.Frame(dialog, style="Surface.TFrame", padding=(18, 12))
        footer.pack(fill=tk.X, side=tk.BOTTOM)
        content = ttk.Frame(dialog, style="App.TFrame", padding=(18, 14, 18, 10))
        content.pack(fill=tk.BOTH, expand=True)
        agreement_body = ttk.Frame(content, style="App.TFrame")
        agreement_body.pack(fill=tk.BOTH, expand=True)
        agreement_text = tk.Text(
            agreement_body,
            height=10,
            wrap=tk.CHAR,
            font=(self.ui_font_family.get(), 10),
            bg=self.palette["surface"],
            fg=self.palette["text"],
            relief=tk.FLAT,
            borderwidth=1,
            padx=18,
            pady=14,
            spacing1=2,
            spacing3=4,
        )
        agreement_scroll = ttk.Scrollbar(
            agreement_body,
            orient=tk.VERTICAL,
            command=agreement_text.yview,
        )
        agreement_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        agreement_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        agreement_text.configure(yscrollcommand=agreement_scroll.set)
        agreement_text.tag_configure(
            "agreement_title", font=(self.ui_font_family.get(), 13, "bold"), foreground=self.palette["text"]
        )
        agreement_text.tag_configure(
            "section", font=(self.ui_font_family.get(), 10, "bold"), foreground=self.palette["link"], spacing1=8
        )

        def render_agreement(selected_language: str) -> None:
            text = USER_AGREEMENT_TEXTS.get(selected_language, USER_AGREEMENT_TEXT)
            agreement_text.configure(state=tk.NORMAL)
            agreement_text.delete("1.0", tk.END)
            agreement_text.insert("1.0", text)
            agreement_text.tag_add("agreement_title", "1.0", "1.end")
            section_headings = set(USER_AGREEMENT_SECTION_HEADINGS.get(selected_language, ()))
            for line_number, line in enumerate(text.splitlines(), start=1):
                if line in section_headings:
                    agreement_text.tag_add(
                        "section",
                        f"{line_number}.0",
                        f"{line_number}.end",
                    )
            agreement_text.configure(state=tk.DISABLED)
            agreement_text.yview_moveto(0)

        render_agreement(language)

        license_button = ttk.Button(
            footer,
            text=self._t("agreement.license"),
            style="Secondary.TButton",
            command=lambda: webbrowser.open(CC_BY_NC_SA_URL),
        )
        license_button.pack(side=tk.LEFT)

        decision = {"accepted": False}

        def close_dialog(accepted: bool = False) -> None:
            decision["accepted"] = accepted
            if accepted and require_acceptance:
                self.user_agreement_accepted = True
                if not self._save_config():
                    messagebox.showwarning(
                        self._t("agreement.save_error_title"),
                        self._t("agreement.save_error_message"),
                        parent=dialog,
                    )
            try: dialog.grab_release()
            except tk.TclError: pass
            dialog.destroy()

        accept_button = decline_button = close_button = None
        if require_acceptance:
            accept_button = ttk.Button(
                footer,
                text=self._t("agreement.accept"),
                style="Primary.TButton",
                command=lambda: close_dialog(True),
            )
            accept_button.pack(side=tk.RIGHT)
            decline_button = ttk.Button(
                footer,
                text=self._t("agreement.decline"),
                style="Danger.TButton",
                command=lambda: close_dialog(False),
            )
            decline_button.pack(side=tk.RIGHT, padx=(0, 10))
        else:
            close_button = ttk.Button(
                footer, text=self._t("common.close"), style="Primary.TButton", command=lambda: close_dialog(False)
            )
            close_button.pack(side=tk.RIGHT)

        def switch_agreement_language(_event=None) -> None:
            selected_label = agreement_language_choice.get()
            selected_language = next(
                (code for code, label in LANGUAGE_LABELS.items() if label == selected_label),
                "zh_CN",
            )
            self.ui_language.set(selected_language)
            self._apply_language()
            dialog.title(self._t("agreement.title"))
            agreement_title_label.configure(text=self._t("agreement.title"))
            agreement_version_label.configure(
                text=self._t("agreement.version", version=USER_AGREEMENT_VERSION)
            )
            agreement_language_label.configure(text=self._t("agreement.language"))
            license_button.configure(text=self._t("agreement.license"))
            if accept_button is not None:
                accept_button.configure(text=self._t("agreement.accept"))
            if decline_button is not None:
                decline_button.configure(text=self._t("agreement.decline"))
            if close_button is not None:
                close_button.configure(text=self._t("common.close"))
            render_agreement(selected_language)
            self._save_ui_preferences(parent=dialog)

        agreement_language_choice.bind("<<ComboboxSelected>>", switch_agreement_language)

        dialog.protocol("WM_DELETE_WINDOW", lambda: close_dialog(False))
        dialog.bind("<Escape>", lambda _event: close_dialog(False))
        dialog.update_idletasks()
        dialog.wait_visibility()
        dialog.grab_set()
        dialog.lift()
        dialog.focus_force()
        self.root.wait_window(dialog)
        return decision["accepted"]
    def _cleanup_temp_overrides(self):
        if self.temp_overrides_dir:
            shutil.rmtree(self.temp_overrides_dir, ignore_errors=True)
            self.temp_overrides_dir = ""

    def _target_snapshot(self) -> tuple[str, str, str]:
        return (
            self.target_mc.get().strip(),
            self.target_loader_type.get().strip(),
            self.target_loader_version.get().strip(),
        )

    def _target_settings_complete(self) -> bool:
        mc_version, loader_type, loader_version = self._target_snapshot()
        return bool(
            re.fullmatch(r"\d+\.\d+(?:\.\d+)?", mc_version)
            and loader_type in {"forge", "fabric", "neoforge", "quilt"}
            and loader_version
        )

    def _analysis_matches_target(self) -> bool:
        return bool(
            self.analysis_ready
            and self.compatibility_report is not None
            and self.analysis_target_snapshot == self._target_snapshot()
        )

    def _parsed_input_matches_current(self) -> bool:
        parsed_path = getattr(self, "parsed_input_path", "")
        if not parsed_path:
            return True
        input_variable = getattr(self, "input_path", None)
        current_path = input_variable.get().strip() if input_variable is not None else ""
        return paths_refer_to_same_location(parsed_path, current_path)

    def _refresh_auto_output_filename(
        self,
        *,
        force: bool = False,
        source_mc: Optional[str] = None,
    ) -> str:
        target_mc = self.target_mc.get().strip()
        if not re.fullmatch(r"\d+\.\d+(?:\.\d+)?", target_mc):
            return ""
        input_path = self.input_path.get().strip()
        source_name = Path(input_path).stem.strip() if input_path else ""
        if not source_name and self.pack_info:
            source_name = str(self.pack_info.raw_data.get("name", "")).strip()
        if not source_name:
            return ""
        if source_mc is None:
            source_mc = (
                self.pack_info.mc_version
                if self.pack_info and self._parsed_input_matches_current()
                else ""
            )
        generated = generate_output_pack_name(
            source_name,
            source_mc,
            target_mc,
            self._current_language(),
        )
        current = self.output_filename.get().strip()
        last_auto = getattr(self, "_last_auto_output_filename", "")
        if force or not current or current == last_auto:
            self.output_filename.set(generated)
            self._last_auto_output_filename = generated
        return generated

    def _on_input_path_write(self, *_args) -> None:
        if not hasattr(self, "build_btn"):
            return
        if self.pack_info and not self._parsed_input_matches_current():
            self._cleanup_temp_overrides()
            self.pack_info = None
            self.parsed_input_path = ""
            self.analysis_ready = False
            self.compatibility_report = None
            self.analysis_target_snapshot = None
            self._build_after_resolution = False
            self._build_resume_pending = False
            self._resolution_skips = set()
            for item in self.mod_tree.get_children():
                self.mod_tree.delete(item)
            self._set_info("")
            self._clear_compatibility_display_key("runtime.input_changed", "#A96000")
            self._set_status_key("runtime.input_changed", "#A96000")
            self._set_working(False)
        self._refresh_auto_output_filename(source_mc="")

    def _schedule(self, delay: int, callback) -> None:
        if getattr(self, "closing", False):
            return

        def run_if_open():
            if not getattr(self, "closing", False):
                callback()

        try:
            self.root.after(delay, run_if_open)
        except (tk.TclError, RuntimeError):
            pass

    def _start_worker(self, target) -> None:
        if getattr(self, "closing", False):
            return
        if not hasattr(self, "_worker_lock"):
            self._worker_lock = threading.Lock(); self._active_workers = 0
        with self._worker_lock:
            self._active_workers += 1

        def runner():
            try:
                target()
            except OperationCancelled:
                Logger.log("info", "后台操作已取消")
            finally:
                with self._worker_lock:
                    self._active_workers -= 1

        try:
            worker = threading.Thread(target=runner, daemon=False)
            worker.start()
        except Exception:
            with self._worker_lock:
                self._active_workers -= 1
            raise

    def _on_close(self, save_config: bool = True):
        if self.closing:
            return
        self.closing = True; self.cancel_event.set(); self.loader_request_id += 1
        if save_config:
            self._save_config()
        try:
            self._set_status_key("runtime.stopping", "#A96000")
            self.root.title(f"{self._t('app.name')} - {self._t('runtime.stopping')}")
        except (tk.TclError, AttributeError):
            pass
        self._finish_close_when_idle()

    def _finish_close_when_idle(self):
        with self._worker_lock:
            active_workers = self._active_workers
        if active_workers:
            try:
                self.root.after(50, self._finish_close_when_idle)
            except (tk.TclError, RuntimeError):
                pass
            return
        self._cleanup_temp_overrides()
        Logger.set_callback(None)
        try:
            self.root.destroy()
        except tk.TclError:
            pass

    def _on_log(self, level: str, msg: str):
        def _append():
            self.log_text.configure(state=tk.NORMAL)
            self.log_text.insert(tk.END, f"[{level.upper()}] {msg}\n")
            self.log_text.see(tk.END); self.log_text.configure(state=tk.DISABLED)
        self._schedule(0, _append)

    def _build_ui(self):
        self._load_logo_assets()

        shell = ttk.Frame(self.root, style="App.TFrame")
        shell.pack(fill=tk.BOTH, expand=True)
        self.sidebar = ttk.Frame(shell, style="Sidebar.TFrame", width=self._sidebar_width())
        self.sidebar.pack(side=tk.LEFT, fill=tk.Y)
        self.sidebar.pack_propagate(False)

        brand = ttk.Frame(self.sidebar, style="Sidebar.TFrame", padding=(18, 18, 14, 16))
        brand.pack(fill=tk.X)
        if self.logo_image is not None:
            ttk.Label(brand, image=self.logo_image, style="SidebarSub.TLabel").pack(anchor=tk.W)
        self.brand_title_label = ttk.Label(brand, text=self._t("app.name"), style="SidebarBrand.TLabel")
        self.brand_title_label.pack(anchor=tk.W, pady=(10, 0))
        self.brand_subtitle_label = ttk.Label(brand, text=self._t("app.subtitle"), style="SidebarSub.TLabel")
        self.brand_subtitle_label.pack(anchor=tk.W, pady=(3, 0))

        self.nav_buttons = {}
        navigation = ttk.Frame(self.sidebar, style="Sidebar.TFrame", padding=(10, 8))
        navigation.pack(fill=tk.X)
        nav_keys = {"home": "nav.home", "migration": "nav.migration", "server": "nav.server", "settings": "nav.settings"}
        for page_id, _label in NAVIGATION_ITEMS[:-1]:
            button = ttk.Button(
                navigation,
                text=self._t(nav_keys[page_id]),
                style="Nav.TButton",
                command=lambda selected=page_id: self._show_page(selected),
            )
            button.pack(fill=tk.X, pady=2)
            self.nav_buttons[page_id] = button

        settings_id, _settings_label = NAVIGATION_ITEMS[-1]
        settings_button = ttk.Button(
            self.sidebar,
            text=self._t(nav_keys[settings_id]),
            style="Nav.TButton",
            command=lambda: self._show_page(settings_id),
        )
        settings_button.pack(side=tk.BOTTOM, fill=tk.X, padx=10, pady=(8, 16))
        self.nav_buttons[settings_id] = settings_button

        self.page_container = ttk.Frame(shell, style="App.TFrame")
        self.page_container.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True)
        self.page_container.rowconfigure(0, weight=1)
        self.page_container.columnconfigure(0, weight=1)
        self.pages = {
            page_id: ttk.Frame(self.page_container, style="App.TFrame")
            for page_id, _label in NAVIGATION_ITEMS
        }
        for page in self.pages.values():
            page.grid(row=0, column=0, sticky=tk.NSEW)

        self._build_home_page(self.pages["home"])
        self._build_migration_page(self.pages["migration"])
        self._build_placeholder_page(self.pages["server"])
        self._build_settings_page(self.pages["settings"])
        self._apply_language()
        self._apply_native_widget_theme()
        self._show_page("home")

    def _load_logo_assets(self):
        logo_path = APP_ROOT / "资源" / "mc_pack_migrator_logo.png"
        if not logo_path.exists():
            logo_path = APP_ROOT / "mc_pack_migrator_logo.png"
        if logo_path.exists():
            try:
                self.logo_source_image = tk.PhotoImage(file=str(logo_path))
                self.logo_image = self.logo_source_image.subsample(4, 4)
                self.logo_home_image = self.logo_source_image.subsample(2, 2)
                self.root.iconphoto(True, self.logo_source_image)
            except Exception as e: Logger.log("warn", f"Logo 加载失败: {e}")

    def _build_home_page(self, page):
        page.columnconfigure(0, weight=1)
        page.rowconfigure(0, weight=1)
        center = ttk.Frame(page, style="App.TFrame", padding=(28, 24))
        center.grid(row=0, column=0)
        if getattr(self, "logo_home_image", None) is not None:
            ttk.Label(center, image=self.logo_home_image, style="PageSub.TLabel").pack(pady=(0, 14))
        self.home_title_label = ttk.Label(center, text=self._t("app.name"), style="HomeTitle.TLabel")
        self.home_title_label.pack()

        module_entries = ttk.Frame(center, style="App.TFrame")
        module_entries.pack(pady=(34, 0))
        self.home_migration_btn = ttk.Button(
            module_entries,
            text=self._t("home.migration"),
            style="HomeModule.TButton",
            command=lambda: self._show_page("migration"),
            width=22,
        )
        self.home_migration_btn.grid(row=0, column=0, padx=(0, 10), sticky=tk.NSEW)
        self.home_server_btn = ttk.Button(
            module_entries,
            text=self._t("home.server"),
            style="HomeModule.TButton",
            command=lambda: self._show_page("server"),
            width=22,
        )
        self.home_server_btn.grid(row=0, column=1, padx=(10, 0), sticky=tk.NSEW)

        home_footer = ttk.Frame(page, style="App.TFrame", padding=(20, 12, 24, 18))
        home_footer.grid(row=1, column=0, sticky=tk.EW)
        social_box = ttk.Frame(home_footer, style="App.TFrame")
        social_box.pack(side=tk.LEFT)
        self.home_bilibili_btn = ttk.Button(
            social_box, text=self._t("home.bilibili"), style="HomeLink.TButton",
            command=lambda: self._open_web_entry(BILIBILI_URL),
        )
        self.home_bilibili_btn.pack(side=tk.LEFT)
        self.home_github_btn = ttk.Button(
            social_box, text=self._t("home.github" if GITHUB_URL else "home.github_pending"),
            style="HomeLink.TButton", command=lambda: self._open_web_entry(GITHUB_URL, pending=True),
        )
        self.home_github_btn.pack(side=tk.LEFT, padx=(6, 0))
        self.home_author_label = ttk.Label(home_footer, text=self._t("author"), style="HomeAuthor.TLabel")
        self.home_author_label.pack(side=tk.RIGHT, padx=(14, 0))
        self.home_agreement_btn = ttk.Button(
            home_footer,
            text=self._t("home.agreement"),
            style="HomeLink.TButton",
            command=self._show_user_agreement_from_home,
        )
        self.home_agreement_btn.pack(side=tk.RIGHT)

    def _build_placeholder_page(self, page):
        header = ttk.Frame(page, style="App.TFrame", padding=(22, 20, 22, 14))
        header.pack(fill=tk.X)
        self.server_title_label = ttk.Label(header, text=self._t("home.server"), style="PageTitle.TLabel")
        self.server_title_label.pack(anchor=tk.W)
        body = ttk.Frame(page, style="App.TFrame")
        body.pack(fill=tk.BOTH, expand=True)
        self.server_placeholder_label = ttk.Label(body, text=self._t("placeholder.server"), style="Placeholder.TLabel")
        self.server_placeholder_label.place(
            relx=0.5, rely=0.46, anchor=tk.CENTER
        )

    def _build_settings_page(self, page):
        header = ttk.Frame(page, style="App.TFrame", padding=(26, 22, 26, 16))
        header.pack(fill=tk.X)
        self.settings_title_label = ttk.Label(header, text=self._t("settings.title"), style="PageTitle.TLabel")
        self.settings_title_label.pack(anchor=tk.W)

        body = ttk.Frame(page, style="App.TFrame", padding=(26, 8, 26, 24))
        body.pack(fill=tk.BOTH, expand=True)
        panel = ttk.Frame(body, style="Surface.TFrame", padding=(22, 20))
        panel.pack(fill=tk.X)
        panel.columnconfigure(1, weight=1)

        self.language_title_label = ttk.Label(panel, text=self._t("settings.language"), style="SettingTitle.TLabel")
        self.language_title_label.grid(row=0, column=0, sticky=tk.NW, padx=(0, 30))
        language_box = ttk.Frame(panel, style="Surface.TFrame")
        language_box.grid(row=0, column=1, sticky=tk.EW, pady=(0, 18))
        self.language_choice = ttk.Combobox(language_box, values=list(LANGUAGE_LABELS.values()), state="readonly", width=28)
        self.language_choice.set(LANGUAGE_LABELS[self.ui_language.get()])
        self.language_choice.pack(anchor=tk.W)
        self.language_choice.bind("<<ComboboxSelected>>", self._on_language_selected)

        self.appearance_title_label = ttk.Label(panel, text=self._t("settings.appearance"), style="SettingTitle.TLabel")
        self.appearance_title_label.grid(row=1, column=0, sticky=tk.NW, padx=(0, 30), pady=(14, 0))
        appearance_box = ttk.Frame(panel, style="Surface.TFrame")
        appearance_box.grid(row=1, column=1, sticky=tk.EW, pady=(14, 18))
        theme_row = ttk.Frame(appearance_box, style="Surface.TFrame")
        theme_row.pack(anchor=tk.W)
        self.light_radio = ttk.Radiobutton(theme_row, text=self._t("settings.light"), value="light", variable=self.ui_theme, command=self._on_theme_selected)
        self.light_radio.pack(side=tk.LEFT)
        self.dark_radio = ttk.Radiobutton(theme_row, text=self._t("settings.dark"), value="dark", variable=self.ui_theme, command=self._on_theme_selected)
        self.dark_radio.pack(side=tk.LEFT, padx=(18, 0))

        self.accent_title_label = ttk.Label(panel, text=self._t("settings.accent"), style="SettingTitle.TLabel")
        self.accent_title_label.grid(row=2, column=0, sticky=tk.NW, padx=(0, 30), pady=(14, 0))
        accent_box = ttk.Frame(panel, style="Surface.TFrame")
        accent_box.grid(row=2, column=1, sticky=tk.EW, pady=(14, 18))
        swatches = ttk.Frame(accent_box, style="Surface.TFrame")
        swatches.pack(anchor=tk.W)
        for color in THEME_PRESETS:
            swatch = tk.Button(swatches, width=3, height=1, bg=color, activebackground=color, relief=tk.FLAT, borderwidth=0, cursor="hand2", command=lambda selected=color: self._use_accent_color(selected))
            swatch.pack(side=tk.LEFT, padx=(0, 8))
        self.accent_preview = tk.Label(swatches, width=3, bg=self.palette["accent"], relief=tk.SOLID, borderwidth=1)
        self.accent_preview.pack(side=tk.LEFT, padx=(5, 10), fill=tk.Y)
        self.custom_color_btn = ttk.Button(swatches, text=self._t("settings.custom_color"), style="Secondary.TButton", command=self._pick_accent_color)
        self.custom_color_btn.pack(side=tk.LEFT)
        self.reset_color_btn = ttk.Button(swatches, text=self._t("settings.reset_color"), style="HomeLink.TButton", command=lambda: self._use_accent_color(DEFAULT_ACCENT_COLOR))
        self.reset_color_btn.pack(side=tk.LEFT, padx=(6, 0))

        self.font_title_label = ttk.Label(panel, text=self._t("settings.font"), style="SettingTitle.TLabel")
        self.font_title_label.grid(row=3, column=0, sticky=tk.NW, padx=(0, 30), pady=(14, 0))
        font_box = ttk.Frame(panel, style="Surface.TFrame")
        font_box.grid(row=3, column=1, sticky=tk.EW, pady=(14, 0))
        font_row = ttk.Frame(font_box, style="Surface.TFrame")
        font_row.pack(fill=tk.X)
        self.font_choice = ttk.Combobox(
            font_row,
            values=getattr(self, "_font_options", self._available_font_families()),
            state="readonly",
            width=38,
        )
        self.font_choice.set(self.ui_font_family.get())
        self.font_choice.pack(side=tk.LEFT)
        self.font_choice.bind("<<ComboboxSelected>>", self._on_font_selected)
        self.default_font_btn = ttk.Button(
            font_row,
            text=self._t("settings.default_font"),
            style="HomeLink.TButton",
            command=self._reset_font,
        )
        self.default_font_btn.pack(side=tk.LEFT, padx=(8, 0))
        self.font_preview_label = ttk.Label(font_box, text=self._t("settings.font_preview"), style="Preview.TLabel")
        self.font_preview_label.pack(fill=tk.X, pady=(10, 0))

    def _show_user_agreement_from_home(self):
        return self._show_user_agreement(require_acceptance=False)

    def _show_page(self, page_id: str):
        if page_id not in self.pages:
            raise ValueError(f"未知页面：{page_id}")
        self.pages[page_id].tkraise()
        for item_id, button in self.nav_buttons.items():
            button.configure(style="NavActive.TButton" if item_id == page_id else "Nav.TButton")
        self.current_page = page_id
        self.root.title(self._t("app.name"))

    def _build_migration_page(self, page):
        header = ttk.Frame(page, style="App.TFrame", padding=(18, 16, 18, 8))
        header.pack(fill=tk.X)
        self.migration_title_label = ttk.Label(header, text=self._t("migration.title"), style="PageTitle.TLabel")
        self.migration_title_label.pack(anchor=tk.W)
        self.migration_subtitle_label = ttk.Label(
            header,
            text=self._t("migration.subtitle"),
            style="PageSub.TLabel",
        )
        self.migration_subtitle_label.pack(anchor=tk.W, pady=(3, 0))

        footer = ttk.Frame(page, style="Surface.TFrame", padding=(16, 10))
        footer.pack(fill=tk.X, side=tk.BOTTOM)
        self.status_label = ttk.Label(footer, text=self._t("migration.ready"), style="Surface.TLabel")
        self.status_label.pack(side=tk.LEFT)
        self.progress = Progressbar(footer, mode="determinate", length=300, style="Fresh.Horizontal.TProgressbar")
        self.progress.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=16)
        self.build_btn = ttk.Button(footer, text=self._t("migration.build"), style="Primary.TButton", command=self._build_pack, state=tk.DISABLED)
        self.build_btn.pack(side=tk.RIGHT)

        main = ttk.Frame(page, style="App.TFrame", padding=(16, 8, 16, 10))
        main.pack(fill=tk.BOTH, expand=True)

        input_panel = ttk.Frame(main, style="Surface.TFrame", padding=12)
        input_panel.pack(fill=tk.X, pady=(0, 10))
        self.pack_file_label = ttk.Label(input_panel, text=self._t("migration.pack_file"), style="Surface.TLabel")
        self.pack_file_label.pack(side=tk.LEFT, padx=(0, 8))
        self.input_entry = ttk.Entry(input_panel, textvariable=self.input_path)
        self.input_entry.pack(side=tk.LEFT, fill=tk.X, expand=True)
        self.browse_input_btn = ttk.Button(input_panel, text=self._t("migration.choose_file"), style="Secondary.TButton", command=self._browse_input)
        self.browse_input_btn.pack(side=tk.LEFT, padx=8)
        self.parse_btn = ttk.Button(input_panel, text=self._t("migration.read_pack"), style="Primary.TButton", command=self._parse_pack)
        self.parse_btn.pack(side=tk.LEFT)

        overview = ttk.Frame(main, style="App.TFrame")
        overview.pack(fill=tk.X, pady=(0, 10))
        self.source_overview_frame = ttk.LabelFrame(overview, text=self._t("migration.source_overview"), style="Section.TLabelframe", padding=12)
        self.source_overview_frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 5))
        info_body = ttk.Frame(self.source_overview_frame, style="Surface.TFrame")
        info_body.pack(fill=tk.BOTH, expand=True)
        self.info_text = tk.Text(
            info_body,
            height=6,
            width=36,
            state=tk.DISABLED,
            font=(self.ui_font_family.get(), 10),
            bg=self.palette["surface"],
            fg=self.palette["text"],
            relief=tk.FLAT,
            borderwidth=0,
        )
        info_scroll = ttk.Scrollbar(info_body, orient=tk.VERTICAL, command=self.info_text.yview)
        info_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.info_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.info_text.configure(yscrollcommand=info_scroll.set)

        self.target_frame = ttk.LabelFrame(overview, text=self._t("migration.target"), style="Section.TLabelframe", padding=12)
        self.target_frame.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True, padx=(5, 0))
        self.target_frame.columnconfigure(1, weight=1); self.target_frame.columnconfigure(3, weight=1)
        ttk.Label(self.target_frame, text="Minecraft", style="Surface.TLabel").grid(row=0, column=0, sticky=tk.W, pady=4)
        target_mc_entry = ttk.Entry(self.target_frame, textvariable=self.target_mc, width=16)
        target_mc_entry.grid(row=0, column=1, sticky=tk.EW, pady=4, padx=(8, 16))
        self.loader_label = ttk.Label(self.target_frame, text=self._t("migration.loader"), style="Surface.TLabel")
        self.loader_label.grid(row=0, column=2, sticky=tk.W, pady=4)
        loader_combo = ttk.Combobox(self.target_frame, textvariable=self.target_loader_type,
                                     values=["forge", "fabric", "neoforge", "quilt"], state="readonly", width=14)
        loader_combo.grid(row=0, column=3, sticky=tk.EW, pady=4, padx=(8, 0))
        self.loader_version_label = ttk.Label(self.target_frame, text=self._t("migration.loader_version"), style="Surface.TLabel")
        self.loader_version_label.grid(row=1, column=0, sticky=tk.W, pady=4)
        loader_version_entry = ttk.Entry(self.target_frame, textvariable=self.target_loader_version, width=16)
        loader_version_entry.grid(row=1, column=1, sticky=tk.EW, pady=4, padx=(8, 16))
        self.fetch_loader_btn = ttk.Button(self.target_frame, text=self._t("migration.latest_loader"), style="Secondary.TButton", command=lambda: self._target_changed(fetch_loader=True, clear_loader=True, reset_targets=False, force=True))
        self.fetch_loader_btn.grid(row=1, column=2, columnspan=2, sticky=tk.EW, pady=4)
        self.output_dir_label = ttk.Label(self.target_frame, text=self._t("migration.output_dir"), style="Surface.TLabel")
        self.output_dir_label.grid(row=2, column=0, sticky=tk.W, pady=4)
        output_entry = ttk.Entry(self.target_frame, textvariable=self.output_dir)
        output_entry.grid(row=2, column=1, columnspan=2, sticky=tk.EW, pady=4, padx=(8, 8))
        self.browse_output_btn = ttk.Button(self.target_frame, text=self._t("migration.browse"), style="Secondary.TButton", command=self._browse_output_dir)
        self.browse_output_btn.grid(row=2, column=3, sticky=tk.EW, pady=4)
        self.output_name_label = ttk.Label(self.target_frame, text=self._t("migration.output_name"), style="Surface.TLabel")
        self.output_name_label.grid(row=3, column=0, sticky=tk.W, pady=4)
        output_name_entry = ttk.Entry(self.target_frame, textvariable=self.output_filename)
        output_name_entry.grid(row=3, column=1, columnspan=3, sticky=tk.EW, pady=4, padx=(8, 0))
        self.download_check = ttk.Checkbutton(self.target_frame, text=self._t("migration.embed_downloads"), variable=self.download_mods)
        self.download_check.grid(row=4, column=0, columnspan=2, sticky=tk.W, pady=(8, 2))
        self.find_btn = ttk.Button(self.target_frame, text=self._t("migration.check"), style="Primary.TButton", command=self._find_targets, state=tk.DISABLED)
        self.find_btn.grid(row=4, column=2, columnspan=2, sticky=tk.EW, pady=(8, 2))
        self.target_controls = [target_mc_entry, loader_combo, loader_version_entry, output_entry, output_name_entry]
        target_mc_entry.bind("<Return>", lambda e: self._target_changed(fetch_loader=True, clear_loader=True))
        target_mc_entry.bind("<FocusOut>", lambda e: self._target_changed(fetch_loader=True, clear_loader=True))
        loader_combo.bind("<<ComboboxSelected>>", lambda e: self._target_changed(fetch_loader=True, clear_loader=True))
        loader_version_entry.bind("<FocusOut>", lambda e: self._target_changed(reset_targets=False))

        self.notebook = ttk.Notebook(main); self.notebook.pack(fill=tk.BOTH, expand=True)
        self.compat_tab = ttk.Frame(self.notebook, style="Surface.TFrame", padding=10)
        self.notebook.add(self.compat_tab, text=self._t("migration.report"))
        compat_bar = ttk.Frame(self.compat_tab, style="Surface.TFrame"); compat_bar.pack(fill=tk.X, pady=(0, 8))
        self.compat_summary = ttk.Label(compat_bar, text=self._t("migration.report_hint"), style="Muted.TLabel")
        self.compat_summary.pack(side=tk.LEFT)
        self.compat_refresh_btn = ttk.Button(compat_bar, text=self._t("migration.refresh"), style="Secondary.TButton", command=self._run_compatibility_analysis, state=tk.DISABLED)
        self.compat_refresh_btn.pack(side=tk.RIGHT)
        self.compat_detail = tk.Text(self.compat_tab, height=3, wrap=tk.WORD, state=tk.DISABLED, bg=self.palette["surface_alt"], fg=self.palette["text"], relief=tk.FLAT, padx=10, pady=8)
        self.compat_detail.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 0))
        compat_body = ttk.Frame(self.compat_tab, style="Surface.TFrame"); compat_body.pack(fill=tk.BOTH, expand=True)
        compat_cols = ("severity", "category", "item", "message")
        self.compat_tree = ttk.Treeview(compat_body, columns=compat_cols, show="headings", selectmode="browse")
        for col, text_value, width in [("severity", "级别", 80), ("category", "类别", 110), ("item", "项目", 220), ("message", "结论", 600)]:
            self.compat_tree.heading(col, text=text_value); self.compat_tree.column(col, width=width, anchor=tk.W)
        self.compat_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        compat_scroll = ttk.Scrollbar(compat_body, orient=tk.VERTICAL, command=self.compat_tree.yview)
        compat_scroll.pack(side=tk.RIGHT, fill=tk.Y); self.compat_tree.configure(yscrollcommand=compat_scroll.set)
        for tag, color in [("blocking", "#FDECEC"), ("warning", "#FFF5E1"), ("info", "#EEF4FF"), ("ok", "#EAF7F0")]:
            self.compat_tree.tag_configure(tag, background=color)
        self.compat_tree.bind("<<TreeviewSelect>>", self._show_compatibility_detail)

        self.files_tab = ttk.Frame(self.notebook, style="Surface.TFrame", padding=10); self.notebook.add(self.files_tab, text=self._t("migration.files"))
        cols = ("name", "source_col", "category", "status_text", "delete_action")
        self.mod_tree = ttk.Treeview(self.files_tab, columns=cols, show="headings", selectmode="browse")
        self.mod_tree.heading("name", text="名称", command=lambda: self._sort_by("name"))
        self.mod_tree.heading("source_col", text="来源", command=lambda: self._sort_by("source"))
        self.mod_tree.heading("category", text="类别", command=lambda: self._sort_by("category"))
        self.mod_tree.heading("status_text", text="状态", command=lambda: self._sort_by("status_text"))
        self.mod_tree.heading("delete_action", text="输出")
        self.mod_tree.column("name", width=390); self.mod_tree.column("source_col", width=70, anchor="center")
        self.mod_tree.column("category", width=90); self.mod_tree.column("status_text", width=220)
        self.mod_tree.column("delete_action", width=90, anchor="center")
        self.mod_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        sb = ttk.Scrollbar(self.files_tab, orient=tk.VERTICAL, command=self.mod_tree.yview); sb.pack(side=tk.RIGHT, fill=tk.Y)
        self.mod_tree.configure(yscrollcommand=sb.set)
        for t, c in [("found", "#EAF7F0"), ("not_found", "#FDECEC"), ("warning", "#FFF5E1"), ("disabled", "#F1ECFA"), ("excluded", "#EEF1F4"), ("pending", "#F5F7FA")]:
            self.mod_tree.tag_configure(t, background=c)
        self.mod_tree.bind("<ButtonRelease-1>", self._on_tree_click)
        self.tree_menu = tk.Menu(self.mod_tree, tearoff=0)
        self.tree_menu.add_command(label="从输出排除此项", command=self._confirm_delete)
        self.tree_menu.add_separator()
        self.tree_menu.add_command(label="在 CurseForge 查看", command=self._open_cf_page)
        self.tree_menu.add_command(label="在 Modrinth 查看", command=self._open_mr_page)
        self.mod_tree.bind("<Button-3>", self._on_tree_right_click)
        self.mod_tree.bind("<Delete>", lambda e: self._confirm_delete())
        self.log_tab = ttk.Frame(self.notebook, style="Surface.TFrame", padding=10); self.notebook.add(self.log_tab, text=self._t("migration.log"))
        self.log_text = tk.Text(
            self.log_tab,
            state=tk.DISABLED,
            font=("Consolas", 9),
            bg=self.palette["log_bg"],
            fg=self.palette["log_fg"],
            insertbackground=self.palette["log_fg"],
            relief=tk.FLAT,
        )
        log_scroll = ttk.Scrollbar(self.log_tab, orient=tk.VERTICAL, command=self.log_text.yview)
        log_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.log_text.configure(yscrollcommand=log_scroll.set)

    # ================================================================
    # 自动获取加载器版本
    # ================================================================
    def _auto_fetch_loader_version(self):
        """在后台线程中获取最新加载器版本并填充"""
        lt = self.target_loader_type.get().strip()
        tmc = self.target_mc.get().strip()
        if not lt or not tmc: return
        self.loader_request_id += 1
        request_id = self.loader_request_id
        starting_value = self.target_loader_version.get().strip()

        def task():
            Logger.log("info", f"正在获取 {lt} 最新版本 (MC={tmc})...")
            ver = fetch_latest_loader_version(lt, tmc, self.cancel_event)
            if ver:
                def apply_version():
                    if (
                        request_id == self.loader_request_id
                        and self.target_loader_type.get().strip() == lt
                        and self.target_mc.get().strip() == tmc
                        and self.target_loader_version.get().strip() == starting_value
                    ):
                        self.target_loader_version.set(ver)
                        self._last_target_snapshot = self._target_snapshot()
                self._schedule(0, apply_version)
                Logger.log("info", f"{lt} 最新稳定版: {ver}")
            else:
                Logger.log("warn", f"无法获取 {lt} 版本，请手动输入")

        self._start_worker(task)

    def _target_changed(self, fetch_loader: bool = False, clear_loader: bool = False,
                        reset_targets: bool = True, force: bool = False):
        current = self._target_snapshot()
        if not force and current == getattr(self, "_last_target_snapshot", current):
            return
        self.loader_request_id += 1
        if clear_loader:
            self.target_loader_version.set("")
        self._last_target_snapshot = self._target_snapshot()
        self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
        self._build_after_resolution = False
        self._build_resume_pending = False
        self._resolution_skips = set()
        if self.pack_info and reset_targets: self._reset_target_results()
        if hasattr(self, "build_btn"): self.build_btn.configure(state=tk.DISABLED)
        self._clear_compatibility_display_key("runtime.target_changed", "#A96000")
        if fetch_loader:
            self._auto_fetch_loader_version()
        elif hasattr(self, "find_btn") and not self.working:
            self._set_working(False)

    def _on_target_value_write(self, *_args) -> None:
        if not hasattr(self, "build_btn"):
            return
        self._refresh_auto_output_filename()
        current = self._target_snapshot()
        previous = getattr(self, "_last_target_snapshot", current)
        if self.pack_info and current[:2] != previous[:2]:
            self._reset_target_results()
        if self.analysis_target_snapshot and current != self.analysis_target_snapshot:
            self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
            self._build_after_resolution = False
            self._build_resume_pending = False
            self._resolution_skips = set()
            self._clear_compatibility_display_key("runtime.target_changed", "#A96000")
        if not self.working:
            self._set_working(False)

    def _content_changed(self) -> None:
        self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
        self._build_after_resolution = False
        self._build_resume_pending = False
        self._resolution_skips = set()
        if hasattr(self, "build_btn"):
            self.build_btn.configure(state=tk.DISABLED)
        self._clear_compatibility_display_key("runtime.output_changed", "#A96000")
        if not self.working:
            self._set_working(False)

    def _clear_compatibility_display(self, message: str, color: str = "#667085") -> None:
        if hasattr(self, "compat_tree"):
            for item in self.compat_tree.get_children():
                self.compat_tree.delete(item)
        if hasattr(self, "compat_detail"):
            self.compat_detail.configure(state=tk.NORMAL)
            self.compat_detail.delete("1.0", tk.END)
            self.compat_detail.configure(state=tk.DISABLED)
        if hasattr(self, "compat_summary"):
            self.compat_summary.configure(text=message, foreground=color)

    def _clear_compatibility_display_key(self, key: str, color: str = "#667085", **values) -> None:
        self._compatibility_state = (key, values, color)
        self._clear_compatibility_display(self._t(key, **values), color)

    def _reset_target_results(self):
        if not self.pack_info: return
        for mod in self.pack_info.mods:
            if mod.passthrough:
                mod.status = "passthrough"
                continue
            if not mod.original_source:
                mod.original_source = mod.source; mod.original_project_id = mod.project_id
            mod.source = mod.original_source; mod.project_id = mod.original_project_id
            mod.status = "pending"; mod.note = ""
            mod.preserve_original = False
            mod.target_file_id = ""; mod.target_version_id = ""; mod.target_download_url = ""
            mod.target_file_name = ""; mod.target_file_size = 0; mod.target_hashes = {}
            mod.target_dependencies = []; mod.dependency_metadata_available = False
            if mod.excluded:
                mod.status = "excluded"
        if hasattr(self, "mod_tree"): self._refresh_mod_tree()

    def _compatibility_message(self, issue: CompatibilityIssue) -> str:
        evidence = issue.evidence
        key = f"compat.issue.{issue.code}"
        if key not in TRANSLATIONS["zh_CN"]:
            return issue.message
        return self._t(
            key,
            dependency=evidence.get("dependency", self._t("compat.item.modpack")),
            item=evidence.get("incompatible_with", self._t("compat.item.modpack")),
        )

    def _translate_limitation(self, text: str) -> str:
        translations = (
            ("Static analysis cannot inspect mod bytecode", "limitation.bytecode"),
            ("Only recognized direct required/incompatible relations", "limitation.direct_relations"),
            ("Dependency/conflict metadata was absent", "limitation.metadata_absent"),
        )
        for prefix, key in translations:
            if text.startswith(prefix):
                return self._t(key)
        return text

    def _render_compatibility_report(self, report) -> None:
        selected_row = None
        view_start = 0.0
        try:
            selection = self.compat_tree.selection()
            if selection and hasattr(self, "_compatibility_rows"):
                selected_row = self._compatibility_rows[int(selection[0])]
            view_start = self.compat_tree.yview()[0]
        except (AttributeError, IndexError, TypeError, ValueError, tk.TclError):
            pass
        for item in self.compat_tree.get_children(): self.compat_tree.delete(item)
        self._compatibility_rows = []
        selected_iid = None
        severity_keys = {"error": "compat.error", "warning": "compat.warning", "info": "compat.info"}
        scope_keys = {
            "mod": "compat.scope.mod", "resourcepack": "compat.scope.resourcepack",
            "shaderpack": "compat.scope.shaderpack", "content": "compat.scope.content",
            "dependency": "compat.scope.dependency", "output": "compat.scope.output",
            "general": "compat.scope.general",
        }
        for issue in report.issues:
            row_index = len(self._compatibility_rows); self._compatibility_rows.append(issue)
            if issue is selected_row:
                selected_iid = str(row_index)
            tag = "blocking" if issue.severity == "error" else issue.severity
            self.compat_tree.insert("", tk.END, iid=str(row_index), values=(
                self._t(severity_keys.get(issue.severity, "compat.info")),
                self._t(scope_keys.get(issue.scope, "compat.scope.general")), issue.item or issue.path or self._t("compat.item.modpack"),
                self._compatibility_message(issue)), tags=(tag,))
        for limitation in report.limitations:
            row_index = len(self._compatibility_rows); self._compatibility_rows.append(limitation)
            if limitation == selected_row:
                selected_iid = str(row_index)
            self.compat_tree.insert("", tk.END, iid=str(row_index), values=(
                self._t("compat.boundary"), self._t("compat.static"), self._t("compat.description"),
                self._translate_limitation(limitation)), tags=("info",))
        if not report.issues:
            self.compat_tree.insert("", tk.END, values=(
                self._t("compat.pass"), self._t("compat.overall"), self._t("compat.item.modpack"),
                self._t("compat.no_issues")), tags=("ok",))
        counts = report.counts
        self.compat_summary.configure(text=self._t(
            "compat.summary", errors=counts.get("error", 0), warnings=counts.get("warning", 0),
            items=report.stats.get("content_items_checked", 0),
        ))
        try:
            self.compat_tree.yview_moveto(view_start)
            if selected_iid is not None:
                self.compat_tree.selection_set(selected_iid)
                self.compat_tree.see(selected_iid)
                self._show_compatibility_detail()
            elif hasattr(self, "compat_detail"):
                self.compat_detail.configure(state=tk.NORMAL)
                self.compat_detail.delete("1.0", tk.END)
                self.compat_detail.configure(state=tk.DISABLED)
        except (AttributeError, tk.TclError):
            pass

    def _run_compatibility_analysis(self, prompt_on_errors: bool = True):
        if not self.pack_info:
            messagebox.showinfo(self._t("dialog.compatibility"), self._t("dialog.read_pack_first")); return
        if not self._target_settings_complete():
            messagebox.showerror(self._t("dialog.target_incomplete_title"), self._t("dialog.target_incomplete")); return
        if prompt_on_errors:
            self._notified_dependency_warnings = set()
        self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
        self._clear_compatibility_display_key("runtime.compat_rechecking", "#2563EB")
        if any(mod.status == "pending" for mod in self.pack_info.mods if not mod.excluded):
            self._find_targets(prompt_on_errors=prompt_on_errors, pending_only=True); return
        self._set_working(True); self._set_status_key("runtime.compat_generating", "#2563EB")
        self.progress.configure(mode="indeterminate"); self.progress.start(12)
        info = self.pack_info; tmc = self.target_mc.get().strip(); tl = self.target_loader_type.get().strip()
        analysis_snapshot = self._target_snapshot()

        def task():
            try:
                _check_cancelled(self.cancel_event)
                report = analyze_compatibility(
                    info.mods, None, info.mc_version, tmc,
                    info.loader_type, tl, target_format=info.format_type,
                    passthrough_paths=info.override_paths,
                    cancel_event=self.cancel_event)
                _check_cancelled(self.cancel_event)
                self._schedule(
                    0,
                    lambda r=report, s=analysis_snapshot, p=prompt_on_errors:
                        self._apply_compatibility_report(r, s, p),
                )
            except OperationCancelled:
                return
            except Exception as e:
                if self.cancel_event.is_set():
                    return
                Logger.log("error", f"兼容性分析失败: {traceback.format_exc()}")
                self._schedule(0, lambda msg=str(e): self._on_error(msg, "error.compatibility"))
        self._start_worker(task)

    def _apply_compatibility_report(
        self,
        report,
        analysis_snapshot=None,
        prompt_on_errors: bool = True,
    ):
        self.progress.stop(); self.progress.configure(mode="determinate", value=0)
        analysis_snapshot = analysis_snapshot or self._target_snapshot()
        if analysis_snapshot != self._target_snapshot():
            self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
            self._build_after_resolution = False
            self._build_resume_pending = False
            self._resolution_skips = set()
            self._clear_compatibility_display_key("runtime.compat_stale", "#A96000")
            self._set_working(False)
            return
        self.compatibility_report = report; self.analysis_ready = True
        self.analysis_target_snapshot = analysis_snapshot
        self._render_compatibility_report(report)
        counts = report.counts
        summary_color = (
            "#C23B3B" if report.has_errors
            else "#A96000" if counts.get("warning", 0)
            else "#18864B"
        )
        self.compat_summary.configure(foreground=summary_color)
        self.compat_refresh_btn.configure(state=tk.NORMAL)
        self.notebook.select(0); self._set_working(False)
        self._show_missing_dependency_notice(report)
        if report.has_errors:
            self._set_status_key("runtime.compat_blocking", "#C23B3B")
            if prompt_on_errors and self.pack_info:
                self._schedule(0, lambda: self._resolve_compatibility_errors(build_after=False))
            elif getattr(self, "_build_after_resolution", False):
                self._schedule(
                    0,
                    lambda: self._resolve_compatibility_errors(
                        build_after=True, continuing=True),
                )
        else:
            if counts.get("warning", 0):
                self._set_status_key("runtime.compat_warning", "#A96000")
            else:
                self._set_status_key("runtime.compat_ready", "#18864B")
            if getattr(self, "_build_after_resolution", False):
                self._build_after_resolution = False
                self._build_resume_pending = True
                if hasattr(self, "build_btn"):
                    self.build_btn.configure(state=tk.DISABLED)
                self._schedule(0, self._resume_build_after_resolution)

    def _resume_build_after_resolution(self) -> None:
        if not getattr(self, "_build_resume_pending", False):
            return
        self._build_resume_pending = False
        self._build_pack()
        if not getattr(self, "working", False) and hasattr(self, "build_btn"):
            self._set_working(False)

    def _mod_for_issue(self, issue: CompatibilityIssue) -> ModInfo | None:
        if not self.pack_info:
            return None
        raw_index = issue.evidence.get("item_index") if issue.evidence else None
        try:
            item_index = int(raw_index)
        except (TypeError, ValueError):
            item_index = -1
        if 0 <= item_index < len(self.pack_info.mods):
            return self.pack_info.mods[item_index]

        label = str(issue.item or "").casefold()
        if not label:
            return None
        matches = [
            mod for mod in self.pack_info.mods
            if label in {
                str(mod.name or "").casefold(),
                str(mod.file_name or "").casefold(),
                str(mod.target_file_name or "").casefold(),
            }
        ]
        return matches[0] if len(matches) == 1 else None

    @staticmethod
    def _dependency_issue_key(issue: CompatibilityIssue) -> tuple[str, str, str]:
        evidence = issue.evidence or {}
        source = str(evidence.get("source", "") or "").strip().casefold()
        reference_type = str(
            evidence.get("dependency_reference_type", "project_id") or "project_id"
        ).strip().casefold()
        reference = str(evidence.get("dependency", "") or "").strip()
        return source, reference_type, reference.casefold()

    def _dependency_platform_text(self, source: str) -> str:
        return {
            "modrinth": "Modrinth",
            "curseforge": "CurseForge",
        }.get(
            source.casefold(), source or self._t("common.unknown_platform"))

    def _show_missing_dependency_notice(self, report) -> None:
        seen = getattr(self, "_notified_dependency_warnings", set())
        groups: dict[tuple[str, str, str], dict[str, object]] = {}
        for issue in report.issues:
            if issue.code != "missing_required_dependency":
                continue
            key = self._dependency_issue_key(issue)
            if not key[2] or key in seen:
                continue
            evidence = issue.evidence or {}
            group = groups.setdefault(key, {
                "reference": str(
                    evidence.get("dependency_exact")
                    or evidence.get("dependency")
                    or key[2]
                ),
                "owners": [],
            })
            owner = issue.item or self._t("common.unknown_item")
            owners = group["owners"]
            if isinstance(owners, list) and owner not in owners:
                owners.append(owner)

        if not groups:
            return
        seen.update(groups)
        self._notified_dependency_warnings = seen
        lines: list[str] = []
        for (source, _reference_type, _normalized_reference), group in list(groups.items())[:20]:
            owners = group["owners"] if isinstance(group["owners"], list) else []
            owner_text = (", " if self._current_language() == "en_US" else "、").join(str(owner) for owner in owners[:3])
            if len(owners) > 3:
                owner_text += self._t("deps.more_owners", count=len(owners) - 3)
            lines.append("- " + self._t(
                "deps.owner", reference=group["reference"],
                platform=self._dependency_platform_text(source), owners=owner_text,
            ))
        if len(groups) > 20:
            lines.append("- " + self._t("deps.more", count=len(groups) - 20))
        messagebox.showwarning(
            self._t("deps.title"),
            self._t("deps.intro") + "\n\n" + "\n".join(lines) + "\n\n" + self._t("deps.footer"),
        )

    def _show_unresolved_blockers(self) -> None:
        report = self.compatibility_report
        count = report.counts.get("error", 0) if report else 0
        if hasattr(self, "notebook"):
            self.notebook.select(0)
        self._set_status_key("runtime.unresolved", "#C23B3B")
        messagebox.showwarning(
            self._t("blockers.title"), self._t("blockers.body", count=count),
        )

    def _resolve_compatibility_errors(
        self,
        build_after: bool = False,
        continuing: bool = False,
    ) -> None:
        if not continuing:
            self._resolution_skips = set()
        report = self.compatibility_report
        if (
            not self.pack_info
            or not report
            or self.analysis_target_snapshot != self._target_snapshot()
        ):
            return
        errors = [issue for issue in report.issues if issue.severity == "error"]
        if not errors:
            if build_after:
                self._build_pack()
            return

        changed = False

        for issue in (item for item in errors if item.code == "item_not_found"):
            mod = self._mod_for_issue(issue)
            if not mod or mod.excluded:
                continue
            resolution_key = (
                "item_not_found",
                (issue.evidence or {}).get("item_index", id(mod)),
            )
            if resolution_key in self._resolution_skips:
                continue
            exclude = messagebox.askyesno(
                self._t("resolution.not_found_title"),
                self._t(
                    "resolution.not_found_export" if build_after else "resolution.not_found_check",
                    name=self._display_mod_name(mod),
                ),
            )
            if exclude:
                mod.excluded = True
                mod.preserve_original = False
                mod.status = "excluded"
                mod.note = "用户选择排除无目标版本的项目"
                changed = True
            else:
                self._resolution_skips.add(resolution_key)

        excludable_codes = {
            "required_embedded_download_unavailable",
            "required_embedded_scope_unsupported",
            "unsafe_output_path",
            "override_output_collision",
            "explicitly_incompatible_item",
            "explicit_incompatibility",
        }
        grouped_item_errors: dict[int, tuple[ModInfo, list[CompatibilityIssue]]] = {}
        for issue in errors:
            if issue.code not in excludable_codes:
                continue
            mod = self._mod_for_issue(issue)
            if not mod or mod.excluded:
                continue
            group = grouped_item_errors.setdefault(id(mod), (mod, []))[1]
            group.append(issue)
        for mod, issues in grouped_item_errors.values():
            if mod.excluded:
                continue
            active_issues: list[CompatibilityIssue] = []
            for issue in issues:
                evidence = issue.evidence or {}
                raw_target_index = next(
                    (
                        evidence.get(key)
                        for key in (
                            "target_item_index",
                            "dependency_item_index",
                            "incompatible_item_index",
                        )
                        if evidence.get(key) is not None
                    ),
                    None,
                )
                try:
                    target_index = int(raw_target_index)
                except (TypeError, ValueError):
                    target_index = -1
                if (
                    0 <= target_index < len(self.pack_info.mods)
                    and self.pack_info.mods[target_index].excluded
                ):
                    continue
                active_issues.append(issue)
            if not active_issues:
                continue
            issues = active_issues
            resolution_key = (
                "item_errors",
                (issues[0].evidence or {}).get("item_index", id(mod)),
                tuple(sorted(issue.code for issue in issues)),
            )
            if resolution_key in self._resolution_skips:
                continue
            details = "\n".join(f"- {self._compatibility_message(issue)}" for issue in issues)
            if messagebox.askyesno(
                self._t("resolution.item_title"),
                self._t("resolution.item_body", name=self._display_mod_name(mod), details=details),
            ):
                mod.excluded = True
                mod.preserve_original = False
                mod.status = "excluded"
                mod.note = "用户选择排除存在阻断问题的项目"
                changed = True
            else:
                self._resolution_skips.add(resolution_key)

        for issue in (item for item in errors if item.code == "duplicate_output_path"):
            raw_indexes = (issue.evidence or {}).get("item_indexes", []) or []
            indexes: list[int] = []
            for value in raw_indexes:
                try:
                    index = int(value)
                except (TypeError, ValueError):
                    continue
                if 0 <= index < len(self.pack_info.mods) and index not in indexes:
                    indexes.append(index)
            active = [
                (index, self.pack_info.mods[index])
                for index in indexes
                if not self.pack_info.mods[index].excluded
            ]
            for mod_index, mod in active[1:]:
                resolution_key = ("duplicate_output_path", issue.path or "", mod_index)
                if resolution_key in self._resolution_skips:
                    continue
                if messagebox.askyesno(
                    self._t("resolution.duplicate_title"),
                    self._t(
                        "resolution.duplicate_body",
                        name=self._display_mod_name(mod),
                        path=issue.path or self._t("resolution.unknown_path"),
                    ),
                ):
                    mod.excluded = True
                    mod.preserve_original = False
                    mod.status = "excluded"
                    mod.note = "用户选择排除重复输出路径项目"
                    changed = True
                else:
                    self._resolution_skips.add(resolution_key)

        if changed:
            self._restart_analysis_after_resolution(build_after)
        elif build_after:
            self._build_after_resolution = False
            self._show_unresolved_blockers()
    def _restart_analysis_after_resolution(self, build_after: bool) -> None:
        self._build_after_resolution = build_after
        self._refresh_mod_tree()
        self._update_info()
        self.analysis_ready = False
        self.compatibility_report = None
        self.analysis_target_snapshot = None
        self._run_compatibility_analysis(prompt_on_errors=False)

    def _show_compatibility_detail(self, event=None):
        selection = self.compat_tree.selection()
        if not selection: return
        try: row = self._compatibility_rows[int(selection[0])]
        except (AttributeError, ValueError, IndexError): return
        if isinstance(row, str): text = self._translate_limitation(row)
        else:
            confidence_key = {
                "confirmed": "detail.confirmed",
                "heuristic": "detail.heuristic",
                "incomplete": "detail.incomplete",
            }.get(row.confidence)
            confidence = self._t(confidence_key) if confidence_key else row.confidence
            details = [
                self._compatibility_message(row),
                self._t("detail.confidence", value=confidence),
                self._t("detail.code", value=row.code),
            ]
            if row.path:
                details.append(self._t("detail.path", value=row.path))
            if row.evidence:
                details.append(self._t(
                    "detail.evidence",
                    value=json.dumps(row.evidence, ensure_ascii=False),
                ))
            text = "\n".join(details)
        self.compat_detail.configure(state=tk.NORMAL); self.compat_detail.delete("1.0", tk.END)
        self.compat_detail.insert("1.0", text); self.compat_detail.configure(state=tk.DISABLED)

    def _browse_input(self):
        path = filedialog.askopenfilename(
            title=self._t("dialog.choose_pack"),
            filetypes=[(self._t("dialog.pack_files"), "*.zip;*.mrpack"), (self._t("dialog.all_files"), "*.*")],
        )
        if path:
            self.input_path.set(path)
            self._refresh_auto_output_filename(force=True, source_mc="")
            if not self.output_dir.get(): self.output_dir.set(os.path.dirname(path) or ".")
    def _browse_output_dir(self):
        d = filedialog.askdirectory(title=self._t("dialog.output_dir"))
        if d: self.output_dir.set(d)
    def _get_output_path(self) -> str:
        d = self.output_dir.get().strip() or "."
        name = self.output_filename.get().strip() or "migrated_modpack"
        fmt = self.pack_info.format_type if self.pack_info else ""
        ext = ".mrpack" if fmt == "modrinth" else ".zip"
        if not name.endswith(ext): name += ext
        return os.path.join(d, name)

    def _get_selected_mod(self) -> ModInfo | None:
        sel = self.mod_tree.selection()
        if not sel or not self.pack_info: return None
        row_mapping = getattr(self, "_mod_rows", {})
        if sel[0] in row_mapping:
            return row_mapping[sel[0]]
        item = sel[0]; values = self.mod_tree.item(item, "values")
        if not values or len(values) < 5: return None
        name = values[0]; cd = values[2]
        cmr = {"模组": "mod", "资源包": "resourcepack", "光影包": "shaderpack"}
        rc = cd.replace("🔒", ""); cat = cmr.get(rc, rc.lower())
        for m in self.pack_info.mods:
            if m.name == name and (m.category == cat or cat == rc.lower()): return m
        return None

    def _display_mod_name(self, mod: ModInfo) -> str:
        name = str(mod.name or "").strip()
        if not name or name == "未知文件":
            return self._t("content.unknown_file")
        project_match = re.fullmatch(r"Project #(.*)", name)
        if project_match:
            return self._t("content.project", project_id=project_match.group(1))
        return name

    def _open_cf_page(self):
        mod = self._get_selected_mod()
        if not mod: messagebox.showinfo(self._t("common.info"), self._t("dialog.select_file_first")); return
        if mod.cf_slug: url = CurseForgeAPI.make_mod_url(slug=mod.cf_slug, category=mod.category)
        elif mod.project_id and mod.project_id.isdigit(): url = CurseForgeAPI.make_mod_url(project_id=int(mod.project_id), category=mod.category)
        else:
            clean_name = mod.file_name
            for ext in ['.jar.disabled','.jar','.zip','.disabled']: clean_name = clean_name.replace(ext, '')
            query = re.sub(r'[^a-zA-Z]', ' ', clean_name); query = re.sub(r'\s+', ' ', query).strip()
            url = f"https://www.curseforge.com/minecraft/search?search={requests.utils.quote(query)}"
        Logger.log("info", f"打开 CurseForge: {url}"); webbrowser.open(url)

    def _open_mr_page(self):
        mod = self._get_selected_mod()
        if not mod: messagebox.showinfo(self._t("common.info"), self._t("dialog.select_file_first")); return
        if mod.project_id and not mod.project_id.isdigit(): url = ModrinthAPI.make_mod_url(project_id=mod.project_id)
        elif mod.mr_slug: url = ModrinthAPI.make_mod_url(slug=mod.mr_slug)
        else:
            clean_name = mod.file_name
            for ext in ['.jar.disabled','.jar','.zip','.disabled']: clean_name = clean_name.replace(ext, '')
            query = re.sub(r'[^a-zA-Z]', ' ', clean_name); query = re.sub(r'\s+', ' ', query).strip()
            url = f"https://modrinth.com/search?query={requests.utils.quote(query)}"
        Logger.log("info", f"打开 Modrinth: {url}"); webbrowser.open(url)

    def _sort_by(self, col: str):
        if col == "delete_action": return
        if self.sort_column == col: self.sort_reverse = not self.sort_reverse
        else: self.sort_column = col; self.sort_reverse = False
        self._refresh_mod_tree()
    def _get_sorted_mods(self) -> list[ModInfo]:
        if not self.pack_info: return []
        mods = self.pack_info.mods[:]
        if not self.sort_column: mods.sort(key=lambda m: (self.CATEGORY_PRIORITY.get(m.category, 9), m.name.lower()))
        else:
            if self.sort_column == "status_text": kfn = lambda m: self.STATUS_PRIORITY.get(m.status, 9)
            elif self.sort_column == "category": kfn = lambda m: self.CATEGORY_PRIORITY.get(m.category, 9)
            elif self.sort_column == "source": kfn = lambda m: (m.source or "").lower()
            else: kfn = lambda m: ((getattr(m, self.sort_column, "") or "").lower() if isinstance(getattr(m, self.sort_column, ""), str) else (getattr(m, self.sort_column, "") or 0))
            mods.sort(key=kfn, reverse=self.sort_reverse)
        return mods
    def _on_tree_click(self, event):
        if self.working: return
        region = self.mod_tree.identify_region(event.x, event.y)
        if region != "cell": return
        if self.mod_tree.identify_column(event.x) == "#5":
            item = self.mod_tree.identify_row(event.y)
            if item: self.mod_tree.selection_set(item); self._confirm_delete()
    def _on_tree_right_click(self, event):
        if self.working: return
        item = self.mod_tree.identify_row(event.y)
        if item: self.mod_tree.selection_set(item); self.tree_menu.post(event.x_root, event.y_root)
    def _confirm_delete(self):
        if self.working: return
        mod = self._get_selected_mod()
        if not mod: return
        if mod.excluded:
            mod.excluded = False; mod.status = "pending"
            Logger.log("info", f"已恢复到输出: {mod.name}")
            self._refresh_mod_tree(); self._update_info(); self._content_changed()
            return
        if messagebox.askyesno(
            self._t("dialog.exclude_title"),
            self._t("dialog.exclude", name=self._display_mod_name(mod)),
        ):
            self._delete_selected_item()
    def _delete_selected_item(self):
        sel = self.mod_tree.selection()
        if not sel or not self.pack_info: return
        for item in sel:
            mapped_mod = getattr(self, "_mod_rows", {}).get(item)
            if mapped_mod is not None:
                mapped_mod.excluded = True; mapped_mod.status = "excluded"
                Logger.log("info", f"已从输出排除: {mapped_mod.name}")
                continue
            values = self.mod_tree.item(item, "values")
            if not values: continue
            name = values[0]; cd = values[2] if len(values) > 2 else ""
            cmr = {"模组": "mod", "资源包": "resourcepack", "光影包": "shaderpack"}
            rc = cd.replace("🔒", ""); cat = cmr.get(rc, "mod")
            tr = None
            for m in self.pack_info.mods:
                if m.name == name and m.category == cat: tr = m; break
            if tr:
                tr.excluded = True; tr.status = "excluded"
                Logger.log("info", f"已从输出排除: {name}")
        self._refresh_mod_tree(); self._update_info(); self._content_changed()
    def _set_info(self, text: str):
        self.info_text.configure(state=tk.NORMAL); self.info_text.delete("1.0", tk.END)
        self.info_text.insert("1.0", text); self.info_text.configure(state=tk.DISABLED)

    def _content_summary(self, include_excluded: bool = True) -> str:
        if not self.pack_info:
            return self._t("count.files", count=0)
        counts = {"mod": 0, "resourcepack": 0, "shaderpack": 0}
        disabled = excluded = 0
        for mod in self.pack_info.mods:
            counts[mod.category] = counts.get(mod.category, 0) + 1
            disabled += bool(mod.disabled)
            excluded += bool(mod.excluded)
        parts = []
        for category, key in (
            ("mod", "count.mods"),
            ("resourcepack", "count.resourcepacks"),
            ("shaderpack", "count.shaderpacks"),
        ):
            if counts.get(category, 0):
                parts.append(self._t(key, count=counts[category]))
        if disabled:
            parts.append(self._t("count.disabled", count=disabled))
        if include_excluded and excluded:
            parts.append(self._t("count.excluded", count=excluded))
        return " + ".join(parts) if parts else self._t("count.files", count=len(self.pack_info.mods))

    def _update_info(self):
        if not self.pack_info: return
        try:
            view_start = self.info_text.yview()[0]
        except (AttributeError, IndexError, tk.TclError):
            view_start = 0.0
        info = self.pack_info
        unknown = self._t("overview.unknown")
        loader = f"{info.loader_type or unknown} {info.loader_version}".strip()
        self._set_info(self._t(
            "overview.body",
            name=info.raw_data.get("name") or self._t("overview.untitled"),
            format=info.format_type.upper() if info.format_type else unknown,
            minecraft=info.mc_version or unknown,
            loader=loader,
            content=self._content_summary(include_excluded=True),
        ))
        try:
            self.info_text.yview_moveto(view_start)
        except (AttributeError, tk.TclError):
            pass
    def _set_status(self, text: str, color: str = "gray"): self.status_label.configure(text=text, foreground=color)
    def _set_status_key(self, key: str, color: str = "gray", **values):
        self._status_state = (key, values, color)
        self._set_status(self._t(key, **values), color)
    def _set_working(self, working: bool):
        self.working = working; st = tk.DISABLED if working else tk.NORMAL
        input_is_current = self._parsed_input_matches_current()
        can_check = bool(
            not working and self.pack_info and input_is_current
            and self._target_settings_complete()
        )
        self.find_btn.configure(state=tk.NORMAL if can_check else tk.DISABLED)
        self.parse_btn.configure(state=st)
        self.input_entry.configure(state=st)
        for control in getattr(self, "target_controls", []): control.configure(state=st)
        for control in (self.browse_input_btn, self.browse_output_btn, self.fetch_loader_btn, self.download_check):
            control.configure(state=st)
        self.compat_refresh_btn.configure(state=tk.NORMAL if can_check else tk.DISABLED)
        if not working and self.target_loader_type.get() in ("forge", "fabric", "neoforge", "quilt"):
            try: self.target_controls[1].configure(state="readonly")
            except Exception: pass
        can_build = bool(
            not working and self.pack_info and input_is_current
            and self._target_settings_complete()
            and self._analysis_matches_target()
        )
        self.build_btn.configure(state=tk.NORMAL if can_build else tk.DISABLED)

    def _parse_pack(self):
        path = self.input_path.get()
        if not path or not os.path.exists(path): messagebox.showerror(self._t("dialog.error"), self._t("dialog.valid_pack")); return
        self._cleanup_temp_overrides()
        self.pack_info = None; self.parsed_input_path = ""
        self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
        self._build_after_resolution = False
        self._build_resume_pending = False
        self._resolution_skips = set()
        self._set_info("")
        for item in self.mod_tree.get_children(): self.mod_tree.delete(item)
        self._clear_compatibility_display_key("runtime.reading", "#667085")
        self._set_working(True); self._set_status_key("runtime.parsing", "blue")
        Logger.log("info", f"开始解析: {path}")
        def task():
            temp_dir = ""
            try:
                info = PackParser.parse(path, self.cancel_event)
                temp_dir = tempfile.mkdtemp(prefix="mcp_overrides_")
                PackParser.extract_overrides(path, temp_dir, self.cancel_event)
                _check_cancelled(self.cancel_event)
                self.pack_info = info
                self.parsed_input_path = os.path.abspath(path)
                self.temp_overrides_dir = temp_dir; temp_dir = ""
                self._schedule(0, self._on_parse_done)
            except OperationCancelled:
                if temp_dir: shutil.rmtree(temp_dir, ignore_errors=True)
                return
            except Exception as e:
                if temp_dir: shutil.rmtree(temp_dir, ignore_errors=True)
                err_msg = str(e)
                Logger.log("error", f"解析失败: {traceback.format_exc()}")
                self._schedule(0, lambda msg=err_msg: self._on_error(msg, "error.parse"))
        self._start_worker(task)
    def _on_parse_done(self):
        info = self.pack_info
        self._update_info()
        if info.loader_type: self.target_loader_type.set(info.loader_type)
        self.target_loader_version.set("")
        self._last_target_snapshot = self._target_snapshot()
        self._refresh_auto_output_filename(source_mc=info.mc_version)
        self._refresh_mod_tree()
        self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
        self.compat_refresh_btn.configure(state=tk.NORMAL)
        self._clear_compatibility_display_key("runtime.read_ready", "#667085")
        summary = self._content_summary(include_excluded=False)
        self._set_status_key("runtime.read_done", "green", summary=summary)
        self._set_working(False)
        Logger.log("info", f"解析完成: fmt={info.format_type} mc={info.mc_version} loader={info.loader_type} total={len(info.mods)}")
        self._schedule(0, self._auto_fetch_loader_version)
    def _refresh_mod_tree(self):
        selected_mod = None
        view_start = 0.0
        try:
            selection = self.mod_tree.selection()
            if selection:
                selected_mod = getattr(self, "_mod_rows", {}).get(selection[0])
            view_start = self.mod_tree.yview()[0]
        except (AttributeError, IndexError, tk.TclError):
            pass
        for item in self.mod_tree.get_children(): self.mod_tree.delete(item)
        self._mod_rows = {}
        if not self.pack_info: return
        selected_iid = None
        for row_index, m in enumerate(self._get_sorted_mods()):
            category_key = f"category.{m.category}"
            localized_category = self._t(category_key) if category_key in TRANSLATIONS["zh_CN"] else m.category
            cd = localized_category
            if m.disabled:
                cd = f"[{self._t('status.disabled')}] {cd}"
            status_keys = {
                "found": ("status.found", "found"),
                "not_found": ("status.not_found", "not_found"),
                "preserved": ("status.preserved", "found"),
                "pending": ("status.pending", "pending"),
                "passthrough": ("status.passthrough", "found"),
            }
            status_key, tag = status_keys.get(m.status, ("status.unknown", "pending"))
            st = self._t(status_key)
            if m.status == "warning":
                tag = "warning"
                if m.note == "仅 Beta/Alpha 版" or not m.note:
                    st = self._t("status.warning_beta_alpha")
                else:
                    release_match = re.fullmatch(r"仅 (.+) 版", m.note)
                    st = self._t(
                        "status.warning_release_type",
                        release_type=release_match.group(1),
                    ) if release_match else m.note
            if m.excluded:
                st, tag = self._t("status.excluded"), "excluded"
            if m.disabled and m.status == "pending": tag = "disabled"
            sd = m.source.upper()[:2] if m.source else "?"
            iid = f"mod-{row_index}"
            self._mod_rows[iid] = m
            if m is selected_mod:
                selected_iid = iid
            action = self._t("action.restore" if m.excluded else "action.exclude")
            self.mod_tree.insert(
                "",
                tk.END,
                iid=iid,
                values=(self._display_mod_name(m), sd, cd, st, action),
                tags=(tag,),
            )
        try:
            self.mod_tree.yview_moveto(view_start)
            if selected_iid is not None:
                self.mod_tree.selection_set(selected_iid)
                self.mod_tree.see(selected_iid)
        except (AttributeError, tk.TclError):
            pass

    def _apply_curseforge_result(self, mod: ModInfo, result: dict, project_id: int):
        target_file_id = str(result.get("id", ""))
        target_download_url = result.get("downloadUrl", "") or ""
        if target_file_id and not target_download_url:
            target_download_url = self.cf_api.get_download_url(project_id, int(target_file_id))

        mod.status = "found"; mod.source = "curseforge"; mod.project_id = str(project_id)
        mod.target_version_id = ""
        mod.target_file_id = target_file_id
        mod.target_file_name = result.get("fileName", "")
        mod.target_file_size = result.get("fileLength", 0)
        mod.target_hashes = extract_curseforge_hashes(result)
        mod.target_dependencies = normalize_curseforge_dependencies(result)
        mod.dependency_metadata_available = "dependencies" in result
        mod.name = result.get("displayName") or mod.name
        mod.target_download_url = target_download_url
        if result.get("releaseType", 1) != 1:
            mod.status = "warning"; mod.note = "仅 Beta/Alpha 版"

    @staticmethod
    def _apply_modrinth_result(mod: ModInfo, result: dict) -> bool:
        mod.target_file_id = ""
        mod.target_version_id = result.get("id", "")
        mod.target_dependencies = list(result.get("dependencies", []) or [])
        mod.dependency_metadata_available = "dependencies" in result
        primary = select_usable_primary_file(result.get("files", []) or [])
        if not primary:
            mod.status = "not_found"; mod.note = "目标版本缺少可用主文件"
            return False
        mod.status = "found"; mod.source = "modrinth"
        mod.target_file_name = primary.get("filename", "")
        mod.target_download_url = primary.get("url", "")
        mod.target_file_size = primary.get("size", 0)
        mod.target_hashes = primary.get("hashes", {})
        if result.get("version_type", "release") != "release":
            mod.status = "warning"; mod.note = f"仅 {result.get('version_type')} 版"
        return True

    @staticmethod
    def _same_content_environment(info: ModpackInfo, target_mc: str, target_loader: str) -> bool:
        return (
            info.mc_version.strip() == target_mc.strip()
            and normalize_loader_name(info.loader_type) == normalize_loader_name(target_loader)
        )

    @staticmethod
    def _preserve_original_reference(mod: ModInfo) -> bool:
        if not mod.original_entry:
            return False
        mod.preserve_original = True
        mod.status = "preserved"
        mod.note = "目标环境未变化，保留原文件"
        mod.target_file_name = mod.file_name
        mod.target_download_url = mod.download_url
        mod.target_file_size = mod.file_size
        mod.target_hashes = dict(mod.hashes)
        mod.target_version_id = mod.version_id
        return True

    @staticmethod
    def _curseforge_file_matches_source(mod: ModInfo, file_data: dict) -> bool:
        if not mod.file_id.isdigit() or str(file_data.get("id", "")) != mod.file_id:
            return False
        if not str(file_data.get("modId", "")).isdigit():
            return False
        source_names: set[str] = set()
        downloads = list((mod.original_entry or {}).get("downloads", []) or [])
        if mod.download_url and mod.download_url not in downloads:
            downloads.append(mod.download_url)
        for url in downloads:
            if parse_curseforge_file_id([url]) != mod.file_id:
                continue
            try:
                source_name = unquote(PurePosixPath(urlparse(str(url)).path).name)
            except (TypeError, ValueError):
                continue
            if source_name:
                source_names.add(source_name.casefold())
        returned_name = str(file_data.get("fileName", "") or "")
        if source_names and returned_name and returned_name.casefold() not in source_names:
            return False
        try:
            returned_size = int(file_data.get("fileLength", 0) or 0)
            source_size = int(mod.file_size or 0)
        except (TypeError, ValueError):
            return False
        if source_size and returned_size and source_size != returned_size:
            return False
        returned_hashes = extract_curseforge_hashes(file_data)
        for algorithm in set(mod.hashes) & set(returned_hashes):
            if str(mod.hashes[algorithm]).casefold() != str(returned_hashes[algorithm]).casefold():
                return False
        return True

    def _resolve_exact_curseforge_target(
        self,
        mod: ModInfo,
        target_mc: str,
        expected_loader: str,
        strict_mc: bool,
        curseforge_source_files: dict[int, dict],
    ) -> tuple[bool, str]:
        if not mod.file_id or not mod.file_id.isdigit():
            return False, ""
        source_file = curseforge_source_files.get(int(mod.file_id))
        if not source_file or not self._curseforge_file_matches_source(mod, source_file):
            return False, "无法验证原 CurseForge 文件身份"
        project_id = int(source_file["modId"])
        try:
            result = self.cf_api.find_target_file(
                project_id, target_mc, expected_loader, strict_mc=strict_mc)
        except APINotFoundError:
            result = None
        if not result:
            return False, "原 CurseForge 项目没有目标版本"

        self._apply_curseforge_result(mod, result, project_id)
        mod.name = source_file.get("displayName") or source_file.get("fileName") or mod.name
        try:
            project = self.cf_api.get_mod(project_id)
            mod.cf_slug = project.get("slug", "")
        except APIRequestError as exc:
            Logger.log("warn", f"无法补充 CurseForge 项目信息 {project_id}: {exc}")
        return True, ""

    def _resolve_modrinth_target(
        self,
        mod: ModInfo,
        target_mc: str,
        target_loader: str,
        curseforge_source_files: dict[int, dict],
    ) -> bool:
        strict_mc = mod.category == "mod"
        expected_loader = target_loader if strict_mc else ""
        identity_failures: list[str] = []
        attempted_modrinth_projects: set[str] = set()

        if mod.source.casefold() == "curseforge" and mod.project_id.isdigit():
            mod.identity_locked = True
            project_id = int(mod.project_id)
            try:
                result = self.cf_api.find_target_file(
                    project_id, target_mc, expected_loader, strict_mc=strict_mc)
            except APINotFoundError:
                result = None
            if result:
                self._apply_curseforge_result(mod, result, project_id)
                try:
                    project = self.cf_api.get_mod(project_id)
                    mod.name = project.get("name") or mod.name
                    mod.cf_slug = project.get("slug", "")
                except APIRequestError as exc:
                    Logger.log("warn", f"无法补充 CurseForge 项目信息 {project_id}: {exc}")
                return True
            mod.status = "not_found"
            mod.note = "原 CurseForge 项目没有目标版本"
            return False

        if mod.project_id:
            mod.identity_locked = True
            attempted_modrinth_projects.add(mod.project_id)
            try:
                result = self.mr_api.find_target_version(
                    mod.project_id, target_mc, expected_loader, strict_mc=strict_mc)
            except APINotFoundError:
                result = None
            if result and select_usable_primary_file(result.get("files", []) or []):
                if self._apply_modrinth_result(mod, result):
                    try:
                        project = self.mr_api.get_project(mod.project_id)
                        mod.name = project.get("title", mod.name); mod.mr_slug = project.get("slug", "")
                    except APIRequestError as exc:
                        Logger.log("warn", f"无法补充项目名称 {mod.project_id}: {exc}")
                    return True
            identity_failures.append("原 Modrinth 项目没有可用的目标版本")

        if mod.file_id:
            mod.identity_locked = True
            found, failure = self._resolve_exact_curseforge_target(
                mod,
                target_mc,
                expected_loader,
                strict_mc,
                curseforge_source_files,
            )
            if found:
                return True
            if failure:
                identity_failures.append(failure)

        for algorithm, hash_value in (("sha1", mod.hashes.get("sha1")), ("sha512", mod.hashes.get("sha512"))):
            if not hash_value:
                continue
            version_file = self.mr_api.lookup_by_hash(hash_value, algorithm)
            if not version_file:
                continue
            project_id = str(version_file.get("project_id", "") or "")
            if not project_id or project_id in attempted_modrinth_projects:
                continue
            attempted_modrinth_projects.add(project_id)
            mod.identity_locked = True
            try:
                result = self.mr_api.find_target_version(
                    project_id, target_mc, expected_loader, strict_mc=strict_mc)
            except APINotFoundError:
                result = None
            if not result or not select_usable_primary_file(result.get("files", []) or []):
                identity_failures.append("哈希对应项目没有可用的目标版本")
                continue
            mod.project_id = project_id; mod.source = "modrinth"
            if not mod.original_project_id:
                mod.original_project_id = project_id; mod.original_source = "modrinth"
            if not self._apply_modrinth_result(mod, result):
                identity_failures.append("哈希对应项目没有可用的目标版本")
                continue
            try:
                project = self.mr_api.get_project(project_id)
                mod.name = project.get("title", mod.name); mod.mr_slug = project.get("slug", "")
            except APIRequestError as exc:
                Logger.log("warn", f"无法补充项目名称 {project_id}: {exc}")
            return True

        if identity_failures:
            mod.status = "not_found"
            mod.note = "；".join(dict.fromkeys(identity_failures))
            return False

        queries = extract_search_queries(mod.file_name)
        project_type = {"resourcepack": "resourcepack", "shaderpack": "shader"}.get(mod.category, "mod")
        search_results: list[dict] = []
        for query in queries:
            _check_cancelled(self.cancel_event)
            if len(query) < 2:
                continue
            search_results.extend(
                self.mr_api.search_project(
                    query,
                    expected_loader or None,
                    project_type,
                )
            )
        ranked = _rank_search_candidates(
            search_results,
            mod.file_name,
            queries[0] if queries else "",
            "title",
        )
        compatible_matches: list[_CandidateMatch] = []
        compatible_versions: dict[str, dict] = {}
        for match in ranked[:MAX_VERIFIED_SEARCH_CANDIDATES]:
            project_id = str(match.result.get("project_id", "") or "")
            if not project_id:
                continue
            try:
                candidate_version = self.mr_api.find_target_version(
                    project_id, target_mc, expected_loader, strict_mc=strict_mc)
            except APINotFoundError:
                continue
            if not candidate_version or not select_usable_primary_file(
                candidate_version.get("files", []) or []
            ):
                continue
            compatible_matches.append(match)
            compatible_versions[project_id] = candidate_version
        selected_match = _pick_unambiguous_match(compatible_matches)
        if not selected_match:
            mod.status = "not_found"; mod.note = "没有高置信度的平台身份匹配"
            return False
        best = selected_match.result
        project_id = str(best.get("project_id", "") or "")
        if not project_id:
            mod.status = "not_found"; mod.note = "搜索结果缺少项目 ID"
            return False
        result = compatible_versions[project_id]
        if not result or not self._apply_modrinth_result(mod, result):
            mod.status = "not_found"; mod.note = "候选项目没有目标版本"
            return False
        mod.project_id = project_id
        mod.name = best.get("title", mod.name); mod.mr_slug = best.get("slug", "")
        Logger.log("info", f"  高置信度搜索匹配: {mod.file_name} -> {mod.name} ({project_id})")
        return True

    def _find_targets(self, prompt_on_errors: bool = True, pending_only: bool = False):
        if not self.pack_info: messagebox.showerror(self._t("dialog.error"), self._t("dialog.parse_pack_first")); return
        tmc = self.target_mc.get().strip(); tl = self.target_loader_type.get().strip()
        if not self._target_settings_complete():
            messagebox.showerror(self._t("dialog.target_incomplete_title"), self._t("dialog.target_incomplete")); return
        self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
        self._lookup_prompt_on_errors = prompt_on_errors
        self._clear_compatibility_display_key("runtime.searching_targets", "#2563EB")
        if not pending_only:
            self._reset_target_results()
        fmt = self.pack_info.format_type
        active_mods = [
            mod for mod in self.pack_info.mods
            if not mod.excluded and not mod.passthrough
            and (not pending_only or mod.status == "pending")
        ]
        self._set_working(True); self._set_status_key("runtime.searching_api", "blue")
        self.progress["maximum"] = len(active_mods); self.progress["value"] = 0

        def task():
            nfc = 0
            same_environment = self._same_content_environment(self.pack_info, tmc, tl)
            curseforge_source_files: dict[int, dict] = {}
            if fmt == "modrinth" and not same_environment:
                source_file_ids = {
                    int(mod.file_id)
                    for mod in active_mods
                    if mod.file_id.isdigit()
                }
                if source_file_ids:
                    try:
                        curseforge_source_files = self.cf_api.get_files_by_ids(source_file_ids)
                        Logger.log(
                            "info",
                            f"ForgeCDN 强身份解析: {len(curseforge_source_files)}/{len(source_file_ids)} 个文件",
                        )
                    except APIRequestError as exc:
                        Logger.log("error", f"ForgeCDN 强身份批量解析失败，检查已中止: {exc}")
                        self._schedule(
                            0,
                            lambda msg=str(exc): self._on_error(
                                f"CurseForge 身份解析失败，兼容性检查已中止：{msg}",
                                "error.identity",
                            ),
                        )
                        return
            for i, mod in enumerate(active_mods):
                _check_cancelled(self.cancel_event)
                try:
                    if same_environment and self._preserve_original_reference(mod):
                        Logger.log("info", f"目标环境未变化，保留原条目: {mod.file_name or mod.name}")
                    elif fmt == "curseforge":
                        project_id = int(mod.project_id)
                        project = self.cf_api.get_mod(project_id) or {}
                        if project:
                            mod.name = project.get("name") or mod.name
                            mod.cf_slug = project.get("slug", "")
                        try:
                            class_id = int(project.get("classId"))
                        except (TypeError, ValueError):
                            class_id = 0
                        category = {6: "mod", 12: "resourcepack", 6552: "shaderpack"}.get(class_id)
                        if not category:
                            mod.category = "other"; mod.status = "passthrough"; mod.passthrough = True
                            passthrough_entry = dict(mod.original_entry) if mod.original_entry else {
                                "projectID": project_id,
                                "fileID": int(mod.file_id) if mod.file_id.isdigit() else mod.file_id,
                                "required": mod.required,
                            }
                            if passthrough_entry not in self.pack_info.passthrough_files:
                                self.pack_info.passthrough_files.append(passthrough_entry)
                            Logger.log("info", f"原样保留不支持迁移的 CF 类别: {mod.name} classId={class_id}")
                        else:
                            mod.category = category
                            strict_mc = mod.category == "mod"
                            expected_loader = tl if strict_mc else ""
                            result = self.cf_api.find_target_file(
                                project_id, tmc, expected_loader, strict_mc=strict_mc)
                            if result:
                                self._apply_curseforge_result(mod, result, project_id)
                            else: mod.status = "not_found"; nfc += 1
                    elif fmt == "modrinth":
                        if not self._resolve_modrinth_target(
                            mod, tmc, tl, curseforge_source_files):
                            nfc += 1
                except OperationCancelled:
                    return
                except APINotFoundError as e2:
                    Logger.log("warn", f"  项目不存在 {mod.name}: {e2}")
                    mod.status = "not_found"; mod.note = "平台项目或版本不存在"; nfc += 1
                except APIRequestError as e2:
                    Logger.log("error", f"API 查找批次中止: {e2}")
                    self._schedule(
                        0,
                        lambda msg=str(e2): self._on_error(
                            f"兼容性检查已中止：{msg}", "error.lookup"
                        ),
                    )
                    return
                except Exception as e2:
                    Logger.log("error", f"  异常 {mod.name}: {traceback.format_exc()}")
                    mod.status = "not_found"; mod.note = f"错误: {str(e2)[:50]}"; nfc += 1
                self._schedule(0, lambda v=i+1: self.progress.configure(value=v))
            _check_cancelled(self.cancel_event)
            self._schedule(0, lambda: self._on_find_done(nfc, fmt, active_mods))
        self._start_worker(task)

    def _on_find_done(
        self,
        nfc: int,
        fmt: str,
        processed_mods: list[ModInfo] | None = None,
    ):
        self._refresh_mod_tree()
        processed_mods = processed_mods if processed_mods is not None else [
            mod for mod in self.pack_info.mods if not mod.excluded and not mod.passthrough
        ]
        total = len(processed_mods)
        found = total - nfc
        self._set_status_key("runtime.lookup_done", "green" if nfc == 0 else "orange", found=found, total=total, missing=nfc)
        Logger.log("info", f"查找完成: found={found} not_found={nfc}")
        if nfc > 0 and fmt == "modrinth":
            nfm = [
                mod for mod in processed_mods
                if not mod.excluded and mod.status == "not_found" and not mod.identity_locked
            ]
            if not nfm:
                self._run_compatibility_analysis(
                    prompt_on_errors=getattr(self, "_lookup_prompt_on_errors", True))
                return
            self._set_status_key("runtime.starting_cf", "blue")
            self._do_cf_fallback(nfm)
        else:
            self._run_compatibility_analysis(
                prompt_on_errors=getattr(self, "_lookup_prompt_on_errors", True))

    # ================================================================
    # CF 回退 - 自动化，不再弹窗确认
    # ================================================================
    def _do_cf_fallback(self, nfm: list[ModInfo]):
        self._set_working(True); self._set_status_key("runtime.searching_cf", "blue")
        self.progress["maximum"] = len(nfm); self.progress["value"] = 0
        target_mc = self.target_mc.get().strip(); target_loader = self.target_loader_type.get().strip()

        def task():
            snf = 0
            for i, mod in enumerate(nfm):
                _check_cancelled(self.cancel_event)
                try:
                    strict_mc = (mod.category == "mod")
                    Logger.log("info", f"CF 自动回退: {mod.file_name} (strict={strict_mc})")
                    queries = generate_cf_search_queries(mod.file_name)
                    Logger.log("info", f"  CF 候选词: {queries}")
                    search_results: list[dict] = []
                    for query in queries:
                        _check_cancelled(self.cancel_event)
                        if len(query) >= 2:
                            search_results.extend(
                                self.cf_api.search_mods(
                                    query, limit=CF_SEARCH_LIMIT, category=mod.category)
                            )
                    ranked = _rank_search_candidates(
                        search_results,
                        mod.file_name,
                        queries[0] if queries else "",
                        "name",
                    )
                    found = False
                    compatible_matches: list[_CandidateMatch] = []
                    compatible_files: dict[int, dict] = {}
                    expected_loader = target_loader if strict_mc else ""
                    for match in ranked[:MAX_VERIFIED_SEARCH_CANDIDATES]:
                        if not str(match.result.get("id", "")).isdigit():
                            continue
                        project_id = int(match.result["id"])
                        try:
                            target_file = self.cf_api.find_target_file(
                                project_id,
                                target_mc,
                                expected_loader,
                                strict_mc=strict_mc,
                            )
                        except APINotFoundError:
                            continue
                        if not target_file:
                            continue
                        compatible_matches.append(match)
                        compatible_files[project_id] = target_file
                    selected_match = _pick_unambiguous_match(compatible_matches)
                    if selected_match and str(selected_match.result.get("id", "")).isdigit():
                        best = selected_match.result
                        project_id = int(best["id"])
                        self._apply_curseforge_result(
                            mod, compatible_files[project_id], project_id)
                        mod.name = best.get("name", mod.name); mod.cf_slug = best.get("slug", "")
                        found = True
                    if not found:
                        mod.status = "not_found"; mod.note = "没有高置信度的 CurseForge 匹配"
                        snf += 1; Logger.log("warn", f"  CF 搜索失败: {mod.file_name}")
                except OperationCancelled:
                    return
                except APINotFoundError as e2:
                    Logger.log("warn", f"  CF 项目不存在 {mod.name}: {e2}"); snf += 1
                except APIRequestError as e2:
                    Logger.log("error", f"CF 回退批次中止: {e2}")
                    self._schedule(
                        0,
                        lambda msg=str(e2): self._on_error(
                            f"CurseForge 回退已中止：{msg}", "error.cf_fallback"
                        ),
                    )
                    return
                except Exception as e2:
                    Logger.log("error", f"  CF 回退异常 {mod.name}: {e2}"); snf += 1
                self._schedule(0, lambda v=i+1: self.progress.configure(value=v))
            _check_cancelled(self.cancel_event)
            self._schedule(0, lambda: self._on_cf_fallback_done(snf))
        self._start_worker(task)

    def _on_cf_fallback_done(self, snf: int):
        self._refresh_mod_tree(); self._update_info()
        unresolved = sum(
            not mod.excluded and mod.status == "not_found"
            for mod in self.pack_info.mods
        )
        if unresolved > 0:
            self._set_status_key("runtime.cf_done", "orange", missing=unresolved)
        else:
            self._set_status_key("runtime.all_search_done", "green")
        self._run_compatibility_analysis(
            prompt_on_errors=getattr(self, "_lookup_prompt_on_errors", True))

    # ================================================================
    # 构建
    # ================================================================
    def _build_pack(self):
        if (
            getattr(self, "working", False)
            or getattr(self, "_build_entry_active", False)
            or getattr(self, "_build_resume_pending", False)
        ):
            return
        self._build_entry_active = True
        try:
            self._build_pack_once()
        finally:
            self._build_entry_active = False

    def _build_pack_once(self):
        if not self.pack_info: messagebox.showerror(self._t("dialog.error"), self._t("dialog.parse_pack_first")); return
        if self.analysis_target_snapshot != self._target_snapshot():
            self.analysis_ready = False; self.compatibility_report = None; self.analysis_target_snapshot = None
            self._build_after_resolution = False
            self._build_resume_pending = False
            self._resolution_skips = set()
            self._clear_compatibility_display_key("runtime.target_changed", "#A96000")
            self.notebook.select(0)
            messagebox.showerror(self._t("dialog.cannot_build"), self._t("dialog.target_changed")); return
        if not self.compatibility_report or not self.analysis_ready:
            self.notebook.select(0)
            messagebox.showerror(self._t("dialog.cannot_build"), self._t("dialog.check_first")); return
        if self.compatibility_report.has_errors:
            self._resolve_compatibility_errors(build_after=True)
            return
        output = self._get_output_path()
        if not output: messagebox.showerror(self._t("dialog.error"), self._t("dialog.output_required")); return
        input_variable = getattr(self, "input_path", None)
        input_path = input_variable.get().strip() if input_variable is not None else ""
        parsed_input_path = getattr(self, "parsed_input_path", "") or input_path
        if parsed_input_path and not paths_refer_to_same_location(parsed_input_path, input_path):
            messagebox.showerror(
                self._t("dialog.cannot_build"),
                self._t("dialog.input_changed"),
            )
            return
        if paths_refer_to_same_location(output, parsed_input_path):
            messagebox.showerror(
                self._t("dialog.cannot_build"),
                self._t("dialog.same_output"),
            )
            return
        tmc = self.target_mc.get().strip(); tlt = self.target_loader_type.get().strip()
        tlv = self.target_loader_version.get().strip()
        if not tmc or not tlt or not tlv:
            messagebox.showerror(self._t("dialog.error"), self._t("dialog.target_empty")); return
        overwrite = os.path.exists(output)
        if overwrite and not messagebox.askyesno(self._t("dialog.overwrite_title"), self._t("dialog.overwrite", path=output)):
            return
        self._set_working(True); self._set_status_key("runtime.building", "blue")
        self.progress["maximum"] = sum(not mod.excluded for mod in self.pack_info.mods); self.progress["value"] = 0
        fmt = self.pack_info.format_type
        pack_name = self.output_filename.get().strip() or self.pack_info.raw_data.get("name", "Migrated Modpack")
        pack_info = self.pack_info; overrides_dir = self.temp_overrides_dir
        download_mods = self.download_mods.get()
        def task():
            try:
                if fmt == "curseforge":
                    build_result = PackBuilder.build_curseforge(
                        output, pack_info, tmc, tlt, tlv, overrides_dir,
                        download_mods=download_mods, pack_name=pack_name, overwrite=overwrite,
                        cancel_event=self.cancel_event)
                else:
                    build_result = PackBuilder.build_modrinth(
                        output, pack_info, tmc, tlt, tlv, overrides_dir,
                        download_mods=download_mods, pack_name=pack_name, overwrite=overwrite,
                        cancel_event=self.cancel_event)
                _check_cancelled(self.cancel_event)
                self._schedule(0, lambda r=build_result, o=output: self._on_build_done(r, o))
            except OperationCancelled:
                return
            except Exception as e:
                err_msg = str(e)
                Logger.log("error", f"构建失败: {traceback.format_exc()}")
                self._schedule(0, lambda msg=err_msg: self._on_error(msg, "error.build"))
        self._start_worker(task)

    def _localize_build_message(self, message: str) -> str:
        text = str(message or "")
        if self._current_language() == "zh_CN":
            return text
        warning_suffixes = (
            ("：目标路径与 overrides 现有文件同名，已保留原文件并使用联网安装引用。", "build.warning.cf_override_collision"),
            ("：目标路径与 overrides 现有文件同名，已保留原文件和联网安装引用。", "build.warning.mr_override_collision"),
            ("：下载失败，已回退为 CurseForge 联网安装引用。", "build.warning.cf_download_fallback"),
            ("：平台未提供下载地址，已保留 CurseForge 联网安装引用。", "build.warning.cf_no_download"),
            ("：为保留 Modrinth env 作用域，已保留联网安装引用。", "build.warning.mr_env_reference"),
            ("：下载失败，已回退为 Modrinth 联网安装引用。", "build.warning.mr_download_fallback"),
            ("：目标下载失败，已保留旧禁用版本。", "build.warning.disabled_download_preserved"),
            ("：未找到目标版本，已保留旧禁用版本。", "build.warning.disabled_no_target_preserved"),
        )
        for suffix, key in warning_suffixes:
            if text.endswith(suffix):
                name = text[:-len(suffix)]
                if name.startswith("[禁用] "):
                    name = name[len("[禁用] "):]
                return self._t(key, name=name)
        if text.startswith("[禁用] "):
            return self._t("build.disabled_item", name=text[len("[禁用] "):])
        item_match = re.fullmatch(
            r"(?P<name>.+) \[(?P<category>[^\]]+)\](?P<reason>.*)",
            text,
        )
        if not item_match:
            return text
        category = item_match.group("category")
        category_key = f"category.{category}"
        localized_category = self._t(category_key) if category_key in TRANSLATIONS["zh_CN"] else category
        item = self._t(
            "build.item",
            name=item_match.group("name"),
            category=localized_category,
        )
        reason = item_match.group("reason")
        if reason == "（与 overrides 现有文件同名，未覆盖原文件）":
            return self._t("build.reason.override_collision", item=item)
        if reason == "（无法保留 env 作用域）":
            return self._t("build.reason.env_scope", item=item)
        return item

    def _on_build_done(self, result: BuildResult, op: str):
        self._set_working(False); self.progress["value"] = self.progress["maximum"]
        has_notes = bool(result.missing_files or result.warnings)
        self._set_status_key("runtime.build_done_notes" if has_notes else "runtime.build_done", "orange" if has_notes else "green")
        msg = f"{self._t('build.success_notice')}\n\n{self._t('build.location', path=op)}"
        if result.missing_files:
            msg += f"\n\n{self._t('build.missing', count=len(result.missing_files))}\n"
            msg += "\n".join(
                f"  - {self._localize_build_message(name)}"
                for name in result.missing_files[:20]
            )
            if len(result.missing_files) > 20:
                msg += f"\n  {self._t('build.more_items', count=len(result.missing_files)-20)}"
        if result.warnings:
            msg += f"\n\n{self._t('build.notes', count=len(result.warnings))}\n"
            msg += "\n".join(
                f"  - {self._localize_build_message(warning)}"
                for warning in result.warnings[:20]
            )
            if len(result.warnings) > 20:
                msg += f"\n  {self._t('build.more_notes', count=len(result.warnings)-20)}"
        messagebox.showinfo(self._t("dialog.complete"), msg); Logger.log("info", "构建完成")
    def _on_error(self, msg: str, context_key: str = ""):
        self._build_after_resolution = False
        self._build_entry_active = False
        self._build_resume_pending = False
        self._resolution_skips = set()
        self.progress.stop(); self.progress.configure(mode="determinate")
        self._set_working(False); self._set_status_key("runtime.error", "red")
        display_message = msg
        if self._current_language() != "zh_CN":
            if context_key:
                display_message = self._t(context_key)
            elif re.search(r"[\u4e00-\u9fff]", str(msg)):
                display_message = self._t("error.generic")
        messagebox.showerror(self._t("dialog.error"), display_message)
    def run(self):
        if not getattr(self, "user_agreement_accepted", False):
            if not self._show_user_agreement(require_acceptance=True):
                self._on_close(save_config=False)
                return
        self._show_page("home")
        self.root.deiconify()
        self.root.lift()
        self.root.mainloop()


if __name__ == "__main__":
    app = App()
    app.run()
