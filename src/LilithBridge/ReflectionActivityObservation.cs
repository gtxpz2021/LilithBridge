using System;
using System.Globalization;

namespace LilithBridge;

// Activity facts for diagnostics and explicitly scoped callers. Native idle
// can miss mod dialogue; cancellation generations are not new submissions.
// No content, receipt, request, deadline or persistent field is kept here.
internal sealed class ReflectionActivityObservation
{
    private float _lastSubmissionAt = float.NegativeInfinity;
    private float _lastShownAt = float.NegativeInfinity;
    private long _submissionVersion;
    // Formal input and verified native choices, not cancellation or own display.
    internal long ExternalActivityVersion { get; private set; }

    internal void Submitted(float now)
    {
        _lastSubmissionAt = now;
        _submissionVersion++;
        ExternalActivityVersion++;
    }

    internal void Shown(float now) => _lastShownAt = now;

    internal void ExplicitNativeChoice() => ExternalActivityVersion++;

    // Missing observations remain unknown; callers retain the native-only
    // startup behavior. This does not change a deadline or create an opportunity.
    internal double? DialogueIdleSeconds(float now)
    {
        float latest = Math.Max(_lastSubmissionAt, _lastShownAt);
        return float.IsNegativeInfinity(latest) ? null : Math.Max(0d, (double)now - latest);
    }

    // Build policy scope, not a claim that this log event passed admission.
    // A start event reports the actual route separately as admission_idle_source.
    internal string Describe(float now, double nativeIdleSeconds, long conversationGeneration) =>
        ";activity_clock_policy=ordinary_old_material_admission"
        + ";native_idle_seconds=" + Format(nativeIdleSeconds)
        + ";mod_input_age_seconds=" + Age(now, _lastSubmissionAt)
        + ";mod_display_age_seconds=" + Age(now, _lastShownAt)
        + ";mod_submission_version=" + _submissionVersion.ToString(CultureInfo.InvariantCulture)
        + ";conversation_generation=" + conversationGeneration.ToString(CultureInfo.InvariantCulture);

    private static string Age(float now, float observedAt) => float.IsNegativeInfinity(observedAt)
        ? "unobserved" : Format(Math.Max(0d, (double)now - observedAt));

    private static string Format(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
