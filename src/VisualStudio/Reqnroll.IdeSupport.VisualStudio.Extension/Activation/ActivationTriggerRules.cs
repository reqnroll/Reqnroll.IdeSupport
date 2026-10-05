#nullable enable

using System;
using System.Threading;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Activation;

/// <summary>
/// Decides whether <see cref="ScratchFileActivationTrigger"/> should run. Split out so the rules
/// can be tested without a running VS.
/// </summary>
internal static class ActivationTriggerRules
{
    /// <summary>
    /// Turns the trigger off when set to anything other than absent, empty, <c>0</c> or
    /// <c>false</c> (case-insensitive).
    /// </summary>
    internal const string DisableEnvironmentVariable = "REQNROLL_IDE_DISABLE_ACTIVATION_TRIGGER";

    /// <summary>True when <see cref="DisableEnvironmentVariable"/> turns the trigger off.</summary>
    internal static bool IsDisabled() =>
        IsDisabledValue(Environment.GetEnvironmentVariable(DisableEnvironmentVariable));

    /// <summary>The rule behind <see cref="IsDisabled()"/>, applied to a given variable value.</summary>
    internal static bool IsDisabledValue(string? value) =>
        !string.IsNullOrEmpty(value) && value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decides what to do, checking the kill switch first and activation second.</summary>
    internal static ActivationTriggerDecision Decide(bool disabled, bool providerActivated, int openFeatureDocuments)
    {
        if (disabled)
            return ActivationTriggerDecision.SkipDisabled;

        if (providerActivated)
            return ActivationTriggerDecision.SkipAlreadyActivated;

        if (openFeatureDocuments <= 0)
            return ActivationTriggerDecision.SkipNoFeatureDocument;

        return ActivationTriggerDecision.Trigger;
    }
}
