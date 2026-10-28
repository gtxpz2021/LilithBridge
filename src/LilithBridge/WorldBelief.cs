using System;

namespace LilithBridge;

/// <summary>
/// A small, honest world-model layer. It records what the program actually
/// observed, with source and certainty, so the model can reason about the
/// desktop scene without treating inferred details as memory.
/// </summary>
internal sealed class WorldBelief
{
    public string Scene { get; set; } = "";
    public string Window { get; set; } = "";
    public string Interaction { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.MinValue;

    public void ObserveForeground(ForegroundAppInfo? app)
    {
        if (app == null)
            return;
        Window = app.DescribeRedacted();
        Scene = app.Category;
        Source = "foreground_window";
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ObserveNative(string nativeState)
    {
        if (string.IsNullOrWhiteSpace(nativeState))
            return;
        Scene = "lilith_desktop";
        Interaction = nativeState;
        Source = "native_state";
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public string Describe()
    {
        if (UpdatedAt == DateTimeOffset.MinValue)
            return "";
        string window = Window.Length > 0 ? "；" + Window : "";
        string interaction = Interaction.Length > 0 ? "；" + Interaction : "";
        return "场景=" + Scene
            + "；来源=" + Source
            + "；更新时间=" + UpdatedAt.LocalDateTime.ToString("HH:mm")
            + window
            + interaction;
    }
}
