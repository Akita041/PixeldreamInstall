# -*- coding: utf-8 -*-
"""PixeldreamNsis 安装包打包器（PyQt5 界面）。

界面与字段对照《简易封包工具》封包日志（Record.txt）：
布局：顶部固定"封包目录 → 生成文件 → 封包模式"，其余按功能分页。

用法：
  直接运行          —— 打开图形界面
  --no-gui --config x.json —— 命令行打包（配置为界面字段的 JSON）
"""

import argparse
import json
import os
import re
import sys
import tempfile
import threading

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from pdengine import MODES, MODE_GUI, PackError, install_dir_of
from pdengine import build_installer as _engine_build

APP_TITLE = "像素封包工具 v2.4"


# ---------------------------------------------------------------- 资源定位

def project_root():
    if getattr(sys, "frozen", False):
        return sys._MEIPASS
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def resource_dir():
    return os.path.join(project_root(), "resources")


def record_path():
    """制作记录（模板）与 exe 同文件夹。"""
    if getattr(sys, "frozen", False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, "制作记录.json")


def new_record_path():
    """Pick the first unused 制作记录[N].json beside the packer."""
    base = record_path()
    folder = os.path.dirname(base)
    if not os.path.exists(base):
        return base
    n = 2
    while os.path.exists(os.path.join(folder, "制作记录%d.json" % n)):
        n += 1
    return os.path.join(folder, "制作记录%d.json" % n)


def default_output_name(pack_dir):
    base = os.path.basename(os.path.normpath(pack_dir))
    return re.sub(r"[^0-9A-Za-z\u4e00-\u9fff_]", "", base) + ".exe"


# ---------------------------------------------------------------- 默认配置

def default_config():
    return {
        "mode": MODE_GUI,
        "shortcut_x64": "",
        "shortcut_x86": "",
        "shortcut_name": "",
        "startmenu_folder": "",
        "desktop_shortcut": False,
        "title": "",
        "write_uninstall": False,
        "license_path": "",
        "uninstall_icon": "",
        "welcome_img": "",
        "header_img": "",
        "exe_icon": "",
        "install_root": "",
        "require_admin": False,
        "prerun": "",
        "prerun_x64only": True,
        "postrun": "",
        "company": "",
        "file_desc": "",
        "copyright": "",
        "file_version": "",
        "pack_dir": "",
        "output_file": "",
    }


_ACTIVE_RECORD = [None]


def save_config(cfg, target):
    data = json.dumps(cfg, ensure_ascii=False, indent=2)
    try:
        with open(target, "w", encoding="utf-8") as f:
            f.write(data)
    except OSError as exc:
        raise PackError("安装包已生成，但制作记录保存失败：%s" % exc)


# ---------------------------------------------------------------- 构建核心

def build_installer(cfg, log, progress=None):
    """执行一次完整打包（自研引擎，逐文件压缩进度），返回输出 exe 路径。"""
    out = _engine_build(cfg, log, progress)
    target = _ACTIVE_RECORD[0] or new_record_path()
    save_config(cfg, target)
    _ACTIVE_RECORD[0] = target
    return out


# ---------------------------------------------------------------- 图形界面

def run_gui(cfg):
    """PyQt5 界面；异常落盘便于诊断（无控制台窗口）。"""
    import traceback
    try:
        _run_gui_qt(cfg)
    except Exception:
        err = traceback.format_exc()
        try:
            with open(os.path.join(tempfile.gettempdir(), "pdnsis_gui.log"),
                      "a", encoding="utf-8") as f:
                f.write(err + "\n")
        except OSError:
            pass
        raise


