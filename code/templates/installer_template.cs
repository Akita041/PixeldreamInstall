// PixeldreamInstaller — 自研安装器引擎（界面风格对齐 NSIS MUI 简体中文向导）
// 由 PixeldreamNsis 打包器在封包时编译（csc /target:winexe）。
// 配置与文件清单通过附加在 exe 尾部的 payload 传入（见 manifest）。
// 命令行：/S 静默安装；/D=路径 覆盖默认安装目录（须为最后一个参数）。
// 卸载器是独立编译的小程序（uninstaller_template.cs），安装时写入安装目录。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("@TITLE@")]
[assembly: System.Reflection.AssemblyCompany("@COMPANY@")]
[assembly: System.Reflection.AssemblyProduct("@TITLE@")]
[assembly: System.Reflection.AssemblyDescription("@DESC@")]
[assembly: System.Reflection.AssemblyCopyright("@COPYRIGHT@")] 
[assembly: System.Reflection.AssemblyFileVersion("@VERSION@")]
[assembly: System.Reflection.AssemblyVersion("@VERSION@")]

namespace PdInstall
{
    static class Entry
    {

        [STAThread]
        static void Main(string[] args)
        {
            bool silent = HasFlag(args, "S");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Installer.Run(silent);
        }

        static bool HasFlag(string[] args, string f)
        {
            foreach (string a in args)
            {
                string t = a.TrimStart('/', '-');
                if (string.Equals(t, f, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // NSIS 兼容：/D=安装目录，必须是最后一个参数（路径可含空格、不加引号）
        public static string GetDOverride()
        {
            string cl = Environment.CommandLine;
            int i = cl.ToUpperInvariant().LastIndexOf("/D=", StringComparison.Ordinal);
            if (i < 0) i = cl.ToUpperInvariant().LastIndexOf("-D=", StringComparison.Ordinal);
            if (i < 0) return null;
            return cl.Substring(i + 3).Trim().Trim('"');
        }
    }

    static class NativeCaret
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool HideCaret(IntPtr hWnd);

        public static void Hide(Control control)
        {
            try { if (control != null && control.IsHandleCreated)
                HideCaret(control.Handle); } catch { }
        }
    }

    static class SingleInstance
    {
        public static bool TryAcquire(string kind, string path, out Mutex mutex)
        {
            mutex = null;
            try
            {
                string full = Path.GetFullPath(path).ToUpperInvariant();
                byte[] hash;
                using (SHA256 sha = SHA256.Create())
                    hash = sha.ComputeHash(Encoding.UTF8.GetBytes(full));
                var name = new StringBuilder("Local\\PixeldreamNsis_" + kind + "_");
                foreach (byte b in hash) name.Append(b.ToString("X2"));
                mutex = new Mutex(false, name.ToString());
                try
                {
                    bool acquired = mutex.WaitOne(0);
                    if (!acquired) { mutex.Dispose(); mutex = null; }
                    return acquired;
                }
                catch (AbandonedMutexException) { return true; }
            }
            catch
            {
                if (mutex != null) { mutex.Dispose(); mutex = null; }
                return false;
            }
        }
    }

    // ---------------------------------------------------------- payload 读取

    class RangeStream : Stream
    {
        readonly Stream _base; readonly long _start; readonly long _len; long _pos;
        public RangeStream(Stream b, long start, long len)
        { _base = b; _start = start; _len = len; _base.Seek(start, SeekOrigin.Begin); }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _len; } }
        public override long Position { get { return _pos; } set { Seek(value, SeekOrigin.Begin); } }
        public override int Read(byte[] buf, int off, int cnt)
        {
            long remain = _len - _pos;
            if (remain <= 0) return 0;
            if (cnt > remain) cnt = (int)remain;
            int n = _base.Read(buf, off, cnt);
            if (n > 0) _pos += n;
            return n;
        }
        public override long Seek(long off, SeekOrigin o)
        {
            long t = o == SeekOrigin.Begin ? off : (o == SeekOrigin.Current ? _pos + off : _len + off);
            if (t < 0) t = 0;
            if (t > _len) t = _len;
            _pos = t;
            _base.Seek(_start + _pos, SeekOrigin.Begin);
            return _pos;
        }
        public override void Flush() { }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing)
        { if (disposing) _base.Dispose(); base.Dispose(disposing); }
    }

    class Manifest
    {
        public Dictionary<string, string> K = new Dictionary<string, string>();
        public List<string[]> Assets = new List<string[]>();   // name, offset, len
        public List<string> Dirs = new List<string>();
        public List<string[]> Files = new List<string[]>();    // path, offset, clen, size, m
    }

    static class Payload
    {
        public const uint Magic = 0x58494450; // "PDIX"
        public static string SelfPath { get { return Application.ExecutablePath; } }
        public static long PayloadStart;
        public static Manifest M;

        public static void Load()
        {
            using (FileStream fs = new FileStream(SelfPath, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 16) throw new IOException("安装包已损坏（无尾部）");
                fs.Seek(-12, SeekOrigin.End);
                byte[] tb = new byte[12];
                ReadFull(fs, tb, 0, 12);
                long off = BitConverter.ToInt64(tb, 0);
                uint magic = BitConverter.ToUInt32(tb, 8);
                if (magic != Magic) throw new IOException("安装包已损坏（尾部魔数不符）");
                PayloadStart = off;
                fs.Seek(off, SeekOrigin.Begin);
                byte[] lb = new byte[4];
                ReadFull(fs, lb, 0, 4);
                int mlen = BitConverter.ToInt32(lb, 0);
                byte[] mb = new byte[mlen];
                ReadFull(fs, mb, 0, mlen);
                M = Parse(Encoding.UTF8.GetString(mb));
            }
        }

        static void ReadFull(FileStream fs, byte[] buf, int off, int cnt)
        {
            int done = 0;
            while (done < cnt)
            {
                int n = fs.Read(buf, off + done, cnt - done);
                if (n <= 0) throw new IOException("安装包读取中断");
                done += n;
            }
        }

        static Manifest Parse(string text)
        {
            var m = new Manifest();
            foreach (string line0 in text.Split('\n'))
            {
                string line = line0.TrimEnd('\r');
                if (line.Length == 0 || line == "PDMANIFEST1" || line == "END") continue;
                string[] p = line.Split('|');
                if (p[0] == "K" && p.Length >= 3)
                    m.K[p[1]] = line.Substring(p[0].Length + p[1].Length + 2);
                else if (p[0] == "A" && p.Length >= 4) m.Assets.Add(new[] { p[1], p[2], p[3] });
                else if (p[0] == "D" && p.Length >= 2) m.Dirs.Add(p[1]);
                else if (p[0] == "F" && p.Length >= 6)
                    m.Files.Add(new[] { p[1], p[2], p[3], p[4], p[5] });
            }
            return m;
        }

