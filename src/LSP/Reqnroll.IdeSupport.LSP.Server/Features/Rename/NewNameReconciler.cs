using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Reconciles a <c>textDocument/rename</c> request's <c>newName</c> against a binding's abstract
/// source expression when the two carry different parameter-slot counts. Extracted from
/// <see cref="RenameHandler.HandleRenameAsync"/> (issue #139) as a self-contained algorithm.
/// </summary>
internal sealed class NewNameReconciler
{
    private readonly IIdeSupportLogger _logger;

    public NewNameReconciler(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// A .feature-triggered rename can arrive in two shapes, both via the same
    /// <c>textDocument/rename</c> call, with no protocol-level way to tell them apart:
    /// <list type="bullet">
    /// <item><description>VS Code's native F2 seeds the dialog via prepareRename's whole-line
    /// range, so <paramref name="newName"/> comes back as concrete step text (real parameter
    /// values, e.g. "I have 10 cukes" rather than "I have {int} cukes"). Comparing that straight
    /// against the abstract expression always trips <c>ValidateNewName</c>'s parameter-count
    /// check, silently discarding every rename of a parameterized step.</description></item>
    /// <item><description>VS's custom "Rename Step" command builds its own prompt seeded with the
    /// binding's abstract expression (<c>RenameStepCommand.cs</c>), so <paramref name="newName"/>
    /// already carries the correct placeholder syntax and needs no reconciliation — attempting it
    /// anyway would fail to find any parameter "value" to locate in already-abstract text and
    /// wrongly reject a rename that never needed fixing up.</description></item>
    /// </list>
    /// Tries the abstract form first (matching parameter-slot count against the live source
    /// expression); only when that count differs does it attempt to derive the abstract
    /// expression by diffing the edited concrete text against the original.
    /// </summary>
    /// <returns>
    /// The effective new name to use — <paramref name="newName"/> unchanged when no reconciliation
    /// is needed or the original step text is unavailable to diff against, or the derived abstract
    /// expression when reconciliation succeeds. <see langword="null"/> when the edited text cannot
    /// be reconciled with the binding's parameter positions (the parameter VALUES, not just the
    /// wording, appear to have changed) — callers must reject the rename in that case.
    /// </returns>
    public string? Reconcile(
        string path,
        DocumentUri uri,
        Position position,
        IReadOnlyList<StepBindingMatch> usages,
        string sourceExpression,
        string newName,
        Func<DocumentUri, LspRange, string?> readStepText)
    {
        if (!path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) ||
            StepExpressionParameters.ExtractSlots(newName).Count == StepExpressionParameters.ExtractSlots(sourceExpression).Count)
        {
            return newName;
        }

        var currentUsage = usages.FirstOrDefault(u =>
            string.Equals(u.FeatureDocumentId, uri.ToString(), StringComparison.OrdinalIgnoreCase) &&
            u.Range != null &&
            position.Line >= u.Range.ToLspRange().Start.Line &&
            position.Line <= u.Range.ToLspRange().End.Line);

        var oldStepText = currentUsage?.Range != null
            ? readStepText(uri, currentUsage.Range.ToLspRange())
            : null;

        if (oldStepText == null)
        {
            // Can't read the pre-edit step text (buffer and disk both unavailable) — fall
            // back to treating newName as-is, same as before this reconciliation existed.
            _logger.LogVerbose("NewNameReconciler: could not read original step text for the edited position — using newName as-is");
            return newName;
        }

        var derived = FeatureStepTextBuilder.DeriveExpressionFromEditedText(sourceExpression, oldStepText, newName);
        if (derived == null)
        {
            _logger.LogVerbose("NewNameReconciler: could not reconcile edited step text with the binding's parameter positions — the parameter values, not just the wording, appear to have changed");
            return null;
        }

        _logger.LogVerbose($"NewNameReconciler: derived abstract expression '{derived}' from edited step text '{newName}'");
        return derived;
    }
}
