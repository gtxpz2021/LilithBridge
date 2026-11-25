using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace LilithSetup
{
    public sealed class WizardOptions
    {
        public string GameRoot, PackageRoot, CacheRoot = TrustedDownloads.DefaultCache, OfflineRoot = "", ExpectedConfigDigest = "MISSING";
        public string ApiKey = "", BaseUrl = ProviderPresets.DeepSeekUrl, ApiModel = ProviderPresets.DefaultModel;
        public bool KeepService = true;
        public bool ConfigureBundledStory, SwitchExistingStory, AllowStoryModelData;
        public bool Plugin = true, Loader = true, Runtime = true, Model = true, Configure = true, EnableRecall = true, VoicePack, Network = true, DownloadPlugin, Confirm = true, ProviderEdited;
        public ReleaseUpdate Update;
        internal string RuntimeStorageRoot = "";
        internal bool FromHandoff, LostHandoffDraft;
    }
    public sealed class WizardProgress
    {
        public string Step, State, Message; public int Percent;
        public WizardProgress(string step, string state, string message, int percent) { Step = step; State = state; Message = message; Percent = percent; }
    }
    public sealed class WizardResult
    {
        public string State, Message, DataRoot, HandoffPlanPath; public bool PluginInstalled, ConfigurationSaved, RecallVerified, VoiceVerified;
    }
    public sealed class ReleaseUpdate
    {
        public string Tag, Page; public DownloadSpec Asset; public long ReleaseId, AssetId;
    }
    public static class WizardCore
    {
        public const bool PreviewChannel = true;
        public const int InstallerSchema = 18, SupportedConfigSchema = 1;
        public static ReleaseUpdate CheckUpdate(CancellationToken cancel)
        {
            TrustedDownloads.UsePlatformTls();
            // Only repository-owned release metadata from this fixed repository is considered.
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/gtxpz2021/LilithBridge/releases?per_page=10");
            request.UserAgent = "LilithBridge-Installer/0.2"; request.Accept = "application/vnd.github+json"; request.Timeout = 30000; request.ReadWriteTimeout = 30000; request.AllowAutoRedirect = false;
            string json;
            using (cancel.Register(request.Abort)) using (var response = request.GetResponse()) using (var stream = response.GetResponseStream()) using (var reader = new StreamReader(stream, Encoding.UTF8))
            { var text = new StringBuilder(); char[] buffer = new char[8192]; int count; while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) { cancel.ThrowIfCancellationRequested(); SetupCore.Require(text.Length + count <= 1048576, "LBWIZ_RELEASE_METADATA_TOO_LARGE"); text.Append(buffer, 0, count); } json = text.ToString(); }
            return ParseReleaseMetadata(json);
        }
        internal static ReleaseUpdate ParseReleaseMetadata(string json)
        {
            var wrapped = DataJson.Read("{\"releases\":" + json + "}");
            foreach (Dictionary<string, object> release in (object[])wrapped["releases"])
            {
                if ((bool)release["draft"]) continue;
                bool includePreview = PreviewChannel;
                if (!includePreview && release.ContainsKey("prerelease") && (bool)release["prerelease"]) continue;
                string tag = DataJson.String(release, "tag_name", "");
                SetupCore.Require(System.Text.RegularExpressions.Regex.IsMatch(tag, "^[A-Za-z0-9._-]{1,100}$"), "LBWIZ_RELEASE_TAG_INVALID");
                var candidates = new List<DownloadSpec>(); var assetIds = new Dictionary<string, long>();
                foreach (Dictionary<string, object> asset in (object[])release["assets"])
                {
                    string name = DataJson.String(asset, "name", "");
                    if (!TrustedDownloads.IsInstallerArchive(name)) continue;
                    string digest = DataJson.String(asset, "digest", ""), url = DataJson.String(asset, "browser_download_url", ""); long bytes = Convert.ToInt64(asset["size"]);
                    SetupCore.Require(digest.StartsWith("sha256:") && bytes > 0 && bytes <= 52428800 && url == "https://github.com/gtxpz2021/LilithBridge/releases/download/" + tag + "/" + name, "LBWIZ_RELEASE_NEEDS_VERIFIABLE_ASSET");
                    candidates.Add(new DownloadSpec(name, url, bytes, digest.Substring(7), "github.com", "release-assets.githubusercontent.com"));
                    assetIds[name] = Convert.ToInt64(asset["id"]);
                }
                if (candidates.Count == 0) continue;
                // Timestamped filenames sort consistently; prefer r10 over r9 by natural numeric suffix.
                DownloadSpec chosen = candidates.OrderBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.Ordinal).Last();
                return new ReleaseUpdate { Tag = tag, Page = "https://github.com/gtxpz2021/LilithBridge/releases/tag/" + tag, Asset = chosen, ReleaseId = Convert.ToInt64(release["id"]), AssetId = assetIds[chosen.Name] };
            }
            throw new SetupException("LBWIZ_NO_VERIFIABLE_RELEASE");
        }
        public static string StagePlugin(string archive, DownloadSpec spec, string cache)
        {
            SetupCore.Require(TrustedDownloads.Matches(archive, spec), "LBWIZ_RELEASE_ARCHIVE_CHANGED");
            string stage = WizardProvisioning.PathIn(cache, "plugin-" + spec.Hash.Substring(0, 16));
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (string relative in new[] { "install-manifest.json", "payload/LilithBridge.dll" })
                {
                    string name = "setup/" + relative; var entries = zip.Entries.Where(e => e.FullName == name).ToArray();
                    SetupCore.Require(entries.Length == 1 && entries[0].Length > 0 && entries[0].Length < 8388608, "LBWIZ_RELEASE_PACKAGE_FORMAT_UNSUPPORTED");
                    byte[] bytes; using (var input = entries[0].Open()) using (var output = new MemoryStream()) { input.CopyTo(output); bytes = output.ToArray(); }
                    string dest = WizardProvisioning.PathIn(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest)) SetupCore.Require(SetupCore.FileHash(dest) == SetupCore.Hash(bytes), "LBWIZ_STAGED_RELEASE_CHANGED");
                    else File.WriteAllBytes(dest, bytes);
                }
            }
            var manifest = InstallManifest.Load(stage); var document = DataJson.Read(File.ReadAllText(Path.Combine(stage, "install-manifest.json")));
            SetupCore.Require((!document.ContainsKey("min_installer_schema") || Convert.ToInt32(document["min_installer_schema"]) <= InstallerSchema) && (!document.ContainsKey("config_schema") || Convert.ToInt32(document["config_schema"]) <= SupportedConfigSchema), "LBWIZ_INSTALLER_UPDATE_REQUIRED");
            SetupCore.Require(SetupCore.FileHash(Path.Combine(stage, "payload/LilithBridge.dll")) == manifest.PluginSha256, "LBWIZ_RELEASE_PLUGIN_HASH_MISMATCH");
            // No executable or script from a downloaded package is launched by this installer.
            return stage;
        }
        static void Say(Action<WizardProgress> report, string step, string state, string text, int percent)
        { if (report != null) report(new WizardProgress(step, state, text, percent)); }
        static void Receipt(string data, string stage, string state)
        {
            // No provider fields, API keys, corpus or machine paths enter the resumable receipt.
            string path = WizardProvisioning.PathIn(data, ".unified-setup-state.json"); Directory.CreateDirectory(data);
            string temp = path + ".tmp"; SetupCore.NoReparse(temp);
            File.WriteAllText(temp, DataJson.Write(new Dictionary<string, object> { { "schema", 1 }, { "stage", stage }, { "state", state }, { "utc", DateTime.UtcNow.ToString("o") } }), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static WizardResult Execute(WizardOptions options, Action<WizardProgress> report, CancellationToken cancel)
        {
            SetupCore.Require(options != null && options.Confirm, "LBWIZ_CONFIRM_SELECTION_FIRST");
            SetupCore.Require(!options.Plugin || !options.DownloadPlugin || options.Update != null, "LBWIZ_CHECK_UPDATE_FIRST");
            SetupCore.Require(!options.ConfigureBundledStory || options.Configure, "LBWIZ_STORY_REQUIRES_CONFIG_SELECTION");
            SetupCore.Require(!options.EnableRecall || (options.Runtime && options.Model && options.Configure), "LBWIZ_RECALL_REQUIRES_COMPONENTS_AND_CONFIG");
            SetupCore.Require(!options.VoicePack || options.Configure, "LBWIZ_VOICE_REQUIRES_CONFIG_SELECTION");
            var result = new WizardResult(); string game = SetupCore.SafePath(options.GameRoot, null), package = SetupCore.SafePath(options.PackageRoot, null), cache = SetupCore.SafePath(options.CacheRoot, null);
            SetupCore.NoReparse(game); SetupCore.NoReparse(package); SetupCore.NoReparse(cache);
            string manifestRoot = package;
            Action<string, long, long> transfer = (name, done, total) => { if (name.Contains("校验本地缓存SHA256")) { Say(report, "download", "working", "正在校验本地缓存：" + name.Split('@')[0].Trim() + "（文件大小 " + (total / 1048576.0).ToString("F1") + " MiB）", 0); return; } if (name.StartsWith("正在校验模型文件", StringComparison.Ordinal)) { Say(report, "model", "working", name, 78); return; } Say(report, "download", "working", "正在准备" + (name.Contains(".whl") ? "召回依赖" : name.Contains(".nupkg") ? "私有 Python" : name.StartsWith("BepInEx") ? "游戏加载器" : name.StartsWith("LilithBridge") ? "插件安装包" : "本地模型") + "：" + name + "（" + (done / 1048576) + " / " + Math.Max(1, total / 1048576) + " MiB）", total == 0 ? 0 : (int)(100 * done / total)); };
            Say(report, "check", "working", "检查游戏、已有组件与配置", 0); cancel.ThrowIfCancellationRequested();
            string plugin = Path.Combine(game, InstallManifest.Plugin); CopyContext existing = null; Snapshot original = null;
            if (File.Exists(plugin)) { existing = SetupCore.Inspect(game); original = SetupCore.ReadSnapshot(existing.ConfigPath); SetupCore.Require(original.Digest == options.ExpectedConfigDigest, "LBSETUP_CONFIG_CHANGED_RECHECK"); }
            else { string retained = InstallerCore.FindRetainedDataRoot(game); string digest = retained == null ? "MISSING" : SetupCore.ReadSnapshot(Path.Combine(retained, "config.json")).Digest; SetupCore.Require(options.ExpectedConfigDigest == digest, "LBSETUP_CONFIG_CHANGED_RECHECK"); }
            if (options.Plugin && options.DownloadPlugin)
            {
                DownloadSpec spec = options.Update == null ? TrustedDownloads.PluginRelease : options.Update.Asset;
                string archive = TrustedDownloads.Acquire(spec, cache, options.OfflineRoot, options.Network, transfer, cancel, package, "");
                try { manifestRoot = StagePlugin(archive, spec, cache); }
                catch (SetupException error)
                {
                    if (error.Message != "LBWIZ_INSTALLER_UPDATE_REQUIRED") throw;
                    string handoff = InstallerHandoff.Prepare(archive, spec, options);
                    return new WizardResult { State = "InstallerHandoffReady", HandoffPlanPath = handoff, Message = "新版安装器已下载并核验。正在切换窗口继续已确认的组件选择；未保存的 Key 不会传递，请在新窗口亲自填写。" };
                }
            }
            InstallManifest manifest = InstallManifest.Load(manifestRoot);
            var running = System.Diagnostics.Process.GetProcessesByName("Lilith"); bool gameRunning = running.Length > 0; foreach (var process in running) process.Dispose();
            if (gameRunning) { result.State = "WaitingForExit"; result.Message = "所需插件包已准备。请正常退出游戏，再点“继续安装”；运行中的游戏文件不会替换。关闭此窗口后，下次会复用已核验下载。"; return result; }
            InstallerCore.RequireStopped();
            var host = HostCapabilities.Inspect(game, Path.Combine(manifestRoot, "payload/LilithBridge.dll")); SetupCore.Require(host.BasicHost, "LBINSTALL_SELECT_CORRECT_LILITH_HOST_FIRST");
            if (host.State == "NeedsLoader" && options.Loader)
            {
                Say(report, "loader", "working", "准备游戏加载器（已有不同文件会保留并停止）", 10);
                string archive = TrustedDownloads.Acquire(TrustedDownloads.Loader, cache, options.OfflineRoot, options.Network, transfer, cancel, package, "loader");
                var preview = LoaderPreparation.PreviewZip(archive, game, Path.Combine(package, "loader-files.json")); SetupCore.Require(preview.Conflicts.Count == 0, "LBWIZ_LOADER_CONFLICT_EXISTING_FILES_PRESERVED");
                cancel.ThrowIfCancellationRequested(); var installed = LoaderPreparation.ExecuteZip(preview.Plan);
                SetupCore.Require(installed.State == "PendingFirstRun", "LBWIZ_LOADER_NOT_PREPARED"); host = HostCapabilities.Inspect(game, Path.Combine(manifestRoot, "payload/LilithBridge.dll"));
            }
            if (options.Plugin || options.Loader)
            {
                bool changedConsole = ConsoleStartupSettings.Apply(game);
                Say(report, "console", "done", changedConsole ? "已关闭启动日志控制台；其他加载器设置保持原样" : "启动日志控制台已关闭；文件日志设置保持原样", 19);
            }
            Say(report, "loader", host.CanInstall ? "done" : "waiting", host.CanInstall ? "游戏运行文件检查通过" : "等待游戏完成首次初始化", 20);
            if (host.State == "PendingFirstRun" || host.State == "PendingRegeneration")
            { result.State = "WaitingForGame"; result.Message = "加载器已准备，尚未完成安装。请主动启动所选目录中的游戏，等待加载器完成准备后退出，再点“继续安装”。窗口可以保持打开。"; return result; }
            SetupCore.Require(host.CanInstall, "LBINSTALL_HOST_" + host.State);
            Say(report, "check", "done", host.InteropFresh ? "游戏接口检查通过" : "所需接口仍满足；加载器将在启动时按需刷新运行文件", 22);
            cancel.ThrowIfCancellationRequested();
            if (options.Plugin)
            {
                Say(report, "plugin", "working", "检查并安装插件，保留可回退备份", 25);
                var plan = InstallerCore.Inspect(manifest, game); var install = InstallerCore.Install(manifest, plan, true); result.PluginInstalled = install.Committed || install.NoChange;
            }
            CopyContext context = SetupCore.Inspect(game); result.DataRoot = context.DataRoot; Snapshot snapshot = SetupCore.ReadSnapshot(context.ConfigPath);
            SetupCore.Require(snapshot.Digest == options.ExpectedConfigDigest, "LBSETUP_CONFIG_CHANGED_RECHECK");
            ExistingDataRoot.EnsureApprovedDirectory(context.GameRoot); Receipt(context.DataRoot, "plugin", "verified");
            string storyResult = options.ConfigureBundledStory ? BundledStory.Prepare(package, context) : "not-selected"; Say(report, "story", "done", BundledStory.Guidance(storyResult), 28);
            if (options.Plugin && options.DownloadPlugin)
            {
                var spec = options.Update == null ? TrustedDownloads.PluginRelease : options.Update.Asset;
                string record = WizardProvisioning.PathIn(context.DataRoot, ".release-provenance-" + manifest.PluginSha256.Substring(0, 16) + ".json"), temp = record + ".tmp"; SetupCore.NoReparse(temp);
                File.WriteAllText(temp, DataJson.Write(new Dictionary<string, object> { { "repository", "gtxpz2021/LilithBridge" }, { "release_id", options.Update == null ? 0 : options.Update.ReleaseId }, { "asset_id", options.Update == null ? 0 : options.Update.AssetId }, { "tag", options.Update == null ? "preview-20261029-r2" : options.Update.Tag }, { "asset_sha256", spec.Hash }, { "plugin_sha256", manifest.PluginSha256 }, { "installer_schema", InstallerSchema } }), new UTF8Encoding(false));
                if (File.Exists(record)) File.Replace(temp, record, null); else File.Move(temp, record);
            }
            Say(report, "plugin", "done", "插件已就绪；已有配置与记忆仍使用原目录", 35);
            if (!options.Runtime && snapshot.Exists)
            {
                try
                {
                    var passivePaths = SetupCore.Locate(context, snapshot, "");
                    if (File.Exists(passivePaths.Python) && WizardProvisioning.UpgradeKnownWorkerInPlace(package, passivePaths.Worker))
                    {
                        Say(report, "plugin", "done", "插件与既有本地语义召回脚本已同步更新；配置与记忆保持原位", 36);
                        snapshot = SetupCore.ReadSnapshot(context.ConfigPath);
                    }
                }
                catch (SetupException) { }
            }
            if (!options.VoicePack && snapshot.Exists)
            {
                try
                {
                    if (VoicePackProvisioning.UpgradeKnownVoiceWorkersInPlace(package, context.DataRoot))
                    {
                        Say(report, "plugin", "done", "插件与既有本地语音脚本已同步更新；配置与记忆保持原位", 37);
                        snapshot = SetupCore.ReadSnapshot(context.ConfigPath);
                    }
                }
                catch (SetupException) { }
            }
            string components = "", python = "", worker = "", model = ""; var validator = new ModelValidator(Path.Combine(package, "model-manifest.json"));
            LocalCheck existingPaths = new LocalCheck(); bool existingRuntime = false, existingRuntimeNeedsWorkerUpgrade = false, existingModel = false;
            if (options.Runtime || options.Model)
            {
                existingPaths = SetupCore.Locate(context, snapshot, "");
                try
                {
                    SetupCore.ValidateRuntimeFiles(existingPaths);
                    string existingWorkerHash = SetupCore.FileHash(existingPaths.Worker);
                    existingRuntime = existingWorkerHash == WizardProvisioning.WorkerHash;
                    existingRuntimeNeedsWorkerUpgrade = !existingRuntime && WizardProvisioning.IsKnownWorkerHash(existingWorkerHash);
                }
                catch (SetupException) { }
                try { validator.Verify(existingPaths.Model, text => Say(report, "model", "working", text, 38)); existingModel = true; } catch (SetupException) { }
            }
            if (options.Runtime)
            {
                Say(report, "runtime", "working", (existingRuntime || existingRuntimeNeedsWorkerUpgrade) ? "检查并同步已有召回环境" : "准备私有 Python 与隔离召回环境", 40);
                if (existingRuntime) { python = existingPaths.Python; worker = existingPaths.Worker; }
                else if (existingRuntimeNeedsWorkerUpgrade)
                {
                    SetupCore.Require(WizardProvisioning.UpgradeKnownWorkerInPlace(package, existingPaths.Worker), "LBWIZ_EXISTING_COMPONENT_CHANGED");
                    python = existingPaths.Python; worker = existingPaths.Worker;
                }
                else
                {
                    components = WizardProvisioning.ManagedRuntimeBase(context.DataRoot, options.RuntimeStorageRoot);
                    // Reuse explicit offline files first, then the package cache, then verified local cache.
                    string archive = TrustedDownloads.Acquire(TrustedDownloads.Python, cache, options.OfflineRoot, options.Network, transfer, cancel, package, "");
                    Say(report, "runtime", "working", "正在展开或核对私有Python解释器（本地文件）", 42);
                    string basePython = WizardProvisioning.InstallPrivatePython(archive, components, cancel);
                    var wheels = TrustedDownloads.Wheels(Path.Combine(package, "optional-recall/wheels.lock.json"));
                    string verifiedWheelhouse = WizardProvisioning.PrepareWheels(wheels, package, cache, options.OfflineRoot, options.Network, transfer, cancel);
                    string runtime = WizardProvisioning.PathIn(components, "runtime"); bool ready = File.Exists(Path.Combine(runtime, "install-result.json")) && File.Exists(Path.Combine(runtime, ".venv/Scripts/python.exe"));
                    if (!ready && Directory.Exists(Path.Combine(runtime, ".venv")))
                        WizardProvisioning.RunPython(basePython, new[] { "-I", "-X", "utf8", "-B", Path.Combine(package, "optional-recall/runtime_setup.py"), "remove-venv", "--root", runtime }, components, Path.Combine(components, "incomplete-environment-recovery.log"), null, cancel);
                    // Create the venv in its final location. Never rename/move an installed venv.
                    var arguments = new List<string> { "-I", "-X", "utf8", "-B", Path.Combine(package, "optional-recall/runtime_setup.py"), ready ? "verify" : "install", "--root", runtime };
                    arguments.Add("--wheel-cache"); arguments.Add(verifiedWheelhouse);
                    WizardProvisioning.RunPython(basePython, arguments, components, Path.Combine(components, "environment-check.log"), text => Say(report, "runtime", "working", text, 55), cancel);
                    python = Path.Combine(runtime, ".venv/Scripts/python.exe"); worker = WizardProvisioning.DeployWorker(package, components);
                }
                Say(report, "runtime", "done", "召回环境文件已准备", 65); Receipt(context.DataRoot, "runtime", "verified");
            }
            if (options.Model)
            {
                Say(report, "model", "working", existingModel ? "核验已有 BGE 模型" : "下载并核验本地 BGE 模型", 70);
                if (existingModel) model = existingPaths.Model;
                else { if (components.Length == 0) components = WizardProvisioning.PrivateBase(context.DataRoot); model = WizardProvisioning.InstallModel(package, components, cache, options.OfflineRoot, options.Network, transfer, cancel); }
                Say(report, "model", "done", "BGE 模型文件核验通过", 80); Receipt(context.DataRoot, "model", "verified");
            }
            VoiceInstallPatch voicePatch = null;
            if (options.VoicePack)
            {
                Say(report, "voice", "working", "准备莉莉丝本地语音包（TTS 语音播报 + F8 语音输入）", 81);
                string voiceBase = WizardProvisioning.ManagedRuntimeBase(context.DataRoot, options.RuntimeStorageRoot);
                voicePatch = VoicePackProvisioning.Install(package, context.DataRoot, voiceBase, cache, options.OfflineRoot, options.Network, snapshot, transfer, text => Say(report, "voice", "working", text, 83), cancel);
                result.VoiceVerified = true;
                Say(report, "voice", "done", "本地语音包（TTS + STT）核验通过", 84);
                Receipt(context.DataRoot, "voice", "verified");
            }
            LocalCheck checkedLocal = null;
            if (options.Runtime && options.Model)
            {
                Say(report, "verify", "working", "离线验证召回环境与模型（仅使用临时合成文本）", 85);
                string work = WizardProvisioning.PathIn(context.DataRoot, ".setup-checks");
                WizardProvisioning.RunPython(python, new[] { "-I", "-X", "utf8", "-B", Path.Combine(package, "optional-recall/verify_local_recall.py"), "--worker", worker, "--model", model, "--work", work, "--lock", Path.Combine(package, "optional-recall/wheels.lock.json") }, context.DataRoot, Path.Combine(work, "local-recall-check.log"), text => Say(report, "verify", "working", text, 85), cancel);
                Say(report, "verify", "working", "正在核对本地文件与配置版本，尚未提交配置", 90);
                checkedLocal = SetupCore.CheckLocal(context, snapshot, model, validator, python, worker); result.RecallVerified = true; Say(report, "verify", "done", "本地模型已通过离线合成召回检查", 92);
            }
            else if (options.VoicePack)
            {
                Say(report, "verify", "done", "语音包组件已通过离线完整性与进程握手检查", 92);
            }
            cancel.ThrowIfCancellationRequested();
            if (options.Configure)
            {
                Say(report, "config", "working", "核对最终设置并一次保存", 95);
                cancel.ThrowIfCancellationRequested();
                var edit = new EditRequest { ConfirmCopy = true, SelectDeepSeek = !options.KeepService, ApiKey = options.KeepService ? "" : options.ApiKey, Recall = RecallAction.Preserve, ConfigureStory = options.ConfigureBundledStory, SwitchExistingStory = options.SwitchExistingStory, AllowStoryModelData = options.AllowStoryModelData, StoryFile = Path.Combine(context.DataRoot,"private-story/first-game.collection.jsonl"), Voice = voicePatch };
                var currentSemantic = DataJson.Map(snapshot.Document, "semantic_recall", false); bool alreadyEnabled = DataJson.Get(currentSemantic, "enabled") is bool && (bool)DataJson.Get(currentSemantic, "enabled");
                if (!options.KeepService && (!snapshot.Exists || options.ProviderEdited)) SetupCore.ApplyChangedProviderText(edit, snapshot, options.BaseUrl, options.ApiModel);
                bool pathsChanged = (python.Length != 0 && !SetupCore.Same(python, existingPaths.Python)) || (worker.Length != 0 && !SetupCore.Same(worker, existingPaths.Worker)) || (model.Length != 0 && !SetupCore.Same(model, existingPaths.Model));
                if (pathsChanged)
                {
                    edit.PythonOverride = python; edit.WorkerOverride = worker; edit.ModelOverride = model;
                    edit.Recall = options.EnableRecall ? RecallAction.Enable : RecallAction.Disable; edit.CheckedLocal = checkedLocal;
                }
                else if (options.EnableRecall) { edit.Recall = RecallAction.Enable; edit.CheckedLocal = checkedLocal; }
                // No write for an unchanged existing configuration, preserving bytes and backups.
                if (StorySetup.WouldChange(snapshot, edit) || VoicePackProvisioning.WouldChange(snapshot, voicePatch) || (!snapshot.Exists && !options.KeepService) || (!options.KeepService && (options.ProviderEdited || options.ApiKey.Length != 0 || DataJson.String(snapshot.Document, "llm_provider", "deepseek") != "deepseek")) || pathsChanged || (options.EnableRecall && !alreadyEnabled))
                { var saved = SetupCore.SaveWithCommitBoundary(context, snapshot, edit, validator, () => { Say(report, "config", "committing", "正在提交最终配置，此短步骤完成前不能取消", 98); cancel.ThrowIfCancellationRequested(); }); SetupCore.Require(saved.Committed && saved.ReadbackVerified, "LBWIZ_CONFIG_READBACK_NEEDS_REVIEW"); result.ConfigurationSaved = true; }
                else result.ConfigurationSaved = true;
                Say(report, "config", "done", "配置已保存；保存不会调用对话 API", 100); Receipt(context.DataRoot, "complete", "verified");
            }
            result.State = "Complete";
            var effective = DataJson.EffectiveProvider(SetupCore.ReadSnapshot(context.ConfigPath).Document); bool hasKey = !String.IsNullOrWhiteSpace(DataJson.String(effective, "api_key", ""));
            string voiceDone = result.VoiceVerified ? "\r\n本地语音包（莉莉丝 TTS 语音播报 + F8 按住说话）已启用并配置就绪。" : "";
            result.Message = LoginGuidance.Completion(options.KeepService, hasKey) + voiceDone + "\r\n" + StorySetup.Result(snapshot, new EditRequest{ConfigureStory=options.ConfigureBundledStory,SwitchExistingStory=options.SwitchExistingStory,AllowStoryModelData=options.AllowStoryModelData,StoryFile=Path.Combine(context.DataRoot,"private-story/first-game.collection.jsonl")}) + BuildInfo.Feedback(context) + "游戏内效果仍需实际启动确认。"; return result;
        }
        public static string Explain(Exception error)
        {
            if (error is OperationCanceledException) return "已停止。完成并核验的组件会保留，重新开始时会检查并复用。";
            if (error is WebException) { var detail = NetworkDiagnostics.Error(error); if ((string)detail["category"] == "windows_schannel_credentials_unavailable") return "Windows HTTPS 当前运行上下文的安全凭证不可用（0x8009030E），下载尚未完成。可以运行包内无 Key 网络诊断入口一次核对，或选择离线包目录；已核验文件会复用。"; return "国内镜像及官方来源未能完成本项下载。请检查网络后点“重新检查并继续”；已核验资源和可续传的下载会复用。安全校验保持启用，不保证所有网络均可连接。"; }
            if (error is UnauthorizedAccessException) return "当前目录没有写入权限。请使用可写的独立游戏目录，或检查系统给出的权限提示。";
            string code = SetupCore.SafeError(error);
            if (code == "LBINSTALL_HOST_InteropGenerationDisabled") return "游戏接口缓存缺失或已过期，但加载器自动生成已关闭。请在 BepInEx/config/BepInEx.cfg 的 IL2CPP 设置中启用 UpdateInteropAssemblies，启动所选游戏完成生成并正常退出，再重新检查。现有设置、缓存和用户数据未删除。";
            if (code == "LBINSTALL_HOST_NeedsInteropConfiguration") return "加载器的接口生成配置存在歧义或使用了未验证的 metadata/interop 路径。请保留原配置和缓存，核对标准生成路径后重新检查；本次未替换插件。";
            if (code == "LBWIZ_CONSOLE_CONFIG_AMBIGUOUS" || code == "LBWIZ_CONSOLE_CONFIG_CHANGED") return "启动日志设置存在重复或已发生变化，已停止自动处理。请保留 BepInEx/config 下的配置与备份，再重新核对；现有游戏存档未修改。";
            if (code == "LBWIZ_RUNTIME_INSTALL_FAILED") return "召回环境未通过检查，配置尚未切换。可重试，或检查系统运行库状态；脱敏技术记录在产品专用环境目录或数据目录的 .setup-checks 中。";
            if (code == "LBWIZ_EXISTING_COMPONENT_CHANGED") return "检测到本地召回脚本存在非官方手动修改，为保护已有改动未覆盖原文件。请先备份自定义修改或移开该文件后重试。";
            if (code == "LBSETUP_CONFIG_CHANGED_RECHECK") return "配置在操作期间发生变化，本次没有覆盖。点“重新读取”核对后再继续。";
            if (code == "LBSETUP_NEW_SERVICE_REQUIRES_OWN_KEY_OR_EXPLICIT_CLEAR") return "服务地址已经更换。请填写新服务对应的 Key；原服务的 Key 尚未修改，也不会自动带到新服务。";
            if (code == "LBWIZ_RESOURCE_ARCHIVE_HASH" || code == "LBWIZ_RESOURCE_ARCHIVE_CHANGED") return "旁边离线资源ZIP未通过完整性校验，该文件未投入使用。请重新取得完整资源包；现有资料不覆盖。";
            if (code == "LBWIZ_RESOURCE_ARCHIVE_SPACE") return "自动解压资源的位置空间不足。请把完整小安装包和资源ZIP放在有足够空间的可写目录后继续。";
            if (code == "LBWIZ_RESOURCE_ARCHIVE_CACHE_CHANGED") return "资源缓存目标发生变化或被占用，原文件保留。请核对后重试。";
            if (code == "LBWIZ_OFFLINE_PACKAGE_INCOMPLETE") return "离线包缺少所选组件的文件。请补齐离线包，或勾选允许从国内镜像或官方来源下载后继续。";
            if (code == "LBWIZ_NOT_ENOUGH_FREE_SPACE") return "可用空间不足。勾选本地召回建议预留 8 GiB，勾选语音包建议额外预留 4 GiB；释放空间后可重新检查。";
            if (code == "LBWIZ_VOICEPACK_NOT_FOUND") return "尚未找到莉莉丝语音包文件。请把 LilithBridge-VoicePack 分卷（.zip.001/.002…）或语音包 ZIP 放在安装程序同一目录，或点“选语音包…”指定位置。";
            if (code == "LBWIZ_VOICEPACK_PART_MISSING") return "语音包分卷不完整（例如缺少 .zip.002 等后续分卷）。请把全部分卷下载到同一文件夹后重试。";
            if (code == "LBWIZ_VOICEPACK_HASH_MISMATCH" || code == "LBWIZ_VOICEPACK_MANIFEST_INVALID") return "语音包文件未通过完整性校验，损坏文件未写入游戏目录。请重新核对语音包分卷完整性后重试。";
            if (code == "LBWIZ_VOICE_RUNTIME_FAILED") return "本地语音 Python 环境准备未完成，原有配置未改动。请检查网络或确认使用包含离线依赖的完整懒人语音包后重试。";
            if (code == "LBWIZ_VOICE_REQUIRES_CONFIG_SELECTION") return "启用语音包需要同时勾选“保存所选配置”，以便自动写入语音路径与开关。";
            if (code == "LBWIZ_DOWNLOAD_INTEGRITY_FAILED") return "下载未通过完整性校验，损坏文件已隔离。可重新开始下载，或选择完整离线包。";
            if (code == "LBWIZ_CHECK_UPDATE_FIRST") return "请先点“检查项目更新”取得明确版本，或保留“安装包内的已核验版本”。不会自动改装旧版插件。";
            if (code == "LBWIZ_SELECT_COMPONENT_FIRST") return "请至少选择一个需要准备或保存的组件。";
            if (code == "LBWIZ_CONFIRM_SELECTION_FIRST") return "请先核对目标目录与所选组件，再勾选确认。";
            if (code == "LBWIZ_CONFIG_READBACK_NEEDS_REVIEW") return "配置提交后的目标或备份校验发生变化，本次不能确认完成。原版本和并发版本可能保留在唯一备份中，请先检查状态；向导不会自动抢写回滚。";
            if (code == "LBWIZ_LOADER_CONFLICT_EXISTING_FILES_PRESERVED") return "已有加载器文件与选定包不同，已保留并停止。请在独立游戏目录重试，或从恢复入口核对状态。";
            if (code == "LBWIZ_INSTALLER_UPDATE_REQUIRED") return "该发行需要更新安装器。已核验的官方安装包已保留在本机下载缓存；当前插件和配置尚未替换。请使用该包中的新版安装器继续。";
            if (code == "LBWIZ_HANDOFF_PROTOCOL_UNSUPPORTED") return "该发行需要新版安装器，但未提供受支持的安全交接信息。已保留核验后的包，当前插件与配置未替换；本次不能自动切换。";
            if (code.StartsWith("LBWIZ_HANDOFF_")) return "安装器切换尚未完成，原安装包仍可打开，插件和配置没有因切换而被替换。请保留检查代码：" + code;
            if (code.StartsWith("LBWIZ_OFFLINE_FILE_MISSING:")) return "离线包缺少文件：" + code.Substring(code.IndexOf(':') + 1) + "。可补齐离线包，或勾选联网下载后重试。";
            return InstallerCore.Explain(error) + "\r\n已完成的组件会保留。重新开始会先检查；配置只在最后核验后提交。";
        }
    }
}
