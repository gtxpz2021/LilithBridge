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
        public bool Plugin = true, Loader = true, Runtime, Model, Configure = true, EnableRecall, Network = true, DownloadPlugin, Confirm, ProviderEdited;
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
        public string State, Message, DataRoot, HandoffPlanPath; public bool PluginInstalled, ConfigurationSaved, RecallVerified;
    }
    public sealed class ReleaseUpdate
    {
        public string Tag, Page; public DownloadSpec Asset; public long ReleaseId, AssetId;
    }
    public static class WizardCore
    {
        public const bool PreviewChannel = true;
        public const int InstallerSchema = 2, SupportedConfigSchema = 1;
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
                if (release.ContainsKey("prerelease") && (bool)release["prerelease"] != PreviewChannel) continue;
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
            SetupCore.Require(!options.EnableRecall || (options.Runtime && options.Model && options.Configure), "LBWIZ_RECALL_REQUIRES_COMPONENTS_AND_CONFIG");
            var result = new WizardResult(); string game = SetupCore.SafePath(options.GameRoot, null), package = SetupCore.SafePath(options.PackageRoot, null), cache = SetupCore.SafePath(options.CacheRoot, null);
            SetupCore.NoReparse(game); SetupCore.NoReparse(package); SetupCore.NoReparse(cache);
            string manifestRoot = package;
            Action<string, long, long> transfer = (name, done, total) => Say(report, "download", "working", "正在准备" + (name.EndsWith(".whl") ? "召回依赖" : name.EndsWith(".nupkg") ? "私有 Python" : name.StartsWith("BepInEx") ? "游戏加载器" : name.StartsWith("LilithBridge") ? "插件安装包" : "本地模型") + "（" + (done / 1048576) + " / " + Math.Max(1, total / 1048576) + " MiB）", total == 0 ? 0 : (int)(100 * done / total));
            Say(report, "check", "working", "检查游戏、已有组件与配置", 0); cancel.ThrowIfCancellationRequested();
            string plugin = Path.Combine(game, InstallManifest.Plugin); CopyContext existing = null; Snapshot original = null;
            if (File.Exists(plugin)) { existing = SetupCore.Inspect(game); original = SetupCore.ReadSnapshot(existing.ConfigPath); SetupCore.Require(original.Digest == options.ExpectedConfigDigest, "LBSETUP_CONFIG_CHANGED_RECHECK"); }
            else { string retained = InstallerCore.FindRetainedDataRoot(game); string digest = retained == null ? "MISSING" : SetupCore.ReadSnapshot(Path.Combine(retained, "config.json")).Digest; SetupCore.Require(options.ExpectedConfigDigest == digest, "LBSETUP_CONFIG_CHANGED_RECHECK"); }
            if (options.Plugin && options.DownloadPlugin)
            {
                DownloadSpec spec = options.Update == null ? TrustedDownloads.PluginRelease : options.Update.Asset;
                string archive = TrustedDownloads.Acquire(spec, cache, options.OfflineRoot, options.Network, transfer, cancel);
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
                string archive = TrustedDownloads.Acquire(TrustedDownloads.Loader, cache, options.OfflineRoot, options.Network, transfer, cancel);
                var preview = LoaderPreparation.PreviewZip(archive, game, Path.Combine(package, "loader-files.json")); SetupCore.Require(preview.Conflicts.Count == 0, "LBWIZ_LOADER_CONFLICT_EXISTING_FILES_PRESERVED");
                cancel.ThrowIfCancellationRequested(); var installed = LoaderPreparation.ExecuteZip(preview.Plan);
                SetupCore.Require(installed.State == "PendingFirstRun", "LBWIZ_LOADER_NOT_PREPARED"); host = HostCapabilities.Inspect(game, Path.Combine(manifestRoot, "payload/LilithBridge.dll"));
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
            Receipt(context.DataRoot, "plugin", "verified");
            if (options.Plugin && options.DownloadPlugin)
            {
                var spec = options.Update == null ? TrustedDownloads.PluginRelease : options.Update.Asset;
                string record = WizardProvisioning.PathIn(context.DataRoot, ".release-provenance-" + manifest.PluginSha256.Substring(0, 16) + ".json"), temp = record + ".tmp"; SetupCore.NoReparse(temp);
                File.WriteAllText(temp, DataJson.Write(new Dictionary<string, object> { { "repository", "gtxpz2021/LilithBridge" }, { "release_id", options.Update == null ? 0 : options.Update.ReleaseId }, { "asset_id", options.Update == null ? 0 : options.Update.AssetId }, { "tag", options.Update == null ? "preview-20261029-r2" : options.Update.Tag }, { "asset_sha256", spec.Hash }, { "plugin_sha256", manifest.PluginSha256 }, { "installer_schema", 2 } }), new UTF8Encoding(false));
                if (File.Exists(record)) File.Replace(temp, record, null); else File.Move(temp, record);
            }
            Say(report, "plugin", "done", "插件已就绪；已有配置与记忆仍使用原目录", 35);
            string components = "", python = "", worker = "", model = ""; var validator = new ModelValidator(Path.Combine(package, "model-manifest.json"));
            LocalCheck existingPaths = new LocalCheck(); bool existingRuntime = false, existingModel = false;
            if (options.Runtime || options.Model)
            {
                existingPaths = SetupCore.Locate(context, snapshot, "");
                try { SetupCore.ValidateRuntimeFiles(existingPaths); existingRuntime = SetupCore.FileHash(existingPaths.Worker) == WizardProvisioning.WorkerHash; } catch (SetupException) { }
                try { validator.Verify(existingPaths.Model); existingModel = true; } catch (SetupException) { }
            }
            if (options.Runtime)
            {
                Say(report, "runtime", "working", existingRuntime ? "检查已有召回环境" : "准备私有 Python 与隔离召回环境", 40);
                if (existingRuntime) { python = existingPaths.Python; worker = existingPaths.Worker; }
                else
                {
                    components = WizardProvisioning.ManagedRuntimeBase(context.DataRoot, options.RuntimeStorageRoot); string archive = TrustedDownloads.Acquire(TrustedDownloads.Python, cache, options.OfflineRoot, options.Network, transfer, cancel);
                    string basePython = WizardProvisioning.InstallPrivatePython(archive, components, cancel);
                    var wheels = TrustedDownloads.Wheels(Path.Combine(package, "optional-recall/wheels.lock.json"));
                    foreach (var wheel in wheels) TrustedDownloads.Acquire(wheel, WizardProvisioning.PathIn(cache, "wheelhouse"), String.IsNullOrEmpty(options.OfflineRoot) ? "" : WizardProvisioning.PathIn(options.OfflineRoot, "wheelhouse"), options.Network, transfer, cancel);
                    string runtime = WizardProvisioning.PathIn(components, "runtime"); bool ready = File.Exists(Path.Combine(runtime, "install-result.json")) && File.Exists(Path.Combine(runtime, ".venv/Scripts/python.exe"));
                    if (!ready && Directory.Exists(Path.Combine(runtime, ".venv")))
                        WizardProvisioning.RunPython(basePython, new[] { "-I", "-X", "utf8", "-B", Path.Combine(package, "optional-recall/runtime_setup.py"), "remove-venv", "--root", runtime }, components, Path.Combine(components, "incomplete-environment-recovery.log"), null, cancel);
                    // Create the venv in its final location. Never rename/move an installed venv.
                    var arguments = new List<string> { "-I", "-X", "utf8", "-B", Path.Combine(package, "optional-recall/runtime_setup.py"), ready ? "verify" : "install", "--root", runtime, "--wheel-cache", WizardProvisioning.PathIn(cache, "wheelhouse") };
                    if (!String.IsNullOrEmpty(options.OfflineRoot)) { arguments.Add("--wheel-cache"); arguments.Add(WizardProvisioning.PathIn(options.OfflineRoot, "wheelhouse")); }
                    WizardProvisioning.RunPython(basePython, arguments, components, Path.Combine(components, "environment-check.log"), text => Say(report, "runtime", "working", "正在安装或检查隔离依赖，请稍候", 55), cancel);
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
            LocalCheck checkedLocal = null;
            if (options.Runtime && options.Model)
            {
                Say(report, "verify", "working", "离线验证召回环境与模型（仅使用临时合成文本）", 85);
                string work = WizardProvisioning.PathIn(context.DataRoot, ".setup-checks");
                WizardProvisioning.RunPython(python, new[] { "-I", "-X", "utf8", "-B", Path.Combine(package, "optional-recall/verify_local_recall.py"), "--worker", worker, "--model", model, "--work", work, "--lock", Path.Combine(package, "optional-recall/wheels.lock.json") }, context.DataRoot, Path.Combine(work, "local-recall-check.log"), null, cancel);
                checkedLocal = SetupCore.CheckLocal(context, snapshot, model, validator, python, worker); result.RecallVerified = true; Say(report, "verify", "done", "本地模型已通过离线合成召回检查", 92);
            }
            cancel.ThrowIfCancellationRequested();
            if (options.Configure)
            {
                Say(report, "config", "working", "核对最终设置并一次保存", 95);
                cancel.ThrowIfCancellationRequested();
                var edit = new EditRequest { ConfirmCopy = true, ApiKey = options.ApiKey, Recall = RecallAction.Preserve };
                var currentSemantic = DataJson.Map(snapshot.Document, "semantic_recall", false); bool alreadyEnabled = DataJson.Get(currentSemantic, "enabled") is bool && (bool)DataJson.Get(currentSemantic, "enabled");
                if (!snapshot.Exists || options.ProviderEdited) SetupCore.ApplyChangedProviderText(edit, snapshot, options.BaseUrl, options.ApiModel);
                bool pathsChanged = (python.Length != 0 && !SetupCore.Same(python, existingPaths.Python)) || (worker.Length != 0 && !SetupCore.Same(worker, existingPaths.Worker)) || (model.Length != 0 && !SetupCore.Same(model, existingPaths.Model));
                if (pathsChanged)
                {
                    edit.PythonOverride = python; edit.WorkerOverride = worker; edit.ModelOverride = model;
                    edit.Recall = options.EnableRecall ? RecallAction.Enable : RecallAction.Disable; edit.CheckedLocal = checkedLocal;
                }
                else if (options.EnableRecall) { edit.Recall = RecallAction.Enable; edit.CheckedLocal = checkedLocal; }
                // No write for an unchanged existing configuration, preserving bytes and backups.
                if (!snapshot.Exists || options.ProviderEdited || options.ApiKey.Length != 0 || pathsChanged || (options.EnableRecall && !alreadyEnabled))
                { var saved = SetupCore.SaveWithCommitBoundary(context, snapshot, edit, validator, () => { Say(report, "config", "committing", "正在提交最终配置，此短步骤完成前不能取消", 98); cancel.ThrowIfCancellationRequested(); }); SetupCore.Require(saved.Committed && saved.ReadbackVerified, "LBWIZ_CONFIG_READBACK_NEEDS_REVIEW"); result.ConfigurationSaved = true; }
                else result.ConfigurationSaved = true;
                Say(report, "config", "done", "配置已保存；保存不会调用对话 API", 100); Receipt(context.DataRoot, "complete", "verified");
            }
            result.State = "Complete";
            var effective = DataJson.EffectiveProvider(SetupCore.ReadSnapshot(context.ConfigPath).Document); bool hasKey = !String.IsNullOrWhiteSpace(DataJson.String(effective, "api_key", ""));
            result.Message = "所选组件已完成。" + (options.EnableRecall ? "本地召回已启用。" : "") + (hasKey ? "可以关闭向导后启动游戏。" : "对话 API Key 尚未填写，可稍后在此窗口填入并保存。") + "游戏内效果仍需实际启动确认。"; return result;
        }
        public static string Explain(Exception error)
        {
            if (error is OperationCanceledException) return "已停止。完成并核验的组件会保留，重新开始时会检查并复用。";
            if (error is WebException) { var detail = NetworkDiagnostics.Error(error); if ((string)detail["category"] == "windows_schannel_credentials_unavailable") return "Windows HTTPS 当前运行上下文的安全凭证不可用（0x8009030E），下载尚未完成。可以运行包内无 Key 网络诊断入口一次核对，或选择离线包目录；已核验文件会复用。"; return "下载未完成。请检查网络、代理或证书状态后重试，也可选择离线包目录。已核验的下载会复用；安全校验保持启用。"; }
            if (error is UnauthorizedAccessException) return "当前目录没有写入权限。请使用可写的独立游戏目录，或检查系统给出的权限提示。";
            string code = SetupCore.SafeError(error);
            if (code == "LBWIZ_RUNTIME_INSTALL_FAILED") return "召回环境未通过检查，配置尚未切换。可重试，或检查系统运行库状态；脱敏技术记录在产品专用环境目录或数据目录的 .setup-checks 中。";
            if (code == "LBSETUP_CONFIG_CHANGED_RECHECK") return "配置在操作期间发生变化，本次没有覆盖。点“重新读取”核对后再继续。";
            if (code == "LBSETUP_NEW_SERVICE_REQUIRES_OWN_KEY_OR_EXPLICIT_CLEAR") return "服务地址已经更换。请填写新服务对应的 Key；原服务的 Key 尚未修改，也不会自动带到新服务。";
            if (code == "LBWIZ_OFFLINE_PACKAGE_INCOMPLETE") return "离线包缺少所选组件的文件。请补齐离线包，或勾选允许从官方来源下载后继续。";
            if (code == "LBWIZ_NOT_ENOUGH_FREE_SPACE") return "可用空间不足。勾选本地召回时建议预留至少 8 GiB；释放空间后可重新检查。";
            if (code == "LBWIZ_DOWNLOAD_INTEGRITY_FAILED") return "下载未通过完整性校验，损坏文件已隔离。可重新开始下载，或选择完整离线包。";
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
