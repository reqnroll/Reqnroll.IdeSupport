using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Concurrency;

/// <summary>
/// Limits how often a server-to-client output for one document is produced while edits arrive faster than the
/// client can take the results in, without ever delaying the output after a pause in typing.
/// </summary>
/// <remarks>
/// <para>
/// Visual Studio handles every <c>publishDiagnostics</c> and token push on its UI thread. In a very large feature
/// file, producing both after every keystroke keeps that thread busy enough that typing stutters, even though each
/// individual message is small. A leading-edge throttle bounds the rate: the first request after a quiet period runs
/// at once (so a single edit costs no extra latency), and requests that follow within the interval are collapsed into
/// one run at the end of it, using the most recent request. The last change is therefore always delivered.
/// </para>
/// <para>
/// This differs from <see cref="IRefreshDebouncer"/>, which waits for edits to stop before running anything (a
/// trailing debounce): that delays results for as long as someone keeps typing, which is the wrong trade for output
/// the user is looking at while they type.
/// </para>
/// <para>
/// MediatR notification handlers are transient, so this state lives in a singleton service (see the remarks on
/// <see cref="IRefreshDebouncer"/>).
/// </para>
/// </remarks>
public interface IOutputThrottle
{
    /// <summary>
    /// Runs <paramref name="action"/> for <paramref name="key"/> now if nothing has run for that key during the last
    /// <paramref name="interval"/>; otherwise remembers it to run once the interval since the last run has elapsed.
    /// A newer call with the same key replaces a remembered action, so at most one run follows a burst of calls.
    /// Runs for one key never overlap.
    /// </summary>
    /// <returns>
    /// A task that completes when <paramref name="action"/> has finished if it ran immediately (so the caller observes
    /// its failure), or at once if it was deferred (a deferred action's failure is logged instead).
    /// </returns>
    Task RunThrottledAsync(string key, TimeSpan interval, Func<Task> action);

    /// <summary>
    /// Drops any remembered action for <paramref name="key"/> and forgets when it last ran, so the next call runs
    /// immediately. An action that is already running is not interrupted, so a call made right after this one can
    /// overlap it (the "never overlap" guarantee holds only between cancellations).
    /// </summary>
    void Cancel(string key);
}

/// <summary>The interval shared by the outputs that are throttled while a document is being edited.</summary>
public static class OutputThrottleDefaults
{
    /// <summary>At most one run per document and output per this interval while edits keep arriving.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
}

/// <summary>The throttle keys of the outputs that are throttled per document.</summary>
public static class OutputThrottleKeys
{
    /// <summary>Key for the <c>textDocument/publishDiagnostics</c> output of <paramref name="uri"/>.</summary>
    public static string Diagnostics(DocumentUri uri) => $"diagnostics:{uri}";

    /// <summary>Key for the <c>reqnroll/semanticTokens</c> push of <paramref name="uri"/>.</summary>
    public static string SemanticTokens(DocumentUri uri) => $"semanticTokens:{uri}";
}

/// <summary>An <see cref="IOutputThrottle"/> that runs every action at once; used where no throttling is wanted.</summary>
public sealed class NullOutputThrottle : IOutputThrottle
{
    /// <summary>The shared instance.</summary>
    public static NullOutputThrottle Instance { get; } = new();

    /// <inheritdoc/>
    public Task RunThrottledAsync(string key, TimeSpan interval, Func<Task> action) => action();

    /// <inheritdoc/>
    public void Cancel(string key)
    {
    }
}
