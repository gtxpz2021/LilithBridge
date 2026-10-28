using System;
using UnityEngine;

namespace LilithBridge;

internal enum SceneEventKind
{
    Touch,
    NativeHitArea,
    DoubleClick,
    LongPress,
    DragStart,
    DragEnd,
    RightClick,
    OptionShown,
    OptionSelected,
    DialogueStart,
    DialogueEnd,
    AppForeground,
    StateChanged,
    CharacterAction,
    UserInput,
    AssistantReply,
    Proactive,
    PaperIntent,
    NoteWritten,
    Unknown,
    // Only observed native body-state transitions use this category.
    NativeStateChanged
}

internal sealed class LilithSceneEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public SceneEventKind Kind { get; init; }
    public string Actor { get; init; } = "player";
    public string Area { get; init; } = "";
    public string Details { get; init; } = "";
    public Vector2 Position { get; init; }
    public float Duration { get; init; }
    public long Sequence { get; init; }

    public string Describe()
    {
        string area = Area.Length > 0 ? ";" + Area : "";
        string detail = Details.Length > 0 ? ";" + Details : "";
        return Kind + ": " + Actor + area + detail;
    }
}
