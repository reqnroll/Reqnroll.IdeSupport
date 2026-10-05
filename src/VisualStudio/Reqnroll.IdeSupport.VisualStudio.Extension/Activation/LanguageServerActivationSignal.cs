#nullable enable

using System;
using System.Threading;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Activation;

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
