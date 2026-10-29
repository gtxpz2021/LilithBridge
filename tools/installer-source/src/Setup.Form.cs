using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace LilithSetup
{
    public sealed class SetupForm : Form
    {
        readonly TextBox rootBox = new TextBox(), dataBox = new TextBox(), keyBox = new TextBox(), urlBox = new TextBox(), localModelBox = new TextBox(), statusBox = new TextBox();
        readonly ComboBox providerBox = new ComboBox(), apiModelBox = new ComboBox();
        readonly Label keyState = new Label(), recallState = new Label(), providerNote = new Label();
        TableLayoutPanel mainLayout; Control urlPanel;
        readonly CheckBox confirm = new CheckBox(), clearKey = new CheckBox();
        readonly ComboBox recallAction = new ComboBox();
        readonly Button browse = new Button(), inspect = new Button(), checkLocal = new Button(), chooseModel = new Button(), undoModel = new Button(), runtimePaths = new Button(), save = new Button(), cancel = new Button();
        readonly ModelValidator validator;
        CopyContext context; Snapshot snapshot; LocalCheck checkedLocal; string pendingModel = "", pendingPython = "", pendingWorker = "";
        bool busy, loadingProvider;
        public SaveResult LastSaveResult { get; private set; }
        public string LastError { get; private set; }
        public SetupForm(string initialRoot, string manifestPath)
        {
            validator = new ModelValidator(manifestPath);
            Text = "LilithBridge · 配置"; Name = "LilithSetupForm";
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(920, 760); MinimumSize = new Size(850, 740);
            BackColor = Color.FromArgb(247, 249, 252);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new Padding(20) };
            mainLayout = layout;
            int[] heights = { 58, 56, 46, 82, 66, 56, 60, 55, 40, 0, 46 };
            for (int i = 0; i < heights.Length; i++) layout.RowStyles.Add(heights[i] == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, heights[i]));
            Controls.Add(layout);
            var title = new Label { Dock = DockStyle.Fill, Text = "配置 LilithBridge", Font = new Font(Font.FontFamily, 17F, FontStyle.Bold), AutoSize = false };
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var subtitle = new Label { Text = "选择服务商和模型，填入自己的 API Key 后保存。本地召回可以稍后设置。", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(70, 80, 95) };
            top.Controls.Add(title, 0, 0); top.Controls.Add(subtitle, 0, 1); layout.Controls.Add(top, 0, 0);
            rootBox.Name = "GameRoot"; rootBox.ReadOnly = true; rootBox.Dock = DockStyle.Fill;
            browse.Text = "选择游戏目录…"; browse.Name = "ChooseCopy"; browse.AutoSize = true;
            inspect.Text = "重新读取"; inspect.Name = "InspectCopy"; inspect.AutoSize = true;
            layout.Controls.Add(Field("游戏目录（含 Lilith.exe）", Line(rootBox, browse, inspect)), 0, 1);
            dataBox.Name = "BridgeDataRoot"; dataBox.ReadOnly = true; dataBox.Dock = DockStyle.Fill;
            layout.Controls.Add(Field("独立配置目录", dataBox), 0, 2);
            keyBox.Name = "ApiKey"; keyBox.UseSystemPasswordChar = true; keyBox.MaxLength = 4096; keyBox.Dock = DockStyle.Fill;
            clearKey.Name = "ClearKey"; clearKey.Text = "明确清除当前有效 Key"; clearKey.AutoSize = true;
            keyState.Name = "KeyState"; keyState.AutoSize = true; keyState.ForeColor = Color.FromArgb(70, 80, 95);
            var keyPanel = Field("API Key（不回显已有值；留空保留）", Line(keyBox, clearKey));
            keyPanel.Controls.Add(keyState, 0, 2); keyPanel.RowCount = 3;
            layout.Controls.Add(keyPanel, 0, 4);
            urlBox.Name = "BaseUrl"; urlBox.Dock = DockStyle.Fill;
            urlPanel = Field("自定义服务地址（HTTPS；需兼容本项目的 DeepSeek 请求格式）", urlBox); layout.Controls.Add(urlPanel, 0, 5);
            providerBox.Name = "ProviderPreset"; providerBox.Dock = DockStyle.Fill; providerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            providerBox.Items.AddRange(new object[] { "DeepSeek 官方", "自定义服务（高级）" });
            apiModelBox.Name = "ApiModel"; apiModelBox.Dock = DockStyle.Fill; apiModelBox.DropDownStyle = ComboBoxStyle.DropDown; apiModelBox.MaxLength = 256;
            providerNote.Name = "ProviderNote"; providerNote.Dock = DockStyle.Fill; providerNote.AutoSize = true; providerNote.ForeColor = Color.FromArgb(70, 80, 95);
            var providerPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            providerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38)); providerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            providerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); providerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            providerPanel.Controls.Add(Field("服务商", providerBox), 0, 0); providerPanel.Controls.Add(Field("对话模型（可选，也可输入模型 ID）", apiModelBox), 1, 0);
            providerPanel.Controls.Add(providerNote, 0, 1); providerPanel.SetColumnSpan(providerNote, 2); layout.Controls.Add(providerPanel, 0, 3);
            localModelBox.Name = "LocalModel"; localModelBox.ReadOnly = true; localModelBox.Dock = DockStyle.Fill;
            chooseModel.Text = "换模型目录…"; chooseModel.Name = "ChooseModel"; chooseModel.AutoSize = true;
            undoModel.Text = "取消更换"; undoModel.Name = "UndoModel"; undoModel.AutoSize = true;
            layout.Controls.Add(Field("本地 BGE 召回模型（可选；固定 revision；用于召回，不是聊天大模型）", Line(localModelBox, chooseModel, undoModel)), 0, 6);
            recallAction.Name = "RecallAction"; recallAction.DropDownStyle = ComboBoxStyle.DropDownList; recallAction.Width = 245;
            recallAction.Items.AddRange(new object[] { "保持当前召回开关", "关闭本地召回", "启用本地召回（先检查文件）" }); recallAction.SelectedIndex = 0;
            checkLocal.Name = "CheckLocal"; checkLocal.Text = "检查本地文件"; checkLocal.AutoSize = true;
            runtimePaths.Name = "ChooseRuntimePaths"; runtimePaths.Text = "Python / worker 路径…"; runtimePaths.AutoSize = true;
            recallState.Name = "RecallState"; recallState.Dock = DockStyle.Fill; recallState.AutoEllipsis = true;
            var recallPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            recallPanel.Controls.Add(Line(recallAction, runtimePaths, checkLocal), 0, 0); recallPanel.Controls.Add(recallState, 0, 1);
            layout.Controls.Add(recallPanel, 0, 7);
            confirm.Name = "ConfirmCopy"; confirm.Text = "我已核对上方目录，允许将配置保存到这里。"; confirm.Dock = DockStyle.Fill;
            layout.Controls.Add(confirm, 0, 8);
            statusBox.Name = "Status"; statusBox.Multiline = true; statusBox.ReadOnly = true; statusBox.ScrollBars = ScrollBars.Vertical; statusBox.Dock = DockStyle.Fill; statusBox.BackColor = Color.White; statusBox.BorderStyle = BorderStyle.FixedSingle;
            layout.Controls.Add(statusBox, 0, 9);
            save.Name = "SaveConfig"; save.Text = "保存配置"; save.Width = 130; save.Height = 34; save.BackColor = Color.FromArgb(37, 99, 170); save.ForeColor = Color.White; save.FlatStyle = FlatStyle.Flat;
            cancel.Name = "CancelConfig"; cancel.Text = "取消 / 关闭"; cancel.Width = 130; cancel.Height = 34;
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 5, 0, 0) };
            footer.Controls.Add(cancel); footer.Controls.Add(save); layout.Controls.Add(footer, 0, 10);
            CancelButton = cancel;
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择包含 Lilith.exe 的游戏目录", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) SelectCopy(dialog.SelectedPath);
            };
            inspect.Click += delegate { InspectCopy(); };
            chooseModel.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择已解压的 BGE-small-zh-v1.5 模型目录（只读检查，不复制索引）", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) SelectModel(dialog.SelectedPath);
            };
            undoModel.Click += delegate { pendingModel = ""; checkedLocal = null; DisplayLocal(); SetStatus("已取消更换模型，尚未写入。重新检查本地文件后才可显式启用。"); };
            clearKey.CheckedChanged += delegate { if (clearKey.Checked) keyBox.Clear(); keyBox.Enabled = !clearKey.Checked && !busy; };
            providerBox.SelectedIndexChanged += delegate { ChangeProviderPreset(); };
            apiModelBox.TextChanged += delegate { if (!loadingProvider) ShowProviderNote(); };
            urlBox.TextChanged += delegate { if (!loadingProvider) { keyBox.Clear(); ShowProviderNote(); } };
            checkLocal.Click += delegate { Run(CheckLocalFiles); };
            runtimePaths.Click += delegate { Run(ChooseRuntimePaths); };
            save.Click += delegate { Run(SaveConfig); };
            cancel.Click += delegate { keyBox.Clear(); Close(); };
            FormClosed += delegate { keyBox.Clear(); };
            if (!String.IsNullOrEmpty(initialRoot)) SelectCopy(initialRoot);
            else SetStatus("请选择游戏目录。API Key 及配置备份以明文保存在本机，请勿分享使用后的配置目录。\r\n保存设置不会发送 API 请求；进入游戏对话后可能产生服务费用。原生存档按 Windows 账户共享。");
        }
        static Control Line(Control main, params Control[] trailing)
        {
            var line = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = trailing.Length + 1, RowCount = 1, Margin = Padding.Empty };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); line.Controls.Add(main, 0, 0);
            for (int i = 0; i < trailing.Length; i++) { line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); line.Controls.Add(trailing[i], i + 1, 0); }
            return line;
        }
        static TableLayoutPanel Field(string caption, Control control)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 3) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = caption, AutoSize = true, Dock = DockStyle.Fill }, 0, 0); panel.Controls.Add(control, 0, 1); return panel;
        }
        void SetStatus(string text) { statusBox.Text = text; }
        void LoadProviderDraft(Snapshot loaded, string url, string model)
        {
            loadingProvider = true;
            try
            {
                bool preset = !loaded.Exists || ProviderPresets.Matches(url, model);
                providerBox.SelectedIndex = preset ? 0 : 1; apiModelBox.Items.Clear();
                if (preset) apiModelBox.Items.AddRange(ProviderPresets.Models);
                urlBox.Text = !loaded.Exists ? ProviderPresets.DeepSeekUrl : url;
                apiModelBox.Text = !loaded.Exists ? ProviderPresets.DefaultModel : model;
            }
            finally { loadingProvider = false; }
            ShowProviderLayout();
        }
        void ChangeProviderPreset()
        {
            if (loadingProvider || providerBox.SelectedIndex < 0) return;
            loadingProvider = true;
            try
            {
                keyBox.Clear(); string currentModel = apiModelBox.Text; apiModelBox.Items.Clear();
                if (providerBox.SelectedIndex == 0) { apiModelBox.Items.AddRange(ProviderPresets.Models); urlBox.Text = ProviderPresets.DeepSeekUrl; apiModelBox.Text = ProviderPresets.DefaultModel; }
                else apiModelBox.Text = currentModel;
            }
            finally { loadingProvider = false; }
            ShowProviderLayout(); ShowProviderNote();
            SetStatus("当前选择尚未保存。切换服务时，请重新填写对应的 Key；原已保存 Key 未改动。只有点击保存后才生效。");
        }
        void ShowProviderLayout()
        {
            bool custom = providerBox.SelectedIndex != 0; urlPanel.Visible = custom; urlBox.ReadOnly = !custom;
            mainLayout.RowStyles[5].Height = custom ? 56 : 0;
        }
        void ShowProviderNote()
        {
            providerNote.Text = providerBox.SelectedIndex == 0 ? ProviderPresets.ModelNote(apiModelBox.Text) : "高级入口：保留自定义地址和模型；其他服务需兼容本项目请求格式，未自动验证。";
            if (snapshot == null) return;
            var original = DataJson.EffectiveProvider(snapshot.Document); bool hasKey = !String.IsNullOrEmpty(DataJson.String(original, "api_key", ""));
            if (hasKey && !ProviderPresets.SameEndpoint(DataJson.String(original, "base_url", ""), urlBox.Text)) keyState.Text = "新服务需另填其 Key 或明确清除；原已保存 Key 尚未修改，也不会自动带到新服务。";
            else keyState.Text = hasKey ? "当前有效 Key：已设置（不会回显）；网络未测试" : "当前有效 Key：未设置；网络未测试";
        }
        void SetBusy(bool value)
        {
            busy = value; UseWaitCursor = value;
            foreach (Control control in new Control[] { browse, inspect, checkLocal, chooseModel, undoModel, runtimePaths, save, urlBox, providerBox, apiModelBox, clearKey, confirm, recallAction }) control.Enabled = !value;
            keyBox.Enabled = !value && !clearKey.Checked;
        }
        void Run(Action action)
        {
            LastError = null; SetBusy(true);
            try { action(); }
            catch (Exception error)
            {
                LastError = SetupCore.SafeError(error); string message = LastError;
                if (LastError == "LBSETUP_FIRST_PROVIDER_URL_AND_MODEL_REQUIRED") message = "请选择服务商和模型；使用自定义服务时需填写地址和模型 ID。";
                if (LastError == "LBSETUP_NEW_SERVICE_REQUIRES_OWN_KEY_OR_EXPLICIT_CLEAR") message = "服务地址已改变。请填写这个服务自己的 Key，或明确勾选清除 Key；原已保存 Key 不会自动带到新服务，原文件尚未修改。";
                if (LastError == "LBSETUP_RUNTIME_PATH_CHANGE_REQUIRES_EXPLICIT_RECALL_OFF") message = "更换 Python/worker 路径时，请先明确选择“关闭本地召回”再保存；重新读取、检查后再主动启用。";
                if (LastError == "LBSETUP_WORKER_MODEL_MANIFEST_MISSING_OR_MISMATCH") message = "worker 同目录缺少匹配的 model-manifest.json，或清单摘要不符。请使用经核对的一组 worker 与清单。";
                SetStatus("未提交更改：" + message + "\r\n重新读取后再试。原配置未由本次失败保存替换。");
            }
            finally { SetBusy(false); }
        }
        void InvalidateDraft()
        {
            context = null; snapshot = null; checkedLocal = null; LastSaveResult = null; LastError = null;
            loadingProvider = true;
            keyBox.Clear(); clearKey.Checked = false; confirm.Checked = false; recallAction.SelectedIndex = 0;
            pendingModel = ""; pendingPython = ""; pendingWorker = ""; localModelBox.Clear(); urlBox.Clear(); apiModelBox.Text = ""; keyState.Text = "尚未读取"; dataBox.Clear(); recallState.Text = "尚未检查";
            providerBox.SelectedIndex = -1; apiModelBox.Items.Clear(); providerNote.Text = ""; loadingProvider = false;
        }
        public void SelectCopy(string path)
        {
            InvalidateDraft(); rootBox.Text = path; InspectCopy();
        }
        public void InspectCopy()
        {
            // Reload invalidates any secret draft, confirmation, model choice and prior check.
            InvalidateDraft(); Run(delegate
            {
                CopyContext next = SetupCore.Inspect(rootBox.Text); Snapshot nextSnapshot = SetupCore.ReadSnapshot(next.ConfigPath);
                var provider = DataJson.EffectiveProvider(nextSnapshot.Document);
                context = next; snapshot = nextSnapshot; rootBox.Text = next.GameRoot; dataBox.Text = next.DataRoot;
                LoadProviderDraft(nextSnapshot, DataJson.String(provider, "base_url", ""), DataJson.String(provider, "model", ""));
                keyState.Text = String.IsNullOrEmpty(DataJson.String(provider, "api_key", "")) ? "当前有效 Key：未设置；网络未测试" : "当前有效 Key：已设置（不会回显）；网络未测试";
                ShowProviderNote(); DisplayLocal();
                SetStatus("已读取配置。选择服务商和模型，填写自己的 Key 后保存。\r\nKey 和配置备份以明文保存在本机，请勿分享使用后的配置目录。保存设置不会发送 API 请求；游戏对话可能产生服务费用。\r\n原生存档按 Windows 账户共享。");
            });
        }
        void DisplayLocal()
        {
            checkedLocal = null;
            if (context == null || snapshot == null) return;
            try
            {
                LocalCheck paths = SetupCore.Locate(context, snapshot, pendingModel, pendingPython, pendingWorker); localModelBox.Text = paths.Model;
                var semantic = DataJson.Map(snapshot.Document, "semantic_recall", false);
                bool enabled = DataJson.Get(semantic, "enabled") is bool && (bool)DataJson.Get(semantic, "enabled");
                string files = File.Exists(paths.Python) && File.Exists(paths.Worker) && Directory.Exists(paths.Model) ? "已定位，未校验" : "缺少文件，不能确认可用";
                recallState.Text = "当前开关：" + (enabled ? "开" : "关") + "；本地文件：" + files + "；运行未测试";
            }
            catch (Exception error) { recallState.Text = "本地路径无效：" + SetupCore.SafeError(error); localModelBox.Text = pendingModel; }
        }
        public void SelectModel(string path)
        {
            pendingModel = path; checkedLocal = null; LastSaveResult = null; DisplayLocal();
            SetStatus("已暂选模型目录，尚未写入。保存更换时必须显式选择“关闭本地召回”。\r\n更换并保存后，可重新读取、检查本地文件，再显式启用。外部模型只读，不复制个人索引。");
        }
        void ChooseRuntimePaths()
        {
            SetupCore.Require(context != null && snapshot != null, "LBSETUP_CHECK_COPY_FIRST");
            var current = SetupCore.Locate(context, snapshot, pendingModel, pendingPython, pendingWorker);
            using (var dialog = new RuntimePathsForm(current.Python, current.Worker))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var original = SetupCore.Locate(context, snapshot, pendingModel);
                pendingPython = SetupCore.Same(dialog.PythonPath, original.Python) ? "" : dialog.PythonPath;
                pendingWorker = SetupCore.Same(dialog.WorkerPath, original.Worker) ? "" : dialog.WorkerPath;
                checkedLocal = null; DisplayLocal();
                SetStatus("Python：" + dialog.PythonPath + "\r\nworker：" + dialog.WorkerPath + "\r\n仅暂选，未保存或执行。更换路径需明确关闭召回后保存，再重新读取、检查文件并主动启用。\r\n请选择自己按说明安装的 Python 和经核对来源的 worker；本工具不下载、安装或运行它们。");
            }
        }
        void CheckLocalFiles()
        {
            SetupCore.Require(context != null && snapshot != null, "LBSETUP_CHECK_COPY_FIRST");
            checkedLocal = null;
            try { checkedLocal = SetupCore.CheckLocal(context, snapshot, pendingModel, validator, pendingPython, pendingWorker); }
            catch { recallState.Text = "本地文件检查失败；不能确认可用，不能启用"; throw; }
            recallState.Text = "模型文件检查通过；Python 和召回程序已找到。运行效果需在启用后确认。";
            SetStatus("本地静态检查通过：模型哈希与 Python/worker 文件路径已核对；没有联网或启动 Python，不代表依赖/架构/worker 协议能运行。\r\n检查不会修改开关。准备好且来源可信后，再明确选择启用并保存。路径更换须先关闭保存，重新读取检查后启用。");
        }
        void SaveConfig()
        {
            SetupCore.Require(context != null && snapshot != null, "LBSETUP_CHECK_COPY_FIRST");
            var edit = new EditRequest { ApiKey = keyBox.Text, ClearKey = clearKey.Checked, ModelOverride = pendingModel, PythonOverride = pendingPython, WorkerOverride = pendingWorker, Recall = (RecallAction)recallAction.SelectedIndex, ConfirmCopy = confirm.Checked, CheckedLocal = checkedLocal };
            SetupCore.ApplyChangedProviderText(edit, snapshot, urlBox.Text, apiModelBox.Text);
            SaveResult result = SetupCore.Save(context, snapshot, edit, validator); LastSaveResult = result;
            // Everything after the commit has a distinct success/warning path.
            try
            {
                keyBox.Clear(); clearKey.Checked = false; confirm.Checked = false; pendingModel = ""; pendingPython = ""; pendingWorker = ""; checkedLocal = null;
                context = null; snapshot = null;
                SetStatus((result.ReadbackVerified ? "配置已保存并复核。" : "配置已写入，请点“重新读取”确认。") + "\r\n" + (result.BackupPath == null ? "" : "原配置备份：" + result.BackupPath) + "\r\nKey 和备份均为本机明文。保存没有发送 API 请求；游戏对话可能产生服务费用。\r\n" + (result.Warnings.Count == 0 ? "再次编辑请点“重新读取”。" : "保存后的提示：" + String.Join(", ", result.Warnings.ToArray())));
            }
            catch { SetStatus("配置已经提交，界面收尾失败。请重新读取；不要按未保存重复操作。"); }
        }
    }
    public sealed class RuntimePathsForm : Form
    {
        readonly TextBox python = new TextBox(), worker = new TextBox();
        public string PythonPath { get { return python.Text; } }
        public string WorkerPath { get { return worker.Text; } }
        public RuntimePathsForm(string pythonPath, string workerPath)
        {
            Text = "本地召回 · 使用自己的 Python 与 worker"; Name = "RuntimePathsForm";
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 290); MinimumSize = new Size(660, 330);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (int h in new[] { 25, 34, 25, 34, 74, 42 }) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, h)); Controls.Add(panel);
            panel.Controls.Add(new Label { Text = "Python 解释器（用户自己的 python.exe）", Dock = DockStyle.Fill }, 0, 0);
            python.Text = pythonPath; python.Name = "PythonPath"; python.ReadOnly = true; python.Dock = DockStyle.Fill;
            var choosePython = new Button { Text = "选择 .exe…", Name = "BrowsePython", AutoSize = true };
            panel.Controls.Add(python, 0, 1); panel.Controls.Add(choosePython, 1, 1);
            panel.Controls.Add(new Label { Text = "匹配插件协议、来源已核对的 semantic worker（.py）", Dock = DockStyle.Fill }, 0, 2);
            worker.Text = workerPath; worker.Name = "WorkerPath"; worker.ReadOnly = true; worker.Dock = DockStyle.Fill;
            var chooseWorker = new Button { Text = "选择 .py…", Name = "BrowseWorker", AutoSize = true };
            panel.Controls.Add(worker, 0, 3); panel.Controls.Add(chooseWorker, 1, 3);
            var note = new Label { Text = "无需把整个 Python 放进插件包。选择自己已安装环境中的解释器；模型目录在主窗口选择。\r\nworker 同目录需保留匹配的 model-manifest.json；本工具不下载、安装或执行所选文件。\r\n采用路径只更新草稿；保存更换须先明确关闭召回，重新读取并检查后才能再启用。\r\n取消不会改动草稿或配置。", Dock = DockStyle.Fill }; panel.Controls.Add(note, 0, 4); panel.SetColumnSpan(note, 2);
            var ok = new Button { Text = "采用路径（未保存）", Name = "AcceptRuntimePaths", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Name = "CancelRuntimePaths", AutoSize = true, DialogResult = DialogResult.Cancel };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; footer.Controls.Add(cancel); footer.Controls.Add(ok); panel.Controls.Add(footer, 0, 5); panel.SetColumnSpan(footer, 2);
            choosePython.Click += delegate { ChooseFile(python, "Python 解释器|*.exe"); };
            chooseWorker.Click += delegate { ChooseFile(worker, "Python worker|*.py"); };
            AcceptButton = ok; CancelButton = cancel;
        }
        void ChooseFile(TextBox target, string filter)
        {
            using (var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false })
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
        }
    }
}
