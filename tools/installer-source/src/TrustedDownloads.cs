using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

namespace LilithSetup
{
    public sealed class DownloadSpec
    {
        public readonly string Name, Url, Hash; public readonly long Bytes; internal readonly string[] Hosts;
        internal DownloadSpec(string name, string url, long bytes, string hash, params string[] hosts)
        { Name = name; Url = url; Bytes = bytes; Hash = InstallManifest.Digest(hash); Hosts = hosts; }
    }
    public static class TrustedDownloads
    {
        public static readonly DownloadSpec PluginRelease = new DownloadSpec("LilithBridge-installer-20261029-r5.zip", "https://github.com/gtxpz2021/LilithBridge/releases/download/preview-20261029-r2/LilithBridge-installer-20261029-r5.zip", 1577555, "dfe9cd914a77607d96c3d7184e09bf7126750d82d0c44cd6fdd17b26ed33611a", "github.com", "release-assets.githubusercontent.com");
        public static readonly DownloadSpec Python = new DownloadSpec("python.3.12.10.nupkg", "https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg", 14515433, "0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8", "api.nuget.org", "globalcdn.nuget.org");
        public static readonly DownloadSpec Loader = new DownloadSpec("BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769+32fb12f.zip", "https://builds.bepinex.dev/projects/bepinex_be/769/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.769%2B32fb12f.zip", 34325441, "12f70b8d7489c0931de5dada591b0e2eb6dc9d16a17161fe15f4144912f8924f", "builds.bepinex.dev");
        public const string WheelManifestHash = "17f66187aae50df1e0613305b0f1d8f42b2665ea499d8f1952483ca85d566891";
        public static void UsePlatformTls() { ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault; }
        public static bool IsInstallerArchive(string name)
        { return name != null && System.Text.RegularExpressions.Regex.IsMatch(name, "^LilithBridge-(?:unified-)?installer-(?!source(?:-|\\.))(?!changes(?:-|\\.))(?!evidence(?:-|\\.))[A-Za-z0-9._-]+\\.zip$"); }
        public static string DefaultCache { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"LilithBridge\InstallerCache\v1"); } }
        public static List<DownloadSpec> Wheels(string manifest)
        {
            SetupCore.NoReparse(manifest); SetupCore.Require(SetupCore.FileHash(manifest) == WheelManifestHash, "LBWIZ_WHEEL_LOCK_CHANGED");
            var list = new List<DownloadSpec>(); var rows = (object[])DataJson.Read(File.ReadAllText(manifest))["wheels"];
            SetupCore.Require(rows.Length == 34, "LBWIZ_WHEEL_LOCK_CHANGED");
            foreach (Dictionary<string, object> row in rows)
            {
                string name = (string)row["filename"]; SetupCore.Require(Path.GetFileName(name) == name && name.EndsWith(".whl", StringComparison.Ordinal), "LBWIZ_UNSAFE_DOWNLOAD_NAME");
                var item = new DownloadSpec(name, (string)row["url"], Convert.ToInt64(row["size_bytes"]), (string)row["sha256"], "files.pythonhosted.org", "download.pytorch.org", "download-r2.pytorch.org"); ValidateUri(new Uri(item.Url), item); list.Add(item);
            }
            return list;
        }
        public static List<DownloadSpec> Model(string manifest)
        {
            SetupCore.Require(SetupCore.FileHash(manifest) == SetupCore.ManifestHash, "LBSETUP_TRUSTED_MANIFEST_HASH_MISMATCH");
            var result = new List<DownloadSpec>();
            foreach (Dictionary<string, object> row in (object[])DataJson.Read(File.ReadAllText(manifest))["files"])
            {
                string file = (string)row["file"];
                // Model files use their immutable revision, never a URL supplied by the user.
                string url = "https://huggingface.co/BAAI/bge-small-zh-v1.5/resolve/" + SetupCore.ModelRevision + "/" + String.Join("/", file.Split('/').Select(Uri.EscapeDataString));
                result.Add(new DownloadSpec(file.Replace('/', '_'), url, Convert.ToInt64(row["bytes"]), (string)row["sha256"], "huggingface.co", "cdn-lfs.huggingface.co", "cdn-lfs-us-1.hf.co", "cas-bridge.xethub.hf.co"));
            }
            return result;
        }
        internal static void ValidateUri(Uri uri, DownloadSpec item)
        { SetupCore.Require(uri.IsAbsoluteUri && uri.Scheme == "https" && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment) && item.Hosts.Contains(uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase), "LBWIZ_DOWNLOAD_SOURCE_REFUSED"); }
        public static bool Matches(string path, DownloadSpec item)
        { SetupCore.NoReparse(path); return File.Exists(path) && new FileInfo(path).Length == item.Bytes && SetupCore.FileHash(path) == item.Hash; }
        public static string Find(DownloadSpec item, IEnumerable<string> caches)
        {
            foreach (string cache in caches.Where(x => !String.IsNullOrWhiteSpace(x)))
            {
                string path = SetupCore.SafePath(item.Name, cache); SetupCore.NoReparse(path);
                if (File.Exists(path) && Matches(path, item)) return path;
            }
            return null;
        }
        public static string Acquire(DownloadSpec item, string cache, string offline, bool allowNetwork, Action<string, long, long> progress, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); UsePlatformTls(); cache = SetupCore.SafePath(cache, null); SetupCore.NoReparse(cache);
            string existing = Find(item, new[] { offline, cache });
            if (existing != null) { if (progress != null) progress(item.Name, item.Bytes, item.Bytes); return existing; }
            SetupCore.Require(allowNetwork, "LBWIZ_OFFLINE_FILE_MISSING:" + item.Name);
            Directory.CreateDirectory(cache); string destination = Path.Combine(cache, item.Name), part = destination + ".part";
            SetupCore.NoReparse(part);
            if (File.Exists(destination)) File.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(part) && new FileInfo(part).Length >= item.Bytes)
            {
                if (Matches(part, item)) { File.Move(part, destination); return destination; }
                File.Move(part, part + ".invalid-" + Guid.NewGuid().ToString("N"));
            }
            long resume = File.Exists(part) ? new FileInfo(part).Length : 0; Uri uri = new Uri(item.Url);
            for (int redirects = 0; redirects <= 8; redirects++)
            {
                cancel.ThrowIfCancellationRequested(); ValidateUri(uri, item);
                var request = (HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect = false; request.Timeout = 45000; request.ReadWriteTimeout = 45000; request.UserAgent = "LilithBridge-Installer/0.2"; request.UseDefaultCredentials = false;
                if (resume > 0) request.AddRange(resume);
                // Framework/OS defaults validate certificates. No validation callback or protocol weakening.
                using (cancel.Register(request.Abort))
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code >= 300 && code < 400) { string location = response.Headers["Location"]; SetupCore.Require(!String.IsNullOrEmpty(location), "LBWIZ_REDIRECT_MISSING"); uri = new Uri(uri, location); continue; }
                    bool partial = code == 206 && resume > 0;
                    if (partial) SetupCore.Require(response.Headers["Content-Range"] == "bytes " + resume + "-" + (item.Bytes - 1) + "/" + item.Bytes, "LBWIZ_DOWNLOAD_RANGE_MISMATCH");
                    long start = partial ? resume : 0;
                    SetupCore.Require((code == 200 || partial) && (response.ContentLength < 0 || response.ContentLength == item.Bytes - start), "LBWIZ_DOWNLOAD_SIZE_MISMATCH");
                    using (var input = response.GetResponseStream())
                    using (var output = new FileStream(part, partial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[131072]; long total = start; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        { cancel.ThrowIfCancellationRequested(); total += count; SetupCore.Require(total <= item.Bytes, "LBWIZ_DOWNLOAD_SIZE_EXCEEDED"); output.Write(buffer, 0, count); if (progress != null) progress(item.Name, total, item.Bytes); }
                        output.Flush(true);
                    }
                    if (!Matches(part, item)) { File.Move(part, part + ".invalid-" + Guid.NewGuid().ToString("N")); throw new SetupException("LBWIZ_DOWNLOAD_INTEGRITY_FAILED"); }
                    SetupCore.Require(!File.Exists(destination), "LBWIZ_CACHE_CHANGED_RETRY"); File.Move(part, destination); return destination;
                }
            }
            throw new SetupException("LBWIZ_TOO_MANY_REDIRECTS");
        }
    }
}
