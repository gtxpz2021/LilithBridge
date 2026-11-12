using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LilithBridge;

// The old numeric reader must reject every new-writer journal, including a
// pending-intents-only cursor with no Work/Source converter to guard it.
// The new reader accepts old numeric receipts for a forward upgrade.
internal sealed class ReflectionSerialJsonConverter : JsonConverter<long>
{
    private static readonly System.Threading.AsyncLocal<bool> ExpressionReader = new();
    internal static IDisposable ForSnapshot(ReflectionWindowSnapshot snapshot)
    {
        bool Uses(ReflectionWindowSnapshot s,int depth=0)=>depth>=16
            ||s.PendingIntents?.Any(i=>i.ReasonBasis?.GeneratedSources?.Length>0)==true
            ||s.Work is {} w&&(w.Request.PurposeContexts?.Any(c=>c.Intent.ReasonBasis?.GeneratedSources?.Length>0)==true||Uses(w.Target,depth+1));
        bool previous=ExpressionReader.Value;ExpressionReader.Value=previous||Uses(snapshot);return new ReaderScope(previous);
    }
    private sealed class ReaderScope:IDisposable
    {private readonly bool _previous;internal ReaderScope(bool previous)=>_previous=previous;public void Dispose()=>ExpressionReader.Value=_previous;}
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            long legacy = reader.GetInt64();
            if (legacy < 0) throw new JsonException("Invalid reflection serial");
            return legacy;
        }
        using var document = JsonDocument.ParseValue(ref reader); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("reader", out var requirement) || requirement.ValueKind != JsonValueKind.String
            || requirement.GetString() is not ("reflection-purpose-journal-v1" or "reflection-purpose-expression-journal-v1")
            || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long serial) || serial < 0)
            throw new JsonException("Unsupported reflection journal reader requirement");
        return serial;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        if (value < 0) throw new JsonException("Invalid reflection serial");
        writer.WriteStartObject(); writer.WriteString("reader", ExpressionReader.Value?"reflection-purpose-expression-journal-v1":"reflection-purpose-journal-v1");
        writer.WriteNumber("value", value); writer.WriteEndObject();
    }
}
