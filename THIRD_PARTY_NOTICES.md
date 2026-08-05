# 第三方组件说明

本项目的 CC BY-NC-SA 4.0 许可只适用于作者有权许可的原创内容。以下第三方组件保持其各自许可，项目许可不会替代或改变这些许可。

## 运行与单文件 EXE 所含组件

| 组件 | 发布构建版本 | 许可 |
| --- | --- | --- |
| Python | 3.13.14 | Python Software Foundation License 2.0 |
| Tcl/Tk | 随 Python 3.13 分发 | Tcl/Tk BSD-style license |
| Requests | 2.34.2 | Apache-2.0 |
| urllib3 | 2.7.0 | MIT |
| certifi | 2026.7.22 | MPL-2.0 |
| charset-normalizer | 3.4.9 | MIT |
| idna | 3.18 | BSD-3-Clause |

## 构建工具及其依赖

| 组件 | 锁定版本 | 许可 |
| --- | --- | --- |
| PyInstaller | 6.21.0 | GPL-2.0-or-later，带 PyInstaller Bootloader Exception |
| pyinstaller-hooks-contrib | 2026.6 | Apache-2.0 / GPL-2.0 |
| altgraph | 0.17.5 | MIT |
| packaging | 26.2 | Apache-2.0 OR BSD-2-Clause |
| pefile | 2024.8.26 | MIT |
| pywin32-ctypes | 0.2.3 | BSD-3-Clause |
| setuptools | 83.0.0 | MIT |

本次锁定发布环境所安装的完整许可与通知文本保存在 [`THIRD_PARTY_LICENSES`](THIRD_PARTY_LICENSES/) 目录。其中 Python 的完整组件许可页面还包含随 Windows 运行时分发的 OpenSSL、expat、libffi、zlib、bzip2 等组件说明。上游项目链接如下：

- [Python](https://docs.python.org/3/license.html)
- [Tcl/Tk](https://www.tcl-lang.org/software/tcltk/license.html)
- [Requests](https://github.com/psf/requests)
- [urllib3](https://github.com/urllib3/urllib3)
- [certifi](https://github.com/certifi/python-certifi)
- [charset-normalizer](https://github.com/jawah/charset_normalizer)
- [idna](https://github.com/kjd/idna)
- [PyInstaller](https://github.com/pyinstaller/pyinstaller)
- [PyInstaller hooks contrib](https://github.com/pyinstaller/pyinstaller-hooks-contrib)

发布 GitHub Release 时，应让 `LICENSE`、`NOTICE.md`、`USER_AGREEMENT.md`、本文件和 `THIRD_PARTY_LICENSES` 压缩包与 EXE 一同可获取。
