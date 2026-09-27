#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Checks, inside a running VS, that the command identifiers this extension hard-codes still name the
/// commands they are meant to. Each <c>{guid}:id</c> pair is resolved to its canonical name through the
/// documented <c>SVsCmdNameMapping</c> service and compared with the name we expect.
/// </summary>
/// <remarks>
/// This covers the values no build-time test can: undocumented IDs copied out of VS assemblies
/// (issue #747 was one of these, and so was the wrong property ID in #774), plus this extension's
/// own <c>.vsct</c> registration as VS actually loaded it. It never changes behaviour: every pair is
/// logged, so a VS update that moves a command shows up in the first log after the update instead of
/// as a feature that silently stopped working. Problems are logged at Info, not Warning: a warning
/// auto-activates the Reqnroll Output pane, and the expected Test Explorer names were recorded from
/// decompiled code and have not yet been confirmed live. Raise them to Warning once a live log shows
/// every entry resolving.
/// </remarks>
public static class VsWellKnownIdsSelfCheck
{
    /// <summary>A hard-coded command identifier and the canonical VS command name it must map to.</summary>
    internal sealed record ExpectedCommand(Guid Group, uint Id, string ExpectedName, string Owner);

    /// <summary>Outcome of resolving one <see cref="ExpectedCommand"/>.</summary>
    internal enum Outcome
    {
        /// <summary>The identifier resolved to the expected command.</summary>
        Match,

        /// <summary>The identifier resolved, but to a different command.</summary>
        NameMismatch,

        /// <summary>VS knows no command with this identifier.</summary>
        NotFound,
    }

    /// <summary>Every command identifier this extension hard-codes, with the name it must resolve to.</summary>
    internal static readonly IReadOnlyList<ExpectedCommand> ExpectedCommands = new[]
    {
        new ExpectedCommand(VsWellKnownIds.EditorCommandSet, VsWellKnownIds.CmdIdToggleLineComment,
            VsWellKnownIds.ToggleLineCommentCommandName, "CommentToggleCommandFilter"),
        new ExpectedCommand(TestExplorerCommandIds.CommandSet, TestExplorerCommandIds.RunCommandId,
            "TestExplorer.RunTestsFromCodeLens", nameof(TestExplorerCommandIds)),
        new ExpectedCommand(TestExplorerCommandIds.CommandSet, TestExplorerCommandIds.DebugCommandId,
            "TestExplorer.DebugTestsFromCodeLens", nameof(TestExplorerCommandIds)),
        new ExpectedCommand(TestExplorerCommandIds.CommandSet, TestExplorerCommandIds.SyncCommandId,
            "TestExplorer.SyncTestFromCodeLens", nameof(TestExplorerCommandIds)),
        new ExpectedCommand(HookCodeLensCommandIds.CommandSet, (uint)HookCodeLensCommandIds.NavigateToHookCommandId,
            "Reqnroll.NavigateToHook", "HookCodeLensCommands.vsct"),
    };

    /// <summary>
    /// Resolves every <see cref="ExpectedCommands"/> entry and logs the result. Must be called on the
    /// UI thread. Never throws.
    /// </summary>
    public static void Run(IServiceProvider serviceProvider, IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (serviceProvider.GetService(typeof(SVsCmdNameMapping)) is not IVsCmdNameMapping mapping)
            {
                logger.LogInfo("VsWellKnownIdsSelfCheck: SVsCmdNameMapping is unavailable; command IDs not checked.");
                return;
            }

            var problems = 0;
            foreach (var expected in ExpectedCommands)
            {
                var group = expected.Group;
                var hr = mapping.MapGUIDIDToName(ref group, expected.Id, VSCMDNAMEOPTS.CNO_GETENU, out var actualName);
                var outcome = Evaluate(expected.ExpectedName, hr, actualName);
                if (outcome == Outcome.Match)
                {
                    logger.LogVerbose(
                        $"VsWellKnownIdsSelfCheck: {{{expected.Group}}}:{expected.Id} = {actualName} (OK, used by {expected.Owner}).");
                    continue;
                }

                problems++;
                logger.LogInfo(outcome == Outcome.NotFound
                    ? $"VsWellKnownIdsSelfCheck: {{{expected.Group}}}:{expected.Id} is not a command in this VS (hr=0x{hr:X8}); expected {expected.ExpectedName}, used by {expected.Owner}. The hard-coded ID is probably wrong for this VS version."
                    : $"VsWellKnownIdsSelfCheck: {{{expected.Group}}}:{expected.Id} resolves to '{actualName}', expected {expected.ExpectedName}, used by {expected.Owner}.");
            }

            var summary = $"VsWellKnownIdsSelfCheck: checked {ExpectedCommands.Count} command IDs, {problems} problem(s).";
            if (problems == 0)
                logger.LogVerbose(summary);
            else
                logger.LogInfo(summary);
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "VsWellKnownIdsSelfCheck: failed.");
        }
    }

    /// <summary>
    /// Decision rule for one <c>IVsCmdNameMapping.MapGUIDIDToName</c> result, split out so it can be
    /// tested without a running VS. Names compare case-insensitively, ignoring a leading dot: the
    /// Test Explorer names were recorded in that form (<c>.TestExplorer.RunTestsFromCodeLens</c>) and
    /// it is not yet known which form <c>MapGUIDIDToName</c> returns.
    /// </summary>
    internal static Outcome Evaluate(string expectedName, int hr, string? actualName)
    {
        // MapGUIDIDToName returns S_FALSE, not an error, when no command has this identifier.
        if (hr != VSConstants.S_OK || string.IsNullOrWhiteSpace(actualName))
            return Outcome.NotFound;

        return string.Equals(Normalize(actualName!), Normalize(expectedName), StringComparison.OrdinalIgnoreCase)
            ? Outcome.Match
            : Outcome.NameMismatch;
    }

    private static string Normalize(string name) => name.Trim().TrimStart('.');
}
