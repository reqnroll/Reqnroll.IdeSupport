#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Telemetry;

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
/// as a feature that silently stopped working. Problems are logged as warnings (which also brings up
/// the Reqnroll Output pane). Every entry below was confirmed to resolve in a live VS 18.0 session
/// (PR #777), so a warning means VS changed, not that an expected name is a guess.
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
        // VS puts the command's top-level menu in front of its .vsct <CanonicalName>
        // ("Reqnroll.NavigateToHook"): the command's group is parented to IDM_VS_MENU_TOOLS.
        new ExpectedCommand(HookCodeLensCommandIds.CommandSet, (uint)HookCodeLensCommandIds.NavigateToHookCommandId,
            "Tools.Reqnroll.NavigateToHook", "HookCodeLensCommands.vsct"),
    };

    /// <summary>
    /// Resolves every <see cref="ExpectedCommands"/> entry and logs the result. Must be called on the
    /// UI thread. Never throws.
    /// </summary>
    public static void Run(IServiceProvider serviceProvider, IIdeSupportLogger logger, ITelemetryTransmitter? telemetryTransmitter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (serviceProvider.GetService(typeof(SVsCmdNameMapping)) is not IVsCmdNameMapping mapping)
            {
                logger.LogInfo("VsWellKnownIdsSelfCheck: SVsCmdNameMapping is unavailable; command IDs not checked.");
                return;
            }

            var problems = new List<string>();
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

                problems.Add($"{expected.Owner}: {expected.ExpectedName} ({outcome})");
                logger.LogWarning(outcome == Outcome.NotFound
                    ? $"VsWellKnownIdsSelfCheck: {{{expected.Group}}}:{expected.Id} is not a command in this VS (hr=0x{hr:X8}); expected {expected.ExpectedName}, used by {expected.Owner}. The hard-coded ID is probably wrong for this VS version."
                    : $"VsWellKnownIdsSelfCheck: {{{expected.Group}}}:{expected.Id} resolves to '{actualName}', expected {expected.ExpectedName}, used by {expected.Owner}.");
            }

            var summary = $"VsWellKnownIdsSelfCheck: checked {ExpectedCommands.Count} command IDs, {problems.Count} problem(s).";
            if (problems.Count == 0)
                logger.LogVerbose(summary);
            else
            {
                logger.LogWarning(summary);
                telemetryTransmitter?.TransmitEvent(CreateMismatchEvent(ExpectedCommands.Count, problems));
            }
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "VsWellKnownIdsSelfCheck: failed.");
        }
    }

    /// <summary>Longest <c>Problems</c> value sent, so an all-commands failure can't produce an oversized property.</summary>
    internal const int MaxProblemsLength = 512;

    /// <summary>
    /// Builds the telemetry alert for a failed check, split out so it can be tested without a running VS.
    /// <c>Problems</c> names each failing command (owner, expected name, outcome) so drift can be
    /// diagnosed from the alert alone.
    /// </summary>
    internal static VsGenericEvent CreateMismatchEvent(int expectedCount, IReadOnlyList<string> problems)
    {
        var joined = string.Join("; ", problems);
        if (joined.Length > MaxProblemsLength)
            joined = joined.Substring(0, MaxProblemsLength);

        return new VsGenericEvent("VsWellKnownIdsSelfCheckMismatch", new Dictionary<string, object>
        {
            ["ProblemCount"] = problems.Count,
            ["ExpectedCount"] = expectedCount,
            ["Problems"] = joined,
        });
    }

    /// <summary>
    /// Decision rule for one <c>IVsCmdNameMapping.MapGUIDIDToName</c> result, split out so it can be
    /// tested without a running VS. Names compare case-insensitively, ignoring a leading dot: the
    /// Test Explorer names were recorded from decompiled code in that form
    /// (<c>.TestExplorer.RunTestsFromCodeLens</c>). VS 18.0 returns them without the dot.
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
