// PixeldreamUninstaller — 独立小体积卸载器（由打包器封包时编译，安装时写入安装目录）。
// 界面与安装器同风格：简洁进度页、细节开关与底部按钮。
// 卸载清单由安装器附加在本 exe 尾部（[u32 长度]["PDUD"] 结尾），不产生可见清单文件。
// 命令行：/S 静默卸载。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Management;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("卸载程序")]
[assembly: System.Reflection.AssemblyDescription("卸载程序")]

namespace PdUninstall
{
    static class Entry
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool silent = HasFlag(args, "S");
            Uninstaller.Run(silent);
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

    static class NativeCaret
    {
        [DllImport("user32.dll")]
        static extern bool HideCaret(IntPtr hWnd);

        public static void Hide(Control control)
        {
            try { if (control != null && control.IsHandleCreated)
                HideCaret(control.Handle); } catch { }
        }
    }

    class Info
    {
        public string InstDir = "";
        public string UnKey = "";
        public bool AllUsers;
        public string Title = "应用程序";
        public string SmFolder;
        public string ShortcutName;
        public bool Desktop = true;
        public string Postrun;
        public List<string> Dirs = new List<string>();
        public List<string> Files = new List<string>();
        public List<string> FailedFiles = new List<string>();
        public List<string> LockedFiles = new List<string>();
        public List<int> LockingProcessIds = new List<int>();
    }

