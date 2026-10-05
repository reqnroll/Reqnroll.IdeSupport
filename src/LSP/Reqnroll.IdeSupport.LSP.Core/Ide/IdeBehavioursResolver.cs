using System;

namespace Reqnroll.IdeSupport.LSP.Core.Ide;

/// <summary>
/// The single place that decides which <see cref="IdeBehaviours"/> apply to a client, from its
/// <c>--ide</c> identifier (or the identifier resolved from <c>ClientInfo</c>) and its reported
/// version. A pure function so every rule is unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// <b>To change a behaviour for a client or version</b>, edit the rule here and nowhere else. Version
/// rules go in the matching <c>case</c> and read <paramref name="clientVersion"/>; it is the
/// free-form <c>InitializeParams.ClientInfo.Version</c> string and is <see langword="null"/> when
/// the client reported none, so a version rule must treat "unknown" explicitly (the current rules
/// deliberately do not narrow by version).
/// </para>
/// <para>
/// An unrecognized or absent identity gets <see cref="IdeBehaviours.None"/>: the standard-LSP
/// baseline. A new client therefore opts in to workarounds explicitly.
/// </para>
/// <para>
/// <b>codeLens/resolve is opt-in and no shipped client is opted in (issue #471).</b> The server
/// CAN defer the per-lens count to codeLens/resolve (see
/// <c>StepCodeLensHandler.ResolveAsync</c>/<c>HookMatchCountCodeLensHandler.ResolveAsync</c>/
/// <c>CodeLensResolveHandler</c>) and declares <c>codeLensProvider.resolveProvider = true</c>, but
/// deferral is only safe when the CLIENT performs the resolve round trip, and none we ship does:
/// </para>
/// <list type="bullet">
/// <item>VS Code — <c>src/VSCode/src/commands/stepCodeLens.ts</c> registers a hand-rolled
/// <c>CodeLensProvider</c> that does not implement <c>resolveCodeLens</c> and discards
/// <c>lens.data</c>, so codeLens/resolve is never sent and a placeholder lens never renders.</item>
/// <item>Rider — <c>StepUsagesCodeVisionProvider.kt</c> filters out any lens whose command is null,
/// silently dropping every deferred lens.</item>
/// <item>Visual Studio — resolve support unconfirmed; never exercised.</item>
/// </list>
/// <para>
/// Confirmed live in VS Code: <c>.cs</c> step-usage lenses vanished entirely and <c>.feature</c>
/// hook-match lenses degraded. Hence an explicit opt-in, not an inverted "everyone but X" check.
/// TO ADD A CLIENT: first make that client implement the resolve round trip (VS Code: implement
/// <c>resolveCodeLens</c> and thread the server's <c>data</c> onto the <c>vscode.CodeLens</c>;
/// Rider: render command-less lenses as a placeholder CodeVision entry and issue codeLens/resolve
/// to fill them in), verify it live against a large solution, THEN set
/// <see cref="IdeBehaviours.SupportsCodeLensResolve"/> for it below.
/// </para>
/// </remarks>
public static class IdeBehavioursResolver
{
    /// <summary>Resolves the behaviours for the given <c>--ide</c> identifier and client version.</summary>
    public static IdeBehaviours Resolve(string? ide, string? clientVersion)
    {
        _ = clientVersion; // no rule narrows by version yet; see the class remarks.

        if (Is(ide, "visualstudio"))
        {
            return new IdeBehaviours
            {
                RequiresPushedSemanticTokens = true,
                UsesCustomCodeLensRefresh = true,
                RejectsEmptyTriggerCompletion = true,
                AppliesRenameResponseEditNatively = true,
                NavigatesStepsViaFindStepDefinitions = true,
            };
        }

        if (Is(ide, "vscode"))
        {
            return new IdeBehaviours
            {
                RunsVscodeOpenCommandLocally = true,
                HonorsShowDocumentRequests = true,
            };
        }

        return IdeBehaviours.None;
    }

    private static bool Is(string? ide, string expected) =>
        string.Equals(ide, expected, StringComparison.OrdinalIgnoreCase);
}
