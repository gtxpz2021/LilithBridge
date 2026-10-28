namespace LilithBridge;
internal static class DialogueUiFocus
{
    // A sticky native keyboard-overlay flag alone must not send a draft while
    // the user pastes copied history into another foreground application.
    public static bool AllowsKeyboard(bool nativeForeground,bool unityFocus,bool overlay,bool uiOpen)
        => nativeForeground || unityFocus || (!uiOpen && overlay);
}
