#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;

/// <summary>
/// A point-in-time reading of the shell and solution state that distinguishes "the IDE is really
/// shutting down" from "the IDE is alive and merely swapping solutions" (issue #555).
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="VsShellStateProbe"/> — which does the UI-thread COM reads — so the
/// interpretation and formatting rules are unit-testable without a live shell, the same split
/// <c>RdtDocumentInitialization</c> uses against <c>DocumentInitializationMonitor</c>.
/// </para>
/// <para>
/// Every field is nullable because each property read is independently best-effort: a probe that
/// cannot reach the shell at all still produces a snapshot, it just reports <c>unknown</c> rather
/// than inventing a value. That matters here specifically — "we could not read the shell state"
/// and "the shell said it is not shutting down" lead to opposite conclusions.
/// </para>
/// </remarks>
internal sealed record VsShellStateSnapshot(
    bool? ShellInitialized,
    bool? ShellShutdownStarted,
    bool? SolutionOpen,
    bool? SolutionClosing,
    string? SolutionFileName,
    string? ProbeError)
{
    /// <summary>A snapshot for the case where the probe itself could not run at all.</summary>
    public static VsShellStateSnapshot Failed(string error) =>
        new(null, null, null, null, null, error);

    /// <summary>
    /// True when the shell explicitly reported that it is <em>not</em> shutting down. This is the
    /// finding that matters for issue #555: if <c>VsShellUtilities.ShutdownToken</c> has fired
    /// while this is true, the token is not reporting an IDE shutdown and the extension is
    /// disposing its LSP server for the rest of a still-live session.
    /// </summary>
    /// <remarks>
    /// Deliberately not simply <c>!ShellShutdownStarted</c>: an unreadable property is
    /// <see langword="false"/> here, never "contradicted", so a failed probe can't be mistaken for
    /// evidence.
    /// </remarks>
    public bool ContradictsShutdown => ShellShutdownStarted == false;

    /// <summary>Renders the snapshot as a single log-line fragment.</summary>
    public string Describe()
    {
        if (ProbeError is not null)
            return $"shell state unavailable ({ProbeError})";

        return $"shellInitialized={Format(ShellInitialized)}, " +
               $"shellShutdownStarted={Format(ShellShutdownStarted)}, " +
               $"solutionOpen={Format(SolutionOpen)}, " +
               $"solutionClosing={Format(SolutionClosing)}, " +
               $"solutionFile={FormatPath(SolutionFileName)}";
    }

    private static string Format(bool? value) => value?.ToString() ?? "unknown";

    private static string FormatPath(string? path) =>
        string.IsNullOrEmpty(path) ? "(none)" : path!;
}
