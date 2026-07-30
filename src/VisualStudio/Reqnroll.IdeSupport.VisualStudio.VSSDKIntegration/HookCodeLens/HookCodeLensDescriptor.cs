#nullable enable

using System;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Describes one hook-match-count lens location for the classic CodeLens API (issue #372).
/// Unlike the built-in per-language providers, this repo has no code-element/symbol model behind
/// a <c>.feature</c> line — the tagger supplies the span itself from the server's lens data.
/// </summary>
internal sealed class HookCodeLensDescriptor : ICodeLensDescriptor
{
    public HookCodeLensDescriptor(string filePath, Span applicableSpan, string elementDescription)
    {
        FilePath           = filePath;
        ApplicableSpan      = applicableSpan;
        ElementDescription = elementDescription;
    }

    public string FilePath { get; }

    /// <summary>Always <see cref="Guid.Empty"/> — hook-match lenses aren't tied to a specific project's compiled output.</summary>
    public Guid ProjectGuid => Guid.Empty;

    public string ElementDescription { get; }

    public Span? ApplicableSpan { get; }

    /// <summary>
    /// No <see cref="CodeElementKinds"/> value fits a Gherkin Feature/Scenario line; <c>Unspecified</c>
    /// is the documented value to use when there's no applicable kind.
    /// </summary>
    public CodeElementKinds Kind => CodeElementKinds.Unspecified;
}