    static class Uninstaller
    {
        static Mutex instanceMutex;
        [StructLayout(LayoutKind.Sequential)]
        struct RmUniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string ServiceShortName;
            public uint ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmStartSession(out uint sessionHandle,
            int sessionFlags, StringBuilder sessionKey);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmRegisterResources(uint sessionHandle,
            uint fileCount, string[] fileNames, uint appCount,
            RmUniqueProcess[] apps, uint serviceCount, string[] services);
        [DllImport("rstrtmgr.dll")]
        static extern int RmGetList(uint sessionHandle, out uint needed,
            ref uint count, [In, Out] RmProcessInfo[] affectedApps,
            ref uint rebootReasons);
        [DllImport("rstrtmgr.dll")]
        static extern int RmShutdown(uint sessionHandle, int actionFlags,
            IntPtr statusCallback);
        [DllImport("rstrtmgr.dll")]
        static extern int RmEndSession(uint sessionHandle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        static extern IntPtr CreateFile(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        static bool IsBlockedFromDelete(string path)
        {
            const uint DELETE_ACCESS = 0x00010000;
            const uint SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004;
            const uint OPEN_EXISTING = 3;
            const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
            IntPtr handle = CreateFile(path, DELETE_ACCESS, SHARE_ALL,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle != new IntPtr(-1))
            {
                CloseHandle(handle);
                return false;
            }
            int error = Marshal.GetLastWin32Error();
            return error == 32 || error == 33; // sharing/lock violation
        }

        public static Info Load()
        {
            Info info = LoadFromTail();
            if (info == null) info = LoadFromXml();
            if (info == null) info = new Info();
            return info;
        }

        // 尾部格式：[PDUNINST1 文本 UTF-8][u32 长度]["PDUD"]
        static Info LoadFromTail()
        {
            try
            {
                string me = Application.ExecutablePath;
                using (FileStream fs = new FileStream(me, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 16) return null;
                    fs.Seek(-8, SeekOrigin.End);
                    byte[] tb = new byte[8];
                    ReadFull(fs, tb, 0, 8);
                    int mlen = BitConverter.ToInt32(tb, 0);
                    uint magic = BitConverter.ToUInt32(tb, 4);
                    if (magic != 0x44554450 || mlen <= 0 || mlen > fs.Length - 16)
                        return null;
                    fs.Seek(-8 - mlen, SeekOrigin.End);
                    byte[] mb = new byte[mlen];
                    ReadFull(fs, mb, 0, mlen);
                    return Parse(Encoding.UTF8.GetString(mb));
                }
            }
            catch { return null; }
        }

        // 旧版兼容：同目录 uninst.xml
        static Info LoadFromXml()
        {
            try
            {
                string me = Application.ExecutablePath;
                string xml = Path.Combine(Path.GetDirectoryName(me), "uninst.xml");
                if (!File.Exists(xml)) return null;
                return Parse(File.ReadAllText(xml, Encoding.UTF8));
            }
            catch { return null; }
        }

        static void ReadFull(FileStream fs, byte[] buf, int off, int cnt)
        {
            int done = 0;
            while (done < cnt)
            {
                int n = fs.Read(buf, off + done, cnt - done);
                if (n <= 0) throw new IOException("读取中断");
                done += n;
            }
        }

        static Info Parse(string text)
        {
            var info = new Info();
            foreach (string line0 in text.Split('\n'))
            {
                string line = line0.TrimEnd('\r');
                if (line.Length == 0 || line == "PDUNINST1" || line == "END") continue;
                string[] p = line.Split('|');
                if (p[0] == "K" && p.Length >= 3)
                {
                    string v = line.Substring(p[0].Length + p[1].Length + 2);
                    switch (p[1])
                    {
                        case "inst_dir": info.InstDir = v; break;
                        case "un_key": info.UnKey = v; break;
                        case "allusers": info.AllUsers = v == "1"; break;
                        case "title": info.Title = v; break;
                        case "sm_folder": info.SmFolder = v; break;
                        case "shortcut_name": info.ShortcutName = v; break;
                        case "desktop": info.Desktop = v == "1"; break;
                        case "postrun": info.Postrun = v; break;
                    }
                }
                else if (p[0] == "D" && p.Length >= 2) info.Dirs.Add(p[1]);
                else if (p[0] == "F" && p.Length >= 2) info.Files.Add(p[1]);
            }
            return info;
        }

        public static void Run(bool silent)
        {
            Info info;
            try { info = Load(); }
            catch { info = new Info(); }
            bool needAdmin = info.AllUsers;
            if (needAdmin && !IsElevated())
            {
                RelaunchElevated(Environment.CommandLine);
                return;
            }
            if (!SingleInstance.TryAcquire("Uninstaller",
                Application.ExecutablePath, out instanceMutex))
            {
                MessageBox.Show(info.Title + "卸载程序已运行，请勿重复打开", "提示", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            try
            {
                RunSingleInstance(info, silent);
            }
            finally
            {
                try { instanceMutex.ReleaseMutex(); } catch { }
                instanceMutex.Dispose();
                instanceMutex = null;
            }
        }

        static void RunSingleInstance(Info info, bool silent)
        {
            if (!silent)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var f = new UninstallForm(info);
                Application.Run(f);
                if (f.Error != null)
                {
                    MessageBox.Show("卸载出错：" + f.Error.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (!f.Completed)
                {
                    return; // 用户取消
                }
            }
            else
            {
                try { DoUninstall(info, null, null); }
                catch { }
            }
            SelfDeleteChain();
        }

        static bool IsElevated()
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

        static void RelaunchElevated(string args)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = Application.ExecutablePath;
            int i = args.IndexOf("\"", 1);
            psi.Arguments = i > 0 ? args.Substring(i + 1) : "";
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try { Process.Start(psi); }
            catch (Win32Exception) { }
            Environment.Exit(0);
        }

        public static void DoUninstall(Info info, Action<int, string> progress,
            Action<string> log)
        {
            string instDir = info.InstDir;
            info.FailedFiles.Clear();
            info.LockingProcessIds.Clear();
            if (info.Postrun != null && instDir != "" &&
                File.Exists(Path.Combine(instDir, info.Postrun)))
                RunWait(Path.Combine(instDir, info.Postrun), null);

            if (info.ShortcutName != null)
            {
                if (info.Desktop)
                {
                    string dd = Environment.GetFolderPath(info.AllUsers
                        ? Environment.SpecialFolder.CommonDesktopDirectory
                        : Environment.SpecialFolder.DesktopDirectory);
                    TryDelete(Path.Combine(dd, info.ShortcutName + ".lnk"));
                }
                if (info.SmFolder != null)
                {
                    string base1 = Environment.GetFolderPath(info.AllUsers
                        ? Environment.SpecialFolder.CommonStartMenu
                        : Environment.SpecialFolder.StartMenu);
                    DeleteTree(Path.Combine(Path.Combine(base1, "Programs"),
                        info.SmFolder));
                }
            }

            int total = info.Files.Count;
            int done = 0;
            foreach (string rel in info.Files)
            {
                if (instDir != "")
                {
                    string path = Path.Combine(instDir, rel);
                    if (!TryDelete(path) && File.Exists(path))
                        info.FailedFiles.Add(path);
                }
                done++;
                if (progress != null && total > 0)
                    progress(done * 100 / total, "正在删除 " + rel);
                if (log != null) log("已删除 " + rel);
            }
            string[] ds = info.Dirs.ToArray();
            Array.Sort(ds, delegate(string a, string b)
            { return b.Length.CompareTo(a.Length); });
            foreach (string d in ds)
            {
                if (instDir != "")
                    TryDeleteDir(Path.Combine(instDir, d));
            }
            if (instDir != "")
            {
                TryDelete(Path.Combine(instDir, "uninst.xml")); // 旧版兼容清理
                TryDelete(Path.Combine(instDir,
                    Path.GetFileName(Application.ExecutablePath)));
                TryDeleteDir(instDir);
            }

            if (info.UnKey != "" && info.FailedFiles.Count == 0)
            {
                RemoveRegistration(info);
            }
            if (info.FailedFiles.Count > 0)
            {
                foreach (Process p in FindLockingProcesses(info.FailedFiles))
                {
                    try { info.LockingProcessIds.Add(p.Id); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            if (progress != null) progress(100, "卸载完成");
        }

        static void RemoveRegistration(Info info)
        {
            if (info.UnKey == "") return;
            RegistryKey bk = info.AllUsers ? Registry.LocalMachine
                : Registry.CurrentUser;
            try { bk.DeleteSubKeyTree(info.UnKey, false); } catch { }
        }

        public static bool RetryFailedFiles(Info info)
        {
            var stillFailed = new List<string>();
            foreach (string path in info.FailedFiles)
                if (!TryDelete(path) && File.Exists(path)) stillFailed.Add(path);
            info.FailedFiles = stillFailed;
            if (stillFailed.Count > 0) return false;

            string[] ds = info.Dirs.ToArray();
            Array.Sort(ds, delegate(string a, string b)
            { return b.Length.CompareTo(a.Length); });
            foreach (string d in ds)
                TryDeleteDir(Path.Combine(info.InstDir, d));
            TryDelete(Path.Combine(info.InstDir, "uninst.xml"));
            TryDelete(Path.Combine(info.InstDir,
                Path.GetFileName(Application.ExecutablePath)));
            TryDeleteDir(info.InstDir);
            RemoveRegistration(info);
            return true;
        }

        public static List<Process> FindLockingProcesses(List<string> files)
        {
            var found = new Dictionary<int, Process>();
            var existing = new List<string>();
            foreach (string file in files)
                try { if (File.Exists(file)) existing.Add(Path.GetFullPath(file)); }
                catch { }
            if (existing.Count == 0) return new List<Process>();

            uint session = 0;
            try
            {
                var key = new StringBuilder(33);
                if (RmStartSession(out session, 0, key) != 0)
                    return new List<Process>();
                for (int offset = 0; offset < existing.Count; offset += 1000)
                {
                    int length = Math.Min(1000, existing.Count - offset);
                    string[] chunk = existing.GetRange(offset, length).ToArray();
                    if (RmRegisterResources(session, (uint)length,
                        chunk, 0, null, 0, null) != 0)
                        return new List<Process>();
                }
                uint needed, count = 0, reasons = 0;
                int rc = RmGetList(session, out needed, ref count, null,
                    ref reasons);
                if (rc == 234 && needed > 0)
                {
                    count = needed;
                    var apps = new RmProcessInfo[(int)count];
                    rc = RmGetList(session, out needed, ref count, apps,
                        ref reasons);
                    if (rc == 0)
                        for (int i = 0; i < count; i++)
                            AddProcess(found, apps[i].Process.ProcessId);
                }
            }
            catch { }
            finally
            {
                if (session != 0) try { RmEndSession(session); } catch { }
            }
            return new List<Process>(found.Values);
        }

        public static List<Process> FindProcessesToClose(Info info)
        {
            var found = new Dictionary<int, Process>();
            string root = "";
            try { root = Path.GetFullPath(info.InstDir).TrimEnd('\\') + "\\"; }
            catch { }
            int ownPid = Process.GetCurrentProcess().Id;
            if (root != "")
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                    using (ManagementObjectCollection rows = searcher.Get())
                    {
                        foreach (ManagementObject row in rows)
                        using (row)
                        {
                            try
                            {
                                int pid = Convert.ToInt32(row["ProcessId"]);
                                if (pid == ownPid) continue;
                                string exe = row["ExecutablePath"] as string;
                                string cmd = row["CommandLine"] as string;
                                bool match = IsInsideInstallDir(exe, root);
                                if (!match && !string.IsNullOrEmpty(cmd))
                                    match = cmd.Replace('/', '\\').IndexOf(
                                        root.TrimEnd('\\'),
                                        StringComparison.OrdinalIgnoreCase) >= 0;
                                if (match) AddProcess(found, pid);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == ownPid) { p.Dispose(); continue; }
                        if (IsInsideInstallDir(p.MainModule.FileName, root))
                        {
                            if (!found.ContainsKey(p.Id)) found.Add(p.Id, p);
                            else p.Dispose();
                        }
                        else p.Dispose();
                    }
                    catch { try { p.Dispose(); } catch { } }
                }
            }

            var files = new List<string>();
            foreach (string rel in info.Files)
            {
                string path = Path.Combine(info.InstDir, rel);
                if (File.Exists(path) && IsBlockedFromDelete(path))
                    files.Add(path);
            }
            info.LockedFiles = files;
            foreach (Process p in FindLockingProcesses(files))
            {
                if (!found.ContainsKey(p.Id)) found.Add(p.Id, p);
                else p.Dispose();
            }
            return new List<Process>(found.Values);
        }

        static bool IsInsideInstallDir(string path, string root)
        {
            try
            {
                return !string.IsNullOrEmpty(path) &&
                    Path.GetFullPath(path).StartsWith(root,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void AddProcess(Dictionary<int, Process> found, int pid)
        {
            if (pid <= 0 || found.ContainsKey(pid)) return;
            try { found.Add(pid, Process.GetProcessById(pid)); } catch { }
        }

        public static void CloseProcesses(List<Process> processes)
        {
            foreach (Process p in processes)
                try { if (!p.HasExited) p.CloseMainWindow(); } catch { }
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                bool alive = false;
                foreach (Process p in processes)
                    try { if (!p.HasExited) { alive = true; break; } } catch { }
                if (!alive) break;
                Thread.Sleep(100);
            }
            foreach (Process p in processes)
                try { if (!p.HasExited) p.Kill(); } catch { }
            foreach (Process p in processes)
            {
                try { p.WaitForExit(3000); } catch { }
                try { p.Dispose(); } catch { }
            }
        }

        static void RunWait(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                if (args != null) psi.Arguments = args;
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi)) p.WaitForExit();
            }
            catch { }
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return !File.Exists(path);
            }
            catch { return false; }
        }

        static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, false); }
            catch { }
        }

        static void DeleteTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
                foreach (string d in Directory.GetDirectories(dir)) DeleteTree(d);
                Directory.Delete(dir, false);
            }
            catch { }
        }

