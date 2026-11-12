using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace LilithSetup
{
    public static class WizardPlanning
    {
        public static string Preview(WizardOptions options)
        {
            SetupCore.Require(options.Plugin || options.Loader || options.Runtime || options.Configure, "LBWIZ_SELECT_COMPONENT_FIRST");
            string root = SetupCore.SafePath(options.GameRoot, null); SetupCore.NoReparse(root);
            var manifest = InstallManifest.Load(options.PackageRoot); var host = HostCapabilities.Inspect(root, Path.Combine(options.PackageRoot, "payload/LilithBridge.dll"));
            SetupCore.Require(host.BasicHost, "LBINSTALL_SELECT_CORRECT_LILITH_HOST_FIRST");
            Snapshot storySnapshot = new Snapshot{Exists=false,Digest="MISSING",Document=new Dictionary<string,object>()};
            var downloads = new List<Tuple<DownloadSpec, string, string>>();
            string offline = options.OfflineRoot ?? ""; bool runtimeReady = false, modelReady = false; string data = Path.Combine(root, "LilithBridgeData");
            if (File.Exists(Path.Combine(root, InstallManifest.Plugin)))
            {
                var context = SetupCore.Inspect(root); var snapshot = SetupCore.ReadSnapshot(context.ConfigPath); SetupCore.Require(snapshot.Digest == options.ExpectedConfigDigest, "LBSETUP_CONFIG_CHANGED_RECHECK"); data = context.DataRoot; storySnapshot = snapshot;
                if (options.Runtime || options.Model)
                {
                    var current = SetupCore.Locate(context, snapshot, "");
                    try { SetupCore.ValidateRuntimeFiles(current); runtimeReady = SetupCore.FileHash(current.Worker) == WizardProvisioning.WorkerHash; } catch (SetupException) { }
                    try { new ModelValidator(Path.Combine(options.PackageRoot, "model-manifest.json")).Verify(current.Model); modelReady = true; } catch (SetupException) { }
                }
            }
            else { string retained = InstallerCore.FindRetainedDataRoot(root); if (retained != null) { data = retained; storySnapshot = SetupCore.ReadSnapshot(Path.Combine(data, "config.json")); SetupCore.Require(storySnapshot.Digest == options.ExpectedConfigDigest, "LBSETUP_CONFIG_CHANGED_RECHECK"); } }
            if (options.Plugin && options.DownloadPlugin) downloads.Add(Tuple.Create(options.Update == null ? TrustedDownloads.PluginRelease : options.Update.Asset, options.CacheRoot, offline));
            if (options.Loader && host.State == "NeedsLoader") downloads.Add(Tuple.Create(TrustedDownloads.Loader, options.CacheRoot, offline));
            if (options.Runtime && !runtimeReady)
            {
                downloads.Add(Tuple.Create(TrustedDownloads.Python, options.CacheRoot, offline));
                foreach (var item in TrustedDownloads.Wheels(Path.Combine(options.PackageRoot, "optional-recall/wheels.lock.json"))) downloads.Add(Tuple.Create(item, Path.Combine(options.CacheRoot, "wheelhouse"), offline.Length == 0 ? "" : Path.Combine(offline, "wheelhouse")));
            }
            if (options.Model && !modelReady) foreach (var item in TrustedDownloads.Model(Path.Combine(options.PackageRoot, "model-manifest.json"))) downloads.Add(Tuple.Create(item, Path.Combine(options.CacheRoot, "model"), offline.Length == 0 ? "" : Path.Combine(offline, "model")));
            long missing = 0; int cached = 0;
            foreach (var item in downloads) { if (TrustedDownloads.Find(item.Item1, new[] { item.Item3, item.Item2 }) == null) missing += item.Item1.Bytes; else cached++; }
            if (!options.Network) SetupCore.Require(missing == 0, "LBWIZ_OFFLINE_PACKAGE_INCOMPLETE");
            string ancestor = data; while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
            long free = new DriveInfo(Path.GetPathRoot(ancestor)).AvailableFreeSpace;
            long budget = (options.Runtime && !runtimeReady ? 8L * 1024 * 1024 * 1024 : 350L * 1024 * 1024) + (options.Model && !modelReady ? 100L * 1024 * 1024 : 0);
            SetupCore.Require(free >= budget, "LBWIZ_NOT_ENOUGH_FREE_SPACE");
            string state = host.CanInstall ? "游戏接口检查通过" : host.State == "PendingFirstRun" || host.State == "PendingRegeneration" ? "需主动启动所选游戏完成接口生成，正常退出后重新检查再安装" : host.State == "InteropGenerationDisabled" ? "接口缓存缺失或过期，且自动生成已关闭；请启用生成后完成初始化再检查" : host.State == "NeedsInteropConfiguration" ? "接口生成路径或设置需核对，当前不能安装" : host.State == "NeedsLoader" ? "将按勾选准备缺少的游戏加载器" : "游戏运行条件需核对：" + host.State;
            string origin = options.DownloadPlugin ? (options.Update == null ? "官方已核验发行" : options.Update.Tag + " / " + options.Update.Asset.Name) : "安装包内已核验版本";
            return state + "。" + ExistingDataRoot.Review(root).Summary + "\r\n" + (options.KeepService ? "保留原服务与登录；" + LoginGuidance.InGame : "仅保存明确填写的API字段，原OpenAI/ChatGPT选择不变。") + "\r\n" + StorySetup.Plan(storySnapshot, options, data) + "\r\n插件来源：" + origin + "。\r\n目标：" + root + "\r\n预计新增下载 " + (missing / 1048576.0).ToString("F1") + " MiB；已核验缓存 " + cached + " 项。建议可用空间 " + (budget / 1073741824.0).ToString("F1") + " GiB。已有设置、模型与记忆保留；配置在最后一次核验后保存。";
        }
    }
}
