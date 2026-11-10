# -*- coding: utf-8 -*-
"""PixeldreamNsis 自研安装器与封包引擎。

产物 = 单文件安装器 exe（C#/.NET Framework 4.x WinForms，Win10/11 自带运行库）：
  [编译的 C# 安装器][payload: manifest + 素材 + 逐文件 deflate 数据][12B 尾部]
- 封包过程逐文件压缩，通过 progress(0-100, 文本) 实时回报；
- 安装器带同风格向导（欢迎页/协议页/进度页/完成页），解压同样有进度条；
- 支持 /S 静默、/D=路径 覆盖安装目录、/U 卸载。

payload 布局：
  [u32 manifest 长度][manifest 文本 UTF-8][素材区][压缩数据区]
manifest 行格式（'|' 分隔，偏移一律 %016d 定宽 → 文本长度与数值无关）：
  K|key|value      A|name|payload相对偏移|长度      D|相对目录
  F|相对路径|payload相对偏移|压缩后长度|原始大小|m(1=deflate 0=store)
尾部：[u64 payload 起始偏移][u32 magic "PDIX"]
"""

import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

from pack_support import (MODES, MODE_GUI, MODE_SILENT, PackError,
                           dir_size_bytes, install_dir_of, norm_version,
                           prepare_assets, sanitize_rel)

STORE_EXT = {
    ".zip", ".jar", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".mp3", ".ogg",
    ".wav", ".mp4", ".webm", ".7z", ".rar", ".gz", ".xz", ".zst", ".lz", ".bz2",
    ".apk", ".ipa", ".iso", ".ttf", ".otf", ".woff", ".woff2", ".bak",
}

CSC_CANDIDATES = [
    r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    r"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe",
]

MAGIC = 0x58494450  # "PDIX"


def _csc():
    for p in CSC_CANDIDATES:
        if os.path.isfile(p):
            return p
    raise PackError("未找到 .NET Framework 编译器 csc.exe（所有 Win10/11 应自带）")


def _here():
    return os.path.dirname(os.path.abspath(__file__))


def _resource_path(name):
    root = (sys._MEIPASS if getattr(sys, "frozen", False)
            else os.path.dirname(_here()))
    return os.path.join(root, "resources", name)


