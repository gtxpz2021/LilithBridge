namespace LilithBridge;

/// <summary>
/// Field names for the game's own clock.
///
/// Two clocks exist and must never share a bare name: the player's real clock
/// ([now], and the real_phase derived from it) and the game world's own clock
/// (these fields). They can legitimately disagree, so neither is derived from
/// the other and both the runtime inspector and every offline fixture use the
/// names defined here.
/// </summary>
internal static class NativeClockFields
{
    public const string Hour = "game_hour";
    public const string Night = "game_night";

    public static string Describe(int hour, bool night) =>
        ";" + Hour + "=" + hour + ";" + Night + "=" + (night ? "true" : "false");
}