        public static byte[] Asset(string name)
        {
            foreach (string[] a in M.Assets)
                if (a[0] == name)
                    using (Stream s = Open(long.Parse(a[1]), long.Parse(a[2])))
                    using (var ms = new MemoryStream((int)long.Parse(a[2])))
                    { s.CopyTo(ms); return ms.ToArray(); }
            return null;
        }

        public static Stream Open(long relOff, long len)
        {
            var fs = new FileStream(SelfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new RangeStream(fs, PayloadStart + relOff, len);
        }
    }

    // ---------------------------------------------------------- 工具

    static class Util
    {
        public static bool Is64OS { get { return Environment.Is64BitOperatingSystem; } }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    var p = new WindowsPrincipal(id);
                    return p.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static void RelaunchElevated(string args, Action beforeExit)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = Payload.SelfPath;
            psi.Arguments = args;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try
            {
                Process.Start(psi);
            }
            catch (Win32Exception)
            {
                if (beforeExit != null) beforeExit();
                Environment.Exit(2); // 用户拒绝 UAC
            }
            if (beforeExit != null) beforeExit();
            Environment.Exit(0);
        }

        public static void CreateShortcut(string lnk, string target, string workdir)
        {
            object sh = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell"));
            Type t = sh.GetType();
            object sc = t.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, sh,
                new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty,
                null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty,
                null, sc, new object[] { workdir });
            st.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty,
                null, sc, new object[] { 1 });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod,
                null, sc, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh);
        }

        public static void RunWait(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                string wd = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(wd)) psi.WorkingDirectory = wd;
                if (args != null) psi.Arguments = args;
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi)) p.WaitForExit();
            }
            catch { }
        }

        public static string FreeSpaceText(string dir)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                foreach (DriveInfo d in DriveInfo.GetDrives())
                    if (string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase))
                        return (d.AvailableFreeSpace / 1073741824.0).ToString("F1") + " GB 可用";
            }
            catch { }
            return "";
        }
    }

    // ---------------------------------------------------------- 安装

    class Ctx
    {
        public string InstDir;
        public string PackFolder;
        public string Prerun, Postrun;
        public bool Silent;
        public long TotalRaw;
        public string Title;
    }

    static class Installer
    {
        public static Ctx ctx;
        static Mutex instanceMutex;

        public static void Run(bool silent)
        {
            try { Payload.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("安装包损坏：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var m = Payload.M;
            ctx = new Ctx();
            ctx.Title = m.K.ContainsKey("title") ? m.K["title"] : "应用程序";
            ctx.Silent = silent || m.K.ContainsKey("silent_default")
                && m.K["silent_default"] == "1";
            ctx.InstDir = m.K.ContainsKey("inst_dir") ? m.K["inst_dir"] : "";
            ctx.PackFolder = string.IsNullOrEmpty(ctx.InstDir)
                ? "" : System.IO.Path.GetFileName(
                    ctx.InstDir.TrimEnd('\\'));
            string dov = Entry.GetDOverride();
            if (!string.IsNullOrEmpty(dov)) ctx.InstDir = dov;
            ctx.Prerun = m.K.ContainsKey("prerun") ? m.K["prerun"] : null;
            ctx.Postrun = m.K.ContainsKey("postrun") ? m.K["postrun"] : null;
            ctx.TotalRaw = 0;
            foreach (string[] f0 in m.Files)
                ctx.TotalRaw += long.Parse(f0[3]);
            bool needAdmin = m.K.ContainsKey("admin") && m.K["admin"] == "1";
            if (needAdmin && !Util.IsElevated())
            {
                string a = Environment.CommandLine;
                int i = a.IndexOf("\"", 1);
                Util.RelaunchElevated(i > 0 ? a.Substring(i + 1) : "", null);
                return;
            }

            if (!SingleInstance.TryAcquire("Installer", Payload.SelfPath,
                out instanceMutex))
            {
                MessageBox.Show(ctx.Title + "安装程序已运行，请勿重复打开", "提示", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            try
            {
                RunSingleInstance(silent);
            }
            finally
            {
                try { instanceMutex.ReleaseMutex(); } catch { }
                instanceMutex.Dispose();
                instanceMutex = null;
            }
        }

        static void RunSingleInstance(bool silent)
        {

            if (ctx.Silent) { DoSilent(); return; }
            Application.Run(new WizardForm(ctx));
        }

        static void DoSilent()
        {
            try
            {
                ExtractStep(null, null);
                ShortcutAndRegistry(null);
                if (ctx.Postrun != null && File.Exists(
                    Path.Combine(ctx.InstDir, ctx.Postrun)))
                    Util.RunWait(Path.Combine(ctx.InstDir, ctx.Postrun), null);
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }
        }

        public static void PrerunStep(Action<string> fileCb)
        {
            if (ctx.Prerun == null) return;
            bool x64only = Payload.M.K.ContainsKey("prerun_x64only")
                && Payload.M.K["prerun_x64only"] == "1";
            if (x64only && !Util.Is64OS) return;
            string dst = Path.Combine(ctx.InstDir, ctx.Prerun);
            if (!File.Exists(dst))
                throw new FileNotFoundException("安装前运行文件未抽取到安装目录", dst);
            if (fileCb != null) fileCb(dst);
            Util.RunWait(dst, null);
        }

        public static void ExtractStep(Action<long, long, long, long> progress,
            Action<string> fileLog)
        {
            Directory.CreateDirectory(ctx.InstDir);
            foreach (string d in Payload.M.Dirs)
                Directory.CreateDirectory(Path.Combine(ctx.InstDir, d));
            long done = 0, total = ctx.TotalRaw;
            string prerun = ctx.Prerun;
            if (string.IsNullOrEmpty(prerun) ||
                (Payload.M.K.ContainsKey("prerun_x64only") &&
                 Payload.M.K["prerun_x64only"] == "1" && !Util.Is64OS))
                prerun = null;
            string[] prerunEntry = null;
            if (prerun != null)
                foreach (string[] f in Payload.M.Files)
                    if (string.Equals(f[0], prerun, StringComparison.OrdinalIgnoreCase))
                    { prerunEntry = f; break; }
            if (prerunEntry != null)
            {
                string prePath = Path.Combine(ctx.InstDir, prerunEntry[0]);
                string preDir = Path.GetDirectoryName(prePath);
                if (!Directory.Exists(preDir)) Directory.CreateDirectory(preDir);
                if (fileLog != null) fileLog("正在抽取: " + prerunEntry[0]);
                ExtractOne(prerunEntry, prePath, delegate(long fd, long ft)
                {
                    if (progress != null) progress(fd, total, fd, ft);
                });
                done = long.Parse(prerunEntry[3]);
                if (progress != null) progress(done, total, done, done);
                PrerunStep(delegate(string p)
                { if (fileLog != null) fileLog("正在安装: " + p); });
                if (fileLog != null) fileLog("正在抽取: 其余文件");
            }
            foreach (string[] f in Payload.M.Files)
            {
                if (prerunEntry != null && object.ReferenceEquals(f, prerunEntry))
                    continue;
                string dst = Path.Combine(ctx.InstDir, f[0]);
                string dd = Path.GetDirectoryName(dst);
                if (!Directory.Exists(dd)) Directory.CreateDirectory(dd);
                if (fileLog != null) fileLog("正在抽取: " + f[0]);
                ExtractOne(f, dst, delegate(long fd, long ft)
                {
                    if (progress != null) progress(done + fd, total, fd, ft);
                });
                done += long.Parse(f[3]);
                if (progress != null) progress(done, total,
                    long.Parse(f[3]), long.Parse(f[3]));
            }
            if (progress != null) progress(total, total, 0, 0);
        }

        static void ExtractOne(string[] f, string dst, Action<long, long> onProg)
        {
            long clen = long.Parse(f[2]), size = long.Parse(f[3]), m = long.Parse(f[4]);
            long done = 0;
            byte[] buf = new byte[1048576];
            using (Stream raw = Payload.Open(long.Parse(f[1]), clen))
            {
                using (FileStream fs = new FileStream(dst, FileMode.Create,
                    FileAccess.Write, FileShare.None))
                {
                    if (m == 1)
                    {
                        using (var ds = new DeflateStream(raw, CompressionMode.Decompress))
                        {
                            while (true)
                            {
                                int r = ds.Read(buf, 0, buf.Length);
                                if (r <= 0) break;
                                fs.Write(buf, 0, r);
                                done += r;
                                if (onProg != null) onProg(done, size);
                            }
                        }
                    }
                    else
                    {
                        while (true)
                        {
                            int r = raw.Read(buf, 0, buf.Length);
                            if (r <= 0) break;
                            fs.Write(buf, 0, r);
                            done += r;
                            if (onProg != null) onProg(done, size);
                        }
                    }
                }
            }
        }

        // 卸载器（独立小 exe，尾部附带卸载清单）——不再产生 uninst.xml 等可见文件
        static string UninstExeName()
        {
            string t = ctx.Title;
            foreach (char c in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
                t = t.Replace(c, '_');
            return t.Trim() + "卸载程序.exe";
        }

        static void WriteUninstallData(string instDir, bool allusers, string unKey)
        {
            var m = Payload.M;
            var sb = new StringBuilder();
            sb.Append("PDUNINST1\n");
            sb.Append("K|inst_dir|" + instDir + "\n");
            sb.Append("K|un_key|" + unKey + "\n");
            sb.Append("K|allusers|" + (allusers ? "1" : "0") + "\n");
            sb.Append("K|title|" + ctx.Title + "\n");
            if (m.K.ContainsKey("sm_folder"))
                sb.Append("K|sm_folder|" + m.K["sm_folder"] + "\n");
            if (m.K.ContainsKey("shortcut_name"))
                sb.Append("K|shortcut_name|" + m.K["shortcut_name"] + "\n");
            sb.Append("K|desktop|"
                + (m.K.ContainsKey("desktop") ? m.K["desktop"] : "1") + "\n");
            if (m.K.ContainsKey("postrun"))
                sb.Append("K|postrun|" + m.K["postrun"] + "\n");
            foreach (string d in m.Dirs) sb.Append("D|" + d + "\n");
            foreach (string[] f in m.Files) sb.Append("F|" + f[0] + "\n");
            sb.Append("END\n");
            byte[] mb = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] ub = Payload.Asset("uninst");
            if (ub == null) ub = new byte[0];
            string unPath = Path.Combine(instDir, UninstExeName());
            using (FileStream fs = new FileStream(unPath, FileMode.Create,
                FileAccess.Write, FileShare.None))
            {
                fs.Write(ub, 0, ub.Length);
                fs.Write(mb, 0, mb.Length);
                fs.Write(BitConverter.GetBytes(mb.Length), 0, 4);
                fs.Write(BitConverter.GetBytes(0x44554450), 0, 4); // "PDUD"
            }
        }

        static void WriteRegValues(RegistryKey k, string unKey, string ver,
            string company, string displayIcon)
        {
            k.SetValue("DisplayName", ctx.Title);
            if (ver != null) k.SetValue("DisplayVersion", ver);
            if (company != null) k.SetValue("Publisher", company);
            k.SetValue("InstallLocation", ctx.InstDir);
            string up = Path.Combine(ctx.InstDir, UninstExeName());
            k.SetValue("UninstallString", "\"" + up + "\" /U");
            k.SetValue("QuietUninstallString", "\"" + up + "\" /U /S");
            k.SetValue("DisplayIcon", displayIcon);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // 卸载面板注册：优先计划蜂巢，失败自动换另一个并回读校验
        public static void ShortcutAndRegistry(Action<string> log)
        {
            var m = Payload.M;
            bool allusers = m.K.ContainsKey("allusers") && m.K["allusers"] == "1";
            bool desktop = m.K.ContainsKey("desktop") && m.K["desktop"] == "1";
            string sname = m.K.ContainsKey("shortcut_name") ? m.K["shortcut_name"] : null;
            string smf = m.K.ContainsKey("sm_folder") ? m.K["sm_folder"] : null;
            string target = Util.Is64OS
                ? (m.K.ContainsKey("x64_target") ? m.K["x64_target"] : null)
                : (m.K.ContainsKey("x86_target") ? m.K["x86_target"] : null);

            if (sname != null && target != null)
            {
                string tpath = Path.Combine(ctx.InstDir, target);
                if (desktop)
                {
                    bool created = false;
                    string[] desks = allusers
                        ? new[] {
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) }
                        : new[] {
                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
                    foreach (string dd in desks)
                    {
                        try
                        {
                            string lnk = Path.Combine(dd, sname + ".lnk");
                            Util.CreateShortcut(lnk, tpath, ctx.InstDir);
                            if (File.Exists(lnk)) { created = true; break; }
                        }
                        catch { }
                    }
                    if (!created && log != null)
                        log("警告：桌面快捷方式创建失败（已尝试 " + desks.Length + " 个位置）");
                }
                if (smf != null)
                {
                    string base1 = Environment.GetFolderPath(allusers
                        ? Environment.SpecialFolder.CommonStartMenu
                        : Environment.SpecialFolder.StartMenu);
                    string fdir = Path.Combine(Path.Combine(base1, "Programs"), smf);
                    Directory.CreateDirectory(fdir);
                    try
                    {
                        Util.CreateShortcut(Path.Combine(fdir, sname + ".lnk"),
                            tpath, ctx.InstDir);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log("警告：开始菜单快捷方式创建失败：" + ex.Message);
                    }
                    try
                    {
                        Util.CreateShortcut(
                            Path.Combine(fdir, "卸载 " + sname + ".lnk"),
                            Path.Combine(ctx.InstDir, UninstExeName()), ctx.InstDir);
                    }
                    catch { }
                }
            }

            string ver = m.K.ContainsKey("version") ? m.K["version"] : null;
            string company = m.K.ContainsKey("company") ? m.K["company"] : null;
            string unKey = m.K.ContainsKey("un_key")
                ? m.K["un_key"]
                : ("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\"
                   + ctx.Title.Replace("\\", " "));
            string di = m.K.ContainsKey("display_icon") ? m.K["display_icon"] : null;
            string displayIcon = (di != null
                && File.Exists(Path.Combine(ctx.InstDir, di)))
                ? Path.Combine(ctx.InstDir, di)
                : Path.Combine(ctx.InstDir, UninstExeName());

            long kb = 0;
            if (m.K.ContainsKey("est_kb"))
            { try { kb = long.Parse(m.K["est_kb"]); } catch { } }
            if (kb > 2147483647L) kb = 2147483647L;

            bool ok = false;
            bool actualAll = allusers;
            Exception werr = null;
            RegistryKey[] order = allusers
                ? new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser }
                : new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (RegistryKey bk in order)
            {
                try
                {
                    using (RegistryKey k = bk.CreateSubKey(unKey))
                    {
                        WriteRegValues(k, unKey, ver, company, displayIcon);
                        if (kb > 0)
                            k.SetValue("EstimatedSize", (int)kb,
                                RegistryValueKind.DWord);
                    }
                    using (RegistryKey chk = bk.OpenSubKey(unKey))
                        ok = chk != null
                            && string.Equals((string)chk.GetValue("DisplayName"),
                                ctx.Title, StringComparison.Ordinal);
                    if (ok)
                    {
                        actualAll = bk == Registry.LocalMachine;
                        break;
                    }
                }
                catch (Exception ex) { werr = ex; }
            }
            WriteUninstallData(ctx.InstDir, actualAll, unKey);
            if (!ok && log != null)
                log("警告：写入卸载面板失败"
                    + (werr != null ? "：" + werr.Message : ""));
        }

        // ---------------------------------------------------------- 向导界面

        class WizardForm : Form
        {
            Panel content;
            Button btnBack, btnNext, btnCancel, detailToggle;
            PictureBox sidePic;
            int page; // 0 welcome 1 license 2 dir 3 install 4 finish
            TextBox dirEdit;
            Label spaceLabel;
            CheckBox runAfter;
            string finishRunTarget;
            ListView detail;
            Panel barBox, barFill, detailFrame;
            Label curFile, progressPercent;
            readonly Queue<string> pendingDetail = new Queue<string>();
            readonly Queue<string> recentDetails = new Queue<string>();
            readonly Dictionary<string, int> detailProgress =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, ListViewItem> detailProgressRows =
                new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
            readonly object detailLock = new object();
            volatile bool detailExpanded;
            bool detailNeedsRefresh;
            string currentFileStatus;
            int detailLineCount;
            System.Windows.Forms.Timer detailTimer;
            float uiScale;


            class InstallProgress
            {
                public string Status;
                public int Overall;
                public int File;
            }


            public WizardForm(Ctx c)
            {
                ctx = c;
                Text = ctx.Title + " 安装向导";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                uiScale = GetUiScale();
                ClientSize = ScaledSize(496, 332);
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.White;
                DoubleBuffered = true;

                content = new BufferedPanel();
                content.Location = new Point(0, 0);
                content.Size = ScaledSize(496, 292);
                content.BackColor = Color.White;
                content.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                    | AnchorStyles.Left | AnchorStyles.Right;
                Controls.Add(content);
                content.Resize += delegate
                {
                    if (page == 3) LayoutProgressRow();
                };

                btnBack = new Button();
                btnBack.Text = "< 上一步(&B)";
                btnBack.Size = ScaledSize(88, 26);
                btnBack.Location = ScaledPoint(210, 298);
                btnBack.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                btnBack.Enabled = false;
                btnBack.Click += delegate
                {
                    if (page == 2 && Payload.Asset("license") == null) ShowPage(0);
                    else ShowPage(page - 1);
                };
                Controls.Add(btnBack);

                btnNext = new Button();
                btnNext.Text = "下一步(&N) >";
                btnNext.Size = ScaledSize(88, 26);
                btnNext.Location = ScaledPoint(302, 298);
                btnNext.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                btnNext.Click += delegate { OnNext(); };
                Controls.Add(btnNext);

                btnCancel = new Button();
                btnCancel.Text = "取消(&C)";
                btnCancel.Size = ScaledSize(80, 26);
                btnCancel.Location = ScaledPoint(404, 298);
                btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                btnCancel.Click += delegate
                {
                    if (MessageBox.Show("确定要退出安装向导吗？", "退出",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                        == DialogResult.Yes) Close();
                };
                Controls.Add(btnCancel);
                FormClosing += delegate(object s, FormClosingEventArgs e)
                {
                    if (page == 3 && e.CloseReason == CloseReason.UserClosing)
                        e.Cancel = true;
                };
                AcceptButton = btnNext;

                sidePic = new PictureBox();
                sidePic.Dock = DockStyle.Left;
                sidePic.Width = 164;
                sidePic.SizeMode = PictureBoxSizeMode.Zoom;
                byte[] wp = Payload.Asset("welcome");
                if (wp != null)
                    using (var ms = new MemoryStream(wp))
                        sidePic.Image = Image.FromStream(ms);

                ShowPage(0);
            }

            Control HeaderImage()
            {
                byte[] hp = Payload.Asset("header");
                var pb = new PictureBox();
                if (hp != null)
                    using (var ms = new MemoryStream(hp))
                        pb.Image = Image.FromStream(ms);
                pb.Height = 57;
                pb.Width = 150;
                pb.SizeMode = PictureBoxSizeMode.Zoom;
                pb.Location = new Point((int)Math.Round(content.Width / uiScale) - 160, 8);
                pb.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                return pb;
            }

            void AddHeaderImage()
            {
                Control image = HeaderImage();
                content.Controls.Add(image);
                image.SendToBack();
            }

            void ShowPage(int p)
            {
                page = p;
                content.Controls.Clear();
                btnBack.Enabled = page == 1 || page == 2;
                btnNext.Text = page == 2 ? "安装(&I)" : "下一步(&N) >";
                if (page == 0) BuildWelcome();
                else if (page == 1) BuildLicense();
                else if (page == 2) BuildDir();
                else if (page == 3) { btnNext.Enabled = false; BuildInstall(); }
                else BuildFinish();
                if (uiScale != 1f)
                    foreach (Control child in content.Controls)
                        ScaleControlTree(child, uiScale);
                if (page == 3) LayoutProgressRow();
            }

            Label BigLabel(string t)
            {
                var l = new Label();
                l.Text = t;
                l.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
                l.AutoSize = true;
                l.MaximumSize = new Size(320, 0);
                l.Location = new Point(176, 24);
                return l;
            }

            Label BodyLabel(string t, int y, int h)
            {
                var l = new Label();
                l.Text = t;
                l.Font = new Font("Microsoft YaHei UI", 9);
                l.AutoSize = false;
                l.Size = new Size(314, h);
                l.Location = new Point(176, y);
                return l;
            }

            void BuildWelcome()
            {
                Panel image = SideImagePanel();
                content.Controls.Add(image);
                image.SendToBack();
                content.Controls.Add(BigLabel("欢迎使用\n" + ctx.Title + " 安装向导"));
                content.Controls.Add(BodyLabel(
                    "这个向导将指引你完成 " + ctx.Title + " 的安装过程。\n\n" +
                    "请保持设备流畅。\n\n" +
                    "单击“下一步”继续。", 130, 180));
                btnNext.Text = "下一步(&N) >";
                btnNext.Enabled = true;
            }

            Panel SideImagePanel()
            {
                var panel = new Panel();
                panel.Dock = DockStyle.Left;
                panel.Width = 164;
                panel.Padding = new Padding(8, 10, 8, 10);
                panel.BackColor = Color.White;
                sidePic.Dock = DockStyle.Fill;
                sidePic.BackColor = Color.White;
                panel.Controls.Add(sidePic);
                return panel;
            }

            void BuildLicense()
            {
                AddHeaderImage();
                content.Controls.Add(BodyLabel("请仔细阅读下面的协议：", 12, 24));
                var tb = new TextBox();
                tb.Multiline = true;
                tb.ReadOnly = true;
                tb.HideSelection = true;
                tb.WordWrap = true;
                tb.ScrollBars = ScrollBars.Vertical;
                tb.Font = new Font("Microsoft YaHei UI", 9);
                tb.BorderStyle = BorderStyle.None;
                tb.Dock = DockStyle.Fill;
                tb.ContextMenuStrip = CreateTextContextMenu(tb);
                byte[] lp = Payload.Asset("license");
                if (lp != null)
                {
                    string txt;
                    using (var ms = new MemoryStream(lp))
                    using (var sr = new StreamReader(ms, Encoding.UTF8, true))
                        txt = sr.ReadToEnd();
                    tb.Text = txt;
                }
                var licenseFrame = new Panel();
                licenseFrame.Location = new Point(12, 70);
                licenseFrame.Size = new Size(472, 190);
                licenseFrame.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                    | AnchorStyles.Left | AnchorStyles.Right;
                licenseFrame.BorderStyle = BorderStyle.FixedSingle;
                licenseFrame.BackColor = Color.White;
                licenseFrame.Controls.Add(tb);
                content.Controls.Add(licenseFrame);
                tb.GotFocus += delegate { NativeCaret.Hide(tb); };
                tb.MouseUp += delegate { NativeCaret.Hide(tb); };
                var ck = new CheckBox();
                ck.Text = "我同意协议中的条款";
                ck.AutoSize = true;
                ck.Location = new Point(12, 272);
                ck.CheckedChanged += delegate { btnNext.Enabled = ck.Checked; };
                content.Controls.Add(ck);
                btnNext.Text = "下一步(&N) >";
                btnNext.Enabled = ck.Checked;
            }

            void BuildDir()
            {
                AddHeaderImage();
                var lbl = new Label();
                lbl.Text = "选择安装位置";
                lbl.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
                lbl.AutoSize = false;
                lbl.Size = new Size(330, 24);
                lbl.Location = new Point(12, 14);
                content.Controls.Add(lbl);

                var lbl2 = BodyLabel2("安装向导将把 " + ctx.Title + " 安装到下面的位置，"
                    + "并自动创建 " + ctx.PackFolder + " 文件夹。\n\n单击“下一步”继续。", 48, 60);
                content.Controls.Add(lbl2);

                dirEdit = new TextBox();
                dirEdit.Text = ctx.InstDir;
                dirEdit.Location = new Point(12, 130);
                dirEdit.Size = new Size(386, 23);
                dirEdit.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                content.Controls.Add(dirEdit);

                var btnBrowse = new Button();
                btnBrowse.Text = "浏览(&R)…";
                btnBrowse.Size = new Size(86, 23);
                btnBrowse.Location = new Point(402, 129);
                btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                btnBrowse.Click += delegate
                {
                    using (var dlg = new FolderBrowserDialog())
                    {
                        dlg.Description = "选择 " + ctx.Title + " 的安装位置（将在其中创建 "
                            + ctx.PackFolder + " 文件夹）";
                        dlg.SelectedPath = LocationPart(dirEdit.Text);
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                            dirEdit.Text = JoinLocation(dlg.SelectedPath);
                    }
                };
                content.Controls.Add(btnBrowse);

                spaceLabel = new Label();
                spaceLabel.Text = FreeSpace(dirEdit.Text);
                spaceLabel.AutoSize = false;
                spaceLabel.Size = new Size(472, 20);
                spaceLabel.Location = new Point(12, 160);
                spaceLabel.ForeColor = Color.DimGray;
                content.Controls.Add(spaceLabel);
                dirEdit.TextChanged += delegate
                { spaceLabel.Text = FreeSpace(dirEdit.Text); };
            }

            // 位置 = 用户所选目录（封包文件夹名由 OnNext/JoinLocation 统一追加）
            string LocationPart(string text)
            {
                string t = (text ?? "").Trim();
                string pn = ctx.PackFolder ?? "";
                if (pn != "" && t.EndsWith("\\" + pn, StringComparison.OrdinalIgnoreCase))
                    t = t.Substring(0, t.Length - pn.Length - 1);
                return t;
            }

            string JoinLocation(string loc)
            {
                string t = (loc ?? "").Trim().Trim('"').Replace('/', '\\');
                t = t.TrimEnd('\\');
                if (t.Length == 2 && t[1] == ':') t += "\\"; // 盘符根保持 C:\
                string pn = ctx.PackFolder ?? "";
                if (pn == "") return t;
                if (t.EndsWith("\\" + pn, StringComparison.OrdinalIgnoreCase)) return t;
                if (t.EndsWith("\\")) return t + pn; // 根目录已有斜杠直接拼名
                return t + "\\" + pn;
            }

            Label BodyLabel2(string t, int y, int h)
            {
                var l = new Label();
                l.Text = t;
                l.Font = new Font("Microsoft YaHei UI", 9);
                l.AutoSize = false;
                l.Size = new Size(310, h);
                l.Location = new Point(12, y);
                return l;
            }

            string FreeSpace(string dir)
            {
                if (string.IsNullOrWhiteSpace(dir)) return "";
                return dir + "（" + Util.FreeSpaceText(dir) + "）";
            }

            void BuildInstall()
            {
                // Match the standard NSIS MUI install page: no large left sidebar art.
                AddHeaderImage();
                var lbl = new Label();
                lbl.Text = "正在安装 " + ctx.Title;
                lbl.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
                lbl.AutoSize = false;
                lbl.Size = new Size(320, 24);
                lbl.Location = new Point(12, 14);
                content.Controls.Add(lbl);

                curFile = new Label();
                curFile.Text = "准备中…";
                curFile.AutoEllipsis = true;
                curFile.AutoSize = false;
                curFile.Size = new Size(472, 20);
                curFile.Location = new Point(12, 68);
                content.Controls.Add(curFile);

                barBox = new Panel();
                barBox.Location = new Point(12, 92);
                barBox.Size = new Size(444, 18);
                barBox.BorderStyle = BorderStyle.FixedSingle;
                barBox.BackColor = Color.White;
                barBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                content.Controls.Add(barBox);

                progressPercent = new Label();
                progressPercent.Text = "0%";
                progressPercent.TextAlign = ContentAlignment.MiddleCenter;
                progressPercent.Font = new Font("Microsoft YaHei UI", 7f);
                progressPercent.AutoSize = false;
                progressPercent.Size = new Size(30, 20);
                progressPercent.Location = new Point(460, 91);
                progressPercent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                content.Controls.Add(progressPercent);

                barFill = new Panel();
                barFill.BackColor = Color.FromArgb(0, 128, 0);
                barFill.Size = new Size(0, 16);
                barFill.Location = new Point(1, 1);
                barBox.Controls.Add(barFill);

                detail = CreateDetailListView();
                detailFrame = new BufferedPanel();
                detailFrame.Location = new Point(12, 146);
                detailFrame.Size = new Size(472, 138);
                detailFrame.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                    | AnchorStyles.Left | AnchorStyles.Right;
                detailFrame.BorderStyle = BorderStyle.None;
                detailFrame.BackColor = Color.White;
                detailFrame.Controls.Add(detail);
                content.Controls.Add(detailFrame);
                detailFrame.Visible = false;

                detailToggle = new Button();
                detailToggle.Text = "显示细节(&D)";
                detailToggle.Size = new Size(100, 26);
                detailToggle.Location = new Point(12, 116);
                detailToggle.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                detailToggle.Click += delegate
                {
                    bool showDetails;
                    string snapshot = null;
                    lock (detailLock)
                    {
                        detailExpanded = !detailExpanded;
                        showDetails = detailExpanded;
                        detailFrame.Visible = showDetails;
                        detailToggle.Text = showDetails
                            ? "隐藏细节(&D)" : "显示细节(&D)";
                        pendingDetail.Clear();
                        detailNeedsRefresh = false;
                        if (showDetails)
                        {
                            snapshot = RecentDetailText();
                        }
                    }
                    if (showDetails) ReplaceDetailContents(snapshot);
                };
                content.Controls.Add(detailToggle);

                detailTimer = new System.Windows.Forms.Timer();
                detailTimer.Interval = 50;
                detailTimer.Tick += delegate { FlushDetailQueue(); };
                detailTimer.Start();
                btnCancel.Enabled = false; // 安装开始后禁止中途退出

                btnNext.Text = "下一步(&N) >";
                btnNext.Enabled = false;

                var worker = new BackgroundWorker();
                worker.WorkerReportsProgress = true;
                worker.DoWork += delegate(object s, DoWorkEventArgs e)
                {
                    worker.ReportProgress(0, null);
                    int lastOverall = -1;
                    int lastFile = -10;
                    int lastTick = Environment.TickCount;
                    string activeStatus = null;
                    Installer.ExtractStep(delegate(long done, long total,
                        long fileDone, long fileTotal)
                    {
                        int overall = total <= 0 ? 100 : Math.Max(0,
                            Math.Min(100, (int)(done * 100 / total)));
                        int file = fileTotal <= 0 ? 100 : Math.Max(0,
                            Math.Min(100, (int)(fileDone * 100 / fileTotal)));
                        int now = Environment.TickCount;
                        if (overall != lastOverall || file >= lastFile + 5
                            || file == 100 || unchecked(now - lastTick) >= 150)
                        {
                            lastOverall = overall;
                            lastFile = file;
                            lastTick = now;
                            worker.ReportProgress(overall, new InstallProgress
                            {
                                Status = activeStatus,
                                Overall = overall,
                                File = file
                            });
                        }
                    }, delegate(string line)
                    {
                        // Keep every extraction/run entry. The UI timer batches
                        // them to avoid flooding the WinForms message queue.
                        activeStatus = line;
                        lastFile = -10;
                        lastTick = Environment.TickCount;
                        EnqueueDetail(line);
                    });
                    Installer.ShortcutAndRegistry(EnqueueDetail);
                };
                worker.ProgressChanged += delegate(object s,
                    ProgressChangedEventArgs e)
                {
                    if (e.ProgressPercentage >= 0)
                    {
                        SetBar(e.ProgressPercentage);
                        progressPercent.Text = e.ProgressPercentage + "%";
                    }
                    InstallProgress update = e.UserState as InstallProgress;
                    if (update != null && !string.IsNullOrEmpty(update.Status))
                    {
                        lock (detailLock)
                            detailProgress[update.Status] = update.File;
                        UpdateDetailProgress(update.Status, update.File);
                    }
                };
                worker.RunWorkerCompleted += delegate(object s,
                    RunWorkerCompletedEventArgs e)
                {
                    detailTimer.Stop();
                    if (e.Error != null)
                    {
                        MessageBox.Show("安装失败：" + e.Error.Message, "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Close();
                        Environment.Exit(1);
                        return;
                    }
                    ShowPage(4);
                };
                worker.RunWorkerAsync();
            }

            void FlushDetailQueue()
            {
                var batch = new List<string>();
                string snapshot = null;
                string latest = null;
                lock (detailLock)
                {
                    latest = currentFileStatus;
                    currentFileStatus = null;
                    if (!detailExpanded)
                    {
                        pendingDetail.Clear();
                        detailNeedsRefresh = false;
                    }
                    else if (detailNeedsRefresh)
                    {
                        snapshot = RecentDetailText();
                        pendingDetail.Clear();
                        detailNeedsRefresh = false;
                    }
                    else
                    {
                        int count = Math.Min(50, pendingDetail.Count);
                        for (int i = 0; i < count; i++)
                            batch.Add(pendingDetail.Dequeue());
                    }
                }
                if (!string.IsNullOrEmpty(latest)) curFile.Text = latest;
                if (snapshot != null)
                {
                    ReplaceDetailContents(snapshot);

                }
                else if (batch.Count > 0 && detailFrame.Visible)
                {
                    AppendDetailLines(batch);
                    

                }
            }

            void EnqueueDetail(string line)
            {
                lock (detailLock)
                {
                    currentFileStatus = line;
                    if (HasFileProgress(line) && !detailProgress.ContainsKey(line))
                        detailProgress[line] = 0;
                    detailLineCount++;
                    recentDetails.Enqueue(line);
                    while (recentDetails.Count > 2000)
                        detailProgress.Remove(recentDetails.Dequeue());
                    if (detailExpanded)
                    {
                        if (pendingDetail.Count < 2000)
                            pendingDetail.Enqueue(line);
                        else
                        {
                            pendingDetail.Clear();
                            detailNeedsRefresh = true;
                        }
                    }
                }
            }

            string RecentDetailText()
            {
                const int tailCount = 100;
                var lines = new List<string>(recentDetails);
                int start = Math.Max(0, lines.Count - tailCount);
                var visible = lines.GetRange(start, lines.Count - start);
                int omitted = Math.Max(0, detailLineCount - visible.Count);
                if (omitted > 0)
                    visible.Insert(0, "…之前还有 " + omitted + " 条记录…");
                return string.Join(Environment.NewLine, visible.ToArray());
            }

            void ReplaceDetailContents(string text)
            {
                detail.BeginUpdate();
                try
                {
                    detailProgressRows.Clear();
                    detail.Items.Clear();
                    if (!string.IsNullOrEmpty(text))
                    {
                        string[] lines = text.Split(new[] { "\r\n", "\n" },
                            StringSplitOptions.None);
                        foreach (string line in lines)
                            AppendDetailLine(line);
                    }
                }
                finally { detail.EndUpdate(); }
                if (detail.Items.Count > 0)
                    detail.Items[detail.Items.Count - 1].EnsureVisible();
            }

            void AppendDetailLines(IEnumerable<string> lines)
            {
                detail.BeginUpdate();
                try
                {
                    foreach (string line in lines)
                        AppendDetailLine(line);
                }
                finally { detail.EndUpdate(); }
                if (detail.Items.Count > 0)
                    detail.Items[detail.Items.Count - 1].EnsureVisible();
            }

            void AppendDetailLine(string line)
            {
                bool hasProgress = HasFileProgress(line);
                int pct = 0;
                if (hasProgress)
                    lock (detailLock) detailProgress.TryGetValue(line, out pct);
                string text = line ?? "";
                if (hasProgress) text += " " + Math.Max(0, Math.Min(100, pct)) + "%";
                var item = new ListViewItem(text);
                detail.Items.Add(item);
                if (hasProgress) detailProgressRows[line] = item;
            }

            static bool HasFileProgress(string line)
            {
                return !string.IsNullOrEmpty(line)
                    && line.StartsWith("正在抽取: ", StringComparison.Ordinal)
                    && !line.Equals("正在抽取: 其余文件",
                        StringComparison.Ordinal);
            }

            void UpdateDetailProgress(string status, int pct)
            {
                detailProgress[status] = Math.Max(0, Math.Min(100, pct));
                ListViewItem row;
                if (!detailProgressRows.TryGetValue(status, out row) || row == null)
                    return;
                row.Text = status + " " + Math.Max(0, Math.Min(100, pct)) + "%";
            }

            static ListView CreateDetailListView()
            {
                var view = new BufferedDetailListView();
                view.View = View.Details;
                view.HeaderStyle = ColumnHeaderStyle.None;
                view.FullRowSelect = true;
                view.MultiSelect = true;
                view.HideSelection = false;
                view.BorderStyle = BorderStyle.FixedSingle;
                view.Font = new Font("MS Shell Dlg", 8f);
                view.Dock = DockStyle.Fill;
                view.Scrollable = true;
                view.Columns.Add("", 400);
                view.Resize += delegate
                {
                    if (view.Columns.Count > 0)
                        view.Columns[0].Width = Math.Max(40, view.ClientSize.Width - 4);
                };
                view.ContextMenuStrip = CreateDetailContextMenu(view);
                return view;
            }

            class BufferedDetailListView : ListView
            {
                public BufferedDetailListView()
                {
                    DoubleBuffered = true;
                }
            }

            class BufferedPanel : Panel
            {
                public BufferedPanel()
                {
                    DoubleBuffered = true;
                }
            }

            static ContextMenuStrip CreateDetailContextMenu(ListView view)
            {
                var menu = new ContextMenuStrip();
                var copy = new ToolStripMenuItem("复制(&C)");
                copy.Enabled = false;
                menu.Opening += delegate { copy.Enabled = view.SelectedItems.Count > 0; };
                copy.Click += delegate
                {
                    var lines = new List<string>();
                    foreach (ListViewItem item in view.SelectedItems) lines.Add(item.Text);
                    if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
                };
                var all = new ToolStripMenuItem("全选(&A)");
                all.Click += delegate { foreach (ListViewItem item in view.Items) item.Selected = true; };
                menu.Items.Add(copy);
                menu.Items.Add(all);
                return menu;
            }

            static ContextMenuStrip CreateTextContextMenu(TextBoxBase box)
            {
                var menu = new ContextMenuStrip();
                var copy = new ToolStripMenuItem("复制(&C)");
                copy.Enabled = false;
                menu.Opening += delegate { copy.Enabled = box.SelectionLength > 0; };
                copy.Click += delegate
                {
                    if (box.SelectionLength > 0) Clipboard.SetText(box.SelectedText);
                };
                var all = new ToolStripMenuItem("全选(&A)");
                all.Click += delegate { box.SelectAll(); };
                menu.Items.Add(copy);
                menu.Items.Add(all);
                return menu;
            }

            static float GetUiScale()
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                float dpi = 96f;
                try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; }
                catch { }
                float dpiScale = Math.Max(1f, dpi / 96f);
                float logicalWidth = area.Width / dpiScale;
                float logicalHeight = area.Height / dpiScale;
                float resolutionScale = (float)Math.Sqrt(
                    logicalWidth * logicalHeight / (1280f * 720f));
                resolutionScale = Math.Min(1.35f, Math.Max(1f, resolutionScale));
                float fit = Math.Min((logicalWidth - 40f) / 496f,
                    (logicalHeight - 80f) / 332f);
                if (fit < 1f) resolutionScale = Math.Max(0.65f, fit);
                return dpiScale * resolutionScale;
            }

            Size ScaledSize(int width, int height)
            {
                return new Size((int)Math.Round(width * uiScale),
                    (int)Math.Round(height * uiScale));
            }

            Point ScaledPoint(int x, int y)
            {
                return new Point((int)Math.Round(x * uiScale),
                    (int)Math.Round(y * uiScale));
            }

            static void ScaleControlTree(Control control, float scale)
            {
                control.Bounds = new Rectangle(
                    (int)Math.Round(control.Left * scale),
                    (int)Math.Round(control.Top * scale),
                    (int)Math.Round(control.Width * scale),
                    (int)Math.Round(control.Height * scale));
                control.Padding = new Padding(
                    (int)Math.Round(control.Padding.Left * scale),
                    (int)Math.Round(control.Padding.Top * scale),
                    (int)Math.Round(control.Padding.Right * scale),
                    (int)Math.Round(control.Padding.Bottom * scale));
                if (!(control is PictureBox))
                    control.Font = new Font(control.Font.FontFamily,
                        control.Font.Size * scale, control.Font.Style,
                        control.Font.Unit, control.Font.GdiCharSet,
                        control.Font.GdiVerticalFont);
                if (control.MaximumSize != Size.Empty)
                    control.MaximumSize = new Size(
                        (int)Math.Round(control.MaximumSize.Width * scale),
                        (int)Math.Round(control.MaximumSize.Height * scale));
                foreach (Control child in control.Controls)
                    ScaleControlTree(child, scale);
            }

            void SetBar(int pct)
            {
                if (barBox == null || barFill == null) return;
                int p = Math.Max(0, Math.Min(100, pct));
                int w = (int)((barBox.ClientSize.Width - 2) * p / 100.0);
                barFill.Size = new Size(Math.Max(0, w),
                    Math.Max(1, barBox.ClientSize.Height - 2));
            }

            void LayoutProgressRow()
            {
                if (content == null || barBox == null || progressPercent == null)
                    return;
                int padding = Math.Max(4, (int)Math.Ceiling(4f * uiScale));
                int gap = Math.Max(3, (int)Math.Ceiling(4f * uiScale));
                int right = Math.Max(4, (int)Math.Ceiling(6f * uiScale));
                int measured = TextRenderer.MeasureText("100%",
                    progressPercent.Font).Width + padding;
                int width = Math.Max((int)Math.Round(30f * uiScale), measured);
                int left = content.ClientSize.Width - right - width;
                progressPercent.SetBounds(left, progressPercent.Top,
                    width, progressPercent.Height);
                barBox.Width = Math.Max(80, left - gap - barBox.Left);
            }

            void BuildFinish()
            {
                Panel image = SideImagePanel();
                content.Controls.Add(image);
                image.SendToBack();
                content.Controls.Add(BigLabel(ctx.Title + " 安装完成"));
                content.Controls.Add(BodyLabel(
                    ctx.Title + " 已成功安装在你的电脑。\n\n单击“完成”关闭此向导。",
                    110, 120));
                btnNext.Text = "完成(&F)";
                btnNext.Enabled = true;
                btnCancel.Visible = false;
                btnBack.Visible = false;
                runAfter = null;
                finishRunTarget = ctx.Postrun;
                if (string.IsNullOrEmpty(finishRunTarget))
                {
                    // 与 NSIS 习惯一致：完成后默认提供运行主程序（快捷方式目标）
                    var m = Payload.M;
                    finishRunTarget = Util.Is64OS
                        ? (m.K.ContainsKey("x64_target") ? m.K["x64_target"] : null)
                        : (m.K.ContainsKey("x86_target") ? m.K["x86_target"] : null);
                }
                if (!string.IsNullOrEmpty(finishRunTarget) && File.Exists(
                    Path.Combine(ctx.InstDir, finishRunTarget)))
                {
                    runAfter = new CheckBox();
                    runAfter.Text = "运行 " + Path.GetFileName(finishRunTarget);
                    runAfter.AutoSize = true;
                    runAfter.Location = new Point(176, 240);
                    runAfter.Checked = true;
                    content.Controls.Add(runAfter);
                }
                else
                {
                    finishRunTarget = null;
                }
            }

            void OnNext()
            {
                if (page == 0)
                {
                    ShowPage(Payload.Asset("license") == null ? 2 : 1);
                    return;
                }
                if (page == 1) { ShowPage(2); return; }
                if (page == 2)
                {
                    string d = "";
                    if (dirEdit != null) d = dirEdit.Text.Trim();
                    if (d.EndsWith(":")) d += "\\"; // 允许只输 E:
                    d = JoinLocation(d);
                    if (d.Length < 3 || d[1] != ':')
                    {
                        MessageBox.Show("请输入有效的安装位置（如 C:\\ 或 C:\\Games）",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    ctx.InstDir = d;
                    StartInstall();
                    return;
                }
                if (page == 4)
                {
                    if (runAfter != null && runAfter.Checked
                        && !string.IsNullOrEmpty(finishRunTarget))
                    {
                        string p = Path.Combine(ctx.InstDir, finishRunTarget);
                        if (File.Exists(p))
                        {
                            try
                            {
                                var psi = new ProcessStartInfo();
                                psi.FileName = p;
                                psi.WorkingDirectory = ctx.InstDir;
                                psi.UseShellExecute = true;
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    try { Process.Start(psi); } catch { }
                                });
                            }
                            catch { }
                        }
                    }
                    Close();
                }
            }

            void StartInstall()
            {
                ShowPage(3);
            }
        }
    }
}
