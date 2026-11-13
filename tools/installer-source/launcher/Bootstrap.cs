using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
[assembly: AssemblyTitle("LilithBridge Setup")]
[assembly: AssemblyVersion("0.1.4.19")]
[assembly: ComVisible(false)]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
namespace LilithSetupLauncher
{
    static class Bootstrap
    {
        [STAThread] static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length != 0) { MessageBox.Show("请直接打开此安装程序；游戏目录和组件在向导中选择。", "LilithBridge 安装"); return 1; }
            using (var mutex = new Mutex(false, "Local\\LilithBridgeSetup-" + PayloadInfo.Hash.Substring(0, 16)))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { MessageBox.Show("正在准备此版本的安装向导，请稍候。", "LilithBridge 安装"); return 1; }
                    using (var form = new PreparationForm()) Application.Run(form);
                    return 0;
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        internal static string Digest(Stream input)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        internal static string HashFile(string path)
        { Guard(path); using (var input = File.OpenRead(path)) return Digest(input); }
        internal static void Guard(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装缓存路径包含链接，请使用普通用户目录。检查代码 LBBOOT_REPARSE"); }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }
        internal static string Prepare(Action<string, int> progress, CancellationToken cancel)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LilithBridge", "InstallerCache", "v1", "singlefile", PayloadInfo.Hash.Substring(0, 24));
            Guard(root); Directory.CreateDirectory(root);
            string archive = Path.Combine(root, "setup-payload.zip"); Guard(archive);
            if (File.Exists(archive) && (new FileInfo(archive).Length != PayloadInfo.Bytes || HashFile(archive) != PayloadInfo.Hash))
                File.Move(archive, archive + ".invalid-" + Guid.NewGuid().ToString("N"));
            if (!File.Exists(archive))
            {
                progress("正在自动准备安装文件，无需手工解压…", 0);
                string part = archive + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("LilithBridge.SetupPayload.zip"))
                    using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (input == null) throw new IOException("安装文件缺失，检查代码 LBBOOT_PAYLOAD_MISSING");
                        var buffer = new byte[131072]; int n; long total = 0;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        { cancel.ThrowIfCancellationRequested(); total += n; if (total > PayloadInfo.Bytes) throw new IOException("LBBOOT_PAYLOAD_SIZE"); output.Write(buffer, 0, n); progress("正在准备安装向导…", (int)(total * 25 / PayloadInfo.Bytes)); }
                        output.Flush(true); if (total != PayloadInfo.Bytes) throw new IOException("LBBOOT_PAYLOAD_SIZE");
                    }
                    if (HashFile(part) != PayloadInfo.Hash) throw new IOException("安装文件未通过校验，请重新下载此安装程序。检查代码 LBBOOT_PAYLOAD_HASH");
                    cancel.ThrowIfCancellationRequested(); File.Move(part, archive);
                }
                finally { if (File.Exists(part)) File.Delete(part); }
            }
            string exe = Path.Combine(root, "setup", "LilithBridge-Installer.exe"), expectedExe = null;
            using (var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != PayloadInfo.Bytes || Digest(input) != PayloadInfo.Hash) throw new IOException("安装缓存未通过校验，未启动安装器。检查代码 LBBOOT_CACHE_HASH");
                input.Position = 0;
                using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int done = 0;
                    foreach (var entry in zip.Entries)
                    {
                        cancel.ThrowIfCancellationRequested(); string name = entry.FullName;
                        if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (name.Contains("\\") || name.Contains(":") || Path.IsPathRooted(name) || name.Split('/').Any(x => x == "" || x == "." || x == "..") || !seen.Add(name)) throw new IOException("LBBOOT_UNSAFE_ENTRY");
                        total += entry.Length; if (entry.Length < 0 || total > 134217728) throw new IOException("LBBOOT_PAYLOAD_SIZE");
                        string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("LBBOOT_UNSAFE_ENTRY");
                        Guard(target); Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string expected; using (var content = entry.Open()) expected = Digest(content);
                        if (name == "setup/LilithBridge-Installer.exe") expectedExe = expected;
                        bool needsFile = !File.Exists(target);
                        if (!needsFile)
                        {
                            if (new FileInfo(target).Length != entry.Length || HashFile(target) != expected)
                            { File.Move(target, target + ".invalid-" + Guid.NewGuid().ToString("N")); needsFile = true; }
                        }
                        if (needsFile)
                        {
                            string part = target + "." + Guid.NewGuid().ToString("N") + ".part";
                            try
                            {
                                using (var content = entry.Open()) using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                {
                                    var buffer = new byte[131072]; int n; long length = 0;
                                    while ((n = content.Read(buffer, 0, buffer.Length)) > 0) { cancel.ThrowIfCancellationRequested(); length += n; if (length > entry.Length) throw new IOException("LBBOOT_ENTRY_SIZE"); output.Write(buffer, 0, n); }
                                    output.Flush(true); if (length != entry.Length) throw new IOException("LBBOOT_ENTRY_SIZE");
                                }
                                if (HashFile(part) != expected) throw new IOException("LBBOOT_ENTRY_HASH");
                                cancel.ThrowIfCancellationRequested(); Guard(target); File.Move(part, target);
                            }
                            finally { if (File.Exists(part)) File.Delete(part); }
                        }
                        progress("正在校验安装文件（" + (++done) + " / " + zip.Entries.Count + "）…", 25 + (int)(70L * done / Math.Max(1, zip.Entries.Count)));
                    }
                }
            }
            cancel.ThrowIfCancellationRequested(); Guard(exe);
            if (expectedExe == null || HashFile(exe) != expectedExe) throw new IOException("LBBOOT_INSTALLER_HASH");
            progress("安装向导已准备好，正在打开…", 100); return exe;
        }
    }
    sealed class PreparationForm : Form
    {
        readonly Label status = new Label(); readonly ProgressBar progress = new ProgressBar(); readonly Button action = new Button();
        CancellationTokenSource cancel; bool busy, closing;
        public PreparationForm()
        {
            Text = "LilithBridge 安装 · .19 整合候选"; ClientSize = new Size(540, 190); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10F);
            status.SetBounds(20, 20, 500, 62); status.Text = "正在准备安装向导…"; Controls.Add(status);
            progress.SetBounds(20, 90, 500, 24); Controls.Add(progress);
            action.SetBounds(395, 132, 125, 34); action.Text = "取消"; Controls.Add(action);
            action.Click += async (s, e) => { if (busy) { closing = true; cancel.Cancel(); status.Text = "正在取消…"; } else await Start(); };
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; closing = true; cancel.Cancel(); } };
            Shown += async (s, e) => await Start();
        }
        void Report(string text, int value)
        { if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(new Action<string, int>(Report), text, value); return; } status.Text = text; progress.Value = Math.Max(0, Math.Min(100, value)); }
        async Task Start()
        {
            busy = true; closing = false; cancel = new CancellationTokenSource(); action.Text = "取消";
            try
            {
                string exe = await Task.Run(() => Bootstrap.Prepare(Report, cancel.Token)); cancel.Token.ThrowIfCancellationRequested();
                string origin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var info = new ProcessStartInfo(exe, "--resource-dir \"" + origin + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
                using (var child = Process.Start(info)) { if (child == null) throw new IOException("LBBOOT_START_FAILED"); }
                closing = true;
            }
            catch (OperationCanceledException) { closing = true; }
            catch (Exception error) { status.Text = "安装向导尚未打开，游戏和存档未操作。可重试；若仍失败，请反馈此信息：\r\n" + error.Message; action.Text = "重试"; }
            finally { busy = false; cancel.Dispose(); cancel = null; if (closing) Close(); }
        }
    }
}
