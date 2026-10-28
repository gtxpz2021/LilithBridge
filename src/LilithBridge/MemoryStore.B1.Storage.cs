using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed partial class B1Document
{
    [System.Text.Json.Serialization.JsonIgnore] internal string StorageIdentity { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] internal long StorageRevision { get; set; } = -1;
    [System.Text.Json.Serialization.JsonIgnore] internal string StorageLegacyHash { get; set; } = "";
    public Dictionary<string, string> Management { get; set; } = new(StringComparer.Ordinal);
    public List<string> DeletedVersions { get; set; } = new();
    public List<B1ManagementReceipt> ManagementReceipts { get; set; } = new();
}

internal sealed partial class MemoryStore
{
    internal const string B1DatabaseFile = "v4-b1-continuity-v2.sqlite3";
    private const string B1DatabaseApplication = "1279406386"; // 0x4c423132, LB12
    private string B1DatabasePath => Path.Combine(_root, B1DatabaseFile);
    private string _b1DatabaseStamp = "";
    private string _b1LegacyStamp = "";
    private string _b1LegacyHash = "absent";
    private B1Document? _b1LegacyDocument;
    private long _b1DatabaseRevision;
    internal long B1StorageTransactions { get; private set; }
    internal long B1StorageRowWrites { get; private set; }
    // Bound values executed for row writes, not physical device I/O.
    internal long B1StoragePayloadBytes { get; private set; }
    internal long B1StorageDatabaseLoads { get; private set; }
    internal long B1StorageLegacyLoads { get; private set; }
    internal string B1StorageEngineVersion => B1Sqlite.Version;
    internal B1Document? ReadB1Snapshot() => ReadB1Storage(true);