def _run_gui_qt(cfg):
    from PyQt5.QtCore import Qt, QObject, pyqtSignal
    from PyQt5.QtGui import QFont, QIcon
    from PyQt5.QtWidgets import (QApplication, QCheckBox, QComboBox, QFileDialog,
                                 QFormLayout, QHBoxLayout, QLabel, QMainWindow,
                                 QLineEdit, QMessageBox, QPlainTextEdit,
                                 QProgressBar, QPushButton, QTabWidget,
                                 QVBoxLayout, QWidget)

    class _MainWin(QMainWindow):
        """QMainWindow 子类：正确覆盖 closeEvent（实例属性赋值在 PyQt5 不可靠）。"""

        def __init__(self, on_close):
            super().__init__()
            self._on_close = on_close

        def closeEvent(self, e):
            self._on_close(e)

    QApplication.setAttribute(Qt.AA_EnableHighDpiScaling, True)
    app = QApplication.instance() or QApplication(sys.argv)
    app.setStyle("windowsvista")

    win = _MainWin(lambda e: None)
    win.setWindowTitle(APP_TITLE)
    screen = app.primaryScreen()
    available = screen.availableGeometry() if screen else None
    if available is not None:
        min_w = min(900, max(480, available.width() - 32))
        min_h = min(600, max(320, available.height() - 32))
        width = min(1400, max(min_w, int(available.width() * 0.78)))
        height = min(1000, max(min_h, int(available.height() * 0.82)))
        win.setMinimumSize(min_w, min_h)
        win.resize(width, height)
        frame = win.frameGeometry()
        frame.moveCenter(available.center())
        win.move(frame.topLeft())
    else:
        win.resize(1000, 760)
        win.setMinimumSize(900, 600)
    icon_file = os.path.join(resource_dir(), "图标.ico")
    if os.path.isfile(icon_file):
        win.setWindowIcon(QIcon(icon_file))

    central = QWidget()
    win.setCentralWidget(central)
    root = QVBoxLayout(central)
    root.setContentsMargins(10, 10, 10, 8)
    root.setSpacing(8)

    edits, checks, combos = {}, {}, {}

    def value_of(key):
        if key in checks:
            return checks[key].isChecked()
        if key in combos:
            return combos[key].currentText()
        return edits[key].text().strip()

    def set_value(key, v):
        if key in checks:
            checks[key].setChecked(bool(v))
        elif key in combos:
            combos[key].setCurrentText(str(v))
        else:
            edits[key].setText(str(v or ""))

    def collect():
        return {k: value_of(k) for k in list(edits) + list(checks) + list(combos)}

    def path_row(key, label, mode="file", filetypes=None, tip=None,
                 placeholder=None, allowed_exts=None):
        edit = QLineEdit(str(cfg.get(key, "") or ""))
        if placeholder:
            edit.setPlaceholderText(placeholder)
        edits[key] = edit
        btn = QPushButton("浏览…")
        btn.setFixedWidth(64)

        def browse():
            pack = edits["pack_dir"].text().strip()
            if mode == "dir":
                p = QFileDialog.getExistingDirectory(win, label,
                                                     edit.text() or pack or "")
            elif mode == "save":
                p, _ = QFileDialog.getSaveFileName(
                    win, label, edit.text() or "installer.exe",
                    "可执行文件 (*.exe)")
            elif mode == "packfile":
                start = os.path.join(pack, edit.text()) \
                    if pack else (edit.text() or "")
                p, _ = QFileDialog.getOpenFileName(win, label, start)
                if p:
                    full = os.path.normpath(p)
                    base = os.path.normpath(pack) if pack else ""
                    if base and full.lower().startswith(base.lower() + os.sep):
                        edit.setText(full[len(base) + 1:])
                        return
                    QMessageBox.warning(win, "提示",
                                        "请选择封包目录内的文件")
                    return
            else:
                ft = filetypes or [("所有文件", "*.*")]
                filt = ";;".join("%s (%s)" % (n, e) for n, e in ft)
                p, _ = QFileDialog.getOpenFileName(win, label,
                                                   edit.text() or "", filt)
            if p:
                if allowed_exts and os.path.splitext(p)[1].lower() not in allowed_exts:
                    QMessageBox.warning(win, "格式不支持",
                                        "该项仅支持：" + ", ".join(allowed_exts))
                    return
                edit.setText(os.path.normpath(p))
                if key == "pack_dir":
                    on_pack_dir_committed(edit.text())

        btn.clicked.connect(browse)
        box = QWidget()
        lay = QHBoxLayout(box)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(6)
        lay.addWidget(edit, 1)
        lay.addWidget(btn)
        if tip:
            box.setToolTip(tip)
        return box

    def text_row(key, label, tip=None, placeholder=None):
        edit = QLineEdit(str(cfg.get(key, "") or ""))
        if placeholder:
            edit.setPlaceholderText(placeholder)
        edits[key] = edit
        if tip:
            edit.setToolTip(tip)
        return edit

    def check_row(key, label, tip=None):
        cb = QCheckBox(label)
        cb.setChecked(bool(cfg.get(key, False)))
        checks[key] = cb
        if tip:
            cb.setToolTip(tip)
        return cb

    # ---- 顶部一行：封包目录 / 生成文件 / 封包模式 ----
    topbar = QWidget()
    tlay = QHBoxLayout(topbar)
    tlay.setContentsMargins(0, 0, 0, 0)
    tlay.setSpacing(6)

    def top_label(text, tip=None):
        lb = QLabel(text)
        if text.endswith("*"):
            lb.setText(text[:-1] + '<span style="color:#d93025">*</span>')
            lb.setTextFormat(Qt.RichText)
        lb.setFixedWidth(70)
        lb.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        if tip:
            lb.setToolTip(tip)
        return lb

    tlay.addWidget(top_label("封包目录*", "必填：要打包成安装包的文件夹"))
    tlay.addWidget(path_row("pack_dir", "选择封包目录", mode="dir",
                            tip="要打包成安装包的文件夹（整个目录内容都会被打进去）"), 3)
    tlay.addWidget(top_label("生成文件*", "必填：输出的安装器 exe 路径"))
    tlay.addWidget(path_row("output_file", "生成文件", mode="save",
                            tip="输出的安装器 exe 路径；留空则按封包目录名自动生成"), 3)
    tlay.addWidget(top_label("模式", "界面模式=欢迎页+协议页+完成页；静默模式=无界面直接安装"))
    mcombo = QComboBox()
    mcombo.addItems(MODES)
    mcombo.setCurrentText(str(cfg.get("mode", MODE_GUI)))
    combos["mode"] = mcombo
    mcombo.setFixedWidth(86)
    tlay.addWidget(mcombo)
    root.addWidget(topbar)

    tabs = QTabWidget()
    root.addWidget(tabs, 1)

    # 页：安装设置
    page1 = QWidget()
    f1 = QFormLayout(page1)
    f1.setLabelAlignment(Qt.AlignRight)
    title_label = QLabel('自定标题<span style="color:#d93025">*</span>')
    title_label.setTextFormat(Qt.RichText)
    f1.addRow(title_label, text_row("title", "自定标题",
                                   placeholder="安装器与卸载面板显示的名称"))
    f1.addRow("", check_row("write_uninstall",
                            "写入卸载面板（控制面板“应用和功能”可见）"))
    f1.addRow("安装协议", path_row("license_path", "选择安装协议", mode="file",
                                   filetypes=[("文本文件", "*.txt"), ("所有文件", "*.*")]))
    root_label = QLabel('解压/运行位置<span style="color:#d93025">*</span>')
    root_label.setTextFormat(Qt.RichText)
    f1.addRow(root_label, text_row("install_root", "解压/运行位置",
                                        tip="如 C:\\ —— 封包文件夹会建在该位置下（<位置>\\<封包目录名>）"))
    f1.addRow("", check_row("require_admin",
                            "需要管理员权限（写 C:\\ 根与 HKLM 卸载面板时勾选）"))
    preview = QLabel("")
    preview.setStyleSheet("color:#0a7d32;")
    f1.addRow("", preview)
    tabs.addTab(page1, "安装设置")

    # 页：快捷方式
    page2 = QWidget()
    f2 = QFormLayout(page2)
    f2.setLabelAlignment(Qt.AlignRight)
    f2.addRow("64位快捷方式文件",
              path_row("shortcut_x64", "64位快捷方式文件", mode="packfile",
                       tip="64 位系统上快捷方式指向的文件（封包目录内相对路径）"))
    f2.addRow("32位快捷方式文件",
              path_row("shortcut_x86", "32位快捷方式文件", mode="packfile",
                       tip="32 位系统上快捷方式指向的文件（封包目录内相对路径）"))
    f2.addRow("自定快捷方式名", text_row("shortcut_name", "自定快捷方式名"))
    f2.addRow("开始文件夹名称", text_row("startmenu_folder", "开始文件夹名称"))
    f2.addRow("", check_row("desktop_shortcut", "创建桌面快捷方式"))
    tabs.addTab(page2, "快捷方式")

    # 页：图片与图标
    page3 = QWidget()
    f3 = QFormLayout(page3)
    f3.setLabelAlignment(Qt.AlignRight)
    f3.addRow("卸载图标（ICO）", path_row("uninstall_icon", "选择卸载图标", mode="file",
                                           filetypes=[("ICO 图标", "*.ico")],
                                           allowed_exts=[".ico"],
                                           tip="仅支持 ICO 格式"))
    f3.addRow("左侧图片（164×314，BMP）",
              path_row("welcome_img", "选择左侧 BMP 图片", mode="file",
                       filetypes=[("BMP 位图", "*.bmp")],
                       allowed_exts=[".bmp"],
                       tip="仅支持 BMP；建议尺寸 164×314 像素"))
    f3.addRow("顶部右图（150×57，BMP）",
              path_row("header_img", "选择顶部 BMP 图片", mode="file",
                       filetypes=[("BMP 位图", "*.bmp")],
                       allowed_exts=[".bmp"],
                       tip="仅支持 BMP；建议尺寸 150×57 像素"))
    f3.addRow("安装包图标（ICO）", path_row("exe_icon", "选择安装包 ICO 图标", mode="file",
                                            filetypes=[("ICO 图标", "*.ico")],
                                            allowed_exts=[".ico"],
                                            tip="仅支持 ICO 格式"))
    tabs.addTab(page3, "图片与图标")

    # 页：运行程序
    page4 = QWidget()
    f4 = QFormLayout(page4)
    f4.setLabelAlignment(Qt.AlignRight)
    f4.addRow("安装前运行",
              path_row("prerun", "选择安装前运行的程序", mode="packfile",
                       tip="相对封包目录的路径，如 java21.exe；先解到临时目录运行，完成后才开始解压文件"))
    f4.addRow("", check_row("prerun_x64only", "仅在64系统运行（安装前程序）"))
    f4.addRow("卸载时运行",
              path_row("postrun", "选择卸载时运行的程序", mode="packfile",
                       tip="卸载时先运行；留空则完成页只提供运行主程序"))
    tabs.addTab(page4, "运行程序")

    # 页：版本信息
    page5 = QWidget()
    f5 = QFormLayout(page5)
    f5.setLabelAlignment(Qt.AlignRight)
    f5.addRow("公司名称", text_row("company", "公司名称"))
    f5.addRow("文件说明", text_row("file_desc", "文件说明"))
    f5.addRow("版权信息", text_row("copyright", "版权信息"))
    f5.addRow("文件版本", text_row("file_version", "文件版本",
                                   tip="格式 x.x.x.x（自动规范成 4 段数字）"))
    tabs.addTab(page5, "版本信息")

    # ---- 底部：操作 + 日志 ----
    btns = QHBoxLayout()
    btn_pack = QPushButton("开始封包")
    btn_pack.setDefault(True)
    btn_pack.setFixedHeight(30)
    f = btn_pack.font()
    f.setBold(True)
    btn_pack.setFont(f)
    btn_open = QPushButton("打开输出目录")
    btn_open.setEnabled(False)
    btn_load = QPushButton("加载制作记录")
    btns.addWidget(btn_pack)
    btns.addWidget(btn_open)
    btns.addStretch(1)
    btns.addWidget(btn_load)
    root.addLayout(btns)

    pbar = QProgressBar()
    pbar.setRange(0, 100)
    pbar.setValue(0)
    pbar.setFormat("待机")
    pbar.setTextVisible(True)

    root.addWidget(pbar)

    logbox = QPlainTextEdit()
    logbox.setReadOnly(True)
    logbox.setMaximumBlockCount(5000)
    logbox.setFont(QFont("Consolas", 9))
    logbox.setStyleSheet("QPlainTextEdit{background:#101010;color:#d8d8d8;}")
    root.addWidget(logbox, 1)
    logbox.appendPlainText("选择封包目录后会自动填写建议的生成文件名。")

    state = {"building": False, "last_output": None, "auto_output": None,
             "last_pack_dir": None}

    class _Bridge(QObject):
        line = pyqtSignal(str)
        prog = pyqtSignal(int, str)
        done = pyqtSignal(bool)

    br = _Bridge()
    br.line.connect(lambda m: logbox.appendPlainText(m))

    def update_preview():
        try:
            if edits["pack_dir"].text().strip():
                preview.setText("将安装到：" + install_dir_of(collect()))
            else:
                preview.setText("")
        except Exception:
            preview.setText("")

    def on_pack_dir_changed(text):
        pd = text.strip()
        if pd and os.path.isdir(pd):
            out = edits["output_file"].text().strip()
            if not out or out == state.get("auto_output"):
                auto = os.path.join(os.path.dirname(os.path.normpath(pd)),
                                    default_output_name(pd))
                state["auto_output"] = auto
                edits["output_file"].setText(auto)
        update_preview()

    def on_pack_dir_committed(text):
        pd = os.path.abspath(os.path.normpath((text or "").strip()))
        if not os.path.isdir(pd):
            return
        current = os.path.normcase(pd)
        previous = state.get("last_pack_dir")
        if current != previous:
            for key in ("shortcut_x64", "shortcut_x86", "prerun", "postrun"):
                rel = edits[key].text().strip()
                if not rel:
                    continue
                candidate = os.path.abspath(os.path.join(pd, rel))
                try:
                    inside = os.path.normcase(os.path.commonpath([pd, candidate])) == current
                except ValueError:
                    inside = False
                if not inside or not os.path.isfile(candidate):
                    edits[key].clear()
            state["last_pack_dir"] = current

    edits["pack_dir"].textChanged.connect(on_pack_dir_changed)
    edits["pack_dir"].editingFinished.connect(
        lambda: on_pack_dir_committed(edits["pack_dir"].text()))
    edits["install_root"].textChanged.connect(lambda _: update_preview())
    edits["title"].textChanged.connect(lambda _: update_preview())

    def on_prog(pct, text):
        if pbar.format() == "待机":
            pbar.setFormat("%p%")
        pbar.setValue(max(0, min(100, pct)))
        if text:
            logbox.appendPlainText(text)

    def on_prog_safe(pct, text):
        br.prog.emit(int(pct), str(text or ""))

    br.prog.connect(on_prog)

    def do_build():
        if state["building"]:
            return
        cfg_now = collect()
        if not cfg_now["pack_dir"] or not os.path.isdir(cfg_now["pack_dir"]):
            QMessageBox.critical(win, "错误", "请选择有效的封包目录")
            return
        if not cfg_now["output_file"]:
            QMessageBox.critical(win, "错误", "请指定生成文件")
            return
        if not cfg_now.get("title", "").strip():
            QMessageBox.critical(win, "错误", "请填写自定标题")
            return
        if not cfg_now.get("install_root", "").strip():
            QMessageBox.critical(win, "错误", "请填写解压/运行位置")
            return
        state["building"] = True
        btn_pack.setEnabled(False)
        btn_open.setEnabled(False)
        pbar.setValue(0)

        def work():
            try:
                out = build_installer(cfg_now, br.line.emit, on_prog_safe)
                br.line.emit("✔ 封包成功")
                state["last_output"] = out
                br.done.emit(True)
            except PackError as e:
                br.line.emit("✘ 失败：%s" % e)
                br.done.emit(False)
            except Exception as e:  # noqa: BLE001
                import traceback
                br.line.emit("✘ 未预期的错误：%s" % e)
                for ln in traceback.format_exc().splitlines()[-6:]:
                    br.line.emit("    " + ln)
                br.done.emit(False)

        threading.Thread(target=work, daemon=True).start()

    def on_done(ok):
        state["building"] = False
        btn_pack.setEnabled(True)
        btn_open.setEnabled(ok)
        if ok:
            pbar.setFormat("封包完成")
            out = state.get("last_output")
            mb = QMessageBox(win)
            mb.setWindowTitle("封包完成")
            mb.setIcon(QMessageBox.Information)
            mb.setText("封包完成：\n" + (out or ""))
            open_btn = mb.addButton("打开输出文件夹", QMessageBox.AcceptRole)
            mb.addButton("确定", QMessageBox.RejectRole)
            mb.exec_()
            if mb.clickedButton() is open_btn and out and os.path.isfile(out):
                os.startfile(os.path.dirname(out))  # noqa: S606
        else:
            pbar.setFormat("失败，详见日志")
            QMessageBox.warning(win, "封包失败", "详见日志窗口。")

    def open_out():
        out = state.get("last_output")
        if out and os.path.isfile(out):
            os.startfile(os.path.dirname(out))  # noqa: S606

    def load_record():
        if state["building"]:
            return
        start = record_path()
        p, _ = QFileDialog.getOpenFileName(
            win, "加载制作记录", start,
            "制作记录 JSON (*.json);;所有文件 (*.*)")
        if not p:
            return
        try:
            with open(p, "r", encoding="utf-8-sig") as f:
                saved = json.load(f)
            if not isinstance(saved, dict):
                raise ValueError("制作记录格式应为 JSON 对象")
            for k in list(edits) + list(checks) + list(combos):
                if k in saved:
                    set_value(k, saved[k])
            on_pack_dir_committed(edits["pack_dir"].text())
            _ACTIVE_RECORD[0] = p
            update_preview()
            br.line.emit("已加载制作记录：" + p)
        except (OSError, ValueError) as e:
            br.line.emit("加载制作记录失败：%r" % e)

    br.done.connect(on_done)
    btn_pack.clicked.connect(do_build)
    btn_open.clicked.connect(open_out)
    btn_load.clicked.connect(load_record)

    def on_close(_e):
        if state["building"]:
            r = QMessageBox.question(
                win, "退出", "正在封包，确定退出？",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if r != QMessageBox.Yes:
                _e.ignore()
                return
        _e.accept()
    win._on_close = on_close

    win.closeEvent = on_close
    update_preview()
    win.show()
    app.exec_()


# ---------------------------------------------------------------- 命令行

def run_cli(args):
    def log(m):
        print(m, flush=True)
        try:
            with open(os.path.join(tempfile.gettempdir(), "pdnsis_cli.log"),
                      "a", encoding="utf-8") as f:
                f.write(m + "\n")
        except OSError:
            pass

    try:
        with open(args.config, "r", encoding="utf-8-sig") as f:
            cfg = json.load(f)
        base = default_config()
        for k in base:
            cfg.setdefault(k, base[k])
        cfg["_keep_workdir"] = args.keep

        def prog(p, t):
            log("[%3d%%] %s" % (p, t or ""))

        out = build_installer(cfg, log, prog)
        print("OK %s" % out)
        return 0
    except PackError as e:
        log("FAIL %s" % e)
        return 1
    except Exception:
        import traceback
        log("FAIL traceback:\n" + traceback.format_exc())
        return 1


def main():
    ap = argparse.ArgumentParser(description=APP_TITLE)
    ap.add_argument("--no-gui", action="store_true", help="命令行打包")
    ap.add_argument("--config", help="配置 JSON（界面字段同名键）")
    ap.add_argument("--keep", action="store_true", help="保留构建临时目录")
    args = ap.parse_args()
    if args.no_gui:
        if not args.config:
            ap.error("--no-gui 需要 --config")
        sys.exit(run_cli(args))
    run_gui(default_config())


if __name__ == "__main__":
    main()
