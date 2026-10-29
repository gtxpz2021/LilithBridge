using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;

namespace LilithSetup
{
    public sealed class WizardForm : Form
    {
        readonly string package;
        readonly TextBox root = new TextBox(), key = new TextBox(), endpoint = new TextBox(), status = new TextBox(), offline = new TextBox();
        readonly ComboBox games = new ComboBox(), provider = new ComboBox(), model = new ComboBox(), source = new ComboBox();
        readonly CheckBox plugin = new CheckBox(), loader = new CheckBox(), recall = new CheckBox(), configure = new CheckBox(), network = new CheckBox(), confirm = new CheckBox();
        readonly Button browse = new Button(), inspect = new Button(), start = new Button(), stop = new Button(), update = new Button(), advanced = new Button(), offlineBrowse = new Button(), recover = new Button(), rollback = new Button();
        readonly Label data = new Label(), keyState = new Label(), providerNote = new Label(), plan = new Label();
        readonly ProgressBar progress = new ProgressBar(); readonly ListView stages = new ListView();
        readonly TableLayoutPanel settings = new TableLayoutPanel(), layout = new TableLayoutPanel(); readonly Panel advancedPanel = new Panel();
        CancellationTokenSource cancellation; bool busy, loading, providerEdited, prepared; string expectedDigest = "MISSING", planSummary = ""; ReleaseUpdate release; RecoveryPlan recovery;
        readonly System.Windows.Forms.Timer planTimer = new System.Windows.Forms.Timer { Interval = 500 };
        volatile bool commitInProgress;
        public string LastState { get; private set; }
        public string LastError { get; private set; }
        public WizardForm(string packageRoot, string initialRoot) : this(packageRoot, initialRoot, null) { }
        public WizardForm(string packageRoot, string initialRoot, WizardOptions resume)
        {
            package = packageRoot; Text = "LilithBridge · 安装与更新"; Name = "LilithBridgeUnifiedWizard";
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(1040, 880); MinimumSize = new Size(920, 850); StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(246, 248, 252);
            layout.Dock = DockStyle.Fill; layout.Padding = new Padding(22, 16, 22, 14); layout.ColumnCount = 1; layout.RowCount = 10;
            foreach (int height in new[] { 58, 34, 36, 220, 28, 0, 34, 76, 32, 44 }) layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height)); Controls.Add(layout);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); heading.Controls.Add(new Label { Text = "把需要的组件一次准备好", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 19, FontStyle.Bold) });
            heading.Controls.Add(new Label { Text = "选择游戏和组件，填入自己的 Key，点击开始。已有设置会保留，缺少的组件会自动准备。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(75, 87, 103) }); layout.Controls.Add(heading, 0, 0);
            games.Name = "DiscoveredGames"; games.DropDownStyle = ComboBoxStyle.DropDownList; games.Dock = DockStyle.Fill; games.Items.Add("已找到的游戏目录（请核对后选择）");
            foreach (var item in InstallerCore.Discover(package)) games.Items.Add(item); games.SelectedIndex = 0; layout.Controls.Add(games, 0, 1);
            root.Name = "GameRoot"; root.ReadOnly = true; root.Dock = DockStyle.Fill; browse.Text = "选择游戏目录…"; inspect.Text = "重新读取"; browse.AutoSize = inspect.AutoSize = true;
            layout.Controls.Add(Line(root, browse, inspect), 0, 2);
            settings.Dock = DockStyle.Fill; settings.ColumnCount = 2; settings.RowCount = 1; settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56)); layout.Controls.Add(settings, 0, 3);
            var components = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Color.White, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 3, 10, 3) };
            foreach (int h in new[] { 25, 29, 29, 38, 29, 40 }) components.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            components.Controls.Add(new Label { Text = "安装与更新组件", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            plugin.Text = "LilithBridge 插件（自动备份旧版）"; loader.Text = "缺少时自动准备游戏加载器"; recall.Text = "本地召回：Python、环境、模型并启用\r\n约 0.77 GiB 下载，建议预留 8 GiB 空间"; configure.Text = "保存下方服务设置（已有 Key 留空保留）";
            plugin.Checked = loader.Checked = configure.Checked = true;
            foreach (var box in new[] { plugin, loader, recall, configure }) { box.Dock = DockStyle.Fill; box.Margin = new Padding(0); }
            plugin.Name = "SelectPlugin"; loader.Name = "SelectLoader"; recall.Name = "SelectRecall"; configure.Name = "SelectConfiguration";
            components.Controls.Add(plugin, 0, 1); components.Controls.Add(loader, 0, 2); components.Controls.Add(recall, 0, 3); components.Controls.Add(configure, 0, 4);
            source.Name = "PluginSource"; source.DropDownStyle = ComboBoxStyle.DropDownList; source.Dock = DockStyle.Fill; source.Items.AddRange(new object[] { "插件来源：安装包内的已核验版本", "插件来源：官方发行下载" }); source.SelectedIndex = 0; components.Controls.Add(source, 0, 5); settings.Controls.Add(components, 0, 0);
            var service = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, BackColor = Color.White, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 3, 0, 3) };
            service.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); service.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            foreach (int h in new[] { 25, 34, 28, 32, 30, 48 }) service.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            service.Controls.Add(new Label { Text = "对话服务", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0); service.SetColumnSpan(service.GetControlFromPosition(0, 0), 2);
            provider.Name = "Provider"; provider.DropDownStyle = ComboBoxStyle.DropDownList; provider.Dock = DockStyle.Fill; provider.Items.AddRange(new object[] { "DeepSeek 官方", "自定义服务（高级）" });
            model.Name = "ApiModel"; model.Dock = DockStyle.Fill; model.DropDownStyle = ComboBoxStyle.DropDown; model.Items.AddRange(ProviderPresets.Models); model.Text = ProviderPresets.DefaultModel;
            service.Controls.Add(provider, 0, 1); service.Controls.Add(model, 1, 1);
            service.Controls.Add(new Label { Text = "API Key（仅保存在所选数据目录）", Dock = DockStyle.Fill }, 0, 2); service.SetColumnSpan(service.GetControlFromPosition(0, 2), 2);
            key.Name = "ApiKey"; key.UseSystemPasswordChar = true; key.MaxLength = 4096; key.Dock = DockStyle.Fill; service.Controls.Add(key, 0, 3); service.SetColumnSpan(key, 2);
            keyState.Dock = DockStyle.Fill; keyState.ForeColor = Color.FromArgb(75, 87, 103); service.Controls.Add(keyState, 0, 4); service.SetColumnSpan(keyState, 2);
            endpoint.Name = "CustomEndpoint"; endpoint.Dock = DockStyle.Fill; endpoint.Text = ProviderPresets.DeepSeekUrl;
            providerNote.Dock = DockStyle.Fill; providerNote.ForeColor = Color.FromArgb(75, 87, 103); service.Controls.Add(providerNote, 0, 5); service.SetColumnSpan(providerNote, 2); settings.Controls.Add(service, 1, 0);
            data.Name = "DataRoot"; data.Dock = DockStyle.Fill; data.AutoEllipsis = true; data.Text = "配置与记忆目录：选择游戏后自动确定；召回环境使用本机产品专用目录"; layout.Controls.Add(data, 0, 4);
            stages.Name = "InstallationStages"; stages.Dock = DockStyle.Fill; stages.View = View.Details; stages.FullRowSelect = true; stages.HeaderStyle = ColumnHeaderStyle.Nonclickable; stages.Columns.Add("组件", 120); stages.Columns.Add("状态", 120); stages.Columns.Add("现在的进度 / 下一步", 670);
            foreach (string name in new[] { "游戏检查", "游戏加载器", "插件", "召回环境", "本地模型", "离线验证", "配置" }) stages.Items.Add(new ListViewItem(new[] { name, "等待", "" })); layout.Controls.Add(stages, 0, 5);
            var advancedRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; advancedRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); advancedRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            advanced.Text = "离线包 / 自定义地址 ▾"; advanced.AutoSize = true; network.Text = "允许从官方来源下载缺少的文件"; network.Checked = true; network.AutoSize = true;
            var toggles = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; toggles.Controls.Add(advanced); toggles.Controls.Add(network); advancedRow.Controls.Add(toggles, 0, 0);
            advancedPanel.Dock = DockStyle.Fill; advancedPanel.Visible = false; offline.Name = "OfflineCache"; offline.Dock = DockStyle.Fill; offline.ReadOnly = true; offlineBrowse.Text = "选择离线包目录…"; offlineBrowse.AutoSize = true;
            var advancedFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); advancedFields.Controls.Add(Line(offline, offlineBrowse), 0, 0); advancedFields.Controls.Add(endpoint, 1, 0); advancedPanel.Controls.Add(advancedFields); advancedRow.Controls.Add(advancedPanel, 0, 1); layout.Controls.Add(advancedRow, 0, 6);
            status.Name = "Status"; status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical; status.Dock = DockStyle.Fill; status.BackColor = Color.White; status.Text = "请选择游戏目录。默认安装插件与所需加载器；本地召回可选。\r\n游戏首次准备完成前会停在等待状态，按提示主动启动游戏并退出后继续。"; layout.Controls.Add(status, 0, 7);
            confirm.Name = "ConfirmSelection"; confirm.Text = "我已核对目标目录和勾选组件，允许安装并保存这些设置。"; confirm.Dock = DockStyle.Fill; layout.Controls.Add(confirm, 0, 8);
            start.Text = "开始安装"; start.Name = "StartInstallation"; start.Width = 132; start.Height = 34; start.BackColor = Color.FromArgb(35, 97, 177); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
            stop.Text = "停止"; stop.Width = 70; stop.Height = 34; stop.Enabled = false; update.Text = "检查项目更新"; recover.Text = "中断恢复"; rollback.Text = "回退上次插件";
            foreach (var button in new[] { update, recover, rollback }) { button.AutoSize = true; button.Height = 34; }
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false }; footer.Controls.Add(start); footer.Controls.Add(stop); footer.Controls.Add(update); footer.Controls.Add(rollback); footer.Controls.Add(recover);
            progress.Width = 115; progress.Height = 27; footer.Controls.Add(progress); layout.Controls.Add(footer, 0, 9);
            plan.Name = "PlanSummary";
            browse.Click += async (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "选择包含 Lilith.exe 的游戏目录", ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) await SelectRoot(dialog.SelectedPath); };
            games.SelectedIndexChanged += async (s, e) => { if (!loading && games.SelectedIndex > 0) await SelectRoot((string)games.SelectedItem); };
            inspect.Click += async (s, e) => { if (root.Text.Length != 0) await SelectRoot(root.Text); };
            start.Click += async (s, e) => await BeginInstall(); stop.Click += (s, e) => { if (commitInProgress) { status.Text = "已进入最终配置提交，此短步骤完成前不能取消。"; return; } if (cancellation != null) { cancellation.Cancel(); status.Text = "正在停止。下载会立即停止；环境安装将完成当前操作后停止，避免留下仍在写文件的子进程。"; } };
            advanced.Click += (s, e) => { advancedPanel.Visible = !advancedPanel.Visible; layout.RowStyles[6].Height = advancedPanel.Visible ? 66 : 34; advanced.Text = advancedPanel.Visible ? "离线包 / 自定义地址 ▴" : "离线包 / 自定义地址 ▾"; };
            offlineBrowse.Click += async (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "选择离线包根目录（含 wheelhouse、model 与官方安装包）", ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) { offline.Text = dialog.SelectedPath; await RefreshPlan(); } };
            provider.SelectedIndexChanged += (s, e) => { if (loading) return; providerEdited = true; key.Clear(); if (provider.SelectedIndex == 0) { loading = true; endpoint.Text = ProviderPresets.DeepSeekUrl; model.Items.Clear(); model.Items.AddRange(ProviderPresets.Models); model.Text = ProviderPresets.DefaultModel; loading = false; } else { advancedPanel.Visible = true; layout.RowStyles[6].Height = 66; } ProviderNote(); Changed(); };
            model.TextChanged += (s, e) => { if (!loading) { providerEdited = true; ProviderNote(); Changed(); } };
            endpoint.TextChanged += (s, e) => { if (!loading) { key.Clear(); providerEdited = true; Changed(); } };
            key.TextChanged += (s, e) => { if (!loading) Changed(); };
            foreach (var box in new[] { plugin, loader, recall, configure, network }) box.CheckedChanged += (s, e) => { if (!loading) { if (recall.Checked) configure.Checked = true; Changed(true); } };
            source.SelectedIndexChanged += (s, e) => { if (!loading) Changed(true); };
            planTimer.Tick += async (s, e) => { planTimer.Stop(); if (!busy) await RefreshPlan(); };
            confirm.CheckedChanged += (s, e) => SetButtons(); update.Click += async (s, e) => await CheckUpdate();
            recover.Click += (s, e) => Recovery(true); rollback.Click += (s, e) => Recovery(false);
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; if (commitInProgress) status.Text = "正在完成最终配置提交，结束后即可关闭。"; else { if (cancellation != null) cancellation.Cancel(); status.Text = "正在结束当前操作。完成后即可关闭；已完成的组件会保留。"; } } else key.Clear(); };
            FormClosed += (s, e) => planTimer.Dispose();
            loading = true; provider.SelectedIndex = 0; loading = false; ProviderNote(); keyState.Text = "已有 Key 不回显；新安装请填写自己的 Key。"; SetButtons();
            Shown += async (s, e) =>
            {
                if (String.IsNullOrEmpty(initialRoot)) return;
                await SelectRoot(initialRoot);
                if (resume == null) return;
                loading = true; plugin.Checked = resume.Plugin; loader.Checked = resume.Loader; recall.Checked = resume.Runtime; configure.Checked = resume.Configure; network.Checked = resume.Network; offline.Text = resume.OfflineRoot; release = resume.Update; source.SelectedIndex = resume.DownloadPlugin ? 1 : 0; loading = false;
                await RefreshPlan();
                if (resume.FromHandoff && prepared)
                {
                    confirm.Checked = true; start.Text = "继续已确认安装";
                    status.Text += "\r\n新版安装器已接续已确认的组件选择。";
                    if (resume.LostHandoffDraft) status.Text += "未保存的 Key 或服务草稿没有传递，请重新填写后继续。";
                    else await BeginInstall();
                }
                else status.Text += "\r\n已恢复上次组件选择。未保存的 Key 请重新填写，核对后继续。";
            };
        }
        static Control Line(Control main, params Control[] buttons)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 + buttons.Length, RowCount = 1, Margin = Padding.Empty }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.Controls.Add(main, 0, 0);
            for (int i = 0; i < buttons.Length; i++) { row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.Controls.Add(buttons[i], i + 1, 0); } return row;
        }
        void ProviderNote()
        { endpoint.ReadOnly = provider.SelectedIndex == 0; providerNote.Text = provider.SelectedIndex == 0 ? ProviderPresets.ModelNote(model.Text) : "高级服务需兼容本项目请求格式。更换服务时请填写对应 Key。"; }
        void Changed(bool replan = false)
        { confirm.Checked = false; confirm.Text = "我已核对目标目录和勾选组件，允许安装并保存这些设置。"; recovery = null; recover.Text = "中断恢复"; rollback.Text = "回退上次插件"; if (prepared) status.Text = planSummary; if (replan) { prepared = false; planTimer.Stop(); planTimer.Start(); } SetButtons(); }
        void SetButtons()
        {
            foreach (Control control in new Control[] { browse, inspect, games, plugin, loader, recall, configure, network, source, provider, model, key, endpoint, offlineBrowse, update, recover, rollback, confirm, advanced }) control.Enabled = !busy;
            start.Enabled = !busy && prepared && confirm.Checked; stop.Enabled = busy && !commitInProgress;
        }
        async Task SelectRoot(string path)
        {
            root.Text = path; expectedDigest = "MISSING"; prepared = false; confirm.Checked = false; confirm.Text = "我已核对目标目录和勾选组件，允许安装并保存这些设置。"; recovery = null; key.Clear(); LastError = null;
            loading = true; provider.SelectedIndex = 0; model.Text = ProviderPresets.DefaultModel; endpoint.Text = ProviderPresets.DeepSeekUrl; loading = false; providerEdited = false;
            try
            {
                string absolute = SetupCore.SafePath(path, null); SetupCore.NoReparse(absolute); root.Text = absolute;
                CopyContext selectedContext = File.Exists(Path.Combine(absolute, InstallManifest.Plugin)) ? SetupCore.Inspect(absolute) : null;
                string retainedRoot = selectedContext == null ? InstallerCore.FindRetainedDataRoot(absolute) : null;
                if (selectedContext != null || retainedRoot != null)
                {
                    string selectedData = selectedContext == null ? retainedRoot : selectedContext.DataRoot; var snapshot = SetupCore.ReadSnapshot(Path.Combine(selectedData, "config.json")); expectedDigest = snapshot.Digest;
                    var current = DataJson.EffectiveProvider(snapshot.Document); loading = true;
                    if (snapshot.Exists) { endpoint.Text = DataJson.String(current, "base_url", ProviderPresets.DeepSeekUrl); model.Text = DataJson.String(current, "model", ProviderPresets.DefaultModel); provider.SelectedIndex = ProviderPresets.Matches(endpoint.Text, model.Text) ? 0 : 1; }
                    loading = false; data.Text = "配置与记忆目录：" + selectedData; keyState.Text = String.IsNullOrEmpty(DataJson.String(current, "api_key", "")) ? "当前 Key 未设置。" : "当前 Key 已设置；留空保留，不回显。";
                }
                else { data.Text = "配置与记忆目录：" + Path.Combine(absolute, "LilithBridgeData"); keyState.Text = "新安装：请填写自己的 API Key，也可稍后填写。"; }
                ProviderNote(); await RefreshPlan();
            }
            catch (Exception error) { loading = false; LastError = WizardCore.Explain(error); status.Text = LastError; SetButtons(); }
        }
        WizardOptions CaptureOptions()
        { return new WizardOptions { PackageRoot = package, GameRoot = root.Text, OfflineRoot = offline.Text, ExpectedConfigDigest = expectedDigest, Plugin = plugin.Checked, Loader = loader.Checked, Runtime = recall.Checked, Model = recall.Checked, EnableRecall = recall.Checked, Configure = configure.Checked, Network = network.Checked, DownloadPlugin = source.SelectedIndex == 1, Update = release, BaseUrl = endpoint.Text, ApiModel = model.Text, ApiKey = key.Text, ProviderEdited = providerEdited, Confirm = confirm.Checked }; }
        async Task RefreshPlan()
        {
            if (root.Text.Length == 0) return; busy = true; SetButtons(); var options = CaptureOptions(); status.Text = "正在检查已有文件与预计下载量…";
            try { string summary = await Task.Run(() => WizardPlanning.Preview(options)); prepared = true; planSummary = summary; status.Text = summary; }
            catch (Exception error) { prepared = false; LastError = WizardCore.Explain(error); status.Text = LastError; }
            finally { busy = false; SetButtons(); }
        }
        void ShowProgress(WizardProgress item)
        {
            if (item.State == "committing") commitInProgress = true;
            if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(new Action<WizardProgress>(ShowProgress), item); return; }
            progress.Value = Math.Max(0, Math.Min(100, item.Percent)); stop.Enabled = busy && !commitInProgress;
            string[] keys = { "check", "loader", "plugin", "runtime", "model", "verify", "config" }; int at = Array.IndexOf(keys, item.Step);
            if (at >= 0) { stages.Items[at].SubItems[1].Text = item.State == "done" ? "已完成" : item.State == "waiting" ? "等待操作" : "进行中"; stages.Items[at].SubItems[2].Text = item.Message; }
            status.Text = item.Message;
        }
        async Task BeginInstall()
        {
            if (recovery != null) { Recovery(recovery.PendingOnly); return; }
            var options = CaptureOptions(); bool switchInstaller = false; commitInProgress = false; busy = true; cancellation = new CancellationTokenSource(); SetButtons(); LastError = null;
            foreach (ListViewItem item in stages.Items) { item.SubItems[1].Text = "等待"; item.SubItems[2].Text = ""; }
            if (!options.Runtime) foreach (int at in new[] { 3, 4, 5 }) { stages.Items[at].SubItems[1].Text = "保持现状"; stages.Items[at].SubItems[2].Text = "未选择变更本地召回"; }
            if (!options.Configure) { stages.Items[6].SubItems[1].Text = "保持现状"; stages.Items[6].SubItems[2].Text = "未选择保存服务设置"; }
            try
            {
                // The same confirmed selection is revalidated, including cache sizes, immediately before applying.
                WizardSession.Save(options, "Applying");
                await Task.Run(() => WizardPlanning.Preview(options));
                WizardResult result = await Task.Run(() => WizardCore.Execute(options, ShowProgress, cancellation.Token)); LastState = result.State; status.Text = result.Message;
                WizardSession.Save(options, result.State);
                if (result.State == "InstallerHandoffReady") { InstallerHandoff.LaunchHelper(package, result.HandoffPlanPath); loading = true; key.Clear(); loading = false; switchInstaller = true; }
                else if (result.State == "WaitingForGame" || result.State == "WaitingForExit") start.Text = "继续安装";
                else { start.Text = "再次检查 / 应用"; loading = true; key.Clear(); loading = false; if (result.DataRoot != null) expectedDigest = SetupCore.ReadSnapshot(Path.Combine(result.DataRoot, "config.json")).Digest; providerEdited = false; progress.Value = 100; }
            }
            catch (Exception error) { LastState = "Incomplete"; LastError = WizardCore.Explain(error); status.Text = "所选流程尚未完成。\r\n" + LastError; start.Text = "重新检查并继续"; try { WizardSession.Save(options, "Incomplete"); } catch { } }
            finally { options.ApiKey = ""; cancellation.Dispose(); cancellation = null; busy = false; commitInProgress = false; SetButtons(); }
            if (switchInstaller) Close();
        }
        async Task CheckUpdate()
        {
            busy = true; cancellation = new CancellationTokenSource(); SetButtons(); status.Text = "正在检查官方项目发行…";
            try
            {
                release = await Task.Run(() => WizardCore.CheckUpdate(cancellation.Token));
                string stage = ""; string cached = TrustedDownloads.Find(release.Asset, new[] { TrustedDownloads.DefaultCache });
                if (cached != null) stage = WizardCore.StagePlugin(cached, release.Asset, TrustedDownloads.DefaultCache);
                bool same = stage.Length != 0 && InstallManifest.Load(stage).PluginSha256 == InstallManifest.Load(package).PluginSha256;
                loading = true; source.SelectedIndex = 1; plugin.Checked = true; loading = false; confirm.Checked = false;
                status.Text = "官方发行：" + release.Tag + " / " + release.Asset.Name + "\r\n" + (same || release.Asset.Hash == TrustedDownloads.PluginRelease.Hash ? "当前插件与这个已核验发行一致。" : "选择已切换到该发行。核对目标并勾选确认后，点击开始下载并应用；配置和记忆保持原位。") + "\r\n插件失败可从本窗口回退。安装器自身不会在运行中被覆盖。";
            }
            catch (Exception error) { status.Text = WizardCore.Explain(error); }
            finally { cancellation.Dispose(); cancellation = null; busy = false; SetButtons(); }
        }
        void Recovery(bool pending)
        {
            try
            {
                if (recovery == null || recovery.PendingOnly != pending) { recovery = InstallerCore.PreviewRecovery(root.Text, pending); confirm.Checked = false; confirm.Text = "我已核对下方恢复清单，允许执行这次恢复。"; status.Text = String.Join("\r\n", recovery.Changes); start.Text = "应用已预览恢复"; prepared = true; }
                else { SetupCore.Require(confirm.Checked, "LBWIZ_CONFIRM_SELECTION_FIRST"); int count = InstallerCore.ExecuteRecovery(recovery, true); recovery = null; confirm.Checked = false; start.Text = "开始安装"; status.Text = "已处理 " + count + " 个插件事务，配置和记忆保留。请点重新读取后继续。"; prepared = false; }
            }
            catch (Exception error) { recovery = null; status.Text = WizardCore.Explain(error); }
            SetButtons();
        }
    }
}
