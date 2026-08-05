# -*- mode: python ; coding: utf-8 -*-
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files


# PyInstaller exposes the current spec path through SPEC. Resolve every input
# from it so the project can be built from any checkout location.
PROJECT_ROOT = Path(SPEC).resolve().parents[2]
MODULE_DIR = PROJECT_ROOT / '程序模块'
RESOURCE_DIR = PROJECT_ROOT / '资源'
VERSION_INFO = PROJECT_ROOT / '依赖' / '打包配置' / 'version_info.txt'

datas = [
    (str(MODULE_DIR / 'compatibility_analyzer.py'), '程序模块'),
    (str(RESOURCE_DIR / 'mc_pack_migrator_logo.png'), '资源'),
]
datas += collect_data_files('certifi')

a = Analysis(
    [str(PROJECT_ROOT / 'MC整合包工具.py')],
    pathex=[str(MODULE_DIR)],
    binaries=[],
    datas=datas,
    hiddenimports=['compatibility_analyzer'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='MC整合包工具',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    version=str(VERSION_INFO),
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=[str(RESOURCE_DIR / 'mc_pack_migrator_logo.ico')],
)
