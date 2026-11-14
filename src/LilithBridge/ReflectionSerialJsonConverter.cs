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
    private static readonly System.Threading.AsyncLocal<bool> RevisitReader = new();
    private static readonly System.Threading.AsyncLocal<bool> MainRevisitReader = new();
    internal static IDisposable ForSnapshot(ReflectionWindowSnapshot snapshot)
    {
        bool Uses(ReflectionWindowSnapshot s,int depth=0)=>depth>=16
            ||s.PendingIntents?.Any(i=>i.ReasonBasis?.GeneratedSources?.Length>0)==true
            ||s.Work is {} w&&(w.Request.PurposeContexts?.Any(c=>c.Intent.ReasonBasis?.GeneratedSources?.Length>0)==true||Uses(w.Target,depth+1));
        bool UsesRevisit(ReflectionWindowSnapshot s,int depth=0)=>depth>=16 || s.LastRevisitCompletion!=null
            ||s.Work is {} w&&(w.Purpose==ReflectionWorkPurpose.ExplicitRevisit||UsesRevisit(w.Target,depth+1));
        bool UsesMainRevisit(ReflectionWindowSnapshot s,int depth=0)=>depth>=16 || s.LastRevisitCompletion?.Invocation.Main!=null
            ||s.Work is {} w&&(w.Request.RevisitContext?.Main!=null||UsesMainRevisit(w.Target,depth+1));
        bool previous=ExpressionReader.Value,previousRevisit=RevisitReader.Value,previousMain=MainRevisitReader.Value;
        ExpressionReader.Value=previous||Uses(snapshot);RevisitReader.Value=previousRevisit||UsesRevisit(snapshot);
        MainRevisitReader.Value=previousMain||UsesMainRevisit(snapshot);
        return new ReaderScope(previous,previousRevisit,previousMain);
    }
    private sealed class ReaderScope:IDisposable
    {private readonly bool _previous,_previousRevisit,_previousMain;
        internal ReaderScope(bool previous,bool revisit,bool main){_previous=previous;_previousRevisit=revisit;_previousMain=main;}
        public void Dispose(){ExpressionReader.Value=_previous;RevisitReader.Value=_previousRevisit;MainRevisitReader.Value=_previousMain;}}
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
            || requirement.GetString() is not ("reflection-purpose-journal-v1" or "reflection-purpose-expression-journal-v1" or "reflection-explicit-revisit-journal-v1" or "reflection-main-revisit-origin-v1")
            || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long serial) || serial < 0)
            throw new JsonException("Unsupported reflection journal reader requirement");
        return serial;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        if (value < 0) throw new JsonException("Invalid reflection serial");
        writer.WriteStartObject(); writer.WriteString("reader", MainRevisitReader.Value?"reflection-main-revisit-origin-v1":RevisitReader.Value?"reflection-explicit-revisit-journal-v1":ExpressionReader.Value?"reflection-purpose-expression-journal-v1":"reflection-purpose-journal-v1");
        writer.WriteNumber("value", value); writer.WriteEndObject();
    }
}
