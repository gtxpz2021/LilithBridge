using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Collections.Generic;

namespace LilithSetup
{
    public sealed class NetworkDiagnosticResult
    {
        public string ReportPath, Summary;
        public Dictionary<string, object> Document;
    }
    public static class NetworkDiagnostics
    {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct Statistics { public Luid TokenId, AuthenticationId; public long Expiration; public int TokenType, Impersonation; public uint Charged, Available, Groups, Privileges; public Luid Modified; }
        sealed class Context { public Dictionary<string, object> Public = new Dictionary<string, object>(); public Luid Logon; public bool Read; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr data, int length, out int returned);
        [DllImport("advapi32.dll")] static extern bool IsTokenRestricted(IntPtr token);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        static IntPtr Info(IntPtr token, int kind)
        {
            int length; GetTokenInformation(token, kind, IntPtr.Zero, 0, out length);
            if (length <= 0 || length > 65536) return IntPtr.Zero;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, kind, buffer, length, out length)) { Marshal.FreeHGlobal(buffer); return IntPtr.Zero; }
            return buffer;
        }
        static int Integer(IntPtr token, int kind)
        { IntPtr data = Info(token, kind); if (data == IntPtr.Zero) return -1; try { return Marshal.ReadInt32(data); } finally { Marshal.FreeHGlobal(data); } }
        static Context ReadContext(int id)
        {
            var result = new Context(); IntPtr process = OpenProcess(0x1000, false, id), token = IntPtr.Zero;
            try
            {
                if (process == IntPtr.Zero || !OpenProcessToken(process, 8, out token)) { result.Public["read_status"] = "unavailable"; result.Public["win32_error"] = Marshal.GetLastWin32Error(); return result; }
                result.Public["read_status"] = "read_only"; result.Public["restricted"] = IsTokenRestricted(token); result.Public["app_container"] = Integer(token, 29) == 1; result.Public["session_id"] = Integer(token, 12); result.Public["elevation_type"] = Integer(token, 18);
                IntPtr integrity = Info(token, 25);
                if (integrity != IntPtr.Zero) try { IntPtr sid = Marshal.ReadIntPtr(integrity); byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid)); int rid = count == 0 ? -1 : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))); result.Public["integrity"] = rid < 0 ? "unknown" : rid < 0x2000 ? "low" : rid < 0x3000 ? "medium" : rid < 0x4000 ? "high" : "system"; } finally { Marshal.FreeHGlobal(integrity); }
                IntPtr statistics = Info(token, 10);
                if (statistics != IntPtr.Zero) try { result.Logon = ((Statistics)Marshal.PtrToStructure(statistics, typeof(Statistics))).AuthenticationId; result.Read = true; } finally { Marshal.FreeHGlobal(statistics); }
                return result;
            }
            finally { if (token != IntPtr.Zero) CloseHandle(token); if (process != IntPtr.Zero) CloseHandle(process); }
        }
        public static Dictionary<string, object> ReadExecutionContext()
        {
            var current = ReadContext(Process.GetCurrentProcess().Id); var doc = new Dictionary<string, object> { { "current_process", current.Public }, { "runtime", Environment.Version.ToString() }, { "process_compatibility_os_version", Environment.OSVersion.VersionString }, { "framework_target", ".NET Framework 4.8" }, { "tls_selection", ServicePointManager.SecurityProtocol.ToString() }, { "certificate_validation_callback_present", ServicePointManager.ServerCertificateValidationCallback != null } };
            uint console = WTSGetActiveConsoleSessionId(); doc["active_console_session"] = console == UInt32.MaxValue ? -1L : console;
            bool compared = false;
            foreach (var shell in Process.GetProcessesByName("explorer")) using (shell)
            {
                try
                {
                    if (console != UInt32.MaxValue && shell.SessionId != console) continue;
                    var desktop = ReadContext(shell.Id); doc["interactive_shell"] = desktop.Public;
                    if (current.Read && desktop.Read) { doc["same_logon_as_interactive_shell"] = current.Logon.Low == desktop.Logon.Low && current.Logon.High == desktop.Logon.High; compared = true; }
                    break;
                }
                catch { doc["interactive_shell"] = new Dictionary<string, object> { { "read_status", "unavailable" } }; }
            }
            doc["interactive_context_compared"] = compared;
            // Neither user names nor SIDs, logon identifiers, process lists, paths, keys or config are recorded.
            return doc;
        }
        public static Dictionary<string, object> Error(Exception error)
        {
            var chain = new List<object>(); bool credentials = false, trust = false, denied = false;
            for (var next = error; next != null; next = next.InnerException)
            {
                var row = new Dictionary<string, object> { { "type", next.GetType().Name }, { "hresult", "0x" + unchecked((uint)next.HResult).ToString("X8") } };
                var native = next as Win32Exception; if (native != null) { row["native_code"] = "0x" + unchecked((uint)native.NativeErrorCode).ToString("X8"); credentials |= unchecked((uint)native.NativeErrorCode) == 0x8009030E; denied |= native.NativeErrorCode == 5; }
                var web = next as WebException; if (web != null) { row["web_status"] = web.Status.ToString(); trust |= web.Status == WebExceptionStatus.TrustFailure; var response = web.Response as HttpWebResponse; if (response != null) row["http_status"] = (int)response.StatusCode; }
                chain.Add(row);
            }
            return new Dictionary<string, object> { { "category", credentials ? "windows_schannel_credentials_unavailable" : trust ? "certificate_trust_failure" : denied ? "access_denied" : error is WebException ? "https_request_failed" : "operation_failed" }, { "exception_chain", chain.ToArray() } };
        }
        public static NetworkDiagnosticResult Run(string outputRoot, Action<string> progress)
        {
            outputRoot = SetupCore.SafePath(outputRoot, null); SetupCore.NoReparse(outputRoot); Directory.CreateDirectory(outputRoot);
            var context = ReadExecutionContext(); var checks = new List<object>(); bool apiPassed = false, assetPassed = false;
            Action<string, Action> check = (name, action) =>
            {
                if (progress != null) progress(name);
                try { action(); checks.Add(new Dictionary<string, object> { { "step", name }, { "status", "passed" } }); }
                catch (Exception error) { checks.Add(new Dictionary<string, object> { { "step", name }, { "status", "failed" }, { "error", Error(error) } }); }
            };
            check("1 / 3 读取官方项目发行信息", () => { WizardCore.CheckUpdate(CancellationToken.None); apiPassed = true; });
            check("2 / 3 下载并核验固定官方小安装包（约 1.5 MiB）", () => { string cache = WizardProvisioning.PathIn(outputRoot, "verified-download"); string path = TrustedDownloads.Acquire(TrustedDownloads.PluginRelease, cache, "", true, null, CancellationToken.None); SetupCore.Require(TrustedDownloads.Matches(path, TrustedDownloads.PluginRelease), "LBWIZ_DIAGNOSTIC_ASSET_MISMATCH"); assetPassed = true; });
            check("3 / 3 检查官方 Python 包的 HTTPS 响应（不下载环境）", () =>
            {
                var request = (HttpWebRequest)WebRequest.Create(TrustedDownloads.Python.Url); request.Method = "HEAD"; request.AllowAutoRedirect = false; request.Timeout = 25000; request.UserAgent = "LilithBridge-Network-Diagnostic/1";
                using (var response = (HttpWebResponse)request.GetResponse()) SetupCore.Require(response.StatusCode == HttpStatusCode.OK && response.ContentLength == TrustedDownloads.Python.Bytes, "LBWIZ_DIAGNOSTIC_PYTHON_METADATA_MISMATCH");
            });
            bool compared = (bool)context["interactive_context_compared"], different = compared && !(bool)context["same_logon_as_interactive_shell"];
            string summary = apiPassed && assetPassed ? "此进程已通过官方发行读取和小安装包下载校验。" : "此进程的在线下载检查尚未通过。";
            summary += different ? " 当前进程与交互式桌面的登录上下文不同；这支持执行环境差异的判断，仍不能替代桌面启动实测。" : " 当前结果不能单独证明普通桌面启动也会失败；需要比较本入口由用户正常双击运行的结果。";
            summary += " 诊断不读取 Key、游戏配置或记忆，不安装组件，不修改 TLS、证书或系统设置。";
            var document = new Dictionary<string, object> { { "schema", 1 }, { "utc", DateTime.UtcNow.ToString("o") }, { "execution_context", context }, { "checks", checks.ToArray() }, { "summary", summary }, { "game_config_or_key_read", false }, { "security_settings_changed", false }, { "normal_desktop_outcome_inferred", false } };
            string report = WizardProvisioning.PathIn(outputRoot, "network-diagnostic-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
            File.WriteAllText(report, DataJson.Write(document), new UTF8Encoding(false)); return new NetworkDiagnosticResult { Document = document, Summary = summary, ReportPath = report };
        }
    }
    public sealed class NetworkDiagnosticForm : Form
    {
        readonly TextBox status = new TextBox(); readonly Button retry = new Button(), copy = new Button(); NetworkDiagnosticResult result;
        public NetworkDiagnosticForm()
        {
            Text = "LilithBridge · 无 Key 网络诊断"; Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(780, 500); StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 }; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); Controls.Add(layout);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "此入口自动检查官方 HTTPS，并下载约 1.5 MiB 的固定安装包作校验。\r\n不需要 Key，不选择游戏，也不会安装或更改系统设置。" }, 0, 0);
            status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical; status.Dock = DockStyle.Fill; layout.Controls.Add(status, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill }; retry.Text = "重新检查"; retry.AutoSize = true; copy.Text = "复制脱敏报告"; copy.AutoSize = true; buttons.Controls.Add(retry); buttons.Controls.Add(copy); layout.Controls.Add(buttons, 0, 2);
            retry.Click += async (s, e) => await Run(); copy.Click += (s, e) => { if (result != null) Clipboard.SetText(DataJson.Write(result.Document)); };
            Shown += async (s, e) => await Run();
        }
        async Task Run()
        {
            retry.Enabled = copy.Enabled = false; status.Text = "正在读取当前进程的运行环境…";
            try
            {
                result = await Task.Run(() => NetworkDiagnostics.Run(Path.Combine(TrustedDownloads.DefaultCache, "diagnostics"), message => { if (!IsDisposed) BeginInvoke(new Action(() => status.AppendText("\r\n" + message))); }));
                status.AppendText("\r\n\r\n" + result.Summary + "\r\n\r\n已保存脱敏报告：" + result.ReportPath);
            }
            catch (Exception error) { status.AppendText("\r\n诊断未完成：" + DataJson.Write(NetworkDiagnostics.Error(error))); }
            finally { if (!IsDisposed) { retry.Enabled = true; copy.Enabled = result != null; } }
        }
    }
}
