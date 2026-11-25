#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// One lens of any kind on a <c>.feature</c> line, as the shared
/// <see cref="FeatureCodeLensTaggerProvider"/> sees it: the line it belongs on, which data-point
/// provider renders it (<see cref="LineLensKinds"/>), and a content-derived key that changes
/// whenever that lens's content does.
/// </summary>
internal sealed record FeatureLineLensEntry(int Line, string Kind, string RevisionKey);
