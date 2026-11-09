# -*- coding: utf-8 -*-
"""把打包器本体构建为单文件 exe（dist\\像素封包工具.exe）。"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
MAIN = os.path.join(HERE, "pixeldream_packer.py")
RES = os.path.join(ROOT, "resources")
ICON = os.path.join(RES, "图标.ico")
APP = "像素封包工具"


def _ensure_conda_dll_path():
    """Expose Conda's native DLLs to PyInstaller's dependency scanner."""
    candidates = [os.path.join(sys.prefix, "Library", "bin"),
                  os.path.join(sys.prefix, "bin")]
    parts = os.environ.get("PATH", "").split(os.pathsep)
    for path in reversed(candidates):
        if os.path.isdir(path) and path not in parts:
            parts.insert(0, path)
    os.environ["PATH"] = os.pathsep.join(p for p in parts if p)


def main():
    _ensure_conda_dll_path()
    for d in ("build", "dist"):
        os.makedirs(os.path.join(ROOT, d), exist_ok=True)
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm", "--clean",
        "--onefile", "--windowed",
        "--name", APP,
        "--icon", ICON,
        "--add-data", RES + ";resources",
        "--add-data", os.path.join(HERE, "templates", "installer_template.cs") + ";templates",
        "--add-data", os.path.join(HERE, "templates", "uninstaller_template.cs") + ";templates",
        "--distpath", os.path.join(ROOT, "dist"),
        "--workpath", os.path.join(ROOT, "build"),
        "--specpath", os.path.join(ROOT, "build"),
        MAIN,
    ]
    print(" ".join(cmd))
    ret = subprocess.call(cmd)
    if ret == 0:
        out = os.path.join(ROOT, "dist", APP + ".exe")
        print("OK %s (%.1f MB)" % (out, os.path.getsize(out) / 1048576))
    sys.exit(ret)


if __name__ == "__main__":
    main()