    private static void CopyB1State(B1Document from, B1Document to)
    {
        to.StorageIdentity = from.StorageIdentity;
        to.StorageRevision = from.StorageRevision;
        to.StorageLegacyHash = from.StorageLegacyHash;
        to.Management = new(from.Management, StringComparer.Ordinal);
        to.DeletedVersions = from.DeletedVersions.ToList();
        to.ManagementReceipts = from.ManagementReceipts.ToList();
    }
    private void MarkB1StorageSnapshot(B1Document doc)
    {
        doc.StorageIdentity = Path.GetFullPath(B1DatabasePath);
        doc.StorageRevision = _b1DatabaseRevision;
        doc.StorageLegacyHash = _b1LegacyHash;
    }
    private static string FileStamp(string path, bool header = false)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "absent";
        string value = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        if (!header) return value;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[100]; int n = stream.Read(bytes, 0, bytes.Length);
        return value + ":" + Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, n)));
    }
    private static void ValidateB1Document(B1Document doc)
    {
        if (doc.SchemaVersion != 1) throw new InvalidDataException("unknown_schema");
        if (doc.Entries == null || doc.Receipts == null || doc.Current == null || doc.Deliveries == null
            || doc.Management == null || doc.DeletedVersions == null || doc.ManagementReceipts == null
            || doc.Entries.Any(e => e == null || e.Version.Length == 0 || e.LogicalId.Length == 0
            || e.Sources == null || e.ObjectAnchors == null || e.Packet == null || e.Packet.Materials == null || e.Packet.Footprint == null
                || e.Packet.SemanticAnchors == null || e.Packet.ShortRefs == null || e.Packet.Omissions == null
                || e.Text == null || e.Scope == null || e.Target == null || e.Kind == null || e.Nature == null || e.Conditions == null || e.AnchorStatus == null
                || e.Packet.Availability == null || e.Packet.Diagnostic == null || e.Packet.FocusStatus == null || e.Packet.SearchMode == null
                || e.GeneratedBy != "main_reply" || e.Selection != "explicit_retain"
                || e.Sources.Any(r => r == null || r.Kind == null || r.Version == null || r.Sha256 == null || r.Purpose == null)
                || e.ObjectAnchors.Concat(e.Packet.SemanticAnchors).Concat(e.Packet.ShortRefs.Values)
                    .Any(r => r == null || r.Kind == null || r.Version == null || r.Sha256 == null || r.Purpose == null)
                || e.Packet.Omissions.Any(o => o == null || o.Ref == null || o.Reason == null)
                || e.Packet.Footprint.Any(r => r == null || r.Kind == null || r.Version == null || r.Sha256 == null || r.Purpose == null)
                || e.Packet.Materials.Any(m => m == null || m.Ref == null || m.Text == null || m.Scope == null
                    || m.VersionFingerprint == null || m.Ref.Kind == null || m.Ref.Version == null
                    || m.Ref.Sha256 == null || m.Ref.Purpose == null
                    || m.DiscoverySource is { } discovery && (discovery.Kind == null || discovery.Version == null
                        || discovery.Sha256 == null || discovery.Purpose == null)))
            || doc.Entries.Select(e => e.Version).Distinct().Count() != doc.Entries.Count
            || doc.Receipts.Any(r => r == null || r.StepId.Length == 0)
            || doc.Receipts.Select(r => r.StepId).Distinct().Count() != doc.Receipts.Count
            || doc.Deliveries.Any(d => d == null || string.IsNullOrEmpty(d.StepId) || string.IsNullOrEmpty(d.EventId)
                || d.RequestId == null || d.Text == null || d.Outcome == null)
            || doc.Deliveries.Select(DeliveryKey).Distinct().Count() != doc.Deliveries.Count
            || doc.Management.Any(kv => kv.Key.Length == 0 || kv.Value is not ("suppressed" or "withdrawn" or "deleting" or "deleted"))
            || doc.DeletedVersions.Any(v => string.IsNullOrEmpty(v))
            || doc.DeletedVersions.Distinct().Count() != doc.DeletedVersions.Count
            || doc.ManagementReceipts.Any(r => r == null || string.IsNullOrEmpty(r.OperationId)
                || r.Kind is not ("suppress" or "withdraw" or "delete")
                || r.TargetVersions == null || r.TargetVersions.Count == 0
                || r.TargetVersions.Any(v => string.IsNullOrEmpty(v))
                || r.TargetVersions.Distinct().Count() != r.TargetVersions.Count
                || r.Outcome is not ("applied" or "deleting" or "purge_incomplete" or "deleted"))
            || doc.ManagementReceipts.Select(r => r.OperationId).Distinct().Count() != doc.ManagementReceipts.Count)
            throw new InvalidDataException("malformed_document");
        var versions = doc.Entries.ToDictionary(e => e.Version, StringComparer.Ordinal);
        if (doc.DeletedVersions.Any(versions.ContainsKey)) throw new InvalidDataException("deleted_body_present");
        if (doc.Current.Any(kv => !versions.TryGetValue(kv.Value, out var entry) || entry.LogicalId != kv.Key))
            throw new InvalidDataException("malformed_current_pointer");
    }
    private B1Document ReadLegacyB1()
    {
        string stamp = FileStamp(B1Path);
        if (_b1LegacyDocument != null && stamp == _b1LegacyStamp) return _b1LegacyDocument;
        B1Document doc;
        if (stamp == "absent") { doc = new(); _b1LegacyHash = "absent"; }
        else
        {
            byte[] bytes = File.ReadAllBytes(B1Path);
            using var source = JsonDocument.Parse(bytes);
            if (source.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("malformed_document");
            if (!source.RootElement.TryGetProperty("SchemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1)
                throw new InvalidDataException("unknown_schema");
            // The original format predates request protocol v2. Adapt only the
            // missing protocol marker in memory; do not invent old object anchors
            // or alter the original source bytes to look like a new request.
            var adapted = JsonNode.Parse(bytes) ?? throw new InvalidDataException("null_document");
            if (adapted["Entries"] is JsonArray entries)
                foreach (JsonNode? node in entries)
                    if (node is JsonObject entry && entry["Packet"] is JsonObject packet && !packet.ContainsKey("ProtocolVersion"))
                        packet["ProtocolVersion"] = 1;
            doc = adapted.Deserialize<B1Document>() ?? throw new InvalidDataException("null_document");
            _b1LegacyHash = Convert.ToHexString(SHA256.HashData(bytes));
            B1StorageLegacyLoads++;
        }
        ValidateB1Document(doc);
        _b1LegacyDocument = doc; _b1LegacyStamp = stamp;
        return doc;
    }
    private static void VerifyB1Database(B1Sqlite db)
    {
        if (db.Scalar("PRAGMA application_id") != B1DatabaseApplication || db.Scalar("PRAGMA user_version") != "2")
            throw new InvalidDataException("unknown_database_schema");
        if (db.Scalar("PRAGMA journal_mode") != "delete") throw new InvalidDataException("unsupported_database_journal");
        if (db.Scalar("PRAGMA quick_check") != "ok") throw new InvalidDataException("corrupt_database");
    }
    private static void VerifyB1DatabaseHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] header = new byte[100]; int count = stream.Read(header, 0, header.Length);
        if (count != 100 || !header.AsSpan(0, 16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")))
            throw new InvalidDataException("corrupt_database_header");
        if (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4)) != 2
            || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(68, 4)).ToString() != B1DatabaseApplication)
            throw new InvalidDataException("unknown_database_schema");
        if (header[18] != 1 || header[19] != 1) throw new InvalidDataException("unsupported_database_journal");
    }
    private static string B1ReadFailure(Exception error)
    {
        if (error is InvalidDataException) return error.Message;
        if (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) return "engine_unavailable";
        if (error is IOException && error.Message.StartsWith("sqlite_", StringComparison.Ordinal))
        {
            if (error.Message.StartsWith("sqlite_5:", StringComparison.Ordinal) || error.Message.StartsWith("sqlite_6:", StringComparison.Ordinal)) return "database_locked";
            if (error.Message.StartsWith("sqlite_11:", StringComparison.Ordinal) || error.Message.StartsWith("sqlite_26:", StringComparison.Ordinal)) return "corrupt_database";
            return error.Message.Split(':')[0];
        }
        return error.GetType().Name;
    }
    private static string DeliveryKey(B1Delivery delivery) => delivery.StepId + "/" + delivery.EventId;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    private static T Decode<T>(string text) => JsonSerializer.Deserialize<T>(text) ?? throw new InvalidDataException("malformed_database_row");
    private static void CreateB1DatabaseSchema(B1Sqlite db)
    {
        db.Execute("PRAGMA journal_mode=DELETE");
        db.Execute("PRAGMA application_id=" + B1DatabaseApplication);
        db.Execute("PRAGMA user_version=2");
        foreach (string name in new[] { "meta", "entries", "heads", "receipts", "deliveries", "management", "deleted", "management_receipts" })
            db.Execute("CREATE TABLE " + name + "(id TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)");
    }
    private void InitializeB1Database(B1Document legacy)
    {
        Directory.CreateDirectory(_root);
        string temp = B1DatabasePath + ".migrate-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var db = new B1Sqlite(temp, create: true))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    CreateB1DatabaseSchema(db);
                    db.Execute("INSERT INTO meta VALUES('legacy_hash',?)", _b1LegacyHash);
                    db.Execute("INSERT INTO meta VALUES('revision','0')");
                    InsertAllB1Rows(db, legacy);
                    db.Execute("COMMIT");
                }
                catch { try { db.Execute("ROLLBACK"); } catch (IOException) { } throw; }
            }
            // A committed, closed database is published atomically. Interrupted
            // migration never exposes a partial database as the active store.
            try { File.Move(temp, B1DatabasePath); }
            catch (IOException) when (File.Exists(B1DatabasePath)) { /* another migrator won; verify its state */ }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(temp + "-journal")) File.Delete(temp + "-journal");
        }
    }
    private static void InsertAllB1Rows(B1Sqlite db, B1Document doc)
    {
        foreach (var e in doc.Entries) db.Execute("INSERT INTO entries VALUES(?,?)", e.Version, Serialize(e));
        foreach (var kv in doc.Current) db.Execute("INSERT INTO heads VALUES(?,?)", kv.Key, kv.Value);
        foreach (var r in doc.Receipts) db.Execute("INSERT INTO receipts VALUES(?,?)", r.StepId, Serialize(r));
        foreach (var d in doc.Deliveries) db.Execute("INSERT INTO deliveries VALUES(?,?)", DeliveryKey(d), Serialize(d));
        foreach (var kv in doc.Management) db.Execute("INSERT INTO management VALUES(?,?)", kv.Key, kv.Value);
        foreach (var id in doc.DeletedVersions.Distinct()) db.Execute("INSERT INTO deleted VALUES(?,?)", id, "");
        foreach (var r in doc.ManagementReceipts) db.Execute("INSERT INTO management_receipts VALUES(?,?)", r.OperationId, Serialize(r));
    }
    private sealed record LegacyPurge(string From, string To, List<string> Versions);
    private void CheckB1LegacyIdentity(B1Sqlite db)
    {
        string expected = db.Scalar("SELECT value FROM meta WHERE id='legacy_hash'") ?? throw new InvalidDataException("malformed_database_metadata");
        if (expected == _b1LegacyHash) return;
        string? pendingText = db.Scalar("SELECT value FROM meta WHERE id='legacy_purge_pending'");
        if (pendingText != null)
        {
            var pending = Decode<LegacyPurge>(pendingText);
            if (_b1LegacyHash == pending.From || _b1LegacyHash == pending.To) return;
        }
        throw new InvalidDataException("legacy_changed_after_migration");
    }
    private B1Document? ReadB1Storage(bool force = false)
    {
        try
        {
            var legacy = ReadLegacyB1();
            if (!File.Exists(B1DatabasePath)) InitializeB1Database(legacy);
            string stamp = FileStamp(B1DatabasePath, true) + "|" + _b1LegacyHash;
            // 'force' requests a fresh filesystem identity check, not a needless
            // full JSON reload when that identity is unchanged.
            if (_b1 != null && _b1DatabaseStamp == stamp) { B1StorageStatus = "ready"; return _b1; }
            VerifyB1DatabaseHeader(B1DatabasePath);
            using var db = new B1Sqlite(B1DatabasePath);
            db.Execute("BEGIN");
            B1Document doc;
            try
            {
                VerifyB1Database(db); CheckB1LegacyIdentity(db);
                doc = new B1Document
                {
                    Entries = db.Rows("SELECT value FROM entries ORDER BY rowid").Select(r => Decode<B1Entry>(r[0])).ToList(),
                    Current = db.Rows("SELECT id,value FROM heads").ToDictionary(r => r[0], r => r[1], StringComparer.Ordinal),
                    Receipts = db.Rows("SELECT value FROM receipts ORDER BY rowid").Select(r => Decode<B1Receipt>(r[0])).ToList(),
                    Deliveries = db.Rows("SELECT value FROM deliveries ORDER BY rowid").Select(r => Decode<B1Delivery>(r[0])).ToList(),
                    Management = db.Rows("SELECT id,value FROM management").ToDictionary(r => r[0], r => r[1], StringComparer.Ordinal),
                    DeletedVersions = db.Rows("SELECT id FROM deleted").Select(r => r[0]).ToList(),
                    ManagementReceipts = db.Rows("SELECT value FROM management_receipts ORDER BY rowid").Select(r => Decode<B1ManagementReceipt>(r[0])).ToList()
                };
                ValidateB1Document(doc);
                _b1DatabaseRevision = long.Parse(db.Scalar("SELECT value FROM meta WHERE id='revision'") ?? throw new InvalidDataException("malformed_database_metadata"));
                db.Execute("COMMIT");
            }
            catch { try { db.Execute("ROLLBACK"); } catch (IOException) { } throw; }
            // Cache only the identity sampled before the read transaction; a
            // concurrent commit must not label an older snapshot as its own.
            MarkB1StorageSnapshot(doc);
            _b1 = doc; _b1DatabaseStamp = stamp;
            B1StorageDatabaseLoads++; B1StorageStatus = "ready";
            return doc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NullReferenceException or FormatException or OverflowException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _b1 = null; _b1DatabaseStamp = "";
            B1StorageStatus = B1ReadFailure(error);
            return null;
        }
    }
    private void B1Row(B1Sqlite db, string sql, params string[] values)
    {
        db.Execute(sql, values);
        B1StorageRowWrites++;
        B1StoragePayloadBytes += values.Sum(v => (long)Encoding.UTF8.GetByteCount(v));
    }
    private void DiffB1Rows<T>(B1Sqlite db, string table, IEnumerable<T> oldRows, IEnumerable<T> newRows,
        Func<T, string> key, Func<T, string> encode)
    {
        var old = oldRows.ToDictionary(key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in newRows)
        {
            string id = key(row); seen.Add(id);
            if (old.TryGetValue(id, out var previous))
            {
                if (ReferenceEquals(previous, row) || Equals(previous, row)) continue;
                string text = encode(row);
                if (encode(previous) == text) continue;
                B1Row(db, "UPDATE " + table + " SET value=? WHERE id=?", text, id);
            }
            else B1Row(db, "INSERT INTO " + table + " VALUES(?,?)", id, encode(row));
        }
        foreach (string id in old.Keys.Where(id => !seen.Contains(id))) B1Row(db, "DELETE FROM " + table + " WHERE id=?", id);
    }
    private bool WriteB1Storage(B1Document next)
    {
        bool committed = false;
        long expectedBaseRevision = next.StorageRevision;
        string expectedLegacy = next.StorageLegacyHash;
        try
        {
            if (expectedBaseRevision < 0 || !string.Equals(next.StorageIdentity, Path.GetFullPath(B1DatabasePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("database_snapshot_required");
            ValidateB1Document(next);
            var previous = ReadB1Storage(true);
            if (previous == null) return false;
            if (_b1DatabaseRevision != expectedBaseRevision || _b1LegacyHash != expectedLegacy)
                throw new InvalidDataException("database_version_conflict");
            using var db = new B1Sqlite(B1DatabasePath);
            // Verify schema again even on the cached fast path. No unavailable
            // storage result is reinterpreted as permission to overwrite it.
            if (db.Scalar("PRAGMA application_id") != B1DatabaseApplication || db.Scalar("PRAGMA user_version") != "2")
                throw new InvalidDataException("unknown_database_schema");
            string? dataVersion = db.Scalar("PRAGMA data_version");
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                if (db.Scalar("SELECT value FROM meta WHERE id='revision'") != expectedBaseRevision.ToString())
                    throw new InvalidDataException("database_version_conflict");
                CheckB1LegacyIdentity(db);
                long writesBefore = B1StorageRowWrites;
                DiffB1Rows(db, "entries", previous.Entries, next.Entries, e => e.Version, Serialize);
                bool entriesChanged = B1StorageRowWrites != writesBefore;
                DiffB1Rows(db, "heads", previous.Current, next.Current, kv => kv.Key, kv => kv.Value);
                DiffB1Rows(db, "receipts", previous.Receipts, next.Receipts, r => r.StepId, Serialize);
                DiffB1Rows(db, "deliveries", previous.Deliveries, next.Deliveries, DeliveryKey, Serialize);
                DiffB1Rows(db, "management", previous.Management, next.Management, kv => kv.Key, kv => kv.Value);
                DiffB1Rows(db, "deleted", previous.DeletedVersions.Distinct(), next.DeletedVersions.Distinct(), s => s, _ => "");
                DiffB1Rows(db, "management_receipts", previous.ManagementReceipts, next.ManagementReceipts, r => r.OperationId, Serialize);
                if (B1StorageRowWrites == writesBefore)
                {
                    db.Execute("COMMIT"); committed = true; MarkB1StorageSnapshot(next); _b1 = next;
                    _b1DatabaseStamp = FileStamp(B1DatabasePath, true) + "|" + _b1LegacyHash;
                    if (db.Scalar("PRAGMA data_version") != dataVersion) _b1DatabaseStamp = "";
                    B1StorageStatus = "ready";
                    return true;
                }
                B1Row(db, "UPDATE meta SET value=? WHERE id='revision'", (_b1DatabaseRevision + 1).ToString());
                B1SaveFault?.Invoke("before_replace");
                db.Execute("COMMIT"); committed = true; B1StorageTransactions++;
                B1SaveFault?.Invoke("after_replace");
                _b1DatabaseRevision++; MarkB1StorageSnapshot(next); _b1 = next;
                _b1DatabaseStamp = FileStamp(B1DatabasePath, true) + "|" + _b1LegacyHash;
                if (db.Scalar("PRAGMA data_version") != dataVersion) _b1DatabaseStamp = "";
                if (entriesChanged) { _b1IndexedEntries = null; _b1Index.Clear(); _b1SearchIndex.Clear(); }
                _b1ResolutionMemo.Clear();
                B1StorageStatus = "ready";
                return true;
            }
            finally { if (!committed) try { db.Execute("ROLLBACK"); } catch (IOException) { } }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NullReferenceException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _b1 = null; _b1DatabaseStamp = "";
            B1StorageStatus = error is InvalidDataException ? error.Message : committed ? "save_result_uncertain" : "save_failed";
            return false;
        }
    }

    // Called only after the management transaction persisted a deleting state.
    // It scrubs B-only compatibility copies; user exchange archives are untouched.
    private bool PurgeLegacyB1Versions(HashSet<string> ids)
    {
        string? temp = null;
        try
        {
            var legacy = ReadLegacyB1();
            if (!File.Exists(B1DatabasePath)) throw new InvalidDataException("database_missing");
            using var db = new B1Sqlite(B1DatabasePath);
            VerifyB1Database(db);
            string expected = db.Scalar("SELECT value FROM meta WHERE id='legacy_hash'") ?? throw new InvalidDataException("malformed_database_metadata");
            string? pendingText = db.Scalar("SELECT value FROM meta WHERE id='legacy_purge_pending'");
            LegacyPurge? pending = pendingText == null ? null : Decode<LegacyPurge>(pendingText);
            if (pending != null && !pending.Versions.All(ids.Contains)) throw new InvalidDataException("different_legacy_purge_pending");
            if (pending != null && _b1LegacyHash == pending.To)
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    db.Execute("UPDATE meta SET value=? WHERE id='legacy_hash'", pending.To);
                    db.Execute("DELETE FROM meta WHERE id='legacy_purge_pending'");
                    db.Execute("COMMIT");
                }
                catch { try { db.Execute("ROLLBACK"); } catch (IOException) { } throw; }
                _b1DatabaseStamp = "";
                return true;
            }
            if (_b1LegacyHash != expected && (pending == null || _b1LegacyHash != pending.From))
                throw new InvalidDataException("legacy_changed_after_migration");
            if (!File.Exists(B1Path)) return true;
            var removed = new HashSet<string>(ids, StringComparer.Ordinal);
            bool added;
            do
            {
                added = false;
                foreach (var entry in legacy.Entries.Where(e => e.Sources.Concat(e.ObjectAnchors).Concat(e.Packet.SemanticAnchors).Concat(e.Packet.Footprint)
                    .Concat(e.Packet.Materials.Select(m => m.Ref)).Any(r => removed.Contains(r.Version))))
                    added |= removed.Add(entry.Version);
            } while (added);
            var steps = legacy.Entries.Where(e => removed.Contains(e.Version)).Select(e => e.StepId).ToHashSet(StringComparer.Ordinal);
            if (steps.Count == 0 && pending == null) return true;
            var next = new B1Document
            {
                Entries = legacy.Entries.Where(e => !removed.Contains(e.Version)).ToList(),
                Current = legacy.Current.Where(kv => !removed.Contains(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
                Receipts = legacy.Receipts.Select(r => steps.Contains(r.StepId) ? r with { Outcome = "source_deleted" } : r).ToList(),
                Deliveries = legacy.Deliveries.Where(d => !steps.Contains(d.StepId) && !removed.Contains(d.EventId)).ToList()
            };
            CopyB1State(legacy, next);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            string targetHash = Convert.ToHexString(SHA256.HashData(bytes));
            var intent = new LegacyPurge(_b1LegacyHash, targetHash, removed.ToList());
            if (pending != null && pending.To != targetHash) throw new InvalidDataException("legacy_purge_version_conflict");
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                db.Execute("INSERT OR REPLACE INTO meta VALUES('legacy_purge_pending',?)", Serialize(intent));
                db.Execute("COMMIT");
            }
            catch { try { db.Execute("ROLLBACK"); } catch (IOException) { } throw; }
            temp = B1Path + ".delete-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, B1Path, true); temp = null;
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                db.Execute("UPDATE meta SET value=? WHERE id='legacy_hash'", targetHash);
                db.Execute("DELETE FROM meta WHERE id='legacy_purge_pending'");
                db.Execute("COMMIT");
            }
            catch { try { db.Execute("ROLLBACK"); } catch (IOException) { } throw; }
            _b1LegacyDocument = next; _b1LegacyHash = targetHash; _b1LegacyStamp = FileStamp(B1Path);
            _b1DatabaseStamp = "";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NullReferenceException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _b1 = null; _b1DatabaseStamp = ""; B1StorageStatus = "legacy_purge_incomplete";
            return false;
        }
        finally { if (temp != null && File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { } }
    }
}
