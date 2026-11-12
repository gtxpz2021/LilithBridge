using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace LilithBridge;

// The native ambient API acquired an optional mixDuration in 1.2.1. Keep its
// own default and the two original null callbacks; do not substitute PlayAction.
internal static class NativeIdleAnimation
{
    internal sealed record Contract(bool Supported, string Shape, float? MixDuration, string Reason);
    private sealed record Binding(MethodInfo? Method, Contract Contract);
    private static readonly Lazy<Binding> Current = new(() => Resolve(typeof(CharacterController)));
    private static bool _reportedUnavailable;

    // Metadata only: no controller construction, native call or class-field read.
    internal static Contract Describe(Type controllerType) => Resolve(controllerType).Contract;

    private static Binding Resolve(Type controllerType)
    {
        try
        {
            bool Prefix(MethodInfo method)
            {
                if (method.Name != "PlayIdleAnimation" || method.IsStatic || method.IsGenericMethod
                    || method.ReturnType != typeof(void)) return false;
                var parameters = method.GetParameters();
                return parameters.Length is 3 or 4
                    && parameters[0].ParameterType == typeof(LilithActionType)
                    && parameters[1].ParameterType == typeof(Il2CppSystem.Action)
                    && parameters[2].ParameterType == typeof(Il2CppSystem.Func<bool>)
                    && parameters.Skip(1).Take(2).All(p => p.IsOptional && p.HasDefaultValue && p.DefaultValue == null);
            }
            var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(Prefix).ToArray();
            var legacy = methods.Where(m => m.GetParameters().Length == 3).ToArray();
            // If both API generations are present, preserve the original exact
            // call rather than choosing an overload by reflection order.
            if (legacy.Length == 1) return new(legacy[0], new(true, "action/null/null", null, "native_legacy_defaults"));
            if (legacy.Length > 1) return Unsupported("ambiguous_legacy_contract");
            var mixed = methods.Where(m =>
            {
                var p = m.GetParameters();
                return p.Length == 4 && p[3].ParameterType == typeof(float)
                    && p[3].Name == "mixDuration" && p[3].IsOptional && p[3].HasDefaultValue
                    && p[3].DefaultValue is float;
            }).ToArray();
            if (mixed.Length == 1)
            {
                float nativeDefault = (float)mixed[0].GetParameters()[3].DefaultValue!;
                return new(mixed[0], new(true, "action/null/null/mixDuration", nativeDefault, "native_optional_mix_default"));
            }
            return Unsupported(mixed.Length > 1 ? "ambiguous_mix_contract" : "unsupported_idle_contract");
        }
        catch (Exception error)
        {
            return Unsupported("idle_metadata_unavailable:" + error.GetType().Name);
        }
    }

    private static Binding Unsupported(string reason) => new(null, new(false, "unavailable", null, reason));

    internal static bool TryPlay(CharacterController controller, LilithActionType action)
    {
        if (controller is null) return false;
        var binding = Current.Value;
        if (binding.Method == null)
        {
            if (!_reportedUnavailable)
            {
                _reportedUnavailable = true;
                NativeEventLog.Trace("action.waiting_api_unavailable", "mod", "system",
                    "Native ambient waiting API unavailable", binding.Contract.Reason);
            }
            return false;
        }
        object?[] arguments = binding.Contract.MixDuration is float mix
            ? new object?[] { action, null, null, mix }
            : new object?[] { action, null, null };
        try { binding.Method.Invoke(controller, arguments); return true; }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            // Preserve the existing waiting owner's native-error handling.
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
