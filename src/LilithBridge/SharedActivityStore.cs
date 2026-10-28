using System;
using System.IO;
using System.Text.Json;
namespace LilithBridge;

internal sealed class SharedActivityStore
{
    private readonly string _path;
    internal Action? BeforeReplaceForTests = null;
    public SharedActivityStore(string directory) { _path = Path.Combine(directory, "shared-activity-v1.json"); }
    public SharedActivityDocument Load()
    {
        if (!File.Exists(_path)) return new();
        // A corrupt store fails closed; it is never replaced with an invented empty successful state.
        return JsonSerializer.Deserialize<SharedActivityDocument>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Shared activity store is invalid");
    }
    public bool TrySave(SharedActivityDocument document)
    {
        string pending = _path + ".pending-" + Guid.NewGuid().ToString("N");
        string bytes = JsonSerializer.Serialize(document);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var data = System.Text.Encoding.UTF8.GetBytes(bytes); stream.Write(data); stream.Flush(true); }
            BeforeReplaceForTests?.Invoke();
            if (File.Exists(_path)) File.Replace(pending, _path, null); else File.Move(pending, _path);
            return true;
        }
        catch
        {
            // If replacement had an uncertain outcome, inspect disk; never replay the operation.
            try { if (File.Exists(_path) && File.ReadAllText(_path) == bytes) return true; } catch { }
            return false;
        }
        finally { try { if (File.Exists(pending)) File.Delete(pending); } catch { } }
    }
}
