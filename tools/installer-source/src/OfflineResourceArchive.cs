using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace LilithSetup
{
    // Only exact requested locked resources are read. ZIP metadata never grants trust.
    public static class OfflineResourceArchive
    {
        static bool Resource(DownloadSpec item, string kind)
        { return kind == "wheelhouse" || kind == "model" || kind == "loader" || (kind == "" && item.Name == TrustedDownloads.Python.Name); }
        static IEnumerable<string> Archives(string package, string offline)
        {
            var roots = new List<string>();
            if (!String.IsNullOrWhiteSpace(offline)) roots.Add(SetupCore.SafePath(offline, null));
            if (!String.IsNullOrWhiteSpace(TrustedDownloads.LauncherDirectory)) roots.Add(SetupCore.SafePath(TrustedDownloads.LauncherDirectory, null));
            if (!String.IsNullOrWhiteSpace(package))
            {
                string root = SetupCore.SafePath(package, null);
                for (int i = 0; i < 3 && !String.IsNullOrEmpty(root); i++, root = Path.GetDirectoryName(root)) roots.Add(root);
            }
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                string[] files;
                try { files = Directory.GetFiles(root, "LilithBridge-offline-resources*.zip", SearchOption.TopDirectoryOnly); }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) { SetupCore.NoReparse(file); yield return file; }
            }
        }
        static ZipArchiveEntry Entry(ZipArchive zip, DownloadSpec item, string kind)
        {
            var names = new List<string>();
            foreach (string prefix in new[] { "setup/offline-cache/", "offline-cache/", "" })
            {
                if (!String.IsNullOrEmpty(kind)) names.Add(prefix + kind + "/" + item.Name);
                if (kind == "" || kind == "loader") names.Add(prefix + item.Name);
            }
            foreach (string name in names)
            {
                var entries = zip.Entries.Where(e => String.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Length == 1 && entries[0].FullName == name && entries[0].Length == item.Bytes) return entries[0];
            }
            return null;
        }
        static string Locate(DownloadSpec item, string package, string offline, string kind)
        {
            if (!Resource(item, kind)) return null;
            foreach (string file in Archives(package, offline))
            {
                try { using (var zip = ZipFile.OpenRead(file)) if (Entry(zip, item, kind) != null) return file; }
                catch (InvalidDataException) { }
                catch (IOException) { }
            }
            return null;
        }
        public static bool CanSupply(DownloadSpec item, string package, string offline, string kind)
        { return Locate(item, package, offline, kind) != null; }
        static string Target(string root, string name)
        { string target = SetupCore.SafePath(name, root); SetupCore.NoReparse(target); return target; }
        public static string TryExtract(DownloadSpec item, string cache, string offline, string package, string kind, Action<string, long, long> progress, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); string archive = Locate(item, package, offline, kind); if (archive == null) return null;
            string root = String.IsNullOrEmpty(kind) ? Path.Combine(package, "offline-cache") : Path.Combine(package, "offline-cache", kind);
            string target = Target(root, item.Name);
            // A different supplied file is preserved; use the application cache instead.
            if (File.Exists(target) || Directory.Exists(target)) { root = cache; target = Target(root, item.Name); }
            try { Directory.CreateDirectory(root); }
            catch (UnauthorizedAccessException) { root = cache; target = Target(root, item.Name); Directory.CreateDirectory(root); }
            if (TrustedDownloads.Matches(target, item)) return target;
            SetupCore.Require(!Directory.Exists(target), "LBWIZ_RESOURCE_ARCHIVE_CACHE_CHANGED");
            SetupCore.Require(new DriveInfo(Path.GetPathRoot(target)).AvailableFreeSpace >= item.Bytes + 16777216, "LBWIZ_RESOURCE_ARCHIVE_SPACE");
            string temp = target + ".unpack-" + Guid.NewGuid().ToString("N") + ".part"; SetupCore.NoReparse(temp);
            try
            {
                using (var zip = ZipFile.OpenRead(archive))
                {
                    var entry = Entry(zip, item, kind); SetupCore.Require(entry != null, "LBWIZ_RESOURCE_ARCHIVE_CHANGED");
                    if (progress != null) progress(item.Name + " @ 离线ZIP自动解压：" + Path.GetFileName(archive), 0, item.Bytes);
                    using (var input = entry.Open()) using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[131072]; long total = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        { cancel.ThrowIfCancellationRequested(); total += count; SetupCore.Require(total <= item.Bytes, "LBWIZ_RESOURCE_ARCHIVE_HASH"); output.Write(buffer, 0, count); if (progress != null) progress(item.Name + " @ 离线ZIP自动解压", total, item.Bytes); }
                        output.Flush(true);
                    }
                }
                cancel.ThrowIfCancellationRequested(); SetupCore.Require(TrustedDownloads.Matches(temp, item), "LBWIZ_RESOURCE_ARCHIVE_HASH"); SetupCore.NoReparse(target);
                if (File.Exists(target))
                {
                    // Only the application's own cache can contain a previous invalid target here.
                    if (TrustedDownloads.Matches(target, item)) return target;
                    SetupCore.Require(SetupCore.Same(root, cache), "LBWIZ_RESOURCE_ARCHIVE_CACHE_CHANGED");
                    File.Move(target, target + ".invalid-" + Guid.NewGuid().ToString("N"));
                }
                File.Move(temp, target); return target;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
