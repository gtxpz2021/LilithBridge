using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace LilithSetup
{
    public sealed class InstallerForm : Form
    {
        readonly string packageRoot;
        readonly TextBox root = new TextBox(), summary = new TextBox();
        readonly ComboBox found = new ComboBox();
        readonly Button browse = new Button(), inspect = new Button(), install = new Button(), configure = new Button(), recover = new Button(), restore = new Button();
        readonly CheckBox confirm = new CheckBox();
        InstallManifest manifest; InstallPlan plan; RecoveryPlan recoveryPlan;
        public string LastError { get; private set; }
        public InstallResult LastResult { get; private set; }
        public InstallerForm(string package, string initialRoot)
        {
            packageRoot = package;
            Text = "LilithBridge · 便捷安装候选"; Name = "LilithBridgeInstaller"; Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(850, 650); MinimumSize = new Size(780, 620);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 8 };
            foreach (int h in new[] { 48, 54, 44, 44, 0, 50, 44, 60 }) panel.RowStyles.Add(h == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, h));
            Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "选择游戏 → 检查改动 → 安装并配置", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 17, FontStyle.Bold) }, 0, 0);
            panel.Controls.Add(new Label { Text = "请选择你自有的合法游戏安装目录，并先退出游戏。此候选不附带游戏、BepInEx、模型或 Python。", Dock = DockStyle.Fill, AutoSize = true }, 0, 1);
            found.Name = "DiscoveredGames"; found.DropDownStyle = ComboBoxStyle.DropDownList; found.Dock = DockStyle.Fill;
            found.Items.Add("自动发现的候选目录（请核对后选择）"); foreach (string candidate in InstallerCore.Discover(package)) found.Items.Add(candidate); found.SelectedIndex = 0;
            found.SelectedIndexChanged += delegate { if (found.SelectedIndex > 0) SelectRoot((string)found.SelectedItem); };
            panel.Controls.Add(found, 0, 2);
            root.Name = "InstallGameRoot"; root.Dock = DockStyle.Fill; root.ReadOnly = true;
            browse.Text = "选择游戏目录…"; browse.Name = "BrowseGame"; browse.AutoSize = true;
            inspect.Text = "检查"; inspect.Name = "InspectInstall"; inspect.AutoSize = true;
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(root, 0, 0); row.Controls.Add(browse, 1, 0); row.Controls.Add(inspect, 2, 0); panel.Controls.Add(row, 0, 3);
            summary.Name = "InstallSummary"; summary.Multiline = true; summary.ReadOnly = true; summary.ScrollBars = ScrollBars.Vertical; summary.Dock = DockStyle.Fill; summary.BackColor = Color.White;
            panel.Controls.Add(summary, 0, 4);
            confirm.Name = "ConfirmInstall"; confirm.Text = "我已核对目录及下列文件改动，允许执行当前安装或恢复操作。"; confirm.Dock = DockStyle.Fill;
            panel.Controls.Add(confirm, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            install.Text = "安装并打开配置"; install.Name = "InstallNow"; install.AutoSize = true; install.Enabled = false;
            configure.Text = "仅打开配置器"; configure.Name = "ConfigureOnly"; configure.AutoSize = true;
            recover.Text = "预览中断恢复"; recover.Name = "RecoverInterrupted"; recover.AutoSize = true;
            restore.Text = "预览最近回退"; restore.Name = "RestorePrevious"; restore.AutoSize = true;
            foreach (var b in new[] { install, configure, recover, restore }) buttons.Controls.Add(b); panel.Controls.Add(buttons, 0, 6);
            panel.Controls.Add(new Label { Text = "升级仅替换清单中的自有插件。配置、记忆、原生存档保持原位。\r\nAPI Key 由你在配置器主动保存；文件和备份为本机明文。工具不会联网或启动游戏。", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray }, 0, 7);
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "选择包含 Lilith.exe 的目录", ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) SelectRoot(dialog.SelectedPath); };
            inspect.Click += delegate { InspectSelection(); };
            install.Click += delegate { Run(delegate
            {
                SetupCore.Require(plan != null, "LBINSTALL_CHECK_FIRST"); LastResult = InstallerCore.Install(manifest, plan, confirm.Checked); confirm.Checked = false;
                string outcome = LastResult.NoChange ? "版本相同，未覆盖文件。" : "插件已安装并保留备份。"; plan = null; summary.Text = outcome;
                try { OpenConfiguration(); }
                catch (Exception error) { LastError = InstallerCore.Explain(error); summary.Text = outcome + "\r\n配置器未打开：" + LastError + "\r\n安装状态已提交；请核对配置器支持的插件身份后使用“仅打开配置器”，不要当作未安装重复覆盖。"; }
            }); };
            configure.Click += delegate { Run(OpenConfiguration); };
            recover.Click += delegate { Run(delegate { ReviewOrExecuteRecovery(true); }); };
            restore.Click += delegate { Run(delegate { ReviewOrExecuteRecovery(false); }); };
            if (!String.IsNullOrEmpty(initialRoot)) SelectRoot(initialRoot); else summary.Text = "未发现目标时，请在 Steam 中选择“管理 → 浏览本地文件”，再选择该目录。\r\n当前随附 manifest 为兼容性待核模板，真实安装会明确停止。";
        }
        void Run(Action action)
        {
            LastError = null; UseWaitCursor = true;
            foreach (var b in new[] { browse, inspect, install, configure, recover, restore }) b.Enabled = false;
            try { action(); }
            catch (Exception error) { LastError = InstallerCore.Explain(error); ResetRecovery(); summary.Text = "操作未完成：" + LastError + "\r\n若提示恢复，请先预览对应恢复操作。不要强制覆盖冲突文件。"; }
            finally { UseWaitCursor = false; foreach (var b in new[] { browse, inspect, configure, recover, restore }) b.Enabled = true; install.Enabled = plan != null; }
        }
        void ResetRecovery() { recoveryPlan = null; confirm.Checked = false; recover.Text = "预览中断恢复"; restore.Text = "预览最近回退"; }
        void ReviewOrExecuteRecovery(bool pendingOnly)
        {
            plan = null;
            if (recoveryPlan == null || recoveryPlan.PendingOnly != pendingOnly || !SetupCore.Same(recoveryPlan.GameRoot, root.Text))
            {
                ResetRecovery(); recoveryPlan = InstallerCore.PreviewRecovery(root.Text, pendingOnly);
                confirm.Text = pendingOnly ? "我已核对上方中断恢复事务及文件操作，允许执行这次恢复。" : "我已核对上方最近回退事务及文件操作，允许执行这次回退。";
                summary.Text = (pendingOnly ? "中断恢复预览" : "最近安装回退预览") + "\r\n目标：" + recoveryPlan.GameRoot + "\r\n\r\n" + String.Join("\r\n", recoveryPlan.Changes) + "\r\n\r\n请核对具体事务和删除/恢复动作，再勾选确认并点击同一按钮。配置、记忆和存档不在本次清单内。";
                if (pendingOnly) recover.Text = "确认中断恢复"; else restore.Text = "确认最近回退";
                return;
            }
            SetupCore.Require(confirm.Checked, "LBINSTALL_REVIEW_AND_CONFIRM_FIRST");
            int count = InstallerCore.ExecuteRecovery(recoveryPlan, true); ResetRecovery();
            summary.Text = "已按预览处理 " + count + " 个事务。用户配置、记忆及存档未改动。请重新检查。";
        }
        public void SelectRoot(string path) { root.Text = path; plan = null; ResetRecovery(); InspectSelection(); }
        public void InspectSelection()
        {
            plan = null; ResetRecovery(); confirm.Text = "我已核对上方安装改动，允许执行当前安装。";
            Run(delegate
            {
                manifest = InstallManifest.Load(packageRoot);
                plan = InstallerCore.Inspect(manifest, root.Text);
                string latest = InstallerCore.LatestCommitted(plan.GameRoot);
                summary.Text = "目标：" + plan.GameRoot + "\r\n独立数据目录：" + plan.DataRoot + "\r\n\r\n将进行的改动：\r\n" + String.Join("\r\n", plan.Changes.ToArray()) + "\r\n\r\n不读取或迁移旧记忆/原生存档。缺模型可先保存 API 配置。\r\n最近可回退事务：" + (latest ?? "无");
            });
        }
        void OpenConfiguration()
        {
            SetupCore.Inspect(root.Text); // Explicit identity and isolated-root gate before form opens.
            using (var setup = new SetupForm(root.Text, Path.Combine(packageRoot, "model-manifest.json"))) setup.ShowDialog(this);
        }
    }
}
