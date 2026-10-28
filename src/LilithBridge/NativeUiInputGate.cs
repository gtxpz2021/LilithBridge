using UnityEngine;
namespace LilithBridge;
internal static class NativeUiInputGate
{
    private static int _claimedThroughFrame=-1;
    public static bool PointerClaimed => Time.frameCount<=_claimedThroughFrame;
    public static void ClaimPointer() => _claimedThroughFrame=Time.frameCount+1;
}
