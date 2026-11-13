using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace LilithSetup
{
    public static class WizardPlanning
    {
        public static string Preview(WizardOptions options) { return Preview(options, null); }
        public static string Preview(WizardOptions options, Action<string> progress)
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
                    try { new ModelValidator(Path.Combine(options.PackageRoot, "model-manifest.json")).Verify(current.Model, progress); modelReady = true; } catch (SetupException) { }
                }
            }
            else { string retained = InstallerCore.FindRetainedDataRoot(root); if (retained != null) { data = retained; storySnapshot = SetupCore.ReadSnapshot(Path.Combine(data, "config.json")); SetupCore.Require(storySnapshot.Digest == options.ExpectedConfigDigest, "LBSETUP_CONFIG_CHANGED_RECHECK"); } }
            SetupCore.Require(!options.Plugin || !options.DownloadPlugin || options.Update != null, "LBWIZ_CHECK_UPDATE_FIRST");
            if (options.Plugin && options.DownloadPlugin) downloads.Add(Tuple.Create(options.Update == null ? TrustedDownloads.PluginRelease : options.Update.Asset, options.CacheRoot, ""));
            if (options.Loader && host.State == "NeedsLoader") downloads.Add(Tuple.Create(TrustedDownloads.Loader, options.CacheRoot, "loader"));
            if (options.Runtime && !runtimeReady)
            {
                downloads.Add(Tuple.Create(TrustedDownloads.Python, options.CacheRoot, ""));
                foreach (var item in TrustedDownloads.Wheels(Path.Combine(options.PackageRoot, "optional-recall/wheels.lock.json"))) downloads.Add(Tuple.Create(item, Path.Combine(options.CacheRoot, "wheelhouse"), "wheelhouse"));
            }
            if (options.Model && !modelReady) foreach (var item in TrustedDownloads.Model(Path.Combine(options.PackageRoot, "model-manifest.json"))) downloads.Add(Tuple.Create(item, Path.Combine(options.CacheRoot, "model"), "model"));
            long missing = 0; int cached = 0, archived = 0;
            int checkAt = 0;
            foreach (var item in downloads)
            {
                if (progress != null) progress("正在核对已下载文件 " + (++checkAt) + "/" + downloads.Count + "：" + item.Item1.Name + "（读取本地文件并校验SHA256）");
                if (TrustedDownloads.FindResource(item.Item1, item.Item2, offline, options.PackageRoot, item.Item3) != null) cached++;
                else if (OfflineResourceArchive.CanSupply(item.Item1, options.PackageRoot, offline, item.Item3)) archived++;
                else missing += item.Item1.Bytes;
            }
            if (!options.Network) SetupCore.Require(missing == 0, "LBWIZ_OFFLINE_PACKAGE_INCOMPLETE");
            string ancestor = data; while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
            long free = new DriveInfo(Path.GetPathRoot(ancestor)).AvailableFreeSpace;
            long budget = (options.Runtime && !runtimeReady ? 8L * 1024 * 1024 * 1024 : 350L * 1024 * 1024) + (options.Model && !modelReady ? 100L * 1024 * 1024 : 0);
            SetupCore.Require(free >= budget, "LBWIZ_NOT_ENOUGH_FREE_SPACE");
            string state = host.CanInstall ? "游戏接口检查通过" : host.State == "PendingFirstRun" || host.State == "PendingRegeneration" ? "需主动启动所选游戏完成接口生成，正常退出后重新检查再安装" : host.State == "InteropGenerationDisabled" ? "接口缓存缺失或过期，且自动生成已关闭；请启用生成后完成初始化再检查" : host.State == "NeedsInteropConfiguration" ? "接口生成路径或设置需核对，当前不能安装" : host.State == "NeedsLoader" ? "将按勾选准备缺少的游戏加载器" : "游戏运行条件需核对：" + host.State;
            string origin = options.DownloadPlugin ? (options.Update == null ? "官方已核验发行" : options.Update.Tag + " / " + options.Update.Asset.Name) : "安装包内已核验版本";
            return state + "。" + ExistingDataRoot.Review(root).Summary + "\r\n" + (options.KeepService ? "保留原服务与登录；" + LoginGuidance.InGame : "仅保存明确填写的API字段，原OpenAI/ChatGPT选择不变。") + "\r\n" + StorySetup.Plan(storySnapshot, options, data) + "\r\n插件来源：" + origin + "。\r\n目标：" + root + "\r\n预计新增下载 " + (missing / 1048576.0).ToString("F1") + " MiB；已核验缓存 " + cached + " 项；旁边资源ZIP待自动解压并校验 " + archived + " 项。建议可用空间 " + (budget / 1073741824.0).ToString("F1") + " GiB。已有设置、模型与记忆保留；配置在最后一次核验后保存。";
        }
    }
}