def _extract_exe_icon(exe_path, workdir, log):
    """Extract an executable's associated icon to an ICO using .NET Framework."""
    if not exe_path or not os.path.isfile(exe_path):
        return None
    csc = _csc()
    source = os.path.join(workdir, "extract_icon.cs")
    helper = os.path.join(workdir, "extract_icon.exe")
    icon = os.path.join(workdir, "selected_app.ico")
    code = r'''using System;
using System.Drawing;
class ExtractIcon {
    static int Main(string[] args) {
        try {
            Icon icon = Icon.ExtractAssociatedIcon(args[0]);
            if (icon == null) return 2;
            using (System.IO.FileStream output = System.IO.File.Create(args[1]))
                icon.Save(output);
            return 0;
        } catch { return 3; }
    }
}'''
    try:
        with open(source, "w", encoding="utf-8-sig") as f:
            f.write(code)
        proc = subprocess.Popen(
            [csc, "/nologo", "/target:exe", "/out:" + helper,
             "/r:System.dll", "/r:System.Drawing.dll", source],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, cwd=workdir,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        output = proc.communicate()[0].decode("gbk", errors="replace")
        if proc.returncode != 0:
            log("未能编译图标提取工具：%s" % output.strip())
            return None
        proc = subprocess.Popen(
            [helper, exe_path, icon], stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, cwd=workdir,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        proc.communicate()
        if proc.returncode == 0 and os.path.isfile(icon) and os.path.getsize(icon) > 0:
            return icon
    except (OSError, PackError) as e:
        log("读取快捷方式目标图标失败：%s" % e)
    return None


def _installer_icon(cfg, workdir, log):
    selected = cfg.get("exe_icon")
    if selected:
        return selected
    target = sanitize_rel(cfg.get("shortcut_x64") or cfg.get("shortcut_x86"))
    if target:
        extracted = _extract_exe_icon(
            os.path.join(cfg.get("pack_dir") or "", target), workdir, log)
        if extracted:
            log("安装包图标使用快捷方式目标程序的图标")
            return extracted
    fallback = _resource_path("图标.ico")
    if os.path.isfile(fallback):
        log("安装包图标使用封包器默认图标")
        return fallback
    return None


def _cs_escape(s):
    return (s or "").replace("\\", "\\\\").replace('"', '\\"')


_APP_MANIFEST = """<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="PdInstall.app"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
    </windowsSettings>
  </application>
</assembly>
"""


def _compile_installer(workdir, out_name, cfg, icon_path, log, src_name="installer_template.cs", res=None):
    """用 Framework csc 编译安装器/卸载器（C#5 语法），返回 exe 路径。res=[(路径, 资源名)]。"""
    csc = _csc()
    tpl_path = os.path.join(_here(), "templates", src_name)
    try:
        tpl = open(tpl_path, "r", encoding="utf-8").read()
    except OSError as e:
        raise PackError("安装器模板缺失（%s）：%s" % (src_name, e))
    title = (cfg.get("title") or "").strip() or "应用程序"
    company = (cfg.get("company") or "").strip()
    desc = (cfg.get("file_desc") or "").strip()
    copyright_text = (cfg.get("copyright") or "").strip()
    ver = norm_version(cfg.get("file_version")) or "1.0.0.0"
    cs = (tpl.replace("@TITLE@", _cs_escape(title))
             .replace("@COMPANY@", _cs_escape(company))
             .replace("@DESC@", _cs_escape(desc))
             .replace("@COPYRIGHT@", _cs_escape(copyright_text))
             .replace("@VERSION@", ver))
    cs_path = os.path.join(workdir, "src_" + out_name + ".cs")
    with open(cs_path, "w", encoding="utf-8-sig") as f:
        f.write(cs)
    mf_path = os.path.join(workdir, "app.manifest")
    with open(mf_path, "w", encoding="utf-8") as f:
        f.write(_APP_MANIFEST)
    out = os.path.join(workdir, out_name)
    cmd = [csc, "/nologo", "/target:winexe", "/optimize+",
           "/out:" + out,
           "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll",
           "/r:System.Windows.Forms.dll", "/r:System.IO.Compression.dll",
           "/r:System.Management.dll", "/r:Microsoft.CSharp.dll",
           "/win32manifest:" + mf_path]
    if icon_path and os.path.isfile(icon_path):
        cmd.append("/win32icon:" + icon_path)
    for rpath, rname in (res or []):
        if rpath and os.path.isfile(rpath):
            # 用相对 workdir 的简单文件名，避免引号/空格/中文路径问题
            cmd.append("/res:%s,%s" % (os.path.basename(rpath), rname))
    cmd.append(cs_path)
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, cwd=workdir,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    out_text = proc.stdout.read().decode("gbk", errors="replace")
    ret = proc.wait()
    if ret != 0 or not os.path.isfile(out):
        for line in out_text.splitlines():
            if line.strip():
                log("csc: " + line.strip())
        raise PackError("安装器编译失败（csc exit=%d）" % ret)
    return out


def _enum_pack(pack_dir):
    """返回 (dirs 相对列表, files [(rel, full, size)], 总字节)。"""
    dirs, files, total = [], [], 0
    pack = os.path.normpath(pack_dir)
    for root, dnames, fnames in os.walk(pack):
        dnames.sort()
        fnames.sort()
        rel_root = os.path.relpath(root, pack)
        for d in dnames:
            rd = "." if rel_root == "." else rel_root
            rel = os.path.normpath(os.path.join(rd, d))
            dirs.append(rel.replace("/", "\\"))
        for f in fnames:
            full = os.path.join(root, f)
            rel = (f if rel_root == "."
                   else os.path.normpath(os.path.join(rel_root, f)))
            try:
                size = os.path.getsize(full)
            except OSError as e:
                raise PackError("无法读取封包文件 %s：%s" % (full, e))
            files.append((rel.replace("/", "\\"), full, size))
            total += size
    return dirs, files, total


def build_installer(cfg, log, progress=None):
    """执行一次完整打包，返回输出 exe 路径；失败抛 PackError。"""
    if progress is None:
        def progress(p, t):
            pass
    if not cfg.get("output_file"):
        raise PackError("生成文件 不能为空")
    if not cfg.get("pack_dir"):
        raise PackError("封包目录 不能为空")

    workdir = tempfile.mkdtemp(prefix="pdnsis_")
    keep = bool(cfg.get("_keep_workdir"))
    try:
        log("＝＝ 像素封包工具 开始封包 ＝＝")
        assets = prepare_assets(cfg, workdir, log)
        inst_dir = install_dir_of(cfg)
        log("将安装到：%s" % inst_dir)

        title = (cfg.get("title") or "").strip() or "应用程序"
        ver = norm_version(cfg.get("file_version")) or "1.0.0.0"
        company = (cfg.get("company") or "").strip()

        progress(3, "正在生成安装器…")
        log("正在编译安装器骨架（.NET Framework csc）…")
        installer_icon = _installer_icon(cfg, workdir, log)
        base = _compile_installer(workdir, "installer.exe", cfg,
                                  installer_icon, log)
        progress(7, "正在生成卸载器…")
        log("正在编译卸载器…")
        uninstall_icon = (cfg.get("uninstall_icon")
                          or _resource_path("卸载.ico"))
        unbase = _compile_installer(workdir, "uninst_base.exe", cfg,
                                    uninstall_icon, log,
                                    src_name="uninstaller_template.cs",
                                    res=[(assets.get("header"),
                                          "PdUninstall.header")])

        progress(9, "正在扫描封包目录…")
        dirs, files, total_raw = _enum_pack(cfg["pack_dir"])
        log("封包内容：%d 个文件 / %d 个目录，共 %.1f MB"
            % (len(files), len(dirs), total_raw / 1048576.0))

        # ---------------- 逐文件压缩（实时进度） ----------------
        archive = os.path.join(workdir, "archive.bin")
        f_entries = []  # (rel, off, clen, size, m)
        bytes_done = 0
        last_pct = 9
        prun = sanitize_rel(cfg.get("prerun"))
        prun_lower = prun.lower() if prun else None
        with open(archive, "wb") as af:
            for i, (rel, full, size) in enumerate(files):
                ext = os.path.splitext(rel)[1].lower()
                if size == 0 or ext in STORE_EXT or rel.lower() == prun_lower:
                    m = 0
                    off = af.tell()
                    with open(full, "rb") as src:
                        shutil.copyfileobj(src, af, 1048576)
                    clen = size
                else:
                    m = 1
                    off = af.tell()
                    co = zlib.compressobj(6, zlib.DEFLATED, -15)
                    with open(full, "rb") as src:
                        while True:
                            chunk = src.read(2097152)
                            if not chunk:
                                break
                            data = co.compress(chunk)
                            if data:
                                af.write(data)
                    af.write(co.flush())
                    clen = af.tell() - off
                f_entries.append((rel, off, clen, size, m))
                bytes_done += size
                pct = 10 + int(72 * bytes_done / total_raw) if total_raw else 82
                if pct != last_pct or i == len(files) - 1:
                    last_pct = pct
                    progress(pct, "正在压缩 %s（%d/%d 个文件）"
                             % (rel, i + 1, len(files)))

        # ---------------- 素材区 ----------------
        def _asset_bytes(path):
            if path and os.path.isfile(path):
                with open(path, "rb") as f:
                    return f.read()
            return b""

        assets_seq = [("welcome", _asset_bytes(assets.get("welcome"))),
                      ("header", _asset_bytes(assets.get("header"))),
                      ("uninst", _asset_bytes(unbase))]
        if assets.get("license"):
            assets_seq.insert(2, ("license", _asset_bytes(assets["license"])))

        # ---------------- manifest（两遍：定宽偏移 → 长度不变） ----------------
        write_uninstall = bool(cfg.get("write_uninstall", True))
        require_admin = bool(cfg.get("require_admin", True))
        un_key = ("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\"
                  + title.replace("\\", " "))
        sname = (cfg.get("shortcut_name") or "").strip()
        smf = (cfg.get("startmenu_folder") or "").strip()
        x64 = sanitize_rel(cfg.get("shortcut_x64"))
        x86 = sanitize_rel(cfg.get("shortcut_x86"))
        prun = sanitize_rel(cfg.get("prerun"))
        postrun = sanitize_rel(cfg.get("postrun"))
        display_icon = (x64 if (sname and x64) else "uninst.exe")

        def make_lines(a_offs, archive_start):
            lines = ["PDMANIFEST1",
                     "K|title|%s" % title,
                     "K|inst_dir|%s" % inst_dir,
                     "K|admin|%d" % (1 if require_admin else 0),
                     "K|allusers|%d" % (1 if require_admin else 0),
                     "K|silent_default|%d"
                     % (1 if cfg.get("mode") == MODE_SILENT else 0),
                     "K|version|%s" % ver,
                     "K|company|%s" % company]
            if smf and sname:
                lines.append("K|sm_folder|%s" % smf)
            if sname:
                lines.append("K|shortcut_name|%s" % sname)
            lines.append("K|desktop|%d"
                         % (1 if cfg.get("desktop_shortcut", True) else 0))
            if sname and x64:
                lines.append("K|x64_target|%s" % x64)
            if sname and x86:
                lines.append("K|x86_target|%s" % x86)
            if prun:
                lines.append("K|prerun|%s" % prun)
                lines.append("K|prerun_x64only|%d"
                             % (1 if cfg.get("prerun_x64only", True) else 0))
            if postrun:
                lines.append("K|postrun|%s" % postrun)
            if write_uninstall:
                lines.append("K|un_key|%s" % un_key)
            lines.append("K|display_icon|%s" % display_icon)
            lines.append("K|est_kb|%d" % (dir_size_bytes(cfg["pack_dir"]) // 1024))
            for d in dirs:
                lines.append("D|%s" % d)
            for name, (off, ln) in a_offs.items():
                lines.append("A|%s|%016d|%016d" % (name, off, ln))
            for rel, off, clen, size, m in f_entries:
                lines.append("F|%s|%016d|%016d|%016d|%d"
                             % (rel, archive_start + off, clen, size, m))
            lines.append("END")
            return "\n".join(lines) + "\n"

        # 第一遍（数值全 0）量出 manifest 字节长度（与数值无关）
        probe = make_lines({n: (0, 0) for n, _ in assets_seq}, 0)
        mlen = 4 + len(probe.encode("utf-8"))
        blob_off = 0
        a_offs = {}
        for name, data in assets_seq:
            a_offs[name] = (mlen + blob_off, len(data))
            blob_off += len(data)
        archive_start = mlen + blob_off
        manifest_bytes = make_lines(a_offs, archive_start).encode("utf-8")
        if len(manifest_bytes) + 4 != mlen:
            # 理论不可达：定宽偏移保证长度一致
            raise PackError("内部错误：manifest 长度计算不一致")

        # ---------------- 组装输出 ----------------
        out_file = cfg["output_file"]
        oparent = os.path.dirname(os.path.normpath(out_file))
        if oparent:
            os.makedirs(oparent, exist_ok=True)
        progress(90, "正在写入安装器…")
        log("组装 payload：manifest %.1f KB + 素材 %.1f MB + 数据 %.1f MB"
            % (len(manifest_bytes) / 1024.0,
               blob_off / 1048576.0,
               os.path.getsize(archive) / 1048576.0))
        base_size = os.path.getsize(base)
        with open(out_file, "wb") as out:
            with open(base, "rb") as bsrc:
                shutil.copyfileobj(bsrc, out, 4194304)
            out.write(struct.pack("<I", len(manifest_bytes)))
            out.write(manifest_bytes)
            for _name, data in assets_seq:
                out.write(data)
            with open(archive, "rb") as asrc:
                shutil.copyfileobj(asrc, out, 4194304)
            out.write(struct.pack("<Q", base_size))
            out.write(struct.pack("<I", MAGIC))
        progress(100, "封包完成")

        size_mb = os.path.getsize(out_file) / 1048576.0
        log("封包完成：%s（%.1f MB）" % (out_file, size_mb))
        return out_file
    finally:
        if not keep:
            shutil.rmtree(workdir, ignore_errors=True)
