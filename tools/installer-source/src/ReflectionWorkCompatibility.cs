using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace LilithSetup
{
    // Deliberately limited to the persisted reflection Work envelope, not a
    // promise of general memory/save compatibility. Never load a candidate DLL.
    internal static class ReflectionWorkCompatibility
    {
        // Reviewed source tree ae9e22b36bb14e91ae9fbdcc0c142ff5cec2ce8d:
        // Work + settlement recovery validated in REFLECTION-DE-VALIDATION and
        // the b05 independent recheck. The second image was rebuilt identically
        // for this guard's matrix. See INSTALLER-WORK-COMPATIBILITY-20261031.md.
        // This installer-owned record is the trust anchor; a package's version,
        // previous_plugin_hashes or self-declared capability cannot extend it.
        // Internal RC1 actual image: product source commit 718392a168714118486ba1f20114f3eed1e7177c,
        // product tree eb50e174391b7b9a291c5f8f808204a6c9abe2ba. Persisted Work/recovery
        // source is unchanged from the reviewed b05 baseline. Exact byte handoff
        // and identity evidence: docs/reviews/20261031-internal-rc1-freeze.md.
        // Internal RC2 actual image: source b826645745073bc83b874f0506023d5a95235537,
        // product tree c820cbd7587634de65b228d60d146209faa2bf09. Work persistence/recovery
        // schema is unchanged; reviewed scope/item-fact changes are request-local.
        // Final byte evidence: docs/reviews/20261031-internal-rc2-freeze.md.
        // Internal RC3 actual image: source a7f33bdadecb9f7002ce405e8ec280bebac85a49,
        // product tree dbc40c7b147198696ec86d3adef0bc2574fe909a. Only result-state wording and
        // bigram string construction changed; persisted Work/recovery is unchanged.
        // Final byte evidence: docs/reviews/20261031-internal-rc3-freeze.md.
        internal static bool SupportsWork(string imageHash)
        {
            return imageHash == "3a651d499e9ca80096d0428e4cf9d2b1d669154a5c8ef73a0c40b773f85093a4"
                || imageHash == "47e3ba80e729b651a0a295add89cee549c93d59aa26a5ecf3b5178aa0d05c207"
                || imageHash == "aab104a05a42a4397a7a04d85681bf0057042449a5e131be62f56f58610546ae"
                || imageHash == "689222dabaf21c0dfa4967a08920a731b085a595ea1d33f321504b0587bd7f04"
                || imageHash == "f7030fd9686c0fa98166d31c61b8ca2b851e23851959f6b0e9bda19da9dbfedf"
                // RC3 fix2: Work envelope, persistence and recovery sources are
                // byte-identical to public RC3. Only UI/console lifetime changed.
                // See docs/UPGRADE-COMPATIBILITY-fix2.md for the bound review.
                || imageHash == "08cef63c6315d9564f59f8d97b510c4671e4eb5ed7e71ca3e21fde76f8755bd7";
        }

        static FileAttributes? Attributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        static void RootReadable(string root)
        {
            // File/Directory.Exists suppress access errors. They cannot prove
            // absence. Check each ancestor even when the final root is absent.
            for (string p = root; !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                var a = Attributes(p);
                if (a.HasValue) SetupCore.Require((a.Value & FileAttributes.Directory) != 0
                    && (a.Value & FileAttributes.ReparsePoint) == 0, "LBINSTALL_WORK_STATE_UNKNOWN");
            }
            if (Attributes(root).HasValue)
                foreach (string ignored in Directory.EnumerateFileSystemEntries(root)) { }
        }
        static void Known(Dictionary<string, object> value, params string[] keys)
        { SetupCore.Require(value.Keys.All(k => keys.Contains(k, StringComparer.Ordinal)), "LBINSTALL_WORK_STATE_UNKNOWN"); }
        static bool Integer(object value)
        { return (value is int || value is long) && Convert.ToInt64(value) >= 0; }
        static void Receipt(Dictionary<string, object> doc, bool target)
        {
            Known(doc, "InputId", "FactActivity", "ModelRequested", "RecentConsumed", "RecentInitialized", "Work");
            SetupCore.Require(DataJson.Get(doc, "InputId") is string && Integer(DataJson.Get(doc, "FactActivity"))
                && DataJson.Get(doc, "ModelRequested") is bool, "LBINSTALL_WORK_STATE_UNKNOWN");
            if (doc.ContainsKey("RecentInitialized")) SetupCore.Require(doc["RecentInitialized"] is bool, "LBINSTALL_WORK_STATE_UNKNOWN");
            object recent = DataJson.Get(doc, "RecentConsumed");
            if (recent != null)
            {
                var rows = recent as object[]; SetupCore.Require(rows != null, "LBINSTALL_WORK_STATE_UNKNOWN");
                foreach (object row in rows)
                {
                    var r = row as Dictionary<string, object>; SetupCore.Require(r != null, "LBINSTALL_WORK_STATE_UNKNOWN");
                    Known(r, "UserId", "RequestId", "SessionId", "LastShownEventId", "ShownChunks", "EventIds", "Key");
                    foreach (string k in new[] { "UserId", "RequestId", "SessionId", "LastShownEventId" })
                        SetupCore.Require(DataJson.Get(r, k) is string && ((string)r[k]).Length > 0, "LBINSTALL_WORK_STATE_UNKNOWN");
                    SetupCore.Require(Integer(DataJson.Get(r, "ShownChunks")) && Convert.ToDecimal(r["ShownChunks"]) > 0, "LBINSTALL_WORK_STATE_UNKNOWN");
                    var ids = DataJson.Get(r, "EventIds") as object[];
                    SetupCore.Require(ids != null && ids.Length == Convert.ToDecimal(r["ShownChunks"]) + 1
                        && ids.All(i => i is string && ((string)i).Length > 0) && ids.Distinct().Count() == ids.Length
                        && (string)ids[0] == (string)r["UserId"] && (string)ids[ids.Length - 1] == (string)r["LastShownEventId"], "LBINSTALL_WORK_STATE_UNKNOWN");
                }
            }
            if (target) SetupCore.Require(DataJson.Get(doc, "Work") == null, "LBINSTALL_WORK_STATE_UNKNOWN");
        }
        static void Work(Dictionary<string, object> work)
        {
            Known(work, "Request", "Window", "Target", "Result", "Progress", "RequestedAgain");
            var request = DataJson.Get(work, "Request") as Dictionary<string, object>;
            var window = DataJson.Get(work, "Window") as object[];
            var target = DataJson.Get(work, "Target") as Dictionary<string, object>;
            SetupCore.Require(request != null && DataJson.Get(request, "RequestId") is string
                && ((string)request["RequestId"]).Length > 0 && DataJson.Get(request, "Source") is string
                && window != null && window.Length > 0 && window.All(t => t is Dictionary<string, object>)
                && target != null, "LBINSTALL_WORK_STATE_UNKNOWN");
            Known(request, "RequestId", "Source", "Evidence", "Notes", "Lessons", "Readings", "DayArtifacts", "Settled", "Dependencies", "RevisionTargets", "NoteHistory", "NoteHistoryPermissions");
            foreach (string k in new[] { "Evidence", "Notes", "Lessons", "Readings", "DayArtifacts", "Settled" })
            {
                var rows = DataJson.Get(request, k) as object[];
                SetupCore.Require(rows != null && rows.All(t => t is Dictionary<string, object>), "LBINSTALL_WORK_STATE_UNKNOWN");
            }
            foreach (string k in new[] { "Dependencies", "RevisionTargets", "NoteHistory", "NoteHistoryPermissions" })
                if (request.ContainsKey(k)) SetupCore.Require(request[k] is object[] && ((object[])request[k]).All(t => t is Dictionary<string, object>), "LBINSTALL_WORK_STATE_UNKNOWN");
            Receipt(target, true);
            foreach (string k in new[] { "Result", "Progress" })
                SetupCore.Require(DataJson.Get(work, k) == null || work[k] is Dictionary<string, object>, "LBINSTALL_WORK_STATE_UNKNOWN");
            var result = DataJson.Get(work, "Result") as Dictionary<string, object>;
            if (result != null)
            {
                Known(result, "Note", "Lesson", "Relationship", "Summary", "Thought", "Patches");
                foreach (string k in new[] { "Summary", "Thought" })
                    SetupCore.Require(DataJson.Get(result, k) is string, "LBINSTALL_WORK_STATE_UNKNOWN");
                foreach (string k in new[] { "Note", "Lesson", "Relationship" })
                    SetupCore.Require(DataJson.Get(result, k) == null || result[k] is Dictionary<string, object>, "LBINSTALL_WORK_STATE_UNKNOWN");
                var patches = DataJson.Get(result, "Patches") as object[];
                SetupCore.Require(patches != null && patches.All(p => p is Dictionary<string, object>), "LBINSTALL_WORK_STATE_UNKNOWN");
            }
            var progress = DataJson.Get(work, "Progress") as Dictionary<string, object>;
            if (progress != null)
            {
                Known(progress, "NoteProposed", "NoteReason", "SettleProposed", "SettleReason", "LessonProposed", "LessonReason", "ReadingProposed", "ReadingReason", "NotesShown", "LessonsShown", "ReadingsShown", "DayArtifactsShown", "SettledShown");
                foreach (string k in new[] { "NoteProposed", "SettleProposed", "LessonProposed", "ReadingProposed" })
                    SetupCore.Require(DataJson.Get(progress, k) is bool, "LBINSTALL_WORK_STATE_UNKNOWN");
                foreach (string k in new[] { "NoteReason", "SettleReason", "LessonReason", "ReadingReason" })
                    SetupCore.Require(DataJson.Get(progress, k) is string, "LBINSTALL_WORK_STATE_UNKNOWN");
                foreach (string k in new[] { "NotesShown", "LessonsShown", "ReadingsShown", "DayArtifactsShown", "SettledShown" })
                    SetupCore.Require(Integer(DataJson.Get(progress, k)), "LBINSTALL_WORK_STATE_UNKNOWN");
            }
            if (work.ContainsKey("RequestedAgain")) SetupCore.Require(work["RequestedAgain"] is bool, "LBINSTALL_WORK_STATE_UNKNOWN");
        }
        internal static string Check(string root, string imageHash)
        {
            try
            {
                root = SetupCore.SafePath(root, null); RootReadable(root);
                string path = Path.Combine(root, "v4-reflection-window.json"); var attributes = Attributes(path);
                if (!attributes.HasValue) return "no-receipt";
                SetupCore.Require((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "LBINSTALL_WORK_STATE_UNKNOWN");
                byte[] bytes;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    SetupCore.Require(f.Length > 0 && f.Length <= 4194304, "LBINSTALL_WORK_STATE_UNKNOWN");
                    using (var copy = new MemoryStream()) { f.CopyTo(copy); bytes = copy.ToArray(); }
                }
                var doc = DataJson.Read(new UTF8Encoding(false, true).GetString(bytes)); Receipt(doc, false);
                object value = DataJson.Get(doc, "Work");
                if (value != null)
                {
                    var work = value as Dictionary<string, object>; SetupCore.Require(work != null, "LBINSTALL_WORK_STATE_UNKNOWN");
                    Work(work);
                    SetupCore.Require(SupportsWork(imageHash), "LBINSTALL_UNFINISHED_WORK_TARGET_UNVERIFIED");
                }
                return SetupCore.Hash(bytes);
            }
            catch (SetupException e)
            {
                if (e.Message == "LBINSTALL_UNFINISHED_WORK_TARGET_UNVERIFIED") throw;
                throw new SetupException("LBINSTALL_WORK_STATE_UNKNOWN");
            }
            catch { throw new SetupException("LBINSTALL_WORK_STATE_UNKNOWN"); }
        }
    }
}
