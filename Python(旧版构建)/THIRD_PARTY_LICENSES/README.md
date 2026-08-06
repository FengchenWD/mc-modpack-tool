# Third-Party License Bundle

This directory contains license and notice files copied from the exact Python 3.13.14 release environment used to build `MC整合包工具.exe` for `v1.0.0-beta.1`.

- `Python-3.13.14-LICENSE.txt` contains the Python Software Foundation license history.
- `Python-3.13.14-bundled-licenses.html` is Python's complete bundled-component license page, including OpenSSL, expat, libffi, zlib, bzip2 and other standard-library dependencies.
- `Tcl-Tk-8.6-LICENSE.txt` covers the Tcl/Tk runtime shipped with the Windows build.
- Package-specific files preserve the exact license or notice text installed by the pinned wheels in `依赖/requirements-release.txt`.
- Files prefixed with `setuptools-vendor-` cover dependencies vendored inside the pinned setuptools wheel and collected by the executable build.

The component/version/license index and upstream project links are maintained in [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md). These files do not change the license of the MC Modpack Tool's original source code or of any third-party component.
