using System;
using System.Reflection;

namespace LilithBridge;

/// <summary>
/// Read-only diagnostics for the original snack-time state. This never
/// mutates save data; it only asks the game what it currently knows.
/// </summary>
internal static class NativeSnackStatus
{
    public static string Build()
    {
        try
        {
            DialogueManager? manager = DialogueManager.instance;
            bool served = manager?.HasServedSnackTimeToday() ?? false;
            string day = SystemSensor.LocalTime.ToString("yyyy-MM-dd");

            PlayerLineController? controller =
                UnityEngine.Object.FindObjectOfType<PlayerLineController>(true);
            bool rootAvailable = TryInvokeBool(controller, "IsSnackTimeAvailable", null);

            return $"served_today={served};day={day};root_available={rootAvailable};"
                + "topic_scan=read_only";
        }
        catch (Exception error)
        {
            return "error=" + error.Message;
        }
    }

    private static bool TryInvokeBool(
        PlayerLineController? controller,
        string methodName,
        object?[]? arguments)
    {
        if (controller == null)
            return false;
        try
        {
            MethodInfo? method = controller.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null)
                return false;
            object? result = method.Invoke(controller, arguments);
            return result is bool value && value;
        }
        catch
        {
            return false;
        }
    }

}
