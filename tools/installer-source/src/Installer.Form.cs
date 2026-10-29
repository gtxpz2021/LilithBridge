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
        InstallManifest manifest; InstallPlan plan; RecoveryPlan recoveryPlan; readonly Button prepare = new Button(); string dependencyZip, dependencyTarget; LoaderPreparationPlan dependencyPlan;
        public string LastError { get; private set; }
        public InstallResult LastResult { get; private set; }
        public InstallerForm(string package, string initialRoot)
        {
            packageRoot = package;
            Text = "LilithBridge · 安装与配置"; Name = "LilithBridgeInstaller"; Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(850, 650); MinimumSize = new Size(780, 620);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 8 };
            foreach (int h in new[] { 48, 54, 44, 44, 0, 50, 44, 60 }) panel.RowStyles.Add(h == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, h));
            Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "选择游戏 → 检查改动 → 安装并配置", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 17, FontStyle.Bold) }, 0, 0);
            panel.Controls.Add(new Label { Text = "选择《不/存在的莉莉丝》的游戏目录，并先退出游戏。游戏和加载器需单独准备；本地召回为可选项。", Dock = DockStyle.Fill, AutoSize = true }, 0, 1);
            found.Name = "DiscoveredGames"; found.DropDownStyle = ComboBoxStyle.DropDownList; found.Dock = DockStyle.Fill;
            found.Items.Add("找到的游戏目录（请核对后选择）"); foreach (string candidate in InstallerCore.Discover(package)) found.Items.Add(candidate); found.SelectedIndex = 0;
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
            prepare.Name = "PrepareDependencies"; prepare.Text = "准备 BepInEx…"; prepare.AutoSize = true; foreach (var b in new[] { install, configure, recover, restore, prepare }) buttons.Controls.Add(b); panel.Controls.Add(buttons, 0, 6);
            panel.Controls.Add(new Label { Text = "安装只更新 LilithBridge 插件文件，配置、记忆和存档保持原位。\r\nAPI Key 及备份以明文保存在本机。保存设置不会发送 API 请求；游戏对话可能产生服务费用。", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray }, 0, 7);
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
            prepare.Click += delegate { Run(ReviewDependencies); }; confirm.CheckedChanged += delegate { prepare.Enabled = !UseWaitCursor && (dependencyPlan == null || confirm.Checked); }; recover.Click += delegate { Run(delegate { ReviewOrExecuteRecovery(true); }); };
            restore.Click += delegate { Run(delegate { ReviewOrExecuteRecovery(false); }); };
            if (!String.IsNullOrEmpty(initialRoot)) SelectRoot(initialRoot); else summary.Text = "请在 Steam 中选择“管理 → 浏览本地文件”，再选择打开的游戏目录并点击“检查”。\r\n如果需要准备游戏加载器，请点“准备 BepInEx…”选择已下载的官方 ZIP。";
        }
        void Run(Action action)
        {
            LastError = null; UseWaitCursor = true;
            foreach (var b in new[] { browse, inspect, install, configure, recover, restore, prepare }) b.Enabled = false;
            try { action(); }
            catch (Exception error) { LastError = InstallerCore.Explain(error); ResetRecovery(); summary.Text = "操作未完成：" + LastError + "\r\n若提示恢复，请先预览对应恢复操作。不要强制覆盖冲突文件。"; }
            finally { UseWaitCursor = false; foreach (var b in new[] { browse, inspect, configure, recover, restore, prepare }) b.Enabled = true; install.Enabled = plan != null; prepare.Enabled = dependencyPlan == null || confirm.Checked; }
        }
        void ResetRecovery() { dependencyZip = null; dependencyPlan = null; prepare.Text = "准备 BepInEx…"; recoveryPlan = null; confirm.Checked = false; recover.Text = "预览中断恢复"; restore.Text = "预览最近回退"; }
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
        static string UserHostSummary(HostCapabilityReport host, string directory)
        {
            string title, next;
            switch (host.State)
            {
                case "NeedsLoader": title = "需要准备游戏加载器"; next = "点“准备 BepInEx…”并选择已下载的官方 ZIP，核对新增文件后确认准备。"; break;
                case "PendingFirstRun": title = "需要首次启动游戏"; next = "关闭安装器，启动下方目录中的游戏。等待加载器准备完成后退出，再回来点“检查”。"; break;
                case "PendingRegeneration": title = "需要更新游戏运行所需文件"; next = "启动下方目录中的游戏，等待加载器完成准备；退出游戏后回来点“检查”。"; break;
                case "NeedsLoaderConfiguration": title = "游戏加载器配置需要调整"; next = "现有加载器未启用或目标不正确。请保留现有配置，并按加载器说明调整；不确定时可反馈检查代码。"; break;
                case "ConfirmedIncompatible": title = "当前游戏或加载器缺少插件所需的运行条件"; next = "先不要覆盖现有文件。请保留下方检查详情反馈，以便确认需要调整的组件。"; break;
                case "NeedsHostReview": title = "暂时无法确认游戏类型"; next = "请选择《不/存在的莉莉丝》的安装目录。若目录正确，请保留检查代码反馈。"; break;
                default: title = "请选择游戏目录"; next = "目录中应包含 Lilith.exe。可在 Steam 中用“管理 → 浏览本地文件”找到它。"; break;
            }
            string text = title + "\r\n游戏目录：" + directory + "\r\n\r\n下一步：" + next + "\r\n\r\n检查代码：" + host.State;
            if (host.Missing.Count > 0) text += "\r\n缺少的文件：\r\n" + String.Join("\r\n", host.Missing.ToArray());
            if (host.ContractFailures.Count > 0) text += "\r\n接口检查详情：\r\n" + String.Join("\r\n", host.ContractFailures.ToArray());
            else if (host.State == "ConfirmedIncompatible") text += "\r\n详细原因：" + host.Detail;
            return text;
        }
        public void InspectSelection()
        {
            plan = null; ResetRecovery(); confirm.Text = "我已核对上方安装改动，允许执行当前安装。";
            Run(delegate
            {
                manifest = InstallManifest.Load(packageRoot); var host = HostCapabilities.Inspect(root.Text, Path.Combine(packageRoot, @"payload\LilithBridge.dll")); if (!host.CanInstall) { summary.Text = UserHostSummary(host, root.Text); return; }
                plan = InstallerCore.Inspect(manifest, root.Text);
                string latest = InstallerCore.LatestCommitted(plan.GameRoot);
                summary.Text = "目标：" + plan.GameRoot + "\r\n独立数据目录：" + plan.DataRoot + "\r\n\r\n将进行的改动：\r\n" + String.Join("\r\n", plan.Changes.ToArray()) + "\r\n\r\n不读取或迁移旧记忆/原生存档。缺模型可先保存 API 配置。\r\n最近可回退事务：" + (latest ?? "无");
            });
        }
        void ReviewDependencies()
        {
            plan = null;
            if (dependencyPlan == null)
            {
                var host = HostCapabilities.Inspect(root.Text, Path.Combine(packageRoot, @"payload\LilithBridge.dll"));
                SetupCore.Require(host.BasicHost, "LBINSTALL_SELECT_CORRECT_LILITH_HOST_FIRST");
                ResetRecovery();
                using (var dialog = new OpenFileDialog { Title = "选择官方 BepInEx IL2CPP x64 离线 ZIP", Filter = "ZIP 包|*.zip", CheckFileExists = true })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) RefreshDependencyPreview(dialog.FileName, false);
                }
                return;
            }
            SetupCore.Require(confirm.Checked && SetupCore.Same(root.Text, dependencyPlan.GameRoot), "LBINSTALL_REVIEW_AND_CONFIRM_FIRST");
            try
            {
                var result = LoaderPreparation.ExecuteZip(dependencyPlan);
                ResetRecovery();
                summary.Text = "游戏加载器已准备好。\r\n新增 " + result.Added.Count + " 个文件；已有相同文件 " + result.Existing.Count + " 个。\r\n" + String.Join("\r\n", result.Conflicts.ToArray()) + "\r\n下一步：关闭安装器，启动这个目录中的游戏，等待准备完成后退出。然后回来点“检查”并安装插件。";
            }
            catch (SetupException error)
            {
                if (error.Message != "LBINSTALL_DEPENDENCY_PREVIEW_CHANGED_RECHECK") throw;
                string previousZip = dependencyZip;
                ResetRecovery();
                RefreshDependencyPreview(previousZip, true);
            }
        }
        void RefreshDependencyPreview(string zip, bool changed)
        {
            var preview = LoaderPreparation.PreviewZip(zip, root.Text, Path.Combine(packageRoot, "loader-files.json"));
            if (preview.Conflicts.Count > 0) { summary.Text = "目标文件状态已变化或存在差异，本次未新增文件。已有文件保持不变：\r\n" + String.Join("\r\n", preview.Conflicts.ToArray()); return; }
            dependencyPlan = preview.Plan; dependencyZip = zip; dependencyTarget = root.Text;
            confirm.Text = "我已核对这次前置新增文件，允许准备到这个游戏目录。"; confirm.Checked = false; prepare.Text = "确认准备依赖";
            summary.Text = (changed ? "目标或来源状态已变化，已刷新预览；尚未执行，请重新核对。\r\n" : "") + "游戏加载器准备预览\r\n目标：" + dependencyTarget + "\r\n将新增：\r\n" + String.Join("\r\n", preview.Added.ToArray()) + "\r\n已有相同文件：" + preview.Existing.Count + "\r\n已有文件保持不变。插件回退不会删除这些共享加载器文件。请核对目录与清单，勾选确认后继续。";
        }

        void OpenConfiguration()
        {
            SetupCore.Inspect(root.Text); // Explicit identity and isolated-root gate before form opens.
            using (var setup = new SetupForm(root.Text, Path.Combine(packageRoot, "model-manifest.json"))) setup.ShowDialog(this);
        }
    }
}
