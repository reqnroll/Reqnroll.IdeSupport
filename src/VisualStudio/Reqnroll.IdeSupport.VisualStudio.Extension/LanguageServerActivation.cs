#nullable enable

using System;
using System.Threading;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Records whether VS has activated the <see cref="ReqnrollLanguageClient"/> provider in this
/// session, so the VSSDK side (<see cref="ReqnrollPluginPackage"/>) can tell when activation was
/// missed (issue #533).
/// </summary>
/// <remarks>
/// <para>
/// Stored in the AppDomain's data, not in a static field. The package and the provider come from
/// different composition roots (VSSDK and VisualStudio.Extensibility) and share no DI container.
/// The package's assembly is also loaded differently: by identity through the pkgdef and
/// <c>ProvideBindingPath</c>, while VisualStudio.Extensibility loads it by path. On .NET Framework
/// that can produce two copies of the assembly, each with its own statics. A static flag would
/// then never reach the package, and the trigger would fire on every start. A boxed
/// <see cref="bool"/> under a string key is visible to both copies. Waiting polls for it, because
/// no wait primitive is shared between the copies.
/// </para>
/// <para>
/// This records activation, not a working connection: the provider's constructor runs when VS
/// activates it, before <c>CreateServerConnectionAsync</c>. That is the signal needed here, because
/// the failure being detected is VS never constructing the provider at all.
/// </para>
/// </remarks>
internal sealed class LanguageServerActivationSignal
{
    /// <summary>How often <see cref="WaitAsync"/> checks for activation.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly string _key;

    /// <summary>The instance shared by the package and the provider.</summary>
    public static LanguageServerActivationSignal Shared { get; } =
        new("Reqnroll.IdeSupport.VisualStudio.LanguageServerProviderActivated");

    /// <summary>Creates a signal stored under <paramref name="key"/> in the AppDomain's data.</summary>
    internal LanguageServerActivationSignal(string key) => _key = key;

    /// <summary>True once VS has activated the provider in this session.</summary>
    public bool IsActivated => AppDomain.CurrentDomain.GetData(_key) is true;

    /// <summary>Records that VS has activated the provider. Idempotent.</summary>
    public void MarkActivated() => AppDomain.CurrentDomain.SetData(_key, true);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for activation, checking every
    /// <see cref="PollInterval"/>. Returns whether it happened.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!IsActivated)
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>What <see cref="ScratchFileActivationTrigger"/> decides to do.</summary>
internal enum ActivationTriggerDecision
{
    /// <summary>Open the scratch file: a <c>.feature</c> document is open and the provider was never activated.</summary>
    Trigger,

    /// <summary>Do nothing: the user turned the trigger off.</summary>
    SkipDisabled,

    /// <summary>Do nothing: VS already activated the provider.</summary>
    SkipAlreadyActivated,

    /// <summary>Do nothing: no <c>.feature</c> document is open, so nothing is missing any features.</summary>
    SkipNoFeatureDocument,
}

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
