using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <summary>
/// Combines parse-error tags and binding-mismatch data into a single, ordered list of
/// <see cref="GherkinDiagnostic"/> items ready for conversion to LSP <c>Diagnostic</c> objects.
/// </summary>
/// <remarks>
/// The LSP specification requires that a single <c>textDocument/publishDiagnostics</c> message
/// delivers the <em>complete</em> diagnostic set for a URI; a partial message clears any
/// diagnostics not included in it. This aggregator is therefore the single point of truth
/// for what gets pushed — callers must not supplement or filter its output.
/// </remarks>
public interface IDiagnosticsAggregator
{
    /// <summary>
    /// Produces the combined set of diagnostics for a feature document.
    /// </summary>
    /// <param name="tags">
    /// The full flat tag collection for the document as stored in <c>IDocumentBufferService</c>.
    /// <see cref="IdeSupportTagTypes.ParserError"/> tags are the source for parser-error diagnostics.
    /// </param>
    /// <param name="matchSet">
    /// The binding match set for the document as stored in <c>IBindingMatchService</c>.
    /// <see cref="FeatureBindingMatchSet.Undefined"/> steps are the source for undefined-step/binding diagnostics.
    /// </param>
    IReadOnlyList<GherkinDiagnostic> Aggregate(
        IReadOnlyCollection<IdeSupportTag> tags,
        FeatureBindingMatchSet matchSet);
}