        static void SelfDeleteChain()
        {
            try
            {
                string me = Application.ExecutablePath;
                string dir = Path.GetDirectoryName(me);
                string root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(dir) || string.Equals(
                    Path.GetFullPath(dir).TrimEnd('\\'),
                    Path.GetFullPath(root).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)) return;
                var psi = new ProcessStartInfo();
                string systemDir = Environment.GetFolderPath(
                    Environment.SpecialFolder.System);
                psi.FileName = Path.Combine(systemDir, "cmd.exe");
                // The uninstaller EXE is still locked briefly after the UI exits.
                // Retry both self-deletion and directory removal so the empty
                // install folder does not remain on slower disks or busy systems.
                string command = "/d /c ping -n 3 127.0.0.1 > nul";
                for (int i = 0; i < 8; i++)
                {
                    command += " & del /f /q \"" + me + "\" >nul 2>&1"
                        + " & rmdir /s /q \"" + dir + "\" >nul 2>&1"
                        + " & ping -n 2 127.0.0.1 > nul";
                }
                psi.Arguments = command;
                // cmd.exe must not inherit the installation folder as its
                // current directory, or Windows can refuse to remove it.
                psi.WorkingDirectory = systemDir;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------- 界面（与安装器同风格）

    static class Res
    {
        public static Image Load(string name)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("PdUninstall." + name))
                {
                    if (s != null)
                    {
                        using (var image = System.Drawing.Image.FromStream(s))
                            return new Bitmap(image);
                    }
                }
            }
            catch { }
            return null;
        }
    }

    class UninstallForm : Form
    {
        Info info;
        Label bigTitle;
        PictureBox headerImage;
        Label body;
        Label cur, progressPercent;
        Panel barBox, barFill, detailFrame;
        ListView detail;
        Button btnUn, btnCancel, btnDone, btnDetails;
        bool working;
        volatile bool detailExpanded;
        readonly Queue<string> pendingDetailLogs = new Queue<string>();
        readonly Queue<string> recentDetailLogs = new Queue<string>();
        readonly object detailLogLock = new object();
        System.Windows.Forms.Timer detailLogTimer;
        bool detailNeedsRefresh;
        int detailLogCount;
        float uiScale;
        public bool Completed;
        public Exception Error;

        public UninstallForm(Info i)
        {
            info = i;
            Text = "卸载 " + info.Title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            uiScale = GetUiScale();
            ClientSize = ScaledSize(496, 332);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Resize += delegate { LayoutProgressRow(); };

            bigTitle = new Label();
            bigTitle.Text = "正在卸载 " + info.Title;
            bigTitle.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
            bigTitle.AutoSize = false;
            bigTitle.Size = new Size(320, 24);
            bigTitle.Location = new Point(12, 14);
            Controls.Add(bigTitle);

            headerImage = new PictureBox();
            headerImage.Location = new Point(334, 8);
            headerImage.Size = new Size(150, 57);
            headerImage.SizeMode = PictureBoxSizeMode.Zoom;
            headerImage.Image = Res.Load("header");
            Controls.Add(headerImage);
            headerImage.SendToBack();

            body = new Label();
            body.Text = "将要从你的电脑中删除 " + info.Title +
                " 及其全部组件。\n\n单击“卸载”开始。";
            body.Font = new Font("Microsoft YaHei UI", 9);
            body.AutoSize = false;
            body.Size = new Size(472, 130);
            body.Location = new Point(12, 64);
            Controls.Add(body);

            cur = new Label();
            cur.Text = "准备中…";
            cur.AutoEllipsis = true;
            cur.AutoSize = false;
            cur.Size = new Size(472, 20);
            cur.Location = new Point(12, 68);
            cur.Visible = false;
            Controls.Add(cur);

            barBox = new Panel();
            barBox.Size = new Size(444, 18);
            barBox.Location = new Point(12, 92);
            barBox.BorderStyle = BorderStyle.FixedSingle;
            barBox.BackColor = Color.White;
            barBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            barBox.Visible = false;
            Controls.Add(barBox);

            progressPercent = new Label();
            progressPercent.Text = "0%";
            progressPercent.TextAlign = ContentAlignment.MiddleCenter;
            progressPercent.Font = new Font("Microsoft YaHei UI", 7f);
            progressPercent.AutoSize = false;
            progressPercent.Size = new Size(30, 20);
            progressPercent.Location = new Point(460, 91);
            progressPercent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            progressPercent.Visible = false;
            Controls.Add(progressPercent);

            barFill = new Panel();
            barFill.BackColor = Color.FromArgb(0, 128, 0);
            barFill.Size = new Size(0, 16);
            barFill.Location = new Point(1, 1);
            barBox.Controls.Add(barFill);

            detail = CreateDetailListView();
            detailFrame = new Panel();
            detailFrame.Size = new Size(472, 138);
            detailFrame.Location = new Point(12, 146);
            detailFrame.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                | AnchorStyles.Left | AnchorStyles.Right;
            detailFrame.BorderStyle = BorderStyle.None;
            detailFrame.BackColor = Color.White;
            detailFrame.Controls.Add(detail);
            Controls.Add(detailFrame);
            detailFrame.Visible = false;

            btnDetails = new Button();
            btnDetails.Text = "显示细节(&D)";
            btnDetails.Size = new Size(100, 26);
            btnDetails.Location = new Point(12, 116);
            btnDetails.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnDetails.Visible = false;
            btnDetails.Click += delegate { SetDetailsExpanded(!detailExpanded); };
            Controls.Add(btnDetails);

            detailLogTimer = new System.Windows.Forms.Timer();
            detailLogTimer.Interval = 50;
            detailLogTimer.Tick += delegate { FlushDetailLogs(); };

            btnUn = new Button();
            btnUn.Text = "卸载(&I)";
            btnUn.Size = new Size(88, 26);
            btnUn.Location = new Point(302, 298);
            btnUn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnUn.Click += delegate { StartUninstall(); };
            Controls.Add(btnUn);

            btnCancel = new Button();
            btnCancel.Text = "取消(&C)";
            btnCancel.Size = new Size(80, 26);
            btnCancel.Location = new Point(404, 298);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.Click += delegate
            {
                if (working) return; // 卸载中不可取消，防中断
                Close();
            };
            Controls.Add(btnCancel);
            AcceptButton = btnUn;

            btnDone = new Button();
            btnDone.Text = "完成(&F)";
            btnDone.Size = new Size(88, 26);
            btnDone.Location = new Point(404, 298);
            btnDone.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnDone.Visible = false;
            btnDone.Click += delegate { Close(); };
            Controls.Add(btnDone);

            if (uiScale != 1f)
                foreach (Control control in Controls)
                    ScaleControlTree(control, uiScale);
            LayoutProgressRow();
            btnDetails.BringToFront();

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (working && e.CloseReason == CloseReason.UserClosing)
                    e.Cancel = true; // 卸载进行中阻止关闭（线程无法安全中止）
            };
        }

        void StartUninstall()
        {
            if (working) return;
            working = true;
            btnUn.Visible = false;
            btnCancel.Visible = false;
            body.Visible = false;
            cur.Visible = true;
            cur.Text = "正在检查运行程序和文件占用…";
            barBox.Visible = true;
            progressPercent.Visible = true;
            progressPercent.Text = "0%";
            SetBar(0);
            SetDetailsExpanded(false);
            btnDetails.Visible = true;

            var check = new BackgroundWorker();
            check.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                e.Result = Uninstaller.FindProcessesToClose(info);
            };
            check.RunWorkerCompleted += delegate(object s,
                RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    RestoreUninstallPrompt("检查程序占用失败：" + e.Error.Message);
                    return;
                }
                var processes = e.Result as List<Process>;
                if (processes == null || processes.Count == 0)
                {
                    if (info.LockedFiles.Count > 0)
                    {
                        string paths = string.Join("\n", info.LockedFiles
                            .GetRange(0, Math.Min(8, info.LockedFiles.Count)).ToArray());
                        RestoreUninstallPrompt(
                            "有文件正被占用，但无法识别占用程序。请先关闭相关程序，再重新点击卸载：\n" + paths);
                        return;
                    }
                    StartRemovalWorker();
                    return;
                }
                DialogResult answer = MessageBox.Show(this,
                    "以下程序正在运行或占用安装文件：\n\n" +
                    ProcessNames(processes) +
                    "\n\n点击“是”关闭程序并继续卸载。未响应的程序会被强制结束，未保存内容可能丢失。",
                    "关闭运行中的程序", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes) StartCloseWorker(processes);
                else
                {
                    DisposeProcesses(processes);
                    RestoreUninstallPrompt("关闭相关程序后，可以再次点击“卸载”继续。");
                }
            };
            check.RunWorkerAsync();
        }

        void StartCloseWorker(List<Process> processes)
        {
            cur.Text = "正在关闭占用程序…";
            var closer = new BackgroundWorker();
            closer.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                Uninstaller.CloseProcesses(processes);
                Thread.Sleep(1000); // let Windows release handles before rescanning
                e.Result = Uninstaller.FindProcessesToClose(info);
            };
            closer.RunWorkerCompleted += delegate(object s,
                RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    RestoreUninstallPrompt("关闭占用程序失败：" + e.Error.Message);
                    return;
                }
                var remaining = e.Result as List<Process>;
                if ((remaining != null && remaining.Count > 0) ||
                    info.LockedFiles.Count > 0)
                {
                    string names = remaining != null && remaining.Count > 0
                        ? ProcessNames(remaining)
                        : string.Join("\n", info.LockedFiles
                            .GetRange(0, Math.Min(8, info.LockedFiles.Count)).ToArray());
                    if (remaining != null) DisposeProcesses(remaining);
                    RestoreUninstallPrompt(
                        "这些程序仍在占用文件，请先关闭后重新点击卸载：\n" + names);
                    return;
                }
                StartRemovalWorker();
            };
            closer.RunWorkerAsync();
        }

        void RestoreUninstallPrompt(string message)
        {
            working = false;
            detailLogTimer.Stop();
            cur.Visible = false;
            barBox.Visible = false;
            progressPercent.Visible = false;
            SetDetailsExpanded(false);
            btnDetails.Visible = false;
            body.Text = message;
            body.ForeColor = Color.Firebrick;
            body.Visible = true;
            btnUn.Visible = true;
            btnCancel.Visible = true;
            btnUn.Focus();
        }

        void StartRemovalWorker()
        {
            cur.Text = "正在卸载…";

            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                int lastPct = -1;
                int lastTick = Environment.TickCount;
                Uninstaller.DoUninstall(info, delegate(int p, string t)
                {
                    int now = Environment.TickCount;
                    if (p != lastPct && (lastPct < 0 || p >= 100
                        || unchecked(now - lastTick) >= 100))
                    {
                        lastPct = p;
                        lastTick = now;
                        worker.ReportProgress(p, t);
                    }
                }, delegate(string line)
                {
                    EnqueueDetailLog(line);
                });
            };
            worker.ProgressChanged += delegate(object s,
                ProgressChangedEventArgs e)
            {
                if (e.ProgressPercentage >= 0)
                {
                    SetBar(e.ProgressPercentage);
                    progressPercent.Text = e.ProgressPercentage + "%";
                    string t = e.UserState as string;
                    if (!string.IsNullOrEmpty(t)) cur.Text = t;
                }
            };

            worker.RunWorkerCompleted += delegate(object s,
                RunWorkerCompletedEventArgs e)
            {
                working = false;
                detailLogTimer.Stop();
                FlushDetailLogs();
                cur.Visible = false;
                barBox.Visible = false;
                progressPercent.Visible = false;
                SetDetailsExpanded(false);
                btnDetails.Visible = false;
                if (e.Error != null)
                {
                    Error = e.Error;
                    body.Text = "卸载出错：" + e.Error.Message;
                    body.ForeColor = Color.Firebrick;
                    body.Visible = true;
                    btnDone.Visible = true;
                    btnDone.Focus();
                    return;
                }
                if (info.FailedFiles.Count > 0)
                {
                    HandleFailedFiles();
                    return;
                }
                ShowUninstallResult(true, info.Title +
                    " 已从你的电脑中删除。\n\n单击“完成”关闭。", false);
            };
            detailLogTimer.Start();
            worker.RunWorkerAsync();
        }

        void SetDetailsExpanded(bool expanded)
        {
            List<string> snapshot = null;
            int omitted = 0;
            if (expanded)
            {
                detail.Visible = true;
                detailFrame.Visible = true;
                detailFrame.BringToFront();
            }
            lock (detailLogLock)
            {
                detailExpanded = expanded;
                pendingDetailLogs.Clear();
                detailNeedsRefresh = false;
                if (expanded)
                    snapshot = RecentDetailSnapshot(out omitted);
            }
            if (!expanded)
            {
                detail.Visible = false;
                detailFrame.Visible = false;
                Invalidate(detailFrame.Bounds, true);
                Update();
            }
            btnDetails.Text = expanded ? "隐藏细节(&D)" : "显示细节(&D)";
            if (expanded) ReplaceDetailLogSnapshot(snapshot, omitted);
            btnDetails.BringToFront();
        }

        void EnqueueDetailLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (detailLogLock)
            {
                recentDetailLogs.Enqueue(line);
                detailLogCount++;
                while (recentDetailLogs.Count > 2000)
                    recentDetailLogs.Dequeue();
                if (detailExpanded)
                {
                    if (pendingDetailLogs.Count < 2000)
                        pendingDetailLogs.Enqueue(line);
                    else
                    {
                        pendingDetailLogs.Clear();
                        detailNeedsRefresh = true;
                    }
                }
            }
        }

        void FlushDetailLogs()
        {
            var batch = new List<string>();
            List<string> snapshot = null;
            int omitted = 0;
            lock (detailLogLock)
            {
                if (!detailExpanded || !detailFrame.Visible)
                {
                    pendingDetailLogs.Clear();
                    return;
                }
                if (detailNeedsRefresh)
                {
                    snapshot = RecentDetailSnapshot(out omitted);
                    pendingDetailLogs.Clear();
                    detailNeedsRefresh = false;
                }
                else
                {
                    int count = Math.Min(50, pendingDetailLogs.Count);
                    for (int i = 0; i < count; i++)
                        batch.Add(pendingDetailLogs.Dequeue());
                }
            }
            if (snapshot != null)
            {
                ReplaceDetailLogSnapshot(snapshot, omitted);
                return;
            }
            if (batch.Count == 0) return;
            detail.BeginUpdate();
            try
            {
                foreach (string line in batch)
                    detail.Items.Add(new ListViewItem(line));
                while (detail.Items.Count > 2000)
                    detail.Items.RemoveAt(0);
            }
            finally { detail.EndUpdate(); }
            if (detail.Items.Count > 0)
                detail.Items[detail.Items.Count - 1].EnsureVisible();
        }

        List<string> RecentDetailSnapshot(out int omitted)
        {
            const int tailCount = 100;
            var all = new List<string>(recentDetailLogs);
            int start = Math.Max(0, all.Count - tailCount);
            var visible = all.GetRange(start, all.Count - start);
            omitted = Math.Max(0, detailLogCount - visible.Count);
            return visible;
        }

        void ReplaceDetailLogSnapshot(List<string> lines, int omitted)
        {
            detail.BeginUpdate();
            try
            {
                detail.Items.Clear();
                if (omitted > 0)
                    detail.Items.Add(new ListViewItem(
                        "…之前还有 " + omitted + " 条记录…"));
                if (lines != null)
                    foreach (string line in lines)
                        detail.Items.Add(new ListViewItem(line));
            }
            finally { detail.EndUpdate(); }
            if (detail.Items.Count > 0)
                detail.Items[detail.Items.Count - 1].EnsureVisible();
        }

        void HandleFailedFiles()
        {
            var lockers = new List<Process>();
            foreach (int pid in info.LockingProcessIds)
                try { lockers.Add(Process.GetProcessById(pid)); } catch { }
            if (lockers.Count == 0)
            {
                ShowUninstallResult(false,
                    "有 " + info.FailedFiles.Count +
                    " 个文件无法删除。请关闭正在使用这些文件的程序后，重新运行卸载程序。",
                    true);
                return;
            }

            DialogResult answer = MessageBox.Show(this,
                "以下程序仍在占用文件：\n\n" + ProcessNames(lockers) +
                "\n\n点击“是”关闭占用程序并重试删除。未保存内容可能丢失。",
                "文件正在使用", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                DisposeProcesses(lockers);
                ShowUninstallResult(false,
                    "部分文件仍被占用，卸载未完成。关闭相关程序后可重新运行卸载程序。",
                    true);
                return;
            }

            working = true;
            body.Text = "正在关闭占用程序并重试删除…";
            body.ForeColor = Color.Black;
            body.Visible = true;
            btnDone.Visible = false;
            var retry = new BackgroundWorker();
            retry.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                Uninstaller.CloseProcesses(lockers);
                Thread.Sleep(700);
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    var remaining = Uninstaller.FindLockingProcesses(
                        info.FailedFiles);
                    if (remaining.Count == 0) break;
                    DisposeProcesses(remaining);
                    Thread.Sleep(500);
                }
                e.Result = Uninstaller.RetryFailedFiles(info);
            };
            retry.RunWorkerCompleted += delegate(object s,
                RunWorkerCompletedEventArgs e)
            {
                working = false;
                if (e.Error != null)
                {
                    ShowUninstallResult(false,
                        "重试删除失败：" + e.Error.Message, true);
                }
                else if (e.Result is bool && (bool)e.Result)
                {
                    ShowUninstallResult(true,
                        info.Title + " 已从你的电脑中删除。\n\n单击“完成”关闭。",
                        false);
                }
                else
                {
                    ShowUninstallResult(false,
                        "仍有 " + info.FailedFiles.Count +
                        " 个文件无法删除。关闭相关程序后可重新运行卸载程序。",
                        true);
                }
            };
            retry.RunWorkerAsync();
        }

        void ShowUninstallResult(bool success, string message, bool error)
        {
            Completed = success;
            body.Text = message;
            body.ForeColor = error ? Color.Firebrick : Color.Black;
            body.Visible = true;
            btnDone.Visible = true;
            btnDone.Focus();
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

        bool CloseInstalledPrograms()
        {
            string root;
            try { root = Path.GetFullPath(info.InstDir).TrimEnd('\\') + "\\"; }
            catch { return true; }
            var found = new Dictionary<int, Process>();
            int ownPid = Process.GetCurrentProcess().Id;
            // MainModule only finds programs whose executable lives under the
            // install directory. WMI command lines also catch Java-hosted apps
            // whose javaw.exe is elsewhere but whose arguments use this folder.
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    using (row)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(row["ProcessId"]);
                            if (pid == ownPid) continue;
                            string exe = row["ExecutablePath"] as string;
                            string cmd = row["CommandLine"] as string;
                            bool inInstall = IsInsideInstallDir(exe, root);
                            if (!inInstall && !string.IsNullOrEmpty(cmd))
                                inInstall = cmd.Replace('/', '\\').IndexOf(
                                    root.TrimEnd('\\'),
                                    StringComparison.OrdinalIgnoreCase) >= 0;
                            if (inInstall) AddProcess(found, pid);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Fallback for systems where WMI is unavailable.
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == ownPid) { p.Dispose(); continue; }
                    string exe = p.MainModule.FileName;
                    if (IsInsideInstallDir(exe, root))
                    {
                        if (!found.ContainsKey(p.Id)) found.Add(p.Id, p);
                        else p.Dispose();
                    }
                    else p.Dispose();
                }
                catch { try { p.Dispose(); } catch { } }
            }
            var running = new List<Process>(found.Values);
            if (running.Count == 0) return true;

            DialogResult answer = MessageBox.Show(this,
                "检测到以下程序仍在运行：\n\n" + ProcessNames(running) +
                "\n\n点击“是”关闭程序并继续卸载。未响应的程序会被强制结束，未保存内容可能丢失。",
                "关闭运行中的程序", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                DisposeProcesses(running);
                return false;
            }

            foreach (Process p in running)
            {
                try { if (!p.HasExited) p.CloseMainWindow(); } catch { }
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                bool alive = false;
                foreach (Process p in running)
                    try { if (!p.HasExited) { alive = true; break; } } catch { }
                if (!alive) break;
                Application.DoEvents();
                Thread.Sleep(100);
            }

            var remaining = new List<Process>();
            foreach (Process p in running)
            {
                try
                {
                    if (!p.HasExited) remaining.Add(p);
                    else p.Dispose();
                }
                catch { p.Dispose(); }
            }
            if (remaining.Count > 0)
            {
                DialogResult force = MessageBox.Show(this,
                    "以下程序未能正常关闭：\n\n" + ProcessNames(remaining) +
                    "\n\n是否强制结束并继续卸载？未保存的内容可能丢失。",
                    "程序仍在运行", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (force != DialogResult.Yes)
                {
                    DisposeProcesses(remaining);
                    return false;
                }
                foreach (Process p in remaining)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
                foreach (Process p in remaining)
                {
                    try { p.WaitForExit(3000); } catch { }
                }
                var stillRunning = new List<Process>();
                foreach (Process p in remaining)
                {
                    try
                    {
                        if (!p.HasExited) stillRunning.Add(p);
                        else p.Dispose();
                    }
                    catch { p.Dispose(); }
                }
                if (stillRunning.Count > 0)
                {
                    string blocked = ProcessNames(stillRunning);
                    DisposeProcesses(stillRunning);
                    MessageBox.Show(this,
                        "无法关闭以下程序，已取消卸载：\n\n" + blocked,
                        "卸载未开始", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return false;
                }
            }
            return true;
        }

        static bool IsInsideInstallDir(string path, string root)
        {
            try
            {
                return !string.IsNullOrEmpty(path) &&
                    Path.GetFullPath(path).StartsWith(root,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void AddProcess(Dictionary<int, Process> found, int pid)
        {
            if (found.ContainsKey(pid)) return;
            try { found.Add(pid, Process.GetProcessById(pid)); } catch { }
        }

        static string ProcessNames(List<Process> processes)
        {
            var names = new List<string>();
            foreach (Process p in processes)
            {
                try
                {
                    string name = p.ProcessName + ".exe";
                    if (!names.Contains(name)) names.Add(name);
                }
                catch { }
            }
            return string.Join("\n", names.ToArray());
        }

        static void DisposeProcesses(List<Process> processes)
        {
            foreach (Process p in processes)
                try { p.Dispose(); } catch { }
        }

        void SetBar(int pct)
        {
            int p = Math.Max(0, Math.Min(100, pct));
            int w = (int)((barBox.ClientSize.Width - 2) * p / 100.0);
            barFill.Size = new Size(Math.Max(0, w),
                Math.Max(1, barBox.ClientSize.Height - 2));
        }

        void LayoutProgressRow()
        {
            if (barBox == null || progressPercent == null) return;
            int padding = Math.Max(4, (int)Math.Ceiling(4f * uiScale));
            int gap = Math.Max(3, (int)Math.Ceiling(4f * uiScale));
            int right = Math.Max(4, (int)Math.Ceiling(6f * uiScale));
            int measured = TextRenderer.MeasureText("100%",
                progressPercent.Font).Width + padding;
            int width = Math.Max((int)Math.Round(30f * uiScale), measured);
            int left = ClientSize.Width - right - width;
            progressPercent.SetBounds(left, progressPercent.Top,
                width, progressPercent.Height);
            barBox.Width = Math.Max(80, left - gap - barBox.Left);
        }
    }
}
