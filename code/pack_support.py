# -*- coding: utf-8 -*-
"""Shared configuration validation and installer asset preparation."""

import os
import re

from PIL import Image


class PackError(Exception):
    """打包过程中的可读错误（面向用户显示）。"""


WELCOME_SIZE = (164, 314)   # welcome page sidebar
HEADER_SIZE = (150, 57)     # header image

MODE_GUI = "界面模式"
MODE_SILENT = "静默模式"
MODES = [MODE_GUI, MODE_SILENT]


# ---------------------------------------------------------------- 素材准备

def read_text_any(path):
    """按常见编码尝试读取文本（协议.txt 原为 GBK）。"""
    with open(path, "rb") as f:
        raw = f.read()
    for enc in ("utf-8-sig", "utf-16", "gb18030"):
        try:
            return raw.decode(enc)
        except (UnicodeDecodeError, UnicodeError):
            continue
    return raw.decode("utf-8", errors="replace")


def fit_bitmap(src, size, dst, bg=(255, 255, 255)):
    """把 BMP 图片缩放到安装界面需要的尺寸（不变形，白底居中）。"""
    im = Image.open(src).convert("RGB")
    if im.size == size:
        canvas = im
    else:
        canvas = Image.new("RGB", size, bg)
        scaled = im.copy()
        scaled.thumbnail(size, Image.LANCZOS)
        x = (size[0] - scaled.size[0]) // 2
        y = (size[1] - scaled.size[1]) // 2
        canvas.paste(scaled, (x, y))
    canvas.save(dst, "BMP")
    return dst


def check_ico(path, label):
    if os.path.splitext(path)[1].lower() != ".ico":
        raise PackError("%s 仅支持 ICO 格式：%s" % (label, path))
    with open(path, "rb") as f:
        head = f.read(4)
    if len(head) < 4 or head[0:2] != b"\x00\x00" or head[2:4] != b"\x01\x00":
        raise PackError("%s 不是有效的 ICO 文件：%s" % (label, path))


def check_bmp(path, label):
    if os.path.splitext(path)[1].lower() != ".bmp":
        raise PackError("%s 仅支持 BMP 格式：%s" % (label, path))
    try:
        with Image.open(path) as image:
            if image.format != "BMP":
                raise PackError("%s 文件内容不是 BMP 位图：%s" % (label, path))
            image.verify()
    except PackError:
        raise
    except Exception as e:
        raise PackError("%s 不是有效的 BMP 位图：%s（%s）" % (label, path, e))


def norm_version(v):
    """文件版本规范成 x.x.x.x（VIProductVersion 要求 4 段数字）。"""
    v = (v or "").strip()
    if not v:
        return None
    parts = re.findall(r"\d+", v)
    if not parts:
        raise PackError("文件版本格式无法识别：%s（应为 x.x.x.x）" % v)
    parts = [str(int(p)) for p in parts[:4]]
    while len(parts) < 4:
        parts.append("0")
    return ".".join(parts)


def sanitize_rel(rel):
    """把用户输入的相对路径规范成反斜杠、去首尾引号。"""
    rel = (rel or "").strip().strip('"')
    rel = rel.replace("/", "\\").lstrip("\\")
    return rel


def install_dir_of(cfg):
    """安装位置 = 解压/运行位置 + 封包目录名（始终自动建封包同名文件夹）。"""
    root = (cfg.get("install_root") or "").strip().strip('"').replace("/", "\\")
    if not root:
        raise PackError("解压/运行位置 不能为空")
    root = root.rstrip("\\")
    if not root:
        raise PackError("解压/运行位置 不能为空")
    name = os.path.basename(os.path.normpath(cfg["pack_dir"]))
    if root.endswith("\\" + name):
        return root  # 用户已填到包名，不重复追加
    if len(root) == 2 and root[1] == ":":
        root += "\\"  # 盘符根 C: -> C:\
    return root + name if root.endswith("\\") else root + "\\" + name


def dir_size_bytes(path):
    total = 0
    for root, _dirs, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def prepare_assets(cfg, workdir, log):
    """校验封包配置并准备安装器使用的协议、位图和图标。"""
    pack_dir = cfg.get("pack_dir") or ""
    if not pack_dir or not os.path.isdir(pack_dir):
        raise PackError("封包目录不存在：%s" % (pack_dir or "(空)"))

    assets = {}

    if cfg.get("license_path"):
        if not os.path.isfile(cfg["license_path"]):
            raise PackError("安装协议文件不存在：%s" % cfg["license_path"])
        lic_out = os.path.join(workdir, "license.txt")
        with open(lic_out, "w", encoding="utf-16", newline="") as f:
            f.write(read_text_any(cfg["license_path"]))
        assets["license"] = lic_out
        log("安装协议已转存为 Unicode 文本：%s" % cfg["license_path"])

    if cfg.get("welcome_img"):
        if not os.path.isfile(cfg["welcome_img"]):
            raise PackError("左侧图片不存在：%s" % cfg["welcome_img"])
        check_bmp(cfg["welcome_img"], "左侧图片")
        assets["welcome"] = fit_bitmap(
            cfg["welcome_img"], WELCOME_SIZE, os.path.join(workdir, "welcome.bmp"))
        log("左侧图片已按 %dx%d 输出" % WELCOME_SIZE)

    if cfg.get("header_img"):
        if not os.path.isfile(cfg["header_img"]):
            raise PackError("顶部右图不存在：%s" % cfg["header_img"])
        check_bmp(cfg["header_img"], "顶部右图")
        assets["header"] = fit_bitmap(
            cfg["header_img"], HEADER_SIZE, os.path.join(workdir, "header.bmp"))
        log("顶部右图已按 %dx%d 输出" % HEADER_SIZE)

    if cfg.get("exe_icon"):
        if not os.path.isfile(cfg["exe_icon"]):
            raise PackError("ICO图标不存在：%s" % cfg["exe_icon"])
        check_ico(cfg["exe_icon"], "ICO图标")

    if cfg.get("uninstall_icon"):
        if not os.path.isfile(cfg["uninstall_icon"]):
            raise PackError("卸载图标不存在：%s" % cfg["uninstall_icon"])
        check_ico(cfg["uninstall_icon"], "卸载图标")

    # 快捷方式目标和自定义运行程序必须存在于封包目录内。
    for key, label in (("shortcut_x64", "64位快捷方式文件"),
                       ("shortcut_x86", "32位快捷方式文件"),
                       ("prerun", "运行前或安装前运行"),
                       ("postrun", "完成后或卸载时运行")):
        rel = sanitize_rel(cfg.get(key))
        if not rel:
            continue
        full = os.path.join(pack_dir, rel)
        if not os.path.isfile(full):
            raise PackError("%s 在封包目录中不存在：%s" % (label, rel))

    if cfg.get("shortcut_x64") and not cfg.get("shortcut_x86"):
        cfg["shortcut_x86"] = cfg["shortcut_x64"]
    if cfg.get("shortcut_x86") and not cfg.get("shortcut_x64"):
        cfg["shortcut_x64"] = cfg["shortcut_x86"]

    return assets


