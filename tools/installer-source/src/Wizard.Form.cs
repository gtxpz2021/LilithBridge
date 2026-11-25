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
        readonly CheckBox story = new CheckBox(), storyGrant = new CheckBox(), storySwitch = new CheckBox();
        readonly CheckBox plugin = new CheckBox(), loader = new CheckBox(), recall = new CheckBox(), voice = new CheckBox(), configure = new CheckBox(), network = new CheckBox(), confirm = new CheckBox();
        readonly Button browse = new Button(), inspect = new Button(), start = new Button(), stop = new Button(), update = new Button(), advanced = new Button(), offlineBrowse = new Button(), voiceBrowse = new Button(), recover = new Button(), rollback = new Button(), serviceOnly = new Button();
        readonly Label data = new Label(), keyState = new Label(), providerNote = new Label(), plan = new Label();
        readonly ProgressBar progress = new ProgressBar(); readonly ListView stages = new ListView();
        readonly TableLayoutPanel settings = new TableLayoutPanel(), layout = new TableLayoutPanel(); readonly Panel advancedPanel = new Panel();
        CancellationTokenSource cancellation; bool busy, loading, providerEdited, prepared; string expectedDigest = "MISSING", planSummary = "", activeDataRoot = ""; ReleaseUpdate release; RecoveryPlan recovery;
        readonly System.Windows.Forms.Timer planTimer = new System.Windows.Forms.Timer { Interval = 450 };
        volatile bool commitInProgress; bool hasExistingApiKey;
        public string LastState { get; private set; }
        public string LastError { get; private set; }
        public WizardForm(string packageRoot, string initialRoot) : this(packageRoot, initialRoot, null) { }
        public WizardForm(string packageRoot, string initialRoot, WizardOptions resume)
        {
            package = packageRoot; Text = BuildInfo.Caption; Name = "LilithBridgeUnifiedWizard";
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(1040, 900); MinimumSize = new Size(920, 860); StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(246, 248, 252);
            layout.Dock = DockStyle.Fill; layout.Padding = new Padding(22, 14, 22, 12); layout.ColumnCount = 1; layout.RowCount = 11;
            foreach (int height in new[] { 58, 34, 36, 248, 26, 82, 0, 34, 74, 28, 44 }) layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height)); Controls.Add(layout);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); heading.Controls.Add(new Label { Text = "一键准备好莉莉丝所需的全部组件", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 19, FontStyle.Bold) });
            heading.Controls.Add(new Label { Text = "推荐功能已默认勾选（含向量召回与同目录语音包）。首次使用填好 API Key（或进游戏登录 ChatGPT），点右下角「一键安装」即可。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(75, 87, 103) }); layout.Controls.Add(heading, 0, 0);
            games.Name = "DiscoveredGames"; games.DropDownStyle = ComboBoxStyle.DropDownList; games.Dock = DockStyle.Fill; games.Items.Add("已自动查找游戏目录（如有多个可切换）");
            foreach (var item in InstallerCore.Discover(package)) games.Items.Add(item); games.SelectedIndex = 0; layout.Controls.Add(games, 0, 1);
            root.Name = "GameRoot"; root.ReadOnly = true; root.Dock = DockStyle.Fill; browse.Text = "选择游戏目录…"; inspect.Text = "重新读取"; browse.AutoSize = inspect.AutoSize = true;
            layout.Controls.Add(Line(root, browse, inspect), 0, 2);
            settings.Dock = DockStyle.Fill; settings.ColumnCount = 2; settings.RowCount = 1; settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); layout.Controls.Add(settings, 0, 3);
            var components = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = Color.White, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 3, 10, 3) };
            foreach (int h in new[] { 24, 27, 27, 36, 32, 27, 38 }) components.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            components.Controls.Add(new Label { Text = "安装与更新组件（推荐项已默认标选）", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            plugin.Text = "LilithBridge 插件（自动备份旧版）"; loader.Text = "缺少时自动准备游戏加载器";
            recall.Text = "本地向量召回：Python、环境与 BGE 模型（推荐）\r\n自动复用懒人包或国内镜像缓存";
            voice.Text = "莉莉丝语音包（TTS 语音播报 + F8 语音输入）";
            voiceBrowse.Text = "选语音包…"; voiceBrowse.AutoSize = true; voiceBrowse.Height = 25;
            configure.Text = "保存所选配置（已有 Key、登录与自定义项保留）";
            plugin.Checked = loader.Checked = recall.Checked = configure.Checked = true;
            voice.Checked = VoicePackProvisioning.CanAutoEnable(package, "", "", null);
            foreach (var box in new[] { plugin, loader, recall, voice, configure }) { box.Dock = DockStyle.Fill; box.Margin = new Padding(0); }
            plugin.Name = "SelectPlugin"; loader.Name = "SelectLoader"; recall.Name = "SelectRecall"; voice.Name = "SelectVoicePack"; configure.Name = "SelectConfiguration";
            components.Controls.Add(plugin, 0, 1); components.Controls.Add(loader, 0, 2); components.Controls.Add(recall, 0, 3);
            components.Controls.Add(Line(voice, voiceBrowse), 0, 4);
            components.Controls.Add(configure, 0, 5);
            source.Name = "PluginSource"; source.DropDownStyle = ComboBoxStyle.DropDownList; source.Dock = DockStyle.Fill; source.Items.AddRange(new object[] { "插件来源：安装包内的已核验版本", "插件来源：先检查项目更新再下载" }); source.SelectedIndex = 0; components.Controls.Add(source, 0, 6); settings.Controls.Add(components, 0, 0);
            var service = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, BackColor = Color.White, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 3, 0, 3) };
            service.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); service.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            foreach (int h in new[] { 24, 34, 26, 32, 30, 56 }) service.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            service.Controls.Add(new Label { Text = "对话服务（首次安装请填写 Key 或选游戏内登录）", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0); service.SetColumnSpan(service.GetControlFromPosition(0, 0), 2);
            provider.Name = "Provider"; provider.DropDownStyle = ComboBoxStyle.DropDownList; provider.Dock = DockStyle.Fill; provider.Items.AddRange(new object[] { "保留服务 / 游戏内选 OpenAI、ChatGPT", "DeepSeek 官方", "自定义服务（高级）" });
            model.Name = "ApiModel"; model.Dock = DockStyle.Fill; model.DropDownStyle = ComboBoxStyle.DropDown; model.Items.AddRange(ProviderPresets.Models); model.Text = ProviderPresets.DefaultModel;
            service.Controls.Add(provider, 0, 1); service.Controls.Add(model, 1, 1);
            service.Controls.Add(new Label { Text = "所选服务 API Key（隐藏，不回显）", Dock = DockStyle.Fill }, 0, 2); service.SetColumnSpan(service.GetControlFromPosition(0, 2), 2);
            key.Name = "ApiKey"; key.UseSystemPasswordChar = true; key.MaxLength = 4096; key.Dock = DockStyle.Fill; service.Controls.Add(key, 0, 3); service.SetColumnSpan(key, 2);
            keyState.Dock = DockStyle.Fill; keyState.ForeColor = Color.FromArgb(75, 87, 103); service.Controls.Add(keyState, 0, 4); service.SetColumnSpan(keyState, 2);
            endpoint.Name = "CustomEndpoint"; endpoint.Dock = DockStyle.Fill; endpoint.Text = ProviderPresets.DeepSeekUrl;
            providerNote.Dock = DockStyle.Fill; providerNote.ForeColor = Color.FromArgb(75, 87, 103); service.Controls.Add(providerNote, 0, 5); service.SetColumnSpan(providerNote, 2); settings.Controls.Add(service, 1, 0);
            data.Name = "DataRoot"; data.Dock = DockStyle.Fill; data.AutoEllipsis = true; data.Text = "配置与记忆目录：正在自动检测游戏目录…"; layout.Controls.Add(data, 0, 4);
            story.Name = "ConfigureBundledStory"; story.Text = "安装并启用随包第一作语料（自动定位并开启本地作品资料）";
            storyGrant.Name = "AllowStoryModelData"; storyGrant.Text = "允许当前模型按需读取这份第一作资料（仅作品资料域，推荐勾选）";
            storySwitch.Name = "SwitchExistingStory"; storySwitch.Text = "切换已有作品资料来源到随包库（旧用户默认不改，仅需强制重置时勾选）";
            var storyOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            foreach (var box in new[] { story, storyGrant, storySwitch }) { box.Dock = DockStyle.Fill; storyOptions.Controls.Add(box); }
            layout.Controls.Add(storyOptions, 0, 5);
            story.CheckedChanged += (s, e) => { if (!loading) { if (story.Checked) configure.Checked = true; Changed(true); SetButtons(); } };
            storyGrant.CheckedChanged += (s, e) => { if (!loading) Changed(true); }; storySwitch.CheckedChanged += (s, e) => { if (!loading) Changed(true); };
            stages.Name = "InstallationStages"; stages.Dock = DockStyle.Fill; stages.View = View.Details; stages.FullRowSelect = true; stages.HeaderStyle = ColumnHeaderStyle.Nonclickable; stages.Columns.Add("组件", 120); stages.Columns.Add("状态", 120); stages.Columns.Add("现在的进度 / 下一步", 670);
            foreach (string name in new[] { "游戏检查", "游戏加载器", "插件", "召回环境", "本地模型", "语音包", "离线验证", "配置" }) stages.Items.Add(new ListViewItem(new[] { name, "等待", "" })); layout.Controls.Add(stages, 0, 6);
            var advancedRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; advancedRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); advancedRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            advanced.Text = "离线包目录 / 自定义地址 ▾"; advanced.AutoSize = true; network.Text = "缺少依赖时允许自动从国内镜像或官方源补齐"; network.Checked = true; network.AutoSize = true;
            var toggles = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; toggles.Controls.Add(advanced); toggles.Controls.Add(network); advancedRow.Controls.Add(toggles, 0, 0);
            advancedPanel.Dock = DockStyle.Fill; advancedPanel.Visible = false; offline.Name = "OfflineCache"; offline.Dock = DockStyle.Fill; offline.ReadOnly = true; offlineBrowse.Text = "选择资源/语音包目录…"; offlineBrowse.AutoSize = true;
            var advancedFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); advancedFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); advancedFields.Controls.Add(Line(offline, offlineBrowse), 0, 0); advancedFields.Controls.Add(endpoint, 1, 0); advancedPanel.Controls.Add(advancedFields); advancedRow.Controls.Add(advancedPanel, 0, 1); layout.Controls.Add(advancedRow, 0, 7);
            status.Name = "Status"; status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical; status.Dock = DockStyle.Fill; status.BackColor = Color.White; status.Text = "正在自动检测游戏与同目录组件包…\r\n把语音包分卷（.zip.001/.002）或 ZIP 放在安装器同一文件夹，即可零操作自动识别并一键装好。"; layout.Controls.Add(status, 0, 8);
            confirm.Name = "ConfirmSelection"; confirm.Checked = true; confirm.Text = "安全保护已就绪：自动备份旧版插件与配置，游戏运行中绝不强行覆盖文件。"; confirm.Dock = DockStyle.Fill; layout.Controls.Add(confirm, 0, 9);
            start.Text = "一键安装"; start.Name = "StartInstallation"; start.Width = 140; start.Height = 34; start.BackColor = Color.FromArgb(35, 97, 177); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat; start.Font = new Font(Font, FontStyle.Bold);
            stop.Text = "停止"; stop.Width = 70; stop.Height = 34; stop.Enabled = false; update.Text = "检查项目更新"; recover.Text = "中断恢复"; rollback.Text = "回退上次插件";
            serviceOnly.Text = "仅修改服务"; serviceOnly.Name = "ServiceConfigurationOnly";
            foreach (var button in new[] { update, recover, rollback, serviceOnly }) { button.AutoSize = true; button.Height = 34; }
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false }; footer.Controls.Add(start); footer.Controls.Add(stop); footer.Controls.Add(update); footer.Controls.Add(rollback); footer.Controls.Add(recover); footer.Controls.Add(serviceOnly);
            progress.Width = 115; progress.Height = 27; footer.Controls.Add(progress); layout.Controls.Add(footer, 0, 10);
            plan.Name = "PlanSummary";
            browse.Click += async (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "选择包含 Lilith.exe 的游戏目录", ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) await SelectRoot(dialog.SelectedPath); };
            games.SelectedIndexChanged += async (s, e) => { if (!loading && games.SelectedIndex > 0) await SelectRoot((string)games.SelectedItem); };
            inspect.Click += async (s, e) => { if (root.Text.Length != 0) await SelectRoot(root.Text); };
            start.Click += async (s, e) => await BeginInstall(); stop.Click += (s, e) => { if (commitInProgress) { status.Text = "已进入最终配置提交，此短步骤完成前不能取消。"; return; } if (cancellation != null) { cancellation.Cancel(); status.Text = "正在停止。下载会立即停止；环境安装将完成当前操作后停止，避免留下仍在写文件的子进程。"; } };
            advanced.Click += (s, e) => { advancedPanel.Visible = !advancedPanel.Visible; layout.RowStyles[7].Height = advancedPanel.Visible ? 66 : 34; advanced.Text = advancedPanel.Visible ? "离线包目录 / 自定义地址 ▴" : "离线包目录 / 自定义地址 ▾"; };
            offlineBrowse.Click += async (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "选择资源包或语音包所在文件夹", ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) { offline.Text = dialog.SelectedPath; SyncAutoVoiceCheck(); await RefreshPlan(); } };
            voiceBrowse.Click += async (s, e) => { if (PromptVoicePackLocation()) await RefreshPlan(); };
            voice.CheckedChanged += async (s, e) =>
            {
                if (loading) return;
                if (voice.Checked)
                {
                    configure.Checked = true;
                    Snapshot snap = ReadCurrentSnapshotSafe();
                    if (!VoicePackProvisioning.CanAutoEnable(package, offline.Text, activeDataRoot, snap))
                    {
                        if (!PromptVoicePackLocation())
                        {
                            loading = true; voice.Checked = false; loading = false;
                            status.Text = "尚未选择语音包位置。您可以把 LilithBridge-VoicePack 分卷（.zip.001/.002）放在安装器同一文件夹，或点“选语音包…”指定后再勾选。";
                            return;
                        }
                    }
                }
                Changed(true);
                await Task.FromResult(0);
            };
            provider.SelectedIndexChanged += (s, e) => { if (loading) return; providerEdited = true; key.Clear(); if (provider.SelectedIndex == 1) { loading = true; endpoint.Text = ProviderPresets.DeepSeekUrl; model.Items.Clear(); model.Items.AddRange(ProviderPresets.Models); model.Text = ProviderPresets.DefaultModel; loading = false; } else if (provider.SelectedIndex == 2) { advancedPanel.Visible = true; layout.RowStyles[7].Height = 66; } ProviderNote(); Changed(); };
            serviceOnly.Click += async (s, e) => {
                loading = true; plugin.Checked = loader.Checked = recall.Checked = voice.Checked = false; configure.Checked = true;
                story.Checked = storyGrant.Checked = storySwitch.Checked = false; source.SelectedIndex = 0; loading = false;
                prepared = false; Changed(); await RefreshPlan();
                if (prepared) status.Text = "当前仅修改所选服务配置，不重新安装插件、加载器、召回或语音组件。\r\n" + planSummary;
            };
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
                string targetRoot = initialRoot;
                if (String.IsNullOrEmpty(targetRoot) && games.Items.Count > 1)
                {
                    loading = true; games.SelectedIndex = 1; loading = false;
                    targetRoot = (string)games.Items[1];
                }
                if (String.IsNullOrEmpty(targetRoot))
                {
                    status.Text = "未能自动定位 Steam 游戏目录，请点上方“选择游戏目录…”找到包含 Lilith.exe 的文件夹。";
                    return;
                }
                await SelectRoot(targetRoot);
                if (resume == null) return;
                loading = true; provider.SelectedIndex = resume.KeepService ? 0 : ProviderPresets.Matches(endpoint.Text, model.Text) ? 1 : 2; plugin.Checked = resume.Plugin; loader.Checked = resume.Loader; recall.Checked = resume.Runtime; voice.Checked = resume.VoicePack || VoicePackProvisioning.CanAutoEnable(package, resume.OfflineRoot, activeDataRoot, ReadCurrentSnapshotSafe()); configure.Checked = resume.Configure; network.Checked = resume.Network; offline.Text = resume.OfflineRoot; release = resume.Update; source.SelectedIndex = resume.DownloadPlugin ? 1 : 0; loading = false;
                await RefreshPlan();
                if (resume.FromHandoff && prepared)
                {
                    confirm.Checked = true; start.Text = "继续一键安装";
                    status.Text += "\r\n新版安装器已接续组件选择。";
                    if (resume.LostHandoffDraft) status.Text += "未保存的 Key 或服务草稿没有传递，请重新填写后继续。";
                    else await BeginInstall();
                }
            };
        }
        Snapshot ReadCurrentSnapshotSafe()
        {
            if (String.IsNullOrEmpty(activeDataRoot)) return null;
            try { return SetupCore.ReadSnapshot(Path.Combine(activeDataRoot, "config.json")); }
            catch { return null; }
        }
        void SyncAutoVoiceCheck()
        {
            if (VoicePackProvisioning.CanAutoEnable(package, offline.Text, activeDataRoot, ReadCurrentSnapshotSafe()))
            {
                loading = true; voice.Checked = true; configure.Checked = true; loading = false;
            }
        }
        bool PromptVoicePackLocation()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择莉莉丝语音包（支持 .zip.001 分卷、单个 .zip 或 voice-pack-manifest.json）",
                Filter = "莉莉丝语音包 (*.001;*.zip;voice-pack-manifest.json)|*LilithBridge-VoicePack*.zip.001;*LilithBridge-VoicePack*.zip;voice-pack-manifest.json|所有文件 (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                string folder = Path.GetDirectoryName(dialog.FileName);
                offline.Text = folder;
                Snapshot snap = ReadCurrentSnapshotSafe();
                if (VoicePackProvisioning.CanAutoEnable(package, offline.Text, activeDataRoot, snap))
                {
                    loading = true; voice.Checked = true; configure.Checked = true; loading = false;
                    return true;
                }
                MessageBox.Show(this, "所选目录未包含完整的莉莉丝语音包（若是分卷包，请确认 .zip.001、.zip.002 等分卷在同一文件夹内）。", "未检测到完整语音包", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }
        static Control Line(Control main, params Control[] buttons)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 + buttons.Length, RowCount = 1, Margin = Padding.Empty }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.Controls.Add(main, 0, 0);
            for (int i = 0; i < buttons.Length; i++) { row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.Controls.Add(buttons[i], i + 1, 0); } return row;
        }
        void ProviderNote()
        { bool preserve = provider.SelectedIndex == 0; model.Enabled = key.Enabled = !busy && !preserve; endpoint.ReadOnly = provider.SelectedIndex != 2; providerNote.Text = preserve ? "DeepSeek 请在上方选“DeepSeek 官方”并填 Key；OpenAI / ChatGPT 可直接安装后在游戏内登录配置；旧配置自动保留。" : provider.SelectedIndex == 1 ? ProviderPresets.ModelNote(model.Text) + " 请填写本人 Key；已有同服务 Key 自动保留。" : "高级服务需兼容 OpenAI Chat Completions 请求格式。更换服务时请填写对应 Key。"; }
        void ReviewExistingRoot(string game)
        {
            var review = ExistingDataRoot.Review(game);
            if (review.NeedsConfirmation) {
                bool yes = MessageBox.Show(this, review.Summary + "\r\n\r\n请先核对旧插件实际资料根；无法确认时选否，原文件保持不变。是否确认继续原位使用？", "核对旧资料根", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                ExistingDataRoot.Approve(review, yes);
            }
        }
        void Changed(bool replan = false)
        {
            recovery = null;
            confirm.Checked = true;
            confirm.Text = "安全保护已就绪：自动备份旧版插件与配置，游戏运行中绝不强行覆盖文件。";
            recover.Text = "中断恢复";
            rollback.Text = "回退上次插件";
            start.Text = root.Text.Length != 0 && File.Exists(Path.Combine(root.Text, InstallManifest.Plugin)) ? "一键更新 / 应用" : "一键安装";
            if (prepared) status.Text = planSummary;
            if (replan) { prepared = false; planTimer.Stop(); planTimer.Start(); }
            SetButtons();
        }
        void SetButtons()
        {
            foreach (Control control in new Control[] { browse, inspect, games, plugin, loader, recall, voice, voiceBrowse, configure, story, storyGrant, storySwitch, network, source, provider, model, key, endpoint, offlineBrowse, update, recover, rollback, confirm, advanced, serviceOnly }) control.Enabled = !busy;
            serviceOnly.Enabled = !busy && root.Text.Length != 0 && File.Exists(Path.Combine(root.Text, InstallManifest.Plugin));
            start.Enabled = !busy && prepared && confirm.Checked; stop.Enabled = busy && !commitInProgress; storyGrant.Enabled = storySwitch.Enabled = !busy && story.Checked; ProviderNote();
        }
        async Task SelectRoot(string path)
        {
            hasExistingApiKey = false; root.Text = path; activeDataRoot = ""; expectedDigest = "MISSING"; prepared = false; recovery = null; key.Clear(); LastError = null;
            loading = true; confirm.Checked = true; confirm.Text = "安全保护已就绪：自动备份旧版插件与配置，游戏运行中绝不强行覆盖文件。"; story.Checked = storyGrant.Checked = storySwitch.Checked = false; provider.SelectedIndex = 0; model.Text = ProviderPresets.DefaultModel; endpoint.Text = ProviderPresets.DeepSeekUrl; loading = false; providerEdited = false;
            try
            {
                string absolute = SetupCore.SafePath(path, null); SetupCore.NoReparse(absolute); root.Text = absolute; ReviewExistingRoot(absolute);
                bool hasPluginAlready = File.Exists(Path.Combine(absolute, InstallManifest.Plugin));
                CopyContext selectedContext = hasPluginAlready ? SetupCore.Inspect(absolute) : null;
                string retainedRoot = selectedContext == null ? InstallerCore.FindRetainedDataRoot(absolute) : null;
                if (selectedContext != null || retainedRoot != null)
                {
                    string selectedData = selectedContext == null ? retainedRoot : selectedContext.DataRoot; activeDataRoot = selectedData;
                    var snapshot = SetupCore.ReadSnapshot(Path.Combine(selectedData, "config.json")); expectedDigest = snapshot.Digest;
                    var current = DataJson.EffectiveProvider(snapshot.Document); hasExistingApiKey = !String.IsNullOrWhiteSpace(DataJson.String(current, "api_key", ""));
                    bool freshStory = !StorySetup.HasExistingChoice(snapshot.Document);
                    loading = true;
                    story.Checked = freshStory;
                    storyGrant.Checked = freshStory;
                    voice.Checked = VoicePackProvisioning.CanAutoEnable(package, offline.Text, selectedData, snapshot);
                    if (snapshot.Exists) { endpoint.Text = DataJson.String(current, "base_url", ProviderPresets.DeepSeekUrl); model.Text = DataJson.String(current, "model", ProviderPresets.DefaultModel); provider.SelectedIndex = 0; }
                    else provider.SelectedIndex = 1;
                    loading = false;
                    data.Text = "配置与记忆目录：" + selectedData;
                    keyState.Text = snapshot.Exists ? "已检测到现有配置，默认保留原服务、Key 与记忆。直接点“一键更新 / 应用”即可。" : "首次配置：请选择服务和模型，并填写自己的 API Key。";
                }
                else
                {
                    activeDataRoot = Path.Combine(absolute, "LilithBridgeData");
                    loading = true;
                    story.Checked = true;
                    storyGrant.Checked = true;
                    voice.Checked = VoicePackProvisioning.CanAutoEnable(package, offline.Text, activeDataRoot, null);
                    provider.SelectedIndex = 1;
                    loading = false;
                    data.Text = "配置与记忆目录：" + activeDataRoot;
                    keyState.Text = "首次配置：DeepSeek 请直接填写 Key；若用 OpenAI/ChatGPT 可切回第一项进游戏登录。";
                }
                start.Text = hasPluginAlready ? "一键更新 / 应用" : "一键安装";
                ProviderNote(); await RefreshPlan();
            }
            catch (Exception error) { loading = false; LastError = WizardCore.Explain(error); status.Text = LastError; SetButtons(); }
        }
        WizardOptions CaptureOptions()
        { return new WizardOptions { PackageRoot = package, GameRoot = root.Text, OfflineRoot = offline.Text, ExpectedConfigDigest = expectedDigest, KeepService = provider.SelectedIndex == 0, ConfigureBundledStory = story.Checked, AllowStoryModelData = storyGrant.Checked, SwitchExistingStory = storySwitch.Checked, Plugin = plugin.Checked, Loader = loader.Checked, Runtime = recall.Checked, Model = recall.Checked, EnableRecall = recall.Checked, VoicePack = voice.Checked, Configure = configure.Checked, Network = network.Checked, DownloadPlugin = source.SelectedIndex == 1, Update = release, BaseUrl = endpoint.Text, ApiModel = model.Text, ApiKey = key.Text, ProviderEdited = providerEdited, Confirm = confirm.Checked }; }
        async Task RefreshPlan()
        {
            if (root.Text.Length == 0) return; busy = true; SetButtons(); var options = CaptureOptions(); status.Text = "正在检查已有文件与本地组件包…";
            try { string summary = await Task.Run(() => WizardPlanning.Preview(options, text => ShowProgress(new WizardProgress("check", "working", text, 0)))); prepared = true; planSummary = summary; status.Text = summary; }
            catch (Exception error) { prepared = false; LastError = WizardCore.Explain(error); status.Text = LastError; }
            finally { busy = false; SetButtons(); }
        }
        void ShowProgress(WizardProgress item)
        {
            if (item.State == "committing") commitInProgress = true;
            if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(new Action<WizardProgress>(ShowProgress), item); return; }
            progress.Value = Math.Max(0, Math.Min(100, item.Percent)); stop.Enabled = busy && !commitInProgress;
            string[] keys = { "check", "loader", "plugin", "runtime", "model", "voice", "verify", "config" }; int at = Array.IndexOf(keys, item.Step);
            if (at >= 0) { stages.Items[at].SubItems[1].Text = item.State == "done" ? "已完成" : item.State == "waiting" ? "等待操作" : "进行中"; stages.Items[at].SubItems[2].Text = item.Message; }
            status.Text = item.Message;
        }
        async Task BeginInstall()
        {
            if (recovery != null) { Recovery(recovery.PendingOnly); return; }
            if (configure.Checked && provider.SelectedIndex != 0 && String.IsNullOrWhiteSpace(key.Text) && !hasExistingApiKey)
            { LastError = "所选 API 服务尚无 Key。请先填写您的 API Key；若准备在游戏内登录 OpenAI / ChatGPT，请在右上角服务下拉框选第一项。"; status.Text = LastError; return; }
            var options = CaptureOptions(); bool switchInstaller = false; commitInProgress = false; busy = true; cancellation = new CancellationTokenSource(); SetButtons(); LastError = null;
            foreach (ListViewItem item in stages.Items) { item.SubItems[1].Text = "等待"; item.SubItems[2].Text = ""; }
            if (!options.Runtime) foreach (int at in new[] { 3, 4 }) { stages.Items[at].SubItems[1].Text = "保持现状"; stages.Items[at].SubItems[2].Text = "未选择变更本地召回"; }
            if (!options.VoicePack) { stages.Items[5].SubItems[1].Text = "保持现状"; stages.Items[5].SubItems[2].Text = "未选择变更语音包"; }
            if (!options.Runtime && !options.VoicePack) { stages.Items[6].SubItems[1].Text = "保持现状"; stages.Items[6].SubItems[2].Text = "未选择本地环境验证项"; }
            if (!options.Configure) { stages.Items[7].SubItems[1].Text = "保持现状"; stages.Items[7].SubItems[2].Text = "未选择保存服务设置"; }
            try
            {
                WizardSession.Save(options, "Applying");
                await Task.Run(() => WizardPlanning.Preview(options, text => ShowProgress(new WizardProgress("check", "working", text, 0))));
                WizardResult result = await Task.Run(() => WizardCore.Execute(options, ShowProgress, cancellation.Token)); LastState = result.State; status.Text = result.Message;
                WizardSession.Save(options, result.State);
                if (result.State == "InstallerHandoffReady") { InstallerHandoff.LaunchHelper(package, result.HandoffPlanPath); loading = true; key.Clear(); loading = false; switchInstaller = true; }
                else if (result.State == "WaitingForGame" || result.State == "WaitingForExit") start.Text = "继续安装";
                else { start.Text = "再次检查 / 应用"; loading = true; key.Clear(); loading = false; if (result.DataRoot != null) { activeDataRoot = result.DataRoot; var savedSnapshot = SetupCore.ReadSnapshot(Path.Combine(result.DataRoot, "config.json")); expectedDigest = savedSnapshot.Digest; hasExistingApiKey = !String.IsNullOrWhiteSpace(DataJson.String(DataJson.EffectiveProvider(savedSnapshot.Document), "api_key", "")); } providerEdited = false; progress.Value = 100; }
            }
            catch (Exception error) { LastState = "Incomplete"; LastError = WizardCore.Explain(error); status.Text = "所选流程尚未完成。\r\n" + LastError + "\r\n可点“重新检查并继续”重试；已校验资源及未完成下载会复用，原资料不会清空。"; start.Text = "重新检查并继续"; try { WizardSession.Save(options, "Incomplete"); } catch { } }
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
                loading = true; source.SelectedIndex = 1; plugin.Checked = true; loading = false; confirm.Checked = true;
                status.Text = "官方发行：" + release.Tag + " / " + release.Asset.Name + "\r\n" + (same || release.Asset.Hash == TrustedDownloads.PluginRelease.Hash ? "当前插件与这个已核验发行一致。" : "选择已切换到该发行。点击右下角按钮即可一键下载并应用；配置和记忆保持原位。") + "\r\n插件失败可从本窗口回退。安装器自身不会在运行中被覆盖。";
            }
            catch (Exception error) { status.Text = WizardCore.Explain(error); }
            finally { cancellation.Dispose(); cancellation = null; busy = false; SetButtons(); }
        }
        void Recovery(bool pending)
        {
            try
            {
                if (recovery == null || recovery.PendingOnly != pending) { recovery = InstallerCore.PreviewRecovery(root.Text, pending); confirm.Checked = false; confirm.Text = "我已核对下方恢复清单，允许执行这次恢复。"; status.Text = String.Join("\r\n", recovery.Changes); start.Text = "应用已预览恢复"; prepared = true; }
                else { SetupCore.Require(confirm.Checked, "LBWIZ_CONFIRM_SELECTION_FIRST"); int count = InstallerCore.ExecuteRecovery(recovery, true); recovery = null; confirm.Checked = true; confirm.Text = "安全保护已就绪：自动备份旧版插件与配置，游戏运行中绝不强行覆盖文件。"; start.Text = "一键安装"; status.Text = "已处理 " + count + " 个插件事务，配置和记忆保留。请点重新读取后继续。"; prepared = false; }
            }
            catch (Exception error) { recovery = null; status.Text = WizardCore.Explain(error); }
            SetButtons();
        }
    }
}
