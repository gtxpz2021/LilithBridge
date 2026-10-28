using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal enum ActionAvailability
{
    Direct,
    Transition,
    Unavailable
}

internal sealed record RuntimeActionCapability(
    LilithActionType Action,
    ActionAvailability Availability,
    string? NativeAnimation,
    string? RequiredTransition,
    string? Reason);

internal sealed record RuntimeActionCapabilityDiagnostics(
    int CatalogCount,
    int NativeResolvableCount,
    int DirectCount,
    int TransitionCount,
    int UnavailableCount);

/// <summary>
/// Minimal semantic Action → contextual capability resolver.
/// It does not decide desire, emotion, probability, ranking or execution.
/// It only answers whether a semantic action exists for the current
/// Pose × Clothing context and what execution state it has.
/// </summary>
internal static class RuntimeActionCapabilityResolver
{
    public static RuntimeActionCapability Resolve(
        LilithActionType action,
        string pose,
        string clothing)
    {
        string poseKey = NormalizePose(pose);
        string clothingKey = NormalizeClothing(clothing);
        string actionName = action.ToString();

        if (!NativeAnimationCatalog.TryResolve(actionName, poseKey, clothingKey, out string? native))
            return new RuntimeActionCapability(
                action,
                ActionAvailability.Unavailable,
                null,
                null,
                "native_animation_missing");

        bool transition = ActionCapability.IsPoseTransition(action);
        string requiredTransition = transition
            ? ResolveRequiredTransition(action)
            : null!;
        return new RuntimeActionCapability(
            action,
            transition ? ActionAvailability.Transition : ActionAvailability.Direct,
            native,
            transition ? requiredTransition : null,
            null);
    }

    public static RuntimeActionCapabilityDiagnostics BuildDiagnostics(
        string pose,
        string clothing)
    {
        var actions = Enum.GetValues<LilithActionType>();
        int direct = 0;
        int transition = 0;
        int unavailable = 0;
        foreach (LilithActionType action in actions)
        {
            RuntimeActionCapability capability = Resolve(action, pose, clothing);
            switch (capability.Availability)
            {
                case ActionAvailability.Direct:
                    direct++;
                    break;
                case ActionAvailability.Transition:
                    transition++;
                    break;
                default:
                    unavailable++;
                    break;
            }
        }

        return new RuntimeActionCapabilityDiagnostics(
            actions.Length,
            direct + transition,
            direct,
            transition,
            unavailable);
    }

    public static IReadOnlyList<string> GetDirectActionNames(
        string pose,
        string clothing)
    {
        var names = new List<string>();
        foreach (LilithActionType action in Enum.GetValues<LilithActionType>())
        {
            if (!ActionCapability.IsExposed(action))
                continue;
            if (action is LilithActionType.None or LilithActionType.Idle)
                continue;
            if (Resolve(action, pose, clothing).Availability == ActionAvailability.Direct)
                names.Add(ToActionName(action));
        }
        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string BuildDirectPromptList(
        string pose,
        string clothing)
    {
        IReadOnlyList<string> names = GetDirectActionNames(pose, clothing);
        return names.Count == 0
            ? "idle"
            : string.Join(", ", names);
    }

    private static string Normalize(string value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private static string NormalizePose(string value)
    {
        return Normalize(value) switch
        {
            "stand" or "standing" or "idle_or_standing" or "idle" => "stand",
            "lie" or "lying" or "lying_down" => "lie",
            "sleep" or "sleeping" => "sleep",
            "sit" or "sitting" => "sit",
            "sofa" or "sofa_sit" => "sofa",
            _ => Normalize(value)
        };
    }

    private static string ToActionName(LilithActionType action)
    {
        string value = action.ToString();
        var parts = new List<string>();
        int start = 0;
        for (int i = 1; i < value.Length; i++)
        {
            if (!char.IsUpper(value[i]))
                continue;
            parts.Add(value[start..i].ToLowerInvariant());
            start = i;
        }
        parts.Add(value[start..].ToLowerInvariant());
        return string.Join("_", parts);
    }

    private static string NormalizeClothing(string value)
    {
        string normalized = Normalize(value);
        return normalized is "pajama" or "pajamas" or "casual"
            ? "casual"
            : normalized;
    }

    private static string ResolveRequiredTransition(LilithActionType action)
    {
        return action switch
        {
            LilithActionType.LieStandUp => "lie_stand_up",
            LilithActionType.SofaStandUp => "sofa_stand_up",
            LilithActionType.SofaDoubt => "sofa_doubt",
            LilithActionType.PillowLie => "pillow_lie",
            LilithActionType.StandUp => "stand_up",
            _ => "stand_up"
        };
    }
}
