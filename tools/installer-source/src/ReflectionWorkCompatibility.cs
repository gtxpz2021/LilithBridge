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
        // Unified caf0be7e / 6148f5f9 / idle 1e701134 are NOT added: new reader
        // requirements, pending intents and B1 control generation need a bound
        // recovery matrix. Finish pending Work in the old version, keep backups,
        // or stop for a reviewed migration. Never delete/clear the journal here.
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
        // Completed cursors only. This is deliberately separate from SupportsWork:
        // reading a serial envelope is not proof of unfinished settlement recovery.
        // .89 is included only for the reviewed no-Work cursor reader, not B1,
        // generated-expression persistence, transfer journals or Work replay.
        internal static bool SupportsCompletedReceipt(string imageHash)
        {
            return imageHash == "caf0be7e84531a1922cf5eb5c300f57434fcbd37286cb1540c4001aab2c80325"
                || imageHash == "6148f5f9139d998cd26ddb877ff12c1b760d3395dbe21d04bb150cfaa4855a3a"
                || imageHash == "1e7011343ce0e601224ce3bd794b61197fce03aa5576d20ff88b331cfcd0a249"
                || imageHash == "c73a3973c4df32fff90a47d5e980637c2607c435e6fee348e21e54941428643b"
                // UI-only history change; persisted readers and Work sources unchanged.
                || imageHash == "f7c67ee6f305eda6758f546b6eac34d7a4a211095ead0d481fbd2342e32d4a30"
                // Supply projection only: persisted readers/Work unchanged, bound source and controlled-data proof.
                || imageHash == "4b53c5366d77ed61b827d69380d04cdb729a1d93cdf98848d07e2cf61ca60e80"
                // Save-status presentation cache only; persistence unchanged.
                || imageHash == "89c86d1fb43132d0ed46037ae6d15b0906979d7b6a30353c3d1257203e997c9e"
                // Selected MAIN generated presentation only; persisted readers unchanged.
                || imageHash == "9ce6835396b4fd97925cdbd0685399f4bcb6336977ca762137c2c4c80cbd697e"
                // .103 and model-options variant: same persisted readers, exact bound images.
                || imageHash == "83168a8fd166b87c56d0ebc3fa9a5cae0f045b2f07b5622e38cb180a3bc54dda"
                || imageHash == "afd03ea7b66f8ad3b5d2513467803e326c40253f3d742d7f681b2a6e74fd4ca7"
                || imageHash == "e4c865ca691559e8b9fe784c88cc7c491f825e32f8cf051b0496f027319152ff" || imageHash == "bfb40b65f2bfeb2963cb753967e6d6d8cb5fac864896103927c1994c321858e6" || imageHash == "ea9fe04b2fa22fbc903538d46790a624cfe634153874daa79495c28f7fe1e802" || imageHash == "61122db554cdc2f71ae52dabae49d4c9fb8bb09c5266e57583353681572bfaf5";
        }
        // Exact private image whose converter supports explicit and MAIN revisit receipts.
        // Header preflight only; no migration, Work replay or data downgrade.
        static bool SupportsRevisitReceipt(string imageHash)
        { return imageHash == "ea9fe04b2fa22fbc903538d46790a624cfe634153874daa79495c28f7fe1e802" || imageHash == "61122db554cdc2f71ae52dabae49d4c9fb8bb09c5266e57583353681572bfaf5"; }
        static bool SupportsFinalReceipt(string imageHash)
        { return imageHash == "61122db554cdc2f71ae52dabae49d4c9fb8bb09c5266e57583353681572bfaf5"; }
        static void RequiredText(Dictionary<string, object> value, string key)
        { SetupCore.Require(DataJson.Get(value, key) is string && !String.IsNullOrWhiteSpace((string)value[key]) && ((string)value[key]).Length <= 256, "LBINSTALL_WORK_STATE_UNKNOWN"); }
        static DateTimeOffset RequiredTime(Dictionary<string, object> value, string key)
        {
            DateTimeOffset result;
            SetupCore.Require(DataJson.Get(value, key) is string && DateTimeOffset.TryParse((string)value[key], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result), "LBINSTALL_WORK_STATE_UNKNOWN");
            return DateTimeOffset.Parse((string)value[key], System.Globalization.CultureInfo.InvariantCulture);
        }
        static void FinalMetadata(Dictionary<string, object> doc, string imageHash)
        {
            foreach (string key in new[] { "LastDialogueService", "LastCurrentDialogueService", "OrdinaryDeferral" })
            {
                object item = DataJson.Get(doc, key); if (item == null) continue;
                SetupCore.Require(SupportsFinalReceipt(imageHash), "LBINSTALL_RECEIPT_TARGET_UNVERIFIED");
                var value = item as Dictionary<string, object>;
                SetupCore.Require(value != null, "LBINSTALL_WORK_STATE_UNKNOWN");
                RequiredText(value, "RequestId");
                if (key == "OrdinaryDeferral")
                {
                    Known(value, "RequestId", "SessionId", "ObservedAtUtc", "UntilUtc");
                    RequiredText(value, "SessionId");
                    SetupCore.Require(RequiredTime(value, "UntilUtc") - RequiredTime(value, "ObservedAtUtc") == TimeSpan.FromSeconds(300), "LBINSTALL_WORK_STATE_UNKNOWN");
                }
                else
                {
                    Known(value, "RequestId", "CompletedInSessionId", "CurrentSessionCoverage", "CoverageCompletionObservedAtUtc");
                    RequiredText(value, "CompletedInSessionId");
                    SetupCore.Require(DataJson.Get(value, "CurrentSessionCoverage") is bool, "LBINSTALL_WORK_STATE_UNKNOWN");
                    RequiredTime(value, "CoverageCompletionObservedAtUtc");
                }
            }
        }
        static void Serial(object value, string imageHash)
        {
            if (Integer(value)) return;
            var serial = value as Dictionary<string, object>;
            SetupCore.Require(serial != null, "LBINSTALL_WORK_STATE_UNKNOWN");
            Known(serial, "reader", "value");
            SetupCore.Require(serial.Count == 2 && Integer(DataJson.Get(serial, "value"))
                && DataJson.Get(serial, "reader") is string, "LBINSTALL_WORK_STATE_UNKNOWN");
            string reader = (string)serial["reader"];
            SetupCore.Require(reader == "reflection-purpose-journal-v1"
                || reader == "reflection-purpose-expression-journal-v1"
                || SupportsRevisitReceipt(imageHash) && (reader == "reflection-explicit-revisit-journal-v1"
                    || reader == "reflection-main-revisit-origin-v1"), "LBINSTALL_RECEIPT_READER_UNSUPPORTED");
        }
        static void Origin(object value)
        {
            if (value == null) return;
            var origin = value as Dictionary<string, object>;
            SetupCore.Require(origin != null, "LBINSTALL_WORK_STATE_UNKNOWN");
            Known(origin, "SessionId", "UserEventId", "RequestId");
            foreach (string key in new[] { "SessionId", "UserEventId", "RequestId" })
            {
                string text = DataJson.Get(origin, key) as string;
                SetupCore.Require(!String.IsNullOrWhiteSpace(text) && text.Length <= 256, "LBINSTALL_WORK_STATE_UNKNOWN");
            }
        }
        static void CompletedFields(Dictionary<string, object> doc, string imageHash)
        {
            string[] fields = { "ModelRequestSerial", "RequestOrigin", "PendingIntents", "ImportedTransfers", "RecentPassesSinceBacklog", "LastRevisitCompletion", "LastDialogueService", "LastCurrentDialogueService", "OrdinaryDeferral" };
            if (!fields.Any(doc.ContainsKey)) return;
            if (doc.ContainsKey("ModelRequestSerial")) Serial(doc["ModelRequestSerial"], imageHash);
            object revisit = DataJson.Get(doc, "LastRevisitCompletion");
            if (revisit != null)
            {
                // Opaque completed metadata is preserved for the bound product's full reader.
                // Installer only checks the capability/header; it never rewrites the journal.
                SetupCore.Require(revisit is Dictionary<string, object>, "LBINSTALL_WORK_STATE_UNKNOWN");
                SetupCore.Require(SupportsRevisitReceipt(imageHash), "LBINSTALL_RECEIPT_TARGET_UNVERIFIED");
            }
            FinalMetadata(doc, imageHash);
            Origin(DataJson.Get(doc, "RequestOrigin"));
            if (doc.ContainsKey("RecentPassesSinceBacklog"))
                SetupCore.Require(Integer(doc["RecentPassesSinceBacklog"])
                    && Convert.ToInt64(doc["RecentPassesSinceBacklog"]) <= Int32.MaxValue, "LBINSTALL_WORK_STATE_UNKNOWN");
            object pending = DataJson.Get(doc, "PendingIntents"), imported = DataJson.Get(doc, "ImportedTransfers");
            var intents = pending as object[]; var transfers = imported as object[];
            SetupCore.Require(pending == null || intents != null && intents.All(x => x is Dictionary<string, object>), "LBINSTALL_WORK_STATE_UNKNOWN");
            SetupCore.Require(imported == null || transfers != null && transfers.All(x => x is string && !String.IsNullOrWhiteSpace((string)x)
                && ((string)x).Length <= 256) && transfers.Distinct().Count() == transfers.Length, "LBINSTALL_WORK_STATE_UNKNOWN");
            SetupCore.Require(SupportsCompletedReceipt(imageHash), "LBINSTALL_RECEIPT_TARGET_UNVERIFIED");
            // These are durable requests/transfer state, not decorative optional
            // fields. Never drop them or infer that Work==null means quiescent.
            SetupCore.Require(intents == null || intents.Length == 0, "LBINSTALL_PENDING_INTENTS_REQUIRE_REVIEW");
            SetupCore.Require(transfers == null || transfers.Length == 0, "LBINSTALL_IMPORTED_TRANSFERS_REQUIRE_REVIEW");
        }
        static void Receipt(Dictionary<string, object> doc, bool target, string imageHash)
        {
            Known(doc, "InputId", "FactActivity", "ModelRequested", "RecentConsumed", "RecentInitialized", "Work",
                "ModelRequestSerial", "RequestOrigin", "PendingIntents", "ImportedTransfers", "RecentPassesSinceBacklog", "LastRevisitCompletion", "LastDialogueService", "LastCurrentDialogueService", "OrdinaryDeferral");
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
            CompletedFields(doc, imageHash);
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
            Receipt(target, true, null);
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
                var doc = DataJson.Read(new UTF8Encoding(false, true).GetString(bytes)); Receipt(doc, false, imageHash);
                object value = DataJson.Get(doc, "Work");
                if (value != null)
                {
                    var work = value as Dictionary<string, object>; SetupCore.Require(work != null, "LBINSTALL_WORK_STATE_UNKNOWN");
                    SetupCore.Require(SupportsWork(imageHash), "LBINSTALL_UNFINISHED_WORK_TARGET_UNVERIFIED");
                    Work(work);
                }
                return SetupCore.Hash(bytes);
            }
            catch (SetupException e)
            {
                if (new[] { "LBINSTALL_UNFINISHED_WORK_TARGET_UNVERIFIED", "LBINSTALL_RECEIPT_TARGET_UNVERIFIED",
                    "LBINSTALL_RECEIPT_READER_UNSUPPORTED", "LBINSTALL_PENDING_INTENTS_REQUIRE_REVIEW",
                    "LBINSTALL_IMPORTED_TRANSFERS_REQUIRE_REVIEW" }.Contains(e.Message)) throw;
                throw new SetupException("LBINSTALL_WORK_STATE_UNKNOWN");
            }
            catch { throw new SetupException("LBINSTALL_WORK_STATE_UNKNOWN"); }
        }
    }
}
